namespace Bcode.App.UI;

/// <summary>One full set of theme colors — Dark and Light are the two built-in palettes.</summary>
public class ColorPalette
{
    public Color Background = Color.White;
    public Color Panel = Color.White;
    public Color PanelAlt = Color.White;
    public Color Border = Color.Gray;
    public Color Text = Color.Black;
    public Color TextMuted = Color.Gray;
    public Color Accent = Color.Blue;
    public Color AccentHover = Color.Blue;
    public Color Selection = Color.LightBlue;
    public Color Input = Color.White;
    public Color ButtonBack = Color.Gainsboro;
}

public static class AppColors
{
    public static readonly ColorPalette Dark = new()
    {
        Background = Color.FromArgb(30, 30, 30),
        Panel = Color.FromArgb(37, 37, 38),
        PanelAlt = Color.FromArgb(45, 45, 48),
        Border = Color.FromArgb(63, 63, 70),
        Text = Color.FromArgb(220, 220, 220),
        TextMuted = Color.FromArgb(150, 150, 150),
        Accent = Color.FromArgb(55, 148, 255),
        AccentHover = Color.FromArgb(80, 165, 255),
        Selection = Color.FromArgb(9, 71, 113),
        Input = Color.FromArgb(60, 60, 60),
        ButtonBack = Color.FromArgb(62, 62, 66),
    };

    public static readonly ColorPalette Light = new()
    {
        Background = Color.FromArgb(245, 245, 246),
        Panel = Color.White,
        PanelAlt = Color.FromArgb(233, 236, 239),
        Border = Color.FromArgb(210, 213, 217),
        Text = Color.FromArgb(32, 32, 32),
        TextMuted = Color.FromArgb(110, 110, 110),
        Accent = Color.FromArgb(0, 102, 204),
        AccentHover = Color.FromArgb(30, 130, 230),
        Selection = Color.FromArgb(204, 228, 247),
        Input = Color.White,
        ButtonBack = Color.FromArgb(225, 227, 230),
    };

    public static ColorPalette Current { get; set; } = Dark;

    public static Color Background => Current.Background;
    public static Color Panel => Current.Panel;
    public static Color PanelAlt => Current.PanelAlt;
    public static Color Border => Current.Border;
    public static Color Text => Current.Text;
    public static Color TextMuted => Current.TextMuted;
    public static Color Accent => Current.Accent;
    public static Color AccentHover => Current.AccentHover;
    public static Color Selection => Current.Selection;
    public static Color Input => Current.Input;
    public static Color ButtonBack => Current.ButtonBack;

    public static bool IsDark => Current == Dark;
}
