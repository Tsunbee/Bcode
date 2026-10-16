using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Write Schema → cập nhật bảng trong .rpt (rptschema.html, Forms/RptSchemaForm): ô file .rpt + Chọn..., mỗi bảng 1 hàng
/// (tên bảng rpt | chọn bảng xsd | khác biệt cột tô màu), tuỳ chọn subreport, nút Cập nhật .rpt.</summary>
internal sealed class RptSchemaPage : DialogPage
{
    private readonly Label _head = NK.Muted("", wrap: true);
    private readonly RoundedInput _rpt = NK.Input("Gõ tên để tìm file .rpt — hoặc dán đường dẫn, Enter để đọc", mono: true);
    private readonly Label _dir = NK.Muted("");
    // Danh sách gợi ý file .rpt (C# lọc sẵn — rptSchema.matches). Ẩn bằng chiều cao 0 thay vì Visible: đổi Visible làm đảo thứ tự các khối Dock=Top.
    private readonly Panel _listHost = new() { Dock = DockStyle.Top, Height = 0, Padding = new Padding(0, 0, 0, 6) };
    private readonly ThemedListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly Label _listNote = NK.Muted("");
    private List<(string Name, string Path)> _items = new();
    private bool _settingText;
    private readonly PillButton _pick, _ok;
    private readonly TableLayoutPanel _grid = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3 };
    private readonly CheckBox _sub = NK.Check("");
    private readonly Panel _scroll = new() { AutoScroll = true, Padding = new Padding(14) };
    private readonly List<(string Key, DropDownButton Drop, List<(string Value, string Note, string Level)> Options)> _rows = new();
    private bool _busy;
    private Font? _bold;

    public RptSchemaPage()
    {
        _pick = NK.Button("Chọn...", () => { if (!_busy) Post(new { action = "pick" }); });
        _ok = NK.Button("Cập nhật .rpt", Apply, primary: true);
        _ok.Enabled = false;
        _rpt.InputKeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Down or Keys.Up)
            {
                e.SuppressKeyPress = true;
                if (!ListOpen) { Search(); return; }
                if (_items.Count > 0) _list.SelectedIndex = (_list.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1) + _items.Count) % _items.Count;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                if (ListOpen && _list.SelectedIndex >= 0) PickItem(_list.SelectedIndex);
                else if (!_busy) { OpenList(false); Post(new { action = "load", rpt = _rpt.Text }); }
            }
        };
        _rpt.Inner.TextChanged += (_, _) => { if (!_settingText) Search(); };
        _rpt.Inner.GotFocus += (_, _) => { BeginInvoke(new Action(_rpt.Inner.SelectAll)); Search(); };
        _rpt.Inner.LostFocus += (_, _) => BeginInvoke(new Action(() => { if (!_list.Focused) OpenList(false); }));
        _list.MouseClick += (_, e) => { var i = _list.IndexFromPoint(e.Location); if (i >= 0) PickItem(i); };
        _list.LostFocus += (_, _) => BeginInvoke(new Action(() => { if (!_rpt.Inner.Focused) OpenList(false); }));
        _listNote.Dock = DockStyle.Bottom; _listNote.Padding = new Padding(4, 2, 0, 0);
        _listHost.Controls.Add(_list); _listHost.Controls.Add(_listNote);
        NK.DarkScroll(_list);
        _dir.Dock = DockStyle.Top; _dir.Margin = Padding.Empty; _dir.Padding = new Padding(0, 0, 0, 6);

        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, NativeSkin.S(170)));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, NativeSkin.S(200)));
        _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _sub.Checked = true; _sub.Visible = false; _sub.Dock = DockStyle.Top; _sub.AutoSize = true; _sub.Margin = new Padding(0, 8, 0, 0);

        var fileRow = NK.Padded(NK.GridRow(8, (NK.Text("File .rpt"), 0), (_rpt, 1), (_pick, 0)), 0, 8, 0, 8);
        fileRow.Dock = DockStyle.Top; _head.Dock = DockStyle.Top;
        _scroll.Controls.Add(_sub); _scroll.Controls.Add(_grid); _scroll.Controls.Add(_listHost); _scroll.Controls.Add(_dir); _scroll.Controls.Add(fileRow); _scroll.Controls.Add(_head);

        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(_ok); Bottom.Controls.Add(NK.Button("Huỷ", () => Post(new { action = "cancel" })));
        Layout(null, _scroll);
    }

    private void Apply()
    {
        if (_busy || _rows.Count == 0) return;
        Post(new { action = "apply", choices = _rows.ToDictionary(r => r.Key, r => r.Options[Math.Max(0, r.Drop.SelectedIndex)].Value), subreports = _sub.Visible && _sub.Checked });
    }

    protected override bool HandleKey(Keys k)
    {
        if (k != Keys.Escape) return false;
        if (ListOpen) OpenList(false); else Post(new { action = "cancel" });   // Esc lần đầu đóng danh sách gợi ý
        return true;
    }

    private bool ListOpen => _listHost.Height > 0;

    private static bool IsPath(string v) => v.Contains('\\') || v.Contains(':');

    private void Search()
    {
        var q = IsPath(_rpt.Text) ? "" : _rpt.Text;
        Post(new { action = "search", q });
        OpenList(true);
    }

    private void OpenList(bool open)
    {
        var rows = Math.Clamp(_items.Count, 1, 10);
        var h = open ? rows * _list.ItemHeight + _listNote.PreferredHeight + _listHost.Padding.Vertical + 2 : 0;
        if (_listHost.Height != h) _listHost.Height = h;
    }

    private void PickItem(int i)
    {
        if (_busy || i < 0 || i >= _items.Count) return;
        SetText(_items[i].Path);
        OpenList(false);
        _ok.Focus();   // không trả focus về ô tìm: GotFocus sẽ bung lại danh sách
        Post(new { action = "load", rpt = _items[i].Path });
    }

    private void SetText(string text) { _settingText = true; try { _rpt.Text = text; } finally { _settingText = false; } }

    private void ShowMatches(JsonElement d)
    {
        _items = PA(d, "items").Select(x => (P(x, "name"), P(x, "path"))).ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var it in _items) _list.Items.Add(it.Name);
        _list.EndUpdate();
        if (_items.Count > 0) _list.SelectedIndex = 0;
        _listNote.Text = P(d, "note");
        if (ListOpen) OpenList(true);   // cao theo số dòng mới
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        var d = Arg(a, 0);
        switch (path)
        {
            case "rptSchema.init":
                _head.Text = $"Đã ghi {P(d, "xsd")} — {string.Join(", ", PA(d, "xsdTables").Select(x => x.GetString()))}.\r\n" +
                             "Chọn file .rpt; mỗi bảng của report lấy nguồn từ bảng nào của xsd (như Set Datasource Location + Verify Database). Bản gốc được sao lưu thành .rpt.bak.";
                SetText(P(d, "rpt"));
                _dir.Text = "Tìm trong: " + P(d, "dir");
                if (_rpt.Text.Length > 0) { OpenList(false); _ok.Focus(); }
                break;
            case "rptSchema.setRpt": SetText(Str(a, 0)); break;
            case "rptSchema.matches": ShowMatches(d); break;
            case "rptSchema.load": ShowRows(d); break;
            case "rptSchema.busy":
                _busy = Bool(a, 0); _ok.Enabled = !_busy && _rows.Count > 0; _pick.Enabled = !_busy;
                break;
            case "rptSchema.status": Say(Str(a, 0), Str(a, 1) is { Length: > 0 } kind ? kind : null); break;
        }
    }

    private void ShowRows(JsonElement d)
    {
        _grid.SuspendLayout(); _grid.Controls.Clear(); _grid.RowStyles.Clear(); _rows.Clear();
        var notes = new List<Action>();
        if (d.ValueKind == JsonValueKind.Object)
        {
            foreach (var h in new[] { "Bảng trong rpt", "Lấy từ", "Khác biệt cột" }) _grid.Controls.Add(NK.Muted(h));
            foreach (var r in PA(d, "rows"))
            {
                var options = PA(r, "options").Select(o => (P(o, "value"), P(o, "note"), P(o, "level"))).ToList();
                var name = new Label { Text = P(r, "label") + "\r\n" + P(r, "sub"), AutoSize = true, BackColor = Color.Transparent, UseMnemonic = false, Margin = new Padding(0, 4, 8, 4) };
                NK.Tip(name, P(r, "tip"));
                var drop = new DropDownButton { Width = NativeSkin.S(190), Margin = new Padding(0, 4, 8, 4) };
                drop.SetItems(PA(r, "options").Select(o => P(o, "text")).ToList(), Math.Max(0, options.FindIndex(o => o.Item1 == P(r, "selected"))));
                var note = new Label { AutoSize = true, BackColor = Color.Transparent, UseMnemonic = false, Margin = new Padding(0, 9, 0, 4), MaximumSize = new Size(NativeSkin.S(420), 0) };
                void ShowNote()
                {
                    var o = options[Math.Max(0, drop.SelectedIndex)];
                    note.Text = o.Item2;
                    note.ForeColor = o.Item3 switch { "err" => AppColors.Danger, "warn" => AppColors.Warning, "ok" => AppColors.Success, _ => AppColors.TextMuted };
                    note.Font = o.Item3 == "err" ? _bold ??= new Font(ThemeManager.BaseFont, FontStyle.Bold) : ThemeManager.BaseFont;
                }
                drop.SelectionChanged += _ => ShowNote();
                notes.Add(ShowNote);
                _rows.Add((P(r, "key"), drop, options));
                _grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                _grid.Controls.Add(name); _grid.Controls.Add(drop); _grid.Controls.Add(note);
            }
        }
        _grid.ResumeLayout(true);
        var subNote = P(d, "subNote");
        _sub.Text = subNote; _sub.Visible = subNote.Length > 0;
        _ok.Enabled = !_busy && _rows.Count > 0;
        ThemeManager.Apply(this);
        notes.ForEach(n => n());   // tô màu ghi chú SAU khi áp theme (Apply đặt lại ForeColor mọi Label)
    }

    // Đã gợi ý sẵn file .rpt thì không đặt con trỏ vào ô tìm (sẽ bung danh sách che bảng ghép).
    public override void FocusPrimary() { if (_rpt.Text.Length == 0) _rpt.Inner.Focus(); else _ok.Focus(); }
}
