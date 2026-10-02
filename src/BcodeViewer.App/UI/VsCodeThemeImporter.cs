using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BcodeViewer.App.UI;

/// <summary>
/// Turns a VSCode color theme into a <see cref="ThemeDefinition"/>, so any theme from the
/// Marketplace can be used without adding it to <see cref="ThemeCatalog.BuiltIn"/> by hand.
///
/// Accepts the theme's own .json (JSONC: comments and trailing commas allowed, "include" of a
/// base theme resolved next to it) or a whole .vsix as downloaded from the Marketplace (every
/// theme listed under contributes.themes in its package.json). What gets stored in
/// %AppData%\Bcode\viewer-themes is the theme flattened into ONE json — includes already
/// merged — so it no longer depends on where it was imported from; it is re-converted on
/// every load, which means a better mapping here applies to themes imported earlier too.
///
/// Mapping: the workbench "colors" fill the WinForms/page palette (sideBar → Panel,
/// button → Accent, ...); "tokenColors" (TextMate scopes) are resolved for the handful of
/// token types this app's Monarch tokenizers actually emit (tag, attribute.name, keyword,
/// string, comment...), using TextMate's own rule: the most specific matching selector wins,
/// later rules break ties. Colors with alpha are blended over the editor background, since
/// WinForms colors are opaque.
/// </summary>
public static class VsCodeThemeImporter
{
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "viewer-themes");

    private static readonly JsonDocumentOptions JsoncOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // ---- import ----------------------------------------------------------------------------

    /// <summary>Imports a .json or .vsix; returns the ids of the themes added (empty if the file
    /// held none). Throws with a readable message when the file can't be used.</summary>
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
        return added;
    }

    /// <summary>Removes an imported theme by id. Built-in themes are not files and can't be removed.</summary>
    public static void Delete(string id)
    {
        var file = Path.Combine(Folder, id + ".json");
        if (File.Exists(file)) File.Delete(file);
    }

    private static string Save(JsonObject flat, string fallbackName)
    {
        var name = Str(flat["name"]);
        if (string.IsNullOrWhiteSpace(name)) { name = fallbackName; flat["name"] = name; }
        // Fail at import time rather than at the next start: converting once here proves the file is usable.
        _ = Convert(flat, "custom-check");
        var id = "custom-" + Slug(name!);
        File.WriteAllText(Path.Combine(Folder, id + ".json"), flat.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return id;
    }

    /// <summary>Follows "include" (VSCode's base-theme mechanism) and returns one object: the base's
    /// colors overridden by this file's, the base's tokenColors followed by this file's.</summary>
    private static JsonObject Flatten(string path, Func<string, string> read, Func<string, string, string> resolve, int depth)
    {
        if (depth > 5) throw new InvalidDataException("Theme include lồng nhau quá sâu.");
        var obj = ParseObject(read(path));

        if (obj["tokenColors"] is JsonValue tcPath && tcPath.TryGetValue<string>(out var tcFile))
        {
            if (!tcFile.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("tokenColors trỏ tới file .tmTheme — chưa hỗ trợ, chỉ nhận theme dạng .json.");
            var tc = ParseNode(read(resolve(path, tcFile)));
            obj["tokenColors"] = tc is JsonObject o && o["tokenColors"] is JsonArray inner ? inner.DeepClone() : tc?.DeepClone();
        }

        if (Str(obj["include"]) is not { Length: > 0 } include) return obj;
        var baseObj = Flatten(resolve(path, include), read, resolve, depth + 1);

        var colors = baseObj["colors"]?.DeepClone() as JsonObject ?? new JsonObject();
        if (obj["colors"] is JsonObject own)
            foreach (var (k, v) in own) colors[k] = v?.DeepClone();
        var tokens = new JsonArray();
        foreach (var src in new[] { baseObj["tokenColors"], obj["tokenColors"] })
            if (src is JsonArray arr) foreach (var t in arr) tokens.Add(t?.DeepClone());

        obj.Remove("include");
        obj["colors"] = colors;
        obj["tokenColors"] = tokens;
        if (obj["type"] is null && baseObj["type"] is { } bt) obj["type"] = bt.DeepClone();
        return obj;
    }

    // ---- load ------------------------------------------------------------------------------

    /// <summary>Every theme in <see cref="Folder"/>; a file that no longer converts is skipped
    /// rather than stopping the app from starting.</summary>
    public static IReadOnlyList<ThemeDefinition> LoadAll()
    {
        var list = new List<ThemeDefinition>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var file in Directory.GetFiles(Folder, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                try { list.Add(Convert(ParseObject(File.ReadAllText(file)), Path.GetFileNameWithoutExtension(file))); }
                catch { /* broken/hand-edited file — leave it out */ }
            }
        }
        catch { /* %AppData% unreadable — built-in themes still work */ }
        return list;
    }

    // ---- conversion ------------------------------------------------------------------------

    private static ThemeDefinition Convert(JsonObject theme, string id)
    {
        var colors = theme["colors"] as JsonObject ?? new JsonObject();
        var type = Str(theme["type"])?.ToLowerInvariant() ?? "";
        var rules = ParseTokenRules(theme["tokenColors"] as JsonArray);

        // Editor background first: every translucent color below is blended over it.
        var defaultFg = rules.FirstOrDefault(r => r.Selectors.Count == 0)?.Foreground;
        var bgGuess = Col(colors, Color.Empty, "editor.background");
        var isDark = type switch
        {
            "light" or "hclight" => false,
            "dark" or "hc" or "hcdark" => true,
            _ => bgGuess.IsEmpty || Luma(bgGuess) < 0.5,
        };
        var bg = bgGuess.IsEmpty ? (isDark ? Rgb(0x1E1E1E) : Color.White) : bgGuess;
        Color C(Color fallback, params string[] keys) => Col(colors, bg, keys) is { IsEmpty: false } c ? c : fallback;

        var text = C(defaultFg is { } dfg ? ParseColor(dfg, bg) : (isDark ? Rgb(0xD4D4D4) : Rgb(0x1F1F1F)), "editor.foreground", "foreground");
        var panel = C(bg, "sideBar.background", "panel.background");
        var panelAlt = C(Mix(panel, text, 0.06), "editorGroupHeader.tabsBackground", "tab.inactiveBackground", "titleBar.activeBackground");
        var border = C(Mix(bg, text, 0.18), "panel.border", "sideBar.border", "editorGroup.border", "contrastBorder", "widget.border");
        var accent = C(isDark ? Rgb(0x0E639C) : Rgb(0x0078D4), "button.background", "focusBorder", "activityBarBadge.background");
        var accentText = C(Luma(accent) > 0.55 ? Rgb(0x1E1E1E) : Color.White, "button.foreground");
        var editorSelection = C(Mix(bg, accent, 0.35), "editor.selectionBackground");

        var def = new ThemeDefinition
        {
            Id = id,
            Name = Str(theme["name"]) is { Length: > 0 } n ? n : id,
            IsDark = isDark,
            MonacoBase = type.StartsWith("hc", StringComparison.Ordinal) && isDark ? "hc-black" : isDark ? "vs-dark" : "vs",
            Background = bg,
            Panel = panel,
            PanelAlt = panelAlt,
            Border = border,
            Text = text,
            TextMuted = C(Mix(text, bg, 0.4), "descriptionForeground", "tab.inactiveForeground", "editorLineNumber.foreground"),
            Accent = accent,
            AccentHover = C(Mix(accent, isDark ? Color.White : Color.Black, 0.15), "button.hoverBackground"),
            AccentText = accentText,
            Selection = C(editorSelection, "list.activeSelectionBackground", "list.inactiveSelectionBackground"),
            Input = C(panel, "input.background", "dropdown.background"),
            ButtonBack = C(panelAlt, "button.secondaryBackground"),
            LineNumber = C(Color.Empty, "editorLineNumber.foreground") is { IsEmpty: false } ln ? ln : null,
            LineHighlight = C(Color.Empty, "editor.lineHighlightBackground") is { IsEmpty: false } lh ? lh : null,
            EditorSelection = editorSelection,
            DirtyMarker = C(isDark ? Color.FromArgb(229, 192, 123) : Rgb(0xA6690A), "gitDecoration.modifiedResourceForeground", "editorWarning.foreground"),
            TokenRules = BuildMonacoRules(rules, bg, text),
            MonacoColors = PassThroughColors(colors),
        };
        return def;
    }

    /// <summary>Workbench color keys Monaco itself understands — copied verbatim (alpha kept, Monaco
    /// handles #RRGGBBAA) so widgets, guides, scrollbars and bracket pair colors look as in VSCode.</summary>
    private static readonly string[] MonacoColorPrefixes =
    {
        "editor.", "editorCursor.", "editorWhitespace.", "editorIndentGuide.", "editorLineNumber.", "editorRuler.",
        "editorBracketMatch.", "editorBracketHighlight.", "editorBracketPairGuide.", "editorWidget.",
        "editorSuggestWidget.", "editorHoverWidget.", "editorGutter.", "editorOverviewRuler.", "editorError.",
        "editorWarning.", "editorInfo.", "editorGhostText.", "editorLink.", "editorStickyScroll.",
        "editorMarkerNavigation.", "editorUnicodeHighlight.", "scrollbar.", "scrollbarSlider.", "minimap.",
        "minimapSlider.", "minimapGutter.", "input.", "inputOption.", "inputValidation.", "list.", "quickInput.",
        "quickInputList.", "peekView.", "peekViewEditor.", "peekViewResult.", "peekViewTitle.", "diffEditor.",
        "widget.", "menu.", "keybindingLabel.", "badge.", "dropdown.", "checkbox.", "toolbar.",
    };

    private static readonly HashSet<string> MonacoColorKeys = new(StringComparer.Ordinal)
    {
        "focusBorder", "foreground", "descriptionForeground", "errorForeground", "contrastBorder",
        "contrastActiveBorder", "selection.background", "icon.foreground", "sash.hoverBorder",
    };

    private static IReadOnlyDictionary<string, string> PassThroughColors(JsonObject colors)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, node) in colors)
        {
            if (Str(node) is not { } value) continue;
            var hex = value.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(hex, "^#([0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")) continue;
            if (MonacoColorKeys.Contains(key) || MonacoColorPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
                result[key] = hex;
        }
        return result;
    }

    /// <summary>Monaco token → TextMate scopes to look up, most representative first. The tokens
    /// are what Web/fcode-language.js and Monaco's built-in xml/sql/javascript/css Monarch
    /// grammars emit; Monaco matches a rule by prefix, so "string" also colors "string.sql".</summary>
    private static readonly (string Token, string[] Scopes)[] TokenScopes =
    {
        ("comment", new[] { "comment.line", "comment.block", "comment" }),
        ("string", new[] { "string.quoted.double", "string.quoted", "string" }),
        ("string.escape", new[] { "constant.character.escape", "constant.character" }),
        ("regexp", new[] { "string.regexp" }),
        ("number", new[] { "constant.numeric", "constant" }),
        ("constant", new[] { "constant.language", "constant" }),
        ("keyword", new[] { "keyword.control", "keyword", "storage.type", "storage" }),
        ("operator", new[] { "keyword.operator", "keyword" }),
        ("delimiter", new[] { "punctuation" }), // tag brackets: see LanguageScopes (.xml/.fcode)
        ("type", new[] { "entity.name.type", "support.type", "storage.type" }),
        ("predefined", new[] { "support.function", "support" }),
        ("tag", new[] { "entity.name.tag" }),
        ("metatag", new[] { "meta.tag.preprocessor", "entity.name.tag" }),
        ("attribute.name", new[] { "entity.other.attribute-name" }),
        ("attribute.value", new[] { "string.quoted.double.xml", "string.quoted.double", "string" }),
        ("function", new[] { "entity.name.function", "support.function" }),
        ("identifier", new[] { "variable.other.readwrite", "variable.other", "variable" }),
        ("variable", new[] { "variable.other.readwrite", "variable" }),
    };

    /// <summary>Per-language refinements: Monaco suffixes each token with its language
    /// (".fcode", ".xml", ".sql", ".js" — the TypeScript grammar used for JS also emits ".js" —
    /// ".css"), and a rule for "keyword.sql" beats one for "keyword". Each entry is looked up
    /// with the language's own TextMate scope, which is how VSCode tells an SQL keyword from a
    /// JS one, an XML tag bracket from any other punctuation, and so on.</summary>
    private static readonly (string Postfix, string Token, string[] Scopes)[] LanguageScopes = BuildLanguageScopes();

    private static (string, string, string[])[] BuildLanguageScopes()
    {
        var xml = new (string Token, string[] Scopes)[]
        {
            ("tag", new[] { "entity.name.tag.localname.xml", "entity.name.tag.xml" }),
            ("delimiter", new[] { "punctuation.definition.tag.xml" }),
            ("attribute.name", new[] { "entity.other.attribute-name.localname.xml", "entity.other.attribute-name.xml" }),
            ("attribute.value", new[] { "string.quoted.double.xml" }),
            ("metatag", new[] { "meta.tag.preprocessor.xml", "entity.name.tag.xml" }),
            ("comment", new[] { "comment.block.xml" }),
            ("string.escape", new[] { "constant.character.entity.xml" }),
            ("delimiter.cdata", new[] { "punctuation.definition.string.begin.xml", "string.unquoted.cdata.xml" }),
        };
        var list = new List<(string, string, string[])>();
        foreach (var postfix in new[] { ".fcode", ".xml" })
            foreach (var (t, s) in xml) list.Add((postfix, t, s));

        list.AddRange(new (string, string, string[])[]
        {
            (".sql", "keyword", new[] { "keyword.other.DML.sql", "keyword.other.sql" }),
            (".sql", "operator", new[] { "keyword.operator.sql" }),
            (".sql", "predefined", new[] { "support.function.aggregate.sql", "support.function.sql" }),
            (".sql", "string", new[] { "string.quoted.single.sql" }),
            (".sql", "number", new[] { "constant.numeric.sql" }),
            (".sql", "comment", new[] { "comment.line.double-dash.sql" }),

            (".js", "keyword", new[] { "keyword.control.js", "storage.type.js" }),
            (".js", "identifier", new[] { "variable.other.readwrite.js" }),
            (".js", "type.identifier", new[] { "support.class.js", "entity.name.type.js" }),
            (".js", "string", new[] { "string.quoted.double.js" }),
            (".js", "string.escape", new[] { "constant.character.escape.js" }),
            (".js", "regexp", new[] { "string.regexp.js" }),
            (".js", "number", new[] { "constant.numeric.decimal.js" }),
            (".js", "comment", new[] { "comment.line.double-slash.js" }),
            (".js", "comment.doc", new[] { "comment.block.documentation.js" }),

            (".css", "tag", new[] { "entity.name.tag.css", "entity.other.attribute-name.class.css" }),
            (".css", "attribute.name", new[] { "support.type.property-name.css" }),
            (".css", "attribute.value", new[] { "support.constant.property-value.css" }),
            (".css", "keyword", new[] { "keyword.control.at-rule.css" }),
            (".css", "number", new[] { "constant.numeric.css" }),
            (".css", "string", new[] { "string.quoted.double.css" }),
            (".css", "comment", new[] { "comment.block.css" }),
        });
        return list.ToArray();
    }

    private sealed record TmRule(List<string> Selectors, string? Foreground, string? FontStyle);

    private static List<TmRule> ParseTokenRules(JsonArray? tokenColors)
    {
        var rules = new List<TmRule>();
        if (tokenColors is null) return rules;
        foreach (var node in tokenColors)
        {
            if (node is not JsonObject r || r["settings"] is not JsonObject s) continue;
            var selectors = new List<string>();
            switch (r["scope"])
            {
                case JsonValue v when v.TryGetValue<string>(out var str):
                    selectors.AddRange(str.Split(','));
                    break;
                case JsonArray arr:
                    foreach (var x in arr) if (Str(x) is { } one) selectors.AddRange(one.Split(','));
                    break;
            }
            // Only plain scopes: a descendant selector ("meta.tag string") or an exclusion
            // ("- comment") depends on context this app's tokens don't carry.
            var plain = selectors.Select(x => x.Trim()).Where(x => x.Length > 0 && !x.Contains(' ') && !x.StartsWith('-')).ToList();
            if (selectors.Count > 0 && plain.Count == 0) continue;
            rules.Add(new TmRule(plain, Str(s["foreground"]), Str(s["fontStyle"])));
        }
        return rules;
    }

    /// <summary>TextMate's choice for one scope: the matching selector with the most segments
    /// wins, a later rule beats an earlier one of equal weight. Foreground and fontStyle are
    /// chosen independently, as TextMate does.</summary>
    private static (string? Fg, string? Style) Resolve(List<TmRule> rules, string scope)
    {
        string? fg = null, style = null;
        int fgScore = -1, styleScore = -1;
        foreach (var r in rules)
        {
            foreach (var sel in r.Selectors)
            {
                if (!(scope == sel || scope.StartsWith(sel + ".", StringComparison.Ordinal))) continue;
                var score = sel.Count(c => c == '.') + 1;
                if (r.Foreground is not null && score >= fgScore) { fg = r.Foreground; fgScore = score; }
                if (r.FontStyle is not null && score >= styleScore) { style = r.FontStyle; styleScore = score; }
            }
        }
        return (fg, style);
    }

    private static IReadOnlyList<ThemeDefinition.TokenRule> BuildMonacoRules(List<TmRule> rules, Color bg, Color text)
    {
        var result = new List<ThemeDefinition.TokenRule> { new("", Hex6(text)) };

        ThemeDefinition.TokenRule? Pick(string token, string[] scopes)
        {
            foreach (var scope in scopes)
            {
                var (fg, style) = Resolve(rules, scope);
                if (fg is null) continue;
                var color = ParseColor(fg, bg);
                if (color.IsEmpty) continue;
                var fontStyle = style?.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(x => x is "italic" or "bold" or "underline").ToArray();
                return new(token, Hex6(color), fontStyle is { Length: > 0 } ? string.Join(' ', fontStyle) : null);
            }
            return null;
        }

        foreach (var (token, scopes) in TokenScopes)
            if (Pick(token, scopes) is { } rule) result.Add(rule);

        // Language-specific rules after the generic ones (Monaco: the longer token wins anyway).
        // Nothing in the theme for that scope → no rule, so the generic one keeps applying.
        foreach (var (postfix, token, scopes) in LanguageScopes)
            if (Pick(token + postfix, scopes) is { } rule) result.Add(rule);

        // Monaco's base themes carry rules MORE specific than the generic ones (string.sql = pure
        // red, predefined.sql = magenta, operator.sql grey, delimiter.xml, metatag.xml). Where the
        // theme gave nothing for the language-specific scope, restate the generic color — or the
        // plain text color — so the base's red/magenta never shows through.
        foreach (var (specific, generic) in new[]
        {
            ("string.sql", "string"), ("predefined.sql", "predefined"), ("operator.sql", "operator"),
            ("delimiter.xml", "delimiter"), ("metatag.xml", "metatag"),
        })
        {
            if (result.Any(r => r.Token == specific)) continue;
            var from = result.FirstOrDefault(r => r.Token == generic)
                ?? (generic == "predefined" ? result.FirstOrDefault(r => r.Token == "function") : null);
            result.Add(new(specific, from?.Foreground ?? Hex6(text), from?.FontStyle));
        }
        // Same aliases ThemeCatalog.Syntax adds for XML attribute strings.
        if (result.FirstOrDefault(r => r.Token == "attribute.value") is { } av)
        {
            result.Add(new("string.xml", av.Foreground, av.FontStyle));
            result.Add(new("string.value.xml", av.Foreground, av.FontStyle));
        }
        return result;
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static Color Col(JsonObject colors, Color over, params string[] keys)
    {
        foreach (var k in keys)
            if (Str(colors[k]) is { } s && ParseColor(s, over) is { IsEmpty: false } c) return c;
        return Color.Empty;
    }

    /// <summary>#RGB, #RGBA, #RRGGBB or #RRGGBBAA; alpha is blended over <paramref name="over"/>
    /// (or dropped when there's nothing to blend over yet).</summary>
    private static Color ParseColor(string s, Color over)
    {
        s = s.Trim().TrimStart('#');
        if (s.Length is 3 or 4) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length is not (6 or 8) || !int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out _)) return Color.Empty;
        var r = System.Convert.ToInt32(s[..2], 16);
        var g = System.Convert.ToInt32(s[2..4], 16);
        var b = System.Convert.ToInt32(s[4..6], 16);
        var c = Color.FromArgb(r, g, b);
        if (s.Length == 8 && !over.IsEmpty)
        {
            var a = System.Convert.ToInt32(s[6..8], 16) / 255.0;
            if (a == 0) return Color.Empty; // fully transparent = "not set" for our purposes
            c = Mix(over, c, a);
        }
        return c;
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

    private static double Luma(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

    private static string Hex6(Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) && ch < 128 ? ch : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length > 0 ? slug : Guid.NewGuid().ToString("N")[..8];
    }

    /// <summary>String value of a node, or null for anything else (theme files are hand-written;
    /// a number or null where a color string belongs must not throw).</summary>
    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static JsonNode? ParseNode(string json) => JsonNode.Parse(json, documentOptions: JsoncOptions);

    private static JsonObject ParseObject(string json) =>
        ParseNode(json) as JsonObject ?? throw new InvalidDataException("File không phải theme VS Code (cần 1 object JSON).");

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Entry lookup tolerant of "\" separators and case (zips made by some tools).</summary>
    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string path)
    {
        var want = NormalizeZipPath(path);
        return zip.GetEntry(want)
            ?? zip.Entries.FirstOrDefault(e => string.Equals(NormalizeZipPath(e.FullName), want, StringComparison.OrdinalIgnoreCase));
    }

    private static string ZipDir(string entryPath) => entryPath.Contains('/') ? entryPath[..(entryPath.LastIndexOf('/') + 1)] : "";

    /// <summary>Resolves "./" and "../" inside a zip entry path.</summary>
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
