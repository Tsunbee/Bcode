using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Danh mục tài khoản của khách (dmtk) — import từ file Excel / kết quả select dán vào, không cần kết nối database của khách.
/// Dùng để: (a) bắt TK khai báo gõ sai (không có trong danh mục); (b) tính đúng dòng "Loại = 1" như Fast (chỉ TK có theo dõi công nợ tk_cn = 1
/// mới vào số dư theo khách — TK khác bị BỎ QUA); (c) tìm "TK anh em" cùng TK mẹ khi đề xuất đưa TK vào nhóm số dư; (d) hiện tên TK.
/// Và import mẫu chỉ tiêu của khách vào thư viện (%AppData%\Bcode\cfs-fast).
/// </summary>
public static partial class CashFlowDiagnosticService
{
    private sealed class AccCat { public string Code = "", Name = "", Parent = ""; public bool CongNo; public int Detail = -1; }

    [ThreadStatic] private static Dictionary<string, AccCat>? _cat;

    private static Dictionary<string, AccCat> LoadCatalog(string path)
    {
        var rows = IsTextFile(path) ? ReadText(path) : Xlsx.Read(path);
        var h = FindHeader(rows, "tk", "Tài khoản", "Số hiệu", "Số hiệu tài khoản", "Mã tài khoản", "Mã TK")
                ?? throw new InvalidDataException(Path.GetFileName(path) + ": không thấy dòng tiêu đề có cột \"tk\" / \"Tài khoản\" / \"Số hiệu\".");
        int cCode = Col(h, 1, "tk", "Tài khoản", "Số hiệu", "Số hiệu tài khoản", "Mã tài khoản", "Mã TK");
        int cName = Col(h, -1, "ten_tk", "Tên tài khoản", "Tên TK");
        int cPar = Col(h, -1, "tk_me", "TK mẹ", "Tài khoản mẹ", "Tk cấp trên");
        int cCn = Col(h, -1, "tk_cn", "TK công nợ", "Theo dõi công nợ", "Công nợ", "Tk công nợ");
        int cDet = Col(h, -1, "loai_tk", "Loại tài khoản", "Loại TK", "Tk chi tiết");
        var res = new Dictionary<string, AccCat>(StringComparer.Ordinal);
        foreach (var r in rows.Where(x => x.No > h.No))
        {
            var code = r.S(cCode).Trim(); if (code.Length == 0 || !Regex.IsMatch(code, @"^[0-9A-Za-z]+$")) continue;
            var cn = cCn < 0 ? "" : r.S(cCn).Trim().ToLowerInvariant();
            var det = cDet < 0 ? "" : r.S(cDet).Trim();
            res[code] = new AccCat
            {
                Code = code, Name = cName < 0 ? "" : r.S(cName).Trim(), Parent = cPar < 0 ? "" : r.S(cPar).Trim(),
                CongNo = cn is "1" or "1.0" or "true" or "x" or "có" or "co",
                Detail = det is "1" or "1.0" ? 1 : det is "0" or "0.0" ? 0 : -1,
            };
        }
        // thiếu cột TK mẹ: TK mẹ = TK dài nhất trong danh mục là tiền tố của TK này
        foreach (var a in res.Values.Where(a => a.Parent.Length == 0))
            a.Parent = res.Keys.Where(k => k.Length < a.Code.Length && a.Code.StartsWith(k, StringComparison.Ordinal)).OrderByDescending(k => k.Length).FirstOrDefault() ?? "";
        return res;
    }

    /// <summary>TK có theo dõi công nợ (chính nó hoặc TK mẹ được đánh dấu). Không có danh mục → coi như có (giữ cách tính cũ).</summary>
    private static bool CatCongNo(string acc)
    {
        if (_cat is null) return true;
        for (var c = acc; c.Length > 0;)
        {
            if (_cat.TryGetValue(c, out var a)) { if (a.CongNo) return true; c = a.Parent; }
            else c = c[..^1];
        }
        return false;
    }

    private static string AccName(string acc, List<TbAcc>? tb = null)
    {
        if (_cat is not null && _cat.TryGetValue(acc, out var a) && a.Name.Length > 0) return a.Name;
        return tb?.FirstOrDefault(x => x.Code == acc)?.Name ?? "";
    }

    /// <summary>Kiểm tra khai báo theo danh mục TK: TK không tồn tại, dòng "Loại = 1" chứa TK không theo dõi công nợ (Fast bỏ qua TK đó).</summary>
    private static void CheckCatalog(CfsResult res, List<Line> cfg, string area, List<TbAcc>? tb)
    {
        if (_cat is null || _cat.Count == 0) return;
        var items = new List<CfsItem>();
        // TK không có trong danh mục (Fast lọc kiểu "like 't%'": không TK nào bắt đầu bằng t thì chỉ tiêu không lấy được gì) — gộp theo TK
        var missing = cfg.Where(l => !l.HasFormula).SelectMany(l => l.Acc.Concat(l.Con).Select(t => (T: t, L: l.Code)))
                         .Where(x => !_cat.Keys.Any(k => k.StartsWith(x.T, StringComparison.Ordinal))).GroupBy(x => x.T).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in missing)
        {
            var codes = g.Select(x => x.L).Distinct().ToList();
            items.Add(new CfsItem
            {
                Severity = "warn", Title = "TK " + g.Key + " không có trong danh mục tài khoản của khách — khai ở chỉ tiêu " + string.Join(", ", codes),
                Action = "Kiểm tra TK " + g.Key + " ở chỉ tiêu " + string.Join(", ", codes) + ": danh mục của khách không có TK nào bắt đầu bằng " + g.Key + " (gõ sai / TK theo mẫu chung mà khách không dùng) — nếu khách hạch toán bằng TK khác thì phải khai TK đó.",
                Detail = "Không lỗi nếu khách thật sự không dùng TK này; sai nếu khách dùng TK chi tiết khác tên (vd 33512 thay vì 3352)."
            });
        }
        foreach (var l in cfg.Where(l => !l.HasFormula && l.CongNo == 1))
            foreach (var t in l.Acc)
            {
                var accs = _cat.Values.Where(a => a.Code.StartsWith(t, StringComparison.Ordinal) && a.Detail != 0).ToList();
                if (accs.Count == 0) continue;
                var notCn = accs.Where(a => !CatCongNo(a.Code)).ToList();
                if (notCn.Count == 0) continue;
                var mv = tb?.Where(x => x.Leaf && notCn.Any(a => a.Code == x.Code)).Sum(x => Math.Abs(x.OpenNet) + Math.Abs(x.CloseNet)) ?? 0;
                items.Add(new CfsItem
                {
                    Severity = mv > 0.5 ? "error" : "warn", Amount = mv > 0.5 ? mv : null,
                    Title = "Chỉ tiêu " + l.Code + " (Loại = 1 — một vế theo khách): TK " + string.Join(", ", notCn.Take(6).Select(a => a.Code)) + " không theo dõi công nợ",
                    Action = mv > 0.5 ? "TK " + string.Join(", ", notCn.Take(6).Select(a => a.Code)) + " có số dư nhưng không theo dõi công nợ — Fast BỎ QUA chúng ở chỉ tiêu " + l.Code + " (Loại = 1). Chuyển TK này sang chỉ tiêu số dư có ô \"Loại\" = 0, hoặc bật theo dõi công nợ cho TK trong danh mục." : "",
                    Detail = "Procedure Fast chỉ lấy số dư theo khách của TK có tk_cn = 1 (join dmtk). TK khác trong ô \"Các tài khoản\" của dòng Loại = 1 cho số 0."
                });
            }
        res.Overview.Add(new CfsOverview { Label = "Đối chiếu với danh mục tài khoản (" + _cat.Count + " TK)", Value = items.Count == 0 ? "khai báo khớp danh mục" : items.Count + " chỗ cần sửa", Severity = items.Count == 0 ? "ok" : "error" });
        if (items.Count > 0)
            res.Sections.Add(new CfsSection { Id = area + "-cat", Area = area, Title = "Khai báo so với danh mục tài khoản của khách (" + items.Count + ")", Note = "TK gõ sai / không tồn tại và TK không theo dõi công nợ ở dòng lấy một vế theo khách.", Items = items });
    }

    // ------------------------------------------------------------------------------------------ import mẫu chỉ tiêu vào thư viện
    /// <summary>Import một bộ chỉ tiêu (file Excel / kết quả select dán vào) làm mẫu của khách. <paramref name="kind"/>: cf-indirect | cf-direct | bs.</summary>
    public static (bool Ok, string Message) ImportTemplate(string cfgPath, string customer, string kind, bool verified)
    {
        try
        {
            if (kind is not ("cf-indirect" or "cf-direct" or "bs")) return (false, "Loại báo cáo không hợp lệ.");
            var cfg = LoadConfig(cfgPath);
            if (cfg.Count == 0) return (false, "Không đọc được bộ chỉ tiêu (cần dòng tiêu đề có cột ma_so / Mã số).");
            var refd = CfsFasts.Reference(kind, cfg.Select(l => l.Code), cfg.Select(l => l.Name));
            List<FastLine> lines;
            var guessed = false;
            if (kind == "cf-indirect")
            {
                var ls = ToEngine(cfg, refd); guessed = ls.Any(e => e.Guessed);
                lines = ls.Select((e, i) => new FastLine { Stt = i + 1, Code = e.Code, Name = e.Name.Trim(), Formula = e.Formula, Tk = new(e.Tk), TkDu = new(e.Du), Kind = e.Kind, NoCo = e.NoCo, DauCuoi = e.DauCuoi, KhongAm = e.KhongAm, CongNo = e.CongNo, ThuChi = e.ThuChi }).ToList();
            }
            else
            {
                int F(int v, int? r, int def) => v >= 0 ? v : r ?? def;
                lines = cfg.Select((l, i) =>
                {
                    var r = refd?.Line(l.Code); guessed |= !l.HasFlags && !l.HasFormula;
                    return new FastLine
                    {
                        Stt = i + 1, Code = l.Code, Name = l.Name.Trim(), Formula = l.Formula.Trim(), Tk = new(l.Acc), TkDu = new(l.Con),
                        Kind = l.HasFormula ? 0 : F(l.Kind, r?.Kind, 1), NoCo = F(l.NoCo, r?.NoCo, 1), ThuChi = F(l.ThuChi, r?.ThuChi, 1), TsNv = F(l.TsNv, r?.TsNv, 1),
                        CongNo = F(l.CongNo, r?.CongNo, 0), KhongAm = F(l.KhongAm, r?.KhongAm, 0), DauCuoi = F(l.DauCuoi, r?.DauCuoi, 1),
                    };
                }).ToList();
            }
            var slug = Regex.Replace(customer.ToLowerInvariant(), @"[^\p{L}\p{N}]+", "-").Trim('-');
            var kindName = kind == "bs" ? "CĐKT" : kind == "cf-direct" ? "LCTT trực tiếp" : "LCTT gián tiếp";
            var d = new FastDecl
            {
                Id = "cust-" + kind + "-" + slug, Kind = kind, Table = kind == "bs" ? "v20gltc1" : kind == "cf-direct" ? "v20gltc5" : "v20gltc6", Form = "KH " + customer,
                Circular = refd?.Circular ?? "TT99", FormNo = refd?.FormNo ?? "", Title = "Mẫu " + kindName + " của " + customer + (verified ? " (đã chạy đúng)" : ""),
                Customer = customer, Verified = verified, BasedOn = refd is null ? "" : refd.Customer.Length > 0 && refd.BasedOn.Length > 0 ? refd.BasedOn : refd.Id,
                Source = "import từ " + Path.GetFileName(cfgPath) + (guessed ? " (file thiếu cột cờ — cờ lấy theo mẫu " + refd?.Form + " / đoán theo tên)" : ""), LearnedAt = DateTime.Now.ToString("yyyy-MM-dd"),
                Lines = lines
            };
            var path = CfsFasts.Save(d);
            return (true, "Đã import mẫu " + kindName + " của \"" + customer + "\" (" + lines.Count + " dòng" + (verified ? ", đánh dấu đã chạy đúng" : "") + ")" + (guessed ? " — file thiếu cột cờ nên một số cờ được lấy theo mẫu tham chiếu" : "") + ". Lần sau khai báo giống mẫu này sẽ được đối chiếu theo nó.");
        }
        catch (Exception ex) { return (false, "Import lỗi: " + ex.Message); }
    }
}
