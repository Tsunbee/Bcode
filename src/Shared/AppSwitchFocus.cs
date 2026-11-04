using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.Shared;

/// <summary>
/// Alt+Tab (hoặc bấm taskbar) sang ứng dụng khác rồi quay lại: trả focus về đúng chỗ đang đứng trước đó — editor Monaco giữ nguyên con trỏ.
/// <para>Vì sao cần: bấm chuột vào trang WebView2 thì focus nằm ở cửa sổ con của tiến trình trình duyệt, cửa sổ vỏ WinForms của WebView2 không nhận
/// WM_SETFOCUS nên <see cref="ContainerControl.ActiveControl"/> của form KHÔNG đổi theo. Khi cửa sổ được kích hoạt lại, WinForms trả focus cho
/// ActiveControl cũ (cây, thanh trên, WebView2 khác...) hoặc chỉ cho vỏ WebView2 mà lõi trình duyệt không nhận → mất con trỏ, gõ phím không vào editor.</para>
/// <para>Cách làm: lúc form mất kích hoạt (WM_ACTIVATE = WA_INACTIVE) nhớ control đang thật sự giữ focus (GetFocus → <see cref="Control.FromChildHandle"/>);
/// lúc CẢ ỨNG DỤNG được kích hoạt lại (WM_ACTIVATEAPP = TRUE) mà form này đang là cửa sổ active thì trả focus cho control đó — WebView2 thì
/// <c>MoveFocus(Programmatic)</c> để trang tự trả focus cho phần tử đang focus trước (ô gõ của Monaco). Chuyển qua lại giữa các cửa sổ TRONG app
/// (mở / đóng hộp thoại) không đụng tới — code sau hộp thoại tự đặt focus như cũ. Bấm chuột vào cửa sổ để quay lại: lệnh trả focus chạy trước cú bấm
/// (message post xử lý trước message chuột) nên chỗ được bấm vẫn thắng.</para>
/// File này được LINK vào cả Bcode.App và BcodeViewer.App — sửa ở đây là cả hai cùng đổi.
/// </summary>
internal sealed class AppSwitchFocus : NativeWindow
{
    private const int WM_ACTIVATE = 0x0006, WM_ACTIVATEAPP = 0x001C, WA_INACTIVE = 0;

    [DllImport("user32.dll")] private static extern IntPtr GetFocus();
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);

    private readonly Form _form;
    private Control? _last;   // control của _form đang giữ focus lúc form mất kích hoạt lần gần nhất

    private AppSwitchFocus(Form form) => _form = form;

    /// <summary>Gắn cho 1 form chính (gọi 1 lần, trong constructor cũng được — tự gắn lại khi handle được tạo lại).</summary>
    public static void Attach(Form form)
    {
        var hook = new AppSwitchFocus(form);
        if (form.IsHandleCreated) hook.AssignHandle(form.Handle);
        form.HandleCreated += (_, _) => { hook.ReleaseHandle(); hook.AssignHandle(form.Handle); };
        form.HandleDestroyed += (_, _) => hook.ReleaseHandle();
    }

    protected override void WndProc(ref Message m)
    {
        try
        {
            if (m.Msg == WM_ACTIVATE && ((int)(long)m.WParam & 0xFFFF) == WA_INACTIVE) Remember();
            else if (m.Msg == WM_ACTIVATEAPP && m.WParam != IntPtr.Zero && _last is not null && _form.IsHandleCreated)
                _form.BeginInvoke(new Action(Restore));
        }
        catch { /* form đang đóng — bỏ qua, không chặn message */ }
        base.WndProc(ref m);
    }

    private void Remember()
    {
        var h = GetFocus();
        var c = h == IntPtr.Zero ? null : Control.FromChildHandle(h);
        // Focus đang ở cửa sổ khác của app (hộp thoại, popup) hay ở chính form (không control nào) → không có gì để trả.
        _last = c is not null && !ReferenceEquals(c, _form) && ReferenceEquals(c.FindForm(), _form) ? c : null;
    }

    private void Restore()
    {
        try
        {
            var c = _last;
            if (c is null || _form.IsDisposed || !ReferenceEquals(Form.ActiveForm, _form)) return;   // quay về hộp thoại của app chứ không phải form này
            if (c.IsDisposed || !c.IsHandleCreated || !c.Visible || !c.Enabled || !ReferenceEquals(c.FindForm(), _form)) return; // tab đã đóng / bị ẩn
            if (c is WebView2 web)
            {
                var focus = GetFocus();
                if (focus != IntPtr.Zero && IsChild(web.Handle, focus)) return;   // lõi trình duyệt đã có focus — để yên, không làm trang blur/focus
                if (focus != web.Handle) web.Focus();
                ControllerOf(web)?.MoveFocus(CoreWebView2MoveFocusReason.Programmatic);   // trang trả focus cho phần tử cũ (ô gõ Monaco) → con trỏ y chỗ cũ
            }
            else if (!c.ContainsFocus) c.Focus();
        }
        catch { /* control đang đóng */ }
    }

    /// <summary>CoreWebView2Controller của control WinForms — không công khai ở mọi bản SDK nên lấy qua reflection; null nếu chưa khởi tạo.</summary>
    private static CoreWebView2Controller? ControllerOf(WebView2 web)
    {
        var t = typeof(WebView2);
        const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
        return (t.GetProperty("CoreWebView2Controller", bf)?.GetValue(web) ?? t.GetField("_coreWebView2Controller", bf)?.GetValue(web)) as CoreWebView2Controller;
    }
}
