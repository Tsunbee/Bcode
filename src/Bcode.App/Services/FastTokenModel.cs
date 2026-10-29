using System.Text;
using System.Text.Json;

namespace Bcode.App.Services;

/// <summary>
/// "Ma trận token" cho ghost text — mô hình ngôn ngữ n-gram đếm từ store thật (không mạng nơ-ron, không AI):
/// <list type="bullet">
/// <item>G1 chuẩn hoá token: chữ thường, chuỗi dài → <see cref="Str"/> (không bao giờ gợi ý), xuống dòng = token <see cref="Eol"/>; giữ cách viết
/// hay gặp nhất (hoa / thường) và có dấu cách trước token hay không theo cặp token.</item>
/// <item>G2 đoán token kế tiếp theo 1–4 token đứng trước, nội suy các bậc kiểu Kneser-Ney (absolute discounting):
/// P(w | h) = max(c(h,w) − D, 0) / c(h) + D · (số token khác nhau sau h) / c(h) · P(w | h ngắn hơn).</item>
/// <item>G3 trộn với chính file đang sửa (cache model): đếm n-gram của file (phía trên + phía dưới dòng đang gõ), trọng số theo số lần gặp ngữ cảnh
/// trong file — tên biến / bảng riêng của file vẫn đoán được.</item>
/// <item>G4 kéo dài: chọn lần lượt từng token (greedy), nhân dồn xác suất, dừng khi xuống dòng hoặc độ chắc dồn &lt; ngưỡng (ngưỡng hiệu chỉnh trên KOG).</item>
/// </list>
/// </summary>
public sealed class TokenModel
{
    public const int MaxCtx = 4;
    public const double D = 0.75;
    public const string Eol = "⏎";
    public const string Str = "'…'";

    /// <summary>1 ngữ cảnh: tổng số lần, số token khác nhau theo sau, vài token theo sau hay gặp nhất.</summary>
    public sealed class Cell
    {
        public int[] Ctx = Array.Empty<int>();
        public int Total, Distinct;
        public int[] Next = Array.Empty<int>(), N = Array.Empty<int>();
        public int CountOf(int w) { for (var i = 0; i < Next.Length; i++) if (Next[i] == w) return N[i]; return 0; }
    }

    public readonly List<string> Vocab = new();
    public readonly List<string> Surface = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    public readonly Dictionary<ulong, Cell> Cells = new();
    /// <summary>Cặp (token trước, token sau) viết liền không dấu cách, vd "a" "." / "(" "@x" / "ma_kh" ",".</summary>
    public readonly HashSet<long> Tight = new();

    public int Id(string norm) => _ids.TryGetValue(norm, out var i) ? i : -1;

    private int AddId(string norm, string surface)
    {
        if (_ids.TryGetValue(norm, out var i)) return i;
        _ids[norm] = i = Vocab.Count;
        Vocab.Add(norm);
        Surface.Add(surface);
        return i;
    }

    // ---------------------------------------------------------------- G1: tách + chuẩn hoá token

    public readonly record struct Tok(string Norm, string Surface, bool SpaceBefore);

    public static string NormTok(string t) =>
        (t.StartsWith('\'') || t.StartsWith("N'", StringComparison.OrdinalIgnoreCase)) && t.Length > 14 ? Str : t.ToLowerInvariant();

    /// <summary>Token của đoạn code (bỏ comment), thêm <see cref="Eol"/> sau mỗi dòng có code; <paramref name="closeLast"/> = false: dòng cuối đang gõ dở, không thêm.</summary>
    public static List<Tok> Lex(string text, bool closeLast = true)
    {
        var res = new List<Tok>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            var toks = FastGhostRules.Tokens(line);
            foreach (var t in toks) res.Add(new Tok(NormTok(t.Text), t.Text, t.Start == 0 || char.IsWhiteSpace(line[t.Start - 1])));
            if (toks.Count > 0 && (closeLast || li < lines.Length - 1)) res.Add(new Tok(Eol, "\n", false));
        }
        return res;
    }

    /// <summary>Khoá ngữ cảnh = <paramref name="k"/> token ngay trước vị trí <paramref name="end"/> (băm 64 bit, bậc khác nhau không trùng khoá).</summary>
    public static ulong Hash(IReadOnlyList<int> ids, int end, int k)
    {
        var h = 14695981039346656037UL ^ (ulong)k;
        for (var j = 1; j <= k; j++) h = (h ^ (uint)ids[end - j]) * 1099511628211UL;
        return h;
    }

    // ---------------------------------------------------------------- rút

    /// <summary>Đếm n-gram từ các store; bỏ ngữ cảnh gặp &lt; <paramref name="minTotal"/> lần, mỗi ngữ cảnh giữ tối đa <paramref name="keep"/> token theo sau (gặp ≥ 2 lần).</summary>
    public static TokenModel Train(IEnumerable<string> defs, int minTotal = 2, int keep = 4, bool keepSingles = false)
    {
        var m = new TokenModel();
        var pairs = new Dictionary<(ulong, int), int>();
        var ctxIds = new Dictionary<ulong, int[]>();
        var space = new Dictionary<long, int>();       // > 0: hay viết liền
        var surf = new Dictionary<int, Dictionary<string, int>>();
        var occ = new List<int>();
        foreach (var def in defs)
        {
            var toks = Lex(def);
            var ids = new int[toks.Count];
            for (var i = 0; i < toks.Count; i++)
            {
                ids[i] = m.AddId(toks[i].Norm, toks[i].Surface);
                while (occ.Count <= ids[i]) occ.Add(0);
                occ[ids[i]]++;
                if (toks[i].Norm != toks[i].Surface && toks[i].Norm != Eol && toks[i].Norm != Str)
                {
                    if (!surf.TryGetValue(ids[i], out var sv)) surf[ids[i]] = sv = new(StringComparer.Ordinal);
                    sv[toks[i].Surface] = sv.GetValueOrDefault(toks[i].Surface) + 1;
                }
            }
            for (var i = 1; i < ids.Length; i++)
            {
                if (toks[i].Norm != Eol && toks[i - 1].Norm != Eol)
                {
                    var key = ((long)ids[i - 1] << 32) | (uint)ids[i];
                    space[key] = space.GetValueOrDefault(key) + (toks[i].SpaceBefore ? -1 : 1);
                }
                for (var k = 1; k <= MaxCtx && i - k >= 0; k++)
                {
                    var h = Hash(ids, i, k);
                    if (!ctxIds.ContainsKey(h)) ctxIds[h] = ids[(i - k)..i];
                    pairs[(h, ids[i])] = pairs.GetValueOrDefault((h, ids[i])) + 1;
                }
            }
        }
        foreach (var ((h, w), c) in pairs)
        {
            if (!m.Cells.TryGetValue(h, out var cell)) m.Cells[h] = cell = new Cell { Ctx = ctxIds[h] };
            cell.Total += c;
            cell.Distinct++;
            if (c >= 2 || keepSingles) Insert(cell, w, c, keep);
        }
        foreach (var h in m.Cells.Where(x => x.Value.Total < minTotal).Select(x => x.Key).ToList()) m.Cells.Remove(h);
        foreach (var (id, sv) in surf)
        {
            // cách viết hay gặp nhất: so cách viết hoa nhiều nhất với số lần viết đúng chữ thường (= tổng − các cách viết khác)
            var best = sv.OrderByDescending(x => x.Value).First();
            var lowerN = occ[id] - sv.Values.Sum();
            m.Surface[id] = best.Value > lowerN ? best.Key : m.Vocab[id];
        }
        foreach (var (k, v) in space) if (v > 0) m.Tight.Add(k);
        return m;
    }

    private static void Insert(Cell c, int w, int n, int keep)
    {
        var next = c.Next.ToList(); var cnt = c.N.ToList();
        var at = cnt.FindIndex(x => x < n);
        if (at < 0) at = cnt.Count;
        if (at >= keep) return;
        next.Insert(at, w); cnt.Insert(at, n);
        if (next.Count > keep) { next.RemoveAt(keep); cnt.RemoveAt(keep); }
        c.Next = next.ToArray(); c.N = cnt.ToArray();
    }

    // ---------------------------------------------------------------- G2: xác suất

    /// <summary>P(w | ngữ cảnh) nội suy từ bậc 1 token lên tới bậc dài nhất có trong ma trận.</summary>
    public double P(int w, IReadOnlyList<int> ctx, out int topTotal)
    {
        double p = 0;
        topTotal = 0;
        for (var k = 1; k <= Math.Min(MaxCtx, ctx.Count); k++)
        {
            if (ctx[ctx.Count - k] < 0 || !Cells.TryGetValue(Hash(ctx, ctx.Count, k), out var c)) break;
            p = Math.Max(c.CountOf(w) - D, 0) / c.Total + D * c.Distinct / c.Total * p;
            if (k >= 2) topTotal = c.Total;
        }
        return p;
    }

    /// <summary>Token có thể theo sau ngữ cảnh (gộp các bậc).</summary>
    public IEnumerable<int> Candidates(IReadOnlyList<int> ctx)
    {
        var seen = new HashSet<int>();
        for (var k = 1; k <= Math.Min(MaxCtx, ctx.Count); k++)
        {
            if (ctx[ctx.Count - k] < 0 || !Cells.TryGetValue(Hash(ctx, ctx.Count, k), out var c)) break;
            foreach (var w in c.Next) if (seen.Add(w)) yield return w;
        }
    }

    public bool IsTight(string prevNorm, string nextNorm)
    {
        var a = Id(prevNorm); var b = Id(nextNorm);
        return a >= 0 && b >= 0 && Tight.Contains(((long)a << 32) | (uint)b);
    }

    // ---------------------------------------------------------------- G3 + G4: giải mã

    /// <summary>Hệ số trộn với file đang sửa: λ = n / (n + β), n = số lần gặp ngữ cảnh (≥ 2 token) trong file.</summary>
    public static double CacheBeta { get; set; } = 8;   // đo trên KOG: β = 8 tốt nhất (2 làm giảm độ chính xác, 30 gần như không trộn)

    /// <summary>Có trộn n-gram của chính file (G3) khi gợi ý không — theo kết quả đo trên KOG.</summary>
    public static bool UseLocal { get => _useLocal && !_offHere; set => _useLocal = value; }
    private static bool _useLocal = true;
    [ThreadStatic] private static bool _offHere;

    /// <summary>Tắt trộn file đang sửa riêng cho luồng hiện tại (lúc học / đo) tới khi Dispose.</summary>
    public static IDisposable LocalOff() { var old = _offHere; _offHere = true; return new Restore(() => _offHere = old); }
    private sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }

    public sealed record Decoded(IReadOnlyList<string> Pieces, IReadOnlyList<double> Cum)
    {
        /// <summary>Phần gợi ý dài nhất mà độ chắc dồn còn ≥ <paramref name="threshold"/>; "" = không có.</summary>
        public string Cut(double threshold)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < Pieces.Count && Cum[i] >= threshold - 1e-9; i++) sb.Append(Pieces[i]);
            return sb.ToString().TrimEnd();
        }
    }

    /// <summary>Đoán tiếp từ cuối <paramref name="before"/> (con trỏ cuối dòng): tối đa <paramref name="maxTok"/> token, dừng ở cuối dòng.
    /// <paramref name="local"/> = n-gram của chính file (null = không trộn). Đang gõ dở 1 từ thì token đầu phải bắt đầu bằng phần đã gõ.</summary>
    public static Decoded? Decode(TokenModel g, TokenModel? local, string before, int maxTok = 12)
    {
        var toks = Lex(before, closeLast: false);
        if (toks.Count == 0) return null;
        var prefix = "";
        if (before.Length > 0 && (char.IsLetterOrDigit(before[^1]) || before[^1] is '_' or '$' or '@' or '#') && toks[^1].Norm != Eol)
        {
            prefix = toks[^1].Norm;
            toks.RemoveAt(toks.Count - 1);
        }
        var endsSpace = before.Length > 0 && char.IsWhiteSpace(before[^1]);
        var ctx = toks.Select(t => t.Norm).ToList();
        if (ctx.Count == 0) return null;
        var declared = new HashSet<string>(System.Text.RegularExpressions.Regex.Matches(before, @"(?<![@\w])@\w+").Select(m => m.Value.ToLowerInvariant()));
        var pieces = new List<string>(); var cum = new List<double>();
        double acc = 1;
        for (var step = 0; step < maxTok; step++)
        {
            var gctx = ctx.Select(g.Id).ToList();
            var lctx = local is null ? null : ctx.Select(local.Id).ToList();
            var cands = g.Candidates(gctx).Select(i => g.Vocab[i]);
            if (local is not null) cands = cands.Concat(local.Candidates(lctx!).Select(i => local.Vocab[i]));
            string? best = null; double bestP = 0, sumP = 0;
            foreach (var w in cands.Distinct())
            {
                if (w == Str) continue;
                if (step == 0 && prefix.Length > 0 && (!w.StartsWith(prefix, StringComparison.Ordinal) || w.Length <= prefix.Length)) continue;
                if (w.StartsWith('@') && !w.StartsWith("@@") && !declared.Contains(w)) continue;   // biến chưa khai báo phía trên → chắc sai
                var pg = g.Id(w) is var gi and >= 0 ? g.P(gi, gctx, out _) : 0;
                double p = pg;
                if (local is not null && local.Id(w) is var li and >= 0)
                {
                    var pl = local.P(li, lctx!, out var n);
                    var lambda = n / (n + CacheBeta);
                    p = (1 - lambda) * pg + lambda * pl;
                }
                sumP += p;
                if (p > bestP) { bestP = p; best = w; }
            }
            if (best is null || best == Eol) break;
            // đang gõ dở 1 từ: xác suất có điều kiện "từ bắt đầu bằng phần đã gõ"
            if (step == 0 && prefix.Length > 0) bestP /= sumP + 0.05;
            acc *= bestP;
            var surface = g.Id(best) is var bi and >= 0 ? g.Surface[bi] : local!.Surface[local.Id(best)];
            string piece;
            if (step == 0 && prefix.Length > 0) piece = surface.Length > prefix.Length ? surface[prefix.Length..] : "";
            else
            {
                var prev = ctx[^1];
                var tight = g.IsTight(prev, best) || (local?.IsTight(prev, best) ?? false);
                piece = (tight || (step == 0 && endsSpace) ? "" : " ") + surface;
            }
            pieces.Add(piece); cum.Add(acc);
            ctx.Add(best);
            if (acc < 0.05) break;
        }
        return pieces.Count == 0 ? null : new Decoded(pieces, cum);
    }

    /// <summary>N-gram của chính file đang sửa (phía trên + phía dưới dòng đang gõ) cho G3; nhớ theo nội dung để mỗi phím không đếm lại.</summary>
    public static TokenModel LocalOf(string before, string after)
    {
        var key = before.Length + ":" + after.Length + ":" + (before.Length > 0 ? before.GetHashCode() : 0) + ":" + after.GetHashCode();
        lock (LocalLock)
        {
            if (_local is { } l && l.Key == key) return l.M;
            // bỏ dòng đang gõ ra khỏi phần phía trên (nó chưa xong)
            var cut = before.LastIndexOf('\n');
            var m = Train(new[] { (cut < 0 ? "" : before[..cut]) + "\n" + after }, minTotal: 1, keep: 6, keepSingles: true);
            _local = (key, m);
            return m;
        }
    }
    private static readonly object LocalLock = new();
    private static (string Key, TokenModel M)? _local;

    // ---------------------------------------------------------------- lọc / gộp / ghi / đọc

    /// <summary>Bản rút gọn: chỉ ngữ cảnh gặp ≥ <paramref name="minTotal"/> lần, token cho qua <paramref name="allow"/> (ma trận đi kèm: chỉ từ vựng công khai).</summary>
    public TokenModel Filter(int minTotal, Func<string, bool>? allow = null)
    {
        var res = new TokenModel();
        int Map(int id) => res.AddId(Vocab[id], Surface[id]);
        foreach (var c in Cells.Values)
        {
            if (c.Total < minTotal || (allow is not null && c.Ctx.Any(i => !allow(Vocab[i])))) continue;
            var keepIdx = Enumerable.Range(0, c.Next.Length).Where(i => allow is null || allow(Vocab[c.Next[i]])).ToList();
            var ctx = c.Ctx.Select(Map).ToArray();
            res.Cells[Hash(ctx, ctx.Length, ctx.Length)] = new Cell
            {
                Ctx = ctx, Total = c.Total, Distinct = c.Distinct,
                Next = keepIdx.Select(i => Map(c.Next[i])).ToArray(), N = keepIdx.Select(i => c.N[i]).ToArray(),
            };
        }
        foreach (var t in Tight)
        {
            var a = res.Id(Vocab[(int)(t >> 32)]); var b = res.Id(Vocab[(int)(uint)t]);
            if (a >= 0 && b >= 0) res.Tight.Add(((long)a << 32) | (uint)b);
        }
        return res;
    }

    /// <summary>Gộp nhiều mô hình (kiến thức chung các dự án): cộng số lần theo chữ của token.</summary>
    public static TokenModel Merge(IEnumerable<TokenModel> models, int keep = 4)
    {
        var res = new TokenModel();
        var votes = new Dictionary<long, int>();
        foreach (var m in models)
        {
            int Map(int id) => res.AddId(m.Vocab[id], m.Surface[id]);
            foreach (var c in m.Cells.Values)
            {
                var ctx = c.Ctx.Select(Map).ToArray();
                var h = Hash(ctx, ctx.Length, ctx.Length);
                if (!res.Cells.TryGetValue(h, out var r)) res.Cells[h] = r = new Cell { Ctx = ctx };
                r.Total += c.Total; r.Distinct = Math.Max(r.Distinct, c.Distinct);
                for (var i = 0; i < c.Next.Length; i++)
                {
                    var w = Map(c.Next[i]); var n = c.N[i];
                    var at = Array.IndexOf(r.Next, w);
                    if (at >= 0) { var nn = r.N[at] + n; r.Next = r.Next.Where((_, j) => j != at).ToArray(); r.N = r.N.Where((_, j) => j != at).ToArray(); Insert(r, w, nn, keep); }
                    else Insert(r, w, n, keep);
                }
            }
            foreach (var t in m.Tight) votes[((long)Map((int)(t >> 32)) << 32) | (uint)Map((int)(uint)t)] = 1;
        }
        foreach (var k in votes.Keys) res.Tight.Add(k);
        return res;
    }

    /// <summary>JSON gọn: vocab, surface (chỉ chỗ khác vocab), tight [a, b, …], cells [[k, ctx…, total, distinct, next, n, …]].</summary>
    public object ToJson() => new
    {
        vocab = Vocab,
        surface = Surface.Select((s, i) => (s, i)).Where(x => x.s != Vocab[x.i]).ToDictionary(x => x.i.ToString(), x => x.s),
        tight = Tight.SelectMany(t => new[] { (int)(t >> 32), (int)(uint)t }).ToArray(),
        cells = Cells.Values.Select(c => new[] { c.Ctx.Length }.Concat(c.Ctx).Append(c.Total).Append(c.Distinct)
            .Concat(c.Next.Zip(c.N, (a, b) => new[] { a, b }).SelectMany(x => x)).ToArray()).ToArray(),
    };

    public static TokenModel FromJson(JsonElement e)
    {
        var m = new TokenModel();
        foreach (var v in e.GetProperty("vocab").EnumerateArray()) { var s = v.GetString() ?? ""; m.AddId(s, s); }
        if (e.TryGetProperty("surface", out var sf)) foreach (var p in sf.EnumerateObject()) m.Surface[int.Parse(p.Name)] = p.Value.GetString() ?? m.Surface[int.Parse(p.Name)];
        if (e.TryGetProperty("tight", out var ti)) { var a = ti.EnumerateArray().Select(x => x.GetInt32()).ToArray(); for (var i = 0; i + 1 < a.Length; i += 2) m.Tight.Add(((long)a[i] << 32) | (uint)a[i + 1]); }
        foreach (var row in e.GetProperty("cells").EnumerateArray())
        {
            var a = row.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            var k = a[0];
            var ctx = a[1..(1 + k)];
            var c = new Cell { Ctx = ctx, Total = a[1 + k], Distinct = a[2 + k] };
            var rest = a[(3 + k)..];
            c.Next = rest.Where((_, i) => i % 2 == 0).ToArray(); c.N = rest.Where((_, i) => i % 2 == 1).ToArray();
            m.Cells[Hash(ctx, k, k)] = c;
        }
        return m;
    }
}
