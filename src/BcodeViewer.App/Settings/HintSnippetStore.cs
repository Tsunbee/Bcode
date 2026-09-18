using System.Text.Json;

namespace BcodeViewer.App.Settings;

/// <summary>
/// One "Hint Code" entry — matches FCodeViewer's own Hint Code panel fields (Category,
/// Type, Tag/Keywords, Description, the code itself, and who created/last touched it).
/// BcodeViewer keeps its own local library rather than reading FCode's — that data lives
/// in FCode's own fcoderdb.s3db (a SQLite file), which isn't something to reach into.
/// </summary>
public class HintSnippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Category { get; set; } = "JS"; // JS / SQL / XML / CSS
    public string Type { get; set; } = "Declare";
    public string Tags { get; set; } = "";
    public string Description { get; set; } = "";
    public string Code { get; set; } = "";
    public string CreatedBy { get; set; } = Environment.UserName;
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public string ModifiedBy { get; set; } = Environment.UserName;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;
}

public class HintSnippetStore
{
    public List<HintSnippet> Snippets { get; set; } = new();

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "viewer-hints.json");

    public static HintSnippetStore Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var loaded = JsonSerializer.Deserialize<HintSnippetStore>(File.ReadAllText(StorePath));
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // fall through to an empty store if the file is corrupt
        }
        return new HintSnippetStore();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
