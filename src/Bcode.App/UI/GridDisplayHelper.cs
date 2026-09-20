namespace Bcode.App.UI;

/// <summary>
/// Binds a DataGridView's data source the way a result grid should feel responsive doing it:
/// one autosize pass right after loading, then fixed column widths — instead of leaving
/// AutoSizeColumnsMode continuously on, which recalculates every column's width on basically
/// every scroll/paint and is what makes a wide result grid with a few hundred+ rows feel
/// stiff ("đơ") while scrolling. Also turns on double buffering (off by default on
/// DataGridView) to cut the flicker/tearing that shows up scrolling a large grid without it.
/// </summary>
public static class GridDisplayHelper
{
    public static void BindOptimized(DataGridView grid, object? dataSource)
    {
        EnableDoubleBuffering(grid);
        EnableRowNumbers(grid);

        grid.SuspendLayout();
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        grid.DataSource = dataSource;
        // One-time sizing pass based on what's actually in the columns — same visual result
        // as leaving AutoSizeColumnsMode on DisplayedCells permanently, minus the ongoing
        // per-scroll recalculation cost.
        if (grid.Columns.Count > 0)
            grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);

        // Widen the row-header column to fit the actual row count (e.g. "1234" needs more
        // room than "1") now that the data is bound and Rows.Count is final.
        var digits = Math.Max(2, grid.Rows.Count.ToString().Length);
        grid.RowHeadersWidth = Math.Max(40, TextRenderer.MeasureText(new string('9', digits), grid.Font).Width + 24);

        grid.ResumeLayout();
    }

    /// <summary>DataGridView's row-header column exists by default but is blank — this draws
    /// a 1-based row number into it, the "phần kết quả cũng có số dòng" a plain result grid
    /// is otherwise missing (unlike the script box's own new line-number gutter). Wired once
    /// per grid (RowPostPaint -= then += with the same static method group is idempotent, so
    /// re-binding the same grid instance repeatedly across runs doesn't stack handlers).</summary>
    private static void EnableRowNumbers(DataGridView grid)
    {
        grid.RowHeadersVisible = true;
        grid.RowPostPaint -= DrawRowNumber;
        grid.RowPostPaint += DrawRowNumber;
    }

    private static void DrawRowNumber(object? sender, DataGridViewRowPostPaintEventArgs e)
    {
        if (sender is not DataGridView grid) return;
        var bounds = new Rectangle(e.RowBounds.Left, e.RowBounds.Top, grid.RowHeadersWidth, e.RowBounds.Height);
        TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), grid.Font, bounds,
            grid.RowHeadersDefaultCellStyle.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPadding);
    }

    /// <summary>DataGridView (like most WinForms controls) has DoubleBuffered as a protected
    /// property — reflection is the standard way to flip it on from outside the control's
    /// own class without subclassing just for this. Idempotent, so it's fine to call on
    /// every bind rather than tracking whether it's already been set.</summary>
    private static void EnableDoubleBuffering(DataGridView grid) =>
        typeof(DataGridView)
            .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(grid, true);
}
