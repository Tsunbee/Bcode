using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Bcode.App.Models;

namespace Bcode.App.Forms;

/// <summary>
/// Tab "TK2 Help" / "Forum TK2 Help" (menu Triển khai): web nội bộ TK2 Help (IIS, http://172.168.5.11/TK2_HELP/…) mở ngay trong tab tài liệu của Bcode
/// (MainForm.AddFormTab — cùng cơ chế FSG FBO / FSG Yêu cầu). Trang là site NGOÀI: WebView2 không gắn cầu của Bcode (không host object, không nhận web
/// message, không chèn script phím tắt — phím tắt toàn cục bắt ở tầng WebView2 qua WebViewEnvironment.AttachGlobalShortcuts(acceleratorOnly)).
/// Hồ sơ trình duyệt "tk2help" trong thư mục dữ liệu WebView2 mặc định như FSG FBO → giữ phiên đăng nhập giữa các lần mở, 2 tab dùng chung phiên.
/// Thanh đăng nhập: User / Pass (☑ Nhớ → AppSettings.Tk2HelpUser / Tk2HelpPassword, mật khẩu mã hoá DPAPI) — trang đăng nhập hiện ra thì tự điền và bấm
/// Đăng nhập (1 lần mỗi tab; sai mật khẩu thì không thử lại). Link mở cửa sổ mới (target=_blank, window.open) → tab TK2 Help mới (như FSG FBO).
/// </summary>
public class Tk2HelpForm : Bcode.App.UI.DpiForm
{
    /// <summary>Tên hồ sơ WebView2 dùng chung cho mọi tab TK2 Help (cookie đăng nhập chung).</summary>
    private const string ProfileName = "tk2help";

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtUrl = new();
    private readonly TextBox _txtUser = new() { Width = Bcode.App.UI.NativeSkin.S(150) };
    private readonly TextBox _txtPass = new() { Width = Bcode.App.UI.NativeSkin.S(150), UseSystemPasswordChar = true };
    private readonly CheckBox _chkRemember = new() { Text = "Nhớ (lần sau tự đăng nhập)", AutoSize = true, Checked = true };
    private readonly Bcode.App.Controls.PillButton _btnLogin = Bcode.App.Controls.PillButton.Flat("▶ Đăng nhập", primary: true);
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new("Đang mở trang...") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Control _loginBar;
    private readonly Bcode.App.Controls.PillButton _btnAccount;

    private readonly string _startUrl;
    private readonly bool _popup;
    private readonly TaskCompletionSource<CoreWebView2> _coreReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Tự đăng nhập chỉ 1 lần mỗi tab (và mỗi lần bấm "▶ Đăng nhập"): sai mật khẩu thì trang đăng nhập hiện lại — không lặp vô hạn.
    private bool _autoLoginDone;
    private bool _loginPending;
    // Trang đang xem lúc tự chuyển sang trang đăng nhập (Forum dạng khách) — đăng nhập xong quay lại đây.
    private string? _returnUrl;
    // window.APP_BASE của trang TK2 Help ("/TK2_HELP"; null = chưa đọc được → lấy đoạn đầu đường dẫn) — xem Tk2HelpUrls.
    private string? _pageBase;

    private const string AppBaseScript = "(typeof window.APP_BASE==='string'?window.APP_BASE:null)";

    /// <summary>Tên người đang đăng nhập theo trang TK2 Help (JSON trong &lt;script id="__ctx__"&gt;): "" = khách; null = trang không có thông tin này.</summary>
    private const string GuestCheckScript =
        "(function(){try{var e=document.getElementById('__ctx__');if(!e)return null;var c=JSON.parse(e.textContent);return typeof c.username==='string'?c.username:null;}catch(x){return null;}})()";

    /// <summary>Do MainForm gán: mở 1 tab TK2 Help "cửa sổ mới" cho trang muốn mở cửa sổ mới. Null = để WebView2 tự mở cửa sổ riêng.</summary>
    public Func<Tk2HelpForm?>? NewWindowTab { get; set; }

    private readonly AppSettings _settings;

    /// <param name="settings">Bản AppSettings của MainForm — nhớ tài khoản thì ghi cả vào bản này (MainForm lưu lại settings sau đó không đè mất).</param>
    public Tk2HelpForm(AppSettings settings, string startUrl, bool popup = false)
    {
        _settings = settings;
        // Bỏ "/" cuối, thêm http:// nếu thiếu (Tk2HelpUrls.Normalize) — "/TK2_HELP/forum/" ở Template thì server báo 404.
        _startUrl = string.IsNullOrWhiteSpace(startUrl) ? AppSettings.Tk2HelpDefaultUrl : Tk2HelpUrls.Normalize(startUrl);
        _popup = popup;
        Text = "TK2 Help";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;

        _txtUser.Text = settings.Tk2HelpUser ?? "";
        _txtPass.Text = settings.Tk2HelpPassword ?? "";
        if (_txtUser.Text.Length == 0)
        {
            // Nhớ ở 1 cửa sổ Bcode khác (bản settings trong bộ nhớ của cửa sổ này cũ hơn) → đọc lại settings.json.
            try { var disk = AppSettings.Load(); _txtUser.Text = disk.Tk2HelpUser ?? ""; _txtPass.Text = disk.Tk2HelpPassword ?? ""; }
            catch { /* settings hỏng — để trống */ }
        }

        // ---- 2 thanh trên cùng, cùng kiểu tab FSG FBO ----
        var rowH = Bcode.App.UI.NativeSkin.RowHeight(this);
        Bcode.App.Controls.PillButton Pill(string text, string tip)
        {
            var b = Bcode.App.Controls.PillButton.Flat(text);
            b.OnPanelAlt = true;
            b.Height = rowH;
            b.Margin = new Padding(0, 0, 6, 0);
            b.TabStop = false;
            if (tip.Length > 0) Bcode.App.Controls.NK.Tip(b, tip);
            return b;
        }
        Padding Mid(Control c, int left = 0, int right = 6) { var top = Math.Max(0, (rowH - c.PreferredSize.Height) / 2); return new Padding(left, top, right, 0); }
        Label Caption(string text) { var l = new Bcode.App.Controls.MutedLabel { Text = text, AutoSize = true, BackColor = Color.Transparent }; l.Margin = Mid(l, 0, 4); return l; }

        // Hàng 1 — đăng nhập: User [__] Pass [__] ☑ Nhớ  [▶ Đăng nhập]
        var loginBar = new Bcode.App.Controls.BarFlow
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Padding = new Padding(8, 5, 8, 4),
        };
        var title = new Label { Text = "TK2 Help", AutoSize = true, BackColor = Color.Transparent, Font = new Font(Font, FontStyle.Bold) };
        title.Margin = Mid(title, 0, 14);
        loginBar.Controls.Add(title);
        loginBar.Controls.Add(Caption("User"));
        _txtUser.Margin = Mid(_txtUser, 0, 12);
        loginBar.Controls.Add(_txtUser);
        loginBar.Controls.Add(Caption("Pass"));
        _txtPass.Margin = Mid(_txtPass, 0, 12);
        loginBar.Controls.Add(_txtPass);
        _chkRemember.BackColor = Color.Transparent;
        _chkRemember.Margin = Mid(_chkRemember, 0, 14);
        loginBar.Controls.Add(_chkRemember);
        _btnLogin.OnPanelAlt = true; _btnLogin.Height = rowH; _btnLogin.Margin = new Padding(0, 0, 6, 0);
        Bcode.App.Controls.NK.Tip(_btnLogin, "Điền User / Pass vào trang đăng nhập TK2 Help và đăng nhập (Enter ở ô Pass). Tích \"Nhớ\" thì lần sau tự đăng nhập.");
        loginBar.Controls.Add(_btnLogin);
        _btnLogin.Click += async (_, _) => await LoginClickedAsync();
        _txtPass.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            await LoginClickedAsync();
        };
        _loginBar = loginBar;

        // Hàng 2 — thanh địa chỉ: ◀ ▶ ⟳ ⌂ [URL] [🌐 Trình duyệt ngoài] [👤 Tài khoản]
        var btnBack = Pill("◀", "Quay lại");
        var btnForward = Pill("▶", "Tiến tới");
        var btnReload = Pill("⟳", "Tải lại");
        var btnHome = Pill("⌂", "Về trang mở đầu của tab: " + _startUrl);
        var btnExternal = Pill("🌐 Trình duyệt ngoài", "Mở trang đang xem bằng trình duyệt mặc định của Windows");
        btnExternal.Margin = new Padding(6, 0, 0, 0);
        _btnAccount = Pill("👤 Tài khoản", "Ẩn / hiện thanh User / Pass (trang đăng nhập hiện ra thì tự hiện)");
        _btnAccount.Margin = new Padding(6, 0, 0, 0);
        _txtUrl.Margin = Mid(_txtUrl, 0, 0);
        _txtUrl.Dock = DockStyle.Fill;
        var navBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 7, RowCount = 1,
            Padding = new Padding(8, 3, 8, 5), BackColor = Bcode.App.UI.AppColors.PanelAlt,
        };
        for (var i = 0; i < 4; i++) navBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 2; i++) navBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navBar.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
        navBar.Controls.Add(btnBack, 0, 0);
        navBar.Controls.Add(btnForward, 1, 0);
        navBar.Controls.Add(btnReload, 2, 0);
        navBar.Controls.Add(btnHome, 3, 0);
        navBar.Controls.Add(_txtUrl, 4, 0);
        navBar.Controls.Add(btnExternal, 5, 0);
        navBar.Controls.Add(_btnAccount, 6, 0);
        void KeepBarColor() { navBar.BackColor = Bcode.App.UI.AppColors.PanelAlt; }
        navBar.HandleCreated += (_, _) => BeginInvoke(new Action(KeepBarColor));
        Bcode.App.UI.ThemeManager.ThemeChanged += KeepBarColor;
        Disposed += (_, _) => Bcode.App.UI.ThemeManager.ThemeChanged -= KeepBarColor;

        btnBack.Click += (_, _) => { if (_web.CoreWebView2?.CanGoBack == true) _web.CoreWebView2.GoBack(); };
        btnForward.Click += (_, _) => { if (_web.CoreWebView2?.CanGoForward == true) _web.CoreWebView2.GoForward(); };
        btnReload.Click += (_, _) => _web.CoreWebView2?.Reload();
        btnHome.Click += (_, _) => Go(_startUrl);
        btnExternal.Click += (_, _) => OpenExternal(_web.CoreWebView2?.Source ?? _startUrl);
        _btnAccount.Click += (_, _) => SetLoginBar(!_loginBar.Visible);
        _txtUrl.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            Go(_txtUrl.Text);
        };
        _txtUrl.GotFocus += (_, _) => _txtUrl.SelectAll();

        _statusStrip.Items.Add(_statusLabel);
        Controls.Add(_web);
        Controls.Add(navBar);
        Controls.Add(loginBar);
        Controls.Add(_statusStrip);
        // Chưa nhớ tài khoản thì hiện sẵn thanh đăng nhập; đã nhớ thì ẩn (trang đăng nhập hiện ra sẽ tự hiện lại).
        SetLoginBar(!_popup && (_txtUser.Text.Trim().Length == 0 || _txtPass.Text.Length == 0));

        Load += async (_, _) => await InitWebAsync();
    }

    private void SetLoginBar(bool visible)
    {
        _loginBar.Visible = visible;
        _btnAccount.Text = visible ? "👤 Tài khoản ▲" : "👤 Tài khoản";
    }

    private void SetStatus(string text) { try { _statusLabel.Text = text; } catch { /* form đang đóng */ } }

    private async Task InitWebAsync()
    {
        try
        {
            // Hồ sơ riêng "tk2help" trong thư mục dữ liệu WebView2 mặc định (như FSG FBO) — cookie đăng nhập giữ giữa các lần mở Bcode.
            _web.CreationProperties ??= new CoreWebView2CreationProperties();
            _web.CreationProperties.ProfileName = ProfileName;
            await _web.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            SetStatus(Bcode.App.UI.WebViewEnvironment.FriendlyInitMessage("trang TK2 Help", ex).Replace("\n", " "));
            return;
        }
        if (IsDisposed) return;
        Bcode.App.UI.UiScale.BindZoom(_web);
        var core = _web.CoreWebView2;
        // Site ngoài, không tin cậy: không cho trang gọi về Bcode (không host object, không web message).
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsScriptEnabled = true;
        core.SourceChanged += (_, _) => { if (!_txtUrl.Focused) _txtUrl.Text = core.Source; };
        // Chốt chặn: URL lặp thư mục ứng dụng ("/TK2_HELP/TK2_HELP/forum" — vd server ghép next sai) → đi thẳng URL đúng.
        core.NavigationStarting += (_, e) =>
        {
            if (Tk2HelpUrls.FixRepeatedBase(e.Uri, CurrentAppBase(e.Uri)) is not { } fixedUrl) return;
            e.Cancel = true;
            BeginInvoke(new Action(() => Go(fixedUrl)));
        };
        core.NavigationCompleted += async (_, e) =>
        {
            if (!_txtUrl.Focused) _txtUrl.Text = core.Source;
            if (!e.IsSuccess && _loginPending && _returnUrl is { } back && !IsLoginPage(core.Source))
            {
                // Vừa đăng nhập xong mà trang server chuyển tới lỗi (404…) → quay về trang đang xem trước khi đăng nhập.
                _loginPending = false; _returnUrl = null; SetLoginBar(false);
                SetStatus("Đã đăng nhập TK2 Help.");
                if (!string.Equals(core.Source, back, StringComparison.OrdinalIgnoreCase)) Go(back);
                return;
            }
            if (!e.IsSuccess)
            {
                SetStatus($"Không mở được trang ({e.WebErrorStatus}) — kiểm tra mạng nội bộ / đường dẫn ở Settings → Template → Đường dẫn → TK2 Help.");
                return;
            }
            await OnPageLoadedAsync();
        };
        core.NewWindowRequested += OnNewWindowRequested;
        core.WindowCloseRequested += (_, _) => { if (_popup) BeginInvoke(new Action(Close)); };

        if (_popup)
        {
            SetStatus("Cửa sổ mới mở từ trang TK2 Help.");
            _coreReady.TrySetResult(core);
            return;
        }
        Go(_startUrl);
    }

    private void Go(string? url)
    {
        var text = (url ?? "").Trim();
        if (text.Length == 0 || _web.CoreWebView2 is null) return;
        if (!text.Contains("://")) text = "http://" + text;   // gõ thiếu giao thức (172.168.5.11/TK2_HELP/...) thì tự thêm http://
        try { _web.CoreWebView2.Navigate(text); }
        catch (Exception ex) { SetStatus("URL không hợp lệ: " + ex.Message); }
    }

    private static void OpenExternal(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* không có trình duyệt mặc định */ }
    }

    /// <summary>Trang đăng nhập của TK2 Help: đường dẫn kết thúc bằng "/login" (kể cả ?next=...).</summary>
    private static bool IsLoginPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.AbsolutePath.TrimEnd('/').EndsWith("/login", StringComparison.OrdinalIgnoreCase);

    /// <summary>Thư mục ứng dụng TK2 Help của <paramref name="url"/>: APP_BASE đọc từ trang (cùng site với trang mở đầu), không thì đoạn đầu của đường dẫn trang mở đầu.</summary>
    private string CurrentAppBase(string? url)
    {
        if (!Uri.TryCreate(_startUrl, UriKind.Absolute, out var start)) return "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && !string.Equals(u.Authority, start.Authority, StringComparison.OrdinalIgnoreCase)) return "";
        return Tk2HelpUrls.AppBase(start, _pageBase);
    }

    private async Task OnPageLoadedAsync()
    {
        var src = _web.CoreWebView2?.Source;
        if (_web.CoreWebView2 is { } c0 && Uri.TryCreate(src, UriKind.Absolute, out var s0) && Uri.TryCreate(_startUrl, UriKind.Absolute, out var st0)
            && string.Equals(s0.Authority, st0.Authority, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var b = await c0.ExecuteScriptAsync(AppBaseScript);
                if (b != "null") _pageBase = JsonSerializer.Deserialize<string>(b);
            }
            catch { /* giữ giá trị cũ */ }
        }
        if (!IsLoginPage(src))
        {
            if (_loginPending)
            {
                _loginPending = false; SetLoginBar(false); SetStatus("Đã đăng nhập TK2 Help.");
                // Tự đăng nhập từ 1 trang khác (vd Forum): đăng nhập xong mà TK2 Help không đưa về đúng trang đó thì tự quay lại.
                if (_returnUrl is { } back) { _returnUrl = null; if (!string.Equals(src, back, StringComparison.OrdinalIgnoreCase)) Go(back); }
                return;
            }
            SetStatus("");
            _returnUrl = null;   // vào trang khác mà không qua đăng nhập (vd đã đăng nhập sẵn) — bỏ trang chờ quay lại
            // Trang xem được không cần đăng nhập (Forum, trang chủ… — "Bạn đang xem với tư cách khách"): đã nhớ tài khoản mà trang báo
            // chưa đăng nhập (JSON <script id="__ctx__"> có username rỗng) → sang trang đăng nhập (?next= trang này) để tự đăng nhập, rồi quay lại.
            if (_autoLoginDone || _txtUser.Text.Trim().Length == 0 || _txtPass.Text.Length == 0 || _web.CoreWebView2 is null) return;
            string who;
            try { who = await _web.CoreWebView2.ExecuteScriptAsync(GuestCheckScript); }
            catch { return; }
            if (who != "\"\"" || !Uri.TryCreate(src, UriKind.Absolute, out var cur)) return;   // đã đăng nhập, hoặc trang không phải TK2 Help (không có __ctx__)
            _returnUrl = src;
            SetStatus("Chưa đăng nhập — tự đăng nhập TK2 Help bằng tài khoản đã nhớ...");
            Go(LoginUrlWithNext(cur));
            return;
        }
        if (_loginPending)
        {
            // Vừa bấm đăng nhập mà vẫn ở trang đăng nhập (trang tải lại) → coi như sai tài khoản.
            _loginPending = false;
            SetLoginBar(true);
            SetStatus("Chưa đăng nhập được — kiểm tra User / Pass rồi bấm \"▶ Đăng nhập\".");
            return;
        }
        SetLoginBar(true);
        var user = _txtUser.Text.Trim();
        var pass = _txtPass.Text;
        if (_autoLoginDone || user.Length == 0 || pass.Length == 0)
        {
            SetStatus(user.Length == 0 || pass.Length == 0
                ? "Gõ User / Pass vào thanh trên rồi bấm \"▶ Đăng nhập\" (tích \"Nhớ\" để lần sau Bcode tự đăng nhập)."
                : "Trang đăng nhập TK2 Help.");
            return;
        }
        _autoLoginDone = true;
        await SubmitLoginAsync(user, pass);
    }

    private async Task LoginClickedAsync()
    {
        var user = _txtUser.Text.Trim();
        var pass = _txtPass.Text;
        if (user.Length == 0 || pass.Length == 0) { SetStatus("Nhập đủ User và Pass."); return; }
        RememberCredentials(user, pass);
        _autoLoginDone = true;
        if (_web.CoreWebView2 is null) return;
        if (!IsLoginPage(_web.CoreWebView2.Source))
        {
            // Đang ở trang khác: về trang đăng nhập trước (TK2 Help đã đăng nhập thì tự chuyển đi; chưa thì OnPageLoadedAsync không tự điền lần nữa
            // vì _autoLoginDone — nên cho phép 1 lần tự điền nữa cho lượt bấm này).
            _autoLoginDone = false;
            var src = _web.CoreWebView2.Source;
            if (Uri.TryCreate(src, UriKind.Absolute, out var cur)) { _returnUrl = src; Go(LoginUrlWithNext(cur)); }
            else Go(LoginUrlFor(src));
            return;
        }
        await SubmitLoginAsync(user, pass);
    }

    /// <summary>Trang đăng nhập cùng site với <paramref name="url"/>: thư mục ứng dụng (/TK2_HELP) + "/login".</summary>
    private string LoginUrlFor(string? url)
    {
        if (IsLoginPage(_startUrl)) return _startUrl.Split('?')[0];
        if (!Uri.TryCreate(url ?? _startUrl, UriKind.Absolute, out var u)) return AppSettings.Tk2HelpDefaultUrl;
        return Tk2HelpUrls.LoginUrl(u, PageAppBase(u));
    }

    /// <summary>Trang đăng nhập kèm "?next=" — next TƯƠNG ĐỐI với thư mục ứng dụng ("/forum", server tự thêm "/TK2_HELP").
    /// Gửi nguyên "/TK2_HELP/forum" thì đăng nhập xong server chuyển tới "/TK2_HELP/TK2_HELP/forum" → Not Found.</summary>
    private string LoginUrlWithNext(Uri page)
    {
        var appBase = PageAppBase(page);
        var next = Tk2HelpUrls.NextFor(page, appBase);
        return LoginUrlFor(page.AbsoluteUri) + "?next=" + Uri.EscapeDataString(next);
    }

    /// <summary>Thư mục ứng dụng của trang <paramref name="u"/>: APP_BASE đã đọc nếu cùng site với trang mở đầu, không thì đoạn đầu đường dẫn.</summary>
    private string PageAppBase(Uri u) =>
        Uri.TryCreate(_startUrl, UriKind.Absolute, out var start) && string.Equals(u.Authority, start.Authority, StringComparison.OrdinalIgnoreCase)
            ? Tk2HelpUrls.AppBase(u, _pageBase)
            : Tk2HelpUrls.AppBase(u, null);

    private void RememberCredentials(string user, string pass)
    {
        var remember = _chkRemember.Checked;
        _settings.Tk2HelpUser = remember ? user : "";
        _settings.Tk2HelpPassword = remember ? pass : "";
        try
        {
            AppSettings.Update(s =>
            {
                s.Tk2HelpUser = remember ? user : "";
                s.Tk2HelpPassword = remember ? pass : "";
            });
        }
        catch { /* không ghi được settings — vẫn đăng nhập cho phiên này */ }
    }

    // Trang đăng nhập TK2 Help là React + Ant Design (Form.Item name="username" / "password" → input#username / input#password), nút type=submit.
    // Gán value qua setter gốc + bắn input/change để React nhận, rồi bấm nút. Chờ form vẽ xong tối đa ~5 giây.
    private const string LoginScript = @"
(async function (u, p) {
  function pick() {
    var pass = document.querySelector('input#password') || document.querySelector('form input[type=password]');
    if (!pass) return null;
    var form = pass.closest('form') || document;
    var user = form.querySelector('input#username') || form.querySelector('input[type=text]:not([tabindex=""-1""]), input:not([type]):not([tabindex=""-1""])');
    return user ? { user: user, pass: pass, form: form } : null;
  }
  var x = null;
  for (var i = 0; i < 50 && !(x = pick()); i++) await new Promise(function (r) { setTimeout(r, 100); });
  if (!x) return 'NO_FORM';
  var setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
  function set(el, v) { el.focus(); setter.call(el, v); el.dispatchEvent(new Event('input', { bubbles: true })); el.dispatchEvent(new Event('change', { bubbles: true })); }
  set(x.user, u); set(x.pass, p);
  await new Promise(function (r) { setTimeout(r, 150); });
  var btn = x.form.querySelector && x.form.querySelector('button[type=submit]');
  if (btn) btn.click(); else if (x.form.requestSubmit) x.form.requestSubmit(); else return 'NO_BUTTON';
  return 'SUBMITTED';
})";

    private async Task SubmitLoginAsync(string user, string pass)
    {
        if (_web.CoreWebView2 is null) return;
        SetStatus("Đang đăng nhập TK2 Help...");
        _loginPending = true;
        try
        {
            await _web.CoreWebView2.ExecuteScriptAsync($"{LoginScript}({JsonSerializer.Serialize(user)}, {JsonSerializer.Serialize(pass)})");
        }
        catch (Exception ex) { _loginPending = false; SetStatus("Không tự đăng nhập được: " + ex.Message); return; }
        // TK2 Help báo sai mật khẩu ngay trên trang (không tải lại) → sau vài giây vẫn ở trang đăng nhập thì báo.
        await Task.Delay(5000);
        if (IsDisposed || !_loginPending || !IsLoginPage(_web.CoreWebView2?.Source)) return;
        _loginPending = false;
        SetLoginBar(true);
        SetStatus("Chưa đăng nhập được — xem thông báo trên trang, kiểm tra User / Pass rồi bấm \"▶ Đăng nhập\".");
    }

    /// <summary>Trang muốn mở cửa sổ mới → mở thành 1 tab TK2 Help trong Bcode (như FSG FBO). Tab chưa sẵn sàng sau 15 giây hoặc lỗi
    /// thì để WebView2 tự mở cửa sổ như mặc định.</summary>
    private async void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        if (NewWindowTab is null) return;
        var deferral = e.GetDeferral();
        try
        {
            if (NewWindowTab() is not { } tab) return;
            var ready = await Task.WhenAny(tab._coreReady.Task, Task.Delay(15_000));
            if (ready != tab._coreReady.Task) { tab.Close(); return; }
            e.NewWindow = tab._coreReady.Task.Result;
            e.Handled = true;
        }
        catch { /* để mặc định */ }
        finally { deferral.Complete(); }
    }
}
