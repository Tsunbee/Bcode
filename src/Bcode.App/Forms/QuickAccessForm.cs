using System.Text.Json;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "Quick Access" — xếp công cụ của thanh công cụ vào 3 loại vùng bằng kéo thả (hoặc ▲ ▼ / ô "Chuyển tới"): <b>Ghim</b> (nút riêng trên thanh),
/// các <b>nhóm</b> do người dùng tạo (mỗi nhóm 1 nút "Tên ▾" thả menu — cho thanh ngắn lại) và <b>Ẩn</b>. Là 1 trang WebView2
/// (Web/Shell/quickaccess.html) nên tự co giãn theo cỡ cửa sổ. Kết quả: <see cref="HiddenKeys"/>, <see cref="Groups"/> và
/// <see cref="OrderedKeys"/> (thứ tự của TẤT CẢ nút: ghim → các nhóm → ẩn, để bật lại thì về đúng chỗ đã xếp).
/// </summary>
public class QuickAccessForm : ThemedForm
{
    private readonly List<(string key, string label)> _tools;
    private readonly HashSet<string> _initialHidden;
    private readonly List<string> _defaultOrder;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    public HashSet<string> HiddenKeys { get; private set; }
    public List<string> OrderedKeys { get; private set; }
    public List<Models.ToolBarGroup> Groups { get; private set; }
    private readonly List<Models.ToolBarGroup> _suggested;
    private readonly string? _title, _intro;
    private readonly Func<string, string> _toolShortcut;
    private readonly Func<int, string> _groupShortcut;

    /// <param name="allTools">Danh sách nút theo thứ tự HIỆN TẠI.</param>
    /// <param name="defaultOrder">Thứ tự mặc định (cho nút "Mặc định").</param>
    /// <param name="groups">Nhóm hiện tại (đã lọc); <paramref name="suggested"/> = nhóm gợi ý cho nút "Gợi ý nhóm".</param>
    public QuickAccessForm(IEnumerable<(string key, string label)> allTools, HashSet<string> hiddenKeys, IEnumerable<string> defaultOrder,
        List<Models.ToolBarGroup>? groups = null, List<Models.ToolBarGroup>? suggested = null,
        string? title = null, string? intro = null, Func<string, string>? toolShortcut = null, Func<int, string>? groupShortcut = null)
    {
        // Mặc định = thanh công cụ chính (tool:key, bar.groupN); thanh SQL Query truyền tiêu đề / phím riêng.
        _title = title;
        _intro = intro;
        _toolShortcut = toolShortcut ?? (k => ShortcutRegistry.Display("tool:" + k));
        _groupShortcut = groupShortcut ?? (i => ShortcutRegistry.Display($"bar.group{i}"));
        Groups = groups ?? new List<Models.ToolBarGroup>();
        _suggested = suggested ?? new List<Models.ToolBarGroup>();
        _tools = allTools.ToList();
        _initialHidden = new HashSet<string>(hiddenKeys);
        _defaultOrder = defaultOrder.ToList();
        HiddenKeys = new HashSet<string>(hiddenKeys);
        OrderedKeys = _tools.Select(t => t.key).ToList();

        Text = title ?? "Quick Access";
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        Width = 760;
        Height = 760;
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
            _web.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("quickaccess.html"));
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
                        items = _tools.Select(t => new { key = t.key, label = t.label, visible = !_initialHidden.Contains(t.key),
                            shortcut = _toolShortcut(t.key) }),
                        defaults = _defaultOrder,
                        groups = Groups.Select((g, i) => new { name = g.Name, keys = g.Keys }),
                        suggested = _suggested.Select(g => new { name = g.Name, keys = g.Keys }),
                        groupShortcuts = Enumerable.Range(1, 9).Select(_groupShortcut),
                        title = _title,
                        intro = _intro,
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
        var groups = new List<Models.ToolBarGroup>();
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("groups", out var garr) && garr.ValueKind == JsonValueKind.Array)
            foreach (var g in garr.EnumerateArray())
            {
                var name = g.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
                if (name.Length == 0) continue;
                var keys = g.TryGetProperty("keys", out var ks) && ks.ValueKind == JsonValueKind.Array
                    ? ks.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
                groups.Add(new Models.ToolBarGroup { Name = name, Keys = keys });
            }
        Groups = groups;
    }

    private Task Js(string script) =>
        IsDisposed || _web.CoreWebView2 is null ? Task.CompletedTask : _web.CoreWebView2.ExecuteScriptAsync(script);

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme({(AppColors.IsDark ? "true" : "false")})");
    }
}
