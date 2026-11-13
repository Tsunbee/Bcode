using System.Text.RegularExpressions;
using Bcode.Watch.Net;
using Microsoft.Win32;

namespace Bcode.Watch;

/// <summary>Link mời vào phòng. Chủ phòng / người xem bấm 🔗 Link mời → chép link http://ip:cổng-phát-file/join (trang nhỏ <see cref="MediaServer"/> trả về,
/// dán vào Zalo / Teams / mail đều bấm được). Trên trang có nút mở "bcwatch://join/ip:cổng" → Windows chạy BcodeWatch.exe với link đó → vào thẳng phòng.
/// Dán link (cả 2 dạng) vào ô "IP chủ phòng" cũng được — <see cref="Parse"/>.</summary>
internal static class Invite
{
    public const string Scheme = "bcwatch";

    private static readonly Regex Deep = new(@"bcwatch://join/([A-Za-z0-9.\-]+|\[[0-9A-Fa-f:.]+\])(?::(\d{1,5}))?", RegexOptions.IgnoreCase);
    private static readonly Regex Http = new(@"https?://([A-Za-z0-9.\-]+|\[[0-9A-Fa-f:.]+\])(?::\d{1,5})?/join/?(?:\?(?:[^#\s]*&)?p=(\d{1,5}))?", RegexOptions.IgnoreCase);

    /// <summary>Chữ người dùng dán / tham số dòng lệnh → "ip:cổng" để vào phòng. Không phải link mời thì trả nguyên (IP gõ tay).</summary>
    public static string Parse(string text)
    {
        text = (text ?? "").Trim();
        var m = Deep.Match(text);
        if (m.Success) return m.Groups[1].Value + ":" + (m.Groups[2].Success ? m.Groups[2].Value : Wire.DefaultPort.ToString());
        m = Http.Match(text);
        if (m.Success) return m.Groups[1].Value + ":" + (m.Groups[2].Success ? m.Groups[2].Value : Wire.DefaultPort.ToString());
        return text;
    }

    public static bool IsLink(string? arg) => arg != null && arg.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>Đăng ký giao thức bcwatch:// cho tài khoản Windows này (HKCU\Software\Classes — không cần quyền admin) trỏ tới exe đang chạy.
    /// Chỉ ghi khi chưa có / đổi chỗ để Bcode. Lỗi (chính sách chặn registry…) thì thôi — dán link vào ô IP vẫn vào được.</summary>
    public static void Register()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var cmd = $"\"{exe}\" \"%1\"";
            using var root = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Scheme);
            using var command = root.CreateSubKey(@"shell\open\command");
            if (command.GetValue("") as string == cmd) return;
            root.SetValue("", "URL:Rạp Bcode — Xem chung");
            root.SetValue("URL Protocol", "");
            using (var icon = root.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exe}\",0");
            command.SetValue("", cmd);
        }
        catch { }
    }
}
