namespace Bcode.App.Services;

/// <summary>
/// KEY KHAI BÁO — đổi <see cref="Key"/> thành key của bạn rồi build lại. File này nằm trong source của Bcode, key được build vào chương trình
/// (không đọc gì từ workspace / server, không có file key nào để người dùng nhìn thấy).
/// <para>Người dùng dán MỘT LẦN ở Settings → "Key bản quyền" chuỗi mã hoá bcrypt (.NET, BCrypt.Net) của key này; khớp thì mở
/// "Create RPT &amp; XML" và "Excel → FRX" (xem <see cref="LicenseService"/>). Tạo chuỗi bcrypt gửi cho người dùng ở mục
/// "Dành cho người phát hành key" trong hộp thoại đó.</para>
/// <para>Cách khai: (1) <b>key gốc</b> (như dưới đây) — người dùng dán chuỗi bcrypt; hoặc (2) chính <b>chuỗi bcrypt</b> <c>$2a$11$…</c> — người dùng dán key gốc.
/// Cách (1) giữ bí mật hơn: người dùng chỉ có chuỗi bcrypt, không có key gốc. Lưu ý: chuỗi hằng trong file .exe vẫn có thể bị dịch ngược;
/// muốn kín hơn hãy dùng thêm công cụ làm rối mã (obfuscator) khi build bản phát hành.</para>
/// </summary>
internal static class LicenseSecret
{
    public const string Key = "BCODE-PhongchodienBeechodien";
}
