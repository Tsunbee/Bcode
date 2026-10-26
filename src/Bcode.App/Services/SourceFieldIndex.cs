using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Field mà source DỰ ÁN đang dùng (<c>App_Data\Controllers\Dir\*.xml|*.f</c>): tên field, tiêu đề v/e, type, tra cứu AutoComplete.
/// "Tạo nhanh danh mục" dùng để đặt tên cột đúng như dự án đã đặt: luật Fast tra tiêu đề → field (ưu tiên hơn header.xml chung),
/// AI được gửi kèm các field khớp cụm trong yêu cầu, bước kiểm tra trước Tạo file so cột với dự án.
/// Chỉ đọc phần &lt;fields&gt; đầu mỗi file; nhớ ra đĩa (<c>%AppData%\Bcode\cache\source-fields\*.json</c>) theo thời gian sửa + kích thước
/// nên lần sau (kể cả source trên ổ mạng) chỉ đọc lại file đã đổi. Danh mục vừa Tạo file vào chỉ mục ở lần làm mới kế tiếp (sau 5 phút).
/// </summary>
public sealed class SourceFieldIndex
{
    /// <summary>1 cách dùng field trong dự án: số file dùng đúng (tên, tiêu đề, controller tra cứu) này + vài file ví dụ.</summary>
    /// <param name="Table">Bảng (dir table="…") của file ví dụ đầu — để lấy kiểu SQL thật của field.</param>
    public sealed record Use(string Name, string V, string E, string Type, FieldLookup? Lookup, int Count, IReadOnlyList<string> Files, string Table);

    private const int MaxExamples = 3;
    private const int MaxHeadChars = 400_000;
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (DateTime At, Task<SourceFieldIndex> Task)> Building = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SourceFieldIndex> Ready = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SourceFieldIndex Empty = new("", new Dictionary<string, CacheFile>());

    private readonly Dictionary<string, List<Use>> _byHeader = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Use>> _byName = new(StringComparer.OrdinalIgnoreCase);

    public string SourcePath { get; }
    public int FileCount { get; }
    /// <summary>Mọi tiêu đề Việt (đã bỏ dấu) có trong chỉ mục — để tìm cụm của yêu cầu trùng tiêu đề field dự án.</summary>
    public IEnumerable<string> Headers => _byHeader.Keys;

    /// <summary>Chỉ mục của source (đọc / làm mới ở nền). Gọi lại trong vòng 5 phút trả bản đang có; source trống → chỉ mục rỗng.</summary>
    public static Task<SourceFieldIndex> GetAsync(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return Task.FromResult(Empty);
        var key = sourcePath.TrimEnd('\\', '/');
        if (Building.TryGetValue(key, out var b) && (!b.Task.IsCompleted || DateTime.Now - b.At < RefreshAfter)) return b.Task;
        var task = Task.Run(() =>
        {
            var idx = Build(key);
            Ready[key] = idx;
            return idx;
        });
        Building[key] = (DateTime.Now, task);
        return task;
    }

    /// <summary>Bản đã đọc xong gần nhất (không chờ) — null nếu chưa đọc lần nào trong phiên.</summary>
    public static SourceFieldIndex? TryGet(string? sourcePath) =>
        string.IsNullOrWhiteSpace(sourcePath) ? null : Ready.TryGetValue(sourcePath.TrimEnd('\\', '/'), out var i) ? i : null;

    /// <summary>Các field có tiêu đề Việt (so bỏ dấu) đúng bằng <paramref name="header"/>, dùng nhiều nhất trước.</summary>
    public IReadOnlyList<Use> ByHeader(string header) =>
        _byHeader.TryGetValue(FastCatalogRules.Fold(header), out var l) ? l : Array.Empty<Use>();

    /// <summary>Các cách dự án dùng field tên này (tiêu đề / tra cứu khác nhau), dùng nhiều nhất trước.</summary>
    public IReadOnlyList<Use> ByName(string name) => _byName.TryGetValue(name, out var l) ? l : Array.Empty<Use>();

    private SourceFieldIndex(string sourcePath, IReadOnlyDictionary<string, CacheFile> files)
    {
        SourcePath = sourcePath;
        FileCount = files.Count;
        var agg = new Dictionary<(string N, string V, string C), (CacheField F, int Count, List<string> Files, string Table)>();
        foreach (var (file, cf) in files)
            foreach (var f in cf.F.DistinctBy(x => (x.N.ToLowerInvariant(), x.V, x.C.ToLowerInvariant())))
            {
                var k = (f.N.ToLowerInvariant(), FastCatalogRules.Fold(f.V), f.C.ToLowerInvariant());
                if (!agg.TryGetValue(k, out var a)) a = (f, 0, new List<string>(), cf.Tb);
                if (a.Files.Count < MaxExamples) a.Files.Add(Path.GetFileNameWithoutExtension(file));
                agg[k] = (a.F, a.Count + 1, a.Files, a.Table.Length > 0 ? a.Table : cf.Tb);
            }
        foreach (var ((_, v, _), (f, count, ex, table)) in agg)
        {
            var use = new Use(f.N, f.V, f.E, f.Ty, f.C.Length == 0 ? null : new FieldLookup(f.C, f.R, f.K, f.Ch, f.I), count, ex, table);
            if (!_byHeader.TryGetValue(v, out var hl)) _byHeader[v] = hl = new();
            hl.Add(use);
            if (!_byName.TryGetValue(f.N, out var nl)) _byName[f.N] = nl = new();
            nl.Add(use);
        }
        foreach (var l in _byHeader.Values.Concat(_byName.Values))
            l.Sort((a, b) => b.Count != a.Count ? b.Count.CompareTo(a.Count) : (a.Lookup is null).CompareTo(b.Lookup is null));
    }

    // ---------------------------------------------------------------------------------------- đọc source

    private static readonly Regex FieldRx = new(@"<field\b(?<a>[^>]*?)(?:/>|>(?<b>.*?)</field>)", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AttrRx = new(@"\b(?<k>[A-Za-z_]+)\s*=\s*""(?<v>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex HeaderRx = new(@"<header\b(?<a>[^>]*)>", RegexOptions.Compiled);
    private static readonly Regex ItemsRx = new(@"<items\b(?<a>[^>]*)>", RegexOptions.Compiled);
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { "datetime0", "datetime2", "user_id0", "user_id2", "cookie", "stt_rec", "status" };

    private static SourceFieldIndex Build(string sourcePath)
    {
        var dir = Path.Combine(sourcePath, "App_Data", "Controllers", "Dir");
        var cachePath = CachePath(sourcePath);
        var old = LoadCache(cachePath);
        var files = new ConcurrentDictionary<string, CacheFile>(StringComparer.OrdinalIgnoreCase);
        var changed = 0;
        try
        {
            if (Directory.Exists(dir))
            {
                var infos = new DirectoryInfo(dir).EnumerateFiles()
                    .Where(f => f.Extension.Equals(".xml", StringComparison.OrdinalIgnoreCase) || f.Extension.Equals(".f", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                // Ổ mạng: đọc song song cho đỡ chờ độ trễ từng file.
                Parallel.ForEach(infos, new ParallelOptions { MaxDegreeOfParallelism = 8 }, fi =>
                {
                    var t = fi.LastWriteTimeUtc.Ticks;
                    if (old.TryGetValue(fi.Name, out var c) && c.T == t && c.L == fi.Length) { files[fi.Name] = c; return; }
                    var (fields, table) = Parse(fi.FullName);
                    files[fi.Name] = new CacheFile { T = t, L = fi.Length, F = fields, Tb = table };
                    Interlocked.Increment(ref changed);
                });
            }
        }
        catch { /* source không đọc được: dùng phần đã có */ }
        if (changed > 0 || files.Count != old.Count) SaveCache(cachePath, sourcePath, files);
        return new SourceFieldIndex(sourcePath, files);
    }

    /// <summary>Các field (có tiêu đề Việt) trong phần &lt;fields&gt; của 1 file Dir — bỏ field ẩn danh (&amp;k;), field tên tra cứu (%l), cột hệ thống.</summary>
    private static (List<CacheField> Fields, string Table) Parse(string path)
    {
        var list = new List<CacheField>();
        string text;
        try
        {
            using var sr = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var sb = new StringBuilder();
            string? line;
            while ((line = sr.ReadLine()) is not null && sb.Length < MaxHeadChars)
            {
                sb.AppendLine(line);
                if (line.Contains("</fields>", StringComparison.Ordinal)) break;
            }
            text = sb.ToString();
        }
        catch { return (list, ""); }

        foreach (Match m in FieldRx.Matches(text))
        {
            var a = Attrs(m.Groups["a"].Value);
            var name = a.GetValueOrDefault("name", "").Trim();
            if (name.Length == 0 || name.IndexOfAny(new[] { '&', '%', '$', '#', ' ' }) >= 0 || Skip.Contains(name)) continue;
            var body = m.Groups["b"].Value;
            var h = HeaderRx.Match(body) is { Success: true } hm ? Attrs(hm.Groups["a"].Value) : new();
            var v = Decode(h.GetValueOrDefault("v", ""));
            if (v.Length == 0) continue;
            var f = new CacheField { N = name, V = v, E = Decode(h.GetValueOrDefault("e", "")), Ty = a.GetValueOrDefault("type", "") };
            if (ItemsRx.Match(body) is { Success: true } im && Attrs(im.Groups["a"].Value) is var it
                && it.GetValueOrDefault("style", "").Equals("AutoComplete", StringComparison.OrdinalIgnoreCase) && it.GetValueOrDefault("controller", "").Length > 0)
            {
                f.C = it["controller"];
                f.R = Decode(it.GetValueOrDefault("reference", ""));
                f.K = Decode(it.GetValueOrDefault("key", ""));
                f.Ch = Decode(it.GetValueOrDefault("check", ""));
                f.I = Decode(it.GetValueOrDefault("information", ""));
            }
            list.Add(f);
        }
        var tb = Regex.Match(text, @"<(dir|grid)\s[^>]*\btable=""(?<t>[A-Za-z0-9_$]+)""") is { Success: true } tm ? tm.Groups["t"].Value : "";
        return (list, tb);
    }

    private static Dictionary<string, string> Attrs(string s)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRx.Matches(s)) d.TryAdd(m.Groups["k"].Value, m.Groups["v"].Value);
        return d;
    }

    private static string Decode(string s) => WebUtility.HtmlDecode(s).Trim();

    // ---------------------------------------------------------------------------------------- cache trên đĩa

    private sealed class CacheFile
    {
        public long T { get; set; }
        public long L { get; set; }
        public string Tb { get; set; } = "";
        public List<CacheField> F { get; set; } = new();
    }

    private sealed class CacheField
    {
        public string N { get; set; } = "";
        public string V { get; set; } = "";
        public string E { get; set; } = "";
        public string Ty { get; set; } = "";
        public string C { get; set; } = "";
        public string R { get; set; } = "";
        public string K { get; set; } = "";
        public string Ch { get; set; } = "";
        public string I { get; set; } = "";
    }

    private sealed class CacheDoc
    {
        public string Source { get; set; } = "";
        public Dictionary<string, CacheFile> Files { get; set; } = new();
    }

    private static string CachePath(string sourcePath)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(sourcePath.ToLowerInvariant())))[..16];
        return Path.Combine(BcodePaths.AppData, "Bcode", "cache", "source-fields", hash + ".json");
    }

    private static Dictionary<string, CacheFile> LoadCache(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<CacheDoc>(File.ReadAllText(path)) is { } doc)
                return new(doc.Files, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* cache hỏng: đọc lại từ đầu */ }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveCache(string path, string source, IDictionary<string, CacheFile> files)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new CacheDoc { Source = source, Files = new(files, StringComparer.OrdinalIgnoreCase) }));
        }
        catch { /* chỉ là cache */ }
    }
}
