using System.Text.Json;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// Màn hình "Projects" hiện khi mới mở Bcode (và qua Actions &gt; Projects...): gõ để lọc danh sách project đã khai báo,
/// hoặc bấm nhanh 1 project trong dải "Last Access" (các project dùng gần nhất), rồi Enter / nhấp đúp để chọn.
/// Chuột phải một project (ở dải Last Access hoặc ở bảng) → Chọn / Edit / Copy info / Delete.
/// New / Edit / Delete dùng ConnectionSettingsForm (cùng popup Edit Project của Ctrl+F5) và ghi thẳng vào AppSettings.
/// Giao diện là trang WebView2 (Web/Shell/projects.html): lọc và sắp xếp chạy ở phía trang nên danh sách hàng trăm project vẫn mượt;
/// C# chỉ giữ dữ liệu và các thao tác (mở form sửa, xoá, copy).
/// </summary>
public class ProjectPickerForm : WebDialogForm
{
    private readonly AppSettings _settings;
    private readonly DbConnectionService _connections;
    private List<Workspace> _list = new();    // thứ tự đã gửi xuống trang; trang tham chiếu project bằng số thứ tự trong danh sách này

    /// <summary>Project được chọn khi form đóng với DialogResult.OK.</summary>
    public Workspace? Chosen { get; private set; }

    public ProjectPickerForm(AppSettings settings, DbConnectionService connections)
        : base("Projects", "projects.html", 1120, 640, 760, 420)
    {
        _settings = settings;
        _connections = connections;
    }

    private static string IdOf(Workspace w) => string.IsNullOrWhiteSpace(w.ProjectId) ? w.Name : w.ProjectId;

    private static string Haystack(Workspace w) =>
        string.Join(' ', IdOf(w), w.Name, w.Server, w.SysDatabase, w.AppDatabase, w.VersionCode, w.DbAccess, w.LoginWLink, w.ProgramPath, w.SourcePath).ToLowerInvariant();

    protected override void OnReady() => PushData();

    private void PushData()
    {
        _list = _settings.Workspaces.OrderBy(IdOf, StringComparer.OrdinalIgnoreCase).ToList();
        var items = _list.Select((w, i) => new
        {
            i, id = IdOf(w), sys = w.SysDatabase, app = w.AppDatabase, ver = w.VersionCode, wlogin = w.LoginWLink, prog = w.ProgramPath, src = w.SourcePath, hay = Haystack(w),
        });
        var recent = _settings.RecentWorkspaces.Take(10)
            .Select(name => _list.FindIndex(x => x.Name == name))
            .Where(i => i >= 0).Select(i => new { i, id = IdOf(_list[i]) });
        Js($"projects.init({J(new { items, recent })})");
    }

    private Workspace? At(JsonElement msg) =>
        msg.TryGetProperty("i", out var e) && e.TryGetInt32(out var i) && i >= 0 && i < _list.Count ? _list[i] : null;

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        var w = At(msg);
        switch (action)
        {
            case "pick": if (w is not null) { Chosen = w; CloseWith(DialogResult.OK); } break;
            case "new": BeginInvoke(new Action(() => OpenEditor(startNew: true))); break;
            case "edit": if (w is not null) BeginInvoke(new Action(() => OpenEditor(select: w))); break;
            case "sync": BeginInvoke(new Action(() => OpenEditor(w, autoSync: true))); break;     // Ctrl+F5 ở màn hình Projects
            case "delete": if (w is not null) BeginInvoke(new Action(() => DeleteProject(w))); break;
            case "copy": if (w is not null) CopyInfo(w); break;
            case "refresh":
                _settings.Workspaces = AppSettings.Load().Workspaces;
                PushData();
                break;
            case "removeRecent":
                if (w is not null) { _settings.RecentWorkspaces.Remove(w.Name); _settings.Save(); PushData(); }
                break;
            case "ctx":
                if (w is not null) BeginInvoke(new Action(() => ShowContextMenu(w, msg.TryGetProperty("kind", out var kind) && kind.GetString() == "chip")));
                break;
        }
        return Task.CompletedTask;
    }

    /// <summary>Menu chuột phải của 1 project: dải Last Access (có thêm "Bỏ khỏi Last Access") hoặc bảng (có thêm Delete).</summary>
    private void ShowContextMenu(Workspace w, bool chip)
    {
        var menu = new WebMenu()
            .Add("Chọn project này", () => { Chosen = w; CloseWith(DialogResult.OK); })
            .Add("Edit project...", () => BeginInvoke(new Action(() => OpenEditor(select: w))))
            .Add("Copy Project Info", () => CopyInfo(w));
        if (chip) menu.AddSeparator().Add("Bỏ khỏi Last Access", () => { _settings.RecentWorkspaces.Remove(w.Name); _settings.Save(); PushData(); });
        else menu.AddSeparator().Add("Delete", () => BeginInvoke(new Action(() => DeleteProject(w))), danger: true);
        menu.Show(this, Cursor.Position);
    }

    /// <summary>Mở đúng giao diện của File &gt; Choose Server (ConnectionSettingsForm) cho New / Edit / Synchronize. allowSync = true
    /// vì đây là màn hình Projects lúc mới mở — nơi duy nhất có "Synchronize (Ctrl+F5)". Đóng mà không Save thì bỏ các thay đổi
    /// chưa lưu (vd dòng WS mới vừa tạo) bằng cách nạp lại danh sách từ file.</summary>
    private void OpenEditor(Workspace? select = null, bool startNew = false, bool autoSync = false)
    {
        using var form = new ConnectionSettingsForm(_settings, _connections, allowSync: true, select, startNew, autoSync);
        var result = form.ShowDialog(this);
        if (result != DialogResult.OK) _settings.Workspaces = AppSettings.Load().Workspaces;
        PushData();
    }

    private void DeleteProject(Workspace w)
    {
        if (MessageBox.Show(this, $"Xoá project \"{IdOf(w)}\" khỏi danh sách?", "Bcode — Projects",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _settings.Workspaces.Remove(w);
        _settings.RecentWorkspaces.Remove(w.Name);
        _settings.Save();
        PushData();
    }

    private static void CopyInfo(Workspace w)
    {
        // Không đưa mật khẩu vào thông tin copy.
        var text = string.Join(Environment.NewLine, new[]
        {
            $"ID: {IdOf(w)}", $"Server: {w.Server}", $"Sys Data: {w.SysDatabase}", $"App Data: {w.AppDatabase}",
            $"WLoginLink: {w.LoginWLink}", $"Program Path: {w.ProgramPath}", $"Source Path: {w.SourcePath}",
            $"Mobile Path: {w.MobilePath}", $"Working Path: {w.WorkingPath}", $"Version: {w.VersionCode}", $"DB Access: {w.DbAccess}",
        });
        try { Clipboard.SetText(text); } catch { /* clipboard đang bận */ }
    }
}
