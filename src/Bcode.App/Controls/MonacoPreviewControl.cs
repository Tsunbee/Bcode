using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// File Lookup's right-hand preview, drawn by Monaco in a WebView2 (Web/Shell/filepreview.html)
/// instead of <see cref="ScriptEditorControl"/>'s RichTextBox. Two reasons:
///   1. the RichTextBox repainted RTF colouring and a line-number gutter on every scroll, which
///      is what made scrolling a long controller stutter; Monaco only renders the visible lines;
///   2. the page uses BcodeViewer's own Monaco build, fcode-xml language, theme.js and font
///      (linked in by Bcode.App.csproj) plus the theme BcodeViewer last used
///      (%AppData%\Bcode\viewer-theme.json), so the preview looks exactly like opening the
///      file in BcodeViewer — khi theme đó cùng sáng / tối với Bcode; khác thì theo màu của Bcode
///      (xem PushThemeIfChanged).
/// Read-only. Exposes the subset of ScriptEditorControl's API that FileLookupControl uses,
/// and F12 resolves through the very same code (<see cref="ScriptEditorControl.ResolveF12"/>).
/// </summary>
public partial class MonacoPreviewControl : UserControl
{
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _ready;
    private string? _pendingLoad;     // last load/message JSON, sent once the page says "ready"
    private string _text = "";        // what the page shows, '\n' line endings — F12 offsets index into this
    private string? _entityResolveBasePath;
    private string? _themeKey;        // sáng/tối của Bcode + màu nền/chữ + giờ ghi viewer-theme.json của lần đẩy theme gần nhất

    public string? CurrentPath { get; private set; }

    /// <summary>F12 on a SYSTEM entity / quoted Include path — the file to open.</summary>
    public event Action<string>? EntityNavigationRequested;

    /// <summary>F12 on a VALUE entity — name, value, declaring file.</summary>
    public event Action<string, string, string>? EntityValuePeekRequested;

    /// <summary>F12 khi bôi đen nhiều entity — xem <see cref="ScriptEditorControl.EntityMultiPeekRequested"/>.</summary>
    public event Action<List<EntityPreviewItem>>? EntityMultiPeekRequested;

    private static string ThemeFilePath => Path.Combine(BcodePaths.AppData, "Bcode", "viewer-theme.json");

    public MonacoPreviewControl()
    {
        Dock = DockStyle.Fill;
        if (UiMode.IsNative) { BuildNative(); return; }
        // Nền lúc trang chưa tải xong = nền theme Bcode (mặc định WebView2 là trắng / trang cũ là đen #1e1e1e).
        _web.DefaultBackgroundColor = AppColors.Background;
        Controls.Add(_web);
        _ = InitAsync();
        WatchThemeFile();
        // Bcode đổi theme sáng ↔ tối (hoặc đổi màu) → đổi theme khung xem trước ngay, không cần mở lại tab.
        ThemeManager.Subscribe(this, () =>
        {
            _web.DefaultBackgroundColor = AppColors.Background;
            if (_ready) PushThemeIfChanged();
        });
    }

    /// <summary>BcodeViewer rewrites viewer-theme.json when its theme changes (or on first start);
    /// re-push then, so the preview follows without waiting for the next file click.</summary>
    private void WatchThemeFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(ThemeFilePath)!;
            Directory.CreateDirectory(dir);
            var watcher = new FileSystemWatcher(dir, Path.GetFileName(ThemeFilePath))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            void Changed(object? _, FileSystemEventArgs __)
            {
                if (IsDisposed || !IsHandleCreated) return;
                // Let BcodeViewer finish writing; a half-written file just fails to parse and is retried later.
                try { BeginInvoke(async () => { await Task.Delay(200); if (_ready) PushThemeIfChanged(); }); }
                catch (InvalidOperationException) { /* handle gone */ }
            }
            watcher.Changed += Changed;
            watcher.Created += Changed;
            watcher.Renamed += (s, e) => Changed(s, e);
            Disposed += (_, _) => watcher.Dispose();
        }
        catch { /* no watcher — the theme is still re-checked on every file load */ }
    }

    private async Task InitAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += (_, e) => OnPageMessage(e.TryGetWebMessageAsString());
            // bg / dark: để trang vẽ ngay đúng nền + theme sáng/tối của Bcode trước khi theme thật được đẩy sang (tránh chớp đen ở theme sáng).
            _web.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("filepreview.html")
                + $"?bg={Hex(AppColors.Background).TrimStart('#')}&dark={(AppColors.IsDark ? 1 : 0)}");
        }
        catch (Exception ex)
        {
            Controls.Clear();
            Controls.Add(new Label
            {
                Dock = DockStyle.Fill, Padding = new Padding(12), ForeColor = AppColors.TextMuted,
                Text = "Không khởi tạo được khung xem file (WebView2).\r\n" + ex.Message,
            });
        }
    }

    private void OnPageMessage(string raw)
    {
        string? action;
        int offset = 0, selStart = -1, selEnd = -1;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            action = doc.RootElement.GetProperty("action").GetString();
            if (doc.RootElement.TryGetProperty("offset", out var o)) offset = o.GetInt32();
            if (doc.RootElement.TryGetProperty("selStart", out var ss)) selStart = ss.GetInt32();
            if (doc.RootElement.TryGetProperty("selEnd", out var se)) selEnd = se.GetInt32();
        }
        catch { return; }

        switch (action)
        {
            case "ready":
                _ready = true;
                _themeKey = null;
                PushThemeIfChanged();
                if (_pendingLoad is not null) Post(_pendingLoad);
                break;
            case "f12":
                OnF12(offset, selStart, selEnd);
                break;
            // Ctrl+G không còn qua host: trang dùng hộp "Go to" của BcodeViewer (viewer/goto.js) — xem filepreview.html.
        }
    }

    private void OnF12(int offset, int selStart = -1, int selEnd = -1)
    {
        // Có vùng bôi đen chứa từ 2 entity trở lên → xem trước lần lượt từng entity.
        if (selStart >= 0 && selEnd > selStart
            && ScriptEditorControl.ResolveSelectionEntities(_text, selStart, selEnd - selStart, _entityResolveBasePath) is { Count: >= 2 } many)
        {
            EntityMultiPeekRequested?.Invoke(many);
            return;
        }
        var result = ScriptEditorControl.ResolveF12(_text, offset, _entityResolveBasePath);
        if (result.NavigatePath is { } target) EntityNavigationRequested?.Invoke(target);
        else if (result.PeekName is { } name) EntityValuePeekRequested?.Invoke(name, result.PeekValue ?? "", result.PeekDeclaringPath!);
        else if (result.Message is { } msg)
            MessageBox.Show(this, msg, "Bcode", MessageBoxButtons.OK,
                result.IsWarning ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
    }

    /// <summary>Same contract as <see cref="ScriptEditorControl.LoadContent"/>: a null
    /// <paramref name="path"/> shows <paramref name="content"/> as a plain message (loading
    /// hint, "not a text file", read error) rather than as a document.</summary>
    public void LoadContent(string? path, string content, string? entityResolveBasePath = null)
    {
        if (_ed is not null) { LoadNative(path, content, entityResolveBasePath); return; }
        CurrentPath = path;
        _entityResolveBasePath = entityResolveBasePath ?? path;
        if (path is not null) ScriptEditorControl.PrewarmEntityIndex(_entityResolveBasePath, content);
        if (path is null)
        {
            _text = "";
            Send(JsonSerializer.Serialize(new { type = "message", text = content }));
            return;
        }
        _text = content.Replace("\r\n", "\n").Replace('\r', '\n');
        Send(JsonSerializer.Serialize(new { type = "load", path, content = _text }));
    }

    public void Clear()
    {
        if (_ed is not null) { LoadNative(null, "", null); return; }
        CurrentPath = null;
        _entityResolveBasePath = null;
        _text = "";
        Send(JsonSerializer.Serialize(new { type = "message", text = "" }));
    }

    /// <summary>Kept for parity with ScriptEditorControl — a read-only preview is never dirty.</summary>
    public void MarkSaved() { }

    public void ShowFind()
    {
        if (_ed is not null) { ShowFindNative(); return; }
        _web.Focus();
        if (_ready) Post(JsonSerializer.Serialize(new { type = "find" }));
    }

    private void Send(string json)
    {
        _pendingLoad = json;
        if (!_ready) return;
        PushThemeIfChanged(); // BcodeViewer may have switched theme since the last file
        Post(json);
    }

    private void Post(string json)
    {
        try { _web.CoreWebView2?.PostWebMessageAsString(json); }
        catch { /* torn down */ }
    }

    /// <summary>Theme của khung xem trước theo sáng / tối của Bcode: theme BcodeViewer đang dùng (viewer-theme.json) nếu cùng sáng / tối
    /// với Bcode — tô màu giống hệt khi mở bằng BcodeViewer; khác (vd Bcode sáng, Viewer Dark+) hoặc chưa có file thì dùng theme dựng từ
    /// màu của Bcode (<see cref="BuiltInTheme"/>). Chỉ đẩy lại khi sáng / tối, màu Bcode hoặc file viewer-theme.json đổi.</summary>
    private void PushThemeIfChanged()
    {
        try
        {
            var dark = AppColors.IsDark;
            var path = ThemeFilePath;
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;
            var key = $"{dark}|{Hex(AppColors.Background)}|{Hex(AppColors.Text)}|{stamp.Ticks}";
            if (key == _themeKey) return;
            if (stamp != default)
            {
                using var theme = JsonDocument.Parse(File.ReadAllText(path));
                var viewerDark = !theme.RootElement.TryGetProperty("isDark", out var d) || d.ValueKind != JsonValueKind.False;
                if (viewerDark == dark)
                {
                    _themeKey = key;
                    Post(JsonSerializer.Serialize(new { type = "theme", theme = theme.RootElement }));
                    return;
                }
            }
            _themeKey = key;
            Post(JsonSerializer.Serialize(new { type = "theme", theme = BuiltInTheme(dark) }));
        }
        catch { /* half-written or unreadable — keep the current theme */ }
    }

    /// <summary>Theme dạng viewer-theme.json dựng từ bảng màu Bcode: nền Monaco vs / vs-dark (tô màu XML / JS như Light+ / Dark+ của
    /// BcodeViewer), luật SQL giống VsCodeSqlRules của BcodeViewer (ThemeCatalog.cs).</summary>
    private static object BuiltInTheme(bool dark) => new
    {
        id = dark ? "bcode-dark" : "bcode-light",
        name = dark ? "Bcode Dark" : "Bcode Light",
        isDark = dark,
        monacoBase = dark ? "vs-dark" : "vs",
        colors = new
        {
            background = Hex(AppColors.Background), panel = Hex(AppColors.Panel), panelAlt = Hex(AppColors.PanelAlt),
            border = Hex(AppColors.Border), text = Hex(AppColors.Text), textMuted = Hex(AppColors.TextMuted),
            accent = Hex(AppColors.Accent), accentHover = Hex(AppColors.AccentHover), accentText = Hex(AppColors.OnAccent),
            selection = Hex(AppColors.Selection), input = Hex(AppColors.Input), buttonBack = Hex(AppColors.ButtonBack),
        },
        editor = new { lineHighlight = dark ? "#282828" : "#f5f5f5", selection = dark ? "#264f78" : "#add6ff" },
        rules = dark
            ? new object[] { new { token = "string.sql", foreground = "CE9178" }, new { token = "predefined.sql", foreground = "DCDCAA" }, new { token = "operator.sql", foreground = "D4D4D4" } }
            : new object[] { new { token = "string.sql", foreground = "A31515" }, new { token = "predefined.sql", foreground = "795E26" }, new { token = "operator.sql", foreground = "000000" } },
    };

    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
}
