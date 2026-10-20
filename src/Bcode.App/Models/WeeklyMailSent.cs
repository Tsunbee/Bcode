namespace Bcode.App.Models;

/// <summary>1 thư Bcode đã gửi từ tool "Mail kế hoạch tuần" (thư theo mẫu, thư gửi lại hoặc thư trả lời). Bản đầy đủ nằm ở file .eml cùng thư mục
/// %AppData%\Bcode\weeklymail-sent\ (<see cref="File"/>); bản ghi này chỉ là mục lục để liệt kê + dò phản hồi theo <see cref="MessageId"/>.</summary>
public sealed class WeeklyMailSentEntry
{
    public string Id { get; set; } = "";
    /// <summary>Message-ID của thư (không có dấu &lt; &gt;) — phản hồi nhận về khớp qua In-Reply-To / References.</summary>
    public string MessageId { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
    public string Project { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Bcc { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>Tên file .eml (tương đối với thư mục weeklymail-sent).</summary>
    public string File { get; set; } = "";
    /// <summary>mail = gửi từ mẫu; resend = gửi lại thư cũ; reply = trả lời trong Bcode.</summary>
    public string Kind { get; set; } = "mail";
    /// <summary>Id thư gốc của chuỗi (thư gốc thì = <see cref="Id"/>): thư trả lời + phản hồi nhận về cùng chuỗi hiện chung 1 chỗ.</summary>
    public string ThreadId { get; set; } = "";
    public string InReplyTo { get; set; } = "";
    /// <summary>Đã chép lên thư mục Sent của hộp thư qua IMAP.</summary>
    public bool CopiedToServer { get; set; }
}

/// <summary>1 phản hồi nhận về (tải từ INBOX qua IMAP) của 1 thư đã gửi. File .eml ở weeklymail-sent\replies\.</summary>
public sealed class WeeklyMailReplyEntry
{
    public string Id { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string ThreadId { get; set; } = "";
    /// <summary>Id thư đã gửi mà phản hồi này trả lời.</summary>
    public string SentId { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public string Subject { get; set; } = "";
    public string File { get; set; } = "";
    /// <summary>true = khớp chắc chắn qua In-Reply-To / References; false = đoán theo tiêu đề "Re:/TL: + tiêu đề gốc".</summary>
    public bool ByHeader { get; set; } = true;
    /// <summary>Đã mở xem trong Bcode.</summary>
    public bool Seen { get; set; }
}

public sealed class WeeklyMailIndex
{
    public List<WeeklyMailSentEntry> Sent { get; set; } = new();
    public List<WeeklyMailReplyEntry> Replies { get; set; } = new();
    public DateTimeOffset? LastCheck { get; set; }
}

/// <summary>Cấu hình IMAP của tool Mail kế hoạch tuần (weeklymail-imap.json): đọc phản hồi ở INBOX + chép thư đã gửi lên thư mục Sent.
/// Tài khoản / mật khẩu dùng chung với SMTP. Host trống = cùng host SMTP.</summary>
public sealed class WeeklyMailImapSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 993;
    public bool Ssl { get; set; } = true;
    public bool CopyToSent { get; set; } = true;
    /// <summary>Nhớ mật khẩu (mã hoá DPAPI theo tài khoản Windows, chỉ máy này đọc được).</summary>
    public bool RememberPassword { get; set; }
    public string PasswordProtected { get; set; } = "";
}
