using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BcodeViewer.App.Settings;

namespace BcodeViewer.App.Host;

/// <summary>
/// Thin wrapper over the Anthropic Messages API (POST /v1/messages) for the AI chat panel.
/// v1 is a single non-streaming call — the reply renders once it's fully back, rather than
/// a typing effect; streaming is a natural follow-up once this plumbing is proven, using
/// CoreWebView2.PostWebMessageAsJson to push incremental chunks to the page.
/// </summary>
public class ClaudeChatService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private readonly ViewerSettings _settings;

    public ClaudeChatService(ViewerSettings settings) => _settings = settings;

    /// <summary>
    /// <paramref name="fileContext"/> is the active tab's content (and path), sent as a system
    /// prompt so the model can answer questions about "this file" without the user having to
    /// paste it in manually. Returns a plain-text error message (not an exception) on failure
    /// so the chat panel can show it inline instead of the call just failing silently.
    /// </summary>
    public async Task<string> AskAsync(string userPrompt, string? fileContext, string? filePath)
    {
        if (string.IsNullOrWhiteSpace(_settings.AnthropicApiKey))
            return "Chưa cấu hình Anthropic API key — vào File > Settings để thêm.";

        var systemPrompt = fileContext is null
            ? "You are a coding assistant embedded in BcodeViewer, an editor for FastBusiness ERP source files (XML-based Dir/Grid controllers, .f scripts, JavaScript)."
            : $"You are a coding assistant embedded in BcodeViewer, an editor for FastBusiness ERP source files. " +
              $"The user currently has this file open ({filePath}):\n\n```\n{fileContext}\n```";

        var requestBody = new
        {
            model = _settings.Model,
            max_tokens = 2048,
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userPrompt } }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", _settings.AnthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return $"Lỗi gọi Claude API ({(int)response.StatusCode}): {ExtractErrorMessage(responseText)}";

            using var doc = JsonDocument.Parse(responseText);
            var parts = doc.RootElement.GetProperty("content").EnumerateArray()
                .Where(p => p.GetProperty("type").GetString() == "text")
                .Select(p => p.GetProperty("text").GetString() ?? "");
            return string.Join("\n", parts);
        }
        catch (Exception ex)
        {
            return $"Không gọi được Claude API: {ex.Message}";
        }
    }

    private static string ExtractErrorMessage(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            return doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var msg)
                ? msg.GetString() ?? responseBody
                : responseBody;
        }
        catch
        {
            return responseBody;
        }
    }
}
