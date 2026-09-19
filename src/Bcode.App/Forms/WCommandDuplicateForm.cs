using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Duplicate Menu" — result of "Check WCommand". Two tabs, matching FCode's own dialog:
/// rows with no matching command row (which also folds in an actual duplicate-wmenu_id
/// check — see WCommandService.FindDuplicatesAsync), and rows where wcommand/command
/// disagree on sysid for the same menu_id.
/// </summary>
public class WCommandDuplicateForm : Bcode.App.UI.ThemedForm
{
    public WCommandDuplicateForm(WCommandDuplicateResult result)
    {
        Text = "Duplicate Menu";
        Width = 780;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var notExistsTab = new TabPage("Not exists in Command");
        notExistsTab.Controls.Add(BuildGrid(result.NotExistsInCommand));

        var diffSysidTab = new TabPage("Difference Sysid");
        diffSysidTab.Controls.Add(BuildGrid(result.DifferenceSysid));

        tabs.TabPages.Add(notExistsTab);
        tabs.TabPages.Add(diffSysidTab);

        var summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            Padding = new Padding(8, 4, 0, 0),
            Text = $"Not exists in Command: {result.NotExistsInCommand.Count}    Difference Sysid: {result.DifferenceSysid.Count}",
        };

        Controls.Add(tabs);
        Controls.Add(summary);
    }

    private static DataGridView BuildGrid(List<WCommandItem> rows)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            ReadOnly = true,
            AutoGenerateColumns = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        };

        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "wmenu_id", HeaderText = "WMenu Id", DataPropertyName = nameof(WCommandItem.WMenuId) });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "bar", HeaderText = "Bar", DataPropertyName = nameof(WCommandItem.Bar) });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "menu_id", HeaderText = "Menu Id", DataPropertyName = nameof(WCommandItem.MenuId) });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "link", HeaderText = "Link", DataPropertyName = nameof(WCommandItem.Link) });
        grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "sysid", HeaderText = "Sysid", DataPropertyName = nameof(WCommandItem.SysId) });

        grid.DataSource = rows;
        return grid;
    }
}
