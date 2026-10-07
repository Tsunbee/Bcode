using System.Text.Json;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// Command Palette (Ctrl+P): hộp tìm nhanh nổi ở đầu cửa sổ — gõ vài chữ để nhảy tới tool, tab đang mở, lệnh, menu WCommand hoặc object SQL
/// (Enter = mở; Shift+Enter trên object SQL = "Ai đang dùng object này?"). Giao diện là trang WebView2 (Web/Shell/commandpalette.html — tìm mờ theo từng
/// ký tự, chạy hoàn toàn trong trang trên danh sách đã nạp sẵn nên gõ tới đâu lọc tới đó, không đợi database); form chỉ nạp danh sách và báo lại mục được chọn.
/// Tự đóng khi bấm ra ngoài hoặc Esc.
/// </summary>
public class CommandPaletteForm : ThemedForm
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string _itemsJson;
    private readonly string _recentJson;
    private bool _closing;

    /// <summary>(kind, id, secondary): secondary = Shift+Enter.</summary>
    public event Action<string, string, bool>? Chosen;

    public CommandPaletteForm(string itemsJson, string recentJson)
    {
        _itemsJson = itemsJson;
        _recentJson = recentJson;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        Padding = new Padding(1);                 // viền 1px màu nhấn quanh trang
        BackColor = AppColors.Accent;
        Controls.Add(_web);
        Deactivate += (_, _) => CloseOnce();
        ThemeManager.ThemeChanged += PushTheme;
        FormClosed += (_, _) => ThemeManager.ThemeChanged -= PushTheme;
        Load += async (_, _) => await InitWebAsync();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner is { } o)
        {
            var w = Math.Clamp((int)(o.ClientSize.Width * 0.5), DpiScale.Px(this, 560), DpiScale.Px(this, 900));
            var h = Math.Clamp((int)(o.ClientSize.Height * 0.62), DpiScale.Px(this, 360), DpiScale.Px(this, 640));
            Size = new Size(w, h);
            Location = new Point(o.Left + (o.Width - w) / 2, o.Top + DpiScale.Px(this, 90));
        }
        Activate();
    }

    private void CloseOnce()
    {
        if (_closing || IsDisposed) return;
        _closing = true;
        try { BeginInvoke(new Action(Close)); } catch { /* đang đóng */ }
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                PushTheme();
                _ = _web.CoreWebView2.ExecuteScriptAsync($"palette.init({_itemsJson}, {_recentJson})");
                _web.Focus();
                _ = _web.CoreWebView2.ExecuteScriptAsync("palette.focus()");
            };
            _web.CoreWebView2.Navigate(UiOverrides.UrlFor("commandpalette.html"));
        }
        catch { CloseOnce(); }
    }

    private void PushTheme() { if (_web.CoreWebView2 is not null) _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({(AppColors.IsDark ? "true" : "false")})"); }

    private void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            switch (root.GetProperty("action").GetString())
            {
                case "choose":
                    _closing = true;                         // đóng xong mới chạy lệnh (để cửa sổ chính lấy lại focus)
                    var kind = root.GetProperty("kind").GetString() ?? "";
                    var id = root.GetProperty("id").GetString() ?? "";
                    var secondary = root.TryGetProperty("secondary", out var s) && s.ValueKind == JsonValueKind.True;
                    Close();
                    Chosen?.Invoke(kind, id, secondary);
                    break;
                case "close":
                    CloseOnce();
                    break;
            }
        }
        catch { CloseOnce(); }
    }
}
