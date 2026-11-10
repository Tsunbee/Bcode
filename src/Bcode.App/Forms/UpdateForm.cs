using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// Hộp "Cập nhật Bcode" (Settings → Kiểm tra bản Bcode mới; Web/Shell/update.html): so bản đang chạy với thư mục phát hành, hiện "Có gì mới",
/// tải trước các file khác về máy (thanh tiến độ, huỷ được), rồi người dùng chọn Khởi động lại &amp; cập nhật / Cài khi đóng Bcode; có "Quay lại bản trước".
/// Form chỉ quyết định — việc cài do MainForm làm lúc đóng (MainForm.Update.cs) qua <see cref="UpdateCheckService.StartApply"/>.
/// </summary>
public sealed class UpdateForm : WebDialogForm
{
    public enum Outcome { None, ApplyNow, ApplyOnClose, Rollback }

    public Outcome Decision { get; private set; }
    public UpdatePlan? Plan { get; private set; }
    public BackupInfo? Backup { get; private set; }
    public bool OverwriteModified { get; private set; }

    private readonly Func<string> _folder;
    private readonly Func<bool> _chooseFolder;
    private readonly bool _pendingOnClose;
    private BuildStamp? _remote;
    private List<ChangeEntry> _changes = new();
    private CancellationTokenSource? _cts;

    /// <param name="folder">Thư mục phát hành đang dùng (đọc lại mỗi lần kiểm tra).</param>
    /// <param name="chooseFolder">Mở hộp đổi thư mục phát hành; true = đã đổi.</param>
    /// <param name="pendingOnClose">Đã hẹn cài khi đóng Bcode (hiện nhắc + cho huỷ hẹn).</param>
    public UpdateForm(Func<string> folder, Func<bool> chooseFolder, bool pendingOnClose)
        : base("Cập nhật Bcode", "update.html", 680, 640, 480, 420)
    {
        _folder = folder;
        _chooseFolder = chooseFolder;
        _pendingOnClose = pendingOnClose;
        FormClosing += (_, _) => _cts?.Cancel();
    }

    private static object StampView(BuildStamp? s) => s is null ? null! : new
    {
        version = s.Version, build = s.Build, when = s.When, commit = s.HasCommit ? s.Commit : "", label = s.Label,
    };

    private void Push(string phase, string? message = null, object? progress = null)
    {
        var folder = _folder();
        Backup = UpdateCheckService.ReadBackup();
        var state = new
        {
            phase,
            message,
            local = StampView(UpdateCheckService.Local),
            remote = StampView(_remote),
            changes = _changes.Select(c => new { hash = c.Hash, date = c.Date, text = c.Text }),
            folder,
            isDefault = folder.Length > 0 && string.Equals(folder.TrimEnd('\\'), UpdateCheckService.DefaultReleaseFolder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase),
            plan = Plan is null ? null : new { files = Plan.Copy.Count, bytes = Plan.Bytes, modified = Plan.Modified, deletes = Plan.Delete.Count, verified = Plan.Verified },
            backup = Backup is null ? null : new { from = Backup.From, at = Backup.At.ToLocalTime().ToString("dd/MM/yyyy HH:mm") },
            pendingOnClose = _pendingOnClose,
            progress,
        };
        Js($"window.update && window.update.set({J(state)})");
    }

    protected override void OnReady() => _ = CheckAsync();

    private async Task CheckAsync()
    {
        _cts?.Cancel();
        Plan = null; _remote = null; _changes = new();
        var folder = _folder();
        if (string.IsNullOrWhiteSpace(folder)) { Push("nofolder"); return; }
        Push("checking");
        var (remote, error, noRelease) = await UpdateCheckService.CheckDetailedAsync(folder);
        if (IsDisposed) return;
        if (remote is null) { Push(noRelease ? "norelease" : "error", error ?? "Không đọc được thư mục phát hành."); return; }
        _remote = remote;
        var local = UpdateCheckService.Local;
        if (!UpdateCheckService.IsNewer(remote, local)) { Push("latest"); return; }
        _changes = UpdateCheckService.NewSince(await UpdateCheckService.ReadChangelogAsync(folder), local);
        if (IsDisposed) return;
        if (UpdateCheckService.SameFolder(folder)) { Push("error", "Thư mục phát hành trùng (hoặc nằm trong) thư mục Bcode đang chạy — không tự chép được. Đổi thư mục phát hành khác."); return; }
        Plan = UpdateCheckService.LoadPlan(folder, remote);
        Push(Plan is null ? "available" : "ready");
    }

    private async Task DownloadAsync()
    {
        if (_remote is null) return;
        var folder = _folder();
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var started = Stopwatch.StartNew();
        var progress = new Progress<UpdateProgress>(p =>
        {
            if (cts.IsCancellationRequested || IsDisposed) return;
            var speed = p.Stage == "download" && started.Elapsed.TotalSeconds > 0.5 ? (long)(p.Bytes / started.Elapsed.TotalSeconds) : 0;
            Push("downloading", null, new { stage = p.Stage, done = p.Done, total = p.Total, bytes = p.Bytes, totalBytes = p.TotalBytes, file = p.File, speed });
        });
        Push("downloading", null, new { stage = "scan", done = 0, total = 0, bytes = 0, totalBytes = 0, file = "", speed = 0 });
        try
        {
            Plan = await UpdateCheckService.PrepareAsync(folder, _remote, progress, cts.Token);
            if (!IsDisposed) Push("ready");
        }
        catch (OperationCanceledException) { if (!IsDisposed) Push("available", "Đã dừng tải."); }
        catch (Exception ex) { if (!IsDisposed) Push("failed", ex.Message); }
    }

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        switch (action)
        {
            case "check":
                await CheckAsync();
                break;
            case "download":
                await DownloadAsync();
                break;
            case "stop":
                _cts?.Cancel();
                break;
            case "apply":
                if (Plan is null) return;
                OverwriteModified = msg.TryGetProperty("overwrite", out var o) && o.ValueKind == JsonValueKind.True;
                Decision = msg.TryGetProperty("when", out var w) && w.GetString() == "close" ? Outcome.ApplyOnClose : Outcome.ApplyNow;
                CloseWith(DialogResult.OK);
                break;
            case "unschedule":
                Decision = Outcome.None;
                CloseWith(DialogResult.Abort);   // MainForm: Abort = huỷ hẹn cài khi đóng
                break;
            case "rollback":
                if (Backup is null) return;
                Decision = Outcome.Rollback;
                CloseWith(DialogResult.OK);
                break;
            case "folder":
                if (_chooseFolder()) await CheckAsync();
                break;
            case "log":
                var log = UpdateCheckService.LogPath;
                if (File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
                else Js("window.update && window.update.toast('Chưa có nhật ký cập nhật.')");
                break;
        }
    }
}
