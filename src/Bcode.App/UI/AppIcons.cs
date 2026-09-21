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
