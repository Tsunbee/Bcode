using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services.Rpt;

/// <summary>Dựng Report xml (Controllers\Report\&lt;name&gt;.xml): các khối <c>&lt;field name="h_xxx"&gt;</c> giữ nguyên nếu report đã có,
/// chỉ sinh thêm những biến còn thiếu. Không đụng phần khác của file (forms, entity, comment...).</summary>
public class RptXmlBuilder
{
    private static readonly Regex FieldBlock = new(@"<field\s+name\s*=\s*""([^""]+)""[^>]*?(?:/>|>.*?</field>)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HeaderRx = new(@"<header\s+v\s*=\s*""([^""]*)""\s+e\s*=\s*""([^""]*)""", RegexOptions.Compiled);

    /// <summary>Biến đã có trong xml hiện hữu → (V, E). Dùng để điền sẵn ở bước thiết kế và đánh dấu "đã có".</summary>
    public Dictionary<string, (string V, string E)> ParseExisting(string? xml)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(xml)) return map;
        var range = LastFieldsRange(xml);
        if (range is null) return map;
        foreach (Match m in FieldBlock.Matches(xml.Substring(range.Value.Start, range.Value.Length)))
        {
            var h = HeaderRx.Match(m.Value);
            map[m.Groups[1].Value] = h.Success ? (WebUtility.HtmlDecode(h.Groups[1].Value), WebUtility.HtmlDecode(h.Groups[2].Value)) : ("", "");
        }
        return map;
    }

    public string Build(string? existingXml, IReadOnlyList<RptVar> vars, string templateFile, string title, string titleE)
    {
        if (string.IsNullOrWhiteSpace(existingXml)) existingXml = NewFile(templateFile, title, titleE);

        var range = LastFieldsRange(existingXml);
        if (range is null) throw new InvalidOperationException("File report xml không có khối <fields>...</fields>.");
        var inner = existingXml.Substring(range.Value.Start, range.Value.Length);

        // Khối có sẵn đã bị sửa header → thay đúng khối đó; biến chưa có → nối vào cuối khối <fields>.
        var edits = vars.Where(v => v.Exists && v.Edited).ToDictionary(v => v.Code);
        if (edits.Count > 0)
            inner = FieldBlock.Replace(inner, m => edits.TryGetValue(m.Groups[1].Value, out var v) ? Block(v, "		").TrimStart() : m.Value);

        var present = new HashSet<string>(FieldBlock.Matches(inner).Select(m => m.Groups[1].Value), StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var v in vars)
            if (!present.Contains(v.Code)) sb.Append(Block(v, "\t\t")).Append('\n');

        var close = inner.LastIndexOf("</fields>", StringComparison.Ordinal);
        var head = inner.Substring(0, close).TrimEnd('\t', ' ');
        var added = sb.Length == 0 ? "" : (head.EndsWith('\n') ? "" : "\n") + sb.ToString();
        return existingXml.Substring(0, range.Value.Start) + head + added + "\t" + inner.Substring(close)
             + existingXml.Substring(range.Value.Start + range.Value.Length);
    }

    private static string Block(RptVar v, string indent) =>
        $"{indent}<field name=\"{v.Code}\" type =\"String\">\n{indent}\t<header v=\"{Esc(v.V)}\" e=\"{Esc(v.E)}\"/>\n{indent}</field>";

    private static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;"); // chỉ escape ký tự XML, giữ nguyên tiếng Việt

    /// <summary>Vị trí khối &lt;fields&gt;..&lt;/fields&gt; cuối cùng trong file (khối của report).</summary>
    private static (int Start, int Length)? LastFieldsRange(string xml)
    {
        var s = xml.LastIndexOf("<fields>", StringComparison.Ordinal);
        if (s < 0) return null;
        var e = xml.IndexOf("</fields>", s, StringComparison.Ordinal);
        if (e < 0) return null;
        return (s, e + "</fields>".Length - s);
    }

    private static string NewFile(string tpl, string title, string titleE)
    {
        var t = Esc(title); var te = Esc(titleE);
        var lines = new[]
        {
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>",
            "<!DOCTYPE report [",
            "\t<!ENTITY b SYSTEM \".\\Include\\BaseCurrency.xml\">",
            "\t<!ENTITY f SYSTEM \".\\Include\\ForeignCurrency.xml\">",
            "\t<!ENTITY s SYSTEM \".\\Include\\Separate.xml\">",
            "",
            "\t<!ENTITY p \"../images/pdf.gif\">",
            "\t<!ENTITY e \"../images/excel.gif\">",
            "\t<!ENTITY bi \"../images/bilingual.png\">",
            "\t<!ENTITY be \"../images/combine.png\">",
            "]>",
            "",
            "<report xmlns=\"urn:schemas-fast-com:data-report\">",
            "\t<forms>",
            $"\t\t<form id=\"110\" templateFile=\"{tpl}\" commandArgument=\"Excel\" urlImage=\"&e;\">",
            $"\t\t\t<header v=\"{t}\" e=\"{te}\"></header>",
            "\t\t\t<download>",
            $"\t\t\t\t<header v=\"{t}\" e=\"{te}\"/>",
            "\t\t\t</download>",
            "\t\t</form>",
            "\t\t&s;",
            "\t</forms>",
            "",
            "\t<fields>",
            "\t</fields>",
            "</report>",
        };
        return string.Join("\n", lines);
    }
}
