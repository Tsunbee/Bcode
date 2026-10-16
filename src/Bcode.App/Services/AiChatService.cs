using System.Text;
using System.Text.Json;

namespace Bcode.App.Services;

/// <summary>
/// Gọi AI 1 lượt (không stream): Claude (Anthropic Messages API) hoặc Gemini (generateContent) — system prompt + 1 tin người dùng,
/// trả về chữ AI viết. Dùng chung cho Ctrl+I của SQL Query (<c>RawSqlControl</c>) và "AI tạo danh mục" (<see cref="AiCatalogService"/>).
/// Lỗi API ném <see cref="InvalidOperationException"/> kèm câu báo lỗi của API; hết giờ chờ ném <see cref="TaskCanceledException"/>.
/// </summary>
public static class AiChatService
{
    public const string DefaultClaudeModel = "claude-sonnet-5-5";
    private const string GeminiModel = "gemini-3.6-flash";

    // Không đặt Timeout ở client: mỗi lượt gọi tự hẹn giờ (Ctrl+I 90s, sinh spec danh mục lâu hơn vì trả JSON dài).
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<string> ClaudeAsync(string apiKey, string? model, string system, string user,
        int maxTokens = 4096, TimeSpan? timeout = null)
    {
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(model) ? DefaultClaudeModel : model,
            max_tokens = maxTokens,
            system,
            messages = new[] { new { role = "user", content = user } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(90));
        var res = await Http.SendAsync(req, cts.Token);
        var body = await res.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        if (!res.IsSuccessStatusCode)
        {
            var msg = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : res.ReasonPhrase;
            throw new InvalidOperationException($"Lỗi Claude API ({(int)res.StatusCode}): {msg}");
        }
        var text = "";
        if (doc.RootElement.TryGetProperty("content", out var blocks))
            foreach (var b in blocks.EnumerateArray())
                if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var tx))
                    text += tx.GetString();
        return text;
    }

    public static async Task<string> GeminiAsync(string apiKey, string system, string user,
        int maxTokens = 8192, TimeSpan? timeout = null)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{GeminiModel}:generateContent?key={apiKey}";
        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
            generationConfig = new { temperature = 0.2, maxOutputTokens = maxTokens },
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(90));
        var res = await Http.PostAsync(url, content, cts.Token);
        var body = await res.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        if (!res.IsSuccessStatusCode)
        {
            var msg = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : res.ReasonPhrase;
            throw new InvalidOperationException($"Lỗi Gemini API ({(int)res.StatusCode}): {msg}");
        }
        var text = "";
        if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0
            && cands[0].TryGetProperty("content", out var c) && c.TryGetProperty("parts", out var parts))
            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out var tx)) text += tx.GetString();
        if (text.Length == 0) throw new InvalidOperationException("Gemini không trả về nội dung (có thể bị chặn bởi bộ lọc an toàn hoặc hết quota).");
        return text;
    }
}
