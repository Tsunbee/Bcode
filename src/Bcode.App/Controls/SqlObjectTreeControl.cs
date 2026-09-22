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
    // DB switcher + name filter are now a small WebView2 strip (Web/Shell/sqlobjectbar.html) —
    // same chrome-vs-content split used throughout: this bar is static/low-data, the tree
    // below (often a few hundred+ nodes) stays 100% native WinForms.
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _barWeb = new();
    private bool _useSysDatabase;
    private string _filterText = "";
    private readonly SqlObjectBrowserService _service;

    public event Action<SqlObjectInfo>? ObjectActivated;

    public SqlObjectTreeControl(SqlObjectBrowserService service)
    {
        _service = service;
        Dock = DockStyle.Fill;

        _barWeb.Dock = DockStyle.Top;
        _barWeb.Height = 64;

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is SqlObjectInfo obj) ObjectActivated?.Invoke(obj);
        };

        Controls.Add(_tree);
        Controls.Add(_barWeb);

        Bcode.App.UI.ThemeManager.ThemeChanged += PushThemeToBar;
        Disposed += (_, _) => Bcode.App.UI.ThemeManager.ThemeChanged -= PushThemeToBar;
        _ = InitBarWebAsync();

        async Task InitBarWebAsync()
        {
            try
            {
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_barWeb);

                _barWeb.CoreWebView2.WebMessageReceived += async (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var root = doc.RootElement;
                    switch (root.GetProperty("action").GetString())
                    {
                        case "db":
                            _useSysDatabase = root.GetProperty("value").GetInt32() == 1;
                            _filterText = root.GetProperty("filter").GetString() ?? "";
                            await ReloadAsync();
                            break;
                        case "reload":
                            _filterText = root.GetProperty("filter").GetString() ?? "";
                            await ReloadAsync();
                            break;
                    }
                };

                _barWeb.CoreWebView2.NavigationCompleted += (_, _) => PushThemeToBar();
                _barWeb.CoreWebView2.Navigate($"https://{Bcode.App.UI.WebViewEnvironment.Host}/sqlobjectbar.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Không khởi tạo được thanh công cụ (dùng WebView2).\nChi tiết lỗi: " + ex.Message,
                    "Bcode — WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void PushThemeToBar()
        {
            if (_barWeb.CoreWebView2 is null) return;
            var isDark = Bcode.App.UI.AppColors.IsDark ? "true" : "false";
            _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
        }
    }

    private bool UseSysDatabase => _useSysDatabase;

    public async Task ReloadAsync()
    {
        _tree.Nodes.Clear();

        // 1. Nếu chưa nhập từ khóa tìm kiếm -> Dừng lại ngay, không query database
        if (string.IsNullOrWhiteSpace(_filterText))
        {
            var hintNode = new TreeNode("Nhập tên đối tượng rồi Enter để tìm kiếm...")
            {
                ForeColor = Color.Gray
            };
            _tree.Nodes.Add(hintNode);
            return;
        }

        // 2. Chỉ query database khi _filterText có dữ liệu
        List<SqlObjectInfo> objects;
        try
        {
            objects = await _service.ListObjectsAsync(UseSysDatabase, _filterText.Trim());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không tải được danh sách object: {ex.Message}", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (objects.Count == 0)
        {
            _tree.Nodes.Add(new TreeNode("Không tìm thấy đối tượng nào khớp.") { ForeColor = Color.Gray });
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
