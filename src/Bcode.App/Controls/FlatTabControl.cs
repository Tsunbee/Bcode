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
    private const int TCM_SETITEMW = 0x133D;
    private const int TCM_INSERTITEMW = 0x133E;
    private const uint TCIF_TEXT = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TcItem { public uint Mask, State, StateMask; public IntPtr Text; public int TextMax, Image; public IntPtr Param; }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TcItem lParam);

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
        FitTab(TabPages.IndexOf(page)); // chấm ghim chiếm thêm chỗ đầu tab
        Invalidate();
    }

    // ---- Bề rộng tab theo chữ vẽ thật ----
    // Control UserPaint thì WinForms KHÔNG gửi WM_SETFONT cho cửa sổ native (Control.OnHandleCreated / OnFontChanged chỉ gửi khi không UserPaint),
    // nên Windows luôn đo bề rộng tab bằng font hệ thống "System", bất kể font / cỡ chữ / UiScale của Template — còn PaintTab vẽ bằng font Template.
    // Font hẹp hơn "System" (Segoe UI cỡ thường) thì dư chỗ; font rộng hơn (Consolas, chữ to, UiScale > 1) thì chữ bị cắt "File L...".
    // Không đổi font native (tab Segoe UI đang vừa sẽ bị đổi bề rộng): chỉ tab nào hẹp hơn ThemeManager.TabMinWidth mới được nới, bằng cách đưa
    // cho Windows chữ có thêm khoảng trắng ở cuối (chỉ ở phía native — TabPage.Text giữ nguyên; chữ trên tab do PaintTab tự vẽ).
    private const char FitPadChar = (char)0xA0; // khoảng trắng không ngắt: Windows không bỏ qua khi đo như dấu cách thường ở cuối chuỗi
    private bool _settingNativeText;

    /// <summary>Đo lại mọi tab (font / mật độ / DPI / UiScale vừa đổi, hoặc vừa bật nút ✕).</summary>
    public void FitAllTabs()
    {
        if (!IsHandleCreated) return;
        for (var i = 0; i < TabPages.Count; i++)
        {
            SetNativeText(i, NativeText(TabPages[i]));
            FitTab(i);
        }
        Invalidate();
    }

    // Chữ WinForms gửi cho Windows: '&' nhân đôi (TabControl.PrefixAmpersands) — giữ y như vậy để bề rộng gốc không đổi.
    private static string NativeText(TabPage page) => (page.Text ?? "").Replace("&", "&&");

    private void SetNativeText(int index, string text)
    {
        var ptr = Marshal.StringToHGlobalUni(text);
        try
        {
            var item = new TcItem { Mask = TCIF_TEXT, Text = ptr, TextMax = text.Length };
            _settingNativeText = true;
            SendMessage(Handle, TCM_SETITEMW, (IntPtr)index, ref item);
        }
        finally { _settingNativeText = false; Marshal.FreeHGlobal(ptr); }
    }

    /// <summary>Tab <paramref name="index"/> đang mang chữ gốc ở phía native: nếu Windows đo hẹp hơn chữ PaintTab sẽ vẽ thì thêm khoảng trắng cho đủ.</summary>
    private void FitTab(int index)
    {
        if (!IsHandleCreated || index < 0 || index >= TabPages.Count) return;
        var page = TabPages[index];
        var need = ThemeManager.TabMinWidth(this, page);
        var baseWidth = GetTabRect(index).Width;
        if (baseWidth >= need) return;
        var text = NativeText(page);
        // Bề rộng 1 khoảng trắng theo font native: đo bằng 1 ký tự thử, rồi thêm đúng số cần (thừa không quá 1 khoảng trắng).
        SetNativeText(index, text + FitPadChar);
        var perPad = GetTabRect(index).Width - baseWidth;
        if (perPad <= 0) { SetNativeText(index, text); return; } // Windows không tính khoảng trắng → thôi, giữ như cũ
        var pad = (need - baseWidth + perPad - 1) / perPad;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (pad > 1) SetNativeText(index, text + new string(FitPadChar, pad));
            if (GetTabRect(index).Width >= need) return;
            pad++;
        }
    }

    // Windows vừa nhận thêm 1 tab (chữ gốc). Tab thêm thường được đo ngay ở OnControlAdded; nhưng khi tạo lại handle (đổi Padding / Alignment...)
    // WinForms chèn lại mọi tab mà không phát ControlAdded → hẹn đo lại cả thanh (BeginInvoke chạy trước lần vẽ kế tiếp, gộp nhiều lần chèn làm một).
    private bool _fitAllQueued;
    private void QueueFitAllTabs()
    {
        if (_fitAllQueued || !IsHandleCreated) return;
        _fitAllQueued = true;
        BeginInvoke(() => { _fitAllQueued = false; if (!IsDisposed) FitAllTabs(); });
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e); // tab mới (hoặc MovePage chèn lại) đã vào TabPages với chữ gốc
        if (e.Control is TabPage page) FitTab(TabPages.IndexOf(page));
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        QueueFitAllTabs(); // hẹn sau, để Template / UiScale kịp đổi font theo DPI mới
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
        // WinForms gửi lại chữ gốc của tab (đổi TabPage.Text / ImageIndex) → bề rộng về mức Windows tự đo; nới lại nếu thiếu.
        if (m.Msg == TCM_SETITEMW && !_settingNativeText && IsHandleCreated && m.LParam != IntPtr.Zero
            && (Marshal.PtrToStructure<TcItem>(m.LParam).Mask & TCIF_TEXT) != 0)
        {
            base.WndProc(ref m);
            FitTab((int)m.WParam);
            return;
        }
        if (m.Msg == TCM_INSERTITEMW && !_settingNativeText)
        {
            base.WndProc(ref m);
            QueueFitAllTabs();
            return;
        }
        base.WndProc(ref m);
    }
}
