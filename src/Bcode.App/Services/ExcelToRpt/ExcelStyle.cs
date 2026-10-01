using System.Globalization;
using System.Text.RegularExpressions;

namespace Bcode.App.Services.ExcelToRpt;

/// <summary>
/// Giải mã MÀU SẮC và ĐỊNH DẠNG SỐ của ô Excel (port của excel_style.py).
///
/// - Màu: mẫu in gần như luôn khai màu theo CHỦ ĐỀ (theme) kèm tint, không khai mã màu —
///   phải tự tra bảng màu chủ đề của workbook rồi áp công thức tint của OOXML.
/// - Định dạng số: Excel khai kiểu kế toán dài '_(* #,##0.000_);...'; Crystal chỉ cần biết
///   mấy chữ số thập phân và có phân cách nghìn không, nên quy về dạng tối giản '#,##0.000'
///   để RptGenerator dịch tiếp.
/// </summary>
internal static class ExcelStyle
{
    // Thứ tự trong <a:clrScheme> của theme1.xml
    private static readonly string[] XmlOrder =
        { "dk1", "lt1", "dk2", "lt2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink" };

    // Chỉ số 'theme' của ô Excel đảo hai cặp đầu (0 = lt1, 1 = dk1, 2 = lt2, 3 = dk2). Nhầm chỗ
    // này thì chữ đen thành chữ trắng — không báo lỗi, chỉ mất chữ khi in.
    private static readonly int[] ExcelToXml = { 1, 0, 3, 2, 4, 5, 6, 7, 8, 9, 10, 11 };

    private static readonly string[] DefaultTheme =
        { "000000", "FFFFFF", "44546A", "E7E6E6", "4472C4", "ED7D31", "A5A5A5", "FFC000", "5B9BD5", "70AD47", "0563C1", "954F72" };

    // Bảng màu 'indexed' cổ (Excel 97). 64/65 là màu hệ thống -> không quy được.
    private static readonly string[] Indexed =
    {
        "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00", "FF00FF",
        "00FFFF", "000000", "FFFFFF", "FF0000", "00FF00", "0000FF", "FFFF00",
        "FF00FF", "00FFFF", "800000", "008000", "000080", "808000", "800080",
        "008080", "C0C0C0", "808080", "9999FF", "993366", "FFFFCC", "CCFFFF",
        "660066", "FF8080", "0066CC", "CCCCFF", "000080", "FF00FF", "FFFF00",
        "00FFFF", "800080", "800000", "008080", "0000FF", "00CCFF", "CCFFFF",
        "CCFFCC", "FFFF99", "99CCFF", "FF99CC", "CC99FF", "FFCC99", "3366FF",
        "33CCCC", "99CC00", "FFCC00", "FF9900", "FF6600", "666699", "969696",
        "003366", "339966", "003300", "333300", "993300", "993366", "333399",
        "333333",
    };

    private static readonly Regex HexRe = new("^[0-9A-F]{6}$");

    /// <summary>12 màu chủ đề, xếp theo chỉ số ô Excel dùng. Không có theme thì dùng chủ đề Office.</summary>
    public static string[] ThemePalette(string? themeXml)
    {
        if (string.IsNullOrEmpty(themeXml)) return (string[])DefaultTheme.Clone();

        var found = new Dictionary<string, string>();
        foreach (var slot in XmlOrder)
        {
            var m = Regex.Match(themeXml, $"<a:{slot}>(.*?)</a:{slot}>", RegexOptions.Singleline);
            if (!m.Success) continue;
            var body = m.Groups[1].Value;
            var c = Regex.Match(body, "srgbClr\\s+val=\"([0-9A-Fa-f]{6})\"");
            if (!c.Success) c = Regex.Match(body, "sysClr[^>]*lastClr=\"([0-9A-Fa-f]{6})\"");
            if (c.Success) found[slot] = c.Groups[1].Value.ToUpperInvariant();
        }
        var xml = XmlOrder.Select((s, i) => found.TryGetValue(s, out var v) ? v : DefaultTheme[i]).ToArray();
        return ExcelToXml.Select(i => xml[i]).ToArray();
    }

    /// <summary>Làm sáng/tối theo tint OOXML (ECMA-376: đổi sang HLS rồi kéo độ sáng).</summary>
    public static string ApplyTint(string hex6, double tint)
    {
        if (tint == 0) return hex6;
        double r = int.Parse(hex6[..2], NumberStyles.HexNumber) / 255.0;
        double g = int.Parse(hex6[2..4], NumberStyles.HexNumber) / 255.0;
        double b = int.Parse(hex6[4..6], NumberStyles.HexNumber) / 255.0;
        var (h, l, s) = RgbToHls(r, g, b);
        l = tint < 0 ? l * (1 + tint) : l * (1 - tint) + tint;
        (r, g, b) = HlsToRgb(h, Math.Min(1.0, Math.Max(0.0, l)), s);
        return $"{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
    }

    /// <summary>Phép % của float Python (dấu theo số chia).</summary>
    private static double PyMod(double a, double b)
    {
        var mod = a % b;                       // % của C# trên double = fmod
        if (mod != 0) { if ((b < 0) != (mod < 0)) mod += b; }
        else mod = b < 0 ? -0.0 : 0.0;
        return mod;
    }

    // colorsys của Python 3.14
    private static (double h, double l, double s) RgbToHls(double r, double g, double b)
    {
        var maxc = Math.Max(r, Math.Max(g, b));
        var minc = Math.Min(r, Math.Min(g, b));
        var sumc = maxc + minc;
        var rangec = maxc - minc;
        var l = sumc / 2.0;
        if (minc == maxc) return (0.0, l, 0.0);
        var s = l <= 0.5 ? rangec / sumc : rangec / (2.0 - maxc - minc);
        var rc = (maxc - r) / rangec;
        var gc = (maxc - g) / rangec;
        var bc = (maxc - b) / rangec;
        double h;
        if (r == maxc) h = bc - gc;
        else if (g == maxc) h = 2.0 + rc - bc;
        else h = 4.0 + gc - rc;
        h = PyMod(h / 6.0, 1.0);
        return (h, l, s);
    }

    private static (double, double, double) HlsToRgb(double h, double l, double s)
    {
        if (s == 0.0) return (l, l, l);
        var m2 = l <= 0.5 ? l * (1.0 + s) : l + s - (l * s);
        var m1 = 2.0 * l - m2;
        return (V(m1, m2, h + 1.0 / 3.0), V(m1, m2, h), V(m1, m2, h - 1.0 / 3.0));
    }

    private static double V(double m1, double m2, double hue)
    {
        hue = PyMod(hue, 1.0);
        if (hue < 1.0 / 6.0) return m1 + (m2 - m1) * hue * 6.0;
        if (hue < 0.5) return m2;
        if (hue < 2.0 / 3.0) return m1 + (m2 - m1) * (2.0 / 3.0 - hue) * 6.0;
        return m1;
    }

    /// <summary>Màu -> '#RRGGBB', hoặc null = không khai màu (để nguyên mặc định của template).</summary>
    public static string? ResolveColor(XColor? color, string[] palette)
    {
        if (color is null) return null;
        switch (color.Type)
        {
            case "rgb":
                var rgb = color.Rgb;
                if (rgb is null || rgb.Length < 6) return null;
                var hex6 = rgb[^6..].ToUpperInvariant();
                if (!HexRe.IsMatch(hex6)) return null;
                return "#" + ApplyTint(hex6, color.Tint);
            case "theme":
                if (color.Theme < 0 || color.Theme >= palette.Length) return null;
                return "#" + ApplyTint(palette[color.Theme], color.Tint);
            case "indexed":
                if (color.Indexed < 0 || color.Indexed >= Indexed.Length) return null;
                return "#" + ApplyTint(Indexed[color.Indexed], color.Tint);
            default:
                return null;
        }
    }

    /// <summary>Màu chữ, hoặc null nếu là đen mặc định.</summary>
    public static string? FontColor(XCell cell, string[] palette)
    {
        var c = ResolveColor(cell.Style.Font.Color, palette);
        return c is null or "#000000" ? null : c;
    }

    /// <summary>Màu nền, hoặc null. Bỏ qua nền TRẮNG: trong Crystal ô có nền là mảng đặc che đường kẻ.</summary>
    public static string? FillColor(XCell cell, string[] palette)
    {
        var fill = cell.Style.Fill;
        if (fill.PatternType is null) return null;
        var c = ResolveColor(fill.FgColor, palette);
        return c is null or "#FFFFFF" ? null : c;
    }

    /// <summary>Màu đường kẻ: cạnh đầu tiên có khai màu. null = đen mặc định.</summary>
    public static string? BorderColor(XCell cell, string[] palette)
    {
        foreach (var side in new[] { "bottom", "top", "left", "right" })
        {
            var sd = cell.Style.Border.Get(side);
            if (sd?.Style is null) continue;
            var c = ResolveColor(sd.Color, palette);
            if (c is not null && c != "#000000") return c;
        }
        return null;
    }

    public static bool Underline(XCell cell) => !string.IsNullOrEmpty(cell.Style.Font.Underline);

    // -----------------------------------------------------------------------
    // Định dạng số
    // -----------------------------------------------------------------------

    private static readonly Regex HiddenRe = new(@"^\s*;\s*;\s*;\s*$");
    private const string Placeholder = "0#?";

    /// <summary>Tách phần ĐẦU (số dương) của chuỗi định dạng thành [(ph|lit, ký tự)].</summary>
    private static List<(bool ph, char ch)> Tokens(string section)
    {
        var output = new List<(bool, char)>();
        int i = 0, n = section.Length;
        while (i < n)
        {
            var ch = section[i];
            if (ch == '\\')
            {
                if (i + 1 < n) output.Add((false, section[i + 1]));
                i += 2;
                continue;
            }
            if (ch == '"')
            {
                var j = section.IndexOf('"', i + 1);
                if (j < 0) break;
                foreach (var c in section[(i + 1)..j]) output.Add((false, c));
                i = j + 1;
                continue;
            }
            if (ch is '_' or '*') { i += 2; continue; }
            if (ch == '[')
            {
                var j = section.IndexOf(']', i + 1);
                i = j < 0 ? n : j + 1;
                continue;
            }
            if (ch == ';') break;
            output.Add((Placeholder.Contains(ch) || "ymdhs".Contains(ch), ch));
            i++;
        }
        return output;
    }

    /// <summary>'m' là THÁNG, trừ khi đi ngay sau giờ hoặc ngay trước giây.</summary>
    private static bool IsMinute(List<(bool ph, char ch)> toks, int k)
    {
        for (var j = k - 1; j >= 0; j--)
        {
            if (!toks[j].ph) continue;
            return toks[j].ch is 'h' or 's';
        }
        for (var j = k + 1; j < toks.Count; j++)
        {
            if (!toks[j].ph) continue;
            return toks[j].ch == 's';
        }
        return false;
    }

    /// <summary>Chuỗi định dạng Excel -> (pattern tối giản | null, hidden = khai ';;;').</summary>
    public static (string? pattern, bool hidden) CrystalNumberFormat(string? fmt)
    {
        if (string.IsNullOrEmpty(fmt) || fmt == "General") return (null, false);
        if (HiddenRe.IsMatch(fmt)) return (null, true);

        var toks = Tokens(fmt);
        var phs = toks.Where(t => t.ph).Select(t => t.ch).ToList();
        if (phs.Count == 0) return (null, false);

        // --- Ngày / giờ ---
        if (phs.Any(c => "ymdhs".Contains(c)))
        {
            var parts = new List<string>();
            char? sep = null;
            var k = 0;
            while (k < toks.Count)
            {
                var (ph, ch) = toks[k];
                if (!ph || !"ymdhs".Contains(ch))
                {
                    if (!ph && sep is null && parts.Count > 0 && !char.IsWhiteSpace(ch)) sep = ch;
                    k++;
                    continue;
                }
                var j = k;
                while (j < toks.Count && toks[j] == (ph, ch)) j++;
                var run = j - k;
                var outCh = ch == 'm' ? (IsMinute(toks, k) ? 'n' : 'M') : ch;
                parts.Add(new string(outCh, run));
                k = j;
            }
            var dateParts = parts.Where(p => "yMd".Contains(p[0])).ToList();
            if (dateParts.Count == 0) return (null, false);
            return (string.Join((sep ?? '/').ToString(), dateParts), false);
        }

        // --- Số ---
        var dec = 0;
        for (var k = 0; k < toks.Count; k++)
        {
            if (toks[k].ch != '.') continue;
            for (var j = k + 1; j < toks.Count; j++)
            {
                if (toks[j].ph && Placeholder.Contains(toks[j].ch)) dec++;
                else break;
            }
            break;
        }

        var grouped = false;
        for (var k = 1; k < toks.Count - 1; k++)
        {
            var (ph, ch) = toks[k];
            if (!ph && (ch == ',' || ch == ' ') && toks[k - 1].ph && toks[k + 1].ph)
            {
                grouped = true;
                break;
            }
        }

        var head = grouped ? "#,##0" : "0";
        return (dec > 0 ? head + "." + new string('0', dec) : head, false);
    }
}
