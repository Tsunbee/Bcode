using System.Text.Json;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "Lịch sử gợi ý AI" — danh sách mọi đoạn AI đã gợi ý trong SQL Query (ghost text và hộp Ctrl+I), mới nhất trước, để lấy lại
/// khi lỡ tay tắt/bỏ qua: xem, lọc, Copy, hoặc chèn thẳng vào vị trí con trỏ của editor. Giao diện là trang WebView2
/// (Web/Shell/aihistory.html) tự co giãn theo cửa sổ; dữ liệu nằm ở <see cref="AiHistoryStore"/>.
/// </summary>
public class AiHistoryForm : ThemedForm
{
    private readonly Func<string, Task> _insert;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    /// <param name="insert">Chèn văn bản vào editor SQL Query tại con trỏ.</param>
    public AiHistoryForm(Func<string, Task> insert)
    {
        _insert = insert;
        Text = "Lịch sử gợi ý AI";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        Width = 960;
        Height = 640;
        MinimumSize = new Size(420, 420);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        Controls.Add(_web);

        ThemeManager.ThemeChanged += PushTheme;
        FormClosed += (_, _) => ThemeManager.ThemeChanged -= PushTheme;
        Load += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("aihistory.html"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Lịch sử gợi ý AI", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var action = doc.RootElement.GetProperty("action").GetString();
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;

            switch (action)
            {
                case "ready": PushTheme(); await PushItemsAsync(); break;
                case "copy": Clipboard.SetText(Str(data, "text")); break;
                case "insert": await _insert(Str(data, "text")); break;
                case "delete": AiHistoryStore.Remove(Str(data, "id")); await PushItemsAsync(); break;
                case "clear":
                    if (MessageBox.Show(this, "Xoá toàn bộ lịch sử gợi ý AI?", "Bcode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    {
                        AiHistoryStore.Clear();
                        await PushItemsAsync();
                    }
                    break;
                case "close": Close(); break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Lịch sử gợi ý AI", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task PushItemsAsync()
    {
        var items = AiHistoryStore.All().Select(h => new
        {
            id = h.Id, time = h.Time.ToString("o"), kind = h.Kind, engine = h.Engine,
            instruction = h.Instruction, context = h.Context, result = h.Result,
        });
        if (_web.CoreWebView2 is not null)
            await _web.CoreWebView2.ExecuteScriptAsync($"window.setItems({JsonSerializer.Serialize(items)})");
    }

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }
}
