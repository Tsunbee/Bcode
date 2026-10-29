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
/// </list>
/// Mỗi loại có ngưỡng riêng hiệu chỉnh trên nửa file mẫu không dùng để rút (ngắn ≥ 90%, dài ≥ 60%); gộp = lấy gợi ý dài nhất qua ngưỡng.
/// </summary>
public sealed class SourceGhost
{
    public sealed record Ghost(string Text, string Kind, double Share);
    public sealed record Quality(double MinShare, double Precision, double Coverage, int Tested);

    /// <summary>Mô hình token theo loại source: "xml" (controller, .ent, include .txt), "aspx" (aspx / ascx / master / html), "js" (đoạn script).</summary>
    public Dictionary<string, SourceTokenModel> Models { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>"F:&lt;field&gt;|&lt;đầu dòng&gt;" → phần còn lại của dòng → số lần.</summary>
    public Dictionary<string, Dictionary<string, int>> Fields { get; } = new(StringComparer.Ordinal);
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
        return area;
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
        return g;
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
    public Ghost? Explain(string before, string after, string kind, string? region, bool longMode, bool useLocal = true, string root = "")
    {
        var cands = Candidates(before, after, kind, region, useLocal, root);
        var area = AreaFor(kind, region, root, before[(before.LastIndexOf('\n') + 1)..]);
        Ghost? best = null;
        foreach (var (k, text, share, cut) in cands)
        {
            var t = cut(MinShareOf(k, area, longMode));
            if (t.Length == 0) continue;
            if (best is null || t.Length > best.Text.Length) best = new Ghost(t, k, share);
        }
        return best;
    }

    /// <summary>Từng nguồn gợi ý: (loại, gợi ý đầy đủ, độ tin, cắt theo ngưỡng).</summary>
    public List<(string Kind, string Text, double Share, Func<double, string> Cut)> Candidates(string before, string after, string kind, string? region, bool useLocal = true, string root = "")
    {
        var res = new List<(string, string, double, Func<double, string>)>();
        var nl = before.LastIndexOf('\n');
        var lineBefore = before[(nl + 1)..];
        var toks = SourceLexer.Tokens(lineBefore);
        // đang gõ dở 1 từ / chuỗi thì phần đã gõ của token cuối là tiền tố bắt buộc
        var typed = Norm(lineBefore);

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
            if (Fields.TryGetValue(key, out var d)) AddDistribution(res, "field", d, partial, EndsSpace(lineBefore));
        }

        // dòng <field name="X" … (gõ xong tên field): phần thuộc tính còn lại theo (1) cách khai field X hay gặp ở loại file này,
        // (2) khai báo đặc thù của CHÍNH file — các dòng <field> gần nhất (import: column="." resource="master"/>), (3) cách khai chung của loại file.
        if (kind == "xml" && FieldLineRx.Match(lineBefore) is { Success: true } fl && !fl.Groups["r"].Value.Contains('>'))
        {
            var name = fl.Groups["n"].Value;
            var partial = Regex.Replace(fl.Groups["r"].Value.TrimStart(), @"\s+", " ");
            var endsSpace = EndsSpace(lineBefore);
            var sec = SectionAt(before);
            if (Fields.TryGetValue("N:" + root + ":" + sec + "|" + name, out var byName)) AddDistribution(res, "field", byName, partial, endsSpace);
            if (FileFieldShapes(before, after, nl, name) is { } shapes) AddDistribution(res, "same", shapes, partial, endsSpace);
            if (Fields.TryGetValue("N:" + root + ":" + sec + "|*", out var generic)) AddDistribution(res, "field", generic, partial, endsSpace);
        }

        // same
        var same = SameFile(before, after, nl, toks, lineBefore);
        // dòng <field name="X" mà X đã khai trong chính file (cùng mục): chép đúng cách khai đó — 1 lần gặp đã đủ chắc (làm trơn 0.25 thay 1)
        if (same is not null) AddDistribution(res, "same", same, "", EndsSpace(lineBefore), FieldLineRx.IsMatch(lineBefore) ? 0.25 : 1);

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
        return res;
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

    private static void AddDistribution(List<(string, string, double, Func<double, string>)> res, string kind, Dictionary<string, int> raw, string partial, bool endsSpace, double smoothing = 1)
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
        if (opts.Count == 0) return;
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
        thresholds = Thresholds,
    };

    public static SourceGhost FromJson(JsonElement e)
    {
        var g = new SourceGhost();
        if (e.TryGetProperty("models", out var ms)) foreach (var p in ms.EnumerateObject()) g.Models[p.Name] = SourceTokenModel.FromJson(p.Value);
        if (e.TryGetProperty("fields", out var fs))
            foreach (var p in fs.EnumerateObject())
                g.Fields[p.Name] = p.Value.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetInt32(), StringComparer.Ordinal);
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
        foreach (var src in new[] { shipped.Fields, project.Fields })
            foreach (var (k, d) in src)
            {
                if (!g.Fields.TryGetValue(k, out var t)) g.Fields[k] = t = new(StringComparer.Ordinal);
                foreach (var (r, n) in d) t[r] = t.GetValueOrDefault(r) + n;
            }
        foreach (var (k, q) in shipped.Thresholds) g.Thresholds[k] = q;
        return g;
    }
}
