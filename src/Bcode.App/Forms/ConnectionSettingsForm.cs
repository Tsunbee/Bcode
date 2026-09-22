using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// File > Choose Server / Workspaces (Edit Project): quản lý danh sách các Workspace (WS).
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
    
    // Sử dụng native components để hiển thị tức thì, tránh lỗi vùng đen do WebView2 load chậm
    private readonly Label _statusLabel;
    private readonly PillButton _testBtn, _applyBtn, _saveBtn, _closeBtn;
    private readonly PillButton _newBtn, _deleteBtn;

    public ConnectionSettingsForm(AppSettings settings, DbConnectionService connections)
    {
        _settings = settings;
        _connections = connections;

        Text = "Edit Project (Workspaces)";
        Width = 860;
        Height = 700;
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterParent;

        // ---- Left: workspace list ----
        _list = new ListBox { Dock = DockStyle.Fill };
        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        foreach (var ws in _settings.Workspaces) _list.Items.Add(ws);

        // Left panel buttons panel (+ New, Delete)
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

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(8, 8, 4, 8) };
        leftPanel.Controls.Add(_list);
        leftPanel.Controls.Add(leftButtonPanel);
        _list.SendToBack();

        // ---- Right: scrollable stack of GroupBox sections ----
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(12) };

        var connGroup = new GroupBox { Text = "Kết nối", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 4, 10, 10) };
        var connTable = NewFieldTable();
        _nameBox = AddRow(connTable, "Tên WS");
        _serverBox = AddRow(connTable, "Server Name");
        _integratedCheck = new CheckBox { Text = "Integrated Security (Windows Auth)", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 6) };
        _integratedCheck.CheckedChanged += (_, _) => ToggleAuthFields();
        AddFullWidthRow(connTable, _integratedCheck);
        _userBox = AddRow(connTable, "Login User");
        _passBox = AddRow(connTable, "Password");
        _passBox.UseSystemPasswordChar = true;
        connGroup.Controls.Add(connTable);

        var dbGroup = new GroupBox { Text = "Database", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 4, 10, 10) };
        var dbTable = NewFieldTable();
        _sysDbBox = AddRow(dbTable, "Sys Data");
        _appDbBox = AddRow(dbTable, "App Data");
        dbGroup.Controls.Add(dbTable);

        var projGroup = new GroupBox { Text = "Project", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 4, 10, 10) };
        var projTable = NewFieldTable();
        _idBox = AddRow(projTable, "ID");
        _loginWLinkBox = AddRow(projTable, "Login WLink");
        _programPathBox = AddRow(projTable, "Program Path");
        _sourcePathBox = AddRow(projTable, "Source Path (UNC, cho File Lookup)");
        _mobilePathBox = AddRow(projTable, "Mobile Path");
        _workingPathBox = AddRow(projTable, "Working Path");
        _registryNameBox = AddRow(projTable, "Registry Name");
        projGroup.Controls.Add(projTable);

        scroll.Controls.Add(projGroup);
        scroll.Controls.Add(dbGroup);
        scroll.Controls.Add(connGroup);

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
    }

    private void SetStatus(string text, bool? ok = null)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = ok is null ? AppColors.TextMuted : ok.Value ? AppColors.Success : AppColors.Danger;
    }

    private static TableLayoutPanel NewFieldTable()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static TextBox AddRow(TableLayoutPanel table, string label)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }, 0, row);
        var box = new TextBox { Width = 480, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 4, 0, 4) };
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
            SetStatus("Chưa chọn Workspace nào ở danh sách bên trái.", ok: false);
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