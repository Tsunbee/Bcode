namespace Bcode.App;

/// <summary>
/// Gốc thư mục dữ liệu của Bcode (settings.json, snippets, ghi chú, lịch sử...) — thay cho gọi thẳng
/// <c>Environment.GetFolderPath(ApplicationData)</c>, vì trên 1 số máy (profile bị chuyển hướng/hạn chế, tài khoản đặc biệt) thư mục
/// %AppData% (Roaming) trống hoặc không tạo/ghi được; khi đó dữ liệu rơi vào thư mục hiện hành hoặc báo lỗi quyền.
/// Thử lần lượt: Roaming → Local → UserProfile\AppData\Roaming → thư mục chứa file .exe; lấy nơi đầu tiên tạo được thư mục "Bcode".
/// Kết quả nhớ lại cho cả tiến trình. LƯU Ý: Bcode.App và BcodeViewer.App mỗi bên có 1 bản y hệt logic này (2 project không tham
/// chiếu nhau) và PHẢI cho ra cùng 1 gốc vì BcodeViewer đọc settings.json của Bcode — sửa 1 bên thì sửa cả bên kia (BcodeViewer.App/BcodePaths.cs).
/// </summary>
internal static class BcodePaths
{
    private static string? _root;

    /// <summary>Dùng như `Environment.GetFolderPath(ApplicationData)`: các nơi gọi vẫn nối thêm "Bcode", ...</summary>
    public static string AppData => _root ??= Resolve();

    private static string Resolve()
    {
        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            try
            {
                var probe = Path.Combine(candidate, "Bcode");
                Directory.CreateDirectory(probe);
                // Tạo được chưa chắc ghi được (thư mục chỉ-đọc): thử ghi 1 file tạm.
                var test = Path.Combine(probe, ".write-test");
                File.WriteAllText(test, "ok");
                File.Delete(test);
                return candidate;
            }
            catch { /* nơi này không dùng được — thử nơi kế tiếp */ }
        }
        return AppContext.BaseDirectory;
    }

    private static IEnumerable<string> Candidates()
    {
        // BCODE_DATA_DIR: thư mục dữ liệu riêng (chạy thử / đo mà không đụng settings.json + hồ sơ WebView2 của bản đang dùng).
        var over = Environment.GetEnvironmentVariable("BCODE_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(over)) yield return over;
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile)) yield return Path.Combine(profile, "AppData", "Roaming");
    }
}
