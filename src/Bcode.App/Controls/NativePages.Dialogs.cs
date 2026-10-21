using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Khung chung của các trang hộp thoại native: thanh trên (tuỳ chọn) / thân / thanh dưới (trạng thái + nút), phím Esc / Enter do trang xử lý qua <see cref="HandleKey"/>.</summary>
internal abstract class DialogPage : NativePage
{
    protected readonly Label StatusLabel = new() { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 14, 0) };
    protected readonly BarFlow Bottom = new() { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 7, 10, 7) };

    protected DialogPage() { Bottom.Controls.Add(StatusLabel); }

    protected void Layout(Control? top, Control main)
    {
        main.Dock = DockStyle.Fill;
        Controls.Add(main);
        if (top is not null) { top.Dock = DockStyle.Top; Controls.Add(top); }
        Controls.Add(Bottom);
    }

    protected void Say(string text, string? kind = null)
    {
        StatusLabel.Text = text;
        StatusLabel.ForeColor = kind switch { "err" => AppColors.Danger, "ok" => AppColors.Success, "warn" => AppColors.Warning, _ => AppColors.TextMuted };
    }

    protected virtual bool HandleKey(Keys key) => false;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => HandleKey(keyData) || base.ProcessCmdKey(ref msg, keyData);

    protected static BarFlow TopBar() => new() { WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 8, 10, 6) };

    protected static Panel Pad(Control c, int p = 12) { var host = new Panel { Padding = new Padding(p) }; c.Dock = DockStyle.Fill; host.Controls.Add(c); return host; }
}

/// <summary>Compare Structure (comparestructure.html): so cột của 1 bảng giữa 2 workspace.</summary>
internal sealed class CompareStructurePage : DialogPage
{
    private readonly DropDownButton _l = new() { Width = NativeSkin.S(190) }, _r = new() { Width = NativeSkin.S(190) };
    private readonly RoundedInput _schema = NK.Input("dbo") , _table = NK.Input("table name");
    private readonly DataGridView _grid;
    private readonly PillButton _run;
    private List<int> _indexes = new();

    public CompareStructurePage()
    {
        _schema.Text = "dbo"; _schema.Width = NativeSkin.S(90); _table.Width = NativeSkin.S(240);
        _run = NK.Button("Compare", RunNow, primary: true);
        var top = TopBar();
        top.Controls.AddRange(new Control[] { NK.Text("WS trái"), _l, NK.Text("WS phải"), _r, NK.Text("Schema"), _schema, NK.Text("Table"), _table, _run });
        _grid = NK.Grid(("st", "Trạng thái", 130), ("col", "Cột", 200), ("l", "Trái", 0), ("r", "Phải", 0));
        Bottom.Controls.Add(NK.Button("Đóng", () => Post(new { action = "close" })));
        Layout(top, _grid);
        _table.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; RunNow(); } };
    }

    private void RunNow() => Post(new { action = "run", left = _indexes.Count > 0 ? _indexes[Math.Max(0, _l.SelectedIndex)] : -1, right = _indexes.Count > 0 ? _indexes[Math.Max(0, _r.SelectedIndex)] : -1, schema = _schema.Text, table = _table.Text });

    public override void FocusPrimary() => _table.Inner.Focus();
    protected override bool HandleKey(Keys key) { if (key == Keys.Escape) { Post(new { action = "close" }); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "cmpStruct.init":
            {
                var ws = PA(Arg(a, 0), "workspaces").ToList();
                _indexes = ws.Select(w => PI(w, "index")).ToList();
                var names = ws.Select(w => P(w, "name")).ToList();
                _l.SetItems(names, 0); _r.SetItems(names, 0);
                _table.Inner.Focus();
                break;
            }
            case "cmpStruct.onBusy": _run.Enabled = !Bool(a, 0); if (Bool(a, 0)) Say("Đang so sánh..."); break;
            case "cmpStruct.onError": Say(Str(a, 0), "err"); break;
            case "cmpStruct.onResult":
            {
                var rows = a.Count > 0 ? a[0].EnumerateArray().ToList() : new List<JsonElement>();
                _grid.Rows.Clear();
                foreach (var r in rows)
                {
                    var i = _grid.Rows.Add(P(r, "text"), P(r, "column"), P(r, "left"), P(r, "right"));
                    _grid.Rows[i].DefaultCellStyle.ForeColor = P(r, "status") switch { "diff" => AppColors.Warning, "left" => AppColors.Success, "right" => AppColors.Danger, _ => AppColors.Text };
                }
                int N(string s) => rows.Count(r => P(r, "status") == s);
                Say($"{rows.Count} cột — khác nhau {N("diff")}, chỉ trái {N("left")}, chỉ phải {N("right")}, giống {N("same")}", N("diff") + N("left") + N("right") > 0 ? "warn" : "ok");
                break;
            }
        }
    }
}

/// <summary>Duplicate Menu (wcommandduplicate.html): kết quả "Check WCommand" gồm 2 tab.</summary>
internal sealed class DuplicateMenuPage : DialogPage
{
    private readonly PillButton _t1, _t2;
    private readonly DataGridView _grid = NK.Grid(("w", "WMenu Id", 110), ("bar", "Bar", 220), ("m", "Menu Id", 130), ("link", "Link", 0), ("sys", "Sysid", 130));
    private readonly Label _sum = NK.Muted("");
    private List<JsonElement> _notExists = new(), _diff = new();
    private int _tab = 1;

    public DuplicateMenuPage()
    {
        _t1 = NK.Button("Not exists in Command", () => { _tab = 1; Render(); }); _t2 = NK.Button("Difference Sysid", () => { _tab = 2; Render(); });
        var top = TopBar(); top.Controls.AddRange(new Control[] { _t1, _t2, _sum });
        Bottom.Controls.Add(NK.Button("Đóng", () => Post(new { action = "close" })));
        Layout(top, _grid);
    }

    protected override bool HandleKey(Keys key) { if (key == Keys.Escape) { Post(new { action = "close" }); return true; } return false; }

    private void Render()
    {
        _t1.IsPrimary = _tab == 1; _t2.IsPrimary = _tab == 2; _t1.Invalidate(); _t2.Invalidate();
        _grid.Rows.Clear();
        foreach (var r in _tab == 1 ? _notExists : _diff) _grid.Rows.Add(P(r, "wmenuId"), P(r, "bar"), P(r, "menuId"), P(r, "link"), P(r, "sysId"));
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "dupMenu.init") return;
        _notExists = PA(Arg(a, 0), "notExists").ToList(); _diff = PA(Arg(a, 0), "diffSysid").ToList();
        _sum.Text = $"Not exists in Command: {_notExists.Count}    Difference Sysid: {_diff.Count}";
        _tab = _notExists.Count > 0 || _diff.Count == 0 ? 1 : 2;
        Render();
    }
}

/// <summary>Tỉ lệ giao diện (uiscale.html).</summary>
internal sealed class UiScalePage : DialogPage
{
    private readonly DropDownButton _mode = new() { Width = NativeSkin.S(320) };
    private List<string> _values = new();

    public UiScalePage()
    {
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 14, 16, 8) };
        _mode.Height = NativeSkin.RowHeight(_mode, 28);
        body.Controls.Add(NK.Text("Tỉ lệ giao diện")); body.Controls.Add(_mode);
        var hint = NK.Muted("Tự động: chữ và nội dung co giãn để vừa khung màn hình hiện tại (khung chuẩn 1600×900). Chọn một mức cố định nếu muốn tự điều chỉnh.", wrap: true);
        hint.MaximumSize = new Size(380, 0); hint.Margin = new Padding(0, 12, 0, 0); body.Controls.Add(hint);
        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("OK", Ok, primary: true)); Bottom.Controls.Add(NK.Button("Hủy", () => Post(new { action = "cancel" })));
        Layout(null, body);
    }

    private void Ok() => Post(new { action = "ok", value = _values.Count > 0 ? _values[Math.Max(0, _mode.SelectedIndex)] : "" });
    protected override bool HandleKey(Keys key) { if (key == Keys.Escape) { Post(new { action = "cancel" }); return true; } if (key == Keys.Enter) { Ok(); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "uiScale.init") return;
        var d = Arg(a, 0);
        var opts = PA(d, "options").ToList();
        _values = opts.Select(o => P(o, "value")).ToList();
        _mode.SetItems(opts.Select(o => P(o, "label")).ToList(), Math.Max(0, _values.IndexOf(P(d, "current"))));
    }
}

/// <summary>"Khai báo nhanh" (listeditor.html): sửa các giá trị dạng danh sách "a, b, c" của nhiều cột, mỗi giá trị 1 dòng.</summary>
internal sealed class ListEditorPage : DialogPage
{
    private readonly DataGridView _grid = new() { AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = true, AllowUserToResizeRows = false, BorderStyle = System.Windows.Forms.BorderStyle.None, SelectionMode = DataGridViewSelectionMode.CellSelect };
    private readonly Label _cap = NK.Muted("");

    public ListEditorPage()
    {
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        var side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = NativeSkin.S(54), FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(4, 8, 4, 4) };
        foreach (var (t, tip, act) in new (string, string, System.Action)[] { ("↑", "Đưa dòng lên (Ctrl+↑)", () => Move(-1)), ("↓", "Đưa dòng xuống (Ctrl+↓)", () => Move(1)), ("＋", "Thêm dòng bên dưới (Ctrl+Enter)", AddRow), ("✕", "Xoá dòng đang chọn (Ctrl+Delete)", DelRow) })
        { var b = NK.Button(t, act, tip: tip); b.AutoWidth = false; b.Width = NativeSkin.S(40); b.Margin = new Padding(0, 0, 0, 6); side.Controls.Add(b); }
        var main = new Panel();
        main.Controls.Add(_grid); main.Controls.Add(side); main.Controls.Add(_cap); _cap.Dock = DockStyle.Top; _grid.Dock = DockStyle.Fill;
        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("✓ Save", Save, primary: true)); Bottom.Controls.Add(NK.Button("Cancel", () => Post(new { action = "cancel" })));
        Layout(null, main);
        _grid.RowPostPaint += (_, e) => { };
    }

    private void Save()
    {
        _grid.EndEdit();
        var cols = new List<object>();
        foreach (DataGridViewColumn c in _grid.Columns)
            cols.Add(new { name = c.HeaderText, items = _grid.Rows.Cast<DataGridViewRow>().Select(r => (r.Cells[c.Index].Value as string) ?? "").ToList() });
        Post(new { action = "save", columns = cols });
    }

    private int Row => _grid.CurrentCell?.RowIndex ?? 0;
    private void AddRow()
    {
        var at = Math.Min(_grid.Rows.Count, Row + 1);
        _grid.Rows.Insert(at, new object[_grid.Columns.Count].Select(_ => (object)"").ToArray());
        UpdateNumbers(); _grid.CurrentCell = _grid.Rows[at].Cells[0]; _grid.BeginEdit(true);
    }
    private void DelRow()
    {
        if (_grid.Rows.Count == 0) return;
        var r = Row; _grid.Rows.RemoveAt(r); UpdateNumbers();
        if (_grid.Rows.Count > 0) _grid.CurrentCell = _grid.Rows[Math.Min(r, _grid.Rows.Count - 1)].Cells[0];
        Say(_grid.Rows.Count + " dòng");
    }
    private void Move(int d)
    {
        var r = Row; var j = r + d;
        if (j < 0 || j >= _grid.Rows.Count) return;
        var col = _grid.CurrentCell?.ColumnIndex ?? 0;
        foreach (DataGridViewColumn c in _grid.Columns) { var t = _grid.Rows[r].Cells[c.Index].Value; _grid.Rows[r].Cells[c.Index].Value = _grid.Rows[j].Cells[c.Index].Value; _grid.Rows[j].Cells[c.Index].Value = t; }
        _grid.CurrentCell = _grid.Rows[j].Cells[col];
    }
    private void UpdateNumbers() { for (var i = 0; i < _grid.Rows.Count; i++) _grid.Rows[i].HeaderCell.Value = (i + 1).ToString(); Say(_grid.Rows.Count + " dòng"); }

    protected override bool HandleKey(Keys k)
    {
        if (k == Keys.Escape && !_grid.IsCurrentCellInEditMode) { Post(new { action = "cancel" }); return true; }
        if (k == (Keys.Control | Keys.S)) { Save(); return true; }
        if (k == (Keys.Control | Keys.Up)) { Move(-1); return true; }
        if (k == (Keys.Control | Keys.Down)) { Move(1); return true; }
        if (k == (Keys.Control | Keys.Enter)) { AddRow(); return true; }
        if (k == (Keys.Control | Keys.Delete)) { DelRow(); return true; }
        return false;
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "listEditor.init") return;
        var d = Arg(a, 0);
        _cap.Text = P(d, "caption");
        var cols = PA(d, "columns").Select(c => (Name: P(c, "name"), Items: PA(c, "items").Select(i => i.GetString() ?? "").ToList())).ToList();
        _grid.Columns.Clear(); _grid.Rows.Clear();
        foreach (var c in cols) _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c.Name, SortMode = DataGridViewColumnSortMode.NotSortable });
        var n = Math.Max(1, cols.Count == 0 ? 1 : cols.Max(c => c.Items.Count));
        for (var r = 0; r < n; r++) _grid.Rows.Add(cols.Select(c => (object)(r < c.Items.Count ? c.Items[r] : "")).ToArray());
        _grid.RowHeadersWidth = 56;
        UpdateNumbers();
        if (_grid.Rows.Count > 0 && _grid.Columns.Count > 0) _grid.CurrentCell = _grid.Rows[0].Cells[0];
    }

    public override void FocusPrimary() => _grid.Focus();
}

/// <summary>"View Detail Datarow" (rowdetail.html): form mọi cột của 1 dòng; trả về các cột đã sửa.</summary>
internal sealed class RowDetailPage : DialogPage
{
    private sealed class Fld { public string Name = "", Kind = "", Type = ""; public string? Value; public bool ReadOnly; public Control Ctl = null!; public bool IsList, IsNull; }

    private readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
    private readonly Panel _scroll = new() { AutoScroll = true, Padding = new Padding(12) };
    private readonly Label _cap = NK.Muted("");
    private List<Fld> _fields = new();

    public RowDetailPage()
    {
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220)); _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _cap.Dock = DockStyle.Top; _scroll.Controls.Add(_grid); _scroll.Controls.Add(_cap);
        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("Cancel", () => Post(new { action = "cancel" }))); Bottom.Controls.Add(NK.Button("✓ OK", Save, primary: true));
        Layout(null, _scroll);
    }

    private static bool IsList(string kind, string? v) => kind == "text" && v is not null && v.Count(c => c == ',') >= 2 && v.Length > 30 && !v.Contains('\n') && !v.Contains('\r');

    private void Save()
    {
        var changed = new Dictionary<string, string?>();
        foreach (var f in _fields)
        {
            if (f.ReadOnly) continue;
            string? now;
            if (f.Kind == "bool") now = ((CheckBox)f.Ctl).Checked ? "True" : "False";
            else
            {
                var text = ((RoundedInput)f.Ctl).Text;
                now = f.IsNull && text == "NULL" ? null : f.IsList ? string.Join(", ", text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0)) : text;
            }
            var orig = f.Value;
            bool same = f.Kind == "bool" ? ((orig is "True" or "true" or "1") ? "True" : "False") == now : (now == orig || (now == "" && orig is null));
            if (!same) changed[f.Name] = now == "" && f.Kind != "text" ? null : now;
        }
        Post(new { action = "save", changed });
    }

    protected override bool HandleKey(Keys k) { if (k == Keys.Escape) { Post(new { action = "cancel" }); return true; } if (k == (Keys.Control | Keys.S)) { Save(); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "rowDetail.init") return;
        var d = Arg(a, 0);
        _cap.Text = P(d, "caption");
        _fields = PA(d, "fields").Select(e => new Fld { Name = P(e, "name"), Kind = P(e, "kind"), Type = P(e, "type"), Value = e.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null ? v.ToString() : null, ReadOnly = PB(e, "readOnly") }).ToList();
        _grid.SuspendLayout(); _grid.Controls.Clear(); _grid.RowStyles.Clear(); _grid.RowCount = 0;
        foreach (var f in _fields)
        {
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lb = new Label { Text = f.Name + "  (" + f.Type + ")", AutoSize = true, BackColor = Color.Transparent, ForeColor = f.ReadOnly ? AppColors.TextMuted : AppColors.Text, Margin = new Padding(0, 9, 8, 0), AutoEllipsis = true };
            Control ctl;
            if (f.Kind == "bool") ctl = new CheckBox { Checked = f.Value is "True" or "true" or "1", Enabled = !f.ReadOnly, AutoSize = true, Margin = new Padding(0, 7, 0, 3), BackColor = Color.Transparent };
            else
            {
                f.IsList = IsList(f.Kind, f.Value); f.IsNull = f.Value is null;
                var multi = f.Kind == "text" && f.Value is not null && (f.Value.Length > 80 || f.Value.Contains('\n') || f.Value.Contains('\r')) && !f.IsList;
                var inp = multi ? NK.Memo(4, "", mono: true) : NK.Input("", mono: false);
                inp.Text = f.Value ?? "NULL";
                inp.ReadOnly = f.ReadOnly;
                if (f.IsNull)
                {
                    // ô NULL: bấm vào thì xoá chữ "NULL" để gõ; để trống rồi rời đi thì trả lại "NULL"
                    var ff = f;
                    inp.Inner.ForeColor = AppColors.TextMuted;
                    inp.Inner.Enter += (_, _) => { if (ff.IsNull && inp.Text == "NULL") inp.Text = ""; };
                    inp.Inner.Leave += (_, _) => { if (ff.IsNull && inp.Text.Length == 0) inp.Text = "NULL"; };
                }
                ctl = inp;
            }
            f.Ctl = ctl;
            ctl.Dock = f.Kind == "bool" ? DockStyle.None : DockStyle.Fill; ctl.Margin = new Padding(0, 3, 0, 3);
            _grid.Controls.Add(lb); _grid.Controls.Add(ctl);
        }
        _grid.ResumeLayout(true);
    }

    public override void FocusPrimary() { var first = _fields.FirstOrDefault(f => !f.ReadOnly); if (first?.Ctl is RoundedInput ri) { ri.Inner.Focus(); ri.Inner.SelectAll(); } }
}

/// <summary>"Add New Column" / "Edit Column" (columndesign.html): bảng Name / Type / Length / Not NULL.</summary>
internal sealed class ColumnDesignPage : DialogPage
{
    private static readonly string[] Types = { "bigint", "bit", "char", "date", "datetime", "datetime2", "decimal", "float", "image", "int", "money", "nchar", "ntext", "numeric", "nvarchar", "real", "smalldatetime", "smallint", "text", "tinyint", "uniqueidentifier", "varbinary", "varchar", "xml" };
    private static readonly Dictionary<string, string> DefLen = new() { ["char"] = "16", ["nchar"] = "16", ["varchar"] = "128", ["nvarchar"] = "128", ["varbinary"] = "max", ["decimal"] = "19,4", ["numeric"] = "19,4" };

    private readonly RoundedInput _tn = NK.Input("") ;
    private readonly Label _hint = NK.Muted("");
    private readonly DataGridView _grid = new() { AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, AllowUserToResizeRows = false, BorderStyle = System.Windows.Forms.BorderStyle.None };
    private bool _isAdd = true, _loading;

    public ColumnDesignPage()
    {
        _tn.ReadOnly = true; _tn.Width = NativeSkin.S(320);
        var top = TopBar(); top.Controls.AddRange(new Control[] { NK.Text("Table Name"), _tn, _hint });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "n", HeaderText = "#", ReadOnly = true, Width = NativeSkin.S(40) });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 160 });
        var ty = new DataGridViewComboBoxColumn { Name = "type", HeaderText = "Type", Width = NativeSkin.S(150), FlatStyle = FlatStyle.Flat }; ty.Items.AddRange(Types); _grid.Columns.Add(ty);
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "len", HeaderText = "Length", Width = NativeSkin.S(100) });
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "nn", HeaderText = "Not NULL", Width = NativeSkin.S(80) });
        _grid.Columns.Add(new DataGridViewButtonColumn { Name = "del", HeaderText = "", Text = "✕", UseColumnTextForButtonValue = true, Width = NativeSkin.S(40) });
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_loading || e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "type") return;
            var t = _grid.Rows[e.RowIndex].Cells["type"].Value as string ?? "";
            _loading = true; _grid.Rows[e.RowIndex].Cells["len"].Value = DefLen.TryGetValue(t, out var l) ? l : ""; _loading = false;
            _grid.Rows[e.RowIndex].Cells["len"].ReadOnly = !DefLen.ContainsKey(t);
        };
        _grid.CellContentClick += (_, e) => { if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == "del") { _grid.Rows.RemoveAt(e.RowIndex); if (_grid.Rows.Count == 0) AddRow("", "", "", false); Renumber(); } };
        _grid.DataError += (_, e) => e.ThrowException = false;
        var main = new Panel { Padding = new Padding(10) };
        var addRow = NK.Row(NK.Button("+ Thêm cột", () => { AddRow("", _isAdd ? "nvarchar" : "varchar", "128", false); Renumber(); }));
        addRow.Dock = DockStyle.Bottom; _grid.Dock = DockStyle.Fill; main.Controls.Add(_grid); main.Controls.Add(addRow);
        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("Cancel", () => Post(new { action = "cancel" }))); Bottom.Controls.Add(NK.Button("OK", Ok, primary: true));
        Layout(top, main);
    }

    private void AddRow(string name, string type, string len, bool nn)
    {
        _loading = true;
        if (type.Length > 0 && !((DataGridViewComboBoxColumn)_grid.Columns["type"]).Items.Contains(type)) ((DataGridViewComboBoxColumn)_grid.Columns["type"]).Items.Insert(0, type);
        var i = _grid.Rows.Add("", name, type.Length > 0 ? type : null, len, nn);
        _grid.Rows[i].Cells["len"].ReadOnly = !DefLen.ContainsKey(type);
        _loading = false;
    }

    private void Renumber() { for (var i = 0; i < _grid.Rows.Count; i++) _grid.Rows[i].Cells["n"].Value = (i + 1).ToString(); }

    private void Ok()
    {
        _grid.EndEdit();
        var cols = _grid.Rows.Cast<DataGridViewRow>().Select(r => new { name = ((r.Cells["name"].Value as string) ?? "").Trim(), type = (r.Cells["type"].Value as string) ?? "", length = ((r.Cells["len"].Value as string) ?? "").Trim(), notNull = r.Cells["nn"].Value is true })
            .Where(c => c.name.Length > 0 && c.type.Length > 0).ToList();
        if (cols.Count == 0) { Say("Nhập ít nhất 1 cột (tên + kiểu).", "err"); return; }
        Post(new { action = "ok", cols });
    }

    protected override bool HandleKey(Keys k) { if (k == Keys.Escape && !_grid.IsCurrentCellInEditMode) { Post(new { action = "cancel" }); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "colDesign.init") return;
        var d = Arg(a, 0);
        _isAdd = PB(d, "isAdd");
        _tn.Text = P(d, "table");
        _hint.Text = P(d, "hint") is { Length: > 0 } hint ? hint : _isAdd ? "Thêm cột mới (cột đã tick ở Structure được điền sẵn kiểu để copy sang bảng khác)." : "Sửa kiểu / độ dài các cột đã tick, rồi OK để sinh script Alter Column.";
        _grid.Rows.Clear();
        var cols = PA(d, "cols").ToList();
        foreach (var c in cols) AddRow(P(c, "name"), P(c, "type"), P(c, "length"), PB(c, "notNull"));
        if (cols.Count == 0) AddRow("", _isAdd ? "nvarchar" : "", _isAdd ? "128" : "", false);
        Renumber();
    }

    public override void FocusPrimary() { if (_grid.Rows.Count > 0) { _grid.CurrentCell = _grid.Rows[_grid.Rows.Count - 1].Cells["name"]; _grid.BeginEdit(true); } }
}

/// <summary>"Debug từng bước — tham số" (stepparams.html): mỗi tham số 1 ô, nhập đúng dạng T-SQL.</summary>
internal sealed class StepParamsPage : DialogPage
{
    private readonly Label _head = NK.Muted("", wrap: true);
    private readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
    private readonly Panel _scroll = new() { AutoScroll = true, Padding = new Padding(14) };
    private readonly Dictionary<string, RoundedInput> _inputs = new();

    public StepParamsPage()
    {
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 230)); _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _head.Dock = DockStyle.Top; _scroll.Controls.Add(_grid); _scroll.Controls.Add(_head);
        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("Bắt đầu debug", Ok, primary: true)); Bottom.Controls.Add(NK.Button("Huỷ", () => Post(new { action = "cancel" })));
        Layout(null, _scroll);
    }

    private void Ok() => Post(new { action = "ok", values = _inputs.ToDictionary(k => k.Key, k => k.Value.Text) });
    protected override bool HandleKey(Keys k) { if (k == Keys.Escape) { Post(new { action = "cancel" }); return true; } if (k == Keys.Enter) { Ok(); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "stepParams.init") return;
        var d = Arg(a, 0);
        _head.Text = P(d, "routine") + " — nhập giá trị tham số (dạng T-SQL: 'chuỗi', số, NULL):";
        _grid.SuspendLayout(); _grid.Controls.Clear(); _grid.RowStyles.Clear(); _inputs.Clear();
        foreach (var p in PA(d, "parameters"))
        {
            _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var lb = new Label { Text = P(p, "name") + "  (" + P(p, "type") + ")", AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 9, 8, 0), AutoEllipsis = true };
            var inp = NK.Input("", mono: true); inp.Text = P(p, "value"); inp.Dock = DockStyle.Fill; inp.Margin = new Padding(0, 3, 0, 3);
            _inputs[P(p, "name")] = inp;
            _grid.Controls.Add(lb); _grid.Controls.Add(inp);
        }
        _grid.ResumeLayout(true);
    }

    public override void FocusPrimary() { var f = _inputs.Values.FirstOrDefault(); if (f is not null) { f.Inner.Focus(); f.Inner.SelectAll(); } }
}
