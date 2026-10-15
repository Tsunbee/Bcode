using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Cột trái của tab "Command" ở chế độ App/WinForms — thay trang WebView2 commandquery.html: các ô SELECT / FROM / WHERE / ORDER BY / Top, nút Add Script + Run,
/// danh sách Fields (tick cột → SELECT được dựng lại từ các cột đã tick, "*" khi không tick cột nào). <see cref="SqlQueryControl"/> nói chuyện với panel này bằng đúng
/// các thao tác như với trang web (Run / FromChanged / AddScript và SetFrom / SetWhere / SetFields / SetBusy...), nên phần truy vấn + lưới giữ nguyên.
/// </summary>
public sealed class NativeCommandPanel : UserControl
{
    private readonly RoundedInput _select = new() { Text = "*" }, _from = new(), _where = new(), _orderBy = new(), _top = new() { PlaceholderText = "0=tất cả" };
    private readonly CheckedListBox _fields = new() { Dock = DockStyle.Fill, CheckOnClick = true, IntegralHeight = false };
    private readonly CheckBox _all = new() { Text = "Tất cả", AutoSize = true };
    private readonly Label _fst = new MutedLabel { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, BackColor = Color.Transparent };
    private readonly PillButton _btnRun, _btnAdd;
    private readonly System.Windows.Forms.Timer _fromTimer = new() { Interval = 600 };
    private bool _rebuilding;
    private string _lastFrom = "";

    public event Action<string, string, string, string, string>? RunRequested;   // select, from, where, orderBy, top
    public event Action<string>? FromChanged;
    public event Action<string>? AddScriptRequested;

    public NativeCommandPanel()
    {
        Dock = DockStyle.Fill;
        var box = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(10, 8, 10, 4) };
        Label L(string t) => new MutedLabel { Text = t, AutoSize = true, Margin = new Padding(2, 4, 0, 1), BackColor = Color.Transparent };
        foreach (var (cap, input) in new[] { ("SELECT", _select), ("FROM", _from), ("WHERE", _where), ("ORDER BY", _orderBy) })
        {
            box.Controls.Add(L(cap)); input.Dock = DockStyle.Top; input.Margin = new Padding(0, 0, 0, 2); box.Controls.Add(input);
        }
        _btnRun = PillButton.Flat("▶ Run", primary: true); _btnAdd = PillButton.Flat("Add Script");
        NK.Tip(_btnAdd, "Gói mọi dòng đang xem thành script DELETE + INSERT");
        _btnRun.Click += (_, _) => DoRun(); _btnAdd.Click += (_, _) => AddScriptRequested?.Invoke(_from.Text);
        _top.Width = NativeSkin.RowHeight(this, 90); _top.Dock = DockStyle.None;
        var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        row.Controls.Add(L("Top")); _top.Margin = new Padding(4, 0, 12, 0); row.Controls.Add(_top);
        _btnAdd.Margin = new Padding(0, 2, 6, 0); _btnRun.Margin = new Padding(0, 2, 0, 0);
        row.Controls.Add(_btnAdd); row.Controls.Add(_btnRun);
        box.Controls.Add(row);

        var head = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 28), Padding = new Padding(10, 4, 10, 0) };
        head.Controls.Add(_fst);
        head.Controls.Add(_all);
        _all.Dock = DockStyle.Left;
        var fb = new Label { Text = "Fields", AutoSize = true, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(ThemeManager.BaseFont, FontStyle.Bold), Padding = new Padding(0, 0, 10, 0) };
        head.Controls.Add(fb);
        var fieldsHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 2, 10, 10) };
        fieldsHost.Controls.Add(_fields);

        Controls.Add(fieldsHost); Controls.Add(head); Controls.Add(box);

        foreach (var c in new[] { _select, _from, _where, _orderBy, _top })
            c.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; if (c == _from) NotifyFrom(); DoRun(); } };
        _from.Inner.Leave += (_, _) => NotifyFrom();
        _from.TextChanged += (_, _) => { _fromTimer.Stop(); _fromTimer.Start(); };
        _fromTimer.Tick += (_, _) => { _fromTimer.Stop(); NotifyFrom(); };
        _fields.ItemCheck += (_, _) => { if (!_rebuilding) BeginInvoke(new Action(RebuildSelect)); };
        _all.CheckedChanged += (_, _) => { if (_rebuilding) return; _rebuilding = true; for (var i = 0; i < _fields.Items.Count; i++) _fields.SetItemChecked(i, _all.Checked); _rebuilding = false; RebuildSelect(); };
        Disposed += (_, _) => _fromTimer.Dispose();
    }

    private void NotifyFrom()
    {
        var f = _from.Text.Trim();
        if (f == _lastFrom) return;
        _lastFrom = f;
        FromChanged?.Invoke(f);
    }

    private void DoRun() => RunRequested?.Invoke(_select.Text, _from.Text, _where.Text, _orderBy.Text, _top.Text);

    private void RebuildSelect()
    {
        var names = _fields.CheckedItems.Cast<FieldItem>().Select(f => f.Name).ToList();
        _select.Text = names.Count > 0 ? string.Join(", ", names.Select(n => "[" + n + "]")) : "*";
        _rebuilding = true;
        _all.Checked = _fields.Items.Count > 0 && names.Count == _fields.Items.Count;
        _rebuilding = false;
    }

    private sealed record FieldItem(string Name, bool Pk) { public override string ToString() => Pk ? Name + "  (PK)" : Name; }

    public void Init(int maxRows) => _top.Text = maxRows.ToString();

    /// <summary>Đặt ô FROM (vd mở từ menu WCommand) và báo để nạp danh sách cột.</summary>
    public void SetFrom(string text) { _from.Text = text; _lastFrom = text.Trim(); FromChanged?.Invoke(_lastFrom); }

    public void SetWhere(string text) => _where.Text = text;

    public void SetSuggestions(IEnumerable<string> names)
    {
        var src = new AutoCompleteStringCollection();
        src.AddRange(names.ToArray());
        _from.Inner.AutoCompleteCustomSource = src;
        _from.Inner.AutoCompleteSource = AutoCompleteSource.CustomSource;
        _from.Inner.AutoCompleteMode = AutoCompleteMode.Suggest;
    }

    public void SetFields(string status, IEnumerable<(string Name, bool Pk)> columns)
    {
        _fst.Text = status;
        _rebuilding = true;
        _fields.BeginUpdate();
        _fields.Items.Clear();
        foreach (var (n, pk) in columns) _fields.Items.Add(new FieldItem(n, pk));
        _fields.EndUpdate();
        _all.Checked = false;
        _rebuilding = false;
    }

    public void SetBusy(bool busy) { _btnRun.Enabled = !busy; _btnAdd.Enabled = !busy; }
}
