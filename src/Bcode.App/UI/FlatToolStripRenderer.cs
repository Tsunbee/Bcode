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

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (!IsPrimary(e.Item)) { base.OnRenderButtonBackground(e); return; }
        var bounds = new Rectangle(Point.Empty, e.Item.Size);
        using var brush = new SolidBrush(e.Item.Pressed || e.Item.Selected ? AppColors.AccentHover : AppColors.Accent);
        e.Graphics.FillRectangle(brush, bounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // no extra border line — keeps the flat look
    }
}
