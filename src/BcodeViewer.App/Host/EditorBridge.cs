using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
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
    private readonly CaptionTranslator _translator;
    private readonly ViewerSettings _settings;
    private readonly SqlSchemaService _sqlSchema;
    private readonly SqlRunnerService _sqlRunner;
    private readonly Func<string, string?> _chooseSaveAsPath;
    private readonly AsyncHostCall _async;
    private readonly Action<string> _openHintDraft;

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
    public EditorBridge(ViewerSettings settings, Func<string, string?> chooseSaveAsPath, Action<string> postJson, Action<string> openHintDraft)
    {
        _settings = settings;
        _chat = new ClaudeChatService(settings);
        _translator = new CaptionTranslator(settings, _chat);
        _chat.Diagnostic += status => AiCompletionReported?.Invoke(status);
        _sqlSchema = new SqlSchemaService(settings);
        _sqlRunner = new SqlRunnerService(settings);
        // Personal library only, which is a local file. The team library is a UNC share in
        // every real deployment, and this constructor runs on the UI thread inside
        // MainForm_Load — reading the share here meant a slow or absent one froze the
        // window before it had finished appearing. It is fetched in the background instead
        // and swapped in when it arrives.
        _openHintDraft = openHintDraft;
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
        _fcodeXml = null; // đọc lại bộ snippet XML của FCode ở lần hỏi kế tiếp
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

    // ---- Web shell: menu / toolbar / breadcrumb / recent-files tree (Web/shell.js) ----------
    // The page draws the chrome; everything that needs the WinForms side (themes, sidebars,
    // Actions, the tree's own model in MainForm) goes through ShellCommand → MainForm.

    /// <summary>Raised for every menu/toolbar/tree action the page sends: (command, argument).</summary>
    public event Action<string, string>? ShellCommandRequested;
    public void ShellCommand(string command, string arg) => ShellCommandRequested?.Invoke(command ?? "", arg ?? "");

    /// <summary>Theme list + toolbar visibility for the page's Theme / ⚙ Toolbar menus — read when a
    /// menu opens, so it always reflects the current state.</summary>
    public string GetShellState() => JsonSerializer.Serialize(new
    {
        themes = new
        {
            builtIn = ThemeCatalog.BuiltIn.Select(t => new { id = t.Id, name = t.Name, isDark = t.IsDark }),
            custom = ThemeCatalog.Custom.Select(t => new { id = t.Id, name = t.Name, isDark = t.IsDark }),
            current = ThemeManager.Current.Id,
            followSystem = ThemeManager.FollowSystem,
        },
        hiddenToolbar = _settings.HiddenToolbarItems,
    });

    /// <summary>⚙ Toolbar: which toolbar buttons are hidden (by label), saved with the settings.</summary>
    public void SetHiddenToolbar(string json)
    {
        try { _settings.HiddenToolbarItems = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); _settings.Save(); }
        catch { /* malformed — keep the previous list */ }
    }

    /// <summary>Help › Giới thiệu, read from the assembly attributes (same source as the .exe's Details tab).</summary>
    public string GetAboutInfo()
    {
        var a = System.Reflection.Assembly.GetExecutingAssembly();
        string Meta<T>(Func<T, string> pick) where T : Attribute =>
            System.Reflection.CustomAttributeExtensions.GetCustomAttribute<T>(a) is { } x ? pick(x) : "";
        var version = Meta<System.Reflection.AssemblyInformationalVersionAttribute>(x => x.InformationalVersion);
        var plus = version.IndexOf('+');
        return JsonSerializer.Serialize(new
        {
            product = Meta<System.Reflection.AssemblyProductAttribute>(x => x.Product),
            version = plus >= 0 ? version[..plus] : version,
            build = plus >= 0 ? version[(plus + 1)..] : "",
            authors = Meta<System.Reflection.AssemblyCompanyAttribute>(x => x.Company),
            description = Meta<System.Reflection.AssemblyDescriptionAttribute>(x => x.Description),
            copyright = Meta<System.Reflection.AssemblyCopyrightAttribute>(x => x.Copyright),
        });
    }

    // ---- Quick Open (Ctrl+P, Web/quickopen.js) ---------------------------------------------
    private static readonly string[] QuickOpenExtensions = { ".xml", ".f", ".ent", ".sql", ".txt", ".js", ".css", ".aspx", ".config", ".json" };
    private readonly ConcurrentDictionary<string, (DateTime At, string Json)> _quickOpenCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every source-like file under <paramref name="root"/> (App_Data), as paths relative to it —
    /// JSON array. Cached ~2 minutes per root: walking a UNC share is the slow part, and Ctrl+P is pressed
    /// over and over in one session. <paramref name="refresh"/> = bypass the cache.</summary>
    public void BeginListFiles(string requestId, string root, bool refresh) =>
        _async.Begin(requestId, () =>
        {
            if (!refresh && _quickOpenCache.TryGetValue(root, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(2))
                return hit.Json;
            var list = new List<string>();
            try
            {
                var prefix = root.TrimEnd('\\') + "\\";
                foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    if (!QuickOpenExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                    list.Add(f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? f[prefix.Length..] : f);
                    if (list.Count >= 50000) break; // a whole share mapped by mistake — don't build a list nobody can scroll
                }
            }
            catch { /* share unreachable — empty list */ }
            var json = JsonSerializer.Serialize(list);
            _quickOpenCache[root] = (DateTime.UtcNow, json);
            return json;
        });

    /// <summary>Kết quả hộp thoại theme (Web/ui.js) mà MainForm mở thay MessageBox — "true"/"false"
    /// cho xác nhận, "" cho thông báo. Xem MainForm.Msg.</summary>
    public event Action<string, string>? UiDialogResolved;
    public void ResolveUiDialog(string id, string result) => UiDialogResolved?.Invoke(id, result ?? "");

    /// <summary>Status line for the Settings dialog's SQL "Test" button.</summary>
    public string DescribeSqlStatus() => _sqlSchema.DescribeStatus();

    // ---- Settings dialog (Web/settings.js) -------------------------------------------------
    // The dialog is drawn by the page so it follows the same theme as everything else there;
    // only the native folder/file pickers still come from WinForms (MainForm sets these).

    public Func<string, string?>? ChooseFolder { get; set; }
    public Func<string, string?>? ChooseFile { get; set; }

    /// <summary>Current settings for the page's Settings dialog — read-only snapshot.</summary>
    public string GetSettings() => JsonSerializer.Serialize(new
    {
        anthropicApiKey = _settings.AnthropicApiKey,
        geminiApiKey = _settings.GeminiApiKey,
        model = _settings.Model,
        enableAiCompletion = _settings.EnableAiCompletion,
        completionModel = _settings.CompletionModel,
        completionEngine = _settings.CompletionEngine,
        translateEngine = _settings.TranslateEngine,
        sharedTemplatePath = _settings.SharedTemplatePath,
        enableSqlCompletion = _settings.EnableSqlCompletion,
        enableSqlWrites = _settings.EnableSqlWrites,
        fcodeConfigXmlPath = _settings.FcodeConfigXmlPath,
        fcodeSqlPassword = _settings.FcodeSqlPassword,
        sqlRegionTags = _settings.SqlRegionTags,
        editorFontFamily = _settings.EditorFontFamily,
    });

    /// <summary>Saves what the page's Settings dialog sends back, then reloads what depends on
    /// it — the same follow-up SettingsForm's OK used to trigger from MainForm.</summary>
    public void BeginSaveSettings(string requestId, string json) =>
        _async.Begin(requestId, () =>
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

            _settings.AnthropicApiKey = S("anthropicApiKey").Trim();
            _settings.GeminiApiKey = S("geminiApiKey").Trim();
            _settings.Model = string.IsNullOrWhiteSpace(S("model")) ? "claude-sonnet-5" : S("model").Trim();
            _settings.EnableAiCompletion = B("enableAiCompletion");
            _settings.CompletionModel = string.IsNullOrWhiteSpace(S("completionModel")) ? "claude-haiku-4-5-20251001" : S("completionModel").Trim();
            _settings.CompletionEngine = S("completionEngine") == "gemini" ? "gemini" : "claude";
            _settings.TranslateEngine = S("translateEngine") is "gemini" or "claude" ? S("translateEngine") : "google";
            _settings.SharedTemplatePath = S("sharedTemplatePath").Trim();
            _settings.EnableSqlCompletion = B("enableSqlCompletion");
            _settings.EnableSqlWrites = B("enableSqlWrites");
            _settings.FcodeConfigXmlPath = S("fcodeConfigXmlPath").Trim();
            _settings.FcodeSqlPassword = S("fcodeSqlPassword");
            _settings.SqlRegionTags = S("sqlRegionTags").Trim();
            _settings.EditorFontFamily = S("editorFontFamily").Trim();
            _settings.Save();

            ReloadSnippets();
            InvalidateSqlSchema();
            return "";
        });

    // ---- Hint Code + New from Template dialogs (Web/templates.js) --------------------------
    // Same idea as Settings: drawn by the page so they follow the active theme. The store the
    // Hint Code dialog edits is loaded fresh each time it opens (personal + shared folder) and
    // kept here until the next open; saves write only the personal file (HintSnippetStore.Save).

    private HintSnippetStore? _hintEditStore;
    private List<FileTemplate> _fileTemplates = new();

    /// <summary>Save dialog for "Export..." (Hint Code) and for the new file of New from Template:
    /// (suggested file name, initial folder, filter) → chosen path or null. Set by MainForm.</summary>
    public Func<string, string?, string, string?>? ChooseSavePath { get; set; }

    /// <summary>Project name MainForm currently groups files under — the ${Project} placeholder.</summary>
    public Func<string>? CurrentProjectName { get; set; }

    private static readonly JsonSerializerOptions CamelJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static object SnippetDto(HintSnippet s) => new
    {
        s.Id, s.Category, s.Type, s.Tags, s.Description, s.Code, s.Prefix, s.PathScope,
        s.ShowInIntelliSense, s.IsShared, s.SourceLabel, s.CreatedBy, s.ModifiedBy,
        CreatedDate = s.CreatedDate.ToString("dd/MM/yyyy HH:mm"),
        ModifiedDate = s.ModifiedDate.ToString("dd/MM/yyyy HH:mm"),
        ModifiedSort = s.ModifiedDate.ToString("o"),
    };

    private string HintListJson() =>
        JsonSerializer.Serialize((_hintEditStore?.All ?? Enumerable.Empty<HintSnippet>()).Select(SnippetDto), CamelJson);

    /// <summary>Loads (or reloads, for "Refresh") the library the Hint Code dialog shows.</summary>
    public void BeginLoadHintSnippets(string requestId) =>
        _async.Begin(requestId, () =>
        {
            _hintEditStore = HintSnippetStore.Load(_settings.SharedTemplatePath);
            return HintListJson();
        });

    /// <summary>Saves one snippet from the dialog. An empty id, or a shared snippet's id, creates a
    /// new personal entry (a team snippet is never written back — saving forks it). Returns
    /// {id, list} so the page can reselect what it just saved.</summary>
    public void BeginSaveHintSnippet(string requestId, string json) =>
        _async.Begin(requestId, () =>
        {
            var store = _hintEditStore ??= HintSnippetStore.Load(_settings.SharedTemplatePath);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

            var id = S("id");
            var current = string.IsNullOrEmpty(id) ? null : store.Snippets.FirstOrDefault(x => x.Id == id);
            if (current is null)
            {
                current = new HintSnippet();
                store.Snippets.Add(current);
            }
            else
            {
                current.ModifiedBy = Environment.UserName;
                current.ModifiedDate = DateTime.Now;
            }

            current.Category = string.IsNullOrEmpty(S("category")) ? "JS" : S("category");
            current.Type = S("type");
            current.Tags = S("tags");
            current.Description = S("description");
            current.Prefix = S("prefix").Trim();
            current.PathScope = S("pathScope").Trim();
            current.ShowInIntelliSense = r.TryGetProperty("showInIntelliSense", out var sis) && sis.ValueKind == JsonValueKind.True;
            current.Code = S("code");

            store.Save();
            ReloadSnippets();
            return JsonSerializer.Serialize(new { id = current.Id, list = JsonDocument.Parse(HintListJson()).RootElement }, CamelJson);
        });

    public void BeginDeleteHintSnippet(string requestId, string id) =>
        _async.Begin(requestId, () =>
        {
            var store = _hintEditStore ??= HintSnippetStore.Load(_settings.SharedTemplatePath);
            store.Snippets.RemoveAll(s => s.Id == id);
            store.Save();
            ReloadSnippets();
            return HintListJson();
        });

    /// <summary>"Export..." — the personal library as a VSCode .code-snippets file. Returns the
    /// message to show ("" when the user cancelled the Save dialog).</summary>
    public void BeginExportHintSnippets(string requestId) =>
        _async.Begin(requestId, () =>
        {
            var store = _hintEditStore ??= HintSnippetStore.Load(_settings.SharedTemplatePath);
            var exportable = store.Snippets.Where(s => !string.IsNullOrWhiteSpace(s.Code)).ToList();
            if (exportable.Count == 0) return "Chưa có snippet riêng nào để export.";
            var path = ChooseSavePath?.Invoke(
                $"bcode-{Environment.UserName}.code-snippets",
                Directory.Exists(_settings.SharedTemplatePath) ? _settings.SharedTemplatePath : null,
                "VSCode snippets (*.code-snippets)|*.code-snippets|JSON (*.json)|*.json");
            if (string.IsNullOrEmpty(path)) return "";
            HintSnippetStore.ExportVsCodeSnippets(path, exportable);
            return $"Đã ghi {exportable.Count} snippet vào:\n{path}";
        });

    /// <summary>Templates for New from Template: {personalFolder, sharedFolder, templates:[{name,path,isShared}]}.</summary>
    public void BeginListFileTemplates(string requestId) =>
        _async.Begin(requestId, () =>
        {
            _fileTemplates = FileTemplateStore.Load(_settings.SharedTemplatePath);
            return JsonSerializer.Serialize(new
            {
                personalFolder = FileTemplateStore.PersonalFolder,
                sharedFolder = FileTemplateStore.SharedFolder(_settings.SharedTemplatePath) ?? "",
                templates = _fileTemplates.Select(t => new { name = t.Name, path = t.Path, isShared = t.IsShared }),
            }, CamelJson);
        });

    /// <summary>Raw template text for the preview (placeholders left unrendered on purpose).</summary>
    public void BeginReadFileTemplate(string requestId, string path) =>
        _async.Begin(requestId, () => _fileTemplates.FirstOrDefault(t => t.Path == path)?.ReadContent() ?? "");

    /// <summary>Asks where to put the new file, writes the rendered template there and returns its
    /// path ("" when cancelled). <paramref name="nearPath"/> = the open file, for the initial folder.</summary>
    public void BeginCreateFromTemplate(string requestId, string path, string nearPath) =>
        _async.Begin(requestId, () =>
        {
            var template = _fileTemplates.FirstOrDefault(t => t.Path == path);
            if (template is null) return "";
            var target = ChooseSavePath?.Invoke(
                template.SuggestedFileName,
                string.IsNullOrEmpty(nearPath) ? null : Path.GetDirectoryName(nearPath),
                "Tất cả file (*.*)|*.*");
            if (string.IsNullOrEmpty(target)) return "";
            File.WriteAllText(target, template.Render(target, CurrentProjectName?.Invoke() ?? ""));
            return target;
        });

    /// <summary>Native folder picker for the Settings dialog; "" when cancelled.</summary>
    public void BeginChooseFolder(string requestId, string initial) =>
        _async.Begin(requestId, () => ChooseFolder?.Invoke(initial) ?? "");

    /// <summary>Native file picker (Config.xml of FCode) for the Settings dialog; "" when cancelled.</summary>
    public void BeginChooseFile(string requestId, string initial) =>
        _async.Begin(requestId, () => ChooseFile?.Invoke(initial) ?? "");

    /// <summary>Raised as the caret moves, so MainForm's status bar can show "Ln X, Col Y"
    /// the way FCodeViewer's own does.</summary>
    public event Action<int, int>? CursorChanged;

    public void NotifyCursorChanged(int line, int column) => CursorChanged?.Invoke(line, column);
    /// <summary>Chuyển tiếp ClaudeChatService.Diagnostic ra ngoài để MainForm hiện lên status bar.</summary>
    public event Action<string>? AiCompletionReported;
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
        _async.Begin(requestId, () => ReadFileDetectEncoding(path));

    public void BeginWriteFile(string requestId, string path, string content) =>
        _async.Begin(requestId, () =>
        {
            lock (SaveGateFor(path)) { WriteFileKeepEncoding(path, content); }
            return "";
        });

    /// <summary>
    /// Encoding THẬT của từng file đang mở, nhớ lại lúc Đọc để dùng lại y hệt lúc Ghi.
    ///
    /// LÝ DO CÓ CÁI NÀY — bug thật Bee gặp: <c>File.ReadAllText(path)</c> (không truyền
    /// Encoding) tự dò BOM nên đọc đúng bất kể file là UTF-8-BOM/UTF-16/không BOM gì cả, NHƯNG
    /// <c>File.WriteAllText(path, content)</c> (không truyền Encoding) LUÔN ghi ra UTF-8
    /// KHÔNG BOM — bất kể file gốc từng là encoding gì. Nhiều controller .f của FCode tự khai
    /// <c>&lt;?xml version="1.0" encoding="utf-16"?&gt;</c> ngay dòng đầu (thấy rõ nhất ở
    /// chính template .tdf mặc định mà <c>EnsureProfilerTemplateExists</c> bên Bcode.App tự
    /// sinh ra) nhưng THỰC TẾ được lưu trên đĩa ở dạng byte UTF-16 — dòng khai
    /// <c>encoding="utf-16"</c> đó chỉ là VĂN BẢN nằm trong nội dung, code Save cũ không hề
    /// đọc nó để quyết định ghi ra byte kiểu gì.
    ///
    /// Hậu quả: mở 1 file .f đang là UTF-16 bằng BcodeViewer rồi Ctrl+S, byte thật trên đĩa
    /// lặng lẽ đổi thành UTF-8, còn dòng "encoding=\"utf-16\"" ở đầu file thì vẫn y nguyên —
    /// giờ lời khai một đằng, byte thật một nẻo. BcodeViewer/Monaco/Notepad vẫn mở lại bình
    /// thường vì chúng tự dò encoding thật của byte thay vì tin lời khai trong &lt;?xml?&gt;,
    /// nên trong BcodeViewer trông như KHÔNG có gì sai — còn <c>XmlReader</c>/
    /// <c>XmlDocument</c> mà FastBusiness Online dùng để nạp lại controller đó lại tin đúng
    /// lời khai, cố giải mã byte UTF-8 kia như thể là UTF-16 — ra toàn ký tự vô nghĩa ngay từ
    /// ký tự đầu tiên, đúng y <c>System.Xml.XmlException: Data at the root level is invalid.
    /// Line 1, position 1.</c> mà Bee gặp khi mở lại màn "Thêm hóa đơn" sau khi lưu file bằng
    /// BcodeViewer.
    ///
    /// Sửa: dò và nhớ lại encoding THẬT của file ngay lúc Đọc (BOM UTF-8/UTF-16 LE/UTF-16
    /// BE/UTF-32, không có BOM thì coi là UTF-8 không BOM — CHỦ Ý không dùng thẳng
    /// <see cref="Encoding.UTF8"/> mặc định của .NET cho trường hợp này, vì đối tượng đó lại
    /// tự thêm BOM khi đem ra ghi), rồi dùng lại ĐÚNG encoding đó lúc Ghi — file vốn là gì thì
    /// lưu lại vẫn đúng là cái đó, không còn bị âm thầm "quy hết về UTF-8" nữa.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Encoding> FileEncodings =
        new(StringComparer.OrdinalIgnoreCase);

    private static string EncodingKeyFor(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; } // đường dẫn dị dạng — Read/Write bên dưới sẽ tự báo lỗi đúng chỗ
    }

    private static string ReadFileDetectEncoding(string path)
    {
        var encoding = DetectFileEncoding(path);
        FileEncodings[EncodingKeyFor(path)] = encoding;
        return File.ReadAllText(path, encoding);
    }

    private static Encoding DetectFileEncoding(string path)
    {
        Span<byte> bom = stackalloc byte[4];
        int read;
        using (var fs = File.OpenRead(path)) read = fs.Read(bom);
        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return new UTF8Encoding(true);
        if (read >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0x00 && bom[3] == 0x00) return Encoding.UTF32;
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;          // UTF-16 LE
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode; // UTF-16 BE
        return new UTF8Encoding(false); // không có BOM: coi là UTF-8 không BOM, giữ nguyên hiện trạng
    }

    /// <summary>Encoding đã nhớ cho file này (từ lần Đọc gần nhất), hoặc UTF-8 không BOM cho
    /// file hoàn toàn mới (Save As tới đường dẫn chưa từng tồn tại).</summary>
    private static Encoding EncodingFor(string path) =>
        FileEncodings.TryGetValue(EncodingKeyFor(path), out var enc) ? enc : new UTF8Encoding(false);

    private static void WriteFileKeepEncoding(string path, string content) =>
        File.WriteAllText(path, content, EncodingFor(path));

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

    /// <summary>Same save as <see cref="BeginSaveWithHistory"/>, run synchronously — for
    /// MainForm writing unsaved tabs while the window closes. Internal so it is not exposed
    /// to the page through the host object. Throws on a failed write.</summary>
    internal static void SaveWithHistory(string path, string content)
    {
        lock (SaveGateFor(path)) { SaveWithHistoryCore(path, content); }
    }

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
                // Dò lại encoding thật ngay trước khi ghi đè (không chỉ dựa vào lần Đọc lúc mở
                // file, có thể đã lâu hoặc chưa từng xảy ra trong phiên này) — xem ghi chú đầy
                // đủ ở ReadFileDetectEncoding/WriteFileKeepEncoding phía trên về vì sao việc
                // này quan trọng: ghi sai encoding so với file gốc là nguyên nhân trực tiếp
                // gây lỗi "System.Xml.XmlException: Data at the root level is invalid" khi
                // FastBusiness Online nạp lại 1 file .f vốn là UTF-16 mà bị lưu đè thành UTF-8.
                var existing = ReadFileDetectEncoding(path);
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

        WriteFileKeepEncoding(path, content);
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

    /// <summary>"Clone file": chép từng file trong <paramref name="sourcesJson"/> sang cùng thư mục với tên gốc mới
    /// <paramref name="newBase"/> (giữ đuôi: SVTran.f → SVTran2.f) — cùng quy ước "Clone files" của File Lookup bên Bcode.App.
    /// Không ghi đè: có file đích nào đã tồn tại thì không chép gì cả. Trả JSON {created:[...]} hoặc {error, exists?}.</summary>
    public void BeginCloneFiles(string requestId, string sourcesJson, string newBase) =>
        _async.Begin(requestId, () =>
        {
            try
            {
                newBase = (newBase ?? "").Trim();
                if (newBase.Length == 0 || newBase.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    return JsonSerializer.Serialize(new { error = "Tên có ký tự không hợp lệ." });
                var sources = JsonSerializer.Deserialize<string[]>(sourcesJson) ?? Array.Empty<string>();
                var targets = sources.Select(s => Path.Combine(Path.GetDirectoryName(s)!, newBase + Path.GetExtension(s))).ToArray();
                var exists = targets.Where(File.Exists).Select(Path.GetFileName).ToArray();
                if (exists.Length > 0)
                    return JsonSerializer.Serialize(new { error = "Đã có sẵn, không ghi đè:\n" + string.Join("\n", exists), exists });
                for (var i = 0; i < sources.Length; i++) File.Copy(sources[i], targets[i]);
                return JsonSerializer.Serialize(new { created = targets });
            }
            catch (Exception ex)
            {
                return JsonSerializer.Serialize(new { error = ex.Message });
            }
        });



    /// <summary>JS phát hiện bạn vừa Tab-accept 1 gợi ý AI (xem completion.js's
    /// checkAiGhostAccepted) và đề nghị lưu lại thành Hint Code dùng lại không cần AI. Mở
    /// đúng dialog như nút "Hint", chỉ khác là điền sẵn code.</summary>
    public void BeginSaveAiSuggestionAsHint(string requestId, string code) =>
        _async.Begin(requestId, () => { _openHintDraft(code); return ""; });
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

    /// <summary>Streamed: each fragment reaches the panel as it is generated (see
    /// AsyncHostCall's emit channel and Web/chat.js), while the final result still carries
    /// the whole reply — so nothing depends on every chunk having been delivered.</summary>
    public void BeginAskAI(string requestId, string prompt, string? fileContext, string? filePath) =>
        _async.Begin(requestId, null,
            (token, emit) => _chat.AskAsync(prompt, fileContext, filePath, emit, token));

    /// <summary>"Dịch caption (v → e)": <paramref name="payloadJson"/> = [{vi, field}]; trả mảng JSON các chuỗi dịch hoặc chuỗi "Lỗi: ...".
    /// Engine (Google không cần key / Gemini / Claude) theo Settings — xem <see cref="CaptionTranslator"/>.</summary>
    public void BeginTranslateCaptions(string requestId, string payloadJson, string lang) =>
        _async.Begin(requestId, null, token => _translator.TranslateAsync(payloadJson, lang, token));

    /// <summary>"Get Hash Source": JSON [{fullpath, subpath, name, ext, size, date, hash}] của file đang mở và các file cùng tên gốc
    /// (SVTran.f/.xml, Main\SVTran.aspx...) dưới App_Data\Controllers và Main của site — để so hash/ngày sửa bản cũ với mới.
    /// hash = SHA-256 hoa; date = giờ ghi file lần cuối (giờ máy).</summary>
    public void BeginGetHashSource(string requestId, string path) =>
        _async.Begin(requestId, null, token => Task.Run(() =>
        {
            var parts = path.Split('\\', '/');
            var cut = Array.FindLastIndex(parts, p => p.Equals("App_Data", StringComparison.OrdinalIgnoreCase)
                                                      || p.Equals("Main", StringComparison.OrdinalIgnoreCase));
            var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            string root = "";
            if (cut > 0)
            {
                root = string.Join("\\", parts.Take(cut));
                if (path.StartsWith(@"\\")) root = @"\\" + root.TrimStart('\\');
                var baseName = Path.GetFileNameWithoutExtension(path);
                foreach (var dir in new[] { Path.Combine(root, "App_Data", "Controllers"), Path.Combine(root, "Main") })
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.EnumerateFiles(dir, baseName + ".*", SearchOption.AllDirectories))
                    {
                        token.ThrowIfCancellationRequested();
                        files.Add(f);
                    }
                }
            }

            var rows = new List<object>();
            foreach (var f in files)
            {
                token.ThrowIfCancellationRequested();
                var info = new FileInfo(f);
                if (!info.Exists) continue;
                using var fs = info.OpenRead();
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
                var dir = Path.GetDirectoryName(f) ?? "";
                var sub = root.Length > 0 && dir.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? dir[root.Length..].TrimStart('\\')
                    : "";
                rows.Add(new
                {
                    fullpath = f,
                    subpath = sub,
                    name = info.Name,
                    ext = info.Extension.ToLowerInvariant(),
                    size = info.Length,
                    date = info.LastWriteTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"),
                    hash,
                });
            }
            return JsonSerializer.Serialize(rows, new JsonSerializerOptions
            {
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        }, token));

    /// <summary>F5: mở menu của file này bằng trình duyệt mặc định của máy (MenuLauncher tra wcommand ra URL), không
    /// đi qua Bcode.App/FSG FBO nữa. Trả về câu thông báo ngắn cho trang hiện ra.</summary>
    public void BeginRunMenu(string requestId, string path) =>
        _async.Begin(requestId, null, async token =>
        {
            var project = WorkspaceConnection.ResolveProjectName(path) ?? "";
            try { return await MenuLauncher.OpenInBrowserAsync(path, project, token); }
            catch (Exception ex) { return "Đã lưu. Không mở được trình duyệt: " + ex.Message; }
        });

    /// <summary>Chạy một hàm async tới khi xong từ code đồng bộ mà KHÔNG deadlock khi đang ở
    /// UI thread — xem ghi chú ở AskAI.</summary>
    private static T RunSync<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

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
        }).Concat(FcodeXml.Snippets.Select(s => new
        {
            prefix = s.Prefix,
            code = s.Code,
            description = s.Description,
            type = "FCode",
            category = s.Category,
            pathScope = "",
            shared = true, // xếp sau snippet cá nhân khi trùng keyword
            source = "FCode",
        })));

    /// <summary>Bộ snippet XML của FCode, đọc 1 lần rồi giữ trong bộ nhớ (file local, nhỏ).</summary>
    private volatile FcodeXmlSnippets.Result? _fcodeXml;
    private FcodeXmlSnippets.Result FcodeXml => _fcodeXml ??=
        FcodeXmlSnippets.Load(FcodeXmlSnippets.ResolveFolder(_settings.FcodeSnippetFolder));

    /// <summary>Gợi ý theo đối tượng ("f." → getItem, getItemValue…) và theo giá trị thuộc tính
    /// (style=" → Mask, Numeric…) — page lọc theo vùng ở caret, xem completion.js provideHints.</summary>
    public string GetFcodeHints() => JsonSerializer.Serialize(FcodeXml.Hints.Select(h => new
    {
        category = h.Category,
        objects = h.Objects,
        @char = h.Char,
        items = h.Items.Select(i => new { keyword = i.Keyword, code = i.Code, description = i.Description }),
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
                dirtyMarker = Hex(t.DirtyMarker),
            },
            editor = new
            {
                lineNumber = t.LineNumber is { } ln ? Hex(ln) : null,
                lineHighlight = t.LineHighlight is { } lh ? Hex(lh) : null,
                selection = t.EditorSelection is { } es ? Hex(es) : null,
            },
            rules = t.TokenRules.Select(r => new { token = r.Token, foreground = r.Foreground, fontStyle = r.FontStyle }),
            monacoColors = t.MonacoColors,
            editorFont = string.IsNullOrWhiteSpace(_settings.EditorFontFamily) ? null : _settings.EditorFontFamily,
        });
    }

    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>Editor-side feature flags, read once at page load — keeps the "is AI
    /// completion on?" decision in settings rather than duplicated in JS.</summary>
    public string GetEditorConfig() => JsonSerializer.Serialize(new
    {
        aiCompletion = _settings.EnableAiCompletion && !string.IsNullOrWhiteSpace(
            string.Equals(_settings.CompletionEngine, "gemini", StringComparison.OrdinalIgnoreCase)
                ? _settings.GeminiApiKey
                : _settings.AnthropicApiKey),
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
    /// <param name="projectFacts">What the editor already knows about the open document —
    /// its fields, the entities it resolves, the SQL columns of the tables it uses. Built by
    /// Web/completion.js's buildProjectFacts and travelling in the cached part of the prompt,
    /// which is what makes it affordable to send at all.</param>
    public void BeginInlineCompletion(
        string requestId, string prefix, string suffix, string? filePath, string? regionHint, string? projectFacts)
    {
        if (!_settings.EnableAiCompletion)
        {
            _async.Begin(requestId, () => "");
            return;
        }
        bool useGemini = string.Equals(_settings.CompletionEngine, "gemini", StringComparison.OrdinalIgnoreCase);
        _async.Begin(requestId, CompletionGroup,
            token => useGemini
                ? _chat.CompleteWithGeminiAsync(prefix, suffix, filePath, regionHint, projectFacts, token)
                : _chat.CompleteAsync(prefix, suffix, filePath, regionHint, projectFacts, token));
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