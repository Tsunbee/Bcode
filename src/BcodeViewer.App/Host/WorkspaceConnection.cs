using System.Text.Json;
using System.Xml.Linq;
using BcodeViewer.App.Settings;
using Microsoft.Data.SqlClient;

namespace BcodeViewer.App.Host;

/// <summary>
/// Which database BcodeViewer talks to, and how. Reads the workspace Bcode.App last had
/// selected (%AppData%\Bcode\settings.json) rather than keeping a connection of its own:
/// nobody should have to configure the same server twice, and "the WS I picked in Bcode" is
/// what people mean when they say which database they are working against.
///
/// Extracted from <see cref="SqlSchemaService"/> once a second caller appeared
/// (<see cref="SqlRunnerService"/>). Credential handling is the wrong thing to have two
/// copies of: the copies stay identical exactly until one of them is fixed.
///
/// Also the home of <see cref="ResolveProjectName"/> — a third caller, but one that never
/// touches a database at all. It exists because BcodeViewer's own project grouping (see
/// RecentFilesStore's doc comment) depends entirely on a launcher passing a project name as
/// args[1] (see Program.cs's Main), and not every launcher does: FCode's own "open in
/// BcodeViewer" hands over only the file path, so without this every file it opens lands in
/// the "#Other" catch-all even though the path itself is enough to place it — one of Bcode.
/// App's configured workspaces almost certainly has a SourcePath that's an ancestor of it.
/// </summary>
internal static class WorkspaceConnection
{
    /// <summary>Connection string for the active workspace's App database, or null when no
    /// workspace is configured or it is missing a server/database.</summary>
    public static string? BuildConnectionString() => BuildConnectionString(LoadActiveWorkspace(), sys: false);

    /// <summary>Workspace của <paramref name="project"/> (khớp tên trong Bcode, hoặc FolderName trong Config.xml của
    /// FCode); không khớp thì workspace đang chọn.</summary>
    public static BcodeWorkspace? LoadWorkspaceForProject(string? project)
    {
        if (!string.IsNullOrWhiteSpace(project) && LoadAllWorkspaces() is { } all
            && all.FirstOrDefault(w => string.Equals(w.Name, project, StringComparison.OrdinalIgnoreCase)) is { } hit)
            return hit;

        var prev = CurrentProject;
        try { CurrentProject = project; return LoadActiveWorkspace(); }
        finally { CurrentProject = prev; }
    }

    public static string? BuildConnectionString(BcodeWorkspace? ws, bool sys)
    {
        var db = sys ? ws?.SysDatabase : ws?.AppDatabase;
        if (ws is null || string.IsNullOrWhiteSpace(ws.Server) || string.IsNullOrWhiteSpace(db))
            return null;

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = ws.Server,
            InitialCatalog = db,
            TrustServerCertificate = true,
            ConnectTimeout = 8, // matches Bcode.App's Workspace.BuildConnectionString
        };

        if (ws.IntegratedSecurity)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = ws.User;
            builder.Password = ws.Password;
        }

        return builder.ConnectionString;
    }

    /// <summary>"WS name — server / database", for status lines. Null when unconfigured.</summary>
    public static string? Describe()
    {
        var ws = LoadActiveWorkspace();
        return ws is null ? null : $"{ws.Name} — {ws.Server} / {ws.AppDatabase}";
    }

    /// <summary>The workspace Bcode.App last had selected. Re-read on each use rather than
    /// cached, so switching WS over there is picked up here without a restart.</summary>
    public static BcodeWorkspace? LoadActiveWorkspace()
    {
        var all = LoadAllWorkspaces();
        if (all is not { Count: > 0 } workspaces) return LoadFromFcodeConfig();

        var lastName = TryReadLastWorkspaceName();
        return workspaces.FirstOrDefault(w =>
                   string.Equals(w.Name, lastName, StringComparison.OrdinalIgnoreCase))
               ?? workspaces[0];
    }

    /// <summary>Settings của BcodeViewer — MainForm gán lúc khởi động. Cần vì lớp này static mà nguồn
    /// dự phòng (Config.xml của FCode) lại khai trong <see cref="ViewerSettings"/>.</summary>
    public static ViewerSettings? Settings { get; set; }

    /// <summary>Project của file đang mở (MainForm cập nhật mỗi lần đổi file) — chọn đúng dòng
    /// &lt;project&gt; trong Config.xml khi file có nhiều project.</summary>
    public static string? CurrentProject { get; set; }

    /// <summary>
    /// Dự phòng khi Bcode chưa có workspace nào (chưa có settings.json): đọc Config.xml của chính
    /// FCode (đường dẫn khai ở Settings). Chọn &lt;project&gt; có FolderName trùng
    /// <see cref="CurrentProject"/>, không có thì lấy project đầu tiên. Password trong Config.xml bị
    /// FCode mã hoá bằng khoá riêng nên KHÔNG đọc được — dùng mật khẩu khai ở Settings.
    /// </summary>
    private static BcodeWorkspace? LoadFromFcodeConfig()
    {
        var settings = Settings;
        var path = settings?.FcodeConfigXmlPath;
        if (settings is null || string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            var projects = XDocument.Load(path).Descendants()
                .Where(e => e.Name.LocalName.Equals("project", StringComparison.OrdinalIgnoreCase))
                .Select(e => new
                {
                    Name = Child(e, "FolderName") ?? Child(e, "User") ?? "",
                    Server = Child(e, "servername") ?? Child(e, "server") ?? "",
                    User = Child(e, "User") ?? "",
                    Db = Child(e, "AppData") ?? "",
                    Source = Child(e, "SourcePath") ?? Child(e, "ProgramPath") ?? "",
                    Sys = Child(e, "df_Database") ?? "",
                    Login = Child(e, "WLoginLink") ?? "",
                })
                .Where(p => p.Server.Length > 0 && p.Db.Length > 0)
                .ToList();
            if (projects.Count == 0) return null;

            var pick = projects.FirstOrDefault(p => string.Equals(p.Name, CurrentProject, StringComparison.OrdinalIgnoreCase))
                       ?? projects[0];
            return new BcodeWorkspace
            {
                Name = pick.Name,
                Server = pick.Server,
                IntegratedSecurity = false,
                User = pick.User,
                Password = settings.FcodeSqlPassword,
                AppDatabase = pick.Db,
                SourcePath = pick.Source,
                SysDatabase = pick.Sys,
                LoginWLink = pick.Login,
            };
        }
        catch
        {
            return null; // Config.xml hỏng — coi như không có
        }
    }

    private static string? Child(XElement e, string name)
    {
        var v = e.Elements().FirstOrDefault(c => c.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    /// <summary>
    /// Best-effort project name for a bare file path, for a launcher that has no notion of
    /// BcodeViewer's own project grouping and passes nothing beyond the file itself (see
    /// Program.cs's Main — args.Length &gt; 1 is what a project-aware launcher like Bcode.App's
    /// own File Lookup provides; FCode's own launch does not). Matches
    /// <paramref name="filePath"/> against every configured workspace's SourcePath — the
    /// same "Source Path" field Bcode.App's own FileLookupControl.SetRootPath browses under
    /// (App_Data/Controllers/...) — and returns the name of whichever workspace's SourcePath
    /// is the longest (most specific) ancestor of the file. Returns null, not "#Other", when
    /// nothing matches or no workspace has a SourcePath configured at all — the caller
    /// decides the catch-all name, this only ever returns a real match or nothing.
    /// </summary>
    public static string? ResolveProjectName(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        string normalizedFile;
        try { normalizedFile = Path.GetFullPath(filePath); }
        catch { return InferProjectFromPath(filePath); }

        var all = LoadAllWorkspaces();
        if (all is not { Count: > 0 } workspaces) return InferProjectFromPath(filePath);

        return FromWorkspaces(workspaces, normalizedFile) ?? InferProjectFromPath(filePath);
    }

    /// <summary>Suy tên project từ chính đường dẫn khi KHÔNG workspace nào đã khai báo chứa file đó (vd mở file của project
    /// chưa có trong Bcode bằng FCode). Cấu trúc site FastBusiness: &lt;...&gt;\&lt;Project&gt;\&lt;FBISPxxx&gt;\App_Data\... hoặc
    /// \Main\... → project là thư mục đứng TRƯỚC thư mục phiên bản (FBISPxxx). Không nhận ra cấu trúc thì null.</summary>
    public static string? InferProjectFromPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        var parts = filePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 2; i--)
            if (parts[i].Equals("App_Data", StringComparison.OrdinalIgnoreCase) || parts[i].Equals("Main", StringComparison.OrdinalIgnoreCase))
                return parts[i - 2];
        return null;
    }

    private static string? FromWorkspaces(List<BcodeWorkspace> workspaces, string normalizedFile)
    {
        return workspaces
            .Where(w => !string.IsNullOrWhiteSpace(w.SourcePath))
            .Select(w => (w.Name, Root: NormalizeRoot(w.SourcePath)))
            .Where(w => w.Root.Length > 0 &&
                        normalizedFile.StartsWith(w.Root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.Root.Length) // most specific (deepest) SourcePath wins
            .Select(w => w.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    /// <summary>Source root (e.g. \\server\CustomerPro\FBI\KOG\FBISP24) for Clear Structure /
    /// Refresh web.config — callers append App_Data\... / web.config themselves.
    ///
    /// Lấy theo FILE ĐANG MỞ trước: thư mục cha của "App_Data" trong đường dẫn file chính là site
    /// chứa web.config. Trước đây chỉ lấy workspace đang chọn bên Bcode.App, nên khi Viewer mở file
    /// dự án A mà Bcode đang chọn dự án B thì refresh/xoá nhầm sang B (hoặc báo không thấy web.config
    /// khi Source Path của B khai ở thư mục dự án chứ không phải thư mục site). Không có file đang mở
    /// hoặc file không nằm dưới App_Data thì mới dùng workspace của Bcode như cũ.</summary>
    public static (string? Name, string? SourcePath) ResolveSourceRoot(string? activeFilePath = null)
    {
        if (SiteRootFromFile(activeFilePath) is { } siteRoot)
            return (ResolveProjectName(activeFilePath!) ?? Path.GetFileName(siteRoot), siteRoot);

        var ws = LoadActiveWorkspace();
        return string.IsNullOrWhiteSpace(ws?.SourcePath) ? (ws?.Name, null) : (ws.Name, ws.SourcePath);
    }

    /// <summary>Thư mục đứng ngay trên "App_Data" gần nhất trong đường dẫn file, hoặc null.</summary>
    private static string? SiteRootFromFile(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;
        for (var dir = Path.GetDirectoryName(filePath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            if (string.Equals(Path.GetFileName(dir), "App_Data", StringComparison.OrdinalIgnoreCase))
                return Path.GetDirectoryName(dir);
        return null;
    }

    /// <summary>SourcePath as configured can be missing a trailing separator ("D:\Site" vs
    /// "D:\SiteOther") — without normalizing, a StartsWith prefix check would wrongly match
    /// "D:\SiteOther\..." against a workspace rooted at "D:\Site". GetFullPath also resolves
    /// any "..\" and normalizes slash direction so a path typed either way still matches.</summary>
    private static string NormalizeRoot(string root)
    {
        try { return Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar; }
        catch { return ""; }
    }

    private static List<BcodeWorkspace>? LoadAllWorkspaces()
    {
        try
        {
            var path = SettingsPath;
            if (!File.Exists(path)) return null;

            var settings = JsonSerializer.Deserialize<BcodeAppSettingsSubset>(File.ReadAllText(path));
            return settings?.Workspaces is { Count: > 0 } ws ? ws : null;
        }
        catch
        {
            return null; // settings file missing/corrupt — same as "no workspace"
        }
    }

    private static string? TryReadLastWorkspaceName()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return null;
            var settings = JsonSerializer.Deserialize<BcodeAppSettingsSubset>(File.ReadAllText(SettingsPath));
            return settings?.LastWorkspace;
        }
        catch
        {
            return null;
        }
    }

    private static string SettingsPath => Path.Combine(
        BcodePaths.AppData, "Bcode", "settings.json");

    /// <summary>Just the members of Bcode.App's AppSettings/Workspace this needs — System.
    /// Text.Json ignores the rest of the file, so the two apps' settings models stay
    /// independent. SourcePath's name matches Bcode.App's Models/Workspace.cs field exactly
    /// so System.Text.Json's default property-name matching picks it up with no [JsonPropertyName].</summary>
    private sealed class BcodeAppSettingsSubset
    {
        public List<BcodeWorkspace> Workspaces { get; set; } = new();
        public string LastWorkspace { get; set; } = "";
    }

    internal sealed class BcodeWorkspace
    {
        public string Name { get; set; } = "";
        public string Server { get; set; } = "";
        public bool IntegratedSecurity { get; set; } = true;
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
        public string AppDatabase { get; set; } = "";

        /// <summary>"Source Path" in Bcode.App's Edit Project dialog — the web source root
        /// File Lookup browses under (see Bcode.App/Models/Workspace.cs's doc comment). The
        /// one field <see cref="ResolveProjectName"/> needs that the original subset didn't
        /// read at all.</summary>
        public string SourcePath { get; set; } = "";

        /// <summary>Sys Data (wcommand — menu web nằm ở đây, không phải App Data).</summary>
        public string SysDatabase { get; set; } = "";

        /// <summary>"Login WLink" — trang đăng nhập web của project (xem MenuLauncher).</summary>
        public string LoginWLink { get; set; } = "";
    }
}