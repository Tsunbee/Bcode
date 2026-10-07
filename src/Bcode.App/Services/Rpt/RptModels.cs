namespace Bcode.App.Services.Rpt;

/// <summary>Một cột của result set profiler. <see cref="IsNum"/> = kiểu số (tick sẵn cột isNum ở bước Choose Fields).</summary>
public record RptColumn(string Name, bool IsNum, bool IsDate = false);

/// <summary>Một result set của profiler: "d" (tham số/bộ lọc, !1.), "d1" (dữ liệu, !2.), "d2"... Index = số N trong <c>!N.field</c>.</summary>
/// <summary>Rows = tối đa <see cref="ReportProfilerService.SampleRows"/> dòng đầu (đã đổi sang chuỗi; ngày dạng yyyy-MM-dd) để trình thiết kế vẽ sơ đồ bằng dữ liệu thật.</summary>
public record RptTable(string Name, int Index, List<RptColumn> Columns, int RowCount, List<string?[]>? Rows = null);

/// <summary>Field khai báo trong Grid controller (Grid\&lt;name&gt;.xml): header Việt/Anh + độ rộng px.</summary>
public record GridField(string Name, int Width, string Type, string HeaderV, string HeaderE, string Aggregate = "", bool Hidden = false);

/// <summary>Khai báo &lt;pivot rowField columnField dataFields .../&gt; trong Grid controller của báo cáo pivot.</summary>
public record PivotInfo(string RowField, string ColumnField, List<string> DataFields);

public record ControllerInfo(string Controller, string Title, string TitleE, List<GridField> GridFields, Dictionary<string, (string V, string E)> FilterHeaders,
    string? GridPath, string? ReportPath, string? Note, PivotInfo? Pivot = null, List<string>? ViewFields = null);

/// <summary>Một biến h_xxx trong Report xml. <see cref="Exists"/> = đã có sẵn trong file report (giữ nguyên, không sinh lại);
/// <see cref="Edited"/> = có sẵn nhưng người dùng sửa header ở bước thiết kế → sinh lại đúng khối đó.</summary>
public class RptVar
{
    public string Code { get; set; } = "";
    public string V { get; set; } = "";
    public string E { get; set; } = "";
    public bool Exists { get; set; }
    public bool Edited { get; set; }
}

/// <summary>Vai trò 1 field trong báo cáo pivot (xem PivotXlsxWriter).</summary>
public static class PivotRole { public const string Row = "row", RowKey = "rowkey", ColKey = "colkey", ColHeader = "colheader", Data = "data"; }

public class PivotField
{
    public string Name { get; set; } = "";       // tên cột ở dòng 1 sheet "Main" (cũng là tên field của PivotTable)
    public string Expr { get; set; } = "";       // nội dung dòng 2, vd !2.sysRow
    public string Role { get; set; } = PivotRole.Row;
    public bool IsDate { get; set; }             // cột kiểu ngày: nhãn dòng của pivot được định dạng short date
    public string DataName { get; set; } = "";   // chỉ với Data: tên hiển thị của data field (vd "Sum of gt_kh_ky" hoặc ?p_th)
}

/// <summary>Cấu hình PivotTable của file Excel pivot; vị trí (Location*) do trình thiết kế tính sẵn theo đúng quy tắc của các mẫu Fast.</summary>
public class PivotSpec
{
    public List<PivotField> Fields { get; set; } = new();   // theo thứ tự cột của sheet "Main"
    public int TopRow { get; set; } = 9;
    public string LocationRef { get; set; } = "";
    public int FirstDataRow { get; set; }
    public int FirstDataCol { get; set; }
    public string DataCaption { get; set; } = " ";
}

/// <summary>Sheet ẩn phụ (Main2, Main3...) liệt kê các biểu thức <c>!N.xxx</c> của bảng phụ ở dòng 1.</summary>
public class ExtraSheet { public string Name { get; set; } = ""; public List<string> Exprs { get; set; } = new(); }

public class SheetCell { public int R { get; set; } public int C { get; set; } public string V { get; set; } = ""; public string K { get; set; } = "plain"; public bool F { get; set; } public bool N { get; set; } }
public class SheetRow { public int R { get; set; } public double H { get; set; } public bool Hidden { get; set; } }

/// <summary>Bố cục sheet do trình thiết kế kéo thả gửi xuống (toạ độ 1-based như Excel; <see cref="ColWidths"/> tính theo ký tự Excel).</summary>
public class SheetLayout
{
    public List<double> ColWidths { get; set; } = new();
    public List<bool> ColHidden { get; set; } = new();
    public List<SheetRow> Rows { get; set; } = new();
    public List<SheetCell> Cells { get; set; } = new();
    public List<int[]> Merges { get; set; } = new();   // [r1,c1,r2,c2]
    public bool Landscape { get; set; }
    public bool BoldComment { get; set; }
    public PivotSpec? Pivot { get; set; }
    public List<ExtraSheet> ExtraSheets { get; set; } = new();   // sheet ẩn phụ của báo cáo pivot có dùng bảng !3., !4....
    public int DataRow { get; set; }
}
