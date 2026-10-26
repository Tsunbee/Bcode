using Bcode.App.UI;

namespace BcodeNote.App;

/// <summary>
/// BcodeNote.exe — "Note (New)" của Bcode làm việc với NHIỀU dự án: bên trái danh sách dự án (workspace trong %AppData%\Bcode\settings.json) + cây menu
/// (wcommand) của dự án đang chọn, bên phải đúng màn Note (New) của Bcode (AdvanceNoteControl / advnote.html) đọc-ghi cùng file request
/// %AppData%\Bcode\AdvanceNotes\&lt;dự án&gt;.json. Chọn menu → gợi ý Gen All / File Path / SQL Object / Table liên quan (GenAllService.SuggestForMenuAsync).
/// Tham số: <c>--project &lt;tên workspace&gt;</c> = mở sẵn dự án đó.
/// </summary>
internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        UiThemes.ApplyPalettes();
        UiMode.Set(UiModeKind.Web);   // màn Note (New) là trang WebView2 (advnote.html) — giống chế độ Web của Bcode
        // Dựng sẵn môi trường WebView2 (dùng chung thư mục %AppData%\Bcode\WebView2 với Bcode) song song với việc đọc cấu hình.
        try { _ = WebViewEnvironment.GetAsync(); } catch { /* báo lỗi ở chỗ dùng */ }
        string? project = null;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i].Equals("--project", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(args[i + 1])) project = args[i + 1].Trim();
        Application.Run(new NoteHubForm(project));
    }
}
