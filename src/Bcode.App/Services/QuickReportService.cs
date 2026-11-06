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
        // @SysDatabase (procedure kiểu fs_TransList của Fast) cũng là tên database Sys — để làm ô lọc thì chạy thử truyền '' → procedure ghép "[]..bảng" → lỗi 1038.
        "sysdatabasename" or "sysdatabase" or "sysdb" or "sys_database" => "'@@sysDatabaseName'",
        "appdatabasename" or "appdatabase" or "appdb" or "app_database" => "'@@appDatabaseName'",
        // @Controller / @TableName của bộ trường động (FastBusiness$Report$GetDynamicFields / GetDynamicKey): controller của báo cáo; bảng lọc động để trống như mặc định của procedure.
        "controller" => "'@@controller'",
        "tablename" or "table_name" => "''",
        "language" or "lang" => "@@language",
        "userid" or "user_id" => "@@userID",
        "admin" => "@@admin",
        _ => null,
    };

    /// <summary>Một tham số procedure đọc từ database. <see cref="BaseType"/> = kiểu hệ thống (kiểu alias như sysname quy về nvarchar);
    /// <see cref="MaxLength"/> = số ký tự / byte, -1 = max; <see cref="Default"/> = giá trị mặc định ghi trong CREATE PROCEDURE (null = không có).</summary>
    public sealed record ProcParam(string Name, string SqlType, string BaseType, int MaxLength, byte Precision, byte Scale, bool IsOutput, bool IsTable, string? Default)
    {
        public bool HasDefault => Default != null;
    }

    /// <summary>Tham số (theo thứ tự khai báo) + định nghĩa của procedure trong App Data. Định nghĩa null khi procedure mã hoá / không có quyền xem.</summary>
    public sealed record ProcInfo(List<ProcParam> Params, string? Definition);

    /// <summary>Tham số của procedure (theo thứ tự khai báo) từ sys.parameters của App Data.</summary>
    public static async Task<List<ProcParam>> ReadParamsAsync(SqlConnection conn, string proc, SqlTransaction? tx = null) =>
        (await ReadProcAsync(conn, proc, tx)).Params;

    public static async Task<ProcInfo> ReadProcAsync(SqlConnection conn, string proc, SqlTransaction? tx = null, CancellationToken ct = default)
    {
        const string sql = @"select p.name, type_name(p.user_type_id), type_name(p.system_type_id), p.max_length, p.precision, p.scale, p.is_output,
  cast(case when t.is_table_type = 1 then 1 else 0 end as bit)
from sys.parameters p join sys.types t on t.user_type_id = p.user_type_id
where p.object_id = object_id(@proc) and p.parameter_id > 0 order by p.parameter_id;
select object_definition(object_id(@proc));";
        await using var cmd = new SqlCommand(sql, conn, tx);
        cmd.Parameters.Add("@proc", SqlDbType.NVarChar, 776).Value = proc;
        var raw = new List<(string Name, string User, string Base, int Len, byte Prec, byte Scale, bool Out, bool Table)>();
        string? def = null;
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
        {
            while (await rd.ReadAsync(ct))
                raw.Add((rd.GetString(0).TrimStart('@'), rd.GetString(1), rd.IsDBNull(2) ? rd.GetString(1) : rd.GetString(2),
                    rd.GetInt16(3), rd.GetByte(4), rd.GetByte(5), rd.GetBoolean(6), rd.GetBoolean(7)));
            if (await rd.NextResultAsync(ct) && await rd.ReadAsync(ct) && !rd.IsDBNull(0)) def = rd.GetString(0);
        }
        var defaults = def is null ? new Dictionary<string, string>() : ParamDefaults(def);
        return new ProcInfo(raw.Select(x =>
        {
            // kiểu alias (sysname, kiểu tự định nghĩa) → kiểu gốc; kiểu bảng / kiểu CLR giữ tên
            var type = x.Table || x.Base.ToLowerInvariant() is "hierarchyid" or "geometry" or "geography" ? x.User : x.Base;
            var low = type.ToLowerInvariant();
            var len = low is "nvarchar" or "nchar" ? (x.Len < 0 ? -1 : x.Len / 2) : x.Len;
            var full = low switch
            {
                "varchar" or "char" or "varbinary" or "binary" or "nvarchar" or "nchar" => $"{type}({(len < 0 ? "max" : len.ToString())})",
                "decimal" or "numeric" => $"{type}({x.Prec}, {x.Scale})",
                "datetime2" or "time" or "datetimeoffset" when x.Scale != 7 => $"{type}({x.Scale})",
                _ => type,
            };
            return new ProcParam(x.Name, full, low, len, x.Prec, x.Scale, x.Out, x.Table,
                defaults.TryGetValue(x.Name, out var d) ? d : x.Table ? "" : null);   // tham số kiểu bảng bỏ trống = bảng rỗng
        }).ToList(), def);
    }

    private static bool IsDateSql(string t) => Regex.IsMatch(t, @"^(small)?date(time2?)?|^date\b", RegexOptions.IgnoreCase);
    private static bool IsNumSql(string t) => Regex.IsMatch(t, @"^(numeric|decimal|money|smallmoney|float|real|int|bigint|smallint|tinyint)\b", RegexOptions.IgnoreCase);

    /// <summary>Giá trị mặc định khai trong CREATE PROCEDURE → giá trị thử hiển thị ('' / N'abc' → abc, NULL → trống).</summary>
    public static string DefaultTest(string? def)
    {
        if (string.IsNullOrWhiteSpace(def) || def.Trim().Equals("null", StringComparison.OrdinalIgnoreCase)) return "";
        var d = def.Trim();
        if (d.StartsWith("N'", StringComparison.OrdinalIgnoreCase)) d = d[1..];
        return d.Length >= 2 && d[0] == '\'' && d[^1] == '\'' ? d[1..^1].Replace("''", "'") : d;
    }

    /// <summary>Gợi ý vai trò / field / kiểu / tra cứu cho một tham số procedure. Tiêu đề + tra cứu ưu tiên từ điển của "Tạo báo cáo"
    /// (ReportCatalog — rút từ Filter thật của source FastBusiness), rồi mới tới header.xml.</summary>
    public static QuickReportParam SuggestParam(ProcParam m)
    {
        var p = SuggestParam(m.Name, m.SqlType, m.IsOutput || m.IsTable);
        if (m.IsTable) p.Arg = "null";   // kiểu bảng: không truyền, procedure nhận bảng rỗng
        if (p.IsFilter && m.HasDefault) p.Test = DefaultTest(m.Default);
        else if (p.IsFilter && p.Type == "DateTime" && p.Test.Length == 0) p.Test = $"{DateTime.Today:yyyy-MM-dd}";
        return p;
    }

    public static QuickReportParam SuggestParam(string name, string sqlType, bool isOutput)
    {
        var p = new QuickReportParam { Name = name, SqlType = sqlType };
        var low = name.ToLowerInvariant();
        if (isOutput) { p.Role = "system"; p.Arg = "null"; return p; }
        if (SystemArg(name) is { } arg) { p.Role = "system"; p.Arg = arg; return p; }

        if (DateFromNames.Contains(low)) { SetField(p, "tu_ngay"); return p; }
        if (DateToNames.Contains(low)) { SetField(p, "den_ngay"); return p; }

        var dict = FieldDictionaryService.Instance;
        // @ma_kh → ma_kh; @Customer (đặt theo tên controller) → field mã tra cứu bằng controller đó.
        SetField(p, low.Contains('_') || Catalog.Instance.Filter(low) != null || dict.Header(low) != null ? low : dict.FieldForController(name) ?? low);
        return p;
    }

    /// <summary>Đặt field lọc cho tham số + tiêu đề / kiểu ô / tra cứu / giá trị thử theo field đó.</summary>
    public static void SetField(QuickReportParam p, string field)
    {
        p.Role = "filter"; p.Arg = ""; p.Field = field; p.HeaderV = p.HeaderE = ""; p.Lookup = null;
        switch (field.ToLowerInvariant())
        {
            case "tu_ngay": p.Type = "DateTime"; p.HeaderV = "Từ ngày"; p.HeaderE = "Date from"; p.Test = $"{DateTime.Today:yyyy}-01-01"; return;
            case "den_ngay": p.Type = "DateTime"; p.HeaderV = "Đến ngày"; p.HeaderE = "Date to"; p.Test = $"{DateTime.Today:yyyy-MM-dd}"; return;
        }
        p.Type = IsDateSql(p.SqlType) ? "DateTime" : IsNumSql(p.SqlType) ? "Decimal" : "";
        if (FilterHeader(field) is { } h) { p.HeaderV = h.V; p.HeaderE = h.E; }
        if (p.Type == "") p.Lookup = Lookups(field).FirstOrDefault();
        p.Test = p.Type == "Decimal" ? NumericTest(p.Name) ?? NumericTest(field) ?? "0" : "";
    }

    private static readonly Regex MonthName = new(@"^(?:(?:from|to|end|start)?(?:month|period|thang|ky)(?:from|to|\d)?|(?:thang|ky)_?(?:tu|den|\d)|p(?:from|to|\d))$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex YearName = new(@"^(?:(?:from|to|fiscal)?(?:year|nam)(?:from|to|\d)?|nam_?(?:tu|den|tc|\d)|y(?:from|to|\d))$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex QuarterName = new(@"^(?:(?:from|to)?(?:quarter|quy)(?:from|to|\d)?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Giá trị thử hợp lý cho tham số số theo tên: tháng / kỳ = tháng hiện tại, năm = năm hiện tại, quý = quý hiện tại
    /// (truyền 0 thì procedure dựng ngày từ kỳ 0 năm 0 → lỗi chuyển kiểu ngày).</summary>
    public static string? NumericTest(string name)
    {
        var t = DateTime.Today;
        if (MonthName.IsMatch(name)) return t.Month.ToString();
        if (YearName.IsMatch(name)) return t.Year.ToString();
        if (QuarterName.IsMatch(name)) return ((t.Month - 1) / 3 + 1).ToString();
        return null;
    }

    // ---- Đọc khai báo procedure ------------------------------------------------------------------

    /// <summary>Thay chú thích (-- … / /* … */) bằng dấu cách, giữ nguyên chuỗi '…' và [tên] — vị trí ký tự không đổi.</summary>
    public static string StripComments(string sql)
    {
        var sb = new StringBuilder(sql);
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'' || c == '[' || c == '"')
            {
                var close = c == '[' ? ']' : c;
                for (i++; i < sql.Length; i++)
                    if (sql[i] == close) { if (i + 1 < sql.Length && sql[i + 1] == close) i++; else break; }
            }
            else if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                for (; i < sql.Length && sql[i] != '\n'; i++) sb[i] = ' ';
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var depth = 0;
                for (; i < sql.Length; i++)
                {
                    if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { depth++; sb[i] = sb[i + 1] = ' '; i++; continue; }
                    if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/') { depth--; sb[i] = sb[i + 1] = ' '; i++; if (depth == 0) break; continue; }
                    if (sql[i] != '\n' && sql[i] != '\r') sb[i] = ' ';
                }
            }
        }
        return sb.ToString();
    }

    private static readonly Regex ProcHead = new(@"\b(?:CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?PROC(?:EDURE)?\s+(?:(?:\[[^\]]*\]|""[^""]*""|[\w$#@]+)\s*\.\s*)*(?:\[[^\]]*\]|""[^""]*""|[\w$#@]+)(?:\s*;\s*\d+)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Giá trị mặc định của từng tham số (<c>@p int = 0</c>, <c>@s varchar(10) = ''</c>…) đọc từ phần khai báo của CREATE PROCEDURE.</summary>
    public static Dictionary<string, string> ParamDefaults(string definition)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var sql = StripComments(definition);
        var m = ProcHead.Match(sql);
        if (!m.Success) return result;
        var i = m.Index + m.Length;
        while (i < sql.Length && char.IsWhiteSpace(sql[i])) i++;
        var wrapped = i < sql.Length && sql[i] == '(';
        if (wrapped) i++;
        var segments = new List<string>();
        var cur = new StringBuilder();
        var depth = 0;
        bool Word(int at, string w) =>
            at + w.Length <= sql.Length && string.Compare(sql, at, w, 0, w.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
            (at == 0 || !(char.IsLetterOrDigit(sql[at - 1]) || sql[at - 1] is '_' or '@' or '$' or '#')) &&
            (at + w.Length == sql.Length || !(char.IsLetterOrDigit(sql[at + w.Length]) || sql[at + w.Length] is '_' or '$' or '#'));
        for (; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c is '\'' or '[' or '"')
            {
                var close = c == '[' ? ']' : c;
                var start = i;
                for (i++; i < sql.Length; i++)
                    if (sql[i] == close) { if (i + 1 < sql.Length && sql[i + 1] == close) i++; else break; }
                cur.Append(sql, start, Math.Min(i, sql.Length - 1) - start + 1);
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') { if (depth == 0 && wrapped) break; depth--; }
            else if (depth == 0 && c == ',') { segments.Add(cur.ToString()); cur.Clear(); continue; }
            else if (depth == 0 && !wrapped && (Word(i, "AS") || Word(i, "WITH") || Word(i, "FOR")))
            {
                // "@p AS int" (AS ngay sau tên tham số) là cú pháp khai báo, không phải đầu thân procedure
                if (!(Word(i, "AS") && Regex.IsMatch(cur.ToString(), @"^\s*@[\w@$#]+\s*$"))) break;
            }
            cur.Append(c);
        }
        segments.Add(cur.ToString());
        foreach (var seg in segments)
        {
            var pm = Regex.Match(seg, @"^\s*@([\w@$#]+)", RegexOptions.Singleline);
            if (!pm.Success) continue;
            var eq = TopLevelIndexOf(seg, '=', pm.Length);
            if (eq < 0) continue;
            var d = Regex.Replace(seg[(eq + 1)..], @"\s+(OUTPUT|OUT|READONLY)(\s+(OUTPUT|OUT|READONLY))*\s*$", "", RegexOptions.IgnoreCase).Trim();
            if (d.Length > 0) result[pm.Groups[1].Value] = d;
        }
        return result;
    }

    /// <summary>Vị trí ký tự <paramref name="ch"/> đầu tiên ngoài chuỗi '…' và ngoài ngoặc, tính từ <paramref name="start"/>.</summary>
    private static int TopLevelIndexOf(string s, char ch, int start)
    {
        var depth = 0;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\'') { for (i++; i < s.Length; i++) if (s[i] == '\'') { if (i + 1 < s.Length && s[i + 1] == '\'') i++; else break; } continue; }
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ch && depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// Tham số chuỗi bị procedure ghép thẳng vào câu SQL động (<c>'… ' + @Order + ', …'</c>) mà không chỗ nào kiểm tra rỗng
    /// (<c>@Order &lt;&gt; ''</c>, <c>len(@Order)</c>, <c>isnull(@Order…</c>…) — để trống thì câu ghép ra sai cú pháp (vd <c>order by a, , b</c>).
    /// </summary>
    public static List<string> UnguardedConcatParams(string? definition, IEnumerable<string> names)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(definition)) return list;
        var body = ProcBody(definition);
        foreach (var name in names)
            if (UnguardedConcats(body, name).Any()) list.Add(name);
        return list;
    }

    /// <summary>Thân procedure đã bỏ chú thích (phần sau tên procedure).</summary>
    private static string ProcBody(string definition)
    {
        var sql = StripComments(definition);
        return ProcHead.Match(sql) is { Success: true } h ? sql[(h.Index + h.Length)..] : sql;
    }

    /// <summary>
    /// Các chỗ procedure ghép thẳng tham số vào chuỗi mà không có IF / CASE WHEN / WHILE KIỂM TRA RỖNG tham số đó (<c>@x &lt;&gt; ''</c>, <c>= ''</c>,
    /// <c>len(@x)</c>, <c>isnull(@x…</c>, <c>@x is null</c>, <c>@a + @x &lt;&gt; ''</c>) ngay trên cùng dòng hoặc 3 dòng trước. <c>IF @x = 0 … + @x</c>
    /// không tính là kiểm tra (chuỗi trống so với 0 vẫn đúng → ghép ra sai cú pháp).
    /// </summary>
    private static IEnumerable<Match> UnguardedConcats(string body, string name)
    {
        var n = Regex.Escape(name);
        // procedure tự gán lại giá trị (SET @x = … / SELECT @x = …) thì không còn là giá trị người dùng truyền
        if (Regex.IsMatch(body, $@"\b(set|select)\s+@{n}\s*=(?!=)", RegexOptions.IgnoreCase)) yield break;
        var guard = new Regex(
            $@"\b(IF|WHEN|WHILE)\b[^\n]*?(?:\w+\s*\(\s*)*@{n}\b\s*\)*\s*(?:\+[^\n]*?)?(?:<>|!=|>|=)\s*N?''(?!')" +
            $@"|\b(IF|WHEN|WHILE)\b[^\n]*?\b(len|datalength|isnull|coalesce|nullif)\s*\([^\n]*?@{n}\b" +
            $@"|\b(IF|WHEN|WHILE)\b[^\n]*?@{n}\b\s*\)*\s+is\s+(not\s+)?null" +
            $@"|\b(IF|WHEN|WHILE)\b[^\n]*?N?''\s*(?:<>|!=|<|=)\s*(?:\w+\s*\(\s*)*@{n}\b", RegexOptions.IgnoreCase);
        foreach (Match m in Regex.Matches(body, $@"\+\s*(?:[\w.$\[\]]+\s*\(\s*)*@{n}\b(?!\s*=)|(?<![=<>!])\B@{n}\b\s*\)*\s*\+", RegexOptions.IgnoreCase))
        {
            var from = m.Index;
            for (var lines = 0; from > 0 && lines < 4; from--) if (body[from - 1] == '\n') lines++;
            var end = body.IndexOf('\n', m.Index + m.Length);
            if (guard.IsMatch(body[from..(end < 0 ? body.Length : end)])) continue;   // gồm cả "IF @a + @b <> ''" và CASE WHEN … THEN … ELSE … + @x
            yield return m;
        }
    }

    private static readonly Regex ByList = new(@"\b(?:order|group)\s+by\s+((?:(?:[A-Za-z_]\w*\.)?[A-Za-z_]\w*(?:\s+(?:asc|desc))?\s*,\s*)*(?:[A-Za-z_]\w*\.)?[A-Za-z_]\w*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Cột có trong các danh sách order by / group by viết sẵn của procedure, bỏ tiền tố bí danh — danh sách trên dòng có #report
    /// (bảng kết quả báo cáo Fast, nơi @Order được dùng) trước, rồi theo thứ tự gặp.</summary>
    private static List<string> ByColumns(string body) => ByList.Matches(body).Cast<Match>()
        .OrderBy(m => body.IndexOf("#report", m.Index - Math.Min(m.Index, 300), Math.Min(m.Index, 300) + m.Length, StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : 1)
        .SelectMany(m => m.Groups[1].Value.Split(',').Select(c => Regex.Replace(c.Trim(), @"\s+(asc|desc)$", "", RegexOptions.IgnoreCase)))
        .Select(c => c.Contains('.') ? c[(c.LastIndexOf('.') + 1)..] : c)
        .Where(c => !Regex.IsMatch(c, "^(case|select|null|newid|row_number|by|order|group)$", RegexOptions.IgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Dòng chứa chỗ ghép <paramref name="m"/>.</summary>
    private static string LineOf(string body, Match m)
    {
        var start = m.Index == 0 ? 0 : body.LastIndexOf('\n', m.Index - 1) + 1;
        var end = body.IndexOf('\n', m.Index);
        return body[start..(end < 0 ? body.Length : end)];
    }

    private static readonly Regex OrderName = new(@"^(order|orderby|order_by|sort|sortby|sort_by|sap_xep|thu_tu)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Giá trị thử thay cho chuỗi trống của tham số mà procedure ghép thẳng vào SQL động không kiểm tra rỗng (null = không đoán được):
    /// ghép vào ORDER BY hoặc tên kiểu @Order / @Sort → <see cref="OrderByFiller"/>; procedure tự so sánh tham số với hằng
    /// (<c>IF @Report = 'ma_phi'</c>, <c>RTRIM(@GroupBy) = 'loai_vt'</c>) → hằng đầu tiên. <paramref name="why"/> = mô tả ngắn để báo.
    /// </summary>
    public static string? BlankFallback(string? definition, string name, out string why) => BlankFallback(definition, name, out why, out _);

    /// <param name="asSystem">true = tham số sắp xếp: nên là tham số hệ thống truyền hằng (như Filter gốc của Fast), không phải ô lọc.</param>
    public static string? BlankFallback(string? definition, string name, out string why, out bool asSystem)
    {
        why = ""; asSystem = false;
        if (string.IsNullOrEmpty(definition)) return null;
        var body = ProcBody(definition);
        if (!UnguardedConcats(body, name).Any()) return null;
        if (IsOrderByConcat(definition, name)) { why = "ghép vào ORDER BY"; asSystem = true; return OrderByFiller; }
        var n = Regex.Escape(name);
        // procedure tự so sánh tham số với hằng: IF @Report = 'ma_phi' / RTRIM(@GroupBy) = 'loai_vt' / IF @CalculateTransfer = 0
        var lit = Regex.Match(body, $@"\b(?:IF|WHEN|AND|OR|WHILE)\b[^\n]*?(?:\w+\s*\(\s*)*@{n}\b\s*\)*\s*=\s*(?:N?'((?:[^']|'')+)'|(-?\d+(?:\.\d+)?)\b)", RegexOptions.IgnoreCase);
        if (lit.Success) { why = "giá trị store tự so sánh"; return (lit.Groups[1].Success ? lit.Groups[1].Value.Replace("''", "'") : lit.Groups[2].Value).Trim(); }
        var hits = UnguardedConcats(body, name).ToList();
        if (OrderName.IsMatch(name))
        {
            // danh sách sắp xếp đưa sang chỗ khác (vd FastBusiness$System$UpdateOrderField → create index … (@Order)) cần tên cột thật:
            // cột chính procedure order by / group by, trừ cột đã có trên dòng ghép @Order (order by trùng cột → lỗi 169)
            why = "tham số sắp xếp"; asSystem = true;
            var used = string.Join("\n", hits.Select(m => LineOf(body, m)));
            return ByColumns(body).FirstOrDefault(c => !Regex.IsMatch(used, $@"(?<![\w.]){Regex.Escape(c)}\b|\.{Regex.Escape(c)}\b", RegexOptions.IgnoreCase))
                ?? OrderByFiller;
        }
        // ghép vào biểu thức của SQL động: "' + REPLACE(@Status, …) + ' = ''1''" → lấy hằng bên kia phép so sánh
        foreach (var m in hits)
        {
            var line = LineOf(body, m);
            var rest = line[(m.Index - (m.Index == 0 ? 0 : body.LastIndexOf('\n', m.Index - 1) + 1) + m.Length)..];
            var cmp = Regex.Match(rest, @"^[^+]*\+\s*'\s*(?:<>|!=|<=|>=|=|<|>)\s*(?:''((?:[^']|'''')*)''|(-?\d+(?:\.\d+)?))", RegexOptions.IgnoreCase);
            if (cmp.Success) { why = "hằng store so sánh trong SQL động"; return cmp.Groups[1].Success ? cmp.Groups[1].Value.Replace("''''", "'") : cmp.Groups[2].Value; }
        }
        return null;
    }

    /// <summary>Tham số số được procedure dùng làm số chia (<c>tuoi / @PeriodCycle</c>, <c>% @n</c>) → truyền 0 sẽ lỗi chia cho 0.</summary>
    public static bool IsDivisor(string? definition, string name) =>
        !string.IsNullOrEmpty(definition) &&
        Regex.IsMatch(ProcBody(definition), $@"[/%]\s*(?:\w+\s*\(\s*)*@{Regex.Escape(name)}\b(?!\s*[+\-*/])", RegexOptions.IgnoreCase);

    /// <summary>
    /// Gợi ý lúc Nạp cho các ô lọc còn trống / 0 mà procedure dùng sao cho trống sẽ lỗi (xem <see cref="BlankFallback"/>, <see cref="IsDivisor"/>):
    /// tham số sắp xếp → tham số hệ thống truyền hằng; còn lại → giá trị thử. Trả mô tả từng thay đổi; <paramref name="risky"/> = tham số
    /// vẫn bị ghép thẳng mà không đoán được giá trị.
    /// </summary>
    public static List<string> SuggestBlankValues(ProcInfo info, List<QuickReportParam> ps, out List<string> risky)
    {
        var notes = new List<string>();
        risky = new List<string>();
        foreach (var p in ps.Where(p => p.IsFilter))
        {
            if (p.Type == "Decimal" && p.Test.Trim() is "0" or "" && IsDivisor(info.Definition, p.Name))
            {
                p.Test = "1"; notes.Add($"@{p.Name} = 1 (số chia)");
                continue;
            }
            if (p.Type != "" || p.Test.Trim().Length > 0) continue;
            if (BlankFallback(info.Definition, p.Name, out var why, out var asSystem) is { } fill)
            {
                if (asSystem) { p.Role = "system"; p.Arg = $"'{fill.Replace("'", "''")}'"; p.Field = ""; p.Lookup = null; notes.Add($"@{p.Name} = '{fill}' ({why}, tham số hệ thống)"); }
                else { p.Test = fill; notes.Add($"@{p.Name} = {fill} ({why})"); }
            }
            else if (UnguardedConcatParams(info.Definition, new[] { p.Name }).Count > 0) risky.Add(p.Name);
        }
        return notes;
    }

    /// <summary>Giá trị thay khi tham số trống bị ghép vào ORDER BY của câu SQL động.</summary>
    public const string OrderByFiller = "(select null)";

    // chuỗi đứng ngay trước dấu + đang mở ở mệnh đề ORDER BY: "… order by ngay_ct, so_ct,'" / "' ORDER BY a.ngay_ct, a.so_ct0, '"
    private static readonly Regex OrderByTail = new(@"\border\s+by\b[^']*'\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Tham số trống bị procedure ghép thẳng vào danh sách ORDER BY của câu SQL động (<c>' order by ngay_ct, so_ct,' + @Order</c>,
    /// <c>', a.so_ct0, ' + REPLACE(RTRIM(@Order)…) + ', a.stt_rec'</c>) → true. Khi đó truyền <see cref="OrderByFiller"/> — một biểu thức sắp xếp
    /// hợp lệ không đổi thứ tự — thay cho chuỗi trống (ra <c>order by a, , b</c> sai cú pháp). Chỗ ghép khác (WHERE, danh sách cột…) → false.
    /// </summary>
    public static bool IsOrderByConcat(string? definition, string name)
    {
        if (string.IsNullOrEmpty(definition)) return false;
        var body = ProcBody(definition);
        var hits = UnguardedConcats(body, name).ToList();
        // mọi chỗ ghép đều là "'…order by …,' + @x" (dạng "@x + …" đứng đầu biểu thức thì không phải)
        return hits.Count > 0 && hits.All(m =>
        {
            if (body[m.Index] != '+') return false;
            var start = m.Index == 0 ? 0 : body.LastIndexOf('\n', m.Index - 1) + 1;
            return OrderByTail.IsMatch(body[start..m.Index]);
        });
    }

    // ---- Lấy cách truyền tham số từ Filter có sẵn -----------------------------------------------

    /// <summary>Một lệnh <c>exec</c> gọi procedure trong Filter có sẵn: file, procedure được gọi, danh sách đối số (tên tham số nếu gọi kiểu
    /// <c>@p = …</c>), các field khai trong Filter kèm giá trị mặc định (clientDefault / defaultValue đơn giản).</summary>
    public sealed record FilterCall(string File, string Proc, List<(string? Name, string Arg)> Args, Dictionary<string, string> Fields);

    /// <summary>Tên controller Filter hay dùng cho procedure: zrs_rptX / rs_rptX / fs_X → rptX, X; giữ cả tên procedure.</summary>
    public static IEnumerable<string> FilterCandidates(string proc)
    {
        var names = new List<string> { proc };
        var bare = Regex.Replace(proc, @"^z?(rs|fs|ws|ts|cs|ps|sp|usp|zrs)_", "", RegexOptions.IgnoreCase);
        if (!names.Contains(bare, StringComparer.OrdinalIgnoreCase)) names.Add(bare);
        if (proc.StartsWith("z", StringComparison.OrdinalIgnoreCase) && proc.Length > 1 && !names.Contains(proc[1..], StringComparer.OrdinalIgnoreCase)) names.Add(proc[1..]);
        return names;
    }

    /// <summary>
    /// Tìm trong <c>App_Data\Controllers\Filter</c> của source workspace (theo tên file đoán từ tên procedure — không quét cả thư mục, thư mục
    /// mạng hàng nghìn file) một Filter đang <c>exec</c> procedure này (hoặc bản z… / bản bỏ z của nó) và tách đối số. Không thấy → null.
    /// </summary>
    public static FilterCall? FindFilterCall(string sourcePath, string proc)
    {
        var dir = Path.Combine(sourcePath, @"App_Data\Controllers\Filter");
        if (!Directory.Exists(dir)) return null;
        var targets = new[] { proc, "z" + proc, proc.StartsWith("z", StringComparison.OrdinalIgnoreCase) ? proc[1..] : proc }
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var call = new Regex(@"\bexec(?:ute)?\s+(?:(?:\[?dbo\]?|\[?\w+\]?\s*\.\s*\[?\w*\]?)\s*\.\s*)?\[?(" + string.Join("|", targets.Select(Regex.Escape)) + @")\]?(?![\w$#])",
            RegexOptions.IgnoreCase);
        foreach (var name in FilterCandidates(proc))
        {
            var file = Path.Combine(dir, name + ".xml");
            if (!File.Exists(file)) continue;
            var text = File.ReadAllText(file).Replace("<![CDATA[", "").Replace("]]>", "");
            // Ưu tiên lệnh gọi đúng tên procedure; không có mới lấy bản z… (bản sửa của dự án) — map theo tên tham số
            var hits = call.Matches(text).Cast<Match>().OrderBy(x => x.Groups[1].Value.Equals(proc, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            foreach (var m in hits)
            {
                var args = SplitExecArgs(text, m.Index + m.Length);
                if (args.Count == 0) continue;
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match f in Regex.Matches(text, @"<field\s+([^>]*?)/?>", RegexOptions.IgnoreCase))
                {
                    var attrs = f.Groups[1].Value;
                    if (Regex.Match(attrs, @"\bname=""(\w+)""") is not { Success: true } fn) continue;
                    var dv = Regex.Match(attrs, @"\bclientDefault=""([^""]*)""") is { Success: true } cd ? cd.Groups[1].Value
                        : Regex.Match(attrs, @"\bdefaultValue=""(-?\d+(?:\.\d+)?|'[^'""]*')""") is { Success: true } d ? DefaultTest(d.Groups[1].Value) : "";
                    fields.TryAdd(fn.Groups[1].Value, dv);
                }
                return new FilterCall(Path.GetFileName(file), m.Groups[1].Value, args, fields);
            }
        }
        return null;
    }

    /// <summary>Tách đối số sau tên procedure: dấu phẩy ngoài chuỗi / ngoặc; xuống dòng mà dòng không kết thúc bằng dấu phẩy = hết lệnh.</summary>
    public static List<(string? Name, string Arg)> SplitExecArgs(string text, int start)
    {
        var args = new List<(string?, string)>();
        var cur = new StringBuilder();
        var depth = 0;
        void Flush()
        {
            var a = cur.ToString().Trim(); cur.Clear();
            if (a.Length == 0) return;
            var named = Regex.Match(a, @"^@([\w$#]+)\s*=\s*(.+)$", RegexOptions.Singleline);
            args.Add(named.Success ? (named.Groups[1].Value, named.Groups[2].Value.Trim()) : (null, a));
        }
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\'')
            {
                var s = i;
                for (i++; i < text.Length; i++) if (text[i] == '\'') { if (i + 1 < text.Length && text[i + 1] == '\'') i++; else break; }
                cur.Append(text, s, Math.Min(i, text.Length - 1) - s + 1);
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')') { if (depth == 0) break; depth--; }
            else if (depth == 0 && c == ',') { Flush(); continue; }
            else if (depth == 0 && (c is '\n' or ';' or '<'))
            {
                if (c == '\n' && args.Count > 0 && cur.ToString().Trim().Length == 0) continue;   // dòng trước kết thúc bằng dấu phẩy → đối số tiếp ở dòng sau
                break;
            }
            cur.Append(c);
        }
        Flush();
        return args;
    }

    /// <summary>
    /// Áp cách Filter có sẵn truyền vào tham số: hằng số ('a.ma_kh', 1, null…) / @@biến → tham số hệ thống truyền đúng hằng đó;
    /// @field có trong Filter → ô lọc field đó (giá trị thử = mặc định của field). Tham số Bcode đã tự nhận ra (@@unit, '@@sysDatabaseName',
    /// @@language…) giữ nguyên. Trả mô tả ngắn những gì đã đổi (rỗng = không đổi).
    /// </summary>
    public static string? ApplyFilterArg(QuickReportParam p, string arg, FilterCall call)
    {
        if (SystemArg(p.Name) != null) return null;
        var a = Regex.Replace(arg.Trim(), @"'\s*&Controller;\s*'", "'@@controller'", RegexOptions.IgnoreCase);
        if (a.Contains('&')) return null;   // entity khác (&Identity;…) — không biết giá trị
        if (Regex.IsMatch(a, @"^@@\w+$|^'@@\w+'$|^-?\d+(\.\d+)?$|^N?'([^']|'')*'$|^null$", RegexOptions.IgnoreCase))
        {
            p.Role = "system"; p.Arg = a; p.Field = ""; p.Lookup = null;
            return $"@{p.Name} = {a}";
        }
        if (Regex.Match(a, @"^@(\w+)$") is { Success: true } v && call.Fields.TryGetValue(v.Groups[1].Value, out var def))
        {
            var field = v.Groups[1].Value;
            if (!p.Field.Equals(field, StringComparison.OrdinalIgnoreCase) || !p.IsFilter) SetField(p, field);
            if (def.Length > 0 && p.Type != "DateTime") p.Test = def;
            return null;
        }
        return null;
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
    /// <param name="notes">nhận các thay thế Bcode tự làm khi chạy thử (vd @Order trống → (select null)).</param>
    public static async Task<List<QuickReportSet>> RunAsync(SqlConnection conn, QuickReportSpec spec, string sysDatabase, int maxRows, CancellationToken ct,
        List<string>? notes = null)
    {
        // Kiểu / mặc định / OUTPUT đọc lại từ database mỗi lần chạy (cấu hình đã lưu có thể cũ hơn procedure)
        var info = await ReadProcAsync(conn, spec.ProcName, null, ct);
        var specByName = spec.Params.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var sets = new List<QuickReportSet>();
        var blank = new List<string>();   // tham số chuỗi đang truyền '' (để gợi ý khi lỗi)
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(ct);
        try
        {
            await using var cmd = new SqlCommand(spec.ProcName, conn, tx) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
            foreach (var m in info.Params)
            {
                var prm = BuildParameter(m, specByName.GetValueOrDefault(m.Name), sysDatabase, conn.Database, spec.Controller, out var sentBlank);
                if (prm != null) cmd.Parameters.Add(prm);
                // trống mà store ghép thẳng vào SQL động (vd "order by a, , b") → giá trị thử đoán được (biểu thức sắp xếp trung tính / hằng store so sánh)
                if (sentBlank && prm != null && BlankFallback(info.Definition, m.Name, out var why) is { } fill && (m.MaxLength < 0 || m.MaxLength >= fill.Length))
                {
                    prm.Value = fill;
                    notes?.Add($"@{m.Name} = {fill} ({why})");
                }
                else if (sentBlank) blank.Add(m.Name);
                // số chia đang là 0 → 1 (tránh lỗi chia cho 0 khi chạy thử)
                else if (prm?.Value is not null and not DBNull && prm.SqlDbType is SqlDbType.Int or SqlDbType.SmallInt or SqlDbType.TinyInt or SqlDbType.BigInt or SqlDbType.Decimal
                         or SqlDbType.Float or SqlDbType.Real or SqlDbType.Money && Convert.ToDecimal(prm.Value, CultureInfo.InvariantCulture) == 0 && IsDivisor(info.Definition, m.Name))
                {
                    prm.Value = ConvertValue(prm.SqlDbType, "1", m);
                    notes?.Add($"@{m.Name} = 1 (số chia)");
                }
            }
            try
            {
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                do
                {
                    if (rd.FieldCount == 0) continue;   // lệnh không trả bảng (update/insert…) — bỏ qua
                    var cols = Enumerable.Range(0, rd.FieldCount).Select(i => (ColumnName(rd, i), FieldType(SafeFieldType(rd, i)))).ToList();
                    var rows = new List<string?[]>();
                    var truncated = false;
                    while (await rd.ReadAsync(ct))
                    {
                        if (rows.Count >= maxRows) { truncated = true; break; }
                        var row = new string?[cols.Count];
                        for (var i = 0; i < cols.Count; i++) row[i] = SafeCell(rd, i);
                        rows.Add(row);
                    }
                    sets.Add(new QuickReportSet(cols, rows, truncated));
                } while (await rd.NextResultAsync(ct));
            }
            // 266/3902/3903: procedure tự COMMIT / ROLLBACK làm lệch transaction chạy thử — các result set đã đọc vẫn dùng được
            catch (SqlException ex) when (ex.Number is 266 or 3902 or 3903 && sets.Count > 0) { }
        }
        catch (SqlException ex) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(Explain(ex, spec.ProcName, info.Definition, blank), ex);
        }
        finally
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch { /* procedure tự commit/rollback hoặc kết nối đã đứt */ }
        }
        return sets;
    }

    /// <summary>Thông báo lỗi chạy thử: nơi lỗi (procedure con / dòng / SQL động) + gợi ý khi lỗi cú pháp do tham số để trống bị ghép vào SQL động.</summary>
    public static string Explain(SqlException ex, string proc, string? definition, IReadOnlyCollection<string> blank)
    {
        // procedure lặp cùng lỗi nhiều lần (mỗi kỳ / mỗi bảng phân kỳ một lần) → gộp dòng trùng, tối đa 3 lỗi khác nhau
        var lines = ex.Message.Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().ToList();
        var sb = new StringBuilder(string.Join(" ⏎ ", lines.Take(3)) + (lines.Count > 3 ? " …" : ""));
        var where = ex.Procedure is { Length: > 0 } pr
            ? (pr.Equals(proc, StringComparison.OrdinalIgnoreCase) || pr.EndsWith("." + proc, StringComparison.OrdinalIgnoreCase) ? $"dòng {ex.LineNumber}" : $"{pr} dòng {ex.LineNumber}")
            : ex.Number is 2812 or 2809 or 201 or 8144 or 8145 or 8146 or 8114 or 8152 ? ""   // lỗi ở chính lệnh gọi (không có procedure / sai tham số)
            : "câu SQL động store tự ghép (sp_executesql / exec)";
        sb.Append(where.Length > 0 ? $" [lỗi {ex.Number}, {where}]" : $" [lỗi {ex.Number}]");
        var suspects = UnguardedConcatParams(definition, blank);
        if (suspects.Count > 0)
            sb.Append($" — Store ghép thẳng tham số đang trống vào câu SQL: {string.Join(", ", suspects.Select(n => "@" + n))}. " +
                      "Nhập giá trị thử cho tham số đó, hoặc bấm “⇣ Hệ thống” và truyền hằng số như Filter gốc (vd @Order = 'a.ma_kh').");
        else if (ex.Number is 102 or 105 or 156 or 170 && blank.Count > 0)
            sb.Append($" — Có thể store ghép tham số đang trống vào SQL động; thử nhập giá trị cho: {string.Join(", ", blank.Take(8).Select(n => "@" + n))}{(blank.Count > 8 ? "…" : "")}.");
        return sb.ToString();
    }

    /// <summary>Giá trị chữ của tham số khi chạy thử: ô lọc = giá trị thử; tham số hệ thống = giá trị Bcode thay cho @@… / hằng số. null = NULL.</summary>
    private static string? RawValue(QuickReportParam p, string sysDatabase, string appDatabase, string controller)
    {
        if (p.IsFilter) return p.Test ?? "";
        var arg = p.Arg.Trim();
        switch (arg.ToLowerInvariant())
        {
            case "@@unit": return p.Test ?? "";
            case "'@@sysdatabasename'" or "@@sysdatabasename": return sysDatabase;
            case "'@@appdatabasename'" or "@@appdatabasename": return appDatabase;
            case "'@@controller'" or "@@controller": return controller;
            case "@@language": return "V";
            case "@@userid" or "@@admin": return "1";
            case "null" or "": return null;
        }
        if (arg.StartsWith("N'", StringComparison.OrdinalIgnoreCase)) arg = arg[1..];
        return arg.Length >= 2 && arg[0] == '\'' && arg[^1] == '\'' ? arg[1..^1].Replace("''", "'") : arg;
    }

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm:ss.fff",
        "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yyyy HH:mm", "dd/MM/yyyy HH:mm:ss", "yyyyMMdd", "yyyy/MM/dd",
    };

    /// <summary>
    /// SqlParameter đúng kiểu khai báo (không dùng AddWithValue — tránh suy sai kiểu / tràn số). Trả null = không truyền tham số đó
    /// (kiểu bảng, hoặc ô lọc để trống mà procedure có giá trị mặc định — SQL tự lấy mặc định). Ô trống không có mặc định: chuỗi '',
    /// số 0, bit 0, ngày / guid / nhị phân… NULL. Giá trị gõ sai kiểu: số → 0, ngày / guid → NULL (không văng lỗi chuyển kiểu).
    /// </summary>
    public static SqlParameter? BuildParameter(ProcParam m, QuickReportParam? p, string sysDatabase, string appDatabase, string controller, out bool sentBlank)
    {
        sentBlank = false;
        if (m.IsTable) return null;
        var raw = p is null ? "" : RawValue(p, sysDatabase, appDatabase, controller);
        var blank = raw is not null && raw.Trim().Length == 0;
        if (blank && (p is null || p.IsFilter || p.Arg.Trim().Length == 0) && m.HasDefault && !m.IsOutput) return null;

        var t = m.BaseType;
        var prm = new SqlParameter { ParameterName = "@" + m.Name, Direction = m.IsOutput ? ParameterDirection.InputOutput : ParameterDirection.Input };
        prm.SqlDbType = t switch
        {
            "bigint" => SqlDbType.BigInt, "int" => SqlDbType.Int, "smallint" => SqlDbType.SmallInt, "tinyint" => SqlDbType.TinyInt, "bit" => SqlDbType.Bit,
            "decimal" or "numeric" => SqlDbType.Decimal, "money" => SqlDbType.Money, "smallmoney" => SqlDbType.SmallMoney,
            "float" => SqlDbType.Float, "real" => SqlDbType.Real,
            "date" => SqlDbType.Date, "datetime" => SqlDbType.DateTime, "datetime2" => SqlDbType.DateTime2, "smalldatetime" => SqlDbType.SmallDateTime,
            "datetimeoffset" => SqlDbType.DateTimeOffset, "time" => SqlDbType.Time,
            "char" => SqlDbType.Char, "varchar" => SqlDbType.VarChar, "text" => SqlDbType.Text,
            "nchar" => SqlDbType.NChar, "nvarchar" => SqlDbType.NVarChar, "ntext" => SqlDbType.NText,
            "binary" => SqlDbType.Binary, "varbinary" => SqlDbType.VarBinary, "image" => SqlDbType.Image, "timestamp" or "rowversion" => SqlDbType.Timestamp,
            "uniqueidentifier" => SqlDbType.UniqueIdentifier, "xml" => SqlDbType.Xml, "sql_variant" => SqlDbType.Variant,
            // kiểu CLR (hierarchyid, geometry, geography): truyền NULL dạng varbinary — SQL tự đổi kiểu, không cần nạp assembly kiểu CLR
            "hierarchyid" or "geometry" or "geography" => SqlDbType.VarBinary,
            _ => SqlDbType.NVarChar,
        };
        if (t is "hierarchyid" or "geometry" or "geography") { prm.Size = -1; prm.Value = DBNull.Value; return prm; }
        if (prm.SqlDbType is SqlDbType.Char or SqlDbType.VarChar or SqlDbType.NChar or SqlDbType.NVarChar or SqlDbType.Binary or SqlDbType.VarBinary)
            prm.Size = m.MaxLength < 0 ? -1 : Math.Max(1, m.MaxLength);
        if (prm.SqlDbType == SqlDbType.Decimal) { prm.Precision = m.Precision; prm.Scale = m.Scale; }
        if (prm.SqlDbType is SqlDbType.DateTime2 or SqlDbType.Time or SqlDbType.DateTimeOffset) prm.Scale = m.Scale;
        prm.Value = raw is null ? DBNull.Value : ConvertValue(prm.SqlDbType, raw.Trim().Length == 0 ? "" : raw, m);
        sentBlank = blank && prm.Value is string;
        return prm;
    }

    private static object ConvertValue(SqlDbType type, string raw, ProcParam m)
    {
        var s = raw.Trim();
        decimal Num() => decimal.TryParse(s.Replace(" ", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var n) ? n
            : decimal.TryParse(s.Replace(" ", ""), NumberStyles.Number, CultureInfo.GetCultureInfo("vi-VN"), out n) ? n : 0m;
        decimal Clamp(decimal v, decimal lo, decimal hi) => Math.Truncate(Math.Min(hi, Math.Max(lo, v)));
        switch (type)
        {
            case SqlDbType.BigInt: return (long)Clamp(Num(), long.MinValue, long.MaxValue);
            case SqlDbType.Int: return (int)Clamp(Num(), int.MinValue, int.MaxValue);
            case SqlDbType.SmallInt: return (short)Clamp(Num(), short.MinValue, short.MaxValue);
            case SqlDbType.TinyInt: return (byte)Clamp(Num(), 0, 255);
            case SqlDbType.Bit:
                return s.ToLowerInvariant() is "true" or "y" or "yes" or "có" or "x" || (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var b) && b != 0);
            case SqlDbType.Decimal or SqlDbType.Money or SqlDbType.SmallMoney:
            {
                var v = Num();
                if (type == SqlDbType.Decimal && m.Precision > 0)
                {
                    v = Math.Round(v, m.Scale, MidpointRounding.AwayFromZero);
                    var max = (decimal)Math.Pow(10, Math.Min(28, m.Precision - m.Scale)) - 1;   // numeric(5, 2) chứa tối đa 999.99
                    if (Math.Abs(v) > max) v = 0m;
                }
                return v;
            }
            case SqlDbType.Float: return (double)Num();
            case SqlDbType.Real: return (float)Num();
            case SqlDbType.Date or SqlDbType.DateTime or SqlDbType.DateTime2 or SqlDbType.SmallDateTime:
            {
                if (!DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return DBNull.Value;
                var (lo, hi) = type switch
                {
                    SqlDbType.SmallDateTime => (new DateTime(1900, 1, 1), new DateTime(2079, 6, 6)),
                    SqlDbType.DateTime => (new DateTime(1753, 1, 1), DateTime.MaxValue),
                    _ => (DateTime.MinValue, DateTime.MaxValue),
                };
                return d < lo || d > hi ? DBNull.Value : d;
            }
            case SqlDbType.DateTimeOffset:
                return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var o) ? o : DBNull.Value;
            case SqlDbType.Time:
                return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var ts) && ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1) ? ts : DBNull.Value;
            case SqlDbType.UniqueIdentifier: return Guid.TryParse(s, out var g) ? g : DBNull.Value;
            case SqlDbType.Binary or SqlDbType.VarBinary or SqlDbType.Image or SqlDbType.Timestamp:
                return Regex.IsMatch(s, @"^0x([0-9a-fA-F]{2})*$") ? Convert.FromHexString(s[2..]) : DBNull.Value;
            case SqlDbType.Xml: return s.Length == 0 ? DBNull.Value : raw;
            default: return raw;   // chuỗi: giữ nguyên (kể cả dấu nháy đơn — SqlParameter tự xử lý, không ghép chuỗi)
        }
    }

    private static string ColumnName(SqlDataReader rd, int i) { var n = rd.GetName(i); return n.Length > 0 ? n : $"Column{i + 1}"; }

    private static Type SafeFieldType(SqlDataReader rd, int i)
    {
        try { return rd.GetFieldType(i) ?? typeof(string); } catch { return typeof(string); }   // kiểu CLR (geography…) chưa nạp assembly
    }

    private static string? SafeCell(SqlDataReader rd, int i)
    {
        try { return rd.IsDBNull(i) ? null : Cell(rd.GetValue(i)); }
        catch
        {
            try { return rd.GetSqlValue(i)?.ToString(); } catch { return "(?)"; }
        }
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

    /// <summary>Mẫu Excel của báo cáo nằm cùng các mẫu khác của source FBO: <c>App_Data\Controllers\Templates\Excel</c> (cạnh Templates\Rpt) — trước đây ghi nhầm vào App_Data\Templates\Excel.</summary>
    public static string ExcelTarget(QuickReportSpec s) => $@"App_Data\Controllers\Templates\Excel\{s.Controller}.xlsx";

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
    /// Khác ở một điểm: thư mục đích chưa có (vd App_Data\Controllers\Templates\Excel) không chặn cả lượt mà chỉ đánh dấu <see cref="FilePlan.NewDir"/>.</summary>
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
