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

    /// <summary>Các tab đã ghim (Pin Tab): luôn nằm bên trái các tab chưa ghim, kéo thả chỉ đổi chỗ trong cùng nhóm, và không bị
    /// Close Other/Right/All đóng giúp.</summary>
    public HashSet<TabPage> Pinned { get; } = new();
    public bool IsPinned(TabPage page) => Pinned.Contains(page);

    /// <summary>Đưa <paramref name="page"/> tới vị trí <paramref name="index"/> (giữ nguyên tab đang chọn).</summary>
    public void MovePage(TabPage page, int index)
    {
        var current = TabPages.IndexOf(page);
        if (current < 0) return;
        index = Math.Clamp(index, 0, TabPages.Count - 1);
        if (index == current) return;
        var selected = SelectedTab;
        SuspendLayout();
        try
        {
            TabPages.Remove(page);
            TabPages.Insert(index, page);
            SelectedTab = selected ?? page;
        }
        finally { ResumeLayout(); }
        Invalidate();
    }

    /// <summary>Ghim / bỏ ghim: tab ghim được đưa về cuối nhóm ghim (đầu thanh tab), bỏ ghim thì ra ngay sau nhóm ghim.</summary>
    public void SetPinned(TabPage page, bool pinned)
    {
        if (pinned == Pinned.Contains(page)) return;
        if (pinned) { var target = Pinned.Count; Pinned.Add(page); MovePage(page, target); }
        else { Pinned.Remove(page); MovePage(page, Pinned.Count); }
        Invalidate();
    }

    // ---- Kéo thả đổi vị trí tab ----
    private TabPage? _dragPage;
    private Point _dragStart;
    private bool _dragging;

    private int HitIndex(Point p)
    {
        for (var i = 0; i < TabPages.Count; i++)
            if (GetTabRect(i).Contains(p)) return i;
        return -1;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _dragPage = null; _dragging = false;
        if (e.Button != MouseButtons.Left) return;
        var i = HitIndex(e.Location);
        // Phần ✕ bên phải mỗi tab là nút đóng, không bắt đầu kéo ở đó.
        if (i >= 0 && GetTabRect(i).Right - e.X > 30) { _dragPage = TabPages[i]; _dragStart = e.Location; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragPage is null || (e.Button & MouseButtons.Left) == 0) return;
        if (!_dragging && Math.Abs(e.X - _dragStart.X) < 8) return;
        _dragging = true;
        Cursor = Cursors.SizeWE;

        var from = TabPages.IndexOf(_dragPage);
        var to = HitIndex(e.Location);
        if (from < 0 || to < 0 || to == from) return;
        var target = TabPages[to];
        if (IsPinned(target) != IsPinned(_dragPage)) return; // không trộn nhóm ghim với nhóm thường
        // Chỉ đổi chỗ khi con trỏ qua giữa tab đích — tránh nhảy qua lại khi các tab rộng hẹp khác nhau.
        var r = GetTabRect(to);
        var mid = r.Left + r.Width / 2;
        if ((to > from && e.X < mid) || (to < from && e.X > mid)) return;
        MovePage(_dragPage, to);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragPage = null; _dragging = false;
        Cursor = Cursors.Default;
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        _dragPage = null; _dragging = false;
        Cursor = Cursors.Default;
    }

    /// <summary>Chiều cao thanh tab (đáy của tab đầu); 0 khi chưa có tab nào.</summary>
    private bool AtBottom => Alignment == TabAlignment.Bottom;
    private int HeaderHeight => IsHandleCreated && TabPages.Count > 0
        ? (AtBottom ? Math.Max(0, Height - GetTabRect(0).Top) : GetTabRect(0).Bottom)
        : 0;

    protected override void OnPaintBackground(PaintEventArgs pevent) { /* AllPaintingInWmPaint: OnPaint tự tô hết */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var header = HeaderHeight;
        var stripColor = UiTemplate.ParseColor(UiTemplate.AreaStyle("tabs")?.BackColor) ?? AppColors.PanelAlt;
        // Thanh tab nằm ở đầu (mặc định) hoặc ở đáy (Template giao diện: "Thanh tab ở dưới").
        var stripY = AtBottom ? Height - header : 0;
        var bodyY = AtBottom ? 0 : header;
        using (var strip = new SolidBrush(stripColor))
            g.FillRectangle(strip, 0, stripY, Width, Math.Max(header, 0));
        using (var body = new SolidBrush(AppColors.Panel))
            g.FillRectangle(body, 0, bodyY, Width, Math.Max(0, Height - header));

        for (var i = 0; i < TabPages.Count; i++)
        {
            if (TabPages[i] is null) continue;
            var r = GetTabRect(i);
            if (r.IntersectsWith(e.ClipRectangle)) ThemeManager.PaintTab(g, this, i);
        }

        if (header > 0)
        {
            using var line = new Pen(AppColors.Border);
            var lineY = AtBottom ? Height - header : header - 1;
            g.DrawLine(line, 0, lineY, Width, lineY);
        }
    }

    protected override void WndProc(ref Message m)
    {
        // Windows tính vùng nội dung = vùng control trừ thanh tab VÀ một lề dày xung quanh. Chỉ giữ phần thanh tab, bỏ lề.
        if (m.Msg == TCM_ADJUSTRECT && !DesignMode && IsHandleCreated && m.LParam != IntPtr.Zero)
        {
            var rc = Marshal.PtrToStructure<NativeRect>(m.LParam);
            var h = HeaderHeight;
            if (AtBottom)
            {
                if (m.WParam == IntPtr.Zero) rc.Bottom -= h; // thanh tab ở đáy: vùng nội dung ngắn đi ở phía dưới
                else rc.Bottom += h;
            }
            else if (m.WParam == IntPtr.Zero) rc.Top += h; // vùng control -> vùng nội dung
            else rc.Top -= h;                              // vùng nội dung -> vùng control
            Marshal.StructureToPtr(rc, m.LParam, false);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }
}
