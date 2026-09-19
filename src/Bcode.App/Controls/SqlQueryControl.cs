using System.Data;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// The SELECT / FROM / WHERE / ORDER BY + Run tool (WCommand &gt; Table in FCode).
/// Wraps SqlQueryService, which is what expands a "...$000000" FROM target into a
/// UNION ALL over every real period table.
/// </summary>
public class SqlQueryControl : UserControl
{
    private readonly TextBox _selectBox;
    private readonly TextBox _fromBox;
    private readonly TextBox _whereBox;
    private readonly TextBox _orderByBox;
    private readonly Button _runButton;
    private readonly DataGridView _grid;
    private readonly Label _statusLabel;
    private readonly SqlQueryService _service;
    private readonly GenInsertService _genInsert;
    private readonly GenUpdateService _genUpdate;
    private readonly SqlObjectBrowserService _sqlObjectService;

    public SqlQueryControl(SqlQueryService service, GenInsertService genInsert, GenUpdateService genUpdate, SqlObjectBrowserService sqlObjectService)
    {
        _service = service;
        _genInsert = genInsert;
        _genUpdate = genUpdate;
        _sqlObjectService = sqlObjectService;
        Dock = DockStyle.Fill;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 100, ColumnCount = 4, RowCount = 2 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        _selectBox = LabeledBox(top, "SELECT", 0, "*");
        _fromBox = LabeledBox(top, "FROM", 1, "");
        _whereBox = LabeledBox(top, "WHERE", 0, "", row: 1);
        _orderByBox = LabeledBox(top, "ORDER BY", 1, "", row: 1);

        _runButton = new Button { Text = "▶ Run", Dock = DockStyle.Fill };
        _runButton.Click += async (_, _) => await RunAsync();
        top.Controls.Add(_runButton, 3, 0);
        top.SetRowSpan(_runButton, 2);

        // Enter in any of the 4 boxes runs the query too — matching FCode, instead
        // of forcing a mouse click on ▶ Run every time.
        foreach (var box in new[] { _selectBox, _fromBox, _whereBox, _orderByBox })
        {
            box.KeyDown += async (_, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                e.Handled = true;
                e.SuppressKeyPress = true; // swallow the "ding" a plain TextBox makes on Enter
                await RunAsync();
            };
        }

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };

        var contextMenu = new ContextMenuStrip();
        var genInsertItem = new ToolStripMenuItem("Gen Insert (dòng đã chọn)");
        genInsertItem.Click += (_, _) => GenInsertSelected();
        var genUpdateItem = new ToolStripMenuItem("Gen Update (dòng đã chọn)") { ShortcutKeyDisplayString = "Ctrl+Shift+U" };
        genUpdateItem.Click += (_, _) => GenUpdateSelected();
        contextMenu.Items.Add(genInsertItem);
        contextMenu.Items.Add(genUpdateItem);
        ResultGridMenu.AddItemsTo(contextMenu, _grid);
        _grid.ContextMenuStrip = contextMenu;
        _grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.Shift && e.KeyCode == Keys.U) { e.Handled = true; GenUpdateSelected(); }
        };

        Controls.Add(_grid);
        Controls.Add(_statusLabel);
        Controls.Add(top);

        // Gợi ý tên table/view (autocomplete) cho ô FROM — nạp 1 lần khi tab mở,
        // gộp cả App Data lẫn Sys Data vì FROM có thể tham chiếu bảng ở cả 2.
        _fromBox.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _fromBox.AutoCompleteSource = AutoCompleteSource.CustomSource;
        _fromBox.AutoCompleteCustomSource = new AutoCompleteStringCollection();
        Load += async (_, _) => await LoadFromSuggestionsAsync();
    }

    /// <summary>Populates the FROM box's autocomplete list with every table/view name
    /// (both "schema.name" and bare "name" forms) from App Data + Sys Data. Silent on
    /// failure — this is a convenience, not something that should block/alert the user
    /// if the workspace isn't connected yet.</summary>
    private async Task LoadFromSuggestionsAsync()
    {
        try
        {
            var names = new List<string>();
            foreach (var useSys in new[] { false, true })
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys);
                foreach (var o in objs.Where(o => o.Kind is SqlObjectKind.Table or SqlObjectKind.View))
                {
                    names.Add(o.QualifiedName);
                    names.Add(o.Name);
                }
            }

            var source = new AutoCompleteStringCollection();
            source.AddRange(names.Distinct().ToArray());
            _fromBox.AutoCompleteCustomSource = source;
        }
        catch
        {
            // Chưa kết nối / lỗi tạm thời — bỏ qua, người dùng vẫn gõ FROM tay được như cũ.
        }
    }

    private static TextBox LabeledBox(TableLayoutPanel parent, string label, int col, string defaultValue, int row = 0)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        panel.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        var box = new TextBox { Width = 260, Text = defaultValue };
        panel.Controls.Add(box);
        parent.Controls.Add(panel, col, row);
        return box;
    }

    public void SetFrom(string fromClause) => _fromBox.Text = fromClause;
    public void SetWhere(string whereClause) => _whereBox.Text = whereClause;

    /// <summary>Fired after a successful Run, carrying the result table (consumed e.g. by Create *.xlsx).</summary>
    public event Action<DataTable>? ResultReady;

    private async Task RunAsync()
    {
        _statusLabel.Text = "Đang chạy...";
        _runButton.Enabled = false;
        try
        {
            var table = await _service.RunAsync(_selectBox.Text, _fromBox.Text, _whereBox.Text, _orderByBox.Text);
            _grid.DataSource = table;
            _statusLabel.Text = $"{table.Rows.Count} dòng · SQL đã thực thi: {_service.LastSql.Replace('\n', ' ')}";
            ResultReady?.Invoke(table);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — SQL Query", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _runButton.Enabled = true;
        }
    }

    private void GenInsertSelected()
    {
        if (_grid.DataSource is not DataTable table || _grid.SelectedRows.Count == 0) return;

        var rows = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Where(r => r.DataBoundItem is DataRowView)
            .Select(r => ((DataRowView)r.DataBoundItem!).Row);

        var targetName = Bcode.App.Forms.SimplePromptForm.Show(this, "Gen Insert", "Tên bảng đích cho câu lệnh INSERT:", table.TableName);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        var sql = _genInsert.GenerateInsertStatements(table, targetName, rows);
        Clipboard.SetText(sql);
        MessageBox.Show(this, "Đã sinh câu lệnh INSERT và copy vào clipboard.", "Bcode",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void GenUpdateSelected()
    {
        if (_grid.DataSource is not DataTable table || _grid.SelectedRows.Count == 0) return;

        var rows = _grid.SelectedRows.Cast<DataGridViewRow>()
            .Where(r => r.DataBoundItem is DataRowView)
            .Select(r => ((DataRowView)r.DataBoundItem!).Row);

        var targetName = Bcode.App.Forms.SimplePromptForm.Show(this, "Gen Update", "Tên bảng đích cho câu lệnh UPDATE:", table.TableName);
        if (string.IsNullOrWhiteSpace(targetName)) return;

        var keyInput = Bcode.App.Forms.SimplePromptForm.Show(this, "Gen Update",
            "Cột khoá (key) làm điều kiện WHERE, cách nhau bởi dấu phẩy (vd: stt_rec hoặc ma_ct,ky):",
            table.Columns.Count > 0 ? table.Columns[0].ColumnName : "");
        if (string.IsNullOrWhiteSpace(keyInput)) return;

        var keyColumns = keyInput.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var sql = _genUpdate.GenerateUpdateStatements(table, targetName, keyColumns, rows);
        Clipboard.SetText(sql);
        MessageBox.Show(this, "Đã sinh câu lệnh UPDATE và copy vào clipboard.", "Bcode",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
