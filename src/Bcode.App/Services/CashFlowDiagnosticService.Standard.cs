using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Đối chiếu khai báo của khách hàng với MẪU CHUẨN (<see cref="CfsStandards"/>, gốc là Thông tư 99/2025/TT-BTC): đi từ chuẩn xuống từng khách hàng, không đi từ khai báo của
/// một doanh nghiệp rồi suy ra — nên không bị thiên kiến theo cách làm riêng của khách hàng đó.
/// </summary>
public static partial class CashFlowDiagnosticService
{
    // Cấu trúc / TK nguồn / dấu lấy từ mẫu chuẩn (người dùng ghi nhận lại thì có hiệu lực ngay); không đọc được thì dùng hằng số cũ.
    private static Dictionary<string, string[]> Tt99Balance => CfsStandards.Parents(CfsStandards.ActiveId("bs")) ?? Tt99BalanceFallback;
    private static Dictionary<string, string[]> Tt99CashFlow => CfsStandards.Parents(CfsStandards.ActiveId("cf-indirect")) ?? Tt99CashFlowFallback;
    private static Dictionary<string, string[]> Tt99CashFlowDirect => CfsStandards.Parents(CfsStandards.ActiveId("cf-direct")) ?? Tt99CashFlowDirectFallback;
    private static Dictionary<string, string[]> CashFlowSourceAccounts => CfsStandards.IndirectSources() ?? CashFlowSourceAccountsFallback;
    private static string[] CashFlowPositive => CfsStandards.Flows(CfsStandards.ActiveId("cf-indirect"))?.Positive ?? CashFlowPositiveFallback;
    private static string[] CashFlowNegative => CfsStandards.Flows(CfsStandards.ActiveId("cf-indirect"))?.Negative ?? CashFlowNegativeFallback;
    private static string[] CashFlowPositiveDirect => CfsStandards.Flows(CfsStandards.ActiveId("cf-direct"))?.Positive ?? CashFlowPositiveDirectFallback;
    private static string[] CashFlowNegativeDirect => CfsStandards.Flows(CfsStandards.ActiveId("cf-direct"))?.Negative ?? CashFlowNegativeDirectFallback;

    private static string BaseCode(string code) { var m = Regex.Match(code ?? "", @"^\d+"); return m.Success ? m.Value : code ?? ""; }

    private static bool AccOverlap(string acc, string prefix) => acc.StartsWith(prefix, StringComparison.Ordinal) || prefix.StartsWith(acc, StringComparison.Ordinal);

    /// <summary>
    /// 1) Phiên bản mẫu: khai báo CĐKT còn theo TT200 (có 270, chưa có 280) → liệt kê từng chỉ tiêu cần đổi mã / đổi tên / thêm / bỏ theo bảng thay đổi của chuẩn.
    /// 2) Chỉ tiêu của chuẩn mà khai báo chưa có (kèm số dư TK nguồn nếu có biến động) và chỉ tiêu ngoài chuẩn.
    /// 3) CĐKT: mỗi TK có số dư trong bảng cân đối phát sinh — chuẩn xếp vào chỉ tiêu nào, khách hàng khai vào chỉ tiêu nào (không khai / khác chuẩn).
    /// </summary>
    private static void CheckAgainstStandard(CfsResult res, string stdId, string idPrefix, string area, List<Line> cfg, List<TbAcc>? tb)
    {
        var std = CfsStandards.Get(stdId); if (std is null || cfg.Count == 0) return;      // stdId = mẫu đang áp dụng (CfsStandards.ActiveId)
        var codes = new HashSet<string>(cfg.Select(l => l.Code), StringComparer.OrdinalIgnoreCase);
        var leaves = tb?.Where(a => a.Leaf).ToList();
        var circ = std.Circular.Split('/')[0];

        // ---- 1) mẫu cũ TT200
        if (std.Kind == "bs" && std.Diffs.Count > 0 && codes.Contains("270") && !codes.Contains("280"))
        {
            var items = new List<CfsItem>();
            foreach (var d in std.Diffs)
            {
                if (d.From.Length > 0 && codes.Contains(d.From))
                {
                    var l = cfg.First(x => x.Code.Equals(d.From, StringComparison.OrdinalIgnoreCase));
                    if (d.To.Length == 0)
                        items.Add(new CfsItem { Severity = "error", Title = "Chỉ tiêu " + d.From + " " + l.Name + " không còn trong " + circ, Action = "Bỏ chỉ tiêu " + d.From + " (" + d.Text + ") — " + circ + " không có; số liệu chuyển về chỉ tiêu tương ứng của chuẩn.", Detail = "Chuẩn " + circ + ": " + d.Change + " — " + d.Text + "." });
                    else if (d.From != d.To || d.Change.Contains("tên"))
                        items.Add(new CfsItem { Severity = d.From != d.To ? "error" : "warn", Title = "Chỉ tiêu " + d.From + " " + l.Name + " → " + (d.From != d.To ? "mã " + d.To : "đổi tên"), Action = (d.From != d.To ? "Đổi mã chỉ tiêu " + d.From + " thành " + d.To : "Đổi tên chỉ tiêu " + d.From) + " — " + d.Text + ".", Detail = "Chuẩn " + circ + ": " + d.Change + " — " + d.Text + "." });
                }
                else if (d.From.Length == 0)
                    items.Add(new CfsItem { Severity = "warn", Title = "Chỉ tiêu mới " + d.To + " của " + circ + " chưa có trong khai báo", Action = "Thêm chỉ tiêu " + d.To + ": " + d.Text + ".", Detail = "Chỉ tiêu mới trong " + circ + " — khai báo (có thể bằng 0) để tổng khớp mẫu." });
            }
            var tl = new List<string>();
            foreach (var c in new[] { "270", "430", "421", "421a", "421b", "422" }) if (codes.Contains(c)) tl.Add(c);
            res.Overview.Add(new CfsOverview { Label = "CĐKT: khai báo đang theo mẫu TT200 (có " + string.Join(", ", tl) + ")", Value = items.Count + " chỉ tiêu cần chuyển sang " + circ, Severity = "error" });
            if (items.Count > 0)
                res.Sections.Add(new CfsSection
                {
                    Id = idPrefix + "-ver", Area = area, Title = "Khai báo CĐKT còn theo mẫu TT200 — chưa chuyển sang chuẩn " + circ,
                    Note = "Đối chiếu từ bảng thay đổi TT200 → " + circ + " của mẫu chuẩn (đổi mã, đổi tên, thêm mới, bỏ). Mọi chỉ tiêu cha / tổng cũng đổi mã (270 → 280…), nên công thức và tổng bị lệch nếu chỉ sửa một phần.",
                    Items = items
                });
            return;     // mã khác nhau — các bước dưới theo mã TT99 không còn ý nghĩa
        }

        var stdCodes = new HashSet<string>(std.Lines.Select(l => l.Code), StringComparer.OrdinalIgnoreCase);
        bool ChildOfStd(string c) => stdCodes.Contains(BaseCode(c)) && !stdCodes.Contains(c);   // 09L1, 10A…

        // ---- 2) thiếu / thừa chỉ tiêu so với chuẩn
        var miss = new List<CfsItem>();
        foreach (var l in std.Lines.Where(x => x.Status != "confirm" && !codes.Contains(x.Code) && !cfg.Any(c => BaseCode(c.Code) == x.Code && !c.Code.Equals(x.Code, StringComparison.OrdinalIgnoreCase))))
        {
            if (!string.IsNullOrEmpty(l.Pair) && codes.Contains(l.Pair)) continue;       // khách hàng gộp ngắn / dài hạn vào 1 chỉ tiêu: lệch số (nếu có) do mục "TK xếp sai chỉ tiêu" bắt
            var accs = l.Sources.SelectMany(s => s.Acc).Distinct().ToList();
            miss.Add(new CfsItem
            {
                Severity = l.Status == "new" ? "info" : "warn",
                Title = "Chuẩn " + circ + " có chỉ tiêu " + l.Code + " " + l.Name + " — khai báo chưa có",
                Detail = (l.Formula.Count > 0 ? "Chỉ tiêu tổng: " + l.Code + " = " + string.Join(" + ", l.Formula) + ". " : "") + (accs.Count > 0 ? "TK nguồn chuẩn: " + string.Join(", ", accs) + ". " : "") + (l.Note.Length > 0 ? l.Note : "")
            });
        }
        var extra = cfg.Where(c => !stdCodes.Contains(c.Code) && !ChildOfStd(c.Code)).Select(c => new CfsItem
        {
            Severity = "info", Title = "Chỉ tiêu " + c.Code + " " + c.Name + " nằm ngoài chuẩn " + circ,
            Detail = "Chỉ tiêu riêng của khách hàng / Fast — hợp lệ nếu là dòng chi tiết thêm, nhưng nếu có số và được cộng vào chỉ tiêu tổng thì dễ bị tính trùng với chỉ tiêu chuẩn."
        }).ToList();
        if (miss.Count > 0 || extra.Count > 0)
            res.Sections.Add(new CfsSection
            {
                Id = idPrefix + "-struct", Area = area, Title = "Khai báo so với danh mục chỉ tiêu của chuẩn " + circ + " (" + miss.Count(i => i.Severity != "info") + " thiếu, " + extra.Count + " ngoài chuẩn)",
                Note = "Mẫu chuẩn: " + std.Title + (std.Verified ? "" : " — chưa xác nhận hết với Phụ lục " + circ + " (xem tab Mẫu chuẩn)") + ".",
                Items = miss.OrderBy(i => i.Severity == "error" ? 0 : i.Severity == "warn" ? 1 : 2).Concat(extra).ToList()
            });

        // ---- 3) CĐKT: TK có số dư → chỉ tiêu chuẩn ↔ chỉ tiêu khai báo
        if (std.Kind != "bs" || leaves is null) return;
        var entries = std.Lines.SelectMany(l => l.Sources.Where(s => s.Side == "N" || s.Side == "C").SelectMany(s => s.Acc.Select(a => (Prefix: a, Side: s.Side, Code: l.Code, Pair: l.Pair)))).ToList();
        var declared = cfg.Where(l => !l.HasFormula && l.Acc.Count > 0).ToList();
        var findings = new List<CfsItem>();
        var okAcc = 0;
        foreach (var a in leaves.Where(x => x.Code.Length > 0 && x.Code[0] >= '1' && x.Code[0] <= '4'))
        {
            bool problem = false; var detail = new List<string>(); var expectedAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase); double amt = 0;
            foreach (var (label, net) in new[] { ("cuối kỳ", a.CloseNet), ("đầu kỳ", a.OpenNet) })
            {
                if (Math.Abs(net) < 0.5) continue;
                var side = net > 0 ? "N" : "C";
                var expect = entries.Where(e => e.Side == side && AccOverlap(a.Code, e.Prefix)).Select(e => e.Code).Distinct().ToList();
                if (expect.Count == 0) continue;                                        // chuẩn không có chỉ tiêu cho TK / chiều này → không kết luận
                foreach (var e in expect.ToList()) { var pr = std.Line(e)?.Pair; if (!string.IsNullOrEmpty(pr)) expect.Add(pr); }
                var decl = declared.Where(l => l.Acc.Any(p => AccOverlap(a.Code, p))).Select(l => l.Code).Distinct().ToList();
                var declSet = new HashSet<string>(decl.Concat(decl.Select(BaseCode)), StringComparer.OrdinalIgnoreCase);
                foreach (var e in expect) expectedAll.Add(e);
                if (decl.Count == 0) { problem = true; amt = amt == 0 ? net : amt; detail.Add(label + " " + N0(net) + ": khách hàng KHÔNG khai chỉ tiêu nào lấy TK này — số bị rơi khỏi báo cáo"); }
                else if (!expect.Any(declSet.Contains)) { problem = true; amt = amt == 0 ? net : amt; detail.Add(label + " " + N0(net) + ": khai ở " + string.Join(", ", decl) + " nhưng chuẩn xếp vào " + string.Join(", ", expect.Distinct())); }
            }
            if (!problem) { if (expectedAll.Count > 0) okAcc++; continue; }
            findings.Add(new CfsItem
            {
                Severity = "error", Amount = amt,
                Title = "TK " + a.Code + (a.Name.Length > 0 ? " " + a.Name : "") + " — chuẩn: chỉ tiêu " + string.Join(" / ", expectedAll),
                Action = "Khai TK " + a.Code + " vào chỉ tiêu " + string.Join(" / ", expectedAll) + " theo chuẩn " + circ + ".",
                Detail = string.Join("; ", detail) + "."
            });
        }
        res.Overview.Add(new CfsOverview { Label = "CĐKT: TK có số dư xếp đúng chỉ tiêu theo chuẩn " + circ, Value = okAcc + " đúng, " + findings.Count + " sai / thiếu", Severity = findings.Count == 0 ? "ok" : "error" });
        if (findings.Count > 0)
            res.Sections.Add(new CfsSection
            {
                Id = idPrefix + "-cover", Area = area, Title = "TK có số dư xếp sai chỉ tiêu so với chuẩn " + circ + " (" + findings.Count + " TK)",
                Note = "Với mỗi TK có số dư (dư Nợ → chỉ tiêu tài sản, dư Có → chỉ tiêu nguồn vốn), so chỉ tiêu mà CHUẨN xếp với chỉ tiêu khách hàng đang khai. Kỳ hạn ngắn / dài hạn (131 ↔ 211…) được coi là cùng một hướng. Đi từ chuẩn nên không phụ thuộc cách làm riêng của doanh nghiệp.",
                Items = findings.OrderByDescending(i => Math.Abs(i.Amount ?? 0)).ToList()
            });
    }
}
