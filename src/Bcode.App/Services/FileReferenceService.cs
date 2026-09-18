namespace Bcode.App.Services;

public record FileReferenceMatch(string FilePath, int LineNumber, string LineText);

/// <summary>
/// Backs the "File Reference" tool — finds which OTHER files under App_Data
/// mention/reference a given term (e.g. the link name of the file/menu
/// currently open), as opposed to "File Lookup" which searches by FILE NAME.
/// This is a content grep: "who calls/references this", useful before
/// renaming or changing a shared include/controller file.
/// </summary>
public class FileReferenceService
{
    private static readonly string[] SearchableExtensions =
        { ".f", ".xml", ".aspx", ".ascx", ".vb", ".cs", ".js", ".htm", ".html", ".txt", ".config" };

    public IEnumerable<FileReferenceMatch> FindReferences(string rootPath, string term)
    {
        if (string.IsNullOrWhiteSpace(term) || !Directory.Exists(rootPath))
            yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
                .Where(f => SearchableExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            string[] lines;
            try { lines = File.ReadAllLines(file); }
            catch { continue; } // locked/binary/permission — skip rather than fail the whole search

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                    yield return new FileReferenceMatch(file, i + 1, lines[i].Trim());
            }
        }
    }
}
