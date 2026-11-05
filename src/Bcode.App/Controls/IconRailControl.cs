using System.Drawing.Drawing2D;

namespace Bcode.App.Controls;

/// <summary>
/// Narrow vertical icon-only navigation rail (~52px wide), the left-edge "activity bar" look
/// used by Fiddler/VS Code-style apps — replaces a plain TabControl's own text tab header as
/// the way to switch MainForm's left panel between SQL Object / WCommand / Mobile. The actual
/// page content controls are untouched; this control only decides which one is visible (see
/// MainForm wiring: SelectedKeyChanged swaps the visible child of a host Panel).
///
/// Inherits Panel (not a plain Control) purely so Bcode.App.UI.ThemeManager's existing
/// "case Panel" branch already sets BackColor/ForeColor here for free — no ThemeManager change
/// needed to pick this control up.
/// </summary>
public class IconRailControl : Panel
{
    public record Item(string Key, Bcode.App.UI.IconGlyph Glyph, string Tooltip);

    private readonly List<Item> _items = new();
    private readonly List<Item> _bottomItems = new(); // pinned to the bottom (e.g. Settings, Theme toggle)
    private readonly ToolTip _toolTip = new() { AutomaticDelay = 350 };
    private string? _hoverKey;
    private string? _lastTooltipKey;

    public const int ItemSize = 38;
    private const int Gap = 3;
    private int S(int v) => (int)Math.Round(v * DeviceDpi / 96.0);   // tự scale theo DPI màn hình

    /// <summary>Kích thước ô khi nằm ngang (một hàng trên cây) — nhỏ hơn dọc để hàng không chiếm nhiều chỗ của cây.</summary>
    public const int HorizontalItemSize = 32;
    private const int HorizontalBarHeight = 40;

    public string? SelectedKey { get; private set; }
    public event Action<string>? SelectedKeyChanged;

    private bool _horizontal;
    /// <summary>true = một hàng ngang nằm trên cây (Dock Top), false = cột dọc sát cạnh cây (Dock Left, mặc định) — Template → Cửa sổ → Cột biểu tượng.</summary>
    public bool Horizontal
    {
        get => _horizontal;
        set
        {
            if (_horizontal == value) return;
            _horizontal = value;
            ApplyOrientation();
        }
    }

    public IconRailControl()
    {
        Width = 46;
        Dock = DockStyle.Left;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyOrientation();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyOrientation();
    }

    private void ApplyOrientation()
    {
        Dock = _horizontal ? DockStyle.Top : DockStyle.Left;
        if (_horizontal) Height = S(HorizontalBarHeight); else Width = S(46);
        Invalidate();
    }

    public void AddItem(string key, Bcode.App.UI.IconGlyph glyph, string tooltip) => _items.Add(new Item(key, glyph, tooltip));

    public void AddBottomItem(string key, Bcode.App.UI.IconGlyph glyph, string tooltip) => _bottomItems.Add(new Item(key, glyph, tooltip));

    public void Select(string key)
    {
        if (SelectedKey == key) return;
        SelectedKey = key;
        Invalidate();
        SelectedKeyChanged?.Invoke(key);
    }

    private IEnumerable<(Item item, Rectangle bounds, bool pinnedBottom)> EnumerateSlots()
    {
        if (_horizontal)
        {
            // Một hàng: mục chính từ trái sang, mục "ghim cuối" dồn về mép phải.
            var size = S(HorizontalItemSize);
            var top = Math.Max(0, (Height - size) / 2);
            var x = S(6);
            foreach (var item in _items)
            {
                yield return (item, new Rectangle(x, top, size, size), false);
                x += size + S(Gap + 1);
            }
            var right = Width - S(6);
            var rightSlots = new List<(Item, Rectangle, bool)>();
            foreach (var item in _bottomItems)
            {
                right -= size;
                rightSlots.Add((item, new Rectangle(right, top, size, size), true));
                right -= S(Gap + 1);
            }
            rightSlots.Reverse();
            foreach (var slot in rightSlots) yield return slot;
            yield break;
        }

        var y = S(Gap + 2);
        foreach (var item in _items)
        {
            yield return (item, new Rectangle(S(4), y, S(ItemSize), S(ItemSize)), false);
            y += S(ItemSize + Gap);
        }

        var bottomY = Height - S(Gap);
        var bottomSlots = new List<(Item, Rectangle, bool)>();
        foreach (var item in _bottomItems)
        {
            bottomY -= S(ItemSize);
            bottomSlots.Add((item, new Rectangle(S(4), bottomY, S(ItemSize), S(ItemSize)), true));
            bottomY -= S(Gap);
        }
        bottomSlots.Reverse();
        foreach (var slot in bottomSlots) yield return slot;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hit = EnumerateSlots().FirstOrDefault(s => s.bounds.Contains(e.Location));
        var newHover = hit.item?.Key;
        if (newHover != _hoverKey)
        {
            _hoverKey = newHover;
            Invalidate();
        }

        if (hit.item is not null && hit.item.Key != _lastTooltipKey)
        {
            _lastTooltipKey = hit.item.Key;
            _toolTip.SetToolTip(this, hit.item.Tooltip);
        }
        else if (hit.item is null)
        {
            _lastTooltipKey = null;
            _toolTip.SetToolTip(this, "");
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverKey is null) return;
        _hoverKey = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var hit = EnumerateSlots().FirstOrDefault(s => s.bounds.Contains(e.Location));
        if (hit.item is not null) Select(hit.item.Key);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var back = new SolidBrush(Bcode.App.UI.AppColors.Background);
        g.FillRectangle(back, ClientRectangle);

        // subtle divider between the rail and whatever sits to its right (dọc) / below it (ngang)
        using (var borderPen = new Pen(Bcode.App.UI.AppColors.Border))
        {
            if (_horizontal) g.DrawLine(borderPen, 0, Height - 1, Width, Height - 1);
            else g.DrawLine(borderPen, Width - 1, 0, Width - 1, Height);
        }

        foreach (var (item, bounds, _) in EnumerateSlots())
        {
            var selected = item.Key == SelectedKey;
            var hovered = item.Key == _hoverKey;

            if (selected)
            {
                using var accentBrush = new SolidBrush(Bcode.App.UI.AppColors.Selection);
                using var path = Bcode.App.UI.FlatToolStripRenderer.RoundedRect(bounds, Bcode.App.UI.NativeSkin.CardRadius(this));
                g.FillPath(accentBrush, path);
                using var barBrush = new SolidBrush(Bcode.App.UI.AppColors.Accent);
                var bar = _horizontal
                    ? new Rectangle(bounds.X + S(7), Height - S(3), bounds.Width - S(14), S(3))   // ngang: vạch dưới ô, sát mép dưới hàng
                    : new Rectangle(0, bounds.Y + S(9), S(3), bounds.Height - S(18));
                using var barPath = Bcode.App.UI.FlatToolStripRenderer.RoundedRect(bar, S(1));
                g.FillPath(barBrush, barPath);
            }
            else if (hovered)
            {
                using var hoverBrush = new SolidBrush(Bcode.App.UI.AppColors.PanelAlt);
                using var path = Bcode.App.UI.FlatToolStripRenderer.RoundedRect(bounds, Bcode.App.UI.NativeSkin.CardRadius(this));
                g.FillPath(hoverBrush, path);
            }

            var iconColor = selected ? Bcode.App.UI.AppColors.Accent
                : hovered ? Bcode.App.UI.AppColors.Text
                : Bcode.App.UI.AppColors.TextMuted;
            Bcode.App.UI.IconGlyphs.Draw(g, item.Glyph, bounds, iconColor, Bcode.App.UI.AppColors.Background);
        }
    }
}
