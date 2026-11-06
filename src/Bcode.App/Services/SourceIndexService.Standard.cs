using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Source chuẩn cho file .f MÃ HOÁ (khối lệnh nằm trong <c>&lt;![CDATA[&lt;Encrypted&gt;…]]&gt;</c> — không đọc được tên store / procedure). Giống FCode
/// (gặp &lt;Encrypted&gt; thì đọc file .xml cùng tên trong các bản source) và "Cấp source nhanh" của File Lookup: mỗi file .f mã hoá của chương trình
/// (không có .xml cạnh nó) → file .xml cùng đường dẫn ở phiên bản của dự án, không có thì phiên bản THẤP HƠN gần nhất có file
/// (<see cref="SourceGrantService.NearestLowerOrder"/>).
/// Kho source nằm trên ổ chậm / ổ mạng nên DỰNG NỀN một lần: liệt kê từng thư mục phiên bản (mới → cũ, dừng khi đã đủ file), đọc mỗi file .xml
/// tìm được 1 lần lấy tập băm từ (như chỉ mục chính), lưu ra <c>%AppData%\Bcode\source-index\std_….bin</c>. Lần sau nạp từ đĩa, chỉ làm lại file
/// .f đổi ngày sửa. Chỉ ĐỌC kho source và chương trình.
/// </summary>
public sealed partial class SourceIndexService
{
    public sealed record StdHit(string ProjectFile, string StdFile, string Version, int Count);

    private sealed class StdEntry { public string Std = "", Ver = ""; public long ProjTicks; public int[] Hashes = Array.Empty<int>(); }
    private sealed class StdMirror
    {
        public Dictionary<string, StdEntry> Files = new(StringComparer.OrdinalIgnoreCase);   // file .f của chương trình → bản .xml trong kho
        public HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);              // file .f không có ở phiên bản nào (khỏi tìm lại)
        public DateTime BuiltAt;                                                             // MinValue = chưa dựng xong lần nào trong phiên
        public volatile string Status = "";                                                  // bước đang làm (hiện ở sơ đồ)
        public volatile int Done, Total;                                                     // tiến độ bước đọc source (Total = 0: chưa tới bước đó)
    }

    /// <summary>Trạng thái dựng chỉ mục source chuẩn — để sơ đồ hiện tiến độ và tự tải lại khi xong. Building = đang dựng mà chưa có bản nào dùng được.</summary>
    public (bool Building, string Status, int Done, int Total) StdState(string sourceRoot, IReadOnlyList<string> roots, string version)
    {
        var key = StdKey(sourceRoot, roots, version);
        if (!StdMemory.TryGetValue(key, out var m)) return (false, "", 0, 0);
        var running = StdBuilding.TryGetValue(key, out var t) && !t.IsCompleted;
        int count; lock (m) count = m.Files.Count;
        var ready = m.BuiltAt != DateTime.MinValue || count > 0;
        return (running && !ready, m.Status, m.Done, m.Total);
    }

    private const int StdFormatVersion = 1;
    private static readonly TimeSpan StdRefreshEvery = TimeSpan.FromHours(12);
    private static readonly ConcurrentDictionary<string, StdMirror> StdMemory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task> StdBuilding = new(StringComparer.OrdinalIgnoreCase);
    private static readonly int EncryptedHash = Hash("encrypted");

    private static string StdKey(string sourceRoot, IReadOnlyList<string> roots, string version) =>
        sourceRoot.TrimEnd('\\', '/') + "|" + version + "|" + string.Join(";", roots);

    private static string StdPath(string key)
    {
        var safe = Regex.Replace(key, @"[^\w.\-]+", "_");
        if (safe.Length > 150) safe = safe[^150..];
        return Path.Combine(BcodePaths.AppData, "Bcode", "source-index", "std_" + safe + ".bin");
    }

    /// <summary>
    /// File .f mã hoá của chương trình mà bản source chuẩn nhắc tới <paramref name="name"/>. Không bao giờ chờ dựng chỉ mục: chưa có thì bắt đầu dựng NỀN
    /// và trả null kèm <paramref name="status"/> (bấm Làm mới sau); có rồi (kể cả nạp từ đĩa) thì tra ngay, cũ quá 12 giờ thì cập nhật nền.
    /// </summary>
    public List<StdHit>? FindInStandard(string sourceRoot, IReadOnlyList<string> roots, string version, IReadOnlyList<string> allVersions,
        string name, CancellationToken ct, out string? status)
    {
        var mirror = EnsureStd(sourceRoot, roots, version, allVersions, out status);
        if (mirror is null) return null;
        var h = Hash(name.ToLowerInvariant());
        var pattern = UsageSearchService.NamePattern(name);
        List<(string Proj, StdEntry E)> cand;
        lock (mirror) cand = mirror.Files.Where(kv => Array.BinarySearch(kv.Value.Hashes, h) >= 0).Select(kv => (kv.Key, kv.Value)).ToList();
        var hits = new List<StdHit>();
        foreach (var (proj, e) in cand)
        {
            ct.ThrowIfCancellationRequested();
            var (count, _) = Scan(e.Std, pattern);
            if (count > 0) hits.Add(new StdHit(proj, e.Std, e.Ver, count));
        }
        return hits;
    }

    /// <summary>Như <see cref="CandidateFilesAsync"/> nhưng trong chỉ mục source chuẩn (chỉ băm, không mở file): tên → (file .f chương trình, bản source, phiên bản).
    /// Chưa dựng xong thì trả rỗng.</summary>
    public Dictionary<string, List<StdHit>> StandardCandidates(string sourceRoot, IReadOnlyList<string> roots, string version, IReadOnlyList<string> allVersions,
        IReadOnlyCollection<string> names)
    {
        var result = new Dictionary<string, List<StdHit>>(StringComparer.OrdinalIgnoreCase);
        if (EnsureStd(sourceRoot, roots, version, allVersions, out _) is not { } mirror) return result;
        var wanted = names.Distinct(StringComparer.OrdinalIgnoreCase).Select(n => (Name: n, H: Hash(n.ToLowerInvariant()))).ToList();
        lock (mirror)
            foreach (var (proj, e) in mirror.Files)
                foreach (var (n, h) in wanted)
                    if (Array.BinarySearch(e.Hashes, h) >= 0)
                    {
                        if (!result.TryGetValue(n, out var l)) result[n] = l = new List<StdHit>();
                        l.Add(new StdHit(proj, e.Std, e.Ver, 0));
                    }
        return result;
    }

    /// <summary>Bản source chuẩn của 1 file .f mã hoá (đã có trong chỉ mục); null = chưa dựng / không có.</summary>
    public (string StdFile, string Version)? StandardOf(string sourceRoot, IReadOnlyList<string> roots, string version, string projectFile)
    {
        if (!StdMemory.TryGetValue(StdKey(sourceRoot, roots, version), out var mirror)) return null;
        lock (mirror) return mirror.Files.TryGetValue(projectFile, out var e) ? (e.Std, e.Ver) : null;
    }

    /// <summary>File có chữ "&lt;Encrypted&gt;" (theo chỉ mục chính của chương trình, không mở file).</summary>
    public bool IsEncryptedIndexed(string sourceRoot, string file)
    {
        var index = GetIndex(ControllersDir(sourceRoot), Folders, out _);
        lock (index) return index.Files.TryGetValue(file, out var e) && Array.BinarySearch(e.Hashes, EncryptedHash) >= 0;
    }

    /// <summary>Chương trình có ít nhất 1 file có chữ "&lt;Encrypted&gt;" (theo chỉ mục chính).</summary>
    public bool IsEncryptedIndexedAny(string sourceRoot)
    {
        var index = GetIndex(ControllersDir(sourceRoot), Folders, out _);
        lock (index) return index.Files.Values.Any(e => Array.BinarySearch(e.Hashes, EncryptedHash) >= 0);
    }

    private StdMirror? EnsureStd(string sourceRoot, IReadOnlyList<string> roots, string version, IReadOnlyList<string> allVersions, out string? status)
    {
        var key = StdKey(sourceRoot, roots, version);
        var mirror = StdMemory.GetOrAdd(key, k => LoadStd(k) ?? new StdMirror());
        var building = StdBuilding.TryGetValue(key, out var t) && !t.IsCompleted;
        if (!building && DateTime.Now - mirror.BuiltAt > StdRefreshEvery)
        {
            var task = Task.Run(() => BuildStd(key, mirror, sourceRoot, roots, version, allVersions));
            StdBuilding[key] = task;
            building = true;
        }
        int count; lock (mirror) count = mirror.Files.Count;
        var ready = mirror.BuiltAt != DateTime.MinValue || count > 0;
        status = building && !ready ? StdStatusText(version, mirror) : null;
        return ready ? mirror : null;
    }

    /// <summary>Dòng báo đang dựng: bước đang làm + % (bước đọc source), sơ đồ tự cập nhật khi xong.</summary>
    public static string StdStatusText(string version, string status, int done, int total) =>
        "Đang dựng chỉ mục source chuẩn " + version + " cho các file .f mã hoá (chạy nền, chỉ lần đầu)"
        + (total > 0 ? $" — {done * 100 / Math.Max(1, total)}% ({done}/{total} file)" : status.Length > 0 ? " — " + status : "")
        + " · sơ đồ tự cập nhật khi xong.";

    private static string StdStatusText(string version, StdMirror m) => StdStatusText(version, m.Status, m.Done, m.Total);

    private void BuildStd(string key, StdMirror mirror, string sourceRoot, IReadOnlyList<string> roots, string version, IReadOnlyList<string> allVersions)
    {
        try
        {
            // 1. File .f mã hoá của chương trình (chỉ mục chính đã có băm từ "Encrypted"), không có .xml cạnh nó
            var controllers = ControllersDir(sourceRoot);
            if (!Directory.Exists(controllers)) return;
            var index = GetIndex(controllers, Folders, out var ikey);
            List<(string File, long Ticks)> encrypted;
            mirror.Done = mirror.Total = 0;
            mirror.Status = "liệt kê file của chương trình";
            lock (index)
            {
                if (!(DateTime.Now - index.RefreshedAt < RefreshEvery && index.Files.Count > 0))
                    Refresh(controllers, Folders, ikey, index, s => mirror.Status = s.TrimEnd('…', '.'), CancellationToken.None);
                encrypted = index.Files
                    .Where(kv => kv.Key.EndsWith(".f", StringComparison.OrdinalIgnoreCase) && Array.BinarySearch(kv.Value.Hashes, EncryptedHash) >= 0
                                 && !index.Files.ContainsKey(kv.Key[..^2] + ".xml"))
                    .Select(kv => (kv.Key, kv.Value.Ticks)).ToList();
            }

            // 2. Giữ mục cũ còn đúng; còn lại tìm trong kho theo phiên bản (mới → cũ), mỗi phiên bản liệt kê thư mục 1 lần
            Dictionary<string, StdEntry> keep;
            lock (mirror) keep = encrypted.Where(x => mirror.Files.TryGetValue(x.File, out var e) && e.ProjTicks == x.Ticks)
                                          .ToDictionary(x => x.File, x => mirror.Files[x.File], StringComparer.OrdinalIgnoreCase);
            var todo = encrypted.Where(x => !keep.ContainsKey(x.File) && !mirror.Missing.Contains(x.File)).ToList();
            var byRel = new Dictionary<string, List<(string File, long Ticks)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in todo)
            {
                var rel = SourceGrantService.SourceRelative(Path.GetRelativePath(sourceRoot, x.File));
                if (!byRel.TryGetValue(rel, out var l)) byRel[rel] = l = new();
                l.Add(x);
            }
            var found = new Dictionary<string, (string Std, string Ver)>(StringComparer.OrdinalIgnoreCase);
            foreach (var ver in SourceGrantService.NearestLowerOrder(version, allVersions))
            {
                if (found.Count == byRel.Count) break;
                mirror.Status = $"tìm file trong kho, phiên bản {ver} (đã thấy {found.Count}/{byRel.Count} file)";
                foreach (var root in roots)
                {
                    var site = Path.Combine(root, ver);
                    foreach (var folder in Folders)
                    {
                        var dir = Path.Combine(site, "App_Data", "Controllers", folder);
                        try
                        {
                            if (!Directory.Exists(dir)) continue;
                            foreach (var f in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
                            {
                                var rel = Path.GetRelativePath(site, f);
                                if (byRel.ContainsKey(rel) && !found.ContainsKey(rel)) found[rel] = (f, ver);
                            }
                        }
                        catch { /* thư mục phiên bản không đọc được — bỏ qua */ }
                    }
                }
            }

            // 3. Đọc bản source tìm được, lấy tập băm
            var fresh = new ConcurrentDictionary<string, StdEntry>(StringComparer.OrdinalIgnoreCase);
            var done = 0;
            mirror.Status = "đọc file source";
            mirror.Total = found.Count;
            Parallel.ForEach(found, new ParallelOptions { MaxDegreeOfParallelism = 4 }, kv =>
            {
                try
                {
                    var hashes = HashesOf(File.ReadAllText(kv.Value.Std));
                    foreach (var x in byRel[kv.Key]) fresh[x.File] = new StdEntry { Std = kv.Value.Std, Ver = kv.Value.Ver, ProjTicks = x.Ticks, Hashes = hashes };
                }
                catch { /* file không đọc được — bỏ qua */ }
                mirror.Done = Interlocked.Increment(ref done);
            });

            lock (mirror)
            {
                mirror.Files = keep;
                foreach (var kv in fresh) mirror.Files[kv.Key] = kv.Value;
                foreach (var (rel, l) in byRel) if (!found.ContainsKey(rel)) foreach (var x in l) mirror.Missing.Add(x.File);
                mirror.BuiltAt = DateTime.Now;
                mirror.Status = "";
                mirror.Done = mirror.Total = 0;
            }
            SaveStd(key, mirror);
        }
        catch (Exception ex) { mirror.Status = "lỗi: " + ex.Message; mirror.BuiltAt = DateTime.Now; }
    }

    private static StdMirror? LoadStd(string key)
    {
        try
        {
            var path = StdPath(key);
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (r.ReadInt32() != StdFormatVersion || r.ReadString() != key) return null;
            var m = new StdMirror();
            var n = r.ReadInt32();
            for (var i = 0; i < n; i++)
            {
                var proj = r.ReadString();
                var e = new StdEntry { Std = r.ReadString(), Ver = r.ReadString(), ProjTicks = r.ReadInt64() };
                e.Hashes = new int[r.ReadInt32()];
                for (var k = 0; k < e.Hashes.Length; k++) e.Hashes[k] = r.ReadInt32();
                m.Files[proj] = e;
            }
            var miss = r.ReadInt32();
            for (var i = 0; i < miss; i++) m.Missing.Add(r.ReadString());
            return m;   // BuiltAt = MinValue → lần tra đầu của phiên cập nhật nền (chỉ làm lại file .f đổi), trong lúc đó vẫn tra được bản trên đĩa
        }
        catch { return null; }
    }

    private static void SaveStd(string key, StdMirror m)
    {
        try
        {
            var path = StdPath(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            lock (m)
                using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
                {
                    w.Write(StdFormatVersion); w.Write(key);
                    w.Write(m.Files.Count);
                    foreach (var (proj, e) in m.Files)
                    {
                        w.Write(proj); w.Write(e.Std); w.Write(e.Ver); w.Write(e.ProjTicks); w.Write(e.Hashes.Length);
                        foreach (var h in e.Hashes) w.Write(h);
                    }
                    w.Write(m.Missing.Count);
                    foreach (var f in m.Missing) w.Write(f);
                }
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* không lưu được thì lần sau dựng lại */ }
    }
}
