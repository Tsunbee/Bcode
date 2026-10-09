using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Gợi ý header (v/e) cho cột khi "Tạo nhanh danh mục", không cần đọc source dự án. Ưu tiên:
/// <list type="number">
/// <item>Header bạn đã nhập lần trước cho đúng tên cột đó (tự nhớ mỗi lần Tạo file — %AppData%\Bcode\quicklist-headers.json).</item>
/// <item>Từ điển field chuẩn FCode (<see cref="FieldDictionaryService"/>: header.xml, parameters.xml).</item>
/// <item>Tên đầy đủ trong từ điển (<c>Templates\HeaderDictionary.json</c>, mục "columns": ghi_chu, status, datetime0...).</item>
/// <item>Ghép "tiền tố + danh từ": <c>ma_bp</c> = "Mã" + "bộ phận"; danh từ trùng tên bảng/khoá danh mục (vd <c>ma_quay</c> của bảng
///   <c>dmquay</c>) lấy từ Title ("Danh mục quầy" → "quầy"); số cuối giữ lại (<c>ma_td1</c> → "Mã tự do 1"); <c>ten_xxx2</c> = tên tiếng Anh.</item>
/// </list>
/// Không đoán được thì trả null (giữ nguyên tên cột). Từ điển là file JSON rời — thêm viết tắt riêng của dự án vào đó.
/// </summary>
public class ColumnHeaderGuesser
{
    public const string DictionaryFileName = @"Templates\HeaderDictionary.json";

    private sealed class Dict
    {
        public Dictionary<string, string[]> Columns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Tiền tố: [v, e-format] — e-format có {0} = danh từ tiếng Anh, vd "{0} Code".</summary>
        public Dictionary<string, string[]> Prefixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string[]> Nouns { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    private static string LearnedPath => Path.Combine(BcodePaths.AppData, "Bcode", "quicklist-headers.json");
    private static string DictionaryPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, DictionaryFileName);

    private readonly Dict _dict;
    private readonly Dictionary<string, string[]> _learned;

    public ColumnHeaderGuesser()
    {
        _dict = Load<Dict>(DictionaryPath) ?? new Dict();
        _dict.Columns = new(_dict.Columns, StringComparer.OrdinalIgnoreCase);
        _dict.Prefixes = new(_dict.Prefixes, StringComparer.OrdinalIgnoreCase);
        _dict.Nouns = new(_dict.Nouns, StringComparer.OrdinalIgnoreCase);
        _learned = new(Load<Dictionary<string, string[]>>(LearnedPath) ?? new(), StringComparer.OrdinalIgnoreCase);
    }

    private static T? Load<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOpts) : null; }
        catch { return null; }
    }

    /// <summary>Ngữ cảnh danh mục: tên bảng, các cột khoá, title v/e (để biết "quay" = "quầy" / "Counter").</summary>
    public record Context(string Table, IReadOnlyList<string> Keys, string TitleV, string TitleE);

    public (string V, string E)? Guess(string column, Context ctx)
    {
        if (_learned.TryGetValue(column, out var l) && l.Length >= 2) return (l[0], l[1]);
        if (FieldDictionaryService.Instance.Header(column) is { } fcode) return fcode;
        if (_dict.Columns.TryGetValue(column, out var c) && c.Length >= 2) return (c[0], c[1]);

        var m = Regex.Match(column, @"^([a-z]+)_([a-z_]*?[a-z])(\d*)$", RegexOptions.IgnoreCase);
        if (!m.Success || !_dict.Prefixes.TryGetValue(m.Groups[1].Value, out var prefix) || prefix.Length < 2) return null;
        var rest = m.Groups[2].Value;
        var number = m.Groups[3].Value;

        // ten_xxx2 = tên tiếng Anh của xxx (FastBusiness: ten_vt%l → ten_vt / ten_vt2).
        var english = number == "2" && m.Groups[1].Value.Equals("ten", StringComparison.OrdinalIgnoreCase);
        if (english) number = "";

        var noun = Noun(rest, ctx);
        if (noun == null) return null;
        var v = $"{prefix[0]} {noun.Value.V}".Trim();
        var e = string.Format(prefix[1], noun.Value.E).Trim();
        if (number != "") { v += " " + number; e += " " + number; }
        if (english) { v += " (tiếng Anh)"; e = "English " + e; }
        return (Cap(v), Cap(e));
    }

    private (string V, string E)? Noun(string rest, Context ctx)
    {
        if (IsEntity(rest, ctx))
        {
            var v = EntityV(ctx.TitleV);
            var e = EntityE(ctx.TitleE);
            if (v != "" || e != "") return (v, e);
        }
        if (_dict.Nouns.TryGetValue(rest, out var n) && n.Length >= 2) return (n[0], n[1]);

        // Danh từ ghép nhiều phần (vd "nhom_kh"): dịch từng phần, thiếu 1 phần thì bỏ.
        var parts = rest.Split('_');
        if (parts.Length < 2) return null;
        var vs = new List<string>(); var es = new List<string>();
        foreach (var p in parts)
        {
            if (!_dict.Nouns.TryGetValue(p, out var pn) || pn.Length < 2) return null;
            vs.Add(pn[0]); es.Add(pn[1]);
        }
        es.Reverse();   // nhom_kh → "nhóm khách hàng" / "Customer Group"
        return (string.Join(" ", vs), string.Join(" ", es));
    }

    /// <summary>Phần sau tiền tố trùng tên bảng (bỏ "dm") hoặc trùng phần sau "ma_" của khoá danh mục → chính là thực thể danh mục.</summary>
    private static bool IsEntity(string rest, Context ctx)
    {
        var table = Regex.Replace(ctx.Table, "^dm", "", RegexOptions.IgnoreCase);
        if (rest.Equals(table, StringComparison.OrdinalIgnoreCase)) return true;
        return ctx.Keys.Any(k => Regex.Replace(k, @"^ma_", "", RegexOptions.IgnoreCase).Equals(rest, StringComparison.OrdinalIgnoreCase));
    }

    private static string EntityV(string title)
    {
        var t = Regex.Replace(title.Trim(), @"^(danh\s+mục|dm)\s+", "", RegexOptions.IgnoreCase);
        return t.Length == 0 ? "" : char.ToLower(t[0]) + t[1..];
    }

    /// <summary>"List of Counters" / "Counter List" / "Counters" → "Counter".</summary>
    private static string EntityE(string title)
    {
        var t = Regex.Replace(title.Trim(), @"^list\s+of\s+|\s+list$", "", RegexOptions.IgnoreCase).Trim();
        return t.EndsWith('s') && !t.EndsWith("ss") ? t[..^1] : t;
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    /// <summary>Ghi nhớ header người dùng đã chốt (khác tên cột) để lần sau gợi ý đúng như vậy.</summary>
    public void Remember(IEnumerable<CatalogColumn> columns)
    {
        var changed = false;
        foreach (var c in columns)
        {
            if (string.IsNullOrWhiteSpace(c.HeaderV) || c.HeaderV == c.Name) continue;
            var value = new[] { c.HeaderV, string.IsNullOrWhiteSpace(c.HeaderE) || c.HeaderE == c.Name ? c.HeaderV : c.HeaderE };
            if (_learned.TryGetValue(c.Name, out var old) && old.SequenceEqual(value)) continue;
            _learned[c.Name] = value;
            changed = true;
        }
        if (!changed) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LearnedPath)!);
            File.WriteAllText(LearnedPath, JsonSerializer.Serialize(_learned, JsonOpts));
        }
        catch { /* chỉ là gợi ý — ghi không được thì thôi */ }
    }
}
