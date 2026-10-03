using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;
using Bcode.App.Models;
using Bcode.App.Services;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Controls;

public class SqlProfilerControl : UserControl
{
    // =========================================================================
    // API WIN32 ĐỂ AUTO-TYPE VÀO CỬA SỔ PROFILER
    // =========================================================================
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    // Dò control con của 1 dialog Win32 NGOÀI process (Connect to Server) để focus/điền ĐÚNG Ô
    // theo VỊ TRÍ THẬT của control, thay vì đoán số lần Tab từ 1 focus mặc định — vì focus mặc
    // định của khung "Connect to Server" đổi tuỳ theo Profiler đã từng nhớ kết nối hay chưa (lần
    // đầu focus có thể khác lần sau), khiến cách đếm Tab cũ bị sai ô.
    private delegate bool EnumChildProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumChildProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    // WM_GETTEXT / CB_GETLBTEXT là message hệ thống nên Windows tự chép buffer qua ranh giới
    // process — đọc được chữ của control nằm trong Profiler.exe. GetWindowText thì KHÔNG: với
    // control thuộc process khác nó trả chuỗi rỗng (combo/edit tự giữ text của mình), nên trước
    // đây ô "Use the template" luôn đọc ra "" và code coi như Template không khớp.
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, StringBuilder lParam);

    private const int WM_GETTEXT = 0x000D;
    private const int CB_GETCURSEL = 0x0147;
    private const int CB_GETLBTEXT = 0x0148;
    private const int CB_GETLBTEXTLEN = 0x0149;

    // ---- Điền ô của dialog Profiler bằng WM_SETTEXT (thay cho Clipboard + Ctrl+V + đếm Tab) ----
    // WM_SETTEXT/WM_GETTEXT là message hệ thống nên Windows tự chép chuỗi qua ranh giới process.
    // Cách cũ hay "dán thiếu / dán sai ô": (1) khôi phục Clipboard NGAY sau Ctrl+V trong khi Profiler
    // xử lý phím chậm hơn một nhịp → dán nhầm nội dung Clipboard cũ hoặc thiếu; (2) đếm số lần Tab
    // phụ thuộc focus ban đầu và thứ tự ô, lệch là dán vào ô khác (ảnh: Login trống, Password đã có).
    // Giờ: tìm ô theo NHÃN ("Server name:", "Login:", "Password:") rồi đặt chữ thẳng vào ô và đọc lại
    // để kiểm tra.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetDlgCtrlID(IntPtr hWnd);

    private const int WM_SETTEXT = 0x000C;
    private const int WM_GETTEXTLENGTH = 0x000E;
    private const int WM_COMMAND = 0x0111;
    private const int CB_SETCURSEL = 0x014E;
    private const int CBN_SELCHANGE = 1;

    private sealed record Ctl(IntPtr Hwnd, string Class, IntPtr Parent, RECT Rect, bool Visible, string Text, bool Enabled);

    [DllImport("user32.dll")]
    private static extern bool IsWindowEnabled(IntPtr hWnd);

    private static List<Ctl> DescribeControls(IntPtr root)
    {
        var list = new List<Ctl>();
        EnumChildWindows(root, (h, _) =>
        {
            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            GetWindowRect(h, out var r);
            var txt = new StringBuilder(256);
            GetWindowText(h, txt, txt.Capacity);
            list.Add(new Ctl(h, NormalizeClass(cls.ToString()), GetParent(h), r, IsWindowVisible(h), txt.ToString(), IsWindowEnabled(h)));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Ô nhập nằm cùng hàng với nhãn <paramref name="label"/> (so khớp hẳn sau khi bỏ '&amp;' và
    /// ':'): ComboBox hoặc Edit đang hiện, nằm bên phải nhãn, lệch dọc ít nhất; bỏ qua ô Edit con bên
    /// trong 1 ComboBox (lấy chính ComboBox). Null nếu không có nhãn đó trên màn hình.</summary>
    private static Ctl? FindFieldByLabel(List<Ctl> all, string label)
    {
        static string Clean(string s) => s.Replace("&", "").Replace(":", "").Trim();
        var lbl = all.FirstOrDefault(c => c.Visible && c.Class == "Static" && Clean(c.Text).Equals(label, StringComparison.OrdinalIgnoreCase));
        if (lbl is null) return null;
        var lblMidY = (lbl.Rect.Top + lbl.Rect.Bottom) / 2;
        var comboHandles = all.Where(c => c.Class == "ComboBox").Select(c => c.Hwnd).ToHashSet();

        return all
            .Where(c => c.Visible && (c.Class == "ComboBox" || c.Class == "Edit")
                        && !comboHandles.Contains(c.Parent)          // Edit con của combo → dùng combo
                        && c.Rect.Left >= lbl.Rect.Right - 4)
            .Select(c => (Ctl: c, Dy: Math.Abs((c.Rect.Top + c.Rect.Bottom) / 2 - lblMidY)))
            .Where(x => x.Dy <= 18)
            .OrderBy(x => x.Dy).ThenBy(x => x.Ctl.Rect.Left)
            .Select(x => x.Ctl)
            .FirstOrDefault();
    }

    /// <summary>Đặt chữ vào ô và đọc lại để chắc đã vào (tối đa 3 lần). Ô mật khẩu chỉ so độ dài
    /// (Windows không trả nội dung ô mật khẩu qua process khác).</summary>
    private static async Task<bool> SetControlTextAsync(IntPtr hwnd, string text, bool isPassword = false)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            SendMessage(hwnd, WM_SETTEXT, IntPtr.Zero, text);
            await Task.Delay(40);

            if (isPassword)
            {
                if ((int)SendMessage(hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero) == text.Length) return true;
            }
            else
            {
                var read = new StringBuilder(text.Length + 16);
                SendMessage(hwnd, WM_GETTEXT, (IntPtr)read.Capacity, read);
                if (read.ToString() == text) return true;
            }
        }
        return false;
    }

    /// <summary>Chọn mục <paramref name="index"/> của ComboBox rồi báo cho dialog cha như người dùng tự
    /// chọn (CBN_SELCHANGE) để các ô phụ thuộc (Login/Password theo kiểu Authentication) cập nhật.</summary>
    private static void SelectComboIndex(IntPtr combo, int index)
    {
        SendMessage(combo, CB_SETCURSEL, (IntPtr)index, IntPtr.Zero);
        var parent = GetParent(combo);
        var id = GetDlgCtrlID(combo);
        SendMessage(parent, WM_COMMAND, (IntPtr)((CBN_SELCHANGE << 16) | (id & 0xFFFF)), combo);
    }

    private static string ReadComboText(IntPtr combo)
    {
        var sb = new StringBuilder(512);
        SendMessage(combo, WM_GETTEXT, (IntPtr)sb.Capacity, sb);
        if (sb.Length > 0) return sb.ToString();

        // Combo kiểu DropDownList không có ô edit: lấy chữ của mục đang chọn.
        var sel = (int)SendMessage(combo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
        if (sel < 0) return "";
        var len = (int)SendMessage(combo, CB_GETLBTEXTLEN, (IntPtr)sel, IntPtr.Zero);
        if (len <= 0) return "";
        var item = new StringBuilder(len + 2);
        SendMessage(combo, CB_GETLBTEXT, (IntPtr)sel, item);
        return item.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const int WM_NEXTDLGCTL = 0x0028;
    private const int BM_CLICK = 0x00F5;

    // Click chuột THẬT theo toạ độ màn hình — chỉ dùng cho các control tự vẽ (Events Selection
    // grid...) không có class/tên chuẩn để dò như Button/ComboBox.
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private static async Task ClickAtScreen(int x, int y)
    {
        SetCursorPos(x, y);
        await Task.Delay(50);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
        await Task.Delay(30);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);
        await Task.Delay(120);
    }

    /// <summary>Liệt kê TOÀN BỘ descendant control (không chỉ con trực tiếp) của 1 cửa sổ Win32
    /// ngoài process, kèm class name, HWND cha thật (GetParent) và toạ độ màn hình. KHÔNG lọc
    /// theo "con trực tiếp của dialog" nữa — vì "Connect to Server"/"Trace Properties" đều là
    /// dialog dạng TAB (Login | Connection Properties | ...; General | Events Selection): nội
    /// dung mỗi tab nằm trên 1 "page" con RIÊNG (không phải con trực tiếp của khung ngoài cùng),
    /// nên lọc theo con trực tiếp sẽ loại sạch mất các ô cần điền — đây chính là lý do lần trước
    /// code rơi về nhánh dự phòng (đếm Tab) và điền sai ô.</summary>
    /// <summary>Profiler bản mới (SSMS 22) dựng dialog bằng WinForms bọc control Win32 gốc nên tên class có dạng
    /// "WindowsForms10.COMBOBOX.app.0.141b42a_r23_ad1" (hậu tố đổi theo phiên bản/process), không phải "ComboBox".
    /// Mọi chỗ so khớp class ("ComboBox", "Static", "Edit", "Button", "SysTabControl32") đều trượt trên máy đó —
    /// đó là lý do dò ô theo nhãn/vị trí thất bại và nút Remember/Connect không bấm được theo tên. Quy về
    /// tên chuẩn ở đúng 1 chỗ này; class không phải WindowsForms10.* giữ nguyên.</summary>
    private static string NormalizeClass(string className)
    {
        const string prefix = "WindowsForms10.";
        if (!className.StartsWith(prefix, StringComparison.Ordinal)) return className;
        var kind = className.Substring(prefix.Length).Split('.')[0];
        return kind.ToUpperInvariant() switch
        {
            "STATIC" => "Static",
            "COMBOBOX" => "ComboBox",
            "EDIT" => "Edit",
            "BUTTON" => "Button",
            "LISTBOX" => "ListBox",
            _ => kind, // vd "SysTabControl32", "Window"
        };
    }

    private static List<(IntPtr Hwnd, string ClassName, IntPtr Parent, int Top)> GetAllDescendantControls(IntPtr rootHwnd)
    {
        var result = new List<(IntPtr, string, IntPtr, int)>();
        EnumChildWindows(rootHwnd, (hwnd, _) =>
        {
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            GetWindowRect(hwnd, out var rect);
            result.Add((hwnd, NormalizeClass(cls.ToString()), GetParent(hwnd), rect.Top));
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Chuyển focus sang đúng control (theo HWND thật) trong dialog của process khác —
    /// dùng WM_NEXTDLGCTL. LƯU Ý: đã xác nhận qua thực tế Bee test là KHÔNG ăn thua với
    /// "Connect to Server" (focus không nhảy đi đâu cả) — nên chỉ còn dùng cho tab control (Ctrl+
    /// Tab ở RunNoTemplateSetupAsync).</summary>
    private static void FocusControl(IntPtr dialogHwnd, IntPtr controlHwnd)
        => SendMessage(dialogHwnd, WM_NEXTDLGCTL, controlHwnd, (IntPtr)1);

    private readonly AppSettings _settings;
    private readonly DbConnectionService _connections;
    private readonly Func<Workspace?> _getCurrentWorkspace;

    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _barWeb = new();
    private readonly Panel _hostPanel;

    private Process? _profilerProcess;
    private IntPtr _profilerHwnd = IntPtr.Zero;

    public SqlProfilerControl(AppSettings settings, DbConnectionService connections, Func<Workspace?> getCurrentWorkspace)
    {
        _settings = settings;
        _connections = connections;
        _getCurrentWorkspace = getCurrentWorkspace;

        Dock = DockStyle.Fill;
        BackColor = Bcode.App.UI.AppColors.Background;

        _barWeb.Dock = DockStyle.Top;
        _barWeb.Height = 120;

        _hostPanel = new Panel { Dock = DockStyle.Fill, BackColor = System.Drawing.Color.Black };
        _hostPanel.Resize += (_, _) => ResizeEmbeddedWindow();

        Controls.Add(_hostPanel);
        Controls.Add(_barWeb);

        Bcode.App.UI.ThemeManager.ThemeChanged += PushThemeToBar;
        _connections.WorkspaceChanged += PushWorkspaceHintsToBar;
        _connections.WorkspaceChanged += PushConfigToBar;
        // Tháo Profiler khỏi host TRƯỚC khi handle bị huỷ (HandleDestroyed chạy trước khi huỷ control con).
        HandleDestroyed += (_, _) => CloseProfiler();
        Disposed += (_, _) =>
        {
            Bcode.App.UI.ThemeManager.ThemeChanged -= PushThemeToBar;
            _connections.WorkspaceChanged -= PushWorkspaceHintsToBar;
            _connections.WorkspaceChanged -= PushConfigToBar;
            CloseProfiler();
        };

        _ = InitBarWebAsync();
    }

    private async Task InitBarWebAsync()
    {
        try
        {
            await Bcode.App.UI.WebViewEnvironment.InitAsync(_barWeb);

            _barWeb.CoreWebView2.WebMessageReceived += async (_, e) =>
            {
                using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                var root = doc.RootElement;
                switch (root.GetProperty("action").GetString())
                {
                    case "browse": BrowseExe(); break;
                    case "__height":
                        // Trang báo chiều cao nội dung (px thiết bị): thanh cao thêm khi cửa sổ hẹp và nội dung xuống dòng,
                        // thay vì cố định 120px làm cắt mất hàng trên/hàng dưới.
                        _barWeb.Height = Math.Clamp(root.GetProperty("height").GetInt32() + 2, Bcode.App.UI.DpiScale.Px(this, 60), Bcode.App.UI.DpiScale.Px(this, 420));
                        break;
                    case "run":
                        await RunProfilerAsync(
                            root.GetProperty("exePath").GetString() ?? "",
                            root.GetProperty("loginUser").GetString() ?? "",
                            root.GetProperty("loginPass").GetString() ?? "",
                            root.GetProperty("template").GetString() ?? "",
                            root.TryGetProperty("targetUser", out var tu) ? tu.GetString() ?? "" : "",
                            root.TryGetProperty("uid", out var ui) ? ui.GetString() ?? "" : "");
                        break;
                    case "close": CloseProfiler(); break;
                    case "lookup-uid": await LookupUidAsync(root.GetProperty("targetUser").GetString() ?? ""); break;
                    case "copy":
                        var value = root.GetProperty("value").GetString() ?? "";
                        if (value.Length > 0 && !value.StartsWith("(")) Clipboard.SetText(value);
                        break;
                    case "refresh-hint": PushWorkspaceHintsToBar(); break;
                    case "open-trace-folder": OpenTraceFolder(); break;
                    case "copy-sql": await CopySqlCommandAsync(); break;
                    case "open-saved-trace": await OpenSavedTraceAsync(); break;
                    case "save-config":
                        SaveConfig(
                            root.GetProperty("exePath").GetString() ?? "",
                            root.GetProperty("loginUser").GetString() ?? "",
                            root.GetProperty("loginPass").GetString() ?? "",
                            root.GetProperty("template").GetString() ?? "");
                        break;
                }
            };

            _barWeb.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                PushThemeToBar();
                PushConfigToBar();
                PushWorkspaceHintsToBar();
            };
            _barWeb.CoreWebView2.Navigate($"https://{Bcode.App.UI.WebViewEnvironment.Host}/sqlprofilerbar.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không khởi tạo được thanh công cụ (dùng WebView2).\nChi tiết lỗi: " + ex.Message, "Bcode — WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PushThemeToBar() { if (_barWeb.CoreWebView2 != null) _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({(Bcode.App.UI.AppColors.IsDark ? "true" : "false")})"); }
    private void PushConfigToBar()
    {
        if (_barWeb.CoreWebView2 is null) return;
        var ws = _getCurrentWorkspace();
        var cfg = JsonSerializer.Serialize(new
        {
            exePath = _settings.SqlProfilerPath,
            loginUser = _settings.ProfilerLoginUser,
            loginPass = _settings.ProfilerLoginPassword,
            template = ws?.ProfilerTemplateName ?? "",
            targetUser = ws?.ProfilerTargetUser ?? "",
            traceFile = ws is null ? "" : GetTraceFilePath(ws)
        });
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setConfig && window.setConfig({cfg})");
    }

    private static string GetTraceFolder(Workspace ws)
    {
        var root = Path.Combine(BcodePaths.AppData, "Bcode", "Traces");
        var name = string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.Name : ws.ProjectId;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(root, name);
    }
    private static string GetTraceFilePath(Workspace ws)
    {
        var name = string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.Name : ws.ProjectId;
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(GetTraceFolder(ws), $"{name}.trc");
    }
    /// <summary>
    /// "Copy SQL Command": chép câu SQL (TextData) của dòng đang chọn trong cửa sổ Profiler để dán sang SQL Query chạy.
    /// Cách 1 (chính): đọc thẳng ô chữ ở khung dưới của Profiler — khung đó luôn hiện TextData của sự kiện đang chọn — bằng
    /// WM_GETTEXT (message hệ thống, đọc được control của process khác). Ô Edit/RichEdit đang hiện lớn nhất có chữ được chọn.
    /// Cách 2 (dự phòng, khi khung đó không phải Edit chuẩn): gửi Ctrl+C cho Profiler rồi lấy ô dài nhất trong dòng vừa copy
    /// (TextData thường là ô dài nhất; dòng được Profiler copy dạng các ô cách nhau bằng Tab).
    /// </summary>
    /// <summary>Bắn sau "Copy SQL Command" với câu SQL vừa copy — nơi chứa tab (MainForm) mở SQL Query mới và dán câu đó vào.</summary>
    public event Action<string>? OpenInSqlQueryRequested;

    private async Task CopySqlCommandAsync()
    {
        if (_profilerHwnd == IntPtr.Zero || _profilerProcess is null || _profilerProcess.HasExited)
        {
            SetStatus("Chưa bung Profiler — bấm \"Bung & Đăng nhập\" trước, chọn 1 dòng sự kiện rồi bấm lại.");
            return;
        }

        var text = ReadSelectedEventText(_profilerHwnd);
        var how = "khung chi tiết";
        if (string.IsNullOrWhiteSpace(text))
        {
            text = await CopyViaCtrlCAsync(_profilerHwnd);
            how = "Ctrl+C";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            SetStatus("Không lấy được câu SQL — hãy bấm chọn 1 dòng có TextData trong Profiler rồi thử lại.");
            return;
        }

        try
        {
            Clipboard.SetText(text.Trim());
            SetStatus($"Đã copy SQL Command ({text.Trim().Length} ký tự, qua {how}) — đã mở SQL Query và dán vào.");
            // Copy xong mở luôn 1 tab SQL Query mới có sẵn câu lệnh để chạy (xem MainForm.OpenSqlProfilerTab).
            OpenInSqlQueryRequested?.Invoke(text.Trim());
        }
        catch (Exception ex)
        {
            SetStatus("Không ghi được clipboard: " + ex.Message);
        }
    }

    private static string ReadSelectedEventText(IntPtr profilerHwnd)
    {
        var candidates = DescribeControls(profilerHwnd)
            .Where(c => c.Visible && c.Class.Contains("edit", StringComparison.OrdinalIgnoreCase)
                        && c.Rect.Bottom - c.Rect.Top >= 30 && c.Rect.Right - c.Rect.Left >= 100)
            .OrderByDescending(c => (long)(c.Rect.Right - c.Rect.Left) * (c.Rect.Bottom - c.Rect.Top));

        foreach (var c in candidates)
        {
            var len = (int)SendMessage(c.Hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            if (len <= 0 || len > 5_000_000) continue;
            var sb = new StringBuilder(len + 1);
            SendMessage(c.Hwnd, WM_GETTEXT, (IntPtr)(len + 1), sb);
            var s = sb.ToString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return "";
    }

    private async Task<string> CopyViaCtrlCAsync(IntPtr profilerHwnd)
    {
        try
        {
            string? before = Clipboard.ContainsText() ? Clipboard.GetText() : null;
            Clipboard.Clear();
            SetForegroundWindow(profilerHwnd);
            await Task.Delay(250);
            SendKeys.SendWait("^c");
            await Task.Delay(300);

            var copied = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (string.IsNullOrEmpty(copied) && before is not null) { Clipboard.SetText(before); return ""; }

            // Dòng được copy là các ô cách nhau bằng Tab (có thể kèm dòng tiêu đề) → lấy ô dài nhất, thường là TextData.
            return copied.Split('\t', '\r', '\n')
                .Select(p => p.Trim())
                .OrderByDescending(p => p.Length)
                .FirstOrDefault() ?? "";
        }
        catch { return ""; }
    }

    private void OpenTraceFolder()
    {
        var ws = _getCurrentWorkspace();
        if (ws is null) { SetStatus("Chưa chọn Workspace nào."); return; }
        try { var folder = GetTraceFolder(ws); Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) { SetStatus("Không mở được thư mục Trace: " + ex.Message); }
    }
    private void PushWorkspaceHintsToBar()
    {
        if (_barWeb.CoreWebView2 is null) return;
        var ws = _getCurrentWorkspace();
        var hint = ws is null ? "(chưa có Workspace)" : string.IsNullOrWhiteSpace(ws.ProjectId) ? "(Workspace chưa khai ID dự án)" : $"%{ws.ProjectId}%";
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setDbHint && window.setDbHint({JsonSerializer.Serialize(hint)})");
    }
    private void SetStatus(string text) { if (_barWeb.CoreWebView2 != null) _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setStatus && window.setStatus({JsonSerializer.Serialize(text)})"); }
    private void SetUid(string text) { if (_barWeb.CoreWebView2 != null) _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setUid && window.setUid({JsonSerializer.Serialize(text)})"); }
    private void SetRunning(bool running) { if (_barWeb.CoreWebView2 != null) _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setRunning && window.setRunning({(running ? "true" : "false")})"); }

    private void BrowseExe()
    {
        using var ofd = new OpenFileDialog { Filter = "Profiler.exe|Profiler.exe;profiler*.exe|Tệp thực thi (*.exe)|*.exe|Tất cả (*.*)|*.*" };
        if (!string.IsNullOrWhiteSpace(_settings.SqlProfilerPath) && File.Exists(_settings.SqlProfilerPath)) ofd.InitialDirectory = Path.GetDirectoryName(_settings.SqlProfilerPath);
        if (ofd.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _settings.SqlProfilerPath = ofd.FileName;
            try { _settings.Save(); } catch { }
            PushConfigToBar();
            SetStatus("Đã chọn Profiler.exe.");
        }
    }

    private void SaveConfig(string exePath, string loginUser, string loginPass, string template)
    {
        var changed = false;
        if (_settings.SqlProfilerPath != exePath) { _settings.SqlProfilerPath = exePath; changed = true; }
        if (_settings.ProfilerLoginUser != loginUser) { _settings.ProfilerLoginUser = loginUser; changed = true; }
        if (_settings.ProfilerLoginPassword != loginPass) { _settings.ProfilerLoginPassword = loginPass; changed = true; }
        var ws = _getCurrentWorkspace();
        if (ws is not null && ws.ProfilerTemplateName != template) { ws.ProfilerTemplateName = template; changed = true; }
        if (!changed) return;
        try { _settings.Save(); SetStatus("Đã lưu cấu hình Profiler."); } catch (Exception ex) { SetStatus("Lỗi lưu settings.json: " + ex.Message); }
    }

    private async Task LookupUidAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) { SetStatus("Chưa nhập tên user cần tra (u_name)."); return; }
        if (_getCurrentWorkspace() is null) { SetStatus("Chưa chọn Workspace nào."); return; }
        try
        {
            await using var conn = _connections.CreateConnection(useSysDatabase: false);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT u_id FROM vsysuser WHERE u_name = @name", conn);
            cmd.Parameters.AddWithValue("@name", name);
            var result = await cmd.ExecuteScalarAsync();
            if (result is null || result is DBNull) { SetUid(""); SetStatus($"Không tìm thấy user \"{name}\" trong vsysuser (App Data)."); }
            else
            {
                var uid = result.ToString() ?? "";
                SetUid(uid);
                SetStatus($"u_id của \"{name}\" là {uid} — copy dán vào bộ lọc ApplicationName trong Profiler.");
                var ws = _getCurrentWorkspace();
                if (ws is not null && ws.ProfilerTargetUser != name) { ws.ProfilerTargetUser = name; try { _settings.Save(); } catch { } }
            }
        }
        catch (Exception ex) { SetStatus("Lỗi tra u_id: " + ex.Message); }
    }

    // =========================================================================
    // KHỞI TẠO TEMPLATE .TDF TỰ ĐỘNG VÀO THƯ MỤC CỦA SQL PROFILER
    // =========================================================================
    private void EnsureProfilerTemplateExists(string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName)) return;

        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            // Profiler thường lưu template cá nhân trong 2 đường dẫn phổ biến này
            string[] baseDirs = {
                Path.Combine(appData, "Microsoft", "SQL Profiler"),
                Path.Combine(appData, "Microsoft", "SQL Server Management Studio")
            };

            string tdfContent = @"<?xml version=""1.0"" encoding=""utf-16""?>
<Root>
  <TemplateDefinition dtversion=""2"">
    <Name>" + templateName + @"</Name>
    <Description>Template tự động tạo bởi Bcode</Description>
    <Events>
      <Event Enabled=""True"" ID=""10"" Name=""RPC:Completed"" />
      <Event Enabled=""True"" ID=""13"" Name=""SQL:BatchStarting"" />
    </Events>
    <Columns>
      <Column Enabled=""True"" ID=""1"" Name=""TextData"" />
      <Column Enabled=""True"" ID=""12"" Name=""SPID"" />
      <Column Enabled=""True"" ID=""14"" Name=""StartTime"" />
      <Column Enabled=""True"" ID=""35"" Name=""DatabaseName"" />
    </Columns>
  </TemplateDefinition>
</Root>";

            foreach (var baseDir in baseDirs)
            {
                if (!Directory.Exists(baseDir)) continue;

                var templateDirs = Directory.GetDirectories(baseDir, "Templates", SearchOption.AllDirectories);
                foreach (var tDir in templateDirs)
                {
                    // Quét sâu vào các thư mục phiên bản con (vd: Microsoft SQL Server\150)
                    var targetDirs = Directory.GetDirectories(tDir, "*", SearchOption.AllDirectories);
                    foreach (var targetDir in targetDirs)
                    {
                        string filePath = Path.Combine(targetDir, $"{templateName}.tdf");
                        if (!File.Exists(filePath)) File.WriteAllText(filePath, tdfContent);
                    }
                }
            }
        }
        catch { /* Bỏ qua lỗi ghi file template nếu không đủ quyền, auto-type vẫn sẽ chạy được với template khác */ }
    }

    private async Task RunProfilerAsync(string exePath, string loginUser, string loginPass, string template, string targetUser = "", string uidFromBar = "")
    {
        exePath = exePath.Trim();
        if (!File.Exists(exePath)) { SetStatus("Không tìm thấy Profiler.exe ở đường dẫn đã khai."); return; }

        var ws = _getCurrentWorkspace();
        if (ws is null) { SetStatus("Chưa chọn Workspace nào."); return; }

        if (_profilerProcess is { HasExited: false }) { SetStatus("Profiler đang chạy rồi trong tab này."); return; }

        // Chưa khai tên Template thì dùng mã workspace (ProjectId, hoặc tên WS) — cũng là tên file .tdf sẽ tự tạo.
        if (string.IsNullOrWhiteSpace(template))
            template = !string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.ProjectId : ws.Name;
        template = template.Trim();
        SaveConfig(exePath, loginUser, loginPass, template);
        try { Directory.CreateDirectory(GetTraceFolder(ws)); } catch { }

        // ĐÃ BỎ gọi EnsureProfilerTemplateExists(template) ở đây — xác nhận qua ảnh lỗi Bee gửi
        // ("Template ... has a wrong format") rằng file .tdf giả do hàm này tự sinh ra bị chính
        // SQL Server Profiler coi là SAI ĐỊNH DẠNG, gây lỗi thay vì giúp ích. Việc "chưa có
        // Template" giờ được xử lý đúng cách hơn ở nhánh RunNoTemplateSetupAsync (tự thiết lập
        // Events Selection/Column Filters) — hàm EnsureProfilerTemplateExists vẫn giữ nguyên định
        // nghĩa bên trên để tiện đối chiếu sau này nhưng không còn được gọi nữa.

        // u_id cho filter ApplicationName (= ID). Ưu tiên giá trị đang hiện ở ô "→ u_id" trên thanh công
        // cụ; chưa có thì tra theo tên user đang gõ ở ô "User cần theo dõi" (trước đây chỉ dùng tên
        // đã lưu từ lần bấm "Tra u_id" thành công, nên gõ tên mà chưa bấm tra thì u_id rỗng và
        // template sinh ra không có filter ID).
        string autoUid = (uidFromBar ?? "").Trim();
        var lookupName = !string.IsNullOrWhiteSpace(targetUser) ? targetUser.Trim() : ws.ProfilerTargetUser;
        if (autoUid.Length == 0 && !string.IsNullOrWhiteSpace(lookupName))
        {
            try
            {
                await using var conn = _connections.CreateConnection(useSysDatabase: false);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand("SELECT u_id FROM vsysuser WHERE u_name = @name", conn);
                cmd.Parameters.AddWithValue("@name", lookupName);
                var res = await cmd.ExecuteScalarAsync();
                if (res != null && res != DBNull.Value) autoUid = (res.ToString() ?? "").Trim();
            }
            catch { }
        }
        if (autoUid.Length > 0) SetUid(autoUid);
        if (!string.IsNullOrWhiteSpace(targetUser) && ws.ProfilerTargetUser != targetUser.Trim())
        {
            ws.ProfilerTargetUser = targetUser.Trim();
            try { _settings.Save(); } catch { }
        }
        if (autoUid.Length == 0)
            SetStatus("Chưa có u_id (nhập tên user rồi bấm \"Tra u_id\") — template sẽ chỉ lọc theo Database, không lọc ID.");

        // Template CHƯA có → tự ghi file .tdf (chỉ SQL:BatchStarting + lọc DatabaseName %mã WS% [+ u_id])
        // vào thư mục template của Profiler TRƯỚC khi mở Profiler.exe, để nó nạp như template tự lưu.
        // Template đã có (người dùng tự lưu, hoặc dựng sẵn của Profiler) thì giữ nguyên, không đụng.
        // Cách này thay cho việc bấm chuột theo toạ độ vào lưới Events Selection (rất dễ lệch).
        await EnsureTemplateFileAsync(exePath, ws, template, autoUid);

        // ĐÃ BỎ HẲN "-S/-U/-P" khỏi dòng lệnh khởi động — xác nhận qua thực tế (ảnh + video Bee gửi)
        // rằng truyền sẵn đủ Server+Login+Password qua tham số dòng lệnh khiến chính Profiler.exe
        // tự động bỏ qua cả "Connect to Server" lẫn việc dừng ở "Trace Properties" để Bee xem lại,
        // rồi tự chạy luôn 1 trace với Template "Standard" mặc định — không phải lỗi do nhúng cửa
        // sổ (embed) như suy đoán ban đầu. Nên giờ mở Profiler.exe HOÀN TOÀN TRỐNG (không tham số
        // gì), rồi tự lái toàn bộ quy trình bằng Win32 automation giống hệt cách Bee làm tay:
        // Ctrl+N (New Trace) → tự điền Server/Login/Password ở "Connect to Server" → tự chọn
        // Trace Template ở "Trace Properties" và dừng lại đó chờ Bee tự bấm Run.
        try
        {
            SetStatus("Đang mở Profiler.exe...");
            _profilerProcess = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
            if (_profilerProcess is null) { SetStatus("Không khởi động được Profiler.exe."); return; }

            // Chờ cửa sổ chính (khung "SQL Server Profiler" còn trống, chưa có trace nào) sẵn
            // sàng — CHƯA nhúng gì vào tab cả.
            var mainHwnd = IntPtr.Zero;
            for (var i = 0; i < 100; i++)
            {
                await Task.Delay(100);
                _profilerProcess.Refresh();
                if (_profilerProcess.HasExited)
                {
                    SetStatus("Profiler.exe đã thoát ngay sau khi mở.");
                    _profilerProcess = null;
                    return;
                }
                mainHwnd = _profilerProcess.MainWindowHandle;
                if (mainHwnd != IntPtr.Zero) break;
            }
            if (mainHwnd == IntPtr.Zero) { SetStatus("Không lấy được cửa sổ chính của Profiler."); return; }

            SetRunning(true);

            // 1. Bấm "New Trace" (Ctrl+N) trên cửa sổ chính đang trống.
            SetForegroundWindow(mainHwnd);
            await Task.Delay(200);
            SendKeys.SendWait("^n");

            // 2. Tự điền Server/Login/Password vào khung "Connect to Server".
            var connected = await AutomateConnectToServer(ws.Server, loginUser, loginPass);
            if (!connected) return; // status đã được set bên trong AutomateConnectToServer

            // 3. Tự chọn Trace Template ở khung "Trace Properties" — KHÔNG tự bấm Run.
            var templateMatched = await AutomateProfilerUI(template);

            // 3b. Nếu gõ Template mà KHÔNG khớp (dự án này chưa từng lưu Template) — tự thiết lập
            // Events Selection/Column Filters mặc định thay vào đó (xem ghi chú ở
            // RunNoTemplateSetupAsync — phần rủi ro nhất là bấm theo toạ độ ước lượng).
            if (templateMatched == false)
                await RunNoTemplateSetupAsync(autoUid, ws.ProjectId);
            else if (templateMatched == true)
                await ClickRunAsync(); // Template có sẵn → chạy luôn, khỏi qua Events Selection

            // 4. Sau khi đã điền sẵn Template (hoặc tự thiết lập xong), để Bee tự xem lại và bấm
            // Run — chỉ nhúng cửa sổ trace THẬT vào tab này SAU KHI khung Trace Properties đã
            // đóng lại.
            _ = WaitAndEmbedTraceWindowAsync();
        }
        catch (Exception ex) { SetStatus("Lỗi mở Profiler: " + ex.Message); }
    }

    /// <summary>Chờ tới khi khung "Trace Properties" đã đóng (Bee đã tự xem lại Template và bấm
    /// Run) rồi mới lấy MainWindowHandle thật (cửa sổ lưới trace đang chạy) để nhúng vào tab —
    /// tránh nhúng nhầm chính khung Trace Properties khi nó còn là modal dialog đang mở.</summary>
    private async Task WaitAndEmbedTraceWindowAsync()
    {
        for (var i = 0; i < 600; i++)
        {
            await Task.Delay(500);
            if (_profilerProcess is null || _profilerProcess.HasExited) return;
            if (FindWindow(null, "Trace Properties") != IntPtr.Zero) continue; // Bee còn đang xem/chỉnh Trace Properties

            _profilerProcess.Refresh();
            var hwnd = _profilerProcess.MainWindowHandle;
            if (hwnd == IntPtr.Zero) continue;

            EmbedWindow(hwnd);
            SetStatus("Đã nhúng cửa sổ trace vào tab này.");
            return;
        }
        SetStatus("Chưa thấy cửa sổ trace sau khi chờ — nếu Bee đã bấm Run, thử bấm lại \"Bung & Đăng nhập\" hoặc kiểm tra cửa sổ Profiler.");
    }

    /// <summary>Escape các ký tự có ý nghĩa đặc biệt trong cú pháp SendKeys (+ ^ % ~ ( ) {{ }} [ ])
    /// — chỉ còn dùng cho vòng gõ từng ký tự vào dropdown "Use the template" bên dưới (BẮT BUỘC
    /// gõ thật từng phím để dropdown tự lọc/nhảy tới đúng mục, dán clipboard không có tác dụng
    /// này). Các ô nhập liệu thường (Server/Login/Password/Trace name) giờ dán bằng
    /// <see cref="PasteText"/> nên không cần escape nữa — dán clipboard là văn bản gốc, không bị
    /// SendKeys hiểu nhầm thành phím tắt.</summary>
    private static string EscapeSendKeys(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c is '+' or '^' or '%' or '~' or '(' or ')' or '{' or '}' or '[' or ']')
                sb.Append('{').Append(c).Append('}');
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Dán nhanh 1 đoạn text vào ô đang focus qua Clipboard + Ctrl+V — thay cho gõ từng
    /// ký tự (nhanh hơn nhiều, và không lo ký tự đặc biệt bị SendKeys hiểu sai). Tự lưu lại và
    /// khôi phục nội dung Clipboard cũ ngay sau khi dán xong, để không để lộ mật khẩu/thông tin
    /// còn sót lại trong Clipboard của máy Bee.</summary>
    private static async Task PasteTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        string? previous = null;
        try { if (Clipboard.ContainsText()) previous = Clipboard.GetText(); } catch { }

        try
        {
            Clipboard.SetText(text);
            SendKeys.SendWait("^v");
            // Profiler (process khác) xử lý Ctrl+V trễ hơn SendWait một nhịp: khôi phục Clipboard ngay
            // là nguyên nhân "dán thiếu / dán nhầm nội dung cũ". Chờ cho nó dán xong rồi mới trả lại.
            await Task.Delay(300);
        }
        finally
        {
            try
            {
                if (previous is not null) Clipboard.SetText(previous);
                else Clipboard.Clear();
            }
            catch { }
        }
    }

    /// <summary>Tự điền Server/Login/Password vào khung "Connect to Server" hiện ra sau khi bấm
    /// New Trace, tick sẵn "Remember password" rồi bấm nút Connect.
    /// ĐÃ BỎ HẲN mọi cách dò control phức tạp (WM_NEXTDLGCTL, tính lệch vị trí theo focus qua
    /// GetGUIThreadInfo...) vì qua nhiều lần Bee test thực tế đều KHÔNG ăn thua — focus không
    /// nhảy đúng chỗ, khiến dán nhầm ô. Theo đúng quan sát thực tế Bee xác nhận LẶP LẠI nhiều lần:
    /// ngay khi khung này vừa mở lên, focus mặc định LUÔN nằm sẵn ở ô "Server name" — nên giờ chỉ
    /// còn: dán thẳng Server vào đó (không cần điều hướng gì), Tab 2 lần xuống ô "Login:" (ô nhập
    /// liệu THẬT trong form, KHÔNG phải tên tab "Login" ở thanh tab phía trên — thanh tab cũng
    /// nằm trong vòng lặp Tab nên nếu đếm sai sẽ vô tình dừng ở đó), dán Login, Tab 1 lần nữa
    /// xuống "Password:", dán Password. Riêng "Remember password" và nút "Connect" bấm thẳng bằng
    /// BM_CLICK sau khi dò theo TÊN (không cần Tab/focus, đáng tin cậy hơn hẳn).</summary>
    private async Task<bool> AutomateConnectToServer(string server, string loginUser, string loginPass)
    {
        SetStatus("Đang chờ cửa sổ Connect to Server...");
        IntPtr hWnd = IntPtr.Zero;
        for (var i = 0; i < 100; i++)
        {
            hWnd = FindWindow(null, "Connect to Server");
            if (hWnd != IntPtr.Zero) break;
            await Task.Delay(100);
        }

        if (hWnd == IntPtr.Zero)
        {
            // Có thể Profiler đã tự nhớ & bỏ qua bước này — không chặn hẳn, để bước tìm
            // "Trace Properties" phía sau tự quyết định tiếp có đi được không.
            SetStatus("Không thấy cửa sổ Connect to Server — có thể Profiler tự bỏ qua bước này, đang chờ Trace Properties...");
            return true;
        }

        SetForegroundWindow(hWnd);
        await Task.Delay(100);

        try
        {
            // CÁCH CHÍNH: đặt chữ THẲNG vào từng ô (tìm theo nhãn), đọc lại để kiểm tra — không dùng
            // Clipboard/Tab nên không còn dán thiếu, dán sai ô.
            var direct = await FillConnectDialogDirectAsync(hWnd, server, loginUser, loginPass);
            if (!direct)
            {
                // Dự phòng (không dò được nhãn/ô — Profiler bản khác): cách cũ bằng Clipboard + Tab.
                SetStatus("Không điền trực tiếp được form Connect to Server — thử cách dán + Tab...");
                SendKeys.SendWait("^a");
                await PasteTextAsync(server);
                await Task.Delay(120);
                SendKeys.SendWait("{TAB}{TAB}");
                await Task.Delay(120);
                SendKeys.SendWait("^a");
                await PasteTextAsync(loginUser);
                await Task.Delay(120);
                SendKeys.SendWait("{TAB}");
                await Task.Delay(120);
                await PasteTextAsync(loginPass);
                await Task.Delay(120);
            }

            // "Remember password" + "Connect" — dò theo TÊN rồi bấm thẳng bằng BM_CLICK, không
            // cần Tab/focus tới nữa.
            var all = GetAllDescendantControls(hWnd);
            var rememberCheckbox = all.FirstOrDefault(c => WindowTextContains(c.Hwnd, c.ClassName, "Button", "Remember")).Hwnd;
            if (rememberCheckbox != IntPtr.Zero) SendMessage(rememberCheckbox, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            await Task.Delay(100);

            var connectButton = all.FirstOrDefault(c => WindowTextContains(c.Hwnd, c.ClassName, "Button", "Connect")).Hwnd;
            if (connectButton != IntPtr.Zero)
                SendMessage(connectButton, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            else
                SendKeys.SendWait("{ENTER}");

            SetStatus("Đã điền Server/Login/Password, đang kết nối...");
            await Task.Delay(200);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi Auto-type Connect to Server: " + ex.Message);
            return false;
        }
    }

    /// <summary>Điền Server name / Login / Password bằng WM_SETTEXT. Đảm bảo Authentication là SQL Server
    /// Authentication trước (nếu đang là Windows Authentication thì Login/Password bị khóa). Trả
    /// false nếu không dò được ô hoặc đặt rồi đọc lại không khớp → bên gọi dùng cách dự phòng.</summary>
    private sealed record ConnectFields(Ctl Server, Ctl Login, Ctl Password, Ctl? Auth);

    /// <summary>Tìm 3 ô của dialog "Connect to Server". Thử theo NHÃN trước; không được (máy khác dùng
    /// class/nhãn khác — trên máy Bee dò theo nhãn đã thất bại, khiến code chờ hết 6 giây rồi mới dán
    /// bằng cách dự phòng) thì dò theo VỊ TRÍ: các ComboBox/Edit đang hiện xếp từ trên xuống là
    /// Server type, Server name, Authentication, Login, Password, Encryption, Host name.</summary>
    private static ConnectFields? FindConnectFields(List<Ctl> all)
    {
        var byLabelServer = FindFieldByLabel(all, "Server name");
        var byLabelLogin = FindFieldByLabel(all, "Login");
        var byLabelPass = FindFieldByLabel(all, "Password");
        if (byLabelServer is not null && byLabelLogin is not null && byLabelPass is not null)
            return new ConnectFields(byLabelServer, byLabelLogin, byLabelPass, FindFieldByLabel(all, "Authentication"));

        var comboHandles = all.Where(c => c.Class == "ComboBox").Select(c => c.Hwnd).ToHashSet();
        var fields = all
            .Where(c => c.Visible && (c.Class == "ComboBox" || c.Class == "Edit") && !comboHandles.Contains(c.Parent))
            .OrderBy(c => c.Rect.Top).ThenBy(c => c.Rect.Left)
            .ToList();
        if (fields.Count >= 5
            && fields[0].Class == "ComboBox" && fields[1].Class == "ComboBox"
            && fields[2].Class == "ComboBox" && fields[3].Class == "ComboBox"
            && fields[4].Class == "Edit")
            return new ConnectFields(fields[1], fields[3], fields[4], fields[2]);
        return null;
    }

    /// <summary>Ghi toàn bộ control của dialog (class, hiện/ẩn, toạ độ, chữ) ra file để chẩn đoán khi
    /// không dò được ô — gửi file này cho người viết code là biết ngay Profiler trên máy đó khác ở đâu.</summary>
    private static string DumpDialogControls(List<Ctl> all)
    {
        try
        {
            var dir = Path.Combine(BcodePaths.AppData, "Bcode");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "profiler_connect_dump.txt");
            var lines = all.Select(c => $"{(c.Visible ? "V" : "-")}{(c.Enabled ? "E" : "-")}  {c.Class,-28} top={c.Rect.Top,5} left={c.Rect.Left,5} w={c.Rect.Right - c.Rect.Left,4} h={c.Rect.Bottom - c.Rect.Top,3}  parent={GetClassNameOf(c.Parent)}  text=\"{c.Text}\"");
            File.WriteAllLines(path, lines);
            return path;
        }
        catch { return ""; }
    }

    private static string GetClassNameOf(IntPtr h)
    {
        var sb = new StringBuilder(128);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private async Task<bool> FillConnectDialogDirectAsync(IntPtr dialog, string server, string user, string pass)
    {
        // 1) Chờ dialog SẴN SÀNG thật, không chỉ "đã hiện". Lần chạy đầu (Profiler khởi động nguội) dialog
        // hiện ra trước khi nạp xong danh sách Server/Login nhớ lần trước — phần nạp đó có thể ghi đè hoặc
        // xóa chữ vừa đặt. Giới hạn chờ ngắn (2,5 giây): không dò được ô thì bỏ ngay xuống cách dự phòng
        // chứ không đứng chờ lâu như trước (6 giây).
        List<Ctl> all = new();
        ConnectFields? f = null;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2500)
        {
            all = DescribeControls(dialog);
            f = FindConnectFields(all);
            if (f is not null) break;
            await Task.Delay(50);
        }
        if (f is null)
        {
            var dump = DumpDialogControls(all);
            SetStatus("Không dò được các ô của Connect to Server" + (dump.Length > 0 ? $" — đã ghi chi tiết ra {dump}" : "") + "; dùng cách dán dự phòng.");
            return false;
        }
        await Task.Delay(120); // cho phần nạp danh sách nhớ xong hẳn

        // Authentication phải là SQL Server Authentication, nếu không ô Login/Password bị khóa.
        if (f.Auth is { Class: "ComboBox" } auth)
        {
            var current = ReadComboText(auth.Hwnd);
            if (!current.Contains("SQL Server", StringComparison.OrdinalIgnoreCase))
            {
                var idx = ReadComboItems(auth.Hwnd).FindIndex(t => t.Contains("SQL Server Authentication", StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                {
                    SelectComboIndex(auth.Hwnd, idx);
                    await Task.Delay(150); // Login/Password vừa được bật/tắt theo kiểu Authentication
                }
            }
        }

        // 2) Đặt chữ rồi KIỂM TRA LẠI sau khi dialog ổn định (2 lần cách nhau) — nếu có gì ghi đè muộn
        // thì đặt lại, tối đa 4 vòng.
        for (var round = 0; round < 4; round++)
        {
            f = FindConnectFields(DescribeControls(dialog)) ?? f;

            await SetControlTextAsync(f.Server.Hwnd, server);
            await SetControlTextAsync(f.Login.Hwnd, user);
            // Ô mật khẩu (ES_PASSWORD): nhiều bản Windows KHÔNG trả độ dài của nó qua process khác (luôn 0).
            // Đặt xong đọc thử ngay; đọc được thì mới kiểm tra mật khẩu ở các lần sau, không đọc được thì
            // tin vào việc đã đặt.
            SendMessage(f.Password.Hwnd, WM_SETTEXT, IntPtr.Zero, pass);
            await Task.Delay(40);
            var passReadable = (int)SendMessage(f.Password.Hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero) == pass.Length;

            await Task.Delay(120);
            if (!AllFieldsHold(f.Server, f.Login, f.Password, server, user, pass, passReadable)) continue;
            await Task.Delay(100);
            if (AllFieldsHold(f.Server, f.Login, f.Password, server, user, pass, passReadable)) return true;
        }
        SetStatus("Form Connect to Server không giữ giá trị đã điền (bị ghi đè nhiều lần).");
        return false;
    }

    private static bool AllFieldsHold(Ctl server, Ctl login, Ctl pass, string s, string u, string p, bool passReadable)
    {
        static string Read(IntPtr h, int cap)
        {
            var sb = new StringBuilder(cap);
            SendMessage(h, WM_GETTEXT, (IntPtr)sb.Capacity, sb);
            return sb.ToString();
        }
        return Read(server.Hwnd, s.Length + 16) == s
            && Read(login.Hwnd, u.Length + 16) == u
            && (!passReadable || (int)SendMessage(pass.Hwnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero) == p.Length);
    }

    /// <summary>Điền "Trace name" (ô đầu tiên của tab General) — cũng bằng WM_SETTEXT, dự phòng dán.</summary>
    private async Task SetTraceNameAsync(IntPtr traceDialog, string name)
    {
        var field = FindFieldByLabel(DescribeControls(traceDialog), "Trace name");
        if (field is not null && await SetControlTextAsync(field.Hwnd, name)) return;
        await PasteTextAsync(name);
    }

    private static bool WindowTextContains(IntPtr hwnd, string actualClassName, string expectedClassName, string substr)
    {
        if (actualClassName != expectedClassName) return false;
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString().Contains(substr, StringComparison.OrdinalIgnoreCase);
    }

    private const int CB_GETCOUNT = 0x0146;

    private static List<string> ReadComboItems(IntPtr combo)
    {
        var items = new List<string>();
        var count = (int)SendMessage(combo, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero);
        for (var i = 0; i < count; i++)
        {
            var len = (int)SendMessage(combo, CB_GETLBTEXTLEN, (IntPtr)i, IntPtr.Zero);
            if (len <= 0) { items.Add(""); continue; }
            var sb = new StringBuilder(len + 2);
            SendMessage(combo, CB_GETLBTEXT, (IntPtr)i, sb);
            items.Add(sb.ToString());
        }
        return items;
    }

    /// <summary>Mục khớp HẲN với tên template: "tên" hoặc "tên (user)" / "tên (default)" — không khớp
    /// kiểu tiền tố ("PMT" không khớp "PMT2" hay "TSQL"). Ưu tiên "(user)". -1 nếu không có.</summary>
    private static int FindExactTemplateIndex(List<string> items, string name)
    {
        int Match(Func<string, bool> pred) => items.FindIndex(t => pred(t.Trim()));
        var userIdx = Match(t => t.Equals(name + " (user)", StringComparison.OrdinalIgnoreCase));
        if (userIdx >= 0) return userIdx;
        var plainIdx = Match(t => t.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (plainIdx >= 0) return plainIdx;
        return Match(t => t.StartsWith(name + " (", StringComparison.OrdinalIgnoreCase) && t.EndsWith(")"));
    }

    /// <summary>Combo "Use the template" của tab General: combo có mục Standard/TSQL (phân biệt với combo khác nếu có).</summary>
    private static IntPtr FindTemplateCombo(IntPtr traceDialog)
    {
        var combos = GetAllDescendantControls(traceDialog).Where(c => c.ClassName == "ComboBox").Select(c => c.Hwnd).ToList();
        foreach (var cb in combos)
        {
            var items = ReadComboItems(cb);
            if (items.Any(t => t.StartsWith("Standard", StringComparison.OrdinalIgnoreCase) || t.StartsWith("TSQL", StringComparison.OrdinalIgnoreCase)))
                return cb;
        }
        return combos.FirstOrDefault();
    }

    /// <summary>Chọn đúng template theo tên (khớp hẳn "tên" / "tên (user)") bằng CB_SETCURSEL + CBN_SELCHANGE rồi kiểm tra
    /// lại mục đang chọn; lệch thì chọn lại (tối đa 4 lần). Không có mục khớp hẳn → không chọn gì (bên gọi báo "không khớp").</summary>
    private async Task SelectTemplateExactAsync(IntPtr traceDialog, IntPtr combo, string templateName)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            // Đọc lại danh sách mỗi lần: Profiler có thể vừa nạp thêm template user vào combo.
            var items = ReadComboItems(combo);
            var target = FindExactTemplateIndex(items, templateName);
            if (target < 0) return;

            SetForegroundWindow(traceDialog);
            SelectComboIndex(combo, target);
            await Task.Delay(180 + attempt * 150);

            if ((int)SendMessage(combo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero) == target) return;
        }
    }

    /// <summary>Trả về true nếu "Use the template" đã nhảy đúng Template mong muốn; false nếu gõ
    /// xong mà tên KHÔNG khớp (dự án này chưa từng lưu Template — cần Bee tự thiết lập Events
    /// Selection/Column Filters, xem RunNoTemplateSetupAsync); null nếu không tìm thấy được cửa
    /// sổ Trace Properties (lỗi khác, đã SetStatus báo bên trong).</summary>
    private async Task<bool?> AutomateProfilerUI(string templateName)
    {
        // Chưa khai tên Template = coi như "không có Template": cũng đi tiếp tới thiết lập Events
        // (chỉ giữ SQL:BatchStarting) thay vì bỏ ngang. Vẫn phải chờ cửa sổ Trace Properties hiện
        // ra trước, nên không return sớm ở đây nữa.
        var hasTemplateName = !string.IsNullOrWhiteSpace(templateName);

        SetStatus(hasTemplateName ? "Đang chờ cửa sổ Trace Properties để nạp Template..." : "Đang chờ cửa sổ Trace Properties...");
        IntPtr hWnd = IntPtr.Zero;

        // Quét tìm cửa sổ trong tối đa 15 giây
        for (int i = 0; i < 150; i++)
        {
            hWnd = FindWindow(null, "Trace Properties");
            if (hWnd != IntPtr.Zero) break;
            await Task.Delay(100);
        }

        if (hWnd == IntPtr.Zero)
        {
            SetStatus("Không tìm thấy cửa sổ Trace Properties.");
            return null;
        }

        // Chờ tới khi cửa sổ NẠP XONG danh sách template (combo có mục) thay vì ngủ cố định 900ms:
        // nhanh hơn khi máy nhanh, vẫn đủ lâu khi Profiler khởi động nguội.
        for (var i = 0; i < 60; i++)
        {
            var cb = GetAllDescendantControls(hWnd).FirstOrDefault(c => c.ClassName == "ComboBox").Hwnd;
            if (cb != IntPtr.Zero && (int)SendMessage(cb, CB_GETCOUNT, IntPtr.Zero, IntPtr.Zero) > 0) break;
            await Task.Delay(100);
        }

        SetForegroundWindow(hWnd);
        await Task.Delay(100);

        if (!hasTemplateName)
        {
            SetStatus("Chưa khai Trace Template — tự thiết lập Events (chỉ giữ SQL:BatchStarting)...");
            return false;
        }

        try
        {
            // BƯỚC 1: Focus mặc định đang ở "Trace name:", dán tên template vào đây
            await SetTraceNameAsync(hWnd, templateName);
            await Task.Delay(60);

            // BƯỚC 2-4: chọn template bằng THÔNG ĐIỆP Win32 (CB_SETCURSEL + CBN_SELCHANGE), không bằng phím.
            // Trước đây dùng Alt+U rồi Home + Down×N bằng SendKeys: phím đi vào cửa sổ ĐANG ở foreground (nếu Profiler chưa
            // kịp lên trên thì phím rơi vào chỗ khác), và mỗi lần Down Profiler lại nạp 1 template (chậm) nên phím dồn/rớt →
            // dừng sai mục (vd chọn nhầm vpmilk thay vì kog) hoặc chưa kịp cập nhật khi mình đọc lại → tưởng "không khớp" và
            // chuyển sang Events Selection thay vì bấm Run. Chọn thẳng theo chỉ số + kiểm tra lại + thử lại là chắc chắn hơn.
            var pickCombo = FindTemplateCombo(hWnd);
            if (pickCombo != IntPtr.Zero)
                await SelectTemplateExactAsync(hWnd, pickCombo, templateName.Trim());
            await Task.Delay(150);

            // ĐÃ BỎ LỆNH TỰ BẤM {ENTER}.
            // Cửa sổ sẽ giữ nguyên cấu hình đã chọn để bạn kiểm tra và tự bấm Run.

            // BƯỚC 4b: Nếu Template gõ vào trỏ tới 1 file .tdf lỗi định dạng, ngay sau khi chốt
            // bằng TAB, chính SQL Server Profiler sẽ bật ra 1 hộp thoại LỖI RIÊNG có tiêu đề CŨNG
            // là "SQL Server Profiler" (trùng tên với khung chính đang trống phía sau) — nên phải
            // phân biệt 2 khung này bằng cách: chỉ coi là hộp thoại lỗi khi nó có 1 nút con tên
            // "OK" (khung chính trống thì không có nút này). Gặp đúng lỗi này thì tự bấm OK rồi coi
            // như "không khớp Template" luôn, để RunProfilerAsync tự chuyển qua
            // RunNoTemplateSetupAsync thiết lập Events Selection thay thế.
            await Task.Delay(250); // hộp thoại lỗi .tdf (nếu có) hiện hơi trễ sau khi chọn template
            var maybeErrorHwnd = FindWindow(null, "SQL Server Profiler");
            if (maybeErrorHwnd != IntPtr.Zero)
            {
                var okBtn = GetAllDescendantControls(maybeErrorHwnd).FirstOrDefault(c => WindowTextContains(c.Hwnd, c.ClassName, "Button", "OK")).Hwnd;
                if (okBtn != IntPtr.Zero)
                {
                    SendMessage(okBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    await Task.Delay(250);
                    SetStatus($"Template '{templateName}' bị lỗi định dạng file .tdf — đã tự bấm OK, chuyển sang tự thiết lập Events Selection...");
                    return false;
                }
            }

            // Đọc lại text thật sự đang hiện trong combo "Use the template" — Profiler nạp template xong mới đổi chữ, nên
            // chờ tới khi khớp (tối đa ~2,5s) thay vì đọc 1 lần ngay rồi kết luận "không khớp".
            var wanted = templateName.Trim();
            var templateCombo = FindTemplateCombo(hWnd);
            var currentText = "";
            var matched = false;
            for (var attempt = 0; attempt < 25; attempt++)
            {
                if (templateCombo != IntPtr.Zero) currentText = ReadComboText(templateCombo);
                var shown = currentText.Trim();
                // Khớp HẲN "tên" hoặc "tên (user)" — StartsWith lỏng trước đây coi "PMT" khớp cả "PMT2".
                matched = shown.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                    || (shown.StartsWith(wanted + " (", StringComparison.OrdinalIgnoreCase) && shown.EndsWith(")"));
                if (matched) break;
                await Task.Delay(100);
            }

            if (matched)
            {
                // Template đã có sẵn Events/Filters đúng ý → không cần qua tab Events Selection,
                // bấm Run luôn (RunProfilerAsync gọi ClickRunAsync khi hàm này trả true).
                SetStatus($"Đã nạp Template '{templateName}' — tự bấm Run...");
            }
            else
            {
                SetStatus($"Không khớp Template '{templateName}' (đang là \"{currentText}\") — dự án này có thể chưa từng lưu Template, đang tự thiết lập Events Selection...");
            }

            return matched;
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi Auto-type: " + ex.Message);
            return null;
        }
    }

    /// <summary>[ƯỚC LƯỢNG TOẠ ĐỘ TỪ ẢNH — RỦI RO CAO NHẤT trong toàn bộ automation] Khi gõ
    /// Template không khớp (dự án này chưa từng lưu Template), Bee muốn tự động: qua tab "Events
    /// Selection", bỏ tick 5 event mặc định của Standard (Audit Login, Audit Logout,
    /// ExistingConnection, RPC:Completed, SQL:BatchCompleted) — chỉ chừa lại SQL:BatchStarting,
    /// tick "Show all columns", rồi mở "Column Filters...".
    /// Grid Events Selection là control tự vẽ, KHÔNG có class/tên chuẩn để dò như Button/ComboBox
    /// — chỉ tự động hoá được bằng cách bấm chuột THẬT theo TOẠ ĐỘ TỈ LỆ ước lượng từ đúng ảnh Bee
    /// gửi (dialog ước lượng ~890x578). Nếu tick nhầm dòng, Bee chụp lại đúng lúc đó (trước khi
    /// Run) để mình chỉnh lại tỉ lệ trong mảng UncheckPointRatios bên dưới cho chuẩn.
    /// Khung "Column Filters..." (tick ApplicationName/DatabaseName, gõ Like) mình CHƯA có ảnh
    /// nào để ước lượng toạ độ — nên dừng lại ngay sau khi MỞ khung đó, để Bee tự tick + Run như
    /// bình thường lần này, đồng thời chụp giúp khung đó gửi mình để hoàn thiện tự động hoá.</summary>
    private async Task RunNoTemplateSetupAsync(string autoUid, string? projectId)
    {
        var traceHwnd = FindWindow(null, "Trace Properties");
        if (traceHwnd == IntPtr.Zero) { SetStatus("Không còn thấy cửa sổ Trace Properties để tự thiết lập."); return; }

        GetWindowRect(traceHwnd, out var rect);
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        Task ClickRatio(double xr, double yr) => ClickAtScreen(rect.Left + (int)(w * xr), rect.Top + (int)(h * yr));

        // 1. Qua tab "Events Selection" bằng Ctrl+Tab (focus vào chính tab control trước để chắc
        // ăn phím tắt này nhận đúng target).
        var tabControl = GetAllDescendantControls(traceHwnd).FirstOrDefault(c => c.ClassName == "SysTabControl32").Hwnd;
        if (tabControl != IntPtr.Zero)
        {
            FocusControl(traceHwnd, tabControl);
            await Task.Delay(150);
        }
        SetForegroundWindow(traceHwnd);
        SendKeys.SendWait("^{TAB}");
        await Task.Delay(400);

        // 2. Bỏ tick 5 event mặc định của Standard — chỉ chừa lại SQL:BatchStarting (đã tick sẵn
        // theo đúng ảnh Bee gửi, không cần đụng vào).
        // Toạ độ tỉ lệ tính lại từ ảnh thật khung "Trace Properties > Events Selection" (SSMS 22
        // Profiler, dialog 750x480 gồm cả thanh tiêu đề): cột tick của event ở x=665 → 0.052;
        // các hàng y=400/417/451/485/519 (so với đỉnh dialog y=262) → 0.2875/0.3229/0.3938/0.4646/
        // 0.5354. Hàng SQL:BatchStarting (y=536) cố ý KHÔNG đụng — đó là event duy nhất cần giữ.
        (double X, double Y)[] uncheckPointRatios =
        {
            (0.052, 0.2875), // Audit Login
            (0.052, 0.3229), // Audit Logout
            (0.052, 0.3938), // ExistingConnection
            (0.052, 0.4646), // RPC:Completed
            (0.052, 0.5354), // SQL:BatchCompleted
        };
        foreach (var (xr, yr) in uncheckPointRatios)
            await ClickRatio(xr, yr);

        // 3. Tick "Show all columns" (checkbox ở x=1202,y=628 trong ảnh → 0.768, 0.7625).
        await ClickRatio(0.768, 0.7625);

        // 4. Mở "Column Filters..." — đây LÀ Button chuẩn, dò theo tên nên đáng tin cậy hơn hẳn
        // phần grid ở trên.
        var columnFiltersBtn = GetAllDescendantControls(traceHwnd).FirstOrDefault(c =>
        {
            if (c.ClassName != "Button") return false;
            var sb = new StringBuilder(256);
            GetWindowText(c.Hwnd, sb, sb.Capacity);
            // Tên nút thật là "Column &Filters..." (có & đánh dấu phím tắt) — phải bỏ & trước khi so.
            return sb.ToString().Replace("&", "").Contains("Column Filters", StringComparison.OrdinalIgnoreCase);
        }).Hwnd;

        if (columnFiltersBtn == IntPtr.Zero)
        {
            SetStatus("Đã bỏ tick Events mặc định + tick Show all columns, nhưng KHÔNG tìm thấy nút \"Column Filters...\" — Bee tự bấm, khai 2 bộ lọc ApplicationName/DatabaseName rồi tự Run nhé.");
            return;
        }

        SendMessage(columnFiltersBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        await Task.Delay(400);

        var uidHint = string.IsNullOrWhiteSpace(autoUid) ? "" : $" (u_id đã tra sẵn: {autoUid} — hoặc bấm nút \"Copy\" cạnh ô \"Tra u_id\" trên thanh công cụ Bcode)";
        var dbHint = string.IsNullOrWhiteSpace(projectId) ? "" : $", DatabaseName Like %{projectId}%";
        SetStatus($"Đã bỏ tick Events mặc định, tick Show all columns, và mở Column Filters. Mình CHƯA có ảnh khung này nên chưa tự tick được — Bee tự tick ApplicationName Like{uidHint}{dbHint}, OK rồi tự bấm Run. Chụp lại khung Column Filters gửi mình để lần sau tự động hoá nốt phần này nhé!");
    }

    /// <summary>Bấm nút "Run" của khung "Trace Properties" (Button chuẩn → dò theo tên rồi BM_CLICK,
    /// không phụ thuộc toạ độ). Không thấy nút thì để người dùng tự bấm.</summary>
    private async Task ClickRunAsync()
    {
        var traceHwnd = FindWindow(null, "Trace Properties");
        if (traceHwnd == IntPtr.Zero) return; // khung đã đóng (vd Profiler tự chạy) — không còn gì để bấm

        var runBtn = GetAllDescendantControls(traceHwnd).FirstOrDefault(c =>
        {
            if (c.ClassName != "Button") return false;
            var sb = new StringBuilder(64);
            GetWindowText(c.Hwnd, sb, sb.Capacity);
            return sb.ToString().Replace("&", "").Trim().Equals("Run", StringComparison.OrdinalIgnoreCase);
        }).Hwnd;

        if (runBtn == IntPtr.Zero)
        {
            SetStatus("Đã nạp Template nhưng không tìm thấy nút Run — bạn tự bấm Run nhé.");
            return;
        }

        await Task.Delay(200);
        SendMessage(runBtn, BM_CLICK, IntPtr.Zero, IntPtr.Zero);
        SetStatus("Đã nạp Template và bấm Run.");
    }

    private async Task EnsureTemplateFileAsync(string exePath, Workspace ws, string template, string autoUid)
    {
        try
        {
            string? version;
            await using (var conn = _connections.CreateConnection(useSysDatabase: false))
            {
                await conn.OpenAsync();
                await using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(50))", conn);
                version = (await cmd.ExecuteScalarAsync()) as string;
            }
            if (string.IsNullOrWhiteSpace(version)) return;

            var dbKey = !string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.ProjectId : ws.Name;
            var result = ProfilerTemplateService.EnsureTemplate(exePath, version, ws.Server, template,
                string.IsNullOrWhiteSpace(autoUid) ? null : autoUid,
                string.IsNullOrWhiteSpace(dbKey) ? null : "%" + dbKey + "%");
            if (result is { Created: true })
            {
                SetStatus($"Chưa có template '{template}' — đã tự tạo (chỉ SQL:BatchStarting, lọc DB %{dbKey}%{(string.IsNullOrWhiteSpace(autoUid) ? "" : ", ID " + autoUid)}).");
            }
            else if (result is { Created: false } existing)
            {
                OfferTemplateFilterUpdate(existing.Path, template,
                    string.IsNullOrWhiteSpace(autoUid) ? null : autoUid,
                    string.IsNullOrWhiteSpace(dbKey) ? null : "%" + dbKey + "%");
            }
        }
        catch (Exception)
        {
            // Không tạo được template (không kết nối được / thiếu quyền ghi thư mục) → luồng cũ ở
            // RunNoTemplateSetupAsync vẫn chạy làm phương án dự phòng.
        }
    }

    /// <summary>Template đã có sẵn nhưng filter ID (ApplicationName) hoặc DatabaseName lệch với giá trị hiện
    /// tại → TỰ CẬP NHẬT luôn vào file .tdf rồi dùng (không hỏi — người dùng muốn thế). Bản cũ luôn được
    /// giữ lại thành .tdf.bak để quay về được. Chỉ xử lý
    /// template cá nhân trong AppData, dạng 1 event SQL:BatchStarting; template dựng sẵn của Profiler
    /// hay dạng khác thì bỏ qua. Bản cũ giữ lại thành *.tdf.bak.</summary>
    private void OfferTemplateFilterUpdate(string path, string template, string? wantApp, string? wantDb)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!path.StartsWith(appData, StringComparison.OrdinalIgnoreCase)) return;
        var info = ProfilerTemplateService.TryRead(File.ReadAllBytes(path));
        if (info is null) return;

        var changes = new List<string>();
        string? newApp = null, newDb = null;
        if (wantApp != null && !string.Equals(info.AppLike, wantApp, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"ID (ApplicationName): {(info.AppLike ?? "(không lọc)")} → {wantApp}");
            newApp = wantApp;
        }
        if (wantDb != null && !string.Equals(info.DatabaseLike, wantDb, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add($"DatabaseName: {(info.DatabaseLike ?? "(không lọc)")} → {wantDb}");
            newDb = wantDb;
        }
        if (changes.Count == 0) return;


        SetStatus(ProfilerTemplateService.TryUpdateFilters(path, newApp, newDb)
            ? $"Đã cập nhật template '{template}': " + string.Join("; ", changes) + " (bản cũ: .tdf.bak)."
            : $"Không cập nhật được template '{template}' — dùng như đang có.");
    }

    private async Task OpenSavedTraceAsync()
    {
        var ws = _getCurrentWorkspace();
        if (ws is null) { SetStatus("Chưa chọn Workspace nào."); return; }

        var exePath = _settings.SqlProfilerPath?.Trim() ?? "";
        if (!File.Exists(exePath)) { SetStatus("Không tìm thấy Profiler.exe ở đường dẫn đã khai."); return; }

        var traceFile = GetTraceFilePath(ws);
        if (!File.Exists(traceFile)) { SetStatus($"Chưa có file trace nào được lưu tại \"{traceFile}\"."); return; }
        if (_profilerProcess is { HasExited: false }) { SetStatus("Đang có Profiler chạy trong tab này — bấm \"✕ Đóng Profiler\" trước."); return; }

        try
        {
            SetStatus("Đang mở trace đã lưu...");
            _profilerProcess = Process.Start(new ProcessStartInfo(exePath, $"-f \"{traceFile}\"") { UseShellExecute = true });
            if (_profilerProcess is null) { SetStatus("Không khởi động được Profiler.exe."); return; }

            var hwnd = IntPtr.Zero;
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(200);
                _profilerProcess.Refresh();
                if (_profilerProcess.HasExited) { SetStatus("Profiler.exe đã thoát ngay khi mở file trace."); _profilerProcess = null; return; }
                hwnd = _profilerProcess.MainWindowHandle;
                if (hwnd != IntPtr.Zero) break;
            }
            if (hwnd == IntPtr.Zero) { SetStatus("Không lấy được cửa sổ Profiler để nhúng."); SetRunning(true); return; }

            EmbedWindow(hwnd);
            SetRunning(true);
            SetStatus($"Đã mở trace đã lưu: \"{traceFile}\".");
        }
        catch (Exception ex) { SetStatus("Lỗi mở trace đã lưu: " + ex.Message); }
    }

    // Profiler được nhúng bằng SetParent nên chia sẻ hàng đợi input với UI thread của Bcode: chờ nó thoát
    // (WaitForExit) ngay trên UI thread khiến cả 2 chờ nhau → treo; và nếu để cửa sổ còn là con của
    // _hostPanel thì khi tab bị huỷ Windows huỷ luôn cửa sổ Profiler (lỗi/crash). Vì vậy: tháo ra khỏi
    // host + ẩn trước, rồi đóng/kill ở luồng nền, không đụng control nào sau khi đã bị huỷ.
    private void CloseProfiler()
    {
        var proc = _profilerProcess;
        var hwnd = _profilerHwnd;
        _profilerProcess = null;
        _profilerHwnd = IntPtr.Zero;

        if (hwnd != IntPtr.Zero)
        {
            try { ShowWindow(hwnd, SW_HIDE); SetParent(hwnd, IntPtr.Zero); } catch { }
        }

        if (proc != null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.CloseMainWindow();
                        if (!proc.WaitForExit(2000)) proc.Kill();
                    }
                }
                catch { }
                finally { proc.Dispose(); }
            });
        }

        if (IsDisposed || Disposing) return;
        try { SetRunning(false); SetStatus("Đã đóng Profiler."); } catch { }
    }

    // =========================================================================
    // WIN32 EMBED WINDOW
    // =========================================================================
    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private void EmbedWindow(IntPtr hwnd)
    {
        _profilerHwnd = hwnd;
        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~WS_POPUP; style &= ~WS_CAPTION; style &= ~WS_THICKFRAME; style |= WS_CHILD;
        SetWindowLong(hwnd, GWL_STYLE, style);
        SetParent(hwnd, _hostPanel.Handle);
        ShowWindow(hwnd, SW_SHOW);
        ResizeEmbeddedWindow();
    }
    private void ResizeEmbeddedWindow()
    {
        if (_profilerHwnd == IntPtr.Zero) return;
        SetWindowPos(_profilerHwnd, IntPtr.Zero, 0, 0, _hostPanel.ClientSize.Width, _hostPanel.ClientSize.Height, SWP_NOZORDER | SWP_FRAMECHANGED);
    }
}