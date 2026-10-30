using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Kho tìm kiếm của Hỏi Fast (H1): mọi kiến thức thành "tài liệu" xếp hạng bằng BM25 — menu báo cáo / chức năng (Sys Data wcommand), store / function,
/// mẫu code, quy ước, bảng, field. Từ đồng nghĩa tự rút (H2) gắn vào tài liệu: store báo cáo mang luôn tiêu đề menu tiếng Việt (rs_rptStockCard ↔
/// "Thẻ kho/Sổ chi tiết vật tư"), bảng danh mục mang tiêu đề field tra cứu vào nó (dmkho ↔ "Kho, Kho nhập, Kho xuất"), bảng phiếu / chi tiết mang tên
/// chứng từ (dmct), tên tiếng Anh tách theo chữ hoa (StockCard → stock card).
/// </summary>
public sealed class FastAskIndex
{
    /// <summary>1 tài liệu: loại (report / menu / store / snippet / topic / table / field), khoá (tên store / prefix mẫu / tên bảng / tên field…), tiêu đề, phần mô tả.</summary>
    public sealed record Doc(string Kind, string Key, string Title, string Body, string Extra = "");

    private readonly List<Doc> _docs = new();
    private readonly List<Dictionary<string, int>> _tf = new();
    private readonly List<int> _len = new();
    private readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);
    private double _avg;
    private const double K1 = 1.2, B = 0.75;

    public int Count => _docs.Count;

    /// <summary>Thêm tài liệu; <paramref name="titleWeight"/> = số lần lặp tiêu đề (tiêu đề khớp quan trọng hơn mô tả).</summary>
    public void Add(Doc d, int titleWeight = 2)
    {
        var terms = Terms(string.Join(" ", Enumerable.Repeat(d.Title, titleWeight)) + " " + d.Body);
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in terms) tf[t] = tf.GetValueOrDefault(t) + 1;
        foreach (var t in tf.Keys) _df[t] = _df.GetValueOrDefault(t) + 1;
        _docs.Add(d); _tf.Add(tf); _len.Add(terms.Count);
        _avg = _len.Average();
    }

    /// <summary>Từ khoá: chữ không dấu, tên tiếng Anh tách theo chữ hoa / gạch dưới, cộng cặp 2 từ liền nhau (tiếng Việt nhiều từ 2 âm tiết: "the kho", "vat tu").</summary>
    public static List<string> Terms(string text)
    {
        var split = Regex.Replace(text, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        var words = Regex.Matches(FastAskService.Fold(split).Replace('_', ' ').Replace('$', ' '), @"[a-z0-9#%@]+").Select(m => m.Value).Where(w => !Stop.Contains(w)).ToList();
        var res = new List<string>(words);
        for (var i = 1; i < words.Count; i++) res.Add(words[i - 1] + "_" + words[i]);
        return res;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "va", "cua", "cho", "cac", "nhung", "mot", "la", "co", "duoc",   // "khong" giữ lại: câu báo lỗi "không hợp lệ" / "không tồn tại" "theo", "tu", "den", "ve", "trong", "voi", "nao", "gi", "nhu", "hay", "minh", "toi",
        "xem", "tra", "tim", "giup", "nhe", "a", "di", "of", "for", "by", "and", "to", "in", "on", "dbo",
    };

    /// <summary>Top <paramref name="top"/> tài liệu theo BM25 (lọc loại nếu cần); điểm 0 thì bỏ.</summary>
    public List<(Doc Doc, double Score)> Search(string query, int top = 8, Func<Doc, bool>? filter = null)
    {
        var q = Terms(query).Distinct().ToList();
        var res = new List<(Doc, double)>();
        if (q.Count == 0) return res;
        for (var i = 0; i < _docs.Count; i++)
        {
            if (filter is not null && !filter(_docs[i])) continue;
            double s = 0;
            foreach (var t in q)
            {
                if (!_tf[i].TryGetValue(t, out var f)) continue;
                var idf = Math.Log(1 + (_docs.Count - _df[t] + 0.5) / (_df[t] + 0.5));
                // cặp 2 từ khớp đúng cụm → nặng gấp đôi
                s += (t.Contains('_') ? 2 : 1) * idf * f * (K1 + 1) / (f + K1 * (1 - B + B * _len[i] / _avg));
            }
            if (s > 0) res.Add((_docs[i], s));
        }
        return res.OrderByDescending(x => x.Item2).Take(top).ToList();
    }
}
