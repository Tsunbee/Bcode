namespace Bcode.App.Models;

/// <summary>1 mẫu mail kế hoạch/báo cáo tuần của 1 dự án (tool "Mail kế hoạch tuần", menu Triển khai). Mỗi dự án có thể khai nhiều mẫu; người nhận To/Cc/Bcc khai như mail thường
/// (nhiều địa chỉ cách nhau bằng ; hoặc ,). Tiêu đề/nội dung có thể chứa {du_an} {tuan_truoc_tu} {tuan_truoc_den} {tuan_sau_tu} {tuan_sau_den} {ngay} — trang thay bằng giá trị thật khi xem trước/gửi.</summary>
public sealed class WeeklyMailTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    /// <summary>Tên dự án (thường trùng tên Workspace; gõ tự do được).</summary>
    public string Project { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Bcc { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>Nội dung thư (chữ ký riêng ở <see cref="Signature"/>, hoặc viết luôn ở cuối nội dung).</summary>
    public string Body { get; set; } = "";
    public bool Html { get; set; }
    /// <summary>Chữ ký dạng HTML (dán thẳng từ Outlook/Gmail hoặc mã HTML) — nối vào cuối nội dung khi gửi; có chữ ký thì thư gửi dạng HTML.</summary>
    public string Signature { get; set; } = "";
}
