using Microsoft.Data.SqlClient;

namespace BcodeViewer.App.Host;

/// <summary>
/// Phương án dự phòng của F5 khi Bcode.App KHÔNG chạy (mở file bằng FCode): tự tra menu wcommand của file rồi bung
/// trình duyệt mặc định tới URL đó để người dùng đăng nhập và kiểm tra. Khác bản trong Bcode.App ở chỗ không tự điền
/// User/Pass được — trình duyệt chuẩn không cho nhúng script đăng nhập — nên chỉ mở trang.
///
/// Workspace lấy từ Bcode (settings.json) hoặc, nếu chưa có, từ Config.xml của FCode (xem Settings) — cùng nguồn với
/// "Chạy SQL". Tra menu cần mật khẩu SQL; thiếu/lỗi thì vẫn mở trang đăng nhập của project.
/// </summary>
internal static class MenuLauncher
{
    /// <returns>Câu thông báo cho người dùng.</returns>
    public static async Task<string> OpenInBrowserAsync(string filePath, string project, CancellationToken token)
    {
        var ws = WorkspaceConnection.LoadWorkspaceForProject(project);
        if (ws is null || string.IsNullOrWhiteSpace(ws.LoginWLink))
            return "Đã lưu. Bcode chưa chạy và chưa biết link web của project (khai Config.xml của FCode trong Settings).";

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

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url ?? ws.LoginWLink) { UseShellExecute = true });
        return (url is null ? "Đã lưu. Bcode chưa chạy — đã mở trình duyệt." + note : "Đã lưu. Bcode chưa chạy — đã mở menu trong trình duyệt: " + url);
    }

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
