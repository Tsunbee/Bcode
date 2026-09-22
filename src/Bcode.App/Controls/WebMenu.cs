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