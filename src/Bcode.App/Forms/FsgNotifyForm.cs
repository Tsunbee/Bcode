using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// Khung "Thông báo y/c FSG" (bấm chuông 🔔 trên thanh trên — cả chế độ Web lẫn App): danh sách thông báo của <see cref="FsgNotifyService"/>
/// (mã YC, menu, loại thay đổi, ai ghi, nội dung mới, giờ phát hiện), đánh dấu đã xem / tất cả, cài đặt (bật/tắt, chu kỳ, loại thay đổi, toast).
/// Bấm 1 dòng → <see cref="OpenRequested"/> (MainForm mở tab Yêu cầu FSG + khung chi tiết y/c đó) rồi đóng khung. Trang: Web/Shell/fsgnotify.html.
/// </summary>
public sealed class FsgNotifyForm : WebDialogForm
{
    private readonly FsgNotifyService _svc;
    private readonly Func<string> _getProgrammer;
    private readonly Action<string> _setProgrammer;

    /// <summary>Bấm 1 thông báo: (mã dự án, mã YC).</summary>
    public event Action<string, string>? OpenRequested;

    public FsgNotifyForm(FsgNotifyService svc, Func<string> getProgrammer, Action<string> setProgrammer)
        : base("Thông báo y/c FSG", "fsgnotify.html", 820, 560, 520, 300)
    {
        _svc = svc;
        _getProgrammer = getProgrammer;
        _setProgrammer = setProgrammer;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        _svc.Changed += Push;
        FormClosed += (_, _) => _svc.Changed -= Push;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private void Push()
    {
        if (IsDisposed || !Web.IsReady) return;
        Js($"fsgNotify.load({J(_svc.Snapshot())})");
    }

    protected override void OnReady() => Push();

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        switch (action)
        {
            case "ready": Push(); break;
            case "poll": await _svc.PollAsync(force: true); break;
            case "seen":
                if (msg.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
                    _svc.MarkSeen(ids.EnumerateArray().Select(x => x.GetString() ?? ""));
                break;
            case "seenAll": _svc.MarkAllSeen(); break;
            case "clearSeen": _svc.ClearSeen(); break;
            case "settings":
            {
                var old = _svc.CurrentSettings;
                var s = msg.TryGetProperty("settings", out var sp) ? sp.Deserialize<FsgNotifyService.Settings>(JsonOpts) ?? old : old;
                var prog = msg.TryGetProperty("programmer", out var pp) ? (pp.GetString() ?? "").Trim() : _getProgrammer();
                var progChanged = !string.Equals(prog, (_getProgrammer() ?? "").Trim(), StringComparison.Ordinal);
                if (progChanged) _setProgrammer(prog);
                _svc.SaveSettings(s);
                // Vừa bật lại / đổi người / đổi phạm vi → kiểm tra ngay, không chờ hết chu kỳ.
                if (s.Enabled && (progChanged || !old.Enabled || old.Deployer != s.Deployer)) _ = _svc.PollAsync(force: true);
                break;
            }
            case "open":
            {
                var id = msg.TryGetProperty("id", out var ip) ? ip.GetString() ?? "" : "";
                if (_svc.Find(id) is not { } it) break;
                _svc.MarkSeen(new[] { id });
                BeginInvoke(() => { OpenRequested?.Invoke(it.MaDa, it.Fcode1); Close(); });
                break;
            }
        }
    }
}
