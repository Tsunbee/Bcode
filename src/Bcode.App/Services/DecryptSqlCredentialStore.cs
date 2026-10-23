using System.Security.Cryptography;
using System.Text;
using Bcode.App.Secrets;
using Bcode.Shared;
using Microsoft.Win32;

namespace Bcode.App.Services;

/// <summary>Thông tin kết nối SQL dùng cho Decrypt SQL Object (DAC cần login sysadmin).</summary>
public sealed record DecryptSqlCredential(string Server, bool WindowsAuth, string User, string Password);

/// <summary>
/// Registry <c>HKCU\SOFTWARE\Bcode\DecryptSql</c> khai báo sẵn server / user / pass SQL cho tool Decrypt SQL Object — user và pass mã hoá DPAPI
/// (<see cref="DpapiText"/>, chỉ đúng tài khoản Windows trên máy này giải được). Giá trị ghi bởi bản cũ (AES-GCM, khoá ở Secrets/LocalSecrets.cs) vẫn
/// đọc được, lần Save sau thành DPAPI. Tool ưu tiên đọc ở đây trước; không có mới lấy theo workspace đang chọn.
/// Lý do: giải mã object WITH ENCRYPTION cần kết nối DAC bằng login sysadmin — nếu không dùng database local thì phải dùng user admin mới giải mã được,
/// mà user của workspace thường không đủ quyền.
/// </summary>
public static class DecryptSqlCredentialStore
{
    private const string KeyPath = @"SOFTWARE\Bcode\DecryptSql";
    private static string Passphrase => LocalSecrets.DecryptSqlRegistryPassphrase;

    /// <summary>Thông tin đã khai báo trong registry; null nếu chưa có (hoặc không giải mã được).</summary>
    public static DecryptSqlCredential? Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return null;
            var server = key.GetValue("Server") as string ?? "";
            if (string.IsNullOrWhiteSpace(server)) return null;
            var windows = (key.GetValue("Windows") as string) == "1";
            var user = Decrypt(key.GetValue("User") as string);
            var pass = Decrypt(key.GetValue("Pass") as string);
            return new DecryptSqlCredential(server.Trim(), windows, user, pass);
        }
        catch { return null; }
    }

    public static void Save(DecryptSqlCredential c)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue("Server", c.Server.Trim());
        key.SetValue("Windows", c.WindowsAuth ? "1" : "0");
        key.SetValue("User", DpapiText.Protect(c.User));
        key.SetValue("Pass", DpapiText.Protect(c.Password));
    }

    public static void Clear()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); } catch { }
    }

    // Định dạng cũ (chỉ còn đọc): base64( nonce[12] | tag[16] | cipher ) — giống FsgProjectLookupService.Decrypt.
    private static byte[] DerivedKey() => SHA256.HashData(Encoding.UTF8.GetBytes(Passphrase));

    private static string Decrypt(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return "";
        if (DpapiText.IsProtected(blob))
        {
            var text = DpapiText.Unprotect(blob, out var failed);
            if (failed) throw new InvalidOperationException("Giá trị registry được mã hoá bằng tài khoản Windows / máy khác.");
            return text;
        }
        var raw = Convert.FromBase64String(blob);
        var plain = new byte[raw.Length - 28];
        using var gcm = new AesGcm(DerivedKey(), 16);
        gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
