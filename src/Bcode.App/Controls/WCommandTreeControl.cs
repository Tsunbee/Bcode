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
    private readonly FileLookupService _fileLookupService;
    private readonly Func<Workspace?> _getCurrentWorkspace;
    public event Action<WCommandItem>? NodeActivated;

    /// <summary>Bắn khi bấm F4 (hoặc menu "Mở File Lookup mới"): mở thêm 1 tab File Lookup riêng cho menu đang chọn.</summary>
    public event Action<WCommandItem>? NewLookupRequested;

    /// <summary>Menu "Thiết kế màn hình": mở Screen Designer (exe riêng) ngay controller của menu đang chọn.</summary>
    public event Action<WCommandItem>? DesignRequested;

    private void OpenInNewLookup()
    {
        if (SelectedItem is { } item) NewLookupRequested?.Invoke(item);
    }

    private readonly AppSettings? _settings;

    public WCommandTreeControl(WCommandService service, FileLookupService fileLookupService, Func<Workspace?> getCurrentWorkspace, AppSettings? settings = null)
    {
        _settings = settings;
        _service = service;
        _fileLookupService = fileLookupService;
        _getCurrentWorkspace = getCurrentWorkspace;
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
        // Đang mở hộp thoại New/Edit mà bấm sang menu khác → hiện thông tin menu đó lên hộp thoại (không phải đóng rồi mở lại).
        _tree.AfterSelect += (_, e) =>
        {
            if (_appEditForm is { IsDisposed: false } appOpen && e.Node?.Tag is WCommandItem { IsAppCommand: true } appPicked)
                _ = appOpen.SwitchToAsync(appPicked.MenuId);
            else if (_editForm is { IsDisposed: false } open && e.Node?.Tag is WCommandItem picked && !picked.IsAppCommand)
                _ = open.SwitchToAsync(picked);
        };
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            // Chỉ bắn NodeActivated cho LÁ THẬT (Children rỗng) — double-click vào 1 menu
            // nhóm/menu cha (Children.Count > 0) đã tự nhiên đóng/mở nhánh (hành vi mặc định
            // của TreeView), không cần làm gì thêm. Thiếu điều kiện này thì double-click 1
            // menu cha vẫn bắn NodeActivated, khiến MainForm.OpenWCommandItem chạy tiếp và
            // hiện cảnh báo "không có Link/SysId gắn với source" — đúng về mặt dữ liệu
            // (menu cha/nhóm quả thật không có Link/SysId) nhưng vô nghĩa vì đó là chuyện
            // BÌNH THƯỜNG theo đúng thiết kế, không phải lỗi dữ liệu cần báo. Cảnh báo đó giờ
            // chỉ còn hiện đúng lúc cần: 1 LÁ thật sự thiếu Link/SysId (dữ liệu bị thiếu thật).
            if (e.Node?.Tag is WCommandItem item && item.Children.Count == 0)
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
        _tree.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var node = _tree.GetNodeAt(e.Location);
            if (node is not null) _tree.SelectedNode = node;
        };

        _tree.KeyDown += async (_, e) =>
        {
            // F4 / Insert cấu hình được (Template → Phím tắt, nhóm "Cây menu WCommand"): F4 mở thêm tab File Lookup, Insert = New.
            var combo = Bcode.App.UI.ShortcutRegistry.FromKeys(e.KeyData);
            if (combo is not null && combo == Bcode.App.UI.ShortcutRegistry.Get("wcommand.newLookup")) { e.Handled = true; OpenInNewLookup(); return; }
            if (combo is not null && combo == Bcode.App.UI.ShortcutRegistry.Get("wcommand.new")) { e.Handled = true; await NewAsync(); return; }
            switch (e.KeyCode)
            {
                case Keys.F3: e.Handled = true; await EditSelectedAsync(); break;
                case Keys.F8: e.Handled = true; await DeleteSelectedAsync(); break;
                case Keys.F12: e.Handled = true; await GenScriptMenuAsync(); break;
                case Keys.F5: e.Handled = true; await ReloadAsync(force: true); break;
                // Copies the full breadcrumb of the selected menu ("Phải thu \ Tạo hóa đơn
                // bán hàng từ Haravan (C)") to the clipboard — handy for pasting into a chat
                // or ticket instead of re-typing which menu something is under.
                case Keys.C when e.Control: e.Handled = true; CopySelectedFullPath(); break;
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
                .Add("New", async () => await NewAsync(), shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.new"))
                .Add("Edit", async () => await EditSelectedAsync(), shortcut: "F3", enabled: hasSelection)
                .Add("Delete", async () => await DeleteSelectedAsync(), shortcut: "F8", enabled: hasSelection, danger: true)
                .AddSeparator()
                .Add("Thiết kế màn hình (Screen Designer)", () => { if (SelectedItem is { } d && !string.IsNullOrWhiteSpace(d.SysId)) DesignRequested?.Invoke(d); }, enabled: _tree.SelectedNode?.Tag is WCommandItem { SysId.Length: > 0 })
                .Add("Mở File Lookup mới", OpenInNewLookup, shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.newLookup"), enabled: hasSelection)
                .Add("Run...", RunSelectedMenu, shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.run"), enabled: hasSelection)
                .Add("Copy source standard", async () => await CopySourceStandardAsync(), enabled: hasSelection)
                .Add("Browse Source Folder", BrowseSourceFolder, enabled: _tree.SelectedNode?.Tag is WCommandItem { IsAppCommand: true })
                .Add("Cấp source (Add Source)...", ShowAddSource, enabled: hasSelection && _settings is not null)
                .AddSeparator()
                .Add("Check WCommand", async () => await CheckWCommandAsync())
                .Add("Gen Script Menu", async () => await GenScriptMenuAsync(), shortcut: "F12", enabled: hasSelection)
                .AddSeparator()
                .Add("Refresh", async () => await ReloadAsync(force: true), shortcut: "F5");
        });

        Controls.Add(_tree);
        Controls.Add(_barWeb);

        Bcode.App.UI.ThemeManager.ThemeChanged += PushThemeToBar;
        Disposed += (_, _) => Bcode.App.UI.ThemeManager.ThemeChanged -= PushThemeToBar;
        // Đổi theme sáng/tối thì tô lại màu phân hệ cho hợp nền.
        Action recolor = RecolorNodes;
        Bcode.App.UI.ThemeManager.ThemeChanged += recolor;
        Disposed += (_, _) => Bcode.App.UI.ThemeManager.ThemeChanged -= recolor;
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
                _barWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("wcommandbar.html"));
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

    // Chỉ lần tải MỚI NHẤT được phép đổ vào cây: đổi workspace nhanh (hoặc F5 liên tiếp) tạo nhiều lần tải
    // chồng nhau; lần cũ về sau trước đây vẫn thêm node vào cây → menu bị nhân đôi / lẫn menu WS cũ.
    private int _reloadVersion;

    public async Task ReloadAsync(bool force = false)
    {
        var version = ++_reloadVersion;
        _tree.Nodes.Clear();
        var filter = string.IsNullOrWhiteSpace(_filterText) ? null : _filterText.Trim();

        // Có bản lưu trên máy (và đang không lọc): hiện cây NGAY từ đó, rồi hỏi database ngầm — giống nhau thì giữ nguyên, khác (hoặc database không với tới được) thì xử lý bên dưới.
        var cachedFlat = filter is null ? _service.LoadCachedFlat() : null;
        var cacheSignature = cachedFlat is { Count: > 0 } ? WCommandService.SignatureOf(cachedFlat) : null;
        if (cacheSignature is not null)
        {
            var cachedRoots = _service.TreeFromFlat(cachedFlat!);
            _rootsAll = cachedRoots;
            RenderRoots(cachedRoots, null);
        }

        // Bản lưu còn đúng (chữ ký nhẹ do database tính trùng) → giữ cây đang hiện, khỏi tải cả bảng wcommand.
        if (!force && cacheSignature is not null && await _service.IsCacheCurrentAsync()) return;
        if (version != _reloadVersion) return;

        List<WCommandItem> roots;
        try
        {
            roots = await _service.LoadTreeAsync(filter);
        }
        catch (Exception ex)
        {
            // Lần tải đã bị thay thế thì không báo lỗi: đổi WS nhanh trước đây để lại cả chồng hộp thoại.
            if (version != _reloadVersion) return;
            if (cacheSignature is not null) return;      // đang hiện bản lưu (vd mất mạng) — giữ nguyên, không làm phiền
            MessageBox.Show(this, $"Không tải được wcommand: {ex.Message}", "Bcode",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (version != _reloadVersion) return;
        if (cacheSignature is not null && _service.LastSignature == cacheSignature) return;   // database giống bản lưu — cây đang hiện đã đúng
        if (cacheSignature is not null) _tree.Nodes.Clear();                                    // khác: dựng lại theo bản mới
        if (filter is null) _rootsAll = roots;   // bản đầy đủ (không lọc) — Command Palette tìm menu trong đây
        RenderRoots(roots, filter);
    }

    private void RenderRoots(List<WCommandItem> roots, string? filter)
    {

        _tree.BeginUpdate();
        try
        {
            foreach (var root in roots)
                _tree.Nodes.Add(ToTreeNode(root));

            // Không có filter (đang xem toàn bộ ~1600 dòng wcommand): giữ nguyên hành vi cũ
            // — chỉ hiện node cha, gấp lại với placeholder "...", cấp con nạp lười khi người
            // dùng tự bấm mở (xem BeforeExpand) để khỏi treo UI.
            //
            // CÓ filter (đang xem kết quả tìm theo tên/ID — tập đã được WCommandService.
            // FilterTree lọc gọn sẵn, chỉ còn đúng các nhánh khớp): bung hết luôn cho thấy
            // ngay menu con khớp nằm ở đâu, khỏi phải tự tay bung từng cấp.
            if (filter is not null)
            {
                foreach (TreeNode root in _tree.Nodes)
                    ExpandRecursive(root);
            }
        }
        finally
        {
            _tree.EndUpdate();
        }
    }

    /// <summary>Bung một node và toàn bộ cây con của nó. Expand() kích hoạt BeforeExpand
    /// ngay lập tức (đồng bộ) để thay placeholder "..." bằng các node con thật trước khi
    /// hàm này đệ quy tiếp xuống — nên chỉ dùng khi tập kết quả đã nhỏ (có filter), tránh
    /// vét cạn toàn bộ ~1600 dòng khi không lọc gì.</summary>
    private static void ExpandRecursive(TreeNode node)
    {
        node.Expand();
        foreach (TreeNode child in node.Nodes)
            ExpandRecursive(child);
    }

    private List<WCommandItem> _rootsAll = new();

    /// <summary>Mọi menu lá (kèm đường dẫn cha) của lần tải đầy đủ gần nhất — Command Palette tìm theo đây, không hỏi lại database.</summary>
    public List<(WCommandItem Item, string Path)> FlatLeaves()
    {
        var list = new List<(WCommandItem, string)>();
        void Walk(WCommandItem it, string parent)
        {
            var path = parent.Length == 0 ? it.Bar : parent + " \\ " + it.Bar;
            if (it.Children.Count == 0) list.Add((it, parent));
            foreach (var c in it.Children.OrderBy(c => c.WMenuId)) Walk(c, path);
        }
        foreach (var r in _rootsAll) Walk(r, "");
        return list;
    }

    /// <summary>Mở các nhánh cha và chọn đúng menu <paramref name="target"/> trong cây (dùng cho Command Palette). Đang lọc thì bỏ lọc, nạp lại cây đầy đủ.</summary>
    public async Task RevealAsync(WCommandItem target)
    {
        _filterText = "";
        await ReloadAsync();
        var path = new List<WCommandItem>();
        bool Find(IEnumerable<WCommandItem> level)
        {
            foreach (var it in level)
            {
                path.Add(it);
                if (it.WMenuId == target.WMenuId && it.Bar == target.Bar) return true;
                if (Find(it.Children)) return true;
                path.RemoveAt(path.Count - 1);
            }
            return false;
        }
        if (!Find(_rootsAll)) return;

        TreeNodeCollection nodes = _tree.Nodes;
        TreeNode? node = null;
        foreach (var step in path)
        {
            node = nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag is WCommandItem w && w.WMenuId == step.WMenuId && w.Bar == step.Bar);
            if (node is null) return;
            if (!ReferenceEquals(step, path[^1])) node.Expand();     // Expand tạo các node con (xem BeforeExpand) ngay trước khi đi tiếp
            nodes = node.Nodes;
        }
        if (node is null) return;
        _tree.SelectedNode = node;
        node.EnsureVisible();
    }

    private WCommandItem? SelectedItem => _tree.SelectedNode?.Tag as WCommandItem;

    /// <summary>Ctrl+F5 (cấu hình được: Template → Phím tắt, nhóm "Cây menu WCommand") khi cây đang có focus: chạy menu đang chọn. Cây bắt phím trước phím cùng tổ hợp ở phạm vi cửa sổ.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_tree.ContainsFocus && Bcode.App.UI.ShortcutRegistry.FromKeys(keyData) is { } combo
            && combo == Bcode.App.UI.ShortcutRegistry.Get("wcommand.run") && SelectedItem is not null)
        {
            RunSelectedMenu();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>"Run...": mở web của project (Login WLink) đúng trang của menu đang chọn: {Login WLink}/Main/{Link}[?Parameter]. Menu không có Link (menu nhóm) thì mở trang chủ của site.</summary>
    private void RunSelectedMenu()
    {
        if (SelectedItem is not { } item) return;
        var wl = _getCurrentWorkspace()?.LoginWLink?.Trim() ?? "";
        if (wl.Length == 0)
        {
            MessageBox.Show(this, "Project này chưa khai báo Login WLink (Edit Project).", "Bcode — Run");
            return;
        }
        var url = BuildMenuUrl(wl, item);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được trang web:\n" + ex.Message, "Bcode — Run"); }
    }

    internal static string BuildMenuUrl(string loginWLink, WCommandItem item)
    {
        var baseUrl = loginWLink.Trim().TrimEnd('/');
        var link = (item.IsAppCommand ? "" : item.Link ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (link.Length == 0) return baseUrl + "/";
        if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return link;
        if (!link.StartsWith("Main/", StringComparison.OrdinalIgnoreCase)) link = "Main/" + link;
        var url = baseUrl + "/" + link;
        var p = (item.Parameter ?? "").Trim().TrimStart('?', '&');
        if (p.Contains('=')) url += (url.Contains('?') ? "&" : "?") + p;
        return url;
    }
    /// <summary>Menu APP: mở Explorer ở thư mục source của chương trình (exe "zinctpxi.exe ..." → {SourcePath}\zinctpxi). Không thấy đúng tên thì tìm thư mục con trùng tên (tối đa 3 cấp).</summary>
    private void BrowseSourceFolder()
    {
        if (SelectedItem is not { IsAppCommand: true } item) return;
        var ws = _getCurrentWorkspace();
        var root = ws?.SourcePath;

        var stem = AppCommandService.ExeStem(item.Exe);
        if (string.IsNullOrWhiteSpace(root)) { MessageBox.Show(this, "Workspace chưa khai Source Path.", "Bcode — Browse Source Folder"); return; }
        if (stem.Length == 0) { MessageBox.Show(this, $"Menu \"{item.Bar}\" không khai tên file exe.", "Bcode — Browse Source Folder"); return; }
        var target = AppCommandService.FindSourceFolder(root, stem);
        if (target is null)
        {
            MessageBox.Show(this, $"Không thấy thư mục source '{stem}' trong:\n{root}", "Bcode — Browse Source Folder");
            return;
        }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bcode — Browse Source Folder", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }



    // Hộp thoại New/Edit mở KHÔNG chặn: vẫn bấm được cây; bấm sang menu khác thì hộp thoại nạp menu đó (xem AfterSelect ở constructor).
    private WCommandEditForm? _editForm;
    private AppCommandEditForm? _appEditForm;

    /// <summary>Menu dạng APP (chỉ có bảng command): mở form COMMAND - Edit tổng quát. existingId = null → New (mượn dữ liệu của templateId nếu có).</summary>
    private void OpenAppEditForm(string? existingId, string? templateId)
    {
        if (_appEditForm is { IsDisposed: false } open)
        {
            _ = open.SwitchToAsync(existingId, templateId);
            open.Activate();
            return;
        }
        var form = new AppCommandEditForm(_service.AppCommands, existingId, templateId);
        _appEditForm = form;
        form.Changed += () => { if (!IsDisposed) BeginInvoke(new Action(async () => await ReloadAsync())); };
        form.FormClosed += (_, _) => { if (ReferenceEquals(_appEditForm, form)) _appEditForm = null; form.Dispose(); };
        form.Show(FindForm());
    }

    private void OpenEditForm(WCommandItem? existing, WCommandItem? template)
    {
        if (_editForm is { IsDisposed: false } open)
        {
            _ = open.SwitchToAsync(existing, template);
            open.Activate();
            return;
        }
        var form = new WCommandEditForm(_service, existing, template, sourcePath: _getCurrentWorkspace()?.SourcePath);
        _editForm = form;
        form.FormClosed += async (_, _) =>
        {
            if (ReferenceEquals(_editForm, form)) _editForm = null;
            var saved = form.DialogResult == DialogResult.OK;
            form.Dispose();
            if (saved && !IsDisposed) await ReloadAsync();
        };
        form.Show(FindForm());
    }

    private Task NewAsync()
    {
        // "New" from a right-clicked menu prefills every field with that menu's own data
        // (same parent, same link/sysid/icon/type/...) instead of opening a blank dialog —
        // the user then only has to tweak a couple of things (usually the id and the name)
        // to get a similar new menu, rather than typing all 18 wcommand fields from scratch.
        // (WMenu Id itself gets overwritten again right after with a suggested free id —
        // see WCommandEditForm's own Load handler — since the cloned id is already taken.)
        var template = SelectedItem;
        if (template is { IsAppCommand: true }) { OpenAppEditForm(null, template.MenuId); return Task.CompletedTask; }
        OpenEditForm(null, template);
        return Task.CompletedTask;
    }

    private Task EditSelectedAsync()
    {
        var item = SelectedItem;
        if (item is null) return Task.CompletedTask;
        if (item.IsAppCommand) { OpenAppEditForm(item.MenuId, null); return Task.CompletedTask; }
        OpenEditForm(item, null);
        return Task.CompletedTask;
    }

    private async Task DeleteSelectedAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        if (item.IsAppCommand)
        {
            if (MessageBox.Show(this, $"Xóa menu '{item.Bar}' ({item.MenuId}) khỏi bảng command?", "Bcode — Command", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { await _service.AppCommands.DeleteAsync(item.MenuId); await ReloadAsync(); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bcode — Command", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            return;
        }

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

    private async Task CopySourceStandardAsync()
    {
        var item = SelectedItem;
        if (item is null) return;

        var ws = _getCurrentWorkspace();
        if (ws is null || string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            MessageBox.Show(this, "Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.",
                "Bcode — Copy source standard");
            return;
        }
        if (string.IsNullOrWhiteSpace(item.Link) && string.IsNullOrWhiteSpace(item.SysId))
        {
            MessageBox.Show(this, $"Menu \"{item.Bar}\" không có Link/SysId gắn với source (có thể là mục nhóm/menu cha).",
                "Bcode — Copy source standard");
            return;
        }

        using var form = new CopySourceStandardForm(_fileLookupService, ws.SourcePath, item);
        form.ShowDialog(this);
        await Task.CompletedTask;
    }


    /// <summary>Cấp source từ kho theo version cho menu đang chọn: mẫu tên = file trang (Main\&lt;link&gt;) + "&lt;sysid&gt;*",
    /// như màn Add Source của FCode; đích = Source Path của workspace.</summary>
    private void ShowAddSource()
    {
        var item = SelectedItem;
        var ws = _getCurrentWorkspace();
        if (item is null || _settings is null) return;
        if (ws is null || string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            MessageBox.Show(this, "Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.", "Bcode — Cấp source");
            return;
        }

        var patterns = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Link)) patterns.Add(Path.GetFileName(item.Link.Replace('/', '\\')));
        if (!string.IsNullOrWhiteSpace(item.SysId)) patterns.Add(item.SysId + "*");
        if (patterns.Count == 0)
        {
            MessageBox.Show(this, $"Menu \"{item.Bar}\" không có Link/SysId để tìm source (có thể là menu nhóm).", "Bcode — Cấp source");
            return;
        }
        if (!AddSourceLauncher.EnsureCollectionPath(this, _settings)) return;

        using var form = new AddSourceForm(_settings.SourceCollectionPath, string.Join("; ", patterns), ws.SourcePath,
            () => _fileLookupService.InvalidateCache());
        form.ShowDialog(this);
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

        // Chỉ ghi các cột bảng wcommand / command thật sự có ở database này (đọc cấu trúc ở nền, có cache).
        var script = await Task.Run(() => _service.GenerateScriptForCurrentDb(item));
        using var form = new WCommandScriptForm(script, $"Script — {item.WMenuId}");
        form.ShowDialog(this);
    }

    /// <summary>Copies the full ancestry path of the selected node — every parent's Bar
    /// joined by " \\ ", ending with "&lt;Bar&gt; (&lt;WMenuId&gt;)" for the selected node itself — e.g.
    /// "Phải thu \ Tạo hóa đơn bán hàng từ Haravan (C)". Bound to Ctrl+C above.</summary>
    private void CopySelectedFullPath()
    {
        var node = _tree.SelectedNode;
        if (node?.Tag is not WCommandItem) return;
        try { Clipboard.SetText(BuildFullBarPath(node)); }
        catch { /* clipboard held by another app right now — nothing to recover, just skip */ }
    }

    private static string BuildFullBarPath(TreeNode node)
    {
        var parents = new List<string>();
        for (var n = node.Parent; n is not null; n = n.Parent)
            if (n.Tag is WCommandItem parentItem) parents.Insert(0, parentItem.Bar);

        var leaf = node.Tag is WCommandItem item ? $"{item.Bar} ({item.WMenuId})" : node.Text;
        parents.Add(leaf);
        return string.Join(" \\ ", parents);
    }

    /// <summary>Các phân hệ cấp ngoài cùng của cây (mã = đoạn đầu wmenu_id, tên menu) — để Template giao diện gợi ý khi đặt màu theo phân hệ.</summary>
    public List<(string Code, string Name)> TopGroups() =>
        _tree.Nodes.Cast<TreeNode>().Select(n => n.Tag as WCommandItem).Where(i => i is not null)
            .Select(i => ((i!.WMenuId ?? "").Split('.')[0].Trim(), i.Bar)).Where(g => g.Item1.Length > 0).Distinct().ToList();

    private static TreeNode ToTreeNode(WCommandItem item)
    {
        var node = new TreeNode($"{item.Bar}  ({item.WMenuId})") { Tag = item };
        ApplyGroupColor(node, item);
        if (item.Children.Count > 0)
            node.Nodes.Add(new TreeNode("...")); // lazy placeholder — replaced in BeforeExpand
        return node;
    }

    // ---- Màu theo phân hệ ----------------------------------------------------------------------------

    /// <summary>Phân hệ = đoạn đầu của wmenu_id ("07" trong "07.10.06"). Mỗi phân hệ 1 sắc độ riêng, tính cố định từ mã
    /// (không theo thứ tự xuất hiện) nên giữ nguyên màu dù lọc/nạp lại; các phân hệ liền nhau cách xa nhau trên vòng màu.</summary>
    private static double GroupHue(string wmenuId)
    {
        var group = (wmenuId ?? "").Split('.')[0].Trim();
        var index = int.TryParse(group, out var n) ? n : group.Aggregate(0, (acc, c) => acc * 31 + c);
        return (Math.Abs(index) * 47) % 360; // 47° (nguyên tố cùng nhau với 360) → các mã 01,02,03... rải đều quanh vòng màu
    }

    /// <summary>CHỈ menu mẹ (có menu con) được đổi màu CHỮ theo phân hệ — dịu (bão hoà vừa phải, không tô nền) để không chói mắt.
    /// Menu con và mục lá giữ màu mặc định.</summary>
    private static void ApplyGroupColor(TreeNode node, WCommandItem item)
    {
        node.BackColor = Color.Empty;
        // Tuỳ chọn ở Template giao diện → "Màu cây": none = giữ màu chữ của theme; custom = màu tự chọn; auto = tô theo phân hệ như dưới.
        var opt = Bcode.App.UI.TreeColorOptions.Current;
        if (opt.WCommandMode == Bcode.App.UI.TreeColorOptions.None) { node.ForeColor = Color.Empty; return; }
        if (opt.WCommandMode == Bcode.App.UI.TreeColorOptions.Custom)
        {
            // Menu mẹ: màu riêng của phân hệ (đoạn đầu wmenu_id) nếu có, không thì màu "menu mẹ" chung; mục lá: màu "mục lá".
            var groupCode = (item.WMenuId ?? "").Split('.')[0].Trim();
            var groupColor = item.Children.Count > 0 && opt.WCommandGroups.TryGetValue(groupCode, out var gc) ? gc : null;
            var custom = Bcode.App.UI.UiTemplate.ParseColor(groupColor ?? (item.Children.Count > 0 ? opt.WCommandParent : opt.WCommandLeaf));
            node.ForeColor = custom ?? Color.Empty;
            return;
        }
        if (item.Children.Count == 0)
        {
            node.ForeColor = Color.Empty;
            return;
        }
        var hue = GroupHue(item.WMenuId);
        var dark = Bcode.App.UI.AppColors.IsDark;
        node.ForeColor = FromHsl(hue, dark ? 0.50 : 0.55, dark ? 0.70 : 0.32);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }

    private void RecolorNodes()
    {
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is WCommandItem item) ApplyGroupColor(n, item);
                Walk(n.Nodes);
            }
        }
        Walk(_tree.Nodes);
    }
}