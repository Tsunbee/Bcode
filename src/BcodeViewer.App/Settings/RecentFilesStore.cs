using System.Text.Json;

namespace BcodeViewer.App.Settings;

public record RecentFileEntry(string ProjectName, string Path, DateTime LastOpened);

/// <summary>
/// Persisted "files I've opened, grouped by project" — matches FCodeViewer's own left
/// panel (project name header with a count, files listed under it) instead of a plain
/// sibling-folder browser: BcodeViewer runs as its own process launched per-file (from
/// Bcode.App's File Lookup, or standalone), so it has no notion of "workspace" on its
/// own — the project name is whatever the launcher passes in (Bcode.App passes its
/// current Workspace's name; standalone launches fall back to "#Other", same as
/// FCodeViewer's own catch-all group for files opened outside a tracked project).
/// </summary>
public class RecentFilesStore
{
    private const int MaxEntries = 300;
    public List<RecentFileEntry> Entries { get; set; } = new();

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "viewer-recent-files.json");

    public static RecentFilesStore Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var loaded = JsonSerializer.Deserialize<RecentFilesStore>(File.ReadAllText(StorePath));
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // fall through to an empty store if the file is corrupt
        }
        return new RecentFilesStore();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(StorePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Records (or moves to the front of) the given file under the given project,
    /// then trims down to <see cref="MaxEntries"/> oldest-first so the list doesn't grow
    /// forever across every file ever opened.</summary>
    public void Touch(string projectName, string path)
    {
        Entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        Entries.Insert(0, new RecentFileEntry(projectName, path, DateTime.Now));
        if (Entries.Count > MaxEntries)
            Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
        Save();
    }

    /// <summary>Removes one file from the recent list (the left tree's per-file "Remove"
    /// action — the closest equivalent to a tab's old ✕ now that file switching has moved
    /// there instead of a tab strip).</summary>
    public void Remove(string path)
    {
        Entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    /// <summary>Removes every file under one project group (the tree's per-group "Remove
    /// all" action).</summary>
    public void RemoveProject(string projectName)
    {
        Entries.RemoveAll(e => string.Equals(e.ProjectName, projectName, StringComparison.OrdinalIgnoreCase));
        Save();
    }

    /// <summary>Groups by project, each group's files alphabetical by name — projects
    /// ordered by their most-recently-touched file, matching "most relevant project
    /// first" the way FCodeViewer's own list reads.</summary>
    public List<(string ProjectName, List<RecentFileEntry> Files)> GroupedByProject() =>
        Entries
            .GroupBy(e => e.ProjectName)
            .OrderByDescending(g => g.Max(e => e.LastOpened))
            .Select(g => (g.Key, g.OrderBy(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
}
