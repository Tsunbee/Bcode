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

    /// <summary>Mở lại Bcode thì dựng lại các tab (kèm nội dung SQL chưa lưu) như lúc đóng — xem MainForm.Session.cs.</summary>
    public bool RestoreSession { get; set; } = true;

    /// <summary>Khôi phục gì khi mở lại: "query" = chỉ tab SQL Query; "query_table" = Query + tab Table (bảng đang xem); "all" = tất cả (cả tab công cụ).</summary>
    public string SessionMode { get; set; } = "all";

    /// <summary>true = mở lại Bcode có phiên cũ thì HỎI muốn khôi phục kiểu nào (hay không khôi phục); false = tự khôi phục theo <see cref="SessionMode"/>.</summary>
    public bool SessionAsk { get; set; } = true;

    /// <summary>Số giây giữa hai lần tự lưu phiên (tab + nội dung SQL) — chỉnh ở Settings.</summary>
    public int SessionSaveSeconds { get; set; } = 4;

    /// <summary>Tên các Workspace đã chọn gần đây, mới nhất trước (tối đa 10) — dải "Last Access" của màn hình Projects.</summary>
    public List<string> RecentWorkspaces { get; set; } = new();

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

    /// <summary>Kho source chuẩn theo từng phiên bản (mỗi thư mục con = 1 version có App_Data\Controllers + Main) — nguồn của
    /// "Cấp source (Add Source)". Đổi được khi kho dời chỗ, không cần sửa code.</summary>
    public string SourceCollectionPath { get; set; } = @"\\172.168.5.14\SourceCollection\FBO-FBI";

    /// <summary>Chế độ hiệu năng: "auto" (đoán theo CPU/RAM máy), "low", "medium", "high" — xem <see cref="UI.PerformanceProfile"/>.</summary>
    public string PerformanceMode { get; set; } = "auto";

    /// <summary>Tuỳ chỉnh riêng số tiến trình hiển thị WebView2 tối đa (0 = không giới hạn); null = theo chế độ. Đổi xong phải mở lại Bcode.</summary>
    public int? RendererProcessLimit { get; set; }

    /// <summary>Tuỳ chỉnh riêng việc dựng sẵn 1 tab SQL Query ẩn ở nền (mở tab nhanh hơn, tốn ~125MB); null = theo chế độ.</summary>
    public bool? PrewarmSqlTab { get; set; }

    /// <summary>Tuỳ chỉnh riêng: gộp khung kết quả của tab SQL Query (thanh tab, lưới, Message, Pivot) vào 1 WebView2; null = theo chế độ. Áp cho tab mở sau đó.</summary>
    public bool? MergeSqlResultFrames { get; set; }

    /// <summary>Gộp thanh Execute + editor Monaco của tab SQL vào 1 WebView2 (null = theo chế độ hiệu năng; bớt ~18MB / tab).</summary>
    public bool? MergeSqlBarEditor { get; set; }

    /// <summary>Tuỳ chỉnh riêng: tab SQL Query không xem quá N phút thì "ngủ đông" (giải phóng WebView2, bấm vào dựng lại — mất lịch sử Undo của tab đó);
    /// 0 = tắt; null = theo chế độ (Thấp = 15 phút).</summary>
    public int? HibernateSqlTabMinutes { get; set; }

    /// <summary>Tỉ lệ giao diện: "Auto" (tự co giãn theo cỡ màn hình) hoặc phần trăm cố định như "100" — xem UiScale.</summary>
    public string UiScale { get; set; } = "Auto";

    /// <summary>Thư mục đích lần "Copy to..." nhiều file gần nhất của File Lookup (xem CopyMultiFileForm).</summary>
    public string? LastCopyToPath { get; set; }

    /// <summary>Thư mục lưu .frx mặc định của tab "Excel → FRX" (thường là Templates\Frx của phần mềm, có thể là đường dẫn
    /// mạng). Ô "Lưu vào" trên tab để trống thì lưu vào đây; trống nốt thì file nằm ở thư mục tạm, bấm "Lưu thành…" để chép ra.</summary>
    public string FrxOutputDir { get; set; } = "";

    public string NotePadApp { get; set; } = "notepad.exe";
    public string VSAppPath { get; set; } = "";
    public string SqlProfilerPath { get; set; } = @"C:\Program Files (x86)\Microsoft SQL Server Management Studio 20\Common7\Profiler.exe";
    public string ProfilerLoginUser { get; set; } = "profile";
    public string ProfilerLoginPassword { get; set; } = "fsd";
    public string ProfilerTemplateName { get; set; } = "";
    public string SqlSmsPath { get; set; } = "ssms.exe";
    public string LibraryPath { get; set; } = "";
    public int FileLookupSplitterDistance { get; set; } = 0;

    /// <summary>Cách File Lookup tìm file liên quan khi bấm 1 menu: "On" (mặc định — dùng cache kết quả phân tích file, tự kiểm lại
    /// mtime/size) hoặc "Off" (cách cũ, đọc lại mọi file mỗi lần bấm; chỉ dùng khi nghi ngờ cache).</summary>
    public string FileLookupCacheMode { get; set; } = "On";

    /// <summary>Path to BcodeViewer.exe — the standalone Monaco/WebView2-based editor with
    /// an AI chat panel that File Lookup's "Edit" action launches for a file, the same way
    /// VSAppPath/SqlSmsPath launch their own external tools. Empty by default.</summary>
    public string ViewerExePath { get; set; } = "";

    /// <summary>Đường dẫn BcodeScreenDesigner.exe (Triển khai → Thiết kế màn hình). Trống = tự dò (xem MainForm.FindScreenDesignerExe). Chỉnh ở Template → Đường dẫn.</summary>
    public string ScreenDesignerExePath { get; set; } = "";

    /// <summary>F12 khi bôi đen nhiều entity: "all" = nối toàn bộ nội dung vào 1 trang, "each" = mỗi entity 1 trang.</summary>
    public string EntityPeekMode { get; set; } = "all";

    /// <summary>
    /// Keys (see MainForm's tool button list) hidden from the Tools toolbar —
    /// backs the "Quick Access" show/hide customizer so the toolbar doesn't
    /// grow unbounded as more tools are added.
    /// </summary>
    public List<string> HiddenToolKeys { get; set; } = new();

    /// <summary>Thứ tự các nút Quick Access trên thanh công cụ (mảng key, trái → phải) do người dùng sắp xếp. Rỗng = thứ tự mặc định.
    /// Nút mới (key chưa có trong danh sách này, vd sau khi cập nhật Bcode) được thêm vào cuối theo thứ tự mặc định.</summary>
    public List<string> ToolOrder { get; set; } = new();

    /// <summary>Nhóm công cụ của thanh công cụ (Quick Access → tạo nhóm, kéo thả công cụ vào nhóm) — rỗng = thanh như cũ. Xem <see cref="ToolBarGroup"/>.</summary>
    public List<ToolBarGroup> ToolBarGroups { get; set; } = new();

    /// <summary>Tên lập trình (cột ma_lt1 của bảng yêu cầu) mà Note (New) dùng khi bấm "Sync yêu cầu" — mỗi máy khai 1 lần.</summary>
    public string NoteProgrammer { get; set; } = "";

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

    /// <summary>API Key của Anthropic (Claude) — dùng cho gợi ý code SQL inline khi <see cref="CopilotEngine"/> = "claude".</summary>
    public string AnthropicApiKey { get; set; } = "";

    /// <summary>Model Claude cho gợi ý inline. Mặc định Haiku: gợi ý phải về kịp trước khi gõ tiếp nên cần nhanh và rẻ.</summary>
    public string ClaudeModel { get; set; } = "claude-haiku-4-5-20251001";

    /// <summary>Model Claude cho hộp "AI sửa/sinh SQL" (Ctrl+I): chạy theo yêu cầu, không phải mỗi lần gõ, nên dùng model mạnh hơn.</summary>
    public string ClaudeEditModel { get; set; } = "claude-sonnet-5-5";

    /// <summary>Engine gợi ý SQL inline trong SQL Query: "gemini" (mặc định, như trước) hoặc "claude".</summary>
    public string CopilotEngine { get; set; } = "gemini";

    /// <summary>
    /// Cờ bật/tắt tính năng gợi ý inline
    /// </summary>
    public bool EnableCopilotSuggest { get; set; } = true;

    /// <summary>Cỡ chữ editor SQL Query (px) người dùng chọn bằng Ctrl+lăn chuột / Tăng-Giảm cỡ chữ.
    /// 0 = mặc định như FCode (Consolas 11px, xem Web/Shell/sqleditor.html).</summary>
    public double SqlEditorFontSize { get; set; } = 0;

    private static string SettingsDir =>
        Path.Combine(BcodePaths.AppData, "Bcode");

    private static string SettingsPath => Path.Combine(SettingsDir, "settings.json");

    /// <summary>Claude/Gemini web: true = nhúng thành khung bên phải của tab SQL Query đang mở; false (mặc định) = mở thành tab riêng. Lưu ở file riêng
    /// (không nằm trong settings.json) vì nhiều nơi giữ bản AppSettings cũ trong bộ nhớ rồi Save cả file — sẽ ghi đè mất giá trị này.</summary>
    public static bool AiEmbedded
    {
        get { try { return File.Exists(AiPlacementPath) && File.ReadAllText(AiPlacementPath).Trim() == "embedded"; } catch { return false; } }
        set { try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(AiPlacementPath, value ? "embedded" : "tab"); } catch { } }
    }
    private static bool FlagFile(string name, bool defaultValue)
    {
        try { var p = Path.Combine(SettingsDir, name); return File.Exists(p) ? File.ReadAllText(p).Trim() == "1" : defaultValue; } catch { return defaultValue; }
    }
    private static void SetFlagFile(string name, bool value) { try { Directory.CreateDirectory(SettingsDir); File.WriteAllText(Path.Combine(SettingsDir, name), value ? "1" : "0"); } catch { } }

    /// <summary>Tab Table, "Run Table Async": tải bảng ở nền (mặc định, giao diện không đơ); tắt = chạy đồng bộ.</summary>
    public static bool TableRunAsync { get => FlagFile("table_run_async.txt", true); set => SetFlagFile("table_run_async.txt", value); }

    private static string AiPlacementPath => Path.Combine(SettingsDir, "ai_placement.txt");

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
