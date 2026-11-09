using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Bcode.App.UI;

/// <summary>
/// Đọc chữ trong clipboard chịu được lúc clipboard đang bận — dùng cho Dán (Ctrl+V / Shift+Insert / menu Paste) của editor SQL Query.
/// Chữ copy ở máy remote (Remote Desktop, UltraViewer, TeamViewer…) tới máy này theo kiểu "hứa trước, đưa sau" (delayed rendering):
/// lúc copy chỉ báo có chữ, khi có app đọc mới kéo dữ liệu qua mạng — đoạn dài mất vài giây. Trong lúc đó app nào đang đọc
/// (lịch sử clipboard Win+V, UltraViewer… đọc ngay khi clipboard đổi) sẽ giữ clipboard mở, nên:
/// Scintilla (chế độ App) chỉ thử mở clipboard ~5 ms rồi bỏ → Ctrl+V không ra gì; WebView2 (chế độ Web) trả chữ rỗng cho sự kiện paste.
/// Ở đây thử mở lại tới khi được (tối đa <paramref name="timeoutMs"/>), đọc CF_UNICODETEXT (Windows tự đổi từ CF_TEXT / CF_OEMTEXT nếu nguồn chỉ có ANSI).
/// Chạy được ở luồng nền (API Win32 thuần, không cần STA) — gọi <see cref="ReadAsync"/> để giao diện không đứng trong lúc chờ.
/// </summary>
internal static class ClipboardText
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);

    private const uint CF_TEXT = 1, CF_OEMTEXT = 7, CF_UNICODETEXT = 13;

    /// <summary>Clipboard có chữ không — chỉ hỏi danh sách định dạng, không mở clipboard nên không lỗi / không chờ khi clipboard đang bận.</summary>
    public static bool HasText()
    {
        try { return IsClipboardFormatAvailable(CF_UNICODETEXT) || IsClipboardFormatAvailable(CF_TEXT) || IsClipboardFormatAvailable(CF_OEMTEXT); }
        catch { return false; }
    }

    /// <summary>Đọc chữ ở luồng nền; null nếu clipboard không có chữ hoặc quá <paramref name="timeoutMs"/> vẫn không mở được.</summary>
    public static Task<string?> ReadAsync(int timeoutMs = 20000) => Task.Run(() => Read(timeoutMs));

    public static string? Read(int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (HasText())
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    var h = GetClipboardData(CF_UNICODETEXT);   // nguồn "hứa trước" thì dòng này chờ tới khi dữ liệu về
                    if (h == IntPtr.Zero) return null;
                    var p = GlobalLock(h);
                    if (p == IntPtr.Zero) return null;
                    try
                    {
                        var chars = (int)Math.Min((ulong)GlobalSize(h) / 2, int.MaxValue);
                        var s = Marshal.PtrToStringUni(p, chars);
                        var z = s.IndexOf('\0');
                        return z >= 0 ? s[..z] : s;
                    }
                    finally { GlobalUnlock(h); }
                }
                catch { return null; }
                finally { CloseClipboard(); }
            }
            if (sw.ElapsedMilliseconds > timeoutMs) return null;
            Thread.Sleep(25);
        }
        return null;
    }
}
