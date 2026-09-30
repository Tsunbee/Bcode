using System.Data;
using System.Linq;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Stacks one DataGridView per result set, matching FCode's own "::Result with N table(s)::"
/// view when a script/procedure returns more than one SELECT's worth of rows in a single
/// batch — most commonly an EXEC of a stored procedure that itself runs several SELECTs.
///
/// From table #2 onward the resize handle was reported as not working (ĐÃ SỬA): the earlier
/// version stacked classic <see cref="Splitter"/> controls between plain Dock=Top grids
/// inside one Panel, calling BringToFront() on every grid/splitter right after adding it.
/// The classic Splitter finds "the control to resize" by z-order among its siblings — and
/// each BringToFront() call reshuffles that z-order for every control already added, so by
/// the time a 2nd or 3rd splitter exists, its target relationship no longer matches which
/// grid is visually next to it, and dragging it does nothing (or moves the wrong grid).
/// Nested <see cref="SplitContainer"/>s (already used for the editor/result split in
/// RawSqlControl) sidestep the problem entirely: each SplitContainer only ever manages its
/// own two panels, so it doesn't matter how many are nested — every splitter drags exactly
/// the two panels either side of it, every time.
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

            var grid = BuildResultGrid(tables[0]);
            grid.Dock = DockStyle.Fill;
            Controls.Add(grid);
            grid.BringToFront();
        }
        else if (tables.Count > 1)
        {
            // Ghép các bảng bằng SplitContainer lồng nhau (Panel1 = phần đã gộp phía trên,
            // Panel2 = bảng mới thêm vào) thay vì nhiều Splitter cổ điển rời rạc — xem chú
            // thích ở đầu file. Mỗi tầng SplitContainer tự lo 2 panel của chính nó nên kéo
            // được ngay cả với bảng thứ 2, thứ 3, ...
            _container.Visible = true;

            Control accumulated = BuildResultGrid(tables[0]);
            accumulated.Dock = DockStyle.Fill;
            for (var i = 1; i < tables.Count; i++)
            {
                var split = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    Orientation = Orientation.Horizontal,
                    SplitterWidth = 6,
                    Panel1MinSize = 40,
                    Panel2MinSize = 40,
                    BackColor = AppColors.Border,
                };
                split.Panel1.Controls.Add(accumulated);
                var nextGrid = BuildResultGrid(tables[i]);
                nextGrid.Dock = DockStyle.Fill;
                split.Panel2.Controls.Add(nextGrid);

                // Đặt tỉ lệ ban đầu theo "trọng lượng" mong muốn của khối đã gộp so với
                // bảng mới (dựa trên HeightFor — gần với cảm giác cũ, bảng nhiều dòng hơn
                // được cấp nhiều chỗ hơn) thay vì chia đôi 50/50 máy móc. Phải làm trong
                // HandleCreated vì SplitterDistance cần Height thật của control đã có handle.
                var accumulatedWeight = tables.Take(i).Sum(HeightFor);
                var nextWeight = HeightFor(tables[i]);
                split.HandleCreated += (_, _) =>
                {
                    try
                    {
                        var total = split.Height;
                        var minTotal = split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth;
                        if (total <= minTotal) return;
                        var ratio = (double)accumulatedWeight / Math.Max(1, accumulatedWeight + nextWeight);
                        split.SplitterDistance = Math.Clamp((int)(total * ratio), split.Panel1MinSize, total - split.Panel2MinSize - split.SplitterWidth);
                    }
                    catch { /* SplitterDistance có thể ném khi control chưa đủ lớn — bỏ qua, giữ mặc định */ }
                };

                accumulated = split;
            }

            accumulated.Dock = DockStyle.Fill;
            _container.Controls.Add(accumulated);
        }

        this.ResumeLayout();
        _container.ResumeLayout();
    }

    public void Clear() => SetTables(Array.Empty<DataTable>());

    private static DataGridView BuildResultGrid(DataTable table)
    {
        var grid = new DataGridView
        {
            AllowUserToAddRows = false,
            ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            BorderStyle = BorderStyle.None
        };

        ResultGridMenu.Attach(grid);
        GridDisplayHelper.BindOptimized(grid, table);
        ThemeManager.Apply(grid);
        return grid;
    }

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