using System.Data;
using System.Text;
using Bcode.App.Forms;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// "Table" tool — giao diện chỉnh sửa dữ liệu bảng trực tiếp, tích hợp ô nhập Fields trực tiếp trên thanh công cụ WebView2.
/// </summary>
public class TableEditControl : UserControl
{
    private readonly WebBarHost _barWeb;
    private int _dbIndex = 0;
    private string _tableInputText = "";
    private string _fieldsInputText = "*";
    private int _topValue = 500;

    private readonly Label _keyLabel;
    private readonly DataGridView _grid;
    private readonly ListView _structureList;
    private readonly CheckedListBox _fieldsList; 
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
    private bool _suppressFieldsChanged;

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

        // Thanh công cụ WebView2 chạy file tablebar.html (đã có sẵn ô nhập Fields)
        _barWeb = new WebBarHost("tablebar.html", height: 40);
        _barWeb.Message += async msg =>
        {
            var action = msg.TryGetProperty("action", out var a) ? a.GetString() : null;
            switch (action)
            {
                case "db":
                    _dbIndex = msg.TryGetProperty("value", out var dbVal) ? dbVal.GetInt32() : 0;
                    break;
                case "load":
                    _tableInputText = msg.TryGetProperty("table", out var tVal) ? tVal.GetString() ?? "" : "";
                    _fieldsInputText = msg.TryGetProperty("fields", out var fVal) ? fVal.GetString() ?? "*" : "*";
                    if (msg.TryGetProperty("top", out var topVal) && int.TryParse(topVal.GetString(), out var parsedTop))
                        _topValue = parsedTop;
                    await LoadAsync();
                    break;
                case "save":
                    await SaveAsync();
                    break;
                case "add-script":
                    await GenDataScriptAsync();
                    break;
            }
        };

        _keyLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };
        _statusLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            ReadOnly = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };

        WebMenu.AttachTo(_grid, () =>
        {
            var menu = new WebMenu()
                .AddCaption("Gen script")
                .Add("Gen Insert (dòng đã chọn)", GenInsertSelected)
                .Add("Gen Update (dòng đã chọn)", GenUpdateSelected, shortcut: "Ctrl+Shift+U");
            return ResultGridMenu.AddItemsTo(menu, _grid);
        });
        ResultGridMenu.WireShortcuts(_grid);
        _grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.U) { e.Handled = true; GenUpdateSelected(); }
        };

        // Cột bên trái: Danh sách cấu trúc cột (Structure) và danh sách chọn nhanh (Fields checklist)
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
            foreach (ListViewItem other in _structureList.SelectedItems) other.Selected = false;
            hit.Item.Selected = true;
            hit.Item.Focused = true;
            BuildStructureContextMenu().Show(_structureList, e.X, e.Y);
        };
        _structureList.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.A) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            _structureList.BeginUpdate();
            foreach (ListViewItem item in _structureList.Items) item.Checked = true;
            _structureList.EndUpdate();
        };

        _fieldsList = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
        _fieldsList.ItemCheck += (_, _) =>
        {
            if (_suppressFieldsChanged) return;
            // Khi người dùng bấm check trên danh sách, tự động gom lại và cập nhật lên ô Fields ở thanh toolbar trên cùng
            BeginInvoke(() => UpdateToolbarFieldsFromCheckedList());
        };
        _fieldsList.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.A) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            _suppressFieldsChanged = true;
            for (var i = 0; i < _fieldsList.Items.Count; i++) _fieldsList.SetItemChecked(i, true);
            _suppressFieldsChanged = false;
            UpdateToolbarFieldsFromCheckedList();
        };

        // Gom nhóm Structure và Fields vào TabControl bên trái
        var leftTabs = new TabControl { Dock = DockStyle.Fill };
        
        var structPage = new TabPage("Structure");
        structPage.Controls.Add(_structureList);
        var structHeader = new Panel { Dock = DockStyle.Top, Height = 24 };
        structHeader.Controls.Add(new Label { Text = "Structure", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(4, 0, 0, 0) });
        structPage.Controls.Add(structHeader);
        leftTabs.TabPages.Add(structPage);

        var fieldsPage = new TabPage("Fields");
        var fieldsHeader = new Panel { Dock = DockStyle.Top, Height = 24 };
        fieldsHeader.Controls.Add(new Label { Text = "Fields (Tích chọn để đưa lên Toolbar)", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(4, 0, 0, 0) });
        fieldsPage.Controls.Add(_fieldsList);
        fieldsPage.Controls.Add(fieldsHeader);
        leftTabs.TabPages.Add(fieldsPage);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(leftTabs);
        split.Panel2.Controls.Add(_grid);
        split.Panel1MinSize = 0;
        split.Panel2MinSize = 0;
        const int desiredStructureWidth = 220;
        void ApplySplitterDistance()
        {
            if (split.Width <= 0) return;
            var clamped = Math.Max(0, Math.Min(desiredStructureWidth, split.Width - split.SplitterWidth));
            if (split.SplitterDistance != clamped) split.SplitterDistance = clamped;
        }
        split.SizeChanged += (_, _) => ApplySplitterDistance();

        var queryPanel = new Panel { Dock = DockStyle.Fill };
        queryPanel.Controls.Add(split);
        queryPanel.Controls.Add(_keyLabel);
        queryPanel.Controls.Add(_statusLabel);

        Controls.Add(queryPanel);
        Controls.Add(_barWeb);
    }

    private void UpdateToolbarFieldsFromCheckedList()
    {
        var checkedNames = _fieldsList.CheckedItems.Cast<string>()
            .Select(s => s.EndsWith(" (PK)") ? s[..^5] : s)
            .ToList();

        var fieldsStr = checkedNames.Count == 0 ? "*" : string.Join(", ", checkedNames);
        _fieldsInputText = fieldsStr;
        _barWeb.Call($"window.setFields && window.setFields({WebBarHost.Json(fieldsStr)})");
    }

    private async Task PopulateStructureAndFieldsListAsync(bool useSysDatabase, DataTable data)
    {
        Dictionary<string, string>? realTypes = null;
        try { realTypes = await _service.GetColumnTypesAsync(useSysDatabase, _schema, _table); }
        catch { }

        _structureList.BeginUpdate();
        _structureList.Items.Clear();
        
        _suppressFieldsChanged = true;
        _fieldsList.Items.Clear();

        var allColNames = new List<string>();

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

            var fieldText = isKey ? $"{col.ColumnName} (PK)" : col.ColumnName;
            var fieldIdx = _fieldsList.Items.Add(fieldText);
            _fieldsList.SetItemChecked(fieldIdx, true);

            allColNames.Add(col.ColumnName);
        }

        _suppressFieldsChanged = false;
        _structureList.EndUpdate();
    }

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

    private List<(string Name, string Type)> GetTargetColumns()
    {
        var checkedCols = _structureList.CheckedItems.Cast<ListViewItem>()
            .Select(i => (i.Text, i.SubItems[1].Text)).ToList();
        if (checkedCols.Count > 0) return checkedCols;

        return _structureList.SelectedItems.Count > 0
            ? new List<(string, string)> { (_structureList.SelectedItems[0].Text, _structureList.SelectedItems[0].SubItems[1].Text) }
            : new List<(string, string)>();
    }

    private WebMenu BuildStructureContextMenu()
    {
        var menu = new WebMenu();
        AddGenGroup(menu, "Gen Structure Table", GenStructureTable);
        AddGenGroup(menu, "Gen Add Column", () => GenColumnDdl("ADD"));
        AddGenGroup(menu, "Gen Alter Column", () => GenColumnDdl("ALTER COLUMN"));
        AddGenGroup(menu, "Gen Drop Column", GenDropColumn);

        menu.AddCaption("Render XML");
        menu.Add("Render Dir XML", () => CopyAndPreview("Render Dir XML", RenderFieldXml));
        menu.Add("Render Grid XML", () => CopyAndPreview("Render Grid XML", RenderFieldXml));
        return menu;
    }

    private void AddGenGroup(WebMenu menu, string label, Func<string> generate)
    {
        menu.AddCaption(label);
        menu.Add("Add to Clipboard", () => { try { Clipboard.SetText(generate()); } catch { } });
        menu.Add("Preview", () => { using var form = new WCommandScriptForm(generate(), label); form.ShowDialog(this); });
    }

    private void CopyAndPreview(string title, Func<string> generate)
    {
        var script = generate();
        try { Clipboard.SetText(script); } catch { }
        using var form = new WCommandScriptForm(script, title);
        form.ShowDialog(this);
    }

    private string GenStructureTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{_schema}].[{_table}] (");
        var lines = _structureList.Items.Cast<ListViewItem>()
            .Select(i => $"    [{i.Text}] {i.SubItems[1].Text} {(i.SubItems[2].Text == "PK" ? "NOT NULL" : "NULL")}")
            .ToList();
        sb.Append(string.Join(",\r\n", lines));
        if (_keyColumns.Count > 0)
            sb.Append($",\r\n    CONSTRAINT [PK_{_table}] PRIMARY KEY ({string.Join(", ", _keyColumns.Select(k => k))})");
        sb.AppendLine();
        sb.AppendLine(");");
        return sb.ToString();
    }

    private string GenColumnDdl(string verb)
    {
        var cols = GetTargetColumns();
        if (cols.Count == 0) return "-- Chưa chọn cột nào.";
        return string.Join("\r\n", cols.Select(c => $"ALTER TABLE [{_schema}].[{_table}] {verb} [{c.Name}] {c.Type} NULL;"));
    }

    private string GenDropColumn()
    {
        var cols = GetTargetColumns();
        if (cols.Count == 0) return "-- Chưa chọn cột nào.";
        return string.Join("\r\n", cols.Select(c => $"ALTER TABLE [{_schema}].[{_table}] DROP COLUMN [{c.Name}];"));
    }

    private string RenderFieldXml()
    {
        var cols = GetTargetColumns();
        if (cols.Count == 0) return "<!-- Chưa chọn cột nào. -->";
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

    public async Task OpenTableAsync(bool useSysDatabase, string schema, string table)
    {
        _dbIndex = useSysDatabase ? 1 : 0;
        _tableInputText = table.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? table : $"{schema}.{table}";
        _barWeb.Call($"window.setDatabase && window.setDatabase({_dbIndex})");
        _barWeb.Call($"window.setTable && window.setTable({WebBarHost.Json(_tableInputText)})");
        await LoadAsync();
    }

    private (string schema, string table) ParseTableRef(string raw)
    {
        raw = raw.Trim().Trim('[', ']');
        var parts = raw.Replace("[", "").Replace("]", "").Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_tableInputText)) return;
        (_schema, _table) = ParseTableRef(_tableInputText);
        var useSys = _dbIndex == 1;
        var topN = _topValue;

        var fieldsToSelect = string.IsNullOrWhiteSpace(_fieldsInputText) ? "*" : _fieldsInputText.Trim();

        _statusLabel.Text = "Đang tải...";
        try
        {
            var data = await _service.LoadTableAsync(useSys, _schema, _table, topN, fieldsToSelect);
            var isPeriodPlaceholder = _service.IsPeriodPlaceholder(_schema, _table);

            if (isPeriodPlaceholder)
            {
                _keyColumns = new List<string>();
                _keyLabel.Text = "ℹ [Schema].[Table]$000000 = gộp TẤT CẢ các bảng phân kỳ (UNION ALL) — không Save được ở đây.";
            }
            else
            {
                _keyColumns = await _service.GetPrimaryKeyColumnsAsync(useSys, _schema, _table);
                _keyLabel.Text = _keyColumns.Count > 0
                    ? $"Primary Key: {string.Join(", ", _keyColumns)}"
                    : "⚠ Bảng không có Primary Key.";
            }

            await PopulateStructureAndFieldsListAsync(useSys, data);
            GridDisplayHelper.BindOptimized(_grid, data);
            _grid.ReadOnly = isPeriodPlaceholder;
            _statusLabel.Text = $"{data.Rows.Count} dòng đã tải ([{_schema}].[{_table}]) với các cột: [{fieldsToSelect}].";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task SaveAsync()
    {
        if (_grid.DataSource is not DataTable data) return;
        var useSys = _dbIndex == 1;

        if (_service.IsPeriodPlaceholder(_schema, _table))
        {
            MessageBox.Show(this, "Không thể Save trên bảng tổng hợp phân kỳ $000000.", "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_keyColumns.Count == 0)
        {
            MessageBox.Show(this, "Bảng không có Primary Key nên không thể lưu an toàn.", "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(this, $"Ghi thay đổi trực tiếp vào [{_schema}].[{_table}]?", "Bcode — Table",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            var count = await _service.SaveChangesAsync(useSys, _schema, _table, _keyColumns, data);
            _statusLabel.Text = $"Đã lưu {count} thay đổi.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

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
        MessageBox.Show(this, "Đã sinh câu lệnh INSERT và copy vào clipboard.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            "Cột khoá (key) làm điều kiện WHERE, cách nhau bởi dấu phẩy:",
            _keyColumns.Count > 0 ? string.Join(",", _keyColumns) : (table.Columns.Count > 0 ? table.Columns[0].ColumnName : ""));
        if (string.IsNullOrWhiteSpace(keyInput)) return;

        var keyColumns = keyInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sql = _genUpdate.GenerateUpdateStatements(table, targetName, keyColumns, rows);
        Clipboard.SetText(sql);
        MessageBox.Show(this, "Đã sinh câu lệnh UPDATE và copy vào clipboard.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

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
            MessageBox.Show(this, "Đã sinh script và copy vào clipboard.", "Bcode — Add Script", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Add Script", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}