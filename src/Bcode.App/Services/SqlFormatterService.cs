using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bcode.App.Services;

/// <summary>
/// Backs "String Beauty": lightweight, dependency-free pretty-printers — not real parsers,
/// just enough to tidy up a pasted block. SQL supports 2 styles (Expanded: clause-based line
/// breaks/indent like before; Compact: whitespace collapsed to one line) — matching FCode's
/// own String Beauty having multiple beautify styles. JSON/XML use the .NET BCL's own
/// pretty-printer (System.Text.Json / XDocument) since those already produce valid, exact
/// results — no reason to hand-roll a parser for well-defined formats. JavaScript has no such
/// BCL helper, so FormatJavaScript is a small brace-counting indenter in the same spirit as
/// the SQL formatter's BEGIN/END handling — not a full parser, won't get every edge case
/// (regex literals, ASI edge cases) right, but tidies up typical pasted snippets.
/// </summary>
public class SqlFormatterService
{
    private static readonly string[] BreakBeforeKeywords =
    {
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING",
        "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "JOIN",
        "UNION ALL", "UNION", "SET", "VALUES", "INSERT INTO", "UPDATE",
        "DELETE FROM", "BEGIN", "END", "ELSE", "IF", "WHILE"
    };

    /// <param name="compact">false (default) = Expanded: tách dòng theo mệnh đề, thụt lề theo
    /// BEGIN/END (hành vi cũ). true = Compact: gộp về khoảng trắng đơn, giữ trên 1 dòng —
    /// dùng khi muốn dán gọn (vd vào 1 ô Excel) thay vì đọc theo cấu trúc.</param>
    public string Format(string sql, bool compact = false)
    {
        // 1) Bảo vệ chuỗi literal '...' (kể cả '' escape): KHÔNG được tách từ khóa nằm
        //    bên trong chuỗi (dynamic SQL), nếu không câu SELECT @x = '...' sẽ vỡ vụn.
        var literals = new List<string>();
        var masked = Regex.Replace(sql, @"'(?:[^']|'')*'", m =>
        {
            literals.Add(m.Value);
            return $"\u0001{literals.Count - 1}\u0002";
        });

        var normalized = Regex.Replace(masked, @"\s+", " ").Trim();

        if (compact)
        {
            var compactResult = Regex.Replace(normalized, "\u0001(\\d+)\u0002",
                m => literals[int.Parse(m.Groups[1].Value)]);
            return compactResult;
        }

        foreach (var kw in BreakBeforeKeywords.OrderByDescending(k => k.Length))
        {
            normalized = Regex.Replace(
                normalized,
                $@"(?<!\n)\b{Regex.Escape(kw)}\b",
                "\n" + kw,
                RegexOptions.IgnoreCase);
        }

        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder();
        var indent = 0;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // Chỉ BEGIN/END điều khiển thụt lề (cân bằng). IF/WHILE/ELSE chỉ xuống dòng,
            // KHÔNG tăng indent — nếu không mỗi IF một dòng sẽ thụt lề vô hạn.
            // Loại 'END' của CASE...END (theo sau là toán tử +,-,),=,, ) khỏi việc giảm indent.
            bool blockEnd = Regex.IsMatch(line, @"^END\b(?!\s*[-+*/%,)=])", RegexOptions.IgnoreCase);
            if (blockEnd) indent = Math.Max(0, indent - 1);

            sb.Append(new string(' ', indent * 4)).AppendLine(line);

            // BEGIN mở block: tăng indent — trừ khi BEGIN...END gói gọn trên cùng một dòng.
            if (line.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch(line, @"\bEND\b", RegexOptions.IgnoreCase))
            {
                indent++;
            }
        }

        var result = sb.ToString().TrimEnd();

        // 2) Khôi phục chuỗi literal.
        result = Regex.Replace(result, "\u0001(\\d+)\u0002",
            m => literals[int.Parse(m.Groups[1].Value)]);
        return result;
    }

    public string MinifyJson(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>Pretty-prints JSON via System.Text.Json — throws JsonException on invalid
    /// input, left to the caller (StringBeautyForm) to show as an error rather than silently
    /// returning the original text.</summary>
    public string FormatJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Pretty-prints XML via XDocument — throws on invalid/malformed XML, same
    /// error-to-caller contract as FormatJson.</summary>
    public string FormatXml(string xml) => XDocument.Parse(xml).ToString();

    /// <summary>Small brace-counting indenter for JavaScript — masks strings/template
    /// literals/comments first so braces/semicolons inside them are never mistaken for
    /// structure, then breaks a line after every "{"/";" and before every "}", indenting by
    /// nesting depth. Handles "} else {"/"} catch {" correctly (dedent for the leading "}",
    /// re-indent for the trailing "{"), same simple per-line heuristic as the SQL formatter's
    /// BEGIN/END handling above — not a real parser, so unusual constructs (regex literals,
    /// ASI-dependent code) may not come out perfectly.</summary>
    public string FormatJavaScript(string js)
    {
        var literals = new List<string>();
        var masked = Regex.Replace(js,
            @"(""(?:[^""\\]|\\.)*""|'(?:[^'\\]|\\.)*'|`(?:[^`\\]|\\.)*`|//[^\n]*|/\*[\s\S]*?\*/)",
            m =>
            {
                literals.Add(m.Value);
                return $"\u0001{literals.Count - 1}\u0002";
            });

        masked = masked.Replace("{", "{\n").Replace("}", "\n}\n").Replace(";", ";\n");

        var sb = new StringBuilder();
        var indent = 0;
        foreach (var raw in masked.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('}')) indent = Math.Max(0, indent - 1);
            sb.Append(new string(' ', indent * 4)).AppendLine(line);
            if (line.EndsWith('{')) indent++;
        }

        var result = sb.ToString().TrimEnd();
        result = Regex.Replace(result, "\u0001(\\d+)\u0002", m => literals[int.Parse(m.Groups[1].Value)]);
        return result;
    }
}