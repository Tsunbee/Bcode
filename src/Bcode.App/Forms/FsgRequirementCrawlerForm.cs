using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

public class FsgRequirementCrawlerForm : Form
{
    // 1. THANH NHẬP LIỆU CẤU HÌNH TRÊN ĐỈNH
    private readonly TextBox _txtUser = new() { Width = 95 };
    private readonly TextBox _txtPass = new() { Width = 95, UseSystemPasswordChar = true };
    private readonly TextBox _txtBpLt = new() { Width = 55, Text = "" }; 
    private readonly TextBox _txtMaDa = new() { Width = 90, Text = "" }; 
    private readonly TextBox _txtMaYc = new() { Width = 80, PlaceholderText = "Mã YC..." };
    private readonly CheckBox _chkRemember = new() { Text = "Nhớ thông tin", AutoSize = true, Checked = true };

    // CÁC NÚT ĐIỀU KHIỂN
    private readonly Button _btnAutoLogin = new() { Text = "▶ Đăng nhập & Mở trang", AutoSize = true, Height = 28 };
    private readonly Button _btnFilterProject = new() { Text = "🏢 Lọc lại Dự án", AutoSize = true, Height = 28 };
    private readonly Button _btnApplyFilter = new() { Text = "🔍 Lọc Mã YC", AutoSize = true, Height = 28 };

    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new("Sẵn sàng");

    // 2. KHUNG TRÌNH DUYỆT TRÀN MÀN HÌNH
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    private const string LoginUrl = "http://172.168.5.14:81/FSG/Main/Login.aspx";
    private const string TargetUrl = "http://172.168.5.14:81/FSG/Main/nbphyc.aspx?id=05.10.01";
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "fsg_crawler_config.json");

    public FsgRequirementCrawlerForm()
    {
        Text = "FSG - Cập nhật & Tra cứu yêu cầu";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized; // Phóng to toàn màn hình

        // Thanh công cụ đỉnh Form
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

        AddLabel("User:");
        topBar.Controls.Add(_txtUser);
        AddLabel("Pass:");
        topBar.Controls.Add(_txtPass);
        AddLabel("Bộ phận:");
        topBar.Controls.Add(_txtBpLt);
        AddLabel("Dự án:");
        topBar.Controls.Add(_txtMaDa);
        AddLabel("Mã YC:");
        topBar.Controls.Add(_txtMaYc);
        topBar.Controls.Add(_chkRemember);
        topBar.Controls.Add(_btnAutoLogin);
        topBar.Controls.Add(_btnFilterProject);
        topBar.Controls.Add(_btnApplyFilter);

        _statusStrip.Items.Add(_statusLabel);

        Controls.Add(_web);
        Controls.Add(topBar);
        Controls.Add(_statusStrip);

        LoadSavedConfig();

        // Gõ Mã Dự án nhấn Enter sẽ tự kích hoạt lọc Dự án
        _txtMaDa.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await FilterProjectOnWebAsync();
            }
        };

        // Gõ Mã YC nhấn Enter sẽ tự kích hoạt lọc Mã YC
        _txtMaYc.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await ApplyFilterToPageAsync();
            }
        };

        _btnAutoLogin.Click += async (_, _) => await StartAutoLoginAsync();
        _btnFilterProject.Click += async (_, _) => await FilterProjectOnWebAsync();
        _btnApplyFilter.Click += async (_, _) => await ApplyFilterToPageAsync();

        Load += async (_, _) =>
        {
            await _web.EnsureCoreWebView2Async();
            _web.CoreWebView2.Settings.IsScriptEnabled = true;
            _web.CoreWebView2.Navigate(LoginUrl);
        };
    }

    private void SetButtonsEnabled(bool enabled)
    {
        _btnAutoLogin.Enabled = enabled;
        _btnFilterProject.Enabled = enabled;
        _btnApplyFilter.Enabled = enabled;
    }

    private void SetStatus(string text) => _statusLabel.Text = text;

    // =========================================================================
    // 1. TỰ ĐỘNG ĐĂNG NHẬP & MỞ TRANG YÊU CẦU
    // =========================================================================
    private async Task StartAutoLoginAsync()
    {
        var user = _txtUser.Text.Trim();
        var pass = _txtPass.Text.Trim();
        var bpLt = _txtBpLt.Text.Trim();
        var maDa = _txtMaDa.Text.Trim();
        var maYc = _txtMaYc.Text.Trim();

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            MessageBox.Show(this, "Vui lòng nhập User và Password!", "FSG", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtUser.Focus();
            return;
        }

        if (_chkRemember.Checked) SaveConfig(user, pass, bpLt, maDa, maYc);

        SetButtonsEnabled(false);

        try
        {
            SetStatus("Đang điều hướng đến trang Đăng nhập...");
            _web.CoreWebView2.Navigate(LoginUrl);
            await WaitForPageLoadAsync();
            await Task.Delay(600);

            SetStatus("Đang nhập User và kích hoạt nạp Đơn vị...");
            var triggerUnitScript = $@"
            (function() {{
                var txtUser = document.getElementById('LoginExtender_txtUserName');
                if (!txtUser) return 'NO_USER';
                txtUser.focus();
                txtUser.value = '{user.Replace("'", "\\'")}';
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

            await _web.ExecuteScriptAsync(triggerUnitScript);

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
                SetStatus("Không tự nạp được Đơn vị. Bạn có thể tự chọn Đơn vị và đăng nhập tay.");
                MessageBox.Show(this, "Không nạp được đơn vị tự động.\nBạn hãy chọn đơn vị và đăng nhập tay trên web, sau đó dùng ứng dụng bình thường.", "FSG", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
                    txtPass.value = '{pass.Replace("'", "\\'")}';
                    txtPass.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    txtPass.dispatchEvent(new Event('change', {{ bubbles: true }}));
                }}

                var btnOk = document.getElementById('LoginExtender_Ok');
                if (btnOk) btnOk.click();
            }})();";

            await _web.ExecuteScriptAsync(submitLoginScript);
            await Task.Delay(2500);

            SetStatus("Đang mở trang nbphyc.aspx (Cập nhật yêu cầu)...");
            _web.CoreWebView2.Navigate(TargetUrl);
            await WaitForPageLoadAsync();

            await CheckAndSubmitFilterDialogAsync(bpLt, maDa, maYc);
            SetStatus("Đã vào trang yêu cầu thành công!");
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi: " + ex.Message);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    // =========================================================================
    // 2. LỌC LẠI DỰ ÁN TRÊN WEB (ĐÃ SỬA THỨ TỰ GÁN VÀ KIỂM TRA HIỂN THỊ)
    // =========================================================================
    private async Task FilterProjectOnWebAsync()
    {
        var maDa = _txtMaDa.Text.Trim();
        var bpLt = _txtBpLt.Text.Trim();

        SetButtonsEnabled(false);

        try
        {
            SetStatus($"Đang mở hộp thoại lọc và gán Dự án '{maDa}'...");

            // Bước A: Kiểm tra xem Popup có THỰC SỰ ĐANG HIỂN THỊ không (kiểm tra offsetParent / offsetHeight)
            var checkAndOpenScript = @"
            (function() {
                var p = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_updateDlgPanel');
                var isVisible = p && p.style.display !== 'none' && (p.offsetWidth > 0 || p.offsetHeight > 0);
                if (isVisible) return 'ALREADY_VISIBLE';

                // Nếu chưa mở -> Bấm nút 'Tìm' trên toolbar
                var searchBtn = document.getElementById('ctl00_FastBusiness_MainReport_ToolbarButton_Search');
                if (searchBtn) {
                    searchBtn.click();
                    return 'CLICKED_SEARCH_BTN';
                }

                var rpt = window.$find ? window.$find('ctl00_FastBusiness_MainReport') : null;
                if (rpt && typeof rpt.executeCommand === 'function') {
                    rpt.executeCommand({ commandName: 'Search', commandArgument: '0' });
                    return 'COMMAND_SEARCH';
                }

                return 'CANNOT_FIND_SEARCH';
            })();";

            await _web.ExecuteScriptAsync(checkAndOpenScript);

            // Bước B: Chờ popup thực sự xuất hiện trên màn hình
            bool dialogReady = false;
            for (int i = 0; i < 15; i++)
            {
                await Task.Delay(300);
                var check = await _web.ExecuteScriptAsync(@"
                (function() {
                    var p = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_updateDlgPanel');
                    return p && p.style.display !== 'none' && (p.offsetWidth > 0 || p.offsetHeight > 0);
                })();");

                if (check == "true")
                {
                    dialogReady = true;
                    break;
                }
            }

            if (dialogReady)
            {
                // BƯỚC C: GÁN BỘ PHẬN TRƯỚC -> GÁN DỰ ÁN SAU CÙNG (ĐỂ KHÔNG BỊ CLEAR)
                var fillDialogScript = $@"
                (function() {{
                    // 1. Gán Bộ phận trước (nếu có sự kiện onchange tự xóa ma_da thì nó sẽ chạy trước ở đây)
                    var txtBp = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_bp_lt');
                    if (txtBp) {{
                        txtBp.value = '{bpLt.Replace("'", "\\'")}';
                        if (txtBp.parentForm && typeof txtBp.parentForm.setItemValue === 'function') {{
                            try {{ txtBp.parentForm.setItemValue('bp_lt', '{bpLt.Replace("'", "\\'")}'); }} catch(e) {{}}
                        }}
                        txtBp.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}

                    // 2. Gán Dự án sau cùng để đè lại giá trị chính xác
                    var txtDa = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_ma_da');
                    if (txtDa) {{
                        txtDa.value = '{maDa.Replace("'", "\\'")}';
                        if (txtDa.parentForm && typeof txtDa.parentForm.setItemValue === 'function') {{
                            try {{ txtDa.parentForm.setItemValue('ma_da', '{maDa.Replace("'", "\\'")}'); }} catch(e) {{}}
                        }}
                        txtDa.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        txtDa.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}

                    // 3. Bấm nút Nhận
                    setTimeout(function() {{
                        var btnOk = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_updateDlgOk');
                        if (btnOk) btnOk.click();
                    }}, 250);

                    return 'POPUP_UPDATED';
                }})();";

                await _web.ExecuteScriptAsync(fillDialogScript);
                SetStatus($"Đã lọc lại theo Dự án '{maDa}'.");
            }
            else
            {
                // Dự phòng: Lọc trực tiếp trên thanh lọc cột của Grid
                var gridFilterScript = $@"
                (function() {{
                    var txtGridDa = document.getElementById('ctl00_FastBusiness_MainReport_FilterPanelTextma_da');
                    if (txtGridDa) {{
                        txtGridDa.focus();
                        txtGridDa.value = '{maDa.Replace("'", "\\'")}';
                        txtGridDa.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        txtGridDa.dispatchEvent(new Event('change', {{ bubbles: true }}));

                        var rpt = window.$find ? window.$find('ctl00_FastBusiness_MainReport') : null;
                        if (rpt && typeof rpt.R0 === 'function') {{
                            rpt.R0(txtGridDa, {{ keyCode: 13, which: 13, charCode: 13, preventDefault: function(){{}}, stopPropagation: function(){{}} }}, 1);
                            return 'GRID_R0_TRIGGERED';
                        }}
                    }}
                    return 'NO_GRID_INPUT';
                }})();";

                var res = await _web.ExecuteScriptAsync(gridFilterScript);
                SetStatus($"Đã lọc Dự án trên cột lưới ({res}).");
            }
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi lọc dự án: " + ex.Message);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    // =========================================================================
    // 3. ĐIỀN ĐIỀU KIỆN LỌC MÃ YÊU CẦU
    // =========================================================================
    private async Task ApplyFilterToPageAsync()
    {
        var bpLt = _txtBpLt.Text.Trim();
        var maDa = _txtMaDa.Text.Trim();
        var maYc = _txtMaYc.Text.Trim();

        SetButtonsEnabled(false);

        try
        {
            SetStatus("Đang áp dụng điều kiện lọc...");

            var filterScript = $@"
            (function() {{
                var p = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_updateDlgPanel');
                var isVisible = p && p.style.display !== 'none' && (p.offsetWidth > 0 || p.offsetHeight > 0);
                var btnDialogOk = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_updateDlgOk');

                // 1. Nếu Popup đang mở
                if (isVisible && btnDialogOk) {{
                    var txtBp = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_bp_lt');
                    if (txtBp) {{
                        txtBp.value = '{bpLt.Replace("'", "\\'")}';
                        if (txtBp.parentForm && typeof txtBp.parentForm.setItemValue === 'function') {{
                            try {{ txtBp.parentForm.setItemValue('bp_lt', '{bpLt.Replace("'", "\\'")}'); }} catch(e) {{}}
                        }}
                        txtBp.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}

                    var txtDa = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_ma_da');
                    if (txtDa) {{
                        txtDa.value = '{maDa.Replace("'", "\\'")}';
                        if (txtDa.parentForm && typeof txtDa.parentForm.setItemValue === 'function') {{
                            try {{ txtDa.parentForm.setItemValue('ma_da', '{maDa.Replace("'", "\\'")}'); }} catch(e) {{}}
                        }}
                        txtDa.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}

                    var txtDialog = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_fcode1');
                    if (txtDialog) {{
                        txtDialog.value = '{maYc.Replace("'", "\\'")}';
                        if (txtDialog.parentForm && typeof txtDialog.parentForm.setItemValue === 'function') {{
                            try {{ txtDialog.parentForm.setItemValue('fcode1', '{maYc.Replace("'", "\\'")}'); }} catch(e) {{}}
                        }}
                        txtDialog.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}

                    btnDialogOk.click();
                    return 'POPUP_SUBMITTED';
                }}

                // 2. Nếu đang ở màn hình lưới -> Điền vào ô FilterPanelText của cột Mã (fcode1)
                var txtGridFilter = document.getElementById('ctl00_FastBusiness_MainReport_FilterPanelTextfcode1');
                if (txtGridFilter) {{
                    txtGridFilter.focus();
                    txtGridFilter.value = '{maYc.Replace("'", "\\'")}';
                    txtGridFilter.dispatchEvent(new Event('input', {{ bubbles: true }}));
                    txtGridFilter.dispatchEvent(new Event('change', {{ bubbles: true }}));

                    var rpt = window.$find ? window.$find('ctl00_FastBusiness_MainReport') : null;
                    if (rpt && typeof rpt.R0 === 'function') {{
                        rpt.R0(txtGridFilter, {{ keyCode: 13, which: 13, charCode: 13, preventDefault: function(){{}}, stopPropagation: function(){{}} }}, 8);
                        return 'GRID_FILTER_R0';
                    }} else if (typeof txtGridFilter.onkeypress === 'function') {{
                        txtGridFilter.onkeypress({{ keyCode: 13, which: 13, charCode: 13, preventDefault: function(){{}}, stopPropagation: function(){{}} }});
                        return 'GRID_FILTER_KEYPRESS';
                    }}
                }}

                return 'NO_FILTER_FOUND';
            }})();";

            var result = await _web.ExecuteScriptAsync(filterScript);
            SetStatus($"Đã thực hiện lọc ({result}).");
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi lọc: " + ex.Message);
        }
        finally
        {
            SetButtonsEnabled(true);
        }
    }

    private async Task CheckAndSubmitFilterDialogAsync(string bpLt, string maDa, string maYc)
    {
        for (int i = 0; i < 15; i++)
        {
            await Task.Delay(400);
            var hasDialog = await _web.ExecuteScriptAsync(@"
            (function() {
                var p = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_updateDlgPanel');
                return p && p.style.display !== 'none' && (p.offsetWidth > 0 || p.offsetHeight > 0);
            })();");

            if (hasDialog == "true")
            {
                SetStatus("Đang điền Bộ phận, Dự án, Mã YC vào điều kiện lọc...");

                // GÁN BỘ PHẬN TRƯỚC -> DỰ ÁN SAU
                var applyFilterScript = $@"
                (function() {{
                    var txtBp = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_bp_lt');
                    if (txtBp) {{
                        txtBp.value = '{bpLt.Replace("'", "\\'")}';
                        if (txtBp.parentForm && typeof txtBp.parentForm.setItemValue === 'function') {{
                            try {{ txtBp.parentForm.setItemValue('bp_lt', '{bpLt.Replace("'", "\\'")}'); }} catch(e) {{}}
                        }}
                        txtBp.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}

                    var txtDa = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_ma_da');
                    if (txtDa) {{
                        txtDa.value = '{maDa.Replace("'", "\\'")}';
                        if (txtDa.parentForm && typeof txtDa.parentForm.setItemValue === 'function') {{
                            try {{ txtDa.parentForm.setItemValue('ma_da', '{maDa.Replace("'", "\\'")}'); }} catch(e) {{}}
                        }}
                        txtDa.dispatchEvent(new Event('input', {{ bubbles: true }}));
                        txtDa.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}

                    var txtYc = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_form_fcode1');
                    if (txtYc) {{
                        txtYc.value = '{maYc.Replace("'", "\\'")}';
                        if (txtYc.parentForm && typeof txtYc.parentForm.setItemValue === 'function') {{
                            try {{ txtYc.parentForm.setItemValue('fcode1', '{maYc.Replace("'", "\\'")}'); }} catch(e) {{}}
                        }}
                        txtYc.dispatchEvent(new Event('change', {{ bubbles: true }}));
                    }}

                    setTimeout(function() {{
                        var btnOk = document.getElementById('ctl00_FastBusiness_MainReport_searchExtender_updateDlgOk');
                        if (btnOk) btnOk.click();
                    }}, 250);
                }})();";

                await _web.ExecuteScriptAsync(applyFilterScript);
                break;
            }
        }

        // Tự động nâng số dòng hiển thị lên 250 dòng
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500);
            await _web.ExecuteScriptAsync(@"
            (function() {
                var rpt = window.$find ? window.$find('ctl00_FastBusiness_MainReport') : null;
                if (rpt && typeof rpt.set_gridPageSize === 'function') {
                    rpt.set_gridPageSize(250);
                }
            })();");
        });
    }

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

    private void SaveConfig(string u, string p, string bp, string da, string yc)
    {
        try
        {
            var cfg = new { User = u, Pass = p, BpLt = bp, MaDa = da, MaYc = yc };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg));
        }
        catch { }
    }

    private void LoadSavedConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("User", out var u)) _txtUser.Text = u.GetString();
                if (root.TryGetProperty("Pass", out var p)) _txtPass.Text = p.GetString();
                if (root.TryGetProperty("BpLt", out var bp)) _txtBpLt.Text = bp.GetString();
                if (root.TryGetProperty("MaDa", out var da)) _txtMaDa.Text = da.GetString();
                if (root.TryGetProperty("MaYc", out var yc)) _txtMaYc.Text = yc.GetString();
            }
        }
        catch { }
    }
}