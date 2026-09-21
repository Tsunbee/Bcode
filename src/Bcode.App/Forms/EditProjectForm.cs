using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Compact single-project editor — matches FCode's own "Edit Project" popup (Server Name /
/// Login User / Password / Test Connection, then Sys Data / App Data / DB Access / ID /
/// Login WLink / Program Path / Source Path / Mobile Path / Working Path / Registry Name,
/// OK/Cancel). ConnectionSettingsForm's list-based "Kết nối/Database/Project" layout stays
/// the full multi-workspace editor; this is the one-project review popup that shows right
/// after MainForm.QuickSelectProjectByCode auto-generates a Workspace from the naming
/// template (see GenerateProjectTemplate) so Bee can check/adjust before it's saved.
/// </summary>
public class EditProjectForm : ThemedForm
{
    private readonly DbConnectionService _connections;

    private readonly TextBox _serverBox, _userBox, _passBox;
    private readonly TextBox _sysDbBox, _appDbBox, _dbAccessBox, _idBox, _wlinkBox;
    private readonly TextBox _programPathBox, _sourcePathBox, _mobilePathBox, _workingPathBox, _registryBox;
    private readonly WebActionBar _actions;

    /// <summary>The edited Workspace — only updated when the dialog closes with DialogResult.OK.</summary>
    public Workspace Result { get; private set; }

    public EditProjectForm(Workspace ws, DbConnectionService connections)
    {
        _connections = connections;
        Result = ws;

        Text = "Edit Project";
        Width = 640;
        Height = 620;
        MinimumSize = new Size(560, 480);
        StartPosition = FormStartPosition.CenterParent;

        var table = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, AutoSize = true, Padding = new Padding(12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _serverBox = AddRow(table, "Server Name", ws.Server);
        _userBox = AddRow(table, "Login User", ws.User);
        _passBox = AddRow(table, "Password", ws.Password);
        _passBox.UseSystemPasswordChar = true;

        // Test Connection moved out of the middle of the field stack and into the bottom
        // action bar with its result line — it is an action, not a field, and inline it
        // pushed every Database/Project row further down the scroll.
        var hr = new Label { Height = 1, BackColor = AppColors.Border, Margin = new Padding(0, 0, 0, 10) };
        AddFullWidthControl(table, hr);

        _sysDbBox = AddRow(table, "Sys Data", ws.SysDatabase);
        _appDbBox = AddRow(table, "App Data", ws.AppDatabase);
        _dbAccessBox = AddRow(table, "DB Access", $"{ws.SysDatabase}, {ws.AppDatabase}");
        _dbAccessBox.ReadOnly = true;
        _idBox = AddRow(table, "ID", ws.ProjectId);
        _wlinkBox = AddRow(table, "Login WLink", ws.LoginWLink);
        _programPathBox = AddRow(table, "Program Path", ws.ProgramPath);
        _sourcePathBox = AddRow(table, "Source Path", ws.SourcePath);
        _mobilePathBox = AddRow(table, "Mobile Path", ws.MobilePath);
        _workingPathBox = AddRow(table, "Working Path", ws.WorkingPath);
        _registryBox = AddRow(table, "Registry Name", ws.RegistryName);

        // DB Access is just "SysData, AppData" — keep it in sync as the user tweaks either box
        // instead of asking them to type the same two names a third time.
        _sysDbBox.TextChanged += (_, _) => UpdateDbAccess();
        _appDbBox.TextChanged += (_, _) => UpdateDbAccess();

        _actions = new WebActionBar { DefaultActionId = "ok", CancelActionId = "cancel" };
        _actions.Add("test", "Test Connection", WebActionKind.Normal, left: true)
                .Add("cancel", "Cancel", WebActionKind.Quiet)
                .Add("ok", "OK", WebActionKind.Primary);
        _actions.Invoked += async id =>
        {
            switch (id)
            {
                case "test": await TestAsync(); break;
                case "ok": Apply(); DialogResult = DialogResult.OK; Close(); break;
                case "cancel": DialogResult = DialogResult.Cancel; Close(); break;
            }
        };
        var buttons = _actions;

        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        scroll.Controls.Add(table);

        Controls.Add(scroll);
        Controls.Add(buttons);
    }

    private void UpdateDbAccess() => _dbAccessBox.Text = $"{_sysDbBox.Text.Trim()}, {_appDbBox.Text.Trim()}";

    private static TextBox AddRow(TableLayoutPanel table, string label, string value)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }, 0, row);
        var box = new TextBox { Text = value, Width = 420, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = new Padding(0, 4, 0, 4) };
        table.Controls.Add(box, 1, row);
        return box;
    }

    private static void AddFullWidthControl(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    private async Task TestAsync()
    {
        var probe = SnapshotToWorkspace();
        _actions.SetEnabled("test", false);
        _actions.SetStatus("Đang kiểm tra...");
        var (sysOk, sysMsg) = await _connections.TestConnectionAsync(probe, useSysDatabase: true);
        var (appOk, appMsg) = await _connections.TestConnectionAsync(probe, useSysDatabase: false);
        _actions.SetStatus($"Sys Data: {sysMsg}   |   App Data: {appMsg}", ok: sysOk && appOk);
        _actions.SetEnabled("test", true);
    }

    private Workspace SnapshotToWorkspace() => new()
    {
        Name = _idBox.Text.Trim(),
        Server = _serverBox.Text.Trim(),
        IntegratedSecurity = false,
        User = _userBox.Text.Trim(),
        Password = _passBox.Text,
        SysDatabase = _sysDbBox.Text.Trim(),
        AppDatabase = _appDbBox.Text.Trim(),
        ProjectId = _idBox.Text.Trim(),
        LoginWLink = _wlinkBox.Text.Trim(),
        ProgramPath = _programPathBox.Text.Trim(),
        SourcePath = _sourcePathBox.Text.Trim(),
        MobilePath = _mobilePathBox.Text.Trim(),
        WorkingPath = _workingPathBox.Text.Trim(),
        RegistryName = _registryBox.Text.Trim(),
    };

    private void Apply()
    {
        var edited = SnapshotToWorkspace();
        Result.Name = edited.Name;
        Result.Server = edited.Server;
        Result.IntegratedSecurity = edited.IntegratedSecurity;
        Result.User = edited.User;
        Result.Password = edited.Password;
        Result.SysDatabase = edited.SysDatabase;
        Result.AppDatabase = edited.AppDatabase;
        Result.ProjectId = edited.ProjectId;
        Result.LoginWLink = edited.LoginWLink;
        Result.ProgramPath = edited.ProgramPath;
        Result.SourcePath = edited.SourcePath;
        Result.MobilePath = edited.MobilePath;
        Result.WorkingPath = edited.WorkingPath;
        Result.RegistryName = edited.RegistryName;
        DialogResult = DialogResult.OK;
        Close();
    }
}
