using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bcode.App.Services;

/// <summary>
/// Nhớ cấu hình "Tạo nhanh danh mục" theo bảng — toàn bộ lựa chọn (tick, header, lookup, ẩn, thứ tự cột, chi tiết...) lưu lại mỗi lần
/// Tạo file thành công, lần sau nạp đúng bảng đó thì trang hỏi khôi phục. Lưu trên máy:
/// <c>%AppData%\Bcode\quicklist\&lt;workspace&gt;\&lt;bảng&gt;.json</c> (tách theo workspace vì cùng tên bảng ở dự án khác có thể khác hẳn).
/// Nội dung là đúng JSON "spec" trang gửi lên — trang tự đọc lại, C# không cần hiểu từng trường.
/// </summary>
public static class QuickListConfigStore
{
    private static string Root => Path.Combine(BcodePaths.AppData, "Bcode", "quicklist");

    private static string PathFor(string workspace, string table)
    {
        static string Safe(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var t = new string(s.Trim().Select(ch => bad.Contains(ch) ? '_' : ch).ToArray());
            return t.Length == 0 ? "_" : t;
        }
        return Path.Combine(Root, Safe(workspace), Safe(table).ToLowerInvariant() + ".json");
    }

    /// <summary>Cấu hình đã lưu của bảng (spec + thời điểm lưu), null nếu chưa có / đọc lỗi.</summary>
    public static JsonNode? Load(string workspace, string table)
    {
        try
        {
            var path = PathFor(workspace, table);
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    public static void Save(string workspace, string table, JsonElement spec)
    {
        try
        {
            var path = PathFor(workspace, table);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var node = new JsonObject
            {
                ["savedAt"] = DateTime.Now.ToString("dd/MM/yyyy HH:mm"),
                ["spec"] = JsonNode.Parse(spec.GetRawText()),
            };
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* chỉ là tiện ích — ghi không được thì thôi */ }
    }
}
