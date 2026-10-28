using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using static Bcode.App.Services.FastGhostRules;

namespace Bcode.App.Services;

/// <summary>
/// "Huấn luyện" ma trận kiến thức ghost text (<see cref="FastGhostRules.KnowledgeBase"/>) từ procedure / function thật — giống 1 AI cục bộ nhưng
/// chỉ là bảng đếm, đọc được, không tốn token:
/// <list type="number">
/// <item>Rút: mỗi lời gọi EXEC / hàm Fast( / dòng lọc IF @x &lt;&gt; '' → đếm cách viết theo ô (routine, ngữ cảnh) và (routine).</item>
/// <item>Đo: chia đôi store; rút từ 1 nửa, giả lập "đang viết store mới" trên nửa kia (chỉ thấy phần phía trên con trỏ) và so gợi ý với code thật.</item>
/// <item>Hiệu chỉnh: mỗi loại chọn ngưỡng độ tin THẤP nhất mà độ chính xác trên nửa kia vẫn ≥ mục tiêu (mặc định 90%); không đạt thì tắt loại đó.</item>
/// <item>Rút lại từ toàn bộ store, bỏ các ô dưới ngưỡng (giữ ô "chặn" cần thiết) → ghi JSON gọn.</item>
/// </list>
/// Chỉ đọc <c>sys.sql_modules</c>; không ghi gì vào database.
/// </summary>
public static class FastGhostMiner
{
    /// <summary>1 chỗ đã thử gợi ý khi đo: loại chỗ (lọc / EXEC / hàm / song ngữ / REPLACE), gợi ý đến từ đâu, độ tin, đúng hay sai (null = không gợi ý).</summary>
    public sealed record Sample(string Site, string? Kind, double Share, bool? Hit, string Typed = "", string Got = "", string Expected = "", int Id = 0);

    public sealed record LearnResult(KnowledgeBase Knowledge, int Modules, int Train, int Test, IReadOnlyList<(string Site, int Sites, int Suggested, double Precision)> Report, double Precision, double Coverage)
    {
        public string Summary()
        {
            var sb = new StringBuilder($"Đã học từ {Modules} procedure/function (rút từ {Train}, đo trên {Test} store chưa dùng để rút).\n\n");
            foreach (var r in Report)
                sb.AppendLine($"• {r.Site}: {r.Sites} chỗ · có gợi ý {(r.Sites == 0 ? 0 : 100.0 * r.Suggested / r.Sites):0}% · đúng {100 * r.Precision:0.0}%");
            sb.AppendLine($"\nTỔNG: có gợi ý {100 * Coverage:0}% số chỗ · ĐÚNG {100 * Precision:0.0}% khi có gợi ý.");
            foreach (var (k, q) in Knowledge.Quality)
                sb.AppendLine($"  ngưỡng {k}: {(q.MinShare > 1 ? "tắt (không đạt mục tiêu)" : q.MinShare.ToString("0.00"))}");
            return sb.ToString();
        }
    }

    private static readonly Regex ExecRx = new(@"\bEXEC(?:UTE)?\s+(?:@\w+\s*=\s*)?(?:\[?\w+\]?\.)?\[?(?<n>[\w$]+)\]?\s+(?<a>\S.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FuncRx = new(@"(?<![\w$])(?:\[?\w+\]?\.)?(?<n>[\w]*\$[\w$]+|ff_\w+)\((?<a>[^()]*(?:\([^()]*\)[^()]*)*)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FilterRx = new(@"^\s*IF\s+@(?<v>\w+)\s*<>\s*''\s*(?<r>SET\s+@Key\b.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Chỗ để đo: phần "p" là cái người dùng đã gõ, phần "r" là cái code thật viết tiếp.</summary>
    private static readonly (string Site, string Kind, Regex Rx)[] Sites =
    {
        ("lọc IF @x <> ''", "filter", new(@"^(?<p>\s*IF\s+@\w+\s*<>\s*'')(?<r>\s*SET\s+@Key\b.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("EXEC routine", "call", new(@"^(?<p>.*?\bEXEC(?:UTE)?\s+(?:dbo\.)?(?:[\w]*\$[\w$]+|rs_\w+|fs_\w+)\s)(?<r>\S.*)$", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("hàm Fast(", "func", new(@"^(?<p>.*?\b(?:dbo\.)?(?:[\w]*\$[\w$]+|ff_\w+)\()(?<r>[^()]*(?:\([^()]*\)[^()]*)*\))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("song ngữ CASE", "bilingual", new(@"^(?<p>.*CASE\s+WHEN\s+@Language\s*=\s*'[vV]'\s+THEN\s+[\w.]+\s)(?<r>ELSE\s+[\w.]+\s+END(?:\s+AS\s+\w+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("REPLACE(RTRIM(@x),", "replace", new(@"^(?<p>.*REPLACE\s*\(\s*RTRIM\s*\(\s*@\w+\s*\)\s*,\s*)(?<r>'''',\s*''''''\))", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
        ("DECLARE @biến", "decl", new(@"^(?<p>\s*DECLARE\s+@\w+\s+(?:AS\s+)?)(?<r>(?!TABLE\b|CURSOR\b)[A-Za-z]+(?:\s*\([^)]*\))?)", RegexOptions.IgnoreCase | RegexOptions.Compiled)),
    };

    private static readonly string[] KnowledgeKinds = { "filter", "call", "func", "decl" };

    public static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim().TrimEnd(';').Trim().ToLowerInvariant();

    private static string StripComment(string l)
    {
        var inS = false;
        for (var i = 0; i < l.Length - 1; i++) { if (l[i] == '\'') inS = !inS; else if (!inS && l[i] == '-' && l[i + 1] == '-') return l[..i]; }
        return l;
    }

    /// <summary>Đọc định nghĩa procedure / function của database (chỉ SELECT sys.sql_modules).</summary>
    public static async Task<List<(string Name, string Def)>> ReadModulesAsync(SqlConnection conn, CancellationToken ct = default)
    {
        var mods = new List<(string, string)>();
        await using var cmd = new SqlCommand("SELECT o.name, m.definition FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.type IN ('P','FN','IF','TF') AND m.definition IS NOT NULL", conn) { CommandTimeout = 180 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) mods.Add((r.GetString(0), r.GetString(1).Replace("\r\n", "\n")));
        return mods;
    }

    /// <summary>Bước 1 — rút ma trận thô (chưa có ngưỡng, chưa tỉa).</summary>
    public static KnowledgeBase Mine(IEnumerable<(string Name, string Def)> src, string source)
    {
        var tabs = KnowledgeKinds.Append("line").ToDictionary(k => k, _ => new Dictionary<string, Dictionary<string, (string Text, int N, int Docs, int Last)>>(StringComparer.OrdinalIgnoreCase));
        var doc = 0;
        void Add(string kind, string key, string text)
        {
            text = text.Trim().TrimEnd(';').Trim();
            if (text.Length == 0 || text.Length > 400) return;
            var d = tabs[kind];
            if (!d.TryGetValue(key, out var v)) d[key] = v = new();
            var nk = Norm(text);
            v[nk] = v.TryGetValue(nk, out var o) ? (o.Text, o.N + 1, o.Docs + (o.Last == doc ? 0 : 1), doc) : (text, 1, 1, doc);
        }
        foreach (var (_, def) in src)
        {
            doc++;
            foreach (Match dm in DeclTypeRx.Matches(def)) Add("decl", dm.Groups["n"].Value, NormType(dm.Groups["t"].Value));
            foreach (var raw in def.Split('\n'))
            {
                var line = StripComment(raw);
                // ma trận đầu dòng: mỗi cách cắt sau 1..LineMaxTokens token đầu → phần còn lại của dòng
                var lt = Tokens(line);
                for (var j = 1; j <= Math.Min(LineMaxTokens, lt.Count - 1); j++)
                {
                    var (lkey, map) = LineKey(lt.Take(j));
                    Add("line", lkey, MapAliases(line[lt[j].Start..].TrimEnd(), map));
                }
                if (FilterRx.Match(line) is { Success: true } fm) { Add("filter", fm.Groups["v"].Value, CanonFilter(fm.Groups["r"].Value)); continue; }
                if (ExecRx.Match(line) is { Success: true } em)
                {
                    var g = em.Groups["a"];
                    var a = AbstractVars(g.Value, line[..g.Index]); var n = em.Groups["n"].Value;
                    Add("call", n, a); Add("call", n + "|" + ContextOf(line[..em.Index]), a);
                }
                foreach (Match f in FuncRx.Matches(line))
                {
                    var g = f.Groups["a"];
                    var a = AbstractVars(g.Value, line[..g.Index]) + ")"; var n = f.Groups["n"].Value;
                    Add("func", n, a); Add("func", n + "|" + ContextOf(line[..f.Index]), a);
                }
            }
        }

        Dictionary<string, Pattern> Best(Dictionary<string, Dictionary<string, (string Text, int N, int Docs, int Last)>> d, int minDocs = 1)
        {
            var res = new Dictionary<string, Pattern>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, v) in d)
            {
                var total = v.Values.Sum(x => x.N);
                if (total < 3) continue;
                // giữ cách đứng đầu + tối đa 4 cách khác gặp ≥ 2 lần và ≥ 5% — để khi cách đầu dùng biến store chưa có thì còn cách khác.
                var opts = v.Values.OrderByDescending(x => x.N).Where((x, i) => i == 0 || (i < 5 && x.N >= 2 && x.N * 20 >= total)).Select(x => new Option(x.Text, x.N, x.Docs)).ToList();
                // đầu dòng: cách viết chỉ có trong 1 store là thói quen của store đó ("theo chính file" lo phần này) — không đưa vào ma trận chung
                if (opts[0].Docs < minDocs) continue;
                res[key] = new Pattern(opts, total);
            }
            // Ô theo ngữ cảnh chỉ giữ khi khác ô chung (khác cách viết hoặc độ tin lệch rõ) — còn lại trùng thì thôi cho gọn.
            foreach (var key in res.Keys.Where(k => k.Contains('|')).ToList())
                if (res.TryGetValue(key[..key.IndexOf('|')], out var g) && Norm(g.Text) == Norm(res[key].Text) && Math.Abs(g.Share - res[key].Share) < 0.05)
                    res.Remove(key);
            return res;
        }
        return new KnowledgeBase { Filters = Best(tabs["filter"]), Calls = Best(tabs["call"]), Funcs = Best(tabs["func"]), Lines = Best(tabs["line"], 2), Decls = Best(tabs["decl"], 2), Source = source };
    }

    /// <summary>Bước 2 — giả lập viết store mới trên <paramref name="test"/> (chỉ thấy phần phía trên con trỏ), ghi lại mỗi chỗ: gợi ý gì, độ tin, đúng không.
    /// Gợi ý đúng = code thật viết tiếp bắt đầu đúng bằng gợi ý (không phân biệt hoa thường / khoảng trắng).</summary>
    public static List<Sample> Evaluate(IEnumerable<(string Name, string Def)> test, KnowledgeBase kb, bool applyThresholds = false, CancellationToken ct = default)
    {
        var samples = new List<Sample>();
        foreach (var (_, def) in test)
        {
            ct.ThrowIfCancellationRequested();
            var offset = 0;
            foreach (var raw in def.Split('\n'))
            {
                var line = StripComment(raw);
                foreach (var (site, kind, rx) in Sites)
                {
                    var m = rx.Match(line);
                    if (!m.Success) continue;
                    var prefix = m.Groups["p"].Value;
                    Ghost? g = null;
                    try { g = Explain(def[..offset] + prefix, offset + prefix.Length, null, kb, ignoreThresholds: !applyThresholds); } catch { }
                    // kiểu dữ liệu so không tính khoảng trắng ("NVARCHAR (4000)" = "nvarchar(4000)")
                    string Cmp(string s) => kind == "decl" ? Regex.Replace(s, @"\s+", "").ToLowerInvariant() : Norm(s);
                    samples.Add(g is null ? new Sample(site, null, 0, null) : new Sample(site, g.Kind, g.Share, Cmp(m.Groups["r"].Value).StartsWith(Cmp(g.Text)), prefix.Trim(), g.Text, m.Groups["r"].Value.Trim()));
                    break;
                }
                offset += raw.Length + 1;
            }
        }
        return samples;
    }

    public const string AnyLineSite = "dòng bất kỳ (giữa dòng)";

    /// <summary>Chừa thêm cho các lớp gợi ý chung ("same", "line") khi hiệu chỉnh.</summary>
    private const double GenericMargin = 0.01;

    /// <summary>Đo gợi ý chung trên "dòng bất kỳ": cứ 3 dòng code lấy 1 (tối đa 40 chỗ / store), cắt sau 1 token bất kỳ của dòng
    /// (giữ khoảng trắng như đang gõ), phần còn lại của dòng là đáp án. <paramref name="parts"/> = true: chấm riêng "theo chính file" và
    /// "ma trận đầu dòng", bỏ ngưỡng (để hiệu chỉnh); false: chấm đúng như khi gõ (Explain có ngưỡng).</summary>
    public static List<Sample> EvaluateAnyLine(IEnumerable<(string Name, string Def)> test, KnowledgeBase kb, bool parts, CancellationToken ct = default)
    {
        var samples = new List<Sample>();
        foreach (var (_, def) in test)
        {
            ct.ThrowIfCancellationRequested();
            var lines = def.Split('\n');
            var offsets = new int[lines.Length];
            for (var i = 1; i < lines.Length; i++) offsets[i] = offsets[i - 1] + lines[i - 1].Length + 1;
            var picked = Enumerable.Range(0, lines.Length).Where(i => i % 3 == 0).ToList();
            if (picked.Count > 40) picked = picked.Where((_, k) => k % (picked.Count / 40 + 1) == 0).ToList();
            foreach (var li in picked)
            {
                var line = StripComment(lines[li]);
                var toks = Tokens(line);
                if (toks.Count < 2) continue;
                var j = 1 + li * 7 % (toks.Count - 1);   // cắt ở vị trí bất kỳ trong dòng (đầu dòng: ma trận đầu dòng; sâu trong dòng: theo chính file)
                var typed = line[..toks[j].Start];
                var expected = line[toks[j].Start..].Trim();
                // "đang sửa store có sẵn": thấy cả code phía trên lẫn phía dưới dòng đang gõ (dòng thật đã bỏ)
                var rest = li + 1 < lines.Length ? def[offsets[li + 1]..] : "";
                var doc = def[..offsets[li]] + typed + "\n" + rest;
                if (GenericSplit(doc, offsets[li] + typed.Length) is not var (before, after)) continue;
                var id = samples.Count + 1;
                Sample S(Ghost? g, string kind) => g is null ? new Sample(AnyLineSite, kind, 0, null, Id: id)
                    : new Sample(AnyLineSite, g.Kind, g.Share, Norm(expected).StartsWith(Norm(g.Text)) && Norm(g.Text).Length > 0, typed.Trim(), g.Text, expected, id);
                try
                {
                    if (parts)
                    {
                        var m = SameCandidates(before, after);
                        var batch = SameLadder.Select(k => S(SameFile(m, k), SameKind(k))).Append(S(LineStart(before, kb, 0), "line")).ToList();
                        samples.AddRange(batch);
                    }
                    else samples.Add(S(Explain(doc, offsets[li] + typed.Length, null, kb), "-"));
                }
                catch { /* bỏ chỗ lỗi */ }
            }
        }
        return samples;
    }

    /// <summary>Ngưỡng thấp nhất cho "same" / "line" từ 1 lần chấm bỏ ngưỡng (không có gợi ý từng phần nên không phải chấm lại ở từng ngưỡng).</summary>
    public static KindQuality CalibrateOffline(List<Sample> samples, string kind, double target)
    {
        var tested = samples.Count(s => s.Kind == kind);
        var mine = samples.Where(s => s.Kind == kind && s.Hit is not null).ToList();
        for (var t = 0.5; t <= 1.0001; t += 0.025)
        {
            var shown = mine.Where(s => s.Share >= t - 1e-9).ToList();
            if (shown.Count < 30) break;
            var p = shown.Count(s => s.Hit == true) / (double)shown.Count;
            if (p >= target) return new KindQuality(Math.Round(t, 3), Math.Round(p, 3), Math.Round(shown.Count / (double)Math.Max(1, tested), 3), tested);
        }
        return new KindQuality(1.01, 0, 0, tested);
    }

    private static KnowledgeBase WithThresholds(KnowledgeBase kb, Func<string, double> minShare) => new()
    {
        Calls = kb.Calls, Funcs = kb.Funcs, Filters = kb.Filters, Lines = kb.Lines, Decls = kb.Decls, Source = kb.Source,
        Quality = KnowledgeKinds.Concat(SameLadder.Select(SameKind)).Append("line").ToDictionary(k => k, k => new KindQuality(minShare(k), 0, 0, 0), StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Bước 3 — mỗi loại: ngưỡng THẤP nhất (gợi ý dài / nhiều nhất) mà độ chính xác trên store chưa dùng để rút vẫn ≥ <paramref name="target"/>
    /// (cần ≥ 20 gợi ý để tin được); không đạt → 1.01 (tắt loại đó). Đo lại ở từng ngưỡng vì độ dài gợi ý từng phần phụ thuộc ngưỡng.</summary>
    public static Dictionary<string, KindQuality> Calibrate(List<(string Name, string Def)> test, KnowledgeBase trainKb, double target, CancellationToken ct = default)
    {
        var res = new Dictionary<string, KindQuality>(StringComparer.OrdinalIgnoreCase);
        var pending = KnowledgeKinds.ToList();
        for (var t = 0.5; t <= 1.0001 && pending.Count > 0; t += 0.05)
        {
            var tt = Math.Round(t, 2);
            var samples = Evaluate(test, WithThresholds(trainKb, k => KnowledgeKinds.Contains(k) ? tt : trainKb.MinShareOf(k)), true, ct);
            foreach (var kind in pending.ToList())
            {
                var site = Sites.First(s => s.Kind == kind).Site;
                var tested = samples.Count(s => s.Site == site);
                var shown = samples.Where(s => s.Kind == kind).ToList();
                if (shown.Count < 20) { res[kind] = new KindQuality(1.01, 0, 0, tested); pending.Remove(kind); continue; }
                var p = shown.Count(s => s.Hit == true) / (double)shown.Count;
                if (p >= target) { res[kind] = new KindQuality(tt, Math.Round(p, 3), Math.Round(shown.Count / (double)Math.Max(1, tested), 3), tested); pending.Remove(kind); }
            }
        }
        foreach (var kind in pending) res[kind] = new KindQuality(1.01, 0, 0, 0);
        return res;
    }

    /// <summary>Bước 4 — bỏ ô không bao giờ đạt ngưỡng: kể cả phần đầu chung của mọi cách đã lưu cũng chỉ được (tổng đã lưu) / (tổng + 1) &lt; ngưỡng.
    /// Ô theo ngữ cảnh: giữ khi tự đạt hoặc ô chung còn giữ (để không rơi về ô chung ở ngữ cảnh mà cách viết khác hẳn).</summary>
    public static Dictionary<string, Pattern> Prune(Dictionary<string, Pattern> d, double minShare)
    {
        bool Reach(Pattern p) => p.Options.Sum(o => o.N) / (double)(p.Count + 1) >= minShare;
        var res = new Dictionary<string, Pattern>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, p) in d.Where(x => !x.Key.Contains('|') && Reach(x.Value))) res[k] = p;
        foreach (var (k, p) in d.Where(x => x.Key.Contains('|') && (Reach(x.Value) || res.ContainsKey(x.Key[..x.Key.IndexOf('|')])))) res[k] = p;
        return res;
    }

    /// <summary>Cả 4 bước: rút → đo → hiệu chỉnh ngưỡng → rút lại từ toàn bộ và tỉa.</summary>
    public static LearnResult Learn(List<(string Name, string Def)> mods, string source, double target = 0.9, CancellationToken ct = default)
    {
        // chia ổn định theo tên (GetHashCode của string đổi theo lần chạy)
        var train = mods.Where(m => m.Name.Sum(c => c) % 2 == 0).ToList();
        var test = mods.Where(m => m.Name.Sum(c => c) % 2 == 1).ToList();
        var trainKb = Mine(train, "train");
        // "theo chính file" + "ma trận đầu dòng" hiệu chỉnh trước (Explain của các loại kia rơi xuống 2 loại này khi không có gợi ý)
        var parts = EvaluateAnyLine(test, trainKb, true, ct);
        // mỗi bậc độ dài của "theo chính file" 1 ngưỡng riêng
        // (hiệu chỉnh trên tập chấm riêng từng lớp, khi gõ thì các lớp chồng nhau → chừa 1 điểm cho chắc đạt mục tiêu)
        var generic = SameLadder.ToDictionary(SameKind, k => CalibrateOffline(parts, SameKind(k), target + GenericMargin), StringComparer.OrdinalIgnoreCase);
        // "line" chỉ chạy khi không bậc "same" nào gợi ý → hiệu chỉnh trên đúng các chỗ đó
        var sameShown = parts.Where(s => s.Kind is { } k && k.StartsWith("same") && s.Hit is not null && s.Share >= generic[k].MinShare).Select(s => s.Id).ToHashSet();
        generic["line"] = CalibrateOffline(parts.Where(s => s.Kind == "line" && !sameShown.Contains(s.Id)).ToList(), "line", target + GenericMargin);
        var quality = Calibrate(test, WithThresholds(trainKb, k => generic.TryGetValue(k, out var q) ? q.MinShare : 1.01), target, ct);
        foreach (var (k, q) in generic) quality[k] = q;
        var finalKb = WithThresholds(trainKb, k => quality[k].MinShare);
        var samples = Evaluate(test, finalKb, true, ct).Concat(EvaluateAnyLine(test, finalKb, false, ct)).ToList();

        var report = new List<(string Site, int Sites, int Suggested, double Precision)>();
        var rules = new Dictionary<string, KindQuality>(StringComparer.OrdinalIgnoreCase);
        foreach (var (site, kind, _) in Sites.Append((AnyLineSite, "any", null!)))
        {
            var all = samples.Where(s => s.Site == site).ToList();
            var shown = all.Where(s => s.Hit is not null).ToList();
            var p = shown.Count == 0 ? 0 : shown.Count(s => s.Hit == true) / (double)shown.Count;
            report.Add((site, all.Count, shown.Count, p));
            if (!KnowledgeKinds.Contains(kind)) rules[kind] = new KindQuality(0, Math.Round(p, 3), Math.Round(shown.Count / (double)Math.Max(1, all.Count), 3), all.Count);
        }
        var totalShown = samples.Where(s => s.Hit is not null).ToList();

        var full = Mine(mods, source);
        var kb = new KnowledgeBase
        {
            Calls = Prune(full.Calls, quality["call"].MinShare), Funcs = Prune(full.Funcs, quality["func"].MinShare), Filters = Prune(full.Filters, quality["filter"].MinShare),
            Lines = Prune(full.Lines, quality["line"].MinShare), Decls = Prune(full.Decls, quality["decl"].MinShare),
            Quality = quality, Rules = rules, Source = source,
        };
        return new LearnResult(kb, mods.Count, train.Count, test.Count, report,
            totalShown.Count == 0 ? 0 : totalShown.Count(s => s.Hit == true) / (double)totalShown.Count, totalShown.Count / (double)Math.Max(1, samples.Count));
    }

    /// <summary>Ghi ma trận ra JSON: mỗi loại 1 khối {ngưỡng, độ chính xác, entries: {khoá: [tổng số lần, [cách viết, số lần] …]}}.</summary>
    public static void Save(KnowledgeBase kb, string path, double target = 0.9)
    {
        object Kind(string k) => new
        {
            minShare = kb.MinShareOf(k),
            precision = kb.Quality.TryGetValue(k, out var q) ? q.Precision : 0,
            coverage = q?.Coverage ?? 0,
            tested = q?.Tested ?? 0,
            entries = kb.Table(k).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key.ToLowerInvariant(), x => new object[] { x.Value.Count }.Concat(x.Value.Options.Select(o => (object)new object[] { o.Text, o.N })).ToArray()),
        };
        var json = JsonSerializer.Serialize(new
        {
            version = 2,
            source = kb.Source,
            built = DateTime.Now.ToString("yyyy-MM-dd"),
            target,
            note = "Ma trận ghost text Fast: loại × khoá ('routine' hoặc 'routine|ngữ cảnh', biến lọc) → [tổng số lần, [cách viết, số lần] …] (nhiều → ít). "
                 + "Khi gõ: bỏ cách dùng biến store chưa khai báo, độ tin = số lần / (tổng các cách còn lại + phần đuôi + 1); chỉ gợi ý khi ≥ minShare của loại "
                 + "(hiệu chỉnh để precision đo trên store chưa dùng để rút ≥ target). Ô không có cách viết = ô chặn. "
                 + "{a} = alias bảng, {lhs} = biến được gán trên dòng, {if} = biến của IF cùng dòng; dòng lọc lưu dạng chuẩn rồi đổi theo văn phong store.",
            rules = kb.Rules.ToDictionary(x => x.Key, x => new { precision = x.Value.Precision, coverage = x.Value.Coverage, tested = x.Value.Tested }),
            kinds = new[] { "filter", "call", "func", "decl", "line" }.Concat(SameLadder.Select(SameKind)).ToDictionary(k => k, Kind),
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
    }

    /// <summary>Học từ database đang kết nối (chỉ đọc) → ghi ma trận riêng của database đó; lần gõ sau tra ma trận này trước ma trận đi kèm.</summary>
    public static async Task<LearnResult> LearnFromDatabaseAsync(string dbKey, Func<SqlConnection> connect, CancellationToken ct = default)
    {
        List<(string Name, string Def)> mods;
        await using (var conn = connect())
        {
            await conn.OpenAsync(ct);
            mods = await ReadModulesAsync(conn, ct);
        }
        var res = await Task.Run(() => Learn(mods, $"{dbKey}: {mods.Count} procedure/function", ct: ct), ct);
        Save(res.Knowledge, LearnedPath(dbKey));
        MergeShared();
        return res;
    }

    /// <summary>Như <see cref="LearnFromDatabaseAsync"/> nhưng với danh sách store đã đọc sẵn (Hỏi Fast → "Học dự án này" đọc 1 lần dùng cho cả 2).</summary>
    public static LearnResult LearnFromModules(string dbKey, List<(string Name, string Def)> mods, Action<string>? progress = null, CancellationToken ct = default)
    {
        var res = Learn(mods, $"{dbKey}: {mods.Count} procedure/function", ct: ct);
        Save(res.Knowledge, LearnedPath(dbKey));
        MergeShared();
        return res;
    }

    /// <summary>Điều chỉnh do người dùng xác nhận ở Hỏi Fast: ô (loại, khoá) cách viết <c>Text</c> cộng / trừ <c>Delta</c> lần.</summary>
    /// <param name="Prefer">Cách viết người dùng gõ lại khi bấm ✘ — luôn được dùng trước (lớp sửa của người dùng, xem <see cref="FastGhostRules.KnowledgeFor"/>).</param>
    public sealed record Adjustment(string Kind, string Key, string Text, int Delta, bool Prefer = false);

    /// <summary>Nguồn điều chỉnh: mặc định đọc file xác nhận của Hỏi Fast (fastaskeedback.json).</summary>
    public static Func<IEnumerable<Adjustment>>? AdjustmentsSource { get; set; } = () => FastAskFeedback.Instance.Adjustments();

    /// <summary>Gộp mọi ma trận dự án đã học trên máy (%AppData%\Bcode\ghost\*.json) + điều chỉnh xác nhận ✔/✘ thành ma trận chung
    /// (<see cref="SharedPath"/>): cộng số lần của cùng cách viết; ngưỡng mỗi loại lấy mức chặt nhất trong các dự án (chắc ăn). Dự án mới chưa học
    /// vẫn dùng được kiến thức của các dự án trước.</summary>
    public static void MergeShared()
    {
        var dir = Path.GetDirectoryName(SharedPath)!;
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json").Where(f => !Path.GetFileName(f).StartsWith("_")).ToList() : new List<string>();
        var adjustments = AdjustmentsSource?.Invoke().ToList() ?? new List<Adjustment>();
        if (files.Count == 0 && adjustments.Count == 0) { try { File.Delete(SharedPath); } catch { } return; }

        string[] kinds = { "call", "func", "filter", "decl", "line" };
        var cells = kinds.ToDictionary(k => k, _ => new Dictionary<string, (int Count, Dictionary<string, (string Text, int N)> Opts)>(StringComparer.OrdinalIgnoreCase));
        var quality = new Dictionary<string, KindQuality>(StringComparer.OrdinalIgnoreCase);
        var projects = 0;
        foreach (var f in files)
        {
            var kb = LoadKnowledgeFile(f);
            if (kb.Quality.Count == 0) continue;
            projects++;
            foreach (var (k, q) in kb.Quality)
                if (!quality.TryGetValue(k, out var old) || (q.MinShare <= 1 && (old.MinShare > 1 || q.MinShare > old.MinShare))) quality[k] = q;
            foreach (var kind in kinds)
                foreach (var (key, p) in kb.Table(kind))
                {
                    (int Count, Dictionary<string, (string Text, int N)> Opts) c = cells[kind].TryGetValue(key, out var v) ? v : (0, new(StringComparer.Ordinal));
                    foreach (var o in p.Options) { var n = Norm(o.Text); c.Opts[n] = c.Opts.TryGetValue(n, out var e) ? (e.Text, e.N + o.N) : (o.Text, o.N); }
                    cells[kind][key] = (c.Count + p.Count, c.Opts);
                }
        }
        foreach (var a in adjustments.Where(a => cells.ContainsKey(a.Kind)))
        {
            (int Count, Dictionary<string, (string Text, int N)> Opts) c = cells[a.Kind].TryGetValue(a.Key, out var v) ? v : (0, new(StringComparer.Ordinal));
            var n = Norm(a.Text);
            var cur = c.Opts.TryGetValue(n, out var e) ? e : (a.Text, 0);
            c.Opts[n] = (cur.Item1, Math.Max(0, cur.Item2 + a.Delta));
            cells[a.Kind][a.Key] = (Math.Max(0, c.Count + Math.Max(0, a.Delta)), c.Opts);
        }
        Dictionary<string, Pattern> Build(string kind) => cells[kind].Where(x => x.Value.Opts.Values.Any(o => o.N > 0)).ToDictionary(x => x.Key, x =>
        {
            var opts = x.Value.Opts.Values.Where(o => o.N > 0).OrderByDescending(o => o.N).Take(5).Select(o => new Option(o.Text, o.N)).ToList();
            return new Pattern(opts, Math.Max(x.Value.Count, opts.Sum(o => o.N)));
        }, StringComparer.OrdinalIgnoreCase);
        var shared = new KnowledgeBase
        {
            Calls = Build("call"), Funcs = Build("func"), Filters = Build("filter"), Decls = Build("decl"), Lines = Build("line"),
            Quality = quality, Source = $"Chung {projects} dự án đã học trên máy này" + (adjustments.Count > 0 ? $" + {adjustments.Count} xác nhận ở Hỏi Fast" : ""),
        };
        Save(shared, SharedPath);
    }
}
