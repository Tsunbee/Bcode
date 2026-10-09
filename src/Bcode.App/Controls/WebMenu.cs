using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Menu ngữ cảnh tối ưu hóa tốc độ: Sử dụng ContextMenuStrip native được theme đồng bộ tuyệt đối 
/// theo AppColors, loại bỏ hoàn toàn độ trễ khởi tạo của WebView2 khi click chuột phải.
/// </summary>
public sealed class WebMenu
{
    private sealed class MenuEntry
    {
        public string Kind { get; init; } = "item"; // item | separator | caption | sub
        public List<MenuEntry>? Children { get; init; }
        public List<(Color? Color, string Tip, Action OnClick)>? Swatches { get; init; }
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public string? Shortcut { get; init; }
        public bool Enabled { get; init; } = true;
        public bool Checked { get; init; }
        public bool Danger { get; init; }
        public Action? OnClick { get; init; }
    }

    private readonly List<MenuEntry> _entries = new();

    // ContextMenuStrip is its own popup window, tracked by the OS's menu message loop rather
    // than the owner Form's normal message pump — a click that lands on a WebView2 child HWND
    // (a separate Chromium surface) never reaches that loop, so the strip's usual "close on
    // outside click" behavior silently doesn't fire there (reported as the RawSqlControl
    // snippet menu staying open after a left-click back on the Monaco editor). RawSqlControl
    // already listens for a left-click inside the WebView2 and asks to close whatever menu is
    // open; this is what it closes.
    private static ContextMenuStrip? _activeMenu;

    /// <summary>Force-closes whichever menu this class most recently showed, if it's still
    /// open. Safe to call when nothing is open.</summary>
    public static void CloseActive() => _activeMenu?.Close();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private static DateTime _dismissedAt = DateTime.MinValue;

    /// <summary>True nếu menu vừa bị đóng do bấm chuột RA NGOÀI nó (trong ~0,5s). Nút mở menu dùng để coi cú bấm đó là
    /// "bấm lần nữa để đóng" thay vì mở lại ngay (cú bấm vào WebView2 chỉ tới trang SAU khi chuột đã nhấn xuống).</summary>
    public static bool JustDismissed => (DateTime.Now - _dismissedAt).TotalMilliseconds < 500;

    /// <summary>Theo dõi 1 menu đang mở và ĐÓNG nó khi bấm chuột ở bất kỳ đâu ngoài menu (kể cả trên WebView2).
    ///
    /// ContextMenuStrip chỉ tự đóng khi click ngoài nếu click đó đi qua vòng lặp thông điệp của nó; WebView2 là cửa sổ
    /// con của process trình duyệt riêng nên click vào đó (thanh topbar, icon rail, editor...) không bao giờ tới menu →
    /// menu "Actions" mở ra rồi không tắt được. Ở đây dò trạng thái nút chuột toàn hệ thống (GetAsyncKeyState) bằng timer.
    /// Dùng cho mọi menu bật từ trang WebView2.</summary>
    public static void Track(ContextMenuStrip menu)
    {
        _activeMenu = menu;
        var wasDown = true; // menu mở ra do 1 cú bấm: chờ nhả chuột rồi mới tính cú bấm "ra ngoài"
        var timer = new System.Windows.Forms.Timer { Interval = 40 };
        timer.Tick += (_, _) =>
        {
            if (!menu.Visible) { timer.Stop(); return; }
            var down = (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;
            if (down && !wasDown && !IsOverMenu(menu, Cursor.Position))
            {
                _dismissedAt = DateTime.Now;
                menu.Close();
            }
            wasDown = down;
        };
        menu.Closed += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (ReferenceEquals(_activeMenu, menu)) _activeMenu = null;
        };
        timer.Start();
    }

    private static bool IsOverMenu(ToolStripDropDown dropDown, Point screenPoint)
    {
        if (dropDown.Visible && dropDown.Bounds.Contains(screenPoint)) return true;
        foreach (ToolStripItem item in dropDown.Items)
            if (item is ToolStripDropDownItem { HasDropDownItems: true } sub && sub.DropDown.Visible && IsOverMenu(sub.DropDown, screenPoint))
                return true;
        return false;
    }

    public WebMenu Add(string label, Action onClick, string? shortcut = null, bool enabled = true, bool @checked = false, bool danger = false)
    {
        _entries.Add(new MenuEntry
        {
            Id = _entries.Count.ToString(),
            Label = label,
            Shortcut = shortcut,
            Enabled = enabled,
            Checked = @checked,
            Danger = danger,
            OnClick = onClick,
        });
        return this;
    }

    /// <summary>Mục có menu con bung ra bên phải (cây): <paramref name="build"/> thêm các mục con vào menu con được đưa vào.</summary>
    public WebMenu AddSub(string label, Action<WebMenu> build)
    {
        var sub = new WebMenu();
        build(sub);
        _entries.Add(new MenuEntry { Kind = "sub", Label = label, Children = sub._entries });
        return this;
    }

    /// <summary>1 dòng: nhãn bên trái + các chấm màu tròn bấm được bên phải (Color null = chấm "không màu").</summary>
    public WebMenu AddSwatches(string label, params (Color? Color, string Tip, Action OnClick)[] swatches)
    {
        _entries.Add(new MenuEntry { Kind = "swatches", Label = label, Swatches = swatches.ToList() });
        return this;
    }

    public WebMenu AddSeparator()
    {
        _entries.Add(new MenuEntry { Kind = "separator" });
        return this;
    }

    /// <summary>Tiêu đề nhóm không bấm được (ví dụ: "MỞ NHANH", "CỘT"...).</summary>
    public WebMenu AddCaption(string label)
    {
        _entries.Add(new MenuEntry { Kind = "caption", Label = label });
        return this;
    }

    public bool IsEmpty => _entries.Count == 0;

    /// <summary>Hiển thị menu ngay lập tức tại tọa độ màn hình.</summary>
    public void Show(Control owner, Point screenPoint)
    {
        if (_entries.Count == 0) return;
        var host = owner.FindForm();
        if (host is null) return;

        var menu = new ContextMenuStrip();
        FillItems(menu.Items, _entries);

        // Áp dụng bảng màu phẳng đồng bộ theo theme hiện tại của ứng dụng
        ThemeManager.ApplyMenu(menu);

        Track(menu);
        menu.Show(screenPoint);
    }

    private static void FillItems(ToolStripItemCollection items, List<MenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Kind == "separator") { items.Add(new ToolStripSeparator()); continue; }
            if (entry.Kind == "caption")
            {
                items.Add(new ToolStripLabel(entry.Label) { Enabled = false, Font = new Font(ThemeManager.BaseFont, FontStyle.Bold) });
                continue;
            }
            if (entry.Kind == "swatches" && entry.Swatches is { } sw)
            {
                items.Add(new SwatchItem(entry.Label, sw));
                continue;
            }
            var item = new ToolStripMenuItem(entry.Label)
            {
                Enabled = entry.Enabled,
                Checked = entry.Checked,
                ShortcutKeyDisplayString = entry.Shortcut,
            };
            if (entry.Danger) item.ForeColor = AppColors.Danger;
            if (entry.Kind == "sub" && entry.Children is { } kids) FillItems(item.DropDownItems, kids);
            else
            {
                var onClick = entry.OnClick;
                item.Click += (_, _) => onClick?.Invoke();
            }
            items.Add(item);
        }
    }

    /// <summary>Gắn sự kiện click chuột phải vào control để tự động bật menu.</summary>
    public static void AttachTo(Control owner, Func<WebMenu?> build)
    {
        var suppressNative = new ContextMenuStrip();
        suppressNative.Opening += (_, e) => e.Cancel = true;
        owner.ContextMenuStrip = suppressNative;

        owner.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var menu = build();
            if (menu is { IsEmpty: false }) menu.Show(owner, e.X, e.Y);
        };
    }

    /// <summary>Dòng menu "nhãn + các chấm màu": tự vẽ, rê chuột vào chấm thì viền sáng + hiện tên màu, bấm chấm thì chạy hành động rồi đóng menu.</summary>
    private sealed class SwatchItem : ToolStripItem
    {
        private readonly string _label;
        private readonly List<(Color? Color, string Tip, Action OnClick)> _sw;
        private int _hover = -1;
        private const int Dot = 16, Gap = 8, PadRight = 12;

        public SwatchItem(string label, List<(Color? Color, string Tip, Action OnClick)> sw)
        {
            _label = label; _sw = sw; AutoToolTip = false;
        }

        private int LabelWidth => TextRenderer.MeasureText(_label, Font).Width;
        public override Size GetPreferredSize(Size constrainingSize) =>
            new(26 + LabelWidth + 18 + _sw.Count * (Dot + Gap) + PadRight, Math.Max(26, Font.Height + 10));

        private Rectangle DotRect(int i) => new(26 + LabelWidth + 18 + i * (Dot + Gap), (Height - Dot) / 2, Dot, Dot);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(AppColors.PanelAlt)) g.FillRectangle(bg, new Rectangle(Point.Empty, Size));
            TextRenderer.DrawText(g, _label, Font, new Rectangle(26, 0, LabelWidth + 4, Height), AppColors.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            for (var i = 0; i < _sw.Count; i++)
            {
                var r = DotRect(i);
                var color = _sw[i].Color;
                if (color is { } c) { using var br = new SolidBrush(c); g.FillEllipse(br, r); }
                using (var pen = new Pen(i == _hover ? AppColors.Accent : Color.FromArgb(120, AppColors.Text), i == _hover ? 2f : 1f)) g.DrawEllipse(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                if (color is null)
                    using (var slash = new Pen(Color.FromArgb(220, 60, 60), 2f)) g.DrawLine(slash, r.Left + 3, r.Bottom - 4, r.Right - 4, r.Top + 3);
            }
        }

        private int HitTest(Point p) { for (var i = 0; i < _sw.Count; i++) if (DotRect(i).Inflate2(3).Contains(p)) return i; return -1; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var h = HitTest(e.Location);
            if (h == _hover) return;
            _hover = h; ToolTipText = h >= 0 ? _sw[h].Tip : ""; Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover != -1) { _hover = -1; Invalidate(); } }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var h = HitTest(e.Location);
            if (h < 0) return;
            var act = _sw[h].OnClick;
            (GetCurrentParent() as ToolStripDropDown)?.Close(ToolStripDropDownCloseReason.ItemClicked);
            act?.Invoke();
        }
    }

    public void Show(Control owner, int clientX, int clientY)
        => Show(owner, owner.PointToScreen(new Point(clientX, clientY)));
}

internal static class RectExt
{
    public static Rectangle Inflate2(this Rectangle r, int n) { r.Inflate(n, n); return r; }
}
