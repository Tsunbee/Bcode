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
    /// <summary>Cột công thức tính trên các cột kết quả, vd "[du_no_dk] + [ps_no] - [ps_co]". Rỗng = cột thường.</summary>
    public string Formula { get; set; } = "";
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
    /// <summary>Giá trị mặc định của ô lọc khi mở form (rỗng = không đặt). Ô chữ / tra cứu: chuỗi; Numeric: số; DropDownList: giá trị của 1 mục (xem <see cref="Items"/>).</summary>
    public string Default { get; set; } = "";
    public List<ComboItem> Items { get; set; } = new();  // với DropDownList
}

public sealed class ComboItem { public string Value { get; set; } = ""; public string Vi { get; set; } = ""; public string En { get; set; } = ""; }

/// <summary>Báo cáo pivot (ma trận): các cột <see cref="Rows"/> là nhãn dòng, <see cref="Column"/> là chiều ngang, <see cref="Values"/> là số liệu ở giao điểm.</summary>
public sealed class MatrixSpec
{
    public List<ColumnSpec> Rows { get; set; } = new();
    public ColumnSpec Column { get; set; } = new();
    public List<ColumnSpec> Values { get; set; } = new();
    /// <summary>"Xoay theo" do người dùng chọn lúc chạy: mỗi lựa chọn là một cột khác nhau làm chiều ngang (vd Phí / Tài khoản / Bộ phận / Vụ việc). Rỗng = chiều ngang cố định là <see cref="Column"/>.
    /// Khi có lựa chọn, form lọc có ô <see cref="ColumnField"/> và procedure có tham số tương ứng; <see cref="Column"/> chỉ còn là cột mặc định / tiêu đề.</summary>
    public List<PivotOption> ColumnOptions { get; set; } = new();
    public string ColumnField { get; set; } = "xoay_theo";
    public string ColumnHeaderVi { get; set; } = "Xoay theo";
    public string ColumnHeaderEn { get; set; } = "Pivot by";
    /// <summary>Giá trị (<see cref="PivotOption.Value"/>) chọn sẵn; rỗng = lựa chọn đầu tiên.</summary>
    public string ColumnDefault { get; set; } = "";
    public bool HasColumnOptions => ColumnOptions.Count > 0;
}

/// <summary>Một lựa chọn "Xoay theo": giá trị gửi từ form lọc, chữ hiển thị song ngữ và cột dùng làm chiều ngang.</summary>
public sealed class PivotOption
{
    public string Value { get; set; } = "";
    public string Vi { get; set; } = "";
    public string En { get; set; } = "";
    public ColumnSpec Column { get; set; } = new();
}

/// <summary>Một lựa chọn "Nhóm theo" lúc chạy: nhóm 1 cấp theo cột <see cref="Column"/> (tên cột kết quả), nhãn lấy từ <see cref="Label"/>.</summary>
public sealed class GroupOption
{
    public string Value { get; set; } = "";
    public string Vi { get; set; } = "";
    public string En { get; set; } = "";
    public string Column { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Header { get; set; } = true;
    public bool Subtotal { get; set; } = true;
    public bool HeaderTotals { get; set; }
    public bool HideKey { get; set; }
}

/// <summary>Số dư đầu kỳ / cuối kỳ: <see cref="Kind"/> = account | customer | item (hàm FastBusiness$Balance$…). Các cột số dư là ColumnSpec có <see cref="ColumnSpec.Bal"/>.</summary>
public sealed class BalanceSpec
{
    public string Kind { get; set; } = "";
    public bool Opening { get; set; }
    public bool Closing { get; set; }
}

/// <summary>
/// Một cấp nhóm của báo cáo dạng bảng. Các cấp xếp từ ngoài vào trong (cấp 1 = nhóm lớn nhất), vd nhóm theo mã phí rồi theo khách hàng.
/// Procedure thêm dòng tiêu đề nhóm (sysorder = 4) và dòng cộng nhóm (sysorder = 6), cả hai có systotal = 0 để lưới in đậm; dòng chi tiết giữ sysorder = 5, systotal = 1.
/// </summary>
public sealed class GroupSpec
{
    /// <summary>Tên cột kết quả (<see cref="ColumnSpec.Name"/>) làm khoá nhóm.</summary>
    public string Column { get; set; } = "";
    /// <summary>Tên cột kết quả hiện nhãn của nhóm (vd tên phí, tên khách); rỗng = chỉ hiện khoá.</summary>
    public string Label { get; set; } = "";
    /// <summary>Thêm dòng tiêu đề đầu mỗi nhóm.</summary>
    public bool Header { get; set; } = true;
    /// <summary>Thêm dòng cộng ở cuối mỗi nhóm (cộng các cột số liệu / số dư).</summary>
    public bool Subtotal { get; set; } = true;
    /// <summary>Dòng tiêu đề nhóm mang luôn số liệu của nhóm (kiểu: dòng 1 là mã khách A kèm tổng, các dòng dưới là chi tiết) — thường dùng thay cho dòng cộng.</summary>
    public bool HeaderTotals { get; set; }
    /// <summary>Dòng chi tiết để trống cột khoá / nhãn của nhóm (không lặp lại mã khách ở từng dòng) — mã chỉ hiện ở dòng tiêu đề.</summary>
    public bool HideKey { get; set; }
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
    /// <summary>Ngày mặc định của ô "Từ ngày" / "Đến ngày": today | monthStart | monthEnd | yearStart | yearEnd | prevMonthStart | prevMonthEnd | yyyy-MM-dd (rỗng = hôm nay).</summary>
    public string DateFromDefault { get; set; } = "";
    public string DateToDefault { get; set; } = "";
    /// <summary>Nhóm nhiều cấp (chỉ báo cáo dạng bảng, không áp dụng cho pivot). Rỗng = không nhóm.</summary>
    public List<GroupSpec> Groups { get; set; } = new();
    /// <summary>Thêm dòng "Tổng cộng" cuối báo cáo khi có nhóm.</summary>
    public bool GroupGrandTotal { get; set; }
    /// <summary>Hiện dạng CÂY: thêm cột <c>noi_dung</c> (một cột, thụt lề 4 khoảng trắng theo cấp) — dòng tên nhóm, dòng cộng và dòng chi tiết đều thụt theo cấp của chúng.</summary>
    public bool GroupTree { get; set; }
    /// <summary>"Nhóm theo" do người dùng chọn lúc chạy (ô lọc <see cref="GroupField"/>, thêm mục "Không nhóm"): mỗi lựa chọn nhóm 1 cấp theo một cột khác nhau. Không dùng chung với <see cref="Groups"/>.</summary>
    public List<GroupOption> GroupOptions { get; set; } = new();
    public string GroupField { get; set; } = "nhom_theo";
    public string GroupHeaderVi { get; set; } = "Nhóm theo";
    public string GroupHeaderEn { get; set; } = "Group by";
    public string GroupNoneVi { get; set; } = "Không nhóm";
    public string GroupNoneEn { get; set; } = "No grouping";
    /// <summary>Giá trị chọn sẵn của ô "Nhóm theo": "0" = Không nhóm, hoặc <see cref="GroupOption.Value"/>.</summary>
    public string GroupDefault { get; set; } = "0";
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
    /// <summary>Khi chỉnh báo cáo CÓ SẴN và muốn ghi đè đúng tên gốc: tên controller / procedure / trang Main của báo cáo gốc (rỗng = tự đặt theo mã + tiền tố).</summary>
    public string ControllerOverride { get; set; } = "";
    public string ProcNameOverride { get; set; } = "";
    public string MainFileOverride { get; set; } = "";
    /// <summary>Thêm lệnh xoá procedure cũ (nếu có) trước CREATE — dùng khi thay thế procedure của báo cáo có sẵn.</summary>
    public bool DropIfExists { get; set; }
    public string Controller => string.IsNullOrWhiteSpace(ControllerOverride) ? FilePrefix + CoreCode : ControllerOverride.Trim();
    public string ProcName => string.IsNullOrWhiteSpace(ProcNameOverride) ? ProcPrefix + CoreCode : ProcNameOverride.Trim();
    public string MainFile => string.IsNullOrWhiteSpace(MainFileOverride) ? FilePrefix + CoreCode : MainFileOverride.Trim();     // Main/<MainFile>.aspx
    public bool HasGroups => !IsMatrix && Groups.Count > 0;
    public bool HasDynGroup => !IsMatrix && Groups.Count == 0 && GroupOptions.Count > 0;
    public bool HasPivotOptions => IsMatrix && Matrix!.HasColumnOptions;
    public bool IsMatrix => string.Equals(Kind, "matrix", StringComparison.OrdinalIgnoreCase) && Matrix is not null;
}
