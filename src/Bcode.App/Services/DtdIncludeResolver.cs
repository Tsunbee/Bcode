using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Đọc phần DTD của 1 file controller FCode (Dir/Grid/Filter...) GIỐNG trình đọc XML của FCode, để File Lookup dò đúng những gì form thật sự dùng:
///   • khai báo &lt;!ENTITY&gt; trước thắng (khai sau cùng tên bị bỏ qua);
///   • %X; gọi tới thì nạp nội dung entity tham số X (literal hoặc file SYSTEM, mọi đuôi: DiscountPromotion.SVTran, EIReleasedInvoice.svtran...) NGAY tại chỗ gọi;
///   • khối điều kiện &lt;![%X;[ ... ]]&gt; chỉ đọc khi X = INCLUDE (X có thể là file chỉ chứa chữ INCLUDE/IGNORE) — khối IGNORE bỏ hẳn, kể cả khai báo bên trong;
///   • phần thân: &amp;Y; với Y là entity thường SYSTEM thì nạp file đó (và các &amp;Z; bên trong nó).
/// Đo trên menu SVTran (VPMILK): DiscountPromotion.ent khai đủ bản SV/SO/SQ/SI nhưng chỉ bản SV được gọi; Extender / DownPayment có khối IGNORE chứa
/// CBDownPayment, CRDownPayment, SVGoodsIssue — FCode không hiện những form đó, đọc thiếu luật IGNORE thì Bcode hiện thừa.
/// </summary>
internal sealed class DtdIncludeResolver
{
    private static readonly Regex DeclRegex = new(@"^<!ENTITY\s+(%\s+)?([A-Za-z0-9_.$:-]+)\s+(?:SYSTEM\s+(?:""([^""]*)""|'([^']*)')|""([^""]*)""|'([^']*)')", RegexOptions.Compiled);
    private static readonly Regex NameRefRegex = new(@"\G([A-Za-z0-9_.$:-]+);", RegexOptions.Compiled);
    private static readonly Regex CondStartRegex = new(@"\G<!\[\s*(?:%([A-Za-z0-9_.$:-]+);|(INCLUDE|IGNORE))\s*\[", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GeneralRefRegex = new(@"&([A-Za-z0-9_.$:-]+);", RegexOptions.Compiled);
    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".xlsx", ".xls", ".rpt", ".frx", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".zip", ".rar", ".7z", ".dll", ".exe", ".xsd" };
    private const int MaxFiles = 300, MaxDepth = 40;

    private sealed record Entity(string? Literal, string? Path);

    private readonly Func<string, string?> _read;
    private readonly Dictionary<string, Entity> _param = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Entity> _general = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _activeDtd = new();
    private int _files;

    private DtdIncludeResolver(Func<string, string?> read) => _read = read;

    /// <summary>Kết quả: <see cref="Texts"/> = phần có hiệu lực (DTD đã bỏ khối IGNORE + thân file + các file nạp vào thân) để dò tên controller;
    /// <see cref="Values"/> = giá trị các &lt;!ENTITY X "..."&gt; thường đang có hiệu lực (để thay &amp;X; trong controller="...").</summary>
    public sealed record Result(List<string> Texts, Dictionary<string, string> Values);

    public static Result Resolve(string filePath, string content, Func<string, string?> read)
    {
        var r = new DtdIncludeResolver(read);
        var texts = new List<string>();
        var dir = Path.GetDirectoryName(filePath) ?? "";
        // <!DOCTYPE x [ ...DTD... ]> — không có phần DTD trong (file .txt/.ent đứng riêng) thì coi cả file là thân.
        var dt = Regex.Match(content, @"<!DOCTYPE\s+[^\[>]*\[", RegexOptions.IgnoreCase);
        var bodyStart = 0;
        if (dt.Success)
        {
            var end = r.ProcessDtd(content, dt.Index + dt.Length, dir, 0, stopAtDoctypeEnd: true);
            bodyStart = end;
        }
        texts.Add(r._activeDtd.ToString());
        var body = content[bodyStart..];
        texts.Add(body);
        r.LoadBodyEntities(body, texts, 0);
        var values = r._general.Where(kv => kv.Value.Literal is not null).ToDictionary(kv => kv.Key, kv => kv.Value.Literal!, StringComparer.Ordinal);
        return new Result(texts, values);
    }

    private string? ReadFile(string path)
    {
        if (_fileCache.TryGetValue(path, out var t)) return t;
        if (_files >= MaxFiles || BinaryExtensions.Contains(Path.GetExtension(path))) return _fileCache[path] = null;
        _files++;
        return _fileCache[path] = _read(path);
    }

    private static string? Combine(string dir, string rel)
    {
        try { return Path.GetFullPath(Path.Combine(dir, rel)); } catch (Exception) { return null; }
    }

    /// <summary>Đọc DTD từ <paramref name="i"/>; ghi phần có hiệu lực vào _activeDtd. Trả vị trí dừng (sau "]&gt;" của DOCTYPE nếu <paramref name="stopAtDoctypeEnd"/>).</summary>
    private int ProcessDtd(string s, int i, string dir, int depth, bool stopAtDoctypeEnd)
    {
        var condStack = 0; // số khối <![ INCLUDE [ đang mở trong lượt đọc này
        while (i < s.Length)
        {
            if (string.CompareOrdinal(s, i, "<!--", 0, 4) == 0)
            {
                var e = s.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = e < 0 ? s.Length : e + 3;
                continue;
            }
            if (string.CompareOrdinal(s, i, "<!ENTITY", 0, 8) == 0)
            {
                var e = DeclEnd(s, i);
                var decl = s[i..e];
                _activeDtd.Append(decl).Append('\n');
                var m = DeclRegex.Match(decl);
                if (m.Success)
                {
                    var map = m.Groups[1].Success ? _param : _general;
                    var name = m.Groups[2].Value;
                    if (!map.ContainsKey(name))
                    {
                        var sys = m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Success ? m.Groups[4].Value : null;
                        map[name] = sys is not null
                            ? new Entity(null, Combine(dir, sys))
                            : new Entity(m.Groups[5].Success ? m.Groups[5].Value : m.Groups[6].Value, null);
                    }
                }
                i = e;
                continue;
            }
            if (string.CompareOrdinal(s, i, "<![", 0, 3) == 0)
            {
                var c = CondStartRegex.Match(s, i);
                if (c.Success)
                {
                    var keyword = c.Groups[2].Success ? c.Groups[2].Value : ParamValue(c.Groups[1].Value, dir, depth)?.Trim();
                    if (string.Equals(keyword, "IGNORE", StringComparison.OrdinalIgnoreCase)) { i = SkipSection(s, c.Index + c.Length); continue; }
                    // INCLUDE (hoặc chưa biết — coi như INCLUDE như trước khi có luật này)
                    condStack++;
                    i = c.Index + c.Length;
                    continue;
                }
            }
            if (string.CompareOrdinal(s, i, "]]>", 0, 3) == 0 && condStack > 0) { condStack--; i += 3; continue; }
            if (stopAtDoctypeEnd && condStack == 0 && s[i] == ']')
            {
                var gt = s.IndexOf('>', i + 1);
                if (gt > 0 && s.AsSpan(i + 1, gt - i - 1).Trim().IsEmpty) return gt + 1;
            }
            if (s[i] == '%')
            {
                var r = NameRefRegex.Match(s, i + 1);
                if (r.Success)
                {
                    ExpandParam(r.Groups[1].Value, depth);
                    i = r.Index + r.Length;
                    continue;
                }
            }
            i++;
        }
        return s.Length;
    }

    /// <summary>Giá trị của entity tham số (để xét INCLUDE/IGNORE): literal, hoặc nội dung file SYSTEM.</summary>
    private string? ParamValue(string name, string dir, int depth)
    {
        if (!_param.TryGetValue(name, out var e)) return null;
        if (e.Literal is not null) return e.Literal;
        return e.Path is null ? null : ReadFile(e.Path);
    }

    private void ExpandParam(string name, int depth)
    {
        if (depth >= MaxDepth || !_param.TryGetValue(name, out var e)) return;
        if (e.Literal is not null) { ProcessDtd(e.Literal, 0, "", depth + 1, stopAtDoctypeEnd: false); return; }
        if (e.Path is null || ReadFile(e.Path) is not { } text) return;
        ProcessDtd(text, 0, Path.GetDirectoryName(e.Path) ?? "", depth + 1, stopAtDoctypeEnd: false);
    }

    /// <summary>Thân file: &amp;Y; với Y là entity thường SYSTEM → nạp file đó vào danh sách dò (và các &amp;Z; trong nó).</summary>
    private void LoadBodyEntities(string text, List<string> texts, int depth)
    {
        if (depth >= MaxDepth) return;
        foreach (Match m in GeneralRefRegex.Matches(text))
        {
            if (!_general.TryGetValue(m.Groups[1].Value, out var e) || e.Path is null || _fileCache.ContainsKey(e.Path)) continue;
            if (ReadFile(e.Path) is not { } inc) continue;
            texts.Add(inc);
            LoadBodyEntities(inc, texts, depth + 1);
        }
    }

    /// <summary>Hết 1 khai báo &lt;!ENTITY ...&gt; (bỏ qua '&gt;' nằm trong chuỗi "..." / '...').</summary>
    private static int DeclEnd(string s, int i)
    {
        char quote = '\0';
        for (var k = i + 2; k < s.Length; k++)
        {
            var ch = s[k];
            if (quote != '\0') { if (ch == quote) quote = '\0'; }
            else if (ch == '"' || ch == '\'') quote = ch;
            else if (ch == '>') return k + 1;
        }
        return s.Length;
    }

    /// <summary>Bỏ qua hết khối điều kiện IGNORE (tính cả khối lồng bên trong).</summary>
    private static int SkipSection(string s, int i)
    {
        var level = 1;
        while (i < s.Length && level > 0)
        {
            if (string.CompareOrdinal(s, i, "<![", 0, 3) == 0) { level++; i += 3; }
            else if (string.CompareOrdinal(s, i, "]]>", 0, 3) == 0) { level--; i += 3; }
            else i++;
        }
        return i;
    }
}
