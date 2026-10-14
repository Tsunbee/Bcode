using Bcode.App.Forms;
using Bcode.App.UI;

namespace Bcode.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Chế độ giao diện (App/WinForms hay Web/WebView2): tham số --native / --web, nếu không thì hỏi (trừ khi đã tắt "hỏi mỗi lần"). Đóng hộp hỏi = thoát.
        UiThemes.ApplyPalettes();
        if (UiMode.Resolve(args) is not { } mode) return;
        UiMode.Set(mode);
        // Tạo sẵn môi trường WebView2 (khởi động tiến trình trình duyệt nền — phần nặng nhất) ngay bây giờ, chạy ngầm song song với việc
        // đọc cấu hình + dựng MainForm; tới lúc các trang cần thì đã gần xong. Không chờ ở đây; lỗi (thiếu Runtime...) vẫn được báo ở chỗ dùng
        // vì GetAsync không nhớ kết quả lỗi. Chế độ App thì KHÔNG tạo sẵn: môi trường chỉ được dựng khi có công cụ cần WebView2.
        if (!UiMode.IsNative)
            try { _ = WebViewEnvironment.GetAsync(); } catch { /* báo lỗi ở chỗ dùng */ }
        Application.Run(new MainForm());
    }
}
