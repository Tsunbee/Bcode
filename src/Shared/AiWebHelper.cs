using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.Shared;

/// <summary>Trang web AI nhúng được vào WebView2.</summary>
internal enum AiSite { Claude, Gemini }

/// <summary>
/// Phần DÙNG CHUNG giữa Bcode.App và BcodeViewer.App để nhúng claude.ai / gemini.google.com vào WebView2 và đưa text vào ô chat.
/// File này được LINK (không chép) vào cả hai csproj (<c>&lt;Compile Include="..\Shared\AiWebHelper.cs" /&gt;</c>) — sửa ở đây là cả hai app cùng đổi.
/// Hai app dùng chung thư mục profile (Bcode\ClaudeWebProfile, Bcode\GeminiWebProfile) nên đăng nhập 1 lần dùng được ở cả hai.
/// </summary>
internal static class AiWebHelper
{
    public const string ClaudeUrl = "https://claude.ai/";
    public const string GeminiUrl = "https://gemini.google.com/";

    private const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>UA Chrome theo đúng phiên bản Chromium của WebView2 đang chạy (UA cố định cũ như Chrome 126 làm Gemini/Google coi là trình duyệt lỗi thời và tắt bớt tính năng, vd đọc file đính kèm → trả lời "chỉ là mô hình ngôn ngữ").</summary>
    private static string ChromeUserAgentFor(string? runtimeVersion)
    {
        var major = (runtimeVersion ?? "").Split('.')[0];
        return int.TryParse(major, out var m) && m >= 100 ? ChromeUserAgent.Replace("Chrome/126.0.0.0", "Chrome/" + m + ".0.0.0") : ChromeUserAgent;
    }

    private static async Task ApplyChromeIdentityAsync(CoreWebView2 core, string? runtimeVersion)
    {
        try
        {
            var full = string.IsNullOrWhiteSpace(runtimeVersion) ? "126.0.0.0" : runtimeVersion.Trim();
            var major = full.Split('.')[0];
            var brands = new object[] { new { brand = "Not)A;Brand", version = "99" }, new { brand = "Google Chrome", version = major }, new { brand = "Chromium", version = major } };
            var fullBrands = new object[] { new { brand = "Not)A;Brand", version = "99.0.0.0" }, new { brand = "Google Chrome", version = full }, new { brand = "Chromium", version = full } };
            var args = new
            {
                userAgent = ChromeUserAgentFor(runtimeVersion), platform = "Windows",
                userAgentMetadata = new { brands, fullVersionList = fullBrands, fullVersion = full, platform = "Windows", platformVersion = "10.0.0", architecture = "x86", model = "", mobile = false, bitness = "64", wow64 = false },
            };
            await core.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride", JsonSerializer.Serialize(args));
        }
        catch { /* không đặt được → vẫn dùng UA ở Settings */ }
    }

    public static string ProfileDir(string appDataRoot, AiSite site) =>
        Path.Combine(appDataRoot, "Bcode", site == AiSite.Claude ? "ClaudeWebProfile" : "GeminiWebProfile");

    /// <summary>Như <see cref="InitAsync"/> nhưng KHÔNG BAO GIỜ ném lỗi: thất bại (vd COMException 0x8007139F "group or resource is not in the correct state" khi thư mục profile đang được
    /// tiến trình WebView2 khác — hoặc lần khởi động trước còn treo — giữ ở trạng thái không khớp) thì thử lại vài lần, vẫn lỗi thì bỏ qua và để khung AI trống. Trước đây lỗi này nằm trong
    /// MainForm_Load (async void) nên làm sập cả cửa sổ BcodeViewer. Trả về true nếu khởi tạo được.</summary>
    public static async Task<bool> InitSafeAsync(WebView2 web, AiSite site, string appDataRoot)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { await InitAsync(web, site, appDataRoot); return true; }
            catch (Exception) when (attempt < 2) { try { await Task.Delay(700 * (attempt + 1)); } catch { } }
            catch (Exception) { return false; }
        }
        return false;
    }

    /// <summary>Khởi tạo <paramref name="web"/> với profile riêng của trang, User-Agent Chrome, bỏ header Client Hints (Sec-CH-UA*) cho khớp User-Agent
    /// (UA nói Chrome 126 mà Client Hints nói WebView2 khiến claude.ai/Google âm thầm tắt tính năng — ô chat không hiện), rồi mở trang chủ.
    /// Không chặn popup accounts.google.com: đăng nhập Google cần popup thật (window.opener/window.close).</summary>
    public static async Task InitAsync(WebView2 web, AiSite site, string appDataRoot)
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: ProfileDir(appDataRoot, site));
        await web.EnsureCoreWebView2Async(env);
        var core = web.CoreWebView2;
        core.Settings.UserAgent = ChromeUserAgentFor(env.BrowserVersionString);
        core.Settings.IsScriptEnabled = true;
        core.Settings.IsWebMessageEnabled = true;

        // Gemini: khai báo UA + Client Hints (header và navigator.userAgentData) khớp nhau như Chrome thật — KHÔNG gỡ header nữa (gỡ làm Gemini thấy trình duyệt bất thường và trả lời
        // "chỉ là mô hình ngôn ngữ" dù Chrome thường vẫn bình thường). Claude giữ cách cũ: gỡ header Sec-CH-UA*.
        if (site == AiSite.Gemini) await ApplyChromeIdentityAsync(core, env.BrowserVersionString);
        else
        {
            foreach (var f in new[] { "https://*.claude.ai/*", "https://*.anthropic.com/*" }) core.AddWebResourceRequestedFilter(f, CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, args) =>
            {
                var headers = args.Request.Headers;
                foreach (var name in new[]
                {
                    "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
                    "sec-ch-ua-full-version", "sec-ch-ua-full-version-list", "sec-ch-ua-platform-version",
                })
                    if (headers.Contains(name)) headers.RemoveHeader(name);
            };
        }

        if (site == AiSite.Claude)
        {
            // Chỉ link anthropic.com (vd "Tìm hiểu thêm") mở ngay trên panel chính; mọi popup khác (OAuth Google...) để WebView2 tự mở popup thật.
            core.NewWindowRequested += (_, args) =>
            {
                var uri = args.Uri;
                if (!string.IsNullOrWhiteSpace(uri) && uri.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    && uri.Contains("anthropic.com") && !uri.Contains("accounts.google.com"))
                {
                    args.Handled = true;
                    core.Navigate(uri);
                }
            };
        }
        core.Navigate(site == AiSite.Claude ? ClaudeUrl : GeminiUrl);
    }

    /// <summary>Chụp lại clipboard hiện tại (text / danh sách file / ảnh) và trả về hàm khôi phục — đính kèm file phải ghi đè clipboard để Ctrl+V thật dán được file.</summary>
    public static Action SnapshotClipboard()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()) { var l = Clipboard.GetFileDropList(); return () => { try { Clipboard.SetFileDropList(l); } catch { } }; }
            if (Clipboard.ContainsText()) { var t = Clipboard.GetText(); return () => { try { Clipboard.SetText(t); } catch { } }; }
            if (Clipboard.ContainsImage()) { var img = Clipboard.GetImage(); if (img is not null) return () => { try { Clipboard.SetImage(img); } catch { } }; }
        }
        catch { /* clipboard đang bị app khác giữ */ }
        return () => { };
    }

    /// <summary>Đính kèm <paramref name="filePath"/> vào ô chat dưới dạng file thật: đặt file lên clipboard Windows (CF_HDROP, ghi đè clipboard rồi trả lại sau
    /// ~1,2 giây), focus ô chat rồi gửi Ctrl+V THẬT qua Chrome DevTools Protocol (isTrusted = true nên trang xử lý như người dùng dán file).
    /// Dùng cho nội dung dài — dán cả khối chữ vào ô chat (nhất là Gemini) rất lag. Trả về null nếu xong, hoặc thông báo lỗi.</summary>
    public static async Task<string?> AttachFileAsync(WebView2 web, string label, string filePath)
    {
        if (web.CoreWebView2 is null) return $"Trang {label} chưa sẵn sàng.";
        if (!File.Exists(filePath)) return "Không tìm thấy file cần đính kèm.";

        var restore = SnapshotClipboard();
        try
        {
            var files = new System.Collections.Specialized.StringCollection { filePath };
            Clipboard.SetFileDropList(files);
        }
        catch { return "Không đặt được file lên Clipboard Windows."; }

        const string focusJs = """
        (function() {
            var candidates = Array.prototype.slice.call(
                document.querySelectorAll('div[contenteditable="true"], textarea'));
            var best = null, bestArea = 0;
            for (var i = 0; i < candidates.length; i++) {
                var el = candidates[i];
                var rect = el.getBoundingClientRect();
                if (rect.width < 100 || rect.height < 20) continue;
                if (el.closest('nav, header')) continue;
                var area = rect.width * rect.height;
                if (area > bestArea) { bestArea = area; best = el; }
            }
            if (best) { best.focus(); return true; }
            return false;
        })();
        """;
        // Trang mới mở có thể chưa vẽ ô chat — thử lại tới ~10 giây.
        var focused = false;
        for (var i = 0; i < 20 && !focused; i++)
        {
            focused = await web.ExecuteScriptAsync(focusJs) == "true";
            if (!focused) await Task.Delay(500);
        }
        if (!focused) { restore(); return $"Không tìm thấy ô chat {label} (chưa đăng nhập?) để dán file vào."; }
        await Task.Delay(200);

        async Task Key(string type, string key, string code, int vk, int modifiers) =>
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new
            {
                type, modifiers, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk, key, code
            }));
        try
        {
            const int ctrl = 2; // CDP Input.Modifier: Alt=1, Ctrl=2, Meta=4, Shift=8
            await Key("rawKeyDown", "Control", "ControlLeft", 0x11, 0);
            await Key("rawKeyDown", "v", "KeyV", 0x56, ctrl);
            await Key("keyUp", "v", "KeyV", 0x56, ctrl);
            await Key("keyUp", "Control", "ControlLeft", 0x11, 0);
            await Task.Delay(1200); // chờ trang đọc xong clipboard
            restore();
            return null;
        }
        catch (Exception ex) { restore(); return "Lỗi khi gửi Ctrl+V qua DevTools Protocol: " + ex.Message; }
    }

    /// <summary>Đưa text nhiều dòng vào ô chat bằng thao tác bàn phím THẬT qua DevTools (Input.insertText cho từng dòng, Shift+Enter giữa các dòng) — dùng cho Claude:
    /// trình soạn thảo của claude.ai bỏ qua xuống dòng khi chèn bằng execCommand nên mọi dòng bị gộp thành một. Không tự gửi (Shift+Enter chỉ xuống dòng).
    /// Trả về null nếu xong, hoặc thông báo lỗi.</summary>
    public static async Task<string?> TypeTextAsync(WebView2 web, string label, string intro, string text)
    {
        if (web.CoreWebView2 is null) return $"Trang {label} chưa sẵn sàng.";
        const string focusClearJs = """
        (function() {
            var candidates = Array.prototype.slice.call(
                document.querySelectorAll('div[contenteditable="true"], textarea'));
            var best = null, bestArea = 0;
            for (var i = 0; i < candidates.length; i++) {
                var el = candidates[i];
                var rect = el.getBoundingClientRect();
                if (rect.width < 100 || rect.height < 20) continue;
                if (el.closest('nav, header')) continue;
                var area = rect.width * rect.height;
                if (area > bestArea) { bestArea = area; best = el; }
            }
            if (!best) return false;
            best.focus();
            if (best.tagName === 'TEXTAREA' || best.tagName === 'INPUT') { best.select(); }
            else { document.execCommand('selectAll', false, null); document.execCommand('delete', false, null); }
            return true;
        })();
        """;
        var ready = false;
        for (var i = 0; i < 40 && !ready; i++) // trang mới mở: chờ ô chat xuất hiện (tối đa ~10 giây)
        {
            ready = await web.ExecuteScriptAsync(focusClearJs) == "true";
            if (!ready) await Task.Delay(250);
        }
        if (!ready) return $"Không tìm thấy ô chat {label} (chưa đăng nhập?).";
        await Task.Delay(300); // để trình soạn thảo kịp nhận focus

        async Task Cdp(string method, object args) =>
            await web.CoreWebView2.CallDevToolsProtocolMethodAsync(method, JsonSerializer.Serialize(args));
        async Task NewLine()
        {
            const int shift = 8; // CDP Input.Modifier: Alt=1, Ctrl=2, Meta=4, Shift=8
            await Cdp("Input.dispatchKeyEvent", new { type = "keyDown", modifiers = shift, windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, key = "Enter", code = "Enter", text = "\r" });
            await Cdp("Input.dispatchKeyEvent", new { type = "keyUp", modifiers = shift, windowsVirtualKeyCode = 13, nativeVirtualKeyCode = 13, key = "Enter", code = "Enter" });
        }
        try
        {
            var lines = (intro + text).Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n').Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) await NewLine();
                if (lines[i].Length > 0) await Cdp("Input.insertText", new { text = lines[i] });
            }
            return null;
        }
        catch (Exception ex) { return "Lỗi khi nhập text qua DevTools Protocol: " + ex.Message; }
    }

    /// <summary>Đưa <paramref name="intro"/> + <paramref name="content"/> vào ô chat của trang (không tự gửi — người dùng gõ câu hỏi rồi Enter).
    /// Dò ô contenteditable/textarea lớn nhất ngoài nav/header, gõ intro rồi phát sự kiện 'paste' mang <paramref name="content"/> (claude.ai gói text dài thành
    /// thẻ PASTED); trang không xử lý paste thì chèn thẳng <paramref name="fallback"/>. Cấu trúc trang là đoán nên hỏng thì im lặng bỏ qua.</summary>
    public static async Task InsertTextAsync(WebView2 web, string intro, string content, string fallback, bool usePaste = true)
    {
        if (web.CoreWebView2 is null) return;
        var js = $$"""
        (function() {
            var intro = {{JsonSerializer.Serialize(intro)}};
            var content = {{JsonSerializer.Serialize(content)}};
            var fallback = {{JsonSerializer.Serialize(fallback)}};
            var usePaste = {{(usePaste ? "true" : "false")}};

            function findComposer() {
                var candidates = Array.prototype.slice.call(
                    document.querySelectorAll('div[contenteditable="true"], textarea'));
                var best = null, bestArea = 0;
                for (var i = 0; i < candidates.length; i++) {
                    var el = candidates[i];
                    var rect = el.getBoundingClientRect();
                    if (rect.width < 100 || rect.height < 20) continue;
                    if (el.closest('nav, header')) continue;
                    var area = rect.width * rect.height;
                    if (area > bestArea) { bestArea = area; best = el; }
                }
                return best;
            }
            function setTextareaValue(el, text) {
                var setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
                setter.call(el, text);
                el.dispatchEvent(new Event('input', { bubbles: true }));
            }
            function pasteText(el, text) {
                try {
                    var dt = new DataTransfer();
                    dt.setData('text/plain', text);
                    var ev = new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true });
                    el.dispatchEvent(ev);
                    return ev.defaultPrevented;
                } catch (e) {
                    return false;
                }
            }
            // claude.ai chỉ gói text dài thành thẻ PASTED khi ô chat đang giữ focus lúc nhận paste.
            function focusComposer(el) {
                el.focus();
                try {
                    var range = document.createRange();
                    range.selectNodeContents(el);
                    range.collapse(false);
                    var sel = window.getSelection();
                    sel.removeAllRanges();
                    sel.addRange(range);
                } catch (e) { }
            }
            function composerHasFocus(el) {
                var active = document.activeElement;
                return document.hasFocus() && active && (active === el || el.contains(active));
            }
            // execCommand('insertText') với "\n" bị trình soạn thảo (Gemini/Quill...) gom hết về 1 dòng → chèn từng dòng, giữa các dòng chèn xuống dòng mềm.
            function insertMultiline(text) {
                var lines = String(text).split(/\r?\n/);
                for (var i = 0; i < lines.length; i++) {
                    if (i > 0) document.execCommand('insertLineBreak', false, null);
                    if (lines[i]) document.execCommand('insertText', false, lines[i]);
                }
            }
            function inject(el) {
                if (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') {
                    el.focus();
                    setTextareaValue(el, intro + fallback);
                    return;
                }
                focusComposer(el);
                document.execCommand('selectAll', false, null);
                document.execCommand('delete', false, null);
                insertMultiline(intro);
                if (!usePaste || !pasteText(el, content)) {
                    insertMultiline(fallback);
                }
            }
            // Trang mới mở: ô chat có thể đã hiện nhưng trình soạn thảo chưa khởi tạo xong nên nội dung vừa chèn bị xoá/không nhận (lần gửi đầu "không có dữ liệu").
            // Chèn xong thì kiểm tra lại sau ~0,9 giây; ô vẫn trống thì chèn lại (tối đa 5 lần).
            function injectVerified(el, tries) {
                inject(el);
                setTimeout(function() {
                    var cur = findComposer() || el;
                    var txt = (cur.value !== undefined ? cur.value : cur.innerText) || '';
                    if (txt.trim().length === 0 && tries < 5) {
                        focusComposer(cur);
                        injectVerified(cur, tries + 1);
                    }
                }, 900);
            }
            function injectWhenFocused(el) {
                var waited = 0;
                (function tryInject() {
                    focusComposer(el);
                    if (composerHasFocus(el) || waited >= 3000) {
                        setTimeout(function() { injectVerified(el, 0); }, 400);
                        return;
                    }
                    waited += 100;
                    setTimeout(tryInject, 100);
                })();
            }

            var attempts = 0;
            var timer = setInterval(function() {
                attempts++;
                var el = findComposer();
                if (el) {
                    clearInterval(timer);
                    injectWhenFocused(el);
                } else if (attempts > 40) {
                    clearInterval(timer);
                }
            }, 250);
        })();
        """;
        try { await web.ExecuteScriptAsync(js); }
        catch { /* trang chưa load xong hoặc đổi cấu trúc — bỏ qua, không phá vỡ app chính */ }
    }
}
