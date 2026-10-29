using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Kiến thức đọc từ source Fast (controller XML): Filter = điều kiện lọc của báo cáo (field + tiêu đề Việt / Anh), Grid = màn hình chứng từ / danh mục
/// (bảng lưu, mã chứng từ, field), Report = các mẫu in. Nguồn: thư mục <c>App_Data\Controllers</c> của dự án (Source Path) nếu với tới được,
/// không thì mẫu Fast đi kèm Bcode (<c>Templates\fileSource\Source</c>). Đọc bằng regex (file có DTD / entity ngoài nên không nạp XML được).
/// Thêm: bảng "tham số store → cột" rút từ mọi Filter: tiêu đề tiếng Anh của ô lọc là tên tham số (ma_kho e="Site" ↔ @Site).
/// </summary>
public sealed class FastSourceKnowledge
{
    public sealed record Field(string Name, string V, string E, string Alias);
    public sealed record Form(string Id, string V);
    public sealed record Controller(string Name, string Kind, string Table, string VoucherId, string Type, List<Field> Fields, List<Form> Forms, string Path);

    private readonly Dictionary<string, List<Controller>> _byName = new(StringComparer.OrdinalIgnoreCase);
    public IEnumerable<Controller> All => _byName.Values.SelectMany(v => v);
    public string Root { get; private init; } = "";

    public IReadOnlyList<Controller> Find(string name) => _byName.TryGetValue(name, out var l) ? l : Array.Empty<Controller>();

    private static readonly Regex FieldRx = new(@"<field\s+name=""(?<n>[^""]+)""(?<a>[^>]*)>(?<body>[\s\S]*?)(?=<field\s|</fields>|$)", RegexOptions.Compiled);
    private static readonly Regex HeaderRx = new(@"<header\s+v=""(?<v>[^""]*)""\s+e\s*=\s*""(?<e>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex FormRx = new(@"<form\s+id=""(?<id>[^""]+)""[^>]*>\s*<header\s+v=""(?<v>[^""]*)""", RegexOptions.Compiled);
    private static string Attr(string tag, string name) => Regex.Match(tag, @"\b" + name + @"=""(?<x>[^""]*)""") is { Success: true } m ? m.Groups["x"].Value : "";

    /// <summary>Đọc 1 file controller; null nếu không đọc được.</summary>
    public static Controller? Parse(string path, string kind)
    {
        string xml;
        try { xml = File.ReadAllText(path); } catch { return null; }
        var root = Regex.Match(xml, @"<(grid|filter|report|dir)\b[^>]*>") is { Success: true } rm ? rm.Value : "";
        var fields = new List<Field>();
        var fieldsPart = kind == "Report" ? Regex.Replace(xml, @"<forms>[\s\S]*?</forms>", "") : xml;
        foreach (Match m in FieldRx.Matches(fieldsPart))
        {
            var a = m.Groups["a"].Value;
            if (a.Contains("external=\"true\"")) continue;   // ten_xx%l chỉ để hiện tên
            var h = HeaderRx.Match(m.Groups["body"].Value);
            fields.Add(new Field(m.Groups["n"].Value, h.Success ? h.Groups["v"].Value : "", h.Success ? h.Groups["e"].Value : "", Attr(a, "aliasName")));
        }
        var forms = FormRx.Matches(xml).Select(m => new Form(m.Groups["id"].Value, m.Groups["v"].Value)).DistinctBy(f => f.Id).ToList();
        return new Controller(Path.GetFileNameWithoutExtension(path), kind, Attr(root, "table"), Attr(root, "id"), Attr(root, "type"), fields, forms, path);
    }

    /// <summary>Đọc cả thư mục Controllers (Filter / Grid / Report; bỏ bản sao lưu *_bk*).</summary>
    public static FastSourceKnowledge Load(string controllersRoot)
    {
        var k = new FastSourceKnowledge { Root = controllersRoot };
        foreach (var kind in new[] { "Filter", "Grid", "Report" })
        {
            var dir = Path.Combine(controllersRoot, kind);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.xml"))
            {
                if (Regex.IsMatch(Path.GetFileName(f), @"_bk\d*|\.bak|copy", RegexOptions.IgnoreCase)) continue;
                if (Parse(f, kind) is not { } c) continue;
                if (!k._byName.TryGetValue(c.Name, out var l)) k._byName[c.Name] = l = new();
                l.Add(c);
            }
        }
        return k;
    }

    // ---------------------------------------------------------------- nguồn: dự án (Source Path) → mẫu đi kèm

    private static readonly ConcurrentDictionary<string, Lazy<FastSourceKnowledge>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mẫu Fast đi kèm Bcode.</summary>
    public static FastSourceKnowledge Shipped => Cache.GetOrAdd("<shipped>", _ => new Lazy<FastSourceKnowledge>(() =>
        Load(Path.Combine(AppContext.BaseDirectory, "Templates", "fileSource", "Source")))).Value;

    /// <summary>Source của dự án (<c>&lt;Source Path&gt;\App_Data\Controllers</c>) nếu đã đọc xong, không thì mẫu đi kèm. Đọc source dự án qua ổ mạng
    /// mất hàng chục giây (đo trên KOG: ~1.400 file, 31 s) → đọc ở nền lần đầu, xong thì gọi <paramref name="loaded"/> (để dựng lại kho tìm kiếm).</summary>
    public static FastSourceKnowledge For(string? sourcePath, Action? loaded = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return Shipped;
        var ctl = Path.Combine(sourcePath.Trim(), "App_Data", "Controllers");
        var task = Background.GetOrAdd(ctl, p => Task.Run(() =>
        {
            try { if (Directory.Exists(Path.Combine(p, "Filter"))) return Load(p); } catch { /* ổ mạng không với tới */ }
            return Shipped;
        }));
        if (task.IsCompletedSuccessfully) return task.Result;
        if (loaded is not null) _ = task.ContinueWith(_ => loaded(), TaskScheduler.Default);
        return Shipped;
    }

    private static readonly ConcurrentDictionary<string, Task<FastSourceKnowledge>> Background = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- tham số store → cột (từ tiêu đề tiếng Anh / aliasName của ô lọc)

    private Dictionary<string, Dictionary<string, int>>? _paramMap;

    /// <summary>Tên tham số (không @, viết liền, chữ thường: "site", "itemgroup", "customer") → cột ô lọc → số lần gặp trong mọi Filter.</summary>
    public Dictionary<string, Dictionary<string, int>> ParamMap
    {
        get
        {
            if (_paramMap is { } pm) return pm;
            var map = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            void Add(string key, string col)
            {
                key = Regex.Replace(key, @"[^A-Za-z0-9]", "").ToLowerInvariant();
                if (key.Length < 2 || col.Contains('%') || !Regex.IsMatch(col, @"^[a-z][a-z0-9_]*$", RegexOptions.IgnoreCase)) return;
                if (!map.TryGetValue(key, out var d)) map[key] = d = new(StringComparer.OrdinalIgnoreCase);
                d[col] = d.GetValueOrDefault(col) + 1;
            }
            foreach (var c in All.Where(c => c.Kind == "Filter"))
                foreach (var f in c.Fields)
                {
                    if (f.E.Length > 0) Add(f.E, f.Name);
                    if (f.Alias.Length > 0) Add(f.Alias, f.Name);
                }
            return _paramMap = map;
        }
    }

    /// <summary>Cột mà tham số @<paramref name="param"/> lọc theo Filter của Fast (cột gặp nhiều nhất, chiếm ≥ 50%); null = không biết.</summary>
    public string? ColumnForParam(string param)
    {
        var key = Regex.Replace(param, @"[^A-Za-z0-9]", "").ToLowerInvariant();
        if (!ParamMap.TryGetValue(key, out var d) || d.Count == 0) return null;
        var top = d.OrderByDescending(x => x.Value).First();
        return top.Value * 2 >= d.Values.Sum() ? top.Key : null;
    }
}
