using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.Services.Rpt.Builder;

/// <summary>
/// "Từ điển trường" rút từ chính source FastBusiness (302 Filter, 445 Grid): với mỗi tên cột → tiêu đề Việt / Anh, độ rộng, định dạng của Grid và kiểu ô lọc
/// (AutoComplete / Lookup + controller + field tên đi kèm). Nhờ đó cột nào quen thuộc (ma_kh, ma_vt, tk, ma_nt…) được điền sẵn đúng như các báo cáo chuẩn;
/// cột lạ thì dựa vào kiểu dữ liệu SQL. Dữ liệu nhúng trong DLL (Data\reportcatalog.json của project, sinh từ source mẫu).
/// </summary>
public sealed class ReportCatalog
{
    public sealed record FilterInfo(string Style, string Controller, string Reference, string Type, string Hv, string He, string Info, string Key, string Check, int N);
    public sealed record GridInfo(string Hv, string He, string Type, int W, string Fmt, int N);

    private readonly Dictionary<string, FilterInfo> _filters = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GridInfo> _grid = new(StringComparer.OrdinalIgnoreCase);
    private static ReportCatalog? _instance;
    public static ReportCatalog Instance => _instance ??= Load();

    public int FilterCount => _filters.Count;
    public int GridCount => _grid.Count;

    public static ReportCatalog Load(string? path = null)
    {
        var cat = new ReportCatalog();
        try
        {
            using var stream = path is null ? typeof(ReportCatalog).Assembly.GetManifestResourceStream("reportcatalog.json") : (File.Exists(path) ? File.OpenRead(path) : null);
            if (stream is null) return cat;
            using var doc = JsonDocument.Parse(stream);
            static string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            static int I(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : 0;
            foreach (var p in doc.RootElement.GetProperty("filters").EnumerateObject())
                cat._filters[p.Name] = new FilterInfo(S(p.Value, "style"), S(p.Value, "controller"), S(p.Value, "reference"), S(p.Value, "type"), S(p.Value, "hv"), S(p.Value, "he"),
                    S(p.Value, "info"), S(p.Value, "key"), S(p.Value, "check"), I(p.Value, "n"));
            foreach (var p in doc.RootElement.GetProperty("grid").EnumerateObject())
                cat._grid[p.Name] = new GridInfo(S(p.Value, "hv"), S(p.Value, "he"), S(p.Value, "type"), I(p.Value, "w"), S(p.Value, "fmt"), I(p.Value, "n"));
        }
        catch { /* thiếu / hỏng file từ điển: vẫn chạy, chỉ là không có gợi ý sẵn */ }
        return cat;
    }

    public FilterInfo? Filter(string column) => _filters.GetValueOrDefault(Bare(column));
    public GridInfo? Grid(string column) => _grid.GetValueOrDefault(Bare(column));

    public static string Bare(string column) { var i = column.LastIndexOf('.'); return (i >= 0 ? column[(i + 1)..] : column).Trim(); }

    /// <summary>Kiểu Grid theo kiểu SQL: char/varchar → String; date → DateTime; số nguyên → Int; còn lại số thực → Decimal.</summary>
    public static string TypeOfSql(string sqlType)
    {
        var t = (sqlType ?? "").ToLowerInvariant();
        if (t.Contains("char") || t.Contains("text") || t == "uniqueidentifier") return "String";
        if (t.Contains("date") || t.Contains("time")) return "DateTime";
        if (t is "int" or "smallint" or "tinyint" or "bigint" or "bit") return "Int";
        return "Decimal";
    }

    /// <summary>Định dạng hiển thị trên Grid: ưu tiên của source mẫu (đổi *InputFormat → *ViewFormat), không có thì suy theo tên cột.</summary>
    public string FormatFor(string column, string type)
    {
        if (type == "DateTime") return "@datetimeFormat";
        if (type == "Int") return "####";
        if (type != "Decimal") return "";
        var g = Grid(column);
        if (g is not null && g.Fmt.StartsWith('@') && g.Fmt.Contains("Format") && !g.Fmt.Contains("upperCase")) return g.Fmt.Replace("InputFormat", "ViewFormat");
        var n = Bare(column).ToLowerInvariant();
        if (n.EndsWith("_nt") || n.Contains("_nt_")) return "@foreignCurrencyAmountViewFormat";
        if (n.Contains("so_luong") || n.StartsWith("sl_") || n == "sl") return "@quantityViewFormat";
        return "@baseCurrencyAmountViewFormat";
    }

    /// <summary>Gợi ý cột báo cáo cho "alias.cột" + kiểu SQL: tiêu đề / độ rộng / định dạng theo từ điển.</summary>
    public ColumnSpec SuggestColumn(string source, string sqlType, string? alias = null)
    {
        var name = Bare(source);
        var g = Grid(name);
        // Kiểu THẬT của cột trong database quyết định loại: cột chữ (tài khoản, số điện thoại, mã…) luôn là String dù từ điển mẫu ghi khác;
        // cột số thì lấy Decimal / Int theo từ điển nếu có. Không biết kiểu SQL (biểu thức…) thì dùng từ điển.
        var sqlT = TypeOfSql(sqlType);
        string type;
        if (string.IsNullOrWhiteSpace(sqlType)) type = g is { Type: "String" or "DateTime" or "Decimal" or "Int" } ? g.Type : "Decimal";
        else if (sqlT is "String" or "DateTime") type = sqlT;
        else type = g is { Type: "Decimal" or "Int" } ? g.Type : sqlT;
        return new ColumnSpec
        {
            Source = source, Name = name, Type = type,
            HeaderVi = g?.Hv ?? Humanize(name), HeaderEn = g?.He ?? Humanize(name),
            Width = g?.W is > 0 ? g.W : type switch { "DateTime" => 80, "Decimal" => 110, "Int" => 70, _ => 120 },
            Format = FormatFor(name, type),
        };
    }

    /// <summary>Gợi ý ô lọc cho một cột: dùng cấu hình tra cứu chuẩn nếu từ điển có (ma_kh → AutoComplete Customer + ten_kh%l), không thì ô chữ lọc LIKE.</summary>
    public FilterSpec SuggestFilter(string column, string sqlType)
    {
        var name = Bare(column);
        var f = Filter(name);
        var type = TypeOfSql(sqlType);
        var spec = new FilterSpec { Field = name, Column = column, Op = "like", HeaderVi = f?.Hv is { Length: > 0 } hv ? hv : Humanize(name), HeaderEn = f?.He is { Length: > 0 } he ? he : Humanize(name) };
        if (f is not null && f.Controller.Length > 0)
        {
            spec.Style = f.Style; spec.Controller = f.Controller; spec.Reference = f.Reference;
            spec.Key = f.Key.Length > 0 ? f.Key : "status = '1'"; spec.Check = f.Check.Length > 0 ? f.Check : "1 = 1";
            spec.Information = f.Info.Length > 0 ? f.Info : InformationFor(name, f.Reference);
        }
        if (type == "DateTime") spec.Op = "date";
        return spec;
    }

    /// <summary>Tham chiếu tên đi kèm: ma_vv + ten_vv%l → <c>ma_vv$dmvv.ten_vv%l</c> (bảng danh mục theo quy ước dm + phần sau "ten_").</summary>
    public static string InformationFor(string field, string reference)
    {
        var m = Regex.Match(reference ?? "", @"^ten_(\w+)%l$", RegexOptions.IgnoreCase);
        return m.Success ? $"{field}$dm{m.Groups[1].Value}.{reference}" : "";
    }

    private static string Humanize(string name)
    {
        var t = Regex.Replace(name, @"^(ma|so|ten)_", m => m.Value == "ma_" ? "Mã " : m.Value == "so_" ? "Số " : "Tên ");
        t = t.Replace('_', ' ').Trim();
        return t.Length == 0 ? name : char.ToUpperInvariant(t[0]) + t[1..];
    }
}
