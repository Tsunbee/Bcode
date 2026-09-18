using Bcode.App.Models;
using Bcode.App.Services;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Forms;

/// <summary>
/// "Compare Structure": diffs a table's columns between two workspaces (e.g. a
/// customer DB vs. a reference/master DB) — useful before pushing a schema update.
/// </summary>
public class CompareStructureForm : Bcode.App.UI.ThemedForm
{
    private readonly AppSettings _settings;
    private readonly SchemaCompareService _schema = new();
    private readonly ComboBox _leftWs, _rightWs;
    private readonly TextBox _tableBox, _schemaBox;
    private readonly ListView _resultView;

    public CompareStructureForm(AppSettings settings)
    {
        _settings = settings;
        Text = "Compare Structure";
        Width = 900;
        Height = 620;
        StartPosition = FormStartPosition.CenterParent;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70, WrapContents = true };
        _leftWs = MakeWsCombo();
        _rightWs = MakeWsCombo();
        _schemaBox = new TextBox { Width = 70, Text = "dbo" };
        _tableBox = new TextBox { Width = 180, PlaceholderText = "table name" };
        var runBtn = new Button { Text = "Compare", Width = 90 };
        runBtn.Click += async (_, _) => await RunAsync();

        top.Controls.Add(Labeled("WS trái:", _leftWs));
        top.Controls.Add(Labeled("WS phải:", _rightWs));
        top.Controls.Add(Labeled("Schema:", _schemaBox));
        top.Controls.Add(Labeled("Table:", _tableBox));
        top.Controls.Add(runBtn);

        _resultView = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
        _resultView.Columns.Add("Trạng thái", 100);
        _resultView.Columns.Add("Cột", 180);
        _resultView.Columns.Add("Trái", 260);
        _resultView.Columns.Add("Phải", 260);

        Controls.Add(_resultView);
        Controls.Add(top);
    }

    private ComboBox MakeWsCombo()
    {
        var combo = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        foreach (var ws in _settings.Workspaces) combo.Items.Add(ws);
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        return combo;
    }

    private static Control Labeled(string text, Control inner)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown };
        panel.Controls.Add(new Label { Text = text, AutoSize = true });
        panel.Controls.Add(inner);
        return panel;
    }

    private async Task RunAsync()
    {
        if (_leftWs.SelectedItem is not Workspace left || _rightWs.SelectedItem is not Workspace right)
        {
            MessageBox.Show(this, "Chọn Workspace bên trái và bên phải trước.", "Bcode");
            return;
        }
        if (string.IsNullOrWhiteSpace(_tableBox.Text))
        {
            MessageBox.Show(this, "Nhập tên bảng cần so sánh.", "Bcode");
            return;
        }

        _resultView.Items.Clear();
        try
        {
            await using var leftConn = new SqlConnection(left.BuildConnectionString());
            await using var rightConn = new SqlConnection(right.BuildConnectionString());
            await leftConn.OpenAsync();
            await rightConn.OpenAsync();

            var leftCols = await _schema.GetColumnsAsync(leftConn, _schemaBox.Text.Trim(), _tableBox.Text.Trim());
            var rightCols = await _schema.GetColumnsAsync(rightConn, _schemaBox.Text.Trim(), _tableBox.Text.Trim());
            var result = _schema.Compare(leftCols, rightCols);

            foreach (var c in result.OnlyInLeft)
                AddRow("Chỉ có ở trái", c.ColumnName, c.Signature, "-", Color.Firebrick);
            foreach (var c in result.OnlyInRight)
                AddRow("Chỉ có ở phải", c.ColumnName, "-", c.Signature, Color.DarkGreen);
            foreach (var (l, r) in result.Changed)
                AddRow("Khác nhau", l.ColumnName, l.Signature, r.Signature, Color.DarkOrange);
            foreach (var c in result.Unchanged)
                AddRow("Giống nhau", c.ColumnName, c.Signature, c.Signature, Color.Gray);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Compare Structure", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AddRow(string status, string column, string left, string right, Color color)
    {
        var item = new ListViewItem(new[] { status, column, left, right }) { ForeColor = color };
        _resultView.Items.Add(item);
    }
}
