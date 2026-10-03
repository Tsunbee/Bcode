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
        // Lần tạo bị lỗi (vd "Class not registered" khi gọi từ trong callback của WebView2 khác) thì KHÔNG nhớ kết quả lỗi —
        // trước đây task lỗi được cache nên MỌI cửa sổ WebView2 sau đó đều hỏng cho tới khi mở lại app.
        if (_envTask is { IsFaulted: true } or { IsCanceled: true }) _envTask = null;
        return _envTask ??= CreateAsync();
    }

    private static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> CreateAsync()
    {
        var userDataFolder = Path.Combine(
            BcodePaths.AppData, "Bcode", "WebView2");
        return Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
    }

    /// <summary>Ensures <paramref name="web"/> is initialized against the shared environment and
    /// has the shell host mapped in — the two steps every shell/tool WebView2 control needs
    /// before it can Navigate to one of its own pages.</summary>
    public static async Task InitAsync(Microsoft.Web.WebView2.WinForms.WebView2 web)
    {
        // Nền chờ theo theme (trước khi trang vẽ xong): không thì WebView2 loé trắng mỗi lần mở/đổi tab.
        web.DefaultBackgroundColor = AppColors.PanelAlt;
        void Recolor() { try { if (!web.IsDisposed) web.DefaultBackgroundColor = AppColors.PanelAlt; } catch { } }
        ThemeManager.ThemeChanged += Recolor;
        web.Disposed += (_, _) => ThemeManager.ThemeChanged -= Recolor;
        var environment = await GetAsync();
        await web.EnsureCoreWebView2Async(environment);
        web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            Host, WebFolder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        // Tắt phím tắt của trình duyệt (F5/Ctrl+R = tải lại trang, Ctrl+P, F12...): trước đây bấm F5 khi Monaco/thanh công cụ chưa kịp
        // đăng ký phím F5 riêng (vd vừa Ctrl+chuột phải mở store/function ở tab mới) thì WebView2 tải lại cả trang → mất nội dung, trang trắng.
        // Các phím tắt của Bcode (F5 chạy, Ctrl+W...) vẫn do trang/MainForm tự xử lý như cũ.
        web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        // Host riêng cho các trang HTML người dùng đã ghi đè (xem UiOverrides): trang nạp từ đây vẫn dùng shell.css/script gốc nhờ thẻ <base>.
        try
        {
            Directory.CreateDirectory(UiOverrides.Folder);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(UiOverrides.UserHost, UiOverrides.Folder,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        }
        catch { /* không tạo được thư mục tuỳ chỉnh — mọi trang dùng bản gốc */ }
        InstallGlobalShortcuts(web.CoreWebView2);
        UiScale.BindZoom(web);
    }

    /// <summary>Phím tắt TOÀN CỤC bấm khi focus đang ở trong 1 trang WebView2 (Monaco, thanh công cụ HTML...): phím không đi qua
    /// ProcessCmdKey của form nên MainForm không thấy. Mỗi trang được gắn 1 script bắt phím rồi báo lên đây qua web message
    /// (<c>{action:"__global-shortcut", key}</c> — action lạ nên các trang/handler khác bỏ qua, giống "__height").</summary>
    public static event Action<string>? GlobalShortcut;

    private const string GlobalShortcutScript = @"
(function () {
  var NAMED = { Tab: 'Tab', Enter: 'Enter', NumpadEnter: 'Enter', Space: 'Space', Backspace: 'Backspace', Delete: 'Delete', Insert: 'Insert',
    Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
    Backquote: '`', Minus: '-', Equal: '=', BracketLeft: '[', BracketRight: ']', Backslash: '\\', Semicolon: ';', Quote: ""'"", Comma: ',', Period: '.', Slash: '/' };
  // Tổ hợp dạng chuẩn ""Ctrl+Alt+Shift+Phím"" — trùng định dạng với Bcode.App.UI.ShortcutRegistry.
  // Trang HTML tuỳ chỉnh (host bcode.user): lỗi script lúc nạp → báo C# để cách ly và quay về bản gốc (xem UiOverrides.Quarantine).
  if (location.host === 'bcode.user') {
    var reported = false, t0 = Date.now();
    var report = function (msg) {
      if (reported || Date.now() - t0 > 8000) return; reported = true;
      window.chrome.webview.postMessage(JSON.stringify({ action: '__ui-error', page: location.pathname.split('/').pop(), message: String(msg).slice(0, 200) }));
    };
    window.addEventListener('error', function (e) { report(e.message || 'lỗi script'); });
    window.addEventListener('unhandledrejection', function (e) { report((e.reason && e.reason.message) || e.reason || 'lỗi bất đồng bộ'); });
  }
  window.__bcodeCombo = function (e) {
    var c = e.code, k = null;
    if (/^Key[A-Z]$/.test(c)) k = c.charAt(3);
    else if (/^Digit[0-9]$/.test(c)) k = c.charAt(5);
    else if (/^F([1-9]|1[0-9]|2[0-4])$/.test(c)) k = c;
    else if (NAMED[c]) k = NAMED[c];
    if (!k || e.metaKey) return null;
    var m = [];
    if (e.ctrlKey) m.push('Ctrl'); if (e.altKey) m.push('Alt'); if (e.shiftKey) m.push('Shift');
    m.push(k);
    return m.join('+');
  };
  // Danh sách tổ hợp toàn cửa sổ do C# đẩy vào (window.__bcodeKeys — xem UiTemplate.BuildScript): khớp thì chặn phím và báo lên MainForm.
  document.addEventListener('keydown', function (e) {
    var keys = window.__bcodeKeys;
    if (!keys || !keys.length || e.isComposing) return;
    var combo = window.__bcodeCombo(e);
    if (combo && keys.indexOf(combo) >= 0) {
      e.preventDefault(); e.stopPropagation();
      window.chrome.webview.postMessage(JSON.stringify({ action: '__global-shortcut', key: combo }));
    }
  }, true);
})();";

    private static void InstallGlobalShortcuts(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(GlobalShortcutScript);
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                if (!doc.RootElement.TryGetProperty("action", out var a)) return;
                var action = a.GetString() ?? "";
                if (action == "__global-shortcut" && doc.RootElement.TryGetProperty("key", out var k))
                    GlobalShortcut?.Invoke(k.GetString() ?? "");
                else if (action.StartsWith("__ui-", StringComparison.Ordinal))
                    UiOverrides.HandleMessage(action, doc.RootElement.Clone());
            }
            catch { /* không phải JSON của mình — bỏ qua */ }
        };
    }
}
