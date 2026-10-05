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

        if (Path.GetExtension(sourcePath).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            return ImportFcodeXml(sourcePath);

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
        DeleteFromFcodeXml(id);
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
            list.AddRange(LoadFcodeXml(Directory.GetFiles(Folder, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase)));
        }
        catch { /* %AppData% unreadable — built-in themes still work */ }
        return list;
    }

    // ---- FcodeViewer XML (<config><theme name='...'>...</theme></config>) -------------------

    // FcodeViewer split one look over several files that all use the same theme names: the UI
    // config (<background>r,g,b</background>...), the SQL editor theme (<Keyword0 fcolor=..>,
    // <String>...) and the XML editor theme (<Tag>, <Attribute>...). Every stored .xml is read
    // together and themes with the same name are merged into one, so importing all three files
    // gives one "Light Theme"/"Dark Theme" with UI, SQL and XML colors.
    //
    // Unlike VS Code themes the .xml files are stored as-is (not converted to json) and re-read
    // on every start, so editing them in %AppData%\Bcode\viewer-themes takes effect on restart.
    // What Monaco has no slot for is ignored: rowHeightGrid/showBorderCell/icon*/fieldNull,
    // Font/Tab/FoldGroup, token background colors (Keyword7 "go"), and the regex-based
    // <KeywordStart> rules (only index 1 of the XML theme — the script keywords — is used).

    private static List<string> ImportFcodeXml(string sourcePath)
    {
        var themes = LoadFcodeXml(new[] { sourcePath });
        if (themes.Count == 0)
            throw new InvalidDataException("File XML không có <theme> nào (cần dạng <config><theme name='...'>...</theme></config>).");
        File.Copy(sourcePath, Path.Combine(Folder, "fcode-" + Slug(Path.GetFileNameWithoutExtension(sourcePath)) + ".xml"), overwrite: true);
        return themes.Select(t => t.Id).ToList();
    }

    /// <summary>All &lt;theme&gt;s of these files, merged by name. A file that doesn't parse, or a
    /// merged theme that doesn't convert, is left out rather than stopping the rest.</summary>
    private static List<ThemeDefinition> LoadFcodeXml(IEnumerable<string> files)
    {
        var elements = new List<System.Xml.Linq.XElement>();
        foreach (var file in files)
        {
            try { elements.AddRange(System.Xml.Linq.XDocument.Load(file).Root?.Elements("theme") ?? Enumerable.Empty<System.Xml.Linq.XElement>()); }
            catch { /* broken/hand-edited file — leave it out */ }
        }

        var list = new List<ThemeDefinition>();
        foreach (var group in elements.GroupBy(FcodeId))
        {
            try
            {
                var parts = group.ToList();
                var converted = Convert(FcodeToVsCode(parts), group.Key);
                list.Add(FcodeStyle ? WithFcodeLook(converted, parts) : WithBuiltInChrome(converted, parts));
            }
            catch { /* leave it out */ }
        }
        return list;
    }

    /// <summary>ViewerSettings.FcodeThemeStyle == "fcode" — set by MainForm before
    /// <see cref="ThemeCatalog.ReloadCustom"/>.</summary>
    public static bool FcodeStyle { get; set; }

    /// <summary>"bcode" style: an Fcode theme only recolors the editor. In both styles the app's
    /// chrome (menu, toolbar, tree, tabs, dialogs) keeps BcodeViewer's own Dark+/Light+ palette;
    /// the editor gets Fcode's colors through MonacoColors (editor.background/foreground...),
    /// which the page applies on top of the palette-derived ones, and through the token rules.</summary>
    private static ThemeDefinition WithBuiltInChrome(ThemeDefinition fcode, List<System.Xml.Linq.XElement> parts) =>
        Rebuild(fcode, null, false, parts);

    /// <summary>"fcode" style: additionally the editor looks like FcodeViewer's — the font named in
    /// the theme, and no indent guides (Fcode draws none).</summary>
    private static ThemeDefinition WithFcodeLook(ThemeDefinition fcode, List<System.Xml.Linq.XElement> parts)
    {
        var font = parts.Select(p => (string?)p.Element("Font")?.Attribute("name")).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        return Rebuild(fcode, font?.Trim(), true, parts);
    }

    private static ThemeDefinition Rebuild(ThemeDefinition fcode, string? font, bool hideIndentGuides,
        List<System.Xml.Linq.XElement> parts)
    {
        var chrome = ThemeCatalog.BuiltIn.FirstOrDefault(t => t.Id == (fcode.IsDark ? ThemeCatalog.DefaultId : "light-plus"))
            ?? ThemeCatalog.Default;
        var background = chrome.Background;
        var (lexer, lexerRules) = FcodeLexerFor(parts, fcode.Text);
        return new()
        {
            Id = fcode.Id,
            Name = fcode.Name,
            IsDark = fcode.IsDark,
            MonacoBase = fcode.MonacoBase,
            Background = background,
            Panel = chrome.Panel,
            PanelAlt = chrome.PanelAlt,
            Border = chrome.Border,
            Text = chrome.Text,
            TextMuted = chrome.TextMuted,
            Accent = chrome.Accent,
            AccentHover = chrome.AccentHover,
            AccentText = chrome.AccentText,
            Selection = chrome.Selection,
            Input = chrome.Input,
            ButtonBack = chrome.ButtonBack,
            DirtyMarker = chrome.DirtyMarker,
            LineNumber = fcode.LineNumber,
            LineHighlight = fcode.LineHighlight,
            EditorSelection = fcode.EditorSelection,
            TokenRules = fcode.TokenRules.Concat(lexerRules).ToList(),
            MonacoColors = fcode.MonacoColors,
            EditorFont = font,
            HideIndentGuides = hideIndentGuides,
            FcodeLexer = lexer,
        };
    }

    /// <summary>
    /// What FcodeViewer actually does with an XML file, as seen in its screenshots: inside CDATA
    /// (and &lt;!ENTITY&gt; values) nothing is lexed as SQL/JS — only strings ('...' SingleString, "..." DoubleString)
    /// and the editor's text color otherwise (DoubleString for entity values); only the &lt;KeywordStart&gt; words/regexes are
    /// recolored; in the DOCTYPE, "&lt;!ENTITY" and " SYSTEM ..." take their KeywordStart
    /// colors while the entity name is plain text. Returns the lexer the page builds those
    /// regions' tokenizer from, plus the token colors for it (see Web/fcode-language.js for
    /// the token names). Needs the XML editor theme (the part with &lt;Tag&gt;); without it the
    /// theme keeps Monaco's SQL/JS tokenizers.
    /// </summary>
    private static (IReadOnlyList<ThemeDefinition.FcodeLexerRule>? Lexer, List<ThemeDefinition.TokenRule> Rules)
        FcodeLexerFor(List<System.Xml.Linq.XElement> parts, Color text)
    {
        var rules = new List<ThemeDefinition.TokenRule>();
        var xml = parts.FirstOrDefault(p => p.Element("Tag") is not null);
        if (xml is null) return (null, rules);

        void Rule(Color? c, params string[] tokens)
        {
            if (c is not { } col) return;
            foreach (var t in tokens) rules.Add(new(t, Hex6(col)));
        }
        Color? A(string tag) => FcodeColor((string?)xml.Element(tag)?.Attribute("fcolor"));

        var keywords = xml.Element("KeywordStart")?.Elements("Keyword").ToList() ?? new();
        Color? ColorOf(string contains) => keywords
            .Where(k => ((string?)k.Attribute("start") ?? "").Contains(contains, StringComparison.OrdinalIgnoreCase))
            .Select(k => FcodeColor((string?)k.Attribute("fcolor"))).FirstOrDefault(c => c is not null);

        var lexer = new List<ThemeDefinition.FcodeLexerRule>();
        for (var i = 0; i < keywords.Count; i++)
        {
            var start = (string?)keywords[i].Attribute("start");
            var color = FcodeColor((string?)keywords[i].Attribute("fcolor"));
            if (string.IsNullOrWhiteSpace(start) || color is null) continue;
            var token = "kw" + i;
            lexer.Add(new(token, start.Split('~')));
            Rule(color, token + ".fcdata", token + ".fcent");
        }

        // Measured on Fcode screenshots: plain CDATA text is the editor's text color (not CData),
        // '...' is SingleString and keywords inside a string stay the string color.
        Rule(text, "text.fcdata");
        Rule(A("DoubleString"), "text.fcent", "string.double.fcdata", "string.double.fcent");
        Rule(A("SingleString") ?? A("DoubleString"), "string.fcdata", "string.fcent");
        Rule(A("XcComment") ?? A("Comment"), "comment.fcdata", "comment.fcent");

        var tag = A("Tag");
        Rule(tag, "delimiter.fcode");                       // < > </ /> of tags: Fcode paints them with the tag
        Rule(ColorOf("<!ENTITY") ?? tag, "delimiter.entity.fcode", "metatag.entity.fcode");
        Rule(text, "attribute.name.entity.fcode");
        Rule(ColorOf("SYSTEM") ?? A("DoubleString"), "keyword.entity.fcode", "string.entity.fcode");
        Rule(ColorOf("CDATA"), "delimiter.cdata.fcode");
        return (lexer, rules);
    }

    private static string FcodeId(System.Xml.Linq.XElement theme) =>
        "custom-fcode-" + Slug((string?)theme.Attribute("name") ?? "theme");

    /// <summary>Removes the &lt;theme&gt; with this id from every stored .xml (a merged theme lives
    /// in several); a file goes too once it has no theme left.</summary>
    private static void DeleteFromFcodeXml(string id)
    {
        if (!Directory.Exists(Folder)) return;
        foreach (var file in Directory.GetFiles(Folder, "*.xml"))
        {
            System.Xml.Linq.XDocument doc;
            try { doc = System.Xml.Linq.XDocument.Load(file); } catch { continue; }
            var matches = doc.Root?.Elements("theme").Where(t => FcodeId(t) == id).ToList();
            if (matches is not { Count: > 0 }) continue;
            matches.ForEach(t => t.Remove());
            if (doc.Root!.Elements("theme").Any()) doc.Save(file);
            else File.Delete(file);
        }
    }

    /// <summary>"r,g,b" or "#rrggbb"; null for empty/invalid.</summary>
    private static Color? FcodeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.StartsWith('#')) return ParseColor(value, Color.Empty) is { IsEmpty: false } hex ? hex : null;
        var parts = value.Split(',');
        if (parts.Length != 3) return null;
        var v = new int[3];
        for (var i = 0; i < 3; i++)
            if (!int.TryParse(parts[i].Trim(), out v[i]) || v[i] is < 0 or > 255) return null;
        return Color.FromArgb(v[0], v[1], v[2]);
    }

    /// <summary>Restates the FcodeViewer themes sharing one name as a VS Code theme (workbench
    /// colors + TextMate tokenColors), so the same <see cref="Convert"/> that handles VS Code
    /// themes builds it.</summary>
    private static JsonObject FcodeToVsCode(List<System.Xml.Linq.XElement> parts)
    {
        // Which file each part came from, told apart by the elements only that kind has.
        var ui = parts.FirstOrDefault(p => p.Element("background") is not null);
        var sql = parts.FirstOrDefault(p => p.Element("Keyword0") is not null);
        var xml = parts.FirstOrDefault(p => p.Element("Tag") is not null);
        var editor = xml ?? sql ?? parts.FirstOrDefault(p => p.Element("Background") is not null);

        var colors = new JsonObject();
        void Set(Color? c, params string[] keys)
        {
            if (c is not { } col) return;
            foreach (var k in keys) colors[k] = "#" + Hex6(col);
        }
        Color? UiGet(string tag) => FcodeColor((string?)ui?.Element(tag));
        Color? Attr(System.Xml.Linq.XElement? theme, string tag, string attr = "fcolor") =>
            FcodeColor((string?)theme?.Element(tag)?.Attribute(attr));

        // ---- UI config ----
        var bg = UiGet("background");
        var text = UiGet("text");
        Set(bg, "editor.background", "sideBar.background");
        Set(text, "editor.foreground", "foreground");
        // bgHeader/background1 are Fcode's grid header and alternate row colors — not the app's
        // chrome; the menu/toolbar color is left to Convert (sidebar lightened slightly).
        Set(UiGet("border"), "panel.border");
        Set(UiGet("text2"), "descriptionForeground");
        Set(UiGet("primary") ?? UiGet("focus"), "button.background");
        Set(UiGet("focus"), "focusBorder");
        Set(UiGet("warning"), "editorWarning.foreground");
        Set(UiGet("danger"), "editorError.foreground");
        Set(UiGet("bgHighlight"), "editor.findMatchHighlightBackground");
        Set(UiGet("link"), "editorLink.activeForeground");

        // Selected rows keep the app's normal text color (there's no "selected text" slot), so
        // a pale selection under light text — Fcode's Dark Theme — is toned down toward the
        // background until the text stays readable on it.
        if (UiGet("selected") is { } selected)
        {
            var sel = selected;
            if (bg is { } b && text is { } t)
                for (var k = 0.8; k > 0 && Math.Abs(Luma(sel) - Luma(t)) < 0.4; k -= 0.1)
                    sel = Mix(b, selected, k);
            Set(sel, "list.activeSelectionBackground", "editor.selectionBackground");
        }

        // ---- editor (shared part of the SQL/XML theme files; overrides the UI config's
        // editor.background/foreground/selection, since these are the editor's own) ----
        if (editor is not null)
        {
            Set(Attr(editor, "Background", "bcolor"), "editor.background");
            Set(Attr(editor, "Background"), "editor.foreground");
            Set(Attr(editor, "LineMargin", "bcolor"), "editorGutter.background");
            Set(Attr(editor, "LineMargin"), "editorLineNumber.foreground");
            Set(Attr(editor, "CurrentLine", "bcolor"), "editor.lineHighlightBackground");
            Set(Attr(editor, "Selection", "bcolor"), "editor.selectionBackground");
            Set(Attr(editor, "Brace"), "editorBracketMatch.border");
            Set(Attr(editor, "Caret", "bcolor"), "editorCursor.foreground");
            Set(Attr(editor, "Space", "bcolor"), "editorWhitespace.foreground", "editorIndentGuide.background");
            if (Attr(editor, "HighlightWord", "bcolor") is { } hw)
            {
                var alpha = int.TryParse((string?)editor.Element("HighlightWord")?.Attribute("alpha"), out var a) ? Math.Clamp(a, 0, 255) : 80;
                colors["editor.wordHighlightBackground"] = $"#{Hex6(hw)}{alpha:X2}";
                colors["editor.wordHighlightStrongBackground"] = $"#{Hex6(hw)}{alpha:X2}";
            }
        }

        // ---- syntax ----
        var tokens = new JsonArray();
        void Token(Color? c, string? fontStyle, params string[] scopes)
        {
            if (c is not { } col) return;
            var settings = new JsonObject { ["foreground"] = "#" + Hex6(col) };
            if (fontStyle is not null) settings["fontStyle"] = fontStyle;
            tokens.Add(new JsonObject { ["scope"] = string.Join(",", scopes), ["settings"] = settings });
        }
        string? Bold(System.Xml.Linq.XElement? theme, string tag) =>
            (string?)theme?.Element(tag)?.Attribute("bold") == "1" ? "bold" : null;

        if (xml is not null)
        {
            // Generic scopes first (they also cover JS/CSS inside the files), then the XML ones.
            Token(Attr(xml, "Comment"), null, "comment");
            Token(Attr(xml, "DoubleString"), null, "string");
            Token(xml.Element("KeywordStart")?.Elements("Keyword")
                    .Where(k => (string?)k.Attribute("index") == "1")
                    .Select(k => FcodeColor((string?)k.Attribute("fcolor"))).FirstOrDefault(c => c is not null),
                null, "keyword", "storage.type");
            Token(Attr(xml, "Tag"), Bold(xml, "Tag"), "entity.name.tag.xml", "meta.tag.preprocessor.xml", "entity.name.tag");
            Token(Attr(xml, "Attribute"), Bold(xml, "Attribute"), "entity.other.attribute-name.xml", "entity.other.attribute-name");
            Token(Attr(xml, "DoubleString"), null, "string.quoted.double.xml");
            Token(Attr(xml, "Comment"), null, "comment.block.xml");
            Token(Attr(xml, "Entity"), null, "constant.character.entity.xml");
            Token(Attr(xml, "CData"), null, "string.unquoted.cdata.xml");
        }

        if (sql is not null)
        {
            if (xml is null)
            {
                Token(Attr(sql, "Comment"), null, "comment");
                Token(Attr(sql, "String"), null, "string");
            }
            Token(Attr(sql, "Keyword0"), Bold(sql, "Keyword0"), "keyword.other.sql");
            // Monaco's SQL tokenizer emits AND/OR/NOT/IN/LIKE/JOIN/NULL... as operators — Keyword1's words.
            Token(Attr(sql, "Keyword1") ?? Attr(sql, "Operator"), Bold(sql, "Keyword1"), "keyword.operator.sql");
            Token(Attr(sql, "Keyword4"), Bold(sql, "Keyword4"), "support.function.sql");
            Token(Attr(sql, "String"), null, "string.quoted.single.sql");
            Token(Attr(sql, "Number"), null, "constant.numeric.sql");
            Token(Attr(sql, "Comment"), null, "comment.line.double-dash.sql");
        }

        var name = parts.Select(p => (string?)p.Attribute("name")).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        var obj = new JsonObject { ["name"] = name ?? "Fcode theme", ["colors"] = colors, ["tokenColors"] = tokens };
        // Without a UI part Convert would guess dark/light from editor.background — fine — but say
        // it explicitly when the editor background is known, since that's what Monaco's base follows.
        if (FcodeColor(((string?)colors["editor.background"])) is { } eb) obj["type"] = Luma(eb) < 0.5 ? "dark" : "light";
        return obj;
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
