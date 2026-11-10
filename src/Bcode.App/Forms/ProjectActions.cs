using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>Màn hình Projects có 2 giao diện (WebView2: <see cref="ProjectPickerForm"/>, WinForms: <see cref="NativeProjectPickerForm"/>); phần dữ liệu + thao tác dùng chung nằm ở đây.</summary>
public interface IProjectPicker : IDisposable
{
    Workspace? Chosen { get; }
    DialogResult ShowDialog(IWin32Window? owner);
}

internal static class ProjectActions
{
    public static string IdOf(Workspace w) => string.IsNullOrWhiteSpace(w.ProjectId) ? w.Name : w.ProjectId;

    /// <summary>Thứ tự trong danh sách dự án mở gần đây (0 = mới nhất); không có = int.MaxValue (xếp sau, theo ID).</summary>
    public static int RecentRank(AppSettings s, Workspace w) { var i = s.RecentWorkspaces.IndexOf(w.Name); return i >= 0 ? i : int.MaxValue; }

    /// <summary>Chữ cột "Mở gần nhất": dd/MM/yyyy HH:mm; dự án có trong danh sách gần đây nhưng chưa ghi giờ (bản cũ) = "gần đây".</summary>
    public static string RecentText(AppSettings s, Workspace w) =>
        s.RecentOpenedAt is { } d && d.TryGetValue(w.Name, out var t) ? t.ToString("dd/MM/yyyy HH:mm")
        : s.RecentWorkspaces.Contains(w.Name) ? "gần đây" : "";

    /// <summary>Bỏ 1 dự án khỏi danh sách mở gần đây.</summary>
    public static void ForgetRecent(AppSettings s, Workspace w)
    {
        s.RecentWorkspaces.Remove(w.Name);
        s.RecentOpenedAt?.Remove(w.Name);
        s.Save();
    }

    /// <summary>Chữ để ô lọc màn hình Projects so khớp: chỉ mã dự án (cột ID) — lọc theo cả link/đường dẫn thì gõ "ttp" khớp mọi "http".</summary>
    public static string Haystack(Workspace w) => (IdOf(w) ?? "").ToLowerInvariant();

    /// <summary>Mở đúng giao diện của File &gt; Choose Server (ConnectionSettingsForm) cho New / Edit / Synchronize. allowSync = true
    /// vì đây là màn hình Projects lúc mới mở — nơi duy nhất có "Synchronize (Ctrl+F5)". Đóng mà không Save thì bỏ các thay đổi
    /// chưa lưu (vd dòng WS mới vừa tạo) bằng cách nạp lại danh sách từ file.</summary>
    public static void OpenEditor(IWin32Window owner, AppSettings settings, DbConnectionService connections, Workspace? select = null, bool startNew = false, bool autoSync = false)
    {
        using var form = new ConnectionSettingsForm(settings, connections, allowSync: true, select, startNew, autoSync);
        var result = form.ShowDialog(owner);
        if (result != DialogResult.OK) settings.Workspaces = AppSettings.Load().Workspaces;
    }

    /// <summary>Hỏi rồi xoá project khỏi danh sách; true = đã xoá.</summary>
    public static bool Delete(IWin32Window owner, AppSettings settings, Workspace w)
    {
        if (MessageBox.Show(owner, $"Xoá project \"{IdOf(w)}\" khỏi danh sách?", "Bcode — Projects",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
        settings.Workspaces.Remove(w);
        settings.RecentWorkspaces.Remove(w.Name);
        settings.Save();
        return true;
    }

    public static void CopyInfo(Workspace w)
    {
        // Không đưa mật khẩu vào thông tin copy.
        var text = string.Join(Environment.NewLine, new[]
        {
            $"ID: {IdOf(w)}", $"Server: {w.Server}", $"Sys Data: {w.SysDatabase}", $"App Data: {w.AppDatabase}",
            $"WLoginLink: {w.LoginWLink}", $"Program Path: {w.ProgramPath}", $"Source Path: {w.SourcePath}",
            $"Mobile Path: {w.MobilePath}", $"Working Path: {w.WorkingPath}", $"API Path: {w.ApiPath}", $"Version: {w.VersionCode}", $"DB Access: {w.DbAccess}",
        });
        try { Clipboard.SetText(text); } catch { /* clipboard đang bận */ }
    }
}
