namespace Bcode.App.Services;

/// <summary>
/// Backs "Note" / "Note (New)" — small per-workspace scratch notes (not tied
/// to any database, just local text), saved under
/// %AppData%\Bcode\Notes\&lt;workspace&gt;\&lt;note name&gt;.txt so they persist
/// across sessions without needing a database table.
/// </summary>
public class NoteService
{
    public const string DefaultNoteName = "default";

    private static string NotesDir(string workspaceName)
    {
        var safeWs = SanitizeFileName(string.IsNullOrWhiteSpace(workspaceName) ? "_no_workspace" : workspaceName);
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "Notes", safeWs);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public List<string> ListNotes(string workspaceName)
    {
        var dir = NotesDir(workspaceName);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.GetFiles(dir, "*.txt")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string LoadNote(string workspaceName, string noteName)
    {
        var path = Path.Combine(NotesDir(workspaceName), SanitizeFileName(noteName) + ".txt");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public void SaveNote(string workspaceName, string noteName, string content)
    {
        var dir = NotesDir(workspaceName);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SanitizeFileName(noteName) + ".txt");
        File.WriteAllText(path, content);
    }

    public void DeleteNote(string workspaceName, string noteName)
    {
        var path = Path.Combine(NotesDir(workspaceName), SanitizeFileName(noteName) + ".txt");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>A fresh, not-yet-taken note name for "Note (New)" — "Note 1", "Note 2", ...</summary>
    public string SuggestNewNoteName(string workspaceName)
    {
        var existing = new HashSet<string>(ListNotes(workspaceName), StringComparer.OrdinalIgnoreCase);
        var i = 1;
        while (existing.Contains($"Note {i}")) i++;
        return $"Note {i}";
    }
}
