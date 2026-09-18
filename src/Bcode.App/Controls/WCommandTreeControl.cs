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

        foreach (var root in roots)
            _tree.Nodes.Add(ToTreeNode(root));

        _tree.ExpandAll();
    }

    private static TreeNode ToTreeNode(WCommandItem item)
    {
        var node = new TreeNode($"{item.Bar}  ({item.WMenuId})") { Tag = item };
        foreach (var child in item.Children.OrderBy(c => c.WMenuId))
            node.Nodes.Add(ToTreeNode(child));
        return node;
    }
}
