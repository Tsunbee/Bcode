using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Bảng đối chiếu TỪNG chỉ tiêu: mẫu chuẩn (TT99) ↔ khai báo của khách hàng ↔ bản Fast release. Mỗi chỉ tiêu của chuẩn một dòng, ghi rõ khớp / lệch ở đâu —
/// để người dùng đọc một mạch từ chỉ tiêu 01 đến hết, không phải ghép từ các mục rời rạc.
/// </summary>
public static partial class CashFlowDiagnosticService
{
    /// <summary>Nhãn thông tư của khai báo khách hàng (đoán theo mẫu Fast giống nhất), dùng đặt tên nhóm kết quả.</summary>
    private static string CircularTag(string kind, List<Line>? cfg, string fallback)
    {
        if (cfg is null || cfg.Count == 0) return fallback;
        var (d, sc) = CfsFasts.Closest(kind, cfg.Select(l => l.Code), cfg.Select(l => l.Name));
        return d is not null && sc > 0.2 && d.Circular.Length > 0 ? d.Circular + " · khai báo " + d.Form : fallback;
    }

    private static void CheckLineByLine(CfsResult res, string kind, string idPrefix, string area, List<Line> cfg, List<TbAcc>? tb)
    {
        var std = CfsStandards.Get(CfsStandards.ActiveId(kind)); if (std is null || cfg.Count == 0) return;
        var circ = std.Circular.Split('/')[0];
        var fast = CfsFasts.Reference(kind, cfg.Select(l => l.Code), cfg.Select(l => l.Name)) ?? CfsFasts.ForStandard(std);   // mẫu Fast (gốc hoặc đặc thù khách) giống khai báo nhất
        var fname = fast is null || fast.Customer.Length == 0 ? "bản Fast" : fast.Verified ? "mẫu đã chạy đúng của khách (" + fast.Form + ")" : "mẫu " + fast.Form + " của khách";
        var by = cfg.GroupBy(l => l.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var leaves = tb?.Where(a => a.Leaf).ToList(); bool isBs = kind == "bs";
        double Moved(string acc) => leaves is null ? 0 : leaves.Where(a => AccOverlap(a.Code, acc)).Sum(a => isBs ? Math.Abs(a.CloseNet) + Math.Abs(a.OpenNet) : Math.Abs(a.PsNo) + Math.Abs(a.PsCo));
        string Cut(IEnumerable<string> x, int n = 12) { var l = x.ToList(); return string.Join(", ", l.Take(n)) + (l.Count > n ? "…" : ""); }

        var items = new List<CfsItem>(); int ok = 0, bad = 0, none = 0, same = 0;
        var cashAcc = new[] { "111", "112", "113", "12811", "12881" };
        foreach (var sl in std.Lines)
        {
            var title0 = sl.Code + " " + sl.Name;
            var stdTk = sl.Sources.SelectMany(s => s.Acc).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
            var stdTxt = sl.Formula.Count > 0 ? "công thức " + sl.Code + " = " + string.Join(" + ", sl.Formula) : stdTk.Count > 0 ? "TK " + Cut(stdTk) : "(chuẩn không nêu TK)";
            var fl0 = fast?.Line(sl.Code);
            var fastTxt = fast is null ? "" : fl0 is not null && fl0.Formula.Length > 0 ? "công thức " + fl0.Formula : CfsFasts.Describe(fast, sl.Code);
            var tail = " Fast" + (fast is null ? "" : " " + fast.Form) + ": " + (fastTxt.Length > 0 ? fastTxt : "(không có)") + ".";

            if (!by.TryGetValue(sl.Code, out var cl))
            {
                if (!string.IsNullOrEmpty(sl.Pair) && by.ContainsKey(sl.Pair)) { ok++; continue; }          // gộp ngắn / dài hạn vào 1 chỉ tiêu
                if (cfg.Any(c => BaseCode(c.Code) == sl.Code)) { ok++; continue; }                          // tách thành 09A, 09B…
                none++;
                items.Add(new CfsItem { Severity = sl.Status == "new" ? "info" : "warn", Title = "✖ " + title0 + " — khai báo CHƯA CÓ chỉ tiêu này", Detail = "Chuẩn " + circ + ": " + stdTxt + "." + tail });
                continue;
            }
            if (sl.Formula.Count > 0)
            {
                var mine = new HashSet<string>(Regex.Matches(cl.Formula ?? "", @"\[([^\]]+)\]").Select(m => m.Groups[1].Value.Trim()), StringComparer.OrdinalIgnoreCase);
                if (mine.Count == 0 && !cl.HasFormula) { ok++; continue; }
                var want = new HashSet<string>(sl.Formula, StringComparer.OrdinalIgnoreCase);
                var lack = want.Where(w => !mine.Contains(w) && !mine.Any(m => BaseCode(m) == w)).ToList(); var more = mine.Where(m => !want.Contains(m) && !want.Contains(BaseCode(m))).ToList();
                if (lack.Count + more.Count == 0) { ok++; continue; }
                var fastRefs = fl0 is null ? null : new HashSet<string>(FastDecl.Refs(fl0.Formula), StringComparer.OrdinalIgnoreCase);
                if (fastRefs is not null && fastRefs.SetEquals(mine))
                {
                    same++;
                    items.Add(new CfsItem { Severity = "warn", Title = "≈ " + title0 + " — công thức khác chuẩn (" + (lack.Count > 0 ? "thiếu " + string.Join(", ", lack) : "") + (more.Count > 0 ? (lack.Count > 0 ? "; " : "") + "thừa " + string.Join(", ", more) : "") + ") nhưng GIỐNG HỆT " + fname + (fast is not null && fast.Verified ? " — đây là cách khai riêng của khách, không phải lỗi" : fast is not null && fast.Customer.Length > 0 ? " — mẫu của khách cũng khai vậy (chưa chắc đúng)" : " — lỗi nằm ở bản Fast release"), Detail = "Chuẩn " + circ + ": " + stdTxt + ". Khách hàng: " + cl.Formula + "." + tail });
                    continue;
                }
                bad++;
                items.Add(new CfsItem
                {
                    Severity = "error", Title = "✖ " + title0 + " — công thức khác chuẩn: " + (lack.Count > 0 ? "thiếu " + string.Join(", ", lack) + "; " : "") + (more.Count > 0 ? "thừa " + string.Join(", ", more) : ""),
                    Detail = "Chuẩn " + circ + ": " + stdTxt + ". Khách hàng: " + cl.Formula + "." + tail
                });
                continue;
            }
            if (stdTk.Count == 0 || sl.Status == "confirm") { ok++; continue; }
            var (ct, cd) = CfgUnion(by, sl.Code); var mineAll = new HashSet<string>(ct.Concat(cd), StringComparer.Ordinal);
            var miss = stdTk.Where(a => !TkCover(a, mineAll)).ToList(); var extra = mineAll.Where(a => !TkCover(a, stdTk) && (isBs || !cashAcc.Contains(a))).OrderBy(a => a, StringComparer.Ordinal).ToList();
            if (miss.Count + extra.Count == 0) { ok++; continue; }
            bool sameFast = false;
            if (fast is not null && fl0 is not null) { var (ft, fd) = fast.Union(sl.Code); var fa = new HashSet<string>(ft.Concat(fd), StringComparer.Ordinal); sameFast = mineAll.All(a => TkCover(a, fa)) && fa.All(a => TkCover(a, mineAll)); }
            if (sameFast) same++; else bad++;
            var mvMiss = miss.Sum(Moved); var mvExtra = extra.Sum(Moved);
            var parts = new List<string>();
            if (miss.Count > 0) parts.Add("thiếu TK " + Cut(miss, 8) + (mvMiss > 0.5 ? " (có số liệu " + N0(mvMiss) + ")" : ""));
            if (extra.Count > 0) parts.Add("khai thêm TK " + Cut(extra, 8) + (mvExtra > 0.5 ? " (có số liệu " + N0(mvExtra) + ")" : ""));
            items.Add(new CfsItem
            {
                Severity = sameFast ? "info" : mvMiss > 0.5 ? "error" : "warn", Amount = !sameFast && mvMiss > 0.5 ? mvMiss : null,
                Title = (sameFast ? "≈ " : "✖ ") + title0 + " — " + string.Join("; ", parts) + (sameFast ? " — khác chuẩn nhưng GIỐNG HỆT " + fname : " — khác cả chuẩn lẫn " + fname),
                Detail = "Chuẩn " + circ + ": " + stdTxt + ". Khách hàng: TK " + (ct.Count > 0 ? Cut(ct.OrderBy(a => a, StringComparer.Ordinal)) : "(không khai)") + (cd.Count > 0 ? " · đối ứng " + Cut(cd.OrderBy(a => a, StringComparer.Ordinal)) : "") + "." + tail
                       + " Khác chuẩn chưa chắc là sai — sai khi số liệu của TK đó không có chỗ nào khác hứng."
            });
        }
        res.Overview.Add(new CfsOverview { Label = "Đối chiếu từng chỉ tiêu với chuẩn " + circ, Value = ok + " khớp chuẩn · " + same + " khác chuẩn nhưng giống Fast · " + bad + " khác cả Fast · " + none + " chưa khai", Severity = bad + none == 0 ? (same == 0 ? "ok" : "warn") : bad > 0 ? "error" : "warn" });
        if (items.Count > 0)
            res.Sections.Add(new CfsSection
            {
                Id = idPrefix + "-line", Area = area, Title = "Đối chiếu từng chỉ tiêu: chuẩn " + circ + " ↔ khai báo của khách (" + (bad + same + none) + " chỉ tiêu khác chuẩn / " + std.Lines.Count + ")",
                Note = "Mỗi dòng là một chỉ tiêu của mẫu chuẩn " + circ + " (" + std.Form + "); chỉ hiện chỉ tiêu KHÁC chuẩn (✖ khác cả Fast = khách tự sửa / sai khai; ≈ giống hệt Fast = cách khai của bản Fast release, không phải lỗi riêng khách) — " + ok + " chỉ tiêu còn lại khớp chuẩn. Mỗi dòng ghi: chuẩn nói gì, khách hàng khai gì, Fast khai gì.",
                Items = items
            });
    }

    /// <summary>
    /// TK bị khai ở HAI chỉ tiêu cùng nhóm số dư (vd 3388 ở 11C và 338811 ở 11A) → biến động của TK đó bị cộng 2 lần, làm LCTT lệch đúng bằng biến động đó.
    /// Chỉ so các dòng cùng tên gốc và cùng kỳ (đầu kỳ / cuối kỳ), không có TK đối ứng, không phải công thức.
    /// </summary>
    private static void CheckDoubleCount(CfsResult res, List<Line> cfg, List<TbAcc>? tb)
    {
        var leaves = tb?.Where(a => a.Leaf).ToList();
        var fast = CfsFasts.Reference("cf-indirect", cfg.Select(l => l.Code), cfg.Select(l => l.Name));
        string Key(Line l)
        {
            var n = Regex.Replace(l.Name ?? "", @"\([^)]*\)", "").ToLowerInvariant();
            var per = n.Contains("đầu kỳ") ? "D" : n.Contains("cuối kỳ") ? "C" : "";
            return per.Length == 0 ? "" : per + "|" + Regex.Replace(n, @"[^\p{L}\p{N}]+", " ").Trim();
        }
        var cand = cfg.Where(l => !l.HasFormula && l.Acc.Count > 0 && l.Con.Count == 0 && Key(l).Length > 0).ToList();
        var items = new List<CfsItem>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grp in cand.GroupBy(Key))
        {
            var ls = grp.ToList();
            for (var i = 0; i < ls.Count; i++)
                for (var j = i + 1; j < ls.Count; j++)
                    foreach (var a in ls[i].Acc)
                        foreach (var b in ls[j].Acc.Where(b => AccOverlap(a, b)))
                        {
                            bool inI = fast?.Line(ls[i].Code)?.Tk.Any(t => AccOverlap(t, a)) ?? false, inJ = fast?.Line(ls[j].Code)?.Tk.Any(t => AccOverlap(t, b)) ?? false;
                            // dòng nào Fast không khai TK đó là dòng thêm sai; không phân định được thì bỏ TK rộng hơn (tiền tố ngắn hơn)
                            bool dropJ = inI && !inJ ? true : !inI && inJ ? false : b.Length <= a.Length;
                            var (keep, drop, tkDrop, tkKeep) = dropJ ? (ls[i], ls[j], b, a) : (ls[j], ls[i], a, b);
                            if (!seen.Add(drop.Code + "|" + tkDrop + "|" + keep.Code)) continue;
                            var longer = a.Length >= b.Length ? a : b;
                            var mv = leaves is null ? 0 : leaves.Where(x => AccOverlap(x.Code, longer)).Sum(x => Math.Abs(x.Delta));
                            items.Add(new CfsItem
                            {
                                Severity = mv > 0.5 ? "error" : "warn", Amount = mv > 0.5 ? mv : null,
                                Title = "Chỉ tiêu " + drop.Code + ": TK " + tkDrop + " trùng với TK " + tkKeep + " của chỉ tiêu " + keep.Code + " — biến động bị cộng 2 lần",
                                Action = "Trên Fast mở chỉ tiêu " + drop.Code + " (Sửa) và BỎ TK " + tkDrop + " khỏi ô \"Các tài khoản\" — TK này đã nằm ở chỉ tiêu " + keep.Code + " nên bị tính 2 lần" + (mv > 0.5 ? " (biến động " + N0(mv) + ")" : "") + ".",
                                Detail = "Hai chỉ tiêu cùng nhóm \"" + grp.Key.Substring(2) + "\" (" + (grp.Key[0] == 'D' ? "đầu kỳ" : "cuối kỳ") + ") nên số dư của TK trùng được cộng vào cả hai, khi tính chênh lệch đầu / cuối kỳ biến động của TK bị lặp lại." +
                                         (fast is null ? "" : " Fast " + fast.Form + ": chỉ tiêu " + drop.Code + " " + (fast.Line(drop.Code)?.Tk.Any(t => AccOverlap(t, tkDrop)) == true ? "có" : "KHÔNG khai") + " TK " + tkDrop + ".")
                            });
                        }
        }
        if (items.Count > 0)
            res.Sections.Add(new CfsSection { Id = "cf-dup", Area = "cfi", Title = "TK bị khai trùng ở 2 chỉ tiêu (tính 2 lần) — " + items.Count + " chỗ", Note = "Đây là nguyên nhân thường gặp khi LCTT lệch lớn bất thường: cùng một TK nằm ở hai dòng cùng nhóm số dư.", Items = items.OrderByDescending(i => i.Amount ?? 0).ToList() });
    }
}
