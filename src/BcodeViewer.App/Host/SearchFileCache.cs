using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;

namespace BcodeViewer.App.Host;

/// <summary>
/// Nhớ NỘI DUNG file cho Tìm trong project / tra entity cả project (<see cref="WorkspaceSearchService"/>).
///
/// Đo trên ổ mạng UNC của khách (21/10/2026): 1 App_Data ~11.000 file / 65 MB, liệt kê cả cây kèm kích thước + giờ sửa mất ~0,7 s,
/// nhưng đọc lần lượt từng file mất ~34 ms/file → tìm 1 từ không có mất 378 s. Thời gian nằm ở độ trễ mỗi lần đọc, không ở dung lượng.
/// Nên: file nào kích thước + giờ sửa (lấy sẵn từ lúc liệt kê thư mục, không tốn thêm lượt gọi mạng) trùng lần đọc trước thì dùng bản đã nhớ,
/// chỉ đọc lại file mới / đã đổi. Giữ dạng byte gốc (không đổi encoding) nên giải mã y như File.ReadAllText(file, UTF8) trước đây (theo BOM).
///
/// Lưu xuống đĩa theo từng thư mục gốc (%LocalAppData%\BcodeViewer\searchcache\&lt;hash&gt;.bin, nén gzip) để lần tìm đầu sau khi mở lại Viewer
/// cũng nhanh. Hỏng / khác định dạng thì bỏ qua (đọc lại từ mạng). Trong RAM giới hạn <see cref="MaxMemoryBytes"/>: vượt thì bỏ các gốc khác.
/// </summary>
internal static class SearchFileCache
{
    private sealed record Entry(long Length, long WriteTicks, byte[] Bytes);

    private const int FormatVersion = 1;
    private const long MaxMemoryBytes = 400L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, Entry> Files = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> LoadedRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, bool> DirtyRoots = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SaveLock = new();
    private static long _bytes;

    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BcodeViewer", "searchcache");

    /// <summary>Gọi trước khi tìm trong <paramref name="root"/>: nạp bản đã lưu trên đĩa (1 lần mỗi gốc mỗi phiên).</summary>
    public static void EnsureLoaded(string root)
    {
        root = Norm(root);
        if (!LoadedRoots.TryAdd(root, true)) return;
        TrimMemory(root);
        try
        {
            var path = FileFor(root);
            if (!File.Exists(path)) return;
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var r = new BinaryReader(gz, Encoding.UTF8);
            if (r.ReadInt32() != FormatVersion || !string.Equals(r.ReadString(), root, StringComparison.OrdinalIgnoreCase)) return;
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var file = r.ReadString();
                var len = r.ReadInt64();
                var ticks = r.ReadInt64();
                var bytes = r.ReadBytes(r.ReadInt32());
                if (Files.TryAdd(file, new Entry(len, ticks, bytes))) Interlocked.Add(ref _bytes, bytes.Length);
            }
        }
        catch { /* file nhớ hỏng / đang ghi dở: bỏ qua, đọc lại từ mạng */ }
    }

    /// <summary>Nội dung file như File.ReadAllText(file, UTF8): lấy bản đã nhớ nếu kích thước + giờ sửa không đổi, không thì đọc từ đĩa và nhớ lại.
    /// <paramref name="info"/> lấy từ lúc liệt kê thư mục (đã có Length / LastWriteTimeUtc, không gọi mạng thêm).</summary>
    public static string ReadText(FileInfo info, string root)
    {
        var ticks = info.LastWriteTimeUtc.Ticks;
        if (Files.TryGetValue(info.FullName, out var e) && e.Length == info.Length && e.WriteTicks == ticks) return Decode(e.Bytes);
        var bytes = File.ReadAllBytes(info.FullName);
        var fresh = new Entry(bytes.LongLength, ticks, bytes);
        Files.AddOrUpdate(info.FullName, fresh, (_, old) => { Interlocked.Add(ref _bytes, -old.Bytes.Length); return fresh; });
        Interlocked.Add(ref _bytes, bytes.Length);
        DirtyRoots[Norm(root)] = true;
        return Decode(bytes);
    }

    /// <summary>Sau 1 lượt tìm: có file mới đọc thì ghi bản nhớ của gốc này xuống đĩa (chạy nền, không giữ chân kết quả).</summary>
    public static void SaveLater(string root)
    {
        root = Norm(root);
        if (!DirtyRoots.TryRemove(root, out _)) return;
        Task.Run(() =>
        {
            lock (SaveLock)
            {
                try
                {
                    Directory.CreateDirectory(Folder);
                    var prefix = root.EndsWith('\\') ? root : root + "\\";
                    var items = Files.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                    var path = FileFor(root);
                    var tmp = path + ".tmp";
                    using (var fs = File.Create(tmp))
                    using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                    using (var w = new BinaryWriter(gz, Encoding.UTF8))
                    {
                        w.Write(FormatVersion);
                        w.Write(root);
                        w.Write(items.Count);
                        foreach (var (file, e) in items)
                        {
                            w.Write(file);
                            w.Write(e.Length);
                            w.Write(e.WriteTicks);
                            w.Write(e.Bytes.Length);
                            w.Write(e.Bytes);
                        }
                    }
                    File.Move(tmp, path, overwrite: true);
                }
                catch { /* không ghi được bản nhớ chỉ làm lần sau chậm hơn */ }
            }
        });
    }

    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Vượt giới hạn RAM: bỏ bản nhớ của các gốc khác (vẫn còn trên đĩa, cần thì nạp lại).</summary>
    private static void TrimMemory(string keepRoot)
    {
        if (Interlocked.Read(ref _bytes) < MaxMemoryBytes) return;
        var prefix = keepRoot.EndsWith('\\') ? keepRoot : keepRoot + "\\";
        foreach (var key in Files.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (Files.TryRemove(key, out var old)) Interlocked.Add(ref _bytes, -old.Bytes.Length);
        }
        foreach (var r in LoadedRoots.Keys) if (!string.Equals(r, keepRoot, StringComparison.OrdinalIgnoreCase)) LoadedRoots.TryRemove(r, out _);
    }

    private static string Norm(string root) => Path.GetFullPath(root).TrimEnd('\\');

    private static string FileFor(string root)
    {
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..16];
        return Path.Combine(Folder, hash + ".bin");
    }
}
