namespace Bcode.App.UI;

/// <summary>
/// Applies a flat/dark (or flat/light) skin on top of plain WinForms controls,
/// without an external UI library. Call Apply(form) once the form's controls
/// exist (ThemedForm does this automatically on Load), and call Apply again
/// on any control tree added dynamically afterwards (e.g. a new document tab).
/// </summary>
/// <summary>Đánh dấu control (và cả cây con) không để ThemeManager.Apply tô màu theme — tự giữ màu riêng.</summary>
public interface IThemeOptOut { }

public static class ThemeManager
{
    private static Font _baseFont = UiTemplate.BuildBaseFont();
    /// <summary>Font gốc: lấy từ template giao diện (UiTemplate) và nhân hệ số UiScale; đổi khi một trong hai đổi.</summary>
    public static Font BaseFont => _baseFont;
    /// <summary>Bản đậm của <see cref="BaseFont"/>, lấy từ cache font của UiTemplate — trước đây mỗi lần Apply tạo <c>new Font(BaseFont, Bold)</c>
    /// cho từng control đậm / từng lưới và không ai giải phóng (rò handle GDI mỗi lần mở tab hay đổi Template).</summary>
    public static Font BoldFont => UiTemplate.GetFont(_baseFont.FontFamily.Name, _baseFont.Size, true);

    /// <summary>Font pt của WinForms dựng theo DPI hệ thống. WinForms chỉ tự nhân font cho màn khác DPI lúc tạo handle / lúc đổi DPI — gán font cho control
    /// ĐÃ có handle nằm trên màn khác DPI (Áp dụng theme, đổi UiScale khi kéo sang màn phụ) thì chữ giữ cỡ của màn chính. Trả bản đã nhân tỉ lệ DPI.</summary>
    public static Font ForDpi(Control c, Font f) =>
        !c.IsHandleCreated || c.DeviceDpi == UiScale.SystemDpi ? f : UiTemplate.GetFont(f.FontFamily.Name, f.Size * c.DeviceDpi / UiScale.SystemDpi, f.Bold);

    static ThemeManager()
    {
        void Rebuild()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var log = new System.Text.StringBuilder();
            _baseFont = UiTemplate.BuildBaseFont();
            // Duyệt bản chụp: áp theme có thể làm một form tạo lại / tạo mới handle → Application.OpenForms đổi giữa chừng
            // ("Collection was modified; enumeration operation may not execute" khi bấm Áp dụng trong Template).
            foreach (var f in Application.OpenForms.Cast<Form>().ToArray())
            {
                if (f.IsDisposed) continue;
                var t0 = sw.ElapsedMilliseconds;
                ApplyBatched(f);
                f.Invalidate(true);
                log.AppendLine($"  {f.GetType().Name}: Apply {sw.ElapsedMilliseconds - t0} ms");
            }
            var t1 = sw.ElapsedMilliseconds;
            ThemeChanged?.Invoke(); // palette/font đổi: các control tự theo dõi (cây, thanh web, editor...) vẽ lại
            log.AppendLine($"  ThemeChanged (các control tự vẽ lại): {sw.ElapsedMilliseconds - t1} ms");
            LogTiming($"ThemeManager.Rebuild: {sw.ElapsedMilliseconds} ms\n" + log);
        }
        UiScale.Changed += Rebuild;
        UiTemplate.Changed += Rebuild;
        // Đổi sáng/tối: Toggle chỉ Apply cho cửa sổ chính → tô lại thanh tiêu đề của mọi cửa sổ đang mở (1 đăng ký tĩnh, không giữ form nào).
        ThemeChanged += () => { if (UiMode.IsNative) foreach (var f in Application.OpenForms.Cast<Form>().ToArray()) ApplyTitleBar(f); };
    }

    // ---- Thanh tiêu đề cửa sổ theo theme (chế độ App) ----
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19, DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35, DWMWA_TEXT_COLOR = 36;
    private static readonly bool IsWin11 = Environment.OSVersion.Version.Build >= 22000;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, object> _titleHooked = new();

    /// <summary>Thanh tiêu đề Windows theo theme: bật chế độ tối của DWM (Windows 10 1809+); Windows 11 tô luôn nền / chữ / viền khung bằng màu
    /// PanelAlt / Text / Border cho liền với thanh trên. Chỉ là thuộc tính vẽ của Windows — không tốn RAM. Máy cũ không hỗ trợ thì DWM bỏ qua.</summary>
    private static void ApplyTitleBar(Form f)
    {
        if (!f.TopLevel || f.IsDisposed) return;
        if (!_titleHooked.TryGetValue(f, out _))
        {
            _titleHooked.Add(f, new object());
            f.HandleCreated += (_, _) => ApplyTitleBar(f);   // handle tạo lại (đổi ShowInTaskbar...) thì áp lại
        }
        if (!f.IsHandleCreated) return;
        try
        {
            var dark = AppColors.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
            if (IsWin11)
            {
                static int Ref(Color c) => c.R | (c.G << 8) | (c.B << 16);   // COLORREF = 0x00BBGGRR
                int caption = Ref(AppColors.PanelAlt), text = Ref(AppColors.Text), border = Ref(AppColors.Border);
                DwmSetWindowAttribute(f.Handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(f.Handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
                DwmSetWindowAttribute(f.Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
            }
            // Windows 10 chỉ vẽ lại thanh tiêu đề khi khung đổi: báo khung đổi để thấy ngay khi bấm sáng/tối lúc cửa sổ đang hiện.
            else if (f.Visible) SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);   // NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED
        }
        catch { /* dwmapi không có / không hỗ trợ: giữ thanh tiêu đề mặc định */ }
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

    /// <summary>Trạng thái chạy vẽ thành chấm nhỏ ở góc icon đầu tab.</summary>
    public enum TabLive { None, Running, Paused }

    // Icon đầu tab (SqlWatch...) — vẽ bằng IconGlyphs; có Badge thì là ô vuông bo góc tô màu đó + hình trắng, chữ tab cùng tông màu
    // (kiểu tab Profiler đỏ của Fcode). Chấm đỏ = đang chạy, xám = tạm dừng.
    private sealed class TabMark { public IconGlyph Glyph; public Color? Badge; public TabLive Live; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TabPage, TabMark> _tabMarks = new();

    /// <summary>Gắn icon đầu tab cho <paramref name="page"/> (gọi sau khi tab đã nằm trong TabControl). <paramref name="badge"/> = màu riêng
    /// của tab: icon thành ô màu nổi, chữ tab cũng đổi sang màu đó (sáng hơn khi theme tối) — nhìn thanh tab là thấy ngay.
    /// Chỗ cho icon do Windows chừa sẵn qua ImageList của TabControl (ảnh rộng bằng icon, cao 1px — không làm thanh tab cao lên),
    /// nên tab tự rộng ra đúng bằng icon.</summary>
    public static void SetTabIcon(TabPage page, IconGlyph glyph, Color? badge = null)
    {
        _tabMarks.AddOrUpdate(page, new TabMark { Glyph = glyph, Badge = badge });
        if (page.Parent is TabControl tab) EnsureTabIconSpace(tab);
        page.ImageIndex = 0;
        page.Parent?.Invalidate();
    }

    /// <summary>Đổi chấm trạng thái ở góc icon đầu tab (tab chưa gắn icon thì bỏ qua).</summary>
    public static void SetTabLive(TabPage page, TabLive live)
    {
        if (!_tabMarks.TryGetValue(page, out var mark) || mark.Live == live) return;
        mark.Live = live;
        if (page.Parent is TabControl tab && tab.TabPages.IndexOf(page) is var i and >= 0) tab.Invalidate(tab.GetTabRect(i));
    }

    // Bề rộng icon đầu tab (gồm khoảng cách tới chữ) — theo Mật độ × DPI × UiScale như Padding của tab.
    private static int TabIconWidth(TabControl tab) => NativeSkin.Dens(tab, 23);

    private static void EnsureTabIconSpace(TabControl tab)
    {
        var size = new Size(TabIconWidth(tab), 1);
        if (tab.ImageList is { } current && current.ImageSize == size && current.Images.Count > 0) return;
        var list = new ImageList { ImageSize = size, ColorDepth = ColorDepth.Depth32Bit };
        list.Images.Add(new Bitmap(size.Width, size.Height));   // ảnh trong suốt — chỉ để giữ chỗ, icon thật vẽ ở PaintTab
        var old = tab.ImageList;
        tab.ImageList = list;
        old?.Dispose();
    }

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
        if (tab is Bcode.App.Controls.FlatTabControl flat) flat.FitAllTabs(); // có nút ✕ → cần thêm chỗ
        tab.Invalidate();
    }

    // Bố cục trong tab có nút đóng (PaintTab): chữ cách mép trái TabTextLeft (+ TabPinWidth nếu ghim, + TabIconWidth nếu có icon),
    // nút ✕ CloseGlyphSize cách mép phải CloseGlyphMargin, chữ dừng trước nút ✕ TabTextCloseGap. TabMinWidth dùng lại đúng các số này.
    private const int CloseGlyphSize = 14, CloseGlyphMargin = 8, TabTextLeft = 10, TabPinWidth = 12, TabTextCloseGap = 4;

    private static Rectangle GetCloseGlyphRect(Rectangle tabRect) =>
        new(tabRect.Right - CloseGlyphSize - CloseGlyphMargin, tabRect.Top + (tabRect.Height - CloseGlyphSize) / 2, CloseGlyphSize, CloseGlyphSize);

    /// <summary>Font PaintTab vẽ chữ tab: font khu vực "Thanh tab" của Template, không có thì <see cref="BaseFont"/>.</summary>
    private static Font TabFont() =>
        UiTemplate.AreaStyle("tabs") is { } area && UiTemplate.FontOf(area) is { } af ? af : BaseFont;

    /// <summary>Bề rộng tối thiểu (px) để <see cref="PaintTab"/> vẽ đủ chữ của <paramref name="page"/> — đo bằng đúng font và bố cục lúc vẽ
    /// (lề trái, chấm ghim, icon, nút ✕). <see cref="Bcode.App.Controls.FlatTabControl"/> dùng để nới tab mà Windows đo hẹp hơn chữ thật.</summary>
    public static int TabMinWidth(TabControl tab, TabPage page)
    {
        var textW = TextRenderer.MeasureText(page.Text, TabFont(), new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        if (!_closableTabs.ContainsKey(tab)) return textW + NativeSkin.Dens(tab, 12); // chữ căn giữa, chừa chút lề hai bên
        var left = TabTextLeft;
        if (tab is Bcode.App.Controls.FlatTabControl flat && flat.IsPinned(page)) left += TabPinWidth;
        if (_tabMarks.TryGetValue(page, out _)) left += TabIconWidth(tab);
        return left + textW + TabTextCloseGap + CloseGlyphSize + CloseGlyphMargin;
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

    /// <summary>Đăng ký <see cref="ThemeChanged"/> và TỰ GỠ khi <paramref name="owner"/> bị Dispose. Dùng thay cho <c>ThemeChanged += () =&gt; ...</c>:
    /// lambda gắn vào event static mà không gỡ sẽ giữ control/form đã đóng sống mãi (cùng editor, lưới, dữ liệu) → RAM tăng dần khi mở/đóng tab.</summary>
    public static void Subscribe(Control owner, Action handler)
    {
        ThemeChanged += handler;
        owner.Disposed += (_, _) => ThemeChanged -= handler;
    }

    public static void Toggle(Control root)
    {
        AppColors.Current = AppColors.IsDark ? AppColors.Light : AppColors.Dark;
        Bcode.App.Models.AppSettings.LightTheme = !AppColors.IsDark;   // nhớ cho lần mở sau (Program.Main đọc lại)
        Apply(root);
        root.Invalidate(true);
        ThemeChanged?.Invoke();
    }

    /// <summary>Ghi thời gian các bước áp template/theme vào %AppData%\Bcode\apply-timing.log (ghi đè mỗi lần) — để biết bước nào làm app đứng khi bấm Áp dụng.</summary>
    public static void LogTiming(string text)
    {
        try
        {
            var path = Path.Combine(BcodePaths.AppData, "Bcode", "apply-timing.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {text}\n");
        }
        catch { /* log chỉ để chẩn đoán */ }
    }

    /// <summary>Áp theme cho cả cây control với layout tạm hoãn: mỗi lần đổi Font / Padding / chiều cao của 1 control không còn kéo theo một lượt
    /// bố trí lại cả form (trước đây mỗi control tự gây một lượt → hàng nghìn lượt khi bấm Áp dụng trong Template). Gộp thành một lượt ở cuối.</summary>
    public static void ApplyBatched(Control root)
    {
        var all = new List<Control>();
        void Collect(Control c) { all.Add(c); foreach (Control child in c.Controls) Collect(child); }
        Collect(root);
        foreach (var c in all) c.SuspendLayout();
        try { Apply(root); }
        finally
        {
            for (var i = all.Count - 1; i >= 0; i--) all[i].ResumeLayout(false);
            root.PerformLayout();
        }
    }

    private static readonly System.Reflection.PropertyInfo? DoubleBufferedProp = typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Chống nháy khi rê chuột / cuộn (chế độ App): bật double-buffer cho mọi control tự vẽ (trừ WebView2 / Scintilla có cơ chế riêng).</summary>
    private static void EnableDoubleBuffer(Control c)
    {
        var ns = c.GetType().Namespace ?? "";
        if (c is Form || ns.StartsWith("Microsoft.Web") || ns.StartsWith("ScintillaNET")) return;
        try
        {
            DoubleBufferedProp?.SetValue(c, true);
            if (c is TreeView tv && tv.IsHandleCreated) SendMessage(tv.Handle, 0x112C, (IntPtr)0x4, (IntPtr)0x4);   // TVS_EX_DOUBLEBUFFER
            else if (c is ListView lv && lv.IsHandleCreated) SendMessage(lv.Handle, 0x1036, (IntPtr)0x10000, (IntPtr)0x10000);   // LVS_EX_DOUBLEBUFFER
        }
        catch { /* không bật được thì chịu nháy */ }
    }

    /// <summary>Chế độ App: (1) Label / CheckBox / RadioButton không coi '&' là phím tắt (tiêu đề "Create RPT & XML" mất dấu &);
    /// (2) thanh cuộn tối theo theme cho mọi vùng cuộn — cột AutoScroll, lưới (thanh cuộn con của DataGridView), danh sách, cây, ô nhiều dòng.</summary>
    private static void NativePolish(Control c)
    {
        switch (c)
        {
            case Label l: l.UseMnemonic = false; break;
            case ButtonBase b and (CheckBox or RadioButton): b.UseMnemonic = false; break;
        }
        if (c is TextBoxBase or ListBox or ListView or TreeView or DataGridView or ScrollBar || (c is ScrollableControl sc && sc.AutoScroll))
            Bcode.App.Controls.NK.DarkScroll(c);
    }

    public static void Apply(Control root)
    {
        if (root is IThemeOptOut) return;   // control tự giữ màu riêng (vd tab SqlWatch nhúng form của app khác)
        if (Bcode.App.UI.UiMode.IsNative) { EnableDoubleBuffer(root); NativePolish(root); }
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
                if (UiMode.IsNative) ApplyTitleBar(form);   // chế độ Web giữ thanh tiêu đề mặc định
                break;

            case MenuStrip menu:
                menu.BackColor = AppColors.PanelAlt;
                menu.ForeColor = AppColors.Text;
                menu.Renderer = FlatToolStripRenderer.Shared;
                break;

            case StatusStrip status:
                status.BackColor = AppColors.PanelAlt;
                status.ForeColor = AppColors.TextMuted;
                status.Renderer = FlatToolStripRenderer.Shared;
                break;

            case ToolStrip toolStrip:
                toolStrip.BackColor = AppColors.PanelAlt;
                toolStrip.ForeColor = AppColors.Text;
                toolStrip.Renderer = FlatToolStripRenderer.Shared;
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

            case TreeView themed when Bcode.App.Controls.NK.IsThemedTree(themed):
                break;   // cây tự vẽ bằng NK.ThemeTree (icon + màu riêng) — không gắn thêm bộ vẽ chung
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
                tree.ItemHeight = Math.Max(10, tree.Font.Height + NativeSkin.Dens(tree, 6));
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
            c.Font = ForDpi(c, c.Font.Bold ? BoldFont : BaseFont);

        // Style khai báo theo loại control (UiTemplate) — ghi đè lên mặc định của theme ở trên.
        if (UiTemplate.StyleFor(c) is { } custom) ApplyTemplateStyle(c, custom);

        // Style theo KHU VỰC (cây menu, thanh trên...) — cụ thể hơn "theo loại control" nên áp sau cùng.
        // Trừ "Thanh tab": chữ / màu tab do PaintTab (TabFont) và FlatTabControl tự vẽ. Áp vào control thì nó lan xuống NỘI DUNG mọi tab
        // (cây File Lookup, lưới, ô nhập... cùng đậm + đổi màu theo thanh tab, không chỉnh lại được) — cả trực tiếp (AreaOf của control con
        // là "tabs") lẫn gián tiếp (Font đậm của TabControl là font "ambient" của TabPage → quy tắc c.Font.Bold ? BoldFont ở trên giữ đậm luôn).
        if (AreaOf(c) is { } area && area != "tabs" && UiTemplate.AreaStyle(area) is { } areaStyle) ApplyTemplateStyle(c, areaStyle);

        if (c is TreeView treeView) treeView.ItemHeight = Math.Max(10, treeView.Font.Height + NativeSkin.Dens(treeView, 6)); // font có thể vừa đổi ở trên
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
                    grid.ColumnHeadersDefaultCellStyle.Font = UiTemplate.GetFont(gfont.FontFamily.Name, gfont.Size, true);
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
            e.Graphics, e.Node.Text, e.Node.NodeFont ?? e.Node.TreeView?.Font ?? BaseFont, e.Bounds,   // node có font riêng (đậm/thường theo loại file) giữ nguyên khi chọn
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
        button.Padding = new Padding(NativeSkin.Dens(button, 8), NativeSkin.Dens(button, 4), NativeSkin.Dens(button, 8), NativeSkin.Dens(button, 4)); // Không gian thở xung quanh chữ
        button.MinimumSize = new Size(NativeSkin.Px(button, 88), NativeSkin.Dens(button, 34)); // Đảm bảo nút đủ to dễ bấm
        button.Margin = new Padding(NativeSkin.Dens(button, 4)); // Khoảng cách giữa các nút rộng hơn
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
        tab.Padding = new Point(NativeSkin.Dens(tab, 18), NativeSkin.Dens(tab, 7)); // theo Mật độ × DPI × UiScale — đặt lại mỗi lần Apply
        if (tab.ImageList is not null) EnsureTabIconSpace(tab);                    // chỗ cho icon đầu tab cũng theo tỉ lệ mới
        if (tab is Bcode.App.Controls.FlatTabControl flat) flat.FitAllTabs();       // font / mật độ / tỉ lệ vừa đổi → đo lại bề rộng tab theo chữ vẽ
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
        var tabFont = TabFont();
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
        if (_tabMarks.TryGetValue(page, out var badgeMark) && badgeMark.Badge is { } badgeColor)
            fore = AppColors.IsDark ? Mix(badgeColor, Color.White, 0.35f) : Mix(badgeColor, Color.Black, 0.15f);   // chữ tab cùng màu icon
        if (!closable)
        {
            TextRenderer.DrawText(g, page.Text, tabFont, bounds, fore,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            return;
        }

        var closeRect = GetCloseGlyphRect(bounds);
        var pinned = tab is Bcode.App.Controls.FlatTabControl flat && flat.IsPinned(page);
        var textLeft = bounds.X + TabTextLeft;
        if (pinned)
        {
            // Chấm màu nhấn đầu tab = tab đã ghim.
            using var pin = new SolidBrush(AppColors.Accent);
            var dot = new Rectangle(bounds.X + 9, bounds.Y + (bounds.Height - 7) / 2, 7, 7);
            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(pin, dot);
            g.SmoothingMode = oldMode;
            textLeft += TabPinWidth;
        }
        if (_tabMarks.TryGetValue(page, out var mark))
        {
            var back = selected || hovered ? AppColors.Panel : (areaBack ?? AppColors.PanelAlt);
            Rectangle icon;
            if (mark.Badge is { } badge)
            {
                // Ô vuông bo góc tô màu riêng của tab, hình trắng bên trong.
                var side = NativeSkin.Dens(tab, 17);
                icon = new Rectangle(textLeft - NativeSkin.Dens(tab, 1), bounds.Y + (bounds.Height - side) / 2, side, side);
                var oldMode = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var path = RoundedRect(icon, NativeSkin.Dens(tab, 4)))
                using (var fill = new SolidBrush(badge))
                    g.FillPath(fill, path);
                g.SmoothingMode = oldMode;
                IconGlyphs.Draw(g, mark.Glyph, Rectangle.Inflate(icon, NativeSkin.Dens(tab, 2), NativeSkin.Dens(tab, 2)), Color.White, badge);
            }
            else
            {
                var iconSize = NativeSkin.Dens(tab, 21);
                icon = new Rectangle(textLeft - NativeSkin.Dens(tab, 2), bounds.Y + (bounds.Height - iconSize) / 2, iconSize, iconSize);
                IconGlyphs.Draw(g, mark.Glyph, icon, AppColors.Accent, back);
            }
            if (mark.Live != TabLive.None)
            {
                // Chấm trạng thái ở góc trên phải icon, viền màu nền tab cho tách khỏi icon.
                var d = NativeSkin.Dens(tab, 7);
                var dot = mark.Badge is null
                    ? new Rectangle(icon.Right - d - NativeSkin.Dens(tab, 1), icon.Y + NativeSkin.Dens(tab, 3), d, d)
                    : new Rectangle(icon.Right - d + NativeSkin.Dens(tab, 3), icon.Y - NativeSkin.Dens(tab, 3), d, d);
                var oldMode = g.SmoothingMode;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var ring = new SolidBrush(back)) g.FillEllipse(ring, Rectangle.Inflate(dot, NativeSkin.Dens(tab, 1), NativeSkin.Dens(tab, 1)));
                using (var fill = new SolidBrush(mark.Live == TabLive.Running ? AppColors.Danger : AppColors.TextMuted)) g.FillEllipse(fill, dot);
                g.SmoothingMode = oldMode;
            }
            textLeft += TabIconWidth(tab);
        }
        var textRect = new Rectangle(textLeft, bounds.Y, Math.Max(0, closeRect.Left - textLeft - TabTextCloseGap), bounds.Height);
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

    private static Color Mix(Color a, Color b, float t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

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
        menu.Renderer = FlatToolStripRenderer.Shared;
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
        grid.ColumnHeadersDefaultCellStyle.Font = BoldFont;

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
        grid.RowTemplate.Height = NativeSkin.Dens(grid, 28);
        grid.ColumnHeadersHeight = NativeSkin.Dens(grid, 34);
        if (grid.Rows.Count > 0 && grid.Rows.Count <= 3000)
            foreach (DataGridViewRow row in grid.Rows) if (!row.IsNewRow && row.Height != grid.RowTemplate.Height && row.Height <= NativeSkin.Px(grid, 40)) row.Height = grid.RowTemplate.Height;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.EnableResizing;
        grid.DefaultCellStyle.Padding = new Padding(NativeSkin.Px(grid, 6), 0, NativeSkin.Px(grid, 6), 0);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(NativeSkin.Px(grid, 6), 0, NativeSkin.Px(grid, 6), 0);
        grid.RowHeadersWidth = NativeSkin.Px(grid, 28);
    }
}
