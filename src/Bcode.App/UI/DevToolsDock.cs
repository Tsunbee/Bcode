using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.UI;

/// <summary>
/// DevTools của 1 WebView2 nằm NGAY TRONG màn hình — khung bên phải, kéo thanh chia để đổi rộng — như bấm F12 ở Chrome.
/// WebView2 chỉ có OpenDevToolsWindow (luôn là cửa sổ nổi riêng, không có kiểu "dock"), nên mở cửa sổ đó rồi nhúng vào khung bằng SetParent
/// (cùng cách nhúng Profiler.exe ở SqlProfilerControl). Cửa sổ DevTools thuộc tiến trình trình duyệt của chính WebView2 — tiến trình vốn đã có cửa sổ con
/// nằm trong Bcode (vùng hiện trang) — nên không thêm rủi ro dùng chung hàng đợi input. Đóng màn hình thì tháo cửa sổ ra rồi mới đóng DevTools,
/// không để Windows huỷ cửa sổ của tiến trình khác theo khung.
/// Dùng: thêm <see cref="DockControls"/> vào form NGAY SAU control WebView2 (Dock = Fill) rồi gọi <see cref="Toggle"/> khi bấm phím.
/// </summary>
public sealed class DevToolsDock
{
    private readonly Control _owner;
    private readonly WebView2 _web;
    private readonly Panel _host = new() { Dock = DockStyle.Right, Visible = false, BackColor = Color.FromArgb(36, 36, 36) };
    private readonly Splitter _splitter = new() { Dock = DockStyle.Right, Width = 5, MinSize = 240, MinExtra = 240, Visible = false };
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 400 };
    private IntPtr _hwnd;
    private bool _opening;
    private static int _lastWidth;   // rộng khung lần trước (dùng chung các tab trong phiên)

    public DevToolsDock(Control owner, WebView2 web)
    {
        _owner = owner;
        _web = web;
        _host.Resize += (_, _) => Fit();
        _splitter.SplitterMoved += (_, _) => { if (_host.Visible) _lastWidth = _host.Width; };
        // DevTools tự đóng (F12 / Ctrl+Shift+I trong chính nó, trang đóng...) → thu khung lại.
        _watch.Tick += (_, _) =>
        {
            if (_hwnd == IntPtr.Zero) return;
            if (!IsWindow(_hwnd) || GetParent(_hwnd) != _host.Handle) Detach(close: false);
            else if (_inset is null) Fit();   // giao diện DevTools vừa dựng xong: đo lại để cắt thanh tiêu đề
        };
        // Màn hình bị đóng / tab bị huỷ: tháo cửa sổ DevTools ra trước khi khung bị huỷ (lúc này khung con vẫn còn).
        owner.HandleDestroyed += (_, _) => Detach(close: true);
        owner.Disposed += (_, _) => _watch.Dispose();
    }

    /// <summary>Khung chứa + thanh chia — thêm vào form theo đúng thứ tự này, sau WebView2 (Dock = Fill).</summary>
    public Control[] DockControls => new Control[] { _splitter, _host };

    public bool IsOpen => _hwnd != IntPtr.Zero;

    /// <summary>Mở DevTools gắn bên phải trang; đang mở thì đóng.</summary>
    public async void Toggle()
    {
        if (_hwnd != IntPtr.Zero) { Detach(close: true); _web.Focus(); return; }
        if (_opening || _web.IsDisposed || _web.CoreWebView2 is null) return;
        _opening = true;
        try
        {
            var core = _web.CoreWebView2;
            var pid = core.BrowserProcessId;
            var before = new HashSet<IntPtr>(DevToolsWindows(pid));
            core.OpenDevToolsWindow();
            var found = IntPtr.Zero;
            for (var i = 0; i < 200 && found == IntPtr.Zero; i++)   // ~3 giây
            {
                found = DevToolsWindows(pid).FirstOrDefault(h => !before.Contains(h));
                if (found == IntPtr.Zero) await Task.Delay(15);
                if (_web.IsDisposed) return;
            }
            // DevTools của trang này đã mở sẵn dạng cửa sổ nổi (vd Ctrl+Shift+I) → OpenDevToolsWindow chỉ đưa nó lên trước: lấy cửa sổ mang địa chỉ trang.
            if (found == IntPtr.Zero)
            {
                var url = System.Text.RegularExpressions.Regex.Replace(core.Source ?? "", "^[a-z]+://", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                found = DevToolsWindows(pid).FirstOrDefault(h => url.Length > 0 && TitleOf(h).EndsWith(url, StringComparison.OrdinalIgnoreCase));
            }
            if (found != IntPtr.Zero) Embed(found);
        }
        catch { /* WebView2 đang đóng / không nhúng được — DevTools (nếu đã mở) vẫn là cửa sổ nổi như cũ */ }
        finally { _opening = false; }
    }

    private void Embed(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _inset = null;
        ShowWindow(hwnd, SW_HIDE);
        var style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~WS_POPUP; style &= ~WS_CAPTION; style &= ~WS_THICKFRAME; style |= WS_CHILD;
        SetWindowLong(hwnd, GWL_STYLE, style);
        var w = _lastWidth > 0 ? _lastWidth : Math.Max(380, _owner.ClientSize.Width * 2 / 5);
        _host.Width = Math.Min(w, Math.Max(240, _owner.ClientSize.Width - 300));
        _host.Visible = _splitter.Visible = true;
        SetParent(hwnd, _host.Handle);
        Fit();
        ShowWindow(hwnd, SW_SHOW);
        _watch.Start();
    }

    /// <summary>Đặt cửa sổ DevTools sao cho phần giao diện thật (cửa sổ con Chrome_RenderWidgetHostHWND) vừa khít khung: Chromium tự vẽ thanh tiêu đề
    /// (tên + nút thu nhỏ/phóng to/đóng) và viền NGAY TRONG vùng client — bỏ WS_CAPTION không mất — nên đẩy cửa sổ lệch lên/sang để khung cắt bỏ phần đó.
    /// Khoảng lệch đo từ chính cửa sổ (đổi theo DPI); giao diện chưa dựng xong thì tạm đặt khít khung, đồng hồ theo dõi đặt lại sau.</summary>
    private void Fit()
    {
        if (_hwnd == IntPtr.Zero || !_host.IsHandleCreated) return;
        if (_inset is null && GetWindowRect(_hwnd, out var w))
        {
            var content = IntPtr.Zero;
            EnumChildWindows(_hwnd, (h, _) => { if (ClassOf(h) == "Chrome_RenderWidgetHostHWND" && IsWindowVisible(h)) { content = h; return false; } return true; }, IntPtr.Zero);
            if (content != IntPtr.Zero && GetWindowRect(content, out var c) && c.Right > c.Left)
                _inset = new Padding(c.Left - w.Left, c.Top - w.Top, w.Right - c.Right, w.Bottom - c.Bottom);
        }
        var p = _inset ?? Padding.Empty;
        SetWindowPos(_hwnd, IntPtr.Zero, -p.Left, -p.Top, _host.ClientSize.Width + p.Horizontal, _host.ClientSize.Height + p.Vertical, SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    private Padding? _inset;   // thanh tiêu đề + viền Chromium tự vẽ quanh giao diện DevTools (đo 1 lần mỗi lần nhúng)

    /// <summary>Tháo cửa sổ DevTools khỏi khung (ẩn đi, trả về cửa sổ gốc), <paramref name="close"/> thì đóng luôn DevTools; thu khung lại.</summary>
    private void Detach(bool close)
    {
        var h = _hwnd;
        _hwnd = IntPtr.Zero;
        try { _watch.Stop(); } catch { }
        if (h != IntPtr.Zero && IsWindow(h))
        {
            try
            {
                ShowWindow(h, SW_HIDE);
                SetParent(h, IntPtr.Zero);
                if (close) PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }
        try { if (!_host.IsDisposed) { _host.Visible = false; _splitter.Visible = false; } } catch { }
    }

    // ---------------------------------------------------------------- Win32

    /// <summary>Cửa sổ cấp cao "DevTools…" của tiến trình trình duyệt <paramref name="pid"/>.</summary>
    private static List<IntPtr> DevToolsWindows(uint pid)
    {
        var list = new List<IntPtr>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid && TitleOf(h).StartsWith("DevTools", StringComparison.Ordinal)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(256);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string TitleOf(IntPtr h)
    {
        var sb = new StringBuilder(512);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    private const int GWL_STYLE = -16;
    private const int WS_CHILD = 0x40000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_THICKFRAME = 0x00040000;
    private const uint SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020;
    private const int SW_HIDE = 0, SW_SHOW = 5;
    private const uint WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
}
