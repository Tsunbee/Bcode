using Bcode.App.Forms;
using Bcode.App.UI;

namespace Bcode.App;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // ~570 mẫu regex khác nhau gọi qua Regex.IsMatch/Match/Replace(tĩnh); bộ đệm mặc định chỉ 15 mẫu → tràn là dựng lại mẫu
        // mỗi lần gọi (đo: chậm 7–10×, ~6–10 KB rác/lần). Kết quả regex không đổi.
        System.Text.RegularExpressions.Regex.CacheSize = 512;
        ApplicationConfiguration.Initialize();
        // Log lỗi tập trung (error.log trong thư mục dữ liệu) + theo dõi hộp lỗi + phím Gửi lỗi (F1) — trước khi có form nào.
        Bcode.App.Services.ErrorLog.Install();
        Bcode.App.UI.ErrorReporter.Install();
        // Thuỷ ấn truy nguồn (Bcode.Core): cấp "muối" = key gốc bản quyền, rồi ghi dấu bản build vào đầu log (log chia sẻ qua F1 truy được nguồn).
        Bcode.Core.WatermarkService.SaltProvider = () => Bcode.App.Services.LicenseSecret.Key;
        try { Bcode.App.Services.ErrorLog.Note(Bcode.Core.WatermarkService.LogLine); } catch { /* log lỗi không ghi được thì bỏ qua */ }
        // Chế độ giao diện (App/WinForms hay Web/WebView2): tham số --native / --web, nếu không thì hỏi (trừ khi đã tắt "hỏi mỗi lần"). Đóng hộp hỏi = thoát.
        UiThemes.ApplyPalettes();
        // Sáng/tối lần trước (nút mặt trời/mặt trăng ghi theme_light.txt) — đặt trước khi có form nào, kể cả hộp hỏi App/Web. Chưa chọn bao giờ = tối.
        if (Bcode.App.Models.AppSettings.LightTheme) AppColors.Current = AppColors.Light;
        if (UiMode.Resolve(args) is not { } mode) return;
        UiMode.Set(mode);
        StartupArgs.Parse(args);
        // Tạo sẵn môi trường WebView2 (khởi động tiến trình trình duyệt nền — phần nặng nhất) ngay bây giờ, chạy ngầm song song với việc
        // đọc cấu hình + dựng MainForm; tới lúc các trang cần thì đã gần xong. Không chờ ở đây; lỗi (thiếu Runtime...) vẫn được báo ở chỗ dùng
        // vì GetAsync không nhớ kết quả lỗi. Chế độ App thì KHÔNG tạo sẵn: môi trường chỉ được dựng khi có công cụ cần WebView2.
        if (!UiMode.IsNative)
            try { _ = WebViewEnvironment.GetAsync(); } catch { /* báo lỗi ở chỗ dùng */ }
        Application.Run(new MainForm());
    }
}

/// <summary>Tham số dòng lệnh khác ngoài chế độ giao diện. <c>--project &lt;tên workspace&gt;</c>: vào thẳng dự án đó, không hiện màn hình Projects
/// (Ctrl+O chọn dự án khác lúc cửa sổ đang có tab → mở Bcode mới bằng tham số này, xem MainForm.ShowProjectPicker).</summary>
internal static class StartupArgs
{
    public static string? Project { get; private set; }

    public static void Parse(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--project", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args[i + 1])) Project = args[i + 1].Trim();
    }
}
