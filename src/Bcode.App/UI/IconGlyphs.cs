using System.Drawing.Drawing2D;

namespace Bcode.App.UI;

public enum IconGlyph
{
    Database,   // SQL Object — hình trụ database (procedure / function / view / bảng)
    MenuTree,   // Cây menu Fast (WCommand) — nút gốc + hai nhánh con
    Phone,      // Fast Mobile (dvcommand) — điện thoại
    Gear,       // Settings / File & Actions
    Sun,        // Light theme
    Moon,       // Dark theme
    Star,       // Quick Access
    Plus,       // generic "add"
    Refresh,    // Refresh database — vòng cung có mũi tên
    Bell,       // Thông báo y/c FSG
    Eye,        // SqlWatch (đầu tab) — con mắt theo dõi câu SQL
    FolderSearch, // File Lookup (đầu tab) — thư mục + kính lúp
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
            case IconGlyph.Database: DrawDatabase(g, box); break;
            case IconGlyph.MenuTree: DrawMenuTree(g, box); break;
            case IconGlyph.Phone: DrawPhone(g, box); break;
            case IconGlyph.Gear: DrawGear(g, brush, pen, box); break;
            case IconGlyph.Sun: DrawSun(g, pen, brush, box); break;
            case IconGlyph.Moon: DrawMoon(g, brush, backColor, box); break;
            case IconGlyph.Star: DrawStar(g, brush, box); break;
            case IconGlyph.Plus: DrawPlus(g, pen, box); break;
            case IconGlyph.Refresh: DrawRefresh(g, pen, brush, box); break;
            case IconGlyph.Bell: DrawBell(g, pen, brush, box); break;
            case IconGlyph.Eye: DrawEye(g, pen, brush, box); break;
            case IconGlyph.FolderSearch: DrawFolderSearch(g, pen, backColor, box); break;
        }

        g.SmoothingMode = oldSmoothing;
    }

    // Database / MenuTree / Phone: icon MÀU (màu cố định, không theo màu chữ — mục đang chọn của cột icon nhận biết bằng
    // nền + vạch cam), vẽ theo lưới 16x16 trùng toạ độ / màu bản SVG của Web/Shell/iconrail.html — sửa hình thì sửa cả hai.
    private static PointF P(Rectangle box, float x, float y) => new(box.X + x * box.Width / 16f, box.Y + y * box.Height / 16f);
    private static RectangleF R(Rectangle box, float x, float y, float w, float h) =>
        new(box.X + x * box.Width / 16f, box.Y + y * box.Height / 16f, w * box.Width / 16f, h * box.Height / 16f);
    private static float U(Rectangle box, float v) => v * box.Width / 16f;
    private static Color Hex(int rgb, int alpha = 255) => Color.FromArgb(alpha, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static GraphicsPath RoundRectF(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0.5f) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static void DrawDatabase(Graphics g, Rectangle box)
    {
        // Trụ database xanh: thân đổ bóng trái-phải (sáng ở giữa), hai vạch đĩa sáng, nắp elip sáng có viền đậm.
        var outline = Hex(0x16437f);
        using var edge = new Pen(outline, Math.Max(1f, U(box, 0.6f)));
        using (var body = new GraphicsPath())
        {
            body.AddLine(P(box, 2.5f, 3.5f), P(box, 2.5f, 12.5f));
            body.AddArc(R(box, 2.5f, 10.5f, 11, 4), 180, -180);
            body.AddLine(P(box, 13.5f, 12.5f), P(box, 13.5f, 3.5f));
            body.CloseFigure();
            var span = R(box, 2.5f, 0, 11, 16);
            using var shade = new LinearGradientBrush(new RectangleF(span.X - 0.5f, span.Y, span.Width + 1f, span.Height), Hex(0x1d5bb8), Hex(0x1a4f9e), LinearGradientMode.Horizontal)
            {
                InterpolationColors = new ColorBlend { Colors = new[] { Hex(0x1d5bb8), Hex(0x4c94ea), Hex(0x1a4f9e) }, Positions = new[] { 0f, 0.45f, 1f } },
            };
            g.FillPath(shade, body);
            g.DrawPath(edge, body);
        }
        using (var band = new Pen(Hex(0xcfe6ff, 217), Math.Max(1f, U(box, 0.7f))))
        {
            g.DrawArc(band, R(box, 2.5f, 4.5f, 11, 4), 0, 180);
            g.DrawArc(band, R(box, 2.5f, 7.5f, 11, 4), 0, 180);
        }
        var lid = R(box, 2.5f, 1.5f, 11, 4);
        using (var lidBrush = new SolidBrush(Hex(0xa6d4ff))) g.FillEllipse(lidBrush, lid);
        g.DrawEllipse(edge, lid);
    }

    private static void DrawMenuTree(Graphics g, Rectangle box)
    {
        // Thư mục gốc vàng; thân nối xám rẽ hai nhánh sang hai mục menu (nút xanh dương / xanh lá có vạch nhãn trắng).
        using (var line = new Pen(Hex(0x8b929c), Math.Max(1f, U(box, 1f))) { LineJoin = LineJoin.Miter })
        {
            g.DrawLines(line, new[] { P(box, 3, 7), P(box, 3, 13.5f), P(box, 5.6f, 13.5f) });
            g.DrawLine(line, P(box, 3, 10), P(box, 5.6f, 10));
        }
        using (var back = new GraphicsPath())
        {
            back.AddLines(new[] { P(box, 1, 1.5f), P(box, 4.1f, 1.5f), P(box, 5.1f, 2.6f), P(box, 9, 2.6f), P(box, 9, 7), P(box, 1, 7) });
            back.CloseFigure();
            using var b = new SolidBrush(Hex(0xd18b0f));
            g.FillPath(b, back);
        }
        using (var front = RoundRectF(R(box, 1, 3.6f, 8, 3.4f), U(box, 0.5f)))
        using (var f = new SolidBrush(Hex(0xffc53d)))
            g.FillPath(f, front);
        using var label = new Pen(Color.White, Math.Max(1f, U(box, 0.9f))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        foreach (var (y, rgb) in new[] { (8.5f, 0x2f80ed), (12f, 0x27a265) })
        {
            using var item = RoundRectF(R(box, 6, y, 9, 3), U(box, 0.7f));
            using var ib = new SolidBrush(Hex(rgb));
            g.FillPath(ib, item);
            g.DrawLine(label, P(box, 7.4f, y + 1.5f), P(box, 12.6f, y + 1.5f));
        }
    }

    private static void DrawPhone(Graphics g, Rectangle box)
    {
        // Điện thoại: thân xám đậm viền sáng, màn hình xanh chuyển sắc có 4 ô ứng dụng, khe loa và nút home.
        using (var body = RoundRectF(R(box, 3.8f, 0.8f, 8.4f, 14.4f), U(box, 1.8f)))
        using (var fill = new SolidBrush(Hex(0x2d3340)))
        using (var edge = new Pen(Hex(0x8a93a3), Math.Max(1f, U(box, 0.6f))))
        {
            g.FillPath(fill, body);
            g.DrawPath(edge, body);
        }
        var screen = R(box, 4.9f, 2.6f, 6.2f, 9.6f);
        using (var sp = RoundRectF(screen, U(box, 0.5f)))
        using (var sb = new LinearGradientBrush(RectangleF.Inflate(screen, 0, 0.5f), Hex(0x5cc0ff), Hex(0x2563eb), LinearGradientMode.Vertical))
            g.FillPath(sb, sp);
        using (var tile = new SolidBrush(Color.FromArgb(230, Color.White)))
            foreach (var (x, y) in new[] { (5.7f, 3.5f), (8.4f, 3.5f), (5.7f, 6.2f), (8.4f, 6.2f) })
            {
                using var t = RoundRectF(R(box, x, y, 1.9f, 1.9f), U(box, 0.35f));
                g.FillPath(tile, t);
            }
        using (var speaker = new Pen(Hex(0x8a93a3), Math.Max(1f, U(box, 0.5f))) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawLine(speaker, P(box, 7, 1.75f), P(box, 9, 1.75f));
        var c = P(box, 8, 13.7f);
        var hr = Math.Max(1f, U(box, 0.75f));
        using (var home = new SolidBrush(Hex(0xc9cfd8))) g.FillEllipse(home, c.X - hr, c.Y - hr, hr * 2, hr * 2);
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

    private static void DrawBell(Graphics g, Pen pen, Brush brush, Rectangle box)
    {
        // Thân chuông: vòm trên + hai cạnh loe xuống vành; vành ngang; quả lắc tròn bên dưới.
        float l = box.X, t = box.Y, w = box.Width, h = box.Height, cx = l + w / 2f;
        var rimY = t + h * 0.74f;
        using var path = new GraphicsPath();
        path.AddArc(cx - w * 0.28f, t + h * 0.06f, w * 0.56f, h * 0.56f, 180, 180);
        path.AddLine(cx + w * 0.28f, t + h * 0.34f, l + w * 0.92f, rimY);
        path.AddLine(l + w * 0.08f, rimY, cx - w * 0.28f, t + h * 0.34f);
        path.CloseFigure();
        g.DrawPath(pen, path);
        var cr = Math.Max(1.5f, w * 0.11f);
        g.FillEllipse(brush, cx - cr, t + h * 0.84f - cr * 0.6f, cr * 2, cr * 2);
    }

    private static void DrawEye(Graphics g, Pen pen, Brush brush, Rectangle box)
    {
        // Mí mắt: hai đường cong nối hai khoé (trái / phải); con ngươi tròn đặc ở giữa.
        float l = box.X, w = box.Width, cy = box.Y + box.Height / 2f, lid = box.Height * 0.48f;
        using var path = new GraphicsPath();
        path.AddBezier(l, cy, l + w * 0.25f, cy - lid, l + w * 0.75f, cy - lid, l + w, cy);
        path.AddBezier(l + w, cy, l + w * 0.75f, cy + lid, l + w * 0.25f, cy + lid, l, cy);
        path.CloseFigure();
        g.DrawPath(pen, path);
        var r = w * 0.16f;
        g.FillEllipse(brush, l + w / 2f - r, cy - r, r * 2, r * 2);
    }

    private static void DrawFolderSearch(Graphics g, Pen pen, Color backColor, Rectangle box)
    {
        // Thư mục (thân + gờ nhãn bên trái) và kính lúp đè góc dưới phải; mặt kính tô màu nền để cắt nét thư mục phía sau.
        float l = box.X, t = box.Y, w = box.Width, h = box.Height;
        float fr = l + w * 0.86f, fb = t + h * 0.80f, ft = t + h * 0.30f, tabTop = t + h * 0.12f;
        using (var folder = new GraphicsPath())
        {
            folder.AddLine(l, fb, l, tabTop);
            folder.AddLine(l, tabTop, l + w * 0.34f, tabTop);
            folder.AddLine(l + w * 0.34f, tabTop, l + w * 0.46f, ft);
            folder.AddLine(l + w * 0.46f, ft, fr, ft);
            folder.AddLine(fr, ft, fr, fb);
            folder.CloseFigure();
            g.DrawPath(pen, folder);
        }
        var r = w * 0.24f;
        float cx = l + w * 0.70f, cy = t + h * 0.70f;
        var lens = new RectangleF(cx - r, cy - r, r * 2, r * 2);
        using (var cut = new SolidBrush(backColor)) g.FillEllipse(cut, RectangleF.Inflate(lens, pen.Width, pen.Width));
        g.DrawEllipse(pen, lens);
        var k = r * 0.7071f;
        g.DrawLine(pen, cx + k, cy + k, l + w * 1.04f, t + h * 1.04f);
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

    private static void DrawRefresh(Graphics g, Pen pen, Brush brush, Rectangle box)
    {
        // Vòng cung 280° theo chiều kim đồng hồ (hở ở phía trên), mũi tên tam giác ở cuối cung chỉ tiếp theo chiều quay — dáng ↻.
        const float start = -50f, sweep = 280f;
        g.DrawArc(pen, box, start, sweep);
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        var r = box.Width / 2f;
        var a = (start + sweep) * Math.PI / 180;
        float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
        var end = new PointF(cx + cos * r, cy + sin * r);
        float tx = -sin, ty = cos;                         // tiếp tuyến theo chiều kim đồng hồ (trục y hướng xuống)
        var h = Math.Max(3f, box.Width * 0.38f);
        g.FillPolygon(brush, new[]
        {
            new PointF(end.X + tx * h * 0.7f, end.Y + ty * h * 0.7f),
            new PointF(end.X + cos * h * 0.55f, end.Y + sin * h * 0.55f),
            new PointF(end.X - cos * h * 0.55f, end.Y - sin * h * 0.55f),
        });
    }

    private static void DrawPlus(Graphics g, Pen pen, Rectangle box)
    {
        var cx = box.X + box.Width / 2f;
        var cy = box.Y + box.Height / 2f;
        g.DrawLine(pen, box.X, cy, box.Right, cy);
        g.DrawLine(pen, cx, box.Y, cx, box.Bottom);
    }
}
