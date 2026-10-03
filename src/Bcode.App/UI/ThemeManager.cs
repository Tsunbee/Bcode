namespace Bcode.App.UI;

/// <summary>
/// Applies a flat/dark (or flat/light) skin on top of plain WinForms controls,
/// without an external UI library. Call Apply(form) once the form's controls
/// exist (ThemedForm does this automatically on Load), and call Apply again
/// on any control tree added dynamically afterwards (e.g. a new document tab).
/// </summary>
public static class ThemeManager
{
    private static Font _baseFont = UiTemplate.BuildBaseFont();
    /// <summary>Font gốc: lấy từ template giao diện (UiTemplate) và nhân hệ số UiScale; đổi khi một trong hai đổi.</summary>
    public static Font BaseFont => _baseFont;

    static ThemeManager()
    {
        void Rebuild()
        {
            _baseFont = UiTemplate.BuildBaseFont();
            foreach (Form f in Application.OpenForms) { Apply(f); f.Invalidate(true); }
            ThemeChanged?.Invoke(); // palette/font đổi: các control tự theo dõi (cây, thanh web, editor...) vẽ lại
        }
        UiScale.Changed += Rebuild;
        UiTemplate.Changed += Rebuild;
    }
    public static readonly Font MonoFont = new("Consolas", 10f);

    // Tab controls opted into a ✕ close button per tab (e.g. MainForm's document
    // area) — keyed to the callback that actually removes the tab, so this stays
    // generic instead of hardcoding one specific TabControl.
    private static readonly Dictionary<TabControl, Action<int>> _closableTabs = new();
    
    // Lưu lại index của tab hoặc nút đóng ✕ đang được hover để hạn chế Invalidate vô tội vạ (chống nháy)
    private static readonly Dictionary<TabControl, int> _hoveredCloseButtons = new();

    // Tab đang được rê chuột lên (để tô nền hover như thanh tab của BcodeViewer).
    private static readonly Dictionary<TabControl, int> _hoveredTabs = new();

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
            _hoveredCloseButtons[tab] = -1;
            tab.MouseDown += ClosableTabMouseDown;
            tab.MouseMove += ClosableTabMouseMove; 
            tab.MouseLeave += ClosableTabMouseLeave;
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

    private static void ClosableTabMouseMove(object? sender, MouseEventArgs e)
    {
        if (sender is not TabControl tab) return;

        int hoveredClose = -1, hoveredTab = -1;
        for (var i = 0; i < tab.TabPages.Count; i++)
        {
            var rect = tab.GetTabRect(i);
            if (!rect.Contains(e.Location)) continue;
            hoveredTab = i;
            if (GetCloseGlyphRect(rect).Contains(e.Location)) hoveredClose = i;
            break;
        }

        // Chỉ vẽ lại khi trạng thái hover (tab hoặc nút đóng) thật sự đổi — chống nháy.
        _hoveredCloseButtons.TryGetValue(tab, out var lastClose);
        _hoveredTabs.TryGetValue(tab, out var lastTab);
        if (lastClose == hoveredClose && lastTab == hoveredTab && _hoveredTabs.ContainsKey(tab)) return;
        _hoveredCloseButtons[tab] = hoveredClose;
        _hoveredTabs[tab] = hoveredTab;
        tab.Invalidate();
    }

    private static void ClosableTabMouseLeave(object? sender, EventArgs e)
    {
        if (sender is not TabControl tab) return;

        var dirty = (_hoveredCloseButtons.TryGetValue(tab, out var c) && c != -1) || (_hoveredTabs.TryGetValue(tab, out var t) && t != -1);
        _hoveredCloseButtons[tab] = -1;
        _hoveredTabs[tab] = -1;
        if (dirty) tab.Invalidate();
    }
    /// <summary>Raised after every <see cref="Toggle"/> — lets a control that isn't part of
    /// the toggled root's tree (e.g. a per-tab WebView2 toolbar living in a document tab, not
    /// under MainForm's own chrome) still learn the theme changed and re-push it to its own
    /// HTML page. Subscribers MUST unsubscribe on Dispose — this is a static event, so a
    /// forgotten unsubscribe would keep every closed tab's control alive forever.</summary>
    public static event Action? ThemeChanged;

    public static void Toggle(Control root)
    {
        AppColors.Current = AppColors.IsDark ? AppColors.Light : AppColors.Dark;
        Apply(root);
        root.Invalidate(true);
        ThemeChanged?.Invoke();
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

            // Must come before the generic "case Button" below (switch picks the first match,
            // and PillButton IS a Button) — PillButton owner-draws itself entirely from
            // AppColors live in its own OnPaint (see Controls/PillButton.cs), so it needs no
            // color/FlatAppearance setup here, and MUST NOT get StyleButton's AutoSize/
            // MinimumSize/Padding treatment (that's tuned for a normal rectangular Button and
            // would fight PillButton's own fixed/AutoSizeToContent sizing).
            case Bcode.App.Controls.PillButton:
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
                ControlPerf.EnableDoubleBuffering(richTextBox); // Thêm dòng này để cuộn mượt hơn
                break;

            case ComboBox combo:
                combo.BackColor = AppColors.Input;
                combo.ForeColor = AppColors.Text;
                combo.FlatStyle = FlatStyle.Flat;
                break;

            // Also covers CheckedListBox (a ListBox subclass) — SqlQueryControl's column
            // picker and QuickAccessForm's list.
            case ListBox listBox:
                listBox.BackColor = AppColors.Input;
                listBox.ForeColor = AppColors.Text;
                listBox.BorderStyle = BorderStyle.FixedSingle;
                break;

            // NumericUpDown / DateTimePicker keep the OS's own white spinner+calendar chrome
            // unless their colors are set explicitly — same white-on-white problem as above.
            case NumericUpDown numeric:
                numeric.BackColor = AppColors.Input;
                numeric.ForeColor = AppColors.Text;
                numeric.BorderStyle = BorderStyle.FixedSingle;
                break;

            case DateTimePicker picker:
                picker.CalendarMonthBackground = AppColors.Input;
                picker.CalendarForeColor = AppColors.Text;
                picker.CalendarTitleBackColor = AppColors.PanelAlt;
                picker.CalendarTitleForeColor = AppColors.Text;
                break;

            case LinkLabel link:
                link.LinkColor = AppColors.Accent;
                link.ActiveLinkColor = AppColors.AccentHover;
                link.VisitedLinkColor = AppColors.AccentHover;
                link.ForeColor = AppColors.Text;
                break;

            case ProgressBar bar:
                bar.BackColor = AppColors.PanelAlt;
                bar.ForeColor = AppColors.Accent;
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
                // Chiều cao dòng cây theo "Mật độ" — tính theo font hiện tại của cây (áp lại sau khi font đổi ở cuối StyleControl).
                tree.ItemHeight = Math.Max(10, tree.Font.Height + UiTemplate.Dens(6));
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

        // Style khai báo theo loại control (UiTemplate) — ghi đè lên mặc định của theme ở trên.
        if (UiTemplate.StyleFor(c) is { } custom) ApplyTemplateStyle(c, custom);

        // Style theo KHU VỰC (cây menu, thanh tab, thanh trên...) — cụ thể hơn "theo loại control" nên áp sau cùng.
        if (AreaOf(c) is { } area && UiTemplate.AreaStyle(area) is { } areaStyle) ApplyTemplateStyle(c, areaStyle);

        if (c is TreeView treeView) treeView.ItemHeight = Math.Max(10, treeView.Font.Height + UiTemplate.Dens(6)); // font có thể vừa đổi ở trên
    }

    // ---- Khu vực giao diện (UiTemplate.AreaList) ----
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, string> _areas = new();

    /// <summary>Đánh dấu <paramref name="root"/> (và mọi control con của nó) thuộc khu vực <paramref name="area"/> ("top", "tree", "tabs"...).</summary>
    public static void SetArea(Control root, string area) => _areas.AddOrUpdate(root, area);

    /// <summary>Khu vực của control: gần nhất được đánh dấu SetArea trên chuỗi cha, hoặc null.</summary>
    public static string? AreaOf(Control c)
    {
        for (Control? p = c; p is not null; p = p.Parent)
            if (_areas.TryGetValue(p, out var a)) return a;
        return null;
    }

    private static void ApplyTemplateStyle(Control c, ControlStyle s)
    {
        if (c.Font.FontFamily.Name != "Consolas" && UiTemplate.FontOf(s) is { } font) c.Font = font;
        var fore = UiTemplate.ParseColor(s.ForeColor);
        var back = UiTemplate.ParseColor(s.BackColor);

        switch (c)
        {
            case DataGridView grid:
                if (fore is { } gf) { grid.DefaultCellStyle.ForeColor = gf; grid.ColumnHeadersDefaultCellStyle.ForeColor = gf; }
                if (back is { } gb) grid.DefaultCellStyle.BackColor = gb;
                if (UiTemplate.FontOf(s) is { } gfont)
                {
                    grid.DefaultCellStyle.Font = gfont;
                    grid.ColumnHeadersDefaultCellStyle.Font = new Font(gfont, FontStyle.Bold);
                }
                break;
            default:
                if (fore is { } f) c.ForeColor = f;
                if (back is { } b && c is not Bcode.App.Controls.PillButton) c.BackColor = b;
                break;
        }
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
            // Node có BackColor riêng (vd menu mẹ tô theo phân hệ ở WCommandTreeControl): DrawDefault của chế độ OwnerDrawText
            // KHÔNG vẽ nền riêng của node (chỉ dùng ForeColor), nên tự tô nền rồi vẽ chữ ở đây.
            if (!e.Node.BackColor.IsEmpty && e.Node.BackColor != Color.Transparent)
            {
                using var nodeBack = new SolidBrush(e.Node.BackColor);
                e.Graphics.FillRectangle(nodeBack, e.Bounds);
                var fore = e.Node.ForeColor.IsEmpty ? (e.Node.TreeView?.ForeColor ?? AppColors.Text) : e.Node.ForeColor;
                TextRenderer.DrawText(
                    e.Graphics, e.Node.Text, e.Node.NodeFont ?? e.Node.TreeView?.Font ?? BaseFont, e.Bounds, fore,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
                return;
            }
            e.DrawDefault = true;
            return;
        }

        using var back = new SolidBrush(AppColors.Accent);
        e.Graphics.FillRectangle(back, e.Bounds);
        TextRenderer.DrawText(
            e.Graphics, e.Node.Text, e.Node.TreeView?.Font ?? BaseFont, e.Bounds,
            AppColors.OnAccent, // contrasts with the amber fill in either theme
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    /// <summary>Styles a stock WinForms Button. Almost nothing reaches this any more — the
    /// app's buttons are either HTML (Controls/WebActionBar.cs) or owner-drawn
    /// (Controls/PillButton.cs); this stays for third-party/system-created buttons and any
    /// plain Button added later.
    ///
    /// Set <c>button.Tag = "primary"</c> before Apply() runs (or any time after —
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
        button.FlatAppearance.BorderSize = UiTemplate.Current.BorderWidth;
        // A non-primary button must not flip to the full accent fill on hover — its text
        // stays AppColors.Text, which is nearly unreadable on amber. It gets a subtle
        // panel-level hover instead, so only the primary button reads as "the" action.
        button.FlatAppearance.MouseOverBackColor = isPrimary ? AppColors.AccentHover : AppColors.PanelAlt;
        button.FlatAppearance.MouseDownBackColor = isPrimary ? AppColors.Accent : AppColors.Border;
        button.BackColor = isPrimary ? AppColors.Accent : AppColors.ButtonBack;
        button.ForeColor = isPrimary ? AppColors.OnAccent : AppColors.Text;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;

        // --- UX/UI HIỆN ĐẠI (CHỐNG CẮT CHỮ) ---
        button.AutoSize = true; // Tự động giãn chiều rộng nếu chữ dài
        button.AutoSizeMode = AutoSizeMode.GrowOnly; // Không bị bóp méo
        button.Padding = new Padding(UiTemplate.Dens(8), UiTemplate.Dens(4), UiTemplate.Dens(8), UiTemplate.Dens(4)); // Không gian thở xung quanh chữ
        button.MinimumSize = new Size(88, UiTemplate.Dens(34)); // Đảm bảo nút đủ to dễ bấm
        button.Margin = new Padding(UiTemplate.Dens(4)); // Khoảng cách giữa các nút rộng hơn
    }

    private static void StyleTabControl(TabControl tab)
    {
        if (!Equals(tab.Tag, "bcode-themed-tabs"))
        {
            tab.Tag = "bcode-themed-tabs";
            tab.SizeMode = TabSizeMode.Normal;
            if (tab is not Bcode.App.Controls.FlatTabControl)
            {
                tab.DrawMode = TabDrawMode.OwnerDrawFixed;
                tab.DrawItem += TabControlDrawItem;
                ControlPerf.EnableDoubleBuffering(tab);
            }
        }
        tab.Padding = new Point(UiTemplate.Dens(18), UiTemplate.Dens(7)); // theo Mật độ — đặt lại mỗi lần Apply
        tab.Invalidate();
    }

    // Kiểu thanh tab của BcodeViewer (Web/style.css .edTab): phẳng, tab đang chọn liền màu với nội dung bên dưới + vạch màu nhấn 2px
    // ở ĐỈNH, ngăn cách các tab bằng đường kẻ dọc mảnh, nút đóng "×" tròn nhẹ, rê chuột thì sáng nền.
    private static void TabControlDrawItem(object? sender, DrawItemEventArgs e)
    {
        if (sender is TabControl tab) PaintTab(e.Graphics, tab, e.Index);
    }

    /// <summary>Vẽ một tab theo kiểu BcodeViewer — dùng cho TabControl owner-draw thường và cho FlatTabControl tự vẽ.</summary>
    public static void PaintTab(Graphics g, TabControl tab, int index)
    {
        if (index < 0 || index >= tab.TabPages.Count) return;
        var page = tab.TabPages[index];
        var bounds = tab.GetTabRect(index);
        var selected = index == tab.SelectedIndex;
        var closable = _closableTabs.ContainsKey(tab);
        var hovered = _hoveredTabs.TryGetValue(tab, out var ht) && ht == index;

        // Khu vực "Thanh tab" của Template giao diện: font / màu chữ / màu nền thanh tab (tab đang chọn vẫn liền màu với nội dung).
        var area = UiTemplate.AreaStyle("tabs");
        var tabFont = area is not null && UiTemplate.FontOf(area) is { } af ? af : BaseFont;
        var areaFore = UiTemplate.ParseColor(area?.ForeColor);
        var areaBack = UiTemplate.ParseColor(area?.BackColor);

        using (var bg = new SolidBrush(selected || hovered ? AppColors.Panel : (areaBack ?? AppColors.PanelAlt)))
            g.FillRectangle(bg, bounds);

        using (var sep = new Pen(AppColors.Border))
            g.DrawLine(sep, bounds.Right - 1, bounds.Top, bounds.Right - 1, bounds.Bottom);

        if (selected)
        {
            using var accent = new SolidBrush(AppColors.Accent);
            // Vạch màu nhấn của tab đang chọn: ở đỉnh tab (thanh tab trên đầu) hoặc ở đáy tab (thanh tab ở dưới).
            g.FillRectangle(accent, bounds.X, tab.Alignment == TabAlignment.Bottom ? bounds.Bottom - 2 : bounds.Y, bounds.Width - 1, 2);
        }

        var fore = areaFore ?? (selected ? AppColors.Text : AppColors.TextMuted);
        if (!closable)
        {
            TextRenderer.DrawText(g, page.Text, tabFont, bounds, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }

        var closeRect = GetCloseGlyphRect(bounds);
        var pinned = tab is Bcode.App.Controls.FlatTabControl flat && flat.IsPinned(page);
        var textLeft = bounds.X + 10;
        if (pinned)
        {
            // Chấm màu nhấn đầu tab = tab đã ghim.
            using var pin = new SolidBrush(AppColors.Accent);
            var dot = new Rectangle(bounds.X + 9, bounds.Y + (bounds.Height - 7) / 2, 7, 7);
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(pin, dot);
            g.SmoothingMode = oldMode;
            textLeft += 12;
        }
        var textRect = new Rectangle(textLeft, bounds.Y, Math.Max(0, closeRect.Left - textLeft - 4), bounds.Height);
        TextRenderer.DrawText(g, page.Text, tabFont, textRect, fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var hot = _hoveredCloseButtons.TryGetValue(tab, out var hc) && hc == index;
        if (hot)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = RoundedRect(closeRect, Math.Max(0, Math.Min(UiTemplate.Current.CornerRadius, 7)));
            using var hotBrush = new SolidBrush(AppColors.ButtonBack);
            g.FillPath(hotBrush, path);
            g.SmoothingMode = old;
        }
        TextRenderer.DrawText(g, "×", BaseFont, closeRect, hot || selected ? AppColors.Text : AppColors.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
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
        // Chiều cao dòng/tiêu đề theo "Mật độ" của Template giao diện (Gọn 0,8 / Vừa 1 / Thoáng 1,25).
        grid.RowTemplate.Height = UiTemplate.Dens(28);
        grid.ColumnHeadersHeight = UiTemplate.Dens(34);
        if (grid.Rows.Count > 0 && grid.Rows.Count <= 3000)
            foreach (DataGridViewRow row in grid.Rows) if (!row.IsNewRow && row.Height != grid.RowTemplate.Height && row.Height <= 40) row.Height = grid.RowTemplate.Height;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
        grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        grid.RowHeadersWidth = 28;
    }
}
