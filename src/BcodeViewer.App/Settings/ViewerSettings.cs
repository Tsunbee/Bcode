using System.Text.Json;

namespace BcodeViewer.App.Settings;

/// <summary>
/// Persisted BcodeViewer configuration — same Load()/Save() JSON-file pattern as
/// Bcode.App's own AppSettings, but its own file so the two apps' settings never collide
/// (BcodeViewer is a separate process with no project reference to Bcode.App).
/// </summary>
public class ViewerSettings
{
    public string AnthropicApiKey { get; set; } = "";
    public string Model { get; set; } = "claude-sonnet-5";

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode");

    private static string SettingsPath => Path.Combine(SettingsDir, "viewer-settings.json");

    public static ViewerSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<ViewerSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // fall through to defaults if the settings file is corrupt
        }

        return new ViewerSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
