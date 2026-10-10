using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bcode.App.Services;

// =====================================================================================================================
//  Dò lỗi Báo cáo lưu chuyển tiền tệ (LCTT gián tiếp) và Bảng cân đối kế toán (CĐKT) của FastBusiness (TT99).
//  Port từ tool CfsDiagnostic (D:\Bee\Tool\BcodeProject\LCTT) + bổ sung phần CĐKT. Không cần NuGet: tự đọc .xlsx bằng
//  System.IO.Compression + System.Xml.Linq (file xuất từ Fast thiếu sharedStrings.xml và ô không có thuộc tính r nên
//  không dùng EPPlus/ClosedXML mặc định được).
//
//  Dữ liệu vào (người dùng đính kèm): bảng kê chứng từ, bảng cân đối phát sinh TK, báo cáo CĐKT, báo cáo LCTT hiện tại,
//  khai báo chỉ tiêu CĐKT, khai báo chỉ tiêu LCTT. Thiếu file nào thì kiểm tra cần file đó được bỏ qua (có thông báo).
//
//  KIẾN THỨC ĐÃ ĐÚC KẾT CHO LCTT (mỗi mục là một kiểm tra):
//   1. Chênh lệch 70B = CK theo LCTT - CK theo CĐKT = (A) dư cuối kỳ TK loại 5-9 chưa kết chuyển
//        + (B) dòng "điều chỉnh" chỉ đụng TK loại 5-9 + (C) dòng flow gắn vào TK đang nằm trong nhóm số dư 09-13
//        + (D) TK ở dòng flow nhưng KHÔNG nằm trong nhóm số dư: (đã lấy) - (biến động thực) + (E) dòng để trống TK.
//   2. Dòng flow chỉ lấy MỘT bên (Nợ hoặc Có) của dòng chứng từ khớp "Tài khoản" + "TK đối ứng" (khớp tiền tố).
//   3. Dòng để trống cả TK lẫn TK đối ứng cộng TOÀN BỘ bảng kê.   4. Nhóm 09/11 lấy dư theo từng đối tượng.
//   5. 05E phải đủ mọi TK con của 515.   6. 05A lấy bút toán kết chuyển (911/8111), 8111 chứa tiền phạt chậm nộp thuế.
//  KIỂM TRA CĐKT: mỗi chỉ tiêu lá so với số dư TK trên bảng cân đối phát sinh; chỉ tiêu cha so với tổng chỉ tiêu con;
//  chỉ tiêu có số nhưng không được cộng vào cha nào; TK có số dư nhưng không thuộc chỉ tiêu nào; Tổng TS - Tổng NV;
//  tiền cuối kỳ LCTT so với tiền trên CĐKT.
// =====================================================================================================================

public sealed class CfsInputs
{
    public string? Journal { get; set; }          // bảng kê chứng từ
    public string? TrialBalance { get; set; }     // bảng cân đối phát sinh tài khoản
    public string? BalanceReport { get; set; }    // báo cáo CĐKT hiện tại
    public string? CashFlowReport { get; set; }   // báo cáo LCTT gián tiếp hiện tại (TT200)
    public string? BalanceConfig { get; set; }    // khai báo chỉ tiêu CĐKT
    public string? CashFlowConfig { get; set; }   // khai báo chỉ tiêu LCTT gián tiếp
    public string? DirectReport { get; set; }     // báo cáo LCTT trực tiếp hiện tại (TT99)
    public string? DirectConfig { get; set; }     // khai báo chỉ tiêu LCTT trực tiếp

    /// <summary>Loại báo cáo cần kiểm tra — chọn tuỳ ý: LCTT gián tiếp (theo TT200), LCTT trực tiếp (TT99), CĐKT (TT99).</summary>
    public bool RunIndirect { get; set; } = true;
    public bool RunDirect { get; set; } = true;
    public bool RunBalance { get; set; } = true;
}

public sealed class CfsItem
{
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
    public double? Amount { get; set; }
    /// <summary>error = lệch chắc chắn; warn = nghi vấn / cần người xem; info = ghi chú.</summary>
    public string Severity { get; set; } = "warn";
    /// <summary>Việc người dùng cần làm để sửa (rỗng = chỉ là thông tin). Gom lên mục "Tổng kết" đầu danh sách kết quả.</summary>
    public string Action { get; set; } = "";
}

public sealed class CfsSection
{
    public string Id { get; set; } = "";
    public string Area { get; set; } = "";     // "lctt" | "cdkt" | "cross"
    public string Title { get; set; } = "";
    public string Note { get; set; } = "";
    public double? Total { get; set; }
    public List<CfsItem> Items { get; set; } = new();
}

public sealed class CfsOverview
{
    public string Label { get; set; } = "";
    public string Value { get; set; } = "";
    public string Severity { get; set; } = "info";
}

public sealed class CfsResult
{
    public List<CfsOverview> Overview { get; set; } = new();
    public List<CfsSection> Sections { get; set; } = new();
    public List<string> Messages { get; set; } = new();
    public string Text { get; set; } = "";
}

public static partial class CashFlowDiagnosticService
{
    // ------------------------------------------------------------------------------------------ đọc xlsx
    private sealed class Row
    {
        public int No;
        public readonly Dictionary<int, string> C = new();
        public string S(int col) => C.TryGetValue(col, out var v) && v != null ? v.Trim() : "";
        public double N(int col) => double.TryParse(S(col), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static class Xlsx
    {
        private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        private static int ColIndex(string letters)
        {
            var n = 0;
            foreach (var ch in letters) n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
            return n;
        }

        public static List<Row> Read(string path)
        {
            var rows = new List<Row>();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);   // file đang mở trong Excel vẫn đọc được
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var shared = new List<string>();
            var ss = zip.GetEntry("xl/sharedStrings.xml");          // có thể không tồn tại
            if (ss != null)
                using (var s = ss.Open())
                    foreach (var si in XDocument.Load(s).Descendants(Ns + "si"))
                        shared.Add(string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)));

            var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ??
                        zip.Entries.First(e => e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml"));
            XDocument doc;
            using (var s = sheet.Open()) doc = XDocument.Load(s);

            var rowNo = 0;
            foreach (var r in doc.Descendants(Ns + "row"))
            {
                var ra = r.Attribute("r");
                rowNo = ra != null ? int.Parse(ra.Value) : rowNo + 1;
                var row = new Row { No = rowNo };
                var ci = 0;
                foreach (var c in r.Elements(Ns + "c"))
                {
                    var ca = c.Attribute("r");
                    if (ca != null) ci = ColIndex(new string(ca.Value.TakeWhile(char.IsLetter).ToArray()));
                    else ci++;
                    var t = c.Attribute("t")?.Value ?? "";
                    string val;
                    if (t == "inlineStr") val = string.Concat(c.Descendants(Ns + "t").Select(x => x.Value));
                    else
                    {
                        var v = c.Element(Ns + "v");
                        if (v == null) continue;
                        val = t == "s" && int.TryParse(v.Value, out var si) && si >= 0 && si < shared.Count ? shared[si] : v.Value;
                    }
                    row.C[ci] = val;
                }
                if (row.C.Count > 0) rows.Add(row);
            }
            return rows;
        }
    }

    // ------------------------------------------------------------------------------------------ mô hình
    private sealed class Line
    {
        public int Row; public string Code = "", Name = "", Formula = "";
        public List<string> Acc = new(), Con = new();
        /// <summary>Cờ khai báo của Fast (chỉ có khi file khai báo là kết quả `select * from v20gltc…` — có đủ cột); -1 = không biết.</summary>
        public int NoCo = -1, CongNo = -1, DauCuoi = -1, KhongAm = -1, Kind = -1, ThuChi = -1, TsNv = -1;
        public bool HasFlags => CongNo >= 0 || KhongAm >= 0 || Kind >= 0;
        public bool HasFormula => Formula.Length > 0;
    }

    private sealed class TbAcc
    {
        public string Code = "", Name = ""; public bool Leaf;
        public double OpenNet, CloseNet, PsNo, PsCo;        // net = Nợ - Có
        public double Delta => CloseNet - OpenNet;
    }

    private sealed class Jr { public string Tk = "", Dut = ""; public double No, Co; public string Date = "", Voucher = "", Desc = ""; }

    private sealed class Finding { public string Group = "", Title = "", Detail = "", Action = ""; public double? Effect; public bool Balanced; }

    private static readonly string[] Cash = { "111", "112", "113", "12811", "12881" };
    private static readonly string[] BalancePrefix = { "09", "10", "11", "12", "13" };   // nhóm chỉ tiêu LCTT tính theo số dư

    // ------------------------------------------------------------------------------------------ tiện ích
    private static List<string> Split(string? s) =>
        (s ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    private static bool Match(string acc, List<string> prefixes)
    {
        foreach (var p in prefixes) if (acc.StartsWith(p, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsCash(string acc) => Cash.Any(p => acc.StartsWith(p, StringComparison.Ordinal));
    private static bool IsBs(string acc) => acc.Length > 0 && acc[0] >= '1' && acc[0] <= '4' && !IsCash(acc);
    private static string N0(double v) => v.ToString("#,##0", CultureInfo.InvariantCulture);

    private static Row? FindHeader(List<Row> rows, params string[] anyOf) =>
        rows.FirstOrDefault(r => r.C.Values.Any(v => anyOf.Any(a => v.Trim().Equals(a, StringComparison.OrdinalIgnoreCase))));

    private static int Col(Row hdr, int fallback, params string[] names)
    {
        foreach (var kv in hdr.C)
            foreach (var n in names)
                if (kv.Value.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        return fallback;
    }

    private static int ColContains(Row hdr, int fallback, params string[] parts)
    {
        foreach (var kv in hdr.C)
        {
            var t = kv.Value.Trim().ToLowerInvariant();
            if (parts.Any(p => t.Contains(p))) return kv.Key;
        }
        return fallback;
    }

    // ------------------------------------------------------------------------------------------ nạp dữ liệu
    /// <summary>File dán từ kết quả select (txt / tsv / csv): dòng đầu là tiêu đề cột; ngăn cột bằng Tab (copy từ lưới / Excel), | (sqlcmd), ; hoặc ,. Bỏ dòng gạch ngang và "(n rows affected)".</summary>
    private static List<Row> ReadText(string path)
    {
        var lines = File.ReadAllText(path).Replace((char)13, (char)10).Split((char)10).Where(l => l.Trim().Length > 0 && !Regex.IsMatch(l.Trim(), @"^[-|+\s]+$") && !Regex.IsMatch(l.Trim(), @"^\(\d+ rows? affected\)$", RegexOptions.IgnoreCase)).ToList();
        if (lines.Count == 0) return new();
        var first = lines[0];
        var sep = first.Contains((char)9) ? (char)9 : first.Contains('|') ? '|' : first.Count(c => c == ';') >= first.Count(c => c == ',') && first.Contains(';') ? ';' : ',';
        var res = new List<Row>();
        for (var i = 0; i < lines.Count; i++)
        {
            var r = new Row { No = i + 1 };
            var cells = lines[i].Split(sep);
            for (var c = 0; c < cells.Length; c++) r.C[c + 1] = cells[c].Trim().Trim('"');
            res.Add(r);
        }
        return res;
    }

    private static bool IsTextFile(string path) { var e = Path.GetExtension(path).ToLowerInvariant(); return e is ".txt" or ".tsv" or ".csv"; }

    private static List<Line> LoadConfig(string path)
    {
        var rows = IsTextFile(path) ? ReadText(path) : Xlsx.Read(path);
        // 2 dạng file: (1) bản xuất từ màn hình khai báo — cột "Mã số", "Công thức", "Tài khoản", "Tài khoản đối ứng"; (2) kết quả `select * from v20gltc1 / v20GLTC5 / v20GLTC6` — cột ma_so, cach_tinh, tk, tk_du (hoặc tk_no, tk_co) + các cờ.
        var h = FindHeader(rows, "Mã số", "ma_so") ?? throw new InvalidDataException($"{Path.GetFileName(path)}: không thấy dòng tiêu đề có cột \"Mã số\" (hoặc ma_so).");
        int C(int fb, params string[] names) => Col(h, fb, names);
        int cCode = C(2, "Mã số", "ma_so"), cName = C(3, "Chỉ tiêu", "chi_tieu"), cF = C(4, "Công thức", "cach_tinh");
        int cA = C(-1, "Tài khoản", "Các tài khoản", "tk", "tk_no", "Các tài khoản nợ"), cB = C(-1, "Tài khoản đối ứng", "Tk đối ứng", "Các tài khoản đối ứng", "tk_du", "tk_co", "Các tài khoản có");
        bool raw = Col(h, -1, "ma_so") >= 0;
        if (cA < 0) cA = raw ? -1 : 5; if (cB < 0) cB = raw ? -1 : 6;
        int cForm = C(-1, "form"), cNoCo = C(-1, "no_co"), cCongNo = C(-1, "cong_no"), cDauCuoi = C(-1, "dau_cuoi"), cKhongAm = C(-1, "khong_am"), cKind = C(-1, "kind"), cThuChi = C(-1, "dau"), cTsNv = C(-1, "ts_nv");
        var data = rows.Where(x => x.No > h.No).ToList();
        if (cForm >= 0)       // file chứa nhiều mẫu: lấy mẫu có nhiều dòng nhất (không phân biệt hoa thường)
        {
            var best = data.GroupBy(r => r.S(cForm).ToUpperInvariant()).Where(g => g.Key.Length > 0).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (best != null) data = data.Where(r => r.S(cForm).ToUpperInvariant() == best.Key).ToList();
        }
        int F(Row r, int c) => c < 0 || r.S(c).Length == 0 ? -1 : (int)Math.Round(r.N(c));
        var res = new List<Line>();
        foreach (var r in data)
        {
            var code = r.S(cCode); if (code.Length == 0 || code == ".") continue;
            res.Add(new Line
            {
                Row = r.No, Code = code, Name = cName < 0 ? "" : r.S(cName), Formula = cF < 0 ? "" : r.S(cF), Acc = cA < 0 ? new() : Split(r.S(cA)).Where(x => x != "#").ToList(), Con = cB < 0 ? new() : Split(r.S(cB)).Where(x => x != "#").ToList(),
                NoCo = F(r, cNoCo), CongNo = F(r, cCongNo), DauCuoi = F(r, cDauCuoi), KhongAm = F(r, cKhongAm), Kind = F(r, cKind), ThuChi = F(r, cThuChi), TsNv = F(r, cTsNv),
            });
        }
        return res;
    }

    /// <summary>Báo cáo (LCTT / CĐKT): mã số → (cuối kỳ/năm nay, đầu kỳ/năm trước). Cột nhận theo tên tiêu đề, không thấy thì dùng vị trí mặc định.</summary>
    private static Dictionary<string, (double Cur, double Prev)> LoadReport(string path, int curFallback, int prevFallback)
    {
        var rows = Xlsx.Read(path);
        var h = FindHeader(rows, "Mã số") ?? throw new InvalidDataException($"{Path.GetFileName(path)}: không thấy dòng tiêu đề có cột \"Mã số\".");
        var cCode = Col(h, 1, "Mã số");
        var cCur = Col(h, -1, "Năm nay", "Cuối năm", "Cuối kỳ", "Số cuối kỳ", "Số cuối năm");
        if (cCur < 0) cCur = ColContains(h, curFallback, "năm nay", "cuối");
        var cPrev = Col(h, -1, "Năm trước", "Đầu năm", "Đầu kỳ", "Số đầu năm", "Số đầu kỳ");
        if (cPrev < 0) cPrev = ColContains(h, prevFallback, "năm trước", "đầu");
        var d = new Dictionary<string, (double, double)>();
        foreach (var r in rows.Where(x => x.No > h.No)) { var c = r.S(cCode); if (c.Length > 0) d[c] = (r.N(cCur), r.N(cPrev)); }
        return d;
    }

    private static List<TbAcc> LoadTb(string path)
    {
        var list = new List<TbAcc>();
        foreach (var r in Xlsx.Read(path))
        {
            var code = r.S(1);
            // Mã TK có thể kèm chữ (155TP, 131KH...): vẫn là TK con của 155 / 131 — bỏ sót chúng làm số dư TK cha thấy bằng 0 (báo lệch sai chỉ tiêu 141E...).
            if (code.Length == 0 || !char.IsDigit(code[0]) || !code.All(char.IsLetterOrDigit)) continue;
            list.Add(new TbAcc { Code = code, Name = r.S(2), OpenNet = r.N(3) - r.N(4), PsNo = r.N(5), PsCo = r.N(6), CloseNet = r.N(7) - r.N(8) });
        }
        foreach (var a in list) a.Leaf = !list.Any(o => o.Code.Length > a.Code.Length && o.Code.StartsWith(a.Code, StringComparison.Ordinal));
        return list;
    }

    private static List<Jr> LoadJournal(string path)
    {
        var rows = Xlsx.Read(path);
        var h = FindHeader(rows, "Tk đối ứng") ?? throw new InvalidDataException($"{Path.GetFileName(path)}: không thấy dòng tiêu đề có cột \"Tk đối ứng\".");
        int cTk = Col(h, 8, "Tài khoản"), cDu = Col(h, 9, "Tk đối ứng"), cNo = Col(h, 10, "Phát sinh nợ"), cCo = Col(h, 11, "Phát sinh có"),
            cDate = Col(h, 1, "Ngày ct"), cVou = Col(h, 4, "Số ct"), cDesc = Col(h, 7, "Diễn giải");
        var list = new List<Jr>(rows.Count);
        foreach (var r in rows.Where(x => x.No > h.No))
        {
            var tk = r.S(cTk); if (tk.Length == 0) continue;           // bỏ dòng "Tổng cộng"
            var date = r.N(cDate);
            list.Add(new Jr { Tk = tk, Dut = r.S(cDu), No = r.N(cNo), Co = r.N(cCo),
                Date = date > 20000 && date < 80000 ? new DateTime(1899, 12, 30).AddDays(date).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) : r.S(cDate),
                Voucher = r.S(cVou), Desc = r.S(cDesc) });
        }
        return list;
    }

    private static void Expand(Dictionary<string, Line> byCode, string code, double coef, Dictionary<string, double> leaf, int depth)
    {
        if (depth > 20 || !byCode.TryGetValue(code, out var l)) return;
        if (!l.HasFormula) { leaf.TryGetValue(code, out var o); leaf[code] = o + coef; return; }
        foreach (Match m in Regex.Matches(l.Formula, @"([+-]?)\s*\[([^\]]+)\]"))
            Expand(byCode, m.Groups[2].Value.Trim(), m.Groups[1].Value == "-" ? -coef : coef, leaf, depth + 1);
    }

    // ------------------------------------------------------------------------------------------ cấu trúc chuẩn TT99
    // Lấy từ mẫu B01-DN (Báo cáo tình hình tài chính) và B03-DN (LCTT gián tiếp) kèm Thông tư 99/2025/TT-BTC: chỉ tiêu cha → các chỉ tiêu con cộng trực tiếp.
    private static readonly Dictionary<string, string[]> Tt99BalanceFallback = new()
    {
        ["110"] = new[] { "111", "112" },
        ["120"] = new[] { "121", "122", "123", "124", "125", "126" },
        ["130"] = new[] { "131", "132", "133", "134", "135", "136", "137" },
        ["140"] = new[] { "141", "142" },
        ["150"] = new[] { "151", "152", "153" },
        ["160"] = new[] { "161", "162", "163", "164", "165" },
        ["100"] = new[] { "110", "120", "130", "140", "150", "160" },
        ["210"] = new[] { "211", "212", "213", "214", "215", "216" },
        ["221"] = new[] { "222", "223" },
        ["224"] = new[] { "225", "226" },
        ["227"] = new[] { "228", "229" },
        ["220"] = new[] { "221", "224", "227" },
        ["233"] = new[] { "234", "235" },
        ["231"] = new[] { "232", "233" },
        ["230"] = new[] { "231", "236", "237", "238" },
        ["240"] = new[] { "241", "242" },
        ["250"] = new[] { "251", "252" },
        ["260"] = new[] { "261", "262", "263", "264", "265", "266" },
        ["270"] = new[] { "271", "272", "273", "274" },
        ["200"] = new[] { "210", "220", "230", "240", "250", "260", "270" },
        ["280"] = new[] { "100", "200" },
        ["310"] = new[] { "311", "312", "313", "314", "315", "316", "317", "318", "319", "320", "321", "322", "323", "324", "325" },
        ["330"] = new[] { "331", "332", "333", "334", "335", "336", "337", "338", "339", "340", "341", "342", "343", "344" },
        ["300"] = new[] { "310", "330" },
        ["411"] = new[] { "411a", "411b" },
        ["420"] = new[] { "420a", "420b" },
        ["400"] = new[] { "411", "412", "413", "414", "415", "416", "417", "418", "419", "420" },
        ["440"] = new[] { "300", "400" },
    };

    private static readonly Dictionary<string, string[]> Tt99CashFlowFallback = new()
    {
        ["08"] = new[] { "01", "02", "03", "04", "05", "06", "07" },
        ["20"] = new[] { "08", "09", "10", "11", "12", "13", "14", "15", "16", "17" },
        ["30"] = new[] { "21", "22", "23", "24", "25", "26", "27" },
        ["40"] = new[] { "31", "32", "33", "34", "35", "36" },
        ["50"] = new[] { "20", "30", "40" },
        ["70"] = new[] { "50", "60", "61" },
    };

    /// <summary>Công thức khai báo của các chỉ tiêu tổng so với cấu trúc chuẩn TT99: thiếu chỉ tiêu con chuẩn (số bị bỏ sót khỏi tổng) hoặc cộng thêm chỉ tiêu ngoài chuẩn.
    /// Chỉ chạy khi khai báo có đúng mã của mẫu TT99 (CĐKT: có 280 và 440; LCTT: có 20 và 50) — khai báo theo mẫu cũ (TT200) thì bỏ qua.</summary>
    private static void CheckStandard(CfsResult res, List<Line> cfg, Dictionary<string, string[]> std, string id, string area, string title, Dictionary<string, (double Cur, double Prev)>? rep = null)
    {
        var byCode = cfg.GroupBy(l => l.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        HashSet<string> Reach(string code, int depth = 0)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (depth > 25 || !byCode.TryGetValue(code, out var l) || !l.HasFormula) return set;
            foreach (Match m in Regex.Matches(l.Formula, @"\[([^\]]+)\]"))
            {
                var c = m.Groups[1].Value.Trim();
                if (set.Add(c)) set.UnionWith(Reach(c, depth + 1));
            }
            return set;
        }

        var items = new List<CfsItem>();
        foreach (var (parent, kids) in std)
        {
            if (!byCode.TryGetValue(parent, out var l)) continue;
            if (!l.HasFormula)
            {
                if (l.Acc.Count > 0) continue;       // chỉ tiêu tự khai TK trực tiếp (vd 420 lấy TK 421, 420A/420B chỉ là dòng chi tiết) — không cần công thức
                items.Add(new CfsItem { Severity = "warn", Action = "Khai công thức cho chỉ tiêu " + parent + " " + l.Name + " (chuẩn TT99: " + parent + " = " + string.Join(" + ", kids) + ").", Title = "Chỉ tiêu " + parent + " " + l.Name + " không có công thức", Detail = "Chuẩn TT99: " + parent + " = " + string.Join(" + ", kids) + "." });
                continue;
            }
            var reach = Reach(parent);
            var missing = kids.Where(k => byCode.ContainsKey(k) && !reach.Contains(k)).ToList();
            var direct = Regex.Matches(l.Formula, @"\[([^\]]+)\]").Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var coverage = new HashSet<string>(kids, StringComparer.OrdinalIgnoreCase);
            foreach (var k in kids) coverage.UnionWith(Reach(k));
            var extra = direct.Where(d => !coverage.Contains(d)).ToList();
            if (missing.Count > 0)
                items.Add(new CfsItem
                {
                    Severity = rep != null && missing.All(k => !rep.TryGetValue(k, out var mv) || (Math.Abs(mv.Cur) < 0.5 && Math.Abs(mv.Prev) < 0.5)) ? "warn" : "error",
                    Action = "Sửa công thức chỉ tiêu " + parent + " " + l.Name + ": thêm " + string.Join(", ", missing.Select(m => "[" + m + "]")) + " (chuẩn TT99: " + parent + " = " + string.Join(" + ", kids) + ").", Title = "Chỉ tiêu " + parent + " " + l.Name + " thiếu chỉ tiêu con chuẩn: " + string.Join(", ", missing),
                    Detail = "Công thức hiện tại [" + l.Formula + "] không cộng " + string.Join(", ", missing) + " (kể cả qua chỉ tiêu trung gian) — số của chúng bị bỏ sót khỏi " + parent + ". Chuẩn TT99: " + parent + " = " + string.Join(" + ", kids) + "."
                });
            if (extra.Count > 0)
                items.Add(new CfsItem
                {
                    Severity = "warn", Action = "Xem lại công thức chỉ tiêu " + parent + ": " + string.Join(", ", extra.Select(m => "[" + m + "]")) + " không thuộc chuẩn TT99 — bỏ nếu trùng ý với chỉ tiêu con chuẩn (tránh cộng 2 lần).", Title = "Chỉ tiêu " + parent + " " + l.Name + " cộng thêm chỉ tiêu ngoài chuẩn: " + string.Join(", ", extra),
                    Detail = "Công thức [" + l.Formula + "] có chỉ tiêu không thuộc cấu trúc chuẩn TT99 — hợp lệ nếu là chỉ tiêu riêng của Fast, nhưng nếu trùng ý với chỉ tiêu con chuẩn thì bị cộng 2 lần."
                });
        }
        // Cộng / trừ cùng 1 chỉ tiêu con hai lần trong 1 công thức (vd [231]+[238]+[237]+[238]) → số bị tính gấp đôi
        foreach (var l in cfg.Where(x => x.HasFormula && !Regex.IsMatch(x.Formula, @"(?i)\b(case|when|then|else|iif|if|max|min|abs)\b|[<>=]")))
        {
            var dups = Regex.Matches(l.Formula, @"\[([^\]]+)\]").Select(m => m.Groups[1].Value.Trim()).GroupBy(c => c, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dups.Count == 0) continue;
            items.Add(new CfsItem
            {
                Severity = "error", Action = "Sửa công thức chỉ tiêu " + l.Code + " " + l.Name + ": " + string.Join(", ", dups.Select(d => "[" + d + "]")) + " xuất hiện nhiều lần — bỏ bớt (hiện bị tính gấp đôi).",
                Title = "Chỉ tiêu " + l.Code + " " + l.Name + " cộng trùng chỉ tiêu: " + string.Join(", ", dups),
                Detail = "Công thức [" + l.Formula + "] có " + string.Join(", ", dups.Select(d => "[" + d + "]")) + " lặp lại — chỉ tiêu này bị cộng 2 lần (hoặc nhầm mã, đáng lẽ là chỉ tiêu khác)."
            });
        }
        if (items.Count > 0)
            res.Sections.Add(new CfsSection { Id = id, Area = area, Title = title, Note = "So công thức khai báo với mẫu chuẩn kèm Thông tư 99/2025/TT-BTC.", Items = items });
    }

    // Theo hướng dẫn lập BCLCTT gián tiếp (AMIS/MISA, TT200 & TT99): TK nguồn của các chỉ tiêu điều chỉnh / thay đổi vốn lưu động.
    // Bỏ 131, 331, 333, 335 vì chúng nằm ở cả 2 chỉ tiêu (09 và 11) hoặc có phần tách riêng (thuế TNDN, lãi vay) nên không kết luận thiếu được.
    private static readonly Dictionary<string, string[]> CashFlowSourceAccountsFallback = new()
    {
        ["02"] = new[] { "214" },
        ["03"] = new[] { "129", "139", "159", "229" },
        ["09"] = new[] { "133", "136", "138", "141", "244" },
        ["10"] = new[] { "151", "152", "153", "154", "155", "156", "157", "158" },
        ["11"] = new[] { "334", "336", "337", "338", "344" },
        ["12"] = new[] { "242" },
        ["13"] = new[] { "121" },
    };

    // Dấu chuẩn của chỉ tiêu "tiền thu" (dương) và "tiền chi / đã trả / đã nộp" (âm) trên LCTT gián tiếp.
    private static readonly string[] CashFlowPositiveFallback = { "16", "22", "24", "26", "27", "31", "33" };
    private static readonly string[] CashFlowNegativeFallback = { "14", "15", "17", "21", "23", "25", "32", "34", "35", "36" };

    /// <summary>TK nguồn theo hướng dẫn mà khai báo chưa khai ở chỉ tiêu tương ứng (kể cả chỉ tiêu con kiểu 09L1, 10A...) trong khi TK đó có biến động trên sổ;
    /// và dấu bất thường của các chỉ tiêu thu / chi.</summary>
    private static void CheckCashFlowGuideline(CfsResult res, List<Line> cfg, Dictionary<string, (double Cur, double Prev)> rep, List<TbAcc> tb, bool direct = false)
    {
        var leaves = tb.Where(a => a.Leaf).ToList();
        var items = new List<CfsItem>();
        if (!direct)
        foreach (var (code, accounts) in CashFlowSourceAccounts)
        {
            // chỉ tiêu lá chính nó, hoặc con của nó dạng "09L1" (mã + chữ cái...)
            var lines = cfg.Where(l => !l.HasFormula && (l.Code == code || (l.Code.StartsWith(code, StringComparison.Ordinal) && l.Code.Length > code.Length && !char.IsDigit(l.Code[code.Length])))).ToList();
            if (lines.Count == 0) continue;
            var declared = lines.SelectMany(l => l.Acc.Concat(l.Con)).ToList();
            var declaredAnywhere = cfg.Where(l => !l.HasFormula).SelectMany(l => l.Acc.Concat(l.Con)).ToList();   // đã xử lý ở chỉ tiêu khác (vd 244 ở 11L6, 16A, 17A): để mục "TK chưa bù" ở dưới kết luận theo số liệu thật
            foreach (var acc in accounts)
            {
                if (declared.Any(p => acc.StartsWith(p, StringComparison.Ordinal) || p.StartsWith(acc, StringComparison.Ordinal))) continue;
                if (declaredAnywhere.Any(p => acc.StartsWith(p, StringComparison.Ordinal) || p.StartsWith(acc, StringComparison.Ordinal))) continue;
                var moved = leaves.Where(a => a.Code.StartsWith(acc, StringComparison.Ordinal) && Math.Abs(a.Delta) > 0.5).ToList();
                if (moved.Count == 0) continue;   // TK không biến động: thiếu khai báo cũng không làm lệch kỳ này
                var delta = moved.Sum(a => a.Delta);
                items.Add(new CfsItem
                {
                    Severity = "error", Amount = delta,
                    Action = "Thêm TK " + acc + " vào khai báo chỉ tiêu " + code + " (TK này biến động " + N0(delta) + " mà chưa nằm trong chỉ tiêu).",
                    Title = "Chỉ tiêu " + code + " chưa khai TK " + acc + " trong khi TK này có biến động " + N0(delta),
                    Detail = "Theo hướng dẫn lập BCLCTT, TK " + acc + " thuộc chỉ tiêu " + code + ". Khai báo hiện có: " + string.Join(" / ", lines.Select(l => l.Code + " [" + string.Join(",", l.Acc) + "]")) +
                             ". TK: " + string.Join("; ", moved.Take(6).Select(a => a.Code + " " + N0(a.Delta))) + ". Nếu TK này được xử lý ở chỉ tiêu khác thì bỏ qua."
                });
            }
        }
        foreach (var code in direct ? CashFlowPositiveDirect : CashFlowPositive)
            if (rep.TryGetValue(code, out var v) && v.Cur < -0.5)
                items.Add(new CfsItem { Severity = "warn", Amount = v.Cur, Action = "Kiểm tra dấu / chiều Nợ - Có khai báo của chỉ tiêu " + code + " (khoản thu đang âm " + N0(v.Cur) + ").", Title = "Chỉ tiêu " + code + " là khoản THU nhưng đang ÂM (" + N0(v.Cur) + ")", Detail = "Theo hướng dẫn, chỉ tiêu 'tiền thu' ghi số dương. Kiểm tra dấu / chiều Nợ - Có của khai báo." });
        foreach (var code in direct ? CashFlowNegativeDirect : CashFlowNegative)
            if (rep.TryGetValue(code, out var v) && v.Cur > 0.5)
                items.Add(new CfsItem { Severity = "warn", Amount = v.Cur, Action = "Kiểm tra dấu / chiều Nợ - Có khai báo của chỉ tiêu " + code + " (khoản chi đang dương " + N0(v.Cur) + ").", Title = "Chỉ tiêu " + code + " là khoản CHI / đã trả nhưng đang DƯƠNG (" + N0(v.Cur) + ")", Detail = "Theo hướng dẫn, chỉ tiêu 'tiền chi / đã trả / đã nộp' ghi số âm. Kiểm tra dấu / chiều Nợ - Có của khai báo." });
        if (items.Count > 0)
            res.Sections.Add(new CfsSection
            {
                Id = direct ? "cfd-guide" : "cf-guide", Area = direct ? "cfd" : "cfi", Title = "Khai báo LCTT so với hướng dẫn lập báo cáo",
                Note = "Đối chiếu với hướng dẫn lập BCLCTT gián tiếp (nguồn: AMIS MISA, mẫu B03-DN): TK nguồn từng chỉ tiêu và dấu thu / chi. Mang tính gợi ý — Fast có thể tách chỉ tiêu khác mẫu.",
                Items = items
            });
    }

    // Dấu chuẩn của LCTT trực tiếp TT99 (B03-DN).
    private static readonly string[] CashFlowPositiveDirectFallback = { "01", "06", "22", "24", "26", "27", "31", "33" };
    private static readonly string[] CashFlowNegativeDirectFallback = { "02", "03", "04", "05", "07", "21", "23", "25", "32", "34", "35", "36" };
    private static readonly Dictionary<string, string[]> Tt99CashFlowDirectFallback = new()
    {
        ["20"] = new[] { "01", "02", "03", "04", "05", "06", "07" },
        ["30"] = new[] { "21", "22", "23", "24", "25", "26", "27" },
        ["40"] = new[] { "31", "32", "33", "34", "35", "36" },
        ["50"] = new[] { "20", "30", "40" },
        ["70"] = new[] { "50", "60", "61" },
    };

    // ------------------------------------------------------------------------------------------ LCTT trực tiếp (TT99)
    /// <summary>
    /// LCTT trực tiếp: mọi chỉ tiêu là dòng tiền thật lấy từ chứng từ có TK tiền (111, 112, 113, 12811, 12881) đối ứng TK khác. Vì vậy phần "lệch ở đâu" là:
    ///  (1) dòng tiền trong bảng kê mà KHÔNG chỉ tiêu nào hứng; (2) dòng tiền bị 2 chỉ tiêu trở lên cùng hứng (tính trùng); (3) chỉ tiêu có số khác bảng kê
    ///  theo đúng khai báo; (4) bảng kê và bảng cân đối phát sinh không cùng kỳ / thiếu chứng từ; (5) chỉ tiêu để trống TK. Chứng từ chuyển tiền nội bộ
    ///  (TK tiền đối ứng TK tiền) triệt tiêu nhau nên bỏ qua.
    /// </summary>
    private static void DiagnoseDirect(CfsResult res, List<Line> cfg, Dictionary<string, (double Cur, double Prev)> repAll, List<TbAcc> tb, List<Jr> jr)
    {
        double V(string c) => repAll.TryGetValue(c, out var v) ? v.Cur : 0;
        var byCode = cfg.GroupBy(l => l.Code).ToDictionary(g => g.Key, g => g.First());
        var leaves = tb.Where(a => a.Leaf).ToList();
        var coef = new Dictionary<string, double>(); Expand(byCode, "50", 1, coef, 0);

        var dCash = leaves.Where(a => IsCash(a.Code)).Sum(a => a.Delta);
        var v50 = V("50"); var v61 = V("61");
        var gap = v50 + v61 - dCash;
        res.Overview.Add(new CfsOverview { Label = "LCTT thuần (mã 50)", Value = N0(v50) });
        res.Overview.Add(new CfsOverview { Label = "Ảnh hưởng tỷ giá (mã 61)", Value = N0(v61) });
        res.Overview.Add(new CfsOverview { Label = "Biến động tiền thực tế trên sổ (111,112,113,12811,12881)", Value = N0(dCash) });
        res.Overview.Add(new CfsOverview { Label = "Chênh lệch cần giải thích (50 + 61 - biến động tiền)", Value = N0(gap), Severity = Math.Abs(gap) < 1 ? "ok" : "error" });

        CheckCashFlowGuideline(res, cfg, repAll, tb, direct: true);
        if (byCode.ContainsKey("20") && byCode.ContainsKey("50"))
            CheckStandard(res, cfg, Tt99CashFlowDirect, "cfd-std", "cfd", "Công thức LCTT trực tiếp so với mẫu chuẩn TT99 (B03-DN)");
        CheckAgainstStandard(res, CfsStandards.ActiveId("cf-direct"), "cfd-std2", "cfd", cfg, null);
        CheckLineByLine(res, "cf-direct", "cfd-line", "cfd", cfg, tb);
        CheckAgainstFast(res, "cf-direct", "cfd", "cfd", cfg, tb);

        // chỉ xét chứng từ có TK tiền ở một đầu và TK không phải tiền ở đầu kia
        var rows = new List<Jr>(); var rowIdx = new List<int>();
        double internalMoves = 0;
        for (var i = 0; i < jr.Count; i++)
        {
            var x = jr[i];
            if (!IsCash(x.Tk)) continue;
            if (x.Dut.Length == 0) continue;
            if (IsCash(x.Dut)) { internalMoves += x.No; continue; }
            rows.Add(x);
        }
        var jNet = rows.Sum(x => x.No - x.Co);
        var covNo = new int[rows.Count]; var covCo = new int[rows.Count];
        var lineRows = new Dictionary<string, List<int>>();

        bool RowMatches(Line l, Jr r) =>
            (Match(r.Tk, l.Acc) && (l.Con.Count == 0 || Match(r.Dut, l.Con))) ||
            (l.Con.Count > 0 && Match(r.Tk, l.Con) && Match(r.Dut, l.Acc)) ||
            (l.Con.Count == 0 && !Match("111", l.Acc) && !IsCashList(l.Acc) && Match(r.Dut, l.Acc));

        var flowLines = cfg.Where(l => !l.HasFormula && l.Acc.Count > 0 && coef.ContainsKey(l.Code)).ToList();
        var blankLines = cfg.Where(l => !l.HasFormula && l.Acc.Count == 0 && l.Con.Count == 0 && coef.ContainsKey(l.Code) && Math.Abs(V(l.Code)) > 0.5).ToList();

        var findings = new List<Finding>();
        var mismatch = new List<CfsItem>();
        double lineDiffSum = 0;
        var useNoOf = new Dictionary<string, bool>();
        foreach (var l in flowLines)
        {
            var hit = new List<int>();
            for (var i = 0; i < rows.Count; i++) if (RowMatches(l, rows[i])) hit.Add(i);
            lineRows[l.Code] = hit;
            double sNo = hit.Sum(i => rows[i].No), sCo = hit.Sum(i => rows[i].Co);
            var rv = V(l.Code);
            // thu = bên Nợ TK tiền, chi = bên Có TK tiền; chọn bên theo dấu của chỉ tiêu trên báo cáo (số 0 thì theo dấu chuẩn của mã)
            var useNo = rv > 0.5 || (Math.Abs(rv) <= 0.5 && CashFlowPositiveDirect.Contains(l.Code));
            useNoOf[l.Code] = useNo;
            foreach (var i in hit)
            {
                if (useNo && rows[i].No > 0) covNo[i]++;
                if (!useNo && rows[i].Co > 0) covCo[i]++;
            }
            var js = useNo ? sNo : -sCo;       // dòng tiền ròng theo bảng kê, dấu thu (+) / chi (-)
            lineDiffSum += coef[l.Code] * (rv - js);
            if (Math.Abs(rv - js) > 1)
                mismatch.Add(new CfsItem
                {
                    Severity = "warn", Amount = rv - js,
                    Action = "Kiểm tra khai báo (TK / TK đối ứng) hoặc kỳ dữ liệu của chỉ tiêu " + l.Code + ": báo cáo " + N0(rv) + " ≠ bảng kê " + N0(js) + ".",
                    Title = "Chỉ tiêu " + l.Code + " " + l.Name + ": báo cáo khác bảng kê",
                    Detail = "Báo cáo = " + N0(rv) + " | bảng kê theo khai báo [" + string.Join(",", l.Acc) + " / " + string.Join(",", l.Con) + "] = " + N0(js) +
                             " (bên " + (useNo ? "Nợ" : "Có") + " TK tiền), lệch " + N0(rv - js) + " — bảng kê khác kỳ, báo cáo chưa tính lại, hoặc chỉ tiêu có lấy ngoài bảng kê."
                });
        }

        foreach (var l in blankLines)
        {
            var eff = coef[l.Code] * V(l.Code);
            findings.Add(new Finding { Group = "E", Effect = eff, Action = "Xoá dòng chỉ tiêu " + l.Code + " (hoặc khai TK thật) và bỏ khỏi công thức chỉ tiêu cha.", Title = "Chỉ tiêu " + l.Code + " (dòng " + l.Row + ") để TRỐNG Tài khoản và TK đối ứng", Detail = "Hãy xóa dòng, bỏ khỏi công thức cha, hoặc khai TK thật." });
        }

        // (1) chưa vào chỉ tiêu nào / (2) tính trùng
        double uncoveredNet = 0, doubleExtra = 0;
        var unc = new Dictionary<string, (double No, double Co)>();
        var dbl = new Dictionary<string, double>();
        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i]; var key = r.Dut.Substring(0, Math.Min(4, r.Dut.Length));
            if (r.No > 0 && covNo[i] == 0) { unc.TryGetValue(key, out var u); unc[key] = (u.No + r.No, u.Co); uncoveredNet += r.No; }
            if (r.Co > 0 && covCo[i] == 0) { unc.TryGetValue(key, out var u); unc[key] = (u.No, u.Co + r.Co); uncoveredNet -= r.Co; }
            if (covNo[i] > 1) { doubleExtra += (covNo[i] - 1) * r.No; dbl.TryGetValue(key, out var d); dbl[key] = d + (covNo[i] - 1) * r.No; }
            if (covCo[i] > 1) { doubleExtra -= (covCo[i] - 1) * r.Co; dbl.TryGetValue(key, out var d); dbl[key] = d - (covCo[i] - 1) * r.Co; }
        }
        if (unc.Count > 0)
        {
            var sec = new CfsSection
            {
                Id = "cfd-uncovered", Area = "cfd", Title = "Dòng tiền trong bảng kê KHÔNG vào chỉ tiêu nào", Total = -uncoveredNet,
                Note = "Theo TK đối ứng (4 số đầu). Số = tác động lên LCTT (thiếu tiền thu thì LCTT thấp hơn sổ). Khai thêm TK đối ứng vào chỉ tiêu phù hợp."
            };
            foreach (var kv in unc.OrderByDescending(k => Math.Abs(k.Value.No - k.Value.Co)).Take(40))
                sec.Items.Add(new CfsItem
                {
                    Severity = "error", Amount = -(kv.Value.No - kv.Value.Co),
                    Action = "Khai TK đối ứng " + kv.Key + "x vào chỉ tiêu LCTT phù hợp (thu chưa hứng " + N0(kv.Value.No) + ", chi chưa hứng " + N0(kv.Value.Co) + ").",
                    Title = "TK đối ứng " + kv.Key + "x",
                    Detail = "Thu (Nợ TK tiền) chưa hứng: " + N0(kv.Value.No) + " | Chi (Có TK tiền) chưa hứng: " + N0(kv.Value.Co)
                });
            res.Sections.Add(sec);
        }
        if (dbl.Count > 0)
        {
            var sec = new CfsSection
            {
                Id = "cfd-double", Area = "cfd", Title = "Dòng tiền bị TÍNH TRÙNG ở nhiều chỉ tiêu", Total = doubleExtra,
                Note = "Cùng một dòng chứng từ khớp khai báo của từ 2 chỉ tiêu trở lên (cùng bên Nợ hoặc cùng bên Có) — phần vượt được cộng nhiều lần vào LCTT."
            };
            foreach (var kv in dbl.OrderByDescending(k => Math.Abs(k.Value)).Take(40))
            {
                var which = lineRows.Where(l => useNoOf.ContainsKey(l.Key) && l.Value.Any(i => rows[i].Dut.StartsWith(kv.Key, StringComparison.Ordinal) && (useNoOf[l.Key] ? covNo[i] > 1 : covCo[i] > 1))).Select(l => l.Key).Take(8);
                sec.Items.Add(new CfsItem { Severity = "error", Amount = kv.Value, Action = "Sửa khai báo các chỉ tiêu " + string.Join(", ", which) + " để TK đối ứng " + kv.Key + "x chỉ vào 1 chỉ tiêu.", Title = "TK đối ứng " + kv.Key + "x bị cộng trùng", Detail = "Chỉ tiêu liên quan: " + string.Join(", ", which) + ". Sửa khai báo để mỗi dòng tiền chỉ vào 1 chỉ tiêu." });
            }
            res.Sections.Add(sec);
        }
        if (mismatch.Count > 0)
            res.Sections.Add(new CfsSection { Id = "cfd-mismatch", Area = "cfd", Title = "Chỉ tiêu khác bảng kê", Total = lineDiffSum, Items = mismatch.OrderByDescending(i => Math.Abs(i.Amount ?? 0)).ToList() });

        var periodDiff = jNet - dCash;
        if (Math.Abs(periodDiff) > 1)
            res.Sections.Add(new CfsSection
            {
                Id = "cfd-period", Area = "cfd", Title = "Bảng kê và bảng cân đối phát sinh không cùng số", Total = periodDiff,
                Note = "Dòng tiền ròng trong bảng kê (chứng từ TK tiền đối ứng TK khác) khác biến động tiền trên bảng cân đối phát sinh: bảng kê khác kỳ / thiếu chứng từ / lọc sót, hoặc có TK tiền ngoài danh sách.",
                Items = { new CfsItem { Severity = "warn", Amount = periodDiff, Action = "Xuất lại bảng kê chứng từ đúng kỳ / đủ chứng từ so với bảng cân đối phát sinh (đang lệch " + N0(periodDiff) + ").", Title = "Bảng kê ròng " + N0(jNet) + " so với biến động tiền " + N0(dCash), Detail = "Chuyển tiền nội bộ (tiền ↔ tiền) bỏ qua: " + N0(internalMoves) + " (bên Nợ)." } }
            });

        foreach (var f in findings)
            res.Sections.Add(new CfsSection { Id = "cfd-E", Area = "cfd", Title = "Dòng để trống TK", Total = f.Effect, Items = { new CfsItem { Severity = "error", Amount = f.Effect, Action = f.Action, Title = f.Title, Detail = f.Detail } } });

        var explained = doubleExtra - uncoveredNet + periodDiff + lineDiffSum + findings.Sum(f => f.Effect ?? 0);
        // gap = (50 - dCash) + 61 → thành phần giải thích cho (50 - dCash) là explained; mã 61 là phần kỳ vọng bù lại
        var rest = gap - (explained + v61);
        res.Overview.Add(new CfsOverview { Label = "Đã giải thích (chưa/trùng chỉ tiêu + lệch bảng kê + khác kỳ + dòng trống)", Value = N0(explained) });
        res.Overview.Add(new CfsOverview
        {
            Label = "Chưa giải thích", Value = N0(rest) + (Math.Abs(rest) < 1 ? "   (khớp hoàn toàn)" : "   (khác 0: chỉ tiêu lấy ngoài bảng kê, công thức tính dấu khác, hoặc TK tiền đặc biệt)"),
            Severity = Math.Abs(rest) < 1 ? "ok" : "warn"
        });
    }

    private static bool IsCashList(List<string> acc) => acc.Any(a => Cash.Any(c => a.StartsWith(c, StringComparison.Ordinal) || c.StartsWith(a, StringComparison.Ordinal)));

    // ------------------------------------------------------------------------------------------ chạy
    public static CfsResult Run(CfsInputs inp)
    {
        var res = new CfsResult();
        List<TbAcc>? tb = null;
        List<Jr>? jr = null;

        T? Load<T>(string? path, string label, Func<string, T> loader) where T : class
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (!File.Exists(path)) { res.Messages.Add($"{label}: không thấy file {path}"); return null; }
            try { return loader(path); }
            catch (Exception ex) { res.Messages.Add($"{label}: không đọc được — {ex.Message}"); return null; }
        }

        tb = Load(inp.TrialBalance, "Bảng cân đối phát sinh", LoadTb);
        jr = Load(inp.Journal, "Bảng kê chứng từ", LoadJournal);
        // Chỉ nạp những file thuộc loại báo cáo được chọn.
        var cfCfg = inp.RunIndirect ? Load(inp.CashFlowConfig, "Khai báo chỉ tiêu LCTT gián tiếp", LoadConfig) : null;
        var cfRep = inp.RunIndirect ? Load(inp.CashFlowReport, "Báo cáo LCTT gián tiếp", p => LoadReport(p, 5, 6)) : null;
        var dcCfg = inp.RunDirect ? Load(inp.DirectConfig, "Khai báo chỉ tiêu LCTT trực tiếp", LoadConfig) : null;
        var dcRep = inp.RunDirect ? Load(inp.DirectReport, "Báo cáo LCTT trực tiếp", p => LoadReport(p, 5, 6)) : null;
        var bsCfg = inp.RunBalance ? Load(inp.BalanceConfig, "Khai báo chỉ tiêu CĐKT", LoadConfig) : null;
        var bsRep = inp.RunBalance ? Load(inp.BalanceReport, "Báo cáo CĐKT", p => LoadReport(p, 5, 6)) : null;

        // Mỗi loại báo cáo chạy riêng; nhãn tổng quan / tên mục được gắn thêm tên loại để phân biệt khi chạy nhiều loại.
        void Tagged(string tag, Action run)
        {
            var ovBefore = new HashSet<CfsOverview>(res.Overview);
            var scBefore = new HashSet<CfsSection>(res.Sections);
            run();
            foreach (var o in res.Overview.Where(o => !ovBefore.Contains(o))) o.Label = "[" + tag + "] " + o.Label;
            foreach (var sct in res.Sections.Where(x => !scBefore.Contains(x))) sct.Title = "[" + tag + "] " + sct.Title;
        }

        if (inp.RunIndirect)
        {
            if (cfCfg != null && cfRep != null && tb != null && jr != null) Tagged("LCTT gián tiếp · " + CircularTag("cf-indirect", cfCfg, "mẫu chưa nhận ra"), () => DiagnoseCashFlow(res, cfCfg, cfRep, tb, jr));
            else if (inp.CashFlowConfig != null || inp.CashFlowReport != null)
                res.Messages.Add("Bỏ qua LCTT gián tiếp: cần đủ khai báo chỉ tiêu + báo cáo + bảng cân đối phát sinh + bảng kê chứng từ.");
        }
        if (inp.RunDirect)
        {
            if (dcCfg != null && dcRep != null && tb != null && jr != null) Tagged("LCTT trực tiếp · " + CircularTag("cf-direct", dcCfg, "TT99"), () => DiagnoseDirect(res, dcCfg, dcRep, tb, jr));
            else if (inp.DirectConfig != null || inp.DirectReport != null)
                res.Messages.Add("Bỏ qua LCTT trực tiếp: cần đủ khai báo chỉ tiêu + báo cáo + bảng cân đối phát sinh + bảng kê chứng từ.");
        }
        if (inp.RunBalance)
        {
            if (bsCfg != null && bsRep != null && tb != null) Tagged("CĐKT · " + CircularTag("bs", bsCfg, "TT99"), () => DiagnoseBalanceSheet(res, bsCfg, bsRep, tb));
            else if (inp.BalanceConfig != null || inp.BalanceReport != null)
                res.Messages.Add("Bỏ qua CĐKT: cần đủ khai báo chỉ tiêu CĐKT + báo cáo CĐKT + bảng cân đối phát sinh.");

            if (bsRep != null && bsCfg != null && tb != null)
            {
                if (cfRep != null) Tagged("Đối chiếu LCTT gián tiếp ↔ CĐKT", () => CrossCheck(res, cfRep, bsRep, bsCfg, tb));
                if (dcRep != null) Tagged("Đối chiếu LCTT trực tiếp ↔ CĐKT", () => CrossCheck(res, dcRep, bsRep, bsCfg, tb));
            }
        }

        if (!inp.RunIndirect && !inp.RunDirect && !inp.RunBalance)
            res.Messages.Add("Chưa chọn loại báo cáo nào để kiểm tra.");
        else if (res.Sections.Count == 0 && res.Messages.Count == 0)
            res.Messages.Add("Chưa đính kèm file nào. Chọn đủ file cho loại báo cáo cần kiểm tra rồi bấm Kiểm tra.");

        BuildSummary(res);
        res.Text = BuildText(res);
        return res;
    }

    // ------------------------------------------------------------------------------------------ LCTT
    private static void DiagnoseCashFlow(CfsResult res, List<Line> cfg, Dictionary<string, (double Cur, double Prev)> repAll, List<TbAcc> tb, List<Jr> jr)
    {
        double V(string c) => repAll.TryGetValue(c, out var v) ? v.Cur : 0;
        var byCode = cfg.GroupBy(l => l.Code).ToDictionary(g => g.Key, g => g.First());
        var leaves = tb.Where(a => a.Leaf).ToList();
        var coef = new Dictionary<string, double>(); Expand(byCode, "50", 1, coef, 0);
        CheckCashFlowGuideline(res, cfg, repAll, tb);
        if (byCode.ContainsKey("20") && byCode.ContainsKey("50"))
            CheckStandard(res, cfg, Tt99CashFlow, "cf-std", "cfi", "Công thức LCTT so với mẫu chuẩn TT99 (B03-DN, gián tiếp)", repAll);
        CheckAgainstStandard(res, CfsStandards.ActiveId("cf-indirect"), "cf-std2", "cfi", cfg, tb);
        CheckLineByLine(res, "cf-indirect", "cf", "cfi", cfg, tb);
        CheckDoubleCount(res, cfg, tb);
        CheckAgainstFast(res, "cf-indirect", "cf", "cfi", cfg, tb);

        var dCash = leaves.Where(a => IsCash(a.Code)).Sum(a => a.Delta);
        var gap = V("50") - dCash;
        res.Overview.Add(new CfsOverview { Label = "LCTT thuần (mã 50)", Value = N0(V("50")) });
        res.Overview.Add(new CfsOverview { Label = "Biến động tiền thực tế trên sổ (111,112,113,12811,12881)", Value = N0(dCash) });
        res.Overview.Add(new CfsOverview { Label = "Chênh lệch LCTT cần giải thích", Value = N0(gap) + "   (70B trên báo cáo = " + N0(V("70B")) + ")", Severity = Math.Abs(gap) < 1 ? "ok" : "error" });

        var balLines = cfg.Where(l => !l.HasFormula && l.Acc.Count > 0 && l.Con.Count == 0 &&
                                      BalancePrefix.Any(p => l.Code.StartsWith(p, StringComparison.Ordinal))).ToList();
        var balPrefix = balLines.SelectMany(l => l.Acc).Distinct().ToList();
        var flowLines = cfg.Where(l => !l.HasFormula && l.Acc.Count > 0 && !balLines.Contains(l) &&
                                       !(l.Code == "60" || l.Code == "70A" || l.Code.StartsWith("61"))).ToList();
        // mẫu của khách đã chạy đúng có dòng để trống (vd 04B, 16C) thì để trống là có chủ ý — không báo
        var okRef = CfsFasts.Reference("cf-indirect", cfg.Select(x => x.Code), cfg.Select(x => x.Name));
        var okBlank = okRef is { Verified: true } ? okRef.Lines.Where(x => x.Formula.Length == 0 && x.Tk.Count + x.TkDu.Count == 0).Select(x => x.Code).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        var blankLines = cfg.Where(l => !l.HasFormula && l.Acc.Count == 0 && l.Con.Count == 0 && coef.ContainsKey(l.Code) && !okBlank.Contains(l.Code)
                                        && Math.Abs(V(l.Code)) > 0.5).ToList();

        var jTotNo = jr.Sum(x => x.No); var jTotCo = jr.Sum(x => x.Co);
        var findings = new List<Finding>();

        foreach (var l in blankLines)
        {
            var eff = coef[l.Code] * V(l.Code);
            var total = Math.Abs(Math.Abs(V(l.Code)) - jTotNo) < 1 || Math.Abs(Math.Abs(V(l.Code)) - jTotCo) < 1;
            findings.Add(new Finding
            {
                Group = "E", Effect = eff,
                Action = "Xoá dòng chỉ tiêu " + l.Code + " (hoặc khai TK thật) và bỏ nó khỏi công thức chỉ tiêu cha.",
                Title = "Chỉ tiêu " + l.Code + " (dòng " + l.Row + ") để TRỐNG Tài khoản và TK đối ứng",
                Detail = (total ? "Giá trị đúng bằng TỔNG phát sinh bảng kê => tool cộng cả bảng kê. " : "") + "Hãy xóa dòng, hoặc bỏ nó khỏi công thức cha, hoặc khai TK thật."
            });
        }

        var flowCap = new Dictionary<string, double>();
        var plBucket = new Dictionary<string, double>();
        var mismatch = new List<CfsItem>();
        foreach (var l in flowLines)
        {
            if (!coef.ContainsKey(l.Code)) continue;
            var rv = V(l.Code); if (Math.Abs(rv) < 0.5) continue;
            var rowsHit = jr.Where(x => Match(x.Tk, l.Acc) && (l.Con.Count == 0 || Match(x.Dut, l.Con))).ToList();
            double sNo = rowsHit.Sum(x => x.No), sCo = rowsHit.Sum(x => x.Co);
            var useNo = Math.Abs(sNo - Math.Abs(rv)) <= Math.Abs(sCo - Math.Abs(rv));
            var used = useNo ? sNo : sCo;
            if (Math.Abs(used - Math.Abs(rv)) > 1)
                mismatch.Add(new CfsItem
                {
                    Severity = "warn", Amount = rv,
                    Action = "Kiểm tra khai báo (TK / TK đối ứng) của chỉ tiêu " + l.Code + " " + l.Name + ": báo cáo " + N0(rv) + " khác tổng bảng kê theo khai báo.",
                    Title = "Chỉ tiêu " + l.Code + " " + l.Name,
                    Detail = "Báo cáo = " + N0(rv) + " | bảng kê: Nợ = " + N0(sNo) + ", Có = " + N0(sCo) + " (lệch dữ liệu / sai khai báo / bảng kê khác kỳ?)  — khai báo: TK [" + string.Join(",", l.Acc) + "] đối ứng [" + string.Join(",", l.Con) + "]"
                });
            var contrib = coef[l.Code] * rv;
            if (used < 0.5) continue;
            foreach (var x in rowsHit)
            {
                var amt = useNo ? x.No : x.Co; if (amt == 0) continue;
                var target = IsBs(x.Tk) ? x.Tk : (IsBs(x.Dut) ? x.Dut : null);
                var part = contrib * amt / used;
                if (target != null) { flowCap.TryGetValue(target, out var o); flowCap[target] = o + part; }
                else { plBucket.TryGetValue(l.Code, out var o); plBucket[l.Code] = o + part; }
            }
        }
        if (mismatch.Count > 0)
            res.Sections.Add(new CfsSection { Id = "cf-mismatch", Area = "cfi", Title = "Dòng flow không khớp bảng kê", Note = "Chỉ tiêu trên báo cáo khác tổng bảng kê theo đúng khai báo của nó.", Items = mismatch });

        foreach (var kv in plBucket)
        {
            var l = byCode[kv.Key];
            findings.Add(new Finding
            {
                Group = "B", Effect = kv.Value,
                Title = "Điều chỉnh P&L " + l.Code + " [" + string.Join(",", l.Acc) + " / " + string.Join(",", l.Con) + "]",
                Detail = "Đóng góp vào LCTT: " + N0(kv.Value) + ". Hợp lệ khi có khoản tương ứng ở TK bảng cân đối."
            });
        }

        var perAcc = new List<KeyValuePair<TbAcc, double>>();
        foreach (var a in leaves.Where(a => IsBs(a.Code)))
        {
            flowCap.TryGetValue(a.Code, out var fc);
            var inBal = Match(a.Code, balPrefix);
            var eff = fc - (inBal ? 0 : -a.Delta);
            if (Math.Abs(eff) > 0.5) perAcc.Add(new KeyValuePair<TbAcc, double>(a, eff));
        }
        // R. Bút toán kết chuyển KHÔNG dùng tiền giữa 1 TK thuộc nhóm tính theo số dư (vd 154 ở chỉ tiêu 10) và 1 TK ngoài nhóm, không có dòng flow nào bù (vd 213 → 154):
        //    chỉ tiêu nhóm số dư thấy 154 tăng (làm LCTT giảm) nhưng 213 giảm không được ghi nhận ở đâu → lệch đúng bằng số kết chuyển. Cách sửa: khai thêm 1 dòng flow.
        var reclassByBal = new Dictionary<string, List<(string Fam, string Acc, double Net, List<Jr> Rows)>>();
        var reclassDone = new Dictionary<string, double>();     // họ TK ngoài nhóm → phần chênh lệch đã quy cho R
        foreach (var g in perAcc.GroupBy(k => k.Key.Code.Substring(0, Math.Min(3, k.Key.Code.Length))))
        {
            if (g.Any(k => Match(k.Key.Code, balPrefix) || flowCap.ContainsKey(k.Key.Code))) continue;
            var codes = g.Select(k => k.Key.Code).ToHashSet(StringComparer.Ordinal);
            foreach (var byBal in jr.Where(x => codes.Contains(x.Tk) && Match(x.Dut, balPrefix) && !IsCash(x.Dut) && Math.Abs(x.No - x.Co) > 0.5)
                                    .GroupBy(x => x.Dut.Substring(0, Math.Min(3, x.Dut.Length))))
            {
                var net = byBal.Sum(x => x.No - x.Co);
                if (!reclassByBal.TryGetValue(byBal.Key, out var lst)) reclassByBal[byBal.Key] = lst = new();
                lst.Add((g.Key, byBal.First().Dut, net, byBal.ToList()));
                reclassDone.TryGetValue(g.Key, out var o); reclassDone[g.Key] = o + net;
            }
        }
        foreach (var (balFam, parts) in reclassByBal)
        {
            var total = parts.Sum(x => x.Net);                  // = phần LCTT đang THIẾU (âm) hoặc THỪA (dương) do các bút toán này
            if (Math.Abs(total) < 1) continue;
            var balLine = balLines.FirstOrDefault(l => Match(parts[0].Acc, l.Acc));
            var parent = balLine == null ? null : cfg.FirstOrDefault(l => l.HasFormula && Regex.IsMatch(l.Formula, @"\[" + Regex.Escape(balLine.Code) + @"\]"));
            var baseCode = parent?.Code ?? balLine?.Code ?? "10";
            string newCode = baseCode + "C"; for (var ch = 'C'; ch <= 'Z' && byCode.ContainsKey(baseCode + ch); ch++) newCode = baseCode + (char)(ch + 1);
            var others = parts.Select(x => x.Fam).Distinct().ToList();
            var asDebit = total < 0;                            // TK ngoài nhóm bị ghi Có → TK nhóm số dư bị ghi Nợ
var sample = parts.SelectMany(x => x.Rows).GroupBy(x => x.Voucher + "|" + x.Tk + "|" + x.Dut + "|" + x.Date).Select(g2 => (Row: g2.First(), Amt: g2.Sum(y => y.No - y.Co))).OrderByDescending(x => Math.Abs(x.Amt)).Take(6).ToList();
            var detail = new StringBuilder();
            detail.Append("Tiền KHÔNG đổi — đây là bút toán chuyển giữa TK " + string.Join(", ", others) + " và TK " + balFam + " (TK " + balFam + " nằm ở chỉ tiêu " + (balLine?.Code ?? "tính theo số dư") + ", tính theo chênh lệch số dư nên thấy " + (asDebit ? "tăng" : "giảm") + " " + N0(Math.Abs(total)) + " làm LCTT " + (asDebit ? "giảm" : "tăng") + " theo; còn TK " + string.Join(", ", others) + " không có dòng nào bù lại).");
            foreach (var (x, amt) in sample)
                detail.Append("\n" + (x.Date.Length > 0 ? x.Date + " · " : "") + (x.Voucher.Length > 0 ? x.Voucher + " · " : "") + "TK " + x.Tk + " / đối ứng " + x.Dut + " · " + N0(Math.Abs(amt)) + (x.Desc.Length > 0 ? " · " + x.Desc : ""));
            findings.Add(new Finding
            {
                Group = "R", Effect = total,
                Title = "Kết chuyển không dùng tiền: TK " + string.Join(", ", others) + (asDebit ? " → " : " ← ") + "TK " + balFam + " = " + N0(Math.Abs(total)),
                Action = "Khai thêm 1 dòng chỉ tiêu mới " + newCode + " (dưới chỉ tiêu " + baseCode + "): Cách tính = 1 (theo phát sinh), Phân loại = " + (asDebit ? "1 (Nợ)" : "2 (Có)") + ", Thu/Chi = 1 (Thu), Đầu/Cuối = 1, Các tài khoản = " + balFam + ", Các tài khoản đối ứng = " + string.Join(",", others) +
                         "; rồi sửa công thức chỉ tiêu " + baseCode + " thêm " + (asDebit ? "+" : "-") + "[" + newCode + "] (bút toán chuyển TK " + string.Join(", ", others) + " sang TK " + balFam + " là không tiền, đang làm lệch " + N0(Math.Abs(total)) + ").",
                Detail = detail.ToString()
            });
        }

        foreach (var g in perAcc.GroupBy(k => k.Key.Code.Substring(0, Math.Min(3, k.Key.Code.Length))))
        {
            var eff = g.Sum(k => k.Value); if (reclassDone.TryGetValue(g.Key, out var covered)) eff -= covered;
            if (Math.Abs(eff) < 0.5) continue;
            var sb = new StringBuilder();
            foreach (var k in g)
            {
                flowCap.TryGetValue(k.Key.Code, out var fc);
                var inBal = Match(k.Key.Code, balPrefix);
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("TK " + k.Key.Code + " " + k.Key.Name + ": biến động sổ " + N0(k.Key.Delta) +
                          (inBal ? " | thuộc nhóm số dư 09-13, chỉ tiêu flow cộng thêm " + N0(fc)
                                 : " | KHÔNG thuộc nhóm số dư, flow lấy " + N0(fc) + " so với đúng " + N0(-k.Key.Delta)));
            }
            var anyFlow = g.Any(k => flowCap.ContainsKey(k.Key.Code));
            string Decl(Line l) => l.Code + " " + l.Name.Trim() + " (TK [" + string.Join(",", l.Acc) + "]" + (l.Con.Count > 0 ? ", đối ứng [" + string.Join(",", l.Con) + "]" : "") + ")";
            string whereNow;
            if (anyFlow)
            {
                var hitLines = flowLines.Concat(balLines).Where(l => g.Any(k => Match(k.Key.Code, l.Acc) || (l.Con.Count > 0 && Match(k.Key.Code, l.Con)))).Take(5).ToList();
                whereNow = hitLines.Count > 0 ? " TK này hiện được khai ở: " + string.Join("; ", hitLines.Select(Decl)) + " — kiểm tra lại TK / TK đối ứng / cách tính của các dòng đó." : "";
            }
            else
            {
                // chưa chỉ tiêu nào lấy họ TK này: chỉ ra chỉ tiêu mà mẫu chuẩn TT99 và bản Fast xếp họ TK này vào
                var kids = g.Select(k => k.Key.Code).ToList();
                var stdI = CfsStandards.Get(CfsStandards.ActiveId("cf-indirect"));
                var fastI = CfsFasts.Reference("cf-indirect", cfg.Select(l => l.Code), cfg.Select(l => l.Name));
                var stdHit = stdI?.Lines.Where(l => l.Sources.Any(sc => sc.Acc.Any(ac => kids.Any(c => AccOverlap(c, ac))))).Take(4).ToList() ?? new();
                var fastHit = fastI?.Lines.Where(l => l.Kind != 0 && l.Tk.Concat(l.TkDu).Any(ac => kids.Any(c => AccOverlap(c, ac)))).Take(5).ToList() ?? new();
                var declaredHit = cfg.Where(l => !l.HasFormula && l.Acc.Any(ac => kids.Any(c => AccOverlap(c, ac)))).Select(l => l.Code).Take(6).ToList();
                var sbw = new StringBuilder();
                sbw.Append(" Mẫu chuẩn TT99 xếp họ TK " + g.Key + " vào: " + (stdHit.Count > 0 ? string.Join("; ", stdHit.Select(l => "chỉ tiêu " + l.Code + " " + l.Name.Trim())) : "không chỉ tiêu nào (thường là bút toán chuyển / kết chuyển không dùng tiền — xem mục NGUYÊN NHÂN CHÍNH)") + ".");
                if (fastHit.Count > 0) sbw.Append(" Fast khai ở: " + string.Join("; ", fastHit.Select(l => l.Code + " (TK " + string.Join(",", l.Tk) + (l.TkDu.Count > 0 ? ", đối ứng " + string.Join(",", l.TkDu) : "") + ")")) + ".");
                if (declaredHit.Count > 0) sbw.Append(" LƯU Ý: TK này ĐÃ nằm trong khai báo của chỉ tiêu " + string.Join(", ", declaredHit) + " — đừng thêm lần nữa (sẽ bị cộng 2 lần); xem TK đối ứng / cách tính của các chỉ tiêu đó.");
                else if (stdHit.Count + fastHit.Count > 0) sbw.Append(" → Thêm các TK con " + string.Join(", ", kids.Take(6)) + " vào ô \"Các tài khoản\" (hoặc \"đối ứng\") của chỉ tiêu đó.");
                whereNow = sbw.ToString();
            }
            findings.Add(new Finding
            {
                Group = g.Any(k => Match(k.Key.Code, balPrefix)) ? "C" : "D", Effect = eff,
                Action = (anyFlow ? "Họ TK " + g.Key + " (" + string.Join(", ", g.Select(k => k.Key.Code).Take(6)) + "): chỉ tiêu lấy TK này đang cho số khác biến động thật trên sổ, lệch " + N0(eff) + "." : "Họ TK " + g.Key + " (" + string.Join(", ", g.Select(k => k.Key.Code).Take(6)) + ") biến động " + N0(eff) + " trên sổ nhưng KHÔNG chỉ tiêu LCTT nào lấy.") + whereNow,
                Title = "Họ TK " + g.Key + ": " + (anyFlow ? "dòng flow lệch so với biến động thật" : "biến động nhưng KHÔNG nằm trong chỉ tiêu nào"),
                Detail = sb.ToString()
            });
        }

        foreach (var a in leaves.Where(a => a.Code[0] >= '5' && Math.Abs(a.CloseNet) > 0.5 && !a.Code.StartsWith("911")))
            findings.Add(new Finding
            {
                Group = "A", Effect = a.CloseNet,
                Action = "Chạy kết chuyển cuối kỳ cho TK " + a.Code + " (còn dư " + N0(a.CloseNet) + ") sang 911.",
                Title = "TK " + a.Code + " " + a.Name + " còn dư " + N0(a.CloseNet) + " chưa kết chuyển sang 911",
                Detail = "Lợi nhuận (01) và CĐKT đều lệch; bảng CĐKT sẽ báo chênh lệch Tổng TS - Tổng NV. Chạy lại kết chuyển cuối kỳ."
            });

        // Các mục B / C / D còn lại triệt tiêu lẫn nhau (tổng = 0) thì không làm lệch tiền: chỉ để tham khảo, không đưa vào "việc cần sửa".
        var restFs = findings.Where(f => f.Group is "B" or "C" or "D").ToList();
        var cashMatches = Math.Abs(gap) < 1;                       // LCTT thuần = biến động tiền thực: tổng không còn gì để sửa
        var balancedAll = restFs.Count > 0 && (Math.Abs(restFs.Sum(f => f.Effect ?? 0)) < 1 || cashMatches);
        if (balancedAll) foreach (var f in restFs) f.Balanced = true;

        string[] order = { "R", "A", "B", "C", "D", "E" };
        string[] gname =
        {
            "NGUYÊN NHÂN CHÍNH — bút toán kết chuyển không dùng tiền chưa được khai báo bù",
            "A. TK loại 5-9 chưa kết chuyển",
            "B. Dòng điều chỉnh chỉ đụng TK loại 5-9 (05x, 27, 01C...)",
            "C. Dòng flow gắn vào TK thuộc nhóm số dư (vd 09L1)",
            "D. TK ở dòng flow / ngoài chỉ tiêu nhưng không thuộc nhóm số dư",
            "E. Dòng để trống TK"
        };
        double explained = 0;
        for (var i = 0; i < order.Length; i++)
        {
            var fs = findings.Where(f => f.Group == order[i]).ToList(); if (fs.Count == 0) continue;
            var secBalanced = fs.All(f => f.Balanced);
            var sec = new CfsSection { Id = "cf-" + order[i], Area = "cfi", Title = gname[i] + (secBalanced && order[i] != "B" ? "  — ĐÃ TỰ BÙ TRỪ, không làm lệch tiền" : ""), Total = fs.Sum(f => f.Effect ?? 0) };
            if (secBalanced) sec.Note = cashMatches && Math.Abs(restFs.Sum(f => f.Effect ?? 0)) >= 1
                ? "LCTT thuần (mã 50) ĐÃ BẰNG biến động tiền thực trên sổ (chênh lệch 0) nên các dòng B + C + D này không làm lệch số cuối — chúng được các chỉ tiêu khác bù lại. Chỉ để tham khảo / chuẩn hoá khai báo, KHÔNG phải việc bắt buộc phải sửa."
                : "Các dòng điều chỉnh B + C + D cộng lại bằng 0 (vd thuế TNDN 333x ↔ 01C, lãi cho vay 138x ↔ 05E/09L2/27): chúng bù trừ nhau nên KHÔNG làm lệch tiền — chỉ để tham khảo, không cần sửa.";
            foreach (var f in fs.OrderByDescending(f => Math.Abs(f.Effect ?? 0)))
            {
                var quiet = order[i] == "B" || f.Balanced;
                sec.Items.Add(new CfsItem { Title = f.Title, Detail = f.Detail, Action = quiet ? "" : f.Action, Amount = f.Effect, Severity = quiet ? "info" : "error" });
                explained += f.Effect ?? 0;
            }
            res.Sections.Add(sec);
        }
        var rest = gap - explained;
        var mainCause = findings.Where(f => f.Group == "R").ToList();
        if (mainCause.Count > 0 && Math.Abs(gap) >= 1)
        {
            var rsum = mainCause.Sum(f => f.Effect ?? 0);
            res.Overview.Add(new CfsOverview
            {
                Label = "Nguyên nhân chính của chênh lệch tiền",
                Value = string.Join("; ", mainCause.Select(f => f.Title)) + (Math.Abs(rsum - gap) < 1 ? "   → giải thích 100% chênh lệch " + N0(gap) : "   → giải thích " + N0(rsum) + " / " + N0(gap)),
                Severity = "error"
            });
        }
        if (!cashMatches) res.Overview.Add(new CfsOverview { Label = "Tổng đã giải thích", Value = N0(explained) });
        if (!cashMatches) res.Overview.Add(new CfsOverview
        {
            Label = "Chưa giải thích", Value = N0(rest) + (Math.Abs(rest) < 1 ? "   (khớp hoàn toàn)" : "   (khác 0: bảng cân đối phát sinh / bảng kê khác kỳ hoặc khác thời điểm với báo cáo)"),
            Severity = Math.Abs(rest) < 1 ? "ok" : "warn"
        });

        // chẩn đoán nguyên nhân gốc (không cộng vào tổng)
        var hints = new List<CfsItem>();
        var lines515 = cfg.Where(l => !l.HasFormula && l.Acc.Any(a => a.StartsWith("515"))).ToList();
        var cov515 = lines515.SelectMany(l => l.Acc).ToList();
        var unc515 = leaves.Where(a => a.Code.StartsWith("515") && !Match(a.Code, cov515) && a.PsCo > 0.5).ToList();
        if (unc515.Count > 0)
        {
            var amt = unc515.Sum(a => a.PsCo);
            var l1 = byCode.Values.Where(l => l.Code.StartsWith("09L1")).Sum(l => V(l.Code));
            hints.Add(new CfsItem
            {
                Severity = Math.Min(amt, l1) > 0.5 ? "error" : "warn", Amount = Math.Min(amt, l1),
                Action = "Thêm TK " + string.Join(", ", unc515.Select(a => a.Code.Substring(0, Math.Min(4, a.Code.Length))).Distinct()) + " vào khai báo chỉ tiêu 05E (và 05G/05H) — hiện thiếu nên 09L1 cộng ngược sai.",
                Title = "Doanh thu tài chính 515 chưa được khai ở 05E/05G/05H",
                Detail = string.Join(", ", unc515.Select(a => a.Code.Substring(0, Math.Min(4, a.Code.Length)) + "=" + N0(a.PsCo)).Distinct()) +
                         ". Chỉ tiêu 09L1 hiện cộng ngược " + N0(l1) + (l1 > 0 ? " => LCTT cao hơn đúng khoảng " + N0(Math.Min(amt, l1)) + " (thêm TK vào 05E)." : ".")
            });
        }
        var l05a = cfg.FirstOrDefault(l => l.Code == "05A");
        if (l05a != null && Math.Abs(V("05A")) > 0.5)
        {
            var contras = jr.Where(x => Match(x.Tk, l05a.Con) && !x.Dut.StartsWith("911"))
                            .GroupBy(x => x.Dut.Substring(0, Math.Min(4, x.Dut.Length)))
                            .Select(g => g.Key + "=" + N0(g.Sum(x => x.No - x.Co))).ToList();
            var assetContra = jr.Any(x => Match(x.Tk, l05a.Con) && new[] { "211", "212", "213", "214", "217", "241" }.Any(p => x.Dut.StartsWith(p)));
            if (!assetContra)
                hints.Add(new CfsItem
                {
                    Severity = "warn", Amount = V("05A"),
                    Action = "Hạch toán lại bút toán (đối ứng 911) của TK " + string.Join(",", l05a.Con) + " sang TK khác (vd 8114), hoặc khai cặp 05A + 22B — 05A đang cộng ngược " + N0(V("05A")) + " không phải thanh lý TSCĐ.",
                    Title = "05A cộng ngược " + N0(V("05A")) + " nhưng " + string.Join(",", l05a.Con) + " KHÔNG đối ứng với TSCĐ (211/214/241)",
                    Detail = "Đối ứng thực tế: " + string.Join(", ", contras) + ". Đây không phải thanh lý TSCĐ: hạch toán lại TK khác (vd 8114) hoặc khai cặp 05A + 22B."
                });
        }
        foreach (var fam in new[] { "635", "711", "811", "632", "8211" })
        {
            var allPrefix = cfg.Where(l => !l.HasFormula).SelectMany(l => l.Acc.Concat(l.Con)).ToList();
            var miss = leaves.Where(a => a.Code.StartsWith(fam) && Math.Abs(a.PsNo - a.PsCo) + a.PsNo > 0.5 && !Match(a.Code, allPrefix))
                             .Select(a => a.Code.Substring(0, Math.Min(4, a.Code.Length)) + "=" + N0(a.PsNo)).Distinct().ToList();
            if (miss.Count > 0)
                hints.Add(new CfsItem { Severity = "warn", Action = "Xem lại họ TK " + fam + ": có phát sinh chưa nằm trong chỉ tiêu nào — cần cộng ngược / trừ ra không?", Title = "Họ TK " + fam + " có phát sinh chưa nằm trong chỉ tiêu nào", Detail = string.Join(", ", miss) + " (kiểm tra có cần cộng ngược/trừ ra không)." });
        }
        if (hints.Count > 0)
            res.Sections.Add(new CfsSection { Id = "cf-hints", Area = "cfi", Title = "Chẩn đoán nguyên nhân gốc (LCTT)", Note = "Không cộng vào tổng ở trên — chỉ ra khai báo nên sửa.", Items = hints });
    }

    // ------------------------------------------------------------------------------------------ CĐKT
    private static string Fold(string s) => new string(s.Normalize(NormalizationForm.FormD).Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).Replace('đ', 'd').Replace('Đ', 'D').ToLowerInvariant();

    private static void DiagnoseBalanceSheet(CfsResult res, List<Line> cfg, Dictionary<string, (double Cur, double Prev)> rep, List<TbAcc> tb)
    {
        var byCode = cfg.GroupBy(l => l.Code).ToDictionary(g => g.Key, g => g.First());
        var leaves = tb.Where(a => a.Leaf).ToList();
        double Cur(string c) => rep.TryGetValue(c, out var v) ? v.Cur : 0;
        double Prev(string c) => rep.TryGetValue(c, out var v) ? v.Prev : 0;

        // 1) chỉ tiêu lá so với số dư TK (cuối kỳ và đầu kỳ)
        var leafMismatch = new List<CfsItem>();
        var leafOk = 0;
        double sumLeafDiffClose = 0;
        foreach (var l in cfg.Where(x => !x.HasFormula && x.Acc.Count > 0 && rep.ContainsKey(x.Code)))
        {
            foreach (var close in new[] { true, false })
            {
                var hit = leaves.Where(a => Match(a.Code, l.Acc) && (l.Con.Count == 0 || true)).ToList();
                if (hit.Count == 0) continue;
                double net = hit.Sum(a => close ? a.CloseNet : a.OpenNet);
                double pos = hit.Sum(a => Math.Max(close ? a.CloseNet : a.OpenNet, 0));
                double neg = hit.Sum(a => Math.Max(-(close ? a.CloseNet : a.OpenNet), 0));
                var reported = close ? Cur(l.Code) : Prev(l.Code);
                var cands = new (string Rule, double V)[] { ("dư Nợ - dư Có", net), ("dư Có - dư Nợ", -net), ("chỉ dư Nợ từng TK", pos), ("chỉ dư Có từng TK", neg), ("-(chỉ dư Nợ từng TK)", -pos), ("-(chỉ dư Có từng TK)", -neg) };
                if (cands.Any(c => Math.Abs(c.V - reported) <= 1)) { if (close) leafOk++; continue; }
                if (Math.Abs(reported) < 0.5 && Math.Abs(net) < 0.5) continue;
                var best = cands.OrderBy(c => Math.Abs(c.V - reported)).First();
                var diff = reported - best.V;
                if (close) sumLeafDiffClose += diff;
                var top = hit.OrderByDescending(a => Math.Abs(close ? a.CloseNet : a.OpenNet)).Take(5)
                             .Select(a => a.Code + " " + N0(close ? a.CloseNet : a.OpenNet));
                leafMismatch.Add(new CfsItem
                {
                    Severity = "error", Amount = diff,
                    Action = "Khai báo lại chỉ tiêu " + l.Code + " " + l.Name + " (" + (close ? "cuối kỳ" : "đầu kỳ") + "): số dư TK [" + string.Join(",", l.Acc) + "] = " + N0(best.V) + " nhưng báo cáo = " + N0(reported) + " (lệch " + N0(diff) + ").",
                    Title = (close ? "Cuối kỳ" : "Đầu kỳ") + " · chỉ tiêu " + l.Code + " " + l.Name,
                    Detail = "Báo cáo = " + N0(reported) + " | số dư TK [" + string.Join(",", l.Acc) + "]: " + N0(net) + " (gần nhất: " + best.Rule + " = " + N0(best.V) + ", lệch " + N0(diff) + "). " +
                             "TK lớn nhất: " + string.Join("; ", top) +
                             ". Nếu chỉ tiêu lấy dư theo từng đối tượng (131/331/138/338...) thì bảng cân đối TK không đủ chi tiết để so — xem lại khai báo hoặc đối chiếu theo đối tượng."
                });
            }
        }
        // Cặp bù trừ do dư theo từng đối tượng: cùng TK nằm ở 1 chỉ tiêu tài sản và 1 chỉ tiêu nguồn vốn, cùng lệch 1 số tiền — Tổng TS và Tổng NV cùng tăng, không phải lỗi khai báo.
        int LeadNum(string c) { var d = new string(c.TakeWhile(char.IsDigit).ToArray()); return d.Length > 0 ? int.Parse(d) : 0; }
        string CodeOfItem(CfsItem it) { var m = Regex.Match(it.Title, @"chỉ tiêu (\S+)"); return m.Success ? m.Groups[1].Value : ""; }
        for (var i = 0; i < leafMismatch.Count; i++)
            for (var j = i + 1; j < leafMismatch.Count; j++)
            {
                var a = leafMismatch[i]; var b = leafMismatch[j];
                if (a.Severity == "info" || b.Severity == "info") continue;
                if (a.Title.Split(' ')[0] != b.Title.Split(' ')[0]) continue;                       // cùng kỳ (Cuối / Đầu)
                if (Math.Abs((a.Amount ?? 0) - (b.Amount ?? 0)) > 1) continue;
                string ca = CodeOfItem(a), cb = CodeOfItem(b);
                if (!byCode.TryGetValue(ca, out var la) || !byCode.TryGetValue(cb, out var lb)) continue;
                if ((LeadNum(ca) < 300) == (LeadNum(cb) < 300)) continue;                          // phải 1 tài sản, 1 nguồn vốn
                if (!la.Acc.Any(x => lb.Acc.Any(y => x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal)))) continue;
                foreach (var it in new[] { a, b })
                {
                    it.Severity = "info"; it.Action = "";
                    it.Detail = "Cặp bù trừ theo đối tượng (" + ca + " ↔ " + cb + ", cùng lệch " + N0(a.Amount ?? 0) + "): số dư TK được tách dư Nợ / dư Có theo từng đối tượng nên lớn hơn số ròng trên bảng cân đối TK; Tổng TS và Tổng NV cùng tăng, không phải lỗi khai báo. " + it.Detail;
                }
            }
        // Cặp bù trừ dạng 2: 131 (dư Nợ từng đối tượng) và 312 (dư Có từng đối tượng) cùng lấy TK 1311: 131 - 312 = số dư ròng của TK — hai chỉ tiêu lệch KHÁC nhau
        // nhưng cộng đúng ra số ròng, tổng TS và tổng NV cùng tăng nên không phải lỗi khai báo (tương tự 132 ↔ 311 với TK 3311).
        for (var i = 0; i < leafMismatch.Count; i++)
            for (var j = i + 1; j < leafMismatch.Count; j++)
            {
                var a = leafMismatch[i]; var b = leafMismatch[j];
                if (a.Severity == "info" || b.Severity == "info") continue;
                if (a.Title.Split(' ')[0] != b.Title.Split(' ')[0]) continue;
                bool close = a.Title.StartsWith("Cuối");
                string ca = CodeOfItem(a), cb = CodeOfItem(b);
                if (!byCode.TryGetValue(ca, out var la) || !byCode.TryGetValue(cb, out var lb)) continue;
                if ((LeadNum(ca) < 300) == (LeadNum(cb) < 300)) continue;
                if (!la.Acc.Any(x => lb.Acc.Any(y => x.StartsWith(y, StringComparison.Ordinal) || y.StartsWith(x, StringComparison.Ordinal)))) continue;
                var (assetCode, assetCfg) = LeadNum(ca) < 300 ? (ca, la) : (cb, lb);
                var liabCode = assetCode == ca ? cb : ca;
                var net = leaves.Where(x => Match(x.Code, assetCfg.Acc)).Sum(x => close ? x.CloseNet : x.OpenNet);
                var rA = close ? Cur(assetCode) : Prev(assetCode); var rB = close ? Cur(liabCode) : Prev(liabCode);
                if (Math.Abs((rA - rB) - net) > 1) continue;
                foreach (var it in new[] { a, b })
                {
                    it.Severity = "info"; it.Action = "";
                    it.Detail = "KHÔNG PHẢI LỖI — cặp bù trừ theo đối tượng (" + assetCode + " ↔ " + liabCode + "): chỉ tiêu " + assetCode + " = " + N0(rA) + " (dư Nợ từng đối tượng), chỉ tiêu " + liabCode + " = " + N0(rB) + " (dư Có từng đối tượng); " + N0(rA) + " - " + N0(rB) + " = " + N0(net) + " đúng bằng số dư ròng của TK trên bảng cân đối phát sinh. " + it.Detail;
                }
            }
        sumLeafDiffClose = leafMismatch.Where(i => i.Severity != "info" && i.Title.StartsWith("Cuối")).Sum(i => i.Amount ?? 0);
        res.Overview.Add(new CfsOverview { Label = "CĐKT: chỉ tiêu lá khớp số dư TK (cuối kỳ)", Value = leafOk + " khớp, " + leafMismatch.Count(i => i.Severity != "info" && i.Title.StartsWith("Cuối")) + " lệch" + (leafMismatch.Any(i => i.Severity == "info") ? " (+ " + leafMismatch.Count(i => i.Severity == "info" && i.Title.StartsWith("Cuối")) + " cặp bù trừ theo đối tượng)" : ""), Severity = leafMismatch.All(i => i.Severity == "info") ? "ok" : "warn" });
        if (leafMismatch.Count > 0)
            res.Sections.Add(new CfsSection
            {
                Id = "bs-leaf", Area = "cdkt", Title = leafMismatch.All(i => i.Severity == "info") ? "Chỉ tiêu CĐKT so với số dư tài khoản — chỉ còn cặp bù trừ theo đối tượng, KHÔNG có lỗi" : "Chỉ tiêu CĐKT lệch so với số dư tài khoản", Total = sumLeafDiffClose,
                Note = "So giá trị chỉ tiêu trên báo cáo với số dư lá trong bảng cân đối phát sinh theo các quy tắc dư Nợ/dư Có thường gặp.",
                Items = leafMismatch.OrderByDescending(i => Math.Abs(i.Amount ?? 0)).ToList()
            });

        // 2) chỉ tiêu cha so với tổng chỉ tiêu con theo công thức
        var formulaMismatch = new List<CfsItem>();
        var referenced = new HashSet<string>();
        foreach (var l in cfg.Where(x => x.HasFormula))
        {
            double calcCur = 0, calcPrev = 0; var parts = new List<string>();
            foreach (Match m in Regex.Matches(l.Formula, @"([+-]?)\s*\[([^\]]+)\]"))
            {
                var code = m.Groups[2].Value.Trim(); var sign = m.Groups[1].Value == "-" ? -1 : 1;
                referenced.Add(code);
                calcCur += sign * Cur(code); calcPrev += sign * Prev(code);
                parts.Add((sign < 0 ? "-" : "+") + code);
            }
            if (!rep.ContainsKey(l.Code)) continue;
            if (Math.Abs(Cur(l.Code) - calcCur) > 1)
                formulaMismatch.Add(new CfsItem
                {
                    Severity = "error", Amount = Cur(l.Code) - calcCur,
                    Action = "Sửa công thức chỉ tiêu " + l.Code + " " + l.Name + " [" + l.Formula + "]: cộng ra " + N0(calcCur) + " nhưng báo cáo " + N0(Cur(l.Code)) + ".",
                    Title = "Cuối kỳ · chỉ tiêu " + l.Code + " " + l.Name + " ≠ tổng chỉ tiêu con",
                    Detail = "Báo cáo = " + N0(Cur(l.Code)) + ", cộng theo công thức [" + l.Formula + "] = " + N0(calcCur) + " (lệch " + N0(Cur(l.Code) - calcCur) + "). Công thức khai báo sai hoặc báo cáo bị sửa tay."
                });
            if (Math.Abs(Prev(l.Code) - calcPrev) > 1)
                formulaMismatch.Add(new CfsItem
                {
                    Severity = "error", Amount = Prev(l.Code) - calcPrev,
                    Action = "Sửa công thức chỉ tiêu " + l.Code + " " + l.Name + " [" + l.Formula + "] (đầu kỳ): cộng ra " + N0(calcPrev) + " nhưng báo cáo " + N0(Prev(l.Code)) + ".",
                    Title = "Đầu kỳ · chỉ tiêu " + l.Code + " " + l.Name + " ≠ tổng chỉ tiêu con",
                    Detail = "Báo cáo = " + N0(Prev(l.Code)) + ", cộng theo công thức [" + l.Formula + "] = " + N0(calcPrev) + " (lệch " + N0(Prev(l.Code) - calcPrev) + ")."
                });
        }
        if (formulaMismatch.Count > 0)
            res.Sections.Add(new CfsSection { Id = "bs-formula", Area = "cdkt", Title = "Chỉ tiêu cha không bằng tổng chỉ tiêu con", Items = formulaMismatch });

        CheckAgainstStandard(res, CfsStandards.ActiveId("bs"), "bs-std2", "cdkt", cfg, tb);
        CheckLineByLine(res, "bs", "bs", "cdkt", cfg, tb);
        CheckAgainstFast(res, "bs", "bs", "cdkt", cfg, tb);
        if (byCode.ContainsKey("280") && byCode.ContainsKey("440"))
            CheckStandard(res, cfg, Tt99Balance, "bs-std", "cdkt", "Công thức CĐKT so với mẫu chuẩn TT99 (B01-DN)", rep);

        // 3) chỉ tiêu lá có số nhưng không được cộng vào chỉ tiêu cha nào
        var orphan = new List<CfsItem>();
        foreach (var l in cfg.Where(x => !x.HasFormula && rep.ContainsKey(x.Code) && !referenced.Contains(x.Code)))
        {
            if (Math.Abs(Cur(l.Code)) < 0.5 && Math.Abs(Prev(l.Code)) < 0.5) continue;
            // dòng chi tiết (vd 420A TK 4211) nằm trong TK của 1 chỉ tiêu khác đã được cộng (vd 420 TK 421) → chỉ để xem chi tiết, không bị bỏ sót
            if (l.Acc.Count > 0 && l.Acc.All(a => cfg.Any(o => o != l && !o.HasFormula && referenced.Contains(o.Code) && o.Acc.Any(p => p.Length < a.Length && a.StartsWith(p, StringComparison.Ordinal))))) continue;
            orphan.Add(new CfsItem
            {
                Severity = "warn", Amount = Cur(l.Code),
                Action = "Thêm [" + l.Code + "] vào công thức chỉ tiêu tổng cha (chỉ tiêu " + l.Code + " " + l.Name + " đang bị bỏ sót, số " + N0(Cur(l.Code)) + ").",
                Title = "Chỉ tiêu " + l.Code + " " + l.Name + " có số nhưng không nằm trong công thức chỉ tiêu nào",
                Detail = "Cuối kỳ " + N0(Cur(l.Code)) + ", đầu kỳ " + N0(Prev(l.Code)) + ". Nếu không phải chỉ tiêu tổng cuối cùng thì số này bị bỏ sót khỏi tổng."
            });
        }
        if (orphan.Count > 0)
            res.Sections.Add(new CfsSection { Id = "bs-orphan", Area = "cdkt", Title = "Chỉ tiêu bị bỏ sót khỏi công thức tổng", Items = orphan });

        // 4) TK có số dư nhưng không thuộc chỉ tiêu lá nào
        var allPrefix = cfg.Where(x => !x.HasFormula).SelectMany(x => x.Acc).Distinct().ToList();
        var uncovered = leaves.Where(a => a.Code[0] >= '1' && a.Code[0] <= '4' && (Math.Abs(a.CloseNet) > 0.5 || Math.Abs(a.OpenNet) > 0.5) && !Match(a.Code, allPrefix)).ToList();
        if (uncovered.Count > 0)
            res.Sections.Add(new CfsSection
            {
                Id = "bs-uncovered", Area = "cdkt", Title = "TK có số dư nhưng không thuộc chỉ tiêu CĐKT nào",
                Total = uncovered.Sum(a => a.CloseNet),
                Note = "Số dư các TK này không lên báo cáo => Tổng TS / Tổng NV lệch đúng bằng số này (nếu TK ngoài bảng không phải TK ngoại bảng).",
                Items = uncovered.OrderByDescending(a => Math.Abs(a.CloseNet)).Select(a => new CfsItem
                {
                    Severity = "error", Amount = a.CloseNet,
                    Action = "Khai TK " + a.Code + " " + a.Name + " (dư " + N0(a.CloseNet) + ") vào chỉ tiêu CĐKT phù hợp.",
                    Title = "TK " + a.Code + " " + a.Name,
                    Detail = "Dư cuối " + N0(a.CloseNet) + ", dư đầu " + N0(a.OpenNet) + ". Khai thêm TK này vào chỉ tiêu phù hợp."
                }).ToList()
            });

        // 5) cân đối Tổng tài sản - Tổng nguồn vốn
        var assetLine = FindTotal(cfg, "tong cong tai san", "280", "270");   // TT99: 280 = 100 + 200 (TT200 cũ: 270)
        var fundLine = FindTotal(cfg, "tong cong nguon von", "440");
        var unclosed = leaves.Where(a => a.Code[0] >= '5' && Math.Abs(a.CloseNet) > 0.5 && !a.Code.StartsWith("911")).ToList();
        var tbNet = leaves.Sum(a => a.CloseNet);
        if (assetLine != null && fundLine != null)
        {
            var d = Cur(assetLine) - Cur(fundLine);
            var dPrev = Prev(assetLine) - Prev(fundLine);
            res.Overview.Add(new CfsOverview { Label = "CĐKT: Tổng tài sản (" + assetLine + ") - Tổng nguồn vốn (" + fundLine + ") cuối kỳ", Value = N0(Cur(assetLine)) + " - " + N0(Cur(fundLine)) + " = " + N0(d), Severity = Math.Abs(d) < 1 ? "ok" : "error" });
            res.Overview.Add(new CfsOverview { Label = "CĐKT: Tổng tài sản - Tổng nguồn vốn đầu kỳ", Value = N0(dPrev), Severity = Math.Abs(dPrev) < 1 ? "ok" : "error" });
            if (Math.Abs(d) >= 1)
            {
                var sec = new CfsSection { Id = "bs-balance", Area = "cdkt", Title = "Bảng CĐKT mất cân đối (Tổng TS ≠ Tổng NV)", Total = d, Note = "Các nguyên nhân thường gặp, đối chiếu từng cái với số chênh lệch:" };
                sec.Items.Add(new CfsItem { Severity = "info", Amount = tbNet, Title = "Tổng số dư cuối (Nợ - Có) toàn bộ TK lá trên bảng cân đối phát sinh", Detail = Math.Abs(tbNet) < 1 ? "Bằng 0: bảng cân đối phát sinh tự nó cân — lệch nằm ở khai báo / báo cáo." : "Khác 0: chính bảng cân đối phát sinh không cân (số liệu sổ lỗi hoặc file không đủ TK)." });
                foreach (var a in unclosed)
                    sec.Items.Add(new CfsItem { Severity = "error", Amount = a.CloseNet, Action = "Chạy kết chuyển cuối kỳ cho TK " + a.Code + " (còn dư " + N0(a.CloseNet) + ") sang 911.", Title = "TK " + a.Code + " " + a.Name + " còn dư " + N0(a.CloseNet) + " chưa kết chuyển sang 911", Detail = "Lãi/lỗ chưa vào 421 nên CĐKT mất cân đối. Chạy lại kết chuyển cuối kỳ." });
                if (uncovered.Count > 0)
                    sec.Items.Add(new CfsItem { Severity = "error", Amount = uncovered.Sum(a => a.CloseNet), Title = "TK có số dư nhưng chưa thuộc chỉ tiêu nào (" + uncovered.Count + " TK)", Detail = "Xem mục \"TK có số dư nhưng không thuộc chỉ tiêu CĐKT nào\"." });
                if (leafMismatch.Count > 0)
                    sec.Items.Add(new CfsItem { Severity = "warn", Amount = sumLeafDiffClose, Title = "Tổng lệch của các chỉ tiêu lá so với số dư TK", Detail = "Xem mục \"Chỉ tiêu CĐKT lệch so với số dư tài khoản\"." });
                if (formulaMismatch.Count > 0)
                    sec.Items.Add(new CfsItem { Severity = "warn", Title = "Có chỉ tiêu cha không bằng tổng chỉ tiêu con (" + formulaMismatch.Count + ")", Detail = "Xem mục \"Chỉ tiêu cha không bằng tổng chỉ tiêu con\"." });
                res.Sections.Insert(0, sec);
            }
        }
        else
        {
            res.Messages.Add("CĐKT: không tìm thấy chỉ tiêu Tổng cộng tài sản / Tổng cộng nguồn vốn trong khai báo (đã thử theo tên, rồi mã 280/270 và 440) nên không kiểm tra cân đối.");
        }
    }

    private static string? FindTotal(List<Line> cfg, string nameKey, params string[] codes)
    {
        var byName = cfg.FirstOrDefault(l => Fold(l.Name).Contains(nameKey));
        if (byName != null) return byName.Code;
        foreach (var c in codes) if (cfg.Any(l => l.Code == c && l.HasFormula)) return c;
        return null;
    }

    // ------------------------------------------------------------------------------------------ chéo LCTT ↔ CĐKT
    private static void CrossCheck(CfsResult res, Dictionary<string, (double Cur, double Prev)> cf, Dictionary<string, (double Cur, double Prev)> bs, List<Line> bsCfg, List<TbAcc> tb)
    {
        var cashLine = bsCfg.Where(l => !l.HasFormula && l.Acc.Any(a => a.StartsWith("111") || a.StartsWith("112"))).Select(l => l.Code).FirstOrDefault();
        if (cashLine == null || !bs.ContainsKey(cashLine)) return;
        var sec = new CfsSection { Id = "cross", Area = "cross", Title = "Đối chiếu tiền: LCTT ↔ CĐKT" };
        // Tiền cuối kỳ: cộng các chỉ tiêu tiền của CĐKT cha (110 nếu có, nếu không thì chỉ tiêu có 111/112)
        var parent = bsCfg.FirstOrDefault(l => l.HasFormula && l.Formula.Contains("[" + cashLine + "]"));
        var cashCode = parent != null && bs.ContainsKey(parent.Code) ? parent.Code : cashLine;
        var bsClose = bs[cashCode].Cur; var bsOpen = bs[cashCode].Prev;
        foreach (var (code, label, bsVal) in new[] { ("70", "Tiền cuối kỳ (LCTT mã 70)", bsClose), ("60", "Tiền đầu kỳ (LCTT mã 60)", bsOpen) })
        {
            if (!cf.ContainsKey(code)) continue;
            var cfVal = code == "60" ? cf[code].Cur : cf[code].Cur;
            var diff = cfVal - bsVal;
            sec.Items.Add(new CfsItem
            {
                Severity = Math.Abs(diff) < 1 ? "info" : "error", Amount = diff,
                Action = "",   // triệu chứng của các lỗi khai báo bên dưới (nguyên nhân chính nằm ở mục LCTT) — không liệt kê thành việc riêng
                Title = label + " " + (Math.Abs(diff) < 1 ? "khớp CĐKT" : "lệch CĐKT"),
                Detail = "LCTT = " + N0(cfVal) + " | CĐKT chỉ tiêu " + cashCode + " = " + N0(bsVal) + (Math.Abs(diff) < 1 ? "" : " (lệch " + N0(diff) + ")")
            });
        }
        if (sec.Items.Count > 0) res.Sections.Add(sec);
    }

    // ------------------------------------------------------------------------------------------ tổng kết việc cần sửa
    /// <summary>Gom mọi mục có Action thành danh sách "việc cần làm" ở đầu kết quả: lệch chắc chắn trước, số tiền lớn trước, bỏ trùng.</summary>
    private static void BuildSummary(CfsResult res)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new List<(CfsItem Item, string Section)>();
        foreach (var s in res.Sections)
            foreach (var i in s.Items)
                if (i.Action.Length > 0 && i.Severity != "info" && seen.Add(i.Action)) todo.Add((i, s.Title));
        var verdicts = BuildVerdicts(res);
        if (todo.Count == 0)
        {
            if (verdicts.Count > 0) res.Overview.Insert(0, new CfsOverview { Label = "KẾT LUẬN", Value = string.Join("  •  ", verdicts) + "  •  Không có việc cần sửa.", Severity = "ok" });
            return;
        }
        var ordered = todo.OrderBy(t => t.Item.Severity == "error" ? 0 : 1).ThenByDescending(t => Math.Abs(t.Item.Amount ?? 0)).ToList();
        var errors = ordered.Count(t => t.Item.Severity == "error");
        var sec = new CfsSection
        {
            Id = "summary", Area = "summary", Title = "VIỆC CẦN SỬA (" + ordered.Count + ")",
            Note = "Làm lần lượt từ trên xuống rồi chạy kiểm tra lại. 🔴 = chắc chắn sai (làm lệch số), 🟡 = nghi vấn / nên chuẩn hoá. Số bên phải = số tiền bị ảnh hưởng. Mỗi việc ghi rõ phải khai báo cái gì; \"Căn cứ\" là mục chi tiết bên dưới đã chứng minh. Mục nào bù trừ hoặc chỉ để tham khảo đã được loại khỏi danh sách này."
        };
        var n = 0;
        foreach (var (item, section) in ordered.Take(80))
            sec.Items.Add(new CfsItem { Severity = item.Severity, Amount = item.Amount, Title = (++n) + ". " + (item.Severity == "error" ? "🔴 " : "🟡 ") + item.Action, Detail = "Căn cứ: " + section + " — " + item.Title });
        if (ordered.Count > 80) sec.Items.Add(new CfsItem { Severity = "info", Title = "... và " + (ordered.Count - 80) + " việc nhỏ hơn (xem các mục chi tiết)." });
        res.Sections.Insert(0, sec);
        // ---- gom theo CHỈ TIÊU: mỗi chỉ tiêu một mục, liệt kê việc phải làm với chính chỉ tiêu đó (đọc từ 01 trở xuống)
        var byCode = new SortedDictionary<(int, string), (string Code, List<(CfsItem Item, string Section)> Acts)>();
        var general = new List<(CfsItem Item, string Section)>();
        foreach (var t in ordered)
        {
            var m = Regex.Match(t.Item.Action, @"chỉ tiêu (\d{2,3}[A-Za-z]?\d?)(?!\d)", RegexOptions.IgnoreCase);
            if (!m.Success) { general.Add(t); continue; }
            var code = m.Groups[1].Value.ToUpperInvariant(); var bm = Regex.Match(code, @"^\d+");
            var key = (int.Parse(bm.Value), code);
            if (!byCode.TryGetValue(key, out var g)) byCode[key] = g = (code, new List<(CfsItem, string)>());
            g.Acts.Add(t);
        }
        var grp = new CfsSection { Id = "by-code", Area = "summary", Title = "SỬA THEO TỪNG CHỈ TIÊU — làm từ trên xuống (" + byCode.Count + " chỉ tiêu" + (general.Count > 0 ? " + " + general.Count + " việc chưa gắn chỉ tiêu" : "") + ")", Note = "Mỗi mục là MỘT chỉ tiêu trên báo cáo, bên dưới là những gì cần sửa trong khai báo của chính chỉ tiêu đó. Sửa xong chỉ tiêu này mới sang chỉ tiêu kế tiếp, rồi chạy kiểm tra lại. 🔴 = làm lệch số, 🟡 = nghi vấn." };
        foreach (var kv in byCode)
        {
            var acts = kv.Value.Acts; var worst = acts.Any(x => x.Item.Severity == "error") ? "error" : "warn";
            var nm = Regex.Match(acts[0].Item.Title, @"[Cc]hỉ tiêu " + Regex.Escape(kv.Value.Code) + @"\s*[-+]?\s*(?!TK )([^:—(]{4,80})");
            var mx = acts.Where(x => x.Item.Severity == "error").Select(x => Math.Abs(x.Item.Amount ?? 0)).DefaultIfEmpty(0).Max();
            grp.Items.Add(new CfsItem
            {
                Severity = worst, Amount = mx > 0 ? mx : null,
                Title = "Chỉ tiêu " + kv.Value.Code + (nm.Success && !nm.Groups[1].Value.TrimStart().StartsWith("là ") ? " — " + Regex.Replace(nm.Groups[1].Value.Trim(), @"^([-+]|\d+\.)\s*", "") : "") + "  (" + acts.Count + " việc)",
                Detail = string.Join("\n", acts.Select(x => (x.Item.Severity == "error" ? "🔴 " : "🟡 ") + x.Item.Action))
            });
        }
        if (general.Count > 0)
            grp.Items.Add(new CfsItem { Severity = general.Any(x => x.Item.Severity == "error") ? "error" : "warn", Title = "Chưa gắn được vào chỉ tiêu nào — cần chọn chỉ tiêu để khai (" + general.Count + " việc)", Detail = string.Join("\n", general.Select(x => (x.Item.Severity == "error" ? "🔴 " : "🟡 ") + x.Item.Action)) });
        res.Sections.Insert(0, grp);
        res.Overview.Insert(0, new CfsOverview { Label = "KẾT LUẬN", Value = (verdicts.Count > 0 ? string.Join("  •  ", verdicts) + "  •  " : "") + ordered.Count + " việc cần sửa (" + errors + " chắc chắn sai, " + (ordered.Count - errors) + " nghi vấn)." + (res.Sections.FirstOrDefault(x => x.Id == "cf-dup")?.Items.FirstOrDefault() is { } dup ? " Nguyên nhân chính làm lệch tiền: " + dup.Title + (dup.Amount.HasValue ? " (" + N0(dup.Amount.Value) + ")" : "") + "." : res.Sections.FirstOrDefault(x => x.Id == "cf-R")?.Items.FirstOrDefault() is { } main ? " Nguyên nhân chính làm lệch tiền: " + main.Title + "." : " Việc quan trọng nhất: " + ordered[0].Item.Action), Severity = errors > 0 ? "error" : "warn" });
    }

    /// <summary>Câu kết luận ngắn từng báo cáo: ĐÚNG / SAI ở mức tổng (tiền khớp, cân đối TS = NV) — để đọc là biết ngay, chi tiết từng việc ở các mục dưới.</summary>
    private static List<string> BuildVerdicts(CfsResult res)
    {
        var v = new List<string>();
        foreach (var o in res.Overview)
        {
            if (o.Label.EndsWith("Chênh lệch LCTT cần giải thích"))
                v.Add(o.Severity == "ok" ? "✔ LCTT: lưu chuyển tiền thuần KHỚP biến động tiền thực trên sổ (lệch 0)" : "✖ LCTT: lưu chuyển tiền thuần LỆCH biến động tiền thực " + o.Value.Split(' ')[0]);
            else if (o.Label.Contains("Tổng tài sản") && o.Label.Contains("cuối kỳ"))
                v.Add(o.Value.TrimEnd().EndsWith("= 0") ? "✔ CĐKT: Tổng tài sản = Tổng nguồn vốn" : "✖ CĐKT: Tổng tài sản ≠ Tổng nguồn vốn (" + o.Value + ")");
        }
        foreach (var s in res.Sections.Where(x => x.Id == "cross"))
        {
            var bad = s.Items.Where(i => i.Severity == "error").ToList();
            v.Add(bad.Count == 0 ? "✔ Tiền đầu kỳ / cuối kỳ của LCTT khớp CĐKT" : "✖ Tiền LCTT lệch CĐKT: " + string.Join("; ", bad.Select(b => b.Title)));
        }
        return v;
    }

    // ------------------------------------------------------------------------------------------ báo cáo text
    private static string BuildText(CfsResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== TỔNG QUAN ===");
        foreach (var o in r.Overview) sb.AppendLine(o.Label + ": " + o.Value);
        foreach (var s in r.Sections)
        {
            sb.AppendLine();
            sb.AppendLine("=== " + s.Title + (s.Total.HasValue ? "  => " + N0(s.Total.Value) : "") + " ===");
            if (s.Note.Length > 0) sb.AppendLine(s.Note);
            foreach (var i in s.Items)
            {
                sb.AppendLine("  - " + i.Title + (i.Amount.HasValue ? "  [" + N0(i.Amount.Value) + "]" : ""));
                if (i.Detail.Length > 0) foreach (var line in i.Detail.Split('\n')) sb.AppendLine("      " + line);
            }
        }
        if (r.Messages.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("=== GHI CHÚ ===");
            foreach (var m in r.Messages) sb.AppendLine("  • " + m);
        }
        return sb.ToString();
    }
}
