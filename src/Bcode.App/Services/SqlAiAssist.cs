using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// AI cho editor SQL Query của chế độ App (native): Ctrl+I sửa/sinh SQL theo yêu cầu và gợi ý ghost-text khi gõ.
/// Cùng prompt, cùng engine (Claude / Gemini theo AppSettings.CopilotEngine), cùng cách kèm cấu trúc bảng như bản Web
/// (RawSqlControl.HandleAiEditAsync / QueryCopilotAiAsync) — nhưng nằm ở lớp riêng để tab native không phụ thuộc vào UI WebView2.
/// </summary>
public sealed class SqlAiAssist
{
    private readonly RawSqlService _service;
    private readonly AppSettings _settings;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static DateTime _cooldownUntil = DateTime.MinValue;
    private static readonly Dictionary<string, string> SchemaCache = new();

    private static readonly Regex TableRefRegex = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO)\s+(\[?[\w$]+\]?(?:\.\[?[\w$]+\]?)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string FastHint =
        "Context: FastBusiness (Fast) ERP database on SQL Server. Table and column names are lowercase Vietnamese abbreviations " +
        "(e.g. dmkh = customer list, dmvt = item list, ma_kh, ten_kh, ma_vt, stt_rec, ngay_ct, so_ct, ma_dvcs, ma_nt, tien_nt, tien, so_luong); " +
        "voucher data is split into master (m..$yyyymm / m21$000000), detail (d..$) and period tables. " +
        "Only use tables and columns that appear in the schema below when it is given; if something is not listed, do not invent it.";

    public SqlAiAssist(RawSqlService service, AppSettings settings)
    {
        _service = service;
        _settings = settings;
    }

    /// <summary>Có ít nhất 1 API key (Claude hoặc Gemini) để gọi AI.</summary>
    public bool HasKey => !string.IsNullOrWhiteSpace(_settings.AnthropicApiKey) || !string.IsNullOrWhiteSpace(_settings.GeminiApiKey);

    private bool UseClaudeEngine => string.Equals(_settings.CopilotEngine, "claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>Engine đang dùng cho gợi ý ("Claude" / "Gemini"); engine được chọn chưa có key mà engine kia có thì dùng engine kia. null = chưa có key nào.</summary>
    public string? Engine
    {
        get
        {
            var hasClaude = !string.IsNullOrWhiteSpace(_settings.AnthropicApiKey);
            var hasGemini = !string.IsNullOrWhiteSpace(_settings.GeminiApiKey);
            if (!hasClaude && !hasGemini) return null;
            return hasGemini && (!hasClaude || !UseClaudeEngine) ? "Gemini" : "Claude";
        }
    }

    // =====================================================================  Ctrl+I: sửa / sinh SQL

    /// <summary>Sửa đoạn đang chọn (hoặc sinh SQL tại con trỏ) theo yêu cầu, có kèm cấu trúc các bảng đang dùng trong script. Trả (text, lỗi): lỗi rỗng = thành công.</summary>
    public async Task<(string Text, string Error)> EditAsync(string instruction, string selection, string script, bool useSys)
    {
        string text = "", error = "";
        try
        {
            var engine = Engine ?? throw new InvalidOperationException("Chưa có API key — vào menu Tùy chọn > cấu hình Claude (Anthropic) hoặc Gemini API Key.");
            var useGemini = engine == "Gemini";

            var schema = await BuildSchemaContextAsync(script + "\n" + selection, useSys);
            var system =
                "You are an expert T-SQL developer working inside an editor for SQL Server scripts. " + FastHint + "\n" +
                (selection.Length > 0
                    ? "The user selected part of their script and wants it changed. Reply with ONLY the full replacement for the selected part."
                    : "The user wants new SQL inserted at the caret. Reply with ONLY the SQL to insert.") +
                " No markdown fences, no explanations; keep the user's style and keep unchanged parts as they are." +
                (schema.Length > 0 ? "\n\nSchema of tables used in the script:\n" + schema : "");

            var script2 = script.Length > 30000 ? script[..30000] + "\n-- ...(truncated)" : script;
            var user = $"--- FULL SCRIPT ---\n{script2}\n\n" +
                       (selection.Length > 0 ? $"--- SELECTED PART (to be replaced) ---\n{selection}\n\n" : "") +
                       $"--- REQUEST ---\n{instruction}";

            text = useGemini
                ? await AiChatService.GeminiAsync(_settings.GeminiApiKey, system, user, maxTokens: 8192)
                : await AiChatService.ClaudeAsync(_settings.AnthropicApiKey, _settings.ClaudeEditModel, system, user, maxTokens: 4096);
            text = Regex.Replace(text.Trim(), @"^```[\w-]*\r?\n|\r?\n?```\s*$", "");
            // Lưu lại ngay khi có kết quả — đóng hộp thoại mà chưa Insert vẫn lấy lại được ở "Lịch sử gợi ý AI".
            AiHistoryStore.Add("Ctrl+I", useGemini ? "Gemini" : "Claude", instruction, selection.Length > 300 ? selection[..300] + "…" : selection, text);
        }
        catch (TaskCanceledException) { error = "Hết thời gian chờ AI (90s)."; }
        catch (Exception ex) { error = ex.Message; }
        return (text, error);
    }

    // =====================================================================  Ghost text

    /// <summary>Gợi ý đoạn SQL chèn tại con trỏ (fill-in-the-middle: đưa cả code trước và sau con trỏ). Trả (gợi ý, thông báo cho thanh trạng thái, có phải lỗi không);
    /// gợi ý rỗng = không có gì để hiện.</summary>
    public async Task<(string Text, string Status, bool IsError)> SuggestAsync(string prefix, string suffix, bool useSys, CancellationToken token)
    {
        var engine = Engine;
        if (engine is null) return ("", "", false);
        if (DateTime.Now < _cooldownUntil)
            return ("", $"Copilot: Tạm dừng {(int)(_cooldownUntil - DateTime.Now).TotalSeconds}s để hồi quota...", true);
        try
        {
            var lastWordMatch = Regex.Match(prefix, @"[@#\w$]+$", RegexOptions.RightToLeft);
            var lastWord = lastWordMatch.Success ? lastWordMatch.Value : "";

            string text;
            if (engine == "Claude")
            {
                var r = await ClaudeSuggestAsync(prefix, suffix, useSys, token);
                if (r.Error is not null) return ("", r.Error, true);
                text = r.Text;
            }
            else
            {
                var r = await GeminiSuggestAsync(prefix, suffix, token);
                if (r.Error is not null) return ("", r.Error, true);
                text = r.Text;
            }

            text = text.Replace("```sql", "").Replace("```", "").TrimStart('\r', '\n');
            if (!string.IsNullOrEmpty(lastWord) && text.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase)) text = text[lastWord.Length..];
            text = TrimSuggestionOverlap(text, prefix, suffix);
            if (string.IsNullOrWhiteSpace(text)) return ("", "Copilot: Không có gợi ý phù hợp", false);
            AiHistoryStore.Add("Ghost", engine, "", CurrentLineOf(prefix), text);
            return (text, $"Copilot ({engine}): Đã có gợi ý (bấm Tab để nhận, Esc để bỏ)", false);
        }
        catch (OperationCanceledException) { return ("", "", false); }
        catch (Exception ex) { return ("", "Copilot: " + ex.Message, true); }
    }

    private async Task<(string Text, string? Error)> ClaudeSuggestAsync(string prefix, string suffix, bool useSys, CancellationToken token)
    {
        var schema = await BuildSchemaContextAsync(prefix + "\n" + suffix, useSys);
        // Còn chữ phía sau con trỏ trên CÙNG dòng = đang sửa giữa dòng: chỉ gợi ý đoạn ngắn chèn vào giữa, không viết lại dòng.
        var restOfLine = suffix.Split('\n')[0].Trim();
        var midLine = restOfLine.Length > 0;
        var system =
            "You are an expert inline T-SQL autocomplete engine for SQL Server (stored procedures, functions, scripts in the " +
            "FastBusiness ERP codebase). The developer is editing at the position marked /* [CURSOR] */.\n" + FastHint + "\n" +
            "Rules: 1) Continue seamlessly from the cursor with the exact next lines needed. " +
            "2) Use the parameters, declared variables and temp tables visible in the code before the cursor. " +
            "3) Read the code after the cursor: never duplicate it and never close a block that is already closed there. " +
            "4) Reply with ONLY the raw SQL to insert — no markdown fences, no explanation, and do not repeat the last typed word. " +
            "5) Stay relevant to the statement the cursor is in (its tables, aliases, columns, the CASE/JOIN/WHERE being written); never start a new unrelated statement." +
            (midLine ? $" 6) The cursor is in the MIDDLE of a line: this text already follows it on the same line and must NOT be repeated: [{restOfLine}]. Suggest only the short piece (usually a few words) that belongs at the cursor." : "") +
            (schema.Length > 0 ? "\n\nSchema of tables used in the script:\n" + schema : "");

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(_settings.ClaudeModel) ? "claude-haiku-4-5-20251001" : _settings.ClaudeModel,
            max_tokens = midLine ? 100 : 400,
            temperature = 0.1,
            system,
            messages = new[] { new { role = "user", content = $"--- CODE BEFORE CURSOR ---\n{prefix}\n/* [CURSOR] */\n--- CODE AFTER CURSOR ---\n{suffix}" } },
            // Claude từ chối stop sequence chỉ gồm khoảng trắng → chỉ giữ dòng GO.
            stop_sequences = new[] { "\nGO\n", "\nGO\r\n" },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", _settings.AnthropicApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var res = await Http.SendAsync(req, token);
        var body = await res.Content.ReadAsStringAsync(token);
        if ((int)res.StatusCode is 429 or 529)
        {
            _cooldownUntil = DateTime.Now.AddSeconds((int)res.StatusCode == 429 ? 30 : 10);
            return ("", $"Copilot lỗi ({(int)res.StatusCode}): quá tải/hạn mức — tạm dừng ít giây.");
        }
        if (!res.IsSuccessStatusCode) return ("", $"Copilot (Claude) lỗi ({(int)res.StatusCode}): {ErrorDetail(body, res.ReasonPhrase)}");

        using var doc = JsonDocument.Parse(body);
        var text = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var blocks))
            foreach (var b in blocks.EnumerateArray())
                if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var tx))
                    text.Append(tx.GetString());
        return (text.ToString(), null);
    }

    private async Task<(string Text, string? Error)> GeminiSuggestAsync(string prefix, string suffix, CancellationToken token)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent?key={_settings.GeminiApiKey}";
        // Prompt FIM (Fill-In-The-Middle): đưa cả code trước và code sau vào để AI điền vào giữa.
        var promptText = "You are an expert inline T-SQL autocomplete engine for SQL Server Stored Procedures and Functions.\n" +
            "The developer is currently editing the procedure at the position marked by /* [CURSOR] */.\n\n" +
            "Requirements:\n" +
            "1. Continue seamlessly from the cursor. Predict the exact next lines of code needed.\n" +
            "2. Read the parameters, declared variables, and temp tables in CODE BEFORE CURSOR.\n" +
            "3. Check CODE AFTER CURSOR carefully: do NOT duplicate any code, do NOT close blocks prematurely if they are already closed after cursor.\n" +
            "4. Return ONLY the raw SQL code to insert at /* [CURSOR] */. Do NOT output markdown code blocks (```). Do NOT repeat the last word.\n\n" +
            $"--- CODE BEFORE CURSOR ---\n{prefix}\n/* [CURSOR] */\n--- CODE AFTER CURSOR ---\n{suffix}";
        var payload = new
        {
            contents = new[] { new { parts = new[] { new { text = promptText } } } },
            generationConfig = new
            {
                temperature = 0.1,
                maxOutputTokens = 1024,
                thinkingConfig = new { thinkingBudget = 0 },
                stopSequences = new[] { "\n\n\n", "GO\r\n", "GO\n" },
            },
        };
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var res = await Http.PostAsync(url, content, token);
        var body = await res.Content.ReadAsStringAsync(token);
        if ((int)res.StatusCode == 429)
        {
            _cooldownUntil = DateTime.Now.AddSeconds(30);
            return ("", "Copilot lỗi (429): Quá hạn mức request. Tự động tạm dừng 30 giây.");
        }
        if ((int)res.StatusCode == 503)
        {
            _cooldownUntil = DateTime.Now.AddSeconds(10);
            return ("", "Copilot: Server Gemini đang quá tải, tạm dừng 10 giây...");
        }
        if (!res.IsSuccessStatusCode) return ("", $"Copilot lỗi ({(int)res.StatusCode}): {ErrorDetail(body, res.ReasonPhrase)}");

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0
            && candidates[0].TryGetProperty("content", out var c) && c.TryGetProperty("parts", out var parts) && parts.GetArrayLength() > 0)
            return (parts[0].GetProperty("text").GetString() ?? "", null);
        return ("", null);
    }

    private static string ErrorDetail(string body, string? fallback)
    {
        var detail = fallback ?? "Error";
        try
        {
            using var err = JsonDocument.Parse(body);
            if (err.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) detail = m.GetString() ?? detail;
        }
        catch { /* thân phản hồi không phải JSON */ }
        return detail;
    }

    /// <summary>Gợi ý chèn vào giữa dòng hay lặp lại đúng phần chữ đã có phía sau con trỏ ("ELSE| RTRIM(status) END WHERE..." → gợi ý "RTRIM(status) END WHERE flag = 1" vô nghĩa).
    /// Cắt phần gợi ý trùng với đầu phần còn lại của file; nếu gợi ý chỉ toàn là phần đó thì bỏ luôn. Thêm 1 khoảng trắng nếu gợi ý dính liền chữ vừa gõ.</summary>
    public static string TrimSuggestionOverlap(string text, string prefix, string suffix)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var rest = suffix.TrimStart(' ', '\t');
        var restLine = rest.Split('\n')[0].TrimEnd('\r');

        if (restLine.Length > 0)
        {
            // Gợi ý bắt đầu bằng đúng phần còn lại của dòng → không có gì mới để chèn.
            if (text.TrimStart().StartsWith(restLine.Trim(), StringComparison.OrdinalIgnoreCase)) return "";
            // Đuôi gợi ý trùng đầu phần còn lại của dòng → cắt đuôi đó.
            var t = restLine.Trim();
            var cut = text.TrimEnd().LastIndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (cut >= 0 && cut + t.Length == text.TrimEnd().Length) text = text[..cut];
        }

        // Trùng nhiều dòng: đuôi gợi ý trùng với phần đầu của suffix (tối thiểu 4 ký tự).
        var tail = text.TrimEnd();
        for (var len = Math.Min(tail.Length, rest.Length); len >= 4; len--)
        {
            if (string.Compare(tail, tail.Length - len, rest, 0, len, StringComparison.OrdinalIgnoreCase) == 0)
            {
                text = tail[..^len];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(text)) return "";
        // "ELSE" + "RTRIM(...)" dính liền → thêm khoảng trắng.
        if (prefix.Length > 0 && char.IsLetterOrDigit(prefix[^1]) && char.IsLetterOrDigit(text[0])) text = " " + text;
        return text;
    }

    private static string CurrentLineOf(string prefix)
    {
        var line = prefix.Split('\n')[^1].Trim();
        return line.Length > 300 ? line[^300..] : line;
    }

    // =====================================================================  cấu trúc bảng kèm theo prompt

    /// <summary>Cấu trúc (cột, kiểu, khoá chính) của các bảng đang được dùng trong script — chỉ những bảng xuất hiện sau FROM/JOIN/UPDATE/INTO (tối đa 8), không gửi cả database.
    /// Nhớ lại theo từng bảng để lần sau không phải truy vấn lại. Lỗi/không có bảng → bỏ qua, AI vẫn chạy được.</summary>
    private async Task<string> BuildSchemaContextAsync(string sql, bool useSys)
    {
        try
        {
            var names = TableRefRegex.Matches(sql)
                .Select(m => m.Groups[1].Value.Replace("[", "").Replace("]", ""))
                .Where(n => n.Length > 0 && !n.StartsWith('#') && !n.StartsWith('@'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            if (names.Count == 0) return "";

            var sb = new StringBuilder();
            foreach (var name in names)
            {
                var key = $"{useSys}|{name.ToLowerInvariant()}";
                string? line;
                lock (SchemaCache) SchemaCache.TryGetValue(key, out line);
                if (line is null)
                {
                    line = await ReadTableSchemaAsync(useSys, name);
                    lock (SchemaCache) SchemaCache[key] = line;
                }
                if (line.Length > 0) sb.AppendLine(line);
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    private async Task<string> ReadTableSchemaAsync(bool useSys, string table)
    {
        try
        {
            await using var conn = _service.CreateConnection(useSys);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(@"
SELECT c.name, ty.name, c.max_length, c.precision, c.scale,
       CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                         WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END
FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(@n) ORDER BY c.column_id", conn);
            cmd.Parameters.AddWithValue("@n", table);
            await using var r = await cmd.ExecuteReaderAsync();

            var cols = new List<string>();
            while (await r.ReadAsync() && cols.Count < 80)
            {
                var type = r.GetString(1);
                var len = r.GetInt16(2);
                var typeText = type.ToLowerInvariant() switch
                {
                    "varchar" or "char" => $"{type}({(len < 0 ? "max" : len.ToString())})",
                    "nvarchar" or "nchar" => $"{type}({(len < 0 ? "max" : (len / 2).ToString())})",
                    "decimal" or "numeric" => $"{type}({r.GetByte(3)},{r.GetByte(4)})",
                    _ => type,
                };
                cols.Add($"{r.GetString(0)} {typeText}{(r.GetInt32(5) == 1 ? " PK" : "")}");
            }
            return cols.Count == 0 ? "" : $"{table}({string.Join(", ", cols)})";
        }
        catch { return ""; }
    }
}
