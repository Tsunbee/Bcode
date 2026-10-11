using System.Globalization;
using System.Text.RegularExpressions;

namespace Bcode.ScreenDesigner.SampleData;

/// <summary>
/// Dựng dữ liệu mẫu cho 1 màn hình theo 3 lớp (lớp trên chính xác hơn):
///  1. DB trên máy: chứng từ có thật (nếu màn hình là chứng từ và DB có dữ liệu) + danh mục thật; danh mục hệ thống (dmtk, dmthue, dmnt, dmtgnt, dmct, dmdvcs) luôn lấy từ DB.
///  2. Danh mục mẫu tự soạn (<see cref="SampleCatalog"/>) cho phần DB trống (DB chuẩn Release_* không có khách hàng / vật tư).
///  3. Quy tắc nghiệp vụ: tiền = SL × giá, thuế theo mã thuế, tổng cộng khớp các dòng, tài khoản theo vật tư / chi tiết nhất.
/// Ô không nhận ra thì KHÔNG trả — trang tự đoán theo tên biến như trước. Ẩn danh: tên / MST / địa chỉ / điện thoại / email của đối tác thay bằng dữ liệu giả.
/// </summary>
public sealed class SampleDataBuilder
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private readonly SampleRequest _r;
    private readonly SampleDb? _db;
    private readonly Random _rnd = new();
    private readonly SampleResult _out = new();
    private readonly Dictionary<string, string?> _leafCache = new();

    // Đối tượng đã chọn cho cả màn hình — mọi ô liên quan (mã, tên, địa chỉ, MST…) lấy cùng 1 đối tượng cho khớp nhau.
    private SampleCatalog.Partner _customer = null!;
    private (string Code, string Name) _site, _employee, _dept, _job, _contract;
    private string _unitCode = "CTY", _unitName = "Công ty";
    private string _currency = "VND";
    private decimal _rate = 1;
    private List<LineItem> _lines = new();

    private sealed record LineItem(string Code, string Name, string Unit, decimal Qty, decimal Price, decimal Cost, string TaxCode, decimal TaxRate,
        string? TkVt, string? TkDt, string? TkGv, string Site, string? Lot);

    public SampleDataBuilder(SampleRequest r, SampleDb? db) { _r = r; _db = db; }

    public SampleResult Build()
    {
        _out.Source = _db is null ? "Danh mục mẫu + quy tắc (không dùng DB)" : "DB " + _db.Database + (_r.Anonymize ? " (ẩn danh đối tác)" : "");
        PickContext();
        Dictionary<string, object?>? master = null;
        List<Dictionary<string, object?>>? detail = null;
        if (_r.UseVoucher && _db is not null && MasterBase() is { } mb)
        {
            try { if (_db.LatestVoucher(mb, _r.VoucherId, Math.Max(1, _r.Rows)) is { } v) { master = v.Master; detail = v.Detail; } }
            catch (Exception ex) { _out.Notes.Add("Không đọc được chứng từ: " + ex.Message); }
            if (master is null) _out.Notes.Add("DB chưa có chứng từ " + (_r.VoucherId ?? mb) + " — dùng danh mục + quy tắc.");
            else _out.Source += " — chứng từ " + Str(master, "so_ct") + " ngày " + Fmt(master.GetValueOrDefault("ngay_ct"), "date");
        }
        if (master is not null) FromVoucher(master, detail ?? new());
        else FromRules();
        return _out;
    }

    // ------------------------------------------------------------------ chọn đối tượng dùng chung

    private void PickContext()
    {
        _customer = PickPartner();
        _site = Pick("dmkho", "ma_kho", "ten_kho", SampleCatalog.Sites);
        _employee = Pick("dmnv", "ma_nv", "ten_nv", SampleCatalog.Employees, person: true);
        _dept = Pick("dmbp", "ma_bp", "ten_bp", SampleCatalog.Departments);
        _job = Pick("dmvv", "ma_vv", "ten_vv", SampleCatalog.Jobs);
        _contract = Pick("dmhd", "ma_hd", "ten_hd", SampleCatalog.Contracts);
        if (_db?.Random("dmdvcs", 1).FirstOrDefault() is { } u) { _unitCode = Str(u, "ma_dvcs"); _unitName = _r.Anonymize ? "Công ty Cổ phần Mẫu" : Str(u, "ten_dvcs"); }
        if (_db?.Random("dmtgnt", 1, "ma_nt = @m", ("@m", _currency)).FirstOrDefault() is { } tg && tg.GetValueOrDefault("ty_gia") is decimal r && r > 0) _rate = r;
        _lines = PickLines(Math.Clamp(_r.Rows, 1, 10));
    }

    private SampleCatalog.Partner PickPartner()
    {
        var fake = SampleCatalog.Customers[_rnd.Next(SampleCatalog.Customers.Length)];
        if (_db is null || _r.Anonymize) return fake;
        var row = _db.Random("dmkh", 1, _db.Has("dmkh", "kh_yn") ? "kh_yn = 1" : null).FirstOrDefault() ?? _db.Random("dmkh", 1).FirstOrDefault();
        return row is null ? fake : new SampleCatalog.Partner(Str(row, "ma_kh"), Str(row, "ten_kh"), Str(row, "ong_ba"), Str(row, "dia_chi"), Str(row, "ma_so_thue"), Str(row, "dien_thoai"), Str(row, "e_mail"));
    }

    private (string, string) Pick(string table, string code, string name, (string, string)[] fallback, bool person = false)
    {
        var fb = fallback[_rnd.Next(fallback.Length)];
        if (_db is null || (person && _r.Anonymize)) return fb;
        var row = _db.Random(table, 1).FirstOrDefault();
        return row is null ? fb : (Str(row, code), Str(row, name));
    }

    private List<LineItem> PickLines(int n)
    {
        var list = new List<LineItem>();
        var dbItems = _db?.Random("dmvt", n) ?? new();
        decimal[] qtys = { 5, 10, 12, 20, 25, 50, 100 };
        for (var i = 0; i < n; i++)
        {
            string code, name, unit, taxCode; decimal price; string? tkVt = null, tkDt = null, tkGv = null, lot = null;
            if (i < dbItems.Count)
            {
                var it = dbItems[i];
                code = Str(it, "ma_vt"); name = Str(it, "ten_vt"); unit = Str(it, "dvt"); taxCode = Str(it, "ma_thue");
                tkVt = NullIfEmpty(Str(it, "tk_vt")); tkDt = NullIfEmpty(Str(it, "tk_dt")); tkGv = NullIfEmpty(Str(it, "tk_gv"));
                price = SampleCatalog.Items[i % SampleCatalog.Items.Length].Price;   // danh mục vật tư thường không có giá bán → giá mẫu theo khoảng thực tế
                if (_db!.Random("dmlo", 1, "ma_vt = @v", ("@v", code)).FirstOrDefault() is { } lo) lot = Str(lo, "ma_lo");
            }
            else
            {
                var it = SampleCatalog.Items[(i + _rnd.Next(SampleCatalog.Items.Length)) % SampleCatalog.Items.Length];
                if (list.Any(l => l.Code == it.Code)) it = SampleCatalog.Items.First(x => list.All(l => l.Code != x.Code));
                code = it.Code; name = it.Name; unit = it.Unit; taxCode = it.TaxCode; price = it.Price;
            }
            if (string.IsNullOrEmpty(taxCode)) taxCode = "10";
            var rate = TaxRate(taxCode);
            var cost = Math.Round(price * 0.78m / 100m) * 100m;
            list.Add(new LineItem(code, name, unit, qtys[_rnd.Next(qtys.Length)], price, cost, taxCode, rate,
                tkVt ?? Leaf("156") ?? "1561", tkDt ?? Leaf("511") ?? "5111", tkGv ?? Leaf("632") ?? "632", _site.Code, lot ?? $"L{DateTime.Today:yyMM}{i + 1:00}"));
        }
        return list;
    }

    // ------------------------------------------------------------------ lớp 3: quy tắc

    private void FromRules()
    {
        var sumQty = _lines.Sum(l => l.Qty);
        var sumAmt = _lines.Sum(l => l.Qty * l.Price);
        var sumCost = _lines.Sum(l => l.Qty * l.Cost);
        var sumTax = _lines.Sum(l => Math.Round(l.Qty * l.Price * l.TaxRate / 100m));
        foreach (var f in _r.Fields)
        {
            var v = HeaderValue(f, sumQty, sumAmt, sumCost, sumTax);
            if (v is not null) _out.Values[f.Name] = v;
            if (f.Ref is { Length: > 0 } rf && !_out.Values.ContainsKey(rf) && RefName(f, _out.Values.GetValueOrDefault(f.Name)) is { } rn) _out.Values[rf] = rn;
        }
        foreach (var l in _lines) _out.Rows.Add(LineValues(l));
    }

    private string? HeaderValue(SampleField f, decimal sumQty, decimal sumAmt, decimal sumCost, decimal sumTax)
    {
        var n = f.Name.ToLowerInvariant();
        var nt = _currency != "VND";
        // ---- tổng cộng (khớp các dòng chi tiết)
        if (f.Kind == "num")
        {
            if (n is "ty_gia") return Num(_rate, 2);
            if (Regex.IsMatch(n, @"^t_so_luong")) return Num(sumQty);
            if (Regex.IsMatch(n, @"^t_tien_nt2|^t_tien2")) return Num(n.Contains("_nt") || !nt ? sumAmt : sumAmt * _rate);
            if (Regex.IsMatch(n, @"^t_tien(_nt)?$")) return Num(n.Contains("_nt") || !nt ? sumCost : sumCost * _rate);
            if (Regex.IsMatch(n, @"^t_thue")) return Num(sumTax);
            if (Regex.IsMatch(n, @"^t_tt")) return Num(sumAmt + sumTax);
            if (Regex.IsMatch(n, @"^t_ck|^t_tien_km|^t_thue_km|^t_cp")) return Num(0);
            if (Regex.IsMatch(n, @"thue_suat|^ts")) return Num(_lines[0].TaxRate);
            return null;
        }
        if (f.Kind == "date" || Regex.IsMatch(n, @"^ngay")) return DateTime.Today.ToString("dd/MM/yyyy");
        if (f.Kind == "check") return null;
        // ---- đối tượng / danh mục
        var cat = Catalog.Resolve(f.Ctl, f.Name);
        if (cat is not null)
        {
            switch (cat.Table)
            {
                case "dmkh": return _customer.Code;
                case "dmkho": return _site.Code;
                case "dmnv": return _employee.Code;
                case "dmbp": return _dept.Code;
                case "dmvv": return _job.Code;
                case "dmhd": return _contract.Code;
                case "dmdvcs": return _unitCode;
                case "dmnt": return _currency;
                case "dmthue": return _lines[0].TaxCode;
                case "dmvt": return _lines[0].Code;
                case "dmlo": return _lines[0].Lot;
                case "dmdvt": return _lines[0].Unit;
                case "dmct": return _r.VoucherId;
                case "dmtk": return HeaderAccount(n);
                case "dmmagd": return Transaction().Code;
                case "dmtt": return Pick("dmtt", "ma_tt", "ten_tt", new[] { ("30", "Hạn thanh toán trong vòng 30 ngày") }).Item1;
                default: return FirstCode(cat);
            }
        }
        if (n is "ong_ba" or "nguoi_mua" or "nguoi_nhan" or "nguoi_giao") return _customer.Contact;
        if (n.Contains("dia_chi")) return _customer.Address;
        if (n is "ma_so_thue" or "mst" || n.Contains("ma_so_thue")) return _customer.TaxCode;
        if (n.Contains("dien_thoai") || n is "tel" or "fax") return _customer.Phone;
        if (n.Contains("mail")) return _customer.Email;
        if (n is "ten_kh" || n.StartsWith("ten_kh")) return _customer.Name;
        if (n.Contains("dien_giai")) return "Xuất bán " + _lines[0].Name.ToLowerInvariant() + " theo hợp đồng " + _contract.Code;
        if (n is "so_ct") return VoucherNumber();
        if (n is "so_hd" or "so_hoa_don") return "000" + _rnd.Next(100, 999);
        if (n is "so_seri" or "ky_hieu" or "ky_hieu_hd") return "C" + DateTime.Today.ToString("yy") + "TAA";
        return null;
    }

    private string HeaderAccount(string n) =>
        n.Contains("tk_dt") ? _lines[0].TkDt! : n.Contains("tk_gv") ? _lines[0].TkGv! : n.Contains("tk_vt") ? _lines[0].TkVt!
        : n.Contains("thue") ? Leaf("3331") ?? "333111" : n.Contains("ck") ? Leaf("521") ?? "5211" : n.Contains("nh") ? Leaf("112") ?? "1121"
        : n.Contains("tm") || n.Contains("quy") ? Leaf("111") ?? "1111" : Leaf("131") ?? "131";

    private Dictionary<string, string> LineValues(LineItem l)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var amt = l.Qty * l.Price; var cost = l.Qty * l.Cost; var tax = Math.Round(amt * l.TaxRate / 100m);
        foreach (var c in _r.GridCols)
        {
            var n = c.Name.ToLowerInvariant().TrimEnd('%', 'l');
            string? v = n switch
            {
                "ma_vt" => l.Code,
                "ten_vt" or "ten_vt%" => l.Name,
                "dvt" => l.Unit,
                "ma_kho" => l.Site,
                "ma_lo" => l.Lot,
                "so_luong" or "sl" => Num(l.Qty),
                "he_so" => Num(1),
                "gia_nt2" or "gia2" or "gia_ban" or "gia_ban_nt" => Num(l.Price),
                "tien_nt2" or "tien2" => Num(amt),
                "gia_nt" or "gia" => Num(l.Cost),
                "tien_nt" or "tien" => Num(cost),
                "ma_thue" => l.TaxCode,
                "thue_suat" or "ts" => Num(l.TaxRate),
                "thue_nt" or "thue" => Num(tax),
                "tt_nt" or "tt" => Num(amt + tax),
                "tk_vt" => l.TkVt,
                "tk_dt" => l.TkDt,
                "tk_gv" => l.TkGv,
                "tk_thue" or "tk_thue_co" => Leaf("3331") ?? "333111",
                "tk_ck" => Leaf("521") ?? "5211",
                "ck_nt" or "ck" or "tl_ck" or "pt_ck" => Num(0),
                "ton" or "sl_ton" or "ton13" => Num(l.Qty * 4 + _rnd.Next(5, 60)),
                "ma_vv" => _job.Code,
                "ma_hd" => _contract.Code,
                "ma_bp" => _dept.Code,
                "ma_nv" or "ma_nvbh" => _employee.Code,
                _ => null,
            };
            if (v is null && n.StartsWith("ten_vt")) v = l.Name;
            if (v is not null) d[c.Name] = v;
        }
        return d;
    }

    // ------------------------------------------------------------------ lớp 1: chứng từ có thật

    private void FromVoucher(Dictionary<string, object?> m, List<Dictionary<string, object?>> detail)
    {
        foreach (var f in _r.Fields)
        {
            if (!m.TryGetValue(f.Name, out var raw) || raw is null) continue;
            var v = Fmt(raw, f.Kind);
            if (_r.Anonymize) v = Anon(f.Name, v);
            if (v.Length > 0) _out.Values[f.Name] = v;
            if (f.Ref is { Length: > 0 } rf && RefName(f, v) is { } rn) _out.Values[rf] = rn;
        }
        if (_r.Anonymize) foreach (var f in _r.Fields.Where(f => f.Name.Contains("dien_giai", StringComparison.OrdinalIgnoreCase)))
            _out.Values[f.Name] = "Xuất bán hàng hóa theo hợp đồng " + _contract.Code;
        foreach (var row in detail)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _r.GridCols)
            {
                var key = c.Name.TrimEnd('%', 'l');
                if (row.TryGetValue(c.Name, out var raw) && raw is not null) d[c.Name] = Fmt(raw, c.Kind);
                else if (key.StartsWith("ten_vt", StringComparison.OrdinalIgnoreCase) && row.GetValueOrDefault("ma_vt") is string mv && _db!.Find("dmvt", "ma_vt", mv) is { } it) d[c.Name] = Str(it, "ten_vt");
            }
            _out.Rows.Add(d);
        }
        // Ô không có trong bảng chứng từ → bổ sung theo quy tắc (giữ giá trị thật đã có)
        var keep = new Dictionary<string, string>(_out.Values, StringComparer.OrdinalIgnoreCase);
        FromRulesHeaderOnly();
        foreach (var kv in keep) _out.Values[kv.Key] = kv.Value;
    }

    private void FromRulesHeaderOnly()
    {
        var rows = _out.Rows; _out.Rows = new();
        FromRules();
        _out.Rows = rows;
    }

    // ------------------------------------------------------------------ tra tên / ẩn danh / định dạng

    private string? RefName(SampleField f, string? code)
    {
        if (string.IsNullOrEmpty(code)) return null;
        var cat = Catalog.Resolve(f.Ctl, f.Name);
        if (cat is null) return null;
        if (cat.Table == "dmkh" && (_r.Anonymize || code == _customer.Code)) return _customer.Name;
        if (_r.Anonymize && cat.Table == "dmnv") return _employee.Name;      // người: ẩn danh
        if (_r.Anonymize && cat.Table == "dmdvcs") return _unitName;
        if (cat.Table == "dmmagd") return Transaction(code).Name;   // khoá kép (ma_ct, ma_gd): tên theo đúng loại chứng từ
        if (_db?.Find(cat.Table, cat.Code, code) is { } row && Str(row, cat.Name) is { Length: > 0 } name) return name;
        return cat.Table switch
        {
            "dmkho" => _site.Name, "dmnv" => _employee.Name, "dmbp" => _dept.Name, "dmvv" => _job.Name, "dmhd" => _contract.Name, "dmdvcs" => _unitName,
            "dmvt" => _lines.FirstOrDefault(l => l.Code == code)?.Name,
            "dmtk" => SampleCatalog.Accounts.GetValueOrDefault(code),
            "dmthue" => SampleCatalog.Taxes.TryGetValue(code, out var t) ? t.Name : null,
            "dmnt" => SampleCatalog.Currencies.GetValueOrDefault(code),
            _ => null,
        };
    }

    private string Anon(string field, string v)
    {
        var n = field.ToLowerInvariant();
        if (n.StartsWith("ma_kh")) return _customer.Code;
        if (n is "ong_ba" || n.StartsWith("nguoi_")) return _customer.Contact;
        if (n.Contains("dia_chi")) return _customer.Address;
        if (n.Contains("ma_so_thue") || n == "mst") return _customer.TaxCode;
        if (n.Contains("dien_thoai") || n == "fax") return _customer.Phone;
        if (n.Contains("mail")) return _customer.Email;
        if (n.StartsWith("ten_kh")) return _customer.Name;
        return v;
    }

    /// <summary>Mã giao dịch của loại chứng từ đang thiết kế (dmmagd theo ma_ct) — ưu tiên "2" (vd HDA: Hóa đơn kiêm phiếu xuất).</summary>
    private (string Code, string Name) Transaction(string? code = null)
    {
        if (_db is not null && _r.VoucherId is { } id && _db.Has("dmmagd", "ma_ct"))
        {
            var rows = code is null ? _db.Random("dmmagd", 20, "ma_ct = @c", ("@c", id)) : _db.Random("dmmagd", 1, "ma_ct = @c AND ma_gd = @g", ("@c", id), ("@g", code));
            var r = rows.FirstOrDefault(x => Str(x, "ma_gd") == "2") ?? rows.OrderBy(x => Str(x, "ma_gd")).FirstOrDefault();
            if (r is not null) return (Str(r, "ma_gd"), Str(r, "ten_gd"));
        }
        return (code ?? "1", "Chứng từ thường");
    }

    private string? FirstCode(Catalog.Def cat) => _db?.Random(cat.Table, 1).FirstOrDefault() is { } r ? Str(r, cat.Code) : null;

    private string VoucherNumber()
    {
        if (_db is not null && _r.VoucherId is { } id && _db.Find("dmct", "ma_ct", id) is { } ct && ct.GetValueOrDefault("so_ct") is { } no
            && decimal.TryParse(Convert.ToString(no, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var k))
            return ((long)k + 1).ToString("0000000");
        return _rnd.Next(1, 999).ToString("0000000");
    }

    private decimal TaxRate(string code)
    {
        if (_db?.Find("dmthue", "ma_thue", code) is { } t && t.GetValueOrDefault("thue_suat") is decimal r) return r;
        return SampleCatalog.Taxes.TryGetValue(code, out var x) ? x.Rate : 10;
    }

    private string? Leaf(string prefix)
    {
        if (_db is null) return null;
        if (!_leafCache.TryGetValue(prefix, out var v)) { try { v = _db.LeafAccount(prefix); } catch { v = null; } _leafCache[prefix] = v; }
        return v;
    }

    private string? MasterBase()
    {
        var t = _r.MasterTable;
        if (string.IsNullOrEmpty(t)) return null;
        var i = t.IndexOf('$');
        var b = i > 0 ? t[..i] : t;
        return Regex.IsMatch(b, @"^[mM]\d{2,3}$") ? b : null;
    }

    private static string Fmt(object? v, string kind) => v switch
    {
        null => "",
        DateTime d => d.ToString("dd/MM/yyyy"),
        decimal m => Num(m),
        double x => Num((decimal)x),
        int or long or short or byte => kind == "check" ? Convert.ToString(v, CultureInfo.InvariantCulture)! : Num(Convert.ToDecimal(v)),
        bool b => b ? "1" : "0",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture)?.Trim() ?? "",
    };

    private static string Num(decimal v, int? dec = null)
    {
        var d = dec ?? (v == Math.Round(v) ? 0 : 2);
        return v.ToString("N" + d, En);
    }

    private static string Str(Dictionary<string, object?> r, string c) => r.TryGetValue(c, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture)!.Trim() : "";
    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;
}

/// <summary>Controller / tên biến → bảng danh mục FastBusiness (mã, tên).</summary>
public static class Catalog
{
    public sealed record Def(string Table, string Code, string Name);

    private static readonly Dictionary<string, Def> ByCtl = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Customer"] = new("dmkh", "ma_kh", "ten_kh"), ["Supplier"] = new("dmkh", "ma_kh", "ten_kh"), ["Vendor"] = new("dmkh", "ma_kh", "ten_kh"),
        ["Item"] = new("dmvt", "ma_vt", "ten_vt"), ["Site"] = new("dmkho", "ma_kho", "ten_kho"), ["Account"] = new("dmtk", "tk", "ten_tk"),
        ["Currency"] = new("dmnt", "ma_nt", "ten_nt"), ["Tax"] = new("dmthue", "ma_thue", "ten_thue"), ["Unit"] = new("dmdvcs", "ma_dvcs", "ten_dvcs"),
        ["Job"] = new("dmvv", "ma_vv", "ten_vv"), ["Contract"] = new("dmhd", "ma_hd", "ten_hd"), ["Department"] = new("dmbp", "ma_bp", "ten_bp"),
        ["Employee"] = new("dmnv", "ma_nv", "ten_nv"), ["SalesMan"] = new("dmnv", "ma_nv", "ten_nv"), ["Lot"] = new("dmlo", "ma_lo", "ten_lo"),
        ["UOM"] = new("dmdvt", "dvt", "ten_dvt"), ["ItemUOM"] = new("dmdvt", "dvt", "ten_dvt"), ["Voucher"] = new("dmct", "ma_ct", "ten_ct"),
    };

    private static readonly (string Pattern, Def Def)[] ByField =
    {
        (@"^ma_kh", ByCtl["Customer"]), (@"^ma_vt", ByCtl["Item"]), (@"^ma_kho", ByCtl["Site"]), (@"^tk(_|$)", ByCtl["Account"]),
        (@"^ma_nt$", ByCtl["Currency"]), (@"^ma_thue", ByCtl["Tax"]), (@"^ma_dvcs", ByCtl["Unit"]), (@"^ma_vv", ByCtl["Job"]),
        (@"^ma_hd$", ByCtl["Contract"]), (@"^ma_bp", ByCtl["Department"]), (@"^ma_nv", ByCtl["Employee"]), (@"^ma_lo", ByCtl["Lot"]),
        (@"^dvt$", ByCtl["UOM"]), (@"^ma_ct$", ByCtl["Voucher"]),
        (@"^ma_gd$", new Def("dmmagd", "ma_gd", "ten_gd")), (@"^ma_tt$", new Def("dmtt", "ma_tt", "ten_tt")),
    };

    public static Def? Resolve(string? ctl, string field)
    {
        if (!string.IsNullOrEmpty(ctl) && ByCtl.TryGetValue(ctl, out var d)) return d;
        foreach (var (p, def) in ByField) if (Regex.IsMatch(field, p, RegexOptions.IgnoreCase)) return def;
        return null;
    }
}
