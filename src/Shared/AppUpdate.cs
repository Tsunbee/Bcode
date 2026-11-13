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
/// Nhiều cửa sổ Bcode (nhiều tiến trình): chỉ 1 lượt cài được chạy — khoá update\apply.lock (<see cref="HoldLock"/>, <see cref="OtherLock"/>);
/// cửa sổ khác thấy khoá thì hiện "Đang chờ đóng Bcode để cài bản mới" thay cho nút cập nhật.
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

    // ---------------------------------------------------------------- cache hash file ở máy
    // Bước "So sánh file ở máy" trước đây đọc + tính SHA-256 lại MỌI file đã cài (3.700+ file, phần lớn Templates\fileSource\ không đổi) mỗi lần
    // cập nhật. Cache (đường dẫn → cỡ|giờ sửa|hash) như index của git: cỡ + giờ sửa khớp thì dùng hash đã lưu, không mở file. File bị sửa tay /
    // ghi đè thì giờ sửa đổi → tính lại, nên vẫn phát hiện được. Cache gắn với thư mục cài (Root) — Bcode chuyển chỗ thì bỏ cache cũ.

    private static string HashCachePath => Path.Combine(UpdateRoot, "hashcache.json");

    private sealed class HashCacheFile
    {
        public string Root { get; set; } = "";
        public Dictionary<string, string> Files { get; set; } = new();
    }

    private static Dictionary<string, string> LoadHashCache(string root)
    {
        try
        {
            if (File.Exists(HashCachePath)
                && JsonSerializer.Deserialize<HashCacheFile>(File.ReadAllText(HashCachePath)) is { } c
                && string.Equals(c.Root, root, StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, string>(c.Files, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* hỏng → như chưa có cache */ }
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveHashCache(string root, Dictionary<string, string> files)
    {
        try
        {
            Directory.CreateDirectory(UpdateRoot);
            var tmp = HashCachePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new HashCacheFile { Root = root, Files = files }));
            File.Move(tmp, HashCachePath, true);
        }
        catch { /* không lưu được thì lần sau tính lại — không ảnh hưởng cập nhật */ }
    }

    private static string CacheKey(FileInfo f) => f.Length + "|" + f.LastWriteTimeUtc.Ticks + "|";

    /// <summary>Ghi vào <paramref name="next"/> — trừ file vừa sửa (≤ 2 giây): có thể còn đang được ghi, cùng giờ sửa mà nội dung khác.</summary>
    private static void PutCache(Dictionary<string, string> next, string rel, FileInfo f, string hash)
    {
        if ((DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds > 2) next[rel] = CacheKey(f) + hash;
    }

    /// <summary>SHA-256 của file ở máy, lấy từ <paramref name="cache"/> nếu cỡ + giờ sửa không đổi; kết quả ghi vào <paramref name="next"/>.</summary>
    private static string HashCached(FileInfo f, string rel, Dictionary<string, string> cache, Dictionary<string, string> next)
    {
        var key = CacheKey(f);
        if (cache.TryGetValue(rel, out var v) && v.Length == key.Length + 64 && v.StartsWith(key, StringComparison.Ordinal))
        {
            next[rel] = v;
            return v[key.Length..];
        }
        var h = HashFile(f.FullName);
        PutCache(next, rel, f, h);
        return h;
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

        // staging đang được dùng cho 1 lượt cài đã hẹn (cửa sổ khác, hoặc chính cửa sổ này) → không được xoá / tải lại
        if (OtherLock() is { } other) throw new InvalidOperationException("Đang chờ đóng " + AppName + " để cài bản mới. " + WaitingText(other));
        if (HoldsLock) throw new InvalidOperationException("Đã hẹn cài bản mới khi đóng " + AppName + " — huỷ hẹn trước khi tải lại.");

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
        //    hash file ở máy lấy từ cache khi cỡ + giờ sửa không đổi (xem LoadHashCache) — không đọc lại hàng nghìn file mẫu mỗi lần
        var cache = LoadHashCache(dst);
        var nextCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                var lh = exists ? HashCached(new FileInfo(local), rel, cache, nextCache) : null;
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

        SaveHashCache(dst, nextCache);   // lưu ngay: tải lỗi giữa chừng → "Thử lại" không phải quét lại từ đầu

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
            // trình cài chép staging → thư mục cài giữ nguyên cỡ + giờ sửa (CopyWithHash đặt giờ như bản phát hành) → ghi sẵn vào cache,
            // lần cập nhật sau khỏi đọc lại. Cài không thành / không chép file "đã sửa" thì cỡ|giờ ở máy khác key → tự tính lại.
            if (hash is not null) PutCache(nextCache, rel, new FileInfo(to), hash);
        }

        // 3) file bản cũ có, bản mới bỏ — chỉ khi biết chắc (có cả 2 manifest) và máy chưa sửa file đó
        if (newMan is not null && oldMan is not null)
            foreach (var (rel, oh) in oldMan)
            {
                if (newMan.ContainsKey(rel) || InNested(rel, nested)) continue;
                var local = Path.Combine(dst, rel);
                if (File.Exists(local) && HashCached(new FileInfo(local), rel, cache, new Dictionary<string, string>()) == oh) plan.Delete.Add(rel);
            }
        SaveHashCache(dst, nextCache);

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

    // ---------------------------------------------------------------- khoá cài (update\apply.lock)
    // Mỗi cửa sổ Bcode là 1 tiến trình riêng. Trước đây 2 cửa sổ cùng hẹn cài → 2 script cùng chờ, cùng chạy một lúc trên cùng staging + cùng
    // thư mục backup: script này xoá staging / backup của script kia → "Cannot find path ...\staging\Bcode.Core.dll" rồi chép trả lẫn lộn.
    // Giờ chỉ 1 bên được giữ khoá: cửa sổ hẹn cài giữ từ lúc chọn cài tới lúc chạy script, script giữ suốt lúc chờ + chép.
    // Khoá = file mở độc quyền ghi (FileShare.Read) + DeleteOnClose: tiến trình giữ chết / bị kill → Windows đóng handle, file tự mất → không kẹt.
    // Sót file (mất điện) thì không ai đang mở nên lấy lại được ngay; bên đọc còn xét PID + giờ khởi động tiến trình ghi trong file.

    /// <summary>Người đang giữ khoá cài. By = "app" (cửa sổ đã hẹn cài) / "script" (trình cài); State = scheduled / waiting / installing.</summary>
    public sealed record InstallLock(int Pid, string By, string State, string Mode, string To, DateTime AtUtc);

    public static string LockPath => Path.Combine(UpdateRoot, "apply.lock");
    private static FileStream? _held;
    private static readonly object LockGate = new();

    /// <summary>Cửa sổ này đang giữ khoá (đã hẹn cài / quay lại).</summary>
    public static bool HoldsLock { get { lock (LockGate) return _held is not null; } }

    /// <summary>Giữ khoá cài cho cửa sổ này (lúc chọn "Khởi động lại &amp; cập nhật" / "Cài khi đóng" / "Quay lại bản trước").
    /// false = tiến trình khác (cửa sổ / trình cài khác) đang giữ.</summary>
    public static bool HoldLock(string mode, string to)
    {
        lock (LockGate)
        {
            if (_held is null)
            {
                if (OtherLock() is not null) return false;
                Directory.CreateDirectory(UpdateRoot);
                for (var i = 0; ; i++)
                {
                    try { _held = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.DeleteOnClose); break; }
                    catch (IOException) { return false; }                          // tiến trình khác đang giữ
                    catch (UnauthorizedAccessException) when (i < 10) { Thread.Sleep(100); }   // bên giữ vừa thả, file đang chờ xoá
                    catch (UnauthorizedAccessException) { return false; }
                }
            }
            WriteLock(_held, "app", "scheduled", mode, to);
            return true;
        }
    }

    /// <summary>Thả khoá (huỷ hẹn, đóng bị huỷ, hoặc đã giao cho script).</summary>
    public static void ReleaseLock()
    {
        lock (LockGate) { try { _held?.Dispose(); } catch { } _held = null; }
    }

    private static void WriteLock(FileStream fs, string by, string state, string mode, string to)
    {
        long start;
        using (var me = Process.GetCurrentProcess()) start = me.StartTime.ToUniversalTime().Ticks;
        var json = JsonSerializer.Serialize(new { pid = Environment.ProcessId, start, by, state, mode, to, at = DateTime.UtcNow.ToString("o") });
        var bytes = new UTF8Encoding(false).GetBytes(json);
        fs.SetLength(0); fs.Write(bytes, 0, bytes.Length); fs.Flush(true);
    }

    /// <summary>Khoá cài do tiến trình KHÁC (còn sống) đang giữ — null = không có / của chính cửa sổ này / khoá sót của tiến trình đã chết.
    /// Chỉ mở đọc (không chặn ai lấy khoá); không có file thì gần như không tốn gì — gọi định kỳ được.</summary>
    public static InstallLock? OtherLock()
    {
        try
        {
            if (!File.Exists(LockPath)) return null;
            string text;
            using (var fs = new FileStream(LockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, Encoding.UTF8)) text = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(text);
            var r = doc.RootElement;
            string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            var pid = r.GetProperty("pid").GetInt32();
            var start = r.GetProperty("start").GetInt64();
            if (pid == Environment.ProcessId || !ProcessAlive(pid, start)) return null;
            var at = DateTime.TryParse(S("at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : DateTime.UtcNow;
            return new InstallLock(pid, S("by"), S("state"), S("mode"), S("to"), at);
        }
        catch { return null; }   // đang được ghi / đang chờ xoá / hỏng → coi như không có
    }

    /// <summary>Tiến trình <paramref name="pid"/> còn chạy và đúng là tiến trình đã ghi khoá (giờ khởi động khớp — PID bị tái dùng thì không khớp).</summary>
    private static bool ProcessAlive(int pid, long startTicks)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            try { return Math.Abs(p.StartTime.ToUniversalTime().Ticks - startTicks) < TimeSpan.FromSeconds(2).Ticks; }
            catch { return true; }   // không đọc được giờ khởi động (khác quyền) → coi như còn
        }
        catch { return false; }
    }

    /// <summary>Câu giải thích cho cửa sổ khác thấy khoá: "Đang chờ đóng Bcode để cài bản mới" + chi tiết.</summary>
    public static string WaitingText(InstallLock l)
    {
        var what = l.Mode == "rollback" ? "quay lại bản trước" : "cài bản mới" + (l.To.Length > 0 ? " " + l.To : "");
        return l.State switch
        {
            "installing" => $"Đang {what}...",
            "waiting" => $"Trình cài đang chờ đóng hết các cửa sổ {AppName} để {what}. Lưu các tab đang sửa rồi đóng {AppName} — xong sẽ tự mở lại nếu đã chọn \"Khởi động lại & cập nhật\".",
            _ => $"Một cửa sổ {AppName} khác (PID {l.Pid}) đã hẹn {what}: sẽ cài khi đóng hết các cửa sổ {AppName}.",
        };
    }

    // ---------------------------------------------------------------- cài / quay lại

    /// <summary>Chạy script cài bản đã chuẩn bị (tiến trình riêng, ẩn) — gọi lúc Bcode ĐÃ đóng (MainForm.FormClosed).</summary>
    /// <param name="overwriteModified">Chép đè cả file người dùng đã sửa ở máy (<see cref="UpdatePlan.Modified"/>).</param>
    /// <param name="relaunch">Xong thì mở lại Bcode (cập nhật ngay) — false khi "cài lúc đóng Bcode".</param>
    public static void StartApply(UpdatePlan plan, bool overwriteModified, bool relaunch)
    {
        var copy = overwriteModified ? plan.Copy.Concat(plan.Modified).ToList() : plan.Copy;
        // undo = backup.new: bản sao file cũ của LẦN NÀY; chỉ khi cài xong mới thay backup cũ (keep) — cài lỗi thì "Quay lại bản trước" cũ vẫn còn
        RunScript(new
        {
            mode = "update", src = Staging, dst = plan.Target, copy, delete = plan.Delete,
            undo = Path.Combine(UpdateRoot, "backup.new"), keep = BackupDir, cleanup = Staging, from = plan.FromLabel, to = plan.ToLabel, relaunch,
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
            undo = Path.Combine(UpdateRoot, "undo"), keep = "", cleanup = BackupDir, from = Local.Label, to = backup.From, relaunch,
        });
    }

    /// <summary>Nội dung apply.ps1 (để harness thử chạy script với job + thư mục giả, không cần cài thật).</summary>
    public static string ScriptText => ApplyScript;

    private static void RunScript(object job)
    {
        try
        {
            Directory.CreateDirectory(UpdateRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            try { File.Delete(ResultPath); } catch { }
            var full = JsonSerializer.SerializeToElement(job);
            var dict = full.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value);
            dict["exe"] = Environment.ProcessPath ?? Path.Combine(ExeDir, ExeName);
            dict["log"] = LogPath;
            dict["result"] = ResultPath;
            dict["lock"] = LockPath;
            dict["journal"] = Path.Combine(UpdateRoot, "inprogress.json");
            dict["owner"] = Environment.ProcessId;   // script chờ cửa sổ này thả khoá (thoát) rồi giữ tiếp; khoá của tiến trình khác → bỏ lượt
            // job riêng mỗi lượt: 2 cửa sổ chạy script gần như cùng lúc không ghi đè job.json của nhau
            var jobPath = Path.Combine(UpdateRoot, $"job-{Environment.ProcessId}.json");
            var ps1 = Path.Combine(UpdateRoot, "apply.ps1");
            File.WriteAllText(jobPath, JsonSerializer.Serialize(dict), new UTF8Encoding(false));
            try { File.WriteAllText(ps1, ApplyScript, new UTF8Encoding(true)); }   // BOM: PowerShell 5.1 đọc đúng tiếng Việt
            catch (IOException) when (File.Exists(ps1)) { /* script khác đang đọc apply.ps1 — cùng nội dung, dùng luôn */ }
            Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\" \"{jobPath}\"")
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = UpdateRoot,
            });
        }
        finally { ReleaseLock(); }   // script lấy khoá ngay khi cửa sổ này thả (chờ được tới 15 phút nếu cửa sổ chưa thoát hẳn)
    }

    /// <summary>Script cài. Thứ tự:
    /// 1) Lấy khoá update\apply.lock (độc quyền + DeleteOnClose). Tiến trình khác còn sống giữ → ghi nhật ký "bỏ qua" và thoát, KHÔNG đụng file nào.
    ///    Người giữ là cửa sổ đã gọi script (owner) → chờ nó thoát. Người giữ đã chết → khoá tự hết hiệu lực.
    /// 2) Chờ mọi tiến trình chạy từ thư mục cài thoát (tối đa 15 phút).
    /// 3) Lượt trước bị dừng giữa chừng (còn inprogress.json) → chép trả theo nhật ký của lượt đó trước.
    /// 4) Kiểm đủ mọi file nguồn (staging) — thiếu file thì dừng, chưa đụng gì, bản đang dùng giữ nguyên.
    /// 5) Sao file sắp ghi đè / xoá vào undo (ghi nhật ký từng file) → chép. Lỗi giữa chừng → chép trả bản cũ + xoá file mới thêm.
    /// 6) Xong: backup.new thay backup cũ (chỉ lúc này) → ghi result.json + update.log → thả khoá → mở lại Bcode nếu cần.</summary>
    private const string ApplyScript = """
$ErrorActionPreference = 'Stop'
$job = Get-Content -LiteralPath $args[0] -Raw -Encoding UTF8 | ConvertFrom-Json
function L($m) { try { Add-Content -LiteralPath $job.log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  [' + $PID + '] ' + $m) -Encoding UTF8 } catch {} }
function Done($ok, $msg) {
  $r = [ordered]@{ ok = $ok; mode = $job.mode; from = $job.from; to = $job.to; message = $msg; at = (Get-Date).ToString('o') }
  try { ($r | ConvertTo-Json) | Set-Content -LiteralPath $job.result -Encoding UTF8 } catch {}
}
function CopyRetry($from, $to) {
  $dir = Split-Path -Parent $to
  if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
  for ($i = 1; ; $i++) {
    # thiếu file nguồn: thử lại không ích gì (trước đây mỗi file thiếu tốn ~7 giây) — chỉ thử lại khi file bị khoá / đang dùng
    if (-not (Test-Path -LiteralPath $from -PathType Leaf)) { throw ('Không có file ' + $from) }
    try { Copy-Item -LiteralPath $from -Destination $to -Force; return }
    catch { if ($i -ge 10) { throw }; Start-Sleep -Milliseconds 700 }
  }
}
function Relaunch { if ($job.relaunch) { try { Start-Process -FilePath $job.exe } catch { L ('Không mở lại được ' + $job.exe + ': ' + $_.Exception.Message) } } }

# ---- 1) khoá: chỉ 1 lượt cài / quay lại chạy một lúc
function LockHolder {
  # người đang giữ khoá (còn sống, đúng giờ khởi động) — $null nếu không có / đã chết / đang ghi dở
  try {
    $fs = New-Object IO.FileStream($job.lock, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try { $t = (New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8)).ReadToEnd() } finally { $fs.Dispose() }
    $h = $t | ConvertFrom-Json
    $p = Get-Process -Id ([int]$h.pid) -ErrorAction Stop
    try { if ([math]::Abs($p.StartTime.ToUniversalTime().Ticks - [long]$h.start) -gt 20000000) { return $null } } catch {}
    return $h
  } catch { return $null }
}
function SetLock($state) {
  try {
    $o = [ordered]@{ pid = $PID; start = (Get-Process -Id $PID).StartTime.ToUniversalTime().Ticks; by = 'script'; state = $state; mode = $job.mode; to = $job.to; at = (Get-Date).ToUniversalTime().ToString('o') }
    $b = (New-Object Text.UTF8Encoding($false)).GetBytes(($o | ConvertTo-Json -Compress))
    $script:lock.SetLength(0); $script:lock.Write($b, 0, $b.Length); $script:lock.Flush()
  } catch {}
}
$lock = $null
$deadline = (Get-Date).AddMinutes(15)
$stuck = $null
New-Item -ItemType Directory -Path (Split-Path -Parent $job.lock) -Force | Out-Null
while ($true) {
  try { $lock = New-Object IO.FileStream($job.lock, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read, 4096, [IO.FileOptions]::DeleteOnClose); break } catch {}
  $h = LockHolder
  if ($h -and [int]$h.pid -ne [int]$job.owner) {
    L ('Bỏ qua ' + $job.mode + ' -> ' + $job.to + ': đã có lượt cài khác đang giữ khoá (PID ' + $h.pid + ', ' + $h.by + '/' + $h.state + '). Không đụng file nào.')
    exit 0
  }
  if ($h) { $stuck = $null } elseif (-not $stuck) { $stuck = (Get-Date).AddSeconds(20) }
  if ((Get-Date) -gt $deadline -or ($stuck -and (Get-Date) -gt $stuck)) {
    $m = 'Không lấy được khoá cài ' + $job.lock + '. Chưa cài gì, bản đang dùng giữ nguyên.'
    L $m; Done $false $m; exit 1
  }
  Start-Sleep -Milliseconds 300
}
SetLock 'waiting'
L ('Bắt đầu ' + $job.mode + ': ' + $job.from + ' -> ' + $job.to + ' (' + @($job.copy).Count + ' file chép, ' + @($job.delete).Count + ' file xoá)')

# ---- 2) chờ mọi tiến trình chạy từ thư mục cài thoát
do {
  $busy = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($job.dst + '\', [StringComparison]::OrdinalIgnoreCase) })
  if ($busy.Count -gt 0) { Start-Sleep -Seconds 1 }
} while ($busy.Count -gt 0 -and (Get-Date) -lt $deadline)
if ($busy.Count -gt 0) {
  $m = 'Hết 15 phút chờ — còn mở: ' + (($busy | ForEach-Object { $_.ProcessName } | Select-Object -Unique) -join ', ') + '. Chưa cài gì, bản cũ giữ nguyên.'
  L $m; Done $false $m; $lock.Dispose(); exit 1
}
SetLock 'installing'

# chép trả: file đã sao (S) lấy lại từ undo\files, file mới thêm (A) xoá. Trả về số file không khôi phục được.
function Restore($undoDir, $dst, $savedList, $addedList) {
  $bad = 0
  foreach ($rel in $savedList) { try { CopyRetry (Join-Path (Join-Path $undoDir 'files') $rel) (Join-Path $dst $rel) } catch { $bad++; L ('  không khôi phục được ' + $rel + ': ' + $_.Exception.Message) } }
  foreach ($rel in $addedList) { try { $p = Join-Path $dst $rel; if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force } } catch { $bad++; L ('  không xoá được file mới ' + $rel) } }
  return $bad
}
function ReadJournal($undoDir) {
  $s = New-Object System.Collections.Generic.List[string]; $a = New-Object System.Collections.Generic.List[string]
  $jf = Join-Path $undoDir 'journal.txt'
  if (Test-Path -LiteralPath $jf) {
    foreach ($line in (Get-Content -LiteralPath $jf -Encoding UTF8)) {
      if ($line.Length -lt 3) { continue }
      if ($line[0] -eq 'S') { $s.Add($line.Substring(2)) } elseif ($line[0] -eq 'A') { $a.Add($line.Substring(2)) }
    }
  }
  return ,@($s, $a)
}

# ---- 3) lượt trước bị dừng giữa chừng (tiến trình chết / tắt máy): đưa thư mục cài về như trước lượt đó
if (Test-Path -LiteralPath $job.journal) {
  try {
    $ip = Get-Content -LiteralPath $job.journal -Raw -Encoding UTF8 | ConvertFrom-Json
    $sa = ReadJournal $ip.undo
    L ('Lượt ' + $ip.mode + ' trước (PID ' + $ip.pid + ') bị dừng giữa chừng — chép trả ' + $sa[0].Count + ' file, xoá ' + $sa[1].Count + ' file mới thêm...')
    $bad = Restore $ip.undo $ip.dst $sa[0] $sa[1]
    if ($bad -gt 0) { throw ('còn ' + $bad + ' file chưa khôi phục được') }
    try { Remove-Item -LiteralPath $ip.undo -Recurse -Force } catch {}
    Remove-Item -LiteralPath $job.journal -Force
    L '  đã khôi phục xong.'
  } catch {
    $m = 'Lượt cài trước bị dừng giữa chừng và chưa khôi phục được (' + $_.Exception.Message + '). Chưa cài gì thêm — xem nhật ký.'
    L $m; Done $false $m; $lock.Dispose(); exit 1
  }
}

# ---- 4) kiểm đủ file nguồn TRƯỚC khi đụng vào thư mục cài
$missing = @(@($job.copy) | Where-Object { -not (Test-Path -LiteralPath (Join-Path $job.src $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
  $m = 'Thư mục bản mới ' + $job.src + ' thiếu ' + $missing.Count + ' file (vd ' + $missing[0] + ') — chưa cài gì, bản đang dùng giữ nguyên. Mở lại Cập nhật để tải lại.'
  L $m; Done $false $m; $lock.Dispose(); Relaunch; exit 1
}

# ---- 5) sao lưu + chép (nhật ký từng file trong undo\journal.txt để lượt sau khôi phục được nếu lượt này chết giữa chừng)
$undo = $job.undo
$saved = New-Object System.Collections.Generic.List[string]
$added = New-Object System.Collections.Generic.List[string]
$jf = Join-Path $undo 'journal.txt'
function J($kind, $rel) {
  # antivirus / trình lập chỉ mục có thể đang mở journal.txt → thử lại vài lần thay vì hỏng cả lượt cài
  for ($i = 1; ; $i++) {
    try { Add-Content -LiteralPath $jf -Value ($kind + '|' + $rel) -Encoding UTF8; return }
    catch { if ($i -ge 30) { throw }; Start-Sleep -Milliseconds 100 }
  }
}
try {
  if (Test-Path -LiteralPath $undo) { Remove-Item -LiteralPath $undo -Recurse -Force }
  New-Item -ItemType Directory -Path (Join-Path $undo 'files') -Force | Out-Null
  New-Item -ItemType File -Path $jf -Force | Out-Null
  ([ordered]@{ pid = $PID; mode = $job.mode; undo = $undo; dst = $job.dst; at = (Get-Date).ToString('o') } | ConvertTo-Json) | Set-Content -LiteralPath $job.journal -Encoding UTF8
  foreach ($rel in @($job.copy)) {
    $d = Join-Path $job.dst $rel
    if (Test-Path -LiteralPath $d) { CopyRetry $d (Join-Path (Join-Path $undo 'files') $rel); J 'S' $rel; $saved.Add($rel) } else { J 'A' $rel; $added.Add($rel) }
    CopyRetry (Join-Path $job.src $rel) $d
  }
  foreach ($rel in @($job.delete)) {
    $d = Join-Path $job.dst $rel
    if (Test-Path -LiteralPath $d) { CopyRetry $d (Join-Path (Join-Path $undo 'files') $rel); J 'S' $rel; $saved.Add($rel); Remove-Item -LiteralPath $d -Force }
  }
  $info = [ordered]@{ from = $job.from; to = $job.to; dst = $job.dst; at = (Get-Date).ToString('o'); added = @($added) }
  ($info | ConvertTo-Json -Depth 3) | Set-Content -LiteralPath (Join-Path $undo 'backup.json') -Encoding UTF8
} catch {
  $err = $_.Exception.Message
  L ('Lỗi: ' + $err + ' — chép trả bản cũ...')
  $bad = Restore $undo $job.dst $saved $added
  if ($bad -eq 0) {
    try { Remove-Item -LiteralPath $job.journal -Force -ErrorAction SilentlyContinue } catch {}
    try { Remove-Item -LiteralPath $undo -Recurse -Force } catch {}
    L '  đã chép trả bản cũ.'
    Done $false $err
  } else {
    # giữ undo + inprogress.json: lần cài sau khôi phục tiếp trước khi làm gì khác
    Done $false ($err + ' — còn ' + $bad + ' file chưa khôi phục được, bản sao ở ' + $undo + '. Xem nhật ký.')
  }
  $lock.Dispose(); Relaunch; exit 1
}

# ---- 6) hoàn tất. Thứ tự để chết ở bước nào lượt sau cũng ra trạng thái đúng:
#  update: backup cũ đổi tên .old → undo thành backup → xoá inprogress.json (undo đã dời đi = nhật ký rỗng, lượt sau không chép trả gì) → xoá .old
#  rollback: xoá inprogress.json TRƯỚC rồi mới xoá undo (xoá dở undo mà còn inprogress thì lượt sau chép trả nửa vời)
L ('Xong: ' + $job.to)
if ($job.keep) {
  $old = $job.keep + '.old'
  try {
    if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force }
    if (Test-Path -LiteralPath $job.keep) { Move-Item -LiteralPath $job.keep -Destination $old }
    Move-Item -LiteralPath $undo -Destination $job.keep
  } catch { L ('  không thay được bản sao lưu ' + $job.keep + ': ' + $_.Exception.Message) }
  try { Remove-Item -LiteralPath $job.journal -Force } catch {}
  try { if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Recurse -Force } } catch {}
} else {
  try { Remove-Item -LiteralPath $job.journal -Force } catch {}
  try { Remove-Item -LiteralPath $undo -Recurse -Force } catch {}
}
Done $true ''
try { Remove-Item -LiteralPath $job.cleanup -Recurse -Force } catch {}
try { Remove-Item -LiteralPath $args[0] -Force } catch {}
$lock.Dispose()
Relaunch
""";
}
