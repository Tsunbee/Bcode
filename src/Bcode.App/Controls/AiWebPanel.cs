using Bcode.App.UI;
using Bcode.Shared;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Controls;

/// <summary>
/// Tab nhúng claude.ai hoặc gemini.google.com (WebView2) — giống sidebar Claude/Gemini của BcodeViewer. Toàn bộ phần khởi tạo trang
/// (profile đăng nhập, User-Agent, Client Hints, popup đăng nhập) và dò ô chat nằm ở <see cref="AiWebHelper"/>, dùng chung với BcodeViewer.
/// Co giãn theo màn hình bằng ZoomFactor = <see cref="UiScale.Factor"/> như các WebView2 khác của Bcode.
/// </summary>
internal sealed class AiWebPanel : UserControl
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AiSite Site { get; }

    public AiWebPanel(AiSite site)
    {
        Site = site;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        UiScale.Changed += ApplyZoom;
        Disposed += (_, _) => UiScale.Changed -= ApplyZoom;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _ = InitAsync();
    }

    /// <summary>Đưa focus bàn phím vào trang web (sau khi đổi tab / bấm phím tắt toàn cục focus bị trả về form nên gõ không vào ô chat).</summary>
    public void FocusWeb()
    {
        try { if (!_web.IsDisposed && _web.CoreWebView2 is not null) _web.Focus(); } catch { /* đang đóng */ }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) BeginInvoke(new Action(FocusWeb)); // tab được chọn lại → focus vào trang
    }

    private void ApplyZoom()
    {
        try { if (!_web.IsDisposed && _web.CoreWebView2 is not null) _web.ZoomFactor = UiScale.Factor; } catch { /* đang đóng */ }
    }

    private async Task InitAsync()
    {
        try
        {
            await AiWebHelper.InitAsync(_web, Site, BcodePaths.AppData);
            // Phím tắt toàn cục của Bcode (Ctrl+Tab, Ctrl+W, Ctrl+Shift+Q...) vẫn chạy khi focus nằm trong trang web.
            WebViewEnvironment.AttachGlobalShortcuts(_web);
            _web.CoreWebView2.NavigationCompleted += (_, _) => { if (_loaded.TrySetResult() && Visible && Form.ActiveForm == FindForm()) FocusWeb(); };
            ApplyZoom();
        }
        catch
        {
            // Không có WebView2 Runtime — để tab trống, không làm hỏng cửa sổ chính.
            _loaded.TrySetResult();
        }
    }

    /// <summary>Đưa text vào ô chat (chưa gửi) — chờ trang tải xong lần đầu (tối đa 30 giây) rồi mới dán. Chưa đăng nhập thì ô chat chưa có:
    /// script tự bỏ cuộc sau ~5 giây, đăng nhập xong bấm lại là được.</summary>
    public async Task InsertTextAsync(string intro, string content, string fallback, bool usePaste = true)
    {
        await Task.WhenAny(_loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (IsDisposed) return;
        _web.Focus();
        await AiWebHelper.InsertTextAsync(_web, intro, content, fallback, usePaste);
    }

    /// <summary>Nhập text nhiều dòng bằng phím thật (Shift+Enter giữa các dòng) — dùng cho Claude, xem <see cref="AiWebHelper.TypeTextAsync"/>. Trả về null nếu xong, hoặc lỗi.</summary>
    public async Task<string?> TypeTextAsync(string intro, string text)
    {
        await Task.WhenAny(_loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (IsDisposed) return null;
        _web.Focus();
        return await AiWebHelper.TypeTextAsync(_web, Site == AiSite.Claude ? "Claude" : "Gemini", intro, text);
    }

    /// <summary>Đính kèm file thật vào ô chat (Ctrl+V thật qua DevTools) — dùng cho nội dung dài vì dán cả khối chữ vào ô chat (nhất là Gemini) rất lag.
    /// Trả về null nếu xong, hoặc thông báo lỗi.</summary>
    public async Task<string?> AttachFileAsync(string filePath)
    {
        await Task.WhenAny(_loaded.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        if (IsDisposed) return null;
        _web.Focus();
        return await AiWebHelper.AttachFileAsync(_web, Site == AiSite.Claude ? "Claude" : "Gemini", filePath);
    }
}
