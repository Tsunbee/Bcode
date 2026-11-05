using System.Runtime.InteropServices;
using System.Windows.Automation;
using Microsoft.Win32;

namespace BcodeViewer.App.Host;

/// <summary>
/// F5 (MenuLauncher): mở trang menu trong CHÍNH Chrome / Edge (họ Chromium) người dùng đang mở và dùng lại đúng tab đã mở lần
/// trước cho trang đó, thay vì Process.Start(url) — cách đó trình duyệt nào cũng bung thêm 1 tab mới mỗi lần.
///
/// Không có cách nào điều khiển tab của trình duyệt thường bằng API (Chrome 136+ không cho bật cổng DevTools trên hồ sơ
/// đang dùng), nên làm qua UI Automation của Windows — đúng thứ trình đọc màn hình dùng: dải tab (TabItem) và thanh địa chỉ
/// (Edit "OmniboxViewViews") của cửa sổ Chrome đều đọc / chọn được.
///  • Lần đầu: Process.Start(url) như cũ (trình duyệt mặc định, đang mở thì thành tab mới trong cửa sổ đang mở, chưa mở thì
///    trình duyệt tự khởi động) rồi so danh sách tab trước / sau để nhớ tab mới là tab của URL này.
///  • Lần sau: tìm tab đã nhớ (RuntimeId của UIA + pid trình duyệt — trình duyệt mở lại thì bỏ), hoặc cửa sổ nào đang hiện
///    đúng trang đó ở tab đang chọn; chọn tab, đưa cửa sổ lên trước (Viewer đang được focus vì vừa nhận F5 nên được quyền),
///    gõ lại địa chỉ vào thanh địa chỉ + Enter = nạp lại bằng GET (không bị hộp "gửi lại biểu mẫu" như phím F5 của trình
///    duyệt). Không đưa được cửa sổ lên trước thì KHÔNG gửi phím (tránh gõ nhầm vào cửa sổ khác) mà bấm nút Tải lại.
///  • Không tìm lại được (tab đã đóng, Viewer mở lại sau khi tab đã chuyển sang trang khác) thì mở tab mới như lần đầu.
/// Trình duyệt mặc định không phải họ Chromium (Firefox...) thì chỉ còn mở tab mới như cũ.
/// </summary>
internal static class BrowserTabLauncher
{
    /// <summary>Tên tiến trình các trình duyệt họ Chromium (cửa sổ lớp Chrome_WidgetWin_1 có dải tab). Electron (VS Code, Teams…)
    /// cũng dùng lớp cửa sổ này nên phải lọc theo tên tiến trình.</summary>
    private static readonly string[] ChromiumProcesses = { "chrome", "msedge", "brave", "vivaldi", "opera", "browser" /* Cốc Cốc */ };

    /// <summary>Mở URL (thay cho Process.Start) — chạy thử thì thay bằng hàm mở trình duyệt thử.</summary>
    internal static Action<string> Opener { get; set; } = url =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    /// <summary>Chỉ xét cửa sổ của các tiến trình này (chạy thử, để không đụng trình duyệt thật). Null = mọi trình duyệt Chromium.</summary>
    internal static Func<int, bool>? ProcessFilter { get; set; }

    private sealed record Remembered(string RuntimeId, int ProcessId);
    private static readonly Dictionary<string, Remembered> TabByUrl = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <returns>Câu mô tả việc đã làm (ghép vào thông báo của F5).</returns>
    public static async Task<string> OpenAsync(string url, CancellationToken token)
    {
        await Gate.WaitAsync(token);
        try
        {
            // UIA gọi sang tiến trình khác: chạy trên luồng nền (MTA), không chặn luồng giao diện của Viewer.
            return await Task.Run(() => Open(url, token), token);
        }
        finally { Gate.Release(); }
    }

    private static string Open(string url, CancellationToken token)
    {
        var key = Normalize(url);
        var windows = BrowserWindows();

        Tab? tab = null;
        if (TabByUrl.TryGetValue(key, out var mem))
            tab = windows.Where(w => w.ProcessId == mem.ProcessId).SelectMany(w => w.Tabs()).FirstOrDefault(t => t.RuntimeId == mem.RuntimeId);
        // Chưa nhớ (Viewer vừa mở lại / tab tự mở tay): cửa sổ nào đang hiện đúng trang này ở tab đang chọn.
        tab ??= windows.Where(w => SamePage(w.AddressBarText(), url)).Select(w => w.Tabs().FirstOrDefault(t => t.IsSelected)).FirstOrDefault(t => t is not null);

        if (tab is null)
        {
            var before = windows.SelectMany(w => w.Tabs()).Select(t => t.RuntimeId).ToHashSet();
            Opener(url);
            var opened = WaitForNewTab(before, token);
            if (opened is not null) TabByUrl[key] = new Remembered(opened.RuntimeId, opened.Window.ProcessId);
            return windows.Count == 0 ? "đã mở trình duyệt" : $"đã mở tab mới trong {opened?.Window.Name ?? "trình duyệt"}";
        }

        TabByUrl[key] = new Remembered(tab.RuntimeId, tab.Window.ProcessId);
        tab.Select();
        var w = tab.Window;
        var front = w.BringToFront();
        if (front && w.Navigate(url)) return $"đã chuyển tới tab cũ trong {w.Name} và nạp lại";
        // Không đưa được cửa sổ lên trước: không gửi phím, chỉ bấm nút Tải lại của cửa sổ đó.
        return w.ClickReload() ? $"đã tải lại tab cũ trong {w.Name}" : $"đã chuyển tới tab cũ trong {w.Name} (không tải lại được — bấm F5 trên trình duyệt)";
    }

    /// <summary>Sau Process.Start: chờ tối đa ~6 giây tab mới xuất hiện (trình duyệt chưa mở thì lâu hơn), ưu tiên tab đang chọn.</summary>
    private static Tab? WaitForNewTab(HashSet<string> before, CancellationToken token)
    {
        for (var i = 0; i < 30 && !token.IsCancellationRequested; i++)
        {
            Thread.Sleep(200);
            var fresh = BrowserWindows().SelectMany(w => w.Tabs()).Where(t => !before.Contains(t.RuntimeId)).ToList();
            if (fresh.Count > 0) return fresh.FirstOrDefault(t => t.IsSelected) ?? fresh[^1];
        }
        return null;
    }

    // ---- cửa sổ / tab qua UI Automation ------------------------------------------------------------------------

    private static List<BrowserWindow> BrowserWindows()
    {
        var list = new List<BrowserWindow>();
        AutomationElementCollection tops;
        try { tops = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ClassNameProperty, "Chrome_WidgetWin_1")); }
        catch { return list; }
        var preferred = DefaultBrowserProcess();
        foreach (AutomationElement e in tops)
        {
            try
            {
                var pid = e.Current.ProcessId;
                if (ProcessFilter is { } f ? !f(pid) : false) continue;
                string name;
                using (var p = System.Diagnostics.Process.GetProcessById(pid)) name = p.ProcessName.ToLowerInvariant();
                if (!ChromiumProcesses.Contains(name)) continue;
                list.Add(new BrowserWindow(e, pid, new IntPtr(e.Current.NativeWindowHandle), name));
            }
            catch { /* cửa sổ vừa đóng */ }
        }
        // Trình duyệt mặc định trước (thứ tự z của cửa sổ giữ nguyên trong từng nhóm).
        return list.OrderBy(w => w.ProcessName == preferred ? 0 : 1).ToList();
    }

    private sealed class Tab
    {
        public required BrowserWindow Window { get; init; }
        public required AutomationElement Element { get; init; }
        public required string RuntimeId { get; init; }

        public bool IsSelected
        {
            get
            {
                try { return Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p) && ((SelectionItemPattern)p).Current.IsSelected; }
                catch { return false; }
            }
        }

        public void Select()
        {
            try { if (Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p)) ((SelectionItemPattern)p).Select(); }
            catch { }
        }
    }

    private sealed class BrowserWindow(AutomationElement element, int processId, IntPtr hwnd, string processName)
    {
        public int ProcessId => processId;
        public string ProcessName => processName;
        public string Name => processName switch { "chrome" => "Chrome", "msedge" => "Edge", "browser" => "Cốc Cốc", _ => char.ToUpper(processName[0]) + processName[1..] };

        public List<Tab> Tabs()
        {
            var tabs = new List<Tab>();
            try
            {
                foreach (AutomationElement t in element.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem)))
                {
                    // Chỉ tab của dải tab trình duyệt — bỏ các "tab" nằm trong nội dung trang web.
                    if (t.Current.ClassName != "Tab") continue;
                    tabs.Add(new Tab { Window = this, Element = t, RuntimeId = string.Join(".", t.GetRuntimeId()) });
                }
            }
            catch { }
            return tabs;
        }

        private AutomationElement? AddressBar()
        {
            try { return element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "OmniboxViewViews")); }
            catch { return null; }
        }

        public string AddressBarText()
        {
            try { return AddressBar() is { } a && a.TryGetCurrentPattern(ValuePattern.Pattern, out var p) ? ((ValuePattern)p).Current.Value : ""; }
            catch { return ""; }
        }

        public bool BringToFront()
        {
            if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
            SetForegroundWindow(hwnd);
            for (var i = 0; i < 10; i++)
            {
                if (GetForegroundWindow() == hwnd) return true;
                Thread.Sleep(30);
            }
            return false;
        }

        /// <summary>Gõ URL vào thanh địa chỉ rồi Enter (cửa sổ phải đang ở trên cùng — kiểm lại ngay trước khi gửi phím).</summary>
        public bool Navigate(string url)
        {
            try
            {
                if (AddressBar() is not { } bar || !bar.TryGetCurrentPattern(ValuePattern.Pattern, out var p)) return false;
                bar.SetFocus();
                ((ValuePattern)p).SetValue(url);
                Thread.Sleep(60);
                if (GetForegroundWindow() != hwnd) return false;
                // Delete: bỏ phần thanh địa chỉ tự gợi ý nối thêm (inline autocomplete) để Enter đi đúng URL.
                SendKey(VK_DELETE);
                SendKey(VK_RETURN);
                return true;
            }
            catch { return false; }
        }

        public bool ClickReload()
        {
            try
            {
                var b = element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ClassNameProperty, "ReloadButton"));
                if (b is null || !b.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) return false;
                ((InvokePattern)p).Invoke();
                return true;
            }
            catch { return false; }
        }
    }

    // ---- tiện ích ----------------------------------------------------------------------------------------------

    /// <summary>So URL: bỏ #..., "/" cuối, không phân biệt hoa thường.</summary>
    private static string Normalize(string url)
    {
        var u = url.Trim();
        var h = u.IndexOf('#');
        if (h >= 0) u = u[..h];
        return u.TrimEnd('/').ToLowerInvariant();
    }

    /// <summary>Chữ trên thanh địa chỉ có phải trang này không: thanh địa chỉ thường giấu "http://" / "https://" và "www.",
    /// nên so phần host + đường dẫn (bỏ query).</summary>
    private static bool SamePage(string addressBar, string url)
    {
        static string Core(string s)
        {
            s = s.Trim();
            var i = s.IndexOf("://", StringComparison.Ordinal);
            if (i >= 0) s = s[(i + 3)..];
            if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) s = s[4..];
            var q = s.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) s = s[..q];
            return s.TrimEnd('/').ToLowerInvariant();
        }
        return addressBar.Length > 0 && Core(addressBar) == Core(url);
    }

    /// <summary>Tên tiến trình của trình duyệt mặc định (theo ProgId của http) — để ưu tiên cửa sổ của nó.</summary>
    private static string DefaultBrowserProcess()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
            var id = k?.GetValue("ProgId") as string ?? "";
            if (id.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase)) return "msedge";
            if (id.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase)) return "chrome";
            if (id.StartsWith("Brave", StringComparison.OrdinalIgnoreCase)) return "brave";
            if (id.StartsWith("Vivaldi", StringComparison.OrdinalIgnoreCase)) return "vivaldi";
            if (id.StartsWith("Opera", StringComparison.OrdinalIgnoreCase)) return "opera";
            if (id.StartsWith("CocCoc", StringComparison.OrdinalIgnoreCase)) return "browser";
        }
        catch { }
        return "";
    }

    private static void SendKey(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD; inputs[0].ki.wVk = vk;
        inputs[1].type = INPUT_KEYBOARD; inputs[1].ki.wVk = vk; inputs[1].ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private const int SW_RESTORE = 9;
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2;
    private const ushort VK_RETURN = 0x0D, VK_DELETE = 0x2E;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    // INPUT là union chuột / phím / phần cứng: chèn đệm cho bằng cỡ MOUSEINPUT (lớn nhất) để SendInput nhận đúng cỡ.
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public KEYBDINPUT ki; private readonly long _pad1; }

    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
