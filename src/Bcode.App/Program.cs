using Bcode.App.Forms;

namespace Bcode.App;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Tạo sẵn môi trường WebView2 (khởi động tiến trình trình duyệt nền — phần nặng nhất) ngay bây giờ, chạy ngầm song song với việc
        // đọc cấu hình + dựng MainForm; tới lúc các trang cần thì đã gần xong. Không chờ ở đây; lỗi (thiếu Runtime...) vẫn được báo ở chỗ dùng
        // vì GetAsync không nhớ kết quả lỗi.
        try { _ = Bcode.App.UI.WebViewEnvironment.GetAsync(); } catch { /* báo lỗi ở chỗ dùng */ }
        Application.Run(new MainForm());
    }
}
