using Bcode.App.Services;
using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Lịch sử SQL" ở chế độ App/WinForms (thay trang queryhistory.html): tab Lịch sử (các lần đã lưu) / ★ Đã ghim, tìm toàn văn, lọc theo dự án và thẻ; bên trái danh sách,
/// bên phải xem nguyên câu SQL (tô màu) + tham số lần chạy + nút: Mở trong tab SQL, Mở bản lưu gần nhất (mang tham số lần trước), Nhãn/thẻ, Ghim, Chép, Bỏ ghim, Xoá các mục đã tick.
/// Dữ liệu do <see cref="QueryHistoryService"/> giữ (đọc nền lúc tab mở lần đầu). Mở lại chỉ ĐẶT script vào tab SQL mới, không tự chạy.
/// </summary>
public sealed class NativeQueryHistoryControl : UserControl
{
    private const int PageSize = 200;

    private readonly QueryHistoryService _service = QueryHistoryService.Instance;
    private readonly Func<string> _currentWorkspace;
    private readonly PillButton _tRuns, _tPins, _bDel, _bAllChk;
    private readonly RoundedInput _q = new() { PlaceholderText = "Tìm toàn văn trong câu SQL, nhãn, thẻ…", Width = NativeSkin.S(360) };
    private readonly DropDownButton _ws = new() { Width = NativeSkin.S(170) }, _tag = new() { Width = NativeSkin.S(150) };
    private readonly Label _status = NK.Muted("");
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill };
    private readonly Scintilla _sql = new() { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
    private readonly Label _head = new() { Dock = DockStyle.Top, Height = NativeSkin.S(30), Padding = new Padding(10, 7, 0, 0) };
    private readonly FlowLayoutPanel _params = new() { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(8, 4, 8, 0) };
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(8, 6, 8, 6) };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 160 };
    private readonly HashSet<string> _ticked = new();
    private List<object> _items = new();      // QueryRun hoặc QueryPin theo thứ tự đang hiện
    private string _tab = "runs";
    private int _version;
    private bool _ready, _loading;
    private List<string> _wsList = new(), _tagList = new();

    /// <summary>(script, dùng Sys Data, tiêu đề tab).</summary>
    public event Action<string, bool, string>? OpenSqlRequested;

    public NativeQueryHistoryControl(Func<string> currentWorkspace)
    {
        _currentWorkspace = currentWorkspace;
        Dock = DockStyle.Fill;

        var bar = new BarFlow { Dock = DockStyle.Top, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 6, 8, 6) };
        _tRuns = NK.Button("Lịch sử", () => SetTab("runs")); _tPins = NK.Button("★ Đã ghim", () => SetTab("pins"));
        _bAllChk = NK.Button("Tick tất cả", TickAll, tip: "Tick / bỏ tick tất cả mục đang hiện");
        _bDel = NK.Button("Xoá đã tick (0)", DeleteTicked, tip: "Xoá các mục đã tick"); _bDel.Enabled = false;
        foreach (var d in new[] { _ws, _tag }) { d.Height = NativeSkin.RowHeight(d, 26); d.OnPanelAlt = true; d.Margin = new Padding(0, 0, 6, 0); }
        _ws.SetItems(new[] { "Mọi dự án" }, 0); _tag.SetItems(new[] { "Mọi thẻ" }, 0);
        _ws.SelectionChanged += _ => Search(); _tag.SelectionChanged += _ => Search();
        _q.Margin = new Padding(6, 0, 6, 0);
        bar.Controls.AddRange(new Control[] { _tRuns, _tPins, _q, _ws, _tag, _status, _bAllChk, _bDel });
        _q.TextChanged += (_, _) => Search();

        // ---- danh sách ----
        _grid.ReadOnly = false; _grid.AllowUserToAddRows = false; _grid.RowHeadersVisible = false; _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _grid.MultiSelect = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; _grid.BorderStyle = System.Windows.Forms.BorderStyle.None; _grid.AllowUserToResizeRows = false;
        _grid.CurrentCellDirtyStateChanged += (_, _) => { if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_loading || e.RowIndex < 0 || e.ColumnIndex != 0 || _tab != "runs" || _grid.Rows[e.RowIndex].Tag is not string id) return;
            if (_grid.Rows[e.RowIndex].Cells[0].Value is true) _ticked.Add(id); else _ticked.Remove(id);
            UpdDel();
        };
        _grid.SelectionChanged += (_, _) => ShowCurrent();
        _grid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) OpenCurrent(false); };

        // ---- chi tiết ----
        SqlScintilla.Configure(_sql); _sql.ReadOnly = true; _sql.Margins[0].Width = 0;
        var detail = new Panel { Dock = DockStyle.Fill };
        detail.Controls.Add(_sql); detail.Controls.Add(_params); detail.Controls.Add(_actions); detail.Controls.Add(_head);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_grid); split.Panel2.Controls.Add(detail);
        Controls.Add(split); Controls.Add(bar);

        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = SearchAsync(); };
        HandleCreated += (_, _) => BeginInvoke(new Action(async () =>
        {
            try { split.SplitterDistance = Math.Max(300, (int)(Width * 0.42)); } catch { }
            SqlScintilla.ApplyTheme(_sql); _sql.Margins[0].Width = 0;
            await _service.EnsureLoadedAsync();
            _wsList = _service.Workspaces(); var cur = _currentWorkspace();
            if (cur.Length > 0 && !_wsList.Contains(cur)) _wsList.Add(cur);
            FillWs(""); FillTags(_service.AllTags());
            _ready = true;
            SetTab("runs");
        }));
        ThemeManager.ThemeChanged += () => { SqlScintilla.ApplyTheme(_sql); _sql.Margins[0].Width = 0; };
        Disposed += (_, _) => _debounce.Dispose();
    }

    private void FillWs(string selected)
    {
        var items = new List<string> { "Mọi dự án" }; items.AddRange(_wsList);
        _ws.SetItems(items, Math.Max(0, items.IndexOf(selected)));
    }

    private void FillTags(List<string> tags)
    {
        _tagList = tags;
        var cur = TagFilter();
        var items = new List<string> { "Mọi thẻ" }; items.AddRange(tags);
        _tag.SetItems(items, Math.Max(0, items.IndexOf(cur)));
    }

    private string WsFilter() => _ws.SelectedIndex <= 0 ? "" : _wsList[_ws.SelectedIndex - 1];
    private string TagFilter() => _tag.SelectedIndex <= 0 || _tag.SelectedIndex - 1 >= _tagList.Count ? "" : _tagList[_tag.SelectedIndex - 1];

    private void SetTab(string tab)
    {
        _tab = tab;
        _tRuns.IsPrimary = tab == "runs"; _tPins.IsPrimary = tab == "pins"; _tRuns.Invalidate(); _tPins.Invalidate();
        _bAllChk.Visible = _bDel.Visible = tab == "runs";
        BuildColumns();
        Search();
    }

    private void BuildColumns()
    {
        _grid.Columns.Clear();
        if (_tab == "runs")
        {
            _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "ck", HeaderText = "✓", Width = NativeSkin.S(34) });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "t", HeaderText = "Thời gian / nhãn", ReadOnly = true, Width = NativeSkin.S(190) });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "tags", HeaderText = "Thẻ", ReadOnly = true, Width = NativeSkin.S(110) });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "db", HeaderText = "DB", ReadOnly = true, Width = NativeSkin.S(70) });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ws", HeaderText = "Dự án", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 80 });
        }
        else
        {
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "t", HeaderText = "★ Nhãn", ReadOnly = true, Width = NativeSkin.S(220) });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "tags", HeaderText = "Thẻ", ReadOnly = true, Width = NativeSkin.S(130) });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "ws", HeaderText = "Dự án", ReadOnly = true, Width = NativeSkin.S(90) });
            _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "at", HeaderText = "Ngày ghim", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 90 });
        }
    }

    private void Search() { if (!_ready) return; _debounce.Stop(); _debounce.Start(); }

    private async Task SearchAsync()
    {
        var version = ++_version;
        var tab = _tab; var text = _q.Text; var ws = WsFilter(); var tag = TagFilter();
        var items = await Task.Run<List<object>>(() =>
        {
            if (tab == "pins")
                return _service.SearchPins(text, string.IsNullOrEmpty(ws) ? null : ws).Where(p => tag.Length == 0 || p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).Cast<object>().ToList();
            return _service.SearchRuns(text, string.IsNullOrEmpty(ws) ? null : ws, tag.Length == 0 ? null : tag, PageSize).Cast<object>().ToList();
        });
        if (version != _version || IsDisposed) return;
        _items = items;
        Render();
    }

    private static string Fmt(DateTime d) => d.ToString("dd/MM/yyyy HH:mm");

    private void Render()
    {
        _loading = true;
        try
        {
            _grid.Rows.Clear();
            foreach (var it in _items)
            {
                if (it is QueryRun r)
                {
                    var i = _grid.Rows.Add(_ticked.Contains(r.Id), r.Label.Length > 0 ? "🏷 " + r.Label : Fmt(r.At), string.Join(", ", r.Tags), r.Sys ? "Sys Data" : "App Data", r.Workspace);
                    _grid.Rows[i].Tag = r.Id;
                }
                else if (it is QueryPin p)
                {
                    var i = _grid.Rows.Add("★ " + p.Label, string.Join(", ", p.Tags), p.Workspace ?? "", Fmt(p.Created));
                    _grid.Rows[i].Tag = p.Id;
                }
            }
        }
        finally { _loading = false; }
        _status.Text = _items.Count > 0 ? (_tab == "runs" ? $"{_items.Count} mục lịch sử" : $"{_items.Count} câu ghim") : "";
        UpdDel();
        if (_items.Count == 0)
        {
            _head.Text = _tab == "pins" ? "Chưa có câu nào được ghim. Vào tab Lịch sử, chọn mục rồi bấm “★ Ghim”." : "Chưa có mục nào. Ở tab SQL Query, bấm “Lưu lịch sử” để lưu script vào đây.";
            _sql.ReadOnly = false; _sql.Text = ""; _sql.ReadOnly = true; _actions.Controls.Clear(); _params.Controls.Clear();
        }
        ShowCurrent();
    }

    private void UpdDel() { _bDel.Text = $"Xoá đã tick ({_ticked.Count})"; _bDel.Enabled = _ticked.Count > 0; _bDel.Invalidate(); }

    private object? Current => _grid.CurrentRow is { } row && row.Index >= 0 && row.Index < _items.Count ? _items[row.Index] : null;

    private void ShowCurrent()
    {
        if (_loading) return;
        _actions.Controls.Clear(); _params.Controls.Clear();
        string sql;
        switch (Current)
        {
            case QueryRun r:
                _head.Text = (r.Label.Length > 0 ? "🏷 " + r.Label + "   ·   " : "") + Fmt(r.At) + "   ·   " + (r.Sys ? "Sys Data" : "App Data") + "   ·   " + r.Workspace;
                sql = r.Sql;
                _actions.Controls.Add(NK.Button("Mở trong tab SQL", () => OpenCurrent(false), primary: true));
                _actions.Controls.Add(NK.Button("Mở bản lưu gần nhất", () => OpenCurrent(true), tip: "Lấy lần lưu gần nhất của cùng câu này (chỉ khác giá trị DECLARE/SET)"));
                _actions.Controls.Add(NK.Button("🏷 Nhãn / thẻ", () => TagRun(r)));
                _actions.Controls.Add(NK.Button("★ Ghim", () => PinRun(r)));
                _actions.Controls.Add(NK.Button("Chép", () => NK.CopyText(r.Sql)));
                AddParams(QueryHistoryService.Params(r.Sql));
                break;
            case QueryPin p:
                _head.Text = "★ " + p.Label + (p.Tags.Count > 0 ? "   ·   " + string.Join(", ", p.Tags) : "") + (p.Workspace is { Length: > 0 } ? "   ·   " + p.Workspace : "");
                sql = p.Sql;
                var hasLast = _service.LastRunOf(p.Skeleton) is not null;
                _actions.Controls.Add(NK.Button("Mở trong tab SQL", () => OpenCurrent(false), primary: true));
                var last = NK.Button("Mở với tham số lần trước", () => OpenCurrent(true)); last.Enabled = hasLast; _actions.Controls.Add(last);
                _actions.Controls.Add(NK.Button("Sửa nhãn / thẻ", () => EditPin(p)));
                _actions.Controls.Add(NK.Button("Chép", () => NK.CopyText(p.Sql)));
                _actions.Controls.Add(NK.Button("Bỏ ghim", () => { _service.DeletePin(p.Id); Search(); }));
                AddParams(QueryHistoryService.Params(p.Sql));
                break;
            default: return;
        }
        _sql.ReadOnly = false; _sql.Text = sql; _sql.ReadOnly = true; _sql.GotoPosition(0);
    }

    private void AddParams(List<QueryParam> list)
    {
        if (list.Count == 0) return;
        foreach (var p in list.Take(8))
            _params.Controls.Add(new Label { Text = $"{p.Name} = {p.Value}", AutoSize = true, ForeColor = AppColors.Accent, BackColor = Color.Transparent, Margin = new Padding(0, 2, 12, 2) });
        if (list.Count > 8) _params.Controls.Add(NK.Muted("…"));
    }

    // =====================================================================  thao tác

    private void OpenCurrent(bool last)
    {
        switch (Current)
        {
            case QueryPin pin:
            {
                var run = last ? _service.LastRunOf(pin.Skeleton, _currentWorkspace()) : null;   // "Chạy lại với tham số lần trước": lần chạy gần nhất cùng bộ khung; chưa từng chạy thì dùng chính câu ghim
                OpenSqlRequested?.Invoke(run?.Sql ?? pin.Sql, run?.Sys ?? false, "★ " + pin.Label);
                break;
            }
            case QueryRun run:
            {
                var use = last ? _service.LastRunOf(run.Skeleton, _currentWorkspace()) ?? run : run;
                OpenSqlRequested?.Invoke(use.Sql, use.Sys, "SQL " + use.At.ToString("dd/MM HH:mm"));
                break;
            }
        }
    }

    private void TickAll()
    {
        var all = _items.OfType<QueryRun>().All(r => _ticked.Contains(r.Id)) && _items.Count > 0;
        foreach (var r in _items.OfType<QueryRun>()) { if (all) _ticked.Remove(r.Id); else _ticked.Add(r.Id); }
        Render();
    }

    private void DeleteTicked()
    {
        var ids = _ticked.ToList();
        if (ids.Count == 0) return;
        if (MessageBox.Show(FindForm(), $"Xoá {ids.Count} mục đã tick khỏi Lịch sử SQL?", "Bcode — Lịch sử SQL", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        _service.DeleteRuns(ids);
        foreach (var id in ids) _ticked.Remove(id);
        FillTags(_service.AllTags());
        Search();
    }

    private void TagRun(QueryRun r)
    {
        using var dlg = new PinDialog("Gắn nhãn / thẻ cho mục lịch sử", r.Label, r.Tags, _tagList.Count > 0 ? _tagList : _wsList, null, showWs: false);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        _service.UpdateRun(r.Id, dlg.LabelText, dlg.Tags);
        _wsList = _service.Workspaces(); FillTags(_service.AllTags());
        Search();
    }

    private void PinRun(QueryRun r)
    {
        using var dlg = new PinDialog("Ghim câu SQL", "", new List<string>(), _tagList.Count > 0 ? _tagList : _wsList, _currentWorkspace(), showWs: true);
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        _service.SavePin(null, dlg.LabelText.Length > 0 ? dlg.LabelText : FirstLine(r.Sql), dlg.Tags, dlg.ThisWs ? _currentWorkspace() : null, r.Sql);
        _wsList = _service.Workspaces(); FillTags(_service.AllTags());
        SetStatus("Đã ghim.");
    }

    private void EditPin(QueryPin p)
    {
        using var dlg = new PinDialog("Sửa câu đã ghim", p.Label, p.Tags, _tagList.Count > 0 ? _tagList : _wsList, _currentWorkspace(), showWs: true, thisWs: !string.IsNullOrEmpty(p.Workspace));
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        _service.SavePin(p.Id, dlg.LabelText.Length > 0 ? dlg.LabelText : FirstLine(p.Sql), dlg.Tags, dlg.ThisWs ? _currentWorkspace() : null, p.Sql);
        _wsList = _service.Workspaces(); FillTags(_service.AllTags());
        Search();
    }

    private void SetStatus(string text) => _status.Text = text;

    private static string FirstLine(string sql)
    {
        var line = sql.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("--")) ?? "Câu SQL";
        return line.Length > 60 ? line[..60] + "…" : line;
    }
}

/// <summary>Hộp nhập nhãn + thẻ (+ "chỉ hiện ở dự án X") để ghim câu SQL hoặc gắn nhãn cho mục lịch sử.</summary>
internal sealed class PinDialog : ThemedForm
{
    private readonly RoundedInput _label = NK.Input("vd: Số dư công nợ theo khách"), _tags = NK.Input("vd: KOG, báo cáo công nợ");
    private readonly CheckBox _ws = NK.Check("");

    public string LabelText => _label.Text.Trim();
    public List<string> Tags => _tags.Text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    public bool ThisWs => _ws.Checked;

    public PinDialog(string title, string label, IEnumerable<string> tags, IEnumerable<string> suggestions, string? workspace, bool showWs, bool thisWs = false)
    {
        Text = title; Width = NativeSkin.S(560); Height = showWs ? 330 : 280; StartPosition = FormStartPosition.CenterParent; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowIcon = false;
        _label.Text = label; _tags.Text = string.Join(", ", tags);
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14, 10, 14, 10), AutoScroll = true };
        _label.Width = NativeSkin.S(510); _tags.Width = NativeSkin.S(510);
        body.Controls.Add(NK.Muted("Nhãn (tên dễ nhớ)")); body.Controls.Add(_label);
        body.Controls.Add(NK.Muted("Thẻ (cách nhau dấu phẩy — dự án, báo cáo, việc…)")); body.Controls.Add(_tags);
        var sugg = new FlowLayoutPanel { AutoSize = true, MaximumSize = new Size(510, 0), WrapContents = true };
        foreach (var s in suggestions.Take(10))
        {
            var t = s;
            sugg.Controls.Add(NK.Button(t, () => { var v = _tags.Text.Trim(); _tags.Text = v.Length > 0 ? v.TrimEnd(',', ';', ' ') + ", " + t : t; }));
        }
        body.Controls.Add(sugg);
        if (showWs)
        {
            _ws.Text = "chỉ hiện ở dự án " + (string.IsNullOrEmpty(workspace) ? "(chưa chọn)" : workspace); _ws.Checked = thisWs; _ws.Enabled = !string.IsNullOrEmpty(workspace);
            body.Controls.Add(_ws);
        }
        var bar = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var ok = NK.Button("Lưu", () => { DialogResult = DialogResult.OK; Close(); }, primary: true); var cancel = NK.Button("Huỷ", () => { DialogResult = DialogResult.Cancel; Close(); });
        bar.Controls.Add(ok); bar.Controls.Add(cancel);
        Controls.Add(body); Controls.Add(bar);
        AcceptButton = null; KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
    }
}
