using System.Drawing.Drawing2D;

namespace Bcode.App.UI;

/// <summary>Flat menu/toolbar rendering (no gray Windows 3D bevels) that follows AppColors.</summary>
public class FlatColorTable : ProfessionalColorTable
{
    public override Color ToolStripGradientBegin => AppColors.PanelAlt;
    public override Color ToolStripGradientMiddle => AppColors.PanelAlt;
    public override Color ToolStripGradientEnd => AppColors.PanelAlt;
    public override Color MenuStripGradientBegin => AppColors.PanelAlt;
    public override Color MenuStripGradientEnd => AppColors.PanelAlt;
    public override Color ImageMarginGradientBegin => AppColors.PanelAlt;
    public override Color ImageMarginGradientMiddle => AppColors.PanelAlt;
    public override Color ImageMarginGradientEnd => AppColors.PanelAlt;
    public override Color MenuItemSelected => AppColors.Selection;
    public override Color MenuItemSelectedGradientBegin => AppColors.Selection;
    public override Color MenuItemSelectedGradientEnd => AppColors.Selection;
    public override Color MenuItemPressedGradientBegin => AppColors.Selection;
    public override Color MenuItemPressedGradientEnd => AppColors.Selection;
    public override Color MenuItemBorder => AppColors.Accent;
    public override Color MenuBorder => AppColors.Border;
    public override Color ButtonSelectedHighlight => AppColors.Selection;
    public override Color ButtonSelectedHighlightBorder => AppColors.Accent;
    public override Color ButtonPressedHighlight => AppColors.Selection;
    public override Color ButtonPressedHighlightBorder => AppColors.Accent;
    public override Color SeparatorDark => AppColors.Border;
    public override Color SeparatorLight => AppColors.Border;
    public override Color ToolStripBorder => AppColors.Border;
    public override Color OverflowButtonGradientBegin => AppColors.PanelAlt;
    public override Color OverflowButtonGradientMiddle => AppColors.PanelAlt;
    public override Color OverflowButtonGradientEnd => AppColors.PanelAlt;
}

public class FlatToolStripRenderer : ToolStripProfessionalRenderer
{
    public FlatToolStripRenderer() : base(new FlatColorTable()) { }

    /// <summary>Set <c>toolStripButton.Tag = "primary"</c> to mark the one action in a
    /// toolbar that actually does something (e.g. RawSqlControl's "▶ Execute (F5)") — same
    /// convention as Button's own Tag="primary" in ThemeManager.StyleButton, so it reads as
    /// the main action instead of blending into a row of a dozen equally-gray buttons.</summary>
    private static bool IsPrimary(ToolStripItem item) => item.Tag as string == "primary";

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = IsPrimary(e.Item) ? Color.White : AppColors.Text;
        base.OnRenderItemText(e);
    }

    /// <summary>Builds a rounded rectangle path — the same "pill/chip" corner treatment used
    /// across the new modern shell (IconRailControl, PillButton) so every hover/pressed/
    /// selected surface in the app reads as one consistent visual language instead of the flat
    /// square rectangles WinForms draws by default.</summary>
    internal static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        if (d <= 0 || d >= bounds.Width || d >= bounds.Height)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// A button that's neither hovered nor pressed nor "primary" now draws NO background at
    /// all (idle toolbar buttons used to get a flat rectangle here in some states via the base
    /// renderer — removed so the toolbar reads as a row of plain icons+text until you actually
    /// interact with one, the same restrained idle state Fiddler's own toolbar uses). Hover and
    /// pressed both get a soft rounded-rect fill instead of the old square block, and "primary"
    /// items (Tag="primary") keep their solid accent fill, also now rounded.
    /// </summary>
    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        var isPrimary = IsPrimary(e.Item);
        if (!isPrimary && !e.Item.Selected && !e.Item.Pressed) return; // idle: no fill at all

        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        bounds.Inflate(-1, -1);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var fill = isPrimary
            ? (e.Item.Pressed || e.Item.Selected ? AppColors.AccentHover : AppColors.Accent)
            : (e.Item.Pressed ? AppColors.Selection : AppColors.ButtonBack);

        var g = e.Graphics;
        var oldMode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedRect(bounds, 6);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        g.SmoothingMode = oldMode;
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // no extra border line — keeps the flat look
    }
}
