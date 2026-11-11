using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace Bcode.Shared;

/// <summary>
/// Phần xử lý của hộp "Cập nhật &lt;app&gt;" (trang update.html) — dùng chung cho Bcode (Forms/UpdateForm.cs) và BcodeViewer (UpdateForm.cs của Viewer):
/// so bản đang chạy với thư mục phát hành, "Có gì mới", tải trước các file khác về máy (tiến độ, huỷ được), rồi người dùng chọn
/// Khởi động lại &amp; cập nhật / Cài khi đóng / Quay lại bản trước. Chỉ quyết định — việc cài do cửa sổ chính làm lúc đóng
/// (<see cref="UpdateCheckService.StartApply"/>). Form chứa chỉ cần chuyển action của trang vào <see cref="HandleAsync"/>.
/// </summary>
public sealed class UpdateDialogLogic
{
    public enum Outcome { None, ApplyNow, ApplyOnClose, Rollback }

    public Outcome Decision { get; private set; }
    public UpdatePlan? Plan { get; private set; }
    public BackupInfo? Backup { get; private set; }
    public bool OverwriteModified { get; private set; }

    private readonly Func<string> _folder;
    private readonly Func<bool> _chooseFolder;
    private readonly bool _pendingOnClose;
    private readonly Action<string> _js;
    private readonly Action<DialogResult> _close;
    private readonly Func<bool> _gone;
    private readonly Func<string?>? _busyNote;
    private BuildStamp? _remote;
    private List<ChangeEntry> _changes = new();
    private CancellationTokenSource? _cts;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <param name="folder">Thư mục phát hành đang dùng (đọc lại mỗi lần kiểm tra).</param>
    /// <param name="chooseFolder">Mở hộp đổi thư mục phát hành; true = đã đổi.</param>
    /// <param name="pendingOnClose">Đã hẹn cài khi đóng app (hiện nhắc + cho huỷ hẹn).</param>
    /// <param name="js">Chạy 1 lệnh JS ở trang (tự chuyển về luồng UI).</param>
    /// <param name="close">Đóng hộp kèm kết quả (OK = có quyết định; Abort = huỷ hẹn cài khi đóng).</param>
    /// <param name="gone">Hộp đã đóng / huỷ.</param>
    /// <param name="busyNote">Nhắc thêm lúc sẵn sàng cài (vd "còn 2 cửa sổ BcodeViewer khác đang mở") — null = không có.</param>
    public UpdateDialogLogic(Func<string> folder, Func<bool> chooseFolder, bool pendingOnClose,
        Action<string> js, Action<DialogResult> close, Func<bool> gone, Func<string?>? busyNote = null)
    {
        _folder = folder; _chooseFolder = chooseFolder; _pendingOnClose = pendingOnClose;
        _js = js; _close = close; _gone = gone; _busyNote = busyNote;
    }

    /// <summary>Hộp đang đóng: dừng tải.</summary>
    public void Cancel() => _cts?.Cancel();

    /// <summary>Trang đã nạp: kiểm tra ngay.</summary>
    public void Ready() => _ = CheckAsync();

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
            app = UpdateCheckService.AppName,
            exe = UpdateCheckService.ExeName,
            publishApp = UpdateCheckService.PublishApp,
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
            busy = phase == "ready" ? _busyNote?.Invoke() : null,
            progress,
        };
        _js($"window.update && window.update.set({JsonSerializer.Serialize(state, JsonOpts)})");
    }

    private async Task CheckAsync()
    {
        _cts?.Cancel();
        Plan = null; _remote = null; _changes = new();
        var folder = _folder();
        if (string.IsNullOrWhiteSpace(folder)) { Push("nofolder"); return; }
        Push("checking");
        var (remote, error, noRelease) = await UpdateCheckService.CheckDetailedAsync(folder);
        if (_gone()) return;
        if (remote is null) { Push(noRelease ? "norelease" : "error", error ?? "Không đọc được thư mục phát hành."); return; }
        _remote = remote;
        var local = UpdateCheckService.Local;
        if (!UpdateCheckService.IsNewer(remote, local)) { Push("latest"); return; }
        _changes = UpdateCheckService.NewSince(await UpdateCheckService.ReadChangelogAsync(folder), local);
        if (_gone()) return;
        if (UpdateCheckService.SameFolder(folder))
        {
            Push("error", $"Thư mục phát hành trùng (hoặc nằm trong) thư mục {UpdateCheckService.AppName} đang chạy — không tự chép được. Đổi thư mục phát hành khác.");
            return;
        }
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
            if (cts.IsCancellationRequested || _gone()) return;
            var speed = p.Stage == "download" && started.Elapsed.TotalSeconds > 0.5 ? (long)(p.Bytes / started.Elapsed.TotalSeconds) : 0;
            Push("downloading", null, new { stage = p.Stage, done = p.Done, total = p.Total, bytes = p.Bytes, totalBytes = p.TotalBytes, file = p.File, speed });
        });
        Push("downloading", null, new { stage = "scan", done = 0, total = 0, bytes = 0, totalBytes = 0, file = "", speed = 0 });
        try
        {
            Plan = await UpdateCheckService.PrepareAsync(folder, _remote, progress, cts.Token);
            if (!_gone()) Push("ready");
        }
        catch (OperationCanceledException) { if (!_gone()) Push("available", "Đã dừng tải."); }
        catch (Exception ex) { if (!_gone()) Push("failed", ex.Message); }
    }

    /// <summary>Action trang gửi lên (trừ "close" / "cancel" — form tự đóng).</summary>
    public async Task HandleAsync(string action, JsonElement msg)
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
                _close(DialogResult.OK);
                break;
            case "unschedule":
                Decision = Outcome.None;
                _close(DialogResult.Abort);   // cửa sổ chính: Abort = huỷ hẹn cài khi đóng
                break;
            case "rollback":
                if (Backup is null) return;
                Decision = Outcome.Rollback;
                _close(DialogResult.OK);
                break;
            case "folder":
                if (_chooseFolder()) await CheckAsync();
                break;
            case "log":
                var log = UpdateCheckService.LogPath;
                if (File.Exists(log)) Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
                else _js("window.update && window.update.toast('Chưa có nhật ký cập nhật.')");
                break;
        }
    }
}
