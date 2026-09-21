using Bcode.App.Forms;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Left-hand menu tree for the WCommand tab: loads the wcommand hierarchy
/// and raises NodeActivated when the user double-clicks a leaf, carrying
/// the underlying WCommandItem (its Link is what File Lookup resolves to
/// an actual source file).
///
/// Right-click menu mirrors FCode's own WCommand context menu, minus the
/// four items that don't apply here (Open Source/Run/Copy Standard Source/
/// Login — those need a live FastBusiness runtime this tool doesn't have):
/// New/Edit/Delete a menu row, "Check WCommand" (flags rows that don't line
/// up between wcommand and command), "Gen Script Menu" (the DELETE/INSERT
/// script for the selected row), and Refresh.
/// </summary>
public class WCommandTreeControl : UserControl
{
    private readonly TreeView _tree;
    private readonly TextBox _filterBox;
    private readonly Button _refreshButton;
    private readonly WCommandService _service;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _editMenuItem;
    private readonly ToolStripMenuItem _deleteMenuItem;
    private readonly ToolStripMenuItem _genScriptMenuItem;

    public event Action<WCommandItem>? NodeActivated;

    public WCommandTreeControl(WCommandService service)
    {
        _service = service;
        Dock = DockStyle.Fill;

        var top = new Panel { Dock = DockStyle.Top, Height = 28 };
        // Was Dock.Left, Width = 160 — the sidebar tab hosting this control sits at a fixed
        // 230px (MainForm's left SplitContainer), and 160 + the Refresh button's 70 adds up
        // to exactly that with no margin for the TabControl's own border/padding, so the
        // right edge of this box was always getting clipped ("wmenu_id LIKE..." cut off).
        // Fill-docked instead: it now shrinks/grows with whatever width the sidebar actually
        // has, same fix as FileLookupControl's Path box below.
        _refreshButton = new Button { Dock = DockStyle.Right, Width = 70, Text = "Refresh" };
        _refreshButton.Click += async (_, _) => await ReloadAsync();
        _filterBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "wmenu_id LIKE..." };
        top.Controls.Add(_filterBox);
        top.Controls.Add(_refreshButton);

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        // "Fcode's lookup bars are smooth — check what makes them not lag." TreeView (like
        // DataGridView, see GridDisplayHelper) doesn't double-buffer itself by default, which
        // shows up as flicker/tearing repainting ~1600 wcommand rows' worth of nodes. This was
        // already the smallest part of the fix here — the real one was the lazy-expansion
        // BeforeExpand handler below — but costs nothing to also turn on.
        Bcode.App.UI.ControlPerf.EnableDoubleBuffering(_tree);
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is WCommandItem item)
                NodeActivated?.Invoke(item);
        };
        // Each group node only gets a single "..." placeholder up front (see ToTreeNode);
        // its real children are materialized here the first time it's expanded, instead
        // of every one of the ~1600 wcommand rows becoming a TreeNode immediately. That's
        // what was actually freezing the UI — not the DB query, but ExpandAll() forcing
        // WinForms to create and lay out every node down to the leaves in one go.
        _tree.BeforeExpand += (_, e) =>
        {
            if (e.Node.Tag is not WCommandItem item) return;
            if (e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Tag is not null) return; // already materialized

            e.Node.Nodes.Clear();
            foreach (var child in item.Children.OrderBy(c => c.WMenuId))
                e.Node.Nodes.Add(ToTreeNode(child));
        };

        // Right-click doesn't select a node on its own in a plain TreeView, so the context
        // menu would open against whatever was selected before (or nothing) instead of the
        // node the user actually right-clicked — hit-test and select it first.
        _tree.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var node = _tree.GetNodeAt(e.Location);
            if (node is not null) _tree.SelectedNode = node;
        };

        _tree.KeyDown += async (_, e) =>
        {
            switch (e.KeyCode)
            {
                case Keys.F3: e.Handled = true; await EditSelectedAsync(); break;
                case Keys.F4: e.Handled = true; await NewAsync(); break;
                case Keys.F8: e.Handled = true; await DeleteSelectedAsync(); break;
                case Keys.F12: e.Handled = true; await GenScriptMenuAsync(); break;
                case Keys.F5: e.Handled = true; await ReloadAsync(); break;
            }
        };

        _menu = new ContextMenuStrip();
        var newItem = new ToolStripMenuItem("New", null, async (_, _) => await NewAsync()) { ShortcutKeyDisplayString = "F4" };
        _editMenuItem = new ToolStripMenuItem("Edit", null, async (_, _) => await EditSelectedAsync()) { ShortcutKeyDisplayString = "F3" };
        _deleteMenuItem = new ToolStripMenuItem("Delete", null, async (_, _) => await DeleteSelectedAsync()) { ShortcutKeyDisplayString = "F8" };
        var checkItem = new ToolStripMenuItem("Check WCommand", null, async (_, _) => await CheckWCommandAsync());
        _genScriptMenuItem = new ToolStripMenuItem("Gen Script Menu", null, async (_, _) => await GenScriptMenuAsync()) { ShortcutKeyDisplayString = "F12" };
        var refreshItem = new ToolStripMenuItem("Refresh", null, async (_, _) => await ReloadAsync()) { ShortcutKeyDisplayString = "F5" };
        // Deliberately NOT implemented, per request — need a live FastBusiness runtime:
        // Open Source, Run, Copy Standard Source, Login.
        _menu.Items.Add(newItem);
        _menu.Items.Add(_editMenuItem);
        _menu.Items.Add(_deleteMenuItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(checkItem);
        _menu.Items.Add(_genScriptMenuItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(refreshItem);
        _menu.Opening += (_, _) =>
        {
            var hasSelection = _tree.SelectedNode?.Tag is WCommandItem;
            _editMenuItem.Enabled = hasSelection;
            _deleteMenuItem.Enabled = hasSelection;
            _genScriptMenuItem.Enabled = hasSelection;
        };
        _tree.ContextMenuStrip = _menu;

        Controls.Add(_tree);
        Controls.Add(top);
    }

    public async Task ReloadAsync()
    {
        _tree.Nodes.Clear();
        var filter = string.IsNullOrWhiteSpace(_filterBox.Text) ? null : _filterBox.Text.Trim();

        List<WCommandItem> roots;
        try
        {
            roots = await _service.LoadTreeAsync(filter);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không tải được wcommand: {ex.Message}", "Bcode",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _tree.BeginUpdate();
        try
        {
            foreach (var root in roots)
                _tree.Nodes.Add(ToTreeNode(root));

            // Nothing is auto-expanded — only the parent (root) menu nodes show up front,
            // collapsed with their "..." placeholder; the user expands a node themselves
            // whenever they actually want to see its child menus.
        }
        finally
        {
            _tree.EndUpdate();
        }
    }

    private WCommandItem? SelectedItem => _tree.SelectedNode?.Tag as WCommandItem;

    private async Task NewAsync()
    {
        // "New" from a right-clicked menu prefills every field with that menu's own data
        // (same parent, same link/sysid/icon/type/...) instead of opening a blank dialog —
        // the user then only has to tweak a couple of things (usually the id and the name)
        // to get a similar new menu, rather than typing all 18 wcommand fields from scratch.
        // (WMenu Id itself gets overwritten again right after with a suggested free id —
        // see WCommandEditForm's own Load handler — since the cloned id is already taken.)
        var template = SelectedItem;
        using var form = new WCommandEditForm(_service, existing: null, template: template);
        if (form.ShowDialog(this) == DialogResult.OK)
            await ReloadAsync();
    }

    private async Task EditSelectedAsync()
    {
        var item = SelectedItem;
        if (item is null) return;

        using var form = new WCommandEditForm(_service, item);
        if (form.ShowDialog(this) == DialogResult.OK)
            await ReloadAsync();
    }

    private async Task DeleteSelectedAsync()
    {
        var item = SelectedItem;
        if (item is null) return;

        var confirm = MessageBox.Show(this,
            $"Xóa menu '{item.Bar}' ({item.WMenuId})?",
            "Bcode — WCommand", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            await _service.DeleteAsync(item);
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — WCommand", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task CheckWCommandAsync()
    {
        try
        {
            var result = await _service.FindDuplicatesAsync();
            using var form = new WCommandDuplicateForm(result);
            form.ShowDialog(this);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Check WCommand", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task GenScriptMenuAsync()
    {
        var item = SelectedItem;
        if (item is null) return;

        var script = WCommandService.GenerateScript(item);
        using var form = new WCommandScriptForm(script, $"Script — {item.WMenuId}");
        form.ShowDialog(this);
        await Task.CompletedTask;
    }

    private static TreeNode ToTreeNode(WCommandItem item)
    {
        var node = new TreeNode($"{item.Bar}  ({item.WMenuId})") { Tag = item };
        if (item.Children.Count > 0)
            node.Nodes.Add(new TreeNode("...")); // lazy placeholder — replaced in BeforeExpand
        return node;
    }
}
