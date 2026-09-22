using System.Runtime.InteropServices;
using System.Text.Json;
using BcodeViewer.App.Settings;
using BcodeViewer.App.UI;

namespace BcodeViewer.App.Host;

/// <summary>
/// Exposed to the page via CoreWebView2.AddHostObjectToScript("host", this) — called from
/// JS as chrome.webview.hostObjects.host.&lt;method&gt;(...), each returning a Promise.
/// All file I/O and the AI call go through here rather than page JS touching the filesystem
/// or the Anthropic key directly: arbitrary file access isn't available to page script
/// anyway, and this keeps one place to handle a locked file or a down UNC path.
///
/// [ComVisible]/[ClassInterface(AutoDual)] are required by WebView2's IDispatch-based
/// marshaling for host objects — without them AddHostObjectToScript still "succeeds" but
/// the page sees an object with no callable members.
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public class EditorBridge
{
    private readonly ClaudeChatService _chat;
    private readonly ViewerSettings _settings;
    private readonly SqlSchemaService _sqlSchema;
    private readonly SqlRunnerService _sqlRunner;
    private readonly Func<string, string?> _chooseSaveAsPath;
    private HintSnippetStore _snippets;

    /// <summary>Cancels the in-flight ghost-text request when a newer one arrives. Only ever
    /// touched under <see cref="_completionLock"/> — host object calls land on arbitrary
    /// WebView2 threads, and two keystrokes racing here would otherwise leak a CTS and leave
    /// an orphaned HTTP call running (and billing).</summary>
    private CancellationTokenSource? _completionCts;
    private readonly object _completionLock = new();

    public EditorBridge(ViewerSettings settings, Func<string, string?> chooseSaveAsPath)
    {
        _settings = settings;
        _chat = new ClaudeChatService(settings);
        _sqlSchema = new SqlSchemaService(settings);
        _sqlRunner = new SqlRunnerService(settings);
        _snippets = HintSnippetStore.Load(settings.SharedTemplatePath);
        _chooseSaveAsPath = chooseSaveAsPath;
    }

    /// <summary>Re-reads the personal + shared snippet libraries — called after the Hint Code
    /// dialog closes and after Settings changes the shared path, so a snippet just saved is
    /// suggestable without restarting. MainForm follows this with a reloadSnippets() call
    /// into the page, which is what actually refreshes Monaco's copy.</summary>
    public void ReloadSnippets() => _snippets = HintSnippetStore.Load(_settings.SharedTemplatePath);

    /// <summary>Lets Settings drop a stale schema after the workspace changes.</summary>
    public void InvalidateSqlSchema() => _sqlSchema.Invalidate();

    /// <summary>Status line for the Settings dialog's SQL "Test" button.</summary>
    public string DescribeSqlStatus() => _sqlSchema.DescribeStatus();

    /// <summary>Raised as the caret moves, so MainForm's status bar can show "Ln X, Col Y"
    /// the way FCodeViewer's own does.</summary>
    public event Action<int, int>? CursorChanged;

    public void NotifyCursorChanged(int line, int column) => CursorChanged?.Invoke(line, column);

    /// <summary>
    /// Raised once the page has finished building window.bcodeViewer, which is the real
    /// "now you can call into me" moment.
    ///
    /// MainForm used to open its initial file from WebView2's NavigationCompleted instead,
    /// which is a different and earlier event: navigation finishing only means the HTML
    /// and scripts arrived, not that the editor object exists. That was already a race, and
    /// it became a reliably lost file once index.html started awaiting the theme before
    /// constructing the editor — the host's `window.bcodeViewer &amp;&amp; ...openFile(...)`
    /// call would short-circuit on an undefined object and silently do nothing, leaving the
    /// window open on an empty editor.
    /// </summary>
    public event Action? PageReady;

    public void NotifyPageReady() => PageReady?.Invoke();

    /// <summary>"Save As" needs a native file picker, which only the WinForms side can show
    /// (and only on the UI thread) — <paramref name="suggestedPath"/> seeds the dialog's
    /// initial folder/name; returns null if the user cancels. The delegate passed in from
    /// MainForm's constructor already handles the UI-thread marshaling.</summary>
    public string? ChooseSaveAsPath(string suggestedPath) => _chooseSaveAsPath(suggestedPath);

    /// <summary>Raised whenever the page (a single-document editor — see editor.js) opens a
    /// new file in place of whatever was shown, so MainForm can add it to the left "recent
    /// files by project" tree and update the window title. Fired from whatever thread
    /// WebView2 dispatches this host object call on — never assume it's the UI thread.</summary>
    public event Action<string>? FileOpened;

    /// <summary>Raised when the open file's dirty (unsaved-changes) state changes, so
    /// MainForm can reflect it in the window title.</summary>
    public event Action<string, bool>? DirtyChanged;

    /// <summary>Raised once the page has actually torn down the open document (see
    /// editor.js's closeActive) — which is also the point at which the user has got past
    /// the unsaved-changes prompt, so the host can safely act on the close rather than
    /// assuming one it requested went through.</summary>
    public event Action<string>? FileClosed;

    public void NotifyFileOpened(string path) => FileOpened?.Invoke(path);

    public void NotifyFileClosed(string path) => FileClosed?.Invoke(path);

    public void NotifyDirtyChanged(string path, bool isDirty) => DirtyChanged?.Invoke(path, isDirty);

    public string ReadFile(string path) => File.ReadAllText(path);

    public void WriteFile(string path, string content) => File.WriteAllText(path, content);

    /// <summary>
    /// The save path used by the editor (see editor.js's saveActive/saveActiveAs). Copies
    /// whatever is on disk into local history BEFORE replacing it, which is the only point
    /// at which a teammate's version — the one the "changed on another machine" banner is
    /// warning about — can still be captured. See LocalHistoryStore for why it snapshots
    /// the old content rather than the new.
    ///
    /// The snapshot is wrapped separately from the write on purpose: a failure to record
    /// history must never turn into a failed save. The write itself is left to throw, so
    /// the page's existing try/catch still reports a locked file or a dead UNC path.
    /// </summary>
    public void SaveWithHistory(string path, string content)
    {
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path);
                // Nothing to preserve if the save is a no-op.
                if (!string.Equals(existing, content, StringComparison.Ordinal))
                    LocalHistoryStore.Snapshot(path, existing);
            }
        }
        catch
        {
            // Unreadable right now (locked by another process, share dropped) — proceed
            // with the save rather than blocking it for the sake of a backup copy.
        }

        File.WriteAllText(path, content);
    }

    /// <summary>JSON array of {"id","savedAt","length"} for one file, newest first.</summary>
    public string GetFileHistory(string path) => LocalHistoryStore.ListJson(path);

    /// <summary>One historical version's full text, or "" if it has since been pruned.</summary>
    public string GetHistorySnapshot(string path, string id) => LocalHistoryStore.Read(path, id) ?? "";

    /// <summary>Opens the file's history folder in Explorer — the escape hatch for anyone
    /// who wants to grab an old version with something other than this editor.</summary>
    public void OpenHistoryFolder(string path)
    {
        var folder = LocalHistoryStore.FolderFor(path);
        if (Directory.Exists(folder)) System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
    }

    public bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    /// <summary>Backs the "file changed on another machine" watch in editor.js: the page
    /// polls this for the currently open file and compares it against the write time it
    /// captured at open/save, showing a reload banner when they differ. Returned as an ISO
    /// "o"-format string rather than ticks/epoch-ms — a DateTime.Ticks value is well past
    /// JS's 2^53 safe-integer range and WebView2's IDispatch marshaling would round-trip it
    /// as a lossy double, breaking the equality check this is used for; string equality has
    /// no such precision concern. Empty string means "can't compare" (file missing/locked),
    /// which the caller treats as "nothing to report" rather than as a change.</summary>
    public string GetFileWriteTimeUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).ToString("o") : "";
        }
        catch
        {
            return ""; // locked/unreadable right now — try again on the next poll
        }
    }

    /// <summary>Backs the "Open Folder" context-menu submenu — opens Explorer at a folder
    /// (or, for a file, opens its containing folder with that file selected), same as
    /// FCode's own quick-access folder shortcuts (Images/Options/Lookup/Templates siblings
    /// of Controllers under App_Data).</summary>
    public void OpenFolder(string path)
    {
        if (File.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
    }

    /// <summary>JSON array of {"name","path","isDirectory"} for one folder level — powers
    /// the page's own "open sibling file" affordance if it wants one beyond the native
    /// WinForms tree on the left; the tree itself is populated directly in MainForm.</summary>
    public string ListDirectory(string path)
    {
        var entries = new List<object>();
        try
        {
            foreach (var d in Directory.GetDirectories(path).OrderBy(x => x))
                entries.Add(new { name = Path.GetFileName(d), path = d, isDirectory = true });
            foreach (var f in Directory.GetFiles(path).OrderBy(x => x))
                entries.Add(new { name = Path.GetFileName(f), path = f, isDirectory = false });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
        return JsonSerializer.Serialize(entries);
    }

    // Synchronous on purpose: this WebView2 SDK version's AddHostObjectToScript is the
    // classic IDispatch-based one (no Async suffix), which predates reliable Task<T>
    // marshaling — JS still sees this as a Promise regardless, since WebView2 dispatches
    // every host object call on its own background thread, so blocking here on the HTTP
    // call doesn't touch the WinForms UI thread.
    public string AskAI(string prompt, string? fileContext, string? filePath) =>
        _chat.AskAsync(prompt, fileContext, filePath).GetAwaiter().GetResult();

    // ---- IntelliSense (see Web/completion.js) ------------------------------------------

    /// <summary>
    /// The whole snippet library as JSON, handed to the page ONCE per load (and again after
    /// ReloadSnippets) rather than queried per keystroke. That's the important part: a
    /// completion provider is called on every character typed, and every host object call
    /// is an IDispatch round trip onto a WebView2 background thread — doing that per
    /// keystroke is exactly how an editor starts feeling laggy. The provider itself runs
    /// against this array in page memory with no host call at all.
    /// </summary>
    public string GetSnippets() => JsonSerializer.Serialize(
        _snippets.All.Where(s => s.ForCompletion).Select(s => new
        {
            prefix = s.Prefix,
            code = s.Code,
            description = s.Description,
            type = s.Type,
            category = s.Category,
            pathScope = s.PathScope,
            shared = s.IsShared,
            source = s.SourceLabel,
        }));

    /// <summary>
    /// The active theme, for the page to turn into CSS custom properties and a Monaco
    /// defineTheme call (see Web/theme.js). The palette lives on this side only — the page
    /// holds no hex values of its own beyond a fallback copy of Dark+ — because the WinForms
    /// chrome and the page have to agree exactly, and two definitions eventually don't.
    /// </summary>
    public string GetTheme()
    {
        var t = ThemeManager.Current;
        return JsonSerializer.Serialize(new
        {
            id = t.Id,
            name = t.Name,
            isDark = t.IsDark,
            monacoBase = t.MonacoBase,
            colors = new
            {
                background = Hex(t.Background),
                panel = Hex(t.Panel),
                panelAlt = Hex(t.PanelAlt),
                border = Hex(t.Border),
                text = Hex(t.Text),
                textMuted = Hex(t.TextMuted),
                accent = Hex(t.Accent),
                accentHover = Hex(t.AccentHover),
                accentText = Hex(t.AccentText),
                selection = Hex(t.Selection),
                input = Hex(t.Input),
                buttonBack = Hex(t.ButtonBack),
            },
            editor = new
            {
                lineNumber = t.LineNumber is { } ln ? Hex(ln) : null,
                lineHighlight = t.LineHighlight is { } lh ? Hex(lh) : null,
                selection = t.EditorSelection is { } es ? Hex(es) : null,
            },
            rules = t.TokenRules.Select(r => new { token = r.Token, foreground = r.Foreground, fontStyle = r.FontStyle }),
        });
    }

    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>Editor-side feature flags, read once at page load — keeps the "is AI
    /// completion on?" decision in settings rather than duplicated in JS.</summary>
    public string GetEditorConfig() => JsonSerializer.Serialize(new
    {
        aiCompletion = _settings.EnableAiCompletion && !string.IsNullOrWhiteSpace(_settings.AnthropicApiKey),
        sqlCompletion = _settings.EnableSqlCompletion,
        sqlRegionTags = (_settings.SqlRegionTags ?? "")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim('<', '>', '/'))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray(),
    });

    /// <summary>Tables/views on the active workspace — see SqlSchemaService for why this is
    /// lazy, cached, and silent on failure.</summary>
    public string GetSqlTables() => _sqlSchema.GetTablesJsonAsync().GetAwaiter().GetResult();

    /// <summary>Columns of one table, fetched the first time a query references it.</summary>
    public string GetSqlColumns(string table) => _sqlSchema.GetColumnsJsonAsync(table).GetAwaiter().GetResult();

    /// <summary>
    /// Ghost text for the caret position. Cancels whatever previous request is still in
    /// flight before starting this one: Monaco asks again on every content change, so
    /// without this a fast typist would have one live HTTP call per character, each billed,
    /// each arriving too late to be useful anyway.
    ///
    /// Blocking on the task is fine here for the same reason AskAI does it — WebView2 calls
    /// host objects on its own background thread, never the WinForms UI thread — and the
    /// page still sees a Promise.
    /// </summary>
    /// <param name="regionHint">"js", "sql", "css" or "xml" — which embedded region of the
    /// document the caret is in (see completion.js's regionAt). An FCode controller is one
    /// .xml file containing all of them, so the file extension alone tells the model the
    /// wrong language for most of its content.</param>
    public string GetInlineCompletion(string prefix, string suffix, string? filePath, string? regionHint)
    {
        if (!_settings.EnableAiCompletion) return "";

        CancellationTokenSource cts;
        lock (_completionLock)
        {
            // Cancel only — disposing the previous source here would race its own still-
            // running SendAsync, whose token registration then throws ObjectDisposedException
            // instead of cancelling cleanly. Each call disposes its OWN source below, once
            // its request has actually finished unwinding.
            _completionCts?.Cancel();
            cts = _completionCts = new CancellationTokenSource();
        }

        try
        {
            return _chat.CompleteAsync(prefix, suffix, filePath, regionHint, cts.Token).GetAwaiter().GetResult();
        }
        catch
        {
            return ""; // cancelled by a newer keystroke — not an error worth surfacing
        }
        finally
        {
            lock (_completionLock)
            {
                if (ReferenceEquals(_completionCts, cts)) _completionCts = null;
            }
            cts.Dispose();
        }
    }

    // ---- Find in Files (see Web/search.js, Host/WorkspaceSearchService.cs) --------------

    /// <summary>The folder a project-wide search/reference lookup covers for the given open
    /// file — App_Data when the path has one, otherwise the containing folder.</summary>
    public string GetWorkspaceRoot(string anchorPath) => WorkspaceSearchService.ResolveRoot(anchorPath);

    /// <summary>One search over the whole project folder. Synchronous for the same reason
    /// AskAI is: WebView2 runs every host object call on its own background thread, so the
    /// walk never touches the WinForms UI thread, and the page sees a Promise either way.</summary>
    public string SearchWorkspace(
        string root, string query, bool useRegex, bool caseSensitive, bool wholeWord,
        string includeGlobs, int maxResults) =>
        WorkspaceSearchService.Search(root, query, useRegex, caseSensitive, wholeWord, includeGlobs, maxResults);

    /// <summary>Replace-all across the listed files. <paramref name="pathsJson"/> is a JSON
    /// string array — WebView2's IDispatch marshaling has no reliable path for a JS array of
    /// strings, so both directions of this feature speak JSON.</summary>
    public string ReplaceInWorkspace(
        string pathsJson, string query, string replacement,
        bool useRegex, bool caseSensitive, bool wholeWord)
    {
        string[] paths;
        try { paths = JsonSerializer.Deserialize<string[]>(pathsJson) ?? Array.Empty<string>(); }
        catch (Exception ex) { return JsonSerializer.Serialize(new { error = ex.Message }); }
        return WorkspaceSearchService.Replace(paths, query, replacement, useRegex, caseSensitive, wholeWord);
    }

    // ---- Running SQL from a <command> (see Web/sqlrun.js, Host/SqlRunnerService.cs) -----

    /// <summary>What the page needs before it can run a script: the parameters to ask for,
    /// the write statements found in it, and whether settings currently allow those.</summary>
    public string InspectSql(string sql) => _sqlRunner.Inspect(sql);

    /// <summary>Hands the script to the server and returns a run id at once. Deliberately
    /// NOT a blocking call that returns the rows: a host object call occupies the thread
    /// that drives the page, so anything slow freezes the editor and locks out every later
    /// call — including the one that would cancel it. See SqlRunnerService.Start.</summary>
    public string StartSql(string sql, string parametersJson, bool rollbackOnly) =>
        _sqlRunner.Start(sql, parametersJson, rollbackOnly);

    /// <summary>Returns immediately: either "still running" or the finished result.</summary>
    public string PollSql(string runId) => _sqlRunner.Poll(runId);

    /// <summary>The panel's "Dừng" button.</summary>
    public void CancelSql() => _sqlRunner.Cancel();
}
