using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BcodeViewer.App.Settings;

namespace BcodeViewer.App.Host;

/// <summary>
/// "Dịch caption (v → e)": dịch 1 lô caption tiếng Việt sang ngôn ngữ khác bằng engine chọn ở Settings
/// (<see cref="ViewerSettings.TranslateEngine"/>):
///   • google — endpoint translate.googleapis.com/translate_a/single?client=gtx (KHÔNG cần API key). Không chính thức:
///     có thể bị đổi/chặn hoặc trả 429 khi gọi dày, và dịch từng chuỗi không có ngữ cảnh (tên field) như AI;
///   • gemini / claude — qua API key tương ứng, kèm tên field làm ngữ cảnh và yêu cầu dùng thuật ngữ ERP.
/// Trả về MẢNG JSON các chuỗi dịch (đúng thứ tự, đúng số lượng) khi thành công; lỗi thì trả chuỗi bắt đầu bằng "Lỗi" —
/// trang hiện thẳng lên dialog.
/// </summary>
public class CaptionTranslator
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ViewerSettings _settings;
    private readonly ClaudeChatService _chat;

    public CaptionTranslator(ViewerSettings settings, ClaudeChatService chat)
    {
        _settings = settings;
        _chat = chat;
    }

    private sealed record Item(string Vi, string Field);

    public async Task<string> TranslateAsync(string payloadJson, string lang, CancellationToken token)
    {
        List<Item> items;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            items = doc.RootElement.EnumerateArray()
                .Select(e => new Item(
                    e.TryGetProperty("vi", out var v) ? v.GetString() ?? "" : "",
                    e.TryGetProperty("field", out var f) ? f.GetString() ?? "" : ""))
                .ToList();
        }
        catch (Exception ex) { return "Lỗi: dữ liệu caption không hợp lệ — " + ex.Message; }
        if (items.Count == 0) return "[]";

        lang = string.IsNullOrWhiteSpace(lang) ? "English" : lang.Trim();
        switch ((_settings.TranslateEngine ?? "google").ToLowerInvariant())
        {
            case "claude":
                if (string.IsNullOrWhiteSpace(_settings.AnthropicApiKey)) return "Lỗi: chưa có Anthropic API key (Settings) — hoặc đổi Engine dịch sang Google.";
                return ParseAiReply(await _chat.AskAsync(BuildPrompt(items, lang), null, null, null, token).ConfigureAwait(false), items.Count);
            case "gemini":
                return ParseAiReply(await _chat.AskGeminiAsync(BuildPrompt(items, lang), token).ConfigureAwait(false), items.Count);
            default:
                return await GoogleAsync(items, lang, token).ConfigureAwait(false);
        }
    }

    // ---- Claude / Gemini ---------------------------------------------------------------------

    private static string BuildPrompt(List<Item> items, string lang)
    {
        var payload = JsonSerializer.Serialize(items.Select(i => new { vi = i.Vi, field = i.Field }));
        return
            $"Dịch các caption/nhãn của phần mềm ERP (tên cột, nút, tiêu đề màn hình) từ tiếng Việt sang {lang}. " +
            "\"field\" là tên field trong code, chỉ để hiểu ngữ cảnh (vd dvt = đơn vị tính → UOM). " +
            "Dùng thuật ngữ kế toán/ERP chuẩn, ngắn gọn, viết hoa chữ cái đầu mỗi từ chính như tiêu đề cột. " +
            $"Chỉ trả về MỘT mảng JSON gồm đúng {items.Count} chuỗi, cùng thứ tự, không giải thích, không markdown.\n\n{payload}";
    }

    /// <summary>Lấy mảng JSON trong câu trả lời của AI (bỏ rào ```), kiểm đúng số phần tử; sai thì trả "Lỗi: ..." kèm đoạn đầu câu trả lời.</summary>
    private static string ParseAiReply(string reply, int expected)
    {
        if (string.IsNullOrWhiteSpace(reply)) return "Lỗi: AI không trả về nội dung.";
        var m = Regex.Match(reply, @"\[[\s\S]*\]");
        if (m.Success)
        {
            try
            {
                var arr = JsonSerializer.Deserialize<List<string?>>(m.Value);
                if (arr is not null && arr.Count == expected)
                    return JsonSerializer.Serialize(arr.Select(s => s ?? ""));
            }
            catch { /* rơi xuống thông báo lỗi bên dưới */ }
        }
        var text = reply.Trim();
        return "Lỗi: " + (text.Length > 300 ? text[..300] : text);
    }

    // ---- Google (không cần key) ---------------------------------------------------------------

    private static readonly Dictionary<string, string> LangCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["english"] = "en", ["tiếng anh"] = "en", ["anh"] = "en",
        ["vietnamese"] = "vi", ["tiếng việt"] = "vi",
        ["chinese"] = "zh-CN", ["tiếng trung"] = "zh-CN", ["trung"] = "zh-CN", ["chinese (traditional)"] = "zh-TW",
        ["japanese"] = "ja", ["tiếng nhật"] = "ja", ["korean"] = "ko", ["tiếng hàn"] = "ko",
        ["french"] = "fr", ["tiếng pháp"] = "fr", ["german"] = "de", ["tiếng đức"] = "de",
        ["spanish"] = "es", ["tiếng tây ban nha"] = "es", ["thai"] = "th", ["tiếng thái"] = "th",
        ["indonesian"] = "id", ["malay"] = "ms", ["russian"] = "ru", ["tiếng nga"] = "ru",
        ["khmer"] = "km", ["lao"] = "lo", ["portuguese"] = "pt", ["italian"] = "it", ["hindi"] = "hi", ["arabic"] = "ar",
    };

    private static string? ResolveLangCode(string lang)
    {
        if (LangCodes.TryGetValue(lang, out var code)) return code;
        return Regex.IsMatch(lang, @"^[A-Za-z]{2,3}(-[A-Za-z]{2,4})?$") ? lang : null; // người dùng nhập thẳng mã: en, ja, zh-TW...
    }

    /// <summary>Dịch cả lô bằng clients5.google.com/translate_a/t (nhiều tham số q → mảng kết quả cùng thứ tự). Trả null khi lỗi
    /// (HTTP khác 2xx, JSON lạ, số phần tử lệch) để caller thử đường khác.</summary>
    private static async Task<string[]?> GoogleBatchAsync(List<Item> items, string tl, CancellationToken token)
    {
        var results = new string[items.Count];
        var pending = Enumerable.Range(0, items.Count).Where(i => !string.IsNullOrWhiteSpace(items[i].Vi)).ToList();
        try
        {
            for (var start = 0; start < pending.Count;)
            {
                var chunk = new List<int>();
                var length = 0;
                while (start < pending.Count && chunk.Count < 20)
                {
                    var q = Uri.EscapeDataString(items[pending[start]].Vi);
                    if (chunk.Count > 0 && length + q.Length > 3000) break;
                    chunk.Add(pending[start++]);
                    length += q.Length + 3;
                }

                var url = "https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=vi&tl=" + Uri.EscapeDataString(tl) +
                          string.Concat(chunk.Select(i => "&q=" + Uri.EscapeDataString(items[i].Vi)));
                using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() != chunk.Count) return null;
                for (var k = 0; k < chunk.Count; k++)
                {
                    var el = doc.RootElement[k];
                    if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() > 0) el = el[0]; // dạng ["bản dịch","vi"] khi tự nhận ngôn ngữ
                    if (el.ValueKind != JsonValueKind.String) return null;
                    results[chunk[k]] = (el.GetString() ?? "").Trim();
                }
            }
            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<string> GoogleAsync(List<Item> items, string lang, CancellationToken token)
    {
        var tl = ResolveLangCode(lang);
        if (tl is null) return $"Lỗi: Google Translate không nhận ra ngôn ngữ \"{lang}\" — nhập tên tiếng Anh (English, Japanese...) hoặc mã (en, ja, zh-CN).";

        // Cách 1: endpoint clients5 (client=dict-chrome-ex, của tiện ích từ điển Chrome) — dịch được nhiều caption trong 1 request
        // và thường vẫn chạy khi endpoint gtx bên dưới bị 429. Cách 2 (gtx) chỉ dùng khi cách 1 lỗi.
        var batched = await GoogleBatchAsync(items, tl, token).ConfigureAwait(false);
        if (batched is not null) return JsonSerializer.Serialize(batched);

        var results = new string[items.Count];
        string? error = null;
        using var gate = new SemaphoreSlim(4);
        var tasks = items.Select(async (item, i) =>
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (error is not null) return;
                if (string.IsNullOrWhiteSpace(item.Vi)) { results[i] = ""; return; }
                var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=vi&tl=" + Uri.EscapeDataString(tl) +
                          "&dt=t&q=" + Uri.EscapeDataString(item.Vi);
                using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    error = response.StatusCode == (System.Net.HttpStatusCode)429
                        ? "Lỗi: Google Translate đang giới hạn tốc độ (429) — đợi một lúc rồi dịch lại, hoặc đổi Engine dịch trong Settings."
                        : $"Lỗi gọi Google Translate ({(int)response.StatusCode}).";
                    return;
                }
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                var sb = new StringBuilder();
                foreach (var seg in doc.RootElement[0].EnumerateArray())
                    if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0 && seg[0].ValueKind == JsonValueKind.String)
                        sb.Append(seg[0].GetString());
                results[i] = sb.ToString().Trim();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error ??= "Lỗi gọi Google Translate: " + ex.Message; }
            finally { gate.Release(); }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return error ?? JsonSerializer.Serialize(results);
    }
}
