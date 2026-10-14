using System.Drawing.Drawing2D;

namespace Bcode.App.Controls;

/// <summary>
/// Fully-rounded "pill" button — the shape used for the standout actions in the new shell's
/// top bar (Quick Access, Settings, Theme toggle), the same visual language as Fiddler's own
/// "Fiddler MCP"/"Ask Assistant" top-bar buttons in the reference screenshot. A normal
/// WinForms Button can't get fully rounded corners just by styling FlatAppearance, so this
/// subclass owner-draws its own background/border/icon/text instead.
///
/// Not run through Bcode.App.UI.ThemeManager's normal Button case (that one assumes a
/// rectangular flat button) — PillButton reads AppColors directly in OnPaint every time, so it
/// re-colors itself correctly on theme toggle with no extra wiring.
/// </summary>
public class PillButton : Button
{
    public Bcode.App.UI.IconGlyph? Icon { get; set; }
    public bool IsPrimary { get; set; }

    /// <summary>Màu chữ / màu nền riêng (từ Template giao diện cho nút Script trên thanh trên); null = theo theme.</summary>
    public Color? ForeOverride { get; set; }
    public Color? BackOverride { get; set; }

    /// <summary>Nút nằm trên thanh tự tô nền PanelAlt (thanh trên/dưới của chế độ App): góc bo được tô đúng màu nền đó thay vì màu BackColor của control cha.</summary>
    public bool OnPanelAlt { get; set; }
    /// <summary>Chữ căn trái (ô chọn thả xuống) thay vì giữa nút.</summary>
    public bool LeftAlign { get; set; }
    /// <summary>Vẽ mũi tên ▾ ở mép phải (nút mở menu).</summary>
    public bool Chevron { get; set; }
    /// <summary>true = nút tự đo bề rộng theo chữ/icon (mặc định của Flat); đặt false khi bề rộng do bố cục quyết định.</summary>
    public bool AutoWidth { get => _autoWidth; set => _autoWidth = value; }

    /// <summary>Corner radius in pixels. Null (the default) keeps the fully-rounded pill used
    /// in the top bar; 6 is the radius the HTML buttons use (shell.css <c>.btn</c>), which is
    /// what <see cref="Flat"/> sets so a native button sitting in a toolbar next to an HTML
    /// one is the same shape.</summary>
    public int? CornerRadius { get => _cornerRadius; set { _cornerRadius = value; if (_autoWidth) AutoSizeToContent(); } }
    private int? _cornerRadius;

    /// <summary>A button shaped like the HTML <c>.btn</c> in shell.css — same radius, padding
    /// and colors. Use this instead of <c>new Button</c> anywhere a plain button has to sit in
    /// a toolbar or panel that also contains WebView2-rendered chrome; a stock WinForms Button
    /// cannot round its corners at all, which is what made those rows look like two different
    /// apps glued together.</summary>
    public static PillButton Flat(string text, bool primary = false)
    {
        var button = new PillButton { Text = text, IsPrimary = primary, CornerRadius = 8, _autoWidth = true };
        button.AutoSizeToContent();
        return button;
    }

    // Flat() buttons re-measure themselves whenever their text or font changes, because
    // ThemeManager assigns the app font AFTER construction — a width measured in the
    // constructor would be measured with the stock WinForms font and clip the label.
    private bool _autoWidth;

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        if (_autoWidth) AutoSizeToContent();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        if (_autoWidth) AutoSizeToContent();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate(); // owner-drawn: nothing repaints the disabled look for us
    }

    private bool _hovered;
    private bool _pressed;

    public PillButton()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Height = 30;
        Padding = new Padding(4);
        UseVisualStyleBackColor = false;
        AutoSize = false;
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs mevent) { base.OnMouseDown(mevent); _pressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs mevent) { base.OnMouseUp(mevent); _pressed = false; Invalidate(); }

    /// <summary>Measures the pill's ideal width from its icon + text so callers can just set
    /// AutoSizeToContent() once after Text/Icon are final, instead of guessing a fixed Width.</summary>
    public void AutoSizeToContent()
    {
        // Đo bằng TextRenderer (đúng bộ vẽ chữ của OnPaint) + chừa thêm chút: đo bằng GDI+ MeasureString lệch vài px nên chữ bị cắt "…" ở nút cuối.
        var textSize = string.IsNullOrEmpty(Text) ? Size.Empty : TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        var iconWidth = Icon is not null ? Height - 8 + 6 : 0;
        var padding = CornerRadius is null ? 16 : 12; // pills need more breathing room than a 6px-radius button
        Width = padding + iconWidth + textSize.Width + padding + (string.IsNullOrEmpty(Text) ? 0 : 6);
    }

    /// <summary>Chế độ App: nhờ control cha vẽ NỀN THẬT của nó vào vùng nút (rồi mới vẽ nút bo tròn đè lên) — 4 góc ngoài vùng bo luôn trùng nền xung quanh.
    /// Tô bằng Parent.BackColor như trước sai khi cha có nền Transparent (hàng nút NK.Row / GridRow / thẻ) → góc giữ rác của lần vẽ trước
    /// (ô vuông nhỏ màu cam / viền thừa ở góc nút).</summary>
    private bool PaintParentUnder(Graphics g, Control parent)
    {
        try
        {
            var state = g.Save();
            g.TranslateTransform(-Left, -Top);
            using (var pe = new PaintEventArgs(g, new Rectangle(Left, Top, Width, Height))) InvokePaintBackground(parent, pe);
            g.Restore(state);
            return true;
        }
        catch { return false; }
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // Tô nền dưới góc bo trước: control tự vẽ hoàn toàn nên nếu không, 4 góc giữ lại rác của lần vẽ trước (vết viền màu ở góc nút).
        if (Bcode.App.UI.UiMode.IsNative && !OnPanelAlt && Parent is { } host && PaintParentUnder(g, host)) { }
        else using (var under = new SolidBrush(OnPanelAlt ? Bcode.App.UI.AppColors.PanelAlt : (Parent?.BackColor ?? BackColor))) g.FillRectangle(under, ClientRectangle);
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = CornerRadius is null ? Height / 2 : Bcode.App.UI.UiMode.IsNative ? Bcode.App.UI.NativeSkin.ButtonRadius(this) : CornerRadius.Value;   // chế độ App: bo góc theo Template giao diện
        // Đường kính bo phải NHỎ HƠN cạnh ngắn: RoundedRect gặp 2×bán kính ≥ chiều cao thì vẽ hình chữ nhật thường → nút thấp + bo góc lớn
        // (Template × hệ số màn hình) thành nút VUÔNG GÓC, lẫn giữa các nút bo tròn (viền vuông "thừa").
        radius = Math.Max(1, Math.Min(radius, (Math.Min(bounds.Height, bounds.Width) - 1) / 2));

        var fill = IsPrimary
            ? (_pressed ? Bcode.App.UI.AppColors.Accent : _hovered ? Bcode.App.UI.AppColors.AccentHover : Bcode.App.UI.AppColors.Accent)
            : (_pressed ? Bcode.App.UI.AppColors.Selection : _hovered ? Bcode.App.UI.AppColors.PanelAlt : Bcode.App.UI.AppColors.ButtonBack);
        // Hover moves the border to the accent on a secondary button — same cue as the HTML
        // .btn:hover rule, which is what tells you the thing is clickable at all.
        var borderColor = IsPrimary || _hovered ? Bcode.App.UI.AppColors.Accent : Bcode.App.UI.AppColors.Border;
        var textColor = IsPrimary ? Bcode.App.UI.AppColors.OnAccent : Bcode.App.UI.AppColors.Text;
        if (!IsPrimary)
        {
            if (BackOverride is { } bo && !_hovered && !_pressed) fill = bo;
            if (ForeOverride is { } fo) textColor = fo;
        }

        if (!Enabled)
        {
            // Stock Button greys itself out; an owner-drawn one has to say so itself, or a
            // disabled Save/Add Script looks exactly like an enabled one.
            fill = Bcode.App.UI.AppColors.PanelAlt;
            borderColor = Bcode.App.UI.AppColors.Border;
            textColor = Bcode.App.UI.AppColors.TextMuted;
        }

        using var path = Bcode.App.UI.FlatToolStripRenderer.RoundedRect(bounds, radius);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        using (var pen = new Pen(borderColor, Bcode.App.UI.UiMode.IsNative ? Bcode.App.UI.NativeSkin.BorderWidth(this) : 1f)) g.DrawPath(pen, path);

        var contentX = CornerRadius is null ? 12 : 10;
        if (Icon is { } glyph)
        {
            var iconBox = string.IsNullOrEmpty(Text) ? new Rectangle((Width - Height) / 2, 0, Height, Height) : new Rectangle(contentX, 0, Height, Height);   // nút chỉ có icon: căn giữa
            Bcode.App.UI.IconGlyphs.Draw(g, glyph, iconBox, textColor, fill);
            contentX += Height - 8;
        }

        if (!string.IsNullOrEmpty(Text))
        {
            // A pill with an icon reads as icon-then-label, so its text is left-aligned; a
            // plain flat button centers, matching the HTML .btn.
            var centered = Icon is null && CornerRadius is not null && !LeftAlign;
            var rightPad = (CornerRadius is null ? 12 : 8) + (Chevron ? Font.Height : 0);
            var textRect = centered
                ? new Rectangle(0, 0, Width, Height)
                : new Rectangle(LeftAlign && Icon is null ? 10 : contentX, 0, Width - (LeftAlign && Icon is null ? 10 : contentX) - rightPad, Height);
            TextRenderer.DrawText(g, Text, Font, textRect, textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis |
                (centered ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left));
        }
        if (Chevron)
            TextRenderer.DrawText(g, "▾", Font, new Rectangle(Width - Font.Height - 8, 0, Font.Height + 4, Height), Enabled ? Bcode.App.UI.AppColors.TextMuted : textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter);
    }
}
