using System;
using System.Diagnostics;
using System.IO;
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

        // 1. Tự động sinh file Template
        EnsureProfilerTemplateExists(template);

        // 2. Tra cứu u_id để tự động hóa gõ phím
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

        var args = new StringBuilder();
        args.Append($"-S \"{ws.Server}\"");
        if (loginUser.Trim().Length > 0) args.Append($" -U \"{loginUser.Trim()}\" -P \"{loginPass}\"");

        try
        {
            SetStatus("Đang mở Profiler.exe...");
            _profilerProcess = Process.Start(new ProcessStartInfo(exePath, args.ToString()) { UseShellExecute = true });
            if (_profilerProcess is null) { SetStatus("Không khởi động được Profiler.exe."); return; }

            var hwnd = IntPtr.Zero;
            for (var i = 0; i < 50; i++)
            {
                await Task.Delay(200);
                _profilerProcess.Refresh();
                if (_profilerProcess.HasExited)
                {
                    SetStatus("Profiler.exe đã thoát ngay sau khi mở — kiểm tra lại đường dẫn hoặc User/Pass đăng nhập.");
                    _profilerProcess = null; return;
                }
                hwnd = _profilerProcess.MainWindowHandle;
                if (hwnd != IntPtr.Zero) break;
            }
            if (hwnd == IntPtr.Zero) { SetStatus("Không lấy được cửa sổ Profiler để nhúng."); SetRunning(true); return; }

            EmbedWindow(hwnd);
            SetRunning(true);

            // 3. Chạy Win32 Automation (Gõ phím chọn template & nhập bộ lọc)
            await AutomateProfilerUI(template, ws.ProjectId, autoUid);
        }
        catch (Exception ex) { SetStatus("Lỗi mở Profiler: " + ex.Message); }
    }

    // =========================================================================
    // TỰ ĐỘNG GÕ PHÍM SETUP TEMPLATE VÀ BỘ LỌC PROFILER
    // =========================================================================
    private async Task AutomateProfilerUI(string templateName, string projectId, string uid)
    {
        SetStatus("Đang tự động setup Template và Filter. Vui lòng không chạm chuột/bàn phím...");
        IntPtr hWnd = IntPtr.Zero;
        
        // Cửa sổ Trace Properties bật ra dạng Modal Dialog
        for (int i = 0; i < 20; i++)
        {
            hWnd = FindWindow(null, "Trace Properties");
            if (hWnd != IntPtr.Zero) break;
            await Task.Delay(500);
        }
        if (hWnd == IntPtr.Zero) { SetStatus("Không tìm thấy cửa sổ Trace Properties để tự setup."); return; }

        SetForegroundWindow(hWnd);
        await Task.Delay(500);

        try
        {
            string safeDb = string.IsNullOrEmpty(projectId) ? "" : $"{{%}}{projectId}{{%}}";
            string safeUid = string.IsNullOrEmpty(uid) ? "" : $"{{%}}{uid}{{%}}";

            // Chọn Template 
            if (!string.IsNullOrWhiteSpace(templateName))
            {
                SendKeys.SendWait("%u"); // Alt + U: Nhảy tới ô "Use the template"
                await Task.Delay(200);
                SendKeys.SendWait(templateName); // Tự gõ tên template (Bcode_Fast)
                await Task.Delay(300);
            }

            // Mở Filter
            SendKeys.SendWait("^{TAB}"); // Ctrl+Tab: Sang tab Events Selection
            await Task.Delay(300);
            SendKeys.SendWait("%f");     // Alt+F: Mở Column Filters
            await Task.Delay(600);

            // Gõ Filter DatabaseName
            SendKeys.SendWait("D");
            await Task.Delay(150);
            SendKeys.SendWait("{TAB}");
            await Task.Delay(150);
            SendKeys.SendWait(safeDb);
            await Task.Delay(150);

            // Gõ Filter TextData
            SendKeys.SendWait("+{TAB}");
            await Task.Delay(150);
            SendKeys.SendWait("T");
            await Task.Delay(150);
            SendKeys.SendWait("{TAB}");
            await Task.Delay(150);
            SendKeys.SendWait(safeUid);
            await Task.Delay(150);

            // OK & Run
            SendKeys.SendWait("{ENTER}");
            await Task.Delay(400);
            SendKeys.SendWait("{ENTER}");

            SetStatus($"Đã tự động nạp Template '{templateName}' và chạy Trace.");
        }
        catch (Exception ex) { SetStatus("Lỗi Auto-type: " + ex.Message); }
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