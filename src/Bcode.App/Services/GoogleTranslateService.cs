using System.Text;
using System.Text.Json;

namespace Bcode.App.Services;

/// <summary>
/// Dịch tiếng Việt sang ngôn ngữ khác bằng endpoint Google Translate (không cần API key). Dùng chung cho form tạo command (dịch Bar → Bar2)
/// và "Tạo nhanh danh mục" (dịch Title / Subtitle).
/// </summary>
public static class GoogleTranslateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Trả (bản dịch, lỗi): lỗi null khi dịch được.</summary>
    public static async Task<(string? Result, string? Error)> TranslateAsync(string text, string lang = "en")
    {
        string? result = null, error = null;
        var tl = string.IsNullOrWhiteSpace(lang) ? "en" : lang;
        // Cách 1: clients5 (client=dict-chrome-ex, của tiện ích từ điển Chrome) — hay chạy khi gtx bị 429. Cách 2: gtx.
        try
        {
            using var resp = await Http.GetAsync("https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=vi&tl=" + Uri.EscapeDataString(tl) + "&q=" + Uri.EscapeDataString(text));
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var el = doc.RootElement;
                if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0)
                {
                    el = el[0];
                    if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0) el = el[0];   // dạng ["bản dịch","vi"]
                    if (el.ValueKind == JsonValueKind.String) result = (el.GetString() ?? "").Trim();
                }
            }
        }
        catch { /* thử đường gtx bên dưới */ }
        if (string.IsNullOrEmpty(result))
        {
            try
            {
                using var resp = await Http.GetAsync("https://translate.googleapis.com/translate_a/single?client=gtx&sl=vi&tl=" + Uri.EscapeDataString(tl) + "&dt=t&q=" + Uri.EscapeDataString(text));
                if (!resp.IsSuccessStatusCode)
                    error = resp.StatusCode == (System.Net.HttpStatusCode)429 ? "Google Dịch đang giới hạn tốc độ (429) — đợi vài phút rồi dịch lại, hoặc tự nhập bản dịch." : $"Lỗi gọi Google Dịch ({(int)resp.StatusCode}).";
                else
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    var sb = new StringBuilder();
                    foreach (var seg in doc.RootElement[0].EnumerateArray())
                        if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String) sb.Append(seg[0].GetString());
                    result = sb.ToString().Trim();
                    if (result.Length == 0) error = "Google Dịch không trả về kết quả.";
                }
            }
            catch (Exception ex) { error = "Không dịch được (kiểm tra mạng): " + ex.Message; }
        }
        return (result, error);
    }
}
