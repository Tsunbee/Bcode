using System.Net;
using System.Text.RegularExpressions;

namespace Bcode.App.Services.Rpt;

/// <summary>Đọc controller của 1 báo cáo trong source project: Main\&lt;trang&gt;.aspx (thuộc tính <c>Controller="..."</c>) →
/// App_Data\Controllers\Grid\&lt;controller&gt;.xml (header Việt/Anh + độ rộng cột) và Filter\&lt;controller&gt;.xml (header tham số).
/// Dùng regex thay vì XmlReader vì các file này có DOCTYPE với entity ngoài (&amp;XMLStandardReportToolbar;...) không resolve được.</summary>
public class GridControllerReader
{
    private static readonly Regex ControllerAttr = new(@"\bController\s*=\s*""([A-Za-z0-9_]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FieldsBlock = new(@"<fields>(.*?)</fields>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex FieldRx = new(@"<field\s+([^>]*?)(/?)>(.*?)(?=<field\s|</fields>)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AttrRx = new(@"([A-Za-z_:][\w:.-]*)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex HeaderRx = new(@"<header\s+([^>]*?)/?>", RegexOptions.Compiled);
    private static readonly Regex TitleRx = new(@"<title\s+([^>]*?)/?>", RegexOptions.Compiled);

    public ControllerInfo Load(string? sourcePath, string pageOrController)
    {
        var name = pageOrController.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrWhiteSpace(sourcePath))
            return Empty(name, "Chưa chọn Source Path hoặc chưa nhập tên.");

        var root = sourcePath.Trim();
        var note = "";
        // 1) Trang Main\<name>.aspx khai báo Controller="..."; 2) không có thì coi chính tên đó là controller.
        var aspx = Path.Combine(root, "Main", name + ".aspx");
        if (File.Exists(aspx))
        {
            var m = ControllerAttr.Match(File.ReadAllText(aspx));
            if (m.Success) { note = $"Main\\{name}.aspx → Controller=\"{m.Groups[1].Value}\"."; name = m.Groups[1].Value; }
        }

        var ctl = Path.Combine(root, "App_Data", "Controllers");
        var gridPath = Path.Combine(ctl, "Grid", name + ".xml");
        var filterPath = Path.Combine(ctl, "Filter", name + ".xml");
        var reportPath = Path.Combine(ctl, "Report", name + ".xml");
        if (!File.Exists(gridPath)) return Empty(name, note + $" Không thấy {gridPath}");

        var gridText = Regex.Replace(File.ReadAllText(gridPath), @"<!--.*?-->", "", RegexOptions.Singleline); // bỏ phần đã comment (vd field ảnh bị tắt)
        var (title, titleE) = ReadAttrPair(TitleRx.Match(gridText));
        var fields = ParseFields(gridText).Select(a =>
        {
            int.TryParse(a.Attrs.GetValueOrDefault("width"), out var w);
            var hidden = (a.Attrs.ContainsKey("width") && w == 0) || string.Equals(a.Attrs.GetValueOrDefault("hidden"), "true", StringComparison.OrdinalIgnoreCase);
            return new GridField(a.Name, w, a.Attrs.GetValueOrDefault("type") ?? "String", a.V, a.E, a.Attrs.GetValueOrDefault("aggregate") ?? "", hidden);
        }).ToList();

        var filters = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        if (File.Exists(filterPath))
            foreach (var f in ParseFields(File.ReadAllText(filterPath))) filters[f.Name] = (f.V, f.E);

        return new ControllerInfo(name, title, titleE, fields, filters, gridPath, File.Exists(reportPath) ? reportPath : null, note.Trim(),
            ParsePivot(gridText), ParseViewFields(gridText));
    }

    private static readonly Regex PivotRx = new(@"<pivot\s+([^>]*?)/?>", RegexOptions.Compiled);
    private static readonly Regex ViewRx = new(@"<view\s+id\s*=\s*""Grid""[^>]*>(.*?)</view>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ViewFieldRx = new(@"<field\s+name\s*=\s*""([^""]+)""", RegexOptions.Compiled);

    /// <summary>&lt;pivot rowField="sysRow" columnField="sysColumn" dataFields="a, b" .../&gt; — null nếu báo cáo không phải pivot.</summary>
    private static PivotInfo? ParsePivot(string xml)
    {
        var m = PivotRx.Match(xml);
        if (!m.Success) return null;
        var a = AttrRx.Matches(m.Groups[1].Value).ToDictionary(x => x.Groups[1].Value, x => x.Groups[2].Value);
        return new PivotInfo(a.GetValueOrDefault("rowField") ?? "", a.GetValueOrDefault("columnField") ?? "",
            (a.GetValueOrDefault("dataFields") ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList());
    }

    /// <summary>Các field của &lt;view id="Grid"&gt; theo đúng thứ tự hiện trên lưới.</summary>
    private static List<string> ParseViewFields(string xml)
    {
        var m = ViewRx.Match(xml);
        return m.Success ? ViewFieldRx.Matches(m.Groups[1].Value).Select(x => x.Groups[1].Value).ToList() : new List<string>();
    }

    private static ControllerInfo Empty(string name, string note) =>
        new(name, "", "", new(), new(StringComparer.OrdinalIgnoreCase), null, null, note.Trim());

    private record Parsed(string Name, Dictionary<string, string> Attrs, string V, string E);

    private static List<Parsed> ParseFields(string xml)
    {
        var list = new List<Parsed>();
        var block = FieldsBlock.Match(xml);
        if (!block.Success) return list;
        foreach (Match m in FieldRx.Matches(block.Groups[1].Value + "</fields>"))
        {
            var attrs = AttrRx.Matches(m.Groups[1].Value).ToDictionary(a => a.Groups[1].Value, a => WebUtility.HtmlDecode(a.Groups[2].Value));
            if (!attrs.TryGetValue("name", out var n)) continue;
            var (v, e) = ReadAttrPair(HeaderRx.Match(m.Groups[3].Value));
            list.Add(new Parsed(n, attrs, v, e));
        }
        return list;
    }

    private static (string V, string E) ReadAttrPair(Match m)
    {
        if (!m.Success) return ("", "");
        var d = AttrRx.Matches(m.Groups[1].Value).ToDictionary(a => a.Groups[1].Value, a => WebUtility.HtmlDecode(a.Groups[2].Value));
        return (d.GetValueOrDefault("v") ?? "", d.GetValueOrDefault("e") ?? "");
    }
}
