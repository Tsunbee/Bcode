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
    public const string Key = "BCODE-PhongchodienBeechodien";
}
