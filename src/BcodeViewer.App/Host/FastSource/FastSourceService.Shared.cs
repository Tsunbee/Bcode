using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BcodeViewer.App.Host.FastSource;

/// <summary>
/// Kiến thức chung + học từ người dùng — cùng mô hình với Bcode SQL (FastGhostMiner.MergeShared → ghost\_shared.json, xác nhận ✔/✘ chồng lên):
/// <list type="bullet">
/// <item><c>viewer\ghost\_shared.&lt;Format&gt;.json</c> = gộp ma trận của MỌI dự án đã học trong thư mục kiến thức (của máy này và của đồng nghiệp nếu thư mục
/// kiến thức là thư mục mạng chung) — dự án chưa học (hoặc chưa mở lần nào) vẫn hưởng cách viết của các dự án khác. Gộp lại sau mỗi lần học 1 dự án.</item>
/// <item>Phản hồi: gợi ý hiện ra rồi người dùng nhận (Tab / gõ đúng hết) hay bỏ qua (gõ khác đi / Enter sang dòng khác) → chỉnh ô ma trận đã sinh ra nó
/// (<see cref="SourceGhost.Adjust"/>): nhận → cách viết đó nặng thêm, bỏ qua → độ tin cả ô giảm. Mỗi máy 1 file <c>viewer\ghost\feedback\&lt;máy-người&gt;.json</c>
/// (không tranh ghi 1 file trên thư mục mạng); khi dựng kiến thức thì cộng phản hồi của mọi máy.</item>
/// </list>
/// Thứ tự lớp: mẫu đi kèm (+ source Fast các phiên bản) → chung (_shared) → phản hồi → dự án đang mở (nếu _shared chưa có dự án này).
/// </summary>
public sealed partial class FastSourceService
{
    private static string GhostDir => ViewerKnowledge.Viewer("ghost");
    private static string SharedFile => Path.Combine(GhostDir, "_shared." + Format + ".json");
    private static string GhostFeedbackDir => Path.Combine(GhostDir, "feedback");
    private static string MyGhostFeedbackFile => Path.Combine(GhostFeedbackDir, ViewerKnowledge.SafeName(Environment.MachineName + "-" + Environment.UserName) + ".json");

    /// <summary>Dự án đã có trong _shared (tên an toàn) — dự án này khỏi gộp lớp dự án lần nữa (tránh đếm 2 lần).</summary>
    private HashSet<string> _sharedProjects = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Đường dẫn ma trận source Fast các phiên bản đang dùng (null = chỉ mẫu đi kèm) — để dựng lại nền khi có lớp chung / phản hồi mới.</summary>
    private string? _fastGhostFile;

    // ---------------------------------------------------------------- nền = đi kèm (+ Fast) + chung + phản hồi

    /// <summary>Dựng lại nền ở nền (đọc lại từ đĩa nên không cần giữ thêm 1 bản gốc trong RAM).</summary>
    private void ComposeBase()
    {
        StartJob("compose", () =>
        {
            _shippedLoad?.Wait();
            var raw = LoadShipped();
            if (raw is null) return;
            if (_fastGhostFile is { } ff && File.Exists(ff))
                raw = SourceGhost.Merge(raw, SourceGhost.FromJson(JsonDocument.Parse(File.ReadAllText(ff)).RootElement));
            var (shared, projects) = LoadShared();
            // luôn tạo bản mới (Merge sao chép các bảng) để phản hồi chỉnh trên bản này không dính vào bản đọc từ đĩa
            var b = SourceGhost.Merge(raw, shared ?? new SourceGhost());
            ApplyGhostFeedback(b, AllGhostFeedback());
            _sharedProjects = projects;
            _base = b;
            _merged.Clear();
            KnowledgeChanged?.Invoke();
        }, oneShot: true);
    }

    private static (SourceGhost? Ghost, HashSet<string> Projects) LoadShared()
    {
        var projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(SharedFile)) return (null, projects);
            using var doc = JsonDocument.Parse(File.ReadAllText(SharedFile));
            if (doc.RootElement.TryGetProperty("projects", out var ps)) foreach (var p in ps.EnumerateArray()) projects.Add(p.GetString() ?? "");
            return (SourceGhost.FromJson(doc.RootElement), projects);
        }
        catch { return (null, projects); }
    }

    /// <summary>Ma trận các dự án trong thư mục kiến thức (bỏ _shared / source Fast các phiên bản / bản Format cũ).</summary>
    private static List<string> ProjectFiles()
    {
        try
        {
            return Directory.Exists(GhostDir)
                ? Directory.GetFiles(GhostDir, "*." + Format + ".json").Where(f => !Path.GetFileName(f).StartsWith('_') && !Path.GetFileName(f).StartsWith("fastsource-")).ToList()
                : new List<string>();
        }
        catch { return new List<string>(); }
    }

    /// <summary>Gộp ma trận mọi dự án → _shared (chỉ khi có file dự án mới hơn _shared, hoặc <paramref name="force"/>), rồi dựng lại nền.</summary>
    public void MergeShared(bool force = false)
    {
        StartJob("shared", () =>
        {
            var files = ProjectFiles();
            if (files.Count == 0) return;
            var sharedAt = File.Exists(SharedFile) ? File.GetLastWriteTimeUtc(SharedFile) : DateTime.MinValue;
            var (_, had) = LoadShared();
            var names = files.Select(f => Path.GetFileName(f)[..^(Format.Length + 6)]).ToList();   // bỏ ".<Format>.json"
            if (!force && files.All(f => File.GetLastWriteTimeUtc(f) <= sharedAt) && names.All(had.Contains))
            {
                ComposeBase();
                return;
            }
            Status = $"Đang gộp kiến thức chung ({files.Count} dự án)…";
            var acc = new SourceGhost();
            var used = new List<string>();
            for (var i = 0; i < files.Count; i++)
            {
                try { acc = SourceGhost.Merge(acc, SourceGhost.FromJson(JsonDocument.Parse(File.ReadAllText(files[i])).RootElement)); used.Add(names[i]); }
                catch { /* file dở / hỏng (máy khác đang ghi) — lần sau gộp lại */ }
            }
            var node = JsonSerializer.SerializeToNode(acc.ToJson(), ViewerKnowledge.Json)!.AsObject();
            node["projects"] = new JsonArray(used.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
            ViewerKnowledge.WriteAtomic(SharedFile, node.ToJsonString(ViewerKnowledge.Json));
            Status = $"Kiến thức chung: {used.Count} dự án";
            ComposeBase();
        }, oneShot: true);
    }

    // ---------------------------------------------------------------- phản hồi

    /// <summary>Gợi ý đã trả cho trang (id → ô ma trận + kiến thức đã dùng) — chờ trang báo nhận / bỏ qua.</summary>
    private readonly ConcurrentDictionary<string, (SourceGhost G, SourceGhost.Cell? Cell, string Stat, string Text)> _shown = new();
    private readonly object _fbLock = new();
    /// <summary>Phản hồi của máy này: "D|khoá" → cách viết → cộng thêm ("" = phần khác); thống kê "loại@vùng" → [hiện, nhận].</summary>
    private Dictionary<string, Dictionary<string, int>>? _myCells;
    private Dictionary<string, int[]>? _myStats;
    private System.Threading.Timer? _fbSave;

    private sealed class GhostFeedbackFile
    {
        public Dictionary<string, Dictionary<string, int>> Cells { get; set; } = new();
        public Dictionary<string, int[]> Stats { get; set; } = new();
    }

    private static GhostFeedbackFile ReadGhostFeedback(string path)
    {
        try { return JsonSerializer.Deserialize<GhostFeedbackFile>(File.ReadAllText(path)) ?? new GhostFeedbackFile(); }
        catch { return new GhostFeedbackFile(); }
    }

    /// <summary>Phản hồi của mọi máy trong thư mục kiến thức (cộng lại).</summary>
    private static List<GhostFeedbackFile> AllGhostFeedback()
    {
        try { return Directory.Exists(GhostFeedbackDir) ? Directory.GetFiles(GhostFeedbackDir, "*.json").Select(ReadGhostFeedback).ToList() : new List<GhostFeedbackFile>(); }
        catch { return new List<GhostFeedbackFile>(); }
    }

    private static void ApplyGhostFeedback(SourceGhost g, IEnumerable<GhostFeedbackFile> all)
    {
        foreach (var f in all)
            foreach (var (cell, opts) in f.Cells)
            {
                var bar = cell.IndexOf('|');
                if (bar <= 0) continue;
                var dict = cell[..bar];
                var key = cell[(bar + 1)..];
                foreach (var (opt, n) in opts)
                    if (opt.Length == 0) g.Adjust(dict, key, "", 0, n); else g.Adjust(dict, key, opt, n, 0);
            }
    }

    /// <summary>Ghi nhớ gợi ý vừa trả cho trang; trả id để trang báo lại.</summary>
    private string RememberShown(SourceGhost g, SourceGhost.Ghost ghost, string stat)
    {
        if (_shown.Count > 300) _shown.Clear();
        var id = Guid.NewGuid().ToString("N")[..12];
        _shown[id] = (g, ghost.Cell, stat, ghost.Text);
        return id;
    }

    /// <summary>Trang báo: gợi ý <paramref name="id"/> được nhận (Tab / gõ đúng hết) hay bị bỏ qua.</summary>
    public void GhostFeedback(string id, bool accepted)
    {
        if (!_shown.TryRemove(id, out var s)) return;
        lock (_fbLock)
        {
            if (_myCells is null) { var mine = ReadGhostFeedback(MyGhostFeedbackFile); _myCells = mine.Cells; _myStats = mine.Stats; }
            var st = _myStats!.TryGetValue(s.Stat, out var a) ? a : _myStats[s.Stat] = new int[2];
            st[0]++;
            if (accepted) st[1]++;
            if (s.Cell is { } c && s.G.Table(c.Dict) is { } table)
            {
                // độ lớn theo cỡ ô: ô gặp hàng trăm lần thì 1 lần nhận / bỏ cũng phải thấy tác dụng
                var total = table.TryGetValue(c.Key, out var d) ? d.Values.Sum() : 0;
                var full = SourceGhost.Norm(s.Text) == SourceGhost.Norm(c.Option);
                int opt = 0, other = 0;
                // bỏ qua ≠ chắc chắn sai (có khi chỉ viết khác mà vẫn đúng) và 1 người không được tắt gợi ý của cả nhóm → cần vài lần bỏ qua mới tắt hẳn
                if (accepted) opt = Math.Max(1, (int)Math.Round(total * (full ? 0.10 : 0.05)));
                else other = Math.Max(1, (int)Math.Round(total * 0.12));
                s.G.Adjust(c.Dict, c.Key, c.Option, opt, other);   // có tác dụng ngay trong phiên này
                var cellKey = c.Dict + "|" + c.Key;
                if (!_myCells!.TryGetValue(cellKey, out var mc)) _myCells[cellKey] = mc = new(StringComparer.Ordinal);
                if (opt != 0) mc[c.Option.TrimStart()] = mc.GetValueOrDefault(c.Option.TrimStart()) + opt;
                if (other != 0) mc[""] = mc.GetValueOrDefault("") + other;
            }
            // gom ghi: tối đa 1 lần / 5 giây
            _fbSave ??= new System.Threading.Timer(_ => SaveFeedback(), null, Timeout.Infinite, Timeout.Infinite);
            _fbSave.Change(5000, Timeout.Infinite);
        }
    }

    private void SaveFeedback()
    {
        string json;
        lock (_fbLock)
        {
            if (_myCells is null) return;
            json = JsonSerializer.Serialize(new GhostFeedbackFile { Cells = _myCells, Stats = _myStats ?? new() }, ViewerKnowledge.Json);
        }
        try { ViewerKnowledge.WriteAtomic(MyGhostFeedbackFile, json); } catch { /* ổ mạng mất — lần sau ghi lại */ }
    }

    /// <summary>Tóm tắt cho Settings: thư mục kiến thức, số dự án trong kiến thức chung, tỷ lệ nhận gợi ý (mọi máy / máy này).</summary>
    public string KnowledgeSummary()
    {
        var all = AllGhostFeedback();
        int shown = 0, ok = 0;
        foreach (var f in all) foreach (var v in f.Stats.Values) { shown += v[0]; ok += v[1]; }
        int myShown = 0, myOk = 0;
        lock (_fbLock) foreach (var v in (_myStats ?? ReadGhostFeedback(MyGhostFeedbackFile).Stats).Values) { myShown += v[0]; myOk += v[1]; }
        var cells = all.Sum(f => f.Cells.Count);
        return $"Đang dùng: {ViewerKnowledge.Root}\\viewer · kiến thức chung: {_sharedProjects.Count} dự án · đã học từ người dùng: {cells} ô ma trận, "
            + $"nhận {ok}/{shown} gợi ý" + (shown > 0 ? $" ({ok * 100 / shown}%)" : "") + $" · máy này {myOk}/{myShown}";
    }

    /// <summary>Đổi thư mục kiến thức (Settings): bỏ kiến thức đã nạp của thư mục cũ, nạp / học lại theo thư mục mới.</summary>
    public void ResetKnowledge()
    {
        lock (_fbLock) { _myCells = null; _myStats = null; }
        _projectGhost.Clear();
        _merged.Clear();
        _sharedProjects = new(StringComparer.OrdinalIgnoreCase);
        StartVersionsLearn(force: false);
        MergeShared();
    }
}
