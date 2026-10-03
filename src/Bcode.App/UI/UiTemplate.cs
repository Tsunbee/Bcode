using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bcode.App.UI;

/// <summary>Style khai báo cho MỘT loại control. Thuộc tính null = theo theme/mặc định (không ghi đè).</summary>
public sealed class ControlStyle
{
    public string? FontFamily { get; set; }
    /// <summary>Cỡ chữ (pt) lúc UiScale = 100%; hệ số UiScale được nhân thêm khi áp.</summary>
    public float? FontSize { get; set; }
    public bool? Bold { get; set; }
    /// <summary>Màu dạng "#RRGGBB" (áp cho cả theme Dark lẫn Light).</summary>
    public string? ForeColor { get; set; }
    public string? BackColor { get; set; }
    /// <summary>Chỉ dùng cho từng mục cụ thể (nút/nhãn trên thanh): chữ hiển thị thay cho chữ gốc. Null = giữ chữ gốc.</summary>
    public string? Text { get; set; }

    [JsonIgnore]
    public bool IsEmpty => FontFamily is null && FontSize is null && Bold is null && ForeColor is null && BackColor is null && string.IsNullOrEmpty(Text);

    public ControlStyle Clone() => (ControlStyle)MemberwiseClone();
}

/// <summary>
/// "Template giao diện" khai báo theo loại control (Label, Button, TextBox...): font gốc + style riêng từng loại, áp cho cả
/// WinForms (ThemeManager) lẫn các trang WebView2 (biến CSS --ui-*, --btn-*... — xem <see cref="ToCssVars"/> và shell.css).
/// Mặc định nằm cứng ở <see cref="CreateDefault"/>; người dùng ghi đè bằng file <c>%AppData%\Bcode\ui-template.json</c> hoặc màn hình
/// "Giao diện (Template)". Muốn thêm loại control mới: thêm tên vào <see cref="Kinds"/> + <see cref="KindOf"/> + chỗ áp ở ThemeManager.
/// </summary>
public sealed class UiTemplate
{
    public const string DefaultFontFamily = "Segoe UI";
    public const float DefaultFontSize = 9.5f;

    /// <summary>Các loại control có thể khai báo style riêng.</summary>
    public static readonly string[] Kinds = { "Label", "Button", "TextBox", "ComboBox", "CheckBox", "Grid", "Tree" };

    public string Name { get; set; } = "Mặc định";
    public string FontFamily { get; set; } = DefaultFontFamily;
    public float FontSize { get; set; } = DefaultFontSize;
    public Dictionary<string, ControlStyle> Controls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Style/chữ riêng từng mục cụ thể trên giao diện, khoá dạng "script:add", "tool:sql_query" (xem MainForm).</summary>
    public Dictionary<string, ControlStyle> Items { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Thứ tự các nút Script ở thanh trên (id: add/view/clear/save/copy). Rỗng = thứ tự mặc định.</summary>
    public List<string> ScriptOrder { get; set; } = new();

    /// <summary>Theme (id trong UiThemes) dùng cho ngăn Tối / ngăn Sáng; nút mặt trời/mặt trăng chuyển giữa hai ngăn.</summary>
    public string DarkTheme { get; set; } = UiThemes.DefaultDark;
    public string LightTheme { get; set; } = UiThemes.DefaultLight;

    /// <summary>Màu chỉnh tay chồng lên theme (khoá = tên ở ColorPalette.Keys, giá trị "#RRGGBB").</summary>
    public Dictionary<string, string> DarkColors { get; set; } = new();
    public Dictionary<string, string> LightColors { get; set; } = new();

    [JsonIgnore]
    public bool IsPaletteCustomized => DarkTheme != UiThemes.DefaultDark || LightTheme != UiThemes.DefaultLight || DarkColors.Count > 0 || LightColors.Count > 0;

    /// <summary>Các nút Script ở thanh trên (id, chữ mặc định) — nguồn khai báo duy nhất, topbar.html đặt data-script theo id này.</summary>
    public static readonly (string Id, string Text)[] ScriptButtons =
    {
        ("add", "Add Script"), ("view", "View Script"), ("clear", "Clear Script"), ("save", "Save Script"), ("copy", "Copy Script"),
    };

    /// <summary>Bản khai báo mặc định (hard-code): font Segoe UI 9,5pt, mọi loại control theo màu theme.</summary>
    public static UiTemplate CreateDefault()
    {
        var t = new UiTemplate();
        foreach (var k in Kinds) t.Controls[k] = new ControlStyle();
        return t;
    }

    public UiTemplate Clone()
    {
        var t = new UiTemplate { Name = Name, FontFamily = FontFamily, FontSize = FontSize };
        foreach (var (k, v) in Controls) t.Controls[k] = v.Clone();
        foreach (var (k, v) in Items) t.Items[k] = v.Clone();
        t.ScriptOrder = new List<string>(ScriptOrder);
        t.DarkTheme = DarkTheme; t.LightTheme = LightTheme;
        t.DarkColors = new(DarkColors); t.LightColors = new(LightColors);
        return t;
    }

    // ------------------------------------------------------------------ trạng thái toàn cục

    private static UiTemplate? _current;
    public static UiTemplate Current
    {
        get => _current ??= Load();
        set { _current = value; _fonts.Clear(); UiThemes.ApplyPalettes(); Changed?.Invoke(); }
    }

    /// <summary>Báo mỗi khi template đổi (áp lại WinForms + đẩy biến CSS xuống mọi WebView2).</summary>
    public static event Action? Changed;

    public static string FilePath => Path.Combine(BcodePaths.AppData, "Bcode", "ui-template.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Đọc file người dùng đè lên mặc định; file thiếu/hỏng thì dùng mặc định.</summary>
    public static UiTemplate Load()
    {
        var t = CreateDefault();
        try
        {
            if (!File.Exists(FilePath)) return t;
            var loaded = JsonSerializer.Deserialize<UiTemplate>(File.ReadAllText(FilePath), JsonOpts);
            if (loaded is null) return t;
            if (!string.IsNullOrWhiteSpace(loaded.Name)) t.Name = loaded.Name;
            if (!string.IsNullOrWhiteSpace(loaded.FontFamily)) t.FontFamily = loaded.FontFamily;
            if (loaded.FontSize is >= 6 and <= 40) t.FontSize = loaded.FontSize;
            foreach (var (k, v) in loaded.Controls) t.Controls[k] = v ?? new ControlStyle();
            foreach (var (k, v) in loaded.Items) t.Items[k] = v ?? new ControlStyle();
            t.ScriptOrder = loaded.ScriptOrder ?? new List<string>();
            if (!string.IsNullOrWhiteSpace(loaded.DarkTheme)) t.DarkTheme = loaded.DarkTheme;
            if (!string.IsNullOrWhiteSpace(loaded.LightTheme)) t.LightTheme = loaded.LightTheme;
            t.DarkColors = loaded.DarkColors ?? new(); t.LightColors = loaded.LightColors ?? new();
        }
        catch { /* file hỏng — giữ mặc định */ }
        return t;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts), new UTF8Encoding(false));
    }

    /// <summary>Nạp lại từ file rồi áp ngay (sau khi người dùng tự sửa JSON).</summary>
    public static void Reload() => Current = Load();

    // ------------------------------------------------------------------ áp cho WinForms

    private static readonly Dictionary<(string, float, bool), Font> _fonts = new();

    /// <summary>Font gốc toàn app (đã nhân UiScale).</summary>
    public static Font BuildBaseFont() => GetFont(Current.FontFamily, Current.FontSize * (float)UiScale.Factor, false);

    public static Font GetFont(string family, float size, bool bold)
    {
        var key = (family, size, bold);
        if (!_fonts.TryGetValue(key, out var f))
            _fonts[key] = f = new Font(family, size, bold ? FontStyle.Bold : FontStyle.Regular);
        return f;
    }

    public static string? KindOf(Control c) => c switch
    {
        DataGridView => "Grid",
        TreeView => "Tree",
        ComboBox => "ComboBox",
        CheckBox or RadioButton => "CheckBox",
        Button => "Button",
        TextBoxBase => "TextBox",
        Label => "Label",
        _ => null,
    };

    /// <summary>Style (không rỗng) áp cho <paramref name="c"/>, hoặc null nếu loại này không khai báo gì.</summary>
    public static ControlStyle? StyleFor(Control c)
        => KindOf(c) is { } kind && Current.Controls.TryGetValue(kind, out var s) && !s.IsEmpty ? s : null;

    public static Color? ParseColor(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return null;
        try { return ColorTranslator.FromHtml(hex.Trim()); } catch { return null; }
    }

    /// <summary>Font của style, lấy phần thiếu từ font gốc; null nếu style không đụng tới font.</summary>
    public static Font? FontOf(ControlStyle s)
    {
        if (s.FontFamily is null && s.FontSize is null && s.Bold is null) return null;
        return GetFont(string.IsNullOrWhiteSpace(s.FontFamily) ? Current.FontFamily : s.FontFamily,
            (s.FontSize ?? Current.FontSize) * (float)UiScale.Factor, s.Bold ?? false);
    }

    // ------------------------------------------------------------------ áp cho WebView2

    private static readonly string[] CssVarNames =
    {
        "--ui-font", "--ui-font-size",
        "--label-fg", "--label-font-size", "--label-weight",
        "--btn-fg", "--btn-bg", "--btn-font-size", "--btn-weight",
        "--input-fg", "--input-bg", "--input-font-size",
        "--select-fg", "--select-bg", "--select-font-size",
    };

    private static readonly string[] AllVarNames = CssVarNames.Concat(ColorPalette.Keys.Select(k => k.Css)).ToArray();

    private static string Px(float pt) => (pt * 13f / DefaultFontSize).ToString("0.##", CultureInfo.InvariantCulture) + "px";

    /// <summary>Biến CSS tương ứng (chữ web: 9,5pt ≙ 13px; UiScale đã do ZoomFactor lo nên không nhân lại).</summary>
    public static Dictionary<string, string> ToCssVars()
    {
        var v = new Dictionary<string, string>();
        var t = Current;
        if (!t.FontFamily.Equals(DefaultFontFamily, StringComparison.OrdinalIgnoreCase))
            v["--ui-font"] = $"\"{t.FontFamily}\", \"Segoe UI\", sans-serif";
        if (Math.Abs(t.FontSize - DefaultFontSize) > 0.01f) v["--ui-font-size"] = Px(t.FontSize);

        void Add(string kind, string prefix, bool bg, bool weight)
        {
            if (!t.Controls.TryGetValue(kind, out var s)) return;
            if (ParseColor(s.ForeColor) is { } fg) v[$"--{prefix}-fg"] = ColorTranslator.ToHtml(fg);
            if (bg && ParseColor(s.BackColor) is { } bgc) v[$"--{prefix}-bg"] = ColorTranslator.ToHtml(bgc);
            if (s.FontSize is { } sz) v[$"--{prefix}-font-size"] = Px(sz);
            if (weight && s.Bold is { } b) v[$"--{prefix}-weight"] = b ? "700" : "400";
        }
        // Bảng màu: chỉ đẩy khi người dùng đã đổi theme/màu — còn lại để shell.css tự lo (giống hệt trước đây).
        if (t.IsPaletteCustomized)
            foreach (var (key, css, _) in ColorPalette.Keys) v[css] = UiThemes.Hex(AppColors.Current.Get(key));
        Add("Label", "label", false, true);
        Add("Button", "btn", true, true);
        Add("TextBox", "input", true, false);
        Add("ComboBox", "select", true, false);
        return v;
    }

    /// <summary>Style một mục cụ thể → thuộc tính CSS inline (chữ pt → px như <see cref="ToCssVars"/>); chỉ gồm phần đã khai báo.</summary>
    public static Dictionary<string, string> ToInlineCss(ControlStyle? s)
    {
        var css = new Dictionary<string, string>();
        if (s is null) return css;
        if (!string.IsNullOrWhiteSpace(s.FontFamily)) css["fontFamily"] = $"\"{s.FontFamily}\", \"Segoe UI\", sans-serif";
        if (s.FontSize is { } sz) css["fontSize"] = Px(sz);
        if (s.Bold is { } b) css["fontWeight"] = b ? "700" : "400";
        if (ParseColor(s.ForeColor) is { } fg) css["color"] = ColorTranslator.ToHtml(fg);
        if (ParseColor(s.BackColor) is { } bg) css["background"] = ColorTranslator.ToHtml(bg);
        return css;
    }

    private static string BuildScript()
    {
        var vars = ToCssVars();
        var sb = new StringBuilder("(function(s){");
        foreach (var name in AllVarNames)
            sb.Append(vars.TryGetValue(name, out var val)
                ? $"s.setProperty('{name}',{JsonSerializer.Serialize(val)});"
                : $"s.removeProperty('{name}');");
        sb.Append("})(document.documentElement.style);");
        return sb.ToString();
    }

    /// <summary>Đẩy biến CSS vào trang sau mỗi lần điều hướng xong và mỗi lần template đổi.</summary>
    public static void BindWeb(Microsoft.Web.WebView2.WinForms.WebView2 web)
    {
        void Push()
        {
            try { if (!web.IsDisposed && web.CoreWebView2 is not null) _ = web.CoreWebView2.ExecuteScriptAsync(BuildScript()); }
            catch { /* WebView2 đang đóng */ }
        }
        Changed += Push;
        ThemeManager.ThemeChanged += Push; // đổi sáng/tối → bảng màu hiện hành đổi theo
        web.Disposed += (_, _) => { Changed -= Push; ThemeManager.ThemeChanged -= Push; };
        web.NavigationCompleted += (_, _) => Push();
        Push();
    }
}
