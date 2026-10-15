using System.Data;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Khung "thẻ" của các tool native: viền bo tròn, thanh tiêu đề (tên + chú thích mờ + nút bên phải), thân tự cao theo nội dung. Dùng thay các <c>.card</c> của trang web.</summary>
public sealed class CardPanel : Panel
{
    private readonly Panel _head = new() { Dock = DockStyle.Top, Height = NativeSkin.S(36) };
    // UseMnemonic = false: tiêu đề như "Đường dẫn & kết nối" phải hiện đúng dấu & (mặc định Label coi & là phím tắt và ẩn nó).
    private readonly Label _title = new() { AutoSize = true, Left = 14, UseMnemonic = false };
    private readonly Label _hint = new MutedLabel { AutoSize = true, BackColor = Color.Transparent, UseMnemonic = false };
    private readonly FlowLayoutPanel _right = new() { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Padding = new Padding(0, 5, 10, 0) };
    private readonly Panel _body = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(14, 12, 14, 14) };

    public Panel Body => _body;
    public Label TitleLabel => _title;
    /// <summary>Hành động mở/đóng thân thẻ do bên dùng đặt (thẻ có thể thu gọn).</summary>
    public Action? OpenToggle { get; set; }
    public FlowLayoutPanel RightBar => _right;

    public CardPanel(string title, string? hint = null)
    {
        DoubleBuffered = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(1);
        Margin = new Padding(0, 0, 0, 10);
        _title.Text = title;
        _title.Font = ThemeManager.BoldFont;
        _title.BackColor = Color.Transparent;
        _hint.Text = hint ?? "";
        _head.Controls.Add(_title);
        _head.Controls.Add(_hint);
        _head.Controls.Add(_right);
        _head.Height = NativeSkin.RowHeight(this, 36);
        // Tiêu đề + chú thích canh giữa theo chiều dọc của thanh đầu thẻ (cao theo Template/DPI).
        // Chú thích chỉ chiếm phần còn trống tới cụm nút bên phải (thẻ hẹp → cắt "…", di chuột xem đủ) — trước đây chữ đè lên nút RightBar.
        _hint.AutoSize = false; _hint.AutoEllipsis = true;
        _title.AutoSize = false; _title.AutoEllipsis = true;
        _title.TextChanged += (_, _) => PlaceTitle();
        _title.FontChanged += (_, _) => PlaceTitle();
        _head.Resize += (_, _) => PlaceTitle();
        _right.SizeChanged += (_, _) => PlaceTitle();
        _right.LocationChanged += (_, _) => PlaceTitle();
        _right.ControlAdded += (_, e) => { e.Control!.VisibleChanged += (_, _) => PlaceTitle(); };
        _hint.TextChanged += (_, _) => PlaceTitle();
        Controls.Add(_body);
        Controls.Add(_head);
        ThemeManager.Subscribe(this, () => Invalidate(true));
    }

    public void SetHint(string text) { _hint.Text = text; PlaceTitle(); }

    private ToolTip? _hintTip;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hintTip?.Dispose();
        base.Dispose(disposing);
    }

    private void PlaceTitle()
    {
        const TextFormatFlags one = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        var rightEdge = _right.Controls.Cast<Control>().Any(c => c.Visible) ? _right.Left : _head.ClientSize.Width - 10;
        // Tiêu đề cũng không được chui dưới cụm nút: thẻ quá hẹp thì cắt "…" (di chuột xem đủ).
        var titlePref = TextRenderer.MeasureText(_title.Text, _title.Font, Size.Empty, one).Width + 4;
        var titleW = Math.Max(NativeSkin.S(40), Math.Min(titlePref, rightEdge - 8 - _title.Left));
        _title.SetBounds(_title.Left, (_head.Height - _title.PreferredHeight) / 2, titleW, _title.PreferredHeight);
        var pref = _hint.Text.Length == 0 ? 0 : TextRenderer.MeasureText(_hint.Text, _hint.Font, Size.Empty, one).Width + 4;
        var w = Math.Max(0, Math.Min(pref, rightEdge - 8 - (_title.Right + 10)));
        _hint.SetBounds(_title.Right + 10, (_head.Height - _hint.PreferredHeight) / 2, w, _hint.PreferredHeight);
        _hint.Visible = w > 24;
        var cut = w < pref; var titleCut = titleW < titlePref;
        if (cut || titleCut || _hintTip is not null)
        {
            _hintTip ??= new ToolTip();
            _hintTip.SetToolTip(_hint, cut ? _hint.Text : null);
            _hintTip.SetToolTip(_title, titleCut ? _title.Text : null);
        }
    }

    /// <summary>AutoSize chỉ quyết định CHIỀU CAO; chiều rộng do bố cục cha (Dock / bề ngang đã đặt) — tránh thẻ co lại thành dải hẹp.</summary>
    public override Size GetPreferredSize(Size proposedSize)
    {
        var s = base.GetPreferredSize(proposedSize);
        var w = Width > 60 ? Width : (proposedSize.Width > 60 && proposedSize.Width < int.MaxValue ? proposedSize.Width : s.Width);
        return new Size(w, s.Height);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var under = new SolidBrush(Parent?.BackColor ?? AppColors.Background)) g.FillRectangle(under, ClientRectangle);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = FlatToolStripRenderer.RoundedRect(r, Math.Min(NativeSkin.CardRadius(this), 14));
        using (var fill = new SolidBrush(AppColors.Panel)) g.FillPath(fill, path);
        var oldClip = g.Clip;
        g.SetClip(new Rectangle(0, 0, Width, _head.Height + 1));
        using (var region = new Region(path)) g.IntersectClip(region);   // Region giữ tài nguyên GDI+: giải phóng ngay, không đợi GC (vẽ mỗi lần sơn thẻ)
        using (var hb = new SolidBrush(AppColors.PanelAlt)) g.FillRectangle(hb, new Rectangle(0, 0, Width, _head.Height + 1));
        g.Clip = oldClip;
        using var pen = new Pen(AppColors.Border, NativeSkin.BorderWidth(this));
        g.DrawPath(pen, path);
        g.DrawLine(pen, 1, _head.Height, Width - 2, _head.Height);
    }
}

/// <summary>Đồ nghề dựng nhanh giao diện native cho các tool (tránh lặp code tạo nhãn / nút / ô nhập / lưới).</summary>
internal static class NK
{
    /// <summary>Nhãn chữ phụ. <paramref name="wrap"/> = true: tự xuống dòng theo bề rộng được cấp và tự cao theo số dòng (<see cref="WrapLabel"/>) — trước đây là
    /// Label AutoSize=false giữ nguyên chiều cao 1 dòng nên chú thích dài bị cắt cụt.</summary>
    public static Label Muted(string text, bool wrap = false) => wrap
        ? new WrapLabel { Text = text, Margin = new Padding(0, 4, 6, 4) }
        : new MutedLabel { Text = text, AutoSize = true, BackColor = Color.Transparent, UseMnemonic = false, Margin = new Padding(0, 9, 6, 3) };   // top 9: canh giữa với nút/ô nhập cùng hàng

    public static Label Text(string text) => new() { Text = text, AutoSize = true, BackColor = Color.Transparent, UseMnemonic = false, Margin = new Padding(0, 9, 6, 3) };

    /// <summary>Nhãn tự xuống dòng theo bề rộng của control cha (AutoSize + MaximumSize cập nhật theo cha).</summary>
    public static Label Wrap(string text, Control host, int margin = 24)
    {
        var l = new MutedLabel { Text = text, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) };
        void Fit() { l.MaximumSize = new Size(Math.Max(120, host.ClientSize.Width - margin), 0); }
        host.SizeChanged += (_, _) => Fit();
        host.HandleCreated += (_, _) => Fit();
        host.VisibleChanged += (_, _) => Fit();
        host.Layout += (_, _) => Fit();
        return l;
    }

    public static PillButton Button(string text, System.Action onClick, bool primary = false, string? tip = null)
    {
        var b = PillButton.Flat(text, primary);
        b.Margin = new Padding(0, 3, 8, 3);   // khe 8px giữa các nút + 3px trên/dưới: hàng nút xuống dòng khi hẹp không dính nhau
        b.Height = NativeSkin.RowHeight(b, 26);
        b.Click += (_, _) => onClick();
        if (tip is not null) Tip(b, tip);
        return b;
    }

    // Một ToolTip cho mỗi form (thay cho mỗi nút một "new ToolTip()": mỗi cái là 1 cửa sổ Windows, không ai Dispose, và giữ tham chiếu tới nút
    // → trang/tab đã đóng không được giải phóng). ToolTip của form bị Dispose cùng form; control bị Dispose thì tự gỡ khỏi ToolTip.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, ToolTip> FormTips = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, ToolTip> BoundTips = new();

    /// <summary>Gắn chú thích di chuột cho <paramref name="c"/> qua ToolTip dùng chung của form chứa nó (gắn khi control đã nằm trong form).</summary>
    public static void Tip(Control c, string? text)
    {
        if (BoundTips.TryGetValue(c, out var bound)) { try { bound.SetToolTip(c, text); } catch (ObjectDisposedException) { } return; }
        bool Bind()
        {
            if (c.IsDisposed || c.FindForm() is not { } form || form.IsDisposed) return false;
            var tip = FormTips.GetValue(form, f => { var t = new ToolTip(); f.Disposed += (_, _) => t.Dispose(); return t; });
            tip.SetToolTip(c, text);
            BoundTips.AddOrUpdate(c, tip);
            c.Disposed += (_, _) => { try { tip.SetToolTip(c, null); } catch (ObjectDisposedException) { } };
            return true;
        }
        if (c.IsHandleCreated && Bind()) return;
        void Try(object? s, EventArgs e) { if (!Bind()) return; c.HandleCreated -= Try; c.VisibleChanged -= Try; }
        c.HandleCreated += Try;
        c.VisibleChanged += Try;   // control con được báo khi khung cha hiện lên — lúc đó chắc chắn đã nằm trong form
    }

    public static RoundedInput Input(string placeholder = "", bool mono = false)
    {
        var i = new RoundedInput { PlaceholderText = placeholder };
        if (mono) i.Mono = true;
        return i;
    }

    public static RoundedInput Memo(int rows, string placeholder = "", bool mono = true)
    {
        var i = new RoundedInput { PlaceholderText = placeholder, Multiline = true, Mono = mono };
        i.Height = rows * 20 + 14;
        return i;
    }

    public static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 5, 10, 0) };

    public static FlowLayoutPanel Row(params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        f.Controls.AddRange(items);
        return f;
    }

    /// <summary>Dựng DataGridView chỉ đọc có tiêu đề cột (bề rộng px thiết kế; 0 = giãn).</summary>
    public static DataGridView Grid(params (string Name, string Title, int Width)[] cols)
    {
        var g = new DataGridView
        {
            ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None, BorderStyle = BorderStyle.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
            Dock = DockStyle.Fill,   // mặc định lấp vùng chứa (Copy Source Standard từng quên Dock → lưới 240×150 kẹt ở góc, bị hàng trên đè)
        };
        foreach (var (name, title, w) in cols)
        {
            var c = new DataGridViewTextBoxColumn { Name = name, HeaderText = title, SortMode = DataGridViewColumnSortMode.NotSortable };
            if (w > 0) c.Width = NativeSkin.S(w); else c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            g.Columns.Add(c);
        }
        return g;
    }

    /// <summary>Lưới "nhãn | ô nhập" gọn cho các thẻ biểu mẫu: cột nhãn cố định, cột ô giãn hết bề rộng; mỗi hàng tự cao theo ô.</summary>
    public static TableLayoutPanel FormGrid(int labelWidth, params (string Label, Control Field)[] rows)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, BackColor = Color.Transparent };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (label, field) in rows)
        {
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            // MaximumSize: nhãn dài tự xuống dòng trong cột nhãn (trước đây bị cắt cụt).
            var l = new MutedLabel { Text = label, AutoSize = true, MaximumSize = new Size(Math.Max(40, labelWidth - 8), 0), UseMnemonic = false, BackColor = Color.Transparent, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 10, 8, 4) };
            field.Dock = DockStyle.Fill; field.Margin = new Padding(0, 4, 0, 4);
            t.Controls.Add(l); t.Controls.Add(field);
        }
        return t;
    }

    /// <summary>Xếp các điều khiển từ trên xuống dưới trong 1 khung (đặt Dock=Top và thêm theo thứ tự ngược — WinForms: điều khiển thêm SAU nằm TRÊN).</summary>
    public static void Stack(Control host, params Control[] topToBottom)
    {
        for (var i = topToBottom.Length - 1; i >= 0; i--)
        {
            topToBottom[i].Dock = DockStyle.Top; host.Controls.Add(topToBottom[i]);
            // Dock bỏ qua Margin: chèn khe 12px giữa 2 thẻ liền nhau (như gap của .card ở trang web) cho bố cục thoáng.
            if (i > 0 && topToBottom[i] is CardPanel && topToBottom[i - 1] is CardPanel) { var gap = Spacer(12); gap.Dock = DockStyle.Top; host.Controls.Add(gap); }
        }
    }

    /// <summary>Hàng control chia cột theo tỉ lệ (weight) trên MỘT hàng: luôn vừa bề ngang, co giãn theo khung, không xuống dòng
    /// (thay hàng nút FlowLayout bị rớt từng nút xuống dòng khi cột hẹp). Nút trong hàng không tự co theo chữ (AutoWidth = false).</summary>
    public static TableLayoutPanel GridRow(int gap = 6, params (Control Item, float Weight)[] items)
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = items.Length, RowCount = 1, BackColor = Color.Transparent, Margin = new Padding(0) };
        t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var h = 0;
        for (var i = 0; i < items.Length; i++)
        {
            var (c, w) = items[i];
            if (w <= 0)
            {
                // weight 0 = cột vừa nội dung (nhãn, nút giữ bề rộng theo chữ) — canh giữa theo chiều dọc của hàng.
                t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                c.Anchor = c is PillButton ? AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Bottom : AnchorStyles.Left;
                c.Margin = new Padding(i == 0 ? 0 : gap / 2, 0, i == items.Length - 1 ? 0 : gap / 2, 0);
                t.Controls.Add(c, i, 0);
                continue;
            }
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, w));
            if (c is PillButton pb) pb.AutoWidth = false;
            c.Dock = DockStyle.Fill;
            c.Margin = new Padding(i == 0 ? 0 : gap / 2, 0, i == items.Length - 1 ? 0 : gap / 2, 0);
            t.Controls.Add(c, i, 0);
            if (c is RoundedInput ri) h = Math.Max(h, ri.NaturalHeight);   // nút / ô chọn: cao theo hàng chuẩn, không lấy Height mặc định (DropDownButton = 100px)
        }
        t.Height = Math.Max(h, NativeSkin.RowHeight(null, 30));
        return t;
    }

    /// <summary>Bọc 1 hàng (vd GridRow) trong panel Dock Top có lề — dùng với NK.Stack để các hàng cách đều nhau.</summary>
    public static Panel Padded(Control row, int left, int top, int right, int bottom)
    {
        var p = new Panel { Dock = DockStyle.Top, Padding = new Padding(left, top, right, bottom), BackColor = Color.Transparent };
        row.Dock = DockStyle.Fill;
        p.Height = row.Height + top + bottom;
        p.Controls.Add(row);
        return p;
    }

    /// <summary>Hàng [ô nhập ⟷ giãn][khe][nút] — ô và nút không dính nhau (Dock bỏ qua Margin nên khe là 1 panel trong suốt).</summary>
    public static Control InputWithButton(Control input, Control button, int gap = 8)
    {
        // Cao đúng bằng ô nhập thường (theo font) — không lấy số cố định, nên ô có nút không cao hơn các ô khác cùng cột.
        var p = new Panel { Height = input is RoundedInput ri ? ri.NaturalHeight : NativeSkin.RowHeight(null, 30), BackColor = Color.Transparent };
        var slot = new Panel { Dock = DockStyle.Right, Width = button.Width + gap, Padding = new Padding(gap, 0, 0, 0), BackColor = Color.Transparent };
        button.Dock = DockStyle.Fill; slot.Controls.Add(button);
        button.SizeChanged += (_, _) => slot.Width = button.Width + gap;
        input.Dock = DockStyle.Fill;
        p.Controls.Add(input); p.Controls.Add(slot);
        // Font của ô chỉ có sau ThemeManager.Apply / đổi Template → tính lại chiều cao khi font đổi.
        if (input is RoundedInput r2) { r2.Inner.FontChanged += (_, _) => p.Height = r2.NaturalHeight; p.HandleCreated += (_, _) => p.Height = r2.NaturalHeight; }
        return p;
    }

    /// <summary>ListView chi tiết theo theme: đầu cột vẽ tay (nền thanh phụ, chữ phụ đậm, gạch dưới) thay cho đầu cột trắng của hệ điều hành; cột cuối tự giãn
    /// hết bề ngang để không còn dải trắng bên phải; thanh cuộn tối khi theme tối.</summary>
    public static void ThemeList(ListView lv)
    {
        lv.OwnerDraw = true;
        var bold = new Font(lv.Font, FontStyle.Bold);   // tạo 1 lần, không tạo lại mỗi lần vẽ
        lv.Disposed += (_, _) => bold.Dispose();
        lv.DrawItem += (_, e) => e.DrawDefault = true;
        lv.DrawSubItem += (_, e) => e.DrawDefault = true;
        lv.DrawColumnHeader += (_, e) =>
        {
            using (var b = new SolidBrush(AppColors.PanelAlt)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var pen = new Pen(AppColors.Border)) { e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1); if (e.ColumnIndex > 0) e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Top + 4, e.Bounds.Left, e.Bounds.Bottom - 5); }
            var r = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", bold, r, AppColors.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
        void Recolor() { lv.BackColor = AppColors.Input; lv.ForeColor = AppColors.Text; lv.Invalidate(); }
        void FitLast()
        {
            if (lv.Columns.Count == 0) return;
            var others = 0; for (var i = 0; i < lv.Columns.Count - 1; i++) others += lv.Columns[i].Width;
            var w = Math.Max(60, lv.ClientSize.Width - others);
            if (lv.Columns[^1].Width != w) lv.Columns[^1].Width = w;
        }
        lv.Resize += (_, _) => FitLast();
        lv.ColumnWidthChanged += (_, e) => { if (e.ColumnIndex < lv.Columns.Count - 1) FitLast(); };
        DarkScroll(lv);
        Recolor();
        ThemeManager.ThemeChanged += Recolor;
        lv.Disposed += (_, _) => ThemeManager.ThemeChanged -= Recolor;
    }

    /// <summary>Cây thư mục / file theo theme (như cây của trang web): icon thư mục (màu nhấn) / file vẽ bằng GDI một lần theo DPI (ImageList vài KB, không ảnh ngoài),
    /// tên thư mục màu nhấn, dòng chọn tô màu chọn của theme thay cho xanh hệ thống, thanh cuộn tối. Nút: <c>Tag</c> bất kỳ; thư mục = nút có con
    /// hoặc <c>ImageKey == "folder"</c>. Dùng <see cref="AddPath"/> + <see cref="SortTree"/> để dựng cây từ đường dẫn tương đối.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TreeView, object> ThemedTrees = new();
    /// <summary>Cây đã tự vẽ bằng <see cref="ThemeTree"/> — ThemeManager không gắn thêm bộ vẽ của nó (2 bộ vẽ chồng chữ lên nhau).</summary>
    public static bool IsThemedTree(TreeView tv) => ThemedTrees.TryGetValue(tv, out _);

    public static void ThemeTree(TreeView tv)
    {
        if (IsThemedTree(tv)) return;
        ThemedTrees.Add(tv, new object());
        tv.BorderStyle = BorderStyle.None;
        tv.ShowLines = false;
        tv.FullRowSelect = true;
        tv.HideSelection = false;
        tv.ItemHeight = Math.Max(20, tv.Font.Height + UiTemplate.Dens(8));
        var images = new ImageList { ColorDepth = ColorDepth.Depth32Bit };
        void Draw()
        {
            var s = Math.Max(16, (int)Math.Round(16 * tv.DeviceDpi / 96.0));
            images.Images.Clear(); images.ImageSize = new Size(s, s);
            images.Images.Add("folder", DrawFolder(s)); images.Images.Add("file", DrawFile(s));
            tv.BackColor = AppColors.Input; tv.ForeColor = AppColors.Text; tv.Invalidate();
        }
        tv.ImageList = images;
        tv.DrawMode = TreeViewDrawMode.OwnerDrawText;
        tv.DrawNode += (_, e) =>
        {
            if (e.Node is null) return;
            var sel = (e.State & TreeNodeStates.Selected) != 0;
            var folder = e.Node.ImageKey == "folder";
            var r = new Rectangle(e.Bounds.X, e.Bounds.Y, Math.Max(e.Bounds.Width, tv.ClientSize.Width - e.Bounds.X), e.Bounds.Height);
            using (var b = new SolidBrush(sel ? AppColors.Selection : AppColors.Input)) e.Graphics.FillRectangle(b, r);
            TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont ?? tv.Font, r, !e.Node.ForeColor.IsEmpty && e.Node.ForeColor != AppColors.Text ? e.Node.ForeColor : folder ? AppColors.Accent : AppColors.Text,   // giữ màu riêng của dòng (vd file lỗi tô đỏ)
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        };
        tv.Disposed += (_, _) => images.Dispose();
        DarkScroll(tv);
        tv.HandleCreated += (_, _) => Draw();
        ThemeManager.ThemeChanged += Draw;
        tv.Disposed += (_, _) => ThemeManager.ThemeChanged -= Draw;
        Draw();
    }

    private static Bitmap DrawFolder(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float u = s / 16f;
        using (var tab = FlatToolStripRenderer.RoundedRect(new Rectangle((int)(1 * u), (int)(3 * u), (int)(6 * u), (int)(3 * u)), Math.Max(1, (int)u)))
        using (var b = new SolidBrush(ControlPaint.Dark(AppColors.Accent, 0.05f))) g.FillPath(b, tab);
        using (var body = FlatToolStripRenderer.RoundedRect(new Rectangle((int)(1 * u), (int)(5 * u), (int)(14 * u), (int)(9 * u)), Math.Max(1, (int)(1.5 * u))))
        using (var b = new SolidBrush(AppColors.Accent)) g.FillPath(b, body);
        return bmp;
    }

    private static Bitmap DrawFile(int s)
    {
        var bmp = new Bitmap(s, s);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float u = s / 16f;
        var pts = new[] { new PointF(3.5f * u, 1.5f * u), new PointF(10 * u, 1.5f * u), new PointF(12.5f * u, 4 * u), new PointF(12.5f * u, 14.5f * u), new PointF(3.5f * u, 14.5f * u) };
        using (var b = new SolidBrush(AppColors.PanelAlt)) g.FillPolygon(b, pts);
        using var pen = new Pen(AppColors.TextMuted, Math.Max(1f, u));
        g.DrawPolygon(pen, pts);
        g.DrawLines(pen, new[] { new PointF(10 * u, 1.5f * u), new PointF(10 * u, 4 * u), new PointF(12.5f * u, 4 * u) });
        for (var y = 7f; y <= 12f; y += 2.5f) g.DrawLine(pen, 5.5f * u, y * u, 10.5f * u, y * u);
        return bmp;
    }

    /// <summary>Thêm 1 file vào cây theo đường dẫn tương đối (tạo các thư mục trung gian nếu chưa có). Trả về nút file.</summary>
    public static TreeNode AddPath(TreeNodeCollection roots, string relativePath, object? tag, string? tip = null)
    {
        var parts = relativePath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var nodes = roots;
        TreeNode? node = null;
        for (var k = 0; k < parts.Length; k++)
        {
            var last = k == parts.Length - 1;
            node = last ? null : nodes.Cast<TreeNode>().FirstOrDefault(x => x.ImageKey == "folder" && x.Text == parts[k]);
            if (node is null)
            {
                var key = last ? "file" : "folder";
                node = new TreeNode(parts[k]) { ImageKey = key, SelectedImageKey = key, Tag = last ? tag : null, ToolTipText = last ? tip ?? "" : "" };
                nodes.Add(node);
            }
            nodes = node.Nodes;
        }
        return node!;
    }

    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int StrCmpLogicalW(string a, string b);

    /// <summary>Xếp như cây của trang web: thư mục trước, file sau; trong mỗi nhóm theo thứ tự "tự nhiên" (file2 &lt; file10).</summary>
    public static void SortTree(TreeNodeCollection nodes)
    {
        var list = nodes.Cast<TreeNode>().ToList();
        list.Sort((a, b) =>
        {
            var fa = a.ImageKey == "folder"; var fb = b.ImageKey == "folder";
            return fa == fb ? StrCmpLogicalW(a.Text, b.Text) : (fa ? -1 : 1);
        });
        nodes.Clear();
        foreach (var n in list) { nodes.Add(n); SortTree(n.Nodes); }
    }

    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

    /// <summary>Thanh cuộn (và mũi tên mở nhánh của TreeView) theo theme: "DarkMode_Explorer" khi tối, "Explorer" khi sáng (Windows 10 1809+; máy cũ hơn bỏ qua).
    /// Không tốn RAM — chỉ đổi theme vẽ của cửa sổ Windows sẵn có.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, object> DarkScrolled = new();

    public static void DarkScroll(Control c)
    {
        // Gọi nhiều lần (ThemeManager.Apply chạy lại khi đổi theme / Template) chỉ đăng ký 1 lần — tránh rò handler.
        if (DarkScrolled.TryGetValue(c, out _)) { ApplyNow(c); return; }
        DarkScrolled.Add(c, new object());
        void Apply() { if (!c.IsHandleCreated) return; try { SetWindowTheme(c.Handle, AppColors.IsDark ? "DarkMode_Explorer" : "Explorer", null); } catch { } }
        c.HandleCreated += (_, _) => Apply();
        ThemeManager.ThemeChanged += Apply;
        c.Disposed += (_, _) => ThemeManager.ThemeChanged -= Apply;
        Apply();
    }

    private static void ApplyNow(Control c)
    {
        if (!c.IsHandleCreated) return;
        try { SetWindowTheme(c.Handle, AppColors.IsDark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
    }

    public static Control Spacer(int h) => new Panel { Height = h, Width = 4, BackColor = Color.Transparent };

    /// <summary>Hộp thoại xác nhận Có / Không (chuẩn của các tool).</summary>
    public static bool Confirm(IWin32Window owner, string text, string title = "Bcode") =>
        MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    public static void CopyText(string text) { try { Clipboard.SetText(text); } catch { /* clipboard đang bị chương trình khác giữ */ } }

    public static void OpenFolder(string path)
    {
        try { if (Directory.Exists(path)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { /* không mở được */ }
    }

    /// <summary>Làm các control con của <paramref name="host"/> (xếp dọc, Dock Top) luôn rộng bằng host — dùng cho cột cuộn các thẻ.</summary>
    public static void FitWidth(Control host, params Control[] stack)
    {
        void Fit() { foreach (var c in stack) { var w = Math.Max(100, host.ClientSize.Width - host.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth); if (c.MinimumSize.Width != w) { c.MinimumSize = new Size(w, 0); c.MaximumSize = new Size(w, 0); } if (c.Width != w) c.Width = w; } }
        host.SizeChanged += (_, _) => Fit();
        host.HandleCreated += (_, _) => Fit();
        host.VisibleChanged += (_, _) => Fit();
        host.Layout += (_, _) => Fit();
    }
}

/// <summary>Nhóm nút chọn một trong nhiều (như <c>.seg</c> của trang web): nút đang chọn tô màu nhấn.</summary>
internal sealed class SegmentedBar : FlowLayoutPanel
{
    private readonly List<(string Value, PillButton Button)> _items = new();
    public string Value { get; private set; } = "";
    public event Action<string>? Changed;

    public SegmentedBar(params (string Value, string Label)[] options)
    {
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; WrapContents = false; BackColor = Color.Transparent; Margin = new Padding(0, 0, 12, 0);
        foreach (var (v, label) in options)
        {
            var value = v;
            var b = PillButton.Flat(label);
            b.Margin = new Padding(0, 0, 3, 0);
            b.Height = NativeSkin.RowHeight(b, 26);
            b.Click += (_, _) => Select(value, raise: true);
            _items.Add((v, b));
            Controls.Add(b);
        }
        if (options.Length > 0) Select(options[0].Value, raise: false);
    }

    public void Select(string value, bool raise)
    {
        Value = value;
        foreach (var (v, b) in _items) { b.IsPrimary = v == value; b.Invalidate(); }
        if (raise) Changed?.Invoke(value);
    }

    public new bool Enabled { get => base.Enabled; set { base.Enabled = value; foreach (var (_, b) in _items) b.Enabled = value; } }
}
