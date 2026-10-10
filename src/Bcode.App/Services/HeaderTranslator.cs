extern alias rb;
using System.Text.Json;
using System.Text.RegularExpressions;
using Catalog = rb::Bcode.App.Services.Rpt.Builder.ReportCatalog;

namespace Bcode.App.Services;

/// <summary>
/// Dịch tiêu đề tiếng Việt → tiếng Anh NGAY TRÊN MÁY (không gửi đi đâu), theo đúng cách các file source FastBusiness vẫn đặt tiêu đề:
/// <list type="number">
/// <item>Khớp nguyên cụm với các cặp Việt/Anh có thật: header.xml + parameters.xml, từ điển của "Tạo báo cáo" (reportcatalog.json — 302 Filter, 445 Grid),
/// Templates\HeaderDictionary.json và tiêu đề người dùng đã nhập (quicklist-headers.json). Một tiêu đề Việt có nhiều bản Anh thì lấy bản gặp nhiều nhất.</item>
/// <item>Tiền tố (Mã / Tên / Số lượng / Tiền / Ngày… của HeaderDictionary): "Mã khách hàng" → "{0} Code" với {0} = dịch phần còn lại.</item>
/// <item>Ghép từng cụm (khớp dài nhất trước) rồi đảo thứ tự kiểu tiếng Anh: "Tiền hàng" → "Goods Amount"; riêng "Tổng…" giữ đầu câu.</item>
/// </list>
/// Còn từ nào không biết thì trả null (không đoán) — người dùng tự nhập.
/// </summary>
public sealed class HeaderTranslator
{
    private static readonly Lazy<HeaderTranslator> _instance = new(() => new HeaderTranslator());
    public static HeaderTranslator Instance => _instance.Value;

    private static readonly string[] Front = { "tổng", "tổng cộng", "cộng" };

    /// <summary>Từ vựng kế toán cơ bản bù chỗ các file source không có cặp riêng (ưu tiên thấp hơn cặp thật trong source).</summary>
    private static readonly (string V, string E)[] Vocab =
    {
        ("mua", "Purchase"), ("bán", "Sales"), ("nhập", "Receipt"), ("xuất", "Issue"), ("tồn", "Stock"), ("đầu kỳ", "Opening"), ("cuối kỳ", "Closing"),
        ("trong kỳ", "In Period"), ("nợ", "Debit"), ("có", "Credit"), ("số dư", "Balance"), ("phát sinh", "Amount"), ("công nợ", "Debt"), ("thanh toán", "Payment"),
        ("tạm ứng", "Advance"), ("đặt cọc", "Deposit"), ("chiết khấu", "Discount"), ("giảm giá", "Allowance"), ("thuế", "Tax"), ("doanh thu", "Revenue"),
        ("chi phí", "Expense"), ("giá vốn", "Cost of Goods"), ("lãi", "Profit"), ("lỗ", "Loss"), ("diện tích", "Area"), ("đặc điểm", "Characteristics"),
        ("đường", "Street"), ("ô", "Plot"), ("lô", "Lot"), ("kinh tế", "Economic"), ("chuyển nhượng", "Transfer"), ("tỷ lệ", "Rate"), ("trạng thái", "Status"),
        ("giao dịch", "Transaction"), ("ghi chú", "Note"), ("số tiền", "Amount"), ("khách", "Customer"), ("đơn giá", "Unit Price"), ("thành tiền", "Amount"),
        ("sổ chi tiết", "Detailed Ledger"), ("sổ cái", "General Ledger"), ("báo cáo", "Report"), ("bảng kê", "List"), ("tổng hợp", "Summary"), ("chi tiết", "Detail"),
        ("kỳ", "Period"), ("tháng", "Month"), ("quý", "Quarter"), ("năm", "Year"), ("hạn", "Due"), ("quá hạn", "Overdue"), ("còn lại", "Remaining"), ("đã", "Done"),
        ("dự án", "Project"), ("công trình", "Project"), ("phòng ban", "Department"), ("chi nhánh", "Branch"), ("đơn vị", "Unit"), ("ngân hàng", "Bank"),
    };

    /// <summary>Chữ viết tắt hay gặp trong tiêu đề FBO → cụm đầy đủ (chỉ dùng khi bản thân chữ đó không có trong từ điển).</summary>
    private static readonly Dictionary<string, string> Abbr = new(StringComparer.Ordinal)
    {
        ["hđ"] = "hợp đồng", ["hd"] = "hợp đồng", ["tt"] = "thanh toán", ["sl"] = "số lượng", ["tk"] = "tài khoản", ["nt"] = "ngoại tệ", ["kh"] = "khách hàng",
        ["ncc"] = "nhà cung cấp", ["vt"] = "vật tư", ["c.từ"] = "chứng từ", ["ct"] = "chứng từ", ["đvt"] = "đơn vị tính", ["ck"] = "chiết khấu",
        ["sp"] = "sản phẩm", ["nv"] = "nhân viên", ["bp"] = "bộ phận", ["vv"] = "vụ việc", ["gtgt"] = "giá trị gia tăng",
    };
    private readonly Dictionary<string, string> _map = new(StringComparer.Ordinal);
    private readonly List<(string V, string Format)> _prefixes = new();

    private HeaderTranslator()
    {
        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        void Add(string? v, string? e, int weight = 1)
        {
            var nv = Norm(v); var ne = (e ?? "").Trim().TrimEnd(':').Trim();
            if (nv.Length == 0 || !IsEnglish(ne) || Norm(ne) == nv) return;
            if (!counts.TryGetValue(nv, out var m)) counts[nv] = m = new(StringComparer.Ordinal);
            m[ne] = m.GetValueOrDefault(ne) + weight;
        }

        foreach (var (v, e) in Vocab) Add(v, e, 2);
        foreach (var (v, e) in FieldDictionaryService.Instance.HeaderPairs()) Add(v, e);
        try
        {
            using var s = typeof(Catalog).Assembly.GetManifestResourceStream("reportcatalog.json");
            if (s is not null)
            {
                using var doc = JsonDocument.Parse(s);
                foreach (var part in new[] { "filters", "grid" })
                    if (doc.RootElement.TryGetProperty(part, out var obj))
                        foreach (var p in obj.EnumerateObject())
                            Add(Str(p.Value, "hv"), Str(p.Value, "he"), p.Value.TryGetProperty("n", out var n) && n.TryGetInt32(out var c) ? Math.Max(1, c) : 1);
            }
        }
        catch { /* thiếu từ điển của Tạo báo cáo: dùng các nguồn còn lại */ }
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ColumnHeaderGuesser.DictionaryFileName);
            if (File.Exists(path))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var part in new[] { "columns", "nouns" })
                    if (doc.RootElement.TryGetProperty(part, out var obj))
                        foreach (var p in obj.EnumerateObject())
                            if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() >= 2) Add(p.Value[0].GetString(), p.Value[1].GetString(), 3);
                if (doc.RootElement.TryGetProperty("prefixes", out var pre))
                    foreach (var p in pre.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() >= 2 && (p.Value[1].GetString() ?? "").Contains("{0}"))
                            _prefixes.Add((Norm(p.Value[0].GetString()), p.Value[1].GetString()!));
            }
        }
        catch { /* từ điển hỏng: bỏ qua */ }
        try
        {
            var learned = Path.Combine(BcodePaths.AppData, "Bcode", "quicklist-headers.json");
            if (File.Exists(learned))
                foreach (var p in JsonDocument.Parse(File.ReadAllText(learned)).RootElement.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() >= 2) Add(p.Value[0].GetString(), p.Value[1].GetString(), 5);
        }
        catch { /* chưa có tiêu đề đã học */ }

        foreach (var (v, m) in counts)
            _map[v] = m.OrderByDescending(x => x.Value).ThenBy(x => x.Key.Length).First().Key;
        _prefixes.Sort((a, b) => b.V.Length.CompareTo(a.V.Length));
    }

    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Có chữ cái Latin, không có dấu tiếng Việt, không phải "???".</summary>
    private static bool IsEnglish(string e) =>
        e.Length > 0 && Regex.IsMatch(e, "[A-Za-z]") && !Regex.IsMatch(e, "[À-ỹđĐ]") && !e.Contains("??");

    private static string Norm(string? s) => Regex.Replace((s ?? "").Trim().TrimEnd(':', '.').Trim(), @"\s+", " ").ToLowerInvariant();

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"???" / trống / còn dấu tiếng Việt = chưa có tiêu đề Anh dùng được.</summary>
    public static bool IsMissing(string? english) => !IsEnglish((english ?? "").Trim());

    /// <summary>Bản tiếng Anh của tiêu đề Việt, null nếu còn từ không biết.</summary>
    public string? Translate(string? vietnamese) => Core(Norm(vietnamese), 0) is { } e ? Cap(e) : null;

    /// <summary>Khớp nguyên cụm, nhưng nếu bản ghép từng cụm dịch được mà không chung từ nào với bản nguyên cụm thì coi cặp nguyên cụm là nhập sai
    /// trong source (vd "Mã hợp đồng kinh tế" ↔ "Description") và dùng bản ghép.</summary>
    private string? Core(string n, int depth)
    {
        if (n.Length == 0 || depth > 3) return null;
        n = Expand(n);
        var exact = _map.GetValueOrDefault(n);
        var composed = Compose(n, depth);
        if (exact is null) return composed;
        return composed is not null && !SharesWord(exact, composed) ? composed : exact;
    }

    private string Expand(string n) =>
        string.Join(' ', n.Split(' ').Select(w => !_map.ContainsKey(w) && Abbr.TryGetValue(w, out var full) ? full : w));

    private static readonly HashSet<string> Minor = new(StringComparer.OrdinalIgnoreCase) { "of", "the", "no.", "no", "and", "&" };
    private static bool SharesWord(string a, string b)
    {
        static IEnumerable<string> W(string s) => Regex.Split(s.ToLowerInvariant(), "[^a-z.]+").Where(x => x.Length > 1 && !Minor.Contains(x));
        var set = W(a).ToHashSet();
        return W(b).Any(set.Contains);
    }

    /// <summary>Dịch theo tiền tố ("Mã X" → "X Code") hoặc ghép từng cụm (khớp dài nhất, không lấy nguyên cả câu) rồi đảo thứ tự kiểu tiếng Anh.</summary>
    private string? Compose(string n, int depth)
    {
        foreach (var (v, fmt) in _prefixes)
            if (n.StartsWith(v + " ", StringComparison.Ordinal) && Core(n[(v.Length + 1)..], depth + 1) is { } rest)
                return string.Format(fmt, Cap(rest));

        var words = n.Split(' ');
        if (words.Length < 2) return null;
        var parts = new List<string>();
        string? first = null;
        for (var i = 0; i < words.Length;)
        {
            var found = false;
            for (var k = Math.Min(6, words.Length - i); k >= 1; k--)
            {
                if (i == 0 && k == words.Length) continue;   // nguyên câu đã thử ở Core
                var phrase = string.Join(' ', words, i, k);
                if (!_map.TryGetValue(phrase, out var e)) continue;
                first ??= phrase;
                parts.Add(e); i += k; found = true;
                break;
            }
            if (!found) return null;
        }
        if (!Front.Contains(first)) parts.Reverse();   // tiếng Việt: chính trước, phụ sau; tiếng Anh ngược lại
        return string.Join(' ', parts.Select(Cap));
    }
}
