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

    /// <summary>Corner radius in pixels. Null (the default) keeps the fully-rounded pill used
    /// in the top bar; 6 is the radius the HTML buttons use (shell.css <c>.btn</c>), which is
    /// what <see cref="Flat"/> sets so a native button sitting in a toolbar next to an HTML
    /// one is the same shape.</summary>
    public int? CornerRadius { get; set; }

    /// <summary>A button shaped like the HTML <c>.btn</c> in shell.css — same radius, padding
    /// and colors. Use this instead of <c>new Button</c> anywhere a plain button has to sit in
    /// a toolbar or panel that also contains WebView2-rendered chrome; a stock WinForms Button
    /// cannot round its corners at all, which is what made those rows look like two different
    /// apps glued together.</summary>
    public static PillButton Flat(string text, bool primary = false)
    {
        var button = new PillButton { Text = text, IsPrimary = primary, CornerRadius = 6, _autoWidth = true };
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
        using var g = CreateGraphics();
        var textSize = string.IsNullOrEmpty(Text) ? Size.Empty : g.MeasureString(Text, Font).ToSize();
        var iconWidth = Icon is not null ? Height - 12 + 6 : 0;
        var padding = CornerRadius is null ? 16 : 12; // pills need more breathing room than a 6px-radius button
        Width = padding + iconWidth + textSize.Width + padding;
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = CornerRadius ?? Height / 2;

        var fill = IsPrimary
            ? (_pressed ? Bcode.App.UI.AppColors.Accent : _hovered ? Bcode.App.UI.AppColors.AccentHover : Bcode.App.UI.AppColors.Accent)
            : (_pressed ? Bcode.App.UI.AppColors.Selection : _hovered ? Bcode.App.UI.AppColors.PanelAlt : Bcode.App.UI.AppColors.ButtonBack);
        // Hover moves the border to the accent on a secondary button — same cue as the HTML
        // .btn:hover rule, which is what tells you the thing is clickable at all.
        var borderColor = IsPrimary || _hovered ? Bcode.App.UI.AppColors.Accent : Bcode.App.UI.AppColors.Border;
        var textColor = IsPrimary ? Bcode.App.UI.AppColors.OnAccent : Bcode.App.UI.AppColors.Text;

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
        using (var pen = new Pen(borderColor)) g.DrawPath(pen, path);

        var contentX = CornerRadius is null ? 12 : 10;
        if (Icon is { } glyph)
        {
            var iconBox = new Rectangle(contentX, 0, Height, Height);
            Bcode.App.UI.IconGlyphs.Draw(g, glyph, iconBox, textColor, fill);
            contentX += Height - 12;
        }

        if (!string.IsNullOrEmpty(Text))
        {
            // A pill with an icon reads as icon-then-label, so its text is left-aligned; a
            // plain flat button centers, matching the HTML .btn.
            var centered = Icon is null && CornerRadius is not null;
            var textRect = centered
                ? new Rectangle(0, 0, Width, Height)
                : new Rectangle(contentX, 0, Width - contentX - (CornerRadius is null ? 12 : 8), Height);
            TextRenderer.DrawText(g, Text, Font, textRect, textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix |
                (centered ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left));
        }
    }
}
