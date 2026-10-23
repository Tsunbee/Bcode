using Bcode.App.Secrets;

namespace Bcode.App.Services;

/// <summary>
/// Tài khoản SQL mặc định cho tool Decrypt SQL Object (DAC cần login sysadmin) — KHÔNG hiện ra giao diện, KHÔNG ghi ra file.
/// Giá trị nằm ở Secrets/LocalSecrets.cs (gitignore, mỗi máy build tự chép vào); bản build không có file đó thì rỗng → <see cref="Available"/> = false.
/// Tool chỉ dùng được khi máy đã có key bản quyền (<see cref="LicenseService"/>). Registry khai báo sẵn (<see cref="DecryptSqlCredentialStore"/>) nếu có thì được ưu tiên hơn.
/// Lưu ý: chuỗi hằng trong .exe vẫn có thể bị dịch ngược — hãy dùng thêm obfuscator khi phát hành.
/// </summary>
internal static class DecryptSqlSecret
{
    public static string User => LocalSecrets.DecryptSqlUser;
    public static string Password => LocalSecrets.DecryptSqlPassword;
    public static bool Available => User.Length > 0;
}
