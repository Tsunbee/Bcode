using System.Text;

namespace Bcode.App.Services;

/// <summary>Một cột của bảng danh mục, kèm cách nó hiện trên Grid / form Dir.</summary>
public class CatalogColumn
{
    public bool InGrid { get; set; } = true;
    public bool InForm { get; set; } = true;
    public string Name { get; set; } = "";
    public string HeaderV { get; set; } = "";
    public string HeaderE { get; set; } = "";
    public int Width { get; set; } = 150;
    /// <summary>"", "Boolean", "Decimal" hoặc "DateTime" — giá trị thuộc tính type="" của field.</summary>
    public string Type { get; set; } = "";
    public bool AllowNulls { get; set; } = true;
    public bool ReadOnly { get; set; }
    public bool IsKey { get; set; }
}

public class CatalogSpec
{
    /// <summary>Tên controller = tên file Dir/Grid/Lookup (không đuôi), vd "Customer".</summary>
    public string Id { get; set; } = "";
    public string GridId { get; set; } = "";
    public string Table { get; set; } = "";
    public string Key { get; set; } = "";
    public string Order { get; set; } = "";
    public string TitleV { get; set; } = "";
    public string TitleE { get; set; } = "";
    public string SubTitleV { get; set; } = "";
    public string SubTitleE { get; set; } = "";
    /// <summary>Tên file Main (không đuôi), vd "arkh" → Main\arkh.aspx.</summary>
    public string MainName { get; set; } = "";
    public bool CreateLookup { get; set; }
    public string LookupTable { get; set; } = "";
    public List<string> ToolbarCommands { get; set; } = new();
    public List<CatalogColumn> Columns { get; set; } = new();
}

/// <summary>
/// "Clone danh mục" — sinh bộ file Dir / Grid / (Lookup) / Main.aspx cho 1 danh mục mới từ
/// thư mục mẫu (Templates\Catalog). Mẫu là file thường có placeholder {{Ten}}, thay bằng
/// string.Replace thuần — không có logic ẩn, nên sửa mẫu là đổi ngay kết quả sinh ra.
///
/// Mẫu mặc định cố ý TỐI GIẢN (field + view + toolbar), không chép các command/script/entity
/// đặc thù của Customer.xml (CheckTaxCode, EBanking, BI.*...) — những thứ đó gắn với nghiệp vụ
/// khách hàng, sinh sang danh mục khác sẽ sai. Cần thêm gì dùng chung thì thêm thẳng vào file
/// mẫu; placeholder {{DirFields}}, {{GridFields}}... do code điền.
/// </summary>
public class CatalogCloneService
{
    public const string DefaultTemplateFolderName = @"Templates\Catalog";

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private const string DirColumnWidths = "120, 30, 45, 25, 65, 45, 30, 25, 65, 75, 25, 0, 203";

    public static string DefaultTemplateDir =>
        Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultTemplateFolderName);

    /// <summary>Các file sẽ được ghi (đường dẫn đầy đủ) — để hỏi xác nhận ghi đè trước khi sinh.</summary>
    public IReadOnlyList<(string Template, string Path)> PlannedFiles(CatalogSpec spec, string outputRoot)
    {
        var list = new List<(string, string)>
        {
            ("Dir.xml", Path.Combine(outputRoot, "Dir", spec.Id + ".xml")),
            ("Grid.xml", Path.Combine(outputRoot, "Grid", spec.Id + ".xml")),
        };
        if (spec.CreateLookup) list.Add(("Lookup.xml", Path.Combine(outputRoot, "Lookup", spec.Id + ".xml")));
        list.Add(("Main.aspx", Path.Combine(outputRoot, "Main", spec.MainName + ".aspx")));
        return list;
    }

    /// <summary>Báo lỗi nhập thiếu (null = hợp lệ).</summary>
    public static string? Validate(CatalogSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Id)) return "Chưa nhập Id (tên file).";
        if (spec.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Id chứa ký tự không hợp lệ cho tên file.";
        if (string.IsNullOrWhiteSpace(spec.Table)) return "Chưa chọn bảng.";
        if (string.IsNullOrWhiteSpace(spec.Key)) return "Chưa chọn cột khoá (Key).";
        if (string.IsNullOrWhiteSpace(spec.MainName)) return "Chưa nhập tên file Main.";
        if (spec.MainName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Tên file Main chứa ký tự không hợp lệ.";
        if (!spec.Columns.Any(c => c.InGrid)) return "Chưa tick cột nào cho Grid.";
        if (!spec.Columns.Any(c => c.InForm)) return "Chưa tick cột nào cho form Dir.";
        if (!spec.Columns.Any(c => c.Name.Equals(spec.Key, StringComparison.OrdinalIgnoreCase) && c.InForm))
            return "Cột khoá phải được tick trong form Dir.";
        return null;
    }

    public List<string> Generate(CatalogSpec spec, string templateDir, string outputRoot)
    {
        var written = new List<string>();
        var map = BuildPlaceholders(spec);

        foreach (var (template, path) in PlannedFiles(spec, outputRoot))
        {
            var templatePath = Path.Combine(templateDir, template);
            if (!File.Exists(templatePath))
                throw new FileNotFoundException($"Thiếu file mẫu: {templatePath}");

            var text = File.ReadAllText(templatePath);
            foreach (var (token, value) in map)
                text = text.Replace("{{" + token + "}}", value);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, NormalizeNewLines(text), Utf8Bom);
            written.Add(path);
        }
        return written;
    }

    private static Dictionary<string, string> BuildPlaceholders(CatalogSpec spec)
    {
        var order = string.IsNullOrWhiteSpace(spec.Order) ? spec.Key : spec.Order;
        var gridCols = spec.Columns.Where(c => c.InGrid).ToList();
        var formCols = spec.Columns.Where(c => c.InForm).ToList();

        // Lookup: khoá + cột tên (cột chữ đầu tiên không phải khoá) + vài cột chữ tiếp theo.
        var textCols = gridCols.Where(c => !c.IsKey && c.Type == "").ToList();
        var lookupName = textCols.FirstOrDefault()?.Name ?? spec.Key;
        var lookupCols = gridCols.Where(c => c.IsKey).Concat(textCols.Take(3)).ToList();

        return new Dictionary<string, string>
        {
            ["Id"] = Attr(spec.Id),
            ["GridId"] = Attr(string.IsNullOrWhiteSpace(spec.GridId) ? spec.Id : spec.GridId),
            ["Table"] = Attr(spec.Table),
            ["Key"] = Attr(spec.Key),
            ["Order"] = Attr(order),
            ["TitleV"] = Attr(spec.TitleV),
            ["TitleE"] = Attr(spec.TitleE),
            ["SubTitleV"] = Attr(spec.SubTitleV),
            ["SubTitleE"] = Attr(spec.SubTitleE),
            ["LookupTable"] = Attr(string.IsNullOrWhiteSpace(spec.LookupTable) ? spec.Table : spec.LookupTable),
            ["LookupName"] = Attr(lookupName),
            ["DirFields"] = string.Join("\n", formCols.Select(DirField)),
            ["DirColumns"] = DirColumnWidths,
            ["DirItems"] = string.Join("\n", formCols.Select(DirItem)),
            ["GridFields"] = string.Join("\n", gridCols.Select(GridField)),
            ["GridViewFields"] = string.Join("\n", gridCols.Select(c => $"\t\t\t<field name=\"{Attr(c.Name)}\"/>")),
            ["ToolbarButtons"] = string.Join("\n", spec.ToolbarCommands.Select(ToolbarButton)),
            ["LookupFields"] = string.Join("\n", lookupCols.Select(LookupField)),
        };
    }

    private static string DirField(CatalogColumn c)
    {
        var attrs = new StringBuilder($"name=\"{Attr(c.Name)}\"");
        if (c.IsKey) attrs.Append(" isPrimaryKey=\"true\"");
        if (c.Type != "") attrs.Append($" type=\"{c.Type}\"");
        if (c.IsKey && c.Type == "") attrs.Append(" dataFormatString=\"@upperCaseFormat\"");
        if (c.Type == "Decimal") attrs.Append(" dataFormatString=\"@baseCurrencyAmountInputFormat\" clientDefault=\"0\"");
        if (c.Type == "DateTime") attrs.Append(" dataFormatString=\"@datetimeFormat\" align=\"left\"");
        if (!c.AllowNulls || c.IsKey) attrs.Append(" allowNulls=\"false\"");
        if (c.ReadOnly) attrs.Append(" readOnly=\"true\"");

        var sb = new StringBuilder();
        sb.Append($"\t\t<field {attrs}>\n");
        sb.Append($"\t\t\t<header v=\"{Attr(c.HeaderV)}\" e=\"{Attr(c.HeaderE)}\"></header>\n");
        if (c.IsKey && c.Type == "") sb.Append("\t\t\t<items style=\"Mask\"/>\n");
        else if (c.Type == "Decimal") sb.Append("\t\t\t<items style=\"Numeric\"/>\n");
        sb.Append("\t\t</field>");
        return sb.ToString();
    }

    // Mỗi field 1 dòng: nhãn ở cột đầu, ô nhập chiếm phần còn lại của hàng (13 cột).
    private static string DirItem(CatalogColumn c) =>
        $"\t\t\t<item value=\"1100000000000: [{Attr(c.Name)}].Label, [{Attr(c.Name)}]\"/>";

    private static string GridField(CatalogColumn c)
    {
        var attrs = new StringBuilder($"name=\"{Attr(c.Name)}\"");
        if (c.IsKey) attrs.Append(" isPrimaryKey=\"true\"");
        attrs.Append(" aliasName=\"a\"");
        if (c.Type != "") attrs.Append($" type=\"{c.Type}\"");
        var width = c.Type == "Boolean" ? Math.Min(c.Width, 60) : c.Width;
        attrs.Append($" width=\"{width}\"");
        if (c.IsKey && c.Type == "") attrs.Append(" dataFormatString=\"X\"");
        attrs.Append(" allowSorting=\"true\"");
        attrs.Append(c.Type == "Boolean" ? " allowFilter=\"false\"" : " allowFilter=\"true\"");

        return $"\t\t<field {attrs}>\n\t\t\t<header v=\"{Attr(c.HeaderV)}\" e=\"{Attr(c.HeaderE)}\"></header>\n\t\t</field>";
    }

    private static string LookupField(CatalogColumn c) =>
        $"\t\t<field name=\"{Attr(c.Name)}\" allowSorting=\"true\" allowFilter=\"true\">\n\t\t\t<header v=\"{Attr(c.HeaderV)}\" e=\"{Attr(c.HeaderE)}\"></header>\n\t\t</field>";

    private static string ToolbarButton(string command)
    {
        if (command == "-")
            return "\t\t<button command=\"Separate\">\n\t\t\t<title v=\"-\" e=\"-\"/>\n\t\t</button>";
        var key = $"Toolbar.{(command == "Clone" ? "Copy" : command)}";
        return $"\t\t<button command=\"{command}\">\n\t\t\t<title v=\"{key}\" e=\"{key}\"></title>\n\t\t</button>";
    }

    private static string Attr(string s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string NormalizeNewLines(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>Đoán type="" của FastBusiness từ kiểu SQL (chuỗi "decimal(18,2)", "bit"...).</summary>
    public static string GuessFieldType(string sqlType)
    {
        var t = sqlType.ToLowerInvariant();
        var paren = t.IndexOf('(');
        if (paren >= 0) t = t[..paren];
        return t switch
        {
            "bit" => "Boolean",
            "date" or "datetime" or "datetime2" or "smalldatetime" => "DateTime",
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real"
                or "int" or "bigint" or "smallint" or "tinyint" => "Decimal",
            _ => ""
        };
    }
}
