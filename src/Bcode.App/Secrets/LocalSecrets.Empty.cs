// Bản RỖNG của Secrets/LocalSecrets.cs — được commit; csproj chỉ build file này khi máy KHÔNG có LocalSecrets.cs (file bí mật thật, gitignore).
// Bcode build từ bản này vẫn chạy bình thường, chỉ các tính năng cần bí mật báo thiếu: Decrypt SQL Object (tài khoản DAC mặc định),
// đồng bộ / yêu cầu FSG (kết nối FSG_A), Setup eInvoice (khoá RSA mẫu). Thêm bí mật mới: thêm hằng vào CẢ HAI file. Xem Secrets/README.md.
namespace Bcode.App.Secrets;

internal static class LocalSecrets
{
    /// <summary>true = bản build có bí mật thật (LocalSecrets.cs); false = bản rỗng này.</summary>
    public static bool Present => false;

    // Decrypt SQL Object — login sysadmin mặc định (DecryptSqlSecret) + khoá AES cũ của giá trị registry (DecryptSqlCredentialStore).
    public const string DecryptSqlUser = "";
    public const string DecryptSqlPassword = "";
    public const string DecryptSqlRegistryPassphrase = "";

    // FSG_A (FsgProjectLookupService): khoá AES + server/db/user/pass đã mã hoá; mật khẩu mặc định thay xpass của server nội bộ.
    public const string FsgKeyPassphrase = "";
    public const string FsgEncServer = "";
    public const string FsgEncDatabase = "";
    public const string FsgEncUser = "";
    public const string FsgEncPassword = "";
    public const string FsgDefaultPassLan = "";
    public const string FsgDefaultPassLtud1 = "";

    // Mật khẩu mặc định của login SQL Profiler (AppSettings.ProfilerLoginPassword khi settings.json chưa có).
    public const string ProfilerDefaultPassword = "";

    // Setup eInvoice (EInvoiceSetupService): khoá RSA / hash cố định của script mẫu.
    public const string EInvPassHddt = "";
    public const string EInvMkTkHddt = "";
    public const string EInvProxyPrivateKey = "";
    public const string EInvProxyPublicKey = "";
    public const string EInvClientPublicKey = "";
    public const string EInvClientPassword = "";
    public const string EInvAppPk = "";
    public const string EInvAppRk = "";
    public const string EInvAppPassword = "";
}
