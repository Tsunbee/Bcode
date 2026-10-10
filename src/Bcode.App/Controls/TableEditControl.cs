using System.Data;
using System.Text;
using System.Text.Json;
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
    private SuggestPopup? _colSuggest;   // gợi ý tên cột cho ô Where/Order/Fields
    private int _tableSuggestRequest; // chỉ hiện kết quả của lần gõ mới nhất

    private readonly Label _keyLabel;
    /// <summary>Dòng trạng thái ở đáy lưới: đang đứng ở dòng mấy / khoá của dòng / dòng mới - đã sửa chưa lưu / đang lưu.</summary>
    private readonly Label _rowLabel;
    private int _hiRow = -1;
    private readonly DataGridView _grid;
    /// <summary>Khung trái Structure / Fields — trang WebView2 Web/Shell/tablestruct.html (đồng bộ giao diện với thanh trên).</summary>
    private readonly WebBarHost _structWeb;
    /// <summary>Cột của bảng đang mở theo đúng thứ tự (tên, kiểu SQL thật, khoá chính) — nguồn cho khung Structure / Fields, gợi ý cột, Gen script.</summary>
    private List<(string Name, string Type, bool IsKey)> _structCols = new();
    private List<string> _structChecked = new();   // cột đang tick ở tab Structure (theo thứ tự bảng)
    private string? _structSelected;                // dòng đang chọn ở tab Structure (chuột phải / click)
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
    private string _structureKey = "";      // (DB|schema|bảng) mà danh sách Structure đang hiển thị
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
        _barWeb = new WebBarHost("tablebar.html", height: 76); // 2 hàng; chiều cao thật do trang báo lại (__height)
        _barWeb.Message += async msg =>
        {
            var action = msg.TryGetProperty("action", out var a) ? a.GetString() : null;
            switch (action)
            {
                case "__height":
                    // Chiều cao thật của thanh (px thiết bị) — theo UiScale/DPI và khi thanh tự thu nhỏ cho vừa bề ngang.
                    _barWeb.Height = Math.Clamp(msg.GetProperty("height").GetInt32() + 1, DpiScale.Px(_barWeb, 30), DpiScale.Px(_barWeb, 160));
                    break;
                case "db":
                    _dbIndex = msg.TryGetProperty("value", out var dbVal) ? dbVal.GetInt32() : 0;
                    HideTableSuggest();
                    _ = GetTableNamesAsync(_dbIndex); // nạp trước danh sách gợi ý của DB vừa chọn
                    break;
                case "load":
                    HideTableSuggest();
                    HideColumnSuggest();
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
                case "col-input":
                {
                    var term = msg.TryGetProperty("term", out var cT) ? cT.GetString() ?? "" : "";
                    var cx = msg.TryGetProperty("x", out var cxV) ? cxV.GetDouble() : 0;
                    var cy = msg.TryGetProperty("y", out var cyV) ? cyV.GetDouble() : 0;
                    var cw = msg.TryGetProperty("w", out var cwV) ? cwV.GetDouble() : 0;
                    var ch = msg.TryGetProperty("h", out var chV) ? chV.GetDouble() : 0;
                    ShowColumnSuggestions(term, cx, cy, cw, ch);
                    break;
                }
                case "col-key":
                    HandleColumnSuggestKey(msg.TryGetProperty("key", out var ckV) ? ckV.GetString() ?? "" : "");
                    break;
                case "col-blur":
                    HideColumnSuggest();
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
        Disposed += (_, _) => { _tableSuggest?.Dispose(); _colSuggest?.Dispose(); };

        _keyLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };
        _statusLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0), Visible = false };
        _statusLabel.TextChanged += (_, _) => _statusLabel.Visible = _statusLabel.Text.Length > 0; // chỉ hiện khi có thông báo (lỗi, tự động lưu...)

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            ReadOnly = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };

        // ---- Cho biết đang xử lý ở dòng nào: tô cả dòng + vạch màu nhấn bên trái + nhãn ở đáy lưới ----
        _grid.RowPostPaint += (_, e) =>
        {
            if (e.RowIndex != _hiRow) return;
            var acc = Bcode.App.UI.AppColors.Accent;
            using (var fill = new SolidBrush(Color.FromArgb(34, acc))) e.Graphics.FillRectangle(fill, e.RowBounds);
            using (var bar = new SolidBrush(acc)) e.Graphics.FillRectangle(bar, e.RowBounds.Left, e.RowBounds.Top, 4, e.RowBounds.Height);
            using var pen = new Pen(Color.FromArgb(150, acc));
            e.Graphics.DrawLine(pen, e.RowBounds.Left, e.RowBounds.Top, e.RowBounds.Right, e.RowBounds.Top);
            e.Graphics.DrawLine(pen, e.RowBounds.Left, e.RowBounds.Bottom - 1, e.RowBounds.Right, e.RowBounds.Bottom - 1);
        };
        // Ô đang NULL được tô màu hổ phách (như FCode) để nhìn ra ngay — chạy Alter Null Value xong thì các ô này hết màu.
        _grid.CellFormatting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || e.RowIndex >= _grid.Rows.Count || e.Value is not (null or DBNull)) return;
            if (_grid.Rows[e.RowIndex].IsNewRow || _grid.Columns[e.ColumnIndex] is DataGridViewCheckBoxColumn) return;
            e.CellStyle.BackColor = Bcode.App.UI.AppColors.IsDark ? Color.FromArgb(140, 90, 14) : Color.FromArgb(255, 220, 160);
        };
        _grid.CurrentCellChanged += (_, _) => UpdateRowHighlight();
        _grid.CellValueChanged += (_, _) => UpdateRowLabel();
        _grid.DataBindingComplete += (_, _) => UpdateRowHighlight();
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
            if (ResultGridMenu.TryBuildHeaderMenu(_grid) is { } headerMenu) return headerMenu;
            string K(string id) { var d = Bcode.App.UI.ShortcutRegistry.Display(id); return d.Length > 0 ? d : null!; }
            var menu = new WebMenu()
                .AddCaption("Dòng")
                .Add("Add New", AddNewRow, shortcut: K("table.addNew"))
                .Add("Delete", () => _ = DeleteSelectedRowsAsync(), shortcut: K("table.delete"), danger: true)
                .AddSeparator()
                .Add("Copy Row", CopySelectedRows, shortcut: "Ctrl+C")
                .Add("Paste", () => _ = PasteFromClipboardAsync(), shortcut: "Ctrl+V")
                .Add("Clone Row", () => _ = CloneRowAsync(), shortcut: K("table.cloneRow"))
                .Add("Insert New Row After", InsertRowAfter, shortcut: K("table.insertAfter"))
                .Add("Copy One Value", CopyOneValue, shortcut: K("table.copyValue"))
                .AddSeparator()
                .Add("Alter Null Value", () => _ = AlterNullValueAsync())
                .AddCaption("Gen script")
                .Add("Gen Insert (dòng đã chọn)", GenInsertSelected)
                .Add("Gen Update (dòng đã chọn)", GenUpdateSelected, shortcut: "Ctrl+Shift+U");
            ResultGridMenu.AddItemsTo(menu, _grid);
            menu.AddSeparator()
                .Add("Set Cells Value to NULL", () => _ = SetCellsToNullAsync(), shortcut: K("table.setNull"))
                .Add("Description Columns...", () => _ = ShowColumnDescriptionsAsync())
                .Add("Run Table Async (" + (AppSettings.TableRunAsync ? "Enabled" : "Disabled") + ")", () => AppSettings.TableRunAsync = !AppSettings.TableRunAsync, @checked: AppSettings.TableRunAsync);
            return menu;
        });
        ResultGridMenu.WireShortcuts(_grid);
        _grid.KeyDown += async (_, e) =>
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.U) { e.Handled = true; GenUpdateSelected(); }
            else if (e.KeyCode == Keys.F5 && !e.Control && !e.Shift && !e.Alt) { e.Handled = true; e.SuppressKeyPress = true; ReloadFromBar(); }
            else if (e.Control && e.KeyCode == Keys.V && ClipboardLooksLikeTable())
            {
                // Dán từ Excel (nhiều ô: cột cách nhau bằng Tab, dòng bằng xuống dòng) — dán theo ô, không dồn hết vào 1 ô.
                e.Handled = true; e.SuppressKeyPress = true;
                await PasteFromClipboardAsync();
            }
        };

        // Cột bên trái: Structure (cấu trúc cột) + Fields (tick nhanh cột đưa lên ô Fields) — trang WebView2 tablestruct.html,
        // cùng kiểu giao diện với thanh công cụ phía trên. Trang giữ tick/lọc/chọn dòng; C# giữ danh sách cột và lo Gen script.
        _structWeb = new WebBarHost("tablestruct.html", height: 200) { Dock = DockStyle.Fill };
        _structWeb.Ready += PushStructure;
        _structWeb.Message += msg =>
        {
            // JsonElement chỉ hợp lệ trong handler — đọc hết ngay tại đây.
            var action = msg.TryGetProperty("action", out var a) ? a.GetString() : null;
            List<string> Names() => msg.TryGetProperty("names", out var n) && n.ValueKind == JsonValueKind.Array
                ? n.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new();
            switch (action)
            {
                case "structChecked":
                    // Tick cột ở Structure → đẩy đúng các cột đã tick lên ô Fields trên thanh công cụ (thay cho *); bỏ tick hết → quay lại *.
                    _structChecked = Names();
                    SetToolbarFields(_structChecked);
                    break;
                case "fieldsChecked":
                    SetToolbarFields(Names());
                    break;
                case "select":
                    _structSelected = msg.TryGetProperty("name", out var s) ? s.GetString() : null;
                    break;
                case "menu":
                    _structSelected = msg.TryGetProperty("name", out var m) ? m.GetString() : null;
                    BeginInvoke(() => BuildStructureContextMenu().Show(_structWeb, Cursor.Position));
                    break;
                case "reload":
                    BeginInvoke(ReloadFromBar);
                    break;
            }
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, FixedPanel = FixedPanel.Panel1 };
        split.Panel1.Controls.Add(_structWeb);
        split.Panel2.Controls.Add(_grid);
        _rowLabel = new Label { Dock = DockStyle.Bottom, Height = 22, ForeColor = Bcode.App.UI.AppColors.Accent, Padding = new Padding(6, 3, 0, 0), AutoEllipsis = true };
        split.Panel2.Controls.Add(_rowLabel);
        var selSummary = new Label { Dock = DockStyle.Bottom, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0), Visible = false };
        selSummary.TextChanged += (_, _) => selSummary.Visible = selSummary.Text.Length > 0; // chỉ hiện khi đang quét khối ô số
        split.Panel2.Controls.Add(selSummary);
        ResultGridMenu.AttachSelectionSummary(_grid, selSummary);
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

    /// <summary>F5 = như bấm Enter/Load trên thanh công cụ: đọc lại các ô Table/Fields/Where/Order/Top đang hiện rồi tải lại dữ liệu.</summary>
    private void ReloadFromBar() => _barWeb.Call("window.triggerLoad && window.triggerLoad()");

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // F5 ở bất kỳ control WinForms nào của tab Table (lưới, danh sách cột...) cũng lọc lại; trong thanh WebView2 thì trang tự bắt F5.
        if (keyData == Keys.F5) { ReloadFromBar(); return true; }
        if (TryRunGridShortcut(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---- F1 (xem / sửa chi tiết dòng) và F3 (khai báo nhanh danh sách) — phím cấu hình được ở Template → Phím tắt (nhóm "Lưới Table") ----

    private bool TryRunGridShortcut(Keys keyData)
    {
        if (!_grid.ContainsFocus || _loading || _grid.DataSource is not DataTable) return false;
        var combo = Bcode.App.UI.ShortcutRegistry.FromKeys(keyData);
        if (combo is null) return false;
        if (combo == Bcode.App.UI.ShortcutRegistry.Get("table.rowDetail")) { BeginInvoke(new Action(() => _ = ShowRowDetailAsync())); return true; }
        if (combo == Bcode.App.UI.ShortcutRegistry.Get("table.listEditor")) { BeginInvoke(new Action(() => _ = ShowListEditorAsync())); return true; }
        if (combo == Bcode.App.UI.ShortcutRegistry.Get("table.addNew")) { BeginInvoke(new Action(AddNewRow)); return true; }
        if (combo == Bcode.App.UI.ShortcutRegistry.Get("table.delete")) { BeginInvoke(new Action(() => _ = DeleteSelectedRowsAsync())); return true; }
        if (combo == Bcode.App.UI.ShortcutRegistry.Get("table.cloneRow")) { BeginInvoke(new Action(() => _ = CloneRowAsync())); return true; }
        if (combo == Bcode.App.UI.ShortcutRegistry.Get("table.insertAfter")) { BeginInvoke(new Action(InsertRowAfter)); return true; }
        if (combo == Bcode.App.UI.ShortcutRegistry.Get("table.copyValue")) { BeginInvoke(new Action(CopyOneValue)); return true; }
        if (combo == Bcode.App.UI.ShortcutRegistry.Get("table.setNull")) { BeginInvoke(new Action(() => _ = SetCellsToNullAsync())); return true; }
        return false;
    }

    // ---------------------------------------------------------------- dòng đang xử lý
    private void UpdateRowHighlight()
    {
        var now = _grid.CurrentCell?.RowIndex ?? -1;
        if (now != _hiRow)
        {
            var old = _hiRow; _hiRow = now;
            if (old >= 0 && old < _grid.Rows.Count) _grid.InvalidateRow(old);
            if (now >= 0 && now < _grid.Rows.Count) _grid.InvalidateRow(now);
        }
        UpdateRowLabel();
    }

    /// <summary>"▶ Dòng 9 / 26 · form = Add_Phieu_Nhap_Kho · cột Note — đã sửa, chưa lưu".</summary>
    private void UpdateRowLabel(string? busy = null)
    {
        if (busy is not null) { _rowLabel.Text = busy; return; }
        var cell = _grid.CurrentCell;
        if (cell is null || _grid.DataSource is not DataTable) { _rowLabel.Text = ""; return; }
        var r = cell.RowIndex;
        if (r < 0 || r >= _grid.Rows.Count) { _rowLabel.Text = ""; return; }
        var total = _grid.Rows.Count - (_grid.AllowUserToAddRows ? 1 : 0);
        var col = cell.OwningColumn is { } oc ? GridColName(oc) : "";
        if (_grid.Rows[r].IsNewRow) { _rowLabel.Text = "➕ Dòng mới — nhập xong rồi chuyển sang dòng khác để tự lưu"; return; }
        var text = "▶ Dòng " + (r + 1) + " / " + total;
        if (_grid.Rows[r].DataBoundItem is DataRowView drv)
        {
            var keys = _keyColumns.Where(k => drv.Row.Table.Columns.Contains(k)).Select(k => k + " = " + (drv.Row.RowState == DataRowState.Deleted || drv.Row.IsNull(k) ? "NULL" : Convert.ToString(drv.Row[k]))).ToList();
            if (keys.Count > 0) text += "  ·  " + string.Join(", ", keys);
            if (col.Length > 0) text += "  ·  cột " + col + (cell.Value is null or DBNull ? " = NULL" : cell.Value is string sv && sv.Length == 0 ? " = '' (trống)" : "");
            text += drv.Row.RowState switch { DataRowState.Added => "  —  dòng mới, chưa lưu", DataRowState.Modified => "  —  đã sửa, chưa lưu", _ => "" };
        }
        _rowLabel.Text = text;
    }

    private DataRow? RowAt(int gridRow) =>
        gridRow >= 0 && gridRow < _grid.Rows.Count && _grid.Rows[gridRow].DataBoundItem is DataRowView d ? d.Row : null;

    private List<int> SelectedRowIndexes()
    {
        var idx = _grid.SelectedRows.Count > 0
            ? _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index)
            : _grid.SelectedCells.Cast<DataGridViewCell>().Select(c => c.RowIndex);
        var list = idx.Distinct().Where(i => i >= 0 && i < _grid.Rows.Count && !_grid.Rows[i].IsNewRow).OrderBy(i => i).ToList();
        if (list.Count == 0 && _grid.CurrentCell is { RowIndex: >= 0 } cc && !_grid.Rows[cc.RowIndex].IsNewRow) list.Add(cc.RowIndex);
        return list;
    }

    private bool CanEditRows(out string why)
    {
        why = "";
        if (_grid.DataSource is not DataTable) why = "Chưa có bảng nào đang mở.";
        else if (_loading) why = "Đang tải dữ liệu.";
        else if (_grid.ReadOnly || _service.IsPeriodPlaceholder(_schema, _table)) why = "Bảng này chỉ xem (bảng tổng hợp phân kỳ $000000 hoặc đang ở chế độ chỉ đọc).";
        if (why.Length > 0) _statusLabel.Text = why;
        return why.Length == 0;
    }

    /// <summary>F4 — Add New: nhảy xuống dòng trống cuối lưới và vào chế độ gõ.</summary>
    private void AddNewRow()
    {
        if (!CanEditRows(out _)) return;
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        if (!_grid.AllowUserToAddRows || _grid.NewRowIndex < 0) { _statusLabel.Text = "Lưới không cho thêm dòng."; return; }
        var col = _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible && !c.ReadOnly).OrderBy(c => c.DisplayIndex).FirstOrDefault();
        if (col is null) return;
        _grid.CurrentCell = _grid.Rows[_grid.NewRowIndex].Cells[col.Index];
        _grid.BeginEdit(true);
    }

    /// <summary>F8 — Delete: xoá các dòng đang chọn (có hỏi), rồi tự lưu như thao tác xoá thường.</summary>
    private async Task DeleteSelectedRowsAsync()
    {
        if (!CanEditRows(out _) || _grid.DataSource is not DataTable) return;
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        var idx = SelectedRowIndexes();
        if (idx.Count == 0) { _statusLabel.Text = "Chọn dòng cần xoá."; return; }
        if (_keyColumns.Count == 0) { _statusLabel.Text = "Bảng không có Primary Key nên không xoá an toàn được."; return; }
        var ask = MessageBox.Show(this, $"Xoá {idx.Count} dòng (dòng {string.Join(", ", idx.Take(8).Select(i => i + 1))}{(idx.Count > 8 ? "…" : "")}) khỏi [{_schema}].[{_table}]?\n\nSẽ ghi ngay vào database ({DescribeStamp(_loadedStamp)}).",
            "Bcode — Table", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ask != DialogResult.Yes) return;
        foreach (var r in idx.Select(RowAt).Where(r => r is not null).ToList()) r!.Delete();
        _grid.Refresh();
        UpdateRowLabel("🗑 Đang xoá " + idx.Count + " dòng…");
        await AutoSaveRowAsync();
        UpdateRowHighlight();
    }

    /// <summary>Copy Row — copy các dòng đang chọn (cột đang hiện, cách nhau Tab) để dán sang Excel / dán lại ở đây.</summary>
    private void CopySelectedRows()
    {
        if (_grid.DataSource is not DataTable) return;
        var idx = SelectedRowIndexes();
        if (idx.Count == 0) return;
        var cols = _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).ToList();
        var sb = new StringBuilder();
        foreach (var i in idx)
            sb.AppendLine(string.Join("\t", cols.Select(c => Convert.ToString(_grid.Rows[i].Cells[c.Index].FormattedValue)?.Replace("\t", " ").Replace("\r", " ").Replace("\n", " ") ?? "")));
        try { Clipboard.SetText(sb.ToString()); _statusLabel.Text = $"Đã copy {idx.Count} dòng."; } catch { /* clipboard bận */ }
    }

    /// <summary>Copy One Value — chỉ giá trị của ô đang đứng (không tiêu đề, không các ô khác).</summary>
    private void CopyOneValue()
    {
        if (_grid.CurrentCell is not { RowIndex: >= 0 } c) return;
        var v = c.Value is null or DBNull ? "" : Convert.ToString(c.Value) ?? "";
        try { Clipboard.SetText(v.Length > 0 ? v : " "); _statusLabel.Text = "Đã copy giá trị ô."; } catch { /* clipboard bận */ }
    }

    private static object DefaultFor(DataColumn dc)
    {
        if (dc.AllowDBNull) return DBNull.Value;
        var t = dc.DataType;
        if (t == typeof(string)) return "";
        if (t == typeof(bool)) return false;
        if (t == typeof(DateTime)) return DateTime.Now;
        if (t == typeof(Guid)) return Guid.NewGuid();
        if (t == typeof(byte[])) return Array.Empty<byte>();
        try { return Convert.ChangeType(0, t); } catch { return DBNull.Value; }
    }

    /// <summary>Ctrl+N — Insert New Row After: chèn dòng trống (giá trị mặc định cho cột NOT NULL) ngay sau dòng đang đứng; gõ khoá rồi chuyển dòng để tự lưu.</summary>
    private void InsertRowAfter()
    {
        if (!CanEditRows(out _) || _grid.DataSource is not DataTable data) return;
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        var at = _grid.CurrentCell is { RowIndex: >= 0 } c && !_grid.Rows[c.RowIndex].IsNewRow ? c.RowIndex + 1 : data.Rows.Count;
        var nr = data.NewRow();
        foreach (DataColumn dc in data.Columns) if (!dc.AutoIncrement && !dc.ReadOnly) { try { nr[dc] = DefaultFor(dc); } catch { /* giữ mặc định */ } }
        try { data.Rows.InsertAt(nr, Math.Min(at, data.Rows.Count)); }
        catch (Exception ex) { _statusLabel.Text = "Không chèn được dòng: " + ex.Message; return; }
        FocusKeyCellOf(Math.Min(at, _grid.Rows.Count - 1));
        _statusLabel.Text = "Đã chèn dòng mới sau dòng " + at + " — nhập khoá / dữ liệu, rồi chuyển dòng để tự lưu.";
    }

    private void FocusKeyCellOf(int gridRow)
    {
        if (gridRow < 0 || gridRow >= _grid.Rows.Count) return;
        var col = _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible && !c.ReadOnly)
            .OrderBy(c => _keyColumns.Contains(GridColName(c), StringComparer.OrdinalIgnoreCase) ? 0 : 1).ThenBy(c => c.DisplayIndex).FirstOrDefault();
        if (col is null) return;
        _grid.CurrentCell = _grid.Rows[gridRow].Cells[col.Index];
        _grid.BeginEdit(true);
    }

    /// <summary>Ctrl+I — Clone Row: nhân bản dòng đang đứng thành dòng mới ngay bên dưới; cột khoá được đổi (chuỗi thêm _2, số = lớn nhất + 1) để không trùng.</summary>
    private async Task CloneRowAsync()
    {
        if (!CanEditRows(out _) || _grid.DataSource is not DataTable data) return;
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        if (_grid.CurrentCell is not { RowIndex: >= 0 } c || RowAt(c.RowIndex) is not { } src) { _statusLabel.Text = "Đứng ở một dòng đã có dữ liệu rồi bấm Clone Row."; return; }
        if (_keyColumns.Count == 0) { _statusLabel.Text = "Bảng không có Primary Key nên không nhân bản an toàn được."; return; }
        var nr = data.NewRow();
        foreach (DataColumn dc in data.Columns) if (!dc.AutoIncrement && !dc.ReadOnly) { try { nr[dc] = src[dc]; } catch { /* bỏ qua */ } }
        var changed = new List<string>();
        foreach (var k in _keyColumns)
        {
            if (data.Columns[k] is not { } kc || kc.AutoIncrement || src.IsNull(kc)) continue;
            if (kc.DataType == typeof(string))
            {
                var baseVal = Convert.ToString(src[kc])!.TrimEnd(); var n = 2; string cand;
                do { cand = baseVal + "_" + n++; } while (data.AsEnumerable().Any(r => r.RowState != DataRowState.Deleted && string.Equals(Convert.ToString(r[kc])?.TrimEnd(), cand, StringComparison.OrdinalIgnoreCase)));
                nr[kc] = kc.MaxLength > 0 && cand.Length > kc.MaxLength ? cand[..kc.MaxLength] : cand; changed.Add(k + " = " + nr[kc]);
            }
            else if (kc.DataType.IsPrimitive || kc.DataType == typeof(decimal))
            {
                try { var mx = data.AsEnumerable().Where(r => r.RowState != DataRowState.Deleted && !r.IsNull(kc)).Max(r => Convert.ToDecimal(r[kc])); nr[kc] = Convert.ChangeType(mx + 1, kc.DataType); changed.Add(k + " = " + nr[kc]); } catch { /* giữ nguyên */ }
            }
        }
        var ask = MessageBox.Show(this, $"Nhân bản dòng {c.RowIndex + 1} thành dòng mới" + (changed.Count > 0 ? $" ({string.Join(", ", changed)})" : "") + $"?\n\nSẽ tự lưu vào [{_schema}].[{_table}] ({DescribeStamp(_loadedStamp)}) khi bạn chuyển sang dòng khác.", "Bcode — Table", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;
        try { data.Rows.InsertAt(nr, c.RowIndex + 1); }
        catch (Exception ex) { _statusLabel.Text = "Không nhân bản được: " + ex.Message; return; }
        FocusKeyCellOf(c.RowIndex + 1);
        _statusLabel.Text = "Đã nhân bản dòng " + (c.RowIndex + 1) + " — sửa lại khoá nếu cần, rồi chuyển dòng để tự lưu.";
        await Task.CompletedTask;
    }

    /// <summary>Ctrl+0 — Set Cells Value to NULL: các ô đang chọn (chỉ cột cho phép NULL) thành NULL rồi tự lưu.</summary>
    private async Task SetCellsToNullAsync()
    {
        if (!CanEditRows(out _) || _grid.DataSource is not DataTable data) return;
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        var done = 0; var skipped = 0;
        foreach (var cell in _grid.SelectedCells.Cast<DataGridViewCell>().Where(c => c.RowIndex >= 0 && !_grid.Rows[c.RowIndex].IsNewRow))
        {
            if (RowAt(cell.RowIndex) is not { } row || data.Columns[GridColName(cell.OwningColumn)] is not { } dc) { skipped++; continue; }
            if (!dc.AllowDBNull || dc.ReadOnly) { skipped++; continue; }
            try { row[dc] = DBNull.Value; done++; } catch { skipped++; }
        }
        _grid.Refresh();
        _statusLabel.Text = done == 0 ? "Chọn các ô (cột cho phép NULL) rồi bấm Set Cells Value to NULL." : $"Đã đặt {done} ô thành NULL" + (skipped > 0 ? $" ({skipped} ô không cho NULL nên bỏ qua)" : "") + ".";
        if (done > 0) await AutoSaveRowAsync();
    }

    /// <summary>Alter Null Value — như fsd_AlterNullTable của FastBusiness, làm cho TOÀN BẢNG: cột chữ đang NULL → '' (trống), cột số / bit đang NULL → 0,
    /// cột ngày (smalldatetime / datetime / date) đang để ngày rỗng 1900-01-01 → NULL. Chỉ đụng các dòng cần đổi; hiện số dòng từng cột trước khi chạy.</summary>
    private async Task AlterNullValueAsync()
    {
        if (_table.Length == 0 || _grid.DataSource is not DataTable) { _statusLabel.Text = "Mở một bảng trước."; return; }
        if (_service.IsPeriodPlaceholder(_schema, _table)) { _statusLabel.Text = "Không sửa được bảng tổng hợp phân kỳ $000000."; return; }
        if (!WorkspaceStillMatches(out var mismatch)) { _statusLabel.Text = mismatch; return; }
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        List<(string Name, string Type, bool Nullable, bool IsKey, bool Identity, string Description)> cols;
        try { cols = await _service.GetColumnDescriptionsAsync(_loadedUseSys, _schema, _table); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bcode — Alter Null Value", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }

        // phân loại cột theo kiểu dữ liệu
        static string Kind(string type)
        {
            var ty = type.ToLowerInvariant();
            if (ty.StartsWith("varchar") || ty.StartsWith("char") || ty.StartsWith("nvarchar") || ty.StartsWith("nchar") || ty.StartsWith("text") || ty.StartsWith("ntext")) return "text";
            if (ty.StartsWith("smalldatetime") || ty.StartsWith("datetime") || ty == "date") return "date";
            foreach (var n in new[] { "int", "bigint", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "smallmoney", "float", "real" }) if (ty == n || ty.StartsWith(n + "(")) return "num";
            return "";   // binary, timestamp, uniqueidentifier, xml... — không đụng
        }
        var work = cols.Where(c => !c.Identity && c.Nullable && Kind(c.Type) != "").Select(c => (c.Name, Kind: Kind(c.Type))).ToList();
        if (work.Count == 0) { _statusLabel.Text = "Bảng không có cột nào cần chuẩn hoá (cột cho phép NULL kiểu chữ / số / ngày)."; return; }

        var full = "[" + _schema + "].[" + _table + "]";
        string Cond(string col, string kind) => kind == "date"
            ? $"[{col}] IS NOT NULL AND CONVERT(VARCHAR(8), [{col}], 112) IN ('19000101', '17530101')"
            : $"[{col}] IS NULL";
        var countSql = "SELECT " + string.Join(", ", work.Select((w, i) => $"SUM(CASE WHEN {Cond(w.Name, w.Kind)} THEN 1 ELSE 0 END) AS c{i}")) + " FROM " + full + " WITH (NOLOCK)";
        Dictionary<string, long> counts;
        UpdateRowLabel("🔎 Đang đếm các giá trị cần chuẩn hoá…");
        try { counts = await _service.QueryCountsAsync(_loadedUseSys, countSql); }
        catch (Exception ex) { UpdateRowLabel(); MessageBox.Show(this, ex.Message, "Bcode — Alter Null Value", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        UpdateRowLabel();

        var todo = work.Select((w, i) => (w.Name, w.Kind, Rows: counts.TryGetValue("c" + i, out var n) ? n : 0)).Where(x => x.Rows > 0).ToList();
        if (todo.Count == 0) { _statusLabel.Text = "Bảng đã chuẩn: không có giá trị NULL cần đổi thành trống / 0 và không có ngày rỗng cần đổi thành NULL."; return; }
        string Stmt((string Name, string Kind, long Rows) x) => x.Kind switch
        {
            "text" => $"UPDATE {full} SET [{x.Name}] = '' WHERE [{x.Name}] IS NULL",
            "num" => $"UPDATE {full} SET [{x.Name}] = 0 WHERE [{x.Name}] IS NULL",
            _ => $"UPDATE {full} SET [{x.Name}] = NULL WHERE {Cond(x.Name, "date")}",
        };
        var lines = todo.Select(x => $"  {x.Name}:  {x.Rows:N0} dòng  →  " + (x.Kind == "text" ? "'' (trống)" : x.Kind == "num" ? "0" : "NULL (ngày rỗng)"));
        var ask = MessageBox.Show(this, $"Chuẩn hoá giá trị của TOÀN BẢNG {full}:\n\n{string.Join("\n", lines.Take(14))}{(todo.Count > 14 ? $"\n  … ({todo.Count - 14} cột nữa)" : "")}\n\nCập nhật dữ liệu thật trên {DescribeStamp(_loadedStamp)}. Chạy?",
            "Bcode — Alter Null Value", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (ask != DialogResult.Yes) return;
        UpdateRowLabel("⚙ Đang chuẩn hoá " + todo.Count + " cột…");
        List<string> errors;
        try { errors = await _service.ExecuteEachAsync(_loadedUseSys, todo.Select(Stmt)); }
        catch (Exception ex) { errors = new List<string> { ex.Message }; }
        UpdateRowLabel();
        _statusLabel.Text = errors.Count == 0 ? $"Đã chuẩn hoá {todo.Count} cột ({todo.Sum(x => x.Rows):N0} giá trị)." : $"Xong, {errors.Count} lỗi: " + errors[0];
        if (errors.Count > 0) MessageBox.Show(this, string.Join("\n\n", errors.Take(6)), "Bcode — Alter Null Value (có lỗi)", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        ReloadFromBar();
    }

    /// <summary>Description Columns… — danh sách cột của bảng: kiểu, NULL, khoá, mô tả (MS_Description).</summary>
    private async Task ShowColumnDescriptionsAsync()
    {
        if (_table.Length == 0) { _statusLabel.Text = "Mở một bảng trước."; return; }
        List<(string Name, string Type, bool Nullable, bool IsKey, bool Identity, string Description)> cols;
        try { cols = await _service.GetColumnDescriptionsAsync(_loadedUseSys, _schema, _table); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bcode — Description Columns", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        using var form = new Form { Text = $"Description Columns — [{_schema}].[{_table}]", Width = 920, Height = 600, StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        var g = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells };
        g.Columns.Add("c", "Cột"); g.Columns.Add("t", "Kiểu"); g.Columns.Add("n", "Cho NULL"); g.Columns.Add("k", "Khoá"); g.Columns.Add("i", "Identity"); g.Columns.Add("d", "Mô tả");
        g.Columns[5].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (var c in cols) g.Rows.Add(c.Name, c.Type, c.Nullable ? "NULL" : "NOT NULL", c.IsKey ? "🔑" : "", c.Identity ? "identity" : "", c.Description);
        var note = new Label { Dock = DockStyle.Bottom, Height = 22, Padding = new Padding(6, 3, 0, 0), Text = cols.Any(c => c.Description.Length > 0) ? $"{cols.Count} cột." : $"{cols.Count} cột — bảng này chưa khai mô tả (MS_Description) cho cột nào." };
        form.Controls.Add(g); form.Controls.Add(note);
        form.KeyPreview = true; form.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) form.Close(); };
        Bcode.App.UI.ThemeManager.Apply(form);
        form.ShowDialog(FindForm());
    }

    private static string GridColName(DataGridViewColumn c) => c.DataPropertyName.Length > 0 ? c.DataPropertyName : c.Name;

    /// <summary>F3: các ô đang chọn của MỘT dòng, mỗi ô là danh sách "a, b, c" → hộp sửa từng giá trị (thêm / xoá / đổi thứ tự) rồi Save ghép lại.</summary>
    private async Task ShowListEditorAsync()
    {
        if (_grid.DataSource is not DataTable data) return;
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        var cells = _grid.SelectedCells.Cast<DataGridViewCell>().Where(c => c.RowIndex >= 0).ToList();
        if (cells.Count == 0 && _grid.CurrentCell is { RowIndex: >= 0 } cc) cells.Add(cc);
        if (cells.Count == 0) { _statusLabel.Text = "Chọn các ô (cột) của một dòng rồi bấm F3."; return; }
        var rowIndex = _grid.CurrentCell is { RowIndex: >= 0 } cur && cells.Any(c => c.RowIndex == cur.RowIndex) ? cur.RowIndex : cells[0].RowIndex;
        if (_grid.Rows[rowIndex].DataBoundItem is not DataRowView drv) { _statusLabel.Text = "Chọn ô của một dòng đã có dữ liệu."; return; }

        var items = new List<(string Column, string Value)>();
        foreach (var cell in cells.Where(c => c.RowIndex == rowIndex).OrderBy(c => c.OwningColumn.DisplayIndex))
        {
            var dc = data.Columns[GridColName(cell.OwningColumn)];
            if (dc is null || dc.ReadOnly || dc.DataType != typeof(string)) continue;   // chỉ cột chữ mới là danh sách
            items.Add((dc.ColumnName, drv.Row.IsNull(dc) ? "" : Convert.ToString(drv.Row[dc]) ?? ""));
        }
        if (items.Count == 0) { _statusLabel.Text = "Các ô đang chọn không phải cột chữ — không khai báo danh sách được."; return; }

        using var form = new Bcode.App.Forms.ListEditorForm($"[{_schema}].[{_table}] — dòng {rowIndex + 1}", items);
        if (form.ShowDialog(FindForm()) != DialogResult.OK) return;
        foreach (var (name, joined) in form.Values)
            if (data.Columns[name] is { } dc && TryConvertCell(joined, dc, out var v)) drv.Row[dc] = v;
        _grid.Refresh();
        _statusLabel.Text = $"Đã cập nhật {form.Values.Count} ô.";
        await AutoSaveRowAsync();
    }

    /// <summary>F1: mọi cột của dòng đang chọn thành một form (nhãn + ô nhập; danh sách "a, b, c" hiện thành dải ô nhỏ) — OK ghi lại các cột đã sửa.</summary>
    private async Task ShowRowDetailAsync()
    {
        if (_grid.DataSource is not DataTable data) return;
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        if (_grid.CurrentRow is not { } row || row.DataBoundItem is not DataRowView drv) { _statusLabel.Text = "Chọn một dòng đã có dữ liệu rồi bấm F1."; return; }

        var ci = System.Globalization.CultureInfo.CurrentCulture;
        var fields = new List<Bcode.App.Forms.RowDetailField>();
        foreach (var gc in _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex))
        {
            var dc = data.Columns[GridColName(gc)];
            if (dc is null) continue;
            var t = Nullable.GetUnderlyingType(dc.DataType) ?? dc.DataType;
            var kind = t == typeof(bool) ? "bool" : t == typeof(DateTime) ? "date"
                : t == typeof(string) ? "text" : t.IsPrimitive || t == typeof(decimal) ? "number" : "text";
            string? value = drv.Row.IsNull(dc) ? null : drv.Row[dc] switch
            {
                bool b => b ? "True" : "False",
                DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", ci),
                IFormattable f => f.ToString(null, ci),
                byte[] bytes => $"0x… ({bytes.Length} bytes)",
                var o => Convert.ToString(o, ci),
            };
            fields.Add(new Bcode.App.Forms.RowDetailField(dc.ColumnName, kind, value, dc.ReadOnly || _grid.ReadOnly || t == typeof(byte[]), t.Name));
        }
        using var form = new Bcode.App.Forms.RowDetailForm($"[{_schema}].[{_table}] — dòng {row.Index + 1}", fields);
        if (form.ShowDialog(FindForm()) != DialogResult.OK || form.Changed.Count == 0) return;

        var errors = 0;
        foreach (var (name, raw) in form.Changed)
        {
            if (data.Columns[name] is not { } dc) continue;
            if (raw is null) { if (dc.AllowDBNull) drv.Row[dc] = DBNull.Value; else errors++; continue; }
            if (TryConvertCell(raw, dc, out var v)) drv.Row[dc] = v; else errors++;
        }
        _grid.Refresh();
        _statusLabel.Text = $"Đã sửa {form.Changed.Count - errors} ô" + (errors > 0 ? $", {errors} ô không đổi được kiểu dữ liệu nên bỏ qua" : "") + ".";
        await AutoSaveRowAsync();
    }

    // ---- Dán từ Excel vào lưới ----------------------------------------------------------------------------------------

    private static bool ClipboardLooksLikeTable()
    {
        try
        {
            if (!Clipboard.ContainsText()) return false;
            var t = Clipboard.GetText();
            return t.Contains('\t') || t.TrimEnd('\r', '\n').Contains('\n');
        }
        catch { return false; }
    }

    /// <summary>Tách văn bản clipboard của Excel thành lưới ô: Tab giữa các cột, xuống dòng giữa các dòng; ô có xuống dòng / Tab / nháy kép
    /// được Excel bọc trong "..." (nháy kép bên trong gấp đôi).</summary>
    private static List<List<string>> ParseClipboardTable(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new System.Text.StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"' && cell.Length == 0)
            {
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"') { if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i += 2; continue; } i++; break; }
                    cell.Append(text[i]); i++;
                }
            }
            else if (c == '\t') { row.Add(cell.ToString()); cell.Clear(); i++; }
            else if (c == '\r' || c == '\n')
            {
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row); row = new List<string>();
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++;
            }
            else { cell.Append(c); i++; }
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    private static bool TryConvertCell(string raw, DataColumn col, out object value)
    {
        value = DBNull.Value;
        var t = col.DataType;
        if (t == typeof(string)) { value = raw; return true; }
        var s = raw.Trim();
        if (s.Length == 0) return col.AllowDBNull;
        try
        {
            if (t == typeof(bool))
            {
                if (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" || s.Equals("x", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase)) { value = true; return true; }
                if (s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0" || s.Equals("no", StringComparison.OrdinalIgnoreCase)) { value = false; return true; }
                return false;
            }
            if (t == typeof(DateTime))
            {
                if (DateTime.TryParse(s, System.Globalization.CultureInfo.CurrentCulture, System.Globalization.DateTimeStyles.None, out var d1) ||
                    DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out d1)) { value = d1; return true; }
                return false;
            }
            if (t == typeof(byte[]) || t == typeof(Guid)) return false;
            // Số: thử theo định dạng máy (dấu phẩy thập phân kiểu Việt) rồi theo invariant.
            foreach (var culture in new[] { System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.InvariantCulture })
            {
                try { value = Convert.ChangeType(s.Replace(" ", ""), t, culture)!; return true; }
                catch (FormatException) { /* thử cách khác */ }
                catch (OverflowException) { return false; }
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Ctrl+V nhiều ô: dán từ ô đang chọn, theo thứ tự cột đang hiện; vượt quá số dòng hiện có thì thêm dòng mới. Thay đổi đi qua DataTable nên
    /// được ghi vào DB bằng đúng đường tự động lưu hiện có — vì vậy hỏi xác nhận trước khi dán nhiều dòng.</summary>
    private async Task PasteFromClipboardAsync()
    {
        if (_grid.DataSource is not DataTable data) return;
        if (_loading) return;
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        if (_grid.CurrentCell is null) { _statusLabel.Text = "Chọn ô bắt đầu rồi dán."; return; }

        List<List<string>> table;
        try { table = ParseClipboardTable(Clipboard.GetText()); }
        catch (Exception ex) { _statusLabel.Text = "Không đọc được clipboard: " + ex.Message; return; }
        if (table.Count == 0) return;

        var visibleCols = _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex).ToList();
        var startCol = visibleCols.FindIndex(c => c.Index == _grid.CurrentCell.ColumnIndex);
        var startRow = _grid.CurrentCell.RowIndex;
        if (startCol < 0) return;
        var cols = table.Max(r => r.Count);
        if (startCol + cols > visibleCols.Count) cols = visibleCols.Count - startCol; // thừa cột thì bỏ phần dư bên phải

        if (table.Count > 1 && !_service.IsPeriodPlaceholder(_schema, _table))
        {
            var ask = MessageBox.Show(this,
                $"Dán {table.Count} dòng × {cols} cột vào [{_schema}].[{_table}] từ ô đang chọn?\n\nThay đổi sẽ được tự động ghi vào database ({DescribeStamp(_loadedStamp)}).",
                "Bcode — Table", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask != DialogResult.Yes) return;
        }

        var errors = 0; var cells = 0; var added = 0;
        var view = _grid.DataSource is DataTable ? data.DefaultView : null;
        for (var r = 0; r < table.Count; r++)
        {
            var gridRow = startRow + r;
            DataRow? dataRow = null;
            if (gridRow < _grid.Rows.Count && _grid.Rows[gridRow].DataBoundItem is DataRowView drv) dataRow = drv.Row;
            else
            {
                dataRow = data.NewRow(); // vượt quá cuối bảng (hoặc dòng "thêm mới" trống): tạo dòng mới
                added++;
                data.Rows.Add(dataRow);
            }
            for (var c = 0; c < cols && c < table[r].Count; c++)
            {
                var dc = data.Columns[visibleCols[startCol + c].DataPropertyName.Length > 0 ? visibleCols[startCol + c].DataPropertyName : visibleCols[startCol + c].Name];
                if (dc is null || dc.ReadOnly) continue;
                if (!TryConvertCell(table[r][c], dc, out var val)) { errors++; continue; }
                dataRow[dc] = val;
                cells++;
            }
        }
        _grid.Refresh();
        _statusLabel.Text = $"Đã dán {cells} ô ({table.Count} dòng × {cols} cột" + (added > 0 ? $", thêm {added} dòng mới" : "") + (errors > 0 ? $", {errors} ô không đổi được kiểu dữ liệu nên bỏ qua" : "") + ").";
        await AutoSaveRowAsync();
    }

    /// <summary>Đưa các cột đã tick (theo thứ tự bảng) lên ô Fields của thanh công cụ; không tick cột nào → "*".</summary>
    private void SetToolbarFields(IReadOnlyCollection<string> names)
    {
        var fieldsStr = names.Count == 0 ? "*" : string.Join(", ", names);
        _fieldsInputText = fieldsStr;
        _barWeb.Call($"window.setFields && window.setFields({WebBarHost.Json(fieldsStr)})");
    }

    /// <summary>Đẩy danh sách cột + trạng thái tick xuống khung Structure / Fields (gọi lại được — trang tự vẽ lại).</summary>
    private void PushStructure()
    {
        var fields = _fieldsInputText.Trim() is "" or "*"
            ? _structCols.Select(c => c.Name).ToList()
            : _fieldsInputText.Split(',').Select(x => x.Trim().Trim('[', ']')).Where(x => x.Length > 0).ToList();
        var payload = new
        {
            cols = _structCols.Select(c => new { name = c.Name, type = c.Type, key = c.IsKey }),
            structChecked = _structChecked,
            fields,
        };
        _structWeb.Call($"window.setColumns && window.setColumns({JsonSerializer.Serialize(payload)})");
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

        // Bảng khớp ở database CÒN LẠI (App ↔ Sys) cũng được gợi ý, kèm nhãn (kể cả trùng tên với DB đang chọn — để chọn được bảng ở DB kia); chọn thì tự chuyển DB (xem PickTableSuggestion).
        var otherIndex = _dbIndex == 1 ? 0 : 1;
        var otherMatches = new List<string>();
        try
        {
            otherMatches = RankTableNames(await GetTableNamesAsync(otherIndex), term, max: 30)
                .Select(n => n + OtherDbMarker(otherIndex))
                .ToList();
        }
        catch { /* DB còn lại không truy cập được — chỉ gợi ý trong DB đang chọn */ }
        if (request != _tableSuggestRequest || IsDisposed || !Visible) return;
        matches = matches.Concat(otherMatches).ToList();

        if (matches.Count == 0 || (matches.Count == 1 && matches[0].Equals(term, StringComparison.OrdinalIgnoreCase)))
        {
            HideTableSuggest();
            return;
        }

        if (FindForm() is not { } owner) return;
        // CSS px → pixel thật trên màn hình theo DPI hiện tại của thanh công cụ.
        var scale = _barWeb.DeviceDpi / 96.0 * UiScale.Factor; // + ZoomFactor của UiScale (WebView2 phóng/thu theo hệ số này)
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

    private static string OtherDbMarker(int dbIndex) => dbIndex == 1 ? "   ·  Sys Data" : "   ·  App Data";

    private void PickTableSuggestion(string name)
    {
        HideTableSuggest();
        // Gợi ý của DB còn lại có đuôi nhãn → bỏ nhãn và chuyển combo "DB:" sang DB đó.
        foreach (var idx in new[] { 0, 1 })
        {
            var marker = OtherDbMarker(idx);
            if (!name.EndsWith(marker, StringComparison.Ordinal)) continue;
            name = name[..^marker.Length];
            SwitchDatabase(idx);
            break;
        }
        _tableInputText = name;
        _barWeb.Call($"window.setTable && window.setTable({WebBarHost.Json(name)})");
    }

    private void SwitchDatabase(int dbIndex)
    {
        _dbIndex = dbIndex;
        _barWeb.Call($"window.setDatabase && window.setDatabase({dbIndex})");
        _ = GetTableNamesAsync(dbIndex);
    }

    /// <summary>Tên bảng gõ vào có nằm trong danh sách <paramref name="names"/> (dbo.xxx coi như xxx) không.</summary>
    private static bool ContainsTable(List<string> names, string raw)
    {
        var n = raw.Trim().Replace("[", "").Replace("]", "");
        if (n.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)) n = n[4..];
        return names.Any(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Bảng không có trong DB đang chọn nhưng CÓ trong DB còn lại (vd gõ bảng của Sys Data khi đang ở App Data) → tự chuyển
    /// sang DB đó trước khi tải. Không chắc chắn (không đọc được danh sách bảng) thì giữ nguyên lựa chọn của người dùng.</summary>
    private async Task AutoSwitchDatabaseForTableAsync(string rawTable)
    {
        try
        {
            if (ContainsTable(await GetTableNamesAsync(_dbIndex), rawTable)) return;
            var other = _dbIndex == 1 ? 0 : 1;
            if (!ContainsTable(await GetTableNamesAsync(other), rawTable)) return;
            SwitchDatabase(other);
            _statusLabel.Text = $"Bảng chỉ có trong {(other == 1 ? "Sys Data" : "App Data")} — đã tự chuyển DB.";
        }
        catch { /* không kết nối được để kiểm tra — để LoadAsync báo lỗi như bình thường */ }
    }

    // ---- Gợi ý tên cột (Where / Order / Fields) ----------------------------------------------------

    private void ShowColumnSuggestions(string term, double x, double y, double w, double h)
    {
        if (IsDisposed || !Visible || term.Length == 0) { HideColumnSuggest(); return; }
        var names = _structCols.Select(c => c.Name).ToList(); // cột của bảng đang mở (đủ cột, kể cả khi đang chọn một phần)
        var matches = names
            .Select(n => (Name: n, Rank: n.StartsWith(term, StringComparison.OrdinalIgnoreCase) ? 0 : n.Contains(term, StringComparison.OrdinalIgnoreCase) ? 1 : -1))
            .Where(t => t.Rank >= 0)
            .OrderBy(t => t.Rank).ThenBy(t => t.Name.Length).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => t.Name).Take(40).ToList();
        if (matches.Count == 0 || (matches.Count == 1 && matches[0].Equals(term, StringComparison.OrdinalIgnoreCase))) { HideColumnSuggest(); return; }

        if (FindForm() is not { } owner) return;
        var scale = _barWeb.DeviceDpi / 96.0 * UiScale.Factor;
        var screen = _barWeb.PointToScreen(new Point((int)Math.Round(x * scale), (int)Math.Round((y + h) * scale) + 2));
        EnsureColumnSuggestPopup().ShowItems(owner, screen, (int)Math.Round(w * scale), matches);
        _barWeb.Call("window.setColSuggestOpen && window.setColSuggestOpen(true)");
    }

    private SuggestPopup EnsureColumnSuggestPopup()
    {
        if (_colSuggest is { IsDisposed: false }) return _colSuggest;
        _colSuggest = new SuggestPopup();
        _colSuggest.Picked += name =>
        {
            HideColumnSuggest();
            _barWeb.Call($"window.insertColumn && window.insertColumn({WebBarHost.Json(name)})");
        };
        if (FindForm() is { } form)
        {
            EventHandler hide = (_, _) => HideColumnSuggest();
            form.Move += hide;
            form.Resize += hide;
            form.Deactivate += hide;
            Disposed += (_, _) => { form.Move -= hide; form.Resize -= hide; form.Deactivate -= hide; };
        }
        return _colSuggest;
    }

    private void HandleColumnSuggestKey(string key)
    {
        if (_colSuggest is not { IsDisposed: false, IsOpen: true }) return;
        switch (key)
        {
            case "down": _colSuggest.MoveSelection(1); break;
            case "up": _colSuggest.MoveSelection(-1); break;
            case "pagedown": _colSuggest.MoveSelection(10); break;
            case "pageup": _colSuggest.MoveSelection(-10); break;
            case "enter":
                if (_colSuggest.SelectedText is { } picked)
                {
                    HideColumnSuggest();
                    _barWeb.Call($"window.insertColumn && window.insertColumn({WebBarHost.Json(picked)})");
                }
                else HideColumnSuggest();
                break;
            case "escape": HideColumnSuggest(); break;
        }
    }

    private void HideColumnSuggest()
    {
        if (_colSuggest is { IsDisposed: false }) _colSuggest.HidePopup();
        if (IsDisposed || Disposing || _barWeb.IsDisposed) return;
        try { _barWeb.Call("window.setColSuggestOpen && window.setColSuggestOpen(false)"); }
        catch { /* WebView2 đang huỷ — bỏ qua */ }
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
        // Đang tải với danh sách cột đã chọn (không phải *) trên CÙNG bảng: giữ nguyên Structure đủ cột + các dấu tick,
        // nếu dựng lại thì chỉ còn những cột vừa tải và mất hết tick.
        var structureKey = $"{useSysDatabase}|{_schema}|{_table}".ToLowerInvariant();
        if (structureKey == _structureKey && _structCols.Count > 0 && _fieldsInputText.Trim() != "*") return;
        _structureKey = structureKey;

        Dictionary<string, string>? realTypes = null;
        try { realTypes = await _service.GetColumnTypesAsync(useSysDatabase, _schema, _table); }
        catch { }

        _structCols = data.Columns.Cast<DataColumn>().Select(col =>
        {
            var isKey = _keyColumns.Contains(col.ColumnName, StringComparer.OrdinalIgnoreCase);
            var typeText = realTypes is not null && realTypes.TryGetValue(col.ColumnName, out var realType)
                ? realType
                : FallbackClrTypeGuess(col);
            return (col.ColumnName, typeText, isKey);
        }).ToList();
        _structChecked = new List<string>();
        _structSelected = null;
        PushStructure();
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
        // Cột đã tick ở Structure (theo thứ tự bảng); chưa tick cột nào thì lấy dòng đang chọn / vừa chuột phải.
        var ticked = new HashSet<string>(_structChecked, StringComparer.OrdinalIgnoreCase);
        var checkedCols = _structCols.Where(c => ticked.Contains(c.Name)).Select(c => (c.Name, c.Type)).ToList();
        if (checkedCols.Count > 0) return checkedCols;

        return _structCols.Where(c => c.Name.Equals(_structSelected, StringComparison.OrdinalIgnoreCase))
            .Select(c => (c.Name, c.Type)).Take(1).ToList();
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
        var lines = _structCols
            .Select(c => $"    [{c.Name}] {c.Type} {(c.IsKey ? "NOT NULL" : "NULL")}")
            .ToList();
        sb.Append(string.Join(",\r\n", lines));
        if (_keyColumns.Count > 0)
            sb.Append($",\r\n    CONSTRAINT [PK_{_table}] PRIMARY KEY ({string.Join(", ", _keyColumns.Select(k => k))})");
        sb.AppendLine();
        sb.AppendLine(");");
        return sb.ToString();
    }

    // ---- Gen Add / Alter / Drop Column — theo đúng kiểu script FastBusiness -------------------------
    // Mỗi cột: IF [NOT] EXISTS (syscolumns) + ALTER TABLE + GO (chạy lại nhiều lần không lỗi). Bảng chia kỳ (xxx$000000):
    // làm thêm cho xxx$log (nếu bảng log có thật), rồi FastBusiness$Partition$Execute để áp lên mọi kỳ xxx$%Partition trong
    // khoảng ngày khoá sổ (dmstt.ngay_gh1..ngay_gh2); riêng ADD còn update giá trị mặc định (số = 0, chữ = '') cho dòng cũ.

    /// <summary>Tên bảng log đi kèm bảng chia kỳ đang mở (vd "m81$log"), null nếu không phải bảng chia kỳ / không có bảng log.</summary>
    private string? _logTable;

    /// <summary>"m81$000000" / "m81$202401" → "m81"; bảng thường → null.</summary>
    private static string? PartitionPrefix(string table)
    {
        var m = System.Text.RegularExpressions.Regex.Match(table, @"^(.+)\$\d{6}$");
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Giá trị gán cho dòng cũ sau khi thêm cột (trong chuỗi SQL động nên chữ rỗng = ''''); null = không update (ngày, kiểu khác).</summary>
    private static string? DefaultFill(string sqlType)
    {
        var t = sqlType.ToLowerInvariant();
        var paren = t.IndexOf('(');
        if (paren >= 0) t = t[..paren];
        return t switch
        {
            "tinyint" or "smallint" or "int" or "bigint" or "bit" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "0",
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" => "''''",
            _ => null,
        };
    }

    private string GenColumnDdl(string verb) => GenColumnScript(verb == "ADD" ? "ADD" : "ALTER COLUMN");

    private string GenDropColumn() => GenColumnScript("DROP COLUMN");

    private string GenColumnScript(string action)
    {
        var cols = GetTargetColumns();
        return cols.Count == 0 ? "-- Chưa chọn cột nào." : BuildColumnScript(_schema, _table, _logTable, cols, action);
    }

    /// <summary>Script thêm / sửa / xoá cột (action = "ADD" | "ALTER COLUMN" | "DROP COLUMN") cho bảng + bảng log + các kỳ.</summary>
    internal static string BuildColumnScript(string schema, string mainTable, string? logTable, IReadOnlyList<(string Name, string Type)> cols, string action)
    {
        string Qualified(string table) => schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? $"dbo.{table}" : $"{schema}.{table}";
        var add = action == "ADD";
        var drop = action == "DROP COLUMN";
        string Def(string name, string type) => drop ? name : $"{name} {type}";

        var tables = new List<string> { mainTable };
        if (logTable != null) tables.Add(logTable);

        var sb = new System.Text.StringBuilder();
        foreach (var table in tables)
        {
            var q = Qualified(table);
            foreach (var (name, type) in cols)
            {
                sb.AppendLine($"IF {(add ? "NOT " : "")}EXISTS (SELECT * FROM syscolumns WHERE id = object_id('{q}') AND name = '{name}')");
                sb.AppendLine($"ALTER TABLE {q} {action} {Def(name, type)}");
                sb.AppendLine("GO");
                sb.AppendLine();
            }
            if (table != tables[^1]) { sb.AppendLine(); sb.AppendLine(); }   // cách giữa bảng chính và bảng log
        }

        if (PartitionPrefix(mainTable) is { } prefix)
        {
            const string exec = "exec FastBusiness$Partition$Execute @strsql, '', 'ngay_ct', @dFrom, @dTo, 1, 1";
            sb.AppendLine("Declare @strsql NVARCHAR(4000), @dFrom SMALLDATETIME, @dTo SMALLDATETIME");
            sb.AppendLine("Select @dFrom = ngay_gh1, @dTo = ngay_gh2 from dmstt");
            sb.AppendLine();
            sb.AppendLine("set @strsql = '' ");
            foreach (var (name, type) in cols)
                sb.AppendLine($"Set @strsql = @strsql + char(13) + 'alter table [{prefix}$%Partition] {action.ToLowerInvariant()} {Def(name, type)} '");
            sb.AppendLine(exec);

            var fills = add ? cols.Select(c => (c.Name, Fill: DefaultFill(c.Type))).Where(x => x.Fill != null).ToList() : new();
            if (fills.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("set @strsql = '' ");
                foreach (var (name, fill) in fills)
                    sb.AppendLine($"set @strsql = @strsql + char(13) + 'update {prefix}$%Partition set {name} = {fill} where  %[ {name} is null]% '");
                sb.AppendLine(exec);
            }
            sb.AppendLine("GO");
        }
        return sb.ToString().TrimEnd() + "\r\n";
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

    /// <summary>Báo tên bảng vừa tải xong ("dmkh", "r00$000000"; schema khác dbo thì "schema.bảng") để MainForm đặt tên tab theo bảng đang xem.</summary>
    public event Action<string>? TableLoaded;

    /// <summary>Bảng đang hiển thị (đã tải xong) — để lưu / khôi phục phiên làm việc; null khi chưa tải bảng nào.</summary>
    public (bool Sys, string Schema, string Table)? LoadedTable => string.IsNullOrEmpty(_table) ? null : (_loadedUseSys, _schema, _table);

    /// <summary>Bộ lọc của lần tải gần nhất (Fields / Where / Order / Top) — lưu cùng phiên làm việc.</summary>
    public (string Fields, string Where, string Order, int Top) LoadedFilter => (_fieldsInputText, _whereInputText, _orderInputText, _topValue);

    public async Task OpenTableAsync(bool useSysDatabase, string schema, string table, (string Fields, string Where, string Order, int Top)? filter = null)
    {
        _dbIndex = useSysDatabase ? 1 : 0;
        _tableInputText = table.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? table : $"{schema}.{table}";
        if (filter is { } f)
        {
            _fieldsInputText = string.IsNullOrWhiteSpace(f.Fields) ? "*" : f.Fields;
            _whereInputText = f.Where ?? "";
            _orderInputText = f.Order ?? "";
            _topValue = f.Top > 0 ? f.Top : _topValue;
        }
        // Thanh nhập (WebView2) có thể chưa nạp xong (tab khôi phục từ phiên cũ vừa dựng): Call lúc đó là no-op nên ô Table / Where... để trống dù dữ liệu vẫn tải. Chờ trang sẵn sàng rồi mới đặt.
        await _barWeb.WaitReadyAsync();
        _barWeb.Call($"window.setDatabase && window.setDatabase({_dbIndex})");
        _barWeb.Call($"window.setTable && window.setTable({WebBarHost.Json(_tableInputText)})");
        if (filter is not null)
        {
            _barWeb.Call($"window.setFields && window.setFields({WebBarHost.Json(_fieldsInputText)})");
            _barWeb.Call($"window.setWhere && window.setWhere({WebBarHost.Json(_whereInputText)})");
            _barWeb.Call($"window.setOrder && window.setOrder({WebBarHost.Json(_orderInputText)})");
            _barWeb.Call($"window.setTop && window.setTop({WebBarHost.Json(_topValue.ToString())})");
        }
        await LoadAsync();
    }

    private (string schema, string table) ParseTableRef(string raw)
    {
        raw = raw.Trim().Trim('[', ']');
        var parts = raw.Replace("[", "").Replace("]", "").Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    // ---- Chống đua + chống ghi nhầm workspace -------------------------------------------------
    // Lần tải mới nhất mới được quyền gán trạng thái "bảng đang hiển thị". Trước đây LoadAsync ghi
    // _schema/_table (field dùng chung) NGAY từ đầu, rồi mới await: bấm Load liên tiếp thì lần cũ về
    // sau đè kết quả lần mới, và các lệnh sau await đọc nhầm _schema/_table của lần khác.
    private int _loadVersion;
    private bool _loading;
    private bool _autoSavePending;
    /// <summary>"Tên WS|Server|Database" của nơi dữ liệu trong lưới được tải về — mọi thao tác ghi
    /// phải đối chiếu lại với workspace hiện tại, tránh ghi dữ liệu của WS này vào DB của WS khác
    /// khi người dùng đổi workspace giữa chừng (AutoSave chạy mỗi khi rời một dòng).</summary>
    private string _loadedStamp = "";
    /// <summary>Sys hay App Data của lần tải — ghi về ĐÚNG DB đó, không theo combo "DB:" hiện tại
    /// (người dùng có thể đã đổi combo sau khi tải).</summary>
    private bool _loadedUseSys;

    private static string DescribeStamp(string stamp)
    {
        var p = stamp.Split('|');
        return p.Length == 3 ? $"{p[0]} / {p[2]}" : stamp;
    }

    /// <summary>true nếu workspace + database hiện tại vẫn là nơi dữ liệu trong lưới được tải về.</summary>
    private bool WorkspaceStillMatches(out string message)
    {
        var now = _service.CurrentStamp(_loadedUseSys);
        if (now == _loadedStamp) { message = ""; return true; }
        message = $"⚠ Workspace/Database đã đổi từ lúc tải (đã tải từ: {DescribeStamp(_loadedStamp)}; hiện tại: {DescribeStamp(now)}) — " +
                  "KHÔNG ghi để tránh ghi nhầm DB. Bấm Load để tải lại rồi sửa tiếp.";
        return false;
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_tableInputText)) return;
        await AutoSwitchDatabaseForTableAsync(_tableInputText);
        var version = ++_loadVersion;
        var (schema, table) = ParseTableRef(_tableInputText);
        var useSys = _dbIndex == 1;
        var stamp = _service.CurrentStamp(useSys);
        var topN = _topValue;

        var fieldsToSelect = string.IsNullOrWhiteSpace(_fieldsInputText) ? "*" : _fieldsInputText.Trim();

        _loading = true;
        _statusLabel.Text = "Đang tải...";
        try
        {
            // "Run Table Async": bật (mặc định) = tải ở nền; tắt = chạy đồng bộ (giao diện chờ tới khi tải xong).
            var data = AppSettings.TableRunAsync
                ? await _service.LoadTableAsync(useSys, schema, table, topN, fieldsToSelect, _whereInputText, _orderInputText)
                : Task.Run(() => _service.LoadTableAsync(useSys, schema, table, topN, fieldsToSelect, _whereInputText, _orderInputText)).GetAwaiter().GetResult();
            if (version != _loadVersion) return; // đã có lần tải mới hơn — bỏ kết quả này
            if (_service.CurrentStamp(useSys) != stamp)
            {
                _statusLabel.Text = "Workspace đã đổi trong lúc tải — bỏ kết quả, bấm Load lại.";
                return;
            }
            var isPeriodPlaceholder = _service.IsPeriodPlaceholder(schema, table);

            List<string> keyColumns;
            string keyText;
            if (isPeriodPlaceholder)
            {
                keyColumns = new List<string>();
                keyText = "ℹ [Schema].[Table]$000000 = gộp các bảng phân kỳ (UNION ALL; bảng không phân kỳ thì chỉ chính nó) — không Save được ở đây.";
            }
            else
            {
                keyColumns = await _service.GetPrimaryKeyColumnsAsync(useSys, schema, table);
                if (version != _loadVersion) return;
                keyText = keyColumns.Count > 0
                    ? $"Primary Key: {string.Join(", ", keyColumns)}"
                    : "⚠ Bảng không có Primary Key.";
            }

            // Từ đây là lần tải mới nhất: gán trạng thái của "bảng đang hiển thị".
            _schema = schema;
            _table = table;
            _keyColumns = keyColumns;
            _keyLabel.Text = keyText;
            _loadedUseSys = useSys;
            _loadedStamp = stamp;

            // Bảng chia kỳ: có bảng log đi kèm (xxx$log) thì Gen Add/Alter/Drop Column làm luôn cho bảng log.
            _logTable = null;
            if (PartitionPrefix(table) is { } prefix)
            {
                try
                {
                    var logCols = await _service.GetColumnTypesAsync(useSys, schema, prefix + "$log");
                    if (version != _loadVersion) return;
                    if (logCols.Count > 0) _logTable = prefix + "$log";
                }
                catch { /* không tra được bảng log — chỉ gen cho bảng chính + các kỳ */ }
            }

            await PopulateStructureAndFieldsListAsync(useSys, data);
            if (version != _loadVersion) return;
            GridDisplayHelper.BindOptimized(_grid, data);
            _grid.ReadOnly = isPeriodPlaceholder;
            var filterInfo = (string.IsNullOrWhiteSpace(_whereInputText) ? "" : $" — Where: {_whereInputText.Trim()}")
                           + (string.IsNullOrWhiteSpace(_orderInputText) ? "" : $" — Order: {_orderInputText.Trim()}");
            TableLoaded?.Invoke(_schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? _table : $"{_schema}.{_table}");
            _statusLabel.Text = ""; // dòng "N dòng đã tải ..." đã ẩn theo yêu cầu (filterInfo/fieldsToSelect không còn hiển thị)
        }
        catch (Exception ex)
        {
            if (version != _loadVersion) return; // lỗi của lần tải đã bị thay thế — không báo
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            if (version == _loadVersion) _loading = false;
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
        // Đang tải bảng mới: lưới sắp bị thay hẳn, không ghi gì lúc này.
        if (_loading) return;
        // Đang lưu dòng trước: đánh dấu "còn việc" để vòng lặp bên dưới lưu tiếp, thay vì BỎ QUA im
        // lặp như trước (thay đổi của dòng thứ hai khi đó không được lưu mà không ai biết).
        if (_autoSaving) { _autoSavePending = true; return; }
        if (_grid.DataSource is not DataTable data) return;
        if (_service.IsPeriodPlaceholder(_schema, _table)) return;
        if (_keyColumns.Count == 0) return;
        if (data.GetChanges() is null) return;

        // Đổi workspace/database sau khi tải thì KHÔNG ghi: thay đổi còn nguyên trong lưới, chỉ báo
        // trên thanh trạng thái (không MessageBox — sự kiện này kích mỗi lần rời dòng, sẽ nháy liên tục).
        if (!WorkspaceStillMatches(out var mismatch)) { _statusLabel.Text = mismatch; return; }

        _autoSaving = true;
        try
        {
            do
            {
                _autoSavePending = false;
                if (data.GetChanges() is null) break;
                if (!WorkspaceStillMatches(out mismatch)) { _statusLabel.Text = mismatch; break; }

                UpdateRowLabel("💾 Đang lưu thay đổi của dòng " + ((_grid.CurrentCell?.RowIndex ?? 0) + 1) + "…");
                var count = await _service.SaveChangesAsync(_loadedUseSys, _schema, _table, _keyColumns, data, _loadedStamp);
                if (count > 0)
                    _statusLabel.Text = $"Đã tự động lưu {count} thay đổi lúc {DateTime.Now:HH:mm:ss} ({DescribeStamp(_loadedStamp)}).";
                UpdateRowLabel();
            }
            while (_autoSavePending);
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
            _autoSavePending = false;
        }
    }

    private async Task SaveAsync()
    {
        if (_grid.DataSource is not DataTable data) return;
        if (_loading) { MessageBox.Show(this, "Đang tải dữ liệu — chờ tải xong rồi Save.", "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

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

        // Ghi về ĐÚNG nơi đã tải (workspace + DB), không theo workspace/combo hiện tại: nếu đã đổi thì từ chối.
        if (!WorkspaceStillMatches(out var mismatch))
        {
            _statusLabel.Text = mismatch;
            MessageBox.Show(this, mismatch, "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"Ghi thay đổi trực tiếp vào [{_schema}].[{_table}]?\n\nNơi ghi: {DescribeStamp(_loadedStamp)}",
            "Bcode — Table", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        try
        {
            var count = await _service.SaveChangesAsync(_loadedUseSys, _schema, _table, _keyColumns, data, _loadedStamp);
            _statusLabel.Text = $"Đã lưu {count} thay đổi ({DescribeStamp(_loadedStamp)}).";
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

    /// <summary>Add Script trên toolbar khi tab này đang mở (giống FCode): sinh script DELETE + nạp lại
    /// từ toàn bộ dữ liệu đang xem cho chính bảng đang Load — không hỏi tên bảng đích — để MainForm hiện
    /// trong cửa sổ Script. Null nếu chưa Load dữ liệu (đã báo cho người dùng).</summary>
    /// <summary>DELETE của script chỉ khớp phần dữ liệu đang xem khi dữ liệu không bị cắt. Số dòng đạt đúng giới hạn Top → rất có thể còn dòng chưa tải:
    /// script sẽ xoá nhiều hơn những gì nó nạp lại — hỏi lại trước khi sinh.</summary>
    private bool ConfirmAddScriptScope(DataTable data)
    {
        if (_topValue <= 0 || data.Rows.Count < _topValue) return true;
        return MessageBox.Show(this,
            $"Dữ liệu đang xem đúng bằng giới hạn Top ({_topValue} dòng) — có thể còn dòng chưa được tải.\n" +
            "Script sẽ XOÁ " + (string.IsNullOrWhiteSpace(_whereInputText) ? "TOÀN BỘ bảng" : $"mọi dòng thoả: {_whereInputText.Trim()}") +
            " nhưng chỉ nạp lại các dòng đang hiện, nên các dòng còn lại sẽ MẤT.\n\nĐặt Top = 0 (tất cả) rồi Load lại sẽ an toàn. Vẫn sinh script?",
            "Bcode — Add Script", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
    }

    public async Task<(string Script, string TableName)?> BuildAddScriptAsync()
    {
        if (_grid.DataSource is not DataTable data || data.Rows.Count == 0)
        {
            MessageBox.Show(this, "Chưa có dữ liệu để sinh Script — Load bảng trước.", "Bcode — Add Script");
            return null;
        }

        if (!ConfirmAddScriptScope(data)) return null;
        var target = _schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? _table : $"{_schema}.{_table}";
        _statusLabel.Text = $"Đang sinh script cho {data.Rows.Count} dòng...";
        var loadedWhere = _whereInputText;
        var script = await Task.Run(() => Environment.NewLine + _dataScript.GenerateDeleteAndReloadScript(data, target, loadedWhere));
        _statusLabel.Text = $"Đã sinh script ({data.Rows.Count} dòng).";
        return (script, target);
    }

    private async Task GenDataScriptAsync()
    {
        if (_grid.DataSource is not DataTable data || data.Rows.Count == 0)
        {
            MessageBox.Show(this, "Chưa có dữ liệu để sinh Script — Load bảng trước.", "Bcode — Add Script");
            return;
        }

        if (!ConfirmAddScriptScope(data)) return;
        var defaultTarget = _schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? _table : $"{_schema}.{_table}";
        var targetName = SimplePromptForm.Show(this, "Add Script",
            string.IsNullOrWhiteSpace(_whereInputText)
                ? "Tên bảng đích (DELETE toàn bộ rồi nạp lại từ dữ liệu đang xem):"
                : $"Tên bảng đích (DELETE WHERE {_whereInputText.Trim()} rồi nạp lại từ dữ liệu đang xem):",
            defaultTarget);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        _statusLabel.Text = $"Đang sinh script cho {data.Rows.Count} dòng...";
        try
        {
            var loadedWhere = _whereInputText;
            var script = await Task.Run(() => _dataScript.GenerateDeleteAndReloadScript(data, targetName, loadedWhere));

            Clipboard.SetText(script);
            _scriptFileService.AddTextToCart(script);

            _statusLabel.Text = $"Đã sinh script ({data.Rows.Count} dòng) và copy vào clipboard.";
            MessageBox.Show(this, "Đã sinh script và copy vào clipboard.", "Bcode — Add Script", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Add Script", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}