using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Left-hand tree for the "SQL Object" tab: tables / views / procedures / functions.
/// Includes a database switcher (Sys Data / App Data) so the same tab can search
/// either of the workspace's two databases, matching FCode's own "switch between
/// databases to filter info" behavior.
/// </summary>
public class SqlObjectTreeControl : UserControl
{
    private readonly TreeView _tree;
    private readonly TextBox _filterBox;
    private readonly ComboBox _dbCombo;
    private readonly SqlObjectBrowserService _service;

    public event Action<SqlObjectInfo>? ObjectActivated;

    public SqlObjectTreeControl(SqlObjectBrowserService service)
    {
        _service = service;
        Dock = DockStyle.Fill;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 54, ColumnCount = 1, RowCount = 2 };

        _dbCombo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        _dbCombo.Items.Add("App Data");
        _dbCombo.Items.Add("Sys Data");
        _dbCombo.SelectedIndex = 0;
        _dbCombo.SelectedIndexChanged += async (_, _) => await ReloadAsync();

        _filterBox = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Lọc theo tên (Enter để tìm)..." };
        _filterBox.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) await ReloadAsync(); };

        top.Controls.Add(_dbCombo, 0, 0);
        top.Controls.Add(_filterBox, 0, 1);

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is SqlObjectInfo obj) ObjectActivated?.Invoke(obj);
        };

        Controls.Add(_tree);
        Controls.Add(top);
    }

    private bool UseSysDatabase => _dbCombo.SelectedIndex == 1;

    public async Task ReloadAsync()
    {
        _tree.Nodes.Clear();
        List<SqlObjectInfo> objects;
        try
        {
            objects = await _service.ListObjectsAsync(UseSysDatabase, string.IsNullOrWhiteSpace(_filterBox.Text) ? null : _filterBox.Text.Trim());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không tải được danh sách object: {ex.Message}", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        foreach (var group in objects.GroupBy(o => o.Kind))
        {
            var groupNode = new TreeNode(GroupLabel(group.Key));
            foreach (var obj in group.OrderBy(o => o.Name))
                groupNode.Nodes.Add(new TreeNode(obj.QualifiedName) { Tag = obj });
            _tree.Nodes.Add(groupNode);
        }
        _tree.ExpandAll();
    }

    private static string GroupLabel(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.Table => "Tables",
        SqlObjectKind.View => "Views",
        SqlObjectKind.StoredProcedure => "Stored Procedures",
        _ => "Functions"
    };
}
