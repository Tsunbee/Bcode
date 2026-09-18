using System.Data;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Create *.rpt, *.xlsx file" tool. The .xlsx half is fully functional
/// (exports the given DataTable via ClosedXML). The .rpt (Crystal Reports)
/// half is a stub — Crystal Reports is a commercial, closed report format
/// and its .rpt structure isn't something to reverse-engineer here.
/// </summary>
public class CreateRptXlsxForm : Bcode.App.UI.ThemedForm
{
    private readonly DataTable? _sourceTable;
    private readonly XlsxExportService _xlsx = new();

    public CreateRptXlsxForm(DataTable? sourceTable)
    {
        _sourceTable = sourceTable;
        Text = "Create *.rpt, *.xlsx file";
        Width = 560;
        Height = 320;
        StartPosition = FormStartPosition.CenterParent;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 4, ColumnCount = 1 };

        var xlsxInfo = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Text = _sourceTable is null
                ? "Chưa có dữ liệu nguồn (chạy 1 truy vấn ở tab SQL Object trước)."
                : $"Sẵn sàng xuất {_sourceTable.Rows.Count} dòng × {_sourceTable.Columns.Count} cột ra .xlsx."
        };

        var xlsxBtn = new Button { Text = "Xuất .xlsx...", Dock = DockStyle.Top, Height = 32, Enabled = _sourceTable is not null };
        xlsxBtn.Click += (_, _) => ExportXlsx();

        var rptInfo = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = "Xuất .rpt (Crystal Reports) chưa được cài đặt trong Bcode:\n\n" +
                   "Định dạng .rpt là định dạng đóng, thuộc sở hữu của SAP Crystal Reports, và FCode dùng SDK Crystal Reports " +
                   "(license riêng) để sinh/đọc file này — Bcode không sao chép logic đó.\n\n" +
                   "Lựa chọn thay thế miễn phí: sinh báo cáo PDF trực tiếp bằng thư viện mã nguồn mở (ví dụ QuestPDF), " +
                   "dùng chung dữ liệu DataTable này làm nguồn."
        };

        layout.Controls.Add(xlsxInfo);
        layout.Controls.Add(xlsxBtn);
        layout.Controls.Add(new Label { Text = "Rpt (.rpt):", Dock = DockStyle.Top, Height = 20, Font = new Font(Font, FontStyle.Bold) });
        layout.Controls.Add(rptInfo);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Controls.Add(layout);
    }

    private void ExportXlsx()
    {
        if (_sourceTable is null) return;

        using var sfd = new SaveFileDialog { Filter = "Excel Workbook (*.xlsx)|*.xlsx", FileName = "export.xlsx" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _xlsx.ExportToXlsx(_sourceTable, sfd.FileName);
            MessageBox.Show(this, "Đã xuất file thành công.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Xuất Excel", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
