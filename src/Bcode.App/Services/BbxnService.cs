using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bcode.App.Services;

// =====================================================================================================================
//  Tạo biên bản xác nhận / nghiệm thu (Word) cho khách hàng của Fast: cài đặt hệ thống, báo cáo tài chính, tài liệu khảo sát, nghiệm thu chương trình...
//  - Thông tin khách hàng (Bên A) khai báo 1 lần, thông tin Fast (Bên B) có sẵn, sửa được.
//  - Mỗi mẫu là 1 file JSON (mẫu có sẵn nằm trong code, mẫu của người dùng ở %AppData%\Bcode\bbxn\templates): import / export được.
//  - Văn bản sinh ra theo 1 mô hình trung gian (BbxnBlock) dùng cho cả xem trước (HTML) lẫn xuất Word (.docx ghi trực tiếp, không cần cài Word).
// =====================================================================================================================

public sealed class BbxnParty
{
    public string Ten { get; set; } = "";
    public string TenNgan { get; set; } = "";       // cách gọi trong câu "giữa Công ty ... và ..." (vd "Công ty CP Phát Triển Quốc Tế Đông Nam Á")
    public string DiaChi { get; set; } = "";
    public string DienThoai { get; set; } = "";
    public string Fax { get; set; } = "";
    public string DaiDien { get; set; } = "";       // gồm cả xưng hô: "Ông Phan Văn Khải"
    public string ChucVu { get; set; } = "";
    public string Mst { get; set; } = "";
    public string DiaDiem { get; set; } = "TP. Hồ Chí Minh";   // nơi lập biên bản (chỉ dùng cho Bên B)
    // bản tiếng Anh (dùng cho mẫu song ngữ)
    public string TenEn { get; set; } = "";
    public string DiaChiEn { get; set; } = "";
    public string DaiDienEn { get; set; } = "";     // vd "Mr. Le Vu Ha"
    public string ChucVuEn { get; set; } = "";
    public string DiaDiemEn { get; set; } = "";     // nơi lập biên bản (Bên B), vd "Ho Chi Minh City"
}

/// <summary>Một pháp nhân của Fast để chọn làm Bên B / bên gửi (công ty có 2 pháp nhân).</summary>
public sealed class BbxnEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";          // tên hiện trong ô chọn
    public BbxnParty Party { get; set; } = new();
}

public sealed class BbxnContract
{
    public string Loai { get; set; } = "Hợp đồng cung cấp phần mềm kế toán";
    public string So { get; set; } = "";
    public string Ngay { get; set; } = "";          // yyyy-MM-dd
    public string LoaiEn { get; set; } = "Software Supply Contract";
}

public sealed class BbxnVar
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Default { get; set; } = "";
}

public sealed class BbxnTemplate
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Abbr { get; set; } = "";           // viết tắt dùng trong số biên bản: BBCD, BCTC, TLKS...
    public string Title { get; set; } = "";          // tiêu đề (nhiều dòng cách nhau \n)
    public string Basis { get; set; } = DefaultBasis; // các câu "Căn cứ ..." (mỗi dòng 1 đoạn)
    public string Intro { get; set; } = "Hai bên thống nhất xác nhận các nội dung sau:";
    public List<string> Items { get; set; } = new(); // các nội dung xác nhận (mỗi phần tử 1 đoạn, gạch đầu dòng)
    public string Closing { get; set; } = "Biên bản này có 01 (một) trang, được lập thành 02 (hai) bản, mỗi bên giữ 01 (một) bản có giá trị pháp lý như nhau.";
    public bool Table { get; set; }                  // có bảng hạng mục (STT | Nội dung | Xác nhận)
    public string TableIntro { get; set; } = "";     // câu dẫn trước bảng
    public List<BbxnVar> Vars { get; set; } = new();
    public bool Builtin { get; set; }
    // song ngữ: mỗi đoạn tiếng Việt kèm 1 đoạn tiếng Anh ngay sau (ItemsEn song song với Items; đoạn rỗng sau khi thay biến thì bỏ)
    public bool Bilingual { get; set; }
    public string TitleEn { get; set; } = "";
    public string BasisEn { get; set; } = "";
    public string IntroEn { get; set; } = "";
    public List<string> ItemsEn { get; set; } = new();
    public string ClosingEn { get; set; } = "";
    public string TableIntroEn { get; set; } = "";

    // ---- phân nhóm + kiểu văn bản (bộ biểu mẫu Fast: công văn, kế hoạch, phiếu…) ----
    /// <summary>Nhóm hiển thị ở danh sách chọn mẫu (rỗng = "Khác").</summary>
    public string Group { get; set; } = "";
    /// <summary>"" = biên bản (Bên A / Bên B) · "congvan" = công văn / thư (Kính gửi, V/v, Nơi nhận) · "phieu" = phiếu / giấy (các dòng "Nhãn|giá trị", bảng, nhiều ô ký).</summary>
    public string Layout { get; set; } = "";
    /// <summary>Tiêu đề các cột của bảng (rỗng = STT | NỘI DUNG | Xác nhận). Mỗi dòng nhập ở ô hạng mục dùng dấu | để ngăn cột.</summary>
    public List<string> Columns { get; set; } = new();
    /// <summary>Các ô ký cuối văn bản (rỗng = Đại diện bên A | Đại diện bên B; công văn mặc định chỉ có người ký của Fast).</summary>
    public List<string> SignRoles { get; set; } = new();
    /// <summary>Công văn: lời mở đầu (đoạn đầu sau V/v) và danh sách "Nơi nhận" (mỗi dòng 1 nơi).</summary>
    public List<string> DefaultRows { get; set; } = new();   // các dòng bảng điền sẵn khi chọn mẫu
    public string Greeting { get; set; } = "";
    public string Recipients { get; set; } = "";

    public const string DefaultBasis = "Căn cứ việc thực hiện {loai_hd} số {so_hd} ký ngày {ngay_hd} giữa {ten_a_ngan} và Chi nhánh Công ty CP phần mềm quản lý Doanh Nghiệp Fast tại TP.HCM.";
}

/// <summary>Một biên bản cần sinh: mẫu nào + số + giá trị các biến riêng của mẫu + các dòng hạng mục.</summary>
public sealed class BbxnDocRequest
{
    public string TemplateId { get; set; } = "";
    public string HauTo { get; set; } = "";          // hậu tố số biên bản, vd "38DV"
    public string SoOverride { get; set; } = "";     // nhập tay thì dùng thay cho số tự động
    public Dictionary<string, string> Vars { get; set; } = new();
    public List<string> Rows { get; set; } = new();  // hạng mục của bảng (mỗi dòng 1 hạng mục)
    public string NgayLap { get; set; } = "";        // yyyy-MM-dd; rỗng = dùng ngày chung
    public string Logo { get; set; } = "1";          // logo góc trái: "1" (FAST + PHẦN MỀM KẾ TOÁN & ERP), "2" (FAST gạch cong), "" = không logo
}

public sealed class BbxnState
{
    public BbxnParty Fast { get; set; } = new();
    public BbxnParty Customer { get; set; } = new();
    public BbxnContract Contract { get; set; } = new();
    public string MaDa { get; set; } = "";           // mã dự án (đứng đầu số biên bản)
    public string SoFormat { get; set; } = "{ma_da}/{abbr}/{yymm}{hau_to}";
    public string NgayLap { get; set; } = "";        // ngày lập chung; rỗng = để trống "ngày …. tháng …. năm ….."
    public List<BbxnDocRequest> Docs { get; set; } = new();
}

public sealed class BbxnBlock
{
    public string Kind { get; set; } = "p";          // p | center | right | party | table | sign | spacer | header
    public bool En { get; set; }                     // song ngữ: p/center = đoạn tiếng Anh (nghiêng, nhỏ hơn); right/sign = các dòng sau dòng đầu là tiếng Anh; header = có thêm dòng tiếng Anh
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public int Indent { get; set; }
    public List<string[]> Rows { get; set; } = new();
}

public static class BbxnService
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "bbxn");
    public static string TemplateFolder => Path.Combine(Root, "templates");
    private static string SettingsPath => Path.Combine(Root, "settings.json");

    // ------------------------------------------------------------------------------------------ cài đặt người dùng
    public sealed class Settings
    {
        public BbxnParty Fast { get; set; } = DefaultFast();
        /// <summary>Các pháp nhân của Fast (mặc định có 2); <see cref="Fast"/> là bản đang chọn.</summary>
        public List<BbxnEntity> Entities { get; set; } = new();
        public string EntityId { get; set; } = "";
        public List<BbxnParty> Customers { get; set; } = new();
        public BbxnParty Customer { get; set; } = new();
        public BbxnContract Contract { get; set; } = new();
        public string MaDa { get; set; } = "";
        public string SoFormat { get; set; } = "{ma_da}/{abbr}/{yymm}{hau_to}";
    }

    public static BbxnParty DefaultFast() => new()
    {
        Ten = "CHI NHÁNH CÔNG TY CỔ PHẦN PHẦN MỀM QLDN FAST TẠI TP.HCM",
        TenNgan = "Chi nhánh Công ty CP phần mềm quản lý Doanh Nghiệp Fast tại TP.HCM",
        DiaChi = "Số 29 Đường 18, Phường Hiệp Bình, TP. Hồ Chí Minh, Việt Nam",
        DienThoai = "(028) 7108-8788", Fax = "",
        DaiDien = "Bà Ninh Thị Tố Uyên", ChucVu = "Giám đốc", Mst = "0100727825-001", DiaDiem = "TP. Hồ Chí Minh",
        TenEn = "FAST SOFTWARE COMPANY – HCM CITY BRANCH", DiaChiEn = "No. 29, Street 18, Hiep Binh Ward, Ho Chi Minh City, Vietnam",
        DaiDienEn = "Ms. Ninh Thi To Uyen", ChucVuEn = "Director", DiaDiemEn = "Ho Chi Minh City",
    };

    /// <summary>Hai pháp nhân của Fast theo các biểu mẫu: Chi nhánh tại TP.HCM (đang dùng) và Công ty CP Phần mềm Quản lý Doanh nghiệp (trụ sở chính — địa chỉ / người đại diện cần điền).</summary>
    public static List<BbxnEntity> DefaultEntities()
    {
        var cn = DefaultFast();
        var ct = new BbxnParty
        {
            Ten = "CÔNG TY CỔ PHẦN PHẦN MỀM QUẢN LÝ DOANH NGHIỆP FAST",
            TenNgan = "Công ty CP Phần mềm Quản lý Doanh nghiệp Fast",
            DiaChi = "", DienThoai = "", Fax = "", DaiDien = "", ChucVu = "Tổng giám đốc", Mst = "0100727825", DiaDiem = "Hà Nội",
            TenEn = "FAST SOFTWARE COMPANY", DiaChiEn = "", DaiDienEn = "", ChucVuEn = "General Director", DiaDiemEn = "Ha Noi",
        };
        return new List<BbxnEntity>
        {
            new() { Id = "cn-hcm", Name = "Chi nhánh Công ty CP Phần mềm QLDN Fast tại TP.HCM", Party = cn },
            new() { Id = "ct-cp", Name = "Công ty CP Phần mềm Quản lý Doanh nghiệp Fast (trụ sở chính)", Party = ct },
        };
    }

    /// <summary>Cài đặt cũ chưa có danh sách pháp nhân → dựng 2 pháp nhân mặc định; bản Fast cũ (người dùng đã sửa) giữ cho pháp nhân đang chọn.</summary>
    private static void FillEntities(Settings s)
    {
        if (s.Entities.Count == 0) s.Entities = DefaultEntities();
        foreach (var e in s.Entities) FillFastEn(e.Party);
        if (string.IsNullOrWhiteSpace(s.EntityId) || s.Entities.All(e => e.Id != s.EntityId)) s.EntityId = s.Entities[0].Id;
        var cur = s.Entities.First(e => e.Id == s.EntityId);
        if (!string.IsNullOrWhiteSpace(s.Fast.Ten) && s.Entities.Count == 2 && cur.Id == "cn-hcm" && cur.Party.Ten == DefaultFast().Ten) cur.Party = s.Fast;   // cài đặt cũ: giữ chỉnh sửa của người dùng
        s.Fast = cur.Party;
    }

    /// <summary>Cài đặt cũ chưa có phần tiếng Anh của Bên B → điền mặc định.</summary>
    private static void FillFastEn(BbxnParty f)
    {
        var d = DefaultFast();
        if (string.IsNullOrWhiteSpace(f.TenEn)) f.TenEn = d.TenEn;
        if (string.IsNullOrWhiteSpace(f.DiaChiEn)) f.DiaChiEn = d.DiaChiEn;
        if (string.IsNullOrWhiteSpace(f.DaiDienEn)) f.DaiDienEn = d.DaiDienEn;
        if (string.IsNullOrWhiteSpace(f.ChucVuEn)) f.ChucVuEn = d.ChucVuEn;
        if (string.IsNullOrWhiteSpace(f.DiaDiemEn)) f.DiaDiemEn = d.DiaDiemEn;
    }

    public static Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath)) { var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), Json) ?? new Settings(); FillFastEn(s.Fast); FillEntities(s); return s; }
        }
        catch { /* file hỏng → dùng mặc định */ }
        var d = new Settings(); FillEntities(d); return d;
    }

    public static void SaveSettings(Settings s)
    {
        // bản Fast đang sửa trên màn hình thuộc về pháp nhân đang chọn → ghi vào danh sách pháp nhân
        var cur = s.Entities.FirstOrDefault(e => e.Id == s.EntityId);
        if (cur is not null) cur.Party = s.Fast;
        Directory.CreateDirectory(Root);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(s, Json), new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------------------------------ mẫu
    private static BbxnTemplate T(string id, string name, string abbr, string title, params string[] items) =>
        new() { Id = id, Name = name, Abbr = abbr, Title = title, Items = items.ToList(), Builtin = true };

    /// <summary>Mẫu có sẵn — lấy từ các biên bản mẫu của Fast (cài đặt, báo cáo tài chính, tài liệu khảo sát, nghiệm thu...). Bản người dùng sửa / import cùng Id sẽ thay bản này.</summary>
    public static List<BbxnTemplate> BuiltIns()
    {
        var list = new List<BbxnTemplate>
        {
            T("bbcd", "Xác nhận cài đặt hệ thống", "BBCD", "BIÊN BẢN XÁC NHẬN CÀI ĐẶT HỆ THỐNG",
                "Bên B đã cài đặt phần mềm kế toán Fast Business Online trên server của Bên A.",
                "Bên B đã hoàn thành việc tư vấn, đào tạo cho bên A sử dụng chương trình chuẩn đã cài đặt.",
                "Bên B tiếp tục hỗ trợ bên A trong quá trình nhập liệu, lên báo cáo {so_thang} tháng để làm cơ sở nghiệm thu chương trình."),
            T("bctc", "Xác nhận báo cáo tài chính", "BCTC", "BIÊN BẢN XÁC NHẬN BÁO CÁO TÀI CHÍNH",
                "Bên B đã thực hiện cài đặt phần mềm Fast Business Online lên máy chủ của Bên A. Bên A đã nhập liệu số liệu từ {ngay_bat_dau} vào chương trình.",
                "Bên B đã hỗ trợ Bên A nhập liệu và kiểm tra số liệu báo cáo {ky_bao_cao}. Chương trình đã lên đúng các báo cáo tài chính chuẩn dựa trên số liệu {ky_bao_cao}.",
                "Bên B tiếp tục hỗ trợ hướng dẫn Bên A nhập liệu, lên các báo cáo chỉnh sửa theo Tài Liệu Khảo Sát đặc thù dựa trên số liệu {ky_bao_cao} để làm căn cứ nghiệm thu hợp đồng."),
            T("tlks", "Xác nhận tài liệu khảo sát", "TLKS", "BIÊN BẢN XÁC NHẬN TÀI LIỆU KHẢO SÁT",
                "Bên B đã thực hiện khảo sát các nghiệp vụ, yêu cầu của bên A.",
                "Bên B đã hoàn thành tài liệu khảo sát nghiệp vụ cho bên A. Tài liệu này bao gồm {so_trang} trang được lập thành 02 (hai) bản, bên B giữ 01 (một) bản và gửi cho bên A 01 (một) bản.",
                "Bên A đã đọc kỹ tài liệu khảo sát do bên B lập và xác nhận nội dung trong tài liệu đã đầy đủ theo yêu cầu của bên A.",
                "Tài liệu này là căn cứ để bên B lập trình chỉnh sửa chương trình cho bên A. Mọi yêu cầu thay đổi cũng như thêm mới sẽ được hai bên thống nhất bằng văn bản."),
            T("tlksapi", "Xác nhận tài liệu khảo sát API", "TLKSAPI", "BIÊN BẢN XÁC NHẬN TÀI LIỆU KHẢO SÁT API {he_thong}",
                "Bên B đã thực hiện việc khảo sát các nghiệp vụ, yêu cầu kết nối API {he_thong} của bên A.",
                "Bên B đã hoàn thành tài liệu khảo sát nghiệp vụ cho bên A. Tài liệu này gồm {so_trang} trang được lập thành 02 (hai) bản, bên B giữ 01 (một) bản và gửi cho bên A 01 (một) bản.",
                "Bên A đã đọc kỹ tài liệu khảo sát do bên B lập và xác nhận nội dung trong tài liệu đã đầy đủ theo yêu cầu của bên A.",
                "Tài liệu này là căn cứ để bên B lập trình chỉnh sửa chương trình kết nối API FAST và {he_thong} cho bên A. Mọi yêu cầu thay đổi cũng như thêm mới sẽ được hai bên thống nhất bằng văn bản."),
            T("ntct", "Nghiệm thu chương trình", "NTCT", "BIÊN BẢN XÁC NHẬN NGHIỆM THU CHƯƠNG TRÌNH",
                "Bên B hoàn thành việc cài đặt chương trình Fast Business Online{noi_dung_them} cho bên A theo đúng nội dung yêu cầu trong hợp đồng 2 bên đã ký.",
                "Bên B đã hỗ trợ bên A trong quá trình nhập liệu, đào tạo sử dụng phần mềm.",
                "Chương trình đã chạy đúng theo yêu cầu của bên A.",
                "Hai bên thống nhất nghiệm thu chương trình theo {loai_hd} số {so_hd} ký ngày {ngay_hd} và chuyển {loai_hd_ngan} qua giai đoạn bảo hành. Bên B chịu trách nhiệm bảo hành cho bên A theo đúng điều khoản bảo hành của {loai_hd_ngan}."),
            T("ntplhd", "Nghiệm thu phụ lục hợp đồng", "BBNTPLHĐ", "BIÊN BẢN NGHIỆM THU PHỤ LỤC HỢP ĐỒNG {noi_dung_pl}",
                "Bên B đã hoàn thành việc {noi_dung_hoan_thanh} cho bên A theo phụ lục hợp đồng.",
                "Bên B đã hỗ trợ bên A test và chuyển dữ liệu lên bản chính thức thành công.",
                "Hai bên xác nhận nghiệm thu phụ lục hợp đồng {noi_dung_pl} số {so_hd} ký ngày {ngay_hd} và chuyển sang giai đoạn bảo hành. Bên B chịu trách nhiệm bảo hành cho bên A theo đúng điều khoản bảo hành của phụ lục hợp đồng."),
            T("xndv", "Xác nhận chương trình sẵn sàng nhập liệu", "XNDV", "BIÊN BẢN XÁC NHẬN CHƯƠNG TRÌNH ĐÃ SẴN SÀNG\nNHẬP LIỆU {phan_he}",
                "Bên B hoàn thành việc chỉnh sửa chương trình phần nhập liệu {phan_he} cho bên A theo đúng Tài liệu khảo sát 2 bên đã ký.",
                "Bên B đã hoàn thành việc đào tạo hướng dẫn cho bên A các phần chỉnh sửa trên. Bên A đã kiểm tra kỹ các phần chỉnh sửa này và xác nhận phần chỉnh sửa đã đúng theo yêu cầu trong Tài liệu khảo sát.",
                "Hai bên cùng xác nhận chương trình đã sẵn sàng cho việc nhập liệu của bên A. Bên A sẽ tiến hành nhập số liệu vào chương trình từ ngày {ngay_bat_dau}.",
                "Bên B tiếp tục hỗ trợ bên A trong quá trình nhập liệu để lên được các báo cáo tài chính cũng như báo cáo đặc thù theo tài liệu khảo sát cho bên A."),
            T("bbnt", "Nghiệm thu chương trình theo bảng kê hạng mục", "BBNT", "BIÊN BẢN NGHIỆM THU CHƯƠNG TRÌNH\n{phan_he}",
                "Bên B đã hoàn thành việc đào tạo hướng dẫn cho bên A các phần chỉnh sửa trên. Bên A đã kiểm tra kỹ các phần chỉnh sửa này và xác nhận phần chỉnh sửa đã đúng theo yêu cầu trong {loai_hd}.",
                "Hai bên thống nhất nghiệm thu chương trình theo {loai_hd} số {so_hd} ký ngày {ngay_hd} và chuyển {loai_hd_ngan} qua giai đoạn bảo hành. Bên B chịu trách nhiệm bảo hành cho bên A theo đúng điều khoản bảo hành của {loai_hd_ngan}."),
        };
        void V(string id, params (string k, string l, string d)[] vars) =>
            list.First(t => t.Id == id).Vars = vars.Select(v => new BbxnVar { Key = v.k, Label = v.l, Default = v.d }).ToList();
        V("bbcd", ("so_thang", "Số tháng lên báo cáo", "3"));
        V("bctc", ("ngay_bat_dau", "Nhập liệu từ ngày", "…../…../….."), ("ky_bao_cao", "Kỳ báo cáo", "quý 1, quý 2 năm 2026"));
        V("tlks", ("so_trang", "Số trang tài liệu", "…"));
        V("tlksapi", ("he_thong", "Hệ thống kết nối", "CRM"), ("so_trang", "Số trang tài liệu", "…"));
        V("ntct", ("noi_dung_them", "Nội dung thêm sau 'cài đặt chương trình Fast Business Online'", " và kết nối Fast eInvoice"));
        V("ntplhd", ("noi_dung_pl", "Nội dung phụ lục", "CHỈNH SỬA API"), ("noi_dung_hoan_thanh", "Nội dung đã hoàn thành", "cài đặt và chỉnh sửa API"));
        V("xndv", ("phan_he", "Phân hệ / nội dung", "ĐẦU VÀO"), ("ngay_bat_dau", "Nhập liệu từ ngày", "…../…../….."));
        V("bbnt", ("phan_he", "Phân hệ / nội dung", "PHÂN HỆ ..."));
        list.First(t => t.Id == "bbnt").Table = true;
        list.First(t => t.Id == "bbnt").TableIntro = "Bên B hoàn thành việc chỉnh sửa chương trình cho bên A theo đúng {loai_hd} hai bên đã ký. Nội dung xác nhận trong biên bản này là cho các hạng mục theo bảng kê dưới đây:";
        list.First(t => t.Id == "bbnt").Closing = "Biên bản này có 02 (hai) trang, được lập thành 02 (hai) bản, mỗi bên giữ 01 (một) bản có giá trị pháp lý như nhau.";
        list.AddRange(BilingualTemplates());
        list.AddRange(BbxnForms.All());
        foreach (var t in list) if (string.IsNullOrEmpty(t.Group)) t.Group = BbxnForms.BuiltInGroups.TryGetValue(t.Id, out var g) ? g : "";
        return list;
    }

    private static BbxnTemplate Bi(string id, string name, string abbr, string title, string titleEn, string intro, string introEn, string closing, string closingEn, (string vi, string en)[] items, params (string k, string l, string d)[] vars) => new()
    {
        Id = id, Name = name, Abbr = abbr, Title = title, TitleEn = titleEn, Bilingual = true, Builtin = true,
        Basis = BiBasis, BasisEn = BiBasisEn, Intro = intro, IntroEn = introEn, Closing = closing, ClosingEn = closingEn,
        Items = items.Select(i => i.vi).ToList(), ItemsEn = items.Select(i => i.en).ToList(),
        Vars = vars.Select(v => new BbxnVar { Key = v.k, Label = v.l, Default = v.d }).ToList(),
    };
    private const string BiBasis = "Căn cứ việc thực hiện {loai_hd} số {so_hd} ký ngày {ngay_hd} giữa {ten_a_ngan} và Chi nhánh Công ty CP phần mềm quản lý Doanh Nghiệp Fast tại TP.HCM.";
    private const string BiBasisEn = "According to the implementation of {loai_hd_en} No.: {so_hd} signed on {ngay_hd_en} between {ten_a_en} and {ten_b_en}.";
    private const string BiIntro = "Hai bên thống nhất các nội dung như sau:";
    private const string BiIntroEn = "The two parties agree with the following contents:";
    private const string BiClosing2 = "Biên bản này có 02 (hai) trang, được lập thành 02 (hai) bản, mỗi bên giữ 01 (một) bản có giá trị pháp lý như nhau.";
    private const string BiClosing2En = "This minutes has 02 (two) pages, is made into 02 (two) copies, each party keeps 01 (one) copy with the same legal value.";

    /// <summary>Mẫu song ngữ Việt – Anh (theo bộ biên bản TSURUMI): tài liệu khảo sát, khảo sát API, nghiệm thu, sẵn sàng nhập liệu.</summary>
    private static List<BbxnTemplate> BilingualTemplates() => new()
    {
        Bi("tlks-vn-en", "Xác nhận tài liệu khảo sát (song ngữ)", "XNTLKS", "BIÊN BẢN XÁC NHẬN TÀI LIỆU KHẢO SÁT\nPHÂN HỆ: {phan_he}", "REQUIREMENT DOCUMENT ACCEPTANCE\nMODULE: {phan_he_en}", BiIntro, BiIntroEn, BiClosing2, BiClosing2En,
            new[]
            {
                ("Bên B đã thực hiện việc đào tạo chương trình chuẩn và khảo sát quy trình hoạt động và các yêu cầu nghiệp vụ của {pham_vi} của bên A trong khoảng thời gian từ ngày {ngay_tu} đến ngày {ngay_den}.",
                 "Party B has conducted the training of the standard program and surveyed the operation process and business requirements of {pham_vi_en} of Party A during the period from {ngay_tu} to {ngay_den}."),
                ("Bên B đã hoàn thành tài liệu khảo sát quy trình hoạt động và các yêu cầu nghiệp vụ của {pham_vi} tại văn phòng bên A. Tài liệu này gồm {so_trang} trang mô tả quy trình hoạt động và các yêu cầu nghiệp vụ, được lập thành 02 (hai) bản bên B giữ 01 (một) bản và gửi cho bên A 01 (một) bản từ ngày {ngay_den}.",
                 "Party B has completed the survey document on the operation process and business requirements of {pham_vi_en} at Party A's office. This document consists of {so_trang} pages describing the operation process and business requirements, made into 02 (two) copies, Party B keeps 01 (one) copy and sends 01 (one) copy to Party A from {ngay_den}."),
                ("Bên A đã đọc kỹ tài liệu khảo sát do bên B lập và xác nhận nội dung trong tài liệu đã đầy đủ theo yêu cầu của bên A.",
                 "Party A has read the survey document which were written by Party B carefully and confirmed that the contents in the document fully covered all the business requirements of Party A and solutions for each specific requirement."),
                ("Tài liệu này là căn cứ để bên B lập trình chỉnh sửa chương trình cho bên A. Mọi yêu cầu thay đổi cũng như thêm mới sẽ được hai bên thống nhất bằng văn bản.",
                 "The developer team of Party B will design and customize the program based on this survey document for Party A. Both parties agree that any modifying or additional requests which were not mentioned in this survey document will be verified by further official minutes."),
            },
            ("phan_he", "Phân hệ (Việt)", "TÀI CHÍNH KẾ TOÁN"), ("phan_he_en", "Phân hệ (Anh)", "ACCOUNTING FINANCIAL"),
            ("pham_vi", "Phạm vi khảo sát (Việt)", "các phân hệ quản lý hệ thống, quản lý tài chính kế toán, quản lý giá thành sản phẩm, quản lý bán hàng"),
            ("pham_vi_en", "Phạm vi khảo sát (Anh)", "the system management modules, financial and accounting management, product cost management, sales management"),
            ("ngay_tu", "Khảo sát từ ngày", "…/…/….."), ("ngay_den", "Đến ngày / ngày giao tài liệu", "…/…/….."), ("so_trang", "Số trang tài liệu", "…")),

        Bi("tlksapi-vn-en", "Xác nhận tài liệu khảo sát API (song ngữ)", "XNTLKSAPI", "BIÊN BẢN XÁC NHẬN TÀI LIỆU KHẢO SÁT\nPHÂN HỆ: KẾT NỐI API", "REQUIREMENT DOCUMENT ACCEPTANCE\nMODULE: API CONNECTION", BiIntro, BiIntroEn, BiClosing2, BiClosing2En.Replace("01 (one) copy with", "01 (one) copy with"),
            new[]
            {
                ("Bên B đã thực hiện việc khảo sát quy trình hoạt động và các yêu cầu nghiệp vụ của phân hệ kết nối API giữa phần mềm Fast Business Online và {he_thong} của bên A đang sử dụng trong khoảng thời gian từ ngày {ngay_tu} đến ngày {ngay_den}.",
                 "Party B has carried out a survey of the operation process and business requirements of the API connection module between the Fast Business Online software and {he_thong_en} of Party A in use during the period from {ngay_tu} to {ngay_den}."),
                ("Bên B đã hoàn thành tài liệu khảo sát quy trình hoạt động và các yêu cầu nghiệp vụ của phân hệ kết nối API giữa phần mềm Fast Business Online và {he_thong} tại văn phòng bên A. Tài liệu này gồm {so_trang} trang mô tả quy trình hoạt động và các yêu cầu nghiệp vụ, được lập thành 02 (hai) bản bên B giữ 01 (một) bản và gửi cho bên A 01 (một) bản từ ngày {ngay_den}.",
                 "Party B has completed the survey document on the operation process and business requirements of the API connection module between Fast Business Online software and {he_thong_en} at Party A's office. This document consists of {so_trang} pages describing the operation process and business requirements, made into 02 (two) copies, Party B keeps 01 (one) copy and sends 01 (one) copy to Party A from {ngay_den}."),
                ("Bên A đã đọc kỹ tài liệu khảo sát do bên B lập và xác nhận nội dung trong tài liệu đã đầy đủ theo yêu cầu của bên A.",
                 "Party A has read the survey document which were written by Party B carefully and confirmed that the contents in the document fully covered all the business requirements of Party A and solutions for each specific requirement."),
                ("Tài liệu này là căn cứ để bên B lập trình chỉnh sửa chương trình cho bên A. Mọi yêu cầu thay đổi cũng như thêm mới sẽ được hai bên thống nhất bằng văn bản.",
                 "The developer team of Party B will design and customize the program based on this survey document for Party A. Both parties agree that any modifying or additional requests which were not mentioned in this survey document will be verified by further official minutes."),
            },
            ("he_thong", "Phần mềm kết nối (Việt)", "phần mềm Quản lý sản xuất TPICS"), ("he_thong_en", "Phần mềm kết nối (Anh)", "the TPICS production management software"),
            ("ngay_tu", "Khảo sát từ ngày", "…/…/….."), ("ngay_den", "Đến ngày / ngày giao tài liệu", "…/…/….."), ("so_trang", "Số trang tài liệu", "…")),

        Bi("nt-vn-en", "Nghiệm thu chương trình (song ngữ)", "BBNT", "BIÊN BẢN NGHIỆM THU CHƯƠNG TRÌNH\nPHÂN HỆ: {phan_he}", "FINAL ACCEPTANCE CERTIFICATE\nMODULE: {phan_he_en}", BiIntro, BiIntroEn, BiClosing2, BiClosing2En,
            new[]
            {
                ("Bên B đã hoàn thành việc cài đặt chương trình Fast Business Online trên {so_may_chu} máy chủ, {so_user} người dùng truy cập đồng thời trên máy chủ của bên A theo đúng hợp đồng.",
                 "Party B has completed the installation of the Fast Business Online program on {so_may_chu} server, {so_user} users can access simultaneously on Party A's server according to the contract."),
                ("Bên B đã hoàn thành việc chỉnh sửa chương trình cho bên A theo đúng tài liệu khảo sát.",
                 "Party B has completed editing the program for Party A according to the survey documents."),
                ("Bên B đã hỗ trợ bên A trong quá trình nhập liệu để lên được báo cáo theo tài liệu khảo sát hai bên đã ký dựa trên số liệu tháng {thang_so_lieu}.",
                 "Party B has supported Party A in the data entry process to prepare a report according to the survey document signed by both parties based on {thang_so_lieu_en} data."),
                ("Đã qua sử dụng {so_ngay_sd} ngày kể từ ngày bên A ký xác nhận chương trình sẵn sàng nhập liệu đầu vào.",
                 "Used for {so_ngay_sd} days from the date Party A signed to confirm that the program is ready to input data."),
                ("Hai bên thống nhất nghiệm thu chương trình - phân hệ {phan_he_thuong} theo {loai_hd} số {so_hd} ký ngày {ngay_hd} và chuyển tiếp hợp đồng qua giai đoạn bảo hành. Bên B chịu trách nhiệm bảo hành cho bên A theo đúng điều khoản hợp đồng.",
                 "The two parties agree to accept the program according to contract No. {so_hd} signed on {ngay_hd_en} and move the contract through the warranty period. Party B is responsible for providing warranty to Party A in accordance with the terms of the contract."),
                ("{ngoai_le}", "{ngoai_le_en}"),
            },
            ("phan_he", "Phân hệ (Việt)", "TÀI CHÍNH KẾ TOÁN"), ("phan_he_en", "Phân hệ (Anh)", "ACCOUNTING FINANCIAL"), ("phan_he_thuong", "Phân hệ trong câu nghiệm thu", "tài chính kế toán"),
            ("so_may_chu", "Số máy chủ", "01"), ("so_user", "Số người dùng đồng thời", "25"),
            ("thang_so_lieu", "Số liệu tháng (Việt)", "…/….."), ("thang_so_lieu_en", "Số liệu tháng (Anh)", "…… ……"), ("so_ngay_sd", "Số ngày đã sử dụng", "60"),
            ("ngoai_le", "Nội dung loại trừ (Việt, bỏ trống nếu không có)", ""), ("ngoai_le_en", "Nội dung loại trừ (Anh)", "")),

        Bi("sn-vn-en", "Xác nhận chương trình sẵn sàng nhập liệu (song ngữ)", "XNSSNL", "BIÊN BẢN XÁC NHẬN\nCHƯƠNG TRÌNH SẴN SÀNG NHẬP LIỆU ĐẦU VÀO\nPHÂN HỆ: {phan_he}", "THE CONFIRMATION OF THE COMPLETED INPUT SYSTEM\nMODULE: {phan_he_en}", BiIntro, BiIntroEn, BiClosing2, BiClosing2En,
            new[]
            {
                ("Bên B đã hoàn thành việc chỉnh sửa phần nhập liệu đầu vào trên chương trình cho bên A theo đúng tài liệu khảo sát 2 bên đã ký.",
                 "Party B has completed editing the input on the program for Party A in accordance with the survey documents signed by both parties."),
                ("Bên B đã hoàn thành việc hướng dẫn cho bên A các phần chỉnh sửa trên. Nhân sự bên A đã kiểm tra kỹ các phần chỉnh sửa này và xác nhận phần chỉnh sửa đầu vào đã đúng theo yêu cầu trong tài liệu khảo sát.",
                 "Party B has completed guiding Party A about the above edited contents. Party A has carefully checked these edits and confirmed that the input edits are correct as required in the survey document."),
                ("Hai bên cùng xác nhận chương trình đã sẵn sàng cho việc nhập liệu chính thức của Bên A.",
                 "Both parties confirm that the program is ready for Party A's official data entry."),
                ("Bên B tiếp tục hỗ trợ Bên A trong quá trình nhập liệu để lên được các báo cáo đặc thù theo tài liệu khảo sát cho bên A dựa trên số liệu tháng {thang_so_lieu}.",
                 "Party B continues to support Party A in the data entry process to produce specific reports according to the survey documents for Party A based on the data of {thang_so_lieu_en}."),
            },
            ("phan_he", "Phân hệ (Việt)", "TÀI CHÍNH KẾ TOÁN"), ("phan_he_en", "Phân hệ (Anh)", "ACCOUNTING FINANCIAL"),
            ("thang_so_lieu", "Số liệu tháng (Việt)", "…/….."), ("thang_so_lieu_en", "Số liệu tháng (Anh)", "…… ……")),
    };

    /// <summary>Mẫu có sẵn + mẫu của người dùng (cùng Id thì bản người dùng thay).</summary>
    public static List<BbxnTemplate> LoadTemplates()
    {
        var map = new Dictionary<string, BbxnTemplate>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in BuiltIns()) map[t.Id] = t;
        try
        {
            if (Directory.Exists(TemplateFolder))
                foreach (var f in Directory.EnumerateFiles(TemplateFolder, "*.json"))
                    try
                    {
                        var t = JsonSerializer.Deserialize<BbxnTemplate>(File.ReadAllText(f), Json);
                        if (t is null || string.IsNullOrWhiteSpace(t.Id)) continue;
                        t.Builtin = false;
                        map[t.Id] = t;
                    }
                    catch { /* 1 file mẫu hỏng không được làm mất cả danh sách */ }
        }
        catch { /* thư mục không đọc được */ }
        var order = map.Values.Select((t, i) => (t, i)).ToDictionary(x => x.t.Id, x => x.i, StringComparer.OrdinalIgnoreCase);
        return map.Values.OrderBy(t => string.IsNullOrEmpty(t.Group) ? 1 : 0).ThenBy(t => t.Group, StringComparer.Ordinal).ThenBy(t => order[t.Id]).ToList();
    }

    private static string SafeId(string s) => Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');

    public static void SaveTemplate(BbxnTemplate t)
    {
        if (string.IsNullOrWhiteSpace(t.Id)) t.Id = SafeId(string.IsNullOrWhiteSpace(t.Abbr) ? t.Name : t.Abbr);
        if (t.Id.Length == 0) t.Id = "mau-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        Directory.CreateDirectory(TemplateFolder);
        File.WriteAllText(Path.Combine(TemplateFolder, SafeId(t.Id) + ".json"), JsonSerializer.Serialize(t, Json), new UTF8Encoding(false));
    }

    /// <summary>Xoá bản của người dùng (mẫu có sẵn bị sửa sẽ quay về bản gốc).</summary>
    public static void DeleteTemplate(string id)
    {
        var p = Path.Combine(TemplateFolder, SafeId(id) + ".json");
        if (File.Exists(p)) File.Delete(p);
    }

    public static void ExportTemplates(string path, IEnumerable<BbxnTemplate> templates) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new { bbxnTemplates = templates }, Json), new UTF8Encoding(true));

    /// <summary>Import: file .json (1 mẫu hoặc gói nhiều mẫu do Export tạo) hoặc .docx (biên bản Word có sẵn → tách tiêu đề / nội dung xác nhận / câu kết).</summary>
    public static List<BbxnTemplate> ImportFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var result = new List<BbxnTemplate>();
        if (ext == ".json")
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("bbxnTemplates", out var arr))
                foreach (var e in arr.EnumerateArray()) { var t = e.Deserialize<BbxnTemplate>(Json); if (t != null) result.Add(t); }
            else if (root.ValueKind == JsonValueKind.Array)
                foreach (var e in root.EnumerateArray()) { var t = e.Deserialize<BbxnTemplate>(Json); if (t != null) result.Add(t); }
            else { var t = root.Deserialize<BbxnTemplate>(Json); if (t != null) result.Add(t); }
        }
        else if (ext == ".docx") result.Add(FromDocx(path));
        else throw new InvalidDataException("Chỉ import được file .json (mẫu) hoặc .docx (biên bản Word). File .doc: mở bằng Word, Lưu thành .docx rồi import.");

        foreach (var t in result)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) t.Name = t.Title.Split('\n')[0];
            if (string.IsNullOrWhiteSpace(t.Id)) t.Id = SafeId(string.IsNullOrWhiteSpace(t.Abbr) ? t.Name : t.Abbr);
        }
        return result;
    }

    private static BbxnTemplate FromDocx(string path)
    {
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("File .docx không hợp lệ (thiếu word/document.xml).");
        XDocument xd;
        using (var s = entry.Open()) xd = XDocument.Load(s);
        var body = xd.Descendants(w + "body").First();

        // chỉ lấy đoạn nằm ngoài bảng (bảng là thông tin Bên A / Bên B / chữ ký — tool tự dựng)
        var paras = body.Elements(w + "p").Select(p => Regex.Replace(string.Concat(p.Descendants(w + "t").Select(t => t.Value)), @"\s+", " ").Trim()).Where(x => x.Length > 0).ToList();

        var t = new BbxnTemplate();
        var ti = paras.FindIndex(x => x.StartsWith("BIÊN BẢN", StringComparison.OrdinalIgnoreCase));
        if (ti >= 0)
        {
            var title = new List<string> { paras[ti] };
            for (var i = ti + 1; i < paras.Count && !paras[i].StartsWith("Số", StringComparison.OrdinalIgnoreCase) && title.Count < 3 && paras[i] == paras[i].ToUpperInvariant(); i++) title.Add(paras[i]);
            t.Title = string.Join("\n", title);
        }
        var basis = paras.Where(x => x.StartsWith("Căn cứ", StringComparison.OrdinalIgnoreCase)).ToList();
        t.Basis = basis.Count > 0 ? string.Join("\n", basis) : BbxnTemplate.DefaultBasis;
        var si = paras.FindIndex(x => x.StartsWith("Hai bên thống nhất", StringComparison.OrdinalIgnoreCase) || x.StartsWith("Hai bên cùng", StringComparison.OrdinalIgnoreCase));
        var ci = paras.FindIndex(x => x.StartsWith("Biên bản này có", StringComparison.OrdinalIgnoreCase));
        if (si >= 0)
        {
            t.Intro = paras[si];
            var end = ci > si ? ci : paras.Count;
            t.Items = paras.Skip(si + 1).Take(end - si - 1).Where(x => !x.StartsWith("Đại diện", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (ci >= 0) t.Closing = paras[ci];
        t.Name = (t.Title.Split('\n')[0]).Trim();
        t.Abbr = "";
        return t;
    }

    // ------------------------------------------------------------------------------------------ nội dung
    private static readonly string[] Months = { "", "01", "02", "03", "04", "05", "06", "07", "08", "09", "10", "11", "12" };

    private static bool ParseDate(string s, out DateTime d) => DateTime.TryParseExact(s, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out d);

    /// <summary>"24 tháng 06 năm 2026"; ngày không hợp lệ / để trống thì "…. tháng …. năm …..".</summary>
    public static string DateText(string yyyyMmDd) =>
        ParseDate(yyyyMmDd, out var d) ? $"{d.Day:00} tháng {Months[d.Month]} năm {d.Year}" : "…. tháng …. năm …..";

    private static readonly string[] MonthsEn = { "", "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December" };

    /// <summary>"July 18, 2023"; ngày không hợp lệ thì "……………".</summary>
    public static string DateTextEn(string yyyyMmDd) =>
        ParseDate(yyyyMmDd, out var d) ? $"{MonthsEn[d.Month]} {d.Day}, {d.Year}" : "……………";

    public static string DocNumber(BbxnState st, BbxnDocRequest req, BbxnTemplate tpl)
    {
        if (!string.IsNullOrWhiteSpace(req.SoOverride)) return req.SoOverride.Trim();
        var when = ParseDate(string.IsNullOrEmpty(req.NgayLap) ? st.NgayLap : req.NgayLap, out var d) ? d : DateTime.Now;
        var hau = string.IsNullOrWhiteSpace(req.HauTo) ? "" : " – " + req.HauTo.Trim();
        var fmt = string.IsNullOrWhiteSpace(st.SoFormat) ? "{ma_da}/{abbr}/{yymm}{hau_to}" : st.SoFormat;
        return fmt.Replace("{ma_da}", st.MaDa.Trim()).Replace("{abbr}", tpl.Abbr).Replace("{yymm}", when.ToString("yyMM"))
                  .Replace("{yyyy}", when.ToString("yyyy")).Replace("{hau_to}", hau);
    }

    private static string Subst(string text, Dictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return Regex.Replace(text, @"\{([A-Za-z0-9_]+)\}", m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private static string LoaiNgan(string loai) =>
        loai.StartsWith("Phụ lục", StringComparison.OrdinalIgnoreCase) ? "phụ lục hợp đồng" : "hợp đồng";

    public static List<BbxnBlock> BuildBlocks(BbxnState st, BbxnDocRequest req, BbxnTemplate tpl)
    {
        var c = st.Customer; var f = st.Fast; var k = st.Contract;
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ten_a"] = c.Ten, ["ten_a_ngan"] = string.IsNullOrWhiteSpace(c.TenNgan) ? c.Ten : c.TenNgan,
            ["so_hd"] = k.So, ["ngay_hd"] = DateText(k.Ngay), ["loai_hd"] = k.Loai, ["loai_hd_ngan"] = LoaiNgan(k.Loai), ["ma_da"] = st.MaDa,
            ["ten_a_en"] = string.IsNullOrWhiteSpace(c.TenEn) ? c.Ten : c.TenEn, ["ten_b_en"] = string.IsNullOrWhiteSpace(f.TenEn) ? f.Ten : f.TenEn,
            ["ten_b"] = f.Ten, ["ten_b_ngan"] = string.IsNullOrWhiteSpace(f.TenNgan) ? f.Ten : f.TenNgan,
            ["dia_chi_a"] = c.DiaChi, ["dien_thoai_a"] = c.DienThoai + (string.IsNullOrWhiteSpace(c.Fax) ? "" : "  Fax: " + c.Fax), ["dai_dien_a"] = c.DaiDien, ["chuc_vu_a"] = c.ChucVu, ["mst_a"] = c.Mst,
            ["ngay_hd_en"] = DateTextEn(k.Ngay), ["loai_hd_en"] = string.IsNullOrWhiteSpace(k.LoaiEn) ? "Contract" : k.LoaiEn,
        };
        if (tpl.Bilingual) return BuildBilingual(st, req, tpl, vars);
        foreach (var v in tpl.Vars) vars[v.Key] = req.Vars.TryGetValue(v.Key, out var val) && val.Length > 0 ? val : v.Default;
        foreach (var kv in req.Vars) vars.TryAdd(kv.Key, kv.Value);

        if (tpl.Layout.StartsWith("congvan", StringComparison.OrdinalIgnoreCase)) return BuildLetter(st, req, tpl, vars);
        if (tpl.Layout.Equals("phieu", StringComparison.OrdinalIgnoreCase)) return BuildForm(st, req, tpl, vars);

        var date = string.IsNullOrEmpty(req.NgayLap) ? st.NgayLap : req.NgayLap;
        var blocks = new List<BbxnBlock>
        {
            new() { Kind = "header", Text = req.Logo },   // logo bên trái + "Cộng hòa xã hội chủ nghĩa Việt Nam / Độc lập – Tự do – Hạnh phúc" bên phải
            new() { Kind = "right", Text = $"{(string.IsNullOrWhiteSpace(f.DiaDiem) ? "TP. Hồ Chí Minh" : f.DiaDiem)}, ngày " + (ParseDate(date, out var dd) ? $"{dd.Day:00} tháng {Months[dd.Month]} năm {dd.Year}" : "…. tháng …. năm ….."), Italic = true },
        };
        foreach (var line in Subst(tpl.Title, vars).Split('\n', StringSplitOptions.RemoveEmptyEntries)) blocks.Add(new BbxnBlock { Kind = "center", Text = line.Trim(), Bold = true });
        blocks.Add(new BbxnBlock { Kind = "right", Text = "Số: " + DocNumber(st, req, tpl), Bold = true });
        foreach (var line in Subst(tpl.Basis, vars).Split('\n', StringSplitOptions.RemoveEmptyEntries)) blocks.Add(new BbxnBlock { Kind = "p", Text = line.Trim() });
        blocks.Add(new BbxnBlock { Kind = "p", Text = "Chúng tôi gồm:" });

        void Party(string label, BbxnParty p, bool includeFax)
        {
            blocks.Add(new BbxnBlock { Kind = "p", Text = $"**{label}: {p.Ten.ToUpperInvariant()}**" });
            var rows = new List<string[]> { new[] { "Địa chỉ", p.DiaChi } };
            if (!string.IsNullOrWhiteSpace(p.DienThoai) || !string.IsNullOrWhiteSpace(p.Fax))
                rows.Add(new[] { "Điện thoại", p.DienThoai + (includeFax && !string.IsNullOrWhiteSpace(p.Fax) ? "      Fax: " + p.Fax : "") });
            rows.Add(new[] { "Đại diện", p.DaiDien });
            rows.Add(new[] { "Chức vụ", p.ChucVu });
            if (!string.IsNullOrWhiteSpace(p.Mst)) rows.Add(new[] { "Mã số thuế", p.Mst });
            blocks.Add(new BbxnBlock { Kind = "party", Rows = rows });
        }
        Party("Bên A", c, includeFax: true);
        Party("Bên B", f, includeFax: true);

        if (tpl.Table && !string.IsNullOrWhiteSpace(tpl.TableIntro))
            blocks.Add(new BbxnBlock { Kind = "p", Text = Subst(tpl.Intro, vars) });
        else
            blocks.Add(new BbxnBlock { Kind = "p", Text = Subst(tpl.Intro, vars) });

        if (tpl.Table)
        {
            if (!string.IsNullOrWhiteSpace(tpl.TableIntro)) blocks.Add(new BbxnBlock { Kind = "p", Text = Subst(tpl.TableIntro, vars) });
            blocks.Add(new BbxnBlock { Kind = "table", Rows = TableRows(tpl, req, vars) });
        }
        foreach (var item in tpl.Items) blocks.Add(new BbxnBlock { Kind = "p", Text = "- " + Subst(item, vars), Indent = 360 });
        if (!string.IsNullOrWhiteSpace(tpl.Closing)) blocks.Add(new BbxnBlock { Kind = "p", Text = Subst(tpl.Closing, vars) });
        blocks.Add(new BbxnBlock { Kind = "sign", Rows = { tpl.SignRoles.Count > 0 ? tpl.SignRoles.Select(x => Subst(x, vars)).ToArray() : new[] { "Đại diện bên A", "Đại diện bên B" } } });
        return blocks;
    }

    /// <summary>Hàng của bảng hạng mục: tiêu đề cột theo mẫu (mặc định STT | NỘI DUNG | Xác nhận); mỗi dòng nhập dùng | ngăn cột; cột đầu là STT / No thì tự đánh số khi dòng thiếu 1 ô.</summary>
    private static List<string[]> TableRows(BbxnTemplate tpl, BbxnDocRequest req, Dictionary<string, string> vars)
    {
        var cols = tpl.Columns.Count > 0 ? tpl.Columns.Select(c => Subst(c, vars)).ToArray() : new[] { "STT", "NỘI DUNG", "Xác nhận" };
        var n = cols.Length;
        var auto = Regex.IsMatch(cols[0].Trim(), @"^(stt|no\.?|#)$", RegexOptions.IgnoreCase);
        var rows = new List<string[]> { cols };
        var i = 0;
        foreach (var raw in req.Rows.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var cells = (tpl.Columns.Count > 0 ? raw.Split('|') : new[] { raw }).Select(c => Subst(c.Trim(), vars)).ToList();
            if (tpl.Columns.Count == 0) { cells.Insert(0, (++i).ToString()); cells.Add(""); }
            else if (auto && cells.Count == n - 1) cells.Insert(0, (++i).ToString());
            else if (auto && cells.Count == n && string.IsNullOrWhiteSpace(cells[0])) cells[0] = (++i).ToString();
            while (cells.Count < n) cells.Add("");
            rows.Add(cells.Take(n).ToArray());
        }
        if (rows.Count == 1) rows.Add(Enumerable.Range(0, n).Select(c => c == 0 && auto ? "1" : "").ToArray());
        return rows;
    }

    /// <summary>Độ rộng các cột (tổng 9100) của bảng N cột: cột STT hẹp, còn lại chia theo độ dài tiêu đề.</summary>
    private static int[] TableWidths(string[] header)
    {
        var n = header.Length; var total = 9100;
        var stt = Regex.IsMatch(header[0].Trim(), @"^(stt|no\.?|#)$", RegexOptions.IgnoreCase);
        var w = new int[n]; var rest = total;
        if (stt) { w[0] = 650; rest -= 650; }
        var start = stt ? 1 : 0; var weights = Enumerable.Range(start, n - start).Select(i => Math.Max(6, Math.Min(24, header[i].Length))).ToArray();
        var sum = weights.Sum();
        for (var i = start; i < n; i++) w[i] = rest * weights[i - start] / sum;
        w[n - 1] += total - w.Sum();
        return w;
    }

    /// <summary>Công văn / thư gửi khách hàng: ngày + số, Kính gửi, V/v, lời mở đầu, căn cứ, nội dung, bảng, lời kết, Nơi nhận, người ký của Fast. "congvan-en" = bản tiếng Anh.</summary>
    private static List<BbxnBlock> BuildLetter(BbxnState st, BbxnDocRequest req, BbxnTemplate tpl, Dictionary<string, string> vars)
    {
        var en = tpl.Layout.EndsWith("-en", StringComparison.OrdinalIgnoreCase);
        var f = st.Fast; var date = string.IsNullOrEmpty(req.NgayLap) ? st.NgayLap : req.NgayLap;
        var place = en ? (string.IsNullOrWhiteSpace(f.DiaDiemEn) ? "Ho Chi Minh City" : f.DiaDiemEn) : (string.IsNullOrWhiteSpace(f.DiaDiem) ? "TP. Hồ Chí Minh" : f.DiaDiem);
        var when = en ? DateTextEn(date) : "ngày " + DateText(date);
        var blocks = new List<BbxnBlock>
        {
            new() { Kind = "right", Text = $"{place}, {when}", Italic = true },
            new() { Kind = "right", Text = "Số: " + DocNumber(st, req, tpl), Bold = true },
            new() { Kind = "p", Text = (en ? "**Respectfully to:** " : "**Kính gửi:** ") + vars.GetValueOrDefault("ten_a_ngan", "") },
        };
        if (vars.TryGetValue("nguoi_nhan", out var nn) && !string.IsNullOrWhiteSpace(nn)) blocks.Add(new BbxnBlock { Kind = "p", Text = nn, Indent = 720 });
        var first = true;
        foreach (var line in Subst(tpl.Title, vars).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        { blocks.Add(new BbxnBlock { Kind = "p", Text = (first ? (en ? "**Ref:** " : "**V/v:** ") : "") + line.Trim() }); first = false; }
        if (!string.IsNullOrWhiteSpace(tpl.Greeting)) blocks.Add(new BbxnBlock { Kind = "p", Text = Subst(tpl.Greeting, vars) });
        foreach (var line in Subst(tpl.Basis, vars).Split('\n', StringSplitOptions.RemoveEmptyEntries)) blocks.Add(new BbxnBlock { Kind = "p", Text = line.Trim() });
        if (!string.IsNullOrWhiteSpace(tpl.Intro)) blocks.Add(new BbxnBlock { Kind = "p", Text = Subst(tpl.Intro, vars) });
        foreach (var item in tpl.Items) blocks.Add(new BbxnBlock { Kind = "p", Text = "- " + Subst(item, vars), Indent = 360 });
        if (tpl.Table) blocks.Add(new BbxnBlock { Kind = "table", Rows = TableRows(tpl, req, vars) });
        foreach (var line in Subst(tpl.Closing, vars).Split('\n', StringSplitOptions.RemoveEmptyEntries)) blocks.Add(new BbxnBlock { Kind = "p", Text = line.Trim() });
        var rec = (tpl.Recipients ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        blocks.Add(new BbxnBlock { Kind = "p", Text = "**" + (en ? "Recipients:" : "Nơi nhận:") + "**" + string.Concat(rec.Select(x => "\n- " + x)) });
        var chucVu = (string.IsNullOrWhiteSpace(en ? f.ChucVuEn : f.ChucVu) ? (en ? "DIRECTOR" : "GIÁM ĐỐC") : (en ? f.ChucVuEn : f.ChucVu)).ToUpperInvariant();
        blocks.Add(new BbxnBlock { Kind = "sign", Rows = { tpl.SignRoles.Count > 0 ? tpl.SignRoles.Select(x => Subst(x, vars)).ToArray() : new[] { "", chucVu } } });
        return blocks;
    }

    /// <summary>Phiếu / giấy / tài liệu khung: tên đơn vị, tiêu đề, các dòng "Nhãn|giá trị" (bảng 2 cột), đoạn văn, bảng, ngày lập và các ô ký.</summary>
    private static List<BbxnBlock> BuildForm(BbxnState st, BbxnDocRequest req, BbxnTemplate tpl, Dictionary<string, string> vars)
    {
        var f = st.Fast; var date = string.IsNullOrEmpty(req.NgayLap) ? st.NgayLap : req.NgayLap;
        var blocks = new List<BbxnBlock> { new() { Kind = "center", Text = string.IsNullOrWhiteSpace(f.TenNgan) ? f.Ten : f.TenNgan, Bold = true } };
        foreach (var line in Subst(tpl.Title, vars).Split('\n', StringSplitOptions.RemoveEmptyEntries)) blocks.Add(new BbxnBlock { Kind = "center", Text = line.Trim(), Bold = true });
        List<string[]>? pending = null;
        void Flush() { if (pending is { Count: > 0 }) blocks.Add(new BbxnBlock { Kind = "party", Rows = pending }); pending = null; }
        foreach (var raw in tpl.Items)
        {
            var line = Subst(raw, vars);
            var bar = line.IndexOf('|');
            if (bar > 0 && bar <= 120 && !line.StartsWith("-") && !line.StartsWith("**")) { (pending ??= new()).Add(new[] { line[..bar].Trim(), line[(bar + 1)..].Trim() }); continue; }
            Flush();
            if (string.IsNullOrWhiteSpace(line)) continue;
            blocks.Add(new BbxnBlock { Kind = "p", Text = line, Indent = line.StartsWith("-") ? 360 : 0 });
        }
        Flush();
        if (tpl.Table) blocks.Add(new BbxnBlock { Kind = "table", Rows = TableRows(tpl, req, vars) });
        if (!string.IsNullOrWhiteSpace(tpl.Closing)) blocks.Add(new BbxnBlock { Kind = "p", Text = Subst(tpl.Closing, vars) });
        if (tpl.SignRoles.Count > 0)
        {
            var place = string.IsNullOrWhiteSpace(f.DiaDiem) ? "TP. Hồ Chí Minh" : f.DiaDiem;
            blocks.Add(new BbxnBlock { Kind = "right", Text = $"{place}, ngày {DateText(date)}", Italic = true });
            blocks.Add(new BbxnBlock { Kind = "sign", Rows = { tpl.SignRoles.Select(x => Subst(x, vars)).ToArray() } });
        }
        return blocks;
    }

    private static List<BbxnBlock> BuildBilingual(BbxnState st, BbxnDocRequest req, BbxnTemplate tpl, Dictionary<string, string> vars)
    {
        var c = st.Customer; var f = st.Fast;
        foreach (var v in tpl.Vars) vars[v.Key] = req.Vars.TryGetValue(v.Key, out var val) && val.Length > 0 ? val : v.Default;
        foreach (var kv in req.Vars) vars.TryAdd(kv.Key, kv.Value);
        var date = string.IsNullOrEmpty(req.NgayLap) ? st.NgayLap : req.NgayLap;
        var haveDate = ParseDate(date, out var dd);
        var blocks = new List<BbxnBlock>
        {
            new() { Kind = "header", Text = req.Logo, En = true },
            new() { Kind = "right", Text = $"{(string.IsNullOrWhiteSpace(f.DiaDiem) ? "TP. Hồ Chí Minh" : f.DiaDiem)}, ngày " + (haveDate ? $"{dd.Day:00} tháng {Months[dd.Month]} năm {dd.Year}" : "…. tháng …. năm …..") + "\n" +
                    $"{(string.IsNullOrWhiteSpace(f.DiaDiemEn) ? "Ho Chi Minh City" : f.DiaDiemEn)}, " + (haveDate ? DateTextEn(date) : "……………"), Italic = true, En = true },
        };
        // tiêu đề: các dòng Việt rồi các dòng Anh
        foreach (var line in Subst(tpl.Title, vars).Split('\n', StringSplitOptions.RemoveEmptyEntries)) blocks.Add(new BbxnBlock { Kind = "center", Text = line.Trim(), Bold = true });
        foreach (var line in Subst(tpl.TitleEn, vars).Split('\n', StringSplitOptions.RemoveEmptyEntries)) blocks.Add(new BbxnBlock { Kind = "center", Text = line.Trim(), Bold = true, En = true });
        var number = DocNumber(st, req, tpl);
        blocks.Add(new BbxnBlock { Kind = "right", Text = "Số: " + number + "\nNo.: " + number, Bold = true, En = true });

        void Pair(string vi, string en, int indent = 0, bool bullet = false)
        {
            vi = (vi ?? "").Trim(); en = (en ?? "").Trim();
            if (vi.Length > 0) blocks.Add(new BbxnBlock { Kind = "p", Text = vi, Indent = indent });
            if (en.Length > 0) blocks.Add(new BbxnBlock { Kind = "p", Text = en, Indent = indent, En = true });
        }
        var basisVi = Subst(tpl.Basis, vars).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var basisEn = Subst(tpl.BasisEn, vars).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Max(basisVi.Length, basisEn.Length); i++) Pair(i < basisVi.Length ? basisVi[i] : "", i < basisEn.Length ? basisEn[i] : "");

        void Party(string key, string labelVi, string labelEn, BbxnParty p)
        {
            var ten = p.Ten.ToUpperInvariant(); var tenEn = string.IsNullOrWhiteSpace(p.TenEn) ? ten : p.TenEn.ToUpperInvariant();
            blocks.Add(new BbxnBlock { Kind = "p", Text = $"**{labelVi}: {ten}**" });
            blocks.Add(new BbxnBlock { Kind = "p", Text = $"**{labelEn}: {tenEn}**", En = true });
            var rows = new List<string[]> { new[] { "Địa chỉ", p.DiaChi }, new[] { "Address", string.IsNullOrWhiteSpace(p.DiaChiEn) ? p.DiaChi : p.DiaChiEn, "en" } };
            if (!string.IsNullOrWhiteSpace(p.DienThoai) || !string.IsNullOrWhiteSpace(p.Fax))
            {
                var tel = p.DienThoai + (string.IsNullOrWhiteSpace(p.Fax) ? "" : "      Fax: " + p.Fax);
                rows.Add(new[] { "Điện thoại", tel }); rows.Add(new[] { "Telephone", tel, "en" });
            }
            rows.Add(new[] { "Đại diện", p.DaiDien }); rows.Add(new[] { "Represented by", string.IsNullOrWhiteSpace(p.DaiDienEn) ? p.DaiDien : p.DaiDienEn, "en" });
            rows.Add(new[] { "Chức vụ", p.ChucVu }); rows.Add(new[] { "Position", string.IsNullOrWhiteSpace(p.ChucVuEn) ? p.ChucVu : p.ChucVuEn, "en" });
            if (!string.IsNullOrWhiteSpace(p.Mst)) { rows.Add(new[] { "Mã số thuế", p.Mst }); rows.Add(new[] { "Tax code", p.Mst, "en" }); }
            blocks.Add(new BbxnBlock { Kind = "party", Rows = rows });
        }
        Party("A", "Bên A", "Party A", c);
        Party("B", "Bên B", "Party B", f);

        Pair(Subst(tpl.Intro, vars), Subst(tpl.IntroEn, vars));
        for (var i = 0; i < tpl.Items.Count; i++)
            Pair(Subst(tpl.Items[i], vars), i < tpl.ItemsEn.Count ? Subst(tpl.ItemsEn[i], vars) : "");
        Pair(Subst(tpl.Closing, vars), Subst(tpl.ClosingEn, vars));
        blocks.Add(new BbxnBlock { Kind = "sign", En = true, Rows = { new[] { "Đại diện bên A\nREPRESENTATIVE OF PARTY A", "Đại diện bên B\nREPRESENTATIVE OF PARTY B" } } });
        return blocks;
    }

    // ------------------------------------------------------------------------------------------ logo
    private static readonly Dictionary<string, byte[]?> LogoCache = new();

    /// <summary>PNG của logo Fast ("1" / "2") nhúng trong Bcode.exe; null nếu id khác.</summary>
    public static byte[]? LogoBytes(string? id)
    {
        if (id is not ("1" or "2")) return null;
        lock (LogoCache)
        {
            if (LogoCache.TryGetValue(id, out var hit)) return hit;
            using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("bbxn.logo" + id + ".png");
            byte[]? bytes = null;
            if (s != null) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
            return LogoCache[id] = bytes;
        }
    }

    private static (int W, int H) PngSize(byte[] b) =>
        b.Length > 24 ? ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]) : (1, 1);

    /// <summary>Bề rộng logo trên giấy (EMU; 1 cm = 360000): logo 1 dài hơn nên rộng hơn logo 2.</summary>
    private static long LogoWidthEmu(string id) => id == "1" ? 1_260_000 : 900_000;

    // ------------------------------------------------------------------------------------------ xem trước (HTML)
    private static string H(string s) => WebUtility.HtmlEncode(s);

    /// <summary>Chữ **đậm** → &lt;b&gt;.</summary>
    private static string Inline(string s) => Regex.Replace(H(s), @"\*\*(.+?)\*\*", "<b>$1</b>").Replace("\n", "<br>");

    public static string ToHtml(List<BbxnBlock> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            var style = (b.Bold ? "font-weight:700;" : "") + (b.Italic ? "font-style:italic;" : "");
            switch (b.Kind)
            {
                case "header":
                {
                    var logo = LogoBytes(b.Text);
                    var img = logo is null ? "" : $"<img src=\"data:image/png;base64,{Convert.ToBase64String(logo)}\" style=\"width:{LogoWidthEmu(b.Text) / 9525}px\" />";
                    var en = b.En ? "<p class=\"c en\" style=\"font-weight:700;margin-top:.4rem\">The socialist republic of Viet Nam</p><p class=\"c en\" style=\"font-weight:700\">Independence – Freedom – Happiness</p>" : "";
                    sb.Append($"<table class=\"hdr\"><tr><td class=\"lg\">{img}</td><td class=\"nq\"><p class=\"c\" style=\"font-weight:700\">Cộng hòa xã hội chủ nghĩa Việt Nam</p><p class=\"c\" style=\"font-weight:700\">Độc lập – Tự do – Hạnh phúc</p><p class=\"c\">──────</p>{en}</td></tr></table>");
                    break;
                }
                case "center": sb.Append($"<p class=\"c{(b.En ? " en" : "")}\" style=\"{style}\">{Inline(b.Text)}</p>"); break;
                case "right":
                    {
                        var ls = b.Text.Split('\n');
                        for (var li = 0; li < ls.Length; li++)
                            sb.Append($"<p class=\"r{(b.En && li > 0 ? " en" : "")}\" style=\"{style}{(li < ls.Length - 1 ? "margin-bottom:.1rem" : "")}\">{Inline(ls[li])}</p>");
                        break;
                    }
                case "party":
                    sb.Append("<table class=\"party\">");
                    foreach (var r in b.Rows) { var en = r.Length > 2 ? " class=\"en\"" : ""; sb.Append($"<tr{en}><td class=\"l\">{H(r[0])}</td><td>: {H(r[1])}</td></tr>"); }
                    sb.Append("</table>");
                    break;
                case "table":
                    sb.Append("<table class=\"grid\">");
                    for (var i = 0; i < b.Rows.Count; i++)
                    {
                        var tag = i == 0 ? "th" : "td";
                        sb.Append("<tr>" + string.Concat(b.Rows[i].Select(cell => $"<{tag}>{Inline(cell)}</{tag}>")) + "</tr>");
                    }
                    sb.Append("</table>");
                    break;
                case "sign":
                    string Sg(string t) { var ls = t.Split('\n'); return Inline(ls[0]) + (b.En ? string.Concat(ls.Skip(1).Select(x => "<br><span class=\"en\">" + H(x) + "</span>")) : string.Concat(ls.Skip(1).Select(x => "<br>" + H(x)))); }
                    sb.Append("<table class=\"sign\"><tr>" + string.Concat(b.Rows[0].Select(c => $"<td>{Sg(c)}</td>")) + "</tr></table>");
                    break;
                default:
                    sb.Append($"<p class=\"j{(b.En ? " en" : "")}\" style=\"{style}margin-left:{b.Indent / 20}pt\">{Inline(b.Text)}</p>");
                    break;
            }
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------ xuất Word (.docx)
    private static string X(string s) => System.Security.SecurityElement.Escape(s) ?? "";

    private const int SmallSz = 24;   // chữ tiếng Anh: 12pt (chữ thường 13pt = 26 half-points)
    private static string Runs(string text, bool bold, bool italic, int size = 0)
    {
        var sb = new StringBuilder();
        var parts = Regex.Split(text, @"(\*\*.+?\*\*)");
        foreach (var part in parts)
        {
            if (part.Length == 0) continue;
            var isBold = bold; var t = part;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4) { isBold = true; t = part[2..^2]; }
            sb.Append("<w:r><w:rPr>" + (isBold ? "<w:b/>" : "") + (italic ? "<w:i/>" : "") + (size > 0 ? $"<w:sz w:val=\"{size}\"/><w:szCs w:val=\"{size}\"/>" : "") + "</w:rPr><w:t xml:space=\"preserve\">" + X(t) + "</w:t></w:r>");
        }
        return sb.ToString();
    }

    private static string P(string text, string align = "both", bool bold = false, bool italic = false, int indent = 0, int after = 120, bool pageBreakBefore = false, int size = 0) =>
        $"<w:p><w:pPr>{(pageBreakBefore ? "<w:pageBreakBefore/>" : "")}<w:spacing w:before=\"0\" w:after=\"{after}\"/>{(indent > 0 ? $"<w:ind w:left=\"{indent}\"/>" : "")}<w:jc w:val=\"{align}\"/></w:pPr>{Runs(text, bold, italic, size)}</w:p>";

    private static string Cell(int width, string inner, bool borders, string? shade = null) =>
        $"<w:tc><w:tcPr><w:tcW w:w=\"{width}\" w:type=\"dxa\"/>{(shade != null ? $"<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"{shade}\"/>" : "")}</w:tcPr>{inner}</w:tc>";

    private static string TableXml(int[] widths, IEnumerable<string> rowsXml, bool borders) =>
        "<w:tbl><w:tblPr><w:tblW w:w=\"0\" w:type=\"auto\"/>" +
        (borders ? "<w:tblBorders>" + string.Concat(new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(n => $"<w:{n} w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"000000\"/>")) + "</w:tblBorders>" : "") +
        "<w:tblLayout w:type=\"fixed\"/></w:tblPr><w:tblGrid>" + string.Concat(widths.Select(w => $"<w:gridCol w:w=\"{w}\"/>")) + "</w:tblGrid>" + string.Concat(rowsXml) + "</w:tbl>";

    private static string BlockXml(BbxnBlock b)
    {
        switch (b.Kind)
        {
            case "header":
            {
                var logo = LogoBytes(b.Text);
                var left = P("", "left", after: 0);
                if (logo != null)
                {
                    var (pw, ph) = PngSize(logo);
                    long cx = LogoWidthEmu(b.Text), cy = cx * ph / Math.Max(1, pw);
                    left = "<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\"/></w:pPr><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">" +
                           $"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{(b.Text == "1" ? 1 : 2)}\" name=\"Logo Fast\"/>" +
                           "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
                           "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:nvPicPr><pic:cNvPr id=\"0\" name=\"logo.png\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                           $"<pic:blipFill><a:blip r:embed=\"rIdLogo{b.Text}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
                           $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
                }
                var right = P("Cộng hòa xã hội chủ nghĩa Việt Nam", "center", true, after: 40) + P("Độc lập – Tự do – Hạnh phúc", "center", true, after: 40) + P("──────", "center", after: b.En ? 60 : 0) +
                            (b.En ? P("The socialist republic of Viet Nam", "center", true, true, after: 40, size: SmallSz) + P("Independence – Freedom – Happiness", "center", true, true, after: 0, size: SmallSz) : "");
                return TableXml(new[] { 3400, 5700 }, new[] { "<w:tr>" + Cell(3400, left, false) + Cell(5700, right, false) + "</w:tr>" }, false);
            }
            case "center": return b.En ? P(b.Text, "center", b.Bold, true, after: 40, size: SmallSz) : P(b.Text, "center", b.Bold, b.Italic, after: 40);
            case "right":
            {
                var ls = b.Text.Split('\n');
                return string.Concat(ls.Select((ln, i) => b.En && i > 0 ? P(ln, "right", b.Bold, true, after: i == ls.Length - 1 ? 120 : 20, size: SmallSz) : P(ln, "right", b.Bold, b.Italic, after: i == ls.Length - 1 ? 120 : 20)));
            }
            case "party":
            {
                var rows = b.Rows.Select(r => { var en = r.Length > 2; return "<w:tr>" + Cell(2000, P(r[0], "left", italic: en, after: 20, size: en ? SmallSz : 0), false) + Cell(7100, P(": " + r[1], "left", italic: en, after: 20, size: en ? SmallSz : 0), false) + "</w:tr>"; });
                return TableXml(new[] { 2000, 7100 }, rows, false);
            }
            case "table":
            {
                var w = b.Rows[0].Length == 3 ? new[] { 700, 6900, 1500 } : TableWidths(b.Rows[0]);
                var rows = b.Rows.Select((r, i) => "<w:tr>" + string.Concat(r.Select((cell, ci) => Cell(w[ci], P(cell, i == 0 || ci == 0 ? "center" : "left", bold: i == 0, after: 40), true, i == 0 ? "F2F2F2" : null))) + "</w:tr>");
                return TableXml(w, rows, true) + P("", after: 120);
            }
            case "sign":
            {
                var n = Math.Max(1, b.Rows[0].Length); var cw = 9100 / n;
                var head = "<w:tr>" + string.Concat(b.Rows[0].Select(role => Cell(cw, string.Concat(role.Split((char)10).Select((l, i) => P(l, "center", bold: true, italic: b.En && i > 0, after: 0, size: b.En && i > 0 ? SmallSz : 0))), false))) + "</w:tr>";
                var blank = "<w:tr>" + string.Concat(b.Rows[0].Select(_ => Cell(cw, P("", after: 0) + P("", after: 0) + P("", after: 0), false))) + "</w:tr>";
                return P("", after: 120) + TableXml(Enumerable.Repeat(cw, n).ToArray(), new List<string> { head, blank }, false);
            }
            default: return b.En ? P(b.Text, "both", b.Bold, true, b.Indent, after: 80, size: SmallSz) : P(b.Text, "both", b.Bold, b.Italic, b.Indent, after: 80);
        }
    }

    /// <summary>Ghi nhiều biên bản vào 1 file Word, mỗi biên bản bắt đầu ở trang mới.</summary>
    public static void WriteDocx(string path, IReadOnlyList<List<BbxnBlock>> documents)
    {
        var body = new StringBuilder();
        for (var d = 0; d < documents.Count; d++)
        {
            var first = true;
            foreach (var b in documents[d])
            {
                var xml = BlockXml(b);
                if (first && d > 0) xml = xml.Replace("<w:pPr>", "<w:pPr><w:pageBreakBefore/>");
                body.Append(xml); first = false;
            }
        }
        const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
        var logosUsed = documents.SelectMany(d => d).Where(b => b.Kind == "header" && LogoBytes(b.Text) != null).Select(b => b.Text).Distinct().ToList();
        var document = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {ns}><w:body>{body}" +
                       "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"680\" w:right=\"1134\" w:bottom=\"680\" w:left=\"1701\" w:header=\"709\" w:footer=\"709\" w:gutter=\"0\"/></w:sectPr></w:body></w:document>";
        var styles = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles {ns}><w:docDefaults><w:rPrDefault><w:rPr>" +
                     "<w:rFonts w:ascii=\"Times New Roman\" w:hAnsi=\"Times New Roman\" w:cs=\"Times New Roman\" w:eastAsia=\"Times New Roman\"/><w:sz w:val=\"26\"/><w:szCs w:val=\"26\"/><w:lang w:val=\"vi-VN\"/></w:rPr></w:rPrDefault></w:docDefaults></w:styles>";
        const string types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                             "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"png\" ContentType=\"image/png\"/>" +
                             "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                             "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>";
        const string rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
        var docRels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                      "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                      string.Concat(logosUsed.Select(id => $"<Relationship Id=\"rIdLogo{id}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/logo{id}.png\"/>")) + "</Relationships>";

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            var e = zip.CreateEntry(name);
            using var s = new StreamWriter(e.Open(), new UTF8Encoding(false));
            s.Write(content);
        }
        Add("[Content_Types].xml", types);
        Add("_rels/.rels", rels);
        Add("word/document.xml", document);
        Add("word/styles.xml", styles);
        Add("word/_rels/document.xml.rels", docRels);
        foreach (var id in logosUsed)
        {
            var e = zip.CreateEntry($"word/media/logo{id}.png");
            using var s = e.Open();
            var bytes = LogoBytes(id)!;
            s.Write(bytes, 0, bytes.Length);
        }
    }

    /// <summary>Tên file an toàn từ số biên bản: "KOG/BBCD/2609 – 38DV" → "KOG_BBCD_2609 - 38DV".</summary>
    public static string FileNameFor(string number) =>
        Regex.Replace(number.Replace('–', '-'), @"[\\/:*?""<>|]+", "_").Trim();
}
