using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Hosts one of the Web/Shell/*.html toolbar pages in a docked WebView2 strip and handles the
/// three things every such strip needs: initializing against the shared WebView2 environment,
/// parsing the page's postMessage payloads, and re-pushing the theme when it changes.
///
/// <see cref="WebActionBar"/> covers the common "row of dialog buttons" case with no HTML to
/// write at all; this is the escape hatch for a bar that needs its own page because it has
/// inputs or a custom layout (the Find box in WCommandScriptForm, for example). Controls that
/// already wire their own WebView2 by hand (RawSqlControl, SqlObjectTreeControl...) do exactly
/// what this class does — they predate it and can move over whenever they're touched next.
/// </summary>
public sealed class WebBarHost : Panel
{
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string _page;
    private bool _ready;

    /// <summary>Raised for every message the page posts, with the parsed JSON object. The
    /// element is only valid for the duration of the handler — copy anything you keep.</summary>
    public event Action<JsonElement>? Message;

    /// <summary>Raised once the page has loaded, after the theme has been pushed — the point
    /// where it is safe to call <see cref="Call"/> with initial state.</summary>
    public event Action? Ready;

    /// <param name="page">File name under Web/Shell, e.g. "scriptviewbar.html".</param>
    public WebBarHost(string page, int height = 40)
    {
        _page = page;
        Dock = DockStyle.Top;
        Height = height;
        BackColor = AppColors.PanelAlt;
        Controls.Add(_web);
        ThemeManager.ThemeChanged += PushTheme;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= PushTheme;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = InitAsync();
    }

    /// <summary>Runs a script in the page — no-op until the page has loaded, so callers don't
    /// have to guard on readiness themselves.</summary>
    public void Call(string script)
    {
        if (!_ready || _web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    /// <summary>Moves keyboard focus into the page's WebView2 — needed before a script can
    /// focus() one of the page's own inputs (e.g. TableEditControl putting the caret in its
    /// Table box on open). No-op until the page has loaded.</summary>
    public void FocusWeb()
    {
        if (!_ready || _web.IsDisposed || _web.CoreWebView2 is null) return;
        _web.Focus();
    }

    /// <summary>JSON-encodes a string for embedding in a <see cref="Call"/> script.</summary>
    public static string Json(string? value) => JsonSerializer.Serialize(value ?? "");

    private async Task InitAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                Message?.Invoke(doc.RootElement);
            };
            _web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _ready = true;
                PushTheme();
                Ready?.Invoke();
            };
            _web.CoreWebView2.Navigate($"https://{WebViewEnvironment.Host}/{_page}");
        }
        catch
        {
            // No WebView2 Runtime — leave the strip blank rather than taking the window down
            // with it. The host form keeps its own keyboard shortcuts, so the window is still
            // usable; WebActionBar's fallback covers the case where buttons are the only way
            // to act at all.
            Controls.Remove(_web);
            _web.Dispose();
        }
    }

    private void PushTheme() => Call($"window.setTheme && window.setTheme({(AppColors.IsDark ? "true" : "false")})");
}
