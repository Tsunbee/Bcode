using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Ô chọn thả xuống bo tròn của chế độ App: thay <see cref="ComboBox"/> (luôn vuông, nền trắng của hệ điều hành, không theo theme) bằng 1 nút
/// <see cref="PillButton"/> mở menu <see cref="WebMenu"/> (cùng kiểu menu với mọi nơi khác) — nên đổi theme sáng/tối là đổi theo ngay.
/// </summary>
public sealed class DropDownButton : PillButton
{
    private IReadOnlyList<string> _items = Array.Empty<string>();
    private int _selected = -1;

    public event Action<int>? SelectionChanged;
    /// <summary>Số chữ hiện trên nút tối đa (cắt "…"); 0 = theo bề rộng nút.</summary>
    public int Count => _items.Count;
    public int SelectedIndex => _selected;

    public DropDownButton()
    {
        CornerRadius = 8;
        LeftAlign = true;
        Chevron = true;
        AutoWidth = false;
    }

    /// <summary>Đặt danh sách + mục đang chọn mà KHÔNG bắn <see cref="SelectionChanged"/>.</summary>
    public void SetItems(IReadOnlyList<string> items, int selected)
    {
        _items = items;
        Select(selected, raise: false);
    }

    public void Select(int index, bool raise)
    {
        _selected = index >= 0 && index < _items.Count ? index : (_items.Count > 0 ? 0 : -1);
        Text = _selected >= 0 ? _items[_selected] : "";
        if (raise && _selected >= 0) SelectionChanged?.Invoke(_selected);
    }

    /// <summary>Danh sách dài hơn số mục này thì mở dạng khung danh sách có thanh cuộn (như &lt;select&gt; của trang web) thay vì menu cao hết màn hình.</summary>
    private const int MenuMaxItems = 12, ListVisibleRows = 18;
    private DateTime _listClosedAt;

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (_items.Count == 0 || WebMenu.JustDismissed) return;   // bấm lần nữa vào nút khi menu đang mở = đóng menu
        if ((DateTime.Now - _listClosedAt).TotalMilliseconds < 300) return;   // cú bấm vừa đóng khung danh sách (bấm lại vào nút) — không mở lại ngay
        if (_items.Count > MenuMaxItems) { ShowList(); return; }
        var menu = new WebMenu();
        for (var i = 0; i < _items.Count; i++)
        {
            var idx = i;
            menu.Add(_items[i], () => Select(idx, raise: true), @checked: idx == _selected);
        }
        menu.Show(this, 0, Height + 2);
    }

    /// <summary>Khung danh sách thả xuống: cao tối đa <see cref="ListVisibleRows"/> dòng, thanh cuộn theo theme, lăn chuột / ↑ ↓ / Enter / Esc,
    /// mục đang chọn tô màu nhấn và được cuộn vào giữa. Dựng mỗi lần mở, đóng là giải phóng (không giữ RAM).</summary>
    private void ShowList()
    {
        var list = new ThemedListBox { Font = Font, BackColor = AppColors.Input, ForeColor = AppColors.Text };
        list.BeginUpdate();
        foreach (var item in _items) list.Items.Add(item);
        list.EndUpdate();
        NK.DarkScroll(list);

        const TextFormatFlags one = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var widest = _items.Max(t => TextRenderer.MeasureText(t, Font, Size.Empty, one).Width);
        var screen = Screen.FromControl(this).WorkingArea;
        var rows = Math.Min(_items.Count, ListVisibleRows);
        list.Size = new Size(Math.Min(screen.Width / 2, Math.Max(Width - 2, widest + NativeSkin.S(24) + SystemInformation.VerticalScrollBarWidth)), rows * list.ItemHeight);

        var host = new ToolStripControlHost(list) { AutoSize = false, Size = list.Size, Margin = Padding.Empty, Padding = Padding.Empty };
        var drop = new ToolStripDropDown { Padding = new Padding(1), AutoSize = true, BackColor = AppColors.Border, DropShadowEnabled = true };   // nền viền 1px quanh danh sách
        drop.Items.Add(host);

        void Choose(int index) { drop.Close(ToolStripDropDownCloseReason.ItemClicked); if (index >= 0) Select(index, raise: true); }
        list.MouseClick += (_, e) => Choose(list.IndexFromPoint(e.Location));
        list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Choose(list.SelectedIndex); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = e.SuppressKeyPress = true; drop.Close(ToolStripDropDownCloseReason.Keyboard); }
        };
        drop.Closed += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.AppClicked) _listClosedAt = DateTime.Now;
            BeginInvoke(new Action(drop.Dispose));   // giải phóng sau khi sự kiện đóng chạy xong
        };

        if (_selected >= 0) list.SelectedIndex = _selected;
        drop.Show(this, new Point(0, Height + 2));
        if (_selected >= 0) list.TopIndex = Math.Max(0, _selected - rows / 2);   // sau khi hiện: ListBox đã tự cuộn tới mục chọn (đáy), đưa nó về giữa
        list.Focus();
    }
}

/// <summary>
/// Ô nhập 1 dòng bo tròn (viền theo theme, viền nhấn màu khi đang gõ): TextBox thật nằm trong 1 khung tự vẽ — TextBox của WinForms không bo góc được
/// và ThemeManager luôn đặt viền vuông cho nó, nên ở đây ép nó về không viền và để khung vẽ viền.
/// </summary>
public sealed class RoundedInput : Panel
{
    private readonly TextBox _tb = new() { BorderStyle = BorderStyle.None };
    public TextBox Inner => _tb;

    public string PlaceholderText { get => _tb.PlaceholderText; set => _tb.PlaceholderText = value; }
    public override string Text { get => _tb.Text; set => _tb.Text = value; }
    private bool _multi;
    /// <summary>Ô nhiều dòng (ô ghi chú / script): cao theo Height do bên dùng đặt, có thanh cuộn dọc.</summary>
    public bool Multiline
    {
        get => _multi;
        set { _multi = value; _tb.Multiline = value; _tb.AcceptsReturn = value; _tb.ScrollBars = value ? ScrollBars.Vertical : ScrollBars.None; _tb.WordWrap = !value || _wrap; PerformLayout(); }
    }
    private bool _wrap = true;
    /// <summary>Chữ đơn cách (Consolas) cho ô script / đường dẫn.</summary>
    public bool Mono { set { _tb.Font = value ? ThemeManager.MonoFont : ThemeManager.BaseFont; PerformLayout(); } }
    public bool ReadOnly { get => _tb.ReadOnly; set => _tb.ReadOnly = value; }
    public event KeyEventHandler? InputKeyDown;

    public RoundedInput()
    {
        DoubleBuffered = true;
        Padding = new Padding(9, 0, 9, 0);
        _tb.BorderStyleChanged += (_, _) => { if (_tb.BorderStyle != BorderStyle.None) _tb.BorderStyle = BorderStyle.None; };   // ThemeManager.Apply đặt FixedSingle
        _tb.TextChanged += (_, e) => OnTextChanged(e);
        _tb.KeyDown += (_, e) => InputKeyDown?.Invoke(this, e);
        _tb.GotFocus += (_, _) => Invalidate();
        _tb.LostFocus += (_, _) => Invalidate();
        Controls.Add(_tb);
        NK.DarkScroll(_tb);   // thanh cuộn của ô nhiều dòng theo theme (không còn thanh trắng trên nền tối)
        Click += (_, _) => _tb.Focus();
        ThemeManager.ThemeChanged += Recolor;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= Recolor;
        Recolor();
    }

    private void Recolor() { _tb.BackColor = AppColors.Input; _tb.ForeColor = AppColors.Text; Invalidate(); }

    /// <summary>Chiều cao tự nhiên của ô 1 dòng (theo font hiện tại) — hàng [ô + nút] dùng để cao bằng các ô nhập khác.</summary>
    public int NaturalHeight { get { var h = _tb.PreferredHeight; return h + Math.Max(8, (int)(h * 0.55)); } }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        _tb.BackColor = AppColors.Input; _tb.ForeColor = AppColors.Text;
        if (_multi) { _tb.SetBounds(Padding.Left, 6, Math.Max(10, Width - Padding.Horizontal), Math.Max(10, Height - 12)); return; }
        var h = _tb.PreferredHeight;
        var want = h + Math.Max(8, (int)(h * 0.55));
        if (Dock is DockStyle.Top or DockStyle.Bottom or DockStyle.None && Height != want) { Height = want; return; }
        _tb.SetBounds(Padding.Left, (Height - h) / 2, Math.Max(10, Width - Padding.Horizontal), h);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var under = new SolidBrush(Parent?.BackColor ?? AppColors.Background)) g.FillRectangle(under, ClientRectangle);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = FlatToolStripRenderer.RoundedRect(r, Math.Min(NativeSkin.InputRadius(this), (Math.Min(Height, Width) - 2) / 2));
        using (var fill = new SolidBrush(AppColors.Input)) g.FillPath(fill, path);
        using var pen = new Pen(_tb.Focused ? AppColors.Accent : AppColors.Border, NativeSkin.BorderWidth(this));
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// ListBox tự vẽ KHÔNG nhấp nháy: (1) rê chuột chỉ vẽ lại 2 dòng (dòng cũ / dòng mới) thay vì cả danh sách; (2) chặn WM_ERASEBKGND — các dòng tự tô kín nền
/// trong OnDrawItem nên Windows không cần xoá trắng trước (chính bước xoá trắng đó gây chớp), chỉ tô phần trống dưới dòng cuối;
/// (3) <see cref="SetItemsSmooth"/> bỏ qua nếu danh sách không đổi và giữ nguyên vị trí cuộn khi phải nạp lại.
/// </summary>
public abstract class SmoothListBox : ListBox
{
    protected int Hot = -1;

    protected SmoothListBox()
    {
        DrawMode = DrawMode.OwnerDrawFixed;
        IntegralHeight = false;
        BorderStyle = BorderStyle.None;
    }

    // KHÔNG dùng WS_EX_COMPOSITED cho ListBox tự vẽ: thử rồi — treo vòng vẽ và làm Bcode báo "Error creating window handle" khi mở Lookup.
    // Thay vào đó mỗi dòng vẽ vào bộ đệm ngoài màn hình rồi đưa lên 1 lần (xem OnDrawItem) — không còn khoảnh khắc "nền đã tô, chữ chưa vẽ".

    /// <summary>Vẽ 1 dòng — lớp con vẽ vào <paramref name="e"/>.Graphics (là bộ đệm, không phải màn hình).</summary>
    protected abstract void DrawRow(DrawItemEventArgs e);

    // Ảnh đệm 1 dòng, dùng lại giữa các lần vẽ. KHÔNG dùng BufferedGraphics.Allocate(g, bounds): nó dịch toạ độ bằng Transform của GDI+ mà
    // TextRenderer (GDI) bỏ qua → chữ của mọi dòng trừ dòng đầu bị vẽ ra ngoài bộ đệm (danh sách Lookup mất tên object, chỉ còn nền).
    private Bitmap? _rowBuf;

    protected sealed override void OnDrawItem(DrawItemEventArgs e)
    {
        var w = e.Bounds.Width; var h = e.Bounds.Height;
        if (w <= 0 || h <= 0) return;
        if (_rowBuf is null || _rowBuf.Width < w || _rowBuf.Height < h)
        {
            // Đo cỡ mới TRƯỚC khi Dispose: đọc Width/Height của Bitmap đã Dispose → GDI+ ném "Parameter is not valid" (kéo danh sách to ra là gặp).
            var nw = Math.Max(w, _rowBuf?.Width ?? 0); var nh = Math.Max(h, _rowBuf?.Height ?? 0);
            _rowBuf?.Dispose();
            // 24-bit, KHÔNG kênh alpha: GDI (TextRenderer) vẽ chữ lên ảnh ARGB làm hỏng alpha → chữ đậm nhoè, lốm đốm (rõ nhất ở dòng đang chọn).
            // Dòng luôn tô kín nền trước khi vẽ chữ nên không cần trong suốt.
            _rowBuf = new Bitmap(nw, nh, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        }
        using (var g = Graphics.FromImage(_rowBuf))
            DrawRow(new DrawItemEventArgs(g, e.Font, new Rectangle(0, 0, w, h), e.Index, e.State, e.ForeColor, e.BackColor));   // vẽ ở gốc (0,0)
        e.Graphics.DrawImage(_rowBuf, e.Bounds, new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);   // đưa lên màn hình 1 lần
    }

    protected override void Dispose(bool disposing) { if (disposing) { _rowBuf?.Dispose(); _rowBuf = null; } base.Dispose(disposing); }

    public void InvalidateItem(int index)
    {
        if (index >= 0 && index < Items.Count) Invalidate(GetItemRectangle(index));
    }

    // ListBox của Windows khi rộng ra chỉ vẽ phần mới lộ ra; dòng tự vẽ có chữ canh phải / cắt "…" theo bề ngang (vd kiểu cột ở khung Structure)
    // nên phần cũ còn chữ vẽ theo bề ngang cũ (2 cột kiểu chồng nhau) tới khi rê chuột qua. Đổi bề ngang → vẽ lại cả danh sách.
    private int _lastWidth = -1;

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width != _lastWidth) { _lastWidth = Width; Invalidate(); }
    }

    /// <summary>Nạp danh sách mới: giống hệt danh sách đang có (cùng phần tử, cùng thứ tự) thì chỉ vẽ lại; khác thì nạp lại nhưng giữ dòng đầu đang hiện.</summary>
    public void SetItemsSmooth<T>(IReadOnlyList<T> list) where T : notnull
    {
        if (Items.Count == list.Count)
        {
            var same = true;
            for (var i = 0; i < list.Count && same; i++) same = Equals(Items[i], list[i]);
            if (same) { Invalidate(); return; }
        }
        var top = TopIndex;
        BeginUpdate();
        Items.Clear();
        foreach (var x in list) Items.Add(x);
        if (Items.Count > 0) TopIndex = Math.Min(top, Items.Count - 1);
        EndUpdate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var i = IndexFromPoint(e.Location);
        if (i == Hot) return;
        var old = Hot; Hot = i;
        InvalidateItem(old); InvalidateItem(i);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (Hot == -1) return;
        var old = Hot; Hot = -1; InvalidateItem(old);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_ERASEBKGND = 0x14;
        if (m.Msg == WM_ERASEBKGND)
        {
            // Chỉ tô vùng trống bên dưới dòng cuối (các dòng tự tô nền khi vẽ).
            using (var g = Graphics.FromHdc(m.WParam))
            {
                var bottom = Items.Count > 0 ? GetItemRectangle(Items.Count - 1).Bottom : 0;
                if (bottom < ClientSize.Height) using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, Math.Max(0, bottom), ClientSize.Width, ClientSize.Height - Math.Max(0, bottom));
            }
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>ListBox vẽ tay theo theme: dòng chọn dùng màu nhấn (như .it.sel của trang web) thay cho màu xanh hệ thống.</summary>
public sealed class ThemedListBox : SmoothListBox
{
    public ThemedListBox()
    {
        FontChanged += (_, _) => ItemHeight = Math.Max(16, Font.Height + UI.UiTemplate.Dens(6));
        ItemHeight = Math.Max(16, Font.Height + UI.UiTemplate.Dens(6));
    }

    protected override void DrawRow(DrawItemEventArgs e)
    {
        var sel = (e.State & DrawItemState.Selected) != 0;
        var hot = !sel && e.Index == Hot;
        var back = sel ? UI.AppColors.Accent : hot ? UI.AppColors.Selection : UI.AppColors.Input;
        using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);
        if (e.Index < 0 || e.Index >= Items.Count) return;
        var rect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        // Truyền màu nền: GDI vẽ chữ ở chế độ nền đặc nên làm mịn ClearType đúng — không truyền thì trên ảnh đệm chữ tối / nền sáng (dòng chọn) bị đặc, nhoè.
        TextRenderer.DrawText(e.Graphics, Items[e.Index]?.ToString() ?? "", Font, rect, sel ? UI.AppColors.OnAccent : UI.AppColors.Text, back,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

}
