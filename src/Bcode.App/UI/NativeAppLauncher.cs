using System.Diagnostics;

namespace Bcode.App.UI;

/// <summary>
/// Một số định dạng file trong App_Data (*.rpt = Crystal Reports, *.xlsx = Excel) không phải là
/// text/script — BcodeViewer/ScriptEditorControl chỉ biết hiển thị nội dung dạng text (*.f, *.xml,
/// *.sql...), nên mở các file này trong đó chỉ ra nội dung rác. Thay vào đó, mở chúng bằng đúng ứng
/// dụng đã đăng ký với Windows cho phần mở rộng đó (Process.Start + UseShellExecute=true, giống hệt
/// double-click file đó trong Explorer).
/// </summary>
internal static class NativeAppLauncher
{
    private static readonly HashSet<string> NativeAppExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rpt",
        ".xlsx",
    };

    public static bool IsNativeAppFile(string path) =>
        NativeAppExtensions.Contains(Path.GetExtension(path));

    /// <summary>Nếu <paramref name="path"/> thuộc nhóm định dạng ở trên: mở bằng ứng dụng hỗ trợ
    /// của Windows (đã hiển thị lỗi cho người dùng nếu có) và trả về true — caller không nên mở nó
    /// bằng cách nào khác nữa. Trả về false nếu path không thuộc nhóm này (caller tự xử lý tiếp).</summary>
    public static bool TryOpenWithNativeApp(IWin32Window owner, string path)
    {
        if (!IsNativeAppFile(path)) return false;

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Không mở được file:\n{ex.Message}", "Bcode — Open File",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        return true;
    }
}
