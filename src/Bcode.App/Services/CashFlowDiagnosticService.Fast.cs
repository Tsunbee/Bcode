using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Đối chiếu khai báo của khách hàng với cách FAST khai báo ở bản release (<see cref="CfsFasts"/>): chỉ tiêu nào Fast có mà khách hàng không có, TK nào Fast khai mà khách hàng bỏ sót
/// (kèm số dư / phát sinh thật trên sổ) hoặc khai thêm. So theo TỔNG TK của chỉ tiêu và các chỉ tiêu con của nó — khách hàng đổi tên / tách / gộp dòng chi tiết (09A…09K) không bị báo oan.
/// </summary>
public static partial class CashFlowDiagnosticService
{
    private static bool TkCover(string a, IEnumerable<string> set) => set.Any(b => a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));

    private static (HashSet<string> Tk, HashSet<string> Du) CfgUnion(Dictionary<string, Line> by, string code, int depth = 0)
    {
        var tk = new HashSet<string>(StringComparer.Ordinal); var du = new HashSet<string>(StringComparer.Ordinal);
        if (depth > 12 || !by.TryGetValue(code, out var l)) return (tk, du);
        foreach (var a in l.Acc) tk.Add(a); foreach (var a in l.Con) du.Add(a);
        foreach (Match m in Regex.Matches(l.Formula ?? "", @"\[([^\]]+)\]")) { var (t, d) = CfgUnion(by, m.Groups[1].Value.Trim(), depth + 1); tk.UnionWith(t); du.UnionWith(d); }
        return (tk, du);
    }

    /// <summary>Các ô trên màn hình "Sửa báo cáo lưu chuyển tiền tệ" của Fast cần đặt theo bản Fast (chỉ ô khác khi khai báo của khách có đủ cột cờ; không có thì liệt kê hết).</summary>
    private static string ScreenFlags(Line cl, FastLine fl, string kind, bool full = false)
    {
        if (kind == "bs") return "";
        // Quy tắc ô theo procedure rs_rptInDirectCashflow (xem Templates/Cfs/source/fast-procedures/README.md):
        //  kind 0 (công thức): chỉ ô Công thức · kind 1 (phát sinh): Phân loại Nợ/Có + Thu/Chi, lọc theo TK và TK đối ứng
        //  kind 2 (số dư): Phân loại + Thu/Chi + Đầu/Cuối + Không âm (+ Loại công nợ chỉ với TK công nợ), không dùng TK đối ứng.
        var o = new List<string>(); bool all = full || !cl.HasFlags || cl.Kind != fl.Kind;     // đổi cách tính / sửa TK → liệt kê đủ các ô có tác dụng
        if (fl.Kind == 0 && (cl.Kind < 0 || cl.Kind == 0)) return "";
        void A(string label, int mine, int want, string txt) { if (all || (mine >= 0 && mine != want)) o.Add("ô \"" + label + "\" = " + want + " (" + txt + ")"); }
        A("Cách tính", cl.Kind, fl.Kind, fl.Kind == 0 ? "tính theo mã số — chỉ nhập ô Công thức" : fl.Kind == 1 ? "tính theo số phát sinh" : "tính theo số dư");
        if (fl.Kind == 0) return string.Join("; ", o);
        if (kind == "cf-indirect")
        {
            A("Phân loại", cl.NoCo, fl.NoCo, fl.Kind == 1 ? (fl.NoCo == 1 ? "Nợ — cộng phát sinh Nợ của TK" : "Có — cộng phát sinh Có của TK") : (fl.NoCo == 1 ? "Nợ — số dư Nợ − Có" : "Có — số dư Có − Nợ"));
            if (fl.Kind == 2) A("Đầu/Cuối", cl.DauCuoi, fl.DauCuoi, fl.DauCuoi == 1 ? "Đầu kỳ" : "Cuối kỳ");
        }
        A("Thu/Chi", cl.ThuChi, fl.ThuChi, fl.ThuChi == 1 ? "Thu — giữ nguyên dấu" : "Chi — đổi dấu thành số âm");
        if (kind == "cf-indirect" && fl.Kind == 2)
        {
            bool congNo = fl.Tk.Any(a => Regex.IsMatch(a, @"^(131|331|1388|3388)")) || fl.CongNo == 1;
            if (congNo) A("Loại", cl.CongNo, fl.CongNo, fl.CongNo == 1 ? "lấy một vế theo từng đối tượng công nợ" : "Không — cấn trừ cả nhóm TK");
            else A("Lấy giá trị không âm", cl.KhongAm, fl.KhongAm, fl.KhongAm == 1 ? "Có — nhóm TK âm tính là 0" : "Không");
            if (congNo && fl.CongNo != 1) A("Lấy giá trị không âm", cl.KhongAm, fl.KhongAm, fl.KhongAm == 1 ? "Có — nhóm TK âm tính là 0" : "Không");
        }
        return string.Join("; ", o);
    }

    private static void CheckAgainstFast(CfsResult res, string kind, string idPrefix, string area, List<Line> cfg, List<TbAcc>? tb)
    {
        var (closest, closeScore) = CfsFasts.Closest(kind, cfg.Select(l => l.Code), cfg.Select(l => l.Name));
        var fast = CfsFasts.Reference(kind, cfg.Select(l => l.Code), cfg.Select(l => l.Name)); if (fast is null || cfg.Count == 0) return;
        if (closest is not null && closeScore > 0.2)
            res.Overview.Add(new CfsOverview
            {
                Label = "Khai báo của khách hàng giống nhất mẫu Fast", Value = closest.Form + " — " + closest.Title + " (" + closest.Circular + (closest.Customer.Length > 0 ? (closest.Verified ? ", mẫu riêng của khách đã chạy đúng" : ", mẫu của khách") : "") + ", " + Math.Round(closeScore * 100) + "% giống)",
                Severity = closest.Circular == "TT99" ? "ok" : "warn"
            });
        if (fast.BasedOn.Length > 0)
        {
            var vd = CfsFasts.VariantDiffs(fast);
            if (vd.Count > 0)
                res.Sections.Add(new CfsSection
                {
                    Id = idPrefix + "-variant", Area = area, Title = "Mẫu " + fast.Form + " đã chạy đúng của khách khác bản V20GLTC605 của khách ở " + vd.Count + " chỗ",
                    Note = "Khai báo của khách giống mẫu " + fast.Form + " (" + fast.Customer + ", đã chạy đúng) — đối chiếu bên dưới lấy mẫu này làm chuẩn. Đây là danh sách TK đặc thù của khách so với bản gốc trong database khách (" + fast.BasedOn + "); khai khác bản gốc ở những chỗ này là có chủ ý.",
                    Items = vd.Select(x => new CfsItem { Severity = "info", Title = x }).ToList()
                });
        }
        var by = cfg.GroupBy(l => l.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var leaves = tb?.Where(a => a.Leaf).ToList();
        bool isBs = kind == "bs";
        double Moved(string acc) => leaves is null ? 0 : leaves.Where(a => AccOverlap(a.Code, acc)).Sum(a => isBs ? Math.Abs(a.CloseNet) + Math.Abs(a.OpenNet) : Math.Abs(a.PsNo) + Math.Abs(a.PsCo));
        // chỉ tiêu "chính thức" của mẫu (mã số thuần hoặc 411a/420a…) — các dòng chi tiết 09A, 131A… do mỗi bản tự tách nên không so từng dòng
        bool Official(string c) => Regex.IsMatch(c, @"^\d{2,3}$") || Regex.IsMatch(c, @"^(411|420)[A-Za-z]$");

        var miss = new List<CfsItem>(); var tkItems = new List<CfsItem>(); var okLines = 0;
        foreach (var fl in fast.Lines.Where(l => Official(l.Code)))
        {
            var rf = FastDecl.Refs(fl.Formula).ToList(); if (rf.Any(Official) || (rf.Count > 0 && rf.All(by.ContainsKey)) || (fl.Formula.Length == 0 && by.ContainsKey(fl.Code))) continue;   // đã so ở từng dòng chi tiết bên dưới                                  // chỉ tiêu tổng của các chỉ tiêu chính thức — lệch (nếu có) đã hiện ở chỉ tiêu con
            var (ft, fd) = fast.Union(fl.Code);
            if (!by.ContainsKey(fl.Code))
            {
                if (ft.Count + fd.Count == 0 && fl.Formula.Length == 0) continue;                    // dòng tiêu đề
                var mv = ft.Sum(Moved);
                miss.Add(new CfsItem
                {
                    Severity = mv > 0.5 ? "error" : "warn", Amount = mv > 0.5 ? mv : null,
                    Title = "Fast có chỉ tiêu " + fl.Code + " " + fl.Name + " — khai báo chưa có",
                    Action = mv > 0.5 ? "Khai chỉ tiêu " + fl.Code + " " + fl.Name + " theo Fast (TK " + string.Join(", ", ft.Take(8)) + " đang có số liệu " + N0(mv) + ")." : "",
                    Detail = "Bản " + fast.Form + " (" + fast.Circular + ") có chỉ tiêu này: " + CfsFasts.Describe(fast, fl.Code) + "."
                });
                continue;
            }
            var (ct, cd) = CfgUnion(by, fl.Code);
            if (ft.Count == 0 && fd.Count == 0) continue;                                            // chỉ tiêu tổng không có TK riêng
            var missTk = ft.Where(a => !TkCover(a, ct)).OrderBy(a => a, StringComparer.Ordinal).ToList();
            var extraTk = ct.Where(a => !TkCover(a, ft)).OrderBy(a => a, StringComparer.Ordinal).ToList();
            var missDu = fd.Where(a => !TkCover(a, cd)).OrderBy(a => a, StringComparer.Ordinal).ToList();
            var extraDu = cd.Where(a => !TkCover(a, fd)).OrderBy(a => a, StringComparer.Ordinal).ToList();
            if (missTk.Count + extraTk.Count + missDu.Count + extraDu.Count == 0) { okLines++; continue; }
            var mvMiss = missTk.Sum(Moved) + (isBs ? 0 : missDu.Sum(Moved)); var mvExtra = extraTk.Sum(Moved);
            var parts = new List<string>();
            if (missTk.Count > 0) parts.Add("thiếu TK " + string.Join(", ", missTk.Take(8)) + (missTk.Count > 8 ? "…" : "") + (mvMiss > 0.5 ? " (có số liệu " + N0(mvMiss) + ")" : ""));
            if (extraTk.Count > 0) parts.Add("khai thêm TK " + string.Join(", ", extraTk.Take(8)) + (extraTk.Count > 8 ? "…" : "") + (mvExtra > 0.5 ? " (có số liệu " + N0(mvExtra) + ")" : ""));
            if (missDu.Count > 0) parts.Add("thiếu TK đối ứng " + string.Join(", ", missDu.Take(8)));
            if (extraDu.Count > 0) parts.Add("khai thêm TK đối ứng " + string.Join(", ", extraDu.Take(8)));
            var worst = mvMiss > 0.5 ? "error" : (mvExtra > 0.5 ? "warn" : "info");
            tkItems.Add(new CfsItem
            {
                Severity = worst, Amount = mvMiss > 0.5 ? mvMiss : mvExtra > 0.5 ? mvExtra : null,
                Title = "Chỉ tiêu " + fl.Code + " " + fl.Name + ": " + string.Join("; ", parts),
                Action = worst == "error" ? "Thêm TK " + string.Join(", ", missTk.Where(a => Moved(a) > 0.5).Take(8)) + " vào chỉ tiêu " + fl.Code + " (Fast khai, đang có số liệu)." : "",
                Detail = "Fast " + fast.Form + ": " + CfsFasts.Describe(fast, fl.Code) + ". Khách hàng: TK " + (ct.Count > 0 ? string.Join(",", ct.Take(14)) : "(không khai)") + (cd.Count > 0 ? " · đối ứng " + string.Join(",", cd.Take(10)) : "") +
                         ". Mục này chỉ là khác với bản Fast chuẩn — khách hàng có thể khai khác có chủ ý; nếu TK có số liệu mà không chỗ nào khác hứng thì mới là sai."
            });
        }
        // ---- so TỪNG DÒNG khai báo (kể cả dòng chi tiết 04A, 04B, 05A…): chỉ rõ phải sửa ở dòng nào, thêm / bớt TK nào
        var leafDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fl in fast.Lines.Where(l => l.Formula.Length == 0 && l.Tk.Count + l.TkDu.Count > 0 && by.ContainsKey(l.Code)))
        {
            var cl = by[fl.Code];
            // khách khai dòng này bằng CÔNG THỨC cộng các dòng con (vd 02 = [02A]) thì so TK theo tổng các dòng con, không so ô TK của chính dòng này
            var cAcc = cl.Acc; var cCon = cl.Con;
            if (cl.HasFormula) { var (ut, ud) = CfgUnion(by, fl.Code); cAcc = ut.ToList(); cCon = ud.ToList(); }
            var missTk = fl.Tk.Where(a => !TkCover(a, cAcc)).ToList(); var extraTk = cAcc.Where(a => !TkCover(a, fl.Tk)).ToList();
            var missDu = fl.Kind == 2 ? new List<string>() : fl.TkDu.Where(a => !TkCover(a, cCon)).ToList(); var extraDu = fl.Kind == 2 ? new List<string>() : cCon.Where(a => !TkCover(a, fl.TkDu)).ToList();
            if (missTk.Count + extraTk.Count + missDu.Count + extraDu.Count == 0) { okLines++; continue; }
            var mvMiss = missTk.Sum(Moved) + (isBs ? 0 : missDu.Sum(Moved)); var mvExtra = extraTk.Sum(Moved);
            var todo = new List<string>(); var why = new List<string>();
            if (missTk.Count > 0) { todo.Add((cl.Acc.Count == 0 ? "khai TK " : "thêm TK ") + string.Join(", ", missTk.Take(8))); why.Add("thiếu TK " + string.Join(", ", missTk.Take(8))); }
            if (missDu.Count > 0) { todo.Add((cl.Con.Count == 0 ? "khai TK đối ứng " : "thêm TK đối ứng ") + string.Join(", ", missDu.Take(8))); why.Add("thiếu TK đối ứng " + string.Join(", ", missDu.Take(8))); }
            if (extraTk.Count > 0) { todo.Add("kiểm tra TK thừa " + string.Join(", ", extraTk.Take(8)) + " (Fast không khai)"); why.Add("thừa TK " + string.Join(", ", extraTk.Take(8))); }
            if (extraDu.Count > 0) { todo.Add("kiểm tra TK đối ứng thừa " + string.Join(", ", extraDu.Take(8)) + " (Fast không khai)"); why.Add("thừa TK đối ứng " + string.Join(", ", extraDu.Take(8))); }
            var worst = mvMiss > 0.5 ? "error" : (mvExtra > 0.5 || missTk.Count + missDu.Count > 0 ? "warn" : "info");
            if (worst != "info") leafDone.Add(fl.Code);
            tkItems.Add(new CfsItem
            {
                Severity = worst, Amount = mvMiss > 0.5 ? mvMiss : mvExtra > 0.5 ? mvExtra : null,
                Title = "Chỉ tiêu " + fl.Code + " " + fl.Name + ": " + string.Join("; ", why),
                Action = worst == "info" ? "" : "Trên Fast mở chỉ tiêu " + fl.Code + " (Sửa) và điền: " + string.Join("; ", new[] { missTk.Count > 0 ? "ô \"Các tài khoản\" = " + string.Join(",", fl.Tk) + (cl.Acc.Count > 0 ? " (đang là " + string.Join(",", cl.Acc) + ")" : " (đang trống)") : extraTk.Count > 0 ? "ô \"Các tài khoản\": BỎ TK " + string.Join(",", extraTk) + " (đang là " + string.Join(",", cl.Acc) + ")" : "", missDu.Count > 0 ? "ô \"Các tài khoản đối ứng\" = " + string.Join(",", fl.TkDu) + (cl.Con.Count > 0 ? " (đang là " + string.Join(",", cl.Con) + ")" : " (đang trống)") : extraDu.Count > 0 ? "ô \"Các tài khoản đối ứng\": BỎ TK " + string.Join(",", extraDu) + " (đang là " + string.Join(",", cl.Con) + ")" : "", (cl.HasFormula ? "" : ScreenFlags(cl, fl, kind, true)) }.Where(x => x.Length > 0)) + (mvMiss > 0.5 ? " — có số liệu " + N0(mvMiss) + "." : "."),
                Detail = "Fast " + fast.Form + ": " + CfsFasts.Describe(fast, fl.Code) + ". Khách hàng: TK " + (cl.Acc.Count > 0 ? string.Join(",", cl.Acc) : "(không khai)") + (cl.Con.Count > 0 ? " · đối ứng " + string.Join(",", cl.Con) : "") + ". Khác Fast chưa chắc là sai nếu khách khai khác có chủ ý."
            });
        }
        // ---- cờ khai báo (chỉ khi file khai báo có đủ cột của bảng v20gltc…): công nợ một vế, không âm, bên Nợ/Có, đầu/cuối kỳ, thu/chi, cách tính ----
        var flagItems = new List<CfsItem>();
        foreach (var fl in fast.Lines.Where(l => l.Tk.Count + l.TkDu.Count > 0))
        {
            if (!by.TryGetValue(fl.Code, out var cl) || !cl.HasFlags || cl.HasFormula || leafDone.Contains(fl.Code)) continue;
            var diffs = new List<string>();
            void D(string label, int mine, int theirs, Func<int, string> txt) { if (mine >= 0 && mine != theirs) diffs.Add(label + ": khách hàng " + txt(mine) + " ≠ Fast " + txt(theirs)); }
            string YN(int v) => v == 1 ? "có" : "không";
            if (kind == "bs") { D("Phân loại", cl.TsNv, fl.TsNv, v => v == 1 ? "tài sản" : "nguồn vốn"); D("Lấy chi tiết một vế công nợ", cl.CongNo, fl.CongNo, YN); D("Chỉ lấy giá trị không âm", cl.KhongAm, fl.KhongAm, YN); D("Cách tính", cl.Kind, fl.Kind, v => v == 0 ? "theo mã số" : "theo số dư"); }
            else if (kind == "cf-indirect") { D("Phân loại Nợ/Có", cl.NoCo, fl.NoCo, v => v == 1 ? "Nợ" : "Có"); D("Đầu/Cuối", cl.DauCuoi, fl.DauCuoi, v => v == 1 ? "đầu kỳ" : "cuối kỳ"); D("Lấy chi tiết một vế công nợ", cl.CongNo, fl.CongNo, YN); D("Lấy giá trị không âm", cl.KhongAm, fl.KhongAm, YN); D("Thu/Chi", cl.ThuChi, fl.ThuChi, v => v == 1 ? "thu" : "chi"); D("Cách tính", cl.Kind, fl.Kind, v => v == 0 ? "theo mã số" : v == 1 ? "theo số phát sinh" : "theo số dư"); }
            else { D("Thu/Chi", cl.ThuChi, fl.ThuChi, v => v == 1 ? "thu" : "chi"); D("Cách tính", cl.Kind, fl.Kind, v => v == 0 ? "theo mã số" : v == 1 ? "theo số phát sinh" : "theo số dư"); }
            var fixTxt = ScreenFlags(cl, fl, kind); if (diffs.Count == 0 || fixTxt.Length == 0) continue;
            var (ft, _) = fast.Union(fl.Code); var mv = ft.Sum(Moved);
            flagItems.Add(new CfsItem
            {
                Severity = mv > 0.5 ? "warn" : "info", Amount = mv > 0.5 ? mv : null,
                Title = "Chỉ tiêu " + fl.Code + " " + fl.Name + " — cờ khai báo khác Fast: " + fixTxt,
                Action = mv > 0.5 ? "Trên Fast mở chỉ tiêu " + fl.Code + " (Sửa) và đặt: " + ScreenFlags(cl, fl, kind) + " — ảnh hưởng cách lấy số của TK " + string.Join(", ", ft.Take(6)) + "." : "",
                Detail = "Fast " + fast.Form + " khai báo: " + CfsFasts.Describe(fast, fl.Code) + ". Cờ đổi cách tính (vd lấy chi tiết một vế công nợ, không âm) làm số khác dù cùng TK."
            });
        }
        if (flagItems.Count > 0) tkItems.AddRange(flagItems);
        res.Overview.Add(new CfsOverview { Label = "So với Fast " + fast.Form + " (" + fast.Circular + "): chỉ tiêu có TK khớp", Value = okLines + " khớp, " + tkItems.Count(i => i.Severity != "info") + " lệch TK có số liệu, " + miss.Count(i => i.Severity != "info") + " thiếu chỉ tiêu" });
        var all = miss.Concat(tkItems).OrderBy(i => i.Severity == "error" ? 0 : i.Severity == "warn" ? 1 : 2).ThenByDescending(i => Math.Abs(i.Amount ?? 0)).ToList();
        if (all.Count > 0)
            res.Sections.Add(new CfsSection
            {
                Id = idPrefix + "-fast", Area = area, Title = "Khai báo so với cách Fast khai báo (" + fast.Form + ", " + fast.Circular + ") — " + all.Count(i => i.Severity != "info") + " chỗ cần xem",
                Note = "Tham chiếu: " + fast.Title + (fast.Customer.Length > 0 ? (fast.Verified ? " (mẫu riêng của khách đã chạy đúng)" : " (mẫu của khách)") : "") + " — học từ " + fast.Source + ". So tổng TK của từng chỉ tiêu cùng các dòng chi tiết của nó.",
                Items = all
            });
    }
}
