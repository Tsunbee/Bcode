using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BcodeViewer.App.Host.FastSource;

/// <summary>
/// Tách token cho source web Fast: controller XML (thẻ, thuộc tính, &amp;entity; / %entity;, CDATA), JavaScript và ASPX / HTML.
/// Chuỗi trong nháy (giá trị thuộc tính, chuỗi JS) là 1 token nếu ngắn — "AutoComplete", 'ma_kh' đoán được nguyên cụm; chuỗi dài
/// (SQL trong key="…") thành <see cref="SourceTokenModel.Str"/> và không bao giờ được gợi ý. Giữ hoa / thường (XML, JS phân biệt).
/// </summary>
public static class SourceLexer
{
    public readonly record struct Token(string Text, int Start);

    private static readonly string[] Multi =
    {
        "<![CDATA[", "]]>", "<!--", "-->", "<!", "</", "/>", "<%@", "<%=", "<%#", "<%", "%>",
        "===", "!==", "==", "!=", "<=", ">=", "&&", "||", "++", "--", "+=", "-=", "*=", "=>",
    };

    /// <summary>Token của 1 dòng. Chuỗi chưa đóng (đang gõ dở giá trị) là 1 token tới cuối dòng.</summary>
    public static List<Token> Tokens(string line)
    {
        var res = new List<Token>();
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i;
            if (c is '"' or '\'')
            {
                var end = line.IndexOf(c, i + 1);
                i = end < 0 ? line.Length : end + 1;
                res.Add(new Token(line[start..i], start));
                continue;
            }
            if ((c == '&' || c == '%') && i + 1 < line.Length && IsWordStart(line[i + 1]))
            {
                var j = i + 1;
                while (j < line.Length && (IsWord(line[j]) || line[j] is '.' or '-')) j++;
                if (j < line.Length && line[j] == ';') { res.Add(new Token(line[i..(j + 1)], start)); i = j + 1; continue; }
            }
            if (IsWordStart(c))
            {
                i++;
                while (i < line.Length && IsWord(line[i])) i++;
                res.Add(new Token(line[start..i], start));
                continue;
            }
            if (char.IsDigit(c))
            {
                i++;
                while (i < line.Length && (char.IsDigit(line[i]) || line[i] == '.')) i++;
                res.Add(new Token(line[start..i], start));
                continue;
            }
            var m = Multi.FirstOrDefault(p => string.CompareOrdinal(line, i, p, 0, p.Length) == 0);
            i += m?.Length ?? 1;
            res.Add(new Token(line[start..i], start));
        }
        return res;
    }

    public static bool IsWordStart(char c) => char.IsLetter(c) || c is '_' or '$' or '@';
    public static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '$';
}

/// <summary>
/// "Ma trận token" cho source web Fast — cùng cách làm với <c>Bcode.App/Services/FastTokenModel.cs</c> (n-gram 1–4 token, nội suy
/// absolute discounting kiểu Kneser-Ney, trộn n-gram của chính file đang sửa, kéo dài gợi ý tới cuối dòng) nhưng tách token theo
/// <see cref="SourceLexer"/> và giữ hoa / thường. Hai project không tham chiếu nhau nên đây là bản riêng của BcodeViewer.
/// </summary>
public sealed class SourceTokenModel
{
    public const int MaxCtx = 4;
    public const double D = 0.75;
    public const string Eol = "⏎";
    public const string Str = "\"…\"";

    public sealed class Cell
    {
        public int[] Ctx = Array.Empty<int>();
        public int Total, Distinct;
        public int[] Next = Array.Empty<int>(), N = Array.Empty<int>();
        public int CountOf(int w) { for (var i = 0; i < Next.Length; i++) if (Next[i] == w) return N[i]; return 0; }
    }

    public readonly List<string> Vocab = new();
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    public readonly Dictionary<ulong, Cell> Cells = new();
    /// <summary>Cặp (token trước, token sau) hay viết liền không dấu cách: "&lt;" "field" / "name" "=" / "f" ".".</summary>
    public readonly HashSet<long> Tight = new();

    public int Id(string norm) => _ids.TryGetValue(norm, out var i) ? i : -1;

    private int AddId(string norm)
    {
        if (_ids.TryGetValue(norm, out var i)) return i;
        _ids[norm] = i = Vocab.Count;
        Vocab.Add(norm);
        return i;
    }

    public readonly record struct Tok(string Norm, bool SpaceBefore);

    /// <summary>Chuỗi dài (SQL trong thuộc tính, câu thông báo) → 1 token chung, không gợi ý.</summary>
    public static string NormTok(string t) => (t[0] is '"' or '\'') && t.Length > 40 ? Str : t;

    /// <summary>Token của đoạn code, thêm <see cref="Eol"/> sau mỗi dòng có token; <paramref name="closeLast"/> = false: dòng cuối đang gõ dở.</summary>
    public static List<Tok> Lex(string text, bool closeLast = true)
    {
        var res = new List<Tok>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            var line = lines[li];
            var toks = SourceLexer.Tokens(line);
            foreach (var t in toks) res.Add(new Tok(NormTok(t.Text), t.Start == 0 || char.IsWhiteSpace(line[t.Start - 1])));
            if (toks.Count > 0 && (closeLast || li < lines.Length - 1)) res.Add(new Tok(Eol, false));
        }
        return res;
    }

    public static ulong Hash(IReadOnlyList<int> ids, int end, int k)
    {
        var h = 14695981039346656037UL ^ (ulong)k;
        for (var j = 1; j <= k; j++) h = (h ^ (uint)ids[end - j]) * 1099511628211UL;
        return h;
    }

    // ---------------------------------------------------------------- rút

    /// <summary>Đếm n-gram; bỏ ngữ cảnh gặp &lt; <paramref name="minTotal"/> lần, mỗi ngữ cảnh giữ tối đa <paramref name="keep"/> token theo sau (gặp ≥ 2 lần).</summary>
    public static SourceTokenModel Train(IEnumerable<string> texts, int minTotal = 2, int keep = 4, bool keepSingles = false)
    {
        var m = new SourceTokenModel();
        var pairs = new Dictionary<(ulong, int), int>();
        var ctxIds = new Dictionary<ulong, int[]>();
        var space = new Dictionary<long, int>();
        foreach (var text in texts)
        {
            var toks = Lex(text);
            var ids = new int[toks.Count];
            for (var i = 0; i < toks.Count; i++) ids[i] = m.AddId(toks[i].Norm);
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

    // ---------------------------------------------------------------- xác suất

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

    public IEnumerable<int> Candidates(IReadOnlyList<int> ctx)
    {
        var seen = new HashSet<int>();
        for (var k = 1; k <= Math.Min(MaxCtx, ctx.Count); k++)
        {
            if (ctx[ctx.Count - k] < 0 || !Cells.TryGetValue(Hash(ctx, ctx.Count, k), out var c)) break;
            foreach (var w in c.Next) if (seen.Add(w)) yield return w;
        }
    }

    public bool IsTight(string prev, string next)
    {
        var a = Id(prev); var b = Id(next);
        return a >= 0 && b >= 0 && Tight.Contains(((long)a << 32) | (uint)b);
    }

    // ---------------------------------------------------------------- giải mã

    /// <summary>Hệ số trộn với file đang sửa: λ = n / (n + β), n = số lần gặp ngữ cảnh (≥ 2 token) trong file.</summary>
    public static double CacheBeta { get; set; } = 8;

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

    /// <summary>Đoán tiếp từ cuối <paramref name="before"/>: tối đa <paramref name="maxTok"/> token, dừng ở cuối dòng. Đang gõ dở 1 từ
    /// (hoặc 1 chuỗi chưa đóng nháy) thì token đầu phải bắt đầu bằng phần đã gõ.</summary>
    public static Decoded? Decode(SourceTokenModel g, SourceTokenModel? local, string before, int maxTok = 16)
    {
        var toks = Lex(before, closeLast: false);
        if (toks.Count == 0) return null;
        var prefix = "";
        var lastLine = before[(before.LastIndexOf('\n') + 1)..];
        var lineToks = SourceLexer.Tokens(lastLine);
        if (lineToks.Count > 0 && toks[^1].Norm != Eol)
        {
            var lt = lineToks[^1];
            var atEnd = lt.Start + lt.Text.Length == lastLine.Length;
            var partialWord = atEnd && SourceLexer.IsWord(lastLine[^1]) && (SourceLexer.IsWordStart(lt.Text[0]) || char.IsDigit(lt.Text[0]));
            var openString = lt.Text[0] is '"' or '\'' && (lt.Text.Length == 1 || lt.Text[^1] != lt.Text[0]);
            if (partialWord || openString)
            {
                prefix = lt.Text;
                toks.RemoveAt(toks.Count - 1);
            }
        }
        var endsSpace = before.Length > 0 && char.IsWhiteSpace(before[^1]);
        var ctx = toks.Select(t => t.Norm).ToList();
        if (ctx.Count == 0) return null;
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
            if (step == 0 && prefix.Length > 0) bestP /= sumP + 0.05;
            acc *= bestP;
            string piece;
            if (step == 0 && prefix.Length > 0) piece = best[prefix.Length..];
            else
            {
                var prev = ctx[^1];
                var tight = g.IsTight(prev, best) || (local?.IsTight(prev, best) ?? false);
                piece = (tight || (step == 0 && endsSpace) || prev == Eol ? "" : " ") + best;
            }
            pieces.Add(piece); cum.Add(acc);
            ctx.Add(best);
            if (acc < 0.05) break;
        }
        return pieces.Count == 0 ? null : new Decoded(pieces, cum);
    }

    /// <summary>N-gram của chính file đang sửa (phía trên + phía dưới dòng đang gõ); nhớ theo nội dung để mỗi phím không đếm lại.</summary>
    public static SourceTokenModel LocalOf(string before, string after)
    {
        var key = before.Length + ":" + after.Length + ":" + before.GetHashCode() + ":" + after.GetHashCode();
        lock (LocalLock)
        {
            if (_local is { } l && l.Key == key) return l.M;
            var cut = before.LastIndexOf('\n');
            var m = Train(new[] { (cut < 0 ? "" : before[..cut]) + "\n" + after }, minTotal: 1, keep: 6, keepSingles: true);
            _local = (key, m);
            return m;
        }
    }
    private static readonly object LocalLock = new();
    private static (string Key, SourceTokenModel M)? _local;

    // ---------------------------------------------------------------- lọc / gộp / ghi / đọc

    /// <summary>Bản rút gọn: chỉ ngữ cảnh gặp ≥ <paramref name="minTotal"/> lần.</summary>
    public SourceTokenModel Filter(int minTotal)
    {
        var res = new SourceTokenModel();
        foreach (var c in Cells.Values)
        {
            if (c.Total < minTotal) continue;
            var ctx = c.Ctx.Select(i => res.AddId(Vocab[i])).ToArray();
            res.Cells[Hash(ctx, ctx.Length, ctx.Length)] = new Cell
            {
                Ctx = ctx, Total = c.Total, Distinct = c.Distinct,
                Next = c.Next.Select(i => res.AddId(Vocab[i])).ToArray(), N = c.N.ToArray(),
            };
        }
        foreach (var t in Tight)
        {
            var a = res.Id(Vocab[(int)(t >> 32)]); var b = res.Id(Vocab[(int)(uint)t]);
            if (a >= 0 && b >= 0) res.Tight.Add(((long)a << 32) | (uint)b);
        }
        return res;
    }

    /// <summary>Gộp nhiều mô hình (mẫu đi kèm + source dự án): cộng số lần theo chữ của token.</summary>
    public static SourceTokenModel Merge(IEnumerable<SourceTokenModel> models, int keep = 4)
    {
        var res = new SourceTokenModel();
        foreach (var m in models)
        {
            int Map(int id) => res.AddId(m.Vocab[id]);
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
            foreach (var t in m.Tight) res.Tight.Add(((long)Map((int)(t >> 32)) << 32) | (uint)Map((int)(uint)t));
        }
        return res;
    }

    public object ToJson() => new
    {
        vocab = Vocab,
        tight = Tight.SelectMany(t => new[] { (int)(t >> 32), (int)(uint)t }).ToArray(),
        cells = Cells.Values.Select(c => new[] { c.Ctx.Length }.Concat(c.Ctx).Append(c.Total).Append(c.Distinct)
            .Concat(c.Next.Zip(c.N, (a, b) => new[] { a, b }).SelectMany(x => x)).ToArray()).ToArray(),
    };

    public static SourceTokenModel FromJson(JsonElement e)
    {
        var m = new SourceTokenModel();
        foreach (var v in e.GetProperty("vocab").EnumerateArray()) m.AddId(v.GetString() ?? "");
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
