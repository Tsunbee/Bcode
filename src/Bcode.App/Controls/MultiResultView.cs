using System.Data;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Stacks one DataGridView per result set, matching FCode's own "::Result with N table(s)::"
/// view when a script/procedure returns more than one SELECT's worth of rows in a single
/// batch — most commonly an EXEC of a stored procedure that itself runs several SELECTs
/// (see RawSqlService.RunBatchesAsync, which used to silently keep only the FIRST result set
/// per batch since DataTable.Load doesn't advance through a reader's NextResult on its own;
/// it now walks every one of them). This is what actually displays all of them: each table
/// gets its own scrollable grid, roughly sized to its row count (small summary tables stay
/// compact, the "main" data table gets more room), stacked top-to-bottom inside one
/// vertically-scrolling view — same shape as FCode's own screenshot.
/// </summary>
public class MultiResultView : UserControl
{
    private readonly Label _countLabel;
    private readonly FlowLayoutPanel _flow;

    public MultiResultView()
    {
        Dock = DockStyle.Fill;

        _countLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 24,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            // ThemeManager.Apply's Label case only ever sets ForeColor, never BackColor — so
            // setting BackColor explicitly here (instead of leaving it unset) is what actually
            // survives the theme pass and keeps this from rendering as a plain white/gray bar
            // ("result tab xấu") against the rest of the dark-themed window. Bold survives too:
            // Apply() rebuilds a control's Font onto the theme's own family/size but keeps
            // FontStyle.Bold when the control already had it.
            BackColor = AppColors.PanelAlt,
            Font = new Font(Font, FontStyle.Bold),
            Visible = false
        };

        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true
        };
        _flow.Resize += (_, _) => ResizeGridsToFitWidth();

        Controls.Add(_flow);
        Controls.Add(_countLabel);
    }

    /// <summary>Replaces the whole view with one grid per table, in order. Passing an empty
    /// list clears the view (same as calling Clear()).</summary>
    public void SetTables(IReadOnlyList<DataTable> tables)
    {
        _flow.SuspendLayout();
        foreach (var old in _flow.Controls.OfType<DataGridView>().ToList())
        {
            _flow.Controls.Remove(old);
            old.Dispose();
        }

        _countLabel.Visible = tables.Count > 0;
        _countLabel.Text = $"Result with {tables.Count} table(s)"; // matches FCode's own "::Result with N table(s)::" wording

        var width = GridWidth();
        foreach (var table in tables)
        {
            var grid = new DataGridView
            {
                Width = width,
                Height = HeightFor(table),
                AllowUserToAddRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                Margin = new Padding(2, 2, 2, 8)
            };
            ResultGridMenu.Attach(grid);
            GridDisplayHelper.BindOptimized(grid, table);
            // Every grid here is created well AFTER the one-time ThemeManager.Apply(page) pass
            // that ran when this tab was first added (AddDocumentTab), since Execute doesn't
            // run until later — so without this, each new grid keeps WinForms' default light
            // colors regardless of the app's dark/light theme ("result tab xấu"). Apply() only
            // needs the grid itself (its DataGridView case doesn't recurse into anything new).
            ThemeManager.Apply(grid);
            _flow.Controls.Add(grid);
        }

        _flow.ResumeLayout();
    }

    public void Clear() => SetTables(Array.Empty<DataTable>());

    /// <summary>Roughly sizes each grid by its own row count instead of one fixed height for
    /// every table — a 1-row summary result (e.g. "rptMarginLeft, queryParameter") stays
    /// compact, a real data table gets enough room to be useful without the user immediately
    /// needing to resize it, same as FCode's own uneven table heights in the screenshot.</summary>
    private static int HeightFor(DataTable table)
    {
        const int headerHeight = 24;
        const int rowHeight = 22;
        var desired = headerHeight + Math.Min(Math.Max(table.Rows.Count, 1), 8) * rowHeight + 24;
        return Math.Clamp(desired, 90, 260);
    }

    private int GridWidth() => Math.Max(_flow.ClientSize.Width - 4, 200);

    private void ResizeGridsToFitWidth()
    {
        var width = GridWidth();
        foreach (var grid in _flow.Controls.OfType<DataGridView>())
            grid.Width = width;
    }
}
