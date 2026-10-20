using Bcode.App.Controls;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// Thông báo y/c FSG cho lập trình: chuông 🔔 + số chưa xem trên thanh trên (topbar.html / NativeTopBar), khung Thông báo (<see cref="FsgNotifyForm"/>),
/// toast Windows khi Bcode không ở trước. Kéo nền + so sánh ở <see cref="FsgNotifyService"/>; bấm 1 thông báo → tab Yêu cầu FSG mở khung chi tiết y/c đó.
/// </summary>
public partial class MainForm
{
    private FsgNotifyService? _fsgNotify;
    private FsgNotifyForm? _fsgNotifyForm;
    private NotifyIcon? _fsgToastIcon;

    /// <summary>Khởi động dịch vụ kéo nền (gọi 1 lần lúc cửa sổ hiện).</summary>
    private void InitFsgNotify()
    {
        if (_fsgNotify is not null) return;
        _fsgNotify = new FsgNotifyService(() => _settings.NoteProgrammer ?? "", () => LicenseService.IsUnlocked);
        _fsgNotify.Changed += () => UiPost(PushNotifyCount);
        _fsgNotify.NewItems += items => UiPost(() => ShowFsgToast(items));
        _fsgNotify.Start();
        Disposed += (_, _) => { _fsgNotify?.Dispose(); _fsgToastIcon?.Dispose(); };
        PushNotifyCount();
    }

    private void UiPost(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { /* đang đóng */ }
    }

    /// <summary>Đẩy số thông báo chưa xem lên chuông của thanh trên (Web: window.setNotify, App: NativeTopBar.SetNotifyCount).</summary>
    private void PushNotifyCount()
    {
        var n = _fsgNotify?.UnseenCount ?? 0;
        if (_topNative is not null) { _topNative.SetNotifyCount(n); return; }
        if (_topBarWeb.CoreWebView2 is null) return;
        _ = _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setNotify && window.setNotify({n})");
    }

    /// <summary>Bấm chuông (hoặc phím app.fsgNotify): mở / đóng khung Thông báo, đặt ở góc phải ngay dưới thanh trên.</summary>
    private void OpenFsgNotifyPanel()
    {
        InitFsgNotify();
        if (_fsgNotifyForm is { IsDisposed: false } open) { open.Close(); return; }
        var f = new FsgNotifyForm(_fsgNotify!, () => _settings.NoteProgrammer ?? "", SetNoteProgrammer);
        f.OpenRequested += OpenFsgRequirementFromNotify;
        f.FormClosed += (_, _) => { if (_fsgNotifyForm == f) _fsgNotifyForm = null; };
        var top = PointToScreen(new Point(0, _headerContainer?.Bottom ?? 0));
        var right = PointToScreen(new Point(ClientSize.Width, 0)).X;
        var work = Screen.FromControl(this).WorkingArea;
        f.Width = Math.Min(f.Width, work.Width - 16);
        f.Height = Math.Min(f.Height, work.Height - 16);
        f.Location = new Point(Math.Max(work.Left, Math.Min(right - f.Width - 12, work.Right - f.Width)), Math.Max(work.Top, Math.Min(top.Y + 4, work.Bottom - f.Height)));
        _fsgNotifyForm = f;
        f.Show(this);
    }

    private void SetNoteProgrammer(string programmer)
    {
        if (_settings.NoteProgrammer == programmer) return;
        _settings.NoteProgrammer = programmer;
        try { _settings.Save(); } catch { /* không lưu được thì chỉ phải nhập lại */ }
    }

    /// <summary>Bấm 1 thông báo: mở tab Yêu cầu FSG + khung chi tiết của y/c (giữ nút 🤖 AI tạo danh mục).</summary>
    private void OpenFsgRequirementFromNotify(string maDa, string fcode1)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
        Activate();
        OpenFsgRequirementReportTab();
        if (_fsgReqTab is not null && FindDescendant<FsgRequirementReportControl>(_fsgReqTab) is { } fsg) fsg.ShowRequirement(maDa, fcode1);
    }

    private static readonly Dictionary<string, string> FsgKindText = new() { ["note"] = "Ghi chú mới", ["status"] = "Đổi trạng thái", ["new"] = "Y/c mới giao", ["due"] = "Đổi hạn HT" };

    /// <summary>Toast Windows (balloon của NotifyIcon — Windows 10/11 hiện thành thông báo) khi Bcode không ở trước; bấm = mở khung Thông báo.</summary>
    private void ShowFsgToast(IReadOnlyList<FsgNotifyService.Item> items)
    {
        if (_fsgNotify is null || items.Count == 0 || !_fsgNotify.CurrentSettings.Toast) return;
        if (Form.ActiveForm is not null && WindowState != FormWindowState.Minimized) return;   // Bcode đang ở trước: chuông là đủ
        if (_fsgToastIcon is null)
        {
            _fsgToastIcon = new NotifyIcon { Icon = Icon ?? SystemIcons.Information, Text = "Bcode — Thông báo y/c FSG" };
            _fsgToastIcon.BalloonTipClicked += (_, _) =>
            {
                _fsgToastIcon.Visible = false;
                if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Maximized;
                Activate();
                if (_fsgNotifyForm is not { IsDisposed: false }) OpenFsgNotifyPanel();
            };
            _fsgToastIcon.BalloonTipClosed += (_, _) => _fsgToastIcon.Visible = false;
            _fsgToastIcon.Click += (_, _) => { _fsgToastIcon.Visible = false; Activate(); OpenFsgNotifyPanel(); };
        }
        var body = string.Join("\n", items.Take(3).Select(i =>
        {
            var t = i.Text.Replace("\r", " ").Replace("\n", " ");
            return $"{i.MaDa} · {i.Fcode1}: {(FsgKindText.TryGetValue(i.Kind, out var k) ? k : i.Kind)}{(i.Who.Length > 0 ? " [" + i.Who + "]" : "")} — {(t.Length > 70 ? t[..70] + "…" : t)}";
        })) + (items.Count > 3 ? $"\n… và {items.Count - 3} thông báo khác" : "");
        _fsgToastIcon.Visible = true;
        _fsgToastIcon.ShowBalloonTip(10000, $"Bcode — {items.Count} thông báo y/c FSG", body.Length > 250 ? body[..250] + "…" : body, ToolTipIcon.Info);
    }
}
