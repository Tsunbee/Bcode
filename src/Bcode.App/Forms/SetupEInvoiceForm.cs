using System.Net.Http;
using System.Text.Json;
using System.Text;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Forms;

public class SetupEInvoiceForm : Form
{
    private readonly DbConnectionService _connections;

    private readonly TextBox _middleLinkBox = new() { Text = "http://dev.fast.com.vn/Fast-EInvoice-Crypto/Fast-Api.asmx/UpdateKey" };
    private readonly TextBox _dbProxyBox = new();
    private readonly TextBox _projectBox = new() { Text = "KOG" };
    private readonly TextBox _productBox = new() { Text = "FBO" };
    private readonly TextBox _unitBox = new() { Text = "05" };
    private readonly TextBox _clientCodeBox = new() { Text = "007986" };
    private readonly TextBox _proxyCodeBox = new() { Text = "006129" };
    private readonly TextBox _portalKeyBox = new() { Multiline = true, Height = 80, ScrollBars = ScrollBars.Vertical };
    private readonly TextBox _linkPortalBox = new() { Text = "https://tportal.fast.com.vn/AppService/FastEInvoice.PortalService.asmx" };
    private readonly TextBox _usernameBox = new() { Text = "hddt@namkimcorp.vn" };
    private readonly TextBox _passwordBox = new() { UseSystemPasswordChar = true };
    private readonly CheckBox _hsmCheck = new() { Text = "Ký HSM", AutoSize = true, Checked = true };
    private readonly RichTextBox _resultBox = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 10), ReadOnly = true };

    public SetupEInvoiceForm(DbConnectionService connections)
    {
        _connections = connections;
        Text = "eInvoice Setup";
        Width = 900;
        Height = 750;
        StartPosition = FormStartPosition.CenterParent;

        var mainLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Inputs
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Buttons
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // Result

        // 1. INPUT PANEL
        var inputLayout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true, Padding = new Padding(10) };
        inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        inputLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        int row = 0;
        AddRow(inputLayout, "Link trung gian / Middle Link", _middleLinkBox, 4, row++);
        AddRow(inputLayout, "CSDL / Database Proxy", _dbProxyBox, 4, row++);
        
        var hr1 = new Label { Height = 1, BackColor = AppColors.Border, Margin = new Padding(0, 10, 0, 10) };
        inputLayout.Controls.Add(hr1, 0, row); inputLayout.SetColumnSpan(hr1, 4); row++;

        AddTwoCols(inputLayout, "Mã dự án / Project", _projectBox, "Sản phẩm / Product", _productBox, row++);
        AddRow(inputLayout, "Đơn vị", _unitBox, 2, row++);
        AddRow(inputLayout, "Mã doanh nghiệp / ClientCode", _clientCodeBox, 2, row++);
        AddRow(inputLayout, "Nhóm dịch vụ / ProxyCode", _proxyCodeBox, 2, row++);
        AddRow(inputLayout, "Portal Key nhận từ mail\nPortal Public Key", _portalKeyBox, 4, row++);
        AddRow(inputLayout, "Link Portal", _linkPortalBox, 4, row++);
        AddRow(inputLayout, "Tài khoản portal / Username", _usernameBox, 2, row++);
        AddRow(inputLayout, "Mật khẩu / Password", _passwordBox, 2, row++);
        
        inputLayout.Controls.Add(_hsmCheck, 1, row++);

        // 2. BUTTON PANEL
        var btnPanel = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 0, 10, 5) };
        var btnUpdate = new Button { Text = "Cập nhật / Update Key", AutoSize = true, BackColor = AppColors.Accent, ForeColor = Color.White };
        var lblOr = new Label { Text = "or", AutoSize = true, TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(10, 5, 10, 0) };
        var btnStep1 = new Button { Text = "Step 1: View UpdateKey Script", AutoSize = true };
        var btnStep2 = new Button { Text = "Step 2: Exec Insert in Server (SQL)", AutoSize = true };

        btnStep1.Click += (_, _) => GenerateScript();
        // Step 2 chạy thẳng câu insert xuống 2 database riêng (Proxy / App — xem
        // ExecuteScriptsAsync), khác với nút "Cập nhật / Update Key" ở trên vốn không
        // đụng SQL trực tiếp mà gọi API UpdateKey của FastBusiness.
        btnStep2.Click += async (_, _) => await ExecuteScriptsAsync();
        btnUpdate.Click += async (_, _) => await ExecUpdateKeyAsync(); // Giả định nút chính cũng gọi API

        btnPanel.Controls.AddRange(new Control[] { btnUpdate, lblOr, btnStep1, btnStep2 });

        // 3. RESULT PANEL
        var resultPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
        var lblResult = new Label { Text = "Kết quả / Result", Dock = DockStyle.Top, Height = 25 };
        _resultBox.BorderStyle = BorderStyle.FixedSingle;
        _resultBox.BackColor = AppColors.Input;
        _resultBox.ForeColor = Color.FromArgb(244, 135, 113); // Màu cam đỏ như trong ảnh báo lỗi
        resultPanel.Controls.Add(_resultBox);
        resultPanel.Controls.Add(lblResult);

        mainLayout.Controls.Add(inputLayout, 0, 0);
        mainLayout.Controls.Add(btnPanel, 0, 1);
        mainLayout.Controls.Add(resultPanel, 0, 2);
        Controls.Add(mainLayout);

        ThemeManager.Apply(this);
        foreach (Control c in inputLayout.Controls)
            if (c is TextBox txt) { txt.Dock = DockStyle.Fill; txt.Margin = new Padding(3, 3, 20, 3); }
    }

    private void AddRow(TableLayoutPanel panel, string label, Control input, int colSpan, int row)
    {
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        panel.Controls.Add(input, 1, row);
        if (colSpan > 2) panel.SetColumnSpan(input, colSpan - 1);
    }

    private void AddTwoCols(TableLayoutPanel panel, string lbl1, Control inp1, string lbl2, Control inp2, int row)
    {
        panel.Controls.Add(new Label { Text = lbl1, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
        panel.Controls.Add(inp1, 1, row);
        panel.Controls.Add(new Label { Text = lbl2, AutoSize = true, Anchor = AnchorStyles.Left }, 2, row);
        panel.Controls.Add(inp2, 3, row);
    }

    private void GenerateScript()
    {
        var prj = _projectBox.Text.Trim();
        var prod = _productBox.Text.Trim();
        var unit = _unitBox.Text.Trim();
        var client = _clientCodeBox.Text.Trim();
        var proxy = _proxyCodeBox.Text.Trim();
        var portalLink = _linkPortalBox.Text.Trim();
        var user = _usernameBox.Text.Trim();
        var portalKey = _portalKeyBox.Text.Trim();
        var isHsm = _hsmCheck.Checked;

        // Nội dung script được thay thế biến động từ UI, giữ nguyên các RSA key tĩnh từ template mẫu của bạn
        var script = BuildProxyScript(prj, unit, client, portalLink, user, prod, proxy, portalKey)
            + "\n\n" + BuildAppScript(prj, unit, client, portalLink, user, portalKey, isHsm);

        _resultBox.ForeColor = AppColors.Text; // Trả về màu text bình thường cho SQL Script
        _resultBox.Text = script;
    }

    /// <summary>Reads every input field once, shared by GenerateScript (Step 1: just
    /// displays the combined text) and ExecuteScriptsAsync (Step 2: actually runs each
    /// half against its own database).</summary>
    private (string prj, string unit, string client, string proxy, string portalLink, string user, string portalKey, string prod, bool isHsm) ReadInputs() => (
        _projectBox.Text.Trim(),
        _unitBox.Text.Trim(),
        _clientCodeBox.Text.Trim(),
        _proxyCodeBox.Text.Trim(),
        _linkPortalBox.Text.Trim(),
        _usernameBox.Text.Trim(),
        _portalKeyBox.Text.Trim(),
        _productBox.Text.Trim(),
        _hsmCheck.Checked
    );

    /// <summary>"Proxy setting" — tkhddt/edmsp/ekey/edmkh. MUST run against the separate
    /// "CSDL / Database Proxy" database (_dbProxyBox), NOT the workspace's normal App/Sys
    /// Data — this database's own "tkhddt" has a different column set than the app
    /// database's "tkhddt" (see BuildAppScript below), which is exactly why running the
    /// whole combined script against one connection used to fail on one half's INSERT.
    /// </summary>
    private string BuildProxyScript(string prj, string unit, string client, string portalLink, string user, string prod, string proxy, string portalKey) =>
        $@"/*Script update to Database Proxy*/

/*Proxy setting*/
delete tkhddt
insert into tkhddt(ma_kh, ma_kn, ten_kn, ten_kn2, url_hddt1, url_hddt2, url_hddt3, user_hddt, [pass_hddt], ten_tk_hddt, [mk_tk_hddt], dich_vu_hddt, status, datetime0, datetime2)
	select N'{prj}', N'{unit}', N'{prj}', '.', N'{portalLink}', '.', '.', N'{user}', N'7266CC5F28AE082CF002DA2ACF0C7B2AF496905ED1E353ED00012F1064DBAABECE046C102D91EAC35F66EE3C0FB297922A0ACEC3DBE309B5312FECA3B28AA8C6536B858010EED6E3427541FD3712AA3F2BF15E5ACB6FCA6D5E9A34B59DE9285F30488FCD9ABC65BCBE95114053D7BD0070B76880FAB5195238CE425792033488', N'{client}', N'75B9DD5031BC28B620E44A06D8466425CCCA84A48DC4C14FC69848AEFC1C61D786726FD42A1A47D2C9650E2BD345FE5D80AB25E1F0272C53A941DEDA1AB0581449DFE40E953A211125287E1431CE4F19ECD95059669A439E4EC89F057BA723E65104E77A03E9A7808703A723ED80C783B19D01C853B909D0B5C201348C5B49B4', 8, '1', getdate(), getdate()

delete edmsp; insert into edmsp (ma_sp, ten_sp) select N'{prod}', N'{prod}'
delete ekey; insert into ekey (proxy_code, private_key, public_key, pk_portal) select N'{proxy}', N'<RSAKeyValue><Modulus>vA4WHJRhbEhh4t0/qsz3yRYJiCbH4Cg+tHIpxtFNp5G/Yl/C8XpeHg24SGg2N2zr/7WoLnz9G5FOd6Elt9Fdxor4S1EiDEWrOupyOWBx5ra0IHiFDer8p3Hx5QBH53QB/HyQGwZD/nwuWj99ITwjeC8L9iowaZgNzL0x+sC6VTE=</Modulus><Exponent>AQAB</Exponent><P>2LtQeONvtqCQFVGNvYj2UWnVLYjKYrGhI1ZCyc/x2MGFcRb/+x5j7DBOELMuNcfBe1Q58goqkDYDzffk0M2Rvw==</P><Q>3iCozm1CeFBH5A4vmKt08qui6yfIDw6yWmszn6ocGayGLlBGBXH4HFFYgUdgq+jbuXnyJa7qxDWNzdBX/o31Dw==</Q><DP>riCgoN+qK4KJAHfLd1IJBJQREEpswCqSmj993YLSfiHNQnUGKQ3bnjGZJtWu9MqO6rVa8Nm2JLMhD2RxVEk1JQ==</DP><DQ>pl6T0Ljo9jA7CEbPw2t4FmITjkmngA+j6jEs40OH9HrRrVKWf3GTQbJztbB+aYPpPoxln2/ZisgJw8NuhMxSZQ==</DQ><InverseQ>XGNuMxHF+kHAJ4FjCMVX1i+ZYFH9MXKCne9EwkKjrXUkrPPh4cwVrIwXhymEYH6UbYw6HpzKgUznyCuLYZ/Qng==</InverseQ><D>GCzTaN8mWw4/DzQUIDfzTrV3ijo6DbX+waG/fyCfFACnktTusa5idQicfSpwddWZzSikMz28KBQY+0YLHENdA5WlAmLit+seEOQe1TVXzAvjG5AeN7VRLRXCOCVI10JSK2+y9RejjSnDtOGEXJ4mD8KFuPmAv6NmMqXL+zARWQU=</D></RSAKeyValue>', N'<RSAKeyValue><Modulus>vA4WHJRhbEhh4t0/qsz3yRYJiCbH4Cg+tHIpxtFNp5G/Yl/C8XpeHg24SGg2N2zr/7WoLnz9G5FOd6Elt9Fdxor4S1EiDEWrOupyOWBx5ra0IHiFDer8p3Hx5QBH53QB/HyQGwZD/nwuWj99ITwjeC8L9iowaZgNzL0x+sC6VTE=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>', N'{portalKey}'
delete edmkh; insert into edmkh (ma_kh, ten_kh, ma_sp, client_code, public_key, password, status) select N'{prj}', N'{prj}', N'{prod}', N'{client}', N'<RSAKeyValue><Modulus>5sMYunQupB+bn2HsF5xVLe8xZLBNNBe/neWLF/75HRtaAAtIW2qR0YEzfi7FP22SHZY7j4hZHhGESNZce1km4zR1+FCAcZWNlu3cwGw2mFsvIi8hCfXX4RUPaGVSIH5/v5FL3FPLuTNG8Q4+Jmy50SQ9s3lyMxOx5wc/jgQmZVk=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>', '62e51ff343af5bc0c89a06a8640cd01c', '1'";

    /// <summary>App setting block — targets the App Database (the workspace's normal
    /// "App Data"), NOT the Proxy database. "tkhddt" here has a different column set
    /// than the Proxy database's own "tkhddt" above (no ma_kh/ten_kn2/pass_hddt/
    /// mk_tk_hddt; has user_id0/user_id2/serial_cert instead) — see ExecuteScriptsAsync.
    /// </summary>
    private string BuildAppScript(string prj, string unit, string client, string portalLink, string user, string portalKey, bool isHsm) =>
        $@"/*App setting*/
delete dmstthddt
insert into dmstthddt (ma_kh, pk, rk, pk_service, password) Select N'{prj}', N'<RSAKeyValue><Modulus>5sMYunQupB+bn2HsF5xVLe8xZLBNNBe/neWLF/75HRtaAAtIW2qR0YEzfi7FP22SHZY7j4hZHhGESNZce1km4zR1+FCAcZWNlu3cwGw2mFsvIi8hCfXX4RUPaGVSIH5/v5FL3FPLuTNG8Q4+Jmy50SQ9s3lyMxOx5wc/jgQmZVk=</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>', N'<RSAKeyValue><Modulus>5sMYunQupB+bn2HsF5xVLe8xZLBNNBe/neWLF/75HRtaAAtIW2qR0YEzfi7FP22SHZY7j4hZHhGESNZce1km4zR1+FCAcZWNlu3cwGw2mFsvIi8hCfXX4RUPaGVSIH5/v5FL3FPLuTNG8Q4+Jmy50SQ9s3lyMxOx5wc/jgQmZVk=</Modulus><Exponent>AQAB</Exponent><P>7NBacsi79GzFgwqyRpOY4ZzY2PZ/rZAbidATFtAag6+QfEqEutRoSZy1TNt1zb+CW665pbnEP7NhQ906hFGH4w==</P><Q>+XU53bHLpmmxgWngSy2h8p4oWY4KuzZCm2hziZCF4WgaBEPiNamUIEkEmr3f+oZcxzsrp1HKXUUraPJOm6eKkw==</Q><DP>Ee75WoXvDeSK1JCjzYpx4mwBU/Te2GL4YuhZ+blKuLw74d22zXs2ZpSyeh6IfktJcO37axx1SymnbP885jZSZw==</DP><DQ>7LjIa8+fsNCVqHg/ZzfreZ+KLMm090kbVfx9v2pNEcTHA4sjq8a7kROZcfqDBGriuhE1cLcV8QKFmjZuUBliTw==</DQ><InverseQ>Ye+lyhOs6eamWYkfEYcBrFEk6uaOVdcdYfy8a2A1nZO4Bl3PkIJZHBLIqlZJ2qHdqT4UQwUWp7vXkzEyDiA+2A==</InverseQ><D>WTbKA6PROGCD8N2RwhsNj2GvLec/IcmgqjHJUbCgrNEbPXMfOUB9OYsC1mDMn1YELG4dfsNO+OH6y5IcVQ/FiUuKR87+elQUokDBpyCTSWJYVbxz4JgwUffZkpVqbITmCWbcaFrI/hi4/fJxu7wom0JKHSZrVGAWDslyD2IL58U=</D></RSAKeyValue>', N'{portalKey}', '62e51ff343af5bc0c89a06a8640cd01c'

delete tkhddt; 
insert into tkhddt(ma_kn, ten_kn, url_hddt1, url_hddt2, url_hddt3, user_hddt, ten_tk_hddt, dich_vu_hddt, status, datetime0, datetime2, user_id0, user_id2, serial_cert)
	select N'{unit}', N'{prj}', N'{portalLink}', '.', '.', N'{user}', N'{client}', 8, '1', getdate(), getdate(), 1, 1, ''
	
update options set val = 1 where rtrim(name) = 'm_sd_hddt'
{(isHsm ? "update options set val = 1 where rtrim(name) = 'm_ky_hddt'" : "")}";

    /// <summary>
    /// "Step 2: Exec Insert in Server (SQL)" — actually runs the generated SQL, unlike
    /// the main "Cập nhật / Update Key" button (which never touches SQL directly, it
    /// calls FastBusiness's own UpdateKey HTTP API instead). The two script blocks MUST
    /// run against two different databases — per Bee: "khi chạy proxy setting thì phải
    /// chạy ở database Einvoice đã khai, đoạn appsetting là chạy ở database app" —
    /// because "tkhddt" (among others) exists in BOTH databases with DIFFERENT columns,
    /// so running the whole combined script against a single connection always failed on
    /// one half's INSERT with an "Invalid column name"-style error.
    /// </summary>
    private async Task ExecuteScriptsAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectBox.Text) || string.IsNullOrWhiteSpace(_clientCodeBox.Text))
        {
            _resultBox.ForeColor = Color.FromArgb(244, 135, 113);
            _resultBox.Text = "Error: please input fields";
            return;
        }

        var dbProxy = _dbProxyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(dbProxy))
        {
            _resultBox.ForeColor = Color.FromArgb(244, 135, 113);
            _resultBox.Text = "Error: chưa khai \"CSDL / Database Proxy\" — cần biết chạy phần Proxy setting vào database nào.";
            return;
        }

        var (prj, unit, client, proxy, portalLink, user, portalKey, prod, isHsm) = ReadInputs();
        var proxyScript = BuildProxyScript(prj, unit, client, portalLink, user, prod, proxy, portalKey);
        var appScript = BuildAppScript(prj, unit, client, portalLink, user, portalKey, isHsm);

        var log = new StringBuilder();
        var ok = true;

        try
        {
            log.AppendLine($"Đang chạy Proxy setting vào database \"{dbProxy}\"...");
            _resultBox.ForeColor = AppColors.TextMuted;
            _resultBox.Text = log.ToString();

            await using var proxyConn = _connections.CreateConnectionToDatabase(dbProxy);
            await proxyConn.OpenAsync();
            await using var proxyCmd = new SqlCommand(proxyScript, proxyConn) { CommandTimeout = 60 };
            await proxyCmd.ExecuteNonQueryAsync();
            log.AppendLine($"✓ Proxy setting: OK (database \"{dbProxy}\").");
        }
        catch (Exception ex)
        {
            ok = false;
            log.AppendLine($"✗ Proxy setting LỖI (database \"{dbProxy}\"): {ex.Message}");
        }

        try
        {
            log.AppendLine();
            log.AppendLine("Đang chạy App setting vào App Data (database app hiện tại)...");
            _resultBox.Text = log.ToString();

            await using var appConn = _connections.CreateConnection(useSysDatabase: false);
            await appConn.OpenAsync();
            await using var appCmd = new SqlCommand(appScript, appConn) { CommandTimeout = 60 };
            await appCmd.ExecuteNonQueryAsync();
            log.AppendLine("✓ App setting: OK.");
        }
        catch (Exception ex)
        {
            ok = false;
            log.AppendLine($"✗ App setting LỖI: {ex.Message}");
        }

        _resultBox.ForeColor = ok ? AppColors.Text : Color.FromArgb(244, 135, 113);
        _resultBox.Text = log.ToString();
    }

    private async Task ExecUpdateKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(_projectBox.Text) || string.IsNullOrWhiteSpace(_clientCodeBox.Text))
        {
            _resultBox.ForeColor = Color.FromArgb(244, 135, 113);
            _resultBox.Text = "Error: please input fields";
            return;
        }

        _resultBox.ForeColor = AppColors.TextMuted;
        _resultBox.Text = "Đang lấy pass_hddt từ Portal...";

        try
        {
            string portalUrl = _linkPortalBox.Text.Trim();
            string proxyCode = _proxyCodeBox.Text.Trim();
            string clientCode = _clientCodeBox.Text.Trim();
            string username = _usernameBox.Text.Trim();

            // 1. Lấy pass_hddt động từ Portal Service
            string? passHddt = await GetPassHddtFromPortalAsync("https://tportal.fast.com.vn/AppService/FastEInvoice.PortalService.asmx", proxyCode, clientCode, username);

            if (string.IsNullOrEmpty(passHddt))
            {
                _resultBox.ForeColor = Color.FromArgb(244, 135, 113);
                _resultBox.Text = "Error: Không thể lấy được pass_hddt từ Link Portal (Kiểm tra lại tài khoản/mật khẩu portal).";
                return;
            }

            _resultBox.Text = "Đang gọi API UpdateKey tới Server...";

            // 2. Chuẩn bị payload đầy đủ tham số
            using var client = new HttpClient();
            var payload = new
            {
                ProjectCode = _projectBox.Text.Trim(),
                ClientCode = clientCode,
                ProxyCode = proxyCode,
                UnitCode = _unitBox.Text.Trim(),
                LinkPotal = portalUrl,
                LinkServiceMTT = string.Empty,
                UserName = username,
                Pass = _passwordBox.Text.Trim(),
                PortalPublicKey = _portalKeyBox.Text.Trim(),
                ProxyPublicKey = string.Empty,
                ProxyPrivateKey = string.Empty,
                pass_hddt = passHddt,
                isProxyFast = 1
            };

            var jsonContent = JsonSerializer.Serialize(payload);
            var httpContent = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            // Đảm bảo request gửi đi đúng chuẩn định dạng JSON
            httpContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

            var response = await client.PostAsync(_middleLinkBox.Text.Trim(), httpContent);
            var responseStr = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                _resultBox.ForeColor = AppColors.Text;
                _resultBox.Text = "Thành công!\n" + responseStr;
            }
            else
            {
                _resultBox.ForeColor = Color.FromArgb(244, 135, 113);
                _resultBox.Text = $"HTTP Error {(int)response.StatusCode}: {responseStr}";
            }
        }
        catch (Exception ex)
        {
            _resultBox.ForeColor = Color.FromArgb(244, 135, 113);
            _resultBox.Text = $"Exception: {ex.Message}";
        }
    }
    private async Task<string?> GetPassHddtFromPortalAsync(string portalUrl, string proxyCode, string clientCode, string username)
    {
        try
        {
            using var client = new HttpClient();
            
            string soapXml = $@"<?xml version=""1.0"" encoding=""utf-8""?>
                <soap:Envelope xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"" xmlns:xsd=""http://www.w3.org/2001/XMLSchema"" xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"">
                <soap:Body>
                    <CheckKey xmlns=""http://tempuri.org/"">
                    <proxyCode>{proxyCode}</proxyCode>
                    <clientCode>{clientCode}</clientCode>
                    <user>{username}</user>
                    <data></data>
                    </CheckKey>
                </soap:Body>
                </soap:Envelope>";

            var content = new StringContent(soapXml, Encoding.UTF8, "text/xml");
            
            // Thêm SOAPAction header đặc trưng của ASMX service
            client.DefaultRequestHeaders.Add("SOAPAction", "http://tempuri.org/CheckKey");

            var response = await client.PostAsync(portalUrl, content);
            if (!response.IsSuccessStatusCode) return null;

            var xmlResponse = await response.Content.ReadAsStringAsync();

            // Parse kết quả thẻ <CheckKeyResult> trả về từ SOAP Response
            var xDoc = System.Xml.Linq.XDocument.Parse(xmlResponse);
            System.Xml.Linq.XName checkKeyResultName = "{http://tempuri.org/}CheckKeyResult";
            
            var resultNode = xDoc.Descendants(checkKeyResultName).FirstOrDefault();
            return resultNode?.Value;
        }
        catch
        {
            return null;
        }
    }
}