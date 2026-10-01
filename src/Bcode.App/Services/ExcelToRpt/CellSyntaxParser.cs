using System.Globalization;
using System.Text.RegularExpressions;

namespace Bcode.App.Services.ExcelToRpt;

/// <summary>
/// Cú pháp ô mẫu in (port của parse_cell_value / parse_concat / split_concat_cells):
/// <code>
///   ?name              -> nhãn / tham số
///   !1.field !2.field  -> trường dữ liệu bảng 1 / bảng 2
///   %c %l %r ...       -> cờ định dạng (c = số tiền, l = trái, r = phải)
///   #cond              -> điều kiện hiển thị (vd sysorder>1, sysorder=-9)
///   {b:cond}           -> điều kiện in đậm
///   #?a + + !1.b ...   -> biểu thức ghép
/// </code>
/// </summary>
internal static class CellSyntaxParser
{
    private static readonly Regex FieldRe = new(@"^!(\d+)\.(\w+)");
    private static readonly Regex StyleRe = new(@"\{([a-z]):([^}]*)\}");
    private static readonly Regex FormatRe = new(@"^%([a-z]+)");
    private static readonly Regex CondRe = new(@"^#(.+)$");

    /// <summary>
    /// Tách '#a + b + c' thành các mảnh. Toán hạng RỖNG giữa hai dấu '+' nghĩa là một DẤU
    /// CÁCH ('+ +' hoặc '++'). Rỗng = không có trường/tham số nào (để nhánh chữ thường xử lý).
    /// </summary>
    public static List<ConcatPart> ParseConcat(string raw)
    {
        var parts = new List<ConcatPart>();
        foreach (var tok in raw[1..].Split('+'))
        {
            var s = tok.Trim();
            if (s.Length == 0)
            {
                parts.Add(new ConcatPart { Type = "text", Text = " " });
                continue;
            }
            var m = FieldRe.Match(s);
            if (m.Success)
            {
                parts.Add(new ConcatPart { Type = "field", Table = ParseInt(m.Groups[1].Value), Name = m.Groups[2].Value });
                continue;
            }
            if (s.StartsWith('?'))
            {
                parts.Add(new ConcatPart { Type = "label", Name = s[1..].Trim() });
                continue;
            }
            parts.Add(new ConcatPart { Type = "text", Text = s });
        }

        var merged = new List<ConcatPart>();
        foreach (var p in parts)
        {
            if (p.Type == "text" && merged.Count > 0 && merged[^1].Type == "text")
                merged[^1].Text += p.Text;
            else
                merged.Add(p);
        }

        if (!merged.Any(p => p.Type is "field" or "label")) return new List<ConcatPart>();
        while (merged.Count > 0 && merged[0].Type == "text" && string.IsNullOrWhiteSpace(merged[0].Text)) merged.RemoveAt(0);
        while (merged.Count > 0 && merged[^1].Type == "text" && string.IsNullOrWhiteSpace(merged[^1].Text)) merged.RemoveAt(merged.Count - 1);
        return merged;
    }

    private static int ParseInt(string digits) =>
        int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : (int)char.GetNumericValue(digits, 0);

    /// <summary>Phân tích chuỗi trong ô (đã là str() của giá trị ô).</summary>
    public static ParsedCell ParseCellValue(string rawIn)
    {
        var raw = rawIn.Trim();
        var info = new ParsedCell { Raw = raw, FromCell = true };

        // Công thức Excel -> BỎ QUA: Crystal tham chiếu theo trường chứ không theo ô
        if (raw.StartsWith('='))
        {
            info.Kind = "skip";
            info.Reason = "cong thuc Excel";
            return info;
        }

        var sm = StyleRe.Match(raw);
        if (sm.Success)
        {
            info.StyleCondition = (sm.Groups[1].Value, sm.Groups[2].Value);
            raw = StyleRe.Replace(raw, "").Trim();
        }

        if (raw.StartsWith('?'))
        {
            info.Kind = "label";
            info.Name = raw[1..];
            return info;
        }

        if (raw.StartsWith('#') && (raw.Contains('+') || raw[1..].Contains('!') || raw[1..].Contains('?')))
        {
            var parts = ParseConcat(raw);
            if (parts.Count > 0)
            {
                info.Kind = "concat";
                info.Parts = parts;
                return info;
            }
            info.Kind = "skip";
            info.Reason = "bieu thuc ghep # khong doc duoc";
            return info;
        }

        var fm = FieldRe.Match(raw);
        if (fm.Success)
        {
            info.Kind = "field";
            info.Table = ParseInt(fm.Groups[1].Value);
            info.Name = fm.Groups[2].Value;
            var rest = raw[(fm.Index + fm.Length)..];
            var fmt = FormatRe.Match(rest);
            if (fmt.Success)
            {
                info.Format = fmt.Groups[1].Value;
                rest = rest[fmt.Length..];
            }
            var cond = CondRe.Match(rest);
            if (cond.Success) info.Condition = cond.Groups[1].Value;
            return info;
        }

        info.Kind = "text";
        info.Text = raw;
        return info;
    }

    /// <summary>
    /// Tách ô ghép thành các ô RIÊNG BIỆT đặt cạnh nhau trong đúng bề rộng cũ:
    /// <c>#?h_nguoi_van_chuyen +: + !1.ten_van_chuyen</c> -> [?h_nguoi_van_chuyen:] [!1.ten_van_chuyen].
    /// Gộp chung thì không kéo thả, căn lề hay định dạng số riêng cho phần giá trị được.
    /// Chữ + tham số liền nhau gộp một ô, mỗi trường một ô; bề rộng chia theo độ dài ước lượng.
    /// Trả về (số ô ghép đã tách, tổng số ô sinh ra) hoặc null.
    /// </summary>
    public static (int src, int output)? SplitConcatCells(SortedDictionary<int, List<LayoutItem>> rows)
    {
        int nSrc = 0, nOut = 0;
        foreach (var r in rows.Keys.ToList())
        {
            var output = new List<LayoutItem>();
            foreach (var it in rows[r])
            {
                var p = it.Parsed;
                if (p.Kind != "concat" || p.Parts is not { Count: > 0 })
                {
                    output.Add(it);
                    continue;
                }

                // --- Gom mảnh thành nhóm ---
                var groups = new List<List<ConcatPart>>();
                foreach (var part in p.Parts)
                {
                    if (part.Type == "field") groups.Add(new List<ConcatPart> { part });
                    else if (groups.Count > 0 && groups[^1][0].Type != "field") groups[^1].Add(part);
                    else groups.Add(new List<ConcatPart> { part });
                }

                static bool Blank(ConcatPart q) => q.Type == "text" && string.IsNullOrWhiteSpace(q.Text ?? "");
                foreach (var g in groups)
                {
                    while (g.Count > 1 && Blank(g[0])) g.RemoveAt(0);
                    while (g.Count > 1 && Blank(g[^1])) g.RemoveAt(g.Count - 1);
                }
                groups = groups.Where(g => !(g.Count == 1 && Blank(g[0]))).ToList();

                if (groups.Count < 2)
                {
                    output.Add(it);
                    continue;
                }

                static int Weight(List<ConcatPart> g)
                {
                    if (g[0].Type == "field") return Math.Max(12, (g[0].Name ?? "").Length);
                    var n = 0;
                    foreach (var q in g) n += q.Type == "text" ? (q.Text ?? "").Length : (q.Name ?? "").Length;
                    return Math.Max(3, n);
                }

                var weights = groups.Select(Weight).ToList();
                var total = weights.Sum();
                if (total == 0) total = 1;
                int x = it.X, left = it.Width;
                var bd = it.Border;

                for (var i = 0; i < groups.Count; i++)
                {
                    var g = groups[i];
                    var last = i == groups.Count - 1;
                    var w = last ? left : Math.Max(180, (int)((double)(it.Width * weights[i]) / total));
                    w = Math.Min(w, left);

                    var neu = it.Clone();
                    neu.X = x;
                    neu.Width = w;
                    neu.Border = new Dictionary<string, bool>
                    {
                        ["top"] = bd.GetValueOrDefault("top"),
                        ["bottom"] = bd.GetValueOrDefault("bottom"),
                        // Viền trái chỉ ô đầu, viền phải chỉ ô cuối — nếu không giữa nhãn và trị
                        // mọc thêm một nét dọc không có trong Excel
                        ["left"] = i == 0 && bd.GetValueOrDefault("left"),
                        ["right"] = last && bd.GetValueOrDefault("right"),
                    };
                    if (g[0].Type == "field")
                    {
                        neu.Parsed = new ParsedCell
                        {
                            Kind = "field", Table = g[0].Table, Name = g[0].Name,
                            Raw = $"!{g[0].Table}.{g[0].Name}",
                        };
                        neu.Align = string.IsNullOrEmpty(it.Align) ? "left" : it.Align;
                    }
                    else if (g.Count == 1 && g[0].Type == "label")
                    {
                        neu.Parsed = new ParsedCell { Kind = "label", Name = g[0].Name, Raw = "?" + g[0].Name };
                    }
                    else if (g.All(q => q.Type == "text"))
                    {
                        var txt = string.Concat(g.Select(q => q.Text ?? "")).Trim();
                        neu.Parsed = new ParsedCell { Kind = "text", Text = txt, Raw = txt };
                    }
                    else
                    {
                        // Nhãn kèm dấu câu (?ten + ":") — vẫn là ô ghép, vì tham số Crystal không
                        // nối thêm ký tự vào được
                        neu.Parsed = new ParsedCell
                        {
                            Kind = "concat", Parts = g,
                            Raw = "#" + string.Join(" + ", g.Select(q => q.Type == "label" ? "?" + q.Name : (q.Text ?? "").Trim())),
                        };
                    }
                    output.Add(neu);
                    x += w;
                    left -= w;
                    nOut++;
                }
                nSrc++;
            }
            rows[r] = output;
        }
        return nSrc > 0 ? (nSrc, nOut) : null;
    }
}
