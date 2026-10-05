using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bcode.App.UI;

/// <summary>Một theme màu: bảng màu dùng cho chrome WinForms + trang WebView2 (cùng ColorPalette của AppColors).</summary>
public sealed record ThemeInfo(string Id, string Name, bool IsDark, ColorPalette Palette, string Source);

/// <summary>
/// Danh mục theme của Bcode, theo cách BcodeViewer làm: bộ theme dựng sẵn (cùng bảng màu với BcodeViewer — chỉ đọc tham khảo, không
/// dùng chung code) + theme nhập từ VS Code (.json hoặc .vsix) lưu ở <c>%AppData%\Bcode\ui-themes</c>; ngoài ra đọc luôn các theme đã
/// nhập ở BcodeViewer (<c>viewer-themes</c>, chỉ đọc) để khỏi nhập hai lần. Bcode có 2 "ngăn": Tối (AppColors.Dark) và Sáng
/// (AppColors.Light) — nút mặt trời/mặt trăng chuyển giữa hai ngăn; mỗi ngăn lấy theme người dùng chọn (UiTemplate.DarkTheme/LightTheme)
/// rồi chồng các màu chỉnh tay (DarkColors/LightColors) lên trên. <see cref="ApplyPalettes"/> ghi kết quả thẳng vào AppColors.Dark/Light.
/// </summary>
public static class UiThemes
{
    public const string DefaultDark = "default-dark";
    public const string DefaultLight = "default-light";

    /// <summary>Bảng màu mặc định của Bcode (tông ong) — chụp lại trước khi bị theme ghi đè.</summary>
    private static readonly ColorPalette DefaultDarkPalette = AppColors.Dark.Clone();
    private static readonly ColorPalette DefaultLightPalette = AppColors.Light.Clone();

    public static string Folder => Path.Combine(BcodePaths.AppData, "Bcode", "ui-themes");
    private static string ViewerFolder => Path.Combine(BcodePaths.AppData, "Bcode", "viewer-themes");

    // ------------------------------------------------------------------ danh mục

    private static List<ThemeInfo>? _all;

    public static IReadOnlyList<ThemeInfo> All => _all ??= BuildAll();

    public static ThemeInfo? Find(string? id) => id is null ? null : All.FirstOrDefault(t => t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Đọc lại thư mục theme (sau khi nhập/xoá).</summary>
    public static void Reload() => _all = null;

    private static List<ThemeInfo> BuildAll()
    {
        var list = new List<ThemeInfo>
        {
            new(DefaultDark, "Bcode Dark (mặc định)", true, DefaultDarkPalette, "builtin"),
            new(DefaultLight, "Bcode Light (mặc định)", false, DefaultLightPalette, "builtin"),
        };
        list.AddRange(Presets());
        foreach (var (folder, source, prefix) in new[] { (Folder, "imported", ""), (ViewerFolder, "viewer", "viewer:") })
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    try { list.Add(Convert(ParseObject(File.ReadAllText(file)), prefix + Path.GetFileNameWithoutExtension(file), source)); }
                    catch { /* file hỏng/sửa tay sai — bỏ qua, không làm app không khởi động được */ }
                }
            }
            catch { /* thư mục đọc không được */ }
        }
        return list;
    }

    // ------------------------------------------------------------------ áp vào AppColors

    /// <summary>Ghi theme + màu chỉnh tay của template hiện tại vào AppColors.Dark/Light (đối tượng giữ nguyên nên AppColors.IsDark vẫn đúng).</summary>
    public static void ApplyPalettes()
    {
        var t = UiTemplate.Current;
        Fill(AppColors.Dark, DefaultDarkPalette, t.DarkTheme, t.DarkColors);
        Fill(AppColors.Light, DefaultLightPalette, t.LightTheme, t.LightColors);
    }

    private static void Fill(ColorPalette target, ColorPalette fallback, string? themeId, Dictionary<string, string> overrides)
    {
        target.CopyFrom(Find(themeId)?.Palette ?? fallback);
        foreach (var (key, hex) in overrides)
            if (UiTemplate.ParseColor(hex) is { } c) target.Set(key, c);
    }

    /// <summary>Bảng màu hiện hành dạng JSON cho các trang Monaco (sqleditor, lịch sử...); "null" khi người dùng chưa đổi theme/màu.</summary>
    public static string PaletteJson() => UiTemplate.Current.IsPaletteCustomized
        ? JsonSerializer.Serialize(new
        {
            panel = Hex(AppColors.Panel), panelAlt = Hex(AppColors.PanelAlt), text = Hex(AppColors.Text), textMuted = Hex(AppColors.TextMuted),
            accent = Hex(AppColors.Accent), selection = Hex(AppColors.Selection), border = Hex(AppColors.Border), input = Hex(AppColors.Input),
        })
        : "null";

    public static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Dictionary<string, string> ToHex(ColorPalette p) => ColorPalette.Keys.ToDictionary(k => k.Key, k => Hex(p.Get(k.Key)));

    // ------------------------------------------------------------------ nhập / xoá

    /// <summary>Nhập theme VS Code (.json hoặc .vsix); trả về id các theme mới. Lỗi thì ném ngoại lệ có thông báo đọc được.</summary>
    public static List<string> Import(string sourcePath)
    {
        Directory.CreateDirectory(Folder);
        var added = new List<string>();

        if (Path.GetExtension(sourcePath).Equals(".vsix", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(sourcePath);
            var pkgEntry = FindEntry(zip, "extension/package.json")
                ?? throw new InvalidDataException("File .vsix không có extension/package.json.");
            var pkg = ParseObject(ReadEntry(pkgEntry));
            var themes = pkg["contributes"]?["themes"] as JsonArray;
            if (themes is null || themes.Count == 0)
                throw new InvalidDataException("Extension này không khai báo color theme nào (contributes.themes).");

            foreach (var t in themes)
            {
                var rel = Str(t?["path"]);
                if (string.IsNullOrWhiteSpace(rel)) continue;
                var entryPath = NormalizeZipPath("extension/" + rel);
                string Read(string p) => ReadEntry(FindEntry(zip, p) ?? throw new FileNotFoundException("Không thấy " + p + " trong .vsix."));
                var flat = Flatten(entryPath, Read, (baseFile, include) => NormalizeZipPath(ZipDir(baseFile) + include), 0);
                var label = Str(t?["label"]);
                if (!string.IsNullOrWhiteSpace(label)) flat["name"] = label;
                if (flat["type"] is null && Str(t?["uiTheme"]) is { } ui)
                    flat["type"] = ui == "vs" ? "light" : ui.StartsWith("hc", StringComparison.Ordinal) ? "hc" : "dark";
                added.Add(Save(flat, label ?? Path.GetFileNameWithoutExtension(rel)));
            }
        }
        else
        {
            var flat = Flatten(Path.GetFullPath(sourcePath), File.ReadAllText,
                (baseFile, include) => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(baseFile)!, include)), 0);
            added.Add(Save(flat, Path.GetFileNameWithoutExtension(sourcePath)));
        }
        Reload();
        return added;
    }

    /// <summary>Xoá theme đã nhập (theme dựng sẵn và theme của BcodeViewer không xoá từ đây).</summary>
    public static void Delete(string id)
    {
        var file = Path.Combine(Folder, id + ".json");
        if (File.Exists(file)) File.Delete(file);
        Reload();
    }

    private static string Save(JsonObject flat, string fallbackName)
    {
        var name = Str(flat["name"]);
        if (string.IsNullOrWhiteSpace(name)) { name = fallbackName; flat["name"] = name; }
        _ = Convert(flat, "check", "imported"); // thử chuyển ngay lúc nhập: file dùng được thì mới lưu
        var id = "custom-" + Slug(name!);
        File.WriteAllText(Path.Combine(Folder, id + ".json"), flat.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return id;
    }

    /// <summary>Theo "include" (theme gốc của VS Code) và gộp thành một object: màu của theme gốc bị màu của file này ghi đè.</summary>
    private static JsonObject Flatten(string path, Func<string, string> read, Func<string, string, string> resolve, int depth)
    {
        if (depth > 5) throw new InvalidDataException("Theme include lồng nhau quá sâu.");
        var obj = ParseObject(read(path));
        if (Str(obj["include"]) is not { Length: > 0 } include) return obj;

        var baseObj = Flatten(resolve(path, include), read, resolve, depth + 1);
        var colors = baseObj["colors"]?.DeepClone() as JsonObject ?? new JsonObject();
        if (obj["colors"] is JsonObject own)
            foreach (var (k, v) in own) colors[k] = v?.DeepClone();
        obj.Remove("include");
        obj["colors"] = colors;
        if (obj["type"] is null && baseObj["type"] is { } bt) obj["type"] = bt.DeepClone();
        return obj;
    }

    // ------------------------------------------------------------------ VS Code -> bảng màu

    private static ThemeInfo Convert(JsonObject theme, string id, string source)
    {
        var colors = theme["colors"] as JsonObject ?? new JsonObject();
        var type = Str(theme["type"])?.ToLowerInvariant() ?? "";
        var bgGuess = Col(colors, Color.Empty, "editor.background");
        var isDark = type switch
        {
            "light" or "hclight" => false,
            "dark" or "hc" or "hcdark" => true,
            _ => bgGuess.IsEmpty || Luma(bgGuess) < 0.5,
        };
        var bg = bgGuess.IsEmpty ? (isDark ? Rgb(0x1E1E1E) : Color.White) : bgGuess;
        Color C(Color fallback, params string[] keys) => Col(colors, bg, keys) is { IsEmpty: false } c ? c : fallback;

        var text = C(isDark ? Rgb(0xD4D4D4) : Rgb(0x1F1F1F), "editor.foreground", "foreground");
        var panel = C(bg, "sideBar.background", "panel.background");
        var panelAlt = C(Mix(panel, text, 0.06), "editorGroupHeader.tabsBackground", "tab.inactiveBackground", "titleBar.activeBackground");
        var border = C(Mix(bg, text, 0.18), "panel.border", "sideBar.border", "editorGroup.border", "contrastBorder", "widget.border");
        var accent = C(isDark ? Rgb(0x0E639C) : Rgb(0x0078D4), "button.background", "focusBorder", "activityBarBadge.background");
        var accentText = C(Luma(accent) > 0.55 ? Rgb(0x1E1E1E) : Color.White, "button.foreground");
        var selection = C(Mix(bg, accent, 0.35), "list.activeSelectionBackground", "list.inactiveSelectionBackground", "editor.selectionBackground");
        var def = isDark ? DefaultDarkPalette : DefaultLightPalette;

        var p = new ColorPalette
        {
            Background = bg,
            Panel = panel,
            PanelAlt = panelAlt,
            Border = border,
            Text = text,
            TextMuted = C(Mix(text, bg, 0.4), "descriptionForeground", "tab.inactiveForeground", "editorLineNumber.foreground"),
            Accent = accent,
            AccentHover = C(Mix(accent, isDark ? Color.White : Color.Black, 0.15), "button.hoverBackground"),
            Selection = selection,
            Input = C(panel, "input.background", "dropdown.background"),
            ButtonBack = C(panelAlt, "button.secondaryBackground"),
            Warning = C(def.Warning, "editorWarning.foreground", "list.warningForeground"),
            Success = C(def.Success, "gitDecoration.addedResourceForeground", "testing.iconPassed", "terminal.ansiGreen"),
            Danger = C(def.Danger, "errorForeground", "editorError.foreground"),
            OnAccent = accentText,
        };
        var name = Str(theme["name"]) is { Length: > 0 } n ? n : id;
        return new ThemeInfo(id, name, isDark, p, source);
    }

    // ------------------------------------------------------------------ bộ dựng sẵn (cùng bảng màu với BcodeViewer)

    private static ThemeInfo P(string id, string name, bool dark, int bg, int panel, int panelAlt, int border, int text, int muted,
        int accent, int accentHover, int selection, int input, int buttonBack, int? accentText)
    {
        var def = dark ? DefaultDarkPalette : DefaultLightPalette;
        var a = Rgb(accent);
        var p = new ColorPalette
        {
            Background = Rgb(bg), Panel = Rgb(panel), PanelAlt = Rgb(panelAlt), Border = Rgb(border), Text = Rgb(text),
            TextMuted = Rgb(muted), Accent = a, AccentHover = Rgb(accentHover), Selection = Rgb(selection), Input = Rgb(input),
            ButtonBack = Rgb(buttonBack), Warning = def.Warning, Success = def.Success, Danger = def.Danger,
            OnAccent = accentText is { } at ? Rgb(at) : (Luma(a) > 0.55 ? Rgb(0x1E1E1E) : Color.White),
        };
        return new ThemeInfo(id, name, dark, p, "builtin");
    }

    private static IEnumerable<ThemeInfo> Presets() => new[]
    {
        P("dark-plus", "Dark+ (mặc định)", true, 0x1E1E1E, 0x252526, 0x2D2D30, 0x3F3F46, 0xD4D4D4, 0x969696, 0x0E639C, 0x1177BB, 0x094771, 0x3C3C3C, 0x3E3E42, null),
        P("dracula", "Dracula", true, 0x282A36, 0x21222C, 0x282A36, 0x44475A, 0xF8F8F2, 0x6272A4, 0xBD93F9, 0xCFB0FF, 0x44475A, 0x21222C, 0x343746, 0x282A36),
        P("one-dark", "One Dark Pro", true, 0x282C34, 0x21252B, 0x2C313A, 0x3E4451, 0xABB2BF, 0x7F848E, 0x61AFEF, 0x7CC0FF, 0x3E4451, 0x1B1D23, 0x3A3F4B, 0x282C34),
        P("nord", "Nord", true, 0x2E3440, 0x2B303B, 0x3B4252, 0x434C5E, 0xD8DEE9, 0x7B88A1, 0x88C0D0, 0x9FD3E3, 0x434C5E, 0x3B4252, 0x434C5E, 0x2E3440),
        P("tokyo-night", "Tokyo Night", true, 0x1A1B26, 0x16161E, 0x1F2335, 0x292E42, 0xA9B1D6, 0x787C99, 0x7AA2F7, 0x96B6FF, 0x283457, 0x1F2335, 0x292E42, 0x1A1B26),
        P("monokai", "Monokai", true, 0x272822, 0x1E1F1C, 0x272822, 0x414339, 0xF8F8F2, 0x90918B, 0xD6216A, 0xF92672, 0x49483E, 0x3B3C35, 0x3B3C35, null),
        P("gruvbox-dark", "Gruvbox Dark", true, 0x282828, 0x1D2021, 0x32302F, 0x504945, 0xEBDBB2, 0xA89984, 0xD79921, 0xFABD2F, 0x504945, 0x3C3836, 0x3C3836, 0x282828),
        P("night-owl", "Night Owl", true, 0x011627, 0x001122, 0x0B2942, 0x1D3B53, 0xD6DEEB, 0x5F7E97, 0x7E57C2, 0x9575D6, 0x1D3B53, 0x0B2942, 0x1D3B53, null),
        P("solarized-dark", "Solarized Dark", true, 0x002B36, 0x00212B, 0x073642, 0x094757, 0x93A1A1, 0x657B83, 0x1C6FA8, 0x268BD2, 0x0B4A5A, 0x073642, 0x073642, null),
        P("github-dark", "GitHub Dark", true, 0x0D1117, 0x010409, 0x161B22, 0x30363D, 0xE6EDF3, 0x8B949E, 0x238636, 0x2EA043, 0x163356, 0x0D1117, 0x21262D, null),
        P("high-contrast", "High Contrast Dark", true, 0x000000, 0x000000, 0x0C0C0C, 0x6FC3DF, 0xFFFFFF, 0xD0D0D0, 0x0E639C, 0x1177BB, 0x264F78, 0x000000, 0x1A1A1A, null),
        P("dark-modern", "Dark Modern", true, 0x1F1F1F, 0x181818, 0x252526, 0x2B2B2B, 0xCCCCCC, 0x9D9D9D, 0x0078D4, 0x026EC1, 0x04395E, 0x313131, 0x313131, null),
        P("github-dark-dimmed", "GitHub Dark Dimmed", true, 0x22272E, 0x1C2128, 0x2D333B, 0x444C56, 0xADBAC7, 0x768390, 0x316DCA, 0x4184E4, 0x2E4562, 0x2D333B, 0x373E47, null),
        P("catppuccin-mocha", "Catppuccin Mocha", true, 0x1E1E2E, 0x181825, 0x313244, 0x45475A, 0xCDD6F4, 0x9399B2, 0xCBA6F7, 0xD9BEFF, 0x45475A, 0x313244, 0x313244, 0x1E1E2E),
        P("ayu-dark", "Ayu Dark", true, 0x0D1017, 0x0B0E14, 0x131721, 0x1E232B, 0xBFBDB6, 0x7A808A, 0xE6B450, 0xF0C46A, 0x273747, 0x131721, 0x1E232B, 0x0D1017),
        P("ayu-mirage", "Ayu Mirage", true, 0x242936, 0x1F2430, 0x2A3040, 0x2D3446, 0xCCCAC2, 0x8A9199, 0xFFCC66, 0xFFD88A, 0x34455A, 0x1F2430, 0x2D3446, 0x242936),
        P("material-palenight", "Material Palenight", true, 0x292D3E, 0x242837, 0x32364A, 0x3A3F58, 0xA6ACCD, 0x7E85AB, 0x80CBC4, 0x9ADDD6, 0x444A73, 0x32364A, 0x3A3F58, 0x292D3E),
        P("material-ocean", "Material Ocean", true, 0x0F111A, 0x090B10, 0x1A1C25, 0x1F2233, 0xBABED8, 0x717CB4, 0x80CBC4, 0x9ADDD6, 0x2B3045, 0x1A1C25, 0x1F2233, 0x0F111A),
        P("cobalt2", "Cobalt2", true, 0x193549, 0x15232D, 0x1F4662, 0x0D3A58, 0xFFFFFF, 0xAAAAAA, 0xFFC600, 0xFFD83D, 0x0050A4, 0x15232D, 0x1F4662, 0x193549),
        P("synthwave-84", "SynthWave '84", true, 0x262335, 0x241B2F, 0x2A2139, 0x34294F, 0xF0EFF1, 0x9A9EC8, 0xFF7EDB, 0xFF9BE4, 0x463465, 0x2A2139, 0x34294F, 0x262335),
        P("rose-pine", "Rosé Pine", true, 0x191724, 0x1F1D2E, 0x26233A, 0x403D52, 0xE0DEF4, 0x908CAA, 0xEBBCBA, 0xF3D1D0, 0x403D52, 0x1F1D2E, 0x26233A, 0x191724),
        P("everforest-dark", "Everforest Dark", true, 0x2D353B, 0x232A2E, 0x343F44, 0x475258, 0xD3C6AA, 0x9DA9A0, 0xA7C080, 0xBBD198, 0x475258, 0x343F44, 0x3D484D, 0x2D353B),
        P("kanagawa", "Kanagawa", true, 0x1F1F28, 0x16161D, 0x2A2A37, 0x363646, 0xDCD7BA, 0xA09F93, 0x7E9CD8, 0x98B2E6, 0x2D4F67, 0x2A2A37, 0x363646, 0x1F1F28),
        P("shades-of-purple", "Shades of Purple", true, 0x2D2B55, 0x222244, 0x1E1E3F, 0x3B3A6E, 0xFFFFFF, 0xA599E9, 0xFAD000, 0xFFDD40, 0x7746AA, 0x1E1E3F, 0x3B3A6E, 0x2D2B55),
        P("light-plus", "Light+", false, 0xFFFFFF, 0xF3F3F3, 0xECECEC, 0xCECECE, 0x1F1F1F, 0x6A6A6A, 0x0078D4, 0x106EBE, 0xCCE5FF, 0xFFFFFF, 0xE4E4E4, null),
        P("github-light", "GitHub Light", false, 0xFFFFFF, 0xF6F8FA, 0xEAEEF2, 0xD0D7DE, 0x1F2328, 0x656D76, 0x1F883D, 0x2C974B, 0xDDF4FF, 0xFFFFFF, 0xF6F8FA, null),
        P("solarized-light", "Solarized Light", false, 0xFDF6E3, 0xEEE8D5, 0xE8E1CB, 0xD5CDB6, 0x4A5B61, 0x6B7C83, 0x1C6FA8, 0x268BD2, 0xD7CFB8, 0xFDF6E3, 0xEEE8D5, null),
        P("quiet-light", "Quiet Light", false, 0xF5F5F5, 0xEDEDED, 0xE4E4E4, 0xCBCBCB, 0x333333, 0x777777, 0x705697, 0x8A6DB5, 0xC9D0D9, 0xFFFFFF, 0xE4E4E4, null),
        P("light-modern", "Light Modern", false, 0xFFFFFF, 0xF8F8F8, 0xF0F0F0, 0xE5E5E5, 0x3B3B3B, 0x616161, 0x005FB8, 0x0258A8, 0xDAE9F7, 0xFFFFFF, 0xE5E5E5, null),
        P("catppuccin-latte", "Catppuccin Latte", false, 0xEFF1F5, 0xE6E9EF, 0xDCE0E8, 0xCCD0DA, 0x4C4F69, 0x6C6F85, 0x8839EF, 0x9D55F5, 0xCCD0DA, 0xFFFFFF, 0xDCE0E8, null),
        P("ayu-light", "Ayu Light", false, 0xFCFCFC, 0xF8F9FA, 0xF0F1F2, 0xE7EAED, 0x5C6166, 0x787B80, 0xFFAA33, 0xFFB84D, 0xDCE7F5, 0xFFFFFF, 0xECEEF0, 0x3E2A00),
        P("atom-one-light", "Atom One Light", false, 0xFAFAFA, 0xEAEAEB, 0xE5E5E6, 0xDBDBDC, 0x383A42, 0x696C77, 0x526FFF, 0x6D84FF, 0xDDE3FF, 0xFFFFFF, 0xE5E5E6, null),
    };

    // ------------------------------------------------------------------ helpers

    private static Color Col(JsonObject colors, Color over, params string[] keys)
    {
        foreach (var k in keys)
            if (Str(colors[k]) is { } s && ParseColor(s, over) is { IsEmpty: false } c) return c;
        return Color.Empty;
    }

    private static Color ParseColor(string s, Color over)
    {
        s = s.Trim().TrimStart('#');
        if (s.Length is 3 or 4) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length is not (6 or 8) || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out _)) return Color.Empty;
        var c = Color.FromArgb(System.Convert.ToInt32(s[..2], 16), System.Convert.ToInt32(s[2..4], 16), System.Convert.ToInt32(s[4..6], 16));
        if (s.Length == 8 && !over.IsEmpty)
        {
            var a = System.Convert.ToInt32(s[6..8], 16) / 255.0;
            if (a == 0) return Color.Empty; // trong suốt hoàn toàn = coi như không khai báo
            c = Mix(over, c, a);            // màu có alpha: trộn lên nền vì WinForms chỉ có màu đặc
        }
        return c;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
    private static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) && ch < 128 ? ch : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 0 ? slug : Guid.NewGuid().ToString("N")[..8];
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static readonly JsonDocumentOptions JsoncOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static JsonObject ParseObject(string json) =>
        JsonNode.Parse(json, documentOptions: JsoncOptions) as JsonObject ?? throw new InvalidDataException("File không phải theme VS Code (cần 1 object JSON).");

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path)
    {
        var want = NormalizeZipPath(path);
        return zip.GetEntry(want)
            ?? zip.Entries.FirstOrDefault(e => string.Equals(NormalizeZipPath(e.FullName), want, StringComparison.OrdinalIgnoreCase));
    }

    private static string ZipDir(string entryPath) => entryPath.Contains('/') ? entryPath[..(entryPath.LastIndexOf('/') + 1)] : "";

    private static string NormalizeZipPath(string p)
    {
        var parts = new List<string>();
        foreach (var seg in p.Replace('\\', '/').Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }
}
