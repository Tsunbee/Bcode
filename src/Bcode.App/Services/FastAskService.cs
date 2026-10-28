using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// "Hỏi Fast" — thanh chat hỏi kiến thức Fast KHÔNG qua AI: hiểu câu hỏi bằng từ khoá (không dấu), hỏi lại bằng nút bấm, trả về thẻ có code để chèn
/// vào SQL Query / mở tab mới. Nguồn kiến thức (đều có sẵn trong Bcode):
/// <list type="number">
/// <item>Mẫu code + cách gọi quen thuộc (<see cref="SqlHintCatalog"/>).</item>
/// <item>Ma trận ghost text (<see cref="FastGhostRules.KnowledgeFor"/>): tham số EXEC / hàm hay dùng, dòng lọc @Key, kiểu biến.</item>
/// <item>Store / function của chính database đang kết nối (chỉ SELECT sys.objects / sys.sql_modules).</item>
/// <item>Cấu trúc bảng thật (cột, khoá, identity) + câu JOIN mẫu.</item>
/// <item>Từ điển field Fast (header.xml — <see cref="FieldDictionaryService"/>, field dùng trong source dự án — <see cref="SourceFieldIndex"/>).</item>
/// <item>Luật danh mục (Tạo nhanh danh mục "Theo luật Fast").</item>
/// <item>Quy ước khi viết / chạy store Fast (%Partition, %[ ]%, #report sysorder, @Key, SET NOCOUNT…).</item>
/// </list>
/// Trạng thái hội thoại (đang ở bước nào của "tạo báo cáo"…) do trang giữ và gửi kèm mỗi câu — dịch vụ không nhớ gì giữa các lần hỏi.
/// </summary>
public sealed class FastAskService
{
    /// <summary>Nút trên thẻ: insert = chèn vào SQL Query đang dùng, newtab = mở tab SQL Query mới, copy, open-object (Arg = tên, Arg2 = "sys"/"app"),
    /// quicklist (Arg = tên danh mục, Arg2 = mô tả cột), ask (gửi Arg như người dùng gõ). open-object: Arg2 = "app|P" / "sys|FN" (database | loại object).</summary>
    public sealed record AskAction(string Kind, string Label, string Arg = "", string Arg2 = "");
    public sealed record AskCard(string Title, string Text, string Code, IReadOnlyList<AskAction> Actions, string Source)
    {
        /// <summary>Thẻ này là gì (để chấm ✔ / ✘): "m|loại|khoá|cách viết" = ô ma trận ghost text, "store|tên", "answer|…", "snip|tiền tố", "join|a|b", "field|tên", "topic|tiêu đề".</summary>
        public string Ref { get; init; } = "";
    }
    public sealed record AskChoice(string Label, string Send);
    /// <param name="Multi">Các lựa chọn là chọn nhiều (trang hiện ô tick + nút Xong, gửi "<c>MultiPrefix</c>nhãn1|nhãn2").</param>
    public sealed record AskReply(string Text, IReadOnlyList<AskCard> Cards, IReadOnlyList<AskChoice> Choices, Dictionary<string, string> State,
        bool Multi = false, string MultiPrefix = "")
    {
        /// <summary>Chữ ký câu hỏi (gửi kèm khi chấm ✔ / ✘).</summary>
        public string Sig { get; init; } = "";
    }

    private readonly DbConnectionService _connections;
    private readonly TableDataService _tables;

    public FastAskService(DbConnectionService connections, TableDataService tables)
    {
        _connections = connections;
        _tables = tables;
    }

    // ---------------------------------------------------------------- tiện ích chữ

    /// <summary>Bỏ dấu tiếng Việt + chữ thường ("Báo cáo" → "bao cao").</summary>
    public static string Fold(string s)
    {
        var n = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (var c in n) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Replace('đ', 'd').Replace('Đ', 'D').ToLowerInvariant();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "tao", "lam", "viet", "cho", "toi", "minh", "can", "the", "nao", "gi", "la", "va", "voi", "cua", "mot", "cac", "nhung", "co", "khong", "duoc",
        "hoi", "giup", "nhe", "a", "o", "de", "trong", "theo", "tu", "den", "ve", "ra", "vao", "hay", "nhu", "sao", "di", "bang", "this", "how", "to",
    };

    private static List<string> Words(string folded) =>
        Regex.Matches(folded, @"[\w$#@]+").Select(m => m.Value).Where(w => w.Length >= 2 && !StopWords.Contains(w)).Distinct().ToList();

    /// <summary>Snippet → chữ thường: ${n:mặc định} → mặc định (chỗ nhắc lại ${n} lấy đúng giá trị đó), ${n|a,b|} → a, $0 bỏ, ¤ → $.</summary>
    public static string Plain(string body, IReadOnlyDictionary<int, string>? fill = null)
    {
        var defaults = new Dictionary<int, string>();
        foreach (Match m in Regex.Matches(body, @"\$\{(\d+):([^}]*)\}")) defaults.TryAdd(int.Parse(m.Groups[1].Value), m.Groups[2].Value);
        foreach (Match m in Regex.Matches(body, @"\$\{(\d+)\|([^,|}]*)[^}]*\}")) defaults.TryAdd(int.Parse(m.Groups[1].Value), m.Groups[2].Value);
        if (fill is not null) foreach (var (k, v) in fill) defaults[k] = v;
        string Val(Match m) => defaults.TryGetValue(int.Parse(m.Groups[1].Value), out var v) ? v : "";
        var s = Regex.Replace(body, @"\$\{(\d+):[^}]*\}", Val);
        s = Regex.Replace(s, @"\$\{(\d+)\|[^}]*\}", Val);
        s = Regex.Replace(s, @"\$\{(\d+)\}", Val);
        s = Regex.Replace(s, @"\$(\d+)", m => m.Groups[1].Value == "0" ? "" : Val(m));
        return s.Replace('¤', '$');
    }

    private static SqlSnippet? Snip(string prefix) => SqlHintCatalog.Snippets.FirstOrDefault(s => s.Prefix == prefix);

    private static List<AskAction> CodeActions(string code) =>
        new() { new("insert", "Chèn vào SQL Query", code), new("newtab", "Mở tab SQL mới", code), new("copy", "Copy", code) };

    private static AskCard SnippetCard(SqlSnippet s, string source = "Mẫu code Fast") =>
        new(s.Name, s.Description + $"  (trong editor: gõ \"{s.Prefix}\" rồi Tab)", Plain(s.Body), CodeActions(Plain(s.Body)), source);

    private static AskReply Reply(string text, IEnumerable<AskCard>? cards = null, IEnumerable<AskChoice>? choices = null, Dictionary<string, string>? state = null,
        bool multi = false, string multiPrefix = "") =>
        new(text, (cards ?? Array.Empty<AskCard>()).ToList(), (choices ?? Array.Empty<AskChoice>()).ToList(), state ?? new(), multi, multiPrefix);

    private static readonly AskChoice[] StartChoices =
    {
        new("Tạo báo cáo", "tạo báo cáo"), new("Tạo danh mục", "tạo danh mục"), new("Viết store / function", "viết store"),
        new("Cấu trúc bảng / JOIN", "cấu trúc bảng"), new("Tra trường (field)", "tra trường"), new("Quy ước Fast", "quy ước fast"),
    };

    // ---------------------------------------------------------------- 8. quy ước Fast

    /// <summary>1 chủ đề quy ước: tiêu đề, từ khoá (không dấu) để nhận ra câu hỏi, giải thích, các mẫu code liên quan.</summary>
    private sealed record Topic(string Title, string[] Keys, string Text, string[] Snippets);

    private static readonly Topic[] Topics =
    {
        new("Bảng phân kỳ r…$%Partition / $000000",
            new[] { "partition", "phan ky", "$000000", "000000", "partition$execute", "theo ky" },
            "Dữ liệu chứng từ chia theo kỳ. Trong SQL động viết bảng dạng r00$%Partition rồi chạy bằng FastBusiness$Partition$Execute @q, @Unit|NULL, 'a.ngay_ct', @DateFrom, @DateTo, @UserID, @Admin " +
            "(cách gọi hay gặp nhất trong code Fast): %Partition được thay theo từng kỳ nằm trong khoảng ngày (SUY RA từ cách các store dùng). Bảng …$000000 là bảng gộp các kỳ — " +
            "dùng để lấy cấu trúc cột (SELECT TOP 0 … INTO #report FROM r00$000000). Bcode khi kiểm tra cú pháp SQL động (Debug từng bước) đổi %Partition → 000000.",
            new[] { "part", "rsrepq" }),
        new("Điều kiện %[ … ]% trong SQL động",
            new[] { "%[", "]%", "%[ ]%", "dieu kien dong" },
            "Trong câu chạy bằng Partition$Execute, điều kiện lọc viết dạng where %[' + @Key + ']%. Bcode khi kiểm tra cú pháp (Debug từng bước → xem SQL động) coi %[ … ]% như ( … ). " +
            "Lỗi hay gặp: thừa / thiếu dấu nháy khi nối chuỗi trong @Key (nháy trong chuỗi phải gấp đôi '').",
            new[] { "part", "key" }),
        new("#report với sysorder / sysprint / systotal",
            new[] { "sysorder", "sysprint", "systotal", "#report", "report" },
            "Báo cáo trả #report có 3 cột đầu sysorder, sysprint, systotal (460 store dùng). Thường: SELECT TOP 0 5 AS sysorder, 1 AS sysprint, 1 AS systotal, … INTO #report FROM bảng$000000 " +
            "để lấy cấu trúc, dòng chi tiết sysorder = 5; dòng tổng cộng chèn thêm (mẫu \"total\": chữ Tổng cộng lấy ở reports.ccode = 'TOTAL'). Cuối cùng SELECT * FROM #report ORDER BY sysorder.",
            new[] { "sysrep", "total" }),
        new("SET NOCOUNT / ANSI_NULLS đầu – cuối store",
            new[] { "nocount", "ansi_nulls", "ansi nulls" },
            "Store Fast mở đầu SET NOCOUNT ON + SET ANSI_NULLS OFF và trả lại SET NOCOUNT OFF + SET ANSI_NULLS ON trước END cuối (ghost text Fast tự gợi ý 2 dòng này).",
            new[] { "rsrep" }),
        new("Dựng điều kiện @Key + GetCheckKey",
            new[] { "@key", "getcheckkey", "dieu kien loc", "loc du lieu", "key" },
            "Điều kiện lọc gom vào chuỗi @Key: SET @Key = ' status = ''1''' (hoặc ''), mỗi tham số lọc 1 dòng IF @x <> '' SET @Key = @Key + ' and a.cot like ''…%''', " +
            "IF @UnitKey IS NOT NULL SET @Key = @Key + ' and ' + @UnitKey, cuối cùng SET @Key = dbo.FastBusiness$Function$System$GetCheckKey(@Key) (662 lần trong code Fast).",
            new[] { "key", "flike", "feq", "fin", "frange", "fkey" }),
        new("Lọc theo đơn vị @UnitKey (GetUnitFilter)",
            new[] { "unitkey", "getunitfilter", "don vi", "ma_dvcs" },
            "SET @UnitKey = dbo.FastBusiness$Function$System$GetUnitFilter('ma_dvcs', @Unit, @UserID, @Admin) (hoặc 'a.ma_dvcs' khi có alias) rồi IF @UnitKey IS NOT NULL ghép vào @Key.",
            new[] { "unit", "fkey" }),
        new("Tên song ngữ theo @Language",
            new[] { "song ngu", "@language", "language", "ten_2", "tieng anh" },
            "CASE WHEN @Language = 'V' THEN ten_x ELSE ten_x2 END AS ten_x (334 lần). Trong editor gõ \"bil <bảng>\" để sinh cho mọi cặp ten_x / ten_x2 của bảng.",
            new[] { "bil" }),
        new("Trường động của báo cáo (GetDynamicFields)",
            new[] { "truong dong", "getdynamicfields", "dynamic field", "basefields" },
            "Cột người dùng tự thêm ở màn hình báo cáo: khai báo 9 biến @baseFields… rồi EXEC FastBusiness$Report$GetDynamicFields, ghép @baseAliasFields / @queryJoin vào câu SQL động.",
            new[] { "dynf" }),
        new("Số dư (FastBusiness$Balance$*)",
            new[] { "so du", "balance", "ton dau", "du dau", "du cuoi" },
            SqlHintCatalog.GroupNotes.FirstOrDefault(g => g.Title.StartsWith("Số dư")).Text ?? "",
            new[] { "balacc", "balcust", "balitem", "baljob", "balaccu", "balcustu", "baljobc", "balctc", "balfee", "balcat" }),
        new("Cursor / SQL động",
            new[] { "cursor", "sp_executesql", "sql dong", "vong lap" },
            "SQL động: dựng chuỗi @q rồi EXEC sp_executesql @q (1.126 lần). Vòng lặp: DECLARE … CURSOR … WHILE @@FETCH_STATUS = 0 … CLOSE / DEALLOCATE (222 store).",
            new[] { "dyn", "cur" }),
    };

    /// <summary>Câu hỏi nêu rõ đối tượng (vd "số dư đầu kỳ theo vụ việc") → chỉ mẫu khớp nhất, đứng trước thẻ quy ước;
    /// không nêu thì 4 mẫu đầu như cũ. Mẫu số dư: "cuối kỳ" đổi @DateFrom → @DateTo, @BalanceType 1 → 2.</summary>
    private static IEnumerable<AskCard> TopicCards(Topic t, string f)
    {
        var snips = t.Snippets.Select(Snip).Where(s => s is not null).Select(s => s!).ToList();
        var q = Words(f).ToHashSet();
        var common = Words(Fold(t.Title)).Append("new").ToHashSet();
        (int Hit, int Miss) Score(SqlSnippet s)
        {
            var w = Words(Fold(s.Name)).Where(x => !common.Contains(x)).ToList();
            var hit = w.Count(q.Contains);
            return (hit, w.Count - hit);
        }
        var scored = snips.Select(s => (S: s, Sc: Score(s))).ToList();
        var maxHit = scored.Count > 0 ? scored.Max(x => x.Sc.Hit) : 0;
        var topic = new AskCard(t.Title, t.Text, "", Array.Empty<AskAction>(), "Quy ước Fast");
        if (maxHit == 0)
        {
            yield return topic;
            foreach (var s in snips.Take(4)) yield return SnippetCard(s);
            yield break;
        }
        var minMiss = scored.Where(x => x.Sc.Hit == maxHit).Min(x => x.Sc.Miss);
        var closing = Regex.IsMatch(f, @"\b(cuoi ky|du cuoi|ton cuoi|cuoi)\b") && !Regex.IsMatch(f, @"\b(dau ky|du dau|ton dau)\b");
        foreach (var (s, _) in scored.Where(x => x.Sc.Hit == maxHit && x.Sc.Miss == minMiss))
        {
            var card = SnippetCard(s);
            if (closing && card.Code.Contains("FastBusiness$Balance$"))
            {
                var code = Regex.Replace(card.Code, @"(EXEC FastBusiness\$Balance\$\w+ )@DateFrom(, [^\r\n]*?), 1, ", "$1@DateTo$2, 2, ");
                card = card with { Title = card.Title + " — cuối kỳ", Code = code, Actions = CodeActions(code) };
            }
            yield return card;
        }
        yield return topic;
    }

    // ---------------------------------------------------------------- dữ liệu database (chỉ đọc, nhớ theo workspace)

    private sealed class DbInfo
    {
        public List<string> Tables { get; init; } = new();
        public List<(string Name, string Type)> Modules { get; init; } = new();
    }

    private static readonly ConcurrentDictionary<string, Task<DbInfo>> DbCache = new();

    private Task<DbInfo> DbAsync(bool useSys)
    {
        var stamp = _connections.CurrentStamp(useSys);
        if (stamp.Length == 0) return Task.FromResult(new DbInfo());
        return DbCache.GetOrAdd(stamp, _ => Task.Run(async () =>
        {
            var tables = new List<string>();
            var modules = new List<(string, string)>();
            try
            {
                await using var conn = _connections.CreateConnection(useSys);
                await conn.OpenAsync();
                await using (var cmd = new SqlCommand("SELECT name FROM sys.tables WHERE is_ms_shipped = 0 AND (name NOT LIKE '%$%' OR name LIKE '%$000000')", conn) { CommandTimeout = 60 })
                await using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) tables.Add(r.GetString(0));
                await using (var cmd = new SqlCommand("SELECT name, type FROM sys.objects WHERE type IN ('P','FN','IF','TF') AND is_ms_shipped = 0", conn) { CommandTimeout = 60 })
                await using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync()) modules.Add((r.GetString(0), r.GetString(1).Trim()));
            }
            catch { /* không đọc được → bỏ phần dữ liệu database */ }
            return new DbInfo { Tables = tables, Modules = modules };
        }));
    }

    private static readonly ConcurrentDictionary<string, Task<List<(string Table, int Uses)>>> PartCache = new();

    /// <summary>Bảng phân kỳ hay dùng nhất trong code của chính database: đếm số store có "xxx$%Partition" (1 lần quét các định nghĩa có %Partition, nhớ theo workspace).</summary>
    private Task<List<(string Table, int Uses)>> PartitionTablesAsync(bool useSys)
    {
        var stamp = _connections.CurrentStamp(useSys);
        if (stamp.Length == 0) return Task.FromResult(new List<(string, int)>());
        return PartCache.GetOrAdd(stamp, _ => Task.Run(async () =>
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                await using var conn = _connections.CreateConnection(useSys);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand("SELECT m.definition FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.type = 'P' AND m.definition LIKE '%$[%]Partition%'", conn) { CommandTimeout = 90 };
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    foreach (var t in Regex.Matches(r.GetString(0), @"\b([A-Za-z]\w{1,15})\$%Partition").Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct())
                        counts[t] = counts.GetValueOrDefault(t) + 1;
            }
            catch { /* bỏ qua */ }
            return counts.OrderByDescending(x => x.Value).Select(x => (x.Key, x.Value)).ToList();
        }));
    }

    /// <summary>Store / function trong database có tên khớp từ khoá (tên tiếng Anh suy từ từ tiếng Việt) hoặc nội dung chứa đủ các chuỗi bắt buộc.</summary>
    private async Task<List<(string Name, string Type, int Score)>> SearchModulesAsync(bool useSys, IReadOnlyList<string> nameStems, IReadOnlyList<string> mustContain, string? namePrefix, int top)
    {
        var res = new List<(string, string, int)>();
        if (_connections.Current is null) return res;
        try
        {
            await using var conn = _connections.CreateConnection(useSys);
            await conn.OpenAsync();
            var sb = new StringBuilder("SELECT TOP 300 o.name, o.type, ");
            sb.Append(nameStems.Count == 0 ? "0" : string.Join(" + ", nameStems.Select((_, i) => $"CASE WHEN o.name LIKE @n{i} THEN 1 ELSE 0 END")));
            sb.Append(" AS score FROM sys.objects o JOIN sys.sql_modules m ON m.object_id = o.object_id WHERE o.type IN ('P','FN','IF','TF') AND o.is_ms_shipped = 0");
            if (namePrefix is not null) sb.Append(" AND o.name LIKE @pre");
            for (var i = 0; i < mustContain.Count; i++) sb.Append($" AND m.definition LIKE @c{i}");
            if (nameStems.Count > 0) sb.Append(" AND (" + string.Join(" OR ", nameStems.Select((_, i) => $"o.name LIKE @n{i}")) + ")");
            sb.Append(" ORDER BY score DESC, LEN(o.name)");
            await using var cmd = new SqlCommand(sb.ToString(), conn) { CommandTimeout = 60 };
            string Like(string x) => "%" + x.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";
            for (var i = 0; i < nameStems.Count; i++) cmd.Parameters.AddWithValue($"@n{i}", Like(nameStems[i]));
            for (var i = 0; i < mustContain.Count; i++) cmd.Parameters.AddWithValue($"@c{i}", Like(mustContain[i]));
            if (namePrefix is not null) cmd.Parameters.AddWithValue("@pre", namePrefix.Replace("_", "[_]") + "%");
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync() && res.Count < top) res.Add((r.GetString(0), r.GetString(1).Trim(), r.GetInt32(2)));
        }
        catch { /* bỏ qua */ }
        return res;
    }

    private static AskCard ModuleCard(string name, string type, bool useSys, string why) =>
        new(name, (type == "P" ? "Store" : "Function") + " trong database đang kết nối" + (why.Length > 0 ? " — " + why : ""), "",
            new List<AskAction>
            {
                new("open-object", "Mở nội dung", name, (useSys ? "sys" : "app") + "|" + type), new("ask", "Save as…", "saveas:" + name, "Save as từ " + name), new("copy", "Copy tên", name),
            }, "Database đang kết nối");

    /// <summary>Từ tiếng Việt hay gặp → chữ thường gặp trong tên store Fast (để tìm store mẫu theo tên).</summary>
    private static readonly (string Vi, string[] En)[] Glossary =
    {
        ("bang ke", new[] { "List" }), ("chung tu", new[] { "Voucher", "Transaction" }), ("so cai", new[] { "GL", "Ledger" }), ("so chi tiet", new[] { "Ledger", "Detail" }),
        ("ton kho", new[] { "Stock", "Inventory" }), ("nhap xuat ton", new[] { "Inventory", "Stock" }), ("nhap kho", new[] { "Receipt", "Inventory" }), ("xuat kho", new[] { "Issue", "Inventory" }),
        ("cong no", new[] { "Debt", "Customer", "Balance" }), ("khach hang", new[] { "Customer" }), ("nha cung cap", new[] { "Supplier", "Vendor" }), ("vat tu", new[] { "Item" }),
        ("hang hoa", new[] { "Item" }), ("doanh thu", new[] { "Sales", "Revenue" }), ("ban hang", new[] { "Sales" }), ("mua hang", new[] { "Purchase" }),
        ("tai san", new[] { "Asset" }), ("cong cu", new[] { "Tool" }), ("luong", new[] { "Payroll", "Salary" }), ("hoa don", new[] { "Invoice" }), ("thue", new[] { "Tax", "VAT" }),
        ("tien mat", new[] { "Cash" }), ("ngan hang", new[] { "Bank" }), ("so du", new[] { "Balance" }), ("tong hop", new[] { "Summary", "Aggregation" }),
        ("chi phi", new[] { "Expense", "Cost" }), ("gia thanh", new[] { "Cost" }), ("hop dong", new[] { "Contract" }), ("vu viec", new[] { "Job" }),
        ("bo phan", new[] { "Department" }), ("kho", new[] { "Site", "Stock" }), ("tai khoan", new[] { "Account" }), ("phai thu", new[] { "Receivable", "Customer" }),
        ("phai tra", new[] { "Payable", "Supplier" }), ("don hang", new[] { "Order" }), ("phieu thu", new[] { "Receipt", "Cash" }), ("phieu chi", new[] { "Payment", "Cash" }),
        ("ke toan", new[] { "GL", "Account" }), ("bao cao tai chinh", new[] { "Financial" }), ("luu chuyen tien", new[] { "CashFlow" }), ("san xuat", new[] { "Production", "MO" }),
    };

    private static List<string> Stems(string folded)
    {
        var res = new List<string>();
        foreach (var (vi, en) in Glossary) if (Regex.IsMatch(folded, @"\b" + Regex.Escape(vi) + @"\b")) res.AddRange(en);
        // từ tiếng Anh / tên kỹ thuật người dùng gõ thẳng
        res.AddRange(Regex.Matches(folded, @"\b[a-z][a-z0-9_$]{3,}\b").Select(m => m.Value)
            .Where(w => !StopWords.Contains(w) && Glossary.All(g => !g.Vi.Split(' ').Contains(w)) && Regex.IsMatch(w, "^[a-z0-9_$]+$") && !IsVietnameseWord(w)));
        return res.Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList();
    }

    // từ tiếng Việt không dấu hay gặp (không dùng làm từ khoá tìm tên store)
    private static readonly HashSet<string> ViWords = new(StringComparer.Ordinal)
    {
        "tao", "bao", "cao", "danh", "muc", "store", "function", "ham", "viet", "theo", "thang", "quy", "nam", "ngay", "loai", "nhom", "dong", "cot", "truong",
        "bang", "cau", "truc", "join", "tra", "quy", "uoc", "fast", "xem", "mau", "gom", "nhung", "chi", "tiet", "tong", "hop",
    };
    private static bool IsVietnameseWord(string w) => ViWords.Contains(w);

    // ---------------------------------------------------------------- điểm vào

    private async Task<AskReply> AskCoreAsync(string text, Dictionary<string, string> state, bool useSys, CancellationToken ct = default)
    {
        text = (text ?? "").Trim();
        var f = Fold(text);
        state ??= new();

        // đang trong 1 luồng hỏi lại → câu trả lời của bước đó
        if (state.TryGetValue("flow", out var flow))
        {
            if (flow == "report" && text == "type:saveas")
                return await SaveAsFlowAsync("", new() { ["flow"] = "saveas", ["topic"] = state.GetValueOrDefault("topic") ?? "" }, useSys);
            if (flow == "report" && (text.StartsWith("type:") || text.StartsWith("table:") || text.StartsWith("filters:") || text.StartsWith("extra:")
                                     || (state.GetValueOrDefault("step") == "table" && Regex.IsMatch(text, @"^[\w$#]+(?:[\s,|]+[\w$#]+)*$"))))
                return await ReportFlowAsync(text, state, useSys);
            if (flow == "catalog" && state.GetValueOrDefault("step") is "name" or "cols")
                return CatalogFlow(text, state);
            if (flow == "saveas" && !text.StartsWith("saveas:") && state.GetValueOrDefault("step") is "source" or "newname" && Regex.IsMatch(text, @"^\[?[\w$#.]+\]?$"))
                return await SaveAsFlowAsync(text, state, useSys);
        }
        state = new();

        // "Save as" từ 1 store có sẵn (nút trên thẻ store / chọn ở bước loại báo cáo / gõ "save as rs_…")
        if (text.StartsWith("saveas:"))
            return await SaveAsFlowAsync("", new() { ["flow"] = "saveas", ["source"] = text[7..].Trim() }, useSys);
        if (Regex.IsMatch(f, @"\bsave ?as\b|\bnhan ban\b|\bsao chep (store|bao cao)\b"))
        {
            var st = new Dictionary<string, string> { ["flow"] = "saveas", ["topic"] = text };
            if (Regex.Match(text, @"\b(?:dbo\.)?(?<n>(?:z?rs|fs|bs|z)_?\w*\$?\w+)\b", RegexOptions.IgnoreCase) is { Success: true } sm && sm.Groups["n"].Value.Contains('_'))
                st["source"] = sm.Groups["n"].Value;
            return await SaveAsFlowAsync("", st, useSys);
        }

        if (f.Length == 0 || Regex.IsMatch(f, @"^(xin chao|chao|hello|hi|help|tro giup|\?)$"))
            return Reply("Mình tra kiến thức Fast có sẵn trong Bcode (mẫu code, ma trận ghost text, store / bảng của database đang kết nối, từ điển field, luật danh mục, quy ước Fast) — không gọi AI. Bạn cần gì?",
                choices: StartChoices);

        // đếm bảng phân kỳ (quét định nghĩa store, vài giây lần đầu) chạy nền ngay từ đầu để tới bước chọn bảng là có sẵn
        if (Regex.IsMatch(f, @"\b(bao cao|report)\b") || f.StartsWith("rs_")) _ = PartitionTablesAsync(useSys);
        if (Regex.IsMatch(f, @"\b(bao cao|report)\b") || f.StartsWith("rs_")) return await ReportFlowAsync("", new() { ["flow"] = "report", ["topic"] = text }, useSys);
        if (Regex.IsMatch(f, @"\bdanh muc\b|\bcatalog\b")) return CatalogFlow("", new() { ["flow"] = "catalog", ["topic"] = text });

        // bảng thật trong database: cấu trúc / JOIN (trước tên routine — "r70$000000" là bảng, không phải routine)
        var db = await DbAsync(useSys);
        var tableHits = Regex.Matches(text, @"[#\w$]+").Select(m => m.Value)
            .Select(w => db.Tables.FirstOrDefault(t => t.Equals(w, StringComparison.OrdinalIgnoreCase))).Where(t => t is not null).Distinct().ToList();
        if (tableHits.Count > 0) return await TablesAsync(tableHits!, useSys);

        // tên routine (FastBusiness$…, ff_…, fs_…, rs_…) → cách gọi hay dùng + mở định nghĩa
        if (Regex.Match(text, @"(?:dbo\.)?\b(?<n>[A-Za-z_]\w*\$[A-Za-z][\w$]*|ff_\w+|fs_\w+|rs_\w+|zrs_\w+|sp_executesql)\b", RegexOptions.IgnoreCase) is { Success: true } rm)
            return await RoutineAsync(rm.Groups["n"].Value, useSys);
        if (Regex.IsMatch(f, @"\b(cau truc|join|ket noi bang)\b") && !Regex.IsMatch(f, @"\bbao cao\b"))
            return Reply("Bạn gõ tên bảng (1 bảng = xem cấu trúc, 2 bảng = câu JOIN mẫu), vd \"dmkh\" hoặc \"join ct70 dmvt\".");

        // quy ước
        var topics = Topics.Where(t => t.Keys.Any(k => f.Contains(k))).ToList();
        if (Regex.IsMatch(f, @"\bquy uoc\b")) topics = Topics.ToList();
        if (topics.Count > 0)
            return Reply(topics.Count == Topics.Length ? "Các quy ước Fast hay gặp khi viết / chạy store:" : "Quy ước Fast liên quan:",
                topics.Count == Topics.Length ? Topics.Select(t => new AskCard(t.Title, t.Text, "", Array.Empty<AskAction>(), "Quy ước Fast")) : topics.SelectMany(t => TopicCards(t, f)).Take(8),
                topics.Count == Topics.Length ? null : new[] { new AskChoice("Tạo báo cáo theo quy ước này", "tạo báo cáo") });

        if (Regex.IsMatch(f, @"\b(store|procedure|thu tuc|function|ham)\b")) return await StoreAsync(text, f, useSys);
        if (Regex.IsMatch(f, @"\b(tra truong|truong|field)\b") || f.StartsWith("ma ")) return FieldReply(f, text);

        return await SearchAllAsync(text, f, useSys);
    }

    // ---------------------------------------------------------------- điểm vào + xác nhận ✔ / ✘

    public async Task<AskReply> AskAsync(string text, Dictionary<string, string> state, bool useSys, CancellationToken ct = default)
    {
        state ??= new();
        // "bỏ qua / quit / hủy / dừng…" → dừng luồng đang hỏi dở (tạo báo cáo, danh mục, Save as, sửa sau ✘)
        if (IsStop(text))
        {
            var flow = state.GetValueOrDefault("flow");
            return flow switch
            {
                "fix" => Reply("OK, bỏ qua phần sửa — chỉ ghi nhận ✘.", choices: StartChoices),
                null => Reply("Không có cuộc hỏi nào đang dở. Bạn cần gì?", choices: StartChoices),
                _ => Reply($"Đã dừng {FlowName(flow)}. Bạn cần gì tiếp?", choices: StartChoices),
            };
        }
        // vừa bấm ✘ → câu tiếp theo là nội dung sửa
        if (state.GetValueOrDefault("flow") == "fix" && !string.IsNullOrWhiteSpace(text)) return SaveCorrection(text.Trim(), state);
        var r = await AskCoreAsync(text, state, useSys, ct);
        return ApplyFeedback(r, r.Sig.Length > 0 ? r.Sig : SigOf(text));
    }

    /// <summary>Câu chỉ để dừng cuộc hỏi đang dở (không dấu, cả câu): bỏ qua, quit, thoát, hủy, dừng, thôi, cancel, stop, exit…</summary>
    public static bool IsStop(string? text) =>
        Regex.IsMatch(Fold(text ?? "").Trim().TrimEnd('.', '!'), @"^(bo qua|quit|exit|thoat|huy|huy bo|dung|dung lai|thoi|thoi khoi|cancel|stop|skip|close|ket thuc|x)$");

    private static string FlowName(string flow) => flow switch
    {
        "report" => "tạo báo cáo",
        "catalog" => "tạo danh mục",
        "saveas" => "Save as",
        _ => "cuộc hỏi này",
    };

    /// <summary>Chữ ký câu hỏi tự do: các từ khoá không dấu đã sắp xếp (câu hỏi giống nhau → cùng chữ ký).</summary>
    public static string SigOf(string text) => "q:" + string.Join(" ", Words(Fold(text ?? "")).OrderBy(w => w, StringComparer.Ordinal));

    /// <summary>Xếp lại thẻ theo xác nhận trước đây: thẻ bị ✘ cho đúng câu hỏi này thì bỏ, thẻ được ✔ lên đầu; câu trả lời người dùng đã sửa hiện đầu tiên.</summary>
    private static AskReply ApplyFeedback(AskReply r, string sig)
    {
        if (r.Cards.Count == 0) return r with { Sig = sig };
        var fb = FastAskFeedback.Instance;
        var scored = r.Cards.Select((c, i) =>
        {
            var card = c.Ref.Length > 0 ? c : c with { Ref = "card|" + c.Source + "|" + c.Title };
            var (here, all) = fb.Net(sig, card.Ref);
            if (here > 0) card = card with { Text = "✔ Bạn đã xác nhận đúng cho câu hỏi này.\n" + card.Text };
            else if (all > 0) card = card with { Text = $"✔ Đã được xác nhận đúng {all} lần.\n" + card.Text };
            return (Card: card, I: i, Here: here, All: all);
        }).Where(x => x.Here >= 0).OrderByDescending(x => x.Here > 0).ThenByDescending(x => Math.Max(0, x.All)).ThenBy(x => x.I).Select(x => x.Card).ToList();
        if (fb.Corrections(sig).FirstOrDefault() is { } fix)
            scored.Insert(0, new AskCard("Câu trả lời bạn đã sửa", $"Bạn sửa lại cho câu hỏi này lúc {fix.At:dd/MM/yyyy HH:mm}.", fix.Correction!, CodeActions(fix.Correction!), "Bạn đã xác nhận")
                { Ref = "fix|" + sig });
        return r with { Cards = scored, Sig = sig };
    }

    /// <summary>Người dùng bấm ✔ / ✘ trên 1 thẻ. ✔: ghi nhận (ô ma trận được cộng điểm). ✘: ghi nhận rồi hỏi cách đúng — câu gõ tiếp theo được lưu làm sửa.</summary>
    public AskReply Feedback(int vote, string reference, string sig, string title)
    {
        FastAskFeedback.Instance.Add(new(sig, reference, Math.Sign(vote), null, DateTime.Now));
        var isMatrix = reference.StartsWith("m|");
        if (vote > 0)
            return Reply("✔ Đã ghi nhận — " + (isMatrix ? "ma trận ghost text cộng điểm cho cách viết này (gợi ý khi gõ cũng dùng)." : "lần sau hỏi tương tự sẽ ưu tiên kết quả này."));
        var key = isMatrix ? reference.Split('|')[2] : title;
        var ask = isMatrix ? $"Cách viết đúng (phần tham số) của {key} là gì? Gõ vào ô bên dưới — ghost text và Hỏi Fast sẽ dùng cách này."
            : reference.StartsWith("answer|") ? "Chỗ nào chưa đúng? Dán bản đã sửa vào ô bên dưới — lần sau hỏi giống vậy sẽ trả lại đúng bản bạn sửa."
            : "Thẻ này sẽ không hiện lại cho câu hỏi tương tự. Câu trả lời đúng là gì? Gõ để lưu lại (hoặc Bỏ qua).";
        return Reply("✘ Đã ghi nhận. " + ask, choices: new[] { new AskChoice("Bỏ qua", "bỏ qua") },
            state: new() { ["flow"] = "fix", ["ref"] = reference, ["sig"] = sig, ["title"] = title });
    }

    private static AskReply SaveCorrection(string text, Dictionary<string, string> state)
    {
        if (Regex.IsMatch(Fold(text), @"^(bo qua|skip|thoi|khong)$")) return Reply("OK, chỉ ghi nhận ✘.", choices: StartChoices);
        var reference = state.GetValueOrDefault("ref") ?? "";
        var sig = state.GetValueOrDefault("sig") ?? "";
        var fix = text;
        if (reference.StartsWith("m|"))
        {
            var p = reference.Split('|', 4);
            // người dùng gõ cả tên hàm / EXEC → chỉ lấy phần tham số; hàm thì kết thúc bằng ")"
            fix = Regex.Replace(fix, @"^\s*(?:EXEC(?:UTE)?\s+)?(?:dbo\.)?" + Regex.Escape(p[2]) + @"\s*\(?", "", RegexOptions.IgnoreCase).Trim();
            if (p[1] == "func" && !fix.EndsWith(")")) fix += ")";
        }
        FastAskFeedback.Instance.Add(new(sig, reference, 0, fix, DateTime.Now));
        return Reply(reference.StartsWith("m|")
            ? $"Đã lưu cách viết: {fix}\nMa trận chung đã cập nhật — ghost text và Hỏi Fast ưu tiên cách này (dự án nào cũng dùng)."
            : "Đã lưu câu trả lời bạn sửa — lần sau hỏi giống vậy sẽ hiện bản này đầu tiên.", choices: StartChoices);
    }

    /// <summary>"Học dự án này": đọc store của database (chỉ đọc) → kiến thức Hỏi Fast của dự án + ma trận ghost text của dự án (có tự đo độ chính xác),
    /// rồi gộp vào kiến thức chung trên máy để các dự án sau dùng lại.</summary>
    public async Task<string> LearnProjectAsync(bool useSys, Action<string> progress, CancellationToken ct = default)
    {
        if (_connections.Current is null) throw new InvalidOperationException("Chưa chọn workspace / database.");
        var key = GhostKey(useSys);
        progress($"Đang đọc store / function của {(useSys ? "Sys" : "App")} Data (chỉ đọc)…");
        List<(string Name, string Def)> mods;
        await using (var conn = _connections.CreateConnection(useSys))
        {
            await conn.OpenAsync(ct);
            mods = await FastGhostMiner.ReadModulesAsync(conn, ct);
        }
        progress($"Đã đọc {mods.Count} store. Đang rút kiến thức Hỏi Fast (JOIN, cột #report, cột ngày bảng phân kỳ, tham số báo cáo)…");
        var k = await Task.Run(() => FastAskKnowledge.Mine(mods, key), ct);
        k.Save(FastAskKnowledge.ProjectPath(key));
        var shared = FastAskKnowledge.MergeShared();
        progress("Đang học ma trận ghost text của dự án và tự đo độ chính xác (1–3 phút)…");
        var res = await Task.Run(() => FastGhostMiner.LearnFromModules(key, mods, ct: ct), ct);
        return $"Đã học dự án {key}: {k.Joins.Count} cặp JOIN, cột #report của {k.ReportCols.Count} bảng phân kỳ, cột ngày của {k.DateCols.Count} bảng, {k.Params.Count} tham số báo cáo.\n" +
               $"Kiến thức chung trên máy: {shared.Projects.Count} lần học dự án ({string.Join("; ", shared.Projects.TakeLast(5))}).\n\n" + res.Summary();
    }

    // ---------------------------------------------------------------- 1 + 2 + 7: báo cáo

    private static readonly (string Label, string Var)[] ReportFilters =
    {
        ("Kho", "Site"), ("Khách hàng", "Customer"), ("Vật tư", "Item"), ("Tài khoản", "Account"), ("Vụ việc", "Job"), ("Bộ phận", "Department"),
        ("Hợp đồng", "Contract"), ("Nhóm khách 1", "CustomerGroup1"), ("Nhóm vật tư 1", "ItemGroup1"), ("Mã giao dịch", "TransactionCode"),
        ("Số chứng từ từ / đến", "InvoiceFrom,InvoiceTo"), ("Diễn giải", "Description"),
    };

    private async Task<AskReply> ReportFlowAsync(string answer, Dictionary<string, string> state, bool useSys)
    {
        if (answer.StartsWith("type:")) state["type"] = answer[5..];
        else if (answer.StartsWith("table:")) state["table"] = TableList(answer[6..]);
        else if (answer.StartsWith("filters:")) state["filters"] = answer[8..];
        else if (answer.StartsWith("extra:")) state["extra"] = answer[6..];
        else if (answer.Length > 0 && state.GetValueOrDefault("step") == "table") state["table"] = TableList(answer);
        // nhiều bảng phân kỳ: "r00|r70", "r00, r70" → "r00|r70" (bỏ $000000 / $%Partition nếu gõ kèm)
        static string TableList(string s) => string.Join("|", s.Split(new[] { '|', ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Replace("$000000", "").Replace("$%Partition", "").Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase));

        if (!state.ContainsKey("type"))
        {
            state["step"] = "type";
            var topic = state.GetValueOrDefault("topic") ?? "";
            var similar = await SimilarReportsAsync(topic, Array.Empty<string>(), useSys);
            return Reply("Báo cáo loại nào? (khung dựng theo mẫu code Fast + quy ước #report / @Key / Partition$Execute)",
                similar.Count > 0 ? similar.Prepend(new AskCard("Store báo cáo có sẵn giống yêu cầu", "Mở để xem / chép làm khung — hoặc chọn loại bên dưới để dựng khung mới.", "", Array.Empty<AskAction>(), "Database đang kết nối")) : null,
                new[]
                {
                    new AskChoice("Bảng kê / sổ chi tiết (theo chứng từ)", "type:detail"), new AskChoice("Tổng hợp theo nhóm (group by)", "type:sum"), new AskChoice("Báo cáo PIVOT", "type:pivot"),
                    new AskChoice("Save as từ báo cáo có sẵn", "type:saveas"),
                },
                state);
        }
        if (!state.ContainsKey("table"))
        {
            state["step"] = "table";
            var choices = (await PartitionTablesAsync(useSys)).Take(12).Select(p => new AskChoice($"{p.Table}  ({p.Uses} store dùng)", p.Table)).ToList();
            if (choices.Count == 0) choices.Add(new AskChoice("r00", "r00"));
            return Reply("Lấy dữ liệu từ bảng phân kỳ nào? Chọn 1 hoặc nhiều bảng rồi bấm Xong (đếm theo số store trong database đang dùng bảng đó; gõ tên khác nếu cần, nhiều bảng cách nhau dấu phẩy)",
                choices: choices, state: state, multi: true, multiPrefix: "table:");
        }
        if (!state.ContainsKey("filters"))
        {
            state["step"] = "filters";
            // thêm tham số hay có của store báo cáo trong các dự án đã học (có dòng lọc theo ma trận / luật thì mới đưa)
            var kbf = FastGhostRules.KnowledgeFor(GhostKey(useSys));
            var known = ReportFilters.SelectMany(x => x.Var.Split(',')).Concat(new[] { "DateFrom", "DateTo", "Unit", "Language", "UserID", "Admin", "Controller", "TableName", "SysDatabase", "Query" })
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var learnedParams = FastAskKnowledge.Shared.TopParams(40)
                .Where(p => !known.Contains(p.Name) && (kbf.Find("filter", p.Name, "") is not null || FastGhostRules.FilterLine(p.Name, "a.") is not null)).Take(8)
                .Select(p => new AskChoice($"@{p.Name} ({p.N} store)", p.Name));
            return Reply("Tham số lọc nào? (lọc đơn vị @Unit luôn có)", choices: ReportFilters.Select(x => new AskChoice(x.Label, x.Var)).Concat(learnedParams), state: state, multi: true, multiPrefix: "filters:");
        }
        if (!state.ContainsKey("extra"))
        {
            state["step"] = "extra";
            return Reply("Thêm phần nào?", choices: new[] { new AskChoice("Trường động (GetDynamicFields)", "dynf"), new AskChoice("Dòng tổng cộng", "total"), new AskChoice("Tên song ngữ (@Language)", "bil") },
                state: state, multi: true, multiPrefix: "extra:");
        }

        // đủ thông tin → dựng khung
        var vars = state["filters"].Split(new[] { '|', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var extra = state["extra"].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        var (code, notes) = await BuildReportAsync(state["type"], state["table"], vars, extra, useSys);
        // chữ ký = đúng các lựa chọn → ✔ / ✘ / bản sửa gắn với tổ hợp này
        var sig = $"report:{state["type"]}:{state["table"]}:{string.Join(",", vars.OrderBy(v => v))}:{string.Join(",", extra.OrderBy(v => v))}".ToLowerInvariant();
        var cards = new List<AskCard> { new("Khung store báo cáo", notes, code, CodeActions(code), "Mẫu code Fast + ma trận") { Ref = "answer|" + sig } };
        if (extra.Contains("bil") && Snip("bil") is { } bil) cards.Add(SnippetCard(bil));
        cards.AddRange(await SimilarReportsAsync(state.GetValueOrDefault("topic") ?? "", vars, useSys));
        cards.AddRange(CallCards(new[] { "fastbusiness$function$system$getunitfilter", "fastbusiness$function$system$getcheckkey", "fastbusiness$partition$execute" }, useSys));
        return Reply("Đây là khung store báo cáo (sửa tên, cột kết quả rồi chạy thử). Bên dưới là store thật giống nhất trong database và cách gọi hàm hay dùng.", cards,
            new[] { new AskChoice("Tạo báo cáo khác", "tạo báo cáo"), new AskChoice("Xem quy ước báo cáo", "quy ước #report") }) with { Sig = sig };
    }

    /// <param name="tableSpec">1 hoặc nhiều bảng phân kỳ "r00|r70": cột kết quả = cột có ở mọi bảng, mỗi bảng 1 lần Partition$Execute vào #report.</param>
    private async Task<(string Code, string Notes)> BuildReportAsync(string type, string tableSpec, List<string> vars, HashSet<string> extra, bool useSys)
    {
        var kb = FastGhostRules.KnowledgeFor(GhostKey(useSys));
        var tables = tableSpec.Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tables.Count == 0) tables.Add("r00");
        var table = tables[0];
        var (keys, amounts) = await PickColumnsAsync(tables, type, useSys);
        var cols = string.Join(", ", keys.Concat(amounts));
        var body = Plain(Snip(type == "pivot" ? "rspivot" : "rsrepq")!.Body.Replace("$0", "¶FILTERS¶"),
            type == "pivot" ? new Dictionary<int, string> { [2] = table } : new Dictionary<int, string> { [2] = cols, [3] = table });
        if (type == "detail") body = Regex.Replace(body, @" group by [^']*'", "'");
        else if (type == "sum")
        {
            // tổng hợp: cột nhóm giữ nguyên, cột tiền / số lượng thì SUM, group by chỉ theo cột nhóm
            var sel = string.Join(", ", keys.Concat(amounts.Select(a => $"sum({a})")));
            body = body.Replace($"select 5, 1, 1, {cols} from", $"select 5, 1, 1, {sel} from").Replace($"group by {cols}'", $"group by {string.Join(", ", keys)}'");
        }

        // tham số lọc + kiểu (ma trận kiểu biến, mặc định VARCHAR(32))
        var param = new StringBuilder();
        var lines = new StringBuilder();
        var needSize = false;
        foreach (var v in vars)
        {
            var t = kb.Find("decl", v, "") is { } d && d.Pattern.Options.Count > 0 ? d.Pattern.Options[0].Text : "VARCHAR(32)";
            param.Append($"\t@{v} {t},\n");
            // dòng lọc: ma trận (dạng chuẩn, alias a.) → luật FilterLine
            var filter = kb.Find("filter", v, "") is { } fp && fp.Pattern.Options.Count > 0 ? fp.Pattern.Options[0].Text.Replace("{a}", "a.") : FastGhostRules.FilterLine(v, "a.");
            if (filter is null) continue;
            if (filter.Contains("@Size")) needSize = true;
            lines.Append($"\tIF @{v} <> '' {filter}\n");
        }
        if (needSize) lines.Insert(0, "\tDECLARE @Size INT\n\tSELECT @Size = dbo.ff_GetDataLength('so_ct')\n");
        // trường động dùng thêm tham số của màn hình báo cáo — khai luôn (kiểu theo ma trận kiểu biến)
        if (extra.Contains("dynf"))
            foreach (var (v, def) in new[] { ("TableName", "VARCHAR(128)"), ("Controller", "VARCHAR(128)"), ("SysDatabase", "VARCHAR(128)"), ("Query", "NVARCHAR(4000)") })
                param.Append($"\t@{v} {(kb.Find("decl", v, "") is { } dv && dv.Pattern.Options.Count > 0 ? dv.Pattern.Options[0].Text : def)},\n");
        body = body.Replace("\t@Language CHAR(1),\n", param + "\t@Language CHAR(1),\n");
        body = body.Replace("\t¶FILTERS¶\n", lines.ToString()).Replace("¶FILTERS¶", lines.ToString().TrimEnd());

        // nhiều bảng phân kỳ: lặp khối "SET @q … EXEC FastBusiness$Partition$Execute" cho từng bảng (cùng cột, cùng @Key) vào cùng #report / #tmp
        // mỗi bảng 1 khối "SET @q … EXEC Partition$Execute" (cột ngày của Execute theo kiến thức đã học cho bảng đó, mặc định a.ngay_ct)
        if (Regex.Match(body, @"\tSET @q = '[^\n]*\n(?:\tSET @q = @q[^\n]*\n)*\tEXEC FastBusiness\$Partition\$Execute[^\n]*\n") is { Success: true } block)
        {
            string Block(string t)
            {
                var b = block.Value.Replace(table + "$%Partition", t + "$%Partition");
                return FastAskKnowledge.Shared.DateColumnFor(t) is { } dc && dc != "ngay_ct" ? b.Replace("'a.ngay_ct'", $"'a.{dc}'") : b;
            }
            var copies = tables.Count == 1 ? Block(table) : string.Concat(tables.Select(t => $"\t-- Bảng {t}\n" + Block(t)));
            body = body.Remove(block.Index, block.Length).Insert(block.Index, copies);
        }

        var notes = new List<string>
        {
            tables.Count == 1 ? $"Bảng {table}$%Partition chạy bằng FastBusiness$Partition$Execute"
                : $"{tables.Count} bảng {string.Join(", ", tables.Select(t => t + "$%Partition"))}: mỗi bảng 1 lần FastBusiness$Partition$Execute vào cùng bảng kết quả, cột kết quả là cột có ở mọi bảng",
            vars.Count == 0 ? "chưa có tham số lọc thêm" : $"lọc {string.Join(", ", vars.Select(v => "@" + v))} (dòng lọc theo cách viết hay gặp nhất trong code Fast)",
            type == "detail" ? "dòng chi tiết sysorder = 5" : type == "sum" ? "cộng theo " + string.Join(", ", keys) : "khung PIVOT theo mẫu rs_rptExpensesAggregationReportPivot",
        };
        if (tables.Count > 1 && amounts.Count == 0)
        {
            var per = new List<string>();
            foreach (var t in tables) { var (_, a) = await PickColumnsAsync(new[] { t }, type, useSys); if (a.Count > 0) per.Add($"{t}: {string.Join(", ", a)}"); }
            if (per.Count > 0) notes.Add("các bảng không có chung cột tiền / số lượng (" + string.Join("; ", per) + ") — chưa đưa vào khung, thêm tay từng khối (CAST / đặt tên cột giống nhau)");
        }
        if (extra.Contains("dynf") && Snip("dynf") is { } dynf)
        {
            body = Regex.Replace(body, @"(\n\tSET @UnitKey = [^\n]*\n)", m => m.Value + "\n\t-- Trường động\n\t" + Plain(dynf.Body).Replace("\n", "\n\t") + "\n");
            notes.Add("trường động: đã khai tham số @TableName, @Controller, @SysDatabase, @Query (màn hình báo cáo truyền vào) — còn ghép @baseFields / @queryJoin vào câu @q");
        }
        if (extra.Contains("total") && type != "pivot" && Snip("total") is { } total)
        {
            if (amounts.Count == 0) notes.Add("bảng không có cột tiền / số lượng quen thuộc → chưa thêm dòng tổng cộng");
            else
            {
                var label = keys.Contains("dien_giai") ? "dien_giai" : keys.FirstOrDefault(k => !k.StartsWith("ngay")) ?? keys[0];
                var tot = Plain(total.Body, new Dictionary<int, string> { [1] = label, [2] = string.Join(", ", amounts), [3] = string.Join(", ", amounts.Select(a => $"SUM({a})")) });
                body = body.Replace("\tSELECT * FROM #report ORDER BY sysorder", "\t-- Dòng tổng cộng\n\t" + tot.Replace("\n", "\n\t") + "\n\n\tSELECT * FROM #report ORDER BY sysorder");
            }
        }
        return (body, string.Join("; ", notes) + ".");
    }

    /// <summary>Cột kết quả: cột mã / ngày / diễn giải (tối đa 6) + cột tiền / số lượng (tối đa 3) có thật trong bảng …$000000.</summary>
    private async Task<(List<string> Keys, List<string> Amounts)> PickColumnsAsync(IReadOnlyList<string> tables, string type, bool useSys)
    {
        var keyPrefer = type == "sum" ? new[] { "ma_kh", "ma_vt", "ma_kho", "tk", "ma_bp", "ma_vv" }
                                      : new[] { "stt_rec", "ngay_ct", "so_ct", "ma_ct", "ma_kh", "ma_vt", "ma_kho", "tk", "dien_giai" };
        try
        {
            var desc = await _tables.GetColumnDescriptionsAsync(useSys, "dbo", tables[0] + "$000000");
            var have = desc.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // nhiều bảng: chỉ giữ cột có ở mọi bảng
            foreach (var t in tables.Skip(1))
            {
                var other = (await _tables.GetColumnDescriptionsAsync(useSys, "dbo", t + "$000000")).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                have.IntersectWith(other);
            }
            desc = desc.Where(c => have.Contains(c.Name)).ToList();
            var keys = keyPrefer.Where(have.Contains).Take(type == "sum" ? 2 : 6).ToList();
            // bảng kê: cột #report hay có của bảng này trong store các dự án đã học (đúng thứ tự hay dùng), chỉ lấy cột không phải số
            var numeric = desc.Where(c => Regex.IsMatch(c.Type, "numeric|decimal|money|float|int", RegexOptions.IgnoreCase)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var learnedCols = FastAskKnowledge.Shared.ReportColumnsFor(tables[0]).Where(x => have.Contains(x.Col) && x.N >= 2).ToList();
            if (type == "detail" && learnedCols.Count(x => !numeric.Contains(x.Col)) >= 3)
                keys = learnedCols.Where(x => !numeric.Contains(x.Col)).Select(x => x.Col).Take(8).ToList();
            // cột số tiền / số lượng: kiểu số thật trong bảng, tên kiểu so_luong / sl_ / tien / ps_ / t_tien… (ưu tiên theo thứ tự đó)
            var rank = new[] { "so_luong", "sl_", "tien", "ps_", "t_tien", "t_tt", "gia" };
            var amounts = desc.Where(c => Regex.IsMatch(c.Type, "numeric|decimal|money|float", RegexOptions.IgnoreCase))
                .Select(c => (c.Name, R: Array.FindIndex(rank, p => c.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
                // bỏ cột ngoại tệ (_nt) và trường tự do (_td1/2/3)
                .Where(x => x.R >= 0 && !x.Name.EndsWith("_nt", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(x.Name, @"_td\d*$"))
                .OrderBy(x => x.R).Select(x => x.Name).Take(3).ToList();
            if (keys.Count > 0) return (keys, amounts);
        }
        catch { /* bảng không có → cột mặc định */ }
        return (new() { "stt_rec", "ngay_ct", "so_ct", "dien_giai" }, new());
    }

    private async Task<List<AskCard>> SimilarReportsAsync(string topic, IReadOnlyList<string> vars, bool useSys)
    {
        var stems = Stems(Fold(topic));
        if (stems.Count == 0 && vars.Count == 0) return new();
        var mods = await SearchModulesAsync(useSys, stems, vars.Select(v => "@" + v).Take(3).ToList(), "rs", 5);
        return mods.Select(m => ModuleCard(m.Name, m.Type, useSys, m.Score > 0 ? "tên khớp " + string.Join(", ", stems.Where(s => m.Name.Contains(s, StringComparison.OrdinalIgnoreCase))) : "dùng cùng tham số lọc")).ToList();
    }

    // ---------------------------------------------------------------- Save as từ store có sẵn

    /// <summary>"Save as": chọn store / function có sẵn → tên mới → định nghĩa đọc từ database (OBJECT_DEFINITION, chỉ đọc) với dòng CREATE / ALTER đổi sang
    /// CREATE tên mới. Không ghi gì vào database — người dùng tự chạy script ở SQL Query.</summary>
    private async Task<AskReply> SaveAsFlowAsync(string answer, Dictionary<string, string> state, bool useSys)
    {
        var step = state.GetValueOrDefault("step");
        answer = answer.Trim().Trim('[', ']');
        if (answer.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase)) answer = answer[4..].Trim('[', ']');
        if (step == "source" && answer.Length > 0) state["source"] = answer;
        else if (step == "newname" && answer.Length > 0) state["newname"] = answer;

        if (!state.ContainsKey("source"))
        {
            state["step"] = "source";
            var similar = await SimilarReportsAsync(state.GetValueOrDefault("topic") ?? "", Array.Empty<string>(), useSys);
            return Reply("Save as từ store báo cáo nào? Chọn bên dưới hoặc gõ tên store (vd rs_rptStockCard).",
                choices: similar.Select(c => new AskChoice(c.Title, c.Title)), state: state);
        }
        var db = await DbAsync(useSys);
        var mod = db.Modules.FirstOrDefault(m => m.Name.Equals(state["source"], StringComparison.OrdinalIgnoreCase));
        if (mod.Name is null)
        {
            var missing = state["source"];
            state.Remove("source");
            state["step"] = "source";
            return Reply($"Không thấy store / function \"{missing}\" trong {(useSys ? "Sys" : "App")} Data. Gõ lại tên (hoặc đổi App / Sys ở trên):", state: state);
        }
        var src = state["source"] = mod.Name;
        if (!state.ContainsKey("newname") || state["newname"].Equals(src, StringComparison.OrdinalIgnoreCase))
        {
            state.Remove("newname");
            state["step"] = "newname";
            var sugg = new[] { src.StartsWith("z", StringComparison.OrdinalIgnoreCase) ? src + "_Copy" : "z" + src, src + "_Copy" }.Distinct();
            return Reply($"Tên mới cho bản sao của {src}? Gõ tên, hoặc chọn gợi ý (tuỳ biến của dự án thường đặt tiền tố z).",
                choices: sugg.Select(s => new AskChoice(s, s)), state: state);
        }
        var newName = state["newname"];
        string? def = null;
        try
        {
            await using var conn = _connections.CreateConnection(useSys);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(@n))", conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@n", "dbo." + src);
            def = await cmd.ExecuteScalarAsync() as string;
        }
        catch (Exception ex) { return Reply($"Không đọc được định nghĩa {src}: {ex.Message}"); }
        if (string.IsNullOrEmpty(def)) return Reply($"Không đọc được định nghĩa {src} (store mã hoá WITH ENCRYPTION hoặc không có quyền xem).");

        var header = new Regex(@"\b(?:CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(?<k>PROC(?:EDURE)?|FUNCTION)\s+(?:\[?dbo\]?\s*\.\s*)?\[?" + Regex.Escape(src) + @"\]?", RegexOptions.IgnoreCase);
        var renamed = false;
        var code = header.Replace(def.Replace("\r\n", "\n"), m => { renamed = true; return $"CREATE {(m.Groups["k"].Value.StartsWith("F", StringComparison.OrdinalIgnoreCase) ? "FUNCTION" : "PROCEDURE")} [dbo].[{newName}]"; }, 1);
        var notes = new List<string>
        {
            $"Định nghĩa {src} đọc từ database đang kết nối (chỉ đọc), dòng CREATE đổi sang [dbo].[{newName}]. Chạy script ở SQL Query để tạo store mới — store gốc không đổi.",
            "Báo cáo mới cần trỏ khai báo báo cáo / controller sang store mới.",
        };
        if (!renamed) notes.Add($"⚠ Không tìm thấy dòng CREATE / ALTER {src} để đổi tên — sửa tay trước khi chạy.");
        if (db.Modules.Any(m => m.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
            notes.Add($"⚠ Database đã có {newName} — chạy sẽ báo lỗi trùng; đổi tên khác hoặc sửa CREATE thành ALTER nếu muốn ghi đè.");
        return Reply($"Bản sao của {src} với tên {newName}:",
            new[] { new AskCard($"Save as {src} → {newName}", string.Join("\n", notes), code, CodeActions(code), "Database đang kết nối") },
            new[] { new AskChoice("Save as store khác", "save as") });
    }

    // ---------------------------------------------------------------- 2: cách gọi hay dùng (ma trận + SqlHintCatalog)

    private string GhostKey(bool useSys) => (_connections.Current?.Name ?? "") + (useSys ? "|sys" : "|app");

    private IEnumerable<AskCard> CallCards(IEnumerable<string> routines, bool useSys)
    {
        var kb = FastGhostRules.KnowledgeFor(GhostKey(useSys));
        foreach (var r in routines)
        {
            var bare = SqlHintCatalog.Idioms.FirstOrDefault(i => i.RoutineName == r.ToLowerInvariant());
            var found = kb.Find("func", r, "") ?? kb.Find("call", r, "");
            if (found is null && bare is null) continue;
            var lines = new List<string>();
            if (found is { } k)
                lines.AddRange(k.Pattern.Options.Take(3).Select(o => $"{Readable(o.Text)}   — {o.N}/{k.Pattern.Count} lần"));
            var kindFound = kb.Find("func", r, "") is not null ? "func" : "call";
            // ô ma trận có cách viết đứng đầu → code theo ma trận (đã gồm xác nhận / sửa của người dùng); không thì mẫu cách gọi của SqlHintCatalog
            var code = found is { Pattern.Options.Count: > 0 } top && (bare is null || FastAskFeedback.Instance.Votes.Any(v => v.Ref.StartsWith($"m|{kindFound}|{r}|")))
                ? (kindFound == "func" ? "dbo." + DisplayName(r) + "(" : DisplayName(r) + " ") + Readable(top.Pattern.Options[0].Text)
                : bare is not null ? Plain(bare.Insert) : "";
            yield return new AskCard(DisplayName(r), (bare?.Note ?? "") + (lines.Count > 0 ? "\nCách viết tham số hay gặp (ma trận ghost text):\n" + string.Join("\n", lines) : ""),
                code, code.Length > 0 ? CodeActions(code) : Array.Empty<AskAction>(), "Ma trận + mẫu cách gọi")
                { Ref = found is { Pattern.Options.Count: > 0 } f ? $"m|{kindFound}|{r}|{f.Pattern.Options[0].Text}" : "" };
        }
    }

    /// <summary>Chỗ trống của ma trận → chữ đọc được: {lhs} = biến đang được gán trên dòng, {if} = biến của IF cùng dòng, {a} = alias bảng.</summary>
    private static string Readable(string pattern) => pattern.Replace("{lhs}", "@<biến đang gán>").Replace("{if}", "@<biến của IF>").Replace("{a}", "a.");

    private static string DisplayName(string lower)
    {
        // tên đẹp từ danh sách hay dùng (giữ hoa thường gốc) nếu có
        var hit = SqlHintCatalog.Idioms.Select(i => Plain(i.Insert)).Select(s => Regex.Match(s, @"(?:dbo\.)?([\w$]+)").Groups[1].Value)
            .FirstOrDefault(n => n.Equals(lower, StringComparison.OrdinalIgnoreCase));
        return hit ?? lower;
    }

    private async Task<AskReply> RoutineAsync(string name, bool useSys)
    {
        var cards = CallCards(new[] { name.ToLowerInvariant() }, useSys).ToList();
        var db = await DbAsync(useSys);
        if (db.Modules.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Name: not null } mod && mod.Name.Length > 0)
            cards.Add(ModuleCard(mod.Name, mod.Type, useSys, "định nghĩa"));
        var users = await SearchModulesAsync(useSys, Array.Empty<string>(), new[] { name }, null, 6);
        if (users.Count > 0)
            cards.Add(new AskCard($"Nơi đang gọi {name}", string.Join("\n", users.Select(u => u.Name)), "", users.Take(4).Select(u => new AskAction("open-object", "Mở " + u.Name, u.Name, (useSys ? "sys" : "app") + "|" + u.Type)).ToList(),
                "Database đang kết nối"));
        return cards.Count == 0
            ? Reply($"Chưa thấy \"{name}\" trong ma trận, mẫu code hay database đang kết nối.")
            : Reply($"Kiến thức về {name}:", cards);
    }

    // ---------------------------------------------------------------- 3: store / function

    private async Task<AskReply> StoreAsync(string text, string f, bool useSys)
    {
        var cards = new List<AskCard>();
        if (Regex.IsMatch(f, @"\b(function|ham)\b") && Snip("rsfn") is { } fn) cards.Add(SnippetCard(fn));
        else
        {
            const string skeleton = "CREATE PROCEDURE [dbo].[fs_TenStore]\n\t@UserID INT,\n\t@Admin BIT\nAS\nBEGIN\n\tSET NOCOUNT ON\n\tSET ANSI_NULLS OFF\n\n\t\n\n\tSET NOCOUNT OFF\n\tSET ANSI_NULLS ON\nEND";
            cards.Add(new AskCard("Khung store xử lý", "SET NOCOUNT / ANSI_NULLS đầu – cuối theo quy ước Fast. Báo cáo thì hỏi \"tạo báo cáo\" để có khung đầy đủ.", skeleton, CodeActions(skeleton), "Quy ước Fast"));
            foreach (var p in new[] { "dyn", "cur", "droptmp" }) if (Snip(p) is { } s) cards.Add(SnippetCard(s));
        }
        var stems = Stems(f);
        if (stems.Count > 0)
            cards.AddRange((await SearchModulesAsync(useSys, stems, Array.Empty<string>(), null, 6)).Select(m => ModuleCard(m.Name, m.Type, useSys, "tên khớp")));
        return Reply("Khung + store / function có sẵn liên quan:", cards, new[] { new AskChoice("Tạo báo cáo", "tạo báo cáo"), new AskChoice("Quy ước Fast", "quy ước fast") });
    }

    // ---------------------------------------------------------------- 4: cấu trúc bảng + JOIN

    private async Task<AskReply> TablesAsync(List<string> tables, bool useSys)
    {
        var cards = new List<AskCard>();
        var descs = new List<(string Table, List<(string Name, string Type, bool Nullable, bool IsKey, bool Identity, string Description)> Cols)>();
        foreach (var t in tables.Take(3))
        {
            try { descs.Add((t, await _tables.GetColumnDescriptionsAsync(useSys, "dbo", t))); }
            catch (Exception ex) { cards.Add(new AskCard(t, "Không đọc được cấu trúc: " + ex.Message, "", Array.Empty<AskAction>(), "Database đang kết nối")); }
        }
        foreach (var (t, cols) in descs)
        {
            var keys = cols.Where(c => c.IsKey).Select(c => c.Name).ToList();
            var info = string.Join("\n", cols.Take(60).Select(c => $"{c.Name,-20} {c.Type}{(c.IsKey ? "  🔑" : "")}{(c.Identity ? "  identity" : "")}{(c.Nullable ? "" : "  NOT NULL")}{(c.Description.Length > 0 ? "  — " + c.Description : "")}"));
            var sel = $"SELECT {string.Join(", ", cols.Take(12).Select(c => c.Name))}\nFROM {t}" + (keys.Count > 0 ? $"\nWHERE {keys[0]} = ''" : "");
            cards.Add(new AskCard($"{t} — {cols.Count} cột" + (keys.Count > 0 ? $", khoá {string.Join(", ", keys)}" : ""), info + (cols.Count > 60 ? $"\n… còn {cols.Count - 60} cột" : ""), sel, CodeActions(sel), "Database đang kết nối"));
        }
        if (descs.Count >= 2)
        {
            var (a, ac) = descs[0];
            var (b, bc) = descs[1];
            var aNames = ac.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // khoá của bảng sau có ở bảng trước → JOIN theo khoá; không thì cột ma_* chung
            var on = bc.Where(c => c.IsKey && aNames.Contains(c.Name)).Select(c => c.Name).ToList();
            if (on.Count == 0) on = bc.Where(c => c.Name.StartsWith("ma_", StringComparison.OrdinalIgnoreCase) && aNames.Contains(c.Name)).Select(c => c.Name).Take(1).ToList();
            // đã học từ store thật của các dự án: điều kiện ON hay dùng nhất giữa 2 bảng này
            if (FastAskKnowledge.Shared.JoinFor(a, b) is { } learned)
            {
                string Alias(string cond) => Regex.Replace(Regex.Replace(cond, Regex.Escape(a.ToLowerInvariant()) + @"\.", "a.", RegexOptions.IgnoreCase), Regex.Escape(b.ToLowerInvariant()) + @"\.", "b.", RegexOptions.IgnoreCase);
                var name = bc.FirstOrDefault(c => c.Name.StartsWith("ten_", StringComparison.OrdinalIgnoreCase) && !aNames.Contains(c.Name)).Name;
                var join = $"SELECT a.*{(name is null ? "" : ", b." + name)}\nFROM {a} a\nLEFT JOIN {b} b ON {Alias(learned.Cond)}";
                cards.Insert(0, new AskCard($"JOIN {a} – {b}", $"Theo {learned.N}/{learned.Total} lần nối 2 bảng này trong store của các dự án đã học.", join, CodeActions(join), "Kiến thức đã học")
                    { Ref = $"join|{a}|{b}".ToLowerInvariant() });
            }
            else if (on.Count > 0)
            {
                var name = bc.FirstOrDefault(c => c.Name.StartsWith("ten_", StringComparison.OrdinalIgnoreCase) && !aNames.Contains(c.Name)).Name;
                var join = $"SELECT a.*{(name is null ? "" : ", b." + name)}\nFROM {a} a\nLEFT JOIN {b} b ON {string.Join(" AND ", on.Select(k => $"a.{k} = b.{k}"))}";
                cards.Insert(0, new AskCard($"JOIN {a} – {b}", $"Nối theo {string.Join(", ", on)} ({(bc.Any(c => c.IsKey && on.Contains(c.Name)) ? "khoá của " + b : "cột mã chung")}).", join, CodeActions(join), "Database đang kết nối"));
            }
            else cards.Insert(0, new AskCard($"JOIN {a} – {b}", "Không thấy cột khoá / cột mã chung giữa 2 bảng.", "", Array.Empty<AskAction>(), "Database đang kết nối"));
        }
        return Reply(descs.Count >= 2 ? "Câu JOIN mẫu + cấu trúc 2 bảng:" : "Cấu trúc bảng:", cards);
    }

    // ---------------------------------------------------------------- 5: từ điển field

    private AskReply FieldReply(string f, string text)
    {
        var words = Words(Regex.Replace(f, @"\b(tra truong|truong|field|ma nao|cot nao)\b", " "));
        if (words.Count == 0) return Reply("Bạn gõ tiêu đề / tên trường cần tra, vd \"tra trường tài khoản đối ứng\" hoặc \"trường ma_kho\".");
        var dict = FieldDictionaryService.Instance;
        var hits = dict.Entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var hay = Fold(g.Key + " " + string.Join(" ", g.Select(e => e.HeaderV + " " + e.HeaderE)));
                var score = words.Count(w => hay.Contains(w)) + (words.Any(w => w.Equals(g.Key, StringComparison.OrdinalIgnoreCase)) ? 3 : 0);
                return (Name: g.Key, Score: score);
            })
            .Where(x => x.Score >= Math.Max(1, (int)Math.Ceiling(words.Count * 0.6))).OrderByDescending(x => x.Score).ThenBy(x => x.Name.Length).Take(8).ToList();
        if (hits.Count == 0) return Reply($"Không thấy trường nào khớp \"{text}\" trong từ điển field (header.xml).");
        var cards = hits.Select(h =>
        {
            var hd = dict.Header(h.Name);
            var lay = dict.Layout(h.Name);
            var lk = dict.Lookups(h.Name).FirstOrDefault();
            var desc = $"{hd?.V} / {hd?.E}" + (lay is { } l && l.Type.Length > 0 ? $"\nKiểu: {l.Type}{(l.Width is { } w ? $" ({w})" : "")}" : "") +
                       (lk is not null ? $"\nLookup: controller {lk.Controller}" + (lk.Source.Table.Length > 0 ? $" → {lk.Source.Table}.{lk.Source.KeyColumn}" : "") : "");
            return new AskCard(h.Name, desc, h.Name, new List<AskAction> { new("insert", "Chèn tên trường", h.Name), new("copy", "Copy", h.Name) }, "Từ điển field Fast (header.xml)");
        }).ToList();
        return Reply("Trường khớp trong từ điển field Fast:", cards);
    }

    // ---------------------------------------------------------------- 6: danh mục (luật Fast)

    private static AskReply CatalogFlow(string answer, Dictionary<string, string> state)
    {
        var step = state.GetValueOrDefault("step");
        if (step == "name" && answer.Length > 0) state["name"] = answer.Trim();
        else if (step == "cols" && answer.Length > 0) state["cols"] = answer.Trim();
        // "tạo danh mục kho hàng" → lấy luôn phần sau "danh mục" làm tên
        if (!state.ContainsKey("name") && Regex.Match(Fold(state.GetValueOrDefault("topic") ?? ""), @"danh muc\s+(.+)$") is { Success: true } nm && nm.Groups[1].Value.Trim().Length > 1)
            state["name"] = (state["topic"])[^nm.Groups[1].Value.Length..].Trim();
        if (!state.ContainsKey("name"))
        {
            state["step"] = "name";
            return Reply("Tên danh mục là gì? (vd \"Danh mục kho hàng\" hoặc tên bảng zdmkho)", state: state);
        }
        if (!state.ContainsKey("cols"))
        {
            state["step"] = "cols";
            return Reply($"Danh mục \"{state["name"]}\" gồm những cột nào? Gõ mỗi cột 1 dòng hoặc cách nhau dấu phẩy (vd: Mã kho, Tên kho, Tài khoản kho, Ghi chú). " +
                         "Luật Fast tự thêm cột chuẩn (ma_/ten_/ten_2, status, datetime0/2, user_id0/2) và đoán field theo từ điển header.xml.", state: state);
        }
        var name = state["name"];
        var cols = state["cols"];
        var content = name + "\n" + string.Join("\n", cols.Split(new[] { '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(c => "- " + c));
        var code = Regex.Replace(Fold(name), @"[^a-z0-9]", "");
        code = code.StartsWith("zdm") || code.StartsWith("dm") ? code : "zdm" + code.Replace("danhmuc", "");
        var cards = new List<AskCard>
        {
            new("Tạo nhanh danh mục theo luật Fast", "Mở Tạo nhanh danh mục, điền sẵn cột theo luật Fast (không AI) — kiểm tra rồi bấm Tạo file như thường lệ.\n\n" + content, "",
                new List<AskAction> { new("quicklist", "Mở Tạo nhanh danh mục (theo luật Fast)", name, content) }, "Luật danh mục Fast"),
        };
        if (Snip("tblcat") is { } tbl)
        {
            var sql = Plain(tbl.Body, new Dictionary<int, string> { [1] = code, [2] = Regex.Replace(code, "^z?dm", "") });
            cards.Add(new AskCard("Chỉ cần bảng (CREATE TABLE chuẩn)", tbl.Description, sql, CodeActions(sql), "Mẫu code Fast"));
        }
        return Reply("Danh mục theo luật Fast:", cards, new[] { new AskChoice("Tạo danh mục khác", "tạo danh mục") });
    }

    // ---------------------------------------------------------------- tìm chung

    private async Task<AskReply> SearchAllAsync(string text, string f, bool useSys)
    {
        var words = Words(f);
        // phải khớp đa số từ của câu hỏi (khớp 1 từ chung chung như "chi", "tiet" thì không tính)
        var need = Math.Max(1, (int)Math.Ceiling(words.Count * 0.5));
        int Score(string hay) { var h = Fold(hay); return words.Count(w => Regex.IsMatch(h, @"\b" + Regex.Escape(w) + @"\b")); }
        var cards = new List<AskCard>();
        // store trong database (câu hỏi kiểu tên báo cáo / nghiệp vụ thì đây là thứ cần nhất → lên đầu)
        var stems = Stems(f);
        if (stems.Count > 0)
            cards.AddRange((await SearchModulesAsync(useSys, stems, Array.Empty<string>(), null, 5))
                .Select(m => ModuleCard(m.Name, m.Type, useSys, "tên khớp " + string.Join(", ", stems.Where(s => m.Name.Contains(s, StringComparison.OrdinalIgnoreCase))))));
        // mẫu code
        foreach (var s in SqlHintCatalog.Snippets.Select(s => (s, score: Score(s.Prefix + " " + s.Name + " " + s.Description)))
                     .Where(x => x.score >= need).OrderByDescending(x => x.score).Take(4).Select(x => x.s))
            cards.Add(SnippetCard(s));
        // quy ước
        foreach (var t in Topics.Where(t => Score(t.Title + " " + t.Text) >= need).Take(2))
            cards.Add(new AskCard(t.Title, t.Text, "", Array.Empty<AskAction>(), "Quy ước Fast"));
        if (cards.Count == 0)
            return Reply("Mình chưa tìm thấy kiến thức khớp câu này. Thử hỏi theo các nhóm dưới, hoặc gõ tên bảng / tên store / tên hàm Fast.", choices: StartChoices);
        var reportish = Regex.IsMatch(f, @"\b(so chi tiet|so cai|bang ke|nhat ky|tong hop|so du|the kho|ton kho|cong no|doanh thu)\b");
        return Reply("Kết quả liên quan:", cards,
            reportish ? new[] { new AskChoice("Dựng khung báo cáo này", "tạo báo cáo " + text) }.Concat(StartChoices.Take(2)) : StartChoices.Take(3));
    }
}
