namespace Bcode.App.UI;

/// <summary>
/// Hệ số co giãn giao diện (chữ + độ rộng/cao nội dung) để cùng một bố cục vừa khung ở mọi cỡ màn hình.
/// Tự động: so kích thước LOGIC của vùng làm việc (px thật chia tỉ lệ DPI của Windows) với khung thiết kế
/// <see cref="DesignWidth"/>×<see cref="DesignHeight"/>, lấy cạnh chặt hơn rồi chặn trong [<see cref="MinFactor"/>, <see cref="MaxFactor"/>].
/// Người dùng cũng chọn tay được một mức cố định (xem <see cref="Options"/>, lưu ở AppSettings.UiScale).
/// Áp vào: ZoomFactor của mọi WebView2 (<see cref="BindZoom"/>) và font gốc WinForms (ThemeManager.BaseFont).
/// </summary>
public static class UiScale
{
    public const double DesignWidth = 1600, DesignHeight = 900;
    public const double MinFactor = 0.75, MaxFactor = 1.25;
    public const string Auto = "Auto";

    /// <summary>Các mức người dùng chọn tay: "Auto" hoặc phần trăm.</summary>
    public static readonly string[] Options = { Auto, "80", "90", "100", "110", "125", "150" };

    public static string Mode { get; private set; } = Auto;
    public static double Factor { get; private set; } = 1.0;
    public static event Action? Changed;

    /// <summary>Đặt chế độ ("Auto" hoặc số phần trăm) rồi tính lại theo màn hình đang chứa <paramref name="host"/>.</summary>
    public static void SetMode(string? mode, Control host)
    {
        Mode = string.IsNullOrWhiteSpace(mode) ? Auto : mode.Trim();
        Update(host);
    }

    /// <summary>Tính lại hệ số (gọi khi mở form, đổi DPI, đổi kích thước/chuyển màn hình); chỉ báo <see cref="Changed"/> khi giá trị đổi.</summary>
    public static void Update(Control host)
    {
        double f;
        if (!Mode.Equals(Auto, StringComparison.OrdinalIgnoreCase) && double.TryParse(Mode, out var pct) && pct > 0)
            f = Math.Clamp(pct / 100.0, 0.5, 2.0);
        else
        {
            var wa = Screen.FromControl(host).WorkingArea;
            var dpi = Math.Max(1, host.DeviceDpi) / 96.0;
            f = Math.Min(wa.Width / dpi / DesignWidth, wa.Height / dpi / DesignHeight);
            f = Math.Round(Math.Clamp(f, MinFactor, MaxFactor) * 20) / 20; // bước 5% — tránh nhấp nhổm vì lệch vài px
        }
        if (Math.Abs(f - Factor) < 0.001) return;
        Factor = f;
        Changed?.Invoke();
    }

    /// <summary>Gắn ZoomFactor của <paramref name="web"/> theo hệ số hiện tại và theo mọi lần đổi sau đó.</summary>
    public static void BindZoom(Microsoft.Web.WebView2.WinForms.WebView2 web)
    {
        void Apply()
        {
            try { if (!web.IsDisposed) web.ZoomFactor = Factor; } catch { /* WebView2 chưa sẵn sàng/đang đóng */ }
        }
        Apply();
        Changed += Apply;
        web.Disposed += (_, _) => Changed -= Apply;
        UiTemplate.BindWeb(web); // cùng nơi khởi tạo WebView2: gắn luôn biến CSS của template giao diện
    }
}
