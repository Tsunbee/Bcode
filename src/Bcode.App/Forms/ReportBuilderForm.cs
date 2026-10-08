using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Cửa sổ riêng (toàn màn hình, kéo sang màn hình phụ được) cho trình thiết kế "Tạo báo cáo" — giống cách BcodeViewer là một cửa sổ riêng — để có chỗ rộng
/// thiết kế: chọn bảng / trường nằm cố định bên trái, xem trước ở giữa, cách thể hiện ở bên phải. Nội dung là control của thư viện Bcode.ReportBuilder.dll
/// (xem <see cref="Services.ReportBuilderModule"/>); vẫn chạy trong tiến trình Bcode nên dùng chung kết nối workspace, theme và tab SQL.
/// </summary>
public class ReportBuilderForm : ThemedForm
{
    public ReportBuilderForm(Control content, string workspaceName)
    {
        Text = "Tạo báo cáo — " + workspaceName;
        if (AppIcons.AppIcon is { } icon) Icon = icon;
        StartPosition = FormStartPosition.CenterScreen;
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Size = new Size(Math.Min(1700, area.Width - 40), Math.Min(980, area.Height - 40));
        MinimumSize = new Size(1000, 640);
        WindowState = FormWindowState.Maximized;
        content.Dock = DockStyle.Fill;
        Controls.Add(content);
    }
}
