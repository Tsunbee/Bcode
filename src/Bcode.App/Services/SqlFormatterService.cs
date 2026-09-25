using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// "String Beauty" tool: a lightweight, dependency-free SQL pretty-printer.
/// Not a full parser — it breaks on major clause keywords and re-indents,
/// which covers the common case of tidying a stored-procedure body or an
/// ad-hoc query pasted from somewhere else.
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

    public string Format(string sql)
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
}
