using System.Text.Json;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>Lưu danh sách mẫu mail tuần ở %AppData%\Bcode\weeklymail.json. Cấu hình SMTP (host/port/SSL/tài khoản...) dùng CHUNG file với tab Check Mail
/// (checkmail.json, <see cref="MailConfig"/>) — khai ở đâu cũng được, mật khẩu không lưu.</summary>
public static class WeeklyMailStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static string Dir => Path.Combine(BcodePaths.AppData, "Bcode");
    private static string TemplatesPath => Path.Combine(Dir, "weeklymail.json");
    private static string SmtpPath => Path.Combine(Dir, "checkmail.json");

    public static List<WeeklyMailTemplate> LoadTemplates()
    {
        try { if (File.Exists(TemplatesPath)) return JsonSerializer.Deserialize<List<WeeklyMailTemplate>>(File.ReadAllText(TemplatesPath), JsonOpts) ?? new(); }
        catch { /* file hỏng → danh sách trống */ }
        return new();
    }

    public static void SaveTemplates(List<WeeklyMailTemplate> list)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(TemplatesPath, JsonSerializer.Serialize(list, JsonOpts));
    }

    /// <summary>Cấu hình SMTP đã lưu (checkmail.json), mật khẩu luôn trống.</summary>
    public static MailConfig LoadSmtp()
    {
        MailConfig cfg;
        try { cfg = File.Exists(SmtpPath) ? JsonSerializer.Deserialize<MailConfig>(File.ReadAllText(SmtpPath), JsonOpts) ?? new MailConfig() : new MailConfig(); }
        catch { cfg = new MailConfig(); }
        cfg.Password = "";
        return cfg;
    }

    /// <summary>Ghi lại các trường máy chủ/người gửi vào checkmail.json mà GIỮ NGUYÊN người nhận/tiêu đề/nội dung thử của tab Check Mail.</summary>
    public static void SaveSmtp(MailConfig c)
    {
        var cur = LoadSmtp();
        cur.Host = c.Host; cur.Port = c.Port; cur.Ssl = c.Ssl; cur.Tls = c.Tls; cur.IgnoreCert = c.IgnoreCert; cur.TimeoutSeconds = c.TimeoutSeconds;
        cur.SenderEmail = c.SenderEmail; cur.Alias = c.Alias; cur.Account = c.Account; cur.Password = "";
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SmtpPath, JsonSerializer.Serialize(cur, JsonOpts));
    }
}
