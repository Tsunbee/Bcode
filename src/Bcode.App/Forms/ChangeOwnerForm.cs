using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Change Owner" tool — moves a SQL object to a different schema (the
/// modern SQL Server equivalent of the old "change owner" concept; see
/// ChangeOwnerService for why this runs ALTER SCHEMA ... TRANSFER).
/// </summary>
public class ChangeOwnerForm : Bcode.App.UI.ThemedForm
{
    private readonly ComboBox _dbCombo;
    private readonly TextBox _objectBox;
    private readonly TextBox _newSchemaBox;
    private readonly Button _runButton;
    private readonly Label _statusLabel;
    private readonly ChangeOwnerService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;

    public ChangeOwnerForm(ChangeOwnerService service, SqlObjectBrowserService sqlObjectService, string? preselectedObject = null)
    {
        _service = service;
        _sqlObjectService = sqlObjectService;

        Text = "Change Owner";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Width = 460;
        Height = 260;
        StartPosition = FormStartPosition.CenterParent;

        var form = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _dbCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 300 };
        _dbCombo.Items.AddRange(new object[] { "App Data", "Sys Data" });
        _dbCombo.SelectedIndex = 0;

        _objectBox = new TextBox { Width = 300, Text = preselectedObject ?? "", PlaceholderText = "dbo.tenobject" };
        _objectBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _objectBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
        _objectBox.AutoCompleteCustomSource = new AutoCompleteStringCollection();

        _newSchemaBox = new TextBox { Width = 300, PlaceholderText = "schema mới, vd: dbo" };

        AddRow(form, "Database", _dbCombo);
        AddRow(form, "Object (schema.name)", _objectBox);
        AddRow(form, "Schema mới", _newSchemaBox);

        _runButton = new Button { Text = "Đổi Owner (ALTER SCHEMA TRANSFER)", AutoSize = true };
        _runButton.Click += async (_, _) => await RunAsync();
        form.Controls.Add(new Panel(), 0, form.RowCount);
        form.Controls.Add(_runButton, 1, form.RowCount - 1);
        form.RowCount++;

        _statusLabel = new Label { AutoSize = true, MaximumSize = new Size(400, 0) };
        form.Controls.Add(new Panel(), 0, form.RowCount);
        form.Controls.Add(_statusLabel, 1, form.RowCount - 1);

        Controls.Add(form);
        Load += async (_, _) => await LoadSuggestionsAsync();
    }

    private static void AddRow(TableLayoutPanel form, string label, Control input)
    {
        var row = form.RowCount;
        form.RowCount = row + 1;
        form.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }, 0, row);
        form.Controls.Add(input, 1, row);
    }

    private async Task LoadSuggestionsAsync()
    {
        try
        {
            var names = new List<string>();
            foreach (var useSys in new[] { false, true })
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys);
                names.AddRange(objs.Select(o => o.QualifiedName));
            }
            var source = new AutoCompleteStringCollection();
            source.AddRange(names.Distinct().ToArray());
            _objectBox.AutoCompleteCustomSource = source;
        }
        catch { /* chưa kết nối */ }
    }

    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(_objectBox.Text) || string.IsNullOrWhiteSpace(_newSchemaBox.Text))
        {
            _statusLabel.Text = "Nhập Object và Schema mới.";
            return;
        }

        var raw = _objectBox.Text.Trim().Replace("[", "").Replace("]", "");
        var parts = raw.Split('.', 2);
        var (schema, name) = parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);

        var confirm = MessageBox.Show(this,
            $"Chuyển [{schema}].[{name}] sang schema [{_newSchemaBox.Text.Trim()}]?\nMọi code/view/proc tham chiếu object này bằng tên schema cũ sẽ phải sửa lại.",
            "Bcode — Change Owner", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _runButton.Enabled = false;
        _statusLabel.ForeColor = SystemColors.ControlText;
        _statusLabel.Text = "Đang chạy...";
        try
        {
            await _service.ChangeOwnerAsync(_dbCombo.SelectedIndex == 1, schema, name, _newSchemaBox.Text.Trim());
            _statusLabel.ForeColor = Color.DarkGreen;
            _statusLabel.Text = "Đã đổi owner (schema) thành công.";
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = ex.Message;
        }
        finally
        {
            _runButton.Enabled = true;
        }
    }
}
