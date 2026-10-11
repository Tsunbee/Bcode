using System.Text.Json;
using System.Text.Json.Nodes;
using Bcode.App.Models;

namespace Bcode.App.Services.Api;

/// <summary>
/// Mẫu chuẩn phòng LT3 — đọc từ Templates/Api/lt3-standard.json, sinh từ tài liệu http://172.168.5.14/developers/docs/ (SyncData 8 form,
/// SyncVoucher 7 form, GetData 16 form; mỗi form có bảng trường: kiểu / bắt buộc / mô tả + request mẫu). KHÔNG chứa tài khoản / mật khẩu / token.
/// Tạo dự án mới từ mẫu: VoucherId → {{key}}, ngày chứng từ → {{today}} để gửi thử không trùng.
/// </summary>
public static class Lt3Templates
{
    public const string TemplateName = "Mẫu LT3 (chuẩn phòng)";

    public sealed record FieldDoc(string Name, string Type, bool Required, string Description);
    public sealed record SectionDoc(string Name, string Label, List<FieldDoc> Fields);
    public sealed record FormDoc(string Form, string Kind, string Endpoint, string Title, string Description, List<SectionDoc> Sections, string? Sample, string Doc);

    private static readonly JsonSerializerOptions Opts = new() { PropertyNameCaseInsensitive = true };
    private static (List<FormDoc> Forms, Dictionary<string, string> Errors)? _cache;

    public static string CatalogPath => Path.Combine(AppContext.BaseDirectory, "Templates", "Api", "lt3-standard.json");

    /// <summary>Danh mục form chuẩn (rỗng nếu thiếu file mẫu).</summary>
    public static List<FormDoc> Catalog() => Load().Forms;

    /// <summary>Mã lỗi chung của API LT3 (200, 201, 202, 400, 401, 403, 500, 601).</summary>
    public static Dictionary<string, string> ErrorCodes() => Load().Errors;

    public static FormDoc? Find(string form) => Catalog().FirstOrDefault(f => string.Equals(f.Form, form, StringComparison.OrdinalIgnoreCase));

    private static (List<FormDoc> Forms, Dictionary<string, string> Errors) Load()
    {
        if (_cache is { } c) return c;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(CatalogPath));
            var forms = doc.RootElement.GetProperty("forms").Deserialize<List<FormDoc>>(Opts) ?? new();
            var errors = doc.RootElement.TryGetProperty("errorCodes", out var e) ? e.Deserialize<Dictionary<string, string>>(Opts) ?? new() : new();
            _cache = (forms, errors);
        }
        catch { _cache = (new(), new()); }
        return _cache.Value;
    }

    /// <summary>Dự án mới theo chuẩn LT3 với các form được chọn (null = tất cả).</summary>
    public static ApiProject Create(string name = "", string projectId = "", string baseUrl = "", IEnumerable<string>? forms = null)
    {
        var pick = forms is null ? null : new HashSet<string>(forms, StringComparer.OrdinalIgnoreCase);
        return new ApiProject
        {
            Name = string.IsNullOrWhiteSpace(name) ? TemplateName : name,
            ProjectId = projectId,
            Department = "LT3",
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "http://172.168.5.14/<DU_AN>_API" : baseUrl,
            Note = "Chuẩn LT3: POST api/getToken {username, password} → token.accesstoken (hạn token.expires). Header Authorization: <token> (không \"Bearer\"). "
                 + "SyncData = đồng bộ danh mục, SyncVoucher = đồng bộ chứng từ (VoucherId duy nhất, ngày yyyy-MM-dd), GetData = lấy dữ liệu (Id hoặc dateFrom–dateTo).",
            Forms = Catalog().Where(f => pick is null || pick.Contains(f.Form)).Select(ToForm).ToList(),
        };
    }

    public static ApiForm ToForm(FormDoc d) => new()
    {
        Name = d.Form,
        Kind = d.Kind,
        Endpoint = d.Endpoint,
        Description = d.Title + (string.IsNullOrWhiteSpace(d.Description) ? "" : " — " + d.Description),
        Body = d.Sample is null ? "{\n    \"form\": \"" + d.Form + "\",\n    \"data\": []\n}" : Parameterize(d.Sample),
    };

    /// <summary>Body mẫu của tài liệu → body gửi thử được nhiều lần: VoucherId = {{key}} (mỗi lần 1 khoá mới), ngày chứng từ = {{today}}.</summary>
    private static string Parameterize(string sample)
    {
        try
        {
            var node = JsonNode.Parse(sample);
            if (node?["data"] is JsonArray arr)
                foreach (var it in arr.OfType<JsonObject>())
                {
                    if (it.ContainsKey("VoucherId")) it["VoucherId"] = "{{key}}";
                    foreach (var k in new[] { "VoucherDate", "Date" }) if (it.ContainsKey(k)) it[k] = "{{today}}";
                }
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? sample;
        }
        catch { return sample; }
    }
}
