using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>Một tab được ghi nhớ: <c>sql</c> (tab SQL Query / tab mở từ SQL Object, kèm nội dung đang gõ) hoặc <c>tool</c> (tab công cụ mở lại bằng khoá tool).</summary>
public sealed record SessionTab(string Kind, string Title, string? Key, string? Text, bool Sys, bool Pinned);

public sealed record SessionState(string Workspace, int Selected, List<SessionTab> Tabs, DateTime SavedAt);

/// <summary>
/// Lưu / đọc "phiên làm việc" (các tab đang mở + nội dung SQL chưa lưu) để mở lại Bcode là tab trở lại như lúc đóng. Mỗi dự án một file trong
/// <c>%AppData%\Bcode\session\</c> (nội dung SQL có thể chứa dữ liệu khách nên KHÔNG ghi lên thư mục source / ổ mạng). Ghi nguyên tử
/// (file tạm rồi đổi tên) để tắt đột ngột không làm hỏng file; mọi thao tác "best effort" — phiên không bao giờ được làm hỏng việc khởi động.
/// </summary>
public static class SessionStore
{
    private const int MaxTextChars = 2_000_000;
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    private static string Dir => Path.Combine(BcodePaths.AppData, "Bcode", "session");

    public static string PathFor(string workspace)
    {
        var safe = Regex.Replace(string.IsNullOrWhiteSpace(workspace) ? "default" : workspace, @"[^\w.\-]+", "_");
        return Path.Combine(Dir, safe + ".json");
    }

    public static SessionState? Load(string workspace)
    {
        try
        {
            var path = PathFor(workspace);
            return File.Exists(path) ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path), Opts) : null;
        }
        catch { return null; }
    }

    /// <summary>Chuỗi JSON của trạng thái — để so với lần ghi trước, không đổi thì khỏi ghi lại.</summary>
    public static string Serialize(SessionState state)
    {
        var trimmed = state with
        {
            Tabs = state.Tabs.Select(t => t.Text is { Length: > MaxTextChars } ? t with { Text = t.Text[..MaxTextChars] } : t).ToList(),
        };
        return JsonSerializer.Serialize(trimmed with { SavedAt = default }, Opts);
    }

    private static readonly object WriteLock = new();

    public static bool Save(SessionState state)
    {
        try
        {
            lock (WriteLock)
            {
                Directory.CreateDirectory(Dir);
                var path = PathFor(state.Workspace);
                var tmp = path + ".tmp";
                var trimmed = state.Tabs.Select(t => t.Text is { Length: > MaxTextChars } ? t with { Text = t.Text[..MaxTextChars] } : t).ToList();
                File.WriteAllText(tmp, JsonSerializer.Serialize(state with { SavedAt = DateTime.Now, Tabs = trimmed }, Opts));
                File.Move(tmp, path, overwrite: true);
            }
            return true;
        }
        catch { return false; }
    }
}
