using System.Data;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Controls;

/// <summary>
/// "Command" tool — a free-form, multi-statement SQL script runner (batches
/// separated by "GO", like SSMS/sqlcmd), as opposed to "SQL Query" which is
/// the structured SELECT/FROM/WHERE/ORDER BY builder. Reuses the same
/// syntax-highlighted RichTextBox as ScriptEditorControl.
///
/// Toolbar mirrors FCode's "Query" tab per the user's screenshots:
/// Open | Save | Execute | Write Schema | Check Fields | Comment | Uncomment |
/// Options... | Default Type ▾ | Suggest Param/Caret ☑ | Reset Connection ☑ | Result Tab ☐
///
/// A few of these are necessarily assumption-based (FCode's exact internal
/// behavior wasn't fully visible in the screenshots) — see the doc-comment on
/// each handler below and the README's "Cập nhật gần đây" for what was assumed.
/// </summary>
public class RawSqlControl : UserControl
{
    private readonly RichTextBox _scriptBox;
    private readonly ComboBox _dbCombo;
    private readonly ToolStripComboBox _defaultTypeCombo;
    private readonly ToolStripButton _suggestCheck;
    private readonly ToolStripButton _resetConnCheck;
    private readonly ToolStripButton _resultTabCheck;
    private readonly MultiResultView _resultView;
    private readonly Label _statusLabel;
    private LineNumberGutter _lineGutter = null!;
    private readonly RawSqlService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly LookupService _lookupService;

    private readonly SnippetLibraryService _snippets;
    private readonly System.Windows.Forms.Timer _highlightDebounce;

    private UndoRedoTracker _undoRedo = null!;
    private string? _currentFilePath;
    private SqlConnection? _persistentConn;
    private bool _persistentConnUsesSys;
    private List<string> _tableNameCache = new();
    private readonly Dictionary<string, List<string>> _columnCache = new();
    private ListBox? _suggestPopup;

    private static readonly Regex TableRefRegex = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO)\s+(\[?[\w$]+\]?(?:\.\[?[\w$]+\]?)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RawSqlControl(RawSqlService service, SqlObjectBrowserService sqlObjectService, LookupService lookupService, SnippetLibraryService snippets)
    {
        _service = service;
        _sqlObjectService = sqlObjectService;
        _lookupService = lookupService;
        _snippets = snippets; // LƯU LẠI BIẾN
        Dock = DockStyle.Fill;

        // ---- Toolbar ----
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };

        var openBtn = new ToolStripButton("Open") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        openBtn.Click += (_, _) => OpenFile();
        var saveBtn = new ToolStripButton("Save") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        saveBtn.Click += (_, _) => SaveFile();
        var runBtn = new ToolStripButton("▶ Execute (F5)") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        runBtn.Click += async (_, _) => await RunAsync();

        // "Debug store/function" + "Debug từng bước" — matches FCode's own toolbar (Run |
        // Cancel | Debug store/function | Debug từng bước | ...). Debug store/function scans
        // the script for EXEC/function calls and hands off to MainForm to open whichever one
        // the user picks in a runnable tab (see DebugTargetChosen below) — the actual
        // Start/Step/Continue execution engine is a separate, later piece of work; for now
        // Debug từng bước previews the line-level breakpoint analysis (SqlLineAnalyzer) that
        // engine will stand on, so it can be checked against real procedures before the engine
        // itself is built on top of it.
        var debugTargetBtn = new ToolStripButton("Debug store/function") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        debugTargetBtn.Click += async (_, _) => await PickDebugTargetAsync();
        var debugStepBtn = new ToolStripButton("Debug từng bước") { DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = true };
        debugStepBtn.CheckedChanged += (_, _) => ToggleSafeLinePreview(debugStepBtn.Checked);

        var writeSchemaBtn = new ToolStripButton("Write Schema") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        writeSchemaBtn.Click += async (_, _) => await WriteSchemaAsync();
        var checkFieldsBtn = new ToolStripButton("Check Fields") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        checkFieldsBtn.Click += async (_, _) => await CheckFieldsAsync();
        var commentBtn = new ToolStripButton("Comment") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        commentBtn.Click += (_, _) => ToggleComment(true);
        var uncommentBtn = new ToolStripButton("Uncomment") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        uncommentBtn.Click += (_, _) => ToggleComment(false);

        var optionsBtn = new ToolStripDropDownButton("Options...");
        var wordWrapItem = new ToolStripMenuItem("Word Wrap") { CheckOnClick = true };
        wordWrapItem.Click += (_, _) => _scriptBoxWordWrapToggle(wordWrapItem.Checked);
        var fontBiggerItem = new ToolStripMenuItem("Tăng cỡ chữ");
        var fontSmallerItem = new ToolStripMenuItem("Giảm cỡ chữ");
        optionsBtn.DropDownItems.Add(wordWrapItem);
        optionsBtn.DropDownItems.Add(fontBiggerItem);
        optionsBtn.DropDownItems.Add(fontSmallerItem);

        _defaultTypeCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        _defaultTypeCombo.Items.AddRange(new object[] { "Default Type", "UPPER Keyword", "lower Keyword" });
        _defaultTypeCombo.SelectedIndex = 0;
        _defaultTypeCombo.SelectedIndexChanged += (_, _) => ApplyDefaultTypeChoice();

        _suggestCheck = new ToolStripButton("Suggest Param/Caret") { DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = true, Checked = true };
        _resetConnCheck = new ToolStripButton("Reset Connection") { DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = true, Checked = true };
        _resetConnCheck.CheckedChanged += (_, _) => { if (_resetConnCheck.Checked) DisposePersistentConnection(); };
        _resultTabCheck = new ToolStripButton("Result Tab") { DisplayStyle = ToolStripItemDisplayStyle.Text, CheckOnClick = true, Checked = false };

        bar.Items.Add(openBtn);
        bar.Items.Add(saveBtn);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(runBtn);
        bar.Items.Add(debugTargetBtn);
        bar.Items.Add(debugStepBtn);
        bar.Items.Add(writeSchemaBtn);
        bar.Items.Add(checkFieldsBtn);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(commentBtn);
        bar.Items.Add(uncommentBtn);
        bar.Items.Add(new ToolStripSeparator());
        bar.Items.Add(optionsBtn);
        bar.Items.Add(_defaultTypeCombo);
        bar.Items.Add(_suggestCheck);
        bar.Items.Add(_resetConnCheck);
        bar.Items.Add(_resultTabCheck);
        Bcode.App.UI.ThemeManager.Apply(bar);

        var dbBar = new Panel { Dock = DockStyle.Top, Height = 28 };
        _dbCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Dock = DockStyle.Left };
        _dbCombo.Items.AddRange(new object[] { "App Data", "Sys Data" });
        _dbCombo.SelectedIndex = 0;
        _dbCombo.SelectedIndexChanged += (_, _) => DisposePersistentConnection(); // a stale persistent conn would target the wrong DB
        dbBar.Controls.Add(_dbCombo);

        // ---- Script box ----
        _scriptBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 10f),
            AcceptsTab = true,
            Text = "-- Viết 1 hoặc nhiều câu lệnh SQL, cách nhau bằng dòng GO nếu cần nhiều batch.\r\nSELECT TOP 100 * FROM sys.tables;"
        };
        _undoRedo = new UndoRedoTracker(_scriptBox);
        _highlightDebounce = new System.Windows.Forms.Timer { Interval = 400 };
        _highlightDebounce.Tick += async (_, _) =>
        {
            _highlightDebounce.Stop();
            // ApplyAsync (not the synchronous Apply used elsewhere in this file) — pasting a
            // few hundred lines was visibly laggier here than FCode's own paste, since Apply()
            // builds the RTF string on the UI thread before its (unavoidably blocking) native
            // box.Rtf assignment. ApplyAsync moves that prep off-thread.
            await SqlSyntaxHighlighter.ApplyAsync(_scriptBox);
            _undoRedo.Checkpoint(); // one undo step per "pause in typing"
            if (_safeLinePreviewOn) ToggleSafeLinePreview(true); // keep the breakpoint-dot preview live as the script changes
        };
        _scriptBox.HandleCreated += (_, _) =>
        {
            SqlSyntaxHighlighter.DisableNativeUndo(_scriptBox);
            SqlSyntaxHighlighter.Apply(_scriptBox);
        };
        _scriptBox.TextChanged += (_, _) =>
        {
            _highlightDebounce.Stop();
            _highlightDebounce.Start();
        };

        fontBiggerItem.Click += (_, _) => _scriptBox.Font = new Font(_scriptBox.Font.FontFamily, _scriptBox.Font.Size + 1f);
        fontSmallerItem.Click += (_, _) => _scriptBox.Font = new Font(_scriptBox.Font.FontFamily, Math.Max(6f, _scriptBox.Font.Size - 1f));

        _scriptBox.KeyDown += async (_, e) =>
        {
            // Native RichTextBox Undo is disabled (see SqlSyntaxHighlighter.DisableNativeUndo) —
            // UndoRedoTracker is the only thing handling Ctrl+Z/Ctrl+Y now, so intercept both here.
            if (e.Control && !e.Shift && e.KeyCode == Keys.Z)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _undoRedo.Undo();
                return;
            }
            if ((e.Control && e.KeyCode == Keys.Y) || (e.Control && e.Shift && e.KeyCode == Keys.Z))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _undoRedo.Redo();
                return;
            }

            if (_suggestPopup is { Visible: true })
            {
                if (e.KeyCode == Keys.Down) { e.Handled = true; MoveSuggestSelection(1); return; }
                if (e.KeyCode == Keys.Up) { e.Handled = true; MoveSuggestSelection(-1); return; }
                if (e.KeyCode is Keys.Enter or Keys.Tab) { e.Handled = true; e.SuppressKeyPress = true; AcceptSuggestion(); return; }
                if (e.KeyCode == Keys.Escape) { e.Handled = true; HideSuggestions(); return; }
            }

            if (e.Control && e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await ShowSuggestionsAsync();
                return;
            }

            if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.Enter))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await RunAsync();
                return;
            }
        };
        _scriptBox.KeyPress += (_, _) => HideSuggestions();
        _scriptBox.LostFocus += (_, _) => HideSuggestions();

        // "Thêm chức năng ctrl + Chuột phải vào tên procedure sẽ tự mở procedure đó và đưa
        // query hiện tại vào cuối procedure tương tự fcode." Assigning a ContextMenuStrip
        // replaces RichTextBox's built-in OS Undo/Cut/Copy/Paste/Select All menu, so this one
        // doubles as that (a plain right-click still needs *some* menu) — it just cancels
        // itself when Ctrl is held, so it doesn't pop up on top of the Ctrl+Right-click action
        // handled by MouseDown below instead.
        var scriptMenu = new ContextMenuStrip();
        var toolsItem = new ToolStripMenuItem("Tools");
        var beautyFormatItem = new ToolStripMenuItem("Beauty Format", null, (_, _) => BeautyFormat());
        var undoItem = new ToolStripMenuItem("Undo", null, (_, _) => _undoRedo.Undo());
        var cutItem = new ToolStripMenuItem("Cut", null, (_, _) => _scriptBox.Cut());
        var copyItem = new ToolStripMenuItem("Copy", null, (_, _) => _scriptBox.Copy());
        var pasteItem = new ToolStripMenuItem("Paste", null, (_, _) => _scriptBox.Paste());
        var selectAllItem = new ToolStripMenuItem("Select All", null, (_, _) => _scriptBox.SelectAll());
        scriptMenu.Items.Add(toolsItem);
        scriptMenu.Items.Add(new ToolStripSeparator());
        scriptMenu.Items.Add(undoItem);
        scriptMenu.Items.Add(new ToolStripSeparator());
        scriptMenu.Items.Add(cutItem);
        scriptMenu.Items.Add(copyItem);
        scriptMenu.Items.Add(pasteItem);
        scriptMenu.Items.Add(new ToolStripSeparator());
        scriptMenu.Items.Add(selectAllItem);
        scriptMenu.Items.Add(new ToolStripSeparator());
        scriptMenu.Items.Add(beautyFormatItem);

        scriptMenu.Opening += (_, e) =>
        {
            if (Control.ModifierKeys == Keys.Control) { e.Cancel = true; return; }
            cutItem.Enabled = _scriptBox.SelectionLength > 0;
            copyItem.Enabled = _scriptBox.SelectionLength > 0;
            pasteItem.Enabled = Clipboard.ContainsText();

            // Tự động load danh sách từ Library vào menu Tools
            toolsItem.DropDownItems.Clear();
            if (_snippets == null || _snippets.Snippets.Count == 0)
            {
                toolsItem.DropDownItems.Add(new ToolStripMenuItem("(Chưa có cấu hình - Mở Library...)") { Enabled = false });
            }
            else
            {
                foreach (var group in _snippets.Snippets.GroupBy(s => s.Category))
                {
                    if (string.IsNullOrWhiteSpace(group.Key) || group.Key == "General")
                    {
                        foreach (var snippet in group)
                        {
                            toolsItem.DropDownItems.Add(new ToolStripMenuItem(snippet.Name, null, (_, _) => InsertText(snippet.Content)));
                        }
                    }
                    else
                    {
                        var catItem = new ToolStripMenuItem(group.Key);
                        foreach (var snippet in group)
                        {
                            catItem.DropDownItems.Add(new ToolStripMenuItem(snippet.Name, null, (_, _) => InsertText(snippet.Content)));
                        }
                        toolsItem.DropDownItems.Add(catItem);
                    }
                }
            }
            
            // Đảm bảo menu con được áp dụng Dark Theme
            Bcode.App.UI.ThemeManager.ApplyMenu(scriptMenu);
        };
        _scriptBox.ContextMenuStrip = scriptMenu;

        _scriptBox.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || Control.ModifierKeys != Keys.Control) return;
            var charIndex = _scriptBox.GetCharIndexFromPosition(e.Location);
            var identifier = GetIdentifierAt(charIndex);
            if (string.IsNullOrWhiteSpace(identifier)) return;
            OpenProcedureWithQueryRequested?.Invoke(identifier, UseSysDatabase, _scriptBox.Text);
        };

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };

        // "nếu 1 procedure có trả ra nhiều kết quả nhiều bảng thì Bcode hiện chỉ trả ra có 1
        // bảng" — a single batch (e.g. one EXEC of a procedure with several SELECTs inside it)
        // can produce several result sets; MultiResultView stacks one grid per table (each
        // already AutoSizeColumnsMode.None + GridDisplayHelper.BindOptimized internally, so the
        // earlier "đơ khi cuộn" scroll fix still applies to every one of them, not just a
        // single grid).
        _resultView = new MultiResultView { Dock = DockStyle.Fill };

        // "ô query ko cho kéo lại size giữa ô query và tab result nhỉ" — the script box used to
        // be a fixed-Height (220px) Panel with the grid just filling whatever was left below
        // it, so there was no boundary to drag. A SplitContainer gives that boundary: Panel1
        // hosts the script box, Panel2 hosts the status label + result grid, and the splitter
        // between them is user-draggable. SplitterDistance can't be set until the control has
        // a real size (throws otherwise, since it validates against current Width/Height), so
        // it's set once on HandleCreated instead, guarded in case the host still hasn't laid it
        // out yet at that point either.
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        split.Panel1MinSize = 80;
        split.Panel2MinSize = 80;
        split.Panel1.Controls.Add(_scriptBox);
        // "sql query cho thêm số dòng bên trái như fcode để dễ debug" — LineNumberGutter
        // already existed (its own doc-comment even calls out RawSqlControl's script box as
        // the intended target: "Assumes WordWrap is off on the target (RawSqlControl's script
        // box already sets this)") but was never actually wired up anywhere. Dock=Left, added
        // after _scriptBox so it claims the left edge and the script box fills what's left.
        _lineGutter = new LineNumberGutter { Dock = DockStyle.Left };
        _lineGutter.Attach(_scriptBox);
        split.Panel1.Controls.Add(_lineGutter);
        split.Panel2.Controls.Add(_resultView);
        split.Panel2.Controls.Add(_statusLabel);
        split.HandleCreated += (_, _) =>
        {
            try { split.SplitterDistance = 220; } catch { /* control not sized yet on some hosts — default 50/50 split is fine */ }
        };

        Controls.Add(split);
        Controls.Add(dbBar);
        Controls.Add(bar);

        _suggestPopup = new ListBox { Width = 240, Height = 150, Visible = false };
        _suggestPopup.Click += (_, _) => AcceptSuggestion();
        Controls.Add(_suggestPopup);
        _suggestPopup.BringToFront();

        Disposed += (_, _) =>
        {
            _highlightDebounce.Stop();
            _highlightDebounce.Dispose();
            DisposePersistentConnection();
        };
    }

    private void _scriptBoxWordWrapToggle(bool wrap)
    {
        _scriptBox.WordWrap = wrap;
        _scriptBox.ScrollBars = wrap ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
    }

    /// <summary>Fired after a run produces at least one result set — carries the LAST table
    /// across every batch/result set that ran, for callers like Gen Insert/Update or Create
    /// *.xlsx that only ever work off a single DataTable. When a run produced more than one
    /// table (see OpenResultInNewTabRequested/MultiResultView for the full set), which table
    /// ends up "last" depends on script order — e.g. an EXEC returning a small trailing
    /// summary result after its main data table means that summary table is what these
    /// single-table callers would see, not the main one; there's no independent signal here
    /// for which table the user actually means as "the" result.</summary>
    public event Action<DataTable>? ResultReady;

    /// <summary>Fired when "Result Tab" is checked and a run returns at least one result set —
    /// MainForm opens a new document tab hosting every table (in order) instead of showing
    /// them in this control's inline MultiResultView.</summary>
    public event Action<List<DataTable>, string>? OpenResultInNewTabRequested;

    /// <summary>Ctrl+Right-click on an identifier in the script (typically a stored-procedure
    /// name inside an EXEC call) — carries the clicked identifier, which database (App/Sys
    /// Data) this tab is currently pointed at, and this control's current script text.
    /// MainForm resolves the identifier to a SqlObjectInfo, opens/reuses its definition tab,
    /// and appends the script to the end of it — matches FCode's own "mở procedure đó và đưa
    /// query hiện tại vào cuối procedure".</summary>
    public event Action<string, bool, string>? OpenProcedureWithQueryRequested;

    /// <summary>Fired when the user picks a candidate in the "Debug store/function" dialog —
    /// carries the resolved target (a stored procedure or function found via EXEC/call
    /// somewhere in this script). MainForm opens it in its own runnable tab, same as the
    /// Ctrl+Right-click "open procedure" flow, but WITHOUT appending this script's own text —
    /// debugging jumps into the target's own body, it doesn't test it against this caller.</summary>
    public event Action<Bcode.App.Models.SqlObjectInfo>? DebugTargetChosen;

    private bool UseSysDatabase => _dbCombo.SelectedIndex == 1;

    // ---------------- Debug store/function ----------------

    private async Task PickDebugTargetAsync()
    {
        var scanner = new DebugTargetScanner(_sqlObjectService);
        List<Bcode.App.Models.DebugCandidate> candidates;
        try
        {
            candidates = await scanner.ScanAsync(_scriptBox.Text, UseSysDatabase);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Debug store/function", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (candidates.Count == 0)
        {
            MessageBox.Show(this,
                "Không tìm thấy câu EXEC store hoặc gọi function nào trong script hiện tại để debug.",
                "Bcode — Debug store/function", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var form = new Bcode.App.Forms.ChooseDebugTargetForm(candidates);
        if (form.ShowDialog(this) == DialogResult.OK && form.Selected is { } chosen)
            DebugTargetChosen?.Invoke(chosen.Target);
    }

    /// <summary>Toggles a preview of SqlLineAnalyzer.FindSafeLines in the gutter (red dots on
    /// every line the future Step/Continue engine would be allowed to stop at) — lets this be
    /// checked against real, already-written procedures before the actual stepping engine is
    /// built on top of it. Not the real debugger yet: clicking a dot here doesn't run anything.</summary>
    private bool _safeLinePreviewOn;
    private void ToggleSafeLinePreview(bool on)
    {
        _safeLinePreviewOn = on;
        if (on)
        {
            var safeLines = SqlLineAnalyzer.FindSafeLines(_scriptBox.Text);
            _lineGutter.BreakpointLines = safeLines;
            _statusLabel.ForeColor = Color.DimGray;
            _statusLabel.Text = $"Xem trước điểm dừng debug: {safeLines.Count} dòng an toàn để dừng (chưa chạy thật — phần Step/Continue sẽ làm ở bản sau).";
        }
        else
        {
            _lineGutter.BreakpointLines = null;
        }
        _lineGutter.RefreshMarkers();
    }

    // ---------------- Execute ----------------

    private async Task RunAsync()
    {
        _statusLabel.Text = "Đang chạy...";
        try
        {
            var useSys = UseSysDatabase;
            var results = _resetConnCheck.Checked
                ? await RunWithFreshConnectionAsync(useSys)
                : await RunWithPersistentConnectionAsync(useSys);

            var errorBatch = results.FirstOrDefault(r => r.Error is not null);
            // Flatten every result set from every batch, in order — a single batch (e.g. one
            // EXEC of a procedure with several SELECTs inside it) can itself contribute more
            // than one table; see RawSqlService.RunBatchesAsync.
            var allTables = results.SelectMany(r => r.Tables).ToList();
            var lastTable = allTables.LastOrDefault();

            if (allTables.Count > 0)
            {
                if (_resultTabCheck.Checked)
                    OpenResultInNewTabRequested?.Invoke(allTables, "Command Result");
                else
                    _resultView.SetTables(allTables);
                if (lastTable is not null)
                    ResultReady?.Invoke(lastTable);
            }
            else if (!_resultTabCheck.Checked)
            {
                _resultView.Clear();
            }

            var totalAffected = results.Sum(r => r.RowsAffected);
            var summary = $"{results.Count} batch đã chạy" +
                           (allTables.Count > 0
                               ? allTables.Count == 1
                                   ? $" · {allTables[0].Rows.Count} dòng kết quả"
                                   : $" · {allTables.Count} bảng kết quả ({allTables.Sum(t => t.Rows.Count)} dòng)"
                               : "") +
                           (totalAffected > 0 ? $" · {totalAffected} dòng bị ảnh hưởng (INSERT/UPDATE/DELETE)" : "") +
                           (_resetConnCheck.Checked ? "" : " · [Reset Connection tắt: giữ nguyên connection/#temp table giữa các lần chạy]");

            if (errorBatch is not null)
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = $"Lỗi ở batch: {errorBatch.Error}";
                MessageBox.Show(this, errorBatch.Error, "Bcode — Command", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                _statusLabel.ForeColor = Color.DimGray;
                _statusLabel.Text = summary;
            }
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — Command", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private Task<List<RawSqlService.BatchResult>> RunWithFreshConnectionAsync(bool useSys)
    {
        DisposePersistentConnection();
        return _service.ExecuteScriptAsync(_scriptBox.Text, useSys);
    }

    private async Task<List<RawSqlService.BatchResult>> RunWithPersistentConnectionAsync(bool useSys)
    {
        if (_persistentConn is null || _persistentConnUsesSys != useSys || _persistentConn.State != ConnectionState.Open)
        {
            DisposePersistentConnection();
            _persistentConn = _service.CreateConnection(useSys);
            await _persistentConn.OpenAsync();
            _persistentConnUsesSys = useSys;
        }
        return await _service.ExecuteScriptOnAsync(_scriptBox.Text, _persistentConn);
    }

    private void DisposePersistentConnection()
    {
        _persistentConn?.Dispose();
        _persistentConn = null;
    }

    /// <summary>Replaces the script text programmatically — used by MainForm's Ctrl+Right-click
    /// "open procedure + append query" feature to fill a freshly-created (or reused) tab with
    /// the procedure's definition plus the appended test query, same as OpenFile above does for
    /// a file it just read from disk (re-highlights, and resets undo so Ctrl+Z doesn't try to
    /// undo back to whatever was here before, e.g. blank on a brand-new tab).</summary>
    public void SetScriptText(string text)
    {
        _scriptBox.Text = text;
        SqlSyntaxHighlighter.Apply(_scriptBox);
        _undoRedo.ResetBaseline();
    }

    /// <summary>Switches the "App Data"/"Sys Data" combo — used by the same Ctrl+Right-click
    /// feature so the opened tab targets whichever database the resolved procedure actually
    /// lives in, instead of always defaulting to "App Data".</summary>
    public void SetDatabase(bool useSysDatabase)
    {
        _dbCombo.SelectedIndex = useSysDatabase ? 1 : 0;
    }

    // ---------------- Open / Save ----------------

    private void OpenFile()
    {
        using var ofd = new OpenFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _scriptBox.Text = File.ReadAllText(ofd.FileName);
            _currentFilePath = ofd.FileName;
            SqlSyntaxHighlighter.Apply(_scriptBox);
            _undoRedo.ResetBaseline(); // opening a different file isn't an "undo-able edit" of the old one
            _statusLabel.Text = $"Đã mở {ofd.FileName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Open", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveFile()
    {
        if (_currentFilePath is null)
        {
            using var sfd = new SaveFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*", FileName = "script.sql" };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            _currentFilePath = sfd.FileName;
        }
        try
        {
            File.WriteAllText(_currentFilePath, _scriptBox.Text);
            _statusLabel.Text = $"Đã lưu {_currentFilePath}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Save", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------------- Write Schema ----------------

    /// <summary>
    /// Inserts the CREATE TABLE (+ indexes/triggers) script of a referenced table as a
    /// comment block at the caret — handy to check column names/types while writing the
    /// rest of the script. Guesses the table from the first FROM/JOIN/UPDATE/INTO in the
    /// script; falls back to asking, since FCode's exact trigger for this wasn't visible
    /// in the screenshots (assumption — see README).
    /// </summary>
    private async Task WriteSchemaAsync()
    {
        var guess = TableRefRegex.Match(_scriptBox.Text) is { Success: true } m
            ? m.Groups[1].Value.Replace("[", "").Replace("]", "")
            : "";

        var input = Bcode.App.Forms.SimplePromptForm.Show(this, "Write Schema", "Tên bảng cần lấy CREATE TABLE:", guess);
        if (string.IsNullOrWhiteSpace(input)) return;

        var (schema, table) = ParseTableRef(input);
        try
        {
            var obj = new SqlObjectInfo { Schema = schema, Name = table, Kind = SqlObjectKind.Table, FromSysDatabase = UseSysDatabase };
            var ddl = await _sqlObjectService.GetDefinitionAsync(obj);
            var block = "\r\n-- ===== Write Schema: " + obj.QualifiedName + " =====\r\n" +
                        string.Join("\r\n", ddl.Split('\n').Select(l => "-- " + l.TrimEnd('\r'))) +
                        "\r\n-- ===== hết Write Schema =====\r\n";
            _scriptBox.Select(_scriptBox.SelectionStart, 0);
            _scriptBox.SelectedText = block;
            SqlSyntaxHighlighter.Apply(_scriptBox);
            _undoRedo.Checkpoint(); // Write Schema insert is its own undo step (Ctrl+Z removes it)
            _statusLabel.Text = $"Đã chèn schema của {obj.QualifiedName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Write Schema", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static (string schema, string table) ParseTableRef(string raw)
    {
        raw = raw.Trim().Replace("[", "").Replace("]", "");
        var parts = raw.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    // ---------------- Check Fields ----------------

    private async Task CheckFieldsAsync()
    {
        _statusLabel.Text = "Đang kiểm tra (SET NOEXEC ON)...";
        try
        {
            var error = await _service.CheckFieldsAsync(_scriptBox.Text, UseSysDatabase);
            if (error is null)
            {
                _statusLabel.ForeColor = Color.DarkGreen;
                _statusLabel.Text = "Check Fields: OK — mọi bảng/cột trong script đều hợp lệ (chưa chạy thật câu lệnh).";
            }
            else
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = $"Check Fields: {error}";
                MessageBox.Show(this, error, "Bcode — Check Fields", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi Check Fields.";
            MessageBox.Show(this, ex.Message, "Bcode — Check Fields", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------------- Comment / Uncomment ----------------

    private void ToggleComment(bool comment)
    {
        if (_scriptBox.TextLength == 0) return;
        var startLine = _scriptBox.GetLineFromCharIndex(_scriptBox.SelectionStart);
        var endCharIndex = _scriptBox.SelectionStart + Math.Max(_scriptBox.SelectionLength - 1, 0);
        var endLine = _scriptBox.GetLineFromCharIndex(endCharIndex);

        var lines = _scriptBox.Lines;
        for (var i = startLine; i <= endLine && i < lines.Length; i++)
        {
            if (comment)
            {
                if (!lines[i].TrimStart().StartsWith("--"))
                    lines[i] = "-- " + lines[i];
            }
            else
            {
                var trimStart = lines[i].Length - lines[i].TrimStart().Length;
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("-- "))
                    lines[i] = lines[i][..trimStart] + trimmed[3..];
                else if (trimmed.StartsWith("--"))
                    lines[i] = lines[i][..trimStart] + trimmed[2..];
            }
        }

        var selStart = _scriptBox.SelectionStart;
        _scriptBox.Lines = lines;
        SqlSyntaxHighlighter.Apply(_scriptBox);
        _undoRedo.Checkpoint(); // Comment/Uncomment is its own undo step
        _scriptBox.SelectionStart = Math.Min(selStart, _scriptBox.TextLength);
        _scriptBox.SelectionLength = 0;
        _scriptBox.Focus();
    }

    // ---------------- Default Type (keyword case) ----------------

    /// <summary>
    /// "Default Type" dropdown: choosing UPPER/lower rewrites every SQL keyword in the
    /// current script to that case right away (a one-off transform, not a persistent
    /// typing mode — FCode's exact semantics weren't visible in the screenshot beyond the
    /// 3 item labels, so this is the assumed behavior; see README).
    /// </summary>
    private void ApplyDefaultTypeChoice()
    {
        if (_defaultTypeCombo.SelectedIndex <= 0) return; // "Default Type" = no-op / neutral
        var toUpper = _defaultTypeCombo.SelectedIndex == 1; // 1 = UPPER Keyword, 2 = lower Keyword
        var selStart = _scriptBox.SelectionStart;
        _scriptBox.Text = SqlSyntaxHighlighter.TransformKeywordCase(_scriptBox.Text, toUpper);
        SqlSyntaxHighlighter.Apply(_scriptBox);
        _undoRedo.Checkpoint(); // Default Type case-rewrite is its own undo step
        _scriptBox.SelectionStart = Math.Min(selStart, _scriptBox.TextLength);
    }

    // ---------------- Suggest Param/Caret (autocomplete) ----------------

    private async Task EnsureTableNamesLoadedAsync()
    {
        if (_tableNameCache.Count > 0) return;
        try
        {
            var names = new List<string>();
            foreach (var useSys in new[] { false, true })
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys);
                names.AddRange(objs.Select(o => o.QualifiedName));
                names.AddRange(objs.Select(o => o.Name));
            }
            _tableNameCache = names.Distinct().ToList();
        }
        catch { /* chưa kết nối — suggest vẫn dùng được với riêng danh sách keyword */ }
    }

    private async Task<List<string>> GetReferencedColumnsAsync(bool useSys)
    {
        var cols = new List<string>();
        foreach (Match m in TableRefRegex.Matches(_scriptBox.Text))
        {
            var (schema, table) = ParseTableRef(m.Groups[1].Value);
            var cacheKey = $"{useSys}:{schema}.{table}";
            if (!_columnCache.TryGetValue(cacheKey, out var tableCols))
            {
                try { tableCols = await _lookupService.GetColumnsAsync(useSys, schema, table); }
                catch { tableCols = new(); }
                _columnCache[cacheKey] = tableCols;
            }
            cols.AddRange(tableCols);
        }
        return cols.Distinct().ToList();
    }

    private int GetCurrentWordStart()
    {
        var text = _scriptBox.Text;
        var caret = _scriptBox.SelectionStart;
        var i = caret;
        while (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_' || text[i - 1] == '$')) i--;
        return i;
    }

    /// <summary>The identifier touching <paramref name="charIndex"/> (same word-char rule as
    /// GetCurrentWordStart above), plus a leading "schema." qualifier immediately before it
    /// when there is one — e.g. clicking anywhere in "rs_rptTransactionList" inside
    /// "exec dbo.rs_rptTransactionList" returns "dbo.rs_rptTransactionList", so ParseTableRef
    /// can split it normally. Returns null when the click didn't land on/against a word (e.g.
    /// whitespace, punctuation).</summary>
    private string? GetIdentifierAt(int charIndex)
    {
        var text = _scriptBox.Text;
        if (text.Length == 0) return null;
        charIndex = Math.Clamp(charIndex, 0, text.Length - 1);

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

        if (!IsWordChar(text[charIndex]))
        {
            if (charIndex > 0 && IsWordChar(text[charIndex - 1])) charIndex--;
            else return null;
        }

        var start = charIndex;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var end = charIndex;
        while (end < text.Length - 1 && IsWordChar(text[end + 1])) end++;
        end++; // make exclusive

        var word = text[start..end];

        if (start > 0 && text[start - 1] == '.')
        {
            var qEnd = start - 1;
            var qStart = qEnd;
            while (qStart > 0 && IsWordChar(text[qStart - 1])) qStart--;
            if (qStart < qEnd)
                word = text[qStart..qEnd] + "." + word;
        }

        return word;
    }

    private async Task ShowSuggestionsAsync()
    {
        if (!_suggestCheck.Checked) return;
        await EnsureTableNamesLoadedAsync();
        var cols = await GetReferencedColumnsAsync(UseSysDatabase);

        var wordStart = GetCurrentWordStart();
        var caret = _scriptBox.SelectionStart;
        var currentWord = caret > wordStart ? _scriptBox.Text[wordStart..caret] : "";

        var candidates = SqlSyntaxHighlighter.KeywordList
            .Concat(_tableNameCache)
            .Concat(cols)
            .Distinct()
            .Where(c => currentWord.Length == 0 || c.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        if (candidates.Count == 0 || _suggestPopup is null) { HideSuggestions(); return; }

        _suggestPopup.Items.Clear();
        _suggestPopup.Items.AddRange(candidates.Cast<object>().ToArray());
        _suggestPopup.SelectedIndex = 0;

        var caretScreenPos = _scriptBox.PointToScreen(_scriptBox.GetPositionFromCharIndex(caret));
        var localPos = PointToClient(caretScreenPos);
        _suggestPopup.Location = new Point(localPos.X, localPos.Y + 18);
        _suggestPopup.Visible = true;
        _suggestPopup.BringToFront();
    }

    private void MoveSuggestSelection(int delta)
    {
        if (_suggestPopup is null || _suggestPopup.Items.Count == 0) return;
        var idx = _suggestPopup.SelectedIndex + delta;
        if (idx < 0) idx = 0;
        if (idx >= _suggestPopup.Items.Count) idx = _suggestPopup.Items.Count - 1;
        _suggestPopup.SelectedIndex = idx;
    }

    private void AcceptSuggestion()
    {
        if (_suggestPopup is not { Visible: true } || _suggestPopup.SelectedItem is not string chosen)
        {
            HideSuggestions();
            return;
        }

        var wordStart = GetCurrentWordStart();
        var caret = _scriptBox.SelectionStart;
        _scriptBox.Select(wordStart, caret - wordStart);
        _scriptBox.SelectedText = chosen;
        HideSuggestions();
        _scriptBox.Focus();
    }

    private void HideSuggestions()
    {
        if (_suggestPopup is not null) _suggestPopup.Visible = false;
    }

    private void BeautyFormat()
    {
        if (_scriptBox.TextLength == 0) return;
        var formatter = new SqlFormatterService();
        
        // Nếu đang bôi đen, chỉ format vùng bôi đen. Nếu không, format toàn bộ.
        if (_scriptBox.SelectionLength > 0)
        {
            var selStart = _scriptBox.SelectionStart;
            var formatted = formatter.Format(_scriptBox.SelectedText);
            _scriptBox.SelectedText = formatted;
            SqlSyntaxHighlighter.Apply(_scriptBox);
            _undoRedo.Checkpoint();
            _scriptBox.Select(selStart, formatted.Length);
        }
        else
        {
            var formatted = formatter.Format(_scriptBox.Text);
            _scriptBox.Text = formatted;
            SqlSyntaxHighlighter.Apply(_scriptBox);
            _undoRedo.Checkpoint();
        }
    }

    private void InsertText(string text)
    {
        // Chèn script vào vị trí con trỏ chuột hiện tại
        _scriptBox.SelectedText = text;
        SqlSyntaxHighlighter.Apply(_scriptBox);
        _undoRedo.Checkpoint();
        _scriptBox.Focus();
    }
}
