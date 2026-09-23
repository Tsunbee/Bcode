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
    private string _whereInputText = "";
    private string _orderInputText = "";
    private int _topValue = 500;

    // Gợi ý tên bảng cho ô "Table" (giống gợi ý bảng bên SQL Query): danh sách bảng/view của
    // từng DB (0 = App, 1 = Sys) nạp 1 lần rồi cache, lọc ngay trong bộ nhớ theo từng phím gõ.
    private readonly Dictionary<int, Task<List<string>>> _tableNamesCache = new();
    private SuggestPopup? _tableSuggest;
    private int _tableSuggestRequest; // chỉ hiện kết quả của lần gõ mới nhất

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
    private bool _autoSaving; // chặn đệ quy — AcceptChanges() trong SaveChangesAsync có thể tự kích lại sự kiện của _grid

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
                    HideTableSuggest();
                    _ = GetTableNamesAsync(_dbIndex); // nạp trước danh sách gợi ý của DB vừa chọn
                    break;
                case "load":
                    HideTableSuggest();
                    _tableInputText = msg.TryGetProperty("table", out var tVal) ? tVal.GetString() ?? "" : "";
                    _fieldsInputText = msg.TryGetProperty("fields", out var fVal) ? fVal.GetString() ?? "*" : "*";
                    _whereInputText = msg.TryGetProperty("where", out var wVal) ? wVal.GetString() ?? "" : "";
                    _orderInputText = msg.TryGetProperty("order", out var oVal) ? oVal.GetString() ?? "" : "";
                    if (msg.TryGetProperty("top", out var topVal) && int.TryParse(topVal.GetString(), out var parsedTop))
                        _topValue = parsedTop;
                    await LoadAsync();
                    break;
                case "table-input":
                {
                    // Đọc hết giá trị TRƯỚC khi await — JsonElement chỉ còn hợp lệ trong lúc handler chạy đồng bộ.
                    var text = msg.TryGetProperty("value", out var vVal) ? vVal.GetString() ?? "" : "";
                    var x = msg.TryGetProperty("x", out var xVal) ? xVal.GetDouble() : 0;
                    var y = msg.TryGetProperty("y", out var yVal) ? yVal.GetDouble() : 0;
                    var w = msg.TryGetProperty("w", out var wwVal) ? wwVal.GetDouble() : 0;
                    var h = msg.TryGetProperty("h", out var hVal) ? hVal.GetDouble() : 0;
                    await ShowTableSuggestionsAsync(text, x, y, w, h);
                    break;
                }
                case "table-key":
                    HandleTableSuggestKey(msg.TryGetProperty("key", out var kVal) ? kVal.GetString() ?? "" : "");
                    break;
                case "table-blur":
                    HideTableSuggest();
                    break;
                case "save":
                    await SaveAsync();
                    break;
                case "add-script":
                    await GenDataScriptAsync();
                    break;
            }
        };

        // Vừa mở tab Table là con trỏ nằm sẵn ở ô "Table" (trước đây focus rơi xuống khung
        // Structure/Fields bên trái), đồng thời nạp trước danh sách bảng để gợi ý hiện ngay
        // từ phím gõ đầu tiên.
        _barWeb.Ready += () =>
        {
            _barWeb.FocusWeb();
            _barWeb.Call("window.focusTable && window.focusTable()");
            _ = GetTableNamesAsync(_dbIndex);
        };
        VisibleChanged += (_, _) => { if (!Visible) HideTableSuggest(); };
        Disposed += (_, _) => _tableSuggest?.Dispose();

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

        // Tự động ghi xuống bảng thật ngay khi rời khỏi dòng vừa nhập/sửa — giống hệt
        // kiểu "Edit Top 200 Rows" của SSMS, không cần bấm Save mới cập nhật. RowValidated
        // là lúc DataGridView coi dòng hiện tại đã "chốt" xong (rời sang dòng khác/click ra
        // ngoài), kể cả dòng mới gõ vào ở hàng trống cuối bảng (AllowUserToAddRows); còn
        // UserDeletedRow lo phần xoá dòng qua phím Delete/menu chuột phải. Nút "Save" trên
        // thanh công cụ vẫn còn — dùng để lưu thủ công có xác nhận khi cần.
        _grid.RowValidated += async (_, _) => await AutoSaveRowAsync();
        _grid.UserDeletedRow += async (_, _) => await AutoSaveRowAsync();

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

    // ------------------------------------------------------------------------------------
    // Gợi ý tên bảng cho ô "Table"
    // ------------------------------------------------------------------------------------

    private Task<List<string>> GetTableNamesAsync(int dbIndex)
    {
        if (!_tableNamesCache.TryGetValue(dbIndex, out var task) || task.IsFaulted || task.IsCanceled)
            _tableNamesCache[dbIndex] = task = LoadTableNamesAsync(dbIndex == 1);
        return task;
    }

    private async Task<List<string>> LoadTableNamesAsync(bool useSysDatabase)
    {
        var objects = await _sqlObjectService.ListObjectsAsync(useSysDatabase, "");
        return objects
            .Where(o => o.Kind is SqlObjectKind.Table or SqlObjectKind.View)
            .Select(o => o.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? o.Name : $"{o.Schema}.{o.Name}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Tên bắt đầu bằng chữ đang gõ lên trước (so cả tên có/không có schema), sau đó
    /// tới tên chỉ chứa chữ đó ở giữa — giống cách gợi ý bảng bên SQL Query.</summary>
    private static List<string> RankTableNames(List<string> names, string term, int max)
    {
        return names
            .Select(n =>
            {
                var dot = n.IndexOf('.');
                var shortName = dot >= 0 ? n[(dot + 1)..] : n;
                var rank = shortName.StartsWith(term, StringComparison.OrdinalIgnoreCase) || n.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 0
                    : n.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1
                    : -1;
                return (Name: n, Rank: rank);
            })
            .Where(t => t.Rank >= 0)
            .OrderBy(t => t.Rank)
            .ThenBy(t => t.Name.Length)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(t => t.Name)
            .ToList();
    }

    /// <param name="x">Toạ độ ô Table trong trang tablebar.html (đơn vị CSS px).</param>
    private async Task ShowTableSuggestionsAsync(string text, double x, double y, double w, double h)
    {
        var request = ++_tableSuggestRequest;
        var term = text.Trim().Trim('[', ']');
        if (term.Length == 0)
        {
            HideTableSuggest();
            return;
        }

        List<string> names;
        try
        {
            names = await GetTableNamesAsync(_dbIndex);
        }
        catch
        {
            return; // không kết nối được DB — không có gợi ý, không làm phiền người dùng
        }
        if (request != _tableSuggestRequest || IsDisposed || !Visible) return;

        var matches = RankTableNames(names, term, max: 50);
        if (matches.Count == 0 || (matches.Count == 1 && matches[0].Equals(term, StringComparison.OrdinalIgnoreCase)))
        {
            HideTableSuggest();
            return;
        }

        if (FindForm() is not { } owner) return;
        // CSS px → pixel thật trên màn hình theo DPI hiện tại của thanh công cụ.
        var scale = _barWeb.DeviceDpi / 96.0;
        var screen = _barWeb.PointToScreen(new Point(
            (int)Math.Round(x * scale),
            (int)Math.Round((y + h) * scale) + 2));
        EnsureTableSuggestPopup().ShowItems(owner, screen, (int)Math.Round(w * scale), matches);
        _barWeb.Call("window.setSuggestOpen && window.setSuggestOpen(true)");
    }

    private SuggestPopup EnsureTableSuggestPopup()
    {
        if (_tableSuggest is { IsDisposed: false }) return _tableSuggest;

        _tableSuggest = new SuggestPopup();
        _tableSuggest.Picked += PickTableSuggestion;

        // Popup là cửa sổ nổi riêng — đóng lại khi cửa sổ chính di chuyển/đổi kích thước/mất
        // focus để nó không lơ lửng lệch chỗ so với ô Table.
        if (FindForm() is { } form)
        {
            EventHandler hide = (_, _) => HideTableSuggest();
            form.Move += hide;
            form.Resize += hide;
            form.Deactivate += hide;
            Disposed += (_, _) =>
            {
                form.Move -= hide;
                form.Resize -= hide;
                form.Deactivate -= hide;
            };
        }
        return _tableSuggest;
    }

    private void HandleTableSuggestKey(string key)
    {
        if (_tableSuggest is not { IsDisposed: false, IsOpen: true }) return;
        switch (key)
        {
            case "down": _tableSuggest.MoveSelection(1); break;
            case "up": _tableSuggest.MoveSelection(-1); break;
            case "pagedown": _tableSuggest.MoveSelection(10); break;
            case "pageup": _tableSuggest.MoveSelection(-10); break;
            case "enter":
                if (_tableSuggest.SelectedText is { } picked) PickTableSuggestion(picked);
                else HideTableSuggest();
                break;
            case "escape": HideTableSuggest(); break;
        }
    }

    private void PickTableSuggestion(string name)
    {
        HideTableSuggest();
        _tableInputText = name;
        _barWeb.Call($"window.setTable && window.setTable({WebBarHost.Json(name)})");
    }

    private void HideTableSuggest()
    {
        _tableSuggestRequest++; // huỷ luôn kết quả gợi ý còn đang chờ (nếu có)
        if (_tableSuggest is { IsDisposed: false }) _tableSuggest.HidePopup();
        if (IsDisposed || Disposing || _barWeb.IsDisposed) return; // đang đóng tab — không gọi vào WebView2 nữa
        try { _barWeb.Call("window.setSuggestOpen && window.setSuggestOpen(false)"); }
        catch { /* WebView2 đang huỷ — bỏ qua */ }
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
            var data = await _service.LoadTableAsync(useSys, _schema, _table, topN, fieldsToSelect,
                _whereInputText, _orderInputText);
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
            var filterInfo = (string.IsNullOrWhiteSpace(_whereInputText) ? "" : $" — Where: {_whereInputText.Trim()}")
                           + (string.IsNullOrWhiteSpace(_orderInputText) ? "" : $" — Order: {_orderInputText.Trim()}");
            _statusLabel.Text = $"{data.Rows.Count} dòng đã tải ([{_schema}].[{_table}]) với các cột: [{fieldsToSelect}]{filterInfo}.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Ghi ngay các thay đổi đang chờ (dòng mới thêm/sửa/xoá) xuống bảng thật,
    /// không hỏi xác nhận — gọi từ RowValidated/UserDeletedRow mỗi khi Bee rời khỏi một dòng
    /// vừa chỉnh, giống cách SSMS tự lưu dòng trong "Edit Top 200 Rows" mà không cần nút Save.
    /// Im lặng bỏ qua (không phải lỗi) khi: bảng tổng hợp phân kỳ $000000 (không ghi được),
    /// bảng chưa xác định Primary Key (nhãn ⚠ phía trên đã cảnh báo sẵn, Bee cần tự chọn cột
    /// khoá rồi Save thủ công), hoặc chưa có gì thay đổi thật sự (tránh mở kết nối vô ích mỗi
    /// lần chỉ di chuyển ô chọn qua lại).</summary>
    private async Task AutoSaveRowAsync()
    {
        if (_autoSaving) return;
        if (_grid.DataSource is not DataTable data) return;
        if (_service.IsPeriodPlaceholder(_schema, _table)) return;
        if (_keyColumns.Count == 0) return;
        if (data.GetChanges() is null) return;

        _autoSaving = true;
        try
        {
            var useSys = _dbIndex == 1;
            var count = await _service.SaveChangesAsync(useSys, _schema, _table, _keyColumns, data);
            if (count > 0)
                _statusLabel.Text = $"Đã tự động lưu {count} thay đổi lúc {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            // Ví dụ vi phạm ràng buộc NOT NULL/khoá khi dòng vừa nhập chưa đủ dữ liệu — báo
            // ngay cho Bee biết dòng đó CHƯA lưu được, giống ô đỏ cảnh báo của SSMS, thay vì
            // âm thầm mất thay đổi.
            MessageBox.Show(this, $"Không tự động lưu được thay đổi:\n{ex.Message}", "Bcode — Table",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _autoSaving = false;
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