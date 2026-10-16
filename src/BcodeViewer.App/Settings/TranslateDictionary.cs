using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BcodeViewer.App.Settings;

/// <summary>
/// Từ điển dịch Việt → Anh của "Dịch caption (v → e)": tra ở đây TRƯỚC, chỉ caption không tra được mới gửi Google/Gemini/Claude
/// (<see cref="Host.CaptionTranslator"/>). Hai lớp:
/// <list type="bullet">
/// <item><b>Từ của người dùng</b> — %AppData%\Bcode\viewer-translate-dict.json, sửa ở hộp "Từ điển dịch" (Tool → Từ điển dịch...) hoặc
/// nút "Lưu vào từ điển" của hộp Dịch caption. Luôn thắng từ có sẵn.</item>
/// <item><b>Từ có sẵn</b> (chỉ đọc, tắt được) — cùng nguồn với HeaderTranslator của Bcode: cặp header/header2 trong header.xml,
/// Templates\HeaderDictionary.json (columns/nouns/prefixes), tiêu đề Bcode đã học (quicklist-headers.json) và bộ từ kế toán cơ bản.</item>
/// </list>
/// So khớp: không phân biệt hoa/thường, khoảng trắng thừa, ":" "." cuối; không khớp có dấu thì thử bản bỏ dấu (chỉ khi không nhập nhằng).
/// Không khớp nguyên câu thì (nếu bật) ghép theo cụm dài nhất + tiền tố "Mã X" → "X Code", đảo thứ tự kiểu tiếng Anh như HeaderTranslator;
/// còn từ nào không biết thì coi như không có (để engine dịch).
/// </summary>
public sealed class TranslateDictionary
{
    public sealed class Entry
    {
        public string Vi { get; set; } = "";
        public string En { get; set; } = "";
    }

    private sealed class FileModel
    {
        public bool UseBuiltin { get; set; } = true;
        public bool Compose { get; set; } = true;
        public List<Entry> Entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Lazy<TranslateDictionary> _instance = new(() => new TranslateDictionary());
    public static TranslateDictionary Instance => _instance.Value;

    public static string FilePath => Path.Combine(BcodePaths.AppData, "Bcode", "viewer-translate-dict.json");

    private readonly object _gate = new();
    private FileModel _model = new();
    private DateTime _loadedStamp = DateTime.MinValue;
    private Dictionary<string, string> _user = new(StringComparer.Ordinal);
    private Dictionary<string, string?> _userBare = new(StringComparer.Ordinal);
    private Dictionary<string, string>? _builtin;
    private Dictionary<string, string?>? _builtinBare;
    private readonly List<(string V, string Format)> _prefixes = new();

    private TranslateDictionary() { }

    // ---- Tra từ ------------------------------------------------------------------------------

    /// <summary>Bản tiếng Anh của caption Việt, null nếu từ điển không có (kể cả ghép cụm). <paramref name="source"/> = "user" | "builtin".</summary>
    public string? Lookup(string? vietnamese, out string source)
    {
        source = "";
        var key = Norm(vietnamese);
        if (key.Length == 0) return null;
        lock (_gate)
        {
            EnsureLoaded();
            var hit = Core(key, 0, out source);
            if (hit is null) return null;
            // Giữ dấu ":" cuối của caption gốc ("Mã khách:" → "Customer Code:").
            return (vietnamese ?? "").TrimEnd().EndsWith(':') && !hit.EndsWith(':') ? hit + ":" : hit;
        }
    }

    private string? Core(string n, int depth, out string source)
    {
        source = "";
        if (n.Length == 0 || depth > 3) return null;
        if (_user.TryGetValue(n, out var u)) { source = "user"; return u; }
        n = Expand(n);
        if (_user.TryGetValue(n, out u)) { source = "user"; return u; }
        string? exact = null;
        if (_model.UseBuiltin && _builtin!.TryGetValue(n, out var b)) exact = Cap(b);
        // Bỏ dấu: chỉ khi caption gõ KHÔNG dấu ("Ma khach hang") — caption có dấu mà tra bỏ dấu dễ khớp nhầm từ khác.
        var bare = Bare(n); var bareOk = bare == n;
        if (exact is null && bareOk && _userBare.TryGetValue(bare, out var ub) && ub is not null) { source = "user"; return ub; }
        if (exact is null && bareOk && _model.UseBuiltin && _builtinBare!.TryGetValue(bare, out var bb) && bb is not null) exact = Cap(bb);

        string? composed = null;
        var composedSrc = "";
        if (_model.Compose) composed = Compose(n, depth, out composedSrc);
        if (exact is null) { source = composed is null ? "" : composedSrc; return composed; }
        // Như HeaderTranslator: cặp có sẵn mà không chung từ nào với bản ghép → coi là nhập sai trong source, dùng bản ghép.
        if (composed is not null && !SharesWord(exact, composed)) { source = composedSrc; return composed; }
        source = "builtin";
        return exact;
    }

    /// <summary>Tra 1 cụm (không ghép): người dùng trước, có sẵn sau; có dấu trước, bỏ dấu sau.</summary>
    private string? Phrase(string p, ref bool usedUser)
    {
        if (_user.TryGetValue(p, out var u)) { usedUser = true; return u; }
        if (_model.UseBuiltin && _builtin!.TryGetValue(p, out var b)) return b;
        var bare = Bare(p);
        if (bare != p) return null;   // cụm có dấu: không tra bỏ dấu (xem Core)
        if (_userBare.TryGetValue(bare, out var ub) && ub is not null) { usedUser = true; return ub; }
        if (_model.UseBuiltin && _builtinBare!.TryGetValue(bare, out var bb) && bb is not null) return bb;
        return null;
    }

    private string? Compose(string n, int depth, out string source)
    {
        source = "";
        var bareInput = Bare(n) == n;   // caption không dấu: so tiền tố cũng bỏ dấu ("ma khach hang" → "Mã {0}")
        foreach (var (pv, fmt) in _model.UseBuiltin ? _prefixes : new())
            if ((bareInput ? Bare(pv) : pv) is var v && n.StartsWith(v + " ", StringComparison.Ordinal)
                && Core(n[(v.Length + 1)..], depth + 1, out var restSrc) is { } rest)
            {
                source = restSrc == "user" ? "user" : "builtin";
                return string.Format(fmt, Cap(rest));
            }

        var words = n.Split(' ');
        if (words.Length < 2) return null;
        var parts = new List<string>();
        string? first = null;
        var usedUser = false;
        for (var i = 0; i < words.Length;)
        {
            var found = false;
            for (var k = Math.Min(8, words.Length - i); k >= 1; k--)
            {
                if (i == 0 && k == words.Length) continue;   // nguyên câu đã thử ở Core
                var phrase = string.Join(' ', words, i, k);
                if (Phrase(phrase, ref usedUser) is not { } e) continue;
                first ??= phrase;
                parts.Add(e); i += k; found = true;
                break;
            }
            if (!found) return null;
        }
        if (!Front.Contains(first)) parts.Reverse();   // tiếng Việt: chính trước, phụ sau; tiếng Anh ngược lại
        source = usedUser ? "user" : "builtin";
        return string.Join(' ', parts.Select(Cap));
    }

    private string Expand(string n) =>
        string.Join(' ', n.Split(' ').Select(w => !_user.ContainsKey(w) && !(_builtin?.ContainsKey(w) ?? false) && Abbr.TryGetValue(w, out var full) ? full : w));

    // ---- Nạp / lưu ---------------------------------------------------------------------------

    /// <summary>Nạp lại file người dùng khi file đổi trên đĩa (sửa tay bằng Notepad cũng ăn); từ có sẵn nạp 1 lần.</summary>
    private void EnsureLoaded()
    {
        if (_builtin is null) LoadBuiltin();
        DateTime stamp;
        try { stamp = File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue; }
        catch { stamp = DateTime.MinValue; }
        if (stamp == _loadedStamp && _loadedStamp != DateTime.MinValue) return;
        _loadedStamp = stamp;
        FileModel model = new();
        try
        {
            if (File.Exists(FilePath))
                model = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(FilePath), JsonOpts) ?? new FileModel();
        }
        catch { model = new FileModel(); /* file hỏng: dùng như trống, không ghi đè cho tới khi người dùng bấm Lưu */ }
        Apply(model);
    }

    private void Apply(FileModel model)
    {
        model.Entries = model.Entries.Where(e => Norm(e.Vi).Length > 0 && (e.En ?? "").Trim().Length > 0)
            .Select(e => new Entry { Vi = e.Vi.Trim(), En = e.En.Trim() }).ToList();
        _model = model;
        _user = new(StringComparer.Ordinal);
        foreach (var e in model.Entries) _user[Norm(e.Vi)] = e.En;   // trùng khoá: dòng sau thắng
        _userBare = BareIndex(_user);
    }

    private static Dictionary<string, string?> BareIndex(Dictionary<string, string> map)
    {
        var bare = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in map)
        {
            var b = Bare(k);
            // Nhiều từ có dấu khác nhau cùng ra 1 bản bỏ dấu ("có"/"cổ") mà dịch khác nhau → nhập nhằng, không dùng.
            if (bare.TryGetValue(b, out var old)) { if (old is not null && !string.Equals(old, v, StringComparison.OrdinalIgnoreCase)) bare[b] = null; }
            else bare[b] = v;
        }
        return bare;
    }

    private void LoadBuiltin()
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
        foreach (var f in FcodeFieldDictionary.Load(FcodeFieldDictionary.DefaultPath)) Add(f.HeaderV, f.HeaderE);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "fields", "HeaderDictionary.json");
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
        catch { /* chưa có tiêu đề Bcode đã học */ }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (v, m) in counts)
            map[v] = m.OrderByDescending(x => x.Value).ThenBy(x => x.Key.Length).First().Key;
        _prefixes.Sort((a, b) => b.V.Length.CompareTo(a.V.Length));
        _builtin = map;
        _builtinBare = BareIndex(map);
    }

    /// <summary>JSON cho hộp "Từ điển dịch": {path, useBuiltin, compose, entries:[{vi,en}], builtin:[{vi,en}]?}.</summary>
    public string GetJson(bool includeBuiltin)
    {
        lock (_gate)
        {
            EnsureLoaded();
            return JsonSerializer.Serialize(new
            {
                path = FilePath,
                useBuiltin = _model.UseBuiltin,
                compose = _model.Compose,
                entries = _model.Entries,
                builtinCount = _builtin!.Count,
                builtin = includeBuiltin ? _builtin.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new Entry { Vi = x.Key, En = x.Value }) : null,
            }, JsonOpts);
        }
    }

    /// <summary>Ghi đè toàn bộ (hộp "Từ điển dịch" bấm Lưu): {useBuiltin, compose, entries}. Trả "" hoặc "Lỗi: ...".</summary>
    public string Save(string json)
    {
        try
        {
            var model = JsonSerializer.Deserialize<FileModel>(json, JsonOpts) ?? new FileModel();
            lock (_gate)
            {
                EnsureLoaded();
                Apply(model);
                Write();
            }
            return "";
        }
        catch (Exception ex) { return "Lỗi lưu từ điển dịch: " + ex.Message; }
    }

    /// <summary>Thêm/cập nhật các cặp [{vi,en}] (nút "Lưu vào từ điển" của hộp Dịch caption). Cùng khoá thì sửa bản dịch tại chỗ.
    /// Trả số cặp mới/đổi hoặc "Lỗi: ...".</summary>
    public string Add(string json)
    {
        try
        {
            var add = JsonSerializer.Deserialize<List<Entry>>(json, JsonOpts) ?? new();
            var changed = 0;
            lock (_gate)
            {
                EnsureLoaded();
                var list = _model.Entries.ToList();
                foreach (var e in add)
                {
                    var key = Norm(e.Vi);
                    var en = (e.En ?? "").Trim();
                    if (key.Length == 0 || en.Length == 0) continue;
                    var i = list.FindIndex(x => Norm(x.Vi) == key);
                    if (i >= 0) { if (list[i].En != en) { list[i] = new Entry { Vi = list[i].Vi, En = en }; changed++; } }
                    else { list.Add(new Entry { Vi = e.Vi.Trim().TrimEnd(':').Trim(), En = en.TrimEnd(':').Trim() }); changed++; }
                }
                if (changed > 0)
                {
                    Apply(new FileModel { UseBuiltin = _model.UseBuiltin, Compose = _model.Compose, Entries = list });
                    Write();
                }
            }
            return changed.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) { return "Lỗi lưu từ điển dịch: " + ex.Message; }
    }

    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_model, JsonOpts), new UTF8Encoding(false));
        File.Move(tmp, FilePath, true);
        _loadedStamp = File.GetLastWriteTimeUtc(FilePath);
    }

    // ---- Chuẩn hoá ---------------------------------------------------------------------------

    public static string Norm(string? s) => Regex.Replace((s ?? "").Trim().TrimEnd(':', '.').Trim(), @"\s+", " ").ToLowerInvariant();

    /// <summary>Bỏ dấu tiếng Việt (đ → d) của khoá đã chuẩn hoá.</summary>
    private static string Bare(string s)
    {
        var d = s.Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static bool IsEnglish(string e) =>
        e.Length > 0 && Regex.IsMatch(e, "[A-Za-z]") && !Regex.IsMatch(e, "[À-ỹđĐ]") && !e.Contains("??");

    private static readonly HashSet<string> Minor = new(StringComparer.OrdinalIgnoreCase) { "of", "the", "no.", "no", "and", "&" };
    private static bool SharesWord(string a, string b)
    {
        static IEnumerable<string> W(string s) => Regex.Split(s.ToLowerInvariant(), "[^a-z.]+").Where(x => x.Length > 1 && !Minor.Contains(x));
        var set = W(a).ToHashSet();
        return W(b).Any(set.Contains);
    }

    // ---- Từ vựng gốc — chép từ Bcode.App/Services/HeaderTranslator.cs (2 project không tham chiếu nhau) ----

    private static readonly string[] Front = { "tổng", "tổng cộng", "cộng" };

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

    private static readonly Dictionary<string, string> Abbr = new(StringComparer.Ordinal)
    {
        ["hđ"] = "hợp đồng", ["hd"] = "hợp đồng", ["tt"] = "thanh toán", ["sl"] = "số lượng", ["tk"] = "tài khoản", ["nt"] = "ngoại tệ", ["kh"] = "khách hàng",
        ["ncc"] = "nhà cung cấp", ["vt"] = "vật tư", ["c.từ"] = "chứng từ", ["ct"] = "chứng từ", ["đvt"] = "đơn vị tính", ["ck"] = "chiết khấu",
        ["sp"] = "sản phẩm", ["nv"] = "nhân viên", ["bp"] = "bộ phận", ["vv"] = "vụ việc", ["gtgt"] = "giá trị gia tăng",
    };
}
