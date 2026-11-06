using System.Text.Json;

namespace Bcode.App.Services;

/// <summary>
/// "Giếng tri thức chung" theo dự án — mục 2 của hướng Cây thế giới: MỘT chỉ mục file source dùng chung cho nhiều tính năng (Ctrl+P tìm file,
/// Gen Update gợi ý tên, sau này IntelliSense / sơ đồ phụ thuộc) thay vì mỗi nơi tự quét lại thư mục Controllers qua mạng.
///
/// Lưu ở <c>%AppData%\Bcode\project-index\&lt;dự án&gt;.json</c>: danh sách file dưới <c>App_Data\Controllers</c> + <c>Main</c> (tên + đường dẫn tương đối).
/// Phục vụ NGAY từ cache (mở Ctrl+P không phải chờ), rồi refresh NỀN khi cache cũ (quá <see cref="StaleAfter"/>) — "cập nhật dần", chỉ ĐỌC, không đụng database.
/// Quét = liệt kê tên file (một lần duyệt cây), không đọc nội dung; nội dung/băm từ vẫn do SourceIndexService lo riêng.
/// </summary>
public sealed class ProjectIndexService
{
    public sealed record FileItem(string Name, string Rel)
    {
        public string FullPath(string sourceRoot) => Path.Combine(sourceRoot, Rel);
    }

    private sealed class Index
    {
        public DateTime RefreshedAt { get; set; }
        public string SourceRoot { get; set; } = "";
        public List<FileItem> Files { get; set; } = new();
    }

    /// <summary>Hình lưu ra đĩa: chỉ giữ đường dẫn tương đối (tên file suy ra lúc đọc) để file nhỏ; nén gzip.</summary>
    private sealed class Disk
    {
        public DateTime RefreshedAt { get; set; }
        public string SourceRoot { get; set; } = "";
        public List<string> Rels { get; set; } = new();
    }

    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    private static readonly string[] Roots = { @"App_Data\Controllers", "Main" };

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object _gate = new();
    private readonly Dictionary<string, Index> _mem = new(StringComparer.OrdinalIgnoreCase);   // dự án -> chỉ mục đang giữ trong RAM
    private readonly HashSet<string> _refreshing = new(StringComparer.OrdinalIgnoreCase);

    private static string SafeName(string project)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var ch in project) sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        var s = sb.ToString();
        return s.Length == 0 ? "default" : s.Length > 80 ? s[..80] : s;
    }

    private static string PathFor(string project) => Path.Combine(BcodePaths.AppData, "Bcode", "project-index", SafeName(project) + ".json.gz");

    private Index Load(string project)
    {
        lock (_gate) if (_mem.TryGetValue(project, out var cached)) return cached;
        Index idx = new();
        try
        {
            var p = PathFor(project);
            if (File.Exists(p))
            {
                using var fs = File.OpenRead(p);
                using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress);
                var disk = JsonSerializer.Deserialize<Disk>(gz, JsonOpts);
                if (disk is not null)
                    idx = new Index { RefreshedAt = disk.RefreshedAt, SourceRoot = disk.SourceRoot,
                        Files = disk.Rels.Select(r => new FileItem(Path.GetFileName(r), r)).ToList() };
            }
        }
        catch { idx = new Index(); }
        lock (_gate) { _mem[project] = idx; return idx; }
    }

    private void Save(string project, Index idx)
    {
        try
        {
            var p = PathFor(project);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var disk = new Disk { RefreshedAt = idx.RefreshedAt, SourceRoot = idx.SourceRoot, Rels = idx.Files.Select(f => f.Rel).ToList() };
            using var fs = File.Create(p);
            using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal);
            JsonSerializer.Serialize(gz, disk, JsonOpts);
        }
        catch { /* không ghi được chỉ mất cache lần sau, không ảnh hưởng tính năng */ }
    }

    /// <summary>Danh sách file hiện có trong chỉ mục (từ cache). Rỗng nếu chưa từng quét — gọi <see cref="EnsureFreshAsync"/> để nạp/refresh nền.</summary>
    public IReadOnlyList<FileItem> Files(string project) => Load(project).Files;

    public bool IsStale(string project) => DateTime.Now - Load(project).RefreshedAt > StaleAfter;

    /// <summary>Tìm file theo tên (mọi từ khoá phải khớp, không phân biệt hoa thường), ưu tiên khớp từ đầu tên; tối đa <paramref name="limit"/>.</summary>
    public List<FileItem> Search(string project, string query, int limit = 50)
    {
        var tokens = (query ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var files = Files(project);
        IEnumerable<FileItem> q = tokens.Length == 0 ? files
            : files.Where(f => { var n = (f.Name + " " + f.Rel).ToLowerInvariant(); return tokens.All(n.Contains); });
        return q.OrderByDescending(f => tokens.Length > 0 && f.Name.StartsWith(tokens[0], StringComparison.OrdinalIgnoreCase))
                .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
    }

    /// <summary>Nạp từ đĩa; nếu chưa có hoặc đã cũ (và chưa có ai đang refresh) thì quét lại NỀN, xong thì gọi <paramref name="onUpdated"/> (trên luồng nền).
    /// Trả về true nếu đã kích hoạt refresh nền.</summary>
    public bool EnsureFreshAsync(string project, string sourceRoot, Action? onUpdated = null)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot)) return false;
        var idx = Load(project);
        var needRefresh = idx.Files.Count == 0 || IsStale(project) || !string.Equals(idx.SourceRoot, sourceRoot, StringComparison.OrdinalIgnoreCase);
        if (!needRefresh) return false;
        lock (_gate) { if (!_refreshing.Add(project)) return false; }   // đã có 1 lần refresh đang chạy
        _ = Task.Run(() =>
        {
            try
            {
                var fresh = Scan(sourceRoot);
                lock (_gate) _mem[project] = fresh;
                Save(project, fresh);
                onUpdated?.Invoke();
            }
            catch { /* mất mạng / UNC lỗi: giữ cache cũ */ }
            finally { lock (_gate) _refreshing.Remove(project); }
        });
        return true;
    }

    /// <summary>Quét ĐỒNG BỘ ngay, lưu cache, trả số file (dùng khi cần chắc chắn có dữ liệu, hoặc để test).</summary>
    public int RefreshNow(string project, string sourceRoot)
    {
        var fresh = Scan(sourceRoot);
        lock (_gate) _mem[project] = fresh;
        Save(project, fresh);
        return fresh.Files.Count;
    }

    private static Index Scan(string sourceRoot)
    {
        var idx = new Index { SourceRoot = sourceRoot, RefreshedAt = DateTime.Now };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in Roots)
        {
            var dir = Path.Combine(sourceRoot, rel);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var r = Path.GetRelativePath(sourceRoot, file);
                if (seen.Add(r)) idx.Files.Add(new FileItem(Path.GetFileName(file), r));
            }
        }
        idx.Files.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return idx;
    }
}
