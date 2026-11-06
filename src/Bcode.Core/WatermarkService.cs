using System.Security.Cryptography;
using System.Text;

namespace Bcode.Core;

/// <summary>
/// Thuỷ ấn TRUY NGUỒN (không phải DRM) — xem tools/bcode-mark. Ở LÕI dùng chung nên cả Bcode lẫn BcodeViewer đều mang CÙNG một dấu bí mật.
/// Mỗi app cấp "muối" (key gốc bản quyền) qua <see cref="SaltProvider"/> lúc khởi động; app không có key thì dấu bí mật rỗng (không nhúng gì).
///
/// Hai phần: dấu tĩnh <see cref="StaticMarker"/> (GUID công khai, literal trong exe — nhận ra bản sao chép nhị phân) và dấu bí mật
/// <see cref="Fingerprint"/> = HMAC-SHA256(muối, nhãn) rút gọn (một chiều, công bố/so khớp được mà không lộ key), nhúng lúc chạy vào DOM mọi
/// trang (<see cref="StampScript"/>) và vào log khởi động (<see cref="LogLine"/>).
/// </summary>
public static class WatermarkService
{
    /// <summary>GUID cố định của bộ Bcode — không bí mật; có mặt trong mọi bản build để nhận diện cold-binary.</summary>
    public const string ProjectGuid = "b3c0de00-0000-4bee-b3c0-deb3c0deb3c0";
    public const string TokenPrefix = "BCWM1:";

    /// <summary>Dấu tĩnh (literal trong exe): bản quyền + GUID. Giữ nguyên văn để dò byte tìm thấy.</summary>
    public const string StaticMarker = "​[Bcode© Bee " + ProjectGuid + " | BcodeMark v1]​";

    /// <summary>Mỗi app đặt lúc khởi động để cấp "muối" (key gốc): Bcode.App dùng LicenseSecret.Key, BcodeViewer dùng LocalSecrets.LicenseKey.
    /// Chưa đặt / rỗng = bản không có dấu bí mật.</summary>
    public static Func<string?> SaltProvider = () => null;

    private static string? _fp;

    /// <summary>Dấu bí mật ổn định theo muối (12 hex); rỗng nếu không có muối. Nhớ kết quả lần đầu.</summary>
    public static string Fingerprint => _fp ??= Derive(SaltProvider());

    /// <summary>HMAC-SHA256(salt, nhãn) rút 12 hex; "" nếu salt rỗng. Một chiều → công bố được.</summary>
    public static string Derive(string? salt)
    {
        var s = (salt ?? "").Trim();
        if (s.Length == 0) return "";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(s), Encoding.UTF8.GetBytes("Bcode.Watermark.v1"));
        return Convert.ToHexString(mac)[..12].ToLowerInvariant();
    }

    /// <summary>Token nhúng vào trang / log: "BCWM1:&lt;dấu&gt;" (rỗng nếu không có dấu bí mật).</summary>
    public static string Token => Fingerprint.Length == 0 ? "" : TokenPrefix + Fingerprint;

    /// <summary>Dòng ghi vào log khởi động — có cả dấu tĩnh (literal) lẫn dấu bí mật (nếu có), để log chia sẻ truy được nguồn.</summary>
    public static string LogLine => $"BcodeMark {StaticMarker}" + (Token.Length > 0 ? " " + Token : " (bản không key)");

    /// <summary>Script nhúng dấu vô hình vào DOM mọi trang (thẻ meta ẩn + comment). Rỗng nếu bản không có dấu bí mật — không nhúng gì.</summary>
    public static string StampScript => Token.Length == 0 ? "" : $@"
(function() {{
  try {{
    var v = {System.Text.Json.JsonSerializer.Serialize(Token)};
    if (document.querySelector('meta[name=""bcwm""]')) return;
    var m = document.createElement('meta'); m.name = 'bcwm'; m.content = v;
    (document.head || document.documentElement).appendChild(m);
    document.documentElement.setAttribute('data-bcwm', v);
    document.documentElement.insertBefore(document.createComment(' ' + v + ' '), document.documentElement.firstChild);
  }} catch (e) {{}}
}})();";

    /// <summary>Dò mọi dấu "BCWM1:xxxxxxxxxxxx" trong một đoạn text (html / log).</summary>
    public static IEnumerable<string> Extract(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text ?? "", TokenPrefix + "([0-9a-f]{12})")
            .Select(m => m.Groups[1].Value).Distinct();

    /// <summary>Dò dấu trong byte của một file bất kỳ (exe/dll/html/log): tìm cả token bí mật lẫn GUID tĩnh (kể cả chuỗi UTF-16 xen byte 0 trong .NET).</summary>
    public static (List<string> Fingerprints, bool HasProjectGuid) Scan(byte[] bytes)
    {
        var raw = Encoding.Latin1.GetString(bytes);
        var stripped = raw.Replace("\u0000", "");
        var fps = Extract(raw).Concat(Extract(stripped)).Distinct().ToList();
        var hasGuid = raw.Contains(ProjectGuid, StringComparison.OrdinalIgnoreCase) || stripped.Contains(ProjectGuid, StringComparison.OrdinalIgnoreCase);
        return (fps, hasGuid);
    }
}
