using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// "Copy File(s) to..." — File Lookup's right-click action for cloning a source file into
/// another configured project, matching FCode's own File Lookup context menu (Project ID +
/// Mobile Path + Copy to Path + a Path/Source/Extension/Destination grid).
///
/// Project ID is resolved against the already-known Workspaces list (File > Choose Server /
/// Workspaces...) rather than typed as a raw path: typing "VPMILK" looks up that workspace's
/// Source Path (or Mobile Path, when the checkbox is on) as the destination root. The file's
/// relative position is kept the same on the other side — a file at
/// "{currentRoot}\App_Data\Controllers\Grid\SVDetail.xml" lands at
/// "{destinationRoot}\App_Data\Controllers\Grid\SVDetail.xml" — same idea as
/// GenUpdatePackageControl's own relative-path copy, just for a single quick clone instead of
/// a whole update package.
/// </summary>
public class CopyFileToForm : Form
{
    private sealed class CopyRow
    {
        public string Path { get; init; } = ""; // relative folder under the source root, e.g. App_Data\Controllers\Grid
        public string Source { get; init; } = ""; // filename
        public string Extension { get; init; } = "";
        public string Destination { get; set; } = ""; // full resolved destination path
    }

    private readonly List<Workspace> _workspaces;
    private readonly List<string> _sourceFiles; // full paths, same order/index as _rows
    private readonly TextBox _projectIdBox;
    private readonly CheckBox _mobilePathBox;
    private readonly TextBox _copyToPathBox;
    private readonly DataGridView _grid;
    private readonly List<CopyRow> _rows;

    public CopyFileToForm(List<Workspace> workspaces, string sourceRoot, IEnumerable<string> sourceFiles)
    {
        _workspaces = workspaces;
        _sourceFiles = sourceFiles.ToList();

        Text = "Copy Files";
        Width = 680;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        MinimizeBox = false;
        MaximizeBox = false;

        // ---- Top: Project ID / Mobile Path / Copy to Path ----
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 76,
            ColumnCount = 3,
            Padding = new Padding(10, 10, 10, 4)
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        top.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        top.Controls.Add(new Label { Text = "Project ID", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _projectIdBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 4, 4) };
        top.Controls.Add(_projectIdBox, 1, 0);
        _mobilePathBox = new CheckBox { Text = "Mobile Path", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(4, 4, 0, 4) };
        top.Controls.Add(_mobilePathBox, 2, 0);

        top.Controls.Add(new Label { Text = "Copy to Path", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        _copyToPathBox = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 4, 4) };
        top.Controls.Add(_copyToPathBox, 1, 1);
        top.SetColumnSpan(_copyToPathBox, 2);

        var listLabel = new Label { Text = "List files", Dock = DockStyle.Top, Height = 22, Padding = new Padding(10, 4, 0, 0) };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = true,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            Margin = new Padding(10, 0, 10, 0)
        };

        // Relative folder is computed against sourceRoot (the path currently typed into File
        // Lookup's own Path box, which is what the tree — and therefore the right-clicked
        // node — was built from), not any one workspace's Source Path, so this still works
        // when the user browsed into a sub-folder rather than the site root.
        _rows = _sourceFiles.Select(f => new CopyRow
        {
            Path = System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(sourceRoot, f)) ?? "",
            Source = System.IO.Path.GetFileName(f),
            Extension = System.IO.Path.GetExtension(f).TrimStart('.'),
        }).ToList();
        GridDisplayHelper.BindOptimized(_grid, _rows);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var okButton = PillButton.Flat("OK", primary: true);
        okButton.Click += (_, _) => DoCopy();
        var cancelButton = PillButton.Flat("Cancel");
        cancelButton.Margin = new Padding(8, 0, 0, 0);
        cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        bottom.Controls.Add(okButton);
        bottom.Controls.Add(cancelButton);

        Controls.Add(_grid);
        Controls.Add(listLabel);
        Controls.Add(bottom);
        Controls.Add(top);

        // Wired after the grid/_rows exist — both handlers end up touching _rows/_grid via
        // RecomputeDestinations, and setting _projectIdBox.Text just below fires this chain
        // immediately (TextChanged), so everything it can reach must already be built.
        _projectIdBox.TextChanged += (_, _) => ResolveCopyToPath();
        _mobilePathBox.CheckedChanged += (_, _) => ResolveCopyToPath();
        _copyToPathBox.TextChanged += (_, _) => RecomputeDestinations();

        // Default Project ID: the workspace whose Source Path matches where File Lookup is
        // currently rooted, if any — saves retyping the same project just to flip Mobile Path.
        var current = _workspaces.FirstOrDefault(w =>
            !string.IsNullOrWhiteSpace(w.SourcePath) &&
            string.Equals(w.SourcePath.TrimEnd('\\'), sourceRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        if (current is not null) _projectIdBox.Text = current.ProjectId;

        RecomputeDestinations(); // covers the no-match case too, leaving Destination blank rather than stale
        ThemeManager.Apply(this);
    }

    /// <summary>Looks up the typed Project ID against the known Workspaces and fills Copy to
    /// Path from its Source Path (or Mobile Path when that checkbox is on). Leaves Copy to
    /// Path untouched on no match, so someone can still type/paste a raw destination path
    /// directly instead of going through a configured project.</summary>
    private void ResolveCopyToPath()
    {
        var id = _projectIdBox.Text.Trim();
        if (id.Length == 0) return;

        var match = _workspaces.FirstOrDefault(w => string.Equals(w.ProjectId, id, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;

        var basePath = _mobilePathBox.Checked ? match.MobilePath : match.SourcePath;
        if (!string.IsNullOrWhiteSpace(basePath))
            _copyToPathBox.Text = basePath.TrimEnd('\\');
    }

    private void RecomputeDestinations()
    {
        var basePath = _copyToPathBox.Text.Trim();
        foreach (var row in _rows)
            row.Destination = basePath.Length == 0
                ? ""
                : System.IO.Path.Combine(basePath, row.Path, row.Source);

        // _rows is a plain List<T> (no INotifyPropertyChanged), so the grid doesn't pick up
        // the Destination edits above on its own — a repaint re-reads each cell's value
        // through the same property, so Invalidate is enough without a full re-bind.
        _grid.Invalidate();
    }

    private void DoCopy()
    {
        if (_rows.Count == 0 || _rows.Any(r => string.IsNullOrWhiteSpace(r.Destination)))
        {
            MessageBox.Show(this,
                "Chưa xác định được Copy to Path — nhập đúng Project ID hoặc gõ thẳng đường dẫn đích.",
                "Bcode — Copy Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var existing = _rows.Where(r => File.Exists(r.Destination)).Select(r => r.Source).ToList();
        if (existing.Count > 0)
        {
            var confirm = MessageBox.Show(this,
                $"{existing.Count} file đã có sẵn ở nơi đến, ghi đè?\n" + string.Join("\n", existing),
                "Bcode — Copy Files", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
        }

        var copied = 0;
        var errors = new List<string>();
        for (var i = 0; i < _rows.Count; i++)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_rows[i].Destination)!);
                File.Copy(_sourceFiles[i], _rows[i].Destination, overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                errors.Add($"{_rows[i].Source}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                $"Đã copy {copied}/{_rows.Count} file.\nLỗi:\n" + string.Join("\n", errors),
                "Bcode — Copy Files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            MessageBox.Show(this, $"Đã copy {copied} file thành công.",
                "Bcode — Copy Files", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        DialogResult = DialogResult.OK;
        Close();
    }
}
