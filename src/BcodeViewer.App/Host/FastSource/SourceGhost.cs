using System.Text.Json;
using System.Text.RegularExpressions;

namespace BcodeViewer.App.Host.FastSource;

/// <summary>
/// Ghost text theo cấu trúc Fast (không AI) cho BcodeViewer — cùng cách làm với phía SQL của Bcode (FastGhostRules / FastGhostMiner):
/// <list type="bullet">
/// <item>"field" — ma trận theo field: trong khối <c>&lt;field name="ma_kh"&gt;</c>, đầu dòng đang gõ (≤ 6 token) → phần còn lại của dòng hay gặp
/// (<c>&lt;header v="Mã khách" e="Customer"&gt;&lt;/header&gt;</c>, <c>&lt;items style="AutoComplete" controller="Customer" …/&gt;</c>); dòng trống =
/// dòng kế tiếp theo đầu dòng phía trên.</item>
/// <item>"same" — theo chính file: dòng khác trong file bắt đầu đúng như phần đã gõ (controller lặp cấu trúc rất nhiều).</item>
/// <item>"tok" — ma trận token <see cref="SourceTokenModel"/> (xml / aspx / js), trộn n-gram của chính file.</item>
/// <item>"line" / "next" — chỉ ở vùng JS và SQL (CDATA), cùng cách với ma trận đầu dòng / dòng tiếp theo của Bcode SQL (FastGhostRules.LineStart /
/// NextLine) nhưng học từ chính JS / SQL trong source Fast: vài token đầu dòng (≤ 6) → phần còn lại của dòng; đầu dòng phía trên (≤ 4 token, kèm dòng
/// trên nữa nếu có) → cả dòng kế tiếp. Đếm theo SỐ FILE có cách viết đó, giữ cách viết có ở ≥ 2 file. Đo: JS + SQL chế độ ngắn 980 → 1.747 gợi ý đúng,
/// vẫn đúng ≥ 90%. (Dùng thẳng ma trận SQL của Bcode — học từ stored procedure — thì đo chỉ đúng 33%: SQL trong controller viết khác hẳn.)</item>
/// <item>"word" — đang gõ dở 1 tên (<c>f.setIt</c>): hoàn tất bằng tên trong chính file, gần + cùng ngữ cảnh thì nặng hơn (<see cref="AddWord"/>).</item>
/// </list>
/// <item>"close" — gõ <c>&lt;/</c>: tên thẻ mở gần nhất chưa đóng (theo cấu trúc, không thống kê; đo 99,9% đúng) — thắng mọi nguồn khác.</item>
/// Mỗi loại có ngưỡng riêng hiệu chỉnh trên nửa file mẫu không dùng để rút (ngắn ≥ 90%, dài ≥ 60%); gộp = <see cref="Choose"/>: nguồn qua ngưỡng có độ tin
/// cao nhất thắng (cùng độ tin thì dài hơn). Trước đây "dài nhất thắng" — đo: 657 / 2.231 chỗ sai là vì 1 gợi ý dài hơn mà sai đè lên nguồn chắc hơn đã đúng.
/// </summary>
public sealed class SourceGhost
{
    public sealed record Ghost(string Text, string Kind, double Share)
    {
        /// <summary>Ô ma trận sinh ra gợi ý — để học từ Tab / bỏ qua của người dùng (<see cref="Adjust"/>); null = nguồn không có ô (same / tok / close).</summary>
        public Cell? Cell { get; init; }
    }

    /// <summary>1 ô ma trận: <see cref="Dict"/> = "F" (<see cref="Fields"/>), "L" (<see cref="Lines"/>), "X" (<see cref="Nexts"/>); <see cref="Option"/> = cách viết
    /// hay gặp nhất của ô (đã bỏ dấu cách đầu theo chỗ gõ) — gợi ý hiện ra là nó hoặc phần đầu của nó.</summary>
    public sealed record Cell(string Dict, string Key, string Option);

    /// <summary>Bảng của 1 loại ô.</summary>
    public Dictionary<string, Dictionary<string, int>>? Table(string dict) => dict switch { "F" => Fields, "L" => Lines, "X" => Nexts, _ => null };

    /// <summary>Chỉnh 1 ô theo phản hồi: <paramref name="optionDelta"/> cộng vào cách viết đó (nhận), <paramref name="otherDelta"/> cộng vào phần "khác" (bỏ qua —
    /// độ tin của mọi cách viết trong ô giảm). Ô chưa có thì tạo (phản hồi của đồng nghiệp ở ô máy này chưa học).</summary>
    public void Adjust(string dict, string key, string option, int optionDelta, int otherDelta)
    {
        if (Table(dict) is not { } t) return;
        if (!t.TryGetValue(key, out var d)) t[key] = d = new(StringComparer.Ordinal);
        if (optionDelta != 0 && option.Length > 0)
        {
            // cách viết lưu có thể có 1 dấu cách đầu (" style=…") — cộng vào đúng bản đang có
            var k = d.ContainsKey(option) ? option : d.ContainsKey(" " + option) ? " " + option : option;
            d[k] = Math.Max(0, d.GetValueOrDefault(k) + optionDelta);
        }
        if (otherDelta != 0) d[""] = Math.Max(0, d.GetValueOrDefault("") + otherDelta);
    }
    public sealed record Quality(double MinShare, double Precision, double Coverage, int Tested);

    /// <summary>Mô hình token theo loại source: "xml" (controller, .ent, include .txt), "aspx" (aspx / ascx / master / html), "js" (đoạn script).</summary>
    public Dictionary<string, SourceTokenModel> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"F:&lt;field&gt;|&lt;đầu dòng&gt;" → phần còn lại của dòng → số lần.</summary>
    public Dictionary<string, Dictionary<string, int>> Fields { get; } = new(StringComparer.Ordinal);
    /// <summary>"&lt;vùng js|sql&gt;|&lt;đầu dòng k token&gt;" → phần còn lại của dòng (1 dấu cách đầu nếu dòng thật có) → số file.</summary>
    public Dictionary<string, Dictionary<string, int>> Lines { get; } = new(StringComparer.Ordinal);
    /// <summary>"&lt;vùng&gt;|^&lt;đầu dòng trên&gt;[|&lt;đầu dòng trên nữa&gt;]" → dòng kế tiếp → số file.</summary>
    public Dictionary<string, Dictionary<string, int>> Nexts { get; } = new(StringComparer.Ordinal);
    /// <summary>Ngưỡng từng loại: field, same, tok (ngắn) và field:long, same:long, tok:long (dài).</summary>
    public Dictionary<string, Quality> Thresholds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public const double ShortTarget = 0.9, LongTarget = 0.6;

    public double MinShareOf(string kind) => Thresholds.TryGetValue(kind, out var q) ? q.MinShare : 1.01;

    /// <summary>Ngưỡng của 1 loại trong 1 vùng ("tok@js", "same@xml:long"…); vùng chưa hiệu chỉnh riêng thì lấy ngưỡng chung của loại.</summary>
    public double MinShareOf(string kind, string area, bool longMode)
    {
        var sfx = longMode ? ":long" : "";
        // vùng riêng chưa hiệu chỉnh thì lùi dần: "import/fl" → "xml/fl" → "import" → "xml" → ngưỡng chung của loại
        foreach (var a in new[] { area, area.Replace("import", "xml"), area.Split('/')[0], area.Split('/')[0].Replace("import", "xml") })
            if (Thresholds.TryGetValue(kind + "@" + a + sfx, out var q)) return q.MinShare;
        return MinShareOf(kind + sfx);
    }

    /// <summary>Vùng ngưỡng của 1 chỗ gõ: vùng cơ bản (<see cref="AreaOf"/>), file import / clipboard dùng "import" thay "xml",
    /// dòng vừa gõ tên field (<c>&lt;field name="X"</c> …) thêm "/fl" — đo trên source Fast: 2 loại chỗ này đúng khác hẳn XML thường.</summary>
    public static string AreaFor(string kind, string? region, string root, string lineBefore)
    {
        var empty = lineBefore.Trim().Length == 0;
        var area = AreaOf(kind, region, empty);
        if (root is "import" or "clipboard" && area.StartsWith("xml")) area = "import" + area[3..];
        if (!empty && FieldLineRx.IsMatch(lineBefore)) area = area.Split('/')[0] + "/fl";
        else if (!empty && EndsInName(lineBefore)) area += "/mw";
        return area;
    }

    /// <summary>Con trỏ sát sau 1 tên (≥ 2 chữ: <c>f.setIt|</c>) — có thể đang gõ dở tên đó: các nguồn đúng khác hẳn chỗ ranh giới token nên có vùng ngưỡng "/mw".</summary>
    public static bool EndsInName(string lineBefore)
    {
        var i = lineBefore.Length;
        while (i > 0 && SourceLexer.IsWord(lineBefore[i - 1])) i--;
        return lineBefore.Length - i >= 2 && SourceLexer.IsWordStart(lineBefore[i]) && lineBefore[i] != '@' && (i == 0 || lineBefore[i - 1] is not ('"' or '\''))
            && SourceLexer.Tokens(lineBefore) is { Count: > 0 } t && t[^1].Start == i;
    }

    /// <summary>Vùng để chọn ngưỡng: aspx / js / sql / xml (giống <see cref="SourceGhostMiner.Regions"/>), thêm "/next" khi con trỏ ở dòng trống
    /// (đoán cả dòng kế tiếp khó hơn hẳn đoán nốt dòng đang gõ nên có ngưỡng riêng).</summary>
    public static string AreaOf(string kind, string? region, bool emptyLine = false) => kind switch
    {
        "aspx" => region == "js" ? "js" : "aspx",
        "js" => "js",
        _ => region is "js" or "sql" ? region : "xml",
    } + (emptyLine ? "/next" : "");

    // ---------------------------------------------------------------- loại file / vùng

    /// <summary>Loại source theo đuôi file: xml / aspx / js ("" = không gợi ý).</summary>
    public static string KindOfPath(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch
    {
        ".xml" or ".ent" or ".txt" or ".f" or ".xsd" => "xml",
        ".aspx" or ".ascx" or ".master" or ".html" or ".htm" => "aspx",
        ".js" => "js",
        "" => "xml",   // include không đuôi (Extra.AIDetail)
        _ => "",
    };

    /// <summary>Mô hình dùng cho vùng con trỏ: vùng script (region "js") dùng mô hình js, còn lại theo loại file.</summary>
    public SourceTokenModel? ModelFor(string kind, string? region)
    {
        if (region == "js" && Models.TryGetValue("js", out var js)) return js;
        return Models.TryGetValue(kind, out var m) ? m : Models.GetValueOrDefault("xml");
    }

    /// <summary>Các đoạn script của 1 file (nội dung &lt;script&gt;…&lt;/script&gt;, bỏ dấu CDATA) — nguồn rút mô hình js.</summary>
    public static IEnumerable<string> ScriptParts(string text)
    {
        foreach (Match m in ScriptRx.Matches(text))
            yield return m.Groups[1].Value.Replace("<![CDATA[", "").Replace("]]>", "");
    }
    private static readonly Regex ScriptRx = new(@"<script\b[^>]*>([\s\S]*?)</script>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ---------------------------------------------------------------- ngữ cảnh field

    /// <summary>Dòng khai 1 field: <c>&lt;field name="X"</c> + phần còn lại của dòng (thuộc tính).</summary>
    private static readonly Regex FieldLineRx = new(@"^\s*<field\s+name\s*=\s*""(?<n>[^""&]+)""(?<r>.*)$", RegexOptions.Compiled);

    /// <summary>Loại controller của file: import / clipboard (Templates\Upload, Clipboard), dir, grid, filter, report, lookup — theo đường dẫn, không thì
    /// theo thẻ gốc trong văn bản; "" = không rõ. Cùng thẻ &lt;field&gt; nhưng mỗi loại khai thuộc tính khác hẳn (import: column / resource / check).</summary>
    public static string RootOf(string? path, string text)
    {
        var p = (path ?? "").Replace('\\', '/');
        if (Regex.Match(p, @"/Templates/(Upload|Clipboard)/", RegexOptions.IgnoreCase) is { Success: true } t) return t.Groups[1].Value.ToLowerInvariant() == "upload" ? "import" : "clipboard";
        if (Regex.Match(p, @"/(Dir|Grid|Filter|Report|Lookup)/", RegexOptions.IgnoreCase) is { Success: true } m) return m.Groups[1].Value.ToLowerInvariant();
        return Regex.Match(text, @"<(dir|grid|filter|report|lookup|import|clipboard)\b") is { Success: true } r ? r.Groups[1].Value : "";
    }

    private static readonly Regex FieldOpenRx =new(@"<field\s+name=""(?<n>[^""]+)""[^>]*?(?<sc>/?)>|<field\s+name=""(?<n>[^""]+)""[^>]*$", RegexOptions.Compiled | RegexOptions.RightToLeft);

    /// <summary>Tên field của khối &lt;field name="…"&gt; chứa con trỏ (chưa đóng), null nếu không ở trong khối field.</summary>
    public static string? FieldAt(string before)
    {
        var tail = before.Length > 8000 ? before[^8000..] : before;
        var m = FieldOpenRx.Match(tail);
        if (!m.Success || m.Groups["sc"].Value == "/") return null;
        if (tail.IndexOf("</field>", m.Index, StringComparison.Ordinal) >= 0) return null;
        var name = m.Groups["n"].Value;
        return name.Length is > 0 and < 60 && !name.Contains('&') ? name : null;
    }

    public static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>Đầu dòng chuẩn hoá (token nối bằng 1 dấu cách).</summary>
    public static string HeadOf(IReadOnlyList<SourceLexer.Token> toks, int n) => string.Join(" ", toks.Take(n).Select(t => SourceTokenModel.NormTok(t.Text)));

    // ---------------------------------------------------------------- rút

    /// <summary>Rút kiến thức từ các file (đường dẫn, nội dung). Ma trận field giữ cách viết gặp ≥ <paramref name="minCount"/> lần.</summary>
    public static SourceGhost Train(IReadOnlyList<(string Path, string Text)> files, int minCount = 2, int tokMin = 2)
    {
        var g = new SourceGhost();
        foreach (var kind in new[] { "xml", "aspx" })
        {
            var texts = files.Where(f => KindOfPath(f.Path) == kind).Select(f => f.Text).ToList();
            if (texts.Count > 0) g.Models[kind] = SourceTokenModel.Train(texts, minTotal: tokMin);
        }
        var scripts = files.Where(f => KindOfPath(f.Path) is "xml" or "aspx").SelectMany(f => ScriptParts(f.Text))
            .Concat(files.Where(f => KindOfPath(f.Path) == "js").Select(f => f.Text)).ToList();
        if (scripts.Count > 0) g.Models["js"] = SourceTokenModel.Train(scripts, minTotal: tokMin);

        var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
        {
            if (KindOfPath(path) != "xml") continue;
            void Count(string key, string rest)
            {
                if (!counts.TryGetValue(key, out var d)) counts[key] = d = new(StringComparer.Ordinal);
                d[rest] = d.GetValueOrDefault(rest) + 1;
            }
            foreach (var (key, rest) in FieldSites(text)) Count(key, rest);
            var root = RootOf(path, text);
            foreach (var (name, rest, sec) in FieldLines(text))
            {
                Count("N:" + root + ":" + sec + "|" + name, rest);
                Count("N:" + root + ":" + sec + "|*", rest);
            }
        }
        foreach (var (k, d) in counts)
        {
            var kept = d.Where(x => x.Value >= minCount).ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
            // còn giữ "tổng" để độ tin không bị thổi phồng khi bỏ các cách viết hiếm
            var other = d.Values.Sum() - kept.Values.Sum();
            if (kept.Count == 0) continue;
            if (other > 0) kept[""] = other;
            g.Fields[k] = kept;
        }
        TrainLines(g, files, minFiles: 2);
        return g;
    }

    /// <summary>Ma trận đầu dòng / dòng tiếp theo cho vùng JS và SQL — đếm số file (1 file lặp 1 cách viết nhiều lần chỉ tính 1).</summary>
    private static void TrainLines(SourceGhost g, IReadOnlyList<(string Path, string Text)> files, int minFiles)
    {
        // giá trị → (số file, file cuối đã đếm): file đọc lần lượt nên đủ để đếm mỗi file 1 lần mà không giữ tập file
        var lines = new Dictionary<string, Dictionary<string, (int N, int Last)>>(StringComparer.Ordinal);
        var nexts = new Dictionary<string, Dictionary<string, (int N, int Last)>>(StringComparer.Ordinal);
        static void Add(Dictionary<string, Dictionary<string, (int N, int Last)>> m, string key, string val, int file)
        {
            if (!m.TryGetValue(key, out var d)) m[key] = d = new(StringComparer.Ordinal);
            var c = d.GetValueOrDefault(val, (N: 0, Last: -1));
            if (c.Last != file) d[val] = (c.N + 1, file);
        }
        for (var fi = 0; fi < files.Count; fi++)
        {
            var (path, raw) = files[fi];
            var kind = KindOfPath(path);
            if (kind == "") continue;
            var text = raw.Replace("\r\n", "\n");
            var ls = text.Split('\n');
            var reg = SourceGhostMiner.Regions(text, kind);
            string? prev = null, prev2 = null;
            var prevReg = "";
            var stack = new List<string>();
            for (var i = 0; i < ls.Length; i++)
            {
                var toks = SourceLexer.Tokens(ls[i]);
                var r = reg[i];
                if (toks.Count == 0) continue;
                if (r is "xml" or "aspx")
                {
                    // XML / ASPX: khoá thêm thẻ cha (ngăn thẻ TRƯỚC dòng này — như lúc gõ chỉ thấy phía trên)
                    var pk = PathKey(stack);
                    for (var k = 1; k <= Math.Min(6, toks.Count - 1); k++)
                        Add(lines, r + "|" + pk + HeadOf(toks, k), (char.IsWhiteSpace(ls[i][toks[k].Start - 1]) ? " " : "") + Norm(ls[i][toks[k].Start..]), fi);
                    if (prev is not null && prevReg == r) Add(nexts, r + "|" + pk + "^" + prev, Norm(ls[i]), fi);
                    FeedTags(stack, ls[i], kind);
                }
                else if (r is "js" or "sql")
                {
                    for (var k = 1; k <= Math.Min(6, toks.Count - 1); k++)
                        Add(lines, r + "|" + HeadOf(toks, k), (char.IsWhiteSpace(ls[i][toks[k].Start - 1]) ? " " : "") + Norm(ls[i][toks[k].Start..]), fi);
                    if (prev is not null && prevReg == r)
                    {
                        Add(nexts, r + "|^" + prev, Norm(ls[i]), fi);
                        if (prev2 is not null) Add(nexts, r + "|^" + prev + "|" + prev2, Norm(ls[i]), fi);
                    }
                }
                prev2 = prevReg == r ? prev : null;
                prev = HeadOf(toks, 4);
                prevReg = r;
            }
        }
        static void Keep(Dictionary<string, Dictionary<string, (int N, int Last)>> src, Dictionary<string, Dictionary<string, int>> dst, int min)
        {
            foreach (var (k, d) in src)
            {
                var kept = d.Where(x => x.Value.N >= min).ToDictionary(x => x.Key, x => x.Value.N, StringComparer.Ordinal);
                if (kept.Count == 0) continue;
                var other = d.Values.Sum(x => x.N) - kept.Values.Sum();
                if (other > 0) kept[""] = other;   // giữ "tổng" để độ tin không bị thổi phồng
                dst[k] = kept;
            }
        }
        Keep(lines, g.Lines, minFiles);
        Keep(nexts, g.Nexts, minFiles);
    }

    /// <summary>Các dòng <c>&lt;field name="X" …</c> của 1 văn bản: (tên, phần sau tên — giữ 1 dấu cách đầu nếu có, gộp khoảng trắng).</summary>
    public static IEnumerable<(string Name, string Tail, string Section)> FieldLines(string text)
    {
        bool inViews = false, inTemplate = false;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            var m = FieldLineRx.Match(line);
            if (m.Success && m.Groups["r"].Value.Trim().Length > 0)
            {
                var r = m.Groups["r"].Value;
                yield return (m.Groups["n"].Value, (char.IsWhiteSpace(r[0]) ? " " : "") + Norm(r), inTemplate ? "template" : inViews ? "views" : "fields");
            }
            if (Regex.IsMatch(line, @"<views\b")) inViews = true;
            if (line.Contains("</views>")) inViews = false;
            if (Regex.IsMatch(line, @"<template\b")) inTemplate = true;
            if (line.Contains("</template>")) inTemplate = false;
        }
    }

    /// <summary>Mục chứa con trỏ: "views" (trong &lt;views&gt; — field chỉ là tham chiếu, thường <c>&lt;field name="x"/&gt;</c>) hay "fields" (khai báo).
    /// Người dùng báo: trong &lt;views&gt; của Grid lại gợi ý thuộc tính khai báo (isPrimaryKey, width…) của khu &lt;fields&gt;.</summary>
    public static string SectionAt(string before)
    {
        static bool Inside(string text, string tag)
        {
            var open = text.LastIndexOf("<" + tag, StringComparison.Ordinal);
            return open >= 0 && text.IndexOf("</" + tag + ">", open, StringComparison.Ordinal) < 0;
        }
        // file import: <template><fields row="5"> = cột của file Excel mẫu (width / starColor), khác hẳn khối field nhập (column / resource / check)
        return Inside(before, "template") ? "template" : Inside(before, "views") ? "views" : "fields";
    }

    /// <summary>Mọi chỗ của ma trận field trong 1 file: (khoá, phần còn lại của dòng).</summary>
    public static IEnumerable<(string Key, string Tail)> FieldSites(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? field = null;
        string prevHead = "";
        foreach (var line in lines)
        {
            var toks = SourceLexer.Tokens(line);
            if (toks.Count == 0) continue;
            var open = Regex.Match(line, @"<field\s+name=""([^""]+)""[^>]*?(/?)>");
            if (field is not null && !line.Contains("<field"))
            {
                var key = "F:" + field + "|";
                yield return (key + "^" + prevHead, Norm(line));
                // phần còn lại giữ 1 dấu cách đầu nếu dòng thật có khoảng trắng trước token đó (gõ "<items" chưa cách → gợi ý " style=…")
                for (var j = 1; j <= Math.Min(6, toks.Count - 1); j++)
                    yield return (key + HeadOf(toks, j), (char.IsWhiteSpace(line[toks[j].Start - 1]) ? " " : "") + Norm(line[toks[j].Start..]));
            }
            prevHead = HeadOf(toks, 3);
            if (open.Success) field = open.Groups[2].Value == "/" || line.Contains("</field>") ? null : open.Groups[1].Value;
            if (line.Contains("</field>")) field = null;
        }
    }

    // ---------------------------------------------------------------- gợi ý

    /// <summary>Gợi ý tại con trỏ: <paramref name="before"/> = toàn bộ văn bản trước con trỏ, <paramref name="after"/> = phần sau dòng đang gõ.
    /// <paramref name="longMode"/> = chế độ dài (ngưỡng 60%). null = không gợi ý.</summary>
    /// <param name="siblings">Các dòng (đã gộp khoảng trắng) của vài file "anh em" trong dự án (cùng loại, nhiều field trùng — FastSourceService.SiblingsFor) → nguồn "sib".</param>
    public Ghost? Explain(string before, string after, string kind, string? region, bool longMode, bool useLocal = true, string root = "",
        IReadOnlyList<string[]>? siblings = null)
    {
        var cells = new Dictionary<int, Cell>();
        var cands = Candidates(before, after, kind, region, useLocal, root, cells, siblings);
        var area = AreaFor(kind, region, root, before[(before.LastIndexOf('\n') + 1)..]);
        var (g, at) = ChooseAt(cands, k => MinShareOf(k, area, longMode));
        return g is not null && cells.TryGetValue(at, out var c) ? g with { Cell = c } : g;
    }

    /// <summary>Gộp các nguồn (dùng chung lúc gõ và lúc đo / hiệu chỉnh — <see cref="SourceGhostMiner"/>): "close" thắng tuyệt đối; còn lại nguồn qua ngưỡng
    /// có độ tin cao nhất, cùng độ tin thì gợi ý dài hơn. null = không nguồn nào qua ngưỡng.</summary>
    public static Ghost? Choose(IEnumerable<(string Kind, string Text, double Share, Func<double, string> Cut)> cands, Func<string, double> threshold) =>
        ChooseAt(cands, threshold).Ghost;

    /// <summary><see cref="Choose"/> kèm vị trí nguồn được chọn trong danh sách (-1 = không có).</summary>
    public static (Ghost? Ghost, int At) ChooseAt(IEnumerable<(string Kind, string Text, double Share, Func<double, string> Cut)> cands, Func<string, double> threshold)
    {
        Ghost? best = null;
        var bestRank = double.NegativeInfinity;
        int i = -1, at = -1;
        foreach (var (k, _, share, cut) in cands)
        {
            i++;
            var t = cut(k == "close" ? 0 : threshold(k));
            if (Norm(t).Length == 0) continue;
            var rank = k == "close" ? double.PositiveInfinity : share;
            if (best is null || rank > bestRank + 1e-9 || (Math.Abs(rank - bestRank) <= 1e-9 && Norm(t).Length > Norm(best.Text).Length))
            {
                best = new Ghost(t, k, share);
                bestRank = rank;
                at = i;
            }
        }
        return (best, at);
    }

    /// <summary>Từng nguồn gợi ý: (loại, gợi ý đầy đủ, độ tin, cắt theo ngưỡng).</summary>
    /// <param name="cells">Nếu có: vị trí nguồn trong danh sách → ô ma trận sinh ra nó (chỉ nguồn field / line / next).</param>
    public List<(string Kind, string Text, double Share, Func<double, string> Cut)> Candidates(string before, string after, string kind, string? region, bool useLocal = true, string root = "",
        Dictionary<int, Cell>? cells = null, IReadOnlyList<string[]>? siblings = null)
    {
        var res = new List<(string, string, double, Func<double, string>)>();
        void Dist(string k, Dictionary<string, int> d, string partial, bool endsSpace, string? dict = null, string? cellKey = null, double smoothing = 1)
        {
            var n = res.Count;
            var top = AddDistribution(res, k, d, partial, endsSpace, smoothing);
            if (cells is not null && dict is not null && cellKey is not null && top is not null && res.Count > n) cells[n] = new Cell(dict, cellKey, top);
        }
        var nl = before.LastIndexOf('\n');
        var lineBefore = before[(nl + 1)..];
        var toks = SourceLexer.Tokens(lineBefore);
        // đang gõ dở 1 từ / chuỗi thì phần đã gõ của token cuối là tiền tố bắt buộc
        var typed = Norm(lineBefore);

        // gõ "</" (hoặc "</ab" dở): đóng đúng thẻ đang mở — chắc chắn, khỏi tính các nguồn thống kê (nhanh hơn: ~0,2 ms thay ~7 ms)
        if (CloseTag(before, kind, region) is { } close)
        {
            res.Add(("close", close, 1.0, _ => close));
            return res;
        }

        // field
        if (kind == "xml" && FieldAt(before) is { } field && toks.Count <= 6)
        {
            string key;
            if (toks.Count == 0)
            {
                var prev = PrevLineTokens(before, nl);
                key = "F:" + field + "|^" + HeadOf(prev, 3);
            }
            else key = "F:" + field + "|" + HeadOf(toks, toks.Count);
            var partial = "";
            if (toks.Count > 0 && !char.IsWhiteSpace(lineBefore[^1]) && toks[^1].Start + toks[^1].Text.Length == lineBefore.Length)
            {
                // đầu dòng kết thúc giữa 1 token: khoá theo các token trước, phần còn lại phải bắt đầu bằng token dở
                key = "F:" + field + "|" + (toks.Count == 1 ? "^" + HeadOf(PrevLineTokens(before, nl), 3) : HeadOf(toks, toks.Count - 1));
                partial = toks[^1].Text;
            }
            if (Fields.TryGetValue(key, out var d)) Dist("field", d, partial, EndsSpace(lineBefore), "F", key);
        }

        // dòng <field name="X" … (gõ xong tên field): phần thuộc tính còn lại theo (1) cách khai field X hay gặp ở loại file này,
        // (2) khai báo đặc thù của CHÍNH file — các dòng <field> gần nhất (import: column="." resource="master"/>), (3) cách khai chung của loại file.
        if (kind == "xml" && FieldLineRx.Match(lineBefore) is { Success: true } fl && !fl.Groups["r"].Value.Contains('>'))
        {
            var name = fl.Groups["n"].Value;
            var partial = Regex.Replace(fl.Groups["r"].Value.TrimStart(), @"\s+", " ");
            var endsSpace = EndsSpace(lineBefore);
            var sec = SectionAt(before);
            if (Fields.TryGetValue("N:" + root + ":" + sec + "|" + name, out var byName)) Dist("field", byName, partial, endsSpace, "F", "N:" + root + ":" + sec + "|" + name);
            if (FileFieldShapes(before, after, nl, name) is { } shapes) Dist("same", shapes, partial, endsSpace);
            if (Fields.TryGetValue("N:" + root + ":" + sec + "|*", out var generic)) Dist("field", generic, partial, endsSpace, "F", "N:" + root + ":" + sec + "|*");
        }

        // same
        var same = SameFile(before, after, nl, toks, lineBefore);
        // dòng <field name="X" mà X đã khai trong chính file (cùng mục): chép đúng cách khai đó — 1 lần gặp đã đủ chắc (làm trơn 0.25 thay 1)
        if (same is not null) Dist("same", same, "", EndsSpace(lineBefore), smoothing: FieldLineRx.IsMatch(lineBefore) ? 0.25 : 1);

        // "sib" — file anh em trong dự án (Dir / Grid… cùng loại, nhiều field trùng): như "theo chính file" nhưng trên các file đó
        if (siblings is { Count: > 0 } && SiblingDistribution(siblings, before, nl, toks, lineBefore) is { } sib)
            Dist("sib", sib, "", toks.Count == 0 || EndsSpace(lineBefore));

        // ma trận đầu dòng / dòng tiếp theo — vùng XML / ASPX: khoá thêm thẻ cha (Cách 4)
        var lr = AreaOf(kind, region);
        if (lr is "xml" or "aspx")
        {
            var stack = new List<string>();
            FeedTags(stack, before[..(nl + 1)], kind);
            var pk = PathKey(stack);
            if (toks.Count is > 0 and <= 6)
            {
                var midToken = SourceLexer.IsWord(lineBefore[^1]) && toks[^1].Start + toks[^1].Text.Length == lineBefore.Length;
                var lk = lr + "|" + pk + HeadOf(toks, toks.Count);
                if (Lines.TryGetValue(lk, out var ld)) Dist("line", ld, "", EndsSpace(lineBefore), "L", lk);
                var lkp = lr + "|" + pk + HeadOf(toks, toks.Count - 1);
                if (midToken && toks.Count > 1 && Lines.TryGetValue(lkp, out var lp)) Dist("line", lp, toks[^1].Text, false, "L", lkp);
            }
            else if (toks.Count == 0 && PrevLineTokens(before, nl) is { Count: > 0 } p1)
            {
                var nk = lr + "|" + pk + "^" + HeadOf(p1, 4);
                if (Nexts.TryGetValue(nk, out var nd)) Dist("next", nd, "", true, "X", nk);
            }
        }
        // ma trận đầu dòng / dòng tiếp theo — vùng JS và SQL
        if (lr is "js" or "sql")
        {
            if (toks.Count is > 0 and <= 6)
            {
                // đang gõ dở token cuối: khoá theo các token trước, gợi ý phải bắt đầu bằng phần đã gõ của token đó
                // (chỉ token chữ / số — dấu "(" "." "=" đã gõ là xong, khoá theo cả nó)
                var midToken = SourceLexer.IsWord(lineBefore[^1]) && toks[^1].Start + toks[^1].Text.Length == lineBefore.Length;
                // Chữ sát con trỏ có thể đã gõ xong ("a" của "a.b") hoặc còn dở ("ge" của "getItemValue") → thử cả 2 khoá, Choose lấy cái chắc hơn.
                var lk = lr + "|" + HeadOf(toks, toks.Count);
                if (Lines.TryGetValue(lk, out var ld)) Dist("line", ld, "", EndsSpace(lineBefore), "L", lk);
                var lkp = lr + "|" + HeadOf(toks, toks.Count - 1);
                if (midToken && toks.Count > 1 && Lines.TryGetValue(lkp, out var lp)) Dist("line", lp, toks[^1].Text, false, "L", lkp);
            }
            else if (toks.Count == 0 && PrevLineTokens(before, nl) is { Count: > 0 } p1)
            {
                var p2 = PrevLineTokens(before, PrevLineStart(before, nl));
                var nk = lr + "|^" + HeadOf(p1, 4) + "|" + HeadOf(p2, 4);
                if (!(p2.Count > 0 && Nexts.TryGetValue(nk, out var nd))) { nk = lr + "|^" + HeadOf(p1, 4); Nexts.TryGetValue(nk, out nd); }
                if (nd is not null) Dist("next", nd, "", true, "X", nk);
            }
        }

        // tok
        var model = ModelFor(kind, region);
        if (model is not null && toks.Count > 0)
        {
            var ctxText = before.Length > 3000 ? before[^3000..] : before;
            SourceTokenModel? local = null;
            if (useLocal) local = SourceTokenModel.LocalOf(before, after);
            var dec = SourceTokenModel.Decode(model, local, ctxText);
            if (dec is not null) res.Add(("tok", dec.Cut(0), dec.Cum[0], t => dec.Cut(t)));
        }
        else if (model is not null && toks.Count == 0 && before.TrimEnd().Length > 0)
        {
            // dòng trống: đoán đầu dòng kế tiếp theo ngữ cảnh token (⏎ là ngữ cảnh)
            var ctxText = before.Length > 3000 ? before[^3000..] : before;
            SourceTokenModel? local = useLocal ? SourceTokenModel.LocalOf(before, after) : null;
            var dec = SourceTokenModel.Decode(model, local, ctxText.TrimEnd(' ', '\t'));
            if (dec is not null) res.Add(("tok", dec.Cut(0), dec.Cum[0], t => dec.Cut(t)));
        }
        AddWord(res, before, after, nl, toks, lineBefore);
        AddAgreement(res);
        return res;
    }

    private static readonly Regex NameRx = new(@"(?<![\w$])[A-Za-z_$][\w$]*", RegexOptions.Compiled);

    /// <summary>"word" — đang gõ dở 1 tên (hàm / biến / thành viên: <c>f.setIt</c>, <c>function onChange$Voucher$Ta</c>): hoàn tất bằng các tên trong
    /// CHÍNH file (trên + dưới, kể cả trong chuỗi / CDATA như <c>onchange="onChange$Voucher$X(this)"</c>) bắt đầu đúng phần đã gõ. Mỗi lần gặp nặng theo
    /// độ gần dòng đang gõ, gặp sau cùng token đứng trước ("f" "." …) nặng gấp 4–8. Sau <c>function</c>: bỏ các tên đã khai hàm (khai lại là trùng) —
    /// còn lại là tên được gọi mà chưa có hàm. Thêm "(" khi tên đó hầu như luôn được gọi. Cắt theo ngưỡng ở ranh giới chữ hoa / $ / _ của tên.</summary>
    private static void AddWord(List<(string Kind, string Text, double Share, Func<double, string> Cut)> res, string before, string after, int nl,
        List<SourceLexer.Token> toks, string lineBefore)
    {
        if (toks.Count == 0) return;
        var last = toks[^1];
        if (last.Text.Length < 2 || last.Start + last.Text.Length != lineBefore.Length || !SourceLexer.IsWordStart(last.Text[0]) || last.Text[0] == '@') return;
        var prefix = last.Text;
        var prev = toks.Count > 1 ? toks[^2].Text : "";
        var prev2 = toks.Count > 2 ? toks[^3].Text : "";
        var decl = prev == "function";
        var w = new Dictionary<string, (double W, double Call)>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        void Scan(string line, int dist)
        {
            if (line.IndexOf(prefix, StringComparison.Ordinal) < 0) return;
            foreach (Match m in NameRx.Matches(line))
            {
                var name = m.Value;
                if (name.Length <= prefix.Length || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var pre = line[..m.Index].TrimEnd();
                if (pre.EndsWith("function", StringComparison.Ordinal) && (pre.Length == 8 || !SourceLexer.IsWord(pre[^9]))) declared.Add(name);
                double ctx = 1;
                if (prev.Length > 0 && pre.EndsWith(prev, StringComparison.Ordinal))
                    ctx = prev2.Length > 0 && pre[..^prev.Length].TrimEnd().EndsWith(prev2, StringComparison.Ordinal) ? 8 : 4;
                // sau "function": chỗ gọi thường ở xa (<clientScript> phía trên) — đếm số lần gọi, không tính độ gần
                var weight = decl ? 1 : ctx / (1 + dist / 20.0);
                var call = line.AsSpan(m.Index + m.Length).TrimStart().StartsWith("(") ? weight : 0;
                var o = w.GetValueOrDefault(name);
                w[name] = (o.W + weight, o.Call + call);
            }
        }
        var above = nl < 0 ? Array.Empty<string>() : before[..nl].Split('\n');
        for (var i = above.Length - 1; i >= 0; i--) Scan(above[i], above.Length - i);
        var below = after.Split('\n');
        for (var i = 0; i < below.Length; i++) Scan(below[i], i + 1);
        if (decl)
        {
            foreach (var d in declared) w.Remove(d);
            // nguồn khác (line / tok…) gợi ý ra tên hàm đã khai trong file → khai lại là trùng: bỏ
            res.RemoveAll(c => c.Kind != "close" && declared.Contains(prefix + new string(c.Text.TakeWhile(SourceLexer.IsWord).ToArray())));
        }
        if (w.Count == 0) return;
        // sau "function" chỉ còn tên được gọi mà chưa có hàm — 1 tên duy nhất đã khá chắc (làm trơn 0.25 thay 1)
        var total = w.Values.Sum(x => x.W) + (decl ? 0.25 : 1);
        var opts = w.OrderByDescending(x => x.Value.W).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        var (top, (topW, topCall)) = (opts[0].Key, opts[0].Value);
        var text = top[prefix.Length..] + (topCall >= 0.8 * topW ? "(" : "");
        var share = topW / total;
        string Cut(double th)
        {
            if (share >= th - 1e-9) return text;
            // phần đầu chung của các tên: lùi dần theo ranh giới chữ hoa / $ / _ của tên hay gặp nhất
            for (var j = top.Length - 1; j > prefix.Length; j--)
            {
                if (!(char.IsUpper(top[j]) || top[j] is '$' or '_')) continue;
                var p = top[..j];
                if (opts.Where(x => x.Key.StartsWith(p, StringComparison.Ordinal)).Sum(x => x.Value.W) / total >= th - 1e-9) return p[prefix.Length..];
            }
            return "";
        }
        res.Add(("word", text, share, Cut));
    }

    /// <summary>"agree": 2 nguồn KHÁC LOẠI (field / same / tok / line / next — đếm độc lập với nhau) cùng gợi ý 1 phần đầu (cắt ở ranh giới token) thì phần đó
    /// đáng tin hơn từng nguồn: độ tin = 1 − (1 − a)(1 − b). Có ngưỡng riêng như các loại khác (hiệu chỉnh trên nửa file mẫu).</summary>
    private static void AddAgreement(List<(string Kind, string Text, double Share, Func<double, string> Cut)> res)
    {
        string bestText = "";
        double bestShare = 0;
        for (var i = 0; i < res.Count; i++)
            for (var j = i + 1; j < res.Count; j++)
            {
                var (ki, ti, si, _) = res[i];
                var (kj, tj, sj, _) = res[j];
                if (ki == kj || ki is "close" or "agree" || kj is "close" or "agree" || si <= 0 || sj <= 0) continue;
                var p = CommonPrefix(ti, tj);
                if (Norm(p).Length == 0) continue;
                var share = 1 - (1 - si) * (1 - sj);
                if (share > bestShare + 1e-9 || (Math.Abs(share - bestShare) <= 1e-9 && Norm(p).Length > Norm(bestText).Length)) { bestText = p; bestShare = share; }
            }
        if (bestText.Length == 0) return;
        var text = bestText;
        res.Add(("agree", text, bestShare, th => bestShare >= th - 1e-9 ? text : ""));
    }

    /// <summary>Phần đầu chung dài nhất của 2 gợi ý, cắt ở ranh giới token của <paramref name="a"/> (so gộp khoảng trắng); "" nếu không chung token nào.</summary>
    private static string CommonPrefix(string a, string b)
    {
        var nb = Norm(b);
        var toks = SourceLexer.Tokens(a);
        for (var j = toks.Count; j >= 1; j--)
        {
            var end = toks[j - 1].Start + toks[j - 1].Text.Length;
            var p = a[..end];
            if (nb.StartsWith(Norm(p), StringComparison.Ordinal)) return p;
        }
        return "";
    }

    /// <summary>Thẻ: comment / CDATA / khai báo &lt;!…&gt; / &lt;?…?&gt; (bỏ qua), hoặc thẻ mở / đóng / tự đóng (giá trị thuộc tính có thể chứa &gt;).</summary>
    private static readonly Regex TagRx = new(@"<!--[\s\S]*?-->|<!\[CDATA\[[\s\S]*?\]\]>|<![^>]*>|<\?[\s\S]*?\?>|<(?<c>/?)(?<n>[A-Za-z_][\w:.-]*)(?<a>(?:[^<>""']|""[^""]*""|'[^']*')*?)(?<s>/?)>", RegexOptions.Compiled);
    private static readonly Regex EventAttrRx = new(@"\bevent=""([^""]+)""", RegexOptions.Compiled);

    /// <summary>Khoá ma trận dòng XML / ASPX theo 2 thẻ mở gần nhất (thẻ có event="…" thì kèm event: command[Loading]) — cùng đầu dòng
    /// <c>&lt;item</c> trong &lt;command event="Loading"&gt; hay trong &lt;views&gt; viết khác hẳn. false = chỉ theo đầu dòng.</summary>
    public const bool XmlPathKeys = true;

    /// <summary>Cập nhật ngăn thẻ đang mở theo 1 đoạn văn bản (thẻ đóng gỡ tới thẻ mở cùng tên gần nhất; bỏ thẻ tự đóng, thẻ void ASPX).</summary>
    private static void FeedTags(List<string> stack, string text, string kind)
    {
        foreach (Match t in TagRx.Matches(text))
        {
            if (!t.Groups["n"].Success || t.Groups["s"].Value == "/") continue;
            var name = t.Groups["n"].Value;
            if (t.Groups["c"].Value == "/") { var k = stack.FindLastIndex(x => x == name || x.StartsWith(name + "[", StringComparison.Ordinal)); if (k >= 0) stack.RemoveRange(k, stack.Count - k); }
            else if (!(kind == "aspx" && VoidHtml.Contains(name)))
                stack.Add(name + (EventAttrRx.Match(t.Groups["a"].Value) is { Success: true } e ? "[" + e.Groups[1].Value + "]" : ""));
        }
    }

    private static string PathKey(List<string> stack) => XmlPathKeys ? string.Join(">", stack.Skip(Math.Max(0, stack.Count - 2))) + "|" : "";
    private static readonly Regex CloseOpenRx = new(@"</(?<p>[A-Za-z_][\w:.-]*)?$", RegexOptions.Compiled);
    private static readonly HashSet<string> VoidHtml = new(StringComparer.OrdinalIgnoreCase) { "br", "img", "input", "meta", "link", "hr", "col", "area", "base", "param", "source", "wbr" };

    /// <summary>Dòng đang gõ kết thúc bằng "&lt;/" (hoặc "&lt;/ab" dở) ở vùng xml / aspx → phần còn lại của tên thẻ mở gần nhất chưa đóng + "&gt;"; null = không áp.</summary>
    public static string? CloseTag(string before, string kind, string? region)
    {
        if (kind is not ("xml" or "aspx") || region is "js" or "sql") return null;
        var nl = before.LastIndexOf('\n');
        var m = CloseOpenRx.Match(before, nl + 1);
        if (!m.Success) return null;
        var partial = m.Groups["p"].Value;
        var stack = new List<string>();
        foreach (Match t in TagRx.Matches(before[..m.Index]))
        {
            if (!t.Groups["n"].Success || t.Groups["s"].Value == "/") continue;
            var name = t.Groups["n"].Value;
            if (t.Groups["c"].Value == "/") { var k = stack.FindLastIndex(x => x == name); if (k >= 0) stack.RemoveRange(k, stack.Count - k); }
            else if (!(kind == "aspx" && VoidHtml.Contains(name))) stack.Add(name);
        }
        if (stack.Count == 0) return null;
        var open = stack[^1];
        return open.StartsWith(partial, StringComparison.Ordinal) ? open[partial.Length..] + ">" : null;
    }

    /// <summary>Vị trí ký tự xuống dòng ngay trước dòng code (có token) phía trên con trỏ — để lấy tiếp dòng trên nữa bằng <see cref="PrevLineTokens"/>; -1 nếu không có.</summary>
    private static int PrevLineStart(string before, int nl)
    {
        var end = nl;
        while (end > 0)
        {
            var s = before.LastIndexOf('\n', end - 1);
            if (SourceLexer.Tokens(before[(s + 1)..end]).Count > 0) return s;
            if (s < 0) break;
            end = s;
        }
        return -1;
    }

    private static List<SourceLexer.Token> PrevLineTokens(string before, int nl)
    {
        var end = nl;
        while (end > 0)
        {
            var s = before.LastIndexOf('\n', end - 1);
            var l = before[(s + 1)..end];
            var t = SourceLexer.Tokens(l);
            if (t.Count > 0) return t;
            end = s;
            if (s < 0) break;
        }
        return new();
    }

    /// <summary>Phân bố phần còn lại → (gợi ý đầy đủ, cắt theo ngưỡng): cả cách viết hay gặp nhất nếu đủ chắc, không thì phần đầu chung của nhiều cách viết
    /// (cắt ở ranh giới token) mà tổng số lần còn ≥ ngưỡng.</summary>
    private static bool EndsSpace(string s) => s.Length == 0 || char.IsWhiteSpace(s[^1]);

    private static string? AddDistribution(List<(string, string, double, Func<double, string>)> res, string kind, Dictionary<string, int> raw, string partial, bool endsSpace, double smoothing = 1)
    {
        var total = raw.Values.Sum();
        // dấu cách đầu: bỏ khi đã gõ khoảng trắng / đang gõ dở token
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (k, n) in raw)
        {
            if (k.Length == 0) continue;
            var key = endsSpace || partial.Length > 0 ? k.TrimStart() : k;
            d[key] = d.GetValueOrDefault(key) + n;
        }
        var opts = d.Where(x => x.Key.Length > 0 && x.Key.StartsWith(partial, StringComparison.Ordinal) && x.Key.Length > partial.Length)
            .OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
        if (opts.Count == 0) return null;
        double denom = (partial.Length > 0 ? opts.Sum(x => x.Value) : total) + smoothing;
        var top = opts[0];
        string Cut(double th)
        {
            if (top.Value / denom >= th - 1e-9) return top.Key[partial.Length..];
            // phần đầu chung: lùi dần theo ranh giới token của cách viết hay gặp nhất
            var toks = SourceLexer.Tokens(top.Key);
            for (var j = toks.Count - 1; j >= 1; j--)
            {
                var p = top.Key[..toks[j].Start].TrimEnd();
                if (p.Length <= partial.Length) break;
                var n = opts.Where(x => x.Key.StartsWith(p, StringComparison.Ordinal)).Sum(x => x.Value);
                if (n / denom >= th - 1e-9) return p[partial.Length..];
            }
            return "";
        }
        res.Add((kind, top.Key[partial.Length..], top.Value / denom, Cut));
        return top.Key;
    }

    /// <summary>Phần sau tên của 30 dòng &lt;field name=…&gt; gần con trỏ nhất trong CÙNG khối &lt;fields&gt; / &lt;views&gt; đang gõ (trên + dưới), trừ field đang gõ.
    /// Không vượt ra ngoài khối: file import có nhiều khối field khác kiểu (danh sách cột Excel width="16" ≠ field nhập column / resource).</summary>
    private static Dictionary<string, int>? FileFieldShapes(string before, string after, int nl, string current)
    {
        var above = (nl < 0 ? "" : before[..nl]).Split('\n');
        var below = after.Split('\n');
        var near = new List<(int Dist, string Tail)>();
        static bool Opens(string l) => Regex.IsMatch(l, @"<(fields|views|view)\b");
        static bool Closes(string l) => Regex.IsMatch(l, @"</(fields|views|view)>");
        for (var i = above.Length - 1; i >= 0 && near.Count < 60 && above.Length - i < 400; i--)
        {
            if (Closes(above[i])) break;
            if (FieldLineRx.Match(above[i]) is { Success: true } m && m.Groups["n"].Value != current && m.Groups["r"].Value.Trim().Length > 0)
                near.Add((above.Length - i, (char.IsWhiteSpace(m.Groups["r"].Value[0]) ? " " : "") + Norm(m.Groups["r"].Value)));
            if (Opens(above[i])) break;
        }
        for (var i = 0; i < below.Length && i < 400; i++)
        {
            if (Opens(below[i])) break;
            if (FieldLineRx.Match(below[i]) is { Success: true } m && m.Groups["n"].Value != current && m.Groups["r"].Value.Trim().Length > 0)
                near.Add((i + 1, (char.IsWhiteSpace(m.Groups["r"].Value[0]) ? " " : "") + Norm(m.Groups["r"].Value)));
            if (Closes(below[i])) break;
        }
        if (near.Count == 0) return null;
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, r) in near.OrderBy(x => x.Dist).Take(30)) d[r] = d.GetValueOrDefault(r) + 1;
        return d;
    }

    /// <summary>"Theo chính file": các dòng khác trong file (trên + dưới) bắt đầu đúng như phần đã gõ của dòng → phần còn lại. Dòng trống: các dòng đứng sau
    /// 1 dòng giống dòng phía trên.</summary>
    /// <summary>Dòng của file anh em bắt đầu đúng như phần đã gõ → phần còn lại; dòng trống → dòng đứng sau 1 dòng giống dòng code phía trên (mỗi file 1 lần).
    /// Đếm theo số dòng khớp (mỗi file đếm các dòng khác nhau 1 lần).</summary>
    private static Dictionary<string, int>? SiblingDistribution(IReadOnlyList<string[]> siblings, string before, int nl, List<SourceLexer.Token> toks, string lineBefore)
    {
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        if (toks.Count == 0)
        {
            var prev = PrevCodeLineText(before, nl);
            if (prev is null) return null;
            foreach (var lines in siblings)
                for (var i = 0; i + 1 < lines.Length; i++)
                    if (lines[i] == prev && lines[i + 1].Length > 0) { d[lines[i + 1]] = d.GetValueOrDefault(lines[i + 1]) + 1; break; }
        }
        else
        {
            var typed = Norm(lineBefore);
            if (typed.Length < 2) return null;
            var endsSpace = char.IsWhiteSpace(lineBefore[^1]);
            foreach (var lines in siblings)
                foreach (var l in lines.Distinct())
                {
                    if (l.Length <= typed.Length || !l.StartsWith(typed, StringComparison.Ordinal)) continue;
                    var rest = l[typed.Length..];
                    if (endsSpace) { if (rest[0] != ' ') continue; rest = rest[1..]; }
                    if (rest.Length > 0) d[rest] = d.GetValueOrDefault(rest) + 1;
                }
        }
        return d.Count == 0 ? null : d;
    }

    /// <summary>Dòng code (có token) gần nhất phía trên con trỏ, đã gộp khoảng trắng; null nếu không có.</summary>
    private static string? PrevCodeLineText(string before, int nl)
    {
        var end = nl;
        while (end > 0)
        {
            var s = before.LastIndexOf('\n', end - 1);
            var l = before[(s + 1)..end];
            if (SourceLexer.Tokens(l).Count > 0) return Norm(l);
            if (s < 0) break;
            end = s;
        }
        return null;
    }

    private static readonly Regex FieldNameRx = new(@"<field\s+name=""([^""&]+)""", RegexOptions.Compiled);
    /// <summary>Tên field khai trong văn bản (chữ thường) — so độ giống giữa các file để tìm file anh em.</summary>
    public static HashSet<string> FieldNamesOf(string text) => FieldNameRx.Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();

    /// <summary>Chỉ dấu "=" của thuộc tính (ngay trước dấu nháy mở giá trị) — không đụng "=" trong câu SQL của check="ma_phi = ''".</summary>
    private static readonly Regex EqRx = new(@"(?<=[\w:-])\s*=\s*(?="")", RegexOptions.Compiled);

    private static Dictionary<string, int>? SameFile(string before, string after, int nl, List<SourceLexer.Token> toks, string lineBefore)
    {
        var typed = Norm(lineBefore);
        var above = (nl < 0 ? "" : before[..nl]).Split('\n');
        var below = after.Split('\n');
        if (FieldLineRx.IsMatch(lineBefore))
        {
            // dòng <field name="X": chỉ so với dòng cùng mục (<views> tham chiếu ≠ <fields> khai báo) — đi lên / xuống từ con trỏ, đổi mục khi qua <views> / </views>
            var here = SectionAt(before);
            var keepAbove = new bool[above.Length];
            var sec = here;
            for (var i = above.Length - 1; i >= 0; i--)
            {
                // đi lên qua </views> = vào khối views phía trên; qua <views> = ra khỏi nó (tương tự <template>)
                if (above[i].Contains("</template>")) sec = "template";
                else if (above[i].Contains("</views>")) sec = "views";
                keepAbove[i] = sec == here;
                if (Regex.IsMatch(above[i], @"<(views|template)\b")) sec = "fields";
            }
            var keepBelow = new bool[below.Length];
            sec = here;
            for (var i = 0; i < below.Length; i++)
            {
                if (Regex.IsMatch(below[i], @"<template\b")) sec = "template";
                else if (Regex.IsMatch(below[i], @"<views\b")) sec = "views";
                keepBelow[i] = sec == here;
                if (below[i].Contains("</views>") || below[i].Contains("</template>")) sec = "fields";
            }
            above = above.Where((_, i) => keepAbove[i]).ToArray();
            below = below.Where((_, i) => keepBelow[i]).ToArray();
        }
        var fieldLine = FieldLineRx.IsMatch(lineBefore);
        // dòng field: so khớp không phân biệt khoảng trắng quanh "=" (gõ name ="ma_td2" vẫn khớp dòng name="ma_td2" đã khai)
        string Key(string l) => fieldLine ? EqRx.Replace(Norm(l), "=") : Norm(l);
        var lines = above.Concat(below).Select(Key).ToList();
        if (fieldLine) typed = Key(lineBefore);
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        if (toks.Count == 0)
        {
            if (nl <= 0) return null;
            var prevNorm = Norm(before[(before.LastIndexOf('\n', nl - 1) + 1)..nl]);
            if (prevNorm.Length == 0) return null;
            for (var i = 0; i + 1 < lines.Count; i++)
                if (lines[i].Length > 0 && lines[i] == prevNorm && lines[i + 1].Length > 0) d[lines[i + 1]] = d.GetValueOrDefault(lines[i + 1]) + 1;
            return d.Count == 0 ? null : d;
        }
        if (typed.Length < 2) return null;
        // so theo chữ, phần đã gõ có thể dừng giữa token
        var endsSpace = char.IsWhiteSpace(lineBefore[^1]);
        foreach (var l in lines)
        {
            if (l.Length <= typed.Length || !l.StartsWith(typed, StringComparison.Ordinal)) continue;
            var rest = l[typed.Length..];
            if (endsSpace) { if (rest[0] != ' ') continue; rest = rest[1..]; }
            if (rest.Length == 0) continue;
            d[rest] = d.GetValueOrDefault(rest) + 1;
        }
        return d.Count == 0 ? null : d;
    }

    // ---------------------------------------------------------------- ghi / đọc

    public object ToJson() => new
    {
        models = Models.ToDictionary(x => x.Key, x => x.Value.ToJson()),
        fields = Fields,
        lines = Lines,
        nexts = Nexts,
        thresholds = Thresholds,
    };

    public static SourceGhost FromJson(JsonElement e)
    {
        var g = new SourceGhost();
        if (e.TryGetProperty("models", out var ms)) foreach (var p in ms.EnumerateObject()) g.Models[p.Name] = SourceTokenModel.FromJson(p.Value);
        if (e.TryGetProperty("fields", out var fs))
            foreach (var p in fs.EnumerateObject())
                g.Fields[p.Name] = p.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32(), StringComparer.Ordinal);
        foreach (var (prop, dst) in new[] { ("lines", g.Lines), ("nexts", g.Nexts) })
            if (e.TryGetProperty(prop, out var ls))
                foreach (var p in ls.EnumerateObject())
                    dst[p.Name] = p.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32(), StringComparer.Ordinal);
        if (e.TryGetProperty("thresholds", out var ts))
            foreach (var p in ts.EnumerateObject())
                g.Thresholds[p.Name] = new Quality(p.Value.GetProperty("MinShare").GetDouble(), p.Value.GetProperty("Precision").GetDouble(),
                    p.Value.GetProperty("Coverage").GetDouble(), p.Value.GetProperty("Tested").GetInt32());
        return g;
    }

    /// <summary>Gộp mẫu đi kèm với kiến thức học từ source dự án (ngưỡng giữ của mẫu đi kèm).</summary>
    public static SourceGhost Merge(SourceGhost shipped, SourceGhost project)
    {
        var g = new SourceGhost();
        foreach (var k in shipped.Models.Keys.Union(project.Models.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var parts = new[] { shipped.Models.GetValueOrDefault(k), project.Models.GetValueOrDefault(k) }.Where(x => x is not null).Cast<SourceTokenModel>().ToList();
            g.Models[k] = parts.Count == 1 ? parts[0] : SourceTokenModel.Merge(parts);
        }
        foreach (var (dst, srcs) in new[] { (g.Fields, new[] { shipped.Fields, project.Fields }), (g.Lines, new[] { shipped.Lines, project.Lines }), (g.Nexts, new[] { shipped.Nexts, project.Nexts }) })
            foreach (var src in srcs)
                foreach (var (k, d) in src)
                {
                    if (!dst.TryGetValue(k, out var t)) dst[k] = t = new(StringComparer.Ordinal);
                    foreach (var (r, n) in d) t[r] = t.GetValueOrDefault(r) + n;
                }
        foreach (var (k, q) in shipped.Thresholds) g.Thresholds[k] = q;
        return g;
    }
}
