using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>How an action reads in the bar — drives the CSS class in actionbar.html.</summary>
public enum WebActionKind
{
    /// <summary>Normal bordered button.</summary>
    Normal,
    /// <summary>The one action that actually does the dialog's job (Save, OK, Execute).</summary>
    Primary,
    /// <summary>Borderless/low-emphasis (Cancel, Close, "Hiện tất cả").</summary>
    Quiet,
    /// <summary>Destructive (Delete) — outlined, turns solid red on hover.</summary>
    Danger,
}

/// <summary>
/// The dialog button row, rendered as HTML/CSS in a WebView2 strip instead of a
/// FlowLayoutPanel full of stock WinForms buttons.
///
/// Why one generic page (Web/Shell/actionbar.html) driven by data, rather than one
/// hand-written HTML file per dialog like the tool toolbars (sqlquerybar.html etc.): every
/// dialog's bar is the same shape — a few secondary actions on the left, the OK/Cancel pair
/// on the right, sometimes an inline status line — so a per-dialog page would be a dozen
/// near-identical copies to keep in sync. Dialogs just declare their buttons in C#.
///
/// It also fixes what made the old rows look cluttered: a RightToLeft FlowLayoutPanel put
/// every button in one undifferentiated group with no visual hierarchy (Delete looked exactly
/// as important as Save), and the reversed flow meant the code's first button landed
/// right-most, which is why the orders read oddly. Here side and emphasis are stated
/// explicitly.
///
/// If WebView2 cannot start (no Runtime on the machine), the bar falls back to plain WinForms
/// buttons — a dialog with no usable OK button would be far worse than an unstyled one.
/// </summary>
public sealed class WebActionBar : Panel
{
    private sealed class ActionItem
    {
        public required string Id { get; init; }
        public required string Text { get; set; }
        public WebActionKind Kind { get; init; }
        public bool Left { get; init; }
        public string? Title { get; init; }
        public bool Enabled { get; set; } = true;
        public bool Visible { get; set; } = true;
    }

    private readonly List<ActionItem> _items = new();
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, Button> _fallbackButtons = new();
    private Label? _fallbackStatus;
    private bool _ready;
    private bool _fallback;
    private string _pendingStatus = "";
    private string _pendingStatusLevel = "";

    /// <summary>Raised on the UI thread with the id passed to <see cref="Add"/>.</summary>
    public event Action<string>? Invoked;

    /// <summary>Id fired by Enter anywhere in the owning form (the old AcceptButton).</summary>
    public string? DefaultActionId { get; set; }

    /// <summary>Id fired by Esc (the old CancelButton).</summary>
    public string? CancelActionId { get; set; }

    public WebActionBar()
    {
        Dock = DockStyle.Bottom;
        Height = 52;
        BackColor = AppColors.PanelAlt;
        Controls.Add(_web);
        ThemeManager.ThemeChanged += PushTheme;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= PushTheme;
    }

    /// <summary>Declares one button. Call every Add before the bar is shown; ordering within
    /// each side is the call order (left to right, unlike the old RightToLeft flow).</summary>
    public WebActionBar Add(string id, string text, WebActionKind kind = WebActionKind.Normal, bool left = false, string? title = null)
    {
        _items.Add(new ActionItem { Id = id, Text = text, Kind = kind, Left = left, Title = title });
        return this;
    }

    public void SetEnabled(string id, bool enabled)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null) return;
        item.Enabled = enabled;
        if (_fallback) { if (_fallbackButtons.TryGetValue(id, out var b)) b.Enabled = enabled; return; }
        Push($"window.setEnabled && window.setEnabled({Json(id)}, {(enabled ? "true" : "false")})");
    }

    public void SetVisible(string id, bool visible)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null) return;
        item.Visible = visible;
        if (_fallback) { if (_fallbackButtons.TryGetValue(id, out var b)) b.Visible = visible; return; }
        Push($"window.setVisible && window.setVisible({Json(id)}, {(visible ? "true" : "false")})");
    }

    public void SetText(string id, string text)
    {
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is null) return;
        item.Text = text;
        if (_fallback) { if (_fallbackButtons.TryGetValue(id, out var b)) b.Text = text; return; }
        Push($"window.setText && window.setText({Json(id)}, {Json(text)})");
    }

    /// <summary>The inline result line next to the buttons ("Đã lưu", "Kết nối thất bại: ...")
    /// — replaces the separate Label several dialogs squeezed into the same button row.</summary>
    public void SetStatus(string text, bool? ok = null)
    {
        _pendingStatus = text;
        _pendingStatusLevel = ok is null ? "" : ok.Value ? "ok" : "err";
        if (_fallback)
        {
            if (_fallbackStatus is not null)
            {
                _fallbackStatus.Text = text;
                _fallbackStatus.ForeColor = ok is null ? AppColors.TextMuted : ok.Value ? AppColors.Success : AppColors.Danger;
            }
            return;
        }
        Push($"window.setStatus && window.setStatus({Json(text)}, {Json(_pendingStatusLevel)})");
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = InitAsync();
        if (FindForm() is { } form)
        {
            form.KeyPreview = true;
            form.KeyDown -= FormKeyDown;
            form.KeyDown += FormKeyDown;
        }
    }

    // Enter/Esc used to come free from Form.AcceptButton/CancelButton, which only work with a
    // real WinForms Button — an HTML button can never be one, so the shortcut is re-created
    // here. Enter is ignored while a multiline TextBox has focus (there it inserts a newline,
    // exactly as AcceptButton already behaved).
    private void FormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        string? id = null;
        if (e.KeyCode == Keys.Enter && !ActiveControlAcceptsEnter()) id = DefaultActionId;
        else if (e.KeyCode == Keys.Escape) id = CancelActionId;
        if (id is null) return;

        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item is { Enabled: true, Visible: true })
        {
            e.Handled = e.SuppressKeyPress = true;
            Invoked?.Invoke(id);
        }
    }

    private bool ActiveControlAcceptsEnter()
        => FindForm()?.ActiveControl is TextBoxBase { Multiline: true } or Microsoft.Web.WebView2.WinForms.WebView2;

    private async Task InitAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                if (doc.RootElement.TryGetProperty("action", out var action) && action.GetString() is { } id)
                    Invoked?.Invoke(id);
            };
            _web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _ready = true;
                Render();
                PushTheme();
            };
            _web.CoreWebView2.Navigate($"https://{WebViewEnvironment.Host}/actionbar.html");
        }
        catch
        {
            BuildFallback();
        }
    }

    private void Render()
    {
        if (!_ready) return;
        var payload = _items.Select(i => new
        {
            id = i.Id,
            text = i.Text,
            kind = i.Kind switch
            {
                WebActionKind.Primary => "primary",
                WebActionKind.Quiet => "quiet",
                WebActionKind.Danger => "danger",
                _ => "",
            },
            side = i.Left ? "left" : "right",
            title = i.Title,
            enabled = i.Enabled,
            visible = i.Visible,
        }).ToList();
        Push($"window.render && window.render({Json(JsonSerializer.Serialize(payload))})");
        if (_pendingStatus.Length > 0)
            Push($"window.setStatus && window.setStatus({Json(_pendingStatus)}, {Json(_pendingStatusLevel)})");
    }

    private void PushTheme()
    {
        if (_fallback) { ThemeManager.Apply(this); return; }
        Push($"window.setTheme && window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }

    private void Push(string script)
    {
        if (!_ready || _web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    private static string Json(string? value) => JsonSerializer.Serialize(value ?? "");

    /// <summary>No WebView2 Runtime — rebuild the same bar out of plain buttons so the dialog
    /// still works. Deliberately minimal: it only has to be usable, not pretty.</summary>
    private void BuildFallback()
    {
        _fallback = true;
        Controls.Remove(_web);
        _web.Dispose();

        var right = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        var left = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Padding = new Padding(8) };
        _fallbackStatus = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = AppColors.TextMuted };

        foreach (var item in _items)
        {
            // PillButton, not a stock Button: it owner-draws the same rounded shape and
            // AppColors the HTML buttons use, so the no-WebView2 fallback still matches the
            // rest of the app instead of dropping back to grey system buttons.
            var button = PillButton.Flat(item.Text, primary: item.Kind == WebActionKind.Primary);
            button.Enabled = item.Enabled;
            button.Visible = item.Visible;
            button.Margin = new Padding(4);
            var id = item.Id;
            button.Click += (_, _) => Invoked?.Invoke(id);
            _fallbackButtons[id] = button;
            (item.Left ? left : right).Controls.Add(button);
        }

        Controls.Add(_fallbackStatus);
        Controls.Add(right);
        Controls.Add(left);
        ThemeManager.Apply(this);
    }
}
