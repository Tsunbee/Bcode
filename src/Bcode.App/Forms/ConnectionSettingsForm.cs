using Bcode.App.Controls;
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
    private readonly WebActionBar _actions;

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

        // The workspace list's own two actions — HTML bar (Controls/WebActionBar.cs) so
        // Delete reads as destructive instead of looking identical to "+ New".
        var listButtons = new WebActionBar { Height = 46 };
        listButtons.Add("new", "+ New", WebActionKind.Normal, left: true)
                   .Add("delete", "Delete", WebActionKind.Danger, left: true);
        listButtons.Invoked += id =>
        {
            if (id == "new") AddNew();
            else if (id == "delete") DeleteSelected();
        };

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

        // ---- Bottom: one action bar instead of two stacked rows ----
        // Test Connection + its result line used to be one FlowLayoutPanel and Apply/Save a
        // second one right below it — 44+ px of chrome each, with the result text wrapping
        // into the buttons. The HTML bar holds all three actions plus the result line
        // (Controls/WebActionBar.cs), so the form gets one strip back and the message can
        // never collide with a button again.
        _actions = new WebActionBar { DefaultActionId = "save", CancelActionId = "close" };
        _actions.Add("test", "Test Connection", WebActionKind.Normal, left: true)
                .Add("close", "Đóng", WebActionKind.Quiet)
                .Add("apply", "Apply", WebActionKind.Normal)
                .Add("save", "Save & Close", WebActionKind.Primary);
        _actions.Invoked += async id =>
        {
            switch (id)
            {
                case "test":
                    await TestAsync();
                    break;
                case "apply":
                    SaveCurrentEdit();
                    _actions.SetStatus("Đã lưu (Apply).", ok: true);
                    break;
                case "save":
                    SaveAll();
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
                case "close":
                    DialogResult = DialogResult.Cancel;
                    Close();
                    break;
            }
        };

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(scroll);
        rightPanel.Controls.Add(_actions);
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
        _actions.SetStatus("");
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
            _actions.SetStatus("Chưa chọn Workspace nào ở danh sách bên trái.", ok: false);
            return;
        }

        _actions.SetEnabled("test", false);
        _actions.SetStatus("Đang kiểm tra Sys Data...");
        try
        {
            var (sysOk, sysMsg) = await _connections.TestConnectionAsync(ws, useSysDatabase: true);
            var (appOk, appMsg) = await _connections.TestConnectionAsync(ws, useSysDatabase: false);

            _actions.SetStatus($"Sys Data: {sysMsg}   |   App Data: {appMsg}", ok: sysOk && appOk);
        }
        catch (Exception ex)
        {
            _actions.SetStatus($"Lỗi: {ex.Message}", ok: false);
        }
        finally
        {
            _actions.SetEnabled("test", true);
        }
    }
}
