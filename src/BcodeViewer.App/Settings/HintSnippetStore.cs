using System.Text.Json;
using System.Text.Json.Serialization;

namespace BcodeViewer.App.Settings;

/// <summary>
/// One "Hint Code" entry — matches FCodeViewer's own Hint Code panel fields (Category,
/// Type, Tag/Keywords, Description, the code itself, and who created/last touched it),
/// plus what it takes to surface the same entry as a Monaco completion item
/// (<see cref="Prefix"/>/<see cref="ShowInIntelliSense"/>/<see cref="PathScope"/>).
/// BcodeViewer keeps its own local library rather than reading FCode's — that data lives
/// in FCode's own fcoderdb.s3db (a SQLite file), which isn't something to reach into.
/// </summary>
public class HintSnippet
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Category { get; set; } = "JS"; // JS / SQL / XML / CSS
    public string Type { get; set; } = "Declare";
    public string Tags { get; set; } = "";
    public string Description { get; set; } = "";
    public string Code { get; set; } = "";
    public string CreatedBy { get; set; } = Environment.UserName;
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public string ModifiedBy { get; set; } = Environment.UserName;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    /// <summary>
    /// The word typed to pull this snippet out of the suggestion list ("fld", "cmd",
    /// "grid"...) — VSCode calls it the same thing. Empty means the snippet is
    /// insert-by-hand only (the pre-existing Hint Code "Insert" button), which is why
    /// <see cref="ForCompletion"/> requires it: a completion item with no prefix would sit
    /// in every suggestion list keyed off nothing but its description.
    /// </summary>
    public string Prefix { get; set; } = "";

    /// <summary>Lets a snippet stay in the Hint Code library for manual insert while being
    /// kept out of the as-you-type list — a long boilerplate block nobody wants popping up
    /// mid-word, for instance.</summary>
    public bool ShowInIntelliSense { get; set; } = true;

    /// <summary>
    /// Optional path filter, semicolon-separated globs matched against the open file's full
    /// path ("*\Controllers\*;*\Grid\*"). A Dir controller and a Report XML are both "XML"
    /// as far as Category goes but want completely different boilerplate, and Category
    /// alone can't tell them apart. Empty = offered in every file of the right language.
    /// </summary>
    public string PathScope { get; set; } = "";

    /// <summary>True for entries loaded from <see cref="ViewerSettings.SharedTemplatePath"/>
    /// — usable everywhere, editable nowhere (the Hint Code form puts its detail panel in
    /// read-only mode for these). Not persisted: it's a fact about where the entry was read
    /// from, re-established on every load.</summary>
    [JsonIgnore]
    public bool IsShared { get; set; }

    /// <summary>Which shared file this came from, shown in the Hint Code list so it's
    /// obvious a suggestion is the team's and not yours. Not persisted, same reason.</summary>
    [JsonIgnore]
    public string SourceLabel { get; set; } = "";

    /// <summary>A snippet is only worth handing to Monaco if it has something to match on
    /// and hasn't been opted out.</summary>
    [JsonIgnore]
    public bool ForCompletion => ShowInIntelliSense && !string.IsNullOrWhiteSpace(Prefix) && !string.IsNullOrWhiteSpace(Code);
}

public class HintSnippetStore
{
    /// <summary>The personal library — the only list that gets written back by Save().</summary>
    public List<HintSnippet> Snippets { get; set; } = new();

    /// <summary>Team library, merged in at load time from
    /// <see cref="ViewerSettings.SharedTemplatePath"/>. Never saved (see Save()).</summary>
    [JsonIgnore]
    public List<HintSnippet> Shared { get; set; } = new();

    /// <summary>Everything the UI and IntelliSense see: personal first, so a personal
    /// snippet sharing a prefix with a team one sorts above it in the suggestion list.</summary>
    [JsonIgnore]
    public IEnumerable<HintSnippet> All => Snippets.Concat(Shared);

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "viewer-hints.json");

    /// <param name="sharedPath">Team library folder, or null/empty for personal only.</param>
    /// <remarks>
    /// Reads the team folder inline, so it takes as long as that share takes to answer.
    /// Callers on the UI thread want <see cref="LoadPersonal"/> plus a background
    /// <see cref="LoadSharedOnly"/> instead — see EditorBridge's constructor for why.
    /// </remarks>
    public static HintSnippetStore Load(string? sharedPath = null)
    {
        var store = LoadPersonal();
        store.Shared = LoadShared(sharedPath);
        return store;
    }

    /// <summary>
    /// The personal library only (<c>%AppData%\Bcode\viewer-hints.json</c>) — a local file,
    /// so this is safe to call where a stall would be felt. Split out from
    /// <see cref="Load"/> because the team folder it used to read in the same breath is
    /// typically a UNC share: loading both together meant a slow or absent share held up
    /// whatever asked, including the editor's own startup.
    /// </summary>
    public static HintSnippetStore LoadPersonal()
    {
        HintSnippetStore store;
        var isFirstRun = !File.Exists(StorePath);
        try
        {
            store = (File.Exists(StorePath)
                ? JsonSerializer.Deserialize<HintSnippetStore>(File.ReadAllText(StorePath))
                : null) ?? new HintSnippetStore();
        }
        catch
        {
            store = new HintSnippetStore(); // corrupt file — start clean rather than refusing to open
        }

        // Seed on first run only. An empty suggestion list looks identical to a broken one,
        // so ship enough FCode-shaped snippets that the feature is visibly working the first
        // time someone types "fld" — and, just as usefully, so the tabstop syntax is there
        // to copy when writing their own.
        if (isFirstRun && store.Snippets.Count == 0)
        {
            store.Snippets.AddRange(BuiltInSeeds());
            try { store.Save(); } catch { /* read-only %AppData% — seeds still work this session */ }
        }

        return store;
    }

    /// <summary>The team folder only, for callers loading it off the UI thread. Same
    /// best-effort contract as the rest of this class: an unreachable share is an empty
    /// list, never an exception.</summary>
    public static List<HintSnippet> LoadSharedOnly(string? sharedPath) => LoadShared(sharedPath);

    public void Save()
    {
        var dir = Path.GetDirectoryName(StorePath)!;
        Directory.CreateDirectory(dir);
        // Shared is [JsonIgnore], so this writes the personal list only — a team snippet can
        // never be silently copied into someone's private file just by them opening the Hint
        // Code dialog.
        File.WriteAllText(StorePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Reads every snippet file in the team folder. Two formats are accepted because they
    /// serve different people: *.json is what BcodeViewer's own "Export..." writes (keeps
    /// Category/Type/PathScope), while *.code-snippets is VSCode's format — so a snippet
    /// pack written for VSCode drops straight in, and anything exported from here can be
    /// committed to a repo's .vscode/ folder and still work for whoever edits in VSCode.
    /// Every failure here is swallowed per-file: the share being down, or one file being
    /// half-written by a teammate mid-save, must not stop BcodeViewer from opening.
    /// </summary>
    private static List<HintSnippet> LoadShared(string? sharedPath)
    {
        var result = new List<HintSnippet>();
        if (string.IsNullOrWhiteSpace(sharedPath)) return result;

        try
        {
            if (!Directory.Exists(sharedPath)) return result;

            foreach (var file in Directory.EnumerateFiles(sharedPath, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".json" && ext != ".code-snippets") continue;

                var label = Path.GetFileNameWithoutExtension(file);
                try
                {
                    var text = File.ReadAllText(file);
                    var parsed = ext == ".code-snippets"
                        ? ParseVsCodeSnippets(text)
                        : JsonSerializer.Deserialize<HintSnippetStore>(text)?.Snippets ?? new List<HintSnippet>();

                    foreach (var s in parsed)
                    {
                        s.IsShared = true;
                        s.SourceLabel = label;
                        result.Add(s);
                    }
                }
                catch { /* one bad file shouldn't cost the whole shared library */ }
            }
        }
        catch { /* UNC share unreachable right now — personal library still works */ }

        return result;
    }

    /// <summary>
    /// VSCode's .code-snippets shape: { "Name": { prefix, body, description, scope } }.
    /// `body` is an array of lines (a plain string is also legal), `scope` a comma-separated
    /// language id list. Its tabstop syntax is Monaco's own — Monaco IS VSCode's editor — so
    /// the body needs no translation, only rehousing.
    /// </summary>
    private static List<HintSnippet> ParseVsCodeSnippets(string json)
    {
        var result = new List<HintSnippet>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

        foreach (var entry in doc.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var v = entry.Value;

            var body = "";
            if (v.TryGetProperty("body", out var bodyEl))
            {
                body = bodyEl.ValueKind == JsonValueKind.Array
                    ? string.Join("\n", bodyEl.EnumerateArray().Select(x => x.GetString() ?? ""))
                    : bodyEl.GetString() ?? "";
            }
            if (string.IsNullOrWhiteSpace(body)) continue;

            var prefix = "";
            if (v.TryGetProperty("prefix", out var prefixEl))
            {
                // VSCode allows an array of prefixes; take the first — the rest are aliases,
                // and a second HintSnippet per alias would just duplicate the list entry.
                prefix = prefixEl.ValueKind == JsonValueKind.Array
                    ? prefixEl.EnumerateArray().FirstOrDefault().GetString() ?? ""
                    : prefixEl.GetString() ?? "";
            }

            var scope = v.TryGetProperty("scope", out var scopeEl) ? scopeEl.GetString() ?? "" : "";
            var description = v.TryGetProperty("description", out var descEl) ? descEl.GetString() ?? "" : "";

            result.Add(new HintSnippet
            {
                // Stable id derived from the entry's own name: a shared snippet is re-read on
                // every launch, and a fresh Guid each time would churn any selection keyed
                // off it in the Hint Code list.
                Id = "shared-" + entry.Name.GetHashCode().ToString("x8"),
                Category = CategoryFromScope(scope),
                Type = "Snippet",
                Tags = entry.Name,
                Description = string.IsNullOrWhiteSpace(description) ? entry.Name : description,
                Code = body,
                Prefix = prefix,
            });
        }
        return result;
    }

    private static string CategoryFromScope(string scope)
    {
        var s = scope.ToLowerInvariant();
        if (s.Contains("sql")) return "SQL";
        if (s.Contains("xml") || s.Contains("html")) return "XML";
        if (s.Contains("css")) return "CSS";
        return "JS";
    }

    /// <summary>
    /// Writes the given snippets as a VSCode .code-snippets file — the "publish to the team"
    /// half of the shared library (BcodeViewer only ever READS the share; dropping the file
    /// there, or committing it, stays a deliberate act). Chosen over our own JSON format as
    /// the export default precisely because it's the one both editors understand.
    /// </summary>
    public static void ExportVsCodeSnippets(string path, IEnumerable<HintSnippet> snippets)
    {
        var map = new Dictionary<string, object>();
        foreach (var s in snippets)
        {
            var key = string.IsNullOrWhiteSpace(s.Tags) ? s.Description : s.Tags;
            if (string.IsNullOrWhiteSpace(key)) key = s.Prefix;
            if (string.IsNullOrWhiteSpace(key)) continue;
            // Two snippets legitimately share a Tags value; a JSON object can't hold the key
            // twice, and last-one-wins would silently drop one, so disambiguate instead.
            var unique = key;
            for (var n = 2; map.ContainsKey(unique); n++) unique = $"{key} ({n})";

            map[unique] = new
            {
                prefix = s.Prefix,
                body = s.Code.Replace("\r\n", "\n").Split('\n'),
                description = s.Description,
                scope = ScopeFromCategory(s.Category),
            };
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string ScopeFromCategory(string category) => category.ToUpperInvariant() switch
    {
        "SQL" => "sql",
        "XML" => "xml,html",
        "CSS" => "css",
        _ => "javascript",
    };

    /// <summary>
    /// First-run content. Deliberately FCode-shaped rather than generic language snippets —
    /// Monaco already covers plain JS/SQL keywords on its own, and what it can't know is how
    /// this codebase's XML is actually structured. Tabstops are written out in full so these
    /// double as worked examples of the syntax.
    /// </summary>
    private static IEnumerable<HintSnippet> BuiltInSeeds() => new[]
    {
        new HintSnippet
        {
            Category = "XML", Type = "Declare", Prefix = "fld", Tags = "field",
            Description = "Khai báo 1 <field>",
            Code = "<field name=\"${1:ten_field}\" caption=\"${2:Nhãn}\" type=\"${3|string,int,decimal,datetime,bool|}\" width=\"${4:100}\" />$0",
        },
        new HintSnippet
        {
            Category = "XML", Type = "Declare", Prefix = "fldhidden", Tags = "field hidden",
            Description = "Field ẩn (khoá chính)",
            Code = "<field name=\"${1:stt_rec}\" caption=\"${1:stt_rec}\" type=\"string\" visible=\"false\" />$0",
        },
        new HintSnippet
        {
            Category = "XML", Type = "Block", Prefix = "fields", Tags = "fields block",
            Description = "Khối <fields> ... </fields>",
            Code = "<fields>\n\t<field name=\"${1:ma_dvcs}\" caption=\"${2:Đơn vị}\" type=\"string\" width=\"${3:100}\" />\n\t$0\n</fields>",
        },
        new HintSnippet
        {
            Category = "XML", Type = "Declare", Prefix = "cmd", Tags = "command",
            Description = "Khai báo 1 <command>",
            Code = "<command event=\"${1|onload,onsave,ondelete,onchange,onclick|}\" caption=\"${2:Nhãn}\">\n\t$0\n</command>",
        },
        new HintSnippet
        {
            Category = "XML", Type = "Declare", Prefix = "act", Tags = "action",
            Description = "Khai báo 1 <action>",
            Code = "<action id=\"${1:id}\" caption=\"${2:Nhãn}\" icon=\"${3:}\" />$0",
        },
        new HintSnippet
        {
            Category = "XML", Type = "Declare", Prefix = "ent", Tags = "entity include",
            Description = "Khai báo ENTITY (include file ngoài)",
            Code = "<!ENTITY ${1:TenEntity} SYSTEM \"${2:../Common/file.f}\">$0",
        },
        new HintSnippet
        {
            Category = "XML", Type = "Declare", Prefix = "view", Tags = "view",
            Description = "Khai báo 1 <view>",
            Code = "<view id=\"${1:view_id}\" caption=\"${2:Nhãn}\">\n\t$0\n</view>",
        },
        new HintSnippet
        {
            Category = "SQL", Type = "Query", Prefix = "sel", Tags = "select where",
            Description = "SELECT TOP ... WHERE",
            Code = "SELECT TOP ${1:100} ${2:*}\nFROM ${3:bang}\nWHERE ${4:1 = 1}\nORDER BY ${5:stt_rec}$0",
        },
        new HintSnippet
        {
            Category = "SQL", Type = "Query", Prefix = "updfrom", Tags = "update join",
            Description = "UPDATE ... FROM ... JOIN",
            Code = "UPDATE a\nSET a.${1:cot} = ${2:gia_tri}\nFROM ${3:bang} a\nJOIN ${4:bang2} b ON b.${5:khoa} = a.${5:khoa}\nWHERE ${6:1 = 1}$0",
        },
        new HintSnippet
        {
            Category = "SQL", Type = "Block", Prefix = "trybegin", Tags = "transaction try catch",
            Description = "BEGIN TRAN + TRY/CATCH",
            Code = "BEGIN TRY\n\tBEGIN TRANSACTION;\n\t$0\n\tCOMMIT TRANSACTION;\nEND TRY\nBEGIN CATCH\n\tIF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;\n\tTHROW;\nEND CATCH",
        },
        new HintSnippet
        {
            Category = "JS", Type = "Function", Prefix = "fn", Tags = "function",
            Description = "Khai báo function",
            Code = "function ${1:tenHam}(${2:thamSo}) {\n\t$0\n}",
        },
        new HintSnippet
        {
            Category = "JS", Type = "Block", Prefix = "foreach", Tags = "loop for",
            Description = "Vòng lặp for qua mảng",
            Code = "for (var ${1:i} = 0; ${1:i} < ${2:arr}.length; ${1:i}++) {\n\tvar ${3:item} = ${2:arr}[${1:i}];\n\t$0\n}",
        },
    };
}
