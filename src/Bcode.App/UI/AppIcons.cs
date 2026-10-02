using System.Drawing.Drawing2D;
using System.Reflection;

namespace Bcode.App.UI;

/// <summary>
/// Bcode's own bee icon/branding — used as the window/taskbar icon (see
/// ThemedForm/MainForm) and as the icon shown in front of every node in the File Lookup
/// tree. Loaded from embedded resources (Assets/bee.ico, Assets/bee_16.png) rather than
/// loose files next to the .exe, so it always ships with the assembly.
/// </summary>
public static class AppIcons
{
    private static Icon? _appIcon;
    private static Bitmap? _fileTreeBitmap;
    private static Bitmap? _folderTreeBitmap;

    /// <summary>The app/window icon (title bar, taskbar, Alt+Tab). Cached after first load.</summary>
    public static Icon? AppIcon
    {
        get
        {
            if (_appIcon is not null) return _appIcon;
            using var stream = OpenResource("bee.ico");
            if (stream is null) return null;
            return _appIcon = new Icon(stream);
        }
    }

    /// <summary>16x16 bee bitmap used as the icon in front of every File Lookup tree node.</summary>
    public static Bitmap? FileTreeBitmap
    {
        get
        {
            if (_fileTreeBitmap is not null) return _fileTreeBitmap;
            using var stream = OpenResource("app.png");
            if (stream is null) return null;
            return _fileTreeBitmap = new Bitmap(stream);
        }
    }

    /// <summary>16x16 folder glyph shown in front of directory nodes in the File Lookup
    /// tree, so a folder reads as a folder at a glance instead of every node (file and
    /// directory alike) sharing the one bee icon. Drawn in code with plain GDI+ shapes —
    /// same "generated, not hand-drawn/borrowed" spirit as the bee icon itself (see
    /// Assets/README.md) — rather than shipping another embedded image asset for
    /// something this simple. Cached after first draw, same pattern as the other icons
    /// here.</summary>
    public static Bitmap? FolderTreeBitmap
    {
        get
        {
            if (_folderTreeBitmap is not null) return _folderTreeBitmap;
            return _folderTreeBitmap = DrawFolderBitmap();
        }
    }

    private static Bitmap? _excelBitmap, _reportBitmap;

    /// <summary>16x16 biểu tượng Excel cho file .xlsx/.xls (ô xanh lá đậm + chữ X trắng, vẽ bằng GDI+ giống cách vẽ icon thư mục) —
    /// để file Excel trong cây File Lookup nhận ra ngay thay vì dùng chung icon con ong.</summary>
    public static Bitmap ExcelTreeBitmap => _excelBitmap ??= DrawExcelBitmap();

    /// <summary>16x16 biểu tượng Crystal Reports cho file .rpt (nền xanh dương + viên pha lê trắng).</summary>
    public static Bitmap ReportTreeBitmap => _reportBitmap ??= DrawReportBitmap();

    private static Bitmap DrawExcelBitmap()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        using var green = new SolidBrush(Color.FromArgb(33, 115, 70));
        using var light = new SolidBrush(Color.FromArgb(185, 225, 200));
        g.FillRectangle(green, 1, 1, 14, 14);
        // Dải sáng bên phải gợi "bảng tính" (các cột).
        g.FillRectangle(light, 11, 3, 3, 10);
        using var font = new Font("Arial", 9.5f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("X", font, Brushes.White, new RectangleF(0, 0, 11.5f, 16), fmt);
        return bmp;
    }

    private static Bitmap DrawReportBitmap()
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bg = new SolidBrush(Color.FromArgb(24, 90, 168));
        g.FillRectangle(bg, 1, 1, 14, 14);

        // Viên pha lê: hình thoi trắng, kèm 1 mặt cắt nhạt để nhìn ra nhiều mặt.
        var gem = new[] { new PointF(8, 2.5f), new PointF(13, 7), new PointF(8, 13.5f), new PointF(3, 7) };
        g.FillPolygon(Brushes.White, gem);
        using var facet = new SolidBrush(Color.FromArgb(150, 200, 245));
        g.FillPolygon(facet, new[] { new PointF(8, 2.5f), new PointF(13, 7), new PointF(8, 7) });
        using var line = new Pen(Color.FromArgb(24, 90, 168), 0.9f);
        g.DrawLine(line, 3, 7, 13, 7);
        g.DrawLine(line, 8, 7, 8, 13.5f);
        return bmp;
    }

    private static Bitmap DrawFolderBitmap()
    {
        var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Warm amber, matching the bee icon's own honey-yellow palette (see
            // ThemeManager's AppColors.Accent) instead of the OS's default folder color —
            // reads as "part of the same app", not a generic system glyph.
            using var fill = new SolidBrush(Color.FromArgb(255, 197, 61));
            using var outline = new Pen(Color.FromArgb(140, 95, 0), 1f);

            // Classic folder silhouette: a small back tab flush against the top-left of
            // the main body — two rectangles is enough to read as a folder at 16px.
            g.FillRectangle(fill, 1, 3, 6, 2);
            g.DrawRectangle(outline, 1, 3, 6, 2);

            var body = new Rectangle(1, 5, 13, 9);
            g.FillRectangle(fill, body);
            g.DrawRectangle(outline, body);
        }
        return bmp;
    }

    private static Stream? OpenResource(string fileName)
    {
        var asm = Assembly.GetExecutingAssembly();
        // Default SDK-style embedded resource naming: <RootNamespace>.<folder>.<file>
        // (path separators become dots), e.g. "Bcode.App.Assets.bee.ico".
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : asm.GetManifestResourceStream(name);
    }
}
