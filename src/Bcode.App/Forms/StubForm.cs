using Bcode.App.Controls;
using Bcode.App.UI;
namespace Bcode.App.Forms;

/// <summary>
/// Shared shell for tools that are intentionally left as a scaffold rather than
/// a full implementation in this first cut of Bcode — each still gets a real
/// Form + a clear description of what to wire up, instead of just being a
/// disabled toolbar button.
/// </summary>
public class StubForm : Bcode.App.UI.ThemedForm
{
    protected readonly TextBox NotesBox;

    public StubForm(string title, string whatItDoesInFCode, string whatToImplement)
    {
        Text = title;
        Width = 640;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new Label
        {
            Text = $"Trong FCode: {whatItDoesInFCode}",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 60,
            Font = new Font(Font, FontStyle.Bold)
        };

        var subHeader = new Label
        {
            Text = "Cần bổ sung để Bcode làm được việc này:",
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = 24
        };

        NotesBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Text = whatToImplement,
            BackColor = AppColors.Input
        };

        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(subHeader, 0, 1);
        layout.Controls.Add(NotesBox, 0, 2);

        Controls.Add(layout);
    }
}

public class CreateProcessingForm : StubForm
{
    public CreateProcessingForm() : base(
        "Create Processing",
        "sinh bộ file xử lý nghiệp vụ (controller .f + view .aspx) từ template, dựa trên tên bảng/luồng người dùng chọn.",
        "1) Định nghĩa bộ template của riêng bạn (có thể copy cấu trúc thư mục Config/fileSource/CreateProcessing làm điểm khởi đầu, nhưng viết nội dung template mới).\n" +
        "2) Viết TemplateEngine đơn giản: đọc file .xml/.f mẫu, thay các placeholder (${TableName}, ${ControllerName}...) bằng giá trị người dùng nhập.\n" +
        "3) Ghi file kết quả ra đúng vị trí trong Controllers/ theo SourceRootPath của Workspace hiện tại (dùng ScriptFileService.WriteFile).\n" +
        "4) Optionally: gọi WCommandService để tự thêm dòng vào bảng wcommand cho menu mới."
    )
    { }
}

public class CheckMailForm : StubForm
{
    public CheckMailForm() : base(
        "Check Mail",
        "kiểm tra cấu hình / gửi thử email (ví dụ email thông báo hoá đơn điện tử, cảnh báo hệ thống).",
        "1) Thêm cấu hình SMTP (host, port, user, password, SSL) vào AppSettings.\n" +
        "2) Dùng System.Net.Mail.SmtpClient (hoặc MailKit nếu cần TLS hiện đại) để gửi mail test.\n" +
        "3) Hiển thị log kết quả gửi (thành công / lỗi xác thực / lỗi kết nối) trong form này."
    )
    { }
}

// DecryptSqlObjectForm đã được thay bằng bản THẬT (kết nối DAC + sys.sysobjvalues qua
// engine SqlDecryptor.Core, kỹ thuật known-plaintext hợp lệ trên object của chính bạn)
// — xem Forms/DecryptSqlObjectForm.cs.


public class ViewRptInFecForm : StubForm
{
    public ViewRptInFecForm() : base(
        "View Rpt in FEC",
        "mở nhanh 1 file .rpt (Crystal Reports) trong công cụ xem báo cáo ngoài (FEC).",
        "1) Đây chỉ là việc gọi 1 ứng dụng ngoài — dùng System.Diagnostics.Process.Start(path, arguments).\n" +
        "2) Thêm đường dẫn ứng dụng xem report vào AppSettings (tương tự VSAppPath).\n" +
        "3) Nếu không có Crystal Reports Runtime, cân nhắc đổi định dạng report sang thứ mở được miễn phí, " +
        "ví dụ xuất PDF bằng QuestPDF/EPPlus thay vì .rpt."
    )
    { }
}
