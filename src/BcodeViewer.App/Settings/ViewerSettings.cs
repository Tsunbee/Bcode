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
    
    public string GeminiApiKey { get; set; } = "";
    /// <summary>Engine dùng cho gợi ý AI khi gõ (ghost text): "claude" (mặc định) hoặc "gemini".
    /// Chat panel và Ctrl+I không đổi theo mục này — luôn dùng Claude.</summary>
    public string CompletionEngine { get; set; } = "claude";
    /// <summary>
    /// Folder holding the TEAM's shared snippet/template library — a UNC share or a git
    /// working copy, whatever everyone can reach. Read-only as far as BcodeViewer is
    /// concerned: snippets found here show up in IntelliSense and in the Hint Code list
    /// alongside personal ones but can't be edited in place (see HintSnippetStore.Load) —
    /// publishing to it is a deliberate "Export..." step, not a side effect of typing.
    /// Empty = personal library only, which is the pre-existing behaviour.
    ///
    /// Layout inside it:
    ///   *.json            — BcodeViewer's own HintSnippetStore format
    ///   *.code-snippets   — VSCode's snippet format (same file you'd put in .vscode/)
    ///   files\*.*         — whole-file templates for "New from Template"
    /// </summary>
    public string SharedTemplatePath { get; set; } = "";

    /// <summary>
    /// Ghost-text AI completion as you type (see completion.js's inline provider). OFF by
    /// default on purpose: unlike every other suggestion layer, this one fires an HTTP call
    /// to Anthropic without anyone pressing anything, so it costs money per pause-in-typing
    /// and needs an explicit opt-in rather than surprising someone with a bill. Ctrl+I
    /// (explicit, one call per press) works regardless of this setting.
    /// </summary>
    public bool EnableAiCompletion { get; set; } = false;

    /// <summary>
    /// Model for ghost text specifically — deliberately NOT <see cref="Model"/>. Inline
    /// completion is latency-bound (it has to land before the user types the next word) and
    /// tiny (a line or two), which is exactly Haiku's shape; the chat panel's model is
    /// picked for reasoning quality instead.
    /// </summary>
    public string CompletionModel { get; set; } = "claude-haiku-4-5-20251001";

    /// <summary>
    /// Table/column suggestions in .sql files, read from the SAME workspace list Bcode.App
    /// already stores (%AppData%\Bcode\settings.json — see SqlSchemaService). Off means
    /// BcodeViewer never opens a database connection at all, which is how it behaved before
    /// this existed; on is still lazy (nothing connects until a .sql file asks for it).
    /// </summary>
    public bool EnableSqlCompletion { get; set; } = true;

    /// <summary>
    /// Whether "Chạy SQL" may run statements that change data (INSERT/UPDATE/DELETE/EXEC…).
    /// OFF by default, and deliberately not a "remember my answer" checkbox on a prompt:
    /// the editor points at whatever workspace Bcode last selected, which on a support
    /// machine is routinely a live customer database. "Chạy thử (rollback)" needs no
    /// setting and covers the everyday case — seeing how many rows an UPDATE would touch —
    /// so the only thing this unlocks is committing for real.
    /// </summary>
    public bool EnableSqlWrites { get; set; } = false;

    /// <summary>
    /// Names of the XML elements whose content is SQL, comma-separated ("sql,query,select").
    /// An FCode controller is a single .xml document that also carries JavaScript and SQL,
    /// and suggestions have to match whichever one the caret is in (see completion.js's
    /// buildRegions). <c>&lt;script&gt;</c> and <c>&lt;style&gt;</c> are recognised without
    /// configuration because the codebase already treats them as such; nothing here says
    /// which element holds SQL, so rather than inventing a tag name the detector falls back
    /// to reading the content (a block starting with SELECT/UPDATE/EXEC..., or shaped like
    /// FROM...WHERE). Naming the real tags here makes that exact instead of heuristic.
    /// </summary>
    public string SqlRegionTags { get; set; } = "";

    /// <summary>Id of the active theme (see <see cref="UI.ThemeCatalog"/>). An unknown id —
    /// a settings file written by a newer build, or hand-edited — falls back to Dark+ rather
    /// than failing to start.</summary>
    public string ThemeId { get; set; } = UI.ThemeCatalog.DefaultId;

    /// <summary>When set, <see cref="ThemeId"/> is ignored and the theme follows the Windows
    /// app-colour setting, tracking it live. Off by default: someone who deliberately picked
    /// Monokai should not have it swapped out when Windows switches to light at sunset.</summary>
    public bool FollowSystemTheme { get; set; } = false;

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
