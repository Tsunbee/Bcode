using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>Cấu hình + tô màu SQL cho control Scintilla — dùng chung giữa tab SQL Query native, Lookup, Table... (một nơi chỉnh màu/từ khoá).</summary>
internal static class SqlScintilla
{
    public static void Configure(Scintilla _ed)
    {
        _ed.Lexer = Lexer.Sql;
        _ed.WrapMode = WrapMode.None;
        _ed.CaretLineVisible = true;
        _ed.TabWidth = 4;
        _ed.IndentWidth = 4;
        _ed.UseTabs = false;
        _ed.ScrollWidth = 400;
        _ed.AutoCIgnoreCase = true;
        _ed.AutoCMaxHeight = 12;
        _ed.AutoCOrder = Order.PerformSort;
        _ed.AutoCChooseSingle = false;
        _ed.Margins[0].Type = MarginType.Number;
        _ed.Margins[1].Width = 0;
        _ed.Margins[2].Width = 0;
        static string Words(params string[] w) => string.Join(" ", w);
        _ed.SetKeywords(0, Words(SqlSyntaxHighlighter.KeywordList.Select(k => k.ToLowerInvariant()).Concat(new[]
        {
            "go","print","raiserror","try","catch","throw","transaction","tran","commit","rollback","cursor","fetch","next","open","close","deallocate","while","break","continue",
            "truncate","nolock","readonly","output","over","partition","apply","cross","full","merge","using","matched","each","for","to","after","before","instead","of","use","add","column",
            "check","grant","revoke","deny","schema","database","proc","returns","returns","except","intersect","pivot","unpivot","offset","rows","only","percent","ties","exists","some","any","into","goto","waitfor","delay","nocount","on","off","view_metadata","schemabinding"
        }).Distinct().ToArray()));
        _ed.SetKeywords(1, Words("int","bigint","smallint","tinyint","bit","decimal","numeric","money","smallmoney","float","real","char","varchar","nchar","nvarchar","text","ntext","datetime","datetime2","smalldatetime","date","time","uniqueidentifier","varbinary","binary","image","xml","sql_variant","table","cursor",
            "getdate","getutcdate","sysdatetime","len","substring","charindex","patindex","replace","stuff","ltrim","rtrim","upper","lower","left","right","reverse","format","concat","isnull","coalesce","nullif","cast","convert","try_convert","iif","choose","abs","ceiling","floor","round","power","sqrt","rand",
            "count","sum","avg","min","max","row_number","rank","dense_rank","ntile","lag","lead","first_value","last_value","string_agg","stuff","datediff","dateadd","datepart","datename","year","month","day","eomonth","isdate","isnumeric","object_id","object_name","object_definition","db_name","scope_identity","newid","@@rowcount","@@error","@@identity","@@trancount","@@fetch_status","@@version","@@servername"));
        _ed.WordChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_$#@";
        if (!KeyGuarded.TryGetValue(_ed, out _)) { KeyGuarded.Add(_ed, new object()); _ed.KeyPress += BlockControlChars; _ed.CharAdded += AutoIndent; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Scintilla, object> KeyGuarded = new();

    /// <summary>Ctrl + chữ chưa gắn lệnh: Windows vẫn gửi ký tự điều khiển (Ctrl+S = 0x13 "DC3", Ctrl+F = 0x06 "ACK"...) và Scintilla chèn nguyên
    /// ký tự đó vào văn bản (hiện thành ô DC3/ACK). Chặn mọi ký tự điều khiển trừ Tab / xuống dòng / Backspace.</summary>
    private static void BlockControlChars(object? sender, KeyPressEventArgs e)
    {
        if ((e.KeyChar < ' ' && e.KeyChar is not ('\t' or '\r' or '\n' or '\b')) || e.KeyChar == '\x7f') e.Handled = true;
    }

    /// <summary>Enter xuống dòng: dòng mới thụt lề bằng dòng vừa rời (như Monaco của bản Web); dòng trước kết thúc bằng "(" thì thụt thêm 1 nấc.
    /// Enter ở giữa dòng: phần chữ bị đẩy xuống cũng được thụt theo, con trỏ đứng ngay trước chữ đó.</summary>
    private static void AutoIndent(object? sender, CharAddedEventArgs e)
    {
        if (sender is not Scintilla ed || ed.ReadOnly) return;
        if (!(e.Char == '\n' || (e.Char == '\r' && ed.EolMode == Eol.Cr))) return;
        var cur = ed.LineFromPosition(ed.CurrentPosition);
        if (cur <= 0) return;
        var prevLine = ed.Lines[cur - 1];
        var indent = prevLine.Indentation;
        if (prevLine.Text.TrimEnd().EndsWith('(')) indent += ed.IndentWidth;
        if (indent <= 0) return;
        ed.Lines[cur].Indentation = indent;
        ed.GotoPosition(ed.Lines[cur].IndentPosition);
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

    public static void ApplyTheme(Scintilla _ed)
    {
        if (_ed.IsDisposed) return;
        var dark = AppColors.IsDark;
        var font = ThemeManager.MonoFont;
        _ed.StyleResetDefault();
        var d = _ed.Styles[Style.Default];
        d.Font = font.FontFamily.Name;
        d.Size = (int)Math.Round(font.Size * UiScale.Factor * 1.1);
        d.BackColor = AppColors.Input;
        d.ForeColor = AppColors.Text;
        _ed.StyleClearAll();
        _ed.Styles[Style.Sql.Default].ForeColor = AppColors.Text;
        _ed.Styles[Style.Sql.Comment].ForeColor = dark ? C(106, 153, 85) : C(0, 128, 0);
        _ed.Styles[Style.Sql.CommentLine].ForeColor = dark ? C(106, 153, 85) : C(0, 128, 0);
        _ed.Styles[Style.Sql.CommentDoc].ForeColor = dark ? C(106, 153, 85) : C(0, 128, 0);
        _ed.Styles[Style.Sql.Number].ForeColor = dark ? C(181, 206, 168) : C(9, 134, 88);
        _ed.Styles[Style.Sql.Word].ForeColor = dark ? C(86, 156, 214) : C(0, 0, 255);
        _ed.Styles[Style.Sql.Word2].ForeColor = dark ? C(220, 220, 170) : C(121, 94, 38);
        _ed.Styles[Style.Sql.String].ForeColor = dark ? C(206, 145, 120) : C(163, 21, 21);
        _ed.Styles[Style.Sql.Character].ForeColor = dark ? C(206, 145, 120) : C(163, 21, 21);
        _ed.Styles[Style.Sql.Operator].ForeColor = dark ? C(212, 212, 212) : C(60, 60, 60);
        _ed.Styles[Style.Sql.QuotedIdentifier].ForeColor = dark ? C(78, 201, 176) : C(43, 145, 175);
        _ed.Styles[Style.LineNumber].BackColor = AppColors.PanelAlt;
        _ed.Styles[Style.LineNumber].ForeColor = AppColors.TextMuted;
        _ed.CaretLineBackColor = Blend(AppColors.Input, AppColors.Text, dark ? 0.12 : 0.07);   // dòng đang đứng: xám sáng hơn nền editor một chút
        _ed.CaretForeColor = AppColors.Text;
        _ed.SetSelectionBackColor(true, AppColors.Selection);
        _ed.SetSelectionForeColor(false, Color.Empty);
        _ed.Margins[0].Width = _ed.TextWidth(Style.LineNumber, "99999") + 10;
        _ed.Styles[Style.CallTip].BackColor = AppColors.PanelAlt;
        _ed.Styles[Style.CallTip].ForeColor = AppColors.Text;
    }


    /// <summary>Đổi ngôn ngữ tô màu của editor (SQL / JSON / XML / JavaScript) — dùng cho String Beauty, Compare... Màu theo theme như <see cref="ApplyTheme"/>.</summary>
    public static void SetLanguage(Scintilla ed, string lang)
    {
        var dark = AppColors.IsDark;
        var com = dark ? C(106, 153, 85) : C(0, 128, 0);
        var str = dark ? C(206, 145, 120) : C(163, 21, 21);
        var num = dark ? C(181, 206, 168) : C(9, 134, 88);
        var kw = dark ? C(86, 156, 214) : C(0, 0, 255);
        var key = dark ? C(156, 220, 254) : C(4, 81, 165);
        var tag = dark ? C(86, 156, 214) : C(128, 0, 0);
        var attr = dark ? C(156, 220, 254) : C(255, 0, 0);
        switch (lang)
        {
            case "JSON":
                ed.Lexer = Lexer.Json;
                ed.SetKeywords(0, "true false null");
                ed.Styles[Style.Json.PropertyName].ForeColor = key; ed.Styles[Style.Json.String].ForeColor = str; ed.Styles[Style.Json.Number].ForeColor = num;
                ed.Styles[Style.Json.Keyword].ForeColor = kw; ed.Styles[Style.Json.LineComment].ForeColor = com; ed.Styles[Style.Json.BlockComment].ForeColor = com;
                break;
            case "XML":
                ed.Lexer = Lexer.Xml;
                ed.Styles[Style.Xml.Tag].ForeColor = tag; ed.Styles[Style.Xml.TagEnd].ForeColor = tag; ed.Styles[Style.Xml.XmlStart].ForeColor = tag; ed.Styles[Style.Xml.XmlEnd].ForeColor = tag;
                ed.Styles[Style.Xml.Attribute].ForeColor = attr; ed.Styles[Style.Xml.DoubleString].ForeColor = str; ed.Styles[Style.Xml.SingleString].ForeColor = str;
                ed.Styles[Style.Xml.Comment].ForeColor = com; ed.Styles[Style.Xml.CData].ForeColor = str;
                // Khối <!DOCTYPE ... [ <!ENTITY Ten SYSTEM "..."> ]> (đầu mọi controller FCode): bộ tô XML cần danh sách từ khoá SGML (bộ số 5) — để trống thì
                // cả khối bị coi là lỗi (style SGML_ERROR) và hiện một màu trắng. Màu cùng họ với Monaco của chế độ Web: từ khoá xanh, tên entity xanh nhạt, chuỗi cam.
                ed.SetKeywords(5, "DOCTYPE ENTITY ELEMENT ATTLIST NOTATION SYSTEM PUBLIC INCLUDE IGNORE CDATA PCDATA");
                var ent = dark ? C(78, 201, 176) : C(38, 127, 153);
                ed.Styles[21].ForeColor = tag;                     // SGML_DEFAULT: <! ... >
                ed.Styles[22].ForeColor = kw;                      // SGML_COMMAND: DOCTYPE / ENTITY / SYSTEM
                ed.Styles[23].ForeColor = key;                     // SGML_1ST_PARAM: tên entity
                ed.Styles[24].ForeColor = str; ed.Styles[25].ForeColor = str;   // SGML_DOUBLESTRING / SIMPLESTRING: đường dẫn, giá trị
                ed.Styles[26].ForeColor = AppColors.Text;          // SGML_ERROR: phần còn lại — chữ thường, không tô đỏ
                ed.Styles[27].ForeColor = kw;                      // SGML_SPECIAL: % [ ]
                ed.Styles[28].ForeColor = ent;                     // SGML_ENTITY: %Ten;
                ed.Styles[29].ForeColor = com; ed.Styles[30].ForeColor = com;   // SGML_COMMENT
                ed.Styles[31].ForeColor = AppColors.Text;          // SGML_BLOCK_DEFAULT
                ed.Styles[Style.Xml.Entity].ForeColor = ent;       // &Ten; trong nội dung
                break;
            case "JavaScript":
                ed.Lexer = Lexer.Cpp;
                ed.SetKeywords(0, "var let const function return if else for while do switch case break continue new this class extends import export from try catch finally throw async await typeof instanceof in of null undefined true false");
                ed.Styles[Style.Cpp.Comment].ForeColor = com; ed.Styles[Style.Cpp.CommentLine].ForeColor = com; ed.Styles[Style.Cpp.CommentDoc].ForeColor = com;
                ed.Styles[Style.Cpp.Number].ForeColor = num; ed.Styles[Style.Cpp.Word].ForeColor = kw; ed.Styles[Style.Cpp.String].ForeColor = str; ed.Styles[Style.Cpp.Character].ForeColor = str;
                break;
            default:
                ed.Lexer = Lexer.Sql;
                break;
        }
    }
}
