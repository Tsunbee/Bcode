using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.UI;

/// <summary>
/// Phần giao diện của log lỗi (<see cref="ErrorLog"/>):
/// - Theo dõi MỌI hộp MessageBox của Bcode (hook WH_CBT trên luồng giao diện): hộp có chữ trùng thông điệp 1 exception vừa ném → ghi exception đó
///   kèm stack; hộp biểu tượng lỗi (X đỏ) không tìm được exception → ghi chữ của hộp.
/// - Phím "Gửi lỗi gần nhất" (ShortcutRegistry "error.send", mặc định F1, đổi ở Template → Phím tắt): CHỈ bắt phím khi vừa có lỗi chưa gửi
///   (<see cref="ErrorLog.HasPending"/>) — ngoài lúc đó F1 đi tiếp như cũ (lưới Table: chi tiết dòng, Monaco...). Bắt ở 3 chỗ: IMessageFilter (mọi cửa sổ
///   WinForms, kể cả hộp thoại modal / hộp lỗi chưa xử lý), WH_MSGFILTER (đang mở MessageBox), WebViewEnvironment.AcceleratorKeyPressed (focus trong trang web).
/// - Gửi: URL Apps Script (…/exec) → POST; chỉ có link Google Sheet → chép 1 dòng lỗi vào clipboard + mở sheet để dán. Luôn hỏi lại kèm nội dung trước khi gửi.
/// </summary>
public static class ErrorReporter
{
    public const string ShortcutId = "error.send";
    private static bool _installed, _sending;
    private static int _quiet;   // > 0: hộp thoại của chính phần gửi lỗi — không theo dõi

    /// <summary>Đang hiện hộp thoại của chính phần gửi lỗi — ThemedMessageBox không đối chiếu chữ với exception (như hook WH_CBT bỏ qua).</summary>
    internal static bool Quiet => _quiet > 0;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Application.AddMessageFilter(new KeyFilter());
        try
        {
            var tid = GetCurrentThreadId();
            _cbtProc = CbtProc;
            _cbtHook = SetWindowsHookEx(WH_CBT, _cbtProc, IntPtr.Zero, tid);
            _msgProc = MsgFilterProc;
            _msgHook = SetWindowsHookEx(WH_MSGFILTER, _msgProc, IntPtr.Zero, tid);
        }
        catch { /* không gắn được hook → vẫn còn log lỗi chưa xử lý / trang web / thanh trạng thái */ }
    }

    /// <summary>Phím vừa bấm có phải phím Gửi lỗi và đang có lỗi chờ gửi không — có thì lên lịch gửi và trả true (nuốt phím).</summary>
    public static bool TryHandleKey(Keys keys)
    {
        var busy = _queued || _sending;
        if (!busy && !ErrorLog.HasPending) return false;
        var combo = ShortcutRegistry.FromKeys(keys);
        if (combo is null) return false;
        var mine = ShortcutRegistry.Get(ShortcutId);
        if (mine.Length == 0 || combo != mine) return false;
        if (busy) return true;   // đang hỏi / đang gửi (giữ phím lặp lại) — nuốt phím, không mở hộp thứ hai
        _queued = true;
        Post(() => SendLast(null, force: false));
        return true;
    }

    private static bool _queued;

    private static void Post(Action a)
    {
        var f = Application.OpenForms.Cast<Form>().FirstOrDefault(x => !x.IsDisposed && x.IsHandleCreated);
        if (f is null) return;
        try { f.BeginInvoke(a); } catch { /* đang đóng */ }
    }

    private static Form? Owner(Form? owner)
    {
        if (owner is { IsDisposed: false }) return owner;
        return Form.ActiveForm ?? Application.OpenForms.Cast<Form>().LastOrDefault(x => !x.IsDisposed && x.Visible);
    }

    private static DialogResult Box(Form? owner, string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        _quiet++;
        try { return owner is null ? MessageBox.Show(text, title, buttons, icon) : MessageBox.Show(owner, text, title, buttons, icon); }
        finally { _quiet--; }
    }

    /// <summary>Gửi lỗi gần nhất tới đích đã khai (Template → Đường dẫn → Báo lỗi). <paramref name="force"/> = gửi cả lỗi cũ / đã gửi (Command Palette).</summary>
    public static async void SendLast(Form? owner, bool force)
    {
        _queued = false;
        if (_sending) return;
        _sending = true;
        try { await SendLastCore(owner, force); }
        catch (Exception ex) { ErrorLog.Record(ex, "Gửi lỗi"); }
        finally { _sending = false; }
    }

    private static async Task SendLastCore(Form? owner, bool force)
    {
        owner = Owner(owner);
        const string title = "Gửi lỗi";
        var e = ErrorLog.Last;
        if (e is null || (!force && !ErrorLog.HasPending))
        {
            Box(owner, "Chưa có lỗi nào được ghi trong phiên này.\n\nFile log: " + ErrorLog.FilePath, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var sheet = AppSettings.ErrorSheetUrl;
        var targets = Targets();
        if (targets.Count == 0 && sheet.Length == 0)
        {
            ErrorLog.MarkHandled();
            Box(owner, "Chưa khai nơi nhận lỗi: Template → Đường dẫn → Báo lỗi (link Google Sheet, URL Apps Script …/exec và / hoặc URL forum TK2 Help).\n\n" +
                       "Lỗi vẫn đã được ghi ở: " + ErrorLog.FilePath, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var how = targets.Count > 0 ? "tới " + string.Join(" + ", targets.Select(t => t.Name)) + " (tự động)" : "— chép vào clipboard rồi mở Google Sheet để bạn dán (Ctrl+V)";
        var ask = Box(owner, $"Gửi lỗi gần nhất {how}?\n\n{ErrorLog.Preview(e)}\n\n(Mật khẩu / key trong chuỗi kết nối đã được che.)",
            title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        ErrorLog.MarkHandled();   // Có hay Không đều trả F1 về chức năng cũ
        if (ask != DialogResult.Yes) return;

        if (targets.Count > 0)
        {
            var results = await Task.WhenAll(targets.Select(async t => (t.Name, R: await ErrorLog.SendAsync(e, t.Url))));
            owner = Owner(owner);
            var report = string.Join("\n", results.Select(x => (x.R.Ok ? "✔ Đã gửi tới " : "✘ Không gửi được tới ") + x.Name
                + (x.R.Ok ? ReplyNote(x.R.Info, targets.First(t => t.Name == x.Name).Url, out _) : ": " + x.R.Info)));
            var sheetOk = results.Any(x => x.R.Ok && x.Name == SheetTarget);
            // Forum TK2 Help trả {ok, id, url, duplicate} → hỏi mở bài vừa tạo / bài đã có
            var postUrl = results.Where(x => x.R.Ok).Select(x => { ReplyNote(x.R.Info, targets.First(t => t.Name == x.Name).Url, out var u); return u; }).FirstOrDefault(u => u is not null);
            if (postUrl is not null && !sheetOk && results.All(x => x.R.Ok))
            {
                if (Box(owner, report + "\n\nMở bài trên forum?", title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) OpenUrl(postUrl);
                return;
            }
            if (results.All(x => x.R.Ok) || sheet.Length == 0 || sheetOk)
            {
                var icon = results.All(x => x.R.Ok) ? MessageBoxIcon.Information : MessageBoxIcon.Warning;
                if (sheetOk && sheet.Length > 0) { if (Box(owner, report + "\n\nMở Google Sheet để xem?", title, MessageBoxButtons.YesNo, icon) == DialogResult.Yes) OpenUrl(sheet); }
                else Box(owner, report, title, MessageBoxButtons.OK, icon);
                return;
            }
            if (Box(owner, report + "\n\nChép lỗi vào clipboard rồi mở Google Sheet để dán tay?", title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        try { Clipboard.SetText(ErrorLog.ToTsvRow(e)); }
        catch (Exception ex) { Box(owner, "Không chép được vào clipboard: " + ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        OpenUrl(sheet);
        Box(owner, "Đã chép lỗi (1 dòng, các cột: Thời gian, Phiên bản, Máy, Người dùng, Dự án, Chức năng, Nguồn, Lỗi, Chi tiết) và mở Google Sheet.\n\n" +
                   "Bấm vào ô cột A của dòng trống đầu tiên rồi Ctrl+V.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private const string SheetTarget = "Google Sheet", ForumTarget = "forum TK2 Help";

    /// <summary>Đọc trả lời JSON của máy nhận (forum TK2 Help: {ok, id, url, duplicate}) → ghi chú thêm vào dòng ✔ + link bài (url tương đối thì ghép với URL đã gửi).</summary>
    private static string ReplyNote(string body, string sentTo, out string? link)
    {
        link = null;
        if (string.IsNullOrWhiteSpace(body) || body.TrimStart()[0] != '{') return "";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var r = doc.RootElement;
            if (r.TryGetProperty("url", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.String && u.GetString() is { Length: > 0 } s
                && Uri.TryCreate(new Uri(sentTo), s, out var abs)) link = abs.ToString();
            var dup = r.TryGetProperty("duplicate", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.True;
            return (dup ? " (lỗi này đã có câu hỏi — thêm bình luận vào đó)" : "") + (link is null ? "" : "\n   " + link);
        }
        catch { return ""; }
    }

    /// <summary>Các đích nhận tự động đã khai (Template → Đường dẫn → Báo lỗi): Apps Script của Google Sheet, forum TK2 Help.</summary>
    public static List<(string Name, string Url)> Targets()
    {
        var l = new List<(string, string)>();
        if (AppSettings.ErrorPostUrl is { Length: > 0 } post) l.Add((SheetTarget, post));
        if (AppSettings.ErrorForumUrl is { Length: > 0 } forum) l.Add((ForumTarget, forum));
        return l;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* không mở được trình duyệt */ }
    }

    // ------------------------------------------------------------------ bắt phím

    private sealed class KeyFilter : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is not (WM_KEYDOWN or WM_SYSKEYDOWN) || !ErrorLog.HasPending) return false;
            return TryHandleKey((Keys)(int)(long)m.WParam | Control.ModifierKeys);
        }
    }

    private static IntPtr MsgFilterProc(int code, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (code == MSGF_DIALOGBOX && ErrorLog.HasPending)
            {
                var msg = Marshal.PtrToStructure<MSG>(lParam);
                if (msg.message is WM_KEYDOWN or WM_SYSKEYDOWN && TryHandleKey((Keys)(int)(long)msg.wParam | Control.ModifierKeys)) return (IntPtr)1;
            }
        }
        catch { }
        return CallNextHookEx(_msgHook, code, wParam, lParam);
    }

    // ------------------------------------------------------------------ theo dõi MessageBox

    private static readonly List<IntPtr> Seen = new();

    private static IntPtr CbtProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HCBT_ACTIVATE && _quiet == 0)
        {
            try { InspectDialog(wParam); } catch { /* không đọc được hộp */ }
        }
        return CallNextHookEx(_cbtHook, code, wParam, lParam);
    }

    private static void InspectDialog(IntPtr hwnd)
    {
        if (Seen.Contains(hwnd)) return;
        var cls = new StringBuilder(32);
        GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() != "#32770") return;
        var textHwnd = GetDlgItem(hwnd, 0xFFFF);   // chữ của MessageBox
        if (textHwnd == IntPtr.Zero) return;
        var text = WindowText(textHwnd);
        if (text.Length == 0) return;
        Seen.Add(hwnd);
        if (Seen.Count > 64) Seen.RemoveRange(0, 32);
        var title = WindowText(hwnd);
        var source = "Hộp thông báo" + (title.Length > 0 ? ": " + title : "");
        if (ErrorLog.NoticeDisplayed(text, source)) return;
        if (IsErrorIcon(hwnd)) ErrorLog.RecordText(text, source + " (biểu tượng lỗi)");
    }

    private static readonly Dictionary<IntPtr, bool> IconIsError = new();

    /// <summary>MessageBox biểu tượng lỗi (MessageBoxIcon.Error / Stop / Hand). HICON ở ô icon (id 0x14) KHÔNG trùng LoadIcon(IDI_HAND) (Windows nạp riêng theo DPI)
    /// nên nhận theo màu: vẽ icon ra 32×32, phần có màu đa số là đỏ (đo: lỗi 0,87; thông tin / cảnh báo / hỏi 0). Kết quả nhớ theo handle (mỗi loại dùng chung 1 handle).</summary>
    private static bool IsErrorIcon(IntPtr dlg)
    {
        var iconHwnd = GetDlgItem(dlg, 0x14);
        if (iconHwnd == IntPtr.Zero) return false;
        var icon = SendMessage(iconHwnd, STM_GETICON, IntPtr.Zero, IntPtr.Zero);
        if (icon == IntPtr.Zero) return false;
        if (IconIsError.TryGetValue(icon, out var known)) return known;
        var isErr = false;
        try
        {
            using var bmp = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                var hdc = g.GetHdc();
                try { DrawIconEx(hdc, 0, 0, icon, 32, 32, 0, IntPtr.Zero, 3 /* DI_NORMAL */); } finally { g.ReleaseHdc(hdc); }
            }
            int red = 0, ink = 0;
            for (var y = 0; y < 32; y++)
                for (var x = 0; x < 32; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.R > 240 && c.G > 240 && c.B > 240) continue;
                    ink++;
                    if (c.R > 150 && c.G < 110 && c.B < 110) red++;
                }
            isErr = ink > 0 && red * 2 > ink;
        }
        catch { /* không vẽ được icon */ }
        if (IconIsError.Count < 32) IconIsError[icon] = isErr;
        return isErr;
    }

    private static string WindowText(IntPtr h)
    {
        var n = GetWindowTextLength(h);
        if (n <= 0) return "";
        var sb = new StringBuilder(n + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Win32

    private const int WH_CBT = 5, WH_MSGFILTER = -1, HCBT_ACTIVATE = 5, MSGF_DIALOGBOX = 0;
    private const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104, STM_GETICON = 0x0171;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private static HookProc? _cbtProc, _msgProc;   // giữ delegate sống suốt tiến trình
    private static IntPtr _cbtHook, _msgHook;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public int message; public IntPtr wParam; public IntPtr lParam; public int time; public int ptX, ptY; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
}
