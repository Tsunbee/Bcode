namespace Bcode.App.UI;

/// <summary>
/// One shared <see cref="Microsoft.Web.WebView2.Core.CoreWebView2Environment"/> for the whole
/// app, reused by every WebView2 control (MainForm's shell chrome, RawSqlControl's toolbar,
/// and any more added later) instead of each one creating its own. Multiple WebView2 controls
/// on one environment share the same underlying browser process — only a cheap per-control
/// renderer is spun up per instance — which matters here because tools like "SQL Query" open a
/// brand-new control (and, with it, a brand-new toolbar WebView2) on every tab: without this
/// sharing, opening several tabs would spin up several independent Edge/Chromium processes
/// instead of one, working directly against the "giữ được tốc độ" (keep it fast) requirement
/// that WebView2 was picked for in the first place.
/// </summary>
internal static class WebViewEnvironment
{
    /// <summary>Virtual host name every shell/tool WebView2 page navigates to
    /// (see <see cref="WebFolder"/> for what it maps to).</summary>
    public const string Host = "bcode.shell";

    /// <summary>Local folder served under <see cref="Host"/> — all shell/tool HTML/CSS/JS
    /// pages live flat under here (Web/Shell), copied to the output directory by the
    /// csproj's Content/Web/** item.</summary>
    public static string WebFolder => Path.Combine(AppContext.BaseDirectory, "Web", "Shell");

    private static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment>? _envTask;

    public static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> GetAsync()
    {
        // Lần tạo bị lỗi (vd "Class not registered" khi gọi từ trong callback của WebView2 khác) thì KHÔNG nhớ kết quả lỗi —
        // trước đây task lỗi được cache nên MỌI cửa sổ WebView2 sau đó đều hỏng cho tới khi mở lại app.
        if (_envTask is { IsFaulted: true } or { IsCanceled: true }) _envTask = null;
        return _envTask ??= CreateAsync();
    }

    private static Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> CreateAsync() => CreateForStepAsync();

    // ---- Thư mục dữ liệu bận (0x800700AA "The requested resource is in use") ----
    // Thư mục chính đang bị một trình duyệt msedgewebview2 khác giữ: trình duyệt của lần mở trước chưa thoát hẳn, Bcode trước bị treo/kill
    // để lại trình duyệt mồ côi, hoặc 1 Bcode khác chạy với tham số khác. Lỗi thường chỉ lộ ra lúc tạo controller (EnsureCoreWebView2Async),
    // không phải lúc tạo môi trường → InitAsync báo thất bại về đây (RecoverBusyAsync). Các bước: thử lại thư mục chính BusyRetries lần,
    // cách nhau BusyRetryDelayMs (chờ trình duyệt cũ thoát) → hồ sơ phụ WebView2-1, -2... (thư mục phụ đang bị giữ cũng báo bận ngay → số kế tiếp).
    // Không bao giờ vì thư mục bận mà mất WebView2. Tạo thành công ngay lần đầu thì không có gì khác trước.
    private const int BusyRetries = 5, BusyRetryDelayMs = 600, MaxTempFolders = 30;
    private const string TempFolderPrefix = "WebView2-";
    private static int _busyStep;                     // 0..BusyRetries-1 = thư mục chính; từ BusyRetries = hồ sơ phụ số (_busyStep - BusyRetries + 1)
    private static string? _envFolder;                // thư mục của môi trường hiện tại
    private static bool _tempNoted, _cleanupStarted;
    private static readonly SemaphoreSlim RecoverLock = new(1, 1);

    private static string DataRoot => Path.Combine(BcodePaths.AppData, "Bcode");
    private static string FolderForStep(int step) =>
        Path.Combine(DataRoot, step < BusyRetries ? "WebView2" : TempFolderPrefix + (step - BusyRetries + 1));

    private static async Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> CreateForStepAsync()
    {
        while (true)
        {
            var folder = FolderForStep(_busyStep);
            try
            {
                var env = await CreateWithProfileAsync(folder);
                _envFolder = folder;
                return env;
            }
            catch (Exception ex) when (IsFolderBusy(ex)) { if (!await AdvanceBusyStepAsync(folder, ex)) throw; }
        }
    }

    /// <summary>Thư mục <paramref name="folder"/> vừa báo bận: sang bước kế (chờ nếu còn thử lại thư mục chính). false = hết cách, để lỗi bay ra.</summary>
    private static async Task<bool> AdvanceBusyStepAsync(string folder, Exception ex)
    {
        if (_busyStep + 1 >= BusyRetries + MaxTempFolders) { Log($"Hết hồ sơ phụ để thử ({folder}): {ex.Message}"); return false; }
        _busyStep++;
        if (_busyStep < BusyRetries) await Task.Delay(BusyRetryDelayMs);
        else Log($"{folder} bận ({ex.Message}) — thử {FolderForStep(_busyStep)}.");
        return true;
    }

    /// <summary>Gọi khi tạo controller trên <paramref name="failed"/> báo thư mục bận: trả về môi trường để thử lại (nhiều control cùng lỗi
    /// với 1 môi trường thì chỉ chuyển bước 1 lần — control đến sau nhận luôn môi trường mới).</summary>
    private static async Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> RecoverBusyAsync(
        Microsoft.Web.WebView2.Core.CoreWebView2Environment failed, Exception ex)
    {
        await RecoverLock.WaitAsync();
        try
        {
            if (_envTask is { IsCompletedSuccessfully: true } cur && !ReferenceEquals(cur.Result, failed)) return cur.Result;
            if (!await AdvanceBusyStepAsync(_envFolder ?? FolderForStep(_busyStep), ex)) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
            _envTask = CreateForStepAsync();
            return await _envTask;
        }
        finally { RecoverLock.Release(); }
    }

    /// <summary>Controller đầu tiên đã tạo xong: báo nếu đang dùng hồ sơ tạm, rồi dọn hồ sơ phụ cũ (1 lần mỗi phiên).</summary>
    private static void OnControllerReady()
    {
        if (_busyStep >= BusyRetries && !_tempNoted && _envFolder is { } folder)
        {
            _tempNoted = true;
            TempProfileNote = $"WebView2: thư mục dữ liệu chính đang bị trình duyệt WebView2 khác giữ — phiên này dùng hồ sơ tạm {Path.GetFileName(folder)} " +
                "(dữ liệu web lưu trong trang của Bcode có thể phải làm lại). Đóng hết Bcode rồi mở lại để về hồ sơ chính.";
            Log(TempProfileNote);
            try { TempProfileNotice?.Invoke(TempProfileNote); } catch { }
        }
        if (_cleanupStarted) return;
        _cleanupStarted = true;
        if (_busyStep is > 0 and < BusyRetries) Log($"{_envFolder} bận, thử lại {_busyStep} lần thì được.");
        var inUse = _envFolder;
        _ = Task.Run(() => CleanupTempFolders(DataRoot, inUse));
    }

    /// <summary>Khác null khi phiên này dùng hồ sơ WebView2 tạm (thư mục chính bận) — câu thông báo cho thanh trạng thái.</summary>
    public static string? TempProfileNote { get; private set; }

    /// <summary>Báo lúc vừa chuyển sang hồ sơ tạm (gọi trên luồng bất kỳ).</summary>
    public static event Action<string>? TempProfileNotice;

    /// <summary>0x800700AA (ERROR_BUSY: "The requested resource is in use") hoặc 0x8007139F (ERROR_INVALID_STATE): thư mục dữ liệu đang bị
    /// một tiến trình trình duyệt WebView2 khác giữ, với tham số khác.</summary>
    private static bool IsFolderBusy(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e.HResult is unchecked((int)0x800700AA) or unchecked((int)0x8007139F)) return true;
        return false;
    }

    /// <summary>Xoá các hồ sơ phụ WebView2-n cũ không còn trình duyệt nào giữ. Không bao giờ đụng thư mục chính WebView2 và hồ sơ phiên này
    /// đang dùng. Trình duyệt đang chạy giữ độc quyền file EBWebView\lockfile (thoát êm thì tự xoá file đó) → mở/tạo được lockfile độc quyền
    /// nghĩa là không ai dùng; giữ khoá đó trong lúc xoá để trình duyệt của Bcode khác không chiếm đúng thư mục giữa chừng (nó sẽ báo bận → số kế tiếp).</summary>
    private static void CleanupTempFolders(string root, string? inUse)
    {
        try
        {
            foreach (var dir in Directory.GetDirectories(root, TempFolderPrefix + "*"))
            {
                if (!int.TryParse(Path.GetFileName(dir)[TempFolderPrefix.Length..], out _)) continue;
                if (inUse is not null && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(inUse), StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var eb = Path.Combine(dir, "EBWebView");
                    if (!Directory.Exists(eb))
                    {
                        // Chưa có EBWebView: hồ sơ hỏng, hoặc 1 Bcode khác vừa chọn thư mục này và đang khởi động — chỉ xoá khi đã cũ.
                        if (DateTime.Now - Directory.GetLastWriteTime(dir) > TimeSpan.FromMinutes(10)) Directory.Delete(dir, true);
                        continue;
                    }
                    var lockPath = Path.Combine(eb, "lockfile");
                    using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))   // đang bị giữ → IOException, bỏ qua
                    {
                        foreach (var sub in Directory.GetDirectories(dir)) if (!string.Equals(sub, eb, StringComparison.OrdinalIgnoreCase)) Directory.Delete(sub, true);
                        foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
                        foreach (var sub in Directory.GetDirectories(eb)) Directory.Delete(sub, true);
                        foreach (var f in Directory.GetFiles(eb)) if (!string.Equals(f, lockPath, StringComparison.OrdinalIgnoreCase)) File.Delete(f);
                    }
                    Directory.Delete(dir, true);
                    Log($"Đã dọn hồ sơ WebView2 tạm cũ {Path.GetFileName(dir)}.");
                }
                catch { /* đang được dùng / không xoá được — để lần sau */ }
            }
        }
        catch { }
    }

    private static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(BcodePaths.AppData, "Bcode", "webview2.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [pid {Environment.ProcessId}]  {message}{Environment.NewLine}"); }
        catch { }
    }

    /// <summary>Tạo môi trường kèm tham số của Chế độ hiệu năng (vd giới hạn số tiến trình hiển thị). Không tạo được với tham số
    /// (vd 1 Bcode khác đang chạy chung thư mục dữ liệu với tham số khác) thì tạo như cũ — không bao giờ vì cài đặt này mà mất WebView2.
    /// Cả hai đều báo thư mục bận thì ném lỗi bận để <see cref="CreateForStepAsync"/> chờ/thử lại hoặc dùng hồ sơ phụ.</summary>
    private static async Task<Microsoft.Web.WebView2.Core.CoreWebView2Environment> CreateWithProfileAsync(string userDataFolder)
    {
        string args = "";
        int limit = 0;
        try { var p = PerformanceProfile.Resolve(Models.AppSettings.Load()); args = p.BrowserArguments; limit = p.RendererProcessLimit; }
        catch { /* settings hỏng — chạy như mặc định */ }
        if (args.Length > 0)
        {
            try
            {
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataFolder,
                    new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions(args));
                PerformanceProfile.AppliedRendererLimit = limit;
                return env;
            }
            catch { /* rơi xuống tạo không tham số */ }
        }
        var plain = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        PerformanceProfile.AppliedRendererLimit = 0;
        return plain;
    }

    /// <summary>Ensures <paramref name="web"/> is initialized against the shared environment and
    /// has the shell host mapped in — the two steps every shell/tool WebView2 control needs
    /// before it can Navigate to one of its own pages.</summary>
    public static async Task InitAsync(Microsoft.Web.WebView2.WinForms.WebView2 web)
    {
        // Nền chờ theo theme (trước khi trang vẽ xong): không thì WebView2 loé trắng mỗi lần mở/đổi tab.
        web.DefaultBackgroundColor = AppColors.PanelAlt;
        void Recolor() { try { if (!web.IsDisposed) web.DefaultBackgroundColor = AppColors.PanelAlt; } catch { } }
        ThemeManager.ThemeChanged += Recolor;
        web.Disposed += (_, _) => ThemeManager.ThemeChanged -= Recolor;
        var environment = await GetAsync();
        while (true)
        {
            try { await web.EnsureCoreWebView2Async(environment); break; }
            catch (Exception ex) when (IsFolderBusy(ex)) { environment = await RecoverBusyAsync(environment, ex); }   // thư mục dữ liệu bận — xem RecoverBusyAsync
        }
        OnControllerReady();
        web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            Host, WebFolder, Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        // Tắt phím tắt của trình duyệt (F5/Ctrl+R = tải lại trang, Ctrl+P, F12...): trước đây bấm F5 khi Monaco/thanh công cụ chưa kịp
        // đăng ký phím F5 riêng (vd vừa Ctrl+chuột phải mở store/function ở tab mới) thì WebView2 tải lại cả trang → mất nội dung, trang trắng.
        // Các phím tắt của Bcode (F5 chạy, Ctrl+W...) vẫn do trang/MainForm tự xử lý như cũ.
        web.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;        // Host riêng cho các trang HTML người dùng đã ghi đè (xem UiOverrides): trang nạp từ đây vẫn dùng shell.css/script gốc nhờ thẻ <base>.
        try
        {
            Directory.CreateDirectory(UiOverrides.Folder);
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(UiOverrides.UserHost, UiOverrides.Folder,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        }
        catch { /* không tạo được thư mục tuỳ chỉnh — mọi trang dùng bản gốc */ }
        InstallGlobalShortcuts(web.CoreWebView2);
        InstallAcceleratorShortcuts(web);
        // Trang nằm trong hộp thoại (ShowDialog: Projects, Edit Project...) tự xử lý phím của nó — các phím tắt toàn cửa sổ (vd Ctrl+F5 của
        // MainForm) mà bị bắt ở đây thì MainForm bỏ qua vì đang có hộp thoại khác, và phím không bao giờ tới trang (mất "Synchronize" ở Projects).
        if (IsInDialog(web)) _ = web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync("window.__bcodeNoGlobalKeys=true;");
        UiScale.BindZoom(web);
    }

    private static bool IsInDialog(Microsoft.Web.WebView2.WinForms.WebView2 web)
    {
        try { return web.FindForm() is { Modal: true }; }
        catch { return false; }
    }

    /// <summary>Phím tắt TOÀN CỤC bấm khi focus đang ở trong 1 trang WebView2 (Monaco, thanh công cụ HTML...): phím không đi qua
    /// ProcessCmdKey của form nên MainForm không thấy. Mỗi trang được gắn 1 script bắt phím rồi báo lên đây qua web message
    /// (<c>{action:"__global-shortcut", key}</c> — action lạ nên các trang/handler khác bỏ qua, giống "__height").</summary>
    public static event Action<string>? GlobalShortcut;

    private static long _ctrlDownAt;       // Environment.TickCount64 lúc Ctrl vừa nhấn (0 = không giữ) — dùng chung cho mọi WebView2
    internal const int CtrlHoldMs = 350;    // khớp MainForm.CtrlHoldMs

    /// <summary>Báo 1 phím tắt toàn cửa sổ (tổ hợp "Ctrl+W"… hoặc "@ctrl-hold" / "@ctrl-hold-prev") như khi bấm trong trang WebView2 —
    /// dùng cho cửa sổ ngoài tiến trình được nhúng vào tab (SQL Profiler) mà WinForms không nhận được phím.</summary>
    public static void RaiseGlobalShortcut(string combo) => GlobalShortcut?.Invoke(combo);

    /// <summary>Bật khi trang Template đang chờ người dùng bấm phím để gán — tạm không chặn phím ở tầng WebView2.</summary>
    public static volatile bool SuspendAccelerators;

    /// <summary>Đường bắt phím thứ hai, KHÔNG phụ thuộc script của trang: <c>AcceleratorKeyPressed</c> của WebView2 báo mọi phím trước khi trang thấy,
    /// nên phím tắt toàn cục (Ctrl+W, Ctrl+Tab...) chạy được dù trang chưa nạp xong script, hay focus nằm ở editor/thanh công cụ bất kỳ.</summary>
    private static void InstallAcceleratorShortcuts(Microsoft.Web.WebView2.WinForms.WebView2 web)
    {
        try
        {
            // WinForms WebView2 không công khai controller → lấy qua reflection (field/property nội bộ).
            var t = typeof(Microsoft.Web.WebView2.WinForms.WebView2);
            const System.Reflection.BindingFlags bf = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var controller = (t.GetProperty("CoreWebView2Controller", bf)?.GetValue(web) ?? t.GetField("_coreWebView2Controller", bf)?.GetValue(web))
                as Microsoft.Web.WebView2.Core.CoreWebView2Controller;
            if (controller is null) return;
            controller.AcceleratorKeyPressed += (_, e) =>
            {
                try
                {
                    if (SuspendAccelerators) return; // trang đang ghi phím mới (Template → Phím tắt): để trang nhận mọi tổ hợp
                    var kind = e.KeyEventKind;
                    var isDown = kind == Microsoft.Web.WebView2.Core.CoreWebView2KeyEventKind.KeyDown || kind == Microsoft.Web.WebView2.Core.CoreWebView2KeyEventKind.SystemKeyDown;
                    // Theo dõi thời điểm Ctrl vừa nhấn (bỏ qua phím lặp) để biết đã "giữ Ctrl một lúc" chưa khi Tab tới.
                    if (e.VirtualKey == 0x11) { if (isDown) { if (e.PhysicalKeyStatus.WasKeyDown == 0) _ctrlDownAt = Environment.TickCount64; } else _ctrlDownAt = 0; return; }
                    if (!isDown) return;
                    // Giữ Ctrl đủ lâu rồi bấm Tab → mở hộp chọn tab (Shift: ngược lại); bấm Ctrl+Tab ngay thì đi tiếp xuống dưới để chuyển tab như phím tắt thường.
                    if (e.VirtualKey == 0x09 && (Control.ModifierKeys & Keys.Control) != 0 && (Control.ModifierKeys & Keys.Alt) == 0
                        && _ctrlDownAt != 0 && Environment.TickCount64 - _ctrlDownAt >= CtrlHoldMs)
                    {
                        e.Handled = true;
                        GlobalShortcut?.Invoke((Control.ModifierKeys & Keys.Shift) != 0 ? "@ctrl-hold-prev" : "@ctrl-hold");
                        return;
                    }
                    if (IsInDialog(web)) return;   // hộp thoại: để trang tự xử lý phím (xem InitAsync)
                    var keys = (Keys)e.VirtualKey | Control.ModifierKeys;
                    var combo = ShortcutRegistry.FromKeys(keys);
                    if (combo is null || ShortcutRegistry.AppIdFor(combo) is null) return;
                    e.Handled = true;
                    GlobalShortcut?.Invoke(combo);
                }
                catch { /* phím không xử lý được — để trang tự xử lý */ }
            };
        }
        catch { /* WebView2 đang đóng */ }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Microsoft.Web.WebView2.WinForms.WebView2, object> ShortcutAttached = new();

    /// <summary>Gắn phím tắt toàn cục (Ctrl+W, Ctrl+Tab, Ctrl+Shift+Q/T...) cho 1 WebView2 do thư viện ngoài tạo (vd tab Excel → FRX trong ExcelToFrx.dll):
    /// <see cref="InitAsync"/> chỉ gắn cho WebView2 do Bcode tự khởi tạo, còn WebView2 do DLL tạo thì focus nằm trong đó là mất hết phím tắt của cửa sổ chính.
    /// An toàn khi gọi trước hoặc sau khi WebView2 khởi tạo xong, và gọi nhiều lần cho cùng 1 control.</summary>
    /// <paramref name="acceleratorOnly"/>: trang web bên ngoài (FSG FBO, FSG Yêu cầu) — chỉ bắt phím ở tầng WebView2, không chèn script / nghe web message từ trang lạ.
    public static void AttachGlobalShortcuts(Microsoft.Web.WebView2.WinForms.WebView2 web, bool acceleratorOnly = false)
    {
        if (web is null || !ShortcutAttached.TryAdd(web, new object())) return;
        void Hook()
        {
            if (web.IsDisposed || web.CoreWebView2 is null) return;
            if (!acceleratorOnly) InstallGlobalShortcuts(web.CoreWebView2);
            InstallAcceleratorShortcuts(web);
        }
        if (web.CoreWebView2 is not null) Hook();
        else web.CoreWebView2InitializationCompleted += (_, e) => { if (e.IsSuccess) Hook(); };
    }

    private const string GlobalShortcutScript = @"
(function () {
  var NAMED = { Tab: 'Tab', Enter: 'Enter', NumpadEnter: 'Enter', Space: 'Space', Backspace: 'Backspace', Delete: 'Delete', Insert: 'Insert',
    Home: 'Home', End: 'End', PageUp: 'PageUp', PageDown: 'PageDown', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right',
    Backquote: '`', Minus: '-', Equal: '=', BracketLeft: '[', BracketRight: ']', Backslash: '\\', Semicolon: ';', Quote: ""'"", Comma: ',', Period: '.', Slash: '/' };
  // Tổ hợp dạng chuẩn ""Ctrl+Alt+Shift+Phím"" — trùng định dạng với Bcode.App.UI.ShortcutRegistry.
  // Trang HTML tuỳ chỉnh (host bcode.user): lỗi script lúc nạp → báo C# để cách ly và quay về bản gốc (xem UiOverrides.Quarantine).
  if (location.host === 'bcode.user') {
    var reported = false, t0 = Date.now();
    var report = function (msg) {
      if (reported || Date.now() - t0 > 8000) return; reported = true;
      window.chrome.webview.postMessage(JSON.stringify({ action: '__ui-error', page: location.pathname.split('/').pop(), message: String(msg).slice(0, 200) }));
    };
    window.addEventListener('error', function (e) { report(e.message || 'lỗi script'); });
    window.addEventListener('unhandledrejection', function (e) { report((e.reason && e.reason.message) || e.reason || 'lỗi bất đồng bộ'); });
  }
  // Hop chon tab: giu Ctrl mot luc (>= 350ms) roi bam Tab thi bao ctrl-hold (Shift: ctrl-hold-prev); Ctrl+Tab bam ngay thi di tiep nhu phim tat thuong (chuyen tab).
  var ctrlDownAt = 0;
  document.addEventListener('keydown', function (e) {
    if (e.key === 'Control') {
      if (!e.repeat) ctrlDownAt = Date.now();
      return;
    }
    if (e.key === 'Tab' && e.ctrlKey && !e.altKey && !e.metaKey && ctrlDownAt && Date.now() - ctrlDownAt >= 350) {
      e.preventDefault(); e.stopImmediatePropagation();
      window.chrome.webview.postMessage(JSON.stringify({ action: '__global-shortcut', key: e.shiftKey ? '@ctrl-hold-prev' : '@ctrl-hold' }));
    }
  }, true);
  document.addEventListener('keyup', function (e) { if (e.key === 'Control') ctrlDownAt = 0; }, true);
  window.addEventListener('blur', function () { ctrlDownAt = 0; }, true);
  window.__bcodeCombo = function (e) {
    var c = e.code, k = null;
    if (/^Key[A-Z]$/.test(c)) k = c.charAt(3);
    else if (/^Digit[0-9]$/.test(c)) k = c.charAt(5);
    else if (/^F([1-9]|1[0-9]|2[0-4])$/.test(c)) k = c;
    else if (NAMED[c]) k = NAMED[c];
    if (!k || e.metaKey) return null;
    var m = [];
    if (e.ctrlKey) m.push('Ctrl'); if (e.altKey) m.push('Alt'); if (e.shiftKey) m.push('Shift');
    m.push(k);
    return m.join('+');
  };
  // Danh sách tổ hợp toàn cửa sổ do C# đẩy vào (window.__bcodeKeys — xem UiTemplate.BuildScript): khớp thì chặn phím và báo lên MainForm.
  document.addEventListener('keydown', function (e) {
    var keys = window.__bcodeKeys;
    if (!keys || !keys.length || e.isComposing || window.__bcodeNoGlobalKeys) return;
    var combo = window.__bcodeCombo(e);
    if (combo && keys.indexOf(combo) >= 0) {
      e.preventDefault(); e.stopPropagation();
      window.chrome.webview.postMessage(JSON.stringify({ action: '__global-shortcut', key: combo }));
    }
  }, true);
})();";

    private static void InstallGlobalShortcuts(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        _ = core.AddScriptToExecuteOnDocumentCreatedAsync(GlobalShortcutScript);
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                if (!doc.RootElement.TryGetProperty("action", out var a)) return;
                var action = a.GetString() ?? "";
                if (action == "__keys-suspend") SuspendAccelerators = doc.RootElement.TryGetProperty("data", out var sv) && sv.ValueKind == System.Text.Json.JsonValueKind.True;
                else if (action == "__global-shortcut" && doc.RootElement.TryGetProperty("key", out var k))
                    GlobalShortcut?.Invoke(k.GetString() ?? "");
                else if (action.StartsWith("__ui-", StringComparison.Ordinal))
                    UiOverrides.HandleMessage(action, doc.RootElement.Clone());
            }
            catch { /* không phải JSON của mình — bỏ qua */ }
        };
    }
}
