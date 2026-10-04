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

    /// <summary>Mật độ giao diện: "compact" (gọn), "normal" (vừa — mặc định), "comfortable" (thoáng). Co/giãn chiều cao dòng lưới, dòng cây,
    /// tab, nút và khoảng đệm — cả WinForms lẫn các trang WebView2 (biến CSS --d).</summary>
    public string Density { get; set; } = "normal";

    /// <summary>Bán kính bo góc (px) của nút, ô nhập, thẻ... — mặc định 6.</summary>
    public int CornerRadius { get; set; } = DefaultCornerRadius;

    /// <summary>Độ dày đường viền (px) của nút, ô nhập, thẻ... — mặc định 1.</summary>
    public int BorderWidth { get; set; } = 1;

    /// <summary>Style riêng từng KHU VỰC của chương trình (khoá = id trong <see cref="AreaList"/>): font/cỡ/đậm/màu chữ/màu nền.
    /// Cụ thể hơn "theo loại control" nên ghi đè lên nó trong khu vực đó.</summary>
    public Dictionary<string, ControlStyle> Areas { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public const int DefaultCornerRadius = 6;

    /// <summary>Cho phép tuỳ chỉnh giao diện web (CSS riêng + HTML ghi đè — xem <see cref="UiOverrides"/>). Tắt = chế độ an toàn: mọi trang dùng bản gốc.</summary>
    public bool CustomUiEnabled { get; set; } = true;
    /// <summary>Chế độ thiết kế: hiện nút ✎ trên mỗi trang web để chọn phần tử và đổi style trực quan (lưu thành CSS của trang).</summary>
    public bool CustomUiDesignMode { get; set; }

    /// <summary>Phím tắt người dùng khai báo lại: id chức năng (xem <see cref="ShortcutRegistry"/>) → tổ hợp "Ctrl+Shift+Q"; chuỗi rỗng = tắt phím đó.
    /// Chức năng không có mặt ở đây dùng phím mặc định.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    // ---- Bố cục cửa sổ ----
    /// <summary>Cây menu nằm bên "left" (mặc định) hay "right".</summary>
    public string TreeSide { get; set; } = "left";
    /// <summary>Mở chương trình với cây menu đang ẩn (Ctrl+Shift+H để hiện).</summary>
    public bool TreeStartHidden { get; set; }
    /// <summary>Độ rộng cây menu (px); 312 = mặc định.</summary>
    public int TreeWidth { get; set; } = DefaultTreeWidth;
    /// <summary>Thanh tab tài liệu nằm ở dưới thay vì trên đầu.</summary>
    public bool TabsAtBottom { get; set; }
    /// <summary>Thanh công cụ: true = tự xuống dòng khi hẹp (mặc định); false = 1 dòng, thừa thì gom vào mũi tên "»".</summary>
    public bool ToolbarWrap { get; set; } = true;
    public const int DefaultTreeWidth = 312;

    // ---- Editor SQL ----
    /// <summary>Độ giãn dòng của editor = bội số của cỡ chữ (vd 1,4); 0 = tự động.</summary>
    public double EditorLineSpacing { get; set; }
    public bool EditorMinimap { get; set; }
    public bool EditorLineNumbers { get; set; } = true;
    public bool EditorWhitespace { get; set; }

    // ---- Lưới kết quả ----
    /// <summary>Tô xen kẽ dòng chẵn/lẻ.</summary>
    public bool ResultStripe { get; set; } = true;
    /// <summary>Màu dòng xen kẽ "#RRGGBB" (null = theo theme).</summary>
    public string? ResultStripeColor { get; set; }
    /// <summary>Màu ô/dòng đang chọn (null = theo theme).</summary>
    public string? ResultSelColor { get; set; }
    /// <summary>Cách hiện giá trị NULL: "italic" (NULL nghiêng mờ), "bracket" ([NULL]), "dash" (—), "blank" (để trống).</summary>
    public string ResultNullStyle { get; set; } = "italic";
    /// <summary>Đường kẻ lưới: "both" (ngang + dọc), "horizontal", "none".</summary>
    public string ResultGridLines { get; set; } = "both";

    public static readonly string[] NullStyles = { "italic", "bracket", "dash", "blank" };
    public static readonly string[] GridLineModes = { "both", "horizontal", "none" };

    public static readonly (string Id, string Text, string Hint)[] AreaList =
    {
        ("top", "Thanh trên", "Brand, Script, Workspace, chọn project, thanh công cụ"),
        ("tree", "Cây menu bên trái", "Cây WCommand / SQL Object, thanh lọc, cột biểu tượng"),
        ("tabs", "Thanh tab", "Các tab tài liệu (SQL Query, Table, Gen Update...)"),
        ("editor", "Vùng soạn thảo SQL", "Editor SQL và thanh Execute / Debug"),
        ("result", "Vùng kết quả", "Lưới kết quả SQL (Result with N table(s))"),
        ("status", "Thanh trạng thái", "Dòng trạng thái dưới cùng"),
    };

    public static readonly (string Id, string Text, double Factor)[] Densities =
    {
        ("compact", "Gọn", 0.8), ("normal", "Vừa", 1.0), ("comfortable", "Thoáng", 1.25),
    };

    /// <summary>Hệ số mật độ hiện hành (0,8 / 1 / 1,25).</summary>
    public static double DensityFactor => Densities.FirstOrDefault(d => d.Id == Current.Density).Factor is var f && f > 0 ? f : 1.0;

    /// <summary>Kích thước px (ở mật độ "Vừa") → theo mật độ hiện hành, tối thiểu 1.</summary>
    public static int Dens(int px) => Math.Max(1, (int)Math.Round(px * DensityFactor));

    /// <summary>Style (không rỗng) của khu vực, hoặc null.</summary>
    public static ControlStyle? AreaStyle(string area)
        => Current.Areas.TryGetValue(area, out var s) && !s.IsEmpty ? s : null;

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
        foreach (var a in AreaList) t.Areas[a.Id] = new ControlStyle();
        return t;
    }

    public UiTemplate Clone()
    {
        var t = new UiTemplate { Name = Name, FontFamily = FontFamily, FontSize = FontSize };
        foreach (var (k, v) in Controls) t.Controls[k] = v.Clone();
        foreach (var (k, v) in Items) t.Items[k] = v.Clone();
        t.ScriptOrder = new List<string>(ScriptOrder);
        t.Density = Density; t.CornerRadius = CornerRadius; t.BorderWidth = BorderWidth;
        foreach (var (k, v) in Areas) t.Areas[k] = v.Clone();
        t.Shortcuts = new Dictionary<string, string>(Shortcuts);
        t.CustomUiEnabled = CustomUiEnabled; t.CustomUiDesignMode = CustomUiDesignMode;
        t.TreeSide = TreeSide; t.TreeStartHidden = TreeStartHidden; t.TreeWidth = TreeWidth; t.TabsAtBottom = TabsAtBottom; t.ToolbarWrap = ToolbarWrap;
        t.EditorLineSpacing = EditorLineSpacing; t.EditorMinimap = EditorMinimap; t.EditorLineNumbers = EditorLineNumbers; t.EditorWhitespace = EditorWhitespace;
        t.ResultStripe = ResultStripe; t.ResultStripeColor = ResultStripeColor; t.ResultSelColor = ResultSelColor;
        t.ResultNullStyle = ResultNullStyle; t.ResultGridLines = ResultGridLines;
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
            if (Densities.Any(d => d.Id == loaded.Density)) t.Density = loaded.Density;
            t.CornerRadius = Math.Clamp(loaded.CornerRadius, 0, 24);
            t.BorderWidth = Math.Clamp(loaded.BorderWidth, 1, 4);
            foreach (var (k, v) in loaded.Areas) t.Areas[k] = v ?? new ControlStyle();
            t.Normalize(loaded);
            if (!string.IsNullOrWhiteSpace(loaded.DarkTheme)) t.DarkTheme = loaded.DarkTheme;
            if (!string.IsNullOrWhiteSpace(loaded.LightTheme)) t.LightTheme = loaded.LightTheme;
            t.DarkColors = loaded.DarkColors ?? new(); t.LightColors = loaded.LightColors ?? new();
        }
        catch { /* file hỏng — giữ mặc định */ }
        return t;
    }

    /// <summary>Chép các tuỳ chọn bố cục / editor / lưới kết quả từ <paramref name="src"/> (đã chặn giá trị lạ về mặc định).</summary>
    public void Normalize(UiTemplate src)
    {
        Shortcuts = (src.Shortcuts ?? new()).Where(kv => kv.Value == "" || ShortcutRegistry.Normalize(kv.Value) is not null)
            .ToDictionary(kv => kv.Key, kv => kv.Value == "" ? "" : ShortcutRegistry.Normalize(kv.Value)!);
        CustomUiEnabled = src.CustomUiEnabled;
        CustomUiDesignMode = src.CustomUiDesignMode;
        TreeSide = src.TreeSide == "right" ? "right" : "left";
        TreeStartHidden = src.TreeStartHidden;
        TreeWidth = Math.Clamp(src.TreeWidth, 160, 800);
        TabsAtBottom = src.TabsAtBottom;
        ToolbarWrap = src.ToolbarWrap;
        EditorLineSpacing = src.EditorLineSpacing is >= 1.0 and <= 3.0 ? Math.Round(src.EditorLineSpacing, 2) : 0;
        EditorMinimap = src.EditorMinimap;
        EditorLineNumbers = src.EditorLineNumbers;
        EditorWhitespace = src.EditorWhitespace;
        ResultStripe = src.ResultStripe;
        ResultStripeColor = ParseColor(src.ResultStripeColor) is null ? null : src.ResultStripeColor;
        ResultSelColor = ParseColor(src.ResultSelColor) is null ? null : src.ResultSelColor;
        ResultNullStyle = NullStyles.Contains(src.ResultNullStyle) ? src.ResultNullStyle : "italic";
        ResultGridLines = GridLineModes.Contains(src.ResultGridLines) ? src.ResultGridLines : "both";
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
        "--d", "--radius", "--border-w",
        "--rv-stripe", "--rv-sel", "--rv-null-style", "--rv-bx", "--rv-by",
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
        // Mật độ / bo góc / viền: chỉ đẩy khi khác mặc định — còn lại shell.css dùng giá trị cứng như trước đây.
        if (DensityFactor != 1.0) v["--d"] = DensityFactor.ToString("0.##", CultureInfo.InvariantCulture);
        if (t.CornerRadius != DefaultCornerRadius) v["--radius"] = t.CornerRadius + "px";
        if (t.BorderWidth != 1) v["--border-w"] = t.BorderWidth + "px";
        // Lưới kết quả (resultview.html): màu dòng xen kẽ / dòng chọn, kiểu NULL, đường kẻ.
        if (!t.ResultStripe) v["--rv-stripe"] = "transparent";
        else if (ParseColor(t.ResultStripeColor) is { } stripe) v["--rv-stripe"] = ColorTranslator.ToHtml(stripe);
        if (ParseColor(t.ResultSelColor) is { } sel) v["--rv-sel"] = ColorTranslator.ToHtml(sel);
        if (t.ResultNullStyle != "italic") v["--rv-null-style"] = t.ResultNullStyle;
        if (t.ResultGridLines != "both") v["--rv-bx"] = "none";
        if (t.ResultGridLines == "none") v["--rv-by"] = "none";
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

        // Khu vực: trang nào khai <body data-area="..."> thì nhận font/cỡ/đậm/màu của khu vực đó (đặt thẳng lên body + biến --ui-font*
        // để các nút/ô nhập trong trang cũng theo). Không khai báo gì thì gỡ sạch, trang trở về như cũ.
        var areas = new Dictionary<string, Dictionary<string, string>>();
        foreach (var (id, _, _) in AreaList)
            if (AreaStyle(id) is { } st) areas[id] = ToInlineCss(st);
        // Chỉ đụng tới thuộc tính mà khu vực khai báo (và gỡ đúng những cái mình đã đặt lần trước) — để không xoá nhầm style do chính trang đặt
        // (vd editor tự đặt nền body theo palette).
        // CSS tuỳ chỉnh (UiOverrides): bản đồ trang → CSS ("*" = chung) — mỗi trang tự lấy phần của mình theo tên file, gắn vào <style id="bcode-user-css">;
        // chế độ thiết kế bật thì nạp thêm công cụ chọn phần tử (ui-designer.js). Trang được bảo vệ (uitemplate.html) không nhận gì.
        var designOn = UiOverrides.Enabled && Current.CustomUiDesignMode;
        sb.Append("(function(m,design,prot){var page=location.pathname.split('/').pop();if(prot.indexOf(page)>=0)return;")
          .Append("window.__bcodeUiRaw=m;var css=(m['*']||'')+'\\n'+(m[page]||'');var st=document.getElementById('bcode-user-css');")
          .Append("if(css.trim()){if(!st){st=document.createElement('style');st.id='bcode-user-css';(document.head||document.documentElement).appendChild(st);}st.textContent=css;}else if(st){st.remove();}")
          .Append("if(design){if(!document.getElementById('bcode-designer-js')&&document.head){var s=document.createElement('script');s.id='bcode-designer-js';s.src='https://").Append(WebViewEnvironment.Host).Append("/ui-designer.js';document.head.appendChild(s);}else if(window.__bcodeDesignerSync)window.__bcodeDesignerSync(true);}")
          .Append("else if(window.__bcodeDesignerSync)window.__bcodeDesignerSync(false);})(")
          .Append(JsonSerializer.Serialize(UiOverrides.AllCss())).Append(",").Append(designOn ? "true" : "false").Append(",")
          .Append(JsonSerializer.Serialize(UiOverrides.Protected)).Append(");");

        // Bản vá cấu trúc của thiết kế trực quan (đổi chữ / thuộc tính / vị trí phần tử): áp ĐÚNG MỘT LẦN cho mỗi lần nạp trang.
        sb.Append("window.__bcodeUiProtected=").Append(JsonSerializer.Serialize(UiOverrides.Protected)).Append(";");
        sb.Append("window.__bcodePatches=").Append(JsonSerializer.Serialize(UiOverrides.AllPatches())).Append(";").Append(UiOverrides.PatchApplyScript);

        // Phím tắt hiện hành: danh sách tổ hợp toàn cửa sổ (trang bắt phím rồi báo C#) + bản đồ phím của editor SQL.
        sb.Append("window.__bcodeKeys=").Append(JsonSerializer.Serialize(ShortcutRegistry.ActiveAppCombos())).Append(";")
          .Append("window.__bcodeEditorKeys=").Append(JsonSerializer.Serialize(ShortcutRegistry.EditorKeymap())).Append(";")
          .Append("if(window.applyKeymap)window.applyKeymap();");
        sb.Append("(function(b,m){if(!b)return;var c=m[b.dataset.area]||{};var prev=(b.dataset.areaKeys||'').split(',').filter(Boolean),now=[];")
          .Append("['fontFamily','fontSize','fontWeight','color','background'].forEach(function(k){if(c[k]){b.style[k]=c[k];now.push(k);}});")
          .Append("prev.forEach(function(k){if(now.indexOf(k)<0)b.style[k]='';});b.dataset.areaKeys=now.join(',');")
          .Append("b.style.setProperty('--ui-font',c.fontFamily||'');b.style.setProperty('--ui-font-size',c.fontSize||'');")
          .Append("})(document.body,").Append(JsonSerializer.Serialize(areas)).Append(");");
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
        // Bản ghi đè HTML / bật-tắt tuỳ chỉnh đổi → trang đang hiển thị nạp lại theo UrlFor (host gốc hoặc host ghi đè). Trang được bảo vệ thì không.
        void OnPageChanged(string page)
        {
            try
            {
                if (web.IsDisposed || web.CoreWebView2 is null || !Uri.TryCreate(web.Source?.ToString(), UriKind.Absolute, out var src)) return;
                var current = Path.GetFileName(src.AbsolutePath);
                if (!current.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || UiOverrides.Protected.Contains(current)) return;
                if (src.Host != WebViewEnvironment.Host && src.Host != UiOverrides.UserHost) return;
                if (page != "*" && !page.Equals(current, StringComparison.OrdinalIgnoreCase)) return;
                web.CoreWebView2.Navigate(UiOverrides.UrlFor(current));
            }
            catch { /* WebView2 đang đóng */ }
        }
        Changed += Push;
        UiOverrides.CssChanged += Push;
        UiOverrides.PageChanged += OnPageChanged;
        ThemeManager.ThemeChanged += Push; // đổi sáng/tối → bảng màu hiện hành đổi theo
        web.Disposed += (_, _) => { Changed -= Push; UiOverrides.CssChanged -= Push; UiOverrides.PageChanged -= OnPageChanged; ThemeManager.ThemeChanged -= Push; };
        web.NavigationCompleted += (_, _) => Push();
        Push();
    }
}
