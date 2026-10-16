using System.Data;
using System.Text;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Data.SqlClient;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>
/// Gợi ý code SQL cho editor native (tab SQL Query chế độ App): gõ → hỏi <see cref="SqlHintEngine"/> (chạy nguyên sqlhints.js của bản Web) ở luồng nền → hiện <see cref="HintPopup"/>;
/// Tab / Enter / chuột chọn → chèn (mẫu dùng <see cref="SnippetSession"/> để điền các ô bằng Tab). Ngoài ra: trợ giúp chữ ký (CallTip) khi gõ tham số, gạch lượn vàng cho biến chuẩn
/// chưa khai báo (rê chuột xem lý do), và các gợi ý từ khoá / tên bảng-view-procedure như trước. Ctrl+Space = gọi gợi ý ngay.
/// </summary>
internal sealed class NativeSqlHints : IDisposable
{
    private const int LintIndicator = 8;

    private readonly Scintilla _ed;
    private readonly Control _owner;
    private readonly Func<bool> _useSys;
    private readonly Func<string> _project;
    private readonly SnippetLibraryService? _snippets;
    private readonly SqlObjectBrowserService _objects;
    private readonly Func<SqlConnection?> _tempConn;
    private readonly Func<IReadOnlyList<string>> _names, _tables;
    private readonly HintPopup _popup = new();
    private readonly SnippetSession _snippet;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 35 }, _lintTimer = new() { Interval = 700 };
    private readonly Dictionary<string, string> _routines = new();
    private readonly Func<bool> _hasConnection;
    private List<HintIssue> _issues = new();
    private string _lintText = "";
    private int _version, _snapPos = -1, _snapChar;
    private string _snapNorm = "";
    private bool _manual;
    private bool _disposed;

    public bool Enabled { get; set; } = true;

    public NativeSqlHints(Scintilla ed, Control owner, Func<bool> useSys, Func<string> project, SnippetLibraryService? snippets, SqlObjectBrowserService objects,
        Func<SqlConnection?> tempConn, Func<IReadOnlyList<string>> names, Func<IReadOnlyList<string>> tables, Func<bool> hasConnection)
    {
        (_ed, _owner, _useSys, _project, _snippets, _objects, _tempConn, _names, _tables, _hasConnection) = (ed, owner, useSys, project, snippets, objects, tempConn, names, tables, hasConnection);
        _snippet = new SnippetSession(ed);
        _popup.Picked += Accept;
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RunAsync(); };
        _lintTimer.Tick += (_, _) => { _lintTimer.Stop(); _ = LintAsync(); };
        _ed.CharAdded += OnCharAdded;
        _ed.TextChanged += (_, _) => { if (!_disposed) { _lintTimer.Stop(); _lintTimer.Start(); } };
        _ed.Delete += (_, _) => { if (_popup.IsOpen && !_disposed) BeginInvoke(() => { if (_popup.IsOpen) Trigger(false); }); };
        _ed.MouseDown += (_, _) => Hide();
        _ed.LostFocus += (_, _) => Hide();
        _ed.DwellStart += OnDwell;
        _ed.DwellEnd += (_, _) => { if (_ed.CallTipActive) _ed.CallTipCancel(); };
        _ed.MouseDwellTime = 400;
        _ed.Indicators[LintIndicator].Style = IndicatorStyle.Squiggle;
        ApplyTheme();
        ThemeManager.ThemeChanged += ApplyTheme;
    }

    private void BeginInvoke(Action a) { try { if (!_owner.IsDisposed) _owner.BeginInvoke(a); } catch { /* đang đóng */ } }

    private void ApplyTheme() { if (!_ed.IsDisposed) _ed.Indicators[LintIndicator].ForeColor = AppColors.Warning; }

    public void Dispose()
    {
        _disposed = true;
        ThemeManager.ThemeChanged -= ApplyTheme;
        _debounce.Dispose(); _lintTimer.Dispose();
        _popup.Dispose();
    }

    public void Hide() { _version++; _debounce.Stop(); if (_popup.IsOpen) _popup.HidePopup(); }

    // ------------------------------------------------------------------ phím

    /// <summary>Xử lý phím khi đang có gợi ý / phiên điền ô. true = phím đã được dùng.</summary>
    public bool HandleKey(Keys key)
    {
        if (key == (Keys.Control | Keys.Space)) { Trigger(true); return true; }
        if (_popup.IsOpen)
        {
            switch (key)
            {
                case Keys.Down: _popup.MoveSelection(1); return true;
                case Keys.Up: _popup.MoveSelection(-1); return true;
                case Keys.PageDown: _popup.MoveSelection(10); return true;
                case Keys.PageUp: _popup.MoveSelection(-10); return true;
                case Keys.Tab: case Keys.Enter: if (_popup.Selected is { } it) { Accept(it); return true; } break;
                case Keys.Escape: Hide(); return true;
            }
        }
        if (_snippet.Active)
        {
            if (key == Keys.Tab) return _snippet.Next(back: false);
            if (key == (Keys.Shift | Keys.Tab)) return _snippet.Next(back: true);
            if (key == Keys.Escape) { _snippet.End(); return true; }
        }
        if (key == Keys.Escape && _ed.CallTipActive) { _ed.CallTipCancel(); return true; }
        return false;
    }

    // ------------------------------------------------------------------ gõ → gợi ý

    private void OnCharAdded(object? sender, CharAddedEventArgs e)
    {
        if (!Enabled || _disposed) return;
        var ch = (char)e.Char;
        if (ch == ')') { if (_ed.CallTipActive) _ed.CallTipCancel(); }
        if (ch is '(' or ',' or ' ') _ = SignatureAsync();
        if (char.IsLetterOrDigit(ch) || ch is '_' or '$' or '#' or '@' or '.' or '\'' or ' ') Trigger(false);
        else if (_popup.IsOpen) Hide();
    }

    private void Trigger(bool manual)
    {
        if (!Enabled && !manual) return;
        _manual = manual;
        _version++;
        _debounce.Stop();
        _debounce.Start();
    }

    private string Key() => (_hasConnection() ? "ws" : "none") + (_useSys() ? "|sys" : "|app") + "|" + _objects.Connections.CurrentStamp(_useSys());

    /// <summary>Nút Refresh database: quên chữ ký procedure/function đã nạp — lần gợi ý sau hỏi lại (SqlHintService đã bỏ cache chung).</summary>
    public void ResetCache() => _routines.Clear();

    private async Task<string> RoutinesAsync(bool sys)
    {
        var key = Key();
        if (_routines.TryGetValue(key, out var cached)) return cached;
        try
        {
            var svc = _hintService ??= new SqlHintService(_objects.Connections);
            var json = await svc.GetPayloadJsonAsync(sys);
            _routines[key] = json;
            return json;
        }
        catch { return ""; }   // offline: chỉ còn mẫu gõ-tắt, từ khoá, tên trong script
    }

    private SqlHintService? _hintService;

    private string UserJson()
    {
        try
        {
            var project = _project();
            var user = _snippets?.Snippets.Where(s => s.AppliesTo(project)).Select(s => new { n = s.Name, c = s.Category, b = s.Content, proj = s.Project }).ToList();
            return user is { Count: > 0 } ? JsonSerializer.Serialize(user) : "[]";
        }
        catch { return "[]"; }
    }

    /// <summary>Cột của bảng cho engine (chạy ở luồng nền, đồng bộ): bảng thật hỏi database, #tạm hỏi connection đang giữ (Reset Connection tắt).</summary>
    private string Columns(string table, bool sys)
    {
        try
        {
            if (table.StartsWith('#'))
            {
                var conn = _tempConn();
                if (conn is null || conn.State != ConnectionState.Open) return "[]";
                var names = new List<object[]>();
                using var cmd = new SqlCommand("SELECT c.name FROM tempdb.sys.columns c WHERE c.object_id = OBJECT_ID('tempdb..' + @n) ORDER BY c.column_id", conn) { CommandTimeout = 10 };
                cmd.Parameters.AddWithValue("@n", table);
                using var rd = cmd.ExecuteReader();
                while (rd.Read()) names.Add(new object[] { rd.GetString(0), false });
                return JsonSerializer.Serialize(names);
            }
            var dot = table.LastIndexOf('.');
            var schema = dot > 0 ? table[..dot] : "dbo";
            var name = dot > 0 ? table[(dot + 1)..] : table;
            var cols = _objects.GetColumnsAsync(sys, schema, name).GetAwaiter().GetResult();
            return JsonSerializer.Serialize(cols.Select(c => new object[] { c.Name, c.IsPrimaryKey }));
        }
        catch { return "[]"; }
    }

    private async Task RunAsync()
    {
        if (_ed.IsDisposed) return;
        var version = _version;
        var manual = _manual;
        var pos = _ed.CurrentPosition;
        var prefix = _ed.GetTextRange(0, pos);
        var full = _ed.Text ?? "";
        var norm = SqlHintEngine.Normalize(full);
        var caretChar = SqlHintEngine.Normalize(prefix).Length;
        var sys = _useSys();
        var key = Key();
        var routines = await RoutinesAsync(sys);
        var tables = _tables();
        var userJson = UserJson();
        List<HintItem> raw;
        try { raw = await Task.Run(() => SqlHintEngine.Shared.Complete(norm, caretChar, key, routines, tables, userJson, t => Columns(t, sys))); }
        catch { raw = new(); }
        if (_disposed || _ed.IsDisposed || version != _version || _ed.CurrentPosition != pos) return;   // gõ tiếp / đổi chỗ trong lúc chờ

        var items = Rank(raw, norm, caretChar);
        items.AddRange(Generic(norm, caretChar, items));
        if (items.Count == 0 || (!manual && items.Count == 1 && items[0].Label.Equals(WordBefore(norm, caretChar), StringComparison.OrdinalIgnoreCase))) { _popup.HidePopup(); return; }
        _snapPos = pos; _snapChar = caretChar; _snapNorm = norm;
        var x = _ed.PointXFromPosition(pos);
        var y = _ed.PointYFromPosition(pos);
        var h = _ed.Lines[_ed.CurrentLine].Height;
        var top = _ed.PointToScreen(new Point(x, y));
        _popup.ShowItems(_owner.FindForm() ?? (IWin32Window)_owner, top, top.Y + h, items.Take(250).ToList());
    }

    private static string WordBefore(string norm, int caret)
    {
        var i = caret;
        while (i > 0 && (char.IsLetterOrDigit(norm[i - 1]) || norm[i - 1] is '_' or '$' or '#' or '@')) i--;
        return norm[i..caret];
    }

    /// <summary>Lọc theo chữ đang gõ (khớp đầu &gt; chứa &gt; các chữ liền nhau) rồi sắp: độ khớp trước, thứ tự riêng của từng nguồn sau — như Monaco.</summary>
    private static List<HintItem> Rank(List<HintItem> raw, string norm, int caret)
    {
        var scored = new List<(int Rank, HintItem Item)>();
        foreach (var it in raw)
        {
            if (it.Start > caret || it.Start < 0) continue;
            var typed = norm[it.Start..caret];
            var rank = Match(typed, string.IsNullOrEmpty(it.Filter) ? it.Label : it.Filter);
            if (rank >= 0) scored.Add((rank, it));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return scored.OrderBy(s => s.Rank).ThenBy(s => s.Item.Sort, StringComparer.Ordinal)
            .Select(s => s.Item).Where(i => seen.Add(i.Label + "|" + i.Start)).ToList();
    }

    private static int Match(string typed, string filter)
    {
        if (typed.Length == 0) return 0;
        if (filter.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) return 0;
        if (filter.Contains(typed, StringComparison.OrdinalIgnoreCase)) return 1;
        var j = 0;
        foreach (var c in filter) { if (j < typed.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(typed[j])) j++; }
        return j == typed.Length ? 2 : -1;
    }

    /// <summary>Từ khoá SQL + tên bảng/view/procedure trong database cho chữ đang gõ (≥ 2 ký tự, không sau dấu chấm) — phần "từ trong văn bản" của Monaco.</summary>
    private IEnumerable<HintItem> Generic(string norm, int caret, List<HintItem> already)
    {
        var w = WordBefore(norm, caret);
        if (w.Length < 2) yield break;
        var before = caret - w.Length - 1;
        if (before >= 0 && norm[before] is '.' or '@' or '#') yield break;
        var taken = new HashSet<string>(already.Select(a => a.Label), StringComparer.OrdinalIgnoreCase);
        var start = caret - w.Length;
        foreach (var k in SqlSyntaxHighlighter.KeywordList)
            if (k.StartsWith(w, StringComparison.OrdinalIgnoreCase) && !k.Equals(w, StringComparison.OrdinalIgnoreCase) && taken.Add(k.ToUpperInvariant()))
                yield return new HintItem(k.ToUpperInvariant(), 100, "từ khoá", "", k.ToUpperInvariant(), false, start, caret, "9" + k, k);
        var n = 0;
        foreach (var name in _names())
        {
            if (!name.StartsWith(w, StringComparison.OrdinalIgnoreCase) || name.Equals(w, StringComparison.OrdinalIgnoreCase) || !taken.Add(name)) continue;
            yield return new HintItem(name, 101, "object", "", name, false, start, caret, "9z" + name, name);
            if (++n >= 80) break;
        }
    }

    // ------------------------------------------------------------------ chấp nhận gợi ý

    private void Accept(HintItem item)
    {
        if (_snapPos < 0 || _ed.CurrentPosition != _snapPos) { Hide(); return; }
        var beforeText = _snapNorm[Math.Max(0, item.Start).._snapChar];
        var afterText = item.End > _snapChar ? _snapNorm[_snapChar..Math.Min(item.End, _snapNorm.Length)] : "";
        var startPos = _snapPos - Encoding.UTF8.GetByteCount(beforeText);
        var endPos = _snapPos + Encoding.UTF8.GetByteCount(afterText);
        Hide();
        if (item.Snippet) _snippet.Begin(startPos, endPos, item.Insert);
        else
        {
            _ed.TargetStart = startPos; _ed.TargetEnd = endPos;
            _ed.ReplaceTarget(item.Insert);
            _ed.GotoPosition(startPos + Encoding.UTF8.GetByteCount(item.Insert));
        }
        _ed.Focus();
    }

    // ------------------------------------------------------------------ chữ ký

    private async Task SignatureAsync()
    {
        try
        {
            var pos = _ed.CurrentPosition;
            var norm = SqlHintEngine.Normalize(_ed.Text ?? "");
            var caretChar = SqlHintEngine.Normalize(_ed.GetTextRange(0, pos)).Length;
            var sys = _useSys();
            var key = Key();
            var routines = await RoutinesAsync(sys);
            var sig = await Task.Run(() => SqlHintEngine.Shared.Signature(norm, caretChar, key, routines));
            if (_disposed || _ed.IsDisposed || sig is null || _ed.CurrentPosition != pos) { if (sig is null && _ed.CallTipActive) _ed.CallTipCancel(); return; }
            if (_ed.CallTipActive) _ed.CallTipCancel();
            _ed.CallTipShow(pos, sig.Label);
            if (sig.Params.Count > 0)
            {
                var (s, e) = sig.Params[Math.Clamp(sig.Active, 0, sig.Params.Count - 1)];
                _ed.CallTipSetHlt(s, e);
            }
        }
        catch { /* chỉ là tiện ích */ }
    }

    // ------------------------------------------------------------------ cảnh báo biến chưa khai báo

    private async Task LintAsync()
    {
        if (_disposed || _ed.IsDisposed) return;
        var text = _ed.Text ?? "";
        var norm = SqlHintEngine.Normalize(text);
        var issues = await Task.Run(() => SqlHintEngine.Shared.Lint(norm));
        if (_disposed || _ed.IsDisposed || (_ed.Text ?? "") != text) return;
        _issues = issues;
        _lintText = text;
        _ed.IndicatorCurrent = LintIndicator;
        _ed.IndicatorClearRange(0, _ed.TextLength);
        foreach (var i in issues)
        {
            var (s, e) = ToByteRange(text, i.Offset, i.Length);
            _ed.IndicatorFillRange(s, e - s);
        }
    }

    /// <summary>Vị trí ký tự trong bản đã chuẩn hoá \n → khoảng byte của Scintilla (có thể có \r\n và chữ có dấu).</summary>
    private static (int Start, int End) ToByteRange(string original, int normOffset, int length)
    {
        int Origin(int norm)
        {
            var n = 0;
            for (var i = 0; i < original.Length; i++)
            {
                if (n == norm) return i;
                if (original[i] == '\r' && i + 1 < original.Length && original[i + 1] == '\n') continue;
                n++;
            }
            return original.Length;
        }
        var a = Origin(normOffset); var b = Origin(normOffset + length);
        var sb = Encoding.UTF8.GetByteCount(original.AsSpan(0, a));
        return (sb, sb + Encoding.UTF8.GetByteCount(original.AsSpan(a, b - a)));
    }

    private void OnDwell(object? sender, DwellEventArgs e)
    {
        if (_issues.Count == 0 || (_ed.Text ?? "") != _lintText) return;
        foreach (var i in _issues)
        {
            var (s, en) = ToByteRange(_lintText, i.Offset, i.Length);
            if (e.Position >= s && e.Position <= en)
            {
                _ed.CallTipShow(e.Position, i.Name + " được dùng nhưng chưa khai báo (không có trong tham số, cũng không DECLARE).");
                return;
            }
        }
    }
}
