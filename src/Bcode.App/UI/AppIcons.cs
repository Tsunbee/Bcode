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
            using var stream = OpenResource("bee_16.png");
            if (stream is null) return null;
            return _fileTreeBitmap = new Bitmap(stream);
        }
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
