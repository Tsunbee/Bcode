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

    /// <summary>Monaco workbench colors passed through as-is ("editorSuggestWidget.background",
    /// "editorBracketHighlight.foreground1", ...) — set by VsCodeThemeImporter from the VSCode
    /// theme's own "colors", applied on top of the colors derived from the palette above so an
    /// imported theme's widgets, scrollbars, guides and bracket colors match VSCode. Null for
    /// built-in themes.</summary>
    public IReadOnlyDictionary<string, string>? MonacoColors { get; init; }
}

public static class ThemeCatalog
{
    public const string DefaultId = "dark-plus";

    /// <summary>
    /// The built-in themes. Ordered dark-first because that's how the Theme menu groups
    /// them (see MainForm.BuildThemeMenu) — the menu reads this order within each group,
    /// so a new entry lands where it's put rather than wherever the array happens to end.
    /// </summary>
    public static readonly IReadOnlyList<ThemeDefinition> BuiltIn = new[]
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
            TokenRules = VsCodeSqlRules(dark: true),
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

        // VSCode's default dark theme since 1.78 — Dark+ above is the previous default. No
        // token rules: Dark Modern keeps Dark+'s syntax colours and only changes the chrome.
        new ThemeDefinition
        {
            Id = "dark-modern",
            Name = "Dark Modern",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x1F1F1F),
            Panel = Rgb(0x181818),
            PanelAlt = Rgb(0x252526),
            Border = Rgb(0x2B2B2B),
            Text = Rgb(0xCCCCCC),
            TextMuted = Rgb(0x9D9D9D),
            Accent = Rgb(0x0078D4),
            AccentHover = Rgb(0x026EC1),
            Selection = Rgb(0x04395E),
            Input = Rgb(0x313131),
            ButtonBack = Rgb(0x313131),
            LineNumber = Rgb(0x6E7681),
            LineHighlight = Rgb(0x282828),
            EditorSelection = Rgb(0x264F78),
            TokenRules = VsCodeSqlRules(dark: true),
        },

        new ThemeDefinition
        {
            Id = "github-dark-dimmed",
            Name = "GitHub Dark Dimmed",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x22272E),
            Panel = Rgb(0x1C2128),
            PanelAlt = Rgb(0x2D333B),
            Border = Rgb(0x444C56),
            Text = Rgb(0xADBAC7),
            TextMuted = Rgb(0x768390),
            Accent = Rgb(0x316DCA),
            AccentHover = Rgb(0x4184E4),
            Selection = Rgb(0x2E4562),
            Input = Rgb(0x2D333B),
            ButtonBack = Rgb(0x373E47),
            LineNumber = Rgb(0x636E7B),
            LineHighlight = Rgb(0x2D333B),
            EditorSelection = Rgb(0x2E4562),
            TokenRules = Syntax(
                comment: "768390", str: "96D0FF", number: "6CB6FF", keyword: "F47067",
                op: "F47067", type: "F69D50", tag: "8DDB8C", attrName: "6CB6FF",
                attrValue: "96D0FF", func: "DCBDFB", identifier: "ADBAC7", delimiter: "ADBAC7",
                italicComment: false),
        },

        new ThemeDefinition
        {
            Id = "catppuccin-mocha",
            Name = "Catppuccin Mocha",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x1E1E2E),
            Panel = Rgb(0x181825),
            PanelAlt = Rgb(0x313244),
            Border = Rgb(0x45475A),
            Text = Rgb(0xCDD6F4),
            TextMuted = Rgb(0x9399B2),
            Accent = Rgb(0xCBA6F7),
            AccentHover = Rgb(0xD9BEFF),
            AccentText = Rgb(0x1E1E2E),
            Selection = Rgb(0x45475A),
            Input = Rgb(0x313244),
            ButtonBack = Rgb(0x313244),
            LineNumber = Rgb(0x7F849C),
            LineHighlight = Rgb(0x2A2B3C),
            EditorSelection = Rgb(0x45475A),
            TokenRules = Syntax(
                comment: "9399B2", str: "A6E3A1", number: "FAB387", keyword: "CBA6F7",
                op: "89DCEB", type: "F9E2AF", tag: "89B4FA", attrName: "F9E2AF",
                attrValue: "A6E3A1", func: "89B4FA", identifier: "CDD6F4", delimiter: "9399B2"),
        },

        new ThemeDefinition
        {
            Id = "ayu-dark",
            Name = "Ayu Dark",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x0D1017),
            Panel = Rgb(0x0B0E14),
            PanelAlt = Rgb(0x131721),
            Border = Rgb(0x1E232B),
            Text = Rgb(0xBFBDB6),
            TextMuted = Rgb(0x7A808A), // Ayu's own #565B66 is too dim for UI text on this background
            Accent = Rgb(0xE6B450),
            AccentHover = Rgb(0xF0C46A),
            AccentText = Rgb(0x0D1017),
            Selection = Rgb(0x273747),
            Input = Rgb(0x131721),
            ButtonBack = Rgb(0x1E232B),
            LineNumber = Rgb(0x4A505A),
            LineHighlight = Rgb(0x131721),
            EditorSelection = Rgb(0x273747),
            TokenRules = Syntax(
                comment: "626A73", str: "AAD94C", number: "D2A6FF", keyword: "FF8F40",
                op: "F29668", type: "59C2FF", tag: "39BAE6", attrName: "FFB454",
                attrValue: "AAD94C", func: "FFB454", identifier: "BFBDB6", delimiter: "BFBDB6"),
        },

        new ThemeDefinition
        {
            Id = "ayu-mirage",
            Name = "Ayu Mirage",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x242936),
            Panel = Rgb(0x1F2430),
            PanelAlt = Rgb(0x2A3040),
            Border = Rgb(0x2D3446),
            Text = Rgb(0xCCCAC2),
            TextMuted = Rgb(0x8A9199),
            Accent = Rgb(0xFFCC66),
            AccentHover = Rgb(0xFFD88A),
            AccentText = Rgb(0x242936),
            Selection = Rgb(0x34455A),
            Input = Rgb(0x1F2430),
            ButtonBack = Rgb(0x2D3446),
            LineNumber = Rgb(0x5C6573),
            LineHighlight = Rgb(0x1A1F29),
            EditorSelection = Rgb(0x34455A),
            TokenRules = Syntax(
                comment: "6E7C8F", str: "D5FF80", number: "DFBFFF", keyword: "FFAD66",
                op: "F29E74", type: "73D0FF", tag: "5CCFE6", attrName: "FFD173",
                attrValue: "D5FF80", func: "FFD173", identifier: "CCCAC2", delimiter: "CCCAC2"),
        },

        new ThemeDefinition
        {
            Id = "material-palenight",
            Name = "Material Palenight",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x292D3E),
            Panel = Rgb(0x242837),
            PanelAlt = Rgb(0x32364A),
            Border = Rgb(0x3A3F58),
            Text = Rgb(0xA6ACCD),
            TextMuted = Rgb(0x7E85AB),
            Accent = Rgb(0x80CBC4),
            AccentHover = Rgb(0x9ADDD6),
            AccentText = Rgb(0x292D3E),
            Selection = Rgb(0x444A73),
            Input = Rgb(0x32364A),
            ButtonBack = Rgb(0x3A3F58),
            LineNumber = Rgb(0x4E5579),
            LineHighlight = Rgb(0x32374D),
            EditorSelection = Rgb(0x444A73),
            TokenRules = Syntax(
                comment: "676E95", str: "C3E88D", number: "F78C6C", keyword: "C792EA",
                op: "89DDFF", type: "FFCB6B", tag: "F07178", attrName: "C792EA",
                attrValue: "C3E88D", func: "82AAFF", identifier: "A6ACCD", delimiter: "89DDFF"),
        },

        new ThemeDefinition
        {
            Id = "material-ocean",
            Name = "Material Ocean",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x0F111A),
            Panel = Rgb(0x090B10),
            PanelAlt = Rgb(0x1A1C25),
            Border = Rgb(0x1F2233),
            Text = Rgb(0xBABED8),
            TextMuted = Rgb(0x717CB4),
            Accent = Rgb(0x80CBC4),
            AccentHover = Rgb(0x9ADDD6),
            AccentText = Rgb(0x0F111A),
            Selection = Rgb(0x2B3045),
            Input = Rgb(0x1A1C25),
            ButtonBack = Rgb(0x1F2233),
            LineNumber = Rgb(0x3B3F51),
            LineHighlight = Rgb(0x1A1C25),
            EditorSelection = Rgb(0x262A3F),
            TokenRules = Syntax(
                comment: "464B5D", str: "C3E88D", number: "F78C6C", keyword: "C792EA",
                op: "89DDFF", type: "FFCB6B", tag: "F07178", attrName: "C792EA",
                attrValue: "C3E88D", func: "82AAFF", identifier: "BABED8", delimiter: "89DDFF"),
        },

        new ThemeDefinition
        {
            Id = "cobalt2",
            Name = "Cobalt2",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x193549),
            Panel = Rgb(0x15232D),
            PanelAlt = Rgb(0x1F4662),
            Border = Rgb(0x0D3A58),
            Text = Rgb(0xFFFFFF),
            TextMuted = Rgb(0xAAAAAA),
            Accent = Rgb(0xFFC600),
            AccentHover = Rgb(0xFFD83D),
            AccentText = Rgb(0x193549),
            Selection = Rgb(0x0050A4),
            Input = Rgb(0x15232D),
            ButtonBack = Rgb(0x1F4662),
            LineNumber = Rgb(0xAAAAAA),
            LineHighlight = Rgb(0x1F4662),
            EditorSelection = Rgb(0x0050A4),
            TokenRules = Syntax(
                comment: "0088FF", str: "3AD900", number: "FF628C", keyword: "FF9D00",
                op: "FF9D00", type: "FFC600", tag: "9EFFFF", attrName: "FFC600",
                attrValue: "3AD900", func: "FFC600", identifier: "E1EFFF", delimiter: "E1EFFF"),
        },

        new ThemeDefinition
        {
            Id = "synthwave-84",
            Name = "SynthWave '84",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x262335),
            Panel = Rgb(0x241B2F),
            PanelAlt = Rgb(0x2A2139),
            Border = Rgb(0x34294F),
            Text = Rgb(0xF0EFF1),
            TextMuted = Rgb(0x9A9EC8),
            Accent = Rgb(0xFF7EDB),
            AccentHover = Rgb(0xFF9BE4),
            AccentText = Rgb(0x262335),
            Selection = Rgb(0x463465),
            Input = Rgb(0x2A2139),
            ButtonBack = Rgb(0x34294F),
            LineNumber = Rgb(0x7D7099),
            LineHighlight = Rgb(0x2D2741),
            EditorSelection = Rgb(0x463465),
            TokenRules = Syntax(
                comment: "848BBD", str: "FF8B39", number: "F97E72", keyword: "FEDE5D",
                op: "FEDE5D", type: "FE4450", tag: "72F1B8", attrName: "FEDE5D",
                attrValue: "FF8B39", func: "36F9F6", identifier: "FF7EDB", delimiter: "B6B1B1"),
        },

        new ThemeDefinition
        {
            Id = "rose-pine",
            Name = "Rosé Pine",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x191724),
            Panel = Rgb(0x1F1D2E),
            PanelAlt = Rgb(0x26233A),
            Border = Rgb(0x403D52),
            Text = Rgb(0xE0DEF4),
            TextMuted = Rgb(0x908CAA),
            Accent = Rgb(0xEBBCBA),
            AccentHover = Rgb(0xF3D1D0),
            AccentText = Rgb(0x191724),
            Selection = Rgb(0x403D52),
            Input = Rgb(0x1F1D2E),
            ButtonBack = Rgb(0x26233A),
            LineNumber = Rgb(0x6E6A86),
            LineHighlight = Rgb(0x21202E),
            EditorSelection = Rgb(0x403D52),
            TokenRules = Syntax(
                comment: "6E6A86", str: "F6C177", number: "EBBCBA", keyword: "31748F",
                op: "908CAA", type: "9CCFD8", tag: "9CCFD8", attrName: "C4A7E7",
                attrValue: "F6C177", func: "EBBCBA", identifier: "E0DEF4", delimiter: "908CAA"),
        },

        new ThemeDefinition
        {
            Id = "everforest-dark",
            Name = "Everforest Dark",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x2D353B),
            Panel = Rgb(0x232A2E),
            PanelAlt = Rgb(0x343F44),
            Border = Rgb(0x475258),
            Text = Rgb(0xD3C6AA),
            TextMuted = Rgb(0x9DA9A0),
            Accent = Rgb(0xA7C080),
            AccentHover = Rgb(0xBBD198),
            AccentText = Rgb(0x2D353B),
            Selection = Rgb(0x475258),
            Input = Rgb(0x343F44),
            ButtonBack = Rgb(0x3D484D),
            LineNumber = Rgb(0x7A8478),
            LineHighlight = Rgb(0x343F44),
            EditorSelection = Rgb(0x475258),
            TokenRules = Syntax(
                comment: "859289", str: "83C092", number: "D699B6", keyword: "E67E80",
                op: "E69875", type: "DBBC7F", tag: "E69875", attrName: "DBBC7F",
                attrValue: "83C092", func: "A7C080", identifier: "D3C6AA", delimiter: "D3C6AA"),
        },

        new ThemeDefinition
        {
            Id = "kanagawa",
            Name = "Kanagawa",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x1F1F28),
            Panel = Rgb(0x16161D),
            PanelAlt = Rgb(0x2A2A37),
            Border = Rgb(0x363646),
            Text = Rgb(0xDCD7BA),
            TextMuted = Rgb(0xA09F93),
            Accent = Rgb(0x7E9CD8),
            AccentHover = Rgb(0x98B2E6),
            AccentText = Rgb(0x1F1F28),
            Selection = Rgb(0x2D4F67),
            Input = Rgb(0x2A2A37),
            ButtonBack = Rgb(0x363646),
            LineNumber = Rgb(0x54546D),
            LineHighlight = Rgb(0x2A2A37),
            EditorSelection = Rgb(0x2D4F67),
            TokenRules = Syntax(
                comment: "727169", str: "98BB6C", number: "D27E99", keyword: "957FB8",
                op: "C0A36E", type: "7AA89F", tag: "7E9CD8", attrName: "E6C384",
                attrValue: "98BB6C", func: "7E9CD8", identifier: "DCD7BA", delimiter: "9CABCA"),
        },

        new ThemeDefinition
        {
            Id = "shades-of-purple",
            Name = "Shades of Purple",
            IsDark = true,
            MonacoBase = "vs-dark",
            Background = Rgb(0x2D2B55),
            Panel = Rgb(0x222244),
            PanelAlt = Rgb(0x1E1E3F),
            Border = Rgb(0x3B3A6E),
            Text = Rgb(0xFFFFFF),
            TextMuted = Rgb(0xA599E9),
            Accent = Rgb(0xFAD000),
            AccentHover = Rgb(0xFFDD40),
            AccentText = Rgb(0x2D2B55),
            Selection = Rgb(0x7746AA),
            Input = Rgb(0x1E1E3F),
            ButtonBack = Rgb(0x3B3A6E),
            LineNumber = Rgb(0xA599E9),
            LineHighlight = Rgb(0x1F1F41),
            EditorSelection = Rgb(0x7746AA),
            TokenRules = Syntax(
                comment: "B362FF", str: "A5FF90", number: "FF628C", keyword: "FF9D00",
                op: "FF9D00", type: "FAD000", tag: "9EFFFF", attrName: "FAD000",
                attrValue: "A5FF90", func: "FAD000", identifier: "FFFFFF", delimiter: "E1EFFF"),
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
            TokenRules = VsCodeSqlRules(dark: false),
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

        // VSCode's default light theme since 1.78; like Dark Modern, Light+'s syntax colours.
        new ThemeDefinition
        {
            Id = "light-modern",
            Name = "Light Modern",
            IsDark = false,
            MonacoBase = "vs",
            Background = Rgb(0xFFFFFF),
            Panel = Rgb(0xF8F8F8),
            PanelAlt = Rgb(0xF0F0F0),
            Border = Rgb(0xE5E5E5),
            Text = Rgb(0x3B3B3B),
            TextMuted = Rgb(0x616161),
            Accent = Rgb(0x005FB8),
            AccentHover = Rgb(0x0258A8),
            Selection = Rgb(0xDAE9F7),
            Input = Rgb(0xFFFFFF),
            ButtonBack = Rgb(0xE5E5E5),
            LineNumber = Rgb(0x6E7681),
            LineHighlight = Rgb(0xF5F5F5),
            EditorSelection = Rgb(0xADD6FF),
            DirtyMarker = Rgb(0xA6690A),
            TokenRules = VsCodeSqlRules(dark: false),
        },

        new ThemeDefinition
        {
            Id = "catppuccin-latte",
            Name = "Catppuccin Latte",
            IsDark = false,
            MonacoBase = "vs",
            Background = Rgb(0xEFF1F5),
            Panel = Rgb(0xE6E9EF),
            PanelAlt = Rgb(0xDCE0E8),
            Border = Rgb(0xCCD0DA),
            Text = Rgb(0x4C4F69),
            TextMuted = Rgb(0x6C6F85),
            Accent = Rgb(0x8839EF),
            AccentHover = Rgb(0x9D55F5),
            Selection = Rgb(0xCCD0DA),
            Input = Rgb(0xFFFFFF),
            ButtonBack = Rgb(0xDCE0E8),
            LineNumber = Rgb(0x8C8FA1),
            LineHighlight = Rgb(0xE6E9EF),
            EditorSelection = Rgb(0xBCC0CC),
            DirtyMarker = Rgb(0xFE640B),
            TokenRules = Syntax(
                comment: "7C7F93", str: "40A02B", number: "FE640B", keyword: "8839EF",
                op: "04A5E5", type: "DF8E1D", tag: "1E66F5", attrName: "DF8E1D",
                attrValue: "40A02B", func: "1E66F5", identifier: "4C4F69", delimiter: "7C7F93"),
        },

        new ThemeDefinition
        {
            Id = "ayu-light",
            Name = "Ayu Light",
            IsDark = false,
            MonacoBase = "vs",
            Background = Rgb(0xFCFCFC),
            Panel = Rgb(0xF8F9FA),
            PanelAlt = Rgb(0xF0F1F2),
            Border = Rgb(0xE7EAED),
            Text = Rgb(0x5C6166),
            TextMuted = Rgb(0x787B80),
            Accent = Rgb(0xFFAA33),
            AccentHover = Rgb(0xFFB84D),
            AccentText = Rgb(0x3E2A00), // white on Ayu's orange is unreadable
            Selection = Rgb(0xDCE7F5),
            Input = Rgb(0xFFFFFF),
            ButtonBack = Rgb(0xECEEF0),
            LineNumber = Rgb(0xABADB1),
            LineHighlight = Rgb(0xF3F4F5),
            EditorSelection = Rgb(0xDCE7F5),
            DirtyMarker = Rgb(0xC17D10),
            TokenRules = Syntax(
                comment: "ABADB1", str: "86B300", number: "A37ACC", keyword: "FA8D3E",
                op: "ED9366", type: "399EE6", tag: "55B4D4", attrName: "F2AE49",
                attrValue: "86B300", func: "F2AE49", identifier: "5C6166", delimiter: "5C6166"),
        },

        new ThemeDefinition
        {
            Id = "atom-one-light",
            Name = "Atom One Light",
            IsDark = false,
            MonacoBase = "vs",
            Background = Rgb(0xFAFAFA),
            Panel = Rgb(0xEAEAEB),
            PanelAlt = Rgb(0xE5E5E6),
            Border = Rgb(0xDBDBDC),
            Text = Rgb(0x383A42),
            TextMuted = Rgb(0x696C77),
            Accent = Rgb(0x526FFF),
            AccentHover = Rgb(0x6D84FF),
            Selection = Rgb(0xDDE3FF),
            Input = Rgb(0xFFFFFF),
            ButtonBack = Rgb(0xE5E5E6),
            LineNumber = Rgb(0x9D9D9F),
            LineHighlight = Rgb(0xF2F2F2),
            EditorSelection = Rgb(0xE5E5E6),
            DirtyMarker = Rgb(0xC18401),
            TokenRules = Syntax(
                comment: "A0A1A7", str: "50A14F", number: "986801", keyword: "A626A4",
                op: "0184BC", type: "C18401", tag: "E45649", attrName: "986801",
                attrValue: "50A14F", func: "4078F2", identifier: "383A42", delimiter: "383A42"),
        },
    };

    /// <summary>Themes imported from VSCode theme files (see VsCodeThemeImporter), loaded from
    /// %AppData%\Bcode\viewer-themes. Replaced wholesale on (re)load so readers never see a
    /// half-built list.</summary>
    public static IReadOnlyList<ThemeDefinition> Custom { get; private set; } = Array.Empty<ThemeDefinition>();

    /// <summary>Built-in themes followed by imported ones — what the Theme menu and ById read.</summary>
    public static IReadOnlyList<ThemeDefinition> All => BuiltIn.Concat(Custom).ToList();

    public static void ReloadCustom() => Custom = VsCodeThemeImporter.LoadAll();

    public static ThemeDefinition Default => BuiltIn.First(t => t.Id == DefaultId);

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
            new("string.xml", str),
            new("string.value.xml", str),
            // Monaco's base themes ship language-specific rules that are MORE specific than the
            // generic ones above and therefore win over them: string.sql = pure red #FF0000,
            // predefined.sql = magenta #FF00FF (convert/rtrim/len...), operator.sql grey, plus
            // delimiter.xml / metatag.xml. Without restating them every theme showed SQL strings
            // red and SQL functions magenta whatever its own palette said.
            new("string.sql", str),
            new("predefined.sql", func),
            new("operator.sql", op),
            new("delimiter.xml", delimiter),
            new("metatag.xml", tag),
            new("", identifier)
        };
    }

    /// <summary>For the themes that otherwise just inherit a Monaco base (Dark+, Dark Modern,
    /// Light+, Light Modern): the base's own SQL rules paint strings pure red and functions
    /// magenta, which is not what VSCode's Dark+/Light+ do. These are VSCode's colors for the
    /// same tokens (string, support.function, keyword.operator).</summary>
    private static IReadOnlyList<ThemeDefinition.TokenRule> VsCodeSqlRules(bool dark) => dark
        ? new ThemeDefinition.TokenRule[] { new("string.sql", "CE9178"), new("predefined.sql", "DCDCAA"), new("operator.sql", "D4D4D4") }
        : new ThemeDefinition.TokenRule[] { new("string.sql", "A31515"), new("predefined.sql", "795E26"), new("operator.sql", "000000") };

    private static Color Rgb(int rgb) =>
        Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
