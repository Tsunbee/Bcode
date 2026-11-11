using Bcode.App.Forms;
using Bcode.App.Services;
using WinMessageBox = System.Windows.Forms.MessageBox;

namespace Bcode.App.UI;

/// <summary>
/// Hộp thông báo / xác nhận theo theme Bcode — cùng API và cùng kết quả trả về với System.Windows.Forms.MessageBox (tiêu đề, nội dung nhiều dòng,
/// icon Info / Warning / Error / Question, nút OK / OKCancel / YesNo / YesNoCancel / RetryCancel / AbortRetryIgnore, nút mặc định).
/// Chế độ Web: trang msgbox.html trong WebView2; chế độ App: trang native MsgBoxPage — xem <see cref="MessageBoxForm"/>. Esc / Enter / chữ tắt (Y, N, C...) /
/// mũi tên / Tab như MessageBox; Ctrl+C không bôi đen = chép cả hộp; đường dẫn dài xuống dòng ở mọi ký tự; tự đổi màu khi đổi sáng / tối.
/// <para>Mọi <c>MessageBox.Show(...)</c> trong code Bcode.App (namespace Bcode.App.*) đã trỏ về đây nhờ lớp <see cref="Bcode.App.MessageBox"/> (cùng tên, ở namespace cha
/// nên C# chọn nó trước System.Windows.Forms) — KHÔNG phải sửa từng chỗ gọi. Cần hộp gốc của Windows thì viết đủ <c>System.Windows.Forms.MessageBox.Show</c>.</para>
/// <para>Tự rơi về MessageBox của Windows khi: gọi từ luồng không phải luồng giao diện (luồng nền / MTA — hộp WinForms + WebView2 không dựng được ở đó, và
/// Invoke về luồng chính có thể treo nếu luồng chính đang chờ luồng này); owner thuộc luồng khác; có MessageBoxOptions (ServiceNotification, DefaultDesktopOnly...);
/// WebView2 không nạp được trang trong 6 giây (thiếu / hỏng WebView2 Runtime); hoặc dựng hộp bị lỗi.</para>
/// </summary>
public static class ThemedMessageBox
{
    public static DialogResult Show(string? text) => Show(null, text, "", MessageBoxButtons.OK, MessageBoxIcon.None);
    public static DialogResult Show(string? text, string? caption) => Show(null, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
    public static DialogResult Show(string? text, string? caption, MessageBoxButtons buttons) => Show(null, text, caption, buttons, MessageBoxIcon.None);
    public static DialogResult Show(string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon) => Show(null, text, caption, buttons, icon);
    public static DialogResult Show(string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton) => Show(null, text, caption, buttons, icon, defaultButton);
    public static DialogResult Show(string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton, MessageBoxOptions options) => Show(null, text, caption, buttons, icon, defaultButton, options);
    public static DialogResult Show(IWin32Window? owner, string? text) => Show(owner, text, "", MessageBoxButtons.OK, MessageBoxIcon.None);
    public static DialogResult Show(IWin32Window? owner, string? text, string? caption) => Show(owner, text, caption, MessageBoxButtons.OK, MessageBoxIcon.None);
    public static DialogResult Show(IWin32Window? owner, string? text, string? caption, MessageBoxButtons buttons) => Show(owner, text, caption, buttons, MessageBoxIcon.None);

    public static DialogResult Show(IWin32Window? owner, string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1, MessageBoxOptions options = 0)
    {
        text ??= "";
        caption ??= "";
        if (options != 0 || !OnUiThread(owner, out var ownerForm))
            return Native(owner, text, caption, buttons, icon, defaultButton, options);
        Notice(text, caption, icon);
        try
        {
            using var dlg = new MessageBoxForm(text, caption, buttons, icon, defaultButton, inTaskbar: ownerForm is null);
            var result = ownerForm is null ? dlg.ShowDialog() : dlg.ShowDialog(ownerForm);
            if (!dlg.Fallback) return result;
        }
        catch (Exception ex) { ErrorLog.Record(ex, "Hộp thông báo theo theme"); }
        return Native(ownerForm ?? owner, text, caption, buttons, icon, defaultButton, options);
    }

    /// <summary>Hộp của Windows (đường lùi) — hook WH_CBT của ErrorReporter vẫn theo dõi như trước.</summary>
    private static DialogResult Native(IWin32Window? owner, string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton def, MessageBoxOptions options)
    {
        try
        {
            return owner is null
                ? WinMessageBox.Show(text, caption, buttons, icon, def, options)
                : WinMessageBox.Show(owner, text, caption, buttons, icon, def, options);
        }
        catch (ArgumentException) when (owner is not null)
        {
            // owner + ServiceNotification / DefaultDesktopOnly không đi cùng nhau (Windows từ chối) — bỏ owner
            return WinMessageBox.Show(text, caption, buttons, icon, def, options);
        }
    }

    /// <summary>Đang ở luồng giao diện (STA, owner cùng luồng) — tìm form làm owner: form chứa control được truyền, không có thì form đang active / form hiện cuối cùng.</summary>
    private static bool OnUiThread(IWin32Window? owner, out Form? form)
    {
        form = null;
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return false;
        Control? c = owner as Control;
        if (c is null && owner is not null) { try { c = Control.FromHandle(owner.Handle); } catch { c = null; } }
        if (c is not null)
        {
            if (c.IsDisposed) c = null;
            else if (c.InvokeRequired) return false;
        }
        form = (c as Form)?.TopLevelControl as Form ?? c?.FindForm()?.TopLevelControl as Form;
        if (form is null && owner is null)
        {
            try { form = Form.ActiveForm ?? Application.OpenForms.Cast<Form>().LastOrDefault(f => f.Visible && !f.IsDisposed && f.TopLevel); }
            catch { form = null; }
            if (form is not null && form.InvokeRequired) form = null;
        }
        if (form is { IsDisposed: true } or { IsHandleCreated: false } or { Visible: false }) form = null;
        return true;
    }

    /// <summary>Thay việc hook WH_CBT đọc chữ MessageBox: chữ trùng thông điệp 1 exception vừa ném → ghi exception đó; icon lỗi mà không khớp → ghi chữ.</summary>
    private static void Notice(string text, string caption, MessageBoxIcon icon)
    {
        if (ErrorReporter.Quiet || text.Length == 0) return;
        try
        {
            var source = "Hộp thông báo" + (caption.Length > 0 ? ": " + caption : "");
            if (ErrorLog.NoticeDisplayed(text, source)) return;
            if (icon == MessageBoxIcon.Error) ErrorLog.RecordText(text, source + " (biểu tượng lỗi)");
        }
        catch { /* ghi log lỗi không được thì vẫn hiện hộp */ }
    }
}
