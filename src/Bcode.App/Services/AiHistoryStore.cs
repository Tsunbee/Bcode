using System.Text.Json;

namespace Bcode.App.Services;

public record AiHistoryEntry(string Id, DateTime Time, string Kind, string Engine, string Instruction, string Context, string Result);

/// <summary>
/// Lưu lại những gì AI đã gợi ý trong SQL Query (ghost text lúc gõ và kết quả hộp Ctrl+I) để lỡ tay tắt/bỏ qua vẫn lấy lại được
/// (menu ⚙ &gt; Lịch sử gợi ý AI). Chỉ giữ <see cref="MaxEntries"/> mục mới nhất, mỗi mục cắt tối đa <see cref="MaxResultChars"/> ký
/// tự; gợi ý trùng hệt mục ngay trước thì không ghi lại (ghost text hay bị hỏi lặp cùng 1 vị trí).
///
/// File: %AppData%\Bcode\ai-history.json. Chứa cả đoạn code của người dùng gửi lên làm ngữ cảnh? KHÔNG — chỉ lưu dòng đang gõ
/// (Context), yêu cầu và kết quả AI trả về.
/// </summary>
public static class AiHistoryStore
{
    private const int MaxEntries = 300;
    private const int MaxResultChars = 20000;
    private static readonly object Gate = new();
    private static List<AiHistoryEntry>? _cache;

    private static string StorePath => Path.Combine(
        BcodePaths.AppData, "Bcode", "ai-history.json");

    private static List<AiHistoryEntry> Load()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (File.Exists(StorePath))
                _cache = JsonSerializer.Deserialize<List<AiHistoryEntry>>(File.ReadAllText(StorePath));
        }
        catch { /* file hỏng → bắt đầu lại danh sách rỗng */ }
        return _cache ??= new List<AiHistoryEntry>();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* lịch sử chỉ là tiện ích — không được làm hỏng gợi ý vì lỗi ghi file */ }
    }

    public static void Add(string kind, string engine, string instruction, string context, string result)
    {
        if (string.IsNullOrWhiteSpace(result)) return;
        if (result.Length > MaxResultChars) result = result[..MaxResultChars];
        lock (Gate)
        {
            var list = Load();
            if (list.Count > 0 && list[0].Kind == kind && list[0].Result == result) return;
            list.Insert(0, new AiHistoryEntry(Guid.NewGuid().ToString("N"), DateTime.Now, kind, engine, instruction, context, result));
            if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            Save();
        }
    }

    /// <summary>Mới nhất trước.</summary>
    public static List<AiHistoryEntry> All()
    {
        lock (Gate) return Load().ToList();
    }

    public static void Remove(string id)
    {
        lock (Gate) { Load().RemoveAll(e => e.Id == id); Save(); }
    }

    public static void Clear()
    {
        lock (Gate) { Load().Clear(); Save(); }
    }
}
