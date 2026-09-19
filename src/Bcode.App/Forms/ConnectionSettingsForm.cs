using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// File &gt; Choose Server / Workspaces (Edit Project): manages the list of
/// Workspaces (WS). Field layout mirrors FastBusiness's own "Edit Project"
/// dialog (Server Name / Login User / Password / Sys Data / App Data /
/// Program Path / Source Path / Mobile Path / Working Path / Registry Name /
/// ID / Login WLink) so a real project's config maps here directly.
///
/// Laid out as clearly separated GroupBox sections (Kết nối / Database /
/// Project) instead of one long TableLayoutPanel, and the Test Connection /
/// Save bar is pinned outside the scrollable area so it's always visible and
/// clickable — a previous version placed these rows by re-reading
/// TableLayoutPanel.RowCount without ever incrementing it, which silently
/// collided controls on top of each other (that's why the Database header
/// looked disconnected and Test Connection / Save appeared to do nothing:
/// they were really hidden behind other controls).
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
    private readonly Button _testButton;
    private readonly Label _testResultLabel;

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

        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(0, 4, 0, 0) };
        var addBtn = new Button { Text = "+ New" };
        var delBtn = new Button { Text = "Delete" };
        addBtn.Click += (_, _) => AddNew();
        delBtn.Click += (_, _) => DeleteSelected();
        listButtons.Controls.Add(addBtn);
        listButtons.Controls.Add(delBtn);

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 220, Padding = new Padding(8, 8, 4, 8) };
        leftPanel.Controls.Add(_list);
        leftPanel.Controls.Add(listButtons);

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

        // Dock order matters: add bottom-most section first so DockStyle.Top
        // stacking ends up Kết nối -> Database -> Project, top to bottom.
        scroll.Controls.Add(projGroup);
        scroll.Controls.Add(dbGroup);
        scroll.Controls.Add(connGroup);

        // ---- Bottom: Test Connection (always visible, own row, never shared with other controls) ----
        _testButton = new Button { Text = "Test Connection", Width = 150, Height = 30 };
        _testButton.Click += async (_, _) => await TestAsync();
        _testResultLabel = new Label { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(10, 8, 0, 0) };
        var testBar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = false, Padding = new Padding(12, 6, 12, 6) };
        testBar.Controls.Add(_testButton);
        testBar.Controls.Add(_testResultLabel);

        var saveButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12, 6, 12, 6) };
        var saveBtn = new Button { Text = "Save && Close", Width = 110 };
        saveBtn.Click += (_, _) => { SaveAll(); DialogResult = DialogResult.OK; Close(); };
        var applyBtn = new Button { Text = "Apply", Width = 90 };
        applyBtn.Click += (_, _) =>
        {
            SaveCurrentEdit();
            _testResultLabel.ForeColor = Color.DarkGreen;
            _testResultLabel.Text = "Đã lưu (Apply).";
        };
        saveButtons.Controls.Add(saveBtn);
        saveButtons.Controls.Add(applyBtn);

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(scroll);
        rightPanel.Controls.Add(testBar);
        rightPanel.Controls.Add(saveButtons);
        // Docking order: Fill first, then Bottom items added after so they
        // reserve their strip and Fill (scroll) takes the remaining space.
        scroll.SendToBack();

        Controls.Add(rightPanel);
        Controls.Add(leftPanel);

        ToggleAuthFields();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private static TableLayoutPanel NewFieldTable()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    /// <summary>Appends a label+textbox row. Row index is a local snapshot taken
    /// and committed (RowCount++) in the same statement, so every caller sees a
    /// consistent next-free-row — never re-derived from a stale RowCount read.</summary>
    private static TextBox AddRow(TableLayoutPanel table, string label)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }, 0, row);
        var box = new TextBox { Width = 480, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 4, 0, 4) };
        table.Controls.Add(box, 1, row);
        return box;
    }

    /// <summary>Appends a row where a single control spans both columns (e.g. a checkbox).</summary>
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
        _testResultLabel.Text = "";
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
        if (idx >= 0) _list.Items[idx] = ws; // refresh display text
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
            _testResultLabel.ForeColor = Color.Firebrick;
            _testResultLabel.Text = "Chưa chọn Workspace nào ở danh sách bên trái.";
            return;
        }

        _testButton.Enabled = false;
        _testResultLabel.ForeColor = SystemColors.ControlText;
        _testResultLabel.Text = "Đang kiểm tra Sys Data...";
        try
        {
            var (sysOk, sysMsg) = await _connections.TestConnectionAsync(ws, useSysDatabase: true);
            var (appOk, appMsg) = await _connections.TestConnectionAsync(ws, useSysDatabase: false);

            _testResultLabel.ForeColor = sysOk && appOk ? Color.DarkGreen : Color.Firebrick;
            _testResultLabel.Text = $"Sys Data: {sysMsg}   |   App Data: {appMsg}";
        }
        catch (Exception ex)
        {
            _testResultLabel.ForeColor = Color.Firebrick;
            _testResultLabel.Text = $"Lỗi: {ex.Message}";
        }
        finally
        {
            _testButton.Enabled = true;
        }
    }
}
