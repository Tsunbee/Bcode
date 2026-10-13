using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace BcodeViewer.App.Settings;

/// <summary>
/// Lịch sử CHUNG của file source, đặt ngay trong dự án để mọi máy cùng thấy — khác <see cref="LocalHistoryStore"/>
/// (chỉ nằm trên máy người sửa, không ghi ai sửa).
///
/// Bố cục (cùng thư mục "History" mà Bcode dùng cho lịch sử procedure, nhưng nằm dưới "_files" để không lẫn):
///   &lt;gốc dự án&gt;\History\_files\&lt;đường dẫn file tính từ gốc dự án&gt;\&lt;yyyyMMdd-HHmmss-fff&gt;.snap   nội dung phiên bản VỪA LƯU
///   &lt;gốc dự án&gt;\History\_files\...\&lt;cùng tên&gt;.json                                        người lưu: máy, user, IP, giờ, hành động
///
/// Mỗi bản = nội dung do một người VỪA ghi (không phải bản bị ghi đè), nên "bản này do ai ghi" luôn đúng. Lần đầu một file có
/// lịch sử, bản đang có trên đĩa được lưu thêm làm mốc (action = "baseline", không rõ người sửa trước đó).
/// Gốc dự án = thư mục tổ tiên gần nhất có thư mục con App_Data (đúng cấu trúc site FastBusiness: Main\… và App_Data\… cùng gốc).
///
/// Máy / user / IP do chính máy lưu khai báo (Environment, card mạng) — để tham khảo, không phải bằng chứng.
/// Mọi thao tác ghi đều không ném lỗi: <see cref="Record"/> trả về trạng thái để trang báo cho người dùng.
/// </summary>
public static class SharedHistoryStore
{
    public const string Dir = "History";
    public const string SubDir = "_files";
    private const int MaxPerFile = 100;
    private const int MaxSnapshotBytes = 5 * 1024 * 1024;
    private const string IdFormat = "yyyyMMdd-HHmmss-fff";

    public sealed record Entry(string Id, string Rel, string Path, DateTime At, string User, string Machine, string Ip, string Action, int Length, string Owner);

    // ---- gốc dự án ------------------------------------------------------------------------

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> RootCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Thư mục tổ tiên gần nhất của file có thư mục con App_Data; null nếu file không nằm trong site FastBusiness.</summary>
    public static string? FindProjectRoot(string path)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            var walked = new List<string>();
            string? found = null;
            for (var i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++, dir = System.IO.Path.GetDirectoryName(dir))
            {
                if (RootCache.TryGetValue(dir, out var cached)) { found = cached; walked.Add(dir); break; }
                walked.Add(dir);
                if (Directory.Exists(System.IO.Path.Combine(dir, "App_Data"))) { found = dir; break; }
            }
            foreach (var d in walked) RootCache[d] = found;
            return found;
        }
        catch { return null; }
    }

    private static string RelOf(string root, string path) =>
        System.IO.Path.GetRelativePath(root, System.IO.Path.GetFullPath(path));

    private static string FilesRoot(string root) => System.IO.Path.Combine(root, Dir, SubDir);

    private static string FolderFor(string root, string rel) => System.IO.Path.Combine(FilesRoot(root), rel);

    // ---- ghi --------------------------------------------------------------------------------

    /// <summary>
    /// Ghi <paramref name="newContent"/> làm một phiên bản chung. Trả về "ok", "same" (trùng bản mới nhất), "none" (file không thuộc
    /// site FastBusiness), "skip" (file quá lớn) hoặc "fail:&lt;lý do&gt;". <paramref name="previousContent"/> là nội dung trên đĩa trước khi ghi đè.
    /// </summary>
    public static string Record(string path, string newContent, string? previousContent, DateTime? previousWriteTime = null, string? previousOwner = null)
    {
        try
        {
            var root = FindProjectRoot(path);
            if (root is null) return "none";
            if (Encoding.UTF8.GetByteCount(newContent) > MaxSnapshotBytes) return "skip";

            var rel = RelOf(root, path);
            var folder = FolderFor(root, rel);
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return "fail:Không tạo được thư mục lịch sử " + System.IO.Path.Combine(root, Dir) + " (" + ex.Message + ")";
            }

            var newest = Directory.EnumerateFiles(folder, "*.snap").OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
            var now = DateTime.Now;
            if (newest is null && previousContent is not null && previousContent != newContent
                && Encoding.UTF8.GetByteCount(previousContent) <= MaxSnapshotBytes)
            {
                Write(folder, now.AddMilliseconds(-1), previousContent, path, rel, "baseline", unknownAuthor: true, owner: previousOwner);
            }
            else if (newest is not null)
            {
                var newestText = File.ReadAllText(newest);
                // Nội dung trên đĩa TRƯỚC khi ghi khác bản chung cuối cùng = có người sửa file mà không qua BcodeViewer
                // (FCode, Notepad, công cụ khác...) kể từ lần lưu chung gần nhất. Ghi lại bản đó, giờ = giờ sửa file, không rõ người.
                if (previousContent is not null && previousContent != newestText
                    && Encoding.UTF8.GetByteCount(previousContent) <= MaxSnapshotBytes)
                {
                    var newestAt = DateTime.TryParseExact(System.IO.Path.GetFileNameWithoutExtension(newest), IdFormat, null, System.Globalization.DateTimeStyles.None, out var na) ? na : DateTime.MinValue;
                    var at = previousWriteTime ?? now.AddMilliseconds(-1);
                    if (at <= newestAt) at = newestAt.AddMilliseconds(1);
                    if (at >= now) at = now.AddMilliseconds(-1);
                    Write(folder, at, previousContent, path, rel, "external", unknownAuthor: true, owner: previousOwner);
                    if (previousContent == newContent) { Prune(folder); return "ok"; }
                }
                else if (newestText == newContent) return "same";
            }

            Write(folder, now, newContent, path, rel, "save", unknownAuthor: false);
            Prune(folder);
            return "ok";
        }
        catch (Exception ex)
        {
            return "fail:" + ex.Message;
        }
    }

    private static void Write(string folder, DateTime at, string content, string path, string rel, string action, bool unknownAuthor, string? owner = null)
    {
        var id = at.ToString(IdFormat);
        File.WriteAllText(System.IO.Path.Combine(folder, id + ".snap"), content, new UTF8Encoding(false));
        var meta = new
        {
            path, rel, action, owner = owner ?? "",
            machine = unknownAuthor ? "" : Environment.MachineName,
            user = unknownAuthor ? "" : Environment.UserName,
            ip = unknownAuthor ? "" : LocalIp(),
            savedAt = at.ToString("o"),
            length = content.Length,
        };
        File.WriteAllText(System.IO.Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(meta), new UTF8Encoding(false));
    }

    private static void Prune(string folder)
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(folder, "*.snap").OrderByDescending(f => f, StringComparer.Ordinal).Skip(MaxPerFile))
            {
                try
                {
                    File.Delete(old);
                    var j = System.IO.Path.ChangeExtension(old, ".json");
                    if (File.Exists(j)) File.Delete(j);
                }
                catch { /* đang mở — lần sau */ }
            }
        }
        catch { /* không đọc được thư mục */ }
    }

    private static string? _ip;

    private static string LocalIp()
    {
        if (_ip is not null) return _ip;
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any)));
            foreach (var n in nics)
                foreach (var u in n.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(u.Address))
                        return _ip = u.Address.ToString();
        }
        catch { /* không đọc được card mạng */ }
        return _ip = "";
    }

    // ---- đọc --------------------------------------------------------------------------------

    private static bool ValidId(string id) => id.Length is > 0 and <= 32 && id.All(c => char.IsAsciiDigit(c) || c == '-');

    private static bool ValidRel(string rel) => !string.IsNullOrWhiteSpace(rel) && !System.IO.Path.IsPathRooted(rel)
        && !rel.Split('\\', '/').Any(p => p == "..");

    private static Entry ReadEntry(string snapFile, string root, string rel)
    {
        var id = System.IO.Path.GetFileNameWithoutExtension(snapFile);
        var at = DateTime.TryParseExact(id, IdFormat, null, System.Globalization.DateTimeStyles.None, out var p) ? p : File.GetLastWriteTime(snapFile);
        string user = "", machine = "", ip = "", action = "save", owner = "";
        try
        {
            var j = System.IO.Path.ChangeExtension(snapFile, ".json");
            if (File.Exists(j))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(j));
                string S(string n) => doc.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                user = S("user"); machine = S("machine"); ip = S("ip"); action = S("action"); owner = S("owner");
                if (action.Length == 0) action = "save";
            }
        }
        catch { /* json hỏng — vẫn liệt kê được bản */ }
        return new Entry(id, rel, System.IO.Path.Combine(root, rel), at, user, machine, ip, action, (int)new FileInfo(snapFile).Length, owner);
    }

    /// <summary>Các bản của một file, mới nhất trước. root null nếu file không thuộc site FastBusiness.</summary>
    public static (string? Root, List<Entry> Entries, string? Error) ListFile(string path)
    {
        var root = FindProjectRoot(path);
        if (root is null) return (null, new(), null);
        try
        {
            var rel = RelOf(root, path);
            var folder = FolderFor(root, rel);
            if (!Directory.Exists(folder)) return (root, new(), null);
            var list = Directory.EnumerateFiles(folder, "*.snap").OrderByDescending(f => f, StringComparer.Ordinal)
                .Select(f => ReadEntry(f, root, rel)).ToList();
            return (root, list, null);
        }
        catch (Exception ex) { return (root, new(), ex.Message); }
    }

    /// <summary>Mọi file có lịch sử chung trong dự án, mới nhất trước, trong <paramref name="days"/> ngày gần đây (0 = tất cả), tối đa <paramref name="max"/> bản.</summary>
    public static (string? Root, List<Entry> Entries, string? Error) ListProject(string anyPath, int days, int max)
    {
        var root = FindProjectRoot(anyPath);
        if (root is null) return (null, new(), null);
        try
        {
            var filesRoot = FilesRoot(root);
            if (!Directory.Exists(filesRoot)) return (root, new(), null);
            var cutoff = days > 0 ? DateTime.Now.AddDays(-days) : DateTime.MinValue;
            var snaps = Directory.EnumerateFiles(filesRoot, "*.snap", SearchOption.AllDirectories)
                .Select(f => (File: f, Id: System.IO.Path.GetFileNameWithoutExtension(f)))
                .Where(x => !(DateTime.TryParseExact(x.Id, IdFormat, null, System.Globalization.DateTimeStyles.None, out var t) && t < cutoff))
                .OrderByDescending(x => x.Id, StringComparer.Ordinal)
                .Take(max)
                .ToList();
            var list = snaps.Select(x => ReadEntry(x.File, root, System.IO.Path.GetRelativePath(filesRoot, System.IO.Path.GetDirectoryName(x.File)!))).ToList();
            return (root, list, null);
        }
        catch (Exception ex) { return (root, new(), ex.Message); }
    }

    /// <summary>Nội dung một bản và bản liền trước của cùng file (rỗng nếu là bản đầu tiên) — để hiện "người này đã đổi gì".</summary>
    public static (string After, string Before, string BeforeAt)? ReadPair(string anyPath, string rel, string id)
    {
        try
        {
            var root = FindProjectRoot(anyPath);
            if (root is null || !ValidId(id) || !ValidRel(rel)) return null;
            var folder = FolderFor(root, rel);
            var file = System.IO.Path.Combine(folder, id + ".snap");
            if (!File.Exists(file)) return null;
            var after = File.ReadAllText(file);
            var prev = Directory.EnumerateFiles(folder, "*.snap")
                .Where(f => string.CompareOrdinal(System.IO.Path.GetFileNameWithoutExtension(f), id) < 0)
                .OrderByDescending(f => f, StringComparer.Ordinal).FirstOrDefault();
            return (after, prev is null ? "" : File.ReadAllText(prev), prev is null ? "" : System.IO.Path.GetFileNameWithoutExtension(prev));
        }
        catch { return null; }
    }


    /// <summary>
    /// Dọn các bản cũ hơn <paramref name="days"/> ngày. scope = "file" (chỉ file của <paramref name="anyPath"/>) hoặc "project" (cả dự án).
    /// Luôn GIỮ bản mới nhất của mỗi file để lịch sử không mất mốc. dryRun = chỉ đếm, không xoá.
    /// </summary>
    public static (int Count, long Bytes, string? Error) Purge(string anyPath, string scope, int days, bool dryRun)
    {
        try
        {
            var root = FindProjectRoot(anyPath);
            if (root is null) return (0, 0, "File không thuộc site FastBusiness.");
            if (days < 1) return (0, 0, "Số ngày phải từ 1 trở lên.");
            var folders = new List<string>();
            if (scope == "file") folders.Add(FolderFor(root, RelOf(root, anyPath)));
            else if (Directory.Exists(FilesRoot(root)))
                folders.AddRange(Directory.EnumerateDirectories(FilesRoot(root), "*", SearchOption.AllDirectories));
            var cutoff = DateTime.Now.AddDays(-days);
            int count = 0; long bytes = 0;
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;
                var snaps = Directory.EnumerateFiles(folder, "*.snap").OrderByDescending(f => f, StringComparer.Ordinal).ToList();
                foreach (var f in snaps.Skip(1)) // bỏ bản mới nhất
                {
                    var id = System.IO.Path.GetFileNameWithoutExtension(f);
                    var at = DateTime.TryParseExact(id, IdFormat, null, System.Globalization.DateTimeStyles.None, out var t) ? t : File.GetLastWriteTime(f);
                    if (at >= cutoff) continue;
                    var j = System.IO.Path.ChangeExtension(f, ".json");
                    bytes += new FileInfo(f).Length + (File.Exists(j) ? new FileInfo(j).Length : 0);
                    count++;
                    if (dryRun) continue;
                    File.Delete(f);
                    if (File.Exists(j)) File.Delete(j);
                }
            }
            return (count, bytes, null);
        }
        catch (Exception ex) { return (0, 0, ex.Message); }
    }

    public static string? HistoryFolder(string anyPath, string? rel)
    {
        var root = FindProjectRoot(anyPath);
        if (root is null) return null;
        var folder = !string.IsNullOrEmpty(rel) && ValidRel(rel) ? FolderFor(root, rel) : FilesRoot(root);
        return Directory.Exists(folder) ? folder : null;
    }

    public static string ToJson((string? Root, List<Entry> Entries, string? Error) r) => JsonSerializer.Serialize(new
    {
        root = r.Root,
        error = r.Error,
        entries = r.Entries.Select(e => new
        {
            id = e.Id, rel = e.Rel, path = e.Path, savedAt = e.At.ToString("dd/MM/yyyy HH:mm:ss"),
            user = e.User, machine = e.Machine, ip = e.Ip, action = e.Action, length = e.Length, owner = e.Owner,
        }),
    });
}
