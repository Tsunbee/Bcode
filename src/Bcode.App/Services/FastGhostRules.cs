using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Ghost text theo cấu trúc Fast (không gọi AI, không tốn token): đang gõ trong editor SQL mà khớp 1 luật thì hiện ngay phần tiếp theo
/// đúng kiểu Fast — Tab để nhận. Chạy TRƯỚC gợi ý AI; không khớp luật nào thì AI (nếu bật) làm như cũ.
/// Nguồn kiến thức (theo thứ tự ưu tiên):
/// <list type="number">
/// <item>Luật cố định (song ngữ CASE, REPLACE(RTRIM(@x), …, DROP TABLE #t, cột danh mục chuẩn, SET ANSI_NULLS…).</item>
/// <item>Ma trận kiến thức (<see cref="KnowledgeBase"/>, rút + hiệu chỉnh bằng <see cref="FastGhostMiner"/>): EXEC routine / hàm Fast( / dòng lọc @Key
/// → vài cách viết hay gặp nhất theo (routine, ngữ cảnh); bỏ cách dùng biến store chưa khai báo; chưa đủ chắc thì chỉ gợi ý phần đầu chắc chắn;
/// ngưỡng từng loại chọn sao cho đo trên store chưa dùng để học đúng ≥ 90%. Ma trận học từ database đang kết nối (nếu có) tra trước ma trận đi kèm.</item>
/// <item>Kiểu cột hay dùng nhất theo tên cột trong database đang kết nối (<see cref="LoadColumnTypes"/>) — cho CREATE TABLE / ALTER ADD / DECLARE.</item>
/// <item>Chỉ khi ma trận trống (hoặc bật <see cref="GuessWhenUnknown"/>): đoán theo <see cref="SqlHintCatalog"/> / header.xml như trước.</item>
/// </list>
/// Chỉ gợi ý khi con trỏ ở cuối dòng (phía sau chỉ còn khoảng trắng) và không nằm trong chuỗi / comment.
/// </summary>
public static class FastGhostRules
{
    // ---- biến lọc → cột (cặp hay gặp nhất trong code FBO; khớp với các mẫu fcust / fitem … của SqlHintCatalog) ----
    private static readonly Dictionary<string, string> FilterFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Customer"] = "ma_kh", ["Item"] = "ma_vt", ["Site"] = "ma_kho", ["Department"] = "ma_bp", ["Job"] = "ma_vv",
        ["Contract"] = "ma_hd", ["Reason"] = "ma_nx", ["ItemType"] = "loai_vt", ["Product"] = "ma_sp", ["Salesman"] = "ma_nvbh",
        ["Currency"] = "ma_nt", ["FC"] = "ma_nt", ["MO"] = "so_lsx", ["Lot"] = "ma_lo", ["Fee"] = "ma_phi", ["BContract"] = "ma_ku",
        ["VC"] = "ma_ct", ["Employee"] = "ma_nv",
    };

    private static readonly Regex FilterIfRx = new(@"^\s*IF\s+@(?<v>\w+)\s*<>\s*''\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilterIfOpenRx = new(@"^\s*IF\s+@(?<v>\w+)\s+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ExecRx = new(@"\bEXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?(?<n>(?:\[?\w+\]?\.)?\[?[\w$]+\]?)\s+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FuncOpenRx = new(@"(?<n>(?:\[?\w+\]?\.)?[\w$]+)\($", RegexOptions.Compiled);
    // tên cột phải gõ xong (có dấu cách sau), ≥ 3 ký tự (ten_kh, b.ten_vt, cname) — tránh gợi ý khi mới gõ alias "b" / "b."
    private static readonly Regex BilRx = new(@"CASE\s+WHEN\s+@Language\s*=\s*'[vV]'\s+THEN\s+(?<c>(?:\w+\.)?\w{3,})\s+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CreateTableRx = new(@"^\s*CREATE\s+TABLE\s+(?:\[?dbo\]?\.)?\[?(?<t>[\w$#]+)\]?\s*\(\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ColumnDefRx = new(@"^\s*,?\s*\[?(?<c>[A-Za-z_]\w*)\]?\s+$", RegexOptions.Compiled);
    private static readonly Regex AlterAddRx = new(@"\bALTER\s+TABLE\s+[\w.\[\]$#]+\s+ADD\s+\[?(?<c>[A-Za-z_]\w*)\]?\s+$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "DECLARE @x " hoặc biến sau trong cùng dòng DECLARE: "DECLARE @a INT, @x " (có thể kèm AS)
    private static readonly Regex DeclareVarRx = new(@"^\s*DECLARE\s+(?:.*,\s*)?@(?<c>[A-Za-z_]\w*)\s+(?:AS\s+)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Cặp biến → kiểu trong DECLARE / danh sách tham số procedure, function (nhóm n = tên biến không @, t = kiểu).</summary>
    public static readonly Regex DeclTypeRx = new(@"(?:\bDECLARE\s+|,\s*|\bPROC(?:EDURE)?\s+[\w.$\[\]]+\s+\(?\s*|\bFUNCTION\s+[\w.$\[\]]+\s*\(\s*)@(?<n>\w+)\s+(?:AS\s+)?(?<t>(?:N?VARCHAR|N?CHAR|BIGINT|SMALLINT|TINYINT|INT|BIT|NUMERIC|DECIMAL|SMALLMONEY|MONEY|FLOAT|REAL|DATETIME2|DATETIMEOFFSET|SMALLDATETIME|DATETIME|DATE|TIME|N?TEXT|UNIQUEIDENTIFIER|VARBINARY|BINARY|XML|IMAGE|SQL_VARIANT)\b(?:\s*\(\s*(?:\d+|max)(?:\s*,\s*\d+)?\s*\))?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Kiểu viết chuẩn để đếm / gợi ý: bỏ khoảng trắng, chữ hoa — "nvarchar (4000)" → "NVARCHAR(4000)".</summary>
    public static string NormType(string t) => Regex.Replace(t, @"\s+", "").ToUpperInvariant();
    private static readonly Regex DropTempRx = new(@"IF\s+OBJECT_ID\s*\(\s*'tempdb\.\.(?<t>#[\w$]+)'\s*\)\s+IS\s+NOT\s+NULL\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ReplaceQuoteRx = new(@"REPLACE\s*\(\s*RTRIM\s*\(\s*@\w+\s*\)\s*,\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>1 gợi ý: chữ, loại (filter / call / func = tra ma trận kiến thức; doc = chép trong script; rule = luật cố định; guess = đoán theo mẫu)
    /// và độ tin của ô ma trận đã dùng (luật cố định = 1).</summary>
    public sealed record Ghost(string Text, string Kind, double Share);

    /// <summary>Gợi ý cho con trỏ ở <paramref name="caret"/> (vị trí ký tự trong <paramref name="text"/>); null = không có luật nào khớp.</summary>
    /// <param name="columnTypes">Tên cột → kiểu hay dùng nhất trong database (null = chưa nạp; dùng quy ước đặt tên thay thế).</param>
    /// <param name="knowledge">Ma trận kiến thức (null = ma trận đi kèm Bcode, xem <see cref="KnowledgeFor"/>).</param>
    public static string? Suggest(string text, int caret, IReadOnlyDictionary<string, string>? columnTypes = null, KnowledgeBase? knowledge = null)
        => Explain(text, caret, columnTypes, knowledge)?.Text;

    /// <summary>Theo chế độ trong Tùy chọn: dài (mặc định) = <see cref="SuggestLong"/>, không có thì gợi ý ngắn; ngắn = chỉ gợi ý chắc ≥ 90%.</summary>
    public static string? SuggestByMode(bool longMode, string text, int caret, IReadOnlyDictionary<string, string>? columnTypes = null, KnowledgeBase? knowledge = null)
        => longMode ? SuggestLong(text, caret, columnTypes, knowledge) ?? Suggest(text, caret, columnTypes, knowledge) : Suggest(text, caret, columnTypes, knowledge);

    /// <summary>Như <see cref="Suggest"/> nhưng cho biết gợi ý đến từ đâu. <paramref name="ignoreThresholds"/> = bỏ ngưỡng từng loại (để hiệu chỉnh ngưỡng).
    /// Thứ tự: luật cố định + ô ma trận theo routine / biến lọc → chép theo đoạn đã viết trong chính file ("same") → ma trận đầu dòng ("line").</summary>
    public static Ghost? Explain(string text, int caret, IReadOnlyDictionary<string, string>? columnTypes = null, KnowledgeBase? knowledge = null, bool ignoreThresholds = false)
    {
        var kb = knowledge ?? Knowledge;
        if (ExplainRules(text, caret, columnTypes, kb, ignoreThresholds) is { } g) return ReferenceEquals(g, Declined) ? null : g;
        // dòng trống → câu hay viết tiếp sau dòng code phía trên
        if (NextLine(text, caret, kb, ignoreThresholds ? 0 : kb.MinShareOf("next"), false) is { } nx) return nx;
        if (GenericSplit(text, caret) is not var (before, after)) return null;
        var m = SameCandidates(before, after);
        if (ignoreThresholds) return SameFile(m, 1) ?? LineStart(before, kb, 0);
        // dài trước: bậc nào đủ ngưỡng của bậc đó thì lấy
        foreach (var k in SameLadder)
            if ((k == 1 || kb.QualityOf(SameKind(k)) is not null) && SameFile(m, k) is { } s && s.Share >= kb.MinShareOf(SameKind(k))) return s;
        return LineStart(before, kb, kb.MinShareOf("line")) is { } l && l.Share >= kb.MinShareOf("line") ? l : null;
    }

    /// <summary>Tỷ lệ tối thiểu các chỗ khớp cùng viết tiếp giống nhau để "Gợi ý Fast dài" lấy bậc độ dài đó.</summary>
    public const double LongAgreement = 0.6;

    /// <summary>"Gợi ý Fast dài" (gọi tay, không qua ngưỡng 90% của ghost tự động): luật / ô ma trận riêng như thường; rồi "theo chính file" lấy
    /// phần viết tiếp DÀI NHẤT mà đa số (≥ 60%) chỗ khớp cùng viết — cả dòng nếu chỉ có 1 chỗ khớp (vd "select top 0 5 as sysorder" → cả phần còn lại của
    /// dòng SELECT phía trên), "set @q =" giữa các dòng "SET @q = @q + …" → "@q +"; cuối cùng ma trận đầu dòng cho cả gợi ý từng phần.</summary>
    public static string? SuggestLong(string text, int caret, IReadOnlyDictionary<string, string>? columnTypes = null, KnowledgeBase? knowledge = null)
    {
        var kb = knowledge ?? Knowledge;
        // gọi tay: luật / ô ma trận riêng lấy cách viết hay gặp nhất, không qua ngưỡng (vd DECLARE @Key → kiểu hay dùng nhất của @Key)
        if (ExplainRules(text, caret, columnTypes, kb, true) is { } g && !ReferenceEquals(g, Declined)) return g.Text;
        // dòng trống: câu tiếp theo theo ngưỡng riêng của chế độ dài (hiệu chỉnh để đúng ≥ 60%), cho gợi ý phần đầu chắc chắn
        if (NextLine(text, caret, kb, kb.MinShareOf("next:long"), true) is { } nx) return nx.Text;
        if (GenericSplit(text, caret) is not var (before, after)) return null;
        if (SameCandidates(before, after) is { } m)
            foreach (var k in new[] { 0, 8, 3, 2, 1 })
                if (SameFile(m, k) is { } s && s.Share / (1 - 1.0 / (m.LineTokens + 1)) >= LongAgreement - 1e-9) return s.Text;
        return LineStart(before, kb, 0.6, partial: true) is { } l && l.Share >= 0.5 ? l.Text : null;
    }

    /// <summary>Phần văn bản trước con trỏ và sau dòng đang gõ khi được gợi ý chung: con trỏ cuối dòng, dòng có chữ, không nằm trong chuỗi / comment;
    /// null = không gợi ý.</summary>
    public static (string Before, string After)? GenericSplit(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        caret -= Regex.Matches(text[..caret], "\r\n").Count;
        text = text.Replace("\r\n", "\n");
        var lineEnd = text.IndexOf('\n', caret);
        if (text[caret..(lineEnd < 0 ? text.Length : lineEnd)].Trim().Length > 0) return null;
        var line = text[(caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1)..caret];
        return line.Trim().Length == 0 || !CaretInCode(line) ? null : (text[..caret], lineEnd < 0 ? "" : text[(lineEnd + 1)..]);
    }

    /// <summary>Ô ma trận riêng (lọc / routine) có nhưng chưa đủ chắc → im lặng, không rơi xuống gợi ý chung.</summary>
    private static readonly Ghost Declined = new("", "declined", 0);

    private static Ghost? ExplainRules(string text, int caret, IReadOnlyDictionary<string, string>? columnTypes, KnowledgeBase kb, bool ignoreThresholds)
    {
        Ghost? Rule(string? s) => string.IsNullOrEmpty(s) ? null : new Ghost(s, "rule", 1);
        caret = Math.Clamp(caret, 0, text.Length);
        caret -= Regex.Matches(text[..caret], "\r\n").Count;   // vị trí theo văn bản đã đổi \r\n → \n
        text = text.Replace("\r\n", "\n");
        var lineStart = text.LastIndexOf('\n', Math.Max(0, caret - 1)) + 1;
        if (caret == 0) lineStart = 0;
        var lineEnd = text.IndexOf('\n', caret);
        if (lineEnd < 0) lineEnd = text.Length;
        if (text[caret..lineEnd].Trim().Length > 0) return null;   // giữa dòng: không gợi ý
        var line = text[lineStart..caret];
        var before = text[..caret];
        var others = text.Remove(lineStart, lineEnd - lineStart);   // phần còn lại của script (không gồm dòng đang gõ)
        var lead = line.Length > 0 && !char.IsWhiteSpace(line[^1]) ? " " : "";

        // Dòng trống ngay sau 1 dòng quen thuộc → dòng tiếp theo quen thuộc.
        if (line.Trim().Length == 0)
        {
            var prev = PreviousLine(text, lineStart);
            if (prev is null) return null;
            if (Regex.IsMatch(prev, @"^\s*SET\s+NOCOUNT\s+ON\s*;?\s*$", RegexOptions.IgnoreCase) && !Regex.IsMatch(text, @"\bANSI_NULLS\b", RegexOptions.IgnoreCase))
                return Rule("SET ANSI_NULLS OFF");
            if (Regex.IsMatch(prev, @"^\s*DECLARE\s+@UnitKey\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(others, @"@UnitKey\s*=", RegexOptions.IgnoreCase))
                return Rule("SET @UnitKey = dbo.FastBusiness$Function$System$GetUnitFilter('a.ma_dvcs', @Unit, @UserID, @Admin)");
            // Sắp tới END cuối của procedure mà đã SET NOCOUNT ON chưa trả lại → 2 dòng kết thúc chuẩn.
            if (NextNonEmptyLine(text, lineEnd) is { } next && Regex.IsMatch(next, @"^\s*END\s*;?\s*$", RegexOptions.IgnoreCase)
                && !Regex.IsMatch(text[lineEnd..], @"\bEND\b[\s\S]*\bEND\b", RegexOptions.IgnoreCase)
                && Regex.IsMatch(before, @"\bSET\s+NOCOUNT\s+ON\b", RegexOptions.IgnoreCase) && !Regex.IsMatch(text, @"\bSET\s+NOCOUNT\s+OFF\b", RegexOptions.IgnoreCase))
                return Rule("SET NOCOUNT OFF\n" + Indent(line) + "SET ANSI_NULLS ON");
            return null;
        }
        if (!CaretInCode(line)) return null;

        // ---- IF @Customer <> '' → điều kiện lọc @Key ----
        if (FilterIfRx.Match(line) is { Success: true } fm)
        {
            var v = fm.Groups["v"].Value;
            // (Đo trên code thật: chép dòng lọc cùng biến đã có phía trên chỉ đúng ~20% — mỗi nhánh lọc 1 bảng khác → không dùng.)
            // Ma trận kiến thức: cách viết chuẩn hoá hay gặp nhất cho biến này, đổi theo văn phong các dòng lọc phía trên của store.
            if (kb.Find("filter", v, "") is { } kf)
            {
                var style = FilterStyle.Of(before);
                return Choose(kf.Pattern, t => style.Apply(t), before, ignoreThresholds ? 0 : kf.MinShare) is { } c && (ignoreThresholds || c.Share >= kf.MinShare)
                    ? new Ghost(lead + c.Text, "filter", c.Share) : Declined;
            }
            // Gợi ý Fast dài: biến chưa gặp → ghép (dòng lọc gần nhất của file + cột của biến), xem ComposeFilter
            if (ignoreThresholds && ComposeFilter(v, text, before, kb) is { } cf) return new Ghost(lead + cf, "filter:compose", 0.5);
            // đã có kiến thức mà biến này chưa gặp → không đoán (kể cả gợi ý chung: đo trên KOG, dòng lọc đoán theo đầu dòng chỉ đúng ~89%)
            if (!GuessWhenUnknown && kb.Has("filter")) return Declined;
            return FilterLine(v, AliasOf(text)) is { } f ? new Ghost(lead + f, "guess", 0) : null;
        }

        // ---- Gợi ý Fast dài: "IF @Customer " (chưa gõ <> '') với tham số chuỗi → cả điều kiện lọc ----
        if (ignoreThresholds && FilterIfOpenRx.Match(line) is { Success: true } fo2 && IsFilterParam(fo2.Groups["v"].Value, before))
        {
            var v = fo2.Groups["v"].Value;
            string? r = null;
            if (kb.Find("filter", v, "") is { } kf && Choose(kf.Pattern, FilterStyle.Of(before).Apply, before, 0) is { } c) r = c.Text;
            r ??= ComposeFilter(v, text, before, kb);
            if (r is not null) return new Ghost("<> '' " + r, "filter:open", 0.5);
        }

        // ---- REPLACE(RTRIM(@X), → '''', '''''') ----
        if (ReplaceQuoteRx.IsMatch(line)) return Rule(lead + "'''', '''''')");

        // ---- IF OBJECT_ID('tempdb..#t') IS NOT NULL → DROP TABLE #t ----
        if (DropTempRx.Match(line) is { Success: true } dm) return Rule(lead + "DROP TABLE " + dm.Groups["t"].Value);

        // ---- CASE WHEN @Language = 'v' THEN ten_x → ELSE ten_x2 END AS ten_x ----
        if (BilRx.Match(line) is { Success: true } bm)
        {
            var c = bm.Groups["c"].Value;
            // Đo trên code thật: sau END lúc thì "AS x", lúc thì dấu phẩy / FROM — chỉ gợi ý tới END cho chắc đúng.
            return Rule(lead + $"ELSE {c}2 END");
        }

        // ---- CREATE TABLE zdmxxx( → cột danh mục chuẩn ----
        if (CreateTableRx.Match(line) is { Success: true } cm)
        {
            var t = cm.Groups["t"].Value;
            var code = Regex.Replace(t.ToLowerInvariant(), "^(z?dm|z)", "");
            if (!Regex.IsMatch(t, "^z?dm", RegexOptions.IgnoreCase) || code.Length == 0) return null;
            var ind = Indent(line) + "\t";
            return Rule("\n" + string.Join(",\n", new[]
            {
                $"[ma_{code}] [varchar](32) NOT NULL", $"[ten_{code}] [nvarchar](256) NOT NULL", $"[ten_{code}2] [nvarchar](256) NULL",
                "[ghi_chu] [nvarchar](512) NULL", "[status] [char](1) NULL", "[datetime0] [datetime] NULL", "[datetime2] [datetime] NULL",
                "[user_id0] [int] NULL", "[user_id2] [int] NULL", $"CONSTRAINT [PK_{t}] PRIMARY KEY CLUSTERED ([ma_{code}])",
            }.Select(x => ind + x)) + "\n" + Indent(line) + ")");
        }

        // ---- tên cột trong CREATE TABLE / ALTER TABLE … ADD, hoặc DECLARE @tên_cột → kiểu hay dùng ----
        if (AlterAddRx.Match(line) is { Success: true } am && TypeOf(am.Groups["c"].Value, columnTypes) is { } at) return Rule(at + " NULL");
        if (ColumnDefRx.Match(line) is { Success: true } colm && InsideCreateTable(before) && TypeOf(colm.Groups["c"].Value, columnTypes) is { } ct) return Rule(ct + " NULL");
        if (DeclareVarRx.Match(line) is { Success: true } dv)
        {
            var name = dv.Groups["c"].Value;
            string Case(string t) => Regex.IsMatch(line, @"\bdeclare\b") ? t.ToLowerInvariant() : t.ToUpperInvariant();   // theo kiểu chữ đang gõ
            // 1. biến cùng tên đã khai báo phía trên (tham số / DECLARE ở batch khác) → y kiểu đó (đo trên KOG: đúng ~93%)
            if (DeclTypeRx.Matches(before).LastOrDefault(m => m.Groups["n"].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } prevDecl)
                return new Ghost(Case(NormType(prevDecl.Groups["t"].Value)), "decl", 0.99);
            // 2. ma trận: kiểu hay dùng nhất của biến tên này trong code thật
            if (kb.Find("decl", name, "") is { } kd)
                return Choose(kd.Pattern, Case, before, ignoreThresholds ? 0 : kd.MinShare) is { } c && (ignoreThresholds || c.Share >= kd.MinShare)
                    ? new Ghost(c.Text, "decl", c.Share) : Declined;
            // biến chưa gặp (vd @Beetest) không đoán — đo trên KOG: đoán theo tên cột / kiểu hay dùng trong file làm tụt dưới 90%.
            // Gợi ý Fast dài (chấp nhận kém chắc): lấy kiểu của biến khai báo gần nhất phía trên (hay khai báo các biến cùng loại liền nhau).
            if (ignoreThresholds && DeclTypeRx.Matches(before).LastOrDefault() is { } near)
                return new Ghost(Case(NormType(near.Groups["t"].Value)), "decl:near", 0.5);
            return Declined;
        }

        // ---- EXEC routine  /  routine( → tham số ----
        if (ExecRx.Match(line) is { Success: true } em) return CallArgs(em.Groups["n"].Value, ContextOf(line[..em.Index]), line, before, others, false, kb, ignoreThresholds);
        // routine( — chỉ hàm của Fast / dự án (có $, ff_…, dbo.…), không đụng hàm hệ thống như CAST(, ISNULL(.
        if (FuncOpenRx.Match(line) is { Success: true } fo
            && (fo.Groups["n"].Value.Contains('$') || fo.Groups["n"].Value.StartsWith("ff_", StringComparison.OrdinalIgnoreCase) || fo.Groups["n"].Value.Contains("dbo.", StringComparison.OrdinalIgnoreCase)))
            return CallArgs(fo.Groups["n"].Value, ContextOf(line[..fo.Index]), line, before, others, true, kb, ignoreThresholds);

        return null;
    }

    /// <summary>Chọn cách viết trong 1 ô ma trận: điền chỗ trống ({lhs}, {if}, {a}…) rồi bỏ các cách viết dùng biến @x mà phía trên con trỏ chưa có
    /// (tham số / DECLARE) — chắc chắn sai. Độ tin = số lần của cách còn lại đứng đầu / (tổng các cách còn lại + phần đuôi không lưu + 1).
    /// Cách đứng đầu chưa đủ <paramref name="minShare"/> thì gợi ý phần ĐẦU của nó tới chỗ các cách viết bắt đầu rẽ nhánh — phần đó đủ chắc
    /// (vd "'a.ma_kho', " rồi dừng, vì sau đó store thì @Site store thì ''), giống AI hoàn thành code dừng ở chỗ không chắc.</summary>
    private static (string Text, double Share)? Choose(Pattern p, Func<string, string> fill, string before, double minShare)
    {
        if (p.Options.Count == 0) return null;   // ô chặn
        HashSet<string>? declared = null;
        bool Ok(string t)
        {
            if (t.Contains("{lhs}") || t.Contains("{if}")) return false;
            declared ??= new HashSet<string>(Regex.Matches(before, @"(?<![@\w])@\w+").Select(m => m.Value), StringComparer.OrdinalIgnoreCase);
            return Regex.Matches(t, @"(?<![@\w])@\w+").All(m => declared.Contains(m.Value));
        }
        var valid = p.Options.Select(o => (o.N, Text: fill(o.Text))).Where(x => Ok(x.Text)).ToList();
        if (valid.Count == 0) return null;
        double total = valid.Sum(x => x.N) + p.Tail + 1;
        var top = valid[0];
        if (top.N / total >= minShare || !PartialSuggestions) return (top.Text, Math.Round(top.N / total, 3));
        // điểm cắt: trước khoảng trắng, sau dấu phẩy / mở ngoặc — từ dài tới ngắn
        var cuts = Enumerable.Range(1, top.Text.Length - 1).Where(i => top.Text[i] == ' ' || top.Text[i - 1] == ',' || top.Text[i - 1] == '(').Reverse();
        foreach (var cut in cuts)
        {
            var pre = top.Text[..cut].TrimEnd();
            var np = FastGhostMiner.Norm(pre);
            if (np.Length < 3) break;
            var share = valid.Where(x => FastGhostMiner.Norm(x.Text).StartsWith(np, StringComparison.Ordinal)).Sum(x => x.N) / total;
            if (share >= minShare) return (pre, Math.Round(share, 3));
        }
        return (top.Text, Math.Round(top.N / total, 3));
    }

    /// <summary>Biến được gán trên dòng (SET @x = … / SELECT @x = … / , @x = …) — tham số của hàm thường chính là nó (GetCheckKey(@x)).</summary>
    public static string? LhsOf(string linePrefix) => Regex.Matches(linePrefix, @"(?<![@\w])@\w+(?=\s*=(?!=))").LastOrDefault()?.Value;

    /// <summary>Biến trong "IF @x …" đầu dòng — thường được truyền tiếp vào hàm lọc cùng dòng.</summary>
    public static string? IfVarOf(string linePrefix) => Regex.Match(linePrefix, @"^\s*IF\s*\(?\s*(@\w+)", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>Tham số → mẫu: biến trùng biến được gán / biến của IF cùng dòng thành {lhs} / {if} để các store đặt tên khác nhau gộp chung 1 ô.</summary>
    public static string AbstractVars(string args, string linePrefix)
    {
        if (LhsOf(linePrefix) is { } lhs) args = Regex.Replace(args, Regex.Escape(lhs) + @"\b", "{lhs}", RegexOptions.IgnoreCase);
        if (IfVarOf(linePrefix) is { } iv) args = Regex.Replace(args, Regex.Escape(iv) + @"\b", "{if}", RegexOptions.IgnoreCase);
        return args;
    }

    private static string FillVars(string pattern, string linePrefix)
    {
        if (pattern.Contains("{lhs}") && LhsOf(linePrefix) is { } lhs) pattern = pattern.Replace("{lhs}", lhs);
        if (pattern.Contains("{if}") && IfVarOf(linePrefix) is { } iv) pattern = pattern.Replace("{if}", iv);
        return pattern;
    }

    /// <summary>Từ khoá đứng ngay trước lời gọi được giữ làm ngữ cảnh; còn lại gom thành "w".</summary>
    private static readonly HashSet<string> CtxWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select", "set", "from", "join", "where", "and", "or", "on", "then", "else", "when", "exec", "execute", "insert", "into", "values",
        "return", "begin", "in", "as", "if", "not", "by", "union", "all", "distinct", "top", "case", "declare", "print", "end",
    };

    /// <summary>Ngữ cảnh của 1 lời gọi = thứ đứng ngay trước nó trên dòng: "^" (đầu dòng), 1 ký hiệu ("=", "(", ",", "+"…) hoặc 1 từ khoá.
    /// Là cột thứ 2 của ma trận: cùng 1 hàm, sau "=" và sau "FROM" thường truyền tham số khác nhau.</summary>
    public static string ContextOf(string before)
    {
        var s = before.TrimEnd();
        if (s.Length == 0) return "^";
        var ch = s[^1];
        if (!char.IsLetterOrDigit(ch) && ch != '_') return ch.ToString();
        var w = Regex.Match(s, @"[\w@#$]+$").Value;
        return CtxWords.Contains(w) ? w.ToLowerInvariant() : "w";
    }

    // ---------------------------------------------------------------- gợi ý chung: theo chính file + ma trận đầu dòng

    /// <summary>1 token SQL (bỏ comment; chuỗi '…' là 1 token) và vị trí trong văn bản.</summary>
    public readonly record struct Tok(string Text, int Start, int End);

    private static readonly Regex TokRx = new(@"--[^\n]*|/\*[\s\S]*?(?:\*/|$)|N?'(?:[^']|'')*'?|[@#]*[\p{L}_][\w$#@]*|\d+(?:\.\d+)?|\S", RegexOptions.Compiled);

    public static List<Tok> Tokens(string s) =>
        TokRx.Matches(s).Where(m => !m.Value.StartsWith("--") && !m.Value.StartsWith("/*")).Select(m => new Tok(m.Value, m.Index, m.Index + m.Length)).ToList();

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#' || c == '@';

    /// <summary>Các chỗ khớp của "theo chính file": số token khớp trên dòng đang gõ + phần viết tiếp tới cuối dòng của từng chỗ (kèm vị trí cuối mỗi token
    /// để cắt 1 / 3 / 8 token). null = không có chỗ nào khớp cả phần đã gõ của dòng.</summary>
    public sealed record SameMatch(int LineTokens, IReadOnlyList<(string Text, int[] Ends)> Conts);

    /// <summary>"Theo chính file": tìm các chỗ trong file (phía trên con trỏ VÀ phía dưới dòng đang gõ) mà đoạn token ngay trước con trỏ cũng xuất hiện
    /// (bỏ qua xuống dòng / khoảng trắng / hoa thường, dấu phẩy đầu dòng) và CẢ phần đã gõ của dòng đều khớp — vd đang gõ
    /// "loai_kh = case when loai_kh = '100000002' then" mà trong file đã có "loai_kh = CASE ↵ WHEN loai_kh ='100000002' THEN '0'" → "'0'";
    /// chèn dòng "INSERT INTO #apiError SELECT TOP 1 'nh_kh1'" phía trên dòng giống hệt → phần còn lại của dòng đó.
    /// Đang gõ dở 1 từ thì từ đó phải là phần đầu của từ tiếp theo ở chỗ cũ (phần còn lại theo kiểu chữ đang gõ).</summary>
    public static SameMatch? SameCandidates(string before, string after = "")
    {
        if (before.Length == 0) return null;
        if (before.Length > 80_000) before = before[^80_000..];   // script rất dài: chỉ dò ~80 KB quanh con trỏ cho nhanh mỗi phím
        if (after.Length > 80_000) after = after[..80_000];
        var toks = Tokens(before);
        var lineStart = before.LastIndexOf('\n') + 1;
        var n = toks.Count;
        var partial = "";
        if (n > 0 && toks[^1].End == before.Length && IsWordChar(before[^1])) { partial = toks[^1].Text; n--; }
        if (n < 2) return null;
        var low = toks.Select(t => t.Text.ToLowerInvariant()).ToArray();
        var endsWithSpace = char.IsWhiteSpace(before[^1]);
        // số token ngữ cảnh nằm trên dòng đang gõ — không tính dấu phẩy đầu dòng (", loai_kh = …" vẫn khớp "loai_kh = …")
        var onLine = n - toks.Take(n).Count(t => t.Start < lineStart);
        while (onLine > 0 && toks[n - onLine].Text == ",") onLine--;
        if (onLine < 1) return null;
        var conts = new List<(string, int[])>();

        void Scan(List<Tok> src, string[] lowSrc, string text, int limit)
        {
            for (var e = Math.Min(limit, src.Count - 1); e >= 1; e--)   // token e = chỗ viết tiếp (token e-1 khớp token cuối đã gõ)
            {
                if (lowSrc[e - 1] != low[n - 1]) continue;
                if (partial.Length > 0 && !lowSrc[e].StartsWith(partial, StringComparison.OrdinalIgnoreCase)) continue;
                var len = 1;
                while (len < onLine && e - 1 - len >= 0 && lowSrc[e - 1 - len] == low[n - 1 - len]) len++;
                if (len < onLine) continue;   // phải khớp cả phần đã gõ của dòng
                var lineEnd = text.IndexOf('\n', src[e].Start);
                if (lineEnd < 0) lineEnd = text.Length;
                var first = partial.Length >= src[e].Text.Length ? e + 1 : e;   // gõ đủ cả từ (chưa có dấu cách) → viết tiếp từ token kế
                if (first >= src.Count || src[first].Start >= lineEnd) continue;
                var from = partial.Length > 0 ? src[e].Start + partial.Length : endsWithSpace ? src[e].Start : src[e - 1].End;
                var line = StripLineComment(text[from..lineEnd]).TrimEnd();
                var ends = src.Skip(first).TakeWhile(t => t.Start < lineEnd).Select(t => t.End - from).Where(x => x <= line.Length).ToArray();
                if (line.Trim().Length == 0 || ends.Length == 0) continue;
                if (partial.Length > 0 && from < src[e].End)
                {
                    var rest = Math.Min(line.Length, src[e].End - from);
                    line = (partial == partial.ToLowerInvariant() ? line[..rest].ToLowerInvariant() : partial == partial.ToUpperInvariant() ? line[..rest].ToUpperInvariant() : line[..rest]) + line[rest..];
                }
                conts.Add((line, ends));
            }
        }

        // phía trên: các dòng trước dòng đang gõ
        Scan(toks, low, before, toks.FindLastIndex(n - 1, t => t.Start < lineStart));
        // phía dưới: các dòng sau dòng đang gõ (đang chèn dòng mới giữa code có sẵn)
        if (after.Length > 0)
        {
            var at = Tokens(after);
            Scan(at, at.Select(t => t.Text.ToLowerInvariant()).ToArray(), after, at.Count - 1);
        }
        return conts.Count == 0 ? null : new SameMatch(onLine, conts);
    }

    /// <summary>Gợi ý "theo chính file" dài tối đa <paramref name="maxTokens"/> token (0 = tới cuối dòng). Độ tin = (1 − 1/(số token khớp trên dòng + 1))
    /// × tỷ lệ các chỗ khớp cùng viết tiếp giống nhau (sau khi cắt). Kind = "same" (1 token) hoặc "same:3", "same:8", "same:0".</summary>
    public static Ghost? SameFile(SameMatch? m, int maxTokens)
    {
        if (m is null) return null;
        var cut = m.Conts.Select(c => (maxTokens <= 0 || c.Ends.Length <= maxTokens ? c.Text : c.Text[..c.Ends[maxTokens - 1]]).TrimEnd())
            .Where(c => c.Trim().Length > 0).ToList();
        if (cut.Count == 0) return null;
        var top = cut.GroupBy(c => FastGhostMiner.Norm(c)).OrderByDescending(g => g.Count()).First();
        return new Ghost(top.First(), SameKind(maxTokens), Math.Round((1 - 1.0 / (m.LineTokens + 1)) * top.Count() / cut.Count, 3));
    }

    public static string SameKind(int maxTokens) => maxTokens == 1 ? "same" : "same:" + maxTokens;

    /// <summary>Các bậc độ dài của "theo chính file", dài trước: mỗi bậc có ngưỡng riêng (hiệu chỉnh trên KOG) — khớp càng dài mới được gợi ý càng dài.
    /// Bậc nào ma trận chưa hiệu chỉnh thì bỏ qua (riêng bậc 1 token luôn có).</summary>
    public static readonly int[] SameLadder = { 0, 8, 3, 1 };

    /// <summary>Tương thích: "theo chính file" chỉ nhìn phía trên, 1 token.</summary>
    public static Ghost? SameFile(string before) => SameFile(SameCandidates(before), 1);


    /// <summary>Số token đầu dòng tối đa làm khoá của ma trận đầu dòng.</summary>
    public const int LineMaxTokens = 6;

    private static bool IsAliasToken(string t) => t.Length <= 2 && Regex.IsMatch(t, @"^[A-Za-z]\d?$");

    /// <summary>Khoá ma trận đầu dòng từ các token đã gõ: chữ thường, cách nhau 1 dấu cách; alias bảng 1 chữ ("a", "b1"…) → {1}, {2} theo thứ tự
    /// gặp (để "LEFT JOIN dmkh b ON" và "LEFT JOIN dmkh k ON" chung 1 ô). Trả kèm bảng alias → chỗ trống.</summary>
    public static (string Key, Dictionary<string, string> Map) LineKey(IEnumerable<Tok> toks)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = toks.Select(t => IsAliasToken(t.Text) ? (map.TryGetValue(t.Text, out var p) ? p : map[t.Text] = "{" + (map.Count + 1) + "}") : t.Text.ToLowerInvariant());
        return (string.Join(" ", parts), map);
    }

    /// <summary>Thay alias trong phần viết tiếp bằng chỗ trống theo <paramref name="map"/> (chỉ token alias, không đụng chuỗi / tên dài).</summary>
    public static string MapAliases(string cont, IReadOnlyDictionary<string, string> map)
    {
        if (map.Count == 0) return cont;
        var sb = new System.Text.StringBuilder(cont);
        foreach (var t in Tokens(cont).AsEnumerable().Reverse())
            if (IsAliasToken(t.Text) && map.TryGetValue(t.Text, out var p)) sb.Remove(t.Start, t.End - t.Start).Insert(t.Start, p);
        return sb.ToString();
    }

    /// <summary>"Ma trận đầu dòng": dòng đang gõ có ≤ <see cref="LineMaxTokens"/> token, con trỏ ở ranh giới từ → tra ô theo khoá đầu dòng
    /// (vd "left join dmkh {1} on" → "a.ma_kh = {1}.ma_kh"), điền lại alias, bỏ cách viết dùng biến chưa khai báo.</summary>
    public static Ghost? LineStart(string before, KnowledgeBase kb, double minShare, bool partial = false)
    {
        var line = before[(before.LastIndexOf('\n') + 1)..];
        if (line.Length == 0 || IsWordChar(line[^1]) || line[^1] == '.') return null;
        var toks = Tokens(line);
        if (toks.Count == 0 || toks.Count > LineMaxTokens) return null;
        var (key, map) = LineKey(toks);
        if (kb.Find("line", key, "") is not { } k) return null;
        var back = map.ToDictionary(x => x.Value, x => x.Key);
        // ghost tự động không gợi ý từng phần (minShare 0): ngưỡng "line" hiệu chỉnh trên cả cách viết; "Gợi ý Fast dài" thì cho từng phần
        return Choose(k.Pattern, t => Regex.Replace(t, @"\{\d+\}", m => back.TryGetValue(m.Value, out var a) ? a : m.Value), before, partial ? minShare : 0) is { } c
            && !Regex.IsMatch(c.Text, @"\{\d+\}") ? new Ghost(c.Text, "line", c.Share) : null;
    }

    private static string StripLineComment(string l)
    {
        var inS = false;
        for (var i = 0; i < l.Length - 1; i++) { if (l[i] == '\'') inS = !inS; else if (!inS && l[i] == '-' && l[i + 1] == '-') return l[..i]; }
        return l;
    }

    private static readonly Regex AliasRx = new(@"(?<![\w$.@#])(?!dbo\.)(?<a>[A-Za-z]\w{0,2})\.(?=[A-Za-z_])", RegexOptions.Compiled);
    private static readonly Regex CaseAndRx = new(@"CASE\s+WHEN\s+@Key\s*=\s*''\s+THEN\s+''\s+ELSE\s+' and '\s+END\s*\+\s*'", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PctPlusRx = new(@"'%'\s*\+\s*''''", RegexOptions.Compiled);
    private static readonly Regex NoRtrimRx = new(@"REPLACE\s*\(\s*(@\w+)\s*,", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DocFilterRx = new(@"(?im)^\s*IF\s+@\w+\s*<>\s*''\s*(?<r>SET\s+@Key\b.*)$", RegexOptions.Compiled);

    /// <summary>Dòng lọc → dạng chuẩn để các store viết hơi khác nhau gộp chung 1 ô ma trận: alias bảng → {a},
    /// "CASE WHEN @Key = '' THEN '' ELSE ' and ' END + '" → "' and ", "'%' + ''''" → "'%'''", "REPLACE(@x," → "REPLACE(RTRIM(@x),".
    /// Khi gợi ý, <see cref="FilterStyle"/> đổi ngược lại theo văn phong dòng lọc gần nhất phía trên.</summary>
    public static string CanonFilter(string filter)
    {
        filter = CaseAndRx.Replace(filter, "' and ");
        filter = PctPlusRx.Replace(filter, "'%'''");
        filter = NoRtrimRx.Replace(filter, "REPLACE(RTRIM($1),");
        var aliases = AliasRx.Matches(filter).Select(m => m.Groups["a"].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return aliases.Count == 1 ? AliasRx.Replace(filter, "{a}") : filter;
    }

    /// <summary>Văn phong lọc của store đang viết = của dòng "IF @x &lt;&gt; '' SET @Key …" GẦN NHẤT phía trên (cùng khối lọc; đo trên code thật:
    /// lấy đa số cả store sai nhiều vì 1 store thường có vài khối lọc viết khác nhau). Chưa có dòng nào = dạng chuẩn, alias "a.".</summary>
    private sealed record FilterStyle(bool CaseAnd, bool PctPlus, bool NoRtrim, string Alias)
    {
        public static FilterStyle Of(string before)
        {
            if (DocFilterRx.Matches(before).LastOrDefault()?.Groups["r"].Value is not { } l) return new FilterStyle(false, false, false, "a.");
            var alias = Regex.Match(CaseAndRx.Replace(l, "' and "), @"' and\s+(?:(?<a>[A-Za-z]\w{0,2})\.)?[A-Za-z_]") is { Success: true } m ? m.Groups["a"].Value : null;
            return new FilterStyle(CaseAndRx.IsMatch(l), PctPlusRx.IsMatch(l), NoRtrimRx.IsMatch(l), alias is null ? "a." : alias.Length == 0 ? "" : alias + ".");
        }

        public string Apply(string canon)
        {
            var s = canon.Replace("{a}", Alias);
            if (CaseAnd) s = new Regex(Regex.Escape("' and ")).Replace(s, "CASE WHEN @Key = '' THEN '' ELSE ' and ' END + '", 1);
            if (PctPlus) s = s.Replace("'%'''", "'%' + ''''");
            if (NoRtrim) s = Regex.Replace(s, @"REPLACE\(RTRIM\((@\w+)\),", "REPLACE($1,");
            return s;
        }
    }

    /// <summary>Dòng lọc chuẩn cho biến <paramref name="v"/> (phần sau "IF @v &lt;&gt; ''"); null nếu không biết cột.</summary>
    internal static string? FilterLine(string v, string alias)
    {
        string Esc(string x) => $"REPLACE(RTRIM(@{x}), '''', '''''')";
        if (Regex.IsMatch(v, "^(Invoice|Voucher)?(No)?From$", RegexOptions.IgnoreCase))
            return $"SET @Key = @Key + ' and {alias}so_ct >= ''' + dbo.ff_PadL({Esc(v)}, @Size) + ''''";
        if (Regex.IsMatch(v, "^(Invoice|Voucher)?(No)?To$", RegexOptions.IgnoreCase))
            return $"SET @Key = @Key + ' and {alias}so_ct <= ''' + dbo.ff_PadL({Esc(v)}, @Size) + ''''";
        if (Regex.IsMatch(v, "^(TransactionCode|TransactionList)$", RegexOptions.IgnoreCase))
            return $"SET @Key = @Key + ' and {alias}ma_gd in (''' + REPLACE(REPLACE(REPLACE(@{v}, ' ', ''), '''', ''''''), ',', ''',''') + ''')'";
        if (Regex.IsMatch(v, "^(VoucherCode|VCs)$", RegexOptions.IgnoreCase))
            return $"SET @Key = @Key + ' and {alias}ma_ct in (''' + REPLACE(REPLACE(REPLACE(@{v}, ' ', ''), '''', ''''''), ',', ''',''') + ''')'";
        if (Regex.IsMatch(v, "^(Description|Decription|Note)$", RegexOptions.IgnoreCase))
            return $"SET @Key = @Key + ' and {alias}dien_giai like N''%' + {Esc(v)} + '%'''";
        if (Regex.IsMatch(v, "^Account(Ref)?$", RegexOptions.IgnoreCase))
            return $"SET @Key = @Key + ' and ' + dbo.FastBusiness$Function$System$GetAccountFilter('{alias}{(v.EndsWith("Ref", StringComparison.OrdinalIgnoreCase) ? "tk_du" : "tk")}', 'like', @{v})";
        var field = FilterFields.TryGetValue(v, out var f) ? f
            : Regex.Match(v, @"^(?<g>ItemGroup|CustomerGroup)(?<n>[123])$", RegexOptions.IgnoreCase) is { Success: true } g
                ? (g.Groups["g"].Value.StartsWith("Item", StringComparison.OrdinalIgnoreCase) ? "nh_vt" : "nh_kh") + g.Groups["n"].Value
            : FieldDictionaryService.Instance.FieldForController(v);
        return field is null ? null : $"SET @Key = @Key + ' and {alias}{field} like ''' + {Esc(v)} + '%'''";
    }

    // ---------------------------------------------------------------- ghép điều kiện lọc cho tham số chưa gặp (chỉ "Gợi ý Fast dài")

    /// <summary>Dòng lọc "đơn giản" trong file: IF @v &lt;&gt; '' SET @Key = @Key + ' and [alias.]cột like|= ''' + REPLACE([RTRIM(]@v … — làm khuôn để ghép.</summary>
    private static readonly Regex SimpleFilterRx = new(
        @"(?im)^[ \t]*IF\s+@(?<v>\w+)\s*<>\s*''\s*(?<r>SET\s+@Key\s*=\s*@Key\s*\+\s*(?:CASE\s+WHEN\s+@Key\s*=\s*''\s+THEN\s+''\s+ELSE\s+' and '\s+END\s*\+\s*'|' and\s+)(?:[A-Za-z]\w{0,2}\.)?(?<c>[A-Za-z_]\w*)\s+(?:like|=)\s+'''\s*\+\s*REPLACE\s*\(\s*(?:RTRIM\s*\(\s*)?@\k<v>\b[^\n]*)$",
        RegexOptions.Compiled);

    /// <summary>Tên biến không phải tham số lọc dù là chuỗi.</summary>
    private static readonly HashSet<string> NonFilterVars = new(StringComparer.OrdinalIgnoreCase) { "Unit", "UnitKey", "Language", "Key", "q", "s", "UserID", "Admin", "filterKey", "queryJoin", "TableName" };

    /// <summary>@v là tham số / biến chuỗi (char / varchar / nvarchar, dài ≥ 2) khai báo phía trên và không thuộc nhóm biến hệ thống.</summary>
    private static bool IsFilterParam(string v, string before) =>
        !NonFilterVars.Contains(v)
        && DeclTypeRx.Matches(before).LastOrDefault(m => m.Groups["n"].Value.Equals(v, StringComparison.OrdinalIgnoreCase)) is { } d
        && Regex.IsMatch(d.Groups["t"].Value, @"^N?VARCHAR|^N?CHAR\s*\(\s*(?:[2-9]|\d{2,}|max)", RegexOptions.IgnoreCase);

    /// <summary>Cột trong 1 cách viết lọc ("' and {a}ma_kh like", "GetCodeFilter('ma_bp'", "GetAccountFilter('a.tk'"…).</summary>
    private static readonly Regex FilterColRx = new(@"(?<![\w@$#])(?:\{a\}|[A-Za-z]\w{0,2}\.)?(?<c>(?:ma|so|nh|loai|tk)(?:_\w*)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Đuôi làm đổi cách lọc: …List (trong danh sách), …From / …To (khoảng).</summary>
    private static readonly Regex RoleSuffixRx = new(@"^(?<b>[A-Za-z]\w*?)(?<s>List|From|To)$", RegexOptions.Compiled);

    /// <summary>Tên cùng nghĩa với @v: bỏ tiền tố kiểu Hungary (cAccount, pCustomer → Account, Customer) và đuôi không đổi nghĩa (…ID, …Code).</summary>
    private static List<string> SameMeaningNames(string v)
    {
        var core = Regex.Replace(v, "^[a-z](?=[A-Z])", "");
        return new[] { core, Regex.Replace(core, "(ID|Id|Code)$", "") }
            .Where(x => x.Length >= 2 && !x.Equals(v, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Mọi ô lọc của ma trận (lớp trên trước, bỏ ô chặn).</summary>
    private static IEnumerable<(string Key, Pattern P)> FilterCells(KnowledgeBase kb)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var k = kb; k is not null; k = k.Fallback)
            foreach (var (key, p) in k.Filters)
                if (!key.Contains('|') && p.Options.Count > 0 && seen.Add(key)) yield return (key, p);
    }

    /// <summary>Cột mà tham số @v lọc. Ứng viên: chỗ dùng ngay trong script (cột = @v / cột like @v) → ô lọc của tên cùng nghĩa trong ma trận →
    /// bảng biến → cột chuẩn Fast → từ điển field (header.xml). Nhiều ứng viên thì ưu tiên cột đã xuất hiện trong script (dự án đặt cột riêng, vd ma_ns).</summary>
    private static string? ColumnFor(string v, string text, KnowledgeBase kb)
    {
        if (Regex.Match(text, @"(?<![\w@.])(?:[A-Za-z]\w{0,2}\.)?(?<c>(?:ma|so|nh|loai|tk)_?\w*)\s*(?:=|like)\s*@" + Regex.Escape(v) + @"\b", RegexOptions.IgnoreCase) is { Success: true } use)
            return use.Groups["c"].Value.ToLowerInvariant();
        var names = new[] { v }.Concat(SameMeaningNames(v)).ToList();
        var cands = new List<string>();
        foreach (var s in names)
            if (kb.Find("filter", s, "") is { } kf && kf.Pattern.Options.Count > 0 && FilterColRx.Match(kf.Pattern.Text) is { Success: true } km) cands.Add(km.Groups["c"].Value.ToLowerInvariant());
        foreach (var s in names)
            if (FilterFields.TryGetValue(s, out var f)) cands.Add(f);
        foreach (var s in names)
            if (FieldDictionaryService.Instance.FieldForController(s) is { } fd) cands.Add(fd.ToLowerInvariant());
        cands = cands.Distinct().ToList();
        return cands.FirstOrDefault(c => Regex.IsMatch(text, @"(?<![\w@])" + Regex.Escape(c) + @"\b", RegexOptions.IgnoreCase)) ?? cands.FirstOrDefault();
    }

    /// <summary>Thứ tự các cách ghép (đo trên KOG, xem <see cref="FastGhostMiner.EvaluateCompose"/>): cách đứng trước có gợi ý thì lấy.</summary>
    public static string[] ComposeOrder { get; set; } = { "file" };

    /// <summary>Các cách "ghép" dòng lọc cho tham số @v chưa có ô trong ma trận (chỉ "Gợi ý Fast dài"):
    /// <list type="bullet">
    /// <item>same — tên cùng nghĩa đã có ô (@cAccount → ô @Account, @CustomerID → ô @Customer): lấy cách viết của ô đó, đổi tên biến.</item>
    /// <item>role — tên có đuôi đổi cách lọc (…List / …From / …To): khuôn hay gặp nhất của các ô cùng đuôi (vd …List → GetCodeFilter('cột', 'inlist', @v)),
    /// thay cột của gốc tên (@JobList → ma_vv).</item>
    /// <item>file — dòng lọc đơn giản GẦN NHẤT trong file làm khuôn (giữ văn phong like / =, RTRIM, '%', alias, CASE WHEN @Key …), thay biến + cột.</item>
    /// <item>std — dòng lọc chuẩn theo bảng biến / từ điển field (FilterLine).</item>
    /// </list></summary>
    public static IEnumerable<(string How, string Text)> ComposeCandidates(string v, string text, string before, KnowledgeBase kb)
    {
        if (!IsFilterParam(v, before)) yield break;
        var style = FilterStyle.Of(before);
        string Rename(string t, string from) => Regex.Replace(t, "@" + Regex.Escape(from) + @"\b", "@" + v, RegexOptions.IgnoreCase);

        foreach (var s in SameMeaningNames(v))
            if (kb.Find("filter", s, "") is { } kf && kf.Pattern.Options.Count > 0) { yield return ("same", style.Apply(Rename(kf.Pattern.Text, s))); break; }

        if (RoleSuffixRx.Match(v) is { Success: true } rm && ColumnFor(rm.Groups["b"].Value, text, kb) is { } rcol)
        {
            var suffix = rm.Groups["s"].Value;
            var votes = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (key, p) in FilterCells(kb))
            {
                if (RoleSuffixRx.Match(key) is not { Success: true } km || !km.Groups["s"].Value.Equals(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                var t = p.Options[0].Text;
                if (FilterColRx.Match(t) is not { Success: true } cm) continue;
                var tpl = Regex.Replace(t.Remove(cm.Groups["c"].Index, cm.Groups["c"].Length).Insert(cm.Groups["c"].Index, "\u0001"), "@" + Regex.Escape(key) + @"\b", "@\u0002", RegexOptions.IgnoreCase);
                votes[tpl] = votes.GetValueOrDefault(tpl) + 1;
            }
            if (votes.Count > 0 && votes.MaxBy(x => x.Value) is var best && best.Value >= 2)
                yield return ("role", style.Apply(best.Key.Replace("\u0001", rcol).Replace("\u0002", v)));
        }

        var col = ColumnFor(v, text, kb);
        if (col is not null)
        {
            var tpl = SimpleFilterRx.Matches(before).LastOrDefault(m => !m.Groups["v"].Value.Equals(v, StringComparison.OrdinalIgnoreCase))
                      ?? SimpleFilterRx.Matches(text[Math.Min(before.Length, text.Length)..]).FirstOrDefault(m => !m.Groups["v"].Value.Equals(v, StringComparison.OrdinalIgnoreCase));
            if (tpl is not null)
            {
                var r = tpl.Groups["r"];
                var c = tpl.Groups["c"];
                yield return ("file", Rename(r.Value[..(c.Index - r.Index)] + col + r.Value[(c.Index - r.Index + c.Length)..], tpl.Groups["v"].Value).TrimEnd());
            }
        }

        if (FilterLine(v, AliasOf(text)) is { } std) yield return ("std", std);
        else if (col is not null) yield return ("std", style.Apply($"SET @Key = @Key + ' and {{a}}{col} like ''' + REPLACE(RTRIM(@{v}), '''', '''''') + '%'''"));
    }

    /// <summary>Dòng lọc ghép cho @v theo <see cref="ComposeOrder"/>; null = không ghép được.</summary>
    public static string? ComposeFilter(string v, string text, string before, KnowledgeBase? kb = null)
    {
        var all = ComposeCandidates(v, text, before, kb ?? Knowledge).ToList();
        foreach (var how in ComposeOrder)
            if (all.FirstOrDefault(x => x.How == how) is { Text: { } t }) return t;
        return null;
    }

    // ---------------------------------------------------------------- câu tiếp theo trên dòng trống ("next")

    /// <summary>Số token đầu dòng làm khoá của ma trận câu tiếp theo.</summary>
    public const int NextKeyTokens = 6;

    /// <summary>Khoá 1 dòng code cho ma trận câu tiếp theo: ≤ <see cref="NextKeyTokens"/> token đầu dòng (như ma trận đầu dòng, alias → {1}…);
    /// null = dòng không có code (trống / chỉ comment).</summary>
    public static string? NextKey(string line)
    {
        var toks = Tokens(StripLineComment(line));
        return toks.Count == 0 ? null : LineKey(toks.Take(NextKeyTokens)).Key;
    }

    /// <summary>Các dòng có code (bỏ dòng trống, dòng chỉ có comment "--"), giữ nguyên chữ.</summary>
    public static IEnumerable<string> CodeLines(IEnumerable<string> lines) => lines.Where(l => StripLineComment(l).Trim().Length > 0 && !l.TrimStart().StartsWith("/*") && !l.TrimStart().StartsWith("*"));

    /// <summary>"Câu tiếp theo": con trỏ trên dòng trống → dòng hay viết ngay sau dòng code phía trên trong store thật (ô theo 2 dòng code trước rồi
    /// theo 1 dòng), vd sau "IF @filterKey &lt;&gt; '' SET @Key = @Key + CASE …" → "SET @Key = dbo.FastBusiness$Function$System$GetCheckKey(@Key)".
    /// Bỏ cách viết dùng biến chưa khai báo, và cách đã có ngay bên dưới (đang chèn giữa code có sẵn). Dòng chưa thụt lề thì thụt theo dòng trên.</summary>
    public static Ghost? NextLine(string text, int caret, KnowledgeBase kb, double minShare, bool partial)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        caret -= Regex.Matches(text[..caret], "\r\n").Count;
        text = text.Replace("\r\n", "\n");
        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var lineEnd = text.IndexOf('\n', caret);
        if (lineEnd < 0) lineEnd = text.Length;
        var line = text[lineStart..caret];
        if (line.Trim().Length > 0 || text[caret..lineEnd].Trim().Length > 0 || lineStart == 0) return null;
        var prev = CodeLines(text[..(lineStart - 1)].Split('\n').Reverse()).Take(2).ToList();
        if (prev.Count == 0 || NextKey(prev[0]) is not { } k1) return null;
        if (kb.Find("next", k1, prev.Count > 1 ? NextKey(prev[1]) ?? "" : "") is not { } k) return null;
        var below = CodeLines(text[lineEnd..].Split('\n')).Take(3).Select(l => FastGhostMiner.Norm(StripLineComment(l))).ToHashSet();
        var p = new Pattern(k.Pattern.Options.Where(o => !below.Contains(FastGhostMiner.Norm(o.Text))).ToList(), k.Pattern.Count);
        if (Choose(p, t => t, text[..caret], partial ? minShare : 0) is not { } c || c.Share < minShare) return null;
        return new Ghost((line.Length == 0 ? Indent(prev[0]) : "") + c.Text, "next", c.Share);
    }

    /// <summary>Tham số của lời gọi: lời gọi cùng routine đã có trong script → y như vậy; không thì "cách gọi quen thuộc" của SqlHintCatalog.</summary>
    private static Ghost? CallArgs(string typedName, string ctx, string line, string before, string others, bool isFunction, KnowledgeBase kb, bool ignoreThresholds)
    {
        var bare = typedName.Split('.').Last().Trim('[', ']');
        if (bare.Length < 3) return null;
        var nameRx = @"(?:\[?\w+\]?\.)?\[?" + Regex.Escape(bare) + @"\]?";
        // Lời gọi cùng routine đã có trong script: chỉ chép khi nó lặp lại y hệt (≥ MinRepeats lần, chiếm ≥ 2/3) — đo trên code thật,
        // chép lời gọi đầu tiên sai nhiều (Balance$Job @dateFrom … 1 rồi @dateTo … 2; Split(@deptCode) rồi Split(@jobCode)).
        var prev = (isFunction
                ? Regex.Matches(others, @"\b" + nameRx + @"\((?<a>[^()\n]*(?:\([^()\n]*\)[^()\n]*)*)\)", RegexOptions.IgnoreCase).Select(x => x.Groups["a"].Value + ")")
                : Regex.Matches(others, @"(?im)\bEXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?" + nameRx + @"\s+(?<a>[^\n]+?)\s*$").Select(x => x.Groups["a"].Value.Trim().TrimEnd(';')))
            .ToList();
        if (DocLocalRepeats > 0 && prev.Count > 0)
        {
            var top = prev.GroupBy(x => Regex.Replace(x, @"\s+", " ").ToLowerInvariant()).OrderByDescending(g => g.Count()).First();
            if (top.Count() >= DocLocalRepeats && top.Count() * 3 >= prev.Count * 2) return new Ghost(top.First(), "doc", 1);
        }

        // Ma trận kiến thức: ô (routine, ngữ cảnh) rồi ô (routine) — chỉ khi độ tin ≥ ngưỡng đã hiệu chỉnh của loại; có mà không đủ chắc thì không gợi ý.
        var kind = isFunction ? "func" : "call";
        if (kb.Find(kind, bare, ctx) is { } k)
            return Choose(k.Pattern, t => FillVars(t, line), before, ignoreThresholds ? 0 : k.MinShare) is { } c && (ignoreThresholds || c.Share >= k.MinShare) ? new Ghost(c.Text, kind, c.Share) : Declined;
        // chưa gặp routine này trong code thật → không đoán, kể cả gợi ý chung (đo trên KOG: để gợi ý chung đoán tham số EXEC thì xuống ~89%)
        if (!GuessWhenUnknown && kb.Has(kind)) return Declined;

        var idiom = SqlHintCatalog.Idioms.FirstOrDefault(i => i.RoutineName.Equals(bare, StringComparison.OrdinalIgnoreCase));
        if (idiom is null) return null;
        var plain = PlainSnippet(idiom.Insert);
        var at = plain.IndexOf(bare, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var rest = plain[(at + bare.Length)..];
        var guess = isFunction ? (rest.StartsWith('(') ? rest[1..] : null) : (rest.StartsWith(' ') ? rest.TrimStart() : null);
        return guess is null ? null : new Ghost(guess, "guess", 0);
    }

    /// <summary>Cú pháp snippet → chữ thường: ${1:x} → x, ${2|a,b|} → a, ¤ → $.</summary>
    private static string PlainSnippet(string s)
    {
        s = Regex.Replace(s, @"\$\{\d+:([^}]*)\}", "$1");
        s = Regex.Replace(s, @"\$\{\d+\|([^,|}]*)[^}]*\}", "$1");
        s = Regex.Replace(s, @"\$\{\d+\}|\$\d+", "");
        return s.Replace('¤', '$');
    }

    private static string? TypeOf(string column, IReadOnlyDictionary<string, string>? db)
    {
        if (db is not null && db.TryGetValue(column, out var t)) return t;
        var c = column.ToLowerInvariant();
        return c switch
        {
            "status" => "char(1)",
            "datetime0" or "datetime2" => "datetime",
            "user_id0" or "user_id2" or "line_nbr" or "stt" or "nam" or "thang" or "ky" or "quy" => "int",
            "ghi_chu" or "dien_giai" => "nvarchar(512)",
            _ when c.StartsWith("ten_") => "nvarchar(256)",
            _ when c.StartsWith("ngay") => "smalldatetime",
            _ when Regex.IsMatch(c, @"^(so_luong|sl_|tien|t_tien|ps_|du_|gia|thue|ck|tl_|ty_gia)") => "numeric(19,4)",
            _ when Regex.IsMatch(c, @"^(ma_|nh_|tk|loai_)") => "varchar(32)",
            _ => null,
        };
    }

    private static bool InsideCreateTable(string before)
    {
        var m = Regex.Matches(before, @"\bCREATE\s+TABLE\b[^(]*\(", RegexOptions.IgnoreCase).LastOrDefault();
        if (m is null) return false;
        var depth = 1;
        foreach (var ch in before[(m.Index + m.Length)..]) { if (ch == '(') depth++; else if (ch == ')' && --depth == 0) return false; }
        return true;
    }

    /// <summary>Con trỏ (cuối <paramref name="line"/>) không nằm trong chuỗi '…' hay sau "--".</summary>
    private static bool CaretInCode(string line)
    {
        var inStr = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\'') inStr = !inStr;
            else if (!inStr && line[i] == '-' && i + 1 < line.Length && line[i + 1] == '-') return false;
        }
        return !inStr;
    }

    private static string AliasOf(string text) => Regex.IsMatch(text, @"\b(from|join)\s+[\w$%#.\[\]]+\s+(?:as\s+)?a\b", RegexOptions.IgnoreCase) || Regex.IsMatch(text, @"\ba\.\w+") ? "a." : "";

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    private static string? PreviousLine(string text, int lineStart)
    {
        if (lineStart == 0) return null;
        var end = lineStart - 1;
        var start = text.LastIndexOf('\n', Math.Max(0, end - 1)) + 1;
        return text[start..end];
    }

    private static string? NextNonEmptyLine(string text, int from)
    {
        foreach (var l in text[Math.Min(from, text.Length)..].Split('\n').Skip(1)) if (l.Trim().Length > 0) return l;
        return null;
    }

    // ---------------------------------------------------------------- ma trận kiến thức (Templates\FastGhostKnowledge.json + %AppData%\Bcode\ghost\*.json)

    /// <summary>1 cách viết trong ô ma trận, số lần gặp và số store khác nhau đã viết như vậy (chỉ có khi rút, không lưu).</summary>
    public sealed record Option(string Text, int N, int Docs = 0);

    /// <summary>1 ô ma trận: vài cách viết hay gặp nhất (nhiều → ít) + tổng số lần gặp (phần chênh = các cách hiếm không lưu).
    /// Không có cách viết nào = ô "chặn": ở ngữ cảnh này cách viết không thống nhất → không gợi ý (kể cả khi ô chung của routine đủ chắc).</summary>
    public sealed record Pattern(IReadOnlyList<Option> Options, int Count)
    {
        public string Text => Options.Count > 0 ? Options[0].Text : "";
        /// <summary>Độ tin của cách đứng đầu khi mọi cách đều hợp lệ = số lần / (tổng + 1) — ít mẫu thì tự hạ.</summary>
        public double Share => Options.Count > 0 ? Math.Round(Options[0].N / (double)(Count + 1), 3) : 0;
        /// <summary>Số lần của các cách hiếm không lưu.</summary>
        public int Tail => Math.Max(0, Count - Options.Sum(o => o.N));
    }

    /// <summary>Chất lượng 1 loại gợi ý đo trên store KHÔNG dùng để rút: ngưỡng độ tin đã chọn, độ chính xác, tỷ lệ chỗ có gợi ý, số chỗ đã đo.</summary>
    public sealed record KindQuality(double MinShare, double Precision, double Coverage, int Tested);

    /// <summary>Ma trận kiến thức ghost text: loại (call / func / filter) × khoá (routine hoặc "routine|ngữ cảnh", biến lọc) → <see cref="Pattern"/>,
    /// kèm ngưỡng riêng từng loại đã hiệu chỉnh để độ chính xác đạt mục tiêu (≥ 90%). Có thể chồng lớp: tra bảng này trước rồi tới <see cref="Fallback"/>.</summary>
    public sealed class KnowledgeBase
    {
        /// <summary>EXEC routine → tham số (phần sau tên). Khoá "routine" hoặc "routine|ngữ cảnh".</summary>
        public Dictionary<string, Pattern> Calls { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>hàm( → tham số + ")". Khoá "hàm" hoặc "hàm|ngữ cảnh".</summary>
        public Dictionary<string, Pattern> Funcs { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>IF @biến &lt;&gt; '' → phần "SET @Key = …" phía sau ("{a}" = alias bảng).</summary>
        public Dictionary<string, Pattern> Filters { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Ma trận đầu dòng: khoá = vài token đầu dòng (alias → {1}, {2}…) → phần còn lại của dòng.</summary>
        public Dictionary<string, Pattern> Lines { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>DECLARE @biến → kiểu hay dùng nhất của biến tên đó (khoá = tên biến không @).</summary>
        public Dictionary<string, Pattern> Decls { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Câu tiếp theo: khoá = đầu dòng code phía trên (như ma trận đầu dòng), ô "khoá|đầu dòng trên nữa" khi 2 dòng cho biết rõ hơn → cả dòng kế.</summary>
        public Dictionary<string, Pattern> Nexts { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>"call" / "func" / "filter" → ngưỡng + độ chính xác đo được. Thiếu = dùng <see cref="DefaultMinShare"/>.</summary>
        public Dictionary<string, KindQuality> Quality { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Độ chính xác của các luật cố định (song ngữ, REPLACE…) đo cùng lúc — chỉ để báo cáo.</summary>
        public Dictionary<string, KindQuality> Rules { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string Source { get; init; } = "";
        public KnowledgeBase? Fallback { get; init; }

        public Dictionary<string, Pattern> Table(string kind) => kind switch { "call" => Calls, "func" => Funcs, "filter" => Filters, "line" => Lines, "decl" => Decls, "next" => Nexts, _ => NoEntries };
        private static readonly Dictionary<string, Pattern> NoEntries = new();
        public double MinShareOf(string kind) => QualityOf(kind)?.MinShare ?? DefaultMinShare;

        /// <summary>Chất lượng đã hiệu chỉnh của loại <paramref name="kind"/>: lớp này trước rồi lớp dưới; null = chưa hiệu chỉnh.</summary>
        public KindQuality? QualityOf(string kind) => Quality.TryGetValue(kind, out var q) ? q : Fallback?.QualityOf(kind);

        /// <summary>Tra ô (khoá|ngữ cảnh) rồi ô (khoá), lớp này trước rồi lớp dưới.</summary>
        public (Pattern Pattern, double MinShare)? Find(string kind, string key, string ctx)
        {
            for (var k = this; k is not null; k = k.Fallback)
            {
                var d = k.Table(kind);
                if ((ctx.Length > 0 && d.TryGetValue(key + "|" + ctx, out var p)) || d.TryGetValue(key, out p)) return (p, k.MinShareOf(kind));
            }
            return null;
        }

        public bool Has(string kind) { for (var k = this; k is not null; k = k.Fallback) if (k.Table(kind).Count > 0) return true; return false; }
    }

    /// <summary>Cách đứng đầu chưa đủ chắc thì gợi ý phần đầu chắc chắn của nó (xem Choose).</summary>
    public static bool PartialSuggestions { get; set; } = true;

    /// <summary>Ngưỡng độ tin khi ma trận không ghi ngưỡng cho loại đó.</summary>
    public static double DefaultMinShare { get; set; } = 0.8;

    /// <summary>Chép lời gọi cùng routine đã có trong script khi nó lặp lại y hệt ít nhất ngần này lần (0 = không chép). Đo trên code thật: chép làm giảm độ chính xác → mặc định tắt.</summary>
    public static int DocLocalRepeats { get; set; }

    /// <summary>Biến lọc / routine chưa có trong kiến thức: vẫn đoán theo mẫu (FilterFields, header.xml, SqlHintCatalog.Idioms)? Đo trên code thật: đoán kém → mặc định không.</summary>
    public static bool GuessWhenUnknown { get; set; }

    private static KnowledgeBase? _knowledge;
    /// <summary>Ma trận đi kèm Bcode (rút sẵn từ procedure Fast thật).</summary>
    public static KnowledgeBase Knowledge => _knowledge ??= LoadKnowledgeFile(Path.Combine(AppContext.BaseDirectory, "Templates", "FastGhostKnowledge.json"));

    /// <summary>Thay ma trận đi kèm (vd để đo / thử bộ khác).</summary>
    public static void UseKnowledge(KnowledgeBase kb) { _knowledge = kb; _shared = null; Learned.Clear(); }

    /// <summary>Ma trận tự học từ từng database (nút "Học gợi ý Fast từ database này").</summary>
    public static string LearnedPath(string dbKey) =>
        Path.Combine(BcodePaths.AppData, "Bcode", "ghost", string.Concat(dbKey.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '|' ? '_' : c)) + ".json");

    private static readonly ConcurrentDictionary<string, (DateTime Stamp, KnowledgeBase Kb)> Learned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ma trận chung của mọi dự án đã học trên máy này (+ xác nhận ✔/✘ ở Hỏi Fast) — gộp bằng <see cref="FastGhostMiner.MergeShared"/>.</summary>
    public static string SharedPath => Path.Combine(BcodePaths.AppData, "Bcode", "ghost", "_shared.json");

    private static (DateTime Stamp, KnowledgeBase Kb)? _shared;

    /// <summary>Lớp chung (nếu có) đặt trên ma trận đi kèm.</summary>
    private static KnowledgeBase SharedKnowledge()
    {
        var stamp = File.Exists(SharedPath) ? File.GetLastWriteTimeUtc(SharedPath) : DateTime.MinValue;
        if (stamp == DateTime.MinValue) return Knowledge;
        if (_shared is { } s && s.Stamp == stamp) return s.Kb;
        var kb = LoadKnowledgeFile(SharedPath, Knowledge);
        _shared = (stamp, kb);
        Learned.Clear();   // lớp chung đổi → các ma trận dự án phải nối lại lên lớp mới
        return kb;
    }

    /// <summary>Ma trận dùng cho database <paramref name="dbKey"/>: tra lần lượt ma trận học từ chính database đó → ma trận chung các dự án đã học
    /// → ma trận đi kèm Bcode.</summary>
    public static KnowledgeBase KnowledgeFor(string dbKey)
    {
        var shared = SharedKnowledge();
        var path = LearnedPath(dbKey);
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        KnowledgeBase kb;
        if (stamp == DateTime.MinValue) kb = shared;
        else if (Learned.TryGetValue(dbKey, out var c) && c.Stamp == stamp) kb = c.Kb;
        else Learned[dbKey] = (stamp, kb = LoadKnowledgeFile(path, shared));
        // lớp sửa của người dùng (✘ rồi gõ cách đúng ở Hỏi Fast) nằm trên cùng: dự án nào cũng dùng cách đã sửa trước
        return UserFixes() is { } fx
            ? new KnowledgeBase { Calls = fx.Calls, Funcs = fx.Funcs, Filters = fx.Filters, Decls = fx.Decls, Lines = fx.Lines, Nexts = fx.Nexts, Source = fx.Source, Fallback = kb }
            : kb;
    }

    private static (DateTime Stamp, KnowledgeBase? Kb)? _fixes;

    /// <summary>Các ô người dùng đã sửa (đọc lại khi file xác nhận đổi); null = chưa sửa gì.</summary>
    private static KnowledgeBase? UserFixes()
    {
        var file = FastAskFeedback.FilePath;
        var stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
        if (_fixes is { } f && f.Stamp == stamp) return f.Kb;
        KnowledgeBase? kb = null;
        var prefer = stamp == DateTime.MinValue ? new List<FastGhostMiner.Adjustment>()
            : (FastGhostMiner.AdjustmentsSource?.Invoke() ?? Array.Empty<FastGhostMiner.Adjustment>()).Where(a => a.Prefer).ToList();
        if (prefer.Count > 0)
        {
            var tables = new[] { "call", "func", "filter", "decl", "line", "next" }.ToDictionary(k => k, _ => new Dictionary<string, Pattern>(StringComparer.OrdinalIgnoreCase));
            foreach (var a in prefer.Where(a => tables.ContainsKey(a.Kind)))   // sửa sau ghi đè sửa trước
                tables[a.Kind][a.Key] = new Pattern(new List<Option> { new(a.Text, 1000) }, 1001);
            kb = new KnowledgeBase
            {
                Calls = tables["call"], Funcs = tables["func"], Filters = tables["filter"], Decls = tables["decl"], Lines = tables["line"], Nexts = tables["next"],
                Source = "Cách viết người dùng đã sửa ở Hỏi Fast",
            };
        }
        _fixes = (stamp, kb);
        return kb;
    }

    public static KnowledgeBase LoadKnowledgeFile(string path, KnowledgeBase? fallback = null)
    {
        try
        {
            if (!File.Exists(path)) return new KnowledgeBase { Fallback = fallback };
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var calls = new Dictionary<string, Pattern>(StringComparer.OrdinalIgnoreCase);
            var funcs = new Dictionary<string, Pattern>(StringComparer.OrdinalIgnoreCase);
            var filters = new Dictionary<string, Pattern>(StringComparer.OrdinalIgnoreCase);
            var lines = new Dictionary<string, Pattern>(StringComparer.OrdinalIgnoreCase);
            var decls = new Dictionary<string, Pattern>(StringComparer.OrdinalIgnoreCase);
            var nexts = new Dictionary<string, Pattern>(StringComparer.OrdinalIgnoreCase);
            var quality = new Dictionary<string, KindQuality>(StringComparer.OrdinalIgnoreCase);
            var rules = new Dictionary<string, KindQuality>(StringComparer.OrdinalIgnoreCase);
            KindQuality ReadQ(System.Text.Json.JsonElement e) => new(
                e.TryGetProperty("minShare", out var m) ? m.GetDouble() : DefaultMinShare,
                e.TryGetProperty("precision", out var p) ? p.GetDouble() : 0,
                e.TryGetProperty("coverage", out var cv) ? cv.GetDouble() : 0,
                e.TryGetProperty("tested", out var t) ? t.GetInt32() : 0);
            if (root.TryGetProperty("kinds", out var kinds))
                foreach (var k in kinds.EnumerateObject())
                {
                    quality[k.Name] = ReadQ(k.Value);
                    var d = k.Name switch { "call" => calls, "func" => funcs, "filter" => filters, "line" => lines, "decl" => decls, "next" => nexts, _ => null };
                    if (d is null) continue;
                    // entries: { khoá: [tổng số lần, [cách viết, số lần], [cách viết, số lần] …] }
                    if (k.Value.TryGetProperty("entries", out var es))
                        foreach (var p in es.EnumerateObject())
                            d[p.Name] = new Pattern(p.Value.EnumerateArray().Skip(1).Select(o => new Option(o[0].GetString() ?? "", o[1].GetInt32())).ToList(), p.Value[0].GetInt32());
                }
            if (root.TryGetProperty("rules", out var rs))
                foreach (var r in rs.EnumerateObject()) rules[r.Name] = ReadQ(r.Value);
            return new KnowledgeBase
            {
                Calls = calls, Funcs = funcs, Filters = filters, Lines = lines, Decls = decls, Nexts = nexts, Quality = quality, Rules = rules, Fallback = fallback,
                Source = root.TryGetProperty("source", out var s) ? s.GetString() ?? "" : "",
            };
        }
        catch { return new KnowledgeBase { Fallback = fallback }; }
    }

    // ---------------------------------------------------------------- kiểu cột hay dùng nhất theo tên (đọc 1 lần / database)

    private static readonly ConcurrentDictionary<string, Task<IReadOnlyDictionary<string, string>>> TypeCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Kiểu hay dùng nhất của từng tên cột trong database (bỏ bảng theo kỳ có "$"); đã nạp thì trả ngay, chưa thì nạp ở nền và trả null.</summary>
    public static IReadOnlyDictionary<string, string>? LoadColumnTypes(string key, Func<SqlConnection> connect)
    {
        var task = TypeCache.GetOrAdd(key, _ => Task.Run(async () =>
        {
            var d = new Dictionary<string, (string Type, int N)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                await using var conn = connect();
                await conn.OpenAsync();
                const string sql = """
                    SELECT c.name, ty.name AS t, c.max_length, c.precision, c.scale, COUNT(*) AS n
                    FROM sys.columns c
                    JOIN sys.tables tb ON tb.object_id = c.object_id
                    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                    WHERE tb.is_ms_shipped = 0 AND tb.name NOT LIKE '%$%'
                    GROUP BY c.name, ty.name, c.max_length, c.precision, c.scale
                    """;
                await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var name = r.GetString(0);
                    var t = r.GetString(1).ToLowerInvariant();
                    int len = r.GetInt16(2), prec = r.GetByte(3), scale = r.GetByte(4), n = r.GetInt32(5);
                    var type = t switch
                    {
                        "varchar" or "char" or "varbinary" or "binary" => $"{t}({(len < 0 ? "max" : len.ToString())})",
                        "nvarchar" or "nchar" => $"{t}({(len < 0 ? "max" : (len / 2).ToString())})",
                        "numeric" or "decimal" => $"{t}({prec},{scale})",
                        _ => t,
                    };
                    if (!d.TryGetValue(name, out var old) || n > old.N) d[name] = (type, n);
                }
            }
            catch { /* không đọc được → dùng quy ước đặt tên */ }
            return (IReadOnlyDictionary<string, string>)d.ToDictionary(x => x.Key, x => x.Value.Type, StringComparer.OrdinalIgnoreCase);
        }));
        return task.IsCompletedSuccessfully ? task.Result : null;
    }
}
