using System.Data;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Lookup" của chế độ App/WinForms — cùng chức năng với <see cref="LookupControl"/> (trang WebView2 lookup.html) nhưng bằng control WinForms:
/// tìm object (Function/Store/Table/View/Trigger) theo tên ở App/Sys/cả hai, xem định nghĩa trong Scintilla (tô màu SQL), tìm theo NỘI DUNG
/// (dùng lại <see cref="SqlDefinitionIndex"/>), ba chế độ khung dưới "Show Search Text / Show Data / Show History" (chọn một trong ba), "Mở trong Tab".
/// Dùng đúng cùng các service nên kết quả giống bản Web.
/// </summary>
public sealed class NativeLookupControl : UserControl
{
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly TableDataService _tableDataService;

    private readonly List<SqlObjectInfo> _results = new(), _history = new(), _textResults = new();
    private static SqlDefinitionIndex? _defIndex;   // dùng chung giữa các tab Lookup
    private SqlObjectInfo? _currentObj;
    private string _mode = "";
    private int _searchVersion, _textVersion;
    private string _defText = "";
    private bool _fromText;

    private readonly DropDownButton _db = new();
    private readonly RoundedInput _q = new() { PlaceholderText = "Search Text (tên object)..." };
    private readonly RoundedInput _tq = new() { PlaceholderText = "Search nội dung trong Function / Store / View / Trigger…" };
    private readonly CheckBox _kFn = new() { Text = "Function", Checked = true, AutoSize = true }, _kProc = new() { Text = "Store", Checked = true, AutoSize = true },
        _kTable = new() { Text = "Table", Checked = true, AutoSize = true }, _kView = new() { Text = "View", Checked = true, AutoSize = true }, _kTrig = new() { Text = "Trigger", Checked = true, AutoSize = true };
    private readonly ThemedListBox _list = new() { Dock = DockStyle.Fill }, _tlist = new() { Dock = DockStyle.Fill }, _bottomList = new() { Dock = DockStyle.Fill };
    private readonly Label _tst = new() { AutoSize = false, Dock = DockStyle.Top, Height = NativeSkin.S(22), Padding = new Padding(6, 3, 6, 0) };
    private readonly CheckBox _mSearch = new() { Text = "Show Search Text", AutoSize = true }, _mData = new() { Text = "Show Data", AutoSize = true }, _mHist = new() { Text = "Show History", AutoSize = true };
    private readonly Scintilla _pv = new() { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
    private readonly Label _nm = new MutedLabel { AutoSize = true, Margin = new Padding(10, 8, 0, 0), BackColor = Color.Transparent };
    private readonly Panel _bottom = new() { Dock = DockStyle.Bottom, Visible = false };
    private readonly DataGridView _dataGrid = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = NativeSkin.S(22), Padding = new Padding(8, 3, 8, 0) };
    private readonly System.Windows.Forms.Timer _qTimer = new() { Interval = 300 }, _tqTimer = new() { Interval = 350 };

    public event Action<SqlObjectInfo>? OpenInTabRequested;

    public NativeLookupControl(SqlObjectBrowserService sqlObjectService, TableDataService tableDataService)
    {
        _sqlObjectService = sqlObjectService;
        _tableDataService = tableDataService;
        Dock = DockStyle.Fill;

        // ---- cột trái ----
        _db.SetItems(new[] { "App Data", "Sys Data", "Cả hai (App + Sys)" }, 2);
        _db.Dock = DockStyle.Top; _db.Height = NativeSkin.RowHeight(this, 28);
        _db.SelectionChanged += _ => { Search(); TextSearch(); };
        _q.Dock = DockStyle.Top;
        var kinds = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 4, 0, 0) };
        kinds.Controls.AddRange(new Control[] { _kFn, _kProc, _kTable, _kView, _kTrig });
        foreach (var c in new[] { _kFn, _kProc, _kTable, _kView, _kTrig }) c.CheckedChanged += (_, _) => { Search(); TextSearch(); };
        var filters = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 8, 8, 6) };
        filters.Controls.Add(kinds); filters.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 5 }); filters.Controls.Add(_q); filters.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 5 }); filters.Controls.Add(_db);

        var tsec = new Panel { Dock = DockStyle.Bottom, Height = NativeSkin.S(260), Padding = new Padding(0, 4, 0, 0) };
        var tq = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 2) };
        _tq.Dock = DockStyle.Top;
        tq.Controls.Add(_tq);
        tsec.Controls.Add(_tlist); tsec.Controls.Add(_tst); tsec.Controls.Add(tq);
        _tst.Text = "Gõ ≥ 2 ký tự — tìm trong cache nội dung.";

        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_list); left.Controls.Add(tsec); left.Controls.Add(filters);

        // ---- cột phải ----
        var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 4, 8, 4) };
        var bTab = PillButton.Flat("Mở trong Tab"); bTab.Margin = new Padding(10, 0, 0, 0); bTab.Click += (_, _) => { if (_currentObj is { } o) OpenInTabRequested?.Invoke(o); };
        tools.Controls.AddRange(new Control[] { _mSearch, _mData, _mHist, bTab, _nm });
        foreach (var m in new[] { _mSearch, _mData, _mHist })
        {
            var self = m;
            m.CheckedChanged += (_, _) =>
            {
                if (!self.Checked) { if (!_mSearch.Checked && !_mData.Checked && !_mHist.Checked) SetMode(""); return; }
                foreach (var o in new[] { _mSearch, _mData, _mHist }) if (o != self) o.Checked = false;
                SetMode(self == _mSearch ? "search" : self == _mData ? "data" : "history");
            };
        }
        SqlScintilla.Configure(_pv);
        _pv.ReadOnly = true;
        _pv.Margins[0].Width = 0;
        _bottom.Controls.Add(_dataGrid); _bottom.Controls.Add(_bottomList);
        _dataGrid.ReadOnly = true; _dataGrid.AllowUserToAddRows = false;
        ResultGridMenu.Attach(_dataGrid);
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_pv); right.Controls.Add(_bottom); right.Controls.Add(tools);

        var split = new SplitContainer { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        Controls.Add(split); Controls.Add(_status);
        HandleCreated += (_, _) => BeginInvoke(new Action(() =>
        {
            try { split.SplitterDistance = Math.Max(200, (int)(Width * 0.3)); _bottom.Height = Math.Max(80, (int)(Height * 0.3)); SqlScintilla.ApplyTheme(_pv); _pv.Margins[0].Width = 0; } catch { }
            Search();
        }));
        ThemeManager.ThemeChanged += OnTheme;
        Disposed += (_, _) => { ThemeManager.ThemeChanged -= OnTheme; _qTimer.Dispose(); _tqTimer.Dispose(); };

        // ---- sự kiện ----
        _qTimer.Tick += (_, _) => { _qTimer.Stop(); _ = SearchAsync(); };
        _tqTimer.Tick += (_, _) => { _tqTimer.Stop(); _ = TextSearchAsync(); };
        _q.TextChanged += (_, _) => { _qTimer.Stop(); _qTimer.Start(); };
        _tq.TextChanged += (_, _) => { _tqTimer.Stop(); _tqTimer.Start(); };
        _tq.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; _tqTimer.Stop(); _ = TextSearchAsync(); } };
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _results.Count) { _fromText = false; _ = LoadObjectAsync(_results[_list.SelectedIndex]); } };
        _tlist.SelectedIndexChanged += (_, _) => { if (_tlist.SelectedIndex >= 0 && _tlist.SelectedIndex < _textResults.Count) { _fromText = true; _ = LoadObjectAsync(_textResults[_tlist.SelectedIndex]); } };
        _bottomList.SelectedIndexChanged += (_, _) => OnBottomPick();
        SetStatus("", false);
    }

    private void OnTheme() { SqlScintilla.ApplyTheme(_pv); _pv.Margins[0].Width = 0; }

    private void Search() { _qTimer.Stop(); _qTimer.Start(); }
    private void TextSearch() { if (_tq.Text.Trim().Length >= 2) { _tqTimer.Stop(); _tqTimer.Start(); } }

    private void SetStatus(string text, bool error)
    {
        _status.Text = text;
        _status.ForeColor = error ? AppColors.Danger : AppColors.TextMuted;
    }

    private static string KindLabel(SqlObjectKind k) => k switch
    {
        SqlObjectKind.Table => "Table", SqlObjectKind.View => "View", SqlObjectKind.StoredProcedure => "Store", SqlObjectKind.Trigger => "Trigger", _ => "Function",
    };

    private static string Line(SqlObjectInfo o, int? hits = null) => $"{(o.FromSysDatabase ? "[Sys]" : "[App]")} {o.QualifiedName}  ({KindLabel(o.Kind)}){(hits is { } n ? $"  {n}×" : "")}";

    private HashSet<SqlObjectKind> Kinds(bool includeTable)
    {
        var k = new HashSet<SqlObjectKind>();
        if (_kFn.Checked) k.Add(SqlObjectKind.Function);
        if (_kProc.Checked) k.Add(SqlObjectKind.StoredProcedure);
        if (includeTable && _kTable.Checked) k.Add(SqlObjectKind.Table);
        if (_kView.Checked) k.Add(SqlObjectKind.View);
        if (_kTrig.Checked) k.Add(SqlObjectKind.Trigger);
        return k;
    }

    private bool[] DbChoices() => _db.SelectedIndex switch { 0 => new[] { false }, 1 => new[] { true }, _ => new[] { false, true } };

    private async Task SearchAsync()
    {
        var version = ++_searchVersion;
        var filter = _q.Text.Trim();
        var kinds = Kinds(true);
        SetStatus("Đang tìm...", false);
        try
        {
            var results = new List<SqlObjectInfo>();
            foreach (var useSys in DbChoices())
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys, filter.Length == 0 ? null : filter);
                results.AddRange(objs.Where(o => kinds.Contains(o.Kind)));
            }
            if (version != _searchVersion || IsDisposed) return;
            _results.Clear();
            _results.AddRange(results.OrderBy(o => o.Kind).ThenBy(o => o.QualifiedName, StringComparer.OrdinalIgnoreCase));
            _list.SetItemsSmooth(_results.Select(o => Line(o)).ToList());   // không đổi thì không nạp lại (hết chớp khi lọc / bấm lại)
            SetStatus($"{_results.Count} object.", false);
        }
        catch (Exception) { SetStatus("Lỗi (chưa kết nối?).", true); }
    }

    private async Task TextSearchAsync()
    {
        var version = ++_textVersion;
        var term = _tq.Text.Trim();
        if (term.Length < 2) { _tlist.Items.Clear(); _tst.Text = "Gõ ≥ 2 ký tự — tìm trong cache nội dung."; return; }
        var kinds = Kinds(false);
        var index = _defIndex ??= new SqlDefinitionIndex(_sqlObjectService.Connections);
        try
        {
            var hits = new List<SqlDefinitionIndex.Hit>();
            var progress = new Progress<string>(s => { if (version == _textVersion) _tst.Text = s; });
            var reloaded = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var useSys in DbChoices())
            {
                var (n, _) = await index.SyncAsync(useSys, progress);
                reloaded += n;
                hits.AddRange(index.Search(useSys, term, kinds));
            }
            if (version != _textVersion || IsDisposed) return;
            hits = hits.OrderBy(h => h.Info.Kind).ThenBy(h => h.Info.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList();
            _textResults.Clear();
            _textResults.AddRange(hits.Select(h => h.Info));
            _tlist.SetItemsSmooth(hits.Select(h => Line(h.Info, h.Matches)).ToList());
            _tst.Text = $"{hits.Count} object chứa \"{term}\" · {sw.ElapsedMilliseconds} ms" + (reloaded > 0 ? $" · vừa cập nhật {reloaded} object" : " · từ cache");
        }
        catch (Exception ex) { if (version == _textVersion) _tst.Text = "Lỗi: " + ex.Message; }
    }

    private void SetPreview(string text)
    {
        _pv.ReadOnly = false;
        _pv.Text = text;
        _pv.ReadOnly = true;
        _pv.GotoPosition(0);
    }

    private async Task LoadObjectAsync(SqlObjectInfo info)
    {
        _currentObj = info;
        _nm.Text = info.QualifiedName;
        SetPreview("-- Đang tải...");
        try
        {
            var def = await _sqlObjectService.GetDefinitionAsync(info);
            if (_currentObj != info) return;   // đã chọn object khác trong lúc tải
            _defText = def ?? "";
            SetPreview(_defText);
            _history.RemoveAll(h => h.FromSysDatabase == info.FromSysDatabase && h.QualifiedName.Equals(info.QualifiedName, StringComparison.OrdinalIgnoreCase));
            _history.Insert(0, info);
            if (_history.Count > 50) _history.RemoveAt(_history.Count - 1);
            var tt = _tq.Text.Trim().ToLowerInvariant();
            if (_fromText && tt.Length > 0)
            {
                var n = _defText.Split('\n').ToList().FindIndex(l => l.ToLowerInvariant().Contains(tt));
                if (n >= 0) JumpToLine(n);
            }
            await RefreshBottomAsync();
        }
        catch (Exception ex) { SetPreview("-- Lỗi: " + ex.Message); }
    }

    private void JumpToLine(int n)
    {
        if (n < 0 || n >= _pv.Lines.Count) return;
        var l = _pv.Lines[n];
        l.Goto();
        _pv.SetSelection(l.Position, l.EndPosition);
    }

    // ---- khung dưới: Show Search Text / Show Data / Show History ----
    private List<int> _lineHits = new();

    private void SetMode(string mode)
    {
        _mode = mode;
        _bottom.Visible = mode != "";
        _ = RefreshBottomAsync();
    }

    private async Task RefreshBottomAsync()
    {
        _dataGrid.Visible = _mode == "data";
        _bottomList.Visible = _mode != "data";
        switch (_mode)
        {
            case "search":
            {
                var term = (_tq.Text.Trim().Length > 0 ? _tq.Text.Trim() : _q.Text.Trim()).ToLowerInvariant();
                _lineHits = new();
                _bottomList.Items.Clear();
                if (term.Length == 0) { _bottomList.Items.Add("Nhập từ khoá ở ô Search để xem các dòng khớp."); return; }
                var lines = _defText.Split('\n');
                for (var i = 0; i < lines.Length; i++)
                    if (lines[i].ToLowerInvariant().Contains(term)) { _lineHits.Add(i); _bottomList.Items.Add($"Dòng {i + 1}: {lines[i].Trim()}"); }
                if (_lineHits.Count == 0) _bottomList.Items.Add("Không có dòng nào khớp.");
                break;
            }
            case "history":
                _bottomList.Items.Clear();
                _bottomList.Items.AddRange(_history.Select(o => (object)Line(o)).ToArray());
                if (_history.Count == 0) _bottomList.Items.Add("Chưa xem object nào.");
                break;
            case "data":
                if (_currentObj is not { Kind: SqlObjectKind.Table } obj) { _dataGrid.DataSource = null; SetStatus("Show Data chỉ dùng cho Table.", false); return; }
                try
                {
                    var table = await _tableDataService.LoadTableAsync(obj.FromSysDatabase, obj.Schema, obj.Name, 100);
                    GridDisplayHelper.BindOptimized(_dataGrid, table);
                    SetStatus($"{table?.Rows.Count ?? 0} dòng (Top 100).", false);
                }
                catch (Exception ex) { SetStatus("Show Data: " + ex.Message, true); }
                break;
        }
    }

    private void OnBottomPick()
    {
        var i = _bottomList.SelectedIndex;
        if (i < 0) return;
        if (_mode == "search") { if (i < _lineHits.Count) JumpToLine(_lineHits[i]); }
        else if (_mode == "history" && i < _history.Count) { _fromText = false; _ = LoadObjectAsync(_history[i]); }
    }
}
