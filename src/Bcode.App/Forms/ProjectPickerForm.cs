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
        _filterBox.TextChanged += (_, _) => Reload();
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
        AddBtn("⟳ Refresh", () => { _settings.Workspaces = AppSettings.Load().Workspaces; BuildRecent(); Reload(); });
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
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        void Col(string header, int width, bool fill = false)
        {
            var c = new DataGridViewTextBoxColumn { HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.Automatic };
            if (fill) { c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill; c.MinimumWidth = width; }
            _grid.Columns.Add(c);
        }
        Col("ID", 130);
        Col("Sys Data", 170);
        Col("App Data", 170);
        Col("WLoginLink", 220);
        Col("Program Path", 260);
        Col("Source Path", 300, fill: true);
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) Pick(); };
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
        string.Join(' ', IdOf(w), w.Name, w.Server, w.SysDatabase, w.AppDatabase, w.LoginWLink, w.ProgramPath, w.SourcePath).ToLowerInvariant();

    private void Reload()
    {
        var tokens = _filterBox.Text.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var current = _grid.CurrentRow?.Tag as Workspace;

        _grid.Rows.Clear();
        var shown = 0;
        foreach (var w in _settings.Workspaces.OrderBy(w => IdOf(w), StringComparer.OrdinalIgnoreCase))
        {
            if (tokens.Length > 0)
            {
                var hay = Haystack(w);
                if (!tokens.All(hay.Contains)) continue;
            }
            var i = _grid.Rows.Add(IdOf(w), w.SysDatabase, w.AppDatabase, w.LoginWLink, w.ProgramPath, w.SourcePath);
            _grid.Rows[i].Tag = w;
            shown++;
        }
        _countLabel.Text = $"{shown}/{_settings.Workspaces.Count} project";

        if (_grid.Rows.Count > 0)
        {
            // Giữ dòng đang chọn nếu còn; không thì chọn dòng đầu (gõ lọc xong Enter là chọn luôn).
            var keep = current is null ? null : _grid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => ReferenceEquals(r.Tag, current));
            var row = keep ?? _grid.Rows[0];
            row.Selected = true;
            _grid.CurrentCell = row.Cells[0];
        }
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
            _recentPanel.Controls.Add(b);
        }
    }

    // ---- Thao tác ---------------------------------------------------------------------------------

    private void FilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; Pick(); return; }
        if (e.KeyCode is Keys.Down or Keys.Up && _grid.Rows.Count > 0)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            var i = _grid.CurrentRow?.Index ?? -1;
            i = Math.Max(0, Math.Min(_grid.Rows.Count - 1, i + (e.KeyCode == Keys.Down ? 1 : -1)));
            _grid.Rows[i].Selected = true;
            _grid.CurrentCell = _grid.Rows[i].Cells[0];
        }
    }

    private Workspace? Selected => _grid.CurrentRow?.Tag as Workspace;

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
        BuildRecent();
        Reload();
    }

    private void NewProject() => OpenEditor(startNew: true);

    private void EditProject()
    {
        if (Selected is not { } w) return;
        OpenEditor(select: w);
    }

    private void DeleteProject()
    {
        if (Selected is not { } w) return;
        if (MessageBox.Show(this, $"Xoá project \"{IdOf(w)}\" khỏi danh sách?", "Bcode — Projects",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _settings.Workspaces.Remove(w);
        _settings.RecentWorkspaces.Remove(w.Name);
        _settings.Save();
        BuildRecent();
        Reload();
    }

    private void CopyInfo()
    {
        if (Selected is not { } w) return;
        // Không đưa mật khẩu vào thông tin copy.
        var text = string.Join(Environment.NewLine, new[]
        {
            $"ID: {IdOf(w)}", $"Server: {w.Server}", $"Sys Data: {w.SysDatabase}", $"App Data: {w.AppDatabase}",
            $"WLoginLink: {w.LoginWLink}", $"Program Path: {w.ProgramPath}", $"Source Path: {w.SourcePath}",
            $"Mobile Path: {w.MobilePath}", $"Working Path: {w.WorkingPath}",
        });
        try { Clipboard.SetText(text); } catch { /* clipboard đang bận */ }
    }
}
