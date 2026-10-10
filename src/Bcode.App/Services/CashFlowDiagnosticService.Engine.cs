using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Bộ máy đối chiếu LCTT gián tiếp không phụ thuộc bộ tài khoản của khách:
///  (1) TÍNH LẠI từng chỉ tiêu đúng như procedure rs_rptInDirectCashflow của Fast (Cách tính 0/1/2, Phân loại, TK / TK đối ứng, Đầu/Cuối, Không âm,
///      Loại công nợ, Thu/Chi) từ bảng kê + bảng cân đối phát sinh, rồi so với báo cáo thật để biết mô hình có đúng không.
///  (2) ĐỐI CHIẾU TỪNG CẶP BÚT TOÁN: mỗi cặp Nợ A / Có B phải làm 70B (chênh lệch tiền) thay đổi đúng 0 — cặp nào khác 0 là điểm sai, kèm số tiền
///      và chỉ tiêu nào đang "hứng" nó. Tài khoản loại 5-9 được coi là lợi nhuận (bút toán vào 5-9 = lợi nhuận, các dòng điều chỉnh 05x/04x… phân bổ lại
///      về đúng bút toán gốc của TK đó).
///  (3) TỰ THỬ CÁCH SỬA: sinh các cách sửa nhỏ (bỏ TK trùng, để trống dòng, đưa TK vào nhóm số dư, lấy theo mẫu đã chạy đúng của khách / mẫu Fast),
///      tính lại toàn bộ cho từng cách, giữ cách làm tổng lệch giảm nhiều nhất — lặp đến khi hết lệch.
/// </summary>
public static partial class CashFlowDiagnosticService
{
    /// <summary>Chỉ để kiểm thử: chạy như khách mới hoàn toàn (không có mẫu tham chiếu nào).</summary>
    public static bool EngineNoReference { get; set; }

    // kết quả bộ máy cho lần chạy hiện tại: tin cậy được không, khai báo đã đúng chưa, 70B tính lại
    [ThreadStatic] private static bool _engReliable;
    [ThreadStatic] private static bool _engCorrect;
    [ThreadStatic] private static double _engGap, _engRepGap;
    [ThreadStatic] private static bool _engStale;

    private sealed class EL
    {
        public string Code = "", Name = "", Formula = "";
        public List<string> Tk = new(), Du = new();
        public int Kind, NoCo = 1, DauCuoi = 1, KhongAm, CongNo, ThuChi = 1;
        public bool Guessed;
        public EL Clone() => new() { Code = Code, Name = Name, Formula = Formula, Tk = new(Tk), Du = new(Du), Kind = Kind, NoCo = NoCo, DauCuoi = DauCuoi, KhongAm = KhongAm, CongNo = CongNo, ThuChi = ThuChi, Guessed = Guessed };
        public bool IsBlank => Formula.Length == 0 && Tk.Count + Du.Count == 0;
    }

    private sealed class Book
    {
        public List<TbAcc> Leaves = new();
        public Dictionary<(string Tk, string Du), double> No = new(), Co = new();
        public List<(string D, string C, double X)> Pairs = new();
    }

    private sealed class PairImb { public string D = "", C = ""; public double X, Imb; public List<(string Code, double V)> Hits = new(); }

    private sealed class EngineEval
    {
        public Dictionary<string, double> Val = new(StringComparer.OrdinalIgnoreCase);
        public List<PairImb> Pairs = new();
        public List<(string Acc, double V)> Open = new();
        public double Objective, Linear;
    }

    private static bool IsPl(string acc) => acc.Length > 0 && acc[0] >= '5' && acc[0] <= '9';

    private static Book BuildBook(List<TbAcc> tb, List<Jr> jr)
    {
        var b = new Book { Leaves = tb.Where(a => a.Leaf && a.Code.Length > 0).ToList() };
        foreach (var r in jr)
        {
            if (r.Tk.Length == 0 || r.Dut.Length == 0) continue;
            var k = (r.Tk, r.Dut);
            if (r.No != 0) { b.No.TryGetValue(k, out var o); b.No[k] = o + r.No; }
            if (r.Co != 0) { b.Co.TryGetValue(k, out var o); b.Co[k] = o + r.Co; }
        }
        foreach (var kv in b.No) if (Math.Abs(kv.Value) > 0.5 && kv.Key.Tk != kv.Key.Du) b.Pairs.Add((kv.Key.Tk, kv.Key.Du, kv.Value));
        return b;
    }

    /// <summary>Giải cờ của từng dòng: lấy từ khai báo (đủ cột), không có thì theo dòng cùng mã của mẫu tham chiếu, không có nữa thì đoán theo tên.</summary>
    private static List<EL> ToEngine(List<Line> cfg, FastDecl? refd)
    {
        var res = new List<EL>();
        foreach (var l in cfg)
        {
            var e = new EL { Code = l.Code, Name = l.Name, Formula = l.Formula.Trim(), Tk = new(l.Acc), Du = new(l.Con) };
            var r = refd?.Line(l.Code);
            if (r is not null && r.Formula.Length == 0 && r.Tk.Count + r.TkDu.Count == 0) r = refd!.BasedOn.Length > 0 ? CfsFasts.Get(refd.BasedOn)?.Line(l.Code) : null;   // dòng mẫu để trống thì không dùng cờ của nó
            if (r is not null && r.Formula.Length == 0 && r.Tk.Count + r.TkDu.Count == 0) r = null;
            var n = (l.Name ?? "").ToLowerInvariant();
            if (e.Formula.Length > 0) e.Kind = 0;
            else if (l.Kind >= 0) e.Kind = l.Kind;
            else if (r is not null && r.Formula.Length == 0) { e.Kind = r.Kind; e.Guessed = true; }
            else
            {
                e.Guessed = true;
                e.Kind = e.Tk.Count + e.Du.Count == 0 ? 0 : e.Du.Count > 0 ? 1 : n.Contains("đầu kỳ") || n.Contains("cuối kỳ") || l.Code is "60" or "70A" ? 2 : 1;
            }
            int Pick(int mine, int? theirs, int def) => mine >= 0 ? mine : theirs is int t && t > 0 ? t : def;
            e.NoCo = Pick(l.NoCo, r?.NoCo, 1);
            e.DauCuoi = Pick(l.DauCuoi, r?.DauCuoi, n.Contains("cuối kỳ") || l.Code == "70A" ? 2 : 1);
            e.KhongAm = l.KhongAm >= 0 ? l.KhongAm : r?.KhongAm ?? 0;
            e.CongNo = l.CongNo >= 0 ? l.CongNo : r?.CongNo ?? 0;
            e.ThuChi = l.ThuChi >= 0 ? l.ThuChi : r?.ThuChi ?? 1;
            res.Add(e);
        }
        return res;
    }

    /// <summary>File khai báo thiếu cột cờ: thử các tổ hợp cờ cho từng dòng, giữ tổ hợp làm số tính lại KHỚP số trên báo cáo (ưu tiên tổ hợp đang đoán).</summary>
    private static int InferFlags(List<EL> ls, Book b, Dictionary<string, (double Cur, double Prev)> rep)
    {
        var fixedN = 0;
        foreach (var e in ls.Where(e => e.Guessed && e.CongNo != 1 && e.Formula.Length == 0 && e.Tk.Count + e.Du.Count > 0 && rep.ContainsKey(e.Code)))     // dòng công nợ theo khách không tái tạo được từ bảng cân đối TK → giữ cờ theo mẫu
        {
            var target = rep[e.Code].Cur;
            double Sim(EL x) { var v = Simulate(new List<EL> { x }, b); return v.TryGetValue(x.Code, out var r) ? r : 0; }
            if (Math.Abs(Sim(e) - target) < 1) continue;
            var tries = new List<EL>();
            foreach (var kind in e.Du.Count > 0 ? new[] { 1 } : new[] { 2, 1 })
                foreach (var thu in new[] { e.ThuChi, 1 - e.ThuChi })
                    foreach (var noco in new[] { e.NoCo, 3 - e.NoCo })
                        if (kind == 1) tries.Add(new EL { Code = e.Code, Tk = e.Tk, Du = e.Du, Kind = 1, NoCo = noco, ThuChi = thu });
                        else foreach (var dc in new[] { e.DauCuoi, 3 - e.DauCuoi })
                            foreach (var cn in new[] { e.CongNo, 1 - e.CongNo })
                                foreach (var ka in new[] { e.KhongAm, 1 - e.KhongAm })
                                    tries.Add(new EL { Code = e.Code, Tk = e.Tk, Du = e.Du, Kind = 2, NoCo = noco, ThuChi = thu, DauCuoi = dc, CongNo = cn, KhongAm = ka });
            var hit = tries.Where(t => t.CongNo != 1).FirstOrDefault(t => Math.Abs(Sim(t) - target) < 1);
            if (hit is null) continue;
            e.Kind = hit.Kind; e.NoCo = hit.NoCo; e.ThuChi = hit.ThuChi; e.DauCuoi = hit.DauCuoi; e.CongNo = hit.CongNo; e.KhongAm = hit.KhongAm; fixedN++;
        }
        return fixedN;
    }

    private static string? TokenOf(string acc, List<string> toks)
    {
        string? best = null;
        foreach (var t in toks) if (acc.StartsWith(t, StringComparison.Ordinal) && (best is null || t.Length > best.Length)) best = t;
        return best;
    }

    private static double GroupVal(EL e, double net)
    {
        if (e.CongNo == 1) return e.NoCo == 1 ? Math.Max(net, 0) : Math.Max(-net, 0);
        var v = e.NoCo == 1 ? net : -net;
        return e.KhongAm == 1 ? Math.Max(v, 0) : v;
    }

    /// <summary>Đạo hàm giá trị dòng số dư theo số dư (Nợ − Có) của nhóm TK: chặn không âm / công nợ một vế thì = 0 ở phía bị chặn; số dư = 0 thì chia đôi (cặp dòng Nợ / Có cộng lại vẫn = 1).</summary>
    private static double Deriv(EL e, double net)
    {
        var s = e.NoCo == 1 ? 1.0 : -1.0;
        if (e.CongNo != 1 && e.KhongAm != 1) return s;
        if (Math.Abs(net) < 0.5) return s * 0.5;
        return s * net > 0 ? s : 0;
    }

    private static Dictionary<string, double> GroupNets(EL e, Book b, bool close)
    {
        var g = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var a in b.Leaves)
        {
            var t = e.Tk.Count == 0 ? "" : TokenOf(a.Code, e.Tk); if (t is null) continue;
            if (e.CongNo == 1 && !CatCongNo(a.Code)) continue;     // Fast: số dư theo khách chỉ có ở TK tk_cn = 1
            g.TryGetValue(t, out var o); g[t] = o + (close ? a.CloseNet : a.OpenNet);
        }
        return g;
    }

    private static Dictionary<string, double> Simulate(List<EL> ls, Book b)
    {
        var raw = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ls)
        {
            if (e.Formula.Length > 0 || e.Kind == 0) continue;
            double v = 0;
            if (e.Kind == 1)
            {
                foreach (var kv in e.NoCo == 2 ? b.Co : b.No)
                    if ((e.Tk.Count == 0 || TokenOf(kv.Key.Tk, e.Tk) is not null) && (e.Du.Count == 0 || TokenOf(kv.Key.Du, e.Du) is not null)) v += kv.Value;
            }
            else foreach (var net in GroupNets(e, b, e.DauCuoi == 2).Values) v += GroupVal(e, net);
            raw[e.Code] = e.ThuChi == 0 ? -v : v;
        }
        var by = ls.GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var val = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double Eval(string code, int depth)
        {
            if (val.TryGetValue(code, out var c)) return c;
            if (depth > 30 || !by.TryGetValue(code, out var e)) return 0;
            double v = 0;
            if (e.Formula.Length > 0) foreach (Match m in Regex.Matches(e.Formula, @"([+-]?)\s*\[([^\]]+)\]")) v += (m.Groups[1].Value == "-" ? -1 : 1) * Eval(m.Groups[2].Value.Trim(), depth + 1);
            else raw.TryGetValue(code, out v);
            val[code] = v; return v;
        }
        foreach (var e in ls) Eval(e.Code, 0);
        return val;
    }

    private static Dictionary<string, double> Coefs(List<EL> ls, string target)
    {
        var by = ls.GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var coef = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        void Go(string code, double c, int depth)
        {
            if (depth > 30 || !by.TryGetValue(code, out var e)) return;
            if (e.Formula.Length == 0) { coef.TryGetValue(code, out var o); coef[code] = o + c; return; }
            foreach (Match m in Regex.Matches(e.Formula, @"([+-]?)\s*\[([^\]]+)\]")) Go(m.Groups[2].Value.Trim(), m.Groups[1].Value == "-" ? -c : c, depth + 1);
        }
        Go(target, 1, 0);
        return coef;
    }

    /// <summary>Mã chỉ tiêu "chênh lệch tiền": 70B nếu có (70 − tiền cuối kỳ CĐKT), không thì 50.</summary>
    private static string GapCode(List<EL> ls) => ls.Any(x => x.Code.Equals("70B", StringComparison.OrdinalIgnoreCase)) ? "70B" : "50";

    private static EngineEval Evaluate(List<EL> ls, Book b, bool detail)
    {
        var ev = new EngineEval { Val = Simulate(ls, b) };
        var gap = GapCode(ls);
        var coef = Coefs(ls, gap);
        if (gap == "50") foreach (var a in b.Leaves.Where(a => IsCash(a.Code))) { }      // 50: kỳ vọng = biến động tiền, xử lý bằng cột "tiền" bên dưới
        var lines = ls.Where(e => e.Formula.Length == 0 && e.Kind != 0 && coef.TryGetValue(e.Code, out var c) && Math.Abs(c) > 1e-9).ToList();

        // độ nhạy của 70B theo số dư cuối kỳ / đầu kỳ của từng TK lá (qua các dòng số dư)
        var dClose = new Dictionary<string, double>(StringComparer.Ordinal); var dOpen = new Dictionary<string, double>(StringComparer.Ordinal);
        var hitClose = new Dictionary<string, List<(string, double)>>(StringComparer.Ordinal);
        foreach (var e in lines.Where(e => e.Kind == 2))
        {
            var sgn = coef[e.Code] * (e.ThuChi == 0 ? -1 : 1);
            var nets = GroupNets(e, b, e.DauCuoi == 2);
            foreach (var a in b.Leaves)
            {
                var t = e.Tk.Count == 0 ? "" : TokenOf(a.Code, e.Tk); if (t is null || !nets.ContainsKey(t)) continue;
                if (e.CongNo == 1 && !CatCongNo(a.Code)) continue;
                var d = sgn * Deriv(e, nets[t]);
                if (d == 0) continue;
                var map = e.DauCuoi == 2 ? dClose : dOpen;
                map.TryGetValue(a.Code, out var o); map[a.Code] = o + d;
                if (e.DauCuoi == 2) { if (!hitClose.TryGetValue(a.Code, out var hl)) hitClose[a.Code] = hl = new(); hl.Add((e.Code, d)); }
            }
        }
        if (gap == "50") foreach (var a in b.Leaves.Where(a => IsCash(a.Code))) { dClose.TryGetValue(a.Code, out var o); dClose[a.Code] = o - 1; }
        double DC(string a) => dClose.TryGetValue(a, out var v) ? v : 0;

        // dòng phát sinh: chỉ tiêu nào hứng cặp (Nợ D / Có C)
        var flow = lines.Where(e => e.Kind == 1).Select(e => (E: e, S: coef[e.Code] * (e.ThuChi == 0 ? -1 : 1))).ToList();
        bool Fits(EL e, string tk, string du) => (e.Tk.Count == 0 || TokenOf(tk, e.Tk) is not null) && (e.Du.Count == 0 || TokenOf(du, e.Du) is not null);

        var imb = new Dictionary<(string, string), PairImb>();
        PairImb P(string d, string c, double x) { if (!imb.TryGetValue((d, c), out var p)) imb[(d, c)] = p = new PairImb { D = d, C = c, X = x }; return p; }
        // bút toán biên của từng TK lãi lỗ (đối ứng ngoài loại 5-9) — để phân bổ lại các dòng hứng bút toán nội bộ 5-9 (vd 05E hứng Nợ 5151 / Có 911)
        var plEdge = new Dictionary<string, List<(string D, string C, double X)>>(StringComparer.Ordinal);
        foreach (var (d, c, x) in b.Pairs)
        {
            if (IsPl(d) == IsPl(c)) continue;
            var p = IsPl(d) ? d : c;
            if (!plEdge.TryGetValue(p, out var l)) plEdge[p] = l = new(); l.Add((d, c, x));
        }
        foreach (var (d, c, x) in b.Pairs)
        {
            double eff = x * (DC(d) - DC(c));
            var hits = new List<(string, double)>();
            foreach (var (e, s) in flow)
            {
                double pick = (e.NoCo == 1 ? Fits(e, d, c) : Fits(e, c, d)) ? x : 0;
                if (pick != 0) hits.Add((e.Code, s * pick));
            }
            bool internalPl = IsPl(d) && IsPl(c);
            if (internalPl)
            {
                // dòng hứng bút toán nội bộ 5-9 → phân bổ về các bút toán biên của TK lãi lỗ không phải 911
                var p = d.StartsWith("911", StringComparison.Ordinal) ? c : d;
                var side = p == d ? "C" : "D";          // Nợ p (kết chuyển số dư Có) ↔ các bút toán ghi Có p; và ngược lại
                var edges = plEdge.TryGetValue(p, out var el) ? el.Where(t => side == "C" ? t.C == p : t.D == p).ToList() : new();
                var tot = edges.Sum(t => t.X);
                foreach (var (code, v) in hits)
                {
                    if (edges.Count == 0 || Math.Abs(tot) < 0.5) { var q = P(d, c, x); q.Imb += v; q.Hits.Add((code, v)); continue; }
                    foreach (var t in edges) { var q = P(t.D, t.C, t.X); var share = v * t.X / tot; q.Imb += share; q.Hits.Add((code, share)); }
                }
                continue;
            }
            // lợi nhuận: bút toán ghi Có TK 5-9 làm lợi nhuận tăng, ghi Nợ làm giảm (tương ứng chỉ tiêu 01 — bút toán kết chuyển 911 ↔ 421 bù lại)
            if (IsPl(c)) eff += x; else if (IsPl(d)) eff -= x;
            var pi = P(d, c, x); pi.Imb += eff + hits.Sum(h => h.Item2);
            foreach (var h in hits) pi.Hits.Add(h);
            foreach (var a in new[] { d, c }) if (hitClose.TryGetValue(a, out var hc)) foreach (var (code, dv) in hc) pi.Hits.Add((code, (a == d ? x : -x) * dv));
        }
        // số dư đầu kỳ: TK có ở dòng cuối kỳ mà không có (hoặc khác) ở dòng đầu kỳ
        foreach (var a in b.Leaves)
        {
            if (Math.Abs(a.OpenNet) < 0.5) continue;
            dOpen.TryGetValue(a.Code, out var o);
            var t = (DC(a.Code) + o) * a.OpenNet;
            if (Math.Abs(t) > 0.5) ev.Open.Add((a.Code, t));
        }
        ev.Pairs = imb.Values.Where(p => Math.Abs(p.Imb) > 0.5).OrderByDescending(p => Math.Abs(p.Imb)).ToList();
        ev.Linear = ev.Pairs.Sum(p => p.Imb) + ev.Open.Sum(o => o.V);
        // mục tiêu: gộp lệch theo TK trung gian (TK bảng cân đối không phải tiền) — chuỗi bút toán qua cùng một TK bù trừ nhau là hợp lệ
        // (vd Nợ 8111 / Có 3339 rồi Nợ 3339 / Có 112: 05A cộng lại + 22B trừ ra — từng cặp lệch nhưng qua TK 3339 thì bằng 0)
        var byAcc = new Dictionary<string, double>(StringComparer.Ordinal);
        void Add(string a, double v) { byAcc.TryGetValue(a, out var o); byAcc[a] = o + v; }
        foreach (var p in ev.Pairs)
        {
            bool mD = !IsCash(p.D) && !IsPl(p.D), mC = !IsCash(p.C) && !IsPl(p.C);
            if (mD && mC) { Add(p.D, p.Imb / 2); Add(p.C, p.Imb / 2); }
            else if (mD) Add(p.D, p.Imb); else if (mC) Add(p.C, p.Imb);
            else Add(IsPl(p.D) ? p.D : p.C, p.Imb);
        }
        ev.Objective = byAcc.Values.Sum(Math.Abs) + ev.Open.Sum(o => Math.Abs(o.V));
        return ev;
    }

    // ------------------------------------------------------------------------------------------ (3) tự thử cách sửa
    private sealed class Edit { public string Text = ""; public List<string> Codes = new(); public Func<List<EL>, List<EL>> Apply = l => l; public bool FromRef; }

    private static List<EL> With(List<EL> ls, string code, Action<EL> f)
    {
        var r = ls.Select(x => x.Clone()).ToList();
        var e = r.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase)); if (e is not null) f(e);
        return r;
    }

    private static string NameKey(EL e)
    {
        var n = Regex.Replace(e.Name ?? "", @"\([^)]*\)", "").ToLowerInvariant().Replace("đầu kỳ", "").Replace("cuối kỳ", "");
        return Regex.Replace(n, @"[^\p{L}\p{N}]+", " ").Trim();
    }

    private static string Flags(EL e) => e.Kind == 1
        ? "Cách tính = 1 (phát sinh), Phân loại = " + e.NoCo + (e.NoCo == 1 ? " (Nợ)" : " (Có)") + ", Thu/Chi = " + e.ThuChi
        : e.Kind == 2 ? "Cách tính = 2 (số dư), Phân loại = " + e.NoCo + ", Đầu/Cuối = " + e.DauCuoi + ", Không âm = " + e.KhongAm + ", Loại = " + e.CongNo + ", Thu/Chi = " + e.ThuChi : "Cách tính = 0";

    private static IEnumerable<Edit> Candidates(List<EL> ls, EngineEval ev, FastDecl? refd)
    {
        var seen = new HashSet<string>();
        Edit? Mk(string key, string text, IEnumerable<string> codes, Func<List<EL>, List<EL>> f, bool fromRef = false) => seen.Add(key) ? new Edit { Text = text, Codes = codes.ToList(), Apply = f, FromRef = fromRef } : null;
        var top = ev.Pairs.Take(8).ToList();
        var accs = top.SelectMany(p => new[] { p.D, p.C }).Concat(ev.Open.OrderByDescending(o => Math.Abs(o.V)).Take(4).Select(o => o.Acc)).Where(a => !IsCash(a)).Distinct().ToList();
        var hitCodes = top.SelectMany(p => p.Hits.Select(h => h.Code)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var by = ls.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);

        // a) để trống dòng phát sinh đang hứng cặp lệch / bỏ đúng TK đó khỏi dòng
        foreach (var code in hitCodes)
        {
            if (!by.TryGetValue(code, out var e) || e.Kind != 1 || e.IsBlank) continue;
            var x = Mk("blank|" + code, "Chỉ tiêu " + code + ": XOÁ TRỐNG ô \"Các tài khoản\" và \"Các tài khoản đối ứng\" (đang là TK " + string.Join(",", e.Tk) + " / đối ứng " + string.Join(",", e.Du) + ") — hoặc xoá dòng và bỏ [" + code + "] khỏi công thức chỉ tiêu cha",
                new[] { code }, l => With(l, code, z => { z.Tk.Clear(); z.Du.Clear(); z.Kind = 0; })); if (x is not null) yield return x;
            foreach (var a in accs)
            {
                foreach (var (list, label) in new[] { (e.Tk, "Các tài khoản"), (e.Du, "Các tài khoản đối ứng") })
                {
                    var t = TokenOf(a, list); if (t is null || list.Count < 2) continue;
                    var isTk = label == "Các tài khoản";
                    x = Mk("rm|" + code + "|" + label + "|" + t, "Chỉ tiêu " + code + ": ô \"" + label + "\" BỎ TK " + t + " (đang là " + string.Join(",", list) + ")",
                        new[] { code }, l => With(l, code, z => (isTk ? z.Tk : z.Du).Remove(t))); if (x is not null) yield return x;
                }
            }
        }
        // b) TK nằm ở 2 dòng số dư cùng nhóm, cùng kỳ → bỏ ở một dòng
        foreach (var a in accs)
        {
            var bal = ls.Where(e => e.Kind == 2 && e.Formula.Length == 0 && TokenOf(a, e.Tk) is not null).ToList();
            foreach (var g in bal.GroupBy(e => (e.DauCuoi, e.NoCo)).Where(g => g.Count() > 1))
                foreach (var e in g)
                {
                    var t = TokenOf(a, e.Tk)!;
                    var partners = ls.Where(o => o.Kind == 2 && o.Code != e.Code && NameKey(o) == NameKey(e) && o.DauCuoi != e.DauCuoi && o.Tk.Contains(t)).Select(o => o.Code).ToList();
                    var codes = new[] { e.Code }.Concat(partners).ToList();
                    var x = Mk("dup|" + e.Code + "|" + t, string.Join(", ", codes.Select(c => "chỉ tiêu " + c)) + ": ô \"Các tài khoản\" BỎ TK " + t + " (TK này đã nằm ở chỉ tiêu " + string.Join(", ", g.Where(o => o != e).Select(o => o.Code)) + " → đang bị tính 2 lần)",
                        codes, l => { var r = l.Select(z => z.Clone()).ToList(); foreach (var z in r.Where(z => codes.Contains(z.Code))) z.Tk.Remove(t); return r; });
                    if (x is not null) yield return x;
                }
        }
        // c) TK chưa nằm ở dòng số dư nào → đưa vào các dòng số dư có TK "anh em" (cùng tiền tố dài nhất), đồng thời bỏ khỏi dòng phát sinh đang hứng
        foreach (var a in accs.Where(a => !IsPl(a)))
        {
            if (ls.Any(e => e.Kind == 2 && TokenOf(a, e.Tk) is not null)) continue;
            int best = 0; var homes = new List<EL>();
            foreach (var e in ls.Where(e => e.Kind == 2 && e.Formula.Length == 0))
                foreach (var t in e.Tk)
                {
                    var k = 0; while (k < Math.Min(t.Length, a.Length) && t[k] == a[k]) k++;
                    if (k < 3) continue;
                    if (k > best) { best = k; homes = new() { e }; } else if (k == best && !homes.Contains(e)) homes.Add(e);
                }
            if (_cat is not null && _cat.TryGetValue(a, out var ca) && ca.Parent.Length > 0)
            {
                var sib = _cat.Values.Where(x => x.Parent == ca.Parent && x.Code != a).Select(x => x.Code).ToList();
                var byParent = ls.Where(e => e.Kind == 2 && e.Formula.Length == 0 && e.Tk.Any(t => sib.Any(s => s.StartsWith(t, StringComparison.Ordinal)))).ToList();
                if (byParent.Count > 0) homes = byParent;       // cùng TK mẹ trong danh mục của khách — chắc hơn so tiền tố
            }
            if (homes.Count == 0) continue;
            var flows = ls.Where(e => e.Kind == 1 && (TokenOf(a, e.Tk) is not null)).ToList();
            var codes = homes.Select(h => h.Code).Concat(flows.Select(f => f.Code)).ToList();
            var x = Mk("home|" + a, string.Join(", ", homes.Select(h => "chỉ tiêu " + h.Code)) + ": ô \"Các tài khoản\" THÊM TK " + a + (flows.Count > 0 ? "; đồng thời " + string.Join(", ", flows.Select(f => "chỉ tiêu " + f.Code + " bỏ TK " + TokenOf(a, f.Tk))) + " (để trống nếu chỉ còn TK đó)" : ""),
                codes, l =>
                {
                    var r = l.Select(z => z.Clone()).ToList();
                    foreach (var z in r.Where(z => homes.Any(h => h.Code == z.Code))) z.Tk.Add(a);
                    foreach (var z in r.Where(z => flows.Any(f => f.Code == z.Code))) { var t = TokenOf(a, z.Tk); if (t is not null) z.Tk.Remove(t); if (z.Tk.Count == 0) { z.Du.Clear(); z.Kind = 0; } }
                    return r;
                });
            if (x is not null) yield return x;
        }
        // d) theo mẫu tham chiếu (ưu tiên mẫu đã chạy đúng của khách): lấy y nguyên các dòng có đụng tới TK đang lệch
        if (refd is null) yield break;
        foreach (var a in accs)
        {
            var refLines = refd.Lines.Where(r => r.Formula.Length == 0 && (TokenOf(a, r.Tk) is not null || TokenOf(a, r.TkDu) is not null) && by.ContainsKey(r.Code)).Select(r => r.Code);
            var mine = ls.Where(e => e.Formula.Length == 0 && (TokenOf(a, e.Tk) is not null || TokenOf(a, e.Du) is not null) && refd.Line(e.Code) is not null).Select(e => e.Code);
            var codes = refLines.Concat(mine).Distinct(StringComparer.OrdinalIgnoreCase).Where(c => Differs(by[c], refd.Line(c)!)).ToList();
            if (codes.Count == 0) continue;
            var x = Mk("ref|" + string.Join(",", codes.OrderBy(c => c)), string.Join("; ", codes.Select(c => RefText(by[c], refd.Line(c)!))), codes, l => AdoptRef(l, refd, codes), true);
            if (x is not null) yield return x;
            foreach (var c in codes)
            {
                x = Mk("ref|" + c, RefText(by[c], refd.Line(c)!), new[] { c }, l => AdoptRef(l, refd, new[] { c }), true);
                if (x is not null) yield return x;
            }
        }
        foreach (var e in ls.Where(e => hitCodes.Contains(e.Code, StringComparer.OrdinalIgnoreCase)))
        {
            var r = refd.Line(e.Code); if (r is null || !Differs(e, r)) continue;
            var x = Mk("ref|" + e.Code, RefText(e, r), new[] { e.Code }, l => AdoptRef(l, refd, new[] { e.Code }), true);
            if (x is not null) yield return x;
        }
    }

    private static bool Differs(EL e, FastLine r) =>
        e.Formula != r.Formula || !e.Tk.OrderBy(x => x).SequenceEqual(r.Tk.OrderBy(x => x)) || !e.Du.OrderBy(x => x).SequenceEqual(r.TkDu.OrderBy(x => x)) ||
        (r.Formula.Length == 0 && (e.Kind != r.Kind || (r.Kind != 0 && (e.NoCo != r.NoCo || e.ThuChi != r.ThuChi)) || (r.Kind == 2 && (e.DauCuoi != r.DauCuoi || e.KhongAm != r.KhongAm || e.CongNo != r.CongNo))));

    private static string RefText(EL e, FastLine r)
    {
        var parts = new List<string>();
        string J(IEnumerable<string> x) => string.Join(",", x);
        if (!e.Tk.OrderBy(x => x).SequenceEqual(r.Tk.OrderBy(x => x)))
        {
            var add = r.Tk.Except(e.Tk).ToList(); var del = e.Tk.Except(r.Tk).ToList();
            parts.Add(r.Tk.Count == 0 ? "ô \"Các tài khoản\" để TRỐNG (đang là " + J(e.Tk) + ")" : "ô \"Các tài khoản\" " + (add.Count > 0 ? "THÊM " + J(add) : "") + (add.Count > 0 && del.Count > 0 ? ", " : "") + (del.Count > 0 ? "BỎ " + J(del) : "") + " → " + J(r.Tk));
        }
        if (!e.Du.OrderBy(x => x).SequenceEqual(r.TkDu.OrderBy(x => x)))
        {
            var add = r.TkDu.Except(e.Du).ToList(); var del = e.Du.Except(r.TkDu).ToList();
            parts.Add(r.TkDu.Count == 0 ? "ô \"Các tài khoản đối ứng\" để TRỐNG (đang là " + J(e.Du) + ")" : "ô \"Các tài khoản đối ứng\" " + (add.Count > 0 ? "THÊM " + J(add) : "") + (add.Count > 0 && del.Count > 0 ? ", " : "") + (del.Count > 0 ? "BỎ " + J(del) : "") + " → " + J(r.TkDu));
        }
        if (e.Formula != r.Formula) parts.Add("ô \"Công thức\" = " + (r.Formula.Length > 0 ? r.Formula : "(trống)") + " (đang là " + (e.Formula.Length > 0 ? e.Formula : "trống") + ")");
        if (r.Formula.Length == 0 && r.Tk.Count + r.TkDu.Count > 0)
        {
            if (e.Kind != r.Kind) parts.Add("ô \"Cách tính\" = " + r.Kind);
            if (e.NoCo != r.NoCo) parts.Add("ô \"Phân loại\" = " + r.NoCo + (r.NoCo == 1 ? " (Nợ)" : " (Có)"));
            if (e.ThuChi != r.ThuChi) parts.Add("ô \"Thu/Chi\" = " + r.ThuChi);
            if (r.Kind == 2 && e.DauCuoi != r.DauCuoi) parts.Add("ô \"Đầu/Cuối\" = " + r.DauCuoi);
            if (r.Kind == 2 && e.KhongAm != r.KhongAm) parts.Add("ô \"Lấy giá trị không âm\" = " + r.KhongAm);
            if (r.Kind == 2 && e.CongNo != r.CongNo) parts.Add("ô \"Loại\" = " + r.CongNo);
        }
        return "Chỉ tiêu " + e.Code + ": " + string.Join("; ", parts);
    }

    private static List<EL> AdoptRef(List<EL> ls, FastDecl refd, IEnumerable<string> codes)
    {
        var r = ls.Select(z => z.Clone()).ToList();
        foreach (var c in codes)
        {
            var e = r.FirstOrDefault(z => z.Code.Equals(c, StringComparison.OrdinalIgnoreCase)); var f = refd.Line(c); if (e is null || f is null) continue;
            e.Formula = f.Formula; e.Tk = new(f.Tk); e.Du = new(f.TkDu);
            e.Kind = f.Formula.Length > 0 || f.Tk.Count + f.TkDu.Count == 0 ? 0 : f.Kind;
            if (f.NoCo > 0) e.NoCo = f.NoCo; if (f.DauCuoi > 0) e.DauCuoi = f.DauCuoi; e.KhongAm = f.KhongAm; e.CongNo = f.CongNo; e.ThuChi = f.ThuChi;
        }
        return r;
    }

    private sealed class FixStep { public string Text = ""; public List<string> Codes = new(); public double GapAfter, ObjBefore, ObjAfter; public List<PairImb> Fixed = new(); }

    private static List<FixStep> SearchFixes(List<EL> start, Book b, FastDecl? refd, double gapOffset, out EngineEval final)
    {
        var steps = new List<FixStep>();
        var cur = start; var ev = Evaluate(cur, b, false);
        for (var it = 0; it < 15 && ev.Objective > 1; it++)
        {
            (Edit E, List<EL> L, EngineEval V)? best = null; double bestScore = 0;
            foreach (var c in Candidates(cur, ev, refd).Take(200))
            {
                var l2 = c.Apply(cur); var v2 = Evaluate(l2, b, false);
                var gain = ev.Objective - v2.Objective;
                var score = gain * (c.FromRef ? 1.02 : 1.0) - c.Codes.Count * 0.01;      // cùng hiệu quả thì ưu tiên mẫu tham chiếu và sửa ít dòng
                if (gain > Math.Max(1, ev.Objective * 0.002) && score > bestScore) { bestScore = score; best = (c, l2, v2); }
            }
            if (best is null) break;
            var (edit, list, val) = best.Value;
            var gapCode = GapCode(list);
            val.Val.TryGetValue(gapCode, out var g);
            var fixedPairs = ev.Pairs.Where(p => !val.Pairs.Any(q => q.D == p.D && q.C == p.C && Math.Abs(q.Imb - p.Imb) < 0.5)).Take(4).ToList();
            steps.Add(new FixStep { Text = edit.Text, Codes = edit.Codes, ObjBefore = ev.Objective, ObjAfter = val.Objective, GapAfter = g + gapOffset, Fixed = fixedPairs });
            cur = list; ev = val;
        }
        final = ev;
        return steps;
    }

    // ------------------------------------------------------------------------------------------ chạy & trình bày
    private static void RunEngine(CfsResult res, List<Line> cfg, Dictionary<string, (double Cur, double Prev)> repAll, List<TbAcc> tb, List<Jr> jr)
    {
        var refd = EngineNoReference ? null : CfsFasts.Reference("cf-indirect", cfg.Select(l => l.Code), cfg.Select(l => l.Name));
        var ls = ToEngine(cfg, refd);
        var b = BuildBook(tb, jr);
        var inferred = ls.Any(e => e.Guessed) ? InferFlags(ls, b, repAll) : 0;
        var ev = Evaluate(ls, b, true);
        var gapCode = GapCode(ls);

        // (1) tính lại như Fast — so với báo cáo
        int ok = 0, bad = 0; var badItems = new List<CfsItem>();
        foreach (var e in ls.Where(e => repAll.ContainsKey(e.Code)))
        {
            ev.Val.TryGetValue(e.Code, out var mine); var rep = repAll[e.Code].Cur;
            if (Math.Abs(mine - rep) < 1) { ok++; continue; }
            bad++;
            if (e.Formula.Length == 0)
                badItems.Add(new CfsItem
                {
                    Severity = "info", Amount = rep - mine,
                    Title = "Chỉ tiêu " + e.Code + ": báo cáo " + N0(rep) + " ≠ tính lại " + N0(mine),
                    Detail = (e.CongNo == 1 ? "Dòng lấy chi tiết một vế công nợ theo TỪNG KHÁCH — bảng cân đối phát sinh tài khoản không có số dư theo khách nên chỉ tính gần đúng; phần chênh này được giữ nguyên khi thử cách sửa. " : "") +
                             "Khai báo: TK " + string.Join(",", e.Tk) + (e.Du.Count > 0 ? " · đối ứng " + string.Join(",", e.Du) : "") + " · " + Flags(e) + (e.Guessed ? " (cờ đoán theo mẫu / tên vì file khai báo thiếu cột)" : "")
                });
        }
        repAll.TryGetValue(gapCode, out var repGap); ev.Val.TryGetValue(gapCode, out var simGap);
        _engStale = badItems.Count(i => !i.Detail.StartsWith("Dòng lấy chi tiết một vế")) > 3;     // nhiều dòng thường (không phải công nợ theo khách) khác báo cáo → báo cáo không phải của khai báo này
        var offset = repGap.Cur - simGap;
        res.Overview.Add(new CfsOverview { Label = "Tính lại như Fast (procedure rs_rptInDirectCashflow)", Value = ok + " chỉ tiêu khớp báo cáo, " + bad + " khác" + (inferred > 0 ? " (suy ra cờ của " + inferred + " dòng từ báo cáo vì file khai báo thiếu cột)" : "") + (bad > 0 ? " — " + gapCode + " tính lại = " + N0(simGap) + ", báo cáo = " + N0(repGap.Cur) : ""), Severity = bad == 0 ? "ok" : Math.Abs(offset) < 1 ? "warn" : "warn" });
        if (badItems.Count > 0)
            res.Sections.Add(new CfsSection { Id = "eng-sim", Area = "cfi", Title = "Tính lại như Fast: " + badItems.Count + " dòng khác báo cáo", Note = "Dòng khác thường do (a) công nợ theo từng khách (bảng cân đối phát sinh tài khoản không có), (b) sổ / báo cáo khác kỳ, (c) file khai báo thiếu cột cờ. Khi thử cách sửa, phần chênh này được giữ nguyên nên con số \"" + gapCode + " dự kiến\" vẫn khớp báo cáo.", Items = badItems.OrderByDescending(i => Math.Abs(i.Amount ?? 0)).Take(30).ToList() });

        // (2) cặp bút toán lệch
        string Nm(string acc) { var n = AccName(acc, tb); return n.Length > 0 ? " (" + (n.Length > 40 ? n[..40] + "…" : n) + ")" : ""; }
        var pairItems = ev.Pairs.Take(25).Select(p => new CfsItem
        {
            Severity = Math.Abs(p.Imb) > 1 ? "error" : "warn", Amount = p.Imb,
            Title = "Nợ " + p.D + Nm(p.D) + " / Có " + p.C + Nm(p.C) + " (" + N0(p.X) + ") đang làm " + gapCode + " lệch " + N0(p.Imb),
            Detail = (IsCash(p.D) || IsCash(p.C) ? "Bút toán CÓ tiền: các chỉ tiêu phải hứng đúng " + N0(p.X) + "." : "Bút toán KHÔNG dùng tiền: tổng các chỉ tiêu hứng phải bằng 0.") +
                     (p.Hits.Count > 0 ? " Đang hứng: " + string.Join("; ", p.Hits.GroupBy(h => h.Code).Select(g => g.Key + " " + N0(g.Sum(h => h.V)))) + "." : " Không chỉ tiêu nào hứng.")
        }).ToList();
        foreach (var (acc, v) in ev.Open.OrderByDescending(o => Math.Abs(o.V)).Take(10))
            pairItems.Add(new CfsItem { Severity = "error", Amount = v, Title = "Số dư ĐẦU KỲ của TK " + acc + " đang làm " + gapCode + " lệch " + N0(v), Detail = "TK có ở dòng số dư cuối kỳ nhưng khác ở dòng đầu kỳ (hoặc ngược lại) — dòng đầu kỳ và cuối kỳ phải cùng danh sách TK." });
        var nonlin = simGap - ev.Linear;
        if (pairItems.Count > 0)
            res.Sections.Add(new CfsSection
            {
                Id = "eng-pairs", Area = "cfi", Title = "Cặp bút toán gây lệch " + gapCode + " (" + ev.Pairs.Count + " cặp; tổng " + N0(ev.Linear) + ")",
                Note = "Mỗi bút toán Nợ / Có không dùng tiền phải làm " + gapCode + " đổi đúng 0; có tiền thì đúng bằng số tiền. Cặp nào khác 0 là chỗ khai báo hứng sai." + (Math.Abs(nonlin) > 1 ? " Phần còn lại " + N0(nonlin) + " do chặn không âm / công nợ một vế (không chia được theo cặp)." : ""),
                Items = pairItems
            });

        // mô hình không tái tạo được báo cáo (thiếu cờ / sổ khác kỳ) → không đưa phương án, tránh chỉ sai
        var trust = Math.Abs(offset) <= Math.Max(1000, Math.Abs(repGap.Cur) * 0.01);
        var flagsComplete = !ls.Any(e => e.Guessed && e.Formula.Length == 0 && e.Tk.Count + e.Du.Count > 0);
        if (!trust && flagsComplete)
        {
            // khai báo đủ cờ → số tự tính từ sổ đáng tin hơn file báo cáo (thường là file báo cáo CŨ chưa chạy lại sau khi sửa khai báo)
            res.Overview.Add(new CfsOverview { Label = "⚠ Báo cáo LCTT đính kèm KHÔNG khớp khai báo hiện tại", Value = "báo cáo " + gapCode + " = " + N0(repGap.Cur) + " nhưng tính theo khai báo đang chọn ra " + N0(simGap) + " — có thể là file báo cáo CŨ. Phương án dưới đây tính theo số tự tính từ sổ; nên chạy lại báo cáo trên Fast và chọn file mới.", Severity = "warn" });
            offset = 0; trust = true;
        }
        if (!trust)
        {
            res.Overview.Add(new CfsOverview { Label = "PHƯƠNG ÁN SỬA (đã tính thử)", Value = "chưa đưa ra — tính lại được " + N0(simGap) + " nhưng báo cáo là " + N0(repGap.Cur) + ". " + (ls.Any(e => e.Guessed) ? "File khai báo thiếu cột cờ (Cách tính, Phân loại, Đầu/Cuối, Loại, Thu/Chi): dán ĐỦ kết quả select * from v20gltc6 where form = '…' (nút Dán…)." : "Kiểm tra bảng kê / bảng cân đối phát sinh có cùng kỳ, cùng đơn vị với báo cáo không."), Severity = "warn" });
            return;
        }
        // (3) tự thử cách sửa
        _engReliable = true; _engGap = simGap + offset; _engRepGap = repGap.Cur;
        if (ev.Objective <= 1)
        {
            _engCorrect = Math.Abs(simGap) < 1;
            res.Sections.RemoveAll(x => x.Id == "eng-pairs" || x.Id == "eng-sim");
            res.Overview.Add(new CfsOverview { Label = "Đối chiếu từng cặp bút toán", Value = "không cặp nào lệch — khai báo hứng đúng mọi bút toán" + (_engCorrect ? ", tính lại từ sổ " + gapCode + " = 0" : ""), Severity = "ok" });
            return;
        }
        var steps = SearchFixes(ls, b, refd, offset, out var fin);
        if (steps.Count == 0) return;
        var items = new List<CfsItem>(); var n = 0;
        foreach (var s in steps)
            items.Add(new CfsItem
            {
                Severity = "error", Amount = s.ObjBefore - s.ObjAfter,
                Title = "Bước " + (++n) + " — " + s.Text,
                Detail = "Sửa xong: " + gapCode + " dự kiến = " + N0(s.GapAfter) + "." + (s.Fixed.Count > 0 ? " Hết lệch ở: " + string.Join("; ", s.Fixed.Select(p => "Nợ " + p.D + " / Có " + p.C + " (" + N0(p.Imb) + ")")) + "." : "")
            });
        var last = steps[^1].GapAfter;
        res.Sections.Add(new CfsSection
        {
            Id = "eng-fix", Area = "cfi", Title = "PHƯƠNG ÁN SỬA — " + steps.Count + " bước, sửa xong " + gapCode + " dự kiến = " + N0(last),
            Note = "Tool đã TÍNH THỬ từng cách sửa trên chính sổ của khách (không đoán theo tên chỉ tiêu) và chỉ giữ cách làm lệch giảm nhiều nhất" + (refd is not null ? "; ưu tiên cách khai của " + (refd.Verified ? "mẫu đã chạy đúng " : "mẫu ") + refd.Form + (refd.Customer.Length > 0 ? " của khách" : " của Fast") : "") + ". Làm lần lượt trên màn hình khai báo của Fast rồi chạy lại báo cáo." +
                   (fin.Objective > 1 ? " Còn " + fin.Pairs.Count + " cặp lệch tool chưa tự tìm được cách sửa (xem mục \"Cặp bút toán gây lệch\")." : ""),
            Items = items
        });
        res.Overview.Add(new CfsOverview { Label = "PHƯƠNG ÁN SỬA (đã tính thử)", Value = steps.Count + " bước → " + gapCode + " dự kiến = " + N0(last), Severity = Math.Abs(last) < 1 ? "ok" : "warn" });
    }

    // ------------------------------------------------------------------------------------------ (4) thư viện mẫu theo khách
    /// <summary>Lưu khai báo LCTT gián tiếp (file / dán) làm "mẫu đã chạy đúng" của khách vào thư viện người dùng. Nếu có báo cáo thì chỉ nhận khi 70B = 0.</summary>
    public static (bool Ok, string Message) SaveCustomerTemplate(string cfgPath, string customer, string? reportPath)
    {
        try
        {
            var cfg = LoadConfig(cfgPath);
            if (cfg.Count == 0) return (false, "Không đọc được khai báo chỉ tiêu.");
            if (!string.IsNullOrEmpty(reportPath) && File.Exists(reportPath))
            {
                var rep = LoadReport(reportPath, 5, 6);
                var gapCode = rep.ContainsKey("70B") ? "70B" : "";
                if (gapCode.Length > 0 && Math.Abs(rep[gapCode].Cur) >= 1)
                    return (false, "Báo cáo đang chọn còn lệch 70B = " + N0(rep[gapCode].Cur) + " — chỉ lưu mẫu khi khai báo đã chạy đúng (70B = 0). Chạy lại báo cáo trên Fast sau khi sửa rồi chọn file báo cáo mới.");
            }
            var refd = CfsFasts.Reference("cf-indirect", cfg.Select(l => l.Code), cfg.Select(l => l.Name));
            var ls = ToEngine(cfg, refd);
            var slug = Regex.Replace(customer.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
            var d = new FastDecl
            {
                Id = "cust-cf-indirect-" + slug, Kind = "cf-indirect", Table = "v20gltc6", Form = "KH " + customer, Circular = refd?.Circular ?? "TT99",
                FormNo = refd?.FormNo ?? "B 03 - DN", Title = "Khai báo LCTT gián tiếp đã chạy đúng của " + customer, Customer = customer, Verified = true,
                BasedOn = refd is null ? "" : refd.Customer.Length > 0 && refd.BasedOn.Length > 0 ? refd.BasedOn : refd.Id,
                Source = "lưu từ " + Path.GetFileName(cfgPath) + (ls.Any(e => e.Guessed) ? " (một số cờ suy theo mẫu " + refd?.Form + " vì file thiếu cột)" : ""), LearnedAt = DateTime.Now.ToString("yyyy-MM-dd"),
                Lines = ls.Select((e, i) => new FastLine { Stt = i + 1, Code = e.Code, Name = e.Name.Trim(), Formula = e.Formula, Tk = new(e.Tk), TkDu = new(e.Du), Kind = e.Kind, NoCo = e.NoCo, DauCuoi = e.DauCuoi, KhongAm = e.KhongAm, CongNo = e.CongNo, ThuChi = e.ThuChi }).ToList()
            };
            var path = CfsFasts.Save(d);
            return (true, "Đã lưu mẫu của khách \"" + customer + "\" (" + d.Lines.Count + " dòng) vào thư viện — lần sau khai báo giống mẫu này sẽ được đối chiếu và đề xuất sửa theo nó. File: " + path);
        }
        catch (Exception ex) { return (false, "Lưu mẫu lỗi: " + ex.Message); }
    }
}
