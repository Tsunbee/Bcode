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
        return Thresholds.TryGetValue(kind + "@" + area + sfx, out var q) ? q.MinShare : MinShareOf(kind + sfx);
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

    private static readonly Regex FieldOpenRx = new(@"<field\s+name=""(?<n>[^""]+)""[^>]*?(?<sc>/?)>|<field\s+name=""(?<n>[^""]+)""[^>]*$", RegexOptions.Compiled | RegexOptions.RightToLeft);

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
            foreach (var (key, rest) in FieldSites(text))
            {
                if (!counts.TryGetValue(key, out var d)) counts[key] = d = new(StringComparer.Ordinal);
                d[rest] = d.GetValueOrDefault(rest) + 1;
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
    public Ghost? Explain(string before, string after, string kind, string? region, bool longMode, bool useLocal = true)
    {
        var cands = Candidates(before, after, kind, region, useLocal);
        var area = AreaOf(kind, region, before[(before.LastIndexOf('\n') + 1)..].Trim().Length == 0);
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
    public List<(string Kind, string Text, double Share, Func<double, string> Cut)> Candidates(string before, string after, string kind, string? region, bool useLocal = true)
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

        // same
        var same = SameFile(before, after, nl, toks, lineBefore);
        if (same is not null) AddDistribution(res, "same", same, "", EndsSpace(lineBefore));

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

    private static void AddDistribution(List<(string, string, double, Func<double, string>)> res, string kind, Dictionary<string, int> raw, string partial, bool endsSpace)
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
        double denom = (partial.Length > 0 ? opts.Sum(x => x.Value) : total) + 1;
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

    /// <summary>"Theo chính file": các dòng khác trong file (trên + dưới) bắt đầu đúng như phần đã gõ của dòng → phần còn lại. Dòng trống: các dòng đứng sau
    /// 1 dòng giống dòng phía trên.</summary>
    private static Dictionary<string, int>? SameFile(string before, string after, int nl, List<SourceLexer.Token> toks, string lineBefore)
    {
        var typed = Norm(lineBefore);
        var lines = (nl < 0 ? "" : before[..nl]).Split('\n').Concat(after.Split('\n')).Select(l => Norm(l)).ToList();
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
