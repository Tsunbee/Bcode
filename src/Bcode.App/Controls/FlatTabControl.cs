using System.Runtime.InteropServices;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// TabControl phẳng, vẽ hoàn toàn bằng code (kiểu thanh tab của BcodeViewer) cho vùng tài liệu của MainForm:
///  * <b>không nhấp nháy</b>: tự vẽ + double buffer + không xoá nền (TabControl gốc xoá nền rồi mới vẽ từng tab nên nháy mỗi lần rê chuột/đổi tab);
///  * <b>không thừa khoảng trắng</b>: bỏ khung viền/lề 3D mà Windows chừa quanh trang (TCM_ADJUSTRECT) — nội dung tab ôm sát ngay dưới thanh tab.
/// Hình dáng từng tab dùng chung <see cref="ThemeManager.PaintTab"/> với các TabControl khác, nên tab nào cũng cùng một kiểu.
/// </summary>
public sealed class FlatTabControl : TabControl
{
    private const int TCM_ADJUSTRECT = 0x1328;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    public FlatTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Multiline = false;
    }

    /// <summary>Chiều cao thanh tab (đáy của tab đầu); 0 khi chưa có tab nào.</summary>
    private int HeaderHeight => IsHandleCreated && TabPages.Count > 0 ? GetTabRect(0).Bottom : 0;

    protected override void OnPaintBackground(PaintEventArgs pevent) { /* AllPaintingInWmPaint: OnPaint tự tô hết */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var header = HeaderHeight;
        using (var strip = new SolidBrush(AppColors.PanelAlt))
            g.FillRectangle(strip, 0, 0, Width, Math.Max(header, 0));
        using (var body = new SolidBrush(AppColors.Panel))
            g.FillRectangle(body, 0, header, Width, Math.Max(0, Height - header));

        for (var i = 0; i < TabPages.Count; i++)
        {
            if (TabPages[i] is null) continue;
            var r = GetTabRect(i);
            if (r.IntersectsWith(e.ClipRectangle)) ThemeManager.PaintTab(g, this, i);
        }

        if (header > 0)
        {
            using var line = new Pen(AppColors.Border);
            g.DrawLine(line, 0, header - 1, Width, header - 1);
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Windows tính vùng nội dung = vùng control trừ thanh tab VÀ một lề dày xung quanh. Chỉ giữ phần thanh tab, bỏ lề.
        if (m.Msg == TCM_ADJUSTRECT && !DesignMode && IsHandleCreated && m.LParam != IntPtr.Zero)
        {
            var rc = Marshal.PtrToStructure<NativeRect>(m.LParam);
            var h = HeaderHeight;
            if (m.WParam == IntPtr.Zero) rc.Top += h; // vùng control -> vùng nội dung
            else rc.Top -= h;                         // vùng nội dung -> vùng control
            Marshal.StructureToPtr(rc, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }
}
