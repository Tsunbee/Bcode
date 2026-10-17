using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// "AI tạo danh mục" khi CHƯA có API key: gợi ý spec Tạo nhanh danh mục theo luật — không gọi AI, không tốn token.
/// Đọc câu kiểu "Thêm danh mục &lt;tên&gt;: Mã, Tên, Khách hàng, Ngày hiệu lực, Ghi chú, Trạng thái" rồi áp quy ước FastBusiness:
/// bảng mới = tiền tố (mặc định zdm, AppSettings.NewCatalogTablePrefix) + chữ đầu các từ (hình thức bán hàng → zdmhtbh, chi tiết zdmhtbhct), ma_/ten_/ten_2, ghi_chu, status, nh_xx1.., cột hệ thống datetime0/2, user_id0/2;
/// cột khác tra ngược theo tiêu đề: lookup thường dùng (Khách hàng → ma_kh/Customer...), HeaderDictionary.json, header.xml (FieldDictionaryService);
/// bảng dm.. đã có trong App Data thì dùng cột thật; danh mục cùng tên đã có trong source (Controllers\Dir) thì hỏi lại.
/// Trả về JSON CÙNG DẠNG câu trả lời AI (loai, tom_tat, cau_hoi, ghi_chu, spec) để <see cref="AiCatalogService"/> chuẩn hoá + hiện chung một giao diện.
/// </summary>
public static class FastCatalogRules
{
    public const string Engine = "Luật Fast (không AI)";

    // Tiền tố câu hỏi — để nhận lại câu trả lời ở lượt sau (trang gửi lại đúng câu hỏi).
    private const string QExisting = "Source đã có danh mục";
    private const string QColumns = "Danh mục cần những cột nào";
    private const string QDetail = "Lưới chi tiết gồm những cột nào";
    private const string QField = "Cột “";
    private const string QKeys = "Khoá của màn";
    private const string QMonthly = "Các chỉ tiêu theo tháng";
    /// <summary>Đánh dấu (trong mô tả cột) cụm "theo 12 tháng / mỗi tháng" — tách 12 cột hoặc lưới theo tháng.</summary>
    private const string MonthlyTag = "⟨12 tháng⟩";

    /// <summary>Lookup thường dùng của FBO (khoá đã bỏ dấu) → (field, controller). Ưu tiên trước header.xml vì header.xml có nhiều biến thể.</summary>
    private static readonly (string[] Words, string Field, string Controller, string V, string E)[] CommonLookups =
    {
        (new[] { "khach hang", "khach" }, "ma_kh", "Customer", "Khách hàng", "Customer"),
        (new[] { "vat tu", "hang hoa", "san pham", "mat hang" }, "ma_vt", "Item", "Vật tư", "Item"),
        (new[] { "kho", "kho hang" }, "ma_kho", "Site", "Kho", "Site"),
        (new[] { "ngoai te", "loai tien", "tien te" }, "ma_nt", "Currency", "Ngoại tệ", "Currency"),
        (new[] { "don vi tinh", "dvt" }, "dvt", "UOM", "Đơn vị tính", "UOM"),
        (new[] { "vu viec", "du an", "cong trinh" }, "ma_vv", "Job", "Vụ việc", "Job"),
        (new[] { "tai khoan", "tk" }, "tk", "Account", "Tài khoản", "Account"),
        (new[] { "bo phan", "phong ban" }, "ma_bp", "Department", "Bộ phận", "Department"),
        (new[] { "don vi", "don vi co so", "chi nhanh", "dvcs" }, "ma_dvcs", "Unit", "Đơn vị", "Unit"),
        (new[] { "nhan vien ban hang", "nvbh", "nhan vien kinh doanh" }, "ma_nvbh", "SalesEmployee", "Nhân viên bán hàng", "Sales employee"),
    };

    // "Thêm (mới) (1) danh mục X" / "Tạo danh mục: “Danh mục X”" — ưu tiên hơn CatalogRx: y/c thật hay trộn nhiều việc (thêm trường vào chứng từ, báo cáo…).
    private static readonly Regex CreateRx = new(@"(?i)\b(th[êe]m|t[ạa]o|b[ổo] sung|x[âa]y d[ựu]ng|khai b[áa]o|l[àa]m)\s+(m[ớo]i\s+)?((1|m[ộo]t)\s+)?danh\s*m[ụu]c\s*(m[ớo]i\s*)?:?\s*[“""']?\s*(?<name>[^:\n\r,.;()“”""]+)", RegexOptions.Compiled);
    private static readonly Regex CatalogRx = new(@"(?i)\b(danh\s*m[ụu]c|khai\s*b[áa]o)\s+(m[ớo]i\s+)?(?<name>[^:\n\r,.;()“”""]+)", RegexOptions.Compiled);
    private static readonly Regex ModifyRx = new(@"(?i)\b(s[ửu]a|ch[ỉi]nh|đi[ềe]u ch[ỉi]nh|b[ổo] sung|th[êe]m)\s+(c[ộo]t|tr[ườu][ờo]ng|field|th[ôo]ng tin|ch[ứu]c n[ăa]ng|n[úu]t)\b|\b(v[àa]o|trong|[ởo]|c[ủu]a)\s+danh\s*m[ụu]c", RegexOptions.Compiled);
    private static readonly Regex GroupRx = new(@"(?i)\b(theo|c[óo])\s+(?<g>nh[óo]m\s+[\p{L}\d]+(\s+[\p{L}\d]+)?)", RegexOptions.Compiled);
    private static readonly Regex ImportRx = new(@"(?i)\bimport\b|\bupload\b|nh[ậa]p\s+((t[ừu]|b[ằa]ng|qua)\s+)?((file|t[ệe]p)\s+)?excel|(t[ừu]|b[ằa]ng)\s+((file|t[ệe]p)\s+)?excel", RegexOptions.Compiled);
    private static readonly Regex FilterRx = new(@"(?i)(m[àa]n h[ìi]nh|form|đi[ềe]u ki[ệe]n)\s+l[ọo]c", RegexOptions.Compiled);
    // "Mỗi kênh gắn với 1 hoặc nhiều đơn vị" (không bắt "cho chọn 1 hoặc nhiều" của điều kiện lọc).
    private static readonly Regex ManyRx = new(@"(?i)\b(g[ắa]n|thu[ộo]c|c[óo]|g[ồo]m|[áa]p d[ụu]ng)\s+(v[ớo]i\s+|cho\s+)?(1|m[ộo]t)\s+ho[ặa]c\s+nhi[ềe]u\s+(?<y>[^,.;:\n\r()]+)", RegexOptions.Compiled);
    private static readonly Regex DetailRx = new(@"(?i)l[ưu][ớo]i\s+(nh[ậa]p|chi\s+ti[ếe]t)|(b[ảa]ng|ph[ầa]n)\s+chi\s+ti[ếe]t|chi\s+ti[ếe]t\s+(g[ồo]m|c[óo]\s+c[áa]c)|\bdetail\b|nhi[ềe]u\s+d[òo]ng", RegexOptions.Compiled);

    // Tên màn ghi rõ: 'Thiết kế menu mới "Cập nhật dự toán"', 'tạo màn hình “…”', 'menu mới: Cập nhật dự toán'.
    private static readonly Regex ScreenQuotedRx = new(@"(?i)\b(thi[ếe]t\s+k[ếe]|t[ạa]o|th[êe]m|x[âa]y\s+d[ựu]ng|l[àa]m|b[ổo]\s+sung)\s+(m[ớo]i\s+)?((1|m[ộo]t)\s+)?(menu|m[àa]n\s*h[ìi]nh|form|ch[ứu]c\s+n[ăa]ng)(\s+m[ớo]i)?(\s+t[êe]n)?\s*:?\s*[“""'](?<name>[^“”""'\n\r]{2,80})[”""']", RegexOptions.Compiled);
    private static readonly Regex ScreenNewRx = new(@"(?i)\b(menu|m[àa]n\s*h[ìi]nh)\s+m[ớo]i\s*:?\s*(?<name>[^:\n\r,.;()“”""]+)", RegexOptions.Compiled);
    // "Lookup từ Danh mục X" / "chọn từ danh mục X" mô tả 1 CỘT tra cứu — không phải màn cần tạo.
    private static readonly Regex LookupPhraseRx = new(@"(?i)\b(lookup|ch[ọo]n|tra|l[ấa]y)\s+(t[ừu]|theo|trong)\s+(danh\s*m[ụu]c|dm)[^\n\r,.;:]*", RegexOptions.Compiled);
    private static readonly Regex MonthlyRx = new(@"(?i)\s*\b(theo|trong|cho)\s+(c[áa]c\s+)?12\s+th[áa]ng(\s+(trong|c[ủu]a)\s+(1\s+)?n[ăa]m)?|\s*\b(m[ỗo]i|h[àa]ng|t[ừu]ng)\s+th[áa]ng", RegexOptions.Compiled);

    /// <param name="Required">true = "bắt buộc nhập" (allowNulls false), false = "không bắt buộc", null = không nói.</param>
    private sealed record Col(string Name, string SqlType, string V, string E, string Controller, string Note, bool? Required = null);

    public static async Task<string> SuggestAsync(string content, IReadOnlyList<(string Question, string Answer)> answers,
        IReadOnlyCollection<string> tables, Func<string, Task<List<AiTableColumn>>> getColumns, string sourcePath, string tablePrefix = "zdm")
    {
        tablePrefix = string.IsNullOrWhiteSpace(tablePrefix) ? "zdm" : tablePrefix.Trim().ToLowerInvariant();
        var tableSet = tables as HashSet<string> ?? new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        string? Answer(string prefix) => answers.Where(a => a.Question.StartsWith(prefix, StringComparison.Ordinal)).Select(a => a.Answer.Trim()).LastOrDefault();
        var allText = content + "\n" + string.Join("\n", answers.Select(a => a.Answer));
        var notes = new List<string> { "Chưa có API key nên Bcode gợi ý theo quy ước đặt tên FastBusiness + từ điển field (header.xml) — không gọi AI. Kiểm tra lại tên bảng, cột, kiểu dữ liệu." };
        var questions = new List<string>();

        // Dòng "Menu: …" (Yêu cầu FSG gửi kèm tên menu) chỉ là ngữ cảnh — tên menu hay là "Danh mục …" có sẵn, không phải danh mục cần tạo.
        content = Regex.Replace(content, @"^Menu:[^\n]*\n", "");
        // "Lookup từ Danh mục hàng hoá vật tư" là mô tả 1 cột tra cứu → che đi (giữ nguyên vị trí ký tự) khi tìm tên màn cần tạo.
        var scan = LookupPhraseRx.Replace(content, x => new string(' ', x.Length));
        // Bỏ cụm không phải tên ("thêm mới danh mục này", "danh mục như yêu cầu"…) — lấy cụm kế tiếp.
        static bool GoodName(Match x) => x.Success && CleanName(x.Groups["name"].Value) is { Length: > 1 } n && char.IsLetter(n[0])
            && !Regex.IsMatch(Fold(n), @"^(nay|nhu|tren|do|kia|khac|tuong ung|chuan|co san|hien co|duoi|sau|ben)\b");
        // Tên màn ghi rõ (menu mới "X") ưu tiên nhất; không phải "Danh mục …" thì là màn khai báo / cập nhật số liệu (không có cặp mã - tên).
        var screen = ScreenQuotedRx.Matches(scan).FirstOrDefault(GoodName) ?? ScreenNewRx.Matches(scan).FirstOrDefault(GoodName);
        var create = screen is null ? CreateRx.Matches(scan).FirstOrDefault(GoodName) : null;
        var isCatalog = true;
        Match m;
        if (screen is not null)
        {
            m = screen;
            isCatalog = Regex.IsMatch(Fold(m.Groups["name"].Value.Trim('“', '”', '"', '\'', ' ')), @"^(danh muc|dm)\b");
        }
        else if (create is not null)
        {
            m = create;
            if (ModifyRx.IsMatch(scan))
                notes.Add("Y/c còn việc khác ngoài tạo danh mục (thêm trường vào màn hình khác, báo cáo…) — phần đó làm tay; ở đây chỉ gợi ý màn danh mục.");
        }
        else
        {
            // Có "thêm mới danh mục này" (tên đứng chỗ khác, vd "Danh mục X" ở dòng trên) thì vẫn là tạo mới.
            var createIntent = CreateRx.IsMatch(scan);
            m = CatalogRx.Matches(scan).FirstOrDefault(GoodName) ?? Match.Empty;
            if (!m.Success || (!createIntent && ModifyRx.IsMatch(scan)))
                return Reply("khac", m.Success
                    ? "Yêu cầu có vẻ là sửa / bổ sung cho màn hình có sẵn, không phải tạo danh mục mới."
                    : "Không thấy cụm “danh mục …” / “khai báo …” / menu mới “…” trong yêu cầu — bộ luật chỉ nhận yêu cầu tạo màn danh mục / khai báo mới.", notes, questions, null);
        }

        // ---- tên màn + mã viết tắt ----
        var name = CleanName(m.Groups["name"].Value);
        var folded = Fold(name);
        var explicitTable = ExplicitTable(scan, tablePrefix);
        string code, table;
        if (isCatalog)
        {
            // Y/c ghi rõ tên bảng (zdmxxx / dmxxx) thì theo đó, không thì tiền tố (mặc định zdm — dễ phân biệt với bảng dm* chuẩn) + chữ đầu các từ.
            code = explicitTable is { } et ? Regex.Replace(et, "^(" + Regex.Escape(tablePrefix) + "|z?dm)", "") : Abbrev(folded);
            if (code.Length == 0) code = Abbrev(folded);
            table = explicitTable ?? tablePrefix + code;
        }
        else
        {
            // Màn khai báo số liệu: tiền tố bỏ "dm" (zdm → z) + tên menu bỏ động từ: "Cập nhật dự toán" → dự toán → zdutoan (chữ đầu quá ngắn thì ghép cả từ).
            var core = Regex.Replace(folded, @"^(cap nhat|khai bao|nhap|theo doi|quan ly|thiet lap|thiet ke)\s+", "");
            code = Abbrev(core);
            if (code.Length < 3) code = Regex.Replace(core, "[^a-z0-9]", "");
            if (code.Length > 16) code = code[..16];
            table = DataPrefix(tablePrefix) + code;
        }
        var titleV = isCatalog ? "Danh mục " + name : Cap(name);
        var titleE = new[] { titleV, Cap(name), name }.Select(t => HeaderTranslator.Instance.Translate(t)).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)) ?? "";
        notes.Add(titleE.Length == 0 ? "Chưa dịch được tiêu đề tiếng Anh — bấm Dịch cạnh ô Title (e) hoặc tự nhập."
            : $"Tiêu đề tiếng Anh dịch tự động theo từ điển (“{titleE}”) — kiểm tra lại.");

        // Danh mục cùng tên đã có trong source → hỏi (có thể là y/c sửa danh mục có sẵn).
        if (isCatalog && FindExistingCatalog(sourcePath, folded) is { } found)
        {
            var ans = Answer(QExisting);
            if (ans is not null && IsNo(ans))
                return Reply("khac", $"Danh mục “{name}” đã có ({found.Controller} / bảng {found.Table}) — y/c là sửa danh mục có sẵn.", notes, questions, null);
            if (ans is null)
                questions.Add($"{QExisting} “{found.Title}” (controller {found.Controller}, bảng {found.Table}). Vẫn tạo màn mới? (trả lời “không” nếu y/c là sửa danh mục này)");
            if (tableSet.Contains(found.Table)) table = found.Table;
        }

        var exists = tableSet.Contains(table);
        var spec = new JsonObject
        {
            ["table"] = table, ["controller"] = table, ["main"] = table.ToLowerInvariant(),
            ["titleV"] = titleV, ["titleE"] = titleE, ["subV"] = "", ["subE"] = "",
            // Màn khai báo số liệu không có cột tên → không tạo file Lookup.
            ["funcNew"] = true, ["funcEdit"] = true, ["funcDelete"] = true, ["funcFilter"] = true, ["lookup"] = isCatalog,
            ["filter"] = FilterRx.IsMatch(allText), ["import"] = ImportRx.IsMatch(allText), ["import2262"] = true,
            ["irregular"] = false, ["comment"] = false,
        };
        var mapped = new List<string>();

        if (exists)
        {
            // Bảng có sẵn: dùng đủ cột thật (AiCatalogService sẽ đối chiếu lại kiểu).
            var real = await getColumns(table);
            var keys = real.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            if (keys.Count == 0 && real.Count > 0) keys.Add(real[0].Name);
            var nameField = real.Select(c => c.Name).FirstOrDefault(n => n.StartsWith("ten_", StringComparison.OrdinalIgnoreCase) && !n.EndsWith("2")) ?? "";
            var guesser = new ColumnHeaderGuesser();
            var ctx = new ColumnHeaderGuesser.Context(table, keys, titleV, titleE);
            spec["keys"] = string.Join(", ", keys);
            spec["order"] = spec["keys"]!.GetValue<string>();
            spec["nameField"] = nameField;
            spec["columns"] = new JsonArray(real.Select(c =>
            {
                var h = FieldDictionaryService.Instance.Header(c.Name) ?? guesser.Guess(c.Name, ctx) ?? (c.Name, c.Name);
                var lk = FieldDictionaryService.Instance.Lookups(c.Name).FirstOrDefault();
                return (JsonNode)ColumnJson(new Col(c.Name, c.SqlType, h.V, h.E, keys.Contains(c.Name) || c.Name == nameField ? "" : lk?.Controller ?? "", ""),
                    keys.Contains(c.Name, StringComparer.OrdinalIgnoreCase), c.Name.Equals(nameField, StringComparison.OrdinalIgnoreCase));
            }).ToArray());
            notes.Add($"Bảng {table} đã có trong App Data — dùng đủ {real.Count} cột thật; nếu đây là danh mục khác thì đổi tên bảng.");
        }
        else if (!isCatalog)
        {
            await DataScreenAsync(spec, content, m.Index + m.Length, name, code, folded, table, answers, tableSet, getColumns, sourcePath, notes, questions, mapped);
        }
        else
        {
            // Bảng mới: cột theo danh sách trong y/c (hoặc câu trả lời), luôn có mã + tên.
            var phrases = ColumnPhrases(content, m.Index + m.Length);
            var listed = phrases.Count > 0;
            // Trả lời câu "cần những cột nào ngoài Mã / Tên / Ghi chú / Trạng thái" → thêm vào (vẫn giữ nhóm + Ghi chú / Trạng thái bên dưới).
            if (!listed && Answer(QColumns) is { Length: > 0 } colAns && !IsNo(colAns)) phrases = SplitPhrases(colAns);
            // Không liệt kê cột: vẫn nhận "chia theo nhóm kênh" → cột nhóm.
            if (!listed)
                foreach (Match g in GroupRx.Matches(content))
                    phrases.Add((Regex.Replace(g.Groups["g"].Value, @"(?i)\s+(cho|v[àa]|đ[ểe]|c[óo])$", "").Trim(), ""));
            if (!listed && Answer(QColumns) is null)
                questions.Add($"{QColumns} ngoài Mã / Tên / Ghi chú / Trạng thái? (ghi cách nhau bằng dấu phẩy, vd: Khách hàng, Ngày hiệu lực, Số tiền)");

            var cols = new List<Col>();
            var groupNo = 0;
            var guessed = new List<string>();
            void Add(Col c) { if (!cols.Any(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))) cols.Add(c); }
            Add(new Col("ma_" + code, "varchar(32)", "Mã " + name, "Code", "", ""));
            Add(new Col("ten_" + code, "nvarchar(256)", "Tên " + name, "Name", "", ""));
            Add(new Col("ten_" + code + "2", "nvarchar(256)", "Tên 2", "Name 2", "", ""));
            foreach (var (p, desc) in listed ? phrases : phrases.Concat(new[] { ("Ghi chú", ""), ("Trạng thái", "") }))
            {
                var c = ApplyDesc(WithLookupHint(MapPhrase(p, code, folded, ref groupNo), desc, sourcePath), desc);
                var ans = Answer(QField + p + "”");
                if (ans is { Length: > 0 } && Regex.Match(ans, @"^(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*(?<t>[a-z]+(\(\s*\d+(\s*,\s*\d+)?\s*\))?)?", RegexOptions.IgnoreCase) is { Success: true } am)
                    c = c with { Name = am.Groups["n"].Value.ToLowerInvariant(), SqlType = am.Groups["t"].Success ? am.Groups["t"].Value : c.SqlType, Note = "" };
                else if (c.Note == "?" && ans is null)
                {
                    // Tối đa 5 câu hỏi tên field; còn lại liệt kê ở ghi chú để sửa thẳng trong bảng cột.
                    if (questions.Count(q => q.StartsWith(QField, StringComparison.Ordinal)) < 5)
                        questions.Add($"{QField}{p}”: tên field? (đang đoán {c.Name} {c.SqlType} — trả lời vd “so_dt varchar(32)”, để trống = giữ)");
                    else guessed.Add($"{p} → {c.Name}");
                }
                if (c.Note.Length > 1) mapped.Add(c.Note);
                Add(await LookupTypeAsync(c with { Note = "" }, tableSet, getColumns));
            }
            if (guessed.Count > 0) notes.Add("Cột đoán tên (sửa thẳng trong bảng cột nếu sai): " + string.Join("; ", guessed) + ".");
            // Cột chuẩn của danh mục FBO (status + 4 cột hệ thống) luôn có, kể cả khi y/c không ghi.
            Add(new Col("status", "char(1)", "Trạng thái", "Status", "", ""));
            Add(new Col("datetime0", "datetime", "Ngày tạo", "Created date", "", ""));
            Add(new Col("datetime2", "datetime", "Ngày sửa", "Modified date", "", ""));
            Add(new Col("user_id0", "int", "Người tạo", "Created by", "", ""));
            Add(new Col("user_id2", "int", "Người sửa", "Modified by", "", ""));

            spec["keys"] = "ma_" + code; spec["order"] = "ma_" + code; spec["nameField"] = "ten_" + code;
            spec["columns"] = new JsonArray(cols.Select(c => (JsonNode)ColumnJson(c, c.Name == "ma_" + code, c.Name == "ten_" + code)).ToArray());
            notes.Add($"Đề xuất bảng mới {table} (mã ma_{code}, tên ten_{code}) theo chữ đầu của “{name}” — đổi lại nếu dự án đặt tên khác.");
            if (mapped.Count > 0) notes.Add("Cột tra theo từ điển: " + string.Join("; ", mapped) + ".");
        }

        // ---- lưới chi tiết ----
        var many = ManyRx.Match(allText);
        var detailPhrases = many.Success ? SplitPhrases(many.Groups["y"].Value) : new List<(string P, string D)>();
        if (Answer(QDetail) is { Length: > 0 } detAns && !IsNo(detAns)) detailPhrases = SplitPhrases(detAns);
        var wantDetail = isCatalog && (many.Success || DetailRx.IsMatch(content) || detailPhrases.Count > 0) && !(Answer(QDetail) is { } dn && IsNo(dn));
        if (wantDetail)
        {
            var key = (spec["keys"]?.GetValue<string>() ?? "").Split(',')[0].Trim();
            var detailTable = table + "ct";   // zdmkbh → zdmkbhct: cùng tiền tố với bảng chính
            var dcols = new List<Col> { new(key, "varchar(32)", "Mã " + name, "Code", "", ""), new("line_nbr", "int", "Stt", "Line", "", "") };
            var groupNo = 0;
            foreach (var (p, desc) in detailPhrases)
            {
                var c = ApplyDesc(WithLookupHint(MapPhrase(p, code, folded, ref groupNo), desc, sourcePath), desc);
                if (!dcols.Any(x => x.Name == c.Name)) dcols.Add(c with { Note = "" });
            }
            if (detailPhrases.Count == 0 && Answer(QDetail) is null)
                questions.Add($"{QDetail}? (ghi cách nhau bằng dấu phẩy; trả lời “không” nếu không cần lưới chi tiết)");
            dcols.AddRange(new Col[]
            {
                new("datetime0", "datetime", "Ngày tạo", "Created date", "", ""), new("datetime2", "datetime", "Ngày sửa", "Modified date", "", ""),
                new("user_id0", "int", "Người tạo", "Created by", "", ""), new("user_id2", "int", "Người sửa", "Modified by", "", ""),
            });
            var detTitle = detailPhrases.Count > 0 ? Cap(detailPhrases[0].P) : "Chi tiết";
            spec["detail"] = true;
            spec["detailTable"] = detailTable;
            spec["detailId"] = table + "Detail";
            spec["detailTitleV"] = detTitle;
            spec["detailTitleE"] = HeaderTranslator.Instance.Translate(detTitle) ?? "Details";
            spec["detailKeys"] = key + ", line_nbr";
            spec["linkKeys"] = key;
            spec["detailOrder"] = "line_nbr";
            spec["detailImport"] = false;
            spec["detailColumns"] = new JsonArray(dcols.Select(c =>
            {
                var j = ColumnJson(c, c.Name == key || c.Name == "line_nbr", false);
                if (c.Name == key || c.Name == "line_nbr") j["inGrid"] = false;
                return (JsonNode)j;
            }).ToArray());
            if (!tableSet.Contains(detailTable)) notes.Add($"Lưới chi tiết: đề xuất bảng mới {detailTable} (khoá {key}, line_nbr).");
        }
        else spec["detail"] ??= false;   // màn khai báo số liệu có thể đã tự đặt lưới theo tháng

        var ncols = (spec["columns"] as JsonArray)?.Count ?? 0;
        var hasDetail = spec["detail"]?.GetValue<bool>() == true;
        var summary = $"{(isCatalog ? "Danh mục " + name : "Màn “" + titleV + "” (khai báo số liệu, không phải danh mục mã - tên)")}: bảng {table} ({(exists ? "có sẵn" : "mới")}), {ncols} cột, khoá {spec["keys"]}"
            + (hasDetail ? $", lưới chi tiết {spec["detailTable"]}" : "") + (spec["import"]!.GetValue<bool>() ? ", có import Excel" : "") + ".";
        return Reply("danh_muc", summary, notes, questions, spec);
    }

    /// <summary>
    /// Màn khai báo / cập nhật số liệu (vd "Cập nhật dự toán"): không có cặp mã - tên; khoá = các cột chiều xác định 1 dòng (đơn vị, năm, bộ phận,
    /// vật tư, …, lần cập nhật — bỏ cột "không bắt buộc"), hỏi lại để người dùng xác nhận. Chỉ tiêu "theo 12 tháng / mỗi tháng" → 12 cột (ns_dt01..12)
    /// hoặc lưới chi tiết theo tháng nếu người dùng trả lời "lưới". Không thêm status (không phải danh mục).
    /// </summary>
    private static async Task DataScreenAsync(JsonObject spec, string content, int from, string name, string code, string folded, string table,
        IReadOnlyList<(string Question, string Answer)> answers, HashSet<string> tableSet, Func<string, Task<List<AiTableColumn>>> getColumns,
        string sourcePath, List<string> notes, List<string> questions, List<string> mapped)
    {
        string? Answer(string prefix) => answers.Where(a => a.Question.StartsWith(prefix, StringComparison.Ordinal)).Select(a => a.Answer.Trim()).LastOrDefault();
        var phrases = ColumnPhrases(content, from);
        if (phrases.Count == 0 && Answer(QColumns) is { Length: > 0 } colAns && !IsNo(colAns)) phrases = SplitPhrases(colAns);
        if (phrases.Count == 0 && Answer(QColumns) is null)
            questions.Add($"{QColumns}? (ghi cách nhau bằng dấu phẩy, vd: Đơn vị, Năm, Bộ phận, Mã vật tư, Số tiền)");

        var cols = new List<Col>();
        var monthly = new List<Col>();
        var groupNo = 0;
        void Add(Col c) { if (!cols.Any(x => x.Name.Equals(c.Name, StringComparison.OrdinalIgnoreCase))) cols.Add(c); }
        foreach (var (p, d0) in phrases)
        {
            var isMonthly = d0.Contains(MonthlyTag);
            var desc = d0.Replace(MonthlyTag, "").Trim();
            var c = ApplyDesc(WithLookupHint(MapPhrase(p, code, folded, ref groupNo), desc, sourcePath), desc);
            if (Answer(QField + p + "”") is { Length: > 0 } ans
                && Regex.Match(ans, @"^(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*(?<t>[a-z]+(\(\s*\d+(\s*,\s*\d+)?\s*\))?)?", RegexOptions.IgnoreCase) is { Success: true } am)
                c = c with { Name = am.Groups["n"].Value.ToLowerInvariant(), SqlType = am.Groups["t"].Success ? am.Groups["t"].Value : c.SqlType, Note = "" };
            if (isMonthly)
            {
                // Chỉ tiêu theo tháng: luôn là số (tiền / số lượng / đơn giá) — tên ngắn để còn chỗ cho hậu tố 01..12.
                monthly.Add(c with { Name = MonthBase(p), SqlType = "numeric(19,4)", Controller = "", Note = "", V = Cap(p) });
                continue;
            }
            if (c.Note.Length > 1) mapped.Add(c.Note);
            Add(await LookupTypeAsync(c with { Note = "" }, tableSet, getColumns));
        }

        // ---- khoá: cột chiều (có lookup / năm / tháng / kỳ / lần …) không ghi "không bắt buộc" ----
        static bool IsDim(Col c) => c.Controller.Length > 0 || Regex.IsMatch(c.Name, @"^(nam|thang|ky|quy|lan|lan_\w+|so_lan\w*|ngay_hl\w*)$", RegexOptions.IgnoreCase);
        var keys = cols.Where(c => c.Required != false && IsDim(c)).Select(c => c.Name).ToList();
        var keyAns = Answer(QKeys);
        if (keyAns is { Length: > 0 } && !Regex.IsMatch(Fold(keyAns), @"^(dung|ok|co|giu|dong y|u|uh|yes|y)\b"))
        {
            var picked = Regex.Split(keyAns, @"[\s,;]+").Select(x => x.Trim().ToLowerInvariant()).Where(x => cols.Any(c => c.Name.Equals(x, StringComparison.OrdinalIgnoreCase))).ToList();
            if (picked.Count > 0) keys = picked;
            else notes.Add($"Câu trả lời khoá “{keyAns}” không khớp tên cột nào — giữ khoá đề xuất, sửa ở ô Key nếu cần.");
        }
        if (keys.Count == 0 && cols.Count > 0) keys.Add(cols[0].Name);
        if (keyAns is null)
            questions.Add($"{QKeys}: đang đề xuất {string.Join(", ", keys)} (mỗi bộ giá trị này chỉ có 1 dòng). Đúng không? (trả lời danh sách field cách nhau dấu phẩy để đổi; để trống = giữ)");
        cols = cols.Select(c => keys.Contains(c.Name, StringComparer.OrdinalIgnoreCase) ? c with { Required = true } : c).ToList();
        var optionalDims = cols.Where(c => c.Required == false && IsDim(c)).Select(c => c.Name).ToList();
        if (optionalDims.Count > 0) notes.Add($"Không đưa vào khoá vì y/c ghi “không bắt buộc”: {string.Join(", ", optionalDims)}.");

        // ---- chỉ tiêu theo tháng: 12 cột (mặc định) hay lưới chi tiết theo tháng ----
        var monthAns = Answer(QMonthly);
        var monthGrid = monthAns is not null && Regex.IsMatch(Fold(monthAns), @"luoi|chi tiet|dong|grid|detail");
        if (monthly.Count > 0 && monthAns is null)
            questions.Add($"{QMonthly}: tách mỗi chỉ tiêu thành 12 cột (đang làm: {string.Join(", ", monthly.Select(x => x.Name + "01.." + x.Name + "12"))}) hay làm lưới chi tiết theo tháng (mỗi dòng 1 tháng)? (trả lời “lưới” để đổi)");
        var monthCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!monthGrid)
            foreach (var mc in monthly)
                for (var i = 1; i <= 12; i++)
                {
                    var c = mc with { Name = $"{mc.Name}{i:00}", V = $"{mc.V} tháng {i}", E = $"{mc.E} M{i:00}" };
                    monthCols.Add(c.Name);
                    Add(c);
                }
        foreach (var s in SystemCols) Add(s);

        spec["keys"] = string.Join(", ", keys);
        spec["order"] = spec["keys"]!.GetValue<string>();
        spec["nameField"] = "";
        spec["columns"] = new JsonArray(cols.Select(c =>
        {
            var j = ColumnJson(c, keys.Contains(c.Name, StringComparer.OrdinalIgnoreCase), false);
            j["autoComplete"] = c.Controller;   // khoá ở đây là cột chiều tra cứu (đơn vị, vật tư…) — khác khoá mã của danh mục, vẫn cần AutoComplete
            if (monthCols.Contains(c.Name)) j["inGrid"] = false;   // 12 × n cột chỉ hiện ở form Dir, lưới danh sách gọn
            return (JsonNode)j;
        }).ToArray());
        notes.Add($"Đề xuất bảng mới {table} — màn khai báo số liệu nên dùng tiền tố không có “dm” (đổi tiền tố bảng danh mục ở Settings thì phần này đổi theo); đổi tên nếu dự án đặt khác.");
        if (mapped.Count > 0) notes.Add("Cột tra theo từ điển: " + string.Join("; ", mapped) + ".");
        if (monthly.Count > 0 && !monthGrid)
            notes.Add($"Chỉ tiêu theo tháng tách 12 cột: {string.Join(", ", monthly.Select(x => $"{x.V} → {x.Name}01..{x.Name}12"))} (ẩn trên lưới, nhập ở form).");

        if (monthGrid && monthly.Count > 0)
        {
            var detailTable = table + "ct";
            var dcols = cols.Where(c => keys.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            dcols.Add(new Col("thang", "int", "Tháng", "Month", "", "", true));
            dcols.AddRange(monthly);
            dcols.AddRange(SystemCols);
            var dkeys = keys.Concat(new[] { "thang" }).ToList();
            spec["detail"] = true;
            spec["detailTable"] = detailTable;
            spec["detailId"] = table + "Detail";
            spec["detailTitleV"] = "Theo tháng";
            spec["detailTitleE"] = "By month";
            spec["detailKeys"] = string.Join(", ", dkeys);
            spec["linkKeys"] = string.Join(", ", keys);
            spec["detailOrder"] = "thang";
            spec["detailImport"] = false;
            spec["detailColumns"] = new JsonArray(dcols.Select(c =>
            {
                var isLink = keys.Contains(c.Name, StringComparer.OrdinalIgnoreCase);
                var j = ColumnJson(c, isLink || c.Name == "thang", false);
                j["autoComplete"] = c.Controller;
                if (isLink) j["inGrid"] = false;
                return (JsonNode)j;
            }).ToArray());
            notes.Add($"Chỉ tiêu theo tháng làm lưới chi tiết {detailTable} (khoá {string.Join(", ", dkeys)}): {string.Join(", ", monthly.Select(x => x.Name))}.");
        }
    }

    private static readonly Col[] SystemCols =
    {
        new("datetime0", "datetime", "Ngày tạo", "Created date", "", ""), new("datetime2", "datetime", "Ngày sửa", "Modified date", "", ""),
        new("user_id0", "int", "Người tạo", "Created by", "", ""), new("user_id2", "int", "Người sửa", "Modified by", "", ""),
    };

    /// <summary>Tiền tố bảng màn khai báo số liệu: tiền tố danh mục bỏ "dm" (zdm → z); rỗng thì "z".</summary>
    private static string DataPrefix(string catalogPrefix) => Regex.Replace(catalogPrefix, "dm$", "") is { Length: > 0 } p ? p : "z";

    /// <summary>Mô tả sau dấu ":" của 1 cột: "bắt buộc nhập" / "không bắt buộc" → Required; "Nhập số tiền", "số lượng", "ngày" → kiểu (Fast: tiền/SL/giá numeric(19,4)).</summary>
    private static Col ApplyDesc(Col c, string desc)
    {
        if (desc.Length == 0) return c;
        var d = Fold(desc);
        bool? req = Regex.IsMatch(d, @"\bkhong\s+(bat buoc|can|phai)\b") ? false : Regex.IsMatch(d, @"\b(bat buoc|phai nhap|khong de trong|khong duoc de trong)\b") ? true : c.Required;
        var type = c.SqlType;
        if (c.Controller.Length == 0)
        {
            if (Regex.IsMatch(d, @"\b(so tien|tien|gia tri|so luong|don gia|ty le|phan tram|nhap so)\b")) type = "numeric(19,4)";
            else if (Regex.IsMatch(d, @"\b(nhap ngay|ngay thang|chon ngay)\b")) type = "smalldatetime";
        }
        return c with { Required = req, SqlType = type };
    }

    /// <summary>Cột có lookup: kiểu lấy theo cột mã của bảng nguồn thật (Information "ma_nh$dmnhphi.ten_nh" → dmnhphi.ma_nh varchar(8)).</summary>
    private static async Task<Col> LookupTypeAsync(Col c, HashSet<string> tableSet, Func<string, Task<List<AiTableColumn>>> getColumns)
    {
        if (c.Controller.Length == 0) return c;
        var lk = FieldDictionaryService.Instance.Lookups(c.Name).FirstOrDefault(l => l.Controller.Equals(c.Controller, StringComparison.OrdinalIgnoreCase));
        var (t, k) = lk?.Source ?? ("", "");
        if (t.Length == 0 || !tableSet.Contains(t)) return c;
        try
        {
            var real = (await getColumns(t)).FirstOrDefault(x => x.Name.Equals(k, StringComparison.OrdinalIgnoreCase));
            return real is { SqlType.Length: > 0 } ? c with { SqlType = real.SqlType } : c;
        }
        catch { return c; }
    }

    /// <summary>
    /// "Danh mục X" của mô tả lookup → (field, controller) chuẩn: lookup thường dùng → header.xml theo tiêu đề ("nhóm phí" → "Nhóm phí 1" = nh_phi1 / ExpenseGroup)
    /// → danh mục cùng tiêu đề trong source (Controllers\Dir) rồi field header.xml tra vào bảng đó.
    /// </summary>
    internal static (string Field, string Controller)? ResolveLookupNoun(string noun, string sourcePath)
    {
        noun = Regex.Replace(noun.Trim(), @"\s+(bat buoc|khong bat buoc|khong)\b.*$", "").Trim();
        if (noun.Length == 0) return null;
        foreach (var l in CommonLookups)
            if (l.Words.Any(w => noun == w || noun.StartsWith(w + " ")))
                return (l.Field, l.Controller);
        foreach (var h in new[] { noun, noun + " 1", "ma " + noun })
            if (BestHeaderField(h) is { Lookup: { } hl } e && hl.Controller.Length > 0)
                return (e.Name, hl.Controller);
        if (FindExistingCatalog(sourcePath, noun) is { } cat)
        {
            var e = FieldDictionaryService.Instance.Entries
                .Where(x => x.Lookup is { } l && l.Information.Contains("$" + cat.Table + ".", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => (x.Lookup!.IsSimple ? 0 : 5) + x.Name.Length / 4.0).FirstOrDefault();
            if (e is not null) return (e.Name, e.Lookup!.Controller);
        }
        return null;
    }

    private static readonly Regex LookupDescRx = new(@"(lookup|chon|lay|tra)\s+(tu|theo|trong)\s+(danh muc\s+|dm\s*)?(?<n>[a-z0-9 /]+)", RegexOptions.Compiled);

    /// <summary>Gợi ý lookup chuẩn cho các cụm "Lookup từ Danh mục X" trong y/c — gửi kèm cho AI (đường có API key) để chọn đúng field / controller.</summary>
    public static List<string> LookupHints(string content, string sourcePath) =>
        LookupDescRx.Matches(Fold(content))
            .Select(m => Regex.Replace(m.Groups["n"].Value, @"\s+(bat buoc|khong bat buoc|khong)\b.*$", "").Trim())
            .Where(n => n.Length > 1).Distinct()
            .Select(n => (n, r: ResolveLookupNoun(n, sourcePath)))
            .Where(x => x.r is not null)
            .Select(x => $"“danh mục {x.n}” → field {x.r!.Value.Field}, autoComplete {x.r.Value.Controller}")
            .ToList();

    /// <summary>
    /// Tên ngắn cho chỉ tiêu theo tháng (thêm hậu tố 01..12): ngân sách dự toán → ns_dt, số lượng dự toán → sl_dt, đơn giá dự toán → gia_dt,
    /// số tiền phát sinh ngoài dự toán → ps_ndt. Từ lạ lấy chữ đầu.
    /// </summary>
    internal static string MonthBase(string phrase)
    {
        var f = Fold(phrase);
        var map = new (string W, string A)[]
        {
            ("ngoai du toan", "ndt"), ("ngoai ke hoach", "nkh"), ("du toan", "dt"), ("ke hoach", "kh"), ("ngan sach", "ns"), ("so luong", "sl"),
            ("don gia", "gia"), ("gia", "gia"), ("phat sinh", "ps"), ("chi phi", "cp"), ("thuc te", "tt"), ("doanh thu", "doanh_thu"),
            ("so tien", "tien"), ("tien", "tien"), ("phe duyet", "pd"),
        };
        var parts = new List<string>();
        var letters = "";
        var rest = Regex.Replace(Regex.Replace(f, @"[^a-z0-9 ]+", " "), @"\s+", " ").Trim();
        while (rest.Length > 0)
        {
            var hit = map.FirstOrDefault(x => rest == x.W || rest.StartsWith(x.W + " "));
            if (hit.W is not null)
            {
                if (letters.Length > 0) { parts.Add(letters); letters = ""; }
                parts.Add(hit.A);
                rest = rest[hit.W.Length..].TrimStart();
                continue;
            }
            var sp = rest.IndexOf(' ');
            var word = sp < 0 ? rest : rest[..sp];
            if (word is not ("cua" or "cac" or "va" or "theo" or "trong")) letters += word[0];
            rest = sp < 0 ? "" : rest[(sp + 1)..];
        }
        if (letters.Length > 0) parts.Add(letters);
        if (parts.Count > 1 && parts[0] == "tien") parts.RemoveAt(0);   // "số tiền phát sinh …" → ps_…
        var s = string.Join('_', parts);
        if (s.Length == 0) s = "ct";
        return s.Length > 12 ? s[..12].TrimEnd('_') : s;
    }

    private static string Reply(string kind, string summary, List<string> notes, List<string> questions, JsonObject? spec) =>
        new JsonObject
        {
            ["loai"] = kind, ["tom_tat"] = summary, ["can_bang"] = new JsonArray(),
            ["cau_hoi"] = new JsonArray(questions.Select(q => (JsonNode)q).ToArray()),
            ["ghi_chu"] = new JsonArray(notes.Select(n => (JsonNode)n).ToArray()),
            ["spec"] = spec,
        }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private static JsonObject ColumnJson(Col c, bool isKey, bool isName)
    {
        var sys = c.Name is "datetime0" or "datetime2" or "user_id0" or "user_id2";
        var second = c.Name.StartsWith("ten_", StringComparison.OrdinalIgnoreCase) && c.Name.EndsWith("2");
        var t = c.SqlType.ToLowerInvariant();
        var width = isKey ? 120 : isName ? 300 : c.Name is "ghi_chu" or "dien_giai" ? 300
            : t.StartsWith("numeric") || t.StartsWith("decimal") || t.Contains("date") || t is "int" or "bit" or "char(1)" ? 100 : 200;
        return new JsonObject
        {
            ["name"] = c.Name, ["sqlType"] = c.SqlType, ["headerV"] = c.V, ["headerE"] = c.E, ["width"] = width,
            ["inGrid"] = !sys && !second, ["inForm"] = !sys, ["inLookup"] = isKey || isName, ["inFilter"] = isKey || isName,
            ["inImport"] = !sys, ["allowNulls"] = !isKey && c.Required != true, ["readOnly"] = false, ["hidden"] = false,
            ["autoComplete"] = isKey || isName ? "" : c.Controller,
        };
    }

    // ---------------------------------------------------------------------------------------- đọc câu y/c

    private static string CleanName(string s)
    {
        s = Regex.Replace(s, @"(?i)\s+(g[ồo]m|bao g[ồo]m|c[óo]\s+c[áa]c|v[ớo]i|cho\s+(ph[ée]p|kh[áa]ch)|đ[ểe]|m[ỗo]i|g[ắa]n|c[óo])\b.*$", "");
        s = Regex.Replace(s.Trim(), @"\s+", " ");
        s = Regex.Replace(s, @"(?i)^(danh\s*m[ụu]c|khai\s*b[áa]o)\s+", "").Trim('“', '”', '"', '\'', ' ');   // "khai báo danh mục X", “Danh mục X”
        var words = s.Split(' ');
        return string.Join(' ', words.Take(6));
    }

    /// <summary>
    /// Danh sách cột sau tên danh mục, trả (cụm, mô tả sau dấu ":"). Chỗ bắt đầu: "X: a, b" ngay sau tên; "Thông tin (khai báo / theo dõi) / (Gồm) các trường /
    /// Gồm / Bao gồm / Chi tiết:"; hoặc gạch đầu dòng ngay dòng sau. Gạch đầu dòng: mỗi dòng 1 cột, đổi loại gạch (vd "+" → "-") = hết danh sách.
    /// </summary>
    private static List<(string P, string D)> ColumnPhrases(string content, int from)
    {
        var rest = content[from..].Replace("\r", "");
        int start;
        // "Gồm các trường:" / "Thông tin khai báo:" gần tên danh mục ưu tiên hơn dấu ":" ngay sau tên (sau đó hay là "Mục đích: …").
        if (IntroRx.Match(rest) is { Success: true } intro && intro.Index < 600) start = intro.Index + intro.Length - 1;
        else if (Regex.Match(rest, @"^[ \t]*[“”""']?[ \t]*:") is { Success: true } here) start = here.Index + here.Length - 1;
        // Gạch đầu dòng ở dòng sau (có thể cách vài dòng trống — y/c FSG hay có).
        else if (rest.IndexOf('\n') is var nl and >= 0 && Regex.IsMatch(rest[(nl + 1)..], @"^([ \t]*\n)*[ \t]*" + Bullet + @"[ \t]*\S")) start = nl;
        else return new();

        var lines = rest[(start + 1)..].Split('\n');
        var result = new List<(string P, string D)>();
        // Liệt kê ngay trên dòng: "…: Mã, Tên, Ghi chú. Cho phép import…" (câu sau không phải cột). Dòng có gạch đầu dòng ký tự lạ (Word) cũng tách ở đây.
        var first = lines[0].Trim();
        var inline = first.Length > 0 && !BulletRx.IsMatch(first);
        if (inline)
        {
            var parts = Regex.Split(first, @"\s+(?=" + Bullet + @"\s)");   // "Gồm các trường:  Mã phương thức  Tên…" trên 1 dòng
            if (parts.Length > 1) foreach (var x in parts.Skip(1)) AddBullet(BulletRx.Replace(x, "", 1));
            else result.AddRange(SplitPhrases(Regex.Split(first, @"\.\s+(?=\p{Lu})")[0]));
        }
        string? marker = null;
        foreach (var l in inline ? lines.Skip(1) : lines)
        {
            var bm = BulletRx.Match(l);
            if (!bm.Success) { if (marker is null && l.Trim().Length == 0) continue; break; }
            var mk = char.IsDigit(bm.Groups["m"].Value[0]) ? "1" : bm.Groups["m"].Value;
            if (marker is not null && mk != marker) break;
            // Loại gạch chỉ "khoá" khi dòng đó là cột thật (dòng "- Màn hình nhập liệu: …" đứng trước "+ Mã …" không tính).
            if (AddBullet(l[bm.Length..])) marker ??= mk;
        }
        return result.DistinctBy(x => x.P, StringComparer.OrdinalIgnoreCase).ToList();

        bool AddBullet(string text)
        {
            var desc = "";
            if (text.IndexOf(':') is var colon and > 0) { desc = text[(colon + 1)..].Trim(); text = text[..colon]; }
            text = Regex.Replace(text, @"\([^)]*\)|\[[^\]]*\]", "").Trim().TrimEnd('.', ',', ';').Trim();
            // "Khai báo ngân sách dự toán theo 12 tháng trong năm để so sánh với …" → "ngân sách dự toán" + đánh dấu theo tháng.
            text = Regex.Replace(text, @"(?i)^(khai\s*b[áa]o|nh[ậa]p|theo\s*d[õo]i|c[ậa]p\s*nh[ậa]t)\s+", "");
            text = Regex.Replace(text, @"(?i)\s+(đ[ểe]|d[ùu]ng\s+đ[ểe]|nh[ằa]m|nh[ưu]ng)\s.*$", "");
            if (MonthlyRx.IsMatch(text))
            {
                text = Regex.Replace(MonthlyRx.Replace(text, " "), @"(?i)\s+c[ủu]a\s+.*$", "");
                desc = (desc + " " + MonthlyTag).Trim();
            }
            text = Regex.Replace(text, @"\s+", " ").Trim();
            // Dòng mô tả ("Mục đích…", "Màn hình…", "Gồm các trường") không phải cột.
            if (text.Length is 0 or > 60 || IntroRx.IsMatch(text + ":")
                || Regex.IsMatch(Fold(text), @"^(muc dich|man hinh|duong dan|yeu cau|link|luu y|chuc nang|thuc hien|master|detail|cho phep|mo ta|giai phap|vi du)\b")) return false;
            result.Add((text, desc));
            return true;
        }
    }

    private const string Bullet = @"(?:[-+*•–▪●◦·]|o(?=[ \t])|\d+[.)])";
    private static readonly Regex BulletRx = new(@"^[ \t]*(?<m>" + Bullet + @")[ \t]*", RegexOptions.Compiled);
    private static readonly Regex IntroRx = new(@"(?i)(th[ôo]ng tin(\s+(khai b[áa]o|theo d[õo]i|g[ồo]m|qu[ảa]n l[ýy]))?|(g[ồo]m\s+)?c[áa]c\s+(tr[ườu][ờo]ng|c[ộo]t|th[ôo]ng tin)|bao g[ồo]m|g[ồo]m|chi ti[ếe]t\s+g[ồo]m)(\s+sau(\s+đ[âa]y)?)?\s*:", RegexOptions.Compiled);

    private static List<(string P, string D)> SplitPhrases(string list) =>
        Regex.Split(list, @"\s*(?:,|;|\s+v[àa]\s+|\s+ho[ặa]c\s+)\s*")
            .Select(p =>
            {
                var desc = "";
                if (p.IndexOf(':') is var c and > 0) { desc = p[(c + 1)..].Trim(); p = p[..c]; }
                return (P: Regex.Replace(p, @"\([^)]*\)", "").Trim().TrimEnd('.', ':').Trim(), D: desc);
            })
            .Where(p => p.P.Length is > 0 and <= 60).DistinctBy(p => p.P, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Mô tả "Lookup từ Danh mục nhóm phí" / "chọn từ danh mục kho hàng" → gắn lookup chuẩn (<see cref="ResolveLookupNoun"/>) nếu cụm chưa có lookup.
    /// Tên cột giữ nguyên khi field đó (header.xml) có lookup cùng controller; không thì dùng field chuẩn của lookup ("Trạm" lookup kho → ma_kho).</summary>
    private static Col WithLookupHint(Col c, string desc, string sourcePath)
    {
        if (c.Controller.Length > 0 || desc.Length == 0) return c;
        var m = LookupDescRx.Match(Fold(desc));
        if (!m.Success || ResolveLookupNoun(m.Groups["n"].Value, sourcePath) is not { } l) return c;
        var keep = FieldDictionaryService.Instance.Lookups(c.Name).Any(x => x.Controller.Equals(l.Controller, StringComparison.OrdinalIgnoreCase));
        var field = keep ? c.Name : l.Field;
        return c with { Name = field, SqlType = "varchar(32)", Controller = l.Controller, Note = $"{c.V} → {field} ({l.Controller})" };
    }

    /// <summary>Tên bảng y/c ghi rõ: "bảng / table zdmxxx" hoặc 1 từ bắt đầu bằng tiền tố bảng mới (zdm…). Tên bảng chuẩn nhắc tới trong
    /// mô tả ("dmvt.nh_vt9", "lấy từ dmkh") KHÔNG tính — trước đây "Danh mục dự án" bị gán nhầm bảng dmvt.</summary>
    private static string? ExplicitTable(string content, string prefix) =>
        Regex.Match(content, @"(?i)\b(b[ảa]ng|table)\s+(dbo\.)?(?<t>z?dm[a-z0-9_]{1,30})\b") is { Success: true } bm ? bm.Groups["t"].Value.ToLowerInvariant()
        : Regex.Matches(content, @"\b" + Regex.Escape(prefix) + @"[a-z0-9_]{1,30}\b", RegexOptions.IgnoreCase).Select(m => m.Value.ToLowerInvariant()).FirstOrDefault();

    /// <summary>Chữ đầu mỗi từ (hình thức bán hàng → htbh); 1 từ thì giữ nguyên; "nhóm" → nh, "loại"/"kiểu" giữ cả từ (nhóm khách hàng → nhkh, loại hợp đồng → loaihd).</summary>
    internal static string Abbrev(string folded)
    {
        var words = Regex.Split(folded.Trim(), @"[^a-z0-9]+").Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return "moi";
        if (words.Count == 1) return words[0].Length > 12 ? words[0][..12] : words[0];
        var head = words[0] switch { "nhom" => "nh", "loai" or "kieu" => words[0], _ => words[0][..1] };
        return head + string.Concat(words.Skip(1).Select(w => w[0]));
    }

    /// <summary>1 cụm cột → field. Note = "Khách hàng → ma_kh (Customer)" khi tra từ điển; "?" khi phải đoán (sẽ hỏi lại).</summary>
    private static Col MapPhrase(string phrase, string code, string catFolded, ref int groupNo)
    {
        var p = Cap(phrase);
        var f = Fold(phrase);
        // "Mã" / "Mã loại" (đầu của tên danh mục "loại hợp đồng") / "Mã loại hợp đồng" = khoá; tương tự "Tên ...".
        // "Mã liên doanh" của "loại hình liên doanh": phần sau Mã/Tên là đầu HOẶC đuôi tên danh mục.
        bool Own(string prefix) => f == prefix || f == prefix + " so" || (f.StartsWith(prefix + " ") && (catFolded + " ").StartsWith(f[(prefix.Length + 1)..] + " "))
            || (f.StartsWith(prefix + " ") && (" " + catFolded).EndsWith(" " + f[(prefix.Length + 1)..]));
        if (Own("ma")) return new Col("ma_" + code, "varchar(32)", p, "Code", "", "");
        if (Own("ten")) return new Col("ten_" + code, "nvarchar(256)", p, "Name", "", "");
        if (f is "ten tieng anh" or "ten 2" or "ten khac" or "ten english" or "ten thu 2" or "ten thu hai" || (f.StartsWith("ten ") && Regex.IsMatch(f, @" (khac|2|tieng anh)$"))) return new Col("ten_" + code + "2", "nvarchar(256)", "Tên 2", "Name 2", "", "");
        if (f is "ghi chu" or "chu thich") return new Col("ghi_chu", "nvarchar(512)", "Ghi chú", "Note", "", "");
        if (f is "dien giai" or "mo ta" or "noi dung") return new Col("dien_giai", "nvarchar(512)", p, "Description", "", "");
        if (f is "trang thai" or "tinh trang" or "su dung" or "con su dung" or "ngung su dung" or "status") return new Col("status", "char(1)", "Trạng thái", "Status", "", "");
        if (f.StartsWith("nhom") && BestHeaderField(f + " 1") is { Lookup: { Controller.Length: > 0 } gl } ge)
            return new Col(ge.Name, "varchar(32)", p, ge.HeaderE.Length > 0 ? ge.HeaderE : HeaderTranslator.Instance.Translate(p) ?? p, gl.Controller, $"{p} → {ge.Name} ({gl.Controller})");
        if (f.StartsWith("nhom") && !f.StartsWith("nhom kh") && !f.StartsWith("nhom vt"))
        {
            groupNo++;
            return new Col($"nh_{code}{groupNo}", "varchar(32)", p, HeaderTranslator.Instance.Translate(p) ?? $"Group {groupNo}", "", $"{p} → nh_{code}{groupNo} (nhập mã)");
        }

        var noun = f.StartsWith("ma ") ? f[3..] : f;
        foreach (var l in CommonLookups)
            if (l.Words.Contains(noun)) return new Col(l.Field, "varchar(32)", p, l.E, l.Controller, $"{p} → {l.Field} ({l.Controller})");

        if (HeaderDict.Value.TryGetValue(f, out var hd)) return new Col(hd.Name, GuessType(hd.Name, "", f), p, hd.E, "", $"{p} → {hd.Name}");

        if ((BestHeaderField(f) ?? (noun != f ? BestHeaderField(noun) : null)) is { } e)
        {
            var ctl = e.Lookup?.Controller ?? "";
            return new Col(e.Name, ctl.Length > 0 ? "varchar(32)" : GuessType(e.Name, e.Type, f), p,
                e.HeaderE.Length > 0 ? e.HeaderE : HeaderTranslator.Instance.Translate(p) ?? p, ctl, $"{p} → {e.Name}{(ctl.Length > 0 ? $" ({ctl})" : "")}");
        }

        var slug = Slug(f);
        return new Col(slug, GuessType(slug, "", f), p, HeaderTranslator.Instance.Translate(p) ?? p, "", "?");
    }

    /// <summary>Field header.xml có tiêu đề (bỏ dấu) trùng cụm: ưu tiên field có lookup, tên ngắn, không có %/$, không phải cờ _yn.</summary>
    private static FieldDictionaryService.Entry? BestHeaderField(string folded) =>
        HeaderIndex.Value.TryGetValue(folded, out var list)
            ? list.OrderBy(e => (e.Name.Contains('%') || e.Name.Contains('$') ? 10 : 0) + (e.Name.EndsWith("_yn") ? 5 : 0) + (e.Lookup is null ? 2 : 0)
                    + (Regex.IsMatch(e.Name, @"^[a-z]_") ? 3 : 0) + e.Name.Length / 4.0)   // f_ma_tinh, s_xxx… là field phụ
                .FirstOrDefault()
            : null;

    private static string GuessType(string name, string dictType, string folded)
    {
        switch (dictType)
        {
            case "Decimal": return "numeric(19,4)";
            case "DateTime": return "smalldatetime";
            case "Boolean": return "bit";
            case "Int16": case "Int32": return "int";
            case "Byte": return "tinyint";
        }
        if (Regex.IsMatch(folded, @"^(nam|thang|ky|quy|lan|so lan|lan cap nhat|lan dieu chinh)\b") && !folded.StartsWith("nam ")) return "int";   // Năm / Lần cập nhật (Fast: nam int)
        if (name.StartsWith("ngay", StringComparison.OrdinalIgnoreCase) || folded.StartsWith("ngay") || folded.Contains("thoi gian")) return "smalldatetime";
        if (Regex.IsMatch(folded, @"\b(so luong|sl|tien|gia|don gia|ty le|ty gia|phan tram|chiet khau|thue suat|tri gia|gia tri)\b") || folded.Contains('%')) return "numeric(19,4)";
        if (Regex.IsMatch(folded, @"^(co |cho phep|la |kiem tra)") || name.EndsWith("_yn", StringComparison.OrdinalIgnoreCase)) return "bit";
        if (name.StartsWith("ma_", StringComparison.OrdinalIgnoreCase)) return "varchar(32)";
        return "nvarchar(256)";
    }

    /// <summary>Tên field từ cụm bỏ dấu: "ngày hiệu lực đến" → ngay_hieu_luc_den (tối đa 32 ký tự); cụm "có ..." → ..._yn.</summary>
    private static string Slug(string folded)
    {
        var yn = Regex.IsMatch(folded, @"^(co |cho phep )");
        var words = Regex.Split(Regex.Replace(folded, @"^(co |cho phep )", ""), @"[^a-z0-9]+").Where(w => w.Length > 0 && w is not "cua" and not "cac" and not "la").ToList();
        var s = string.Join('_', words);
        if (s.Length == 0) s = "cot_moi";
        if (char.IsDigit(s[0])) s = "c" + s;
        if (yn) s += "_yn";
        return s.Length > 32 ? s[..32].TrimEnd('_') : s;
    }

    private static bool IsNo(string s) => Regex.IsMatch(Fold(s), @"^(khong|ko|k|no|n|0|bo|khong can)\b");

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0], CultureInfo.GetCultureInfo("vi-VN")) + s[1..];

    internal static string Fold(string s) =>
        Regex.Replace(new string(s.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .Replace('đ', 'd').Replace('Đ', 'D').ToLowerInvariant(), @"\s+", " ").Trim().TrimEnd(':').Trim();

    // ---------------------------------------------------------------------------------------- từ điển

    /// <summary>header.xml: tiêu đề Việt (bỏ dấu) → các field có tiêu đề đó.</summary>
    private static readonly Lazy<Dictionary<string, List<FieldDictionaryService.Entry>>> HeaderIndex = new(() =>
    {
        var d = new Dictionary<string, List<FieldDictionaryService.Entry>>(StringComparer.Ordinal);
        foreach (var e in FieldDictionaryService.Instance.Entries)
        {
            if (e.HeaderV.Length == 0) continue;
            var k = Fold(e.HeaderV);
            if (!d.TryGetValue(k, out var l)) d[k] = l = new();
            if (!l.Any(x => x.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase))) l.Add(e);
        }
        return d;
    });

    /// <summary>Templates\HeaderDictionary.json (mục "columns": tên cột → [Việt, Anh]) tra ngược: tiêu đề Việt (bỏ dấu) → (cột, tiêu đề Anh).</summary>
    private static readonly Lazy<Dictionary<string, (string Name, string E)>> HeaderDict = new(() =>
    {
        var d = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, ColumnHeaderGuesser.DictionaryFileName);
            if (!File.Exists(path)) return d;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("columns", out var cols))
                foreach (var p in cols.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() >= 2 && p.Name is not ("ma" or "ten" or "ten2"))
                        d.TryAdd(Fold(p.Value[0].GetString() ?? ""), (p.Name, p.Value[1].GetString() ?? ""));
        }
        catch { /* thiếu / hỏng file thì bỏ qua */ }
        return d;
    });

    // ---------------------------------------------------------------------------------------- danh mục có sẵn trong source

    private sealed record ExistingCatalog(string Controller, string Table, string Title);
    private static readonly Dictionary<string, Dictionary<string, ExistingCatalog>> SourceCatalogs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Controllers\Dir\*.xml|*.f của source có bảng dm* và tiêu đề (bỏ dấu, bỏ "danh mục") trùng tên danh mục — đọc phần đầu file, nhớ theo source.</summary>
    private static ExistingCatalog? FindExistingCatalog(string sourcePath, string folded)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) return null;
        Dictionary<string, ExistingCatalog> map;
        lock (SourceCatalogs)
        {
            if (!SourceCatalogs.TryGetValue(sourcePath, out map!))
            {
                map = new(StringComparer.Ordinal);
                try
                {
                    var dir = Path.Combine(sourcePath, "App_Data", "Controllers", "Dir");
                    if (Directory.Exists(dir))
                        foreach (var file in Directory.EnumerateFiles(dir, "*.*").Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".f", StringComparison.OrdinalIgnoreCase)))
                        {
                            string head;
                            try
                            {
                                using var sr = new StreamReader(file, Encoding.UTF8, true);
                                var buf = new char[6000];
                                head = new string(buf, 0, sr.ReadBlock(buf, 0, buf.Length));
                            }
                            catch { continue; }
                            var t = Regex.Match(head, @"<dir\s[^>]*\btable=""(?<t>z?dm[^""]*)""");
                            var v = Regex.Match(head, @"<title\s+v=""(?<v>[^""]*)""");
                            if (!t.Success || !v.Success) continue;
                            var title = Regex.Replace(v.Groups["v"].Value, @"&[^;]+;", "").Trim();
                            var key = Regex.Replace(Fold(title), @"^danh muc\s+", "");
                            if (key.Length > 0) map.TryAdd(key, new ExistingCatalog(Path.GetFileNameWithoutExtension(file), t.Groups["t"].Value, title));
                        }
                }
                catch { /* source không đọc được thì bỏ qua bước này */ }
                SourceCatalogs[sourcePath] = map;
            }
        }
        return map.TryGetValue(folded, out var hit) ? hit : null;
    }
}
