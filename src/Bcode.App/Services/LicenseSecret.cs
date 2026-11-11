namespace Bcode.App.Services;

/// <summary>
/// KEY GỐC — đổi <see cref="Key"/> thành chuỗi bí mật của bạn rồi build lại cả Bcode và Bcode.KeyGen (công cụ cấp mã link file này, nên hai bên luôn cùng key).
/// Key được build vào chương trình (không đọc gì từ workspace / server, không có file key nào để người dùng nhìn thấy).
/// <para>Người dùng KHÔNG dán key gốc. Họ gửi "Mã máy" (<c>TÊN_MÁY|IP</c>, hiện ở Settings → Key bản quyền) cho bạn; bạn dùng Bcode.KeyGen sinh chuỗi bcrypt
/// riêng cho máy đó, họ dán lại (xem <see cref="LicenseService"/>). Mã của máy này không dùng được cho máy khác.</para>
/// <para>Lưu ý: chuỗi hằng trong file .exe vẫn có thể bị dịch ngược; muốn kín hơn hãy dùng thêm công cụ làm rối mã (obfuscator) khi build bản phát hành,
/// và KHÔNG phát hành Bcode.KeyGen cho người dùng.</para>
/// </summary>
internal static class LicenseSecret
{
    public static string Key => Bcode.App.Secrets.LocalSecrets.LicenseKey;
    /// <summary>Cách ghép key Decrypt SQL Object (Secrets/LocalSecrets.cs, gitignore) — xem <c>LicenseCodec.Kind.Decrypt</c>.</summary>
    public static string DecryptTemplate => Bcode.App.Secrets.LocalSecrets.DecryptKeyTemplate;   // key gốc nằm ở Secrets/LocalSecrets.cs (gitignore) — không để trong source
    /// <summary>Cách ghép key "Dịch file .f" (Secrets/LocalSecrets.cs, gitignore) — xem <c>LicenseCodec.Kind.DecryptF</c>.</summary>
    public static string DecryptFTemplate => Bcode.App.Secrets.LocalSecrets.DecryptFKeyTemplate;
    /// <summary>hashKey (391 ký tự) giải khối &lt;Encrypted&gt; của file .f (Secrets/LocalSecrets.cs, gitignore). Rỗng = bản build không giải được.</summary>
    public static string FHashKey => Bcode.App.Secrets.LocalSecrets.FHashKey;
}
