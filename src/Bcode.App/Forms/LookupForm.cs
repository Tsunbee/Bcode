using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Lookup" tool — quick "find the row(s) matching this key" search: pick a
/// table + column, type a value, see matching rows immediately. Faster than
/// opening "Table" or writing a query in "SQL Query"/"Command" just to check
/// one value.
/// </summary>
public class LookupForm : Bcode.App.UI.ThemedForm
{
    private readonly ComboBox _dbCombo;
    private readonly TextBox _tableBox;
    private readonly ComboBox _columnCombo;
    private readonly TextBox _valueBox;
    private readonly CheckBox _exactCheck;
    private readonly WebActionBar _actions;
    private readonly DataGridView _grid;
    private readonly LookupService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;

    public LookupForm(LookupService service, SqlObjectBrowserService sqlObjectService)
    {
        _service = service;
        _sqlObjectService = sqlObjectService;

        Text = "Lookup";
        Width = 760;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;

        var top = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6)};
        for (var i = 0; i < 4; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _dbCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        _dbCombo.Items.AddRange(new object[] { "App Data", "Sys Data" });
        _dbCombo.SelectedIndex = 0;

        _tableBox = new TextBox { Width = 180, PlaceholderText = "dbo.tenbang" };
        _tableBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _tableBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
        _tableBox.AutoCompleteCustomSource = new AutoCompleteStringCollection();
        _tableBox.Leave += async (_, _) => await LoadColumnsAsync();

        _columnCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 140 };
        _valueBox = new TextBox { Width = 180 };
        _valueBox.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; await SearchAsync(); } };
        _exactCheck = new CheckBox { Text = "Đúng tuyệt đối (=)", AutoSize = true, Checked = false };

        top.Controls.Add(Labeled("DB", _dbCombo), 0, 0);
        top.Controls.Add(Labeled("Table", _tableBox), 1, 0);
        top.Controls.Add(Labeled("Column", _columnCombo), 2, 0);
        top.Controls.Add(Labeled("Value", _valueBox), 3, 0);
        top.Controls.Add(_exactCheck, 4, 0);

        // Search button + result count used to be two full-width strips stacked above the
        // grid, eating 48px of vertical space before a single row was shown. Both now live
        // in the bottom action bar (Controls/WebActionBar.cs).
        _actions = new WebActionBar { DefaultActionId = "search", CancelActionId = "close" };
        _actions.Add("close", "Đóng", WebActionKind.Quiet)
                .Add("search", "🔍 Search", WebActionKind.Primary);
        _actions.Invoked += async id =>
        {
            if (id == "search") await SearchAsync();
            else Close();
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };

        Controls.Add(_grid);
        Controls.Add(_actions);
        Controls.Add(top);

        Load += async (_, _) => await LoadTableSuggestionsAsync();
    }

    private static Control Labeled(string label, Control input)
    {
        var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown };
        panel.Controls.Add(new Label { Text = label, AutoSize = true });
        panel.Controls.Add(input);
        return panel;
    }

    private async Task LoadTableSuggestionsAsync()
    {
        try
        {
            var names = new List<string>();
            foreach (var useSys in new[] { false, true })
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys);
                foreach (var o in objs.Where(o => o.Kind == SqlObjectKind.Table))
                {
                    names.Add(o.QualifiedName);
                    names.Add(o.Name);
                }
            }
            var source = new AutoCompleteStringCollection();
            source.AddRange(names.Distinct().ToArray());
            _tableBox.AutoCompleteCustomSource = source;
        }
        catch { /* chưa kết nối */ }
    }

    private (string schema, string table) ParseTableRef(string raw)
    {
        raw = raw.Trim().Replace("[", "").Replace("]", "");
        var parts = raw.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    private async Task LoadColumnsAsync()
    {
        if (string.IsNullOrWhiteSpace(_tableBox.Text)) return;
        try
        {
            var (schema, table) = ParseTableRef(_tableBox.Text);
            var cols = await _service.GetColumnsAsync(_dbCombo.SelectedIndex == 1, schema, table);
            _columnCombo.Items.Clear();
            _columnCombo.Items.AddRange(cols.Cast<object>().ToArray());
            if (cols.Count > 0) _columnCombo.SelectedIndex = 0;
        }
        catch { /* để trống, người dùng có thể tự gõ tên cột */ }
    }

    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(_tableBox.Text) || string.IsNullOrWhiteSpace(_columnCombo.Text))
        {
            _actions.SetStatus("Chọn Table và Column trước.", ok: false);
            return;
        }

        var (schema, table) = ParseTableRef(_tableBox.Text);
        _actions.SetStatus("Đang tìm...");
        _actions.SetEnabled("search", false);
        try
        {
            var result = await _service.SearchAsync(_dbCombo.SelectedIndex == 1, schema, table, _columnCombo.Text, _valueBox.Text, _exactCheck.Checked);
            _grid.DataSource = result;
            _actions.SetStatus($"{result.Rows.Count} dòng khớp (tối đa 200).", ok: true);
        }
        catch (Exception ex)
        {
            _actions.SetStatus("Lỗi.", ok: false);
            MessageBox.Show(this, ex.Message, "Bcode — Lookup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _actions.SetEnabled("search", true);
        }
    }
}
