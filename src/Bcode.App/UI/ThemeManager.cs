namespace Bcode.App.UI;

/// <summary>
/// Applies a flat/dark (or flat/light) skin on top of plain WinForms controls,
/// without an external UI library. Call Apply(form) once the form's controls
/// exist (ThemedForm does this automatically on Load), and call Apply again
/// on any control tree added dynamically afterwards (e.g. a new document tab).
/// </summary>
public static class ThemeManager
{
    public static readonly Font BaseFont = new("Segoe UI", 9.5f);
    public static readonly Font MonoFont = new("Consolas", 10f);

    public static void Toggle(Control root)
    {
        AppColors.Current = AppColors.IsDark ? AppColors.Light : AppColors.Dark;
        Apply(root);
        root.Invalidate(true);
    }

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
                break;

            case ToolStrip toolStrip:
                toolStrip.BackColor = AppColors.PanelAlt;
                toolStrip.ForeColor = AppColors.Text;
                toolStrip.Renderer = new FlatToolStripRenderer();
                toolStrip.GripStyle = ToolStripGripStyle.Hidden;
                break;

            case TabControl tab:
                StyleTabControl(tab);
                break;

            case TabPage page:
                page.BackColor = AppColors.Panel;
                page.ForeColor = AppColors.Text;
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
                richTextBox.BackColor = AppColors.Input;
                richTextBox.ForeColor = AppColors.Text;
                richTextBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case ComboBox combo:
                combo.BackColor = AppColors.Input;
                combo.ForeColor = AppColors.Text;
                combo.FlatStyle = FlatStyle.Flat;
                break;

            case ListBox listBox:
                listBox.BackColor = AppColors.Input;
                listBox.ForeColor = AppColors.Text;
                listBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            case TreeView tree:
                tree.BackColor = AppColors.Panel;
                tree.ForeColor = AppColors.Text;
                tree.BorderStyle = BorderStyle.None;
                break;

            case ListView listView:
                listView.BackColor = AppColors.Panel;
                listView.ForeColor = AppColors.Text;
                listView.BorderStyle = BorderStyle.FixedSingle;
                listView.GridLines = false;
                foreach (ColumnHeader col in listView.Columns) { /* header color follows OS on classic ListView */ }
                break;

            case DataGridView grid:
                StyleGrid(grid);
                break;

            case SplitContainer split:
                split.BackColor = AppColors.Border;
                break;

            case CheckBox or RadioButton:
                c.ForeColor = AppColors.Text;
                c.BackColor = Color.Transparent;
                break;

            case Label label:
                label.ForeColor = AppColors.Text;
                break;

            case Panel or TableLayoutPanel or FlowLayoutPanel or UserControl or GroupBox:
                c.BackColor = AppColors.Background;
                c.ForeColor = AppColors.Text;
                break;
        }

        if (c.Font.FontFamily.Name != "Consolas")
            c.Font = c.Font.Bold ? new Font(BaseFont, FontStyle.Bold) : BaseFont;
    }

    private static void StyleButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = AppColors.Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = AppColors.AccentHover;
        button.FlatAppearance.MouseDownBackColor = AppColors.Accent;
        button.BackColor = AppColors.ButtonBack;
        button.ForeColor = AppColors.Text;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
    }

    private static void StyleTabControl(TabControl tab)
    {
        if (!Equals(tab.Tag, "bcode-themed-tabs"))
        {
            tab.Tag = "bcode-themed-tabs";
            tab.DrawMode = TabDrawMode.OwnerDrawFixed;
            tab.SizeMode = TabSizeMode.Normal;
            tab.Padding = new Point(16, 6);
            tab.DrawItem += TabControlDrawItem;
        }
        tab.Invalidate();
    }

    private static void TabControlDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tab || tab.TabPages.Count == 0) return;
        var page = tab.TabPages[e.Index];
        var bounds = tab.GetTabRect(e.Index);
        var selected = e.Index == tab.SelectedIndex;

        using (var bg = new SolidBrush(selected ? AppColors.Panel : AppColors.PanelAlt))
            e.Graphics.FillRectangle(bg, bounds);

        if (selected)
        {
            using var accent = new SolidBrush(AppColors.Accent);
            e.Graphics.FillRectangle(accent, bounds.X, bounds.Bottom - 3, bounds.Width, 3);
        }

        TextRenderer.DrawText(e.Graphics, page.Text, BaseFont, bounds,
            selected ? AppColors.Text : AppColors.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = AppColors.Panel;
        grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;

        grid.ColumnHeadersDefaultCellStyle.BackColor = AppColors.PanelAlt;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = AppColors.Text;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = AppColors.PanelAlt;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = AppColors.Text;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font(BaseFont, FontStyle.Bold);

        grid.DefaultCellStyle.BackColor = AppColors.Panel;
        grid.DefaultCellStyle.ForeColor = AppColors.Text;
        grid.DefaultCellStyle.SelectionBackColor = AppColors.Selection;
        grid.DefaultCellStyle.SelectionForeColor = AppColors.Text;

        grid.AlternatingRowsDefaultCellStyle.BackColor = AppColors.PanelAlt;
        grid.AlternatingRowsDefaultCellStyle.ForeColor = AppColors.Text;
        grid.AlternatingRowsDefaultCellStyle.SelectionBackColor = AppColors.Selection;
        grid.AlternatingRowsDefaultCellStyle.SelectionForeColor = AppColors.Text;

        grid.RowHeadersDefaultCellStyle.BackColor = AppColors.PanelAlt;
        grid.RowHeadersDefaultCellStyle.ForeColor = AppColors.Text;
        grid.GridColor = AppColors.Border;
        grid.RowTemplate.Height = 24;
    }
}
