using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Left-hand "File Lookup" tree: browses the UNC source path of the current
/// workspace rooted at App_Data (whatever it contains — layout varies by
/// site, e.g. App_Data/{Include,Request,Structure,Templates}), with an
/// extension filter ("Only Show *.ext") and free-text search, matching the
/// FCode File Lookup tab.
/// </summary>
public class FileLookupControl : UserControl
{
    private readonly TreeView _tree;
    private readonly TextBox _pathBox;
    private readonly TextBox _searchBox;
    private readonly CheckBox _onlyShowFiltered;
    private readonly ComboBox _extensionCombo;
    private readonly Button _goButton;
    private readonly FileLookupService _service;

    public event Action<string>? FileActivated; // full path

    public FileLookupControl(FileLookupService service)
    {
        _service = service;
        Dock = DockStyle.Fill;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 56, ColumnCount = 1, RowCount = 2 };
        var row1 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 26, WrapContents = false };
        _pathBox = new TextBox { Width = 320, PlaceholderText = @"\\server\CustomerPro\...\App_Data" };
        _goButton = new Button { Text = "Load", Width = 50 };
        _goButton.Click += (_, _) => Reload();
        row1.Controls.Add(new Label { Text = "Path:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        row1.Controls.Add(_pathBox);
        row1.Controls.Add(_goButton);

        var row2 = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 26, WrapContents = false };
        _extensionCombo = new ComboBox { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
        _extensionCombo.Items.AddRange(new object[] { ".f", ".xml", ".aspx", ".xlsx", ".rpt" });
        _extensionCombo.SelectedIndex = 0;
        _extensionCombo.SelectedIndexChanged += (_, _) => Reload();
        _onlyShowFiltered = new CheckBox { Text = "Only Show *.ext", Checked = true, AutoSize = true };
        _onlyShowFiltered.CheckedChanged += (_, _) => Reload();
        _searchBox = new TextBox { Width = 140, PlaceholderText = "Search..." };
        _searchBox.TextChanged += (_, _) => Reload();
        row2.Controls.Add(_extensionCombo);
        row2.Controls.Add(_onlyShowFiltered);
        row2.Controls.Add(_searchBox);

        top.Controls.Add(row1, 0, 0);
        top.Controls.Add(row2, 0, 1);

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is FileLookupNode { IsDirectory: false } node)
                FileActivated?.Invoke(node.FullPath);
        };

        Controls.Add(_tree);
        Controls.Add(top);
    }

    public void SetRootPath(string path)
    {
        _pathBox.Text = path;
        Reload();
    }

    /// <summary>
    /// Used when a WCommand menu node is activated: searches every extension
    /// (not just the current filter) for the given term and expands every match,
    /// so the user sees all source files related to that menu item at once.
    /// </summary>
    public void SearchFor(string term)
    {
        _onlyShowFiltered.Checked = false;
        _searchBox.Text = term;
        Reload();
    }

    public void Reload()
    {
        _tree.Nodes.Clear();
        if (string.IsNullOrWhiteSpace(_pathBox.Text)) return;

        var root = _service.BuildTree(
            _pathBox.Text.Trim(),
            _extensionCombo.SelectedItem?.ToString() ?? ".f",
            string.IsNullOrWhiteSpace(_searchBox.Text) ? null : _searchBox.Text.Trim(),
            _onlyShowFiltered.Checked);

        _tree.Nodes.Add(ToTreeNode(root));
        if (!string.IsNullOrWhiteSpace(_searchBox.Text))
            _tree.ExpandAll();
        else
            _tree.Nodes[0].Expand();
    }

    private static TreeNode ToTreeNode(FileLookupNode node)
    {
        var treeNode = new TreeNode(node.Name) { Tag = node };
        foreach (var child in node.Children)
            treeNode.Nodes.Add(ToTreeNode(child));
        return treeNode;
    }
}
