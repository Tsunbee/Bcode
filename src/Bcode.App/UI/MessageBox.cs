namespace Bcode.App;

/// <summary>
/// Cùng tên với System.Windows.Forms.MessageBox và nằm ở namespace cha của mọi code Bcode.App (Bcode.App.Controls / Forms / Services / UI...) nên
/// C# chọn lớp này trước MessageBox của WinForms (global using) → mọi <c>MessageBox.Show(...)</c> có sẵn hiện hộp theo theme (<see cref="UI.ThemedMessageBox"/>)
/// mà không sửa từng chỗ gọi, kết quả trả về y như cũ. Cần hộp gốc của Windows: viết đủ <c>System.Windows.Forms.MessageBox.Show</c>.
/// Internal: BcodeNote (InternalsVisibleTo, có <c>using Bcode.App;</c>) gọi thẳng <see cref="UI.ThemedMessageBox"/> để không nhập nhằng tên.
/// </summary>
internal static class MessageBox
{
    public static DialogResult Show(string? text) => UI.ThemedMessageBox.Show(text);
    public static DialogResult Show(string? text, string? caption) => UI.ThemedMessageBox.Show(text, caption);
    public static DialogResult Show(string? text, string? caption, MessageBoxButtons buttons) => UI.ThemedMessageBox.Show(text, caption, buttons);
    public static DialogResult Show(string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon) => UI.ThemedMessageBox.Show(text, caption, buttons, icon);
    public static DialogResult Show(string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton) => UI.ThemedMessageBox.Show(text, caption, buttons, icon, defaultButton);
    public static DialogResult Show(string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton, MessageBoxOptions options) => UI.ThemedMessageBox.Show(text, caption, buttons, icon, defaultButton, options);
    public static DialogResult Show(IWin32Window? owner, string? text) => UI.ThemedMessageBox.Show(owner, text);
    public static DialogResult Show(IWin32Window? owner, string? text, string? caption) => UI.ThemedMessageBox.Show(owner, text, caption);
    public static DialogResult Show(IWin32Window? owner, string? text, string? caption, MessageBoxButtons buttons) => UI.ThemedMessageBox.Show(owner, text, caption, buttons);
    public static DialogResult Show(IWin32Window? owner, string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon) => UI.ThemedMessageBox.Show(owner, text, caption, buttons, icon);
    public static DialogResult Show(IWin32Window? owner, string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton) => UI.ThemedMessageBox.Show(owner, text, caption, buttons, icon, defaultButton);
    public static DialogResult Show(IWin32Window? owner, string? text, string? caption, MessageBoxButtons buttons, MessageBoxIcon icon, MessageBoxDefaultButton defaultButton, MessageBoxOptions options) => UI.ThemedMessageBox.Show(owner, text, caption, buttons, icon, defaultButton, options);
}
