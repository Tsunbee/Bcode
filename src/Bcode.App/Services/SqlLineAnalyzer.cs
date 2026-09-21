using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Figures out which source lines of a script/procedure body are safe places for the debugger
/// (see RawSqlControl's "Debug store/function" / "Debug từng bước") to stop/step at — matches
/// FCode/SQLdebug.exe's own line-granularity stepping (their breakpoint dot and current-line
/// highlight both live on a source line, not on some internally-parsed "statement" concept).
///
/// A line is safe to stop at only when running the script text from the very start THROUGH
/// (and including) that line, as its own batch, would be a syntactically COMPLETE T-SQL batch —
/// never a half-finished one. Two things can make a line's end incomplete:
///   1. An open paren that hasn't closed yet (a multi-line EXEC(...) call's argument list, say)
///      — tracked directly by counting '(' / ')' outside strings/comments.
///   2. A statement that simply continues onto the next line without any open paren at all —
///      the single most common shape in this codebase's own procedures (a SELECT's column list
///      or a WHERE's AND-chain spilling across several lines, each one leading with a comma or
///      a clause keyword). There's no bracket to count here, so this is detected differently:
///      a line only counts as a real boundary when the NEXT non-blank/non-comment line begins
///      with a keyword that can legally START a brand new top-level statement (SELECT, SET,
///      DECLARE, EXEC, IF, BEGIN, ...) — never a clause-continuation word like FROM/WHERE/
///      GROUP/ORDER/HAVING/AND/OR/JOIN, which only ever continues whatever statement is already
///      open. Getting this wrong in the OTHER direction (declaring a mid-statement line "safe")
///      would hand SQL Server a truncated batch — usually just a clear syntax error (each Step
///      here runs inside a rolled-back transaction, so a bad guess costs nothing beyond that
///      error), but still confusing, so this is worth getting right rather than glossing over.
///
/// This is a client-side heuristic, not a real T-SQL parser — it doesn't understand CTEs
/// ("WITH x AS (...)"), doesn't special-case every clause keyword, and a sufficiently unusual
/// layout could still get a boundary slightly wrong. Verified against the multi-line SELECT/
/// WHERE/EXEC shapes actually seen in this codebase's own procedures (see the throwaway sandbox
/// test used to build this), but if a real procedure trips it up, the fix is either widening
/// BoundaryKeywords or (more robustly, at the cost of a round trip per candidate line)
/// confirming each candidate against SQL Server itself via "SET PARSEONLY ON" — the same
/// technique RawSqlService.CheckFieldsAsync already uses for "Check Fields" — instead of
/// trusting this heuristic alone.
/// </summary>
public static class SqlLineAnalyzer
{
    private static readonly Regex StringRegex = new(@"'([^']|'')*'", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LineCommentRegex = new(@"--[^\r\n]*", RegexOptions.Compiled);
    private static readonly Regex BlockCommentRegex = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Keywords that can legally START a brand-new top-level T-SQL statement.
    /// Deliberately excludes clause-continuation words (FROM/WHERE/GROUP/ORDER/HAVING/AND/OR/
    /// JOIN/...) — see the class doc-comment for why treating those as boundaries would be
    /// wrong.</summary>
    private static readonly HashSet<string> BoundaryKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "SET", "DECLARE", "IF", "ELSE", "WHILE",
        "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP", "TRUNCATE", "PRINT", "RAISERROR", "THROW",
        "RETURN", "BEGIN", "END", "WITH", "WAITFOR", "GOTO", "OPEN", "FETCH", "CLOSE",
        "DEALLOCATE", "COMMIT", "ROLLBACK", "USE",
    };

    /// <summary>Returns the 1-based line numbers of <paramref name="text"/> that are safe
    /// step/breakpoint targets, per the class doc-comment above. Lines with no real code at all
    /// (blank, or entirely a comment) are never included.</summary>
    public static HashSet<int> FindSafeLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var commentMask = MaskComments(normalized);
        var fullMask = MaskCommentsAndStrings(normalized, commentMask);
        var lines = normalized.Split('\n');

        var hasCode = new bool[lines.Length];
        var firstWord = new string?[lines.Length];
        var parenDepthAtEnd = new int[lines.Length];

        var pos = 0;
        var depth = 0;
        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            var foundFirst = false;
            for (var i = 0; i < line.Length; i++)
            {
                var abs = pos + i;
                var c = line[i];
                if (abs < commentMask.Length && commentMask[abs]) continue; // comment: never real code

                if (!char.IsWhiteSpace(c)) hasCode[li] = true;
                else continue;

                if (!foundFirst)
                {
                    foundFirst = true;
                    var insideString = abs < fullMask.Length && fullMask[abs] && !(abs < commentMask.Length && commentMask[abs]);
                    if (!insideString && (char.IsLetter(c) || c == '_' || c == '#'))
                    {
                        var start = i;
                        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_' || line[i] == '$' || line[i] == '#'))
                            i++;
                        firstWord[li] = line[start..i];
                        i--; // the for-loop will re-add 1
                    }
                }

                if (abs < fullMask.Length && fullMask[abs]) continue; // inside a string: '(' / ')' here don't count
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
            }
            parenDepthAtEnd[li] = depth;
            pos += line.Length + 1; // +1 for the '\n' split on
        }

        int NextRealLine(int li)
        {
            for (var j = li + 1; j < lines.Length; j++)
                if (hasCode[j]) return j;
            return -1;
        }

        var safe = new HashSet<int>();
        for (var li = 0; li < lines.Length; li++)
        {
            if (!hasCode[li]) continue;
            if (parenDepthAtEnd[li] != 0) continue; // still inside an open paren call

            var next = NextRealLine(li);
            var isLastRealLine = next == -1;
            var nextStartsNewStatement = next != -1 && firstWord[next] is { } w && BoundaryKeywords.Contains(w);

            if (isLastRealLine || nextStartsNewStatement)
                safe.Add(li + 1); // 1-based, matching RichTextBox.GetLineFromCharIndex + 1
        }
        return safe;
    }

    /// <summary>Marks characters that are // or /* */ comments — never real code, and their
    /// contents never count toward a leading keyword or paren depth.</summary>
    private static bool[] MaskComments(string text)
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
        return masked;
    }

    /// <summary>Comments plus string-literal characters — used for paren-depth tracking (a
    /// '(' typed inside a quoted string is just text, not an actual open paren) and to keep a
    /// leading string literal from being misread as a bare keyword. Unlike MaskComments, a
    /// string-literal-only line still counts as real code (see hasCode above) — it's part of a
    /// statement, just not one that starts with an identifier.</summary>
    private static bool[] MaskCommentsAndStrings(string text, bool[] commentMask)
    {
        var masked = (bool[])commentMask.Clone();
        foreach (Match m in StringRegex.Matches(text))
            for (var i = m.Index; i < m.Index + m.Length && i < masked.Length; i++)
                masked[i] = true;
        return masked;
    }
}
