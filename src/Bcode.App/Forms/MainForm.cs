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
    private readonly GenUpdateService _genUpdate = new();
    private readonly FileLookupService _fileLookupService = new();
    private readonly ScriptFileService _scriptFileService = new();
    private readonly SnippetLibraryService _snippets;
    private readonly RawSqlService _rawSqlService;
    private readonly TableDataService _tableDataService;
    private readonly LookupService _lookupService;
    private readonly FileReferenceService _fileReferenceService = new();
    private readonly ChangeOwnerService _changeOwnerService;
    private readonly NoteService _noteService = new();

    private readonly ComboBox _wsCombo;
    private readonly TabControl _leftTabs;
    private readonly TabControl _documentTabs;
    private DataTable? _lastQueryResult;

    private readonly ToolStrip _toolsBar = new();
    private readonly List<(string key, string label, EventHandler action)> _toolSpecs = new();
    private TabPage? _fileLookupTabPage;
    private Controls.FileLookupControl? _fileLookupControl;
    private TabPage? _lookupTabPage;
    private TabPage? _fileReferenceTabPage;
    private TabPage _wcommandTab = null!;
    // Note tabs are keyed by note name so re-opening the same note (e.g. "default"
    // via Ctrl+Shift+E) reuses the tab instead of stacking duplicates; "Note (New)"
    // always creates a fresh, not-yet-used name so it never collides with this.
    private readonly Dictionary<string, TabPage> _noteTabs = new();
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
        _rawSqlService = new RawSqlService(_connections, _periods);
        _tableDataService = new TableDataService(_connections, _periods);
        _lookupService = new LookupService(_connections);
        _changeOwnerService = new ChangeOwnerService(_connections);

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
        // Keys/shortcuts mirror FCode's quick-action menu from the user's screenshot:
        // SQL Query=Q, Lookup=L, Table=T, Command=C, WCommand=W, File Lookup=F,
        // File Reference=R, Change Owner=O, Gen Update=U, Note=E, Note (New)=4
        // (all Ctrl+Shift+<key>, wired via ProcessCmdKey below). Per explicit user
        // instruction, Command/WCommand/File Lookup/File Reference/Change Owner do
        // NOT get right-click context-menu entries — toolbar/shortcut only.
        _toolSpecs.Add(("sql_query", "SQL Query (Ctrl+Shift+Q)", (_, _) => OpenFreeScriptTab()));
        _toolSpecs.Add(("lookup", "Lookup (Ctrl+Shift+L)", (_, _) => OpenLookupTab()));
        _toolSpecs.Add(("table", "Table (Ctrl+Shift+T)", (_, _) => OpenTableTab()));
        _toolSpecs.Add(("command", "Command (Ctrl+Shift+C)", (_, _) => OpenSelectBuilderTab()));
        _toolSpecs.Add(("wcommand", "WCommand (Ctrl+Shift+W)", (_, _) => SelectWCommandTab()));
        _toolSpecs.Add(("file_lookup", "File Lookup (Ctrl+Shift+F)", (_, _) => OpenFileLookupTab()));
        _toolSpecs.Add(("file_reference", "File Reference (Ctrl+Shift+R)", (_, _) => OpenFileReferenceTab()));
        _toolSpecs.Add(("change_owner", "Change Owner (Ctrl+Shift+O)", (_, _) => OpenChangeOwnerDialog()));
        _toolSpecs.Add(("gen_update", "Gen Update (Ctrl+Shift+U)", (_, _) => GenUpdateFromLastResult()));
        _toolSpecs.Add(("note", "Note (Ctrl+Shift+E)", (_, _) => OpenNoteTab(NoteService.DefaultNoteName)));
        _toolSpecs.Add(("note_new", "Note (New) (Ctrl+Shift+4)", (_, _) => OpenNoteTab(_noteService.SuggestNewNoteName(WorkspaceName))));
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
        _wcommandTab = wcommandTab;

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
        Bcode.App.UI.ThemeManager.MakeClosable(_documentTabs, CloseDocumentTab);

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

    /// <summary>Closes a document tab (the ✕ drawn by ThemeManager.MakeClosable) — asks first
    /// if it holds an unsaved script, and keeps the File Lookup/SQL Object tab bookkeeping in
    /// sync so a new tab is opened fresh instead of a stale reference being reused.</summary>
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

        _documentTabs.TabPages.RemoveAt(index);
        page.Dispose(); // fires _objectTabs cleanup via the Disposed handler wired when the tab was opened
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

        var control = new FileLookupControl(_fileLookupService, _scriptFileService);
        control.FileActivated += path => OpenFileInScriptTab(path);
        _fileLookupTabPage = AddDocumentTab("File Lookup", control);
        _fileLookupControl = control;
        // App_Data itself is the browse root on the real site (children are
        // Include/Request/Structure/Templates directly under it) — there is no
        // separate "Controllers" folder at this level, so don't hardcode one.
        control.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"));
        return control;
    }

    /// <summary>
    /// "Command" — the structured SELECT/FROM/WHERE/ORDER BY + Run builder (SqlQueryControl).
    /// Fix: this and "SQL Query" below were swapped in the previous push — clicking "SQL
    /// Query" opened this builder and clicking "Command" opened the free-script tool, the
    /// opposite of what the user actually described for "SQL Query" (Open/Save/Execute/Write
    /// Schema/Check Fields/Comment/Uncomment/Options/Default Type/Suggest Param/Caret/Reset
    /// Connection/Result Tab) — that whole toolbar is OpenFreeScriptTab below, not this one.
    /// </summary>
    private void OpenSelectBuilderTab()
    {
        var control = new SqlQueryControl(_sqlQueryService, _genInsert, _genUpdate, _sqlObjectService);
        control.ResultReady += table => _lastQueryResult = table;
        AddDocumentTab("Command", control);
    }

    /// <summary>"SQL Query" — free-form multi-statement SQL script runner (RawSqlControl),
    /// with the full Open/Save/Execute/Write Schema/Check Fields/Comment/Uncomment/Options/
    /// Default Type/Suggest Param/Caret/Reset Connection/Result Tab toolbar. Opens a fresh
    /// tab each time so multiple scripts can be worked on side by side.</summary>
    private void OpenFreeScriptTab()
    {
        var control = new RawSqlControl(_rawSqlService, _sqlObjectService, _lookupService);
        control.ResultReady += table => _lastQueryResult = table;
        control.OpenResultInNewTabRequested += (table, title) =>
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                DataSource = table
            };
            ResultGridMenu.Attach(grid);
            AddDocumentTab(title, grid);
        };
        AddDocumentTab("SQL Query", control);
    }

    /// <summary>"Lookup" — searches/browses SQL objects (see LookupControl). Reused as a
    /// single tab, like File Lookup, since it's a navigational tool you keep coming back to.</summary>
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

    /// <summary>"Table" — direct Excel-like table editor (TableEditControl). Opens a fresh
    /// tab each time, so more than one table can be open/edited at once.</summary>
    private void OpenTableTab()
    {
        var control = new TableEditControl(_tableDataService, _sqlObjectService);
        AddDocumentTab("Table", control);
    }

    /// <summary>"File Reference" — content grep across App_Data source files (see
    /// FileReferenceControl). Reused as a single tab, like File Lookup.</summary>
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

    /// <summary>"Change Owner" — moves a SQL object to a different schema (ChangeOwnerForm/Service).
    /// A modal dialog rather than a tab, matching how it was already built.</summary>
    private void OpenChangeOwnerDialog()
    {
        using var form = new ChangeOwnerForm(_changeOwnerService, _sqlObjectService);
        form.ShowDialog(this);
    }

    /// <summary>"Note" / "Note (New)" — small per-workspace scratch notes (NoteControl/NoteService).
    /// Reuses the tab for a given note name so pressing Ctrl+Shift+E twice doesn't stack
    /// duplicate "default" note tabs; "Note (New)" always gets a fresh, unused name first.</summary>
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

    /// <summary>"Gen Update" from the toolbar/shortcut: generates UPDATE statements for every
    /// row of the last query result (SQL Query/Command/Table), keyed on user-chosen columns —
    /// same logic as SqlQueryControl's grid context-menu "Gen Update", just without needing a
    /// row selection first (works on the whole last result set).</summary>
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

    private void SelectWCommandTab() => _leftTabs.SelectedTab = _wcommandTab;

    private string WorkspaceName => _connections.Current?.Name ?? "";

    /// <summary>
    /// "Mã dự án" quick-pick (Ctrl+F5) — NOT the same feature as the FCode registry
    /// "ConnectStr" trick the user described. That trick relies on decrypting an
    /// FCode-issued encrypted blob (via FastBusiness.Crypto.dll's proprietary algorithm)
    /// to call FCode's own remote project-lookup service — the exact kind of "reverse the
    /// vendor's crypto / talk to their infrastructure" work Bcode already declines to do
    /// (see README's "Về Decrypt SQL Object"), so it isn't reproduced here.
    ///
    /// What Bcode CAN do without any of that: reuse Workspace.ProjectId (already a field on
    /// every saved Workspace, editable in Choose Server/Workspaces) as a locally-owned
    /// project registry. Ctrl+F5 asks for a mã dự án and switches WS to the workspace whose
    /// ProjectId matches — same "gõ mã dự án, tự lấy thông tin dự án" convenience, but backed
    /// entirely by connections Bee already entered, not a decrypted vendor blob.
    /// </summary>
    private void QuickSelectProjectByCode()
    {
        var code = SimplePromptForm.Show(this, "Mã dự án", "Nhập mã dự án (ID) để tự chọn Workspace đã lưu:", "");
        if (string.IsNullOrWhiteSpace(code)) return;

        var match = _settings.Workspaces.FirstOrDefault(w => w.ProjectId.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            var addNew = MessageBox.Show(this,
                $"Không tìm thấy Workspace nào có mã dự án (ID) \"{code}\".\nMở Choose Server / Workspaces để thêm mới?",
                "Bcode — Mã dự án", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (addNew == DialogResult.Yes) OpenConnectionSettings();
            return;
        }

        var idx = _wsCombo.Items.IndexOf(match);
        if (idx >= 0) _wsCombo.SelectedIndex = idx;
    }

    /// <summary>
    /// Global Ctrl+Shift+&lt;key&gt; shortcuts matching FCode's quick-action menu, since
    /// ToolStripButton.ShortcutKeys (unlike a MenuStrip item's) aren't processed by the
    /// WinForms message loop on their own — this is what actually makes them work anywhere
    /// in the window, not just when a ToolStrip has focus.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F5))
        {
            QuickSelectProjectByCode();
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
                case Keys.E: OpenNoteTab(NoteService.DefaultNoteName); return true;
                case Keys.D4: OpenNoteTab(_noteService.SuggestNewNoteName(WorkspaceName)); return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Clicking a WCommand menu node opens (or reuses) the File Lookup tab, showing
    /// exactly the source for that menu item the way the menu itself is wired to it:
    /// <c>link</c> is the page file under the site's "main" folder, and <c>sysid</c>
    /// is the controller folder under App_Data\Controllers holding that page's source
    /// files — no guessing by file-name search.
    /// </summary>
    private void OpenWCommandItem(WCommandItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Link) && string.IsNullOrWhiteSpace(item.SysId))
        {
            MessageBox.Show(this, $"Menu \"{item.Bar}\" không có Link/SysId gắn với source (có thể là mục nhóm/menu cha).", "wcommand");
            return;
        }

        var control = OpenFileLookupTab();
        if (control is null) return;

        var ws = _connections.Current!; // OpenFileLookupTab already validated SourcePath is present
        control.ShowForMenuItem(ws.SourcePath, item.Link, item.SysId);
    }

    private void OpenFileInScriptTab(string path)
    {
        try
        {
            var content = _scriptFileService.ReadFile(path);
            var editor = new ScriptEditorControl();
            editor.EntityNavigationRequested += OpenFileInScriptTab; // F12 on &Entity; -> open its Include file in its own tab
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
