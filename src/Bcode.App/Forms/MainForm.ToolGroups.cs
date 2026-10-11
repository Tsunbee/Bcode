using Bcode.App.Controls;
using Bcode.App.Models;

namespace Bcode.App.Forms;

/// <summary>
/// Nhóm công cụ trên thanh công cụ (Quick Access → tạo nhóm, kéo thả công cụ vào nhóm): thanh chỉ còn các công cụ "ghim" + mỗi nhóm 1 nút "Tên ▾"
/// (menu WebMenu các công cụ trong nhóm) + nút ⌕ mở Command Palette. Phím Alt+1…Alt+5 (ShortcutRegistry "bar.groupN", chỉnh được) thả menu nhóm thứ N.
/// Chưa tạo nhóm nào thì thanh y như cũ. Phím tắt riêng "tool:key" của từng công cụ không đổi.
/// </summary>
public partial class MainForm
{
    private readonly List<ToolStripButton> _toolGroupButtons = new();

    /// <summary>Nhóm đã lưu, đã lọc: chỉ giữ công cụ còn tồn tại trên thanh, mỗi công cụ thuộc tối đa 1 nhóm, bỏ nhóm không tên.</summary>
    private List<ToolBarGroup> ActiveToolGroups()
    {
        var valid = new HashSet<string>(BarToolKeys());
        var used = new HashSet<string>();
        var result = new List<ToolBarGroup>();
        foreach (var g in _settings.ToolBarGroups)
        {
            if (string.IsNullOrWhiteSpace(g.Name)) continue;
            result.Add(new ToolBarGroup { Name = g.Name.Trim(), Keys = g.Keys.Where(k => valid.Contains(k) && used.Add(k)).ToList() });
        }
        return result;
    }

    /// <summary>Gợi ý nhóm cho nút "Gợi ý" của Quick Access — dựa trên các vùng chức năng sẵn có (<see cref="ToolGroups"/>);
    /// SQL Query / Table / Lookup / Command để ghim ngoài thanh.</summary>
    private List<ToolBarGroup> SuggestedToolGroups()
    {
        string[] names = { "SQL", "Đối tượng", "Gói & báo cáo", "Văn bản", "Dự án / FSG" };
        var pinned = new HashSet<string> { "sql_query", "table", "lookup", "command" };
        var bar = BarToolKeys();
        var groups = new List<ToolBarGroup>();
        for (var i = 0; i < ToolGroups.Length && i < names.Length; i++)
            groups.Add(new ToolBarGroup { Name = names[i], Keys = ToolGroups[i].Where(k => bar.Contains(k) && !pinned.Contains(k)).ToList() });
        var rest = bar.Where(k => !pinned.Contains(k) && !groups.Any(g => g.Keys.Contains(k))).ToList();
        if (rest.Count > 0) groups.Add(new ToolBarGroup { Name = "Khác", Keys = rest });
        return groups.Where(g => g.Keys.Count > 0).ToList();
    }

    private void AddToolGroupButtons(List<ToolBarGroup> groups, bool afterPinned)
    {
        _toolGroupButtons.Clear();
        if (afterPinned) _toolsBar.Items.Add(new ToolStripSeparator { Margin = new Padding(6, 2, 6, 2) });
        var compact = Bcode.App.UI.UiTemplate.DensityFactor < 1;
        var pad = new Padding(Math.Max(0, Bcode.App.UI.UiTemplate.Dens(10) - 10), Math.Max(0, Bcode.App.UI.UiTemplate.Dens(6) - 6),
            Math.Max(0, Bcode.App.UI.UiTemplate.Dens(10) - 10), Math.Max(0, Bcode.App.UI.UiTemplate.Dens(6) - 6));
        var margin = compact ? new Padding(1, 0, 1, 0) : new Padding(1, 1, 1, 2);
        for (var i = 0; i < groups.Count; i++)
        {
            var index = i;
            var labels = groups[i].Keys.Where(k => !_settings.HiddenToolKeys.Contains(k))
                .Select(k => _toolSpecs.FirstOrDefault(t => t.key == k).label).Where(l => l is not null);
            var combo = Bcode.App.UI.ShortcutRegistry.Display($"bar.group{i + 1}");
            var button = new ToolStripButton(groups[i].Name + " ▾") { Margin = margin, Padding = pad };
            button.ToolTipText = (combo.Length > 0 ? $"{groups[i].Name} ({combo})" : groups[i].Name) + "\n" + string.Join(", ", labels);
            button.Click += (_, _) => { if (!WebMenu.JustDismissed) ShowToolGroupMenu(index); };   // bấm lần nữa khi menu đang mở = đóng
            _toolGroupButtons.Add(button);
            _toolsBar.Items.Add(button);
        }
        var search = new ToolStripButton("⌕") { Margin = margin, Padding = pad };
        var paletteCombo = Bcode.App.UI.ShortcutRegistry.Display("app.palette");
        search.ToolTipText = paletteCombo.Length > 0 ? $"Tìm công cụ, tab, lệnh… ({paletteCombo})" : "Tìm công cụ, tab, lệnh…";
        search.Click += (_, _) => BeginInvoke(new Action(OpenCommandPalette));
        _toolsBar.Items.Add(search);
    }

    /// <summary>Thả menu nhóm thứ <paramref name="index"/> (0 = nhóm đầu) ngay dưới nút của nhóm. false = không có nhóm đó.</summary>
    private bool ShowToolGroupMenu(int index)
    {
        var groups = ActiveToolGroups();
        if (index < 0 || index >= groups.Count) return false;
        var g = groups[index];
        var menu = new WebMenu().AddCaption(g.Name);
        var any = false;
        foreach (var key in g.Keys)
        {
            if (_settings.HiddenToolKeys.Contains(key)) continue;
            var spec = _toolSpecs.FirstOrDefault(t => t.key == key);
            if (spec.key is null) continue;
            var combo = Bcode.App.UI.ShortcutRegistry.Display("tool:" + key);
            var handler = spec.action;
            menu.Add(spec.label, () => handler(this, EventArgs.Empty), shortcut: combo.Length > 0 ? combo : null);
            any = true;
        }
        if (!any) menu.Add("(Nhóm trống — kéo công cụ vào ở Quick Access)", () => { }, enabled: false);
        menu.AddSeparator().Add("Sửa nhóm… (Quick Access)", () => BeginInvoke(new Action(OpenQuickAccess)));

        var anchor = index < _toolGroupButtons.Count ? _toolGroupButtons[index] : null;
        if (anchor is { Visible: true } && _toolsBar.Visible && _toolsBar.IsHandleCreated)
            menu.Show(this, _toolsBar.PointToScreen(new Point(anchor.Bounds.Left, anchor.Bounds.Bottom)));
        else
            menu.Show(this, Cursor.Position);   // thanh công cụ đang ẩn: thả tại con trỏ chuột
        return true;
    }
}
