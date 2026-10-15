using System.Text.RegularExpressions;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Thanh công cụ của tab "Table" ở chế độ App/WinForms — thay trang WebView2 tablebar.html: DB, Table, Fields (hàng 1), Where, Order (hàng 2) và khối nút Top / Load / Save / Add Script.
/// Giao tiếp với <see cref="TableEditControl"/> bằng đúng các "thông điệp" của trang web (<see cref="Action"/> nhận đối tượng {action, ...} cùng tên trường), nên toàn bộ phần xử lý
/// (tải bảng, gợi ý tên bảng / tên cột bằng SuggestPopup, lưu, Add Script) dùng chung giữa hai chế độ. Toạ độ ô nhập gửi kèm là pixel thiết bị, tính từ góc trái-trên của thanh này.
/// </summary>
public sealed class NativeTableBar : Panel
{
    private readonly DropDownButton _db = new();
    private readonly RoundedInput _table = new() { PlaceholderText = "dbo.tenbang" };
    private readonly RoundedInput _fields = new() { Text = "*", PlaceholderText = "*, ma_kh, ten_kh..." };
    private readonly RoundedInput _where = new() { PlaceholderText = "ma_kh like 'KH%' and status = '1'" };
    private readonly RoundedInput _order = new() { PlaceholderText = "ngay_ct desc" };
    private readonly RoundedInput _top = new() { Text = "0", PlaceholderText = "0=tất cả" };
    private readonly PillButton _btnLoad, _btnSave, _btnAdd;
    private bool _suggestOpen, _colSuggestOpen;
    private bool _programmatic;   // đang đặt chữ từ code (chủ gọi xuống) — không coi là người dùng gõ, khỏi bật lại gợi ý
    private RoundedInput? _colField;
    private int _tokenStart, _tokenEnd;

    /// <summary>Thông điệp gửi về chủ: {action = "db"|"load"|"table-input"|"table-key"|"table-blur"|"col-input"|"col-key"|"col-blur"|"save"|"add-script", ...}.</summary>
    public event Action<object>? Action;

    public NativeTableBar()
    {
        Dock = DockStyle.Top;
        DoubleBuffered = true;
        Padding = new Padding(10, 6, 10, 6);

        _db.SetItems(new[] { "App Data", "Sys Data" }, 0);
        _db.Dock = DockStyle.Fill;
        _db.SelectionChanged += i => Action?.Invoke(new { action = "db", value = i });

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        Label L(string t) => new MutedLabel { Text = t, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 0, 4, 0), BackColor = Color.Transparent };
        foreach (var x in new Control[] { _db, _table, _fields, _where, _order }) { x.Dock = DockStyle.Fill; x.Margin = new Padding(0, 2, 8, 2); }
        grid.Controls.Add(L("DB:"), 0, 0); grid.Controls.Add(_db, 1, 0);
        grid.Controls.Add(L("Table:"), 2, 0); grid.Controls.Add(_table, 3, 0);
        grid.Controls.Add(L("Fields:"), 4, 0); grid.Controls.Add(_fields, 5, 0);
        grid.Controls.Add(L("Where:"), 0, 1); grid.Controls.Add(_where, 1, 1); grid.SetColumnSpan(_where, 3);
        grid.Controls.Add(L("Order:"), 4, 1); grid.Controls.Add(_order, 5, 1);

        _btnLoad = PillButton.Flat("Load", primary: true); _btnSave = PillButton.Flat("💾 Save (ghi vào DB)"); _btnAdd = PillButton.Flat("Add Script");
        var actions = new TableLayoutPanel { Dock = DockStyle.Right, ColumnCount = 2, RowCount = 2, AutoSize = true, Padding = new Padding(8, 0, 0, 0) };
        _top.Width = NativeSkin.RowHeight(this, 80); _top.Dock = DockStyle.Fill;
        var topBox = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 6, 2) };
        topBox.Controls.Add(new MutedLabel { Text = "Top:", AutoSize = true, Margin = new Padding(0, 6, 4, 0), BackColor = Color.Transparent });
        _top.Dock = DockStyle.None; topBox.Controls.Add(_top);
        actions.Controls.Add(topBox, 0, 0); actions.Controls.Add(_btnLoad, 1, 0);
        actions.Controls.Add(_btnSave, 0, 1); actions.Controls.Add(_btnAdd, 1, 1);
        foreach (var b in new[] { _btnLoad, _btnSave, _btnAdd }) { b.Dock = DockStyle.Fill; b.Margin = new Padding(0, 2, 6, 2); b.AutoWidth = false; }
        _btnLoad.Width = NativeSkin.RowHeight(this, 90); _btnAdd.Width = NativeSkin.RowHeight(this, 90); _btnSave.Width = NativeSkin.RowHeight(this, 170);
        _btnLoad.Click += (_, _) => TriggerLoad();
        _btnSave.Click += (_, _) => Action?.Invoke(new { action = "save" });
        _btnAdd.Click += (_, _) => Action?.Invoke(new { action = "add-script" });

        Controls.Add(grid);
        Controls.Add(actions);
        Height = NativeSkin.RowHeight(this, 84);

        // ---- ô Table: gợi ý tên bảng ----
        _table.TextChanged += (_, _) => { if (_programmatic) return; var r = Rect(_table); Action?.Invoke(new { action = "table-input", value = _table.Text, x = (double)r.Left, y = (double)r.Top, w = (double)r.Width, h = (double)r.Height }); };
        _table.InputKeyDown += (_, e) => OnKey(e, _suggestOpen, "table-key", () => TriggerLoad(), isTable: true);
        _table.Inner.Leave += (_, _) => { _suggestOpen = false; Action?.Invoke(new { action = "table-blur" }); };

        // ---- Fields / Where / Order / Top: gợi ý tên cột ----
        foreach (var f in new[] { _fields, _where, _order, _top })
        {
            var field = f;
            field.InputKeyDown += (_, e) => OnKey(e, _colSuggestOpen && _colField == field, "col-key", () => TriggerLoad(), isTable: false);
            if (field == _top) continue;
            field.TextChanged += (_, _) => { if (!_programmatic) OnColInput(field); };
            field.Inner.Leave += (_, _) => { _colSuggestOpen = false; Action?.Invoke(new { action = "col-blur" }); };
        }
    }

    private Rectangle Rect(Control c) => RectangleToClient(c.RectangleToScreen(c.ClientRectangle));

    private void OnKey(KeyEventArgs e, bool popupOpen, string action, System.Action load, bool isTable)
    {
        if (popupOpen)
        {
            string? key = e.KeyCode switch
            {
                Keys.Down => "down", Keys.Up => "up", Keys.PageDown => "pagedown", Keys.PageUp => "pageup", Keys.Enter => "enter", Keys.Tab => "enter", Keys.Escape => "escape", _ => null,
            };
            if (key is not null)
            {
                if (e.KeyCode != Keys.Tab) e.Handled = e.SuppressKeyPress = true;   // Tab: chọn gợi ý rồi vẫn để Tab nhảy ô
                Action?.Invoke(new { action, key });
                return;
            }
        }
        if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; load(); }
        else if (e.KeyCode == Keys.F5 && !e.Control && !e.Shift && !e.Alt) { e.Handled = e.SuppressKeyPress = true; load(); }
    }

    private static readonly Regex Token = new(@"[A-Za-z_][\w$]*$", RegexOptions.Compiled);

    private void OnColInput(RoundedInput input)
    {
        var tb = input.Inner;
        var pos = tb.SelectionStart;
        var before = tb.Text[..Math.Min(pos, tb.TextLength)];
        if (before.Count(c => c == '\'') % 2 == 1) { HideCol(); return; }   // đang trong chuỗi '...' thì không gợi ý
        var m = Token.Match(before);
        if (!m.Success || (before.Length > m.Length && before[before.Length - m.Length - 1] is '@' or '.')) { HideCol(); return; }
        _colField = input; _tokenStart = pos - m.Length; _tokenEnd = pos;
        var r = Rect(input);
        var caret = tb.GetPositionFromCharIndex(Math.Max(0, _tokenStart));
        var x = Math.Min(Math.Max(r.Left + input.Padding.Left + caret.X, r.Left), r.Right - 60);
        Action?.Invoke(new { action = "col-input", term = m.Value, x = (double)x, y = (double)r.Top, w = (double)Math.Max(r.Width / 2, NativeSkin.RowHeight(this, 260)), h = (double)r.Height });
    }

    private void HideCol() { if (_colSuggestOpen) Action?.Invoke(new { action = "col-blur" }); _colSuggestOpen = false; }

    public void TriggerLoad() =>
        Action?.Invoke(new { action = "load", table = _table.Text, fields = _fields.Text, where = _where.Text, order = _order.Text, top = _top.Text });

    // ---- chủ gọi xuống (tương đương window.* của trang web) ----
    public void SetDatabase(int index) => _db.Select(index, raise: false);
    public void SetTable(string t) { _programmatic = true; try { _table.Text = t; } finally { _programmatic = false; } }
    public void SetFields(string t) { _programmatic = true; try { _fields.Text = t; } finally { _programmatic = false; } }
    public void SetWhere(string t) { _programmatic = true; try { _where.Text = t; } finally { _programmatic = false; } }
    public void SetOrder(string t) { _programmatic = true; try { _order.Text = t; } finally { _programmatic = false; } }
    public void SetTop(string t) { _programmatic = true; try { _top.Text = t; } finally { _programmatic = false; } }
    public void SetSuggestOpen(bool open) => _suggestOpen = open;
    public void SetColSuggestOpen(bool open) => _colSuggestOpen = open;
    public void FocusTable() { _table.Inner.Focus(); _table.Inner.SelectAll(); }

    /// <summary>Thay chữ đang gõ (vị trí đã nhớ lúc hiện gợi ý) bằng tên cột được chọn.</summary>
    public void InsertColumn(string name)
    {
        if (_colField is null) return;
        var tb = _colField.Inner;
        var v = tb.Text;
        var s = Math.Min(_tokenStart, v.Length); var e = Math.Min(_tokenEnd, v.Length);
        _programmatic = true; try { tb.Text = v[..s] + name + v[e..]; } finally { _programmatic = false; }
        tb.Focus();
        tb.SelectionStart = s + name.Length; tb.SelectionLength = 0;
        _colSuggestOpen = false;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var b = new SolidBrush(AppColors.PanelAlt);
        e.Graphics.FillRectangle(b, ClientRectangle);
        using var p = new Pen(AppColors.Border);
        e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
    }
}
