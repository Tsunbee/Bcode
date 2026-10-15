using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Clone danh mục (catalogclone.html): đọc cột của 1 bảng nguồn, chọn cột Grid/Form, đổi tên, rồi sinh file Dir/Grid/Main (+ script bảng mới).</summary>
internal sealed class CatalogClonePage : DialogPage
{
    private sealed class Col
    {
        public bool InGrid = true, InForm = true, AllowNulls, ReadOnly;
        public string Src = "", Name = "", SqlType = "", HeaderV = "", HeaderE = "", Width = "150", Type = "";
    }

    private static readonly Regex NameOk = new(@"^[A-Za-z_][A-Za-z0-9_$]*$");
    private static readonly string[] Types = { "", "Boolean", "Decimal", "DateTime" };

    private readonly RoundedInput _src = NK.Input("vd dmkh"), _template = NK.Input(""), _output = NK.Input("");
    private readonly RoundedInput _id = NK.Input("vd Supplier"), _main = NK.Input("vd apncc"), _table = NK.Input(""), _key = NK.Input("");
    private readonly RoundedInput _titleV = NK.Input(""), _titleE = NK.Input(""), _subV = NK.Input(""), _subE = NK.Input(""), _order = NK.Input(""), _lookupTable = NK.Input("mặc định = bảng của danh mục mới");
    private readonly CheckBox _lookup = NK.Check("Tạo Lookup — bảng lookup");
    private readonly FlowLayoutPanel _tb = new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.Transparent };
    private readonly RoundedInput _flt = NK.Input("Lọc cột…"), _rFind = NK.Input("_kh"), _rTo = NK.Input("_ncc");
    private readonly Label _cnt = NK.Muted(""), _tblNote = NK.Muted("");
    private readonly DataGridView _grid;
    private readonly AutoCompleteStringCollection _tableSrc = new();
    private List<Col> _cols = new();
    private List<int> _shown = new();
    private string _srcLoaded = "";
    private bool _orderAuto = true, _tableAuto = true, _idAuto = true, _loading;

    public CatalogClonePage()
    {
        _src.Inner.AutoCompleteMode = AutoCompleteMode.Suggest; _src.Inner.AutoCompleteSource = AutoCompleteSource.CustomSource; _src.Inner.AutoCompleteCustomSource = _tableSrc;
        var load = NK.Button("Nạp cột", LoadCols, primary: true);
        var pickT = NK.Button("...", () => Post(new { action = "pickFolder", target = "template", path = _template.Text }), tip: "Chọn thư mục");
        var pickO = NK.Button("...", () => Post(new { action = "pickFolder", target = "output", path = _output.Text }), tip: "Chọn thư mục");
        Control WithBtn(RoundedInput i, Control b) { var p = new Panel { Height = NativeSkin.RowHeight(this, 28) }; b.Dock = DockStyle.Right; i.Dock = DockStyle.Fill; p.Controls.Add(i); p.Controls.Add(b); return p; }

        var c1 = new CardPanel("1 · Nguồn & nơi lưu") { Dock = DockStyle.Top };
        c1.Body.Controls.Add(NK.FormGrid(220, ("Bảng nguồn (lấy danh sách cột)", WithBtn(_src, load)), ("Thư mục mẫu", WithBtn(_template, pickT)), ("Lưu vào (App_Data\\Controllers)", WithBtn(_output, pickO))));
        var c2 = new CardPanel("2 · Danh mục mới") { Dock = DockStyle.Top };
        _lookupTable.Enabled = false;
        var lk = new Panel { Height = NativeSkin.RowHeight(this, 28) };
        _lookup.Dock = DockStyle.Left; _lookupTable.Dock = DockStyle.Fill; lk.Controls.Add(_lookupTable); lk.Controls.Add(_lookup);
        c2.Body.Controls.Add(NK.FormGrid(220, ("Id (tên file Dir / Grid)", _id), ("File Main (.aspx)", _main), ("Bảng của danh mục mới", _table), ("Key", _key), ("Title (v)", _titleV), ("Title (e)", _titleE), ("Subtitle (v)", _subV), ("Subtitle (e)", _subE), ("Order", _order), ("Lookup", lk), ("", _tblNote)));
        _lookup.CheckedChanged += (_, _) => _lookupTable.Enabled = _lookup.Checked;

        var tbRow = NK.Row(NK.Text("Toolbar:"), _tb); tbRow.Dock = DockStyle.Top;
        _flt.Width = NativeSkin.S(180); _rFind.Width = NativeSkin.S(90); _rTo.Width = NativeSkin.S(90);
        var tools = TopBar();
        tools.Controls.AddRange(new Control[]
        {
            NK.Text("Cột:"), _flt, _cnt,
            NK.Muted("Grid"), NK.Button("✓ hết", () => SetAll(true, true)), NK.Button("✕ hết", () => SetAll(true, false)),
            NK.Muted("Form"), NK.Button("✓ hết", () => SetAll(false, true)), NK.Button("✕ hết", () => SetAll(false, false)),
            NK.Muted("Đổi tên: thay"), _rFind, NK.Muted("→"), _rTo, NK.Button("Áp dụng", Rename), NK.Button("Về tên gốc", RenameReset, tip: "Trả tên các cột đang hiện về tên cột nguồn"),
        });

        _grid = NK.Grid(("src", "Cột nguồn", 190), ("name", "Name (tên mới)", 190), ("hv", "Header (v)", 0), ("he", "Header (e)", 0), ("w", "Width", 70));
        _grid.ReadOnly = false;
        _grid.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "g", HeaderText = "Grid", Width = NativeSkin.S(48) });
        _grid.Columns.Insert(1, new DataGridViewCheckBoxColumn { Name = "f", HeaderText = "Form", Width = NativeSkin.S(48) });
        var ty = new DataGridViewComboBoxColumn { Name = "type", HeaderText = "type", Width = NativeSkin.S(100), FlatStyle = FlatStyle.Flat }; ty.Items.AddRange(Types); _grid.Columns.Add(ty);
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "nul", HeaderText = "AllowNulls", Width = NativeSkin.S(78) });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "ro", HeaderText = "ReadOnly", Width = NativeSkin.S(74) });
        _grid.Columns["src"]!.ReadOnly = true;
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += OnCell;
        _grid.DataError += (_, e) => e.ThrowException = false;

        _flt.TextChanged += (_, _) => RenderCols();
        _src.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; LoadCols(); } };
        _src.Inner.Leave += (_, _) => { if (_src.Text.Trim().Length > 0 && !string.Equals(_src.Text.Trim(), _srcLoaded, StringComparison.OrdinalIgnoreCase)) LoadCols(); };
        _table.TextChanged += (_, _) => { if (_loading) return; _tableAuto = false; if (_idAuto) { _loading = true; _id.Text = _table.Text; _loading = false; } RefreshMeta(); };
        _id.TextChanged += (_, _) => { if (!_loading) _idAuto = false; };
        _key.TextChanged += (_, _) => { if (_loading) return; if (_order.Text.Length == 0 || _orderAuto) { _loading = true; _order.Text = _key.Text; _loading = false; _orderAuto = true; } RenderCols(); };
        _order.Inner.KeyDown += (_, _) => _orderAuto = false;

        var upper = new Panel { Dock = DockStyle.Top, AutoScroll = false, AutoSize = true, Padding = new Padding(10, 6, 10, 0) };
        // Dock Top: thêm ngược để c1 nằm trên cùng
        upper.Controls.Add(tools); upper.Controls.Add(tbRow); upper.Controls.Add(c2); upper.Controls.Add(c1);
        var main = new Panel { AutoScroll = true };
        _grid.Dock = DockStyle.Fill; _grid.MinimumSize = new Size(300, 220);
        // Cuộn dọc cả trang khi màn hình thấp: lưới nằm cuối, cao tối thiểu 220
        var host = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
        host.Controls.Add(upper); _grid.Height = NativeSkin.S(300); _grid.Dock = DockStyle.Top; host.Controls.Add(_grid);
        main.Controls.Add(host);

        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("Tạo file", Generate, primary: true)); Bottom.Controls.Add(NK.Button("Đóng", () => Post(new { action = "close" })));
        Layout(null, main);
        main.Resize += (_, _) => { _grid.Height = Math.Max(240, main.ClientSize.Height - upper.Height - 8); };
    }

    // ---- logic ----
    private void LoadCols()
    {
        if (_src.Text.Trim().Length == 0) { Say("Nhập bảng nguồn trước.", "err"); return; }
        Post(new { action = "loadColumns", table = _src.Text });
    }

    private bool Visible(Col c)
    {
        var f = _flt.Text.Trim().ToLowerInvariant();
        return f.Length == 0 || c.Src.ToLowerInvariant().Contains(f) || c.Name.ToLowerInvariant().Contains(f) || c.HeaderV.ToLowerInvariant().Contains(f);
    }

    private (bool Bad, bool Ren) NameState(int i)
    {
        var n = _cols[i].Name.Trim();
        var dup = n.Length > 0 && _cols.Where((c, j) => j != i && string.Equals(c.Name.Trim(), n, StringComparison.OrdinalIgnoreCase)).Any();
        return (n.Length == 0 || !NameOk.IsMatch(n) || dup, n != _cols[i].Src);
    }

    private void RenderCols()
    {
        _loading = true;
        _grid.SuspendLayout();
        _grid.Rows.Clear(); _shown.Clear();
        var key = _key.Text.Trim().ToLowerInvariant();
        for (var i = 0; i < _cols.Count; i++)
        {
            var c = _cols[i];
            if (!Visible(c)) continue;
            _shown.Add(i);
            var r = _grid.Rows.Add(c.InGrid, c.InForm, c.Src + "  " + c.SqlType, c.Name, c.HeaderV, c.HeaderE, c.Width, c.Type, c.AllowNulls, c.ReadOnly);
            Style(_grid.Rows[r], i, key);
        }
        _grid.ResumeLayout();
        _loading = false;
        RefreshMeta();
    }

    private void Style(DataGridViewRow row, int i, string key)
    {
        var st = NameState(i);
        row.Cells["name"].Style.ForeColor = st.Bad ? AppColors.Danger : st.Ren ? AppColors.Warning : AppColors.Text;
        row.DefaultCellStyle.Font = _cols[i].Name.Trim().ToLowerInvariant() == key && key.Length > 0 ? new Font(ThemeManager.BaseFont, FontStyle.Bold) : null;
    }

    private void RefreshMeta()
    {
        var renamed = _cols.Count(c => c.Name.Trim() != c.Src);
        _cnt.Text = _cols.Count > 0 ? $"{_shown.Count}/{_cols.Count} cột" + (renamed > 0 ? $" · {renamed} cột đổi tên" : "") : "";
        var nt = _table.Text.Trim();
        var diff = nt.Length > 0 && _srcLoaded.Length > 0 && !string.Equals(nt, _srcLoaded, StringComparison.OrdinalIgnoreCase);
        _tblNote.Text = _srcLoaded.Length == 0 ? "" : diff || renamed > 0 ? "Sẽ sinh thêm script CREATE TABLE cho bảng mới." : "Dùng lại bảng nguồn (không sinh script bảng).";
    }

    private void OnCell(object? s, DataGridViewCellEventArgs e)
    {
        if (_loading || e.RowIndex < 0 || e.RowIndex >= _shown.Count) return;
        var i = _shown[e.RowIndex]; var c = _cols[i]; var row = _grid.Rows[e.RowIndex];
        string Cell(string n) => (row.Cells[n].Value as string) ?? "";
        switch (_grid.Columns[e.ColumnIndex].Name)
        {
            case "g": c.InGrid = row.Cells["g"].Value is true; break;
            case "f": c.InForm = row.Cells["f"].Value is true; break;
            case "hv": c.HeaderV = Cell("hv"); break;
            case "he": c.HeaderE = Cell("he"); break;
            case "w": c.Width = Cell("w"); break;
            case "type": c.Type = Cell("type"); break;
            case "nul": c.AllowNulls = row.Cells["nul"].Value is true; break;
            case "ro": c.ReadOnly = row.Cells["ro"].Value is true; break;
            case "name":
                SetName(i, Cell("name"));
                _loading = true; row.Cells["hv"].Value = c.HeaderV; row.Cells["he"].Value = c.HeaderE; _loading = false;
                Style(row, i, _key.Text.Trim().ToLowerInvariant());
                RefreshMeta();
                break;
        }
    }

    // Đổi tên 1 cột: Key / Order đang trỏ vào tên cũ thì đi theo tên mới; Header còn bằng tên cũ thì cũng đổi theo.
    private void SetName(int i, string next)
    {
        var c = _cols[i]; var prev = c.Name; c.Name = next;
        _loading = true;
        if (prev.Trim().Length > 0 && string.Equals(_key.Text.Trim(), prev.Trim(), StringComparison.OrdinalIgnoreCase)) _key.Text = next.Trim();
        if (prev.Trim().Length > 0 && string.Equals(_order.Text.Trim(), prev.Trim(), StringComparison.OrdinalIgnoreCase)) _order.Text = next.Trim();
        _loading = false;
        if (c.HeaderV == prev) c.HeaderV = next;
        if (c.HeaderE == prev) c.HeaderE = next;
    }

    private void SetAll(bool grid, bool on)
    {
        foreach (var c in _cols) if (Visible(c)) { if (grid) c.InGrid = on; else c.InForm = on; }
        RenderCols();
    }

    private void Rename()
    {
        var f = _rFind.Text; var to = _rTo.Text;
        if (f.Length == 0) { Say("Nhập chuỗi cần thay ở ô \"thay\".", "err"); return; }
        var n = 0;
        for (var i = 0; i < _cols.Count; i++) if (Visible(_cols[i]) && _cols[i].Name.Contains(f)) { SetName(i, _cols[i].Name.Replace(f, to)); n++; }
        RenderCols(); Say($"Đã đổi tên {n} cột.", n > 0 ? "ok" : null);
    }

    private void RenameReset()
    {
        for (var i = 0; i < _cols.Count; i++) if (Visible(_cols[i]) && _cols[i].Name != _cols[i].Src) SetName(i, _cols[i].Src);
        RenderCols();
    }

    private void Generate()
    {
        _grid.EndEdit();
        if (_cols.Count == 0) { Say("Chưa nạp cột — nhập bảng nguồn rồi bấm Nạp cột.", "err"); return; }
        for (var i = 0; i < _cols.Count; i++)
            if (NameState(i).Bad)
            {
                Say($"Tên cột \"{_cols[i].Name}\" (gốc {_cols[i].Src}) không hợp lệ hoặc bị trùng.", "err");
                _flt.Text = ""; RenderCols();
                var idx = _shown.IndexOf(i);
                if (idx >= 0) { _grid.CurrentCell = _grid.Rows[idx].Cells["name"]; _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, idx - 3); }
                return;
            }
        var toolbar = _tb.Controls.OfType<CheckBox>().Where(c => c.Checked).Select(c => c.Text).ToList();
        var columns = _cols.Select(c => new { inGrid = c.InGrid, inForm = c.InForm, src = c.Src, name = c.Name.Trim(), sqlType = c.SqlType, headerV = c.HeaderV, headerE = c.HeaderE, width = c.Width, type = c.Type, allowNulls = c.AllowNulls, readOnly = c.ReadOnly }).ToList();
        Post(new
        {
            action = "generate", template = _template.Text, output = _output.Text,
            spec = new { id = _id.Text, main = _main.Text, table = _table.Text, sourceTable = _srcLoaded, key = _key.Text, order = _order.Text, titleV = _titleV.Text, titleE = _titleE.Text, subV = _subV.Text, subE = _subE.Text, lookup = _lookup.Checked, lookupTable = _lookupTable.Text, toolbar, columns },
        });
    }

    protected override bool HandleKey(Keys k) { if (k == Keys.Escape && !_grid.IsCurrentCellInEditMode) { Post(new { action = "close" }); return true; } return false; }
    public override void FocusPrimary() => _src.Inner.Focus();

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "catalog.init":
            {
                var d = Arg(a, 0);
                _template.Text = P(d, "template"); _output.Text = P(d, "output");
                _tb.Controls.Clear();
                foreach (var n in PA(d, "toolbar")) { var cb = NK.Check(n.GetString() ?? ""); cb.Checked = true; _tb.Controls.Add(cb); }
                _src.Inner.Focus();
                break;
            }
            case "catalog.onTables":
            {
                _tableSrc.Clear();
                if (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array) _tableSrc.AddRange(a[0].EnumerateArray().Select(x => x.GetString() ?? "").ToArray());
                break;
            }
            case "catalog.onColumns":
            {
                var d = Arg(a, 0);
                _srcLoaded = P(d, "table"); _loading = true; _src.Text = _srcLoaded;
                _cols = PA(d, "columns").Select(c => new Col { Src = P(c, "name"), Name = P(c, "name"), SqlType = P(c, "sqlType"), HeaderV = P(c, "headerV"), HeaderE = P(c, "headerE"), Type = P(c, "type"), AllowNulls = !PB(c, "isKey") }).ToList();
                _key.Text = P(d, "key");
                if (_order.Text.Length == 0 || _orderAuto) { _order.Text = P(d, "key"); _orderAuto = true; }
                if (_table.Text.Length == 0 || _tableAuto) { _table.Text = _srcLoaded; _tableAuto = true; }
                if (_id.Text.Length == 0 || _idAuto) { _id.Text = _table.Text; _idAuto = true; }
                _flt.Text = "";
                _loading = false;
                RenderCols();
                break;
            }
            case "catalog.onFolder": { var t = Str(a, 0); var p = Str(a, 1); if (t == "template") _template.Text = p; else if (t == "output") _output.Text = p; break; }
            case "catalog.onStatus": Say(Str(a, 0), Str(a, 1)); break;
        }
    }
}
