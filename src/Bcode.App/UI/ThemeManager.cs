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

    // Tab controls opted into a ✕ close button per tab (e.g. MainForm's document
    // area) — keyed to the callback that actually removes the tab, so this stays
    // generic instead of hardcoding one specific TabControl.
    private static readonly Dictionary<TabControl, Action<int>> _closableTabs = new();

    /// <summary>Draws a ✕ on every tab of <paramref name="tab"/> and calls
    /// <paramref name="onCloseRequested"/>(index) when it's clicked — used for
    /// MainForm's document tabs, which previously had no way to close a tab at all
    /// (SQL Object / Command / File Lookup tabs just kept piling up).</summary>
    public static void MakeClosable(TabControl tab, Action<int> onCloseRequested)
    {
        var alreadyWired = _closableTabs.ContainsKey(tab);
        _closableTabs[tab] = onCloseRequested;
        if (!alreadyWired)
        {
            tab.MouseDown += ClosableTabMouseDown;
            tab.MouseMove += (_, _) => tab.Invalidate(); // repaint so the ✕ can show a hover state
        }
        tab.Invalidate();
    }

    private static Rectangle GetCloseGlyphRect(Rectangle tabRect)
    {
        const int size = 14;
        return new Rectangle(tabRect.Right - size - 8, tabRect.Top + (tabRect.Height - size) / 2, size, size);
    }

    private static void ClosableTabMouseDown(object? sender, MouseEventArgs e)
    {
        if (sender is not TabControl tab || !_closableTabs.TryGetValue(tab, out var onClose)) return;
        for (var i = 0; i < tab.TabPages.Count; i++)
        {
            if (GetCloseGlyphRect(tab.GetTabRect(i)).Contains(e.Location))
            {
                onClose(i);
                return;
            }
        }
    }

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
                // Fix: the overflow ("»") popup and any ToolStripDropDownButton's dropdown
                // (e.g. RawSqlControl's "Options...") are separate popups, not children in
                // .Controls — Apply()'s recursion never reached them, so they kept the
                // default ProfessionalRenderer's near-invisible dim/gray-on-dark text.
                ApplyMenu(toolStrip.OverflowButton.DropDown);
                foreach (ToolStripItem item in toolStrip.Items)
                    if (item is ToolStripDropDownItem ddItem) ApplyMenu(ddItem.DropDown);
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
                // Plain TreeView draws a SELECTED node with the OS's own system highlight
                // color (usually Windows blue) no matter what ForeColor/BackColor say — that
                // was the actual reason the bee-amber accent barely showed anywhere: the one
                // thing people actually look at while browsing (the highlighted row in
                // WCommand/File Lookup/SQL Object) was never touched by the palette at all.
                // Owner-drawing just the text/background (icons and expand glyphs stay
                // native) lets a selected node use AppColors.Accent instead.
                tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
                tree.DrawNode -= TreeViewDrawNode; // avoid double-subscribing if Apply runs again
                tree.DrawNode += TreeViewDrawNode;
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
                if (grid.ContextMenuStrip is { } gridMenu) ApplyMenu(gridMenu);
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

        // Any control's own right-click menu (ContextMenuStrip) is, like the ToolStrip
        // overflow/dropdown popups above, not a child in .Controls — recursion alone never
        // reaches it, so it needs styling explicitly wherever one is attached.
        if (c.ContextMenuStrip is { } ownContextMenu) ApplyMenu(ownContextMenu);

        if (c.Font.FontFamily.Name != "Consolas")
            c.Font = c.Font.Bold ? new Font(BaseFont, FontStyle.Bold) : BaseFont;
    }

    /// <summary>Owner-draw handler wired in StyleControl's TreeView case — paints a selected
    /// node's text with a solid AppColors.Accent background (dark text on top, for contrast
    /// against the light amber) instead of the OS's native highlight color. Any other node
    /// state (including hot-tracking while collapsed/expanded) falls back to the default
    /// drawing, which already picks up each node's own ForeColor (WCommandTreeControl/
    /// FileLookupControl color individual nodes by file type) and the TreeView's ForeColor.</summary>
    private static void TreeViewDrawNode(object? sender, DrawTreeNodeEventArgs e)
    {
        if ((e.State & TreeNodeStates.Selected) == 0)
        {
            e.DrawDefault = true;
            return;
        }

        using var back = new SolidBrush(AppColors.Accent);
        e.Graphics.FillRectangle(back, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics, e.Node.Text, e.Node.TreeView?.Font ?? BaseFont, e.Bounds,
            Color.FromArgb(28, 22, 8), // dark honey-brown text — readable on the amber fill
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    /// <summary>Set <c>button.Tag = "primary"</c> before Apply() runs (or any time after —
    /// re-running Apply picks it up) to mark a button as the main action in its group (e.g.
    /// File Lookup's "Load"/"Search") — it gets a solid accent fill instead of the flat
    /// gray every other button uses, so the one action that actually does something on that
    /// row/dialog reads as such at a glance instead of every button looking equally
    /// important.</summary>
    private static void StyleButton(Button button)
    {
        var isPrimary = button.Tag as string == "primary";
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = isPrimary ? AppColors.Accent : AppColors.Border;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.MouseOverBackColor = AppColors.AccentHover;
        button.FlatAppearance.MouseDownBackColor = AppColors.Accent;
        button.BackColor = isPrimary ? AppColors.Accent : AppColors.ButtonBack;
        button.ForeColor = isPrimary ? Color.White : AppColors.Text;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;

        // --- UX/UI HIỆN ĐẠI (CHỐNG CẮT CHỮ) ---
        button.AutoSize = true; // Tự động giãn chiều rộng nếu chữ dài
        button.AutoSizeMode = AutoSizeMode.GrowOnly; // Không bị bóp méo
        button.Padding = new Padding(8, 4, 8, 4); // Không gian thở xung quanh chữ
        button.MinimumSize = new Size(88, 34); // Đảm bảo nút đủ to dễ bấm
        button.Margin = new Padding(4); // Khoảng cách giữa các nút rộng hơn
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
        var closable = _closableTabs.ContainsKey(tab);

        using (var bg = new SolidBrush(selected ? AppColors.Panel : AppColors.PanelAlt))
            e.Graphics.FillRectangle(bg, bounds);

        if (selected)
        {
            using var accent = new SolidBrush(AppColors.Accent);
            e.Graphics.FillRectangle(accent, bounds.X, bounds.Bottom - 3, bounds.Width, 3);
        }

        if (closable)
        {
            var textRect = new Rectangle(bounds.X + 4, bounds.Y, bounds.Width - 22, bounds.Height);
            TextRenderer.DrawText(e.Graphics, page.Text, BaseFont, textRect,
                selected ? AppColors.Text : AppColors.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            var closeRect = GetCloseGlyphRect(bounds);
            var hot = closeRect.Contains(tab.PointToClient(Cursor.Position));
            TextRenderer.DrawText(e.Graphics, "✕", BaseFont, closeRect,
                hot ? AppColors.Accent : AppColors.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, page.Text, BaseFont, bounds,
                selected ? AppColors.Text : AppColors.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    /// <summary>
    /// Themes a popup menu (ContextMenuStrip, a ToolStrip's overflow dropdown, or a
    /// ToolStripDropDownButton's DropDown) — none of these are child Controls, so
    /// Apply()'s normal .Controls recursion never visits them, and left alone they render
    /// with the default ProfessionalRenderer's colors: usually a light popup with dim/gray
    /// text that reads as barely-visible ("mờ") once the rest of the app is dark-themed.
    /// Safe to call more than once (e.g. on every theme toggle) — it just re-applies colors.
    /// </summary>
    public static void ApplyMenu(ToolStripDropDown menu)
    {
        menu.BackColor = AppColors.PanelAlt;
        menu.ForeColor = AppColors.Text;
        menu.Renderer = new FlatToolStripRenderer();
        ApplyMenuItemColors(menu.Items);
    }

    private static void ApplyMenuItemColors(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
        {
            item.ForeColor = AppColors.Text;
            item.BackColor = AppColors.PanelAlt;
            if (item is ToolStripDropDownItem { HasDropDownItems: true } ddItem)
                ApplyMenu(ddItem.DropDown);
        }
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
        
        // --- UX/UI HIỆN ĐẠI ---
        grid.RowTemplate.Height = 28;  // Tăng chiều cao dòng (Cũ là 24) cho dữ liệu thoáng hơn
        grid.ColumnHeadersHeight = 34; // Header cao và rõ ràng hơn
    }
}
