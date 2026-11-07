using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Kết quả phân tích lỗi cột của Debug từng bước. Kind: truncate | mismatch | invalidcol | overflow | convert | null | unsupported.
/// Details: mỗi dòng 1 cột có vấn đề (hoặc bảng đối chiếu cột đích ↔ nguồn khi lệch số cột). Columns: tên các cột bị chỉ ra.</summary>
public sealed record InsertErrorReport(string Kind, string Target, int FromLine, int ToLine, string Summary, DataTable Details, IReadOnlyList<string> Columns);

/// <summary>
/// Debug từng bước: lỗi "String or binary data would be truncated" (8152 — SQL 2008 không nói cột nào), "Column name or number of supplied values does not match
/// table definition" (213), tràn số, sai kiểu, NULL vào cột NOT NULL, tên cột sai… → chỉ ra ĐÚNG CỘT gây lỗi.
///
/// Cách làm (mọi lượt chạy đều trong transaction rồi ROLLBACK — không để lại gì):
///  1. Thu hẹp: dò nhị phân các điểm dừng (safe line) giữa bước chạy được và bước lỗi → đoạn chứa đúng câu lệnh lỗi; lấy số dòng lỗi SQL Server trả về
///     (khớp dòng editor) để chọn câu INSERT. Lỗi nằm trong store con → báo tên store con.
///  2. Chạy lại script tới dòng đó nhưng THAY câu INSERT lỗi ngay tại chỗ (giữ nguyên IF / WHILE, biến, bảng tạm lúc đó) bằng khối chẩn đoán:
///     #__bco = bản sao cấu trúc bảng đích (SELECT TOP 0 * INTO), #__bcw = bản sao "nới rộng" (chữ → nvarchar(max), nhị phân → varbinary(max),
///     số / ngày → sql_variant, mọi cột cho NULL) để dữ liệu lỗi vẫn ghi được; SET FMTONLY ON lấy cấu trúc cột nguồn (chạy được trên SQL 2008);
///     rồi chạy chính câu INSERT đó vào #__bcw.
///  3. So từng cột đích với dữ liệu trong #__bcw: độ dài vượt, số vượt precision / kiểu nguyên, chuỗi không đổi được sang số / ngày, NULL vào NOT NULL;
///     lệch số cột / cột không tồn tại thì đối chiếu cột đích ↔ cột / biểu thức nguồn theo thứ tự.
///  SQL động (EXEC sp_executesql @q / EXEC(@q) / EXEC proc @q, …): lấy giá trị @q tại điểm lỗi, sửa câu INSERT bên trong như trên, chạy lại qua đúng câu EXEC gốc.
/// </summary>
public static class InsertErrorAnalyzer
{
    private static readonly Regex KnownError = new(
        @"would be truncated|does not match table definition|Arithmetic overflow|Conversion failed|Error converting data type|Cannot insert the value NULL|Invalid column name|out-of-range" +
        @"|(more|fewer) items than the insert list|(more|fewer) columns in the INSERT statement than values|explicit value for the identity column" +
        @"|Msg (8152|2628|213|8115|220|232|245|8114|241|242|515|207|120|121|109|110|8101)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool Recognizes(string? error) => !string.IsNullOrEmpty(error) && KnownError.IsMatch(error);

    // ------------------------------------------------------------------ chạy batch

    private sealed record Col(string Name, string Type, int Size, int Precision, int Scale);
    private sealed record RS(List<Col> Cols, List<object?[]> Rows)
    {
        public string? Mark => Cols.Count >= 1 && Cols[0].Name == "__bcode_mark" && Rows.Count > 0 ? Rows[0][0] as string : null;
    }

    /// <summary>Chạy 1 batch, đọc mọi bảng kết quả (kể cả bảng 0 dòng của FMTONLY — giữ cấu trúc cột). Lỗi SQL: trả các bảng đã đọc được + exception.</summary>
    private static async Task<(List<RS> Sets, SqlException? Error)> RunAsync(SqlConnection conn, string sql, CancellationToken ct)
    {
        var sets = new List<RS>();
        try
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            do
            {
                if (rd.FieldCount == 0) continue;
                var cols = new List<Col>();
                var schema = rd.GetSchemaTable();
                for (var i = 0; i < rd.FieldCount; i++)
                {
                    int Get(string n) { try { var v = schema?.Rows[i][n]; return v is null or DBNull ? 0 : Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return 0; } }
                    cols.Add(new Col(rd.GetName(i), rd.GetDataTypeName(i), Get("ColumnSize"), Get("NumericPrecision"), Get("NumericScale")));
                }
                var rows = new List<object?[]>();
                while (await rd.ReadAsync(ct))
                {
                    var row = new object?[rd.FieldCount];
                    for (var i = 0; i < rd.FieldCount; i++) row[i] = rd.IsDBNull(i) ? null : rd.GetValue(i);
                    rows.Add(row);
                }
                sets.Add(new RS(cols, rows));
            } while (await rd.NextResultAsync(ct));
            return (sets, null);
        }
        catch (SqlException ex) { return (sets, ex); }
    }

    private static async Task RollbackAsync(SqlConnection conn)
    {
        try { await using var rb = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRAN;", conn); await rb.ExecuteNonQueryAsync(); } catch { /* connection hỏng — server tự rollback */ }
    }

    /// <summary>Chạy kế hoạch tới dòng <paramref name="line"/> (connection mới, rollback). null = chạy được; khác = lỗi đầu tiên.</summary>
    private static async Task<SqlException?> TryLineAsync(StepPlan plan, int line, Func<SqlConnection> connect, CancellationToken ct)
    {
        await using var conn = connect();
        await conn.OpenAsync(ct);
        try { return (await RunAsync(conn, plan.BuildBatch(line).Sql, ct)).Error; }
        finally { await RollbackAsync(conn); }
    }

    /// <summary>Batch giống StepPlan.BuildBatch nhưng thân script (tới hết dòng) do người gọi đưa vào (đã chèn khối chẩn đoán), không kèm phần xem biến.</summary>
    private static string Batch(StepPlan plan, string prefix)
    {
        var sb = new StringBuilder("SET XACT_ABORT OFF; BEGIN TRAN; ");
        if (plan.PreludeSql.Length > 0) sb.Append(plan.PreludeSql).Append(' ');
        sb.Append(plan.DeclareSql).Append(' ').Append(prefix);
        var closers = SqlStepPlanner.Closers(SqlStepPlanner.MaskedCode(prefix));
        if (closers.Length > 0) sb.Append('\n').Append(closers);
        return sb.Append(";\nIF XACT_STATE() = -1 ROLLBACK TRAN;\n").ToString();
    }

    // ------------------------------------------------------------------ đọc câu INSERT

    private sealed class InsertStmt
    {
        public int Start, End;              // [Start, End) trong text gốc — cả câu INSERT
        public int TargetStart, TargetEnd;  // tên bảng đích
        public string Target = "";
        public List<string>? ColumnList;    // INSERT T (a, b, c)
        public string SourceKind = "";      // SELECT | VALUES | EXEC | DEFAULT
        public int SourceStart;
        public bool HasOutput;
        public List<string> SourceItems = new();   // biểu thức nguồn theo thứ tự (SELECT list / VALUES tuple 1); rỗng = không tách được (*, EXEC)
        public string Cte = "";             // "WITH a AS (…), b AS (…) " đứng trước INSERT (Start đã lùi về chữ WITH)
    }

    private static readonly Regex InsertKw = new(@"\bINSERT\b(?:\s+INTO\b)?\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TargetName = new(@"\G(?:\[[^\]]+\]|[#@]{0,2}[\w$#@]+)(?:\s*\.\s*(?:\[[^\]]*\]|[\w$#]+|(?=\.))){0,3}", RegexOptions.Compiled);
    private static readonly HashSet<string> Terminators = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "SET", "DECLARE", "IF", "WHILE", "EXEC", "EXECUTE", "PRINT", "RETURN", "BEGIN", "END", "ELSE", "TRUNCATE", "DROP",
        "CREATE", "ALTER", "GOTO", "RAISERROR", "COMMIT", "ROLLBACK", "MERGE", "THROW", "BREAK", "CONTINUE", "WAITFOR", "OPEN", "FETCH", "CLOSE", "DEALLOCATE",
    };
    private static readonly Regex Word = new(@"[A-Za-z_@#$][\w@#$]*", RegexOptions.Compiled);

    private static int SkipWs(string m, int i) { while (i < m.Length && char.IsWhiteSpace(m[i])) i++; return i; }

    /// <summary>Vị trí ngoặc đóng khớp với ngoặc mở tại <paramref name="open"/> (text đã mask); -1 nếu không có.</summary>
    private static int MatchParen(string m, int open)
    {
        var d = 0;
        for (var i = open; i < m.Length; i++)
        {
            if (m[i] == '(') d++;
            else if (m[i] == ')' && --d == 0) return i;
        }
        return -1;
    }

    private static string WordAt(string m, int i) => Word.Match(m, i) is { Success: true } w && w.Index == i ? w.Value : "";

    /// <summary>Hết câu lệnh bắt đầu nguồn ở <paramref name="from"/>: dấu ; hoặc từ khoá mở câu mới ở mức ngoặc 0 — không tính ELSE / END của CASE;
    /// SELECT mới (không đứng sau UNION / EXCEPT / INTERSECT / ALL) và WITH mở CTE cũng là câu mới; nhãn GOTO ("ten:") cũng vậy.</summary>
    private static int StatementEnd(string m, int from, int limit)
    {
        int depth = 0, caseDepth = 0;
        var prevWord = "";
        for (var i = from; i < limit; i++)
        {
            var c = m[i];
            if (c == '(') depth++;
            else if (c == ')') { if (depth > 0) depth--; }
            else if (c == ';' && depth == 0) return i;
            else if ((char.IsLetter(c) || c == '_') && (i == 0 || !(char.IsLetterOrDigit(m[i - 1]) || m[i - 1] is '_' or '@' or '#' or '$' or '.')))
            {
                var w = WordAt(m, i);
                if (w.Length == 0) continue;
                if (depth == 0)
                {
                    if (w.Equals("CASE", StringComparison.OrdinalIgnoreCase)) caseDepth++;
                    else if (w.Equals("END", StringComparison.OrdinalIgnoreCase) && caseDepth > 0) caseDepth--;
                    else if (caseDepth == 0)
                    {
                        if (Terminators.Contains(w)) return i;
                        if (w.Equals("SELECT", StringComparison.OrdinalIgnoreCase)
                            && !(prevWord.Equals("UNION", StringComparison.OrdinalIgnoreCase) || prevWord.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                                 || prevWord.Equals("EXCEPT", StringComparison.OrdinalIgnoreCase) || prevWord.Equals("INTERSECT", StringComparison.OrdinalIgnoreCase)))
                            return i;
                        if (w.Equals("WITH", StringComparison.OrdinalIgnoreCase))
                        {
                            var n = SkipWs(m, i + 4);
                            var nw = WordAt(m, n);
                            if (n < m.Length && m[n] != '(' && nw.Length > 0
                                && !Regex.IsMatch(nw, @"^(TIES|ROLLUP|CUBE|NOWAIT|RECOMPILE)$", RegexOptions.IgnoreCase)) return i;
                        }
                        // nhãn GOTO: "ten:" (không phải "::")
                        var after = i + w.Length;
                        if (after < m.Length && m[after] == ':' && (after + 1 >= m.Length || m[after + 1] != ':')) return i;
                    }
                    prevWord = w;
                }
                i += w.Length - 1;
            }
        }
        return limit;
    }

    /// <summary>CTE đứng ngay trước câu INSERT ("WITH a AS (…), b AS (…) INSERT …") → vị trí chữ WITH; -1 nếu không có.</summary>
    private static int CteStartBefore(string m, int insertStart)
    {
        var p = insertStart - 1;
        while (true)
        {
            while (p >= 0 && char.IsWhiteSpace(m[p])) p--;
            if (p < 0 || m[p] != ')') return -1;
            var open = MatchParenBack(m, p);                       // thân CTE ( … )
            if (open < 0) return -1;
            p = open - 1;
            while (p >= 0 && char.IsWhiteSpace(m[p])) p--;
            if (p < 1 || !(m[p - 1] is 'A' or 'a') || !(m[p] is 'S' or 's') || (p >= 2 && (char.IsLetterOrDigit(m[p - 2]) || m[p - 2] == '_'))) return -1;
            p -= 2;                                                // trước "AS"
            while (p >= 0 && char.IsWhiteSpace(m[p])) p--;
            if (p >= 0 && m[p] == ')') { var o = MatchParenBack(m, p); if (o < 0) return -1; p = o - 1; while (p >= 0 && char.IsWhiteSpace(m[p])) p--; }   // (cột…)
            var end = p;                                           // tên CTE
            while (p >= 0 && (char.IsLetterOrDigit(m[p]) || m[p] is '_' or '#' or '$' or '[' or ']')) p--;
            if (end == p) return -1;
            var q = p;
            while (q >= 0 && char.IsWhiteSpace(m[q])) q--;
            if (q >= 0 && m[q] == ',') { p = q - 1; continue; }    // CTE trước đó
            if (q >= 3 && m.Substring(q - 3, 4).Equals("WITH", StringComparison.OrdinalIgnoreCase) && (q < 4 || !(char.IsLetterOrDigit(m[q - 4]) || m[q - 4] == '_')))
                return q - 3;
            return -1;
        }
    }

    private static int MatchParenBack(string m, int close)
    {
        var d = 0;
        for (var i = close; i >= 0; i--)
        {
            if (m[i] == ')') d++;
            else if (m[i] == '(' && --d == 0) return i;
        }
        return -1;
    }

    /// <summary>Tách theo dấu phẩy ở mức ngoặc 0 (text đã mask để tìm, cắt từ text gốc).</summary>
    private static List<string> SplitTop(string orig, string m, int from, int to)
    {
        var parts = new List<string>();
        int depth = 0, s = from;
        for (var i = from; i < to; i++)
        {
            if (m[i] == '(') depth++;
            else if (m[i] == ')') depth--;
            else if (m[i] == ',' && depth == 0) { parts.Add(orig[s..i].Trim()); s = i + 1; }
        }
        parts.Add(orig[s..to].Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static readonly Regex SelectHead = new(@"\G\s*SELECT\s+(?:(?:DISTINCT|ALL)\s+)?(?:TOP\s*(?:\([^)]*\)|\d+)\s*(?:PERCENT\s+)?(?:WITH\s+TIES\s+)?)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly HashSet<string> SelectListEnd = new(StringComparer.OrdinalIgnoreCase) { "FROM", "INTO", "WHERE", "GROUP", "ORDER", "HAVING", "UNION", "EXCEPT", "INTERSECT", "OPTION", "FOR" };

    private static List<string> SelectItems(string orig, string m, int start, int end)
    {
        var h = SelectHead.Match(m, start);
        if (!h.Success) return new();
        var from = h.Index + h.Length;
        int depth = 0, caseDepth = 0, stop = end;
        for (var i = from; i < end; i++)
        {
            if (m[i] == '(') depth++;
            else if (m[i] == ')') depth--;
            else if (depth == 0 && char.IsLetter(m[i]) && (i == 0 || !(char.IsLetterOrDigit(m[i - 1]) || m[i - 1] is '_' or '@' or '#' or '.')))
            {
                var w = WordAt(m, i);
                if (w.Equals("CASE", StringComparison.OrdinalIgnoreCase)) caseDepth++;
                else if (w.Equals("END", StringComparison.OrdinalIgnoreCase) && caseDepth > 0) caseDepth--;
                else if (caseDepth == 0 && SelectListEnd.Contains(w)) { stop = i; break; }
                i += Math.Max(0, w.Length - 1);
            }
        }
        var items = SplitTop(orig, m, from, stop);
        return items.Any(x => x == "*" || x.EndsWith(".*")) ? new() : items;   // có * → không ghép 1-1 được
    }

    /// <summary>Mọi câu INSERT bắt đầu trong [from, to) của text.</summary>
    private static List<InsertStmt> FindInserts(string orig, int from, int to)
    {
        var m = SqlStepPlanner.MaskedCode(orig);
        var list = new List<InsertStmt>();
        foreach (Match ins in InsertKw.Matches(m))
        {
            if (ins.Index < from || ins.Index >= to) continue;
            if (ins.Index > 0 && (char.IsLetterOrDigit(m[ins.Index - 1]) || m[ins.Index - 1] is '_' or '@' or '#' or '$')) continue;
            var t = TargetName.Match(m, ins.Index + ins.Length);
            if (!t.Success || t.Length == 0) continue;
            var st = new InsertStmt { Start = ins.Index, TargetStart = t.Index, TargetEnd = t.Index + t.Length, Target = orig.Substring(t.Index, t.Length).Trim() };
            var i = SkipWs(m, st.TargetEnd);
            if (WordAt(m, i).Equals("WITH", StringComparison.OrdinalIgnoreCase))   // gợi ý bảng: WITH (TABLOCK)
            {
                var p = SkipWs(m, i + 4);
                if (p < m.Length && m[p] == '(') { var e = MatchParen(m, p); if (e > 0) i = SkipWs(m, e + 1); }
            }
            if (i < m.Length && m[i] == '(' && !WordAt(m, SkipWs(m, i + 1)).Equals("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                var e = MatchParen(m, i);
                if (e < 0) continue;
                // Tách trên text đã bỏ comment / chuỗi: "/* cột đầu */ ma_kh" → ma_kh.
                st.ColumnList = SplitTop(m, m, i + 1, e).Select(c => c.Trim().Trim('[', ']').Trim()).ToList();
                i = SkipWs(m, e + 1);
            }
            if (WordAt(m, i).Equals("OUTPUT", StringComparison.OrdinalIgnoreCase))
            {
                st.HasOutput = true;
                var k = i;
                while (k < m.Length && !(WordAt(m, k) is var w && (w.Equals("SELECT", StringComparison.OrdinalIgnoreCase) || w.Equals("VALUES", StringComparison.OrdinalIgnoreCase)
                       || w.Equals("EXEC", StringComparison.OrdinalIgnoreCase) || w.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase)))) k++;
                i = k;
            }
            if (i < m.Length && m[i] == '(') { st.SourceKind = "SELECT"; }   // INSERT T (SELECT …)
            var kw = WordAt(m, i).ToUpperInvariant();
            st.SourceKind = kw switch { "SELECT" => "SELECT", "VALUES" => "VALUES", "EXEC" or "EXECUTE" => "EXEC", "DEFAULT" => "DEFAULT", _ => st.SourceKind };
            if (st.SourceKind.Length == 0) continue;
            st.SourceStart = i;
            st.End = StatementEnd(m, i + Math.Max(1, kw.Length), m.Length);
            if (st.SourceKind == "SELECT") st.SourceItems = SelectItems(orig, m, i, st.End);
            else if (st.SourceKind == "VALUES")
            {
                var p = SkipWs(m, i + 6);
                if (p < m.Length && m[p] == '(') { var e = MatchParen(m, p); if (e > 0) st.SourceItems = SplitTop(orig, m, p + 1, e); }
            }
            var cte = CteStartBefore(m, ins.Index);
            if (cte >= 0) { st.Cte = orig[cte..ins.Index]; st.Start = cte; }
            list.Add(st);
        }
        return list;
    }

    /// <summary>EXEC chạy SQL động trong đoạn: EXEC(@v) / EXEC sp_executesql @v / EXEC proc @v, … (tham số đầu là biến chứa câu SQL).</summary>
    private static readonly Regex DynExec = new(@"\bEXEC(?:UTE)?\s*(?:\(\s*(?<v>@[\w$#@]+)\s*\)|(?:[\w$#\[\]\.]+)\s+(?<v>@[\w$#@]+)(?![\w$#@]|\s*=))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChildExec = new(@"\bEXEC(?:UTE)?\s+(?:@[\w]+\s*=\s*)?(?<p>[\w$#\[\]\.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ------------------------------------------------------------------ khối chẩn đoán

    private static string Q(string name) => "[" + name.Replace("]", "]]") + "]";
    private static string N(string s) => "N'" + s.Replace("'", "''") + "'";

    /// <summary>Tạo #__bco (cấu trúc thật) + #__bcw (nới rộng) từ bảng đích — chỉ 1 lần (trong vòng lặp không tạo lại).</summary>
    private static string CloneSql(string target) =>
        $"IF OBJECT_ID('tempdb..#__bco') IS NULL BEGIN " +
        $"SELECT TOP 0 * INTO #__bco FROM {target}; SELECT TOP 0 * INTO #__bcw FROM {target}; " +
        ComputedColsSql(target) +
        "DECLARE @__bcs nvarchar(max); SET @__bcs = N''; " +
        // Cột computed của bảng gốc: bỏ khỏi bản nhân bản (SELECT INTO biến nó thành cột thường → INSERT không danh sách cột bị lệch số cột giả).
        "SELECT @__bcs = @__bcs + N'ALTER TABLE #__bcw DROP COLUMN ' + QUOTENAME(x.name) + N'; ' FROM #__bcc x; " +
        "SELECT @__bcs = @__bcs + N'ALTER TABLE #__bcw ALTER COLUMN ' + QUOTENAME(c.name) + N' ' + " +
        "CASE WHEN TYPE_NAME(c.system_type_id) IN ('char','varchar','nchar','nvarchar','text','ntext','xml') THEN N'nvarchar(max)' " +
        "WHEN TYPE_NAME(c.system_type_id) IN ('binary','varbinary','image') THEN N'varbinary(max)' ELSE N'sql_variant' END + N' NULL; ' " +
        "FROM tempdb.sys.columns c WHERE c.object_id = OBJECT_ID('tempdb..#__bcw') AND c.is_identity = 0 AND c.is_computed = 0 " +
        "AND c.name NOT IN (SELECT x.name FROM #__bcc x) " +
        "AND TYPE_NAME(c.system_type_id) NOT IN ('uniqueidentifier','bit','sql_variant','timestamp','hierarchyid','geography','geometry'); " +
        "IF @__bcs <> N'' EXEC (@__bcs); END; ";

    /// <summary>#__bcc = tên các cột computed của bảng đích GỐC (bảng tạm: tempdb; bảng thật: database hiện tại; biến bảng: bỏ qua).</summary>
    private static string ComputedColsSql(string target)
    {
        var t = target.Trim();
        var bare = t.TrimStart('[');
        if (bare.StartsWith('@')) return "SELECT CAST(N'' AS sysname) AS name INTO #__bcc WHERE 1 = 0; ";   // biến bảng: luôn có #__bcc (rỗng)
        var temp = bare.StartsWith('#');
        var cat = temp ? "tempdb.sys.columns" : "sys.columns";
        var obj = temp ? "tempdb.." + t : t;
        return $"SELECT c.name INTO #__bcc FROM {cat} c WHERE c.object_id = OBJECT_ID({N(obj)}) AND c.is_computed = 1; ";
    }

    /// <summary>Thay câu INSERT bằng: lấy cấu trúc nguồn (FMTONLY) + chạy chính câu đó vào #__bcw (TRY/CATCH, báo số dòng / lỗi qua bảng đánh dấu).</summary>
    /// <param name="includeInsert">false khi lỗi gốc là lệch số cột (120/121/109/110/213): SQL Server từ chối cả batch ngay lúc biên dịch nếu có câu INSERT lệch
    /// — chỉ lấy cấu trúc đích + nguồn rồi đối chiếu.</param>
    private static string DiagnosticFor(string text, InsertStmt st, bool createClone, bool includeInsert = true)
    {
        var cte = st.Cte.Length > 0 ? "; " : "";   // CTE phải đứng sau dấu ; khi có câu lệnh trước nó
        var insert = cte + text[st.Start..st.TargetStart] + "#__bcw" + text[st.TargetEnd..st.End];
        var src = (st.Cte.Length > 0 ? "; " + st.Cte : "") + text[st.SourceStart..st.End];
        var sb = new StringBuilder("BEGIN ");
        if (createClone) sb.Append(CloneSql(st.Target));
        if (st.SourceKind is "SELECT" or "EXEC" && !st.HasOutput)
            sb.Append("SELECT N'src' AS [__bcode_mark]; SET FMTONLY ON; ").Append(src).Append("\n; SET FMTONLY OFF; ");
        if (includeInsert)
            sb.Append("BEGIN TRY ").Append(insert).Append("\n; SELECT N'ins' AS [__bcode_mark], @@ROWCOUNT AS [n]; END TRY ")
              .Append("BEGIN CATCH SELECT N'err' AS [__bcode_mark], ERROR_NUMBER() AS [n], ERROR_MESSAGE() AS [m]; END CATCH ");
        sb.Append("END ");
        return sb.ToString();
    }

    private static bool IsCountMismatch(int n) => n is 213 or 120 or 121 or 109 or 110;
    private static bool IsSyntax(int n) => n is 102 or 105 or 156 or 170 or 319 or 1018 or 4145 or 8180;

    // ------------------------------------------------------------------ phân tích

    private sealed record TCol(int Id, string Name, string Type, int MaxLen, int Precision, int Scale, bool Nullable, bool Identity, bool Computed)
    {
        public bool IsText => Type is "char" or "varchar" or "nchar" or "nvarchar";
        public bool IsBin => Type is "binary" or "varbinary";
        public int Limit => Type is "nchar" or "nvarchar" ? MaxLen / 2 : MaxLen;
        public bool IsNum => Type is "tinyint" or "smallint" or "int" or "bigint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real";
        public bool IsDate => Type is "date" or "datetime" or "smalldatetime" or "datetime2" or "datetimeoffset" or "time";
        public string Display => Type + (IsText || IsBin ? $"({(MaxLen == -1 ? "max" : Limit.ToString(CultureInfo.InvariantCulture))})"
            : Type is "decimal" or "numeric" ? $"({Precision},{Scale})" : "") + (Nullable ? "" : " NOT NULL");
    }

    private static async Task<List<TCol>> TargetColumnsAsync(SqlConnection conn, CancellationToken ct)
    {
        // is_computed lấy từ bảng đích GỐC (#__bcc — SELECT INTO biến cột computed thành cột thường, INSERT không danh sách cột thì không ghi vào cột computed).
        var (sets, _) = await RunAsync(conn, @"SET FMTONLY OFF; IF OBJECT_ID('tempdb..#__bcc') IS NULL SELECT CAST(N'' AS sysname) AS name INTO #__bcc WHERE 1 = 0;
SELECT c.column_id, c.name, TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
CAST(CASE WHEN c.is_computed = 1 OR (OBJECT_ID('tempdb..#__bcc') IS NOT NULL AND EXISTS (SELECT 1 FROM #__bcc x WHERE x.name = c.name)) THEN 1 ELSE 0 END AS bit)
FROM tempdb.sys.columns c WHERE c.object_id = OBJECT_ID('tempdb..#__bco') ORDER BY c.column_id", ct);
        return sets.FirstOrDefault()?.Rows.Select(r => new TCol(Convert.ToInt32(r[0]), (string)r[1]!, ((string?)r[2] ?? "").ToLowerInvariant(), Convert.ToInt32(r[3]),
            Convert.ToInt32(r[4]), Convert.ToInt32(r[5]), (bool)r[6]!, (bool)r[7]!, (bool)r[8]!)).ToList() ?? new();
    }

    /// <summary>1 truy vấn: mỗi cột đích dùng tới → (vấn đề, giá trị lớn nhất / mẫu, số dòng lỗi). Chỉ đọc #__bcw.</summary>
    private static string AnalysisSql(List<TCol> cols)
    {
        const string NumBase = "('tinyint','smallint','int','bigint','decimal','numeric','money','smallmoney','float','real','bit')";
        const string StrBase = "('varchar','nvarchar','char','nchar')";
        var parts = new List<string>();
        foreach (var c in cols)
        {
            var q = Q(c.Name);
            var s = $"LTRIM(RTRIM(CAST({q} AS nvarchar(4000))))";
            // chuỗi "là số" an toàn để CAST sang float: chỉ chữ số / 1 dấu chấm / dấu - ở đầu
            var t = $"(CASE WHEN LEFT({s},1) = '-' THEN STUFF({s},1,1,'') ELSE {s} END)";
            var isNumStr = $"({t} NOT LIKE '%[^0-9.]%' AND {t} LIKE '%[0-9]%' AND {t} NOT LIKE '%.%.%')";
            string? check = null, measure = null, problem = null, limit = null;
            if ((c.IsText || c.IsBin) && c.MaxLen != -1)
            {
                var len = c.IsBin ? $"DATALENGTH({q})" : $"LEN({q})";
                check = $"{len} > {c.Limit}"; measure = $"CAST(MAX({len}) AS nvarchar(50))"; problem = "truncate"; limit = $"{c.Limit} ký tự" + (c.IsBin ? " (byte)" : "");
            }
            else if (c.IsNum)
            {
                var num = $"(CASE WHEN SQL_VARIANT_PROPERTY({q},'BaseType') IN {NumBase} THEN CAST({q} AS float) WHEN SQL_VARIANT_PROPERTY({q},'BaseType') IN {StrBase} AND {isNumStr} THEN CAST({s} AS float) END)";
                var bad = c.Type is "decimal" or "numeric" ? $"ABS({num}) >= POWER(CAST(10 AS float), {c.Precision - c.Scale})"
                    : c.Type switch
                    {
                        "tinyint" => $"({num} < 0 OR {num} > 255)", "smallint" => $"ABS({num}) > 32767",
                        "int" => $"ABS({num}) > 2147483647", _ => "1 = 0",
                    };
                var conv = $"(SQL_VARIANT_PROPERTY({q},'BaseType') IN {StrBase} AND NOT {isNumStr}" + (c.Type is "decimal" or "numeric" ? "" : $" AND {s} <> ''") + ")";
                check = $"({bad}) OR {conv}";
                measure = $"CAST(MAX(ABS({num})) AS nvarchar(50))";
                problem = $"CASE WHEN SUM(CASE WHEN {conv} THEN 1 ELSE 0 END) > 0 THEN 'convert' ELSE 'overflow' END";
                limit = c.Type is "decimal" or "numeric" ? $"< 10^{c.Precision - c.Scale} ({c.Type}({c.Precision},{c.Scale}))" : c.Type;
                parts.Add($"SELECT {c.Id} AS [id], {N(c.Name)} AS [col], {problem} AS [problem], {N(limit)} AS [limit], {measure} AS [actual], " +
                          $"SUM(CASE WHEN {check} THEN 1 ELSE 0 END) AS [cnt], (SELECT TOP 1 CAST({q} AS nvarchar(4000)) FROM #__bcw WHERE {check}) AS [sample], " +
                          $"SUM(CASE WHEN {q} IS NULL THEN 1 ELSE 0 END) AS [nulls] FROM #__bcw");
                continue;
            }
            else if (c.IsDate)
            {
                // Chuỗi không đọc được thành ngày; hoặc ngày (kiểu ngày / chuỗi ngày hợp lệ) nằm ngoài khoảng của kiểu đích:
                // datetime ≥ 1753-01-01; smalldatetime 1900-01-01 … 2079-06-06 23:59:29 (lỗi 242 / 8115 khi đổi kiểu).
                const string DateBase = "('datetime','datetime2','date','smalldatetime','datetimeoffset')";
                var dt2 = $"(CASE WHEN SQL_VARIANT_PROPERTY({q},'BaseType') IN {DateBase} THEN CAST({q} AS datetime2) " +
                          $"WHEN SQL_VARIANT_PROPERTY({q},'BaseType') IN {StrBase} AND ISDATE({s}) = 1 THEN CAST(CAST({s} AS datetime) AS datetime2) END)";
                var conv = $"(SQL_VARIANT_PROPERTY({q},'BaseType') IN {StrBase} AND {s} <> '' AND ISDATE({s}) = 0)";
                var range = c.Type switch
                {
                    "datetime" => $" OR {dt2} < '17530101'",
                    "smalldatetime" => $" OR {dt2} < '19000101' OR {dt2} > '20790606 23:59:29'",
                    _ => "",
                };
                check = $"{conv}{range}";
                measure = $"CONVERT(nvarchar(30), MIN({dt2}), 120) + N' … ' + CONVERT(nvarchar(30), MAX({dt2}), 120)";
                problem = "convert";
                limit = c.Type switch { "datetime" => "datetime (từ 1753-01-01)", "smalldatetime" => "smalldatetime (1900-01-01 … 2079-06-06)", _ => c.Type };
            }
            if (check is null)
            {
                parts.Add($"SELECT {c.Id} AS [id], {N(c.Name)} AS [col], 'none' AS [problem], N'' AS [limit], CAST(NULL AS nvarchar(50)) AS [actual], 0 AS [cnt], " +
                          $"CAST(NULL AS nvarchar(4000)) AS [sample], SUM(CASE WHEN {q} IS NULL THEN 1 ELSE 0 END) AS [nulls] FROM #__bcw");
                continue;
            }
            parts.Add($"SELECT {c.Id} AS [id], {N(c.Name)} AS [col], '{problem}' AS [problem], {N(limit!)} AS [limit], {measure} AS [actual], " +
                      $"SUM(CASE WHEN {check} THEN 1 ELSE 0 END) AS [cnt], (SELECT TOP 1 CAST({q} AS nvarchar(4000)) FROM #__bcw WHERE {check}" +
                      (c.IsText ? $" ORDER BY LEN({q}) DESC" : "") + ") AS [sample], " +
                      $"SUM(CASE WHEN {q} IS NULL THEN 1 ELSE 0 END) AS [nulls] FROM #__bcw");
        }
        return "SET FMTONLY OFF;\n" + string.Join("\nUNION ALL\n", parts) + "\nORDER BY [id];";
    }

    private static DataTable NewDetails()
    {
        var dt = new DataTable("Phân tích lỗi cột");
        foreach (var c in new[] { "STT", "Cột đích", "Kiểu đích", "Vấn đề", "Giới hạn", "Thực tế", "Số dòng lỗi", "Giá trị mẫu", "Biểu thức nguồn" })
            dt.Columns.Add(c, typeof(string));
        return dt;
    }

    private static string Short(string? s, int n = 120) => s is null ? "" : s.Length > n ? s[..n] + "…" : s;
    private static string Flat(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    private static readonly Dictionary<string, string> ProblemVi = new()
    {
        ["truncate"] = "Vượt độ dài", ["overflow"] = "Tràn số", ["convert"] = "Sai kiểu / không đổi được", ["null"] = "NULL vào cột NOT NULL",
    };

    /// <summary>Phân tích. null = không xác định được câu lệnh (báo lỗi như cũ).</summary>
    public static async Task<InsertErrorReport?> AnalyzeAsync(StepPlan plan, int lastOkLine, int errorLine, string error, Func<SqlConnection> connect, CancellationToken ct = default)
    {
        // 1. Thu hẹp tới điểm dừng đầu tiên bị lỗi (lỗi đã xảy ra thì mọi bước sau cũng lỗi → dò nhị phân). Điểm dừng nào chạy ra lỗi CÚ PHÁP là chỗ cắt
        //    batch không hợp lệ (không phải lỗi của store) → bỏ khỏi danh sách rồi dò tiếp, không để nó đánh lừa phép dò.
        var safe = plan.SafeLines.Where(l => l > lastOkLine && l <= errorLine).OrderBy(l => l).ToList();
        if (safe.Count == 0 || safe[^1] != errorLine) safe.Add(errorLine);
        var tried = new Dictionary<int, SqlException?>();
        async Task<SqlException?> Try(int l)
        {
            if (!tried.TryGetValue(l, out var e)) tried[l] = e = await TryLineAsync(plan, l, connect, ct);
            return e;
        }
        static int Num(SqlException e) => RealNumber(e.Errors.Cast<SqlError>().FirstOrDefault(x => x.Class > 10) ?? e.Errors[0]);
        int lo = 0, hi = safe.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var e = await Try(safe[mid]);
            if (e is not null && IsSyntax(Num(e)) && safe[mid] != errorLine) { safe.RemoveAt(mid); hi--; continue; }
            if (e is not null) hi = mid; else lo = mid + 1;
        }
        var line = safe[lo];
        var sqlErr = await Try(line);
        if (sqlErr is null) return null;   // không tái hiện được lỗi
        var prev = plan.SafeLines.Where(l => l < line && !(tried.TryGetValue(l, out var te) && te is not null)).DefaultIfEmpty(0).Max();
        if (prev < lastOkLine) prev = lastOkLine;

        var prefix = string.Join("\n", plan.Lines.Take(line));
        var segStart = string.Join("\n", plan.Lines.Take(prev)).Length + (prev > 0 ? 1 : 0);
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < prefix.Length; i++) if (prefix[i] == '\n') lineStarts.Add(i + 1);
        int LineOf(int pos) { var k = lineStarts.BinarySearch(pos); return (k >= 0 ? k : ~k - 1) + 1; }
        var err = sqlErr.Errors.Cast<SqlError>().FirstOrDefault(x => x.Class > 10) ?? sqlErr.Errors[0];
        var errNo = RealNumber(err);
        // Kết quả chấp nhận được phải KHỚP loại lỗi gốc: lỗi cấu trúc (lệch số cột / cột sai) ↔ mismatch / invalidcol; lỗi dữ liệu ↔ truncate / overflow / convert / null.
        var structural = IsCountMismatch(errNo) || errNo is 207 or 8101;
        bool Accept(InsertErrorReport? r) => r is { Columns.Count: > 0 } &&
            (structural ? r.Kind is "mismatch" or "invalidcol" : r.Kind is "truncate" or "overflow" or "convert" or "null");

        var inserts = FindInserts(prefix, segStart, prefix.Length);
        var maskedPrefix = SqlStepPlanner.MaskedCode(prefix);
        var namedProc = !string.IsNullOrEmpty(err.Procedure) && !Regex.IsMatch(err.Procedure, @"^sp_executesql$", RegexOptions.IgnoreCase);
        static string Bare(string n) => n.Split('.').Last().Trim('[', ']', ' ');
        bool ExecsProc(string text, InsertStmt s) => namedProc && s.SourceKind == "EXEC" &&
            Regex.IsMatch(text[s.SourceStart..s.End], @"\b" + Regex.Escape(Bare(err.Procedure)) + @"\b", RegexOptions.IgnoreCase);

        // Thử lần lượt các câu ứng viên (tối đa 6), lấy câu đầu tiên mà chạy lại có chẩn đoán ra đúng loại lỗi gốc; không câu nào khớp thì trả kết quả "có ích" nhất.
        async Task<InsertErrorReport?> TryCandidatesAsync(IEnumerable<InsertStmt> cands, Func<InsertStmt, Task<InsertErrorReport?>> diagnose)
        {
            InsertErrorReport? fallback = null;
            foreach (var st in cands.Distinct().Take(6))
            {
                var rep = await diagnose(st);
                if (Accept(rep)) return rep;
                if (fallback is null || (fallback.Columns.Count == 0 && rep is { Columns.Count: > 0 })) fallback = rep ?? fallback;
            }
            return fallback;
        }
        Task<InsertErrorReport?> DiagnoseStatic(InsertStmt st)
        {
            var text = prefix[..st.Start] + DiagnosticFor(prefix, st, createClone: true, includeInsert: !IsCountMismatch(errNo)) + prefix[st.End..];
            return DiagnoseAsync(plan, Batch(plan, text), st, LineOf(st.Start), LineOf(Math.Max(st.Start, st.End - 1)), err, errNo, connect, ct);
        }
        IEnumerable<InsertStmt> Ordered(List<InsertStmt> list, string text, Func<InsertStmt, int> lineOf, Func<InsertStmt, int> endLineOf)
        {
            if (namedProc)
                return list.Where(s => ExecsProc(text, s)).Concat(list.AsEnumerable().Reverse());
            var onLine = list.Where(s => lineOf(s) <= err.LineNumber && endLineOf(s) >= err.LineNumber);
            var before = list.Where(s => lineOf(s) < err.LineNumber).Reverse();
            return onLine.Concat(before).Concat(list.AsEnumerable().Reverse());
        }

        // Lỗi gắn tên 1 store khác: nếu đó là store NGUỒN của "INSERT … EXEC store" trong đoạn thì lỗi là của câu INSERT (dữ liệu store trả về không vừa bảng đích)
        // — SQL Server gắn tên store nguồn vào lỗi. Còn lại (EXEC store con trơn) → lỗi nằm bên trong store con.
        if (namedProc)
        {
            var insertExec = inserts.Any(s => ExecsProc(prefix, s));
            // Đoạn lỗi có EXEC SQL động: câu INSERT … EXEC <store> có thể nằm TRONG giá trị @q — để nhánh SQL động bên dưới đọc @q rồi quyết.
            var dynVar = DynExec.Match(maskedPrefix, segStart);
            if (!insertExec && !dynVar.Success)
            {
                var child = ChildExec.Matches(maskedPrefix).Where(x => x.Index >= segStart).Select(x => x.Groups["p"].Value).FirstOrDefault();
                return Unsupported($"Lỗi nằm trong store con {err.Procedure} (dòng {err.LineNumber} của store đó){(child is null ? "" : $", gọi ở đoạn dòng {prev + 1}–{line}")}: {err.Message}\nMở {err.Procedure} rồi Debug từng bước bên trong để biết cột nào.", prev + 1, line);
            }
        }

        // 2a. Câu INSERT tĩnh trong đoạn — thử lần lượt: câu chứa dòng lỗi (2 câu cùng dòng: thử cả 2), câu trước đó; INSERT … EXEC store trong lỗi trước tiên.
        if (inserts.Count > 0)
            return await TryCandidatesAsync(Ordered(inserts, prefix, s => LineOf(s.Start), s => LineOf(Math.Max(s.Start, s.End - 1))), DiagnoseStatic);

        // 2b. SQL động (lồng tối đa 4 tầng: @q chứa EXEC @q2 …): đọc lần lượt giá trị biến SQL của từng tầng tới tầng có câu INSERT.
        var dyn = DynExec.Matches(maskedPrefix).FirstOrDefault(x => x.Index >= segStart);
        if (dyn is not null)
        {
            var layers = new List<(string Text, int Start, int End, string Var)>
            {
                (prefix, dyn.Index, StatementEnd(maskedPrefix, dyn.Index + 4, maskedPrefix.Length), dyn.Groups["v"].Value),
            };
            // rep thay câu EXEC của tầng j; đi ngược lên: mỗi tầng ngoài nhận text tầng trong (đã sửa) qua biến mới @__bcqN rồi chạy lại đúng câu EXEC gốc.
            string Propagate(int j, string rep, string top)
            {
                while (true)
                {
                    var L = layers[j];
                    if (j == 0) return L.Text[..L.Start] + (top.Length > 0 ? "BEGIN " + top + rep + " END " : rep) + L.Text[L.End..];
                    var newText = L.Text[..L.Start] + rep + L.Text[L.End..];
                    j--;
                    var P = layers[j];
                    var execText = P.Text[P.Start..P.End];
                    var vAt = SqlStepPlanner.MaskedCode(execText).IndexOf(P.Var, StringComparison.OrdinalIgnoreCase);
                    rep = $"BEGIN DECLARE @__bcq{j + 1} nvarchar(max); SET @__bcq{j + 1} = {N(newText)}; " + execText[..vAt] + $"@__bcq{j + 1}" + execText[(vAt + P.Var.Length)..] + "\n; END ";
                }
            }
            string? dynText = null;
            List<InsertStmt> dIns = new();
            for (var level = 0; level < 4; level++)
            {
                var last = layers[^1];
                var getText = Propagate(layers.Count - 1, $"BEGIN SELECT N'dyn' AS [__bcode_mark], CAST({last.Var} AS nvarchar(max)) AS [q]; END ", "");
                await using (var conn = connect())
                {
                    await conn.OpenAsync(ct);
                    try { var (sets, _) = await RunAsync(conn, Batch(plan, getText), ct); dynText = sets.FirstOrDefault(s => s.Mark == "dyn")?.Rows[0][1] as string; }
                    finally { await RollbackAsync(conn); }
                }
                if (string.IsNullOrWhiteSpace(dynText))
                    return Unsupported($"Lỗi trong SQL động {last.Var} (đoạn dòng {prev + 1}–{line}) nhưng không lấy được nội dung {last.Var} tại đó: {err.Message}", prev + 1, line);
                dIns = FindInserts(dynText, 0, dynText.Length);
                if (dIns.Count > 0) break;
                var md = SqlStepPlanner.MaskedCode(dynText);
                var inner = DynExec.Match(md);
                if (!inner.Success) break;
                layers.Add((dynText, inner.Index, StatementEnd(md, inner.Index + 4, md.Length), inner.Groups["v"].Value));
            }
            var outerVar = layers[0].Var;
            if (dIns.Count == 0 || dynText is null)
                return namedProc && !err.Procedure.Contains("Execute", StringComparison.OrdinalIgnoreCase)
                    ? Unsupported($"Lỗi nằm trong store con {err.Procedure} (dòng {err.LineNumber} của store đó), gọi qua SQL động {outerVar} ở đoạn dòng {prev + 1}–{line}: {err.Message}\nMở {err.Procedure} rồi Debug từng bước bên trong để biết cột nào.", prev + 1, line)
                    : Unsupported($"Lỗi trong SQL động {outerVar} (đoạn dòng {prev + 1}–{line}) — câu lỗi không phải INSERT (UPDATE / SELECT INTO…), chưa phân tích được cột: {err.Message}", prev + 1, line);
            var inText = dynText;
            var depthLabel = layers.Count > 1 ? $" ({layers.Count} tầng: {string.Join(" → ", layers.Select(l => l.Var))})" : "";
            Task<InsertErrorReport?> DiagnoseDyn(InsertStmt dst)
            {
                // Bảng nhân bản tạo ở tầng NGOÀI CÙNG (bảng tạm tạo trong SQL động mất khi SQL động chạy xong); đích phải thấy được từ ngoài.
                var newInner = inText[..dst.Start] + DiagnosticFor(inText, dst, createClone: false, includeInsert: !IsCountMismatch(errNo)) + inText[dst.End..];
                var last = layers[^1];
                var execText = last.Text[last.Start..last.End];
                var vAt = SqlStepPlanner.MaskedCode(execText).IndexOf(last.Var, StringComparison.OrdinalIgnoreCase);
                var n = layers.Count;
                var rep = $"BEGIN DECLARE @__bcq{n} nvarchar(max); SET @__bcq{n} = {N(newInner)}; " + execText[..vAt] + $"@__bcq{n}" + execText[(vAt + last.Var.Length)..] + "\n; END ";
                // Propagate thay câu EXEC của tầng cuối bằng rep (đã gói text mới) — nên gọi với tầng cuối nhưng rep thay ĐÚNG câu EXEC đó.
                var text = PropagateExec(n - 1, rep, CloneSql(dst.Target));
                return DiagnoseAsync(plan, Batch(plan, text), dst, LineOf(dyn.Index), LineOf(Math.Max(dyn.Index, layers[0].End - 1)), err, errNo, connect, ct);
            }
            // Như Propagate nhưng rep thay chính câu EXEC của tầng j (không phải nội dung bên trong).
            string PropagateExec(int j, string rep, string top) => Propagate(j, rep, top);
            var dPos = PosOfLine(inText, err.LineNumber);
            var dCands = namedProc
                ? dIns.Where(s => ExecsProc(inText, s)).Concat(dIns.AsEnumerable().Reverse())
                : dIns.Where(s => s.Start <= dPos && s.End >= dPos).Concat(dIns.AsEnumerable().Reverse());
            var rep = await TryCandidatesAsync(dCands, DiagnoseDyn);
            return rep is null ? null : rep with { Summary = $"[SQL động {outerVar}{depthLabel}] " + rep.Summary };
        }

        // 2c. Lỗi báo ở THROW / RAISERROR trong khối CATCH của chính store (ném lại lỗi của khối TRY): tìm câu INSERT trong khối TRY ngay trước đó.
        var tryAt = Regex.Matches(maskedPrefix[..segStart], @"\bBEGIN\s+TRY\b", RegexOptions.IgnoreCase).LastOrDefault();
        if (tryAt is not null && Regex.IsMatch(maskedPrefix[tryAt.Index..], @"\bBEGIN\s+CATCH\b", RegexOptions.IgnoreCase))
        {
            var tryInserts = FindInserts(prefix, tryAt.Index, segStart);
            if (tryInserts.Count > 0)
            {
                var rep = await TryCandidatesAsync(tryInserts.AsEnumerable().Reverse(), DiagnoseStatic);
                if (rep is not null) return rep with { Summary = "[lỗi bị CATCH bắt rồi ném lại] " + rep.Summary };
            }
        }

        // 2d. SQL báo lỗi ở dòng TRƯỚC đoạn (vòng WHILE: câu INSERT phía trên chỉ lỗi ở vòng sau, khi câu tăng biến/FETCH phía dưới đã chạy)
        //     → phân tích câu INSERT chứa dòng SQL báo.
        if (!namedProc && err.LineNumber >= 1 && err.LineNumber <= prev)
        {
            var above = FindInserts(prefix, 0, prefix.Length)
                .Where(s => LineOf(s.Start) <= err.LineNumber && LineOf(Math.Max(s.Start, s.End - 1)) >= err.LineNumber).ToList();
            if (above.Count > 0)
            {
                var rep = await TryCandidatesAsync(above, DiagnoseStatic);
                if (rep is not null) return rep with { Summary = $"[lỗi ở vòng lặp sau — chỉ xảy ra khi đã chạy tới dòng {line}] " + rep.Summary };
            }
        }

        if (Regex.IsMatch(maskedPrefix[segStart..], @"\bUPDATE\b", RegexOptions.IgnoreCase))
            return Unsupported($"Câu lỗi ở đoạn dòng {prev + 1}–{line} là UPDATE — bản này mới phân tích cột cho INSERT (tĩnh và SQL động): {err.Message}", prev + 1, line);
        return Unsupported($"Không thấy câu INSERT nào ở đoạn dòng {prev + 1}–{line} để phân tích cột: {err.Message}", prev + 1, line);
    }

    /// <summary>Số lỗi thật: lỗi ném lại qua RAISERROR (50000) mang "Msg 8152: …" trong thông điệp (CATCH tự thêm của debugger) → 8152.</summary>
    private static int RealNumber(SqlError e)
    {
        if (e.Number != 50000) return e.Number;
        foreach (Match m in Regex.Matches(e.Message ?? "", @"Msg (\d+):"))
            if (int.TryParse(m.Groups[1].Value, out var n) && n != 50000) return n;
        return e.Number;
    }

    /// <summary>Kết quả đọc 1 câu INSERT (cho kiểm thử độ chính xác — đối chiếu với ScriptDom): vị trí, dòng, bảng đích, kiểu nguồn, số cột trong danh sách cột
    /// (-1 = không có), số biểu thức nguồn tách được (0 = không tách được: *, EXEC…), có OUTPUT; FullTextWithDiagnostic = toàn bộ text với câu này đã thay bằng
    /// khối chẩn đoán (phải vẫn là T-SQL hợp lệ).</summary>
    public sealed record InsertInfo(int Start, int End, int Line, string Target, string SourceKind, int ColumnListCount, int SourceItemCount, bool HasOutput,
        string FullTextWithDiagnostic);

    public static List<InsertInfo> InspectInserts(string text)
    {
        var list = new List<InsertInfo>();
        foreach (var st in FindInserts(text, 0, text.Length))
        {
            var line = 1;
            for (var i = 0; i < st.Start; i++) if (text[i] == '\n') line++;
            list.Add(new InsertInfo(st.Start, st.End, line, st.Target, st.SourceKind, st.ColumnList?.Count ?? -1, st.SourceItems.Count, st.HasOutput,
                text[..st.Start] + DiagnosticFor(text, st, createClone: true) + text[st.End..]));
        }
        return list;
    }

    private static int PosOfLine(string text, int line)
    {
        var pos = 0;
        for (var i = 1; i < line && pos >= 0; i++) { pos = text.IndexOf('\n', pos); if (pos >= 0) pos++; }
        return Math.Max(0, pos);
    }

    /// <summary>Tên cột nguồn: tên FMTONLY trả về; nếu không có thì alias sau AS / định danh cuối của biểu thức.</summary>
    private static string SourceName(List<Col>? srcCols, IReadOnlyList<string> items, int i)
    {
        if (srcCols is not null && i < srcCols.Count) return (srcCols[i].Name ?? "").Trim();
        if (i >= items.Count) return "";
        var m = Regex.Match(Flat(items[i]), @"(?:\bAS\s+)?\[?([A-Za-z_][\w$#@]*)\]?\s*$", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Căn chỉnh cột đích với cột nguồn theo tên (quy hoạch động). Trả về các cặp (chỉ số đích | -1, chỉ số nguồn | -1) theo thứ tự.
    /// Cùng tên = 0, nguồn không tên = 5 (ghép yếu), khác tên = 10, thừa/thiếu = 15; hoà thì ưu tiên ghép theo vị trí.</summary>
    internal static List<(int, int)> AlignColumns(IReadOnlyList<string> target, IReadOnlyList<string> source,
        IReadOnlyList<string>? targetTypes = null, IReadOnlyList<string>? sourceTypes = null)
    {
        int n = target.Count, m = source.Count;
        var d = new int[n + 1, m + 1];
        for (var i = 0; i <= n; i++) d[i, 0] = i * 15;
        for (var j = 0; j <= m; j++) d[0, j] = j * 15;
        // Nguồn không tên (sum(...), '', 1…) thì nhóm kiểu quyết định: số ↔ số, chuỗi ↔ chuỗi, ngày ↔ ngày; khác nhóm phạt thêm.
        int TypePenalty(int i, int j)
        {
            if (targetTypes is null || sourceTypes is null || i >= targetTypes.Count || j >= sourceTypes.Count) return 0;
            var (a, b) = (TypeFamily(targetTypes[i]), TypeFamily(sourceTypes[j]));
            return a.Length == 0 || b.Length == 0 || a == b ? 0 : 5;
        }
        int Sub(int i, int j) => source[j].Equals(target[i], StringComparison.OrdinalIgnoreCase) && source[j].Length > 0 ? 0
            : (source[j].Length == 0 ? 3 : 10) + TypePenalty(i, j);
        for (var i = 1; i <= n; i++)
            for (var j = 1; j <= m; j++)
                d[i, j] = Math.Min(d[i - 1, j - 1] + Sub(i - 1, j - 1), Math.Min(d[i, j - 1], d[i - 1, j]) + 15);
        var res = new List<(int, int)>();
        int a = n, b = m;
        while (a > 0 || b > 0)
        {
            if (a > 0 && b > 0 && d[a, b] == d[a - 1, b - 1] + Sub(a - 1, b - 1)) { res.Add((a - 1, b - 1)); a--; b--; }
            else if (b > 0 && d[a, b] == d[a, b - 1] + 15) { res.Add((-1, b - 1)); b--; }
            else { res.Add((a - 1, -1)); a--; }
        }
        res.Reverse();
        return res;
    }

    private static string TypeFamily(string? type)
    {
        var t = (type ?? "").ToLowerInvariant();
        var p = t.IndexOf('('); if (p >= 0) t = t[..p];
        t = t.Trim();
        return t switch
        {
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "xml" => "s",
            "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "float" or "real" or "money" or "smallmoney" or "bit" => "n",
            "date" or "datetime" or "smalldatetime" or "datetime2" or "time" or "datetimeoffset" => "d",
            "binary" or "varbinary" or "image" => "b",
            _ => "",
        };
    }

    private static InsertErrorReport Unsupported(string summary, int from, int to) =>
        new("unsupported", "", from, to, summary, NewDetails(), Array.Empty<string>());

    private static async Task<InsertErrorReport?> DiagnoseAsync(StepPlan plan, string batch, InsertStmt st, int fromLine, int toLine, SqlError origErr,
        int origNo, Func<SqlConnection> connect, CancellationToken ct)
    {
        await using var conn = connect();
        await conn.OpenAsync(ct);
        try
        {
            var (sets, batchErr) = await RunAsync(conn, batch, ct);
            var tcols = await TargetColumnsAsync(conn, ct);

            // 207 do cột sai nằm ở phía SELECT nguồn (không thuộc danh sách cột INSERT): khối chẩn đoán cũng không biên dịch được,
            // nên báo thẳng từ thông điệp lỗi gốc + chỉ ra biểu thức nguồn chứa tên cột đó.
            if (origNo == 207 || batchErr?.Number == 207)
            {
                var bad = Regex.Matches(origErr.Message + "\n" + batchErr?.Message, @"Invalid column name '([^']+)'", RegexOptions.IgnoreCase)
                    .Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(n => st.ColumnList is not { Count: > 0 } || !st.ColumnList.Any(c => c.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (bad.Count > 0) // không có danh sách cột thì 207 chắc chắn ở phía nguồn
                {
                    var details0 = NewDetails();
                    var srcLines = new List<string>();
                    foreach (var name in bad)
                    {
                        var rx = new Regex(@"(?<![\w@#$])(?:\[?\w+\]?\.)?\[?" + Regex.Escape(name) + @"\]?(?![\w@#$])", RegexOptions.IgnoreCase);
                        var pos = -1;
                        for (var i = 0; i < st.SourceItems.Count && pos < 0; i++) if (rx.IsMatch(st.SourceItems[i])) pos = i;
                        var expr = pos >= 0 ? Short(Flat(st.SourceItems[pos])) : "";
                        details0.Rows.Add(pos >= 0 ? (pos + 1).ToString(CultureInfo.InvariantCulture) : "", pos >= 0 && st.ColumnList is { } cl && pos < cl.Count ? cl[pos] : "", "",
                            pos >= 0 ? "Cột không tồn tại ở bảng nguồn của SELECT" : "Cột không tồn tại (phía nguồn: FROM / JOIN / WHERE…)", "", "", "", name, expr);
                        srcLines.Add(pos >= 0 ? $"'{name}' ở cột nguồn #{pos + 1}: {expr}" : $"'{name}' (trong FROM/JOIN/WHERE/ORDER BY của phần SELECT)");
                    }
                    return new InsertErrorReport("invalidcol", st.Target, fromLine, toLine,
                        $"Câu INSERT {st.Target} (dòng {fromLine}–{toLine}): cột không tồn tại ở phía NGUỒN (SELECT), không phải bảng đích — {string.Join("; ", srcLines)}. " +
                        "Kiểm tra bí danh bảng (a./b.) và tên cột của bảng nguồn.", details0, bad);
                }
            }

            if (tcols.Count == 0)
                return Unsupported($"Không đọc được cấu trúc bảng đích {st.Target} tại dòng {fromLine} (bảng chưa tồn tại / tạo bên trong SQL động?). Lỗi gốc: {origErr.Message}" +
                                   (batchErr is null ? "" : $"\nLỗi khi chẩn đoán: {batchErr.Message}"), fromLine, toLine);

            // Cấu trúc nguồn từ FMTONLY (bảng ngay sau dấu 'src'); số dòng đã ghi vào bản nhân bản; lỗi khi ghi vào bản nhân bản.
            List<Col>? srcCols = null; long inserted = 0; string? insErr = null; int insErrNo = 0;
            for (var i = 0; i < sets.Count; i++)
                switch (sets[i].Mark)
                {
                    case "src": if (srcCols is null && i + 1 < sets.Count && sets[i + 1].Mark is null) srcCols = sets[i + 1].Cols; break;
                    case "ins": inserted += Convert.ToInt64(sets[i].Rows[0][1] ?? 0L); break;
                    case "err": insErr ??= sets[i].Rows[0][2] as string; insErrNo = Convert.ToInt32(sets[i].Rows[0][1] ?? 0); break;
                }
            if (insErr is null && batchErr is not null && (IsCountMismatch(batchErr.Number) || batchErr.Number is 207 or 8101)) { insErr = batchErr.Message; insErrNo = batchErr.Number; }

            // Cột đích được ghi theo thứ tự: danh sách cột của INSERT, hoặc mọi cột (trừ identity / computed / timestamp).
            var details = NewDetails();
            var flagged = new List<string>();
            List<TCol> used;
            var missing = new List<string>();
            if (st.ColumnList is { Count: > 0 })
            {
                used = new();
                foreach (var name in st.ColumnList)
                {
                    var c = tcols.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (c is null) missing.Add(name); else used.Add(c);
                }
            }
            else used = tcols.Where(c => !c.Identity && !c.Computed && c.Type != "timestamp").ToList();

            string SrcExpr(int pos) => pos < st.SourceItems.Count ? Short(Flat(st.SourceItems[pos])) : "";
            string SrcType(int pos)
            {
                if (srcCols is null || pos >= srcCols.Count) return "";
                var c = srcCols[pos];
                var t = c.Type.ToLowerInvariant();
                return t + (t is "varchar" or "nvarchar" or "char" or "nchar" or "varbinary" or "binary" ? $"({(c.Size >= 1073741823 || c.Size <= 0 ? "max" : c.Size.ToString(CultureInfo.InvariantCulture))})"
                    : t is "decimal" or "numeric" ? $"({c.Precision},{c.Scale})" : "");
            }

            // --- Cột không tồn tại trong bảng đích (207)
            if (missing.Count > 0)
            {
                foreach (var name in missing)
                {
                    var near = tcols.Select(c => c.Name).OrderBy(n => Distance(n.ToLowerInvariant(), name.ToLowerInvariant())).FirstOrDefault();
                    details.Rows.Add("", name, "(không có trong bảng)", "Cột không tồn tại", "", near is null ? "" : "gần giống: " + near, "", "", "");
                    flagged.Add(name);
                }
                return new InsertErrorReport("invalidcol", st.Target, fromLine, toLine,
                    $"Câu INSERT {st.Target} (dòng {fromLine}–{toLine}): cột {string.Join(", ", missing)} không có trong bảng đích ({tcols.Count} cột: {string.Join(", ", tcols.Select(c => c.Name).Take(30))}{(tcols.Count > 30 ? ", …" : "")}).",
                    details, flagged);
            }

            // --- Lệch số cột (213): đối chiếu từng vị trí
            var srcCount = srcCols?.Count ?? (st.SourceItems.Count > 0 ? st.SourceItems.Count : -1);
            if (IsCountMismatch(insErrNo) || insErrNo == 8101 || IsCountMismatch(origNo) || (srcCount >= 0 && srcCount != used.Count && st.SourceKind != "DEFAULT"))
            {
                // Ghép cột theo TÊN (căn chỉnh chuỗi), không chỉ theo vị trí: cột thừa/thiếu ở giữa (vd thêm a.ma_vv trước các cột động)
                // sẽ được chỉ đúng chỗ thay vì dồn về cuối. Tên nguồn trống (biểu thức không alias) chỉ ghép yếu → giữ theo vị trí.
                var srcNames = Enumerable.Range(0, Math.Max(srcCount, 0)).Select(i => SourceName(srcCols, st.SourceItems, i)).ToList();
                var pairs = AlignColumns(used.Select(c => c.Name).ToList(), srcNames, used.Select(c => c.Type).ToList(),
                    Enumerable.Range(0, srcNames.Count).Select(i => srcCols is not null && i < srcCols.Count ? srcCols[i].Type : "").ToList());
                var extras = new List<string>(); var lacks = new List<string>();
                foreach (var (ti, si) in pairs)
                {
                    var tc = ti >= 0 ? used[ti] : null;
                    var hasSrc = si >= 0;
                    string note;
                    if (tc is null)
                    {
                        var dup = -1;
                        for (var k = 0; k < srcNames.Count && srcNames[si].Length > 0; k++)
                            if (k != si && srcNames[k].Equals(srcNames[si], StringComparison.OrdinalIgnoreCase)) { dup = k; break; }
                        note = "THỪA ở nguồn (bảng đích không có cột này)" + (dup >= 0 ? $" — trùng tên '{srcNames[si]}' với cột nguồn #{dup + 1}" : "");
                        flagged.Add($"nguồn #{si + 1}");
                        extras.Add($"#{si + 1} {SrcExpr(si)}" + (dup >= 0 ? $" (trùng tên với cột nguồn #{dup + 1})" : ""));
                    }
                    else if (!hasSrc) { note = "THIẾU ở nguồn"; flagged.Add(tc.Name); lacks.Add(tc.Name); }
                    else note = srcNames[si].Equals(tc.Name, StringComparison.OrdinalIgnoreCase) ? (si == ti ? "khớp" : $"khớp tên (nguồn #{si + 1} → đích #{ti + 1})") : "khớp vị trí";
                    details.Rows.Add(hasSrc ? (si + 1).ToString(CultureInfo.InvariantCulture) : "—", tc?.Name ?? "", tc?.Display ?? "", note,
                        "", hasSrc ? SrcType(si) : "", "", hasSrc ? srcNames[si] : "", hasSrc ? SrcExpr(si) : "");
                }
                var atEnd = extras.Count > 0 && pairs.SkipWhile(p => p.Item1 >= 0).All(p => p.Item1 < 0);
                return new InsertErrorReport("mismatch", st.Target, fromLine, toLine,
                    $"Câu INSERT {st.Target} (dòng {fromLine}–{toLine}): bảng đích nhận {used.Count} cột{(st.ColumnList is null ? $" (theo cấu trúc bảng, không ghi danh sách cột)" : " (theo danh sách cột)")} nhưng nguồn trả {(srcCount < 0 ? "?" : srcCount.ToString(CultureInfo.InvariantCulture))} cột" +
                    (extras.Count > 0 ? (atEnd ? $" — thừa {extras.Count} cột ở cuối nguồn: " : $" — thừa {extras.Count} cột nguồn: ") + string.Join("; ", extras) : "") +
                    (lacks.Count > 0 ? $" — thiếu {lacks.Count} cột: {string.Join(", ", lacks)}" : "") +
                    ". Cột nguồn (STT) được ghép với cột đích theo tên, xem bảng đối chiếu.", details, flagged);
            }

            // --- Dữ liệu: vượt độ dài / tràn số / sai kiểu / NULL
            if (insErr is not null && inserted == 0)
                return Unsupported($"Câu INSERT {st.Target} (dòng {fromLine}–{toLine}) vẫn lỗi khi ghi vào bảng nhân bản nới rộng ({insErrNo}): {insErr}. Lỗi gốc: {origErr.Message}", fromLine, toLine);
            var (aSets, aErr) = await RunAsync(conn, AnalysisSql(used), ct);
            if (aErr is not null || aSets.Count == 0)
                return Unsupported($"Không phân tích được dữ liệu của câu INSERT {st.Target}: {aErr?.Message}. Lỗi gốc: {origErr.Message}", fromLine, toLine);
            var kinds = new List<string>();
            foreach (var r in aSets[0].Rows)
            {
                var id = Convert.ToInt32(r[0]); var col = (string)r[1]!; var problem = (string)r[2]!; var cnt = Convert.ToInt32(r[5] ?? 0); var nulls = Convert.ToInt32(r[7] ?? 0);
                var tc = tcols.First(c => c.Id == id);
                var pos = used.IndexOf(tc);
                if (cnt > 0 && problem != "none")
                {
                    details.Rows.Add((pos + 1).ToString(CultureInfo.InvariantCulture), col, tc.Display, ProblemVi[problem], (string?)r[3] ?? "", (string?)r[4] ?? "",
                        cnt.ToString(CultureInfo.InvariantCulture), Short(r[6] as string, 200), SrcExpr(pos));
                    flagged.Add(col); kinds.Add(problem);
                }
                if (!tc.Nullable && !tc.Identity && nulls > 0)
                {
                    details.Rows.Add((pos + 1).ToString(CultureInfo.InvariantCulture), col, tc.Display, ProblemVi["null"], "NOT NULL", "NULL",
                        nulls.ToString(CultureInfo.InvariantCulture), "", SrcExpr(pos));
                    if (!flagged.Contains(col)) flagged.Add(col);
                    kinds.Add("null");
                }
            }
            // Cột NOT NULL không có trong danh sách cột (không giá trị mặc định khi tạo bằng SELECT INTO) — chỉ gợi ý khi lỗi gốc là NULL.
            if (origNo == 515 && st.ColumnList is { Count: > 0 } && kinds.Count == 0)
                foreach (var c in tcols.Where(c => !c.Nullable && !c.Identity && !c.Computed && !used.Contains(c)))
                {
                    details.Rows.Add("", c.Name, c.Display, "NOT NULL nhưng không có trong danh sách cột", "", "", "", "", "");
                    flagged.Add(c.Name); kinds.Add("null");
                }

            if (flagged.Count == 0)
                return Unsupported($"Đã chạy lại câu INSERT {st.Target} (dòng {fromLine}–{toLine}, {inserted} dòng) vào bảng nhân bản nhưng không thấy cột nào vượt giới hạn — có thể lỗi phụ thuộc dữ liệu đổi giữa các lượt chạy. Lỗi gốc: {origErr.Message}", fromLine, toLine);

            var kind = kinds.GroupBy(k => k).OrderByDescending(g => g.Count()).First().Key;
            var lines = details.Rows.Cast<DataRow>().Select(r =>
                $"  • {r["Cột đích"]} {r["Kiểu đích"]}: {r["Vấn đề"]}" + ((string)r["Thực tế"] is { Length: > 0 } a ? $" (thực tế {a}{((string)r["Giới hạn"] is { Length: > 0 } l ? ", giới hạn " + l : "")})" : "") +
                ((string)r["Số dòng lỗi"] is { Length: > 0 } c2 ? $", {c2} dòng" : "") + ((string)r["Giá trị mẫu"] is { Length: > 0 } s ? $", ví dụ: '{Short(s, 60)}'" : "") +
                ((string)r["Biểu thức nguồn"] is { Length: > 0 } e ? $" ← {e}" : ""));
            return new InsertErrorReport(kind, st.Target, fromLine, toLine,
                $"Câu INSERT {st.Target} (dòng {fromLine}–{toLine}, {inserted} dòng): {flagged.Distinct().Count()} cột gây lỗi:\n" + string.Join("\n", lines), details, flagged.Distinct().ToList());
        }
        finally { await RollbackAsync(conn); }
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
