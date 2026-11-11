using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>
/// Tự đóng ngoặc / nháy cho editor SQL chế độ App (Scintilla) — cùng luật với cấu hình 'sql' của Monaco ở sqleditor.html (bản Web):
/// ( [ { " luôn tự đóng; ' chỉ khi bật Options… → "Gõ ' tự chèn thêm ' đóng" (AppSettings.SqlAutoCloseSingleQuote, mặc định tắt).
/// Như Monaco (autoClosingBrackets/Quotes = languageDefined, autoCloseBefore, autoClosingOvertype/Delete = auto, surroundingPairs):
/// <list type="bullet">
/// <item>chỉ chèn dấu đóng khi ký tự ngay sau con trỏ là khoảng trắng / cuối dòng / một trong <c>;:.,=}])&gt;</c>;</item>
/// <item>nháy không tự đóng khi ngay trước là chữ / số / _ (vd it's) và khi đang đứng trong chuỗi '...' chưa đóng;</item>
/// <item>gõ đúng dấu đóng vừa được tự chèn thì chỉ nhảy qua; Backspace giữa cặp vừa tự chèn xoá cả cặp;</item>
/// <item>đang bôi đen mà gõ ( [ { " ' thì bọc 2 đầu đoạn chọn (kể cả khi tắt tự đóng ').</item>
/// </list>
/// </summary>
internal sealed class SqlAutoClose
{
    private const string AutoCloseBefore = ";:.,=}])> \t\r\n";
    private readonly Scintilla _ed;
    private readonly Func<bool> _singleQuote;
    // Các dấu đóng do đây tự chèn còn "đang chờ" (vị trí ký tự) — chỉ những dấu này mới được gõ đè / xoá theo cặp (Monaco autoClosingOvertype "auto").
    private readonly List<int> _pending = new();
    private char _closeAfterChar;   // dấu đóng cần chèn ở CharAdded kế tiếp (để các xử lý CharAdded khác — gợi ý tham số khi gõ '(' — vẫn chạy như cũ)

    private SqlAutoClose(Scintilla ed, Func<bool> singleQuote)
    {
        _ed = ed; _singleQuote = singleQuote;
        _ed.KeyPress += OnKeyPress;
        _ed.KeyDown += OnKeyDown;
        _ed.CharAdded += OnCharAdded;
        _ed.Insert += (_, e) => Shift(e.Position, e.Text.Length);
        _ed.Delete += (_, e) => Shift(e.Position, -e.Text.Length);
        _ed.UpdateUI += (_, e) => { if ((e.Change & UpdateChange.Selection) != 0) Prune(); };
    }

    public static SqlAutoClose Attach(Scintilla ed, Func<bool> singleQuote) => new(ed, singleQuote);

    private static char CloserOf(char c) => c switch { '(' => ')', '[' => ']', '{' => '}', '"' => '"', '\'' => '\'', _ => '\0' };

    private bool Enabled(char open) => open != '\'' || _singleQuote();

    private void OnKeyPress(object? sender, KeyPressEventArgs e)
    {
        if (_ed.ReadOnly || _ed.Selections.Count > 1) return;   // Ctrl+phím không sinh ký tự ( [ { " ' nên không cần xét phím bổ trợ
        var c = e.KeyChar;
        var close = CloserOf(c);
        int s = _ed.SelectionStart, t = _ed.SelectionEnd;

        // Bôi đen + gõ dấu mở → bọc 2 đầu (surroundingPairs của Monaco có cả ').
        if (close != '\0' && t > s)
        {
            var text = _ed.SelectedText;
            _ed.BeginUndoAction();
            _ed.ReplaceSelection(c + text + close);
            _ed.EndUndoAction();
            _ed.SetSelection(s + 1 + text.Length, s + 1);   // giữ đoạn chọn bên trong cặp (như Monaco)
            e.Handled = true;
            return;
        }
        if (t != s) return;

        var pos = _ed.CurrentPosition;
        var next = pos < _ed.TextLength ? (char)_ed.GetCharAt(pos) : '\n';

        // Gõ dấu đóng ngay trước dấu đóng vừa tự chèn → nhảy qua.
        if ((c is ')' or ']' or '}' or '"' or '\'') && next == c && _pending.Contains(pos))
        {
            _pending.Remove(pos);
            _ed.GotoPosition(pos + 1);
            e.Handled = true;
            return;
        }

        if (close == '\0' || !Enabled(c) || AutoCloseBefore.IndexOf(next) < 0) return;
        if (c is '"' or '\'')
        {
            var prev = pos > 0 ? (char)_ed.GetCharAt(pos - 1) : ' ';
            if (char.IsLetterOrDigit(prev) || prev == '_' || prev == c) return;
            if (c == '\'' && InsideSqlString(pos)) return;
        }
        _closeAfterChar = close;   // chữ gõ vẫn đi đường thường (CharAdded của gợi ý code chạy như cũ), dấu đóng chèn ở CharAdded
    }

    private void OnCharAdded(object? sender, CharAddedEventArgs e)
    {
        var close = _closeAfterChar;
        _closeAfterChar = '\0';
        if (close == '\0') return;
        var pos = _ed.CurrentPosition;
        _ed.InsertText(pos, close.ToString());   // chèn ĐÚNG tại con trỏ: con trỏ đứng yên, dấu đóng nằm sau
        _pending.Add(pos);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Back || e.Modifiers != Keys.None || _ed.ReadOnly || _ed.SelectionStart != _ed.SelectionEnd) return;
        var pos = _ed.CurrentPosition;
        if (pos <= 0 || pos >= _ed.TextLength || !_pending.Contains(pos)) return;
        var open = (char)_ed.GetCharAt(pos - 1);
        if (CloserOf(open) is var close && close != '\0' && (char)_ed.GetCharAt(pos) == close)
        {
            _pending.Remove(pos);
            _ed.DeleteRange(pos - 1, 2);   // xoá cả cặp vừa tự chèn (Monaco autoClosingDelete "auto")
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    /// <summary>Con trỏ đang trong chuỗi '...' chưa đóng trên dòng hiện tại (đếm nháy, '' = nháy thoát; bỏ qua sau --).</summary>
    private bool InsideSqlString(int pos)
    {
        var line = _ed.Lines[_ed.LineFromPosition(pos)];
        var before = _ed.GetTextRange(line.Position, pos - line.Position);
        var inStr = false;
        for (var i = 0; i < before.Length; i++)
        {
            var ch = before[i];
            if (!inStr && ch == '-' && i + 1 < before.Length && before[i + 1] == '-') return false;
            if (ch == '\'') inStr = !inStr;
        }
        return inStr;
    }

    // Vị trí các dấu đóng đang chờ dời theo sửa đổi văn bản; dấu bị xoá thì bỏ.
    private void Shift(int at, int delta)
    {
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var p = _pending[i];
            if (delta < 0 && p >= at && p < at - delta) { _pending.RemoveAt(i); continue; }
            if (p >= at) _pending[i] = p + delta;
        }
    }

    // Con trỏ rời khỏi dòng của cặp → thôi "chờ" (như Monaco: chỉ gõ đè khi còn ở ngay đó).
    private void Prune()
    {
        if (_pending.Count == 0) return;
        var line = _ed.CurrentLine;
        _pending.RemoveAll(p => p > _ed.TextLength || _ed.LineFromPosition(p) != line);
    }
}
