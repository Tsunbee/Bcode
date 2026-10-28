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
    // Chế độ App (UiMode.Native): ba thanh khung là control WinForms thay cho ba WebView2 ở trên (WebView2 không được khởi động).
    private NativeTopBar? _topNative;
    private NativeStatusBar? _statusNative;
    private IconRailControl? _railNative;
    private Control TopBarControl => _topNative ?? (Control)_topBarWeb;
    private Control StatusBarControl => _statusNative ?? (Control)_statusBarWeb;
    private Control IconRailHost => _railNative ?? (Control)_iconRailWeb;
    private Func<WebMenu> _settingsMenu = null!;

    /// <summary>Khai 1 lần các mẫu tên file bị bỏ qua khi dựng cây file của MENU (File Lookup theo menu, Gen Update, Gen All...): file khớp không hiện và không bị quét nội dung nên dựng cây nhanh hơn.
    /// Kiểu LIKE: không có * thì "tên chứa" (_bk khớp X_bk, X_Bk20, X_bk12312), có * hoặc % thì khớp cả tên (tmp*, *_old). Cách nhau bằng dấu phẩy.</summary>
    private void EditMenuTreeIgnore()
    {
        var text = SimplePromptForm.Show(this, "Cây menu — bỏ qua file",
            "Bỏ qua file có tên (không đuôi) khớp — cách nhau bằng dấu phẩy. Không có * thì \"chứa\" (vd _bk khớp X_bk, X_Bk20, X_bk12312, không khớp zpt_bkhd vì ngay sau là chữ cái); có * thì khớp cả tên (vd tmp*, *_old). Để trống = không bỏ qua:",
            _settings.FileLookupIgnoreSuffixes);
        if (text is null) return;
        _settings.FileLookupIgnoreSuffixes = text.Trim();
        try { _settings.Save(); } catch { /* không lưu được thì chỉ áp dụng phiên này */ }
        FileLookupService.SetIgnoreSuffixes(_settings.FileLookupIgnoreSuffixes);
        FileLookupService.SetSearchRules(_settings);
        PushStatus("Đã lưu mẫu bỏ qua file của cây menu — bấm lại menu để dựng cây theo mẫu mới.");
    }
    /// <summary>Mở file mẫu quy tắc đoán pivot (%AppData%\Bcode\pivot-rules.txt, chưa có thì tạo từ mẫu mặc định) bằng NotePadApp — lưu là áp dụng ở lần chạy SQL sau.</summary>
    private void EditPivotRules()
    {
        try
        {
            var path = Bcode.App.Services.Rpt.PivotRules.EnsureFile();
            var app = string.IsNullOrWhiteSpace(_settings.NotePadApp) ? "notepad.exe" : _settings.NotePadApp;
            try { Process.Start(new ProcessStartInfo(app, "\"" + path + "\"") { UseShellExecute = true }); }
            catch { Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true }); }
            PushStatus("Đã mở " + path + " — sửa, lưu rồi chạy lại SQL để tab Pivot áp quy tắc mới.");
        }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được file quy tắc pivot:\n" + ex.Message, "Bcode"); }
    }
    private SqlObjectTreeControl _sqlObjectTree = null!;
    private WCommandTreeControl _wcommandTree = null!;
    // Mục "Mobile" ở cột icon trái: cây menu app mobile (bảng dvcommand, Sys Data) — cùng control với WCommand, file nằm ở thư mục MobileService.
    private WCommandTreeControl _mobileTree = null!;
    private FileLookupControl? _mobileLookup;     // tab File Lookup dùng lại cho menu mobile (double-click); F4 thì mở tab mới
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
        _headerContainer.Height = TopBarControl.Height + _toolsBar.Height;
    }
    private readonly List<(string key, string label, string? shortcut, EventHandler action)> _toolSpecs = new();
    private TabPage? _fileLookupTabPage;
    private Controls.FileLookupControl? _fileLookupControl;
    private TabPage? _genUpdatePackageTabPage;
    private Controls.GenUpdatePackageControl? _genUpdatePackageControl;
    private TabPage? _lookupTabPage;
    private TabPage? _fileReferenceTabPage;
    private TabPage? _rawSqlTabPage;
    private ISqlTab? _rawSqlControl;
    private readonly Dictionary<string, TabPage> _noteTabs = new();
    private readonly Dictionary<string, TabPage> _objectTabs = new();
    private readonly Dictionary<string, TabPage> _debugTargetTabs = new();
    
    // 1. ĐÃ BỔ SUNG BIẾN NÀY ĐỂ TRÁNH LỖI Ở HÀM OpenCompareTextTab
    private TabPage? _compareTextTab;
    private TabPage? _sqlProfilerTab;
    private readonly ViewerControlServer _viewerControl = new();
    private QuickLaunchLoginForm? _quickLaunchForm;
    private QuickListForm? _quickListForm;

    /// <summary>"Tạo nhanh danh mục" mở song song (modeless) — vẫn quay lại Bcode được trong lúc làm; mở lại khi đang mở thì chỉ đưa cửa sổ đó lên.</summary>
    private void OpenQuickList()
    {
        if (!RequireLicense("Tạo nhanh danh mục")) return;
        if (_quickListForm is { IsDisposed: false } open)
        {
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }
        _quickListForm = new QuickListForm(_sqlObjectService, _tableDataService, _connections, _wcommandService, _settings);
        _quickListForm.FormClosed += (_, _) => _quickListForm = null;
        // Tab Script (bảng mới / cột thêm): Add Script = như Add Script của Bcode (Script Cart + clipboard); Mở SQL Query = tab mới trên App Data.
        _quickListForm.AddScriptRequested += AddGeneratedScript;
        _quickListForm.OpenSqlRequested += OpenViewerScriptTab;
        _quickListForm.Show();
    }

    /// <summary>
    /// Note (New) → "🤖 AI tạo danh mục" (bản thử): mở (hoặc đưa lên) Tạo nhanh danh mục rồi để AI đọc y/c và điền spec ở đó.
    /// AI không ghi gì — người dùng duyệt, sửa, bấm Tạo file như bình thường.
    /// </summary>
    private void OpenQuickListWithAi(string requestName, string content)
    {
        OpenQuickList();
        if (_quickListForm is not { IsDisposed: false }) return;   // chưa có key — RequireLicense đã báo
        _quickListForm.StartAi(requestName, content);
    }

    /// <summary>
    /// "🔎 AI phân tích" (mode "analyze"): mở Tạo nhanh danh mục, AI viết lại y/c theo mẫu luật Fast rồi dừng cho người dùng sửa / xác nhận,
    /// xác nhận xong luật Fast (không AI) mới điền. "⚙ Theo luật Fast" (mode "rules"): điền ngay theo luật, không gọi AI.
    /// </summary>
    private void OpenQuickListWithAssist(string requestName, string content, string mode)
    {
        OpenQuickList();
        if (_quickListForm is not { IsDisposed: false }) return;
        if (mode == "rules") _quickListForm.StartRules(requestName, content);
        else _quickListForm.StartAnalyze(requestName, content);
    }
    /// <summary>MainForm tự dàn bằng Dock + thanh web báo chiều cao, nên không nhân bố cục thêm theo UiScale.</summary>
    protected override bool ScaleLayoutWithUiScale => false;

    public MainForm()
    {
        // Giữ Shift lúc mở Bcode = chế độ an toàn cho phiên này: bỏ qua CSS riêng và bản HTML ghi đè (xem UiOverrides).
        Bcode.App.UI.UiOverrides.SessionSafe = (Control.ModifierKeys & Keys.Shift) == Keys.Shift;
        Bcode.App.UI.UiOverrides.Notice += msg => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => PushStatus(msg)); };
        Bcode.App.UI.WebViewEnvironment.TempProfileNotice += msg => { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => PushStatus(msg)); };   // thư mục WebView2 chính bận → phiên này dùng hồ sơ tạm
        // Save as CSV / Excel ở tab Result: tiến độ + kết quả (kèm link Mở file / Mở thư mục) lên thanh trạng thái của đúng cửa sổ.
        Action<Form?, string, string?> onExport = (owner, text, path) => { if ((owner is null || owner == this) && !IsDisposed && IsHandleCreated) BeginInvoke(() => PushStatus(text, path)); };
        ResultExport.Notice += onExport;
        Disposed += (_, _) => ResultExport.Notice -= onExport;
        _settings = AppSettings.Load();
        Bcode.App.UI.UiThemes.ApplyPalettes(); // theme/màu người dùng chọn — trước khi dựng control
        // Giữ con trỏ chuột luôn hiện khi gõ (tắt "Hide pointer while typing" của Windows trong lúc chạy; trả lại khi thoát).
        Bcode.App.UI.MousePointer.Apply(Bcode.App.UI.UiTemplate.Current.KeepMousePointer);
        void OnPointerSetting() => Bcode.App.UI.MousePointer.Apply(Bcode.App.UI.UiTemplate.Current.KeepMousePointer);
        Bcode.App.UI.UiTemplate.Changed += OnPointerSetting;
        Disposed += (_, _) => { Bcode.App.UI.UiTemplate.Changed -= OnPointerSetting; Bcode.App.UI.MousePointer.Restore(); };
        Bcode.App.UI.UiScale.SetMode(_settings.UiScale, this);
        FileLookupService.SetIgnoreSuffixes(_settings.FileLookupIgnoreSuffixes);
        FileLookupService.SetSearchRules(_settings);
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
        _tableMenu = new TableMenuService(_connections, () => _wcommandTree?.FlatLeaves() ?? new());
        _genAllService.TableData = _tableDataService;   // Note (New) → "Table liên quan": đọc dữ liệu để sinh script
        _genAllService.DataScript = _dataScript;
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
        _viewerControl.SqlScriptRequested += (script, title) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => OpenViewerScriptTab(script, title))); }
            catch (InvalidOperationException) { /* cửa sổ đang đóng */ }
        };
        _viewerControl.SqlScriptForProjectRequested += (script, title, project, sys) =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => OpenProjectScriptTab(script, title, project, sys))); }
            catch (InvalidOperationException) { /* cửa sổ đang đóng */ }
        };
        _viewerControl.Start();
        Disposed += (_, _) => _viewerControl.Dispose();

        Text = "Bcode";
        // Tiêu đề cửa sổ kèm tên dự án (workspace) đang mở → phân biệt được khi mở nhiều Bcode (thanh tác vụ, Alt+Tab).
        _connections.WorkspaceChanged += UpdateWindowTitle;
        // F12 trong khung xem XML: include không có trong source thì tìm tiếp ở Program Path của dự án đang chọn.
        Bcode.App.Controls.ScriptEditorControl.ProjectRoots = () => (_connections.Current?.SourcePath, _connections.Current?.ProgramPath);
        UpdateWindowTitle();
        Width = 1280;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        if (Bcode.App.UI.AppIcons.AppIcon is { } appIcon) Icon = appIcon;

        if (Bcode.App.UI.UiMode.IsNative)
        {
            _topNative = new NativeTopBar();
            _statusNative = new NativeStatusBar();
            _railNative = new IconRailControl();
            _railNative.AddItem("sql_object", Bcode.App.UI.IconGlyph.Table, "SQL Object");
            _railNative.AddItem("wcommand", Bcode.App.UI.IconGlyph.Terminal, "WCommand");
            _railNative.AddItem("mobile", Bcode.App.UI.IconGlyph.Phone, "Mobile");
            _railNative.SelectedKeyChanged += SelectLeftSection;
            _topNative.BarHeightChanged += UpdateHeaderHeight;
            _topNative.Action += a => OnTopBarCommand(a);
            _topNative.ToolClicked += k => OnTopBarCommand("tool", key: k);
            _topNative.ScriptClicked += w => OnTopBarCommand("script", which: w);
            _topNative.WorkspaceSelected += i => OnTopBarCommand("select-ws", index: i);
            _topNative.DatabaseSelected += sys => OnTopBarCommand("select-db", which: sys ? "sys" : "app");
            _statusNative.ProjectWebClicked += () => BeginInvoke(new Action(OpenProjectWeb));
            _statusNative.ProjectMenuRequested += pt => BeginInvoke(new Action(() => ShowProjectMenu(pt)));
        }
        else
        {
            _topBarWeb.Dock = DockStyle.Top;
            _topBarWeb.Height = 46;
            _iconRailWeb.Dock = DockStyle.Left;
            _iconRailWeb.Width = 52;
            _statusBarWeb.Dock = DockStyle.Bottom;
            _statusBarWeb.Height = Bcode.App.UI.UiTemplate.Dens(26); // theo Mật độ của Template giao diện
        }

        _settingsMenu = () => new WebMenu()
            .Add("Choose Server / Workspaces...", OpenConnectionSettings)
            .Add("Tỉ lệ giao diện...", ChooseUiScale)
            .Add("Khôi phục các tab khi mở lại Bcode", ToggleRestoreSession, shortcut: Bcode.App.UI.ShortcutRegistry.Display("app.restoreSession"), @checked: _settings.RestoreSession)
            .Add($"Tự lưu phiên mỗi {_settings.SessionSaveSeconds} giây...", () => BeginInvoke(new Action(ChooseSessionSaveInterval)))
            .Add("Claude/Gemini nhúng vào tab SQL Query (tắt = tab riêng)", () => AppSettings.AiEmbedded = !AppSettings.AiEmbedded, @checked: AppSettings.AiEmbedded)
            .Add("Giao diện (Template)...", () => BeginInvoke(new Action(OpenUiTemplate)))
            .AddSub("Kiểu giao diện (bo góc, mật độ)", sub => { foreach (var pr in Bcode.App.UI.NativeSkin.Presets) { var id = pr.Id; sub.Add(pr.Label, () => Bcode.App.UI.NativeSkin.ApplyPreset(id), @checked: Bcode.App.UI.NativeSkin.IsPreset(id)); } })
            .Add($"Chế độ giao diện: {Bcode.App.UI.UiMode.Label(Bcode.App.UI.UiMode.Current)} — đổi...", () => BeginInvoke(new Action(ChangeUiMode)))
            .Add("Cây menu: bỏ qua file theo tên (like)...", () => BeginInvoke(new Action(EditMenuTreeIgnore)))
            .Add("Pivot SQL Query: quy tắc đoán cấu trúc...", () => BeginInvoke(new Action(EditPivotRules)))
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
        _toolSpecs.Add(("weekly_mail", "Mail kế hoạch tuần", null, (_, _) => OpenWeeklyMailTab()));
        _toolSpecs.Add(("excel_to_frx", "Excel → FRX", null, (_, _) => OpenExcelToFrxTab()));
        _toolSpecs.Add(("check_cfs", "Check LCTT / CĐKT", null, (_, _) => OpenCashFlowCheckTab()));
        _toolSpecs.Add(("bbxn", "Biên bản xác nhận (Word)", null, (_, _) => OpenBbxnTab()));
        _toolSpecs.Add(("fsg_req_report", "Báo cáo yêu cầu FSG", null, (_, _) => OpenFsgRequirementReportTab()));
        _toolSpecs.Add(("check_include", "Check Include", null, (_, _) => OpenIncludeCheckTab()));
        _toolSpecs.Add(("screen_designer", "Thiết kế màn hình", null, (_, _) => LaunchScreenDesigner(null)));
        _toolSpecs.Add(("compare_text", "Compare Text", null, (_, _) => OpenCompareTextTab()));
        _toolSpecs.Add(("string_beauty", "String Beauty", null, (_, _) => OpenStringBeautyTab()));
        _toolSpecs.Add(("query_history", "Lịch sử SQL", "Y", (_, _) => OpenQueryHistoryTab()));
        _toolSpecs.Add(("compare_objects", "So sánh object", "J", (_, _) => OpenCompareObjectsTab()));
        _toolSpecs.Add(("library", "Library...", null, (_, _) => OpenLibrary()));
        _toolSpecs.Add(("decrypt_sql_object", "Decrypt SQL Object", null, (_, _) => OpenDecryptSqlTab()));
        _toolSpecs.Add(("setup_einvoice", "Setup eInvoice (FE)", null, (_, _) => OpenSetupEInvoiceTab()));
        _toolSpecs.Add(("create_rpt_xlsx", "Create *.rpt, *.xlsx", null, (_, _) => OpenCreateRptTab()));
        _toolSpecs.Add(("report_builder", "Tạo báo cáo", null, (_, _) => OpenReportBuilderTab()));        _toolSpecs.Add(("compare_structure", "Compare Structure", null, (_, _) => new CompareStructureForm(_settings).ShowDialog(this)));
        _toolSpecs.Add(("view_rpt_fec", "View Rpt in FEC", null, (_, _) => new ViewRptInFecForm().ShowDialog(this)));
        _toolSpecs.Add(("fsg_crawler", "FSG Yêu cầu", null, (_, _) => OpenFsgCrawlerTab()));
        _toolSpecs.Add(("quick_launch", "FSG FBO", "Ctrl+4", (_, _) => OpenQuickLaunchLogin()));
        _toolSpecs.Add(("sql_profiler", "SQL Profiler", null, (_, _) => OpenSqlProfilerTab()));
        _toolSpecs.Add(("api_config", "Khai báo API", null, (_, _) => { if (RequireLicense("Khai báo API")) new ApiDeclarationForm().ShowDialog(this); }));
        _toolSpecs.Add(("api_schema_builder", "Tạo cấu trúc API", null, (_, _) => { if (RequireLicense("Tạo cấu trúc API")) new ApiSchemaBuilderForm(_sqlObjectService, _tableDataService).ShowDialog(this); }));
        _toolSpecs.Add(("catalog_clone", "Clone danh mục", null, (_, _) => { if (RequireLicense("Clone danh mục")) new CatalogCloneForm(_sqlObjectService, _tableDataService, _connections).ShowDialog(this); }));
        _toolSpecs.Add(("quick_list", "Tạo nhanh danh mục", null, (_, _) => OpenQuickList()));
        _toolSpecs.Add(("bcode_note", "Note (New) nhiều dự án", null, (_, _) => _ = LaunchBcodeNoteAsync()));
        _toolSpecs.Add(("claude_web", "Claude", null, (_, _) => OpenAi(Bcode.Shared.AiSite.Claude, null)));
        _toolSpecs.Add(("gemini_web", "Gemini", null, (_, _) => OpenAi(Bcode.Shared.AiSite.Gemini, null)));
        _toolSpecs.Add(("help", "Trợ giúp", null, (_, _) => OpenHelpTab(HelpTopicForActiveTab())));
        _toolSpecs.Add(("fast_ask", "Hỏi Fast", "Ctrl+Shift+M", (_, _) => OpenFastAskTab()));
        // Đăng ký các nút công cụ vào danh sách phím tắt cấu hình được (phím mặc định = Ctrl+Shift+<chữ> như trước; SQL Profiler = Ctrl+3;
        // Trợ giúp = Ctrl+F1 — không lấy F1 vì F1 đang là "chi tiết dòng" của lưới Table, phím phạm vi App sẽ chặn mất).
        Bcode.App.UI.ShortcutRegistry.SetTools(_toolSpecs.Select(t => (t.key, t.label,
            // 1 ký tự ("Q") = Ctrl+Shift+Q; đã ghi đủ tổ hợp ("Ctrl+4" của FSG FBO) thì giữ nguyên — trước đây thành Ctrl+Shift+4, trùng Note (New).
            t.shortcut is not null ? (t.shortcut.Contains('+') ? t.shortcut : $"Ctrl+Shift+{t.shortcut}") : t.key == "sql_profiler" ? "Ctrl+3" : t.key == "help" ? "Ctrl+F1" : (string?)null)));
        RebuildToolsBar();
        void OnTemplateChanged()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            RebuildToolsBar(); var a = sw.ElapsedMilliseconds;
            PushTopBarLayout(); var b = sw.ElapsedMilliseconds;
            StatusBarControl.Height = Bcode.App.UI.UiTemplate.Dens(26);
            if (Bcode.App.UI.UiMode.IsNative) Invalidate(true);   // control native tự vẽ đọc bo góc/viền/mật độ từ Template → vẽ lại
            ApplyWindowLayout(first: false);
            Bcode.App.UI.ThemeManager.LogTiming($"MainForm.OnTemplateChanged: tổng {sw.ElapsedMilliseconds} ms (RebuildToolsBar {a}, PushTopBarLayout {b - a}, ApplyWindowLayout {sw.ElapsedMilliseconds - b})");
        }
        Bcode.App.UI.UiTemplate.Changed += OnTemplateChanged;
        Disposed += (_, _) => Bcode.App.UI.UiTemplate.Changed -= OnTemplateChanged;
        // Thanh công cụ native: màn hình hẹp thì XUỐNG DÒNG (cao thêm) thay vì giấu bớt nút vào mũi tên ">>".
        _toolsBar.LayoutStyle = ToolStripLayoutStyle.Flow;
        _toolsBar.CanOverflow = false;
        _toolsBar.AutoSize = true;
        _toolsBar.GripStyle = ToolStripGripStyle.Hidden;
        if (_toolsBar.LayoutSettings is FlowLayoutSettings toolsFlow) toolsFlow.WrapContents = true;

        _headerContainer = new Panel { Dock = DockStyle.Top, Height = TopBarControl.Height + _toolsBar.Height };
        _headerContainer.Controls.Add(_toolsBar);
        _headerContainer.Controls.Add(TopBarControl);
        _toolsBar.SizeChanged += (_, _) => UpdateHeaderHeight();
        var headerContainer = _headerContainer;
        // Khu vực của Template giao diện (font/màu riêng từng vùng): thanh trên + thanh công cụ, cây menu bên trái, thanh tab.
        Bcode.App.UI.ThemeManager.SetArea(_headerContainer, "top");
        Bcode.App.UI.ThemeManager.SetArea(_leftContentHost, "tree");

        var sqlObjectTree = new SqlObjectTreeControl(_sqlObjectService) { Dock = DockStyle.Fill };
        sqlObjectTree.ObjectActivated += async (obj, dropCreate) => await OpenObjectDefinitionAsync(obj, dropCreate);
        sqlObjectTree.UsagesRequested += obj => OpenUsagesTab(obj);
        _sqlObjectTree = sqlObjectTree;

        var wcommandTree = new WCommandTreeControl(_wcommandService, _fileLookupService, () => _connections.Current, _settings) { Dock = DockStyle.Fill };
        wcommandTree.NodeActivated += item => OpenWCommandItem(item);
        wcommandTree.NewLookupRequested += item => OpenWCommandItemInNewLookup(item);
        wcommandTree.DesignRequested += item => LaunchScreenDesigner(item.SysId);
        wcommandTree.RequirementsRequested += menu => OpenFsgRequirementReportTab(menu);
        wcommandTree.RunInFboRequested += item => _ = RunMenuInFboAsync(item);
        _wcommandTree = wcommandTree;

        var mobilePanel = new WCommandTreeControl(_wcommandService, _fileLookupService, () => _connections.Current, _settings,
            menuTable: new AppCommandService(_connections, MenuTableSpec.DvCommand),
            tableDir: sysId => MobilePathService.ResolveTables(_connections.Current, sysId)) { Dock = DockStyle.Fill };
        mobilePanel.NodeActivated += item => OpenMobileItem(item, newTab: false);
        mobilePanel.NewLookupRequested += item => OpenMobileItem(item, newTab: true);
        _mobileTree = mobilePanel;

        _leftSections["sql_object"] = sqlObjectTree;
        _leftSections["wcommand"] = wcommandTree;
        _leftSections["mobile"] = mobilePanel;
        _leftContentHost.Controls.Add(sqlObjectTree);
        _leftContentHost.Controls.Add(wcommandTree);
        _leftContentHost.Controls.Add(mobilePanel);
        SelectLeftSection("sql_object");

        var leftContainer = new Panel { Dock = DockStyle.Fill };
        leftContainer.Controls.Add(_leftContentHost);
        leftContainer.Controls.Add(IconRailHost);

        _documentTabs = new Bcode.App.Controls.FlatTabControl { Dock = DockStyle.Fill };
        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(_documentTabs, true, null);

        Bcode.App.UI.ThemeManager.SetArea(_documentTabs, "tabs");
        // Đổi / mở tab: tab cũ bị ẩn nên control đang giữ focus bàn phím (WebView2 của editor cũ) biến mất → phím tắt "bấm không ăn" cho tới khi bấm chuột vào đâu đó
        // (vd thanh Execute). Mỗi lần chọn tab, trả focus về nội dung của tab đó (editor SQL / trang web / control đầu tiên).
        _documentTabs.SelectedIndexChanged += (_, _) =>
        {
            // Nhớ tab vừa rời để Ctrl+Tab chế độ "tab song song" quay lại được (mọi cách đổi tab đều tính: bấm chuột, hộp chọn, Ctrl+Tab).
            if (_curTab is not null && _curTab != _documentTabs.SelectedTab) _prevTab = _curTab;
            _curTab = _documentTabs.SelectedTab;
            if (_curTab is not null) { _tabMru.Remove(_curTab); _tabMru.Insert(0, _curTab); }   // thứ tự dùng gần nhất — đóng tab thì quay về tab trước đó, không nhảy về tab đầu
        };
        _documentTabs.SelectedIndexChanged += (_, _) => BeginInvoke(new Action(() => RestoreContentFocus(activate: false)));
        Bcode.App.UI.ThemeManager.MakeClosable(_documentTabs, CloseDocumentTab);
        _documentTabs.SizeChanged += (_, _) => UpdateQuickAccessOverlayBounds();
        // Tab đổi tên (tab Table lấy tên bảng vừa tải, tab đánh dấu chưa lưu...) thì bề rộng tab đổi theo — phải dời dải "Mở nhanh" (xem OnTabTitleChanged).
        _documentTabs.ControlAdded += (_, e) => { if (e.Control is TabPage added) added.TextChanged += (_, _) => OnTabTitleChanged(); };

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
        Controls.Add(StatusBarControl);
        Controls.Add(headerContainer);
        Load += (_, _) => ApplyWindowLayout(first: true);
        
        // --project <tên>: Bcode được mở từ Ctrl+O của cửa sổ khác (đang có tab) để làm dự án vừa chọn — vào thẳng dự án đó, khỏi hỏi lại.
        var startupProject = StartupArgs.Project is { } sp ? _settings.Workspaces.FindIndex(w => string.Equals(w.Name, sp, StringComparison.OrdinalIgnoreCase)) : -1;
        // Sắp hiện màn Projects: cây SQL Object chờ tới khi đóng màn đó mới nạp (dựng cây cùng lúc làm màn Projects mở chậm).
        _sqlObjectTree.DeferAutoLoad = startupProject < 0;
        if (_settings.Workspaces.Count > 0)
        {
            var last = startupProject >= 0 ? startupProject : _settings.Workspaces.FindIndex(w => w.Name == _settings.LastWorkspace);
            SelectWorkspace(last >= 0 ? last : 0);
        }
        // Màn hình Projects khi mới mở Bcode: lọc/chọn nhanh project đã khai báo (đóng đi thì giữ project dùng gần nhất).
        Shown += (_, _) => BeginInvoke(new Action(() =>
        {
            if (startupProject < 0)
            {
                try { ShowProjectPicker(skipIfSame: true); }
                finally { _sqlObjectTree.ResumeAutoLoad(); }   // đóng màn Projects: giờ mới nạp cây SQL Object (project mới thì SelectWorkspace đã nạp)
            }
            TryRestoreSession();
        }));
        InitSession();
        InitHibernation();
        // Dựng sẵn 1 tab SQL Query ở nền sau khi cửa sổ đã lên hình (xem TakeSqlControl).
        _spareTimer.Tick += (_, _) => PrepareSpareSql();
        Controls.Add(_spareHost);
        Shown += (_, _) => QueueSpareSql(2500);
        Shown += (_, _) => InitFsgNotify();   // Thông báo y/c FSG (chuông 🔔): kéo nền định kỳ — MainForm.FsgNotify.cs
        Disposed += (_, _) => _spareTimer.Dispose();

        if (Bcode.App.UI.UiMode.IsNative) Load += (_, _) => { PushWorkspacesToTopBar(); PushThemeToShell(); PushProjectInfo(); };   // khung native dựng đồng bộ, không phải chờ trang nạp
        else _ = InitShellWebViewsAsync();

        async Task InitShellWebViewsAsync()
        {
            try
            {
                // Ba trang khung chính khởi tạo CÙNG LÚC (trước đây lần lượt, mỗi trang chờ trang trước).
                await Task.WhenAll(
                    Bcode.App.UI.WebViewEnvironment.InitAsync(_topBarWeb),
                    Bcode.App.UI.WebViewEnvironment.InitAsync(_iconRailWeb),
                    Bcode.App.UI.WebViewEnvironment.InitAsync(_statusBarWeb));
                const string host = Bcode.App.UI.WebViewEnvironment.Host;

                _topBarWeb.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var root = doc.RootElement;
                    var action = root.GetProperty("action").GetString() ?? "";
                    if (action == "__height")
                    {
                        // Trang topbar báo chiều cao nội dung thật (px thiết bị) → thanh cao thêm khi hẹp và xuống dòng.
                        _topBarWeb.Height = Math.Clamp(root.GetProperty("height").GetInt32() + 1, Bcode.App.UI.DpiScale.Px(this, 40), Bcode.App.UI.DpiScale.Px(this, 260));
                        UpdateHeaderHeight();
                        return;
                    }
                    OnTopBarCommand(action,
                        key: root.TryGetProperty("key", out var kp) ? kp.GetString() : null,
                        which: root.TryGetProperty("which", out var wp) ? wp.GetString() : null,
                        index: root.TryGetProperty("index", out var ip) && ip.TryGetInt32(out var iv) ? iv : -1);
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
                    PushNotifyCount();
                };
                _iconRailWeb.CoreWebView2.NavigationCompleted += (_, _) => PushThemeToShell();
                _statusBarWeb.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var root = doc.RootElement;
                    switch (root.GetProperty("action").GetString())
                    {
                        case "project-web": BeginInvoke(new Action(OpenProjectWeb)); break;
                        case "status-link":
                            var which = root.TryGetProperty("id", out var lp) ? lp.GetString() : null;
                            if (_statusFile is { } sf) BeginInvoke(new Action(() => { if (which == "folder") ResultExport.OpenFolder(sf); else ResultExport.OpenFile(sf); }));
                            break;
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
                    if (Bcode.App.UI.WebViewEnvironment.TempProfileNote is { } wvNote) PushStatus(wvNote);   // báo lúc tạo môi trường thì thanh trạng thái chưa nạp xong
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

    /// <summary>Lệnh từ thanh trên — dùng chung cho thanh web (topbar.html gửi JSON) và thanh native (NativeTopBar). action: settings | ext | tool | deploy | quickaccess | notify | theme | script | select-ws | select-db | show-actions-menu.</summary>
    private void OnTopBarCommand(string action, string? key = null, string? which = null, int index = -1)
    {
        var bar = TopBarControl;
        switch (action)
        {
            case "settings":
                if (WebMenu.JustDismissed) break; // cú bấm này vừa đóng menu đang mở → coi như "bấm lần nữa để đóng"
                _settingsMenu().Show(bar, 10, bar.Height);
                break;
            case "ext":
                if (WebMenu.JustDismissed) break;
                ShowExtMenu();
                break;
            case "tool":
            {
                // Claude / Gemini / Trợ giúp, hoặc nút thanh công cụ đã kéo lên thanh trên (Template → Bố cục & font).
                var ts = _toolSpecs.FirstOrDefault(t => t.key == key && (TopBarToolKeys.Contains(t.key) || TopToolKeys().Contains(t.key)));
                if (ts.key is not null) { var act = ts.action; BeginInvoke(new Action(() => act(this, EventArgs.Empty))); }
                break;
            }
            case "deploy":
                if (WebMenu.JustDismissed) break; // cú bấm này vừa đóng menu đang mở → coi như "bấm lần nữa để đóng"
                ShowDeployMenu();
                break;
            case "notify":
                // Chuông Thông báo y/c FSG — như Quick Access: không mở form có WebView2 ngay trong handler message của WebView2 thanh trên.
                BeginInvoke(new Action(OpenFsgNotifyPanel));
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
                switch (which)
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
                // Ô chọn ở topbar liệt kê database của project đang vào — index là vị trí trong _topDbEntries.
                if (index >= 0 && index < _topDbEntries.Count) ApplyActiveDatabase(_topDbEntries[index].Sys, _topDbEntries[index].Extra, setOverride: true);
                BeginInvoke(new Action(RestoreContentFocus));
                break;
            case "select-db":
                ApplyActiveDatabase(which == "sys");
                BeginInvoke(new Action(RestoreContentFocus));
                break;
            case "refresh-db":
                _ = RefreshDatabaseAsync();
                BeginInvoke(new Action(RestoreContentFocus));
                break;
            case "actions":
            case "show-actions-menu":
                if (WebMenu.JustDismissed) break; // bấm lần nữa vào nút Actions để đóng menu đang mở
                var actionsMenu = BuildActionsMenu();
                WebMenu.Track(actionsMenu); // tự đóng khi bấm ra ngoài (kể cả trên WebView2)
                actionsMenu.Show(bar, 100, bar.Height); // Tọa độ x=100 tạm tính để thả xuống đúng chỗ nút Actions
                break;
        }
    }

    /// <summary>Đổi chế độ App/Web: lưu lựa chọn rồi hỏi mở lại Bcode (chế độ chỉ áp lúc khởi động).</summary>
    private void ChangeUiMode()
    {
        using var dlg = new UiModeChooserForm(Bcode.App.UI.UiMode.Current, AppSettings.AskUiMode);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        AppSettings.AskUiMode = dlg.AskNextTime;
        if (dlg.Chosen == Bcode.App.UI.UiMode.Current) return;
        AppSettings.SavedUiMode = dlg.Chosen;
        if (MessageBox.Show(this, "Đã lưu chế độ " + Bcode.App.UI.UiMode.Label(dlg.Chosen) + ". Mở lại Bcode bây giờ để áp dụng?\n(Tab đang mở sẽ được lưu theo phiên nếu bạn bật khôi phục phiên.)", "Bcode",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, Bcode.App.UI.UiMode.Arg(dlg.Chosen)) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory }); Close(); }
        catch (Exception ex) { MessageBox.Show(this, "Không mở lại được Bcode:\n" + ex.Message, "Bcode"); }
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
        // Mật khẩu mã hoá DPAPI bằng tài khoản / máy khác (settings.json chép sang) → báo ngay, khỏi phải đoán lỗi "Login failed".
        var pwNote = ws.PasswordUnreadable ? "  |  ⚠ Mật khẩu đã lưu được mã hoá trên máy / tài khoản Windows khác — nhập lại ở Edit Project hoặc Ctrl+F5 đồng bộ lại" : "";
        PushStatus($"Workspace: {ws.Name}  —  Server: {ws.Server}  |  Dev: HàoTN|PhongNT | Tester: ThinhBM| KhanhNN{pwNote}");

        var swSwitch = System.Diagnostics.Stopwatch.StartNew();
        var syncBefore = swSwitch.ElapsedMilliseconds;
        _ = TimedAsync("WCommandTree.ReloadAsync", () => _wcommandTree.ReloadAsync());
        if (_mobileTree.Visible) _ = _mobileTree.ReloadAsync();   // mục Mobile đang mở: nạp dvcommand của project mới (ẩn thì nạp khi bấm vào mục)
        var tWc = swSwitch.ElapsedMilliseconds;
        _sqlObjectTree.ResetForWorkspace(); // đổi project → danh sách SQL Object nạp lại (từ cache của project mới)
        var tSql = swSwitch.ElapsedMilliseconds;

        if (_fileLookupControl is not null && !string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            _fileLookupControl.ProjectName = ws.Name;
            _fileLookupControl.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"), load: false);   // vào / đổi project: không tự quét — người dùng bấm menu (hoặc Load) mới chạy
        }

        if (_fileReferenceTabPage is not null && !string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            var frc = _fileReferenceTabPage.Controls.OfType<FileReferenceControl>().FirstOrDefault();
            frc?.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"));
        }
        Bcode.App.UI.ThemeManager.LogTiming($"Đổi project → {ws.Name}: phần đồng bộ trên UI {swSwitch.ElapsedMilliseconds} ms (WCommand bắt đầu {tWc - syncBefore}, SQL Object bắt đầu {tSql - tWc}, File Lookup/Reference {swSwitch.ElapsedMilliseconds - tSql})");
    }

    /// <summary>Nút ⟳ cạnh WS (phím db.refresh): nạp lại dự án hiện tại từ database — bỏ cache danh sách SQL Object (App + Sys), dữ liệu gợi ý code
    /// (bảng / cột / chữ ký procedure) và danh sách tên bảng của các tab Table đang mở, rồi nạp lại cây SQL Object ngay.</summary>
    private async Task RefreshDatabaseAsync()
    {
        if (_connections.Current is not { } ws) { PushStatus("Chưa chọn workspace — chọn project trước khi Refresh database."); return; }
        var hints = new SqlHintService(_connections);
        hints.Forget(false);
        hints.Forget(true);
        SqlHintEngine.Shared.Invalidate();
        _tableMenu.Forget();
        foreach (TabPage p in _documentTabs.TabPages)
        {
            foreach (var sql in p.Controls.OfType<ISqlTab>()) if (!sql.IsDisposed) sql.RefreshSchema();
            foreach (var table in p.Controls.OfType<TableEditControl>()) table.ResetTableNamesCache();
        }
        PushStatus($"Refresh database: đang nạp lại danh sách object của {ws.Name}...");
        try
        {
            var count = await _sqlObjectTree.RefreshFromDatabaseAsync();
            PushStatus(count >= 0
                ? $"Refresh database: đã nạp lại {count} object — {ws.Name}."
                : $"Refresh database: đã xoá cache của {ws.Name} — danh sách object sẽ nạp mới khi mở mục SQL Object.");
        }
        catch (Exception ex) { PushStatus("Refresh database: không nạp lại được từ database (" + ex.Message + ") — giữ danh sách cũ."); }
    }

    /// <summary>Chạy một việc nền và ghi thời gian xong vào log (để biết bước nào làm đổi project lâu).</summary>
    private static async Task TimedAsync(string what, Func<Task> work)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try { await work(); }
        finally { Bcode.App.UI.ThemeManager.LogTiming($"  {what}: xong sau {sw.ElapsedMilliseconds} ms"); }
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
    /// <param name="newWindowIfTabs">Ctrl+O / Choose Server: cửa sổ này đang có tab mà chọn dự án KHÁC → mở Bcode mới cho dự án đó, cửa sổ này giữ nguyên
    /// dự án + các tab đang làm. Không có tab (hoặc chọn lại đúng dự án đang vào) thì đổi dự án ngay tại cửa sổ này như trước.</param>
    /// <param name="skipIfSame">Màn Projects lúc mới mở Bcode: Bcode đã tự vào project dùng gần nhất — chọn lại đúng project đó thì KHÔNG nạp lại
    /// (trước đây cây WCommand + SQL Object bị nạp 2 lần mỗi lần mở Bcode, xem apply-timing.log).</param>
    private void ShowProjectPicker(bool newWindowIfTabs = false, bool skipIfSame = false)
    {
        if (IsDisposed) return;
        using IProjectPicker picker = Bcode.App.UI.UiMode.IsNative ? new NativeProjectPickerForm(_settings, _connections) : new ProjectPickerForm(_settings, _connections);
        string Snapshot(Workspace? w) { try { return w is null ? "" : System.Text.Json.JsonSerializer.Serialize(w); } catch { return Guid.NewGuid().ToString(); } }
        var before = skipIfSame ? Snapshot(_connections.Current) : "";
        var result = picker.ShowDialog(this);
        PushWorkspacesToTopBar(); // New/Edit/Delete trong màn hình có thể đã đổi danh sách
        if (result == DialogResult.OK && picker.Chosen is { } chosen)
        {
            if (newWindowIfTabs && _documentTabs.TabPages.Count > 0
                && !string.Equals(chosen.Name, _connections.Current?.Name, StringComparison.OrdinalIgnoreCase))
            {
                StartNewBcodeWindow(chosen.Name);
                return;
            }
            // Edit chính project này trong màn Projects (đổi server, database...) thì vẫn nạp lại.
            if (skipIfSame && ReferenceEquals(chosen, _connections.Current) && Snapshot(chosen) == before) return;
            var idx = _settings.Workspaces.IndexOf(chosen);
            if (idx >= 0) SelectWorkspace(idx);
        }
        // Không chọn gì: PushWorkspacesToTopBar ở trên đã đồng bộ lại ô database ở topbar, không nạp lại workspace.
    }

    /// <summary>Ctrl+F5 ở ngoài màn hình Projects: dùng CHUNG một form với Ctrl+F5 trong Projects — Edit Project mở sẵn hộp Synchronize
    /// (thêm 1 mã dự án / chỉ sync mới / overwrite tất cả), mã dự án điền sẵn theo project đang vào.</summary>
    private void ShowSynchronizeProjects()
    {
        if (IsDisposed) return;
        using var form = new ConnectionSettingsForm(_settings, _connections, allowSync: true, select: _connections.Current, startNew: false, autoSync: true);
        var result = form.ShowDialog(this);
        if (result != DialogResult.OK) _settings.Workspaces = AppSettings.Load().Workspaces;   // đóng không Save: bỏ thay đổi chưa lưu
        PushWorkspacesToTopBar();
    }

    /// <summary>Choose Server (Ctrl+O, File/Actions &gt; Choose Server): mở ĐÚNG màn hình Projects như lúc vừa vào Bcode — danh sách
    /// project có ô lọc + Last Access; New/Edit/Delete và Synchronize (Ctrl+F5) đều nằm trong đó (xem ProjectPickerForm).</summary>
    private void OpenConnectionSettings() => ShowProjectPicker(newWindowIfTabs: true);

    /// <summary>Mở thêm 1 cửa sổ Bcode chạy song song (cùng chế độ App/Web). <paramref name="project"/> != null → vào thẳng dự án đó (tham số --project).</summary>
    private void StartNewBcodeWindow(string? project = null)
    {
        try
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            psi.ArgumentList.Add(Bcode.App.UI.UiMode.Arg(Bcode.App.UI.UiMode.Current));
            if (!string.IsNullOrWhiteSpace(project)) { psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(project); }
            Process.Start(psi);
            if (project is not null) PushStatus($"Đang mở Bcode mới cho dự án {project} — cửa sổ này giữ nguyên dự án và các tab đang mở.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được Bcode mới:\n" + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void PushDbNamesToTopBar(Bcode.App.Models.Workspace ws)
    {
        if (_topNative is not null) { _topNative.SetDbNames(ws.SysDatabase, ws.EffectiveAppDatabase); return; }
        if (_topBarWeb.CoreWebView2 is null) return;
        var sysArg = System.Text.Json.JsonSerializer.Serialize(ws.SysDatabase);
        var appArg = System.Text.Json.JsonSerializer.Serialize(ws.EffectiveAppDatabase);
        _ = _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setDbNames && window.setDbNames({sysArg}, {appArg})");
    }
    // Ô chọn ở topbar KHÔNG còn là danh sách project: chỉ liệt kê 2 database (App, Sys) của project đang vào, để chuyển nhanh
    // giữa chúng khi chạy SQL Query. Muốn sang project khác thì dùng Choose Server / Ctrl+O.
    private bool _topDbSys;
    private readonly List<(bool Sys, string? Extra)> _topDbEntries = new(); // theo thứ tự các mục trong ô chọn: Sys / App / database phụ trong DB Access


    /// <summary>Nút thanh công cụ đang nằm trên thanh trên (Template → kéo từ "Thanh công cụ" lên "Thanh trên").</summary>
    /// <summary>Id các nút có sẵn của thanh trên (add/view/.../deploy/ext/claude/gemini/help) đã kéo xuống thanh công cụ hoặc khung Ẩn
    /// (token "top:id" trong ToolOrder / HiddenToolKeys — Template → Bố cục & font).</summary>
    private HashSet<string> TopMovedDown()
    {
        var p = Bcode.App.UI.UiTemplate.TopDownPrefix;
        return new HashSet<string>(_settings.ToolOrder.Concat(_settings.HiddenToolKeys)
            .Where(k => k.StartsWith(p, StringComparison.Ordinal)).Select(k => k[p.Length..]));
    }

    /// <summary>Nút "giả" trên thanh công cụ cho 1 nút có sẵn của thanh trên đã kéo xuống: cùng chữ, bấm = đúng lệnh của nút đó trên thanh trên.</summary>
    private (string key, string label, string? shortcut, EventHandler action)? TopDownSpec(string token)
    {
        var id = token[Bcode.App.UI.UiTemplate.TopDownPrefix.Length..];
        var top = Bcode.App.UI.UiTemplate.TopButtons.FirstOrDefault(b => b.Id == id);
        if (top.Id is null) return null;
        var label = top.Text.Replace(" ▾", "").Trim();
        EventHandler act = id switch
        {
            "add" or "view" or "clear" or "save" or "copy" => (_, _) => OnTopBarCommand("script", which: id),
            "deploy" => (_, _) => ShowDeployMenu(),
            "ext" => (_, _) => ShowExtMenu(),
            _ => (_, _) =>
            {
                var key = id switch { "claude" => "claude_web", "gemini" => "gemini_web", _ => id };
                var spec = _toolSpecs.FirstOrDefault(t => t.key == key);
                spec.action?.Invoke(this, EventArgs.Empty);
            },
        };
        return (token, id is "deploy" or "ext" ? label + " ▾" : label, null, act);
    }

    private HashSet<string> TopToolKeys()
    {
        var bar = new HashSet<string>(BarToolKeys());
        return Bcode.App.UI.UiTemplate.Current.TopToolKeys(bar.Contains);
    }

    /// <summary>Đẩy thứ tự + chữ + font/màu các nút thanh trên (nút Script, Triển khai, Tool mở rộng, Claude, Gemini, Trợ giúp
    /// và nút thanh công cụ đã kéo lên — template giao diện) xuống thanh trên.</summary>
    private void PushTopBarLayout()
    {
        var t = Bcode.App.UI.UiTemplate.Current;
        var bar = new HashSet<string>(BarToolKeys());
        var movedDown = TopMovedDown();
        var order = t.TopOrder(bar.Contains, movedDown);
        // Nút có sẵn: style ở "script:id"; nút công cụ: id đã là "tool:key" — cùng khoá style với nút đó trên thanh công cụ.
        Bcode.App.UI.ControlStyle? StyleOf(string id) =>
            t.Items.TryGetValue(id.StartsWith(Bcode.App.UI.UiTemplate.TopToolPrefix) ? id : "script:" + id, out var st) ? st : null;
        (string Label, string Tip)? ToolOf(string id)
        {
            if (!id.StartsWith(Bcode.App.UI.UiTemplate.TopToolPrefix)) return null;
            var key = id[Bcode.App.UI.UiTemplate.TopToolPrefix.Length..];
            var spec = _toolSpecs.FirstOrDefault(x => x.key == key);
            if (spec.key is null) return null;
            var combo = Bcode.App.UI.ShortcutRegistry.Display("tool:" + key);
            return (spec.label, combo.Length > 0 ? $"{spec.label} ({combo})" : spec.label);
        }
        if (_topNative is not null)
        {
            _topNative.SetScriptLayout(order, StyleOf, ToolOf, movedDown);
            return;
        }
        if (_topBarWeb.CoreWebView2 is null) return;
        var items = new Dictionary<string, object>();
        foreach (var id in order)
        {
            var st = StyleOf(id);
            var tool = ToolOf(id);
            items[id] = new { text = string.IsNullOrEmpty(st?.Text) ? null : st.Text, css = Bcode.App.UI.UiTemplate.ToInlineCss(st), label = tool?.Label, tip = tool?.Tip };
        }
        var json = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Serialize(new { order, items, hidden = movedDown }));
        _ = _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.applyLayout && window.applyLayout({json})");
    }
    private void PushWorkspacesToTopBar()
    {
        if (_topNative is null && _topBarWeb.CoreWebView2 is null) return;
        _topDbEntries.Clear();
        var names = new List<string>();
        if (_connections.Current is { } ws)
        {
            if (!string.IsNullOrWhiteSpace(ws.AppDatabase)) { names.Add($"{ws.AppDatabase}  (App)"); _topDbEntries.Add((false, null)); }
            if (!string.IsNullOrWhiteSpace(ws.SysDatabase)) { names.Add($"{ws.SysDatabase}  (Sys)"); _topDbEntries.Add((true, null)); }
            // Ô WS chỉ ghi tên database (+ App/Sys/DB) — không nối mã dự án: vào dự án nào thì đã biết database của dự án đó.
            // Các database khai trong "DB Access" (Proxy {mã_dự_án}_eInv, _C...) ngoài App/Sys — chọn để làm việc trên database đó.
            foreach (var extra in ws.AccessDatabases())
            {
                if (extra.Equals(ws.AppDatabase, StringComparison.OrdinalIgnoreCase) || extra.Equals(ws.SysDatabase, StringComparison.OrdinalIgnoreCase)) continue;
                names.Add($"{extra}  (DB)"); _topDbEntries.Add((false, extra));
            }
        }
        var over = _connections.Current?.ActiveAppDatabaseOverride;
        var sel = _topDbEntries.FindIndex(en => en.Sys == _topDbSys && (_topDbSys || string.Equals(en.Extra, string.IsNullOrWhiteSpace(over) ? null : over, StringComparison.OrdinalIgnoreCase)));
        if (sel < 0) sel = 0;
        if (_topNative is not null)
        {
            _topNative.SetWorkspaces(names, sel, _topDbSys);
            if (_connections.Current is { } cw) _topNative.SetDbNames(cw.SysDatabase, cw.EffectiveAppDatabase);
            PushTopBarLayout();
            return;
        }
        var arg = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonSerializer.Serialize(names.ToArray()));
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
        if (_documentTabs.SelectedTab?.Controls.OfType<ISqlTab>().FirstOrDefault() is { } sql)
            sql.SetDatabase(useSys);
        else if (_documentTabs.SelectedTab?.Controls.OfType<TableEditControl>().FirstOrDefault() is { } table)
            table.SetDatabase(useSys);   // tab Table: đổi luôn ô "DB:" của tab
        if (_connections.Current is { } ws)
            PushStatus($"Database: {(useSys ? ws.SysDatabase : ws.EffectiveAppDatabase)} ({(useSys ? "Sys" : "App")})  —  Project: {ws.Name}");
    }

    /// <summary>Ctrl+1 / menu Switch Database: đảo App ↔ Sys. Đang ở tab Table thì đảo theo ô "DB:" của tab (có thể đã tự chuyển khác topbar).</summary>
    private void ToggleActiveDatabase()
    {
        var table = _documentTabs.SelectedTab?.Controls.OfType<TableEditControl>().FirstOrDefault();
        ApplyActiveDatabase(!(table?.UseSysDatabase ?? _topDbSys));
    }

    private void PushThemeToShell()
    {
        if (Bcode.App.UI.UiMode.IsNative) { Invalidate(true); return; }   // control native tự đọc AppColors khi vẽ
        var isDark = Bcode.App.UI.AppColors.IsDark ? "true" : "false";
        foreach (var web in new[] { _topBarWeb, _iconRailWeb, _statusBarWeb })
            if (web.CoreWebView2 is not null)
                _ = web.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
    }

    private string? _statusFile;   // file vừa lưu (Save as CSV / Excel) — link "Mở file / Mở thư mục" trên thanh trạng thái; dòng trạng thái mới thì bỏ link

    /// <param name="file">Có giá trị = sau chữ trạng thái hiện link "Mở file" / "Mở thư mục" cho file này.</param>
    private void PushStatus(string text, string? file = null)
    {
        _statusFile = file;
        if (_statusNative is not null)
        {
            _statusNative.SetStatus(text, file is null ? null : new (string, Action)[] { ("Mở file", () => ResultExport.OpenFile(file)), ("Mở thư mục", () => ResultExport.OpenFolder(file)) });
            PushProjectInfo();
            return;
        }
        if (_statusBarWeb.CoreWebView2 is null) return;
        var arg = System.Text.Json.JsonSerializer.Serialize(text);
        var links = file is null ? "null" : "[{\"id\":\"file\",\"label\":\"Mở file\"},{\"id\":\"folder\",\"label\":\"Mở thư mục\"}]";
        _ = _statusBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setStatus && window.setStatus({arg}, {links})");
        PushProjectInfo(); // đổi workspace/database thì thông tin project ở góc phải cũng đổi theo
    }

    // ---- Góc phải thanh trạng thái: link web đăng nhập, menu project, tên máy ----

    private void PushProjectInfo()
    {
        if (_statusNative is not null) { _statusNative.SetProject(_connections.Current?.LoginWLink?.Trim() ?? "", Environment.MachineName); return; }
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
        var scale = _statusBarWeb.DeviceDpi / 96.0 * Bcode.App.UI.UiScale.Factor;
        ShowProjectMenu(_statusBarWeb.PointToScreen(new Point((int)Math.Round(cssX * scale), (int)Math.Round(cssY * scale))));
    }

    /// <summary>Menu "▾ Project" của thanh trạng thái, thả tại toạ độ màn hình <paramref name="screen"/>.</summary>
    private void ShowProjectMenu(Point screen)
    {
        if (_connections.Current is not { } ws) { MessageBox.Show(this, "Chưa chọn workspace (WS).", "Bcode"); return; }
        new WebMenu()
            .Add($"Login WLink: {ws.LoginWLink}", OpenProjectWeb, enabled: !string.IsNullOrWhiteSpace(ws.LoginWLink))
            .Add($"Program: {ws.ProgramPath}", () => OpenProjectFolder(ws.ProgramPath), enabled: !string.IsNullOrWhiteSpace(ws.ProgramPath))
            .Add($"Source: {ws.SourcePath}", () => OpenProjectFolder(ws.SourcePath), enabled: !string.IsNullOrWhiteSpace(ws.SourcePath))
            .Add($"Working: {ws.WorkingPath}", () => OpenProjectFolder(ws.WorkingPath), enabled: !string.IsNullOrWhiteSpace(ws.WorkingPath))
            .Add($"Mobile: {ws.MobilePath}", () => OpenProjectFolder(ws.MobilePath), enabled: !string.IsNullOrWhiteSpace(ws.MobilePath))
            .AddSeparator()
            .Add("Copy Project Info", () => CopyProjectInfo(ws))
            .Show(StatusBarControl, screen);
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
        if (key == "mobile") _ = _mobileTree.ReloadAsync();
        if (key == "sql_object") _ = _sqlObjectTree.EnsureLoadedAsync(); // vào mục SQL Object: nạp sẵn (từ cache), chỉ nạp thêm khi có object mới/đổi
        _railNative?.Select(key);
        if (_iconRailWeb.CoreWebView2 is not null)
        {
            var arg = System.Text.Json.JsonSerializer.Serialize(key);
            _ = _iconRailWeb.CoreWebView2.ExecuteScriptAsync($"window.setSelected && window.setSelected({arg})");
        }
    }

    /// <summary>Vùng chức năng của các nút công cụ: giữa hai vùng liền nhau trên thanh có vạch ngăn "|". Nút chưa khai vùng (nút mới) nằm vùng cuối.</summary>
    private static readonly string[][] ToolGroups =
    {
        new[] { "sql_query", "lookup", "table", "command", "query_history", "sql_profiler" },                                   // truy vấn SQL
        new[] { "wcommand", "file_lookup", "file_reference", "change_owner", "compare_objects", "compare_structure", "decrypt_sql_object" },   // đối tượng / cấu hình Fast
        new[] { "gen_update_package", "gen_update", "create_rpt_xlsx", "excel_to_frx", "view_rpt_fec", "create_processing", "catalog_clone" },   // gói cập nhật, báo cáo, danh mục
        new[] { "note", "note_new", "compare_text", "string_beauty" },                                                          // văn bản, ghi chú
        new[] { "check_mail", "setup_einvoice", "fsg_crawler", "quick_launch", "library" },                                     // dự án / FSG / tiện ích khác
    };
    private static int ToolGroupOf(string key)
    {
        for (var i = 0; i < ToolGroups.Length; i++) if (Array.IndexOf(ToolGroups[i], key) >= 0) return i;
        return ToolGroups.Length;
    }

    private void RebuildToolsBar()
    {
        _toolsBar.Items.Clear();
        _toolsBar.Padding = new Padding(4, Bcode.App.UI.UiTemplate.Dens(2), 4, Bcode.App.UI.UiTemplate.Dens(2));

        var prevGroup = -1;
        var groups = ActiveToolGroups();
        var grouped = new HashSet<string>(groups.SelectMany(g => g.Keys));
        var onTop = TopToolKeys();   // đã kéo lên thanh trên (Template): không hiện lại ở đây; nhóm Quick Access giữ nguyên
        foreach (var (key, label, shortcut, action) in OrderedToolSpecs())
        {
            if (_settings.HiddenToolKeys.Contains(key) || grouped.Contains(key) || onTop.Contains(key)) continue;   // đã vào nhóm: chỉ hiện trong menu của nhóm
            var grp = ToolGroupOf(key);
            if (prevGroup >= 0 && grp != prevGroup) _toolsBar.Items.Add(new ToolStripSeparator { Margin = new Padding(6, 2, 6, 2) });   // vạch | giữa hai vùng chức năng
            prevGroup = grp;
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
            var styleKey = key.StartsWith(Bcode.App.UI.UiTemplate.TopDownPrefix, StringComparison.Ordinal) ? "script:" + key[Bcode.App.UI.UiTemplate.TopDownPrefix.Length..] : "tool:" + key;
            if (Bcode.App.UI.UiTemplate.Current.Items.TryGetValue(styleKey, out var itemStyle)) ApplyItemStyle(button, itemStyle);
            _toolsBar.Items.Add(button);
        }

        if (groups.Count > 0) AddToolGroupButtons(groups, prevGroup >= 0);
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
        var all = _toolSpecs.Where(t => !IsMenuTool(t.key)).ToList();
        foreach (var tok in _settings.ToolOrder.Where(k => k.StartsWith(Bcode.App.UI.UiTemplate.TopDownPrefix, StringComparison.Ordinal)).Distinct())
            if (TopDownSpec(tok) is { } td) all.Add(td);   // nút có sẵn của thanh trên đã kéo xuống thanh công cụ (Template)
        if (_settings.ToolOrder.Count == 0) return all.OrderBy(t => ToolGroupOf(t.key)).ToList();      // OrderBy ổn định: trong vùng giữ thứ tự khai báo
        var byKey = all.ToDictionary(t => t.key);
        var ordered = new List<(string key, string label, string? shortcut, EventHandler action)>();
        foreach (var key in _settings.ToolOrder)
            if (byKey.Remove(key, out var spec)) ordered.Add(spec);
        ordered.AddRange(all.Where(t => byKey.ContainsKey(t.key))); // nút mới chưa có trong thứ tự đã lưu
        return ordered;
    }

    // ---- Menu "Triển khai" ----------------------------------------------------------------------------
    // Các công cụ phục vụ triển khai gom vào 1 nút menu trên thanh công cụ, KHÔNG hiện riêng ở thanh công cụ / Quick Access / Template thứ tự nút.
    // Phím tắt riêng của từng công cụ ("tool:<key>") vẫn cấu hình được ở Template → Phím tắt.
    private static readonly string[] DeployToolKeys = { "check_cfs", "bbxn", "screen_designer", "api_config", "api_schema_builder", "fsg_req_report", "weekly_mail" };

    // Menu "Tool mở rộng" (cạnh "Triển khai"): các công cụ phụ, cũng không hiện riêng ở thanh công cụ / Quick Access.
    private static readonly string[] ExtToolKeys = { "check_include", "report_builder", "quick_list", "bcode_note" };

    // Claude / Gemini / Trợ giúp: nút riêng ngay trên thanh trên, cùng hàng với "Tool mở rộng" (xem topbar.html) — không hiện ở thanh công cụ.
    private static readonly string[] TopBarToolKeys = { "claude_web", "gemini_web", "help" };
    private static bool IsMenuTool(string key) => DeployToolKeys.Contains(key) || ExtToolKeys.Contains(key) || TopBarToolKeys.Contains(key);

    /// <summary>Khoá các nút mặc định của thanh công cụ (không gồm các công cụ trong menu Triển khai).</summary>
    private List<string> BarToolKeys() => _toolSpecs.Where(t => !IsMenuTool(t.key)).Select(t => t.key).ToList();

    private void ShowDeployMenu() => ShowToolMenu("Triển khai", DeployToolKeys);

    private void ShowExtMenu() => ShowToolMenu("Tool mở rộng", ExtToolKeys);

    private void ShowToolMenu(string caption, string[] keys)
    {
        var menu = new WebMenu().AddCaption(caption);
        foreach (var key in keys)
        {
            var spec = _toolSpecs.FirstOrDefault(t => t.key == key);
            if (spec.key is null) continue;
            var combo = Bcode.App.UI.ShortcutRegistry.Display("tool:" + key);
            var handler = spec.action;
            menu.Add(spec.label, () => handler(this, EventArgs.Empty), shortcut: combo.Length > 0 ? combo : null);
        }
        menu.Show(this, Cursor.Position);   // nút "Triển khai ▾" nằm ở thanh trên, cạnh Copy Script (xem topbar.html)
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
            if (d.Id is "app.palette" || d.Id.StartsWith("tab.goto", StringComparison.Ordinal) || d.Id.StartsWith("bar.group", StringComparison.Ordinal)) continue;
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
                var sqlHere = _documentTabs.SelectedTab?.Controls.OfType<ISqlTab>().FirstOrDefault();
                if (sqlHere is null) { OpenFreeScriptTab(); sqlHere = _documentTabs.SelectedTab?.Controls.OfType<ISqlTab>().FirstOrDefault(); }
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

    private TabPage? _helpTab;
    private HelpControl? _helpControl;
    private TabPage? _fastAskTab;
    private FastAskService? _fastAskService;

    /// <summary>Tab "Hỏi Fast" (một tab duy nhất): chat tra kiến thức Fast không qua AI — mẫu code, ma trận ghost text, store / bảng của database đang kết nối,
    /// từ điển field, luật danh mục, quy ước Fast.</summary>
    private void OpenFastAskTab()
    {
        // đang ở tab SQL Query + bật "Claude/Gemini nhúng vào tab SQL Query" → nhúng Hỏi Fast bên phải tab đó như Claude / Gemini
        if (AppSettings.AiEmbedded && _documentTabs.SelectedTab?.Controls.OfType<ISqlTab>().FirstOrDefault() is { } host) { EmbedFastAsk(host); return; }
        if (_fastAskTab is not null && _documentTabs.TabPages.Contains(_fastAskTab)) { _documentTabs.SelectedTab = _fastAskTab; return; }
        _fastAskService ??= new FastAskService(_connections, _tableDataService);
        var control = new FastAskControl(_fastAskService);
        control.InsertRequested += InsertFromFastAsk;
        control.OpenObjectRequested += (name, sys, type) => _ = OpenObjectDefinitionAsync(new SqlObjectInfo
        {
            Name = name, FromSysDatabase = sys, Kind = type == "P" ? SqlObjectKind.StoredProcedure : SqlObjectKind.Function,
        });
        control.QuickListRequested += (name, content) => OpenQuickListWithAssist(name, content, "rules");
        _fastAskTab = AddDocumentTab("Hỏi Fast", control);
        _fastAskTab.Disposed += (_, _) => _fastAskTab = null;
    }

    /// <summary>Khung Hỏi Fast nhúng của từng tab SQL Query (mỗi tab 1 khung, giữ cuộc hỏi khi chuyển qua Claude / Gemini rồi quay lại).</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISqlTab, FastAskControl> _embeddedFastAsk = new();

    /// <summary>Hiện Hỏi Fast nhúng bên phải tab SQL Query <paramref name="host"/>; "Chèn vào SQL Query" chèn thẳng vào tab đó.</summary>
    private void EmbedFastAsk(ISqlTab host)
    {
        if (!_embeddedFastAsk.TryGetValue(host, out var control))
        {
            _fastAskService ??= new FastAskService(_connections, _tableDataService);
            control = new FastAskControl(_fastAskService);
            control.InsertRequested += (text, newTab) =>
            {
                if (newTab || host.IsDisposed) { _ = OpenFreeScriptTab().OpenScriptAsync(text); return; }
                _ = host.InsertSnippetAsync(text);
                host.FocusEditor();
            };
            control.OpenObjectRequested += (name, sys, type) => _ = OpenObjectDefinitionAsync(new SqlObjectInfo
            {
                Name = name, FromSysDatabase = sys, Kind = type == "P" ? SqlObjectKind.StoredProcedure : SqlObjectKind.Function,
            });
            control.QuickListRequested += (name, content) => OpenQuickListWithAssist(name, content, "rules");
            _embeddedFastAsk.Add(host, control);
        }
        host.ShowFastAsk(control);
    }

    /// <summary>"Chèn vào SQL Query" ở Hỏi Fast: chèn tại con trỏ của tab SQL Query dùng gần nhất (chưa có thì mở tab mới); "Mở tab SQL mới" = luôn tab mới.</summary>
    private void InsertFromFastAsk(string text, bool newTab)
    {
        var page = newTab ? null : _tabMru.FirstOrDefault(p => _documentTabs.TabPages.Contains(p) && p.Controls.OfType<ISqlTab>().Any(t => !t.IsDisposed));
        if (page?.Controls.OfType<ISqlTab>().First() is not { } tab)
        {
            _ = OpenFreeScriptTab().OpenScriptAsync(text);
            return;
        }
        _documentTabs.SelectedTab = page;
        _ = tab.InsertSnippetAsync(text);
        tab.FocusEditor();
    }

    /// <summary>Tab "Trợ giúp" (một tab duy nhất): cây tính năng + hướng dẫn sử dụng, mở sẵn ở mục <paramref name="topic"/> (id trong Help\HuongDanSuDung.md).</summary>
    private void OpenHelpTab(string? topic)
    {
        if (_helpTab is not null && _helpControl is not null && _documentTabs.TabPages.Contains(_helpTab))
        {
            _documentTabs.SelectedTab = _helpTab;
            if (topic is not null) _helpControl.ShowTopic(topic);
            return;
        }
        _helpControl = new HelpControl(topic);
        _helpTab = AddDocumentTab("Trợ giúp", _helpControl);
        _helpTab.Disposed += (_, _) => { _helpTab = null; _helpControl = null; };
    }

    // Loại control của tab đang mở → id mục hướng dẫn tương ứng (Trợ giúp / Ctrl+F1 mở đúng tính năng đang dùng). Khớp các dòng <a id="..."> trong docs/HuongDanSuDung.md.
    private static readonly Dictionary<string, string> HelpTopicByControl = new(StringComparer.Ordinal)
    {
        ["RawSqlControl"] = "sql-query", ["NativeSqlControl"] = "sql-query",
        ["TableEditControl"] = "table",
        ["SqlQueryControl"] = "command-lookup", ["LookupControl"] = "command-lookup", ["NativeLookupControl"] = "command-lookup",
        ["FileLookupControl"] = "file-lookup",
        ["FastAskControl"] = "hoi-fast",
        ["GenUpdatePackageControl"] = "gen-update",
        ["NoteControl"] = "note", ["AdvanceNoteControl"] = "note", ["NativeAdvanceNoteControl"] = "note",
        ["QueryHistoryControl"] = "lich-su-sql", ["NativeQueryHistoryControl"] = "lich-su-sql",
        ["CreateRptControl"] = "create-rpt", ["QuickReportControl"] = "tool-mo-rong",
        ["CompareObjectsControl"] = "sql-object", ["NativeCompareObjectsControl"] = "sql-object", ["UsagesControl"] = "sql-object",
        ["DecryptSqlObjectControl"] = "sql-object", ["SqlProfilerControl"] = "sql-object",
        ["FileReferenceControl"] = "cap-source", ["IncludeCheckControl"] = "tool-mo-rong",
        ["CashFlowCheckControl"] = "trien-khai", ["BbxnControl"] = "trien-khai", ["FsgRequirementReportControl"] = "trien-khai",
        ["NativeWeeklyMailControl"] = "trien-khai",
        ["SetupEInvoiceControl"] = "tien-ich", ["CheckMailControl"] = "tien-ich", ["NativeCheckMailControl"] = "tien-ich",
        ["StringBeautyControl"] = "tien-ich", ["NativeStringBeautyControl"] = "tien-ich",
        ["SqlHintsHelpControl"] = "sql-query",
    };

    /// <summary>Mục hướng dẫn theo tab đang mở (tìm control đã biết trong vài lớp con đầu của tab); không khớp → null (mục đầu).</summary>
    private string? HelpTopicForActiveTab()
    {
        if (_documentTabs.SelectedTab is not { } page || page == _helpTab) return null;
        var queue = new Queue<(Control c, int depth)>();
        foreach (Control c in page.Controls) queue.Enqueue((c, 0));
        while (queue.Count > 0)
        {
            var (c, depth) = queue.Dequeue();
            if (HelpTopicByControl.TryGetValue(c.GetType().Name, out var topic)) return topic;
            if (depth < 3) foreach (Control k in c.Controls) queue.Enqueue((k, depth + 1));
        }
        return null;
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
        Action<string, string> openSql = (script, title) =>
        {
            var sql = TakeSqlControl();
            sql.SetScriptText(script);
            AddDocumentTab(title, sql.AsControl());
            sql.FocusEditor();
        };
        Control control;
        if (Bcode.App.UI.UiMode.IsNative) { var nc = new NativeCompareObjectsControl(_settings, () => _connections.Current); nc.OpenSqlRequested += openSql; control = nc; }
        else { var wc = new CompareObjectsControl(_settings, () => _connections.Current); wc.OpenSqlRequested += openSql; control = wc; }
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
        Action<string, bool, string> openSql = (script, sys, title) =>
        {
            var sql = TakeSqlControl();
            sql.SetDatabase(sys);
            sql.SetScriptText(script);
            AddDocumentTab(title, sql.AsControl());
            sql.FocusEditor();
        };
        Control control;
        if (Bcode.App.UI.UiMode.IsNative) { var nc = new NativeQueryHistoryControl(() => _connections.Current?.Name ?? ""); nc.OpenSqlRequested += openSql; control = nc; }
        else { var wc = new QueryHistoryControl(() => _connections.Current?.Name ?? ""); wc.OpenSqlRequested += openSql; control = wc; }
        _queryHistoryTab = AddDocumentTab("Lịch sử SQL", control);
        _queryHistoryTab.Disposed += (_, _) => _queryHistoryTab = null;
    }

    private void OpenQuickAccess()
    {
        var allTools = OrderedToolSpecs().Where(t => !t.key.StartsWith(Bcode.App.UI.UiTemplate.TopDownPrefix, StringComparison.Ordinal)).Select(t => (t.key, t.label));   // nút có sẵn của thanh trên kéo xuống: chỉnh ở Template
        using var form = new QuickAccessForm(allTools, new HashSet<string>(_settings.HiddenToolKeys), BarToolKeys(), ActiveToolGroups(), SuggestedToolGroups());
        if (form.ShowDialog(this) != DialogResult.OK) return;

        // Quick Access chỉ biết nút công cụ: giữ lại các nút có sẵn của thanh trên đã kéo xuống ("top:id", Template) để chúng không nhảy về thanh trên.
        var topPrefix = Bcode.App.UI.UiTemplate.TopDownPrefix;
        var keepTopOrder = _settings.ToolOrder.Where(k => k.StartsWith(topPrefix, StringComparison.Ordinal)).ToList();
        var keepTopHidden = _settings.HiddenToolKeys.Where(k => k.StartsWith(topPrefix, StringComparison.Ordinal)).ToList();
        _settings.HiddenToolKeys = form.HiddenKeys.Concat(keepTopHidden).ToList();
        _settings.ToolBarGroups = form.Groups;
        // Thứ tự trùng mặc định thì lưu rỗng (để nút/vạch ngăn nhóm mặc định hoạt động như cũ).
        _settings.ToolOrder = form.OrderedKeys.SequenceEqual(BarToolKeys()) ? new List<string>() : form.OrderedKeys;
        if (keepTopOrder.Count > 0) _settings.ToolOrder = (_settings.ToolOrder.Count == 0 ? BarToolKeys() : _settings.ToolOrder).Concat(keepTopOrder).ToList();
        _settings.Save();
        RebuildToolsBar();
    }

    /// <summary>Tiêu đề tab có thể mở nhiều cái cùng lúc (SQL Query, Command, Table): "SQL Query (1)", "SQL Query (2)"... — lấy số nhỏ nhất
    /// chưa có tab nào đang dùng, nên đóng (1) rồi mở mới thì lại là (1).</summary>
    private static readonly System.Text.RegularExpressions.Regex CreatedObjectRx = new(
        @"\b(?:CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(?:PROC(?:EDURE)?|FUNCTION|VIEW|TRIGGER)\s+(?<n>(?:\[?\w+\]?\s*\.\s*)?\[?[\w$#]+\]?)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Script vừa chạy có CREATE / ALTER PROC | FUNCTION | VIEW | TRIGGER (ngoài comment) → đổi tên tab thành "schema.tên" (object đầu tiên).
    /// Không có thì giữ tên tab.</summary>
    private void RenameTabByScript(ISqlTab tab, string script)
    {
        if (tab.IsDisposed || string.IsNullOrEmpty(script)) return;
        // bỏ comment để không lấy nhầm tên trong đoạn đã chú thích
        var code = System.Text.RegularExpressions.Regex.Replace(script, @"/\*[\s\S]*?\*/|--[^\r\n]*", " ");
        if (CreatedObjectRx.Match(code) is not { Success: true } m) return;
        var parts = m.Groups["n"].Value.Replace("[", "").Replace("]", "").Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var title = parts.Length >= 2 ? $"{parts[^2]}.{parts[^1]}" : $"dbo.{parts[0]}";
        var page = _documentTabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Controls.Contains(tab.AsControl()));
        if (page is null || page.Text == title) return;
        page.Text = title;
        page.ToolTipText = title;
    }

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
        if (Bcode.App.UI.UiMode.IsNative)
        {
            // Chế độ App: tô theme TRƯỚC khi tab hiện ra — trước đây tab được chọn (vẽ lên màn hình) với màu trắng mặc định rồi mới áp theme → nháy trắng.
            page.BackColor = Bcode.App.UI.AppColors.Background;
            Bcode.App.UI.ThemeManager.Apply(page);
            _documentTabs.TabPages.Add(page);
            _documentTabs.SelectedTab = page;
            UpdateQuickAccessOverlayBounds();
            return page;
        }
        _documentTabs.TabPages.Add(page);
        _documentTabs.SelectedTab = page;
        Bcode.App.UI.ThemeManager.Apply(page);
        UpdateQuickAccessOverlayBounds();
        return page;
    }

    /// <summary>
    /// Nhúng 1 Form có sẵn (FSG Yêu cầu, FSG FBO…) vào tab tài liệu thay vì mở cửa sổ riêng — Ctrl+Tab / bấm tab để qua lại như mọi công cụ khác.
    /// Form giữ nguyên logic (WebView2, nút, đăng nhập…); chỉ bỏ viền / phóng to và lấp đầy tab. Đóng tab = đóng form; form tự Close() thì tab cũng đóng.
    /// </summary>
    private TabPage AddFormTab(string title, Form form)
    {
        form.TopLevel = false;
        form.FormBorderStyle = FormBorderStyle.None;
        form.ShowInTaskbar = false;
        form.WindowState = FormWindowState.Normal;   // form tự đặt Maximized cho lúc là cửa sổ riêng — trong tab thì Dock lo kích thước
        var page = AddDocumentTab(title, form);
        form.Visible = true;
        // WebView2 trong form (FSG FBO, FSG Yêu cầu) không qua WebViewEnvironment.InitAsync → focus nằm trong trang web là mất Ctrl+Tab, Ctrl+W...
        AttachShortcutsToWebViews(form);
        form.FormClosed += (_, _) =>
        {
            if (page.IsDisposed || !_documentTabs.TabPages.Contains(page)) return;
            _documentTabs.TabPages.Remove(page);
            page.Dispose();
        };
        return page;
    }

    private static void AttachShortcutsToWebViews(Control root)
    {
        foreach (Control c in root.Controls)
        {
            if (c is Microsoft.Web.WebView2.WinForms.WebView2 web) WebViewEnvironment.AttachGlobalShortcuts(web, acceleratorOnly: true);
            else AttachShortcutsToWebViews(c);
        }
    }

    /// <summary>Đưa tab chứa form nhúng (AddFormTab) lên trước; false = form không còn nằm trong tab nào.</summary>
    private bool SelectFormTab(Form? form)
    {
        if (form is not { IsDisposed: false } || form.Parent is not TabPage page || !_documentTabs.TabPages.Contains(page)) return false;
        _documentTabs.SelectedTab = page;
        return true;
    }

    private FsgRequirementCrawlerForm? _fsgCrawlerForm;

    /// <summary>"FSG Yêu cầu" (cập nhật &amp; tra cứu y/c trên web FSG): 1 tab duy nhất — mở lại thì chuyển tới tab đang có.</summary>
    private void OpenFsgCrawlerTab()
    {
        if (SelectFormTab(_fsgCrawlerForm)) return;
        _fsgCrawlerForm = new FsgRequirementCrawlerForm();
        var page = AddFormTab("FSG Yêu cầu", _fsgCrawlerForm);
        page.Disposed += (_, _) => _fsgCrawlerForm = null;
    }

    /// <summary>"FSG FBO" (đăng nhập web chương trình của workspace): mỗi workspace 1 tab; mở lại cùng workspace thì chuyển tới tab đó.</summary>
    private QuickLaunchLoginForm ShowQuickLaunchTab(Workspace ws, string? url = null, string note = "")
    {
        if (_quickLaunchForm is { IsDisposed: false } existing && ReferenceEquals(existing.Workspace, ws) && SelectFormTab(existing)) return existing;
        var form = url is null && note.Length == 0 ? new QuickLaunchLoginForm(ws, _settings) : new QuickLaunchLoginForm(ws, _settings, url, note);
        _quickLaunchForm = form;
        var page = AddFormTab("FSG FBO · " + ws.Name, form);
        page.Disposed += (_, _) => { if (ReferenceEquals(_quickLaunchForm, form)) _quickLaunchForm = null; };
        return form;
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
        menu.Items.Add(new ToolStripMenuItem("Reopen Closed Tab", null, (_, _) => ReopenClosedTab(0))
            { Enabled = _closedTabs.Count > 0, ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.reopen") });
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
    private readonly List<TabPage> _tabMru = new();

    private void CloseTabsWhere(Func<TabPage, bool> match)
    {
        var targets = _documentTabs.TabPages.Cast<TabPage>().Where(p => !_documentTabs.IsPinned(p) && match(p)).Reverse().ToList();
        foreach (var p in targets) CloseDocumentTab(_documentTabs.TabPages.IndexOf(p));
    }

    private readonly HashSet<TabPage> _closingPages = new();

    private void CloseDocumentTab(int index)
    {
        if (index < 0 || index >= _documentTabs.TabPages.Count) return;
        var page = _documentTabs.TabPages[index];
        // Tab SQL đang mở thật: lấy nội dung mới nhất từ editor TRƯỚC khi đóng (Reopen Closed Tab mở lại đúng chữ vừa gõ), chờ tối đa ~0,4s rồi mới đóng.
        if (!_sessionRestoring && !_closingPages.Contains(page) && page.Controls.OfType<ISqlTab>().FirstOrDefault() is { IsReady: true } sqlCtl)
        {
            _closingPages.Add(page);
            _ = CaptureThenCloseAsync(page, sqlCtl);
            return;
        }
        CloseDocumentTabCore(page);
    }

    private async Task CaptureThenCloseAsync(TabPage page, ISqlTab c)
    {
        try { await Task.WhenAny(CaptureSessionTextAsync(c), Task.Delay(400)); }
        catch { /* editor đang đóng — dùng bản đã nhớ */ }
        _closingPages.Remove(page);
        if (!IsDisposed && !page.IsDisposed) CloseDocumentTabCore(page, afterCapture: true);
    }

    private void CloseDocumentTabCore(TabPage page, bool afterCapture = false)
    {
        var index = _documentTabs.TabPages.IndexOf(page);
        if (index < 0) return;

        if (page.Controls.OfType<ScriptEditorControl>().FirstOrDefault() is { IsDirty: true } editor)
        {
            var choice = MessageBox.Show(this, $"Tab \"{page.Text}\" có thay đổi chưa lưu. Đóng và bỏ qua thay đổi?",
                "Bcode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (choice != DialogResult.Yes) return;
        }

        RecordClosedTab(page);

        if (page == _fileLookupTabPage)
        {
            _fileLookupTabPage = null;
            _fileLookupControl = null;
        }
        if (page == _cashFlowCheckTabPage) _cashFlowCheckTabPage = null;
        if (page == _bbxnTabPage) _bbxnTabPage = null;
        if (page == _includeCheckTabPage) _includeCheckTabPage = null;
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
        // Đóng đúng tab đang xem: chọn lại tab đã xem ngay trước đó (không để WinForms tự nhảy về tab đầu tiên).
        if (_documentTabs.SelectedTab == page)
        {
            var back = _tabMru.FirstOrDefault(p => p != page && _documentTabs.TabPages.Contains(p))
                       ?? (_documentTabs.TabPages.Count > 1 ? _documentTabs.TabPages[index > 0 ? index - 1 : 1] : null);
            if (back is not null) _documentTabs.SelectedTab = back;
        }
        _tabMru.Remove(page);
        if (_prevTab == page) _prevTab = null;
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

    /// <summary>Tên một tab vừa đổi. Dải "Mở nhanh" (_quickAccessOverlay) là một panel nằm ĐÈ lên phần trống bên phải tab cuối, đặt theo mép phải của tab cuối tại lúc
    /// thêm / bớt tab. Tab dài ra mà không dời dải này thì nó che mất phần mới nới của tab — đúng chỗ có nút ✕ (lỗi "lọc bảng xong tab mất dấu ✕, mở thêm tab mới thấy lại").</summary>
    private void OnTabTitleChanged()
    {
        if (IsDisposed || _documentTabs.IsDisposed) return;
        _documentTabs.Invalidate();
        UpdateQuickAccessOverlayBounds();
        // Làm lại một nhịp sau: phòng khi Windows chưa đo xong bề rộng tab ngay trong lúc đổi chữ.
        if (IsHandleCreated)
            BeginInvoke(new Action(() => { if (!IsDisposed && !_documentTabs.IsDisposed) { UpdateQuickAccessOverlayBounds(); _documentTabs.Invalidate(); } }));
    }

    private void UpdateQuickAccessOverlayBounds()
    {
        var atBottom = _documentTabs.Alignment == TabAlignment.Bottom;
        var headerHeight = atBottom ? _documentTabs.Height - _documentTabs.DisplayRectangle.Bottom : _documentTabs.DisplayRectangle.Top;
        if (headerHeight <= 0) headerHeight = Bcode.App.UI.DpiScale.Px(this, 26);

        var lastTabRight = _documentTabs.TabPages.Count > 0
            ? _documentTabs.GetTabRect(_documentTabs.TabPages.Count - 1).Right
            : 0;

        // Quá nhiều tab (tổng bề rộng > thanh tab): Windows hiện 2 nút ◀ ▶ cuộn ở mép phải thanh tab — dải "Mở nhanh" nằm đè lên đó sẽ che mất nút, nên ẩn đi (vẫn chuột phải vào thanh tab để mở menu).
        var tabsWidth = 0;
        for (var i = 0; i < _documentTabs.TabPages.Count; i++) tabsWidth += _documentTabs.GetTabRect(i).Width;
        var overflow = tabsWidth > _documentTabs.Width - Bcode.App.UI.DpiScale.Px(this, 4);
        _quickAccessOverlay.Visible = !overflow;
        if (overflow) return;
        _quickAccessOverlay.Bounds = new Rectangle(lastTabRight, atBottom ? _documentTabs.Height - headerHeight : 0, Math.Max(0, _documentTabs.Width - lastTabRight), headerHeight);
        _quickAccessOverlay.BringToFront();
    }

    private WebMenu BuildQuickAccessMenu()
    {
        var menu = new WebMenu();
        if (_closedTabs.Count > 0)
        {
            menu.AddSub("Tab đã đóng gần đây", sub =>
            {
                for (var i = 0; i < Math.Min(_closedTabs.Count, 10); i++)
                {
                    var idx = i;
                    sub.Add(ClosedTabLabel(_closedTabs[i]), () => ReopenClosedTab(idx), shortcut: i == 0 ? Bcode.App.UI.ShortcutRegistry.Display("tab.reopen") : null);
                }
            });
            menu.AddSeparator();
        }
        menu.AddCaption("Mở nhanh");
        foreach (var (qKey, label, _, action) in OrderedToolSpecs())
        {
            var qCombo = Bcode.App.UI.ShortcutRegistry.Display("tool:" + qKey);
            if (qCombo.Length == 0) continue;
            var handler = action;
            menu.Add(label, () => handler(this, EventArgs.Empty), shortcut: qCombo);
        }
        return menu;
    }

    /// <param name="autoLoad">true (mở từ nút công cụ) = liệt kê App_Data ngay; false (mở do bấm menu) = để nguyên, menu vừa bấm sẽ tự dựng cây của nó — khỏi quét App_Data thừa.</param>
    private FileLookupControl? OpenFileLookupTab(bool autoLoad = true)
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
        control.ExcelToFrxRequested += path => OpenExcelToFrxTab(path, "frx");
        control.ExcelToRptRequested += path => OpenExcelToFrxTab(path, "rpt");
        control.NewLookupRequested += src => OpenLookupCloneOf(src);
        control.MenuForPage = MenuForLookupPage;
        _fileLookupTabPage = AddDocumentTab("File Lookup", control);
        _fileLookupControl = control;
        control.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"), load: autoLoad);
        return control;
    }

    /// <summary>Tìm BcodeScreenDesigner.exe: cạnh Bcode (thư mục ScreenDesigner\) hoặc bản dev build ở src\Bcode.ScreenDesigner.</summary>
    internal static string? FindScreenDesignerExe(string? custom = null)
    {
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom.Trim())) return custom.Trim();
        var baseDir = AppContext.BaseDirectory;
        var parent = Directory.GetParent(baseDir.TrimEnd('\\', '/'))?.FullName;   // cách phát hành: Bcode\, BcodeScreenDesigner\, Bcodeviewer\ nằm song song
        var candidates = new List<string> { Path.Combine(baseDir, "ScreenDesigner", "BcodeScreenDesigner.exe"), Path.Combine(baseDir, "BcodeScreenDesigner.exe") };
        if (parent is not null)
        {
            candidates.Add(Path.Combine(parent, "BcodeScreenDesigner", "BcodeScreenDesigner.exe"));
            candidates.Add(Path.Combine(parent, "ScreenDesigner", "BcodeScreenDesigner.exe"));
        }
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        for (var dir = new DirectoryInfo(baseDir); dir != null; dir = dir.Parent)
        {
            var proj = Path.Combine(dir.FullName, "Bcode.ScreenDesigner");
            if (!Directory.Exists(proj)) proj = Path.Combine(dir.FullName, "src", "Bcode.ScreenDesigner");
            if (!Directory.Exists(proj)) continue;
            foreach (var cfg in new[] { "Release", "Debug" })
            {
                var exe = Path.Combine(proj, "bin", cfg, "net8.0-windows", "BcodeScreenDesigner.exe");
                if (File.Exists(exe)) return exe;
            }
        }
        return null;
    }

    /// <summary>Mở Screen Designer (exe riêng) kèm project đang chọn (gốc source) và — nếu bấm từ cây menu — controller của menu đó.</summary>
    private void LaunchScreenDesigner(string? controller)
    {
        if (!RequireLicense("Thiết kế màn hình")) return;
        var exe = FindScreenDesignerExe(_settings.ScreenDesignerExePath);
        if (exe is null)
        {
            MessageBox.Show(this, "Chưa thấy BcodeScreenDesigner.exe. Khai báo lại đường dẫn ở Template → Đường dẫn, hoặc build project src\\Bcode.ScreenDesigner (hoặc build lại Bcode) để có thư mục ScreenDesigner cạnh Bcode.exe.", "Bcode — Thiết kế màn hình");
            return;
        }
        var ws = _connections.Current;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        if (ws is not null && !string.IsNullOrWhiteSpace(ws.SourcePath)) { psi.ArgumentList.Add("--source"); psi.ArgumentList.Add(ws.SourcePath); }
        if (ws is not null) { psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(ws.Name); }
        if (!string.IsNullOrWhiteSpace(controller)) { psi.ArgumentList.Add("--controller"); psi.ArgumentList.Add(controller); }
        try { Process.Start(psi); }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được Screen Designer:\n" + ex.Message, "Bcode"); }
    }

    /// <summary>BcodeNote.exe (src/BcodeNote.App, build tự chép cạnh Bcode.exe): cạnh Bcode.exe, không có thì thư mục BcodeNote\ cạnh đó.</summary>
    internal static string? FindBcodeNoteExe() =>
        new[] { Path.Combine(AppContext.BaseDirectory, "BcodeNote.exe"), Path.Combine(AppContext.BaseDirectory, "BcodeNote", "BcodeNote.exe") }
            .FirstOrDefault(File.Exists);

    /// <summary>"Note (New) nhiều dự án" (menu Tool mở rộng, nút ở thanh Note (New)): mở BcodeNote.exe vào đúng dự án đang chọn.
    /// Ghi nốt phần đang gõ ở tab Note (New) trước để BcodeNote đọc file request mới nhất.</summary>
    private async Task LaunchBcodeNoteAsync()
    {
        var exe = FindBcodeNoteExe();
        if (exe is null)
        {
            MessageBox.Show(this, "Chưa thấy BcodeNote.exe cạnh Bcode.exe. Build project src\\BcodeNote.App (build xong tự chép exe sang cạnh Bcode.exe) rồi thử lại.", "Bcode — Note (New) nhiều dự án");
            return;
        }
        if (_advanceNoteTab is not null && FindDescendant<AdvanceNoteControl>(_advanceNoteTab) is { } note) await note.FlushAsync();
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        if (_connections.Current is { } ws) { psi.ArgumentList.Add("--project"); psi.ArgumentList.Add(ws.Name); }
        try { Process.Start(psi); }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được BcodeNote:\n" + ex.Message, "Bcode"); }
    }

    private TabPage? _includeCheckTabPage;

    /// <summary>Tab "Check Include" (một tab duy nhất): so INCLUDE / IGNORE, options, wcommand của dự án với catalog tính năng từng phiên bản.</summary>
    private void OpenIncludeCheckTab()
    {
        if (_includeCheckTabPage is not null && _documentTabs.TabPages.Contains(_includeCheckTabPage))
        {
            _documentTabs.SelectedTab = _includeCheckTabPage;
            return;
        }
        _includeCheckTabPage = AddDocumentTab("Check Include", new IncludeCheckControl(_connections));
    }

    private TabPage? _bbxnTabPage;

    /// <summary>Tab "Biên bản xác nhận (Word)" (một tab duy nhất): tạo biên bản cài đặt / báo cáo tài chính / tài liệu khảo sát / nghiệm thu cho khách hàng rồi xuất Word.</summary>
    private void OpenBbxnTab()
    {
        if (_bbxnTabPage is not null && _documentTabs.TabPages.Contains(_bbxnTabPage))
        {
            _documentTabs.SelectedTab = _bbxnTabPage;
            return;
        }
        _bbxnTabPage = AddDocumentTab("Biên bản xác nhận", new BbxnControl(() => _connections.Current));
    }

    private TabPage? _cashFlowCheckTabPage;

    /// <summary>Tab "Check LCTT / CĐKT" (một tab duy nhất): đối chiếu báo cáo lưu chuyển tiền tệ + cân đối kế toán với bảng cân đối phát sinh, bảng kê chứng từ.</summary>
    private void OpenCashFlowCheckTab()
    {
        if (_cashFlowCheckTabPage is not null && _documentTabs.TabPages.Contains(_cashFlowCheckTabPage))
        {
            _documentTabs.SelectedTab = _cashFlowCheckTabPage;
            return;
        }
        _cashFlowCheckTabPage = AddDocumentTab("Check LCTT / CĐKT", new CashFlowCheckControl());
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

        var control = new GenUpdatePackageControl(_fileLookupService, _sqlObjectService, ws, _genAllService);
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
    private ISqlTab? _spareSql;
    private readonly System.Windows.Forms.Timer _spareTimer = new();

    private void QueueSpareSql(int delayMs)
    {
        if (IsDisposed || !Bcode.App.UI.PerformanceProfile.Resolve(_settings).PrewarmSqlTab) return; // Chế độ hiệu năng tắt dựng sẵn: tab mở như bình thường
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
            // Dựng ở đúng cỡ vùng tab: khung ẩn hẹp (1100px) làm thanh nút xuống 2 hàng, gắn vào tab rồi mới co về 1 hàng → thấy thanh nhảy lúc mở.
            var area = _documentTabs.DisplayRectangle.Size;
            if (area.Width > 200 && area.Height > 200) _spareHost.Size = area;
            var c = CreateFreeScriptControl(prewarm: true);
            _spareHost.Controls.Add(c.AsControl());
            Bcode.App.UI.ThemeManager.Apply(c.AsControl());
            _spareSql = c;
        }
        catch { _spareSql = null; /* không dựng sẵn được — lần mở tab sau tạo bình thường */ }
    }

    /// <summary>Lấy tab SQL Query dựng sẵn nếu có (rồi dựng cái dự phòng kế tiếp), không thì tạo mới.</summary>
    private ISqlTab TakeSqlControl()
    {
        var spare = _spareSql;
        _spareSql = null;
        ISqlTab control;
        // Chỉ lấy tab dựng sẵn khi nó đã SẴN SÀNG: lấy ngay lúc WebView2 còn đang khởi tạo rồi chuyển sang tab khác làm khởi tạo bị huỷ (E_ABORT) —
        // dễ gặp khi vừa mở Bcode và khôi phục tab. Chưa sẵn sàng thì để nó lại làm dự phòng và tạo tab mới.
        var tookSpare = spare is not null && !spare.IsDisposed && spare.IsReady;
        if (tookSpare) { control = spare!; control.BeginUse(); }
        else
        {
            if (spare is not null && !spare.IsDisposed) _spareSql = spare;
            control = CreateFreeScriptControl();
        }
        // Vừa lấy tab dựng sẵn (đã nạp xong, không còn tranh tài nguyên) → dựng ngay cái kế tiếp, để mở liên tiếp vẫn có sẵn;
        // vừa tạo tab mới từ đầu thì để nó nạp xong trước.
        QueueSpareSql(tookSpare ? 250 : 1200);
        return control;
    }

    /// <summary>"Debug trong Bcode" từ hộp Chạy SQL của BcodeViewer: tab SQL Query mới (App Data) chứa script, rồi đưa
    /// cửa sổ Bcode lên trước để chạy/sửa tiếp — từng bước bằng Debug Step của tab nếu cần.</summary>
    private void OpenViewerScriptTab(string script, string title)
    {
        var control = TakeSqlControl();
        control.SetDatabase(false);
        control.SetScriptText(script);
        AddDocumentTab(NumberedTabTitle(string.IsNullOrWhiteSpace(title) ? "SQL Query" : title), control.AsControl());
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
        Activate();
        control.FocusEditor();
    }

    /// <summary>BcodeNote.exe gửi script (Note (New) của dự án <paramref name="project"/>) sang: tab SQL Query mới trên App / Sys của dự án ĐANG MỞ ở Bcode.
    /// Khác dự án thì ghi cảnh báo đầu script + tiêu đề để không chạy nhầm database.</summary>
    private void OpenProjectScriptTab(string script, string title, string project, bool sys)
    {
        var current = _connections.Current?.Name ?? "";
        var other = project.Length > 0 && !string.Equals(project, current, StringComparison.OrdinalIgnoreCase);
        if (other)
        {
            script = $"-- ⚠ Script của dự án \"{project}\" ({(sys ? "Sys" : "App")}) — Bcode đang mở dự án \"{(current.Length > 0 ? current : "(chưa chọn)")}\". "
                + "Đổi sang đúng dự án (Ctrl+O) trước khi chạy.\r\n" + script;
            title = $"{title} [{project}]";
        }
        var control = TakeSqlControl();
        control.SetDatabase(sys);
        control.SetScriptText(script);
        AddDocumentTab(NumberedTabTitle(string.IsNullOrWhiteSpace(title) ? "SQL Query" : title), control.AsControl());
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
        Activate();
        control.FocusEditor();
        if (other) PushStatus($"Script từ BcodeNote thuộc dự án {project} — Bcode đang mở {current}.");
    }

    private ISqlTab OpenFreeScriptTab()
    {
        // Luôn tạo tab SQL Query mới, không dùng lại tab cũ
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _rawSqlControl = TakeSqlControl();
        var tTake = sw.ElapsedMilliseconds;
        _rawSqlTabPage = AddDocumentTab(NumberedTabTitle("SQL Query"), _rawSqlControl.AsControl());
        var tSync = sw.ElapsedMilliseconds;
        void LogShown(object? s, EventArgs e)
        {
            Application.Idle -= LogShown;   // hàng đợi thông điệp rỗng = tab đã vẽ xong lần đầu
            Bcode.App.UI.ThemeManager.LogTiming($"SQL Query: mở tab — phần trên UI {tSync} ms (lấy/tạo control {tTake}, AddDocumentTab {tSync - tTake}), vẽ xong {sw.ElapsedMilliseconds} ms");
        }
        Application.Idle += LogShown;
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
    private AiWebPanel OpenAi(Bcode.Shared.AiSite site, ISqlTab? host)
    {
        if (AppSettings.AiEmbedded)
        {
            host ??= _documentTabs.SelectedTab?.Controls.OfType<ISqlTab>().FirstOrDefault();
            if (host is not null) return host.ShowAi(site);
        }
        return OpenAiTab(site);
    }

    /// <summary>Từ tab SQL Query: mở Claude/Gemini rồi đưa script vào ô chat (chưa gửi — gõ câu hỏi rồi Enter).</summary>
    private async void SendScriptToAi(string engine, string text, ISqlTab? host = null)
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

    private ISqlTab CreateFreeScriptControl(bool prewarm = false)
    {
        // Chế độ App: tab SQL native (Scintilla + lưới WinForms, không WebView2); chế độ Web: Monaco như trước.
        ISqlTab control = Bcode.App.UI.UiMode.IsNative
            ? new NativeSqlControl(_rawSqlService, _sqlObjectService, _lookupService, _snippets)
            : new RawSqlControl(_rawSqlService, _sqlObjectService, _lookupService, _snippets, prewarm);
        control.ResultReady += table => _lastQueryResult = table;
        // mỗi lần chạy: script tạo / sửa store, function, view, trigger → tên tab = tên object đó (biết tab nào đang làm store nào)
        control.ScriptExecuted += (script, _, _, _, _) => BeginInvoke(() => RenameTabByScript(control, script));
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
        control.FastAskRequested += () => EmbedFastAsk(control);
        control.OpenObjectInNewTabRequested += (title, sys, text) =>
        {
            var tab = TakeSqlControl();
            tab.SetDatabase(sys);
            AddDocumentTab(title, tab.AsControl());
            _ = tab.OpenScriptAsync(text);
        };
        return control;
    }

    /// <summary>
    /// SQL Profiler → "🐞 Debug store": tìm store được gọi trong câu SQL vừa bắt (TextData, vd "declare @0…; select @0=…; exec rs_xxx 'V', @0…"),
    /// mở tab gồm định nghĩa store + GO + nguyên câu gọi đó rồi bật Debug từng bước — giá trị tham số, biến @0…, bảng tạm (#$query) lấy từ
    /// câu gọi (StepParamsForm.InitialValues / SqlStepPlanner tail prelude). Định nghĩa giữ nguyên CREATE nên lỡ bấm Execute cũng không đè store.
    /// </summary>
    private async Task OpenProfilerDebugAsync(SqlProfilerControl profiler, string sql)
    {
        List<DebugCandidate> candidates = new();
        try
        {
            var scanner = new DebugTargetScanner(_sqlObjectService);
            foreach (var sys in new[] { false, true })
            {
                candidates = (await scanner.ScanAsync(sql, sys)).Where(c => c.Target.Kind == SqlObjectKind.StoredProcedure).ToList();
                if (candidates.Count > 0) break;
            }
        }
        catch (Exception ex) { profiler.ReportStatus("Không tìm được store trong câu SQL: " + ex.Message); return; }
        if (candidates.Count == 0) { profiler.ReportStatus("Câu SQL đang chọn không gọi store nào có trong App / Sys Data (cần dòng có \"exec <store> …\")."); return; }

        var chosen = candidates.Count == 1 ? candidates[0] : null;
        if (chosen is null)
        {
            await Task.Yield(); // thoát khỏi callback của trang Profiler trước khi mở hộp chọn (WebView2 mới)
            using var form = new ChooseDebugTargetForm(candidates);
            if (form.ShowDialog(this) != DialogResult.OK || form.Selected is not { } picked) return;
            chosen = picked;
        }
        profiler.ReportStatus($"Mở debug {chosen.Target.QualifiedName} với câu gọi vừa bắt...");
        await OpenDebugTargetAsync(chosen.Target, null, definition => definition.TrimEnd() + Environment.NewLine + "GO" + Environment.NewLine + sql.Trim() + Environment.NewLine);
    }

    /// <param name="compose">Dựng nội dung tab từ định nghĩa object (vd thêm "GO + câu gọi" của Profiler); null = chỉ định nghĩa.</param>
    private async Task OpenDebugTargetAsync(SqlObjectInfo target, string? callText = null, Func<string, string>? compose = null)
    {
        string definition;
        try
        {
            definition = await _sqlObjectService.GetDefinitionAsync(target);
            if (compose is not null) definition = compose(definition);
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
            if (existingPage.Controls.OfType<ISqlTab>().FirstOrDefault() is { } existingControl)
            {
                existingControl.SetDatabase(target.FromSysDatabase);
                _ = existingControl.LoadAndDebugAsync(definition, callText);
            }
            return;
        }

        var control = TakeSqlControl();
        control.SetDatabase(target.FromSysDatabase);
        var page = AddDocumentTab(target.QualifiedName, control.AsControl());
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

        Control control;
        if (Bcode.App.UI.UiMode.IsNative)
        {
            var nc = new NativeLookupControl(_sqlObjectService, _tableDataService);
            nc.OpenInTabRequested += obj => _ = OpenObjectDefinitionAsync(obj);
            control = nc;
        }
        else
        {
            var wc = new LookupControl(_sqlObjectService, _tableDataService);
            wc.OpenInTabRequested += obj => _ = OpenObjectDefinitionAsync(obj);
            control = wc;
        }
        _lookupTabPage = AddDocumentTab("Lookup", control);
        _lookupTabPage.Disposed += (_, _) => _lookupTabPage = null;
    }

    private void OpenTableTab()
    {
        var control = new TableEditControl(_tableDataService, _sqlObjectService, _dataScript, _scriptFileService, _genInsert, _genUpdate);
        WireTableMenu(control);
        var page = AddDocumentTab(NumberedTabTitle("Table"), control);
        // Lọc/tải bảng nào thì tab đổi tên thành bảng đó (dmkh, r00$000000...).
        control.TableLoaded += name => { if (!page.IsDisposed) { page.Text = name; OnTabTitleChanged(); } };
    }

    private readonly TableMenuService _tableMenu;

    /// <summary>Tab Table: dòng "Menu: Phiếu nhập kho (inctpnd.aspx)" theo bảng đang nhập (dmct9 + controller Dir/Grid của menu) —
    /// bấm vào thì hiện cây WCommand, mở các nhánh cha và chọn đúng menu (như chọn menu trên cây).</summary>
    private void WireTableMenu(TableEditControl control)
    {
        control.MenuLookup = table => _tableMenu.FindAsync(table, control.UseSysDatabase);
        control.MenuRevealRequested += item =>
        {
            if (_mainSplit is { Panel1Collapsed: true }) ToggleMenuTree();
            SelectWCommandTab();
            _ = _wcommandTree.RevealAsync(item);
        };
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
        _stringBeautyTab = AddDocumentTab("String Beauty", Bcode.App.UI.UiMode.IsNative ? new NativeStringBeautyControl() : new StringBeautyControl());
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
    private TabPage? _fsgReqTab;

    /// <summary>Tab "Báo cáo yêu cầu FSG": kéo yêu cầu từ database FSG, lọc theo nhóm trạng thái, tô màu yêu cầu tới hạn chưa hoàn thành. Một tab duy nhất.</summary>
    /// <param name="menu">Khác null = chuột phải cây WCommand → "Yêu cầu liên quan (QLYC)…": lọc sẵn các y/c của dự án đang chọn liên quan tới menu đó.</param>
    private void OpenFsgRequirementReportTab(FsgRequirementReportControl.MenuQuery? menu = null)
    {
        if (!RequireLicense("Báo cáo yêu cầu FSG")) return;
        if (_fsgReqTab is not null && _documentTabs.TabPages.Contains(_fsgReqTab))
        {
            _documentTabs.SelectedTab = _fsgReqTab;
            if (menu is not null && FindDescendant<FsgRequirementReportControl>(_fsgReqTab) is { } open) open.ShowForMenu(menu);
            return;
        }
        var fsgReq = new FsgRequirementReportControl(() => _connections.Current, () => _settings.NoteProgrammer ?? "");
        fsgReq.AiCatalogRequested += OpenQuickListWithAi;
        fsgReq.CatalogAssistRequested += OpenQuickListWithAssist;
        if (menu is not null) fsgReq.ShowForMenu(menu);
        _fsgReqTab = AddDocumentTab("Yêu cầu FSG", fsgReq);
        _fsgReqTab.Disposed += (_, _) => _fsgReqTab = null;
    }

    private TabPage? _weeklyMailTab;

    /// <summary>Tab "Mail kế hoạch tuần" (menu Triển khai): mẫu mail báo cáo/kế hoạch tuần theo dự án, gửi qua SMTP dùng chung với Check Mail. Một tab duy nhất.</summary>
    private void OpenWeeklyMailTab()
    {
        if (_weeklyMailTab is not null && _documentTabs.TabPages.Contains(_weeklyMailTab)) { _documentTabs.SelectedTab = _weeklyMailTab; return; }
        Func<IReadOnlyList<string>> projects = () => _settings.Workspaces.Select(w => w.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        Func<string?> currentProject = () => _connections.Current?.Name;
        _weeklyMailTab = AddDocumentTab("Mail kế hoạch tuần", Bcode.App.UI.UiMode.IsNative ? new NativeWeeklyMailControl(projects, currentProject) : new WeeklyMailControl(projects, currentProject));
        _weeklyMailTab.Disposed += (_, _) => _weeklyMailTab = null;
    }

    private void OpenCheckMailTab()
    {
        if (_checkMailTab is not null && _documentTabs.TabPages.Contains(_checkMailTab))
        {
            _documentTabs.SelectedTab = _checkMailTab;
            return;
        }
        // Check Mail dùng bản WebView ở CẢ hai chế độ (user chọn 2026-10-15) — NativeCheckMailControl còn trong code, chỉ không dùng.
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

    private ReportStudioForm? _reportStudioForm;

    /// <summary>"Tạo báo cáo" — CỬA SỔ RIÊNG toàn màn hình (<see cref="ReportStudioForm"/>) gồm 2 chế độ: "Thiết kế từ bảng" (thư viện Libs\Bcode.ReportBuilder.dll,
    /// xem <see cref="ReportBuilderModule"/>: chọn bảng / trường như Power BI → sinh procedure zrs_ + Filter / Grid / Report / Main / Excel) và "Từ procedure có sẵn"
    /// (tham số → Filter, kết quả chạy → Grid, file từ source mẫu CreateReport). Mở lại thì đưa cửa sổ đó lên trước.
    /// Procedure chỉ được sinh ra để xem / mở sang tab SQL; lưu file vào source phải qua màn hình xác nhận (có diff + backup).</summary>
    private void OpenReportBuilderTab()
    {
        if (!RequireLicense("Tạo báo cáo")) return;
        if (_reportStudioForm is not null && !_reportStudioForm.IsDisposed)
        {
            if (_reportStudioForm.WindowState == FormWindowState.Minimized) _reportStudioForm.WindowState = FormWindowState.Normal;
            _reportStudioForm.Activate();
            return;
        }
        _reportStudioForm = new ReportStudioForm(_connections, _sqlObjectService, (script, sys, title) =>
        {
            var sql = TakeSqlControl();
            sql.SetDatabase(sys);
            sql.SetScriptText(script);
            AddDocumentTab(title, sql.AsControl());
            sql.FocusEditor();
            Activate(); // đưa cửa sổ Bcode lên để thấy tab SQL vừa mở (cửa sổ thiết kế vẫn mở)
        });
        _reportStudioForm.FormClosed += (_, _) => _reportStudioForm = null;
        _reportStudioForm.Show();
    }

    private TabPage? _excelToFrxTab;

    /// <summary>Tab "Excel → FRX": Excel mẫu in → FastReport (.frx), kéo thả chỉnh bố cục, xem trước PDF (ExcelToFrx.dll, source ở
    /// D:\phongnt\ConvertBcode). Một tab duy nhất, mở lại thì chuyển tới; <paramref name="xlsxPath"/> = nạp sẵn file đó,
    /// <paramref name="format"/> = chọn sẵn định dạng ra ("frx" | "rpt"; null = giữ lựa chọn trên trang).</summary>
    private void OpenExcelToFrxTab(string? xlsxPath = null, string? format = null)
    {
        if (!RequireLicense("Excel → FRX")) return;
        if (_excelToFrxTab is not null && _documentTabs.TabPages.Contains(_excelToFrxTab))
        {
            _documentTabs.SelectedTab = _excelToFrxTab;
            if (xlsxPath is not null && _excelToFrxTab.Controls.OfType<ExcelToFrx.ExcelToFrxControl>().FirstOrDefault() is { } open)
                open.OpenExcel(xlsxPath, format);
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

        if (xlsxPath is not null) ctl.OpenExcel(xlsxPath, format);
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
        Action<IReadOnlyList<(string Title, bool Sys, string Text)>> openScripts = scripts =>
        {
            foreach (var (title, sys, text) in scripts)
            {
                var tab = TakeSqlControl();
                tab.SetDatabase(sys);
                AddDocumentTab(title, tab.AsControl());
                _ = tab.OpenScriptAsync(text);
            }
        };
        Control control;
        if (Bcode.App.UI.UiMode.IsNative)
        {
            var nc = new NativeAdvanceNoteControl(_advanceNoteService, _genAllService, () => _connections.Current, _settings);
            nc.OpenScriptsRequested += openScripts;
            nc.AiCatalogRequested += OpenQuickListWithAi;
            nc.CatalogAssistRequested += OpenQuickListWithAssist;
            nc.NoteHubRequested += () => _ = LaunchBcodeNoteAsync();
            control = nc;
        }
        else
        {
            var wc = new AdvanceNoteControl(_advanceNoteService, _genAllService, () => _connections.Current, _settings);
            wc.OpenScriptsRequested += openScripts;
            wc.AiCatalogRequested += OpenQuickListWithAi;
            wc.CatalogAssistRequested += OpenQuickListWithAssist;
            wc.NoteHubRequested += () => _ = LaunchBcodeNoteAsync();
            control = wc;
        }
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
        if (page?.Controls.OfType<ISqlTab>().FirstOrDefault() is { } sql) sql.FocusEditor();
        else if (page?.Controls.OfType<AiWebPanel>().FirstOrDefault() is { } ai) ai.FocusWeb(); // Claude/Gemini: focus vào trang web để gõ được ngay
        else if (page is not null) { page.Focus(); page.SelectNextControl(page, true, true, true, false); }
        else _documentTabs.Focus();
    }

    // ---- Ctrl+Tab → hộp chọn tab ----
    private TabPage? _curTab, _prevTab;           // tab đang chọn / tab vừa rời — cho chế độ Ctrl+Tab "tab song song"
    private TabSwitcherForm? _switcher;           // hộp chọn tab đang mở (hoặc đã tạo, chờ hiện)

    /// <summary>Tab mà tab.next / tab.prev đưa tới: chế độ "tab song song" (Template → Cửa sổ → Ctrl+Tab) về lại tab vừa rời — A→B rồi B→A;
    /// không có tab trước (hoặc chế độ "tab tới") thì tab kế / trước, vòng tròn.</summary>
    private int TabSwitchTarget(int step)
    {
        if (Bcode.App.UI.UiTemplate.Current.TabSwitchMode == "toggle" && _prevTab is { } prev && _documentTabs.TabPages.Contains(prev) && prev != _documentTabs.SelectedTab)
            return _documentTabs.TabPages.IndexOf(prev);
        var count = _documentTabs.TabPages.Count;
        return (Math.Max(0, _documentTabs.SelectedIndex) + step + count) % count;
    }

    /// <summary>Phím tắt tab.next / tab.prev bấm khi đang giữ Ctrl (Ctrl+Tab...): hiện NGAY hộp liệt kê mọi tab (xem <see cref="TabSwitcherForm"/>),
    /// sáng sẵn ở <paramref name="target"/>; giữ Ctrl bấm tiếp để đi, thả Ctrl là chuyển. Bấm nhanh rồi thả thì tới đúng <paramref name="target"/> như trước.
    /// Hộp được tạo ngay (bấm Tab tiếp trước khi hộp kịp hiện vẫn đếm bước — <see cref="TabSwitcherForm.Step"/>), còn ShowDialog chạy sau khi
    /// handler phím (ProcessCmdKey / callback WebView2) đã trả về.</summary>
    private void OpenTabSwitcher(int target)
    {
        var names = _documentTabs.TabPages.Cast<TabPage>().Select(p => p.Text).ToList();
        var switcher = new TabSwitcherForm(names, Math.Max(0, _documentTabs.SelectedIndex), target, commitOnCtrlUp: true);
        _switcher = switcher;
        BeginInvoke(new Action(() =>
        {
            try
            {
                if (IsDisposed) return;
                if (!TabSwitcherForm.CtrlHeld) switcher.Accept();   // đã thả Ctrl trước khi hộp kịp hiện: chuyển luôn, không hiện hộp
                else switcher.ShowDialog(this);
                var i = switcher.SelectedIndex;
                if (i >= 0 && i < _documentTabs.TabPages.Count)
                {
                    _documentTabs.SelectedIndex = i;
                    _documentTabs.SelectedTab?.Focus();
                }
            }
            finally
            {
                if (ReferenceEquals(_switcher, switcher)) _switcher = null;
                switcher.Dispose();
            }
        }));
    }

    private void OnWebGlobalShortcut(string combo)
    {
        // Ctrl+Tab bấm tiếp mà phím còn tới trang WebView2 (hộp chọn tab vừa tạo, chưa kịp nhận focus): đi tiếp trong hộp, không mở hộp thứ hai.
        if (_switcher is { } open && Bcode.App.UI.ShortcutRegistry.AppIdFor(combo) is "tab.next" or "tab.prev")
        {
            var step = Bcode.App.UI.ShortcutRegistry.AppIdFor(combo) == "tab.prev" ? -1 : 1;
            BeginInvoke(new Action(() => open.Step(step)));
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
        using var form = new UiTemplateForm(_settings, OrderedToolSpecs().Where(t => !t.key.StartsWith(Bcode.App.UI.UiTemplate.TopDownPrefix, StringComparison.Ordinal)).Select(t => (t.key, t.label)).ToList(), BarToolKeys(), _wcommandTree.TopGroups());
        form.ShowDialog(this);
        ApplySparePolicy();
    }

    /// <summary>Áp ngay phần "dựng sẵn tab SQL" của Chế độ hiệu năng (giới hạn tiến trình thì phải mở lại Bcode): tắt thì bỏ tab dự phòng đang giữ
    /// để trả RAM, bật thì dựng lại ở nền.</summary>
    private void ApplySparePolicy()
    {
        if (IsDisposed) return;
        if (Bcode.App.UI.PerformanceProfile.Resolve(_settings).PrewarmSqlTab) { if (_spareSql is null) QueueSpareSql(1200); return; }
        _spareTimer.Stop();
        var spare = _spareSql;
        _spareSql = null;
        if (spare is not null && !spare.IsDisposed) { _spareHost.Controls.Remove(spare.AsControl()); spare.Dispose(); }
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

    /// <summary>Control kiểu <typeparamref name="T"/> đầu tiên trong vài lớp con đầu của <paramref name="root"/> (như HelpTopicForActiveTab).</summary>
    private static T? FindDescendant<T>(Control root) where T : Control
    {
        var queue = new Queue<(Control c, int depth)>();
        foreach (Control c in root.Controls) queue.Enqueue((c, 0));
        while (queue.Count > 0)
        {
            var (c, depth) = queue.Dequeue();
            if (c is T hit) return hit;
            if (depth < 3) foreach (Control k in c.Controls) queue.Enqueue((k, depth + 1));
        }
        return null;
    }

    /// <summary>Chạy chức năng phạm vi App theo id ("tree.toggle", "tab.close", "tool:sql_query"...). Trả false nếu id không chạy được lúc này.</summary>
    private bool RunAppShortcut(string id)
    {
        switch (id)
        {
            case "tree.toggle": ToggleMenuTree(); return true;
            case "project.quick": ShowSynchronizeProjects(); return true;
            case "project.picker": ShowProjectPicker(); return true;
            case "debug.decrypt": DebugDecryptConnectStr(); return true;
            case "app.chooseServer": OpenConnectionSettings(); return true;
            case "app.toggleDb": ToggleActiveDatabase(); return true;
            case "app.programPath": OpenProgramPath(); return true;
            case "project.openSource": OpenProjectFolder(_connections.Current?.SourcePath ?? ""); return true;
            case "project.openWorking": OpenProjectFolder(_connections.Current?.WorkingPath ?? ""); return true;
            case "project.openMobile": OpenProjectFolder(_connections.Current?.MobilePath ?? ""); return true;
            case "left.mobile": SelectLeftSection("mobile"); return true;
            case "project.web": OpenProjectWeb(); return true;
            case "project.copyInfo":
                if (_connections.Current is { } projectWs) CopyProjectInfo(projectWs);
                return true;
            case "tab.close": CloseDocumentTab(_documentTabs.SelectedIndex); return true;
            case "tab.reopen": ReopenClosedTab(0); return true;
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
            case "app.fsgNotify": BeginInvoke(new Action(OpenFsgNotifyPanel)); return true;
            case "app.settingsMenu": _settingsMenu().Show(TopBarControl, 10, TopBarControl.Height); return true;
            case "app.template": BeginInvoke(new Action(OpenUiTemplate)); return true;
            case "app.customUi": ToggleCustomUi(); return true;
            case "db.app": ApplyActiveDatabase(false); return true;
            case "db.sys": ApplyActiveDatabase(true); return true;
            case "db.refresh": _ = RefreshDatabaseAsync(); return true;
            case "script.add": AddScript(); return true;
            case "script.view": ViewScriptCart(); return true;
            case "script.clear": _scriptFileService.ClearCart(); return true;
            case "script.save": SaveActiveScript(); return true;
            case "script.copy": CopyActiveScript(); return true;
            case "app.openWebConfig": OpenWebConfig(); return true;
            case "app.refreshWebConfig": RefreshWebConfig(); return true;
            case "app.clearStructure": ClearStructureApp(); return true;
            case "app.createMenu": _ = CreateMenuAsync(); return true;
            case "note.aiCatalog":
            {
                // Chỉ có tác dụng khi đang ở tab Note (New) hoặc Yêu cầu FSG; tab khác thì để phím đi tiếp.
                if (_fsgReqTab is not null && _documentTabs.SelectedTab == _fsgReqTab)
                {
                    if (FindDescendant<FsgRequirementReportControl>(_fsgReqTab) is not { } fsg) return false;
                    fsg.RequestAiCatalog();
                    return true;
                }
                if (_advanceNoteTab is null || _documentTabs.SelectedTab != _advanceNoteTab) return false;
                var note = FindDescendant<AdvanceNoteControl>(_advanceNoteTab);
                var nativeNote = FindDescendant<NativeAdvanceNoteControl>(_advanceNoteTab);
                if (note is not null) note.RequestAiCatalog();
                else if (nativeNote is not null) nativeNote.RequestAiCatalog();
                else return false;
                return true;
            }
            case "note.aiAnalyze":
            case "note.fastRules":
            {
                // Như note.aiCatalog nhưng cho nút 🔎 AI phân tích / ⚙ Theo luật Fast.
                var mode = id == "note.fastRules" ? "rules" : "analyze";
                if (_fsgReqTab is not null && _documentTabs.SelectedTab == _fsgReqTab)
                {
                    if (FindDescendant<FsgRequirementReportControl>(_fsgReqTab) is not { } fsg) return false;
                    fsg.RequestCatalogAssist(mode);
                    return true;
                }
                if (_advanceNoteTab is null || _documentTabs.SelectedTab != _advanceNoteTab) return false;
                var note = FindDescendant<AdvanceNoteControl>(_advanceNoteTab);
                var nativeNote = FindDescendant<NativeAdvanceNoteControl>(_advanceNoteTab);
                if (note is not null) note.RequestCatalogAssist(mode);
                else if (nativeNote is not null) nativeNote.RequestCatalogAssist(mode);
                else return false;
                return true;
            }
            case "tab.next":
            case "tab.prev":
            {
                // Chuyển tab kế / trước (vòng tròn), bấm được ở bất kỳ đâu miễn là có tab.
                if (_documentTabs.TabPages.Count == 0) return false;
                var step = id == "tab.prev" ? -1 : 1;
                if (_switcher is { } open) { open.Step(step); return true; }   // hộp chọn tab đang mở: đi tiếp trong hộp
                var target = TabSwitchTarget(step);
                // Tổ hợp có Ctrl (mặc định Ctrl+Tab) và Ctrl còn đang giữ: hiện hộp chọn tab, thả Ctrl mới chuyển. Tổ hợp không có Ctrl
                // (người dùng tự gán) hoặc gọi từ Command Palette: chuyển thẳng như trước.
                if (TabSwitcherForm.CtrlHeld && (Control.ModifierKeys & Keys.Alt) == 0) { OpenTabSwitcher(target); return true; }
                _documentTabs.SelectedIndex = target;
                _documentTabs.SelectedTab?.Focus();
                return true;
            }
            case "window.new":
                // Mở thêm 1 cửa sổ Bcode MỚI chạy song song (mỗi cửa sổ làm 1 dự án — lúc mở có màn hình Projects để chọn).
                StartNewBcodeWindow();
                return true;
        }

        if (id.StartsWith("tab.goto", StringComparison.Ordinal) && int.TryParse(id["tab.goto".Length..], out var gotoNo))
        {
            if (gotoNo < 1 || gotoNo > _documentTabs.TabPages.Count) return false;
            _documentTabs.SelectedIndex = gotoNo - 1;
            _documentTabs.SelectedTab?.Focus();
            return true;
        }

        if (id.StartsWith("bar.group", StringComparison.Ordinal) && int.TryParse(id["bar.group".Length..], out var groupNo))
            return ShowToolGroupMenu(groupNo - 1);

        if (id.StartsWith("tool:", StringComparison.Ordinal))
        {
            var spec = _toolSpecs.FirstOrDefault(t => "tool:" + t.key == id);
            if (spec.action is not null) { spec.action(this, EventArgs.Empty); return true; }
        }
        return false;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (ResultGridMenu.HandleCopyKey(FromHandle(msg.HWnd), keyData))   // lưới kết quả SQL: Ctrl+Shift+C = Copy kèm tên cột (trùng phím mở Command)
            return true;
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

        var control = OpenFileLookupTab(autoLoad: false);
        if (control is null) return;

        var ws = _connections.Current!;
        control.ShowForMenuItem(ws.SourcePath, item.Link, item.SysId);
    }

    /// <summary>F4 ở cây menu: bung thêm 1 tab File Lookup MỚI (không dùng lại tab cũ) cho menu đang chọn, để đặt cạnh nhau so sánh nhiều menu.
    /// Tab thêm này không được theo dõi như tab File Lookup chính (đổi project không tự đổi gốc của nó).</summary>
    private void OpenWCommandItemInNewLookup(WCommandItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Link) && string.IsNullOrWhiteSpace(item.SysId))
        {
            MessageBox.Show(this, $"Menu \"{item.Bar}\" không có Link/SysId gắn với source (có thể là mục nhóm/menu cha).", "wcommand");
            return;
        }
        if (_connections.Current is not { } ws || string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            MessageBox.Show(this, "Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.", "Bcode");
            return;
        }

        var label = !string.IsNullOrWhiteSpace(item.SysId) ? item.SysId : item.Bar;
        var control = CreateExtraLookupTab(ws, $"File Lookup · {label}");
        control.MenuForPage = MenuForLookupPage;
        control.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"), load: false);
        control.ShowForMenuItem(ws.SourcePath, item.Link, item.SysId);
    }

    /// <summary>Ô Search của File Lookup gõ "arcthd1.aspx": menu lá trên cây WCommand có Link trỏ đúng trang đó (ưu tiên menu có sysid) —
    /// tab File Lookup dựng cây bằng đúng Link/SysId của menu như khi bấm menu. Không có menu nào thì null (ô Search lọc tên như cũ).</summary>
    private (string SourceRoot, WCommandItem Menu, int Count)? MenuForLookupPage(string page)
    {
        if (_connections.Current is not { } ws || string.IsNullOrWhiteSpace(ws.SourcePath)) return null;
        var hits = _wcommandTree.FlatLeaves().Select(x => x.Item)
            .Where(i => TableMenuService.PageOf(i.Link).Equals(page, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => string.IsNullOrWhiteSpace(i.SysId) ? 1 : 0).ToList();
        return hits.Count == 0 ? null : (ws.SourcePath, hits[0], hits.Count);
    }

    /// <summary>Menu mobile (cây dvcommand): File Lookup theo sysid trong thư mục MobileService của project (Mobile Path, trống thì đoán cạnh Source Path).
    /// Double-click dùng lại 1 tab "File Lookup · Mobile"; F4 (<paramref name="newTab"/>) mở thêm tab riêng.</summary>
    private void OpenMobileItem(WCommandItem item, bool newTab)
    {
        if (string.IsNullOrWhiteSpace(item.SysId))
        {
            MessageBox.Show(this, $"Menu \"{item.Bar}\" không có sysid gắn với source (có thể là mục nhóm/menu cha).", "dvcommand");
            return;
        }
        var ws = _connections.Current;
        if (MobilePathService.Resolve(ws) is not { } root)
        {
            MessageBox.Show(this, MobilePathService.MissingMessage(ws), "Bcode — Mobile");
            return;
        }

        FileLookupControl control;
        if (!newTab && _mobileLookup is { IsDisposed: false } open && open.Parent is TabPage page && _documentTabs.TabPages.Contains(page))
        {
            control = open;
            _documentTabs.SelectedTab = page;
        }
        else
        {
            control = CreateExtraLookupTab(ws!, $"File Lookup · Mobile · {item.SysId}");
            if (!newTab) _mobileLookup = control;
        }
        if (control.Parent is TabPage tab) tab.Text = $"File Lookup · Mobile · {item.SysId}";
        control.ShowForMenuItem(root, "", item.SysId);
    }

    /// <summary>Tab File Lookup phụ (không được theo dõi như tab chính); F4 ngay trong tab phụ này cũng bung tiếp được.</summary>
    private FileLookupControl CreateExtraLookupTab(Workspace ws, string title)
    {
        var control = new FileLookupControl(_fileLookupService, _scriptFileService, _settings) { ProjectName = ws.Name };
        control.FileActivated += path => OpenFileFromLookup(path);
        control.ExcelToFrxRequested += path => OpenExcelToFrxTab(path, "frx");
        control.ExcelToRptRequested += path => OpenExcelToFrxTab(path, "rpt");
        control.NewLookupRequested += src => OpenLookupCloneOf(src);
        AddDocumentTab(title, control);
        return control;
    }

    /// <summary>F4 trên cây file: mở thêm tab File Lookup giữ nguyên menu / thư mục của tab đang bấm.</summary>
    private void OpenLookupCloneOf(FileLookupControl src)
    {
        if (_connections.Current is not { } ws) return;
        var title = _documentTabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Controls.Contains(src))?.Text ?? "File Lookup";
        var control = CreateExtraLookupTab(ws, title.StartsWith("File Lookup") ? title : "File Lookup");
        control.CloneViewFrom(src);
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

    /// <param name="dropCreate">true (double-click trên cây SQL Object): procedure / function / view / trigger mở dạng IF EXISTS → DROP → GO → CREATE;
    /// false: dạng ALTER như trước. Table luôn là CREATE TABLE kèm khối IF EXISTS / DROP TABLE để trong comment.</param>
    private async Task<ISqlTab?> OpenObjectDefinitionAsync(SqlObjectInfo obj, bool dropCreate = false)
    {
        try
        {
            var key = (obj.FromSysDatabase ? "sys:" : "app:") + obj.QualifiedName;
            string Shape(string text) => dropCreate ? SqlObjectBrowserService.ToDropCreateScript(obj, text)
                : obj.Kind == SqlObjectKind.Table ? SqlObjectBrowserService.WithCommentedTableDrop(obj, text) : text;
            // Có bản lưu trên máy thì hiện ngay, kiểm tra database ngầm; chưa có thì lấy từ database (rồi lưu cho lần sau).
            var cached = _sqlObjectService.TryGetCachedDefinition(obj);
            var definition = Shape(cached?.Text ?? await _sqlObjectService.GetDefinitionAndCacheAsync(obj));
    
            // 1. Nếu Procedure/Bảng này đã có tab đang mở -> Chuyển focus đến tab đó
            if (_objectTabs.TryGetValue(key, out var existingPage) && _documentTabs.TabPages.Contains(existingPage))
            {
                _documentTabs.SelectedTab = existingPage;
                if (existingPage.Controls.OfType<ISqlTab>().FirstOrDefault() is { } existingCtrl)
                {
                    existingCtrl.SetDatabase(obj.FromSysDatabase);
                    await existingCtrl.SetScriptTextAsync(definition);
                    if (cached is not null) _ = RefreshCachedDefinitionAsync(obj, existingCtrl, cached, Shape);
                    return existingCtrl;
                }
            }

            // 2. Nếu là Procedure/Bảng khác -> Khởi tạo một tab SQL Query mới (ISqlTab)
            var control = TakeSqlControl();
            control.SetDatabase(obj.FromSysDatabase);
            control.SetScriptText(definition); // Tự động nạp code vào Monaco khi editor sẵn sàng

            // 3. Đặt tiêu đề tab theo tên QualifiedName (ví dụ: dbo.rs_Transfer$AfterSynchronize)
            var page = AddDocumentTab(obj.QualifiedName, control.AsControl());
            _objectTabs[key] = page;
            page.Disposed += (_, _) => _objectTabs.Remove(key);
            if (cached is not null) _ = RefreshCachedDefinitionAsync(obj, control, cached, Shape);

            return control;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — SQL Object", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }
    /// <summary>Sau khi hiện bản lưu: hỏi database xem object đã đổi chưa. Đổi và bạn chưa sửa gì trong tab → thay bằng bản mới; bạn đã sửa → giữ bản của bạn và báo ở thanh trạng thái.</summary>
    private async Task RefreshCachedDefinitionAsync(SqlObjectInfo obj, ISqlTab control, SqlObjectBrowserService.DefinitionCache cached, Func<string, string> shape)
    {
        try
        {
            var (changed, text) = await _sqlObjectService.RefreshIfChangedAsync(obj, cached);
            if (!changed || control.IsDisposed) return;
            var shown = shape(cached.Text);   // tab hiện bản đã đổi dạng (ALTER / IF EXISTS → CREATE) — so với đúng bản đó
            var current = control.IsReady ? await control.GetScriptTextAsync() : shown;
            if (current == shown)
            {
                await control.SetScriptTextAsync(shape(text));
                PushStatus($"{obj.QualifiedName}: trên database đã có bản mới hơn — tab đã được cập nhật.");
            }
            else PushStatus($"{obj.QualifiedName}: trên database đã có bản mới hơn nhưng bạn đã sửa trong tab nên giữ nguyên — đóng tab rồi mở lại để lấy bản mới.");
        }
        catch { /* offline / lỗi nhẹ — vẫn dùng bản lưu */ }
    }

    private async Task OpenProcedureWithQueryAsync(ISqlTab source, string identifier, bool useSysDatabase)
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

            if (page?.Controls.OfType<ISqlTab>().FirstOrDefault() is { } sql)
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

    /// <summary>Tab FSG FBO của <paramref name="ws"/>: đang mở thì chuyển tới và đi tới menu (chưa đăng nhập thì chờ đăng nhập xong); chưa có thì mở mới, tự đăng nhập rồi vào menu.</summary>
    private async Task OpenMenuInFboTabAsync(Workspace ws, string? url, string note)
    {
        if (_quickLaunchForm is { IsDisposed: false } existing && ReferenceEquals(existing.Workspace, ws) && SelectFormTab(existing))
        {
            await existing.RunMenuAsync(url, note);
            return;
        }
        ShowQuickLaunchTab(ws, url, note);
    }

    /// <summary>Ctrl+4 trên cây menu: mở FSG FBO, đăng nhập rồi vào trang của menu đang chọn (menu nhóm / không có link: chỉ mở FSG FBO).</summary>
    private async Task RunMenuInFboAsync(WCommandItem item)
    {
        var ws = _connections.Current;
        if (ws is null || string.IsNullOrWhiteSpace(ws.LoginWLink)) { OpenQuickLaunchLogin(); return; }   // tự báo thiếu Workspace / Login WLink
        try
        {
            var url = BuildMenuUrl(ws.LoginWLink, item);
            var note = url is null ? $"Menu \"{item.Bar}\" không có link — chỉ mở FSG FBO." : $"Menu: {item.Bar} ({item.WMenuId})";
            await OpenMenuInFboTabAsync(ws, url, note);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — FSG FBO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        ShowQuickLaunchTab(ws);
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

            await OpenMenuInFboTabAsync(ws, url, note);
            Activate();
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
        // "🐞 Debug store" ở Profiler → định nghĩa store được gọi + GO + câu gọi đó, bật Debug từng bước.
        control.DebugRequested += sql => _ = OpenProfilerDebugAsync(control, sql);
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



    /// <summary>Mở file web.config ở gốc Source Path của dự án: BcodeViewer nếu đã khai đường dẫn, không thì tab script trong Bcode (như double-click file ở File Lookup).</summary>
    private void OpenWebConfig()
    {
        if (_connections.Current is not { } ws || string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            MessageBox.Show(this, "Workspace chưa khai báo Source Path.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var webConfigPath = Path.Combine(ws.SourcePath, "web.config");
        if (!File.Exists(webConfigPath))
        {
            MessageBox.Show(this, $"Không tìm thấy file web.config tại:\n{webConfigPath}", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        OpenFileFromLookup(webConfigPath);
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
        menu.Items.Add(new ToolStripMenuItem("Switch Database", null, (_, _) => ToggleActiveDatabase()) { ShortcutKeyDisplayString = "Ctrl+1" });
        menu.Items.Add(new ToolStripMenuItem("SQL Profiler", null, (_, _) => OpenSqlProfilerTab()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tool:sql_profiler") });
        menu.Items.Add(new ToolStripMenuItem("SQL SMS", null, (_, _) => { /* Mở SSMS */ }));
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
        // NHÓM 4: QUẢN LÝ TAB VÀ CỬA SỔ — gom vào 1 menu con (như Tools) cho menu Actions gọn
        // ---------------------------------------------------------
        var tabMenu = new ToolStripMenuItem("Tab & cửa sổ");
        tabMenu.DropDownItems.Add(new ToolStripMenuItem("Close Tab", null, (_, _) => {
            if (_documentTabs.SelectedIndex >= 0) CloseDocumentTab(_documentTabs.SelectedIndex);
        }) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.close") });
        
        tabMenu.DropDownItems.Add(new ToolStripMenuItem("Close All But This", null, (_, _) => {
            var currentIdx = _documentTabs.SelectedIndex;
            for (int i = _documentTabs.TabPages.Count - 1; i >= 0; i--)
                if (i != currentIdx) CloseDocumentTab(i);
        }) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.closeOthers") });
        
        tabMenu.DropDownItems.Add(new ToolStripMenuItem(
            Bcode.App.UI.UiOverrides.Enabled ? "Tuỳ chỉnh giao diện web: đang BẬT (bấm để tắt — chế độ an toàn)" : "Tuỳ chỉnh giao diện web: đang TẮT (bấm để bật)", null,
            (_, _) => ToggleCustomUi()));
        tabMenu.DropDownItems.Add(new ToolStripMenuItem("Hide / Show cây menu", null, (_, _) => ToggleMenuTree()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tree.toggle") });

        tabMenu.DropDownItems.Add(new ToolStripMenuItem("Close Tabs to the Right", null, (_, _) => {
            if (_documentTabs.SelectedTab is not { } cur) return;
            var from = _documentTabs.SelectedIndex;
            CloseTabsWhere(p => _documentTabs.TabPages.IndexOf(p) > from);
        }) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.closeRight") });

        tabMenu.DropDownItems.Add(new ToolStripMenuItem("Close All Tab", null, (_, _) => {
            for (int i = _documentTabs.TabPages.Count - 1; i >= 0; i--)
                CloseDocumentTab(i);
        }) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("tab.closeAll") });
        menu.Items.Add(tabMenu);

        menu.Items.Add(new ToolStripSeparator());

        // ---------------------------------------------------------
        // NHÓM 5: CÁC TIỆN ÍCH KHÁC
        // ---------------------------------------------------------
        menu.Items.Add(new ToolStripMenuItem("Get Key Data/Value", null, (_, _) => { }));
        menu.Items.Add(new ToolStripMenuItem("Clear Structure App", null, (_, _) => ClearStructureApp()));
        menu.Items.Add(new ToolStripMenuItem("Open Web.config", null, (_, _) => OpenWebConfig()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("app.openWebConfig") });
        menu.Items.Add(new ToolStripMenuItem("Refresh Web.config", null, (_, _) => RefreshWebConfig()) { ShortcutKeyDisplayString = Bcode.App.UI.ShortcutRegistry.Display("app.refreshWebConfig") });
        menu.Items.Add(new ToolStripMenuItem("Create Menu", null, async (_, _) => await CreateMenuAsync()));

        // Áp dụng màu nền Dark/Light theo hệ thống theme của Bcode
        Bcode.App.UI.ThemeManager.Apply(menu);

        return menu;
    }
}