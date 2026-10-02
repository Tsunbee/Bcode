using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>Cách File Lookup (chế độ menu) tìm file liên quan của 1 menu.
/// Off = cách cũ (đọc lại mọi file qua UNC mỗi lần bấm menu). On = dùng cache kết quả phân tích từng file.</summary>
public enum FileLookupCacheMode { Off, On }

/// <summary>Một lần dựng cây menu: nhớ nội dung + "dấu vân tay" (kích thước:mtime) của file đã đọc để các file khác
/// trong cùng lần dựng dùng chung, và đếm hit/miss của cache để hiện lên thanh trạng thái.</summary>
internal sealed class ReadSession
{
    private readonly ConcurrentDictionary<string, (string Stamp, string? Text, bool Failed)> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _stamps = new(StringComparer.OrdinalIgnoreCase);
    private int _hits, _misses;

    public ReadSession(FileParseCache cache) => Cache = cache;

    /// <summary>Cache của dự án (source root) đang dựng cây.</summary>
    public FileParseCache Cache { get; }

    public int Hits => _hits;
    public int Misses => _misses;
    public void CountHit() => Interlocked.Increment(ref _hits);
    public void CountMiss() => Interlocked.Increment(ref _misses);

    /// <summary>Đọc file; dấu vân tay lấy TRƯỚC khi đọc để nếu file đổi giữa chừng thì lần sau chỉ tính lại (không bao giờ dùng nhầm kết quả cũ).</summary>
    public (string Stamp, string? Text, bool Failed) Load(string path) => _files.GetOrAdd(path, static p =>
    {
        var stamp = FileParseCache.StatStamp(p);
        if (stamp == FileParseCache.Missing) return (stamp, null, false);
        if (stamp == FileParseCache.Unknown) return (stamp, null, true);
        try { return (stamp, File.ReadAllText(p), false); }
        catch (Exception) { return (stamp, null, true); } // khoá/rớt share — KHÔNG được cache kết quả này
    });

    /// <summary>Dấu vân tay hiện tại của file, stat tối đa 1 lần cho mỗi file trong 1 lần dựng.</summary>
    public string Stamp(string path) =>
        _files.TryGetValue(path, out var f) ? f.Stamp : _stamps.GetOrAdd(path, FileParseCache.StatStamp);
}

/// <summary>Đọc file qua <see cref="ReadSession"/> và ghi lại mọi file đã đọc (kể cả file không tồn tại) làm phụ thuộc của 1 kết quả.</summary>
internal sealed class DepTracker
{
    private readonly ReadSession _session;
    public Dictionary<string, string> Deps { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Unreliable { get; private set; }

    public DepTracker(ReadSession session) => _session = session;

    public string? Read(string path)
    {
        var r = _session.Load(path);
        Deps[path] = r.Stamp;
        if (r.Failed) Unreliable = true;
        return r.Text;
    }
}

/// <summary>
/// Cache kết quả phân tích từng file (tên controller liên quan, tên template báo cáo, include, lỗi entity) của MỘT dự án (một source
/// root) — phần tốn thời gian nhất khi bấm 1 menu, vì phải đọc lại nhiều file .xml/.f/.ent qua UNC. Mỗi kết quả nhớ KÈM danh sách mọi
/// file nó đã đọc và "dấu vân tay" (kích thước:mtime) của từng file đó; lần sau chỉ stat các file ấy — khớp hết thì dùng lại, lệch bất kỳ
/// file nào (sửa, thêm, xoá, kể cả file include) thì phân tích lại đúng mục đó.
///
/// Lưu mỗi dự án 1 file riêng %AppData%\Bcode\FileLookupCache\&lt;Tên&gt;_&lt;Phiên bản&gt;.json (tên lấy từ 2 thư mục cuối của source root),
/// đường dẫn trong file là đường dẫn TƯƠNG ĐỐI so với source root nên đổi IP/ổ đĩa của server vẫn dùng lại được. Mở nhiều Bcode cùng lúc
/// được: lúc ghi khoá bằng mutex, đọc lại file trên đĩa rồi gộp mục của mình vào (không đè mất mục của app kia). Mục lâu không dùng
/// (<see cref="PruneDays"/> ngày) tự bị dọn khi nạp/ghi. Cache chỉ để tăng tốc: hỏng/sai version thì bỏ, nút Load trên File Lookup xoá.
/// </summary>
internal sealed class FileParseCache
{
    /// <summary>Tăng số này mỗi khi sửa logic phân tích (regex, quy tắc entity...) hoặc đổi định dạng file để bỏ cache cũ.</summary>
    private const int SchemaVersion = 2;
    private const int MaxEntries = 100_000;
    public const int PruneDays = 60;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(3);

    internal const string Missing = "-";
    internal const string Unknown = "?";

    private static readonly ConcurrentDictionary<string, FileParseCache> Projects = new(StringComparer.OrdinalIgnoreCase);

    static FileParseCache()
    {
        // Thoát app: ghi nốt phần chưa kịp ghi (đang chờ SaveDelay).
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            foreach (var p in Projects.Values)
                try { p.SaveNow(1000); } catch (Exception) { /* thoát rồi, bỏ qua */ }
        };
        DeleteLegacyCacheFile();
    }

    /// <summary>Cache của dự án có source root này (tạo + nạp từ đĩa lần đầu gọi).</summary>
    public static FileParseCache For(string sourceRoot)
    {
        var root = NormalizeRoot(sourceRoot);
        return Projects.GetOrAdd(root, r => new FileParseCache(r));
    }

    /// <summary>Xoá cache của dự án này (nút Load).</summary>
    public static void Reset(string sourceRoot) => For(sourceRoot).Clear();

    private sealed class Entry
    {
        public KeyValuePair<string, string>[] Deps = Array.Empty<KeyValuePair<string, string>>();
        public string[] Result = Array.Empty<string>();
        public int LastUsedDay;
    }

    private readonly string _root;
    private readonly string _fileKey;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _touched = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _dirty;
    private volatile bool _clearPending;
    private int _scheduled;

    private FileParseCache(string root)
    {
        _root = root;
        _fileKey = BuildFileKey(root);
        Load();
    }

    private string CachePath => Path.Combine(CacheDir, _fileKey + ".json");
    private static string CacheDir => Path.Combine(BcodePaths.AppData, "Bcode", "FileLookupCache");
    private static int Today() => (int)(DateTime.UtcNow - DateTime.UnixEpoch).TotalDays;

    internal static string StatStamp(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" : Missing;
        }
        catch (Exception) { return Unknown; }
    }

    private static string NormalizeRoot(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch (Exception) { return path.Trim().TrimEnd('\\', '/'); }
    }

    /// <summary>Tên file cache: 2 thư mục cuối của source root (vd ...\KOG\FBISP24 → KOG_FBISP24). Cùng tên ở 2 server khác nhau dùng chung
    /// 1 file cũng an toàn vì mọi mục đều được đối chiếu lại bằng mtime/size.</summary>
    private static string BuildFileKey(string root)
    {
        var parts = root.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        var name = string.Join("_", parts.Skip(Math.Max(0, parts.Length - 2)));
        name = Regex.Replace(name, @"[^A-Za-z0-9._-]+", "_").Trim('_');
        return name.Length == 0 ? "default" : name;
    }

    private static void DeleteLegacyCacheFile()
    {
        try
        {
            var legacy = Path.Combine(BcodePaths.AppData, "Bcode", "filelookup-cache.json"); // bản gộp mọi dự án, đường dẫn tuyệt đối
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        catch (Exception) { /* không xoá được thì thôi, không ai đọc file này nữa */ }
    }

    // ---- đường dẫn tương đối ----

    private string ToRel(string path) =>
        path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? path[(_root.Length + 1)..] : path;

    private string ToAbs(string path) => Path.IsPathRooted(path) ? path : Path.Combine(_root, path);

    /// <summary>Kết quả kiểu "inc" là danh sách đường dẫn nên cũng lưu tương đối; các loại khác là tên/thông báo, giữ nguyên.</summary>
    private static bool ResultIsPaths(string kind) => kind == "inc";

    private static (string Kind, string Path) SplitKey(string key)
    {
        var i = key.IndexOf('|');
        return (key[..i], key[(i + 1)..]);
    }

    // ---- dùng cache ----

    /// <summary>Xoá toàn bộ cache của dự án (nút Load); lần ghi kế tiếp thay hẳn file trên đĩa thay vì gộp.</summary>
    public void Clear()
    {
        _entries.Clear();
        _touched.Clear();
        _clearPending = true;
        _dirty = true;
    }

    /// <summary>Lấy kết quả đã cache nếu mọi file phụ thuộc còn nguyên; không thì chạy <paramref name="compute"/> (đọc file qua hàm được đưa vào) rồi lưu lại.</summary>
    public string[] GetOrCompute(string kind, string mainFile, ReadSession session, Func<Func<string, string?>, IEnumerable<string>> compute)
    {
        var key = kind + "|" + mainFile;
        if (_entries.TryGetValue(key, out var entry))
        {
            if (IsValid(entry, session))
            {
                session.CountHit();
                var today = Today();
                if (entry.LastUsedDay != today) { entry.LastUsedDay = today; _touched[key] = 0; _dirty = true; }
                return entry.Result;
            }
            _entries.TryRemove(key, out _);
        }

        session.CountMiss();
        var tracker = new DepTracker(session);
        var result = compute(tracker.Read).ToArray();
        if (!tracker.Unreliable && tracker.Deps.Count > 0 && _entries.Count < MaxEntries)
        {
            _entries[key] = new Entry { Deps = tracker.Deps.ToArray(), Result = result, LastUsedDay = Today() };
            _touched[key] = 0;
            _dirty = true; // chỉ bật ở đây (mục mới/đổi) và khi đổi ngày dùng — toàn hit thì không ghi gì
        }
        return result;
    }

    private static bool IsValid(Entry entry, ReadSession session)
    {
        foreach (var (path, stamp) in entry.Deps)
        {
            var current = session.Stamp(path);
            if (current == Unknown || current != stamp) return false;
        }
        return true;
    }

    // ---- định dạng file ----

    private sealed class DiskFile
    {
        public int Version { get; set; }
        public string Root { get; set; } = "";
        public List<string> Paths { get; set; } = new();
        public List<DiskEntry> Entries { get; set; } = new();
    }

    private sealed class DiskEntry
    {
        public string K { get; set; } = "";            // "kind|đường dẫn tương đối của file chính"
        public List<int> D { get; set; } = new();      // chỉ số vào Paths (file phụ thuộc)
        public List<string> S { get; set; } = new();   // dấu vân tay tương ứng
        public List<string> R { get; set; } = new();   // kết quả
        public int U { get; set; }                     // ngày dùng gần nhất (số ngày từ 1970)
    }

    /// <summary>Dạng giải mã (đường dẫn tương đối, chưa nén bảng path) — dùng khi gộp với file trên đĩa.</summary>
    private sealed record Decoded(string Key, string[] Deps, string[] Stamps, string[] Result, int LastUsedDay);

    private List<Decoded>? ReadDisk()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var file = JsonSerializer.Deserialize<DiskFile>(File.ReadAllText(CachePath));
            if (file is null || file.Version != SchemaVersion) return null;
            var list = new List<Decoded>(file.Entries.Count);
            foreach (var e in file.Entries)
            {
                if (e.D.Count != e.S.Count || e.D.Any(i => i < 0 || i >= file.Paths.Count)) continue;
                list.Add(new Decoded(e.K, e.D.Select(i => file.Paths[i]).ToArray(), e.S.ToArray(), e.R.ToArray(), e.U));
            }
            return list;
        }
        catch (Exception) { return null; } // hỏng / đang bị app khác thay file — coi như chưa có
    }

    private void Load()
    {
        var today = Today();
        if (ReadDisk() is not { } disk) return;
        foreach (var d in disk)
        {
            if (today - d.LastUsedDay > PruneDays) continue;
            var (kind, rel) = SplitKey(d.Key);
            var deps = new KeyValuePair<string, string>[d.Deps.Length];
            for (var i = 0; i < deps.Length; i++) deps[i] = new(ToAbs(d.Deps[i]), d.Stamps[i]);
            var result = ResultIsPaths(kind) ? d.Result.Select(ToAbs).ToArray() : d.Result;
            _entries[kind + "|" + ToAbs(rel)] = new Entry { Deps = deps, Result = result, LastUsedDay = d.LastUsedDay };
        }
    }

    // ---- ghi ----

    /// <summary>Hẹn ghi cache xuống đĩa ở luồng nền sau vài giây nếu có thay đổi (gộp nhiều lần bấm menu thành 1 lần ghi, không chặn UI).</summary>
    public void SaveInBackground()
    {
        if (!_dirty || Interlocked.Exchange(ref _scheduled, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SaveDelay);
                SaveNow(3000);
            }
            catch (Exception) { /* thử lại ở lần sau */ }
            finally { Interlocked.Exchange(ref _scheduled, 0); }
        });
    }

    private void SaveNow(int mutexWaitMs)
    {
        if (!_dirty) return;

        // Mutex đặt tên (cùng máy, nhiều Bcode): chỉ 1 app ghi file của dự án này tại 1 thời điểm.
        using var mutex = new Mutex(false, "Bcode.FileLookupCache." + _fileKey);
        var got = false;
        try { got = mutex.WaitOne(mutexWaitMs); }
        catch (AbandonedMutexException) { got = true; }
        if (!got) return; // app khác đang ghi — _dirty còn nguyên, lần sau ghi

        try
        {
            _dirty = false;
            var today = Today();
            var merged = new Dictionary<string, Decoded>(StringComparer.OrdinalIgnoreCase);

            // 1) Mục đang có trên đĩa (kể cả do app khác ghi) — giữ lại, trừ khi vừa bấm Load hoặc quá hạn.
            if (!_clearPending && ReadDisk() is { } disk)
                foreach (var d in disk)
                    if (today - d.LastUsedDay <= PruneDays) merged[d.Key] = d;
            _clearPending = false;

            // 2) Mục của mình: mục vừa thêm/đổi ghi đè bản trên đĩa; mục không đổi chỉ thêm nếu đĩa chưa có.
            var touched = _touched.Keys.ToArray();
            foreach (var (key, entry) in _entries)
            {
                if (today - entry.LastUsedDay > PruneDays) continue;
                var (kind, path) = SplitKey(key);
                var relKey = kind + "|" + ToRel(path);
                if (merged.ContainsKey(relKey) && !_touched.ContainsKey(key)) continue;
                merged[relKey] = new Decoded(relKey,
                    entry.Deps.Select(d => ToRel(d.Key)).ToArray(),
                    entry.Deps.Select(d => d.Value).ToArray(),
                    ResultIsPaths(kind) ? entry.Result.Select(ToRel).ToArray() : entry.Result,
                    entry.LastUsedDay);
            }
            foreach (var k in touched) _touched.TryRemove(k, out _);

            var file = new DiskFile { Version = SchemaVersion, Root = _root };
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in merged.Values)
            {
                var em = new DiskEntry { K = d.Key, R = d.Result.ToList(), U = d.LastUsedDay };
                for (var i = 0; i < d.Deps.Length; i++)
                {
                    if (!index.TryGetValue(d.Deps[i], out var idx)) { idx = file.Paths.Count; file.Paths.Add(d.Deps[i]); index[d.Deps[i]] = idx; }
                    em.D.Add(idx);
                    em.S.Add(d.Stamps[i]);
                }
                file.Entries.Add(em);
            }

            Directory.CreateDirectory(CacheDir);
            var tmp = CachePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(file));
            File.Move(tmp, CachePath, overwrite: true);
        }
        catch (Exception) { _dirty = true; /* ghi không được (khoá/quyền) — thử lại lần sau */ }
        finally { mutex.ReleaseMutex(); }
    }
}
