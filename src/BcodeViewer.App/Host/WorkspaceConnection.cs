using System.Text.Json;
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
    public static string? BuildConnectionString()
    {
        var ws = LoadActiveWorkspace();
        if (ws is null || string.IsNullOrWhiteSpace(ws.Server) || string.IsNullOrWhiteSpace(ws.AppDatabase))
            return null;

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = ws.Server,
            InitialCatalog = ws.AppDatabase,
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
        if (all is not { Count: > 0 } workspaces) return null;

        var lastName = TryReadLastWorkspaceName();
        return workspaces.FirstOrDefault(w =>
                   string.Equals(w.Name, lastName, StringComparison.OrdinalIgnoreCase))
               ?? workspaces[0];
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
        catch { return null; }

        var all = LoadAllWorkspaces();
        if (all is not { Count: > 0 } workspaces) return null;

        return workspaces
            .Where(w => !string.IsNullOrWhiteSpace(w.SourcePath))
            .Select(w => (w.Name, Root: NormalizeRoot(w.SourcePath)))
            .Where(w => w.Root.Length > 0 &&
                        normalizedFile.StartsWith(w.Root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.Root.Length) // most specific (deepest) SourcePath wins
            .Select(w => w.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
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
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "settings.json");

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
    }
}