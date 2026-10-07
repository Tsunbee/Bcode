using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Chỉ mục "file nào nhắc tới tên gì" cho các thư mục controller trong source (<c>App_Data\Controllers\{Dir, Filter, Grid, Include, Options, Report}</c>) —
/// dùng cho "Ai đang dùng object này?". Quét cả thư mục mỗi lần (đọc hàng nghìn file qua ổ mạng) quá chậm, nên:
/// (1) lần đầu mỗi file chỉ đọc MỘT lần, lấy ra tập "băm" của mọi từ trong file (4 byte / từ, không lưu nội dung) rồi lưu ra <c>%AppData%\Bcode\source-index\</c>;
/// (2) các lần sau chỉ liệt kê thư mục (không đọc nội dung) và đọc lại ĐÚNG những file có ngày sửa / kích thước đổi;
/// (3) tra một tên = tìm trong chỉ mục (tức thì) rồi chỉ mở các file ứng viên để lấy dòng trích và xác nhận đúng ranh giới tên.
/// Việc liệt kê thư mục chỉ làm lại sau <see cref="RefreshEvery"/> (hoặc khi người dùng bấm "Quét lại").
/// </summary>
public sealed class SourceIndexService
{
    public static readonly string[] Folders = { "Dir", "Filter", "Grid", "Include", "Options", "Report" };
    private static readonly string[] Extensions = { ".f", ".xml", ".aspx", ".ascx", ".vb", ".cs", ".js", ".htm", ".html", ".txt", ".config", ".sql" };
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(3);
    private const int FormatVersion = 1, MaxSnippets = 4, SnippetWidth = 220, MaxHits = 400;

    private sealed class Entry { public long Ticks, Length; public int[] Hashes = Array.Empty<int>(); }
    private sealed class Index { public Dictionary<string, Entry> Files = new(StringComparer.OrdinalIgnoreCase); public DateTime RefreshedAt; }

    private static readonly ConcurrentDictionary<string, Index> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Word = new(@"[\w$#@]+", RegexOptions.Compiled);

    public sealed record Result(List<SourceUsage> Files, int IndexedFiles, int ReadFiles, bool FromCache, TimeSpan Elapsed, string? Warning);

    public static string ControllersDir(string sourceRoot) => Path.Combine(sourceRoot, "App_Data", "Controllers");

    private static string IndexPath(string root)
    {
        var safe = Regex.Replace(root.TrimEnd('\\', '/'), @"[^\w.\-]+", "_");
        if (safe.Length > 120) safe = safe[^120..];
        return Path.Combine(BcodePaths.AppData, "Bcode", "source-index", safe + ".bin");
    }

    private static int Hash(string lowerToken)
    {
        unchecked
        {
            var h = (int)2166136261;
            foreach (var c in lowerToken) h = (h ^ c) * 16777619;
            return h;
        }
    }

    private static int[] HashesOf(string text)
    {
        var set = new HashSet<int>();
        foreach (Match m in Word.Matches(text))
            if (m.Length >= 3) set.Add(Hash(m.Value.ToLowerInvariant()));
        var arr = set.ToArray();
        Array.Sort(arr);
        return arr;
    }

    /// <param name="force">true = bỏ qua "vừa liệt kê gần đây", liệt kê lại thư mục ngay (nút Quét lại).</param>
    public Task<Result> FindAsync(string sourceRoot, string name, bool force, Action<string> progress, CancellationToken ct) =>
        Task.Run(() => Find(sourceRoot, name, force, progress, ct), ct);

    private Result Find(string sourceRoot, string name, bool force, Action<string> progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var controllers = ControllersDir(sourceRoot);
        if (!Directory.Exists(controllers))
            return new Result(new(), 0, 0, false, sw.Elapsed, "Không thấy thư mục " + controllers);

        var index = GetIndex(controllers, Folders, out var key);
        lock (index) return FindLocked(index, controllers, Folders, key, name, force, progress, ct, sw);
    }

    private Result FindLocked(Index index, string controllers, string[]? folders, string key, string name, bool force, Action<string> progress, CancellationToken ct, System.Diagnostics.Stopwatch sw)
    {
        var read = 0;
        var fromCache = !force && DateTime.Now - index.RefreshedAt < RefreshEvery && index.Files.Count > 0;
        if (!fromCache) read = Refresh(controllers, folders, key, index, progress, ct);

        progress("Đang tra chỉ mục…");
        var h = Hash(name.ToLowerInvariant());
        var pattern = UsageSearchService.NamePattern(name);
        var hits = new List<SourceUsage>();
        var total = 0;
        foreach (var (path, e) in index.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (Array.BinarySearch(e.Hashes, h) < 0) continue;            // không có từ này trong file (băm trùng thì bị loại ở bước đọc dưới)
            var (count, snippets) = Scan(path, pattern);
            if (count == 0) continue;
            hits.Add(new SourceUsage(path, count, snippets));
            if ((total += count) >= MaxHits) break;
        }
        return new Result(hits, index.Files.Count, read, fromCache, sw.Elapsed, null);
    }

    private static Index GetIndex(string dir, string[]? folders, out string key)
    {
        key = dir + (folders is null ? "|all" : "|ctl");
        var k = key;
        return Memory.GetOrAdd(k, _ => Load(k) ?? new Index());
    }

    /// <summary>Phạm vi tra cứu cho File Reference: gốc là App_Data (hoặc chính thư mục Controllers) → chỉ 6 thư mục controller; gốc khác → toàn bộ thư mục đó.</summary>
    public static (string Dir, string[]? Folders) ResolveScope(string root)
    {
        root = root.Trim().TrimEnd('\\', '/');
        var ctl = Path.Combine(root, "Controllers");
        if (Directory.Exists(ctl)) return (ctl, Folders);
        return string.Equals(Path.GetFileName(root), "Controllers", StringComparison.OrdinalIgnoreCase) ? (root, Folders) : (root, null);
    }

    /// <summary>Tra "mọi dòng chứa từ nguyên vẹn <paramref name="word"/>" bằng chỉ mục (cho File Reference). <paramref name="scope"/> trả về mô tả phạm vi đã dùng.</summary>
    public Task<(List<FileReferenceMatch> Matches, string Scope, int ReadFiles, bool FromCache)> FindLinesAsync(string root, string word, bool force, int max, Action<string> progress, CancellationToken ct) =>
        Task.Run(() =>
        {
            var (dir, folders) = ResolveScope(root);
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException("Không truy cập được " + dir);
            var index = GetIndex(dir, folders, out var key);
            lock (index)
            {
                var fromCache = !force && DateTime.Now - index.RefreshedAt < RefreshEvery && index.Files.Count > 0;
                var read = fromCache ? 0 : Refresh(dir, folders, key, index, progress, ct);
                progress("Đang tra chỉ mục…");
                var h = Hash(word.ToLowerInvariant());
                var pattern = UsageSearchService.NamePattern(word);
                var matches = new List<FileReferenceMatch>();
                foreach (var (path, e) in index.Files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Array.BinarySearch(e.Hashes, h) < 0) continue;
                    try
                    {
                        var n = 0;
                        foreach (var line in File.ReadLines(path))
                        {
                            n++;
                            if (!pattern.IsMatch(line)) continue;
                            matches.Add(new FileReferenceMatch(path, n, line.Trim()));
                            if (matches.Count >= max) return (matches, folders is null ? dir : "App_Data\\Controllers: " + string.Join(", ", folders), read, fromCache);
                        }
                    }
                    catch (IOException) { /* file khoá / mất */ }
                }
                return (matches, folders is null ? dir : "App_Data\\Controllers: " + string.Join(", ", folders), read, fromCache);
            }
        }, ct);

    private static (int Count, List<UsageLine> Snippets) Scan(string path, Regex pattern)
    {
        var snippets = new List<UsageLine>();
        var count = 0;
        try
        {
            var n = 0;
            foreach (var line in File.ReadLines(path))
            {
                n++;
                if (!pattern.IsMatch(line)) continue;
                count++;
                if (snippets.Count < MaxSnippets)
                {
                    var t = line.Trim();
                    snippets.Add(new UsageLine(n, t.Length <= SnippetWidth ? t : t[..SnippetWidth] + "…"));
                }
            }
        }
        catch { /* file đang bị khoá / bị xoá giữa chừng — bỏ qua */ }
        return (count, snippets);
    }

    /// <summary>Liệt kê thư mục (rẻ), đọc lại các file mới / đổi, bỏ file đã mất. Trả số file đã đọc nội dung.</summary>
    private int Refresh(string controllers, string[]? folders, string key, Index index, Action<string> progress, CancellationToken ct)
    {
        progress("Đang liệt kê file trong App_Data\\Controllers…");
        var current = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders ?? new string?[] { null })
        {
            var dir = folder is null ? controllers : Path.Combine(controllers, folder);
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var fi in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    if (Extensions.Contains(fi.Extension, StringComparer.OrdinalIgnoreCase)) current[fi.FullName] = fi;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* thư mục con không truy cập được — bỏ qua */ }
        }

        var changed = current.Where(kv => !index.Files.TryGetValue(kv.Key, out var e) || e.Ticks != kv.Value.LastWriteTimeUtc.Ticks || e.Length != kv.Value.Length).Select(kv => kv.Value).ToList();
        var removed = index.Files.Keys.Where(k => !current.ContainsKey(k)).ToList();

        var done = 0;
        var fresh = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        if (changed.Count > 0)
        {
            Parallel.ForEach(changed, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, fi =>
            {
                try { fresh[fi.FullName] = new Entry { Ticks = fi.LastWriteTimeUtc.Ticks, Length = fi.Length, Hashes = HashesOf(File.ReadAllText(fi.FullName)) }; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* khoá / không đọc được — lần sau thử lại */ }
                var d = Interlocked.Increment(ref done);
                if (d % 100 == 0) progress($"Đang lập chỉ mục lần đầu / cập nhật: {d}/{changed.Count} file…");
            });
        }

        foreach (var kv in fresh) index.Files[kv.Key] = kv.Value;
        foreach (var k in removed) index.Files.Remove(k);
        index.RefreshedAt = DateTime.Now;
        if (fresh.Count > 0 || removed.Count > 0) Save(key, index);
        return changed.Count;
    }

    // ---- lưu / nạp chỉ mục ----

    private static Index? Load(string controllers)
    {
        try
        {
            var path = IndexPath(controllers);
            if (!File.Exists(path)) return null;
            using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            if (r.ReadInt32() != FormatVersion) return null;
            var idx = new Index();
            var n = r.ReadInt32();
            for (var i = 0; i < n; i++)
            {
                var file = r.ReadString();
                var e = new Entry { Ticks = r.ReadInt64(), Length = r.ReadInt64() };
                e.Hashes = new int[r.ReadInt32()];
                for (var k = 0; k < e.Hashes.Length; k++) e.Hashes[k] = r.ReadInt32();
                idx.Files[file] = e;
            }
            return idx;     // RefreshedAt = mặc định (rất cũ) → lần tra đầu của phiên luôn liệt kê lại thư mục để bắt file đã đổi
        }
        catch { return null; }
    }

    private static void Save(string controllers, Index index)
    {
        try
        {
            var path = IndexPath(controllers);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
            {
                w.Write(FormatVersion);
                w.Write(index.Files.Count);
                foreach (var (file, e) in index.Files)
                {
                    w.Write(file); w.Write(e.Ticks); w.Write(e.Length); w.Write(e.Hashes.Length);
                    foreach (var h in e.Hashes) w.Write(h);
                }
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* không lưu được thì lần sau lập lại */ }
    }
}
