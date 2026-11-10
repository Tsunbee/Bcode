using System.Text.Json;
using Bcode.Shared;
using Microsoft.Web.WebView2.Core;

namespace Bcode.ScreenDesigner;

/// <summary>Cập nhật Screen Designer — cơ chế RIÊNG, cùng lõi + cùng hộp với BcodeViewer (Shared/AppUpdate.cs, AppUpdateDialog.cs, AppUpdateForm.cs).
/// Designer thường nằm ở thư mục ScreenDesigner\ trong thư mục Bcode nhưng bản cập nhật của Bcode bỏ qua thư mục đó (AppUpdate: KeepDirs + thư mục con
/// có bcode-version.json riêng) — Designer tự cập nhật chính thư mục của nó. Thư mục phát hành: &lt;BcodeReleaseFolder&gt; của Bcode.ScreenDesigner.csproj,
/// đổi được trong hộp (lưu %AppData%\BcodeScreenDesigner\update.json). Thư mục tạm %LocalAppData%\Bcode\designer-update, nhật ký %AppData%\Bcode\designer-update.log.
/// Tự kiểm tra ~20 giây sau khi mở rồi mỗi 4 giờ; có bản mới thì nút "⬆ Cập nhật" trên thanh trên sáng lên + thông báo. Cài lúc Designer đã đóng.</summary>
public sealed partial class MainForm
{
    private CoreWebView2Environment? _webEnv;
    private BuildStamp? _newBuild;
    private (UpdatePlan? Plan, BackupInfo? Rollback, bool Overwrite, bool Relaunch)? _pendingUpdate;
    private bool _updateInit;
    private static readonly TimeSpan UpdateRecheck = TimeSpan.FromHours(4);

    private static string UpdateSettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BcodeScreenDesigner", "update.json");

    /// <summary>Thư mục phát hành khai ở máy này: "" = mặc định, "-" = tắt.</summary>
    private static string ReleaseSetting
    {
        get
        {
            try
            {
                if (!File.Exists(UpdateSettingsPath)) return "";
                using var doc = JsonDocument.Parse(File.ReadAllText(UpdateSettingsPath));
                return doc.RootElement.TryGetProperty("releaseFolder", out var v) ? v.GetString() ?? "" : "";
            }
            catch { return ""; }
        }
        set
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(UpdateSettingsPath)!);
                File.WriteAllText(UpdateSettingsPath, JsonSerializer.Serialize(new { releaseFolder = value }));
            }
            catch { /* lưu lỗi: chỉ mất cấu hình này */ }
        }
    }

    private static string ReleaseFolder => UpdateCheckService.EffectiveFolder(ReleaseSetting);

    private void InitUpdateCheck()
    {
        if (_updateInit) return;
        _updateInit = true;
        UpdateCheckService.Configure("Screen Designer", "designer-update", Bcode.App.BcodePaths.AppData, "BcodeScreenDesigner.exe", "Designer");
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
            PushUpdateState(null);
            ShowUpdateToast();
        };
        Disposed += (_, _) => timer.Dispose();
        timer.Start();
        PushUpdateState(null);
    }

    /// <summary>Thông báo "Có bản Screen Designer mới" không tự tắt, góc dưới phải (Shared/AppUpdateToast.cs): Xem = mở hộp Cập nhật; Để sau = nhắc lại sau 4 giờ.
    /// Màu theo designer.css (nền tối, nhấn cam).</summary>
    private AppUpdateToast? _updateToast;

    private AppUpdateToast UpdateToast => _updateToast ??= new AppUpdateToast(this,
        () => new ToastPalette(Color.FromArgb(0x2b, 0x2d, 0x31), Color.FromArgb(0xe3, 0xe5, 0xe8), Color.FromArgb(0x9a, 0xa0, 0xa8), Color.FromArgb(0xf5, 0xa6, 0x23), Color.FromArgb(0x3d, 0x40, 0x46)),
        () => BeginInvoke(CheckForUpdateInteractive));

    private static (string Title, string Detail) UpdateToastText(BuildStamp b) =>
        ($"Có bản Screen Designer mới — {(b.Version.Length > 0 ? "v" + b.Version + " " : "")}{(b.Build > 0 ? "build " + b.Build : b.When)}",
         "Bấm Xem để xem có gì mới và cập nhật. Tải về chạy nền, vẫn thiết kế bình thường.");

    private void ShowUpdateToast()
    {
        if (_newBuild is not { } b || _pendingUpdate is not null) return;
        var (t, d) = UpdateToastText(b);
        UpdateToast.Show(t, d);
    }

    /// <summary>Nút "⬆ Cập nhật" trên trang (designer.js window.setUpdateState): nhãn + sáng lên khi có bản mới; <paramref name="toast"/> = thông báo.</summary>
    private void PushUpdateState(string? toast)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        var state = new
        {
            label = _pendingUpdate is { Relaunch: false } ? "⬆ Cài khi đóng" : _newBuild is not null ? "⬆ Có bản mới" : "⬆ Cập nhật",
            hot = _newBuild is not null || _pendingUpdate is not null,
            title = _newBuild is { } b ? "Có bản Screen Designer mới: " + b.Label : "Kiểm tra bản Screen Designer mới (đang dùng " + UpdateCheckService.Local.Label + ")",
            toast,
        };
        _ = _web.ExecuteScriptAsync($"window.setUpdateState && window.setUpdateState({JsonSerializer.Serialize(state)})");
    }

    private void ReportLastUpdate()
    {
        if (UpdateCheckService.TakeResult() is not { } r) return;
        var what = r.Mode == "rollback" ? "Quay lại bản trước" : "Cập nhật Screen Designer";
        if (r.Ok)
        {
            PushUpdateState(r.Mode == "rollback" ? $"Đã quay lại bản trước ({r.To})." : $"Đã cập nhật Screen Designer lên {r.To}. Có lỗi thì ⬆ Cập nhật → Quay lại bản trước.");
            return;
        }
        BeginInvoke(() => MessageBox.Show(this, $"{what} không thành công, Screen Designer vẫn là bản cũ.\n\n{r.Message}\n\nNhật ký: {UpdateCheckService.LogPath}",
            "Screen Designer — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Warning));
    }

    /// <summary>Nút "⬆ Cập nhật" (DesignerBridge.BeginUpdate).</summary>
    internal void CheckForUpdateInteractive()
    {
        if (!_updateInit) InitUpdateCheck();
        bool Choose()
        {
            var before = ReleaseFolder;
            var text = AppUpdateForm.Prompt(this, "Thư mục phát hành bản Screen Designer mới", UpdateCheckService.FolderPromptHint + ":", ReleaseFolder, dark: true);
            if (text is null) return false;
            ReleaseSetting = UpdateCheckService.ToSetting(text);
            _newBuild = null; _updateToast?.Clear();
            PushUpdateState(null);
            return !string.Equals(before, ReleaseFolder, StringComparison.OrdinalIgnoreCase);
        }
        _updateToast?.Clear();
        using var f = new AppUpdateForm(_webEnv, dark: true, () => ReleaseFolder, Choose, _pendingUpdate is { Relaunch: false });
        var res = f.ShowDialog(this);
        if (res == DialogResult.Abort) { _pendingUpdate = null; PushUpdateState("Đã huỷ hẹn cài bản mới khi đóng Screen Designer."); }
        if (res != DialogResult.OK || f.Decision == UpdateDialogLogic.Outcome.None)
        {
            // đóng hộp mà chưa cập nhật: nhắc lại sau 4 giờ (thông báo cố định); nút ⬆ vẫn sáng
            if (_newBuild is { } nb && _pendingUpdate is null) { var (t, d) = UpdateToastText(nb); UpdateToast.Later(t, d); }
            return;
        }
        switch (f.Decision)
        {
            case UpdateDialogLogic.Outcome.ApplyNow when f.Plan is not null:
                _pendingUpdate = (f.Plan, null, f.OverwriteModified, true);
                Close();
                break;
            case UpdateDialogLogic.Outcome.ApplyOnClose when f.Plan is not null:
                _pendingUpdate = (f.Plan, null, f.OverwriteModified, false);
                PushUpdateState($"Bản mới {f.Plan.ToLabel} sẽ được cài khi bạn đóng Screen Designer.");
                break;
            case UpdateDialogLogic.Outcome.Rollback when f.Backup is not null:
                _pendingUpdate = (null, f.Backup, false, true);
                Close();
                break;
        }
    }

    /// <summary>FormClosed: chạy script cài / quay lại (tự chờ mọi Screen Designer chạy từ thư mục này thoát rồi mới chép).</summary>
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
            MessageBox.Show("Không chạy được trình cập nhật: " + ex.Message, "Screen Designer — Cập nhật", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
