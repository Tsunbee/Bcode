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
    // Accent/AccentHover/Selection below are re-tinted to the bee icon's own colors
    // (sampled straight from Assets/bee.ico: honey amber ~#FEBB00, deeper orange ~#FE8D00,
    // bright gold highlight ~#FCD41E) instead of the previous blue — Background/Panel/
    // Border/Text stay neutral gray/black/white so long code/XML stays readable; only the
    // "brand" accent (buttons, selection, active/hover state) actually reads as bee colors.
    public static readonly ColorPalette Dark = new()
    {
        Background = Color.FromArgb(30, 30, 30),
        Panel = Color.FromArgb(37, 37, 38),
        PanelAlt = Color.FromArgb(45, 45, 48),
        Border = Color.FromArgb(63, 63, 70),
        Text = Color.FromArgb(220, 220, 220),
        TextMuted = Color.FromArgb(150, 150, 150),
        Accent = Color.FromArgb(245, 166, 35),      // honey amber
        AccentHover = Color.FromArgb(255, 193, 61),  // brighter gold on hover
        Selection = Color.FromArgb(92, 62, 9),       // dark honey-brown selection fill
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
        Accent = Color.FromArgb(196, 115, 0),        // deeper amber (contrast on white)
        AccentHover = Color.FromArgb(230, 143, 15),  // lighter amber on hover
        Selection = Color.FromArgb(253, 230, 168),   // pale honey selection fill
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
