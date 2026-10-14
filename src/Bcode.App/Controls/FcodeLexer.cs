using System.Text;
using System.Text.Json;
using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>
/// Tô màu file FCode (controller .f / .xml, file include) cho Scintilla ở chế độ App — bản C# của bộ tách token Monaco <c>fcode-language.js</c> mà chế độ Web dùng:
/// XML (thẻ, thuộc tính, comment, &lt;!DOCTYPE/ENTITY&gt;, <c>&amp;Entity;</c>) và mã NHÚNG trong từng khối CDATA, ngôn ngữ theo thẻ chứa nó —
/// &lt;script&gt;/&lt;clientScript&gt; = JavaScript, &lt;command&gt;/&lt;action&gt;/&lt;processing&gt; = T-SQL, &lt;css&gt;/&lt;style&gt; = CSS; khối mở bằng
/// <c>/* &lt;flatten type="Javascript"&gt; */</c> luôn là JavaScript; CDATA ngoài các thẻ đó (vd &lt;query&gt;, file mảnh) = T-SQL.
/// Lexer.Xml của Scintilla coi cả khối CDATA là một màu nên SQL/JS bên trong mất hết màu.
/// Màu lấy từ cùng file theme của BcodeViewer mà trang web dùng (%AppData%\Bcode\viewer-theme.json, luật token kiểu Monaco) nếu cùng sáng/tối với Bcode,
/// không thì bảng Dark+/Light+. Tô 1 lượt cả văn bản bằng SCI_SETSTYLINGEX (theo byte UTF-8) — file 100 KB vài ms.
/// </summary>
public static class FcodeLexer
{
    private enum K : byte
    {
        Text = 1, Delim, Tag, AttrName, AttrValue, Comment, Entity, Cdata, Meta,
        SqlKw, SqlFn, SqlStr, SqlNum, SqlCmt, SqlOp, SqlVar,
        JsKw, JsStr, JsNum, JsCmt,
        Brk1, Brk2, Brk3, SqlGo,
    }

    private sealed class State { public bool Active, SqlOnly; }

    /// <summary>Editor chỉ chứa T-SQL (tab SQL Query): cùng bộ tô T-SQL như trong CDATA của file FCode — hàm (kể cả dbo.X(...)) màu riêng, biến, chuỗi N'..', số...
    /// (lexer SQL của Scintilla chỉ tô từ khoá trong danh sách nên thiếu nhiều màu so với Monaco của chế độ Web).</summary>
    public static void ApplySql(Scintilla ed)
    {
        Apply(ed);
        if (Hooked.TryGetValue(ed, out var st)) { st.SqlOnly = true; StyleAll(ed); }
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scintilla, State> Hooked = new();

    /// <summary>Tắt tô FCode (khi editor chuyển sang lexer SQL/JSON... của Scintilla).</summary>
    public static void Detach(Scintilla ed) { if (Hooked.TryGetValue(ed, out var st)) st.Active = false; }

    /// <summary>Bật tô màu FCode cho <paramref name="ed"/> (gọi lại sau khi đổi theme để cập nhật màu).</summary>
    public static void Apply(Scintilla ed)
    {
        // Scintilla 5 / Scintilla.NET 5: không còn Lexer.Container — "tự tô" = gỡ lexer (SCI_SETILEXER null), Scintilla khi đó gửi StyleNeeded cho mình.
        ed.DirectMessage(SCI_SETILEXER, IntPtr.Zero, IntPtr.Zero);
        ApplyColors(ed);
        if (!Hooked.TryGetValue(ed, out var state))
        {
            state = new State();
            Hooked.Add(ed, state);
            // Cờ riêng (không dựa LexerName: Scintilla.NET nhớ tên lexer cũ dù đã gỡ). Detach() khi editor chuyển sang lexer khác.
            ed.StyleNeeded += (_, _) => { if (state.Active) StyleAll(ed); };
        }
        state.Active = true; state.SqlOnly = false;   // ApplySql đặt lại SqlOnly ngay sau
        StyleAll(ed);
    }

    private const int SCI_STARTSTYLING = 2032, SCI_SETSTYLINGEX = 2073, SCI_SETILEXER = 4033;

    /// <summary>Tô lại ngay toàn bộ (gọi sau khi nạp nội dung mới) — không chờ StyleNeeded, để lần vẽ đầu tiên đã đủ màu.</summary>
    public static void Restyle(Scintilla ed)
    {
        if (!Hooked.TryGetValue(ed, out var st) || !st.Active) return;
        StyleAll(ed);
        ed.Invalidate();
    }

    private static void StyleAll(Scintilla ed)
    {
        var text = ed.Text;
        byte[] styles;
        if (Hooked.TryGetValue(ed, out var mode) && mode.SqlOnly)
        {
            styles = new byte[text.Length];
            Array.Fill(styles, (byte)K.Text);
            Sql(text, 0, text.Length, (a, b, k) => { for (var x = Math.Max(0, a); x < b && x < styles.Length; x++) styles[x] = (byte)k; });
        }
        else styles = Tokenize(text);
        // Scintilla đánh vị trí theo byte UTF-8: mỗi ký tự nhận style của nó cho đủ số byte.
        var bytes = new byte[Encoding.UTF8.GetByteCount(text)];
        var bi = 0;
        for (var i = 0; i < text.Length; i++)
        {
            int n;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length) { n = 4; bytes.AsSpan(bi, n).Fill(styles[i]); bi += n; i++; continue; }
            var c = text[i];
            n = c < 0x80 ? 1 : c < 0x800 ? 2 : 3;
            bytes.AsSpan(bi, n).Fill(styles[i]); bi += n;
        }
        ed.DirectMessage(SCI_STARTSTYLING, IntPtr.Zero, IntPtr.Zero);
        var pin = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { ed.DirectMessage(SCI_SETSTYLINGEX, (IntPtr)bytes.Length, pin.AddrOfPinnedObject()); }
        finally { pin.Free(); }
    }

    // ---------------- tách token ----------------

    private enum Lang { None, Sql, Js, Css }

    private static byte[] Tokenize(string s)
    {
        var st = new byte[s.Length];
        Array.Fill(st, (byte)K.Text);
        void Mark(int a, int b, K k) { for (var x = Math.Max(0, a); x < b && x < st.Length; x++) st[x] = (byte)k; }

        var section = Lang.None; string? sectionTag = null; var rootLang = Lang.None;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '<')
            {
                if (At(s, i, "<!--")) { var e = IndexOrEnd(s, "-->", i + 4, 3); Mark(i, e, K.Comment); i = e; continue; }
                if (At(s, i, "<![CDATA["))
                {
                    Mark(i, i + 9, K.Cdata);
                    var start = i + 9;
                    var end = s.IndexOf("]]>", start, StringComparison.Ordinal); if (end < 0) end = s.Length;
                    // CDATA ngoài các thẻ đã biết: như trang web, đoán MỘT lần cho cả file (file mảnh là JS hay SQL) từ khối đầu tiên.
                    if (section == Lang.None && rootLang == Lang.None) rootLang = Guess(s, start, end);
                    var lang = section == Lang.None ? rootLang : section;
                    var head = s.AsSpan(start, Math.Min(80, end - start)).TrimStart();
                    if (head.StartsWith("/*") && head.ToString().Contains("flatten", StringComparison.OrdinalIgnoreCase) && head.ToString().Contains("Javascript", StringComparison.OrdinalIgnoreCase)) lang = Lang.Js;
                    if (lang == Lang.Sql) Sql(s, start, end, Mark); else Js(s, start, end, Mark);
                    if (end < s.Length) Mark(end, end + 3, K.Cdata);
                    i = Math.Min(s.Length, end + 3);
                    continue;
                }
                if (At(s, i, "<!"))
                {
                    // <!DOCTYPE x [  /  <!ENTITY [%] Ten SYSTEM "..." | "giá trị">
                    var nameEnd = i + 2; while (nameEnd < s.Length && char.IsLetter(s[nameEnd])) nameEnd++;
                    Mark(i, i + 2, K.Delim); Mark(i + 2, nameEnd, K.Meta);
                    var isDoctype = s.AsSpan(i + 2, nameEnd - i - 2).Equals("DOCTYPE", StringComparison.OrdinalIgnoreCase);
                    i = Decl(s, nameEnd, Mark, isDoctype);
                    continue;
                }
                if (At(s, i, "<?")) { Mark(i, i + 2, K.Delim); var ne = Name(s, i + 2); Mark(i + 2, ne, K.Meta); i = Attrs(s, ne, Mark, out _); continue; }
                if (At(s, i, "</"))
                {
                    var ne = Name(s, i + 2);
                    var name = s.Substring(i + 2, ne - i - 2);
                    Mark(i, i + 2, K.Delim); Mark(i + 2, ne, K.Tag);
                    var gt = s.IndexOf('>', ne); if (gt < 0) gt = s.Length - 1;
                    Mark(gt, gt + 1, K.Delim);
                    if (sectionTag is not null && name.Equals(sectionTag, StringComparison.OrdinalIgnoreCase)) { section = Lang.None; sectionTag = null; }
                    i = gt + 1; continue;
                }
                if (i + 1 < s.Length && (char.IsLetter(s[i + 1]) || s[i + 1] == '_'))
                {
                    var ne = Name(s, i + 1);
                    var name = s.Substring(i + 1, ne - i - 1);
                    Mark(i, i + 1, K.Delim); Mark(i + 1, ne, K.Tag);
                    i = Attrs(s, ne, Mark, out var selfClosing);
                    if (!selfClosing && section == Lang.None)
                    {
                        var l = SectionOf(name);
                        if (l != Lang.None) { section = l; sectionTag = name; }
                    }
                    continue;
                }
            }
            if (c == ']' && At(s, i, "]]>")) { Mark(i, i + 3, K.Cdata); i += 3; continue; }
            if (c == '&')
            {
                var e = i + 1; while (e < s.Length && (char.IsLetterOrDigit(s[e]) || s[e] is '_' or '.' or ':' or '$' or '-')) e++;
                if (e > i + 1 && e < s.Length && s[e] == ';') { Mark(i, e + 1, K.Entity); i = e + 1; continue; }
            }
            i++;
        }
        return st;
    }

    /// <summary>CDATA ngoài các thẻ đã biết (file mảnh Include\*.txt, &lt;query&gt;...): đoán theo chữ đầu như trang web — dạng JavaScript
    /// (function/var/let/const/return/try/switch/this./$, "if (", "//") → JS; còn lại → T-SQL.</summary>
    private static Lang Guess(string s, int a, int b)
    {
        var i = a;
        while (i < b && char.IsWhiteSpace(s[i])) i++;
        if (i >= b) return Lang.Sql;
        if (s[i] is '$' or '{' or '}' || (s[i] == '/' && i + 1 < b && s[i + 1] == '/')) return Lang.Js;
        var e = i; while (e < b && (char.IsLetterOrDigit(s[e]) || s[e] is '_' or '.' or '$')) e++;
        var w = s.Substring(i, e - i).ToLowerInvariant();
        if (w is "function" or "var" or "let" or "const" or "return" or "try" or "switch" or "this" || w.StartsWith("this.") || w.StartsWith("g.")) return Lang.Js;
        if ((w is "if" or "for" or "while") && NextNonSpace(s, e, b) == '(') return Lang.Js;
        if (e < b && s[e] == '(' && !SqlKeywords.Contains(w) && !SqlFunctions.Contains(w)) return Lang.Js;   // foo(...) ở đầu khối: lời gọi JS
        return Lang.Sql;
    }

    private static Lang SectionOf(string tag) => tag.ToLowerInvariant() switch
    {
        "script" or "clientscript" => Lang.Js,
        "command" or "action" or "processing" => Lang.Sql,
        "css" or "style" => Lang.Css,
        _ => Lang.None,
    };

    private static bool At(string s, int i, string t) => string.CompareOrdinal(s, i, t, 0, t.Length) == 0;
    private static int IndexOrEnd(string s, string t, int from, int add) { var e = s.IndexOf(t, from, StringComparison.Ordinal); return e < 0 ? s.Length : e + add; }
    private static int Name(string s, int i) { while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' or '-' or ':')) i++; return i; }

    /// <summary>Thuộc tính của thẻ tới dấu &gt; (tên = AttrName, "giá trị" = AttrValue). Trả vị trí sau &gt;.</summary>
    private static int Attrs(string s, int i, Action<int, int, K> mark, out bool selfClosing)
    {
        selfClosing = false;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '>') { mark(i, i + 1, K.Delim); return i + 1; }
            if ((c == '/' || c == '?') && i + 1 < s.Length && s[i + 1] == '>') { selfClosing = c == '/'; mark(i, i + 2, K.Delim); return i + 2; }
            if (c is '"' or '\'') { var e = s.IndexOf(c, i + 1); e = e < 0 ? s.Length : e + 1; mark(i, e, K.AttrValue); i = e; continue; }
            if (char.IsLetter(c) || c == '_') { var e = Name(s, i); mark(i, e, K.AttrName); i = e; continue; }
            if (c == '<') return i;   // thẻ chưa đóng — trả lại cho vòng ngoài
            i++;
        }
        return i;
    }

    // Đúng 2 biểu thức ENTITY_SQL_START / ENTITY_JS_START của fcode-language.js (tokenizer ignoreCase), đặt ^ vì xét từ đầu giá trị.
    private static readonly System.Text.RegularExpressions.Regex EntitySqlStart = new(
        @"^(?:--|/\*(?!\s*<flatten)|(?:declare|select|insert|update|delete|exec|execute|set|with|begin|create|alter|drop|truncate|merge|print|while|union|goto|raiserror|if\s+(?:not\s+)?exists|if\s*\(\s*select|if\s*@)\b)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex EntityJsStart = new(
        @"^(?://|/\*\s*<flatten|(?:function|var|let|const|return|this|document|window|typeof|new|if|for|switch|try)\b|\$find\b|[A-Za-z_$][\w$]*\s*\(|[A-Za-z_$][\w$]*\.[A-Za-z_$])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Khai báo &lt;!DOCTYPE …[ / &lt;!ENTITY …&gt;: SYSTEM/PUBLIC/% = Meta, tên = AttrName, "chuỗi" = AttrValue. DOCTYPE dừng ở '[' (các ENTITY bên trong tô riêng).</summary>
    private static int Decl(string s, int i, Action<int, int, K> mark, bool doctype)
    {
        var system = false;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '>') { mark(i, i + 1, K.Delim); return i + 1; }
            if (doctype && c == '[') { mark(i, i + 1, K.Delim); return i + 1; }
            if (c == '"' && !doctype && !system)
            {
                // <!ENTITY Ten "giá trị">: như entityStates của fcode-language.js — đoán theo chữ đầu giá trị: SQL → tô T-SQL, JS → tô JavaScript,
                // còn lại là chuỗi (có &Entity; bên trong). Dấu " đóng kết thúc (trong XML " của giá trị phải viết &quot;).
                var end = s.IndexOf('"', i + 1); if (end < 0) end = s.Length;
                mark(i, i + 1, K.AttrValue);
                var v = i + 1; while (v < end && char.IsWhiteSpace(s[v])) v++;
                var head = s.Substring(v, Math.Min(200, end - v));
                if (EntitySqlStart.IsMatch(head)) Sql(s, i + 1, end, mark);
                else if (EntityJsStart.IsMatch(head)) Js(s, i + 1, end, mark);
                else
                {
                    mark(i + 1, end, K.AttrValue);
                    for (var x = i + 1; x < end; x++)
                        if (s[x] == '&') { var y = x + 1; while (y < end && (char.IsLetterOrDigit(s[y]) || s[y] is '_' or '.' or ':' or '$' or '-')) y++; if (y < end && s[y] == ';' && y > x + 1) { mark(x, y + 1, K.Entity); x = y; } }
                }
                if (end < s.Length) mark(end, end + 1, K.AttrValue);
                i = Math.Min(s.Length, end + 1); continue;
            }
            if (c is '"' or '\'') { var e = s.IndexOf(c, i + 1); e = e < 0 ? s.Length : e + 1; mark(i, e, K.AttrValue); i = e; continue; }
            if (c == '%') { mark(i, i + 1, K.Meta); i++; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                var e = Name(s, i);
                var w = s.AsSpan(i, e - i);
                var kw = w.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) || w.Equals("PUBLIC", StringComparison.OrdinalIgnoreCase);
                if (kw) system = true;   // giá trị sau SYSTEM / PUBLIC là đường dẫn file → chuỗi
                mark(i, e, kw ? K.Meta : K.AttrName);
                i = e; continue;
            }
            if (c == '<') return i;
            i++;
        }
        return i;
    }

    // ---------------- T-SQL ----------------

    private static readonly HashSet<string> SqlKeywords = new(SqlSyntaxHighlighter.KeywordList.Select(k => k.ToLowerInvariant()).Concat(new[]
    {
        "begin", "end", "if", "else", "while", "return", "declare", "set", "exec", "execute", "case", "when", "then", "as", "with", "nolock", "top", "distinct",
        "union", "all", "on", "inner", "left", "right", "outer", "join", "cross", "apply", "full", "group", "order", "by", "having", "into", "values", "is", "null",
        "not", "and", "or", "in", "exists", "between", "like", "asc", "desc", "go", "proc", "procedure", "function", "table", "view", "trigger", "create", "alter",
        "drop", "insert", "update", "delete", "select", "from", "where", "output", "print", "raiserror", "try", "catch", "transaction", "tran", "commit", "rollback",
        "int", "bigint", "smallint", "tinyint", "bit", "decimal", "numeric", "money", "float", "real", "char", "varchar", "nchar", "nvarchar", "text", "ntext",
        "datetime", "datetime2", "smalldatetime", "date", "time", "uniqueidentifier", "varbinary", "binary", "xml", "cursor", "fetch", "next", "open", "close", "deallocate",
        "for", "browse", "option", "recompile", "nocount", "off", "default", "primary", "key", "foreign", "references", "index", "identity", "over", "partition",
    }));

    private static readonly HashSet<string> SqlFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "isnull", "coalesce", "nullif", "cast", "convert", "try_cast", "try_convert", "replace", "str", "substring", "left", "right", "len", "datalength", "ltrim", "rtrim", "trim",
        "upper", "lower", "charindex", "patindex", "stuff", "replicate", "reverse", "space", "concat", "format", "day", "month", "year", "getdate", "getutcdate", "sysdatetime",
        "dateadd", "datediff", "datename", "datepart", "eomonth", "isdate", "isnumeric", "abs", "round", "floor", "ceiling", "power", "sqrt", "sign", "count", "sum", "avg",
        "min", "max", "row_number", "rank", "dense_rank", "newid", "object_id", "object_name", "db_name", "scope_identity", "iif", "choose", "string_agg", "quotename",
    };

    private static readonly HashSet<string> TableIntro = new(StringComparer.OrdinalIgnoreCase) { "into", "from", "join", "update", "table" };

    private static void Sql(string s, int a, int b, Action<int, int, K> mark)
    {
        var i = a; var depth = 0; string? prevWord = null;
        while (i < b)
        {
            var c = s[i];
            if (c == '-' && i + 1 < b && s[i + 1] == '-') { var e = s.IndexOf('\n', i); e = e < 0 || e > b ? b : e; mark(i, e, K.SqlCmt); i = e; continue; }
            if (c == '/' && i + 1 < b && s[i + 1] == '*') { var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); e = e < 0 || e > b ? b : e + 2; mark(i, e, K.SqlCmt); i = e; continue; }
            if (c == '\'' || ((c == 'N' || c == 'n') && i + 1 < b && s[i + 1] == '\''))
            {
                var e = c == '\'' ? i + 1 : i + 2;
                while (e < b) { if (s[e] == '\'') { if (e + 1 < b && s[e + 1] == '\'') { e += 2; continue; } e++; break; } e++; }
                mark(i, e, K.SqlStr); i = e; continue;
            }
            if (char.IsDigit(c)) { var e = i; while (e < b && (char.IsDigit(s[e]) || s[e] == '.')) e++; mark(i, e, K.SqlNum); i = e; continue; }
            if (c == '@') { var e = i + 1; while (e < b && (char.IsLetterOrDigit(s[e]) || s[e] is '_' or '@' or '$')) e++; mark(i, e, K.SqlVar); i = e; continue; }
            if (char.IsLetter(c) || c == '_' || c == '#')
            {
                // Tên có thể có dấu chấm (dbo.FastBusiness$Function$System$ReadCurrency, a.ma_kh).
                var e = i + 1;
                while (e < b && (char.IsLetterOrDigit(s[e]) || s[e] is '_' or '$' or '#' || (s[e] == '.' && e + 1 < b && (char.IsLetter(s[e + 1]) || s[e + 1] is '_' or '[')))) e++;
                var w = s.Substring(i, e - i);
                var call = NextNonSpace(s, e, b) == '(';
                var afterExec = prevWord is not null && (prevWord.Equals("exec", StringComparison.OrdinalIgnoreCase) || prevWord.Equals("execute", StringComparison.OrdinalIgnoreCase));
                // Tên bảng (bảng tạm #x, hoặc đứng sau into/from/join/update/table) không phải lời gọi hàm dù có "(" ngay sau: insert into #duyet(cột...)
                var tableName = w.StartsWith('#') || (prevWord is not null && TableIntro.Contains(prevWord));
                if (tableName) call = false;
                prevWord = w;
                if (w.Equals("GO", StringComparison.OrdinalIgnoreCase) && IsGoLine(s, i, e)) mark(i, e, K.SqlGo);
                else if (afterExec && !SqlKeywords.Contains(w.ToLowerInvariant())) mark(i, e, K.SqlFn);   // exec <tên thủ tục>: màu hàm như chế độ Web
                else if (call && (SqlFunctions.Contains(w) || !SqlKeywords.Contains(w.ToLowerInvariant()))) mark(i, e, K.SqlFn);
                else if (w.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) || w.StartsWith("xp_", StringComparison.OrdinalIgnoreCase) || w.Contains(".sp_", StringComparison.OrdinalIgnoreCase)) mark(i, e, K.SqlFn);   // thủ tục hệ thống (sp_executesql...)   // gọi hàm (kể cả hàm người dùng dbo.X(...)) — như chế độ Web
                else if (!w.Contains('.') && SqlKeywords.Contains(w.ToLowerInvariant())) mark(i, e, K.SqlKw);
                i = e; continue;
            }
            if (Bracket(c, i, ref depth, mark)) { i++; continue; }
            if ("=<>+-*/%!".IndexOf(c) >= 0) { mark(i, i + 1, K.SqlOp); i++; continue; }
            i++;
        }
    }

    /// <summary>Ngoặc ( [ { tô 3 màu xoay vòng theo cấp lồng nhau — như bracket pair colorization của Monaco ở chế độ Web.</summary>
    private static bool Bracket(char c, int i, ref int depth, Action<int, int, K> mark)
    {
        if (c is '(' or '[' or '{') { mark(i, i + 1, (K)((int)K.Brk1 + depth % 3)); depth++; return true; }
        if (c is ')' or ']' or '}') { depth = Math.Max(0, depth - 1); mark(i, i + 1, (K)((int)K.Brk1 + depth % 3)); return true; }
        return false;
    }

    /// <summary>"GO" đứng riêng 1 dòng (dấu tách batch).</summary>
    private static bool IsGoLine(string s, int i, int e)
    {
        var ls = s.LastIndexOf('\n', Math.Max(0, i - 1)) + 1;
        var le = s.IndexOf('\n', e); if (le < 0) le = s.Length;
        return s.AsSpan(ls, le - ls).Trim().Equals("GO", StringComparison.OrdinalIgnoreCase);
    }

    private static char NextNonSpace(string s, int i, int b) { while (i < b && char.IsWhiteSpace(s[i])) i++; return i < b ? s[i] : '\0'; }

    // ---------------- JavaScript (và CSS: dùng chung, chỉ tô chuỗi / số / comment / từ khoá) ----------------

    private static readonly HashSet<string> JsKeywords = new(StringComparer.Ordinal)
    {
        "var", "let", "const", "function", "return", "if", "else", "for", "while", "do", "switch", "case", "break", "continue", "new", "this", "class", "extends",
        "import", "export", "from", "try", "catch", "finally", "throw", "async", "await", "typeof", "instanceof", "in", "of", "null", "undefined", "true", "false",
        "default", "delete", "void", "yield", "with", "debugger", "super", "static", "get", "set",
    };

    private static void Js(string s, int a, int b, Action<int, int, K> mark)
    {
        var i = a; var depth = 0;
        while (i < b)
        {
            var c = s[i];
            if (c == '/' && i + 1 < b && s[i + 1] == '/') { var e = s.IndexOf('\n', i); e = e < 0 || e > b ? b : e; mark(i, e, K.JsCmt); i = e; continue; }
            if (c == '/' && i + 1 < b && s[i + 1] == '*') { var e = s.IndexOf("*/", i + 2, StringComparison.Ordinal); e = e < 0 || e > b ? b : e + 2; mark(i, e, K.JsCmt); i = e; continue; }
            if (c is '\'' or '"' or '`')
            {
                var e = i + 1;
                while (e < b && s[e] != c) { if (s[e] == '\\') e++; else if (s[e] == '\n' && c != '`') break; e++; }
                e = Math.Min(b, e + 1); mark(i, e, K.JsStr); i = e; continue;
            }
            if (char.IsDigit(c)) { var e = i; while (e < b && (char.IsLetterOrDigit(s[e]) || s[e] == '.')) e++; mark(i, e, K.JsNum); i = e; continue; }
            if (char.IsLetter(c) || c is '_' or '$')
            {
                var e = i + 1; while (e < b && (char.IsLetterOrDigit(s[e]) || s[e] is '_' or '$')) e++;
                if (JsKeywords.Contains(s.Substring(i, e - i))) mark(i, e, K.JsKw);
                i = e; continue;
            }
            if (Bracket(c, i, ref depth, mark)) { i++; continue; }
            i++;
        }
    }

    // ---------------- màu ----------------

    private static void ApplyColors(Scintilla ed)
    {
        var dark = AppColors.IsDark;
        var rules = LoadViewerRules(dark);
        Color Rule(string token, string dflt)
        {
            // Như Monaco: luật cụ thể nhất thắng ("keyword.sql" trước "keyword"); không có thì màu mặc định Dark+/Light+.
            for (var t = token; t.Length > 0; t = t.Contains('.') ? t[..t.LastIndexOf('.')] : "")
                if (rules.TryGetValue(t, out var hex)) return Hex(hex);
            return rules.TryGetValue(token, out var h2) ? Hex(h2) : Hex(dflt);
        }
        var map = new (K Kind, string Token, string Dark, string Light)[]
        {
            (K.Text, "identifier", "D4D4D4", "000000"),
            (K.Delim, "delimiter.xml", "808080", "800000"),
            (K.Tag, "tag.fcode", "569CD6", "800000"),
            (K.AttrName, "attribute.name.fcode", "9CDCFE", "FF0000"),
            (K.AttrValue, "attribute.value.fcode", "CE9178", "0000FF"),
            (K.Comment, "comment.fcode", "6A9955", "008000"),
            (K.Entity, "string.escape.fcode", "D7BA7D", "EE0000"),
            (K.Cdata, "delimiter.cdata.fcode", "CE9178", "A31515"),
            (K.Meta, "metatag.fcode", "569CD6", "0000FF"),
            (K.SqlKw, "keyword.sql", "569CD6", "0000FF"),
            (K.SqlFn, "predefined.sql", "DCDCAA", "795E26"),
            (K.SqlStr, "string.sql", "CE9178", "A31515"),
            (K.SqlNum, "number.sql", "B5CEA8", "098658"),
            (K.SqlCmt, "comment.sql", "6A9955", "008000"),
            (K.SqlOp, "operator.sql", "D4D4D4", "000000"),
            (K.SqlVar, "variable.sql", "D4D4D4", "000000"),
            (K.JsKw, "keyword.js", "569CD6", "0000FF"),
            (K.JsStr, "string.js", "CE9178", "A31515"),
            (K.JsNum, "number.js", "B5CEA8", "098658"),
            (K.JsCmt, "comment.js", "6A9955", "008000"),
            // Màu ngoặc theo cấp: đúng màu mặc định bracket pair colorization của Monaco (vs-dark / vs).
            (K.Brk1, "~bracket1", "FFD700", "0431FA"),
            (K.Brk2, "~bracket2", "DA70D6", "319331"),
            (K.Brk3, "~bracket3", "179FFF", "7B3814"),
            (K.SqlGo, "keyword.sql", "FFFFFF", "FFFFFF"),
        };
        var font = ed.Styles[Style.Default];
        foreach (var (kind, token, d, l) in map)
        {
            var st = ed.Styles[(int)kind];
            st.Font = font.Font; st.Size = font.Size; st.BackColor = font.BackColor;
            // Chữ thường: luật token rỗng "" của theme (như Monaco), không có thì màu chữ của Bcode.
            st.ForeColor = kind == K.Text ? (rules.TryGetValue("", out var baseHex) ? Hex(baseHex) : AppColors.Text) : Rule(token, dark ? d : l);
            st.Italic = false; st.Bold = false;
        }
        // GO: chữ trắng đậm trên nền đỏ — dấu tách batch nổi bật như chế độ Web.
        var go = ed.Styles[(int)K.SqlGo];
        go.ForeColor = Color.White; go.BackColor = Color.FromArgb(0xD1, 0x34, 0x38); go.Bold = true;
    }

    private static Color Hex(string h) { h = h.TrimStart('#'); return h.Length >= 6 ? Color.FromArgb(Convert.ToInt32(h[..2], 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16)) : Color.Gray; }

    /// <summary>Luật token của theme BcodeViewer (cùng file trang web File Lookup đọc) — chỉ dùng khi cùng sáng/tối với Bcode; đọc lại mỗi lần (file nhỏ, chỉ lúc đổi theme / tạo khung).</summary>
    private static Dictionary<string, string> LoadViewerRules(bool dark)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "viewer-theme.json");
            if (!File.Exists(path)) return d;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.TryGetProperty("isDark", out var isDark) && isDark.ValueKind is JsonValueKind.True or JsonValueKind.False && isDark.GetBoolean() != dark) return d;
            if (!root.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array) return d;
            foreach (var r in rules.EnumerateArray())
                if (r.TryGetProperty("token", out var t) && r.TryGetProperty("foreground", out var f) && f.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(f.GetString()))
                    d[t.GetString() ?? ""] = f.GetString()!;
        }
        catch { /* theme hỏng → bảng mặc định */ }
        return d;
    }
}
