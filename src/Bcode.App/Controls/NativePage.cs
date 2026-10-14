using System.Text;
using System.Text.Json;

namespace Bcode.App.Controls;

/// <summary>
/// "Trang native": bản WinForms thay cho 1 trang Web/Shell/*.html ở chế độ App, nói ĐÚNG giao thức của trang web — C# chủ gọi xuống bằng câu lệnh JS (vd <c>cmpStruct.init({...})</c>,
/// <c>window.setStatus('...', 'ok')</c>) và trang gửi lên bằng thông điệp JSON <c>{action, ...}</c>. <see cref="WebBarHost"/> ở chế độ App tự dùng trang native (nếu đã đăng ký trong
/// <see cref="NativePages"/>) thay cho WebView2 nên MỌI control / form dựa trên WebBarHost (và WebDialogForm) chạy native mà không phải sửa dòng logic nào của chúng.
/// Lớp con chỉ viết giao diện + (1) <see cref="Dispatch"/> nhận các lời gọi, (2) <see cref="Post"/> các hành động người dùng.
/// </summary>
public abstract class NativePage : UserControl
{
    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Thông điệp người dùng gửi lên (tương đương <c>chrome.webview.postMessage</c>).</summary>
    public event Action<JsonElement>? Message;

    protected NativePage() { Dock = DockStyle.Fill; }

    /// <summary>Gửi 1 hành động lên chủ: đối tượng ẩn danh <c>new { action = "...", ... }</c> (tên trường camelCase như JSON của trang web).</summary>
    protected void Post(object payload)
    {
        var el = JsonSerializer.SerializeToElement(payload, Camel);
        Message?.Invoke(el);
    }

    /// <summary>Nhận 1 lời gọi hàm từ chủ. <paramref name="path"/> = tên hàm đầy đủ ("cmpStruct.init", "window.setStatus"); <paramref name="args"/> = các đối số đã đổi sang JSON.
    /// Lời gọi không nhận ra thì bỏ qua (như gọi hàm chưa định nghĩa với <c>window.x &amp;&amp; window.x()</c> ở trang web).</summary>
    public abstract void Dispatch(string path, IReadOnlyList<JsonElement> args);

    /// <summary>Dữ liệu lớn chủ gửi bằng <see cref="WebBarHost.PostJson"/>.</summary>
    public virtual void OnPostJson(JsonElement data) { }

    /// <summary>Đưa con trỏ vào ô chính (tương đương FocusWeb + focus ô nhập của trang).</summary>
    public virtual void FocusPrimary() { }

    /// <summary>Chiều cao nội dung của trang dạng "thanh" (mọi hàng Dock Top/Bottom, hàng BarFlow tự xuống dòng khi hẹp) — <see cref="WebBarHost"/> dùng để tự cao
    /// theo nội dung thay vì con số cố định (nút xuống dòng không còn bị control bên dưới che). null = trang có vùng Fill (không phải thanh) → giữ chiều cao chủ đặt.</summary>
    public int? ContentHeight()
    {
        var h = Padding.Vertical;
        var any = false;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            if (c.Dock == DockStyle.Fill) return null;
            if (c.Dock is DockStyle.Top or DockStyle.Bottom) { h += c.Height; any = true; }
        }
        return any ? h : null;
    }

    // ---- tiện ích đọc đối số ----
    protected static string Str(IReadOnlyList<JsonElement> a, int i) => i < a.Count && a[i].ValueKind == JsonValueKind.String ? a[i].GetString() ?? "" : i < a.Count && a[i].ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? a[i].ToString() : "";
    protected static bool Bool(IReadOnlyList<JsonElement> a, int i) => i < a.Count && a[i].ValueKind == JsonValueKind.True;
    protected static int Int(IReadOnlyList<JsonElement> a, int i, int d = 0) => i < a.Count && a[i].ValueKind == JsonValueKind.Number && a[i].TryGetInt32(out var v) ? v : d;
    protected static JsonElement Arg(IReadOnlyList<JsonElement> a, int i) => i < a.Count ? a[i] : default;
    protected static string P(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "" : v.ToString()) : "";
    protected static bool PB(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    protected static int PI(JsonElement e, string name, int d = 0) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : d;
    protected static IEnumerable<JsonElement> PA(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
    protected static JsonElement PO(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
}

/// <summary>Đăng ký các <see cref="NativePage"/> theo tên file trang (vd "comparestructure.html"). <see cref="WebBarHost"/> hỏi ở đây khi chạy chế độ App.</summary>
public static class NativePages
{
    private static readonly Dictionary<string, Func<NativePage>> Map = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(string page, Func<NativePage> factory) => Map[page] = factory;

    public static bool Has(string page) { EnsureRegistered(); return Map.ContainsKey(page); }

    public static bool TryCreate(string page, out NativePage? native)
    {
        EnsureRegistered();
        native = Map.TryGetValue(page, out var f) ? f() : null;
        return native is not null;
    }

    private static bool _registered;
    private static void EnsureRegistered()
    {
        if (_registered) return;
        _registered = true;
        NativePageCatalog.RegisterAll();
    }
}

/// <summary>Bộ phân tích câu lệnh JS mà C# gửi cho trang (<c>a.b(...); window.x &amp;&amp; window.x(...)</c>) thành (tên hàm, các đối số JSON). Chịu được literal kiểu JS viết tay:
/// khoá không ngoặc kép, chuỗi nháy đơn, <c>undefined</c>. Câu lệnh không phải lời gọi hàm đơn giản thì bỏ qua.</summary>
public static class JsCall
{
    public static List<(string Path, List<JsonElement> Args)> Parse(string script)
    {
        var result = new List<(string, List<JsonElement>)>();
        foreach (var stmt in SplitStatements(script))
        {
            var s = stmt.Trim();
            if (s.Length == 0) continue;
            // bỏ phần bảo vệ "window.x && window.x(...)" (có thể lặp)
            while (true)
            {
                var amp = TopLevelIndexOf(s, "&&");
                if (amp < 0) break;
                var left = s[..amp].Trim();
                if (left.Contains('(')) break;   // vế trái là 1 lời gọi thật, không phải điều kiện tồn tại
                s = s[(amp + 2)..].Trim();
            }
            var open = s.IndexOf('(');
            if (open <= 0) continue;
            var name = s[..open].Trim();
            if (name.Length == 0 || !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '$' or '.')) continue;
            var close = MatchParen(s, open);
            if (close < 0) continue;
            var inner = s[(open + 1)..close];
            var args = new List<JsonElement>();
            foreach (var part in SplitTopLevel(inner, ','))
            {
                if (part.Trim().Length == 0) continue;
                try { args.Add(ToElement(part.Trim())); } catch { args.Add(default); }
            }
            result.Add((name, args));
        }
        return result;
    }

    private static IEnumerable<string> SplitStatements(string s) => SplitTopLevel(s, ';');

    private static List<string> SplitTopLevel(string s, char sep)
    {
        var parts = new List<string>();
        var depth = 0; var q = '\0'; var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (q != '\0') { if (c == '\\') i++; else if (c == q) q = '\0'; continue; }
            if (c is '\'' or '"' or '`') { q = c; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == sep && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts;
    }

    private static int TopLevelIndexOf(string s, string token)
    {
        var depth = 0; var q = '\0';
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (q != '\0') { if (c == '\\') i++; else if (c == q) q = '\0'; continue; }
            if (c is '\'' or '"' or '`') { q = c; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && string.CompareOrdinal(s, i, token, 0, token.Length) == 0) return i;
        }
        return -1;
    }

    private static int MatchParen(string s, int open)
    {
        var depth = 0; var q = '\0';
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (q != '\0') { if (c == '\\') i++; else if (c == q) q = '\0'; continue; }
            if (c is '\'' or '"' or '`') { q = c; continue; }
            if (c == '(') depth++;
            else if (c == ')') { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    /// <summary>Literal JS → JSON hợp lệ → JsonElement.</summary>
    public static JsonElement ToElement(string literal)
    {
        var json = new StringBuilder();
        var i = 0;
        WriteValue(literal, ref i, json);
        using var doc = JsonDocument.Parse(json.ToString());
        return doc.RootElement.Clone();
    }

    private static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

    private static void WriteValue(string s, ref int i, StringBuilder o)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) { o.Append("null"); return; }
        var c = s[i];
        if (c is '"' or '\'') { WriteString(s, ref i, o); return; }
        if (c == '{')
        {
            i++; o.Append('{'); var first = true;
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == '}') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                if (!first) o.Append(',');
                first = false;
                if (s[i] is '"' or '\'') WriteString(s, ref i, o);
                else { var st = i; while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++; o.Append('"').Append(s, st, i - st).Append('"'); }
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ':') i++;
                o.Append(':');
                WriteValue(s, ref i, o);
            }
            o.Append('}');
            return;
        }
        if (c == '[')
        {
            i++; o.Append('['); var first = true;
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) break;
                if (s[i] == ']') { i++; break; }
                if (s[i] == ',') { i++; continue; }
                if (!first) o.Append(',');
                first = false;
                WriteValue(s, ref i, o);
            }
            o.Append(']');
            return;
        }
        // số / true / false / null / undefined / định danh khác
        var start = i;
        while (i < s.Length && !(s[i] is ',' or '}' or ']' or ')') && !char.IsWhiteSpace(s[i])) i++;
        var tok = s[start..i];
        if (tok is "true" or "false" or "null") o.Append(tok);
        else if (tok == "undefined" || tok.Length == 0) o.Append("null");
        else if (double.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) o.Append(tok);
        else { o.Append('"').Append(tok.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); }
    }

    private static void WriteString(string s, ref int i, StringBuilder o)
    {
        var q = s[i++];
        o.Append('"');
        while (i < s.Length && s[i] != q)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                var n = s[i + 1];
                if (n == '\'') { o.Append('\''); i += 2; continue; }
                o.Append(c).Append(n); i += 2; continue;   // \" \\ \n \uXXXX ... giữ nguyên (JSON hiểu)
            }
            if (c == '"') o.Append("\\\"");
            else if (c == '\n') o.Append("\\n");
            else if (c == '\r') o.Append("\\r");
            else if (c == '\t') o.Append("\\t");
            else o.Append(c);
            i++;
        }
        i++;   // dấu nháy đóng
        o.Append('"');
    }
}

/// <summary>Nơi đăng ký tất cả trang native (thêm 1 dòng cho mỗi trang mới).</summary>
internal static class NativePageCatalog
{
    public static void RegisterAll()
    {
        // (đăng ký ở các file NativePages.*.cs thông qua phương thức partial — xem bên dưới)
        Pages.Register();
    }
}
