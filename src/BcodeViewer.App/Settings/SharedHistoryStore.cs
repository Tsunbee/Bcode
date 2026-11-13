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
/// Settings → "Lưu file" khai thư mục lưu riêng (<see cref="CustomRoot"/>) thì "&lt;gốc dự án&gt;\History" được thay bằng
/// "&lt;thư mục riêng&gt;\&lt;dự án&gt;" (giống Bcode) — xem <see cref="FilesRoot"/>, <see cref="ProjectKey"/>.
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

    // ---- nơi lưu ------------------------------------------------------------------------------
    //
    // Mặc định: <gốc dự án>\History\_files (như trước). Settings → "Lưu file" → "Thư mục lưu lịch sử chung" (ViewerSettings.SharedHistoryPath)
    // khai thư mục riêng thì — giống Bcode với lịch sử procedure (Template → Lịch sử SQL: {thư mục}\{dự án}\{database}\...) — thêm một tầng
    // tên dự án để nhiều dự án dùng chung một thư mục không trộn vào nhau:
    //   <thư mục riêng>\<dự án>\_files\<đường dẫn file tính từ gốc dự án>\...
    // <dự án> = ProjectId của workspace Bcode chứa gốc dự án (trống thì tên workspace) — đúng tên Bcode dùng; gốc dự án nằm sâu hơn
    // Source Path của workspace (1 workspace chứa nhiều site) thì thêm các thư mục ở giữa (vd KOG\FBISP24) để site không trộn nhau.
    // Không workspace nào chứa file thì <thư mục cha>_<thư mục gốc dự án> (vd KOG_FBISP24).
    // Đổi nơi lưu KHÔNG di chuyển / xoá lịch sử cũ; <see cref="OldInfo"/> / <see cref="CopyOld"/> cho trang Lịch sử hỏi rồi chép sang.

    /// <summary>Thư mục lưu riêng (ViewerSettings.SharedHistoryPath); "" = mặc định trong dự án. EditorBridge đặt lúc khởi động và khi lưu Settings.</summary>
    public static string CustomRoot { get; set; } = "";

    private static string? Custom => string.IsNullOrWhiteSpace(CustomRoot) ? null : CustomRoot.Trim();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> KeyCache = new(StringComparer.OrdinalIgnoreCase);

    private static string SafeName(string s)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var t = new string(s.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return t.Length == 0 ? "_" : t;
    }

    /// <summary>Tầng tên dự án dưới thư mục lưu riêng (có thể gồm vài cấp, vd "KOG\FBISP24") — xem ghi chú ở trên.</summary>
    public static string ProjectKey(string root)
    {
        if (KeyCache.TryGetValue(root, out var cached)) return cached;
        string key;
        var probe = System.IO.Path.Combine(root, "App_Data", "_");
        if (Host.WorkspaceConnection.ResolveHistoryProject(probe) is { } ws)
        {
            var parts = new List<string> { SafeName(ws.Project) };
            var inner = System.IO.Path.GetRelativePath(ws.Root, root);
            if (inner != "." && !inner.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(inner))
                parts.AddRange(inner.Split('\\', '/').Where(p => p.Length > 0).Select(SafeName));
            key = System.IO.Path.Combine(parts.ToArray());
        }
        else
        {
            var name = System.IO.Path.GetFileName(root.TrimEnd('\\', '/'));
            var parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? "");
            key = SafeName(string.IsNullOrEmpty(parent) || parent.Contains(':') ? name : parent + "_" + name);
        }
        KeyCache[root] = key;
        return key;
    }

    /// <summary>Chỗ lưu mặc định trong dự án: &lt;gốc dự án&gt;\History\_files.</summary>
    private static string DefaultFilesRoot(string root) => System.IO.Path.Combine(root, Dir, SubDir);

    /// <summary>Thư mục "_files" đang dùng cho dự án <paramref name="root"/> — theo Settings.</summary>
    public static string FilesRoot(string root) =>
        Custom is { } custom ? System.IO.Path.Combine(custom, ProjectKey(root), SubDir) : DefaultFilesRoot(root);

    private static string FolderFor(string root, string rel) => System.IO.Path.Combine(FilesRoot(root), rel);

    /// <summary>Lời báo khi thư mục lưu riêng không truy cập được (share mất kết nối / thiếu quyền / ổ không có); null = dùng mặc định hoặc vào được.</summary>
    private static string? CustomUnreachable()
    {
        if (Custom is not { } custom) return null;
        try
        {
            if (!System.IO.Path.IsPathFullyQualified(custom)) return "Thư mục lưu lịch sử chung \"" + custom + "\" không phải đường dẫn đầy đủ (Settings → Lưu file).";
            if (Directory.Exists(custom)) return null;
        }
        catch { /* rơi xuống báo lỗi */ }
        return "Không truy cập được thư mục lưu lịch sử chung " + custom + " (đặt ở Settings → Lưu file) — ổ mạng mất kết nối, thiếu quyền hoặc thư mục không còn.";
    }

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
                // Thư mục lưu riêng (Settings) không vào được thì nói rõ là thư mục đó — KHÔNG lặng lẽ ghi về chỗ mặc định trong dự án.
                return Custom is { } custom
                    ? "fail:Không ghi được vào thư mục lưu lịch sử chung " + custom + " (đặt ở Settings → Lưu file) — " + ex.Message
                    : "fail:Không tạo được thư mục lịch sử " + System.IO.Path.Combine(root, Dir) + " (" + ex.Message + ")";
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
            if (!Directory.Exists(folder)) return (root, new(), CustomUnreachable());
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
            if (!Directory.Exists(filesRoot)) return (root, new(), CustomUnreachable());
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
            if (CustomUnreachable() is { } unreachable) return (0, 0, unreachable);
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
        if (Directory.Exists(folder)) return folder;
        return Directory.Exists(FilesRoot(root)) ? FilesRoot(root) : null; // file chưa có bản nào thì mở thư mục của dự án
    }

    // ---- lịch sử ở chỗ mặc định cũ (sau khi đổi sang thư mục lưu riêng) ------------------------
    // Không tự di chuyển / xoá: trang Lịch sử báo "còn N bản ở chỗ cũ" và chỉ chép khi người dùng bấm đồng ý. Chép = thêm bản chưa có
    // ở chỗ mới (trùng tên thì bỏ qua), chỗ cũ giữ nguyên.

    /// <summary>Các bản ở chỗ mặc định cũ (&lt;gốc dự án&gt;\History\_files) chưa có ở thư mục lưu riêng. scope = "file" | "project".</summary>
    private static List<(string Snap, string DestFolder)> OldPending(string anyPath, string scope, out string? oldFolder, out string? root)
    {
        oldFolder = null;
        var list = new List<(string, string)>();
        root = FindProjectRoot(anyPath);
        if (root is null || Custom is null) return list;
        var oldRoot = DefaultFilesRoot(root);
        var newRoot = FilesRoot(root);
        if (string.Equals(System.IO.Path.GetFullPath(oldRoot), System.IO.Path.GetFullPath(newRoot), StringComparison.OrdinalIgnoreCase)) return list;
        oldFolder = scope == "file" ? System.IO.Path.Combine(oldRoot, RelOf(root, anyPath)) : oldRoot;
        if (!Directory.Exists(oldFolder)) { oldFolder = null; return list; }
        foreach (var snap in Directory.EnumerateFiles(oldFolder, "*.snap", scope == "file" ? SearchOption.TopDirectoryOnly : SearchOption.AllDirectories))
        {
            var rel = System.IO.Path.GetRelativePath(oldRoot, System.IO.Path.GetDirectoryName(snap)!);
            var dest = System.IO.Path.Combine(newRoot, rel);
            if (!File.Exists(System.IO.Path.Combine(dest, System.IO.Path.GetFileName(snap)))) list.Add((snap, dest));
        }
        return list;
    }

    /// <summary>JSON {oldFolder, count, bytes, error}: lịch sử ở chỗ mặc định cũ chưa chép sang thư mục lưu riêng (count = 0 / oldFolder null = không có gì).</summary>
    public static string OldInfoJson(string anyPath, string scope)
    {
        try
        {
            var pending = OldPending(anyPath, scope, out var oldFolder, out _);
            long bytes = 0;
            foreach (var (snap, _) in pending)
            {
                bytes += new FileInfo(snap).Length;
                var j = System.IO.Path.ChangeExtension(snap, ".json");
                if (File.Exists(j)) bytes += new FileInfo(j).Length;
            }
            return JsonSerializer.Serialize(new { oldFolder, count = pending.Count, bytes, error = (string?)null });
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { oldFolder = (string?)null, count = 0, bytes = 0L, error = ex.Message }); }
    }

    /// <summary>Chép các bản ở chỗ mặc định cũ sang thư mục lưu riêng (chỉ bản chưa có; không xoá chỗ cũ). JSON {count, error}.</summary>
    public static string CopyOldJson(string anyPath, string scope)
    {
        var copied = 0;
        try
        {
            if (CustomUnreachable() is { } unreachable) return JsonSerializer.Serialize(new { count = 0, error = unreachable });
            var pending = OldPending(anyPath, scope, out _, out _);
            var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (snap, dest) in pending)
            {
                Directory.CreateDirectory(dest);
                var j = System.IO.Path.ChangeExtension(snap, ".json");
                if (File.Exists(j)) File.Copy(j, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(j)), overwrite: true);
                File.Copy(snap, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(snap)), overwrite: false);
                touched.Add(dest);
                copied++;
            }
            foreach (var d in touched) Prune(d); // giữ đúng giới hạn MaxPerFile ở chỗ mới
            return JsonSerializer.Serialize(new { count = copied, error = (string?)null });
        }
        catch (Exception ex) { return JsonSerializer.Serialize(new { count = copied, error = ex.Message }); }
    }

    public static string ToJson((string? Root, List<Entry> Entries, string? Error) r) => JsonSerializer.Serialize(new
    {
        root = r.Root,
        folder = r.Root is null ? null : FilesRoot(r.Root),
        custom = Custom is not null,
        error = r.Error,
        entries = r.Entries.Select(e => new
        {
            id = e.Id, rel = e.Rel, path = e.Path, savedAt = e.At.ToString("dd/MM/yyyy HH:mm:ss"),
            user = e.User, machine = e.Machine, ip = e.Ip, action = e.Action, length = e.Length, owner = e.Owner,
        }),
    });
}
