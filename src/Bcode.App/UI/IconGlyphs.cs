using System.Drawing.Drawing2D;

namespace Bcode.App.UI;

public enum IconGlyph
{
    Table,      // SQL Object — a data grid
    Terminal,   // WCommand — a command prompt
    Phone,      // Mobile
    Gear,       // Settings / File & Actions
    Sun,        // Light theme
    Moon,       // Dark theme
    Star,       // Quick Access
    Plus,       // generic "add"
}

/// <summary>
/// Tiny flat vector icon set drawn purely with GDI+ primitives (lines/arcs/polygons) — no
/// icon font, no embedded PNG. A Segoe MDL2/Fluent icon font would need a codepoint guessed
/// blind (no way to preview it on Bee's machine from here), and a wrong one just renders an
/// empty "tofu" box; hand-drawn vector shapes can't silently go missing like that, so this is
/// what backs IconRailControl and the theme-toggle/settings buttons in the new shell.
/// Every glyph is drawn centered inside <paramref name="bounds"/> with roughly a 20% margin,
/// stroke width scales a little with the box so it still reads at both toolbar and rail sizes.
/// </summary>
public static class IconGlyphs
{
    public static void Draw(Graphics g, IconGlyph glyph, Rectangle bounds, Color color, Color backColor)
    {
        var oldSmoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var size = Math.Min(bounds.Width, bounds.Height);
        var margin = (int)(size * 0.22);
        var box = new Rectangle(
            bounds.X + (bounds.Width - size) / 2 + margin,
            bounds.Y + (bounds.Height - size) / 2 + margin,
            size - margin * 2,
            size - margin * 2);
        var stroke = Math.Max(1.4f, size * 0.09f);

        using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);

        switch (glyph)
        {
            case IconGlyph.Table: DrawTable(g, pen, box); break;
            case IconGlyph.Terminal: DrawTerminal(g, pen, box); break;
            case IconGlyph.Phone: DrawPhone(g, pen, box); break;
            case IconGlyph.Gear: DrawGear(g, brush, pen, box); break;
            case IconGlyph.Sun: DrawSun(g, pen, brush, box); break;
            case IconGlyph.Moon: DrawMoon(g, brush, backColor, box); break;
            case IconGlyph.Star: DrawStar(g, brush, box); break;
            case IconGlyph.Plus: DrawPlus(g, pen, box); break;
        }

        g.SmoothingMode = oldSmoothing;
    }

    private static void DrawTable(Graphics g, Pen pen, Rectangle box)
    {
        using var path = FlatToolStripRenderer.RoundedRect(box, Math.Max(2, box.Width / 8));
        g.DrawPath(pen, path);
        var headerY = box.Y + box.Height / 3;
        g.DrawLine(pen, box.X, headerY, box.Right, headerY);
        var midX = box.X + box.Width / 2;
        g.DrawLine(pen, midX, headerY, midX, box.Bottom);
    }

    private static void DrawTerminal(Graphics g, Pen pen, Rectangle box)
    {
        using var path = FlatToolStripRenderer.RoundedRect(box, Math.Max(2, box.Width / 8));
        g.DrawPath(pen, path);
        var cx = box.X + box.Width * 0.28f;
        var cy = box.Y + box.Height * 0.38f;
        var cy2 = box.Y + box.Height * 0.62f;
        g.DrawLine(pen, cx, cy, cx + box.Width * 0.18f, (cy + cy2) / 2);
        g.DrawLine(pen, cx, cy2, cx + box.Width * 0.18f, (cy + cy2) / 2);
        var lineY = box.Y + box.Height * 0.72f;
        g.DrawLine(pen, box.X + box.Width * 0.52f, lineY, box.Right - box.Width * 0.14f, lineY);
    }

    private static void DrawPhone(Graphics g, Pen pen, Rectangle box)
    {
        var phoneWidth = box.Width * 0.6f;
        var rect = new RectangleF(box.X + (box.Width - phoneWidth) / 2, box.Y, phoneWidth, box.Height);
        using var path = FlatToolStripRenderer.RoundedRect(Rectangle.Round(rect), Math.Max(2, (int)(phoneWidth / 4)));
        g.DrawPath(pen, path);
        var homeY = rect.Bottom - rect.Height * 0.12f;
        g.DrawLine(pen, rect.X + rect.Width * 0.3f, homeY, rect.Right - rect.Width * 0.3f, homeY);
    }

    private static void DrawGear(Graphics g, Brush brush, Pen pen, Rectangle box)
    {
        var center = new PointF(box.X + box.Width / 2f, box.Y + box.Height / 2f);
        var outerR = box.Width / 2f;
        var innerR = outerR * 0.55f;
        const int teeth = 8;
        for (var i = 0; i < teeth; i++)
        {
            var angle = i * (Math.PI * 2 / teeth);
            var tx = center.X + (float)Math.Cos(angle) * outerR;
            var ty = center.Y + (float)Math.Sin(angle) * outerR;
            var toothSize = outerR * 0.34f;
            g.FillEllipse(brush, tx - toothSize / 2, ty - toothSize / 2, toothSize, toothSize);
        }
        using var ring = new Pen(pen.Color, pen.Width) { LineJoin = LineJoin.Round };
        g.FillEllipse(brush, center.X - innerR, center.Y - innerR, innerR * 2, innerR * 2);
        var holeR = innerR * 0.45f;
        // "punch" the center hole using the caller's supplied background isn't safe here (Fill
        // uses a solid color, not the real backdrop), so the hole is drawn as a ring instead —
        // an outlined circle reads clearly as a gear hub either way.
        g.DrawEllipse(ring, center.X - holeR, center.Y - holeR, holeR * 2, holeR * 2);
    }

    private static void DrawSun(Graphics g, Pen pen, Brush brush, Rectangle box)
    {
        var center = new PointF(box.X + box.Width / 2f, box.Y + box.Height / 2f);
        var coreR = box.Width * 0.22f;
        g.FillEllipse(brush, center.X - coreR, center.Y - coreR, coreR * 2, coreR * 2);
        var rayInner = coreR * 1.6f;
        var rayOuter = box.Width / 2f;
        for (var i = 0; i < 8; i++)
        {
            var angle = i * (Math.PI * 2 / 8);
            var x1 = center.X + (float)Math.Cos(angle) * rayInner;
            var y1 = center.Y + (float)Math.Sin(angle) * rayInner;
            var x2 = center.X + (float)Math.Cos(angle) * rayOuter;
            var y2 = center.Y + (float)Math.Sin(angle) * rayOuter;
            g.DrawLine(pen, x1, y1, x2, y2);
        }
    }

    private static void DrawMoon(Graphics g, Brush brush, Color backColor, Rectangle box)
    {
        var r = box.Width / 2f;
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        g.FillEllipse(brush, cx - r, cy - r, r * 2, r * 2);
        using var cutout = new SolidBrush(backColor);
        var offset = r * 0.55f;
        g.FillEllipse(cutout, cx - r + offset, cy - r - offset * 0.25f, r * 2, r * 2);
    }

    private static void DrawStar(Graphics g, Brush brush, Rectangle box)
    {
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        var outerR = box.Width / 2f;
        var innerR = outerR * 0.42f;
        var points = new PointF[10];
        for (var i = 0; i < 10; i++)
        {
            var r = i % 2 == 0 ? outerR : innerR;
            var angle = -Math.PI / 2 + i * (Math.PI / 5);
            points[i] = new PointF(cx + (float)Math.Cos(angle) * r, cy + (float)Math.Sin(angle) * r);
        }
        g.FillPolygon(brush, points);
    }

    private static void DrawPlus(Graphics g, Pen pen, Rectangle box)
    {
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        g.DrawLine(pen, box.X, cy, box.Right, cy);
        g.DrawLine(pen, cx, box.Y, cx, box.Bottom);
    }
}
