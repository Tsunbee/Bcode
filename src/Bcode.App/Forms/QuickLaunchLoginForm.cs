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
public class QuickLaunchLoginForm : Form
{
    private readonly Workspace _ws;
    private readonly AppSettings _settings;

    private readonly TextBox _txtUser = new() { Width = 130 };
    private readonly TextBox _txtPass = new() { Width = 130, UseSystemPasswordChar = true };
    private readonly CheckBox _chkRemember = new() { Text = "Lưu vào Workspace", AutoSize = true, Checked = true };
    private readonly Button _btnLogin = new() { Text = "▶ Đăng nhập", AutoSize = true, Height = 28 };

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new("Đang mở...");

    public QuickLaunchLoginForm(Workspace ws, AppSettings settings)
    {
        _ws = ws;
        _settings = settings;

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

        Controls.Add(_web);
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
            _web.CoreWebView2.Settings.IsScriptEnabled = true;

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
            await Task.Delay(1500);
            SetStatus("Đã đăng nhập.");
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