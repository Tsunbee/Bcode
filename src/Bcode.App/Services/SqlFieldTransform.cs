using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// "Change Field to…" trong menu chuột phải của SQL Query: biến đổi từng mục trong danh sách cột đang bôi đen
/// (vd <c>ma_kh, ten_kh, ghi_chu</c>) — thêm tiền tố alias, bọc MAX/MIN/SUM/…, ISNULL, [ ], AS tên cột, "a.x = b.x"...
/// Chỉ tách theo dấu phẩy ở mức ngoài cùng (không cắt trong ngoặc / chuỗi / [ ]); khoảng trắng và xuống dòng giữa các mục được giữ nguyên.
/// </summary>
public static class SqlFieldTransform
{
    private static readonly Regex Plain = new(@"^(?:(?<pre>[\w$#]+|\[[^\]]+\])\.)?(?<col>\[[^\]]+\]|[\w$#@]+)$", RegexOptions.Compiled);
    private static readonly Regex AsTail = new(@"^(?<expr>.+?)(?<as>\s+as\s+\[?[\w$#]+\]?)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex InnerCol = new(@"(?:[\w$#]+\.)?\[?(?<c>[\w$#]+)\]?\s*(?:,[^()]*)?\)*$", RegexOptions.Compiled);

    /// <summary>Tách thành (đầu khoảng trắng, nội dung, đuôi khoảng trắng, dấu phân cách thật sau mục — "," hoặc "" ở mục cuối).</summary>
    private static List<(string lead, string core, string trail, string sep)> Split(string s)
    {
        var parts = new List<(string, string, string, string)>();
        int depth = 0; char quote = '\0'; var cur = new StringBuilder();
        void Flush(string sep)
        {
            var t = cur.ToString(); cur.Clear();
            var core = t.Trim();
            var lead = t[..(t.Length - t.TrimStart().Length)];
            var trail = core.Length == 0 ? "" : t[(lead.Length + core.Length)..];
            parts.Add((lead, core, trail, sep));
        }
        foreach (var ch in s)
        {
            if (quote != '\0') { cur.Append(ch); if (ch == quote) quote = '\0'; continue; }
            if (ch == '\'' ) { quote = '\''; cur.Append(ch); continue; }
            if (ch == '[') { quote = ']'; cur.Append(ch); continue; }
            if (ch == '(') depth++; else if (ch == ')') depth = Math.Max(0, depth - 1);
            if (ch == ',' && depth == 0) { Flush(","); continue; }
            cur.Append(ch);
        }
        Flush("");
        return parts;
    }

    /// <summary>Áp <paramref name="f"/> lên phần biểu thức của từng mục (bỏ qua đuôi "AS tên" nếu có); mục rỗng giữ nguyên. <paramref name="joiner"/> thay dấu phẩy giữa các mục (null = giữ ",").</summary>
    private static string Map(string selection, Func<string, string> f, string? joiner = null)
    {
        var parts = Split(selection);
        var sb = new StringBuilder();
        for (var i = 0; i < parts.Count; i++)
        {
            var (lead, core, trail, sep) = parts[i];
            if (core.Length > 0)
            {
                var m = AsTail.Match(core);
                core = m.Success ? f(m.Groups["expr"].Value.Trim()) + m.Groups["as"].Value : f(core);
            }
            sb.Append(lead).Append(core).Append(trail);
            if (sep.Length > 0) sb.Append(joiner ?? sep);
        }
        return sb.ToString();
    }

    private static string? ColumnOf(string expr)
    {
        var m = Plain.Match(expr);
        if (m.Success) return m.Groups["col"].Value;
        var inner = InnerCol.Match(expr);
        return inner.Success ? inner.Groups["c"].Value : null;
    }

    /// <summary>Bọc: MAX(x), MIN(x), SUM(x), COUNT(x), AVG(x), LTRIM(RTRIM(x)), ISNULL(x, 0)... <paramref name="template"/> chứa {0}.</summary>
    public static string Wrap(string selection, string template) => Map(selection, e => string.Format(template, e));

    /// <summary>Thêm tiền tố alias ("a.") cho cột trơn (cột đã có tiền tố thì đổi sang tiền tố mới); biểu thức / hàm giữ nguyên.</summary>
    public static string Prefix(string selection, string prefix) => Map(selection, e =>
    {
        var m = Plain.Match(e);
        return m.Success && !m.Groups["col"].Value.StartsWith('@') ? prefix + m.Groups["col"].Value : e;
    });

    /// <summary>Bỏ tiền tố alias: a.ma_kh → ma_kh.</summary>
    public static string StripPrefix(string selection) => Map(selection, e =>
    {
        var m = Plain.Match(e);
        return m.Success && m.Groups["pre"].Success ? m.Groups["col"].Value : e;
    });

    /// <summary>[ma_kh]</summary>
    public static string Bracket(string selection) => Map(selection, e =>
    {
        var m = Plain.Match(e);
        if (!m.Success) return e;
        var col = m.Groups["col"].Value;
        return (m.Groups["pre"].Success ? m.Groups["pre"].Value + "." : "") + (col.StartsWith('[') ? col : "[" + col + "]");
    });

    /// <summary>@ma_kh (cột → biến).</summary>
    public static string Variable(string selection) => Map(selection, e =>
    {
        var m = Plain.Match(e);
        return m.Success && !m.Groups["col"].Value.StartsWith('@') ? "@" + m.Groups["col"].Value.Trim('[', ']') : e;
    });

    /// <summary>Thêm "AS tên cột" cho mục chưa có (MIN(a.x) → MIN(a.x) AS x).</summary>
    public static string AddAlias(string selection)
    {
        var parts = Split(selection);
        var sb = new StringBuilder();
        foreach (var (lead, core, trail, sep) in parts)
        {
            var c = core;
            if (c.Length > 0 && !AsTail.IsMatch(c) && !Plain.IsMatch(c))
            {
                var name = ColumnOf(c);
                if (name != null) c += " AS " + name;
            }
            sb.Append(lead).Append(c).Append(trail);
            if (sep.Length > 0) sb.Append(sep);
        }
        return sb.ToString();
    }

    /// <summary>"x = b.x" (vế SET của UPDATE, giữ dấu phẩy) hoặc "a.x = b.x" nối bằng AND (điều kiện ON / WHERE).</summary>
    public static string Compare(string selection, string left, string right, bool and)
    {
        return Map(selection, e =>
        {
            var m = Plain.Match(e);
            if (!m.Success) return e;
            var col = m.Groups["col"].Value;
            return left + col + " = " + right + col;
        }, and ? " AND" : null);
    }
}
