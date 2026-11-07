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
        public bool Selected = true, IsPk, Required, Nullable = true, Identity, ViUser, Manual;
        public string ColName = "", DataType = "", FieldDesc = "", LogicDesc = "", ViName = "", TKey = "", TName = "", TDb = "";
        public string? PrevField;
    }

    private sealed class ApiModel
    {
        public string Name = "", Desc = "", View = "", Func = "";
        public bool FuncAuto = true;
        public List<(string Db, string Table)> Tables = new();
        public List<ApiCol> Columns = new();
        /// <summary>TKey bảng → controller Dir tìm được ("" = không có); bảng chưa có khoá = đang tìm.</summary>
        public Dictionary<string, string> Controllers = new();
        /// <summary>TKey bảng → menu ("wmenu_id tên") có sysid = controller; "" = controller lấy từ Dir.</summary>
        public Dictionary<string, string> Menus = new();
    }

    private RoundedInput _nFunc = null!;
    private Label _funcInfo = null!;
    private CheckBox _chkHide = null!;

    private static string FuncFor(ApiModel a) =>
        a.Tables.Count == 0 ? "" : Bcode.App.Services.Api.ApiSchemaStore.FuncName(
            a.Controllers.TryGetValue(TKey(a.Tables[0].Db, a.Tables[0].Table), out var c) && c.Length > 0 ? c : a.Tables[0].Table);

    private static void AutoFunc(ApiModel a) { if (a.FuncAuto) a.Func = FuncFor(a); }

    private void RenderFuncInfo(ApiModel a)
    {
        if (a.Tables.Count == 0) { _funcInfo.Text = "Tự đặt khi thêm bảng: Set + controller của bảng."; return; }
        var t = a.Tables[0];
        var menu = a.Menus.GetValueOrDefault(TKey(t.Db, t.Table), "");
        var state = !a.Controllers.TryGetValue(TKey(t.Db, t.Table), out var c) ? $"đang tìm menu của {t.Table}…"
            : c.Length > 0 ? (menu.Length > 0 ? $"sysid {c} của menu {menu}" : $"controller Dir {c} (không thấy menu dùng bảng)")
            : $"không thấy menu (sysid) / controller Dir nào dùng {t.Table} — dùng tên bảng";
        _funcInfo.Text = (a.FuncAuto ? "Tự đặt" : "Đã sửa tay") + " · " + state;
    }

    // ---- lưu / nạp theo dự án (ApiSchemaStore — cùng file với bản Web) ----
    private void LoadNativeState()
    {
        if (Bcode.App.Services.Api.ApiSchemaStore.Load(_wsKey) is not { } st) { _apis.Add(new ApiModel { Name = "API 1" }); return; }
        foreach (var x in st.Apis)
        {
            var a = new ApiModel { Name = x.Name, Desc = x.Desc, View = x.View, Func = x.Func, FuncAuto = x.FuncAuto };
            foreach (var t in x.Tables) { a.Tables.Add((t.Db, t.Table)); a.Controllers[TKey(t.Db, t.Table)] = t.Controller; a.Menus[TKey(t.Db, t.Table)] = t.Menu ?? ""; }
            foreach (var c in x.Columns)
                a.Columns.Add(new ApiCol
                {
                    Selected = c.Selected, ColName = c.ColName, DataType = c.DataType, IsPk = c.IsPk, Nullable = c.Nullable, Identity = c.Identity, Required = c.Required,
                    FieldDesc = c.FieldDesc, LogicDesc = c.LogicDesc, ViName = c.ViName, ViUser = c.ViUser, TKey = c.Tkey, TName = c.Tname, TDb = c.Tdb, PrevField = c.PrevField, Manual = c.Manual,
                });
            _apis.Add(a);
        }
        if (_apis.Count == 0) _apis.Add(new ApiModel { Name = "API 1" });
        _active = Math.Clamp(st.Active, 0, _apis.Count - 1);
        _chkHide.Checked = st.HideSource;
    }

    private void SaveNativeState()
    {
        if (_fillN) return;
        try
        {
            var st = new Bcode.App.Services.Api.ApiSchemaState
            {
                HideSource = _chkHide.Checked, Active = _active,
                Apis = _apis.Select(a => new Bcode.App.Services.Api.ApiSchemaApi
                {
                    Name = a.Name, Desc = a.Desc, View = a.View, Func = a.Func, FuncAuto = a.FuncAuto,
                    Tables = a.Tables.Select(t => new Bcode.App.Services.Api.ApiSchemaTable { Db = t.Db, Table = t.Table, Controller = a.Controllers.GetValueOrDefault(TKey(t.Db, t.Table), ""), Menu = a.Menus.GetValueOrDefault(TKey(t.Db, t.Table), "") }).ToList(),
                    Columns = a.Columns.Select(c => new Bcode.App.Services.Api.ApiSchemaColumn
                    {
                        Selected = c.Selected, ColName = c.ColName, DataType = c.DataType, IsPk = c.IsPk, Nullable = c.Nullable, Identity = c.Identity, Required = c.Required,
                        FieldDesc = c.FieldDesc, LogicDesc = c.LogicDesc, ViName = c.ViName, ViUser = c.ViUser, Tkey = c.TKey, Tname = c.TName, Tdb = c.TDb, PrevField = c.PrevField, Manual = c.Manual,
                    }).ToList(),
                }).ToList(),
            };
            Bcode.App.Services.Api.ApiSchemaStore.Save(_wsKey, st);
        }
        catch (Exception ex) { _info.Text = "Không lưu được danh sách API: " + ex.Message; }
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
        var left = new Panel { Dock = DockStyle.Left, Width = NativeSkin.S(230), Padding = new Padding(8) };
        var lhead = NK.Muted("Danh sách API"); lhead.Dock = DockStyle.Top;
        var lbtn = NK.Row(NK.Button("+ Thêm", AddApi, primary: true), NK.Button("Xóa", DelApi)); lbtn.Dock = DockStyle.Bottom;
        left.Controls.Add(_apiList); left.Controls.Add(lbtn); left.Controls.Add(lhead);

        // ---- phải: đầu ----
        _nName = NK.Input(""); _nTable = NK.Input("Gõ để lọc bảng / view (vd dmkh, ctgs, SVTran) — chọn để THÊM vào API, có thể thêm nhiều bảng...");
        _nDesc = NK.Input("Mô tả nghiệp vụ / logic...");
        _nFunc = NK.Input("Set + controller của bảng"); _nFunc.Width = NativeSkin.S(260);
        _funcInfo = NK.Muted("");
        var funcRow = NK.Row(_nFunc, NK.Button("↻ Tự đặt", () =>
            {
                SyncCur(); var a = Cur; if (a is null) return; a.FuncAuto = true; AutoFunc(a); RenderDetail();
                // Tra lại menu của bảng đầu (sysid) — xong thì tự đặt lại tên hàm.
                if (a.Tables.Count > 0) { var t0 = a.Tables[0]; var k0 = TKey(t0.Db, t0.Table); _ = ApplyTableInfoAsync(a, k0, t0.Table, a.Columns.Where(c => c.TKey == k0).Select(c => c.ColName).ToList()); }
            },
            tip: "Đặt lại tên hàm = Set + sysid của menu dùng bảng đầu tiên (không có menu thì controller Dir)"), _funcInfo);
        _chips = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        var head = NK.FormGrid(120, ("Tên API:", _nName), ("Tên hàm API:", funcRow), ("Thêm bảng:", _nTable), ("Bảng đã chọn:", _chips), ("Mô tả xử lý:", _nDesc));
        _nFunc.Inner.LostFocus += (_, _) => { if (_fillN || Cur is null) return; SyncCur(); RenderFuncInfo(Cur); SaveNativeState(); };
        _nName.TextChanged += (_, _) => { var a = Cur; if (a is null || _fillN) return; a.Name = _nName.Text; _fillN = true; if (_apiList.SelectedIndex >= 0) _apiList.Items[_apiList.SelectedIndex] = a.Name.Length == 0 ? "(Chưa đặt tên)" : a.Name; _fillN = false; };

        // ---- gợi ý bảng ----
        _sug = new NoFocusListBox { Visible = false, IntegralHeight = false, Height = NativeSkin.S(240), BorderStyle = BorderStyle.FixedSingle };
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
            NK.Button("☑ Tick bắt buộc (NOT NULL)", ReqDb, tip: "Tick cột Bắt buộc cho các cột NOT NULL của bảng (không tính cột identity)"), NK.Button("Bỏ tick bắt buộc", ReqClear),
            NK.Button("＋ Thêm cột", AddManualCol, tip: "Thêm 1 cột KHÔNG có trong bảng database (cột tự khai): sửa được mọi ô; chọn dòng rồi Delete để xoá. Lưu cùng API, xuất Word như cột thật.\nLưới: Ctrl+A / Ctrl+Click / Shift+Click / kéo chuột chọn nhiều dòng — Space tick / bỏ tick cả nhóm."), _info);
        _chkAll = NK.Check("Chọn tất cả cột"); _chkAll.Checked = true;
        _chkAll.Click += (_, _) => { SyncCur(); var a = Cur; if (a is null) return; foreach (var c in Vis(a)) c.Selected = _chkAll.Checked; RenderDetail(); };

        _grid = NK.Grid(("src", "Bảng nguồn", 130), ("col", "Cột DB", 150), ("type", "Kiểu dữ liệu", 110), ("field", "Tên trường API", 170), ("vi", "Tên tiếng Việt", 180), ("logic", "Quy tắc / Logic", 0));
        _grid.ReadOnly = false;
        _grid.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "sel", HeaderText = "✓", Width = NativeSkin.S(34) });
        _grid.Columns.Insert(4, new DataGridViewCheckBoxColumn { Name = "pk", HeaderText = "🔑 PK", Width = NativeSkin.S(56) });
        _grid.Columns.Insert(5, new DataGridViewCheckBoxColumn { Name = "req", HeaderText = "Bắt buộc", Width = NativeSkin.S(70) });
        _grid.Columns["src"]!.ReadOnly = true;   // "col" / "type": khoá theo dòng (RenderDetail) — cột thêm tay sửa được
        _grid.MultiSelect = true;                 // Ctrl+A / Ctrl+Click / Shift+Click / kéo chuột chọn nhiều dòng (sẵn của DataGridView)
        _grid.KeyDown += (_, e) =>
        {
            if (_grid.IsCurrentCellInEditMode) return;
            if (e.KeyCode == Keys.Space && !e.Control && !e.Alt && _grid.SelectedRows.Count > 0) { e.Handled = e.SuppressKeyPress = true; ToggleSelectedRows(); }
            else if (e.KeyCode == Keys.Delete && _grid.SelectedRows.Count > 0) { e.Handled = e.SuppressKeyPress = true; DeleteManualRows(); }
        };
        // Bấm ô ✓ của 1 dòng thuộc nhóm đang chọn (nhiều dòng): đổi cả nhóm theo ô vừa bấm.
        _grid.CellMouseDown += (_, e) =>
        {
            _pendingGroup = e.RowIndex >= 0 && e.ColumnIndex == _grid.Columns["sel"]!.Index && e.Button == MouseButtons.Left && ModifierKeys == Keys.None
                && _grid.Rows[e.RowIndex].Selected && _grid.SelectedRows.Count > 1
                ? _grid.SelectedRows.Cast<DataGridViewRow>().Select(x => x.Index).ToList() : null;
        };
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_fillN || e.RowIndex < 0) return;
            var name = _grid.Columns[e.ColumnIndex].Name;
            if (name == "sel" && _pendingGroup is { } grp)
            {
                _pendingGroup = null;
                var on = _grid.Rows[e.RowIndex].Cells["sel"].Value is true;
                _fillN = true;
                foreach (var ri in grp) if (ri < _grid.Rows.Count) _grid.Rows[ri].Cells["sel"].Value = on;
                _fillN = false;
            }
            // cột thêm tay: gõ tên cột mà Tên trường API còn trống thì điền theo
            if (name == "col" && (_grid.Rows[e.RowIndex].Cells["field"].Value as string ?? "").Length == 0)
            { _fillN = true; _grid.Rows[e.RowIndex].Cells["field"].Value = ((_grid.Rows[e.RowIndex].Cells["col"].Value as string) ?? "").ToLowerInvariant(); _fillN = false; }
            SyncCur();
            if (name is "sel" or "field" or "col") RenderDetail(true); else UpdateInfo();
        };

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
        head.Dock = DockStyle.Top; tools.Dock = DockStyle.Top; _chkAll.Dock = DockStyle.Top;
        top.Controls.Add(_chkAll); top.Controls.Add(tools); top.Controls.Add(head);
        _grid.Dock = DockStyle.Fill;
        right.Controls.Add(_grid); right.Controls.Add(top); right.Controls.Add(_sug);

        var bar = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
        _chkHide = NK.Check("Bỏ database nguồn khi xuất Word");
        NK.Tip(_chkHide, "Bản gửi đối tác: Word không có Bảng DB / Bảng nguồn / Cột CSDL — mỗi trường định nghĩa bằng Tên tiếng Việt");
        _chkHide.CheckedChanged += (_, _) => SaveNativeState();
        bar.Controls.Add(NK.Button("Đóng", Close)); bar.Controls.Add(NK.Button("📄 Xuất tất cả API ra Word (.doc)", ExportNative, primary: true)); bar.Controls.Add(_chkHide);

        Controls.Add(right); Controls.Add(left); Controls.Add(bar);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && !_sug.Visible) { e.Handled = true; Close(); } };
        Shown += (_, _) => { _ = LoadTablesNativeAsync(false); _ = LoadTablesNativeAsync(true); };
        // Đóng form: lưu lần cuối (ô đang sửa dở) — mở lại vẫn còn các API đã tạo.
        FormClosing += (_, _) => { try { _grid.EndEdit(); SyncCur(); } catch { } SaveNativeState(); };
        _fillN = true; LoadNativeState(); _fillN = false;
        RenderList(); RenderDetail();
        if (_startInfo.Length > 0) _info.Text = _startInfo;
    }

    // ---- mô hình / hiển thị ----
    private static IEnumerable<ApiCol> Vis(ApiModel a) => a.Columns.Where(c => a.View.Length == 0 || c.TKey == a.View || c.Manual);
    private static string TKey(string db, string t) => db + "|" + t;

    private void SyncCur()
    {
        var a = Cur; if (a is null) return;
        a.Name = _nName.Text; a.Desc = _nDesc.Text;
        var fv = _nFunc.Text.Trim();
        if (fv != a.Func) { a.Func = fv; a.FuncAuto = false; }
        for (var r = 0; r < _grid.Rows.Count && r < _shownCols.Count; r++)
        {
            var c = a.Columns[_shownCols[r]]; var row = _grid.Rows[r];
            c.Selected = row.Cells["sel"].Value is true; c.IsPk = row.Cells["pk"].Value is true; c.Required = row.Cells["req"].Value is true;
            if (c.Manual) { c.ColName = (row.Cells["col"].Value as string) ?? ""; c.DataType = (row.Cells["type"].Value as string) ?? ""; }
            c.FieldDesc = (row.Cells["field"].Value as string) ?? ""; c.LogicDesc = (row.Cells["logic"].Value as string) ?? "";
            var vi = (row.Cells["vi"].Value as string) ?? "";
            if (vi != c.ViName) { c.ViName = vi; c.ViUser = true; }
        }
    }

    private void RenderList()
    {
        _fillN = true; _apiList.Items.Clear();
        foreach (var a in _apis) _apiList.Items.Add(a.Name.Length == 0 ? "(Chưa đặt tên)" : a.Name);
        if (_active >= 0 && _active < _apis.Count) _apiList.SelectedIndex = _active;
        _fillN = false;
        SaveNativeState();
    }

    private void RenderDetail(bool keepScroll = false)
    {
        var a = Cur; if (a is null) return;
        _fillN = true;
        _nName.Text = a.Name; _nDesc.Text = a.Desc; _nFunc.Text = a.Func;
        RenderFuncInfo(a);
        RenderChips(a);
        var first = keepScroll && _grid.FirstDisplayedScrollingRowIndex >= 0 ? _grid.FirstDisplayedScrollingRowIndex : -1;
        _grid.SuspendLayout(); _grid.Rows.Clear(); _shownCols.Clear();
        var count = a.Columns.Where(c => c.Selected).GroupBy(c => c.FieldDesc.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.Count());
        for (var i = 0; i < a.Columns.Count; i++)
        {
            var c = a.Columns[i];
            if (a.View.Length > 0 && c.TKey != a.View && !c.Manual) continue;
            _shownCols.Add(i);
            var r = _grid.Rows.Add(c.Selected, c.TName, c.ColName, c.DataType, c.IsPk, c.Required, c.FieldDesc, c.ViName, c.LogicDesc);
            _grid.Rows[r].Cells["col"].ReadOnly = _grid.Rows[r].Cells["type"].ReadOnly = !c.Manual;
            if (c.Manual)
            {
                _grid.Rows[r].Cells["src"].Style.Font = new Font(_grid.Font, FontStyle.Italic);
                _grid.Rows[r].Cells["src"].ToolTipText = "Cột thêm tay — không có trong bảng database. Delete để xoá (khi không đang sửa ô).";
            }
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
        SaveNativeState();
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
            var x = NK.Button("✕", () => { SyncCur(); a.Tables.Remove(t); a.Columns.RemoveAll(c => c.TKey == key); if (a.View == key) a.View = ""; AutoFunc(a); RenderDetail(); }, tip: "Bỏ bảng này (và các cột lấy từ nó)");
            x.AutoWidth = false; x.Width = NativeSkin.S(30);
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
        AutoFunc(a);
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

            // Menu dùng bảng → sysid → tên hàm "Set<sysid>" + tên tiếng Việt cho cột chưa sửa tay (source có thể chậm).
            await ApplyTableInfoAsync(a, key, rawTable, list.Select(c => c.ColName).ToList());
        }
        catch { /* không đọc được cột */ }
    }

    /// <summary>Controller của bảng (= sysid của menu dùng bảng, không có thì controller Dir) + tên tiếng Việt → model, tự đặt lại tên hàm nếu là bảng đầu.</summary>
    private async Task ApplyTableInfoAsync(ApiModel a, string key, string rawTable, List<string> cols)
    {
        try
        {
            var info = await Bcode.App.Services.Api.ApiSchemaStore.TableInfoAsync(_sourcePath, rawTable, cols, _tableMenus);
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                if (a == Cur) SyncCur();
                a.Controllers[key] = info.Controller;
                a.Menus[key] = info.Menu;
                foreach (var c in a.Columns) if (c.TKey == key && !c.ViUser && info.Vi.TryGetValue(c.ColName, out var v)) c.ViName = v;
                if (a.Tables.Count > 0 && TKey(a.Tables[0].Db, a.Tables[0].Table) == key) AutoFunc(a);
                if (a == Cur) RenderDetail(true); else SaveNativeState();
            });
        }
        catch { /* không tra được — giữ như cũ */ }
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

    /// <summary>Dòng của nhóm đang chọn khi bấm ô ✓ của 1 dòng trong nhóm (đổi cả nhóm theo ô đó); null = bấm bình thường.</summary>
    private List<int>? _pendingGroup;

    /// <summary>Space trên lưới: các dòng đang chọn — đang tick hết thì bỏ hết, còn lại thì tick hết.</summary>
    private void ToggleSelectedRows()
    {
        SyncCur(); var a = Cur; if (a is null) return;
        var list = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i < _shownCols.Count).Select(i => a.Columns[_shownCols[i]]).ToList();
        if (list.Count == 0) return;
        var allOn = list.All(c => c.Selected);
        foreach (var c in list) c.Selected = !allOn;
        var keep = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).ToList();
        RenderDetail(true);
        foreach (var i in keep) if (i < _grid.Rows.Count) _grid.Rows[i].Selected = true;
        UpdateInfo((allOn ? "Đã bỏ tick " : "Đã tick ") + list.Count + " cột đang chọn.");
    }

    /// <summary>"＋ Thêm cột": 1 cột không có trong database — Bảng nguồn "(thêm tay)", sửa được Cột DB / Kiểu và mọi ô khác.</summary>
    private void AddManualCol()
    {
        SyncCur(); var a = Cur; if (a is null) return;
        a.Columns.Add(new ApiCol { Manual = true, TName = "(thêm tay)", DataType = "nvarchar(255)", ViUser = true });
        RenderDetail(true);
        var r = _shownCols.IndexOf(a.Columns.Count - 1);
        if (r >= 0)
        {
            _grid.ClearSelection();
            _grid.CurrentCell = _grid.Rows[r].Cells["col"];
            _grid.Rows[r].Selected = true;
            _grid.BeginEdit(true);
        }
        UpdateInfo("Đã thêm 1 cột thêm tay — gõ tên cột, kiểu, tên trường API…; chọn dòng rồi Delete để xoá.");
    }

    /// <summary>Delete trên lưới: xoá các cột THÊM TAY đang chọn (cột lấy từ database thì không xoá — bỏ tick ✓ là đủ).</summary>
    private void DeleteManualRows()
    {
        SyncCur(); var a = Cur; if (a is null) return;
        var idx = _grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i < _shownCols.Count).Select(i => _shownCols[i])
            .Where(i => a.Columns[i].Manual).OrderByDescending(i => i).ToList();
        if (idx.Count == 0) { UpdateInfo("Chỉ xoá được cột thêm tay — cột lấy từ database thì bỏ tick ✓."); return; }
        foreach (var i in idx) a.Columns.RemoveAt(i);
        RenderDetail(true);
        UpdateInfo($"Đã xoá {idx.Count} cột thêm tay.");
    }

    private void ReqClear() { SyncCur(); var a = Cur; if (a is null) return; foreach (var c in Vis(a)) c.Required = false; RenderDetail(); }

    private void ExportNative()
    {
        _grid.EndEdit(); SyncCur();
        var model = _apis.Select(a => new
        {
            name = a.Name, desc = a.Desc, func = a.Func,
            tables = a.Tables.Select(t => new { db = t.Db, table = t.Table }),
            columns = a.Columns.Select(c => new { selected = c.Selected, colName = c.ColName, dataType = c.DataType, isPk = c.IsPk, required = c.Required, fieldDesc = c.FieldDesc, logicDesc = c.LogicDesc, viName = c.ViName, tname = c.TName }),
        }).ToList();
        ExportWord(JsonSerializer.SerializeToElement(model), _chkHide.Checked);
    }
}
