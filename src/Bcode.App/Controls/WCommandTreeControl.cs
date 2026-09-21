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
    // Filter box + Refresh button are now a small WebView2 strip (Web/Shell/wcommandbar.html)
    // — same chrome-vs-content split used throughout: this bar is static/low-data, the tree
    // below (~1600 wcommand rows, lazily materialized — see BeforeExpand) stays 100% native.
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _barWeb = new();
    private string _filterText = "";
    private readonly WCommandService _service;

    public event Action<WCommandItem>? NodeActivated;

    public WCommandTreeControl(WCommandService service)
    {
        _service = service;
        Dock = DockStyle.Fill;

        _barWeb.Dock = DockStyle.Top;
        _barWeb.Height = 34;

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

        // Right-click menu is HTML/CSS (Controls/WebMenu.cs), rebuilt per click so the
        // selection-dependent items (Edit/Delete/Gen Script Menu) get their enabled state from
        // the node that is actually selected at that moment — what the old Opening handler did.
        // Deliberately NOT implemented, per request — need a live FastBusiness runtime:
        // Open Source, Run, Copy Standard Source, Login.
        WebMenu.AttachTo(_tree, () =>
        {
            var hasSelection = _tree.SelectedNode?.Tag is WCommandItem;
            return new WebMenu()
                .Add("New", async () => await NewAsync(), shortcut: "F4")
                .Add("Edit", async () => await EditSelectedAsync(), shortcut: "F3", enabled: hasSelection)
                .Add("Delete", async () => await DeleteSelectedAsync(), shortcut: "F8", enabled: hasSelection, danger: true)
                .AddSeparator()
                .Add("Check WCommand", async () => await CheckWCommandAsync())
                .Add("Gen Script Menu", async () => await GenScriptMenuAsync(), shortcut: "F12", enabled: hasSelection)
                .AddSeparator()
                .Add("Refresh", async () => await ReloadAsync(), shortcut: "F5");
        });

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
                    if (root.GetProperty("action").GetString() == "reload")
                    {
                        _filterText = root.GetProperty("filter").GetString() ?? "";
                        await ReloadAsync();
                    }
                };

                _barWeb.CoreWebView2.NavigationCompleted += (_, _) => PushThemeToBar();
                _barWeb.CoreWebView2.Navigate($"https://{Bcode.App.UI.WebViewEnvironment.Host}/wcommandbar.html");
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

    public async Task ReloadAsync()
    {
        _tree.Nodes.Clear();
        var filter = string.IsNullOrWhiteSpace(_filterText) ? null : _filterText.Trim();

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
