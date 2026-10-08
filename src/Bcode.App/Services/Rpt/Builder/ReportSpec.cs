namespace Bcode.App.Services.Rpt.Builder;

/// <summary>Một bảng nối thêm vào bảng chính (thường là danh mục dm* để lấy tên). <see cref="Left"/> là cột của bảng chính (hoặc bảng nối trước), <see cref="Right"/> là cột của <see cref="Table"/>.</summary>
public sealed class JoinSpec
{
    public string Table { get; set; } = "";
    public string Alias { get; set; } = "";
    public string Left { get; set; } = "";       // vd "a.ma_kh"
    public string Right { get; set; } = "";      // vd "ma_kh"
    /// <summary>Nối nhiều cột: Left "a.stt_rec,a.stt_rec0" ↔ Right "stt_rec,stt_rec0" (cùng thứ tự).</summary>
    public string Type { get; set; } = "left";   // left | inner
    /// <summary>true = bảng chứng từ phân kỳ (d21$%Partition) nối ngay trong bước lấy dữ liệu theo kỳ; false = danh mục nối ở bước ghép tên.</summary>
    public bool Partitioned { get; set; }
    /// <summary>Cột ngày của bảng chứng từ nối (lọc kỳ); rỗng = dùng cột ngày của bảng chính.</summary>
    public string DateField { get; set; } = "";
}

/// <summary>
/// Một cột của báo cáo. <see cref="Source"/> = "alias.cột" (hoặc biểu thức cho cột số liệu). Cột có <see cref="Aggregate"/> là số liệu (đo), cột còn lại là chiều (nhóm theo).
/// <see cref="Source2"/> khác rỗng = cột tên song ngữ: <c>CASE WHEN @Language = 'V' THEN Source ELSE Source2 END</c>.
/// <see cref="Key"/> = cột mã dùng làm khoá khi cột hiển thị là tên (cần cho pivot và nhóm theo).
/// </summary>
public sealed class ColumnSpec
{
    public string Source { get; set; } = "";
    public string Source2 { get; set; } = "";
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string HeaderVi { get; set; } = "";
    public string HeaderEn { get; set; } = "";
    public string Type { get; set; } = "String";        // String | DateTime | Decimal | Int
    public int Width { get; set; } = 100;
    public string Format { get; set; } = "";            // dataFormatString của Grid
    public string Aggregate { get; set; } = "";         // "" | Sum | Count | Min | Max | Avg
    public string Order { get; set; } = "";             // "" | asc | desc
    public bool Hidden { get; set; }
    /// <summary>Cột số dư lấy từ hàm số dư của Fast: "dk:du_no" (đầu kỳ) / "ck:du_no" (cuối kỳ). Rỗng = cột thường.</summary>
    public string Bal { get; set; } = "";
    public bool IsMeasure => !string.IsNullOrEmpty(Aggregate);
}

/// <summary>Một bộ lọc người dùng nhập ở màn hình điều kiện lọc; sinh ra field trong Filter xml, tham số của procedure và điều kiện lọc trong procedure.</summary>
public sealed class FilterSpec
{
    public string Field { get; set; } = "";             // tên field của Filter, thường = tên cột: ma_kh
    public string Column { get; set; } = "";            // cột để lọc, có alias: a.ma_kh
    public string Param { get; set; } = "";             // tên tham số procedure (rỗng = tự đặt)
    public string Op { get; set; } = "like";            // like | eq | in | inlist | date
    public string HeaderVi { get; set; } = "";
    public string HeaderEn { get; set; } = "";
    public string Style { get; set; } = "";             // AutoComplete | Lookup | DropDownList | Numeric | (rỗng = ô chữ)
    public string Controller { get; set; } = "";
    public string Reference { get; set; } = "";         // field tên đi kèm: ten_kh%l
    public string Key { get; set; } = "status = '1'";
    public string Check { get; set; } = "1 = 1";
    public string Information { get; set; } = "";
    public List<ComboItem> Items { get; set; } = new();  // với DropDownList
}

public sealed class ComboItem { public string Value { get; set; } = ""; public string Vi { get; set; } = ""; public string En { get; set; } = ""; }

/// <summary>Báo cáo pivot (ma trận): các cột <see cref="Rows"/> là nhãn dòng, <see cref="Column"/> là chiều ngang, <see cref="Values"/> là số liệu ở giao điểm.</summary>
public sealed class MatrixSpec
{
    public List<ColumnSpec> Rows { get; set; } = new();
    public ColumnSpec Column { get; set; } = new();
    public List<ColumnSpec> Values { get; set; } = new();
}

/// <summary>Số dư đầu kỳ / cuối kỳ: <see cref="Kind"/> = account | customer | item (hàm FastBusiness$Balance$…). Các cột số dư là ColumnSpec có <see cref="ColumnSpec.Bal"/>.</summary>
public sealed class BalanceSpec
{
    public string Kind { get; set; } = "";
    public bool Opening { get; set; }
    public bool Closing { get; set; }
}

public sealed class ReportSpec
{
    public string Code { get; set; } = "";               // phần tên không gồm tiền tố: "O067" → zrpt_O067 / zrs_O067
    public string FilePrefix { get; set; } = "zrpt_";
    public string ProcPrefix { get; set; } = "zrs_";
    public string TitleVi { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public string Kind { get; set; } = "table";          // table | matrix
    public string Mode { get; set; } = "voucher";        // voucher = bảng chứng từ có phân kỳ (Partition$Execute) | catalog = danh mục / bảng thường
    public string MainTable { get; set; } = "";
    public string MainAlias { get; set; } = "a";
    public string DateField { get; set; } = "ngay_ct";   // cột ngày: lọc kỳ (voucher) / lọc từ-đến ngày
    public bool DateRange { get; set; } = true;          // có ô Từ ngày / Đến ngày
    public bool HasStatus { get; set; } = true;          // bảng chính có cột status → thêm điều kiện status = '1'
    public string UnitColumn { get; set; } = "ma_dvcs"; // cột đơn vị; rỗng = không lọc đơn vị
    public bool Stt { get; set; } = true;
    public List<JoinSpec> Joins { get; set; } = new();
    public List<ColumnSpec> Columns { get; set; } = new();
    public List<FilterSpec> Filters { get; set; } = new();
    public MatrixSpec? Matrix { get; set; }
    public BalanceSpec? Balance { get; set; }
    /// <summary>Chỉ dùng nội bộ khi sinh procedure: cột của bảng phân kỳ nối thêm được kéo lên bước 1 dưới tên <c>alias_cột</c>.</summary>
    [System.Text.Json.Serialization.JsonIgnore] public List<(string Alias, string Col, string Out)> PartRaw { get; set; } = new();

    // ---- tên suy ra ----
    private static string Strip(string code, params string[] prefixes)
    {
        foreach (var p in prefixes) if (code.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return code[p.Length..];
        return code;
    }
    public string CoreCode => Strip((Code ?? "").Trim(), FilePrefix, ProcPrefix);
    public string Controller => FilePrefix + CoreCode;
    public string ProcName => ProcPrefix + CoreCode;
    public string MainFile => FilePrefix + CoreCode;     // Main/<MainFile>.aspx
    public bool IsMatrix => string.Equals(Kind, "matrix", StringComparison.OrdinalIgnoreCase) && Matrix is not null;
}
