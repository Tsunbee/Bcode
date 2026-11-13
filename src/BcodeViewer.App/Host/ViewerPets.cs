using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;

namespace BcodeViewer.App.Host;

/// <summary>
/// Thú cưng trên thanh menu của BcodeViewer (Web/viewerpets.js + engine statuspets.js link từ Bcode.App). Cài đặt lấy NGUYÊN mục "StatusPets"
/// trong %AppData%\Bcode\settings.json — đổi ở Bcode (Giao diện (Template) → Theme & màu → Thú cưng...) là Viewer theo, Viewer không ghi gì. Bcode tắt thú cưng thì Viewer cũng tắt.
/// Môi trường tính ở đây: project đang mở có phải DB thật của khách, mùa / giờ, tạm ẩn khi đang chia sẻ màn hình (dò tiến trình), mẹo của Viewer.
/// Không có: phản ứng chạy SQL, nhắc nghỉ (đó là việc của Bcode).
/// </summary>
internal static class ViewerPets
{
    private static readonly object Gate = new();
    private static (DateTime WriteUtc, long Length, StatusPetSettings Value)? _cache;

    private static readonly JsonSerializerOptions Camel = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly string[] Tips =
    {
        "Ctrl+P: mở nhanh file theo tên.",
        "Ctrl+G: Go to — nhảy tới TAG / DETAIL trong file controller.",
        "F12 trên entity / field: tới chỗ khai báo; Alt+F12: xem tại chỗ (peek).",
        "Alt+P: xem trước màn hình Dir / mail mẫu của file đang mở.",
        "Mỗi lần Ctrl+S có lưu lịch sử cục bộ — lỡ tay ghi đè thì mở History lấy lại bản cũ.",
        "Đổi thú cưng (loài, tên, mùa…) ở Bcode: Giao diện (Template) → Theme & màu → Thú cưng — Viewer tự theo.",
    };

    private static string SettingsPath => Path.Combine(BcodePaths.AppData, "Bcode", "settings.json");

    /// <summary>JSON {settings, env} cho viewerpets.js. Settings trống / lỗi đọc → thú cưng tắt.</summary>
    public static string Json()
    {
        var s = Load();
        var now = DateTime.Now;
        var env = new StatusPetEnv
        {
            Warn = s.IsWarnWorkspace(WorkspaceConnection.CurrentProject),
            Season = s.EffectiveSeason(now),
            Night = StatusPetEnv.IsNight(now),
            Hidden = s.Enabled && s.HideOnShare && AnyRunning(s.ShareAppList()),
            Tips = Tips,
        };
        return JsonSerializer.Serialize(new { settings = s, env }, Camel);
    }

    private static StatusPetSettings Load()
    {
        try
        {
            var info = new FileInfo(SettingsPath);
            if (!info.Exists) return new StatusPetSettings();
            lock (Gate)
            {
                if (_cache is { } c && c.WriteUtc == info.LastWriteTimeUtc && c.Length == info.Length) return c.Value;
                using var doc = JsonDocument.Parse(File.ReadAllText(info.FullName));
                var s = doc.RootElement.TryGetProperty("StatusPets", out var el) && el.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<StatusPetSettings>(el.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()
                    : new StatusPetSettings();
                s.Normalize();
                _cache = (info.LastWriteTimeUtc, info.Length, s);
                return s;
            }
        }
        catch
        {
            return new StatusPetSettings();   // file đang được Bcode ghi dở / hỏng — lần hỏi sau (10 giây) đọc lại
        }
    }

    private static bool AnyRunning(IEnumerable<string> names)
    {
        foreach (var n in names)
        {
            try
            {
                var ps = Process.GetProcessesByName(n);
                var found = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                if (found) return true;
            }
            catch { /* không đọc được danh sách tiến trình */ }
        }
        return false;
    }
}
