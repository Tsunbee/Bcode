using System.Data;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Table" tool — open one table as a live, directly-editable grid (like
/// opening a table in Excel), as opposed to "SQL Query"/"Command" where you
/// have to write SELECT yourself. Add/edit/delete rows in the grid, then
/// "Save" writes the real INSERT/UPDATE/DELETE statements back.
/// </summary>
public class TableEditControl : UserControl
{
    private readonly ComboBox _dbCombo;
    private readonly TextBox _tableBox;
    private readonly TextBox _topBox;
    private readonly Button _loadButton;
    private readonly Button _saveButton;
    private readonly Label _keyLabel;
    private readonly DataGridView _grid;
    private readonly Label _statusLabel;
    private readonly TableDataService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;

    private string _schema = "dbo";
    private string _table = "";
    private List<string> _keyColumns = new();

    public TableEditControl(TableDataService service, SqlObjectBrowserService sqlObjectService)
    {
        _service = service;
        _sqlObjectService = sqlObjectService;
        Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, Padding = new Padding(4, 4, 0, 0) };
        _dbCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        _dbCombo.Items.AddRange(new object[] { "App Data", "Sys Data" });
        _dbCombo.SelectedIndex = 0;
        _tableBox = new TextBox { Width = 220, PlaceholderText = "dbo.tenbang hoặc tenbang" };
        _tableBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _tableBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
        _tableBox.AutoCompleteCustomSource = new AutoCompleteStringCollection();
        _tableBox.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; await LoadAsync(); } };
        _topBox = new TextBox { Width = 60, Text = "500" };
        _loadButton = new Button { Text = "Load" };
        _loadButton.Click += async (_, _) => await LoadAsync();
        _saveButton = new Button { Text = "💾 Save (ghi vào DB)", Enabled = false };
        _saveButton.Click += async (_, _) => await SaveAsync();

        top.Controls.Add(new Label { Text = "DB:", AutoSize = true, Padding = new Padding(0, 6, 2, 0) });
        top.Controls.Add(_dbCombo);
        top.Controls.Add(new Label { Text = "Table:", AutoSize = true, Padding = new Padding(6, 6, 2, 0) });
        top.Controls.Add(_tableBox);
        top.Controls.Add(new Label { Text = "Top:", AutoSize = true, Padding = new Padding(6, 6, 2, 0) });
        top.Controls.Add(_topBox);
        top.Controls.Add(_loadButton);
        top.Controls.Add(_saveButton);

        _keyLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };
        _statusLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = true,
            AllowUserToDeleteRows = true,
            ReadOnly = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            SelectionMode = DataGridViewSelectionMode.CellSelect
        };
        _grid.CellValueChanged += (_, _) => _saveButton.Enabled = true;
        _grid.UserAddedRow += (_, _) => _saveButton.Enabled = true;
        _grid.UserDeletedRow += (_, _) => _saveButton.Enabled = true;

        Controls.Add(_grid);
        Controls.Add(_keyLabel);
        Controls.Add(_statusLabel);
        Controls.Add(top);

        Load += async (_, _) => await LoadTableSuggestionsAsync();
    }

    /// <summary>Preselect a table (e.g. from the SQL Object tree's right-click menu) and load it.</summary>
    public async Task OpenTableAsync(bool useSysDatabase, string schema, string table)
    {
        _dbCombo.SelectedIndex = useSysDatabase ? 1 : 0;
        _tableBox.Text = table.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? table : $"{schema}.{table}";
        await LoadAsync();
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
        catch { /* chưa kết nối — gõ tay vẫn được */ }
    }

    private (string schema, string table) ParseTableRef(string raw)
    {
        raw = raw.Trim().Trim('[', ']');
        var parts = raw.Replace("[", "").Replace("]", "").Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_tableBox.Text)) return;
        (_schema, _table) = ParseTableRef(_tableBox.Text);
        var useSys = _dbCombo.SelectedIndex == 1;
        var topN = int.TryParse(_topBox.Text, out var n) ? n : 500;

        _statusLabel.Text = "Đang tải...";
        _loadButton.Enabled = false;
        try
        {
            var data = await _service.LoadTableAsync(useSys, _schema, _table, topN);
            var isPeriodPlaceholder = _service.IsPeriodPlaceholder(_schema, _table);

            if (isPeriodPlaceholder)
            {
                _keyColumns = new List<string>();
                _keyLabel.Text = "ℹ [Schema].[Table]$000000 = gộp TẤT CẢ các bảng phân kỳ (UNION ALL), giống SQL Query/Command — " +
                                  "đây không phải 1 bảng vật lý nên KHÔNG Save được ở đây; chỉnh sửa qua Command/SQL Query rồi nhắm đúng bảng kỳ cụ thể.";
            }
            else
            {
                _keyColumns = await _service.GetPrimaryKeyColumnsAsync(useSys, _schema, _table);
                _keyLabel.Text = _keyColumns.Count > 0
                    ? $"Primary Key: {string.Join(", ", _keyColumns)}"
                    : "⚠ Bảng này không có Primary Key — Save sẽ báo lỗi trừ khi bạn tự set khoá (chưa hỗ trợ chọn tay ở bản này, hãy chỉnh sửa qua Command/SQL Query thay vì Table).";
            }

            _grid.DataSource = data;
            _grid.ReadOnly = isPeriodPlaceholder;
            _saveButton.Enabled = false;
            _statusLabel.Text = $"{data.Rows.Count} dòng đã tải ([{_schema}].[{_table}])" +
                                 (isPeriodPlaceholder ? " — gộp mọi kỳ, chỉ xem, không Save được." : ".");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _loadButton.Enabled = true;
        }
    }

    private async Task SaveAsync()
    {
        if (_grid.DataSource is not DataTable data) return;
        var useSys = _dbCombo.SelectedIndex == 1;

        if (_service.IsPeriodPlaceholder(_schema, _table))
        {
            MessageBox.Show(this, "[Schema].[Table]$000000 là kết quả gộp TẤT CẢ bảng phân kỳ (UNION ALL), không phải 1 bảng vật lý — " +
                "không Save được ở đây. Dùng Command/SQL Query và nhắm đúng bảng kỳ cụ thể (vd r00$202601) để sửa dữ liệu.",
                "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_keyColumns.Count == 0)
        {
            MessageBox.Show(this, "Bảng không có Primary Key nên Table chưa lưu được an toàn. Dùng tool Command để tự viết UPDATE/INSERT/DELETE cho bảng này.",
                "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(this, $"Ghi thay đổi trực tiếp vào [{_schema}].[{_table}]?", "Bcode — Table",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
        if (confirm != DialogResult.Yes) return;

        _saveButton.Enabled = false;
        try
        {
            var count = await _service.SaveChangesAsync(useSys, _schema, _table, _keyColumns, data);
            _statusLabel.Text = $"Đã lưu {count} thay đổi.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Table", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _saveButton.Enabled = true;
        }
    }
}
