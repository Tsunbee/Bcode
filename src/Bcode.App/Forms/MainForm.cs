using System.Data;
using System.Diagnostics;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.Services.Rpt;
using Bcode.App.UI;

using static Bcode.App.UI.ThemeManager;
namespace Bcode.App.Forms;

public partial class MainForm : Bcode.App.UI.ThemedForm
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
    private SplitContainer? _mainSplit; // Panel1 = cây menu WCommand, Panel2 = vùng tab tài liệu (cây nằm trái hoặc phải tuỳ Template giao diện)
    private bool _layoutApplied;
    private string _layoutSide = "left";
    private int _layoutTreeWidth;
    /// <summary>Đã đổi "thanh tab ở dưới/trên" nhưng đang còn tab mở — đổi hướng TabControl làm tạo lại cửa sổ của mọi tab (WebView2 sẽ trắng/tải lại),
    /// nên chỉ áp khi không còn tab nào (hoặc ở lần mở sau).</summary>
    public bool TabsPositionPending { get; private set; }
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
        // Giữ Shift lúc mở Bcode = chế độ an toàn cho phiên này: bỏ qua CSS riêng và bản HTML ghi đè (xem UiOverrides).
        Bcode.App.UI.UiOverrides.SessionSafe = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
        Bcode.App.UI.UiOverrides.Notice += msg => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => PushStatus(msg)); };
        _settings = AppSettings.Load();
        Bcode.App.UI.UiThemes.ApplyPalettes(); // theme/màu người dùng chọn — trước khi dựng control
        // Giữ con trỏ chuột luôn hiện khi gõ (tắt "Hide pointer while typing" của Windows trong lúc chạy; trả lại khi thoát).
        Bcode.App.UI.MousePointer.Apply(Bcode.App.UI.UiTemplate.Current.KeepMousePointer);
        void OnPointerSetting() => Bcode.App.UI.MousePointer.Apply(Bcode.App.UI.UiTemplate.Current.KeepMousePointer);
        Bcode.App.UI.UiTemplate.Changed += OnPointerSetting;
        Disposed += (_, _) => { Bcode.App.UI.UiTemplate.Changed -= OnPointerSetting; Bcode.App.UI.MousePointer.Restore(); };
        Bcode.App.UI.UiScale.SetMode(_settings.UiScale, this);
        FileLookupService.CacheMode = Enum.TryParse<FileLookupCacheMode>(_settings.FileLookupCacheMode, true, out var cacheMode)
            ? cacheMode : FileLookupCacheMode.On;
        _wcommandService = new WCommandService(_connections);
        _sqlObjectService = new SqlObjectBrowserService(_connections);
        _usageService = new UsageSearchService(_connections, _fileReferenceService);
        _genAllService = new GenAllService(_fileLookupService, _sqlObjectService, _wcommandService);
        _sqlQueryService = new SqlQueryService(_connections, _periods);
        _snippets = new SnippetLibraryService(_settings.LibraryPath);
        _rawSqlService = new RawSqlService(_connections);
        _tableDataService = new TableDataService(_connections, _periods);
        _genAllService.TableData = _tableDataService;   // Note (New) → "Table liên quan": đọc dữ liệu để sinh script
        _genAllService.DataScript = _dataScript;
        _lookupService = new LookupService(_connections);
        _changeOwnerService = new ChangeOwnerService(_connections);

        // Ctrl+3 bấm khi con trỏ đang nằm trong 1 trang WebView2 (editor SQL, thanh công cụ...) — xem WebViewEnvironment.GlobalShortcut.
        // Chỉ nhận khi cửa sổ chính đang là cửa sổ hoạt động (không mở tab sau lưng 1 hộp thoại đang bật).
        // Giữ Ctrl ~0,7 giây (khi focus ở control WinForms) → hộp chọn tab; phía trang web thì script trong WebViewEnvironment báo "@ctrl-hold".
        _ctrlHoldTimer.Tick += (_, _) =>
        {
            _ctrlHoldTimer.Stop();
            if (ReferenceEquals(Form.ActiveForm, this) && Control.ModifierKeys == Keys.Control) ShowTabSwitcher();
        };
        var ctrlHoldFilter = new CtrlHoldFilter(this);
        Application.AddMessageFilter(ctrlHoldFilter);
        Disposed += (_, _) => { Application.RemoveMessageFilter(ctrlHoldFilter); _ctrlHoldTimer.Dispose(); };
        WebViewEnvironment.GlobalShortcut += OnWebGlobalShortcut;
        Disposed += (_, _) => WebViewEnvironment.GlobalShortcut -= OnWebGlobalShortcut;

        // F5 trong BcodeViewer: lưu file xong gửi sang đây để bung FSG FBO chạy menu của file đó.
        _viewerControl.RunMenuRequested += (path, project) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(async () => await RunMenuForFileAsync(path, project))); }
            catch (InvalidOperationException) { /* cửa sổ đang đóng */ }
        };
        _viewerControl.SqlScriptRequested += (script, title) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => OpenViewerScriptTab(script, title))); }
            catch (InvalidOperationException) { /* cửa sổ đang đóng */ }
        };
        _viewerControl.Start();
        Disposed += (_, _) => _viewerControl.Dispose();

        Text = "Bcode";
        // Tiêu đề cửa sổ kèm tên dự án (workspace) đang mở → phân biệt được khi mở nhiều Bcode (thanh tác vụ, Alt+Tab).
        _connections.WorkspaceChanged += UpdateWindowTitle;
        UpdateWindowTitle();
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
        _statusBarWeb.Height = Bcode.App.UI.UiTemplate.Dens(26); // theo Mật độ của Template giao diện

        _settingsMenu = () => new WebMenu()
            .Add("Choose Server / Workspaces...", OpenConnectionSettings)
            .Add("Tỉ lệ giao diện...", ChooseUiScale)
            .Add("Khôi phục các tab khi mở lại Bcode", ToggleRestoreSession, shortcut: Bcode.App.UI.ShortcutRegistry.Display("app.restoreSession"), @checked: _settings.RestoreSession)
            .Add($"Tự lưu phiên mỗi {_settings.SessionSaveSeconds} giây...", () => BeginInvoke(new Action(ChooseSessionSaveInterval)))
            .Add("Claude/Gemini nhúng vào tab SQL Query (tắt = tab riêng)", () => AppSettings.AiEmbedded = !AppSettings.AiEmbedded, @checked: AppSettings.AiEmbedded)
            .Add("Giao diện (Template)...", () => BeginInvoke(new Action(OpenUiTemplate)))
            .Add("Hướng dẫn gợi ý code SQL (gõ gì ra gì)...", () => BeginInvoke(new Action(OpenSqlHintsHelpTab)), shortcut: Bcode.App.UI.ShortcutRegistry.Display("app.sqlHints"))
            .Add(LicenseService.IsUnlocked ? "Key bản quyền ✓ (đã kích hoạt)..." : "Key bản quyền (Decrypt SQL, Create RPT & XML, Excel → FRX)...", () => BeginInvoke(new Action(() => { using var f = new LicenseKeyForm(); f.ShowDialog(this); })))
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
        _toolSpecs.Add(("check_mail", "Check Mail", null, (_, _) => OpenCheckMailTab()));
        _toolSpecs.Add(("excel_to_frx", "Excel → FRX", null, (_, _) => OpenExcelToFrxTab()));
        _toolSpecs.Add(("compare_text", "Compare Text", null, (_, _) => OpenCompareTextTab()));
        _toolSpecs.Add(("string_beauty", "String Beauty", null, (_, _) => OpenStringBeautyTab()));
        _toolSpecs.Add(("query_history", "Lịch sử SQL", "Y", (_, _) => OpenQueryHistoryTab()));
        _toolSpecs.Add(("compare_objects", "So sánh object", "J", (_, _) => OpenCompareObjectsTab()));
        _toolSpecs.Add(("library", "Library...", null, (_, _) => OpenLibrary()));
        _toolSpecs.Add(("decrypt_sql_object", "Decrypt SQL Object", null, (_, _) => OpenDecryptSqlTab()));
        _toolSpecs.Add(("setup_einvoice", "Setup eInvoice (FE)", null, (_, _) => OpenSetupEInvoiceTab()));
        _toolSpecs.Add(("create_rpt_xlsx", "Create *.rpt, *.xlsx", null, (_, _) => OpenCreateRptTab()));
        _toolSpecs.Add(("compare_structure", "Compare Structure", null, (_, _) => new CompareStructureForm(_settings).ShowDialog(this)));
        _toolSpecs.Add(("view_rpt_fec", "View Rpt in FEC", null, (_, _) => new ViewRptInFecForm().ShowDialog(this)));
        _toolSpecs.Add(("fsg_crawler", "FSG Yêu cầu", null, (_, _) => new FsgRequirementCrawlerForm().Show()));
        _toolSpecs.Add(("quick_launch", "FSG FBO", null, (_, _) => OpenQuickLaunchLogin()));
        _toolSpecs.Add(("sql_profiler", "SQL Profiler", null, (_, _) => OpenSqlProfilerTab()));
        _toolSpecs.Add(("api_config", "Khai báo API", null, (_, _) => new ApiDeclarationForm().ShowDialog(this)));
        _toolSpecs.Add(("api_schema_builder", "Tạo cấu trúc API", null, (_, _) => new ApiSchemaBuilderForm(_sqlObjectService, _tableDataService).ShowDialog(this)));
        _toolSpecs.Add(("catalog_clone", "Clone danh mục", null, (_, _) => new CatalogCloneForm(_sqlObjectService, _tableDataService, _connections).ShowDialog(this)));
        _toolSpecs.Add(("claude_web", "Claude", null, (_, _) => OpenAi(Bcode.Shared.AiSite.Claude, null)));
        _toolSpecs.Add(("gemini_web", "Gemini", null, (_, _) => OpenAi(Bcode.Shared.AiSite.Gemini, null)));
        // Đăng ký các nút công cụ vào danh sách phím tắt cấu hình được (phím mặc định = Ctrl+Shift+<chữ> như trước; SQL Profiler = Ctrl+3).
        Bcode.App.UI.ShortcutRegistry.SetTools(_toolSpecs.Select(t => (t.key, t.label,
            t.shortcut is not null ? $"Ctrl+Shift+{t.shortcut}" : t.key == "sql_profiler" ? "Ctrl+3" : (string?)null)));
        RebuildToolsBar();
        void OnTemplateChanged() { RebuildToolsBar(); PushTopBarLayout(); _statusBarWeb.Height = Bcode.App.UI.UiTemplate.Dens(26); ApplyWindowLayout(first: false); }
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
        // Khu vực của Template giao diện (font/màu riêng từng vùng): thanh trên + thanh công cụ, cây menu bên trái, thanh tab.
        Bcode.App.UI.ThemeManager.SetArea(_headerContainer, "top");
        Bcode.App.UI.ThemeManager.SetArea(_leftContentHost, "tree");

        var sqlObjectTree = new SqlObjectTreeControl(_sqlObjectService) { Dock = DockStyle.Fill };
        sqlObjectTree.ObjectActivated += async obj => await OpenObjectDefinitionAsync(obj);
        sqlObjectTree.UsagesRequested += obj => OpenUsagesTab(obj);
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

        Bcode.App.UI.ThemeManager.SetArea(_documentTabs, "tabs");
        // Đổi / mở tab: tab cũ bị ẩn nên control đang giữ focus bàn phím (WebView2 của editor cũ) biến mất → phím tắt "bấm không ăn" cho tới khi bấm chuột vào đâu đó
        // (vd thanh Execute). Mỗi lần chọn tab, trả focus về nội dung của tab đó (editor SQL / trang web / control đầu tiên).
        _documentTabs.SelectedIndexChanged += (_, _) => BeginInvoke(new Action(() => RestoreContentFocus(activate: false)));
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
        _mainSplit = split;
        split.Panel1.Controls.Add(leftContainer);
        split.Panel2.Controls.Add(_documentTabs);
        split.Panel2.Controls.Add(_quickAccessOverlay);
        UpdateQuickAccessOverlayBounds();

        Controls.Add(split);
        Controls.Add(_statusBarWeb);
        Controls.Add(headerContainer);
        Load += (_, _) => ApplyWindowLayout(first: true);
        
        if (_settings.Workspaces.Count > 0)
        {
            var last = _settings.Workspaces.FindIndex(w => w.Name == _settings.LastWorkspace);
            SelectWorkspace(last >= 0 ? last : 0);
        }
        // Màn hình Projects khi mới mở Bcode: lọc/chọn nhanh project đã khai báo (đóng đi thì giữ project dùng gần nhất).
        Shown += (_, _) => BeginInvoke(new Action(() => { ShowProjectPicker(); TryRestoreSession(); }));
        InitSession();
        // Dựng sẵn 1 tab SQL Query ở nền sau khi cửa sổ đã lên hình (xem TakeSqlControl).
        _spareTimer.Tick += (_, _) => PrepareSpareSql();
        Controls.Add(_spareHost);
        Shown += (_, _) => QueueSpareSql(2500);
        Disposed += (_, _) => _spareTimer.Dispose();

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
                            BeginInvoke(new Action(RestoreContentFocus));
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
                            BeginInvoke(new Action(RestoreContentFocus));
                            break;
                        case "select-ws":
                        {
                            // Ô chọn ở topbar liệt kê database của project đang vào — index là vị trí trong _topDbEntries.
                            var i = root.GetProperty("index").GetInt32();
                            if (i >= 0 && i < _topDbEntries.Count) ApplyActiveDatabase(_topDbEntries[i].Sys, _topDbEntries[i].Extra, setOverride: true);
                            BeginInvoke(new Action(RestoreContentFocus));
                            break;
                        }
                        case "select-db":
                            ApplyActiveDatabase(root.GetProperty("which").GetString() == "sys");
                            BeginInvoke(new Action(RestoreContentFocus));
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
                _statusBarWeb.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var root = doc.RootElement;
                    switch (root.GetProperty("action").GetString())
                    {
                        case "project-web": BeginInvoke(new Action(OpenProjectWeb)); break;
                        case "project-menu":
                            // Toạ độ (px CSS) của nút trên thanh trạng thái; lấy số ra TRƯỚC khi JsonDocument bị dispose.
                            var mx = root.TryGetProperty("x", out var xp) ? xp.GetDouble() : 0;
                            var my = root.TryGetProperty("y", out var yp) ? yp.GetDouble() : 0;
                            BeginInvoke(new Action(() => ShowProjectMenu(mx, my)));
                            break;
                    }
                };
                _statusBarWeb.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    PushThemeToShell();
                    PushProjectInfo();
                    if (_connections.Current is { } cur) PushStatus($"Workspace: {cur.Name}  —  Server: {cur.Server}  |  Dev: HàoTN|PhongNT | Tester: ThinhBM| KhanhNN");
                };

                _topBarWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("topbar.html"));
                _iconRailWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("iconrail.html"));
                _statusBarWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("statusbar.html"));
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

    private void UpdateWindowTitle()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(new Action(UpdateWindowTitle)); } catch (InvalidOperationException) { } return; }
        var name = _connections.Current?.Name;
        Text = string.IsNullOrWhiteSpace(name) ? "Bcode" : $"Bcode - {name}";
    }

    private void SelectWorkspace(int index)
    {
        if (index < 0 || index >= _settings.Workspaces.Count) return;
        var ws = _settings.Workspaces[index];
        _connections.SetWorkspace(ws);
        RememberWorkspace(ws);
        _topDbSys = false; // đổi project → quay về App Data
        ws.ActiveAppDatabaseOverride = null; // và về App Data gốc (bỏ database phụ đã chọn từ DB Access)
        PushWorkspacesToTopBar();
        PushDbNamesToTopBar(ws);
        PushStatus($"Workspace: {ws.Name}  —  Server: {ws.Server}  |  Dev: HàoTN|PhongNT | Tester: ThinhBM| KhanhNN");

        _ = _wcommandTree.ReloadAsync();
        _sqlObjectTree.ResetForWorkspace(); // đổi project → danh sách SQL Object nạp lại (từ cache của project mới)

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
        var appArg = System.Text.Json.JsonSerializer.Serialize(ws.EffectiveAppDatabase);
        _ = _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setDbNames && window.setDbNames({sysArg}, {appArg})");
    }
    // Ô chọn ở topbar KHÔNG còn là danh sách project: chỉ liệt kê 2 database (App, Sys) của project đang vào, để chuyển nhanh
    // giữa chúng khi chạy SQL Query. Muốn sang project khác thì dùng Choose Server / Ctrl+O.
    private bool _topDbSys;
    private readonly List<(bool Sys, string? Extra)> _topDbEntries = new(); // theo thứ tự các mục trong ô chọn: Sys / App / database phụ trong DB Access


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
        _topDbEntries.Clear();
        var names = new List<string>();
        if (_connections.Current is { } ws)
        {
            if (!string.IsNullOrWhiteSpace(ws.AppDatabase)) { names.Add($"{ws.Name} — {ws.AppDatabase}  (App)"); _topDbEntries.Add((false, null)); }
            if (!string.IsNullOrWhiteSpace(ws.SysDatabase)) { names.Add($"{ws.Name} — {ws.SysDatabase}  (Sys)"); _topDbEntries.Add((true, null)); }
            // Các database khai trong "DB Access" (Proxy {mã_dự_án}_eInv, _C...) ngoài App/Sys — chọn để làm việc trên database đó.
            foreach (var extra in ws.AccessDatabases())
            {
                if (extra.Equals(ws.AppDatabase, StringComparison.OrdinalIgnoreCase) || extra.Equals(ws.SysDatabase, StringComparison.OrdinalIgnoreCase)) continue;
                names.Add($"{ws.Name} — {extra}  (DB)"); _topDbEntries.Add((false, extra));
            }
        }
        var arg = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Serialize(names.ToArray()));
        var over = _connections.Current?.ActiveAppDatabaseOverride;
        var sel = _topDbEntries.FindIndex(en => en.Sys == _topDbSys && (_topDbSys || string.Equals(en.Extra, string.IsNullOrWhiteSpace(over) ? null : over, StringComparison.OrdinalIgnoreCase)));
        if (sel < 0) sel = 0;
        _ = _topBarWeb.CoreWebView2.ExecuteScriptAsync(
            $"window.setWorkspaces && window.setWorkspaces({arg}); window.setSelectedWs && window.setSelectedWs({sel}); window.setDbView && window.setDbView('{(_topDbSys ? "sys" : "app")}')");
        PushTopBarLayout();
    }

    /// <summary>Chuyển database đang làm việc của project hiện tại sang App hoặc Sys: cập nhật topbar và đổi database của tab SQL
    /// Query đang mở (nếu tab đang mở là SQL Query).</summary>
    /// <param name="extra">Database phụ (trong DB Access) chọn làm "App"; null = App gốc. Chỉ có tác dụng khi <paramref name="setOverride"/> và không phải Sys.</param>
    private void ApplyActiveDatabase(bool useSys, string? extra = null, bool setOverride = false)
    {
        _topDbSys = useSys;
        if (setOverride && !useSys && _connections.Current is { } cur && !string.Equals(cur.ActiveAppDatabaseOverride, extra, StringComparison.OrdinalIgnoreCase))
        {
            cur.ActiveAppDatabaseOverride = extra;
            PushDbNamesToTopBar(cur);
            _sqlObjectTree.ResetForWorkspace(); // đổi database làm việc → danh sách SQL Object nạp lại theo database mới
        }
        PushWorkspacesToTopBar();
        if (_documentTabs.SelectedTab?.Controls.OfType<RawSqlControl>().FirstOrDefault() is { } sql)
            sql.SetDatabase(useSys);
        if (_connections.Current is { } ws)
            PushStatus($"Database: {(useSys ? ws.SysDatabase : ws.EffectiveAppDatabase)} ({(useSys ? "Sys" : "App")})  —  Project: {ws.Name}");
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
        PushProjectInfo(); // đổi workspace/database thì thông tin project ở góc phải cũng đổi theo
    }

    // ---- Góc phải thanh trạng thái: link web đăng nhập, menu project, tên máy ----

    private void PushProjectInfo()
    {
        if (_statusBarWeb.CoreWebView2 is null) return;
        var info = new { link = _connections.Current?.LoginWLink?.Trim() ?? "", machine = Environment.MachineName };
        var arg = System.Text.Json.JsonSerializer.Serialize(info);
        _ = _statusBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setProject && window.setProject({arg})");
    }

    private void OpenProjectWeb()
    {
        var link = _connections.Current?.LoginWLink?.Trim();
        if (string.IsNullOrWhiteSpace(link)) { MessageBox.Show(this, "Project này chưa khai báo Login WLink (Edit Project).", "Bcode"); return; }
        try { Process.Start(new ProcessStartInfo(link) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được trang web:\n" + ex.Message, "Bcode"); }
    }

    private void OpenProjectFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path.Trim()}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được thư mục:\n" + ex.Message, "Bcode"); }
    }

    private void ShowProjectMenu(double cssX, double cssY)
    {
        if (_connections.Current is not { } ws) { MessageBox.Show(this, "Chưa chọn workspace (WS).", "Bcode"); return; }
        var scale = _statusBarWeb.DeviceDpi / 96.0 * Bcode.App.UI.UiScale.Factor;
        var screen = _statusBarWeb.PointToScreen(new Point((int)Math.Round(cssX * scale), (int)Math.Round(cssY * scale)));
        new WebMenu()
            .Add($"Login WLink: {ws.LoginWLink}", OpenProjectWeb, enabled: !string.IsNullOrWhiteSpace(ws.LoginWLink))
            .Add($"Program: {ws.ProgramPath}", () => OpenProjectFolder(ws.ProgramPath), enabled: !string.IsNullOrWhiteSpace(ws.ProgramPath))
            .Add($"Source: {ws.SourcePath}", () => OpenProjectFolder(ws.SourcePath), enabled: !string.IsNullOrWhiteSpace(ws.SourcePath))
            .Add($"Working: {ws.WorkingPath}", () => OpenProjectFolder(ws.WorkingPath), enabled: !string.IsNullOrWhiteSpace(ws.WorkingPath))
            .Add($"Mobile: {ws.MobilePath}", () => OpenProjectFolder(ws.MobilePath), enabled: !string.IsNullOrWhiteSpace(ws.MobilePath))
            .AddSeparator()
            .Add("Copy Project Info", () => CopyProjectInfo(ws))
            .Show(_statusBarWeb, screen);
    }

    /// <summary>Copy thông tin project (ID, SQL, Web, Program, Source, Mobile, Update, Version) vào clipboard để dán cho đồng nghiệp.</summary>
    private void CopyProjectInfo(Workspace ws)
    {
        var id = string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.Name : ws.ProjectId;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ID: {id}");
        sb.AppendLine($"SQL: {ws.Server} | {id} | {ws.User} | {ws.SysDatabase} | {ws.AppDatabase}");
        sb.AppendLine($"Web: {ws.LoginWLink}");
        sb.AppendLine($"Program: {ws.ProgramPath}");
        sb.AppendLine($"Source: {ws.SourcePath}");
        sb.AppendLine($"Mobile: {ws.MobilePath}");
        sb.AppendLine($"Update: {ws.WorkingPath}");
        sb.Append("Version:");
        try { Clipboard.SetText(sb.ToString()); }
        catch (Exception ex) { MessageBox.Show(this, "Không copy được vào clipboard:\n" + ex.Message, "Bcode"); }
    }

    private void SelectLeftSection(string key)
    {
        foreach (var (sectionKey, control) in _leftSections) control.Visible = sectionKey == key;
        if (key == "wcommand") _ = _wcommandTree.ReloadAsync();
        if (key == "sql_object") _ = _sqlObjectTree.EnsureLoadedAsync(); // vào mục SQL Object: nạp sẵn (từ cache), chỉ nạp thêm khi có object mới/đổi
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
        _toolsBar.Padding = new Padding(4, Bcode.App.UI.UiTemplate.Dens(2), 4, Bcode.App.UI.UiTemplate.Dens(2));

        // Đã sắp xếp lại bởi người dùng thì bỏ vạch ngăn nhóm mặc định (các nhóm cũ không còn nằm cạnh nhau nữa).
        var customOrder = _settings.ToolOrder.Count > 0;
        foreach (var (key, label, shortcut, action) in OrderedToolSpecs())
        {
            if (_settings.HiddenToolKeys.Contains(key)) continue;
            var compact = Bcode.App.UI.UiTemplate.DensityFactor < 1;
            var button = new ToolStripButton(label, null, action)
            {
                Margin = compact ? new Padding(1, 0, 1, 0) : new Padding(1, 1, 1, 2),
                // Mật độ "Thoáng": thêm đệm quanh chữ (Vừa = như cũ, Gọn = bỏ bớt lề trên/dưới ở Margin).
                Padding = new Padding(Math.Max(0, Bcode.App.UI.UiTemplate.Dens(10) - 10), Math.Max(0, Bcode.App.UI.UiTemplate.Dens(6) - 6),
                    Math.Max(0, Bcode.App.UI.UiTemplate.Dens(10) - 10), Math.Max(0, Bcode.App.UI.UiTemplate.Dens(6) - 6)),
            };
            var toolCombo = Bcode.App.UI.ShortcutRegistry.Display("tool:" + key);
            if (toolCombo.Length > 0) button.ToolTipText = $"{label} ({toolCombo})";
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

    // ---- Command Palette (Ctrl+P) ----------------------------------------------------------------------

    private readonly List<string> _paletteRecent = new();
    private CommandPaletteForm? _palette;

    /// <summary>Mở hộp tìm nhanh: tab đang mở, tool, lệnh phím tắt, menu WCommand (đã nạp), object SQL (đọc từ cache trên máy — không chạm database).</summary>
    private void OpenCommandPalette()
    {
        if (_palette is { IsDisposed: false }) { _palette.Activate(); return; }
        var items = new List<object>();
        var objects = new Dictionary<string, SqlObjectInfo>();
        var menus = new Dictionary<string, WCommandItem>();

        for (var i = 0; i < _documentTabs.TabPages.Count; i++)
            items.Add(new { kind = "tab", id = "tab:" + i, title = _documentTabs.TabPages[i].Text.Trim(), sub = "", key = i < 9 ? Bcode.App.UI.ShortcutRegistry.Display("tab.goto" + (i + 1)) : "" });

        foreach (var d in Bcode.App.UI.ShortcutRegistry.All.Where(d => d.Scope == Bcode.App.UI.ShortcutScope.App))
        {
            if (d.Id is "app.palette" || d.Id.StartsWith("tab.goto", StringComparison.Ordinal)) continue;
            var isTool = d.Id.StartsWith("tool:", StringComparison.Ordinal);
            items.Add(new { kind = isTool ? "tool" : "cmd", id = d.Id, title = d.Text, sub = isTool ? "" : d.Group, key = Bcode.App.UI.ShortcutRegistry.Display(d.Id) });
        }

        foreach (var (item, path) in _wcommandTree.FlatLeaves())
        {
            menus[item.WMenuId] = item;
            items.Add(new { kind = "menu", id = "menu:" + item.WMenuId, title = item.Bar, sub = path, code = item.WMenuId, key = "" });
        }

        var snippets = new Dictionary<string, string>();
        try { _snippets.Load(); } catch { /* dùng bản đang có */ }
        var projectNow = _connections.Current?.Name ?? "";
        for (var i = 0; i < _snippets.Snippets.Count; i++)
        {
            var sn = _snippets.Snippets[i];
            if (!sn.AppliesTo(projectNow)) continue;
            var sid = "snip:" + i;
            snippets[sid] = sn.Content;
            items.Add(new { kind = "snip", id = sid, title = sn.Name, sub = sn.Category + (string.IsNullOrWhiteSpace(sn.Project) ? "" : " · " + sn.Project), key = "" });
        }

        foreach (var sys in new[] { false, true })
        {
            ObjectCacheOrNull(sys)?.ForEach(o =>
            {
                var id = "obj:" + (sys ? "sys:" : "app:") + o.QualifiedName;
                objects[id] = o;
                items.Add(new { kind = "obj", id, title = o.QualifiedName, sub = (sys ? "Sys · " : "App · ") + o.Kind, key = "" });
            });
        }

        var form = new CommandPaletteForm(System.Text.Json.JsonSerializer.Serialize(items), System.Text.Json.JsonSerializer.Serialize(_paletteRecent));
        _palette = form;
        form.FormClosed += (_, _) => { if (ReferenceEquals(_palette, form)) _palette = null; };
        form.Chosen += (kind, id, secondary) => BeginInvoke(new Action(() => RunPaletteChoice(kind, id, secondary, objects, menus, snippets)));
        form.Show(this);
    }

    private List<SqlObjectInfo>? ObjectCacheOrNull(bool sys)
    {
        try { return _sqlObjectService.LoadCache(sys)?.Items; } catch { return null; }
    }

    private void RunPaletteChoice(string kind, string id, bool secondary, Dictionary<string, SqlObjectInfo> objects, Dictionary<string, WCommandItem> menus, Dictionary<string, string> snippets)
    {
        _paletteRecent.Remove(id);
        _paletteRecent.Insert(0, id);
        if (_paletteRecent.Count > 12) _paletteRecent.RemoveAt(12);
        switch (kind)
        {
            case "tab":
                if (int.TryParse(id["tab:".Length..], out var n) && n >= 0 && n < _documentTabs.TabPages.Count)
                {
                    _documentTabs.SelectedIndex = n;
                    _documentTabs.SelectedTab?.Focus();
                }
                break;
            case "tool":
            case "cmd":
                RunAppShortcut(id);
                break;
            case "menu":
                if (menus.TryGetValue(id["menu:".Length..], out var mi))
                {
                    // Hiện cây menu bên trái, mở các nhánh cha và chọn đúng menu — rồi mở file của menu như bấm đúp.
                    if (_mainSplit is { Panel1Collapsed: true }) ToggleMenuTree();
                    SelectWCommandTab();
                    _ = _wcommandTree.RevealAsync(mi);
                    OpenWCommandItem(mi);
                }
                break;
            case "snip":
                if (!snippets.TryGetValue(id, out var snippetText)) break;
                // Chèn vào tab SQL đang chọn; không phải tab SQL thì mở tab SQL mới rồi chèn.
                var sqlHere = _documentTabs.SelectedTab?.Controls.OfType<RawSqlControl>().FirstOrDefault();
                if (sqlHere is null) { OpenFreeScriptTab(); sqlHere = _documentTabs.SelectedTab?.Controls.OfType<RawSqlControl>().FirstOrDefault(); }
                if (sqlHere is not null) _ = sqlHere.InsertSnippetAsync(snippetText);
                break;
            case "obj":
                if (!objects.TryGetValue(id, out var obj)) break;
                if (secondary) OpenUsagesTab(obj); else _ = OpenObjectDefinitionAsync(obj);
                break;
        }
    }

    private readonly UsageSearchService _usageService;

    /// <summary>Tab "Ai đang dùng …" cho 1 object SQL: quét database (App + Sys) và file source. Mỗi object 1 tab; mở lại thì chuyển tới và quét lại.</summary>
    private void OpenUsagesTab(SqlObjectInfo obj)
    {
        var key = "usages:" + (obj.FromSysDatabase ? "sys:" : "app:") + obj.QualifiedName;
        if (_objectTabs.TryGetValue(key, out var existing) && _documentTabs.TabPages.Contains(existing))
        {
            _documentTabs.SelectedTab = existing;
            return;
        }
        var control = new UsagesControl(obj, _usageService, _connections.Current?.SourcePath, () => _wcommandTree.FlatLeaves());
        control.RevealMenuRequested += id =>
        {
            var hit = _wcommandTree.FlatLeaves().Select(x => x.Item).FirstOrDefault(i => i.WMenuId == id);
            if (hit is null) return;
            if (_mainSplit is { Panel1Collapsed: true }) ToggleMenuTree();
            SelectWCommandTab();
            _ = _wcommandTree.RevealAsync(hit);
        };
        control.OpenObjectRequested += o => _ = OpenObjectDefinitionAsync(o);
        control.OpenFileRequested += path => OpenFileFromLookup(path);
        var page = AddDocumentTab("Dùng: " + obj.Name, control);
        _objectTabs[key] = page;
        page.Disposed += (_, _) => _objectTabs.Remove(key);
    }

    /// <summary>Phím tắt "Ai đang dùng object này?" cho tab SQL đang mở từ cây SQL Object (hoặc từ palette).</summary>
    private void OpenUsagesForCurrentTab()
    {
        var page = _documentTabs.SelectedTab;
        var key = _objectTabs.FirstOrDefault(kv => ReferenceEquals(kv.Value, page) && !kv.Key.StartsWith("usages:", StringComparison.Ordinal)).Key;
        if (key is null) { MessageBox.Show(this, "Tab đang chọn không phải tab mở từ cây SQL Object — mở một procedure / view / bảng từ cây SQL Object rồi thử lại, hoặc dùng Ctrl+P → Shift+Enter.", "Bcode — Ai đang dùng", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        var sys = key.StartsWith("sys:", StringComparison.Ordinal);
        var name = key[4..];
        var obj = ObjectCacheOrNull(sys)?.FirstOrDefault(o => o.QualifiedName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (obj is null) { var dot = name.IndexOf('.'); obj = new SqlObjectInfo { Schema = dot > 0 ? name[..dot] : "dbo", Name = dot > 0 ? name[(dot + 1)..] : name, FromSysDatabase = sys, Kind = SqlObjectKind.StoredProcedure }; }
        OpenUsagesTab(obj);
    }

    private void ToggleRestoreSession()
    {
        _settings.RestoreSession = !_settings.RestoreSession;
        try { _settings.Save(); } catch { /* chỉ áp cho phiên này */ }
    }

    /// <summary>Hỏi số giây giữa hai lần tự lưu phiên (Settings → "Tự lưu phiên mỗi … giây").</summary>
    private void ChooseSessionSaveInterval()
    {
        var text = SimplePromptForm.Show(this, "Tự lưu phiên", "Số giây giữa hai lần tự lưu các tab + nội dung SQL (1 – 600):", _settings.SessionSaveSeconds.ToString());
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!int.TryParse(text.Trim(), out var seconds) || seconds < 1 || seconds > 600)
        { MessageBox.Show(this, "Nhập một số nguyên từ 1 đến 600.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        _settings.SessionSaveSeconds = seconds;
        try { _settings.Save(); } catch { /* chỉ áp cho phiên này */ }
        ApplySessionInterval();
    }

    private TabPage? _sqlHintsHelpTab;

    /// <summary>Tab "Hướng dẫn gợi ý code SQL": bảng "gõ gì → ra gì" (mẫu gõ tắt, gợi ý hàm, cột, options, cảnh báo). Nội dung sinh từ SqlHintCatalog nên luôn khớp với editor.</summary>
    private void OpenSqlHintsHelpTab()
    {
        if (_sqlHintsHelpTab is not null && _documentTabs.TabPages.Contains(_sqlHintsHelpTab)) { _documentTabs.SelectedTab = _sqlHintsHelpTab; return; }
        _sqlHintsHelpTab = AddDocumentTab("Gợi ý SQL — hướng dẫn", new SqlHintsHelpControl());
        _sqlHintsHelpTab.Disposed += (_, _) => _sqlHintsHelpTab = null;
    }

    private TabPage? _compareObjectsTab;

    /// <summary>Tab "So sánh object": thân procedure / view / function / trigger giữa hai database + sinh script ALTER (chỉ sinh, không chạy). Một tab duy nhất.</summary>
    private void OpenCompareObjectsTab()
    {
        if (_compareObjectsTab is not null && _documentTabs.TabPages.Contains(_compareObjectsTab))
        {
            _documentTabs.SelectedTab = _compareObjectsTab;
            return;
        }
        var control = new CompareObjectsControl(_settings, () => _connections.Current);
        control.OpenSqlRequested += (script, title) =>
        {
            var sql = TakeSqlControl();
            sql.SetScriptText(script);
            AddDocumentTab(title, sql);
            sql.FocusEditor();
        };
        _compareObjectsTab = AddDocumentTab("So sánh object", control);
        _compareObjectsTab.Disposed += (_, _) => _compareObjectsTab = null;
    }

    private TabPage? _queryHistoryTab;

    /// <summary>Tab "Lịch sử SQL" (tìm toàn văn, ghim, tham số lần trước). Một tab duy nhất; mở lại thì chuyển tới.</summary>
    private void OpenQueryHistoryTab()
    {
        if (_queryHistoryTab is not null && _documentTabs.TabPages.Contains(_queryHistoryTab))
        {
            _documentTabs.SelectedTab = _queryHistoryTab;
            return;
        }
        var control = new QueryHistoryControl(() => _connections.Current?.Name ?? "");
        control.OpenSqlRequested += (script, sys, title) =>
        {
            var sql = TakeSqlControl();
            sql.SetDatabase(sys);
            sql.SetScriptText(script);
            AddDocumentTab(title, sql);
            sql.FocusEditor();
        };
        _queryHistoryTab = AddDocumentTab("Lịch sử SQL", control);
        _queryHistoryTab.Disposed += (_, _) => _queryHistoryTab = null;
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
        menu.Items.Add(new ToolStripMenuItem(pinned ? "Unpin Tab" : "Pin Tab", null, (_, _) => _documentTabs.SetPinned(page, !pinned))
            { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.pin") });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Close Tab", null, (_, _) => CloseDocumentTab(_documentTabs.TabPages.IndexOf(page)))
            { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.close") });
        menu.Items.Add(new ToolStripMenuItem("Close Other Tabs", null, (_, _) => CloseTabsWhere(p => p != page))
            { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.closeOthers") });
        menu.Items.Add(new ToolStripMenuItem("Close Tabs to the Right", null, (_, _) =>
        {
            var from = _documentTabs.TabPages.IndexOf(page);
            CloseTabsWhere(p => _documentTabs.TabPages.IndexOf(p) > from);
        }) { Enabled = _documentTabs.TabPages.IndexOf(page) < _documentTabs.TabPages.Count - 1, ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.closeRight") });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Close All Tabs", null, (_, _) => CloseTabsWhere(_ => true))
            { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.closeAll") });
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
        if (TabsPositionPending && _documentTabs.TabPages.Count == 0) ApplyTabsAlignment(); // hết tab → áp vị trí thanh tab đang chờ
        UpdateQuickAccessOverlayBounds();
    }

    /// <summary>Bố cục cửa sổ theo Template giao diện: cây menu bên trái/phải + độ rộng + ẩn lúc mở, thanh tab trên/dưới, thanh công cụ 1 dòng/xuống dòng.
    /// Chỉ đụng tới thứ thật sự đổi (để không đặt lại độ rộng cây người dùng vừa kéo mỗi lần Áp dụng một mục khác).</summary>
    private void ApplyWindowLayout(bool first)
    {
        var t = Bcode.App.UI.UiTemplate.Current;
        if (_mainSplit is { } split)
        {
            var side = t.TreeSide == "right" ? "right" : "left";
            if (first || side != _layoutSide)
            {
                // Đảo bên bằng RightToLeft của SplitContainer (Panel1 hiện ở bên phải) — không phải chuyển control con sang panel khác,
                // nên các tab chứa WebView2 không bị tạo lại. Panel con đặt No để chữ/bố cục bên trong không bị lật theo.
                split.RightToLeft = side == "right" ? RightToLeft.Yes : RightToLeft.No;
                split.Panel1.RightToLeft = RightToLeft.No;
                split.Panel2.RightToLeft = RightToLeft.No;
                _layoutSide = side;
            }
            if (first || t.TreeWidth != _layoutTreeWidth)
            {
                try { if (!split.Panel1Collapsed) split.SplitterDistance = Math.Min(t.TreeWidth, Math.Max(split.Panel1MinSize, split.Width - 200)); }
                catch { /* cửa sổ còn quá nhỏ để đặt độ rộng này — bỏ qua, giữ như cũ */ }
                _layoutTreeWidth = t.TreeWidth;
            }
            if (first && t.TreeStartHidden) split.Panel1Collapsed = true;
        }

        // Thanh công cụ: xuống dòng khi hẹp (mặc định) hoặc 1 dòng + mũi tên "»" cho phần thừa.
        if (t.ToolbarWrap)
        {
            _toolsBar.LayoutStyle = ToolStripLayoutStyle.Flow;
            _toolsBar.CanOverflow = false;
            if (_toolsBar.LayoutSettings is FlowLayoutSettings flow) flow.WrapContents = true;
        }
        else
        {
            _toolsBar.LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
            _toolsBar.CanOverflow = true;
        }
        UpdateHeaderHeight();

        ApplyTabsAlignment();
        _layoutApplied = true;
    }

    private void ApplyTabsAlignment()
    {
        var want = Bcode.App.UI.UiTemplate.Current.TabsAtBottom ? TabAlignment.Bottom : TabAlignment.Top;
        if (_documentTabs.Alignment == want) { TabsPositionPending = false; return; }
        if (_documentTabs.TabPages.Count > 0) { TabsPositionPending = true; return; }
        _documentTabs.Alignment = want;
        TabsPositionPending = false;
        _documentTabs.Invalidate();
        UpdateQuickAccessOverlayBounds();
    }

    private void UpdateQuickAccessOverlayBounds()
    {
        var atBottom = _documentTabs.Alignment == TabAlignment.Bottom;
        var headerHeight = atBottom ? _documentTabs.Height - _documentTabs.DisplayRectangle.Bottom : _documentTabs.DisplayRectangle.Top;
        if (headerHeight <= 0) headerHeight = Bcode.App.UI.DpiScale.Px(this, 26);

        var lastTabRight = _documentTabs.TabPages.Count > 0
            ? _documentTabs.GetTabRect(_documentTabs.TabPages.Count - 1).Right
            : 0;

        _quickAccessOverlay.Bounds = new Rectangle(lastTabRight, atBottom ? _documentTabs.Height - headerHeight : 0, Math.Max(0, _documentTabs.Width - lastTabRight), headerHeight);
        _quickAccessOverlay.BringToFront();
    }

    private WebMenu BuildQuickAccessMenu()
    {
        var menu = new WebMenu().AddCaption("Mở nhanh");
        foreach (var (qKey, label, _, action) in OrderedToolSpecs())
        {
            var qCombo = Bcode.App.UI.ShortcutRegistry.Display("tool:" + qKey);
            if (qCombo.Length == 0) continue;
            var handler = action;
            menu.Add(label, () => handler(this, EventArgs.Empty), shortcut: qCombo);
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
        control.ExcelToFrxRequested += path => OpenExcelToFrxTab(path);
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

    // ---- Tab SQL Query dựng sẵn ----------------------------------------------------------------
    // Mở 1 tab SQL Query phải khởi tạo 2 WebView2 (thanh Execute + editor Monaco), mất cỡ nửa giây tới 1 giây. Nên luôn giữ sẵn MỘT tab đã nạp xong ở khung ẩn
    // (ngoài màn hình): bấm mở tab thì chỉ việc gắn nó vào thanh tab (đổi cha của control, WebView2 giữ nguyên) rồi dựng tiếp 1 tab dự phòng ở nền.
    private readonly Panel _spareHost = new() { Left = -6000, Top = 0, Width = 1100, Height = 700, TabStop = false };
    private RawSqlControl? _spareSql;
    private readonly System.Windows.Forms.Timer _spareTimer = new();

    private void QueueSpareSql(int delayMs)
    {
        if (IsDisposed) return;
        _spareTimer.Stop();
        _spareTimer.Interval = Math.Max(100, delayMs);
        _spareTimer.Start();
    }

    private void PrepareSpareSql()
    {
        _spareTimer.Stop();
        if (_spareSql is not null || IsDisposed || !IsHandleCreated || _connections.Current is null) return;
        try
        {
            var c = CreateFreeScriptControl(prewarm: true);
            _spareHost.Controls.Add(c);
            Bcode.App.UI.ThemeManager.Apply(c);
            _spareSql = c;
        }
        catch { _spareSql = null; /* không dựng sẵn được — lần mở tab sau tạo bình thường */ }
    }

    /// <summary>Lấy tab SQL Query dựng sẵn nếu có (rồi dựng cái dự phòng kế tiếp), không thì tạo mới.</summary>
    private RawSqlControl TakeSqlControl()
    {
        var spare = _spareSql;
        _spareSql = null;
        RawSqlControl control;
        // Chỉ lấy tab dựng sẵn khi nó đã SẴN SÀNG: lấy ngay lúc WebView2 còn đang khởi tạo rồi chuyển sang tab khác làm khởi tạo bị huỷ (E_ABORT) —
        // dễ gặp khi vừa mở Bcode và khôi phục tab. Chưa sẵn sàng thì để nó lại làm dự phòng và tạo tab mới.
        if (spare is not null && !spare.IsDisposed && spare.IsReady) { control = spare; control.BeginUse(); }
        else
        {
            if (spare is not null && !spare.IsDisposed) _spareSql = spare;
            control = CreateFreeScriptControl();
        }
        QueueSpareSql(1200);
        return control;
    }

    /// <summary>"Debug trong Bcode" từ hộp Chạy SQL của BcodeViewer: tab SQL Query mới (App Data) chứa script, rồi đưa
    /// cửa sổ Bcode lên trước để chạy/sửa tiếp — từng bước bằng Debug Step của tab nếu cần.</summary>
    private void OpenViewerScriptTab(string script, string title)
    {
        var control = TakeSqlControl();
        control.SetDatabase(false);
        control.SetScriptText(script);
        AddDocumentTab(NumberedTabTitle(string.IsNullOrWhiteSpace(title) ? "SQL Query" : title), control);
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
        Activate();
        control.FocusEditor();
    }

    private RawSqlControl OpenFreeScriptTab()
    {
        // Luôn tạo tab SQL Query mới, không dùng lại tab cũ
        _rawSqlControl = TakeSqlControl();
        _rawSqlTabPage = AddDocumentTab(NumberedTabTitle("SQL Query"), _rawSqlControl);
        _rawSqlTabPage.Disposed += (_, _) =>
        {
            _rawSqlTabPage = null;
            _rawSqlControl = null;
        };
        return _rawSqlControl;
    }
    // ---- Claude / Gemini web (dùng chung code với BcodeViewer: Shared/AiWebHelper.cs) ----
    private readonly Dictionary<Bcode.Shared.AiSite, TabPage> _aiTabs = new();

    /// <summary>Mở (hoặc chuyển tới tab đã mở) trang Claude/Gemini; mỗi trang chỉ 1 tab để giữ nguyên cuộc trò chuyện đang dở.</summary>
    private AiWebPanel OpenAiTab(Bcode.Shared.AiSite site)
    {
        if (_aiTabs.TryGetValue(site, out var page) && _documentTabs.TabPages.Contains(page)
            && page.Controls.OfType<AiWebPanel>().FirstOrDefault() is { } existing)
        {
            _documentTabs.SelectedTab = page;
            return existing;
        }
        var panel = new AiWebPanel(site);
        _aiTabs[site] = AddDocumentTab(site == Bcode.Shared.AiSite.Claude ? "Claude" : "Gemini", panel);
        return panel;
    }

    /// <summary>Mở Claude/Gemini theo Settings: nhúng thành khung bên phải của tab SQL Query (<paramref name="host"/>, hoặc tab SQL Query đang chọn), còn không có tab SQL Query
    /// hay đang để "tab riêng" thì mở tab riêng như trước.</summary>
    private AiWebPanel OpenAi(Bcode.Shared.AiSite site, RawSqlControl? host)
    {
        if (AppSettings.AiEmbedded)
        {
            host ??= _documentTabs.SelectedTab?.Controls.OfType<RawSqlControl>().FirstOrDefault();
            if (host is not null) return host.ShowAi(site);
        }
        return OpenAiTab(site);
    }

    /// <summary>Từ tab SQL Query: mở Claude/Gemini rồi đưa script vào ô chat (chưa gửi — gõ câu hỏi rồi Enter).</summary>
    private async void SendScriptToAi(string engine, string text, RawSqlControl? host = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var site = engine.Equals("gemini", StringComparison.OrdinalIgnoreCase) ? Bcode.Shared.AiSite.Gemini : Bcode.Shared.AiSite.Claude;
        // Text dài: dán cả khối chữ vào ô chat rất lag (nhất là Gemini) → nén thành file .txt rồi đính kèm như file thật. Text ngắn vẫn dán thẳng.
        const int attachAbove = 3000;
        try
        {
            var panel = OpenAi(site, host);
            if (text.Length <= attachAbove)
            {
                if (site == Bcode.Shared.AiSite.Claude)
                {
                    // claude.ai bỏ qua xuống dòng khi chèn bằng execCommand (mọi dòng gộp một) → nhập bằng phím thật (Shift+Enter giữa các dòng).
                    var typeError = await panel.TypeTextAsync("Script SQL đang mở trong Bcode:\n", text);
                    if (typeError is not null) MessageBox.Show(this, typeError, "Bcode — Claude", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else await panel.InsertTextAsync("Script SQL đang mở trong Bcode:\n", text, text + "\n", usePaste: false);
                return;
            }
            var dir = Path.Combine(Path.GetTempPath(), "Bcode", "ai");
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "script_*.txt")) // dọn file cũ hơn 1 ngày
                try { if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-1)) File.Delete(old); } catch { }
            var path = Path.Combine(dir, $"script_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(path, text, new System.Text.UTF8Encoding(true));
            PushStatus($"Đang đính kèm {Path.GetFileName(path)} ({text.Length:N0} ký tự) vào {engine}...");
            var error = await panel.AttachFileAsync(path);
            if (error is not null) MessageBox.Show(this, error, "Bcode — " + engine, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else PushStatus($"Đã đính kèm {Path.GetFileName(path)} vào {engine} — gõ câu hỏi rồi Enter.");
        }
        catch (Exception ex) { PushStatus("Không gửi được sang " + engine + ": " + ex.Message); }
    }

    private RawSqlControl CreateFreeScriptControl(bool prewarm = false)
    {
        var control = new RawSqlControl(_rawSqlService, _sqlObjectService, _lookupService, _snippets, prewarm);
        control.ResultReady += table => _lastQueryResult = table;
        control.CurrentProject = () => _connections.Current?.Name ?? "";
        control.SaveHistoryRequested += (script, sys) => QueryHistoryService.Instance.Save(_connections.Current?.Name ?? "", sys, script);   // chỉ lưu khi người dùng bấm "Lưu lịch sử"
        control.CreateRptRequested += sql => OpenCreateRptTab(sql, pivot: true);
        control.OpenResultInNewTabRequested += (tables, title) =>
        {
            var view = new MultiResultView();
            view.SetTables(tables);
            AddDocumentTab(title, view);
        };
        control.OpenProcedureWithQueryRequested += (identifier, useSys, script) =>
            _ = OpenProcedureWithQueryAsync(control, identifier, useSys);
        control.DebugTargetChosen += (target, call) => _ = OpenDebugTargetAsync(target, call);
        control.AskAiRequested += (engine, text) => SendScriptToAi(engine, text, control);
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

        var control = TakeSqlControl();
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
    private TabPage? _setupEInvoiceTab;

    /// <summary>Tab "Setup eInvoice (FE)" (WebView2) — thay cho form cũ <see cref="SetupEInvoiceForm"/> (class vẫn còn trong project nhưng không mở nữa). Một tab duy nhất.</summary>
    private void OpenSetupEInvoiceTab()
    {
        if (_setupEInvoiceTab is not null && _documentTabs.TabPages.Contains(_setupEInvoiceTab))
        {
            _documentTabs.SelectedTab = _setupEInvoiceTab;
            return;
        }
        _setupEInvoiceTab = AddDocumentTab("Setup eInvoice", new SetupEInvoiceControl(_connections));
        _setupEInvoiceTab.Disposed += (_, _) => _setupEInvoiceTab = null;
    }

    private TabPage? _stringBeautyTab;

    /// <summary>Tab "String Beauty" (WebView2) — thay cho form cũ <see cref="StringBeautyForm"/> (class vẫn còn trong project nhưng không mở nữa). Một tab duy nhất, mở lại thì chuyển tới.</summary>
    private void OpenStringBeautyTab()
    {
        if (_stringBeautyTab is not null && _documentTabs.TabPages.Contains(_stringBeautyTab))
        {
            _documentTabs.SelectedTab = _stringBeautyTab;
            return;
        }
        _stringBeautyTab = AddDocumentTab("String Beauty", new StringBeautyControl());
        _stringBeautyTab.Disposed += (_, _) => _stringBeautyTab = null;
    }

    private TabPage? _decryptSqlTab;

    /// <summary>Tab "Decrypt SQL Object" (WebView2): giải mã object WITH ENCRYPTION qua DAC — một tab duy nhất, mở lại thì chuyển tới tab đó.</summary>
    private void OpenDecryptSqlTab()
    {
        if (!RequireLicense("Decrypt SQL Object")) return;
        if (_decryptSqlTab is not null && _documentTabs.TabPages.Contains(_decryptSqlTab))
        {
            _documentTabs.SelectedTab = _decryptSqlTab;
            return;
        }
        _decryptSqlTab = AddDocumentTab("Decrypt SQL Object", new DecryptSqlObjectControl(() => _connections.Current));
        _decryptSqlTab.Disposed += (_, _) => _decryptSqlTab = null;
    }

    private TabPage? _checkMailTab;

    /// <summary>Tab "Check Mail": khai báo SMTP (host/port/SSL-TLS/tài khoản) và gửi thử email — một tab duy nhất, mở lại thì chuyển tới tab đó.</summary>
    private void OpenCheckMailTab()
    {
        if (_checkMailTab is not null && _documentTabs.TabPages.Contains(_checkMailTab))
        {
            _documentTabs.SelectedTab = _checkMailTab;
            return;
        }
        _checkMailTab = AddDocumentTab("Check Mail", new CheckMailControl(() => _connections.Current));
        _checkMailTab.Disposed += (_, _) => _checkMailTab = null;
    }

    /// <summary>"Create RPT &amp; XML" và "Excel → FRX" chỉ dùng được khi key đã dán ở Settings khớp key khai báo trong source (<see cref="LicenseService"/>).
    /// Chưa kích hoạt thì mở luôn hộp thoại dán key; dán đúng thì dùng tiếp được ngay.</summary>
    private bool RequireLicense(string feature)
    {
        if (LicenseService.IsUnlocked) return true;
        using var f = new LicenseKeyForm($"Tính năng “{feature}” cần key bản quyền. Dán key rồi bấm Xác nhận.");
        f.ShowDialog(this);
        return LicenseService.IsUnlocked;
    }

    private TabPage? _createRptTab;

    /// <summary>Tab "Create RPT &amp; XML" (WebView2): profiler → controller → chọn field → thiết kế Excel mẫu → sinh .xlsx + .xml. Một tab duy nhất.
    /// Form WinForms cũ <see cref="CreateRptXlsxForm"/> vẫn còn trong project nhưng toolbar không mở nữa.</summary>
    private void OpenCreateRptTab(string? sql = null, bool pivot = false)
    {
        if (!RequireLicense("Create RPT & XML")) return;
        if (_createRptTab is not null && _documentTabs.TabPages.Contains(_createRptTab))
        {
            _documentTabs.SelectedTab = _createRptTab;
            if (sql is not null) _createRptTab.Controls.OfType<CreateRptControl>().FirstOrDefault()?.Prefill(sql, pivot);
            return;
        }
        var created = new CreateRptControl(() => _connections.Current, new ReportProfilerService(_rawSqlService));
        if (sql is not null) created.Prefill(sql, pivot);
        _createRptTab = AddDocumentTab("Create RPT & XML", created);
        _createRptTab.Disposed += (_, _) => _createRptTab = null;
    }

    private TabPage? _excelToFrxTab;

    /// <summary>Tab "Excel → FRX": Excel mẫu in → FastReport (.frx), kéo thả chỉnh bố cục, xem trước PDF (ExcelToFrx.dll, source ở
    /// D:\phongnt\ConvertBcode). Một tab duy nhất, mở lại thì chuyển tới; <paramref name="xlsxPath"/> = nạp sẵn file đó.</summary>
    private void OpenExcelToFrxTab(string? xlsxPath = null)
    {
        if (!RequireLicense("Excel → FRX")) return;
        if (_excelToFrxTab is not null && _documentTabs.TabPages.Contains(_excelToFrxTab))
        {
            _documentTabs.SelectedTab = _excelToFrxTab;
            if (xlsxPath is not null && _excelToFrxTab.Controls.OfType<ExcelToFrx.ExcelToFrxControl>().FirstOrDefault() is { } open)
                open.OpenExcel(xlsxPath);
            return;
        }

        // FrxGenerator.exe + mẫu .frx chuẩn được csproj chép ra <output>\ExcelToFrx\ (xem Libs/ExcelToFrx/README.md)
        var toolDir = Path.Combine(AppContext.BaseDirectory, "ExcelToFrx");
        var templates = new List<string> { Path.Combine(toolDir, "Templates", "Frx") };
        if (!string.IsNullOrWhiteSpace(_settings.FrxOutputDir)) templates.Add(_settings.FrxOutputDir);
        var ctl = new ExcelToFrx.ExcelToFrxControl(new ExcelToFrx.ExcelToFrxOptions
        {
            FrxGeneratorPath = Path.Combine(toolDir, "FrxGenerator", "FrxGenerator.exe"),
            TemplateFolders = templates,
            DefaultOutputDir = _settings.FrxOutputDir,
        })
        {
            EnvironmentProvider = WebViewEnvironment.GetAsync,   // dùng chung trình duyệt với các trang khác
            DarkTheme = AppColors.IsDark,
        };
        // Theme sáng/tối + Template giao diện (font, mật độ, bo góc) như các trang Web/Shell
        UiTemplate.BindWeb(ctl.WebView);
        WebViewEnvironment.AttachGlobalShortcuts(ctl.WebView); // WebView2 do ExcelToFrx.dll tạo: gắn phím tắt toàn cục như các trang khác
        void OnTheme() => ctl.DarkTheme = AppColors.IsDark;
        ThemeChanged += OnTheme;
        ctl.Disposed += (_, _) => ThemeChanged -= OnTheme;

        if (xlsxPath is not null) ctl.OpenExcel(xlsxPath);
        _excelToFrxTab = AddDocumentTab("Excel → FRX", ctl);
        _excelToFrxTab.Disposed += (_, _) => _excelToFrxTab = null;
    }

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

    /// <summary>Trả focus bàn phím về vùng làm việc (editor SQL hoặc tab đang mở) sau khi bấm vào thanh trên (ô chọn WS, nút Sys/App, Script...). Thanh trên là 1 WebView2
    /// riêng: bấm vào nó thì focus nằm trong đó nên phím tắt của editor (F5, F10...) không tới được editor, và sau khi ô chọn <c>&lt;select&gt;</c> đóng, focus đôi khi
    /// không trả lại form nên phím tắt "bấm hoài không ăn" cho tới khi bấm chuột vào chỗ khác.</summary>
    private void RestoreContentFocus() => RestoreContentFocus(activate: true);

    private void RestoreContentFocus(bool activate)
    {
        if (IsDisposed || !IsHandleCreated) return;
        // Đổi tab: không tự kéo cửa sổ lên trước nếu Bcode đang không phải ứng dụng đang dùng (vd tab đổi do tác vụ nền).
        if (!activate && Form.ActiveForm is null) return;
        if (activate && !ReferenceEquals(Form.ActiveForm, this)) Activate();
        var page = _documentTabs.SelectedTab;
        if (page?.Controls.OfType<RawSqlControl>().FirstOrDefault() is { } sql) sql.FocusEditor();
        else if (page?.Controls.OfType<AiWebPanel>().FirstOrDefault() is { } ai) ai.FocusWeb(); // Claude/Gemini: focus vào trang web để gõ được ngay
        else if (page is not null) { page.Focus(); page.SelectNextControl(page, true, true, true, false); }
        else _documentTabs.Focus();
    }

    // ---- Giữ Ctrl → chọn tab ----
    private const int CtrlHoldMs = 700;
    private readonly System.Windows.Forms.Timer _ctrlHoldTimer = new() { Interval = CtrlHoldMs };
    private bool _switcherOpen;

    private void StartCtrlHold()
    {
        if (!ReferenceEquals(Form.ActiveForm, this)) return;
        _ctrlHoldTimer.Stop();
        _ctrlHoldTimer.Start();
    }

    private void StopCtrlHold() => _ctrlHoldTimer.Stop();

    /// <summary>Theo dõi phím/chuột ở mức cả ứng dụng: Ctrl vừa nhấn (không phải lặp phím) thì bắt đầu đếm; bấm thêm phím nào, bấm/lăn chuột,
    /// hoặc thả Ctrl thì huỷ — để Ctrl+C, Ctrl+Click... không bật hộp chọn tab.</summary>
    private sealed class CtrlHoldFilter : IMessageFilter
    {
        private readonly MainForm _form;
        public CtrlHoldFilter(MainForm form) => _form = form;

        public bool PreFilterMessage(ref Message m)
        {
            switch (m.Msg)
            {
                case 0x0100: case 0x0104: // WM_KEYDOWN / WM_SYSKEYDOWN
                    if ((int)m.WParam == 0x11) { if (((long)m.LParam & 0x40000000) == 0) _form.StartCtrlHold(); }
                    else _form.StopCtrlHold();
                    break;
                case 0x0101: case 0x0105: // WM_KEYUP / WM_SYSKEYUP
                    if ((int)m.WParam == 0x11) _form.StopCtrlHold();
                    break;
                case 0x0201: case 0x0204: case 0x0207: case 0x020A: // bấm chuột / lăn chuột
                    _form.StopCtrlHold();
                    break;
            }
            return false;
        }
    }

    /// <summary>Hộp liệt kê mọi tab đang mở để chọn tab cần đến (xem <see cref="TabSwitcherForm"/>).</summary>
    internal void ShowTabSwitcher()
    {
        if (_switcherOpen || IsDisposed || _documentTabs.TabPages.Count == 0) return;
        _switcherOpen = true;
        try
        {
            var names = _documentTabs.TabPages.Cast<TabPage>().Select(p => p.Text).ToList();
            using var switcher = new TabSwitcherForm(names, Math.Max(0, _documentTabs.SelectedIndex));
            switcher.ShowDialog(this);
            var i = switcher.SelectedIndex;
            if (i >= 0 && i < _documentTabs.TabPages.Count)
            {
                _documentTabs.SelectedIndex = i;
                _documentTabs.SelectedTab?.Focus();
            }
        }
        finally { _switcherOpen = false; }
    }

    private void OnWebGlobalShortcut(string combo)
    {
        if (combo == "@ctrl-hold")
        {
            if (!IsDisposed && (Form.ActiveForm is null || ReferenceEquals(Form.ActiveForm, this))) BeginInvoke(new Action(ShowTabSwitcher));
            return;
        }
        // Chỉ bỏ qua khi đang ở một hộp thoại khác của Bcode. ActiveForm = null (vd ngay sau khi đóng ô chọn <select> của WebView2 — cửa sổ popup vừa đóng
        // chưa trả focus về form) thì vẫn xử lý: phím đã tới được trang web tức là cửa sổ này đang nhận bàn phím.
        if (IsDisposed || (Form.ActiveForm is { } active && !ReferenceEquals(active, this))) return;
        // Chạy SAU khi callback WebMessageReceived của WebView2 đã trả về: nhiều phím tắt mở hộp thoại modal (Projects, Choose Server...), mà trong hộp thoại đó
        // lại tạo WebView2 mới (Edit Project...) — tạo WebView2 khi đang đứng trong callback của WebView2 khác là lỗi "Class not registered" / E_ABORT.
        if (Bcode.App.UI.ShortcutRegistry.AppIdFor(combo) is { } id) BeginInvoke(new Action(() => { if (!IsDisposed) RunAppShortcut(id); }));
    }

    private void OpenUiTemplate()
    {
        using var form = new UiTemplateForm(_settings, OrderedToolSpecs().Select(t => (t.key, t.label)).ToList(), _toolSpecs.Select(t => t.key).ToList(), _wcommandTree.TopGroups());
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

    /// <summary>Bật / tắt tuỳ chỉnh giao diện web (CSS riêng + HTML ghi đè): tắt = mọi trang web dùng bản gốc. Lưu vào template, các trang đang mở nạp lại.</summary>
    private void ToggleCustomUi()
    {
        var t = Bcode.App.UI.UiTemplate.Current.Clone();
        t.CustomUiEnabled = !Bcode.App.UI.UiTemplate.Current.CustomUiEnabled;
        Bcode.App.UI.UiOverrides.SessionSafe = false; // đã chủ động chọn thì bỏ chế độ an toàn tạm của phiên
        Bcode.App.UI.UiTemplate.Current = t;
        try { t.Save(); } catch { /* không lưu được thì chỉ áp cho phiên này */ }
        Bcode.App.UI.UiOverrides.RaisePageChanged("*");
        PushStatus(t.CustomUiEnabled ? "Đã bật tuỳ chỉnh giao diện web." : "Đã tắt tuỳ chỉnh giao diện web (chế độ an toàn) — mọi trang dùng bản gốc.");
    }

    /// <summary>Ctrl+Shift+H: ẩn / hiện cây menu bên trái để vùng làm việc rộng ra (ẩn đi thì tab chiếm toàn bộ chiều ngang).</summary>
    private void ToggleMenuTree()
    {
        if (_mainSplit is null) return;
        _mainSplit.Panel1Collapsed = !_mainSplit.Panel1Collapsed;
        UpdateQuickAccessOverlayBounds();
    }

    /// <summary>Phím bấm (WinForms) → chức năng theo bảng phím tắt hiện hành (<see cref="Bcode.App.UI.ShortcutRegistry"/>: mặc định + phần người dùng khai báo lại).</summary>
    public bool HandleGlobalShortcut(Keys keyData)
    {
        var combo = Bcode.App.UI.ShortcutRegistry.FromKeys(keyData);
        if (combo is null) return false;
        var id = Bcode.App.UI.ShortcutRegistry.AppIdFor(combo);
        return id is not null && RunAppShortcut(id);
    }

    /// <summary>Chạy chức năng phạm vi App theo id ("tree.toggle", "tab.close", "tool:sql_query"...). Trả false nếu id không chạy được lúc này.</summary>
    private bool RunAppShortcut(string id)
    {
        switch (id)
        {
            case "tree.toggle": ToggleMenuTree(); return true;
            case "project.quick": _ = QuickSelectProjectByCodeAsync(); return true;
            case "project.picker": ShowProjectPicker(); return true;
            case "debug.decrypt": DebugDecryptConnectStr(); return true;
            case "app.chooseServer": OpenConnectionSettings(); return true;
            case "app.programPath": OpenProgramPath(); return true;
            case "project.openSource": OpenProjectFolder(_connections.Current?.SourcePath ?? ""); return true;
            case "project.openWorking": OpenProjectFolder(_connections.Current?.WorkingPath ?? ""); return true;
            case "project.openMobile": OpenProjectFolder(_connections.Current?.MobilePath ?? ""); return true;
            case "project.web": OpenProjectWeb(); return true;
            case "project.copyInfo":
                if (_connections.Current is { } projectWs) CopyProjectInfo(projectWs);
                return true;
            case "tab.close": CloseDocumentTab(_documentTabs.SelectedIndex); return true;
            case "tab.pin":
                if (_documentTabs.SelectedTab is { } pinPage) _documentTabs.SetPinned(pinPage, !_documentTabs.IsPinned(pinPage));
                return true;
            case "tab.closeOthers":
                if (_documentTabs.SelectedTab is { } keepPage) CloseTabsWhere(p => p != keepPage);
                return true;
            case "tab.closeRight":
            {
                var from = _documentTabs.SelectedIndex;
                if (from >= 0) CloseTabsWhere(p => _documentTabs.TabPages.IndexOf(p) > from);
                return true;
            }
            case "tab.closeAll": CloseTabsWhere(_ => true); return true;
            case "app.theme": Bcode.App.UI.ThemeManager.Toggle(this); PushThemeToShell(); return true;
            case "app.palette": BeginInvoke(new Action(OpenCommandPalette)); return true;
            case "app.usages": OpenUsagesForCurrentTab(); return true;
            case "app.sqlHints": OpenSqlHintsHelpTab(); return true;
            case "app.restoreSession": ToggleRestoreSession(); return true;
            case "app.quickAccess": BeginInvoke(new Action(OpenQuickAccess)); return true;
            case "app.settingsMenu": _settingsMenu().Show(_topBarWeb, 10, _topBarWeb.Height); return true;
            case "app.template": BeginInvoke(new Action(OpenUiTemplate)); return true;
            case "app.customUi": ToggleCustomUi(); return true;
            case "db.app": ApplyActiveDatabase(false); return true;
            case "db.sys": ApplyActiveDatabase(true); return true;
            case "script.add": AddScript(); return true;
            case "script.view": ViewScriptCart(); return true;
            case "script.clear": _scriptFileService.ClearCart(); return true;
            case "script.save": SaveActiveScript(); return true;
            case "script.copy": CopyActiveScript(); return true;
            case "app.refreshWebConfig": RefreshWebConfig(); return true;
            case "app.clearStructure": ClearStructureApp(); return true;
            case "app.createMenu": _ = CreateMenuAsync(); return true;
            case "tab.next":
            case "tab.prev":
            {
                // Chuyển tab kế / trước (vòng tròn), bấm được ở bất kỳ đâu miễn là có tab.
                var count = _documentTabs.TabPages.Count;
                if (count == 0) return false;
                var step = id == "tab.prev" ? -1 : 1;
                _documentTabs.SelectedIndex = (Math.Max(0, _documentTabs.SelectedIndex) + step + count) % count;
                _documentTabs.SelectedTab?.Focus();
                return true;
            }
            case "window.new":
                // Mở thêm 1 cửa sổ Bcode MỚI chạy song song (mỗi cửa sổ làm 1 dự án — lúc mở có màn hình Projects để chọn).
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

        if (id.StartsWith("tab.goto", StringComparison.Ordinal) && int.TryParse(id["tab.goto".Length..], out var gotoNo))
        {
            if (gotoNo < 1 || gotoNo > _documentTabs.TabPages.Count) return false;
            _documentTabs.SelectedIndex = gotoNo - 1;
            _documentTabs.SelectedTab?.Focus();
            return true;
        }

        if (id.StartsWith("tool:", StringComparison.Ordinal))
        {
            var spec = _toolSpecs.FirstOrDefault(t => "tool:" + t.key == id);
            if (spec.action is not null) { spec.action(this, EventArgs.Empty); return true; }
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
            // Có bản lưu trên máy thì hiện ngay, kiểm tra database ngầm; chưa có thì lấy từ database (rồi lưu cho lần sau).
            var cached = _sqlObjectService.TryGetCachedDefinition(obj);
            var definition = cached?.Text ?? await _sqlObjectService.GetDefinitionAndCacheAsync(obj);
    
            // 1. Nếu Procedure/Bảng này đã có tab đang mở -> Chuyển focus đến tab đó
            if (_objectTabs.TryGetValue(key, out var existingPage) && _documentTabs.TabPages.Contains(existingPage))
            {
                _documentTabs.SelectedTab = existingPage;
                if (existingPage.Controls.OfType<RawSqlControl>().FirstOrDefault() is { } existingCtrl)
                {
                    existingCtrl.SetDatabase(obj.FromSysDatabase);
                    await existingCtrl.SetScriptTextAsync(definition);
                    if (cached is not null) _ = RefreshCachedDefinitionAsync(obj, existingCtrl, cached);
                    return existingCtrl;
                }
            }

            // 2. Nếu là Procedure/Bảng khác -> Khởi tạo một tab SQL Query mới (RawSqlControl)
            var control = TakeSqlControl();
            control.SetDatabase(obj.FromSysDatabase);
            control.SetScriptText(definition); // Tự động nạp code vào Monaco khi editor sẵn sàng

            // 3. Đặt tiêu đề tab theo tên QualifiedName (ví dụ: dbo.rs_Transfer$AfterSynchronize)
            var page = AddDocumentTab(obj.QualifiedName, control);
            _objectTabs[key] = page;
            page.Disposed += (_, _) => _objectTabs.Remove(key);
            if (cached is not null) _ = RefreshCachedDefinitionAsync(obj, control, cached);

            return control;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — SQL Object", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }
    /// <summary>Sau khi hiện bản lưu: hỏi database xem object đã đổi chưa. Đổi và bạn chưa sửa gì trong tab → thay bằng bản mới; bạn đã sửa → giữ bản của bạn và báo ở thanh trạng thái.</summary>
    private async Task RefreshCachedDefinitionAsync(SqlObjectInfo obj, RawSqlControl control, SqlObjectBrowserService.DefinitionCache cached)
    {
        try
        {
            var (changed, text) = await _sqlObjectService.RefreshIfChangedAsync(obj, cached);
            if (!changed || control.IsDisposed) return;
            var current = control.IsReady ? await control.GetScriptTextAsync() : cached.Text;
            if (current == cached.Text)
            {
                await control.SetScriptTextAsync(text);
                PushStatus($"{obj.QualifiedName}: trên database đã có bản mới hơn — tab đã được cập nhật.");
            }
            else PushStatus($"{obj.QualifiedName}: trên database đã có bản mới hơn nhưng bạn đã sửa trong tab nên giữ nguyên — đóng tab rồi mở lại để lấy bản mới.");
        }
        catch { /* offline / lỗi nhẹ — vẫn dùng bản lưu */ }
    }

    private async Task OpenProcedureWithQueryAsync(RawSqlControl source, string identifier, bool useSysDatabase)
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

        // Chèn định nghĩa lên đầu ngay trong tab đang Ctrl + chuột phải (như FCode) rồi nhảy lên đó — không mở tab mới nữa.
        // Database của tab theo database chứa object, như tab riêng trước đây: script CREATE/ALTER phải chạy đúng chỗ của nó.
        if (source.UseSysDatabase != obj.FromSysDatabase) source.SetDatabase(obj.FromSysDatabase);
        await source.PrependDefinitionAsync(definition);
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
        using var form = new LibrarySnippetForm(_snippets, _connections.Current?.Name ?? "", _settings.Workspaces.Select(w => w.Name));
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

    /// <summary>Add Script trên toolbar/menu/phím tắt — tuỳ tab đang chọn (giống FCode):
    ///   - tab Table: script DELETE + nạp lại toàn bộ dữ liệu đang xem của bảng đó;
    ///   - tab SQL Query: câu lệnh đang chọn (không chọn thì cả editor) kèm header FCode + GO;
    /// hai trường hợp trên tự copy vào clipboard + thêm vào Script Cart (View Script để xem). Tab khác: chọn file
    /// .f/.xml/.sql từ máy để thêm vào Script Cart như trước.</summary>
    private void AddScript() => _ = AddScriptAsync();

    private async Task AddScriptAsync()
    {
        var page = _documentTabs.SelectedTab;
        try
        {
            if (page?.Controls.OfType<TableEditControl>().FirstOrDefault() is { } table)
            {
                if (await table.BuildAddScriptAsync() is { } result)
                    AddGeneratedScript(result.Script);
                return;
            }

            if (page?.Controls.OfType<RawSqlControl>().FirstOrDefault() is { } sql)
            {
                var text = await sql.GetSelectedTextAsync();
                if (string.IsNullOrWhiteSpace(text)) text = await sql.GetScriptTextAsync();
                if (string.IsNullOrWhiteSpace(text))
                {
                    MessageBox.Show(this, "Editor đang trống — chưa có câu lệnh để sinh Script.", "Bcode — Add Script");
                    return;
                }
                AddGeneratedScript(_dataScript.GenerateQueryScript(text));
                return;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Add Script", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        AddScriptFiles();
    }

    /// <summary>Script vừa sinh từ Add Script: copy vào clipboard và thêm thẳng vào Script Cart (không ghi
    /// file) để View Script gom lại — không mở cửa sổ nào.</summary>
    private void AddGeneratedScript(string script)
    {
        _scriptFileService.AddTextToCart(script);
        try { Clipboard.SetText(script); } catch { /* clipboard đang bị app khác giữ — script vẫn có trong cart */ }

        PushStatus($"Đã copy script vào clipboard và thêm vào Script Cart. Tổng số lượng: {_scriptFileService.Cart.Count} file.");
    }

    private void AddScriptFiles()
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

        // Hiện trong cửa sổ Script riêng (Monaco, không lag với script lớn); Clear ở đây xoá luôn Script Cart.
        var form = new ScriptPopupForm(_scriptFileService.ViewCartConcatenated(),
            $"Script Cart ({_scriptFileService.Cart.Count} files)", "script_cart.sql");
        form.Cleared += () =>
        {
            _scriptFileService.ClearCart();
            PushStatus("Đã xoá Script Cart.");
        };
        form.Show(this);
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
            PushStatus($"Workspace: {cur.Name}  —  Server: {cur.Server}  |  Dev: HàoTN|PhongNT | Tester: ThinhBM| KhanhNN");
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
        menu.Items.Add(new ToolStripMenuItem("Choose Server", null, (_, _) => OpenConnectionSettings()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("app.chooseServer") });
        menu.Items.Add(new ToolStripMenuItem("Projects...", null, (_, _) => ShowProjectPicker()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("project.picker") });
        menu.Items.Add(new ToolStripMenuItem("New Bcode window", null, (_, _) => RunAppShortcut("window.new")) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("window.new") });
        menu.Items.Add(new ToolStripMenuItem("Switch Database", null, (_, _) => { /* Chức năng chưa rõ */ }) { ShortcutKeyDisplayString = "Ctrl+1" });
        menu.Items.Add(new ToolStripMenuItem("SQL Profiler", null, (_, _) => OpenSqlProfilerTab()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tool:sql_profiler") });
        menu.Items.Add(new ToolStripMenuItem("SQL SMS", null, (_, _) => { /* Mở SSMS */ }) { ShortcutKeyDisplayString = "Ctrl+4" });
        menu.Items.Add(new ToolStripMenuItem("Open Program Path", null, (_, _) => OpenProgramPath()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("app.programPath") });
        
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Refresh", null, (_, _) => { /* Lệnh refresh */ }));
        menu.Items.Add(new ToolStripSeparator());

        // ---------------------------------------------------------
        // NHÓM 2: SCRIPT (Tạo Submenu có mũi tên chĩa ngang)
        // ---------------------------------------------------------
        var scriptMenu = new ToolStripMenuItem("Script");
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("Library (Script đã lưu)...", null, (_, _) => OpenLibrary()));
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("Add Script", null, (_, _) => AddScript()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("script.add") });
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("View Script Cart", null, (_, _) => ViewScriptCart()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("script.view") });
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("Clear Script", null, (_, _) => _scriptFileService.ClearCart()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("script.clear") });
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("Save Script", null, (_, _) => SaveActiveScript()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("script.save") });
        scriptMenu.DropDownItems.Add(new ToolStripMenuItem("Copy Script", null, (_, _) => CopyActiveScript()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("script.copy") });
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
            var itemCombo = Bcode.App.UI.ShortcutRegistry.Display("tool:" + key);
            if (itemCombo.Length > 0) item.ShortcutKeyDisplayString = itemCombo;
            toolsMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(toolsMenu);

        menu.Items.Add(new ToolStripSeparator());

        // ---------------------------------------------------------
        // NHÓM 4: QUẢN LÝ TAB VÀ CỬA SỔ
        // ---------------------------------------------------------
        menu.Items.Add(new ToolStripMenuItem("Close Tab", null, (_, _) => {
            if (_documentTabs.SelectedIndex >= 0) CloseDocumentTab(_documentTabs.SelectedIndex);
        }) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.close") });
        
        menu.Items.Add(new ToolStripMenuItem("Close All But This", null, (_, _) => {
            var currentIdx = _documentTabs.SelectedIndex;
            for (int i = _documentTabs.TabPages.Count - 1; i >= 0; i--)
                if (i != currentIdx) CloseDocumentTab(i);
        }) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.closeOthers") });
        
        menu.Items.Add(new ToolStripMenuItem(
            Bcode.App.UI.UiOverrides.Enabled ? "Tuỳ chỉnh giao diện web: đang BẬT (bấm để tắt — chế độ an toàn)" : "Tuỳ chỉnh giao diện web: đang TẮT (bấm để bật)", null,
            (_, _) => ToggleCustomUi()));
        menu.Items.Add(new ToolStripMenuItem("Hide / Show cây menu", null, (_, _) => ToggleMenuTree()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tree.toggle") });

        menu.Items.Add(new ToolStripMenuItem("Close Tabs to the Right", null, (_, _) => {
            if (_documentTabs.SelectedTab is not { } cur) return;
            var from = _documentTabs.SelectedIndex;
            CloseTabsWhere(p => _documentTabs.TabPages.IndexOf(p) > from);
        }) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.closeRight") });

        menu.Items.Add(new ToolStripMenuItem("Close All Tab", null, (_, _) => {
            for (int i = _documentTabs.TabPages.Count - 1; i >= 0; i--)
                CloseDocumentTab(i);
        }) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.closeAll") });

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