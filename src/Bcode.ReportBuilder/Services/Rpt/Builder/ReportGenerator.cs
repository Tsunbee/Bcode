using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services.Rpt.Builder;

public sealed class ReportBuildResult
{
    public string Procedure { get; set; } = "";
    public List<string> Warnings { get; } = new();
    public List<ProcParam> Params { get; set; } = new();
    public string ProcName { get; set; } = "";
    public bool HasErrors => Warnings.Any(w => w.StartsWith("LỖI", StringComparison.Ordinal));
}

/// <summary>Tham số của procedure + cách Filter truyền vào lệnh exec (cùng THỨ TỰ — Filter ↔ procedure nối theo vị trí).</summary>
public sealed record ProcParam(string Name, string SqlType, string FilterArg, string Sample);

/// <summary>
/// Từ <see cref="ReportSpec"/> (người dùng chọn bảng / cột / bộ lọc) sinh ra 5 mảnh của một báo cáo FastBusiness: procedure (script), Filter xml, Grid xml
/// (thường hoặc pivot), Report xml (mẫu Excel) và trang Main .aspx. Khuôn mẫu lấy từ các báo cáo tuỳ biến thật (zrpt_*, zrs_* của KOG / PMT):
/// báo cáo theo chứng từ = @Key động + <c>Partition$Execute</c> + #report; báo cáo danh mục = SELECT trực tiếp. Chỉ SINH văn bản — không chạy gì lên database.
/// </summary>
public sealed partial class ReportGenerator
{
    private const string NL = "\r\n";
    private static readonly Regex Ref = new(@"^(?<a>[A-Za-z_]\w*)\.(?<c>\[?[\w$]+\]?)$", RegexOptions.Compiled);

    private static string Q(string s) => s.Replace("'", "''");                         // nhân đôi dấu nháy để nhúng vào chuỗi T-SQL
    private static string X(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");   // chỉ escape ký tự XML, giữ nguyên tiếng Việt

    private static (string Alias, string Col, bool Plain) Parse(string src)
    {
        var m = Ref.Match((src ?? "").Trim());
        return m.Success ? (m.Groups["a"].Value, m.Groups["c"].Value.Trim('[', ']'), true) : ("", src ?? "", false);
    }

    // ---- nối bảng ----
    private static string JoinOn(JoinSpec j)
    {
        var l = (j.Left ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries); var r = (j.Right ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (l.Length == 0 || r.Length == 0) return "1 = 1";
        return string.Join(" AND ", l.Select((x, i) => $"{x.Trim()} = {j.Alias}.{r[Math.Min(i, r.Length - 1)].Trim()}"));
    }
    private static string JoinKw(JoinSpec j) => string.Equals(j.Type, "inner", StringComparison.OrdinalIgnoreCase) ? "JOIN" : "LEFT JOIN";
    private static string FirstLeft(JoinSpec j) => (j.Left ?? "").Split(',')[0].Trim();

    /// <summary>Báo cáo chứng từ có nối bảng phân kỳ khác (vd m21 + d21): các bảng đó nối ngay ở bước lấy dữ liệu theo kỳ; cột của chúng được kéo lên dưới tên <c>alias_cột</c>
    /// rồi các bước sau coi như cột của bảng chính. Trả về bản sao đã viết lại (không đụng spec gốc).</summary>
    private static ReportSpec Fold(ReportSpec spec)
    {
        if (spec.Mode != "voucher" || !spec.Joins.Any(j => j.Partitioned)) return spec;
        var c = System.Text.Json.JsonSerializer.Deserialize<ReportSpec>(System.Text.Json.JsonSerializer.Serialize(spec))!;
        var part = new HashSet<string>(c.Joins.Where(j => j.Partitioned).Select(j => j.Alias), StringComparer.OrdinalIgnoreCase);
        string RW(string src)
        {
            if (string.IsNullOrEmpty(src)) return src;
            return Regex.Replace(src, @"\b([A-Za-z_]\w*)\.(\w+)", m =>
            {
                if (!part.Contains(m.Groups[1].Value)) return m.Value;
                var o = m.Groups[1].Value + "_" + m.Groups[2].Value;
                if (!c.PartRaw.Any(x => x.Out.Equals(o, StringComparison.OrdinalIgnoreCase))) c.PartRaw.Add((m.Groups[1].Value, m.Groups[2].Value, o));
                return c.MainAlias + "." + o;
            });
        }
        void Col(ColumnSpec k) { if (k is null) return; if (!k.IsMeasure) k.Source = RW(k.Source); k.Source2 = RW(k.Source2); k.Key = RW(k.Key); }
        foreach (var k in c.Columns) Col(k);
        if (c.Matrix is not null) { foreach (var k in c.Matrix.Rows) Col(k); Col(c.Matrix.Column); foreach (var k in c.Matrix.Values) Col(k); }
        foreach (var j in c.Joins.Where(j => !j.Partitioned)) j.Left = string.Join(",", (j.Left ?? "").Split(',').Select(x => RW(x.Trim())));
        return c;
    }

    // =====================================================================================================================
    //  Tên & tham số
    // =====================================================================================================================

    /// <summary>Bộ lọc đơn vị (field ma_dvcs / tra cứu Unit / cột đơn vị của báo cáo): dùng chung tham số @Unit có sẵn và điều kiện đơn vị của procedure, không sinh tham số thứ hai.</summary>
    private static bool IsUnitFilter(ReportSpec spec, FilterSpec f) =>
        ParamName(f).Equals("Unit", StringComparison.OrdinalIgnoreCase)
        || (!string.IsNullOrWhiteSpace(spec.UnitColumn) && ReportCatalog.Bare(string.IsNullOrEmpty(f.Column) ? f.Field : f.Column).Equals(spec.UnitColumn, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tên tham số procedure mặc định của một bộ lọc (dùng khi dựng lại từ báo cáo có sẵn để tránh trùng tên).</summary>
    public static string DefaultParamName(FilterSpec f) => ParamName(f);

    private static string ParamName(FilterSpec f)
    {
        if (!string.IsNullOrWhiteSpace(f.Param)) return f.Param.Trim().TrimStart('@');
        if (!string.IsNullOrEmpty(f.Controller)) return f.Controller.Replace("LoanContract", "LoanContract");
        var n = ReportCatalog.Bare(string.IsNullOrEmpty(f.Column) ? f.Field : f.Column);
        n = Regex.Replace(n, @"^(ma|so)_", "");
        return string.Concat(n.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    public List<ProcParam> BuildParams(ReportSpec spec)
    {
        var ps = new List<ProcParam>();
        if (spec.DateRange)
        {
            ps.Add(new("DateFrom", "SMALLDATETIME", "@tu_ngay", "'20260101'"));
            ps.Add(new("DateTo", "SMALLDATETIME", "@den_ngay", "'20261231'"));
        }
        foreach (var f in spec.Filters.Where(f => f.Op != "date" && !IsUnitFilter(spec, f)))
        {
            var t = f.Op is "in" or "inlist" ? "VARCHAR(1023)" : f.Style == "Numeric" ? "NUMERIC(28, 6)" : "VARCHAR(33)";
            if (ps.Any(p => p.Name.Equals(ParamName(f), StringComparison.OrdinalIgnoreCase))) continue;   // trùng tên (Validate đã báo lỗi)
            ps.Add(new(ParamName(f), t, "@" + f.Field, t.StartsWith("NUMERIC") ? "0" : "''"));
        }
        if (spec.HasPivotOptions) ps.Add(new("PivotBy", "VARCHAR(10)", "@" + spec.Matrix!.ColumnField, "'" + PivotDefault(spec) + "'"));
        if (spec.HasDynGroup) ps.Add(new("GroupBy", "VARCHAR(10)", "@" + spec.GroupField, "'" + (spec.GroupDefault ?? "0") + "'"));
        var unitFilter = spec.Filters.FirstOrDefault(f => f.Op != "date" && IsUnitFilter(spec, f));
        ps.Add(new("Unit", "VARCHAR(1023)", unitFilter is null ? "@@unit" : "@" + unitFilter.Field, "''"));
        ps.Add(new("sysDatabaseName", "VARCHAR(128)", "'@@sysDatabaseName'", "''"));
        ps.Add(new("Language", "CHAR(1)", "@@language", "'V'"));
        ps.Add(new("UserID", "INT", "@@userID", "1"));
        ps.Add(new("Admin", "BIT", "@@admin", "1"));
        return ps;
    }

    // =====================================================================================================================
    //  Kiểm tra đặc tả
    // =====================================================================================================================

    public List<string> Validate(ReportSpec spec)
    {
        var w = new List<string>();
        if (string.IsNullOrWhiteSpace(spec.CoreCode)) w.Add("LỖI: chưa đặt mã báo cáo.");
        else if (!Regex.IsMatch(spec.CoreCode, @"^[A-Za-z0-9_]+$")) w.Add("LỖI: mã báo cáo chỉ gồm chữ, số và dấu gạch dưới.");
        if (string.IsNullOrWhiteSpace(spec.MainTable)) w.Add("LỖI: chưa chọn bảng chính.");
        var cols = AllColumns(spec).ToList();
        if (cols.Count == 0) w.Add("LỖI: chưa chọn cột nào cho báo cáo.");
        var dup = cols.GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dup.Count > 0) w.Add("LỖI: tên cột kết quả bị trùng: " + string.Join(", ", dup) + " — đổi tên một trong các cột.");
        if (cols.Any(c => string.IsNullOrWhiteSpace(c.Name))) w.Add("LỖI: có cột chưa có tên.");
        foreach (var j in spec.Joins)
        {
            if (string.IsNullOrWhiteSpace(j.Left) || string.IsNullOrWhiteSpace(j.Right)) w.Add($"LỖI: bảng nối '{j.Table}' ({j.Alias}) chưa chọn cột nối.");
            if (j.Partitioned && spec.Mode != "voucher") w.Add($"LỖI: bảng chứng từ '{j.Table}' chỉ nối được trong báo cáo kiểu Chứng từ.");
        }
        foreach (var g in spec.Joins.GroupBy(j => j.Alias, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1 || g.Key.Equals(spec.MainAlias, StringComparison.OrdinalIgnoreCase))) w.Add($"LỖI: bí danh bảng '{g.Key}' bị trùng.");
        if (spec.Mode == "voucher" && !spec.DateRange) w.Add("LỖI: báo cáo theo chứng từ cần khoảng ngày (Partition$Execute lọc theo kỳ) — bật 'Từ ngày / Đến ngày'.");
        if (spec.IsMatrix)
        {
            var m = spec.Matrix!;
            if (m.Rows.Count == 0) w.Add("LỖI: pivot cần ít nhất một trường ở ô Hàng.");
            if (string.IsNullOrWhiteSpace(m.Column.Source)) w.Add("LỖI: pivot cần một trường ở ô Cột.");
            if (m.Values.Count == 0) w.Add("LỖI: pivot cần ít nhất một trường ở ô Giá trị.");
            if (m.Values.Any(v => !v.IsMeasure)) w.Add("LỖI: trường ở ô Giá trị phải có phép tính (Sum, Count…).");
        }
        else if (spec.Kind == "matrix") w.Add("LỖI: chưa cấu hình pivot (Hàng / Cột / Giá trị).");
        foreach (var c in cols)
        {
            var (a, _, plain) = Parse(c.Source);
            if (!c.IsMeasure && !plain && string.IsNullOrEmpty(c.Bal) && string.IsNullOrEmpty(c.Formula)) w.Add($"LỖI: cột '{c.Name}' là biểu thức nhưng không có phép tính — chỉ cột số liệu (Sum…) mới dùng được biểu thức.");
            if (plain && a != spec.MainAlias && spec.Joins.All(j => !j.Alias.Equals(a, StringComparison.OrdinalIgnoreCase)))
                w.Add($"LỖI: cột '{c.Name}' lấy từ bảng có bí danh '{a}' nhưng chưa nối bảng đó.");
        }
        ValidateBalance(spec, w);
        ValidateFormulas(spec, w);
        ValidateGroups(spec, w);
        ValidateDynamic(spec, w);
        foreach (var f in spec.Filters)
        {
            if (string.IsNullOrWhiteSpace(f.Field)) w.Add("LỖI: có bộ lọc chưa có tên field.");
            if (spec.Mode == "voucher")
            {
                var (a, _, _) = Parse(f.Column);
                if (!string.IsNullOrEmpty(a) && a != spec.MainAlias && !spec.Joins.Any(j => j.Partitioned && j.Alias.Equals(a, StringComparison.OrdinalIgnoreCase))) w.Add($"Cảnh báo: bộ lọc '{f.Field}' lọc theo cột của bảng danh mục đã nối ('{a}') — lọc được, nhưng lọc SAU khi đã lấy dữ liệu theo kỳ (không giới hạn được ngay từ bước đọc bảng phân vùng) nên có thể chậm hơn với dữ liệu lớn.");
            }
        }
        foreach (var gp in spec.Filters.Where(f => f.Op != "date" && !IsUnitFilter(spec, f)).GroupBy(ParamName, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1))
            w.Add($"LỖI: các bộ lọc {string.Join(", ", gp.Select(f => f.Field))} trùng tên tham số '{gp.Key}' — đổi lại một bộ lọc (hoặc bỏ bớt).");
        var fd = spec.Filters.GroupBy(f => f.Field, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (fd.Count > 0) w.Add("LỖI: tên field bộ lọc bị trùng: " + string.Join(", ", fd));
        if (spec.Mode == "catalog" && spec.Filters.Any(f => f.Op == "like" && string.IsNullOrEmpty(f.Column))) w.Add("LỖI: có bộ lọc chưa chọn cột để lọc.");
        return w;
    }

    private static IEnumerable<ColumnSpec> AllColumns(ReportSpec s)
    {
        if (s.IsMatrix)
        {
            foreach (var r in s.Matrix!.Rows) yield return r;
            yield return s.Matrix.Column;
            // các cột "Xoay theo" khác cũng là cột chiều (đi qua bước lấy dữ liệu / nhóm) để xColumn chọn giữa chúng lúc chạy
            foreach (var o in s.Matrix.ColumnOptions) if (!o.Column.Name.Equals(s.Matrix.Column.Name, StringComparison.OrdinalIgnoreCase)) yield return o.Column;
            foreach (var v in s.Matrix.Values) yield return v;
        }
        else foreach (var c in s.Columns) yield return c;
    }

    // =====================================================================================================================
    //  Chính
    // =====================================================================================================================

    public ReportBuildResult Build(ReportSpec spec)
    {
        var r = new ReportBuildResult { ProcName = spec.ProcName, Params = BuildParams(spec) };
        r.Warnings.AddRange(Validate(spec));
        if (r.HasErrors) return r;
        try
        {
            r.Procedure = Procedure(spec, r.Params);
        }
        catch (TemplateMissingException ex) { r.Warnings.Add("LỖI: " + ex.Message); }
        return r;
    }

    // =====================================================================================================================
    //  PROCEDURE
    // =====================================================================================================================

    /// <summary>Biểu thức SELECT của một cột ở bước cuối (đọc từ #tmp / bảng chính bí danh a, và các bảng nối).</summary>
    private static string OutExpr(ColumnSpec c, ReportSpec spec, bool fromTmp)
    {
        var (alias, col, plain) = Parse(c.Source);
        if (!string.IsNullOrEmpty(c.Formula)) return $"CAST(0 AS NUMERIC(28, 6)) AS {c.Name}";
        if (!string.IsNullOrEmpty(c.Bal)) { var bp = c.Bal.Split(':'); return $"ISNULL(z{bp[0]}.{bp[1]}, 0) AS {c.Name}"; }
        if (c.IsMeasure) return fromTmp ? $"{spec.MainAlias}.{c.Name}" : $"{AggFn(c.Aggregate)}({c.Source}) AS {c.Name}";
        string expr;
        if (!string.IsNullOrEmpty(c.Source2))
        {
            var (_, col2, _) = Parse(c.Source2);
            var a2 = Parse(c.Source2).Alias;
            expr = $"CASE WHEN @Language = 'V' THEN {alias}.{col} ELSE {(string.IsNullOrEmpty(a2) ? alias : a2)}.{col2} END";
        }
        else expr = $"{alias}.{col}";
        // cột chiều: khi đọc từ #tmp thì cột của bảng chính nằm ở bí danh a (không đổi), cột bảng nối đọc từ bảng nối
        return col.Equals(c.Name, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(c.Source2) ? expr : $"{expr} AS {c.Name}";
    }

    private static string AggFn(string a) => a switch { "Count" => "COUNT", "Min" => "MIN", "Max" => "MAX", "Avg" => "AVG", _ => "SUM" };

    private static string OrderBy(IEnumerable<ColumnSpec> cols, IReadOnlyList<ColumnSpec> dims, bool fromTmp, string main)
    {
        var o = cols.Where(c => !string.IsNullOrEmpty(c.Order)).Select(c => (fromTmp && !c.IsMeasure ? OrderName(c) : c.IsMeasure ? (fromTmp ? main + "." + c.Name : AggFn(c.Aggregate) + "(" + c.Source + ")") : OrderName(c)) + (c.Order == "desc" ? " DESC" : "")).ToList();
        if (o.Count == 0 && dims.Count > 0) o.Add(OrderName(dims[0]));
        return o.Count == 0 ? "(SELECT NULL)" : string.Join(", ", o);
    }

    /// <summary>Số thứ tự: báo cáo thường = ROW_NUMBER theo thứ tự sắp xếp; pivot = DENSE_RANK theo các cột nhãn dòng (mọi ô cùng một dòng pivot có cùng stt).</summary>
    private static string SttExpr(ReportSpec spec, List<ColumnSpec> cols, List<ColumnSpec> dims, bool fromTmp)
    {
        if (spec.IsMatrix)
        {
            var rows = spec.Matrix!.Rows;
            return $"DENSE_RANK() OVER(ORDER BY {OrderBy(rows.Where(c => !string.IsNullOrEmpty(c.Order)), rows, fromTmp, spec.MainAlias)}) AS stt";
        }
        return $"ROW_NUMBER() OVER(ORDER BY {OrderBy(cols.Where(c => string.IsNullOrEmpty(c.Bal) && string.IsNullOrEmpty(c.Formula) && (!c.IsMeasure || !string.IsNullOrEmpty(c.Order))), dims, fromTmp, spec.MainAlias)}) AS stt";
    }

    private static string OrderName(ColumnSpec c)
    {
        var (alias, col, plain) = Parse(c.Source);
        return plain ? $"{alias}.{col}" : c.Name;
    }

    private void VoucherBody(ReportSpec spec, List<ProcParam> ps, StringBuilder sb, List<ColumnSpec> cols, List<ColumnSpec> dims, List<ColumnSpec> measures, MatrixSpec? matrix)
    {
        void L(string s = "") => sb.Append(s).Append(NL);
        var main = spec.MainAlias;
        var hasUnit = !string.IsNullOrWhiteSpace(spec.UnitColumn);

        var partAl = spec.Joins.Where(j => j.Partitioned).Select(j => j.Alias).ToList();
        bool IsPart(string col) => partAl.Any(a => col.StartsWith(a + ".", StringComparison.OrdinalIgnoreCase));
        L("\tDECLARE @Key NVARCHAR(4000), @q NVARCHAR(4000)" + (hasUnit ? ", @UnitKey NVARCHAR(4000)" : "") + string.Concat(partAl.Select(a => $", @Key_{a} NVARCHAR(4000)")));
        if (hasUnit) L($"\tSET @UnitKey = dbo.FastBusiness$Function$System$GetUnitFilter('{main}.{spec.UnitColumn}', @Unit, @UserID, @Admin)");
        L();
        L("\t-- @Key: điều kiện lọc");
        L(spec.HasStatus ? $"\tSET @Key = ' {main}.status <> ''*'''" : "\tSET @Key = ''");
        if (hasUnit) L("\tIF @UnitKey IS NOT NULL SET @Key = @Key + CASE WHEN @Key = '' THEN '' ELSE ' and ' END + @UnitKey");
        void KeyFilters(string kv, IEnumerable<FilterSpec> fs)
        {
            foreach (var f in fs)
            {
                var p = ParamName(f);
                var and = $"CASE WHEN {kv} = '' THEN '' ELSE ' and ' END";
                switch (f.Op)
                {
                    case "eq": L($"\tIF @{p} <> '' SET {kv} = {kv} + {and} + '{f.Column} = ''' + REPLACE(RTRIM(@{p}), '''', '''''') + ''''"); break;
                    case "in": L($"\tIF @{p} <> '' SET {kv} = {kv} + {and} + '{f.Column} in (''' + REPLACE(REPLACE(REPLACE(@{p}, ' ', ''), '''', ''''''), ',', ''',''') + ''')'"); break;
                    case "inlist": L($"\tIF @{p} <> '' SET {kv} = {kv} + {and} + 'dbo.ff_Inlist({f.Column}, ''' + REPLACE(@{p}, '''', '''''') + ''') = 1'"); break;
                    default: L($"\tIF @{p} <> '' SET {kv} = {kv} + {and} + '{f.Column} like ''' + REPLACE(RTRIM(@{p}), '''', '''''') + '%'''"); break;
                }
            }
        }
        // Bộ lọc có thể theo cột của: (1) bảng chính → @Key (bước 1, SQL động); (2) bảng chứng từ khác đã nối (partitioned join) → @Key_<alias> riêng (bước 1);
        // (3) bảng DANH MỤC đã nối (không phân vùng, vd dmvv/dmkh) → KHÔNG có alias đó trong SQL động bước 1 (chỉ có bảng chính / bảng chứng từ nối),
        // nên phải lọc ở bước 2 (joinConds, where #report được ghép tên) — bí danh đó chỉ tồn tại ở bước 2. Trước đây gộp chung (3) vào (1) nên
        // sinh ra SQL động tham chiếu bí danh không tồn tại trong câu lệnh đó → lỗi khi người dùng thật sự nhập giá trị để lọc.
        bool IsMain(string col) { var (al, _, _) = Parse(col); return al.Length == 0 || al.Equals(main, StringComparison.OrdinalIgnoreCase); }
        var active = spec.Filters.Where(f => f.Op != "date" && !string.IsNullOrEmpty(f.Column) && !IsUnitFilter(spec, f)).ToList();
        KeyFilters("@Key", active.Where(f => IsMain(f.Column)));
        L("\tSET @Key = dbo.FastBusiness$Function$System$GetCheckKey(@Key)");
        foreach (var a in partAl)
        {
            L($"\tSET @Key_{a} = ''");
            KeyFilters($"@Key_{a}", active.Where(f => f.Column.StartsWith(a + ".", StringComparison.OrdinalIgnoreCase)));
            L($"\tIF @Key_{a} = '' SET @Key_{a} = '1 = 1'");
        }
        L();
        var joinConds = active.Where(f => !IsMain(f.Column) && !IsPart(f.Column)).Select(f =>
        {
            var p = ParamName(f);
            return f.Op switch
            {
                "eq" => $"(@{p} = '' OR {f.Column} = RTRIM(@{p}))",
                "in" => $"(@{p} = '' OR dbo.ff_ExactInlist({f.Column}, @{p}) = 1)",
                "inlist" => $"(@{p} = '' OR dbo.ff_Inlist({f.Column}, @{p}) = 1)",
                _ => $"(@{p} = '' OR {f.Column} LIKE RTRIM(@{p}) + '%')",
            };
        }).ToList();

        // ---- Bước 1: dữ liệu thô theo kỳ từ bảng chính (các cột của bảng chính + khoá nối), cộng dồn số liệu ----
        var usedJoins = UsedJoins(spec, cols, active.Where(f => !IsMain(f.Column) && !IsPart(f.Column)).Select(f => f.Column));
        var rawDims = new List<string>();                                  // "col" của bảng chính, theo thứ tự
        void AddRaw(string col) { if (!rawDims.Contains(col, StringComparer.OrdinalIgnoreCase)) rawDims.Add(col); }
        foreach (var d in dims)
        {
            var (alias, col, _) = Parse(d.Source);
            if (alias == main) AddRaw(col);
            else { var j = spec.Joins.First(x => x.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase)); AddRaw(Parse(FirstLeft(j)).Col); }
            if (!string.IsNullOrEmpty(d.Key)) { var (ka, kc, _) = Parse(d.Key); if (ka == main) AddRaw(kc); }
        }
        foreach (var j in usedJoins) AddRaw(Parse(FirstLeft(j)).Col);

        string RawSel(string c) { var p = spec.PartRaw.FirstOrDefault(x => x.Out.Equals(c, StringComparison.OrdinalIgnoreCase)); return p.Out is null ? $"{main}.{c}" : $"{p.Alias}.{p.Col}"; }
        string RawOut(string c) { var p = spec.PartRaw.FirstOrDefault(x => x.Out.Equals(c, StringComparison.OrdinalIgnoreCase)); return p.Out is null ? RawSel(c) : $"{RawSel(c)} AS {p.Out}"; }
        var sel = rawDims.Select(RawOut).ToList();
        var struc = rawDims.Select(RawOut).ToList();
        foreach (var m in measures)
        {
            var (ma, mc, mplain) = Parse(m.Source);
            sel.Add($"{AggFn(m.Aggregate)}({m.Source}) AS {m.Name}");
            struc.Add($"CAST(0 AS NUMERIC(28, 6)) AS {m.Name}");               // luôn rộng (28, 6): SUM theo nhóm có thể vượt độ chính xác của cột gốc → "Arithmetic overflow"
        }
        var table = spec.MainTable;
        var partJoins = spec.Joins.Where(j => j.Partitioned).ToList();
        var tmpName = "#tmp";
        if (partJoins.Count > 0) tmpName = JoinedStep1(sb, spec, rawDims, measures, partJoins);
        else
        {
        L("\t-- Dữ liệu thô theo kỳ (cấu trúc, rồi lấy từ các bảng phân kỳ)");
        var fromTop = "";
        var fromQ = "";
        L($"\tSELECT TOP 0 {string.Join(", ", struc)} INTO #tmp FROM {table}$000000 {main}{fromTop}");
        var group = (measures.Count > 0 || !string.IsNullOrEmpty(spec.Balance?.Kind)) && rawDims.Count > 0 ? " group by " + string.Join(", ", rawDims.Select(RawSel)) : "";
        L($"\tSET @q = 'insert into #tmp select {Q(string.Join(", ", sel))} from {table}$%Partition {main} with(nolock){Q(fromQ)} where %[' + @Key + ']%{Q(group)}'");
        L($"\tEXEC FastBusiness$Partition$Execute @q, NULL, '{main}.{spec.DateField}', @DateFrom, @DateTo, @UserID, @Admin");
        L();
        }

        var balJoins = !string.IsNullOrEmpty(spec.Balance?.Kind) ? BalanceStep(sb, spec, tmpName, rawDims, measures) : new List<string>();

        // ---- Bước 2: ghép tên danh mục, tên song ngữ, số thứ tự → #report ----
        var select = new List<string>();
        var fixedCols = "5 AS sysorder, 1 AS sysprint, 1 AS systotal";
        if (spec.Stt) fixedCols += ", " + SttExpr(spec, cols, dims, true);
        foreach (var c in cols) select.Add(OutExpr(c, spec, true));
        if (matrix is not null) select.AddRange(MatrixKeyCols(matrix, spec, fromTmp: true));
        L("\t-- Kết quả");
        L($"\tSELECT {fixedCols}");
        foreach (var s in select) L($"\t\t, {s}");
        L("\t\tINTO #report");
        L($"\t\tFROM {tmpName} {main}");
        foreach (var j in usedJoins) L($"\t\t\t{JoinKw(j)} {j.Table} {j.Alias} ON {JoinOn(j)}");
        foreach (var bj in balJoins) L("\t\t\t" + bj);
        for (var i = 0; i < joinConds.Count; i++) L((i == 0 ? "\t\tWHERE " : "\t\t\tAND ") + joinConds[i]);
        L();
    }

    /// <summary>Báo cáo chứng từ có nối bảng chứng từ khác: MỖI bảng lấy theo kỳ vào bảng tạm riêng (#tmp, #tmp_b…) chỉ với các cột cần dùng, rồi gộp một lần (kèm cộng dồn số liệu) vào #tmp2.
    /// Trả về tên bảng tạm cuối cùng để bước ghép tên đọc.</summary>
    private static string JoinedStep1(StringBuilder sb, ReportSpec spec, List<string> rawDims, List<ColumnSpec> measures, List<JoinSpec> partJoins)
    {
        void L(string s = "") => sb.Append(s).Append(NL);
        var main = spec.MainAlias;
        var need = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { [main] = new() };
        foreach (var j in partJoins) need[j.Alias] = new();
        void Need(string alias, string col) { if (need.TryGetValue(alias, out var l) && !l.Contains(col, StringComparer.OrdinalIgnoreCase)) l.Add(col); }
        void NeedRefs(string expr) { foreach (Match m in Regex.Matches(expr ?? "", @"\b([A-Za-z_]\w*)\.(\w+)")) Need(m.Groups[1].Value, m.Groups[2].Value); }
        (string Alias, string Col, string Out) Raw(string c)
        {
            var p = spec.PartRaw.FirstOrDefault(x => x.Out.Equals(c, StringComparison.OrdinalIgnoreCase));
            return p.Out is null ? (main, c, c) : (p.Alias, p.Col, p.Out);
        }
        foreach (var c in rawDims) { var r = Raw(c); Need(r.Alias, r.Col); }
        foreach (var m in measures) NeedRefs(m.Source);
        foreach (var j in partJoins) { NeedRefs(j.Left); foreach (var r in (j.Right ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)) Need(j.Alias, r.Trim()); }

        if (need[main].Count == 0) need[main].Add("stt_rec");      // bảng chính chưa có cột nào được dùng: vẫn cần một cột để tạo bảng tạm
        var dateMain = spec.DateField;
        L("\t-- Bước 1: mỗi bảng chứng từ lấy theo kỳ vào bảng tạm riêng (chỉ các cột cần dùng)");
        string Cols(string alias) => string.Join(", ", need[alias].Select(c => $"{alias}.{c}"));
        L($"\tSELECT TOP 0 {Cols(main)} INTO #tmp FROM {spec.MainTable}$000000 {main}");
        L($"\tSET @q = 'insert into #tmp select {Q(Cols(main))} from {spec.MainTable}$%Partition {main} with(nolock) where %[' + @Key + ']%'");
        L($"\tEXEC FastBusiness$Partition$Execute @q, NULL, '{main}.{dateMain}', @DateFrom, @DateTo, @UserID, @Admin");
        L();
        foreach (var j in partJoins)
        {
            var d = string.IsNullOrWhiteSpace(j.DateField) ? dateMain : j.DateField;
            L($"\tSELECT TOP 0 {Cols(j.Alias)} INTO #tmp_{j.Alias} FROM {j.Table}$000000 {j.Alias}");
            L($"\tSET @q = 'insert into #tmp_{j.Alias} select {Q(Cols(j.Alias))} from {j.Table}$%Partition {j.Alias} with(nolock) where %[' + @Key_{j.Alias} + ']%'");
            L($"\tEXEC FastBusiness$Partition$Execute @q, NULL, '{j.Alias}.{d}', @DateFrom, @DateTo, @UserID, @Admin");
            L();
        }

        L("\t-- Gộp các bảng tạm (và cộng dồn số liệu)");
        var sel = rawDims.Select(c => { var r = Raw(c); return r.Alias == main ? $"{main}.{r.Col}" : $"{r.Alias}.{r.Col} AS {r.Out}"; }).ToList();
        foreach (var m in measures) sel.Add($"{AggFn(m.Aggregate)}({m.Source}) AS {m.Name}");
        L($"\tSELECT {string.Join(", ", sel)}");
        L("\t\tINTO #tmp2");
        L($"\t\tFROM #tmp {main}");
        foreach (var j in partJoins) L($"\t\t\t{JoinKw(j)} #tmp_{j.Alias} {j.Alias} ON {JoinOn(j)}");
        if ((measures.Count > 0 || !string.IsNullOrEmpty(spec.Balance?.Kind)) && rawDims.Count > 0)
            L("\t\tGROUP BY " + string.Join(", ", rawDims.Select(c => { var r = Raw(c); return $"{r.Alias}.{r.Col}"; })));
        L();
        return "#tmp2";
    }

    // ---- Cột công thức: tính trên các cột KẾT QUẢ (vd [du_no_dk] + [ps_no] - [ps_co]) ----

    /// <summary>Chia cho 0 cho kết quả 0: bọc mẫu số (một [cột] hoặc cả cụm trong ngoặc) bằng NULLIF(…, 0) rồi đổi [cột] thành ISNULL(cột, 0).</summary>
    private static string FormulaSql(string f) => Regex.Replace(WrapDivisors(f ?? ""), @"\[(\w+)\]", "ISNULL($1, 0)");

    private static string WrapDivisors(string s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            sb.Append(s[i]);
            if (s[i] != '/') continue;
            var j = i + 1; while (j < s.Length && char.IsWhiteSpace(s[j])) j++;
            if (j >= s.Length) continue;
            if (s[j] == '[')
            {
                var e = s.IndexOf(']', j); if (e < 0) continue;
                sb.Append(' ').Append("NULLIF(").Append(s, j, e - j + 1).Append(", 0)"); i = e;
            }
            else if (s[j] == '(')
            {
                var depth = 0; var e = j;
                for (; e < s.Length; e++) { if (s[e] == '(') depth++; else if (s[e] == ')' && --depth == 0) break; }
                if (e >= s.Length) continue;
                sb.Append(' ').Append("NULLIF(").Append(WrapDivisors(s.Substring(j, e - j + 1))).Append(", 0)"); i = e;
            }
        }
        return sb.ToString();
    }

    /// <summary>Cột công thức được khai báo trong #report (giá trị 0) rồi cập nhật theo thứ tự khai báo, nên công thức sau dùng được kết quả của công thức trước.</summary>
    private static void FormulaUpdates(StringBuilder sb, List<ColumnSpec> cols, string where = "")
    {
        var any = false;
        foreach (var c in cols.Where(c => !string.IsNullOrEmpty(c.Formula)))
        {
            if (!any) { sb.Append("\t-- Cột công thức").Append(NL); any = true; }
            sb.Append($"\tUPDATE #report SET {c.Name} = ISNULL({FormulaSql(c.Formula)}, 0){where}").Append(NL);
        }
        if (any) sb.Append(NL);
    }

    private static void ValidateFormulas(ReportSpec spec, List<string> w)
    {
        var all = AllColumns(spec).ToList();
        foreach (var c in all)
        {
            if (c.IsMeasure && !string.IsNullOrEmpty(c.Source) && (c.Source.Contains(';') || c.Source.Contains('\'') || c.Source.Contains('"') || c.Source.Contains("--") || c.Source.Contains("/*")))
                w.Add($"LỖI: biểu thức của cột '{c.Name}' chứa ký tự không được phép (; ' \" -- /*).");
            if (string.IsNullOrEmpty(c.Formula)) continue;
            if (spec.IsMatrix) { w.Add("LỖI: chưa hỗ trợ cột công thức trong báo cáo Pivot."); continue; }
            if (!Regex.IsMatch(c.Formula, @"^[\w\s\[\]\.\+\-\*/\(\)]+$"))
            { w.Add($"LỖI: công thức của cột '{c.Name}' chỉ được dùng [tên cột], số, + - * / và dấu ngoặc."); continue; }
            foreach (Match m in Regex.Matches(c.Formula, @"\[(\w+)\]"))
            {
                var n = m.Groups[1].Value;
                if (n.Equals(c.Name, StringComparison.OrdinalIgnoreCase)) w.Add($"LỖI: công thức của cột '{c.Name}' tự tham chiếu chính nó.");
                else if (!all.Any(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) w.Add($"LỖI: công thức của cột '{c.Name}' dùng cột [{n}] không có trong báo cáo.");
            }
        }
    }

    // ---- Số dư đầu kỳ / cuối kỳ (gọi hàm số dư chuẩn của Fast) ----

    /// <summary>Kind → (hàm số dư, cột khoá của kết quả, câu SELECT TOP 0 khai báo cấu trúc bảng tạm, các cột giá trị).</summary>
    public static readonly Dictionary<string, (string Func, string[] Keys, string Struct, string[] Values)> BalanceKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["account"] = ("FastBusiness$Balance$Account", new[] { "tk" }, "tk, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt FROM cdtk", new[] { "du_no", "du_co", "du_no_nt", "du_co_nt" }),
        ["customer"] = ("FastBusiness$Balance$Customer", new[] { "tk", "ma_kh" }, "tk, ma_kh, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt FROM cdkh", new[] { "du_no", "du_co", "du_no_nt", "du_co_nt" }),
        ["job"] = ("FastBusiness$Balance$Job", new[] { "tk", "ma_vv" }, "tk, ma_vv, du_no00 AS du_no, du_co00 AS du_co, du_no_nt00 AS du_no_nt, du_co_nt00 AS du_co_nt FROM cdvv", new[] { "du_no", "du_co", "du_no_nt", "du_co_nt" }),
        ["jobcustomer"] = ("FastBusiness$Balance$JobCustomer_new", new[] { "tk", "ma_vv", "ma_kh" }, "tk, ma_vv, ma_kh, ps_no AS du_no, ps_co AS du_co, ps_no_nt AS du_no_nt, ps_co_nt AS du_co_nt FROM wrkgl", new[] { "du_no", "du_co", "du_no_nt", "du_co_nt" }),
        ["contract"] = ("FastBusiness$Balance$Contract", new[] { "tk", "ma_hd" }, "tk, ma_hd, ps_no AS du_no, ps_co AS du_co, ps_no_nt AS du_no_nt, ps_co_nt AS du_co_nt FROM wrkgl", new[] { "du_no", "du_co", "du_no_nt", "du_co_nt" }),
        ["contractcustomer"] = ("FastBusiness$Balance$ContractCustomer_new", new[] { "tk", "ma_hd", "ma_kh" }, "tk, ma_hd, ma_kh, ps_no AS du_no, ps_co AS du_co, ps_no_nt AS du_no_nt, ps_co_nt AS du_co_nt FROM wrkgl", new[] { "du_no", "du_co", "du_no_nt", "du_co_nt" }),
        ["item"] = ("FastBusiness$Balance$Item", new[] { "ma_kho", "ma_vt" }, "ma_kho, ma_vt, ton00 AS so_luong, du00 AS tien, du_nt00 AS tien_nt FROM cdvt", new[] { "so_luong", "tien", "tien_nt" }),
    };

    private static string FilterArg(ReportSpec spec, string col)
    {
        var f = spec.Filters.FirstOrDefault(x => x.Op != "date" && x.Column.EndsWith("." + col, StringComparison.OrdinalIgnoreCase));
        return f is null ? "''" : "@" + ParamName(f);
    }

    /// <summary>Sinh các bước lấy số dư vào #zdk (đầu kỳ, tại @DateFrom) / #zck (cuối kỳ, tại @DateTo), bổ sung các khoá chỉ có số dư mà không phát sinh vào bảng tạm,
    /// và trả về các mệnh đề LEFT JOIN để bước ghép kết quả lấy cột số dư.</summary>
    private static List<string> BalanceStep(StringBuilder sb, ReportSpec spec, string tmp, List<string> rawDims, List<ColumnSpec> measures)
    {
        void L(string s = "") => sb.Append(s).Append(NL);
        var joins = new List<string>();
        var b = spec.Balance!; var k = BalanceKinds[b.Kind];
        var main = spec.MainAlias;
        foreach (var (phase, date, type, on) in new[] { ("dk", "@DateFrom", 1, b.Opening), ("ck", "@DateTo", 2, b.Closing) })
        {
            if (!on) continue;
            var t = "#z" + phase;
            string args = b.Kind.ToLowerInvariant() switch
            {
                "customer" => $"{date}, @Unit, {FilterArg(spec, "tk")}, {FilterArg(spec, "ma_kh")}, {type}, 2, @UserID, @Admin",
                "job" => $"{date}, @Unit, {FilterArg(spec, "tk")}, {FilterArg(spec, "ma_vv")}, {type}, 2, @UserID, @Admin",
                "jobcustomer" => $"{date}, @Unit, {FilterArg(spec, "tk")}, {FilterArg(spec, "ma_vv")}, {FilterArg(spec, "ma_kh")}, {type}, 2, @UserID, @Admin",
                "contract" => $"{date}, @Unit, {FilterArg(spec, "tk")}, {FilterArg(spec, "ma_hd")}, {type}, 2, @UserID, @Admin",
                "contractcustomer" => $"{date}, @Unit, {FilterArg(spec, "tk")}, {FilterArg(spec, "ma_hd")}, {FilterArg(spec, "ma_kh")}, {type}, 2, @UserID, @Admin",
                "item" => $"{date}, @Unit, {FilterArg(spec, "ma_kho")}, {FilterArg(spec, "ma_vt")}, {type}, 2, 2, @UserID, @Admin",
                _ => $"{date}, @Unit, {FilterArg(spec, "tk")}, {type}, 2, @UserID, @Admin",
            };
            L($"\t-- Số dư {(phase == "dk" ? "đầu kỳ" : "cuối kỳ")}");
            L($"\tSELECT TOP 0 {k.Struct.Replace(" FROM ", " INTO " + t + " FROM ")}");
            foreach (var v in k.Values) L($"\tALTER TABLE {t} ALTER COLUMN {v} NUMERIC(28, 6) NULL");      // hàm số dư có thể trả số rộng hơn cột mẫu của bảng nguồn
            L($"\tINSERT INTO {t} EXEC dbo.{k.Func} {args}");
            // khoá có số dư nhưng không phát sinh trong kỳ cũng phải lên báo cáo: chỉ điền KHOÁ số dư (bảng số dư không có các cột chia nhỏ khác như stt_rec, ngày…), cột còn lại để NULL
            var keyDims = rawDims.Where(c => k.Keys.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
            var cols = string.Join(", ", keyDims.Concat(measures.Select(m => m.Name)));
            var vals = string.Join(", ", keyDims.Select(c => "x." + c).Concat(measures.Select(_ => "0")));
            var cond = string.Join(" AND ", keyDims.Select(c => $"{main}.{c} = x.{c}"));
            sb.Append(TemplateStore.Fragment("report_nullable.sql", new Dictionary<string, string> { ["VAR"] = "@nsql_" + phase, ["TBL"] = tmp })).Append(NL);
            L($"\tINSERT INTO {tmp} ({cols}) SELECT DISTINCT {vals} FROM {t} x WHERE NOT EXISTS (SELECT 1 FROM {tmp} {main} WHERE {cond})");
            L();
            joins.Add($"LEFT JOIN {t} z{phase} ON " + string.Join(" AND ", k.Keys.Select(c => $"{main}.{c} = z{phase}.{c}")));
        }
        return joins;
    }

    /// <summary>Kiểm tra khai báo số dư.</summary>
    private static void ValidateBalance(ReportSpec spec, List<string> w)
    {
        var b = spec.Balance;
        if (b is null || string.IsNullOrEmpty(b.Kind)) return;
        if (!BalanceKinds.TryGetValue(b.Kind, out var k)) { w.Add("LỖI: kiểu số dư không hợp lệ: " + b.Kind); return; }
        if (spec.Mode != "voucher") w.Add("LỖI: số dư đầu kỳ / cuối kỳ chỉ dùng cho báo cáo kiểu Chứng từ.");
        if (spec.IsMatrix) w.Add("LỖI: chưa hỗ trợ số dư trong báo cáo Pivot.");
        if (!b.Opening && !b.Closing) w.Add("LỖI: chọn đầu kỳ và/hoặc cuối kỳ cho số dư.");
        var dims = spec.Columns.Where(c => !c.IsMeasure && string.IsNullOrEmpty(c.Bal)).ToList();
        foreach (var key in k.Keys)
            if (!dims.Any(c => { var (a, col, plain) = Parse(c.Source); return plain && a == spec.MainAlias && col.Equals(key, StringComparison.OrdinalIgnoreCase); }))
                w.Add($"LỖI: báo cáo có số dư cần cột nhóm '{key}' của bảng chính (để nối với số dư) — hãy chọn cột {spec.MainAlias}.{key}.");
        foreach (var c in dims)
        {
            var (a, col, plain) = Parse(c.Source);
            if (plain && a == spec.MainAlias && !k.Keys.Contains(col, StringComparer.OrdinalIgnoreCase))
                w.Add($"Lưu ý: cột '{c.Name}' ({c.Source}) chia báo cáo nhỏ hơn khoá số dư ({string.Join(", ", k.Keys)}) — số dư chỉ hiện một lần cho mỗi ({string.Join(", ", k.Keys)}): đầu kỳ ở dòng đầu, cuối kỳ ở dòng cuối.");
        }
        foreach (var c in spec.Columns.Where(c => !string.IsNullOrEmpty(c.Bal)))
        {
            var p = c.Bal.Split(':');
            if (p.Length != 2 || !k.Values.Contains(p[1]) || (p[0] == "dk" && !b.Opening) || (p[0] == "ck" && !b.Closing)) w.Add($"LỖI: cột số dư '{c.Name}' không khớp với kiểu / kỳ số dư đã chọn.");
        }
    }

    private void CatalogBody(ReportSpec spec, List<ProcParam> ps, StringBuilder sb, List<ColumnSpec> cols, List<ColumnSpec> dims, List<ColumnSpec> measures, MatrixSpec? matrix)
    {
        void L(string s = "") => sb.Append(s).Append(NL);
        var main = spec.MainAlias;
        var usedJoins = UsedJoins(spec, cols);

        var fixedCols = "5 AS sysorder, 1 AS sysprint, 1 AS systotal";
        if (spec.Stt) fixedCols += ", " + SttExpr(spec, cols, dims, false);
        var select = cols.Select(c => OutExpr(c, spec, false)).ToList();
        if (matrix is not null) select.AddRange(MatrixKeyCols(matrix, spec, fromTmp: false));

        var conds = new List<string>();
        if (spec.HasStatus) conds.Add($"{main}.status = '1'");
        if (!string.IsNullOrWhiteSpace(spec.UnitColumn)) conds.Add($"(@Unit = '' OR dbo.ff_ExactInlist({main}.{spec.UnitColumn}, @Unit) = 1)");
        if (spec.DateRange && !string.IsNullOrWhiteSpace(spec.DateField)) conds.Add($"{main}.{spec.DateField} BETWEEN @DateFrom AND @DateTo");
        foreach (var f in spec.Filters.Where(f => f.Op != "date" && !string.IsNullOrEmpty(f.Column) && !IsUnitFilter(spec, f)))
        {
            var p = ParamName(f);
            conds.Add(f.Op switch
            {
                "eq" => $"(@{p} = '' OR {f.Column} = RTRIM(@{p}))",
                "in" => $"(@{p} = '' OR dbo.ff_ExactInlist({f.Column}, @{p}) = 1)",
                "inlist" => $"(@{p} = '' OR dbo.ff_Inlist({f.Column}, @{p}) = 1)",
                _ => $"(@{p} = '' OR {f.Column} LIKE RTRIM(@{p}) + '%')",
            });
        }

        L($"\tSELECT {fixedCols}");
        foreach (var s in select) L($"\t\t, {s}");
        L("\t\tINTO #report");
        L($"\t\tFROM {spec.MainTable} {main}");
        foreach (var j in usedJoins) L($"\t\t\t{JoinKw(j)} {j.Table} {j.Alias} ON {JoinOn(j)}");
        for (var i = 0; i < conds.Count; i++) L((i == 0 ? "\t\tWHERE " : "\t\t\tAND ") + conds[i]);
        if (measures.Count > 0)
        {
            var g = new List<string>();
            foreach (var d in dims)
            {
                var (a, c, plain) = Parse(d.Source); g.Add($"{a}.{c}");
                if (!string.IsNullOrEmpty(d.Source2)) { var (a2, c2, _) = Parse(d.Source2); g.Add($"{(string.IsNullOrEmpty(a2) ? a : a2)}.{c2}"); }
            }
            if (matrix is not null) foreach (var k in MatrixKeys(matrix).Distinct()) if (!g.Contains(k, StringComparer.OrdinalIgnoreCase)) g.Add(k);
            if (g.Count > 0) L("\t\tGROUP BY " + string.Join(", ", g.Distinct(StringComparer.OrdinalIgnoreCase)));
        }
        L();
    }

    private static List<JoinSpec> UsedJoins(ReportSpec spec, IEnumerable<ColumnSpec> cols, IEnumerable<string>? extraRefs = null)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Mark(string src)
        {
            foreach (Match m in Regex.Matches(src ?? "", @"\b([A-Za-z_]\w*)\.")) used.Add(m.Groups[1].Value);
        }
        foreach (var c in cols) { Mark(c.Source); Mark(c.Source2); Mark(c.Key); }
        if (extraRefs is not null) foreach (var x in extraRefs) Mark(x);       // cột của bộ lọc trên bảng danh mục: bảng đó phải được nối ở bước 2
        // nối theo chuỗi: join dùng alias của join khác thì kéo theo
        var js = spec.Joins.Where(j => used.Contains(j.Alias) && !(spec.Mode == "voucher" && j.Partitioned)).ToList();
        foreach (var j in js.ToList()) foreach (var dep in spec.Joins.Where(x => Parse(FirstLeft(j)).Alias.Equals(x.Alias, StringComparison.OrdinalIgnoreCase)))
            if (!js.Contains(dep)) js.Insert(0, dep);
        return js;
    }

    // ---- Pivot: khoá dòng / cột và bảng mô tả cột động ----

    private static IEnumerable<string> MatrixKeys(MatrixSpec m)
    {
        foreach (var r in m.Rows) yield return KeyOf(r);
        yield return KeyOf(m.Column);
        foreach (var o in m.ColumnOptions) yield return KeyOf(o.Column);
    }
    private static string KeyOf(ColumnSpec c) => string.IsNullOrEmpty(c.Key) ? c.Source : c.Key;
    private static string PivotDefaultOf(MatrixSpec m) => string.IsNullOrWhiteSpace(m.ColumnDefault) ? (m.ColumnOptions.Count > 0 ? m.ColumnOptions[0].Value : "") : m.ColumnDefault.Trim();
    private static string PivotDefault(ReportSpec spec) => PivotDefaultOf(spec.Matrix!);

    private static IEnumerable<string> MatrixKeyCols(MatrixSpec m, ReportSpec spec, bool fromTmp)
    {
        string K(ColumnSpec c) { var k = KeyOf(c); var (a, col, _) = Parse(k); return fromTmp && a == spec.MainAlias ? $"{spec.MainAlias}.{col}" : k; }
        string Pad(ColumnSpec c) => $"dbo.ff_PadL(CONVERT(VARCHAR(33), {K(c)}), 32)";
        string HeaderOf(ColumnSpec hdr) => hdr.Source.Length > 0 && !string.IsNullOrEmpty(hdr.Source2) ? $"CASE WHEN @Language = 'V' THEN {hdr.Source} ELSE {hdr.Source2} END" : hdr.Source;
        yield return "'[' + " + string.Join(" + '][' + ", m.Rows.Select(Pad)) + " + ']' AS xRow";
        if (m.HasColumnOptions)
        {
            // "Xoay theo" chọn lúc chạy: chiều ngang là cột của lựa chọn đang chọn ở tham số @PivotBy (không khớp lựa chọn nào → lựa chọn mặc định)
            var def = m.ColumnOptions.FirstOrDefault(o => o.Value == PivotDefaultOf(m)) ?? m.ColumnOptions[0];
            string Case(Func<PivotOption, string> expr) =>
                "CASE " + string.Join(" ", m.ColumnOptions.Select(o => $"WHEN @PivotBy = '{Q(o.Value)}' THEN {expr(o)}")) + $" ELSE {expr(def)} END";
            yield return Case(o => $"'[' + {Pad(o.Column)} + ']'") + " AS xColumn";
            yield return "CONVERT(NVARCHAR(512), " + Case(o => HeaderOf(o.Column)) + ") AS xHeader";
        }
        else
        {
            yield return $"'[' + {Pad(m.Column)} + ']' AS xColumn";
            yield return $"CONVERT(NVARCHAR(512), {HeaderOf(m.Column)}) AS xHeader";
        }
    }

    private void PivotTail(StringBuilder sb, ReportSpec spec, MatrixSpec m)
    {
        void L(string s = "") => sb.Append(s).Append(NL);
        L("\t-- View");
        L("\tSELECT * FROM #report ORDER BY " + (spec.Stt ? "stt" : "xRow") + ", xColumn");
        L();
        L("\t-- Pivot: danh sách cột động (tên cột = <số liệu>$<số thứ tự cột>)");
        L("\tCREATE TABLE #xpivot(id INT IDENTITY(1, 1) NOT NULL, header NVARCHAR(128) NULL, header2 NVARCHAR(128) NULL)");
        L("\tINSERT INTO #xpivot(header, header2) SELECT xColumn, MAX(xHeader) FROM #report GROUP BY xColumn ORDER BY xColumn");
        L();
        L("\tCREATE TABLE #xcolumn(id INT, name VARCHAR(64) NULL, header NVARCHAR(128) NULL)");
        for (var i = 0; i < m.Values.Count; i++)
        {
            var v = m.Values[i];
            var h = m.Values.Count == 1 ? "''" : $"CASE WHEN @Language = 'V' THEN N'{Q(v.HeaderVi)}' ELSE N'{Q(v.HeaderEn)}' END";
            L($"\tINSERT INTO #xcolumn SELECT {i + 1}, '{v.Name}$', {h}");
        }
        L();
        L("\tCREATE TABLE #pivot (id INT NULL, id2 INT NULL, name VARCHAR(64) NULL, header NVARCHAR(128) NULL)");
        L("\tINSERT INTO #pivot SELECT a.id, b.id, b.name + LTRIM(a.id), RTRIM(a.header2) + b.header FROM #xpivot a CROSS JOIN #xcolumn b ORDER BY a.id, b.id");
        L("\tSELECT id, name, header FROM #pivot ORDER BY id, id2");
    }
}
