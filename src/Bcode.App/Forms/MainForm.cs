using System.Data;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;

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
    private readonly FileLookupService _fileLookupService = new();
    private readonly ScriptFileService _scriptFileService = new();
    private readonly SnippetLibraryService _snippets;

    private readonly ComboBox _wsCombo;
    private readonly TabControl _leftTabs;
    private readonly TabControl _documentTabs;
    private DataTable? _lastQueryResult;

    private readonly ToolStrip _toolsBar = new();
    private readonly List<(string key, string label, EventHandler action)> _toolSpecs = new();
    private TabPage? _fileLookupTabPage;
    private Controls.FileLookupControl? _fileLookupControl;
    // Tracks open "SQL Object definition" tabs by qualified name (e.g. "dbo.hddtr00")
    // so clicking the same table/view/proc twice reuses and reloads that one tab
    // instead of stacking up duplicate "dbo.hddtr00" tabs with stale content.
    private readonly Dictionary<string, TabPage> _objectTabs = new();

    public MainForm()
    {
        _settings = AppSettings.Load();
        _wcommandService = new WCommandService(_connections);
        _sqlObjectService = new SqlObjectBrowserService(_connections);
        _sqlQueryService = new SqlQueryService(_connections, _periods);
        _snippets = new SnippetLibraryService(_settings.LibraryPath);

        Text = "Bcode";
        Width = 1280;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;

        // ---- Menu bar ----
        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("File");
        var chooseServer = new ToolStripMenuItem("Choose Server / Workspaces...", null, (_, _) => OpenConnectionSettings());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) => Close());
        fileMenu.DropDownItems.Add(chooseServer);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(exitItem);

        var actionsMenu = new ToolStripMenuItem("Actions");
        actionsMenu.DropDownItems.Add(new ToolStripMenuItem("Backup Database...", null, async (_, _) => await BackupDatabaseAsync()));
        actionsMenu.DropDownItems.Add(new ToolStripMenuItem("Restore Database...", null, (_, _) => MessageBox.Show(this,
            "Restore là thao tác có rủi ro cao (ghi đè database) nên chưa bật sẵn.\nGợi ý cài đặt: dùng RESTORE DATABASE ... FROM DISK, chạy trên kết nối master, " +
            "và bắt xác nhận rõ ràng (gõ lại tên database) trước khi chạy.", "Bcode — Restore Database")));
        actionsMenu.DropDownItems.Add(new ToolStripMenuItem("Attach Database...", null, (_, _) => MessageBox.Show(this,
            "Gợi ý cài đặt: CREATE DATABASE ... ON (FILENAME = '<mdf>') FOR ATTACH, chạy trên kết nối master.", "Bcode — Attach Database")));

        menu.Items.Add(fileMenu);
        menu.Items.Add(actionsMenu);

        // ---- WS selector ----
        _wsCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        foreach (var ws in _settings.Workspaces) _wsCombo.Items.Add(ws);
        _wsCombo.SelectedIndexChanged += (_, _) => OnWorkspaceSelected();
        var wsCombHost = new ToolStripControlHost(_wsCombo) { Alignment = ToolStripItemAlignment.Right };
        menu.Items.Add(new ToolStripLabel("WS:") { Alignment = ToolStripItemAlignment.Right });
        menu.Items.Add(wsCombHost);

        var themeToggle = new ToolStripButton("Dark Theme") { Alignment = ToolStripItemAlignment.Right, CheckOnClick = true, Checked = true };
        themeToggle.Click += (_, _) =>
        {
            Bcode.App.UI.ThemeManager.Toggle(this);
            themeToggle.Text = Bcode.App.UI.AppColors.IsDark ? "Dark Theme" : "Light Theme";
        };
        menu.Items.Add(themeToggle);

        MainMenuStrip = menu;

        // ---- Script toolbar ----
        var scriptBar = new ToolStrip();
        scriptBar.Items.Add(new ToolStripButton("Add Script", null, (_, _) => AddScript()));
        scriptBar.Items.Add(new ToolStripButton("View Script", null, (_, _) => ViewScriptCart()));
        scriptBar.Items.Add(new ToolStripButton("Clear Script", null, (_, _) => _scriptFileService.ClearCart()));
        scriptBar.Items.Add(new ToolStripButton("Save Script", null, (_, _) => SaveActiveScript()));
        scriptBar.Items.Add(new ToolStripButton("Copy Script", null, (_, _) => CopyActiveScript()));

        // ---- Tools toolbar (customizable via Quick Access) ----
        _toolSpecs.Add(("file_lookup", "File Lookup", (_, _) => OpenFileLookupTab()));
        _toolSpecs.Add(("command", "Command", (_, _) => OpenSqlQueryTab()));
        _toolSpecs.Add(("create_processing", "Create Processing", (_, _) => new CreateProcessingForm().ShowDialog(this)));
        _toolSpecs.Add(("check_mail", "Check Mail", (_, _) => new CheckMailForm().ShowDialog(this)));
        _toolSpecs.Add(("compare_text", "Compare Text", (_, _) => new CompareTextForm().ShowDialog(this)));
        _toolSpecs.Add(("string_beauty", "String Beauty", (_, _) => new StringBeautyForm().ShowDialog(this)));
        _toolSpecs.Add(("library", "Library", (_, _) => OpenLibrary()));
        _toolSpecs.Add(("decrypt_sql_object", "Decrypt SQL Object", (_, _) => new DecryptSqlObjectForm(new PassthroughDecryptionProvider()).ShowDialog(this)));
        _toolSpecs.Add(("setup_einvoice", "Setup eInvoice (FE)", (_, _) => new SetupEInvoiceForm().ShowDialog(this)));
        _toolSpecs.Add(("create_rpt_xlsx", "Create *.rpt, *.xlsx", (_, _) => new CreateRptXlsxForm(_lastQueryResult).ShowDialog(this)));
        _toolSpecs.Add(("compare_structure", "Compare Structure", (_, _) => new CompareStructureForm(_settings).ShowDialog(this)));
        _toolSpecs.Add(("view_rpt_fec", "View Rpt in FEC", (_, _) => new ViewRptInFecForm().ShowDialog(this)));
        RebuildToolsBar();

        // ---- Left: category tabs (SQL Object / WCommand / Mobile) ----
        _leftTabs = new TabControl { Dock = DockStyle.Fill };

        var sqlObjectTree = new SqlObjectTreeControl(_sqlObjectService);
        sqlObjectTree.ObjectActivated += async obj => await OpenObjectDefinitionAsync(obj);
        var sqlObjectTab = new TabPage("SQL Object");
        sqlObjectTab.Controls.Add(sqlObjectTree);

        var wcommandTree = new WCommandTreeControl(_wcommandService);
        wcommandTree.NodeActivated += item => OpenWCommandItem(item);
        var wcommandTab = new TabPage("WCommand");
        wcommandTab.Controls.Add(wcommandTree);

        var mobileTab = new TabPage("Mobile");
        mobileTab.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = 120,
            Text = "Mobile: duyệt cấu trúc màn hình app di động (tương đương \"Mobile Path\" trong FCode).\n" +
                   "Dùng lại FileLookupControl trỏ tới thư mục con dành cho mobile trong Source Path khi bạn xác định " +
                   "được đúng quy ước thư mục trên server của mình.",
            Padding = new Padding(6)
        });

        _leftTabs.TabPages.Add(sqlObjectTab);
        _leftTabs.TabPages.Add(wcommandTab);
        _leftTabs.TabPages.Add(mobileTab);
        _leftTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_leftTabs.SelectedTab == wcommandTab) _ = wcommandTree.ReloadAsync();
            if (_leftTabs.SelectedTab == sqlObjectTab) _ = sqlObjectTree.ReloadAsync();
        };

        // ---- Right: open document tabs ----
        _documentTabs = new TabControl { Dock = DockStyle.Fill };

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 320 };
        split.Panel1.Controls.Add(_leftTabs);
        split.Panel2.Controls.Add(_documentTabs);

        Controls.Add(split);
        Controls.Add(_toolsBar);
        Controls.Add(scriptBar);
        Controls.Add(menu);

        if (_wsCombo.Items.Count > 0) _wsCombo.SelectedIndex = 0;
    }

    // ---------------- WS handling ----------------

    private void OnWorkspaceSelected()
    {
        if (_wsCombo.SelectedItem is Workspace ws)
            _connections.SetWorkspace(ws);
    }

    private void OpenConnectionSettings()
    {
        using var form = new ConnectionSettingsForm(_settings, _connections);
        form.ShowDialog(this);

        _wsCombo.Items.Clear();
        foreach (var ws in _settings.Workspaces) _wsCombo.Items.Add(ws);
        if (_wsCombo.Items.Count > 0) _wsCombo.SelectedIndex = 0;
    }

    // ---------------- Tools toolbar / Quick Access ----------------

    private void RebuildToolsBar()
    {
        _toolsBar.Items.Clear();

        var quickAccess = new ToolStripButton("☰ Quick Access") { ToolTipText = "Chọn tính năng hiển thị trên toolbar" };
        quickAccess.Click += (_, _) => OpenQuickAccess();
        _toolsBar.Items.Add(quickAccess);
        _toolsBar.Items.Add(new ToolStripSeparator());

        foreach (var (key, label, action) in _toolSpecs)
        {
            if (_settings.HiddenToolKeys.Contains(key)) continue;
            _toolsBar.Items.Add(new ToolStripButton(label, null, action));
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

    // ---------------- Document tabs ----------------

    private TabPage AddDocumentTab(string title, Control content)
    {
        var page = new TabPage(title);
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        _documentTabs.TabPages.Add(page);
        _documentTabs.SelectedTab = page;
        Bcode.App.UI.ThemeManager.Apply(page); // tab is added after the form's own Load, so theme it explicitly
        return page;
    }

    /// <summary>Opens the File Lookup tab, reusing the existing one if it's still open.</summary>
    private FileLookupControl? OpenFileLookupTab()
    {
        if (_connections.Current is not { } ws || string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            MessageBox.Show(this, "Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.", "Bcode");
            return null;
        }

        if (_fileLookupControl is not null && _fileLookupTabPage is not null && _documentTabs.TabPages.Contains(_fileLookupTabPage))
        {
            _documentTabs.SelectedTab = _fileLookupTabPage;
            return _fileLookupControl;
        }

        var control = new FileLookupControl(_fileLookupService);
        control.FileActivated += path => OpenFileInScriptTab(path);
        _fileLookupTabPage = AddDocumentTab("File Lookup", control);
        _fileLookupControl = control;
        // App_Data itself is the browse root on the real site (children are
        // Include/Request/Structure/Templates directly under it) — there is no
        // separate "Controllers" folder at this level, so don't hardcode one.
        control.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"));
        return control;
    }

    private void OpenSqlQueryTab()
    {
        var control = new SqlQueryControl(_sqlQueryService, _genInsert, _sqlObjectService);
        control.ResultReady += table => _lastQueryResult = table;
        AddDocumentTab("Command", control);
    }

    /// <summary>
    /// Clicking a WCommand menu node opens (or reuses) the File Lookup tab, filtered
    /// to every source file matching that menu's link — same idea as FCode: pick a
    /// menu item and its Controller source pops open in File Lookup for you to browse,
    /// rather than guessing and opening a single file.
    /// </summary>
    private void OpenWCommandItem(WCommandItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Link))
        {
            MessageBox.Show(this, $"Menu \"{item.Bar}\" không có Link gắn với source (có thể là mục nhóm/menu cha).", "wcommand");
            return;
        }

        var control = OpenFileLookupTab();
        if (control is null) return;

        // "Filter/VAInvoiceMultiForm" -> "VAInvoiceMultiForm"
        var term = item.Link.TrimEnd('/', '\\').Split('/', '\\').Last();
        control.SearchFor(term);
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

    private async Task OpenObjectDefinitionAsync(SqlObjectInfo obj)
    {
        try
        {
            // Key includes which database it came from — Sys Data and App Data can
            // both have an object with the same qualified name.
            var key = (obj.FromSysDatabase ? "sys:" : "app:") + obj.QualifiedName;

            var definition = await _sqlObjectService.GetDefinitionAsync(obj);

            if (_objectTabs.TryGetValue(key, out var existingPage) && _documentTabs.TabPages.Contains(existingPage))
            {
                // Reuse the existing tab instead of opening a duplicate — replaces its
                // (possibly stale) content rather than leaving the old one behind.
                if (existingPage.Controls.OfType<ScriptEditorControl>().FirstOrDefault() is { } existingEditor)
                    existingEditor.LoadContent(null, definition);
                _documentTabs.SelectedTab = existingPage;
                return;
            }

            var editor = new ScriptEditorControl();
            editor.LoadContent(null, definition);
            editor.ShowPathBar = _settings.ShowTempContentBar;
            editor.TempBarHidden += () => { _settings.ShowTempContentBar = false; _settings.Save(); };
            var page = AddDocumentTab(obj.QualifiedName, editor);
            _objectTabs[key] = page;
            page.Disposed += (_, _) => _objectTabs.Remove(key);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — SQL Object", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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

    // ---------------- Script bar actions ----------------

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

    // ---------------- Actions menu ----------------

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
}
