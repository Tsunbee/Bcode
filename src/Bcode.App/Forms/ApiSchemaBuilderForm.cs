using System.Text;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

public class ApiSchemaBuilderForm : ThemedForm
{
    private class ApiDefinitionItem
    {
        public string ApiName { get; set; } = "API Mới";
        public int DbIndex { get; set; } = 0; // 0: App Data, 1: Sys Data
        public string TableName { get; set; } = "";
        public string Description { get; set; } = "";
        public List<ApiColumnItem> Columns { get; set; } = new();

        public override string ToString() => ApiName;
    }

    private class ApiColumnItem
    {
        public string ColName { get; set; } = "";
        public string DataType { get; set; } = "";
        public string FieldDesc { get; set; } = "";
        public string LogicDesc { get; set; } = "";
    }

    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly TableDataService _tableDataService;

    // Danh sách API (Bên trái)
    private readonly ListBox _lstApis = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private readonly List<ApiDefinitionItem> _apiList = new();
    private ApiDefinitionItem? _currentApi;

    // Chi tiết API (Bên phải)
    private readonly TextBox _txtApiName = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _dbCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly ComboBox _tableCombo = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly TextBox _txtApiDesc = new() { Multiline = true, Height = 65, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    private bool _isLoadingData = false;

    public ApiSchemaBuilderForm(SqlObjectBrowserService sqlObjectService, TableDataService tableDataService)
    {
        _sqlObjectService = sqlObjectService;
        _tableDataService = tableDataService;

        Text = "Tạo Cấu Trúc API (Nhiều API & Xuất 1 Lần)";
        Width = 1000;
        Height = 680;
        MinimumSize = new Size(860, 560);
        StartPosition = FormStartPosition.CenterParent;

        InitializeComponents();
        Load += async (_, _) =>
        {
            await LoadDbTablesAsync();
            AddNewApi("API 1");
        };
    }

    private void InitializeComponents()
    {
        _dbCombo.Items.AddRange(new object[] { "App Data", "Sys Data" });
        _dbCombo.SelectedIndex = 0;
        _dbCombo.SelectedIndexChanged += async (_, _) =>
        {
            if (_isLoadingData) return;
            await LoadDbTablesAsync();
        };

        _tableCombo.SelectedIndexChanged += async (_, _) =>
        {
            if (_isLoadingData) return;
            await LoadColumnsFromSelectedTableAsync();
        };

        _txtApiName.TextChanged += (_, _) =>
        {
            if (_isLoadingData || _currentApi is null) return;
            _currentApi.ApiName = string.IsNullOrWhiteSpace(_txtApiName.Text) ? "(Chưa đặt tên)" : _txtApiName.Text.Trim();
            
            // Cập nhật lại tên trên ListBox
            var idx = _lstApis.SelectedIndex;
            if (idx >= 0)
            {
                _lstApis.Items[idx] = _currentApi;
            }
        };

        // Grid thiết lập các cột
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ColName", HeaderText = "Cột DB", ReadOnly = true, FillWeight = 25 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "DataType", HeaderText = "Kiểu dữ liệu", ReadOnly = true, FillWeight = 20 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FieldDesc", HeaderText = "Tên API / Diễn giải", FillWeight = 25 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "LogicDesc", HeaderText = "Quy tắc xử lý / Mặc định", FillWeight = 30 });

        // Layout chia đôi: Trái (List API) - Phải (Nội dung API)
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1,
            SplitterDistance = 240
        };

        // --- PANEL TRÁI: Quản lý danh sách API ---
        var leftPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var leftHeader = new Label { Text = "Danh sách API:", Dock = DockStyle.Top, Height = 24, Font = new Font(Font, FontStyle.Bold) };
        
        var leftButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, FlowDirection = FlowDirection.LeftToRight };
        var btnAddApi = new PillButton { Text = "+ Thêm API", Width = 95, Height = 30 };
        btnAddApi.Tag = "primary";
        btnAddApi.Click += (_, _) => AddNewApi($"API {_apiList.Count + 1}");

        var btnRemoveApi = new PillButton { Text = "Xóa", Width = 65, Height = 30 };
        btnRemoveApi.Click += (_, _) => RemoveCurrentApi();

        leftButtons.Controls.Add(btnAddApi);
        leftButtons.Controls.Add(btnRemoveApi);

        _lstApis.SelectedIndexChanged += (_, _) => OnApiSelectionChanged();

        leftPanel.Controls.Add(_lstApis);
        leftPanel.Controls.Add(leftButtons);
        leftPanel.Controls.Add(leftHeader);
        split.Panel1.Controls.Add(leftPanel);

        // --- PANEL PHẢI: Chi tiết cấu hình API ---
        var rightPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var detailTop = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 175,
            ColumnCount = 4,
            RowCount = 3
        };
        detailTop.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        detailTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        detailTop.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        detailTop.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

        // Hàng 0: Tên API
        detailTop.Controls.Add(new Label { Text = "Tên API:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        detailTop.Controls.Add(_txtApiName, 1, 0);
        detailTop.SetColumnSpan(_txtApiName, 3);

        // Hàng 1: DB & Chọn bảng
        detailTop.Controls.Add(new Label { Text = "Database:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        detailTop.Controls.Add(_dbCombo, 1, 1);
        detailTop.Controls.Add(new Label { Text = "Chọn Bảng:", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
        detailTop.Controls.Add(_tableCombo, 3, 1);

        // Hàng 2: Mô tả luồng xử lý
        detailTop.Controls.Add(new Label { Text = "Mô tả xử lý:", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top }, 0, 2);
        detailTop.Controls.Add(_txtApiDesc, 1, 2);
        detailTop.SetColumnSpan(_txtApiDesc, 3);

        rightPanel.Controls.Add(_grid);
        rightPanel.Controls.Add(detailTop);
        split.Panel2.Controls.Add(rightPanel);

        // Thanh thao tác dưới cùng
        var actions = new WebActionBar { Dock = DockStyle.Bottom };
        actions.Add("export_doc", "📄 Xuất tất cả API ra Word (.doc)", WebActionKind.Primary, left: true)
               .Add("close", "Đóng", WebActionKind.Quiet);

        actions.Invoked += id =>
        {
            if (id == "export_doc") ExportAllToWord();
            else Close();
        };

        Controls.Add(split);
        Controls.Add(actions);
    }

    private void AddNewApi(string defaultName)
    {
        SaveCurrentApiState();

        var api = new ApiDefinitionItem
        {
            ApiName = defaultName,
            DbIndex = 0,
            TableName = _tableCombo.Items.Count > 0 ? _tableCombo.Items[0]?.ToString() ?? "" : ""
        };

        _apiList.Add(api);
        _lstApis.Items.Add(api);
        _lstApis.SelectedItem = api;
    }

    private void RemoveCurrentApi()
    {
        if (_currentApi is null) return;
        if (_apiList.Count <= 1)
        {
            MessageBox.Show(this, "Phải giữ lại ít nhất 1 API trong danh sách.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var idx = _lstApis.SelectedIndex;
        _apiList.Remove(_currentApi);
        _lstApis.Items.RemoveAt(idx);

        var nextIdx = Math.Min(idx, _apiList.Count - 1);
        _lstApis.SelectedIndex = nextIdx;
    }

    private void SaveCurrentApiState()
    {
        if (_currentApi is null) return;

        _currentApi.ApiName = _txtApiName.Text.Trim();
        _currentApi.DbIndex = _dbCombo.SelectedIndex;
        _currentApi.TableName = _tableCombo.Text.Trim();
        _currentApi.Description = _txtApiDesc.Text.Trim();

        _currentApi.Columns.Clear();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            _currentApi.Columns.Add(new ApiColumnItem
            {
                ColName = row.Cells["ColName"].Value?.ToString() ?? "",
                DataType = row.Cells["DataType"].Value?.ToString() ?? "",
                FieldDesc = row.Cells["FieldDesc"].Value?.ToString() ?? "",
                LogicDesc = row.Cells["LogicDesc"].Value?.ToString() ?? ""
            });
        }
    }

    private async void OnApiSelectionChanged()
    {
        if (_lstApis.SelectedItem is not ApiDefinitionItem selected || selected == _currentApi) return;

        SaveCurrentApiState();
        _currentApi = selected;

        _isLoadingData = true;
        try
        {
            _txtApiName.Text = selected.ApiName;
            _dbCombo.SelectedIndex = Math.Clamp(selected.DbIndex, 0, _dbCombo.Items.Count - 1);
            await LoadDbTablesAsync();

            _tableCombo.Text = selected.TableName;
            _txtApiDesc.Text = selected.Description;

            _grid.Rows.Clear();
            if (selected.Columns.Count > 0)
            {
                foreach (var c in selected.Columns)
                {
                    _grid.Rows.Add(c.ColName, c.DataType, c.FieldDesc, c.LogicDesc);
                }
            }
            else if (!string.IsNullOrWhiteSpace(selected.TableName))
            {
                await LoadColumnsFromSelectedTableAsync();
            }
        }
        finally
        {
            _isLoadingData = false;
        }
    }

    private async Task LoadDbTablesAsync()
    {
        try
        {
            var isSys = _dbCombo.SelectedIndex == 1;
            var objects = await _sqlObjectService.ListObjectsAsync(isSys);
            _tableCombo.Items.Clear();
            foreach (var o in objects.Where(o => o.Kind == SqlObjectKind.Table || o.Kind == SqlObjectKind.View))
                _tableCombo.Items.Add(o.QualifiedName);

            if (_tableCombo.Items.Count > 0 && string.IsNullOrWhiteSpace(_tableCombo.Text))
                _tableCombo.SelectedIndex = 0;
        }
        catch { }
    }

    private async Task LoadColumnsFromSelectedTableAsync()
    {
        var rawTable = _tableCombo.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawTable)) return;

        var parts = rawTable.Replace("[", "").Replace("]", "").Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : parts[0];
        var isSys = _dbCombo.SelectedIndex == 1;

        _grid.Rows.Clear();
        try
        {
            var types = await _tableDataService.GetColumnTypesAsync(isSys, schema, table);
            foreach (var kvp in types)
            {
                _grid.Rows.Add(kvp.Key, kvp.Value, kvp.Key.ToLower(), "");
            }
        }
        catch { }
    }

    private void ExportAllToWord()
    {
        SaveCurrentApiState();

        if (_apiList.Count == 0)
        {
            MessageBox.Show(this, "Không có API nào để xuất.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var sfd = new SaveFileDialog
        {
            Filter = "Word Document (*.doc)|*.doc",
            FileName = $"Tai_Lieu_Tong_Hop_API_{DateTime.Now:yyyyMMdd_HHmmss}.doc"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("<html xmlns:o='urn:schemas-microsoft-com:office:office' xmlns:w='urn:schemas-microsoft-com:office:word' xmlns='http://www.w3.org/TR/REC-html40'>");
        sb.AppendLine("<head><meta charset='utf-8'><title>Tài liệu đặc tả tổng hợp API</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; color: #222; margin: 20px; }");
        sb.AppendLine("h1 { color: #D97706; border-bottom: 2px solid #D97706; padding-bottom: 6px; }");
        sb.AppendLine("h2 { color: #1E3A8A; margin-top: 30px; border-bottom: 1px solid #CCC; padding-bottom: 4px; }");
        sb.AppendLine(".desc-box { background: #F8FAFC; border-left: 4px solid #F5A623; padding: 10px; margin: 10px 0; font-size: 13px; }");
        sb.AppendLine("table { border-collapse: collapse; width: 100%; margin-top: 10px; }");
        sb.AppendLine("th, td { border: 1px solid #CBD5E1; padding: 7px 10px; font-size: 12.5px; }");
        sb.AppendLine("th { background-color: #F1F5F9; font-weight: bold; text-align: left; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h1>TÀI LIỆU ĐẶC TẢ TỔNG HỢP API</h1>");
        sb.AppendLine($"<p><i>Ngày xuất tài liệu: {DateTime.Now:dd/MM/yyyy HH:mm:ss} | Tổng số API: {_apiList.Count}</i></p>");
        sb.AppendLine("<hr/>");

        // Mục lục tóm tắt
        sb.AppendLine("<h3>Mục Lục API</h3><ol>");
        foreach (var api in _apiList)
        {
            sb.AppendLine($"<li><b>{api.ApiName}</b> (Bảng: <code>{api.TableName}</code> - {(api.DbIndex == 1 ? "Sys Data" : "App Data")})</li>");
        }
        sb.AppendLine("</ol><br/>");

        // Chi tiết từng API
        for (int i = 0; i < _apiList.Count; i++)
        {
            var api = _apiList[i];
            sb.AppendLine($"<h2>{i + 1}. {api.ApiName}</h2>");
            sb.AppendLine($"<p><b>Database:</b> {(api.DbIndex == 1 ? "Sys Data" : "App Data")} &nbsp;&nbsp;|&nbsp;&nbsp; <b>Bảng gốc:</b> <code>{api.TableName}</code></p>");
            
            sb.AppendLine("<p><b>Mô tả luồng / Logic xử lý:</b></p>");
            var safeDesc = string.IsNullOrWhiteSpace(api.Description) ? "<i>(Chưa có mô tả)</i>" : System.Net.WebUtility.HtmlEncode(api.Description).Replace("\n", "<br/>");
            sb.AppendLine($"<div class='desc-box'>{safeDesc}</div>");

            sb.AppendLine("<table>");
            sb.AppendLine("<thead><tr><th style='width: 25%;'>Cột CSDL</th><th style='width: 20%;'>Kiểu dữ liệu</th><th style='width: 25%;'>Tên trường API</th><th style='width: 30%;'>Quy tắc xử lý / Mặc định</th></tr></thead>");
            sb.AppendLine("<tbody>");

            if (api.Columns.Count == 0)
            {
                sb.AppendLine("<tr><td colspan='4' style='text-align:center; color: #888;'><i>Không có cột dữ liệu</i></td></tr>");
            }
            else
            {
                foreach (var c in api.Columns)
                {
                    sb.AppendLine($"<tr><td>{c.ColName}</td><td>{c.DataType}</td><td><b>{c.FieldDesc}</b></td><td>{c.LogicDesc}</td></tr>");
                }
            }

            sb.AppendLine("</tbody></table>");
            sb.AppendLine("<br/><br/>");
        }

        sb.AppendLine("</body></html>");

        File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
        MessageBox.Show(this, $"Đã xuất thành công {_apiList.Count} API ra file Word!", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}