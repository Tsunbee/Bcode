using System.Data;
using System.Diagnostics;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

using static Bcode.App.UI.ThemeManager;
namespace Bcode.App.Forms;

public class MainForm : Bcode.App.UI.ThemedForm
{
    private readonly AppSettings _settings;
    private readonly DbConnectionService _connections = new();
    private readonly WCommandService _wcommandService;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly PeriodTableQueryService _periods = new();
    private readonly SqlQueryService _sqlQueryService;
    private readonly GenInsertService _genInsert = new();

    private readonly GenUpdateService _genUpdate = new();
    private readonly DataScriptService _dataScript = new();
    private readonly FileLookupService _fileLookupService = new();
    private readonly ScriptFileService _scriptFileService = new();
    private readonly SnippetLibraryService _snippets;
    private readonly RawSqlService _rawSqlService;
    private readonly TableDataService _tableDataService;
    private readonly LookupService _lookupService;
    private readonly FileReferenceService _fileReferenceService = new();
    private readonly ChangeOwnerService _changeOwnerService;
    private readonly NoteService _noteService = new();
    private readonly AdvanceNoteService _advanceNoteService = new();
    private readonly GenAllService _genAllService;
    private TabPage? _advanceNoteTab;

    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _topBarWeb = new();
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _iconRailWeb = new();
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _statusBarWeb = new();
    private Func<WebMenu> _settingsMenu = null!;
    private SqlObjectTreeControl _sqlObjectTree = null!;
    private WCommandTreeControl _wcommandTree = null!;
    private readonly Panel _leftContentHost = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, Control> _leftSections = new();
    private readonly Bcode.App.Controls.FlatTabControl _documentTabs;
    private readonly Panel _quickAccessOverlay = new() { BackColor = SystemColors.Control };
    private DataTable? _lastQueryResult;

    private readonly ToolStrip _toolsBar = new();
    private Panel _headerContainer = null!;

    /// <summary>Khung đầu cửa sổ cao = thanh trên (WebView2, tự cao theo nội dung) + thanh công cụ (tự xuống dòng).</summary>
    private void UpdateHeaderHeight()
    {
        if (_headerContainer is null) return;
        _headerContainer.Height = _topBarWeb.Height + _toolsBar.Height;
    }
    private readonly List<(string key, string label, string? shortcut, EventHandler action)> _toolSpecs = new();
    private TabPage? _fileLookupTabPage;
    private Controls.FileLookupControl? _fileLookupControl;
    private TabPage? _genUpdatePackageTabPage;
    private Controls.GenUpdatePackageControl? _genUpdatePackageControl;
    private TabPage? _lookupTabPage;
    private TabPage? _fileReferenceTabPage;
    private TabPage? _rawSqlTabPage;
    private RawSqlControl? _rawSqlControl;
    private readonly Dictionary<string, TabPage> _noteTabs = new();
    private readonly Dictionary<string, TabPage> _objectTabs = new();
    private readonly Dictionary<string, TabPage> _procedureQueryTabs = new();
    private readonly Dictionary<string, TabPage> _debugTargetTabs = new();
    
    // 1. ĐÃ BỔ SUNG BIẾN NÀY ĐỂ TRÁNH LỖI Ở HÀM OpenCompareTextTab
    private TabPage? _compareTextTab;
    private TabPage? _sqlProfilerTab;
    private readonly ViewerControlServer _viewerControl = new();
    private QuickLaunchLoginForm? _quickLaunchForm;

    /// <summary>MainForm tự dàn bằng Dock + thanh web báo chiều cao, nên không nhân bố cục thêm theo UiScale.</summary>
    protected override bool ScaleLayoutWithUiScale => false;

    public MainForm()
    {
        _settings = AppSettings.Load();
        Bcode.App.UI.UiThemes.ApplyPalettes(); // theme/màu người dùng chọn — trước khi dựng control
        Bcode.App.UI.UiScale.SetMode(_settings.UiScale, this);
        FileLookupService.CacheMode = Enum.TryParse<FileLookupCacheMode>(_settings.FileLookupCacheMode, true, out var cacheMode)
            ? cacheMode : FileLookupCacheMode.On;
        _wcommandService = new WCommandService(_connections);
        _sqlObjectService = new SqlObjectBrowserService(_connections);
        _genAllService = new GenAllService(_fileLookupService, _sqlObjectService, _wcommandService);
        _sqlQueryService = new SqlQueryService(_connections, _periods);
        _snippets = new SnippetLibraryService(_settings.LibraryPath);
        _rawSqlService = new RawSqlService(_connections);
        _tableDataService = new TableDataService(_connections, _periods);
        _lookupService = new LookupService(_connections);
        _changeOwnerService = new ChangeOwnerService(_connections);

        // Ctrl+3 bấm khi con trỏ đang nằm trong 1 trang WebView2 (editor SQL, thanh công cụ...) — xem WebViewEnvironment.GlobalShortcut.
        // Chỉ nhận khi cửa sổ chính đang là cửa sổ hoạt động (không mở tab sau lưng 1 hộp thoại đang bật).
        WebViewEnvironment.GlobalShortcut += OnWebGlobalShortcut;
        Disposed += (_, _) => WebViewEnvironment.GlobalShortcut -= OnWebGlobalShortcut;

        // F5 trong BcodeViewer: lưu file xong gửi sang đây để bung FSG FBO chạy menu của file đó.
        _viewerControl.RunMenuRequested += (path, project) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(async () => await RunMenuForFileAsync(path, project))); }
            catch (InvalidOperationException) { /* cửa sổ đang đóng */ }
        };
        _viewerControl.Start();
        Disposed += (_, _) => _viewerControl.Dispose();

        Text = "Bcode";
        Width = 1280;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        if (Bcode.App.UI.AppIcons.AppIcon is { } appIcon) Icon = appIcon;

        _topBarWeb.Dock = DockStyle.Top;
        _topBarWeb.Height = 46;
        _iconRailWeb.Dock = DockStyle.Left;
        _iconRailWeb.Width = 52;
        _statusBarWeb.Dock = DockStyle.Bottom;
        _statusBarWeb.Height = 26;

        _settingsMenu = () => new WebMenu()
            .Add("Choose Server / Workspaces...", OpenConnectionSettings)
            .Add("Tỉ lệ giao diện...", ChooseUiScale)
            .Add("Giao diện (Template)...", OpenUiTemplate)
            .AddCaption("Database")
            .Add("Backup Database...", async () => await BackupDatabaseAsync())
            .Add("Restore Database...", () => MessageBox.Show(this,
                "Restore là thao tác có rủi ro cao (ghi đè database) nên chưa bật sẵn.\nGợi ý cài đặt: dùng RESTORE DATABASE ... FROM DISK, chạy trên kết nối master, " +
                "và bắt xác nhận rõ ràng (gõ lại tên database) trước khi chạy.", "Bcode — Restore Database"))
            .Add("Attach Database...", () => MessageBox.Show(this,
                "Gợi ý cài đặt: CREATE DATABASE ... ON (FILENAME = '<mdf>') FOR ATTACH, chạy trên kết nối master.", "Bcode — Attach Database"))
            .AddSeparator()
            .Add("Exit", Close);

        _toolSpecs.Add(("sql_query", "SQL Query", "Q", (_, _) => OpenFreeScriptTab()));
        _toolSpecs.Add(("lookup", "Lookup", "L", (_, _) => OpenLookupTab()));
        _toolSpecs.Add(("table", "Table", "T", (_, _) => OpenTableTab()));
        _toolSpecs.Add(("command", "Command", "C", (_, _) => OpenSelectBuilderTab()));
        _toolSpecs.Add(("wcommand", "WCommand", "W", (_, _) => SelectWCommandTab()));
        _toolSpecs.Add(("file_lookup", "File Lookup", "F", (_, _) => OpenFileLookupTab()));
        _toolSpecs.Add(("gen_update_package", "Gen Update", "G", (_, _) => OpenGenUpdatePackageTab()));
        _toolSpecs.Add(("file_reference", "File Reference", "R", (_, _) => OpenFileReferenceTab()));
        _toolSpecs.Add(("change_owner", "Change Owner", "O", (_, _) => OpenChangeOwnerDialog()));
        _toolSpecs.Add(("gen_update", "Gen Update (Result)", "U", (_, _) => GenUpdateFromLastResult()));
        _toolSpecs.Add(("note", "Note", "E", (_, _) => OpenNoteTab(NoteService.DefaultNoteName)));
        _toolSpecs.Add(("note_new", "Note (New)", "4", (_, _) => OpenAdvanceNoteTab()));
        _toolSpecs.Add(("create_processing", "Create Processing", null, (_, _) => new CreateProcessingForm().ShowDialog(this)));
        _toolSpecs.Add(("check_mail", "Check Mail", null, (_, _) => new CheckMailForm().ShowDialog(this)));
        _toolSpecs.Add(("compare_text", "Compare Text", null, (_, _) => OpenCompareTextTab()));
        _toolSpecs.Add(("string_beauty", "String Beauty", null, (_, _) => new StringBeautyForm().ShowDialog(this)));
        _toolSpecs.Add(("library", "Library...", null, (_, _) => OpenLibrary()));
        _toolSpecs.Add(("decrypt_sql_object", "Decrypt SQL Object", null, (_, _) => new DecryptSqlObjectForm(_settings, _connections).ShowDialog(this)));
        _toolSpecs.Add(("setup_einvoice", "Setup eInvoice (FE)", null, (_, _) => new SetupEInvoiceForm(_connections).ShowDialog(this)));
        _toolSpecs.Add(("create_rpt_xlsx", "Create *.rpt, *.xlsx", null, (_, _) => new CreateRptXlsxForm(_lastQueryResult).ShowDialog(this)));
        _toolSpecs.Add(("compare_structure", "Compare Structure", null, (_, _) => new CompareStructureForm(_settings).ShowDialog(this)));
        _toolSpecs.Add(("view_rpt_fec", "View Rpt in FEC", null, (_, _) => new ViewRptInFecForm().ShowDialog(this)));
        _toolSpecs.Add(("fsg_crawler", "FSG Yêu cầu", null, (_, _) => new FsgRequirementCrawlerForm().Show()));
        _toolSpecs.Add(("quick_launch", "FSG FBO", null, (_, _) => OpenQuickLaunchLogin()));
        _toolSpecs.Add(("sql_profiler", "SQL Profiler", null, (_, _) => OpenSqlProfilerTab()));
        _toolSpecs.Add(("api_config", "Khai báo API", null, (_, _) => new ApiDeclarationForm().ShowDialog(this)));
        _toolSpecs.Add(("api_schema_builder", "Tạo cấu trúc API", null, (_, _) => new ApiSchemaBuilderForm(_sqlObjectService, _tableDataService).ShowDialog(this)));
        _toolSpecs.Add(("catalog_clone", "Clone danh mục", null, (_, _) => new CatalogCloneForm(_sqlObjectService, _tableDataService, _connections).ShowDialog(this)));
        RebuildToolsBar();
        void OnTemplateChanged() { RebuildToolsBar(); PushTopBarLayout(); }
        Bcode.App.UI.UiTemplate.Changed += OnTemplateChanged;
        Disposed += (_, _) => Bcode.App.UI.UiTemplate.Changed -= OnTemplateChanged;
        // Thanh công cụ native: màn hình hẹp thì XUỐNG DÒNG (cao thêm) thay vì giấu bớt nút vào mũi tên ">>".
        _toolsBar.LayoutStyle = ToolStripLayoutStyle.Flow;
        _toolsBar.CanOverflow = false;
        _toolsBar.AutoSize = true;
        _toolsBar.GripStyle = ToolStripGripStyle.Hidden;
        if (_toolsBar.LayoutSettings is FlowLayoutSettings toolsFlow) toolsFlow.WrapContents = true;

        _headerContainer = new Panel { Dock = DockStyle.Top, Height = _topBarWeb.Height + _toolsBar.Height };
        _headerContainer.Controls.Add(_toolsBar);
        _headerContainer.Controls.Add(_topBarWeb);
        _toolsBar.SizeChanged += (_, _) => UpdateHeaderHeight();
        var headerContainer = _headerContainer;

        var sqlObjectTree = new SqlObjectTreeControl(_sqlObjectService) { Dock = DockStyle.Fill };
        sqlObjectTree.ObjectActivated += async obj => await OpenObjectDefinitionAsync(obj);
        _sqlObjectTree = sqlObjectTree;

        var wcommandTree = new WCommandTreeControl(_wcommandService, _fileLookupService, () => _connections.Current, _settings) { Dock = DockStyle.Fill };
        wcommandTree.NodeActivated += item => OpenWCommandItem(item);
        _wcommandTree = wcommandTree;

        var mobilePanel = new Panel { Dock = DockStyle.Fill };
        mobilePanel.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = 120,
            Text = "Mobile: duyệt cấu trúc màn hình app di động (tương đương \"Mobile Path\" trong FCode).\n" +
                   "Dùng lại FileLookupControl trỏ tới thư mục con dành cho mobile trong Source Path khi bạn xác định " +
                   "được đúng quy ước thư mục trên server của mình.",
            Padding = new Padding(6)
        });

        _leftSections["sql_object"] = sqlObjectTree;
        _leftSections["wcommand"] = wcommandTree;
        _leftSections["mobile"] = mobilePanel;
        _leftContentHost.Controls.Add(sqlObjectTree);
        _leftContentHost.Controls.Add(wcommandTree);
        _leftContentHost.Controls.Add(mobilePanel);
        SelectLeftSection("sql_object");

        var leftContainer = new Panel { Dock = DockStyle.Fill };
        leftContainer.Controls.Add(_leftContentHost);
        leftContainer.Controls.Add(_iconRailWeb);

        _documentTabs = new Bcode.App.Controls.FlatTabControl { Dock = DockStyle.Fill };
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(_documentTabs, true, null);

        Bcode.App.UI.ThemeManager.MakeClosable(_documentTabs, CloseDocumentTab);
        _documentTabs.SizeChanged += (_, _) => UpdateQuickAccessOverlayBounds();

        _quickAccessOverlay.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) BuildQuickAccessMenu().Show(_quickAccessOverlay, e.X, e.Y);
        };

        // Bổ sung: chuột phải ngay trên _documentTabs (thanh tab, hoặc — lúc CHƯA có tab nào
        // mở, tức khung vừa mở Bcode lên còn trắng — toàn bộ vùng khung tài liệu) cũng hiện
        // menu "Mở nhanh" giống hệt _quickAccessOverlay ở trên. Trước đó menu này CHỈ bấm
        // được ở đúng dải hẹp bên phải tab cuối cùng (_quickAccessOverlay): chưa có tab nào
        // thì dải đó cao đúng 1 dòng header tab (~26px), còn cả khung to bên dưới nó lại
        // không phản hồi chuột phải gì cả — đúng như Bee báo. _documentTabs chỉ thực sự nhận
        // được sự kiện chuột ở phần bề mặt của chính nó (thanh header tab, và toàn bộ khung
        // khi chưa có TabPage nào che phủ) — khi 1 tab đang mở, nội dung bên trong tab đó
        // (ScriptEditorControl...) che kín nên chuột phải trong lúc đang có tab vẫn ra đúng
        // menu riêng của control đó như trước, không bị đè bởi menu "Mở nhanh" này.
        _documentTabs.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            // Chuột phải đúng lên 1 tab → menu tab (Pin / Close...); lên vùng trống của thanh tab → menu "Mở nhanh" như trước.
            for (var i = 0; i < _documentTabs.TabPages.Count; i++)
                if (_documentTabs.GetTabRect(i).Contains(e.Location))
                {
                    BuildTabContextMenu(_documentTabs.TabPages[i]).Show(_documentTabs, e.X, e.Y);
                    return;
                }
            BuildQuickAccessMenu().Show(_documentTabs, e.X, e.Y);
        };

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1
        };
        split.Panel1.Controls.Add(leftContainer);
        split.Panel2.Controls.Add(_documentTabs);
        split.Panel2.Controls.Add(_quickAccessOverlay);
        UpdateQuickAccessOverlayBounds();

        Controls.Add(split);
        Controls.Add(_statusBarWeb);
        Controls.Add(headerContainer);
        Load += (_, _) => split.SplitterDistance = 312;
        
        if (_settings.Workspaces.Count > 0)
        {
            var last = _settings.Workspaces.FindIndex(w => w.Name == _settings.LastWorkspace);
            SelectWorkspace(last >= 0 ? last : 0);
        }
        // Màn hình Projects khi mới mở Bcode: lọc/chọn nhanh project đã khai báo (đóng đi thì giữ project dùng gần nhất).
        Shown += (_, _) => BeginInvoke(new Action(() => ShowProjectPicker()));

        _ = InitShellWebViewsAsync();

        async Task InitShellWebViewsAsync()
        {
            try
            {
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_topBarWeb);
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_iconRailWeb);
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_statusBarWeb);
                const string host = Bcode.App.UI.WebViewEnvironment.Host;

                _topBarWeb.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var root = doc.RootElement;
                    switch (root.GetProperty("action").GetString())
                    {
                        case "__height":
                            // Trang topbar báo chiều cao nội dung thật (px thiết bị) → thanh cao thêm khi hẹp và xuống dòng.
                            _topBarWeb.Height = Math.Clamp(root.GetProperty("height").GetInt32() + 1, Bcode.App.UI.DpiScale.Px(this, 40), Bcode.App.UI.DpiScale.Px(this, 260));
                            UpdateHeaderHeight();
                            break;
                        case "settings":
                            if (WebMenu.JustDismissed) break; // cú bấm này vừa đóng menu đang mở → coi như "bấm lần nữa để đóng"
                            _settingsMenu().Show(_topBarWeb, 10, _topBarWeb.Height);
                            break;
                        case "quickaccess":
                            // Không mở hộp thoại (có WebView2 riêng) NGAY trong handler message của WebView2 thanh trên: tạo WebView2 mới
                            // từ trong callback của WebView2 khác gây lỗi "Class not registered". Để handler trả về rồi mới mở.
                            BeginInvoke(new Action(OpenQuickAccess));
                            break;
                        case "theme":
                            Bcode.App.UI.ThemeManager.Toggle(this);
                            PushThemeToShell();
                            break;
                        case "script":
                            switch (root.GetProperty("which").GetString())
                            {
                                case "add": AddScript(); break;
                                case "view": ViewScriptCart(); break;
                                case "clear": _scriptFileService.ClearCart(); break;
                                case "save": SaveActiveScript(); break;
                                case "copy": CopyActiveScript(); break;
                            }
                            break;
                        case "select-ws":
                        {
                            // Ô chọn ở topbar giờ liệt kê database của project đang vào — index là vị trí trong _topDbKinds.
                            var i = root.GetProperty("index").GetInt32();
                            if (i >= 0 && i < _topDbKinds.Count) ApplyActiveDatabase(_topDbKinds[i]);
                            break;
                        }
                        case "select-db":
                            ApplyActiveDatabase(root.GetProperty("which").GetString() == "sys");
                            break;
                        case "show-actions-menu":
                            if (WebMenu.JustDismissed) break; // bấm lần nữa vào nút Actions để đóng menu đang mở
                            var actionsMenu = BuildActionsMenu();
                            WebMenu.Track(actionsMenu); // tự đóng khi bấm ra ngoài (kể cả trên WebView2)
                            actionsMenu.Show(_topBarWeb, 100, _topBarWeb.Height); // Tọa độ x=100 tạm tính để thả xuống đúng chỗ nút Actions
                            break;
                    }
                };

                _iconRailWeb.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    if (doc.RootElement.GetProperty("key").GetString() is { } key) SelectLeftSection(key);
                };

                _topBarWeb.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    PushWorkspacesToTopBar();
                    PushThemeToShell();
                };
                _iconRailWeb.CoreWebView2.NavigationCompleted += (_, _) => PushThemeToShell();
                _statusBarWeb.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    PushThemeToShell();
                    if (_connections.Current is { } cur) PushStatus($"Workspace: {cur.Name}  —  Server: {cur.Server}  |  Dev: HàoTN|PhongNT");
                };

                _topBarWeb.CoreWebView2.Navigate($"https://{host}/topbar.html");
                _iconRailWeb.CoreWebView2.Navigate($"https://{host}/iconrail.html");
                _statusBarWeb.CoreWebView2.Navigate($"https://{host}/statusbar.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Không khởi tạo được phần giao diện mới (top bar/icon rail/status bar, dùng WebView2).\n" +
                    "Kiểm tra máy đã có WebView2 Runtime chưa (thường có sẵn qua Edge trên Windows 10/11 — " +
                    "nếu chưa, tải \"WebView2 Runtime\" (Evergreen Bootstrapper) từ trang Microsoft rồi mở lại Bcode).\n\n" +
                    "Chi tiết lỗi: " + ex.Message,
                    "Bcode — WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void SelectWorkspace(int index)
    {
        if (index < 0 || index >= _settings.Workspaces.Count) return;
        var ws = _settings.Workspaces[index];
        _connections.SetWorkspace(ws);
        RememberWorkspace(ws);
        _topDbSys = false; // đổi project → quay về App Data
        PushWorkspacesToTopBar();
        PushDbNamesToTopBar(ws);
        PushStatus($"Workspace: {ws.Name}  —  Server: {ws.Server}  |  Dev: HàoTN|PhongNT");

        _ = _wcommandTree.ReloadAsync();

        if (_fileLookupControl is not null && !string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            _fileLookupControl.ProjectName = ws.Name;
            _fileLookupControl.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"));
        }

        if (_fileReferenceTabPage is not null && !string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            var frc = _fileReferenceTabPage.Controls.OfType<FileReferenceControl>().FirstOrDefault();
            frc?.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"));
        }
    }

    private void RememberWorkspace(Workspace ws)
    {
        _settings.LastWorkspace = ws.Name;
        _settings.RecentWorkspaces.Remove(ws.Name);
        _settings.RecentWorkspaces.Insert(0, ws.Name);
        if (_settings.RecentWorkspaces.Count > 10) _settings.RecentWorkspaces.RemoveRange(10, _settings.RecentWorkspaces.Count - 10);
        try { _settings.Save(); } catch { /* không lưu được thì chỉ mất "Last Access", không ảnh hưởng việc chọn */ }
    }

    /// <summary>Màn hình Projects: lọc / chọn nhanh project đã khai báo (mở lúc khởi động và qua Actions &gt; Projects).</summary>
    private void ShowProjectPicker()
    {
        if (IsDisposed) return;
        using var picker = new ProjectPickerForm(_settings, _connections);
        var result = picker.ShowDialog(this);
        PushWorkspacesToTopBar(); // New/Edit/Delete trong màn hình có thể đã đổi danh sách
        if (result == DialogResult.OK && picker.Chosen is { } chosen)
        {
            var idx = _settings.Workspaces.IndexOf(chosen);
            if (idx >= 0) SelectWorkspace(idx);
        }
        // Không chọn gì: PushWorkspacesToTopBar ở trên đã đồng bộ lại ô database ở topbar, không nạp lại workspace.
    }

    /// <summary>Choose Server (Ctrl+O, File/Actions &gt; Choose Server): mở ĐÚNG màn hình Projects như lúc vừa vào Bcode — danh sách
    /// project có ô lọc + Last Access; New/Edit/Delete và Synchronize (Ctrl+F5) đều nằm trong đó (xem ProjectPickerForm).</summary>
    private void OpenConnectionSettings() => ShowProjectPicker();

    private void PushDbNamesToTopBar(Bcode.App.Models.Workspace ws)
    {
        if (_topBarWeb.CoreWebView2 is null) return;
        var sysArg = System.Text.Json.JsonSerializer.Serialize(ws.SysDatabase);
        var appArg = System.Text.Json.JsonSerializer.Serialize(ws.AppDatabase);
        _ = _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setDbNames && window.setDbNames({sysArg}, {appArg})");
    }
    // Ô chọn ở topbar KHÔNG còn là danh sách project: chỉ liệt kê 2 database (App, Sys) của project đang vào, để chuyển nhanh
    // giữa chúng khi chạy SQL Query. Muốn sang project khác thì dùng Choose Server / Ctrl+O.
    private bool _topDbSys;
    private readonly List<bool> _topDbKinds = new(); // theo thứ tự các mục trong ô chọn: true = Sys, false = App


    /// <summary>Đẩy thứ tự + chữ + font/màu các nút Script (template giao diện) xuống thanh trên.</summary>
    private void PushTopBarLayout()
    {
        if (_topBarWeb.CoreWebView2 is null) return;
        var t = Bcode.App.UI.UiTemplate.Current;
        var items = new Dictionary<string, object>();
        foreach (var (id, _) in Bcode.App.UI.UiTemplate.ScriptButtons)
        {
            t.Items.TryGetValue("script:" + id, out var st);
            items[id] = new { text = string.IsNullOrEmpty(st?.Text) ? null : st.Text, css = Bcode.App.UI.UiTemplate.ToInlineCss(st) };
        }
        var json = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Serialize(new { order = t.ScriptOrder, items }));
        _ = _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.applyLayout && window.applyLayout({json})");
    }
    private void PushWorkspacesToTopBar()
    {
        if (_topBarWeb.CoreWebView2 is null) return;
        _topDbKinds.Clear();
        var names = new List<string>();
        if (_connections.Current is { } ws)
        {
            if (!string.IsNullOrWhiteSpace(ws.AppDatabase)) { names.Add($"{ws.Name} — {ws.AppDatabase}  (App)"); _topDbKinds.Add(false); }
            if (!string.IsNullOrWhiteSpace(ws.SysDatabase)) { names.Add($"{ws.Name} — {ws.SysDatabase}  (Sys)"); _topDbKinds.Add(true); }
        }
        var arg = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Serialize(names.ToArray()));
        var sel = Math.Max(0, _topDbKinds.IndexOf(_topDbSys));
        _ = _topBarWeb.CoreWebView2.ExecuteScriptAsync(
            $"window.setWorkspaces && window.setWorkspaces({arg}); window.setSelectedWs && window.setSelectedWs({sel}); window.setDbView && window.setDbView('{(_topDbSys ? "sys" : "app")}')");
        PushTopBarLayout();
    }

    /// <summary>Chuyển database đang làm việc của project hiện tại sang App hoặc Sys: cập nhật topbar và đổi database của tab SQL
    /// Query đang mở (nếu tab đang mở là SQL Query).</summary>
    private void ApplyActiveDatabase(bool useSys)
    {
        _topDbSys = useSys;
        PushWorkspacesToTopBar();
        if (_documentTabs.SelectedTab?.Controls.OfType<RawSqlControl>().FirstOrDefault() is { } sql)
            sql.SetDatabase(useSys);
        if (_connections.Current is { } ws)
            PushStatus($"Database: {(useSys ? ws.SysDatabase : ws.AppDatabase)} ({(useSys ? "Sys" : "App")})  —  Project: {ws.Name}");
    }

    private void PushThemeToShell()
    {
        var isDark = Bcode.App.UI.AppColors.IsDark ? "true" : "false";
        foreach (var web in new[] { _topBarWeb, _iconRailWeb, _statusBarWeb })
            if (web.CoreWebView2 is not null)
                _ = web.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
    }

    private void PushStatus(string text)
    {
        if (_statusBarWeb.CoreWebView2 is null) return;
        var arg = System.Text.Json.JsonSerializer.Serialize(text);
        _ = _statusBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setStatus && window.setStatus({arg})");
    }

    private void SelectLeftSection(string key)
    {
        foreach (var (sectionKey, control) in _leftSections) control.Visible = sectionKey == key;
        if (key == "wcommand") _ = _wcommandTree.ReloadAsync();
        if (_iconRailWeb.CoreWebView2 is not null)
        {
            var arg = System.Text.Json.JsonSerializer.Serialize(key);
            _ = _iconRailWeb.CoreWebView2.ExecuteScriptAsync($"window.setSelected && window.setSelected({arg})");
        }
    }

    private static readonly HashSet<string> ToolGroupBreaks = new() { "command", "gen_update", "note_new" };

    private void RebuildToolsBar()
    {
        _toolsBar.Items.Clear();
        _toolsBar.Padding = new Padding(4, 2, 4, 2);

        // Đã sắp xếp lại bởi người dùng thì bỏ vạch ngăn nhóm mặc định (các nhóm cũ không còn nằm cạnh nhau nữa).
        var customOrder = _settings.ToolOrder.Count > 0;
        foreach (var (key, label, shortcut, action) in OrderedToolSpecs())
        {
            if (_settings.HiddenToolKeys.Contains(key)) continue;
            var button = new ToolStripButton(label, null, action) { Margin = new Padding(1, 1, 1, 2) };
            if (shortcut is not null) button.ToolTipText = $"{label} (Ctrl+Shift+{shortcut})";
            if (Bcode.App.UI.UiTemplate.Current.Items.TryGetValue("tool:" + key, out var itemStyle)) ApplyItemStyle(button, itemStyle);
            _toolsBar.Items.Add(button);
            if (!customOrder && ToolGroupBreaks.Contains(key)) _toolsBar.Items.Add(new ToolStripSeparator());
        }

        Bcode.App.UI.ThemeManager.Apply(_toolsBar);
    }

    /// <summary>Chữ/font/màu riêng của từng nút công cụ theo template giao diện (UiTemplate.Items["tool:key"]).</summary>
    private static void ApplyItemStyle(ToolStripItem item, Bcode.App.UI.ControlStyle s)
    {
        if (!string.IsNullOrEmpty(s.Text)) item.Text = s.Text;
        if (Bcode.App.UI.UiTemplate.FontOf(s) is { } font) item.Font = font;
        if (Bcode.App.UI.UiTemplate.ParseColor(s.ForeColor) is { } fg) item.ForeColor = fg;
        if (Bcode.App.UI.UiTemplate.ParseColor(s.BackColor) is { } bg) item.BackColor = bg;
    }

    /// <summary>Các nút theo thứ tự người dùng đã sắp (AppSettings.ToolOrder); key không còn tồn tại bị bỏ, nút mới thêm vào cuối.</summary>
    private IEnumerable<(string key, string label, string? shortcut, EventHandler action)> OrderedToolSpecs()
    {
        if (_settings.ToolOrder.Count == 0) return _toolSpecs;
        var byKey = _toolSpecs.ToDictionary(t => t.key);
        var ordered = new List<(string key, string label, string? shortcut, EventHandler action)>();
        foreach (var key in _settings.ToolOrder)
            if (byKey.Remove(key, out var spec)) ordered.Add(spec);
        ordered.AddRange(_toolSpecs.Where(t => byKey.ContainsKey(t.key))); // nút mới chưa có trong thứ tự đã lưu
        return ordered;
    }

    private void OpenQuickAccess()
    {
        var allTools = OrderedToolSpecs().Select(t => (t.key, t.label));
        using var form = new QuickAccessForm(allTools, new HashSet<string>(_settings.HiddenToolKeys), _toolSpecs.Select(t => t.key));
        if (form.ShowDialog(this) != DialogResult.OK) return;

        _settings.HiddenToolKeys = form.HiddenKeys.ToList();
        // Thứ tự trùng mặc định thì lưu rỗng (để nút/vạch ngăn nhóm mặc định hoạt động như cũ).
        _settings.ToolOrder = form.OrderedKeys.SequenceEqual(_toolSpecs.Select(t => t.key)) ? new List<string>() : form.OrderedKeys;
        _settings.Save();
        RebuildToolsBar();
    }

    /// <summary>Tiêu đề tab có thể mở nhiều cái cùng lúc (SQL Query, Command, Table): "SQL Query (1)", "SQL Query (2)"... — lấy số nhỏ nhất
    /// chưa có tab nào đang dùng, nên đóng (1) rồi mở mới thì lại là (1).</summary>
    private string NumberedTabTitle(string baseTitle)
    {
        var used = new HashSet<int>();
        var prefix = baseTitle + " (";
        foreach (TabPage p in _documentTabs.TabPages)
            if (p.Text.StartsWith(prefix, StringComparison.Ordinal) && p.Text.EndsWith(')')
                && int.TryParse(p.Text.AsSpan(prefix.Length, p.Text.Length - prefix.Length - 1), out var n))
                used.Add(n);
        var next = 1;
        while (used.Contains(next)) next++;
        return $"{baseTitle} ({next})";
    }

    private TabPage AddDocumentTab(string title, Control content)
    {
        var page = new TabPage(title);
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        _documentTabs.TabPages.Add(page);
        _documentTabs.SelectedTab = page;
        Bcode.App.UI.ThemeManager.Apply(page);
        UpdateQuickAccessOverlayBounds();
        return page;
    }

    /// <summary>Menu chuột phải trên 1 tab: Pin Tab, Close Tab, Close Other Tabs, Close Tabs to the Right, Close All Tabs.
    /// Close Other/Right/All bỏ qua tab đã ghim (muốn đóng thì Close Tab hoặc bỏ ghim trước).</summary>
    private ContextMenuStrip BuildTabContextMenu(TabPage page)
    {
        var menu = new ContextMenuStrip();
        var pinned = _documentTabs.IsPinned(page);
        menu.Items.Add(new ToolStripMenuItem(pinned ? "Unpin Tab" : "Pin Tab", null, (_, _) => _documentTabs.SetPinned(page, !pinned)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Close Tab", null, (_, _) => CloseDocumentTab(_documentTabs.TabPages.IndexOf(page)))
            { ShortcutKeyDisplayString = "Ctrl+W" });
        menu.Items.Add(new ToolStripMenuItem("Close Other Tabs", null, (_, _) => CloseTabsWhere(p => p != page)));
        menu.Items.Add(new ToolStripMenuItem("Close Tabs to the Right", null, (_, _) =>
        {
            var from = _documentTabs.TabPages.IndexOf(page);
            CloseTabsWhere(p => _documentTabs.TabPages.IndexOf(p) > from);
        }) { Enabled = _documentTabs.TabPages.IndexOf(page) < _documentTabs.TabPages.Count - 1 });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Close All Tabs", null, (_, _) => CloseTabsWhere(_ => true)));
        Bcode.App.UI.ThemeManager.Apply(menu);
        return menu;
    }

    /// <summary>Đóng mọi tab thoả điều kiện (trừ tab đã ghim), từ phải sang trái; tab có thay đổi chưa lưu vẫn hỏi như Close Tab.</summary>
    private void CloseTabsWhere(Func<TabPage, bool> match)
    {
        var targets = _documentTabs.TabPages.Cast<TabPage>().Where(p => !_documentTabs.IsPinned(p) && match(p)).Reverse().ToList();
        foreach (var p in targets) CloseDocumentTab(_documentTabs.TabPages.IndexOf(p));
    }

    private void CloseDocumentTab(int index)
    {
        if (index < 0 || index >= _documentTabs.TabPages.Count) return;
        var page = _documentTabs.TabPages[index];

        if (page.Controls.OfType<ScriptEditorControl>().FirstOrDefault() is { IsDirty: true } editor)
        {
            var choice = MessageBox.Show(this, $"Tab \"{page.Text}\" có thay đổi chưa lưu. Đóng và bỏ qua thay đổi?",
                "Bcode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes) return;
        }

        if (page == _fileLookupTabPage)
        {
            _fileLookupTabPage = null;
            _fileLookupControl = null;
        }
        if (page == _genUpdatePackageTabPage)
        {
            _genUpdatePackageTabPage = null;
            _genUpdatePackageControl = null;
        }
        
        if (page == _rawSqlTabPage)
        {
            _rawSqlTabPage = null;
            _rawSqlControl = null;
        }
        _documentTabs.Pinned.Remove(page);
        _documentTabs.TabPages.RemoveAt(index);
        page.Dispose();
        UpdateQuickAccessOverlayBounds();
    }

    private void UpdateQuickAccessOverlayBounds()
    {
        var headerHeight = _documentTabs.DisplayRectangle.Top;
        if (headerHeight <= 0) headerHeight = Bcode.App.UI.DpiScale.Px(this, 26);

        var lastTabRight = _documentTabs.TabPages.Count > 0
            ? _documentTabs.GetTabRect(_documentTabs.TabPages.Count - 1).Right
            : 0;

        _quickAccessOverlay.Bounds = new Rectangle(lastTabRight, 0, Math.Max(0, _documentTabs.Width - lastTabRight), headerHeight);
        _quickAccessOverlay.BringToFront();
    }

    private WebMenu BuildQuickAccessMenu()
    {
        var menu = new WebMenu().AddCaption("Mở nhanh");
        foreach (var (_, label, shortcut, action) in OrderedToolSpecs())
        {
            if (shortcut is null) continue;
            var handler = action;
            menu.Add(label, () => handler(this, EventArgs.Empty), shortcut: $"Ctrl+Shift+{shortcut}");
        }
        return menu;
    }

    private FileLookupControl? OpenFileLookupTab()
    {
        if (_connections.Current is not { } ws || string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            MessageBox.Show(this, "Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.", "Bcode");
            return null;
        }

        if (_fileLookupControl is not null && _fileLookupTabPage is not null && _documentTabs.TabPages.Contains(_fileLookupTabPage))
        {
            _fileLookupControl.ProjectName = ws.Name;
            _documentTabs.SelectedTab = _fileLookupTabPage;
            return _fileLookupControl;
        }

        var control = new FileLookupControl(_fileLookupService, _scriptFileService, _settings);
        control.ProjectName = ws.Name;
        control.FileActivated += path => OpenFileFromLookup(path);
        _fileLookupTabPage = AddDocumentTab("File Lookup", control);
        _fileLookupControl = control;
        control.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"));
        return control;
    }

    private GenUpdatePackageControl? OpenGenUpdatePackageTab()
    {
        if (_connections.Current is not { } ws || string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            MessageBox.Show(this, "Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.", "Bcode");
            return null;
        }

        if (_genUpdatePackageControl is not null && _genUpdatePackageTabPage is not null && _documentTabs.TabPages.Contains(_genUpdatePackageTabPage))
        {
            _documentTabs.SelectedTab = _genUpdatePackageTabPage;
            return _genUpdatePackageControl;
        }

        var control = new GenUpdatePackageControl(_fileLookupService, _sqlObjectService, ws);
        _genUpdatePackageTabPage = AddDocumentTab("Gen Update", control);
        _genUpdatePackageControl = control;
        return control;
    }

    private void OpenSelectBuilderTab()
    {
        var control = new SqlQueryControl(_sqlQueryService, _genInsert, _genUpdate, _sqlObjectService, _dataScript, _scriptFileService);
        control.ResultReady += table => _lastQueryResult = table;
        AddDocumentTab(NumberedTabTitle("Command"), control);
    }

    private RawSqlControl OpenFreeScriptTab()
    {
        // Luôn tạo tab SQL Query mới, không dùng lại tab cũ
        _rawSqlControl = CreateFreeScriptControl();
        _rawSqlTabPage = AddDocumentTab(NumberedTabTitle("SQL Query"), _rawSqlControl);
        _rawSqlTabPage.Disposed += (_, _) =>
        {
            _rawSqlTabPage = null;
            _rawSqlControl = null;
        };
        return _rawSqlControl;
    }
    private RawSqlControl CreateFreeScriptControl()
    {
        var control = new RawSqlControl(_rawSqlService, _sqlObjectService, _lookupService, _snippets);
        control.ResultReady += table => _lastQueryResult = table;
        control.OpenResultInNewTabRequested += (tables, title) =>
        {
            var view = new MultiResultView();
            view.SetTables(tables);
            AddDocumentTab(title, view);
        };
        control.OpenProcedureWithQueryRequested += (identifier, useSys, script) =>
            _ = OpenProcedureWithQueryAsync(identifier, useSys, script);
        control.DebugTargetChosen += (target, call) => _ = OpenDebugTargetAsync(target, call);
        return control;
    }

    private async Task OpenDebugTargetAsync(SqlObjectInfo target, string? callText = null)
    {
        string definition;
        try
        {
            definition = await _sqlObjectService.GetDefinitionAsync(target);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — SQL Object", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var key = (target.FromSysDatabase ? "sys:" : "app:") + target.QualifiedName;

        if (_debugTargetTabs.TryGetValue(key, out var existingPage) && _documentTabs.TabPages.Contains(existingPage))
        {
            _documentTabs.SelectedTab = existingPage;
            if (existingPage.Controls.OfType<RawSqlControl>().FirstOrDefault() is { } existingControl)
            {
                existingControl.SetDatabase(target.FromSysDatabase);
                _ = existingControl.LoadAndDebugAsync(definition, callText);
            }
            return;
        }

        var control = CreateFreeScriptControl();
        control.SetDatabase(target.FromSysDatabase);
        var page = AddDocumentTab(target.QualifiedName, control);
        _ = control.LoadAndDebugAsync(definition, callText); // nạp định nghĩa + bắt đầu debug từng bước (hỏi tham số nếu có)
        _debugTargetTabs[key] = page;
        page.Disposed += (_, _) => _debugTargetTabs.Remove(key);
    }

    private void OpenLookupTab()
    {
        if (_lookupTabPage is not null && _documentTabs.TabPages.Contains(_lookupTabPage))
        {
            _documentTabs.SelectedTab = _lookupTabPage;
            return;
        }

        var control = new LookupControl(_sqlObjectService, _tableDataService);
        control.OpenInTabRequested += obj => _ = OpenObjectDefinitionAsync(obj);
        _lookupTabPage = AddDocumentTab("Lookup", control);
        _lookupTabPage.Disposed += (_, _) => _lookupTabPage = null;
    }

    private void OpenTableTab()
    {
        var control = new TableEditControl(_tableDataService, _sqlObjectService, _dataScript, _scriptFileService, _genInsert, _genUpdate);
        var page = AddDocumentTab(NumberedTabTitle("Table"), control);
        // Lọc/tải bảng nào thì tab đổi tên thành bảng đó (dmkh, r00$000000...).
        control.TableLoaded += name => { if (!page.IsDisposed) { page.Text = name; _documentTabs.Invalidate(); } };
    }

    private void OpenFileReferenceTab()
    {
        if (_fileReferenceTabPage is not null && _documentTabs.TabPages.Contains(_fileReferenceTabPage))
        {
            _documentTabs.SelectedTab = _fileReferenceTabPage;
        }
        else
        {
            var control = new FileReferenceControl(_fileReferenceService);
            control.FileActivated += (path, _) => OpenFileInScriptTab(path);
            _fileReferenceTabPage = AddDocumentTab("File Reference", control);
            _fileReferenceTabPage.Disposed += (_, _) => _fileReferenceTabPage = null;
        }

        if (_connections.Current is { } ws && !string.IsNullOrWhiteSpace(ws.SourcePath) &&
            _fileReferenceTabPage.Controls.OfType<FileReferenceControl>().FirstOrDefault() is { } frc)
        {
            frc.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"));
        }
    }

    private void OpenChangeOwnerDialog()
    {
        using var form = new ChangeOwnerForm(_changeOwnerService, _sqlObjectService);
        form.ShowDialog(this);
    }

    /// <summary>"Note (New)" — Advance Note (Request List + Gen All + Generate Update), giao diện WebView2.</summary>
    private void OpenAdvanceNoteTab()
    {
        if (_advanceNoteTab is not null && _documentTabs.TabPages.Contains(_advanceNoteTab))
        {
            _documentTabs.SelectedTab = _advanceNoteTab;
            return;
        }
        var control = new AdvanceNoteControl(_advanceNoteService, _genAllService, () => _connections.Current, _settings);
        _advanceNoteTab = AddDocumentTab("Note (New)", control);
        _advanceNoteTab.Disposed += (_, _) => _advanceNoteTab = null;
    }

    private void OpenNoteTab(string noteName)
    {
        if (_noteTabs.TryGetValue(noteName, out var existing) && _documentTabs.TabPages.Contains(existing))
        {
            _documentTabs.SelectedTab = existing;
            return;
        }

        var control = new NoteControl(_noteService, WorkspaceName, noteName);
        var page = AddDocumentTab($"Note: {noteName}", control);
        _noteTabs[noteName] = page;
        page.Disposed += (_, _) => _noteTabs.Remove(noteName);
    }

    private void GenUpdateFromLastResult()
    {
        if (_lastQueryResult is not { } table || table.Rows.Count == 0)
        {
            MessageBox.Show(this, "Chưa có kết quả truy vấn nào để sinh UPDATE. Chạy SQL Query/Command/Table trước.", "Bcode — Gen Update");
            return;
        }

        var targetName = SimplePromptForm.Show(this, "Gen Update", "Tên bảng đích cho câu lệnh UPDATE:", table.TableName);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        var keyInput = SimplePromptForm.Show(this, "Gen Update",
            "Cột khoá (key) làm điều kiện WHERE, cách nhau bởi dấu phẩy (vd: stt_rec hoặc ma_ct,ky):",
            table.Columns.Count > 0 ? table.Columns[0].ColumnName : "");
        if (string.IsNullOrWhiteSpace(keyInput)) return;

        var keyColumns = keyInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sql = _genUpdate.GenerateUpdateStatements(table, targetName, keyColumns);
        Clipboard.SetText(sql);
        MessageBox.Show(this, "Đã sinh câu lệnh UPDATE và copy vào clipboard.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SelectWCommandTab() => SelectLeftSection("wcommand");

    private string WorkspaceName => _connections.Current?.Name ?? "";

    private async Task QuickSelectProjectByCodeAsync()
    {
        var code = SimplePromptForm.Show(this, "Mã dự án", "Nhập mã dự án (ID) để tự chọn Workspace đã lưu:", "");
        if (string.IsNullOrWhiteSpace(code)) return;
        code = code.Trim();

        var match = _settings.Workspaces.FirstOrDefault(w => w.ProjectId.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            var idx = _settings.Workspaces.IndexOf(match);
            if (idx >= 0) SelectWorkspace(idx);
            return;
        }

        var sync = MessageBox.Show(this,
            $"Do you want to synchronize project [{code}]?",
            "Bcode — Mã dự án", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (sync != DialogResult.Yes) return;

        // Thứ tự ưu tiên: Config.xml của FCode (offline, nhanh) -> tra ngầm ở Danh mục dự án
        // FSG (online, dữ liệu thật đã đăng ký — xem FsgProjectLookupService) -> cuối cùng mới
        // rơi về mẫu đoán theo quy ước đặt tên như trước (GenerateProjectTemplate).
        var generated = TryImportFromFCodeConfig(code)
            ?? await TryLookupFromFsgAsync(code)
            ?? GenerateProjectTemplate(code);
        using var editForm = new EditProjectForm(generated, _connections);
        if (editForm.ShowDialog(this) != DialogResult.OK) return;

        _settings.Workspaces.Add(editForm.Result);
        _settings.Save();
        PushWorkspacesToTopBar();
        SelectWorkspace(_settings.Workspaces.Count - 1);
    }

    /// <summary>
    /// Tra ngầm ở trang "Danh mục dự án" của FSG (nbdmda.aspx) khi mã dự án không có sẵn
    /// trong Config.xml của FCode. Trả về null nếu không tra được (offline, chưa lưu tài
    /// khoản FSG, không tìm thấy mã dự án, giao diện FSG đổi khác...) để
    /// QuickSelectProjectByCodeAsync rơi về GenerateProjectTemplate như trước — không bao giờ
    /// chặn luồng cũ lại.
    /// </summary>
    private async Task<Workspace?> TryLookupFromFsgAsync(string code)
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            var result = await new FsgProjectLookupService().LookupAsync(code);
            if (!result.Found || result.Workspace is null)
            {
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    MessageBox.Show(this,
                        $"Không tự tra được từ FSG:\n{result.Error}\n\n(Sẽ dùng mẫu đoán theo quy ước đặt tên như trước.)",
                        "Bcode — Tra cứu FSG", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return null;
            }

            if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                MessageBox.Show(this,
                    $"Đã tự điền từ FSG cho \"{code}\":\n{result.Summary}\n\nBee rà lại các trường trong popup Edit Project trước khi bấm OK nhé — vài trường (Database App, Host link...) là suy luận, có thể cần chỉnh tay.",
                    "Bcode — Tra cứu FSG", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            return result.Workspace;
        }
        finally
        {
            Cursor = Cursors.Default;
        }
    }

    private Workspace? TryImportFromFCodeConfig(string code)
    {
        if (string.IsNullOrWhiteSpace(_settings.FCodeConfigXmlPath) || !File.Exists(_settings.FCodeConfigXmlPath))
        {
            var path = SimplePromptForm.Show(this, "FCode Config.xml",
                "Nhập đường dẫn tới tệp Config.xml của FCode (vd: D:\\Tool\\FCode\\Config\\Config.xml):",
                string.IsNullOrWhiteSpace(_settings.FCodeConfigXmlPath) ? @"D:\Tool\FCode\Config\Config.xml" : _settings.FCodeConfigXmlPath);
            
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path.Trim())) return null;
            
            _settings.FCodeConfigXmlPath = path.Trim();
            _settings.Save();
        }

        FCodeConfigImportService.ImportedProject? found = null;
        try
        {
            found = FCodeConfigImportService.FindById(_settings.FCodeConfigXmlPath, code);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không đọc được tệp Config.xml:\n{ex.Message}", "Bcode — Import FCode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return null;
        }

        if (found is null) return null;

        return new Workspace
        {
            Name = found.Id,
            Server = found.Server,
            IntegratedSecurity = false,
            User = found.User,
            Password = "",
            SysDatabase = found.SysDatabase,
            AppDatabase = found.AppDatabase,
            ProjectId = found.Id,
            LoginWLink = found.LoginWLink,
            ProgramPath = found.ProgramPath,
            SourcePath = found.SourcePath,
            MobilePath = found.MobilePath,
            WorkingPath = found.WorkingPath,
            RegistryName = found.RegistryName,
        };
    }

    private Workspace GenerateProjectTemplate(string code)
    {
        var id = code.Trim().ToUpperInvariant();
        var server = _settings.DefaultProjectServer;
        var suffix = _settings.DefaultProjectVersionSuffix;
        var host = server.Contains('\\') ? server[..server.IndexOf('\\')] : server;

        return new Workspace
        {
            Name = id,
            Server = server,
            IntegratedSecurity = false,
            User = id,
            Password = "",
            SysDatabase = $"{id}_{suffix}_S",
            AppDatabase = $"{id}_{suffix}_A",
            ProjectId = id,
            LoginWLink = $"http://{host}/{id}/",
            ProgramPath = $"\\\\{host}\\CustomerPro\\FBI\\{id}\\{suffix}\\",
            SourcePath = $"\\\\{host}\\CustomerPro\\FBI\\{id}\\{suffix}\\",
            MobilePath = "",
            WorkingPath = $"\\\\{host}\\CustomerUpdate\\{id}\\update\\",
            RegistryName = "Software\\Fast",
        };
    }

    private void DebugDecryptConnectStr()
    {
        string? connectStr;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\FCoder");
            connectStr = key?.GetValue("ConnectStr") as string;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không đọc được registry: " + ex.Message, "Bcode — Debug ConnectStr",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(connectStr))
        {
            MessageBox.Show(this,
                "Không tìm thấy HKCU\\SOFTWARE\\FCoder\\ConnectStr.\nImport file .reg trước rồi thử lại.",
                "Bcode — Debug ConnectStr", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ConnectStr (đã mã hoá, {connectStr.Length} ký tự):");
        sb.AppendLine(connectStr.Length > 60 ? connectStr[..60] + "..." : connectStr);
        sb.AppendLine();
        TryDecryptCandidate(sb, "Crypto.RSADecrypt(cipherText)", () => global::Crypto.RSADecrypt(connectStr));
        TryDecryptCandidate(sb, "Crypto.Encode(s)", () => global::Crypto.Encode(connectStr));

        MessageBox.Show(this, sb.ToString(), "Bcode — Debug ConnectStr (chỉ để kiểm tra, chưa dùng thật)",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void TryDecryptCandidate(System.Text.StringBuilder sb, string label, Func<string?> call)
    {
        try
        {
            var result = call();
            sb.AppendLine($"[{label}]");
            sb.AppendLine(result is null ? "  => (null — hàm chạy được nhưng không trả kết quả)" : $"  => {result}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"[{label}]");
            sb.AppendLine($"  => LỖI: {ex.GetType().Name}: {ex.Message}");
        }
        sb.AppendLine();
    }

    private void OnWebGlobalShortcut(string key)
    {
        if (IsDisposed || !ReferenceEquals(Form.ActiveForm, this)) return;
        if (key == "ctrl+3") HandleGlobalShortcut(Keys.Control | Keys.D3);
        else if (key == "ctrl+w") HandleGlobalShortcut(Keys.Control | Keys.W);
        else if (key == "ctrl+tab") HandleGlobalShortcut(Keys.Control | Keys.Tab);
        else if (key == "ctrl+shift+tab") HandleGlobalShortcut(Keys.Control | Keys.Shift | Keys.Tab);
        else if (key.StartsWith("ctrl+shift+", StringComparison.Ordinal)
                 && Enum.TryParse<Keys>(key.Substring("ctrl+shift+".Length), out var k))
            HandleGlobalShortcut(Keys.Control | Keys.Shift | k);
    }

    private void OpenUiTemplate()
    {
        using var form = new UiTemplateForm(_settings, OrderedToolSpecs().Select(t => (t.key, t.label)).ToList(), _toolSpecs.Select(t => t.key).ToList());
        form.ShowDialog(this);
    }

    private void ChooseUiScale()
    {
        using var dlg = new UiScaleForm(_settings.UiScale);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _settings.UiScale = dlg.SelectedMode;
        try { _settings.Save(); } catch { /* không lưu được thì chỉ áp cho phiên này */ }
        Bcode.App.UI.UiScale.SetMode(_settings.UiScale, this);
    }

    // Tính lại hệ số co giãn khi cửa sổ hiện ra, đổi DPI, đổi cỡ hoặc chuyển sang màn hình khác.
    protected override void OnShown(EventArgs e) { base.OnShown(e); Bcode.App.UI.UiScale.Update(this); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Bcode.App.UI.UiScale.Update(this); }
    protected override void OnResizeEnd(EventArgs e) { base.OnResizeEnd(e); Bcode.App.UI.UiScale.Update(this); }
    protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); if (IsHandleCreated) Bcode.App.UI.UiScale.Update(this); }

    public bool HandleGlobalShortcut(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F5))
        {
            _ = QuickSelectProjectByCodeAsync();
            return true;
        }

        // Ctrl+Shift+N: mở thêm 1 cửa sổ Bcode MỚI chạy song song (mỗi cửa sổ làm 1 dự án — lúc mở có màn hình Projects để chọn).
        if (keyData == (Keys.Control | Keys.Shift | Keys.N))
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Không mở được Bcode mới:\n" + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return true;
        }

        if (keyData == (Keys.Control | Keys.Shift | Keys.F5))
        {
            DebugDecryptConnectStr();
            return true;
        }

        if (keyData == (Keys.Control | Keys.D3))
        {
            OpenSqlProfilerTab();
            return true;
        }

        // Các phím tắt đã ghi trên menu Actions (Ctrl+O = Choose Server, Ctrl+5 = Open Program Path) — trước đây chỉ là chữ trên
        // menu, chưa có phím nào được gắn thật nên bấm Ctrl+O không mở được Choose Server.
        if (keyData == (Keys.Control | Keys.O))
        {
            OpenConnectionSettings();
            return true;
        }

        // Ctrl+Tab / Ctrl+Shift+Tab: chuyển sang tab kế / tab trước (vòng tròn), bấm được ở bất kỳ đâu miễn là có tab.
        if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            var count = _documentTabs.TabPages.Count;
            if (count == 0) return false;
            var step = (keyData & Keys.Shift) == Keys.Shift ? -1 : 1;
            _documentTabs.SelectedIndex = (Math.Max(0, _documentTabs.SelectedIndex) + step + count) % count;
            _documentTabs.SelectedTab?.Focus();
            return true;
        }

        // Ctrl+W: đóng tab/file đang mở.
        if (keyData == (Keys.Control | Keys.W))
        {
            CloseDocumentTab(_documentTabs.SelectedIndex);
            return true;
        }

        if (keyData == (Keys.Control | Keys.D5))
        {
            OpenProgramPath();
            return true;
        }

        if ((keyData & Keys.Control) == Keys.Control && (keyData & Keys.Shift) == Keys.Shift)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Q: OpenFreeScriptTab(); return true;
                case Keys.L: OpenLookupTab(); return true;
                case Keys.T: OpenTableTab(); return true;
                case Keys.C: OpenSelectBuilderTab(); return true;
                case Keys.W: SelectWCommandTab(); return true;
                case Keys.F: OpenFileLookupTab(); return true;
                case Keys.R: OpenFileReferenceTab(); return true;
                case Keys.O: OpenChangeOwnerDialog(); return true;
                case Keys.U: GenUpdateFromLastResult(); return true;
                case Keys.G: OpenGenUpdatePackageTab(); return true;
                case Keys.E: OpenNoteTab(NoteService.DefaultNoteName); return true;
                case Keys.D4: OpenAdvanceNoteTab(); return true;
                case Keys.P: ShowProjectPicker(); return true;
            }
        }

        return false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleGlobalShortcut(keyData))
            return true;

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void OpenWCommandItem(WCommandItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Link) && string.IsNullOrWhiteSpace(item.SysId))
        {
            MessageBox.Show(this, $"Menu \"{item.Bar}\" không có Link/SysId gắn với source (có thể là mục nhóm/menu cha).", "wcommand");
            return;
        }

        if (_genUpdatePackageTabPage is not null && _documentTabs.SelectedTab == _genUpdatePackageTabPage
            && _genUpdatePackageControl is not null)
        {
            _genUpdatePackageControl.LoadForMenuItem(item);
            return;
        }

        var control = OpenFileLookupTab();
        if (control is null) return;

        var ws = _connections.Current!;
        control.ShowForMenuItem(ws.SourcePath, item.Link, item.SysId);
    }

    private void OpenFileFromLookup(string path)
    {
        if (NativeAppLauncher.TryOpenWithNativeApp(this, path)) return; 

        if (!string.IsNullOrWhiteSpace(_settings.ViewerExePath) && File.Exists(_settings.ViewerExePath))
        {
            try
            {
                var projectName = _connections.Current?.Name;
                var arguments = string.IsNullOrWhiteSpace(projectName)
                    ? $"\"{path}\""
                    : $"\"{path}\" \"{projectName}\"";
                Process.Start(new ProcessStartInfo(_settings.ViewerExePath, arguments) { UseShellExecute = true });
                return;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Không mở được BcodeViewer:\n{ex.Message}", "Bcode — File Lookup",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        OpenFileInScriptTab(path);
    }

    private void OpenFileInScriptTab(string path)
    {
        try
        {
            var content = _scriptFileService.ReadFile(path);
            var editor = new ScriptEditorControl();
            editor.LoadContent(path, content);
            AddDocumentTab(Path.GetFileName(path), editor);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Open File", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task<RawSqlControl?> OpenObjectDefinitionAsync(SqlObjectInfo obj)
    {
        try
        {
            var key = (obj.FromSysDatabase ? "sys:" : "app:") + obj.QualifiedName;
            var definition = await _sqlObjectService.GetDefinitionAsync(obj);
    
            // 1. Nếu Procedure/Bảng này đã có tab đang mở -> Chuyển focus đến tab đó
            if (_objectTabs.TryGetValue(key, out var existingPage) && _documentTabs.TabPages.Contains(existingPage))
            {
                _documentTabs.SelectedTab = existingPage;
                if (existingPage.Controls.OfType<RawSqlControl>().FirstOrDefault() is { } existingCtrl)
                {
                    existingCtrl.SetDatabase(obj.FromSysDatabase);
                    await existingCtrl.SetScriptTextAsync(definition);
                    return existingCtrl;
                }
            }

            // 2. Nếu là Procedure/Bảng khác -> Khởi tạo một tab SQL Query mới (RawSqlControl)
            var control = CreateFreeScriptControl();
            control.SetDatabase(obj.FromSysDatabase);
            control.SetScriptText(definition); // Tự động nạp code vào Monaco khi editor sẵn sàng

            // 3. Đặt tiêu đề tab theo tên QualifiedName (ví dụ: dbo.rs_Transfer$AfterSynchronize)
            var page = AddDocumentTab(obj.QualifiedName, control);
            _objectTabs[key] = page;
            page.Disposed += (_, _) => _objectTabs.Remove(key);

            return control;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — SQL Object", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }
    private async Task OpenProcedureWithQueryAsync(string identifier, bool useSysDatabase, string currentScript)
    {
        var obj = await ResolveProcedureAsync(identifier, useSysDatabase);
        if (obj is null)
        {
            MessageBox.Show(this, $"Không tìm thấy procedure/function '{identifier}' (đã tìm cả App Data và Sys Data).", "Bcode — SQL Query",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string definition;
        try
        {
            definition = await _sqlObjectService.GetDefinitionAsync(obj);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — SQL Object", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var combined = definition.TrimEnd() + "\r\nGO\r\n" + currentScript.Trim() + "\r\n";
        var key = (obj.FromSysDatabase ? "sys:" : "app:") + obj.QualifiedName;

        if (_procedureQueryTabs.TryGetValue(key, out var existingPage) && _documentTabs.TabPages.Contains(existingPage))
        {
            _documentTabs.SelectedTab = existingPage;
            if (existingPage.Controls.OfType<RawSqlControl>().FirstOrDefault() is { } existingControl)
            {
                existingControl.SetDatabase(obj.FromSysDatabase);
                existingControl.SetScriptText(combined);
            }
            return;
        }

        var control = CreateFreeScriptControl();
        control.SetDatabase(obj.FromSysDatabase);
        control.SetScriptText(combined);
        var page = AddDocumentTab(obj.QualifiedName, control);
        _procedureQueryTabs[key] = page;
        page.Disposed += (_, _) => _procedureQueryTabs.Remove(key);
    }

    private async Task<SqlObjectInfo?> ResolveProcedureAsync(string identifier, bool useSysDatabase)
    {
        var raw = identifier.Trim().Replace("[", "").Replace("]", "");
        var parts = raw.Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : null;
        var name = parts.Length == 2 ? parts[1] : parts[0];
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Procedure, Function (scalar/table), View, Trigger — mọi object có định nghĩa (trừ bảng). Tìm ở database đang chọn trước, không thấy
        // thì thử database còn lại (object của Fast thường nằm ở Sys Data hoặc App Data tuỳ loại).
        foreach (var sys in new[] { useSysDatabase, !useSysDatabase })
        {
            var matches = (await _sqlObjectService.ListObjectsAsync(sys, name))
                .Where(o => o.Kind != SqlObjectKind.Table && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 0) continue;

            if (schema is not null)
            {
                var exact = matches.FirstOrDefault(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact;
            }
            return matches.FirstOrDefault();
        }
        return null;
    }

    private void OpenLibrary()
    {
        using var form = new LibrarySnippetForm(_snippets);
        if (form.ShowDialog(this) == DialogResult.OK && form.SelectedContentToInsert is { } content)
        {
            if (GetActiveEditor() is { } editor)
                editor.Content += content;
            else
                Clipboard.SetText(content);
        }
    }

    private ScriptEditorControl? GetActiveEditor() =>
        _documentTabs.SelectedTab?.Controls.OfType<ScriptEditorControl>().FirstOrDefault();

    private void AddScript()
    {
        using var ofd = new OpenFileDialog { Filter = "Script files (*.f;*.xml;*.sql)|*.f;*.xml;*.sql|All files (*.*)|*.*", Multiselect = true };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;

        int addedCount = 0;
        foreach (var path in ofd.FileNames)
        {
            _scriptFileService.AddToCart(path);
            addedCount++;
        }

        // Thông báo cho người dùng biết đã cộng dồn thành công vào giỏ script
        PushStatus($"Đã thêm {addedCount} file vào Script Cart. Tổng số lượng: {_scriptFileService.Cart.Count} file.");
    }
    private void ViewScriptCart()
    {
        if (_scriptFileService.Cart.Count == 0)
        {
            MessageBox.Show(this, "Script Cart đang trống. Hãy dùng 'Add Script' ở các bảng dữ liệu hoặc chọn file từ máy.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Sử dụng Monaco Editor (RawSqlControl) để render nội dung lớn cực mượt, không bị lag
        var control = CreateFreeScriptControl();
        
        var concatenatedContent = _scriptFileService.ViewCartConcatenated();
        control.SetScriptText(concatenatedContent);

        AddDocumentTab($"Script Cart ({_scriptFileService.Cart.Count} files)", control);
    }

    private void SaveActiveScript()
    {
        if (GetActiveEditor() is not { } editor) return;
        if (editor.CurrentPath is null)
        {
            MessageBox.Show(this, "Tab này không gắn với file trên đĩa (ví dụ kết quả Decrypt/Library). Dùng Copy Script để lấy nội dung.", "Bcode");
            return;
        }
        _scriptFileService.WriteFile(editor.CurrentPath, editor.Content);
        editor.MarkSaved();
    }

    private void CopyActiveScript() => GetActiveEditor()?.CopyToClipboard();

    private async Task BackupDatabaseAsync()
    {
        if (_connections.Current is not { } ws)
        {
            MessageBox.Show(this, "Chưa chọn Workspace.", "Bcode");
            return;
        }

        using var sfd = new SaveFileDialog { Filter = "Backup file (*.bak)|*.bak", FileName = $"{ws.AppDatabase}.bak" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            await using var conn = _connections.CreateConnection();
            await conn.OpenAsync();
            await using var cmd = new Microsoft.Data.SqlClient.SqlCommand(
                $"BACKUP DATABASE [{ws.AppDatabase.Replace("]", "]]")}] TO DISK = N'{sfd.FileName.Replace("'", "''")}' WITH INIT, COMPRESSION;",
                conn)
            { CommandTimeout = 0 };
            await cmd.ExecuteNonQueryAsync();
            MessageBox.Show(this, "Backup hoàn tất.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Backup Database", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenQuickLaunchLogin()
    {
        var ws = _connections.Current;
        if (ws is null)
        {
            MessageBox.Show(this, "Chưa chọn Workspace nào.", "Bcode — Bung link chương trình", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(ws.LoginWLink))
        {
            MessageBox.Show(this,
                $"Workspace \"{ws.Name}\" chưa khai \"Login WLink\".\nVào File > Choose Server / Workspaces (Edit Project) để khai báo trước khi bung link.",
                "Bcode — Bung link chương trình", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        new QuickLaunchLoginForm(ws, _settings).Show();
    }

    /// <summary>Actions &gt; Create Menu: chạy NGAY <c>exec ns_createCommand N'mã dự án'</c> trên FSG_A cho project đang chọn (không hỏi lại).</summary>
    private async Task CreateMenuAsync()
    {
        var ws = _connections.Current;
        var code = ws is null ? "" : (string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.Name : ws.ProjectId).Trim();
        if (code.Length == 0)
        {
            MessageBox.Show(this, "Chưa chọn project nào.", "Create Menu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Chạy luôn, không hỏi. Thành công thì KHÔNG thông báo gì (không lộ tên lệnh/script); chỉ khi lỗi mới hiện 1 câu chung.
        PushStatus("Đang tạo menu...");
        var (ok, message) = await new FsgProjectLookupService().CreateMenuAsync(code);
        if (_connections.Current is { } cur)
            PushStatus($"Workspace: {cur.Name}  —  Server: {cur.Server}  |  Dev: HàoTN|PhongNT");
        if (!ok) MessageBox.Show(this, message, "Create Menu", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    /// <summary>Lệnh từ BcodeViewer (F5): chọn đúng workspace của file, tra menu wcommand trỏ tới controller của file
    /// (sysid = tên file không đuôi, hoặc link "&lt;tên&gt;.aspx"), rồi bung FSG FBO đăng nhập và đi thẳng tới URL của menu.
    /// Không tìm thấy menu (vd file Include/.ent) vẫn bung FSG FBO, chỉ không điều hướng tới đâu.</summary>
    private async Task RunMenuForFileAsync(string path, string projectName)
    {
        try
        {
            var idx = _settings.Workspaces.FindIndex(w => !string.IsNullOrWhiteSpace(projectName)
                && string.Equals(w.Name, projectName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
                idx = _settings.Workspaces.FindIndex(w => !string.IsNullOrWhiteSpace(w.SourcePath)
                    && path.StartsWith(w.SourcePath.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0 && !ReferenceEquals(_settings.Workspaces[idx], _connections.Current))
                SelectWorkspace(idx);

            var ws = _connections.Current;
            if (ws is null || string.IsNullOrWhiteSpace(ws.LoginWLink))
            {
                OpenQuickLaunchLogin(); // tự báo thiếu Workspace / Login WLink
                return;
            }

            string? url = null;
            var note = "";
            var controller = Path.GetFileNameWithoutExtension(path);
            try
            {
                var items = await _wcommandService.FindByControllerAsync(controller);
                var item = items.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Link));
                if (item is null) note = $"Không có menu nào gắn với \"{controller}\" — chỉ bung FSG FBO.";
                else
                {
                    url = BuildMenuUrl(ws.LoginWLink, item);
                    note = $"Menu: {item.Bar} ({item.WMenuId})";
                }
            }
            catch (Exception ex)
            {
                note = "Không tra được menu (wcommand): " + ex.Message;
            }

            if (_quickLaunchForm is { IsDisposed: false } existing && ReferenceEquals(existing.Workspace, ws))
            {
                existing.WindowState = FormWindowState.Maximized;
                existing.Activate();
                await existing.RunMenuAsync(url, note);
                return;
            }

            _quickLaunchForm = new QuickLaunchLoginForm(ws, _settings, url, note);
            _quickLaunchForm.Show();
            _quickLaunchForm.Activate();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Chạy menu từ BcodeViewer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>URL trang menu: gốc = thư mục "Main" của site (LoginWLink có thể là .../Main/Login.aspx hoặc chỉ .../Tên/),
    /// nối link của wcommand (đường dẫn tương đối trong Main), kèm parameter nếu có.</summary>
    private static string? BuildMenuUrl(string loginLink, WCommandItem item)
    {
        var link = (item.Link ?? "").Trim().Replace('\\', '/').TrimStart('/');
        if (link.Length == 0) return null;
        if (link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return link;
        if (!Uri.TryCreate(loginLink.Trim(), UriKind.Absolute, out var u)) return null;

        var p = u.AbsolutePath;
        var i = p.IndexOf("/Main/", StringComparison.OrdinalIgnoreCase);
        var root = i >= 0 ? p[..(i + 6)]
            : (p.EndsWith('/') ? p : p[..(p.LastIndexOf('/') + 1)]) + "Main/";

        var url = u.GetLeftPart(UriPartial.Authority) + root + link;
        var prm = (item.Parameter ?? "").Trim().TrimStart('?', '&');
        if (prm.Length > 0) url += (url.Contains('?') ? "&" : "?") + prm;
        return url;
    }
    private void OpenSqlProfilerTab()
    {
        if (_sqlProfilerTab != null && _documentTabs.TabPages.Contains(_sqlProfilerTab))
        {
            _documentTabs.SelectedTab = _sqlProfilerTab;
            return;
        }

        var control = new SqlProfilerControl(_settings, _connections, () => _connections.Current);
        // "Copy SQL Command" ở Profiler → mở tab SQL Query mới với câu lệnh đã dán sẵn.
        control.OpenInSqlQueryRequested += sql =>
        {
            var sqlTab = OpenFreeScriptTab();
            sqlTab.SetScriptText(sql);
        };
        _sqlProfilerTab = AddDocumentTab("SQL Profiler", control);
        _sqlProfilerTab.Disposed += (_, _) => _sqlProfilerTab = null;
    }

    private void OpenCompareTextTab()
    {
        if (_compareTextTab != null && _documentTabs.TabPages.Contains(_compareTextTab))
        {
            _documentTabs.SelectedTab = _compareTextTab;
            return;
        }

        var compareCtrl = new CompareTextControl();
        compareCtrl.FloatWindowRequested += () =>
        {
            var floatForm = new Bcode.App.UI.DpiForm
            {
                Text = "Compare Text",
                Width = 1050,
                Height = 720,
                StartPosition = FormStartPosition.CenterScreen,
                BackColor = Color.FromArgb(20, 20, 20)
            };
            var innerCtrl = new CompareTextControl { Dock = DockStyle.Fill };
            floatForm.Controls.Add(innerCtrl);
            ThemeManager.Apply(floatForm);
            floatForm.Show(this);
        };

        _compareTextTab = AddDocumentTab("Compare Text", compareCtrl);
        _compareTextTab.Disposed += (_, _) => _compareTextTab = null;
    }


    private void ClearStructureApp()
    {
        if (_connections.Current is not { } ws || string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            MessageBox.Show(this, "Workspace chưa khai báo Source Path.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var targetPath = Path.Combine(ws.SourcePath, "App_Data", "Controllers", "Structure", "App");

        if (!Directory.Exists(targetPath))
        {
            MessageBox.Show(this, $"Thư mục không tồn tại:\n{targetPath}", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Hỏi xác nhận trước khi xóa hàng loạt
        var confirm = MessageBox.Show(this, 
            $"Bạn có chắc chắn muốn xóa toàn bộ file trong thư mục này không?\n{targetPath}", 
            "Xác nhận xóa", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            
        if (confirm != DialogResult.Yes) return;

        try
        {
            var files = Directory.GetFiles(targetPath);
            int count = 0;
            foreach (var file in files)
            {
                File.Delete(file);
                count++;
            }
            MessageBox.Show(this, $"Đã xóa thành công {count} file.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Có lỗi xảy ra khi xóa file:\n{ex.Message}", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }



    private void RefreshWebConfig()
    {
        // 1. Kiểm tra cấu hình Workspace
        if (_connections.Current is not { } ws || string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            MessageBox.Show(this, "Workspace chưa khai báo Source Path.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 2. Trỏ tới file web.config nằm ở thư mục gốc của SourcePath
        var webConfigPath = Path.Combine(ws.SourcePath, "web.config");

        if (!File.Exists(webConfigPath))
        {
            MessageBox.Show(this, $"Không tìm thấy file web.config tại:\n{webConfigPath}", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            // 3. Mở file và ghi thêm đúng 1 khoảng trắng (space) vào cuối cùng
            // Việc thay đổi nội dung file sẽ báo hiệu cho IIS tự động Restart lại chương trình
            File.AppendAllText(webConfigPath, " ");
            
            MessageBox.Show(this, "Đã refresh web.config thành công! (IIS đang khởi động lại)", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Có lỗi xảy ra khi tác động vào web.config:\n{ex.Message}", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }


    private void OpenProgramPath()
    {
        if (_connections.Current is { } ws && !string.IsNullOrWhiteSpace(ws.ProgramPath))
            Process.Start(new ProcessStartInfo("explorer.exe", ws.ProgramPath) { UseShellExecute = true });
    }

    private ContextMenuStrip BuildActionsMenu()
    {
        var menu = new ContextMenuStrip();
        
        // ---------------------------------------------------------
        // NHÓM 1: KẾT NỐI & SERVER
        // ---------------------------------------------------------
        menu.Items.Add(new ToolStripMenuItem("Choose Server", null, (_, _) => OpenConnectionSettings()) { ShortcutKeyDisplayString = "Ctrl+O" });
        menu.Items.Add(new ToolStripMenuItem("Projects...", null, (_, _) => ShowProjectPicker()) { ShortcutKeyDisplayString = "Ctrl+Shift+P" });
        menu.Items.Add(new ToolStripMenuItem("New Bcode window", null, (_, _) => HandleGlobalShortcut(Keys.Control | Keys.Shift | Keys.N)) { ShortcutKeyDisplayString = "Ctrl+Shift+N" });
        menu.Items.Add(new ToolStripMenuItem("Switch Database", null, (_, _) => { /* Chức năng chưa rõ */ }) { ShortcutKeyDisplayString = "Ctrl+1" });
        menu.Items.Add(new ToolStripMenuItem("SQL Profiler", null, (_, _) => OpenSqlProfilerTab()) { ShortcutKeyDisplayString = "Ctrl+3" });
        menu.Items.Add(new ToolStripMenuItem("SQL SMS", null, (_, _) => { /* Mở SSMS */ }) { ShortcutKeyDisplayString = "Ctrl+4" });
        menu.Items.Add(new ToolStripMenuItem("Open Program Path", null, (_, _) => OpenProgramPath()) { ShortcutKeyDisplayString = "Ctrl+5" });
        
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Refresh", null, (_, _) => { /* Lệnh refresh */ }));
        menu.Items.Add(new ToolStripSeparator());

        // ---------------------------------------------------------
        // NHÓM 2: SCRIPT (Tạo Submenu có mũi tên chĩa ngang)
        // ---------------------------------------------------------
        var scriptMenu = new ToolStripMenuItem("Script");
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("Library (Script đã lưu)...", null, (_, _) => OpenLibrary()));
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("Add Script", null, (_, _) => AddScript()) { ShortcutKeyDisplayString = "Ctrl+Alt+Shift+A" });
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("View Script Cart", null, (_, _) => ViewScriptCart()) { ShortcutKeyDisplayString = "Ctrl+Alt+Shift+V" });
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("Clear Script", null, (_, _) => _scriptFileService.ClearCart()));
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("Save Script", null, (_, _) => SaveActiveScript()));
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("Copy Script", null, (_, _) => CopyActiveScript()));
        menu.Items.Add(scriptMenu); // Gắn menu con vào menu chính

        // Các nhóm menu con khác (Để sẵn khung chờ bạn gắn lệnh)
        menu.Items.Add(new ToolStripMenuItem("Database"));
        menu.Items.Add(new ToolStripMenuItem("Document"));
        menu.Items.Add(new ToolStripMenuItem("Create Source"));

        // ---------------------------------------------------------
        // NHÓM 3: TOOLS (Tự động nạp toàn bộ ToolSpecs vào Submenu)
        // ---------------------------------------------------------
        var toolsMenu = new ToolStripMenuItem("Tools");
        foreach (var (key, label, shortcut, action) in _toolSpecs)
        {
            var item = new ToolStripMenuItem(label, null, (s, e) => action(this, EventArgs.Empty));
            if (shortcut != null) item.ShortcutKeyDisplayString = $"Ctrl+Shift+{shortcut}";
            toolsMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(toolsMenu);

        menu.Items.Add(new ToolStripSeparator());

        // ---------------------------------------------------------
        // NHÓM 4: QUẢN LÝ TAB VÀ CỬA SỔ
        // ---------------------------------------------------------
        menu.Items.Add(new ToolStripMenuItem("Close Tab", null, (_, _) => {
            if (_documentTabs.SelectedIndex >= 0) CloseDocumentTab(_documentTabs.SelectedIndex);
        }) { ShortcutKeyDisplayString = "Ctrl+W" });
        
        menu.Items.Add(new ToolStripMenuItem("Close All But This", null, (_, _) => {
            var currentIdx = _documentTabs.SelectedIndex;
            for (int i = _documentTabs.TabPages.Count - 1; i >= 0; i--)
                if (i != currentIdx) CloseDocumentTab(i);
        }));
        
        menu.Items.Add(new ToolStripMenuItem("Close Tabs to the Right", null, (_, _) => {
            if (_documentTabs.SelectedTab is not { } cur) return;
            var from = _documentTabs.SelectedIndex;
            CloseTabsWhere(p => _documentTabs.TabPages.IndexOf(p) > from);
        }));

        menu.Items.Add(new ToolStripMenuItem("Close All Tab", null, (_, _) => {
            for (int i = _documentTabs.TabPages.Count - 1; i >= 0; i--)
                CloseDocumentTab(i);
        }));

        menu.Items.Add(new ToolStripSeparator());

        // ---------------------------------------------------------
        // NHÓM 5: CÁC TIỆN ÍCH KHÁC
        // ---------------------------------------------------------
        menu.Items.Add(new ToolStripMenuItem("Get Key Data/Value", null, (_, _) => { }));
        menu.Items.Add(new ToolStripMenuItem("Clear Structure App", null, (_, _) => ClearStructureApp()));
        menu.Items.Add(new ToolStripMenuItem("Refresh Web.config", null, (_, _) => RefreshWebConfig()));
        menu.Items.Add(new ToolStripMenuItem("Create Menu", null, async (_, _) => await CreateMenuAsync()));

        // Áp dụng màu nền Dark/Light theo hệ thống theme của Bcode
        Bcode.App.UI.ThemeManager.Apply(menu);

        return menu;
    }
}