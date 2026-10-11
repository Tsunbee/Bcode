namespace Bcode.ScreenDesigner.SampleData;

/// <summary>Yêu cầu "Điền dữ liệu mẫu" từ trang: các ô của màn hình (+ cột lưới) và nguồn dữ liệu đã chọn.</summary>
public sealed class SampleRequest
{
    /// <summary>"server|database" (chỉ SQL Server trên máy này) — null/rỗng = không dùng DB (danh mục mẫu + quy tắc).</summary>
    public string? Db { get; set; }
    /// <summary>Thay tên / MST / địa chỉ / điện thoại… của đối tác bằng dữ liệu giả (bật sẵn khi DB có dữ liệu nghiệp vụ).</summary>
    public bool Anonymize { get; set; }
    /// <summary>true = lấy 1 chứng từ có thật (nếu màn hình là chứng từ và DB có dữ liệu), false = chỉ danh mục + quy tắc.</summary>
    public bool UseVoucher { get; set; }
    /// <summary>Bảng của màn hình đọc từ XML (vd "m81$000000"), mã chứng từ (vd "HDA").</summary>
    public string? MasterTable { get; set; }
    public string? VoucherId { get; set; }
    public List<SampleField> Fields { get; set; } = new();
    public List<SampleField> GridCols { get; set; } = new();
    public int Rows { get; set; } = 3;
}

public sealed class SampleField
{
    public string Name { get; set; } = "";
    /// <summary>Controller của ô tra cứu (vd "Customer", "Item").</summary>
    public string? Ctl { get; set; }
    /// <summary>Tên biến hiện tên của ô tra cứu (vd "ten_kh%l").</summary>
    public string? Ref { get; set; }
    /// <summary>"text" | "num" | "date" | "check" | "lookup".</summary>
    public string Kind { get; set; } = "text";
    public string? Label { get; set; }
}

public sealed class SampleResult
{
    /// <summary>Tên biến → chuỗi hiển thị (đã định dạng số / ngày). Ô không có ở đây thì trang tự đoán như cũ.</summary>
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Dòng lưới: tên cột → chuỗi hiển thị.</summary>
    public List<Dictionary<string, string>> Rows { get; set; } = new();
    public string Source { get; set; } = "";
    public List<string> Notes { get; set; } = new();
}

/// <summary>1 DB FastBusiness tìm thấy trên máy (có bảng dmtk).</summary>
public sealed record SampleSource(string Id, string Server, string Database, bool HasData, bool Standard, int Items, int Customers);
