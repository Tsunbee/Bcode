using System.Collections.Concurrent;
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
///
/// EVERY METHOD HERE RUNS ON THE WINFORMS UI THREAD. This is a COM object created on the
/// STA thread, so WebView2's IDispatch calls are marshalled back into that apartment
/// rather than served on a thread of the browser's own — measured, not assumed (a probe
/// method reports the UI thread's own managed id and a live WindowsFormsSynchronizationContext).
/// Earlier comments in this file asserted the opposite and the code was written to match,
/// which is what made the editor stall while typing and left the process unable to exit.
/// <see cref="AsyncHostCall"/> has the full account.
///
/// The rule that follows: a method may stay a plain synchronous one ONLY if it answers out
/// of memory. Anything touching the filesystem, the network or a database is a
/// <c>Begin*</c> method that queues the work and answers by web message.
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
    private readonly AsyncHostCall _async;

    /// <summary>Swapped wholesale when the team library finishes loading, never mutated in
    /// place. volatile because the swap happens on a worker while <see cref="GetSnippets"/>
    /// reads it on the UI thread; a reference assignment is atomic, so a reader sees either
    /// the personal-only store or the complete one and never a half-built list.</summary>
    private volatile HintSnippetStore _snippets;

    /// <summary>Supersede-group for ghost text: a fresh keystroke cancels the request the
    /// previous one started, so a fast typist doesn't leave one billed HTTP call per
    /// character in flight. See <see cref="AsyncHostCall.Begin"/>.</summary>
    private const string CompletionGroup = "inlineCompletion";

    /// <param name="postJson">Sends one JSON message to the page; MainForm supplies it and
    /// owns the UI-thread marshalling CoreWebView2 requires.</param>
    public EditorBridge(ViewerSettings settings, Func<string, string?> chooseSaveAsPath, Action<string> postJson)
    {
        _settings = settings;
        _chat = new ClaudeChatService(settings);
        _sqlSchema = new SqlSchemaService(settings);
        _sqlRunner = new SqlRunnerService(settings);
        // Personal library only, which is a local file. The team library is a UNC share in
        // every real deployment, and this constructor runs on the UI thread inside
        // MainForm_Load — reading the share here meant a slow or absent one froze the
        // window before it had finished appearing. It is fetched in the background instead
        // and swapped in when it arrives.
        _snippets = HintSnippetStore.LoadPersonal();
        _chooseSaveAsPath = chooseSaveAsPath;
        _async = new AsyncHostCall(postJson);
        LoadSharedSnippetsInBackground();
    }

    /// <summary>Raised once a background load has changed what <see cref="GetSnippets"/>
    /// would return, so MainForm can tell the page to re-pull. Raised on a worker thread —
    /// unlike the host object calls above, this one really does need marshalling.</summary>
    public event Action? SnippetsChanged;

    /// <summary>Re-reads the snippet libraries — called after the Hint Code dialog closes
    /// and after Settings changes the shared path, so a snippet just saved is suggestable
    /// without restarting. Returns as soon as the personal library is in place; the team
    /// folder follows on a worker and raises <see cref="SnippetsChanged"/> when it lands.</summary>
    public void ReloadSnippets()
    {
        _snippets = HintSnippetStore.LoadPersonal();
        LoadSharedSnippetsInBackground();
    }

    private void LoadSharedSnippetsInBackground()
    {
        var sharedPath = _settings.SharedTemplatePath;
        if (string.IsNullOrWhiteSpace(sharedPath)) return; // nothing configured — no worker, no event

        _async.Post(() =>
        {
            var shared = HintSnippetStore.LoadSharedOnly(sharedPath);
            if (shared.Count == 0) return; // share down or empty: personal list already stands

            // Rebuild rather than assigning into the live store: a reader mid-Concat over
            // .All would otherwise see the list grow underneath it.
            var current = _snippets;
            _snippets = new HintSnippetStore { Snippets = current.Snippets, Shared = shared };
            SnippetsChanged?.Invoke();
        });
    }

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
    /// initial folder/name; answers "" if the user cancels.
    ///
    /// Queued like everything else, even though the dialog ends up back on the UI thread
    /// anyway (MainForm.ChooseSaveAsPath marshals it). What that avoids is showing a modal
    /// dialog from INSIDE the IDispatch call that page script is waiting on: the dialog runs
    /// its own message loop, which re-enters WebView2 while a host call is still on the
    /// stack. Going through the thread pool means the browser's call has already returned
    /// by the time the picker opens.</summary>
    public void BeginChooseSaveAsPath(string requestId, string suggestedPath) =>
        _async.Begin(requestId, () => _chooseSaveAsPath(suggestedPath) ?? "");

    /// <summary>Raised whenever the page (a single-document editor — see editor.js) opens a
    /// new file in place of whatever was shown, so MainForm can add it to the left "recent
    /// files by project" tree and update the window title. Raised on the UI thread, like
    /// every other host object call (see this class's own summary), so handlers may touch
    /// controls directly.</summary>
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

    public void BeginReadFile(string requestId, string path) =>
        _async.Begin(requestId, () => File.ReadAllText(path));

    public void BeginWriteFile(string requestId, string path, string content) =>
        _async.Begin(requestId, () =>
        {
            lock (SaveGateFor(path)) { File.WriteAllText(path, content); }
            return "";
        });

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
    ///
    /// Queued rather than run inline because this is five round trips to a UNC share back
    /// to back — read the old content, read the newest snapshot to compare, write the new
    /// snapshot, prune the folder, write the file — on every single Ctrl+S.
    ///
    /// Queueing is what makes the lock below necessary. While saves ran inline they were
    /// serialised by the UI thread whether anyone thought about it or not; running them on
    /// the thread pool means two quick Ctrl+S presses, or a Save As landing on a path a
    /// slow save is still writing, are now genuinely concurrent — two File.WriteAllText
    /// calls on one file, which is a torn write or an IOException rather than a save.
    /// </summary>
    public void BeginSaveWithHistory(string requestId, string path, string content) =>
        _async.Begin(requestId, () =>
        {
            lock (SaveGateFor(path))
            {
                return SaveWithHistoryCore(path, content);
            }
        });

    /// <summary>One lock object per file, shared across every EditorBridge in the process.
    /// Keyed on the full path case-insensitively, as Windows compares them.</summary>
    private static readonly ConcurrentDictionary<string, object> SaveGates =
        new(StringComparer.OrdinalIgnoreCase);

    private static object SaveGateFor(string path)
    {
        string key;
        try { key = Path.GetFullPath(path); }
        catch { key = path; } // malformed path — the write below will report it properly
        return SaveGates.GetOrAdd(key, _ => new object());
    }

    private static string SaveWithHistoryCore(string path, string content)
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
        return "";
    }

    /// <summary>JSON array of {"id","savedAt","length"} for one file, newest first.</summary>
    public void BeginGetFileHistory(string requestId, string path) =>
        _async.Begin(requestId, () => LocalHistoryStore.ListJson(path));

    /// <summary>One historical version's full text, or "" if it has since been pruned.</summary>
    public void BeginGetHistorySnapshot(string requestId, string path, string id) =>
        _async.Begin(requestId, () => LocalHistoryStore.Read(path, id) ?? "");

    /// <summary>Opens the file's history folder in Explorer — the escape hatch for anyone
    /// who wants to grab an old version with something other than this editor.</summary>
    public void OpenHistoryFolder(string path) =>
        _async.Post(() =>
        {
            var folder = LocalHistoryStore.FolderFor(path);
            if (Directory.Exists(folder)) System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        });

    /// <summary>
    /// Existence of several paths in one call, as a JSON array of booleans positionally
    /// matching <paramref name="pathsJson"/>.
    ///
    /// Batched, not one call per path, because of the caller: problems.js re-validates 400ms
    /// after every keystroke and asks about every <c>&lt;!ENTITY ... SYSTEM&gt;</c>
    /// declaration in the document. One at a time, a controller with twenty entities meant
    /// twenty IDispatch round trips and twenty sequential stat calls across the share, every
    /// time the user paused.
    /// </summary>
    public void BeginPathsExist(string requestId, string pathsJson) =>
        _async.Begin(requestId, () =>
        {
            string[] paths;
            try { paths = JsonSerializer.Deserialize<string[]>(pathsJson) ?? Array.Empty<string>(); }
            catch { return "[]"; }

            var found = new bool[paths.Length];
            for (var i = 0; i < paths.Length; i++)
            {
                try { found[i] = File.Exists(paths[i]) || Directory.Exists(paths[i]); }
                catch { found[i] = false; } // unreachable share reads as "not there"
            }
            return JsonSerializer.Serialize(found);
        });

    /// <summary>Backs the "file changed on another machine" watch in editor.js: the page
    /// polls this for the currently open file and compares it against the write time it
    /// captured at open/save, showing a reload banner when they differ. Returned as an ISO
    /// "o"-format string rather than ticks/epoch-ms — a DateTime.Ticks value is well past
    /// JS's 2^53 safe-integer range and WebView2's IDispatch marshaling would round-trip it
    /// as a lossy double, breaking the equality check this is used for; string equality has
    /// no such precision concern. Empty string means "can't compare" (file missing/locked),
    /// which the caller treats as "nothing to report" rather than as a change.</summary>
    public void BeginGetFileWriteTimeUtc(string requestId, string path) =>
        _async.Begin(requestId, () =>
        {
            try
            {
                return File.Exists(path) ? File.GetLastWriteTimeUtc(path).ToString("o") : "";
            }
            catch
            {
                return ""; // locked/unreadable right now — try again on the next poll
            }
        });

    /// <summary>Backs the "Open Folder" context-menu submenu — opens Explorer at a folder
    /// (or, for a file, opens its containing folder with that file selected), same as
    /// FCode's own quick-access folder shortcuts (Images/Options/Lookup/Templates siblings
    /// of Controllers under App_Data).</summary>
    public void OpenFolder(string path) =>
        _async.Post(() =>
        {
            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
        });

    /// <summary>JSON array of {"name","path","isDirectory"} for one folder level — powers
    /// the page's own "open sibling file" affordance if it wants one beyond the native
    /// WinForms tree on the left; the tree itself is populated directly in MainForm.</summary>
    public void BeginListDirectory(string requestId, string path) =>
        _async.Begin(requestId, () =>
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
        });

    /// <summary>
    /// One chat turn against the Anthropic API.
    ///
    /// This used to be a synchronous method that blocked on the task, on the theory that
    /// WebView2 served host calls from a background thread. It does not — it serves them on
    /// the UI thread, with a synchronization context installed — so blocking here deadlocked
    /// the process outright: the continuation after <c>await Http.SendAsync</c> was posted
    /// back to the thread that was sitting in GetResult() waiting for it. The window would
    /// not close and the process would not exit. See <see cref="AsyncHostCall"/>.
    /// </summary>
    public void BeginAskAI(string requestId, string prompt, string? fileContext, string? filePath) =>
        _async.Begin(requestId, null, _ => _chat.AskAsync(prompt, fileContext, filePath));

    // ---- IntelliSense (see Web/completion.js) ------------------------------------------

    /// <summary>
    /// The whole snippet library as JSON, handed to the page ONCE per load (and again after
    /// ReloadSnippets) rather than queried per keystroke. That's the important part: a
    /// completion provider is called on every character typed, and every host object call
    /// is an IDispatch round trip that lands on the UI thread — doing that per keystroke is
    /// exactly how an editor starts feeling laggy. The provider itself runs against this
    /// array in page memory with no host call at all.
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
    public void BeginGetSqlTables(string requestId) =>
        _async.Begin(requestId, null, _ => _sqlSchema.GetTablesJsonAsync());

    /// <summary>Columns of one table, fetched the first time a query references it.</summary>
    public void BeginGetSqlColumns(string requestId, string table) =>
        _async.Begin(requestId, null, _ => _sqlSchema.GetColumnsJsonAsync(table));

    /// <summary>
    /// Ghost text for the caret position. Cancels whatever previous request is still in
    /// flight before starting this one — that is what <see cref="CompletionGroup"/> is:
    /// Monaco asks again on every content change, so without it a fast typist would have one
    /// live HTTP call per character, each billed, each arriving too late to be useful anyway.
    ///
    /// This was the worst of the four methods that used to block on their task. It sits on
    /// the typing path, so the deadlock it caused (see <see cref="AsyncHostCall"/>) landed
    /// mid-edit, with the window frozen and the file unsaved.
    /// </summary>
    /// <param name="regionHint">"js", "sql", "css" or "xml" — which embedded region of the
    /// document the caret is in (see completion.js's regionAt). An FCode controller is one
    /// .xml file containing all of them, so the file extension alone tells the model the
    /// wrong language for most of its content.</param>
    public void BeginInlineCompletion(string requestId, string prefix, string suffix, string? filePath, string? regionHint)
    {
        if (!_settings.EnableAiCompletion)
        {
            // Still answer, so the page's promise settles rather than being left pending.
            _async.Begin(requestId, () => "");
            return;
        }

        _async.Begin(requestId, CompletionGroup,
            token => _chat.CompleteAsync(prefix, suffix, filePath, regionHint, token));
    }

    // ---- Find in Files (see Web/search.js, Host/WorkspaceSearchService.cs) --------------

    /// <summary>The folder a project-wide search/reference lookup covers for the given open
    /// file — App_Data when the path has one, otherwise the containing folder.</summary>
    public string GetWorkspaceRoot(string anchorPath) => WorkspaceSearchService.ResolveRoot(anchorPath);

    /// <summary>One search over the whole project folder. Queued: this is a recursive walk
    /// of an App_Data tree on a share, reading every candidate file — seconds of frozen
    /// window if it ran where the call arrives.</summary>
    public void BeginSearchWorkspace(
        string requestId, string root, string query, bool useRegex, bool caseSensitive,
        bool wholeWord, string includeGlobs, int maxResults) =>
        _async.Begin(requestId, () =>
            WorkspaceSearchService.Search(root, query, useRegex, caseSensitive, wholeWord, includeGlobs, maxResults));

    /// <summary>Replace-all across the listed files. <paramref name="pathsJson"/> is a JSON
    /// string array — WebView2's IDispatch marshaling has no reliable path for a JS array of
    /// strings, so both directions of this feature speak JSON.</summary>
    public void BeginReplaceInWorkspace(
        string requestId, string pathsJson, string query, string replacement,
        bool useRegex, bool caseSensitive, bool wholeWord) =>
        _async.Begin(requestId, () =>
        {
            string[] paths;
            try { paths = JsonSerializer.Deserialize<string[]>(pathsJson) ?? Array.Empty<string>(); }
            catch (Exception ex) { return JsonSerializer.Serialize(new { error = ex.Message }); }
            return WorkspaceSearchService.Replace(paths, query, replacement, useRegex, caseSensitive, wholeWord);
        });

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
