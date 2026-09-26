using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Controls;

/// <summary>
/// "SQL Profiler" (Ctrl+3): bung nhanh SQL Server Profiler.exe THẬT (không viết lại), tự điền
/// sẵn Server của Workspace đang chọn qua tham số dòng lệnh "-S" (đã xác nhận hoạt động đúng),
/// rồi NHÚNG cửa sổ chính của Profiler vào ngay trong tab này (kỹ thuật SetParent của Win32 —
/// dùng lại đúng cửa sổ thật, không phải giả lập giao diện).
///
/// ĐÃ XÁC NHẬN THỰC TẾ qua nhiều lần Bee test: "-U/-P" (tự điền Login/Pass) và "-T" (tự nạp Trace
/// Template) KHÔNG đáng tin cậy trên bản Profiler.exe này — "-U/-P" bị bỏ qua (khung Connect to
/// Server vẫn hiện Login cũ do Windows/SSMS tự nhớ từ trước, không phải "profile" Bcode truyền
/// vào), còn "-T" tệ hơn: khiến Profiler rơi vào trạng thái mở lại file trace tĩnh cũ thay vì bắt
/// đầu trace sống mới. Nên đã BỎ HẲN "-T" khỏi dòng lệnh; "-U/-P" vẫn giữ lại (vô hại dù không ăn
/// thua, để phòng trường hợp máy khác/bản Profiler khác lại nhận đúng). Bee cần tự: (1) gõ tay
/// Login "profile"/Pass đã khai lần đầu, tick "Remember password" — Windows/SSMS sẽ tự nhớ các
/// lần sau, không phải do Bcode; (2) tự chọn đúng tên Template ở dropdown "Use the template"
/// trong Trace Properties mỗi lần (ô "Trace Template" trên thanh công cụ giờ chỉ là ghi chú nhắc
/// tên, không tự nạp qua dòng lệnh nữa).
///
/// Thanh công cụ phía trên (đường dẫn, login, Trace Template, các nút, khung tra u_id, gợi ý
/// bộ lọc DatabaseName) là WebView2 (Web/Shell/sqlprofilerbar.html), giống hệt cách
/// WCommandTreeControl/RawSqlControl dựng thanh công cụ của chúng — để đồng nhất diện mạo với
/// phần còn lại của app (bo góc, tông màu theo AppColors qua shell.css) thay vì TextBox/Button
/// WinForms mặc định. Phần thân bên dưới (nơi nhúng cửa sổ Profiler.exe) BẮT BUỘC vẫn là
/// Panel WinForms thuần — không thể nhúng 1 cửa sổ Win32 ngoài vào bên trong nội dung HTML.
///
/// Theo yêu cầu của Bee: KHÔNG cố gắng tự động tích sự kiện / gõ Column Filters bên trong
/// Trace Properties (quá rủi ro vì không có cách kiểm chứng control thật của Profiler.exe từ
/// xa). Lần đầu Bee tự thiết lập Trace Properties (tích SQL:BatchStarting, khai 2 bộ lọc
/// ApplicationName/DatabaseName) như bình thường ngay trên cửa sổ đã nhúng này, rồi bấm
/// File > Save As > Trace Template... trong chính Profiler để lưu lại; gõ đúng tên template đó
/// vào ô "Trace Template" trên thanh công cụ chỉ để LÀM GHI CHÚ nhắc tên — các lần sau Bee vẫn
/// cần TỰ CHỌN tay đúng tên đó ở dropdown "Use the template" (không tự nạp qua dòng lệnh được,
/// xem lý do ở đoạn "-T" phía trên).
///
/// Có sẵn 2 ô hỗ trợ để Bee copy-dán khi tự khai 2 bộ lọc đó lần đầu:
///  - "User cần theo dõi" + nút "Tra u_id": tự SELECT u_id FROM vsysuser WHERE u_name=... (App
///    Data của Workspace đang chọn — bảng vsysuser nằm ở App Data, không phải Sys Data) — dán
///    u_id ra vào bộ lọc ApplicationName trong Profiler.
///  - "Lọc DatabaseName gợi ý": tự ghép sẵn %ProjectId% từ chính Workspace (vd KOG → %KOG%).
///
/// Trace Template (Workspace.ProfilerTemplateName) được lưu THEO TỪNG WORKSPACE — không dùng
/// chung 1 tên cho mọi dự án nữa (trước đây nằm ở AppSettings.ProfilerTemplateName, dùng chung —
/// sai vì mỗi dự án cần bộ lọc/tên Template khác nhau, vd KOG có Template riêng tên "kog"). Login
/// dùng để tự đăng nhập vào chính Profiler (mặc định user "profile"/"fsd") vẫn dùng chung mọi dự
/// án (AppSettings.ProfilerLoginUser/Password) — đúng như Bee mô tả ban đầu, đây là tài khoản
/// bắt profile, không phải tài khoản riêng theo dự án.
///
/// Thư mục lưu file trace (.trc) nằm NGAY TRONG dữ liệu của Bcode, tự tạo theo từng dự án:
/// %AppData%\Bcode\Traces\&lt;ProjectId&gt;\&lt;ProjectId&gt;.trc — không cần Bee tự gõ đường dẫn.
/// Có nút "📂 Mở thư mục Trace" để mở nhanh bằng Explorer, và nút "🗂 Mở trace đã lưu" để tự nhúng
/// lại đúng file .trc cũ (nếu đã có) vào tab này để xem lại — dùng đúng tham số "-f" của
/// Profiler.exe (xem ghi chú bên dưới, đã XÁC NHẬN THỰC TẾ qua báo lỗi của Bee).
///
/// LƯU Ý: "-f đường_dẫn" chỉ MỞ 1 file trace ĐÃ CÓ SẴN để xem lại — không phải chỗ để Profiler tự
/// ghi trace mới ra file khi đang chạy (đã thử và bị lỗi "Failed to open a file" vì lần đầu file
/// chưa tồn tại) — nên KHÔNG dùng -f khi "Bung & Đăng nhập" (chạy trace mới) nữa. Muốn Profiler tự
/// lưu dữ liệu trace đang chạy ra file, Bee cần tự tick "Save to file" ngay trong Trace Properties
/// và dán đúng đường dẫn Bcode đang hiện ở ô "File trace" — CHƯA rõ việc này có được nhớ lại khi
/// Save As Trace Template hay không (Template thường chỉ lưu Events/Columns/Filters, không chắc
/// lưu cả đường dẫn Save to file), Bee thử và báo lại nếu mỗi lần chạy đều phải tick/gõ lại.
///
/// GIẢ ĐỊNH còn lại cần Bee xác nhận (chưa có cách tự kiểm chứng trên máy thật):
///  - "-S" (chỉ định Server) hoạt động đúng — đã xác nhận qua thực tế (Server tự điền đúng trong
///    Connect to Server). "-U/-P" và "-T" KHÔNG đáng tin cậy — đã xác nhận qua thực tế, xem đoạn
///    ghi chú ở đầu file.
///  - Cửa sổ chính của Profiler.exe có thể nhúng được qua SetParent (kỹ thuật chuẩn cho app
///    Win32/MFC cũ) — đã xác nhận hoạt động tốt qua thực tế.
/// </summary>
public class SqlProfilerControl : UserControl
{
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
        _barWeb.Height = 80;

        _hostPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
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
                    case "browse":
                        BrowseExe();
                        break;
                    case "run":
                        await RunProfilerAsync(
                            root.GetProperty("exePath").GetString() ?? "",
                            root.GetProperty("loginUser").GetString() ?? "",
                            root.GetProperty("loginPass").GetString() ?? "",
                            root.GetProperty("template").GetString() ?? "");
                        break;
                    case "close":
                        CloseProfiler();
                        break;
                    case "lookup-uid":
                        await LookupUidAsync(root.GetProperty("targetUser").GetString() ?? "");
                        break;
                    case "copy":
                        var value = root.GetProperty("value").GetString() ?? "";
                        if (value.Length > 0 && !value.StartsWith("(")) Clipboard.SetText(value);
                        break;
                    case "refresh-hint":
                        PushWorkspaceHintsToBar();
                        break;
                    case "open-trace-folder":
                        OpenTraceFolder();
                        break;
                    case "open-saved-trace":
                        await OpenSavedTraceAsync();
                        break;
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
            MessageBox.Show(this,
                "Không khởi tạo được thanh công cụ (dùng WebView2).\nChi tiết lỗi: " + ex.Message,
                "Bcode — WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PushThemeToBar()
    {
        if (_barWeb.CoreWebView2 is null) return;
        var isDark = Bcode.App.UI.AppColors.IsDark ? "true" : "false";
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
    }

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

    // Thư mục/tệp trace nằm ngay trong dữ liệu của Bcode, tự tạo riêng theo từng dự án —
    // %AppData%\Bcode\Traces\<ProjectId>\<ProjectId>.trc — Bee không cần tự gõ đường dẫn.
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
        try
        {
            var folder = GetTraceFolder(ws);
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus("Không mở được thư mục Trace: " + ex.Message);
        }
    }

    private void PushWorkspaceHintsToBar()
    {
        if (_barWeb.CoreWebView2 is null) return;
        var ws = _getCurrentWorkspace();
        var hint = ws is null
            ? "(chưa có Workspace)"
            : string.IsNullOrWhiteSpace(ws.ProjectId) ? "(Workspace chưa khai ID dự án)" : $"%{ws.ProjectId}%";
        var arg = JsonSerializer.Serialize(hint);
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setDbHint && window.setDbHint({arg})");
    }

    private void SetStatus(string text)
    {
        if (_barWeb.CoreWebView2 is null) return;
        var arg = JsonSerializer.Serialize(text);
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setStatus && window.setStatus({arg})");
    }

    private void SetUid(string text)
    {
        if (_barWeb.CoreWebView2 is null) return;
        var arg = JsonSerializer.Serialize(text);
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setUid && window.setUid({arg})");
    }

    private void SetRunning(bool running)
    {
        if (_barWeb.CoreWebView2 is null) return;
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setRunning && window.setRunning({(running ? "true" : "false")})");
    }

    private void BrowseExe()
    {
        using var ofd = new OpenFileDialog { Filter = "Profiler.exe|Profiler.exe;profiler*.exe|Tệp thực thi (*.exe)|*.exe|Tất cả (*.*)|*.*" };
        if (!string.IsNullOrWhiteSpace(_settings.SqlProfilerPath) && File.Exists(_settings.SqlProfilerPath))
            ofd.InitialDirectory = Path.GetDirectoryName(_settings.SqlProfilerPath);

        if (ofd.ShowDialog(FindForm()) == DialogResult.OK)
        {
            _settings.SqlProfilerPath = ofd.FileName;
            try { _settings.Save(); } catch { /* lưu cấu hình là tiện ích thêm */ }
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

        // Trace Template lưu THEO WORKSPACE hiện tại — mỗi dự án có Template riêng.
        var ws = _getCurrentWorkspace();
        if (ws is not null && ws.ProfilerTemplateName != template) { ws.ProfilerTemplateName = template; changed = true; }

        if (!changed) return;

        try
        {
            _settings.Save();
            SetStatus("Đã lưu cấu hình Profiler.");
        }
        catch (Exception ex)
        {
            SetStatus("Gõ đã nhận, nhưng lưu settings.json bị lỗi: " + ex.Message);
        }
    }

    private async Task LookupUidAsync(string name)
    {
        name = name.Trim();
        if (name.Length == 0) { SetStatus("Chưa nhập tên user cần tra (u_name)."); return; }
        if (_getCurrentWorkspace() is null) { SetStatus("Chưa chọn Workspace nào."); return; }

        try
        {
            // vsysuser nằm ở App Data (đã xác nhận qua chính công cụ Table của Bcode — DB "App
            // Data" đọc được vsysuser bình thường), KHÔNG phải Sys Data như giả định ban đầu.
            await using var conn = _connections.CreateConnection(useSysDatabase: false);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT u_id FROM vsysuser WHERE u_name = @name", conn);
            cmd.Parameters.AddWithValue("@name", name);
            var result = await cmd.ExecuteScalarAsync();

            if (result is null || result is DBNull)
            {
                SetUid("");
                SetStatus($"Không tìm thấy user \"{name}\" trong vsysuser (App Data).");
            }
            else
            {
                var uid = result.ToString() ?? "";
                SetUid(uid);
                SetStatus($"u_id của \"{name}\" là {uid} — copy dán vào bộ lọc ApplicationName trong Profiler.");

                var ws = _getCurrentWorkspace();
                if (ws is not null && ws.ProfilerTargetUser != name)
                {
                    ws.ProfilerTargetUser = name;
                    try { _settings.Save(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi tra u_id: " + ex.Message);
        }
    }

    private async Task RunProfilerAsync(string exePath, string loginUser, string loginPass, string template)
    {
        exePath = exePath.Trim();
        if (!File.Exists(exePath))
        {
            SetStatus("Không tìm thấy Profiler.exe ở đường dẫn đã khai — kiểm tra lại hoặc bấm \"…\" để chọn.");
            return;
        }

        var ws = _getCurrentWorkspace();
        if (ws is null)
        {
            SetStatus("Chưa chọn Workspace nào.");
            return;
        }

        if (_profilerProcess is { HasExited: false })
        {
            SetStatus("Profiler đang chạy rồi trong tab này — bấm \"✕ Đóng Profiler\" trước nếu muốn mở lại.");
            return;
        }

        SaveConfig(exePath, loginUser, loginPass, template);

        var traceFile = GetTraceFilePath(ws);
        try { Directory.CreateDirectory(GetTraceFolder(ws)); }
        catch { /* không tạo được thư mục cũng không chặn việc mở Profiler */ }

        // KHÔNG dùng "-f" ở đây — đã xác nhận thực tế "-f" chỉ dùng để MỞ 1 file trace ĐÃ CÓ,
        // không phải để chỉ định nơi ghi trace mới (dùng thì Profiler báo "Failed to open a
        // file" vì file chưa tồn tại lần đầu). Muốn tự lưu ra file, Bee tick "Save to file" tay
        // trong Trace Properties, trỏ đúng đường dẫn ở ô "File trace" trên thanh công cụ.
        //
        // KHÔNG dùng "-T" nữa — đã thử nhiều lần, kể cả sau khi đã Save As Trace Template tên
        // "kog" thật, "-T" vẫn khiến Profiler rơi vào lại đúng trạng thái mở file tĩnh cũ (y hệt
        // lỗi của "-f") thay vì bắt đầu trace sống mới — không đáng tin cậy với bản Profiler.exe
        // này. Ô "Trace Template" trên thanh công cụ giờ CHỈ còn là ghi chú nhắc Bee tự chọn tay
        // đúng tên đó trong dropdown "Use the template" ở khung Trace Properties sau khi bấm
        // "Bung & Đăng nhập" — không truyền qua dòng lệnh nữa.
        var args = new StringBuilder();
        args.Append($"-S \"{ws.Server}\"");
        if (loginUser.Trim().Length > 0) args.Append($" -U \"{loginUser.Trim()}\" -P \"{loginPass}\"");

        try
        {
            SetStatus("Đang mở Profiler.exe...");
            _profilerProcess = Process.Start(new ProcessStartInfo(exePath, args.ToString()) { UseShellExecute = true });

            if (_profilerProcess is null)
            {
                SetStatus("Không khởi động được Profiler.exe.");
                return;
            }

            // MainWindowHandle có thể chưa sẵn sàng ngay sau Start — dò tối đa ~10 giây.
            var hwnd = IntPtr.Zero;
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(200);
                _profilerProcess.Refresh();
                if (_profilerProcess.HasExited)
                {
                    SetStatus("Profiler.exe đã thoát ngay sau khi mở — kiểm tra lại đường dẫn hoặc User/Pass đăng nhập.");
                    _profilerProcess = null;
                    return;
                }
                hwnd = _profilerProcess.MainWindowHandle;
                if (hwnd != IntPtr.Zero) break;
            }

            if (hwnd == IntPtr.Zero)
            {
                SetStatus("Không lấy được cửa sổ Profiler để nhúng — Profiler vẫn đang chạy ở cửa sổ riêng bên ngoài tab này.");
                SetRunning(true);
                return;
            }

            EmbedWindow(hwnd);
            SetRunning(true);
            var templateHint = template.Trim().Length > 0
                ? $" Nhớ chọn Template \"{template.Trim()}\" ở dropdown \"Use the template\" trong Trace Properties."
                : "";
            SetStatus($"Đã nhúng Profiler vào tab này.{templateHint} Muốn tự lưu ra file, tick \"Save to file\" và trỏ vào \"{traceFile}\".");
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi mở Profiler: " + ex.Message);
        }
    }

    /// <summary>Mở lại 1 file trace .trc ĐÃ CÓ SẴN trong thư mục Trace của dự án để xem lại —
    /// dùng đúng tham số "-f" thật của Profiler.exe (xác nhận qua thực tế: -f dùng để MỞ file có
    /// sẵn, không phải nơi ghi trace mới). Không cần Server/User/Pass vì chỉ xem dữ liệu đã lưu,
    /// không kết nối trực tiếp.</summary>
    private async Task OpenSavedTraceAsync()
    {
        var ws = _getCurrentWorkspace();
        if (ws is null) { SetStatus("Chưa chọn Workspace nào."); return; }

        var exePath = _settings.SqlProfilerPath?.Trim() ?? "";
        if (!File.Exists(exePath))
        {
            SetStatus("Không tìm thấy Profiler.exe ở đường dẫn đã khai.");
            return;
        }

        var traceFile = GetTraceFilePath(ws);
        if (!File.Exists(traceFile))
        {
            SetStatus($"Chưa có file trace nào được lưu tại \"{traceFile}\". Trong Trace Properties, tick \"Save to file\" và trỏ đúng đường dẫn này khi chạy trace để lần sau mở lại được.");
            return;
        }

        if (_profilerProcess is { HasExited: false })
        {
            SetStatus("Đang có Profiler chạy trong tab này — bấm \"✕ Đóng Profiler\" trước khi mở trace đã lưu.");
            return;
        }

        try
        {
            SetStatus("Đang mở trace đã lưu...");
            _profilerProcess = Process.Start(new ProcessStartInfo(exePath, $"-f \"{traceFile}\"") { UseShellExecute = true });

            if (_profilerProcess is null)
            {
                SetStatus("Không khởi động được Profiler.exe.");
                return;
            }

            var hwnd = IntPtr.Zero;
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(200);
                _profilerProcess.Refresh();
                if (_profilerProcess.HasExited)
                {
                    SetStatus("Profiler.exe đã thoát ngay khi mở file trace — kiểm tra lại file có hỏng không.");
                    _profilerProcess = null;
                    return;
                }
                hwnd = _profilerProcess.MainWindowHandle;
                if (hwnd != IntPtr.Zero) break;
            }

            if (hwnd == IntPtr.Zero)
            {
                SetStatus("Không lấy được cửa sổ Profiler để nhúng — vẫn đang chạy ở cửa sổ riêng bên ngoài tab này.");
                SetRunning(true);
                return;
            }

            EmbedWindow(hwnd);
            SetRunning(true);
            SetStatus($"Đã mở trace đã lưu: \"{traceFile}\".");
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi mở trace đã lưu: " + ex.Message);
        }
    }

    private void CloseProfiler()
    {
        try
        {
            if (_profilerProcess is { HasExited: false })
            {
                _profilerProcess.CloseMainWindow();
                if (!_profilerProcess.WaitForExit(2000)) _profilerProcess.Kill();
            }
        }
        catch { /* tốt nhất có thể — process có thể đã tự thoát */ }
        finally
        {
            _profilerProcess = null;
            _profilerHwnd = IntPtr.Zero;
            SetRunning(false);
            SetStatus("Đã đóng Profiler.");
        }
    }

    // =========================================================================
    // Nhúng cửa sổ Win32 thật của Profiler.exe vào _hostPanel — kỹ thuật SetParent chuẩn.
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
        style &= ~WS_POPUP;
        style &= ~WS_CAPTION;
        style &= ~WS_THICKFRAME;
        style |= WS_CHILD;
        SetWindowLong(hwnd, GWL_STYLE, style);

        SetParent(hwnd, _hostPanel.Handle);
        ShowWindow(hwnd, SW_SHOW);
        ResizeEmbeddedWindow();
    }

    private void ResizeEmbeddedWindow()
    {
        if (_profilerHwnd == IntPtr.Zero) return;
        SetWindowPos(_profilerHwnd, IntPtr.Zero, 0, 0, _hostPanel.ClientSize.Width, _hostPanel.ClientSize.Height,
            SWP_NOZORDER | SWP_FRAMECHANGED);
    }
}