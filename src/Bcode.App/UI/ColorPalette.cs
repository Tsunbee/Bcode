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

    // Semantic colors — for the few places that need to say "this is a warning / this
    // worked / this failed" instead of just following the neutral chrome. Kept in the
    // palette (not hardcoded at the call site) so both themes can tune them and so a
    // theme toggle actually repaints them.
    public Color Warning = Color.Goldenrod;
    public Color Success = Color.SeaGreen;
    public Color Danger = Color.Firebrick;
    public Color OnAccent = Color.White; // text/glyph color drawn on top of Accent/Warning/Danger fills

    /// <summary>Tên các màu trong bảng màu (khớp biến CSS --bg, --panel... ở shell.css) — nguồn duy nhất cho màn hình chỉnh màu/theme.</summary>
    public static readonly (string Key, string Css, string Label)[] Keys =
    {
        ("Background", "--bg", "Nền chính"),
        ("Panel", "--panel", "Nền khung"),
        ("PanelAlt", "--panel-alt", "Nền thanh/khung phụ"),
        ("Border", "--border", "Viền"),
        ("Text", "--text", "Chữ"),
        ("TextMuted", "--text-muted", "Chữ phụ (mờ)"),
        ("Accent", "--accent", "Màu nhấn"),
        ("AccentHover", "--accent-hover", "Màu nhấn khi rê chuột"),
        ("Selection", "--selection", "Nền mục đang chọn"),
        ("Input", "--input", "Nền ô nhập"),
        ("ButtonBack", "--button-back", "Nền nút"),
        ("Warning", "--warning", "Cảnh báo"),
        ("Success", "--success", "Thành công"),
        ("Danger", "--danger", "Lỗi/nguy hiểm"),
        ("OnAccent", "--on-accent", "Chữ trên màu nhấn"),
    };

    public Color Get(string key) => key switch
    {
        "Background" => Background, "Panel" => Panel, "PanelAlt" => PanelAlt, "Border" => Border, "Text" => Text,
        "TextMuted" => TextMuted, "Accent" => Accent, "AccentHover" => AccentHover, "Selection" => Selection,
        "Input" => Input, "ButtonBack" => ButtonBack, "Warning" => Warning, "Success" => Success, "Danger" => Danger,
        "OnAccent" => OnAccent, _ => Color.Empty,
    };

    public void Set(string key, Color c)
    {
        switch (key)
        {
            case "Background": Background = c; break; case "Panel": Panel = c; break; case "PanelAlt": PanelAlt = c; break;
            case "Border": Border = c; break; case "Text": Text = c; break; case "TextMuted": TextMuted = c; break;
            case "Accent": Accent = c; break; case "AccentHover": AccentHover = c; break; case "Selection": Selection = c; break;
            case "Input": Input = c; break; case "ButtonBack": ButtonBack = c; break; case "Warning": Warning = c; break;
            case "Success": Success = c; break; case "Danger": Danger = c; break; case "OnAccent": OnAccent = c; break;
        }
    }

    public void CopyFrom(ColorPalette other) { foreach (var (k, _, _) in Keys) Set(k, other.Get(k)); }

    public ColorPalette Clone() { var p = new ColorPalette(); p.CopyFrom(this); return p; }
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
        Warning = Color.FromArgb(122, 91, 0),
        Success = Color.FromArgb(56, 142, 60),
        Danger = Color.FromArgb(198, 63, 57),
        OnAccent = Color.FromArgb(28, 22, 8),
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
        Warning = Color.FromArgb(176, 128, 0),
        Success = Color.FromArgb(46, 125, 50),
        Danger = Color.FromArgb(183, 45, 40),
        OnAccent = Color.FromArgb(255, 255, 255),
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
    public static Color Warning => Current.Warning;
    public static Color Success => Current.Success;
    public static Color Danger => Current.Danger;
    public static Color OnAccent => Current.OnAccent;

    public static bool IsDark => Current == Dark;
}
