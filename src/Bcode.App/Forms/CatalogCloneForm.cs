using System.ComponentModel;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// "Clone danh mục" — khai báo 1 danh mục mới (bảng, khoá, tiêu đề, cột nào hiện ở Grid / form)
/// rồi sinh bộ file Dir / Grid / (Lookup) / Main.aspx từ thư mục mẫu. Danh sách cột lấy từ bảng
/// thật của workspace (cùng cách "Tạo cấu trúc API" liệt kê bảng/cột). Phần sinh file nằm hết
/// trong <see cref="CatalogCloneService"/> — form này chỉ thu thập dữ liệu.
/// </summary>
public class CatalogCloneForm : ThemedForm
{
    private static readonly string[] ToolbarOptions = { "New", "Edit", "Delete", "Clone", "Search", "View", "Export", "Freeze" };

    private readonly SqlObjectBrowserService _sqlObjects;
    private readonly TableDataService _tableData;
    private readonly DbConnectionService _connections;
    private readonly CatalogCloneService _service = new();

    private readonly TextBox _idBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _mainBox = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _tableBox = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly ComboBox _keyBox = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _orderBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _titleVBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _titleEBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _subVBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _subEBox = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _lookupCheck = new() { Text = "Tạo Lookup", AutoSize = true };
    private readonly TextBox _lookupTableBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _templateBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _outputBox = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, CheckBox> _toolbarChecks = new();
    private readonly DataGridView _grid = new();
    private readonly BindingList<CatalogColumn> _columns = new();
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };

    public CatalogCloneForm(SqlObjectBrowserService sqlObjects, TableDataService tableData, DbConnectionService connections)
    {
        _sqlObjects = sqlObjects;
        _tableData = tableData;
        _connections = connections;

        Text = "Clone danh mục";
        Width = 1100;
        Height = 780;
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;

        _templateBox.Text = CatalogCloneService.DefaultTemplateDir;
        _outputBox.Text = DefaultOutputRoot();
        _lookupCheck.CheckedChanged += (_, _) => _lookupTableBox.Enabled = _lookupCheck.Checked;
        _lookupTableBox.Enabled = false;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(BuildGeneralPanel(), 0, 0);
        root.Controls.Add(BuildToolbarPanel(), 0, 1);
        BuildGrid();
        root.Controls.Add(_grid, 0, 2);
        root.Controls.Add(BuildBottomPanel(), 0, 3);
        Controls.Add(root);

        Load += async (_, _) => await LoadTablesAsync();
    }

    private string DefaultOutputRoot()
    {
        var src = _connections.Current?.SourcePath;
        return string.IsNullOrWhiteSpace(src)
            ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
            : Path.Combine(src, "App_Data", "Controllers");
    }

    // ---------------------------------------------------------------- layout

    private Control BuildGeneralPanel()
    {
        var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 5, Padding = new Padding(0, 0, 0, 6) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));

        var loadBtn = PillButton.Flat("Nạp cột", primary: true);
        loadBtn.Click += async (_, _) => await LoadColumnsAsync();
        var browseTemplate = PillButton.Flat("...");
        browseTemplate.Click += (_, _) => PickFolder(_templateBox);
        var browseOutput = PillButton.Flat("...");
        browseOutput.Click += (_, _) => PickFolder(_outputBox);

        void Row(string l1, Control c1, string? l2, Control? c2, Control? extra = null)
        {
            var r = t.RowCount++;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            t.Controls.Add(Lbl(l1), 0, r);
            t.Controls.Add(c1, 1, r);
            if (l2 != null) t.Controls.Add(Lbl(l2), 2, r);
            if (c2 != null) t.Controls.Add(c2, 3, r);
            if (extra != null) t.Controls.Add(extra, 4, r);
        }

        Row("Id (tên file)", _idBox, "File Main (.aspx)", _mainBox);
        Row("Table", _tableBox, "Key", _keyBox, loadBtn);
        Row("Order", _orderBox, null, null);
        Row("Title (v)", _titleVBox, "Title (e)", _titleEBox);
        Row("Subtitle (v)", _subVBox, "Subtitle (e)", _subEBox);
        Row("Lookup", _lookupCheck, "Lookup table", _lookupTableBox);
        Row("Thư mục mẫu", _templateBox, null, null, browseTemplate);
        Row("Lưu vào", _outputBox, null, null, browseOutput);
        // Hai ô đường dẫn trải dài hết 3 cột giữa
        foreach (var box in new Control[] { _templateBox, _outputBox }) t.SetColumnSpan(box, 3);

        _tableBox.SelectedIndexChanged += (_, _) => OnTableChosen();
        _keyBox.TextChanged += (_, _) => { if (string.IsNullOrWhiteSpace(_orderBox.Text) || _orderBox.Tag as string == "auto") { _orderBox.Text = _keyBox.Text; _orderBox.Tag = "auto"; } ApplyKey(); };
        _orderBox.KeyPress += (_, _) => _orderBox.Tag = "manual";
        return t;
    }

    private Control BuildToolbarPanel()
    {
        var f = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 6) };
        f.Controls.Add(new Label { Text = "Toolbar:", AutoSize = true, Padding = new Padding(0, 4, 8, 0), Font = new Font(Font, FontStyle.Bold) });
        foreach (var name in ToolbarOptions)
        {
            var cb = new CheckBox { Text = name, AutoSize = true, Checked = true };
            _toolbarChecks[name] = cb;
            f.Controls.Add(cb);
        }
        return f;
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.RowHeadersVisible = false;
        _grid.DataSource = _columns;

        DataGridViewColumn Check(string prop, string head, int w) => new DataGridViewCheckBoxColumn { DataPropertyName = prop, HeaderText = head, Width = w };
        DataGridViewColumn Text(string prop, string head, int w, bool ro = false) => new DataGridViewTextBoxColumn { DataPropertyName = prop, HeaderText = head, Width = w, ReadOnly = ro };

        _grid.Columns.Add(Check(nameof(CatalogColumn.InGrid), "Grid", 50));
        _grid.Columns.Add(Check(nameof(CatalogColumn.InForm), "Form", 50));
        _grid.Columns.Add(Text(nameof(CatalogColumn.Name), "Name", 160, ro: true));
        _grid.Columns.Add(Text(nameof(CatalogColumn.HeaderV), "Header (v)", 200));
        _grid.Columns.Add(Text(nameof(CatalogColumn.HeaderE), "Header (e)", 200));
        _grid.Columns.Add(Text(nameof(CatalogColumn.Width), "Width", 60));
        var type = new DataGridViewComboBoxColumn { DataPropertyName = nameof(CatalogColumn.Type), HeaderText = "type", Width = 90, FlatStyle = FlatStyle.Flat };
        type.Items.AddRange("", "Boolean", "Decimal", "DateTime");
        _grid.Columns.Add(type);
        _grid.Columns.Add(Check(nameof(CatalogColumn.AllowNulls), "AllowNulls", 80));
        _grid.Columns.Add(Check(nameof(CatalogColumn.ReadOnly), "ReadOnly", 80));
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.DataError += (_, e) => e.ThrowException = false;
    }

    private Control BuildBottomPanel()
    {
        var p = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        p.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        p.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var gen = PillButton.Flat("Tạo file", primary: true);
        gen.Click += (_, _) => Generate();
        var close = PillButton.Flat("Đóng");
        close.Click += (_, _) => Close();
        p.Controls.Add(_status, 0, 0);
        p.Controls.Add(gen, 1, 0);
        p.Controls.Add(close, 2, 0);
        return p;
    }

    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 0, 6) };

    private static void PickFolder(TextBox target)
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(target.Text) ? target.Text : "" };
        if (dlg.ShowDialog() == DialogResult.OK) target.Text = dlg.SelectedPath;
    }

    // ---------------------------------------------------------------- data

    private async Task LoadTablesAsync()
    {
        if (_connections.Current is null) { _status.Text = "Chưa chọn workspace."; return; }
        try
        {
            var objs = await _sqlObjects.ListObjectsAsync(useSysDatabase: false);
            var names = objs.Where(o => o.Kind is SqlObjectKind.Table or SqlObjectKind.View).Select(o => o.Name).OrderBy(n => n).ToArray();
            _tableBox.Items.AddRange(names);
            _status.Text = $"{names.Length} bảng/view trong App Data.";
        }
        catch (Exception ex) { _status.Text = "Không nạp được danh sách bảng: " + ex.Message; }
    }

    private async void OnTableChosen()
    {
        if (!string.IsNullOrWhiteSpace(_tableBox.Text)) await LoadColumnsAsync();
    }

    private async Task LoadColumnsAsync()
    {
        var table = _tableBox.Text.Trim();
        if (table.Length == 0) { _status.Text = "Chọn bảng trước."; return; }
        try
        {
            var cols = await _sqlObjects.GetColumnsAsync(false, "dbo", table);
            if (cols.Count == 0) { _status.Text = $"Không thấy cột nào của bảng '{table}'."; return; }
            var types = await _tableData.GetColumnTypesAsync(false, "dbo", table);

            _columns.Clear();
            foreach (var (name, isPk) in cols)
            {
                types.TryGetValue(name, out var sqlType);
                _columns.Add(new CatalogColumn
                {
                    Name = name,
                    HeaderV = name,
                    HeaderE = name,
                    IsKey = isPk,
                    Type = CatalogCloneService.GuessFieldType(sqlType ?? ""),
                });
            }

            _keyBox.Items.Clear();
            _keyBox.Items.AddRange(cols.Select(c => (object)c.Name).ToArray());
            _keyBox.Text = cols.FirstOrDefault(c => c.IsPrimaryKey).Name ?? cols[0].Name;
            if (string.IsNullOrWhiteSpace(_lookupTableBox.Text)) _lookupTableBox.Text = table;
            if (string.IsNullOrWhiteSpace(_idBox.Text)) _idBox.Text = table;
            _status.Text = $"Đã nạp {cols.Count} cột của '{table}'.";
        }
        catch (Exception ex) { _status.Text = "Không nạp được cột: " + ex.Message; }
    }

    private void ApplyKey()
    {
        foreach (var c in _columns) c.IsKey = c.Name.Equals(_keyBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- generate

    private void Generate()
    {
        _grid.EndEdit();
        ApplyKey();

        var spec = new CatalogSpec
        {
            Id = _idBox.Text.Trim(),
            Table = _tableBox.Text.Trim(),
            Key = _keyBox.Text.Trim(),
            Order = _orderBox.Text.Trim(),
            TitleV = _titleVBox.Text.Trim(),
            TitleE = _titleEBox.Text.Trim(),
            SubTitleV = _subVBox.Text.Trim(),
            SubTitleE = _subEBox.Text.Trim(),
            MainName = _mainBox.Text.Trim(),
            CreateLookup = _lookupCheck.Checked,
            LookupTable = _lookupTableBox.Text.Trim(),
            ToolbarCommands = ToolbarOptions.Where(n => _toolbarChecks[n].Checked).ToList(),
            Columns = _columns.ToList(),
        };

        var error = CatalogCloneService.Validate(spec);
        if (error != null) { MessageBox.Show(this, error, "Clone danh mục", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        var output = _outputBox.Text.Trim();
        if (output.Length == 0) { MessageBox.Show(this, "Chưa chọn thư mục lưu.", "Clone danh mục", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        var existing = _service.PlannedFiles(spec, output).Where(f => File.Exists(f.Path)).Select(f => f.Path).ToList();
        if (existing.Count > 0)
        {
            var ask = MessageBox.Show(this, "Các file sau đã tồn tại, ghi đè?\n\n" + string.Join("\n", existing),
                "Xác nhận ghi đè", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (ask != DialogResult.Yes) return;
        }

        try
        {
            var written = _service.Generate(spec, _templateBox.Text.Trim(), output);
            _status.Text = $"Đã tạo {written.Count} file.";
            MessageBox.Show(this, "Đã tạo:\n\n" + string.Join("\n", written), "Clone danh mục", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không tạo được file:\n" + ex.Message, "Clone danh mục", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
