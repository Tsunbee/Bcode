using System.Data;
using ClosedXML.Excel;

namespace Bcode.App.Services;

/// <summary>
/// "Create *.xlsx file" tool — genuinely functional export of any DataTable
/// (typically a SQL Query / Table result) to an .xlsx workbook, via the
/// open-source ClosedXML library. The ".rpt" (Crystal Reports) half of the
/// original tool is left as a stub — see CreateRptXlsxForm.
/// </summary>
public class XlsxExportService
{
    public void ExportToXlsx(DataTable table, string filePath, string sheetName = "Sheet1")
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(string.IsNullOrWhiteSpace(sheetName) ? "Sheet1" : sheetName);
        sheet.Cell(1, 1).InsertTable(table);
        sheet.Columns().AdjustToContents();
        workbook.SaveAs(filePath);
    }
}
