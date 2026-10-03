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
///      file in BcodeViewer.
/// Read-only. Exposes the subset of ScriptEditorControl's API that FileLookupControl uses,
/// and F12 resolves through the very same code (<see cref="ScriptEditorControl.ResolveF12"/>).
/// </summary>
public class MonacoPreviewControl : UserControl
{
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _web = new() { Dock = DockStyle.Fill };
    private bool _ready;
    private string? _pendingLoad;     // last load/message JSON, sent once the page says "ready"
    private string _text = "";        // what the page shows, '\n' line endings — F12 offsets index into this
    private string? _entityResolveBasePath;
    private DateTime _themeStamp;     // write time of viewer-theme.json last pushed

    public string? CurrentPath { get; private set; }

    /// <summary>F12 on a SYSTEM entity / quoted Include path — the file to open.</summary>
    public event Action<string>? EntityNavigationRequested;

    /// <summary>F12 on a VALUE entity — name, value, declaring file.</summary>
    public event Action<string, string, string>? EntityValuePeekRequested;

    private static string ThemeFilePath => Path.Combine(BcodePaths.AppData, "Bcode", "viewer-theme.json");

    public MonacoPreviewControl()
    {
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _ = InitAsync();
        WatchThemeFile();
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
            _web.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("filepreview.html"));
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
        int offset = 0;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            action = doc.RootElement.GetProperty("action").GetString();
            if (doc.RootElement.TryGetProperty("offset", out var o)) offset = o.GetInt32();
        }
        catch { return; }

        switch (action)
        {
            case "ready":
                _ready = true;
                _themeStamp = default;
                PushThemeIfChanged();
                if (_pendingLoad is not null) Post(_pendingLoad);
                break;
            case "f12":
                OnF12(offset);
                break;
            case "goto":
                // Ctrl+G — same "Go to" dialog as the old preview; the page moves the caret.
                if (CurrentPath is not null && ScriptEditorControl.ShowGoToDialog(FindForm(), _text, offset) is int target)
                    Post(JsonSerializer.Serialize(new { type = "reveal", offset = target }));
                else _web.Focus();
                break;
        }
    }

    private void OnF12(int offset)
    {
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
        CurrentPath = path;
        _entityResolveBasePath = entityResolveBasePath ?? path;
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
        CurrentPath = null;
        _entityResolveBasePath = null;
        _text = "";
        Send(JsonSerializer.Serialize(new { type = "message", text = "" }));
    }

    /// <summary>Kept for parity with ScriptEditorControl — a read-only preview is never dirty.</summary>
    public void MarkSaved() { }

    public void ShowFind()
    {
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

    /// <summary>Re-reads viewer-theme.json only when BcodeViewer has rewritten it. Missing
    /// file (BcodeViewer never run) leaves theme.js's built-in Dark+ default.</summary>
    private void PushThemeIfChanged()
    {
        try
        {
            var path = ThemeFilePath;
            if (!File.Exists(path)) return;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (stamp == _themeStamp) return;
            using var theme = JsonDocument.Parse(File.ReadAllText(path));
            _themeStamp = stamp;
            Post(JsonSerializer.Serialize(new { type = "theme", theme = theme.RootElement }));
        }
        catch { /* half-written or unreadable — keep the current theme */ }
    }
}
