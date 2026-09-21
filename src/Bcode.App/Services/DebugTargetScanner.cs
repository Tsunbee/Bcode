using System.Text.RegularExpressions;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>
/// Scans a script's text for EXEC calls and function() calls that reference a resolvable
/// stored procedure or function — backs "Debug store/function": FCode's own picker
/// ("Chọn store/function để debug") shows exactly this list so the user can jump straight to
/// debugging whichever one they actually meant, rather than guessing from a click position the
/// way Ctrl+Right-click does for the separate "append query to procedure" feature.
///
/// Every candidate this finds is confirmed against the database (ListObjectsAsync, filtered to
/// the expected SqlObjectKind) before being surfaced — the regexes below are deliberately loose
/// (especially CallRegex, which matches ANY "identifier(" it sees) because a false-positive
/// candidate just fails to resolve and gets silently dropped; a false negative (missing a real
/// call) is the worse failure mode for a picker whose whole job is "don't make me hunt for it
/// myself".
/// </summary>
public class DebugTargetScanner
{
    private readonly SqlObjectBrowserService _sqlObjectService;

    public DebugTargetScanner(SqlObjectBrowserService sqlObjectService) => _sqlObjectService = sqlObjectService;

    private static readonly Regex StringRegex = new(@"'([^']|'')*'", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LineCommentRegex = new(@"--[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex BlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ExecRegex = new(
        @"\bEXEC(?:UTE)?\b\s+(?:@\w+\s*=\s*)?(\[?[\w#$]+\]?(?:\.\[?[\w#$]+\]?)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "identifier(" — a possible function call. BuiltinNames below trims the obvious noise
    // (CAST, ISNULL, ...) before it ever reaches the database, but isn't relied on for
    // correctness — ResolveAsync's Kind == Function check is what actually decides.
    private static readonly Regex CallRegex = new(@"\b([\w]+(?:\.[\w]+)?)\s*\(", RegexOptions.Compiled);

    private static readonly HashSet<string> BuiltinNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CAST", "CONVERT", "ISNULL", "COALESCE", "COUNT", "SUM", "AVG", "MIN", "MAX", "LEN",
        "SUBSTRING", "REPLACE", "RTRIM", "LTRIM", "GETDATE", "GETUTCDATE", "DATEADD", "DATEDIFF",
        "DATENAME", "DATEPART", "CHARINDEX", "PATINDEX", "OBJECT_ID", "OBJECT_DEFINITION",
        "ROUND", "ABS", "FLOOR", "CEILING", "STUFF", "UPPER", "LOWER", "YEAR", "MONTH", "DAY",
        "TRY_CAST", "TRY_CONVERT", "IIF", "NEWID", "CHECKSUM", "EXEC", "EXECUTE", "IF", "WHILE",
        "CASE", "PRINT",
    };

    public async Task<List<DebugCandidate>> ScanAsync(string script, bool useSysDatabase)
    {
        var masked = Mask(script);
        var candidates = new List<DebugCandidate>();
        var seen = new HashSet<(int Line, string Name)>();

        foreach (Match m in ExecRegex.Matches(script))
        {
            if (IsMasked(masked, m.Index)) continue;
            var name = m.Groups[1].Value;
            var line = LineOf(script, m.Index);
            var obj = await ResolveAsync(name, useSysDatabase, SqlObjectKind.StoredProcedure);
            if (obj is null || !seen.Add((line, obj.QualifiedName))) continue;
            candidates.Add(new DebugCandidate { Line = line, Target = obj, CallText = CallTextFrom(script, m.Index) });
        }

        foreach (Match m in CallRegex.Matches(script))
        {
            if (IsMasked(masked, m.Index)) continue;
            var name = m.Groups[1].Value;
            var bare = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
            if (BuiltinNames.Contains(bare)) continue;
            var line = LineOf(script, m.Index);
            var obj = await ResolveAsync(name, useSysDatabase, SqlObjectKind.Function);
            if (obj is null || !seen.Add((line, obj.QualifiedName))) continue;
            candidates.Add(new DebugCandidate { Line = line, Target = obj, CallText = CallTextFrom(script, m.Index) });
        }

        return candidates.OrderBy(c => c.Line).ToList();
    }

    private async Task<SqlObjectInfo?> ResolveAsync(string identifier, bool useSysDatabase, SqlObjectKind kind)
    {
        var raw = identifier.Trim().Replace("[", "").Replace("]", "");
        var parts = raw.Split('.', 2);
        var schema = parts.Length == 2 ? parts[0] : null;
        var name = parts.Length == 2 ? parts[1] : parts[0];
        if (string.IsNullOrWhiteSpace(name)) return null;

        List<SqlObjectInfo> matches;
        try
        {
            matches = (await _sqlObjectService.ListObjectsAsync(useSysDatabase, name))
                .Where(o => o.Kind == kind && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return null; // no connection yet, or the name simply isn't a real object — either way, not a candidate
        }

        if (schema is not null)
        {
            var exact = matches.FirstOrDefault(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase));
            if (exact is not null) return exact;
        }
        return matches.FirstOrDefault();
    }

    private static bool[] Mask(string text)
    {
        var masked = new bool[text.Length];
        void Mark(MatchCollection matches)
        {
            foreach (Match m in matches)
                for (var i = m.Index; i < m.Index + m.Length && i < masked.Length; i++)
                    masked[i] = true;
        }
        Mark(LineCommentRegex.Matches(text));
        Mark(BlockCommentRegex.Matches(text));
        Mark(StringRegex.Matches(text));
        return masked;
    }

    private static bool IsMasked(bool[] masked, int index) => index < masked.Length && masked[index];

    private static int LineOf(string text, int index)
    {
        var line = 1;
        for (var i = 0; i < index && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    private static string CallTextFrom(string text, int startIndex)
    {
        var lineEnd = text.IndexOf('\n', startIndex);
        if (lineEnd < 0) lineEnd = text.Length;
        var raw = text[startIndex..lineEnd].TrimEnd('\r', '\n', ' ', '\t');
        return raw.Length > 120 ? raw[..117] + "..." : raw;
    }
}
