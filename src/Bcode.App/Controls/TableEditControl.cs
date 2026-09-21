using System.Data;
using System.Text;
using Bcode.App.Forms;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// "Table" tool — open one table as a live, directly-editable grid (like
/// opening a table in Excel), as opposed to "SQL Query"/"Command" where you
/// have to write SELECT yourself. Add/edit/delete rows in the grid, then
/// "Save" writes the real INSERT/UPDATE/DELETE statements back.
/// </summary>
public class TableEditControl : UserControl
{
    private readonly ComboBox _dbCombo;
    private readonly TextBox _tableBox;
    private readonly TextBox _topBox;
    private readonly Button _loadButton;
    private readonly Button _saveButton;
    private readonly Button _addScriptButton;
    private readonly Label _keyLabel;
    private readonly DataGridView _grid;
    private readonly ListView _structureList;
    private readonly Label _statusLabel;
    private readonly TableDataService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly DataScriptService _dataScript;
    private readonly ScriptFileService _scriptFileService;
    private readonly GenInsertService _genInsert;
    private readonly GenUpdateService _genUpdate;

    private string _schema = "dbo";
    private string _table = "";
    private List<string> _keyColumns = new();

    public TableEditControl(TableDataService service, SqlObjectBrowserService sqlObjectService, DataScriptService dataScript,
        ScriptFileService scriptFileService, GenInsertService genInsert, GenUpdateService genUpdate)
    {
        _service = service;
        _sqlObjectService = sqlObjectService;
        _dataScript = dataScript;
        _scriptFileService = scriptFileService;
        _genInsert = genInsert;
        _genUpdate = genUpdate;
        Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, Padding = new Padding(4, 4, 0, 0) };
        _dbCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        _dbCombo.Items.AddRange(new object[] { "App Data", "Sys Data" });
        _dbCombo.SelectedIndex = 0;
        _tableBox = new TextBox { Width = 220, PlaceholderText = "dbo.tenbang hoặc tenbang" };
        _tableBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _tableBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
        _tableBox.AutoCompleteCustomSource = new AutoCompleteStringCollection();
        _tableBox.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; await LoadAsync(); } };
        // 500 is just the prefilled default, not a ceiling — TableDataService.LoadTableAsync
        // treats 0 (or a blank/unparsed box, see LoadAsync below) as "no limit at all", for
        // tables the user genuinely wants to see/edit in full.
        _topBox = new TextBox { Width = 60, Text = "500", PlaceholderText = "0 = tất cả" };
        _loadButton = new Button { Text = "Load", Tag = "primary" };
        _loadButton.Click += async (_, _) => await LoadAsync();
        _saveButton = new Button { Text = "💾 Save (ghi vào DB)", Enabled = false };
        _saveButton.Click += async (_, _) => await SaveAsync();
        // Same "Add Script" feature as Command's SqlQueryControl (DELETE + bulk-INSERT reload
        // script for the target table, from whatever's currently loaded in the grid) — Table
        // was missing it even though it's the more natural home for "dump this table's data as
        // a script" than Command, which needs a SELECT written first.
        _addScriptButton = new Button { Text = "Add Script" };
        _addScriptButton.Click += async (_, _) => await GenDataScriptAsync();

        top.Controls.Add(new Label { Text = "DB:", AutoSize = true, Padding = new Padding(0, 6, 2, 0) });
        top.Controls.Add(_dbCombo);
        top.Controls.Add(new Label { Text = "Table:", AutoSize = true, Padding = new Padding(6, 6, 2, 0) });
        top.Controls.Add(_tableBox);
        top.Controls.Add(new Label { Text = "Top:", AutoSize = true, Padding = new Padding(6, 6, 2, 0) });
        top.Controls.Add(_topBox);
        top.Controls.Add(_loadButton);
        top.Controls.Add(_saveButton);
        top.Controls.Add(_addScriptButton);

        _keyLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };
        _statusLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };

        // AutoSizeColumnsMode.None + GridDisplayHelper.BindOptimized (one autosize pass right
        // after loading, not continuously) — DisplayedCells left on permanently is what made
        // a wide/tall table feel stiff ("đơ") while scrolling, recalculating every column's
        // width on basically every paint.
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            ReadOnly = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };
        _grid.CellValueChanged += (_, _) => _saveButton.Enabled = true;
        _grid.UserAddedRow += (_, _) => _saveButton.Enabled = true;
        _grid.UserDeletedRow += (_, _) => _saveButton.Enabled = true;

        // "Bên table khi click chuột phải có các tính năng này như bên command nhé" — same
        // right-click menu as Command's result grid (SqlQueryControl): Gen Insert/Gen Update
        // for the selected rows, plus everything ResultGridMenu adds (Goto Column, Copy Column
        // Name(s), Filter, Add Index Column Order, Generate Design Fields, Maxlength Column
        // Content, Compare Column Content, Set Color Cell). Table's grid uses CellSelect (it's
        // directly editable, unlike Command's read-only FullRowSelect result grid), so
        // SelectedRows alone is often empty here — GetSelectedDataRows below falls back to the
        // distinct rows behind whatever cells are selected.
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

        // ---- Left: table structure (column name/type/PK), with FCode's own right-click
        // menu (Gen Structure Table/Add/Alter/Drop Column, Render Dir/Grid XML) — see
        // BuildStructureContextMenu. CheckBoxes lets Gen Add/Alter/Drop Column target several
        // columns at once; right-click without any ticked just targets the clicked row.
        _structureList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            CheckBoxes = true
        };
        _structureList.Columns.Add("Column", 130);
        _structureList.Columns.Add("Type", 90);
        _structureList.Columns.Add("Key", 36);
        _structureList.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _structureList.HitTest(e.Location);
            if (hit.Item is null) return;
            // Right-clicking a row selects it — same as most Windows list UIs — so a
            // right-click with nothing ticked still has an unambiguous single target.
            foreach (ListViewItem other in _structureList.SelectedItems) other.Selected = false;
            hit.Item.Selected = true;
            hit.Item.Focused = true;
            BuildStructureContextMenu().Show(_structureList, e.Location);
        };
        // "ấn ctrl + a sẽ tự tick hết các column" — ListView's own default Ctrl+A just
        // (row-)selects everything, not the same thing as ticking every checkbox (what Gen
        // Add/Alter/Drop Column and the other multi-column actions above actually read).
        // Handled here instead so Ctrl+A checks every column in one press.
        _structureList.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.A) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            _structureList.BeginUpdate();
            foreach (ListViewItem item in _structureList.Items) item.Checked = true;
            _structureList.EndUpdate();
        };

        var structureHeader = new Panel { Dock = DockStyle.Top, Height = 24 };
        structureHeader.Controls.Add(new Label
        {
            Text = "Structure", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font(Font, FontStyle.Bold), Padding = new Padding(4, 0, 0, 0)
        });

        var structurePanel = new Panel { Dock = DockStyle.Fill };
        structurePanel.Controls.Add(_structureList);
        structurePanel.Controls.Add(structureHeader);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(structurePanel);
        split.Panel2.Controls.Add(_grid);
        split.Panel1MinSize = 0;
        split.Panel2MinSize = 0;
        const int desiredStructureWidth = 190;
        void ApplySplitterDistance()
        {
            if (split.Width <= 0) return;
            var clamped = Math.Max(0, Math.Min(desiredStructureWidth, split.Width - split.SplitterWidth));
            if (split.SplitterDistance != clamped) split.SplitterDistance = clamped;
        }
        split.SizeChanged += (_, _) => ApplySplitterDistance();

        Controls.Add(split);
        Controls.Add(_keyLabel);
        Controls.Add(_statusLabel);
        Controls.Add(top);

        Load += async (_, _) => await LoadTableSuggestionsAsync();
    }

    /// <summary>Populates the left "Structure" list — column names come from the just-loaded
    /// DataTable, but the TYPE shown for each is queried straight from
    /// INFORMATION_SCHEMA.COLUMNS (via TableDataService.GetColumnTypesAsync) rather than
    /// guessed from the DataTable's own .NET CLR types. The CLR-type guess was the "sai kiểu
    /// dữ liệu nhiều" bug: several distinct SQL types collapse into the same CLR type (e.g.
    /// decimal/money/numeric/smallmoney all read back as System.Decimal with no
    /// precision/scale), and it had no case at all for things like uniqueidentifier or
    /// varbinary. Falls back to the old CLR-based guess only if the metadata query itself
    /// fails (e.g. mid-network-hiccup) rather than leaving the panel empty.</summary>
    private async Task PopulateStructureListAsync(bool useSysDatabase, DataTable data)
    {
        Dictionary<string, string>? realTypes = null;
        try { realTypes = await _service.GetColumnTypesAsync(useSysDatabase, _schema, _table); }
        catch { /* metadata query failed — fall back to the CLR-based guess below per column */ }

        _structureList.BeginUpdate();
        _structureList.Items.Clear();
        foreach (DataColumn col in data.Columns)
        {
            var isKey = _keyColumns.Contains(col.ColumnName, StringComparer.OrdinalIgnoreCase);
            var typeText = realTypes is not null && realTypes.TryGetValue(col.ColumnName, out var realType)
                ? realType
                : FallbackClrTypeGuess(col);
            var item = new ListViewItem(col.ColumnName);
            item.SubItems.Add(typeText);
            item.SubItems.Add(isKey ? "PK" : "");
            if (isKey) item.Font = new Font(_structureList.Font, FontStyle.Bold);
            _structureList.Items.Add(item);
        }
        _structureList.EndUpdate();
    }

    /// <summary>Only used if the real INFORMATION_SCHEMA query above fails — an approximate
    /// type from the DataTable's own .NET CLR type, better than nothing but known-lossy (see
    /// PopulateStructureListAsync's doc comment).</summary>
    private static string FallbackClrTypeGuess(DataColumn col)
    {
        if (col.DataType == typeof(string)) return col.MaxLength > 0 ? $"char({col.MaxLength})" : "varchar";
        if (col.DataType == typeof(decimal)) return "decimal";
        if (col.DataType == typeof(DateTime)) return "datetime";
        if (col.DataType == typeof(bool)) return "bit";
        if (col.DataType == typeof(int)) return "int";
        if (col.DataType == typeof(long)) return "bigint";
        if (col.DataType == typeof(short)) return "smallint";
        if (col.DataType == typeof(byte)) return "tinyint";
        if (col.DataType == typeof(double) || col.DataType == typeof(float)) return "float";
        return col.DataType.Name.ToLowerInvariant();
    }

    // ---------------- Structure list right-click menu (Gen Structure Table/Add/Alter/Drop
    // Column, Render Dir/Grid XML) — matches FCode's own menu on its Fields/Structure list.

    /// <summary>Whichever columns Gen Add/Alter/Drop Column should act on: every ticked
    /// checkbox if any are ticked, otherwise just the row that was right-clicked (already
    /// selected by the MouseUp handler above) — same convention a lot of Windows list UIs
    /// use for "act on the checked set, or on what you clicked if nothing's checked".</summary>
    private List<(string Name, string Type)> GetTargetColumns()
    {
        var checkedCols = _structureList.CheckedItems.Cast<ListViewItem>()
            .Select(i => (i.Text, i.SubItems[1].Text)).ToList();
        if (checkedCols.Count > 0) return checkedCols;

        return _structureList.SelectedItems.Count > 0
            ? new List<(string, string)> { (_structureList.SelectedItems[0].Text, _structureList.SelectedItems[0].SubItems[1].Text) }
            : new List<(string, string)>();
    }

    private ContextMenuStrip BuildStructureContextMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(BuildGenSubmenu("Gen Structure Table", GenStructureTable));
        menu.Items.Add(BuildGenSubmenu("Gen Add Column", () => GenColumnDdl("ADD")));
        menu.Items.Add(BuildGenSubmenu("Gen Alter Column", () => GenColumnDdl("ALTER COLUMN")));
        menu.Items.Add(BuildGenSubmenu("Gen Drop Column", GenDropColumn));
        menu.Items.Add(new ToolStripSeparator());

        // No submenu for these two, per FCode's own menu — one click both copies and
        // previews, since there's no separate "Add to Clipboard" leaf for them to pick.
        var renderDir = new ToolStripMenuItem("Render Dir XML");
        renderDir.Click += (_, _) => CopyAndPreview("Render Dir XML", RenderFieldXml);
        var renderGrid = new ToolStripMenuItem("Render Grid XML");
        renderGrid.Click += (_, _) => CopyAndPreview("Render Grid XML", RenderFieldXml);
        menu.Items.Add(renderDir);
        menu.Items.Add(renderGrid);

        Bcode.App.UI.ThemeManager.ApplyMenu(menu);
        return menu;
    }

    private ToolStripMenuItem BuildGenSubmenu(string label, Func<string> generate)
    {
        var item = new ToolStripMenuItem(label);
        var clipboardItem = new ToolStripMenuItem("Add to Clipboard");
        clipboardItem.Click += (_, _) => { try { Clipboard.SetText(generate()); } catch { /* clipboard held by another app */ } };
        var previewItem = new ToolStripMenuItem("Preview");
        previewItem.Click += (_, _) => { using var form = new WCommandScriptForm(generate(), label); form.ShowDialog(this); };
        item.DropDownItems.Add(clipboardItem);
        item.DropDownItems.Add(previewItem);
        return item;
    }

    private void CopyAndPreview(string title, Func<string> generate)
    {
        var script = generate();
        try { Clipboard.SetText(script); } catch { /* clipboard held by another app */ }
        using var form = new WCommandScriptForm(script, title);
        form.ShowDialog(this);
    }

    /// <summary>"Gen Structure Table" always covers the WHOLE table (its name says Table,
    /// not Column) — checkbox selection doesn't narrow this one, unlike Add/Alter/Drop
    /// Column below.</summary>
    private string GenStructureTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{_schema}].[{_table}] (");
        var lines = _structureList.Items.Cast<ListViewItem>()
            .Select(i => $"    [{i.Text}] {i.SubItems[1].Text} {(i.SubItems[2].Text == "PK" ? "NOT NULL" : "NULL")}")
            .ToList();
        sb.Append(string.Join(",\r\n", lines));
        if (_keyColumns.Count > 0)
            sb.Append($",\r\n    CONSTRAINT [PK_{_table}] PRIMARY KEY ({string.Join(", ", _keyColumns.Select(k => $"[{k}]"))})");
        sb.AppendLine();
        sb.AppendLine(");");
        return sb.ToString();
    }

    private string GenColumnDdl(string verb)
    {
        var cols = GetTargetColumns();
        if (cols.Count == 0) return "-- Chưa chọn cột nào (tích checkbox hoặc chuột phải đúng dòng).";
        return string.Join("\r\n", cols.Select(c => $"ALTER TABLE [{_schema}].[{_table}] {verb} [{c.Name}] {c.Type} NULL;"));
    }

    private string GenDropColumn()
    {
        var cols = GetTargetColumns();
        if (cols.Count == 0) return "-- Chưa chọn cột nào (tích checkbox hoặc chuột phải đúng dòng).";
        return string.Join("\r\n", cols.Select(c => $"ALTER TABLE [{_schema}].[{_table}] DROP COLUMN [{c.Name}];"));
    }

    /// <summary>Renders FastBusiness's own Dir/Grid &lt;field&gt; block per selected column —
    /// same shape as the real Dir/Grid XML seen earlier (name/type/allowNulls + a header
    /// v/e pair), with the SQL type mapped to FastBusiness's field type per the one pairing
    /// actually confirmed (DateTime→"DateTime", bit→"Boolean") and a reasonable extension of
    /// that same convention for the others (char/varchar→"Char", decimal/float→"Decimal",
    /// int family→"Int32") — flagged here since those extensions aren't independently
    /// confirmed the way DateTime/Boolean are. header v/e default to the column name itself
    /// (no real Vietnamese/English captions to draw from) — fill those in by hand afterward.
    /// Dir and Grid share the same &lt;field&gt; shape in every real example seen so far, so
    /// this one generator backs both menu items.</summary>
    private string RenderFieldXml()
    {
        var cols = GetTargetColumns();
        if (cols.Count == 0) return "<!-- Chưa chọn cột nào (tích checkbox hoặc chuột phải đúng dòng). -->";
        return string.Join("\r\n", cols.Select(c =>
            $"<field name=\"{c.Name}\" type=\"{MapFieldType(c.Type)}\" allowNulls=\"true\">\r\n" +
            $"    <header v=\"{c.Name}\" e=\"{c.Name}\"></header>\r\n" +
            "</field>"));
    }

    private static string MapFieldType(string sqlType)
    {
        if (sqlType.StartsWith("char", StringComparison.OrdinalIgnoreCase) ||
            sqlType.StartsWith("varchar", StringComparison.OrdinalIgnoreCase) ||
            sqlType.StartsWith("nvarchar", StringComparison.OrdinalIgnoreCase)) return "Char";
        if (sqlType.StartsWith("decimal", StringComparison.OrdinalIgnoreCase) ||
            sqlType.StartsWith("float", StringComparison.OrdinalIgnoreCase)) return "Decimal";
        if (sqlType.Equals("datetime", StringComparison.OrdinalIgnoreCase)) return "DateTime";
        if (sqlType.Equals("bit", StringComparison.OrdinalIgnoreCase)) return "Boolean";
        if (sqlType is "int" or "bigint" or "smallint" or "tinyint") return "Int32";
        return "Char";
    }

    /// <summary>Preselect a table (e.g. from the SQL Object tree's right-click menu) and load it.</summary>
    public async Task OpenTableAsync(bool useSysDatabase, string schema, string table)
    {
        _dbCombo.SelectedIndex = useSysDatabase ? 1 : 0;
        _tableBox.Text = table.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? table : $"{schema}.{table}";
        await LoadAsync();
    }

    private async Task LoadTableSuggestionsAsync()
    {
        try
        {
            var names = new List<string>();
            foreach (var useSys in new[] { false, true })
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys);
                foreach (var o in objs.Where(o => o.Kind == SqlObjectKind.Table))
                {
                    names.Add(o.QualifiedName);
                    names.Add(o.Name);
                }
            }
            var source = new AutoCompleteStringCollection();
            source.AddRange(names.Distinct().ToArray());
            _tableBox.AutoCompleteCustomSource = source;
        }
        catch { /* chưa kết nối — gõ tay vẫn được */ }
    }

    private (string schema, string table) ParseTableRef(string raw)
    {
        raw = raw.Trim().Trim('[', ']');
        var parts = raw.Replace("[", "").Replace("]", "").Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_tableBox.Text)) return;
        (_schema, _table) = ParseTableRef(_tableBox.Text);
        var useSys = _dbCombo.SelectedIndex == 1;
        var topN = int.TryParse(_topBox.Text, out var n) ? n : 500;

        _statusLabel.Text = "Đang tải...";
        _loadButton.Enabled = false;
        try
        {
            var data = await _service.LoadTableAsync(useSys, _schema, _table, topN);
            var isPeriodPlaceholder = _service.IsPeriodPlaceholder(_schema, _table);

            if (isPeriodPlaceholder)
            {
                _keyColumns = new List<string>();
                _keyLabel.Text = "ℹ [Schema].[Table]$000000 = gộp TẤT CẢ các bảng phân kỳ (UNION ALL), giống SQL Query/Command — " +
                                  "đây không phải 1 bảng vật lý nên KHÔNG Save được ở đây; chỉnh sửa qua Command/SQL Query rồi nhắm đúng bảng kỳ cụ thể.";
            }
            else
            {
                _keyColumns = await _service.GetPrimaryKeyColumnsAsync(useSys, _schema, _table);
                _keyLabel.Text = _keyColumns.Count > 0
                    ? $"Primary Key: {string.Join(", ", _keyColumns)}"
                    : "⚠ Bảng này không có Primary Key — Save sẽ báo lỗi trừ khi bạn tự set khoá (chưa hỗ trợ chọn tay ở bản này, hãy chỉnh sửa qua Command/SQL Query thay vì Table).";
            }

            await PopulateStructureListAsync(useSys, data);
            GridDisplayHelper.BindOptimized(_grid, data);
            _grid.ReadOnly = isPeriodPlaceholder;
            _saveButton.Enabled = false;
            _statusLabel.Text = $"{data.Rows.Count} dòng đã tải ([{_schema}].[{_table}])" +
                                 (isPeriodPlaceholder ? " — gộp mọi kỳ, chỉ xem, không Save được." : ".");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _loadButton.Enabled = true;
        }
    }

    private async Task SaveAsync()
    {
        if (_grid.DataSource is not DataTable data) return;
        var useSys = _dbCombo.SelectedIndex == 1;

        if (_service.IsPeriodPlaceholder(_schema, _table))
        {
            MessageBox.Show(this, "[Schema].[Table]$000000 là kết quả gộp TẤT CẢ bảng phân kỳ (UNION ALL), không phải 1 bảng vật lý — " +
                "không Save được ở đây. Dùng Command/SQL Query và nhắm đúng bảng kỳ cụ thể (vd r00$202601) để sửa dữ liệu.",
                "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_keyColumns.Count == 0)
        {
            MessageBox.Show(this, "Bảng không có Primary Key nên Table chưa lưu được an toàn. Dùng tool Command để tự viết UPDATE/INSERT/DELETE cho bảng này.",
                "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(this, $"Ghi thay đổi trực tiếp vào [{_schema}].[{_table}]?", "Bcode — Table",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _saveButton.Enabled = false;
        try
        {
            var count = await _service.SaveChangesAsync(useSys, _schema, _table, _keyColumns, data);
            _statusLabel.Text = $"Đã lưu {count} thay đổi.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _saveButton.Enabled = true;
        }
    }

    /// <summary>SqlQueryControl's GenInsertSelected/GenUpdateSelected read _grid.SelectedRows
    /// directly, which works there because that grid's SelectionMode is FullRowSelect. This
    /// grid is CellSelect instead (it's directly editable, unlike Command's read-only result
    /// grid), so a normal click-a-cell-then-right-click selection leaves SelectedRows empty —
    /// this falls back to the distinct rows behind whatever cells are selected instead.</summary>
    private static IEnumerable<DataRow> GetSelectedDataRows(DataGridView grid)
    {
        var rowIndexes = grid.SelectedRows.Count > 0
            ? grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index)
            : grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex).Distinct();

        return rowIndexes
            .Where(i => i >= 0 && i < grid.Rows.Count)
            .Select(i => grid.Rows[i])
            .Where(r => r.DataBoundItem is DataRowView)
            .Select(r => ((DataRowView)r.DataBoundItem!).Row);
    }

    private void GenInsertSelected()
    {
        if (_grid.DataSource is not DataTable table) return;
        var rows = GetSelectedDataRows(_grid).ToList();
        if (rows.Count == 0) { MessageBox.Show(this, "Chưa chọn dòng nào.", "Bcode — Gen Insert"); return; }

        var defaultTarget = _schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? _table : $"{_schema}.{_table}";
        var targetName = SimplePromptForm.Show(this, "Gen Insert", "Tên bảng đích cho câu lệnh INSERT:", defaultTarget);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        var sql = _genInsert.GenerateInsertStatements(table, targetName, rows);
        Clipboard.SetText(sql);
        MessageBox.Show(this, "Đã sinh câu lệnh INSERT và copy vào clipboard.", "Bcode",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void GenUpdateSelected()
    {
        if (_grid.DataSource is not DataTable table) return;
        var rows = GetSelectedDataRows(_grid).ToList();
        if (rows.Count == 0) { MessageBox.Show(this, "Chưa chọn dòng nào.", "Bcode — Gen Update"); return; }

        var defaultTarget = _schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? _table : $"{_schema}.{_table}";
        var targetName = SimplePromptForm.Show(this, "Gen Update", "Tên bảng đích cho câu lệnh UPDATE:", defaultTarget);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        var keyInput = SimplePromptForm.Show(this, "Gen Update",
            "Cột khoá (key) làm điều kiện WHERE, cách nhau bởi dấu phẩy (vd: stt_rec hoặc ma_ct,ky):",
            _keyColumns.Count > 0 ? string.Join(",", _keyColumns) : (table.Columns.Count > 0 ? table.Columns[0].ColumnName : ""));
        if (string.IsNullOrWhiteSpace(keyInput)) return;

        var keyColumns = keyInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sql = _genUpdate.GenerateUpdateStatements(table, targetName, keyColumns, rows);
        Clipboard.SetText(sql);
        MessageBox.Show(this, "Đã sinh câu lệnh UPDATE và copy vào clipboard.", "Bcode",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>"Add Script" — packages every row currently loaded in the grid into a DELETE +
    /// bulk-INSERT reload script for the loaded table. Target table name defaults to whatever's
    /// actually loaded ([schema].[table] as typed in the Table box), since — unlike Command,
    /// which has to guess from a free-form FROM clause — Table already knows exactly which
    /// table is on screen.
    ///
    /// Fix history: this used to show the generated script in a RichTextBox "Script" popup,
    /// syntax-highlighted — several rounds of trying to make THAT popup stay responsive for an
    /// 18k+-row script (background generation, precomputed RTF, chunked Select()+SelectionColor
    /// coloring, WordWrap tricks) all still ended up "Not Responding" at some scale, because the
    /// RichTextBox control itself is what doesn't scale to this much text/formatting, not any
    /// particular way of feeding it. Gen Insert/Gen Update just to the right of this (see
    /// GenInsertSelected/GenUpdateSelected in SqlQueryControl) never had this problem because
    /// they never show their result in a RichTextBox at all — they copy straight to the
    /// clipboard and confirm with a MessageBox ("Làm giống chức năng gen insert giống bên tab
    /// command, vì nhanh hơn rất nhiều"). This does the same: no popup, no highlighting, just
    /// clipboard + a status line. The script is also written to a scratch file and added to the
    /// Script Cart (silently, off the UI thread) so the toolbar's View/Save/Copy Script still
    /// pick it up — same as before, just without a RichTextBox anywhere in the path.</summary>
    private async Task GenDataScriptAsync()
    {
        if (_grid.DataSource is not DataTable data || data.Rows.Count == 0)
        {
            MessageBox.Show(this, "Chưa có dữ liệu để sinh Script — Load bảng trước.", "Bcode — Add Script");
            return;
        }

        var defaultTarget = _schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? _table : $"{_schema}.{_table}";
        var targetName = SimplePromptForm.Show(this, "Add Script",
            "Tên bảng đích (DELETE toàn bộ rồi nạp lại từ dữ liệu đang xem):", defaultTarget);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        _addScriptButton.Enabled = false;
        _statusLabel.Text = $"Đang sinh script cho {data.Rows.Count} dòng...";
        try
        {
            string script = null!;
            string path = null!;
            await Task.Run(() =>
            {
                script = _dataScript.GenerateDeleteAndReloadScript(data, targetName);
                var fileName = $"{targetName.Replace('.', '_')}_{DateTime.Now:yyyyMMdd_HHmmss}.sql";
                path = Path.Combine(Path.GetTempPath(), "Bcode", "GeneratedScripts", fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            });

            Clipboard.SetText(script);
            _scriptFileService.AddToCart(path);

            _statusLabel.Text = $"Đã sinh script ({data.Rows.Count} dòng) và copy vào clipboard.";
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
