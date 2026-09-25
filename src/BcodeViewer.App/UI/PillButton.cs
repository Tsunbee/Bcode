using System.Drawing.Drawing2D;

namespace BcodeViewer.App.UI;

/// <summary>
/// Flat, rounded-corner button — same visual language Bcode.App uses for its own toolbar
/// actions (Controls/PillButton.cs over there). A stock WinForms <see cref="Button"/> can't
/// get rounded corners just from FlatAppearance, so this subclass owner-draws its own
/// background/border/text instead, reading <see cref="AppColors"/> live in
/// <see cref="OnPaint"/> so it re-colors itself correctly on every theme change with no extra
/// wiring — the same trick HintCodeForm's category badges already use via its own private
/// RoundedRect helper.
///
/// This is a standalone copy rather than a reference to Bcode.App.Controls.PillButton — the
/// two projects have no project reference in either direction by design (see
/// RecentFilesStore's doc comment), and this version deliberately drops the Icon/IconGlyph
/// support that copy has, since BcodeViewer.App.UI has no equivalent icon set: every place
/// this is used today (HintCodeForm's New/Save/Delete/Insert/Export/Refresh row) is
/// text-only.
/// </summary>
public class PillButton : Button
{
    public bool IsPrimary { get; set; }

    /// <summary>A button shaped like Bcode.App's HTML <c>.btn</c> (shell.css) — 6px corner
    /// radius, same padding rhythm. Use this instead of <c>new Button</c> for any action row
    /// in a dialog that also hosts WebView2-rendered chrome (like HintCodeForm's Monaco code
    /// box) so the native buttons don't read as a leftover from a different, older app.</summary>
    public static PillButton Flat(string text, bool primary = false)
    {
        var button = new PillButton { Text = text, IsPrimary = primary, _autoWidth = true };
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

    /// <summary>Measures the pill's ideal width from its text so callers can just call
    /// Flat(...) once instead of guessing a fixed Width.</summary>
    public void AutoSizeToContent()
    {
        using var g = CreateGraphics();
        var textSize = string.IsNullOrEmpty(Text) ? Size.Empty : g.MeasureString(Text, Font).ToSize();
        const int padding = 12; // shell.css .btn's horizontal padding rhythm
        Width = padding + textSize.Width + padding;
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var g = pevent.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        const int radius = 6;

        var fill = IsPrimary
            ? (_pressed ? AppColors.Accent : _hovered ? AppColors.AccentHover : AppColors.Accent)
            : (_pressed ? AppColors.Selection : _hovered ? AppColors.PanelAlt : AppColors.ButtonBack);
        // Hover moves the border to the accent on a secondary button — same cue as the HTML
        // .btn:hover rule, which is what tells you the thing is clickable at all.
        var borderColor = IsPrimary || _hovered ? AppColors.Accent : AppColors.Border;
        var textColor = IsPrimary ? AppColors.AccentText : AppColors.Text;

        if (!Enabled)
        {
            // Stock Button greys itself out; an owner-drawn one has to say so itself, or a
            // disabled Delete looks exactly like an enabled one.
            fill = AppColors.PanelAlt;
            borderColor = AppColors.Border;
            textColor = AppColors.TextMuted;
        }

        using var path = RoundedRect(bounds, radius);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, path);
        using (var pen = new Pen(borderColor)) g.DrawPath(pen, path);

        if (!string.IsNullOrEmpty(Text))
        {
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}