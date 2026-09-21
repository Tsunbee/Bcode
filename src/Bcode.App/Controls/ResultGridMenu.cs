using System.Data;
using System.Text;
using Bcode.App.Forms;

namespace Bcode.App.Controls;

/// <summary>
/// Right-click menu for a "Result" grid (SQL Query / Command / any grid opened via
/// "Result Tab") — matches FCode's own result-grid menu from the user's screenshot:
/// Goto Column, Copy selected/all Column Name(s), Filter, Add Index Column Order,
/// Generate Design Fields, Maxlength Column Content, Compare Column Content, Set Color
/// Cell. Attach once per grid via <see cref="Attach"/>.
///
/// Everything here works off the grid's bound DataTable (grid.DataSource as DataTable);
/// actions that need one (Filter, Add Index Column Order, Generate Design Fields,
/// Maxlength Column Content) no-op with a message if the grid isn't bound to one.
/// </summary>
public static class ResultGridMenu
{
    /// <summary>Wires the grid's right-click to a menu containing just these items — use for
    /// a grid that has no menu of its own yet.</summary>
    public static void Attach(DataGridView grid)
    {
        WebMenu.AttachTo(grid, () => AddItemsTo(new WebMenu(), grid));
        WireShortcuts(grid);
    }

    /// <summary>Appends these items to a menu a host is already building (e.g.
    /// SqlQueryControl's grid adds its own Gen Insert/Gen Update first) — use this so the two
    /// menus merge rather than one silently replacing the other. Call
    /// <see cref="WireShortcuts"/> once per grid alongside it.</summary>
    public static WebMenu AddItemsTo(WebMenu menu, DataGridView grid)
    {
        if (!menu.IsEmpty) menu.AddSeparator();

        menu.AddCaption("Cột");
        menu.Add("Goto Column ...", () => GotoColumn(grid), shortcut: "Ctrl+G");
        menu.Add("Copy selected Column Name ...", () => CopySelectedColumnNames(grid));
        menu.Add("Copy All Column Name ...", () => CopyAllColumnNames(grid));

        menu.AddCaption("Dữ liệu");
        menu.Add("Filter", () => ShowFilterDialog(grid));
        menu.Add("Add Index Column Order", () => AddIndexColumnOrder(grid));
        menu.Add("Generate Design Fields", () => GenerateDesignFields(grid));
        menu.Add("Maxlength Column Content", () => ShowMaxlength(grid));
        menu.Add("Compare Column Content", () => CompareColumnContent(grid));

        // "Set Color Cell" was a submenu; with an HTML menu the five colors are cheaper to
        // show inline under a caption than to hide behind another hover-and-wait level.
        menu.AddCaption("Set Color Cell");
        menu.Add("Không màu (xoá màu)", () => SetColorCell(grid, null));
        menu.Add("Xanh lá", () => SetColorCell(grid, Color.FromArgb(198, 239, 206)));
        menu.Add("Xanh dương", () => SetColorCell(grid, Color.FromArgb(189, 215, 238)));
        menu.Add("Tím", () => SetColorCell(grid, Color.FromArgb(204, 192, 218)));
        menu.Add("Đỏ", () => SetColorCell(grid, Color.FromArgb(255, 199, 206)));
        return menu;
    }

    /// <summary>Ctrl+G (Goto Column) — separate from the menu itself because the menu is now
    /// rebuilt on every right-click, and this must be wired exactly once per grid.</summary>
    public static void WireShortcuts(DataGridView grid)
    {
        grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.G) { e.Handled = true; GotoColumn(grid); }
        };
    }

    private static DataTable? GetTable(DataGridView grid) => grid.DataSource as DataTable;
    private static IWin32Window? Owner(DataGridView grid) => grid.FindForm();

    private static void GotoColumn(DataGridView grid)
    {
        if (grid.Columns.Count == 0) return;
        var input = SimplePromptForm.Show(Owner(grid) ?? grid, "Goto Column", "Tên cột (hoặc số thứ tự cột, bắt đầu từ 1):", "");
        if (string.IsNullOrWhiteSpace(input)) return;

        DataGridViewColumn? col = null;
        if (int.TryParse(input, out var idx) && idx >= 1 && idx <= grid.Columns.Count)
            col = grid.Columns[idx - 1];
        else
            col = grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.Name.Equals(input, StringComparison.OrdinalIgnoreCase))
                  ?? grid.Columns.Cast<DataGridViewColumn>().FirstOrDefault(c => c.Name.StartsWith(input, StringComparison.OrdinalIgnoreCase));

        if (col is null)
        {
            MessageBox.Show(Owner(grid), $"Không tìm thấy cột \"{input}\".", "Bcode — Goto Column");
            return;
        }

        grid.FirstDisplayedScrollingColumnIndex = col.Index;
        if (grid.Rows.Count > 0)
        {
            var rowIndex = Math.Max(grid.CurrentCell?.RowIndex ?? 0, 0);
            grid.ClearSelection();
            grid.CurrentCell = grid.Rows[rowIndex].Cells[col.Index];
            grid.Rows[rowIndex].Cells[col.Index].Selected = true;
        }
    }

    private static void CopySelectedColumnNames(DataGridView grid)
    {
        var names = grid.SelectedCells.Cast<DataGridViewCell>()
            .Select(c => grid.Columns[c.ColumnIndex].Name)
            .Distinct()
            .ToList();
        if (names.Count == 0)
        {
            MessageBox.Show(Owner(grid), "Chưa chọn ô/cột nào.", "Bcode — Copy Column Name");
            return;
        }
        Clipboard.SetText(string.Join(", ", names));
    }

    private static void CopyAllColumnNames(DataGridView grid)
    {
        var names = grid.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex).Select(c => c.Name);
        Clipboard.SetText(string.Join(", ", names));
    }

    private static void ShowFilterDialog(DataGridView grid)
    {
        if (GetTable(grid) is not { } table)
        {
            MessageBox.Show(Owner(grid), "Kết quả này không hỗ trợ Filter (không phải dữ liệu dạng bảng).", "Bcode — Filter");
            return;
        }

        using var form = new ColumnFilterForm(table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList());
        if (form.ShowDialog(Owner(grid)) != DialogResult.OK) return;

        try { table.DefaultView.RowFilter = form.FilterExpression; }
        catch (Exception ex) { MessageBox.Show(Owner(grid), "Biểu thức lọc không hợp lệ: " + ex.Message, "Bcode — Filter"); }
    }

    private static void AddIndexColumnOrder(DataGridView grid)
    {
        var table = GetTable(grid);
        var selectedCols = grid.SelectedCells.Cast<DataGridViewCell>()
            .Select(c => grid.Columns[c.ColumnIndex])
            .Distinct()
            .OrderBy(c => c.DisplayIndex)
            .Select(c => c.Name)
            .ToList();
        var cols = selectedCols.Count > 0
            ? selectedCols
            : grid.Columns.Cast<DataGridViewColumn>().OrderBy(c => c.DisplayIndex).Select(c => c.Name).ToList();
        if (cols.Count == 0) return;

        var tableName = SimplePromptForm.Show(Owner(grid) ?? grid, "Add Index Column Order", "Tên bảng cần tạo Index:", table?.TableName ?? "");
        if (string.IsNullOrWhiteSpace(tableName)) return;

        var indexName = $"IX_{tableName}_{string.Join("_", cols.Take(3))}";
        var sql = $"CREATE INDEX [{indexName}] ON [dbo].[{tableName}] (\n    {string.Join(",\n    ", cols.Select(c => $"[{c}] ASC"))}\n);";
        Clipboard.SetText(sql);
        MessageBox.Show(Owner(grid), "Đã sinh CREATE INDEX theo thứ tự cột đã chọn (hoặc toàn bộ nếu chưa chọn ô nào) và copy vào clipboard:\n\n" + sql,
            "Bcode — Add Index Column Order", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void GenerateDesignFields(DataGridView grid)
    {
        if (GetTable(grid) is not { } table)
        {
            MessageBox.Show(Owner(grid), "Không có dữ liệu để sinh Design Fields.", "Bcode — Generate Design Fields");
            return;
        }

        var lines = table.Columns.Cast<DataColumn>().Select(c => $"[{c.ColumnName}] {GuessSqlType(c)} {(c.AllowDBNull ? "NULL" : "NOT NULL")}");
        var script = string.Join(",\n", lines);
        Clipboard.SetText(script);
        MessageBox.Show(Owner(grid), "Đã sinh danh sách thiết kế trường (kiểu dữ liệu suy đoán từ dữ liệu trả về) và copy vào clipboard:\n\n" + script,
            "Bcode — Generate Design Fields", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string GuessSqlType(DataColumn c)
    {
        if (c.DataType == typeof(int)) return "INT";
        if (c.DataType == typeof(long)) return "BIGINT";
        if (c.DataType == typeof(short)) return "SMALLINT";
        if (c.DataType == typeof(byte)) return "TINYINT";
        if (c.DataType == typeof(decimal)) return "DECIMAL(18,2)";
        if (c.DataType == typeof(double) || c.DataType == typeof(float)) return "FLOAT";
        if (c.DataType == typeof(bool)) return "BIT";
        if (c.DataType == typeof(DateTime)) return "DATETIME";
        if (c.DataType == typeof(Guid)) return "UNIQUEIDENTIFIER";
        if (c.DataType == typeof(byte[])) return "VARBINARY(MAX)";
        if (c.DataType == typeof(string))
        {
            var maxLen = 0;
            foreach (DataRow row in c.Table!.Rows)
                if (row[c] is string s) maxLen = Math.Max(maxLen, s.Length);
            var size = maxLen <= 0 ? 50 : Math.Min(4000, maxLen + Math.Max(10, maxLen / 5));
            return $"NVARCHAR({size})";
        }
        return "NVARCHAR(255)";
    }

    private static void ShowMaxlength(DataGridView grid)
    {
        if (GetTable(grid) is not { } table)
        {
            MessageBox.Show(Owner(grid), "Không có dữ liệu.", "Bcode — Maxlength Column Content");
            return;
        }

        var sb = new StringBuilder();
        foreach (DataColumn col in table.Columns)
        {
            var maxLen = 0;
            foreach (DataRow row in table.Rows)
            {
                var v = row[col];
                var len = v is DBNull ? 0 : v?.ToString()?.Length ?? 0;
                if (len > maxLen) maxLen = len;
            }
            sb.AppendLine($"{col.ColumnName}: {maxLen}");
        }

        Clipboard.SetText(sb.ToString());
        MessageBox.Show(Owner(grid), "Độ dài nội dung lớn nhất từng cột (đã copy vào clipboard):\n\n" + sb,
            "Bcode — Maxlength Column Content", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void CompareColumnContent(DataGridView grid)
    {
        if (GetTable(grid) is not { } table || table.Columns.Count < 2)
        {
            MessageBox.Show(Owner(grid), "Cần dữ liệu dạng bảng với ít nhất 2 cột để so sánh.", "Bcode — Compare Column Content");
            return;
        }

        var colNames = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
        var colA = SimplePromptForm.Show(Owner(grid) ?? grid, "Compare Column Content", $"Cột thứ nhất (một trong: {string.Join(", ", colNames)}):", colNames[0]);
        if (string.IsNullOrWhiteSpace(colA) || !table.Columns.Contains(colA)) return;
        var colB = SimplePromptForm.Show(Owner(grid) ?? grid, "Compare Column Content", "Cột thứ hai:", colNames.Count > 1 ? colNames[1] : colNames[0]);
        if (string.IsNullOrWhiteSpace(colB) || !table.Columns.Contains(colB)) return;

        grid.ClearSelection();
        var diffCount = 0;
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.DataBoundItem is not DataRowView drv) continue;
            var a = drv.Row[colA]?.ToString() ?? "";
            var b = drv.Row[colB]?.ToString() ?? "";
            if (string.Equals(a, b, StringComparison.Ordinal)) continue;

            row.Cells[colA].Selected = true;
            row.Cells[colB].Selected = true;
            diffCount++;
        }

        MessageBox.Show(Owner(grid), $"{diffCount} dòng có nội dung khác nhau giữa \"{colA}\" và \"{colB}\" (đã bôi chọn các ô khác nhau).",
            "Bcode — Compare Column Content", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void SetColorCell(DataGridView grid, Color? color)
    {
        if (grid.SelectedCells.Count == 0) return;
        foreach (DataGridViewCell cell in grid.SelectedCells)
        {
            cell.Style.BackColor = color ?? Color.Empty; // Color.Empty = unset, inherits the row/grid default again
            // Set Color Cell's swatches are always light/pastel (for readability against
            // black text in both themes) — force black text on them, and clear it back to
            // the grid's own themed ForeColor when clearing the color.
            cell.Style.ForeColor = color is null ? Color.Empty : Color.Black;
        }
        grid.Invalidate();
    }
}

/// <summary>Small "Filter" dialog for ResultGridMenu — column + operator + value, building
/// a DataView.RowFilter expression (System.Data's DataTable expression syntax).</summary>
internal sealed class ColumnFilterForm : Bcode.App.UI.ThemedForm
{
    private readonly ComboBox _columnCombo;
    private readonly ComboBox _opCombo;
    private readonly TextBox _valueBox;

    public string FilterExpression { get; private set; } = "";

    public ColumnFilterForm(List<string> columns)
    {
        Text = "Filter";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        Width = 440;
        Height = 220;
        StartPosition = FormStartPosition.CenterParent;

        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _columnCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        _columnCombo.Items.AddRange(columns.Cast<object>().ToArray());
        if (columns.Count > 0) _columnCombo.SelectedIndex = 0;

        _valueBox = new TextBox { Width = 260 };

        _opCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
        _opCombo.Items.AddRange(new object[] { "Chứa (LIKE)", "Bằng (=)", "Khác (<>)", "Bắt đầu bằng", "Trống (IS NULL)" });
        _opCombo.SelectedIndex = 0;
        _opCombo.SelectedIndexChanged += (_, _) => _valueBox.Enabled = _opCombo.SelectedIndex != 4;

        AddRow(panel, "Cột", _columnCombo);
        AddRow(panel, "Điều kiện", _opCombo);
        AddRow(panel, "Giá trị", _valueBox);

        var buttons = new WebActionBar { DefaultActionId = "apply", CancelActionId = "cancel" };
        buttons.Add("clear", "Xoá Filter", WebActionKind.Quiet, left: true)
               .Add("cancel", "Huỷ", WebActionKind.Quiet)
               .Add("apply", "Áp dụng", WebActionKind.Primary);
        buttons.Invoked += id =>
        {
            switch (id)
            {
                case "apply": FilterExpression = BuildExpression(); DialogResult = DialogResult.OK; break;
                case "clear": FilterExpression = ""; DialogResult = DialogResult.OK; break;
                default: DialogResult = DialogResult.Cancel; break;
            }
            Close();
        };

        Controls.Add(panel);
        Controls.Add(buttons);
    }

    private static void AddRow(TableLayoutPanel form, string label, Control input)
    {
        var row = form.RowCount;
        form.RowCount = row + 1;
        form.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 10, 0) }, 0, row);
        form.Controls.Add(input, 1, row);
    }

    private string BuildExpression()
    {
        if (_columnCombo.SelectedItem is not string col || string.IsNullOrEmpty(col)) return "";
        var escapedCol = "[" + col.Replace("]", "]]") + "]";

        if (_opCombo.SelectedIndex == 4) return $"{escapedCol} IS NULL";

        var val = _valueBox.Text.Replace("'", "''");
        return _opCombo.SelectedIndex switch
        {
            0 => $"{escapedCol} LIKE '%{val}%'",
            1 => $"{escapedCol} = '{val}'",
            2 => $"{escapedCol} <> '{val}'",
            3 => $"{escapedCol} LIKE '{val}%'",
            _ => ""
        };
    }
}
