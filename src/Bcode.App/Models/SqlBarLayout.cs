using System.Text.Json;

namespace Bcode.App.Models;

/// <summary>
/// Bố cục thanh Execute của SQL Query (Web/Shell/sqlquerybar.html): nút nào ghim ngoài thanh, nút nào gom vào nhóm "Tên ▾", nút nào ẩn.
/// Khoá nút do trang tự định nghĩa (vd "run", "beauty", "reset-conn"); C# chỉ lưu và đẩy lại cho trang. null = bố cục mặc định của trang.
/// Lưu file riêng (không nằm trong settings.json) vì mỗi tab SQL / MainForm giữ 1 bản AppSettings riêng — ghi chung dễ đè mất nhau.
/// </summary>
public sealed class SqlBarLayout
{
    public List<string> Pinned { get; set; } = new();
    public List<ToolBarGroup> Groups { get; set; } = new();
    public List<string> Hidden { get; set; } = new();

    private static string FilePath => Path.Combine(BcodePaths.AppData, "Bcode", "sqlbar_layout.json");
    private static readonly JsonSerializerOptions Opts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>Bố cục vừa đổi (Tùy chỉnh thanh…) — mọi tab SQL đang mở áp lại ngay. null = về mặc định.</summary>
    public static event Action<SqlBarLayout?>? Changed;

    public static SqlBarLayout? Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<SqlBarLayout>(File.ReadAllText(FilePath), Opts) : null; }
        catch { return null; }   // file hỏng — dùng mặc định
    }

    public static void Save(SqlBarLayout? layout)
    {
        try
        {
            if (layout is null) { if (File.Exists(FilePath)) File.Delete(FilePath); }
            else { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(layout, Opts)); }
        }
        catch { /* không ghi được — chỉ áp cho phiên này */ }
        Changed?.Invoke(layout);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Opts);
}
