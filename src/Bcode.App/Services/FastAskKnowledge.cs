using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Kiến thức "Hỏi Fast" học từ store của từng dự án (nút "Học dự án này" — chỉ đọc sys.sql_modules), lưu riêng từng dự án rồi gộp thành kiến thức
/// chung trên máy để dự án sau dùng lại (<c>%AppData%\Bcode\fastask\</c>):
/// <list type="bullet">
/// <item>Cặp JOIN giữa 2 bảng và điều kiện ON hay dùng (giải alias theo FROM / JOIN trong cùng store; bảng phân kỳ quy về xxx$000000).</item>
/// <item>Cột của #report theo bảng phân kỳ (SELECT TOP 0 … INTO #report FROM xxx$000000): cột nào hay có, đứng thứ mấy.</item>
/// <item>Cột ngày truyền cho FastBusiness$Partition$Execute theo bảng phân kỳ.</item>
/// <item>Tham số hay có của store báo cáo rs_ (tên + kiểu).</item>
/// </list>
/// </summary>
public sealed class FastAskKnowledge
{
    /// <summary>"bảngA|bảngB" (xếp theo tên) → "bảngA.cột = bảngB.cột" → số store.</summary>
    public Dictionary<string, Dictionary<string, int>> Joins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bảng phân kỳ (vd r70) → cột #report → [số store, tổng vị trí].</summary>
    public Dictionary<string, Dictionary<string, int[]>> ReportCols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Bảng phân kỳ → cột ngày truyền Partition$Execute → số lần.</summary>
    public Dictionary<string, Dictionary<string, int>> DateCols { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Tham số store báo cáo rs_ (không @) → kiểu → số store.</summary>
    public Dictionary<string, Dictionary<string, int>> Params { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Dự án đã gộp vào (tên + ngày học + số store).</summary>
    public List<string> Projects { get; set; } = new();

    public static string Dir => Path.Combine(BcodePaths.AppData, "Bcode", "fastask");
    public static string SharedPath => Path.Combine(Dir, "shared.json");
    public static string ProjectPath(string key) =>
        Path.Combine(Dir, "projects", string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '|' ? '_' : c)) + ".json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    // ---------------------------------------------------------------- rút từ store

    private static readonly HashSet<string> NotAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "where", "with", "left", "right", "inner", "outer", "join", "cross", "full", "group", "order", "set", "union", "select", "into", "as", "and", "or", "having", "from",
    };
    private static readonly Regex FromJoinRx = new(@"\b(?:FROM|JOIN)\s+(?:\[?dbo\]?\.)?\[?(?<t>[\w$%#]+)\]?\s+(?:AS\s+)?(?<a>[A-Za-z_]\w*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JoinOnRx = new(@"\bJOIN\s+(?:\[?dbo\]?\.)?\[?(?<t>[\w$%#]+)\]?\s+(?:AS\s+)?(?<a>[A-Za-z_]\w*)(?:\s+WITH\s*\(\s*NOLOCK\s*\))?\s+ON\s+(?<x>\w+)\.(?<xc>\w+)\s*=\s*(?<y>\w+)\.(?<yc>\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StructRx = new(@"SELECT\s+TOP\s+0\s+(?<cols>.{1,4000}?)\s+INTO\s+#report\s+FROM\s+(?:\[?dbo\]?\.)?\[?(?<t>[A-Za-z]\w*)\$000000", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ExecPartRx = new(@"Partition\$Execute\s+@\w+\s*,\s*[^,]+,\s*'(?<c>[\w.]+)'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PartTableRx = new(@"\b(?<t>[A-Za-z]\w{1,15})\$%Partition", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex HeaderRx = new(@"CREATE\s+PROC(?:EDURE)?\s+[\w.\[\]$]+(?<h>.*?)\bAS\b", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ParamRx = new(@"@(?<n>\w+)\s+(?<t>[A-Za-z]+(?:\s*\(\s*(?:\d+|max)(?:\s*,\s*\d+)?\s*\))?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Tên bảng chuẩn: bỏ dbo / [ ], bảng phân kỳ (xxx$%Partition) → xxx$000000; bảng tạm / biến bảng → null.</summary>
    private static string? TableName(string t)
    {
        t = t.Trim('[', ']');
        if (t.StartsWith('#') || t.StartsWith('@')) return null;
        if (t.EndsWith("$%Partition", StringComparison.OrdinalIgnoreCase)) t = t[..^"%Partition".Length] + "000000";
        return t.Contains('%') ? null : t.ToLowerInvariant();
    }

    public static FastAskKnowledge Mine(IEnumerable<(string Name, string Def)> mods, string project)
    {
        var k = new FastAskKnowledge();
        var n = 0;
        foreach (var (name, def) in mods)
        {
            n++;
            // alias → bảng theo vị trí xuất hiện (FROM x a / JOIN y b)
            var aliases = FromJoinRx.Matches(def).Where(m => !NotAlias.Contains(m.Groups["a"].Value))
                .Select(m => (Pos: m.Index, Alias: m.Groups["a"].Value.ToLowerInvariant(), Table: TableName(m.Groups["t"].Value))).ToList();
            string? Resolve(string alias, int pos) => aliases.LastOrDefault(a => a.Pos <= pos && a.Alias == alias.ToLowerInvariant()).Table;
            var seen = new HashSet<string>();
            foreach (Match m in JoinOnRx.Matches(def))
            {
                var tj = TableName(m.Groups["t"].Value);
                var aj = m.Groups["a"].Value.ToLowerInvariant();
                string? T(string alias) => alias.Equals(aj, StringComparison.OrdinalIgnoreCase) ? tj : Resolve(alias, m.Index);
                var (tx, ty) = (T(m.Groups["x"].Value), T(m.Groups["y"].Value));
                if (tx is null || ty is null || tx == ty) continue;
                var (a, ac, b, bc) = string.CompareOrdinal(tx, ty) <= 0 ? (tx, m.Groups["xc"].Value, ty, m.Groups["yc"].Value) : (ty, m.Groups["yc"].Value, tx, m.Groups["xc"].Value);
                var cond = $"{a}.{ac.ToLowerInvariant()} = {b}.{bc.ToLowerInvariant()}";
                if (!seen.Add(a + "|" + b + "|" + cond)) continue;   // mỗi store đếm 1 lần / cặp
                Bump(k.Joins, a + "|" + b, cond);
            }
            // cột #report theo bảng phân kỳ
            foreach (Match m in StructRx.Matches(def))
            {
                var t = m.Groups["t"].Value.ToLowerInvariant();
                var pos = 0;
                foreach (var col in SplitTop(m.Groups["cols"].Value))
                {
                    var c = Regex.Match(col.Trim(), @"\bAS\s+\[?(\w+)\]?\s*$", RegexOptions.IgnoreCase) is { Success: true } asm ? asm.Groups[1].Value
                          : Regex.Match(col.Trim(), @"^(?:\w+\.)?\[?(\w+)\]?$") is { Success: true } cm ? cm.Groups[1].Value : null;
                    if (c is null || c.StartsWith("sys", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!k.ReportCols.TryGetValue(t, out var d)) k.ReportCols[t] = d = new(StringComparer.OrdinalIgnoreCase);
                    d[c.ToLowerInvariant()] = d.TryGetValue(c, out var v) ? new[] { v[0] + 1, v[1] + pos } : new[] { 1, pos };
                    pos++;
                }
            }
            // cột ngày của Partition$Execute: bảng phân kỳ gần nhất phía trên câu EXEC
            foreach (Match m in ExecPartRx.Matches(def))
                if (PartTableRx.Matches(def[..m.Index]).LastOrDefault() is { } pt)
                    Bump(k.DateCols, pt.Groups["t"].Value.ToLowerInvariant(), Regex.Replace(m.Groups["c"].Value, @"^\w+\.", "").ToLowerInvariant());
            // tham số store báo cáo
            if (name.StartsWith("rs_", StringComparison.OrdinalIgnoreCase) && HeaderRx.Match(def) is { Success: true } hm)
                foreach (Match p in ParamRx.Matches(hm.Groups["h"].Value))
                    Bump(k.Params, p.Groups["n"].Value, FastGhostRules.NormType(p.Groups["t"].Value));
        }
        k.Projects.Add($"{project} · {DateTime.Now:dd/MM/yyyy} · {n} store");
        return k;
    }

    private static void Bump(Dictionary<string, Dictionary<string, int>> d, string key, string val)
    {
        if (!d.TryGetValue(key, out var inner)) d[key] = inner = new(StringComparer.OrdinalIgnoreCase);
        inner[val] = inner.GetValueOrDefault(val) + 1;
    }

    /// <summary>Tách danh sách cột theo dấu phẩy ở mức ngoặc 0.</summary>
    private static IEnumerable<string> SplitTop(string s)
    {
        int depth = 0, start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (s[i] == ',' && depth == 0) { yield return s[start..i]; start = i + 1; }
        }
        yield return s[start..];
    }

    // ---------------------------------------------------------------- lưu / gộp

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    public static FastAskKnowledge Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<FastAskKnowledge>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    /// <summary>Gộp mọi dự án đã học (fastask\projects\*.json) thành kiến thức chung.</summary>
    public static FastAskKnowledge MergeShared()
    {
        var all = new FastAskKnowledge();
        var dir = Path.Combine(Dir, "projects");
        if (Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                var p = Load(f);
                all.Projects.AddRange(p.Projects);
                void Add(Dictionary<string, Dictionary<string, int>> to, Dictionary<string, Dictionary<string, int>> from)
                {
                    foreach (var (key, inner) in from) foreach (var (v, c) in inner) { if (!to.TryGetValue(key, out var x)) to[key] = x = new(StringComparer.OrdinalIgnoreCase); x[v] = x.GetValueOrDefault(v) + c; }
                }
                Add(all.Joins, p.Joins);
                Add(all.DateCols, p.DateCols);
                Add(all.Params, p.Params);
                foreach (var (t, cols) in p.ReportCols)
                {
                    if (!all.ReportCols.TryGetValue(t, out var d)) all.ReportCols[t] = d = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var (c, v) in cols) d[c] = d.TryGetValue(c, out var o) ? new[] { o[0] + v[0], o[1] + v[1] } : v;
                }
            }
        all.Save(SharedPath);
        _shared = null;
        return all;
    }

    private static (DateTime Stamp, FastAskKnowledge K)? _shared;

    /// <summary>Kiến thức chung trên máy (đọc lại khi file đổi).</summary>
    public static FastAskKnowledge Shared
    {
        get
        {
            var stamp = File.Exists(SharedPath) ? File.GetLastWriteTimeUtc(SharedPath) : DateTime.MinValue;
            if (_shared is { } s && s.Stamp == stamp) return s.K;
            var k = Load(SharedPath);
            _shared = (stamp, k);
            return k;
        }
    }

    // ---------------------------------------------------------------- tra

    /// <summary>Điều kiện JOIN hay dùng nhất giữa 2 bảng (đã đổi về đúng thứ tự a → b) + số store / tổng.</summary>
    public (string Cond, int N, int Total)? JoinFor(string a, string b)
    {
        a = TableName(a) ?? a.ToLowerInvariant();
        b = TableName(b) ?? b.ToLowerInvariant();
        var (x, y) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
        if (!Joins.TryGetValue(x + "|" + y, out var d) || d.Count == 0) return null;
        var top = d.OrderByDescending(v => v.Value).First();
        return (top.Key, top.Value, d.Values.Sum());
    }

    /// <summary>Cột #report hay có của bảng phân kỳ (theo số store, rồi vị trí trung bình).</summary>
    public List<(string Col, int N)> ReportColumnsFor(string table) =>
        ReportCols.TryGetValue(table, out var d) ? d.OrderByDescending(x => x.Value[0]).Select(x => (x.Key, x.Value[0], Avg: x.Value[1] / (double)x.Value[0]))
            .Take(14).OrderBy(x => x.Avg).Select(x => (x.Key, x.Item2)).ToList() : new();

    public string? DateColumnFor(string table) => DateCols.TryGetValue(table, out var d) && d.Count > 0 ? d.OrderByDescending(x => x.Value).First().Key : null;

    public List<(string Name, string Type, int N)> TopParams(int n) =>
        Params.Select(p => (p.Key, p.Value.OrderByDescending(x => x.Value).First().Key, p.Value.Values.Sum())).OrderByDescending(x => x.Item3).Take(n).ToList();
}

/// <summary>Xác nhận ✔ / ✘ ở Hỏi Fast (<c>%AppData%\Bcode\fastask\feedback.json</c>): xếp lại thẻ cho câu hỏi tương tự, lưu câu trả lời người dùng sửa,
/// và điều chỉnh ma trận ghost text (ô call / func / filter / decl / line) qua <see cref="FastGhostMiner.AdjustmentsSource"/>.</summary>
public sealed class FastAskFeedback
{
    /// <param name="Sig">Chữ ký câu hỏi (từ khoá không dấu đã sắp xếp / tham số luồng báo cáo).</param>
    /// <param name="Ref">Thẻ được chấm: "m|loại|khoá|cách viết" (ô ma trận), "store|tên", "answer|…", "snip|…", "join|a|b", …</param>
    /// <param name="Correction">Nội dung người dùng sửa / gõ lại (null = chỉ ✔ / ✘).</param>
    public sealed record Vote(string Sig, string Ref, int V, string? Correction, DateTime At);

    public List<Vote> Votes { get; set; } = new();

    public static string FilePath => Path.Combine(FastAskKnowledge.Dir, "feedback.json");
    private static FastAskFeedback? _instance;
    public static FastAskFeedback Instance => _instance ??= Load();

    private static FastAskFeedback Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<FastAskFeedback>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Add(Vote v)
    {
        lock (Votes) Votes.Add(v);
        Directory.CreateDirectory(FastAskKnowledge.Dir);
        lock (Votes) File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        if (v.Ref.StartsWith("m|")) FastGhostMiner.MergeShared();   // ô ma trận ghost text → gộp lại lớp chung ngay
    }

    /// <summary>Tổng ✔ − ✘ của thẻ cho đúng câu hỏi này / cho mọi câu hỏi.</summary>
    public (int Here, int All) Net(string sig, string reference)
    {
        lock (Votes)
            return (Votes.Where(v => v.Ref == reference && v.Sig == sig).Sum(v => Math.Sign(v.V)), Votes.Where(v => v.Ref == reference).Sum(v => Math.Sign(v.V)));
    }

    /// <summary>Câu trả lời người dùng đã sửa cho câu hỏi này (mới nhất trước).</summary>
    public List<Vote> Corrections(string sig)
    {
        // sửa ô ma trận (m|…) chỉ vào ma trận, không thành "câu trả lời đã sửa" của cả câu hỏi
        lock (Votes) return Votes.Where(v => v.Sig == sig && !v.Ref.StartsWith("m|") && !string.IsNullOrWhiteSpace(v.Correction)).OrderByDescending(v => v.At).ToList();
    }

    /// <summary>Điều chỉnh ma trận ghost text: ✔ +3, ✘ −3 cho cách viết đã hiện; cách viết người dùng gõ lại +5.</summary>
    public IEnumerable<FastGhostMiner.Adjustment> Adjustments()
    {
        List<Vote> list;
        lock (Votes) list = Votes.Where(v => v.Ref.StartsWith("m|")).ToList();
        foreach (var v in list)
        {
            var p = v.Ref.Split('|', 4);
            if (p.Length < 4) continue;
            if (v.V != 0) yield return new(p[1], p[2], p[3], v.V > 0 ? 3 : -3);
            if (!string.IsNullOrWhiteSpace(v.Correction)) yield return new(p[1], p[2], v.Correction.Trim(), 5, Prefer: true);
        }
    }
}

/// <summary>Lịch sử chat Hỏi Fast theo phiên (<c>%AppData%\Bcode\fastask\sessions\*.json</c>) — trang gửi nguyên danh sách tin nhắn sau mỗi lượt.</summary>
public static class FastAskSessions
{
    public static string Dir => Path.Combine(FastAskKnowledge.Dir, "sessions");
    private static string PathOf(string id) => Path.Combine(Dir, Regex.Replace(id, @"[^\w-]", "") + ".json");

    public static void Save(string id, string json)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(PathOf(id), json);
    }

    public static string? Load(string id) => File.Exists(PathOf(id)) ? File.ReadAllText(PathOf(id)) : null;

    public static void Delete(string id) { try { File.Delete(PathOf(id)); } catch { /* đã xoá */ } }

    public static void DeleteAll() { try { if (Directory.Exists(Dir)) foreach (var f in Directory.GetFiles(Dir, "*.json")) File.Delete(f); } catch { } }

    /// <summary>Danh sách phiên mới nhất trước: id, tiêu đề, lúc sửa.</summary>
    public static List<object> List()
    {
        var res = new List<object>();
        if (!Directory.Exists(Dir)) return res;
        foreach (var f in new DirectoryInfo(Dir).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Take(200))
        {
            string title = Path.GetFileNameWithoutExtension(f.Name);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f.FullName));
                if (doc.RootElement.TryGetProperty("title", out var t) && t.GetString() is { Length: > 0 } s) title = s;
            }
            catch { /* file hỏng → vẫn hiện để xoá */ }
            res.Add(new { id = Path.GetFileNameWithoutExtension(f.Name), title, at = f.LastWriteTime.ToString("dd/MM HH:mm") });
        }
        return res;
    }
}
