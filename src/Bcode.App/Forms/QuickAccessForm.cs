using System.Text.Json;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "Quick Access" — chọn nút nào hiện trên thanh công cụ VÀ đổi vị trí các nút (kéo thả dòng hoặc bấm ▲ ▼). Là 1 trang WebView2
/// (Web/Shell/quickaccess.html) nên tự co giãn theo cỡ cửa sổ. Kết quả: <see cref="HiddenKeys"/> (nút bị ẩn) và
/// <see cref="OrderedKeys"/> (thứ tự trái → phải của TẤT CẢ nút, kể cả nút đang ẩn, để bật lại thì về đúng chỗ đã xếp).
/// </summary>
public class QuickAccessForm : ThemedForm
{
    private readonly List<(string key, string label)> _tools;
    private readonly HashSet<string> _initialHidden;
    private readonly List<string> _defaultOrder;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public HashSet<string> HiddenKeys { get; private set; }
    public List<string> OrderedKeys { get; private set; }

    /// <param name="allTools">Danh sách nút theo thứ tự HIỆN TẠI.</param>
    /// <param name="defaultOrder">Thứ tự mặc định (cho nút "Mặc định").</param>
    public QuickAccessForm(IEnumerable<(string key, string label)> allTools, HashSet<string> hiddenKeys, IEnumerable<string> defaultOrder)
    {
        _tools = allTools.ToList();
        _initialHidden = new HashSet<string>(hiddenKeys);
        _defaultOrder = defaultOrder.ToList();
        HiddenKeys = new HashSet<string>(hiddenKeys);
        OrderedKeys = _tools.Select(t => t.key).ToList();

        Text = "Quick Access";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        Width = 520;
        Height = 700;
        MinimumSize = new Size(340, 420);
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
            _web.CoreWebView2.Navigate($"https://{WebViewEnvironment.Host}/quickaccess.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Quick Access", MessageBoxButtons.OK, MessageBoxIcon.Error);
            DialogResult = DialogResult.Cancel;
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
                case "ready":
                    PushTheme();
                    var state = new
                    {
                        items = _tools.Select(t => new { key = t.key, label = t.label, visible = !_initialHidden.Contains(t.key) }),
                        defaults = _defaultOrder,
                    };
                    await Js($"window.init({JsonSerializer.Serialize(state)})");
                    break;
                case "ok":
                    ReadResult(data);
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
                case "cancel":
                    DialogResult = DialogResult.Cancel;
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Quick Access", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ReadResult(JsonElement data)
    {
        var order = new List<string>();
        var hidden = new HashSet<string>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var it in arr.EnumerateArray())
            {
                var key = it.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                if (key.Length == 0) continue;
                order.Add(key);
                if (it.TryGetProperty("visible", out var v) && v.ValueKind == JsonValueKind.False) hidden.Add(key);
            }
        }
        if (order.Count > 0) { OrderedKeys = order; HiddenKeys = hidden; }
    }

    private Task Js(string script) =>
        IsDisposed || _web.CoreWebView2 is null ? Task.CompletedTask : _web.CoreWebView2.ExecuteScriptAsync(script);

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }
}
