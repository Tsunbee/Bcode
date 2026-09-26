using System.Text;
using System.Text.Json;
using System.Net.Http;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.UI;

namespace Bcode.App.Forms;

public class ApiDeclarationForm : ThemedForm
{
    private readonly TextBox _txtProject = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtDept = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _cboSyncType = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _txtApiUrl = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtTokenUrl = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtToken = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox _txtSchemaPath = new() { Dock = DockStyle.Fill };
    private readonly TextBox _txtTokenBody = new()
    {
        Multiline = true,
        Height = 150,
        Dock = DockStyle.Fill,
        ScrollBars = ScrollBars.Both,
        Font = ThemeManager.MonoFont
    };

    public ApiDeclarationForm()
    {
        Text = "Khai báo & Quản lý API";
        Width = 860;
        Height = 680;
        MinimumSize = new Size(720, 560);
        StartPosition = FormStartPosition.CenterParent;

        _cboSyncType.Items.AddRange(new object[] { "Sync Danh mục", "Sync Chứng từ" });
        _cboSyncType.SelectedIndex = 0;
        _txtTokenBody.Text = "{\r\n  \"username\": \"admin\",\r\n  \"password\": \"123\"\r\n}";

        // Layout tổng thể
        var mainLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(16, 12, 16, 12),
            AutoScroll = true
        };
        // Cột nhãn cố định 160px, cột nhập liệu chiếm 100% phần còn lại
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddFormRow(mainLayout, "Mã dự án:", _txtProject);
        AddFormRow(mainLayout, "Phòng xử lý:", _txtDept);
        AddFormRow(mainLayout, "Loại đồng bộ:", _cboSyncType);
        AddFormRow(mainLayout, "Link API Chính:", _txtApiUrl);
        AddFormRow(mainLayout, "Link lấy Token:", _txtTokenUrl);
        AddFormRow(mainLayout, "Payload lấy Token (JSON):", _txtTokenBody);

        // Khối Token + Nút Get Token
        var pnlToken = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Height = 32,
            Margin = new Padding(0)
        };
        pnlToken.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pnlToken.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        var btnGetToken = new PillButton { Text = "Get Token", CornerRadius = 6, Dock = DockStyle.Fill, Margin = new Padding(6, 0, 0, 0) };
        btnGetToken.Tag = "primary";
        btnGetToken.Click += async (_, _) => await FetchTokenAsync();

        pnlToken.Controls.Add(_txtToken, 0, 0);
        pnlToken.Controls.Add(btnGetToken, 1, 0);
        AddFormRow(mainLayout, "Access Token:", pnlToken);

        // Khối File đính kèm + Nút Chọn file
        var pnlPath = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Height = 32,
            Margin = new Padding(0)
        };
        pnlPath.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pnlPath.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        var btnBrowse = new PillButton { Text = "Chọn...", CornerRadius = 6, Dock = DockStyle.Fill, Margin = new Padding(6, 0, 0, 0) };
        btnBrowse.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog { Filter = "Schema Files (*.json;*.xml;*.docx)|*.json;*.xml;*.docx|All files (*.*)|*.*" };
            if (ofd.ShowDialog(this) == DialogResult.OK) _txtSchemaPath.Text = ofd.FileName;
        };

        pnlPath.Controls.Add(_txtSchemaPath, 0, 0);
        pnlPath.Controls.Add(btnBrowse, 1, 0);
        AddFormRow(mainLayout, "File cấu trúc đính kèm:", pnlPath);

        // Thanh hành động dưới cùng
        var actions = new WebActionBar { Dock = DockStyle.Bottom };
        actions.Add("export_postman", "Xuất file Import Postman", WebActionKind.Primary, left: true)
               .Add("close", "Đóng", WebActionKind.Quiet);

        actions.Invoked += id =>
        {
            if (id == "export_postman") ExportPostmanCollection();
            else Close();
        };

        Controls.Add(mainLayout);
        Controls.Add(actions);
    }

    private static void AddFormRow(TableLayoutPanel layout, string labelText, Control inputControl)
    {
        var row = layout.RowCount++;
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var lbl = new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Top,
            Margin = new Padding(0, 8, 8, 8)
        };

        inputControl.Margin = new Padding(0, 4, 0, 8);
        layout.Controls.Add(lbl, 0, row);
        layout.Controls.Add(inputControl, 1, row);
    }

    private async Task FetchTokenAsync()
    {
        var url = _txtTokenUrl.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "Vui lòng nhập Link lấy Token.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            var content = new StringContent(_txtTokenBody.Text.Trim(), Encoding.UTF8, "application/json");
            var res = await client.PostAsync(url, content);
            var resString = await res.Content.ReadAsStringAsync();

            if (!res.IsSuccessStatusCode)
            {
                MessageBox.Show(this, $"Lỗi lấy token ({res.StatusCode}):\n{resString}", "API Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using var doc = JsonDocument.Parse(resString);
            var root = doc.RootElement;
            string? token = null;

            if (root.TryGetProperty("token", out var tProp) ||
                root.TryGetProperty("access_token", out tProp) ||
                root.TryGetProperty("data", out tProp))
            {
                token = tProp.GetString() ?? tProp.ToString();
            }

            _txtToken.Text = token ?? resString;
            MessageBox.Show(this, "Lấy Token thành công!", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Lỗi kết nối: " + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportPostmanCollection()
    {
        using var sfd = new SaveFileDialog
        {
            Filter = "Postman Collection (*.postman_collection.json)|*.postman_collection.json",
            FileName = $"{_txtProject.Text.Trim()}_{_txtDept.Text.Trim()}_API.postman_collection.json"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        var collection = new
        {
            info = new
            {
                name = $"{_txtProject.Text.Trim()} - {_txtDept.Text.Trim()} API ({_cboSyncType.SelectedItem})",
                schema = "https://schema.getpostman.com/json/collection/v2.1.0/collection.json"
            },
            item = new object[]
            {
                new
                {
                    name = "01. Get Token",
                    request = new
                    {
                        method = "POST",
                        header = new[] { new { key = "Content-Type", value = "application/json" } },
                        body = new { mode = "raw", raw = _txtTokenBody.Text.Trim() },
                        url = new { raw = _txtTokenUrl.Text.Trim(), host = new[] { _txtTokenUrl.Text.Trim() } }
                    }
                },
                new
                {
                    name = $"02. {_cboSyncType.SelectedItem}",
                    request = new
                    {
                        method = "POST",
                        header = new[]
                        {
                            new { key = "Content-Type", value = "application/json" },
                            new { key = "Authorization", value = "Bearer " + _txtToken.Text.Trim() }
                        },
                        body = new { mode = "raw", raw = "{}" },
                        url = new { raw = _txtApiUrl.Text.Trim(), host = new[] { _txtApiUrl.Text.Trim() } }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(collection, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(sfd.FileName, json, Encoding.UTF8);
        MessageBox.Show(this, "Đã xuất file Postman Collection thành công!", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}