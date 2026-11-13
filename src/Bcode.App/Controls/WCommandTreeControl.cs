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
    private readonly WCommandService _service;
    private readonly FileLookupService _fileLookupService;
    private readonly Func<Workspace?> _getCurrentWorkspace;
    public event Action<WCommandItem>? NodeActivated;

    /// <summary>Bắn khi bấm F4 (hoặc menu "Mở File Lookup mới"): mở thêm 1 tab File Lookup riêng cho menu đang chọn.</summary>
    public event Action<WCommandItem>? NewLookupRequested;

    /// <summary>Menu "Thiết kế màn hình": mở Screen Designer (exe riêng) ngay controller của menu đang chọn.</summary>
    public event Action<WCommandItem>? DesignRequested;

    /// <summary>Ctrl+4 (wcommand.runFbo): mở menu đang chọn trong tab FSG FBO — MainForm đăng nhập rồi đi tới trang của menu.</summary>
    public event Action<WCommandItem>? RunInFboRequested;

    /// <summary>Menu "Yêu cầu liên quan (QLYC)…": mở tab Yêu cầu FSG lọc sẵn các y/c của dự án đang chọn liên quan tới menu này (quy tắc map: xem fsgreq.html, menuWhy).</summary>
    public event Action<FsgRequirementReportControl.MenuQuery>? RequirementsRequested;

    /// <summary>Menu "Sơ đồ phụ thuộc…": mở tab sơ đồ từ menu đang chọn (menu con, file controller, procedure / bảng trong các file đó).</summary>
    public event Action<WCommandItem>? DependencyRequested;
    /// <summary>Chuột phải → "Mở dạng lưới (khai field)" — chỉ hiện khi bật Chế độ triển khai (AppSettings.DeployGridMode).</summary>
    public event Action<WCommandItem>? FieldGridRequested;

    /// <summary>Menu "Tạo cấu trúc API…": dựng cấu trúc API (bảng Dir + Grid chi tiết của menu) rồi mở màn Tạo cấu trúc API đang chọn đúng API đó.</summary>
    public event Action<WCommandItem>? ApiSchemaRequested;

    /// <summary>Menu "Tạo khai báo API…": dựng cấu trúc như trên rồi đưa thẳng thành form trong Khai báo API.</summary>
    public event Action<WCommandItem>? ApiDeclareRequested;

    /// <summary>Menu "Gen nhanh menu": mở Note (New) nhiều dự án (BcodeNote.exe) ở đúng dự án + menu này, tự hiện gợi ý Gen All / File / SQL Object / Table.</summary>
    public event Action<WCommandItem>? QuickGenRequested;

    /// <summary>Menu "View Relative Tables" (như FCode): mở tab liệt kê các bảng liên quan tới chứng từ đang chọn (theo syscode). Chỉ hiện khi node có syscode.</summary>
    public event Action<WCommandItem>? RelativeTablesRequested;

    private void RequestRequirements()
    {
        if (IsMenuTable || _tree.SelectedNode is not { Tag: WCommandItem item } node) return;
        // Menu con (kể cả nhánh chưa bung — Children luôn đủ) và menu cha của node: để khớp y/c gắn ở menu con / y/c chung ở cấp cha.
        var childIds = new List<string>();
        void Walk(WCommandItem it) { foreach (var c in it.Children) { if (!string.IsNullOrWhiteSpace(c.WMenuId)) childIds.Add(c.WMenuId.Trim()); Walk(c); } }
        Walk(item);
        var ancestorIds = new List<string>();
        for (var n = node.Parent; n is not null; n = n.Parent)
            if (n.Tag is WCommandItem p && !string.IsNullOrWhiteSpace(p.WMenuId)) ancestorIds.Add(p.WMenuId.Trim());
        RequirementsRequested?.Invoke(new FsgRequirementReportControl.MenuQuery(item.WMenuId.Trim(), item.Bar ?? "", item.Bar2 ?? "", childIds, ancestorIds, BuildFullBarPath(node)));
    }

    private void OpenInNewLookup()
    {
        if (SelectedItem is { } item) NewLookupRequested?.Invoke(item);
    }

    private readonly AppSettings? _settings;

    // Cây của 1 bảng menu khác wcommand (mục Mobile = bảng dvcommand): nạp / New / Edit / Delete / Gen Script qua AppCommandService
    // của bảng đó, sửa bằng form AppCommandEditForm. null = cây WCommand như cũ.
    private readonly AppCommandService? _menuTable;
    private readonly Func<string, CommandTableService.Result>? _tableDir;
    private bool IsMenuTable => _menuTable is not null;

    /// <param name="menuTable">Khác null = cây của bảng menu đó (vd <see cref="MenuTableSpec.DvCommand"/>) thay cho wcommand; các mục chỉ dành cho menu web
    /// (Run, Screen Designer, Copy source standard, Cấp source, Check WCommand) bị ẩn.</param>
    /// <param name="tableDir">Ô Table Dir của form sửa (bảng m/d theo sysid) khi ở chế độ <paramref name="menuTable"/>.</param>
    public WCommandTreeControl(WCommandService service, FileLookupService fileLookupService, Func<Workspace?> getCurrentWorkspace, AppSettings? settings = null,
        AppCommandService? menuTable = null, Func<string, CommandTableService.Result>? tableDir = null)
    {
        _settings = settings;
        _menuTable = menuTable;
        _tableDir = tableDir;
        _service = service;
        _fileLookupService = fileLookupService;
        _getCurrentWorkspace = getCurrentWorkspace;
        Dock = DockStyle.Fill;

        _barWeb.Dock = DockStyle.Top;
        _barWeb.Height = Bcode.App.UI.NativeSkin.S(34);   // trang phóng theo DPI × UiScale → khung cũng vậy (đổi UiScale: xem ThemeManager.Subscribe bên dưới)
        Bcode.App.UI.ThemeManager.Subscribe(this, () => _barWeb.Height = Bcode.App.UI.NativeSkin.Px(_barWeb, 34));

        _tree = new SafeTreeView { Dock = DockStyle.Fill, HideSelection = false };
        // "Fcode's lookup bars are smooth — check what makes them not lag." TreeView (like
        // DataGridView, see GridDisplayHelper) doesn't double-buffer itself by default, which
        // shows up as flicker/tearing repainting ~1600 wcommand rows' worth of nodes. This was
        // already the smallest part of the fix here — the real one was the lazy-expansion
        // BeforeExpand handler below — but costs nothing to also turn on.
        Bcode.App.UI.ControlPerf.EnableDoubleBuffering(_tree);
        // Đang mở hộp thoại New/Edit mà bấm sang menu khác → hiện thông tin menu đó lên hộp thoại (không phải đóng rồi mở lại).
        _tree.AfterSelect += (_, e) =>
        {
            if (_appEditForm is { IsDisposed: false } appOpen && e.Node?.Tag is WCommandItem appPicked && AppKey(appPicked) is { } appKey)
                _ = appOpen.SwitchToAsync(appKey);
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
            if (combo is not null && combo == Bcode.App.UI.ShortcutRegistry.Get("wcommand.menuName")) { e.Handled = true; ToggleMenuName(); return; }
            if (combo is not null && !IsMenuTable && combo == Bcode.App.UI.ShortcutRegistry.Get("wcommand.requirements")) { e.Handled = true; RequestRequirements(); return; }
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
            if (IsMenuTable)
                return new WebMenu()
                    .Add("New", async () => await NewAsync(), shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.new"))
                    .Add("Edit", async () => await EditSelectedAsync(), shortcut: "F3", enabled: hasSelection)
                    .Add("Delete", async () => await DeleteSelectedAsync(), shortcut: "F8", enabled: hasSelection, danger: true)
                    .AddSeparator()
                    .Add("Mở File Lookup mới", OpenInNewLookup, shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.newLookup"), enabled: hasSelection)
                    .Add("Gen Script Menu", async () => await GenScriptMenuAsync(), shortcut: "F12", enabled: hasSelection)
                    .AddSeparator()
                    .Add(MenuNameToggleLabel, ToggleMenuName, shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.menuName"))
                    .Add("Refresh", async () => await ReloadAsync(force: true), shortcut: "F5");
            var menu = new WebMenu()
                .Add("New", async () => await NewAsync(), shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.new"))
                .Add("Edit", async () => await EditSelectedAsync(), shortcut: "F3", enabled: hasSelection)
                .Add("Delete", async () => await DeleteSelectedAsync(), shortcut: "F8", enabled: hasSelection, danger: true)
                .AddSeparator()
                .Add("Thiết kế màn hình (Screen Designer)", () => { if (SelectedItem is { } d && !string.IsNullOrWhiteSpace(d.SysId)) DesignRequested?.Invoke(d); }, enabled: _tree.SelectedNode?.Tag is WCommandItem { SysId.Length: > 0 });
            // Chế độ triển khai (Template → chuột phải File Lookup → "Chế độ triển khai"): khai field Dir / Grid / Filter của menu dạng lưới như FSD Studio.
            if (_settings?.DeployGridMode == true && FieldGridRequested is not null)
                menu.Add("Mở dạng lưới (khai field)", () => { if (SelectedItem is { SysId.Length: > 0 } g) FieldGridRequested?.Invoke(g); }, enabled: _tree.SelectedNode?.Tag is WCommandItem { SysId.Length: > 0 });
            return menu
                .Add("Mở File Lookup mới", OpenInNewLookup, shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.newLookup"), enabled: hasSelection)
                .Add("Run...", RunSelectedMenu, shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.run"), enabled: hasSelection)
                .Add("Run trong FSG FBO (tự đăng nhập)", () => { if (SelectedItem is { } it) RunInFboRequested?.Invoke(it); }, shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.runFbo"), enabled: hasSelection && RunInFboRequested is not null)
                .Add("Copy source standard", async () => await CopySourceStandardAsync(), enabled: hasSelection)
                .Add("Browse Source Folder", BrowseSourceFolder, enabled: _tree.SelectedNode?.Tag is WCommandItem { IsAppCommand: true })
                .Add("Cấp source (Add Source)...", ShowAddSource, enabled: hasSelection && _settings is not null)
                .AddSeparator()
                .Add("Yêu cầu liên quan (QLYC)…", RequestRequirements, shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.requirements"), enabled: hasSelection && RequirementsRequested is not null)
                .Add("Sơ đồ phụ thuộc…", () => { if (SelectedItem is { } it) DependencyRequested?.Invoke(it); }, enabled: hasSelection && DependencyRequested is not null)
                .Add("View Relative Tables", () => { if (SelectedItem is { SysCode.Length: > 0 } it) RelativeTablesRequested?.Invoke(it); }, enabled: _tree.SelectedNode?.Tag is WCommandItem { SysCode.Length: > 0 } && RelativeTablesRequested is not null)
                .Add("Tạo cấu trúc API…", () => { if (SelectedItem is { SysId.Length: > 0 } it) ApiSchemaRequested?.Invoke(it); }, enabled: _tree.SelectedNode?.Tag is WCommandItem { SysId.Length: > 0 } && ApiSchemaRequested is not null)
                .Add("Tạo khai báo API…", () => { if (SelectedItem is { SysId.Length: > 0 } it) ApiDeclareRequested?.Invoke(it); }, enabled: _tree.SelectedNode?.Tag is WCommandItem { SysId.Length: > 0 } && ApiDeclareRequested is not null)
                .Add("Gen nhanh menu", () => { if (SelectedItem is { } it) QuickGenRequested?.Invoke(it); }, enabled: _tree.SelectedNode?.Tag is WCommandItem qg && (!string.IsNullOrWhiteSpace(qg.SysId) || !string.IsNullOrWhiteSpace(qg.Link)) &&QuickGenRequested is not null)
                .AddSeparator()
                .Add("Check WCommand", async () => await CheckWCommandAsync())
                .Add("Gen Script Menu", async () => await GenScriptMenuAsync(), shortcut: "F12", enabled: hasSelection)
                .AddSeparator()
                .Add(MenuNameToggleLabel, ToggleMenuName, shortcut: Bcode.App.UI.ShortcutRegistry.Display("wcommand.menuName"))
                .Add("Refresh", async () => await ReloadAsync(force: true), shortcut: "F5");
        });

        Controls.Add(_tree);
        var native = Bcode.App.UI.UiMode.IsNative;
        if (native) Controls.Add(BuildNativeBar()); else Controls.Add(_barWeb);

        Bcode.App.UI.ThemeManager.ThemeChanged += PushThemeToBar;
        Disposed += (_, _) => Bcode.App.UI.ThemeManager.ThemeChanged -= PushThemeToBar;
        // Đổi theme sáng/tối thì tô lại màu phân hệ cho hợp nền.
        Action recolor = RecolorNodes;
        Bcode.App.UI.ThemeManager.ThemeChanged += recolor;
        Disposed += (_, _) => Bcode.App.UI.ThemeManager.ThemeChanged -= recolor;
        // Đổi Menu Name (Bar ↔ Bar2) ở 1 cây thì cả cây WCommand lẫn cây Mobile cùng đổi tên hiển thị.
        Action relabel = RelabelNodes;
        MenuNameChanged += relabel;
        Disposed += (_, _) => MenuNameChanged -= relabel;
        // Thanh lọc (WebView2 ~10MB) chỉ dựng khi mục WCommand được hiện lần đầu: control ẩn thì chưa có handle nên chưa tốn tiến trình nào.
        var barStarted = false;
        void StartBar() { if (barStarted) return; barStarted = true; _ = InitBarWebAsync(); }
        if (!native)
        {
            HandleCreated += (_, _) => BeginInvoke(new Action(StartBar));   // sau khi tạo handle xong hẳn (khởi tạo WebView2 ngay trong HandleCreated dễ bị E_ABORT)
            if (IsHandleCreated) BeginInvoke(new Action(StartBar));
        }

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
                        var text = root.GetProperty("filter").GetString() ?? "";
                        var custom = root.TryGetProperty("custom", out var cu) && cu.ValueKind == System.Text.Json.JsonValueKind.True;
                        var refresh = root.TryGetProperty("src", out var src) && src.GetString() == "refresh";
                        await SetViewAsync(text, custom, refresh);
                    }
                };

                _barWeb.CoreWebView2.NavigationCompleted += (_, _) => PushThemeToBar();
                _barWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("wcommandbar.html"));
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(this,   // hộp Windows: WebView2 hỏng thì hộp theo theme (cần WebView2 ở chế độ Web) cũng không hiện được
                    Bcode.App.UI.WebViewEnvironment.FriendlyInitMessage("thanh công cụ", ex),
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

    private RoundedInput? _nativeFilter;

    /// <summary>Thanh lọc bản native (tương đương wcommandbar.html): ô lọc + ô (C) (chỉ menu customize) + nút Refresh; Enter, đổi (C) hoặc Refresh = tải lại theo bộ lọc.</summary>
    private Control BuildNativeBar()
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = Bcode.App.UI.NativeSkin.Dens(null, 40), Padding = new Padding(6, 6, 6, 6) };
        var refresh = PillButton.Flat("Refresh");
        refresh.Dock = DockStyle.Right;
        _nativeFilter = new RoundedInput { Dock = DockStyle.Fill, PlaceholderText = "Tìm theo tên menu, ID hoặc link..." };
        var custom = NK.Check("(C)");
        custom.Dock = DockStyle.Right; custom.AutoSize = true; custom.Padding = new Padding(4, 0, 0, 0);
        NK.Tip(custom, "Chỉ hiện menu customize (tên có (C) hoặc (*))");
        async void Go(bool refreshClick = false) => await SetViewAsync(_nativeFilter.Text, custom.Checked, refreshClick);
        custom.CheckedChanged += (_, _) => Go();
        refresh.Click += (_, _) => Go(true);
        _nativeFilter.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Go(); } };
        panel.Controls.Add(_nativeFilter);
        panel.Controls.Add(custom);
        panel.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6 });
        panel.Controls.Add(refresh);
        return panel;
    }

    // Chỉ lần tải MỚI NHẤT được phép đổ vào cây: đổi workspace nhanh (hoặc F5 liên tiếp) tạo nhiều lần tải
    // chồng nhau; lần cũ về sau trước đây vẫn thêm node vào cây → menu bị nhân đôi / lẫn menu WS cũ.
    private int _reloadVersion;

    // ---- Chế độ xem của thanh lọc: chữ tìm + ô (C). Lọc làm trên cây đã nạp (_rootsAll), không đọc lại database ----
    // Trước đây mỗi lần đổi (C) / chữ tìm đều nạp lại cả cây rồi chụp trạng thái của cây ĐANG LỌC (bung hết) làm trạng thái
    // của cây đầy đủ → bỏ tick (C) / xoá chữ tìm là cây đầy đủ bung hết các nhánh đã hiện lúc lọc. Giờ nhớ riêng trạng thái
    // cây đầy đủ lúc RỜI nó (_plainState) và trả lại đúng như thế khi quay về; cây (C) không chữ tìm cũng được nhớ khi
    // chuyển sang tìm chữ trong (C) (_customState) để xoá chữ tìm là về lại đúng cây (C) trước đó.
    private string? _viewText;      // chữ tìm đã chuẩn hoá (Trim), null = không lọc chữ
    private bool _customOnly;       // ô "(C)": chỉ menu customize (tên có (C) / (*)) kèm menu cha
    private TreeState? _plainState, _customState;
    private string? _emptyNote;     // cây Mobile: bảng menu không có / rỗng → 1 dòng ghi chú xám

    private sealed class TreeState
    {
        public readonly HashSet<string> Expanded = new(StringComparer.Ordinal);
        public string? Selected, Top;
    }

    private bool IsPlainView => _viewText is null && !_customOnly;
    private bool HasData => _rootsAll.Count > 0 && ReferenceEquals(_getCurrentWorkspace(), _stateWorkspace);

    /// <summary>Thanh lọc đổi chữ tìm / ô (C) / bấm Refresh. Không đổi gì (Enter lại cùng chữ, Refresh) = nạp lại như trước;
    /// đổi chế độ = lọc tại chỗ trên cây đã nạp, nhớ trạng thái chế độ cũ. <paramref name="refresh"/> (nút Refresh) thì lọc xong vẫn nạp lại.</summary>
    private async Task SetViewAsync(string? text, bool custom, bool refresh)
    {
        var t = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        if (t == _viewText && custom == _customOnly) { await ReloadAsync(); return; }

        if (HasData && _tree.Nodes.Count > 0)
        {
            if (IsPlainView) _plainState = CaptureState();
            else if (_viewText is null && _customOnly) _customState = (custom && t is not null) ? CaptureState() : null;
        }
        if (!custom) _customState = null;
        _viewText = t; _customOnly = custom;

        if (HasData) RenderView(StoredState());
        else _tree.Nodes.Clear();
        if (refresh || !HasData) await ReloadAsync();
    }

    /// <summary>Trạng thái đã nhớ của chế độ đang xem: cây đầy đủ → lúc rời nó; cây (C) → lúc chuyển sang tìm chữ trong (C); tìm chữ → không (bung hết).</summary>
    private TreeState? StoredState() => IsPlainView ? _plainState : (_viewText is null ? _customState : null);

    public async Task ReloadAsync(bool force = false)
    {
        var version = ++_reloadVersion;
        // Nhớ các nhánh đang bung / menu đang chọn / dòng đầu đang xem để dựng lại y như cũ (trước đây mỗi lần quay lại mục Menu là cây gập hết).
        // Đổi workspace thì bỏ (cây của dự án khác).
        TreeState? keep;
        var wsNow = _getCurrentWorkspace();
        if (!ReferenceEquals(wsNow, _stateWorkspace))
        {
            _plainState = _customState = null; keep = null;
            _rootsAll = new();
            _stateWorkspace = wsNow;
        }
        else keep = _tree.Nodes.Count > 0 ? CaptureState() : StoredState();
        _tree.Nodes.Clear();

        if (_menuTable is not null)
        {
            // Bảng menu nhỏ (dvcommand vài chục dòng): luôn đọc thẳng database, không bản lưu / chữ ký.
            List<WCommandItem> flat;
            try { flat = await _menuTable.LoadMenuItemsAsync(); }
            catch (Exception ex)
            {
                if (version != _reloadVersion) return;
                MessageBox.Show(this, $"Không tải được {_menuTable.Spec.Table}: {ex.Message}", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (version != _reloadVersion) return;
            _rootsAll = WCommandService.BuildTree(flat, null, inferParent: false);
            _emptyNote = flat.Count == 0 ? $"(Không có bảng {_menuTable.Spec.Table} ở Sys Data của workspace này, hoặc bảng rỗng)" : null;
            RenderView(keep);
            return;
        }

        // Có bản lưu trên máy: hiện cây NGAY từ đó, rồi hỏi database ngầm — giống nhau thì giữ nguyên, khác (hoặc database không với tới được) thì xử lý bên dưới.
        var cachedFlat = _service.LoadCachedFlat();
        var cacheSignature = cachedFlat is { Count: > 0 } ? WCommandService.SignatureOf(cachedFlat) : null;
        if (cacheSignature is not null)
        {
            _rootsAll = _service.TreeFromFlat(cachedFlat!);
            RenderView(keep);
        }

        // Bản lưu còn đúng (chữ ký nhẹ do database tính trùng) → giữ cây đang hiện, khỏi tải cả bảng wcommand.
        if (!force && cacheSignature is not null && await _service.IsCacheCurrentAsync()) return;
        if (version != _reloadVersion) return;

        List<WCommandItem> roots;
        try
        {
            roots = await _service.LoadTreeAsync();
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
        if (cacheSignature is not null) keep = CaptureState();                                  // khác: dựng lại theo bản mới (giữ nhánh đang bung)
        _rootsAll = roots;   // bản đầy đủ (không lọc) — Command Palette tìm menu trong đây; lọc chữ / (C) làm trên bản sao
        RenderView(keep);
    }

    private Workspace? _stateWorkspace;

    private static string NodeKey(TreeNode n) => n.Tag is WCommandItem w ? w.WMenuId + "\u0001" + w.Bar : n.Text;

    private TreeState CaptureState()
    {
        var s = new TreeState();
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (!n.IsExpanded) continue;
                s.Expanded.Add(NodeKey(n));
                Walk(n.Nodes);
            }
        }
        Walk(_tree.Nodes);
        s.Selected = _tree.SelectedNode is { } sel ? NodeKey(sel) : null;
        s.Top = _tree.TopNode is { } top ? NodeKey(top) : null;
        return s;
    }

    /// <summary>Bung lại các nhánh / chọn lại menu đã nhớ; trả node dòng đầu cần cuộn tới (đặt TopNode sau EndUpdate). Gọi trong BeginUpdate.</summary>
    private TreeNode? ApplyState(TreeState s)
    {
        TreeNode? selected = null, top = null;
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                var key = NodeKey(n);
                if (key == s.Selected) selected = n;
                if (key == s.Top) top = n;
                if (!s.Expanded.Contains(key)) continue;
                n.Expand();          // tạo node con thật (BeforeExpand) rồi đi tiếp xuống
                Walk(n.Nodes);
            }
        }
        Walk(_tree.Nodes);
        if (selected is not null) _tree.SelectedNode = selected;
        return top;
    }

    /// <summary>Dựng lại cây theo chế độ đang xem (chữ tìm + (C)) từ <see cref="_rootsAll"/>. Có <paramref name="restore"/> thì trả đúng các nhánh
    /// bung / menu chọn / vị trí cuộn đó; không có thì cây đầy đủ gập hết (cấp con nạp lười), cây đang lọc bung hết cho thấy ngay menu khớp.</summary>
    private void RenderView(TreeState? restore)
    {
        var shown = _rootsAll;
        if (_viewText is not null) shown = WCommandService.FilterText(shown, _viewText);
        if (_customOnly) shown = WCommandService.FilterCustom(shown);

        TreeNode? top = null;
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            if (_rootsAll.Count == 0 && _emptyNote is not null) _tree.Nodes.Add(new TreeNode(_emptyNote) { ForeColor = Color.Gray });
            foreach (var root in shown)
                _tree.Nodes.Add(ToTreeNode(root));

            // Không lọc (đang xem toàn bộ ~1600 dòng wcommand): chỉ hiện node cha, gấp lại với placeholder "...",
            // cấp con nạp lười khi người dùng tự bấm mở (xem BeforeExpand) để khỏi treo UI.
            // CÓ lọc (tập đã gọn, chỉ còn đúng các nhánh khớp): bung hết luôn cho thấy ngay menu con khớp nằm ở đâu.
            if (restore is not null) top = ApplyState(restore);
            else if (!IsPlainView)
            {
                foreach (TreeNode root in _tree.Nodes)
                    ExpandRecursive(root);
                if (_tree.Nodes.Count > 0) _tree.Nodes[0].EnsureVisible();   // bung hết xong cuộn về đầu kết quả
            }
        }
        finally
        {
            _tree.EndUpdate();
        }
        if (top is not null) { try { _tree.TopNode = top; } catch { /* node chưa hiện */ } }
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

    /// <summary>Cây menu đầy đủ (không lọc) của lần tải gần nhất — hộp "Chọn menu…" của Gen Update dựng cây theo đây, không hỏi lại database. Chỉ đọc.</summary>
    public IReadOnlyList<WCommandItem> RootsAll => _rootsAll;

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
        if (_nativeFilter is not null) _nativeFilter.Text = "";
        await SetViewAsync("", _customOnly, refresh: false);
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

    /// <summary>Đưa focus bàn phím vào cây menu — Command Palette chọn menu xong gọi để phím tắt của cây (Ctrl+4, Ctrl+F5, F4…) ăn ngay.</summary>
    public void FocusTree() { if (_tree.IsHandleCreated && _tree.CanFocus) _tree.Focus(); }

    private WCommandItem? SelectedItem => _tree.SelectedNode?.Tag as WCommandItem;

    /// <summary>Ctrl+F5 (cấu hình được: Template → Phím tắt, nhóm "Cây menu WCommand") khi cây đang có focus: chạy menu đang chọn. Cây bắt phím trước phím cùng tổ hợp ở phạm vi cửa sổ.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!IsMenuTable && _tree.ContainsFocus && Bcode.App.UI.ShortcutRegistry.FromKeys(keyData) is { } combo
            && combo == Bcode.App.UI.ShortcutRegistry.Get("wcommand.run") && SelectedItem is not null)
        {
            RunSelectedMenu();
            return true;
        }
        if (!IsMenuTable && _tree.ContainsFocus && RunInFboRequested is not null && SelectedItem is { } fboItem
            && Bcode.App.UI.ShortcutRegistry.FromKeys(keyData) is { } fboCombo && fboCombo == Bcode.App.UI.ShortcutRegistry.Get("wcommand.runFbo"))
        {
            RunInFboRequested(fboItem);
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

    /// <summary>Bảng mà form COMMAND/DVCOMMAND - Edit sửa: bảng của cây (chế độ <see cref="IsMenuTable"/>) hoặc command của sản phẩm APP.</summary>
    private AppCommandService AppTable => _menuTable ?? _service.AppCommands;

    /// <summary>Khoá của dòng khi sửa bằng form COMMAND/DVCOMMAND - Edit; null = dòng wcommand (form WCOMMAND - Edit).</summary>
    private string? AppKey(WCommandItem item) => _menuTable is not null ? item.WMenuId : item.IsAppCommand ? item.MenuId : null;

    /// <summary>Menu dạng APP (chỉ có bảng command): mở form COMMAND - Edit tổng quát. existingId = null → New (mượn dữ liệu của templateId nếu có).</summary>
    private void OpenAppEditForm(string? existingId, string? templateId)
    {
        if (_appEditForm is { IsDisposed: false } open)
        {
            _ = open.SwitchToAsync(existingId, templateId);
            open.Activate();
            return;
        }
        var form = new AppCommandEditForm(AppTable, existingId, templateId, _tableDir);
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
        if (IsMenuTable) { OpenAppEditForm(null, template?.WMenuId); return Task.CompletedTask; }
        if (template is { IsAppCommand: true }) { OpenAppEditForm(null, template.MenuId); return Task.CompletedTask; }
        OpenEditForm(null, template);
        return Task.CompletedTask;
    }

    private Task EditSelectedAsync()
    {
        var item = SelectedItem;
        if (item is null) return Task.CompletedTask;
        if (AppKey(item) is { } key) { OpenAppEditForm(key, null); return Task.CompletedTask; }
        OpenEditForm(item, null);
        return Task.CompletedTask;
    }

    private async Task DeleteSelectedAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        if (AppKey(item) is { } key)
        {
            if (MessageBox.Show(this, $"Xóa menu '{item.Bar}' ({key}) khỏi bảng {AppTable.Spec.Table}?", "Bcode — Command", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { await AppTable.DeleteAsync(key); await ReloadAsync(); }
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

        using var form = new AddSourceForm(_settings.SourceCollectionRoots, string.Join("; ", patterns), ws.SourcePath,
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

        // Menu có menu mẹ trên cây → hỏi gen kèm menu mẹ: Có = hộp tick các menu mẹ (gốc trước) + menu đang chọn, script gom theo thứ tự cha trước con
        // (chạy ở DB khác không thiếu menu cha); Không = chỉ menu đang chọn như cũ; Huỷ / Esc = thôi. Menu gốc thì không hỏi.
        var parents = new List<WCommandItem>();
        for (var n = _tree.SelectedNode?.Parent; n is not null; n = n.Parent)
            if (n.Tag is WCommandItem p) parents.Insert(0, p);
        var items = new List<WCommandItem> { item };
        if (parents.Count > 0)
        {
            var ask = MessageBox.Show(this,
                $"Menu \"{DisplayName(item)}\" ({item.WMenuId}) có {parents.Count} menu mẹ:\n" +
                string.Join("\n", parents.Select((p, i) => new string(' ', i * 3) + $"• {DisplayName(p)} ({p.WMenuId})")) +
                "\n\nCó muốn gen cả menu mẹ không?\nYes = chọn menu mẹ cần gen kèm · No = chỉ gen menu đang chọn · Cancel = thôi",
                "Bcode — Gen Script Menu", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (ask == DialogResult.Cancel) return;
            if (ask == DialogResult.Yes)
            {
                var chain = parents.Append(item).ToList();
                var picked = Forms.MenuPickerForm.PickChain(FindForm(), chain);
                if (picked is null || picked.Count == 0) return;
                var set = new HashSet<string>(picked, StringComparer.OrdinalIgnoreCase);
                items = chain.Where(c => set.Contains(c.WMenuId)).ToList();   // giữ thứ tự cha trước con
                if (items.Count == 0) return;
            }
        }

        var parts = new List<string>();
        foreach (var it in items)
        {
            string one;
            if (_menuTable is not null)
            {
                try { one = await _menuTable.GenerateScriptAsync(it.WMenuId); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bcode — Gen Script Menu", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
            else
            // Chỉ ghi các cột bảng wcommand / command thật sự có ở database này (đọc cấu trúc ở nền, có cache).
            one = await Task.Run(() => _service.GenerateScriptForCurrentDb(it));
            // 1 menu: y như trước. Nhiều menu: mỗi menu 1 dòng chú thích "-- Menu: ..." (như Script menu của Note (New)).
            parts.Add(items.Count == 1 ? one : $"-- Menu: {it.Bar} ({it.WMenuId})\r\n" + one.TrimEnd() + "\r\n");
        }
        var script = string.Join(Environment.NewLine, parts);
        var title = items.Count == 1 ? $"Script — {items[0].WMenuId}" : $"Script — {item.WMenuId} + {items.Count - 1} menu mẹ";
        if (items.Count > 1 && !items.Contains(item)) title = $"Script — {items.Count} menu mẹ của {item.WMenuId}";
        using var form = new WCommandScriptForm(script, title);
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
            if (n.Tag is WCommandItem parentItem) parents.Insert(0, DisplayName(parentItem));

        var leaf = node.Tag is WCommandItem item ? $"{DisplayName(item)} ({item.WMenuId})" : node.Text;
        parents.Add(leaf);
        return string.Join(" \\ ", parents);
    }

    /// <summary>Các phân hệ cấp ngoài cùng của cây (mã = đoạn đầu wmenu_id, tên menu) — để Template giao diện gợi ý khi đặt màu theo phân hệ.</summary>
    public List<(string Code, string Name)> TopGroups() =>
        _tree.Nodes.Cast<TreeNode>().Select(n => n.Tag as WCommandItem).Where(i => i is not null)
            .Select(i => ((i!.WMenuId ?? "").Split('.')[0].Trim(), i.Bar)).Where(g => g.Item1.Length > 0).Distinct().ToList();

    private static TreeNode ToTreeNode(WCommandItem item)
    {
        var node = new TreeNode(NodeText(item)) { Tag = item };
        ApplyGroupColor(node, item);
        if (item.Children.Count > 0)
            node.Nodes.Add(new TreeNode("...")); // lazy placeholder — replaced in BeforeExpand
        return node;
    }

    // ---- Menu Name: hiện Bar (tiếng Việt) hay Bar2 (tiếng Anh) -------------------------------------------

    /// <summary>Bắn khi đổi Menu Name ở bất kỳ cây nào — mọi cây WCommand / Mobile đang mở đổi tên node theo.</summary>
    private static event Action? MenuNameChanged;

    /// <summary>Tên hiển thị của menu: Bar2 khi đang chọn Menu Name: Bar2 (trống thì vẫn dùng Bar), không thì Bar.</summary>
    internal static string DisplayName(WCommandItem item) =>
        ShowBar2 && !string.IsNullOrWhiteSpace(item.Bar2) ? item.Bar2 : item.Bar;

    // Đọc file cờ 1 lần (DisplayName chạy cho từng node, ~1600 menu).
    private static bool? _showBar2;
    private static bool ShowBar2 => _showBar2 ??= AppSettings.MenuNameBar2;

    private static string NodeText(WCommandItem item) => $"{DisplayName(item)}  ({item.WMenuId})";

    /// <summary>Mục menu chuột phải: ghi tên cột sẽ chuyển sang (như FCode "Menu Name: Bar2").</summary>
    private static string MenuNameToggleLabel => ShowBar2 ? "Menu Name: Bar (tiếng Việt)" : "Menu Name: Bar2 (tiếng Anh)";

    private static void ToggleMenuName()
    {
        _showBar2 = !ShowBar2;
        AppSettings.MenuNameBar2 = _showBar2.Value;
        MenuNameChanged?.Invoke();
    }

    /// <summary>Đổi chữ các node đã dựng (node con chưa bung tự lấy tên mới khi bung — xem ToTreeNode).</summary>
    private void RelabelNodes()
    {
        _tree.BeginUpdate();
        try
        {
            void Walk(TreeNodeCollection nodes)
            {
                foreach (TreeNode n in nodes)
                {
                    if (n.Tag is WCommandItem item) n.Text = NodeText(item);
                    Walk(n.Nodes);
                }
            }
            Walk(_tree.Nodes);
        }
        finally { _tree.EndUpdate(); }
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