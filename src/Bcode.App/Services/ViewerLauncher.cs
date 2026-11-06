using System.Diagnostics;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>
/// Mở 1 file bằng BcodeViewer (exe riêng, đường dẫn ở cài đặt <see cref="AppSettings.ViewerExePath"/>) từ những nơi không giữ AppSettings —
/// khung xem file (<see cref="Controls.MonacoPreviewControl"/>), cửa sổ xem F12 (<see cref="Forms.SourceViewForm"/>), tab mở file
/// (<see cref="Controls.ScriptEditorControl"/>): phím file.openInViewer (mặc định Ctrl+F12). MainForm gán <see cref="Settings"/> /
/// <see cref="ProjectName"/> lúc khởi động. Cách gọi giống MainForm.OpenFileInViewer / FileLookupControl.OpenInViewer: chưa khai đường dẫn thì
/// hỏi chọn BcodeViewer.exe một lần rồi lưu vào cài đặt; tham số = "file" "tên dự án" (BcodeViewer chưa nhận số dòng).
/// </summary>
internal static class ViewerLauncher
{
    internal static Func<AppSettings>? Settings;
    internal static Func<string?>? ProjectName;

    /// <param name="line">&gt; 0 = BcodeViewer mở file rồi nhảy tới dòng này (qua --line; BcodeViewer đang mở sẵn thì tự chuyển tiếp tới cửa sổ đó — pipe có phiên bản).</param>
    public static void Open(IWin32Window? owner, string path, int line = 0, int col = 1)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(owner, "Không thấy file:\n" + path, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var settings = Settings?.Invoke();
        if (settings is null) return;
        if (string.IsNullOrWhiteSpace(settings.ViewerExePath) || !File.Exists(settings.ViewerExePath))
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Chọn BcodeViewer.exe",
                Filter = "BcodeViewer (BcodeViewer.exe)|BcodeViewer.exe|Tất cả file (*.exe)|*.exe",
            };
            if (dialog.ShowDialog(owner) != DialogResult.OK) return;
            settings.ViewerExePath = dialog.FileName;
            settings.Save();
        }
        try
        {
            var projectName = ProjectName?.Invoke();
            var arguments = string.IsNullOrWhiteSpace(projectName) ? $"\"{path}\"" : $"\"{path}\" \"{projectName}\"";
            if (line > 0) arguments += $" --line {line} --col {Math.Max(1, col)}";
            Process.Start(new ProcessStartInfo(settings.ViewerExePath, arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Không mở được BcodeViewer:\n{ex.Message}", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
