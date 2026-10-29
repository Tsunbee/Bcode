using System.Collections.Concurrent;
using System.Text.Json;
using BcodeViewer.App.Settings;

namespace BcodeViewer.App.Host.FastSource;

/// <summary>
/// Giữ kiến thức Fast của BcodeViewer và nạp / học ở nền (không bao giờ chặn luồng UI):
/// <list type="number">
/// <item>Ma trận đi kèm <c>Assets\fastsource\ghost.json</c> (rút từ <c>Bcode.App\Templates\fileSource</c>, công khai).</item>
/// <item>Source Fast theo phiên bản (Settings → "Thư mục source Fast các phiên bản"): lịch sử chỉnh sửa (<see cref="SourceVersions"/>) + ghost học
/// từ bản mới nhất của mọi file → <c>viewer\versions\</c>, <c>viewer\ghost\</c> trong thư mục kiến thức.</item>
/// <item>Source của dự án đang mở (SourcePath của workspace trong Bcode): <c>App_Data\Controllers</c> + <c>Main</c> → <c>viewer\ghost\&lt;dự án&gt;.json</c>.</item>
/// </list>
/// Ghost dùng = gộp các lớp đã có; lớp nào chưa xong thì dùng lớp đã có (lần đầu đọc source qua ổ mạng mất hàng chục giây).
/// </summary>
public sealed partial class FastSourceService
{
    private readonly ViewerSettings _settings;
    /// <summary>Ma trận nền đang dùng: mẫu đi kèm, hoặc mẫu gộp với source Fast mới nhất (chỉ giữ 1 bản gộp — mỗi bản ~100 MB RAM).</summary>
    private SourceGhost? _base;
    private bool _hasFast;
    private readonly ConcurrentDictionary<string, SourceGhost?> _projectGhost = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SourceGhost> _merged = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private Task? _shippedLoad;

    /// <summary>Kiến thức lịch sử phiên bản đã nạp (null = chưa khai thư mục / chưa học xong).</summary>
    public SourceVersions? Versions { get; private set; }
    /// <summary>Trạng thái học gần nhất (hiện ở Settings / Hỏi Fast).</summary>
    public string Status { get; private set; } = "";
    /// <summary>Kiến thức vừa đổi (học xong 1 lớp) — Hỏi Fast dựng lại kho tìm kiếm.</summary>
    public event Action? KnowledgeChanged;

    public FastSourceService(ViewerSettings settings)
    {
        _settings = settings;
    }

    public static string ShippedPath => Path.Combine(AppContext.BaseDirectory, "Assets", "fastsource", "ghost.json");

    /// <summary>Bắt đầu nạp ở nền (gọi lúc mở app và sau khi đổi Settings).</summary>
    public void Warmup()
    {
        _shippedLoad ??= Task.Run(() =>
        {
            var shipped = LoadShipped();
            if (shipped is not null && !_hasFast) { _base = shipped; _merged.Clear(); }
        });
        StartVersionsLearn(force: false);
        // dựng sẵn kho Hỏi Fast (~vài giây với source Fast các phiên bản) để câu hỏi đầu không phải chờ
        StartJob("askwarm", () => { _shippedLoad?.Wait(); AskFor(null); }, oneShot: true);
    }

    // ---------------------------------------------------------------- ghost

    private SourceGhost? LoadShipped()
    {
        try { return SourceGhost.FromJson(JsonDocument.Parse(File.ReadAllText(ShippedPath)).RootElement); }
        catch (Exception ex) { Status = "Không đọc được ma trận đi kèm: " + ex.Message; return null; }
    }

    /// <summary>Gộp mẫu đi kèm với ma trận học từ source Fast mới nhất thành nền mới.</summary>
    private void SetFastGhost(SourceGhost fast)
    {
        _shippedLoad?.Wait();
        var shipped = LoadShipped();
        _base = shipped is null ? fast : SourceGhost.Merge(shipped, fast);
        _hasFast = true;
        _merged.Clear();
    }

    /// <summary>Gợi ý tại con trỏ; "" = không có. <paramref name="path"/> = file đang mở (chọn loại xml / aspx / js và dự án).</summary>
    public SourceGhost.Ghost? Ghost(string before, string after, string? path, string? region, bool longMode)
    {
        var kind = SourceGhost.KindOfPath(path);
        if (kind == "") return null;
        var g = GhostFor(path);
        return g?.Explain(before, after, kind, region, longMode, root: SourceGhost.RootOf(path, before));
    }

    /// <summary>Kiến thức ghost cho file: đi kèm + source Fast theo bản + dự án của file (gộp 1 lần, nhớ lại).</summary>
    private SourceGhost? GhostFor(string? path)
    {
        var b = _base;
        if (b is null) return null;
        var (project, sourcePath) = SafeResolve(path);
        if (project is not null && sourcePath is not null) StartProjectLearn(project, sourcePath);
        var pg = project is not null && _projectGhost.TryGetValue(project, out var p) ? p : null;
        if (pg is null) return b;
        if (_merged.TryGetValue(project!, out var m)) return m;
        // gộp tốn vài giây với ma trận lớn → gộp ở nền, tạm dùng nền; chỉ giữ bản gộp của dự án vừa dùng
        StartJob("merge:" + project, () =>
        {
            var r = SourceGhost.Merge(b, pg);
            if (!ReferenceEquals(b, _base)) return;
            _merged.Clear();
            _merged[project!] = r;
        }, oneShot: true);
        return b;
    }

    private static (string? Name, string? SourcePath) SafeResolve(string? path)
    {
        try { return WorkspaceConnection.ResolveSourceRoot(path); } catch { return (null, null); }
    }

    /// <summary>Chạy 1 việc nền mỗi khoá 1 lần; <paramref name="oneShot"/> = xong thì cho chạy lại được (gộp lại sau khi có lớp mới).</summary>
    private void StartJob(string key, Action work, bool oneShot = false)
    {
        if (!_jobs.TryAdd(key, Task.CompletedTask)) return;
        _jobs[key] = Task.Run(() =>
        {
            try { work(); }
            catch (Exception ex) { Status = "Lỗi học kiến thức: " + ex.Message; }
            finally { if (oneShot) _jobs.TryRemove(key, out _); }
        });
    }

    // ---------------------------------------------------------------- source dự án

    /// <summary>Đổi khi cách rút kiến thức đổi (2 = thêm ma trận dòng <field name="X"> + khai báo import; 3 = tách dòng field của <views> với <fields>; 4 = thêm mục <template> của file import, name ="x")
    /// — file cũ bị bỏ qua, tự học lại 1 lần.</summary>
    public const string Format = "v4";

    private static string ProjectGhostFile(string project) => ViewerKnowledge.Viewer("ghost", ViewerKnowledge.SafeName(project) + "." + Format + ".json");

    /// <summary>Học source dự án ở nền (đã có file kiến thức dưới 7 ngày thì chỉ đọc lại).</summary>
    public void StartProjectLearn(string project, string sourcePath, bool force = false)
    {
        if (!force && _projectGhost.ContainsKey(project)) return;
        _projectGhost.TryAdd(project, null);
        if (force) _jobs.TryRemove("project:" + project, out _);
        StartJob("project:" + project, () =>
        {
            var file = ProjectGhostFile(project);
            var askFile = AskFile(ViewerKnowledge.SafeName(project));
            SourceGhost? g = null;
            if (!force && File.Exists(file) && File.Exists(askFile) && (DateTime.Now - File.GetLastWriteTime(file)).TotalDays < 7)
            {
                g = SourceGhost.FromJson(JsonDocument.Parse(File.ReadAllText(file)).RootElement);
                _projectDocs[project] = SourceAsk.FromJson(File.ReadAllText(askFile));
            }
            else
            {
                var files = ReadSource(sourcePath, s => Status = s).ToList();
                if (files.Count == 0) return;
                var full = SourceGhost.Train(files);
                g = new SourceGhost();
                foreach (var (k, m) in full.Models) g.Models[k] = m.Filter(3);
                foreach (var (k, d) in full.Fields) g.Fields[k] = d;
                ViewerKnowledge.WriteAtomic(file, JsonSerializer.Serialize(g.ToJson(), ViewerKnowledge.Json));
                var docs = SourceAsk.Build(files, "dự án " + project).Concat(SourceAsk.BuildFunctions(files, "dự án " + project)).ToList();
                ViewerKnowledge.WriteAtomic(askFile, SourceAsk.ToJson(docs));
                _projectDocs[project] = docs;
                Status = $"Đã học source dự án {project}: {files.Count} file";
            }
            _projectGhost[project] = g;
            _merged.Clear();
            _askCache = null;
            KnowledgeChanged?.Invoke();
        });
    }

    /// <summary>File source của 1 site Fast: controller (App_Data\Controllers, bỏ Templates) + Main (aspx / ascx / js).</summary>
    public static IEnumerable<(string Path, string Text)> ReadSource(string sourcePath, Action<string>? status = null)
    {
        var roots = new[] { Path.Combine(sourcePath, "App_Data", "Controllers"), Path.Combine(sourcePath, "Main") };
        var n = 0;
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories); } catch { continue; }
            foreach (var f in files)
            {
                if (SourceVersions.RelKey(f) is null || System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"_bk\d*|\.bak|copy", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                string text;
                try { if (new FileInfo(f).Length > 3_000_000) continue; text = File.ReadAllText(f).Replace("\r\n", "\n"); } catch { continue; }
                if (++n % 200 == 0) status?.Invoke($"Đang đọc source: {n} file…");
                yield return (f, text);
            }
        }
    }

    // ---------------------------------------------------------------- source Fast theo phiên bản

    private static string FastGhostFile(string root) => ViewerKnowledge.Viewer("ghost", "fastsource-" + Format + "-" + ViewerKnowledge.SafeName(root.TrimEnd('\\', '/')) + ".json");

    /// <summary>Nạp / học lịch sử phiên bản ở nền: đã học và thư mục không đổi (chữ ký = tên + giờ sửa các thư mục bản) thì chỉ đọc lại.</summary>
    public void StartVersionsLearn(bool force)
    {
        var root = (_settings.FastSourceVersionsPath ?? "").Trim();
        if (root.Length == 0)
        {
            Versions = null; _fastDocs = null; _askCache = null;
            if (_hasFast) { _hasFast = false; _base = LoadShipped(); _merged.Clear(); }
            return;
        }
        if (force) _jobs.TryRemove("versions:" + root, out _);
        StartJob("versions:" + root, () =>
        {
            if (!Directory.Exists(root)) { Status = "Không thấy thư mục source Fast: " + root; return; }
            var saved = SourceVersions.Load(root);
            var sig = SourceVersions.SignatureOf(root);
            var ghostFile = FastGhostFile(root);
            var askFile = AskFile("fastsource-" + ViewerKnowledge.SafeName(root.TrimEnd('\\', '/')));
            if (!force && saved is not null && saved.Signature == sig && File.Exists(ghostFile) && File.Exists(askFile))
            {
                Versions = saved;
                SetFastGhost(SourceGhost.FromJson(JsonDocument.Parse(File.ReadAllText(ghostFile)).RootElement));
                _fastDocs = SourceAsk.FromJson(File.ReadAllText(askFile));
                Status = $"Source Fast: {saved.Versions.Count} bản, {saved.Latest.Count} file, {saved.Changes.Count} thay đổi (học lúc {saved.ScannedAt:dd/MM HH:mm})";
            }
            else
            {
                var v = SourceVersions.Scan(root, (name, i, n) => Status = $"Đang học source Fast theo phiên bản: {i}/{n} {name}");
                v.Save();
                Versions = v;
                Status = $"Đang học ghost từ bản mới nhất ({v.Latest.Count} file)…";
                var files = v.LatestFiles().Select(x => { try { return (x.Path, File.ReadAllText(x.Path).Replace("\r\n", "\n")); } catch { return (x.Path, ""); } })
                    .Where(x => x.Item2.Length > 0).ToList();
                var full = SourceGhost.Train(files);
                var g = new SourceGhost();
                // ~9.000 file: giữ ngữ cảnh gặp ≥ 6 lần, ô field ≥ 3 lần (đủ chính xác, RAM gọn hơn nhiều)
                foreach (var (k, m) in full.Models) g.Models[k] = m.Filter(6);
                foreach (var (k, d) in full.Fields) if (d.Where(x => x.Key != "").Sum(x => x.Value) >= 3) g.Fields[k] = d;
                ViewerKnowledge.WriteAtomic(ghostFile, JsonSerializer.Serialize(g.ToJson(), ViewerKnowledge.Json));
                SetFastGhost(g);
                var docs = SourceAsk.Build(files, "Fast " + v.Versions.LastOrDefault(x => x.Product == "FBO")?.Name).Concat(SourceAsk.BuildFunctions(files, "Fast")).ToList();
                ViewerKnowledge.WriteAtomic(askFile, SourceAsk.ToJson(docs));
                _fastDocs = docs;
                Status = $"Đã học source Fast: {v.Versions.Count} bản, {v.Latest.Count} file, {v.Changes.Count} thay đổi";
            }
            _merged.Clear();
            _askCache = null;
            KnowledgeChanged?.Invoke();
        });
    }
}
