using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>"Chọn menu…" của Gen nhanh Menu (menupicker.html, Forms/MenuPickerForm.cs) ở chế độ App: ô lọc + cây WCommand có ô tick.
/// Nhận menuPicker.init({roots, checked, filter}), gửi lên {action:"ok", ids, filter} / {action:"cancel"} như trang web.</summary>
internal sealed class MenuPickerPage : DialogPage
{
    private sealed class Node { public string Id = "", Name = "", SysId = ""; public WCommandItem Probe = new(); public List<Node> Kids = new(); }

    private readonly RoundedInput _filter = NK.Input("Lọc theo tên menu / WMenu ID / link / SysId (gõ không dấu cũng được)...");
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false, ShowNodeToolTips = true };
    private readonly PillButton _ok;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 160 };
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);
    private List<Node> _roots = new();
    private bool _loading;

    public MenuPickerPage()
    {
        NK.ThemeTree(_tree);
        _tree.AfterCheck += OnAfterCheck;
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Accept(); }
            else if (e.KeyCode == Keys.Up && _tree.SelectedNode is { } s && s.PrevVisibleNode is null) { e.Handled = true; _filter.Inner.Focus(); }
        };
        // Double-click 1 menu lá = chọn đúng menu đó rồi gen luôn.
        _tree.NodeMouseDoubleClick += (_, e) => { if (e.Node is { Nodes.Count: 0 } n) { if (!n.Checked) n.Checked = true; Accept(); } };

        _filter.Inner.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Rebuild(); };
        _filter.InputKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down && _tree.Nodes.Count > 0)
            {
                e.Handled = e.SuppressKeyPress = true;
                _tree.Focus(); _tree.SelectedNode ??= FirstLeaf(_tree.Nodes) ?? _tree.Nodes[0];
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (_debounce.Enabled) { _debounce.Stop(); Rebuild(); }
                Accept();
            }
        };
        Disposed += (_, _) => _debounce.Dispose();

        var top = new Panel { Height = NativeSkin.RowHeight(this, 30) + 16, Padding = new Padding(12, 10, 12, 6) };
        _filter.Dock = DockStyle.Fill; top.Controls.Add(_filter);
        var hint = NK.Muted("Tick menu cần gen (tick nhóm = cả nhóm đang hiện). ↓ từ ô lọc xuống cây; Space = tick / bỏ; Enter hoặc double-click 1 menu = Gen Script Menu ngay.", true);
        hint.Dock = DockStyle.Top;
        var body = new Panel { Padding = new Padding(12, 2, 12, 4) };
        body.Controls.Add(_tree); body.Controls.Add(hint);

        _ok = NK.Button("Gen Script Menu", Accept, primary: true);
        Bottom.Controls.Add(NK.Button("Bỏ tick hết", () => { _checked.Clear(); Rebuild(); }));
        Bottom.Controls.Add(NK.Button("Đóng", () => Post(new { action = "cancel" })));
        Bottom.Controls.Add(_ok);
        Layout(top, body);
    }

    public override void FocusPrimary() { _filter.Inner.Focus(); _filter.Inner.SelectAll(); }
    protected override bool HandleKey(Keys key) { if (key == Keys.Escape) { Post(new { action = "cancel" }); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "menuPicker.init") return;
        var d = Arg(a, 0);
        Node Read(JsonElement e) => new()
        {
            Id = P(e, "id"), Name = P(e, "name"), SysId = P(e, "sysId"),
            Probe = new WCommandItem { WMenuId = P(e, "id"), Bar = P(e, "bar"), Bar2 = P(e, "bar2"), Link = P(e, "link") },
            Kids = PA(e, "kids").Select(Read).ToList(),
        };
        _roots = PA(d, "roots").Select(Read).ToList();
        _checked.Clear();
        foreach (var id in PA(d, "checked")) if (id.GetString() is { Length: > 0 } s) _checked.Add(s);
        _loading = true; _filter.Text = P(d, "filter"); _loading = false;
        _debounce.Stop();
        Rebuild();
        FocusPrimary();
    }

    /// <summary>Cùng luật ô lọc cây WCommand (<see cref="WCommandService.Matches"/>): tên Bar / Bar2, link, WMenu ID; thêm SysId như trang web.</summary>
    private static bool Hit(Node n, string filter) =>
        WCommandService.Matches(n.Probe, filter) || (n.SysId.Length > 0 && WCommandService.Matches(new WCommandItem { Bar = n.SysId }, filter));

    private void Rebuild()
    {
        if (_loading) return;
        var filter = _filter.Text.Trim();
        _loading = true;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        // Node hiện khi chính nó khớp (kèm mọi menu con — để tick cả nhóm) hoặc có menu con khớp (chỉ hiện nhánh khớp).
        TreeNode? Build(Node n, bool parentHit)
        {
            var self = parentHit || filter.Length == 0 || Hit(n, filter);
            var kids = n.Kids.Select(k => Build(k, self)).Where(k => k is not null).Cast<TreeNode>().ToArray();
            if (!self && kids.Length == 0) return null;
            var grp = n.Kids.Count > 0;
            var t = new TreeNode(n.Id.Length > 0 ? $"{n.Id}  {n.Name}" : n.Name)
            {
                Tag = n, Checked = _checked.Contains(n.Id), ImageKey = grp ? "folder" : "file", SelectedImageKey = grp ? "folder" : "file",
                ToolTipText = string.Join("\n", new[] { n.Probe.Bar, n.Probe.Bar2, n.SysId.Length > 0 ? "SysId: " + n.SysId : "", n.Probe.Link }.Where(s => !string.IsNullOrWhiteSpace(s))),
            };
            t.Nodes.AddRange(kids);
            return t;
        }
        foreach (var r in _roots) if (Build(r, false) is { } node) _tree.Nodes.Add(node);
        if (filter.Length > 0) _tree.ExpandAll();
        else
        {
            // Không lọc: chỉ mở các nhánh đang chứa menu được tick (mở lại hộp vẫn thấy menu đã chọn).
            bool Open(TreeNode n) { var any = false; foreach (TreeNode c in n.Nodes) any |= Open(c); if (any) n.Expand(); return any || n.Checked; }
            foreach (TreeNode n in _tree.Nodes) Open(n);
        }
        _tree.EndUpdate();
        if (_tree.Nodes.Count > 0) _tree.TopNode = _tree.Nodes[0];
        _loading = false;
        UpdateCount();
    }

    private static TreeNode? FirstLeaf(TreeNodeCollection nodes)
    {
        foreach (TreeNode n in nodes) { if (n.Nodes.Count == 0) return n; if (FirstLeaf(n.Nodes) is { } l) return l; }
        return null;
    }

    // Tick 1 dòng: kéo theo mọi dòng con đang hiện trên cây.
    private void OnAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_loading || e.Node is null) return;
        _loading = true;
        void Set(TreeNode n, bool on)
        {
            n.Checked = on;
            if (n.Tag is Node m) { if (on) _checked.Add(m.Id); else _checked.Remove(m.Id); }
            foreach (TreeNode c in n.Nodes) Set(c, on);
        }
        Set(e.Node, e.Node.Checked);
        _loading = false;
        UpdateCount();
    }

    private void UpdateCount()
    {
        Say(_checked.Count > 0 ? $"Đã tick {_checked.Count} menu" : "Chưa tick menu nào");
        _ok.Text = _checked.Count > 0 ? $"Gen Script Menu ({_checked.Count})" : "Gen Script Menu";
    }

    private void Accept()
    {
        // Chưa tick gì mà đang đứng ở 1 menu → lấy menu đó (gõ lọc, ↓ tới menu, Enter là xong).
        if (_checked.Count == 0 && _tree.SelectedNode?.Tag is Node cur && cur.Id.Length > 0) _checked.Add(cur.Id);
        if (_checked.Count == 0) { Say("Tick ít nhất 1 menu.", "err"); return; }
        var ids = new List<string>();
        void Walk(Node n) { if (_checked.Contains(n.Id)) ids.Add(n.Id); n.Kids.ForEach(Walk); }
        _roots.ForEach(Walk);
        Post(new { action = "ok", ids, filter = _filter.Text.Trim() });
    }
}
