using System.Text.Json;

namespace Bcode.App.Models;

/// <summary>
/// Persisted app configuration, equivalent to FCode's "Registry Value" menu
/// (NotePadApp, VSApp20xx, SQLProfiler, SQLSMS, WSPath, LibraryPath, FileLookup...).
/// Stored as JSON under %AppData%\Bcode\settings.json instead of the Windows
/// registry, so it is portable and easy to inspect/back up.
/// </summary>
public class AppSettings
{
    public List<Workspace> Workspaces { get; set; } = new();
    public string LastWorkspace { get; set; } = "";

    public string NotePadApp { get; set; } = "notepad.exe";
    public string VSAppPath { get; set; } = "";
    public string SqlProfilerPath { get; set; } = "";
    public string SqlSmsPath { get; set; } = "ssms.exe";
    public string LibraryPath { get; set; } = "";


    /// <summary>Path to BcodeViewer.exe — the standalone Monaco/WebView2-based editor with
    /// an AI chat panel that File Lookup's "Edit" action launches for a file, the same way
    /// VSAppPath/SqlSmsPath launch their own external tools. Empty by default.</summary>
    public string ViewerExePath { get; set; } = "";

    /// <summary>
    /// Keys (see MainForm's tool button list) hidden from the Tools toolbar —
    /// backs the "Quick Access" show/hide customizer so the toolbar doesn't
    /// grow unbounded as more tools are added.
    /// </summary>
    public List<string> HiddenToolKeys { get; set; } = new();

    /// <summary>
    /// Whether the "(nội dung tạm)" bar at the top of a generated script tab
    /// (SQL Object definition, Script Cart view — anything not backed by a real
    /// file) is shown. Off once the user hides it via the ✕ on that bar.
    /// </summary>
    public bool ShowTempContentBar { get; set; } = true;

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // fall through to defaults if the settings file is corrupt
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
