using Bcode.App.Controls;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Forms;

public class SetupEInvoiceForm : ThemedForm
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
    private readonly RichTextBox _resultBox = new() { Dock = DockStyle.Fill, Font = ThemeManager.MonoFont, ReadOnly = true };

    public SetupEInvoiceForm(DbConnectionService connections)
    {
        _connections = connections;
        Text = "eInvoice Setup";
        Width = 900;
        Height = 750;
        StartPosition = FormStartPosition.CenterParent;

        var mainLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Inputs
        mainLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56)); // Buttons (fixed: the HTML bar has no meaningful AutoSize preferred height)
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

        // 2. BUTTON PANEL — HTML/CSS (Controls/WebActionBar.cs). The two "Step" buttons are
        // the manual SQL alternative to the one-click API call, so they sit on the left as
        // secondary actions; the old row put all three side by side with a bare "or" label
        // between them, which read as three equally likely things to click.
        //
        // Step 2 chạy thẳng câu insert xuống 2 database riêng (Proxy / App — xem
        // ExecuteScriptsAsync), khác với nút "Cập nhật / Update Key" vốn không đụng SQL
        // trực tiếp mà gọi API UpdateKey của FastBusiness.
        var btnPanel = new WebActionBar { DefaultActionId = "update", Dock = DockStyle.Fill };
        btnPanel.Add("step1", "Step 1: View UpdateKey Script", WebActionKind.Normal, left: true)
                .Add("step2", "Step 2: Exec Insert in Server (SQL)", WebActionKind.Normal, left: true)
                .Add("update", "Cập nhật / Update Key", WebActionKind.Primary);
        btnPanel.Invoked += async id =>
        {
            switch (id)
            {
                case "step1": GenerateScript(); break;
                case "step2": await ExecuteScriptsAsync(); break;
                case "update": await ExecUpdateKeyAsync(); break;
            }
        };

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

    /// <summary>Script Proxy setting — xem <see cref="EInvoiceSetupService.BuildProxyScript"/> (một bản dùng chung với tab Setup eInvoice).</summary>
    private string BuildProxyScript(string prj, string unit, string client, string portalLink, string user, string prod, string proxy, string portalKey) =>
        EInvoiceSetupService.BuildProxyScript(prj, unit, client, portalLink, user, prod, proxy, portalKey);

    /// <summary>Script App setting — xem <see cref="EInvoiceSetupService.BuildAppScript"/> (một bản dùng chung với tab Setup eInvoice).</summary>
    private string BuildAppScript(string prj, string unit, string client, string portalLink, string user, string portalKey, bool isHsm) =>
        EInvoiceSetupService.BuildAppScript(prj, unit, client, portalLink, user, portalKey, isHsm);

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