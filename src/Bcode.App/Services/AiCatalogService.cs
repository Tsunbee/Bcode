using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>Cột của 1 bảng thật trong App Data (chỉ tên/kiểu — không bao giờ có dữ liệu dòng).</summary>
public sealed record AiTableColumn(string Name, string SqlType, bool IsPrimaryKey);

/// <summary>Kết quả 1 lượt "AI tạo danh mục".</summary>
public sealed class AiCatalogResult
{
    /// <summary>"danh_muc" = yêu cầu tạo danh mục mới; "khac" = loại khác (bản thử chưa hỗ trợ).</summary>
    public string Kind { get; set; } = "khac";
    public string Summary { get; set; } = "";
    public List<string> Questions { get; set; } = new();
    public List<string> Notes { get; set; } = new();
    /// <summary>Spec dạng JSON của trang quicklist.html (đã chuẩn hoá) — null nếu AI không điền.</summary>
    public JsonObject? Spec { get; set; }
    /// <summary>Bảng AI đề xuất mà App Data chưa có → cột lấy từ spec (để trang nạp như bảng thật) + script tạo bảng (không tự chạy).</summary>
    public Dictionary<string, List<AiTableColumn>> NewTables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string CreateTableSql { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Raw { get; set; } = "";
}

/// <summary>
/// "AI tạo danh mục" (bản thử): đọc nội dung 1 yêu cầu (Note (New) — đã Sync từ nvphyc), hỏi AI xem có phải yêu cầu tạo danh mục mới không
/// và điền spec của "Tạo nhanh danh mục" (<see cref="QuickListSpec"/>, dạng JSON của trang quicklist.html). AI KHÔNG viết file / chạy SQL:
/// spec chỉ được nạp vào màn Tạo nhanh danh mục để người dùng duyệt, bấm Tạo file mới ghi (qua <see cref="QuickListService"/>).
///
/// Gửi lên AI: nội dung yêu cầu + tên bảng của App Data + tên/kiểu cột của các bảng liên quan (bảng nêu trong yêu cầu, bảng AI xin thêm qua
/// "can_bang"). KHÔNG gửi dữ liệu dòng. Mỗi lượt gọi lưu vào Lịch sử gợi ý AI (<see cref="AiHistoryStore"/>, loại "Danh mục").
/// </summary>
public sealed class AiCatalogService
{
    private const int MaxTableNames = 1500;
    private const int MaxDetailedTables = 10;
    private const int MaxRounds = 3;

    private readonly Func<Task<List<string>>> _listTables;
    private readonly Func<string, Task<List<AiTableColumn>>> _getColumns;
    private readonly Func<string, string, Task<(string Text, string Engine)>> _ask;
    private readonly Func<bool> _useRules;
    private readonly string _sourcePath;
    private readonly string _tablePrefix;
    private List<string>? _tables;
    private readonly Dictionary<string, List<AiTableColumn>> _columnCache = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="listTables">Tên mọi bảng của App Data.</param>
    /// <param name="getColumns">Cột (tên, kiểu SQL, khoá chính) của 1 bảng; rỗng nếu bảng không có.</param>
    /// <param name="ask">Gọi AI (system, user) → (chữ trả về, tên engine).</param>
    /// <param name="useRules">true = không gọi AI, gợi ý theo luật cấu trúc Fast (<see cref="FastCatalogRules"/>) — mặc định khi chưa có API key.</param>
    /// <param name="sourcePath">Source Path của workspace — để luật biết danh mục nào đã có (Controllers\Dir).</param>
    /// <param name="tablePrefix">Tiền tố tên bảng danh mục mới (mặc định "zdm"; chi tiết = bảng + "ct").</param>
    public AiCatalogService(Func<Task<List<string>>> listTables, Func<string, Task<List<AiTableColumn>>> getColumns,
        Func<string, string, Task<(string Text, string Engine)>> ask, Func<bool>? useRules = null, string sourcePath = "", string tablePrefix = "zdm")
    {
        _listTables = listTables;
        _getColumns = getColumns;
        _ask = ask;
        _useRules = useRules ?? (() => false);
        _sourcePath = sourcePath;
        _tablePrefix = string.IsNullOrWhiteSpace(tablePrefix) ? "zdm" : tablePrefix.Trim().ToLowerInvariant();
    }

    /// <summary>Bản dùng trong Bcode: đọc App Data qua service sẵn có, gọi Claude (ưu tiên) hoặc Gemini theo API key trong settings;
    /// chưa có key nào thì gợi ý theo luật cấu trúc Fast (không tốn token).</summary>
    public AiCatalogService(SqlObjectBrowserService sqlObjects, TableDataService tableData, AppSettings settings, string sourcePath = "")
        : this(
            async () => (await sqlObjects.ListObjectsAsync(useSysDatabase: false))
                .Where(o => o.Kind == SqlObjectKind.Table).Select(o => o.Name).ToList(),
            async table =>
            {
                var cols = await sqlObjects.GetColumnsAsync(false, "dbo", table);
                if (cols.Count == 0) return new List<AiTableColumn>();
                var types = await tableData.GetColumnTypesAsync(false, "dbo", table);
                return cols.Select(c => new AiTableColumn(c.Name, types.TryGetValue(c.Name, out var t) ? t : "", c.IsPrimaryKey)).ToList();
            },
            (system, user) => AskConfiguredAsync(settings, system, user),
            () => { settings.SyncApiKeysFromDisk(); return !HasApiKey(settings); }, sourcePath, settings.NewCatalogTablePrefix)
    { }

    public static bool HasApiKey(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.AnthropicApiKey) || !string.IsNullOrWhiteSpace(settings.GeminiApiKey);

    /// <summary>Claude nếu có Anthropic API key, không thì Gemini; hẹn giờ 3 phút vì spec JSON dài.</summary>
    public static async Task<(string Text, string Engine)> AskConfiguredAsync(AppSettings settings, string system, string user)
    {
        var timeout = TimeSpan.FromMinutes(3);
        if (!string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
            return (await AiChatService.ClaudeAsync(settings.AnthropicApiKey, settings.ClaudeEditModel, system, user, maxTokens: 16000, timeout: timeout), "Claude");
        if (!string.IsNullOrWhiteSpace(settings.GeminiApiKey))
            return (await AiChatService.GeminiAsync(settings.GeminiApiKey, system, user, maxTokens: 16000, timeout: timeout), "Gemini");
        throw new InvalidOperationException("Chưa có API key — vào menu ⚙ > cấu hình Claude (Anthropic) hoặc Gemini API Key.");
    }

    /// <summary>
    /// Hỏi AI cho 1 yêu cầu. <paramref name="answers"/> = các câu hỏi AI đã hỏi lượt trước và câu trả lời của người dùng.
    /// AI xin thêm cột của bảng nào (can_bang) thì tự đọc cột rồi hỏi lại (tối đa <see cref="MaxRounds"/> lượt).
    /// </summary>
    /// <param name="forceRules">true = gợi ý theo luật cấu trúc Fast dù đã có API key (nút "Theo luật Fast" — vd AI quá tải / không muốn gửi dữ liệu ra ngoài).</param>
    public async Task<AiCatalogResult> SuggestAsync(string requestName, string content, IReadOnlyList<(string Question, string Answer)> answers,
        Action<string>? progress = null, bool forceRules = false)
    {
        progress?.Invoke("Đang đọc danh sách bảng App Data...");
        var tables = _tables ??= (await _listTables()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var tableSet = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);

        // Bảng có tên xuất hiện trong yêu cầu / câu trả lời → gửi kèm cột ngay lượt đầu.
        var text = content + "\n" + string.Join("\n", answers.Select(a => a.Answer));
        var detailed = MentionedTables(text, tables).Take(MaxDetailedTables).ToList();

        // Field source dự án (đặt tên cột như dự án đã đặt): lần đầu đọc ổ mạng có thể lâu — chờ tối đa 20 giây rồi làm tiếp không có nó.
        if (_sourcePath.Length > 0 && SourceFieldIndex.TryGet(_sourcePath) is null)
        {
            progress?.Invoke("Đang đọc field của source dự án (lần đầu)...");
            var load = SourceFieldIndex.GetAsync(_sourcePath);
            await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(20)));
        }

        AiCatalogResult result = new();
        if (forceRules || _useRules())
        {
            // Chưa có API key (hoặc người dùng chọn "Theo luật Fast"): gợi ý theo luật cấu trúc Fast — cùng dạng JSON nên chuẩn hoá + giao diện dùng chung với AI.
            progress?.Invoke(forceRules ? "Đang gợi ý theo cấu trúc Fast (không dùng AI)..." : "Chưa có API key — đang gợi ý theo cấu trúc Fast (không dùng AI)...");
            var raw = await Task.Run(() => FastCatalogRules.SuggestAsync(content, answers, tableSet, ColumnsAsync, _sourcePath, _tablePrefix));
            result = Parse(raw);
            result.Engine = FastCatalogRules.Engine;
        }
        else
        for (var round = 1; round <= MaxRounds; round++)
        {
            var columns = new Dictionary<string, List<AiTableColumn>>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in detailed)
            {
                progress?.Invoke($"Đang đọc cột bảng {t}...");
                columns[t] = await ColumnsAsync(t);
            }

            progress?.Invoke(round == 1 ? "Đang hỏi AI..." : $"Đang hỏi AI (lượt {round}, đã gửi thêm cột bảng được xin)...");
            var user = BuildUserPrompt(requestName, content, tables, columns, answers, FastCatalogRules.LookupHints(content, _sourcePath), ProjectHints(text));
            var dataPrefix = Regex.Replace(_tablePrefix, "dm$", "") is { Length: > 0 } dp ? dp : "z";
            var (raw, engine) = await _ask(SystemPrompt.Replace("@@PREFIX@@", _tablePrefix).Replace("@@DATAPREFIX@@", dataPrefix), user);
            AiHistoryStore.Add("Danh mục", engine, $"[{requestName}] AI tạo danh mục", Clip(content, 300), raw);

            var parsed = Parse(raw);
            result = parsed;
            result.Engine = engine;
            var wanted = parsed.WantedTables.Where(tableSet.Contains).Where(t => !detailed.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            if (wanted.Count == 0 || round == MaxRounds) break;
            detailed.AddRange(wanted.Take(MaxDetailedTables));
        }

        if (result.Spec is not null)
        {
            progress?.Invoke("Đang đối chiếu spec với cấu trúc bảng...");
            await NormalizeAsync(result, tableSet);
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------- AI phân tích (rồi chạy luật Fast)

    /// <summary>Kết quả "AI phân tích": nội dung y/c viết lại theo mẫu luật Fast đọc được (người dùng sửa / xác nhận rồi mới chạy luật).</summary>
    public sealed class AiAnalysis
    {
        /// <summary>danh_muc / man_so_lieu / khac.</summary>
        public string Kind { get; set; } = "khac";
        public string Summary { get; set; } = "";
        public string Text { get; set; } = "";
        public List<string> Notes { get; set; } = new();
        public string Engine { get; set; } = "";
    }

    /// <summary>
    /// "AI phân tích": AI đọc y/c (văn tự do của khách) rồi VIẾT LẠI theo đúng mẫu câu <see cref="FastCatalogRules"/> đọc chuẩn nhất
    /// (Thêm danh mục X / Gồm các trường: - cột [field]: lookup từ danh mục …, bắt buộc nhập…). Không điền spec — người dùng sửa nội dung,
    /// bấm Xác nhận thì luật Fast (không AI) mới dựng danh mục từ nội dung đó, nên kết quả ổn định và đúng quy ước. Cần API key.
    /// </summary>
    public async Task<AiAnalysis> AnalyzeAsync(string requestName, string content, Action<string>? progress = null)
    {
        if (_useRules())
            throw new InvalidOperationException("AI phân tích cần API key (Claude / Gemini) — chưa có key thì bấm ⚙ Theo luật Fast.");
        if (_sourcePath.Length > 0 && SourceFieldIndex.TryGet(_sourcePath) is null)
        {
            progress?.Invoke("Đang đọc field của source dự án (lần đầu)...");
            await Task.WhenAny(SourceFieldIndex.GetAsync(_sourcePath), Task.Delay(TimeSpan.FromSeconds(20)));
        }
        var sb = new StringBuilder();
        sb.AppendLine($"--- YÊU CẦU (mã {requestName}) ---");
        sb.AppendLine(content.Trim());
        if (FastCatalogRules.LookupHints(content, _sourcePath) is { Count: > 0 } lh)
        {
            sb.AppendLine();
            sb.AppendLine("--- LOOKUP CHUẨN cho các cụm \"Lookup từ danh mục …\" ---");
            foreach (var h in lh) sb.AppendLine(h);
        }
        if (ProjectHints(content) is { Count: > 0 } ph)
        {
            sb.AppendLine();
            sb.AppendLine("--- FIELD DỰ ÁN ĐANG DÙNG / BẠN ĐÃ CHỐT (tiêu đề → field, ac = lookup controller) ---");
            foreach (var h in ph) sb.AppendLine(h);
        }
        progress?.Invoke("Đang nhờ AI phân tích yêu cầu...");
        var (raw, engine) = await _ask(AnalyzePrompt.Replace("@@PREFIX@@", _tablePrefix), sb.ToString());
        AiHistoryStore.Add("Danh mục", engine, $"[{requestName}] AI phân tích", Clip(content, 300), raw);

        var r = new AiAnalysis { Engine = engine };
        var s = raw.Trim();
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        JsonObject? o = null;
        if (a >= 0 && b > a) { try { o = JsonNode.Parse(s[a..(b + 1)]) as JsonObject; } catch { /* không phải JSON */ } }
        if (o is null)
        {
            // AI trả thẳng văn bản: vẫn cho người dùng sửa / xác nhận.
            r.Kind = "danh_muc"; r.Text = s; r.Notes.Add("AI không trả JSON — nội dung bên dưới là nguyên văn AI viết, kiểm tra kỹ trước khi xác nhận.");
            return r;
        }
        r.Kind = Str(o["loai"]) is { Length: > 0 } k ? k : "danh_muc";
        r.Summary = Str(o["tom_tat"]);
        r.Text = Str(o["noi_dung"]).Replace("\r\n", "\n").Trim();
        r.Notes = Strs(o["ghi_chu"]);
        return r;
    }

    public const string AnalyzePrompt =
"""
You help a FastBusiness ERP (FCode) developer. Read a customer requirement (Vietnamese, free text) and REWRITE it as a short structured text that a rule-based tool ("luật Fast") parses to build a catalog screen. You do NOT build the screen; the developer reviews / edits your text, then the tool builds it.

Reply with ONE JSON object only (no markdown fences):
{ "loai": "danh_muc" | "man_so_lieu" | "khac", "tom_tat": "1 sentence in Vietnamese", "noi_dung": "the structured text", "ghi_chu": ["short Vietnamese notes: assumptions, parts of the requirement NOT covered (reports, voucher changes, special logic) that must be done by hand"] }

"danh_muc" = a new master-data catalog with its own code + name. "man_so_lieu" = a new data-entry screen without code/name (e.g. menu "Kế hoạch doanh thu tháng", "Cập nhật dự toán": rows identified by dimensions such as đơn vị, tháng, năm, khách hàng). "khac" = not a new screen (changing an existing screen, report, voucher...): noi_dung = "".

noi_dung format for "danh_muc" (Vietnamese, exactly these line shapes, one column per "- " line):
Thêm danh mục <tên ngắn, lowercase, no "danh mục" prefix>
Bảng @@PREFIX@@<viết tắt>          (only when the requirement names a table; otherwise omit this line)
Gồm các trường:
- Mã <tên>: bắt buộc nhập
- Tên <tên>: bắt buộc nhập
- <Tiêu đề cột> [field]: <mô tả>
Chi tiết gồm: <Tiêu đề cột> [field], <Tiêu đề cột> [field]      (only if the catalog has a detail grid of lines)
Cho phép import Excel                                           (only if the requirement mentions import)

noi_dung format for "man_so_lieu":
Thiết kế menu mới "<tên màn>", bao gồm các trường sau:
- <Tiêu đề cột> [field]: <mô tả>
(a measure entered per month: "- <Chỉ tiêu> theo 12 tháng: nhập số tiền")

Rules for the column lines:
- Header (Tiêu đề cột) in Vietnamese with diacritics, short, the way a Fast screen labels it ("Mã khách", "Mã kho", "Ngày hiệu lực", "Số tiền").
- One line per column. A combined phrase like "Tháng / Năm" becomes two lines "- Tháng" and "- Năm".
- [field] = the FastBusiness field name, ONLY when you are sure: standard ones (ma_kh, ma_vt, ma_kho, ma_lo, ma_bp, ma_dvcs, ma_nvbh, ma_nt, tk, ma_vv, dvt, ngay_hl, thang, nam, ghi_chu) or a field listed under "FIELD DỰ ÁN / BẠN ĐÃ CHỐT". Otherwise omit the brackets and let the tool pick. Never put [field] on the catalog's own "Mã …" / "Tên …" lines.
- <mô tả> uses only these phrases, comma separated: "lookup từ danh mục <X>" (column refers to another catalog: khách hàng, vật tư, kho, lô, bộ phận, đơn vị, nhân viên kinh doanh...), "bắt buộc nhập", "không bắt buộc nhập", "nhập số tiền", "nhập số lượng", "nhập ngày". Omit ": <mô tả>" when nothing applies.
- Do not invent columns the requirement does not ask for (the tool adds status and system columns itself). Keep behaviour the tool cannot express (formulas, auto-fill, checks, reports) out of noi_dung and list it in ghi_chu.
""";

    private async Task<List<AiTableColumn>> ColumnsAsync(string table)
    {
        if (_columnCache.TryGetValue(table, out var c)) return c;
        try { c = await _getColumns(table); } catch { c = new(); }
        return _columnCache[table] = c;
    }

    private static string Clip(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    /// <summary>Tên bảng (≥ 3 ký tự) xuất hiện nguyên từ trong <paramref name="text"/>.</summary>
    internal static IEnumerable<string> MentionedTables(string text, IEnumerable<string> tables)
    {
        var words = new HashSet<string>(Regex.Matches(text, @"[A-Za-z_][A-Za-z0-9_$]*").Select(m => m.Value), StringComparer.OrdinalIgnoreCase);
        return tables.Where(t => t.Length >= 3 && words.Contains(t));
    }

    // ---------------------------------------------------------------------------------------- prompt

    public const string SystemPrompt =
"""
You help a FastBusiness ERP (FCode) developer turn a customer requirement into a new CATALOG screen ("danh mục": a master-data list where users add / edit / delete / search records of one table, optionally with a detail grid of lines stored in a second table).
A Bcode tool ("Tạo nhanh danh mục") generates every file (Dir, Grid, Lookup, Filter, Import, Main.aspx) from a spec. You only fill the spec; the developer reviews it before anything is written.

Reply with ONE JSON object and nothing else (no markdown fences, no text before or after):
{
  "loai": "danh_muc" or "khac",
  "tom_tat": "1-2 sentences in Vietnamese: what the requirement asks for",
  "can_bang": [],
  "cau_hoi": [],
  "ghi_chu": [],
  "spec": { ... } or null
}
- "khac" = anything that is not creating a new catalog (changing an existing screen, a report, a voucher, fixing a procedure or data...). Then spec = null and cau_hoi = [].
- "can_bang": names of EXISTING tables (only from the table list) whose columns you still need to see to fill the spec — e.g. the table the requirement means but whose columns were not given. The tool sends their columns and asks you again. Leave it empty once you have what you need.
- "cau_hoi": questions in Vietnamese for the developer when a needed decision cannot be inferred (which table, which key, which columns, whether there is a detail grid...). Do not ask about things you can reasonably default. If you can still produce a draft spec, return it together with the questions.
- "ghi_chu": short Vietnamese notes: assumptions you made, what to check.

spec (keys exactly as below):
  "table": the catalog table. Use an EXISTING table when the requirement names one or one clearly matches (its columns must have been given to you; otherwise ask for it in can_bang). Otherwise propose a NEW table name: lowercase, starting with the prefix "@@PREFIX@@" (e.g. "@@PREFIX@@loaihd" = prefix + initials of the catalog words), and define all its columns with sqlType.
  "controller": file name for Dir/Grid/Lookup (letters, digits, _; usually the table name, e.g. "dmloaihd")
  "main": Main .aspx name without extension (usually the table name, lowercase)
  "keys": key column(s), comma separated string
  "order": order-by columns (usually same as keys)
  "nameField": the name column (ten_xxx) shown by the Lookup
  "titleV", "titleE": screen title Vietnamese / English, e.g. "Danh mục loại hợp đồng" / "Contract types"
  "subV", "subE": subtitle, usually ""
  "funcNew", "funcEdit", "funcDelete", "funcFilter": toolbar buttons New / Edit / Delete / Search (true unless the requirement says otherwise)
  "lookup": true = also create a Lookup file so other screens can pick values from this catalog (usually true)
  "filter": true only if a filter form should appear when the list opens
  "import": true only if the requirement mentions importing from Excel; "import2262": true
  "irregular": true only if codes must not contain special characters; "comment": false
  "columns": [ { "name", "sqlType", "headerV", "headerE", "width", "inGrid", "inForm", "inLookup", "inFilter", "inImport", "allowNulls", "readOnly", "hidden", "autoComplete" } ]
     One entry per column of the table, in display order. For an existing table use exactly its column names (all of them).
     System columns datetime0, datetime2, user_id0, user_id2: inGrid false, inForm false, inImport false.
     Key columns: allowNulls false, inForm true, inGrid true, inLookup true. The name column: allowNulls false, inLookup true, inFilter true.
     A column whose phrase names ANOTHER catalog ("Mã kho" = ma_kho / Site, "Mã khách" = ma_kh / Customer) is a lookup column of that catalog, even if the word also appears in this catalog's own name (catalog "mapping kho": its own code is ma_mk, "Mã kho" is still ma_kho).
     width: number 60-400 (code 100-120, name 250-300, number/date 100).
     autoComplete: for a column that refers to another catalog (ma_kh, ma_vt, ma_nt, ma_bp...): the lookup controller to use, picked from the "ac" list given for that column; for columns of a NEW table (no "ac" list) write "auto" and the tool attaches the standard FCode lookup for that column name. "" = plain input (always "" for the catalog's own key and name columns).
  "detail": true if the catalog has a detail grid; then also
  "detailTable", "detailId" (controller of the detail grid, e.g. controller + "Detail"), "detailTitleV", "detailTitleE",
  "detailKeys" (e.g. "ma_xxx, line_nbr"), "linkKeys" (catalog key column(s) that also exist in the detail table), "detailOrder" ("line_nbr"),
  "detailImport" (false), "detailColumns" (same shape as columns; link columns and line_nbr: inGrid false).
  When detail is false: "detailTable": "", "detailColumns": [].

Conventions for a NEW FastBusiness catalog table: code ma_xxx varchar(32) (key, not null), name ten_xxx nvarchar(256), English name ten_xxx2 nvarchar(256),
the business columns the requirement describes (codes of other catalogs varchar(32), amounts numeric(19,4), quantities numeric(19,4), dates smalldatetime, flags bit or char(1)),
ghi_chu nvarchar(512) when useful, status char(1) (1 = đang sử dụng), datetime0 datetime, datetime2 datetime, user_id0 int, user_id2 int.
A NEW detail table is named catalog table + "ct" (e.g. "@@PREFIX@@loaihdct") and has the catalog key columns + line_nbr int + its own columns + the same system columns.
Headers: Vietnamese with full diacritics ("Mã ...", "Tên ...", "Tên 2", "Ghi chú", "Trạng thái"), English short.
Never invent existing tables or columns: use only names from the schema you were given.
DATA-ENTRY SCREENS: the same tool also builds a NEW single-table screen for declaring / updating figures (e.g. menu "Cập nhật dự toán", "Khai báo kế hoạch / ngân sách / định mức"). Answer loai "danh_muc" for it too. For such a screen:
  - table: prefix "@@DATAPREFIX@@" (the catalog prefix without "dm") + a short name from the menu title without the verb, e.g. "Cập nhật dự toán" → "@@DATAPREFIX@@dutoan". Never use the catalog prefix "@@PREFIX@@" for it.
  - no code/name pair (no ma_xxx / ten_xxx / ten_xxx2 of its own, no status): keys = the dimension columns that identify one row (unit ma_dvcs, year nam, department ma_bp, item ma_vt, ..., update number lan_cap_nhat), leaving out columns the requirement says are optional ("không bắt buộc"); nameField "", lookup false. Ask once in cau_hoi to confirm the key.
  - values "theo 12 tháng" / "mỗi tháng" → 12 numeric columns per measure named short code + 01..12 (e.g. ns_dt01..ns_dt12, sl_dt01..sl_dt12, gia_dt01..gia_dt12, ps_ndt01..ps_ndt12) with inGrid false, unless the developer answered to use a detail grid by month (then detail table = table + "ct", detail keys = keys + thang). Ask once which layout they prefer.
When "FIELD DỰ ÁN ĐANG DÙNG / BẠN ĐÃ CHỐT" lists a field for a phrase of the requirement, use exactly that field name (and its ac as autoComplete) for the column of a NEW table instead of inventing a name; "đã chốt" entries win over "dự án" ones.
For every screen: a phrase like "Lookup từ Danh mục X" / "chọn từ danh mục X" describes the lookup of ONE COLUMN, never the screen to create; use the standard field / controller given under "LOOKUP CHUẨN" when present.
A combined time phrase such as "Tháng / Năm", "Kỳ - Năm", "Quý/Năm" means SEPARATE columns (thang int, nam int, ky int, quy int), never one column.
"bắt buộc nhập" → allowNulls false. Money ("Nhập số tiền"), quantity and price: numeric(19,4); year (nam) and counters (lan_cap_nhat): int.
The menu entry is created afterwards in the tool's Menu tab, so do not ask or note about it. Behaviour beyond the standard catalog (auto-filling fields, formulas, special checks) is coded by hand later: mention it once in ghi_chu, do not ask about it.
""";

    public static string BuildUserPrompt(string requestName, string content, IReadOnlyList<string> tables,
        IReadOnlyDictionary<string, List<AiTableColumn>> columns, IReadOnlyList<(string Question, string Answer)> answers,
        IReadOnlyList<string>? lookupHints = null, IReadOnlyList<string>? projectHints = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"--- YÊU CẦU (mã {requestName}) ---");
        sb.AppendLine(content.Trim());
        sb.AppendLine();

        // Danh sách tên bảng: bỏ bảng theo kỳ (tên có $); quá dài thì chỉ giữ bảng danh mục (dm*) + bảng đã gửi cột.
        var names = tables.Where(t => !t.Contains('$')).ToList();
        if (names.Count > MaxTableNames)
            names = names.Where(t => t.StartsWith("dm", StringComparison.OrdinalIgnoreCase) || t.StartsWith("zdm", StringComparison.OrdinalIgnoreCase) || columns.ContainsKey(t)).Take(MaxTableNames).ToList();
        sb.AppendLine($"--- BẢNG TRONG APP DATA ({names.Count}{(names.Count < tables.Count ? $" / {tables.Count}" : "")}) ---");
        sb.AppendLine(string.Join(", ", names));
        sb.AppendLine();

        sb.AppendLine("--- CỘT CỦA CÁC BẢNG LIÊN QUAN (tên kiểu, PK = khoá chính, ac = lookup controller chuẩn của FCode cho cột) ---");
        if (columns.Count == 0) sb.AppendLine("(chưa có — xin bằng can_bang nếu cần)");
        foreach (var (table, cols) in columns)
        {
            if (cols.Count == 0) { sb.AppendLine($"{table}: (không đọc được cột)"); continue; }
            sb.AppendLine($"{table}:");
            foreach (var c in cols)
            {
                var ac = FieldDictionaryService.Instance.Lookups(c.Name).Select(l => l.Controller).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
                sb.AppendLine($"  {c.Name} {c.SqlType}{(c.IsPrimaryKey ? " PK" : "")}{(ac.Count > 0 ? " ac: " + string.Join(" | ", ac) : "")}");
            }
        }

        if (lookupHints is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("--- LOOKUP CHUẨN (tra từ header.xml / source cho các cụm \"Lookup từ danh mục …\" trong yêu cầu) ---");
            foreach (var h in lookupHints) sb.AppendLine(h);
        }

        if (projectHints is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("--- FIELD DỰ ÁN ĐANG DÙNG / BẠN ĐÃ CHỐT cho các cụm trong yêu cầu (tiêu đề → field, ac = lookup controller) ---");
            foreach (var h in projectHints) sb.AppendLine(h);
        }

        if (answers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- TRẢ LỜI CỦA LẬP TRÌNH VIÊN CHO CÂU HỎI LẦN TRƯỚC ---");
            foreach (var (q, a) in answers)
            {
                sb.AppendLine("Hỏi: " + q.Trim());
                sb.AppendLine("Đáp: " + (string.IsNullOrWhiteSpace(a) ? "(không trả lời — tự chọn hợp lý)" : a.Trim()));
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------- đọc kết quả

    /// <summary>Đọc JSON AI trả (bỏ ```fence``` / chữ thừa quanh object). Không đọc được → loại "khac" kèm câu báo.</summary>
    public static ParsedReply Parse(string raw)
    {
        var r = new ParsedReply { Raw = raw };
        var s = raw.Trim();
        var a = s.IndexOf('{');
        var b = s.LastIndexOf('}');
        JsonObject? o = null;
        if (a >= 0 && b > a)
        {
            try { o = JsonNode.Parse(s[a..(b + 1)]) as JsonObject; } catch { /* không phải JSON hợp lệ */ }
        }
        if (o is null)
        {
            r.Kind = "loi";
            r.Summary = "AI không trả về JSON đọc được — xem nội dung ở Lịch sử gợi ý AI.";
            return r;
        }
        r.Kind = Str(o["loai"]).ToLowerInvariant() is "danh_muc" or "danhmuc" or "danh muc" ? "danh_muc" : "khac";
        r.Summary = Str(o["tom_tat"]);
        r.Questions = Strs(o["cau_hoi"]);
        r.Notes = Strs(o["ghi_chu"]);
        r.WantedTables = Strs(o["can_bang"]);
        r.Spec = r.Kind == "danh_muc" ? o["spec"] as JsonObject : null;
        r.Spec = r.Spec?.DeepClone() as JsonObject;
        return r;
    }

    /// <summary>Kết quả đọc thô (thêm "can_bang" so với <see cref="AiCatalogResult"/>).</summary>
    public sealed class ParsedReply
    {
        public string Kind { get; set; } = "khac";
        public string Summary { get; set; } = "";
        public List<string> Questions { get; set; } = new();
        public List<string> Notes { get; set; } = new();
        public List<string> WantedTables { get; set; } = new();
        public JsonObject? Spec { get; set; }
        public string Raw { get; set; } = "";

        public static implicit operator AiCatalogResult(ParsedReply p) => new()
        {
            Kind = p.Kind, Summary = p.Summary, Questions = p.Questions, Notes = p.Notes, Spec = p.Spec, Raw = p.Raw,
        };
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : n is JsonValue v2 ? v2.ToString() : "";
    private static List<string> Strs(JsonNode? n) => n is JsonArray arr ? arr.Select(Str).Where(x => x.Length > 0).ToList() : new();

    // ---------------------------------------------------------------------------------------- chuẩn hoá spec

    /// <summary>
    /// Đưa spec AI về đúng dạng trang quicklist.html đọc được và đối chiếu với DB: bảng có thật → bỏ cột AI bịa, kiểu lấy từ DB (bỏ sqlType/type của AI);
    /// bảng mới → kiểu từ sqlType AI đề xuất + script CREATE TABLE (chỉ để người dùng xem/chạy tay). autoComplete → lookup chuẩn từ header.xml.
    /// </summary>
    private async Task NormalizeAsync(AiCatalogResult r, HashSet<string> tableSet)
    {
        var spec = r.Spec!;
        string S(string k) => Str(spec[k]);
        bool B(string k, bool def) => spec[k] is JsonValue v && v.TryGetValue<bool>(out var x) ? x : def;

        // Chuỗi ghép dấu phẩy (AI hay trả mảng).
        foreach (var k in new[] { "keys", "order", "detailKeys", "linkKeys", "detailOrder" })
            if (spec[k] is JsonArray arr) spec[k] = string.Join(", ", arr.Select(Str));
        foreach (var k in new[] { "table", "controller", "main", "keys", "order", "nameField", "titleV", "titleE", "subV", "subE",
                     "detailTable", "detailId", "detailTitleV", "detailTitleE", "detailKeys", "linkKeys", "detailOrder" })
            spec[k] = S(k);
        foreach (var (k, def) in new[] { ("funcNew", true), ("funcEdit", true), ("funcDelete", true), ("funcFilter", true), ("lookup", true),
                     ("filter", false), ("import", false), ("import2262", true), ("irregular", false), ("comment", false), ("detail", false), ("detailImport", false) })
            spec[k] = B(k, def);
        if (S("controller").Length == 0) spec["controller"] = S("table");
        if (S("main").Length == 0) spec["main"] = S("table").ToLowerInvariant();
        if (S("order").Length == 0) spec["order"] = S("keys");
        if (!B("detail", false)) { spec["detailTable"] = ""; spec["detailColumns"] = new JsonArray(); }

        var masterKeys = SplitCsv(S("keys"));
        var newTableScripts = new List<string>();
        await NormalizeColumnsAsync(r, "columns", S("table"), masterKeys, tableSet, newTableScripts);
        if (B("detail", false) && S("detailTable").Length > 0)
            await NormalizeColumnsAsync(r, "detailColumns", S("detailTable"), SplitCsv(S("detailKeys")), tableSet, newTableScripts);
        r.CreateTableSql = string.Join("\r\n", newTableScripts);

        // Cột tên không có trong danh sách cột → để trống (trang báo thiếu khi bật Lookup).
        var names = ((spec["columns"] as JsonArray) ?? new JsonArray()).Select(c => Str(c?["name"])).ToList();
        if (S("nameField").Length > 0 && !names.Contains(S("nameField"), StringComparer.OrdinalIgnoreCase))
        {
            r.Notes.Add($"Cột tên '{S("nameField")}' không có trong bảng — chọn lại ở ô Cột tên (Lookup).");
            spec["nameField"] = "";
        }
        var badKeys = masterKeys.Where(k => !names.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (badKeys.Count > 0) r.Notes.Add($"Khoá '{string.Join(", ", badKeys)}' không có trong bảng — kiểm tra ô Key.");
    }

    private async Task NormalizeColumnsAsync(AiCatalogResult r, string prop, string table, List<string> keys, HashSet<string> tableSet, List<string> scripts)
    {
        var spec = r.Spec!;
        var aiCols = (spec[prop] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
        var exists = table.Length > 0 && tableSet.Contains(table);
        var real = exists ? await ColumnsAsync(table) : new List<AiTableColumn>();
        var realByName = real.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var outCols = new JsonArray();
        var dropped = new List<string>();

        foreach (var c in aiCols)
        {
            var name = Str(c["name"]);
            if (name.Length == 0) continue;
            if (exists && !realByName.ContainsKey(name)) { dropped.Add(name); continue; }
            var col = new JsonObject
            {
                ["name"] = exists ? realByName[name].Name : name,
                ["headerV"] = Str(c["headerV"]) is { Length: > 0 } hv ? hv : name,
                ["headerE"] = Str(c["headerE"]) is { Length: > 0 } he ? he : name,
                ["width"] = (int.TryParse(Str(c["width"]), out var w) && w > 0 ? w : 150).ToString(),
            };
            foreach (var f in new[] { "inGrid", "inForm", "inLookup", "inFilter", "inImport", "allowNulls", "readOnly", "hidden", "hideName" })
                col[f] = c[f] is JsonValue v && v.TryGetValue<bool>(out var x) ? x : f is "inGrid" or "inForm" or "inImport" or "allowNulls";
            if (keys.Contains(name, StringComparer.OrdinalIgnoreCase)) { col["allowNulls"] = false; col["inForm"] = true; }
            if (!exists)
            {
                var sqlType = Str(c["sqlType"]) is { Length: > 0 } st ? st : "nvarchar(128)";
                col["sqlType"] = sqlType;
                col["type"] = CatalogCloneService.GuessFieldType(sqlType);
            }
            if (ResolveLookup(name, Str(c["autoComplete"])) is { } l)
                col["lookup"] = new JsonObject { ["controller"] = l.Controller, ["reference"] = l.Reference, ["key"] = l.Key, ["check"] = l.Check, ["information"] = l.Information };
            outCols.Add(col);
        }
        spec[prop] = outCols;
        if (dropped.Count > 0) r.Notes.Add($"Bỏ cột AI nêu nhưng bảng {table} không có: {string.Join(", ", dropped)}.");

        if (!exists && table.Length > 0)
        {
            var list = outCols.OfType<JsonObject>().Select(c => new AiTableColumn(Str(c["name"]), Str(c["sqlType"]),
                keys.Contains(Str(c["name"]), StringComparer.OrdinalIgnoreCase))).ToList();
            r.NewTables[table] = list;
            // Tái dùng script tạo bảng của "Clone danh mục" — chỉ hiện cho người dùng, không tự chạy. Script đó đặt PK trên 1 cột → khoá ghép thì nối đủ cột.
            var script = CatalogCloneService.BuildTableScript(new CatalogSpec
                {
                    Table = table,
                    Columns = outCols.OfType<JsonObject>().Select(c => new CatalogColumn
                    {
                        Name = Str(c["name"]), SqlType = Str(c["sqlType"]), Type = Str(c["type"]),
                        IsKey = keys.Contains(Str(c["name"]), StringComparer.OrdinalIgnoreCase),
                        AllowNulls = c["allowNulls"] is JsonValue av && av.TryGetValue<bool>(out var an) && an,
                    }).ToList(),
                });
            var keyCols = list.Where(c => c.IsPrimaryKey).Select(c => $"[{c.Name}]").ToList();
            if (keyCols.Count > 1)
                script = script.Replace($"PRIMARY KEY CLUSTERED ({keyCols[0]})", $"PRIMARY KEY CLUSTERED ({string.Join(", ", keyCols)})");
            scripts.Add($"-- Bảng {table} chưa có trong App Data ({(r.Engine == FastCatalogRules.Engine ? "Bcode" : "AI")} đề xuất). Xem lại rồi tự chạy trước khi dùng danh mục.\r\n" + script);
        }
    }

    /// <summary>autoComplete AI / luật chọn → biến thể lookup: header.xml khớp controller → tra cứu bạn đã chốt → source dự án → biến thể đơn giản của header.xml; không có thì bỏ.</summary>
    private FieldLookup? ResolveLookup(string column, string controller) =>
        controller.Length == 0 ? null : FastCatalogRules.KnownLookup(column, controller, _sourcePath);

    /// <summary>
    /// Tiêu đề field (đã bỏ dấu, ≥ 4 ký tự) xuất hiện nguyên cụm trong yêu cầu → field bạn đã chốt / field source dự án dùng nhiều nhất.
    /// Gửi kèm AI để đặt tên cột như dự án; tối đa 40 dòng, cụm dài (cụ thể hơn) trước.
    /// </summary>
    private List<string> ProjectHints(string text)
    {
        var folded = " " + Regex.Replace(FastCatalogRules.Fold(text), @"[^a-z0-9]+", " ") + " ";
        var lines = new List<(int Len, string Line)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (h, m) in CatalogFieldMemory.InText(folded))
            if (seen.Add(h)) lines.Add((1000 + h.Length, $"“{m.V}” → {m.Name}{(m.SqlType.Length > 0 ? " " + m.SqlType : "")}{(m.Controller.Length > 0 ? " ac: " + m.Controller : "")} (đã chốt)"));
        if (SourceFieldIndex.TryGet(_sourcePath) is { } idx)
            foreach (var h in idx.Headers)
            {
                if (h.Length < 4 || seen.Contains(h) || !folded.Contains(" " + Regex.Replace(h, @"[^a-z0-9]+", " ").Trim() + " ", StringComparison.Ordinal)) continue;
                var u = idx.ByHeader(h)[0];
                if (!seen.Add(h)) continue;
                lines.Add((h.Length, $"“{u.V}” → {u.Name}{(u.Type.Length > 0 ? " type " + u.Type : "")}{(u.Lookup is { } l ? " ac: " + l.Controller : "")} (dự án, {u.Count} file)"));
            }
        return lines.OrderByDescending(x => x.Len).Take(40).Select(x => x.Line).ToList();
    }

    private static List<string> SplitCsv(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
