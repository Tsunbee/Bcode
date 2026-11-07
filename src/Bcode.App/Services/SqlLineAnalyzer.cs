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
        var safe = FindSafeLinesByParens(normalized);
        // Lọc thêm: dòng mà câu lệnh còn DỞ ở cuối dòng (INSERT chưa có SELECT/VALUES/EXEC, UPDATE chưa có SET, CTE chưa có câu chính,
        // CURSOR FOR chưa có SELECT, UNION/EXCEPT chờ SELECT, IF/WHILE/ELSE chưa có câu thân, BEGIN/END TRY, BEGIN CATCH, MERGE chưa có ;)
        // — cắt batch ở đó là lỗi cú pháp (đo trên 8.536 store thật: phần lớn bước hỏng rơi vào các dạng này).
        var incomplete = IncompleteLineEnds(normalized);
        incomplete.UnionWith(LinesEndingInsideStringOrComment(normalized));
        safe.RemoveWhere(l => incomplete.Contains(l));
        return safe;
    }

    // Mọi token (tên, số, dấu nháy của chuỗi đã mask, toán tử…) — để "từ cuối dòng" đúng: "ON 1 = 0" kết thúc bằng 0, không phải ON.
    private static readonly Regex Token = new(@"(?<![\w@#$])[A-Za-z_@#$][\w@#$]*|\d[\w.]*|[^\s\w]", RegexOptions.Compiled);

    /// <summary>Dòng (1-based) mà cuối dòng còn nằm trong chuỗi '…' hoặc comment /* … */ chưa đóng — đọc tuần tự (chuỗi chứa "--" / "/*" không làm lệch).</summary>
    private static HashSet<int> LinesEndingInsideStringOrComment(string text)
    {
        var res = new HashSet<int>();
        bool inStr = false, inBlock = false, inLine = false;
        var line = 1;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var n = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '\n') { if (inStr || inBlock) res.Add(line); inLine = false; line++; continue; }
            if (inLine) continue;
            if (inBlock) { if (c == '*' && n == '/') { inBlock = false; i++; } continue; }
            if (inStr) { if (c == '\'') { if (n == '\'') i++; else inStr = false; } continue; }
            if (c == '\'') inStr = true;
            else if (c == '-' && n == '-') inLine = true;
            else if (c == '/' && n == '*') { inBlock = true; i++; }
        }
        return res;
    }
    private static readonly HashSet<string> StatementKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "SET", "DECLARE", "IF", "WHILE", "EXEC", "EXECUTE", "CREATE", "ALTER", "DROP", "TRUNCATE",
        "PRINT", "RAISERROR", "THROW", "RETURN", "BEGIN", "WAITFOR", "GOTO", "OPEN", "FETCH", "CLOSE", "DEALLOCATE", "COMMIT", "ROLLBACK", "BREAK", "CONTINUE", "WITH",
    };

    /// <summary>Số dòng (1-based) mà tại cuối dòng câu lệnh đang dở — không được dừng ở đó dù dòng sau mở đầu bằng từ khoá câu mới.</summary>
    private static HashSet<int> IncompleteLineEnds(string text)
    {
        var m = SqlStepPlanner.MaskedCode(text);   // comment + chuỗi → khoảng trắng, đọc tuần tự (đúng cả khi "--" nằm trong chuỗi)
        var lineEnds = new List<int>();
        for (var i = 0; i < m.Length; i++) if (m[i] == '\n') lineEnds.Add(i);
        lineEnds.Add(m.Length);

        var result = new HashSet<int>();
        string? pending = null;   // INSERT | UPDATE | CTE | CURSOR | SETOP | COND | MERGE
        var depth = 0;
        var caseDepth = 0;        // CASE … END ở mức ngoặc 0 (ELSE / END bên trong không phải câu lệnh)
        var cursorDecl = false;   // đã gặp CURSOR, chờ FOR
        var curStmt = "";         // từ khoá mở câu lệnh hiện tại (SET NOCOUNT ON: ON cuối dòng không phải JOIN … ON dở)
        string prev = "", prev2 = "";
        var li = 0;
        var tokens = Token.Matches(m).Cast<Match>().ToList();
        for (var ti = 0; ti <= tokens.Count; ti++)
        {
            var pos = ti < tokens.Count ? tokens[ti].Index : int.MaxValue;
            // Hết các dòng nằm trước token này: ghi trạng thái cuối dòng.
            while (li < lineEnds.Count && lineEnds[li] < pos)
            {
                var up = prev.ToUpperInvariant(); var up2 = prev2.ToUpperInvariant();
                var dangling = up is "ELSE" or "UNION" or "EXCEPT" or "INTERSECT" or "AND" or "OR" or "NOT" or "THEN" or "WHEN" or "AS" or "FOR"
                               || (up == "ON" && !curStmt.Equals("SET", StringComparison.OrdinalIgnoreCase))
                               || (up == "ALL" && up2 == "UNION") || (up == "TRY" && up2 is "BEGIN" or "END");   // khối CATCH rỗng hợp lệ, TRY rỗng thì không
                if (pending is not null || dangling || depth > 0 || caseDepth > 0) result.Add(li + 1);   // giữa CASE … END: cắt ở đây là thiếu ELSE / phần sau của câu
                li++;
            }
            if (ti == tokens.Count) break;

            var t = tokens[ti].Value;
            if (t == "(") { depth++; prev2 = prev; prev = t; continue; }
            if (t == ")") { if (depth > 0) depth--; prev2 = prev; prev = t; continue; }
            if (depth > 0) continue;
            if (t == ";") { pending = null; prev2 = prev; prev = t; continue; }
            var w = t.ToUpperInvariant();
            var next = ti + 1 < tokens.Count ? tokens[ti + 1].Value : "";
            if (w == "CASE") { caseDepth++; prev2 = prev; prev = t; continue; }
            if (w == "END" && caseDepth > 0) { caseDepth--; prev2 = prev; prev = t; continue; }
            if (caseDepth > 0) { prev2 = prev; prev = t; continue; }   // WHEN / THEN / ELSE của CASE
            var isStmt = StatementKeywords.Contains(w) && !(w == "UPDATE" && next == "(") && !(w == "INSERT" && prev.Equals("BULK", StringComparison.OrdinalIgnoreCase))
                         && !(w == "WITH" && next == "(");   // WITH (ROWLOCK) / WITH (NOLOCK): gợi ý bảng, không phải câu mới
            if (isStmt) curStmt = w;
            var handled = false;
            // 1) Câu đang dở chờ phần tiếp theo; gặp câu lệnh khác (không phải phần tiếp) thì coi như câu cũ đã xong.
            switch (pending)
            {
                case "INSERT":
                    if (w is "SELECT" or "VALUES" or "EXEC" or "EXECUTE" or "DEFAULT") { pending = null; handled = true; }
                    else if (isStmt && w != "WITH") pending = null;
                    break;
                case "UPDATE":
                    if (w == "SET") { pending = null; handled = true; }
                    else if (isStmt) pending = null;
                    break;
                case "CTE":
                    if (w is "SELECT" or "DELETE") { pending = null; handled = true; }
                    else if (w is "INSERT" or "UPDATE" or "MERGE") { pending = w; handled = true; }
                    else if (isStmt && w != "WITH") pending = null;
                    break;
                case "CURSOR": case "SETOP":
                    if (w == "SELECT") { pending = null; handled = true; }
                    else if (isStmt) pending = null;
                    break;
                case "COND":
                    if (w == "BEGIN") { pending = null; handled = true; }        // IF … BEGIN: thân là khối (dòng kết thúc bằng BEGIN loại riêng)
                    else if (w is "IF" or "WHILE") handled = true;              // ELSE IF …: vẫn chờ câu thân
                    else if (isStmt) pending = null;                             // câu thân bắt đầu — xử lý tiếp như câu mới bên dưới
                    else handled = true;                                         // còn trong điều kiện
                    break;
                case "MERGE":
                    handled = true;                                              // MERGE chỉ hết ở dấu ;
                    break;
                case "ALTERTABLE":                                               // ALTER TABLE #x ⏎ ALTER COLUMN / ADD / DROP …
                    if (w is "ADD" or "ALTER" or "DROP" or "WITH" or "CHECK" or "NOCHECK" or "ENABLE" or "DISABLE" or "SWITCH" or "SET" or "REBUILD")
                    { pending = null; handled = true; }
                    else if (isStmt) pending = null;
                    break;
            }
            // 2) Câu mới bắt đầu.
            if (!handled)
                switch (w)
                {
                    case "INSERT":
                        if (isStmt) pending = "INSERT";
                        break;
                    case "UPDATE":
                        if (isStmt && !prev.Equals("FOR", StringComparison.OrdinalIgnoreCase)) pending = "UPDATE";
                        break;
                    case "MERGE":
                        pending = "MERGE";
                        break;
                    case "ALTER":
                        if (next.Equals("TABLE", StringComparison.OrdinalIgnoreCase)) pending = "ALTERTABLE";
                        break;
                    case "WITH":
                        // WITH (NOLOCK) / WITH TIES / WITH ROLLUP… không phải CTE; CTE = WITH tên … AS (
                        if (next != "(" && Regex.IsMatch(next, @"^[A-Za-z_\[#]") &&
                            !Regex.IsMatch(next, @"^(TIES|ROLLUP|CUBE|NOWAIT|LOG|RECOMPILE|ENCRYPTION|EXECUTE|SCHEMABINDING|CHECK|NOCHECK|VALUES|ROWLOCK|NOLOCK|TABLOCK|XLOCK|UPDLOCK|RESULT)$", RegexOptions.IgnoreCase))
                            pending = "CTE";
                        break;
                    case "DECLARE":
                        cursorDecl = false;
                        break;
                    case "CURSOR":
                        cursorDecl = true;                                       // DECLARE c CURSOR … FOR SELECT; DECLARE @c CURSOR thì không có FOR
                        break;
                    case "FOR":
                        if (cursorDecl) { pending = "CURSOR"; cursorDecl = false; }
                        break;
                    case "UNION": case "EXCEPT": case "INTERSECT":
                        pending = "SETOP";
                        break;
                    case "IF": case "WHILE": case "ELSE":
                        pending = "COND";
                        break;
                }
            prev2 = prev; prev = t;
        }
        return result;
    }

    private static HashSet<int> FindSafeLinesByParens(string normalized)
    {
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
