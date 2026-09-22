namespace BcodeViewer.App.UI;

/// <summary>
/// One complete look for the whole app. The important word is "whole": BcodeViewer is a
/// WinForms shell wrapped around a WebView2 page, so a theme that only knows about one of
/// them produces the exact thing this replaces — a VSCode-dark editor sitting inside
/// stock-gray Windows chrome. Every colour here is therefore consumed three times:
///
///   * WinForms chrome  — via <see cref="AppColors"/> (tree, toolbar, dialogs, status bar)
///   * the page's CSS   — as custom properties on :root (see Web/theme.js, Web/style.css)
///   * Monaco itself    — as a defineTheme() call built from <see cref="TokenRules"/>
///
/// A theme is data, not code, so adding one is adding an entry to
/// <see cref="ThemeCatalog.All"/> and nothing else.
/// </summary>
public sealed class ThemeDefinition
{
    public required string Id { get; init; }

    /// <summary>What the menu shows. Kept to the names VSCode itself uses where the theme
    /// is a port of one, so "Monokai" means what someone expects it to mean.</summary>
    public required string Name { get; init; }

    /// <summary>Drives the Windows title-bar colour (DWM immersive dark mode), which half
    /// of the Theme menu the entry is listed under, and the default text-on-accent choice.
    /// Not inferred from the background: a theme author can legitimately disagree with the
    /// luminance maths on a borderline colour.</summary>
    public required bool IsDark { get; init; }

    /// <summary>Monaco's own base to inherit from — "vs", "vs-dark" or "hc-black". Rules
    /// below are layered on top, so a base that's already close means fewer rules and, more
    /// importantly, sane defaults for the hundreds of token types not listed.</summary>
    public required string MonacoBase { get; init; }

    // ---- The shared palette (same names AppColors has always exposed) ----
    public required Color Background { get; init; }
    public required Color Panel { get; init; }
    public required Color PanelAlt { get; init; }
    public required Color Border { get; init; }
    public required Color Text { get; init; }
    public required Color TextMuted { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color Selection { get; init; }
    public required Color Input { get; init; }
    public required Color ButtonBack { get; init; }

    /// <summary>Text drawn ON the accent colour (primary buttons, dialog headers). Its own
    /// token because accent is not always dark: several themes here use a deliberately pale
    /// accent (Dracula's lilac, Nord's ice blue, Gruvbox's mustard) that white text simply
    /// disappears into, while Light+ needs white on blue although its body text is near
    /// black. Deriving this from luminance guesses wrong exactly on the colours people
    /// argue about, so each theme states it.</summary>
    public Color AccentText { get; init; } = Color.White;

    /// <summary>Editor gutter/line-number colour; falls back to TextMuted when unset.</summary>
    public Color? LineNumber { get; init; }

    /// <summary>The "unsaved changes" marker on a file in the left tree. Themed rather than
    /// fixed because the amber that reads clearly on #1e1e1e is nearly invisible on a white
    /// sidebar — and this marker is the only thing telling someone they have unsaved work.</summary>
    public Color DirtyMarker { get; init; } = Color.FromArgb(229, 192, 123);

    /// <summary>The current line's highlight in the editor. Null = let Monaco's base decide.</summary>
    public Color? LineHighlight { get; init; }

    /// <summary>Selected text in the editor (distinct from <see cref="Selection"/>, which is
    /// the WinForms list/tree selection).</summary>
    public Color? EditorSelection { get; init; }

    /// <summary>
    /// Monaco token colours. Empty means "inherit the base entirely", which is right for the
    /// three themes that ARE a Monaco base (Dark+/Light+/High Contrast) and wrong for every
    /// port, where leaving this out would give a Dracula window frame around a vs-dark
    /// editor. Built through <see cref="ThemeCatalog.Syntax"/> rather than listed by hand.
    /// </summary>
    public IReadOnlyList<TokenRule> TokenRules { get; init; } = Array.Empty<TokenRule>();

    public sealed record TokenRule(string Token, string Foreground, string? FontStyle = null);
}

public static class ThemeCatalog
{
    public const string DefaultId = "dark-plus";

    /// <summary>
    /// The built-in themes. Ordered dark-first because that's how the Theme menu groups
    /// them (see MainForm.BuildThemeMenu) — the menu reads this order within each group,
    /// so a new entry lands where it's put rather than wherever the array happens to end.
    /// </summary>
    public static readonly IReadOnlyList<ThemeDefinition> All = new[]
    {
        // ---------------------------------------------------------------- dark ----------
        new ThemeDefinition
        {
            Id = DefaultId,
            Name = "Dark+ (mặc định)",
            IsDark = true,
            MonacoBase = "vs-dark",
            // Exactly the palette BcodeViewer shipped with, so switching away and back
            // returns to the look people are already used to rather than an approximation.
            // No token rules for the same reason: vs-dark IS this theme's syntax colouring.
            Background = Rgb(0x1E1E1E),
            Panel = Rgb(0x252526),
            PanelAlt = Rgb(0x2D2D30),
            Border = Rgb(0x3F3F46),
            Text = Rgb(0xD4D4D4),
            TextMuted = Rgb(0x969696),
            Accent = Rgb(0x0E639C),
            AccentHover = Rgb(0x1177BB),
            Selection = Rgb(0x094771),
            Input = Rgb(0x3C3C3C),
            ButtonBack = Rgb(0x3E3E42),
        },

        new ThemeDefinition
        {
            Id = "dracula",
            Name = "Dracula",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x282A36),
            Panel = Rgb(0x21222C),
            PanelAlt = Rgb(0x282A36),
            Border = Rgb(0x44475A),
            Text = Rgb(0xF8F8F2),
            TextMuted = Rgb(0x6272A4),
            Accent = Rgb(0xBD93F9),
            AccentHover = Rgb(0xCFB0FF),
            AccentText = Rgb(0x282A36), // pale lilac accent — white text vanishes into it
            Selection = Rgb(0x44475A),
            Input = Rgb(0x21222C),
            ButtonBack = Rgb(0x343746),
            LineNumber = Rgb(0x6272A4),
            LineHighlight = Rgb(0x313341),
            EditorSelection = Rgb(0x44475A),
            TokenRules = Syntax(
                comment: "6272A4", str: "F1FA8C", number: "BD93F9", keyword: "FF79C6",
                op: "FF79C6", type: "8BE9FD", tag: "FF79C6", attrName: "50FA7B",
                attrValue: "F1FA8C", func: "50FA7B", identifier: "F8F8F2", delimiter: "F8F8F2"),
        },

        new ThemeDefinition
        {
            Id = "one-dark",
            Name = "One Dark Pro",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x282C34),
            Panel = Rgb(0x21252B),
            PanelAlt = Rgb(0x2C313A),
            Border = Rgb(0x3E4451),
            Text = Rgb(0xABB2BF),
            TextMuted = Rgb(0x7F848E),
            Accent = Rgb(0x61AFEF),
            AccentHover = Rgb(0x7CC0FF),
            AccentText = Rgb(0x282C34),
            Selection = Rgb(0x3E4451),
            Input = Rgb(0x1B1D23),
            ButtonBack = Rgb(0x3A3F4B),
            LineNumber = Rgb(0x5C6370),
            LineHighlight = Rgb(0x2C313C),
            EditorSelection = Rgb(0x3E4451),
            TokenRules = Syntax(
                comment: "5C6370", str: "98C379", number: "D19A66", keyword: "C678DD",
                op: "56B6C2", type: "E5C07B", tag: "E06C75", attrName: "D19A66",
                attrValue: "98C379", func: "61AFEF", identifier: "ABB2BF", delimiter: "ABB2BF"),
        },

        new ThemeDefinition
        {
            Id = "nord",
            Name = "Nord",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x2E3440),
            Panel = Rgb(0x2B303B),
            PanelAlt = Rgb(0x3B4252),
            Border = Rgb(0x434C5E),
            Text = Rgb(0xD8DEE9),
            TextMuted = Rgb(0x7B88A1),
            Accent = Rgb(0x88C0D0),
            AccentHover = Rgb(0x9FD3E3),
            AccentText = Rgb(0x2E3440),
            Selection = Rgb(0x434C5E),
            Input = Rgb(0x3B4252),
            ButtonBack = Rgb(0x434C5E),
            LineNumber = Rgb(0x616E88),
            LineHighlight = Rgb(0x353C4A),
            EditorSelection = Rgb(0x434C5E),
            TokenRules = Syntax(
                comment: "616E88", str: "A3BE8C", number: "B48EAD", keyword: "81A1C1",
                op: "81A1C1", type: "8FBCBB", tag: "81A1C1", attrName: "8FBCBB",
                attrValue: "A3BE8C", func: "88C0D0", identifier: "D8DEE9", delimiter: "ECEFF4"),
        },

        new ThemeDefinition
        {
            Id = "tokyo-night",
            Name = "Tokyo Night",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x1A1B26),
            Panel = Rgb(0x16161E),
            PanelAlt = Rgb(0x1F2335),
            Border = Rgb(0x292E42),
            Text = Rgb(0xA9B1D6),
            // Lighter than Tokyo Night's own #565F89, which is its COMMENT colour: comments
            // are meant to recede inside the editor (and still use it below), but the same
            // value as UI muted text — status bar, breadcrumb, tree metadata — only reaches
            // 2.8:1 on this background and stops being readable.
            TextMuted = Rgb(0x787C99),
            Accent = Rgb(0x7AA2F7),
            AccentHover = Rgb(0x96B6FF),
            AccentText = Rgb(0x1A1B26),
            Selection = Rgb(0x283457),
            Input = Rgb(0x1F2335),
            ButtonBack = Rgb(0x292E42),
            LineNumber = Rgb(0x3B4261),
            LineHighlight = Rgb(0x1F2335),
            EditorSelection = Rgb(0x283457),
            TokenRules = Syntax(
                comment: "565F89", str: "9ECE6A", number: "FF9E64", keyword: "BB9AF7",
                op: "89DDFF", type: "2AC3DE", tag: "F7768E", attrName: "BB9AF7",
                attrValue: "9ECE6A", func: "7AA2F7", identifier: "C0CAF5", delimiter: "89DDFF"),
        },

        new ThemeDefinition
        {
            Id = "monokai",
            Name = "Monokai",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x272822),
            Panel = Rgb(0x1E1F1C),
            PanelAlt = Rgb(0x272822),
            Border = Rgb(0x414339),
            Text = Rgb(0xF8F8F2),
            TextMuted = Rgb(0x90918B),
            // Monokai's signature #F92672 carries white text at only 3.8:1, so it moves to
            // the hover state (where it reads as the colour brightening under the cursor)
            // and the resting accent is a deeper shade of the same pink at 5.9:1.
            Accent = Rgb(0xD6216A),
            AccentHover = Rgb(0xF92672),
            Selection = Rgb(0x49483E),
            Input = Rgb(0x3B3C35),
            ButtonBack = Rgb(0x3B3C35),
            LineNumber = Rgb(0x90918B),
            LineHighlight = Rgb(0x3E3D32),
            EditorSelection = Rgb(0x49483E),
            TokenRules = Syntax(
                comment: "75715E", str: "E6DB74", number: "AE81FF", keyword: "F92672",
                op: "F92672", type: "66D9EF", tag: "F92672", attrName: "A6E22E",
                attrValue: "E6DB74", func: "A6E22E", identifier: "F8F8F2", delimiter: "F8F8F2",
                italicType: true),
        },

        new ThemeDefinition
        {
            Id = "gruvbox-dark",
            Name = "Gruvbox Dark",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x282828),
            Panel = Rgb(0x1D2021),
            PanelAlt = Rgb(0x32302F),
            Border = Rgb(0x504945),
            Text = Rgb(0xEBDBB2),
            TextMuted = Rgb(0xA89984),
            Accent = Rgb(0xD79921),
            AccentHover = Rgb(0xFABD2F),
            AccentText = Rgb(0x282828),
            Selection = Rgb(0x504945),
            Input = Rgb(0x3C3836),
            ButtonBack = Rgb(0x3C3836),
            LineNumber = Rgb(0x7C6F64),
            LineHighlight = Rgb(0x32302F),
            EditorSelection = Rgb(0x504945),
            TokenRules = Syntax(
                comment: "928374", str: "B8BB26", number: "D3869B", keyword: "FB4934",
                op: "FE8019", type: "FABD2F", tag: "8EC07C", attrName: "FABD2F",
                attrValue: "B8BB26", func: "B8BB26", identifier: "EBDBB2", delimiter: "EBDBB2"),
        },

        new ThemeDefinition
        {
            Id = "night-owl",
            Name = "Night Owl",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x011627),
            Panel = Rgb(0x001122),
            PanelAlt = Rgb(0x0B2942),
            Border = Rgb(0x1D3B53),
            Text = Rgb(0xD6DEEB),
            TextMuted = Rgb(0x5F7E97),
            Accent = Rgb(0x7E57C2),
            AccentHover = Rgb(0x9575D6),
            Selection = Rgb(0x1D3B53),
            Input = Rgb(0x0B2942),
            ButtonBack = Rgb(0x1D3B53),
            LineNumber = Rgb(0x4B6479),
            LineHighlight = Rgb(0x0B2942),
            EditorSelection = Rgb(0x1D3B53),
            TokenRules = Syntax(
                comment: "637777", str: "ECC48D", number: "F78C6C", keyword: "C792EA",
                op: "C792EA", type: "FFCB8B", tag: "7FDBCA", attrName: "ADDB67",
                attrValue: "ECC48D", func: "82AAFF", identifier: "D6DEEB", delimiter: "D6DEEB"),
        },

        new ThemeDefinition
        {
            Id = "solarized-dark",
            Name = "Solarized Dark",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x002B36),
            Panel = Rgb(0x00212B),
            PanelAlt = Rgb(0x073642),
            Border = Rgb(0x094757),
            Text = Rgb(0x93A1A1),
            TextMuted = Rgb(0x657B83),
            // Same story as Monokai's pink: Solarized's blue #268BD2 only carries white at
            // 3.7:1, so it becomes the hover and the resting accent is a step darker.
            Accent = Rgb(0x1C6FA8),
            AccentHover = Rgb(0x268BD2),
            Selection = Rgb(0x0B4A5A),
            Input = Rgb(0x073642),
            ButtonBack = Rgb(0x073642),
            LineNumber = Rgb(0x586E75),
            LineHighlight = Rgb(0x073642),
            EditorSelection = Rgb(0x0B4A5A),
            TokenRules = Syntax(
                comment: "586E75", str: "2AA198", number: "D33682", keyword: "859900",
                op: "859900", type: "B58900", tag: "268BD2", attrName: "93A1A1",
                attrValue: "2AA198", func: "268BD2", identifier: "93A1A1", delimiter: "93A1A1"),
        },

        new ThemeDefinition
        {
            Id = "github-dark",
            Name = "GitHub Dark",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x0D1117),
            Panel = Rgb(0x010409),
            PanelAlt = Rgb(0x161B22),
            Border = Rgb(0x30363D),
            Text = Rgb(0xE6EDF3),
            TextMuted = Rgb(0x8B949E),
            Accent = Rgb(0x238636),
            AccentHover = Rgb(0x2EA043),
            Selection = Rgb(0x163356),
            Input = Rgb(0x0D1117),
            ButtonBack = Rgb(0x21262D),
            LineNumber = Rgb(0x6E7681),
            LineHighlight = Rgb(0x161B22),
            EditorSelection = Rgb(0x163356),
            TokenRules = Syntax(
                comment: "8B949E", str: "A5D6FF", number: "79C0FF", keyword: "FF7B72",
                op: "FF7B72", type: "FFA657", tag: "7EE787", attrName: "79C0FF",
                attrValue: "A5D6FF", func: "D2A8FF", identifier: "E6EDF3", delimiter: "E6EDF3",
                italicComment: false),
        },

        new ThemeDefinition
        {
            Id = "high-contrast",
            Name = "High Contrast Dark",
            IsDark = true,
            MonacoBase = "hc-black",
            // Pure black with a cyan edge — the point of this one is legibility on a bad
            // monitor or in a bright room, so borders are loud on purpose rather than the
            // near-invisible hairlines the other dark themes use. No token rules: hc-black
            // already tunes its syntax colours for contrast, and overriding them with a
            // port's palette would undo exactly what this theme is for.
            Background = Rgb(0x000000),
            Panel = Rgb(0x000000),
            PanelAlt = Rgb(0x0C0C0C),
            Border = Rgb(0x6FC3DF),
            Text = Rgb(0xFFFFFF),
            TextMuted = Rgb(0xD0D0D0),
            Accent = Rgb(0x0E639C),
            AccentHover = Rgb(0x1177BB),
            Selection = Rgb(0x264F78),
            Input = Rgb(0x000000),
            ButtonBack = Rgb(0x1A1A1A),
            LineNumber = Rgb(0xFFFFFF),
            DirtyMarker = Rgb(0xFFD700),
        },

        // --------------------------------------------------------------- light ----------
        new ThemeDefinition
        {
            Id = "light-plus",
            Name = "Light+",
            IsDark = false,
            MonacoBase = "vs",
            Background = Rgb(0xFFFFFF),
            Panel = Rgb(0xF3F3F3),
            PanelAlt = Rgb(0xECECEC),
            Border = Rgb(0xCECECE),
            Text = Rgb(0x1F1F1F),
            TextMuted = Rgb(0x6A6A6A),
            Accent = Rgb(0x0078D4),
            AccentHover = Rgb(0x106EBE),
            Selection = Rgb(0xCCE5FF),
            Input = Rgb(0xFFFFFF),
            ButtonBack = Rgb(0xE4E4E4),
            LineHighlight = Rgb(0xF5F5F5),
            EditorSelection = Rgb(0xADD6FF),
            DirtyMarker = Rgb(0xA6690A),
        },

        new ThemeDefinition
        {
            Id = "github-light",
            Name = "GitHub Light",
            IsDark = false,
            MonacoBase = "vs",
            Background = Rgb(0xFFFFFF),
            Panel = Rgb(0xF6F8FA),
            PanelAlt = Rgb(0xEAEEF2),
            Border = Rgb(0xD0D7DE),
            Text = Rgb(0x1F2328),
            TextMuted = Rgb(0x656D76),
            Accent = Rgb(0x1F883D),
            AccentHover = Rgb(0x2C974B),
            Selection = Rgb(0xDDF4FF),
            Input = Rgb(0xFFFFFF),
            ButtonBack = Rgb(0xF6F8FA),
            LineNumber = Rgb(0x8C959F),
            LineHighlight = Rgb(0xF6F8FA),
            EditorSelection = Rgb(0xB6E3FF),
            DirtyMarker = Rgb(0x9A6700),
            TokenRules = Syntax(
                comment: "6E7781", str: "0A3069", number: "0550AE", keyword: "CF222E",
                op: "CF222E", type: "953800", tag: "116329", attrName: "0550AE",
                attrValue: "0A3069", func: "8250DF", identifier: "1F2328", delimiter: "1F2328",
                italicComment: false),
        },

        new ThemeDefinition
        {
            Id = "solarized-light",
            Name = "Solarized Light",
            IsDark = false,
            MonacoBase = "vs",
            Background = Rgb(0xFDF6E3),
            Panel = Rgb(0xEEE8D5),
            PanelAlt = Rgb(0xE8E1CB),
            Border = Rgb(0xD5CDB6),
            // Solarized Light is famously low-contrast by design — its own base00/base1 for
            // body and secondary text land at 4.4:1 and 2.5:1 here, i.e. below AA on a
            // screen in a bright room, which is the situation someone picks a light theme
            // for. Both are darkened a step. The syntax colours below are untouched, so the
            // editor still reads as Solarized; this only affects UI chrome text.
            Text = Rgb(0x4A5B61),
            TextMuted = Rgb(0x6B7C83),
            Accent = Rgb(0x1C6FA8),
            AccentHover = Rgb(0x268BD2),
            Selection = Rgb(0xD7CFB8),
            Input = Rgb(0xFDF6E3),
            ButtonBack = Rgb(0xEEE8D5),
            LineNumber = Rgb(0x93A1A1),
            LineHighlight = Rgb(0xEEE8D5),
            EditorSelection = Rgb(0xD7CFB8),
            DirtyMarker = Rgb(0xB58900),
            TokenRules = Syntax(
                comment: "93A1A1", str: "2AA198", number: "D33682", keyword: "859900",
                op: "859900", type: "B58900", tag: "268BD2", attrName: "657B83",
                attrValue: "2AA198", func: "268BD2", identifier: "586E75", delimiter: "586E75"),
        },

        new ThemeDefinition
        {
            Id = "quiet-light",
            Name = "Quiet Light",
            IsDark = false,
            MonacoBase = "vs",
            Background = Rgb(0xF5F5F5),
            Panel = Rgb(0xEDEDED),
            PanelAlt = Rgb(0xE4E4E4),
            Border = Rgb(0xCBCBCB),
            Text = Rgb(0x333333),
            TextMuted = Rgb(0x777777),
            Accent = Rgb(0x705697),
            AccentHover = Rgb(0x8A6DB5),
            Selection = Rgb(0xC9D0D9),
            Input = Rgb(0xFFFFFF),
            ButtonBack = Rgb(0xE4E4E4),
            LineNumber = Rgb(0x9B9B9B),
            LineHighlight = Rgb(0xE4E6F1),
            EditorSelection = Rgb(0xC9D0D9),
            DirtyMarker = Rgb(0x9A6700),
            TokenRules = Syntax(
                comment: "AAAAAA", str: "448C27", number: "AB6526", keyword: "4B69C6",
                op: "777777", type: "7A3E9D", tag: "4B69C6", attrName: "91B3E0",
                attrValue: "448C27", func: "AA3731", identifier: "333333", delimiter: "777777"),
        },
    };

    public static ThemeDefinition Default => All.First(t => t.Id == DefaultId);

    /// <summary>Falls back to the default for an id that no longer exists — a settings file
    /// naming a theme from a newer build, or one simply typed wrong by hand.</summary>
    public static ThemeDefinition ById(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>The theme used when "theo Windows" is on — the pair Windows itself is
    /// choosing between. Deliberately the two defaults rather than the last dark/light
    /// theme picked: following the OS is a request for the standard look, and silently
    /// resurrecting a Gruvbox chosen weeks ago would be a surprise.</summary>
    public static ThemeDefinition ForSystem(bool systemIsDark) =>
        ById(systemIsDark ? DefaultId : "light-plus");

    /// <summary>
    /// Builds the token rules for a ported theme. Twelve colours in, sixteen Monaco rules
    /// out — the extra four are aliases Monaco's tokenizers actually emit for the same
    /// concept ("constant" alongside "number", "metatag" alongside "tag", "predefined" and
    /// "variable"), which are easy to forget one of when each theme lists its rules by hand,
    /// and the symptom is one token type staying the base theme's colour.
    ///
    /// The set is chosen for what THIS codebase's files produce: XML tags and attributes,
    /// SQL keywords, and JavaScript. Everything else inherits the Monaco base.
    /// </summary>
    private static IReadOnlyList<ThemeDefinition.TokenRule> Syntax(
        string comment, string str, string number, string keyword, string op,
        string type, string tag, string attrName, string attrValue, string func,
        string identifier, string delimiter,
        bool italicComment = true, bool italicType = false)
    {
        return new ThemeDefinition.TokenRule[]
        {
            new("comment", comment, italicComment ? "italic" : null),
            new("string", str),
            new("number", number),
            new("constant", number),
            new("keyword", keyword),
            new("operator", op),
            new("delimiter", delimiter),
            new("type", type, italicType ? "italic" : null),
            new("predefined", type),
            new("tag", tag),
            new("metatag", tag),
            new("attribute.name", attrName),
            new("attribute.value", attrValue),
            new("function", func),
            new("identifier", identifier),
            new("variable", identifier),
        };
    }

    private static Color Rgb(int rgb) =>
        Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
