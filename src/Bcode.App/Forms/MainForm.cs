using System.Data;
using System.Diagnostics;
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

    private readonly ComboBox _wsCombo;
    private readonly TabControl _leftTabs;
    private readonly TabControl _documentTabs;
    // Thin invisible overlay pinned over the blank remainder of _documentTabs' own tab strip
    // (past the last open tab) purely to host a right-click quick-access menu there — see
    // UpdateQuickAccessOverlayBounds for why this is a separate control rather than handling
    // the click on the TabControl itself.
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
    private TabPage _wcommandTab = null!;
    // Note tabs are keyed by note name so re-opening the same note (e.g. "default"
    // via Ctrl+Shift+E) reuses the tab instead of stacking duplicates; "Note (New)"
    // always creates a fresh, not-yet-used name so it never collides with this.
    private readonly Dictionary<string, TabPage> _noteTabs = new();
    // Tracks open "SQL Object definition" tabs by qualified name (e.g. "dbo.hddtr00")
    // so clicking the same table/view/proc twice reuses and reloads that one tab
    // instead of stacking up duplicate "dbo.hddtr00" tabs with stale content.
    private readonly Dictionary<string, TabPage> _objectTabs = new();
    // Separate from _objectTabs above: these are the runnable "procedure + appended test
    // query" tabs opened by Ctrl+Right-click in "SQL Query" (RawSqlControl-based, with the
    // full Execute toolbar), not the read-only ScriptEditorControl definition viewer tabs —
    // the two must not collide/reuse each other's tab, since one can run SQL and the other
    // can't.
    private readonly Dictionary<string, TabPage> _procedureQueryTabs = new();
    // "Debug store/function" opens a runnable tab loaded with just the target object's OWN
    // definition (no appended caller query) — kept in its own dictionary rather than reusing
    // _procedureQueryTabs above, since reusing the same tab across those two different intents
    // would silently drop whichever content (appended test query vs. plain definition) the
    // other flow had put there.
    private readonly Dictionary<string, TabPage> _debugTargetTabs = new();

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
        WindowState = FormWindowState.Maximized; // THÊM DÒNG NÀY: Tự bung full màn hình
        if (Bcode.App.UI.AppIcons.AppIcon is { } appIcon) Icon = appIcon;

        // ---- Menu bar: File/Actions + script actions + WS selector + theme toggle, all in
        // ONE row — matches FCode's own top row, which fuses its menu, "Add Script...Copy
        // Script" buttons and WS selector together instead of stacking them as separate rows.
        // Was 2 rows here (this MenuStrip, plus a standalone "script toolbar" ToolStrip right
        // below it) — merging them frees a full row of vertical space for the tools toolbar.
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
        menu.Items.Add(new ToolStripSeparator());

        // ---- Script actions (used to be their own ToolStrip row) ----
        menu.Items.Add(new ToolStripButton("Add Script", null, (_, _) => AddScript()));
        menu.Items.Add(new ToolStripButton("View Script", null, (_, _) => ViewScriptCart()));
        menu.Items.Add(new ToolStripButton("Clear Script", null, (_, _) => _scriptFileService.ClearCart()));
        menu.Items.Add(new ToolStripButton("Save Script", null, (_, _) => SaveActiveScript()));
        menu.Items.Add(new ToolStripButton("Copy Script", null, (_, _) => CopyActiveScript()));

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

        // ---- Tools toolbar (customizable via Quick Access) ----
        // Short labels only (icon-and-text style like FCode's own quick-action row) — the
        // Ctrl+Shift+<key> shortcut used to be spelled out in every button's text ("SQL Query
        // (Ctrl+Shift+Q)"), which was the main reason only ~7 of these 21 tools fit before the
        // row ran out of width; it's now in each button's tooltip instead (hover to see it),
        // and the actual key handling is unchanged — still wired in ProcessCmdKey below, keyed
        // off "shortcut", not the button text. Per explicit user instruction, Command/WCommand/
        // File Lookup/File Reference/Change Owner do NOT get right-click context-menu entries —
        // toolbar/shortcut only.
        _toolSpecs.Add(("sql_query", "SQL Query", "Q", (_, _) => OpenFreeScriptTab()));
        _toolSpecs.Add(("lookup", "Lookup", "L", (_, _) => OpenLookupTab()));
        _toolSpecs.Add(("table", "Table", "T", (_, _) => OpenTableTab()));
        _toolSpecs.Add(("command", "Command", "C", (_, _) => OpenSelectBuilderTab()));
        _toolSpecs.Add(("wcommand", "WCommand", "W", (_, _) => SelectWCommandTab()));
        _toolSpecs.Add(("file_lookup", "File Lookup", "F", (_, _) => OpenFileLookupTab()));
        _toolSpecs.Add(("gen_update_package", "Gen Update", "G", (_, _) => OpenGenUpdatePackageTab()));
        _toolSpecs.Add(("file_reference", "File Reference", "R", (_, _) => OpenFileReferenceTab()));
        _toolSpecs.Add(("change_owner", "Change Owner", "O", (_, _) => OpenChangeOwnerDialog()));
        // Same underlying "Gen Update" as gen_update_package above but a different feature
        // (this one works off the last query result, no separate tab) — kept "(Result)" in the
        // short label itself, not just the tooltip, so the two aren't ambiguous at a glance now
        // that neither shows its shortcut inline.
        _toolSpecs.Add(("gen_update", "Gen Update (Result)", "U", (_, _) => GenUpdateFromLastResult()));
        _toolSpecs.Add(("note", "Note", "E", (_, _) => OpenNoteTab(NoteService.DefaultNoteName)));
        _toolSpecs.Add(("note_new", "Note (New)", "4", (_, _) => OpenNoteTab(_noteService.SuggestNewNoteName(WorkspaceName))));
        _toolSpecs.Add(("create_processing", "Create Processing", null, (_, _) => new CreateProcessingForm().ShowDialog(this)));
        _toolSpecs.Add(("check_mail", "Check Mail", null, (_, _) => new CheckMailForm().ShowDialog(this)));
        _toolSpecs.Add(("compare_text", "Compare Text", null, (_, _) => new CompareTextForm().ShowDialog(this)));
        _toolSpecs.Add(("string_beauty", "String Beauty", null, (_, _) => new StringBeautyForm().ShowDialog(this)));
        _toolSpecs.Add(("library", "Library...", null, (_, _) => OpenLibrary()));
        _toolSpecs.Add(("decrypt_sql_object", "Decrypt SQL Object", null, (_, _) => new DecryptSqlObjectForm(new PassthroughDecryptionProvider()).ShowDialog(this)));
        _toolSpecs.Add(("setup_einvoice", "Setup eInvoice (FE)", null, (_, _) => new SetupEInvoiceForm().ShowDialog(this)));
        _toolSpecs.Add(("create_rpt_xlsx", "Create *.rpt, *.xlsx", null, (_, _) => new CreateRptXlsxForm(_lastQueryResult).ShowDialog(this)));
        _toolSpecs.Add(("compare_structure", "Compare Structure", null, (_, _) => new CompareStructureForm(_settings).ShowDialog(this)));
        _toolSpecs.Add(("view_rpt_fec", "View Rpt in FEC", null, (_, _) => new ViewRptInFecForm().ShowDialog(this)));
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
        _documentTabs.SizeChanged += (_, _) => UpdateQuickAccessOverlayBounds();

        // Right-click the blank remainder of the tab strip (past the last open tab, or the
        // whole strip when nothing's open yet) for a quick-launch menu of every tool that has
        // its own Ctrl+Shift+<key> shortcut — same spot/idea as FCode's own quick-access popup
        // there, so a tool can be opened without reaching for the toolbar row above.
        //
        // Fix history: (1) a plain MouseUp handler calling menu.Show() manually never fired;
        // (2) assigning ContextMenuStrip straight to _documentTabs (relying on WM_CONTEXTMENU)
        // ALSO never fired — the blank part of a native TabControl's own header row apparently
        // doesn't forward either one for the stock SysTabControl32. _quickAccessOverlay is a
        // separate, perfectly ordinary Panel pinned on top of just that blank strip (see
        // UpdateQuickAccessOverlayBounds) — an ordinary control's own right-click handling is
        // reliable (the same ContextMenuStrip pattern already works fine on _grid and
        // _structureList elsewhere in this app), it just has to not BE the TabControl.
        _quickAccessOverlay.ContextMenuStrip = BuildQuickAccessMenu();

        // FixedPanel = Panel1 pins the tree sidebar to an exact pixel width regardless of how
        // the window is resized/maximized afterwards — without it, the split had been observed
        // ballooning the sidebar to take most of the window on a wide screen instead of staying
        // narrow, since neither panel had an explicit "this one keeps its width" owner.
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            // Was 230 — exactly the combined width of WCommandTreeControl's filter box
            // (160) + Refresh button (70), leaving zero margin for the TabControl's own
            // border/padding, so the filter box's right edge was always getting clipped.
            // That box is now Fill-docked (see WCommandTreeControl) so it no longer
            // overflows at any width, but a bit more room keeps things comfortable.
            SplitterDistance = 260
        };
        split.Panel1.Controls.Add(_leftTabs);
        split.Panel2.Controls.Add(_documentTabs);
        split.Panel2.Controls.Add(_quickAccessOverlay);
        UpdateQuickAccessOverlayBounds();

        Controls.Add(split);
        Controls.Add(_toolsBar);
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

    // Keys after which RebuildToolsBar inserts a separator — breaks the one long unbroken
    // row of 20 buttons into a few logical groups (query/data tools, menu navigation, notes,
    // then everything else) so it reads as sections instead of a wall of text, which was
    // the actual substance of "chưa mượt" (cramped/hard to scan) beyond hover-color hunting.
    private static readonly HashSet<string> ToolGroupBreaks = new() { "command", "gen_update", "note_new" };

    private void RebuildToolsBar()
    {
        _toolsBar.Items.Clear();
        _toolsBar.Padding = new Padding(4, 2, 4, 2);

        var quickAccess = new ToolStripButton("☰ Quick Access") { ToolTipText = "Chọn tính năng hiển thị trên toolbar" };
        quickAccess.Click += (_, _) => OpenQuickAccess();
        _toolsBar.Items.Add(quickAccess);
        _toolsBar.Items.Add(new ToolStripSeparator());

        foreach (var (key, label, shortcut, action) in _toolSpecs)
        {
            if (_settings.HiddenToolKeys.Contains(key)) continue;
            var button = new ToolStripButton(label, null, action) { Margin = new Padding(1, 1, 1, 2) };
            // The shortcut used to be spelled out right in the button text ("SQL Query (Ctrl+
            // Shift+Q)") — moved to the tooltip so the visible label stays short and more
            // buttons fit on the row (see the "Tools toolbar" setup above for why).
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

    // ---------------- Document tabs ----------------

    private TabPage AddDocumentTab(string title, Control content)
    {
        var page = new TabPage(title);
        content.Dock = DockStyle.Fill;
        page.Controls.Add(content);
        _documentTabs.TabPages.Add(page);
        _documentTabs.SelectedTab = page;
        Bcode.App.UI.ThemeManager.Apply(page); // tab is added after the form's own Load, so theme it explicitly
        UpdateQuickAccessOverlayBounds(); // the last tab's right edge just moved
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
        if (page == _genUpdatePackageTabPage)
        {
            _genUpdatePackageTabPage = null;
            _genUpdatePackageControl = null;
        }

        _documentTabs.TabPages.RemoveAt(index);
        page.Dispose(); // fires _objectTabs cleanup via the Disposed handler wired when the tab was opened
        UpdateQuickAccessOverlayBounds(); // the last tab's right edge just moved
    }

    /// <summary>Repositions <see cref="_quickAccessOverlay"/> to exactly cover the blank
    /// remainder of the document tab strip — from the right edge of the last open tab (0 when
    /// none are open) to the far edge of the control, one header row tall — so it never
    /// overlaps an actual tab (which would block normal left-click tab switching) and always
    /// tracks the strip correctly as tabs are added/removed or the window is resized. Called
    /// from every one of those three triggers.</summary>
    private void UpdateQuickAccessOverlayBounds()
    {
        // DisplayRectangle.Top is the header row's own height (where the selected tab's page
        // content begins) — 0 when there are no tabs yet, since TabControl doesn't reserve a
        // header row at all in that state; fall back to a plausible single-row height so the
        // overlay (and its quick-access menu) still exists over an empty tab strip.
        var headerHeight = _documentTabs.DisplayRectangle.Top;
        if (headerHeight <= 0) headerHeight = 26;

        var lastTabRight = _documentTabs.TabPages.Count > 0
            ? _documentTabs.GetTabRect(_documentTabs.TabPages.Count - 1).Right
            : 0;

        _quickAccessOverlay.Bounds = new Rectangle(lastTabRight, 0, Math.Max(0, _documentTabs.Width - lastTabRight), headerHeight);
        _quickAccessOverlay.BringToFront();
    }

    /// <summary>Same tool set ProcessCmdKey's Ctrl+Shift+&lt;key&gt; block wires up (every
    /// _toolSpecs entry that has a shortcut) — reused here instead of duplicating the list,
    /// so a new shortcut-bearing tool automatically shows up in this menu too.</summary>
    private ContextMenuStrip BuildQuickAccessMenu()
    {
        var menu = new ContextMenuStrip();
        foreach (var (_, label, shortcut, action) in _toolSpecs)
        {
            if (shortcut is null) continue;
            var item = new ToolStripMenuItem(label) { ShortcutKeyDisplayString = $"Ctrl+Shift+{shortcut}" };
            item.Click += (_, _) => action(this, EventArgs.Empty);
            menu.Items.Add(item);
        }
        Bcode.App.UI.ThemeManager.ApplyMenu(menu);
        return menu;
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
            // Refreshed on every call (not just at creation) so switching WS while the tab
            // stays open still sends BcodeViewer the right project name to group under.
            _fileLookupControl.ProjectName = ws.Name;
            _documentTabs.SelectedTab = _fileLookupTabPage;
            return _fileLookupControl;
        }

        var control = new FileLookupControl(_fileLookupService, _scriptFileService, _settings);
        control.ProjectName = ws.Name;
        control.FileActivated += path => OpenFileFromLookup(path);
        _fileLookupTabPage = AddDocumentTab("File Lookup", control);
        _fileLookupControl = control;
        // App_Data itself is the browse root on the real site (children are
        // Include/Request/Structure/Templates directly under it) — there is no
        // separate "Controllers" folder at this level, so don't hardcode one.
        control.SetRootPath(Path.Combine(ws.SourcePath, "App_Data"));
        return control;
    }

    /// <summary>Opens the Gen Update (file-packaging) tab, reusing the existing one if still
    /// open — mirrors OpenFileLookupTab's singleton-tab pattern.</summary>
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
        var control = new SqlQueryControl(_sqlQueryService, _genInsert, _genUpdate, _sqlObjectService, _dataScript, _scriptFileService);
        control.ResultReady += table => _lastQueryResult = table;
        AddDocumentTab("Command", control);
    }

    /// <summary>"SQL Query" — free-form multi-statement SQL script runner (RawSqlControl),
    /// with the full Open/Save/Execute/Write Schema/Check Fields/Comment/Uncomment/Options/
    /// Default Type/Suggest Param/Caret/Reset Connection/Result Tab toolbar. Opens a fresh
    /// tab each time so multiple scripts can be worked on side by side.</summary>
    private void OpenFreeScriptTab()
    {
        var control = CreateFreeScriptControl();
        AddDocumentTab("SQL Query", control);
    }

    /// <summary>Builds a RawSqlControl with its standard event wiring (result handling, "Result
    /// Tab" new-tab, Ctrl+Right-click "open procedure") — shared by OpenFreeScriptTab above and
    /// OpenProcedureWithQueryAsync below, since the latter also opens a full RawSqlControl (not
    /// a read-only viewer) so the combined procedure+query script is actually runnable there,
    /// same as any other "SQL Query" tab.</summary>
    private RawSqlControl CreateFreeScriptControl()
    {
        // THÊM _snippets VÀO CUỐI CONSTRUCTOR
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

    /// <summary>"Debug store/function" (RawSqlControl.DebugTargetChosen) — opens/reuses a
    /// runnable tab loaded with the picked object's own definition, on the database it
    /// actually lives in. Doesn't append anything from the caller script — debugging jumps
    /// straight into the target's own body; see OpenProcedureWithQueryAsync above for the
    /// separate "open + append my current query" flow.</summary>
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
        var control = new TableEditControl(_tableDataService, _sqlObjectService, _dataScript, _scriptFileService, _genInsert, _genUpdate);
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
    /// Diagnostic-only (Ctrl+Shift+F5): reads HKCU\SOFTWARE\FCoder\ConnectStr and tries it
    /// against FastBusiness.Crypto.dll's public no-key-argument methods — Bcode references
    /// that DLL as an ordinary library and calls only its public API (see Libs/README.md),
    /// never anything decompiled or patched.
    ///
    /// This DLL predates .NET 8 (it's the same one FCode.exe itself ships with, built years
    /// earlier), so the honest first question is simply "does calling it under .NET 8/CoreCLR
    /// even work at all" before anything gets wired into the real Ctrl+F5 flow — shows the raw
    /// result (or exception) of each candidate call rather than guessing at what a "success"
    /// looks like.
    /// </summary>
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
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>
    /// Clicking a WCommand menu node opens (or reuses) the File Lookup tab, filtered
    /// to every source file matching that menu's link — same idea as FCode: pick a
    /// menu item and its Controller source pops open in File Lookup for you to browse,
    /// rather than guessing and opening a single file.
    /// </summary>
    /// <summary>
    /// Routes a WCommand tree double-click to whichever document tab makes sense to feed —
    /// same as FCode: if "Gen Update" is the currently active tab, the menu item populates
    /// its Source File tree there; otherwise (the common case) it opens/targets File Lookup
    /// as before.
    /// </summary>
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

        var ws = _connections.Current!; // OpenFileLookupTab already validated SourcePath is present
        control.ShowForMenuItem(ws.SourcePath, item.Link, item.SysId);
    }

    /// <summary>Double-click in File Lookup: once BcodeViewer's path is already configured
    /// in Settings ("Edit In" — same <see cref="AppSettings.ViewerExePath"/> the "Edit in
    /// BcodeViewer" button next to File Lookup's preview pane uses), double-click prioritizes
    /// opening the file there directly instead of the app's own internal script tab. Falls
    /// back to the internal tab when BcodeViewer isn't configured/found, same as before.</summary>
    private void OpenFileFromLookup(string path)
    {
        if (!string.IsNullOrWhiteSpace(_settings.ViewerExePath) && File.Exists(_settings.ViewerExePath))
        {
            try
            {
                // args[1] (project name) is what lets BcodeViewer's recent-files panel group
                // this file under the current workspace instead of its own "#Other" catch-all.
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
                // fall through to the internal tab so double-click still does *something*
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

    /// <summary>Opens/reuses the read-only definition viewer tab for a SQL object and returns
    /// its ScriptEditorControl (null on failure, or if an existing tab's editor couldn't be
    /// found). Not used by OpenProcedureWithQueryAsync below anymore — that needs a runnable
    /// RawSqlControl (Execute toolbar), not this viewer, so it builds its own tab via
    /// CreateFreeScriptControl instead.</summary>
    private async Task<ScriptEditorControl?> OpenObjectDefinitionAsync(SqlObjectInfo obj)
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
                _documentTabs.SelectedTab = existingPage;
                if (existingPage.Controls.OfType<ScriptEditorControl>().FirstOrDefault() is not { } existingEditor)
                    return null;
                existingEditor.LoadContent(null, definition);
                return existingEditor;
            }

            var editor = new ScriptEditorControl();
            editor.LoadContent(null, definition);
            editor.ShowPathBar = _settings.ShowTempContentBar;
            editor.TempBarHidden += () => { _settings.ShowTempContentBar = false; _settings.Save(); };
            var page = AddDocumentTab(obj.QualifiedName, editor);
            _objectTabs[key] = page;
            page.Disposed += (_, _) => _objectTabs.Remove(key);
            return editor;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — SQL Object", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
    }

    /// <summary>Ctrl+Right-click on a procedure name in "SQL Query" (RawSqlControl.
    /// OpenProcedureWithQueryRequested) — resolves the clicked identifier to a stored
    /// procedure, opens/reuses a RUNNABLE tab for it (a full RawSqlControl, same as any other
    /// "SQL Query" tab — Open/Save/Execute/Write Schema/Check Fields/.../Result Tab toolbar),
    /// so re-clicking keeps reusing the same tab instead of piling up duplicates, then appends
    /// the script that was open at the time of the click to the end of it after a GO separator
    /// and switches the tab's database combo to match where the procedure lives — matches
    /// FCode's own "mở procedure đó và đưa query hiện tại vào cuối procedure", except FCode's
    /// version can actually be run there too, which is what this used to be missing (it used
    /// to open the read-only ScriptEditorControl viewer via OpenObjectDefinitionAsync instead,
    /// which has no Execute button at all).</summary>
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

    /// <summary>Looks up a stored procedure by the bare or schema-qualified name the user
    /// Ctrl+Right-clicked (e.g. "rs_rptTransactionList" or "dbo.rs_rptTransactionList").
    /// Prefers an exact schema match when one was given in the click; otherwise returns the
    /// first procedure with that name — a name collision across schemas is rare enough not to
    /// warrant a picker dialog here.</summary>
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
