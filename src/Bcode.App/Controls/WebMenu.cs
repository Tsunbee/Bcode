using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// A popup menu drawn as HTML/CSS (Web/Shell/contextmenu.html) in a borderless window,
/// replacing ContextMenuStrip where the menu is long enough that grouping and emphasis
/// actually matter.
///
/// ContextMenuStrip could be recolored (ThemeManager.ApplyMenu does), but not restyled: its
/// item height, padding, check column and separator spacing come from the native renderer, so
/// a 15-item grid menu stayed a dense, flat list with no way to caption groups or mark one
/// entry destructive. Here the menu is just a page, so it gets section captions, a shortcut
/// column, rounded corners and a real hover state for free.
///
/// The window deliberately does not steal focus from its owner in a way that would move the
/// caret out of the editor — it is shown as a tool window owned by the host form, and closes
/// on selection, Esc, or as soon as it is deactivated.
/// </summary>
public sealed class WebMenu
{
    private sealed class MenuEntry
    {
        public string Kind { get; init; } = "item"; // item | separator | caption
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public string? Shortcut { get; init; }
        public bool Enabled { get; init; } = true;
        public bool Checked { get; init; }
        public bool Danger { get; init; }
        public Action? OnClick { get; init; }
    }

    private readonly List<MenuEntry> _entries = new();

    public WebMenu Add(string label, Action onClick, string? shortcut = null, bool enabled = true, bool @checked = false, bool danger = false)
    {
        _entries.Add(new MenuEntry
        {
            Id = _entries.Count.ToString(),
            Label = label,
            Shortcut = shortcut,
            Enabled = enabled,
            Checked = @checked,
            Danger = danger,
            OnClick = onClick,
        });
        return this;
    }

    public WebMenu AddSeparator()
    {
        _entries.Add(new MenuEntry { Kind = "separator" });
        return this;
    }

    /// <summary>A non-clickable group heading ("COPY", "SCRIPT", ...).</summary>
    public WebMenu AddCaption(string label)
    {
        _entries.Add(new MenuEntry { Kind = "caption", Label = label });
        return this;
    }

    public bool IsEmpty => _entries.Count == 0;

    /// <summary>Shows the menu at a screen point. Returns immediately — the menu is
    /// modeless, and the chosen item's callback runs on the UI thread afterwards.</summary>
    public void Show(Control owner, Point screenPoint)
    {
        if (_entries.Count == 0) return;
        var host = owner.FindForm();
        if (host is null) return;

        var popup = new PopupWindow(_entries, screenPoint);
        popup.Show(host);
    }

    /// <summary>Wires right-click on <paramref name="owner"/> to build and show a menu.
    /// <paramref name="build"/> runs per click (so item state — enabled, checked — reflects
    /// the moment of the click), and returning null or an empty menu shows nothing.</summary>
    public static void AttachTo(Control owner, Func<WebMenu?> build)
    {
        // A TextBoxBase/RichTextBox shows the OS's own Undo/Cut/Copy/Paste menu on right-click
        // unless a ContextMenuStrip is assigned — assigning one that always cancels its own
        // Opening suppresses that, leaving the HTML menu below as the only one that appears.
        var suppressNative = new ContextMenuStrip();
        suppressNative.Opening += (_, e) => e.Cancel = true;
        owner.ContextMenuStrip = suppressNative;

        owner.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var menu = build();
            if (menu is { IsEmpty: false }) menu.Show(owner, e.X, e.Y);
        };
    }

    /// <summary>Convenience overload for a right-click on a control.</summary>
    public void Show(Control owner, int clientX, int clientY)
        => Show(owner, owner.PointToScreen(new Point(clientX, clientY)));

    private sealed class PopupWindow : Form
    {
        private readonly List<MenuEntry> _entries;
        private readonly Microsoft.Web.WebView2.WinForms.WebView2 _web = new() { Dock = DockStyle.Fill };
        private readonly Point _anchor;
        private bool _ready;

        public PopupWindow(List<MenuEntry> entries, Point anchor)
        {
            _entries = entries;
            _anchor = anchor;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // Sized for real once the page reports its content size; start small so a slow
            // first paint never flashes a big empty rectangle on screen.
            Size = new Size(10, 10);
            Location = anchor;
            BackColor = AppColors.PanelAlt;
            Controls.Add(_web);
            Deactivate += (_, _) => Close();
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
                _web.DefaultBackgroundColor = Color.Transparent;
                _web.CoreWebView2.WebMessageReceived += (_, e) => OnMessage(e.TryGetWebMessageAsString());
                _web.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    _ready = true;
                    Push($"window.setTheme && window.setTheme({(AppColors.IsDark ? "true" : "false")})");
                    Push($"window.render && window.render({JsonSerializer.Serialize(Payload())})");
                };
                _web.CoreWebView2.Navigate($"https://{WebViewEnvironment.Host}/contextmenu.html");
            }
            catch
            {
                // No WebView2 Runtime — fall back to a stock ContextMenuStrip so the commands
                // stay reachable (ThemeManager still colors it).
                Close();
                ShowFallbackMenu();
            }
        }

        private string Payload() => JsonSerializer.Serialize(_entries.Select(en => new
        {
            kind = en.Kind,
            id = en.Id,
            label = en.Label,
            shortcut = en.Shortcut,
            enabled = en.Enabled,
            @checked = en.Checked,
            danger = en.Danger,
        }));

        private void OnMessage(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            switch (root.TryGetProperty("action", out var a) ? a.GetString() : null)
            {
                case "size":
                    ResizeToContent(root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32());
                    break;
                case "invoke":
                    var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
                    var entry = _entries.FirstOrDefault(en => en.Id == id);
                    Close();
                    entry?.OnClick?.Invoke();
                    break;
                case "close":
                    Close();
                    break;
            }
        }

        private void ResizeToContent(int width, int height)
        {
            Size = new Size(width, height);
            // Keep the whole menu on the screen the click happened on — a menu opened near the
            // right or bottom edge flips back over the anchor instead of being clipped, which
            // is what the native menu did for free.
            var screen = Screen.FromPoint(_anchor).WorkingArea;
            var x = Math.Min(_anchor.X, screen.Right - width);
            var y = _anchor.Y + height > screen.Bottom ? _anchor.Y - height : _anchor.Y;
            Location = new Point(Math.Max(screen.Left, x), Math.Max(screen.Top, y));
            _web.Focus();
        }

        private void Push(string script)
        {
            if (!_ready || _web.CoreWebView2 is null) return;
            _ = _web.CoreWebView2.ExecuteScriptAsync(script);
        }

        private void ShowFallbackMenu()
        {
            var menu = new ContextMenuStrip();
            foreach (var entry in _entries)
            {
                if (entry.Kind == "separator") { menu.Items.Add(new ToolStripSeparator()); continue; }
                if (entry.Kind == "caption") { menu.Items.Add(new ToolStripLabel(entry.Label) { Enabled = false }); continue; }
                var item = new ToolStripMenuItem(entry.Label)
                {
                    Enabled = entry.Enabled,
                    Checked = entry.Checked,
                    ShortcutKeyDisplayString = entry.Shortcut,
                };
                var onClick = entry.OnClick;
                item.Click += (_, _) => onClick?.Invoke();
                menu.Items.Add(item);
            }
            ThemeManager.ApplyMenu(menu);
            menu.Show(_anchor);
        }
    }
}
