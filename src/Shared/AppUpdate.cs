using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.Shared;

/// <summary>Dấu 1 bản build — từ bcode-version.json do target WriteBuildStamp (Bcode.App.csproj) ghi cạnh Bcode.exe.
/// <see cref="Build"/> = số commit git (tăng dần), 0 = bản build cũ chưa có.</summary>
public sealed record BuildStamp(DateTime BuiltUtc, string Commit, string Config, string Version = "", int Build = 0)
{
    public string When => BuiltUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"v2.1-beta · build 466 · 10/11/2026 09:57" (phần nào thiếu thì bỏ).</summary>
    public string Label => (Version.Length > 0 ? "v" + Version + " · " : "") + (Build > 0 ? "build " + Build + " · " : "") + When;

    public bool HasCommit => Commit is { Length: > 0 } c && c != "unknown";
}

/// <summary>1 commit trong bcode-changelog.txt: dòng "hash|yyyy-MM-dd|tiêu đề commit", có thể kèm ngay sau dòng "|note|câu cho người dùng"
/// (trailer "note:" trong thân commit — nhiều note nối bằng ";;").</summary>
public sealed record ChangeEntry(string Hash, string Date, string Text, string Note = "");

/// <summary>1 mục "Có gì mới" đã xử lý: Kind = "new" (tính năng mới) / "fix" (sửa lỗi) / "imp" (cải tiến khác).</summary>
public sealed record ChangeItem(string Kind, string Text, string Hash, string Date);

/// <summary>Tiến độ chuẩn bị bản mới. Stage: "scan" (so file ở máy với bản mới) / "download" (tải file khác về thư mục tạm).</summary>
public sealed record UpdateProgress(string Stage, int Done, int Total, long Bytes, long TotalBytes, string File);

/// <summary>Kế hoạch cập nhật đã chuẩn bị xong: file đã tải đủ + kiểm hash trong <see cref="UpdateCheckService.Staging"/>. Lưu ở staging\plan.json.</summary>
public sealed class UpdatePlan
{
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public DateTime RemoteBuiltUtc { get; set; }
    public string FromLabel { get; set; } = "";
    public string ToLabel { get; set; } = "";
    /// <summary>File sẽ chép đè / thêm (đường dẫn tương đối, đã nằm trong staging).</summary>
    public List<string> Copy { get; set; } = new();
    /// <summary>File người dùng đã tự sửa ở máy (khác bản cũ) — mặc định GIỮ, chỉ chép khi người dùng chọn ghi đè. Cũng đã nằm trong staging.</summary>
    public List<string> Modified { get; set; } = new();
    /// <summary>File bản cũ có, bản mới bỏ (và máy chưa sửa) — dời vào thư mục sao lưu.</summary>
    public List<string> Delete { get; set; } = new();
    public long Bytes { get; set; }
    /// <summary>Bản phát hành có bcode-manifest.json (kiểm được hash) hay chỉ so cỡ + giờ sửa file.</summary>
    public bool Verified { get; set; }
}

/// <summary>Kết quả lần cập nhật / quay lại vừa chạy (script ghi update\result.json, Bcode đọc lúc mở lại rồi xoá).</summary>
public sealed record UpdateResult(bool Ok, string Mode, string From, string To, string Message);

/// <summary>Bản trước còn giữ (thư mục update\backup) để "Quay lại bản trước".</summary>
public sealed record BackupInfo(string From, string To, DateTime At, List<string> Added);

/// <summary>
/// "Cập nhật Bcode". Kiểm tra: so bcode-version.json của bản đang chạy với thư mục phát hành (Settings → Thư mục phát hành bản mới) — chỉ đọc 1 file nhỏ,
/// có hạn giờ (UNC treo không làm treo Bcode). Chuẩn bị (<see cref="PrepareAsync"/>, lúc Bcode còn chạy): so từng file theo bcode-manifest.json (SHA-256)
/// — bản phát hành không có manifest thì so cỡ + giờ sửa — chỉ tải file khác về %LocalAppData%\Bcode\update\staging, kiểm hash, đọc lại dấu phiên bản
/// (bản phát hành đổi giữa chừng thì báo thử lại). Cài (<see cref="StartApply"/>): 1 script PowerShell tách riêng chờ mọi tiến trình chạy từ thư mục Bcode
/// thoát, sao file sắp ghi đè vào update\backup, chép từ staging (chỉ ổ local — vài giây), lỗi giữa chừng thì chép trả bản cũ; ghi update\result.json,
/// mở lại Bcode. Backup giữ 1 bản để "Quay lại bản trước". Dữ liệu người dùng ở %AppData%\Bcode không bị đụng.
/// </summary>
public static class UpdateCheckService
{
    // ---------------------------------------------------------------- chương trình nào đang dùng
    // File này link vào Bcode.App và BcodeViewer.App: mỗi exe một cơ chế cập nhật riêng, thư mục phát hành riêng, thư mục tạm / nhật ký riêng.
    // Mỗi app gọi Configure lúc khởi động (Bcode: MainForm.InitUpdateCheck; Viewer: MainForm.Update.cs).

    /// <summary>Tên hiện trên giao diện + tên exe ("Bcode" / "BcodeViewer").</summary>
    public static string AppName { get; private set; } = "Bcode";
    /// <summary>Tên thư mục làm việc trong %LocalAppData%\Bcode và tên nhật ký trong %AppData%\Bcode ("update" → update\, update.log).</summary>
    private static string WorkName { get; set; } = "update";
    /// <summary>%AppData% thật của app (BcodePaths.AppData — mỗi app có bản sao riêng).</summary>
    private static string AppDataRoot { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Tên file exe ("BcodeScreenDesigner.exe").</summary>
    public static string ExeName { get; private set; } = "Bcode.exe";
    /// <summary>Giá trị -App của tools\publish-release.ps1 ("" = Bcode, "Viewer", "Designer") — chỉ để hiện hướng dẫn.</summary>
    public static string PublishApp { get; private set; } = "";
    /// <summary>Thư mục con (cấp 1) trong thư mục cài là APP KHÁC tự cập nhật riêng (Bcode: "ScreenDesigner") — không bao giờ tải / ghi đè / xoá.
    /// Ngoài danh sách này, thư mục con nào có bcode-version.json riêng cũng được coi là app khác.</summary>
    private static string[] KeepDirs { get; set; } = Array.Empty<string>();

    /// <param name="appName">Tên hiện trên giao diện.</param>
    /// <param name="workName">Thư mục tạm %LocalAppData%\Bcode\&lt;workName&gt; + nhật ký %AppData%\Bcode\&lt;workName&gt;.log.</param>
    /// <param name="exeName">Tên exe (mặc định appName + ".exe").</param>
    /// <param name="publishApp">-App của publish-release.ps1.</param>
    /// <param name="keepDirs">Thư mục con là app khác (xem <see cref="KeepDirs"/>).</param>
    public static void Configure(string appName, string workName, string appDataRoot, string? exeName = null, string publishApp = "", params string[] keepDirs)
    {
        AppName = appName; WorkName = workName; AppDataRoot = appDataRoot;
        ExeName = exeName ?? appName + ".exe"; PublishApp = publishApp; KeepDirs = keepDirs;
    }

    /// <summary>Thư mục con (cấp 1, tương đối) của <paramref name="installDir"/> thuộc app khác: <see cref="KeepDirs"/> + thư mục có bcode-version.json riêng.</summary>
    private static HashSet<string> NestedApps(string installDir)
    {
        var set = new HashSet<string>(KeepDirs, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in Directory.EnumerateDirectories(installDir))
                if (File.Exists(Path.Combine(d, StampFileName))) set.Add(Path.GetFileName(d));
        }
        catch { /* không liệt kê được: chỉ dùng KeepDirs */ }
        return set;
    }

    private static bool InNested(string rel, HashSet<string> nested)
    {
        var cut = rel.IndexOfAny(new[] { '\\', '/' });
        return cut > 0 && nested.Contains(rel[..cut]);
    }

    public const string StampFileName = "bcode-version.json";
    public const string ManifestFileName = "bcode-manifest.json";
    public const string ChangelogFileName = "bcode-changelog.txt";
    private static readonly string[] MetaFiles = { StampFileName, ManifestFileName, ChangelogFileName };
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>Giá trị khai trong Settings nghĩa là "tắt kiểm tra" (khác với trống = dùng thư mục mặc định).</summary>
    public const string OffMarker = "-";

    /// <summary>Thư mục phát hành mặc định — &lt;BcodeReleaseFolder&gt; trong Bcode.App.csproj (nhúng vào metadata assembly). "" = không có.</summary>
    public static string DefaultReleaseFolder { get; } = ReadDefaultFolder();

    private static string ReadDefaultFolder()
    {
        try
        {
            return System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(UpdateCheckService).Assembly)
                .FirstOrDefault(a => a.Key == "BcodeReleaseFolder")?.Value?.Trim() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Thư mục phát hành dùng thật: khai trong Settings; trống → mặc định; "-" → "" (tắt).</summary>
    public static string EffectiveFolder(string? setting)
    {
        var s = (setting ?? "").Trim().Trim('"');
        if (s == OffMarker) return "";
        return s.Length > 0 ? s : DefaultReleaseFolder;
    }

    /// <summary>Chữ người dùng gõ → giá trị lưu Settings: trùng mặc định thì lưu trống (lên bản đổi mặc định thì theo luôn), trống / "-" = tắt.</summary>
    public static string ToSetting(string? typed)
    {
        var s = (typed ?? "").Trim().Trim('"');
        if (s.Length == 0 || s == OffMarker) return DefaultReleaseFolder.Length > 0 ? OffMarker : "";
        return string.Equals(s.TrimEnd('\\'), DefaultReleaseFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? "" : s;
    }

    /// <summary>Chú thích ô nhập thư mục phát hành (hộp Settings → Thư mục phát hành bản mới và Template → Đường dẫn).</summary>
    public static string FolderPromptHint =>
        $"Thư mục chứa bản build {AppName} mới (có file bcode-version.json) — {AppName} tự kiểm tra lúc mở. Để trống hoặc gõ - = không kiểm tra"
        + (DefaultReleaseFolder.Length > 0 ? ". Mặc định: " + DefaultReleaseFolder : "");

    // ---------------------------------------------------------------- dấu phiên bản

    /// <summary>Dấu của bản đang chạy; thiếu file (build cũ) thì lấy giờ sửa Bcode.exe. Thiếu số phiên bản thì lấy &lt;Version&gt; của assembly.</summary>
    public static BuildStamp Local
    {
        get
        {
            var s = Read(Path.Combine(ExeDir, StampFileName))
                ?? new BuildStamp(File.GetLastWriteTimeUtc(Environment.ProcessPath ?? Path.Combine(ExeDir, ExeName)), "", "");
            return s.Version.Length > 0 ? s : s with { Version = AssemblyVersion };
        }
    }

    /// <summary>&lt;Version&gt; của exe ("2.2-beta"), bỏ đuôi "+commit".</summary>
    private static string AssemblyVersion
    {
        get
        {
            try
            {
                var a = typeof(UpdateCheckService).Assembly;
                var v = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(a)?.InformationalVersion
                        ?? a.GetName().Version?.ToString() ?? "";
                var plus = v.IndexOf('+');
                return plus > 0 ? v[..plus] : v;
            }
            catch { return ""; }
        }
    }

    public static BuildStamp? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var built = r.TryGetProperty("built", out var b) && DateTime.TryParse(b.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;
            if (built is null) return null;
            string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var build = r.TryGetProperty("build", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var i) ? i : 0;
            return new BuildStamp(built.Value, S("commit"), S("config"), S("version"), build);
        }
        catch { return null; }
    }

    /// <summary>Đọc dấu phiên bản trong thư mục phát hành. Error != null khi không đọc được (mất mạng, thư mục sai, thiếu file).</summary>
    public static async Task<(BuildStamp? Remote, string? Error)> CheckAsync(string? releaseFolder)
    {
        var (remote, error, _) = await CheckDetailedAsync(releaseFolder);
        return (remote, error);
    }

    /// <summary>Như <see cref="CheckAsync"/>, thêm <c>NoRelease</c> = vào được thư mục nhưng chưa có bản phát hành (thiếu bcode-version.json) —
    /// khác với không vào được (mất mạng, sai đường dẫn, quá giờ).</summary>
    public static async Task<(BuildStamp? Remote, string? Error, bool NoRelease)> CheckDetailedAsync(string? releaseFolder)
    {
        var folder = (releaseFolder ?? "").Trim();
        if (folder.Length == 0) return (null, "Chưa đặt thư mục phát hành (Settings → Thư mục phát hành bản mới).", false);
        var path = Path.Combine(folder, StampFileName);
        var work = Task.Run(() => Directory.Exists(folder) ? (Read(path), true, File.Exists(path)) : ((BuildStamp?)null, false, false));
        if (await Task.WhenAny(work, Task.Delay(Timeout)) != work) return (null, $"Không vào được {folder} (quá {Timeout.TotalSeconds:0} giây).", false);
        try
        {
            var (stamp, dir, exists) = await work;
            if (stamp is not null) return (stamp, null, false);
            if (!dir) return (null, $"Không vào được {folder} — thư mục không tồn tại hoặc máy này không có quyền đọc.", false);
            if (exists) return (null, $"{path} không đúng định dạng.", false);
            return (null, $"Chưa có bản phát hành trong {folder} (chưa có file {StampFileName}).", true);
        }
        catch (Exception ex) { return (null, ex.Message, false); }
    }

    /// <summary>Bản phát hành mới hơn bản đang chạy: hai bên có số build khác nhau thì so số build (build lại commit cũ không bị coi là mới);
    /// cùng số / thiếu số thì so giờ build (lệch dưới 1 phút coi như cùng bản).</summary>
    public static bool IsNewer(BuildStamp remote, BuildStamp local)
    {
        if (remote.Build > 0 && local.Build > 0 && remote.Build != local.Build) return remote.Build > local.Build;
        return remote.BuiltUtc > local.BuiltUtc.AddMinutes(1);
    }

    /// <summary>Thư mục phát hành chính là thư mục đang chạy (hoặc nằm trong nhau) thì không cập nhật được.</summary>
    public static bool SameFolder(string releaseFolder)
    {
        static string N(string p) => Path.GetFullPath(p).TrimEnd('\\', '/') + "\\";
        try
        {
            var a = N(releaseFolder); var b = N(ExeDir);
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    // ---------------------------------------------------------------- "Có gì mới"

    private static readonly Regex CommitPrefix = new(@"^\s*(bcode\s*viewer|bcode|viewer)\s*\|\s*", RegexOptions.IgnoreCase);

    /// <summary>Đọc bcode-changelog.txt (có hạn giờ). Lỗi / không có → rỗng.</summary>
    public static async Task<List<ChangeEntry>> ReadChangelogAsync(string folder)
    {
        var work = Task.Run(() => ReadChangelog(Path.Combine(folder, ChangelogFileName)));
        return await Task.WhenAny(work, Task.Delay(Timeout)) == work ? await work : new List<ChangeEntry>();
    }

    private static List<ChangeEntry> ReadChangelog(string path)
    {
        var list = new List<ChangeEntry>();
        try
        {
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
            {
                // "|note|..." đi ngay sau dòng commit của nó (bản Bcode cũ bỏ qua dòng này vì hash trống)
                if (line.StartsWith(NoteLinePrefix, StringComparison.Ordinal))
                {
                    var note = line[NoteLinePrefix.Length..].Trim();
                    if (note.Length > 0 && list.Count > 0) list[^1] = list[^1] with { Note = note };
                    continue;
                }
                var p = line.Split('|', 3);
                if (p.Length < 3 || p[0].Trim().Length == 0) continue;
                list.Add(new ChangeEntry(p[0].Trim(), p[1].Trim(), CommitPrefix.Replace(p[2], "").Trim()));
            }
        }
        catch { /* không đọc được: không hiện "Có gì mới" */ }
        return list;
    }

    /// <summary>Các commit bản mới có mà bản đang chạy chưa có: tới commit của bản đang chạy thì dừng; không thấy commit đó thì lấy commit từ ngày build bản đang chạy.</summary>
    public static List<ChangeEntry> NewSince(List<ChangeEntry> log, BuildStamp local, int max = 40)
    {
        var idx = local.HasCommit ? log.FindIndex(e => local.Commit.StartsWith(e.Hash, StringComparison.OrdinalIgnoreCase) || e.Hash.StartsWith(local.Commit, StringComparison.OrdinalIgnoreCase)) : -1;
        if (idx >= 0) return log.Take(Math.Min(idx, max)).ToList();
        var since = local.BuiltUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return log.Where(e => string.CompareOrdinal(e.Date, since) >= 0).Take(max).ToList();
    }

    private const string NoteLinePrefix = "|note|";

    /// <summary>Tiêu đề có [internal] / [skip] / [nội bộ] → không hiện cho người dùng.</summary>
    private static readonly Regex HiddenTag = new(@"\[(internal|skip|nội bộ)\]|^\s*Merge\s", RegexOptions.IgnoreCase);
    /// <summary>Chữ "fix" đầu ý — bỏ khi hiện (đã nằm dưới nhóm "Sửa lỗi").</summary>
    private static readonly Regex FixLead = new(@"^(fix|fixed)\s+(lỗi\s+)?", RegexOptions.IgnoreCase);
    /// <summary>Tách 1 commit nhiều ý: dấu phẩy / chấm phẩy + khoảng trắng, hoặc dấu phẩy dính liền ý mới ("a,fix b").</summary>
    private static readonly Regex ItemSplit = new(@"[,;]\s+|,(?=(?:fix|sửa|bổ sung|thêm)\b)", RegexOptions.IgnoreCase);
    private static readonly Regex FixWord = new(@"^(fix|sửa|lỗi|bug)\b", RegexOptions.IgnoreCase);
    private static readonly Regex NewWord = new(@"^(bổ sung|thêm|hỗ trợ|cho phép|cho|add|new|tính năng mới)\b", RegexOptions.IgnoreCase);
    private static readonly Regex ImpWord = new(@"^(đổi|chỉnh|điều chỉnh|tối ưu|cải tiến|update|cập nhật|bỏ|ẩn|giấu)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Spaces = new(@"\s+");
    /// <summary>Ý chỉ gồm toàn các từ này = vô nghĩa với người dùng ("fix", "fix update", "fix và bổ sung 1 vài tính năng").</summary>
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "fix", "fixed", "bug", "bugs", "sửa", "lỗi", "update", "updated", "cập", "nhật", "và", "&", "+", "bổ", "sung", "thêm",
        "1", "một", "vài", "số", "các", "nhiều", "ít", "tính", "năng", "chức", "nhỏ", "linh", "tinh", "chỗ", "cái", "cải", "tiến",
        "wip", "test", "tmp", "temp", "minor", "misc", "version", "phiên", "bản", "install", "bcode", "code", "data",
    };

    /// <summary>Commit → mục "Có gì mới": bỏ commit [internal]; có note thì dùng note thay tiêu đề; tách ý theo dấu phẩy; phân loại
    /// Mới / Sửa lỗi / Cải tiến theo chữ đầu; bỏ ý vô nghĩa ("fix", "update"...) và ý trùng. Có ý bị bỏ vì vô nghĩa → thêm 1 dòng
    /// "Một số sửa lỗi và cải tiến nhỏ khác". Thứ tự: mới nhất trước.</summary>
    public static List<ChangeItem> Digest(IEnumerable<ChangeEntry> commits)
    {
        var items = new List<ChangeItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var noise = false;
        foreach (var c in commits)
        {
            if (HiddenTag.IsMatch(c.Text)) continue;
            var parts = c.Note.Length > 0
                ? c.Note.Split(";;", StringSplitOptions.RemoveEmptyEntries)   // note do người viết soạn sẵn: không tách theo dấu phẩy
                : ItemSplit.Split(c.Text);
            var prevKind = "imp";   // ý không có từ khoá đầu câu theo loại của ý trước trong cùng commit ("bổ sung A, B" → B cũng là mới)
            foreach (var raw in parts)
            {
                var t = Spaces.Replace(raw, " ").Trim().TrimEnd(',', ';', ' ');
                if (t.Trim('.', ' ').Length == 0) continue;
                var words = t.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.All(NoiseWords.Contains) || t.Length < 4) { noise = true; continue; }
                var kind = FixWord.IsMatch(t) ? "fix" : NewWord.IsMatch(t) ? "new" : ImpWord.IsMatch(t) ? "imp" : prevKind;
                prevKind = kind;
                if (kind == "fix" && FixLead.Replace(t, "") is { Length: >= 2 } rest) t = rest;
                if (!seen.Add(t)) continue;
                items.Add(new ChangeItem(kind, char.ToUpper(t[0]) + t[1..], c.Hash, c.Date));
            }
        }
        if (noise) items.Add(new ChangeItem("imp", "Một số sửa lỗi và cải tiến nhỏ khác", "", ""));
        return items;
    }

    // ---------------------------------------------------------------- thư mục làm việc

    /// <summary>%LocalAppData%\Bcode\update (không roaming — bản tải về có thể vài trăm MB).</summary>
    public static string UpdateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bcode", WorkName);
    public static string Staging => Path.Combine(UpdateRoot, "staging");
    public static string BackupDir => Path.Combine(UpdateRoot, "backup");
    private static string ResultPath => Path.Combine(UpdateRoot, "result.json");
    private static string PlanPath => Path.Combine(Staging, "plan.json");
    public static string LogPath => Path.Combine(AppDataRoot, "Bcode", WorkName + ".log");

    private static string Full(string p) => Path.GetFullPath(p.Trim()).TrimEnd('\\', '/');

    private static bool IsMeta(string rel) => MetaFiles.Any(m => string.Equals(rel, m, StringComparison.OrdinalIgnoreCase));

    /// <summary>Không bao giờ tải / ghi đè / xoá: hồ sơ WebView2 (Bcode.exe.WebView2\…, sinh ra khi chạy Bcode từ thư mục build) — dữ liệu riêng từng máy.</summary>
    private static bool IsExcluded(string rel) =>
        rel.Split('\\', '/').Any(seg => seg.EndsWith(".WebView2", StringComparison.OrdinalIgnoreCase));

    /// <summary>bcode-manifest.json → (đường dẫn tương đối kiểu Windows → sha256 chữ thường). null = không có / hỏng.</summary>
    private static Dictionary<string, string>? ReadManifest(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateArray())
            {
                var p = f.GetProperty("p").GetString(); var h = f.GetProperty("h").GetString();
                if (string.IsNullOrEmpty(p) || string.IsNullOrEmpty(h)) continue;
                var rel = p.Replace('/', '\\');
                if (rel.Contains("..") || Path.IsPathRooted(rel) || IsMeta(rel) || IsExcluded(rel)) continue;   // không cho manifest trỏ ra ngoài thư mục Bcode
                map[rel] = h.ToLowerInvariant();
            }
            return map;
        }
        catch { return null; }
    }

    private static string HashFile(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    // ---------------------------------------------------------------- chuẩn bị (tải về staging)

    /// <summary>Kế hoạch đã chuẩn bị sẵn cho đúng bản phát hành này (mở lại hộp Cập nhật không phải tải lại). null = chưa có / đã cũ.</summary>
    public static UpdatePlan? LoadPlan(string releaseFolder, BuildStamp remote)
    {
        try
        {
            if (!File.Exists(PlanPath)) return null;
            var plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(PlanPath));
            if (plan is null || plan.RemoteBuiltUtc != remote.BuiltUtc) return null;
            if (!string.Equals(plan.Target, Full(ExeDir), StringComparison.OrdinalIgnoreCase) || !string.Equals(plan.Source, Full(releaseFolder), StringComparison.OrdinalIgnoreCase)) return null;
            return plan.Copy.Concat(plan.Modified).All(r => File.Exists(Path.Combine(Staging, r))) ? plan : null;
        }
        catch { return null; }
    }

    /// <summary>So bản mới với thư mục Bcode, tải các file khác về staging (kiểm hash nếu có manifest). Chạy nền; huỷ được bằng <paramref name="ct"/>.</summary>
    public static Task<UpdatePlan> PrepareAsync(string releaseFolder, BuildStamp remote, IProgress<UpdateProgress>? progress, CancellationToken ct)
        => Task.Run(() => Prepare(releaseFolder, remote, progress, ct), ct);

    private static UpdatePlan Prepare(string releaseFolder, BuildStamp remote, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        var src = Full(releaseFolder);
        var dst = Full(ExeDir);
        var newMan = ReadManifest(Path.Combine(src, ManifestFileName));
        var oldMan = ReadManifest(Path.Combine(dst, ManifestFileName));
        var nested = NestedApps(dst);   // thư mục con là app khác tự cập nhật riêng (Bcode: ScreenDesigner) — không đụng

        // Danh sách file của bản mới: theo manifest; không có thì liệt kê thư mục (cỡ + giờ sửa lấy luôn từ danh sách — không hỏi mạng từng file)
        List<(string Rel, string? Hash, FileInfo? Info)> files;
        if (newMan is not null) files = newMan.Where(kv => !InNested(kv.Key, nested)).Select(kv => (kv.Key, (string?)kv.Value, (FileInfo?)null)).ToList();
        else
        {
            var root = new DirectoryInfo(src);
            files = root.EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(f => (Rel: Path.GetRelativePath(src, f.FullName), Hash: (string?)null, Info: (FileInfo?)f))
                .Where(x => !IsMeta(x.Rel) && !IsExcluded(x.Rel) && !InNested(x.Rel, nested)).ToList();
        }

        if (Directory.Exists(Staging)) Directory.Delete(Staging, true);
        Directory.CreateDirectory(Staging);

        var last = Stopwatch.StartNew();
        void Report(UpdateProgress p, bool force = false)
        {
            if (!force && last.ElapsedMilliseconds < 120) return;
            last.Restart(); progress?.Report(p);
        }

        // 1) so với máy: file giống hệt thì bỏ qua; khác thì đánh dấu tải; khác bản cũ (người dùng tự sửa) thì đánh dấu "đã sửa"
        var need = new List<(string Rel, string? Hash, bool Modified)>();
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (rel, hash, info) = files[i];
            Report(new UpdateProgress("scan", i, files.Count, 0, 0, rel));
            var local = Path.Combine(dst, rel);
            var exists = File.Exists(local);
            if (hash is not null)
            {
                var lh = exists ? HashFile(local) : null;
                if (lh == hash) continue;
                var modified = exists && (oldMan is not null
                    ? oldMan.TryGetValue(rel, out var oh) && oh != lh
                    : File.GetLastWriteTimeUtc(local) > File.GetLastWriteTimeUtc(Path.Combine(src, rel)).AddSeconds(2));
                need.Add((rel, hash, modified));
            }
            else
            {
                if (exists)
                {
                    var li = new FileInfo(local);
                    if (li.Length == info!.Length && Math.Abs((li.LastWriteTimeUtc - info.LastWriteTimeUtc).TotalSeconds) <= 2) continue;
                    need.Add((rel, null, li.LastWriteTimeUtc > info.LastWriteTimeUtc.AddSeconds(2)));   // như robocopy /XO cũ: file máy mới hơn = tự sửa
                }
                else need.Add((rel, null, false));
            }
        }

        // 2) tải về staging
        long total = 0;
        var sizes = new long[need.Count];
        for (var i = 0; i < need.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var fi = new FileInfo(Path.Combine(src, need[i].Rel));
            if (!fi.Exists) throw new InvalidOperationException($"Bản phát hành thiếu file {need[i].Rel} — có thể đang được chép dở. Thử lại sau ít phút.");
            sizes[i] = fi.Length; total += fi.Length;
        }
        long done = 0;
        var plan = new UpdatePlan { Source = src, Target = dst, RemoteBuiltUtc = remote.BuiltUtc, FromLabel = Local.Label, ToLabel = remote.Label, Bytes = total, Verified = newMan is not null };
        for (var i = 0; i < need.Count; i++)
        {
            var (rel, hash, modified) = need[i];
            Report(new UpdateProgress("download", i, need.Count, done, total, rel), force: i == 0);
            var from = Path.Combine(src, rel);
            var to = Path.Combine(Staging, rel);
            var before = done;
            string? prevGot = null;
            for (var attempt = 1; ; attempt++)
            {
                done = before;
                var got = CopyWithHash(from, to, n => { done += n; Report(new UpdateProgress("download", i, need.Count, done, total, rel)); }, ct);
                if (hash is null || got == hash) break;
                if (attempt >= 2)
                {
                    // 2 lần tải ra CÙNG một hash sai = file trên server đứng yên nhưng lệch bcode-manifest.json (manifest sinh trước khi file được build lại —
                    // v2.3-beta build 475: BcodeNote.dll) → Thử lại không hết, phải phát hành lại. Hash khác nhau = file đang bị chép đè.
                    if (got == prevGot)
                        throw new InvalidOperationException($"File {rel} trên thư mục phát hành không khớp mã kiểm tra (SHA-256) ghi trong bcode-manifest.json — "
                            + "bản phát hành bị lệch, Thử lại sẽ không hết. Báo người phát hành chạy lại tools\\publish-release.ps1 (script kiểm lại manifest trước khi chép).");
                    throw new InvalidOperationException($"File {rel} tải về sai mã kiểm tra (SHA-256) — bản phát hành có thể đang được chép dở. Thử lại sau ít phút.");
                }
                prevGot = got;
            }
            (modified ? plan.Modified : plan.Copy).Add(rel);
        }

        // 3) file bản cũ có, bản mới bỏ — chỉ khi biết chắc (có cả 2 manifest) và máy chưa sửa file đó
        if (newMan is not null && oldMan is not null)
            foreach (var (rel, oh) in oldMan)
            {
                if (newMan.ContainsKey(rel) || InNested(rel, nested)) continue;
                var local = Path.Combine(dst, rel);
                if (File.Exists(local) && HashFile(local) == oh) plan.Delete.Add(rel);
            }

        // 4) dấu phiên bản + manifest + changelog luôn chép đè (chép SAU CÙNG, rồi kiểm bản phát hành không đổi trong lúc tải)
        foreach (var m in MetaFiles)
        {
            var from = Path.Combine(src, m);
            if (!File.Exists(from)) continue;
            File.Copy(from, Path.Combine(Staging, m), true);
            plan.Copy.Add(m);
        }
        var again = Read(Path.Combine(Staging, StampFileName));
        if (again is null || again.BuiltUtc != remote.BuiltUtc)
            throw new InvalidOperationException("Bản phát hành vừa thay đổi trong lúc tải — bấm Thử lại để tải bản mới nhất.");

        Report(new UpdateProgress("download", need.Count, need.Count, total, total, ""), force: true);
        File.WriteAllText(PlanPath, JsonSerializer.Serialize(plan));
        return plan;
    }

    private static string CopyWithHash(string from, string to, Action<long> onBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var i = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
        using (var o = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buf = new byte[1 << 16];
            int n;
            while ((n = i.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                ih.AppendData(buf, 0, n);
                o.Write(buf, 0, n);
                onBytes(n);
            }
        }
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));   // giữ giờ sửa như bản phát hành (lần sau so cỡ + giờ vẫn khớp)
        return Convert.ToHexString(ih.GetHashAndReset()).ToLowerInvariant();
    }

    // ---------------------------------------------------------------- bản trước / kết quả

    public static BackupInfo? ReadBackup()
    {
        try
        {
            var path = Path.Combine(BackupDir, "backup.json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            if (!string.Equals(S("dst").TrimEnd('\\'), Full(ExeDir), StringComparison.OrdinalIgnoreCase)) return null;   // backup của bản Bcode ở thư mục khác
            var added = r.TryGetProperty("added", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
            var at = DateTime.TryParse(S("at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : File.GetLastWriteTime(path);
            return new BackupInfo(S("from"), S("to"), at, added);
        }
        catch { return null; }
    }

    /// <summary>Đọc kết quả lần cập nhật / quay lại vừa chạy rồi xoá (chỉ báo 1 lần).</summary>
    public static UpdateResult? TakeResult()
    {
        try
        {
            if (!File.Exists(ResultPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(ResultPath));
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var res = new UpdateResult(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, S("mode"), S("from"), S("to"), S("message"));
            File.Delete(ResultPath);
            return res;
        }
        catch { try { File.Delete(ResultPath); } catch { } return null; }
    }

    // ---------------------------------------------------------------- cài / quay lại

    /// <summary>Chạy script cài bản đã chuẩn bị (tiến trình riêng, ẩn) — gọi lúc Bcode ĐÃ đóng (MainForm.FormClosed).</summary>
    /// <param name="overwriteModified">Chép đè cả file người dùng đã sửa ở máy (<see cref="UpdatePlan.Modified"/>).</param>
    /// <param name="relaunch">Xong thì mở lại Bcode (cập nhật ngay) — false khi "cài lúc đóng Bcode".</param>
    public static void StartApply(UpdatePlan plan, bool overwriteModified, bool relaunch)
    {
        var copy = overwriteModified ? plan.Copy.Concat(plan.Modified).ToList() : plan.Copy;
        RunScript(new
        {
            mode = "update", src = Staging, dst = plan.Target, copy, delete = plan.Delete,
            undo = BackupDir, cleanup = Staging, from = plan.FromLabel, to = plan.ToLabel, relaunch,
        });
    }

    /// <summary>Quay lại bản trước: chép trả mọi file trong backup, xoá file lần cập nhật đó thêm vào.</summary>
    public static void StartRollback(BackupInfo backup, bool relaunch = true)
    {
        var files = Path.Combine(BackupDir, "files");
        var copy = Directory.Exists(files) ? Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(files, f)).ToList() : new List<string>();
        RunScript(new
        {
            mode = "rollback", src = files, dst = Full(ExeDir), copy, delete = backup.Added,
            undo = Path.Combine(UpdateRoot, "undo"), cleanup = BackupDir, from = Local.Label, to = backup.From, relaunch,
        });
    }

    private static void RunScript(object job)
    {
        Directory.CreateDirectory(UpdateRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        try { File.Delete(ResultPath); } catch { }
        var full = JsonSerializer.SerializeToElement(job);
        var dict = full.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
        dict["exe"] = Environment.ProcessPath ?? Path.Combine(ExeDir, ExeName);
        dict["log"] = LogPath;
        dict["result"] = ResultPath;
        var jobPath = Path.Combine(UpdateRoot, "job.json");
        var ps1 = Path.Combine(UpdateRoot, "apply.ps1");
        File.WriteAllText(jobPath, JsonSerializer.Serialize(dict), new UTF8Encoding(false));
        File.WriteAllText(ps1, ApplyScript, new UTF8Encoding(true));   // BOM: PowerShell 5.1 đọc đúng tiếng Việt
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\" \"{jobPath}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = UpdateRoot,
        });
    }

    /// <summary>Script cài: chờ mọi tiến trình chạy từ thư mục Bcode thoát (tối đa 15 phút) → sao file sắp ghi đè / xoá vào thư mục undo → chép từ src →
    /// lỗi bất kỳ thì chép trả bản cũ + xoá file mới thêm. Ghi result.json + update.log, dọn thư mục tạm, mở lại Bcode nếu cần.</summary>
    private const string ApplyScript = """
$ErrorActionPreference = 'Stop'
$job = Get-Content -LiteralPath $args[0] -Raw -Encoding UTF8 | ConvertFrom-Json
function L($m) { try { Add-Content -LiteralPath $job.log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $m) -Encoding UTF8 } catch {} }
function Done($ok, $msg) {
  $r = [ordered]@{ ok = $ok; mode = $job.mode; from = $job.from; to = $job.to; message = $msg; at = (Get-Date).ToString('o') }
  try { ($r | ConvertTo-Json) | Set-Content -LiteralPath $job.result -Encoding UTF8 } catch {}
}
function CopyRetry($from, $to) {
  $dir = Split-Path -Parent $to
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  for ($i = 1; ; $i++) {
    try { Copy-Item -LiteralPath $from -Destination $to -Force; return }
    catch { if ($i -ge 10) { throw }; Start-Sleep -Milliseconds 700 }
  }
}
L ('Bắt đầu ' + $job.mode + ': ' + $job.from + ' -> ' + $job.to + ' (' + @($job.copy).Count + ' file chép, ' + @($job.delete).Count + ' file xoá)')

$deadline = (Get-Date).AddMinutes(15)
do {
  $busy = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($job.dst + '\', [StringComparison]::OrdinalIgnoreCase) })
  if ($busy.Count -gt 0) { Start-Sleep -Seconds 1 }
} while ($busy.Count -gt 0 -and (Get-Date) -lt $deadline)
if ($busy.Count -gt 0) {
  $m = 'Hết 15 phút chờ — còn mở: ' + (($busy | ForEach-Object { $_.ProcessName } | Select-Object -Unique) -join ', ') + '. Chưa cài gì, bản cũ giữ nguyên.'
  L $m; Done $false $m; exit 1
}

$undo = $job.undo
$saved = New-Object System.Collections.Generic.List[string]
$added = New-Object System.Collections.Generic.List[string]
try {
  if (Test-Path -LiteralPath $undo) { Remove-Item -LiteralPath $undo -Recurse -Force }
  New-Item -ItemType Directory -Path (Join-Path $undo 'files') -Force | Out-Null
  foreach ($rel in @($job.copy)) {
    $d = Join-Path $job.dst $rel
    if (Test-Path -LiteralPath $d) { CopyRetry $d (Join-Path (Join-Path $undo 'files') $rel); $saved.Add($rel) } else { $added.Add($rel) }
    CopyRetry (Join-Path $job.src $rel) $d
  }
  foreach ($rel in @($job.delete)) {
    $d = Join-Path $job.dst $rel
    if (Test-Path -LiteralPath $d) { CopyRetry $d (Join-Path (Join-Path $undo 'files') $rel); $saved.Add($rel); Remove-Item -LiteralPath $d -Force }
  }
  $info = [ordered]@{ from = $job.from; to = $job.to; dst = $job.dst; at = (Get-Date).ToString('o'); added = @($added) }
  ($info | ConvertTo-Json -Depth 3) | Set-Content -LiteralPath (Join-Path $undo 'backup.json') -Encoding UTF8
  L ('Xong: ' + $job.to)
  Done $true ''
  try { Remove-Item -LiteralPath $job.cleanup -Recurse -Force } catch {}
  if ($job.mode -ne 'update') { try { Remove-Item -LiteralPath $undo -Recurse -Force } catch {} }
} catch {
  $err = $_.Exception.Message
  L ('Lỗi: ' + $err + ' — chép trả bản cũ...')
  foreach ($rel in $saved) { try { CopyRetry (Join-Path (Join-Path $undo 'files') $rel) (Join-Path $job.dst $rel) } catch { L ('  không khôi phục được ' + $rel) } }
  foreach ($rel in $added) { try { Remove-Item -LiteralPath (Join-Path $job.dst $rel) -Force -ErrorAction SilentlyContinue } catch {} }
  try { Remove-Item -LiteralPath $undo -Recurse -Force } catch {}
  Done $false $err
}
if ($job.relaunch) { Start-Process -FilePath $job.exe }
""";
}
