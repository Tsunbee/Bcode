using System.Text.Json;

namespace Bcode.App.Models;

/// <summary>
/// Persisted app configuration, equivalent to FCode's "Registry Value" menu
/// (NotePadApp, VSApp20xx, SQLProfiler, SQLSMS, WSPath, LibraryPath, FileLookup...).
/// Stored as JSON under %AppData%\Bcode\settings.json instead of the Windows
/// registry, so it is portable and easy to inspect/back up.
/// </summary>
public class AppSettings
{
    public List<Workspace> Workspaces { get; set; } = new();
    public string LastWorkspace { get; set; } = "";

    /// <summary>Đường dẫn tới Config.xml của chính FCode (thường ở
    /// "...\FCode\Config\Config.xml") — Ctrl+F5 tra ở đây TRƯỚC để lấy đúng server thật của
    /// từng dự án (SQL2008/2014/2016/... khác nhau tuỳ dự án, không phải lúc nào cũng giống
    /// nhau) thay vì đoán 1 server mặc định. Xem FCodeConfigImportService. Hỏi Bee 1 lần rồi
    /// lưu lại ở đây, không cần sửa code khi đổi máy.</summary>
    public string FCodeConfigXmlPath { get; set; } = "";

    /// <summary>Server dùng làm PHƯƠNG ÁN DỰ PHÒNG khi Ctrl+F5 tự sinh config cho 1 mã dự án
    /// không tìm thấy cả trong Workspaces đã lưu LẪN trong Config.xml của FCode (xem
    /// MainForm.GenerateProjectTemplate) — chỉ là đoán theo quy ước đặt tên phổ biến nhất,
    /// không phải giá trị đúng cho mọi dự án (có dự án ở SQL2014/2016 — xem
    /// FCodeConfigXmlPath ở trên mới là nguồn đáng tin). Bee đổi được ở đây, không cần sửa
    /// code.</summary>
    public string DefaultProjectServer { get; set; } = "172.168.5.14\\SQL2008";

    /// <summary>Hậu tố phiên bản dùng khi ĐOÁN (fallback) tên database/đường dẫn cho dự án
    /// mới không tìm thấy ở đâu cả, vd "FBISP2422" trong "{ID}_FBISP2422_S",
    /// "\\...\FBI\{ID}\FBISP2422\". Đổi ở đây khi có version mới, không cần sửa code.</summary>
    public string DefaultProjectVersionSuffix { get; set; } = "FBISP2422";

    public string NotePadApp { get; set; } = "notepad.exe";
    public string VSAppPath { get; set; } = "";
    public string SqlProfilerPath { get; set; } = @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\Profiler.exe";
    public string ProfilerLoginUser { get; set; } = "profile";
    public string ProfilerLoginPassword { get; set; } = "fsd";
    public string ProfilerTemplateName { get; set; } = "";
    public string SqlSmsPath { get; set; } = "ssms.exe";
    public string LibraryPath { get; set; } = "";
    public int FileLookupSplitterDistance { get; set; } = 0;

    /// <summary>Path to BcodeViewer.exe — the standalone Monaco/WebView2-based editor with
    /// an AI chat panel that File Lookup's "Edit" action launches for a file, the same way
    /// VSAppPath/SqlSmsPath launch their own external tools. Empty by default.</summary>
    public string ViewerExePath { get; set; } = "";

    /// <summary>
    /// Keys (see MainForm's tool button list) hidden from the Tools toolbar —
    /// backs the "Quick Access" show/hide customizer so the toolbar doesn't
    /// grow unbounded as more tools are added.
    /// </summary>
    public List<string> HiddenToolKeys { get; set; } = new();

    /// <summary>
    /// Whether the "(nội dung tạm)" bar at the top of a generated script tab
    /// (SQL Object definition, Script Cart view — anything not backed by a real
    /// file) is shown. Off once the user hides it via the ✕ on that bar.
    /// </summary>
    public bool ShowTempContentBar { get; set; } = true;

    /// <summary>
    /// API Key của Google Gemini để gợi ý code inline
    /// </summary>
    public string GeminiApiKey { get; set; } = "";

    /// <summary>
    /// Cờ bật/tắt tính năng gợi ý inline
    /// </summary>
    public bool EnableCopilotSuggest { get; set; } = true;

    private static string SettingsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // fall through to defaults if the settings file is corrupt
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
