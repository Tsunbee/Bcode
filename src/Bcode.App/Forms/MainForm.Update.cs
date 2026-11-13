using Bcode.App.Services;
using Bcode.Shared;

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
        // Lõi dùng chung với BcodeViewer / Screen Designer (Shared/AppUpdate.cs). ScreenDesigner cạnh Bcode.exe là app tự cập nhật riêng — cập nhật Bcode không đụng.
        UpdateCheckService.Configure("Bcode", "update", BcodePaths.AppData, "Bcode.exe", "", "ScreenDesigner");
        FormClosed += (_, _) => RunPendingUpdate();
        ReportLastUpdate();
        // cửa sổ Bcode khác đã hẹn cài / trình cài đang chờ: đọc update\apply.lock mỗi 3 giây (không có file = gần như không tốn gì)
        var lockTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        lockTimer.Tick += (_, _) => { if (!IsDisposed) WatchInstallLock(); };
        Disposed += (_, _) => lockTimer.Dispose();
        lockTimer.Start();
        WatchInstallLock();
        if (string.IsNullOrWhiteSpace(ReleaseFolder)) return;
        var timer = new System.Windows.Forms.Timer { Interval = 20_000 };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = (int)UpdateRecheck.TotalMilliseconds;
            if (IsDisposed || _newBuild is not null || string.IsNullOrWhiteSpace(ReleaseFolder)) return;
            var (remote, _) = await UpdateCheckService.CheckAsync(ReleaseFolder);   // lỗi mạng lúc tự kiểm tra: im lặng
            if (IsDisposed || remote is null || !UpdateCheckService.IsNewer(remote, UpdateCheckService.Local)) return;
            _newBuild = remote;
            ShowUpdateToast();
        };
        Disposed += (_, _) => timer.Dispose();
        timer.Start();
    }

    /// <summary>Thông báo "Có bản Bcode mới" không tự tắt, góc dưới phải cửa sổ (Shared/AppUpdateToast.cs): Xem = mở hộp Cập nhật; Để sau = nhắc lại sau 4 giờ.</summary>
    private AppUpdateToast? _updateToast;

    private AppUpdateToast UpdateToast => _updateToast ??= new AppUpdateToast(this,
        () => new ToastPalette(UI.AppColors.Panel, UI.AppColors.Text, UI.AppColors.TextMuted, UI.AppColors.Accent, UI.AppColors.Border),
        () => BeginInvoke(CheckForUpdateInteractive));

    private (string Title, string Detail) UpdateToastText(BuildStamp b) =>
        ($"Có bản Bcode mới — {(b.Version.Length > 0 ? "v" + b.Version + " " : "")}{(b.Build > 0 ? "build " + b.Build : b.When)}",
         "Bấm Xem để xem có gì mới và cập nhật. Tải về chạy nền, Bcode vẫn dùng bình thường.");

    private void ShowUpdateToast()
    {
        if (_newBuild is not { } b || _pendingUpdate is not null || _installWaiting is not null) return;
        var (t, d) = UpdateToastText(b);
        UpdateToast.Show(t, d);
    }

    /// <summary>Khoá cài của cửa sổ Bcode khác (UpdateCheckService.OtherLock) — có thì thay thông báo "Có bản mới" bằng "Đang chờ đóng Bcode để cài bản mới".</summary>
    private UpdateCheckService.InstallLock? _installWaiting;

    private void WatchInstallLock()
    {
        var l = UpdateCheckService.OtherLock();
        if (l is null)
        {
            if (_installWaiting is null) return;
            _installWaiting = null;   // bên kia huỷ hẹn / đã xong
            _updateToast?.Clear();
            ShowUpdateToast();
            return;
        }
        var changed = _installWaiting is null || _installWaiting.Pid != l.Pid || _installWaiting.State != l.State;
        _installWaiting = l;
        if (changed) UpdateToast.Show("Đang chờ đóng Bcode để cài bản mới", UpdateCheckService.WaitingText(l));
    }

    /// <summary>Lần cập nhật / quay lại vừa chạy (trước khi mở Bcode này): thành công → thanh trạng thái; lỗi → hộp báo kèm đường dẫn nhật ký.</summary>
    private void ReportLastUpdate()
    {
        if (UpdateCheckService.TakeResult() is not { } r) return;
        var what = r.Mode == "rollback" ? "Quay lại bản trước" : "Cập nhật Bcode";
        if (r.Ok)
        {
            PushNotice(r.Mode == "rollback" ? $"Đã quay lại bản trước ({r.To})." : $"Đã cập nhật Bcode lên {r.To}. Có lỗi thì Settings → Cập nhật Bcode → Quay lại bản trước.");
            return;
        }
        PushNotice(what + " không thành công — Bcode vẫn là bản cũ.");
        BeginInvoke(() => MessageBox.Show(this, $"{what} không thành công, Bcode vẫn là bản cũ.\n\n{r.Message}\n\nNhật ký: {UpdateCheckService.LogPath}",
            "Bcode — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    /// <summary>Mục menu Settings: nhãn đổi theo kết quả kiểm tra.</summary>
    private string UpdateMenuLabel =>
        _installWaiting is not null ? "Cập nhật Bcode — đang chờ đóng Bcode để cài bản mới..."
        : _pendingUpdate is { Relaunch: false } ? "Cập nhật Bcode — sẽ cài khi đóng Bcode..."
        : _newBuild is { } b ? $"Cập nhật Bcode — có bản mới {b.Label}..." : "Kiểm tra bản Bcode mới...";

    private void CheckForUpdateInteractive()
    {
        bool Choose() { var before = ReleaseFolder; ChooseReleaseFolder(); return !string.Equals(before, ReleaseFolder, StringComparison.OrdinalIgnoreCase); }
        _updateToast?.Clear();
        using var f = new UpdateForm(() => ReleaseFolder, Choose, _pendingUpdate is { Relaunch: false });
        var res = f.ShowDialog(this);
        if (res == DialogResult.Abort) { _pendingUpdate = null; PushNotice("Đã huỷ hẹn cài bản mới khi đóng Bcode."); }
        if (res != DialogResult.OK || f.Decision == UpdateDialogLogic.Outcome.None)
        {
            // đóng hộp mà chưa cập nhật: nhắc lại sau 4 giờ (thông báo cố định)
            if (_newBuild is { } nb && _pendingUpdate is null) { var (t, d) = UpdateToastText(nb); UpdateToast.Later(t, d); }
            return;
        }
        switch (f.Decision)
        {
            case UpdateDialogLogic.Outcome.ApplyNow when f.Plan is not null:
                _pendingUpdate = (f.Plan, null, f.OverwriteModified, true);
                PushStatus("Đang đóng Bcode để cài bản mới...");
                Close();
                if (!IsDisposed && Visible) { _pendingUpdate = null; UpdateCheckService.ReleaseLock(); PushNotice("Chưa cài bản mới vì Bcode chưa đóng. Mở lại Settings → Cập nhật Bcode khi sẵn sàng."); }
                break;
            case UpdateDialogLogic.Outcome.ApplyOnClose when f.Plan is not null:
                _pendingUpdate = (f.Plan, null, f.OverwriteModified, false);
                PushNotice($"Bản mới {f.Plan.ToLabel} sẽ được cài khi bạn đóng Bcode.");
                break;
            case UpdateDialogLogic.Outcome.Rollback when f.Backup is not null:
                _pendingUpdate = (null, f.Backup, false, true);
                PushStatus("Đang đóng Bcode để quay lại bản trước...");
                Close();
                if (!IsDisposed && Visible) { _pendingUpdate = null; UpdateCheckService.ReleaseLock(); PushNotice("Chưa quay lại bản trước vì Bcode chưa đóng."); }
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

    /// <summary>Settings → About Bcode: phiên bản / build / commit của bản đang chạy (bcode-version.json), nơi cài, thư mục phát hành.
    /// Hộp MessageBox — Ctrl+C chép được cả nội dung.</summary>
    private void ShowAbout()
    {
        var b = UpdateCheckService.Local;
        string webView;
        try { webView = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { webView = "(không có)"; }
        var lines = new List<string>
        {
            "Bcode" + (b.Version.Length > 0 ? " v" + b.Version : ""),
            "",
            "Build: " + (b.Build > 0 ? b.Build.ToString() : "—") + (b.Config.Length > 0 ? " (" + b.Config + ")" : ""),
            "Ngày build: " + b.When,
            "Commit: " + (b.HasCommit ? b.Commit : "—"),
            "",
            "Cài ở: " + AppContext.BaseDirectory.TrimEnd('\\'),
            "Thư mục phát hành: " + (ReleaseFolder.Length > 0 ? ReleaseFolder : "(tắt kiểm tra bản mới)"),
        };
        if (_newBuild is { } nb) lines.Add("Có bản mới: " + nb.Label + " — Settings → Cập nhật Bcode.");
        lines.Add(".NET " + Environment.Version + " · WebView2 " + webView + " · máy " + Environment.MachineName);
        lines.Add("");
        lines.Add("Dev: HàoTN | PhongNT · Tester: ThinhBM | KhanhNN");
        MessageBox.Show(this, string.Join("\n", lines), "About Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ChooseReleaseFolder()
    {
        var text = SimplePromptForm.Show(this, "Thư mục phát hành bản mới", UpdateCheckService.FolderPromptHint + ":", ReleaseFolder);
        if (text is null) return;
        _settings.ReleaseFolder = UpdateCheckService.ToSetting(text);
        try { _settings.Save(); } catch { /* lưu lỗi: chỉ mất cấu hình này */ }
        _newBuild = null; _updateToast?.Clear();
        PushNotice(ReleaseFolder.Length == 0 ? "Đã tắt kiểm tra bản mới." : "Thư mục phát hành: " + ReleaseFolder);
    }
}
