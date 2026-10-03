using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Bcode.App.Models;

namespace Bcode.App.Forms;

/// <summary>
/// "Bung link chương trình": mở nhanh trang web của 1 Workspace (đúng "Login WLink" đã khai
/// trong Edit Project — Bcode.App/Models/Workspace.cs) và tự điền User/Password để đăng nhập,
/// dùng lại đúng cơ chế JS injection theo control ID chuẩn của FastBusiness
/// (LoginExtender_txtUserName / _cboUnit / _txtPassword / _Ok) đã có sẵn trong
/// FsgRequirementCrawlerForm — nhưng KHÔNG có phần lọc Dự án / Mã YC vì link chương trình nói
/// chung không cần lọc theo yêu cầu như màn FSG.
///
/// User/Password được gán ngay trên thanh công cụ của chính cửa sổ này (giống FSG có sẵn ô
/// nhập User/Pass), không bắt phải quay lại Edit Project mới gõ được — ban đầu điền sẵn từ
/// Workspace (nếu có), Bee gõ/sửa trực tiếp ở đây; tick "Lưu vào Workspace" (mặc định bật) để
/// lần đăng nhập kế tiếp tự điền lại.
///
/// QUAN TRỌNG: lưu vào Workspace.WebLoginUser/WebLoginPassword — 2 trường RIÊNG, KHÔNG phải
/// Workspace.User/Password. User/Password là đăng nhập SQL Server mà DbConnectionService dùng
/// cho MỌI kết nối DB (WCommand, SQL Query, Gen Update...); nếu tái dùng chung 2 trường đó cho
/// đăng nhập WEB (thường khác tài khoản/mật khẩu DB) thì chỉ cần gõ khác đi 1 lần ở đây là toàn
/// bộ kết nối DB của Workspace sẽ hỏng theo (đã xảy ra thực tế — DB báo "Login failed for user
/// ..." vì Password DB bị ghi đè bởi Password web đã gõ ở form này).
///
/// GIẢ ĐỊNH cần Bee xác nhận: các dự án/khách hàng khác cũng dùng chung khung đăng nhập
/// FastBusiness (cùng control ID LoginExtender_*) như trang FSG. Nếu 1 dự án cụ thể có trang
/// đăng nhập khác dạng, auto-fill sẽ không tìm thấy ô nhập và form báo "Không tìm thấy khung
/// đăng nhập chuẩn" thay vì tự đoán sai, để Bee tự đăng nhập tay ngay trên cửa sổ này.
/// </summary>
public class QuickLaunchLoginForm : Bcode.App.UI.DpiForm
{
    private readonly Workspace _ws;
    private readonly AppSettings _settings;

    private readonly TextBox _txtUrl = new();
    private readonly TextBox _txtUser = new() { Width = 130 };
    private readonly TextBox _txtPass = new() { Width = 130, UseSystemPasswordChar = true };
    private readonly CheckBox _chkRemember = new() { Text = "Lưu vào Workspace", AutoSize = true, Checked = true };
    private readonly Button _btnLogin = new() { Text = "▶ Đăng nhập", AutoSize = true, Height = 28 };

    private readonly Button _btnCapture = new() { Text = "💾 Lưu HTML form", AutoSize = true, Height = 28 };

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new("Đang mở...");

    // Sau khi bấm Đăng nhập, nếu tài khoản đã đang đăng nhập ở nơi khác, FastBusiness sẽ hiện
    // 1 modal hỏi "Tài khoản của bạn đang được đăng nhập và sử dụng. Bạn có muốn đóng không?"
    // với 2 nút Có/Không — nút "Có" gọi $find('LoginExtender')._login(true) để đóng phiên cũ
    // và đăng nhập tiếp. Script này dò modal đó (qua class ModalBackgroundChildFormLabel +
    // đúng nội dung) và tự bấm nút "Có" (tìm bằng onclick chứa "_login(true)" rồi .click() —
    // không gọi thẳng $find(...)._login(true) để tránh lỗi khi modal chưa/không tồn tại).
    // Nếu không thấy modal (đăng nhập bình thường, không có phiên cũ) thì trả về 'NO_MODAL' —
    // đây là trường hợp bình thường, không phải lỗi.
    private const string AlreadyLoggedInConfirmScript = @"
    (function() {
        var label = document.querySelector('.ModalBackgroundChildFormLabel');
        if (!label) return 'NO_MODAL';
        var text = label.textContent || '';
        if (text.indexOf('đang được đăng nhập') === -1) return 'NO_MODAL';

        var modal = label.closest('.ModalBackgroundChild') || document.querySelector('.ModalBackgroundChild');
        if (!modal) return 'NO_MODAL';

        var buttons = modal.querySelectorAll('button');
        var btnYes = null;
        for (var i = 0; i < buttons.length; i++) {
            var onclickAttr = buttons[i].getAttribute('onclick') || '';
            if (onclickAttr.indexOf('_login(true)') !== -1) { btnYes = buttons[i]; break; }
        }
        if (!btnYes) return 'NO_BUTTON';

        btnYes.click();
        return 'CONFIRMED';
    })();";

    // Chụp form đang mở (ví dụ form Dir sau khi bấm "Thêm"): outerHTML của mọi table.FormTable (kể cả
    // trong iframe cùng origin), computed style của các class FBO dùng để dựng form, và các luật CSS
    // có chứa Form/Tab/Required lấy từ stylesheet thật. Chỉ ĐỌC trang — không bấm/nhập gì.
    private const string CaptureFormScript = @"
    (function() {
        var CLASSES = ['FormTable','FormRow','FormCell','Required','FormContainer','FormContainerInput',
            'FormContainerInputDisabled','FormInput','FormTextInput','FormLabel'];
        var PROPS = ['font-family','font-size','font-weight','color','background-color','border','border-top',
            'border-bottom','border-left','border-right','padding','margin','height','line-height','text-align',
            'box-sizing','border-radius','white-space','overflow'];
        function styleOf(el) {
            var cs = el.ownerDocument.defaultView.getComputedStyle(el), o = {};
            PROPS.forEach(function(p) { o[p] = cs.getPropertyValue(p); });
            return o;
        }
        function describe(doc) {
            var info = { url: doc.location.href, title: doc.title, tables: [], computed: {}, tabs: [], rules: [] };
            Array.prototype.forEach.call(doc.querySelectorAll('table.FormTable'), function(t) {
                info.tables.push({ id: t.id, width: t.offsetWidth, height: t.offsetHeight, html: t.outerHTML });
            });
            CLASSES.forEach(function(c) {
                var el = doc.querySelector('.' + c);
                if (el) info.computed[c] = { tag: el.tagName, style: styleOf(el) };
            });
            var tabEls = doc.querySelectorAll('[class*=""Tab""],[id*=""Tab""],[role=tab]');
            for (var i = 0; i < tabEls.length && i < 12; i++) {
                var e = tabEls[i];
                info.tabs.push({ tag: e.tagName, id: e.id, cls: e.className, text: (e.textContent || '').trim().slice(0, 40),
                    style: styleOf(e), html: e.outerHTML.slice(0, 600) });
            }
            Array.prototype.forEach.call(doc.styleSheets, function(sh) {
                var rules; try { rules = sh.cssRules; } catch (e) { info.rules.push('/* chặn cross-origin: ' + sh.href + ' */'); return; }
                Array.prototype.forEach.call(rules, function(r) {
                    if (r.cssText && /Form|Tab|Required|Lookup|Calendar/i.test(r.selectorText || '')) info.rules.push(r.cssText);
                });
            });
            return info;
        }
        var docs = [document];
        Array.prototype.forEach.call(document.querySelectorAll('iframe'), function(f) {
            try { if (f.contentDocument) docs.push(f.contentDocument); } catch (e) {}
        });
        var out = docs.map(describe).filter(function(d) { return d.tables.length > 0; });
        return JSON.stringify({ capturedAt: new Date().toISOString(), documents: out });
    })();";

    // Chạy menu từ BcodeViewer (F5): URL trang menu cần mở sau khi đăng nhập xong + ghi chú hiện ở status.
    private string? _pendingUrl;
    private string _pendingNote = "";
    private bool _loggedIn;

    public Workspace Workspace => _ws;

    public QuickLaunchLoginForm(Workspace ws, AppSettings settings, string? startUrl = null, string startNote = "")
    {
        _ws = ws;
        _settings = settings;
        _pendingUrl = startUrl;
        _pendingNote = startNote;

        Text = $"Bung link chương trình — {ws.Name}";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;

        _txtUser.Text = ws.WebLoginUser ?? "";
        _txtPass.Text = ws.WebLoginPassword ?? "";

        var topBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(6, 6, 6, 4),
            WrapContents = false,
            AutoScroll = true
        };

        void AddLabel(string text) =>
            topBar.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(4, 7, 2, 0) });

        AddLabel($"[{ws.Name}]  User:");
        topBar.Controls.Add(_txtUser);
        AddLabel("Pass:");
        topBar.Controls.Add(_txtPass);
        _chkRemember.Margin = new Padding(10, 8, 4, 0);
        topBar.Controls.Add(_chkRemember);
        _btnLogin.Margin = new Padding(10, 5, 4, 0);
        topBar.Controls.Add(_btnLogin);
        _btnLogin.Click += async (_, _) => await AutoLoginAsync();
        _btnCapture.Margin = new Padding(10, 5, 4, 0);
        topBar.Controls.Add(_btnCapture);
        _btnCapture.Click += async (_, _) => await CaptureFormAsync();

        // Tự đăng nhập tay (không có sẵn User/Pass) thì bấm nút này để đi tới menu của file đang chạy từ BcodeViewer.
        var btnMenu = new Button { Text = "➡ Menu", AutoSize = true, Height = 28, Margin = new Padding(10, 5, 4, 0) };
        btnMenu.Click += async (_, _) => { _loggedIn = true; _menuNavigatedOnce = true; await GoToPendingMenuAsync(); };
        topBar.Controls.Add(btnMenu);

        // Enter ở ô Pass cũng kích hoạt đăng nhập luôn, khỏi phải với chuột lên nút.
        _txtPass.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await AutoLoginAsync();
            }
        };

        _statusStrip.Items.Add(_statusLabel);

        // Thanh địa chỉ như trình duyệt: Back / Forward / Reload + ô URL (Enter để đi) + nút ẩn/hiện thanh đăng nhập.
        var navBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = 32, ColumnCount = 5, RowCount = 1, Padding = new Padding(4, 2, 4, 2),
        };
        navBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        navBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        Button NavButton(string text, string tip)
        {
            var b = new Button { Text = text, Width = 34, Height = 26, Margin = new Padding(1, 0, 1, 0), TabStop = false };
            new ToolTip().SetToolTip(b, tip);
            return b;
        }
        var btnBack = NavButton("◀", "Quay lại (Alt+←)");
        var btnForward = NavButton("▶", "Tiến tới (Alt+→)");
        var btnReload = NavButton("⟳", "Tải lại (F5)");
        _txtUrl.Dock = DockStyle.Fill;
        _txtUrl.Margin = new Padding(4, 3, 4, 0);
        var btnToggleLogin = new Button { Text = "▲ Ẩn đăng nhập", AutoSize = true, Height = 26, Margin = new Padding(1, 0, 1, 0), TabStop = false };
        navBar.Controls.Add(btnBack, 0, 0);
        navBar.Controls.Add(btnForward, 1, 0);
        navBar.Controls.Add(btnReload, 2, 0);
        navBar.Controls.Add(_txtUrl, 3, 0);
        navBar.Controls.Add(btnToggleLogin, 4, 0);

        btnBack.Click += (_, _) => { if (_web.CoreWebView2?.CanGoBack == true) _web.CoreWebView2.GoBack(); };
        btnForward.Click += (_, _) => { if (_web.CoreWebView2?.CanGoForward == true) _web.CoreWebView2.GoForward(); };
        btnReload.Click += (_, _) => _web.CoreWebView2?.Reload();
        btnToggleLogin.Click += (_, _) =>
        {
            topBar.Visible = !topBar.Visible;
            btnToggleLogin.Text = topBar.Visible ? "▲ Ẩn đăng nhập" : "▼ Hiện đăng nhập";
        };
        _txtUrl.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            var text = _txtUrl.Text.Trim();
            if (text.Length == 0 || _web.CoreWebView2 is null) return;
            // Gõ thiếu giao thức (172.168.5.14/VPMILK/...) thì tự thêm http://.
            if (!text.Contains("://")) text = "http://" + text;
            try { _web.CoreWebView2.Navigate(text); } catch (Exception ex) { SetStatus("URL không hợp lệ: " + ex.Message); }
        };
        _txtUrl.GotFocus += (_, _) => _txtUrl.SelectAll();

        Controls.Add(_web);
        Controls.Add(navBar);
        Controls.Add(topBar);
        Controls.Add(_statusStrip);

        Load += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_ws.LoginWLink))
            {
                SetStatus("Workspace chưa khai \"Login WLink\" — vào Edit Project để bổ sung.");
                return;
            }

            await _web.EnsureCoreWebView2Async();
            Bcode.App.UI.UiScale.BindZoom(_web);
            _web.CoreWebView2.Settings.IsScriptEnabled = true;
            // Ô URL luôn phản ánh trang đang xem (kể cả khi trang tự chuyển hướng / bấm link trong web).
            _web.CoreWebView2.SourceChanged += (_, _) =>
            {
                if (!_txtUrl.Focused) _txtUrl.Text = _web.CoreWebView2.Source;
            };

            // Chỉ tự bấm đăng nhập ngay khi đã có sẵn User/Pass (từ Workspace) — còn để trống
            // thì chỉ mở trang lên, chờ Bee gõ vào 2 ô trên thanh công cụ rồi tự bấm/Enter.
            if (_txtUser.Text.Trim().Length > 0 && _txtPass.Text.Length > 0)
            {
                await AutoLoginAsync();
            }
            else
            {
                _web.CoreWebView2.Navigate(_ws.LoginWLink);
                SetStatus("Chưa có sẵn User/Pass — gõ vào 2 ô trên thanh công cụ rồi bấm \"▶ Đăng nhập\" (hoặc Enter ở ô Pass).");
            }
        };
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    /// <summary>F5 từ BcodeViewer khi cửa sổ này đang mở sẵn: đã đăng nhập thì đi thẳng tới URL menu (nạp lại luôn bản
    /// source vừa lưu); chưa đăng nhập thì đặt lại URL chờ, đăng nhập xong sẽ tự đi tới.</summary>
    public async Task RunMenuAsync(string? url, string note)
    {
        _pendingUrl = url;
        _pendingNote = note;
        if (_loggedIn) await GoToPendingMenuAsync();
        else SetStatus((string.IsNullOrEmpty(note) ? "" : note + " — ") + "chưa đăng nhập, sẽ tự mở menu sau khi đăng nhập.");
    }

    private async Task GoToPendingMenuAsync()
    {
        var url = _pendingUrl;
        if (string.IsNullOrEmpty(url) || _web.CoreWebView2 is null)
        {
            if (!string.IsNullOrEmpty(_pendingNote)) SetStatus(_pendingNote);
            return;
        }
        // Sau khi bấm Ok, trang đăng nhập còn chuyển hướng vào trang chủ — chờ 1 nhịp để không bị nó ghi đè.
        if (!_menuNavigatedOnce) await Task.Delay(1500);
        _menuNavigatedOnce = true;
        SetStatus($"{_pendingNote}  →  {url}");
        _web.CoreWebView2.Navigate(url);
    }

    private bool _menuNavigatedOnce;

    private async Task AutoLoginAsync()
    {
        if (_web.CoreWebView2 is null || string.IsNullOrWhiteSpace(_ws.LoginWLink)) return;

        var user = _txtUser.Text.Trim();
        var pass = _txtPass.Text;

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            SetStatus("Vui lòng nhập cả User và Password.");
            return;
        }

        if (_chkRemember.Checked) SaveToWorkspace(user, pass);

        _btnLogin.Enabled = false;
        try
        {
            SetStatus("Đang điều hướng đến trang đăng nhập...");
            _web.CoreWebView2.Navigate(_ws.LoginWLink);
            await WaitForPageLoadAsync();
            await Task.Delay(600);

            SetStatus("Đang nhập User và kích hoạt nạp Đơn vị...");
            var triggerUnitScript = $@"
            (function() {{
                var txtUser = document.getElementById('LoginExtender_txtUserName');
                if (!txtUser) return 'NO_USER';
                txtUser.focus();
                txtUser.value = '{JsEscape(user)}';
                txtUser.dispatchEvent(new Event('input', {{ bubbles: true }}));
                txtUser.dispatchEvent(new Event('change', {{ bubbles: true }}));

                if (typeof txtUser.onkeypress === 'function') {{
                    txtUser.onkeypress({{ keyCode: 13, which: 13, charCode: 13, preventDefault: function() {{}}, stopPropagation: function() {{}} }});
                }}

                var ext = window.$find ? window.$find('LoginExtender') : null;
                if (ext) {{
                    try {{ ext.executeCommand({{ commandName: 'Change', commandArgument: '1' }}); }} catch(e) {{}}
                }}
                return 'TRIGGERED';
            }})();";

            var triggerResult = await _web.ExecuteScriptAsync(triggerUnitScript);
            if (triggerResult.Trim('"') == "NO_USER")
            {
                SetStatus("Không tìm thấy khung đăng nhập chuẩn FastBusiness trên trang này — vui lòng đăng nhập tay.");
                return;
            }

            bool unitLoaded = false;
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(400);
                var checkResult = await _web.ExecuteScriptAsync(@"
                (function() {
                    var cbo = document.getElementById('LoginExtender_cboUnit');
                    if (!cbo || !cbo.options) return 0;
                    var count = 0;
                    for (var j = 0; j < cbo.options.length; j++) {
                        if (cbo.options[j].text.trim().length > 0 || cbo.options[j].value.trim().length > 0) count++;
                    }
                    return count;
                })();");

                if (int.TryParse(checkResult, out int validOptions) && validOptions > 0)
                {
                    unitLoaded = true;
                    break;
                }
            }

            if (!unitLoaded)
            {
                SetStatus("Không tự nạp được Đơn vị — bạn có thể tự chọn Đơn vị và đăng nhập tay ngay trên cửa sổ này.");
                return;
            }

            SetStatus("Đang xác thực thông tin đăng nhập...");
            var submitLoginScript = $@"
            (function() {{
                var cbo = document.getElementById('LoginExtender_cboUnit');
                if (cbo && cbo.selectedIndex <= 0 && cbo.options.length > 0) {{
                    for (var j = 0; j < cbo.options.length; j++) {{
                        if (cbo.options[j].text.trim().length > 0) {{ cbo.selectedIndex = j; break; }}
                    }}
                    cbo.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }}

                var txtPass = document.getElementById('LoginExtender_txtPassword');
                if (txtPass) {{
                    txtPass.focus();
                    txtPass.value = '{JsEscape(pass)}';
                    txtPass.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    txtPass.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }}

                var btnOk = document.getElementById('LoginExtender_Ok');
                if (btnOk) btnOk.click();
            }})();";

            await _web.ExecuteScriptAsync(submitLoginScript);
            await Task.Delay(1000);

            // Nếu tài khoản đang đăng nhập ở nơi khác, dò modal xác nhận và tự bấm "Có" giúp
            // Bee — modal xuất hiện gần như ngay sau khi bấm Ok nên chỉ cần dò vài lần ngắn;
            // không thấy sau vài lần thì coi như đăng nhập bình thường (không phải lỗi).
            for (int i = 0; i < 6; i++)
            {
                var confirmResult = await _web.ExecuteScriptAsync(AlreadyLoggedInConfirmScript);
                if (confirmResult.Trim('"') == "CONFIRMED")
                {
                    SetStatus("Tài khoản đang đăng nhập nơi khác — đã tự xác nhận \"Có\" để đóng phiên cũ...");
                    await Task.Delay(800);
                    break;
                }
                await Task.Delay(300);
            }

            _loggedIn = true;
            SetStatus("Đã đăng nhập.");
            await GoToPendingMenuAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi: " + ex.Message);
        }
        finally
        {
            _btnLogin.Enabled = true;
        }
    }

    private async Task CaptureFormAsync()
    {
        if (_web.CoreWebView2 is null) return;
        try
        {
            var raw = await _web.ExecuteScriptAsync(CaptureFormScript);
            var json = System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? "";
            if (!json.Contains("\"tables\":[{"))
            {
                SetStatus("Không thấy table.FormTable nào — hãy mở form Dir (bấm \"Thêm\"/\"Sửa\") rồi bấm lại.");
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Title = "Lưu HTML + style của form FBO",
                Filter = "JSON (*.json)|*.json",
                FileName = $"fbo-form-{DateTime.Now:yyyyMMdd-HHmmss}.json",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            // JSON thụt lề để đọc/diff được; kèm 1 file .html chỉ chứa các FormTable để mở xem nhanh.
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var opts = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            var utf8 = new System.Text.UTF8Encoding(false);
            File.WriteAllText(dlg.FileName, System.Text.Json.JsonSerializer.Serialize(doc, opts), utf8);

            var sb = new System.Text.StringBuilder("<!doctype html><meta charset=\"utf-8\"><body>");
            foreach (var d in doc.RootElement.GetProperty("documents").EnumerateArray())
                foreach (var t in d.GetProperty("tables").EnumerateArray())
                    sb.Append(t.GetProperty("html").GetString()).Append("<hr>");
            File.WriteAllText(Path.ChangeExtension(dlg.FileName, ".html"), sb.ToString(), utf8);

            SetStatus("Đã lưu: " + dlg.FileName);
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi lưu form: " + ex.Message);
        }
    }

    private void SaveToWorkspace(string user, string pass)
    {
        // Cố ý lưu vào WebLoginUser/WebLoginPassword — KHÔNG được đụng vào ws.User/ws.Password
        // (đó là đăng nhập SQL Server dùng chung cho toàn bộ kết nối DB của Workspace này).
        if (_ws.WebLoginUser == user && _ws.WebLoginPassword == pass) return;
        _ws.WebLoginUser = user;
        _ws.WebLoginPassword = pass;
        try { _settings.Save(); } catch { /* lưu Workspace là tiện ích thêm, lỗi ghi settings không được làm gãy đăng nhập */ }
    }

    // So với bản gốc trong FsgRequirementCrawlerForm chỉ escape dấu nháy đơn, ở đây escape
    // thêm dấu backslash trước — phòng trường hợp User dạng "DOMAIN\\user" làm vỡ chuỗi JS.
    private static string JsEscape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

    private Task WaitForPageLoadAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e)
        {
            _web.NavigationCompleted -= Handler;
            tcs.TrySetResult(e.IsSuccess);
        }
        _web.NavigationCompleted += Handler;
        return tcs.Task;
    }
}