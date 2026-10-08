using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace Bcode.App.Services;

/// <summary>Thông tin kết nối SQL dùng cho Decrypt SQL Object (DAC cần login sysadmin).</summary>
public sealed record DecryptSqlCredential(string Server, bool WindowsAuth, string User, string Password);

/// <summary>
/// Registry <c>HKCU\SOFTWARE\Bcode\DecryptSql</c> khai báo sẵn server / user / pass SQL cho tool Decrypt SQL Object — user và pass được mã hoá AES-GCM
/// với khoá nhúng cứng trong chương trình (cùng cách QLYC FSG giữ tài khoản, xem <see cref="FsgProjectLookupService"/>), nên không nằm dạng chữ thường trong registry.
/// Đây là che dấu chứ không phải bảo mật mạnh: khoá nằm trong exe. Tool ưu tiên đọc ở đây trước; không có mới lấy theo workspace đang chọn.
/// Lý do: giải mã object WITH ENCRYPTION cần kết nối DAC bằng login sysadmin — nếu không dùng database local thì phải dùng user admin mới giải mã được,
/// mà user của workspace thường không đủ quyền.
/// </summary>
public static class DecryptSqlCredentialStore
{
    private const string KeyPath = @"SOFTWARE\Bcode\DecryptSql";
    private const string Passphrase = "Bc0de-DecryptSql-AES-7Hq2Zr9LmXw4Vt8K";

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
        key.SetValue("User", Encrypt(c.User));
        key.SetValue("Pass", Encrypt(c.Password));
    }

    public static void Clear()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false); } catch { }
    }

    // Định dạng: base64( nonce[12] | tag[16] | cipher ) — giống FsgProjectLookupService.Decrypt.
    private static byte[] DerivedKey() => SHA256.HashData(Encoding.UTF8.GetBytes(Passphrase));

    private static string Encrypt(string plain)
    {
        var data = Encoding.UTF8.GetBytes(plain ?? "");
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[data.Length];
        using (var gcm = new AesGcm(DerivedKey(), 16)) gcm.Encrypt(nonce, data, cipher, tag);
        var blob = new byte[28 + cipher.Length];
        nonce.CopyTo(blob, 0); tag.CopyTo(blob, 12); cipher.CopyTo(blob, 28);
        return Convert.ToBase64String(blob);
    }

    private static string Decrypt(string? blob)
    {
        if (string.IsNullOrEmpty(blob)) return "";
        var raw = Convert.FromBase64String(blob);
        var plain = new byte[raw.Length - 28];
        using var gcm = new AesGcm(DerivedKey(), 16);
        gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
        return Encoding.UTF8.GetString(plain);
    }
}
