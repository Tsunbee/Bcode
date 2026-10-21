using System.Runtime.InteropServices;

namespace Bcode.App.Controls;

/// <summary>
/// TreeView chặn NullReferenceException nội bộ của WinForms trong <c>TreeView.CustomDraw</c> (NM_CUSTOMDRAW).
///
/// Cây của Bcode vẽ tay (OwnerDrawText, xem ThemeManager). Khi bấm "Áp dụng" trong Giao diện (Template), ThemeManager đổi
/// Font + ItemHeight của mọi cây; ItemHeight lẻ khiến WinForms tạo lại handle cây (RecreateHandle) và Windows vẽ lại ngay
/// trong lúc node đang được dựng lại / tô lại màu. Nếu đúng lúc đó Windows xin vẽ một dòng mà WinForms tra ra TreeNode đã
/// tách khỏi cây (node.TreeView == null), CustomDraw đọc <c>node.TreeView.Font</c> → NRE → hộp "Unhandled exception".
/// Lỗi chỉ ở một lượt vẽ: bỏ qua lượt đó (để Windows vẽ mặc định) rồi vẽ lại cây sau, thay vì làm sập app.
/// </summary>
public class SafeTreeView : TreeView
{
    private const int WM_REFLECT_NOTIFY = 0x2000 + 0x004E; // WM_REFLECT | WM_NOTIFY — WinForms chuyển NM_CUSTOMDRAW về chính cây
    private const int NM_CUSTOMDRAW = -12;
    private long _lastRecoverTick;

    protected override void WndProc(ref Message m)
    {
        try
        {
            base.WndProc(ref m);
        }
        catch (NullReferenceException ex) when (m.Msg == WM_REFLECT_NOTIFY && m.LParam != IntPtr.Zero
                                                && Marshal.ReadInt32(m.LParam, IntPtr.Size * 2) == NM_CUSTOMDRAW) // NMHDR.code
        {
            m.Result = IntPtr.Zero; // CDRF_DODEFAULT
            // Vẽ lại cây một lần sau khi xong lượt hiện tại (giới hạn 1 lần/giây để không lặp vẽ nếu lỗi còn kéo dài).
            var now = Environment.TickCount64;
            if (now - _lastRecoverTick > 1000 && IsHandleCreated && !IsDisposed)
            {
                _lastRecoverTick = now;
                BeginInvoke(new Action(() => { if (!IsDisposed) Invalidate(); }));
                Bcode.App.UI.ThemeManager.LogTiming($"SafeTreeView: bỏ qua NRE trong TreeView.CustomDraw ({Name}) — {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}");
            }
        }
    }
}
