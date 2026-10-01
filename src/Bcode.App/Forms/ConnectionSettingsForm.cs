using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// File > Choose Server / Workspaces (Edit Project): quản lý danh sách các Workspace (WS)
/// với giao diện hoàn toàn responsive, tự động co giãn theo kích thước cửa sổ.
/// </summary>
public class ConnectionSettingsForm : ThemedForm
{
    private readonly AppSettings _settings;
    private readonly DbConnectionService _connections;
    private readonly ListBox _list;
    private readonly TextBox _nameBox, _serverBox, _userBox, _passBox;
    private readonly TextBox _sysDbBox, _appDbBox, _idBox, _loginWLinkBox;
    private readonly TextBox _programPathBox, _sourcePathBox, _mobilePathBox, _workingPathBox, _registryNameBox;
    private readonly CheckBox _integratedCheck;
    
    private readonly Label _statusLabel;
    private readonly PillButton _testBtn, _applyBtn, _saveBtn, _closeBtn;
    private readonly PillButton _newBtn, _deleteBtn;

    private readonly ListBox _fsgCacheList;
    private readonly TextBox _fsgCodeBox;
    private readonly TextBox _fsgSearchBox;
    private readonly PillButton _fsgCrawlBtn, _fsgAddBtn, _fsgFindNewBtn;
    private List<FsgProjectLookupService.FsgProjectSummary> _fsgCacheAll = new();

    public ConnectionSettingsForm(AppSettings settings, DbConnectionService connections)
    {
        _settings = settings;
        _connections = connections;

        Text = "Edit Project (Workspaces)";
        Width = 1120;
        Height = 760;
        MinimumSize = new Size(920, 640);
        StartPosition = FormStartPosition.CenterParent;
        
        // Cho phép Form co giãn và phóng to thu nhỏ thoải mái
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = true;

        // ---- Left: workspace list ----
        _list = new ListBox { Dock = DockStyle.Fill };
        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        foreach (var ws in _settings.Workspaces) _list.Items.Add(ws);

        var leftButtonPanel = new FlowLayoutPanel 
        { 
            Dock = DockStyle.Bottom, 
            Height = 46, 
            FlowDirection = FlowDirection.LeftToRight, 
            Padding = new Padding(4)
        };
        _newBtn = PillButton.Flat("+ New");
        _newBtn.Click += (_, _) => AddNew();
        
        _deleteBtn = PillButton.Flat("Delete");
        _deleteBtn.ForeColor = AppColors.Danger;
        _deleteBtn.Click += (_, _) => DeleteSelected();
        
        leftButtonPanel.Controls.Add(_newBtn);
        leftButtonPanel.Controls.Add(_deleteBtn);

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 240, Padding = new Padding(10, 12, 4, 8) };
        leftPanel.Controls.Add(_list);
        leftPanel.Controls.Add(leftButtonPanel);
        _list.SendToBack();

        // ---- Right: scrollable page of cards (2 columns; FSG card full width underneath) ----
        // Trước đây mọi GroupBox xếp 1 cột dọc, cột nhãn AutoSize hẹp nên ô nhập bị bó, còn khối
        // FSG (list 140px + 4 hàng nút) đẩy phần cấu hình chính xuống dưới. Giờ: "Kết nối" +
        // "Database" bên trái, "Project" (đường dẫn) bên phải — cùng 1 tầm nhìn, không phải cuộn;
        // cột nhãn cố định 120px để mọi ô nhập thẳng hàng giữa các card.
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14, 12, 14, 8) };

        var page = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0) };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var connTable = NewFieldTable();
        _nameBox = AddRow(connTable, "Tên WS");
        _serverBox = AddRow(connTable, "Server Name");
        _integratedCheck = new CheckBox { Text = "Integrated Security (Windows Auth)", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 6) };
        _integratedCheck.CheckedChanged += (_, _) => ToggleAuthFields();
        AddFullWidthRow(connTable, _integratedCheck);
        _userBox = AddRow(connTable, "Login User");
        _passBox = AddRow(connTable, "Password");
        _passBox.UseSystemPasswordChar = true;
        var connGroup = NewCard("Kết nối", connTable);

        var dbTable = NewFieldTable();
        _sysDbBox = AddRow(dbTable, "Sys Data");
        _appDbBox = AddRow(dbTable, "App Data");
        var dbGroup = NewCard("Database", dbTable);

        var projTable = NewFieldTable();
        _idBox = AddRow(projTable, "ID");
        _loginWLinkBox = AddRow(projTable, "Login WLink");
        _programPathBox = AddRow(projTable, "Program Path");
        _sourcePathBox = AddRow(projTable, "Source Path");
        _sourcePathBox.PlaceholderText = @"UNC, dùng cho File Lookup — \server\...\FBISP23";
        _mobilePathBox = AddRow(projTable, "Mobile Path");
        _workingPathBox = AddRow(projTable, "Working Path");
        _registryNameBox = AddRow(projTable, "Registry Name");
        var projGroup = NewCard("Project", projTable);

        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(connGroup, 0, 0);
        page.Controls.Add(projGroup, 1, 0);
        page.SetRowSpan(projGroup, 2);
        page.Controls.Add(dbGroup, 0, 1);

        // ---- FSG — Danh mục dự án: trái = tìm + danh sách, phải = các thao tác ----
        _fsgSearchBox = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Gõ mã hoặc tên dự án để lọc danh sách..." };
        _fsgSearchBox.TextChanged += (_, _) => ApplyFsgSearchFilter();

        _fsgCacheList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        _fsgCacheList.SelectedIndexChanged += (_, _) =>
        {
            if (_fsgCacheList.SelectedItem is FsgProjectLookupService.FsgProjectSummary p)
                _fsgCodeBox.Text = p.MaDuAn;
        };

        var fsgLeft = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 0) };
        fsgLeft.Controls.Add(_fsgCacheList);
        fsgLeft.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6 });
        fsgLeft.Controls.Add(_fsgSearchBox);

        _fsgCrawlBtn = PillButton.Flat("🔄 Cào lại danh sách dự án (FSG)");
        _fsgCrawlBtn.Click += async (_, _) => await CrawlFsgProjectsAsync();
        _fsgAddBtn = PillButton.Flat("+ Thêm dự án đã chọn vào Workspace");
        _fsgAddBtn.Click += async (_, _) => await AddFsgProjectAsync(
            _fsgCacheList.SelectedItem is FsgProjectLookupService.FsgProjectSummary sel ? sel.MaDuAn : _fsgCodeBox.Text);
        _fsgCodeBox = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Mã dự án chưa có trong danh sách" };
        _fsgFindNewBtn = PillButton.Flat("🔍 Tìm & thêm dự án mới");
        _fsgFindNewBtn.Click += async (_, _) => await AddFsgProjectAsync(_fsgCodeBox.Text);

        var fsgRight = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        fsgRight.Controls.Add(_fsgCrawlBtn);
        fsgRight.Controls.Add(_fsgAddBtn);
        fsgRight.Controls.Add(new Label { Text = "Mã dự án:", AutoSize = true, Margin = new Padding(3, 12, 0, 2), ForeColor = AppColors.TextMuted });
        _fsgCodeBox.Dock = DockStyle.None;
        _fsgCodeBox.Width = 250;
        fsgRight.Controls.Add(_fsgCodeBox);
        fsgRight.Controls.Add(_fsgFindNewBtn);

        var fsgTable = new TableLayoutPanel { Dock = DockStyle.Top, Height = 190, ColumnCount = 2 };
        fsgTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        fsgTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        fsgTable.Controls.Add(fsgLeft, 0, 0);
        fsgTable.Controls.Add(fsgRight, 1, 0);
        var fsgGroup = NewCard("FSG — Danh mục dự án (cào ngầm)", fsgTable);
        fsgGroup.Margin = new Padding(0, 0, 0, 10);
        page.Controls.Add(fsgGroup, 0, 2);
        page.SetColumnSpan(fsgGroup, 2);

        scroll.Controls.Add(page);

        // ---- Bottom action bar panel ----
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        
        _statusLabel = new Label 
        { 
            Dock = DockStyle.Fill, 
            TextAlign = ContentAlignment.MiddleLeft, 
            ForeColor = AppColors.TextMuted,
            AutoEllipsis = true
        };

        var rightButtonFlow = new FlowLayoutPanel 
        { 
            Dock = DockStyle.Right, 
            AutoSize = true, 
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        _testBtn = PillButton.Flat("Test Connection");
        _testBtn.Click += async (_, _) => await TestAsync();
        
        _closeBtn = PillButton.Flat("Đóng");
        _closeBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _applyBtn = PillButton.Flat("Apply");
        _applyBtn.Click += (_, _) => {
            SaveCurrentEdit();
            SetStatus("Đã lưu (Apply).", ok: true);
        };

        _saveBtn = PillButton.Flat("Save & Close", primary: true);
        _saveBtn.Click += (_, _) => {
            SaveAll();
            DialogResult = DialogResult.OK;
            Close();
        };

        rightButtonFlow.Controls.Add(_testBtn);
        rightButtonFlow.Controls.Add(_closeBtn);
        rightButtonFlow.Controls.Add(_applyBtn);
        rightButtonFlow.Controls.Add(_saveBtn);

        bottomBar.Controls.Add(_statusLabel);
        bottomBar.Controls.Add(rightButtonFlow);
        _statusLabel.SendToBack();

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(scroll);
        rightPanel.Controls.Add(bottomBar);
        scroll.SendToBack();

        Controls.Add(rightPanel);
        Controls.Add(leftPanel);

        ToggleAuthFields();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        RefreshFsgCacheList();
    }

    private void RefreshFsgCacheList()
    {
        _fsgCacheAll = FsgProjectLookupService.LoadCache();
        ApplyFsgSearchFilter();
    }

    private void ApplyFsgSearchFilter()
    {
        var keyword = _fsgSearchBox.Text.Trim();
        var filtered = keyword.Length == 0
            ? _fsgCacheAll
            : _fsgCacheAll.Where(p =>
                p.MaDuAn.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                p.TenDuAn.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        var previouslySelected = (_fsgCacheList.SelectedItem as FsgProjectLookupService.FsgProjectSummary)?.MaDuAn;
        _fsgCacheList.Items.Clear();
        foreach (var p in filtered) _fsgCacheList.Items.Add(p);

        if (previouslySelected is not null)
        {
            var match = filtered.FirstOrDefault(p => p.MaDuAn == previouslySelected);
            if (match is not null) _fsgCacheList.SelectedItem = match;
        }
    }

    private async Task CrawlFsgProjectsAsync()
    {
        _fsgCrawlBtn.Enabled = false;
        Cursor = Cursors.WaitCursor;
        SetStatus("Đang cào danh sách dự án từ FSG...");
        try
        {
            var result = await new FsgProjectLookupService().CrawlProjectListAsync();
            if (!result.Success)
            {
                SetStatus($"Cào danh sách FSG thất bại: {result.Error}", ok: false);
                return;
            }

            RefreshFsgCacheList();
            SetStatus($"Đã cào {result.Projects.Count} dự án từ FSG.", ok: true);
        }
        finally
        {
            Cursor = Cursors.Default;
            _fsgCrawlBtn.Enabled = true;
        }
    }

    private async Task AddFsgProjectAsync(string? code)
    {
        code = code?.Trim() ?? "";
        if (code.Length == 0)
        {
            SetStatus("Chọn 1 dự án trong danh sách cào, hoặc gõ mã dự án trước.", ok: false);
            return;
        }

        var existing = _settings.Workspaces.FirstOrDefault(w => w.ProjectId.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _list.SelectedItem = existing;
            SetStatus($"\"{code}\" đã có sẵn trong danh sách Workspace.", ok: true);
            return;
        }

        _fsgAddBtn.Enabled = false;
        _fsgFindNewBtn.Enabled = false;
        Cursor = Cursors.WaitCursor;
        SetStatus($"Đang tra thông tin kết nối cho \"{code}\" từ FSG...");
        try
        {
            var result = await new FsgProjectLookupService().LookupAsync(code);
            if (!result.Found || result.Workspace is null)
            {
                SetStatus($"Không tra được \"{code}\" từ FSG: {result.Error}", ok: false);
                return;
            }

            var ws = result.Workspace;
            _settings.Workspaces.Add(ws);
            _list.Items.Add(ws);
            _list.SelectedItem = ws;

            SetStatus($"Đã thêm \"{code}\" từ FSG thành công.", ok: true);
        }
        finally
        {
            Cursor = Cursors.Default;
            _fsgAddBtn.Enabled = true;
            _fsgFindNewBtn.Enabled = true;
        }
    }

    private void SetStatus(string text, bool? ok = null)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = ok is null ? AppColors.TextMuted : ok.Value ? AppColors.Success : AppColors.Danger;
    }

    /// <summary>Bảng nhãn + ô nhập: cột nhãn rộng cố định (mọi card thẳng hàng), ô nhập co giãn hết phần còn lại.</summary>
    private static TableLayoutPanel NewFieldTable()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    /// <summary>Một "card" của trang: GroupBox tự co theo nội dung, chiếm trọn ô lưới, cách đều các card khác.</summary>
    private static GroupBox NewCard(string title, Control content)
    {
        var box = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 6, 12, 12),
            Margin = new Padding(0, 0, 10, 10),
        };
        box.Controls.Add(content);
        return box;
    }

    private static TextBox AddRow(TableLayoutPanel table, string label)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }, 0, row);
        
        // Đặt Anchor Left | Right để ô TextBox tự động co giãn theo chiều ngang của bảng
        var box = new TextBox { Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 4, 0, 4) };
        table.Controls.Add(box, 1, row);
        return box;
    }

    private static void AddFullWidthRow(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private void ToggleAuthFields()
    {
        _userBox.Enabled = !_integratedCheck.Checked;
        _passBox.Enabled = !_integratedCheck.Checked;
    }

    private Workspace? SelectedWorkspace => _list.SelectedItem as Workspace;

    private void AddNew()
    {
        var ws = new Workspace { Name = $"WS{_settings.Workspaces.Count + 1}" };
        _settings.Workspaces.Add(ws);
        _list.Items.Add(ws);
        _list.SelectedItem = ws;
    }

    private void DeleteSelected()
    {
        if (SelectedWorkspace is not { } ws) return;
        _settings.Workspaces.Remove(ws);
        _list.Items.Remove(ws);
    }

    private void LoadSelected()
    {
        if (SelectedWorkspace is not { } ws) return;
        _nameBox.Text = ws.Name;
        _serverBox.Text = ws.Server;
        _integratedCheck.Checked = ws.IntegratedSecurity;
        _userBox.Text = ws.User;
        _passBox.Text = ws.Password;
        _sysDbBox.Text = ws.SysDatabase;
        _appDbBox.Text = ws.AppDatabase;
        _idBox.Text = ws.ProjectId;
        _loginWLinkBox.Text = ws.LoginWLink;
        _programPathBox.Text = ws.ProgramPath;
        _sourcePathBox.Text = ws.SourcePath;
        _mobilePathBox.Text = ws.MobilePath;
        _workingPathBox.Text = ws.WorkingPath;
        _registryNameBox.Text = ws.RegistryName;
        SetStatus("");
    }

    private void SaveCurrentEdit()
    {
        if (SelectedWorkspace is not { } ws) return;
        ws.Name = _nameBox.Text.Trim();
        ws.Server = _serverBox.Text.Trim();
        ws.IntegratedSecurity = _integratedCheck.Checked;
        ws.User = _userBox.Text.Trim();
        ws.Password = _passBox.Text;
        ws.SysDatabase = _sysDbBox.Text.Trim();
        ws.AppDatabase = _appDbBox.Text.Trim();
        ws.ProjectId = _idBox.Text.Trim();
        ws.LoginWLink = _loginWLinkBox.Text.Trim();
        ws.ProgramPath = _programPathBox.Text.Trim();
        ws.SourcePath = _sourcePathBox.Text.Trim();
        ws.MobilePath = _mobilePathBox.Text.Trim();
        ws.WorkingPath = _workingPathBox.Text.Trim();
        ws.RegistryName = _registryNameBox.Text.Trim();

        var idx = _list.SelectedIndex;
        if (idx >= 0) _list.Items[idx] = ws;
    }

    private void SaveAll()
    {
        SaveCurrentEdit();
        _settings.Save();
    }

    private async Task TestAsync()
    {
        SaveCurrentEdit();
        if (SelectedWorkspace is not { } ws)
        {
            SetStatus("Chưa chọn Workspace nào.", ok: false);
            return;
        }

        _testBtn.Enabled = false;
        SetStatus("Đang kiểm tra kết nối...");
        try
        {
            var (sysOk, sysMsg) = await _connections.TestConnectionAsync(ws, useSysDatabase: true);
            var (appOk, appMsg) = await _connections.TestConnectionAsync(ws, useSysDatabase: false);

            SetStatus($"Sys Data: {sysMsg}   |   App Data: {appMsg}", ok: sysOk && appOk);
        }
        catch (Exception ex)
        {
            SetStatus($"Lỗi: {ex.Message}", ok: false);
        }
        finally
        {
            _testBtn.Enabled = true;
        }
    }
}