using System.Net;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Lấy nhanh cấu hình gửi mail của 1 dự án để điền sẵn vào tab Check Mail:
///   • Đọc <c>App_Data\Controllers\Options\Message.xml</c> của source dự án.
///   • Nếu trong đó có tham chiếu entity <c>&amp;EmailConfig;</c> thì cấu hình nằm ở nơi entity đó trỏ tới (thường là
///     <c>&lt;!ENTITY EmailConfig SYSTEM "..\Options\EmailConfig.xml"&gt;</c>, hoặc entity giá trị chứa luôn khối &lt;setting&gt;).
///   • Không có entity thì lấy khối &lt;setting&gt; ngay trong Message.xml.
/// Khối cấu hình có dạng: <c>&lt;setting&gt;&lt;host value="..." /&gt;&lt;port value="587" /&gt;&lt;userName value="..." /&gt;...&lt;/setting&gt;</c>.
/// Chỉ ĐỌC file; mật khẩu "???" (chưa khai báo) thì bỏ trống.
/// </summary>
public static class MailSettingsReader
{
    private static readonly Regex ValueRegex = new(
        @"<(?<k>isSend|aliasName|userName|password|host|port|isBodyHTML|enableSSL|clientTimeout)\s+value\s*=\s*""(?<v>[^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <returns>Cấu hình (null nếu không đọc được) và câu mô tả nguồn / lý do để hiện cho người dùng.</returns>
    public static (MailConfig? Config, string Note) Read(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return (null, "Project hiện tại chưa khai báo Source Path.");
        var optionsDir = Path.Combine(sourcePath.Trim(), "App_Data", "Controllers", "Options");
        var messagePath = Path.Combine(optionsDir, "Message.xml");
        string messageText;
        try
        {
            if (!File.Exists(messagePath)) return (null, $"Không thấy {messagePath}");
            messageText = File.ReadAllText(messagePath);
        }
        catch (Exception ex) { return (null, "Không đọc được Message.xml: " + ex.Message); }

        // Chỗ chứa khối <setting>: file mà entity EmailConfig trỏ tới, entity giá trị, hoặc chính Message.xml.
        string settingText = messageText, from = "Message.xml";
        if (Regex.IsMatch(messageText, @"&EmailConfig;", RegexOptions.IgnoreCase))
        {
            var sys = Regex.Match(messageText, @"<!ENTITY\s+EmailConfig\s+SYSTEM\s+""([^""]+)""", RegexOptions.IgnoreCase);
            if (sys.Success)
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(messagePath)!, sys.Groups[1].Value.Replace('/', '\\')));
                try
                {
                    if (!File.Exists(path)) return (null, $"Message.xml dùng entity EmailConfig nhưng không thấy file {path}");
                    settingText = File.ReadAllText(path);
                    from = Path.GetFileName(path) + " (entity EmailConfig của Message.xml)";
                }
                catch (Exception ex) { return (null, "Không đọc được file EmailConfig: " + ex.Message); }
            }
            else
            {
                var val = Regex.Match(messageText, @"<!ENTITY\s+EmailConfig\s+""([^""]*)""", RegexOptions.IgnoreCase);
                if (val.Success) { settingText = WebUtility.HtmlDecode(val.Groups[1].Value); from = "entity EmailConfig trong Message.xml"; }
            }
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = Regex.Match(settingText, @"<setting\b[\s\S]*?</setting>", RegexOptions.IgnoreCase);
        foreach (Match m in ValueRegex.Matches(block.Success ? block.Value : settingText))
            values.TryAdd(m.Groups["k"].Value, WebUtility.HtmlDecode(m.Groups["v"].Value));
        if (!values.ContainsKey("host") && !values.ContainsKey("userName"))
            return (null, $"Không thấy khối cấu hình <setting> trong {from}.");

        static bool B(string? s) => string.Equals(s?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        var cfg = new MailConfig
        {
            Host = values.GetValueOrDefault("host", "").Trim(),
            Port = int.TryParse(values.GetValueOrDefault("port"), out var port) && port is > 0 and < 65536 ? port : 587,
            Ssl = B(values.GetValueOrDefault("enableSSL")),
            Tls = false, // SSL tick: cổng 465 = SSL ngay khi kết nối, cổng khác = STARTTLS (xem MailConfig)
            SenderEmail = values.GetValueOrDefault("userName", "").Trim(),
            Account = values.GetValueOrDefault("userName", "").Trim(),
            Alias = values.GetValueOrDefault("aliasName", "").Trim(),
            Html = B(values.GetValueOrDefault("isBodyHTML")),
        };
        var pw = values.GetValueOrDefault("password", "").Trim();
        cfg.Password = pw is "" or "???" ? "" : pw;
        if (int.TryParse(values.GetValueOrDefault("clientTimeout"), out var ms) && ms > 0)
            cfg.TimeoutSeconds = Math.Clamp(ms / 1000, 5, 300);

        var pwNote = cfg.Password.Length == 0 ? " — mật khẩu chưa khai báo (\"???\"), nhập tay" : "";
        return (cfg, $"Đã lấy cấu hình từ {from}{pwNote}.");
    }
}
