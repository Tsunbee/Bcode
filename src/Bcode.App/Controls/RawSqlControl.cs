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
    private readonly LineNumberGutter _lineGutter;
    private readonly ComboBox _dbCombo;
    private readonly ToolStripComboBox _defaultTypeCombo;
    private readonly ToolStripButton _suggestCheck;
    private readonly ToolStripButton _resetConnCheck;
    private readonly ToolStripButton _resultTabCheck;
    private readonly DataGridView _grid;
    private readonly Label _statusLabel;
    private readonly RawSqlService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly LookupService _lookupService;

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

    public RawSqlControl(RawSqlService service, SqlObjectBrowserService sqlObjectService, LookupService lookupService)
    {
        _service = service;
        _sqlObjectService = sqlObjectService;
        _lookupService = lookupService;
        Dock = DockStyle.Fill;

        // ---- Toolbar ----
        var bar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };

        var openBtn = new ToolStripButton("Open") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        openBtn.Click += (_, _) => OpenFile();
        var saveBtn = new ToolStripButton("Save") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        saveBtn.Click += (_, _) => SaveFile();
        var runBtn = new ToolStripButton("▶ Execute (F5)") { DisplayStyle = ToolStripItemDisplayStyle.Text, Tag = "primary" };
        runBtn.Click += async (_, _) => await RunAsync();
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

        var dbBar = new Panel { Dock = DockStyle.Top, Height = 32, Padding = new Padding(6, 5, 0, 0) };
        _dbCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Dock = DockStyle.Left, Margin = new Padding(4, 0, 0, 0) };
        _dbCombo.Items.AddRange(new object[] { "App Data", "Sys Data" });
        _dbCombo.SelectedIndex = 0;
        _dbCombo.SelectedIndexChanged += (_, _) => DisposePersistentConnection(); // a stale persistent conn would target the wrong DB
        var dbLabel = new Label { Text = "DB:", Dock = DockStyle.Left, AutoSize = true, TextAlign = ContentAlignment.MiddleLeft };
        dbBar.Controls.Add(_dbCombo);
        dbBar.Controls.Add(dbLabel);

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
        _scriptBox.HandleCreated += (_, _) =>
        {
            SqlSyntaxHighlighter.DisableNativeUndo(_scriptBox);
            SqlSyntaxHighlighter.Apply(_scriptBox);
        };
        _highlightDebounce = new System.Windows.Forms.Timer { Interval = 400 };
        _highlightDebounce.Tick += (_, _) =>
        {
            _highlightDebounce.Stop();
            if (_scriptBox.IsDisposed) return;
            SqlSyntaxHighlighter.Apply(_scriptBox);
            _undoRedo.Checkpoint();
        };
        _scriptBox.TextChanged += (_, _) => { _highlightDebounce.Stop(); _highlightDebounce.Start(); };

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

        // Line-number gutter (RichTextBox has no built-in one) — Left-docked before the
        // script box so it claims the left edge; the script box (already Dock=Fill) fills
        // whatever's left.
        _lineGutter = new LineNumberGutter { Dock = DockStyle.Left };
        var scriptPanel = new Panel { Dock = DockStyle.Fill };
        scriptPanel.Controls.Add(_scriptBox);
        scriptPanel.Controls.Add(_lineGutter);
        _lineGutter.Attach(_scriptBox);

        _statusLabel = new Label
        {
            Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray, Padding = new Padding(6, 0, 0, 0)
        };

        // AutoSizeColumnsMode.DisplayedCells left continuously on recalculates every column's
        // width on basically every paint/scroll — the actual cause of "SQL Query" (this
        // control, RawSqlControl — the tab is labeled "SQL Query" though the class is
        // RawSqlControl; "Command" in the UI is the OTHER control, SqlQueryControl) feeling
        // stiff/laggy on a result with many rows or columns. GridDisplayHelper.BindOptimized
        // (used in RunAsync below) does that sizing pass once instead.
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };

        ResultGridMenu.Attach(_grid);

        // Script box and result grid used to split as a fixed 220px/rest — a resizable
        // splitter (matching every other split view already in this app) is friendlier when
        // a script is longer than a few lines or a result set is wide, instead of being
        // stuck squinting at whichever pane the fixed split shortchanged.
        var resultSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        resultSplit.Panel1.Controls.Add(scriptPanel);
        resultSplit.Panel2.Controls.Add(_grid);
        resultSplit.Panel1MinSize = 0;
        resultSplit.Panel2MinSize = 0;
        var splitterInitialized = false;
        resultSplit.SizeChanged += (_, _) =>
        {
            if (resultSplit.Height <= 0) return;
            var maxDistance = Math.Max(0, resultSplit.Height - resultSplit.SplitterWidth);
            if (!splitterInitialized)
            {
                resultSplit.SplitterDistance = Math.Min(220, maxDistance);
                splitterInitialized = true;
            }
            else if (resultSplit.SplitterDistance > maxDistance)
            {
                resultSplit.SplitterDistance = maxDistance; // keep the user's own drag valid as the window shrinks, don't reset it
            }
        };

        Controls.Add(resultSplit);
        Controls.Add(_statusLabel);
        Controls.Add(dbBar);
        Controls.Add(bar);

        _suggestPopup = new ListBox { Width = 240, Height = 150, Visible = false };
        _suggestPopup.Click += (_, _) => AcceptSuggestion();
        Controls.Add(_suggestPopup);
        _suggestPopup.BringToFront();

        Disposed += (_, _) =>
        {
            DisposePersistentConnection();
            _highlightDebounce.Stop();
            _highlightDebounce.Dispose();
        };
    }

    private void _scriptBoxWordWrapToggle(bool wrap)
    {
        _scriptBox.WordWrap = wrap;
        _scriptBox.ScrollBars = wrap ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
    }

    /// <summary>Fired after a batch with a result set runs — used both for the inline grid
    /// (when "Result Tab" is unchecked) and by callers like Gen Insert/Update or Create *.xlsx.</summary>
    public event Action<DataTable>? ResultReady;

    /// <summary>Fired when "Result Tab" is checked and a query returns rows — MainForm opens a
    /// new document tab hosting the result instead of showing it in this control's inline grid.</summary>
    public event Action<DataTable, string>? OpenResultInNewTabRequested;

    private bool UseSysDatabase => _dbCombo.SelectedIndex == 1;

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
            var lastTable = results.LastOrDefault(r => r.Table is not null)?.Table;
            if (lastTable is not null)
            {
                if (_resultTabCheck.Checked)
                    OpenResultInNewTabRequested?.Invoke(lastTable, "Command Result");
                else
                    GridDisplayHelper.BindOptimized(_grid, lastTable);
                ResultReady?.Invoke(lastTable);
            }

            var totalAffected = results.Where(r => r.Table is null).Sum(r => r.RowsAffected);
            var summary = $"{results.Count} batch đã chạy" +
                           (lastTable is not null ? $" · {lastTable.Rows.Count} dòng kết quả (batch cuối có SELECT)" : "") +
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
}
