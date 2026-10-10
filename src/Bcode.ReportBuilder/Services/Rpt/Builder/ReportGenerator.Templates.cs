using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services.Rpt.Builder;

/// <summary>
/// Phần của <see cref="ReportGenerator"/> sinh khung procedure TỪ FILE MẪU (<c>Templates\fileSource\BuildReport</c>, xem <see cref="TemplateStore"/>) cùng các bước nhóm nhiều cấp /
/// "Nhóm theo" / "Xoay theo" chọn lúc chạy. Filter / Grid / Report / Main / Excel KHÔNG sinh ở đây — chuyển procedure sang chế độ "Từ procedure có sẵn" để sinh.
/// </summary>
public sealed partial class ReportGenerator
{
    private static string Frag(string file, Dictionary<string, string>? values = null, params (string Name, bool On)[] toggles) =>
        TemplateStore.Fragment(file, values ?? new Dictionary<string, string>(), toggles);

    // =====================================================================================================================
    //  Nhóm nhiều cấp
    // =====================================================================================================================

    private static void ValidateGroups(ReportSpec spec, List<string> w)
    {
        if (spec.Groups.Count == 0) return;
        if (spec.IsMatrix) { w.Add("Nhóm chỉ dùng cho báo cáo dạng bảng — pivot bỏ qua phần nhóm."); return; }
        if (spec.Groups.Count > 5) w.Add("LỖI: nhóm tối đa 5 cấp.");
        var dimNames = spec.Columns.Where(c => !c.IsMeasure && string.IsNullOrEmpty(c.Bal) && string.IsNullOrEmpty(c.Formula)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < spec.Groups.Count; i++)
        {
            var g = spec.Groups[i];
            if (string.IsNullOrWhiteSpace(g.Column)) { w.Add($"LỖI: cấp nhóm {i + 1} chưa chọn cột."); continue; }
            if (!dimNames.Contains(g.Column)) w.Add($"LỖI: cột nhóm '{g.Column}' (cấp {i + 1}) phải là một cột dạng chữ / mã / ngày đang có trên báo cáo (không phải cột số liệu, số dư hay công thức).");
            if (!seen.Add(g.Column)) w.Add($"LỖI: cột '{g.Column}' bị dùng cho hai cấp nhóm.");
            if (!string.IsNullOrWhiteSpace(g.Label) && !dimNames.Contains(g.Label)) w.Add($"LỖI: cột nhãn '{g.Label}' (cấp {i + 1}) không có trong báo cáo.");
            if (!g.Header && !g.Subtotal) w.Add($"Nhóm {i + 1} chưa bật “dòng tên nhóm” lẫn “dòng cộng” nên chỉ dùng để sắp xếp, không có dòng riêng. Tick một trong hai nếu muốn thấy dòng nhóm.");
        }
    }

    /// <summary>Hàm gộp khi cộng một nhóm: số liệu Đếm thì CỘNG các số đếm, Min/Max/Avg giữ nguyên hàm, mặc định (Sum, số dư) là SUM.</summary>
    private static string SubtotalFn(ColumnSpec c) => c.Aggregate switch { "Min" => "MIN", "Max" => "MAX", "Avg" => "AVG", _ => "SUM" };

    /// <summary>
    /// Thêm dòng nhóm vào #report (đã có các dòng chi tiết sysorder = 5): với mỗi cấp k, hạng nhóm <c>xg<i>k</i></c> = DENSE_RANK theo khoá cấp 1..k; dòng tiêu đề (4) có hạng các cấp sâu hơn = 0
    /// nên đứng TRƯỚC chi tiết, dòng cộng (6) có hạng các cấp sâu hơn = số lớn nhất nên đứng SAU; sắp xếp cuối cùng theo xg1..xgn rồi sysorder. Cột công thức không cộng — được tính lại
    /// bởi <c>FormulaUpdates</c> chạy ngay sau bước này.
    /// </summary>
    private static void GroupRows(StringBuilder sb, ReportSpec spec, List<ColumnSpec> cols) => GroupRows(sb, spec, cols, spec.Groups, alter: true);

    /// <param name="alter">true = thêm cột xid / xg1..xgn vào #report; false = đã thêm sẵn (nhóm theo lựa chọn lúc chạy thêm một lần rồi mỗi lựa chọn dùng lại).</param>
    private static void GroupRows(StringBuilder sb, ReportSpec spec, List<ColumnSpec> cols, List<GroupSpec> g, bool alter)
    {
        var n = g.Count;
        ColumnSpec Col(string name) => cols.First(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        string KeyOrder(int i) => g[i].Column + (Col(g[i].Column).Order == "desc" ? " DESC" : "");
        string Xg(int i) => "xg" + (i + 1);
        var allXg = string.Join(", ", Enumerable.Range(0, n).Select(Xg));
        string XgSel(int upTo, string rest) => string.Join(", ", Enumerable.Range(0, n).Select(i => i < upTo ? Xg(i) : rest));
        var sums = cols.Where(c => (c.IsMeasure || !string.IsNullOrEmpty(c.Bal)) && string.IsNullOrEmpty(c.Formula)).ToList();
        string LabelOf(int i) => string.IsNullOrWhiteSpace(g[i].Label) || g[i].Label.Equals(g[i].Column, StringComparison.OrdinalIgnoreCase) ? "" : g[i].Label;

        if (alter) sb.Append(Frag("report_nullable.sql", new() { ["VAR"] = "@nsql_g", ["TBL"] = "#report" })).Append(NL);
        sb.Append(Frag("group_setup.sql", new()
        {
            ["LEVELS"] = n.ToString(),
            ["XGCOLS"] = string.Join(", ", Enumerable.Range(0, n).Select(i => Xg(i) + " INT NULL")),
            ["RANKS"] = string.Join(", ", Enumerable.Range(0, n).Select(k => $"DENSE_RANK() OVER (ORDER BY {string.Join(", ", Enumerable.Range(0, k + 1).Select(KeyOrder))}) AS r{k + 1}")),
            ["RANKSET"] = string.Join(", ", Enumerable.Range(0, n).Select(i => $"{Xg(i)} = w.r{i + 1}")),
            ["XGLIST"] = allXg,
        }, ("Stt", spec.Stt), ("Alter", alter))).Append(NL);

        for (var k = 0; k < n; k++)
        {
            var label = LabelOf(k);
            var groupBy = string.Join(", ", Enumerable.Range(0, k + 1).Select(Xg).Append(g[k].Column));
            if (g[k].Header)
            {
                var hc = new List<string> { g[k].Column }; var hs = new List<string> { g[k].Column };
                if (label.Length > 0) { hc.Add(label); hs.Add($"MAX({label})"); }
                if (g[k].HeaderTotals)
                    foreach (var c in sums.Where(c => !c.Name.Equals(g[k].Column, StringComparison.OrdinalIgnoreCase) && !c.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
                    { hc.Add(c.Name); hs.Add($"{SubtotalFn(c)}({c.Name})"); }
                sb.Append(Frag("group_header.sql", new()
                {
                    ["COLS"] = string.Join(", ", hc),
                    ["XGCOLS"] = allXg,
                    ["SEL"] = string.Join(", ", hs),
                    ["XGSEL"] = XgSel(k + 1, "0"),
                    ["GROUPBY"] = groupBy,
                })).Append(NL);
            }
            if (g[k].Subtotal)
            {
                var cs = new List<string> { g[k].Column };
                var ss = new List<string> { g[k].Column };
                if (label.Length > 0) { cs.Add(label); ss.Add(Frag("group_subtotal_label.sql", new() { ["LABEL"] = label })); }
                foreach (var c in sums.Where(c => !c.Name.Equals(g[k].Column, StringComparison.OrdinalIgnoreCase) && !c.Name.Equals(label, StringComparison.OrdinalIgnoreCase)))
                { cs.Add(c.Name); ss.Add($"{SubtotalFn(c)}({c.Name})"); }
                sb.Append(Frag("group_subtotal.sql", new()
                {
                    ["COLS"] = string.Join(", ", cs),
                    ["XGCOLS"] = allXg,
                    ["SEL"] = string.Join(", ", ss),
                    ["XGSEL"] = XgSel(k + 1, "2147483647"),
                    ["GROUPBY"] = groupBy,
                })).Append(NL);
            }
        }

        if (spec.GroupGrandTotal)
        {
            var label = LabelOf(0);
            var cs = new List<string>(); var ss = new List<string>();
            if (label.Length > 0) { cs.Add(label); ss.Add(Frag("group_grand_label.sql")); }
            foreach (var c in sums.Where(c => !c.Name.Equals(label, StringComparison.OrdinalIgnoreCase))) { cs.Add(c.Name); ss.Add($"{SubtotalFn(c)}({c.Name})"); }
            if (cs.Count > 0)
                sb.Append(Frag("group_grand.sql", new()
                {
                    ["COLS"] = string.Join(", ", cs), ["XGCOLS"] = allXg, ["SEL"] = string.Join(", ", ss), ["XGSEL"] = XgSel(0, "2147483647"),
                })).Append(NL);
        }
        sb.Append(NL);
    }

    /// <summary>Một lựa chọn "Nhóm theo" có thể gộp NHIỀU cấp: <see cref="GroupOption.Column"/> / <see cref="GroupOption.Label"/> là danh sách cách nhau dấu phẩy (cấp 1 trước), vd "stt_rec,so_ct,ma_phi".</summary>
    private static List<GroupSpec> OptionLevels(GroupOption o)
    {
        var cs = (o.Column ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ls = (o.Label ?? "").Split(',', StringSplitOptions.TrimEntries);
        return cs.Select((c, i) => new GroupSpec { Column = c, Label = i < ls.Length ? ls[i] : "", Header = o.Header, Subtotal = o.Subtotal, HeaderTotals = o.HeaderTotals, HideKey = o.HideKey }).ToList();
    }

    /// <summary>
    /// Báo cáo chia nhỏ hơn khoá số dư (vd theo từng chứng từ trong khi số dư theo tk + ma_kh): số dư KHÔNG lặp ở từng dòng mà chỉ hiện một lần cho mỗi khoá —
    /// đầu kỳ ở dòng đầu của khoá, cuối kỳ ở dòng cuối (các dòng còn lại để trống). Nhờ vậy dòng cộng nhóm / tổng cộng (SUM) cộng đúng, như các báo cáo sổ chi tiết của Fast.
    /// </summary>
    private static void BalanceOnce(StringBuilder sb, ReportSpec spec, List<ColumnSpec> cols)
    {
        if (string.IsNullOrEmpty(spec.Balance?.Kind) || !BalanceKinds.TryGetValue(spec.Balance.Kind, out var k)) return;
        var keyCols = new List<string>(); var finer = false;
        foreach (var c in cols.Where(c => !c.IsMeasure && string.IsNullOrEmpty(c.Bal) && string.IsNullOrEmpty(c.Formula)))
        {
            var (a, col, plain) = Parse(c.Source);
            if (!plain || a != spec.MainAlias) continue;
            if (k.Keys.Contains(col, StringComparer.OrdinalIgnoreCase)) keyCols.Add(c.Name); else finer = true;
        }
        if (!finer || keyCols.Count != k.Keys.Length) return;
        var part = string.Join(", ", keyCols);
        var ord = spec.Stt ? "stt" : "(SELECT 0)";
        sb.Append(Frag("report_nullable.sql", new() { ["VAR"] = "@nsql_b", ["TBL"] = "#report" })).Append(NL);
        sb.Append("	ALTER TABLE #report ADD xb INT IDENTITY(1, 1) NOT NULL").Append(NL);
        foreach (var (pre, rn, dir) in new[] { ("dk", "rf", ""), ("ck", "rl", " DESC") })
        {
            var bal = cols.Where(c => !string.IsNullOrEmpty(c.Bal) && c.Bal.StartsWith(pre + ":")).Select(c => c.Name).ToList();
            if (bal.Count == 0) continue;
            sb.Append($"	-- Số dư {(pre == "dk" ? "đầu kỳ chỉ ở dòng đầu" : "cuối kỳ chỉ ở dòng cuối")} của mỗi ({part})").Append(NL);
            sb.Append($"	;WITH w AS (SELECT xb, ROW_NUMBER() OVER (PARTITION BY {part} ORDER BY {ord}{dir}) AS {rn} FROM #report)").Append(NL);
            sb.Append($"		UPDATE #report SET {string.Join(", ", bal.Select(n => n + " = NULL"))} FROM #report r JOIN w ON w.xb = r.xb WHERE w.{rn} > 1").Append(NL).Append(NL);
        }
        sb.Append("	ALTER TABLE #report DROP COLUMN xb").Append(NL).Append(NL);
    }

    /// <summary>"Nhóm theo" chọn lúc chạy: thêm cột xid / xg1 một lần (để ORDER BY luôn hợp lệ kể cả khi chọn Không nhóm), rồi mỗi lựa chọn là một khối <c>IF @GroupBy = '…'</c> nhóm 1 cấp theo cột của nó.</summary>
    private static void DynGroupRows(StringBuilder sb, ReportSpec spec, List<ColumnSpec> cols)
    {
        sb.Append(Frag("report_nullable.sql", new() { ["VAR"] = "@nsql_d", ["TBL"] = "#report" })).Append(NL);
        var maxLv = spec.GroupOptions.Max(o => OptionLevels(o).Count);
        sb.Append(Frag("group_dyn_alter.sql", new() { ["XGCOLS"] = string.Join(", ", Enumerable.Range(1, maxLv).Select(i => $"xg{i} INT NULL")) })).Append(NL);
        foreach (var o in spec.GroupOptions)
        {
            sb.Append($"\tIF @GroupBy = '{Q(o.Value)}'").Append(NL).Append("\tBEGIN").Append(NL);
            var one = OptionLevels(o);
            var tmp = new StringBuilder();
            GroupRows(tmp, spec, cols, one, alter: false);
            sb.Append(tmp.ToString().TrimEnd('\r', '\n')).Append(NL).Append("\tEND").Append(NL).Append(NL);
        }
    }

    /// <summary>"Xoay theo" chọn lúc chạy: #report còn mang cột của mọi lựa chọn nên một ô (hàng × cột ngang) có thể rải trên nhiều dòng — gộp lại theo hàng + chiều ngang đã chọn rồi bỏ các cột đó.</summary>
    private static void PivotCollapse(StringBuilder sb, ReportSpec spec)
    {
        var m = spec.Matrix!;
        sb.Append(Frag("pivot_collapse.sql", new()
        {
            ["SETS"] = string.Join(", ", m.Values.Select(v => $"r.{v.Name} = x.{v.Name}")),
            ["SUMS"] = string.Join(", ", m.Values.Select(v => $"{SubtotalFn(v)}({v.Name}) AS {v.Name}")),
        })).Append(NL).Append(NL);
    }

    /// <summary>Cột khoá / nhãn nhóm cần để trống ở dòng chi tiết (HideKey) → điều kiện SQL áp dụng: nhóm cố định = luôn; nhóm chọn lúc chạy = chỉ khi @GroupBy là lựa chọn đó.</summary>
    private static Dictionary<string, string> HiddenKeyConditions(ReportSpec spec)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string col, string cond) { if (string.IsNullOrWhiteSpace(col)) return; if (!map.TryGetValue(col, out var l)) map[col] = l = new(); l.Add(cond); }
        if (spec.HasGroups) foreach (var g in spec.Groups.Where(g => g.HideKey)) { Add(g.Column, "1 = 1"); Add(g.Label, "1 = 1"); }
        else if (spec.HasDynGroup) foreach (var o in spec.GroupOptions.Where(o => o.HideKey)) { var c = $"@GroupBy = '{Q(o.Value)}'"; foreach (var g in OptionLevels(o)) { Add(g.Column, c); Add(g.Label, c); } }
        return map.ToDictionary(kv => kv.Key, kv => string.Join(" OR ", kv.Value));
    }

    private static void ValidateDynamic(ReportSpec spec, List<string> w)
    {
        var fields = new HashSet<string>(spec.Filters.Select(f => f.Field), StringComparer.OrdinalIgnoreCase) { "tu_ngay", "den_ngay", "mau_bc" };
        if (spec.Groups.Count > 0 && spec.GroupOptions.Count > 0) w.Add("LỖI: đã nhóm cố định nhiều cấp thì không dùng thêm \"Nhóm theo\" chọn lúc chạy — chọn một trong hai.");
        if (spec.HasDynGroup)
        {
            var dimNames = spec.Columns.Where(c => !c.IsMeasure && string.IsNullOrEmpty(c.Bal) && string.IsNullOrEmpty(c.Formula)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!Regex.IsMatch(spec.GroupField ?? "", @"^[a-z][a-z0-9_]*$", RegexOptions.IgnoreCase)) w.Add("LỖI: tên field \"Nhóm theo\" không hợp lệ.");
            else if (!fields.Add(spec.GroupField)) w.Add($"LỖI: field '{spec.GroupField}' của \"Nhóm theo\" trùng với một ô lọc khác.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "0" };
            foreach (var o in spec.GroupOptions)
            {
                if (string.IsNullOrWhiteSpace(o.Value) || !Regex.IsMatch(o.Value, @"^[A-Za-z0-9_]{1,10}$")) w.Add($"LỖI: giá trị lựa chọn \"Nhóm theo\" '{o.Value}' phải là chữ/số tối đa 10 ký tự (khác 0).");
                else if (!seen.Add(o.Value)) w.Add($"LỖI: lựa chọn \"Nhóm theo\" '{o.Value}' bị trùng.");
                var lv = OptionLevels(o);
                if (lv.Count == 0) w.Add($"LỖI: lựa chọn \"Nhóm theo\" '{o.Vi}' chưa chọn cột nào.");
                if (lv.Count > 5) w.Add($"LỖI: lựa chọn \"Nhóm theo\" '{o.Vi}' tối đa 5 cấp.");
                foreach (var g in lv)
                {
                    if (!dimNames.Contains(g.Column)) w.Add($"LỖI: lựa chọn \"Nhóm theo\" '{o.Vi}' dùng cột '{g.Column}' không có trong các cột dạng chữ / mã của báo cáo.");
                    if (!string.IsNullOrWhiteSpace(g.Label) && !dimNames.Contains(g.Label)) w.Add($"LỖI: cột nhãn '{g.Label}' của lựa chọn \"Nhóm theo\" '{o.Vi}' không có trong báo cáo.");
                }
                if (lv.Select(g => g.Column).Distinct(StringComparer.OrdinalIgnoreCase).Count() != lv.Count) w.Add($"LỖI: lựa chọn \"Nhóm theo\" '{o.Vi}' dùng một cột cho hai cấp.");
            }
            if (!seen.Contains(spec.GroupDefault ?? "0")) w.Add($"LỖI: giá trị mặc định của \"Nhóm theo\" ('{spec.GroupDefault}') không nằm trong các lựa chọn.");
        }
        if (spec.IsMatrix && spec.Matrix!.HasColumnOptions)
        {
            var m = spec.Matrix!;
            if (!Regex.IsMatch(m.ColumnField ?? "", @"^[a-z][a-z0-9_]*$", RegexOptions.IgnoreCase)) w.Add("LỖI: tên field \"Xoay theo\" không hợp lệ.");
            else if (!fields.Add(m.ColumnField)) w.Add($"LỖI: field '{m.ColumnField}' của \"Xoay theo\" trùng với một ô lọc khác.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in m.ColumnOptions)
            {
                if (string.IsNullOrWhiteSpace(o.Value) || !Regex.IsMatch(o.Value, @"^[A-Za-z0-9_]{1,10}$")) w.Add($"LỖI: giá trị lựa chọn \"Xoay theo\" '{o.Value}' phải là chữ/số tối đa 10 ký tự.");
                else if (!seen.Add(o.Value)) w.Add($"LỖI: lựa chọn \"Xoay theo\" '{o.Value}' bị trùng.");
                if (string.IsNullOrWhiteSpace(o.Column.Source) || string.IsNullOrWhiteSpace(o.Column.Name)) w.Add($"LỖI: lựa chọn \"Xoay theo\" '{o.Vi}' chưa chọn trường.");
            }
            if (!string.IsNullOrWhiteSpace(m.ColumnDefault) && !seen.Contains(m.ColumnDefault.Trim())) w.Add($"LỖI: giá trị mặc định của \"Xoay theo\" ('{m.ColumnDefault}') không nằm trong các lựa chọn.");
        }
    }

    // =====================================================================================================================
    //  PROCEDURE (khung từ file mẫu; thân từng loại báo cáo vẫn do VoucherBody / CatalogBody / PivotTail dựng)
    // =====================================================================================================================

    public string Procedure(ReportSpec spec, List<ProcParam> ps)
    {
        spec = Fold(spec);
        var body = new StringBuilder();
        var cols = AllColumns(spec).ToList();
        var dims = cols.Where(c => !c.IsMeasure && string.IsNullOrEmpty(c.Bal) && string.IsNullOrEmpty(c.Formula)).ToList();
        var measures = cols.Where(c => c.IsMeasure).ToList();
        var matrix = spec.IsMatrix ? spec.Matrix! : null;

        if (spec.Mode == "voucher") VoucherBody(spec, ps, body, cols, dims, measures, matrix);
        else CatalogBody(spec, ps, body, cols, dims, measures, matrix);
        if (matrix is not null && spec.HasPivotOptions) PivotCollapse(body, spec);
        if (matrix is null) BalanceOnce(body, spec, cols);
        if (spec.HasGroups) GroupRows(body, spec, cols);
        else if (spec.HasDynGroup) DynGroupRows(body, spec, cols);
        FormulaUpdates(body, cols, spec.HasGroups || spec.HasDynGroup ? " WHERE sysorder <> 4" : "");   // dòng tiêu đề nhóm để trống số liệu

        // SELECT kết quả + (pivot) bảng mô tả cột động
        if (matrix is not null) PivotTail(body, spec, matrix);
        else if (spec.HasGroups || spec.HasDynGroup)
        {
            var shown = new List<string> { "sysorder", "sysprint", "systotal" };
            if (spec.Stt) shown.Add("stt");
            var hide = HiddenKeyConditions(spec);
            shown.AddRange(cols.Select(c => hide.TryGetValue(c.Name, out var cond) ? $"CASE WHEN sysorder = 5 AND ({cond}) THEN NULL ELSE {c.Name} END AS {c.Name}" : c.Name));
            var order = string.Join(", ", Enumerable.Range(1, spec.HasGroups ? spec.Groups.Count : spec.GroupOptions.Max(o => OptionLevels(o).Count)).Select(i => "xg" + i)) + ", sysorder, " + (spec.Stt ? "stt" : "xid");
            body.Append(Frag("proc_view.sql", new() { ["COLS"] = string.Join(", ", shown), ["ORDER"] = order }));
        }
        else body.Append(Frag("proc_view.sql", new() { ["COLS"] = "*", ["ORDER"] = spec.Stt ? "stt" : "sysorder" }));

        var notes = new StringBuilder();
        if (spec.HasDynGroup)
            notes.Append($"\t-- @GroupBy ({spec.GroupHeaderVi}): 0 = {spec.GroupNoneVi}; " + string.Join("; ", spec.GroupOptions.Select(o => $"{o.Value} = {o.Vi}")) + NL);
        if (spec.HasPivotOptions)
            notes.Append($"\t-- @PivotBy ({spec.Matrix!.ColumnHeaderVi}): " + string.Join("; ", spec.Matrix.ColumnOptions.Select(o => $"{o.Value} = {o.Vi}")) + NL);
        if (notes.Length > 0) body.Insert(0, notes.Append(NL).ToString());
        var values = new Dictionary<string, string>
        {
            ["CREATEDAT"] = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
            ["PROCNAME"] = spec.ProcName,
            ["PARAMS"] = string.Join(NL, ps.Select((p, i) => $"\t@{p.Name} {p.SqlType}{(i < ps.Count - 1 ? "," : "")}")),
            ["BODY"] = body.ToString().TrimEnd('\r', '\n'),
            ["SAMPLE"] = string.Join(", ", ps.Select(p => p.Sample)),
        };
        return TemplateStore.Render(@"Procedure\report.sql", values, ("DropIfExists", spec.DropIfExists));
    }
}
