using System.Data;
using System.Text;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// The SELECT / FROM / WHERE / ORDER BY + Run tool (WCommand &gt; Table in FCode).
/// Wraps SqlQueryService, which is what expands a "...$000000" FROM target into a
/// UNION ALL over every real period table.
///
/// Left of the query itself: a "Fields" checklist for whatever table is currently in the
/// FROM box (name + a "(PK)" tag for primary-key columns, matching FCode showing a table's
/// structure this way) — ticking columns there rebuilds the SELECT box from the checked
/// names, so building a query is "type FROM, tick the fields you want" instead of typing
/// column names by hand. And a dedicated "Add Script" button (distinct from the toolbar's
/// own "Add Script", which imports a .f/.xml/.sql file as a new tab) that packages every
/// row currently loaded in the grid into a DELETE + bulk-INSERT script for the FROM table —
/// see DataScriptService for the exact format (matches FCode's own output).
/// </summary>
public class SqlQueryControl : UserControl
{
    private readonly TextBox _selectBox;
    private readonly TextBox _fromBox;
    private readonly TextBox _whereBox;
    private readonly TextBox _orderByBox;
    private readonly TextBox _topBox;
    private readonly Button _runButton;
    private readonly Button _addScriptButton;
    private readonly DataGridView _grid;
    private readonly Label _statusLabel;
    private readonly CheckedListBox _fieldsList;
    private readonly Label _fieldsStatusLabel;
    private readonly SqlQueryService _service;
    private readonly GenInsertService _genInsert;
    private readonly GenUpdateService _genUpdate;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly DataScriptService _dataScript;
    private readonly ScriptFileService _scriptFileService;

    // Guards ItemCheck's SELECT-box rebuild while the list itself is being (re)populated in
    // code (ReloadFieldsAsync/SetItemChecked in a loop) — without this, checking/unchecking
    // items programmatically during a reload would also fire ItemCheck and stomp on the
    // SELECT box (or worse, run mid-reload against a half-populated list).
    private bool _suppressFieldsChanged;
    // Bumped on every ReloadFieldsAsync call — a slow metadata query for a FROM value the
    // user already changed past is a no-op instead of overwriting the list with stale columns
    // (same pattern FileLookupControl.PreviewFile uses for its own background reads).
    private int _fieldsRequestVersion;

    public SqlQueryControl(SqlQueryService service, GenInsertService genInsert, GenUpdateService genUpdate,
        SqlObjectBrowserService sqlObjectService, DataScriptService dataScript, ScriptFileService scriptFileService)
    {
        _service = service;
        _genInsert = genInsert;
        _genUpdate = genUpdate;
        _sqlObjectService = sqlObjectService;
        _dataScript = dataScript;
        _scriptFileService = scriptFileService;
        Dock = DockStyle.Fill;

        // 5 columns now (was 4) — "Top" gets its own column instead of hardcoding the row
        // cap in SqlQueryService, so a query that's hiding rows behind the old fixed 500 can
        // actually show everything (0 = tất cả) instead of silently truncating.
        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 100, ColumnCount = 5, RowCount = 2 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        _selectBox = LabeledBox(top, "SELECT", 0, "*");
        _fromBox = LabeledBox(top, "FROM", 1, "");
        _whereBox = LabeledBox(top, "WHERE", 0, "", row: 1);
        _orderByBox = LabeledBox(top, "ORDER BY", 1, "", row: 1);

        var topBoxPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        topBoxPanel.Controls.Add(new Label { Text = "Top", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        _topBox = new TextBox { Width = 55, Text = SqlQueryService.DefaultMaxRows.ToString(), PlaceholderText = "0=tất cả" };
        topBoxPanel.Controls.Add(_topBox);
        top.Controls.Add(topBoxPanel, 2, 1); // same row as WHERE/ORDER BY, its own column

        // "Add Script" only makes sense once a query has actually been run (it works off the
        // loaded grid, not the SELECT text). Added the exact same way as _runButton right
        // below (a Button straight into the TableLayoutPanel cell, Dock=Fill, RowSpan 2) —
        // an earlier version wrapped it in an extra Panel first and that cell rendered as a
        // plain unstyled dark rectangle with no visible button at all ("bị đen thui"), so the
        // wrapper is gone; this is the same pattern Run has always used successfully.
        _addScriptButton = new Button { Text = "Add Script", Dock = DockStyle.Fill };
        _addScriptButton.Click += async (_, _) => await GenDataScriptAsync();
        top.Controls.Add(_addScriptButton, 3, 0);
        top.SetRowSpan(_addScriptButton, 2);

        _runButton = new Button { Text = "▶ Run", Dock = DockStyle.Fill, Tag = "primary" };
        _runButton.Click += async (_, _) => await RunAsync();
        top.Controls.Add(_runButton, 4, 0);
        top.SetRowSpan(_runButton, 2);

        // Enter in any of the boxes runs the query too — matching FCode, instead
        // of forcing a mouse click on ▶ Run every time.
        foreach (var box in new[] { _selectBox, _fromBox, _whereBox, _orderByBox, _topBox })
        {
            box.KeyDown += async (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true;
                e.SuppressKeyPress = true; // swallow the "ding" a plain TextBox makes on Enter
                await RunAsync();
            };
        }

        // FROM is also what drives the Fields checklist — Enter already triggers Run above,
        // but Leave catches "typed FROM then clicked straight into WHERE/the grid" too.
        _fromBox.Leave += async (_, _) => await ReloadFieldsAsync();
        _fromBox.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) await ReloadFieldsAsync(); };

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray };

        // AutoSizeColumnsMode.DisplayedCells left continuously ON recalculates every column's
        // width on basically every paint/scroll — fine for a handful of rows, but on a wide
        // ERP table with a few hundred rows it's what makes the grid feel "đơ" (stiff/laggy)
        // while scrolling. GridDisplayHelper.BindOptimized runs that same sizing pass ONCE
        // right after data loads instead, then leaves column widths fixed (None) — mirrors
        // how a real spreadsheet/grid app behaves (columns don't refit on every scroll tick).
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };

        var contextMenu = new ContextMenuStrip();
        var genInsertItem = new ToolStripMenuItem("Gen Insert (dòng đã chọn)");
        genInsertItem.Click += (_, _) => GenInsertSelected();
        var genUpdateItem = new ToolStripMenuItem("Gen Update (dòng đã chọn)") { ShortcutKeyDisplayString = "Ctrl+Shift+U" };
        genUpdateItem.Click += (_, _) => GenUpdateSelected();
        contextMenu.Items.Add(genInsertItem);
        contextMenu.Items.Add(genUpdateItem);
        ResultGridMenu.AddItemsTo(contextMenu, _grid);
        _grid.ContextMenuStrip = contextMenu;
        _grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.U) { e.Handled = true; GenUpdateSelected(); }
        };

        // ---- Right side: the query bar + result grid (everything above) ----
        var queryPanel = new Panel { Dock = DockStyle.Fill };
        queryPanel.Controls.Add(_grid);
        queryPanel.Controls.Add(_statusLabel);
        queryPanel.Controls.Add(top);

        // ---- Left side: "Fields" checklist for the table currently in FROM ----
        var fieldsTop = new Panel { Dock = DockStyle.Top, Height = 24 };
        fieldsTop.Controls.Add(new Label
        {
            Text = "Fields",
            Dock = DockStyle.Left,
            AutoSize = true,
            Padding = new Padding(4, 5, 0, 0),
            Font = new Font(Font, FontStyle.Bold)
        });
        _fieldsStatusLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            AutoEllipsis = true,
            ForeColor = SystemColors.GrayText,
            Padding = new Padding(0, 5, 4, 0)
        };
        fieldsTop.Controls.Add(_fieldsStatusLabel);

        _fieldsList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        _fieldsList.ItemCheck += (_, e) =>
        {
            if (_suppressFieldsChanged) return;
            // ItemCheck fires BEFORE the item's own CheckState updates — BeginInvoke so the
            // rebuild below sees the list's state AFTER this particular check/uncheck lands.
            BeginInvoke(() => RebuildSelectFromFields());
        };
        // "ấn ctrl + a sẽ tự tick hết các column" — CheckedListBox has no built-in "check
        // everything" for Ctrl+A, so handle it directly: suppress the per-item ItemCheck
        // rebuild while ticking every row, then rebuild the SELECT box once at the end.
        _fieldsList.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.A) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            _suppressFieldsChanged = true;
            for (var i = 0; i < _fieldsList.Items.Count; i++) _fieldsList.SetItemChecked(i, true);
            _suppressFieldsChanged = false;
            RebuildSelectFromFields();
        };

        var fieldsPanel = new Panel { Dock = DockStyle.Fill };
        fieldsPanel.Controls.Add(_fieldsList);
        fieldsPanel.Controls.Add(fieldsTop);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(fieldsPanel);
        split.Panel2.Controls.Add(queryPanel);
        split.Panel1MinSize = 0;
        split.Panel2MinSize = 0;
        const int desiredFieldsWidth = 200;
        void ApplySplitterDistance()
        {
            if (split.Width <= 0) return;
            var clamped = Math.Max(0, Math.Min(desiredFieldsWidth, split.Width - split.SplitterWidth));
            if (split.SplitterDistance != clamped) split.SplitterDistance = clamped;
        }
        split.SizeChanged += (_, _) => ApplySplitterDistance();

        Controls.Add(split);

        // Gợi ý tên table/view (autocomplete) cho ô FROM — nạp 1 lần khi tab mở,
        // gộp cả App Data lẫn Sys Data vì FROM có thể tham chiếu bảng ở cả 2.
        _fromBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _fromBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
        _fromBox.AutoCompleteCustomSource = new AutoCompleteStringCollection();
        Load += async (_, _) => await LoadFromSuggestionsAsync();
        // Prefill (e.g. via SetFrom, before the tab is even shown) already has a FROM value —
        // load its fields once the control is actually on screen instead of waiting for the
        // user to touch the FROM box first.
        Load += async (_, _) => await ReloadFieldsAsync();
    }

    /// <summary>Populates the FROM box's autocomplete list with every table/view name
    /// (both "schema.name" and bare "name" forms) from App Data + Sys Data. Silent on
    /// failure — this is a convenience, not something that should block/alert the user
    /// if the workspace isn't connected yet.</summary>
    private async Task LoadFromSuggestionsAsync()
    {
        try
        {
            var names = new List<string>();
            foreach (var useSys in new[] { false, true })
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys);
                foreach (var o in objs.Where(o => o.Kind is SqlObjectKind.Table or SqlObjectKind.View))
                {
                    names.Add(o.QualifiedName);
                    names.Add(o.Name);
                }
            }

            var source = new AutoCompleteStringCollection();
            source.AddRange(names.Distinct().ToArray());
            _fromBox.AutoCompleteCustomSource = source;
        }
        catch
        {
            // Chưa kết nối / lỗi tạm thời — bỏ qua, người dùng vẫn gõ FROM tay được như cũ.
        }
    }

    /// <summary>(schema, table) parsed out of a "dbo.dmkh" / "[dbo].[dmkh]" / "dmkh" FROM
    /// value — same convention TableEditControl.ParseTableRef uses. Anything more complex
    /// (a join, a "$000000" period placeholder, a subquery) isn't a single real table, so the
    /// Fields list just stays empty for it rather than guessing.</summary>
    private static (string schema, string table) ParseTableRef(string raw)
    {
        raw = raw.Trim().Trim('[', ']');
        var parts = raw.Replace("[", "").Replace("]", "").Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    /// <summary>Reloads the Fields checklist for whatever's currently in FROM. A FROM with a
    /// space (join / alias / "$000000" placeholder) is skipped — GetColumnsAsync only makes
    /// sense for a single bare table reference — and the list is just cleared instead.</summary>
    private async Task ReloadFieldsAsync()
    {
        var version = ++_fieldsRequestVersion;
        var fromText = _fromBox.Text.Trim();

        _suppressFieldsChanged = true;
        _fieldsList.Items.Clear();
        _suppressFieldsChanged = false;

        if (fromText.Length == 0 || fromText.Contains(' ') || fromText.Contains('$'))
        {
            _fieldsStatusLabel.Text = "";
            return;
        }

        var (schema, table) = ParseTableRef(fromText);
        _fieldsStatusLabel.Text = "Đang tải...";
        try
        {
            var columns = await _sqlObjectService.GetColumnsAsync(false, schema, table);
            if (columns.Count == 0)
                columns = await _sqlObjectService.GetColumnsAsync(true, schema, table);

            if (version != _fieldsRequestVersion) return; // FROM moved on again while this was loading

            _suppressFieldsChanged = true;
            foreach (var (name, isPk) in columns)
                _fieldsList.Items.Add(isPk ? $"{name} (PK)" : name);
            _suppressFieldsChanged = false;

            _fieldsStatusLabel.Text = columns.Count == 0 ? "(không có cột)" : $"{columns.Count} cột";
        }
        catch
        {
            if (version != _fieldsRequestVersion) return;
            _fieldsStatusLabel.Text = "";
            // Không kết nối được / tên bảng chưa hợp lệ — bỏ qua, SELECT vẫn gõ tay được.
        }
    }

    /// <summary>Strips the " (PK)" display suffix back to the real column name.</summary>
    private static string ColumnName(string listItemText) =>
        listItemText.EndsWith(" (PK)", StringComparison.Ordinal) ? listItemText[..^5] : listItemText;

    /// <summary>Rebuilds SELECT from whichever Fields items are ticked — "*" when none are
    /// (so unchecking everything doesn't leave behind an empty, invalid SELECT list).</summary>
    private void RebuildSelectFromFields()
    {
        var checkedNames = _fieldsList.CheckedItems.Cast<string>().Select(ColumnName).ToList();
        _selectBox.Text = checkedNames.Count == 0 ? "*" : string.Join(", ", checkedNames.Select(n => $"[{n}]"));
    }

    private static TextBox LabeledBox(TableLayoutPanel parent, string label, int col, string defaultValue, int row = 0)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        var box = new TextBox { Width = 260, Text = defaultValue };
        panel.Controls.Add(box);
        parent.Controls.Add(panel, col, row);
        return box;
    }

    public void SetFrom(string fromClause) => _fromBox.Text = fromClause;
    public void SetWhere(string whereClause) => _whereBox.Text = whereClause;

    /// <summary>Fired after a successful Run, carrying the result table (consumed e.g. by Create *.xlsx).</summary>
    public event Action<DataTable>? ResultReady;

    private async Task RunAsync()
    {
        var maxRows = int.TryParse(_topBox.Text, out var n) ? n : SqlQueryService.DefaultMaxRows;

        _statusLabel.Text = "Đang chạy...";
        _runButton.Enabled = false;
        try
        {
            var table = await _service.RunAsync(_selectBox.Text, _fromBox.Text, _whereBox.Text, _orderByBox.Text, maxRows);
            GridDisplayHelper.BindOptimized(_grid, table);
            var capNote = maxRows > 0 ? $"(tối đa {maxRows})" : "(không giới hạn)";
            _statusLabel.Text = $"{table.Rows.Count} dòng {capNote} · SQL đã thực thi: {_service.LastSql.Replace('\n', ' ')}";
            ResultReady?.Invoke(table);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — SQL Query", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _runButton.Enabled = true;
        }
    }

    private void GenInsertSelected()
    {
        if (_grid.DataSource is not DataTable table || _grid.SelectedRows.Count == 0) return;

        var rows = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Where(r => r.DataBoundItem is DataRowView)
            .Select(r => ((DataRowView)r.DataBoundItem!).Row);

        var targetName = Bcode.App.Forms.SimplePromptForm.Show(this, "Gen Insert", "Tên bảng đích cho câu lệnh INSERT:", table.TableName);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        var sql = _genInsert.GenerateInsertStatements(table, targetName, rows);
        Clipboard.SetText(sql);
        MessageBox.Show(this, "Đã sinh câu lệnh INSERT và copy vào clipboard.", "Bcode",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void GenUpdateSelected()
    {
        if (_grid.DataSource is not DataTable table || _grid.SelectedRows.Count == 0) return;

        var rows = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Where(r => r.DataBoundItem is DataRowView)
            .Select(r => ((DataRowView)r.DataBoundItem!).Row);

        var targetName = Bcode.App.Forms.SimplePromptForm.Show(this, "Gen Update", "Tên bảng đích cho câu lệnh UPDATE:", table.TableName);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        var keyInput = Bcode.App.Forms.SimplePromptForm.Show(this, "Gen Update",
            "Cột khoá (key) làm điều kiện WHERE, cách nhau bởi dấu phẩy (vd: stt_rec hoặc ma_ct,ky):",
            table.Columns.Count > 0 ? table.Columns[0].ColumnName : "");
        if (string.IsNullOrWhiteSpace(keyInput)) return;

        var keyColumns = keyInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sql = _genUpdate.GenerateUpdateStatements(table, targetName, keyColumns, rows);
        Clipboard.SetText(sql);
        MessageBox.Show(this, "Đã sinh câu lệnh UPDATE và copy vào clipboard.", "Bcode",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>"Add Script" — packages every row currently loaded in the grid (not just a
    /// selection, unlike Gen Insert/Gen Update above) into a DELETE + bulk-INSERT script for
    /// the target table. Defaults the target table name to whatever's in FROM, same as Gen
    /// Insert/Gen Update default to the result table's own name.
    ///
    /// Fix history: this used to show the generated script in a RichTextBox "Script" popup,
    /// syntax-highlighted — several rounds of trying to make THAT popup stay responsive for an
    /// 18k+-row script (background generation, precomputed RTF, chunked Select()+SelectionColor
    /// coloring, WordWrap tricks) all still ended up "Not Responding" at some scale, because the
    /// RichTextBox control itself is what doesn't scale to this much text/formatting, not any
    /// particular way of feeding it. Gen Insert/Gen Update just above never had this problem
    /// because they never show their result in a RichTextBox at all — they copy straight to the
    /// clipboard and confirm with a MessageBox ("Làm giống chức năng gen insert giống bên tab
    /// command, vì nhanh hơn rất nhiều"). This does the same: no popup, no highlighting, just
    /// clipboard + a status line. The script is also written to a scratch file and added to the
    /// Script Cart (silently, off the UI thread) so the toolbar's View/Save/Copy Script still
    /// pick it up — same as before, just without a RichTextBox anywhere in the path.</summary>
    private async Task GenDataScriptAsync()
    {
        if (_grid.DataSource is not DataTable table || table.Rows.Count == 0)
        {
            MessageBox.Show(this, "Chưa có dữ liệu để sinh Script — bấm Run trước.", "Bcode — Add Script");
            return;
        }

        var (_, defaultTable) = ParseTableRef(_fromBox.Text);
        var targetName = Bcode.App.Forms.SimplePromptForm.Show(this, "Add Script",
            "Tên bảng đích (DELETE toàn bộ rồi nạp lại từ dữ liệu đang xem):",
            string.IsNullOrWhiteSpace(defaultTable) ? table.TableName : defaultTable);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        _addScriptButton.Enabled = false;
        _statusLabel.Text = $"Đang sinh script cho {table.Rows.Count} dòng...";
        try
        {
            string script = null!;
            string path = null!;
            await Task.Run(() =>
            {
                script = _dataScript.GenerateDeleteAndReloadScript(table, targetName);
                var fileName = $"{targetName.Replace('.', '_')}_{DateTime.Now:yyyyMMdd_HHmmss}.sql";
                path = Path.Combine(Path.GetTempPath(), "Bcode", "GeneratedScripts", fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            });

            Clipboard.SetText(script);
            _scriptFileService.AddToCart(path);

            _statusLabel.Text = $"Đã sinh script ({table.Rows.Count} dòng) và copy vào clipboard.";
            MessageBox.Show(this, "Đã sinh script và copy vào clipboard.", "Bcode — Add Script",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Add Script", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _addScriptButton.Enabled = true;
        }
    }
}
