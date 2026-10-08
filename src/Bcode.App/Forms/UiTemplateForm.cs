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
    /// <summary>true khi form đã làm đổi template thật (Áp dụng / Mặc định / Nạp lại) — chưa đổi gì thì đóng form không cần "trả lại" (trả lại = áp lại cả giao diện → giật).</summary>
    private bool _changed;

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
        public string HistoryPath { get; set; } = "";
        public Bcode.App.UI.TreeColorOptions? TreeColors { get; set; }
        public string Density { get; set; } = "normal";
        public int CornerRadius { get; set; } = UiTemplate.DefaultCornerRadius;
        public int BorderWidth { get; set; } = 1;
        public Dictionary<string, ControlStyle> Areas { get; set; } = new();
        public bool CustomUiEnabled { get; set; } = true;
        public bool CustomUiDesignMode { get; set; }
        /// <summary>Phím tắt khai báo lại: id → tổ hợp ("" = tắt phím).</summary>
        public Dictionary<string, string> Shortcuts { get; set; } = new();
        // Bố cục cửa sổ
        public string TreeSide { get; set; } = "left";
        public bool TreeStartHidden { get; set; }
        public int TreeWidth { get; set; } = UiTemplate.DefaultTreeWidth;
        public bool TabsAtBottom { get; set; }
        public bool ToolbarWrap { get; set; } = true;
        public bool KeepMousePointer { get; set; } = true;
        // Editor SQL
        public double EditorLineSpacing { get; set; }
        public bool EditorMinimap { get; set; }
        public bool EditorLineNumbers { get; set; } = true;
        public bool EditorWhitespace { get; set; }
        // Lưới kết quả
        public bool ResultStripe { get; set; } = true;
        public string? ResultStripeColor { get; set; }
        public string? ResultSelColor { get; set; }
        public string ResultNullStyle { get; set; } = "italic";
        public string ResultGridLines { get; set; } = "both";
    }

    /// <param name="tools">Các nút công cụ theo thứ tự hiện tại (key, chữ).</param>
    /// <param name="defaultToolKeys">Thứ tự mặc định của các nút công cụ — thứ tự lưu trùng cái này thì lưu rỗng.</param>
    private readonly IReadOnlyList<(string Code, string Name)> _wcGroups;

    public UiTemplateForm(AppSettings settings, IReadOnlyList<(string key, string label)> tools, IReadOnlyList<string> defaultToolKeys,
        IReadOnlyList<(string Code, string Name)>? wcGroups = null)
    {
        _settings = settings;
        _wcGroups = wcGroups ?? Array.Empty<(string Code, string Name)>();
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

        FormClosed += (_, _) => { Bcode.App.UI.WebViewEnvironment.SuspendAccelerators = false; if (!_saved) Revert(); };
        Load += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("uitemplate.html"));
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
                    _changed = true;
                    UiTemplate.Current = UiTemplate.CreateDefault();
                    _settings.ToolOrder = new List<string>();
                    PushState();
                    break;
                case "reload":
                    _changed = true;
                    UiTemplate.Reload();
                    PushState();
                    break;
                case "open-json": OpenJson(ReadDraft(data)); break;
                case "ui-list": PushUiList(); break;
                case "ui-load": UiLoad(data); break;
                case "ui-save": UiSave(data); break;
                case "ui-reset": UiReset(data); break;
                case "ui-export-new": UiExportNew(data); break;
                case "ui-open-folder":
                    Directory.CreateDirectory(UiOverrides.Folder);
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{UiOverrides.Folder}\"") { UseShellExecute = true });
                    break;
                case "import-theme": BeginInvoke(new Action(ImportTheme)); break;
                case "browse-history":
                {
                    var histCurrent = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("path", out var pe) ? pe.GetString() : null; // lấy ra trước khi JsonDocument bị dispose
                    BeginInvoke(new Action(() => BrowseHistory(histCurrent)));
                    break;
                }
                case "open-history":
                {
                    var dir = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("path", out var op) && !string.IsNullOrWhiteSpace(op.GetString())
                        ? op.GetString()!.Trim() : Bcode.App.Services.SqlHistoryService.RootFor(_settings.Workspaces.FirstOrDefault(w => w.Name == _settings.LastWorkspace));
                    try { Directory.CreateDirectory(dir); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true }); }
                    catch (Exception ex) { Js($"window.setStatus({JsonSerializer.Serialize("Không mở được thư mục: " + ex.Message)}, 'err')"); }
                    break;
                }
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
        t.HistoryPath = (d.HistoryPath ?? "").Trim();
        t.TreeColors = (d.TreeColors ?? new Bcode.App.UI.TreeColorOptions()).Clean();
        if (UiTemplate.Densities.Any(x => x.Id == d.Density)) t.Density = d.Density;
        t.CornerRadius = Math.Clamp(d.CornerRadius, 0, 24);
        t.BorderWidth = Math.Clamp(d.BorderWidth, 1, 4);
        foreach (var (k, v) in d.Areas) if (v is not null && UiTemplate.AreaList.Any(a => a.Id == k)) t.Areas[k] = v;
        t.Shortcuts = ShortcutRegistry.CleanOverrides(d.Shortcuts);
        t.Normalize(new UiTemplate
        {
            Shortcuts = t.Shortcuts, CustomUiEnabled = d.CustomUiEnabled, CustomUiDesignMode = d.CustomUiDesignMode,
            TreeSide = d.TreeSide, TreeStartHidden = d.TreeStartHidden, TreeWidth = d.TreeWidth, TabsAtBottom = d.TabsAtBottom, ToolbarWrap = d.ToolbarWrap, KeepMousePointer = d.KeepMousePointer,
            EditorLineSpacing = d.EditorLineSpacing, EditorMinimap = d.EditorMinimap, EditorLineNumbers = d.EditorLineNumbers, EditorWhitespace = d.EditorWhitespace,
            ResultStripe = d.ResultStripe, ResultStripeColor = d.ResultStripeColor, ResultSelColor = d.ResultSelColor,
            ResultNullStyle = d.ResultNullStyle, ResultGridLines = d.ResultGridLines,
        });
        return t;
    }

    private static Dictionary<string, string> Valid(Dictionary<string, string>? colors) =>
        (colors ?? new()).Where(kv => UiTemplate.ParseColor(kv.Value) is not null && ColorPalette.Keys.Any(k => k.Key == kv.Key))
                         .ToDictionary(kv => kv.Key, kv => kv.Value);

    private void Apply(Draft d)
    {
        _settings.ToolOrder = d.ToolOrder.SequenceEqual(_defaultToolKeys) ? new List<string>() : d.ToolOrder;
        _settings.HiddenToolKeys = d.ToolHidden;
        var wasEnabled = UiTemplate.Current.CustomUiEnabled;
        _changed = true;
        UiTemplate.Current = BuildTemplate(d); // báo Changed → MainForm dựng lại thanh công cụ + thanh trên, ThemeManager áp lại font
        // Bật / tắt tuỳ chỉnh giao diện web: mọi trang đang mở nạp lại theo bản gốc hoặc bản tuỳ chỉnh (trang này được bảo vệ nên không bị nạp lại).
        if (wasEnabled != UiTemplate.Current.CustomUiEnabled) UiOverrides.RaisePageChanged("*");
        // Đổi thanh tab trên/dưới chỉ áp được khi không còn tab nào mở (đổi hướng TabControl tạo lại cửa sổ của mọi tab).
        var tabsPending = Application.OpenForms.OfType<MainForm>().FirstOrDefault()?.TabsPositionPending == true;
        Js(tabsPending
            ? "window.setStatus('Đã áp dụng — vị trí thanh tab sẽ đổi khi đóng hết tab (hoặc lần mở Bcode sau). Bấm Lưu để giữ lại.', 'ok')"
            : "window.setStatus('Đã áp dụng — bấm Lưu để giữ lại.', 'ok')");
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
        if (!_changed) return; // mở xem rồi đóng, không Áp dụng gì → giữ nguyên, không áp lại giao diện
        _settings.ToolOrder = _originalToolOrder;
        _settings.HiddenToolKeys = _originalHidden;
        UiTemplate.Current = _originalTemplate;
    }

    private static object[] ThemeList() => UiThemes.All.Select(t => (object)new
    {
        id = t.Id, name = t.Name, dark = t.IsDark, source = t.Source, palette = UiThemes.ToHex(t.Palette),
    }).ToArray();

    private void BrowseHistory(string? current)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Chọn thư mục lưu lịch sử sửa procedure / function (có thể là ổ mạng dùng chung)",
            UseDescriptionForTitle = true,
            InitialDirectory = !string.IsNullOrWhiteSpace(current) && Directory.Exists(current) ? current : "",
        };
        if (dlg.ShowDialog(this) == DialogResult.OK) Js($"window.setHistoryPath({JsonSerializer.Serialize(dlg.SelectedPath)})");
    }

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
            treeColors = t.TreeColors,
            wcGroups = _wcGroups.Select(g => new { code = g.Code, name = g.Name }).ToArray(),
            history = new { path = t.HistoryPath, defaultPath = @"{Source Path của dự án}\History  (dự án chưa có Source/Program Path thì dùng " + Bcode.App.Services.SqlHistoryService.DefaultRoot + ")" },
            structure = new
            {
                density = t.Density,
                densities = UiTemplate.Densities.Select(x => new { id = x.Id, text = x.Text, factor = x.Factor }).ToArray(),
                radius = t.CornerRadius,
                border = t.BorderWidth,
                defaultRadius = UiTemplate.DefaultCornerRadius,
                areas = t.Areas,
                areaList = UiTemplate.AreaList.Select(a => new { id = a.Id, text = a.Text, hint = a.Hint }).ToArray(),
            },
            shortcuts = ShortcutRegistry.All.Select(s => new
            {
                id = s.Id, text = s.Text, group = s.Group, scope = s.Scope == ShortcutScope.App ? "app" : s.Scope == ShortcutScope.Grid ? "grid" : "editor",
                def = s.Default, cur = ShortcutRegistry.Get(s.Id),
            }).ToArray(),
            customUi = new { enabled = t.CustomUiEnabled, design = t.CustomUiDesignMode, sessionSafe = UiOverrides.SessionSafe },
            extra = new
            {
                treeSide = t.TreeSide, treeStartHidden = t.TreeStartHidden, treeWidth = t.TreeWidth, defaultTreeWidth = UiTemplate.DefaultTreeWidth,
                tabsAtBottom = t.TabsAtBottom, toolbarWrap = t.ToolbarWrap, keepMousePointer = t.KeepMousePointer,
                editorLineSpacing = t.EditorLineSpacing, editorMinimap = t.EditorMinimap, editorLineNumbers = t.EditorLineNumbers, editorWhitespace = t.EditorWhitespace,
                resultStripe = t.ResultStripe, resultStripeColor = t.ResultStripeColor, resultSelColor = t.ResultSelColor,
                resultNullStyle = t.ResultNullStyle, resultGridLines = t.ResultGridLines,
            },
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

    // ---------------------------------------------------------------- Tuỳ chỉnh HTML & CSS (UiOverrides)

    private static string Str(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private void PushUiList()
    {
        var state = new
        {
            folder = UiOverrides.Folder,
            sessionSafe = UiOverrides.SessionSafe,
            hasGlobal = UiOverrides.GlobalCss.Trim().Length > 0,
            pages = UiOverrides.ListPages().Select(p => new { page = p.Page, title = p.Title, hasCss = p.HasCss, hasHtml = p.HasHtml, quarantined = p.Quarantined, stale = p.Stale, hasPatches = p.HasPatches }),
        };
        Js($"window.uiCustom && window.uiCustom.onList({JsonSerializer.Serialize(state, Web)})");
    }

    /// <summary>kind: "global" | "css" | "html". html: tạo bản ghi đè từ bản gốc nếu chưa có.</summary>
    private void UiLoad(JsonElement data)
    {
        var kind = Str(data, "kind"); var page = Str(data, "page");
        string text;
        try
        {
            text = kind switch
            {
                "global" => UiOverrides.GlobalCss,
                "css" => UiOverrides.PageCss(page),
                "html" => UiOverrides.ExportOverride(page),
                "patch" => UiOverrides.PagePatches(page) is { Length: > 0 } pj ? pj : "[]",
                _ => throw new InvalidOperationException("Loại không hợp lệ."),
            };
        }
        catch (Exception ex) { Js($"window.uiCustom.onSaved(false, {JsonSerializer.Serialize(ex.Message)})"); return; }
        Js($"window.uiCustom.onText({JsonSerializer.Serialize(kind)}, {JsonSerializer.Serialize(page)}, {JsonSerializer.Serialize(text)})");
        if (kind == "html") PushUiList(); // vừa tạo bản ghi đè → cập nhật trạng thái trong danh sách
    }

    private void UiSave(JsonElement data)
    {
        var kind = Str(data, "kind"); var page = Str(data, "page"); var text = Str(data, "text");
        if (kind == "patch")
        {
            var perr = UiOverrides.SavePatches(page, text);
            if (perr is not null) { Js($"window.uiCustom.onSaved(false, {JsonSerializer.Serialize(perr)})"); return; }
            Js("window.uiCustom.onSaved(true, 'Đã lưu bản vá cấu trúc — các trang đang mở đã nạp lại.')");
        }
        else if (kind == "html")
        {
            var problems = UiOverrides.SaveHtml(page, text);
            if (problems.Count > 0) { Js($"window.uiCustom.onSaved(false, {JsonSerializer.Serialize(string.Join("\n", problems))})"); return; }
            Js("window.uiCustom.onSaved(true, 'Đã lưu bản HTML tuỳ chỉnh — các trang đang mở đã nạp lại.')");
        }
        else
        {
            var err = UiOverrides.SaveCss(kind == "global" ? "*" : page, text);
            if (err is not null) { Js($"window.uiCustom.onSaved(false, {JsonSerializer.Serialize(err)})"); return; }
            Js("window.uiCustom.onSaved(true, 'Đã lưu CSS — áp dụng ngay trên các trang đang mở.')");
        }
        PushUiList();
    }

    private void UiReset(JsonElement data)
    {
        var page = Str(data, "page"); var what = Str(data, "what");
        if (what == "global") UiOverrides.SaveCss("*", "");
        else UiOverrides.Reset(page, html: what is "html" or "both", css: what is "css" or "both", patches: what is "patch" or "both");
        PushUiList();
        Js("window.uiCustom.onReset()");
    }

    private void UiExportNew(JsonElement data)
    {
        try
        {
            var path = UiOverrides.ExportNewOriginal(Str(data, "page"));
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            Js("window.uiCustom.onSaved(true, 'Đã xuất bản gốc mới ra file .new.html cạnh bản tuỳ chỉnh — so sánh (Compare Text) rồi hợp nhất tay.')");
            PushUiList();
        }
        catch (Exception ex) { Js($"window.uiCustom.onSaved(false, {JsonSerializer.Serialize(ex.Message)})"); }
    }

    private void Js(string script)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync(script);
    }
}
