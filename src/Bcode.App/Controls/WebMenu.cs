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
        public string Kind { get; init; } = "item"; // item | separator | caption
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
        foreach (var entry in _entries)
        {
            if (entry.Kind == "separator") 
            { 
                menu.Items.Add(new ToolStripSeparator()); 
                continue; 
            }
            if (entry.Kind == "caption") 
            { 
                var lbl = new ToolStripLabel(entry.Label) 
                { 
                    Enabled = false, 
                    Font = new Font(ThemeManager.BaseFont, FontStyle.Bold) 
                };
                menu.Items.Add(lbl); 
                continue; 
            }

            var item = new ToolStripMenuItem(entry.Label)
            {
                Enabled = entry.Enabled,
                Checked = entry.Checked,
                ShortcutKeyDisplayString = entry.Shortcut,
            };

            if (entry.Danger)
            {
                item.ForeColor = AppColors.Danger;
            }

            var onClick = entry.OnClick;
            item.Click += (_, _) => onClick?.Invoke();
            menu.Items.Add(item);
        }

        // Áp dụng bảng màu phẳng đồng bộ theo theme hiện tại của ứng dụng
        ThemeManager.ApplyMenu(menu);

        Track(menu);
        menu.Show(screenPoint);
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

    public void Show(Control owner, int clientX, int clientY)
        => Show(owner, owner.PointToScreen(new Point(clientX, clientY)));
}