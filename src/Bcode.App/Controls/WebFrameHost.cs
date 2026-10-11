using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// MỘT WebView2 chứa nhiều trang Web/Shell/*.html, mỗi trang là 1 iframe (Web/Shell/framehost.html) — thay cho mỗi trang 1 WebView2 riêng
/// (<see cref="WebBarHost"/>) để bớt RAM: đo 2026-10-10, 4 trang khung kết quả SQL: 4 WebView2 ~230MB → 1 WebView2 + 4 iframe ~182MB.
/// Các trang giữ NGUYÊN mã: script chèn vào mỗi iframe thay <c>window.chrome.webview</c> bằng bản chuyển tiếp qua trang chủ, gắn tên khung
/// vào tin nhắn (<c>__frame</c>) để C# chuyển đúng <see cref="IWebPage"/>. Khung chỉ được tạo khi hiện lần đầu (như WebBarHost ẩn chưa tạo).
/// Chỉ dùng được cho trang cùng host <see cref="WebViewEnvironment.Host"/> — trang người dùng đã ghi đè (UiOverrides) nằm host khác nên
/// nơi dùng phải kiểm <see cref="CanHost"/> và quay về WebBarHost.
/// </summary>
public sealed class WebFrameHost : Panel
{
    private const string FrameShim = """
        (() => {
          if (window === window.top) return;
          let name = null;
          try { name = window.frameElement && window.frameElement.getAttribute('data-bframe'); } catch { return; }
          if (!name) return;
          const real = window.top.chrome.webview, listeners = [];
          const shim = {
            postMessage(m) {
              let o = m;
              if (typeof m === 'string') { try { o = JSON.parse(m); } catch { o = { raw: m }; } }
              if (!o || typeof o !== 'object') o = { raw: o };
              o.__frame = name;
              real.postMessage(JSON.stringify(o));
            },
            addEventListener(t, fn) { if (t === 'message') listeners.push(fn); },
            removeEventListener(t, fn) { const i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); },
            hostObjects: real.hostObjects,
          };
          window.__bframeDeliver = data => listeners.slice().forEach(fn => { try { fn({ data }); } catch (e) { setTimeout(() => { throw e; }); } });
          try { Object.defineProperty(window.chrome, 'webview', { value: shim, configurable: true, writable: true }); }
          catch { window.chrome.webview = shim; }
        })();
        """;

    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly List<Frame> _frames = new();
    private bool _hostReady;

    public WebFrameHost()
    {
        BackColor = AppColors.PanelAlt;
        Controls.Add(_web);
        ThemeManager.ThemeChanged += PushThemeAll;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= PushThemeAll;
    }

    /// <summary>Gọi (và chờ) ngay sau khi WebView2 khởi tạo xong, trước khi nạp framehost.html — chỗ đặt cài đặt mức WebView2 (vd tắt menu chuột phải mặc định)
    /// và AddScriptToExecuteOnDocumentCreated cho trang con (script áp cho cả khung chính lẫn mọi iframe).</summary>
    public Func<Microsoft.Web.WebView2.Core.CoreWebView2, Task>? CoreInitializing { get; set; }

    /// <summary>Mọi trang đều nằm ở host gốc (không bị UiOverrides ghi đè) — điều kiện để gộp vào 1 WebView2.</summary>
    public static bool CanHost(params string[] pages) =>
        pages.All(p => UiOverrides.UrlFor(p).StartsWith($"https://{WebViewEnvironment.Host}/", StringComparison.OrdinalIgnoreCase));

    /// <summary>Khai báo 1 khung. <paramref name="height"/> = chiều cao cố định (px CSS, co theo zoom giao diện) cho thanh trên cùng;
    /// null = chia phần còn lại. Khung ẩn chưa tạo iframe cho tới khi <see cref="IWebPage"/> được hiện (<see cref="Show"/>).</summary>
    public IWebPage AddFrame(string name, string page, int? height = null, bool visible = true)
    {
        var f = new Frame(this, name, page, height, visible);
        _frames.Add(f);
        if (visible) f.Created = true;
        if (_hostReady && f.Created) HostCall($"bframe.add({Json(f.Name)}, {Json(UiOverrides.UrlFor(f.Page))}, {f.Height?.ToString() ?? "null"}, {Bool(f.Visible)})");
        return f;
    }

    /// <summary>Hiện / ẩn 1 khung (ẩn = display:none, trang vẫn giữ nguyên trạng thái).</summary>
    public void Show(IWebPage page, bool visible)
    {
        if (page is not Frame f || f.Visible == visible && f.Created) return;
        f.Visible = visible;
        if (!_hostReady) { if (visible) f.Created = true; return; }
        if (visible && !f.Created)
        {
            f.Created = true;
            HostCall($"bframe.add({Json(f.Name)}, {Json(UiOverrides.UrlFor(f.Page))}, {f.Height?.ToString() ?? "null"}, true)");
        }
        else if (f.Created) HostCall($"bframe.show({Json(f.Name)}, {Bool(visible)})");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            await _web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(FrameShim);
            if (CoreInitializing is { } init) await init(_web.CoreWebView2);
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                JsonDocument doc;
                try { doc = JsonDocument.Parse(e.TryGetWebMessageAsString()); } catch { return; }
                using (doc)
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("__frame", out var fn)) return;
                    var f = _frames.Find(x => x.Name == fn.GetString());
                    if (f is null) return;
                    if (root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String && a.GetString() == "__frame-ready") f.OnLoaded();
                    else f.Raise(root);
                }
            };
            _web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _hostReady = true;
                HostCall($"window.setTheme && window.setTheme({Bool(AppColors.IsDark)})");
                foreach (var f in _frames.Where(f => f.Created))
                    HostCall($"bframe.add({Json(f.Name)}, {Json(UiOverrides.UrlFor(f.Page))}, {f.Height?.ToString() ?? "null"}, {Bool(f.Visible)})");
            };
            _web.CoreWebView2.Navigate($"https://{WebViewEnvironment.Host}/framehost.html");
        }
        catch
        {
            // Không có WebView2 Runtime — để trống như WebBarHost.
            Controls.Remove(_web);
            _web.Dispose();
        }
    }

    /// <summary>Chạy script trong 1 khung và lấy kết quả (JSON, như CoreWebView2.ExecuteScriptAsync) — "null" nếu khung chưa sẵn sàng.</summary>
    public Task<string> EvalAsync(IWebPage page, string script)
    {
        if (page is not Frame f || !f.IsReady || _web.IsDisposed || _web.CoreWebView2 is null) return Task.FromResult("null");
        return _web.CoreWebView2.ExecuteScriptAsync($"bframe.eval({Json(f.Name)}, {Json(script)})");
    }

    /// <summary>Đặt chiều cao 1 khung theo px thiết bị (như chiều cao control WinForms) — host đổi sang px CSS.</summary>
    public void SetFrameHeightDevice(IWebPage page, int devicePx)
    {
        if (page is Frame f) HostCall($"bframe.setHeightDevice({Json(f.Name)}, {devicePx})");
    }

    private void HostCall(string script)
    {
        if (_web.IsDisposed || _web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void PushThemeAll()
    {
        HostCall($"window.setTheme && window.setTheme({Bool(AppColors.IsDark)})");
        foreach (var f in _frames) f.PushTheme();
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);
    private static string Bool(bool b) => b ? "true" : "false";

    /// <summary>1 khung iframe — cùng giao diện <see cref="IWebPage"/> như WebBarHost.</summary>
    private sealed class Frame : IWebPage
    {
        private readonly WebFrameHost _host;
        public string Name { get; }
        public string Page { get; }
        public int? Height { get; }
        public bool Visible { get; set; }
        public bool Created { get; set; }
        public bool IsReady { get; private set; }
        public event Action<JsonElement>? Message;
        public event Action? Ready;

        public Frame(WebFrameHost host, string name, string page, int? height, bool visible)
        {
            _host = host; Name = name; Page = page; Height = height; Visible = visible;
        }

        public void OnLoaded()
        {
            IsReady = true;
            PushTheme();
            Ready?.Invoke();
        }

        public void Raise(JsonElement root) => Message?.Invoke(root);

        public void PushTheme() => Call($"window.setTheme && window.setTheme({Bool(AppColors.IsDark)})");

        public void Call(string script)
        {
            if (!IsReady) return;
            _host.HostCall($"bframe.call({Json(Name)}, {Json(script)})");
        }

        public bool PostJson(string json)
        {
            if (!IsReady || _host._web.IsDisposed || _host._web.CoreWebView2 is null) return false;
            _host._web.CoreWebView2.PostWebMessageAsJson($"{{\"__frame\":{Json(Name)},\"data\":{json}}}");
            return true;
        }
    }
}
