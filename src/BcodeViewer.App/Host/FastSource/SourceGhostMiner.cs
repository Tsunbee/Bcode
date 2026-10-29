namespace BcodeViewer.App.Host.FastSource;

/// <summary>
/// Đo và hiệu chỉnh ghost text Fast của BcodeViewer — cùng cách đo với <c>Bcode.App/Services/FastGhostMiner.cs</c>: rút từ một nửa file, giả lập
/// gõ trên nửa còn lại (cứ 3 dòng lấy 1, tối đa 40 chỗ / file, cắt sau 1 token bất kỳ của dòng; thấy cả phía trên lẫn phía dưới dòng đang gõ)
/// và dòng trống (vừa Enter, chỉ thấy phía trên). Gợi ý đúng = phần thật của dòng bắt đầu đúng bằng gợi ý (gộp khoảng trắng, phân biệt hoa thường).
/// </summary>
public static class SourceGhostMiner
{
    /// <summary>1 chỗ đo: vùng (xml / js / sql / aspx), loại chỗ (line / next), các nguồn gợi ý (kèm hàm cắt), đáp án và vùng ngưỡng
    /// (<see cref="SourceGhost.AreaFor"/> — giống hệt lúc gõ thật).</summary>
    public sealed record Site(string File, string Region, string Where, string Typed, string Expected,
        List<(string Kind, string Text, double Share, Func<double, string> Cut)> Cands, string Area = "");

    public sealed record Score(int Sites, int Shown, int Hit)
    {
        public double Coverage => Sites == 0 ? 0 : Shown / (double)Sites;
        public double Precision => Shown == 0 ? 0 : Hit / (double)Shown;
        public override string ToString() => $"{Sites} chỗ, có gợi ý {Coverage:P1}, đúng {Precision:P1} ({Hit}/{Shown})";
    }

    public static bool IsHit(string expected, string ghost) =>
        SourceGhost.Norm(ghost).Length > 0 && SourceGhost.Norm(expected).StartsWith(SourceGhost.Norm(ghost), StringComparison.Ordinal);

    /// <summary>Vùng của từng dòng: aspx; với xml: js (trong &lt;script&gt;), sql (trong CDATA ngoài script), xml.</summary>
    public static string[] Regions(string text, string kind)
    {
        var lines = text.Split('\n');
        var res = new string[lines.Length];
        bool inScript = false, inCdata = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var l = lines[i];
            if (l.Contains("<script", StringComparison.OrdinalIgnoreCase)) inScript = true;
            var cdOpen = l.LastIndexOf("<![CDATA[", StringComparison.Ordinal);
            var cdClose = l.LastIndexOf("]]>", StringComparison.Ordinal);
            var startCdata = inCdata;
            if (cdOpen >= 0 || cdClose >= 0) inCdata = cdOpen > cdClose;
            res[i] = kind == "aspx" ? (inScript ? "js" : "aspx") : kind == "js" ? "js" : inScript ? "js" : (startCdata || inCdata) && !l.TrimStart().StartsWith('<') ? "sql" : "xml";
            if (l.Contains("</script>", StringComparison.OrdinalIgnoreCase)) inScript = false;
        }
        return res;
    }

    /// <summary>Dựng các chỗ đo trên file thử (gồm danh sách nguồn gợi ý chưa áp ngưỡng — để hiệu chỉnh 1 lần, chấm ở mọi ngưỡng).</summary>
    public static List<Site> Sites(IEnumerable<(string Path, string Text)> test, SourceGhost g, bool useLocal = true, int perFile = 40)
    {
        var res = new List<Site>();
        foreach (var (path, raw) in test)
        {
            var kind = SourceGhost.KindOfPath(path);
            if (kind == "") continue;
            var def = raw.Replace("\r\n", "\n");
            var lines = def.Split('\n');
            var offsets = new int[lines.Length];
            for (var i = 1; i < lines.Length; i++) offsets[i] = offsets[i - 1] + lines[i - 1].Length + 1;
            var regions = Regions(def, kind);

            // giữa dòng
            var picked = Enumerable.Range(0, lines.Length).Where(i => i % 3 == 0).ToList();
            if (picked.Count > perFile) picked = picked.Where((_, k) => k % (picked.Count / perFile + 1) == 0).ToList();
            foreach (var li in picked)
            {
                var line = lines[li];
                var toks = SourceLexer.Tokens(line);
                if (toks.Count < 2) continue;
                var j = 1 + li * 7 % (toks.Count - 1);
                var typed = line[..toks[j].Start];
                var expected = line[toks[j].Start..].Trim();
                var before = def[..offsets[li]] + typed;
                var after = li + 1 < lines.Length ? def[offsets[li + 1]..] : "";
                List<(string, string, double, Func<double, string>)> c;
                var root = SourceGhost.RootOf(path, def);
                try { c = g.Candidates(before, after, kind, regions[li], useLocal, root); } catch { continue; }
                res.Add(new Site(path, regions[li], "line", typed.Trim(), expected, c, SourceGhost.AreaFor(kind, regions[li], root, typed)));
            }

            // dòng trống (câu tiếp theo): chỉ thấy phía trên
            var code = Enumerable.Range(1, Math.Max(0, lines.Length - 1)).Where(i => lines[i].Trim().Length > 0).ToList();
            var nexts = code.Where((_, k) => k % 3 == 1).ToList();
            if (nexts.Count > perFile) nexts = nexts.Where((_, k) => k % (nexts.Count / perFile + 1) == 0).ToList();
            foreach (var li in nexts)
            {
                var ind = lines[li][..(lines[li].Length - lines[li].TrimStart().Length)];
                var before = def[..offsets[li]] + ind;
                List<(string, string, double, Func<double, string>)> c;
                var root = SourceGhost.RootOf(path, def);
                try { c = g.Candidates(before, "", kind, regions[li], useLocal, root); } catch { continue; }
                res.Add(new Site(path, regions[li], "next", "", lines[li].Trim(), c, SourceGhost.AreaFor(kind, regions[li], root, ind)));
            }
        }
        return res;
    }

    /// <summary>Chấm như khi gõ: mỗi chỗ lấy gợi ý dài nhất qua ngưỡng của loại đó.</summary>
    public static Score Grade(IEnumerable<Site> sites, Func<string, double> threshold, Func<string, bool>? kindOn = null)
    {
        int n = 0, shown = 0, hit = 0;
        foreach (var s in sites)
        {
            n++;
            var best = Pick(s, threshold, kindOn);
            if (best.Length == 0) continue;
            shown++;
            if (IsHit(s.Expected, best)) hit++;
        }
        return new Score(n, shown, hit);
    }

    public static string Pick(Site s, Func<string, double> threshold, Func<string, bool>? kindOn = null)
    {
        var best = "";
        foreach (var (k, _, _, cut) in s.Cands)
        {
            if (kindOn is not null && !kindOn(k)) continue;
            var t = cut(threshold(k));
            if (SourceGhost.Norm(t).Length > SourceGhost.Norm(best).Length) best = t;
        }
        return best;
    }

    /// <summary>Ngưỡng thấp nhất của từng loại để riêng loại đó đúng ≥ <paramref name="target"/> (cần ≥ 30 gợi ý), rồi nâng dần loại kém nhất tới khi
    /// gộp các loại (gợi ý dài nhất) cũng ≥ mục tiêu.</summary>
    public static Dictionary<string, SourceGhost.Quality> Calibrate(List<Site> sites, double target, string suffix = "")
    {
        var kinds = new[] { "field", "same", "tok" };
        var th = new Dictionary<string, double>();
        foreach (var k in kinds)
        {
            th[k] = 1.01;
            for (var t = 0.05; t <= 1.0001; t += 0.025)
            {
                var sc = Grade(sites, _ => t, x => x == k);
                if (sc.Shown < 30) break;
                if (sc.Precision >= target) { th[k] = Math.Round(t, 3); break; }
            }
        }
        // gộp: lấy gợi ý dài nhất có thể làm giảm độ đúng → nâng ngưỡng loại đang đúng thấp nhất trong các chỗ được chọn
        for (var guard = 0; guard < 60; guard++)
        {
            var all = Grade(sites, k => th.GetValueOrDefault(k, 1.01));
            if (all.Precision >= target || all.Shown == 0) break;
            var worst = kinds.Where(k => th[k] <= 1)
                .Select(k => (k, sc: GradeChosen(sites, th, k))).Where(x => x.sc.Shown > 0).OrderBy(x => x.sc.Precision).FirstOrDefault();
            if (worst.k is null) break;
            th[worst.k] = Math.Min(1.01, Math.Round(th[worst.k] + 0.025, 3));
        }
        return kinds.ToDictionary(k => k + suffix, k =>
        {
            var sc = Grade(sites, _ => th[k], x => x == k);
            return new SourceGhost.Quality(th[k], Math.Round(sc.Precision, 3), Math.Round(sc.Coverage, 3), sc.Sites);
        });
    }

    /// <summary>Chấm riêng các chỗ mà gợi ý được chọn (gộp) đến từ loại <paramref name="kind"/>.</summary>
    private static Score GradeChosen(List<Site> sites, Dictionary<string, double> th, string kind)
    {
        int n = 0, hit = 0;
        foreach (var s in sites)
        {
            string best = "", from = "";
            foreach (var (k, _, _, cut) in s.Cands)
            {
                var t = cut(th.GetValueOrDefault(k, 1.01));
                if (SourceGhost.Norm(t).Length > SourceGhost.Norm(best).Length) { best = t; from = k; }
            }
            if (from != kind) continue;
            n++;
            if (IsHit(s.Expected, best)) hit++;
        }
        return new Score(n, n, hit);
    }
}
