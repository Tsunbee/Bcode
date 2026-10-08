namespace Bcode.App.Services;

/// <summary>
/// Tài khoản SQL mặc định cho tool Decrypt SQL Object (DAC cần login sysadmin) — nhúng cứng trong source, KHÔNG hiện ra giao diện, KHÔNG ghi ra file.
/// Tool chỉ dùng được khi máy đã có key bản quyền (<see cref="LicenseService"/>). Registry khai báo sẵn (<see cref="DecryptSqlCredentialStore"/>) nếu có thì được ưu tiên hơn.
/// Lưu ý: chuỗi hằng trong .exe vẫn có thể bị dịch ngược — hãy dùng thêm obfuscator khi phát hành.
/// </summary>
internal static class DecryptSqlSecret
{
    public const string User = "fastad";
    public const string Password = "F@st#970611@#$$Ql";
}
