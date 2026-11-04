using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Phần "đổi tên bên trong file" của Copy source standard (CopySourceStandardForm). File.Copy thuần để lại bản mới vẫn trỏ về
/// tên cũ: Main\x.aspx còn Controller="Cũ" + v/e tiêu đề cũ, Grid còn &lt;title&gt; cũ và Controller: 'Cũ' trong hyperlink,
/// Report còn reportFile/templateFile="Cũ_01"... Ở đây mỗi file được clone lập bảng tên cũ → tên mới (tên file bỏ đuôi, vd
/// Grid\rptA.xml → rptB.xml cho rptA → rptB) rồi CHỈ thay trong những chỗ chắc chắn là tham chiếu, để không đụng chuỗi tình cờ trùng tên:
/// <list type="bullet">
/// <item>chuỗi trong nháy chứa ĐÚNG NGUYÊN tên cũ: Controller="rptA", controller="rptA", reportFile="rptA_01", Controller: 'rptA', g.showForm('rptA'),
/// &lt;!ENTITY TransferID "rptA"&gt; — trừ giá trị của thuộc tính v= / e= (chữ hiện cho người dùng, vd e="Customer");</item>
/// <item>tên file trong đường dẫn trong nháy: SYSTEM ".\Include\rptABI.xml" khi chính file đó cũng được clone;</item>
/// <item>tiêu đề: cặp v="…" e="…" có v bằng đúng tiêu đề cũ (không phân biệt hoa thường) → tiêu đề mới (e chỉ đổi khi cũng bằng tiêu đề E cũ);
/// bản cũ viết hoa thì bản mới viết hoa. v khác tiêu đề thì giữ cả cặp.</item>
/// </list>
/// Tên nằm giữa chữ khác (rs_PrintrptA, rptA_01 khi chỉ clone rptA) không đổi. File .f (đã mã hoá), .rpt, .xlsx... chỉ copy nguyên.
/// </summary>
public static class SourceCopyRenamer
{
    /// <summary>Đuôi file văn bản được đổi tên bên trong.</summary>
    public static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".aspx", ".ascx", ".master", ".xml", ".txt", ".js", ".ent", ".dct", ".xsd", ".sql" };

    public sealed record Title(string V, string E);

    /// <summary>Tiêu đề của trang Main\*.aspx: v="…" e="…" trong chỉ thị &lt;%@ Page … %&gt;.</summary>
    public static Title? ReadAspxTitle(string text)
    {
        var page = Regex.Match(text, @"<%@\s*Page\b(?<b>.*?)%>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!page.Success) return null;
        string Attr(string name) { var m = Regex.Match(page.Groups["b"].Value, @"\b" + name + @"\s*=\s*""(?<v>[^""]*)"""); return m.Success ? m.Groups["v"].Value : ""; }
        var t = new Title(Attr("v"), Attr("e"));
        return t.V.Length + t.E.Length > 0 ? t : null;
    }

    /// <summary>Tiêu đề của file Grid / Dir: &lt;title v="…" e="…"&gt;.</summary>
    public static Title? ReadXmlTitle(string text)
    {
        var m = Regex.Match(text, @"<title\b(?<b>[^>]*)>", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string Attr(string name) { var a = Regex.Match(m.Groups["b"].Value, @"\b" + name + @"\s*=\s*""(?<v>[^""]*)"""); return a.Success ? a.Groups["v"].Value : ""; }
        var t = new Title(Attr("v"), Attr("e"));
        return t.V.Length + t.E.Length > 0 ? t : null;
    }

    /// <summary>Bảng tên cũ → tên mới từ các cặp (file nguồn, tên file mới). Bỏ cặp không đổi tên; cùng tên cũ mà 2 tên mới khác nhau thì giữ cặp đầu
    /// và ghi vào <paramref name="conflicts"/>.</summary>
    public static Dictionary<string, string> BuildNameMap(IEnumerable<(string Source, string NewName)> pairs, List<string> conflicts)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, newName) in pairs)
        {
            var oldBase = Path.GetFileNameWithoutExtension(source);
            var newBase = Path.GetFileNameWithoutExtension(newName);
            if (oldBase.Length == 0 || newBase.Length == 0 || oldBase.Equals(newBase, StringComparison.OrdinalIgnoreCase)) continue;
            if (!map.TryGetValue(oldBase, out var existing)) map[oldBase] = newBase;
            else if (!existing.Equals(newBase, StringComparison.OrdinalIgnoreCase)) conflicts.Add($"{oldBase} → {existing} (bỏ {newBase})");
        }
        return map;
    }

    /// <summary>Đổi tên + tiêu đề trong nội dung một file. <paramref name="oldTitle"/>/<paramref name="newTitle"/> null = không đổi tiêu đề.</summary>
    public static string Rewrite(string text, IReadOnlyDictionary<string, string> names, Title? oldTitle, Title? newTitle)
    {
        if (names.Count > 0)
        {
            var alt = string.Join("|", names.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
            // 1) Chuỗi trong nháy = đúng nguyên tên cũ; không phải giá trị của v= / e= (chữ hiển thị).
            text = Regex.Replace(text, @"(?<!(?:^|[^\w.:-])[ve]\s*=\s*)(?<q>[""'])(?<n>" + alt + @")\k<q>",
                m => m.Groups["q"].Value + names[m.Groups["n"].Value] + m.Groups["q"].Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            // 2) Tên file trong đường dẫn trong nháy: ".\Include\rptABI.xml" / "../x/rptA.js".
            text = Regex.Replace(text, @"(?<=[""'][^""'\r\n]*[\\/])(?<n>" + alt + @")(?=\.[A-Za-z0-9]{1,6}[""'])",
                m => names[m.Groups["n"].Value], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (oldTitle is not null && newTitle is not null)
        {
            var changeV = Changed(oldTitle.V, newTitle.V);
            var changeE = Changed(oldTitle.E, newTitle.E);
            // v= và e= xét theo CẶP (v="…" e="…" của cùng thẻ): v phải bằng tiêu đề cũ mới đổi — header mẫu in Report có v khác tiêu đề nhưng
            // e trùng ("Customer Balance") thì giữ cả cặp, không để V cũ đi với E mới. Thẻ chỉ có e= (không có v) thì so riêng e.
            if (changeV || changeE)
                text = Regex.Replace(text,
                    @"(?<=(?:^|[\s<]))(?:(?<va>[vV])(?<veq>\s*=\s*"")(?<v>[^""]*)""(?:(?<sp>\s+)(?<ea>[eE])(?<eeq>\s*=\s*"")(?<e>[^""]*)"")?|(?<ea>[eE])(?<eeq>\s*=\s*"")(?<e>[^""]*)"")",
                    m =>
                    {
                        var hasV = m.Groups["va"].Success;
                        var hasE = m.Groups["ea"].Success;
                        if (hasV ? !SameText(m.Groups["v"].Value, oldTitle.V) : !SameText(m.Groups["e"].Value, oldTitle.E)) return m.Value;
                        var sb = new StringBuilder();
                        if (hasV)
                        {
                            sb.Append(m.Groups["va"].Value).Append(m.Groups["veq"].Value)
                              .Append(changeV ? Escape(Case(m.Groups["v"].Value, oldTitle.V, newTitle.V)) : m.Groups["v"].Value).Append('"');
                            if (hasE) sb.Append(m.Groups["sp"].Value);
                        }
                        if (hasE)
                        {
                            var e = m.Groups["e"].Value;
                            sb.Append(m.Groups["ea"].Value).Append(m.Groups["eeq"].Value)
                              .Append(changeE && SameText(e, oldTitle.E) ? Escape(Case(e, oldTitle.E, newTitle.E)) : e).Append('"');
                        }
                        return sb.ToString();
                    }, RegexOptions.Multiline);
        }
        return text;
    }

    /// <summary>Bản trong file viết hoa toàn bộ (dòng tiêu đề của mẫu in) mà tiêu đề cũ thì không → tiêu đề mới cũng viết hoa.</summary>
    private static string Case(string raw, string oldValue, string newValue)
    {
        var decoded = Norm(raw);
        var upper = decoded.Any(char.IsLetter) && decoded == decoded.ToUpperInvariant() && Norm(oldValue) != Norm(oldValue).ToUpperInvariant();
        return upper ? newValue.ToUpperInvariant() : newValue;
    }

    private static bool Changed(string oldValue, string newValue) =>
        Norm(oldValue).Trim().Length > 0 && Norm(newValue).Trim().Length > 0 && Norm(oldValue) != Norm(newValue);

    private static string Norm(string s) => WebUtility.HtmlDecode(s ?? "").Normalize(NormalizationForm.FormC);

    private static bool SameText(string raw, string oldValue) =>
        string.Equals(Norm(raw).Trim(), Norm(oldValue).Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>Copy <paramref name="source"/> sang <paramref name="destination"/>; file văn bản thì đổi tên/tiêu đề bên trong, giữ nguyên BOM / mã hoá.
    /// Trả về: true = đã sửa nội dung, false = copy nguyên, null = file văn bản không đọc được dạng UTF-8/UTF-16 (copy nguyên, không đổi gì).</summary>
    public static bool? CopyRewriting(string source, string destination, IReadOnlyDictionary<string, string> names, Title? oldTitle, Title? newTitle)
    {
        if (!TextExtensions.Contains(Path.GetExtension(source)))
        {
            File.Copy(source, destination, overwrite: true);
            return false;
        }

        var bytes = File.ReadAllBytes(source);
        if (!TryDecode(bytes, out var text, out var encoding, out var preambleLength))
        {
            File.Copy(source, destination, overwrite: true);
            return null;
        }
        var rewritten = Rewrite(text, names, oldTitle, newTitle);
        if (rewritten == text)
        {
            File.Copy(source, destination, overwrite: true);
            return false;
        }
        var body = encoding.GetBytes(rewritten);
        var output = new byte[preambleLength + body.Length];
        Array.Copy(bytes, output, preambleLength);
        Array.Copy(body, 0, output, preambleLength, body.Length);
        File.WriteAllBytes(destination, output);
        return true;
    }

    /// <summary>Đọc file văn bản kèm BOM nếu có; không BOM thì phải là UTF-8 hợp lệ (file ANSI thì bỏ qua để không làm hỏng chữ có dấu).</summary>
    public static bool TryDecode(byte[] bytes, out string text, out Encoding encoding, out int preambleLength)
    {
        text = ""; preambleLength = 0;
        encoding = new UTF8Encoding(false, throwOnInvalidBytes: true);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) preambleLength = 3;
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { encoding = new UnicodeEncoding(false, false, true); preambleLength = 2; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { encoding = new UnicodeEncoding(true, false, true); preambleLength = 2; }
        try { text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength); return true; }
        catch (DecoderFallbackException) { return false; }
    }
}
