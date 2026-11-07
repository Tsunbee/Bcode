using Bcode.App.Controls;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// Cấp cho Hỏi Fast danh sách chức năng THẬT của cửa sổ này (<see cref="BcodeFeatureCatalog"/>): nút công cụ <c>_toolSpecs</c> kèm chỗ đang nằm
/// (thanh công cụ / nhóm Quick Access / Triển khai ▾ / Tool mở rộng ▾ / thanh trên / đang ẩn), mục menu ⚙ Settings và Actions ▾; và mở chức năng
/// khi người dùng bấm "▶ Mở ngay" trên thẻ trả lời. Thêm tool mới vào <c>_toolSpecs</c> là Hỏi Fast tự biết.
/// </summary>
public partial class MainForm
{
    private void InitBcodeFeatureCatalog()
    {
        BcodeFeatureCatalog.LiveProvider = () =>
        {
            if (IsDisposed) return Array.Empty<BcodeFeatureCatalog.LiveItem>();
            return InvokeRequired ? (IReadOnlyList<BcodeFeatureCatalog.LiveItem>)Invoke(new Func<IReadOnlyList<BcodeFeatureCatalog.LiveItem>>(CollectBcodeFeatures)) : CollectBcodeFeatures();
        };
        BcodeFeatureCatalog.Opener = id =>
        {
            if (IsDisposed) return false;
            // không mở form / tab có WebView2 ngay trong handler tin nhắn của WebView2 (trang Hỏi Fast) — để handler trả về rồi mới mở
            BeginInvoke(new Action(() => OpenBcodeFeature(id)));
            return true;
        };
        UI.UiTemplate.Changed += BcodeFeatureCatalog.Invalidate;
        Disposed += (_, _) => UI.UiTemplate.Changed -= BcodeFeatureCatalog.Invalidate;
    }

    /// <summary>Chỗ nằm của 1 nút công cụ theo cách người dùng đang sắp (giống <see cref="RebuildToolsBar"/>).</summary>
    private string ToolLocation(string key, string label)
    {
        if (key.StartsWith("plugin:", StringComparison.Ordinal)) return "Thanh trên → Tool mở rộng ▾ (plugin)";
        if (DeployToolKeys.Contains(key)) return "Thanh trên → Triển khai ▾";
        if (ExtToolKeys.Contains(key)) return "Thanh trên → Tool mở rộng ▾";
        if (TopBarToolKeys.Contains(key)) return $"Thanh trên → nút {label}";
        if (TopToolKeys().Contains(key)) return "Thanh trên (đã kéo lên ở Template → Bố cục)";
        if (_settings.HiddenToolKeys.Contains(key)) return "Đang ẩn khỏi thanh công cụ (★ Quick Access để hiện lại)";
        if (ActiveToolGroups().FirstOrDefault(g => g.Keys.Contains(key)) is { } g) return $"Thanh công cụ → nhóm \"{g.Name} ▾\"";
        return "Thanh công cụ (hàng nút dưới thanh trên)";
    }

    private List<BcodeFeatureCatalog.LiveItem> CollectBcodeFeatures()
    {
        var list = new List<BcodeFeatureCatalog.LiveItem>();
        foreach (var (key, label, _, _) in _toolSpecs)
            list.Add(new("tool:" + key, label, ToolLocation(key, label), UI.ShortcutRegistry.Display("tool:" + key), "tool"));
        try
        {
            foreach (var (path, shortcut, _) in _settingsMenu().Items())
                list.Add(new("menu:settings:" + path, path, "Thanh trên → ⚙ Settings (bánh răng)" + ParentPath(path), shortcut ?? "", "menu"));
        }
        catch { /* menu dựng lỗi → bỏ qua phần này */ }
        try
        {
            using var actions = BuildActionsMenu();
            foreach (var (path, shortcut, _) in MenuStripItems(actions.Items, ""))
                list.Add(new("menu:actions:" + path, path, "Thanh trên → Actions ▾" + ParentPath(path), shortcut, "menu"));
        }
        catch { /* như trên */ }
        return list;
    }

    private static string ParentPath(string path) { var i = path.LastIndexOf(" → ", StringComparison.Ordinal); return i < 0 ? "" : " → " + path[..i]; }

    /// <summary>Mục bấm được của ContextMenuStrip (đi cả menu con): đường dẫn, phím hiển thị, mục.</summary>
    private static IEnumerable<(string Path, string Shortcut, ToolStripMenuItem Item)> MenuStripItems(ToolStripItemCollection items, string prefix)
    {
        foreach (ToolStripItem it in items)
        {
            if (it is not ToolStripMenuItem mi || string.IsNullOrWhiteSpace(mi.Text)) continue;
            var path = prefix + mi.Text.Replace("&", "");
            if (mi.HasDropDownItems) { foreach (var x in MenuStripItems(mi.DropDownItems, path + " → ")) yield return x; continue; }
            yield return (path, mi.ShortcutKeyDisplayString ?? "", mi);
        }
    }

    /// <summary>Mở chức năng theo id của <see cref="BcodeFeatureCatalog"/>: tool:… / sc:… (phím tắt phạm vi App), menu:settings:… / menu:actions:…, help:mục.</summary>
    private void OpenBcodeFeature(string id)
    {
        try
        {
            if (id.StartsWith("help:", StringComparison.Ordinal)) { OpenHelpTab(id[5..]); return; }
            if (id.StartsWith("tool:", StringComparison.Ordinal))
            {
                var spec = _toolSpecs.FirstOrDefault(t => "tool:" + t.key == id);
                if (spec.key is not null) spec.action(this, EventArgs.Empty);
                return;
            }
            if (id.StartsWith("sc:", StringComparison.Ordinal)) { RunAppShortcut(id[3..]); return; }
            if (id.StartsWith("menu:settings:", StringComparison.Ordinal))
            {
                var path = id["menu:settings:".Length..];
                _settingsMenu().Items().FirstOrDefault(i => i.Path == path).OnClick?.Invoke();
                return;
            }
            if (id.StartsWith("menu:actions:", StringComparison.Ordinal))
            {
                var path = id["menu:actions:".Length..];
                using var menu = BuildActionsMenu();
                MenuStripItems(menu.Items, "").FirstOrDefault(i => i.Path == path).Item?.PerformClick();
            }
        }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được chức năng: " + ex.Message, "Bcode — Hỏi Fast"); }
    }
}
