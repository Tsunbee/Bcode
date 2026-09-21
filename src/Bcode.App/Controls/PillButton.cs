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
        var padding = 16;
        Width = padding + iconWidth + textSize.Width + padding;
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Height / 2;

        var fill = IsPrimary
            ? (_pressed ? Bcode.App.UI.AppColors.Accent : _hovered ? Bcode.App.UI.AppColors.AccentHover : Bcode.App.UI.AppColors.Accent)
            : (_pressed ? Bcode.App.UI.AppColors.Selection : _hovered ? Bcode.App.UI.AppColors.PanelAlt : Bcode.App.UI.AppColors.ButtonBack);
        var borderColor = IsPrimary ? Bcode.App.UI.AppColors.Accent : Bcode.App.UI.AppColors.Border;
        var textColor = IsPrimary ? Color.White : Bcode.App.UI.AppColors.Text;

        using var path = Bcode.App.UI.FlatToolStripRenderer.RoundedRect(bounds, radius);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        using (var pen = new Pen(borderColor)) g.DrawPath(pen, path);

        var contentX = 12;
        if (Icon is { } glyph)
        {
            var iconBox = new Rectangle(contentX, 0, Height, Height);
            Bcode.App.UI.IconGlyphs.Draw(g, glyph, iconBox, textColor, fill);
            contentX += Height - 12;
        }

        if (!string.IsNullOrEmpty(Text))
        {
            var textRect = new Rectangle(contentX, 0, Width - contentX - 12, Height);
            TextRenderer.DrawText(g, Text, Font, textRect, textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }
    }
}
