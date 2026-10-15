using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Màn hình "Projects" của chế độ App/WinForms — cùng chức năng với <see cref="ProjectPickerForm"/> (trang WebView2 projects.html) nhưng bằng control Windows,
/// nên mở màn hình này KHÔNG khởi động WebView2: gõ để lọc (cách nhau bằng khoảng trắng = mọi từ phải khớp), dải Last Access, bấm tiêu đề cột để sắp xếp,
/// Enter / nhấp đúp để chọn, Ctrl+F5 = Synchronize, chuột phải = Chọn / Edit / Copy info / Delete.
/// Lưới ảo (VirtualMode): danh sách hàng nghìn project vẫn mượt, không dựng từng dòng.
/// </summary>
public sealed class NativeProjectPickerForm : ThemedForm, IProjectPicker
{
    private static readonly (string Key, string Title, Func<Workspace, string> Get)[] Cols =
    {
        ("id", "ID", ProjectActions.IdOf), ("sys", "Sys Data", w => w.SysDatabase ?? ""), ("app", "App Data", w => w.AppDatabase ?? ""),
        ("ver", "Mã phiên bản", w => w.VersionCode ?? ""), ("wlogin", "WLoginLink", w => w.LoginWLink ?? ""),
        ("prog", "Program Path", w => w.ProgramPath ?? ""), ("src", "Source Path", w => w.SourcePath ?? ""),
    };

    private readonly AppSettings _settings;
    private readonly DbConnectionService _connections;
    private readonly TextBox _filter = new() { Width = 340 };
    private readonly Label _count = new() { AutoSize = true, Margin = new Padding(12, 9, 0, 0) };
    private readonly FlowLayoutPanel _recent = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 4, 8, 4) };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill };
    private List<(Workspace W, string Hay)> _all = new();
    private List<Workspace> _view = new();
    private int _sortCol;
    private bool _asc = true;

    public Workspace? Chosen { get; private set; }

    public NativeProjectPickerForm(AppSettings settings, DbConnectionService connections)
    {
        _settings = settings;
        _connections = connections;
        Text = "Projects";
        Width = 1120; Height = 640; MinimumSize = new Size(760, 420);
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; ShowIcon = false; KeyPreview = true;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 4), WrapContents = true };
        top.Controls.Add(_filter);
        PillButton Add(string text, System.Action act, bool primary = false)
        {
            var b = PillButton.Flat(text, primary); b.Margin = new Padding(6, 1, 0, 0); b.Click += (_, _) => act(); top.Controls.Add(b); return b;
        }
        Add("＋ New", () => Edit(null, startNew: true));
        Add("✎ Edit", () => { if (Current() is { } w) Edit(w); });
        Add("✕ Delete", () => { if (Current() is { } w) Remove(w); });
        Add("⟳ Refresh", Refresh);
        Add("⧉ Copy Project Info", () => { if (Current() is { } w) ProjectActions.CopyInfo(w); });
        Add("Chọn", PickCurrent, primary: true);
        Add("Close", () => { DialogResult = DialogResult.Cancel; Close(); });
        top.Controls.Add(_count);

        _grid.ReadOnly = true; _grid.AllowUserToAddRows = false; _grid.AllowUserToDeleteRows = false; _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = false;
        _grid.VirtualMode = true; _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; _grid.BorderStyle = BorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        var widths = new[] { 150, 170, 170, 110, 260, 260, 260 };
        for (var i = 0; i < Cols.Length; i++)
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = Cols[i].Title, Width = widths[i], SortMode = DataGridViewColumnSortMode.Programmatic });
        _grid.CellValueNeeded += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < _view.Count) e.Value = Cols[e.ColumnIndex].Get(_view[e.RowIndex]); };
        _grid.ColumnHeaderMouseClick += (_, e) => { _asc = _sortCol == e.ColumnIndex ? !_asc : true; _sortCol = e.ColumnIndex; ApplyFilter(Current()); };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) PickCurrent(); };
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            _grid.ClearSelection(); _grid.Rows[e.RowIndex].Selected = true; _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
            ShowMenu(_view[e.RowIndex], chip: false);
        };

        _filter.TextChanged += (_, _) => ApplyFilter(Current());
        _filter.KeyDown += (_, e) =>
        {
            var step = e.KeyCode switch { Keys.Down => 1, Keys.Up => -1, Keys.PageDown => 10, Keys.PageUp => -10, _ => 0 };
            if (step == 0 || _view.Count == 0) return;
            e.Handled = e.SuppressKeyPress = true;
            Select(Math.Clamp((_grid.CurrentRow?.Index ?? 0) + step, 0, _view.Count - 1));
        };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { PickCurrent(); e.Handled = e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.F5) { if (e.Control) Edit(Current(), autoSync: true); e.Handled = e.SuppressKeyPress = true; }
        };

        Controls.Add(_grid);
        Controls.Add(_recent);
        Controls.Add(top);
        Shown += (_, _) => _filter.Focus();
        LoadData();
    }

    private Workspace? Current() => _grid.CurrentRow is { } r && r.Index >= 0 && r.Index < _view.Count ? _view[r.Index] : null;

    private void Select(int i)
    {
        if (i < 0 || i >= _view.Count) return;
        _grid.ClearSelection();
        _grid.CurrentCell = _grid.Rows[i].Cells[0];
        _grid.Rows[i].Selected = true;
    }

    private void LoadData()
    {
        _all = _settings.Workspaces.Select(w => (w, ProjectActions.Haystack(w))).ToList();
        RebuildRecent();
        ApplyFilter(null);
    }

    private void RebuildRecent()
    {
        _recent.SuspendLayout();
        _recent.Controls.Clear();
        _recent.Controls.Add(new MutedLabel { Text = "Last Access", AutoSize = true, Margin = new Padding(0, 8, 6, 0), BackColor = Color.Transparent });
        foreach (var name in _settings.RecentWorkspaces.Take(10))
        {
            var w = _settings.Workspaces.FirstOrDefault(x => x.Name == name);
            if (w is null) continue;
            var chip = PillButton.Flat(ProjectActions.IdOf(w));
            chip.CornerRadius = null;   // viên thuốc tròn như .chip của trang web
            chip.Margin = new Padding(0, 0, 6, 0);
            chip.Click += (_, _) => { Chosen = w; DialogResult = DialogResult.OK; Close(); };
            chip.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowMenu(w, chip: true); };
            _recent.Controls.Add(chip);
        }
        _recent.ResumeLayout(true);
    }

    private void ApplyFilter(Workspace? keep)
    {
        var tokens = _filter.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var get = Cols[_sortCol].Get;
        var q = _all.Where(x => tokens.All(t => x.Hay.Contains(t))).Select(x => x.W);
        q = _asc ? q.OrderBy(get, StringComparer.OrdinalIgnoreCase) : q.OrderByDescending(get, StringComparer.OrdinalIgnoreCase);
        _view = q.ToList();
        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
        _count.Text = $"{_view.Count}/{_all.Count} project";
        if (_view.Count > 0) Select(Math.Max(0, keep is null ? 0 : _view.IndexOf(keep)));
        _grid.Invalidate();
    }

    private void PickCurrent()
    {
        if (Current() is not { } w) return;
        Chosen = w; DialogResult = DialogResult.OK; Close();
    }

    private void Refresh()
    {
        _settings.Workspaces = AppSettings.Load().Workspaces;
        LoadData();
    }

    private void Edit(Workspace? w, bool startNew = false, bool autoSync = false)
    {
        BeginInvoke(new Action(() =>
        {
            ProjectActions.OpenEditor(this, _settings, _connections, w, startNew, autoSync);
            LoadData();
        }));
    }

    private void Remove(Workspace w) { if (ProjectActions.Delete(this, _settings, w)) LoadData(); }

    /// <summary>Menu chuột phải của 1 project: dải Last Access (có thêm "Bỏ khỏi Last Access") hoặc bảng (có thêm Delete).</summary>
    private void ShowMenu(Workspace w, bool chip)
    {
        var menu = new WebMenu()
            .Add("Chọn project này", () => { Chosen = w; DialogResult = DialogResult.OK; Close(); })
            .Add("Edit project...", () => Edit(w))
            .Add("Copy Project Info", () => ProjectActions.CopyInfo(w));
        if (chip) menu.AddSeparator().Add("Bỏ khỏi Last Access", () => { _settings.RecentWorkspaces.Remove(w.Name); _settings.Save(); RebuildRecent(); });
        else menu.AddSeparator().Add("Delete", () => BeginInvoke(new Action(() => Remove(w))), danger: true);
        menu.Show(this, Cursor.Position);
    }
}
