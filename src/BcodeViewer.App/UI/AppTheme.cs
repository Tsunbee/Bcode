using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BcodeViewer.App.UI;

/// <summary>
/// A small dark palette matching the Monaco "vs-dark" theme already used inside the
/// WebView2 page (see Web/style.css — #1e1e1e/#252526/#0e639c etc.), so the native WinForms
/// chrome around it (tree, toolbar, status bar, Hint Code / Settings dialogs) reads as one
/// app instead of a VSCode-dark editor bolted onto a stock-gray Windows shell. Deliberately
/// a standalone copy rather than referencing Bcode.App's own ThemeManager/AppColors — the
/// two projects have no project reference in either direction by design (BcodeViewer runs
/// as its own process; see MainForm.cs's doc comment) — just a smaller port sized for the
/// handful of control types this app actually uses.
/// </summary>
public static class AppColors
{
    public static readonly Color Background = Color.FromArgb(30, 30, 30);
    public static readonly Color Panel = Color.FromArgb(37, 37, 38);
    public static readonly Color PanelAlt = Color.FromArgb(45, 45, 48);
    public static readonly Color Border = Color.FromArgb(63, 63, 70);
    public static readonly Color Text = Color.FromArgb(212, 212, 212);
    public static readonly Color TextMuted = Color.FromArgb(150, 150, 150);
    public static readonly Color Accent = Color.FromArgb(14, 99, 156);
    public static readonly Color AccentHover = Color.FromArgb(17, 119, 187);
    public static readonly Color Selection = Color.FromArgb(9, 71, 113);
    public static readonly Color Input = Color.FromArgb(60, 60, 60);
    public static readonly Color ButtonBack = Color.FromArgb(62, 62, 66);
}

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

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = AppColors.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        // no extra border line — keeps the flat look
    }

    protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripButton { Enabled: true } button) { base.OnRenderButtonBackground(e); return; }
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        if (button.Pressed || button.Checked)
            using (var b = new SolidBrush(AppColors.Selection)) e.Graphics.FillRectangle(b, rect);
        else if (button.Selected)
            using (var b = new SolidBrush(AppColors.PanelAlt)) e.Graphics.FillRectangle(b, rect);
    }
}

public static class ThemeManager
{
    public static readonly Font BaseFont = new("Segoe UI", 9f);

    public static void Apply(Control root)
    {
        StyleControl(root);
        foreach (Control child in root.Controls)
            Apply(child);
    }

    private static void StyleControl(Control c)
    {
        switch (c)
        {
            case Form form:
                form.BackColor = AppColors.Background;
                form.ForeColor = AppColors.Text;
                break;

            case MenuStrip menu:
                menu.BackColor = AppColors.PanelAlt;
                menu.ForeColor = AppColors.Text;
                menu.Renderer = new FlatToolStripRenderer();
                break;

            case StatusStrip status:
                status.BackColor = AppColors.PanelAlt;
                status.ForeColor = AppColors.TextMuted;
                status.Renderer = new FlatToolStripRenderer();
                status.SizingGrip = false;
                break;

            case ToolStrip toolStrip:
                toolStrip.BackColor = AppColors.PanelAlt;
                toolStrip.ForeColor = AppColors.Text;
                toolStrip.Renderer = new FlatToolStripRenderer();
                toolStrip.GripStyle = ToolStripGripStyle.Hidden;
                foreach (ToolStripItem item in toolStrip.Items)
                    if (item is ToolStripDropDownItem ddItem) ApplyMenu(ddItem.DropDown);
                break;

            case Button button:
                StyleButton(button);
                break;

            case TextBox textBox:
                textBox.BackColor = AppColors.Input;
                textBox.ForeColor = AppColors.Text;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case RichTextBox richTextBox:
                richTextBox.BackColor = AppColors.Panel;
                richTextBox.ForeColor = AppColors.Text;
                richTextBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ComboBox combo:
                combo.BackColor = AppColors.Input;
                combo.ForeColor = AppColors.Text;
                combo.FlatStyle = FlatStyle.Flat;
                break;

            case ListBox listBox:
                listBox.BackColor = AppColors.Panel;
                listBox.ForeColor = AppColors.Text;
                listBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case TreeView tree:
                tree.BackColor = AppColors.Panel;
                tree.ForeColor = AppColors.Text;
                tree.BorderStyle = BorderStyle.None;
                tree.LineColor = AppColors.Border;
                tree.FullRowSelect = true;
                tree.HotTracking = true;
                break;

            case LinkLabel link:
                link.LinkColor = Color.FromArgb(78, 165, 240);
                link.ActiveLinkColor = AppColors.AccentHover;
                link.VisitedLinkColor = Color.FromArgb(78, 165, 240);
                link.BackColor = Color.Transparent;
                break;

            case CheckBox or RadioButton:
                c.ForeColor = AppColors.Text;
                c.BackColor = Color.Transparent;
                break;

            case Label label:
                label.ForeColor = AppColors.Text;
                break;

            case SplitContainer split:
                split.BackColor = AppColors.Border;
                break;

            case Panel or TableLayoutPanel or FlowLayoutPanel:
                c.BackColor = AppColors.Background;
                c.ForeColor = AppColors.Text;
                break;
        }

        if (c.ContextMenuStrip is { } ownContextMenu) ApplyMenu(ownContextMenu);

        if (!c.Font.FontFamily.Name.Equals("Consolas", StringComparison.OrdinalIgnoreCase) && c is not Form)
            c.Font = c.Font.Bold ? new Font(BaseFont, FontStyle.Bold) : BaseFont;
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = AppColors.Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = AppColors.AccentHover;
        button.FlatAppearance.MouseDownBackColor = AppColors.Accent;
        button.BackColor = button.DialogResult == DialogResult.OK ? AppColors.Accent : AppColors.ButtonBack;
        button.ForeColor = AppColors.Text;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    public static void ApplyMenu(ToolStripDropDown menu)
    {
        menu.BackColor = AppColors.PanelAlt;
        menu.ForeColor = AppColors.Text;
        menu.Renderer = new FlatToolStripRenderer();
        foreach (ToolStripItem item in menu.Items)
        {
            item.ForeColor = AppColors.Text;
            item.BackColor = AppColors.PanelAlt;
            if (item is ToolStripDropDownItem { HasDropDownItems: true } ddItem)
                ApplyMenu(ddItem.DropDown);
        }
    }
}
