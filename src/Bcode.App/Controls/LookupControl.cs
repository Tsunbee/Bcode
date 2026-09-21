using Bcode.App.UI;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Lookup" tool — searches SQL objects (Function/Store/Table/View/Trigger) by
/// name, previews the selected object's definition, and can additionally show
/// a data sample (for tables) or a "where does the search text appear in this
/// definition" list, plus a session History of recently viewed objects.
///
/// Rebuilt per the user's corrected screenshot (superseding the earlier simple
/// key-value LookupForm/LookupService, which only searched one table's rows by
/// one column — kept in the project but no longer wired into MainForm).
///
/// A few specifics of the "ShowSearchText/Show Data/Show History" toggles were
/// not fully visible in the screenshot, so this is an assumption documented in
/// README: the three checkboxes act like radio buttons selecting what shows in
/// the panel under the definition preview — matches within the definition text,
/// a Top-100 data sample (tables only), or the history list.
/// </summary>
public class LookupControl : UserControl
{
    private readonly ComboBox _dbCombo;
    private readonly TextBox _searchBox;
    private readonly CheckBox _fnCheck;
    private readonly CheckBox _procCheck;
    private readonly CheckBox _tableCheck;
    private readonly CheckBox _viewCheck;
    private readonly CheckBox _triggerCheck;
    private readonly ListBox _objectList;
    private readonly RichTextBox _previewBox;
    private readonly CheckBox _showSearchTextCheck;
    private readonly CheckBox _showDataCheck;
    private readonly CheckBox _showHistoryCheck;
    private readonly Panel _bottomPanel;
    private readonly ListBox _searchMatchesList;
    private readonly DataGridView _dataGridPreview;
    private readonly ListBox _historyList;
    private readonly Button _openInTabButton;
    private readonly Label _statusLabel;

    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly TableDataService _tableDataService;
    private readonly System.Windows.Forms.Timer _searchDebounce = new() { Interval = 350 };

    private readonly List<SqlObjectInfo> _history = new();
    private SqlObjectInfo? _currentObj;
    private bool _syncingCheckboxes;

    public event Action<SqlObjectInfo>? OpenInTabRequested;

    private record ObjEntry(SqlObjectInfo Info)
    {
        public override string ToString() =>
            $"{(Info.FromSysDatabase ? "[Sys]" : "[App]")} {Info.QualifiedName}  ({KindLabel(Info.Kind)})";

        private static string KindLabel(SqlObjectKind k) => k switch
        {
            SqlObjectKind.Table => "Table",
            SqlObjectKind.View => "View",
            SqlObjectKind.StoredProcedure => "Store",
            SqlObjectKind.Trigger => "Trigger",
            _ => "Function"
        };
    }

    public LookupControl(SqlObjectBrowserService sqlObjectService, TableDataService tableDataService)
    {
        _sqlObjectService = sqlObjectService;
        _tableDataService = tableDataService;
        Dock = DockStyle.Fill;

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 320 };

        // ---- Left: filters + object list ----
        var left = new Panel { Dock = DockStyle.Fill };

        _objectList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        _objectList.SelectedIndexChanged += async (_, _) =>
        {
            if (_objectList.SelectedItem is ObjEntry entry) await LoadObjectAsync(entry.Info);
        };

        var filterPanel = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 96, WrapContents = true, Padding = new Padding(4) };

        _dbCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        _dbCombo.Items.AddRange(new object[] { "App Data", "Sys Data", "Cả hai (App + Sys)" });
        _dbCombo.SelectedIndex = 2;
        _dbCombo.SelectedIndexChanged += async (_, _) => await ReloadListAsync();

        _searchBox = new TextBox { Width = 200, PlaceholderText = "Search Text (tên object)..." };
        _searchBox.TextChanged += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Start(); };
        _searchDebounce.Tick += async (_, _) =>
        {
            _searchDebounce.Stop();
            if (IsDisposed) return;
            await ReloadListAsync();
        };
        // Same class of "Cannot access a disposed object" fix as ScriptEditorControl/RawSqlControl —
        // the Timer isn't a child Control so closing this tab wouldn't stop it on its own.
        Disposed += (_, _) => { _searchDebounce.Stop(); _searchDebounce.Dispose(); };

        _fnCheck = new CheckBox { Text = "Function", AutoSize = true, Checked = true };
        _procCheck = new CheckBox { Text = "Store", AutoSize = true, Checked = true };
        _tableCheck = new CheckBox { Text = "Table", AutoSize = true, Checked = true };
        _viewCheck = new CheckBox { Text = "View", AutoSize = true, Checked = true };
        _triggerCheck = new CheckBox { Text = "Trigger", AutoSize = true, Checked = true };
        foreach (var cb in new[] { _fnCheck, _procCheck, _tableCheck, _viewCheck, _triggerCheck })
            cb.CheckedChanged += async (_, _) => await ReloadListAsync();

        filterPanel.Controls.Add(new Label { Text = "DB:", AutoSize = true, Padding = new Padding(0, 6, 2, 0) });
        filterPanel.Controls.Add(_dbCombo);
        filterPanel.Controls.Add(_searchBox);
        filterPanel.Controls.Add(_fnCheck);
        filterPanel.Controls.Add(_procCheck);
        filterPanel.Controls.Add(_tableCheck);
        filterPanel.Controls.Add(_viewCheck);
        filterPanel.Controls.Add(_triggerCheck);

        left.Controls.Add(_objectList);
        left.Controls.Add(filterPanel);
        split.Panel1.Controls.Add(left);

        // ---- Right: preview + toggles + bottom panel ----
        var right = new Panel { Dock = DockStyle.Fill };
        var rightSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 300 };

        _previewBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false,
            ReadOnly = true,
            Font = ThemeManager.MonoFont
        };
        rightSplit.Panel1.Controls.Add(_previewBox);

        _bottomPanel = new Panel { Dock = DockStyle.Fill, Visible = false };
        _searchMatchesList = new ListBox { Dock = DockStyle.Fill, Visible = false };
        _searchMatchesList.DoubleClick += (_, _) => JumpToSelectedMatch();
        _dataGridPreview = new DataGridView
        {
            Dock = DockStyle.Fill,
            Visible = false,
            AllowUserToAddRows = false,
            ReadOnly = true,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect
        };
        _historyList = new ListBox { Dock = DockStyle.Fill, Visible = false };
        _historyList.DoubleClick += async (_, _) =>
        {
            if (_historyList.SelectedItem is ObjEntry entry) await LoadObjectAsync(entry.Info);
        };
        _bottomPanel.Controls.Add(_searchMatchesList);
        _bottomPanel.Controls.Add(_dataGridPreview);
        _bottomPanel.Controls.Add(_historyList);
        rightSplit.Panel2.Controls.Add(_bottomPanel);

        right.Controls.Add(rightSplit);

        var toggleBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false, Padding = new Padding(4, 4, 0, 0) };
        _showSearchTextCheck = new CheckBox { Text = "Show Search Text", AutoSize = true };
        _showDataCheck = new CheckBox { Text = "Show Data", AutoSize = true };
        _showHistoryCheck = new CheckBox { Text = "Show History", AutoSize = true };
        _showSearchTextCheck.CheckedChanged += async (_, _) => await OnToggleChanged(_showSearchTextCheck);
        _showDataCheck.CheckedChanged += async (_, _) => await OnToggleChanged(_showDataCheck);
        _showHistoryCheck.CheckedChanged += async (_, _) => await OnToggleChanged(_showHistoryCheck);
        _openInTabButton = PillButton.Flat("Mở trong Tab");
        _openInTabButton.Click += (_, _) => { if (_currentObj is { } obj) OpenInTabRequested?.Invoke(obj); };

        toggleBar.Controls.Add(_showSearchTextCheck);
        toggleBar.Controls.Add(_showDataCheck);
        toggleBar.Controls.Add(_showHistoryCheck);
        toggleBar.Controls.Add(_openInTabButton);
        right.Controls.Add(toggleBar);

        split.Panel2.Controls.Add(right);

        _statusLabel = new Label { Dock = DockStyle.Bottom, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };

        Controls.Add(split);
        Controls.Add(_statusLabel);

        Load += async (_, _) => await ReloadListAsync();
    }

    private async Task OnToggleChanged(CheckBox source)
    {
        if (_syncingCheckboxes) return;
        _syncingCheckboxes = true;
        try
        {
            if (source.Checked)
            {
                foreach (var cb in new[] { _showSearchTextCheck, _showDataCheck, _showHistoryCheck })
                    if (cb != source) cb.Checked = false;
            }
        }
        finally
        {
            _syncingCheckboxes = false;
        }

        await RefreshBottomPanelAsync();
    }

    private HashSet<SqlObjectKind> SelectedKinds()
    {
        var set = new HashSet<SqlObjectKind>();
        if (_fnCheck.Checked) set.Add(SqlObjectKind.Function);
        if (_procCheck.Checked) set.Add(SqlObjectKind.StoredProcedure);
        if (_tableCheck.Checked) set.Add(SqlObjectKind.Table);
        if (_viewCheck.Checked) set.Add(SqlObjectKind.View);
        if (_triggerCheck.Checked) set.Add(SqlObjectKind.Trigger);
        return set;
    }

    private async Task ReloadListAsync()
    {
        _statusLabel.Text = "Đang tìm...";
        try
        {
            var filter = string.IsNullOrWhiteSpace(_searchBox.Text) ? null : _searchBox.Text.Trim();
            var dbChoices = _dbCombo.SelectedIndex switch { 0 => new[] { false }, 1 => new[] { true }, _ => new[] { false, true } };
            var kinds = SelectedKinds();

            var results = new List<SqlObjectInfo>();
            foreach (var useSys in dbChoices)
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys, filter);
                results.AddRange(objs.Where(o => kinds.Contains(o.Kind)));
            }

            _objectList.Items.Clear();
            foreach (var o in results.OrderBy(o => o.Kind).ThenBy(o => o.QualifiedName, StringComparer.OrdinalIgnoreCase))
                _objectList.Items.Add(new ObjEntry(o));

            _statusLabel.Text = $"{results.Count} object.";
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "Lỗi (chưa kết nối?).";
            _ = ex; // hiển thị lỗi qua status thay vì popup — tránh làm phiền khi gõ tìm liên tục
        }
    }

    private async Task LoadObjectAsync(SqlObjectInfo info)
    {
        _currentObj = info;
        _previewBox.Text = "-- Đang tải...";
        try
        {
            var def = await _sqlObjectService.GetDefinitionAsync(info);
            _previewBox.Text = def;
            SqlSyntaxHighlighter.Apply(_previewBox);
            AddToHistory(info);
            await RefreshBottomPanelAsync();
        }
        catch (Exception ex)
        {
            _previewBox.Text = "-- Lỗi: " + ex.Message;
        }
    }

    private void AddToHistory(SqlObjectInfo info)
    {
        _history.RemoveAll(h => h.FromSysDatabase == info.FromSysDatabase &&
                                 h.QualifiedName.Equals(info.QualifiedName, StringComparison.OrdinalIgnoreCase));
        _history.Insert(0, info);
        if (_history.Count > 50) _history.RemoveAt(_history.Count - 1);

        _historyList.Items.Clear();
        foreach (var h in _history) _historyList.Items.Add(new ObjEntry(h));
    }

    private async Task RefreshBottomPanelAsync()
    {
        _searchMatchesList.Visible = _showSearchTextCheck.Checked;
        _dataGridPreview.Visible = _showDataCheck.Checked;
        _historyList.Visible = _showHistoryCheck.Checked;
        _bottomPanel.Visible = _showSearchTextCheck.Checked || _showDataCheck.Checked || _showHistoryCheck.Checked;

        if (_showSearchTextCheck.Checked)
            RefreshSearchMatches();
        else if (_showDataCheck.Checked)
            await RefreshDataPreviewAsync();
    }

    private void RefreshSearchMatches()
    {
        _searchMatchesList.Items.Clear();
        var term = _searchBox.Text.Trim();
        if (string.IsNullOrEmpty(term)) return;

        var lines = _previewBox.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                _searchMatchesList.Items.Add($"Dòng {i + 1}: {lines[i].Trim()}");
    }

    private void JumpToSelectedMatch()
    {
        if (_searchMatchesList.SelectedItem is not string text) return;
        var prefix = text.Split(':', 2)[0]; // "Dòng N"
        var digits = new string(prefix.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var lineNo)) return;

        var lineIndex = _previewBox.GetFirstCharIndexFromLine(lineNo - 1);
        if (lineIndex < 0) return;
        _previewBox.Select(lineIndex, _previewBox.Lines.ElementAtOrDefault(lineNo - 1)?.Length ?? 0);
        _previewBox.ScrollToCaret();
        _previewBox.Focus();
    }

    private async Task RefreshDataPreviewAsync()
    {
        if (_currentObj is not { Kind: SqlObjectKind.Table } obj)
        {
            _dataGridPreview.DataSource = null;
            return;
        }
        try
        {
            var data = await _tableDataService.LoadTableAsync(obj.FromSysDatabase, obj.Schema, obj.Name, 100);
            _dataGridPreview.DataSource = data;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Lookup (Show Data)", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
