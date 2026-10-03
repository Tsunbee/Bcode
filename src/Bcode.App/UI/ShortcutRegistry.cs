namespace Bcode.App.UI;

public enum ShortcutScope
{
    /// <summary>Phím toàn cửa sổ Bcode (bấm được cả khi con trỏ đang ở trong trang WebView2).</summary>
    App,
    /// <summary>Phím chỉ có tác dụng khi con trỏ đang ở editor SQL (Monaco).</summary>
    Editor,
}

public sealed record ShortcutDef(string Id, string Text, string Group, ShortcutScope Scope, string Default);

/// <summary>
/// Danh sách MỌI phím tắt cấu hình được + phím mặc định, và phím người dùng đã khai báo lại (UiTemplate.Shortcuts: id → tổ hợp phím,
/// chuỗi rỗng = tắt phím đó). Tổ hợp viết dạng chuẩn "Ctrl+Alt+Shift+Phím" — Phím là A–Z, 0–9, F1–F24, Tab, Enter, Space, Backspace,
/// Delete, Insert, Home, End, PageUp, PageDown, Up/Down/Left/Right hoặc ký tự dấu ( ` - = [ ] \ ; ' , . / ).
///
/// Nhóm "Công cụ" do MainForm đăng ký lúc khởi động (<see cref="SetTools"/>) từ danh sách nút công cụ thật, nên nút mới thêm tự có mặt ở đây.
/// </summary>
public static class ShortcutRegistry
{
    private static readonly List<ShortcutDef> _fixed = new()
    {
        new("tree.toggle", "Ẩn / hiện cây menu bên trái", "Cửa sổ", ShortcutScope.App, "Ctrl+Shift+H"),
        new("tab.next", "Chuyển sang tab kế", "Cửa sổ", ShortcutScope.App, "Ctrl+Tab"),
        new("tab.prev", "Chuyển về tab trước", "Cửa sổ", ShortcutScope.App, "Ctrl+Shift+Tab"),
        new("tab.close", "Đóng tab đang mở", "Cửa sổ", ShortcutScope.App, "Ctrl+W"),
        new("window.new", "Mở thêm cửa sổ Bcode mới", "Cửa sổ", ShortcutScope.App, "Ctrl+Shift+N"),
        new("project.picker", "Chọn project (màn hình Projects)", "Project & kết nối", ShortcutScope.App, "Ctrl+Shift+P"),
        new("project.quick", "Chọn nhanh project theo mã", "Project & kết nối", ShortcutScope.App, "Ctrl+F5"),
        new("app.chooseServer", "Choose Server (kết nối)", "Project & kết nối", ShortcutScope.App, "Ctrl+O"),
        new("app.programPath", "Mở thư mục Program Path", "Project & kết nối", ShortcutScope.App, "Ctrl+5"),
        new("debug.decrypt", "Giải mã chuỗi kết nối (debug)", "Project & kết nối", ShortcutScope.App, "Ctrl+Shift+F5"),

        new("editor.run", "Chạy script (Execute)", "Editor SQL", ShortcutScope.Editor, "F5"),
        new("editor.run2", "Chạy script (phím phụ)", "Editor SQL", ShortcutScope.Editor, "Ctrl+Enter"),
        new("editor.beauty", "Làm đẹp SQL (Beauty)", "Editor SQL", ShortcutScope.Editor, "F7"),
        new("editor.wrap", "Bật / tắt tự xuống dòng (Wrap)", "Editor SQL", ShortcutScope.Editor, "Alt+Z"),
        new("editor.ai", "AI: sửa / sinh SQL theo yêu cầu", "Editor SQL", ShortcutScope.Editor, "Ctrl+I"),
        new("editor.suggest", "Gọi gợi ý AI ngay", "Editor SQL", ShortcutScope.Editor, "Alt+\\"),
        new("debug.next", "Debug: chạy dòng kế (Step)", "Debug từng bước", ShortcutScope.Editor, "F10"),
        new("debug.cursor", "Debug: chạy tới dòng con trỏ", "Debug từng bước", ShortcutScope.Editor, "Ctrl+F10"),
        new("debug.stop", "Debug: dừng", "Debug từng bước", ShortcutScope.Editor, "Shift+F5"),
    };

    private static List<ShortcutDef> _tools = new();

    /// <summary>Đăng ký các nút công cụ (key, chữ, phím mặc định hoặc null) — id phím = "tool:&lt;key&gt;".</summary>
    public static void SetTools(IEnumerable<(string Key, string Label, string? DefaultCombo)> tools)
        => _tools = tools.Select(t => new ShortcutDef("tool:" + t.Key, t.Label, "Công cụ (mở tab / hộp thoại)", ShortcutScope.App,
                Normalize(t.DefaultCombo) ?? "")).ToList();

    public static IEnumerable<ShortcutDef> All => _tools.Concat(_fixed).OrderBy(d => d.Scope).ThenBy(d => GroupOrder(d.Group)).ToList();

    private static int GroupOrder(string g) => g switch
    {
        "Công cụ (mở tab / hộp thoại)" => 0, "Cửa sổ" => 1, "Project & kết nối" => 2, "Editor SQL" => 3, "Debug từng bước" => 4, _ => 9,
    };

    public static ShortcutDef? Find(string id) => All.FirstOrDefault(d => d.Id == id);

    /// <summary>Phím hiện hành của <paramref name="id"/> ("" = không có phím): bản người dùng khai báo, không có thì mặc định.</summary>
    public static string Get(string id)
    {
        var def = Find(id);
        if (def is null) return "";
        if (UiTemplate.Current.Shortcuts.TryGetValue(id, out var custom)) return custom == "" ? "" : Normalize(custom) ?? def.Default;
        return def.Default;
    }

    /// <summary>Chữ hiển thị trên menu/tooltip ("Ctrl+Shift+Q"), rỗng nếu chưa gán phím.</summary>
    public static string Display(string id) => Get(id);

    /// <summary>Id chức năng (phạm vi App) đang gắn với tổ hợp <paramref name="combo"/>, hoặc null.</summary>
    public static string? AppIdFor(string combo)
    {
        var c = Normalize(combo);
        if (c is null) return null;
        foreach (var d in All)
            if (d.Scope == ShortcutScope.App && Get(d.Id) == c) return d.Id;
        return null;
    }

    /// <summary>Mọi tổ hợp phạm vi App đang dùng — đẩy xuống các trang WebView2 để bắt phím ngay cả khi con trỏ nằm trong trang.</summary>
    public static List<string> ActiveAppCombos()
        => All.Where(d => d.Scope == ShortcutScope.App).Select(d => Get(d.Id)).Where(c => c.Length > 0).Distinct().ToList();

    /// <summary>Bản đồ id → tổ hợp của các phím Editor (rỗng = tắt) để đẩy cho editor SQL.</summary>
    public static Dictionary<string, string> EditorKeymap()
        => All.Where(d => d.Scope == ShortcutScope.Editor).ToDictionary(d => d.Id, d => Get(d.Id));

    // ------------------------------------------------------------------ tổ hợp phím <-> chuỗi / Keys

    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tab"] = "Tab", ["enter"] = "Enter", ["return"] = "Enter", ["space"] = "Space", ["spacebar"] = "Space", ["backspace"] = "Backspace",
        ["delete"] = "Delete", ["del"] = "Delete", ["insert"] = "Insert", ["ins"] = "Insert", ["home"] = "Home", ["end"] = "End",
        ["pageup"] = "PageUp", ["pgup"] = "PageUp", ["pagedown"] = "PageDown", ["pgdn"] = "PageDown",
        ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
    };
    private const string Punctuation = "`-=[]\\;',./";

    /// <summary>Chuẩn hoá tổ hợp ("ctrl + shift + q" → "Ctrl+Shift+Q"); null nếu không hợp lệ.</summary>
    public static string? Normalize(string? combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return null;
        bool ctrl = false, alt = false, shift = false;
        string? key = null;
        // Dấu "+" cũng là phím (Ctrl++): tách theo "+" nhưng cho phép phần cuối rỗng nghĩa là phím "+".
        var parts = combo.Split('+').Select(p => p.Trim()).ToList();
        if (parts.Count >= 2 && parts[^1] == "" && parts[^2] == "") { parts.RemoveRange(parts.Count - 2, 2); parts.Add("+"); }
        foreach (var p in parts)
        {
            if (p.Length == 0) return null;
            switch (p.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; continue;
                case "alt": alt = true; continue;
                case "shift": shift = true; continue;
            }
            if (key is not null) return null;
            if (p.Length == 1 && char.IsLetter(p[0])) key = char.ToUpperInvariant(p[0]).ToString();
            else if (p.Length == 1 && (char.IsDigit(p[0]) || Punctuation.Contains(p[0]) || p[0] == '+')) key = p;
            else if (System.Text.RegularExpressions.Regex.IsMatch(p, @"^[Ff]([1-9]|1\d|2[0-4])$")) key = "F" + p[1..];
            else if (NamedKeys.TryGetValue(p, out var n)) key = n;
            else return null;
        }
        if (key is null) return null;
        var mods = new List<string>();
        if (ctrl) mods.Add("Ctrl");
        if (alt) mods.Add("Alt");
        if (shift) mods.Add("Shift");
        mods.Add(key);
        return string.Join("+", mods);
    }

    private static readonly HashSet<string> Reserved = new()
    {
        "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Z", "Ctrl+Y", "Ctrl+A", "Alt+F4", "Ctrl+Alt+Delete", "Ctrl+Shift+Escape",
    };

    /// <summary>Kiểm tra tổ hợp có dùng làm phím tắt được không (trả lý do nếu không). Phím chữ/số/dấu phải đi cùng Ctrl hoặc Alt;
    /// phím F1–F24 dùng riêng được; các phím sửa văn bản (Ctrl+C/V/X/Z/Y/A...) bị cấm.</summary>
    public static bool IsAllowed(string combo, out string reason)
    {
        reason = "";
        var c = Normalize(combo);
        if (c is null) { reason = "Tổ hợp phím không hợp lệ."; return false; }
        if (Reserved.Contains(c)) { reason = $"{c} là phím hệ thống / sửa văn bản, không dùng làm phím tắt."; return false; }
        var parts = c.Split('+');
        var key = parts[^1];
        var hasMod = parts.Length > 1;
        var hasCtrlOrAlt = parts.Contains("Ctrl") || parts.Contains("Alt");
        var isF = key.StartsWith('F') && key.Length >= 2 && char.IsDigit(key[1]);
        if (!hasMod && !isF) { reason = "Phím này phải đi cùng Ctrl hoặc Alt (chỉ phím F1–F24 được dùng riêng)."; return false; }
        if (hasMod && !hasCtrlOrAlt && !isF) { reason = "Cần Ctrl hoặc Alt (Shift một mình sẽ chặn việc gõ chữ)."; return false; }
        if (key is "Enter" or "Tab" or "Space" or "Backspace" && !hasCtrlOrAlt) { reason = "Phím này cần đi cùng Ctrl hoặc Alt."; return false; }
        return true;
    }

    /// <summary>Chỉ giữ lại các khai báo hợp lệ: id còn tồn tại, tổ hợp hợp lệ + được phép (hoặc rỗng = tắt), khác mặc định.</summary>
    public static Dictionary<string, string> CleanOverrides(IDictionary<string, string>? overrides)
    {
        var result = new Dictionary<string, string>();
        if (overrides is null) return result;
        foreach (var (id, raw) in overrides)
        {
            var def = Find(id);
            if (def is null) continue;
            if (string.IsNullOrWhiteSpace(raw)) { if (def.Default != "") result[id] = ""; continue; }
            var c = Normalize(raw);
            if (c is null || !IsAllowed(c, out _)) continue;
            if (c != def.Default) result[id] = c;
        }
        return result;
    }

    /// <summary>Tổ hợp từ <see cref="Keys"/> của WinForms ("Ctrl+Shift+Q"); null nếu là phím không hỗ trợ / chỉ có phím bổ trợ.</summary>
    public static string? FromKeys(Keys keyData)
    {
        var code = keyData & Keys.KeyCode;
        string? key = code switch
        {
            >= Keys.A and <= Keys.Z => ((char)('A' + (code - Keys.A))).ToString(),
            >= Keys.D0 and <= Keys.D9 => ((char)('0' + (code - Keys.D0))).ToString(),
            >= Keys.F1 and <= Keys.F24 => "F" + (code - Keys.F1 + 1),
            Keys.Tab => "Tab", Keys.Return => "Enter", Keys.Space => "Space", Keys.Back => "Backspace", Keys.Delete => "Delete",
            Keys.Insert => "Insert", Keys.Home => "Home", Keys.End => "End", Keys.PageUp => "PageUp", Keys.PageDown => "PageDown",
            Keys.Up => "Up", Keys.Down => "Down", Keys.Left => "Left", Keys.Right => "Right",
            Keys.Oemtilde => "`", Keys.OemMinus => "-", Keys.Oemplus => "=", Keys.OemOpenBrackets => "[", Keys.Oem6 => "]",
            Keys.Oem5 or Keys.OemBackslash => "\\", Keys.Oem1 => ";", Keys.Oem7 => "'", Keys.Oemcomma => ",", Keys.OemPeriod => ".", Keys.OemQuestion => "/",
            _ => null,
        };
        if (key is null) return null;
        var mods = new List<string>();
        if ((keyData & Keys.Control) == Keys.Control) mods.Add("Ctrl");
        if ((keyData & Keys.Alt) == Keys.Alt) mods.Add("Alt");
        if ((keyData & Keys.Shift) == Keys.Shift) mods.Add("Shift");
        mods.Add(key);
        return string.Join("+", mods);
    }
}
