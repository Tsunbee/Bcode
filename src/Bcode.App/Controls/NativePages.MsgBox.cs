using System.Drawing.Drawing2D;
using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Hộp thông báo theo theme (msgbox.html) ở chế độ App — cùng giao thức với trang web, chủ là Forms/MessageBoxForm.cs.
/// Chữ nằm trong ô chỉ đọc không viền (bôi đen + Ctrl+C được; ô Edit xuống dòng cả giữa đường dẫn dài không dấu cách), quá cao thì ô tự cuộn.</summary>
internal sealed class MsgBoxPage : NativePage
{
    private readonly Panel _main = new() { Dock = DockStyle.Fill };
    private readonly IconBox _icon = new() { Dock = DockStyle.Left };
    private readonly MsgText _text = new();
    private readonly Panel _bottom = new() { Dock = DockStyle.Bottom };
    // Hàng nút canh phải, tự đặt toạ độ (LayoutButtons). FlowLayoutPanel (Dock Right + AutoSize hay RightToLeft) giữ toạ độ cũ khi PillButton tự giãn → nút tràn mép phải.
    private readonly Panel _buttons = new() { Dock = DockStyle.Fill, BackColor = Color.Transparent };
    private readonly Label _hint = new() { Text = "Ctrl+C: chép nội dung", Dock = DockStyle.Left, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, UseMnemonic = false };
    private readonly List<(PillButton Button, string Id, string Key)> _set = new();
    private int _cur;
    private int _lastWidth;

    public MsgBoxPage()
    {
        _main.Padding = new Padding(NativeSkin.S(18), NativeSkin.S(16), NativeSkin.S(18), NativeSkin.S(12));
        _icon.Width = NativeSkin.S(46);
        _text.Dock = DockStyle.Fill;
        _main.Controls.Add(_text);
        _main.Controls.Add(_icon);
        _bottom.Height = NativeSkin.S(48);
        _bottom.Padding = new Padding(NativeSkin.S(14), 0, NativeSkin.S(12), 0);
        _bottom.Layout += (_, _) => FitHint();
        _buttons.Layout += (_, _) => LayoutButtons();
        _bottom.Controls.Add(_buttons);
        _bottom.Controls.Add(_hint);   // Fill (_buttons) ở index 0 → dock sau cùng, lấy phần còn lại sau nhãn bên trái
        _bottom.Paint += (_, e) => { using var p = new Pen(AppColors.Border); e.Graphics.DrawLine(p, 0, 0, _bottom.Width, 0); };
        Controls.Add(_main);
        Controls.Add(_bottom);
        Recolor();
        ThemeManager.Subscribe(this, Recolor);
    }

    private void Recolor()
    {
        BackColor = _main.BackColor = AppColors.Background;
        // ThemeManager.Apply (lúc mở form / đổi theme) tô Panel / Label theo màu chung → đặt lại đủ cả nền thanh nút và nhãn
        _bottom.BackColor = _buttons.BackColor = _hint.BackColor = AppColors.PanelAlt;
        _hint.ForeColor = AppColors.TextMuted;
        _text.BackColor = AppColors.Background;
        _text.ForeColor = AppColors.Text;
        _icon.Invalidate();
        _bottom.Invalidate();
        foreach (var (b, _, _) in _set) b.Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _text.Font = ThemeManager.ForDpi(_text, ThemeManager.BaseFont);   // ô chữ bỏ qua ThemeManager.Apply (IThemeOptOut) → tự đặt font
        // ThemedForm.OnLoad: Load chạy TRƯỚC ThemeManager.Apply(form) → tô lại màu riêng sau lượt Apply đó
        if (FindForm() is { } f) f.Load += (_, _) => { try { BeginInvoke(Recolor); } catch { /* đang đóng */ } };
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "msgbox.init") return;
        var o = Arg(a, 0);
        _text.Text = P(o, "text").Replace("\r\n", "\n").Replace("\n", "\r\n");
        _icon.Kind = P(o, "icon");
        _icon.Visible = _icon.Kind.Length > 0;
        _buttons.Controls.Clear();
        foreach (var (b, _, _) in _set) b.Dispose();
        _set.Clear();
        foreach (var b in PA(o, "buttons"))
        {
            var id = P(b, "id");
            var btn = NK.Button(P(b, "label"), () => Post(new { action = "answer", r = id }));
            btn.MinimumSize = new Size(NativeSkin.S(84), 0);
            btn.Width = Math.Max(btn.Width, NativeSkin.S(84));
            var index = _set.Count;
            btn.GotFocus += (_, _) => Select(index, focus: false);
            _set.Add((btn, id, P(b, "key")));
        }
        foreach (var (btn, _, _) in _set) { btn.SizeChanged += (_, _) => _buttons.PerformLayout(); _buttons.Controls.Add(btn); }
        _buttons.PerformLayout();
        Select(Math.Clamp(PI(o, "def"), 0, Math.Max(0, _set.Count - 1)), focus: true);
        BeginInvoke(FitNow);
    }

    /// <summary>Nhãn gợi ý bên trái rộng vừa chữ (cao bằng thanh, chữ canh giữa dọc).</summary>
    private void FitHint()
    {
        var hintW = TextRenderer.MeasureText(_hint.Text, _hint.Font).Width + NativeSkin.S(8);
        if (_hint.Width != hintW) _hint.Width = hintW;
    }

    /// <summary>Xếp nút từ phải sang trái theo thứ tự đọc Yes  No  Cancel, khe 8px, canh giữa theo chiều dọc của thanh.</summary>
    private void LayoutButtons()
    {
        var x = _buttons.ClientSize.Width;
        var gap = NativeSkin.S(8);
        for (var i = _set.Count - 1; i >= 0; i--)
        {
            var b = _set[i].Button;
            x -= b.Width;
            var loc = new Point(x, Math.Max(0, (_buttons.ClientSize.Height - b.Height) / 2));
            if (b.Location != loc) b.Location = loc;
            x -= gap;
        }
    }

    private void Select(int index, bool focus)
    {
        if (_set.Count == 0) return;
        _cur = (index % _set.Count + _set.Count) % _set.Count;
        for (var i = 0; i < _set.Count; i++) { _set[i].Button.IsPrimary = i == _cur; _set[i].Button.Invalidate(); }
        if (focus && _set[_cur].Button.CanFocus) _set[_cur].Button.Focus();
    }

    public override void FocusPrimary() => Select(_cur, focus: true);

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width != _lastWidth && _set.Count > 0 && IsHandleCreated) { _lastWidth = Width; BeginInvoke(FitNow); }
    }

    /// <summary>Báo chủ chiều cao cần (cùng tin "fit" của trang web): số dòng ô chữ sau khi xuống dòng × cao dòng + lề + thanh nút.</summary>
    private void FitNow()
    {
        if (IsDisposed || !_text.IsHandleCreated || ClientSize.Height <= 0) return;
        var lines = _text.TextLength == 0 ? 1 : _text.GetLineFromCharIndex(_text.TextLength) + 1;
        var lineH = TextRenderer.MeasureText("Ág", _text.Font).Height;
        var textH = lines * lineH + NativeSkin.S(6);
        var need = Math.Max(textH, _icon.Visible ? NativeSkin.S(34) : 0) + _main.Padding.Vertical + _bottom.Height;
        Post(new { action = "fit", content = need, view = ClientSize.Height });
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Escape: Post(new { action = "escape" }); return true;
            case Keys.Enter: case Keys.Space: if (_set.Count > 0) Post(new { action = "answer", r = _set[_cur].Id }); return true;
            case Keys.Right: case Keys.Down: case Keys.Tab: Select(_cur + 1, focus: true); return true;
            case Keys.Left: case Keys.Up: case Keys.Shift | Keys.Tab: Select(_cur - 1, focus: true); return true;
            case Keys.Control | Keys.C: case Keys.Control | Keys.Insert:
                if (_text.SelectionLength > 0) NK.CopyText(_text.SelectedText);
                else Post(new { action = "copy" });
                return true;
            case Keys.Control | Keys.A: _text.Focus(); _text.SelectAll(); return true;
        }
        if ((keyData & (Keys.Control | Keys.Alt)) == 0 && (keyData & Keys.KeyCode) is >= Keys.A and <= Keys.Z)
        {
            var ch = ((char)('a' + ((keyData & Keys.KeyCode) - Keys.A))).ToString();
            foreach (var (_, id, key) in _set)
                if (string.Equals(key, ch, StringComparison.OrdinalIgnoreCase)) { Post(new { action = "answer", r = id }); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Ô chữ: TextBox chỉ đọc không viền, nền trùng nền hộp. IThemeOptOut: ThemeManager.Apply tô ô nhập (nền Input + viền) — ở đây phải là chữ thường.</summary>
    private sealed class MsgText : TextBox, IThemeOptOut
    {
        public MsgText()
        {
            Multiline = true; ReadOnly = true; WordWrap = true; BorderStyle = BorderStyle.None; TabStop = false;
            ScrollBars = ScrollBars.None; ShortcutsEnabled = true; Cursor = Cursors.IBeam;
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); NK.DarkScroll(this); }
        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            // Đổi ScrollBars = tạo lại handle → không làm ngay trong lúc đang tạo / đổi cỡ (lỗi 1400 "Error creating window handle"), đợi lượt sau.
            if (IsHandleCreated && !_pending) { _pending = true; BeginInvoke(UpdateBars); }
        }

        private bool _pending;

        // Chỉ hiện thanh cuộn khi chữ thật sự cao hơn ô (TextBox không tự ẩn thanh cuộn dọc).
        public void UpdateBars()
        {
            _pending = false;
            if (!IsHandleCreated || IsDisposed) return;
            var lines = TextLength == 0 ? 1 : GetLineFromCharIndex(TextLength) + 1;
            var need = lines * TextRenderer.MeasureText("Ág", Font).Height > ClientSize.Height + 2;
            var want = need ? ScrollBars.Vertical : ScrollBars.None;
            if (ScrollBars != want) ScrollBars = want;
        }
    }

    /// <summary>Biểu tượng vẽ cùng hình với SVG của msgbox.html: tròn màu nhấn + "i" / "?", tam giác cảnh báo + "!", tròn đỏ + "×".</summary>
    private sealed class IconBox : Control
    {
        private string _kind = "";
        public string Kind { get => _kind; set { _kind = value ?? ""; Invalidate(); } }

        public IconBox() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_kind.Length == 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var size = Math.Min(Width - NativeSkin.S(10), NativeSkin.S(32));
            if (size <= 4) return;
            var k = size / 32f;   // toạ độ theo khung 32×32 của SVG
            g.TranslateTransform(0, NativeSkin.S(1));
            g.ScaleTransform(k, k);
            switch (_kind)
            {
                case "info":
                {
                    using var bg = new SolidBrush(AppColors.Accent); using var fg = new SolidBrush(AppColors.OnAccent);
                    g.FillEllipse(bg, 1, 1, 30, 30);
                    using (var p = RoundRect(14.4f, 13.5f, 3.2f, 10f, 1.2f)) g.FillPath(fg, p);
                    g.FillEllipse(fg, 14f, 7.3f, 4f, 4f);
                    break;
                }
                case "question":
                {
                    using var bg = new SolidBrush(AppColors.Accent); using var fg = new SolidBrush(AppColors.OnAccent);
                    using var pen = new Pen(AppColors.OnAccent, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.FillEllipse(bg, 1, 1, 30, 30);
                    using (var p = new GraphicsPath())
                    {
                        p.AddArc(11.6f, 7.9f, 8.8f, 8.8f, 180, 225);   // vòng trên của dấu "?"
                        p.AddBezier(19.1f, 15.7f, 17.9f, 16.3f, 16f, 17f, 16f, 19.6f);
                        g.DrawPath(pen, p);
                    }
                    g.FillEllipse(fg, 14.1f, 22.1f, 3.8f, 3.8f);
                    break;
                }
                case "warning":
                {
                    var amber = Color.FromArgb(0xf2, 0xb3, 0x2a);   // như msgbox.html: không lấy AppColors.Warning (bảng màu tối có warning nâu sậm → "!" không đọc được)
                    using var bg = new SolidBrush(amber); using var fg = new SolidBrush(Color.FromArgb(0x1e, 0x1e, 0x1e));
                    using (var tri = new GraphicsPath())
                    {
                        tri.AddLines(new[] { new PointF(16, 2.5f), new PointF(31, 29.8f), new PointF(1, 29.8f) });
                        tri.CloseFigure();
                        using var edge = new Pen(amber, 2.4f) { LineJoin = LineJoin.Round };
                        g.FillPath(bg, tri); g.DrawPath(edge, tri);
                    }
                    using (var p = RoundRect(14.4f, 10f, 3.2f, 10.5f, 1.2f)) g.FillPath(fg, p);
                    g.FillEllipse(fg, 14.1f, 22.7f, 3.8f, 3.8f);
                    break;
                }
                case "error":
                {
                    using var bg = new SolidBrush(AppColors.Danger);
                    using var pen = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                    g.FillEllipse(bg, 1, 1, 30, 30);
                    g.DrawLine(pen, 11, 11, 21, 21); g.DrawLine(pen, 21, 11, 11, 21);
                    break;
                }
            }
        }

        private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath(); var d = r * 2;
            p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
