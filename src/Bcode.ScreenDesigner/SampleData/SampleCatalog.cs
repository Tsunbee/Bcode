namespace Bcode.ScreenDesigner.SampleData;

/// <summary>
/// Danh mục mẫu tự soạn (giả nhưng đúng kiểu Việt Nam, mã – tên – đơn vị tính khớp cặp) — bù cho DB mẫu chuẩn (Release_*) vốn trống khách hàng / vật tư / kho,
/// và làm dữ liệu thay thế khi ẩn danh. Tài khoản / thuế suất ở đây chỉ là dự phòng: có DB thì lấy đúng từ dmtk / dmthue.
/// </summary>
public static class SampleCatalog
{
    public sealed record Partner(string Code, string Name, string Contact, string Address, string TaxCode, string Phone, string Email);
    public sealed record Item(string Code, string Name, string Unit, decimal Price, string TaxCode, string Group);

    public static readonly Partner[] Customers =
    {
        new("KH0001", "Công ty TNHH Thương mại Minh Phát", "Nguyễn Văn An", "125 Nguyễn Thị Minh Khai, Phường Bến Thành, TP. Hồ Chí Minh", "0312345678", "028 3822 1234", "ketoan@minhphat.vn"),
        new("KH0002", "Công ty Cổ phần Thực phẩm Sao Việt", "Trần Thị Bích Ngọc", "48 Lê Duẩn, Phường Sài Gòn, TP. Hồ Chí Minh", "0309876543", "028 3910 5566", "muahang@saoviet.com.vn"),
        new("KH0003", "Công ty TNHH Phân phối Hoàng Long", "Lê Quốc Hùng", "Lô B2, KCN Sóng Thần, Dĩ An, Bình Dương", "3701234567", "0274 3733 888", "info@hoanglong.vn"),
        new("KH0004", "Công ty TNHH Xuất nhập khẩu Đông Á", "Phạm Minh Tuấn", "17 Trần Hưng Đạo, Phường Cửa Nam, Hà Nội", "0101234569", "024 3936 7788", "xnk@donga-trading.vn"),
        new("KH0005", "Siêu thị Tiện Lợi Xanh", "Võ Thị Hồng", "220 Hùng Vương, Phường Thanh Khê, Đà Nẵng", "0401234570", "0236 3825 999", "order@tienloixanh.vn"),
        new("KH0006", "Hộ kinh doanh Nguyễn Thanh Bình", "Nguyễn Thanh Bình", "35 Chợ Lớn, Phường Bình Tây, TP. Hồ Chí Minh", "8012345671", "0908 456 123", "thanhbinh.hkd@gmail.com"),
    };

    public static readonly Partner[] Suppliers =
    {
        new("NCC0001", "Công ty TNHH Bao bì Tân Tiến", "Đỗ Văn Khánh", "KCN Tân Tạo, Bình Tân, TP. Hồ Chí Minh", "0302223334", "028 3754 1122", "sales@tantienpack.vn"),
        new("NCC0002", "Công ty Cổ phần Nông sản Tây Nguyên", "Y Bloong Niê", "Km 7 QL14, Buôn Ma Thuột, Đắk Lắk", "6001234565", "0262 3812 345", "contact@taynguyenagri.vn"),
    };

    public static readonly Item[] Items =
    {
        new("VT0001", "Hạt điều nhân W320", "Kg", 285000m, "08", "HH"),
        new("VT0002", "Hạt điều rang muối hộp 500g", "Hộp", 165000m, "10", "TP"),
        new("VT0003", "Cà phê rang xay nguyên chất 1kg", "Gói", 240000m, "10", "TP"),
        new("VT0004", "Hạt tiêu đen xuất khẩu", "Kg", 152000m, "05", "HH"),
        new("VT0005", "Bao bì carton 5 lớp 20kg", "Thùng", 18500m, "10", "VT"),
        new("VT0006", "Túi zip PE in logo 500g", "Cái", 1200m, "10", "VT"),
        new("VT0007", "Dịch vụ vận chuyển nội địa", "Chuyến", 1500000m, "10", "DV"),
    };

    public static readonly (string Code, string Name)[] Sites = { ("KHO01", "Kho thành phẩm"), ("KHO02", "Kho nguyên vật liệu"), ("KHO03", "Kho hàng gửi bán") };
    public static readonly (string Code, string Name)[] Employees = { ("NV001", "Nguyễn Hoàng Nam"), ("NV002", "Lê Thị Thu Hà"), ("NV003", "Trương Văn Phúc") };
    public static readonly (string Code, string Name)[] Departments = { ("BH", "Phòng Kinh doanh"), ("KT", "Phòng Kế toán"), ("SX", "Xưởng sản xuất") };
    public static readonly (string Code, string Name)[] Jobs = { ("VV001", "Dự án mở rộng kênh siêu thị"), ("VV002", "Hợp đồng xuất khẩu Q3") };
    public static readonly (string Code, string Name)[] Contracts = { ("HD2026-015", "Hợp đồng mua bán số 15/2026/HĐMB"), ("HD2026-021", "Hợp đồng nguyên tắc số 21/2026") };

    /// <summary>Dự phòng khi không có DB: tài khoản / thuế / ngoại tệ theo hệ thống tài khoản TT200.</summary>
    public static readonly Dictionary<string, string> Accounts = new()
    {
        ["1111"] = "Tiền Việt Nam", ["1121"] = "Tiền Việt Nam gửi ngân hàng", ["131"] = "Phải thu của khách hàng", ["1561"] = "Giá mua hàng hóa",
        ["1551"] = "Thành phẩm nhập kho", ["331"] = "Phải trả cho người bán", ["333111"] = "Thuế GTGT đầu ra phải nộp", ["5111"] = "Doanh thu bán hàng hóa",
        ["5112"] = "Doanh thu bán các thành phẩm", ["5211"] = "Chiết khấu thương mại", ["632"] = "Giá vốn hàng bán", ["6421"] = "Chi phí nhân viên quản lý",
    };
    public static readonly Dictionary<string, (string Name, decimal Rate)> Taxes = new()
    {
        ["00"] = ("Thuế suất 0%", 0), ["05"] = ("Thuế suất 5%", 5), ["08"] = ("Thuế suất 8%", 8), ["10"] = ("Thuế suất 10%", 10), ["KT"] = ("Không chịu thuế", 0),
    };
    public static readonly Dictionary<string, string> Currencies = new() { ["VND"] = "Đồng Việt Nam", ["USD"] = "Đô la Mỹ", ["EUR"] = "Euro" };
}
