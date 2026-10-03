using System.Net.Sockets;
using System.Security.Authentication;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Bcode.App.Services;

/// <summary>Cấu hình 1 lần "Check Mail" (do trang checkmail.html gửi lên).</summary>
public sealed class MailConfig
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    /// <summary>Mã hoá đường truyền (SSL/TLS): cổng 465 = SSL ngay khi kết nối, cổng khác = STARTTLS. Không tick = không mã hoá.</summary>
    public bool Ssl { get; set; }
    public bool Tls { get; set; }
    /// <summary>Bỏ qua lỗi chứng chỉ của máy chủ (chứng chỉ tự ký, sai tên miền...) — chỉ nên bật khi test.</summary>
    public bool IgnoreCert { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public bool VerboseLog { get; set; }
    public string SenderEmail { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Account { get; set; } = "";
    public string Password { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Bcc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Html { get; set; }
}

/// <summary>
/// Kiểm tra cấu hình SMTP và gửi thử email (tính năng "Check Mail" giống FCode): kết nối host:port, mã hoá SSL/TLS, đăng nhập, rồi gửi.
/// Dùng MailKit (SmtpClient của .NET không hỗ trợ SSL ngay-khi-kết-nối ở cổng 465 và đã bị đánh dấu lỗi thời). Mật khẩu không bao giờ vào log
/// (ProtocolLogger của MailKit che các dòng AUTH).
/// </summary>
public static class MailTestService
{
    /// <param name="sendMail">true = kết nối + đăng nhập + gửi mail; false = chỉ kiểm tra kết nối + đăng nhập.</param>
    /// <param name="log">Nhận từng dòng tiến trình: (nội dung, loại: info / ok / err).</param>
    /// <returns>true nếu mọi bước thành công.</returns>
    public static async Task<bool> RunAsync(MailConfig c, bool sendMail, Action<string, string> log, CancellationToken ct)
    {
        var problem = Validate(c, sendMail);
        if (problem is not null) { log(problem, "err"); return false; }

        using var protocolLog = new MemoryStream();
        using var client = c.VerboseLog ? new SmtpClient(new ProtocolLogger(protocolLog)) : new SmtpClient();
        client.Timeout = Math.Clamp(c.TimeoutSeconds, 5, 300) * 1000;
        if (c.IgnoreCert) client.ServerCertificateValidationCallback = (_, _, _, _) => true;

        var secure = c.Ssl || c.Tls;
        var options = !secure ? SecureSocketOptions.None : c.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        var started = DateTime.Now;
        try
        {
            log($"Kết nối tới {c.Host}:{c.Port} ({Describe(options)})...", "info");
            await client.ConnectAsync(c.Host.Trim(), c.Port, options, ct);
            log($"Đã kết nối. Đường truyền {(client.IsSecure ? "ĐÃ mã hoá" : "KHÔNG mã hoá")}." +
                (client.AuthenticationMechanisms.Count > 0 ? $" Kiểu đăng nhập máy chủ hỗ trợ: {string.Join(", ", client.AuthenticationMechanisms)}." : ""), "ok");

            if (!string.IsNullOrWhiteSpace(c.Account))
            {
                log($"Đăng nhập bằng tài khoản {c.Account.Trim()}...", "info");
                await client.AuthenticateAsync(c.Account.Trim(), c.Password, ct);
                log("Đăng nhập thành công.", "ok");
            }
            else log("Không khai báo tài khoản đăng nhập — bỏ qua bước xác thực.", "info");

            if (sendMail)
            {
                var message = Build(c);
                log($"Đang gửi tới {string.Join(", ", message.To.Concat(message.Cc).Concat(message.Bcc).Select(a => a.ToString()))}...", "info");
                var response = await client.SendAsync(message, ct);
                log($"Gửi thành công. Phản hồi của máy chủ: {response}", "ok");
            }
            else log("Kết nối và đăng nhập đều ổn (chưa gửi thư).", "ok");

            await client.DisconnectAsync(true, CancellationToken.None);
            log($"Hoàn tất sau {(DateTime.Now - started).TotalSeconds:0.0} giây.", "info");
            return true;
        }
        catch (OperationCanceledException) { log("Đã huỷ.", "err"); return false; }
        catch (MailKit.Security.AuthenticationException ex)
        {
            log($"Lỗi xác thực: {ex.Message}. Kiểm tra lại tài khoản / mật khẩu (Gmail, Outlook... thường cần \"mật khẩu ứng dụng\" thay vì mật khẩu thường).", "err");
            return false;
        }
        catch (SslHandshakeException ex)
        {
            log($"Lỗi bắt tay SSL/TLS: {ex.Message}. Thử đổi tuỳ chọn SSL/TLS cho khớp cổng (465 = SSL, 587 = STARTTLS), hoặc tick \"Bỏ qua lỗi chứng chỉ\" nếu máy chủ dùng chứng chỉ tự ký.", "err");
            return false;
        }
        catch (SmtpCommandException ex)
        {
            log($"Máy chủ từ chối lệnh ({(int)ex.StatusCode} {ex.ErrorCode}): {ex.Message}", "err");
            return false;
        }
        catch (SmtpProtocolException ex)
        {
            log($"Lỗi giao thức SMTP: {ex.Message}. Thường do sai cổng hoặc sai kiểu SSL/TLS (vd bật SSL ở cổng 25/587 hoặc ngược lại).", "err");
            return false;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or IOException or ServiceNotConnectedException)
        {
            log($"Không kết nối được {c.Host}:{c.Port}: {ex.Message.TrimEnd('.', ' ')}. Kiểm tra host, cổng, tường lửa / mạng.", "err");
            return false;
        }
        catch (Exception ex)
        {
            log($"Lỗi: {ex.Message}", "err");
            return false;
        }
        finally
        {
            if (c.VerboseLog && protocolLog.Length > 0)
            {
                log("--- Log SMTP chi tiết (C: = Bcode gửi, S: = máy chủ trả lời) ---", "info");
                foreach (var line in System.Text.Encoding.UTF8.GetString(protocolLog.ToArray()).Split('\n'))
                    if (!string.IsNullOrWhiteSpace(line)) log(line.TrimEnd('\r'), "dim");
            }
        }
    }

    private static string Describe(SecureSocketOptions o) => o switch
    {
        SecureSocketOptions.SslOnConnect => "SSL ngay khi kết nối",
        SecureSocketOptions.StartTls => "STARTTLS",
        _ => "không mã hoá",
    };

    private static string? Validate(MailConfig c, bool sendMail)
    {
        if (string.IsNullOrWhiteSpace(c.Host)) return "Chưa nhập Host / máy chủ.";
        if (c.Port is < 1 or > 65535) return "Port / cổng phải từ 1 đến 65535.";
        if (!sendMail) return null;
        if (!IsEmail(c.SenderEmail)) return "Email người gửi chưa đúng (cần dạng ten@ten-mien.com).";
        if (SplitAddresses(c.To).Count == 0) return "Chưa nhập email người nhận.";
        foreach (var a in SplitAddresses(c.To).Concat(SplitAddresses(c.Cc)).Concat(SplitAddresses(c.Bcc)))
            if (!IsEmail(a)) return $"Địa chỉ email không hợp lệ: {a}";
        return null;
    }

    /// <summary>MailKit chấp nhận cả "abc" (không có @) là hợp lệ; ở đây bắt buộc có phần tên miền.</summary>
    private static bool IsEmail(string? text) =>
        MailboxAddress.TryParse((text ?? "").Trim(), out var a) && a is MailboxAddress m && m.Address.Contains('@') && !m.Address.EndsWith('@') && m.Address.Split('@')[1].Contains('.');

    private static List<string> SplitAddresses(string text) =>
        (text ?? "").Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    private static MimeMessage Build(MailConfig c)
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress(string.IsNullOrWhiteSpace(c.Alias) ? "" : c.Alias.Trim(), c.SenderEmail.Trim()));
        foreach (var a in SplitAddresses(c.To)) m.To.Add(MailboxAddress.Parse(a));
        foreach (var a in SplitAddresses(c.Cc)) m.Cc.Add(MailboxAddress.Parse(a));
        foreach (var a in SplitAddresses(c.Bcc)) m.Bcc.Add(MailboxAddress.Parse(a));
        m.Subject = c.Subject ?? "";
        m.Body = new TextPart(c.Html ? "html" : "plain") { Text = c.Body ?? "" };
        return m;
    }
}
