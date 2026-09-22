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
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "settings.json");
            if (!File.Exists(path)) return null;

            var settings = JsonSerializer.Deserialize<BcodeAppSettingsSubset>(File.ReadAllText(path));
            if (settings?.Workspaces is not { Count: > 0 }) return null;

            return settings.Workspaces.FirstOrDefault(w =>
                       string.Equals(w.Name, settings.LastWorkspace, StringComparison.OrdinalIgnoreCase))
                   ?? settings.Workspaces[0];
        }
        catch
        {
            return null; // settings file missing/corrupt — same as "no workspace"
        }
    }

    /// <summary>Just the two members of Bcode.App's AppSettings this needs — System.Text.Json
    /// ignores the rest of the file, so the two apps' settings models stay independent.</summary>
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
    }
}
