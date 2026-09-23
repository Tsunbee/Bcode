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
            using var response = await Http.SendAsync(request).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Fill-in-the-middle completion for the editor's ghost text (see completion.js's inline
    /// provider). Differs from <see cref="AskAsync"/> in every way that matters on the typing
    /// path, which is why it's a separate method rather than a parameter on that one:
    ///
    ///  * <see cref="ViewerSettings.CompletionModel"/> (Haiku) instead of the chat model —
    ///    this has to land before the user finishes their next word.
    ///  * A hard 96-token ceiling. Ghost text that runs on past the current line is noise:
    ///    nobody reads a 30-line suggestion they didn't ask for, and it's billed either way.
    ///  * Cancellable. The caller cancels the previous request on every keystroke (see
    ///    EditorBridge.BeginInlineCompletion) — without that, typing a word queues one
    ///    in-flight call per character and pays for all of them.
    ///  * Returns "" on ANY failure, including a missing API key. An error string would be
    ///    inserted into the user's file as a suggestion; silence is the only safe failure
    ///    mode here. Configuration problems surface in the chat panel instead.
    ///
    /// <paramref name="suffix"/> (the text AFTER the caret) matters as much as the prefix:
    /// without it the model re-suggests a closing tag that's already three lines below.
    /// </summary>
    public async Task<string> CompleteAsync(string prefix, string suffix, string? filePath, string? regionHint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.AnthropicApiKey)) return "";
        if (string.IsNullOrWhiteSpace(prefix) && string.IsNullOrWhiteSpace(suffix)) return "";

        // Window the context rather than sending whole files: an FCode Dir controller runs
        // to thousands of lines, and what's useful for the next line or two is what's
        // immediately around the caret — the rest is latency and tokens.
        const int prefixBudget = 6000;
        const int suffixBudget = 2000;
        var head = prefix.Length > prefixBudget ? prefix[^prefixBudget..] : prefix;
        var tail = suffix.Length > suffixBudget ? suffix[..suffixBudget] : suffix;

        var systemPrompt =
            "You complete code inside BcodeViewer, an editor for FastBusiness ERP source files " +
            "(XML-based Dir/Grid/Report controllers, .f scripts, SQL, JavaScript).\n" +
            "The user's caret is at <CURSOR>. Reply with ONLY the raw text to insert at that point.\n" +
            "Rules: no explanation, no markdown fences, no repeating text that already appears " +
            "before or after the cursor. Continue at most one statement, tag, or line unless the " +
            "construct obviously needs closing. Match the surrounding indentation and naming style. " +
            "If nothing sensible follows, reply with nothing at all.";

        // Naming the language at the caret matters more here than anywhere else: an FCode
        // controller is one .xml file whose <script> block is JavaScript and whose query
        // blocks are T-SQL, so "File: ....xml" on its own points the model at the wrong
        // language for most of the places someone actually types.
        var regionLine = regionHint switch
        {
            "js" => "The cursor is inside a <script> block — continue JavaScript.\n",
            "sql" => "The cursor is inside a SQL block — continue T-SQL (SQL Server).\n",
            "css" => "The cursor is inside a <style> block — continue CSS.\n",
            "xml" => "The cursor is in the XML markup itself — continue FCode XML.\n",
            _ => "",
        };

        var userContent =
            (filePath is null ? "" : $"File: {filePath}\n") +
            regionLine +
            $"\n{head}<CURSOR>{tail}";

        var requestBody = new
        {
            model = string.IsNullOrWhiteSpace(_settings.CompletionModel) ? "claude-haiku-4-5-20251001" : _settings.CompletionModel,
            max_tokens = 96,
            temperature = 0.0, // a suggestion that changes each time it's re-triggered is worse than none
            system = systemPrompt,
            messages = new[] { new { role = "user", content = userContent } }
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", _settings.AnthropicApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return "";

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(responseText);
            var text = string.Join("", doc.RootElement.GetProperty("content").EnumerateArray()
                .Where(p => p.GetProperty("type").GetString() == "text")
                .Select(p => p.GetProperty("text").GetString() ?? ""));

            return CleanCompletion(text);
        }
        catch
        {
            // Cancelled (the normal case — the user kept typing), offline, rate-limited,
            // malformed response: all the same outcome, which is no ghost text.
            return "";
        }
    }

    /// <summary>
    /// Strips what the model adds despite being told not to. A fenced block inserted
    /// verbatim would put ``` into the user's source file, so this is not cosmetic.
    /// </summary>
    private static string CleanCompletion(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var cleaned = text.Replace("\r\n", "\n");
        if (cleaned.TrimStart().StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline >= 0) cleaned = cleaned[(firstNewline + 1)..];
            var fenceEnd = cleaned.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0) cleaned = cleaned[..fenceEnd];
        }

        // Only trailing whitespace goes: leading whitespace can be the indentation the
        // suggestion is supposed to carry, and trimming it would jam the text against the
        // caret. A suggestion that is nothing but whitespace is not worth showing.
        cleaned = cleaned.TrimEnd();
        return cleaned.Trim().Length == 0 ? "" : cleaned;
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
