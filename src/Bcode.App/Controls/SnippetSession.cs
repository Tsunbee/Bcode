using System.Text;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>
/// Chèn "snippet" kiểu Monaco vào Scintilla: <c>${1:chữ mặc định}</c>, <c>${1|a,b,c|}</c> (lấy lựa chọn đầu), <c>$1</c>, <c>$0</c> (vị trí cuối), <c>\$ \} \\</c> là ký tự thường.
/// Chèn xong chọn sẵn ô đầu tiên; Tab = sang ô kế (các ô cùng số được chép theo chữ vừa gõ), Shift+Tab = lùi, Esc / hết ô / sửa ngoài vùng = kết thúc. Vị trí các ô được theo dõi
/// bằng sự kiện Insert/Delete của Scintilla (đơn vị byte UTF-8) nên gõ chữ có dấu tiếng Việt vẫn đúng.
/// </summary>
internal sealed class SnippetSession
{
    private sealed class Field { public int Index; public int Start, End; }

    private readonly Scintilla _ed;
    private List<Field> _fields = new();
    private int _current = -1;
    private bool _busy;

    public bool Active => _fields.Count > 0;

    public SnippetSession(Scintilla ed)
    {
        _ed = ed;
        _ed.Insert += (_, e) => OnInsert(e.Position, Encoding.UTF8.GetByteCount(e.Text ?? ""));
        _ed.Delete += (_, e) => OnDelete(e.Position, Encoding.UTF8.GetByteCount(e.Text ?? ""));
    }

    /// <summary>Phân tích thân snippet → (chữ thuần, các ô theo thứ tự xuất hiện; ô Index 0 = vị trí cuối nếu có).</summary>
    public static (string Text, List<(int Index, int Start, int Length)> Fields) Parse(string body)
    {
        var sb = new StringBuilder();
        var raw = new List<(int Index, int Start, string Def)>();
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length && (body[i + 1] is '$' or '}' or '\\')) { sb.Append(body[++i]); continue; }
            if (c != '$' || i + 1 >= body.Length) { sb.Append(c); continue; }
            var n = body[i + 1];
            if (char.IsDigit(n))
            {
                var j = i + 1; var num = 0;
                while (j < body.Length && char.IsDigit(body[j])) { num = num * 10 + (body[j] - '0'); j++; }
                raw.Add((num, sb.Length, ""));
                i = j - 1; continue;
            }
            if (n == '{')
            {
                var j = i + 2; var num = 0; var any = false;
                while (j < body.Length && char.IsDigit(body[j])) { num = num * 10 + (body[j] - '0'); j++; any = true; }
                if (!any) { sb.Append(c); continue; }
                var def = new StringBuilder();
                if (j < body.Length && body[j] == ':')
                {
                    j++;
                    while (j < body.Length && body[j] != '}')
                    {
                        if (body[j] == '\\' && j + 1 < body.Length && (body[j + 1] is '$' or '}' or '\\')) { def.Append(body[j + 1]); j += 2; continue; }
                        def.Append(body[j]); j++;
                    }
                }
                else if (j < body.Length && body[j] == '|')
                {
                    j++;
                    var choices = new StringBuilder();
                    while (j < body.Length && body[j] != '|') { choices.Append(body[j]); j++; }
                    def.Append(choices.ToString().Split(',')[0]);
                    j++;   // bỏ '|' cuối
                }
                raw.Add((num, sb.Length, def.ToString()));
                sb.Append(def);
                i = j;   // đứng ở '}'
                continue;
            }
            sb.Append(c);
        }
        // Ô cùng số dùng chung chữ mặc định (lấy cái không rỗng đầu tiên) — dựng lại chữ để mọi lần xuất hiện cùng nội dung.
        var defaults = raw.Where(r => r.Index != 0 && r.Def.Length > 0).GroupBy(r => r.Index).ToDictionary(g => g.Key, g => g.First().Def);
        var outText = new StringBuilder();
        var fields = new List<(int, int, int)>();
        var last = 0;
        var src = sb.ToString();
        var shift = 0;
        foreach (var r in raw)
        {
            var origStart = r.Start;
            outText.Append(src, last, origStart - last);
            last = origStart + r.Def.Length;
            var def = r.Index != 0 && defaults.TryGetValue(r.Index, out var d) ? d : r.Def;
            var start = origStart + shift;
            outText.Append(def);
            fields.Add((r.Index, start, def.Length));
            shift += def.Length - r.Def.Length;
        }
        outText.Append(src, last, src.Length - last);
        return (outText.ToString(), fields);
    }

    /// <summary>Thay đoạn [startPos, endPos) (byte) bằng snippet và bắt đầu phiên điền ô.</summary>
    public void Begin(int startPos, int endPos, string body)
    {
        End();
        var (text, fields) = Parse(body);
        _busy = true;
        try
        {
            _ed.TargetStart = startPos; _ed.TargetEnd = endPos;
            _ed.ReplaceTarget(text);
            var list = new List<Field>();
            foreach (var (idx, s, len) in fields)
            {
                var bs = startPos + Encoding.UTF8.GetByteCount(text.AsSpan(0, s));
                var be = bs + Encoding.UTF8.GetByteCount(text.AsSpan(s, len));
                list.Add(new Field { Index = idx, Start = bs, End = be });
            }
            var endAll = startPos + Encoding.UTF8.GetByteCount(text);
            if (!list.Any(f => f.Index == 0)) list.Add(new Field { Index = 0, Start = endAll, End = endAll });
            _fields = list;
        }
        finally { _busy = false; }
        var firstIdx = _fields.Where(f => f.Index > 0).Select(f => f.Index).DefaultIfEmpty(0).Min();
        if (firstIdx == 0) { var z = _fields.First(f => f.Index == 0); _ed.SetSelection(z.Start, z.End); _fields = new(); return; }   // không có ô: con trỏ ở cuối
        GoTo(firstIdx);
    }

    private IEnumerable<int> Order() => _fields.Select(f => f.Index).Where(i => i > 0).Distinct().OrderBy(i => i).Concat(_fields.Any(f => f.Index == 0) ? new[] { 0 } : Array.Empty<int>());

    private void GoTo(int index)
    {
        var f = _fields.First(x => x.Index == index);
        _current = index;
        _ed.SetSelection(f.End, f.Start);   // chọn cả ô, con trỏ ở cuối
        if (index == 0) End();
    }

    /// <summary>Tab / Shift+Tab. Trả true nếu phiên đang chạy (phím đã được xử lý).</summary>
    public bool Next(bool back)
    {
        if (!Active) return false;
        SyncMirrors();
        var order = Order().ToList();
        var at = order.IndexOf(_current);
        var next = back ? at - 1 : at + 1;
        if (next < 0) next = 0;
        if (next >= order.Count) { End(); return true; }
        GoTo(order[next]);
        return true;
    }

    /// <summary>Chép chữ của ô đang điền sang các ô cùng số.</summary>
    private void SyncMirrors()
    {
        if (_current <= 0) return;
        var cur = _fields.FirstOrDefault(f => f.Index == _current && f.Start <= _ed.CurrentPosition && _ed.CurrentPosition <= f.End) ?? _fields.First(f => f.Index == _current);
        var text = _ed.GetTextRange(cur.Start, cur.End);
        var mirrors = _fields.Where(f => f.Index == _current && f != cur).OrderByDescending(f => f.Start).ToList();
        if (mirrors.Count == 0) return;
        _busy = true;
        try
        {
            foreach (var m in mirrors)
            {
                if (_ed.GetTextRange(m.Start, m.End) == text) continue;
                _ed.TargetStart = m.Start; _ed.TargetEnd = m.End;
                _ed.ReplaceTarget(text);
                var delta = Encoding.UTF8.GetByteCount(text) - (m.End - m.Start);
                foreach (var f in _fields)
                {
                    if (f == m) continue;
                    if (f.Start >= m.End) { f.Start += delta; f.End += delta; }
                }
                m.End += delta;
            }
        }
        finally { _busy = false; }
    }

    public void End() { _fields = new(); _current = -1; }

    private void OnInsert(int pos, int len)
    {
        if (_busy || !Active) return;
        var inside = false;
        foreach (var f in _fields)
        {
            if (f.Start > pos || (f.Start == pos && f.End > f.Start && f.Index != _current)) { f.Start += len; f.End += len; }
            else if (f.End >= pos) { f.End += len; inside = true; }
        }
        if (!inside && !_fields.Any(f => f.Start <= pos && pos <= f.End)) End();
    }

    private void OnDelete(int pos, int len)
    {
        if (_busy || !Active) return;
        var end = pos + len;
        foreach (var f in _fields)
        {
            int Map(int p) => p >= end ? p - len : p > pos ? pos : p;
            f.Start = Map(f.Start); f.End = Map(f.End);
        }
    }
}
