using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>Tham số của store/function đang debug (đọc từ phần header CREATE/ALTER PROC|FUNCTION).</summary>
public sealed record StepParam(string Name, string Type, string Default, bool ReadOnly);

/// <summary>
/// Kế hoạch debug từng bước cho 1 script / body của store/function. Mỗi "bước" là chạy script TỪ ĐẦU ĐẾN HẾT dòng N trong 1 transaction
/// rồi ROLLBACK (nên không để lại dữ liệu), kèm 1 lượt SELECT các biến đã khai báo để xem giá trị tại thời điểm dừng. Số dòng được giữ
/// nguyên so với editor (header CREATE PROC... được thay bằng khoảng trắng, DECLARE tham số nằm cùng dòng 1) nên số dòng trong thông báo
/// lỗi của SQL Server khớp đúng dòng trên editor.
///
/// Giới hạn (heuristic phía client, không phải parser T-SQL đầy đủ): không dừng được ở dòng "IF/WHILE/ELSE" còn chờ câu lệnh ở dòng sau
/// hay dòng "BEGIN" trống; CTE (WITH ...) và inline table-valued function (RETURNS TABLE AS RETURN (...)) chưa hỗ trợ.
/// </summary>
public sealed class StepPlan
{
    public required string[] Lines { get; init; }
    public required List<int> SafeLines { get; init; }
    public required string DeclareSql { get; init; }
    public required List<StepParam> Params { get; init; }
    public string? RoutineName { get; init; }

    /// <summary>Dòng an toàn kế tiếp sau <paramref name="after"/> (0 = từ đầu); 0 nếu hết.</summary>
    public int NextSafe(int after) => SafeLines.FirstOrDefault(l => l > after);

    public const string WatchSentinel = "__BCODE_WATCH__";

    /// <summary>Batch chạy từ đầu đến hết dòng <paramref name="toLine"/>. Trả thêm số bảng "watch" ở cuối kết quả (nếu batch chạy tới đoạn watch).</summary>
    public (string Sql, List<string> WatchNames) BuildBatch(int toLine)
    {
        var prefixLines = Lines.Take(toLine).ToArray();
        var prefix = string.Join("\n", prefixLines);
        var code = SqlStepPlanner.MaskedCode(prefix);

        var sb = new StringBuilder();
        // Mọi thứ thêm vào đầu nằm CÙNG dòng 1 để không lệch số dòng.
        sb.Append("SET XACT_ABORT OFF; BEGIN TRAN; ").Append(DeclareSql).Append(' ');
        sb.Append(prefix);
        var closers = SqlStepPlanner.Closers(code);
        if (closers.Length > 0) sb.Append('\n').Append(closers);
        sb.Append(";\n");

        var (scalars, tableVars) = SqlStepPlanner.DeclaredVariables(code, Params);
        var watchNames = new List<string>();
        sb.Append("PRINT '").Append(WatchSentinel).Append("';\n");
        if (scalars.Count > 0)
        {
            sb.Append("SELECT ").Append(string.Join(", ", scalars.Select(v => $"{v} AS [{v}]"))).Append(";\n");
            watchNames.Add("Variables");
        }
        foreach (var t in tableVars)
        {
            sb.Append("SELECT * FROM ").Append(t).Append(";\n");
            watchNames.Add(t);
        }
        return (sb.ToString(), watchNames);
    }
}

public static class SqlStepPlanner
{
    private static readonly Regex HeaderRegex = new(
        @"\b(CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(PROC|PROCEDURE|FUNCTION)\s+([\w\.\[\]#$]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WordRegex = new(@"\b[A-Za-z_@#$][\w@#$]*\b", RegexOptions.Compiled);
    private static readonly Regex StatementWord = new(
        @"\b(BEGIN|SELECT|SET|EXEC|EXECUTE|PRINT|INSERT|UPDATE|DELETE|MERGE|RETURN|BREAK|CONTINUE|RAISERROR|THROW|DECLARE|TRUNCATE|WAITFOR|GOTO)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GoLine = new(@"^[ \t]*GO[ \t]*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>Text với comment và nội dung chuỗi '...' thay bằng khoảng trắng (giữ nguyên độ dài, xuống dòng) — để regex không bị lừa.</summary>
    public static string MaskedCode(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                while (i < text.Length && text[i] != '\n') { sb.Append(' '); i++; }
            }
            else if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? text.Length : end + 2;
                for (; i < stop; i++) sb.Append(text[i] == '\n' ? '\n' : ' ');
            }
            else if (c == '\'')
            {
                sb.Append('\''); i++;
                while (i < text.Length)
                {
                    if (text[i] == '\'')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '\'') { sb.Append("  "); i += 2; continue; }
                        break;
                    }
                    sb.Append(text[i] == '\n' ? '\n' : ' '); i++;
                }
                if (i < text.Length) { sb.Append('\''); i++; }
            }
            else { sb.Append(c); i++; }
        }
        return sb.ToString();
    }

    /// <summary>Phần còn dang dở cuối đoạn <paramref name="code"/> (BEGIN chưa END, TRY chưa đóng...) → câu lệnh đóng lại cho batch hợp lệ.</summary>
    public static string Closers(string code)
    {
        var words = WordRegex.Matches(code).Select(m => m.Value.ToUpperInvariant()).ToList();
        var stack = new Stack<char>();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            var next = i + 1 < words.Count ? words[i + 1] : "";
            switch (w)
            {
                case "BEGIN":
                    if (next is "TRAN" or "TRANSACTION" or "DISTRIBUTED") break;
                    if (next == "TRY") { stack.Push('T'); i++; }
                    else if (next == "CATCH") { stack.Push('K'); i++; }
                    else stack.Push('B');
                    break;
                case "CASE": stack.Push('C'); break;
                case "END":
                    if (next == "CONVERSATION") break;
                    if (next is "TRY" or "CATCH") i++;
                    if (stack.Count > 0) stack.Pop();
                    break;
            }
        }
        var parts = new List<string>();
        foreach (var open in stack) // từ trong ra ngoài
            parts.Add(open switch
            {
                'T' => "END TRY BEGIN CATCH SELECT ERROR_NUMBER() AS error_number, ERROR_LINE() AS error_line, ERROR_MESSAGE() AS error_message END CATCH",
                'K' => "END CATCH",
                _ => "END",
            });
        return string.Join("\n", parts);
    }

    private static readonly Regex DeclareRegex = new(@"\bDECLARE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex VarTypeRegex = new(@"(?<![\w@#$])(@[\w#$]+)\s+(?:AS\s+)?([A-Za-z_]\w*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Biến vô hướng và biến bảng đã khai báo trong <paramref name="code"/> (cộng tham số không READONLY) — để SELECT ra xem.</summary>
    public static (List<string> Scalars, List<string> TableVars) DeclaredVariables(string code, List<StepParam> parameters)
    {
        var scalars = new List<string>();
        var tables = new List<string>();
        foreach (var p in parameters)
        {
            if (p.ReadOnly) tables.Add(p.Name);
            else scalars.Add(p.Name);
        }

        var lines = code.Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            var m = DeclareRegex.Match(lines[li]);
            if (!m.Success) continue;
            var region = new StringBuilder(lines[li][m.Index..]);
            // DECLARE @a int,\n @b int — các dòng tiếp theo nếu dòng trước kết thúc bằng dấu phẩy.
            var k = li;
            while (lines[k].TrimEnd().EndsWith(',') && k + 1 < lines.Length) { k++; region.Append(' ').Append(lines[k]); }
            foreach (Match v in VarTypeRegex.Matches(region.ToString()))
            {
                var name = v.Groups[1].Value;
                if (name.StartsWith("@@")) continue;
                var type = v.Groups[2].Value;
                if (type.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
                { if (!tables.Contains(name, StringComparer.OrdinalIgnoreCase)) tables.Add(name); }
                else if (!type.Equals("CURSOR", StringComparison.OrdinalIgnoreCase) && !scalars.Contains(name, StringComparer.OrdinalIgnoreCase))
                    scalars.Add(name);
            }
        }
        return (scalars, tables);
    }

    /// <summary>Đọc header CREATE/ALTER PROC|FUNCTION (nếu có): tên, tham số, và vị trí token AS kết thúc header.</summary>
    public static (string? Name, List<StepParam> Params, int BodyStart) ParseHeader(string text)
    {
        var code = MaskedCode(text);
        var m = HeaderRegex.Match(code);
        if (!m.Success) return (null, new List<StepParam>(), 0);

        var isFunction = m.Groups[2].Value.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase);
        var depth = 0;
        var asIndex = -1;
        for (var i = m.Index + m.Length; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '(') depth++;
            else if (c == ')') { if (depth > 0) depth--; }
            else if (depth == 0 && (c == 'A' || c == 'a') && i + 1 < code.Length && (code[i + 1] == 'S' || code[i + 1] == 's')
                     && (i == 0 || !(char.IsLetterOrDigit(code[i - 1]) || code[i - 1] == '_' || code[i - 1] == '@' || code[i - 1] == '#' || code[i - 1] == '$'))
                     && (i + 2 >= code.Length || !(char.IsLetterOrDigit(code[i + 2]) || code[i + 2] == '_' || code[i + 2] == '@' || code[i + 2] == '#' || code[i + 2] == '$')))
            { asIndex = i; break; }
        }
        if (asIndex < 0) return (null, new List<StepParam>(), 0);

        // Vùng tham số: từ sau tên đến AS; function thì cắt tại RETURNS, proc cắt tại WITH (RECOMPILE/ENCRYPTION...).
        var start = m.Index + m.Length;
        var region = code[start..asIndex];
        var cut = Regex.Match(region, isFunction ? @"\bRETURNS\b" : @"\bWITH\b", RegexOptions.IgnoreCase);
        var regionLen = cut.Success ? cut.Index : region.Length;
        var paramCode = region[..regionLen];
        var paramText = text.Substring(start, regionLen);

        // Bỏ cặp ngoặc bao ngoài nếu có: CREATE PROC x (@a int, @b int) AS
        var firstNonSpace = paramCode.Length - paramCode.TrimStart().Length;
        if (paramCode.TrimStart().StartsWith('('))
        {
            var d = 0; var close = -1;
            for (var i = firstNonSpace; i < paramCode.Length; i++)
            {
                if (paramCode[i] == '(') d++;
                else if (paramCode[i] == ')') { d--; if (d == 0) { close = i; break; } }
            }
            if (close > 0) { paramCode = paramCode.Substring(firstNonSpace + 1, close - firstNonSpace - 1); paramText = paramText.Substring(firstNonSpace + 1, close - firstNonSpace - 1); }
        }

        var parameters = new List<StepParam>();
        var depth2 = 0; var segStart = 0;
        for (var i = 0; i <= paramCode.Length; i++)
        {
            var end = i == paramCode.Length;
            if (!end)
            {
                if (paramCode[i] == '(') depth2++;
                else if (paramCode[i] == ')') depth2--;
            }
            if (end || (paramCode[i] == ',' && depth2 == 0))
            {
                var segCode = paramCode[segStart..i];
                var segText = paramText[segStart..i];
                segStart = i + 1;
                var p = ParseParam(segCode, segText);
                if (p is not null) parameters.Add(p);
            }
        }
        return (m.Groups[3].Value, parameters, asIndex + 2);
    }

    private static StepParam? ParseParam(string segCode, string segText)
    {
        var nm = Regex.Match(segCode, @"@[\w#$]+");
        if (!nm.Success) return null;
        var name = nm.Value;
        var afterName = nm.Index + nm.Length;
        var eq = segCode.IndexOf('=', afterName);
        var typeText = (eq >= 0 ? segText[afterName..eq] : segText[afterName..]).Trim();
        var def = eq >= 0 ? segText[(eq + 1)..].Trim() : "";

        var readOnly = Regex.IsMatch(typeText + " " + def, @"\bREADONLY\b", RegexOptions.IgnoreCase);
        typeText = Regex.Replace(typeText, @"\b(OUT|OUTPUT|READONLY|VARYING)\b", "", RegexOptions.IgnoreCase).Trim();
        typeText = Regex.Replace(typeText, @"^AS\s+", "", RegexOptions.IgnoreCase).Trim();
        def = Regex.Replace(def, @"\s+\b(OUT|OUTPUT|READONLY)\b\s*$", "", RegexOptions.IgnoreCase).Trim();
        if (typeText.Length == 0) return null;
        return new StepParam(name, typeText, def, readOnly);
    }

    /// <summary>Dựng kế hoạch từ text editor. <paramref name="paramValues"/>: giá trị T-SQL (literal) cho từng tham số (null/thiếu = NULL).</summary>
    public static StepPlan Build(string text, Dictionary<string, string>? paramValues)
    {
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var (name, parameters, bodyStart) = ParseHeader(text);

        // Dòng GO → trống (không tách batch được khi chạy từng bước).
        var processed = new StringBuilder(text.Length);
        // Header (và mọi thứ trước nó) → khoảng trắng, giữ xuống dòng để không lệch số dòng.
        for (var i = 0; i < text.Length; i++)
            processed.Append(i < bodyStart && text[i] != '\n' ? ' ' : text[i]);
        var body = GoLine.Replace(processed.ToString(), m => new string(' ', m.Length));

        var declare = "";
        var declarable = parameters.Where(p => !p.ReadOnly).ToList();
        if (declarable.Count > 0)
        {
            var items = declarable.Select(p =>
            {
                var value = paramValues is not null && paramValues.TryGetValue(p.Name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : "NULL";
                return $"{p.Name} {p.Type} = {value}";
            });
            declare = "DECLARE " + string.Join(", ", items) + ";";
        }

        var lines = body.Split('\n');
        var safe = Services.SqlLineAnalyzer.FindSafeLines(body).Where(l => IsRealStep(lines[l - 1])).OrderBy(l => l).ToList();
        return new StepPlan { Lines = lines, SafeLines = safe, DeclareSql = declare, Params = parameters, RoutineName = name };
    }

    /// <summary>Loại các dòng không dừng được: IF/WHILE/ELSE còn chờ câu lệnh ở dòng sau, và dòng kết thúc bằng BEGIN (khối rỗng là cú pháp sai).</summary>
    private static bool IsRealStep(string line)
    {
        var code = MaskedCode(line);
        var words = WordRegex.Matches(code).Select(m => m.Value).ToList();
        if (words.Count == 0) return false;
        var first = words[0].ToUpperInvariant();
        var last = words[^1].ToUpperInvariant();
        if (last == "BEGIN") return false;
        if (first is "IF" or "WHILE" or "ELSE")
        {
            // bỏ nội dung trong ngoặc rồi tìm từ khoá câu lệnh phía sau từ đầu tiên
            var sb = new StringBuilder();
            var depth = 0;
            foreach (var c in code)
            {
                if (c == '(') { depth++; sb.Append(' '); }
                else if (c == ')') { if (depth > 0) depth--; sb.Append(' '); }
                else sb.Append(depth > 0 ? ' ' : c);
            }
            var flat = sb.ToString().TrimStart();
            flat = flat[first.Length..];
            if (!StatementWord.IsMatch(flat)) return false;
            if (Regex.IsMatch(flat, @"\bBEGIN\s*$", RegexOptions.IgnoreCase)) return false;
        }
        return true;
    }
}
