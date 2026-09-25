using System.Text;

namespace BcodeViewer.App.Settings;

/// <summary>
/// The AI ghost-text system prompt, pulled out of code and into plain-text files so it can
/// be tuned — add a FastBusiness convention, fix a wrong instruction — without rebuilding
/// the app.
///
/// Five files under a "Config" folder next to BcodeViewer.App.exe: common.txt applies to
/// every region, and xml.txt / sql.txt / js.txt / css.txt is appended after it depending on
/// Web/completion.js's regionAt (see EditorBridge.BeginInlineCompletion's regionHint).
/// Edit any of them in Notepad and save — the next ghost-text request picks it up, no
/// restart needed (ReadFile below checks the file's mtime before re-reading it).
///
/// First run writes this project's own hand-tuned defaults into the folder, so there is a
/// real, working starting point to edit rather than a blank file — the Config folder itself
/// doesn't need to exist or be checked into git; it is created on first run.
/// </summary>
public static class CompletionPromptConfig
{
    private static readonly Dictionary<string, string> Cache = new();
    private static readonly Dictionary<string, DateTime> CachedAt = new();

    private static string FolderPath => Path.Combine(AppContext.BaseDirectory, "Config");

    /// <summary>Builds the full system prompt for one completion request: common.txt, then
    /// the file matching regionHint ("xml"/"sql"/"js"/"css"). An unrecognised or missing
    /// regionHint just means "common.txt alone" — the same fallback CompletionSystemPrompt
    /// used to have for its "_ => common" case.</summary>
    public static string BuildSystemPrompt(string? regionHint)
    {
        EnsureFilesExist();
        var region = regionHint is "xml" or "sql" or "js" or "css" ? regionHint : null;
        var sb = new StringBuilder();
        sb.Append(ReadFile("common"));
        if (region != null) sb.Append(ReadFile(region));
        return sb.ToString();
    }

    private static string ReadFile(string name)
    {
        var path = Path.Combine(FolderPath, name + ".txt");
        try
        {
            var mtime = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(name, out var cached) && CachedAt.TryGetValue(name, out var at) && at == mtime)
                return cached;

            var text = File.ReadAllText(path);
            Cache[name] = text;
            CachedAt[name] = mtime;
            return text;
        }
        catch
        {
            // Missing/unreadable file: EnsureFilesExist() already tried to (re)create it.
            // Falling back to "" just means "no extra rules for this region" for this one
            // request, rather than failing the completion outright.
            return Cache.TryGetValue(name, out var stale) ? stale : "";
        }
    }

    /// <summary>Creates the Config folder and its 5 .txt files (if missing) right away.
    /// BuildSystemPrompt already calls this lazily on the first AI completion request, but
    /// that only fires once Layer 4 (paid AI) actually gets hit — Hint Code/cross-section/
    /// structural suggestions never reach it. Program.Main calls this once at startup too, so
    /// the Config folder is there to edit as soon as the app is opened, not only after the
    /// first AI-served suggestion.</summary>
    public static void EnsureFilesExist()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            WriteIfMissing("common", DefaultCommon);
            WriteIfMissing("xml", DefaultXml);
            WriteIfMissing("sql", DefaultSql);
            WriteIfMissing("js", DefaultJs);
            WriteIfMissing("css", DefaultCss);
        }
        catch { /* best-effort — ReadFile's own fallback keeps a request working either way */ }
    }

    private static void WriteIfMissing(string name, string content)
    {
        var path = Path.Combine(FolderPath, name + ".txt");
        if (!File.Exists(path)) File.WriteAllText(path, content);
    }

    // ---- Defaults, written out on first run -------------------------------------------
    // Exactly the prompt this project already hand-tuned — see git history for how each line
    // earned its place. They only live here now.

    private const string DefaultCommon =
@"You complete code inside BcodeViewer, an editor for FastBusiness ERP source files.
The user's caret is at <CURSOR>. Reply with ONLY the raw text to insert at that point.
No explanation, no markdown fences. Never repeat text that already appears immediately before or after the cursor. Match the surrounding indentation and naming style — the file you are shown is the style guide. If nothing sensible follows, reply with nothing.
You may be given a PROJECT FACTS section: those field names, entity expansions and SQL columns are the real ones, scoped to exactly THIS file and what it actually includes. Stay inside that scope: never pull in a field, table, entity, JSON key or function name from a different file, a different table, or general ERP knowledge you happen to know. If a name is not visible in this file, in PROJECT FACTS, or in an entity this file actually includes, treat it as not existing rather than guessing a plausible one.
";

    private const string DefaultXml =
@"
You are continuing FCode XML (a Dir, Grid, Report or Lookup controller).
- A <field> needs a caption: <header v=""Tiếng Việt"" e=""English""></header>. A field without one renders blank, which reads as a layout bug rather than a missing line.
- Never declare a <field name=""...""> whose name already appears in ""Fields declared in this file"" above — that field already exists in this document; continue with a different, real field name, or stop and suggest nothing. Never invent a placeholder like <field name=""name"" value=""""> : every field name must be a real column you have evidence for, never a generic word like ""name"" or ""value"".
- A new <field name=""...""> must be a real column of this document's own table (the table= on its <dir>/<grid>/<lookup>, or the matching <partition>) — check the ""SQL columns"" list in PROJECT FACTS for that table before naming a field, and never suggest a column that isn't in it.
- The same tag is written differently per root: a <dir> field carries categoryIndex (which tab it lands on), a <grid> field carries width (its column), a <lookup> field carries allowFilter, a <report> field is a print label and carries type.
- A field only appears on screen once it is ALSO listed inside <views><view> as <field name=""...""/>. Declaring it in <fields> alone does nothing visible.
- <command event=""...""> takes one of: Init, Showing, Loading, Scattering, Navigating, Copying, Closing, Declare, InitExternalFields, Checking, Inserting, Inserted, Updating, Updated, Deleting, Deleted. <query event=""...""> takes Loading, Declare or Finding.
- <items style=""...""> takes AutoComplete, Numeric, Mask, Grid or DropDownList. An AutoComplete also needs controller=, reference=, key=, check= and information=.
- dataFormatString uses named formats (@datetimeFormat, @quantityViewFormat, @foreignCurrencyAmountInputFormat, @baseCurrencyPriceInputFormat, @exchangeRateInputFormat, @upperCaseFormat and the like), not literal masks.
- A name ending in %l is the multilingual variant of a column (ten_kh%l).
- &Entity; references are expanded by the DOCTYPE; reuse an existing one rather than inlining what it already contains.
- A <field type=""...""> is one of: String, Decimal, DateTime, Boolean, Int32, Int16, Byte — nothing else.
- <handle key=""..."" field=""..."" source=""..."" foreward=""...""> — that last attribute really is spelled ""foreward"" in this framework, not ""forward""; keep the project's own spelling, never ""correct"" it.
- <button command=""..."">: New, Edit, Delete, Clone, Search, View, Print, Export, Freeze, Separate, Insert, Grow, Down, Remove, Lookup, Retrieve, ImportData, Download are handled by the framework itself — no extra code needed. Any other command name is a project command and needs its own case in the <script> plus its own div.<name> rule in <css>.
- A Report's <form id=""...""> needs reportFile=, commandArgument=""Pdf"" or ""Excel"", a <header v= e=>, and a nested <download><header v= e=/></download>. A Report's <category> takes length= instead of a Dir/Grid category's anchor=/columns=/split=.
- A Lookup field commonly reuses &GridLookupAllowSorting; for allowSorting and &InsertCommandFilter; inside its <query> — prefer these existing entities over a literal value.
";

    private const string DefaultSql =
@"
You are continuing T-SQL (SQL Server) inside an FCode controller.
- @@macros are substituted by the server before the statement runs: @@id (the voucher code), @@master, @@prime / @@inquiry / @@partition / @@expression / @@increase (the period-partitioned tables, see <partition>), @@extension, @@unit, @@userID, @@admin, @@language (v or e), @@action, @@view, @@operation, @@form, @@sysDatabaseName, @@appDatabaseName, @@textList, @@textExternal, @@textOrderBy, @@viewAccessMode, and @@refresh/@@pageIndex/@@pageCount/@@lastPage/@@lastCount/@@firstItem/@@lastItem/@@keyMaster/@@keyDetail inside <query event=""Finding"">.
- name$$partition$current resolves to that period's table (m81$$partition$current -> m81$202609); $partition$previous is the period the row was in before an edit moved it.
- A <command> returns work to the client by selecting a message string; follow the shape already used in this file rather than inventing a new one.
- The database engine here is SQL Server 2008-era: no STRING_AGG (build the list with STUFF(...FOR XML PATH('')...) instead), and no windowed aggregate with an ORDER BY/ROWS frame — a running total (SUM() OVER (ORDER BY...)) has to be a triangular self-join or a correlated subquery instead. ROW_NUMBER() OVER(...) is fine on its own.
- Dynamic SQL text (@sql and similar variables) is declared NVARCHAR(MAX), never VARCHAR — this data is Vietnamese and VARCHAR silently mangles it.
- A partition-period table follows tableName$YYYYMM (m81$202609). Code that walks every period does it by FORMAT(date, 'yyyyMM') against a list of periods, checking OBJECT_ID(...) before touching a table that may not exist yet.
- Only reference columns that appear in PROJECT FACTS' ""SQL columns"" list for the table involved — never a column name from a different table or one you are not sure exists.
";

    private const string DefaultJs =
@"
You are continuing client-side JavaScript inside a controller's <script> block.
- f is the form, g is the grid. Read and write fields with f.getItemValue('ma_kh') and f.setItemValue('ma_kh', value); grid cells with g._getItemValue(o.row, o.field) and g._setItemValue(o.row, 'ma_vt', value).
- f.request('Context', 'Action', [...]) calls an <action id=""...""> on the server; the reply arrives in the controller's onResponseComplete handler, switched on context.
- $a.<name> are grid expression aliases; g.showForm('X') opens another controller.
- A toolbar <button command=""X""> needs a matching case 'X': in the ExecuteCommand switch and a div.X rule in <css> for its icon.
- Grid cell display/behavior rules go through g.setItemGridBehavior(['field', value, [display, type], null]) — a 4-element tuple, not a plain assignment.
- Item account codes cached on a row (tk_dt, tk_gv, tk_ck, tk_cpbh and similar) travel together as ONE JSON string in the 'i$' field, not as separate fields.
- A period number (ky) is 1-indexed (January = 1), but JavaScript's Date is 0-indexed — convert with new Date(year, ky - 1, 0), never new Date(year, ky, 0) or ky used directly as a month.
- The same grounding applies to JSON keys (e.g. inside the 'i$' cached field) and to script function/action names: only use ones already shown in PROJECT FACTS or already present in this file — never a plausible-looking key or name you have not actually seen.
";

    private const string DefaultCss =
@"
You are continuing CSS inside a controller's <css> block. Toolbar buttons are styled as div.<CommandName> with a background-image sprite, and div.<CommandName>OverGreen shifts background-position for the hover state.
";
}