using System.Diagnostics;

namespace Bcode.Shared;

/// <summary>
/// Dọn thư mục hồ sơ WebView2 theo tiến trình (<c>%TEMP%\&lt;App&gt;.WebView2\&lt;pid&gt;</c>) của các lần chạy đã thoát.
/// Xem chung (Watch), Giải lao (Game), Screen Designer cố ý dùng 1 hồ sơ / tiến trình (chạy nhiều bản cùng lúc để thử nhiều người trên 1 máy,
/// mỗi bản localStorage riêng) nhưng trước đây không bao giờ xoá: đo 14/11/2026 có 41 thư mục / 1,2 GB (Watch) và 24 / 323 MB (Game).
/// Chạy ở luồng nền, bỏ qua thư mục của tiến trình còn sống và thư mục đang bị khoá (trình duyệt cũ chưa thoát hẳn — lần mở sau xoá tiếp).
/// </summary>
public static class WebViewProfileCleanup
{
    public static void DeleteStaleInBackground(string root)
    {
        var self = Environment.ProcessId;
        _ = Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    if (!int.TryParse(Path.GetFileName(dir), out var pid) || pid == self || IsAlive(pid)) continue;
                    try { Directory.Delete(dir, recursive: true); }
                    catch { /* file còn bị giữ — để lần sau */ }
                }
            }
            catch { /* thư mục tạm không đọc được — bỏ qua */ }
        });
    }

    private static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }   // không còn tiến trình này
    }
}
