using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Màn hình "Projects" hiện khi mới mở Bcode (và qua Actions &gt; Projects...): gõ để lọc danh sách project đã khai báo,
/// hoặc bấm nhanh 1 project trong dải "Last Access" (các project dùng gần nhất), rồi Enter / nhấp đúp để chọn.
/// New / Edit / Delete dùng EditProjectForm (cùng popup Edit Project của Ctrl+F5) và ghi thẳng vào AppSettings.
/// </summary>
public class ProjectPickerForm : ThemedForm
{
    private readonly AppSettings _settings;
    private readonly DbConnectionService _connections;
    private readonly TextBox _filterBox = new() { Width = 260, PlaceholderText = "Gõ để lọc (ID, database, link, đường dẫn)..." };
    private readonly DataGridView _grid = new();
    private readonly FlowLayoutPanel _recentPanel = new();
    private readonly Label _countLabel = new() { AutoSize = true, Dock = DockStyle.Right, Padding = new Padding(0, 8, 8, 0) };

    // Lưới chạy ở chế độ ảo (VirtualMode): chỉ dựng các dòng đang nhìn thấy nên danh sách nhiều project (FSG có thể đồng bộ hàng trăm) vẫn mượt.
    // _view = các project sau khi lọc + sắp xếp; ô lọc có độ trễ ngắn (gõ liên tục không lọc lại mỗi phím).
    private List<Workspace> _view = new();
    private readonly Dictionary<Workspace, string> _hay = new();
    private int _sortCol;
    private bool _sortAsc = true;
    private readonly System.Windows.Forms.Timer _filterTimer = new() { Interval = 120 };

    /// <summary>Project được chọn khi form đóng với DialogResult.OK.</summary>
    public Workspace? Chosen { get; private set; }

    public ProjectPickerForm(AppSettings settings, DbConnectionService connections)
    {
        _settings = settings;
        _connections = connections;

        Text = "Projects";
        Width = 1040;
        Height = 600;
        MinimumSize = new Size(760, 420);
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;

        // ---- Hàng nút: ô lọc + New/Edit/Delete/Refresh/Copy/Close ----
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8, 8, 8, 4), WrapContents = false };
        _filterBox.Margin = new Padding(0, 3, 12, 0);
        _filterTimer.Tick += (_, _) => { _filterTimer.Stop(); Reload(); };
        _filterBox.TextChanged += (_, _) => { _filterTimer.Stop(); _filterTimer.Start(); };
        Disposed += (_, _) => _filterTimer.Dispose();
        _filterBox.KeyDown += FilterKeyDown;
        bar.Controls.Add(_filterBox);

        void AddBtn(string text, Action onClick, bool primary = false)
        {
            var b = PillButton.Flat(text, primary);
            b.Margin = new Padding(0, 0, 6, 0);
            b.Click += (_, _) => onClick();
            bar.Controls.Add(b);
        }
        AddBtn("＋ New", NewProject);
        AddBtn("✎ Edit", EditProject);
        AddBtn("✕ Delete", DeleteProject);
        AddBtn("⟳ Refresh", () => { _settings.Workspaces = AppSettings.Load().Workspaces; _hay.Clear(); BuildRecent(); Reload(); });
        AddBtn("⧉ Copy Project Info", CopyInfo);
        AddBtn("Chọn", Pick, primary: true);
        AddBtn("Close", () => { DialogResult = DialogResult.Cancel; Close(); });

        // ---- Dải Last Access ----
        _recentPanel.Dock = DockStyle.Top;
        _recentPanel.Height = 40;
        _recentPanel.Padding = new Padding(8, 4, 8, 4);
        _recentPanel.WrapContents = true;
        _recentPanel.AutoScroll = false;

        // ---- Lưới ----
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersWidth = 28;
        _grid.VirtualMode = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        ControlPerf.EnableDoubleBuffering(_grid);
        void Col(string header, int width, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.Programmatic };
            if (fill) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.MinimumWidth = width; }
            _grid.Columns.Add(c);
        }
        Col("ID", 130);
        Col("Sys Data", 170);
        Col("App Data", 170);
        Col("Mã phiên bản", 130);
        Col("WLoginLink", 220);
        Col("Program Path", 260);
        Col("Source Path", 300, fill: true);
        _grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
            var w = _view[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => IdOf(w), 1 => w.SysDatabase, 2 => w.AppDatabase, 3 => w.VersionCode, 4 => w.LoginWLink, 5 => w.ProgramPath, _ => w.SourcePath,
            };
        };
        _grid.ColumnHeaderMouseClick += (_, e) =>
        {
            _sortAsc = _sortCol == e.ColumnIndex ? !_sortAsc : true;
            _sortCol = e.ColumnIndex;
            Reload();
        };
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Pick(); };
        // Chuột phải một dòng: chọn dòng đó rồi hiện menu (Chọn / Edit / Delete / Copy).
        _grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0 || e.RowIndex >= _view.Count) return;
            _grid.ClearSelection();
            _grid.Rows[e.RowIndex].Selected = true;
            _grid.CurrentCell = _grid.Rows[e.RowIndex].Cells[Math.Max(0, e.ColumnIndex)];
        };
        WebMenu.AttachTo(_grid, () => Selected is { } w ? ProjectMenu(w, includeDelete: true) : null);
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; Pick(); }
        };

        Controls.Add(_grid);
        Controls.Add(_recentPanel);
        Controls.Add(bar);
        bar.Controls.Add(_countLabel);

        BuildRecent();
        Reload();

        Shown += (_, _) => { _filterBox.Focus(); };
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            // Ctrl+F5 ngay tại màn hình Projects: đồng bộ project từ danh mục FSG (không còn là phím tắt chung khi đang làm việc).
            else if (e.KeyCode == Keys.F5 && e.Control) { e.Handled = true; OpenEditor(Selected, autoSync: true); }
        };
    }

    // ---- Dữ liệu ----------------------------------------------------------------------------

    private static string IdOf(Workspace w) => string.IsNullOrWhiteSpace(w.ProjectId) ? w.Name : w.ProjectId;

    private static string Haystack(Workspace w) =>
        string.Join(' ', IdOf(w), w.Name, w.Server, w.SysDatabase, w.AppDatabase, w.VersionCode, w.DbAccess, w.LoginWLink, w.ProgramPath, w.SourcePath).ToLowerInvariant();

    private string HayOf(Workspace w) => _hay.TryGetValue(w, out var h) ? h : _hay[w] = Haystack(w);

    private string SortKey(Workspace w) => (_sortCol switch
    {
        0 => IdOf(w), 1 => w.SysDatabase, 2 => w.AppDatabase, 3 => w.VersionCode, 4 => w.LoginWLink, 5 => w.ProgramPath, _ => w.SourcePath,
    }) ?? "";

    private void Reload()
    {
        var tokens = _filterBox.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = Selected;

        IEnumerable<Workspace> q = _settings.Workspaces;
        if (tokens.Length > 0) q = q.Where(w => { var hay = HayOf(w); return tokens.All(hay.Contains); });
        var list = q.ToList();
        list.Sort((a, b) => (_sortAsc ? 1 : -1) * StringComparer.OrdinalIgnoreCase.Compare(SortKey(a), SortKey(b)));
        _view = list;

        _grid.RowCount = 0;
        _grid.RowCount = _view.Count;
        foreach (DataGridViewColumn c in _grid.Columns)
            c.HeaderCell.SortGlyphDirection = c.Index == _sortCol ? (_sortAsc ? SortOrder.Ascending : SortOrder.Descending) : SortOrder.None;
        _countLabel.Text = $"{_view.Count}/{_settings.Workspaces.Count} project";

        if (_view.Count > 0)
        {
            // Giữ dòng đang chọn nếu còn; không thì chọn dòng đầu (gõ lọc xong Enter là chọn luôn).
            var idx = current is null ? -1 : _view.IndexOf(current);
            var i = idx >= 0 ? idx : 0;
            _grid.ClearSelection();
            _grid.Rows[i].Selected = true;
            _grid.CurrentCell = _grid.Rows[i].Cells[0];
        }
    }

    /// <summary>Menu chuột phải của 1 project (ở dải Last Access và ở lưới): Chọn / Edit / Copy info / (Delete).</summary>
    private WebMenu ProjectMenu(Workspace w, bool includeDelete)
    {
        var menu = new WebMenu()
            .Add("Chọn project này", () => { Chosen = w; DialogResult = DialogResult.OK; Close(); })
            .Add("Edit project...", () => BeginInvoke(new Action(() => OpenEditor(select: w))))
            .Add("Copy Project Info", () => CopyInfoOf(w));
        if (includeDelete) menu.AddSeparator().Add("Delete", () => BeginInvoke(new Action(() => DeleteProjectOf(w))), danger: true);
        return menu;
    }

    private void BuildRecent()
    {
        _recentPanel.Controls.Clear();
        _recentPanel.Controls.Add(new Label { Text = "Last Access", AutoSize = true, Margin = new Padding(0, 7, 8, 0), ForeColor = AppColors.TextMuted });
        foreach (var name in _settings.RecentWorkspaces.Take(10))
        {
            var w = _settings.Workspaces.FirstOrDefault(x => x.Name == name);
            if (w is null) continue;
            var b = PillButton.Flat(IdOf(w));
            b.Margin = new Padding(0, 0, 6, 0);
            b.Click += (_, _) => { Chosen = w; DialogResult = DialogResult.OK; Close(); };
            WebMenu.AttachTo(b, () => ProjectMenu(w, includeDelete: false)
                .AddSeparator().Add("Bỏ khỏi Last Access", () => { _settings.RecentWorkspaces.Remove(w.Name); _settings.Save(); BuildRecent(); }));
            _recentPanel.Controls.Add(b);
        }
    }

    // ---- Thao tác ---------------------------------------------------------------------------------

    private void FilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; Pick(); return; }
        if (e.KeyCode is Keys.Down or Keys.Up && _view.Count > 0)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            var i = _grid.CurrentRow?.Index ?? -1;
            i = Math.Max(0, Math.Min(_view.Count - 1, i + (e.KeyCode == Keys.Down ? 1 : -1)));
            _grid.ClearSelection();
            _grid.Rows[i].Selected = true;
            _grid.CurrentCell = _grid.Rows[i].Cells[0];
        }
    }

    private Workspace? Selected => _grid.CurrentRow is { Index: var i } && i >= 0 && i < _view.Count ? _view[i] : null;

    private void Pick()
    {
        if (Selected is not { } w) return;
        Chosen = w;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>Mở đúng giao diện của File &gt; Choose Server (ConnectionSettingsForm) cho New / Edit / Synchronize. allowSync = true
    /// vì đây là màn hình Projects lúc mới mở — nơi duy nhất có "Synchronize (Ctrl+F5)". Đóng mà không Save thì bỏ các thay đổi
    /// chưa lưu (vd dòng WS mới vừa tạo) bằng cách nạp lại danh sách từ file.</summary>
    private void OpenEditor(Workspace? select = null, bool startNew = false, bool autoSync = false)
    {
        using var form = new ConnectionSettingsForm(_settings, _connections, allowSync: true, select, startNew, autoSync);
        var result = form.ShowDialog(this);
        if (result != DialogResult.OK) _settings.Workspaces = AppSettings.Load().Workspaces;
        _hay.Clear();
        BuildRecent();
        Reload();
    }

    private void NewProject() => OpenEditor(startNew: true);

    private void EditProject()
    {
        if (Selected is not { } w) return;
        OpenEditor(select: w);
    }

    private void DeleteProject() { if (Selected is { } w) DeleteProjectOf(w); }

    private void DeleteProjectOf(Workspace w)
    {
        if (MessageBox.Show(this, $"Xoá project \"{IdOf(w)}\" khỏi danh sách?", "Bcode — Projects",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _settings.Workspaces.Remove(w);
        _settings.RecentWorkspaces.Remove(w.Name);
        _settings.Save();
        _hay.Clear();
        BuildRecent();
        Reload();
    }

    private void CopyInfo() { if (Selected is { } w) CopyInfoOf(w); }

    private void CopyInfoOf(Workspace w)
    {
        // Không đưa mật khẩu vào thông tin copy.
        var text = string.Join(Environment.NewLine, new[]
        {
            $"ID: {IdOf(w)}", $"Server: {w.Server}", $"Sys Data: {w.SysDatabase}", $"App Data: {w.AppDatabase}",
            $"WLoginLink: {w.LoginWLink}", $"Program Path: {w.ProgramPath}", $"Source Path: {w.SourcePath}",
            $"Mobile Path: {w.MobilePath}", $"Working Path: {w.WorkingPath}", $"Version: {w.VersionCode}", $"DB Access: {w.DbAccess}",
        });
        try { Clipboard.SetText(text); } catch { /* clipboard đang bận */ }
    }
}
