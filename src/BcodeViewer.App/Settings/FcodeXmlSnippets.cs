using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BcodeViewer.App.Settings;

/// <summary>
/// Đọc bộ snippet gốc của FCodeViewer (sqlsnippet.xml, jssnippet.xml, htmlsnippet.xml, csssnippet.xml và
/// các bản viewsrc*) và đổi sang cú pháp tabstop của Monaco để dùng chung với Hint Code.
///
/// Mỗi file có tối đa hai phần:
///   &lt;Snippet&gt;&lt;Item keyword="select"&gt;…   — gõ keyword ra ngay đoạn code (như Hint Code có Prefix)
///   &lt;Hint&gt;&lt;HintItem object="f;w" [char='"']&gt;  — gợi ý SAU "f." (JS) hoặc sau  attr=" (giá trị thuộc tính)
/// Vùng áp dụng suy từ tên file: *sql* → SQL, *css* → CSS, *html* → XML, còn lại → JS (xem
/// completion.js: REGION_CATEGORIES quyết định snippet nào hiện ở vùng nào của controller).
///
/// Cú pháp placeholder của FCode → Monaco:
///   {{-}} {{|}} {{--}}   vị trí con trỏ đầu tiên          → ${1}
///   {{N}} / {{N-tên}}    điểm dừng kế tiếp (tên là chữ mờ) → ${k:tên}   ({{N-a,b,c}} thành danh sách chọn)
/// Chữ thường có '$' hay '\' được escape để Monaco không hiểu nhầm là biến (vd. $func, @$msgCheck).
/// </summary>
public static class FcodeXmlSnippets
{
    public record Snip(string Category, string Prefix, string Code, string Description);
    public record HintGroup(string Category, string[] Objects, string Char, List<HintEntry> Items);
    public record HintEntry(string Keyword, string Code, string Description);
    public record Result(List<Snip> Snippets, List<HintGroup> Hints);

    // Thư mục người dùng chỉ định (nếu có) thắng bản đóng gói kèm app.
    public static string ResolveFolder(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured)) return configured;
        return Path.Combine(AppContext.BaseDirectory, "Assets", "snippets");
    }

    public static Result Load(string folder)
    {
        var snippets = new List<Snip>();
        var hints = new List<HintGroup>();
        if (!Directory.Exists(folder)) return new Result(snippets, hints);

        // viewsrc* trước: bản "viewer" là bản đầy đủ hơn, trùng khoá thì nó thắng.
        var files = Directory.EnumerateFiles(folder, "*snippet*.xml")
            .Where(f => !Path.GetFileName(f).Equals("snippet.xml", StringComparison.OrdinalIgnoreCase)
                     && !Path.GetFileName(f).Equals("snippetViewer.xml", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => Path.GetFileName(f).StartsWith("viewsrc", StringComparison.OrdinalIgnoreCase))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase);

        var seenSnip = new HashSet<string>(StringComparer.Ordinal);
        var seenHint = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            try
            {
                var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                var category = name.Contains("sql") ? "SQL" : name.Contains("css") ? "CSS" : name.Contains("html") ? "XML" : "JS";
                var cssNewline = category == "CSS";
                var doc = XDocument.Load(file);

                foreach (var item in doc.Descendants("Snippet").SelectMany(s => s.Elements("Item")))
                {
                    var keyword = (item.Attribute("keyword")?.Value ?? "").Trim();
                    var code = ToMonaco(item.Value, cssNewline);
                    if (keyword.Length == 0 || code.Length == 0) continue;
                    if (!seenSnip.Add(category + "|" + keyword)) continue;
                    snippets.Add(new Snip(category, keyword, code, item.Attribute("description")?.Value ?? ""));
                }

                foreach (var group in doc.Descendants("HintItem"))
                {
                    var objects = (group.Attribute("object")?.Value ?? "")
                        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (objects.Length == 0) continue;
                    var items = new List<HintEntry>();
                    foreach (var item in group.Elements("Item"))
                    {
                        var keyword = (item.Attribute("keyword")?.Value ?? "").Trim();
                        var code = ToMonaco(item.Value, cssNewline);
                        if (keyword.Length == 0 || code.Length == 0) continue;
                        if (!seenHint.Add(category + "|" + string.Join(';', objects) + "|" + keyword)) continue;
                        items.Add(new HintEntry(keyword, code, item.Attribute("description")?.Value ?? ""));
                    }
                    if (items.Count > 0) hints.Add(new HintGroup(category, objects, group.Attribute("char")?.Value ?? "", items));
                }
            }
            catch { /* một file hỏng không được làm mất cả bộ snippet */ }
        }
        return new Result(snippets, hints);
    }

    private static readonly Regex Token = new(@"\{\{([^{}]*)\}\}", RegexOptions.Compiled);
    private static readonly Regex Numbered = new(@"^\s*(\d+)\s*(?:-\s*(.*?))?\s*$", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex CursorOnly = new(@"^\s*[-|]*\s*$", RegexOptions.Compiled);

    public static string ToMonaco(string raw, bool literalBackslashN)
    {
        var s = raw.Replace("\r\n", "\n").Trim('\n', '\r', ' ', '\t');
        if (literalBackslashN) s = s.Replace("\\n", "\n");
        // Lỗi gõ trong file gốc: {{0}-formID} → {{0-formID}}
        s = Regex.Replace(s, @"\{\{(\d+)\}-([^{}]*)\}", "{{$1-$2}}");
        if (s.Length == 0) return "";

        // Điểm dừng theo thứ tự chỉ số N; con trỏ ({{-}}) luôn là điểm dừng 1.
        var matches = Token.Matches(s).Cast<Match>().ToList();
        var hasCursor = matches.Any(m => CursorOnly.IsMatch(m.Groups[1].Value));
        var order = matches.Select(m => Numbered.Match(m.Groups[1].Value)).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(n => n).ToList();
        var first = hasCursor ? 2 : 1;

        var sb = new StringBuilder();
        var last = 0;
        var cursorUsed = false;
        foreach (var m in matches)
        {
            sb.Append(EscapeText(s.Substring(last, m.Index - last)));
            last = m.Index + m.Length;
            var inner = m.Groups[1].Value;

            if (CursorOnly.IsMatch(inner))
            {
                // Chỉ con trỏ đầu tiên là điểm dừng; dấu thừa thì bỏ.
                if (!cursorUsed) { sb.Append("${1}"); cursorUsed = true; }
                continue;
            }
            var n = Numbered.Match(inner);
            if (!n.Success) { sb.Append(EscapeText(m.Value)); continue; }

            var index = first + order.IndexOf(int.Parse(n.Groups[1].Value));
            var label = n.Groups[2].Success ? n.Groups[2].Value : "";
            if (label.Length == 0) sb.Append("${").Append(index).Append('}');
            else if (label.Contains(',')) sb.Append("${").Append(index).Append('|').Append(label.Replace("\\", "\\\\").Replace("|", "\\|")).Append("|}");
            else sb.Append("${").Append(index).Append(':').Append(EscapePlaceholder(label)).Append('}');
        }
        sb.Append(EscapeText(s.Substring(last)));
        return sb.ToString();
    }

    private static string EscapeText(string t) => t.Replace("\\", "\\\\").Replace("$", "\\$");
    private static string EscapePlaceholder(string t) => t.Replace("\\", "\\\\").Replace("$", "\\$").Replace("}", "\\}");
}
