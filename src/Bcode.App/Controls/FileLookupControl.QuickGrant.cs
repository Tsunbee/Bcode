using System.Text.Json;
using Bcode.App.Forms;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Cấp source" nhanh ngay ở thanh xem trước (dòng 2, giữa Last Modified và breadcrumb): chọn 1 file .f của Dir / Grid / Filter trên cây →
/// hiện sẵn phiên bản trong kho SourceCollection sẽ lấy source (phiên bản của dự án; không có file thì phiên bản THẤP HƠN gần nhất có file —
/// <see cref="SourceGrantService.ResolveNearestLower"/>) + nút "Cấp source" cấp luôn file đó (hỏi xác nhận trước khi ghi).
/// Bấm vào nhãn = xem file trong kho (<see cref="SourceViewForm"/>). Nút "Cấp source" ở thanh trên vẫn mở hộp thoại như cũ.
/// </summary>
public partial class FileLookupControl
{
    private int _quickGrantVersion;
    private GrantRow? _quickGrantRow;   // kết quả của file đang chọn (null = chưa có / đang tìm / không áp dụng)

    // Danh sách phiên bản của kho (đọc thư mục trên ổ mạng) — dùng chung mọi tab File Lookup, đọc lại sau 5 phút hoặc khi đổi kho.
    private static readonly object VersionCacheLock = new();
    private static (string Key, DateTime At, List<string> Versions)? _versionCache;

    private void ClearQuickGrant()
    {
        ++_quickGrantVersion;
        _quickGrantRow = null;
        PushSource(null);
    }

    private void UpdateQuickGrant(string path)
    {
        var token = ++_quickGrantVersion;
        _quickGrantRow = null;
        var roots = _settings.SourceCollectionRoots;
        if (!path.EndsWith(".f", StringComparison.OrdinalIgnoreCase) || !SourceGrantService.IsGrantable(path)
            || roots.Count == 0 || SourceGrantService.SplitProjectPath(path) is not { } split)
        {
            PushSource(null);
            return;
        }
        // Nhãn ngắn (phiên bản + thư mục); diễn giải đầy đủ để ở tooltip.
        PushSource(new { version = "", text = "Đang tìm source...", kind = "no", tip = string.Join("\n", roots), can = false });

        var dest = split.Root;
        var versionCode = WorkspaceForProject(dest)?.VersionCode;
        Task.Run(() =>
        {
            object state; GrantRow? row = null;
            try
            {
                var versions = CachedVersions(roots);
                var chosen = SourceGrantService.DetectVersion(dest, versions, out var note, versionCode);
                if (chosen is null)
                    state = new { version = "", text = "Chưa rõ phiên bản source", kind = "no", tip = note + "\nBấm Cấp source ở thanh trên để chọn phiên bản.", can = false };
                else
                {
                    row = SourceGrantService.ResolveNearestLower(roots, chosen, versions, path, dest);
                    var destNote = row.DestExists ? "đích đã có (sẽ hỏi ghi đè)" : "đích chưa có";
                    var tip = (row.FromFallback ? $"{chosen} không có file — lấy phiên bản thấp hơn gần nhất {row.FromVersion}\n" : "")
                              + $"Phiên bản dự án: {chosen} ({note})\nNguồn: {row.SourcePath ?? "(không có)"}\nĐích: {row.DestPath} — {destNote}";
                    if (row.SourcePath is null)
                        state = new { version = "", text = "Không có source", kind = "no", tip = $"Không có {Path.GetFileName(row.RelativeSource)} ở {chosen} và các phiên bản thấp hơn\n" + tip, can = false };
                    else
                    {
                        // Lùi phiên bản: chỉ mũi tên ↓ + màu cam, diễn giải nằm ở tooltip.
                        var text = (row.FromFallback ? "↓ · " : "· ") +(Path.GetDirectoryName(row.SourcePath) ?? "") + (row.DestExists ? " · đã có" : "");
                        state = new { version = row.FromVersion, text, kind = row.FromFallback ? "fb" : "ok", tip, can = true };
                    }
                }
            }
            catch (Exception ex)
            {
                state = new { version = "", text = "Lỗi đọc kho source", kind = "no", tip = ex.Message + "\n" + string.Join("\n", roots), can = false };
            }
            if (IsDisposed) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (token != _quickGrantVersion) return; // đã chọn file khác trong lúc tìm
                    _quickGrantRow = row?.SourcePath is null ? null : row;
                    PushSource(state);
                });
            }
            catch (InvalidOperationException) { /* control đã đóng */ }
        });
    }

    private void PushSource(object? state) =>
        _previewBarWeb.Call($"window.setSource && window.setSource({JsonSerializer.Serialize(state)})");

    private static List<string> CachedVersions(IReadOnlyList<string> roots)
    {
        var key = string.Join("|", roots);
        lock (VersionCacheLock)
            if (_versionCache is { } c && c.Key == key && DateTime.Now - c.At < TimeSpan.FromMinutes(5)) return c.Versions;
        var versions = SourceGrantService.ListVersions(roots);
        lock (VersionCacheLock) _versionCache = (key, DateTime.Now, versions);
        return versions;
    }

    /// <summary>Workspace của dự án có gốc site <paramref name="projectRoot"/> (theo Source Path, không thì theo tên dự án đang duyệt) — lấy mã phiên bản đã khai báo.</summary>
    private Workspace? WorkspaceForProject(string projectRoot) =>
        _settings.Workspaces.FirstOrDefault(w => !string.IsNullOrWhiteSpace(w.SourcePath)
            && string.Equals(w.SourcePath.TrimEnd('\\', '/'), projectRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        ?? _settings.Workspaces.FirstOrDefault(w => string.Equals(w.Name, ProjectName, StringComparison.OrdinalIgnoreCase));

    private void ViewQuickGrantSource()
    {
        if (_quickGrantRow is not { SourcePath: { } src } r) return;
        SourceViewForm.Open(src, "Phiên bản " + r.FromVersion + (r.FromFallback ? " (thấp hơn gần nhất)" : "") + " → " + r.RelativeSource, FindForm());
    }

    /// <summary>Nút "Cấp source" ở thanh xem trước: cấp file source đã tìm cho file .f đang chọn — luôn hỏi xác nhận (nói rõ khi sẽ ghi đè).</summary>
    private async void GrantSelectedNow()
    {
        if (_quickGrantRow is not { SourcePath: { } src, DestPath: { } dest } row) return;
        var exists = File.Exists(dest);
        var msg = $"Cấp source cho {Path.GetFileName(row.ProjectFile)}?\n\nTừ (phiên bản {row.FromVersion}{(row.FromFallback ? " — thấp hơn gần nhất có file" : "")}):\n{src}\n\nVào:\n{dest}"
                  + (exists ? "\n\nFile đích ĐÃ CÓ — sẽ bị GHI ĐÈ." : "");
        if (MessageBox.Show(this, msg, "Bcode — Cấp source", MessageBoxButtons.YesNo, exists ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                exists ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) != DialogResult.Yes) return;

        var (copied, _, errors) = await Task.Run(() => SourceGrantService.CopyRows(new[] { row }, overwrite: true));
        if (IsDisposed) return;
        if (copied == 0)
        {
            MessageBox.Show(this, "Không cấp được source:\n" + string.Join("\n", errors), "Bcode — Cấp source", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        // Nạp lại cây (file .xml mới hiện ra) rồi chọn lại file .f vừa cấp — nhãn source làm mới theo ("đích đã có").
        _service.InvalidateCache();
        await ReloadAsync();
        var node = FindTreeNode(_tree.Nodes, row.ProjectFile);
        if (node is not null) { _tree.SelectedNode = node; node.EnsureVisible(); }
        _statusLabel.Text = $"Đã cấp {Path.GetFileName(dest)} từ {row.FromVersion}.";
    }

    private static TreeNode? FindTreeNode(TreeNodeCollection nodes, string fullPath)
    {
        foreach (TreeNode n in nodes)
        {
            if (n.Tag is FileLookupNode { IsDirectory: false } f && string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)) return n;
            if (FindTreeNode(n.Nodes, fullPath) is { } hit) return hit;
        }
        return null;
    }
}
