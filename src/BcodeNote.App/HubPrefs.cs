using System.Text.Json;
using Bcode.App;

namespace BcodeNote.App;

/// <summary>Tuỳ chọn riêng của BcodeNote (%AppData%\Bcode\bcodenote.json): dự án mở gần nhất, bề rộng khung trái, thu gọn danh sách dự án.
/// Dữ liệu request KHÔNG nằm ở đây — vẫn là %AppData%\Bcode\AdvanceNotes\&lt;dự án&gt;.json dùng chung với Note (New) của Bcode.</summary>
internal sealed class HubPrefs
{
    public string LastProject { get; set; } = "";
    /// <summary>Bề rộng khung trái theo px thiết kế (96 DPI).</summary>
    public int LeftWidth { get; set; } = 340;
    public bool ProjectsCollapsed { get; set; }
    public bool Maximized { get; set; } = true;

    private static string FilePath => Path.Combine(BcodePaths.AppData, "Bcode", "bcodenote.json");

    public static HubPrefs Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<HubPrefs>(File.ReadAllText(FilePath)) ?? new HubPrefs(); }
        catch { /* file hỏng — dùng mặc định */ }
        return new HubPrefs();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* không ghi được — lần sau dùng mặc định */ }
    }
}
