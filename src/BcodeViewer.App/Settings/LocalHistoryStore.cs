using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BcodeViewer.App.Settings;

/// <summary>One saved version of a file.</summary>
/// <param name="Id">The snapshot's file name without extension — also its sort key, since
/// the name is the timestamp it was taken at.</param>
public record HistoryEntry(string Id, DateTime SavedAt, int Length);

/// <summary>
/// Keeps a local copy of what was on disk before each save overwrote it.
///
/// This exists because of a specific, already-demonstrated failure: the source lives on a
/// UNC share that several people edit at once. BcodeViewer can already warn that a file
/// changed underneath you (see editor.js's checkExternalChange), but before this the only
/// two outcomes were "Reload and lose your work" or "Save and lose theirs" — and in the
/// second case their version was simply gone. Now the version that was about to be
/// overwritten is copied here first, so it can be read back and diffed.
///
/// Snapshots are taken of the PRE-WRITE disk content, not of what's being written. That's
/// deliberate: it's the version at risk, one entry per save rather than two, and the
/// newest version is always just the file itself. Your own previous saves end up in here
/// too, because your last save is the pre-write content of your next one.
///
/// Everything here is best-effort. History must never be the reason a save fails, so every
/// operation swallows its own errors (see EditorBridge.SaveWithHistory).
/// </summary>
public static class LocalHistoryStore
{
    /// <summary>Snapshots kept per file. Fifty covers "what did this look like this
    /// morning" without letting one busy file fill %AppData%.</summary>
    private const int MaxSnapshotsPerFile = 50;

    /// <summary>Files bigger than this aren't snapshotted. A 5 MB generated XML saved
    /// fifty times is 250 MB of history for a file nobody hand-edits; the warning banner
    /// still works for those, they just have no history.</summary>
    private const int MaxSnapshotBytes = 5 * 1024 * 1024;

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "history");

    /// <summary>
    /// Records <paramref name="content"/> as a version of <paramref name="path"/>. Silently
    /// does nothing when it can't — a locked history folder is not worth interrupting
    /// someone's save over.
    /// </summary>
    public static void Snapshot(string path, string content)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (Encoding.UTF8.GetByteCount(content) > MaxSnapshotBytes) return;

            var folder = FolderFor(path);
            Directory.CreateDirectory(folder);

            // The original path, stored once per folder — the folder name is a hash, so
            // without this there is no way to tell whose history a folder is when looking
            // at it in Explorer, or to clean up entries for files that no longer exist.
            var pathFile = Path.Combine(folder, "path.txt");
            if (!File.Exists(pathFile)) File.WriteAllText(pathFile, path);

            // Don't record the same content twice in a row: saving repeatedly with no edits
            // in between (Ctrl+S is a habit) would otherwise push the genuinely different
            // older versions out of the fifty-entry window.
            var newest = List(path).FirstOrDefault();
            if (newest is not null && Read(path, newest.Id) == content) return;

            var id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            File.WriteAllText(Path.Combine(folder, id + ".snap"), content);

            Prune(folder);
        }
        catch
        {
            // Out of disk, no permission, path too long — history is a convenience.
        }
    }

    /// <summary>Newest first.</summary>
    public static List<HistoryEntry> List(string path)
    {
        var result = new List<HistoryEntry>();
        try
        {
            var folder = FolderFor(path);
            if (!Directory.Exists(folder)) return result;

            foreach (var file in Directory.EnumerateFiles(folder, "*.snap"))
            {
                var info = new FileInfo(file);
                var id = Path.GetFileNameWithoutExtension(file);
                // The id IS the timestamp, so a snapshot keeps its real time even if the
                // file is later copied to another machine (which resets LastWriteTime).
                var savedAt = DateTime.TryParseExact(id, "yyyyMMdd-HHmmss-fff", null,
                    System.Globalization.DateTimeStyles.None, out var parsed)
                    ? parsed
                    : info.LastWriteTime;
                result.Add(new HistoryEntry(id, savedAt, (int)info.Length));
            }
        }
        catch
        {
            return result;
        }

        return result.OrderByDescending(e => e.Id, StringComparer.Ordinal).ToList();
    }

    public static string? Read(string path, string id)
    {
        try
        {
            // The id comes from the page, so it is checked rather than trusted: without
            // this, an id of "..\..\something" would read a file outside the history folder.
            if (!IsValidId(id)) return null;
            var file = Path.Combine(FolderFor(path), id + ".snap");
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }
        catch
        {
            return null;
        }
    }

    public static string ListJson(string path) => JsonSerializer.Serialize(
        List(path).Select(e => new
        {
            id = e.Id,
            savedAt = e.SavedAt.ToString("dd/MM/yyyy HH:mm:ss"),
            length = e.Length,
        }));

    /// <summary>Folder holding one file's snapshots — named by a hash of the full path
    /// because the path itself contains characters a folder name can't, is far longer than
    /// the limit once nested under %AppData%, and differs only in its last segment between
    /// the many same-named files across projects.</summary>
    public static string FolderFor(string path)
    {
        var normalized = path.Replace('/', '\\').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];
        // Prefixed with the readable file name so the folder is identifiable at a glance;
        // the hash is what actually makes it unique.
        var name = Path.GetFileName(normalized);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return Path.Combine(Root, $"{name}-{hash}");
    }

    private static bool IsValidId(string id) =>
        id.Length is > 0 and <= 32 && id.All(c => char.IsAsciiDigit(c) || c == '-');

    private static void Prune(string folder)
    {
        try
        {
            var snaps = Directory.EnumerateFiles(folder, "*.snap")
                .OrderByDescending(f => Path.GetFileNameWithoutExtension(f), StringComparer.Ordinal)
                .Skip(MaxSnapshotsPerFile)
                .ToList();
            foreach (var old in snaps)
            {
                try { File.Delete(old); } catch { /* in use — next prune gets it */ }
            }
        }
        catch { /* nothing to prune, or folder unreadable */ }
    }
}
