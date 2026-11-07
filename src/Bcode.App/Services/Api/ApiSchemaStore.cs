using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bcode.App.Services.Api;

/// <summary>
/// Dữ liệu của "Tạo cấu trúc API" (ApiSchemaBuilderForm) lưu theo dự án tại %AppData%\Bcode\ApiSchemas\&lt;workspace&gt;.json — đóng form mở lại
/// vẫn còn các API đã tạo. Tên thuộc tính JSON camelCase = đúng model của trang apischema.html (bản Web gửi / nhận nguyên khối).
/// </summary>
public sealed class ApiSchemaState
{
    /// <summary>Xuất Word không có bảng / cột database nguồn (gửi đối tác): mỗi trường mô tả bằng tên tiếng Việt.</summary>
    public bool HideSource { get; set; }
    public int Active { get; set; }
    public List<ApiSchemaApi> Apis { get; set; } = new();
}

public sealed class ApiSchemaApi
{
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    /// <summary>Tên hàm API — tự đặt "Set" + controller Dir của bảng đầu tiên (<see cref="FuncAuto"/>); sửa tay thì không tự đổi nữa.</summary>
    public string Func { get; set; } = "";
    public bool FuncAuto { get; set; } = true;
    public string View { get; set; } = "";
    public List<ApiSchemaTable> Tables { get; set; } = new();
    public List<ApiSchemaColumn> Columns { get; set; } = new();
}

public sealed class ApiSchemaTable
{
    public string Db { get; set; } = "0";
    public string Table { get; set; } = "";
    /// <summary>Controller Dir của bảng tìm được trong source dự án ("" = chưa tìm / không có).</summary>
    public string Controller { get; set; } = "";
}

public sealed class ApiSchemaColumn
{
    public bool Selected { get; set; } = true;
    public string ColName { get; set; } = "";
    public string DataType { get; set; } = "";
    public bool IsPk { get; set; }
    public bool Nullable { get; set; } = true;
    public bool Identity { get; set; }
    public bool Required { get; set; }
    public string FieldDesc { get; set; } = "";
    public string LogicDesc { get; set; } = "";
    /// <summary>Tên tiếng Việt của trường — lấy từ tiêu đề field ở controller Dir của bảng (hoặc từ điển field FCode); sửa tay được.</summary>
    public string ViName { get; set; } = "";
    /// <summary>true = người dùng đã sửa tay tên tiếng Việt → không tự điền đè.</summary>
    public bool ViUser { get; set; }
    public string Tkey { get; set; } = "";
    public string Tname { get; set; } = "";
    public string Tdb { get; set; } = "";
    public string? PrevField { get; set; }
}

public static class ApiSchemaStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        // prevField vắng mặt (không phải null) thì trang mới coi là "chưa đổi camelCase".
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FilePath(string workspace)
    {
        var safe = string.IsNullOrWhiteSpace(workspace) ? "_no_workspace" : workspace.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
        return Path.Combine(BcodePaths.AppData, "Bcode", "ApiSchemas", safe + ".json");
    }

    /// <summary>null = chưa lưu lần nào (hoặc file hỏng).</summary>
    public static ApiSchemaState? Load(string workspace)
    {
        try
        {
            var p = FilePath(workspace);
            if (!File.Exists(p)) return null;
            var s = JsonSerializer.Deserialize<ApiSchemaState>(File.ReadAllText(p), Json);
            return s is { Apis.Count: > 0 } ? s : null;
        }
        catch { return null; }
    }

    public static void Save(string workspace, ApiSchemaState state)
    {
        var p = FilePath(workspace);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var tmp = p + ".tmp";   // ghi file tạm rồi thay: tắt ngang không hỏng bản đang có
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, p, overwrite: true);
    }

    /// <summary>"Set" + controller (bỏ ký tự không hợp lệ cho tên hàm).</summary>
    public static string FuncName(string controllerOrTable)
    {
        var n = controllerOrTable ?? "";
        if (n.LastIndexOf('.') is var dot and >= 0) n = n[(dot + 1)..];
        n = new string(n.Replace("[", "").Replace("]", "").Where(ch => char.IsLetterOrDigit(ch) || ch == '_').ToArray());
        return n.Length == 0 ? "" : "Set" + n;
    }

    /// <summary>Controller Dir + tên tiếng Việt từng cột của 1 bảng: tiêu đề field ở controller của source dự án (Workspace.SourcePath),
    /// cột không có trong controller thì lấy header.xml (từ điển field FCode). Source chưa khai / chưa đọc xong trong 20 giây → chỉ từ điển.</summary>
    public static async Task<(string Controller, string[] Others, Dictionary<string, string> Vi)> TableInfoAsync(string? sourcePath, string table, IEnumerable<string> columns)
    {
        var vi = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string controller = ""; string[] others = Array.Empty<string>();
        IReadOnlyDictionary<string, string>? heads = null;
        if (!string.IsNullOrWhiteSpace(sourcePath))
        {
            try
            {
                var load = SourceFieldIndex.GetAsync(sourcePath);
                if (await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(20))) == load)
                {
                    var list = load.Result.ForTable(table);
                    if (list.Count > 0)
                    {
                        controller = list[0].Controller; heads = list[0].HeadersV;
                        others = list.Skip(1).Select(c => c.Controller).ToArray();
                    }
                }
            }
            catch { /* source không đọc được — dùng từ điển */ }
        }
        foreach (var col in columns)
        {
            if (heads is not null && heads.TryGetValue(col, out var v) && v.Length > 0) vi[col] = v;
            else if (FieldDictionaryService.Instance.Header(col) is { V.Length: > 0 } h) vi[col] = h.V;
        }
        return (controller, others, vi);
    }
}
