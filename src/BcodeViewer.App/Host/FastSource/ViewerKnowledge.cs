using System.Text;
using System.Text.Json;

namespace BcodeViewer.App.Host.FastSource;

/// <summary>
/// Thư mục kiến thức: Settings của BcodeViewer → "Thư mục kiến thức chung" (<c>KnowledgeDir</c> của Viewer), trống thì dùng chung với Bcode
/// (Template → Đường dẫn → "Thư mục kiến thức" = <c>KnowledgeDir</c> trong settings.json của Bcode); trống hoặc không với tới được → <c>%AppData%\Bcode</c>. Phần của BcodeViewer nằm trong thư mục con <c>viewer\</c>: ma trận ghost học từ source
/// (<c>viewer\ghost\</c>), lịch sử chỉnh sửa theo phiên bản (<c>viewer\versions\</c>), xác nhận ✔ / ✘ của Hỏi Fast (<c>viewer\ask\feedback.json</c>).
/// Lịch sử chat luôn ở máy này (<c>%AppData%\Bcode\viewer-fastask\</c>). Kiến thức học từ source Fast / dự án KHÔNG nằm trong repo.
/// </summary>
public static class ViewerKnowledge
{
    /// <summary>Đặt khi chạy thử (không ghi vào %AppData% thật).</summary>
    internal static string? _rootOverride;
    private static (string Dir, DateTime At)? _probe;
    /// <summary>Thư mục kiến thức khai ở Settings của BcodeViewer (ưu tiên hơn của Bcode); null / trống = theo Bcode.</summary>
    private static string? _viewerDir;

    /// <summary>Đặt thư mục kiến thức theo Settings của BcodeViewer (gọi lúc mở app và khi lưu Settings).</summary>
    public static void Configure(string? dir)
    {
        _viewerDir = string.IsNullOrWhiteSpace(dir) ? null : dir.Trim();
        _probe = null;
    }

    public static string LocalBase => Path.Combine(BcodePaths.AppData, "Bcode");

    /// <summary>Thư mục kiến thức đang dùng (kiểm tra lại mỗi phút — ổ mạng có lúc mất).</summary>
    public static string Root
    {
        get
        {
            if (_rootOverride is { } o) return o;
            if (_probe is { } p && (DateTime.UtcNow - p.At).TotalSeconds < 60) return p.Dir;
            var dir = LocalBase;
            if (_viewerDir is { } vd)
            {
                try { Directory.CreateDirectory(vd); if (Directory.Exists(vd)) { _probe = (vd, DateTime.UtcNow); return vd; } }
                catch { /* ổ mạng không với tới → theo Bcode / máy này */ }
            }
            try
            {
                var settings = Path.Combine(LocalBase, "settings.json");
                if (File.Exists(settings))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                    if (doc.RootElement.TryGetProperty("KnowledgeDir", out var k) && k.GetString() is { Length: > 0 } custom)
                    {
                        Directory.CreateDirectory(custom.Trim());
                        if (Directory.Exists(custom.Trim())) dir = custom.Trim();
                    }
                }
            }
            catch { /* settings hỏng / ổ mạng không với tới → máy này */ }
            _probe = (dir, DateTime.UtcNow);
            return dir;
        }
    }

    public static string Viewer(params string[] parts) => Path.Combine(new[] { Root, "viewer" }.Concat(parts).ToArray());

    /// <summary>Ghi JSON giữ nguyên chữ có dấu (mặc định System.Text.Json đổi thành mã \uXXXX — file kiến thức to gấp đôi).</summary>
    public static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Lịch sử chat Hỏi Fast — luôn ở máy này.</summary>
    public static string Sessions => _rootOverride is { } o ? Path.Combine(o, "viewer-fastask") : Path.Combine(LocalBase, "viewer-fastask");

    /// <summary>Ghi file qua file tạm rồi đổi tên (máy khác đang đọc cùng thư mục mạng không thấy file dở).</summary>
    public static void WriteAtomic(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }

    /// <summary>Tên file an toàn từ tên dự án / đường dẫn.</summary>
    public static string SafeName(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        var r = sb.ToString().Trim('_');
        return r.Length == 0 ? "_" : r.Length > 80 ? r[..80] : r;
    }
}
