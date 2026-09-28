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
    private static List<(IntPtr Hwnd, string ClassName, IntPtr Parent, int Top)> GetAllDescendantControls(IntPtr rootHwnd)
    {
        var result = new List<(IntPtr, string, IntPtr, int)>();
        EnumChildWindows(rootHwnd, (hwnd, _) =>
        {
            var cls = new StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            GetWindowRect(hwnd, out var rect);
            result.Add((hwnd, cls.ToString(), GetParent(hwnd), rect.Top));
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
                    case "run":
                        await RunProfilerAsync(
                            root.GetProperty("exePath").GetString() ?? "",
                            root.GetProperty("loginUser").GetString() ?? "",
                            root.GetProperty("loginPass").GetString() ?? "",
                            root.GetProperty("template").GetString() ?? "");
                        break;
                    case "close": CloseProfiler(); break;
                    case "lookup-uid": await LookupUidAsync(root.GetProperty("targetUser").GetString() ?? ""); break;
                    case "copy":
                        var value = root.GetProperty("value").GetString() ?? "";
                        if (value.Length > 0 && !value.StartsWith("(")) Clipboard.SetText(value);
                        break;
                    case "refresh-hint": PushWorkspaceHintsToBar(); break;
                    case "open-trace-folder": OpenTraceFolder(); break;
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
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "Traces");
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

    private async Task RunProfilerAsync(string exePath, string loginUser, string loginPass, string template)
    {
        exePath = exePath.Trim();
        if (!File.Exists(exePath)) { SetStatus("Không tìm thấy Profiler.exe ở đường dẫn đã khai."); return; }

        var ws = _getCurrentWorkspace();
        if (ws is null) { SetStatus("Chưa chọn Workspace nào."); return; }

        if (_profilerProcess is { HasExited: false }) { SetStatus("Profiler đang chạy rồi trong tab này."); return; }

        SaveConfig(exePath, loginUser, loginPass, template);
        try { Directory.CreateDirectory(GetTraceFolder(ws)); } catch { }

        // ĐÃ BỎ gọi EnsureProfilerTemplateExists(template) ở đây — xác nhận qua ảnh lỗi Bee gửi
        // ("Template ... has a wrong format") rằng file .tdf giả do hàm này tự sinh ra bị chính
        // SQL Server Profiler coi là SAI ĐỊNH DẠNG, gây lỗi thay vì giúp ích. Việc "chưa có
        // Template" giờ được xử lý đúng cách hơn ở nhánh RunNoTemplateSetupAsync (tự thiết lập
        // Events Selection/Column Filters) — hàm EnsureProfilerTemplateExists vẫn giữ nguyên định
        // nghĩa bên trên để tiện đối chiếu sau này nhưng không còn được gọi nữa.

        // Tra cứu u_id để tự động hóa gõ phím
        string autoUid = "";
        if (!string.IsNullOrWhiteSpace(ws.ProfilerTargetUser))
        {
            try
            {
                await using var conn = _connections.CreateConnection(useSysDatabase: false);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand("SELECT u_id FROM vsysuser WHERE u_name = @name", conn);
                cmd.Parameters.AddWithValue("@name", ws.ProfilerTargetUser);
                var res = await cmd.ExecuteScalarAsync();
                if (res != null && res != DBNull.Value) autoUid = res.ToString() ?? "";
            }
            catch { }
        }

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
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(200);
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
            await Task.Delay(400);
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
    private static void PasteText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        string? previous = null;
        try { if (Clipboard.ContainsText()) previous = Clipboard.GetText(); } catch { }

        try
        {
            Clipboard.SetText(text);
            SendKeys.SendWait("^v");
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
        for (var i = 0; i < 20; i++)
        {
            hWnd = FindWindow(null, "Connect to Server");
            if (hWnd != IntPtr.Zero) break;
            await Task.Delay(500);
        }

        if (hWnd == IntPtr.Zero)
        {
            // Có thể Profiler đã tự nhớ & bỏ qua bước này — không chặn hẳn, để bước tìm
            // "Trace Properties" phía sau tự quyết định tiếp có đi được không.
            SetStatus("Không thấy cửa sổ Connect to Server — có thể Profiler tự bỏ qua bước này, đang chờ Trace Properties...");
            return true;
        }

        await Task.Delay(300);
        SetForegroundWindow(hWnd);
        await Task.Delay(300);

        try
        {
            // Focus mặc định lúc này đã nằm sẵn ở "Server name" (đã xác nhận qua thực tế) — dán
            // thẳng, không cần Tab trước.
            SendKeys.SendWait("^a");
            PasteText(server);
            await Task.Delay(120);

            // Tab 2 lần: Server name → Authentication → Login.
            SendKeys.SendWait("{TAB}{TAB}");
            await Task.Delay(120);
            SendKeys.SendWait("^a");
            PasteText(loginUser);
            await Task.Delay(120);

            // Tab 1 lần: Login → Password.
            SendKeys.SendWait("{TAB}");
            await Task.Delay(120);
            PasteText(loginPass);
            await Task.Delay(120);

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
            await Task.Delay(500);
            return true;
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi Auto-type Connect to Server: " + ex.Message);
            return false;
        }
    }

    private static bool WindowTextContains(IntPtr hwnd, string actualClassName, string expectedClassName, string substr)
    {
        if (actualClassName != expectedClassName) return false;
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString().Contains(substr, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Trả về true nếu "Use the template" đã nhảy đúng Template mong muốn; false nếu gõ
    /// xong mà tên KHÔNG khớp (dự án này chưa từng lưu Template — cần Bee tự thiết lập Events
    /// Selection/Column Filters, xem RunNoTemplateSetupAsync); null nếu không tìm thấy được cửa
    /// sổ Trace Properties (lỗi khác, đã SetStatus báo bên trong).</summary>
    private async Task<bool?> AutomateProfilerUI(string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName)) return null;

        SetStatus("Đang chờ cửa sổ Trace Properties để nạp Template...");
        IntPtr hWnd = IntPtr.Zero;

        // Quét tìm cửa sổ trong tối đa 15 giây
        for (int i = 0; i < 30; i++)
        {
            hWnd = FindWindow(null, "Trace Properties");
            if (hWnd != IntPtr.Zero) break;
            await Task.Delay(500);
        }

        if (hWnd == IntPtr.Zero)
        {
            SetStatus("Không tìm thấy cửa sổ Trace Properties.");
            return null;
        }

        // Tạm dừng để cửa sổ Profiler load xong toàn bộ nút bấm
        await Task.Delay(900);

        SetForegroundWindow(hWnd);
        await Task.Delay(300);

        try
        {
            // BƯỚC 1: Focus mặc định đang ở "Trace name:", dán tên template vào đây
            PasteText(templateName);
            await Task.Delay(200);

            // BƯỚC 2: Nhảy tới dropdown "Use the template:" bằng Alt+U
            SendKeys.SendWait("%u");
            await Task.Delay(300); // Chờ dropdown kịp kích hoạt

            // BƯỚC 3: GÕ THẬT từng ký tự (không dán được) — dropdown "Use the template" chỉ tự lọc
            // và nhảy tới đúng mục khi nhận từng phím gõ thật (kiểu type-ahead), dán clipboard
            // không kích hoạt được hành vi này.
            foreach (char c in templateName)
            {
                SendKeys.SendWait(EscapeSendKeys(c.ToString()));
                await Task.Delay(35);
            }
            await Task.Delay(250);

            // BƯỚC 4: Chốt giá trị bằng phím TAB để không bị trượt kết quả
            SendKeys.SendWait("{TAB}");
            await Task.Delay(250);

            // ĐÃ BỎ LỆNH TỰ BẤM {ENTER}.
            // Cửa sổ sẽ giữ nguyên cấu hình đã chọn để bạn kiểm tra và tự bấm Run.

            // BƯỚC 4b: Nếu Template gõ vào trỏ tới 1 file .tdf lỗi định dạng, ngay sau khi chốt
            // bằng TAB, chính SQL Server Profiler sẽ bật ra 1 hộp thoại LỖI RIÊNG có tiêu đề CŨNG
            // là "SQL Server Profiler" (trùng tên với khung chính đang trống phía sau) — nên phải
            // phân biệt 2 khung này bằng cách: chỉ coi là hộp thoại lỗi khi nó có 1 nút con tên
            // "OK" (khung chính trống thì không có nút này). Gặp đúng lỗi này thì tự bấm OK rồi coi
            // như "không khớp Template" luôn, để RunProfilerAsync tự chuyển qua
            // RunNoTemplateSetupAsync thiết lập Events Selection thay thế.
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

            // Đọc lại text thật sự đang hiện trong combo "Use the template" (chỉ có đúng 1
            // ComboBox trên tab General) để biết gõ có nhảy đúng Template hay không — combo chưa
            // từng lưu Template thì vẫn đứng ở "Standard (default)" (hoặc tên gõ dở không khớp).
            var templateCombo = GetAllDescendantControls(hWnd).FirstOrDefault(c => c.ClassName == "ComboBox").Hwnd;
            var currentText = "";
            if (templateCombo != IntPtr.Zero)
            {
                var sb = new StringBuilder(256);
                GetWindowText(templateCombo, sb, sb.Capacity);
                currentText = sb.ToString();
            }

            var matched = currentText.TrimStart().StartsWith(templateName.Trim(), StringComparison.OrdinalIgnoreCase);

            if (matched)
            {
                SetStatus($"Đã điền Template '{templateName}'. Bạn hãy tự bấm nút Run nhé!");
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
        (double X, double Y)[] uncheckPointRatios =
        {
            (0.042, 0.234), // Audit Login
            (0.042, 0.265), // Audit Logout
            (0.042, 0.330), // ExistingConnection
            (0.042, 0.396), // RPC:Completed
            (0.042, 0.462), // SQL:BatchCompleted
        };
        foreach (var (xr, yr) in uncheckPointRatios)
            await ClickRatio(xr, yr);

        // 3. Tick "Show all columns".
        await ClickRatio(0.738, 0.673);

        // 4. Mở "Column Filters..." — đây LÀ Button chuẩn, dò theo tên nên đáng tin cậy hơn hẳn
        // phần grid ở trên.
        var columnFiltersBtn = GetAllDescendantControls(traceHwnd).FirstOrDefault(c =>
        {
            if (c.ClassName != "Button") return false;
            var sb = new StringBuilder(256);
            GetWindowText(c.Hwnd, sb, sb.Capacity);
            return sb.ToString().Contains("Column Filters", StringComparison.OrdinalIgnoreCase);
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

    private void CloseProfiler()
    {
        try { if (_profilerProcess is { HasExited: false }) { _profilerProcess.CloseMainWindow(); if (!_profilerProcess.WaitForExit(2000)) _profilerProcess.Kill(); } }
        catch { }
        finally { _profilerProcess = null; _profilerHwnd = IntPtr.Zero; SetRunning(false); SetStatus("Đã đóng Profiler."); }
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