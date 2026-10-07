using ClosedXML.Excel;

namespace Bcode.App.Services.Rpt;

/// <summary>Ghi file Excel mẫu (?h_xxx, !2.field, #biểu thức...) từ bố cục kéo thả. Nội dung ô giữ nguyên là chuỗi — engine báo cáo
/// của FastBusiness tự thay biến khi chạy; ở đây chỉ ghi chữ + định dạng theo "loại ô" (<see cref="SheetCell.K"/>), lấy tông giống các
/// mẫu Excel của Fast (zrpt_*, rpt*): Times New Roman 10, tiêu đề 16 đậm, header nền xanh nhạt #EDF5FF có viền mảnh, dòng dữ liệu viền
/// trái/phải/dưới, số dạng <c>#,##0</c>, ngày <c>dd/mm/yyyy</c>, khối chữ ký cỡ 9.</summary>
public class ExcelTemplateWriter
{
    private const string FontName = "Times New Roman";
    private const string NumFormat = "_-* #,##0_-;\\-* #,##0_-;_-* \" - \"_-;_-@_-";
    private const string DateFormat = "dd/mm/yyyy";
    private static readonly XLColor HeaderFill = XLColor.FromHtml("#EDF5FF");

    public void Write(SheetLayout layout, string filePath, string sheetName = "Main")
    {
        using var wb = new XLWorkbook();
        WriteSheet(wb.Worksheets.Add(sheetName), layout);
        wb.SaveAs(filePath);
    }

    /// <summary>Ghi bố cục (cột, dòng, merge, ô, trang in) lên 1 sheet có sẵn — dùng chung cho file thường và sheet "Main - Pivot".</summary>
    public void WriteSheet(IXLWorksheet ws, SheetLayout layout)
    {
        ws.Style.Font.FontName = FontName;     // cả sheet dùng Times New Roman 10 như các mẫu Fast
        ws.Style.Font.FontSize = 10;

        for (int i = 0; i < layout.ColWidths.Count; i++)
        {
            var col = ws.Column(i + 1);
            col.Width = Math.Max(0.5, layout.ColWidths[i]);
            if (i < layout.ColHidden.Count && layout.ColHidden[i]) col.Hide();
        }
        foreach (var r in layout.Rows)
        {
            if (r.H > 0) ws.Row(r.R).Height = r.H;
            if (r.Hidden) ws.Row(r.R).Hide();
        }
        foreach (var m in layout.Merges)
            ws.Range(m[0], m[1], m[2], m[3]).Merge();

        foreach (var c in layout.Cells)
        {
            var cell = ws.Cell(c.R, c.C);
            if (c.N && double.TryParse(c.V, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var num)) cell.SetValue(num);
            else if (c.F) cell.FormulaA1 = c.V.TrimStart('=');
            else if (c.V.Length > 0) cell.SetValue(c.V);
            Style(cell, c.K);
            if (layout.BoldComment && c.R == layout.DataRow && (c.K is "data" or "dataN" or "dataD"))
                cell.CreateComment().AddText("#b:systotal=0");
        }

        ws.PageSetup.PageOrientation = layout.Landscape ? XLPageOrientation.Landscape : XLPageOrientation.Portrait;
    }

    private static void DataBorder(IXLStyle s)
    {
        s.Border.LeftBorder = XLBorderStyleValues.Thin; s.Border.RightBorder = XLBorderStyleValues.Thin; s.Border.BottomBorder = XLBorderStyleValues.Thin;
        s.Alignment.WrapText = true;
    }

    private static void Style(IXLCell cell, string kind)
    {
        var s = cell.Style;
        switch (kind)
        {
            case "entity1": s.Font.Bold = true; break;                                                                     // dòng đầu tên đơn vị: đậm
            case "title": s.Font.Bold = true; s.Font.FontSize = 16; s.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; break;
            case "sub": s.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; break;
            case "hdr":
            case "hdrN":
                s.Font.Bold = true; s.Fill.BackgroundColor = HeaderFill;
                s.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; s.Alignment.Vertical = XLAlignmentVerticalValues.Center; s.Alignment.WrapText = true;
                s.Border.OutsideBorder = XLBorderStyleValues.Thin; break;
            case "data": DataBorder(s); break;
            case "dataN": DataBorder(s); s.Alignment.Horizontal = XLAlignmentHorizontalValues.Right; s.NumberFormat.Format = NumFormat; break;
            case "dataD": DataBorder(s); s.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; s.NumberFormat.Format = DateFormat; break;
            case "total": s.Font.Bold = true; s.Border.TopBorder = XLBorderStyleValues.Thin; s.Alignment.Horizontal = XLAlignmentHorizontalValues.Right; s.NumberFormat.Format = NumFormat; break;
            case "sig": s.Font.Bold = true; s.Font.FontSize = 9; s.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; break;
            case "sigName": s.Font.Italic = true; s.Font.FontSize = 9; s.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; break;
        }
    }
}
