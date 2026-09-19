using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Left-hand menu tree for the WCommand tab: loads the wcommand hierarchy
/// and raises NodeActivated when the user double-clicks a leaf, carrying
/// the underlying WCommandItem (its Link is what File Lookup resolves to
/// an actual source file).
/// </summary>
public class WCommandTreeControl : UserControl
{
    private readonly TreeView _tree;
    private readonly TextBox _filterBox;
    private readonly Button _refreshButton;
    private readonly WCommandService _service;

    public event Action<WCommandItem>? NodeActivated;

    public WCommandTreeControl(WCommandService service)
    {
        _service = service;
        Dock = DockStyle.Fill;

        var top = new Panel { Dock = DockStyle.Top, Height = 28 };
        _filterBox = new TextBox { Dock = DockStyle.Left, Width = 160, PlaceholderText = "wmenu_id LIKE..." };
        _refreshButton = new Button { Dock = DockStyle.Left, Width = 70, Text = "Refresh" };
        _refreshButton.Click += async (_, _) => await ReloadAsync();
        top.Controls.Add(_refreshButton);
        top.Controls.Add(_filterBox);

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
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

    private static TreeNode ToTreeNode(WCommandItem item)
    {
        var node = new TreeNode($"{item.Bar}  ({item.WMenuId})") { Tag = item };
        if (item.Children.Count > 0)
            node.Nodes.Add(new TreeNode("...")); // lazy placeholder — replaced in BeforeExpand
        return node;
    }
}
