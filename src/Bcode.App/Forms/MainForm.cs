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

    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _topBarWeb = new();
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _iconRailWeb = new();
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _statusBarWeb = new();
    private Func<WebMenu> _settingsMenu = null!;
    private SqlObjectTreeControl _sqlObjectTree = null!;
    private WCommandTreeControl _wcommandTree = null!;
    private readonly Panel _leftContentHost = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, Control> _leftSections = new();
    private readonly TabControl _documentTabs;
    private readonly Panel _quickAccessOverlay = new() { BackColor = SystemColors.Control };
    private DataTable? _lastQueryResult;

    private readonly ToolStrip _toolsBar = new();
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

    public MainForm()
    {
        _settings = AppSettings.Load();
        _wcommandService = new WCommandService(_connections);
        _sqlObjectService = new SqlObjectBrowserService(_connections);
        _sqlQueryService = new SqlQueryService(_connections, _periods);
        _snippets = new SnippetLibraryService(_settings.LibraryPath);
        _rawSqlService = new RawSqlService(_connections);
        _tableDataService = new TableDataService(_connections, _periods);
        _lookupService = new LookupService(_connections);
        _changeOwnerService = new ChangeOwnerService(_connections);

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
        _toolSpecs.Add(("note_new", "Note (New)", "4", (_, _) => OpenNoteTab(_noteService.SuggestNewNoteName(WorkspaceName))));
        _toolSpecs.Add(("create_processing", "Create Processing", null, (_, _) => new CreateProcessingForm().ShowDialog(this)));
        _toolSpecs.Add(("check_mail", "Check Mail", null, (_, _) => new CheckMailForm().ShowDialog(this)));
        _toolSpecs.Add(("compare_text", "Compare Text", null, (_, _) => OpenCompareTextTab()));
        _toolSpecs.Add(("string_beauty", "String Beauty", null, (_, _) => new StringBeautyForm().ShowDialog(this)));
        _toolSpecs.Add(("library", "Library...", null, (_, _) => OpenLibrary()));
        _toolSpecs.Add(("decrypt_sql_object", "Decrypt SQL Object", null, (_, _) => new DecryptSqlObjectForm(new PassthroughDecryptionProvider()).ShowDialog(this)));
        _toolSpecs.Add(("setup_einvoice", "Setup eInvoice (FE)", null, (_, _) => new SetupEInvoiceForm(_connections).ShowDialog(this)));
        _toolSpecs.Add(("create_rpt_xlsx", "Create *.rpt, *.xlsx", null, (_, _) => new CreateRptXlsxForm(_lastQueryResult).ShowDialog(this)));
        _toolSpecs.Add(("compare_structure", "Compare Structure", null, (_, _) => new CompareStructureForm(_settings).ShowDialog(this)));
        _toolSpecs.Add(("view_rpt_fec", "View Rpt in FEC", null, (_, _) => new ViewRptInFecForm().ShowDialog(this)));
        RebuildToolsBar();
        _toolsBar.AutoSize = false;
        _toolsBar.Height = 34;

        var headerContainer = new Panel { Dock = DockStyle.Top, Height = _topBarWeb.Height + _toolsBar.Height };
        headerContainer.Controls.Add(_toolsBar);
        headerContainer.Controls.Add(_topBarWeb);

        var sqlObjectTree = new SqlObjectTreeControl(_sqlObjectService) { Dock = DockStyle.Fill };
        sqlObjectTree.ObjectActivated += async obj => await OpenObjectDefinitionAsync(obj);
        _sqlObjectTree = sqlObjectTree;

        var wcommandTree = new WCommandTreeControl(_wcommandService) { Dock = DockStyle.Fill };
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

        _documentTabs = new TabControl { Dock = DockStyle.Fill };
        Bcode.App.UI.ThemeManager.MakeClosable(_documentTabs, CloseDocumentTab);
        _documentTabs.SizeChanged += (_, _) => UpdateQuickAccessOverlayBounds();

        _quickAccessOverlay.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) BuildQuickAccessMenu().Show(_quickAccessOverlay, e.X, e.Y);
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
        
        if (_settings.Workspaces.Count > 0) SelectWorkspace(0);

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
                        case "settings":
                            _settingsMenu().Show(_topBarWeb, 10, _topBarWeb.Height);
                            break;
                        case "quickaccess":
                            OpenQuickAccess();
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
                            SelectWorkspace(root.GetProperty("index").GetInt32());
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
        PushDbNamesToTopBar(ws);   // <-- thêm dòng này
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

    private void OpenConnectionSettings()
    {
        using var form = new ConnectionSettingsForm(_settings, _connections);
        if (form.ShowDialog(this) == DialogResult.OK || _settings.Workspaces.Count > 0)
        {
            PushWorkspacesToTopBar();
            var currentIdx = _connections.Current != null 
                ? _settings.Workspaces.FindIndex(w => w.Name == _connections.Current.Name) 
                : 0;
            
            SelectWorkspace(currentIdx >= 0 ? currentIdx : 0);
        }
    }

    private void PushDbNamesToTopBar(Bcode.App.Models.Workspace ws)
    {
        if (_topBarWeb.CoreWebView2 is null) return;
        var sysArg = System.Text.Json.JsonSerializer.Serialize(ws.SysDatabase);
        var appArg = System.Text.Json.JsonSerializer.Serialize(ws.AppDatabase);
        _ = _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setDbNames && window.setDbNames({sysArg}, {appArg})");
    }
    private void PushWorkspacesToTopBar()
    {
        if (_topBarWeb.CoreWebView2 is null) return;
        var namesArrayJson = System.Text.Json.JsonSerializer.Serialize(
        _settings.Workspaces.Select(w => $"{w.Name} — {w.AppDatabase}").ToArray());
        var arg = System.Text.Json.JsonSerializer.Serialize(namesArrayJson);
        _ = _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setWorkspaces && window.setWorkspaces({arg})");
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

        foreach (var (key, label, shortcut, action) in _toolSpecs)
        {
            if (_settings.HiddenToolKeys.Contains(key)) continue;
            var button = new ToolStripButton(label, null, action) { Margin = new Padding(1, 1, 1, 2) };
            if (shortcut is not null) button.ToolTipText = $"{label} (Ctrl+Shift+{shortcut})";
            _toolsBar.Items.Add(button);
            if (ToolGroupBreaks.Contains(key)) _toolsBar.Items.Add(new ToolStripSeparator());
        }

        Bcode.App.UI.ThemeManager.Apply(_toolsBar);
    }

    private void OpenQuickAccess()
    {
        var allTools = _toolSpecs.Select(t => (t.key, t.label));
        using var form = new QuickAccessForm(allTools, new HashSet<string>(_settings.HiddenToolKeys));
        if (form.ShowDialog(this) != DialogResult.OK) return;

        _settings.HiddenToolKeys = form.HiddenKeys.ToList();
        _settings.Save();
        RebuildToolsBar();
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
        _documentTabs.TabPages.RemoveAt(index);
        page.Dispose();
        UpdateQuickAccessOverlayBounds();
    }

    private void UpdateQuickAccessOverlayBounds()
    {
        var headerHeight = _documentTabs.DisplayRectangle.Top;
        if (headerHeight <= 0) headerHeight = 26;

        var lastTabRight = _documentTabs.TabPages.Count > 0
            ? _documentTabs.GetTabRect(_documentTabs.TabPages.Count - 1).Right
            : 0;

        _quickAccessOverlay.Bounds = new Rectangle(lastTabRight, 0, Math.Max(0, _documentTabs.Width - lastTabRight), headerHeight);
        _quickAccessOverlay.BringToFront();
    }

    private WebMenu BuildQuickAccessMenu()
    {
        var menu = new WebMenu().AddCaption("Mở nhanh");
        foreach (var (_, label, shortcut, action) in _toolSpecs)
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

        var control = new GenUpdatePackageControl(_fileLookupService, ws);
        _genUpdatePackageTabPage = AddDocumentTab("Gen Update", control);
        _genUpdatePackageControl = control;
        return control;
    }

    private void OpenSelectBuilderTab()
    {
        var control = new SqlQueryControl(_sqlQueryService, _genInsert, _genUpdate, _sqlObjectService, _dataScript, _scriptFileService);
        control.ResultReady += table => _lastQueryResult = table;
        AddDocumentTab("Command", control);
    }

    private RawSqlControl OpenFreeScriptTab()
    {
        // Nếu tab SQL Query đã tồn tại thì chỉ cần focus vào nó
        if (_rawSqlTabPage is not null && _documentTabs.TabPages.Contains(_rawSqlTabPage) && _rawSqlControl is not null)
        {
            _documentTabs.SelectedTab = _rawSqlTabPage;
            return _rawSqlControl;
        }

        // Nếu chưa có thì tạo mới tab SQL Query
        _rawSqlControl = CreateFreeScriptControl();
        _rawSqlTabPage = AddDocumentTab("SQL Query", _rawSqlControl);
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
        control.DebugTargetChosen += target => _ = OpenDebugTargetAsync(target);
        return control;
    }

    private async Task OpenDebugTargetAsync(SqlObjectInfo target)
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
                existingControl.SetScriptText(definition);
            }
            return;
        }

        var control = CreateFreeScriptControl();
        control.SetDatabase(target.FromSysDatabase);
        control.SetScriptText(definition);
        var page = AddDocumentTab(target.QualifiedName, control);
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
        AddDocumentTab("Table", control);
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

    private void QuickSelectProjectByCode()
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

        var generated = TryImportFromFCodeConfig(code) ?? GenerateProjectTemplate(code);
        using var editForm = new EditProjectForm(generated, _connections);
        if (editForm.ShowDialog(this) != DialogResult.OK) return;

        _settings.Workspaces.Add(editForm.Result);
        _settings.Save();
        PushWorkspacesToTopBar();
        SelectWorkspace(_settings.Workspaces.Count - 1);
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

    public bool HandleGlobalShortcut(Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F5))
        {
            QuickSelectProjectByCode();
            return true;
        }

        if (keyData == (Keys.Control | Keys.Shift | Keys.F5))
        {
            DebugDecryptConnectStr();
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
                case Keys.D4: OpenNoteTab(_noteService.SuggestNewNoteName(WorkspaceName)); return true;
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
            MessageBox.Show(this, $"Không tìm thấy procedure '{identifier}'.", "Bcode — SQL Query",
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

        var matches = (await _sqlObjectService.ListObjectsAsync(useSysDatabase, name))
            .Where(o => o.Kind == SqlObjectKind.StoredProcedure && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (schema is not null)
        {
            var exact = matches.FirstOrDefault(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        return matches.FirstOrDefault();
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

        foreach (var path in ofd.FileNames)
        {
            _scriptFileService.AddToCart(path);
            OpenFileInScriptTab(path);
        }
    }

    private void ViewScriptCart()
    {
        var editor = new ScriptEditorControl();
        editor.LoadContent(null, _scriptFileService.ViewCartConcatenated());
        editor.ShowPathBar = _settings.ShowTempContentBar;
        editor.TempBarHidden += () => { _settings.ShowTempContentBar = false; _settings.Save(); };
        AddDocumentTab($"Script Cart ({_scriptFileService.Cart.Count})", editor);
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
            var floatForm = new Form
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
}