using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>Kiểm tra bản Bcode mới (Services/UpdateCheckService.cs): mở Bcode được ~20 giây thì đọc nền bcode-version.json ở thư mục phát hành;
/// có bản mới thì báo ở thanh trạng thái + mục "Cập nhật Bcode" trong menu Settings. Không bao giờ tự cài khi chưa hỏi.</summary>
public partial class MainForm
{
    private BuildStamp? _newBuild;

    /// <summary>Thư mục phát hành dùng thật: khai trong Settings, trống = mặc định &lt;BcodeReleaseFolder&gt; của Bcode.App.csproj, "-" = tắt.</summary>
    private string ReleaseFolder => UpdateCheckService.EffectiveFolder(_settings.ReleaseFolder);

    private void InitUpdateCheck()
    {
        if (string.IsNullOrWhiteSpace(ReleaseFolder)) return;
        var timer = new System.Windows.Forms.Timer { Interval = 20_000 };
        timer.Tick += async (_, _) =>
        {
            timer.Stop(); timer.Dispose();
            var (remote, _) = await UpdateCheckService.CheckAsync(ReleaseFolder);   // lỗi mạng lúc tự kiểm tra: im lặng
            if (IsDisposed || remote is null || !UpdateCheckService.IsNewer(remote, UpdateCheckService.Local)) return;
            _newBuild = remote;
            PushStatus($"Có bản Bcode mới ({remote.Label}) — Settings → \"Cập nhật Bcode\" để cập nhật.");
        };
        timer.Start();
    }

    /// <summary>Mục menu Settings: nhãn đổi theo kết quả kiểm tra.</summary>
    private string UpdateMenuLabel => _newBuild is { } b ? $"Cập nhật Bcode — có bản mới {b.Label}..." : "Kiểm tra bản Bcode mới...";

    private async void CheckForUpdateInteractive()
    {
        if (string.IsNullOrWhiteSpace(ReleaseFolder)) { ChooseReleaseFolder(); if (string.IsNullOrWhiteSpace(ReleaseFolder)) return; }
        var folder = ReleaseFolder;
        var local = UpdateCheckService.Local;
        PushStatus("Đang kiểm tra bản Bcode mới...");
        var (remote, error) = await UpdateCheckService.CheckAsync(folder);
        if (IsDisposed) return;
        if (remote is null)
        {
            PushStatus("Không kiểm tra được bản mới.");
            MessageBox.Show(this, error ?? "Không đọc được thư mục phát hành.", "Bcode — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!UpdateCheckService.IsNewer(remote, local))
        {
            _newBuild = null;
            PushStatus($"Bcode đang là bản mới nhất ({local.Label}).");
            MessageBox.Show(this, $"Bcode đang là bản mới nhất.\n\nBản đang chạy: {local.Label}\nBản phát hành: {remote.Label}", "Bcode — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _newBuild = remote;
        if (UpdateCheckService.SameFolder(folder))
        {
            MessageBox.Show(this, "Thư mục phát hành trùng (hoặc nằm trong) thư mục Bcode đang chạy — không tự chép được. Đặt lại thư mục phát hành khác.", "Bcode — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var ask = MessageBox.Show(this,
            $"Có bản Bcode mới.\n\nBản đang chạy: {local.Label}\nBản mới: {remote.Label}\nTừ: {folder}\n\n" +
            "Cập nhật ngay? Bcode sẽ đóng; khi MỌI cửa sổ Bcode (cả BcodeViewer / BcodeNote chạy từ thư mục này) đã đóng thì bản mới được chép vào rồi Bcode tự mở lại.\n" +
            "Dữ liệu (dự án, phiên, note…) ở %AppData%\\Bcode không bị đụng tới. Nhớ lưu các tab đang sửa trước.",
            "Bcode — Cập nhật", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes) return;
        try { UpdateCheckService.StartUpdater(folder); }
        catch (Exception ex) { MessageBox.Show(this, "Không chạy được trình cập nhật: " + ex.Message, "Bcode — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        Close();
    }

    private void ChooseReleaseFolder()
    {
        var text = SimplePromptForm.Show(this, "Thư mục phát hành bản mới", UpdateCheckService.FolderPromptHint + ":", ReleaseFolder);
        if (text is null) return;
        _settings.ReleaseFolder = UpdateCheckService.ToSetting(text);
        try { _settings.Save(); } catch { /* lưu lỗi: chỉ mất cấu hình này */ }
        PushStatus(ReleaseFolder.Length == 0 ? "Đã tắt kiểm tra bản mới." : "Thư mục phát hành: " + ReleaseFolder);
    }
}
