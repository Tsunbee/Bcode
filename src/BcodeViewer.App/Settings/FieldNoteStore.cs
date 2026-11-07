using System.Text.Json;
using System.Text.Json.Serialization;
using BcodeViewer.App.Host.FastSource;

namespace BcodeViewer.App.Settings;

/// <summary>
/// Note thuộc tính field (Tool → Note thuộc tính field…): mô tả tiếng Việt + giá trị hợp lệ của từng thuộc tính khai báo trên
/// <c>&lt;field …&gt;</c> / <c>&lt;items …&gt;</c> — hiện khi gõ thuộc tính (completion.js) và khi rê chuột (Web/fieldnotes.js).
/// <list type="bullet">
/// <item><b>Định nghĩa của Fast</b> (chỉ đọc) — Assets\fieldnotes\fast-field-notes.json đi kèm exe, soạn từ thống kê source Fast
/// (Templates\fileSource + bộ source FBO-FBI) và form khai báo field của FCode.</item>
/// <item><b>Note của người dùng</b> — <c>&lt;Thư mục kiến thức&gt;\viewer\field-notes.json</c> (thư mục kiến thức chung của nhóm nếu có,
/// không thì %AppData%\Bcode\viewer\): thêm / sửa / xoá ở hộp Note; cùng thẻ + thuộc tính + loại file thì thắng định nghĩa của Fast.</item>
/// </list>
/// </summary>
public static class FieldNoteStore
{
    public sealed class Value
    {
        [JsonPropertyName("v")] public string V { get; set; } = "";
        [JsonPropertyName("d")] public string D { get; set; } = "";
    }

    public sealed class Note
    {
        /// <summary>Thẻ: "field" | "items" | …</summary>
        [JsonPropertyName("tag")] public string Tag { get; set; } = "field";
        [JsonPropertyName("attr")] public string Attr { get; set; } = "";
        /// <summary>Loại file áp dụng: "" = mọi loại; "grid" | "dir" | "filter" | "lookup" | "report" | "import" | "clipboard".</summary>
        [JsonPropertyName("scope")] public string Scope { get; set; } = "";
        [JsonPropertyName("desc")] public string Desc { get; set; } = "";
        [JsonPropertyName("values")] public List<Value> Values { get; set; } = new();
        [JsonPropertyName("example")] public string Example { get; set; } = "";
        /// <summary>Số lần gặp trong source Fast (chỉ định nghĩa của Fast) — xếp thứ tự gợi ý.</summary>
        [JsonPropertyName("count")] public int Count { get; set; }
    }

    private sealed class FileModel
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("notes")] public List<Note> Notes { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string BuiltinPath => Path.Combine(AppContext.BaseDirectory, "Assets", "fieldnotes", "fast-field-notes.json");
    public static string UserPath => ViewerKnowledge.Viewer("field-notes.json");

    private static List<Note>? _builtin;

    private static List<Note> Builtin()
    {
        if (_builtin is { } b) return b;
        try { _builtin = File.Exists(BuiltinPath) ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(BuiltinPath), Opts)?.Notes ?? new() : new(); }
        catch { _builtin = new(); }
        return _builtin;
    }

    private static List<Note> ReadUser(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Opts)?.Notes ?? new() : new(); }
        catch { return new(); }
    }

    /// <summary>{path, builtin:[…], user:[…]} cho trang (Web/fieldnotes.js giữ trong bộ nhớ, tra mỗi phím không gọi host).</summary>
    public static string GetJson()
    {
        var path = UserPath;
        return JsonSerializer.Serialize(new { path, builtin = Builtin(), user = ReadUser(path) }, Opts);
    }

    /// <summary>Ghi đè toàn bộ note của người dùng ({notes:[…]} hoặc […]). Bỏ dòng không có tên thuộc tính; trùng thẻ + thuộc tính + loại thì giữ dòng sau.</summary>
    public static string Save(string json)
    {
        List<Note> notes;
        using (var doc = JsonDocument.Parse(json))
        {
            var arr = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement
                : doc.RootElement.TryGetProperty("notes", out var n) ? n : default;
            notes = arr.ValueKind == JsonValueKind.Array ? JsonSerializer.Deserialize<List<Note>>(arr.GetRawText(), Opts) ?? new() : new();
        }
        var clean = new Dictionary<string, Note>(StringComparer.OrdinalIgnoreCase);
        foreach (var x in notes)
        {
            x.Tag = (x.Tag ?? "").Trim(); x.Attr = (x.Attr ?? "").Trim(); x.Scope = (x.Scope ?? "").Trim().ToLowerInvariant();
            x.Desc = (x.Desc ?? "").Trim(); x.Example = (x.Example ?? "").Trim(); x.Count = 0;
            x.Values = (x.Values ?? new()).Where(v => !string.IsNullOrWhiteSpace(v.V)).Select(v => new Value { V = v.V.Trim(), D = (v.D ?? "").Trim() }).ToList();
            if (x.Attr.Length == 0) continue;
            if (x.Tag.Length == 0) x.Tag = "field";
            clean[x.Tag + "|" + x.Attr + "|" + x.Scope] = x;
        }
        var path = UserPath;
        ViewerKnowledge.WriteAtomic(path, JsonSerializer.Serialize(new FileModel { Notes = clean.Values.ToList() }, Opts));
        return path;
    }
}
