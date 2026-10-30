namespace BcodeViewer.App.Host.FastSource;

/// <summary>
/// Đo và hiệu chỉnh ghost text Fast của BcodeViewer — cùng cách đo với <c>Bcode.App/Services/FastGhostMiner.cs</c>: rút từ một nửa file, giả lập
/// gõ trên nửa còn lại (cứ 3 dòng lấy 1, tối đa 40 chỗ / file, cắt sau 1 token bất kỳ của dòng; thấy cả phía trên lẫn phía dưới dòng đang gõ),
/// giữa 1 tên đang gõ dở (<c>f.setIt|</c>) và dòng trống (vừa Enter, chỉ thấy phía trên). Gợi ý đúng = phần thật của dòng bắt đầu đúng bằng gợi ý (gộp khoảng trắng, phân biệt hoa thường).
/// Chấm / hiệu chỉnh chọn gợi ý đúng như lúc gõ (<see cref="SourceGhost.Choose"/>: "close" thắng, rồi độ tin cao nhất).
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
    /// <param name="siblingsFor">Nếu có: (đường dẫn, nội dung) của file thử → dòng các file anh em (nguồn "sib", như lúc gõ có dự án).</param>
    public static List<Site> Sites(IEnumerable<(string Path, string Text)> test, SourceGhost g, bool useLocal = true, int perFile = 40,
        Func<string, string, IReadOnlyList<string[]>?>? siblingsFor = null)
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
            var sibs = siblingsFor?.Invoke(path, def);

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
                try { c = g.Candidates(before, after, kind, regions[li], useLocal, root, siblings: sibs); } catch { continue; }
                res.Add(new Site(path, regions[li], "line", typed.Trim(), expected, c, SourceGhost.AreaFor(kind, regions[li], root, typed)));
            }

            // gõ dở 1 tên (f.setIt|, function onChange$Voucher$Ta|): cắt giữa 1 tên ≥ 4 chữ ngoài chuỗi, đã gõ 2 … dài−1 chữ
            var mids = Enumerable.Range(0, lines.Length).Where(i => i % 3 == 2).ToList();
            if (mids.Count > perFile) mids = mids.Where((_, k) => k % (mids.Count / perFile + 1) == 0).ToList();
            foreach (var li in mids)
            {
                var line = lines[li];
                var names = SourceLexer.Tokens(line).Where(t => t.Text.Length >= 4 && SourceLexer.IsWordStart(t.Text[0]) && t.Text[0] != '@').ToList();
                if (names.Count == 0) continue;
                var t = names[li * 7 % names.Count];
                var cut = t.Start + 2 + li * 5 % (t.Text.Length - 2);
                var typed = line[..cut];
                var before = def[..offsets[li]] + typed;
                var after = li + 1 < lines.Length ? def[offsets[li + 1]..] : "";
                var root = SourceGhost.RootOf(path, def);
                List<(string, string, double, Func<double, string>)> c;
                try { c = g.Candidates(before, after, kind, regions[li], useLocal, root, siblings: sibs); } catch { continue; }
                res.Add(new Site(path, regions[li], "word", typed.Trim(), line[cut..].Trim(), c, SourceGhost.AreaFor(kind, regions[li], root, typed)));
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
                try { c = g.Candidates(before, "", kind, regions[li], useLocal, root, siblings: sibs); } catch { continue; }
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

    /// <summary>Gợi ý được chọn ở 1 chỗ — đúng cách chọn lúc gõ (<see cref="SourceGhost.Choose"/>).</summary>
    public static string Pick(Site s, Func<string, double> threshold, Func<string, bool>? kindOn = null) =>
        SourceGhost.Choose(kindOn is null ? s.Cands : s.Cands.Where(c => kindOn(c.Kind)), threshold)?.Text ?? "";

    /// <summary>Ngưỡng thấp nhất của từng loại để riêng loại đó đúng ≥ <paramref name="target"/> (cần ≥ 30 gợi ý), rồi nâng dần loại kém nhất tới khi
    /// gộp các loại (gợi ý dài nhất) cũng ≥ mục tiêu.</summary>
    public static Dictionary<string, SourceGhost.Quality> Calibrate(List<Site> sites, double target, string suffix = "")
    {
        var kinds = new[] { "field", "same", "tok", "line", "next", "agree", "sib", "word" };
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
            var chosen = SourceGhost.Choose(s.Cands, k => th.GetValueOrDefault(k, 1.01));
            if (chosen is null || chosen.Kind != kind) continue;
            n++;
            if (IsHit(s.Expected, chosen.Text)) hit++;
        }
        return new Score(n, n, hit);
    }
}
