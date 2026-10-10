namespace Bcode.App.Services;

/// <summary>
/// Bộ biểu mẫu của Fast (thư mục "biểu mẫu": danh mục mẫu văn bản gửi khách hàng, kế hoạch, công văn, biên bản làm việc, bàn giao bảo hành, phiếu nội bộ…)
/// chuyển thành mẫu của "Tạo biên bản". Phân nhóm theo trình tự một dự án (đúng thứ tự danh mục 00.DM của Fast), cuối cùng là nhóm hành chính nội bộ.
/// Dữ liệu cá nhân / mật khẩu trong file mẫu gốc KHÔNG được chép vào đây.
/// </summary>
internal static class BbxnForms
{
    public const string G1 = "1. Khởi động & kế hoạch dự án";
    public const string G2 = "2. Khảo sát & tài liệu khảo sát";
    public const string G3 = "3. Cài đặt – đào tạo – nhập liệu";
    public const string G4 = "4. Nghiệm thu";
    public const string G5 = "5. Làm việc & công văn gửi khách hàng";
    public const string G6 = "6. Bàn giao – bảo hành – chăm sóc khách";
    public const string G7 = "7. Hành chính nội bộ (giấy tờ, phiếu)";

    /// <summary>Nhóm của các mẫu đã có sẵn trong BbxnService (theo Id).</summary>
    public static readonly Dictionary<string, string> BuiltInGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tlks"] = G2, ["tlksapi"] = G2, ["tlks-vn-en"] = G2, ["tlksapi-vn-en"] = G2,
        ["bbcd"] = G3, ["bctc"] = G3, ["xndv"] = G3, ["sn-vn-en"] = G3,
        ["ntct"] = G4, ["ntplhd"] = G4, ["bbnt"] = G4, ["nt-vn-en"] = G4,
    };

    private static BbxnTemplate Bb(string id, string name, string abbr, string group, string title, string[] items, params (string k, string l, string d)[] vars) => new()
    {
        Id = id, Name = name, Abbr = abbr, Group = group, Title = title, Items = items.ToList(), Builtin = true,
        Vars = vars.Select(v => new BbxnVar { Key = v.k, Label = v.l, Default = v.d }).ToList(),
    };

    private const string ColStt = "STT";

    public static List<BbxnTemplate> All()
    {
        var list = new List<BbxnTemplate>();

        // ---------------------------------------------------------------- 1. Khởi động & kế hoạch dự án
        list.Add(new BbxnTemplate
        {
            Id = "qd-doi-du-an", Name = "Thành lập đội dự án (gửi khách hàng)", Abbr = "QĐ", Group = BbxnForms.G1, Layout = "congvan", Builtin = true,
            Title = "Thành lập đội dự án",
            Basis = "Theo nội dung của {loai_hd} số {so_hd} ký ngày {ngay_hd} giữa {ten_b_ngan} và {ten_a_ngan}, công ty Fast quyết định thành lập đội dự án và gửi tới Quý công ty danh sách đội dự án triển khai phần mềm tại Quý công ty như sau:",
            Intro = "", Table = true, Columns = { "Stt", "Họ và tên", "Vị trí", "Số điện thoại", "Email" },
            DefaultRows = new[] { "Trưởng dự án", "Nhân viên triển khai", "Trưởng nhóm lập trình", "Nhân viên lập trình", "Nhân viên kiểm tra sản phẩm" }.Select(r => "| " + r + "||").ToList(),
            Closing = "Kính mong Quý công ty gửi cho chúng tôi danh sách đội dự án của Quý công ty, để hai bên cùng lên kế hoạch thực hiện dự án.\nTrân trọng",
            Recipients = "Như trên\nĐội dự án Fast\nLưu văn phòng",
            Vars = { new BbxnVar { Key = "nguoi_nhan", Label = "Người nhận (xưng hô – chức vụ)", Default = "Ông/Bà …… – Kế toán trưởng" } },
        });
        list.Add(new BbxnTemplate
        {
            Id = "qd-doi-du-an-en", Name = "Organize Project Team (English)", Abbr = "QĐ-EN", Group = BbxnForms.G1, Layout = "congvan-en", Builtin = true,
            Title = "FAST Team for {du_an_en}",
            Basis = "According to the contract no. {so_hd} signed on {ngay_hd_en} between {ten_b_en} and {ten_a_en}, FAST have officially organized a team for the project. We would like to inform FAST's Team as below:",
            Intro = "", Table = true, Columns = { "No", "Full name", "Project role", "Cellphone", "Email" },
            DefaultRows = { "| Project Manager||", "| Consultant||", "| Consultant||", "| Developer||" },
            Closing = "We would like to receive the feedback about your Team from your company so that both sides can cooperate effectively in this project.\nSincerely",
            Recipients = "As above\nFAST's Team\nFAST's Administration Dept.",
            Vars = { new BbxnVar { Key = "nguoi_nhan", Label = "Respectfully to (name – position)", Default = "Mr. …… – Chief Accountant" }, new BbxnVar { Key = "du_an_en", Label = "Tên dự án (Anh)", Default = "the Project" } },
        });
        list.Add(new BbxnTemplate
        {
            Id = "kh-trien-khai", Name = "Kế hoạch triển khai hợp đồng (gửi khách hàng)", Abbr = "FSG-CV", Group = BbxnForms.G1, Layout = "congvan", Builtin = true,
            Title = "KẾ HOẠCH TRIỂN KHAI HỢP ĐỒNG",
            Greeting = "{ten_b_ngan} xin gởi tới Quý công ty lời chào trân trọng. Cảm ơn Quý công ty đã lựa chọn và sử dụng sản phẩm của Công ty Fast.",
            Basis = "Theo {loai_hd} số {so_hd} ký ngày {ngay_hd} giữa {ten_b_ngan} và {ten_a_ngan}, chúng tôi gửi tới Quý công ty bản kế hoạch triển khai hợp đồng như sau:",
            Intro = "", Table = true, Columns = { "Ngày", "Nội dung", "Nhân sự Fast", "Nhân sự khách hàng", "Số ngày dự kiến" },
            DefaultRows =
            {
                "…/…/…… | Họp khởi động dự án giữa {ten_a_ngan} và Fast | Đội dự án Fast | Đội dự án khách hàng | 1 ngày",
                "…/…/…… | Khảo sát chi tiết các nghiệp vụ và yêu cầu phát sinh | … | Nhân sự chủ chốt phụ trách phân hệ được khảo sát | … ngày",
                "…/…/…… | Thiết kế mẫu thử cho các màn hình, chức năng quan trọng | … | … | … ngày",
                "…/…/…… | Lập trình theo tài liệu khảo sát và các mẫu đã thống nhất, cập nhật chỉnh sửa và chạy thử chương trình | … | … | … ngày",
                "…/…/…… | Sử dụng chính thức chương trình | … | … | … ngày",
                "…/…/…… | Nghiệm thu chương trình, chuyển sang giai đoạn bảo hành | … | … | 1 ngày",
            },
            Closing = "Ghi chú: Kế hoạch trên đây chỉ là dự kiến để hai bên sắp xếp công việc và nhân sự để triển khai dự án được hiệu quả. Kế hoạch cụ thể cho các bước triển khai tiếp theo của Giai đoạn 1 sẽ thể hiện trong kế hoạch triển khai chi tiết được hai bên thống nhất sau khi khảo sát.\nTrân trọng",
            Recipients = "Như trên\nĐội dự án Fast\nLưu văn phòng",
            Vars = { new BbxnVar { Key = "nguoi_nhan", Label = "Người nhận (xưng hô – chức vụ)", Default = "Ông/Bà …… – Giám đốc" } },
        });
        list.Add(new BbxnTemplate
        {
            Id = "kh-du-an-chi-tiet", Name = "Kế hoạch dự án chi tiết", Abbr = "KHDA", Group = BbxnForms.G1, Layout = "phieu", Builtin = true,
            Title = "KẾ HOẠCH DỰ ÁN",
            Items =
            {
                "Ngày|{ngay_ke_hoach}",
                "**1. GIỚI THIỆU**",
                "**1.1 Căn cứ**",
                "- {loai_hd} số {so_hd} ngày {ngay_hd} giữa {ten_a_ngan} (Bên A) và {ten_b_ngan} (Bên B).",
                "- Điều kiện thực tế của hai bên.",
                "**1.2 Mục đích**",
                "- Xác định phương thức triển khai, công tác phối hợp làm việc giữa hai bên trong dự án.",
                "- Thống nhất nội dung công việc sẽ được triển khai trong tiến trình thực hiện dự án nhằm rút ngắn thời gian triển khai, tăng cường trao đổi thông tin giữa hai bên.",
                "- Thống nhất về mặt thời gian cho từng giai đoạn của dự án để hai bên chuẩn bị đủ cơ sở vật chất và nhân sự.",
                "**1.3 Nội dung và phạm vi áp dụng**",
                "- Nhân sự huy động cho dự án; các giai đoạn của dự án; một số điểm cần thống nhất. Bản kế hoạch áp dụng cho cả hai bên trong suốt quá trình thực hiện dự án.",
                "**2. NHÂN SỰ HUY ĐỘNG CHO DỰ ÁN**",
                "- Nhân sự Fast: {nhan_su_fast}",
                "- Nhân sự khách hàng: {nhan_su_kh}",
                "**3. KẾ HOẠCH CỦA DỰ ÁN** (khảo sát; phân tích nghiệp vụ, viết tài liệu khảo sát; hoàn thiện sản phẩm; chuyển giao; kết thúc)",
                "**4. MỘT SỐ ĐIỂM CẦN THỐNG NHẤT**",
                "- {diem_thong_nhat}",
            },
            Table = true, Columns = { "Giai đoạn", "Nội dung công việc", "Thời gian", "Phụ trách" },
            DefaultRows = { "Khảo sát | | | ", "Phân tích nghiệp vụ, viết tài liệu khảo sát | | | ", "Hoàn thiện sản phẩm | | | ", "Chuyển giao | | | ", "Kết thúc | | | " },
            SignRoles = { "Đại diện Bên A", "Đại diện Bên B" },
            Vars =
            {
                new BbxnVar { Key = "ngay_ke_hoach", Label = "Ngày lập kế hoạch", Default = "…/…/……" },
                new BbxnVar { Key = "nhan_su_fast", Label = "Nhân sự Fast", Default = "(họ tên – vị trí – điện thoại)" },
                new BbxnVar { Key = "nhan_su_kh", Label = "Nhân sự khách hàng", Default = "(họ tên – vị trí – điện thoại)" },
                new BbxnVar { Key = "diem_thong_nhat", Label = "Điểm cần thống nhất", Default = "…" },
            },
        });

        // ---------------------------------------------------------------- 2. Khảo sát
        list.Add(new BbxnTemplate
        {
            Id = "tlks-khung", Name = "Tài liệu khảo sát (trang bìa + mục lục khung)", Abbr = "TLKS-KHUNG", Group = G2, Layout = "phieu", Builtin = true,
            Title = "TÀI LIỆU KHẢO SÁT\n{ten_a_ngan}",
            Items =
            {
                "Ngày viết tài liệu|{ngay_viet}", "Ngày sửa lần cuối|{ngay_sua}", "Người viết|{nguoi_viet}",
                "**MỤC LỤC**",
                "**1. PHÒNG KẾ TOÁN**",
                "1.1 Giới thiệu về công ty, quy trình nghiệp vụ hiện tại (giới thiệu về công ty; quy trình nghiệp vụ hiện tại)",
                "1.2 Xử lý nghiệp vụ trên chương trình Fast (kế toán tổng hợp; kế toán tiền mặt, tiền gửi, tiền vay; các phân hệ khác theo hợp đồng)",
                "**2. XÁC NHẬN TÀI LIỆU KHẢO SÁT**",
                "2.1 Kiểm tra tài liệu · 2.2 Tham gia thực hiện tài liệu · 2.3 Thay đổi chi tiết · 2.4 Xác nhận tài liệu",
                "**Giới thiệu về công ty**",
                "- Tên công ty: {ten_a}", "- Lĩnh vực hoạt động: {linh_vuc}", "- Phần mềm đang sử dụng: {phan_mem_cu}",
                "- Chế độ kế toán áp dụng: {che_do_kt}", "- Đồng tiền hạch toán: VND · Ngôn ngữ báo cáo: Việt Nam",
            },
            Vars =
            {
                new BbxnVar { Key = "ngay_viet", Label = "Ngày viết", Default = "…/…/……" }, new BbxnVar { Key = "ngay_sua", Label = "Ngày sửa lần cuối", Default = "…/…/……" },
                new BbxnVar { Key = "nguoi_viet", Label = "Người viết", Default = "……" }, new BbxnVar { Key = "linh_vuc", Label = "Lĩnh vực hoạt động", Default = "……" },
                new BbxnVar { Key = "phan_mem_cu", Label = "Phần mềm đang sử dụng", Default = "……" }, new BbxnVar { Key = "che_do_kt", Label = "Chế độ kế toán", Default = "Thông tư 200/2014/TT-BTC" },
            },
        });

        // ---------------------------------------------------------------- 3. Cài đặt – đào tạo – nhập liệu
        list.Add(Bb("bbxndt", "Xác nhận đào tạo", "BBXNĐT", G3, "BIÊN BẢN XÁC NHẬN ĐÀO TẠO", new[]
        {
            "Bên B đã cài đặt phần mềm kế toán Fast {phien_ban} trên server của {don_vi} tại địa chỉ {dia_chi_cai_dat}.",
            "Bên B đã cung cấp tài liệu hướng dẫn sử dụng cho bên A theo hợp đồng.",
            "Bên B đã hoàn thành việc tư vấn, đào tạo cho bên A sử dụng chương trình.",
            "Bên B tiếp tục hỗ trợ bên A trong quá trình nhập liệu, lên báo cáo {so_thang} tháng để làm cơ sở nghiệm thu chương trình.",
        }, ("phien_ban", "Phiên bản Fast", "FBO"), ("don_vi", "Đơn vị thụ hưởng", "bên A"), ("dia_chi_cai_dat", "Địa chỉ cài đặt", "……"), ("so_thang", "Số tháng lên báo cáo", "1, 2 hoặc 3")));
        list.Add(Bb("ntcdt", "Nghiệm thu cài đặt và đào tạo", "NTCĐĐT", G3, "BIÊN BẢN NGHIỆM THU CÀI ĐẶT VÀ ĐÀO TẠO", new[]
        {
            "Bên B đã cài đặt phần mềm kế toán Fast {phien_ban} trên {so_may_chu} máy chủ và {so_may_tram} máy trạm tại {noi_cai_dat} của bên A.",
            "Bên B đã cung cấp tài liệu hướng dẫn sử dụng cho bên A theo hợp đồng.",
            "Bên B đã hoàn thành việc tư vấn, đào tạo cho bên A sử dụng chương trình.",
            "Bên B tiếp tục hỗ trợ bên A trong quá trình nhập liệu, lên báo cáo 1, 2 hoặc 3 tháng để làm cơ sở nghiệm thu chương trình.",
        }, ("phien_ban", "Phiên bản Fast", "FBO"), ("so_may_chu", "Số máy chủ", "01"), ("so_may_tram", "Số máy trạm", "…"), ("noi_cai_dat", "Nơi cài đặt", "Phòng kế toán")));

        // ---------------------------------------------------------------- 4. Nghiệm thu
        list.Add(Bb("nthd", "Nghiệm thu hợp đồng", "NTHD", G4, "BIÊN BẢN NGHIỆM THU HỢP ĐỒNG", new[]
        {
            "Bên B đã cài đặt chương trình Fast {phien_ban} trên {so_may_chu} máy chủ (gồm {so_database} database và {so_dvcs} đơn vị cơ sở) và cài đặt chương trình trên {so_may_tram} máy trạm tại văn phòng bên A theo đúng hợp đồng.",
            "Bên B hoàn thành việc chỉnh sửa chương trình cho bên A theo đúng tài liệu khảo sát 2 bên đã ký.",
            "Bên B đã hỗ trợ bên A trong quá trình nhập liệu và lên được các báo cáo tài chính và báo cáo đặc thù theo tài liệu khảo sát hai bên đã ký dựa trên số liệu tháng {thang_so_lieu}.",
            "Chương trình đã chạy đúng theo yêu cầu của bên A.",
            "Hai bên thống nhất nghiệm thu {loai_hd} số {so_hd} ký ngày {ngay_hd} và chuyển {loai_hd_ngan} qua giai đoạn bảo hành. Bên B chịu trách nhiệm bảo hành cho bên A theo đúng điều khoản bảo hành của hợp đồng.",
        }, ("phien_ban", "Phiên bản Fast", "FBO"), ("so_may_chu", "Số máy chủ", "01"), ("so_database", "Số database", "01"), ("so_dvcs", "Số đơn vị cơ sở", "01"), ("so_may_tram", "Số máy trạm", "…"), ("thang_so_lieu", "Số liệu tháng", "…/……")));

        // ---------------------------------------------------------------- 5. Làm việc & công văn
        var lv = Bb("bblv", "Biên bản làm việc (bảng vấn đề – kế hoạch)", "BBLV", G5, "BIÊN BẢN LÀM VIỆC", new string[0],
            ("ngay_lv", "Ngày làm việc", "…/…/……"), ("xac_nhan_ten", "Tiêu đề cột xác nhận", "Xác nhận của khách hàng"));
        lv.Basis = "Căn cứ công việc hai bên đã thực hiện.";
        lv.Intro = "Hai bên cùng thống nhất các nội dung sau. Các vấn đề còn tồn tại và đã giải quyết trong ngày {ngay_lv}:";
        lv.Table = true; lv.Columns = new() { "Phân hệ", "Nội dung công việc", "Fast", "Kế hoạch", "{xac_nhan_ten}" };
        lv.DefaultRows = new() { "… | … | … | …/…/…… | ", "… | … | … | …/…/…… | " };
        lv.Closing = "Biên bản có 01 (một) trang, được lập thành 02 (hai) bản, mỗi bên giữ 01 (một) bản có giá trị như nhau.";
        list.Add(lv);

        list.Add(new BbxnTemplate
        {
            Id = "cv-gui-kh", Name = "Công văn gửi khách hàng", Abbr = "FSG-CV", Group = G5, Layout = "congvan", Builtin = true,
            Title = "{noi_dung_cv}",
            Greeting = "{ten_b_ngan} xin gởi tới Quý công ty lời chào trân trọng. Cảm ơn Quý công ty đã lựa chọn và sử dụng sản phẩm của Công ty Fast.",
            Basis = "Căn cứ vào:\n{loai_hd} số {so_hd} ký ngày {ngay_hd} giữa {ten_b_ngan} và {ten_a_ngan};\n{can_cu_khac}",
            Intro = "Công ty Fast đề nghị như sau:",
            Items = { "{de_nghi}" },
            Closing = "Rất mong nhận được sự hợp tác từ Quý công ty.\nTrân trọng!",
            Recipients = "Như trên.\nVăn phòng.",
            Vars =
            {
                new BbxnVar { Key = "nguoi_nhan", Label = "Người nhận (xưng hô – chức vụ)", Default = "Ông/Bà …… – Kế toán trưởng" },
                new BbxnVar { Key = "noi_dung_cv", Label = "V/v (nội dung chính)", Default = "……" },
                new BbxnVar { Key = "can_cu_khac", Label = "Căn cứ khác (vd biên bản nghiệm thu số …)", Default = "Biên bản nghiệm thu chương trình số …… ký ngày …/…/……." },
                new BbxnVar { Key = "de_nghi", Label = "Nội dung đề nghị", Default = "……" },
            },
        });
        list.Add(new BbxnTemplate
        {
            Id = "yc-htkt", Name = "Phiếu yêu cầu hỗ trợ kỹ thuật", Abbr = "HTKT", Group = G5, Layout = "phieu", Builtin = true,
            Title = "PHIẾU YÊU CẦU HỖ TRỢ KỸ THUẬT",
            Items =
            {
                "Tên khách hàng|{ten_a}", "Sản phẩm, phiên bản|{phien_ban}", "Ngày phát sinh vấn đề|{ngay_phat_sinh}", "Nhân viên liên hệ bên khách hàng|{nv_lien_he}",
                "Nhân viên tư vấn / bảo hành|{nv_fast}", "Phòng|{phong}",
                "**Mô tả yêu cầu, vấn đề phát sinh**", "{mo_ta}",
                "Người giao|{nguoi_giao}", "Ngày giao|{ngay_giao}", "Ngày đề nghị hoàn thành|{ngay_de_nghi}",
                "Người nhận|{nguoi_nhan_ht}", "Người sửa|{nguoi_sua}", "Ngày hẹn hoàn thành|{ngay_hen}", "Ngày hoàn thành thực tế|{ngay_thuc_te}",
            },
            SignRoles = { "Người giao", "Người nhận", "Người sửa" },
            Vars =
            {
                new BbxnVar { Key = "phien_ban", Label = "Sản phẩm, phiên bản", Default = "FBO" }, new BbxnVar { Key = "ngay_phat_sinh", Label = "Ngày phát sinh vấn đề", Default = "" },
                new BbxnVar { Key = "nv_lien_he", Label = "Nhân viên liên hệ bên KH", Default = "" }, new BbxnVar { Key = "nv_fast", Label = "Nhân viên TV/BH", Default = "" },
                new BbxnVar { Key = "phong", Label = "Phòng", Default = "Triển khai" }, new BbxnVar { Key = "mo_ta", Label = "Mô tả vấn đề", Default = "Vấn đề 1: ……" },
                new BbxnVar { Key = "nguoi_giao", Label = "Người giao", Default = "" }, new BbxnVar { Key = "ngay_giao", Label = "Ngày giao", Default = "" }, new BbxnVar { Key = "ngay_de_nghi", Label = "Ngày đề nghị hoàn thành", Default = "" },
                new BbxnVar { Key = "nguoi_nhan_ht", Label = "Người nhận", Default = "" }, new BbxnVar { Key = "nguoi_sua", Label = "Người sửa", Default = "" },
                new BbxnVar { Key = "ngay_hen", Label = "Ngày hẹn hoàn thành", Default = "" }, new BbxnVar { Key = "ngay_thuc_te", Label = "Ngày hoàn thành thực tế", Default = "" },
            },
        });

        // ---------------------------------------------------------------- 6. Bàn giao – bảo hành – chăm sóc
        list.Add(new BbxnTemplate
        {
            Id = "bg-bao-hanh", Name = "Biên bản bàn giao bảo hành", Abbr = "BGBH", Group = G6, Layout = "phieu", Builtin = true,
            Title = "BIÊN BẢN BÀN GIAO BẢO HÀNH",
            Items =
            {
                "**I. THÔNG TIN KHÁCH HÀNG**",
                "Tên công ty|{ten_a}", "Địa chỉ|{dia_chi_a}", "Điện thoại|{dien_thoai_a}", "Liên hệ|{lien_he}", "Chức vụ|{chuc_vu_lh}", "Email|{email_lh}",
                "**II. THÔNG TIN VỀ CHƯƠNG TRÌNH**",
                "Phiên bản sử dụng|{phien_ban}", "Số lượng phiên bản / số máy đang dùng|{so_may}", "Hệ quản trị CSDL|{hqt_csdl}",
                "Số lượng database / mã đơn vị cơ sở đang sử dụng|{so_db}", "Có nhiều chi nhánh? Có sao chép số liệu vào ra? Có tổng hợp số liệu?|{da_chi_nhanh}",
                "Có chạy online / xem báo cáo web? (host)|{online}", "Đã update hóa đơn / TT 200: có – chưa|{update_hd}", "Chương trình hỗ trợ từ xa đang sử dụng|{ho_tro_tu_xa}",
                "Đã lên số liệu đúng đến tháng|{den_thang}", "Các phân hệ sử dụng trong hợp đồng|{phan_he}",
                "Tài khoản đăng nhập chương trình / CSDL / máy chủ|(ghi trong tài liệu bàn giao riêng — không in vào biên bản)",
                "**III. THÔNG TIN CHỈNH SỬA**",
                "- Xem chi tiết thông tin chỉnh sửa trong tài liệu bàn giao bảo hành.",
                "Ghi chú|{ghi_chu}",
            },
            SignRoles = { "PHÒNG TRIỂN KHAI", "PHÒNG BẢO HÀNH" },
            Vars =
            {
                new BbxnVar { Key = "lien_he", Label = "Người liên hệ", Default = "" }, new BbxnVar { Key = "chuc_vu_lh", Label = "Chức vụ người liên hệ", Default = "" }, new BbxnVar { Key = "email_lh", Label = "Email", Default = "" },
                new BbxnVar { Key = "phien_ban", Label = "Phiên bản sử dụng", Default = "FBO" }, new BbxnVar { Key = "so_may", Label = "Số phiên bản / số máy", Default = "" },
                new BbxnVar { Key = "hqt_csdl", Label = "Hệ quản trị CSDL", Default = "SQL Server" }, new BbxnVar { Key = "so_db", Label = "Số database / đơn vị cơ sở", Default = "" },
                new BbxnVar { Key = "da_chi_nhanh", Label = "Chi nhánh / sao chép / tổng hợp", Default = "Không" }, new BbxnVar { Key = "online", Label = "Chạy online / báo cáo web", Default = "Không" },
                new BbxnVar { Key = "update_hd", Label = "Đã update hóa đơn / thông tư", Default = "" }, new BbxnVar { Key = "ho_tro_tu_xa", Label = "Phần mềm hỗ trợ từ xa", Default = "TeamViewer" },
                new BbxnVar { Key = "den_thang", Label = "Đã lên số liệu đúng đến tháng", Default = "" }, new BbxnVar { Key = "phan_he", Label = "Các phân hệ trong hợp đồng", Default = "" },
                new BbxnVar { Key = "ghi_chu", Label = "Ghi chú", Default = "" },
            },
        });
        list.Add(new BbxnTemplate
        {
            Id = "phieu-cskh", Name = "Phiếu chăm sóc khách hàng", Abbr = "CSKH", Group = G6, Layout = "phieu", Builtin = true,
            Title = "PHIẾU CHĂM SÓC KHÁCH HÀNG\nCUSTOMER CARE FORM",
            Items =
            {
                "Khách hàng / Customer|{ten_a}", "Địa chỉ / Address|{dia_chi_a}", "Người liên hệ / Contact|{lien_he}", "Điện thoại / Tel|{dien_thoai_a}",
                "**Nội dung yêu cầu / Request**", "{noi_dung}", "**Nội dung xử lý / Handling**", "{xu_ly}", "**Kết quả / Result**", "{ket_qua}",
            },
            SignRoles = { "Khách hàng", "Nhân viên Fast" },
            Vars =
            {
                new BbxnVar { Key = "lien_he", Label = "Người liên hệ", Default = "" }, new BbxnVar { Key = "noi_dung", Label = "Nội dung yêu cầu", Default = "……" },
                new BbxnVar { Key = "xu_ly", Label = "Nội dung xử lý", Default = "……" }, new BbxnVar { Key = "ket_qua", Label = "Kết quả", Default = "……" },
            },
        });

        // ---------------------------------------------------------------- 7. Hành chính nội bộ
        list.Add(new BbxnTemplate
        {
            Id = "giay-di-duong", Name = "Giấy đi đường", Abbr = "GĐĐ", Group = G7, Layout = "phieu", Builtin = true,
            Title = "GIẤY ĐI ĐƯỜNG",
            Items =
            {
                "Cấp cho|{nguoi_di}", "Chức vụ|{chuc_vu_di}", "Được cử đi công tác tại|{noi_cong_tac}", "Theo công lệnh (hoặc quyết định) số|{so_cong_lenh}",
                "Từ ngày|{tu_ngay}", "Đến ngày|{den_ngay}",
            },
            Table = true, Columns = { "Ngày giờ", "Nơi đi – nơi đến", "Phương tiện sử dụng", "Độ dài chặng đường (km)", "Thời gian lưu trú trên đường", "Thời gian lưu trú ở nơi đến", "Chứng nhận của cơ quan ký tên, đóng dấu" },
            DefaultRows = new[] { "…/…/…… | Nơi đi: ……", "…/…/…… | Nơi đến: ……" }.Select(r => r + " | | | | | ").ToList(),
            SignRoles = { "Thủ trưởng đơn vị" },
            Vars =
            {
                new BbxnVar { Key = "nguoi_di", Label = "Cấp cho", Default = "" }, new BbxnVar { Key = "chuc_vu_di", Label = "Chức vụ", Default = "" },
                new BbxnVar { Key = "noi_cong_tac", Label = "Nơi công tác", Default = "" }, new BbxnVar { Key = "so_cong_lenh", Label = "Công lệnh / quyết định số", Default = "" },
                new BbxnVar { Key = "tu_ngay", Label = "Từ ngày", Default = "…/…/……" }, new BbxnVar { Key = "den_ngay", Label = "Đến ngày", Default = "…/…/……" },
            },
        });
        list.Add(DeNghi("de-nghi-thanh-toan", "Giấy đề nghị thanh toán", "GĐNTT", "GIẤY ĐỀ NGHỊ THANH TOÁN", "Lý do thanh toán", new[] { "Người đề nghị", "Trưởng bộ phận", "Kế toán", "Giám đốc" }, thanhToan: true));
        list.Add(DeNghi("de-nghi-tam-ung", "Giấy đề nghị tạm ứng", "GĐNTU", "GIẤY ĐỀ NGHỊ TẠM ỨNG", "Lý do tạm ứng", new[] { "Người đề nghị", "Trưởng bộ phận", "Giám đốc" }, thanhToan: false));
        list.Add(new BbxnTemplate
        {
            Id = "phieu-cong-tac", Name = "Phiếu công tác thực hiện hợp đồng", Abbr = "PCT", Group = G7, Layout = "phieu", Builtin = true,
            Title = "PHIẾU CÔNG TÁC THỰC HIỆN HỢP ĐỒNG",
            Items = { "Tên khách hàng|{ten_a}", "Địa chỉ|{dia_chi_a}", "Điện thoại|{dien_thoai_a}", "Phiên bản|{phien_ban}" },
            Table = true, Columns = { "Ngày", "Nhân viên thực hiện", "Chi tiết công việc", "Ngày hoàn thành", "Xác nhận của khách hàng" },
            DefaultRows = { "…/…/…… | … | … | …/…/…… | ", "…/…/…… | … | … | …/…/…… | " },
            SignRoles = { "Đại diện khách hàng", "Nhân viên Fast" },
            Vars = { new BbxnVar { Key = "phien_ban", Label = "Phiên bản", Default = "FBO" } },
        });
        var defClosing = new BbxnTemplate().Closing;
        foreach (var t in list) if (t.Layout.Length > 0 && t.Closing == defClosing) t.Closing = "";     // phiếu / công văn không dùng câu kết của biên bản
        return list;
    }

    private static BbxnTemplate DeNghi(string id, string name, string abbr, string title, string lyDo, string[] roles, bool thanhToan)
    {
        var t = new BbxnTemplate
        {
            Id = id, Name = name, Abbr = abbr, Group = G7, Layout = "phieu", Builtin = true, Title = title,
            Items = { "Người đề nghị|{nguoi_de_nghi}", "Bộ phận|{bo_phan}", lyDo + "|{ly_do}" },
            Table = true, Columns = { "Stt", "Hàng hóa, dịch vụ", "Đvt", "SL", "Đơn giá", "Thành tiền", "Ghi chú" },
            DefaultRows = { "… | | | | | ", " | | | | | " },
            SignRoles = roles.ToList(),
            Vars =
            {
                new BbxnVar { Key = "nguoi_de_nghi", Label = "Người đề nghị", Default = "" }, new BbxnVar { Key = "bo_phan", Label = "Bộ phận", Default = "Phòng Triển khai" },
                new BbxnVar { Key = "ly_do", Label = lyDo, Default = "" }, new BbxnVar { Key = "tong_cong", Label = "Tổng cộng (đ)", Default = "" },
            },
        };
        if (thanhToan)
        {
            t.Items.AddRange(new[] { "Khách hàng / từ ngày – đến ngày|{khach_hang}", "Tổng cộng|{tong_cong}", "Số tiền đã tạm ứng|{da_tam_ung}", "Số tiền phải hoàn ứng|{hoan_ung}", "Số tiền còn được thanh toán|{con_lai}" });
            t.Vars.AddRange(new[]
            {
                new BbxnVar { Key = "khach_hang", Label = "Khách hàng / thời gian", Default = "" }, new BbxnVar { Key = "da_tam_ung", Label = "Số tiền đã tạm ứng", Default = "" },
                new BbxnVar { Key = "hoan_ung", Label = "Số tiền phải hoàn ứng", Default = "" }, new BbxnVar { Key = "con_lai", Label = "Số tiền còn được thanh toán", Default = "" },
            });
        }
        else
        {
            t.Items.AddRange(new[] { "Tổng cộng|{tong_cong}", "Thời hạn hoàn ứng|{han_hoan_ung}" });
            t.Vars.Add(new BbxnVar { Key = "han_hoan_ung", Label = "Thời hạn hoàn ứng", Default = "" });
        }
        return t;
    }
}
