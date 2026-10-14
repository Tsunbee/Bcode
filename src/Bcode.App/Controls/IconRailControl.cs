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

    public string? SelectedKey { get; private set; }
    public event Action<string>? SelectedKeyChanged;

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
        Width = S(46);
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

        // subtle divider between the rail and whatever sits to its right
        using (var borderPen = new Pen(Bcode.App.UI.AppColors.Border))
            g.DrawLine(borderPen, Width - 1, 0, Width - 1, Height);

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
                using var barPath = Bcode.App.UI.FlatToolStripRenderer.RoundedRect(new Rectangle(0, bounds.Y + S(9), S(3), bounds.Height - S(18)), S(1));
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
