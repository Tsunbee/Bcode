using System.Text.Json;
using Bcode.Shared;
using Microsoft.Web.WebView2.Core;

namespace BcodeViewer.App;

/// <summary>Cập nhật BcodeViewer — cơ chế RIÊNG của Viewer, cùng lõi với Bcode (Shared/AppUpdate.cs + AppUpdateDialog.cs) nhưng thư mục phát hành riêng
/// (&lt;BcodeReleaseFolder&gt; của BcodeViewer.App.csproj, đổi được trong hộp), thư mục tạm %LocalAppData%\Bcode\viewer-update, nhật ký
/// %AppData%\Bcode\viewer-update.log. Mở Viewer ~20 giây thì đọc nền bcode-version.json ở thư mục phát hành, sau đó cứ 4 giờ; có bản mới thì
/// hiện thông báo + đổi nhãn Help → "Cập nhật BcodeViewer — có bản mới". Không tự cài khi chưa hỏi; việc cài chạy lúc Viewer ĐÃ đóng (OnFormClosed),
/// người dùng huỷ đóng (Cancel ở hộp lưu file) thì không cài.</summary>
public partial class MainForm
{
    private CoreWebView2Environment? _webEnv;
    private BuildStamp? _newBuild;
    private (UpdatePlan? Plan, BackupInfo? Rollback, bool Overwrite, bool Relaunch)? _pendingUpdate;
    private bool _updateInit;
    private static readonly TimeSpan UpdateRecheck = TimeSpan.FromHours(4);

    private string ReleaseFolder => UpdateCheckService.EffectiveFolder(_settings.ReleaseFolder);

    /// <summary>Gọi 1 lần khi trang đã nạp (OnPageReady) — thông báo hiện được trong trang.</summary>
    private void InitUpdateCheck()
    {
        if (_updateInit) return;
        _updateInit = true;
        UpdateCheckService.Configure("BcodeViewer", "viewer-update", BcodePaths.AppData, publishApp: "Viewer");
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
            PushUpdateLabel();
            ShowUpdateToast();
        };
        Disposed += (_, _) => timer.Dispose();
        timer.Start();
    }

    /// <summary>Thông báo "Có bản BcodeViewer mới" không tự tắt, góc dưới phải (Shared/AppUpdateToast.cs): Xem = mở hộp Cập nhật; Để sau = nhắc lại sau 4 giờ.</summary>
    private AppUpdateToast? _updateToast;

    private AppUpdateToast UpdateToast => _updateToast ??= new AppUpdateToast(this,
        () => new ToastPalette(UI.AppColors.Panel, UI.AppColors.Text, UI.AppColors.TextMuted, UI.AppColors.Accent, UI.AppColors.Border),
        () => BeginInvoke(CheckForUpdateInteractive));

    private static (string Title, string Detail) UpdateToastText(BuildStamp b) =>
        ($"Có bản BcodeViewer mới — {(b.Version.Length > 0 ? "v" + b.Version + " " : "")}{(b.Build > 0 ? "build " + b.Build : b.When)}",
         "Bấm Xem để xem có gì mới và cập nhật. Tải về chạy nền, vẫn soạn thảo bình thường.");

    private void ShowUpdateToast()
    {
        if (_newBuild is not { } b || _pendingUpdate is not null) return;
        var (t, d) = UpdateToastText(b);
        UpdateToast.Show(t, d);
    }

    private void UpdateNotice(string text, int ms = 6000)
    {
        if (IsDisposed || _webView.CoreWebView2 is null) return;
        _ = _webView.ExecuteScriptAsync($"window.bcodeViewer && window.bcodeViewer.showToast({JsonSerializer.Serialize(text)}, {ms})");
    }

    private void PushUpdateLabel()
    {
        if (IsDisposed || _webView.CoreWebView2 is null) return;
        var label = _pendingUpdate is { Relaunch: false } ? "Cập nhật BcodeViewer — sẽ cài khi đóng..."
            : _newBuild is { } b ? $"Cập nhật BcodeViewer — có bản mới {b.Label}..." : "";
        _ = _webView.ExecuteScriptAsync($"window.bcodeShell && window.bcodeShell.setUpdateLabel && window.bcodeShell.setUpdateLabel({JsonSerializer.Serialize(label)})");
    }

    /// <summary>Lần cập nhật / quay lại vừa chạy (trước khi mở Viewer này): thành công → thông báo; lỗi → hộp báo kèm đường dẫn nhật ký.</summary>
    private void ReportLastUpdate()
    {
        if (UpdateCheckService.TakeResult() is not { } r) return;
        var what = r.Mode == "rollback" ? "Quay lại bản trước" : "Cập nhật BcodeViewer";
        if (r.Ok)
        {
            UpdateNotice(r.Mode == "rollback" ? $"Đã quay lại bản trước ({r.To})." : $"Đã cập nhật BcodeViewer lên {r.To}. Có lỗi thì Help → Cập nhật BcodeViewer → Quay lại bản trước.", 8000);
            return;
        }
        BeginInvoke(() => MessageBox.Show(this, $"{what} không thành công, BcodeViewer vẫn là bản cũ.\n\n{r.Message}\n\nNhật ký: {UpdateCheckService.LogPath}",
            "BcodeViewer — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    private void CheckForUpdateInteractive()
    {
        bool Choose() { var before = ReleaseFolder; ChooseReleaseFolder(); return !string.Equals(before, ReleaseFolder, StringComparison.OrdinalIgnoreCase); }
        _updateToast?.Clear();
        using var f = new AppUpdateForm(_webEnv, UI.ThemeManager.Current.IsDark, () => ReleaseFolder, Choose, _pendingUpdate is { Relaunch: false });
        var res = f.ShowDialog(this);
        if (res == DialogResult.Abort) { _pendingUpdate = null; PushUpdateLabel(); UpdateNotice("Đã huỷ hẹn cài bản mới khi đóng BcodeViewer."); }
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
                Close();   // OnFormClosing hỏi lưu file chưa lưu; huỷ → OnCloseAborted bỏ hẹn
                break;
            case UpdateDialogLogic.Outcome.ApplyOnClose when f.Plan is not null:
                _pendingUpdate = (f.Plan, null, f.OverwriteModified, false);
                PushUpdateLabel();
                UpdateNotice($"Bản mới {f.Plan.ToLabel} sẽ được cài khi bạn đóng BcodeViewer.");
                break;
            case UpdateDialogLogic.Outcome.Rollback when f.Backup is not null:
                _pendingUpdate = (null, f.Backup, false, true);
                Close();
                break;
        }
    }

    /// <summary>Đóng bị huỷ: "Khởi động lại &amp; cập nhật" / "Quay lại bản trước" không còn hiệu lực (hẹn "cài khi đóng" thì giữ).</summary>
    private void OnCloseAborted()
    {
        if (_pendingUpdate is not { Relaunch: true }) return;
        _pendingUpdate = null;
        UpdateNotice("Chưa cài vì BcodeViewer chưa đóng. Mở lại Help → Cập nhật BcodeViewer khi sẵn sàng.");
    }

    /// <summary>OnFormClosed: chạy script cài / quay lại (tiến trình riêng — tự chờ mọi BcodeViewer chạy từ thư mục này thoát rồi mới chép).</summary>
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
            MessageBox.Show("Không chạy được trình cập nhật: " + ex.Message, "BcodeViewer — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ChooseReleaseFolder()
    {
        var text = AppUpdateForm.Prompt(this, "Thư mục phát hành bản BcodeViewer mới", UpdateCheckService.FolderPromptHint + ":", ReleaseFolder, UI.ThemeManager.Current.IsDark);
        if (text is null) return;
        _settings.ReleaseFolder = UpdateCheckService.ToSetting(text);
        try { _settings.Save(); } catch { /* lưu lỗi: chỉ mất cấu hình này */ }
        _newBuild = null; _updateToast?.Clear();
        PushUpdateLabel();
    }
}
