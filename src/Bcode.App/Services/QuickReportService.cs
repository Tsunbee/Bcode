extern alias rb;
using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bcode.App.Services.Rpt;
using Microsoft.Data.SqlClient;
using Catalog = rb::Bcode.App.Services.Rpt.Builder.ReportCatalog;
using RbDeploy = rb::Bcode.App.Services.Rpt.Builder;

namespace Bcode.App.Services;

/// <summary>Một tham số của procedure báo cáo. <see cref="Role"/> = "filter": người dùng nhập ở form lọc (field <see cref="Field"/>, truyền <c>@field</c>);
/// "system": Bcode tự truyền <see cref="Arg"/> (<c>@@unit</c>, <c>'@@sysDatabaseName'</c>, <c>@@language</c>, <c>@@userID</c>, <c>@@admin</c>, hằng số...).</summary>
public class QuickReportParam
{
    public string Name { get; set; } = "";          // tên tham số procedure, không có @
    public string SqlType { get; set; } = "";       // vd varchar(33), smalldatetime, numeric(28, 6)
    public string Role { get; set; } = "filter";    // filter | system
    public string Arg { get; set; } = "";           // với system
    public string Field { get; set; } = "";         // với filter: tên field trên form lọc
    public string HeaderV { get; set; } = "";
    public string HeaderE { get; set; } = "";
    public string Type { get; set; } = "";          // "" (chữ) | DateTime | Decimal
    public FieldLookup? Lookup { get; set; }
    public string Test { get; set; } = "";          // giá trị khi chạy thử
    public bool IsFilter => Role == "filter";
}

/// <summary>Một cột của lưới — lấy từ result set khi chạy procedure.</summary>
public class QuickReportColumn
{
    public string Name { get; set; } = "";
    public string HeaderV { get; set; } = "";
    public string HeaderE { get; set; } = "";
    public string Type { get; set; } = "";          // "" | DateTime | Decimal
    public int Width { get; set; } = 120;
    public string Format { get; set; } = "";
    public bool Sum { get; set; }
    public bool InGrid { get; set; } = true;
    public bool Hidden { get; set; }
}

public class QuickReportSpec
{
    public string ProcName { get; set; } = "";
    public string Controller { get; set; } = "";
    public string MainName { get; set; } = "";
    public string TitleV { get; set; } = "";
    public string TitleE { get; set; } = "";
    public List<QuickReportParam> Params { get; set; } = new();
    /// <summary>Mọi cột của result set dùng làm lưới (theo thứ tự hiển thị); cột không tick <see cref="QuickReportColumn.InGrid"/> không ra Grid.</summary>
    public List<QuickReportColumn> Columns { get; set; } = new();
    /// <summary>Result set thứ mấy (0 = đầu tiên) của procedure là dữ liệu lưới — trong Excel là <c>!(2 + ResultSet).</c> (vì !1. là dòng tham số của Filter).</summary>
    public int ResultSet { get; set; }
    public bool Excel { get; set; } = true;

    public IEnumerable<QuickReportParam> FilterParams => Params.Where(p => p.IsFilter && p.Field.Length > 0);
    public bool HasDateRange =>
        FilterParams.Any(p => p.Field.Equals("tu_ngay", StringComparison.OrdinalIgnoreCase)) &&
        FilterParams.Any(p => p.Field.Equals("den_ngay", StringComparison.OrdinalIgnoreCase));
    public List<QuickReportColumn> GridColumns => Columns.Where(c => c.InGrid).ToList();
}

/// <summary>Kết quả chạy thử: mỗi result set gồm cột (tên + kiểu .NET) và vài dòng đầu (đã đổi sang chuỗi).</summary>
public record QuickReportSet(List<(string Name, string Type)> Columns, List<string?[]> Rows, bool Truncated);

/// <summary>
/// "Tạo nhanh báo cáo" — báo cáo từ một procedure CÓ SẴN: tham số procedure thành ô lọc (Filter), chạy thử procedure lấy cột kết quả thành
/// cột lưới (Grid), rồi sinh Filter / Grid / Report / Main từ bộ source mẫu <c>Templates\fileSource\CreateReport</c> (cùng kiểu đánh dấu với
/// "Tạo nhanh danh mục": <c>[#TEN#]</c> = giá trị, <c>[#Ten#]…[#Ten#]</c> = khối bật/tắt — xem <see cref="QuickListService.ApplyToggle"/>)
/// và mẫu Excel. Khác "Tạo báo cáo" (ReportBuilder): không sinh procedure, không nối bảng — procedure do người dùng viết sẵn.
/// </summary>
public class QuickReportService
{
    public const string DefaultTemplateFolderName = @"Templates\fileSource\CreateReport";
    public static string DefaultTemplateDir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DefaultTemplateFolderName);

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);
    private static readonly Regex AnyMarker = new(@"\[#([A-Za-z0-9_]+)#\]", RegexOptions.Compiled);
    private static string A(string s) => CatalogCloneService.Attr(s);

    // ---- Tham số procedure ----------------------------------------------------------------------

    private static readonly string[] DateFromNames = { "datefrom", "fromdate", "tu_ngay", "ngay_ct1", "ngay_tu", "ngay1", "ngay_bd" };
    private static readonly string[] DateToNames = { "dateto", "todate", "den_ngay", "ngay_ct2", "ngay_den", "ngay2", "ngay_kt" };

    /// <summary>Tham số hệ thống chuẩn của procedure báo cáo FBO (cùng quy ước "Tạo báo cáo" sinh ra) → biểu thức Filter truyền vào.</summary>
    public static string? SystemArg(string param) => param.ToLowerInvariant() switch
    {
        "unit" => "@@unit",
        "sysdatabasename" => "'@@sysDatabaseName'",
        "language" or "lang" => "@@language",
        "userid" or "user_id" => "@@userID",
        "admin" => "@@admin",
        _ => null,
    };

    /// <summary>Tham số của procedure (theo thứ tự khai báo) từ sys.parameters của App Data.</summary>
    public static async Task<List<(string Name, string SqlType, bool IsOutput)>> ReadParamsAsync(SqlConnection conn, string proc)
    {
        const string sql = @"select p.name, type_name(p.user_type_id), p.max_length, p.precision, p.scale, p.is_output
from sys.parameters p where p.object_id = object_id(@proc) and p.parameter_id > 0 order by p.parameter_id";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@proc", proc);
        var list = new List<(string, string, bool)>();
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var type = rd.GetString(1);
            int len = rd.GetInt16(2), prec = rd.GetByte(3), scale = rd.GetByte(4);
            var full = type.ToLowerInvariant() switch
            {
                "varchar" or "char" or "varbinary" or "binary" => $"{type}({(len < 0 ? "max" : len.ToString())})",
                "nvarchar" or "nchar" => $"{type}({(len < 0 ? "max" : (len / 2).ToString())})",
                "decimal" or "numeric" => $"{type}({prec}, {scale})",
                _ => type,
            };
            list.Add((rd.GetString(0).TrimStart('@'), full, rd.GetBoolean(5)));
        }
        return list;
    }

    private static bool IsDateSql(string t) => Regex.IsMatch(t, @"^(small)?date(time2?)?|^date\b", RegexOptions.IgnoreCase);
    private static bool IsNumSql(string t) => Regex.IsMatch(t, @"^(numeric|decimal|money|smallmoney|float|real|int|bigint|smallint|tinyint)\b", RegexOptions.IgnoreCase);

    /// <summary>Gợi ý vai trò / field / kiểu / tra cứu cho một tham số procedure. Tiêu đề + tra cứu ưu tiên từ điển của "Tạo báo cáo"
    /// (ReportCatalog — rút từ Filter thật của source FastBusiness), rồi mới tới header.xml.</summary>
    public static QuickReportParam SuggestParam(string name, string sqlType, bool isOutput)
    {
        var p = new QuickReportParam { Name = name, SqlType = sqlType };
        var low = name.ToLowerInvariant();
        if (isOutput) { p.Role = "system"; p.Arg = "null"; return p; }
        if (SystemArg(name) is { } arg) { p.Role = "system"; p.Arg = arg; return p; }

        if (DateFromNames.Contains(low)) { p.Field = "tu_ngay"; p.Type = "DateTime"; p.HeaderV = "Từ ngày"; p.HeaderE = "Date from"; p.Test = $"{DateTime.Today:yyyy}-01-01"; return p; }
        if (DateToNames.Contains(low)) { p.Field = "den_ngay"; p.Type = "DateTime"; p.HeaderV = "Đến ngày"; p.HeaderE = "Date to"; p.Test = $"{DateTime.Today:yyyy-MM-dd}"; return p; }

        var dict = FieldDictionaryService.Instance;
        // @ma_kh → ma_kh; @Customer (đặt theo tên controller) → field mã tra cứu bằng controller đó.
        p.Field = low.Contains('_') || Catalog.Instance.Filter(low) != null || dict.Header(low) != null ? low : dict.FieldForController(name) ?? low;
        p.Type = IsDateSql(sqlType) ? "DateTime" : IsNumSql(sqlType) ? "Decimal" : "";
        if (FilterHeader(p.Field) is { } h) { p.HeaderV = h.V; p.HeaderE = h.E; }
        if (p.Type == "") p.Lookup = Lookups(p.Field).FirstOrDefault();
        if (p.Type == "Decimal") p.Test = "0";
        return p;
    }

    /// <summary>Tiêu đề ô lọc của field: ReportCatalog (Filter thật) → header.xml.</summary>
    public static (string V, string E)? FilterHeader(string field)
    {
        var h = Catalog.Instance.Filter(field) is { Hv.Length: > 0 } f ? (f.Hv, f.He) : FieldDictionaryService.Instance.Header(field);
        return h is { } x ? (x.V, English(x.V, x.E)) : null;
    }

    /// <summary>Tiêu đề Anh dùng được: từ điển ghi "???" / trống / còn tiếng Việt thì dịch từ tiêu đề Việt (<see cref="HeaderTranslator"/>).</summary>
    public static string English(string vietnamese, string? english) =>
        !HeaderTranslator.IsMissing(english) ? english!.Trim()
            : HeaderTranslator.Instance.Translate(vietnamese) ?? ((english ?? "").Contains("??") ? "" : (english ?? "").Trim());

    /// <summary>Các cách tra cứu của field: cấu hình hay dùng nhất trong Filter thật (ReportCatalog) lên đầu, sau đó các biến thể của header.xml (bỏ trùng).</summary>
    public static List<FieldLookup> Lookups(string field)
    {
        var list = new List<FieldLookup>();
        if (Catalog.Instance.Filter(field) is { Controller.Length: > 0 } f)
            list.Add(new FieldLookup(f.Controller, f.Reference, f.Key.Length > 0 ? f.Key : "status = '1'", f.Check.Length > 0 ? f.Check : "1 = 1",
                f.Info.Length > 0 ? f.Info : Catalog.InformationFor(field, f.Reference)));
        static string N(string s) => s.Replace(" ", "");
        foreach (var l in FieldDictionaryService.Instance.Lookups(field))
            if (!list.Any(x => x.Controller.Equals(l.Controller, StringComparison.OrdinalIgnoreCase) && x.Reference == l.Reference && N(x.Key) == N(l.Key) && N(x.Check) == N(l.Check)))
                list.Add(l);
        return list;
    }

    // ---- Chạy thử procedure ---------------------------------------------------------------------

    /// <summary>
    /// Chạy procedure với giá trị thử, trong một transaction luôn ROLLBACK (procedure có ghi bảng tạm / bảng thật cũng không để lại gì),
    /// trả về cấu trúc mọi result set + tối đa <paramref name="maxRows"/> dòng đầu mỗi set.
    /// </summary>
    public static async Task<List<QuickReportSet>> RunAsync(SqlConnection conn, QuickReportSpec spec, string sysDatabase, int maxRows, CancellationToken ct)
    {
        var sets = new List<QuickReportSet>();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = new SqlCommand(spec.ProcName, conn, tx) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
            foreach (var p in spec.Params) cmd.Parameters.AddWithValue("@" + p.Name, TestValue(p, sysDatabase));
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            do
            {
                if (rd.FieldCount == 0) continue;
                var cols = Enumerable.Range(0, rd.FieldCount).Select(i => (rd.GetName(i), FieldType(rd.GetFieldType(i)))).ToList();
                var rows = new List<string?[]>();
                var truncated = false;
                while (await rd.ReadAsync(ct))
                {
                    if (rows.Count >= maxRows) { truncated = true; break; }
                    var row = new string?[cols.Count];
                    for (var i = 0; i < cols.Count; i++) row[i] = rd.IsDBNull(i) ? null : Cell(rd.GetValue(i));
                    rows.Add(row);
                }
                sets.Add(new QuickReportSet(cols, rows, truncated));
            } while (await rd.NextResultAsync(ct));
        }
        finally
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { /* procedure tự commit/rollback hoặc kết nối đã đứt */ }
        }
        return sets;
    }

    private static object TestValue(QuickReportParam p, string sysDatabase)
    {
        string raw;
        if (p.IsFilter) raw = p.Test;
        else
        {
            var arg = p.Arg.Trim();
            raw = arg.ToLowerInvariant() switch
            {
                "@@unit" => p.Test,
                "'@@sysdatabasename'" or "@@sysdatabasename" => sysDatabase,
                "@@language" => "V",
                "@@userid" or "@@admin" => "1",
                "null" or "" => "\0",
                _ => arg.Length >= 2 && arg[0] == '\'' && arg[^1] == '\'' ? arg[1..^1].Replace("''", "'") : arg,
            };
        }
        if (raw == "\0") return DBNull.Value;
        if (IsDateSql(p.SqlType))
            return DateTime.TryParseExact(raw, new[] { "yyyy-MM-dd", "dd/MM/yyyy", "yyyyMMdd", "d/M/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : DBNull.Value;
        if (Regex.IsMatch(p.SqlType, @"^bit\b", RegexOptions.IgnoreCase)) return raw is "1" or "true" or "True";
        if (IsNumSql(p.SqlType))
            return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n : 0m;
        return raw;
    }

    private static string FieldType(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        if (t == typeof(DateTime) || t == typeof(DateTimeOffset)) return "DateTime";
        return Type.GetTypeCode(t) is TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.UInt16
            or TypeCode.UInt32 or TypeCode.UInt64 or TypeCode.Decimal or TypeCode.Double or TypeCode.Single ? "Decimal" : "";
    }

    private static string? Cell(object v) => v switch
    {
        DateTime d => d.ToString("yyyy-MM-dd"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        byte[] => "(binary)",
        _ => v.ToString() is { Length: > 80 } s ? s[..80] : v.ToString(),
    };

    // ---- Gợi ý cột lưới ------------------------------------------------------------------------------

    private static readonly Regex SystemColumn = new(@"^(sys\w*|stt_rec\d*|datetime[02]|user_id[02])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoSum = new(@"^(stt|gia|don_gia|gia\w*|ty_gia|ty_le\w*|thue_suat|nam|ky|thang|quy|tuoi)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Gợi ý header / độ rộng / định dạng / tổng cho cột kết quả. <paramref name="type"/> = kiểu suy từ dữ liệu ("" / DateTime / Decimal).
    /// Ưu tiên từ điển của "Tạo báo cáo" (ReportCatalog — rút từ 445 Grid thật), rồi header.xml, cuối cùng đoán theo tên cột.</summary>
    public static QuickReportColumn SuggestColumn(string name, string type)
    {
        var c = new QuickReportColumn { Name = name, Type = type, InGrid = !SystemColumn.IsMatch(name) };
        var low = name.ToLowerInvariant();
        var g = Catalog.Instance.Grid(name);
        if (g is { Hv.Length: > 0 }) { c.HeaderV = g.Hv; c.HeaderE = g.He.Length > 0 ? g.He : g.Hv; }
        else if (FieldDictionaryService.Instance.Header(name) is { } h) { c.HeaderV = h.V; c.HeaderE = h.E; }
        if (c.HeaderV.Length > 0) c.HeaderE = English(c.HeaderV, c.HeaderE);
        var width = g is { W: > 0 } ? g.W : FieldDictionaryService.Instance.Layout(name)?.Width;
        if (low == "stt" || (type == "Decimal" && g?.Type == "Int")) { c.Width = width ?? 60; c.Format = "####"; return c; }
        switch (type)
        {
            case "DateTime": c.Width = width ?? 100; c.Format = "@datetimeFormat"; break;
            case "Decimal":
                c.Width = width ?? 120;
                c.Format = Catalog.Instance.FormatFor(name, "Decimal");   // định dạng của Grid mẫu (đổi *Input → *View), không có thì theo tên cột
                c.Sum = !NoSum.IsMatch(low);
                break;
            default:
                c.Width = width ?? (low.StartsWith("ten") || low.StartsWith("dien_giai") ? 250 : low.StartsWith("ma_") || low.StartsWith("so_") || low == "tk" ? 100 : 150);
                break;
        }
        return c;
    }

    // ---- Kiểm tra + sinh file -------------------------------------------------------------------------

    public static string? Validate(QuickReportSpec s)
    {
        if (string.IsNullOrWhiteSpace(s.ProcName)) return "Chưa chọn procedure.";
        if (string.IsNullOrWhiteSpace(s.Controller)) return "Chưa nhập Controller (tên file Filter / Grid / Report).";
        if (s.Controller.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Controller chứa ký tự không hợp lệ cho tên file.";
        if (string.IsNullOrWhiteSpace(s.MainName)) return "Chưa nhập tên file Main.";
        if (s.MainName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Tên file Main chứa ký tự không hợp lệ.";
        if (string.IsNullOrWhiteSpace(s.TitleV)) return "Chưa nhập tiêu đề báo cáo.";
        foreach (var p in s.Params)
        {
            if (p.IsFilter && !Regex.IsMatch(p.Field, @"^[A-Za-z_]\w*$")) return $"Tham số @{p.Name}: tên field lọc '{p.Field}' không hợp lệ (chữ không dấu, số, gạch dưới).";
            if (!p.IsFilter && p.Arg.Trim().Length == 0) return $"Tham số @{p.Name}: chưa nhập giá trị hệ thống truyền vào.";
        }
        var clash = s.FilterParams.GroupBy(p => p.Field, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Select(p => p.Type).Distinct().Count() > 1);
        if (clash != null) return $"Field lọc '{clash.Key}' dùng cho nhiều tham số nhưng khác kiểu ô.";
        if (s.Columns.Count == 0) return "Chưa có cột lưới — bấm “▶ Chạy store” để lấy cột từ kết quả procedure.";
        if (s.GridColumns.Count == 0) return "Chưa tick cột nào hiện trên lưới.";
        var dup = s.Columns.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) return $"Kết quả procedure có 2 cột cùng tên '{dup.Key}' — Grid không phân biệt được, đặt lại alias trong procedure.";
        return null;
    }

    /// <summary>(file mẫu tương đối trong thư mục mẫu, file đích tương đối trong source).</summary>
    public static IReadOnlyList<(string Template, string Target)> PlannedFiles(QuickReportSpec s) => new[]
    {
        (@"App_Data\Controllers\Filter\report.xml", $@"App_Data\Controllers\Filter\{s.Controller}.xml"),
        (@"App_Data\Controllers\Grid\report.xml", $@"App_Data\Controllers\Grid\{s.Controller}.xml"),
        (@"App_Data\Controllers\Report\report.xml", $@"App_Data\Controllers\Report\{s.Controller}.xml"),
        (@"Main\report.aspx", $@"Main\{s.MainName}.aspx"),
    };

    public static string ExcelTarget(QuickReportSpec s) => $@"App_Data\Templates\Excel\{s.Controller}.xlsx";

    /// <summary>Báo cáo thường (không tag, không drilldown, không pivot, toolbar chuẩn) — các khối tuỳ chọn khác của mẫu tắt hết.</summary>
    private static readonly Dictionary<string, bool> Toggles = new()
    {
        // Filter
        ["TRANSACTION"] = false, ["FIELDTYPEUOM"] = false, ["FIELDTYPE"] = false, ["REPORTCIRCULAR"] = false, ["DRILLDOWN"] = false, ["TabControl"] = false,
        // Grid
        ["NOTREPORTTAGDRILLDOWN"] = true, ["NOTREPORTTAG"] = true, ["REPORTTAG"] = false, ["REPORTTAGRILLDOWN"] = false, ["PIVOT"] = false,
        ["SCRIPT"] = false, ["CSS"] = false, ["STANDARDBUTTON"] = true, ["CUSTOMBUTTON"] = false,
        // Report
        ["PRINTQUERY"] = false,
    };

    /// <summary>Dựng nội dung Filter / Grid / Report / Main (chưa ghi). Leftovers = các [#...#] trong mẫu mà code không biết (đã để trống).</summary>
    public List<(string Target, string Content)> Render(QuickReportSpec spec, string templateDir, out List<string> leftovers)
    {
        var values = BuildValues(spec);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        var result = new List<(string, string)>();
        foreach (var (template, target) in PlannedFiles(spec))
        {
            var path = Path.Combine(templateDir, template);
            if (!File.Exists(path)) throw new FileNotFoundException($"Thiếu file mẫu: {path}");
            var text = File.ReadAllText(path);
            foreach (var (name, on) in Toggles) text = QuickListService.ApplyToggle(text, name, on);
            var folder = template.Split('\\').Reverse().Skip(1).FirstOrDefault() ?? "";
            text = AnyMarker.Replace(text, m =>
            {
                var key = m.Groups[1].Value;
                if (values.TryGetValue(folder + ":" + key, out var v) || values.TryGetValue(key, out v)) return v;
                unknown.Add(m.Value);
                return "";
            });
            result.Add((target, CatalogCloneService.NormalizeNewLines(text)));
        }
        leftovers = unknown.ToList();
        return result;
    }

    /// <summary>Giá trị cho các [#TEN#]; khoá "Thư mục:TÊN" dùng riêng cho mẫu trong thư mục đó (FIELDS / VIEW mỗi file một khác).</summary>
    private static Dictionary<string, string> BuildValues(QuickReportSpec s)
    {
        var range = s.HasDateRange;
        var grid = s.GridColumns;
        return new Dictionary<string, string>
        {
            ["ID"] = A(s.Controller),
            ["CONTROLLER"] = A(s.Controller),
            ["TITLE"] = A(s.TitleV),
            ["TITLE2"] = A(s.TitleE.Length > 0 ? s.TitleE : s.TitleV),
            ["SUBTITLE"] = range ? "Từ ngày %s1 đến ngày %s2" : "",
            ["SUBTITLE2"] = range ? "From Date %s1 to %s2" : "",

            ["Filter:FIELDS"] = FilterFields(s),
            ["Filter:VIEW"] = FilterView(s),
            ["REPORTCOMMAND"] = ReportCommand(s),
            ["FOCUSTAB"] = $"'{(s.FilterParams.FirstOrDefault()?.Field ?? "mau_bc")}'",
            ["HIDDENFORMS"] = "g._hiddenForms = [];",
            ["ALTERSUBTITLE"] = range ? "g._alterTitle = [null, [['%s1', f.getItem('tu_ngay').value, true], ['%s2', f.getItem('den_ngay').value, true]]];" : "",

            ["Grid:FIELDS"] = string.Join("\n", grid.Select(GridField)).TrimStart(),
            ["Grid:VIEW"] = string.Join("\n", grid.Where(c => !c.Hidden).Select(c => $"\t\t\t<field name=\"{A(c.Name)}\"/>")).TrimStart(),

            ["PRINTFORM"] = PrintForm(s),
            ["PRINTFIELD"] = string.Join("\n", ReportVars(s).Select(v =>
                $"    <field name=\"{A(v.Code)}\" type=\"String\">\n      <header v=\"{A(v.V)}\" e=\"{A(v.E)}\"/>\n    </field>")).TrimStart(),   // thụt lề bằng dấu cách như mẫu Report
        };
    }

    /// <summary>Field duy nhất theo tên (nhiều tham số có thể cùng nhận một ô lọc).</summary>
    private static List<QuickReportParam> DistinctFilters(QuickReportSpec s) =>
        s.FilterParams.GroupBy(p => p.Field, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    private static string FilterFields(QuickReportSpec s)
    {
        var sb = new StringBuilder();
        void L(string line) => sb.Append(line).Append('\n');
        foreach (var p in DistinctFilters(s))
        {
            var hv = A(p.HeaderV.Length > 0 ? p.HeaderV : p.Field); var he = A(p.HeaderE.Length > 0 ? p.HeaderE : hv);
            var f = p.Field.ToLowerInvariant();
            if (s.HasDateRange && f is "tu_ngay" or "den_ngay")
            {
                L($"\t\t<field name=\"{p.Field}\" type=\"DateTime\" dataFormatString=\"@datetimeFormat\" allowNulls=\"false\" aliasName=\"{(f == "tu_ngay" ? "fromDate" : "toDate")}\" defaultValue=\"new Date()\">");
                L($"\t\t\t<header v=\"{hv}\" e=\"{he}\"></header>");
                if (f == "tu_ngay") L("\t\t\t<footer v=\"Từ/đến ngày\" e=\"Date from/to\"></footer>");
                L("\t\t</field>");
                continue;
            }
            var type = p.Type is "DateTime" or "Decimal" ? $" type=\"{p.Type}\"" : "";
            var fmt = p.Type == "DateTime" ? " dataFormatString=\"@datetimeFormat\"" : "";
            L($"\t\t<field name=\"{A(p.Field)}\"{type}{fmt}>");
            L($"\t\t\t<header v=\"{hv}\" e=\"{he}\"></header>");
            if (p.Type == "Decimal") L("\t\t\t<items style=\"Numeric\"/>");
            else if (p.Type == "" && p.Lookup is { } lk) L("\t\t\t" + Items(lk));
            L("\t\t</field>");
            if (p.Type == "" && p.Lookup is { Reference.Length: > 0 } r)
            {
                L($"\t\t<field name=\"{A(r.Reference)}\" readOnly=\"true\" external=\"true\" defaultValue=\"''\">");
                L("\t\t\t<header v=\"\" e=\"\"></header>");
                L("\t\t</field>");
            }
        }
        L("\t\t<field name=\"mau_bc\" clientDefault=\"10\">");
        L("\t\t\t<header v=\"Mẫu báo cáo\" e=\"Report Form\"></header>");
        L("\t\t\t<items style=\"DropDownList\">");
        L("\t\t\t\t<item value=\"10\">");
        L("\t\t\t\t\t<text v=\"Mẫu chuẩn\" e=\"Standard Form\"/>");
        L("\t\t\t\t</item>");
        L("\t\t\t</items>");
        L("\t\t\t<clientScript>&OnSelectionOutline;</clientScript>");
        sb.Append("\t\t</field>");
        return sb.ToString().TrimStart();
    }

    private static string Items(FieldLookup l)
    {
        var sb = new StringBuilder($"<items style=\"AutoComplete\" controller=\"{A(l.Controller)}\"");
        if (l.Reference != "") sb.Append($" reference=\"{A(l.Reference)}\"");
        if (l.Key != "") sb.Append($" key=\"{A(l.Key)}\"");
        if (l.Check != "") sb.Append($" check=\"{A(l.Check)}\"");
        if (l.Information != "") sb.Append($" information=\"{A(l.Information)}\"");
        return sb.Append("/>").ToString();
    }

    private static string FilterView(QuickReportSpec s)
    {
        var rows = new List<string> { "120, 40, 60, 100, 130, 0, 50, 0" };
        if (s.HasDateRange) rows.Add("1101----: [tu_ngay].Description, [tu_ngay], [den_ngay]");
        foreach (var p in DistinctFilters(s))
        {
            if (s.HasDateRange && p.Field.ToLowerInvariant() is "tu_ngay" or "den_ngay") continue;
            rows.Add(p.Type == "" && p.Lookup is { Reference.Length: > 0 } r
                ? $"110100--: [{p.Field}].Label, [{p.Field}], [{r.Reference}]"
                : $"110000--: [{p.Field}].Label, [{p.Field}]");
        }
        rows.Add("110000--: [mau_bc].Label, [mau_bc]");
        return "<view id=\"Dir\">\n" + string.Join("\n", rows.Select(r => $"\t\t\t<item value=\"{A(r)}\"/>")) + "\n\t\t</view>";
    }

    /// <summary>Lệnh Processing của Filter: dòng tham số (!1. trong Excel) rồi exec procedure — tham số truyền theo đúng thứ tự khai báo.</summary>
    private static string ReportCommand(QuickReportSpec s)
    {
        var fields = DistinctFilters(s).Select(p => $"@{p.Field} as {p.Field}").ToList();
        var args = s.Params.Select(p => p.IsFilter ? "@" + p.Field : p.Arg.Trim());
        return "select " + (fields.Count > 0 ? string.Join(", ", fields) : "1 as dummy") + "\n" +
               $"exec {s.ProcName}" + (s.Params.Count > 0 ? " " + string.Join(", ", args) : "");
    }

    private static string GridField(QuickReportColumn c)
    {
        var attrs = new StringBuilder($"name=\"{A(c.Name)}\" width=\"{(c.Width > 0 ? c.Width : 100)}\"");
        if (c.Type is "DateTime" or "Decimal") attrs.Append($" type=\"{c.Type}\"");
        if (c.Format.Length > 0) attrs.Append($" dataFormatString=\"{A(c.Format)}\"");
        attrs.Append(" allowSorting=\"true\" allowFilter=\"true\"");
        if (c.Sum && c.Type == "Decimal") attrs.Append(" aggregate=\"Sum\"");
        if (c.Hidden) attrs.Append(" hidden=\"true\"");
        var hv = c.HeaderV.Length > 0 ? c.HeaderV : c.Name;
        return $"\t\t<field {attrs}>\n\t\t\t<header v=\"{A(hv)}\" e=\"{A(c.HeaderE.Length > 0 ? c.HeaderE : hv)}\"></header>\n\t\t</field>";
    }

    private static string PrintForm(QuickReportSpec s)
    {
        var t = A(s.TitleV); var te = A(s.TitleE.Length > 0 ? s.TitleE : s.TitleV);
        return $"<form id=\"110\" templateFile=\"{A(s.Controller)}\" commandArgument=\"Excel\" urlImage=\"&e;\">\n" +
               $"      <header v=\"{t}\" e=\"{te}\"></header>\n" +
               "      <download>\n" +
               $"        <header v=\"{t}\" e=\"{te}\"/>\n" +
               "      </download>\n" +
               "    </form>\n" +
               "    &s;";
    }

    /// <summary>Biến ?h_xxx của mẫu Excel (khai trong Report xml).</summary>
    private static List<RptVar> ReportVars(QuickReportSpec s)
    {
        var vars = new List<RptVar> { new() { Code = "title", V = s.TitleV.ToUpperInvariant(), E = (s.TitleE.Length > 0 ? s.TitleE : s.TitleV).ToUpperInvariant() } };
        if (s.HasDateRange)
        {
            vars.Add(new RptVar { Code = "h_tu_ngay", V = "Từ ngày", E = "Date from" });
            vars.Add(new RptVar { Code = "h_den_ngay", V = "đến ngày", E = "to" });
        }
        foreach (var c in s.GridColumns.Where(c => !c.Hidden))
        {
            var hv = c.HeaderV.Length > 0 ? c.HeaderV : c.Name;
            vars.Add(new RptVar { Code = "h_" + c.Name, V = hv, E = c.HeaderE.Length > 0 ? c.HeaderE : hv });
        }
        return vars;
    }

    // ---- Mẫu Excel ----------------------------------------------------------------------------------

    /// <summary>Mẫu Excel theo bố cục các mẫu zrpt_* của Fast (giống "Tạo báo cáo"): tên đơn vị, tiêu đề, dòng từ ngày – đến ngày, header ?h_ ở dòng 9,
    /// dữ liệu !N. ở dòng 10, dòng tổng SUMIF theo systotal (khi procedure có trả cột systotal), khối chữ ký.</summary>
    public static byte[] ExcelBytes(QuickReportSpec s)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "bcode_qr_" + Guid.NewGuid().ToString("N") + ".xlsx");
        try
        {
            new ExcelTemplateWriter().Write(ExcelLayout(s), tmp);
            return File.ReadAllBytes(tmp);
        }
        finally { try { File.Delete(tmp); } catch { /* file tạm */ } }
    }

    private static double Chars(int px) => Math.Round((px / 7.0 + 0.43) * 100) / 100;

    private static string ColName(int c) { var r = ""; while (c > 0) { var m = (c - 1) % 26; r = (char)(65 + m) + r; c = (c - m - 1) / 26; } return r; }

    public static SheetLayout ExcelLayout(QuickReportSpec s)
    {
        var data = "!" + (2 + Math.Max(0, s.ResultSet)) + ".";
        var fields = s.GridColumns.Select(c => (c.Name, IsNum: c.Type == "Decimal", IsDate: c.Type == "DateTime", c.Hidden, Width: c.Width > 0 ? c.Width : 100, Sum: c.Sum && c.Type == "Decimal")).ToList();
        var hasTotal = s.Columns.Any(c => c.Name.Equals("systotal", StringComparison.OrdinalIgnoreCase));
        if (hasTotal && !fields.Any(f => f.Name.Equals("systotal", StringComparison.OrdinalIgnoreCase))) fields.Add(("systotal", true, false, true, 60, false));
        var n = Math.Max(fields.Count, 1);
        var l = new SheetLayout();
        void Put(int r, int c, string v, string k, bool f = false) => l.Cells.Add(new SheetCell { R = r, C = c, V = v, K = k, F = f });
        void Merge(int r1, int c1, int r2, int c2) => l.Merges.Add(new[] { r1, c1, r2, c2 });

        l.ColWidths = Enumerable.Range(0, Math.Max(n, 6)).Select(i => i < fields.Count ? Chars(fields[i].Width) : 14.7).ToList();
        l.ColHidden = Enumerable.Range(0, Math.Max(n, 6)).Select(i => i < fields.Count && fields[i].Hidden).ToList();
        for (var i = 1; i <= 4; i++) Put(i, 1, "?Entity_Line" + i, i == 1 ? "entity1" : "plain");
        Put(6, 1, "?title", "title"); Merge(6, 1, 6, n);
        if (s.HasDateRange) { Put(7, 1, "#?h_tu_ngay + + !1.tu_ngay + + ?h_den_ngay + + !1.den_ngay", "sub"); Merge(7, 1, 7, n); }
        l.Rows.Add(new SheetRow { R = 9, H = 30 });
        for (var i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (!f.Name.Equals("systotal", StringComparison.OrdinalIgnoreCase)) Put(9, i + 1, "?h_" + f.Name, f.IsNum ? "hdrN" : "hdr");
            Put(10, i + 1, data + f.Name, f.IsDate ? "dataD" : f.IsNum ? "dataN" : "data");
        }
        l.Rows.Add(new SheetRow { R = 11, Hidden = true });
        var sys = fields.FindIndex(f => f.Name.Equals("systotal", StringComparison.OrdinalIgnoreCase));
        if (sys >= 0)
            for (var i = 0; i < fields.Count; i++)
            {
                if (!fields[i].Sum) continue;
                var col = ColName(i + 1); var sc = ColName(sys + 1);
                Put(12, i + 1, $"=SUMIF(${sc}10:${sc}11,1,{col}10:{col}11)", "total", f: true);
            }
        Signature(l, n);
        l.Landscape = fields.Count(f => !f.Hidden) > 8;
        l.BoldComment = sys >= 0;
        l.DataRow = 10;
        return l;
    }

    private static void Signature(SheetLayout l, int n)
    {
        if (n < 3) return;
        void Put(int r, int c, string v, string k) => l.Cells.Add(new SheetCell { R = r, C = c, V = v, K = k });
        void Merge(int r1, int c1, int r2, int c2) => l.Merges.Add(new[] { r1, c1, r2, c2 });
        var a = Math.Max(1, (int)Math.Round(n * 4 / 13.0));
        var b = Math.Min(n - 1, a + Math.Max(1, (int)Math.Round(n * 5 / 13.0)));
        if (n > 20) { a = (int)Math.Ceiling(n / 3.0); b = Math.Min(n - 1, a + (int)Math.Floor((n - a) / 2.0)); }
        Put(13, b + 1, "?reportDate", "sub"); Merge(13, b + 1, 13, n);
        foreach (var (c1, c2, t, sg) in new[] { (1, a, "?preparedBy", "?signatureFullname"), (a + 1, b, "?chiefAccountant", "?signatureFullname"), (b + 1, n, "?director", "?signatureFullnameSeal") })
        {
            Put(14, c1, t, "sig"); Merge(14, c1, 14, c2);
            Put(15, c1, sg, "sigName"); Merge(15, c1, 15, c2);
        }
        l.Rows.Add(new SheetRow { R = 16, H = 60 });
        Put(17, 1, "?preparedByName", "sig"); Merge(17, 1, 17, a);
        Put(17, a + 1, "?chiefAccountantName", "sig"); Merge(17, a + 1, 17, b);
        Put(17, b + 1, "?directorName", "sig"); Merge(17, b + 1, 17, n);
    }

    // ---- Ghi vào source ------------------------------------------------------------------------------

    /// <summary>Ghi file vào source; file đã có thì chép bản cũ sang <paramref name="backupDir"/> trước (giữ đường dẫn tương đối). Trả về các file đã ghi.</summary>
    public static List<string> Write(IEnumerable<(string Target, byte[] Bytes)> files, string sourceRoot, string backupDir)
    {
        var written = new List<string>();
        foreach (var (target, bytes) in files)
        {
            var path = Path.Combine(sourceRoot, target);
            if (File.Exists(path))
            {
                var bak = Path.Combine(backupDir, target);
                Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                File.Copy(path, bak, overwrite: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            written.Add(path);
        }
        return written;
    }

    /// <summary>Một file sẽ ghi: so với bản đang có trong source (giống "Lưu vào source…" của Tạo báo cáo). <see cref="NewDir"/> = thư mục đích chưa có, sẽ tạo.</summary>
    public sealed record FilePlan(string Kind, string Rel, string Target, bool Exists, bool Same, bool NewDir, long Size, long? TargetSize, DateTime? TargetTime,
        int Added, int Removed, bool IsText, IReadOnlyList<object> Diff);

    /// <summary>So sánh từng file với source bằng chính bộ so sánh của "Tạo báo cáo" (<c>ReportFilesDeployService.Plan</c>: mới / trùng hệt / ghi đè + diff).
    /// Khác ở một điểm: thư mục đích chưa có (vd App_Data\Templates\Excel) không chặn cả lượt mà chỉ đánh dấu <see cref="FilePlan.NewDir"/>.</summary>
    public static (string? Problem, List<FilePlan> Files) Plan(string sourceRoot, IReadOnlyList<(string Kind, string Rel, byte[] Bytes, bool IsText)> files)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot)) return ("Chưa chọn thư mục source để lưu.", new());
        if (!Directory.Exists(sourceRoot)) return ("Không truy cập được thư mục source: " + sourceRoot, new());
        var ready = files.Where(f => Directory.Exists(Path.GetDirectoryName(Path.Combine(sourceRoot, f.Rel)))).ToList();
        var plan = new RbDeploy.ReportFilesDeployService().Plan(sourceRoot, ready.Select(f => new RbDeploy.BuildFile(f.Kind, f.Rel, f.Bytes, f.IsText)).ToList());
        if (plan.Problem is not null) return (plan.Problem, new());
        var list = files.Select(f =>
        {
            var p = plan.Files.FirstOrDefault(x => x.Kind == f.Kind);
            return p is null
                ? new FilePlan(f.Kind, f.Rel, Path.Combine(sourceRoot, f.Rel), false, false, true, f.Bytes.Length, null, null, 0, 0, f.IsText, Array.Empty<object>())
                : new FilePlan(p.Kind, p.Rel, p.Target, p.Exists, p.Same, false, p.Size, p.TargetSize, p.TargetTime, p.Added, p.Removed, p.IsText, p.Diff.Cast<object>().ToList());
        }).ToList();
        return (null, list);
    }

    public static byte[] TextBytes(string content) => Utf8Bom.GetPreamble().Concat(Utf8Bom.GetBytes(content)).ToArray();
}
