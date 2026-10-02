namespace Bcode.App.UI;

/// <summary>
/// One shared <see cref="Microsoft.Web.WebView2.Core.CoreWebView2Environment"/> for the whole
/// app, reused by every WebView2 control (MainForm's shell chrome, RawSqlControl's toolbar,
/// and any more added later) instead of each one creating its own. Multiple WebView2 controls
/// on one environment share the same underlying browser process — only a cheap per-control
/// renderer is spun up per instance — which matters here because tools like "SQL Query" open a
/// brand-new control (and, with it, a brand-new toolbar WebView2) on every tab: without this
/// sharing, opening several tabs would spin up several independent Edge/Chromium processes
/// instead of one, working directly against the "giữ được tốc độ" (keep it fast) requirement
/// that WebView2 was picked for in the first place.
/// </summary>
internal static class WebViewEnvironment
{
    /// <summary>Virtual host name every shell/tool WebView2 page navigates to
    /// (see <see cref="WebFolder"/> for what it maps to).</summary>
    public const string Host = "bcode.shell";

    /// <summary>Local folder served under <see cref="Host"/> — all shell/tool HTML/CSS/JS
    /// pages live flat under here (Web/Shell), copied to the output directory by the
    /// csproj's Content/Web/** item.</summary>
    public static string WebFolder => Path.Combine(AppContext.BaseDirectory, "Web", "Shell");

    private static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment>? _envTask;

    public static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> GetAsync()
    {
        return _envTask ??= CreateAsync();
    }

    private static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> CreateAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "WebView2");
        return Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
    }

    /// <summary>Ensures <paramref name="web"/> is initialized against the shared environment and
    /// has the shell host mapped in — the two steps every shell/tool WebView2 control needs
    /// before it can Navigate to one of its own pages.</summary>
    public static async Task InitAsync(Microsoft.Web.WebView2.WinForms.WebView2 web)
    {
        var environment = await GetAsync();
        await web.EnsureCoreWebView2Async(environment);
        web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            Host, WebFolder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        InstallGlobalShortcuts(web.CoreWebView2);
    }

    /// <summary>Phím tắt TOÀN CỤC bấm khi focus đang ở trong 1 trang WebView2 (Monaco, thanh công cụ HTML...): phím không đi qua
    /// ProcessCmdKey của form nên MainForm không thấy. Mỗi trang được gắn 1 script bắt phím rồi báo lên đây qua web message
    /// (<c>{action:"__global-shortcut", key}</c> — action lạ nên các trang/handler khác bỏ qua, giống "__height").</summary>
    public static event Action<string>? GlobalShortcut;

    private const string GlobalShortcutScript = @"
document.addEventListener('keydown', function (e) {
  if (e.ctrlKey && !e.shiftKey && !e.altKey && !e.metaKey && (e.code === 'Digit3' || e.key === '3')) {
    e.preventDefault(); e.stopPropagation();
    window.chrome.webview.postMessage(JSON.stringify({ action: '__global-shortcut', key: 'ctrl+3' }));
  }
}, true);";

    private static void InstallGlobalShortcuts(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(GlobalShortcutScript);
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                if (doc.RootElement.TryGetProperty("action", out var a) && a.GetString() == "__global-shortcut"
                    && doc.RootElement.TryGetProperty("key", out var k))
                    GlobalShortcut?.Invoke(k.GetString() ?? "");
            }
            catch { /* không phải JSON của mình — bỏ qua */ }
        };
    }
}
