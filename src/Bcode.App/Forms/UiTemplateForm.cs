using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "Giao diện (Template)" — màn hình WebView2 (Web/Shell/uitemplate.html) hiện các nhãn/nút đang có trên chương trình, chia sẵn
/// thành các khung (Thanh trên - Script, Thanh công cụ, Ẩn): kéo thả để đổi vị trí, bấm một mục để đổi chữ/font/màu; ngoài ra khai
/// font gốc và style theo loại control. C# giữ phần ghi/áp dụng, trang chỉ chỉnh bản nháp rồi gửi <c>{action, data}</c>:
/// apply (xem thử ngay trên app) / save / defaults / reload / open-json / close (hủy = trả về như lúc mở).
/// </summary>
public class UiTemplateForm : ThemedForm
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly AppSettings _settings;
    private readonly IReadOnlyList<(string key, string label)> _tools;
    private readonly IReadOnlyList<string> _defaultToolKeys;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    // Trạng thái lúc mở, để Hủy trả về.
    private readonly UiTemplate _originalTemplate = UiTemplate.Current;
    private readonly List<string> _originalToolOrder;
    private readonly List<string> _originalHidden;
    private bool _saved;

    /// <summary>Bản nháp do trang gửi lên.</summary>
    private sealed class Draft
    {
        public string FontFamily { get; set; } = UiTemplate.DefaultFontFamily;
        public float FontSize { get; set; } = UiTemplate.DefaultFontSize;
        public Dictionary<string, ControlStyle> Kinds { get; set; } = new();
        public Dictionary<string, ControlStyle> Items { get; set; } = new();
        public List<string> ScriptOrder { get; set; } = new();
        public List<string> ToolOrder { get; set; } = new();
        public List<string> ToolHidden { get; set; } = new();
        public string DarkTheme { get; set; } = UiThemes.DefaultDark;
        public string LightTheme { get; set; } = UiThemes.DefaultLight;
        public Dictionary<string, string> DarkColors { get; set; } = new();
        public Dictionary<string, string> LightColors { get; set; } = new();
    }

    /// <param name="tools">Các nút công cụ theo thứ tự hiện tại (key, chữ).</param>
    /// <param name="defaultToolKeys">Thứ tự mặc định của các nút công cụ — thứ tự lưu trùng cái này thì lưu rỗng.</param>
    public UiTemplateForm(AppSettings settings, IReadOnlyList<(string key, string label)> tools, IReadOnlyList<string> defaultToolKeys)
    {
        _settings = settings;
        _tools = tools;
        _defaultToolKeys = defaultToolKeys;
        _originalToolOrder = new List<string>(settings.ToolOrder);
        _originalHidden = new List<string>(settings.HiddenToolKeys);

        Text = "Giao diện (Template)";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ClientSize = new Size(1040, 700);
        MinimumSize = new Size(760, 520);
        ShowIcon = false;
        Controls.Add(_web);

        FormClosed += (_, _) => { if (!_saved) Revert(); };
        Load += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate($"https://{WebViewEnvironment.Host}/uitemplate.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var action = doc.RootElement.GetProperty("action").GetString();
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;
            switch (action)
            {
                case "ready": PushState(); break;
                case "apply": Apply(ReadDraft(data)); break;
                case "save": Save(ReadDraft(data)); break;
                case "defaults":
                    UiTemplate.Current = UiTemplate.CreateDefault();
                    _settings.ToolOrder = new List<string>();
                    PushState();
                    break;
                case "reload":
                    UiTemplate.Reload();
                    PushState();
                    break;
                case "open-json": OpenJson(ReadDraft(data)); break;
                case "import-theme": BeginInvoke(new Action(ImportTheme)); break;
                case "delete-theme": DeleteTheme(data.TryGetProperty("id", out var idEl) ? idEl.GetString() : null); break;
                case "open-themes":
                    Directory.CreateDirectory(UiThemes.Folder);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{UiThemes.Folder}\"") { UseShellExecute = true });
                    break;
                case "close": Close(); break;
            }
        }
        catch (Exception ex)
        {
            Js($"window.setStatus({JsonSerializer.Serialize(ex.Message)}, 'err')");
        }
    }

    private static Draft ReadDraft(JsonElement data) => data.ValueKind == JsonValueKind.Object
        ? JsonSerializer.Deserialize<Draft>(data.GetRawText(), Web) ?? new Draft()
        : new Draft();

    // ---------------------------------------------------------------- áp dụng / lưu

    private UiTemplate BuildTemplate(Draft d)
    {
        var t = UiTemplate.CreateDefault();
        if (!string.IsNullOrWhiteSpace(d.FontFamily)) t.FontFamily = d.FontFamily;
        if (d.FontSize is >= 6 and <= 40) t.FontSize = d.FontSize;
        foreach (var (k, v) in d.Kinds) if (v is not null) t.Controls[k] = v;
        foreach (var (k, v) in d.Items) if (v is { IsEmpty: false }) t.Items[k] = v;
        // Thứ tự mặc định thì lưu rỗng để file JSON chỉ chứa phần người dùng đã đổi.
        var defaultScript = UiTemplate.ScriptButtons.Select(b => b.Id).ToList();
        t.ScriptOrder = d.ScriptOrder.SequenceEqual(defaultScript) ? new List<string>() : d.ScriptOrder;
        t.DarkTheme = d.DarkTheme; t.LightTheme = d.LightTheme;
        t.DarkColors = Valid(d.DarkColors); t.LightColors = Valid(d.LightColors);
        return t;
    }

    private static Dictionary<string, string> Valid(Dictionary<string, string>? colors) =>
        (colors ?? new()).Where(kv => UiTemplate.ParseColor(kv.Value) is not null && ColorPalette.Keys.Any(k => k.Key == kv.Key))
                         .ToDictionary(kv => kv.Key, kv => kv.Value);

    private void Apply(Draft d)
    {
        _settings.ToolOrder = d.ToolOrder.SequenceEqual(_defaultToolKeys) ? new List<string>() : d.ToolOrder;
        _settings.HiddenToolKeys = d.ToolHidden;
        UiTemplate.Current = BuildTemplate(d); // báo Changed → MainForm dựng lại thanh công cụ + thanh trên, ThemeManager áp lại font
        Js("window.setStatus('Đã áp dụng — bấm Lưu để giữ lại.', 'ok')");
    }

    private void Save(Draft d)
    {
        try
        {
            Apply(d);
            UiTemplate.Current.Save();
            _settings.Save();
            _saved = true;
            Close();
        }
        catch (Exception ex)
        {
            Js($"window.setStatus({JsonSerializer.Serialize("Không lưu được: " + ex.Message)}, 'err')");
        }
    }

    /// <summary>Đóng mà không Lưu → trả cả template lẫn thứ tự/ẩn công cụ về lúc mở.</summary>
    private void Revert()
    {
        _settings.ToolOrder = _originalToolOrder;
        _settings.HiddenToolKeys = _originalHidden;
        UiTemplate.Current = _originalTemplate;
    }

    private static object[] ThemeList() => UiThemes.All.Select(t => (object)new
    {
        id = t.Id, name = t.Name, dark = t.IsDark, source = t.Source, palette = UiThemes.ToHex(t.Palette),
    }).ToArray();

    private void ImportTheme()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "Chọn theme VS Code",
            Filter = "VS Code theme (*.json;*.vsix)|*.json;*.vsix|Tất cả (*.*)|*.*",
        };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var ids = UiThemes.Import(ofd.FileName);
            if (ids.Count == 0) { Js("window.setStatus('File không có theme nào dùng được.', 'err')"); return; }
            Js($"window.setThemes({JsonSerializer.Serialize(ThemeList(), Web)}, {JsonSerializer.Serialize(ids[0])}); window.setStatus({JsonSerializer.Serialize($"Đã nhập {ids.Count} theme — bấm Áp dụng để dùng.")}, 'ok')");
        }
        catch (Exception ex) { Js($"window.setStatus({JsonSerializer.Serialize("Không nhập được theme: " + ex.Message)}, 'err')"); }
    }

    private void DeleteTheme(string? id)
    {
        if (string.IsNullOrEmpty(id)) return;
        if (UiThemes.Find(id) is not { Source: "imported" } theme) { Js("window.setStatus('Chỉ xóa được theme đã nhập.', 'err')"); return; }
        if (MessageBox.Show(this, $"Xóa theme \"{theme.Name}\"?", "Bcode", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            UiThemes.Delete(id);
            Js($"window.setThemes({JsonSerializer.Serialize(ThemeList(), Web)}, {JsonSerializer.Serialize(theme.IsDark ? UiThemes.DefaultDark : UiThemes.DefaultLight)})");
        }
        catch (Exception ex) { Js($"window.setStatus({JsonSerializer.Serialize(ex.Message)}, 'err')"); }
    }

    private void OpenJson(Draft d)
    {
        try
        {
            if (!File.Exists(UiTemplate.FilePath)) BuildTemplate(d).Save(); // tạo sẵn file mẫu để người dùng sửa tay
            Process.Start(new ProcessStartInfo(UiTemplate.FilePath) { UseShellExecute = true });
            Js("window.setStatus('Sửa xong, lưu file rồi bấm \"Nạp lại từ file\".')");
        }
        catch (Exception ex) { Js($"window.setStatus({JsonSerializer.Serialize(ex.Message)}, 'err')"); }
    }

    // ---------------------------------------------------------------- trạng thái gửi cho trang

    private void PushState()
    {
        var t = UiTemplate.Current;
        var hidden = new HashSet<string>(_settings.HiddenToolKeys);

        var scriptIds = t.ScriptOrder.Where(id => UiTemplate.ScriptButtons.Any(b => b.Id == id)).ToList();
        scriptIds.AddRange(UiTemplate.ScriptButtons.Select(b => b.Id).Where(id => !scriptIds.Contains(id)));

        object Item(string id, string text) => new { id, text };
        var state = new
        {
            fonts = new System.Drawing.Text.InstalledFontCollection().Families.Select(f => f.Name).ToArray(),
            defaultFont = new { family = UiTemplate.DefaultFontFamily, size = UiTemplate.DefaultFontSize },
            global = new { fontFamily = t.FontFamily, fontSize = t.FontSize },
            kindNames = UiTemplate.Kinds,
            activeDark = AppColors.IsDark,
            themes = ThemeList(),
            paletteKeys = ColorPalette.Keys.Select(k => new { key = k.Key, label = k.Label }).ToArray(),
            theme = new { dark = t.DarkTheme, light = t.LightTheme },
            colors = new { dark = t.DarkColors, light = t.LightColors },
            kinds = t.Controls,
            items = t.Items,
            zones = new object[]
            {
                new { id = "script", title = "Thanh trên — Script", hint = "Kéo để đổi thứ tự", prefix = "script:",
                      items = scriptIds.Select(id => Item(id, UiTemplate.ScriptButtons.First(b => b.Id == id).Text)).ToArray() },
                new { id = "tools", title = "Thanh công cụ", hint = "Kéo để đổi thứ tự; kéo xuống khung \"Ẩn\" để giấu", prefix = "tool:",
                      items = _tools.Where(x => !hidden.Contains(x.key)).Select(x => Item(x.key, x.label)).ToArray() },
                new { id = "hidden", title = "Ẩn khỏi thanh công cụ", hint = "Kéo ngược lên khung \"Thanh công cụ\" để hiện lại", prefix = "tool:",
                      items = _tools.Where(x => hidden.Contains(x.key)).Select(x => Item(x.key, x.label)).ToArray() },
            },
        };
        Js($"window.init({JsonSerializer.Serialize(state, Web)}, {(AppColors.IsDark ? "true" : "false")})");
    }

    private void Js(string script)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync(script);
    }
}
