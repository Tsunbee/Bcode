using Bcode.App.UI;

namespace BcodeNote.App;

/// <summary>
/// BcodeNote.exe — "Note (New)" của Bcode làm việc với NHIỀU dự án: bên trái danh sách dự án (workspace trong %AppData%\Bcode\settings.json) + cây menu
/// (wcommand) của dự án đang chọn, bên phải đúng màn Note (New) của Bcode (AdvanceNoteControl / advnote.html) đọc-ghi cùng file request
/// %AppData%\Bcode\AdvanceNotes\&lt;dự án&gt;.json. Chọn menu → gợi ý Gen All / File Path / SQL Object / Table liên quan (GenAllService.SuggestForMenuAsync).
/// Tham số: <c>--project &lt;tên workspace&gt;</c> = mở sẵn dự án đó; thêm <c>--menu &lt;wmenu_id&gt;</c> (và/hoặc <c>--sysid &lt;controller&gt;</c>) = "Gen nhanh menu"
/// của Bcode: chọn sẵn menu đó trong cây và tự mở hộp gợi ý như bấm đôi menu.
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
        string? project = null, menu = null, sysId = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            var v = string.IsNullOrWhiteSpace(args[i + 1]) ? null : args[i + 1].Trim();
            if (args[i].Equals("--project", StringComparison.OrdinalIgnoreCase)) project = v ?? project;
            else if (args[i].Equals("--menu", StringComparison.OrdinalIgnoreCase)) menu = v ?? menu;
            else if (args[i].Equals("--sysid", StringComparison.OrdinalIgnoreCase)) sysId = v ?? sysId;
        }
        Application.Run(new NoteHubForm(project, menu, sysId));
    }
}
