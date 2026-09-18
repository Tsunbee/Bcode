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
        var normalized = Regex.Replace(sql, @"\s+", " ").Trim();

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
            if (line.StartsWith("END", StringComparison.OrdinalIgnoreCase)) indent = Math.Max(0, indent - 1);

            sb.Append(new string(' ', indent * 4)).AppendLine(line);

            if (line.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("IF", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("WHILE", StringComparison.OrdinalIgnoreCase))
            {
                indent++;
            }
        }

        return sb.ToString().TrimEnd();
    }

    public string MinifyJson(string s) => Regex.Replace(s, @"\s+", " ").Trim();
}
