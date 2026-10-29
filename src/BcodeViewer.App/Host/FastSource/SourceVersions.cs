using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BcodeViewer.App.Host.FastSource;

/// <summary>
/// Kiến thức chỉnh sửa theo phiên bản source Fast: thư mục gốc chứa các gói theo bản (FBOR1, FBOR2SP01 … FBOR2SP22.6.2.1 … FBOR2SP24.2.2; mỗi gói
/// chỉ chứa file đổi, đặt dưới <c>…\App_Data\Controllers\…</c> và <c>…\Main\…</c>). Quét các bản theo thứ tự, tóm tắt mỗi file controller thành các
/// phần (thẻ gốc, field, entity, hàm JS, command / action / query, nút toolbar) rồi so với lần file đó xuất hiện ở bản trước → danh sách thay đổi
/// (thêm / bỏ / sửa, kèm thuộc tính đổi). Bản mới nhất của mỗi file là nguồn học thêm cho ghost text và Hỏi Fast.
/// Kết quả ghi <c>viewer\versions\&lt;thư mục&gt;.json</c> trong thư mục kiến thức (source Fast — không đưa vào repo).
/// </summary>
public sealed class SourceVersions
{
    public sealed record Version(string Name, string Product, int Files);
    /// <summary>1 thay đổi: bản, file (Grid/AIDetail.xml), loại phần (file / root / field / entity / func / command / action / query / button),
    /// tên phần, cách đổi (+ thêm / - bỏ / ~ sửa), chi tiết.</summary>
    public sealed record Change(string V, string F, string K, string N, string Op, string D);

    public string Root { get; set; } = "";
    public DateTime ScannedAt { get; set; }
    public string Signature { get; set; } = "";
    public List<Version> Versions { get; set; } = new();
    public List<Change> Changes { get; set; } = new();
    /// <summary>File → (bản mới nhất, đường dẫn đầy đủ).</summary>
    public Dictionary<string, string[]> Latest { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- thứ tự bản

    /// <summary>(dòng sản phẩm, khoá sắp xếp): FBOR1 và FBOR2SPxx cùng dòng "FBO"; CRM10, DMS10… mỗi cái 1 dòng; thư mục ghi chú (_…, ZZZ…) bỏ.</summary>
    public static (string Product, string Key)? ParseVersion(string name)
    {
        if (name.StartsWith('_') || name.StartsWith("ZZZ", StringComparison.OrdinalIgnoreCase)) return null;
        if (Regex.Match(name, @"^FBOR(?<r>\d+)(?:SP(?<n>\d+(?:\.\d+)*))?(?<x>[A-Za-z]*)$", RegexOptions.IgnoreCase) is { Success: true } m)
        {
            var nums = new List<int> { int.Parse(m.Groups["r"].Value) };
            if (m.Groups["n"].Success) nums.AddRange(m.Groups["n"].Value.Split('.').Select(int.Parse));
            return ("FBO", string.Join(".", nums.Select(n => n.ToString("D4"))) + (m.Groups["x"].Value.Length > 0 ? "~" + m.Groups["x"].Value : ""));
        }
        return (name, "0");
    }

    // ---------------------------------------------------------------- tóm tắt 1 file

    /// <summary>Các phần của 1 file controller: khoá "loại:tên" → văn bản chuẩn hoá (cắt ngắn) — so 2 bản theo khoá.</summary>
    public static Dictionary<string, string> Summarize(string text)
    {
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, string val)
        {
            val = Regex.Replace(val, @"\s+", " ").Trim();
            if (!res.TryAdd(key, val)) res[key] += " ‖ " + val;
        }
        foreach (Match m in Regex.Matches(text, @"<!ENTITY\s+(%\s+)?(?<n>[\w.$-]+)\s+(?<v>SYSTEM\s+""[^""]*""|""[^""]*""|'[^']*')\s*>"))
            Put("entity:" + m.Groups["n"].Value, m.Groups["v"].Value);
        var root = Regex.Match(text, @"<(dir|grid|filter|report|lookup|list|query|import|clipboard)\b[^>]*>");
        if (root.Success) Put("root:" + root.Groups[1].Value, root.Value);
        // import / clipboard: <setting><type value="Voucher"/><baseTable value="…"/>… và thông báo lỗi <var id="C001">
        if (Regex.Match(text, @"<setting>([\s\S]*?)</setting>") is { Success: true } st)
            foreach (Match m in Regex.Matches(st.Groups[1].Value, @"<(?<n>\w+)\b[^>]*?\bvalue=""[^""]*""[^>]*>")) Put("setting:" + m.Groups["n"].Value, m.Value);
        foreach (Match m in Regex.Matches(text, @"<var\s+id=""(?<n>[^""]+)""[\s\S]*?</var>")) Put("var:" + m.Groups["n"].Value, m.Value);
        // file include chỉ chứa danh sách <field …/> (Upload\Include\SVTranFieldsCompact.txt) không có thẻ <fields>
        var fieldsPart = Regex.Match(text, @"<fields\b[^>]*>([\s\S]*?)</fields>") is { Success: true } fp ? fp.Groups[1].Value
            : root.Success ? "" : text;
        foreach (Match m in Regex.Matches(fieldsPart, @"<field\s+name=""(?<n>[^""]+)""[^>]*?(/>|>[\s\S]*?</field>)"))
            Put("field:" + m.Groups["n"].Value, m.Value);
        foreach (Match m in Regex.Matches(text, @"<command\s+event=""(?<n>[^""]+)""[\s\S]*?</command>")) Put("command:" + m.Groups["n"].Value, m.Value);
        foreach (Match m in Regex.Matches(text, @"<action\s+id=""(?<n>[^""]+)""[\s\S]*?</action>")) Put("action:" + m.Groups["n"].Value, m.Value);
        foreach (Match m in Regex.Matches(text, @"<query\s+event=""(?<n>[^""]+)""[\s\S]*?</query>")) Put("query:" + m.Groups["n"].Value, m.Value);
        if (Regex.Match(text, @"<toolbar\b[^>]*>([\s\S]*?)</toolbar>") is { Success: true } tb)
            foreach (Match m in Regex.Matches(tb.Groups[1].Value, @"<button\b[^>]*?\b(?:command|id)=""(?<n>[^""]+)""[^>]*>"))
                Put("button:" + m.Groups["n"].Value, m.Value);
        foreach (var script in SourceGhost.ScriptParts(text))
            foreach (Match m in Regex.Matches(script, @"function\s+(?<n>[\w$]+)\s*\("))
            {
                // thân hàm = tới function kế tiếp (đủ để biết hàm có đổi không)
                var next = Regex.Match(script[(m.Index + m.Length)..], @"\bfunction\s+[\w$]+\s*\(");
                var body = next.Success ? script.Substring(m.Index, m.Length + next.Index) : script[m.Index..];
                Put("func:" + m.Groups["n"].Value, body);
            }
        return res;
    }

    private static string Hash(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s)))[..12];

    private static Dictionary<string, string> Attrs(string tag) =>
        Regex.Matches(Regex.Match(tag, @"^<[^>]*>").Value, @"(?<n>[\w:]+)\s*=\s*""(?<v>[^""]*)""").GroupBy(m => m.Groups["n"].Value)
            .ToDictionary(g => g.Key, g => g.First().Groups["v"].Value);

    /// <summary>Mô tả ngắn chỗ khác giữa 2 bản của 1 phần: thuộc tính thẻ mở đổi, dòng con thêm / bỏ.</summary>
    public static string Describe(string kind, string a, string b)
    {
        var parts = new List<string>();
        if (kind is "field" or "root" or "button" or "setting" or "var")
        {
            var x = Attrs(a); var y = Attrs(b);
            foreach (var (k, v) in y) if (!x.TryGetValue(k, out var o)) parts.Add($"+{k}=\"{v}\""); else if (o != v) parts.Add($"{k}: \"{o}\" → \"{v}\"");
            foreach (var k in x.Keys) if (!y.ContainsKey(k)) parts.Add($"-{k}");
            var ca = Regex.Matches(a, @"<(?!/)(?!field\b)[^>]+>").Select(m => m.Value).ToList();
            var cb = Regex.Matches(b, @"<(?!/)(?!field\b)[^>]+>").Select(m => m.Value).ToList();
            foreach (var t in cb.Except(ca)) parts.Add("+ " + t);
            foreach (var t in ca.Except(cb)) parts.Add("- " + t);
        }
        else if (kind == "entity") parts.Add($"{Cut(a, 120)} → {Cut(b, 120)}");
        else parts.Add("sửa nội dung");
        var d = string.Join("; ", parts);
        return Cut(d.Length == 0 ? "đổi khoảng trắng / thứ tự" : d, 400);
    }

    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ---------------------------------------------------------------- quét

    /// <summary>File thuộc phạm vi học: controller (xml, ent, txt, file include không đuôi) dưới <c>App_Data\Controllers</c>, trang aspx / ascx và js.
    /// Trả khoá tương đối ("Grid/AIDetail.xml", "Main/Login.aspx") hoặc null.</summary>
    public static string? RelKey(string path)
    {
        var p = path.Replace('\\', '/');
        var i = p.IndexOf("/App_Data/Controllers/", StringComparison.OrdinalIgnoreCase);
        var ext = Path.GetExtension(p).ToLowerInvariant();
        if (i >= 0)
        {
            if (ext is not (".xml" or ".ent" or ".txt" or "") && !ext.StartsWith(".ent")) return null;
            // Templates: chỉ học khai báo import (Upload — nhập danh mục / chứng từ từ Excel) và Clipboard (dán từ Excel); bỏ mẫu Excel / Rpt (nhị phân)
            if (p.Contains("/Templates/", StringComparison.OrdinalIgnoreCase)
                && !p.Contains("/Templates/Upload/", StringComparison.OrdinalIgnoreCase) && !p.Contains("/Templates/Clipboard/", StringComparison.OrdinalIgnoreCase)) return null;
            return p[(i + "/App_Data/Controllers/".Length)..];
        }
        var mi = p.IndexOf("/Main/", StringComparison.OrdinalIgnoreCase);
        if (mi >= 0 && ext is ".aspx" or ".ascx" or ".master" or ".js") return p[(mi + 1)..];
        return null;
    }

    /// <summary>Danh sách thư mục bản (đã sắp theo dòng sản phẩm, rồi theo số bản).</summary>
    public static List<(string Name, string Product, string Key, string Dir)> VersionDirs(string root) =>
        Directory.EnumerateDirectories(root)
            .Select(d => (Dir: d, Name: Path.GetFileName(d), P: ParseVersion(Path.GetFileName(d))))
            .Where(x => x.P is not null)
            .Select(x => (x.Name, x.P!.Value.Product, x.P!.Value.Key, x.Dir))
            .OrderBy(x => x.Product == "FBO" ? 0 : 1).ThenBy(x => x.Product, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key, StringComparer.Ordinal)
            .ToList();

    /// <summary>Chữ ký nhanh của thư mục gốc (tên + giờ sửa từng thư mục bản) — đổi thì quét lại.</summary>
    public static string SignatureOf(string root) =>
        Hash(string.Join("|", VersionDirs(root).Select(v => v.Name + ":" + Directory.GetLastWriteTimeUtc(v.Dir).Ticks)));

    /// <summary>Quét toàn bộ. <paramref name="progress"/> nhận (bản đang đọc, số bản đã xong, tổng số bản).</summary>
    public static SourceVersions Scan(string root, Action<string, int, int>? progress = null, CancellationToken ct = default)
    {
        var res = new SourceVersions { Root = root, ScannedAt = DateTime.Now, Signature = SignatureOf(root) };
        var dirs = VersionDirs(root);
        var prev = new Dictionary<string, (string Product, Dictionary<string, string> Sum, string Hash)>(StringComparer.OrdinalIgnoreCase);
        // gói lớn chép lại nguyên nhiều file không đổi: cùng kích thước + giờ sửa với lần gặp trước thì khỏi đọc (đọc đĩa là phần chậm nhất)
        var stamp = new Dictionary<string, (long Len, DateTime At)>(StringComparer.OrdinalIgnoreCase);
        for (var vi = 0; vi < dirs.Count; vi++)
        {
            ct.ThrowIfCancellationRequested();
            var (name, product, _, dir) = dirs[vi];
            progress?.Invoke(name, vi, dirs.Count);
            // 1 bản có thể có nhiều gói con trùng file: lấy file sửa sau cùng
            var files = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            List<FileInfo> all;
            // FileInfo từ lượt liệt kê thư mục đã có kích thước + giờ sửa (không stat lại từng file)
            try { all = new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); } catch { continue; }
            foreach (var fi in all)
            {
                if (RelKey(fi.FullName) is not { } key) continue;
                if (fi.Length > 3_000_000) continue;
                if (!files.TryGetValue(key, out var old) || fi.LastWriteTimeUtc > old.LastWriteTimeUtc) files[key] = fi;
            }
            if (files.Count == 0) continue;
            res.Versions.Add(new Version(name, product, files.Count));
            string Fk(string key) => product == "FBO" ? key : product + ":" + key;
            var todo = files.Where(x => !(stamp.TryGetValue(Fk(x.Key), out var st) && st.Len == x.Value.Length && st.At == x.Value.LastWriteTimeUtc))
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).ToList();
            // đọc + tóm tắt song song, so sánh tuần tự theo thứ tự file
            var read = todo.AsParallel().AsOrdered().WithDegreeOfParallelism(8).WithCancellation(ct).Select(x =>
            {
                string? text = null;
                try { text = File.ReadAllText(x.Value.FullName).Replace("\r\n", "\n"); } catch { }
                if (text is null) return (x.Key, x.Value, (string?)null, (Dictionary<string, string>?)null);
                var isCtl = !x.Key.StartsWith("Main/", StringComparison.OrdinalIgnoreCase);
                var s = (isCtl ? Summarize(text) : new Dictionary<string, string>()).ToDictionary(y => y.Key, y => Compact(y.Value), StringComparer.Ordinal);
                return (x.Key, x.Value, Hash(text), s);
            }).ToList();
            foreach (var (key, fi, hh, ss) in read)
            {
                if (hh is null || ss is null) continue;
                var h = hh; var sum = ss;
                var isCtl = !key.StartsWith("Main/", StringComparison.OrdinalIgnoreCase);
                var fk = Fk(key);
                stamp[fk] = (fi.Length, fi.LastWriteTimeUtc);
                if (!prev.TryGetValue(fk, out var p))
                    res.Changes.Add(new Change(name, fk, "file", Path.GetFileName(key), "+", $"lần đầu có trong gói {name} ({sum.Keys.Count(k => k.StartsWith("field:"))} field)"));
                else if (p.Hash != h)
                {
                    var n0 = res.Changes.Count;
                    foreach (var (k, v) in sum)
                    {
                        var (kind, nm) = Split(k);
                        if (!p.Sum.TryGetValue(k, out var o)) res.Changes.Add(new Change(name, fk, kind, nm, "+", Cut(v, 300)));
                        else if (o != v) res.Changes.Add(new Change(name, fk, kind, nm, "~", Describe(kind, o, v)));
                    }
                    foreach (var k in p.Sum.Keys.Where(k => !sum.ContainsKey(k)))
                    {
                        var (kind, nm) = Split(k);
                        res.Changes.Add(new Change(name, fk, kind, nm, "-", ""));
                    }
                    if (res.Changes.Count == n0) res.Changes.Add(new Change(name, fk, "file", Path.GetFileName(key), "~", isCtl ? "sửa phần ngoài field / entity / script (vd views, title, SQL)" : "sửa nội dung"));
                }
                prev[fk] = (product, sum, h);
                res.Latest[fk] = new[] { name, fi.FullName };
            }
        }
        progress?.Invoke("", dirs.Count, dirs.Count);
        return res;
    }

    /// <summary>Phần dài (khối SQL / hàm JS) giữ 300 ký tự đầu + băm của cả phần — đủ để so và để mô tả thẻ mở.</summary>
    private static string Compact(string v) => v.Length > 600 ? v[..300] + " #" + Hash(v) : v;

    private static (string Kind, string Name) Split(string key) { var i = key.IndexOf(':'); return (key[..i], key[(i + 1)..]); }

    // ---------------------------------------------------------------- ghi / đọc

    public static string FileFor(string root) => ViewerKnowledge.Viewer("versions", ViewerKnowledge.SafeName(root.TrimEnd('\\', '/')) + ".json");

    public void Save() => ViewerKnowledge.WriteAtomic(FileFor(Root), JsonSerializer.Serialize(this, ViewerKnowledge.Json));

    public static SourceVersions? Load(string root)
    {
        try
        {
            var f = FileFor(root);
            return File.Exists(f) ? JsonSerializer.Deserialize<SourceVersions>(File.ReadAllText(f)) : null;
        }
        catch { return null; }
    }

    /// <summary>Đường dẫn bản mới nhất của mọi file (để học ghost / Hỏi Fast).</summary>
    public IEnumerable<(string Key, string Path)> LatestFiles() => Latest.Select(x => (x.Key, x.Value[1]));
}
