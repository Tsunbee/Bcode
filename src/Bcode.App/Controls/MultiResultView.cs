using System.Data;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Stacks one DataGridView per result set, matching FCode's own "::Result with N table(s)::"
/// view when a script/procedure returns more than one SELECT's worth of rows in a single
/// batch — most commonly an EXEC of a stored procedure that itself runs several SELECTs.
/// </summary>
public class MultiResultView : UserControl
{
    private readonly Label _countLabel;
    private readonly Panel _container; // Thay đổi từ FlowLayoutPanel sang Panel thường để hỗ trợ Splitter

    public MultiResultView()
    {
        Dock = DockStyle.Fill;

        _countLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = AppColors.PanelAlt,
            Font = new Font(Font, FontStyle.Bold),
            Visible = false
        };

        _container = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true
        };

        Controls.Add(_container);
        Controls.Add(_countLabel);
    }

    /// <summary>Replaces the whole view with one grid per table, in order. Passing an empty
    /// list clears the view (same as calling Clear()).</summary>
    public void SetTables(IReadOnlyList<DataTable> tables)
    {
        _container.SuspendLayout();
        this.SuspendLayout();

        // Xóa toàn bộ control cũ (grid + splitter)
        foreach (var old in _container.Controls.OfType<Control>().ToList())
        {
            _container.Controls.Remove(old);
            old.Dispose();
        }
        foreach (var old in Controls.OfType<DataGridView>().ToList())
        {
            Controls.Remove(old);
            old.Dispose();
        }

        _countLabel.Visible = tables.Count > 0;
        _countLabel.Text = $"Result with {tables.Count} table(s)";

        if (tables.Count == 1)
        {
            // Trải dài toàn bộ màn hình nếu chỉ có 1 kết quả
            _container.Visible = false;
            
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BorderStyle = BorderStyle.None
            };
            
            ResultGridMenu.Attach(grid);
            GridDisplayHelper.BindOptimized(grid, tables[0]);
            ThemeManager.Apply(grid);
            
            Controls.Add(grid);
            grid.BringToFront();
        }
        else if (tables.Count > 1)
        {
            // Xếp chồng và chèn thanh kéo Splitter nếu có nhiều kết quả
            _container.Visible = true;
            
            for (int i = 0; i < tables.Count; i++)
            {
                var table = tables[i];
                var grid = new DataGridView
                {
                    Height = HeightFor(table),
                    Dock = DockStyle.Top, // Dock top tự động lấy Width = chiều rộng container
                    AllowUserToAddRows = false,
                    ReadOnly = true,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                    SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    BorderStyle = BorderStyle.None
                };
                
                ResultGridMenu.Attach(grid);
                GridDisplayHelper.BindOptimized(grid, table);
                ThemeManager.Apply(grid);
                
                _container.Controls.Add(grid);
                grid.BringToFront();

                // Thêm Splitter vào sau mỗi Grid (ngoại trừ Grid cuối cùng)
                if (i < tables.Count - 1)
                {
                    var splitter = new Splitter
                    {
                        Dock = DockStyle.Top,
                        Height = 6, // Độ dày của thanh kéo
                        BackColor = AppColors.Border // Lấy màu viền của Theme tối/sáng
                    };
                    _container.Controls.Add(splitter);
                    splitter.BringToFront();
                }
            }
        }

        this.ResumeLayout();
        _container.ResumeLayout();
    }

    public void Clear() => SetTables(Array.Empty<DataTable>());

    /// <summary>Roughly sizes each grid by its own row count instead of one fixed height for
    /// every table.</summary>
    private static int HeightFor(DataTable table)
    {
        const int headerHeight = 24;
        const int rowHeight = 22;
        var desired = headerHeight + Math.Min(Math.Max(table.Rows.Count, 1), 8) * rowHeight + 24;
        return Math.Clamp(desired, 90, 260);
    }
}