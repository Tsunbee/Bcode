using System.Text.Json;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>Giao diện WinForms của "Tạo Cấu Trúc API & Đặc Tả Word" (chế độ App, thay apischema.html): danh sách API bên trái; bên phải tên API, ô thêm bảng có gợi ý (App + Sys), các bảng đã chọn, thao tác trên cột và lưới cột.</summary>
public partial class ApiSchemaBuilderForm
{
    private sealed class ApiCol
    {
        public bool Selected = true, IsPk, Required, Nullable = true, Identity;
        public string ColName = "", DataType = "", FieldDesc = "", LogicDesc = "", TKey = "", TName = "", TDb = "";
        public string? PrevField;
    }

    private sealed class ApiModel
    {
        public string Name = "", Desc = "", View = "";
        public List<(string Db, string Table)> Tables = new();
        public List<ApiCol> Columns = new();
    }

    private static readonly string[] SystemCols = { "datetime0", "datetime2", "user_id0", "user_id2", "date0", "time0", "date2", "time2", "status", "sysorder" };

    private readonly List<ApiModel> _apis = new();
    private int _active;
    private readonly List<string>[] _allTables = { new(), new() };
    private bool _fillN;
    private ListBox _apiList = null!;
    private RoundedInput _nName = null!, _nTable = null!, _nDesc = null!;
    private FlowLayoutPanel _chips = null!;
    private DataGridView _grid = null!;
    private CheckBox _chkAll = null!;
    private Label _info = null!;
    private ListBox _sug = null!;
    private List<(string Db, string Table)> _sugItems = new();
    private List<int> _shownCols = new();

    private sealed class NoFocusListBox : ListBox { public NoFocusListBox() { SetStyle(ControlStyles.Selectable, false); } }

    private ApiModel? Cur => _active >= 0 && _active < _apis.Count ? _apis[_active] : null;

    private void BuildNative()
    {
        KeyPreview = true;
        // ---- trái ----
        _apiList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
        _apiList.SelectedIndexChanged += (_, _) => { if (_fillN || _apiList.SelectedIndex < 0) return; SyncCur(); _active = _apiList.SelectedIndex; RenderDetail(); };
        var left = new Panel { Dock = DockStyle.Left, Width = 230, Padding = new Padding(8) };
        var lhead = NK.Muted("Danh sách API"); lhead.Dock = DockStyle.Top;
        var lbtn = NK.Row(NK.Button("+ Thêm", AddApi, primary: true), NK.Button("Xóa", DelApi)); lbtn.Dock = DockStyle.Bottom;
        left.Controls.Add(_apiList); left.Controls.Add(lbtn); left.Controls.Add(lhead);

        // ---- phải: đầu ----
        _nName = NK.Input(""); _nTable = NK.Input("Gõ để lọc bảng / view (vd dmkh, ctgs, SVTran) — chọn để THÊM vào API, có thể thêm nhiều bảng...");
        _nDesc = NK.Input("Mô tả nghiệp vụ / logic...");
        _chips = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        var head = NK.FormGrid(120, ("Tên API:", _nName), ("Thêm bảng:", _nTable), ("Bảng đã chọn:", _chips), ("Mô tả xử lý:", _nDesc));
        _nName.TextChanged += (_, _) => { var a = Cur; if (a is null || _fillN) return; a.Name = _nName.Text; _fillN = true; if (_apiList.SelectedIndex >= 0) _apiList.Items[_apiList.SelectedIndex] = a.Name.Length == 0 ? "(Chưa đặt tên)" : a.Name; _fillN = false; };

        // ---- gợi ý bảng ----
        _sug = new NoFocusListBox { Visible = false, IntegralHeight = false, Height = 240, BorderStyle = BorderStyle.FixedSingle };
        _sug.MouseDown += (_, e) => { var i = _sug.IndexFromPoint(e.Location); if (i >= 0) AddTable(_sugItems[i]); };
        _nTable.TextChanged += (_, _) => ShowSuggest();
        _nTable.Inner.GotFocus += (_, _) => ShowSuggest();
        _nTable.Inner.LostFocus += (_, _) => _sug.Visible = false;
        _nTable.InputKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Down) { e.Handled = e.SuppressKeyPress = true; if (!_sug.Visible) ShowSuggest(); else _sug.SelectedIndex = Math.Min(_sug.Items.Count - 1, _sug.SelectedIndex + 1); }
            else if (e.KeyCode == Keys.Up) { e.Handled = e.SuppressKeyPress = true; _sug.SelectedIndex = Math.Max(0, _sug.SelectedIndex - 1); }
            else if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; if (_sug.SelectedIndex >= 0 && _sug.SelectedIndex < _sugItems.Count) AddTable(_sugItems[_sug.SelectedIndex]); }
            else if (e.KeyCode == Keys.Escape && _sug.Visible) { e.Handled = e.SuppressKeyPress = true; _sug.Visible = false; }
        };

        // ---- thanh thao tác ----
        _info = NK.Muted("");
        var tools = NK.Row(
            NK.Button("Bỏ cột hệ thống", FilterSys), NK.Button("Đổi sang camelCase", Camel), NK.Button("↩ Về tên cũ", UndoName, tip: "Trả tên trường API về tên trước khi bấm camelCase"),
            NK.Button("☑ Tick bắt buộc (NOT NULL)", ReqDb, tip: "Tick cột Bắt buộc cho các cột NOT NULL của bảng (không tính cột identity)"), NK.Button("Bỏ tick bắt buộc", ReqClear), _info);
        _chkAll = NK.Check("Chọn tất cả cột"); _chkAll.Checked = true;
        _chkAll.Click += (_, _) => { SyncCur(); var a = Cur; if (a is null) return; foreach (var c in Vis(a)) c.Selected = _chkAll.Checked; RenderDetail(); };

        _grid = NK.Grid(("src", "Bảng nguồn", 130), ("col", "Cột DB", 150), ("type", "Kiểu dữ liệu", 110), ("field", "Tên trường API", 190), ("logic", "Quy tắc / Logic", 0));
        _grid.ReadOnly = false;
        _grid.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "✓", Width = 34 });
        _grid.Columns.Insert(4, new DataGridViewCheckBoxColumn { Name = "pk", HeaderText = "🔑 PK", Width = 56 });
        _grid.Columns.Insert(5, new DataGridViewCheckBoxColumn { Name = "req", HeaderText = "Bắt buộc", Width = 70 });
        foreach (var n in new[] { "src", "col", "type" }) _grid.Columns[n]!.ReadOnly = true;
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, e) => { if (_fillN || e.RowIndex < 0) return; SyncCur(); if (_grid.Columns[e.ColumnIndex].Name is "sel" or "field") RenderDetail(true); else UpdateInfo(); };

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
        head.Dock = DockStyle.Top; tools.Dock = DockStyle.Top; _chkAll.Dock = DockStyle.Top;
        top.Controls.Add(_chkAll); top.Controls.Add(tools); top.Controls.Add(head);
        _grid.Dock = DockStyle.Fill;
        right.Controls.Add(_grid); right.Controls.Add(top); right.Controls.Add(_sug);

        var bar = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
        bar.Controls.Add(NK.Button("Đóng", Close)); bar.Controls.Add(NK.Button("📄 Xuất tất cả API ra Word (.doc)", ExportNative, primary: true));

        Controls.Add(right); Controls.Add(left); Controls.Add(bar);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && !_sug.Visible) { e.Handled = true; Close(); } };
        Shown += (_, _) => { _ = LoadTablesNativeAsync(false); _ = LoadTablesNativeAsync(true); };
        _apis.Add(new ApiModel { Name = "API 1" });
        RenderList(); RenderDetail();
    }

    // ---- mô hình / hiển thị ----
    private static IEnumerable<ApiCol> Vis(ApiModel a) => a.Columns.Where(c => a.View.Length == 0 || c.TKey == a.View);
    private static string TKey(string db, string t) => db + "|" + t;

    private void SyncCur()
    {
        var a = Cur; if (a is null) return;
        a.Name = _nName.Text; a.Desc = _nDesc.Text;
        for (var r = 0; r < _grid.Rows.Count && r < _shownCols.Count; r++)
        {
            var c = a.Columns[_shownCols[r]]; var row = _grid.Rows[r];
            c.Selected = row.Cells["sel"].Value is true; c.IsPk = row.Cells["pk"].Value is true; c.Required = row.Cells["req"].Value is true;
            c.FieldDesc = (row.Cells["field"].Value as string) ?? ""; c.LogicDesc = (row.Cells["logic"].Value as string) ?? "";
        }
    }

    private void RenderList()
    {
        _fillN = true; _apiList.Items.Clear();
        foreach (var a in _apis) _apiList.Items.Add(a.Name.Length == 0 ? "(Chưa đặt tên)" : a.Name);
        if (_active >= 0 && _active < _apis.Count) _apiList.SelectedIndex = _active;
        _fillN = false;
    }

    private void RenderDetail(bool keepScroll = false)
    {
        var a = Cur; if (a is null) return;
        _fillN = true;
        _nName.Text = a.Name; _nDesc.Text = a.Desc;
        RenderChips(a);
        var first = keepScroll && _grid.FirstDisplayedScrollingRowIndex >= 0 ? _grid.FirstDisplayedScrollingRowIndex : -1;
        _grid.SuspendLayout(); _grid.Rows.Clear(); _shownCols.Clear();
        var count = a.Columns.Where(c => c.Selected).GroupBy(c => c.FieldDesc.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
        for (var i = 0; i < a.Columns.Count; i++)
        {
            var c = a.Columns[i];
            if (a.View.Length > 0 && c.TKey != a.View) continue;
            _shownCols.Add(i);
            var r = _grid.Rows.Add(c.Selected, c.TName, c.ColName, c.DataType, c.IsPk, c.Required, c.FieldDesc, c.LogicDesc);
            if (c.Selected && count.TryGetValue(c.FieldDesc.ToLowerInvariant(), out var n) && n > 1)
            {
                _grid.Rows[r].Cells["field"].Style.ForeColor = AppColors.Danger;
                _grid.Rows[r].Cells["field"].ToolTipText = "Trùng tên trường API với cột khác — đổi tên để không đè nhau";
            }
        }
        if (first >= 0 && first < _grid.Rows.Count) _grid.FirstDisplayedScrollingRowIndex = first;
        _grid.ResumeLayout();
        _chkAll.Checked = Vis(a).Any() && Vis(a).All(c => c.Selected);
        _fillN = false;
        UpdateInfo();
    }

    private void UpdateInfo(string? msg = null)
    {
        var a = Cur; if (a is null) return;
        if (msg is not null) { _info.Text = msg; return; }
        var sel = a.Columns.Count(c => c.Selected);
        _info.Text = a.Columns.Count == 0 ? "" : (a.View.Length > 0 ? $"Đang hiện {Vis(a).Count()} cột của bảng đã chọn · " : "") + $"{sel}/{a.Columns.Count} cột · {a.Columns.Count(c => c.Selected && c.IsPk)} khóa chính · {a.Columns.Count(c => c.Selected && c.Required)} bắt buộc" + (a.Tables.Count > 1 ? $" · {a.Tables.Count} bảng" : "");
    }

    private void RenderChips(ApiModel a)
    {
        _chips.Controls.Clear();
        if (a.Tables.Count == 0) { _chips.Controls.Add(NK.Muted("Chưa chọn bảng nào.")); return; }
        if (a.View.Length > 0 && !a.Tables.Any(t => TKey(t.Db, t.Table) == a.View)) a.View = "";
        void Pick(string key) { SyncCur(); a.View = key; RenderDetail(); }
        if (a.Tables.Count > 1) { var all = NK.Button($"Tất cả · {a.Columns.Count} cột", () => Pick(""), primary: a.View.Length == 0, tip: "Hiện cột của mọi bảng"); _chips.Controls.Add(all); }
        foreach (var t in a.Tables.ToList())
        {
            var key = TKey(t.Db, t.Table); var n = a.Columns.Count(c => c.TKey == key);
            var chip = NK.Button($"{t.Table} · {(t.Db == "1" ? "Sys" : "App")} · {n} cột", () => Pick(key), primary: a.View == key || a.Tables.Count == 1, tip: "Bấm để chỉ hiện cột của bảng này");
            var x = NK.Button("✕", () => { SyncCur(); a.Tables.Remove(t); a.Columns.RemoveAll(c => c.TKey == key); if (a.View == key) a.View = ""; RenderDetail(); }, tip: "Bỏ bảng này (và các cột lấy từ nó)");
            x.AutoWidth = false; x.Width = 30;
            _chips.Controls.Add(chip); _chips.Controls.Add(x);
        }
    }

    private void AddApi() { SyncCur(); _apis.Add(new ApiModel { Name = "API " + (_apis.Count + 1) }); _active = _apis.Count - 1; RenderList(); RenderDetail(); }
    private void DelApi() { if (_apis.Count <= 1) return; _apis.RemoveAt(_active); _active = Math.Max(0, _active - 1); RenderList(); RenderDetail(); }

    // ---- gợi ý bảng ----
    private void ShowSuggest()
    {
        var q = _nTable.Text.Trim().ToLowerInvariant();
        var all = new List<(string Db, string Table)>();
        foreach (var db in new[] { "0", "1" }) foreach (var t in _allTables[int.Parse(db)]) all.Add((db, t));
        IEnumerable<(string Db, string Table)> res;
        if (q.Length == 0) res = all.Take(60);
        else
        {
            var starts = new List<(string, string)>(); var has = new List<(string, string)>();
            foreach (var x in all)
            {
                var full = x.Table.ToLowerInvariant(); var dot = full.IndexOf('.'); var shortN = dot >= 0 ? full[(dot + 1)..] : full;
                if (shortN.StartsWith(q) || full.StartsWith(q)) starts.Add(x); else if (full.Contains(q)) has.Add(x);
            }
            res = starts.Concat(has).Take(80);
        }
        _sugItems = res.ToList();
        _sug.Items.Clear();
        foreach (var x in _sugItems) _sug.Items.Add(x.Table + "    " + (x.Db == "1" ? "Sys Data" : "App Data"));
        if (_sugItems.Count == 0) _sug.Items.Add(all.Count == 0 ? "(đang tải danh sách bảng…)" : "Không có bảng nào khớp");
        else _sug.SelectedIndex = 0;
        var host = _nTable.Parent; // ô nằm trong FormGrid → định vị theo toạ độ của form phải
        var pt = _sug.Parent!.PointToClient(_nTable.PointToScreen(new Point(0, _nTable.Height)));
        _sug.SetBounds(pt.X, pt.Y, Math.Max(300, _nTable.Width), 240);
        _sug.Visible = true; _sug.BringToFront();
    }

    private void AddTable((string Db, string Table) x)
    {
        var a = Cur; if (a is null) return;
        SyncCur();
        if (a.Tables.Any(t => t.Db == x.Db && t.Table.Equals(x.Table, StringComparison.OrdinalIgnoreCase))) { UpdateInfo($"Bảng {x.Table} đã có trong API này."); return; }
        a.Tables.Add(x); a.View = TKey(x.Db, x.Table);
        RenderDetail();
        _ = LoadColumnsNativeAsync(a, x.Db == "1", x.Table);
        _nTable.Text = ""; _sug.Visible = false;
    }

    private async Task LoadTablesNativeAsync(bool isSys)
    {
        try
        {
            var objects = await _sqlObjectService.ListObjectsAsync(isSys);
            _allTables[isSys ? 1 : 0] = objects.Where(o => o.Kind == SqlObjectKind.Table || o.Kind == SqlObjectKind.View).Select(o => o.QualifiedName).ToList();
            if (!IsDisposed && _nTable.Inner.Focused) ShowSuggest();
        }
        catch { /* chưa kết nối — không có gợi ý */ }
    }

    private async Task LoadColumnsNativeAsync(ApiModel a, bool isSys, string rawTable)
    {
        var parts = rawTable.Replace("[", "").Replace("]", "").Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : parts[0];
        var db = isSys ? "1" : "0"; var key = TKey(db, rawTable);
        try
        {
            var list = new List<ApiCol>();
            try
            {
                var desc = await _tableDataService.GetColumnDescriptionsAsync(isSys, schema, table);
                foreach (var c in desc) list.Add(new ApiCol { ColName = c.Name, DataType = c.Type, IsPk = c.IsKey, Nullable = c.Nullable, Identity = c.Identity, FieldDesc = c.Name.ToLower(), LogicDesc = c.Description ?? "" });
            }
            catch
            {
                var types = await _tableDataService.GetColumnTypesAsync(isSys, schema, table);
                var pks = new HashSet<string>(await _tableDataService.GetPrimaryKeyColumnsAsync(isSys, schema, table), StringComparer.OrdinalIgnoreCase);
                foreach (var kv in types) list.Add(new ApiCol { ColName = kv.Key, DataType = kv.Value, IsPk = pks.Contains(kv.Key), FieldDesc = kv.Key.ToLower() });
            }
            if (IsDisposed || !a.Tables.Any(t => t.Db == db && t.Table == rawTable)) return;   // bảng đã bị bỏ trong lúc chờ
            a.Columns.RemoveAll(c => c.TKey == key);
            var used = new HashSet<string>(a.Columns.Select(c => c.FieldDesc.ToLowerInvariant()));
            var dot = rawTable.IndexOf('.'); var shortName = (dot >= 0 ? rawTable[(dot + 1)..] : rawTable).ToLowerInvariant();
            foreach (var c in list)
            {
                c.TKey = key; c.TName = rawTable; c.TDb = db;
                if (used.Contains(c.FieldDesc.ToLowerInvariant())) c.FieldDesc = shortName + "_" + c.FieldDesc;
                used.Add(c.FieldDesc.ToLowerInvariant());
                a.Columns.Add(c);
            }
            if (a == Cur) { BeginInvoke(() => RenderDetail()); }
        }
        catch { /* không đọc được cột */ }
    }

    // ---- thao tác trên cột ----
    private void FilterSys() { SyncCur(); var a = Cur; if (a is null) return; foreach (var c in Vis(a)) if (SystemCols.Contains(c.ColName.ToLowerInvariant())) c.Selected = false; RenderDetail(); }

    private static string ToCamel(string v)
    {
        if (!v.Contains('_')) return v;
        var p = v.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return p[0].ToLowerInvariant() + string.Concat(p.Skip(1).Select(s => char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant()));
    }

    private void Camel()
    {
        SyncCur(); var a = Cur; if (a is null) return;
        foreach (var c in Vis(a)) { var nv = ToCamel(c.FieldDesc); if (nv != c.FieldDesc) { c.PrevField ??= c.FieldDesc; c.FieldDesc = nv; } }
        RenderDetail();
    }

    private void UndoName()
    {
        SyncCur(); var a = Cur; if (a is null) return;
        var n = 0; foreach (var c in Vis(a)) if (c.PrevField is not null) { c.FieldDesc = c.PrevField; c.PrevField = null; n++; }
        RenderDetail(); UpdateInfo(n > 0 ? $"Đã trả {n} tên trường về tên cũ." : "Chưa có tên nào đổi sang camelCase để trả lại.");
    }

    private void ReqDb()
    {
        SyncCur(); var a = Cur; if (a is null) return;
        var n = 0; foreach (var c in Vis(a)) if (c.Selected && !c.Nullable && !c.Identity) { c.Required = true; n++; }
        RenderDetail(); UpdateInfo($"Đã tick bắt buộc {n} cột NOT NULL (cột identity và cột nullable không tick). Sửa tay được ở cột Bắt buộc.");
    }

    private void ReqClear() { SyncCur(); var a = Cur; if (a is null) return; foreach (var c in Vis(a)) c.Required = false; RenderDetail(); }

    private void ExportNative()
    {
        _grid.EndEdit(); SyncCur();
        var model = _apis.Select(a => new
        {
            name = a.Name, desc = a.Desc,
            tables = a.Tables.Select(t => new { db = t.Db, table = t.Table }),
            columns = a.Columns.Select(c => new { selected = c.Selected, colName = c.ColName, dataType = c.DataType, isPk = c.IsPk, required = c.Required, fieldDesc = c.FieldDesc, logicDesc = c.LogicDesc, tname = c.TName }),
        }).ToList();
        ExportWord(JsonSerializer.SerializeToElement(model));
    }
}
