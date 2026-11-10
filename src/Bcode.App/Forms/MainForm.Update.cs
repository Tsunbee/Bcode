using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>Cập nhật Bcode (Services/UpdateCheckService.cs, hộp Forms/UpdateForm.cs + Web/Shell/update.html):
/// mở Bcode ~20 giây thì đọc nền bcode-version.json ở thư mục phát hành, sau đó cứ 4 giờ đọc lại; có bản mới thì báo ở thanh trạng thái + mục
/// "Cập nhật Bcode" trong menu Settings. Không bao giờ tự cài khi chưa hỏi. Việc cài chạy lúc Bcode ĐÃ đóng (FormClosed) — người dùng huỷ đóng
/// (tab chưa lưu…) thì không cài gì. Mở lại sau khi cài: đọc update\result.json để báo thành công / lỗi.</summary>
public partial class MainForm
{
    private BuildStamp? _newBuild;
    /// <summary>Việc cài đang chờ Bcode đóng: bản đã chuẩn bị (hoặc quay lại bản trước) + có mở lại Bcode không.</summary>
    private (UpdatePlan? Plan, BackupInfo? Rollback, bool Overwrite, bool Relaunch)? _pendingUpdate;
    private static readonly TimeSpan UpdateRecheck = TimeSpan.FromHours(4);

    /// <summary>Thư mục phát hành dùng thật: khai trong Settings, trống = mặc định &lt;BcodeReleaseFolder&gt; của Bcode.App.csproj, "-" = tắt.</summary>
    private string ReleaseFolder => UpdateCheckService.EffectiveFolder(_settings.ReleaseFolder);

    private void InitUpdateCheck()
    {
        FormClosed += (_, _) => RunPendingUpdate();
        ReportLastUpdate();
        if (string.IsNullOrWhiteSpace(ReleaseFolder)) return;
        var timer = new System.Windows.Forms.Timer { Interval = 20_000 };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = (int)UpdateRecheck.TotalMilliseconds;
            if (IsDisposed || _newBuild is not null || string.IsNullOrWhiteSpace(ReleaseFolder)) return;
            var (remote, _) = await UpdateCheckService.CheckAsync(ReleaseFolder);   // lỗi mạng lúc tự kiểm tra: im lặng
            if (IsDisposed || remote is null || !UpdateCheckService.IsNewer(remote, UpdateCheckService.Local)) return;
            _newBuild = remote;
            PushStatus($"Có bản Bcode mới ({remote.Label}) — Settings → \"Cập nhật Bcode\" để xem và cập nhật.");
        };
        Disposed += (_, _) => timer.Dispose();
        timer.Start();
    }

    /// <summary>Lần cập nhật / quay lại vừa chạy (trước khi mở Bcode này): thành công → thanh trạng thái; lỗi → hộp báo kèm đường dẫn nhật ký.</summary>
    private void ReportLastUpdate()
    {
        if (UpdateCheckService.TakeResult() is not { } r) return;
        var what = r.Mode == "rollback" ? "Quay lại bản trước" : "Cập nhật Bcode";
        if (r.Ok)
        {
            PushStatus(r.Mode == "rollback" ? $"Đã quay lại bản trước ({r.To})." : $"Đã cập nhật Bcode lên {r.To}. Có lỗi thì Settings → Cập nhật Bcode → Quay lại bản trước.");
            return;
        }
        PushStatus(what + " không thành công — Bcode vẫn là bản cũ.");
        BeginInvoke(() => MessageBox.Show(this, $"{what} không thành công, Bcode vẫn là bản cũ.\n\n{r.Message}\n\nNhật ký: {UpdateCheckService.LogPath}",
            "Bcode — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    /// <summary>Mục menu Settings: nhãn đổi theo kết quả kiểm tra.</summary>
    private string UpdateMenuLabel =>
        _pendingUpdate is { Relaunch: false } ? "Cập nhật Bcode — sẽ cài khi đóng Bcode..."
        : _newBuild is { } b ? $"Cập nhật Bcode — có bản mới {b.Label}..." : "Kiểm tra bản Bcode mới...";

    private void CheckForUpdateInteractive()
    {
        bool Choose() { var before = ReleaseFolder; ChooseReleaseFolder(); return !string.Equals(before, ReleaseFolder, StringComparison.OrdinalIgnoreCase); }
        using var f = new UpdateForm(() => ReleaseFolder, Choose, _pendingUpdate is { Relaunch: false });
        var res = f.ShowDialog(this);
        if (res == DialogResult.Abort) { _pendingUpdate = null; PushStatus("Đã huỷ hẹn cài bản mới khi đóng Bcode."); return; }
        if (res != DialogResult.OK) return;
        switch (f.Decision)
        {
            case UpdateForm.Outcome.ApplyNow when f.Plan is not null:
                _pendingUpdate = (f.Plan, null, f.OverwriteModified, true);
                PushStatus("Đang đóng Bcode để cài bản mới...");
                Close();
                if (!IsDisposed && Visible) { _pendingUpdate = null; PushStatus("Chưa cài bản mới vì Bcode chưa đóng. Mở lại Settings → Cập nhật Bcode khi sẵn sàng."); }
                break;
            case UpdateForm.Outcome.ApplyOnClose when f.Plan is not null:
                _pendingUpdate = (f.Plan, null, f.OverwriteModified, false);
                PushStatus($"Bản mới {f.Plan.ToLabel} sẽ được cài khi bạn đóng Bcode.");
                break;
            case UpdateForm.Outcome.Rollback when f.Backup is not null:
                _pendingUpdate = (null, f.Backup, false, true);
                PushStatus("Đang đóng Bcode để quay lại bản trước...");
                Close();
                if (!IsDisposed && Visible) { _pendingUpdate = null; PushStatus("Chưa quay lại bản trước vì Bcode chưa đóng."); }
                break;
        }
    }

    /// <summary>FormClosed: chạy script cài / quay lại (tiến trình riêng — script tự chờ mọi tiến trình Bcode thoát rồi mới chép).</summary>
    private void RunPendingUpdate()
    {
        if (_pendingUpdate is not { } p) return;
        _pendingUpdate = null;
        try
        {
            if (p.Rollback is not null) UpdateCheckService.StartRollback(p.Rollback, p.Relaunch);
            else if (p.Plan is not null) UpdateCheckService.StartApply(p.Plan, p.Overwrite, p.Relaunch);
        }
        catch (Exception ex)
        {
            ErrorLog.Record(ex, "Cập nhật Bcode");
            MessageBox.Show("Không chạy được trình cập nhật: " + ex.Message, "Bcode — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ChooseReleaseFolder()
    {
        var text = SimplePromptForm.Show(this, "Thư mục phát hành bản mới", UpdateCheckService.FolderPromptHint + ":", ReleaseFolder);
        if (text is null) return;
        _settings.ReleaseFolder = UpdateCheckService.ToSetting(text);
        try { _settings.Save(); } catch { /* lưu lỗi: chỉ mất cấu hình này */ }
        _newBuild = null;
        PushStatus(ReleaseFolder.Length == 0 ? "Đã tắt kiểm tra bản mới." : "Thư mục phát hành: " + ReleaseFolder);
    }
}
