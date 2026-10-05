using System.Text;
using System.Text.Json;
using Bcode.App.Controls;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Cửa sổ "Script" của View Script (giống FCode): Save / Clear / Close + ô Find, nội dung nằm
/// trong Monaco (Web/Shell/scriptpopup.html) chứ không phải RichTextBox như
/// <see cref="WCommandScriptForm"/> — script DELETE + INSERT của bảng 18k+ dòng nạp vào
/// RichTextBox là treo, Monaco thì mở tức thì và vẫn sửa được trước khi Save.
/// </summary>
public class ScriptPopupForm : ThemedForm
{
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly WebBarHost _toolbar;
    private readonly string _defaultFileName;
    private string _pendingText;
    private bool _ready;

    /// <summary>Bấm Clear — View Script dùng để xoá luôn Script Cart.</summary>
    public event Action? Cleared;

    public ScriptPopupForm(string script, string title = "Script", string defaultFileName = "script.sql")
    {
        Text = title;
        Width = 900;
        Height = 600;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        _pendingText = script;
        _defaultFileName = defaultFileName;

        _toolbar = new WebBarHost("scriptviewbar.html", height: 42);
        _toolbar.Message += msg =>
        {
            var action = msg.TryGetProperty("action", out var a) ? a.GetString() : null;
            switch (action)
            {
                case "save": _ = SaveToFileAsync(); break;
                case "clear":
                    Post(new { type = "clear" });
                    foreach (var tb in Controls.OfType<TextBox>()) tb.Clear(); // khung dự phòng khi không có WebView2
                    Cleared?.Invoke();
                    break;
                case "close": Close(); break;
                case "find":
                    Post(new { type = "find", value = msg.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "" });
                    break;
            }
        };

        Controls.Add(_web);
        Controls.Add(_toolbar);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                string? action, text = null;
                try
                {
                    using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
                    action = doc.RootElement.GetProperty("action").GetString();
                    if (doc.RootElement.TryGetProperty("text", out var t)) text = t.GetString();
                }
                catch { return; }

                switch (action)
                {
                    case "ready":
                        _ready = true;
                        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
                        Post(new { type = "load", text = _pendingText });
                        _pendingText = "";
                        break;
                    case "find-status":
                        _toolbar.Call($"window.setFindStatus && window.setFindStatus({WebBarHost.Json(text)})");
                        break;
                }
            };
            _web.CoreWebView2.Navigate(UiOverrides.UrlFor("scriptpopup.html"));
        }
        catch
        {
            // Không có WebView2 Runtime — hiện script trong TextBox thường để vẫn xem/Save được.
            Controls.Remove(_web);
            Controls.Add(new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false,
                Font = ThemeManager.MonoFont, Text = _pendingText,
            });
            _toolbar.BringToFront();
        }
    }

    private void Post(object message)
    {
        if (!_ready || _web.CoreWebView2 is null) return;
        try { _web.CoreWebView2.PostWebMessageAsString(JsonSerializer.Serialize(message)); }
        catch { /* torn down */ }
    }

    private async Task<string> GetTextAsync()
    {
        if (!_ready || _web.CoreWebView2 is null)
            return Controls.OfType<TextBox>().FirstOrDefault()?.Text ?? _pendingText;
        var json = await _web.CoreWebView2.ExecuteScriptAsync("window.getText()");
        return JsonSerializer.Deserialize<string>(json) ?? "";
    }

    private async Task SaveToFileAsync()
    {
        var text = await GetTextAsync();
        using var sfd = new SaveFileDialog { Filter = "SQL script (*.sql)|*.sql|Tất cả (*.*)|*.*", FileName = _defaultFileName };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(sfd.FileName, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
