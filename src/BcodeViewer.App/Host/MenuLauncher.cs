using Microsoft.Data.SqlClient;

namespace BcodeViewer.App.Host;

/// <summary>
/// F5 của BcodeViewer: tự tra menu wcommand của file rồi bung
/// trình duyệt tới URL đó để người dùng đăng nhập và kiểm tra. Khác bản trong Bcode.App ở chỗ không tự điền
/// User/Pass được — trình duyệt chuẩn không cho nhúng script đăng nhập — nên chỉ mở trang.
/// Settings → "Mở web (F5)": mở bằng trình duyệt mặc định / Chrome / Edge — F5 lần sau dùng lại đúng tab đã mở cho trang đó
/// (BrowserTabLauncher, tắt được = mỗi lần 1 tab mới như cũ) — hoặc gửi sang Bcode chạy trong tab FSG FBO như Ctrl+4 (SendToBcode).
///
/// Workspace lấy từ Bcode (settings.json) hoặc, nếu chưa có, từ Config.xml của FCode (xem Settings) — cùng nguồn với
/// "Chạy SQL". Tra menu cần mật khẩu SQL; thiếu/lỗi thì vẫn mở trang đăng nhập của project.
/// </summary>
internal static class MenuLauncher
{
    /// <param name="browser">"default" (trình duyệt mặc định), "chrome", "edge" hoặc "bcode" (gửi sang Bcode — <see cref="SendToBcode"/>).</param>
    /// <param name="reuseTab">true (mặc định) = dùng lại đúng tab đã mở cho trang này trong Chrome / Edge đang mở (BrowserTabLauncher);
    /// false = mỗi lần F5 bung 1 tab mới (cách cũ).</param>
    /// <returns>Câu thông báo cho người dùng.</returns>
    public static async Task<string> OpenInBrowserAsync(string filePath, string project, string browser, bool reuseTab, CancellationToken token)
    {
        if (browser == "bcode") return SendToBcode(filePath, project);

        var ws = WorkspaceConnection.LoadWorkspaceForProject(project);
        if (ws is null || string.IsNullOrWhiteSpace(ws.LoginWLink))
            return "Đã lưu. Chưa biết link web của project (khai Config.xml của FCode trong Settings).";

        var controller = Path.GetFileNameWithoutExtension(filePath);
        string? url = null;
        var note = "";
        try
        {
            var cs = WorkspaceConnection.BuildConnectionString(ws, sys: true);
            if (cs is null) throw new InvalidOperationException("thiếu thông tin kết nối Sys Data");

            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(token);
            await using var cmd = new SqlCommand(
                "SELECT TOP 1 link, parameter FROM dbo.wcommand " +
                "WHERE (sysid = @n OR link LIKE @l) AND link <> '' ORDER BY wmenu_id", conn);
            cmd.Parameters.AddWithValue("@n", controller);
            cmd.Parameters.AddWithValue("@l", controller + ".aspx%");
            await using var r = await cmd.ExecuteReaderAsync(token);
            if (await r.ReadAsync())
                url = BuildMenuUrl(ws.LoginWLink, r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1));
            else
                note = $" Không có menu nào gắn với \"{controller}\" — mở trang đăng nhập.";
        }
        catch (Exception ex)
        {
            note = " Không tra được menu (" + ex.Message + ") — mở trang đăng nhập.";
        }

        var target = url ?? ws.LoginWLink.Trim();
        if ((browser is "chrome" or "edge") && BrowserTabLauncher.FindExe(browser) is null)
            note += $" Máy không có {(browser == "edge" ? "Edge" : "Chrome")} — mở bằng trình duyệt mặc định.";
        if (reuseTab)
        {
            try
            {
                var done = await BrowserTabLauncher.OpenAsync(target, browser, token);
                return "Đã lưu — " + done + (url is null ? "." + note : ": " + url + note);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* lỗi UI Automation xảy ra trước khi mở gì — mở như cũ bên dưới */ }
        }

        BrowserTabLauncher.Launch(target, browser);
        return (url is null ? "Đã lưu — đã mở trình duyệt." + note : "Đã lưu — đã mở menu trong trình duyệt: " + url + note);
    }

    /// <summary>F5 khi chọn "Bcode — FSG FBO (như Ctrl+4)": gửi "fsg|&lt;file&gt;|&lt;dự án&gt;" sang Bcode qua pipe
    /// Bcode.Control.&lt;session&gt; (Bcode.App/Services/ViewerControlServer.cs). Bcode tự chọn workspace, tra menu wcommand
    /// của file rồi chạy y như Ctrl+4 trên cây menu (MainForm.RunMenuForFileAsync → RunMenuInFboAsync).</summary>
    public static string SendToBcode(string filePath, string project)
    {
        var pipeName = $"Bcode.Control.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        try
        {
            AllowSetForegroundWindow(-1); // ASFW_ANY — để Bcode được đưa cửa sổ của nó lên trước
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.Out);
            client.Connect(1500);
            // Đường dẫn Windows / tên dự án không chứa "|" nên gửi thẳng (giữ đúng định dạng Bcode vẫn đọc).
            var bytes = System.Text.Encoding.UTF8.GetBytes($"fsg|{filePath}|{project}\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return "Đã lưu — đã gửi sang Bcode (FSG FBO, như Ctrl+4).";
        }
        catch (TimeoutException) { return "Đã lưu. Bcode chưa mở — mở Bcode rồi bấm F5 lại (hoặc đổi cách mở ở Settings → Mở web (F5))."; }
        catch (Exception ex) { return "Đã lưu. Không gửi được sang Bcode: " + ex.Message; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>Cùng cách ghép với Bcode.App (MainForm.BuildMenuUrl): gốc = thư mục Main của site.</summary>
    private static string? BuildMenuUrl(string loginLink, string link, string parameter)
    {
        link = link.Trim().Replace('\\', '/').TrimStart('/');
        if (link.Length == 0) return null;
        if (link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return link;
        if (!Uri.TryCreate(loginLink.Trim(), UriKind.Absolute, out var u)) return null;

        var p = u.AbsolutePath;
        var i = p.IndexOf("/Main/", StringComparison.OrdinalIgnoreCase);
        var root = i >= 0 ? p[..(i + 6)]
            : (p.EndsWith('/') ? p : p[..(p.LastIndexOf('/') + 1)]) + "Main/";

        var url = u.GetLeftPart(UriPartial.Authority) + root + link;
        var prm = parameter.Trim().TrimStart('?', '&');
        if (prm.Length > 0) url += (url.Contains('?') ? "&" : "?") + prm;
        return url;
    }
}
