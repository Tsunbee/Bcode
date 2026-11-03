using Bcode.App.Controls;
using System.Text;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

public class SetupEInvoiceForm : ThemedForm
{
    private readonly DbConnectionService _connections;

    private readonly TextBox _middleLinkBox = new() { Text = EInvoiceSetupService.DefaultMiddleLink };
    private readonly TextBox _dbProxyBox = new();
    private readonly TextBox _projectBox = new() { Text = "KOG" };
    private readonly TextBox _productBox = new() { Text = "FBO" };
    private readonly TextBox _unitBox = new() { Text = "05" };
    private readonly TextBox _clientCodeBox = new() { Text = "007986" };
    private readonly TextBox _proxyCodeBox = new() { Text = EInvoiceSetupService.FastProxyCode, Enabled = false };
    private readonly CheckBox _ownProxyCheck = new() { Text = "Nhóm dịch vụ riêng", AutoSize = true };
    private readonly TextBox _portalKeyBox = new() { Multiline = true, Height = 80, ScrollBars = ScrollBars.Vertical, Text = EInvoiceSetupService.FastPortalPublicKey };
    private readonly TextBox _linkPortalBox = new() { Text = EInvoiceSetupService.DefaultPortalLink };
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
        AddRow(inputLayout, "Đơn vị (nhiều mã cách dấu phẩy)", _unitBox, 2, row++);
        AddRow(inputLayout, "Mã doanh nghiệp / ClientCode", _clientCodeBox, 2, row++);
        AddRow(inputLayout, "Nhóm dịch vụ / ProxyCode", _proxyCodeBox, 2, row);
        inputLayout.Controls.Add(_ownProxyCheck, 2, row++);
        _ownProxyCheck.CheckedChanged += (_, _) =>   // như tool VB: không tick = nhóm dịch vụ chung của Fast
        {
            _proxyCodeBox.Enabled = _ownProxyCheck.Checked;
            _proxyCodeBox.Text = _ownProxyCheck.Checked ? "" : EInvoiceSetupService.FastProxyCode;
        };
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
        // Step 2 chạy thẳng câu insert xuống App Data (+ database Proxy khi nhóm dịch vụ
        // riêng — xem ExecuteScriptsAsync), khác với nút "Cập nhật / Update Key" vốn không
        // đụng SQL trực tiếp mà gọi API UpdateProxy của Fast.
        var btnPanel = new WebActionBar { DefaultActionId = "update", Dock = DockStyle.Fill };
        btnPanel.Add("step1", "Step 1: View UpdateKey Script", WebActionKind.Normal, left: true)
                .Add("step2", "Step 2: Exec Insert in Server (SQL)", WebActionKind.Normal, left: true)
                .Add("update", "Cập nhật / Update Key", WebActionKind.Primary);
        btnPanel.Invoked += async id =>
        {
            switch (id)
            {
                case "step1": await GenerateScriptAsync(); break;
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

    /// <summary>Đọc các ô nhập thành <see cref="EInvoiceInputs"/> — form cũ dùng chung logic với tab Setup eInvoice (<see cref="EInvoiceSetupService"/>).</summary>
    private EInvoiceInputs ReadInputs() => new(_projectBox.Text, _productBox.Text, _unitBox.Text, _clientCodeBox.Text, _proxyCodeBox.Text, _portalKeyBox.Text,
        _linkPortalBox.Text, _usernameBox.Text, _passwordBox.Text, _middleLinkBox.Text, _dbProxyBox.Text, _hsmCheck.Checked, _ownProxyCheck.Checked);

    private void ShowError(string text) { _resultBox.ForeColor = Color.FromArgb(244, 135, 113); _resultBox.Text = text; }

    private async Task GenerateScriptAsync()
    {
        var i = ReadInputs();
        if (EInvoiceSetupService.ValidateRequired(i) is { } missing) { ShowError(missing); return; }
        try
        {
            var script = await new EInvoiceSetupService(_connections).BuildScriptAsync(i);
            _resultBox.ForeColor = AppColors.Text; // Trả về màu text bình thường cho SQL Script
            _resultBox.Text = script;
        }
        catch (Exception ex) { ShowError("Error: " + ex.Message); }
    }

    /// <summary>"Step 2: Exec Insert in Server (SQL)" — xem <see cref="EInvoiceSetupService.ExecuteScriptsAsync"/> (App setting vào App Data, Proxy setting vào
    /// database Proxy khi nhóm dịch vụ riêng).</summary>
    private async Task ExecuteScriptsAsync()
    {
        var log = new StringBuilder();
        _resultBox.ForeColor = AppColors.TextMuted;
        _resultBox.Text = "";
        var ok = await new EInvoiceSetupService(_connections).ExecuteScriptsAsync(ReadInputs(), (line, _) => { log.AppendLine(line); _resultBox.Text = log.ToString(); });
        _resultBox.ForeColor = ok ? AppColors.Text : Color.FromArgb(244, 135, 113);
    }

    /// <summary>"Cập nhật / Update Key" — xem <see cref="EInvoiceSetupService.UpdateKeyAndRunAsync"/> (gọi API UpdateProxy rồi chạy script vào database).</summary>
    private async Task ExecUpdateKeyAsync()
    {
        var log = new StringBuilder();
        _resultBox.ForeColor = AppColors.TextMuted;
        _resultBox.Text = "";
        var ok = await new EInvoiceSetupService(_connections).UpdateKeyAndRunAsync(ReadInputs(), (line, _) => { log.AppendLine(line); _resultBox.Text = log.ToString(); });
        _resultBox.ForeColor = ok ? AppColors.Text : Color.FromArgb(244, 135, 113);
    }
}
