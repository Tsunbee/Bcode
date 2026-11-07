using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Tra cứu (lookup) của từng ô trong form FBO đang mở, đọc từ source dự án (CHỈ ĐỌC): file <c>Dir\&lt;controller&gt;.f|xml</c> của menu
/// (+ lưới chi tiết <c>items style="Grid" controller="SVDetail"</c> → <c>Grid\SVDetail.*</c>), mỗi ô có
/// <c>&lt;items controller="Customer" key="status = '1' and kh_yn = 1" check="…"/&gt;</c>; controller tra cứu → <c>Dir\Customer.*</c>
/// → <c>&lt;dir table="dmkh" code="ma_kh"&gt;</c>. "🎲 Điền mẫu" dùng để lấy mã thoả đúng điều kiện key của ô (không thì FBO báo mã không tồn tại).
/// </summary>
public static class FboFormLookups
{
    /// <param name="Key">Điều kiện where của ô tra cứu (đã giải mã &amp;amp; …); rỗng nếu không có / còn entity chưa giải được.</param>
    /// <param name="Reference">Ô tên FBO tự điền khi tra mã (reference="ten_kh%l" → "ten_kh").</param>
    /// <param name="Filter">filter của controller Lookup (áp thêm cho mọi ô dùng controller này).</param>
    /// <param name="Sys">Bảng tra cứu nằm ở DB Sys (Lookup database="Sys", vd userinfo2).</param>
    public sealed record Lookup(string Field, string Controller, string Table, string Code, string Key, string Check, bool Grid,
        string Reference = "", string Filter = "", bool Sys = false);

    /// <param name="Table">Bảng chính của form (dmvt khi Thêm vật tư, m81$000000 khi Hóa đơn).</param>
    /// <param name="Code">Cột mã của bảng chính (ma_vt…; chứng từ là stt_rec).</param>
    /// <param name="VoucherId">dir id="HDA" của chứng từ — giá trị @@id trong key (ma_ct = @@id).</param>
    public sealed record Form(string File, string Table, string Code, string VoucherId, bool IsVoucher, IReadOnlyDictionary<string, Lookup> Fields)
    {
        /// <summary>Field bắt buộc (allowNulls="false") của form chính — đọc cả file include (entity SYSTEM) như FCode, bỏ khối IGNORE.</summary>
        public IReadOnlySet<string> Required { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Field bắt buộc của các lưới chi tiết.</summary>
        public IReadOnlySet<string> GridRequired { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Bảng của các lưới chi tiết (grid table="d21$000000").</summary>
        public IReadOnlyList<string> GridTables { get; init; } = Array.Empty<string>();
    }

    private static readonly Regex DirTag = new(@"<dir\b([^>]*)>", RegexOptions.IgnoreCase);
    // field tự đóng (<field name="x" .../>) phải khớp riêng, không thì "(.*?)</field>" nuốt luôn field kế tiếp
    private static readonly Regex FieldRe = new(@"<field\s+name\s*=\s*""([^""]+)""([^>]*?)(?:/>|>(.*?)</field>)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex GridTag = new(@"<grid\b([^>]*)>", RegexOptions.IgnoreCase);
    private static readonly Regex NotNull = new(@"\ballowNulls\s*=\s*""false""", RegexOptions.IgnoreCase);
    // thuộc tính có thể chứa '>' (key="… and ngay_ct >= …") — chỉ dừng ở '>' NGOÀI dấu nháy
    private static readonly Regex ItemsRe = new(@"<items\b((?:[^>""]|""[^""]*"")*?)/?>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex AttrRe = new(@"([\w:]+)\s*=\s*""([^""]*)""", RegexOptions.Singleline);
    private static readonly Regex EntityDecl = new(@"<!ENTITY\s+([A-Za-z_][\w.\-]*)\s+""([^""]*)""\s*>", RegexOptions.Singleline);
    private static readonly Regex EntityRef = new(@"&([A-Za-z_][\w.\-]*);");

    /// <summary>Entity nội bộ khai trong DOCTYPE của file (&lt;!ENTITY Tag "dmkh"&gt;, &lt;!ENTITY k "ma_kh"&gt;) — Customer.f viết table="&amp;Tag;" code="&amp;k;".
    /// Entity SYSTEM (file ngoài) / tham số (%) không đọc.</summary>
    private static Dictionary<string, string> Entities(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        var end = text.IndexOf("]>", StringComparison.Ordinal);
        foreach (Match m in EntityDecl.Matches(end > 0 ? text[..end] : text)) d.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        return d;
    }

    private static string Resolve(string v, Dictionary<string, string> ents)
    {
        for (var i = 0; i < 5 && v.Contains('&'); i++)
        {
            var nv = EntityRef.Replace(v, m => ents.TryGetValue(m.Groups[1].Value, out var r) ? r : m.Value);
            if (nv == v) break;
            v = nv;
        }
        return v;
    }

    /// <summary>File controller (Dir / Grid) theo tên: bản sửa sau cùng giữa .xml và .f.</summary>
    public static string? FindFile(string sourceRoot, string folder, string name)
    {
        var dir = Path.Combine(SourceIndexService.ControllersDir(sourceRoot), folder);
        string? best = null; var bestAt = DateTime.MinValue;
        foreach (var ext in new[] { ".xml", ".f" })
        {
            var p = Path.Combine(dir, name + ext);
            try { if (File.Exists(p) && File.GetLastWriteTimeUtc(p) is var t && t > bestAt) { best = p; bestAt = t; } } catch { }
        }
        return best;
    }

    private static Dictionary<string, string> Attrs(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRe.Matches(s)) d[m.Groups[1].Value] = m.Groups[2].Value;
        return d;
    }

    /// <summary>Giá trị thuộc tính XML → chữ thật (&amp;amp; &amp;lt; &amp;quot; &amp;#37;…); còn entity tự đặt (&amp;Tag;) thì không dùng được → rỗng.</summary>
    private static string Decode(string v, Dictionary<string, string> ents)
    {
        var d = WebUtility.HtmlDecode(Resolve(v, ents));
        return Regex.IsMatch(d, @"&[A-Za-z_][\w.\-]*;") ? "" : d.Trim();
    }

    /// <summary>Nguồn của 1 controller tra cứu: bảng (hoặc view) + cột mã, điều kiện filter riêng của controller, bảng nằm ở DB Sys (database="Sys").</summary>
    public sealed record Source(string Table, string Code, string Filter, bool Sys);

    private static readonly ConcurrentDictionary<string, (DateTime At, Source? Src)> SrcCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex LookupTag = new(@"<lookup\b((?:[^>""]|""[^""]*"")*)>", RegexOptions.IgnoreCase);

    /// <summary>
    /// Bảng + cột mã mà FBO THẬT SỰ tra cho controller của ô AutoComplete: <c>Controllers\Lookup\&lt;ctrl&gt;.xml</c> (thẻ &lt;lookup table code filter database&gt;)
    /// trước, không có thì <c>Dir\&lt;ctrl&gt;.*</c> (thẻ &lt;dir&gt;); tên có hậu tố (BIPlant.Right) không thấy thì thử bỏ hậu tố. Cột mã ghép
    /// ("ma_ct, loai_ct, ma_gd") lấy cột CUỐI (cột người dùng gõ). Nhớ 10 phút.
    /// (Trước đây chỉ đọc Dir\ → ~1/3 ô tra cứu bị coi là ô thường / tra nhầm bảng: Customer của KOG tra view zvdmkh, UOMItem tra vdmvtqddvt…)
    /// </summary>
    public static Source? SourceOf(string sourceRoot, string controller)
    {
        var key = sourceRoot + "|" + controller;
        if (SrcCache.TryGetValue(key, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(10)) return c.Src;
        Source? res = null;
        try
        {
            var names = new List<string> { controller };
            if (controller.LastIndexOf('.') is var dot and > 0) names.Add(controller[..dot]);
            foreach (var n in names)
            {
                if (FindFile(sourceRoot, "Lookup", n) is { } lf && Read(lf, LookupTag) is { } ls) { res = ls; break; }
                if (FindFile(sourceRoot, "Dir", n) is { } df && Read(df, DirTag) is { } ds) { res = ds; break; }
            }
        }
        catch { /* ổ mạng / quyền — không có thông tin tra cứu */ }
        SrcCache[key] = (DateTime.UtcNow, res);
        return res;

        static Source? Read(string path, Regex tag)
        {
            using var sr = new StreamReader(path);
            var buf = new char[200_000];
            var head = new string(buf, 0, sr.ReadBlock(buf, 0, buf.Length));
            if (tag.Match(head) is not { Success: true } m) return null;
            var ents = Entities(head);
            var a = Attrs(Resolve(m.Groups[1].Value, ents));
            if (!a.TryGetValue("table", out var t) || !a.TryGetValue("code", out var code) || t.Contains('&') || code.Contains('&')) return null;
            var col = code.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
            if (string.IsNullOrEmpty(col) || t.Trim().Length == 0) return null;
            var filter = a.TryGetValue("filter", out var fl) ? Decode(fl, ents) : "";
            var sys = a.TryGetValue("database", out var db) && db.Trim().Equals("Sys", StringComparison.OrdinalIgnoreCase);
            return new Source(t.Trim(), col, filter, sys);
        }
    }

    /// <summary>Bảng + cột mã của controller tra cứu — xem <see cref="SourceOf"/>.</summary>
    public static (string Table, string Code)? TableOf(string sourceRoot, string controller) =>
        SourceOf(sourceRoot, controller) is { } s ? (s.Table, s.Code) : null;

    /// <summary>Đọc form của controller <paramref name="dirController"/> (Dir\…) + các lưới chi tiết của nó. null = không thấy file.</summary>
    public static Form? Load(string sourceRoot, string dirController)
    {
        var file = FindFile(sourceRoot, "Dir", dirController);
        if (file is null) return null;
        string text;
        try { text = File.ReadAllText(file); } catch { return null; }
        var dirEnts = Entities(text);
        var dirAttrs = DirTag.Match(text) is { Success: true } dm ? Attrs(Resolve(dm.Groups[1].Value, dirEnts)) : new();
        var fields = new Dictionary<string, Lookup>(StringComparer.OrdinalIgnoreCase);
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gridRequired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var gridTables = new List<string>();
        var grids = new List<string>();
        CollectFile(file, text, grid: false);
        foreach (var g in grids.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            if (FindFile(sourceRoot, "Grid", g) is { } gf)
                try
                {
                    var gt = File.ReadAllText(gf);
                    if (GridTag.Match(gt) is { Success: true } gm && Attrs(Resolve(gm.Groups[1].Value, Entities(gt))).TryGetValue("table", out var tb) && !tb.Contains('&'))
                        gridTables.Add(tb.Trim());
                    CollectFile(gf, gt, grid: true);
                }
                catch { }
        dirAttrs.TryGetValue("table", out var table);
        dirAttrs.TryGetValue("code", out var code);
        dirAttrs.TryGetValue("id", out var vid);
        var isVoucher = dirAttrs.TryGetValue("type", out var type) && type.Equals("Voucher", StringComparison.OrdinalIgnoreCase);
        return new Form(file, table ?? "", code ?? "", vid is { } v && !v.Contains('&') ? v : "", isVoucher, fields)
        {
            Required = required, GridRequired = gridRequired, GridTables = gridTables,
        };

        // Đọc như FCode: thân file + các file include (entity SYSTEM gọi trong thân), DTD có hiệu lực (khối IGNORE bỏ) — field khai ở include cũng tính.
        void CollectFile(string path, string src, bool grid)
        {
            DtdIncludeResolver.Result res;
            try { res = DtdIncludeResolver.Resolve(path, src, p => { try { return File.ReadAllText(p); } catch { return null; } }); }
            catch { res = new DtdIncludeResolver.Result(new List<string> { "", src }, new Dictionary<string, string>()); }
            var ents = Entities(src);
            foreach (var (k, val) in res.Values) ents.TryAdd(k, val);
            foreach (var t in res.Texts) Collect(t, grid, ents);
            foreach (var val in res.Values.Values) if (val.Contains("<field", StringComparison.OrdinalIgnoreCase)) Collect(WebUtility.HtmlDecode(val), grid, ents);
        }

        void Collect(string src, bool grid, Dictionary<string, string> ents)
        {
            foreach (Match f in FieldRe.Matches(src))
            {
                var name = Resolve(f.Groups[1].Value, ents).Trim();
                if (name.Length == 0 || name.Contains('&')) continue;
                var it = ItemsRe.Match(f.Groups[3].Value);
                var a = it.Success ? Attrs(Resolve(it.Groups[1].Value, ents)) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var isGridHolder = a.TryGetValue("style", out var st) && st.Equals("Grid", StringComparison.OrdinalIgnoreCase);
                if (!isGridHolder && NotNull.IsMatch(Resolve(f.Groups[2].Value, ents))) (grid ? gridRequired : required).Add(name);
                if (!it.Success || !a.TryGetValue("controller", out var ctrl) || ctrl.Contains('&')) continue;
                if (isGridHolder) { grids.Add(ctrl.Trim()); continue; }
                if (fields.ContainsKey(name)) continue;
                if (SourceOf(sourceRoot, ctrl.Trim()) is not { } tc) continue;
                var refName = a.TryGetValue("reference", out var rf) ? Resolve(rf, ents).Trim() : "";
                if (refName.EndsWith("%l", StringComparison.OrdinalIgnoreCase)) refName = refName[..^2];
                fields[name] = new Lookup(name, ctrl.Trim(), tc.Table, tc.Code,
                    a.TryGetValue("key", out var k) ? Decode(k, ents) : "", a.TryGetValue("check", out var ch) ? Decode(ch, ents) : "", grid,
                    refName, tc.Filter, tc.Sys);
            }
        }
    }

    /// <summary>Tên các field mà điều kiện tham chiếu ({$%c[ma_kho]}, {[$ma_dvcs]}, {$%c$%r.[ma_vt]}) — chọn các ô đó trước.</summary>
    public static IEnumerable<string> Refs(string expr) =>
        Regex.Matches(expr ?? "", @"\{\s*(?:\$%c(?:\$%r\.)?\[|\[\$)(\w+)\]\s*\}").Select(m => m.Groups[1].Value);

    /// <summary>
    /// Điều kiện key/check của FBO → SQL chạy được trên bảng tra cứu: giá trị ô khác ({$%c[x]} …) lấy từ ô đã chọn / đang có trên form,
    /// @@id = mã chứng từ, @@unit = đơn vị đầu tiên, @@admin = 1 (bỏ lọc quyền), @@userID = -1, @@language = 'v'. Còn @@ lạ → null (không dùng được).
    /// </summary>
    public static string? ToSql(string expr, Func<string, string?> valueOf, string voucherId, string sysDatabase, string appDatabase = "", string unit = "")
    {
        if (string.IsNullOrWhiteSpace(expr)) return null;
        // giá trị ô khác; ngày dd/MM/yyyy (cách điền của ô ngày FBO) đổi sang yyyyMMdd — chèn thẳng dd/MM vào SQL bị đọc thành MM/dd (lỗi khi ngày > 12)
        var s = Regex.Replace(expr, @"\{\s*(?:\$%c(?:\$%r\.)?\[|\[\$)(\w+)\]\s*\}", m =>
        {
            var v = valueOf(m.Groups[1].Value) ?? "";
            if (Regex.Match(v, @"^(\d{2})/(\d{2})/(\d{4})$") is { Success: true } d) v = d.Groups[3].Value + d.Groups[2].Value + d.Groups[1].Value;
            return v.Replace("'", "''");
        });
        // @@unit: đơn vị đang làm việc (ma_dvcs của form / đơn vị hay dùng) — không phải đơn vị đầu tiên trong dmdvcs (KOG: '00' không có kho nào)
        s = Regex.Replace(s, @"@@unit\b", unit.Length > 0 ? "N'" + unit.Replace("'", "''") + "'" : "(select top 1 ma_dvcs from dmdvcs)", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"@@admin\b", "1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"@@userID\b", "-1", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"@@language\b", "'v'", RegexOptions.IgnoreCase);
        if (sysDatabase.Length > 0) s = Regex.Replace(s, @"@@sysDatabaseName\b", "[" + sysDatabase.Replace("]", "]]") + "]", RegexOptions.IgnoreCase);
        if (appDatabase.Length > 0) s = Regex.Replace(s, @"@@appDatabaseName\b", "[" + appDatabase.Replace("]", "]]") + "]", RegexOptions.IgnoreCase);
        if (voucherId.Length > 0) s = Regex.Replace(s, @"@@id\b", "'" + voucherId.Replace("'", "''") + "'", RegexOptions.IgnoreCase);
        // biến ngày của chứng từ trong key (ma_bgm: "ngay_bd <= @d1 and …") = hôm nay (ngày Điền mẫu điền vào ngay_ct)
        s = Regex.Replace(s, @"(?<![@\w])@d1?\b", "'" + DateTime.Today.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture) + "'", RegexOptions.IgnoreCase);
        if (s.Contains("@@") || s.Contains("{$") || s.Contains("{[")) return null;
        if (Regex.IsMatch(s, @"(?<![@\w'])@[A-Za-z_]\w*")) return null;   // biến @xxx khác (@u, @userName…) — không biết giá trị

        if (Regex.IsMatch(s, @"^\s*1\s*=\s*1\s*$")) return "";
        return s;
    }
}
