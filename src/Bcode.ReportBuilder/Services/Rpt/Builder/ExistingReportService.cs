using System.Text.RegularExpressions;
using System.Xml.Linq;
using Bcode.ReportBuilder;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services.Rpt.Builder;

public sealed record GridColumnInfo(string Name, string HeaderVi, string HeaderEn, string Type, int Width, string Format, string Aggregate, bool Hidden);
public sealed record ReportMenuItem(string Id, string Path, string Title, string TitleEn, string Link, string Controller, string Kind);   // Kind: report | other (danh mục / cập nhật) | unknown (không đọc được Filter)

/// <summary>Kết quả phân tích một báo cáo có sẵn: đặc tả dựng lại để chỉnh trong trình thiết kế + những gì suy ra được / không suy ra được.</summary>
public sealed class ReportAnalysis
{
    public string Controller { get; set; } = "";
    public string MainFile { get; set; } = "";
    public string ProcName { get; set; } = "";
    public string? Error { get; set; }
    public bool Encrypted { get; set; }
    public bool Pivot { get; set; }
    public ReportSpec Spec { get; set; } = new();
    public List<string> Tables { get; set; } = new();           // "a · m21 (bảng chính, chứng từ)"
    public List<string> Mapped { get; set; } = new();           // cột đã gắn vào bảng nguồn
    public List<string> Unmapped { get; set; } = new();         // cột của Grid mà procedure gốc tính ra (không suy được nguồn)
    public List<string> OtherTables { get; set; } = new();      // bảng khác thấy trong procedure nhưng chưa dùng cột nào
    public List<string> Notes { get; set; } = new();
    public List<GridColumnInfo> GridColumns { get; set; } = new();     // mọi cột của Grid gốc (theo thứ tự) — dùng để tự tick theo tên khi bạn thêm bảng
}

/// <summary>
/// "Chỉnh báo cáo có sẵn": liệt kê menu báo cáo của chương trình (bảng wcommand), rồi với báo cáo được chọn đọc Filter / Grid / Report trong Source Path và procedure trong App Data,
/// phân tích procedure để biết các bảng đang JOIN (bí danh, điều kiện nối, kiểu nối), gắn các cột của Grid về bảng nguồn và dựng lại <see cref="ReportSpec"/> —
/// người dùng tick thêm trường, chỉnh rồi sinh lại procedure / Filter / Grid / Excel. Chỉ ĐỌC; phân tích dựa trên chữ của procedure (không chạy), nên những cột do logic phức tạp
/// tính ra sẽ được liệt kê riêng ở <see cref="ReportAnalysis.Unmapped"/>.
/// </summary>
public sealed class ExistingReportService
{
    private readonly Func<bool, SqlConnection> _connect;
    private readonly Func<string> _sourcePath;
    private readonly ReportMetaService _meta;
    private readonly Func<Task<List<MenuRow>>>? _menu;

    public ExistingReportService(Func<bool, SqlConnection> connect, Func<string> sourcePath, ReportMetaService meta, Func<Task<List<MenuRow>>>? menu = null) { _connect = connect; _sourcePath = sourcePath; _meta = meta; _menu = menu; }

    // ---------------------------------------------------------------- danh sách menu báo cáo
    public async Task<List<ReportMenuItem>> ListAsync(CancellationToken ct = default)
    {
        var rows = new Dictionary<string, (string Parent, string Bar, string Bar2, string Link, string Sys)>(StringComparer.OrdinalIgnoreCase);
        var fromHost = _menu is null ? null : await _menu();
        if (fromHost is { Count: > 0 }) foreach (var m in fromHost) rows[m.Id] = (m.ParentId, m.Title, m.TitleEn, m.Link, m.SysId);
        else
        {
            await using var conn = _connect(true);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT wmenu_id, wmenu_id0, bar, bar2, link, sysid FROM wcommand", conn) { CommandTimeout = 60 };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            string S(int i) => r.IsDBNull(i) ? "" : r.GetString(i).Trim();
            while (await r.ReadAsync(ct)) rows[S(0)] = (S(1), S(2), S(3), S(4), S(5));
        }
        static string Clean(string t) => Regex.Replace(t ?? "", @"\s*\(\*\)\s*$", "").Trim();
        string PathOf(string id)
        {
            var parts = new List<string>(); var cur = rows.TryGetValue(id, out var x) ? x.Parent : "";
            for (var i = 0; i < 8 && !string.IsNullOrEmpty(cur) && rows.TryGetValue(cur, out var p); i++) { parts.Insert(0, Clean(p.Bar)); cur = p.Parent; }
            return string.Join(" › ", parts);
        }
        return rows.Where(kv => (kv.Value.Link ?? "").EndsWith(".aspx", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(kv.Value.Sys))
            .Select(kv => new ReportMenuItem(kv.Key, PathOf(kv.Key), Clean(kv.Value.Bar), Clean(kv.Value.Bar2), kv.Value.Link.Trim(), kv.Value.Sys.Trim(), "unknown"))
            .OrderBy(m => m.Path, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Phân loại báo cáo / màn hình danh mục bằng cách đọc đầu file Filter — nhiều file (có thể ở thư mục mạng) nên chạy NỀN, không chặn giao diện.</summary>
    public List<ReportMenuItem> WithKinds(List<ReportMenuItem> items) => items.Select(m => m with { Kind = KindOf(m.Controller) }).ToList();

    /// <summary>Đọc phần đầu file Filter: gốc &lt;dir type="Report"…&gt; là báo cáo; không có type=Report là màn hình danh mục / cập nhật.</summary>
    private string KindOf(string controller)
    {
        var root = _sourcePath(); if (string.IsNullOrWhiteSpace(root)) return "unknown";
        foreach (var p in new[] { Path.Combine(root, "App_Data", "Controllers", "Filter", controller + ".xml"), Path.Combine(root, "Filter", controller + ".xml") })
        {
            if (!File.Exists(p)) continue;
            try
            {
                // đọc cả file (nhỏ, và hàm này chạy nền): phần DOCTYPE đầu file có thể dài hàng nghìn ký tự nên không thể chỉ nhìn vài KB đầu
                var xml = File.ReadAllText(p);
                if (Regex.IsMatch(xml, @"<dir\b[^>]*\btype\s*=\s*""Report""", RegexOptions.IgnoreCase)) return "report";
                if (Regex.IsMatch(xml, @"event\s*=\s*""Processing""[\s\S]{0,800}?\bexec(?:ute)?\s", RegexOptions.IgnoreCase)) return "report";   // có lệnh chạy procedure → coi là báo cáo
                return Regex.IsMatch(xml, @"<dir", RegexOptions.IgnoreCase) ? "other" : "unknown";
            }
            catch { return "unknown"; }
        }
        return "unknown";
    }

    // ---------------------------------------------------------------- phân tích
    private string? ReadSource(string kind, string controller)
    {
        var root = _sourcePath(); if (string.IsNullOrWhiteSpace(root)) return null;
        foreach (var p in new[] { Path.Combine(root, "App_Data", "Controllers", kind, controller + ".xml"), Path.Combine(root, kind, controller + ".xml") })
            if (File.Exists(p)) { try { return File.ReadAllText(p); } catch { /* đang bị khoá */ } }
        return null;
    }

    private static XDocument? ParseXml(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        var s = Regex.Replace(xml, @"<!DOCTYPE[^>\[]*(\[[\s\S]*?\])?\s*>", "");
        s = Regex.Replace(s, @"&(?!(?:amp|lt|gt|quot|apos|#\d+|#x[0-9a-fA-F]+);)[\w\.]+;", "");     // &XMLWhenReportLoading; … (file Include không có ở đây)
        try { return XDocument.Parse(s.TrimStart('﻿', ' ', '\r', '\n', '\t')); } catch { return null; }
    }

    private static IEnumerable<XElement> Els(XContainer? d, string name) => d?.Descendants().Where(e => e.Name.LocalName == name) ?? Enumerable.Empty<XElement>();
    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    private static string Attr(XElement e, string n) => (string?)e.Attribute(n) ?? "";

    private sealed record TRef(string Table, string Alias, bool Partitioned, string Join, List<(string A1, string C1, string A2, string C2)> On, int Order);
    private static readonly HashSet<string> Kw = new(StringComparer.OrdinalIgnoreCase)
    { "on", "where", "left", "right", "inner", "outer", "join", "group", "order", "union", "select", "set", "with", "cross", "full", "into", "having", "and", "or", "as", "insert", "values", "from", "case", "when", "then", "else", "end", "nolock", "update", "delete", "exec", "begin", "declare" };

    public async Task<ReportAnalysis> AnalyzeAsync(string controller, string link, string menuTitle, CancellationToken ct = default)
    {
        var a = new ReportAnalysis { Controller = controller, MainFile = Path.GetFileNameWithoutExtension(link) };
        if (string.IsNullOrWhiteSpace(_sourcePath())) { a.Error = "Workspace hiện tại chưa khai Source Path (File > Choose Server) nên chưa đọc được file Filter / Grid của báo cáo."; return a; }
        var filterXml = ReadSource("Filter", controller); var gridXml = ReadSource("Grid", controller);
        if (filterXml is null && gridXml is null) { a.Error = $"Không thấy Filter / Grid của báo cáo '{controller}' trong Source Path ({_sourcePath()})."; return a; }
        var fd = ParseXml(filterXml); var gd = ParseXml(gridXml);
        if (gridXml is not null && gd is null) a.Notes.Add("Không đọc được file Grid (XML lỗi).");
        if (filterXml is not null && fd is null) a.Notes.Add("Không đọc được file Filter (XML lỗi).");
        a.Pivot = gridXml?.Contains("<pivot", StringComparison.OrdinalIgnoreCase) == true;
        if (a.Pivot) a.Notes.Add("Báo cáo gốc là PIVOT: tool mở các cột của Grid như bảng thường — cấu hình Hàng / Cột ngang / Giá trị cần đặt lại bằng nút Pivot.");

        // ---- procedure
        var cmdText = fd is null ? "" : string.Join("\n", Els(fd, "command").Where(c => Attr(c, "event").Equals("Processing", StringComparison.OrdinalIgnoreCase)).Select(c => c.Value));
        var m = Regex.Matches(cmdText, @"\bexec(?:ute)?\s+(?:\[?dbo\]?\.)?\[?(\w+)\]?", RegexOptions.IgnoreCase).Cast<Match>().FirstOrDefault(x => !x.Groups[1].Value.StartsWith("sp_", StringComparison.OrdinalIgnoreCase));
        a.ProcName = m?.Groups[1].Value ?? "";
        var rootDir = fd?.Root; var isReportFilter = rootDir is null || Regex.IsMatch(rootDir.ToString().Split('>')[0], @"type\s*=\s*""Report""", RegexOptions.IgnoreCase);
        if (fd is not null && !isReportFilter && a.ProcName.Length == 0) { a.Error = $"“{controller}” là màn hình DANH MỤC / CẬP NHẬT dữ liệu (Filter không phải loại Report), không phải báo cáo tính từ procedure nên không có bảng join để phân tích. Hãy chọn một báo cáo khác trong menu."; return a; }
        a.Notes.Add($"Filter: {(fd is null ? "KHÔNG đọc được" : $"đọc được, {Els(fd, "field").Count()} ô nhập")}; Grid: {(gd is null ? "KHÔNG đọc được" : $"đọc được, {Els(gd, "field").Select(f => Attr(f, "name")).Distinct().Count()} cột")}.");
        string proc = "";
        if (a.ProcName.Length == 0) a.Notes.Add("Không thấy lệnh exec procedure trong Filter (command Processing) — không phân tích được các bảng nguồn.");
        else
        {
            try
            {
                await using var conn = _connect(false); await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand("SELECT OBJECT_ID(@n), (SELECT m.definition FROM sys.sql_modules m WHERE m.object_id = OBJECT_ID(@n))", conn) { CommandTimeout = 60 };
                cmd.Parameters.AddWithValue("@n", "dbo." + a.ProcName);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                await rd.ReadAsync(ct);
                var exists = !rd.IsDBNull(0); var def = rd.IsDBNull(1) ? "" : rd.GetString(1);
                if (def.Length > 0) { proc = def; a.Notes.Insert(0, $"Procedure '{a.ProcName}': đọc được ({def.Length:N0} ký tự)."); }
                else if (!exists) { a.Encrypted = true; a.Notes.Insert(0, $"Procedure '{a.ProcName}' KHÔNG tồn tại trong database App Data đang chọn (kiểm tra đúng workspace / database)."); }
                else { a.Encrypted = true; a.Notes.Insert(0, $"Procedure '{a.ProcName}' bị MÃ HOÁ (WITH ENCRYPTION) nên không đọc được nội dung — không phân tích được bảng nguồn."); }
            }
            catch (Exception ex) { a.Notes.Add("Không đọc được procedure: " + ex.Message); }
        }
        var text = Regex.Replace(Regex.Replace(proc, @"/\*[\s\S]*?\*/", " "), @"--[^\r\n]*", " ");

        // ---- các bảng / nối
        var refs = FindTables(text);
        var schema = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var firstCol = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in refs.Select(r => (r.Table, r.Partitioned)).Distinct())
        {
            try { var cl = await _meta.GetColumnsAsync(t.Table, t.Partitioned, ct); schema[t.Table] = cl.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); if (cl.Count > 0) firstCol[t.Table] = cl[0].Name; }
            catch { schema[t.Table] = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
        }
        TRef? FindRef(string alias, string? col = null) => refs.Where(r => r.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) && schema.ContainsKey(r.Table) && schema[r.Table].Count > 0)
            .OrderByDescending(r => col is not null && schema[r.Table].Contains(col) ? 1 : 0).ThenBy(r => r.Order).FirstOrDefault();

        // ---- cột của Grid (mỗi tên một lần — Grid có thể liệt kê cột ở cả fields và views)
        var gridFields = Els(gd, "field").Where(f => Attr(f, "name").Length > 0).GroupBy(f => Attr(f, "name"), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var spec = a.Spec;
        spec.TitleVi = Els(gd, "title").Select(t => Attr(t, "v")).FirstOrDefault(x => x.Length > 0) ?? menuTitle;
        spec.TitleEn = Els(gd, "title").Select(t => Attr(t, "e")).FirstOrDefault(x => x.Length > 0) ?? "";
        spec.Stt = gridFields.Any(f => Attr(f, "name").Equals("stt", StringComparison.OrdinalIgnoreCase));
        spec.Kind = "table"; spec.MainAlias = "a";

        var rename = new Dictionary<string, (string Alias, string Col)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match x in Regex.Matches(text, @"\b(\w+)\.(\w+)\s+(?:AS\s+)?(\w+)\b(?=\s*(?:,|\r|\n|INTO\b|FROM\b|$))", RegexOptions.IgnoreCase))
            if (!Kw.Contains(x.Groups[3].Value) && FindRef(x.Groups[1].Value, x.Groups[2].Value) is { } fr && schema[fr.Table].Contains(x.Groups[2].Value) && !rename.ContainsKey(x.Groups[3].Value) && !x.Groups[2].Value.Equals(x.Groups[3].Value, StringComparison.OrdinalIgnoreCase))
                rename[x.Groups[3].Value] = (x.Groups[1].Value, x.Groups[2].Value);
        var bilingual = new Dictionary<string, (string A1, string C1, string A2, string C2)>(StringComparer.OrdinalIgnoreCase);
        foreach (Match x in Regex.Matches(text, @"CASE\s+WHEN\s+@Language\s*=\s*'V'\s+THEN\s+(\w+)\.(\w+)\s+ELSE\s+(\w+)\.(\w+)\s+END\s+(?:AS\s+)?(\w+)", RegexOptions.IgnoreCase))
            bilingual[x.Groups[5].Value] = (x.Groups[1].Value, x.Groups[2].Value, x.Groups[3].Value, x.Groups[4].Value);

        foreach (var f in gridFields)
        {
            var gn = Attr(f, "name"); if (gn.ToLowerInvariant() is "stt" or "sysorder" or "sysprint" or "systotal" or "xrow" or "xcolumn" or "xheader") continue;
            var gh = Child(f, "header"); var gty = Attr(f, "type");
            a.GridColumns.Add(new GridColumnInfo(gn, gh is null ? gn : Attr(gh, "v"), gh is null ? gn : Attr(gh, "e"), gty is "Decimal" or "DateTime" or "Int" ? gty : "String",
                int.TryParse(Attr(f, "width"), out var gw) && gw > 0 ? gw : 100, Attr(f, "dataFormatString"), Attr(f, "aggregate").Equals("Sum", StringComparison.OrdinalIgnoreCase) && gty is "Decimal" or "Int" ? "Sum" : "", Attr(f, "hidden").Equals("true", StringComparison.OrdinalIgnoreCase)));
        }
        var cols = new List<(ColumnSpec Col, TRef Src, TRef? Src2)>();
        foreach (var f in gridFields)
        {
            var name = Attr(f, "name"); var lower = name.ToLowerInvariant();
            if (lower is "stt" or "sysorder" or "sysprint" or "systotal" or "xrow" or "xcolumn" or "xheader") continue;
            TRef? src = null, src2 = null; string col = name, col2 = "";
            if (bilingual.TryGetValue(name, out var b) && FindRef(b.A1, b.C1) is { } r1 && schema[r1.Table].Contains(b.C1)) { src = r1; col = b.C1; if (FindRef(b.A2, b.C2) is { } r2 && schema[r2.Table].Contains(b.C2)) { src2 = r2; col2 = b.C2; } }
            else if (rename.TryGetValue(name, out var rn) && FindRef(rn.Alias, rn.Col) is { } r3 && schema[r3.Table].Contains(rn.Col)) { src = r3; col = rn.Col; }
            else
            {
                // alias.name có trong procedure, hoặc cột trùng tên chỉ có ở một bảng
                var viaText = Regex.Matches(text, @"\b(\w+)\." + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase).Cast<Match>().Select(x => FindRef(x.Groups[1].Value, name)).FirstOrDefault(r => r is not null && schema[r.Table].Contains(name));
                src = viaText ?? (refs.Where(r => schema.ContainsKey(r.Table) && schema[r.Table].Contains(name)).DistinctBy(r => r.Table).Count() == 1 ? refs.First(r => schema[r.Table].Contains(name)) : null);
            }
            if (src is null) { a.Unmapped.Add(name); continue; }
            var h = Child(f, "header");
            var cs = new ColumnSpec
            {
                Name = name, Source = col, Source2 = col2, HeaderVi = h is null ? name : Attr(h, "v"), HeaderEn = h is null ? name : Attr(h, "e"),
                Type = Attr(f, "type") is "Decimal" or "DateTime" or "Int" ? Attr(f, "type") : "String",
                Width = int.TryParse(Attr(f, "width"), out var w) && w > 0 ? w : 100, Format = Attr(f, "dataFormatString"),
                Aggregate = Attr(f, "aggregate").Equals("Sum", StringComparison.OrdinalIgnoreCase) && Attr(f, "type") is "Decimal" or "Int" ? "Sum" : "", Hidden = Attr(f, "hidden").Equals("true", StringComparison.OrdinalIgnoreCase),
            };
            cols.Add((cs, src, src2));
        }

        // bảng chính = bảng FROM (ưu tiên bảng chứng từ) có nhiều cột của Grid nhất
        int Score(TRef r) => cols.Count(c => c.Src.Table == r.Table);
        var froms = refs.Where(r => r.Join == "from" && schema.ContainsKey(r.Table) && schema[r.Table].Count > 0).ToList();
        var main = froms.OrderByDescending(r => r.Partitioned ? 1 : 0).ThenByDescending(Score).ThenBy(r => r.Order).FirstOrDefault()
                   ?? cols.Select(c => c.Src).OrderByDescending(Score).FirstOrDefault();
        if (main is null && proc.Length > 0) a.Notes.Add("Không tìm thấy bảng chính (FROM) trong procedure.");
        if (main is not null)
        {
            for (var i = 0; i < cols.Count; i++)
            {
                var (cc, s1, s2) = cols[i];
                var n1 = s1.Table == main.Table && s1.Partitioned == main.Partitioned ? main : s1;           // procedure nối chính bảng đó lần nữa (bí danh khác): coi như bảng chính
                var n2 = s2 is not null && s2.Table == main.Table && s2.Partitioned == main.Partitioned ? main : s2;
                if (cc.Aggregate.Length > 0 && !n1.Partitioned && n1.Table != main.Table) { cc.Aggregate = ""; a.Notes.Add($"Cột '{cc.Name}' lấy từ bảng danh mục {n1.Table} nên không cộng dồn (Tổng) được — giữ như cột thường."); }
                cols[i] = (cc, n1, n2);
            }
        }
        spec.Mode = main is { Partitioned: true } ? "voucher" : "catalog";
        spec.MainTable = main?.Table ?? "";
        spec.UnitColumn = Regex.Match(text, @"GetUnitFilter\(\s*'(?:\w+\.)?(\w+)'", RegexOptions.IgnoreCase) is { Success: true } um ? um.Groups[1].Value : "";
        if (main is not null && spec.UnitColumn.Length > 0 && !schema[main.Table].Contains(spec.UnitColumn)) spec.UnitColumn = "";
        spec.HasStatus = main is not null && schema[main.Table].Contains("status") && Regex.IsMatch(text, @"\bstatus\b", RegexOptions.IgnoreCase);
        var dm = Regex.Match(text, @"Partition\$Execute\s+@q\s*,\s*[^,]+,\s*'(?:\w+\.)?(\w+)'", RegexOptions.IgnoreCase);
        spec.DateField = dm.Success ? dm.Groups[1].Value : "ngay_ct";
        if (main is not null && !schema[main.Table].Contains(spec.DateField)) spec.DateField = new[] { "ngay_ct", "ngay_lct", "ngay" }.FirstOrDefault(c => schema[main.Table].Contains(c)) ?? schema[main.Table].FirstOrDefault(c => c.StartsWith("ngay", StringComparison.OrdinalIgnoreCase)) ?? spec.DateField;
        var dateFilterFields = Els(fd, "field").Count(f => Attr(f, "type").Equals("DateTime", StringComparison.OrdinalIgnoreCase));
        spec.DateRange = spec.Mode == "voucher" || dateFilterFields >= 2;

        // bản đồ bí danh cũ → bí danh mới (a, b, c…) chỉ cho các bảng thật sự dùng
        var newAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // alias cũ|bảng → alias mới
        TRef Canon(TRef r) => main is not null && r.Table == main.Table && r.Partitioned == main.Partitioned ? main : r;
        string Key(TRef r) => r.Alias.ToLowerInvariant() + "|" + r.Table.ToLowerInvariant();
        var used = new List<TRef>(); if (main is not null) used.Add(main);
        const string Letters = "abcdefghijklmnop";
        string Assign(TRef r) { if (!newAlias.TryGetValue(Key(r), out var n)) { n = Letters[Math.Min(newAlias.Count, Letters.Length - 1)].ToString(); newAlias[Key(r)] = n; } return n; }
        if (main is not null) Assign(main);

        // bảng được dùng + chuỗi nối tới bảng chính
        var queue = new List<TRef>(cols.Select(c => c.Src).Concat(cols.Where(c => c.Src2 is not null).Select(c => c.Src2!)));
        for (var i = 0; i < queue.Count; i++)
        {
            var r = queue[i]; if (main is not null && Key(r) == Key(main) || used.Any(u => Key(u) == Key(r))) continue;
            used.Add(r); Assign(r);
            foreach (var p in r.On) { var other = string.Equals(p.A1, r.Alias, StringComparison.OrdinalIgnoreCase) ? p.A2 : p.A1; if (FindRef(other) is { } dep) queue.Add(Canon(dep)); }
        }
        foreach (var (c, s, s2) in cols)
        {
            c.Source = newAlias[Key(s)] + "." + c.Source;
            if (s2 is not null) c.Source2 = newAlias[Key(s2)] + "." + c.Source2; else c.Source2 = "";
            a.Mapped.Add(c.Name);
            spec.Columns.Add(c);
        }
        foreach (var r in used)
        {
            var al = newAlias[Key(r)];
            a.Tables.Add($"{al} · {r.Table} ({(main is not null && Key(r) == Key(main) ? "bảng chính, " : "")}{(r.Partitioned ? "chứng từ" : "danh mục")})");
            if (main is not null && Key(r) == Key(main)) continue;
            var pairs = r.On.Where(p => string.Equals(p.A1, r.Alias, StringComparison.OrdinalIgnoreCase) || string.Equals(p.A2, r.Alias, StringComparison.OrdinalIgnoreCase)).ToList();
            var mine = new List<string>(); var theirs = new List<string>();
            foreach (var p in pairs)
            {
                var self1 = string.Equals(p.A1, r.Alias, StringComparison.OrdinalIgnoreCase); var oa = self1 ? p.A2 : p.A1; var oc = self1 ? p.C2 : p.C1; var mc = self1 ? p.C1 : p.C2;
                var other = FindRef(oa, oc); if (other is not null) other = Canon(other); if (other is null || !newAlias.TryGetValue(Key(other), out var oal)) continue;
                mine.Add(mc); theirs.Add(oal + "." + oc);
            }
            if (mine.Count == 0 && main is not null)                       // procedure không ghi điều kiện nối rõ: suy ra theo khoá chung
            {
                var mainAl = newAlias[Key(main)];
                // 1) "cây quan hệ đã học" (KnownRelations — khoá ngoại đã thấy thật trong các procedure từng phân tích): thử bảng chính trước, rồi các bảng khác đã nối
                (string FromCol, string ToCol, TRef Other, string OtherAl)? known = null;
                if (KnownRelations.Instance.TryGet(r.Table, main.Table) is { } relMain && schema[r.Table].Contains(relMain.FromCol) && schema[main.Table].Contains(relMain.ToCol))
                    known = (relMain.FromCol, relMain.ToCol, main, mainAl);
                else
                    foreach (var other in used)
                    {
                        if (Key(other) == Key(r) || (main is not null && Key(other) == Key(main))) continue;
                        if (KnownRelations.Instance.TryGet(r.Table, other.Table) is { } rel && schema[r.Table].Contains(rel.FromCol) && schema[other.Table].Contains(rel.ToCol))
                        { known = (rel.FromCol, rel.ToCol, other, newAlias[Key(other)]); break; }
                    }
                if (known is { } kk) { mine.Add(kk.FromCol); theirs.Add(kk.OtherAl + "." + kk.ToCol); a.Notes.Add($"Điều kiện nối của {r.Table} ({al}) lấy theo khoá đã biết ({kk.FromCol} = {kk.OtherAl}.{kk.ToCol}) — kiểm tra lại nếu khác ý."); }
                else
                {
                    var recs = new[] { "stt_rec", "stt_rec0" }.Where(c => schema[r.Table].Contains(c) && schema[main!.Table].Contains(c)).ToList();
                    if (recs.Count > 0 && r.Partitioned) { foreach (var c in recs) { mine.Add(c); theirs.Add(mainAl + "." + c); } }
                    else if (firstCol.TryGetValue(r.Table, out var fc) && schema[main!.Table].Contains(fc)) { mine.Add(fc); theirs.Add(mainAl + "." + fc); a.Notes.Add($"Điều kiện nối của {r.Table} ({al}) được suy ra theo khoá {fc} — kiểm tra lại."); }
                }
            }
            spec.Joins.Add(new JoinSpec { Table = r.Table, Alias = al, Left = string.Join(",", theirs), Right = string.Join(",", mine), Type = r.Join.Contains("inner") || r.Join == "join" ? "inner" : "left", Partitioned = r.Partitioned && spec.Mode == "voucher" });
            if (theirs.Count == 0) a.Notes.Add($"Chưa suy ra được điều kiện nối của bảng {r.Table} ({al}) — chọn cột nối ở khối “Chọn bảng & trường”.");
        }
        a.OtherTables = refs.Where(r => schema.ContainsKey(r.Table) && schema[r.Table].Count > 0 && !used.Any(u => Key(u) == Key(r))).Select(r => r.Table).Distinct().ToList();

        // ---- bộ lọc từ Filter
        var usedParams = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var unboundFilters = 0;
        foreach (var f in Els(fd, "field"))
        {
            var name = Attr(f, "name"); if (name.Length == 0) continue;
            var items = Child(f, "items");
            if (Attr(f, "type").Equals("DateTime", StringComparison.OrdinalIgnoreCase) || name.Equals("mau_bc", StringComparison.OrdinalIgnoreCase) || (Attr(f, "external")).Equals("true", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.EndsWith("_yn", StringComparison.OrdinalIgnoreCase)) continue;      // ô tick tuỳ chọn của báo cáo, không phải điều kiện lọc theo cột
            var h = Child(f, "header");
            var fs = new FilterSpec { Field = name, HeaderVi = h is null ? name : Attr(h, "v"), HeaderEn = h is null ? name : Attr(h, "e") };
            if (items is not null)
            {
                fs.Style = Attr(items, "style"); fs.Controller = Attr(items, "controller"); fs.Reference = Attr(items, "reference");
                fs.Key = Attr(items, "key") is { Length: > 0 } k ? k : fs.Key; fs.Check = Attr(items, "check") is { Length: > 0 } ck ? ck : fs.Check; fs.Information = Attr(items, "information");
                foreach (var it in Els(items, "item")) { var tx = Child(it, "text"); fs.Items.Add(new ComboItem { Value = Attr(it, "value"), Vi = tx is null ? "" : Attr(tx, "v"), En = tx is null ? "" : Attr(tx, "e") }); }
            }
            foreach (var r in used)
                if (schema.TryGetValue(r.Table, out var sc) && sc.Contains(name)) { fs.Column = newAlias[Key(r)] + "." + name; break; }
            if (fs.Column.Length == 0 && name.Equals("ma_dvcs", StringComparison.OrdinalIgnoreCase)) fs.Column = spec.MainAlias + "." + (spec.UnitColumn.Length > 0 ? spec.UnitColumn : "ma_dvcs");
            else if (fs.Column.Length == 0) { fs.Op = "param"; unboundFilters++; }       // vẫn hiện trên form lọc, nhưng chưa lọc theo cột nào
            {
                var pn = ReportGenerator.DefaultParamName(fs);
                if (!pn.Equals("Unit", StringComparison.OrdinalIgnoreCase) && usedParams.Contains(pn))
                {
                    var pascal = string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(x => char.ToUpperInvariant(x[0]) + x[1..])); fs.Param = usedParams.Contains(pascal) ? pascal + "2" : pascal; pn = fs.Param;
                }
                usedParams.Add(pn); spec.Filters.Add(fs);
            }
        }

        if (unboundFilters > 0) a.Notes.Add($"{unboundFilters} ô nhập của Filter chưa gắn được cột để lọc — vẫn hiện trên form lọc dạng “Chỉ nhập (không lọc)”; muốn lọc theo cột nào thì đổi “Cách lọc” hoặc tick “Lọc” ở trường tương ứng.");
        if (a.Unmapped.Count > 0) a.Notes.Add($"{a.Unmapped.Count} cột của Grid do procedure gốc TÍNH RA (không lấy thẳng từ một bảng): {string.Join(", ", a.Unmapped.Take(12))}{(a.Unmapped.Count > 12 ? "…" : "")}. Cột này không có trong bản dựng lại — thêm bằng “Thêm cột tính toán” nếu cần.");
        var cursor = Regex.IsMatch(text, @"\bcursor\b", RegexOptions.IgnoreCase); var dyn = Regex.Matches(text, @"sp_executesql|EXEC\s*\(", RegexOptions.IgnoreCase).Count; var temps = Regex.Matches(text, @"#\w+").Cast<Match>().Select(x => x.Value.ToLowerInvariant()).Distinct().Count();
        if (cursor || temps > 4) a.Notes.Add($"Procedure gốc khá phức tạp ({(cursor ? "có cursor, " : "")}{temps} bảng tạm, {dyn} SQL động): bản sinh lại chỉ phủ phần lấy dữ liệu chính — hãy chạy thử và đối chiếu với báo cáo gốc trước khi dùng thay thế.");
        if (spec.Columns.Count == 0 && proc.Length > 0) a.Notes.Add("Không gắn được cột nào của Grid về bảng nguồn.");
        return a;
    }

    private static List<TRef> FindTables(string text)
    {
        var list = new List<TRef>(); var order = 0;
        // bảng mà procedure GHI vào (INSERT / DELETE / UPDATE …) là bảng làm việc, không phải nguồn dữ liệu (vd wrkgl)
        var written = Regex.Matches(text, @"\b(?:insert\s+(?:into\s+)?|delete\s+(?:from\s+)?|truncate\s+table\s+|update\s+)\[?(?:dbo\.)?(\w+)\]?", RegexOptions.IgnoreCase).Cast<Match>().Select(x => x.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // "rest" phải DỪNG LẠI trước từ khoá from/join KẾ TIẾP (lazy + lookahead), không được "nuốt" luôn các JOIN sau trong cùng câu FROM —
        // trước đây rest là [^;]{0,500} THAM (greedy) nên một FROM/JOIN nuốt gọn tới 500 ký tự kế tiếp, làm Regex.Matches (không chồng lấn)
        // nhảy qua mất các JOIN khác ngay sau nó trong cùng câu lệnh (vd "FROM dmhd a LEFT JOIN dmkh b ON … LEFT JOIN dmnvbh c ON … LEFT JOIN dmvv d ON …"
        // chỉ bắt được "dmhd a", mất hẳn dmkh/dmnvbh/dmvv) — khiến nhiều cột của Grid lấy thẳng từ các bảng đó bị coi nhầm là "procedure tính ra".
        const string NextKw = @"(?:left\s+(?:outer\s+)?join|right\s+(?:outer\s+)?join|inner\s+join|full\s+(?:outer\s+)?join|cross\s+join|join|from|where|group|order|union|having|insert|select|set)";
        foreach (Match m in Regex.Matches(text, @"(?<j>\b(?:left\s+(?:outer\s+)?join|right\s+(?:outer\s+)?join|inner\s+join|full\s+(?:outer\s+)?join|cross\s+join|join|from)\b)\s+(?<t>\[?[#@\w\$%\.]+\]?)(?<rest>[\s\S]{0,500}?)(?=;|\s*\b" + NextKw + @"\b|$)", RegexOptions.IgnoreCase))
        {
            var j = m.Groups["j"].Value.ToLowerInvariant();
            var raw = m.Groups["t"].Value.Trim('[', ']').Replace("[", "").Replace("]", "");
            if (raw.StartsWith('#') || raw.StartsWith('@') || raw.Length == 0) continue;
            var before = text[Math.Max(0, m.Index - 6000)..m.Index];
            var sel = before.LastIndexOf("select", StringComparison.OrdinalIgnoreCase);          // câu SELECT chứa FROM này
            if (sel >= 0 && j.StartsWith("from") && Regex.IsMatch(before[sel..], @"^select\s+top\s*\(?\s*0\s*\)?", RegexOptions.IgnoreCase) && Regex.IsMatch(before[sel..], @"into\s+#", RegexOptions.IgnoreCase)) continue;   // chỉ lấy cấu trúc cột
            var rest = m.Groups["rest"].Value;
            if (rest.TrimStart().StartsWith("(") && !Regex.IsMatch(rest, @"^\s*\(\s*nolock", RegexOptions.IgnoreCase) && !Regex.IsMatch(rest, @"^\s*with", RegexOptions.IgnoreCase)) continue;   // hàm / bảng dẫn xuất
            raw = Regex.Replace(raw, @"^dbo\.", "", RegexOptions.IgnoreCase);
            if (raw.Contains('.') ) continue;
            var partitioned = Regex.IsMatch(raw, @"\$(%|\d{6})", RegexOptions.IgnoreCase) && !raw.StartsWith("$");
            var table = raw.Contains('$') ? raw[..raw.IndexOf('$')] : raw;
            if (table.Length == 0 || Kw.Contains(table) || written.Contains(table) || table.StartsWith("wrk", StringComparison.OrdinalIgnoreCase)) continue;
            var am = Regex.Match(rest, @"^\s*(?:with\s*\([^)]*\)\s*)?(?:\(\s*nolock\s*\)\s*)?(?:with\s*\([^)]*\)\s*)?(?:as\s+)?(?<a>\w+)", RegexOptions.IgnoreCase);
            var alias = am.Success && !Kw.Contains(am.Groups["a"].Value) ? am.Groups["a"].Value : table;
            var on = new List<(string, string, string, string)>();
            var om = Regex.Match(rest, @"\bon\b(?<c>[\s\S]{0,300}?)(?=\b(?:left|right|inner|full|cross|join|where|group|order|union|having|insert|select|set)\b|$)", RegexOptions.IgnoreCase);
            if (om.Success) foreach (Match p in Regex.Matches(om.Groups["c"].Value, @"(\w+)\.(\w+)\s*=\s*(\w+)\.(\w+)")) on.Add((p.Groups[1].Value, p.Groups[2].Value, p.Groups[3].Value, p.Groups[4].Value));
            var jk = j.StartsWith("from") ? "from" : j.Contains("left") ? "left join" : j.Contains("inner") ? "inner join" : j.Contains("right") ? "right join" : "join";
            if (list.Any(x => x.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) && x.Table.Equals(table, StringComparison.OrdinalIgnoreCase)))
            {
                var ex = list.First(x => x.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) && x.Table.Equals(table, StringComparison.OrdinalIgnoreCase));
                foreach (var p in on) if (!ex.On.Contains(p)) ex.On.Add(p);
                continue;
            }
            list.Add(new TRef(table, alias, partitioned, jk, on, order++));
        }
        return list;
    }
}
