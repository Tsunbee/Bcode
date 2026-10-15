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

    /// <summary>Thứ tự các nhánh dự án trên cây, cố định: mở file của một dự án đã có không
    /// đẩy dự án đó lên đầu nữa; chỉ dự án mới lần đầu xuất hiện mới chèn lên trên cùng.</summary>
    public List<string> ProjectOrder { get; set; } = new();

    private static string StorePath => Path.Combine(
        BcodePaths.AppData, "Bcode", "viewer-recent-files.json");

    public static RecentFilesStore Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var loaded = JsonSerializer.Deserialize<RecentFilesStore>(File.ReadAllText(StorePath));
                if (loaded != null)
                {
                    // File lưu từ bản cũ chưa có ProjectOrder: chốt theo thứ tự đang hiện (gần đây nhất trước).
                    if (loaded.ProjectOrder.Count == 0)
                        loaded.ProjectOrder = loaded.Entries
                            .GroupBy(EffectiveProject, StringComparer.OrdinalIgnoreCase)
                            .OrderByDescending(g => g.Max(e => e.LastOpened))
                            .Select(g => g.Key).ToList();
                    return loaded;
                }
            }
        }
        catch
        {
            // fall through to an empty store if the file is corrupt
        }
        return new RecentFilesStore();
    }

    // Save() được gọi MỖI lần đổi tab (Touch) — ghi đĩa ngay trên luồng giao diện làm Ctrl+Tab khựng. Giờ: chụp JSON ngay (rẻ, đúng trạng thái lúc gọi),
    // ghi ở luồng nền theo hàng đợi 1 chiều; lần ghi đã có bản mới hơn chờ phía sau thì bỏ. Flush() (lúc đóng Viewer) đợi lần ghi cuối xong.
    private static readonly object WriteLock = new();
    private static long _writeSeq;
    private static Task _lastWrite = Task.CompletedTask;

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        var seq = Interlocked.Increment(ref _writeSeq);
        lock (WriteLock)
            _lastWrite = _lastWrite.ContinueWith(_ =>
            {
                if (seq != Interlocked.Read(ref _writeSeq)) return;   // đã có bản mới hơn — để lần sau ghi
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                    var tmp = StorePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, StorePath, overwrite: true);
                }
                catch { /* không ghi được thì giữ bản cũ trên đĩa, danh sách trong RAM vẫn đúng */ }
            }, TaskScheduler.Default);
    }

    /// <summary>Đợi lần ghi nền cuối cùng xong (gọi lúc đóng Viewer để không mất thay đổi vừa có).</summary>
    public static void Flush()
    {
        Task t;
        lock (WriteLock) t = _lastWrite;
        try { t.Wait(3000); } catch { }
    }

    /// <summary>Records (or moves to the front of) the given file under the given project,
    /// then trims down to <see cref="MaxEntries"/> oldest-first so the list doesn't grow
    /// forever across every file ever opened.</summary>
    public void Touch(string projectName, string path)
    {
        Entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        var entry = new RecentFileEntry(projectName, path, DateTime.Now);
        Entries.Insert(0, entry);
        if (Entries.Count > MaxEntries)
            Entries.RemoveRange(MaxEntries, Entries.Count - MaxEntries);
        PruneProjectOrder();
        var project = EffectiveProject(entry);
        if (!ProjectOrder.Contains(project, StringComparer.OrdinalIgnoreCase))
            ProjectOrder.Insert(0, project);
        Save();
    }

    /// <summary>Removes one file from the recent list (the left tree's per-file "Remove"
    /// action — the closest equivalent to a tab's old ✕ now that file switching has moved
    /// there instead of a tab strip).</summary>
    public void Remove(string path)
    {
        Entries.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        PruneProjectOrder();
        Save();
    }

    /// <summary>Dự án không còn file nào thì bỏ khỏi thứ tự — mở lại sau sẽ coi như dự án mới.</summary>
    private void PruneProjectOrder()
    {
        var live = Entries.Select(EffectiveProject).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ProjectOrder.RemoveAll(p => !live.Contains(p));
    }

    /// <summary>Removes every file under one project group (the tree's per-group "Remove
    /// all" action).</summary>
    public void RemoveProject(string projectName)
    {
        Entries.RemoveAll(e => string.Equals(EffectiveProject(e), projectName, StringComparison.OrdinalIgnoreCase));
        PruneProjectOrder();
        Save();
    }

    /// <summary>Project để NHÓM: mục lưu là "#Other" (mở bằng FCode, không có tên project) thì suy lại từ đường dẫn, nên các file
    /// đã mở trước đây cũng về đúng nhánh project mà không cần mở lại.</summary>
    public static string EffectiveProject(RecentFileEntry e) =>
        string.Equals(e.ProjectName, "#Other", StringComparison.OrdinalIgnoreCase)
            ? Host.WorkspaceConnection.InferProjectFromPath(e.Path) ?? e.ProjectName
            : e.ProjectName;

    /// <summary>Groups by project, each group's files alphabetical by name — projects in
    /// <see cref="ProjectOrder"/>, so opening a file never reshuffles the tree. A project
    /// missing from it (e.g. its inferred name changed) goes last, most recent first.</summary>
    public List<(string ProjectName, List<RecentFileEntry> Files)> GroupedByProject() =>
        Entries
            .GroupBy(e => EffectiveProject(e))
            .OrderBy(g =>
            {
                var i = ProjectOrder.FindIndex(p => string.Equals(p, g.Key, StringComparison.OrdinalIgnoreCase));
                return i < 0 ? int.MaxValue : i;
            })
            .ThenByDescending(g => g.Max(e => e.LastOpened))
            .Select(g => (g.Key, g.OrderBy(e => Path.GetFileName(e.Path), StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
}
