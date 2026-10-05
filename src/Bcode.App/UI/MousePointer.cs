using System.Runtime.InteropServices;

namespace Bcode.App.UI;

/// <summary>
/// Windows có tuỳ chọn "Hide pointer while typing" (Mouse → Pointer Options): gõ phím thì con trỏ CHUỘT biến mất cho tới khi di chuột — do hệ điều
/// hành làm nên cả editor Monaco (WebView2) lẫn ô nhập WinForms đều bị. Bật <see cref="Apply"/> với keepVisible = true để Bcode giữ con trỏ chuột
/// luôn hiện: tắt tuỳ chọn đó trong lúc Bcode chạy (chỉ trong phiên đăng nhập hiện tại, không ghi vào registry) và trả về như cũ khi thoát
/// (<see cref="Restore"/>). Điều khiển bằng UiTemplate.KeepMousePointer (Template giao diện → tab Cửa sổ).
/// </summary>
internal static class MousePointer
{
    private const uint SPI_GETMOUSEVANISH = 0x1020, SPI_SETMOUSEVANISH = 0x1021;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    private static bool? _original; // giá trị của hệ thống trước khi Bcode đổi (null = chưa đổi)

    public static void Apply(bool keepVisible)
    {
        try
        {
            if (keepVisible)
            {
                if (_original is null)
                {
                    var current = 0;
                    if (!SystemParametersInfo(SPI_GETMOUSEVANISH, 0, ref current, 0)) return;
                    _original = current != 0;
                }
                SystemParametersInfo(SPI_SETMOUSEVANISH, 0, IntPtr.Zero, 0); // 0 = không ẩn; winIni = 0: không ghi vào hồ sơ người dùng
            }
            else Restore();
        }
        catch { /* không đổi được thì thôi — chỉ là tiện ích hiển thị */ }
    }

    /// <summary>Trả tuỳ chọn của Windows về đúng giá trị trước khi Bcode đổi.</summary>
    public static void Restore()
    {
        try
        {
            if (_original is { } original)
            {
                SystemParametersInfo(SPI_SETMOUSEVANISH, 0, original ? (IntPtr)1 : IntPtr.Zero, 0);
                _original = null;
            }
        }
        catch { }
    }
}
