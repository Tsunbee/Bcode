using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BcodeViewer.App.Settings;

namespace BcodeViewer.App.Host;

/// <summary>
/// Thin wrapper over the Anthropic Messages API (POST /v1/messages), serving both the chat
/// panel and the editor's ghost text.
///
/// Both calls stream, for opposite reasons. The chat panel streams to RENDER: the answer
/// types itself out instead of the panel showing "..." until the last token. Ghost text
/// streams to HANG UP: Monaco's inline provider returns once so there is nowhere to put a
/// partial suggestion, but reading the response incrementally means a model that keeps
/// going past a useful suggestion can be cut off mid-generation rather than waited on and
/// paid for in full (see ShouldStopCompletion).
///
/// Both also carry a prompt-cache breakpoint over the part of the prompt that does not
/// change between calls — the open file for chat, the head of the file for completion.
/// </summary>
public class ClaudeChatService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    /// <summary>How long a ghost-text request may run before it is abandoned. The shared
    /// <see cref="Http"/> timeout is the chat panel's, where 90s is reasonable; here the
    /// caret has moved on long before that and a late answer is only a bill.</summary>
    private static readonly TimeSpan CompletionDeadline = TimeSpan.FromSeconds(4);

    /// <summary>Characters of file head that must be typed before the cached block is
    /// re-cut. Larger = fewer cache misses, but a wider band of recent text that reaches
    /// the model only through the (much shorter) live window.</summary>
    private const int StableStep = 3000;
    private readonly ViewerSettings _settings;

    public ClaudeChatService(ViewerSettings settings) => _settings = settings;
    /// <summary>Chỉ để debug: báo lại kết quả thật của mỗi lần gọi CompleteAsync, vì hàm này
    /// luôn trả về "" khi lỗi (để không chèn text lỗi vào file người dùng).</summary>
    public event Action<string>? Diagnostic;
    /// <summary>
    /// <paramref name="fileContext"/> is the active tab's content (and path), sent as a system
    /// prompt so the model can answer questions about "this file" without the user having to
    /// paste it in manually. Returns a plain-text error message (not an exception) on failure
    /// so the chat panel can show it inline instead of the call just failing silently.
    /// </summary>
    /// <param name="onDelta">Called with each fragment as it arrives, so the panel can type
    /// the answer out instead of showing "..." for ten seconds. Optional: the return value
    /// is still the complete reply, so a caller that passes null behaves exactly as before.
    /// An error message is NOT streamed — it is only ever the return value — so the panel
    /// never ends up with half an answer followed by an apology.</param>
    public async Task<string> AskAsync(
        string userPrompt, string? fileContext, string? filePath,
        Action<string>? onDelta = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.AnthropicApiKey))
        {
            Diagnostic?.Invoke("AI: chưa có API key");
            return "";
        }

        var systemPrompt = fileContext is null
            ? "You are a coding assistant embedded in BcodeViewer, an editor for FastBusiness ERP source files (XML-based Dir/Grid controllers, .f scripts, JavaScript)."
            : $"You are a coding assistant embedded in BcodeViewer, an editor for FastBusiness ERP source files. " +
              $"The user currently has this file open ({filePath}):\n\n```\n{fileContext}\n```";

        var requestBody = new
        {
            model = _settings.Model,
            max_tokens = 2048,
            // The open file is the bulk of this prompt and does not change between questions
            // about it, so the same cache breakpoint that pays for itself on the typing path
            // pays for itself across a conversation too.
            system = new[]
            {
                new { type = "text", text = systemPrompt, cache_control = new { type = "ephemeral" } },
            },
            messages = new[] { new { role = "user", content = userPrompt } },
            stream = true,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        request.Headers.Add("x-api-key", _settings.AnthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        try
        {
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return $"Lỗi gọi Claude API ({(int)response.StatusCode}): {ExtractErrorMessage(errorBody)}";
            }

            var (text, _) = await ReadStreamAsync(response, onDelta, null, cancellationToken).ConfigureAwait(false);
            return text;
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
    public async Task<string> CompleteAsync(
        string prefix, string suffix, string? filePath, string? regionHint, string? projectFacts,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.AnthropicApiKey))
        {
            Diagnostic?.Invoke("AI: chưa có API key");
            return "";
        }
        if (string.IsNullOrWhiteSpace(prefix) && string.IsNullOrWhiteSpace(suffix))
        {
            Diagnostic?.Invoke("AI: không có ngữ cảnh quanh con trỏ");
            return "";
        }

        // Báo ngay khi bắt đầu, không chỉ khi xong: nếu không thì "request chưa từng được
        // gửi" và "đã gửi mà không có kết quả" nhìn từ ngoài giống hệt nhau.
        Diagnostic?.Invoke("AI: đang hỏi…");

        // Context is sent in two pieces so the larger one can be CACHED (see the request
        // body below). Everything from the start of the file up to a quantised boundary is
        // the "stable head": it is byte-identical across keystrokes and only changes once
        // per StableStep characters typed, which is exactly what a cache breakpoint needs.
        // The rest — the live window around the caret — is the only part re-billed at full
        // price per request.
        //
        // The stable head is also why the window can be generous now: an FCode controller's
        // DOCTYPE (the whole ENTITY table, i.e. what &ZVCReferenceGridTranFields; and
        // friends actually mean) sits at the very top of the file, thousands of lines above
        // where anyone types. Before caching, sending it was unaffordable per keystroke;
        // cached, it costs a tenth as much and is the single most useful context there is.
        const int stableBudget = 24000;  // how much of the file head travels as the cached block
        const int liveBudget = 3000;     // fresh context immediately before the caret
        const int suffixBudget = 2000;
        var tail = suffix.Length > suffixBudget ? suffix[..suffixBudget] : suffix;

        var (stableHead, liveHead) = SplitForCache(prefix, stableBudget, liveBudget);

        var systemPrompt = CompletionSystemPrompt(regionHint);

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

        // Three content blocks, with a cache breakpoint on each of the first two. A cached
        // prefix is cumulative, so two breakpoints means the facts stay cached even on the
        // call where the file head has just moved past its boundary — the two change on
        // quite different schedules, and one breakpoint would throw both away together.
        var blocks = new List<object>();
        if (!string.IsNullOrWhiteSpace(projectFacts))
        {
            blocks.Add(new
            {
                type = "text",
                text = "PROJECT FACTS — what the editor knows about this document. These names are " +
                       "real; prefer them over anything you would guess.\n\n" + projectFacts,
                cache_control = new { type = "ephemeral" },
            });
        }
        if (stableHead.Length > 0)
        {
            blocks.Add(new
            {
                type = "text",
                text = (filePath is null ? "" : $"File: {filePath}\n") +
                       "Beginning of the file, for context:\n" + stableHead,
                cache_control = new { type = "ephemeral" },
            });
        }
        blocks.Add(new
        {
            type = "text",
            text = (stableHead.Length > 0 || filePath is null ? "" : $"File: {filePath}\n") +
                   regionLine +
                   $"\n{liveHead}<CURSOR>{tail}",
        });

        var model = string.IsNullOrWhiteSpace(_settings.CompletionModel)
            ? "claude-haiku-4-5-20251001"
            : _settings.CompletionModel;
        var requestBody = BuildCompletionRequest(model, systemPrompt, blocks, prefix);

        // A ghost-text request that has not landed within a few seconds is worthless — the
        // caret has moved on — but the shared HttpClient's 90s timeout belongs to the chat
        // panel, where a long answer is normal. Linked, so a keystroke still cancels it
        // immediately through the caller's token.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(CompletionDeadline);
        var cancel = deadline.Token;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", _settings.AnthropicApiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            // ResponseHeadersRead: the body is read as it arrives, which is what makes an
            // early break actually cut the connection short instead of just discarding text
            // that has already been downloaded.
            using var response = await Http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
                Diagnostic?.Invoke($"AI: lỗi {(int)response.StatusCode} — {ExtractErrorMessage(errorBody)}");
                return "";
            }

            var (text, note) = await ReadStreamAsync(response, null, ShouldStopCompletion, cancel)
                .ConfigureAwait(false);

            var cleaned = CleanCompletion(text);
            Diagnostic?.Invoke((string.IsNullOrEmpty(cleaned)
                ? "AI: gọi thành công, nhưng không có gợi ý"
                : $"AI: đã gợi ý \"{(cleaned.Length > 70 ? cleaned[..70] + "…" : cleaned)}\"") + note);
            return cleaned;
        }
        catch (OperationCanceledException)
        {
            // Bình thường: người dùng gõ tiếp nên request cũ bị hủy — không cần báo.
            return "";
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke("AI: lỗi — " + ex.Message);
            return "";
        }
    }

    /// <summary>
    /// The rules of the language at the caret, which nothing else in the prompt states.
    ///
    /// Không có phần này thì model không biết rằng một field thiếu &lt;header v= e=&gt; sẽ
    /// hiện nhãn rỗng, rằng field khai mà không liệt kê trong &lt;view&gt; thì không hiện, hay
    /// rằng &lt;command event=&gt; chỉ nhận một tập đóng. Tất cả đều đã được ghi trong
    /// Web/completion.js — nhưng là comment cho người đọc, chưa bao giờ tới tay model.
    ///
    /// Everything here is read off this project's own controllers, so it is "what this
    /// codebase does" rather than "what FCode accepts". Static per region, so it sits in the
    /// cached part of the prompt and costs almost nothing to send.
    /// </summary>
    private static string CompletionSystemPrompt(string? regionHint)
    {
        const string common =
            "You complete code inside BcodeViewer, an editor for FastBusiness ERP source files.\n" +
            "The user's caret is at <CURSOR>. Reply with ONLY the raw text to insert at that point.\n" +
            "No explanation, no markdown fences. Never repeat text that already appears immediately " +
            "before or after the cursor. Match the surrounding indentation and naming style — the " +
            "file you are shown is the style guide. If nothing sensible follows, reply with nothing.\n" +
            "You may be given a PROJECT FACTS section: those field names, entity expansions and SQL " +
            "columns are the real ones. Prefer them over anything you would otherwise guess, and " +
            "never invent a column or field that is not in the file or in those facts.\n";

        return regionHint switch
        {
            "xml" => common +
                "\nYou are continuing FCode XML (a Dir, Grid, Report or Lookup controller).\n" +
                "- A <field> needs a caption: <header v=\"Tiếng Việt\" e=\"English\"></header>. A field " +
                "without one renders blank, which reads as a layout bug rather than a missing line.\n" +
                "- The same tag is written differently per root: a <dir> field carries categoryIndex " +
                "(which tab it lands on), a <grid> field carries width (its column), a <lookup> field " +
                "carries allowFilter, a <report> field is a print label and carries type.\n" +
                "- A field only appears on screen once it is ALSO listed inside <views><view> as " +
                "<field name=\"...\"/>. Declaring it in <fields> alone does nothing visible.\n" +
                "- <command event=\"...\"> takes one of: Init, Showing, Loading, Scattering, Navigating, " +
                "Copying, Closing, Declare, InitExternalFields, Checking, Inserting, Inserted, Updating, " +
                "Updated, Deleting, Deleted. <query event=\"...\"> takes Loading, Declare or Finding.\n" +
                "- <items style=\"...\"> takes AutoComplete, Numeric, Mask, Grid or DropDownList. An " +
                "AutoComplete also needs controller=, reference=, key=, check= and information=.\n" +
                "- dataFormatString uses named formats (@datetimeFormat, @quantityViewFormat, " +
                "@foreignCurrencyAmountInputFormat, @baseCurrencyPriceInputFormat, @exchangeRateInputFormat, " +
                "@upperCaseFormat and the like), not literal masks.\n" +
                "- A name ending in %l is the multilingual variant of a column (ten_kh%l).\n" +
                "- &Entity; references are expanded by the DOCTYPE; reuse an existing one rather than " +
                "inlining what it already contains.\n",

            "sql" => common +
                "\nYou are continuing T-SQL (SQL Server) inside an FCode controller.\n" +
                "- @@macros are substituted by the server before the statement runs: @@id (the voucher " +
                "code), @@master, @@prime / @@inquiry / @@partition / @@expression / @@increase (the " +
                "period-partitioned tables, see <partition>), @@extension, @@unit, @@userID, @@admin, " +
                "@@language (v or e), @@action, @@view, @@operation, @@form, @@sysDatabaseName, " +
                "@@appDatabaseName, @@textList, @@textExternal, @@textOrderBy, @@viewAccessMode, and " +
                "@@refresh/@@pageIndex/@@pageCount/@@lastPage/@@lastCount/@@firstItem/@@lastItem/" +
                "@@keyMaster/@@keyDetail inside <query event=\"Finding\">.\n" +
                "- name$$partition$current resolves to that period's table (m81$$partition$current -> " +
                "m81$202609); $partition$previous is the period the row was in before an edit moved it.\n" +
                "- A <command> returns work to the client by selecting a message string; follow the " +
                "shape already used in this file rather than inventing a new one.\n",

            "js" => common +
                "\nYou are continuing client-side JavaScript inside a controller's <script> block.\n" +
                "- f is the form, g is the grid. Read and write fields with f.getItemValue('ma_kh') and " +
                "f.setItemValue('ma_kh', value); grid cells with g._getItemValue(o.row, o.field) and " +
                "g._setItemValue(o.row, 'ma_vt', value).\n" +
                "- f.request('Context', 'Action', [...]) calls an <action id=\"...\"> on the server; the " +
                "reply arrives in the controller's onResponseComplete handler, switched on context.\n" +
                "- $a.<name> are grid expression aliases; g.showForm('X') opens another controller.\n" +
                "- A toolbar <button command=\"X\"> needs a matching case 'X': in the ExecuteCommand " +
                "switch and a div.X rule in <css> for its icon.\n",

            "css" => common +
                "\nYou are continuing CSS inside a controller's <css> block. Toolbar buttons are styled " +
                "as div.<CommandName> with a background-image sprite, and div.<CommandName>OverGreen " +
                "shifts background-position for the hover state.\n",

            _ => common,
        };
    }

    /// <summary>
    /// When a ghost-text answer has gone far enough to hang up on.
    ///
    /// The system prompt already asks for at most one line "unless the construct obviously
    /// needs closing", and that exception is real — a &lt;field&gt; suggestion properly
    /// carries its &lt;header&gt; and closing tag. So the cut is at THREE complete lines,
    /// which fits that shape and still stops a model that has started writing the rest of
    /// the file. Nobody reads a longer suggestion; waiting for one is pure latency.
    ///
    /// Counted on complete lines only (a trailing fragment doesn't count), so a suggestion
    /// is never truncated mid-line into something that looks finished but isn't.
    /// </summary>
    internal static bool ShouldStopCompletion(string soFar)
    {
        var lines = 0;
        for (var i = 0; i < soFar.Length; i++)
            if (soFar[i] == '\n' && ++lines == 3) return true;
        return false;
    }

    /// <summary>
    /// Reads a <c>"stream": true</c> response (server-sent events) and returns the text as
    /// it accumulates.
    ///
    /// Only two event types matter here. <c>content_block_delta</c> carries the text, one
    /// fragment at a time; <c>message_start</c> carries the usage block, which is the only
    /// place a streamed response reports how much of the prompt came from the cache. Every
    /// other event (ping, content_block_start/stop, message_delta, message_stop) is a
    /// framing detail this method has no use for, and an unknown one is ignored rather than
    /// treated as an error — the stream format gains event types over time and a reader
    /// that throws on the unfamiliar breaks on somebody else's schedule.
    ///
    /// <paramref name="stopWhen"/> is checked against the text so far after each fragment.
    /// Returning true breaks out, and disposing the response then aborts the download — the
    /// model stops being generated and stops being billed. That is the point of streaming
    /// on the ghost-text path: without it, a model that ignores "at most one line" is paid
    /// for all 96 tokens and waited on for all of them too.
    /// </summary>
    internal static async Task<(string Text, string Note)> ReadStreamAsync(
        HttpResponseMessage response,
        Action<string>? onDelta,
        Func<string, bool>? stopWhen,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var note = "";

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            // SSE frames are "field: value" with blank lines between them. The event type is
            // repeated inside the data payload, so the "event:" line adds nothing.
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload.Length == 0 || payload == "[DONE]") continue;

            JsonDocument frame;
            try { frame = JsonDocument.Parse(payload); }
            catch (JsonException) { continue; } // a truncated frame is not worth failing the whole answer over
            using (frame)
            {
                if (!frame.RootElement.TryGetProperty("type", out var typeProp)) continue;
                switch (typeProp.GetString())
                {
                    case "content_block_delta":
                        if (frame.RootElement.TryGetProperty("delta", out var delta) &&
                            delta.TryGetProperty("text", out var deltaText))
                        {
                            var piece = deltaText.GetString() ?? "";
                            if (piece.Length == 0) break;
                            text.Append(piece);
                            onDelta?.Invoke(piece);
                        }
                        break;

                    case "message_start":
                        if (frame.RootElement.TryGetProperty("message", out var message))
                            note = CacheNote(message);
                        break;
                }
            }

            if (stopWhen is not null && stopWhen(text.ToString())) break;
        }

        return (text.ToString(), note);
    }

    /// <summary>"· cache 1842/0" — tokens read from the cache vs. written to it, straight off
    /// the response's usage block. Without this, prompt caching working and prompt caching
    /// silently not applying look exactly the same from the outside.</summary>
    private static string CacheNote(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)) return "";
        var read = usage.TryGetProperty("cache_read_input_tokens", out var r) ? r.GetInt32() : 0;
        var written = usage.TryGetProperty("cache_creation_input_tokens", out var w) ? w.GetInt32() : 0;
        return read == 0 && written == 0 ? "" : $" · cache {read}/{written}";
    }

    /// <summary>
    /// Splits the text before the caret into a cacheable head and a live tail.
    ///
    /// The cut point is quantised to a multiple of <see cref="StableStep"/> measured from
    /// the START of the document, which is the whole trick: a sliding window anchored to
    /// the caret moves on every keystroke and could never be cached, while this boundary
    /// stays put for thousands of characters. It is then nudged forward to the next line
    /// break so the cached block never ends mid-tag — a half-written &lt;field so the model
    /// reads as an instruction to finish it, in the middle of context it was only meant to
    /// be reading.
    ///
    /// Returns ("", prefix) for a short file: below one step there is nothing worth a cache
    /// entry, and a breakpoint under the model's minimum cacheable length is billed as a
    /// write that can never be read.
    /// </summary>
    internal static (string Stable, string Live) SplitForCache(string prefix, int stableBudget, int liveBudget)
    {
        var cut = ((prefix.Length - liveBudget) / StableStep) * StableStep;
        if (cut < StableStep) return ("", prefix.Length > liveBudget ? prefix[^liveBudget..] : prefix);

        var lineEnd = prefix.IndexOf('\n', cut - 1);
        if (lineEnd >= 0 && lineEnd < prefix.Length - 1) cut = lineEnd + 1;

        var stable = prefix[..cut];
        // Head-capped, not tail-capped: what matters up there is the DOCTYPE, and dropping
        // the far middle of a very long file costs less than dropping the entity table.
        if (stable.Length > stableBudget)
        {
            var lastLine = stable.LastIndexOf('\n', stableBudget - 1);
            stable = stable[..(lastLine > 0 ? lastLine + 1 : stableBudget)] + "\n[... middle of the file omitted ...]\n";
        }
        return (stable, prefix[cut..]);
    }

    /// <summary>
    /// Thân request gửi tới /v1/messages cho ghost text.
    ///
    /// Tách khỏi CompleteAsync để NHÌN được trước khi gửi: mỗi tham số ở đây gửi sai một
    /// chút là API trả 400, mà CompleteAsync cố ý nuốt mọi lỗi thành chuỗi rỗng, nên một
    /// request hỏng trông hệt như "model không có gì để gợi ý".
    /// </summary>
    internal static object BuildCompletionRequest(
        string model, string systemPrompt, List<object> blocks, string prefix)
    {
        // Mồi sẵn lượt trả lời của model bằng chính dòng đang gõ dở.
        //
        // Đây là biện pháp mạnh nhất chống lối trả lời kể chuyện, vì nó chặn từ cấu trúc chứ
        // không phải bằng lời dặn: khi lượt assistant đã bắt đầu bằng `<item value="0`, model
        // không còn chỗ nào để mở lời "Looking at the XML structure…" nữa — nó chỉ có thể
        // viết tiếp. Phần trả về là phần NỐI THEO, tức đúng thứ cần chèn.
        //
        // Chỉ mồi khi dòng hiện tại kết thúc bằng ký tự không phải khoảng trắng: API từ chối
        // lượt assistant kết thúc bằng khoảng trắng, và một dòng chỉ có thụt đầu dòng thì
        // toàn bộ nội dung của nó là khoảng trắng.
        var lastBreak = prefix.LastIndexOf('\n');
        var currentLine = lastBreak < 0 ? prefix : prefix[(lastBreak + 1)..];
        var canPrefill = currentLine.Length > 0 && !char.IsWhiteSpace(currentLine[^1]);

        var messages = new List<object> { new { role = "user", content = blocks } };
        if (canPrefill)
            messages.Add(new { role = "assistant", content = currentLine });

        return new
        {
            model,
            max_tokens = 96,
            temperature = 0.0, // a suggestion that changes each time it's re-triggered is worse than none
            system = new[] { new { type = "text", text = systemPrompt } },
            messages,
            // KHÔNG có stop_sequences ở đây, và đừng thêm lại "\n\n".
            //
            // Tôi đã thêm nó để cắt lối trả lời kể chuyện ở đoạn văn thứ hai. API từ chối
            // thẳng: 400 "stop_sequences: each stop sequence must contain non-whitespace".
            // Một chuỗi dừng toàn khoảng trắng là không hợp lệ. Hậu quả không phải là mất
            // một tính năng nhỏ mà là MỌI request ghost text đều hỏng, và vì CompleteAsync
            // cố ý nuốt lỗi thành chuỗi rỗng nên nhìn từ ngoài nó giống hệt "model không có
            // gì để gợi ý" — mất hẳn tính năng, không một dấu hiệu nào.
            //
            // Việc cắt độ dài đã có ShouldStopCompletion lo (dừng ở ba dòng trọn vẹn), còn
            // lối kể chuyện đã có LooksLikeProse và phần mồi lượt assistant lo.

            // Streamed not to render progressively — Monaco's inline provider returns once,
            // so there is nowhere to put a half-finished suggestion — but to be able to HANG
            // UP. See ShouldStopCompletion.
            stream = true,
        };
    }

    /// <summary>
    /// Câu trả lời này là lời giải thích chứ không phải code?
    ///
    /// Đã xảy ra thật, và rơi thẳng vào file người dùng:
    ///
    ///   &lt;item value="0Looking at the XML structure, I can see this is a `&lt;view&gt;`
    ///
    /// System prompt có dặn "chỉ trả về đoạn text cần chèn", nhưng một chỉ dẫn không phải
    /// một bảo đảm, và trên đường ghost text thì im lặng là cách hỏng an toàn duy nhất.
    ///
    /// Hai dấu hiệu, tách riêng vì độ chắc chắn khác nhau: những cụm mở đầu của lối kể
    /// chuyện, và "dòng đầu là một CÂU" (nhiều từ, không một ký tự cú pháp). Đặt ngưỡng rộng
    /// tay — thà để lọt một gợi ý vô hại còn hơn chặn một gợi ý đúng.
    /// </summary>
    internal static bool LooksLikeProse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        foreach (var marker in ProseMarkers)
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;

        var firstLine = text.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        if (firstLine.Length == 0) return false;
        if (firstLine.Any(c => "<>{}[]()=\"'@$;/\\|&#*+".Contains(c))) return false;

        // Ngoài bảng ASCII cơ bản nghĩa là có dấu tiếng Việt, và đó là một NHÃN chứ không
        // phải lời kể: system prompt viết bằng tiếng Anh nên model cũng kể chuyện bằng tiếng
        // Anh. Không có vế này thì "Danh sách khách hàng chưa thanh toán trong kỳ này" —
        // chín từ, không một dấu cú pháp — bị chặn oan, mà nhãn dài kiểu đó đầy trong các
        // controller của dự án.
        if (firstLine.Any(c => c > 127)) return false;

        return firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 8;
    }

    /// <summary>Cách một câu trả lời hội thoại mở lời. Đọc từ đúng sự cố đã gặp, cộng những
    /// biến thể cùng loại — không phải danh sách đầy đủ, mà là cái lưới đủ dày cho thứ hay
    /// xảy ra nhất.</summary>
    private static readonly string[] ProseMarkers =
    {
        "I can see", "I notice", "I'll ", "I will ", "Let me ", "Looking at",
        "Based on the", "Here's the", "Here is the", "It seems", "It looks like",
        "appears to be", "the cursor is", "The cursor is", "Note that", "you can ",
        "you should", "we need to", "this should be", "This appears",
    };

    /// <summary>
    /// Strips what the model adds despite being told not to. A fenced block inserted
    /// verbatim would put ``` into the user's source file, so this is not cosmetic.
    /// </summary>
    internal static string CleanCompletion(string text)
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
        if (cleaned.Trim().Length == 0) return "";

        // Sau khi đã gỡ fence mới xét: một lời giải thích bọc trong ``` vẫn là lời giải thích.
        return LooksLikeProse(cleaned) ? "" : cleaned;
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
