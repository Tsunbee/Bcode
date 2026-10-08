namespace Bcode.App.UI;

public enum ShortcutScope
{
    /// <summary>Phím toàn cửa sổ Bcode (bấm được cả khi con trỏ đang ở trong trang WebView2).</summary>
    App,
    /// <summary>Phím chỉ có tác dụng khi con trỏ đang ở editor SQL (Monaco).</summary>
    Editor,
    /// <summary>Phím chỉ có tác dụng khi con trỏ ở lưới của tab Table.</summary>
    Grid,
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
    private static readonly List<ShortcutDef> _fixed = BuildFixed();

    private static List<ShortcutDef> BuildFixed()
    {
        var l = new List<ShortcutDef>
        {
            // ---- Cửa sổ ----
            new("tree.toggle", "Ẩn / hiện cây menu bên trái", "Cửa sổ", ShortcutScope.App, "Ctrl+Shift+H"),
            new("window.new", "Mở thêm cửa sổ Bcode mới", "Cửa sổ", ShortcutScope.App, "Ctrl+Shift+N"),
            new("app.theme", "Đổi giao diện sáng / tối", "Cửa sổ", ShortcutScope.App, ""),
            new("app.palette", "Command Palette — tìm nhanh tool, tab, menu, object SQL", "Cửa sổ", ShortcutScope.App, "Ctrl+P"),
            new("app.sqlHints", "Mở hướng dẫn gợi ý code SQL (gõ gì ra gì)", "Cửa sổ", ShortcutScope.App, ""),
            new("app.usages", "Ai đang dùng object của tab SQL đang mở?", "Cửa sổ", ShortcutScope.App, "Ctrl+Alt+U"),
            new("app.restoreSession", "Bật / tắt khôi phục tab khi mở lại Bcode", "Cửa sổ", ShortcutScope.App, ""),
            new("app.quickAccess", "Mở Quick Access (chọn nút hiện trên thanh công cụ)", "Cửa sổ", ShortcutScope.App, ""),
            new("app.settingsMenu", "Mở menu Settings (bánh răng)", "Cửa sổ", ShortcutScope.App, ""),
            new("app.template", "Mở màn Giao diện (Template)", "Cửa sổ", ShortcutScope.App, ""),
            new("app.customUi", "Bật / tắt tuỳ chỉnh giao diện web (chế độ an toàn)", "Cửa sổ", ShortcutScope.App, ""),

            // ---- Tab ----
            new("tab.next", "Chuyển sang tab kế", "Tab", ShortcutScope.App, "Ctrl+Tab"),
            new("tab.prev", "Chuyển về tab trước", "Tab", ShortcutScope.App, "Ctrl+Shift+Tab"),
            new("tab.close", "Đóng tab đang mở (Close Tab)", "Tab", ShortcutScope.App, "Ctrl+W"),
            new("tab.pin", "Ghim / bỏ ghim tab đang mở (Pin Tab)", "Tab", ShortcutScope.App, ""),
            new("tab.closeOthers", "Đóng các tab khác (Close Other Tabs)", "Tab", ShortcutScope.App, ""),
            new("tab.closeRight", "Đóng các tab bên phải (Close Tabs to the Right)", "Tab", ShortcutScope.App, ""),
            new("tab.closeAll", "Đóng tất cả tab (Close All Tabs)", "Tab", ShortcutScope.App, ""),
        };
        for (var i = 1; i <= 9; i++) l.Add(new($"tab.goto{i}", $"Chuyển tới tab thứ {i}", "Tab", ShortcutScope.App, ""));

        l.AddRange(new ShortcutDef[]
        {
            // ---- Database & Script ----
            new("db.app", "Chuyển sang App Data", "Database & Script", ShortcutScope.App, ""),
            new("db.sys", "Chuyển sang Sys Data", "Database & Script", ShortcutScope.App, ""),
            new("script.add", "Add Script (thêm vào giỏ script)", "Database & Script", ShortcutScope.App, "Ctrl+Alt+Shift+A"),
            new("script.view", "View Script Cart (xem giỏ script)", "Database & Script", ShortcutScope.App, "Ctrl+Alt+Shift+V"),
            new("script.clear", "Clear Script (xoá giỏ script)", "Database & Script", ShortcutScope.App, ""),
            new("script.save", "Save Script", "Database & Script", ShortcutScope.App, ""),
            new("script.copy", "Copy Script", "Database & Script", ShortcutScope.App, ""),

            // ---- Project & kết nối ----
            new("project.picker", "Chọn project (màn hình Projects)", "Project & kết nối", ShortcutScope.App, "Ctrl+Shift+P"),
            new("project.quick", "Chọn nhanh project theo mã", "Project & kết nối", ShortcutScope.App, "Ctrl+F5"),
            new("app.chooseServer", "Choose Server (kết nối)", "Project & kết nối", ShortcutScope.App, "Ctrl+O"),
            new("app.programPath", "Mở thư mục Program Path", "Project & kết nối", ShortcutScope.App, "Ctrl+5"),
            new("project.openSource", "Mở thư mục Source Path của project", "Project & kết nối", ShortcutScope.App, ""),
            new("project.openWorking", "Mở thư mục Working Path (Update) của project", "Project & kết nối", ShortcutScope.App, ""),
            new("project.openMobile", "Mở thư mục Mobile Path của project", "Project & kết nối", ShortcutScope.App, ""),
            new("project.web", "Bung web đăng nhập (Login WLink) của project", "Project & kết nối", ShortcutScope.App, ""),
            new("project.copyInfo", "Copy Project Info (ID, SQL, Web, Program, Source...)", "Project & kết nối", ShortcutScope.App, ""),
            new("app.refreshWebConfig", "Refresh Web.config", "Project & kết nối", ShortcutScope.App, ""),
            new("app.clearStructure", "Clear Structure App", "Project & kết nối", ShortcutScope.App, ""),
            new("app.createMenu", "Create Menu", "Project & kết nối", ShortcutScope.App, ""),
            new("debug.decrypt", "Giải mã chuỗi kết nối (debug)", "Project & kết nối", ShortcutScope.App, "Ctrl+Shift+F5"),

            // ---- Editor SQL (chỉ khi con trỏ ở editor SQL Query) ----
            new("editor.run", "Chạy script (Execute)", "Editor SQL", ShortcutScope.Editor, "F5"),
            new("editor.run2", "Chạy script (phím phụ)", "Editor SQL", ShortcutScope.Editor, "Ctrl+Enter"),
            new("editor.beauty", "Làm đẹp SQL (Beauty)", "Editor SQL", ShortcutScope.Editor, "F7"),
            new("editor.wrap", "Bật / tắt tự xuống dòng (Wrap)", "Editor SQL", ShortcutScope.Editor, "Alt+Z"),
            new("editor.ai", "AI: sửa / sinh SQL theo yêu cầu", "Editor SQL", ShortcutScope.Editor, "Ctrl+I"),
            new("editor.suggest", "Gọi gợi ý AI ngay", "Editor SQL", ShortcutScope.Editor, "Alt+\\"),
            new("editor.saveHistory", "Lưu script vào Lịch sử SQL", "Editor SQL", ShortcutScope.Editor, "Ctrl+Alt+H"),
            new("editor.open", "Open (mở file .sql)", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.save", "Save (lưu file .sql)", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.writeSchema", "Write Schema", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.checkFields", "Check Fields", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.comment", "Comment (chú thích dòng đang chọn)", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.uncomment", "Uncomment (bỏ chú thích)", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.fontUp", "Tăng cỡ chữ editor", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.fontDown", "Giảm cỡ chữ editor", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.options", "Mở menu Options của editor", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.dbApp", "Editor: chuyển sang App Data", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.dbSys", "Editor: chuyển sang Sys Data", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.toggleSuggest", "Bật / tắt Suggest Param/Caret", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.toggleResetConn", "Bật / tắt Reset Connection", "Editor SQL", ShortcutScope.Editor, ""),
            new("editor.toggleResultTab", "Bật / tắt Result Tab (kết quả ra tab mới)", "Editor SQL", ShortcutScope.Editor, ""),

            // ---- Lưới Table ----
            new("table.rowDetail", "Table: xem / sửa chi tiết dòng đang chọn (View Detail Datarow)", "Lưới Table", ShortcutScope.Grid, "F1"),
            new("table.listEditor", "Table: khai báo nhanh danh sách (a, b, c) trong các ô đang chọn", "Lưới Table", ShortcutScope.Grid, "F3"),

            // ---- Debug từng bước ----
            new("editor.debugTarget", "Debug store/function (chọn store/function để debug)", "Debug từng bước", ShortcutScope.Editor, ""),
            new("editor.debugStep", "Bật / tắt chế độ Debug từng bước", "Debug từng bước", ShortcutScope.Editor, ""),
            new("debug.next", "Debug: chạy dòng kế (Step)", "Debug từng bước", ShortcutScope.Editor, "F10"),
            new("debug.cursor", "Debug: chạy tới dòng con trỏ", "Debug từng bước", ShortcutScope.Editor, "Ctrl+F10"),
            new("debug.stop", "Debug: dừng", "Debug từng bước", ShortcutScope.Editor, "Shift+F5"),
        });
        return l;
    }

    private static List<ShortcutDef> _tools = new();

    /// <summary>Đăng ký các nút công cụ (key, chữ, phím mặc định hoặc null) — id phím = "tool:&lt;key&gt;".</summary>
    public static void SetTools(IEnumerable<(string Key, string Label, string? DefaultCombo)> tools)
        => _tools = tools.Select(t => new ShortcutDef("tool:" + t.Key, t.Label, "Công cụ (mở tab / hộp thoại)", ShortcutScope.App,
                Normalize(t.DefaultCombo) ?? "")).ToList();

    public static IEnumerable<ShortcutDef> All => _tools.Concat(_fixed).OrderBy(d => d.Scope).ThenBy(d => GroupOrder(d.Group)).ToList();

    private static int GroupOrder(string g) => g switch
    {
        "Công cụ (mở tab / hộp thoại)" => 0, "Cửa sổ" => 1, "Tab" => 2, "Database & Script" => 3, "Project & kết nối" => 4, "Editor SQL" => 5, "Debug từng bước" => 6, "Lưới Table" => 7, _ => 9,
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
