using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Check Include (includecheck.html): so khai báo INCLUDE / IGNORE, options, wcommand của dự án với catalog tính năng từng phiên bản; thêm / sửa / xoá tính năng của riêng mình.</summary>
internal sealed class IncludeCheckPage : NativePage
{
    private sealed class Cond { public string Kind = "", Key = "", Expected = "", Actual = "", State = "unknown", Note = "", Fix = ""; }
    private sealed class Feature
    {
        public string Group = "", Title = "", Version = "", Id = "", Products = "", Mode = ""; public bool Builtin;
        public List<Cond> Conds = new(); public List<string> Guide = new();
        public string Key => Group + "|" + Title;
    }
    private sealed class Result { public string Key = "", Status = "none", StatusText = ""; public List<Cond> Conds = new(); }
    private sealed class FileRow { public string Name = "", State = ""; public List<string> Features = new(); }

    private static readonly Dictionary<string, string> St = new() { ["ok"] = "Đúng khai báo", ["bad"] = "Chưa bật / sai", ["partial"] = "Một phần", ["diff"] = "Khác mặc định", ["dbonly"] = "Chưa kiểm tra DB", ["none"] = "Chỉ hướng dẫn" };
    private static readonly string[] StOrder = { "bad", "partial", "diff", "ok", "dbonly", "none" };
    private static readonly Dictionary<string, string> KindName = new() { ["include"] = "File Include", ["option"] = "Option", ["wcommand"] = "WCommand", ["entity"] = "Entity" };

    private readonly RoundedInput _src = NK.Input("Thư mục source / Program (chứa App_Data\\Controllers)"), _q = NK.Input("Tìm tính năng / ID / file…"), _qi = NK.Input("Tìm file Include…");
    private readonly CheckBox _useDb = NK.Check("Kiểm tra DB (options / wcommand)"), _onlyCat = NK.Check("chỉ file có trong catalog");
    private readonly DropDownButton _fGroup = new() { Width = NativeSkin.S(170) }, _fProd = new() { Width = NativeSkin.S(170) };
    private readonly Label _msg = new() { AutoSize = true, ForeColor = AppColors.Warning, BackColor = Color.Transparent, Visible = false, Margin = new Padding(10, 2, 10, 2) }, _toast = NK.Muted("");
    private readonly FlowLayoutPanel _sum = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 2, 8, 2), BackColor = Color.Transparent };
    private readonly DataGridView _gridF, _gridC, _gridI;
    private readonly Panel _detail = new() { Dock = DockStyle.Fill, Padding = new Padding(10), AutoScroll = true };
    private readonly Label _dTitle = new() { AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 2, FontStyle.Bold) };
    private readonly Label _dMeta = NK.Muted("", true), _dGuide = NK.Muted("", true), _refs = NK.Muted("", true);
    private readonly FlowLayoutPanel _dBtns = new() { AutoSize = true, BackColor = Color.Transparent, Dock = DockStyle.Top };
    private readonly Panel _paneF = new() { Dock = DockStyle.Fill }, _paneI = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly PillButton _tabF, _tabI;
    private readonly BarFlow _fixBar = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 4, 0, 4) };
    private FeatureEditor? _editor;
    private List<Feature> _catalog = new();
    private Dictionary<string, Result> _results = new();
    private List<FileRow> _files = new();
    private List<Feature> _view = new();
    private List<FileRow> _fileView = new();
    private List<Cond> _curConds = new();
    private string _status = "", _sel = "";
    private bool _analyzed, _loading, _busy;

    public IncludeCheckPage()
    {
        _useDb.Checked = true; _src.Width = NativeSkin.S(520); _q.Width = NativeSkin.S(280); _qi.Width = NativeSkin.S(280);
        var head = new BarFlow { Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 2), AutoSize = true, WrapContents = true };
        head.Controls.AddRange(new Control[]
        {
            new Label { Text = "Check Include", AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 2, FontStyle.Bold), Margin = new Padding(0, 4, 10, 0) },
            NK.Muted("So khai báo INCLUDE / IGNORE, options, wcommand với catalog tính năng từng phiên bản"),
            NK.Button("＋ Thêm tính năng", () => OpenEditor(null), tip: "Thêm 1 tính năng mới vào catalog của bạn"),
            NK.Button("Import Excel", () => Post(new { action = "importXlsx" }), tip: "Nạp thêm catalog từ file Excel (FBO_Catalog.xlsx)"),
            NK.Button("📂 Catalog", () => Post(new { action = "openFolder" }), tip: "Thư mục chứa catalog của bạn (file .txt, sửa tay được)"),
        });
        var head2 = new BarFlow { Dock = DockStyle.Top, Padding = new Padding(10, 2, 10, 4), AutoSize = true, WrapContents = true };
        head2.Controls.AddRange(new Control[] { NK.Text("Dự án"), _src, NK.Button("Chọn…", () => Post(new { action = "browse", source = _src.Text })), _useDb, NK.Button("▶ Phân tích", Run, primary: true) });
        _src.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Run(); } };
        _toast.Margin = new Padding(10, 6, 0, 0); head2.Controls.Add(_toast);

        _tabF = NK.Button("Tính năng", () => Tab(true), primary: true); _tabI = NK.Button("File Include", () => Tab(false));
        var tabs = new BarFlow { Dock = DockStyle.Top, Padding = new Padding(8, 2, 8, 2), AutoSize = true };
        tabs.Controls.Add(_tabF); tabs.Controls.Add(_tabI);

        // ---- pane Tính năng ----
        _gridF = NK.Grid(("grp", "Nhóm", 90), ("title", "Tính năng", 0), ("ver", "Phiên bản", 90), ("sp", "SP", 120), ("st", "Trạng thái", 130));
        _gridF.SelectionChanged += (_, _) => { if (_loading || _gridF.CurrentRow is null || _gridF.CurrentRow.Index >= _view.Count) return; _sel = _view[_gridF.CurrentRow.Index].Key; RenderDetail(); };
        _fGroup.SetItems(new[] { "Nhóm: tất cả" }, 0); _fProd.SetItems(new[] { "Sản phẩm: tất cả" }, 0);
        _fGroup.SelectionChanged += _ => RenderList(); _fProd.SelectionChanged += _ => RenderList();
        _q.TextChanged += (_, _) => RenderList();
        var flt = new BarFlow { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
        flt.Controls.AddRange(new Control[] { _q, _fGroup, _fProd });
        var lst = new Panel { Dock = DockStyle.Left, Width = NativeSkin.S(560) };
        _gridF.Dock = DockStyle.Fill; lst.Controls.Add(_gridF); lst.Controls.Add(flt);
        _gridC = NK.Grid(("dot", "", 28), ("kind", "Loại", 100), ("key", "Khoá", 230), ("exp", "Cần", 130), ("act", "Hiện tại", 0));
        _gridC.Dock = DockStyle.Top; _gridC.Height = NativeSkin.S(200);
        _gridC.SelectionChanged += (_, _) => UpdateFixBar();
        _dGuide.Dock = DockStyle.Top; _dMeta.Dock = DockStyle.Top; _dTitle.Dock = DockStyle.Top; _dBtns.Dock = DockStyle.Top;
        NK.Stack(_detail, _dTitle, _dMeta, _gridC, _fixBar, _dBtns, _dGuide);
        _paneF.Controls.Add(_detail); _paneF.Controls.Add(lst);

        // ---- pane File Include ----
        _gridI = NK.Grid(("name", "File Include", 280), ("state", "Hiện tại", 90), ("feat", "Tính năng trong catalog", 0));
        _gridI.Dock = DockStyle.Fill;
        _qi.TextChanged += (_, _) => RenderFiles(); _onlyCat.CheckedChanged += (_, _) => RenderFiles();
        var fbar = new BarFlow { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
        fbar.Controls.AddRange(new Control[]
        {
            _qi, _onlyCat,
            NK.Button("Đổi IGNORE ⇄ INCLUDE", () => { var f = CurFile(); if (f is not null) Post(new { action = "fixInclude", rel = f.Name, state = f.State == "INCLUDE" ? "IGNORE" : "INCLUDE" }); }),
            NK.Button("Nơi dùng", () => { var f = CurFile(); if (f is not null) { _refs.Text = "Đang tìm…"; Post(new { action = "refs", rel = f.Name }); } }),
            NK.Button("Mở", () => { var f = CurFile(); if (f is not null) Post(new { action = "openFile", rel = f.Name }); }),
        });
        _refs.Dock = DockStyle.Bottom; _refs.Padding = new Padding(10, 2, 10, 4);
        _paneI.Controls.Add(_gridI); _paneI.Controls.Add(_refs); _paneI.Controls.Add(fbar);

        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(_paneF); body.Controls.Add(_paneI);
        Controls.Add(body); Controls.Add(tabs); Controls.Add(_sum); Controls.Add(_msg); Controls.Add(head2); Controls.Add(head);
    }

    // ---- tiện ích ----
    private FileRow? CurFile() => _gridI.CurrentRow is { } r && r.Index < _fileView.Count ? _fileView[r.Index] : null;
    private void Tab(bool f) { _paneF.Visible = f; _paneI.Visible = !f; _tabF.IsPrimary = f; _tabI.IsPrimary = !f; _tabF.Invalidate(); _tabI.Invalidate(); }
    private void Toast(string t, bool err = false) { _toast.Text = t; _toast.ForeColor = err ? AppColors.Danger : AppColors.TextMuted; }
    private void Run()
    {
        if (_src.Text.Trim().Length == 0) { Toast("Chọn thư mục dự án trước.", true); return; }
        Post(new { action = "analyze", source = _src.Text.Trim(), useDb = _useDb.Checked });
    }

    private static Color StColor(string s) => s switch { "ok" => AppColors.Success, "bad" => AppColors.Danger, "partial" or "diff" => AppColors.Warning, _ => AppColors.TextMuted };

    private static Feature ParseFeature(JsonElement f) => new()
    {
        Group = P(f, "group"), Title = P(f, "title"), Version = P(f, "version"), Id = P(f, "id"), Products = P(f, "products"), Mode = P(f, "mode"), Builtin = PB(f, "builtin"),
        Conds = PA(f, "conditions").Select(c => new Cond { Kind = P(c, "kind"), Key = P(c, "key"), Expected = P(c, "expected") }).ToList(),
        Guide = PA(f, "guide").Select(g => g.GetString() ?? "").ToList(),
    };

    private string StOf(Feature f) => _results.TryGetValue(f.Key, out var r) ? r.Status : "none";

    private List<Feature> Filtered(bool ignoreStatus)
    {
        var q = _q.Text.Trim().ToLowerInvariant();
        var g = _fGroup.SelectedIndex > 0 ? _groups[_fGroup.SelectedIndex - 1] : ""; var p = _fProd.SelectedIndex > 0 ? _prods[_fProd.SelectedIndex - 1] : "";
        return _catalog.Where(f =>
        {
            if (g.Length > 0 && f.Group != g) return false;
            if (p.Length > 0 && !f.Products.Split(',').Select(x => x.Trim()).Contains(p)) return false;
            if (!ignoreStatus && _status.Length > 0 && _analyzed && StOf(f) != _status) return false;
            return q.Length == 0 || (f.Title + " " + f.Id + " " + f.Version + " " + f.Group + " " + string.Join(" ", f.Conds.Select(c => c.Key))).ToLowerInvariant().Contains(q);
        }).ToList();
    }

    private List<string> _groups = new(), _prods = new();

    private void FillFilters()
    {
        _loading = true;
        _groups = _catalog.Select(f => f.Group).Distinct().ToList();
        _prods = _catalog.SelectMany(f => f.Products.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0)).Distinct().ToList();
        var gi = _fGroup.SelectedIndex > 0 && _fGroup.SelectedIndex - 1 < _groups.Count ? _fGroup.SelectedIndex : 0;
        _fGroup.SetItems(new[] { "Nhóm: tất cả" }.Concat(_groups).ToList(), gi);
        _fProd.SetItems(new[] { "Sản phẩm: tất cả" }.Concat(_prods).ToList(), 0);
        _loading = false;
    }

    private void RenderSummary()
    {
        _sum.Controls.Clear();
        var baseList = Filtered(true);
        var cnt = baseList.GroupBy(f => _analyzed ? StOf(f) : "none").ToDictionary(g => g.Key, g => g.Count());
        var all = NK.Button($"{baseList.Count} Tất cả", () => { _status = ""; RenderList(); }, primary: _status.Length == 0); _sum.Controls.Add(all);
        if (_analyzed)
            foreach (var s in StOrder.Where(cnt.ContainsKey))
            {
                var key = s; var b = NK.Button($"{cnt[s]} {St[s]}", () => { _status = key; RenderList(); }, primary: _status == s); b.ForeOverride = _status == s ? null : StColor(s); _sum.Controls.Add(b);
            }
        else _sum.Controls.Add(NK.Muted("Bấm “Phân tích” để so với dự án."));
    }

    private void RenderList()
    {
        if (_loading) return;
        _view = Filtered(false);
        RenderSummary();
        _loading = true;
        _gridF.Rows.Clear();
        foreach (var f in _view)
        {
            var s = _analyzed ? StOf(f) : "none"; _results.TryGetValue(f.Key, out var r);
            var i = _gridF.Rows.Add(f.Group + (f.Builtin ? "" : " ★"), f.Title, f.Version, f.Products, _analyzed ? r?.StatusText ?? "" : "—");
            _gridF.Rows[i].Cells["st"].Style.ForeColor = StColor(s);
        }
        var at = _view.FindIndex(f => f.Key == _sel);
        if (at >= 0) _gridF.CurrentCell = _gridF.Rows[at].Cells[1]; else _gridF.ClearSelection();
        _loading = false;
        RenderDetail();
    }

    private void RenderDetail()
    {
        var f = _catalog.FirstOrDefault(x => x.Key == _sel);
        _dBtns.Controls.Clear(); _fixBar.Controls.Clear(); _gridC.Rows.Clear();
        if (f is null) { _dTitle.Text = ""; _dMeta.Text = "Chọn 1 tính năng ở bên trái để xem điều kiện, giá trị hiện tại và cách sửa."; _dGuide.Text = ""; _gridC.Visible = false; _curConds = new(); return; }
        _gridC.Visible = true;
        _results.TryGetValue(f.Key, out var r);
        _curConds = r is not null ? r.Conds : f.Conds.Select(c => new Cond { Kind = c.Kind, Key = c.Key, Expected = c.Expected }).ToList();
        _dTitle.Text = f.Title;
        _dMeta.Text = $"Nhóm {f.Group}" + (f.Version.Length > 0 ? " · phiên bản " + f.Version : "") + (f.Id.Length > 0 ? " · ID " + f.Id : "") + (f.Products.Length > 0 ? " · " + f.Products : "") + (_analyzed && r is not null ? " · " + r.StatusText : "");
        foreach (var c in _curConds)
        {
            var i = _gridC.Rows.Add("●", KindName.GetValueOrDefault(c.Kind, c.Kind), c.Key, c.Expected.Length == 0 ? "(trống)" : c.Expected, _analyzed ? c.Actual + (c.Note.Length > 0 ? "  " + c.Note : "") : "chưa phân tích");
            _gridC.Rows[i].Cells["dot"].Style.ForeColor = _analyzed ? c.State switch { "ok" => AppColors.Success, "bad" or "missing" => AppColors.Danger, _ => AppColors.TextMuted } : AppColors.TextMuted;
        }
        var fixable = _curConds.Where(c => c.Kind == "include" && _analyzed && c.State == "bad").ToList();
        if (fixable.Count > 1) _dBtns.Controls.Add(NK.Button($"Sửa tất cả file Include ({fixable.Count})", () => { foreach (var c in fixable) Post(new { action = "fixInclude", rel = c.Key, state = c.Expected }); }, primary: true));
        var dbFix = _curConds.Where(c => c.Fix.Length > 0 && c.State != "ok").ToList();
        if (dbFix.Count > 0) _dBtns.Controls.Add(NK.Button($"Copy script sửa DB ({dbFix.Count})", () => { Post(new { action = "copy", text = string.Join("\nGO\n", dbFix.Select(x => x.Fix)) }); Toast("Đã copy script sửa DB — chạy trong SQL Query của project."); }, tip: "Chép các câu UPDATE vào clipboard"));
        _dBtns.Controls.Add(NK.Button("✎ Sửa tính năng", () => OpenEditor(f)));
        _dGuide.Text = f.Guide.Count > 0 ? "Hướng dẫn\n" + string.Join("\n", f.Guide) : _curConds.Count == 0 ? "Tính năng này chỉ có hướng dẫn, không có điều kiện tự kiểm tra được. Có thể thêm điều kiện bằng nút Sửa." : "";
        UpdateFixBar();
    }

    private void UpdateFixBar()
    {
        _fixBar.Controls.Clear();
        var i = _gridC.CurrentRow?.Index ?? -1;
        if (i < 0 || i >= _curConds.Count) return;
        var c = _curConds[i];
        if (c.Kind is "include" or "entity")
        {
            if (c.Kind == "include" && _analyzed && c.State != "ok" && c.State != "unknown") _fixBar.Controls.Add(NK.Button("Sửa → " + c.Expected, () => Post(new { action = "fixInclude", rel = c.Key, state = c.Expected }), primary: true, tip: "Ghi " + c.Expected + " vào file (có bản .bak)"));
            if (c.Kind == "include" && _analyzed && c.State != "missing") _fixBar.Controls.Add(NK.Button("Mở file", () => Post(new { action = "openFile", rel = c.Key })));
        }
        else if (c.Fix.Length > 0) _fixBar.Controls.Add(NK.Button("Copy SQL", () => { Post(new { action = "copy", text = c.Fix }); Toast("Đã copy: " + c.Fix); }, tip: "Chép câu lệnh sửa vào clipboard"));
    }

    private void RenderFiles()
    {
        var q = _qi.Text.Trim().ToLowerInvariant(); var only = _onlyCat.Checked;
        _fileView = _files.Where(f => (q.Length == 0 || f.Name.ToLowerInvariant().Contains(q) || string.Join(" ", f.Features).ToLowerInvariant().Contains(q)) && (!only || f.Features.Count > 0)).ToList();
        _gridI.Rows.Clear();
        foreach (var f in _fileView)
        {
            var i = _gridI.Rows.Add(f.Name, f.State, f.Features.Count > 0 ? string.Join(" · ", f.Features) : "—");
            _gridI.Rows[i].Cells["state"].Style.ForeColor = f.State == "INCLUDE" ? AppColors.Success : AppColors.TextMuted;
        }
        if (_files.Count == 0) _refs.Text = _analyzed ? "Không thấy file công tắc nào (Include\\*.txt chứa INCLUDE / IGNORE)." : "Bấm “Phân tích” để liệt kê các file Include của dự án.";
    }

    private void OpenEditor(Feature? f)
    {
        _editor = new FeatureEditor(f, _catalog.Select(x => x.Group).Distinct().ToList(), text => Post(new { action = "parsePaste", text }));
        if (_editor.ShowDialog(FindForm()) == DialogResult.OK && _editor.Saved is { } saved)
        {
            _sel = saved.Group + "|" + saved.Title;
            Post(new { action = "saveFeature", feature = new { group = saved.Group, title = saved.Title, version = saved.Version, id = saved.Id, products = saved.Products, mode = f?.Mode ?? "", conditions = saved.Conds.Select(c => new { kind = c.Kind, key = c.Key, expected = c.Expected }), guide = saved.Guide }, original = f is not null && f.Group == saved.Group ? f.Title : null });
        }
        else if (_editor.Action == "delete" && f is not null) { Post(new { action = "deleteFeature", group = f.Group, title = f.Title }); _sel = ""; }
        else if (_editor.Action == "reset" && f is not null) Post(new { action = "resetGroup", group = f.Group });
        _editor.Dispose(); _editor = null;
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "incheck.init":
            {
                var d = Arg(a, 0);
                _catalog = PA(d, "catalog").Select(ParseFeature).ToList();
                if (_src.Text.Length == 0) _src.Text = P(d, "source");
                FillFilters(); RenderList(); RenderFiles();
                if (_src.Text.Length > 0 && !_analyzed) Run();
                break;
            }
            case "incheck.onCatalog":
                _catalog = (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>()).Select(ParseFeature).ToList();
                FillFilters(); if (Str(a, 1).Length > 0) Toast(Str(a, 1));
                if (_analyzed) Run(); else RenderList();
                break;
            case "incheck.onResult":
            {
                var d = Arg(a, 0); _analyzed = true;
                _results = PA(d, "results").Select(r => new Result { Key = P(r, "key"), Status = P(r, "status"), StatusText = P(r, "statusText"), Conds = PA(r, "conditions").Select(c => new Cond { Kind = P(c, "kind"), Key = P(c, "key"), Expected = P(c, "expected"), Actual = P(c, "actual"), State = P(c, "state"), Note = P(c, "note"), Fix = P(c, "fix") }).ToList() }).ToDictionary(r => r.Key, r => r);
                _files = PA(d, "files").Select(f => new FileRow { Name = P(f, "name"), State = P(f, "state"), Features = PA(f, "features").Select(x => x.GetString() ?? "").ToList() }).ToList();
                var note = P(d, "note"); _msg.Text = note; _msg.Visible = note.Length > 0;
                RenderList(); RenderFiles(); _tabI.Text = $"File Include ({_files.Count})";
                break;
            }
            case "incheck.onPicked": _src.Text = Str(a, 0); Run(); break;
            case "incheck.onFixed": { var d = Arg(a, 0); Toast($"Đã ghi {P(d, "state")} vào {P(d, "rel")} (bản gốc lưu .bak)."); Run(); break; }
            case "incheck.onRefs":
            {
                var d = Arg(a, 0); var hits = PA(d, "hits").Select(h => $"{P(h, "file")}:{P(h, "line")}").ToList();
                _refs.Text = hits.Count > 0 ? string.Join("\n", hits.Take(40)) : "Không file nào nhắc tới tên này.";
                break;
            }
            case "incheck.onPasted": _editor?.OnPasted(Arg(a, 0)); break;
            case "incheck.onError": Toast(Str(a, 0), true); break;
            case "incheck.busy": _busy = Bool(a, 0); Cursor = _busy ? Cursors.AppStarting : Cursors.Default; break;
        }
    }

    /// <summary>Hộp thêm / sửa 1 tính năng của catalog (kèm dán nhanh hướng dẫn để tự tách điều kiện).</summary>
    private sealed class FeatureEditor : ThemedForm
    {
        private readonly RoundedInput _group = NK.Input(""), _ver = NK.Input(""), _title = NK.Input(""), _id = NK.Input(""), _prod = NK.Input(""), _paste = NK.Memo(3, "Dán nguyên các dòng hướng dẫn vào đây rồi bấm “Tách điều kiện”"), _guide = NK.Memo(4, "");
        private readonly Panel _conds = new() { Dock = DockStyle.Top, AutoSize = true };
        private readonly Action<string> _parse;
        public Feature? Saved { get; private set; }
        public string Action { get; private set; } = "";

        public FeatureEditor(Feature? f, List<string> groups, Action<string> parsePaste)
        {
            _parse = parsePaste;
            Text = f is null ? "Thêm tính năng" : "Sửa tính năng" + (f.Builtin ? " (có sẵn — bản sửa lưu thành của bạn)" : "");
            Width = NativeSkin.S(780); Height = NativeSkin.S(780); StartPosition = FormStartPosition.CenterParent; ShowIcon = false;
            _group.Text = f?.Group ?? ""; _ver.Text = f?.Version ?? ""; _title.Text = f?.Title ?? ""; _id.Text = f?.Id ?? ""; _prod.Text = f?.Products ?? "FBO, FAO, FAW"; _guide.Text = string.Join("\r\n", f?.Guide ?? new List<string>());
            _group.Inner.AutoCompleteMode = AutoCompleteMode.SuggestAppend; _group.Inner.AutoCompleteSource = AutoCompleteSource.CustomSource;
            var gs = new AutoCompleteStringCollection(); gs.AddRange(groups.ToArray()); _group.Inner.AutoCompleteCustomSource = gs;
            foreach (var c in f?.Conds ?? new List<Cond>()) AddCond(c);

            var body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(14, 8, 14, 8) };
            var addBar = NK.Row(
                NK.Button("＋ File Include", () => AddCond(new Cond { Kind = "include", Expected = "INCLUDE" })), NK.Button("＋ Option", () => AddCond(new Cond { Kind = "option", Expected = "1" })),
                NK.Button("＋ WCommand", () => AddCond(new Cond { Kind = "wcommand", Expected = "1" })), NK.Button("＋ Entity (.ent)", () => AddCond(new Cond { Kind = "entity", Expected = "INCLUDE" })));
            var form = NK.FormGrid(210, ("Nhóm (thường là phiên bản)", _group), ("Phiên bản áp dụng (vd SP24.2.2)", _ver), ("Tên tính năng", _title), ("ID / ticket", _id), ("Sản phẩm hỗ trợ (cách nhau dấu phẩy)", _prod),
                ("⚡ Dán nhanh hướng dẫn (Include\\X.txt, update options…, update wcommand9…, <!ENTITY % …>)", _paste));
            var pasteBtn = NK.Row(NK.Button("Tách điều kiện từ đoạn dán", () => { if (_paste.Text.Trim().Length > 0) _parse(_paste.Text.Trim()); }));
            var guideForm = NK.FormGrid(210, ("Hướng dẫn (mỗi dòng 1 ý)", _guide));
            NK.Stack(body, form, pasteBtn, NK.Muted("Điều kiện kiểm tra"), _conds, addBar, guideForm);

            var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
            foot.Controls.Add(NK.Button("Lưu", Save, primary: true)); foot.Controls.Add(NK.Button("Đóng", () => DialogResult = DialogResult.Cancel));
            if (f is not null)
            {
                foot.Controls.Add(NK.Button("Khôi phục nhóm", () => { if (NK.Confirm(this, $"Bỏ mọi chỉnh sửa của nhóm \"{f.Group}\" và dùng lại catalog có sẵn?", "Check Include")) { Action = "reset"; DialogResult = DialogResult.Cancel; } }, tip: "Bỏ chỉnh sửa của nhóm, dùng lại bản có sẵn"));
                foot.Controls.Add(NK.Button("Xoá", () => { if (NK.Confirm(this, $"Xoá \"{f.Title}\" khỏi catalog của bạn?", "Check Include")) { Action = "delete"; DialogResult = DialogResult.Cancel; } }));
            }
            Controls.Add(body); Controls.Add(foot);
        }

        private void AddCond(Cond c)
        {
            var row = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 32), Tag = c };
            var kind = new DropDownButton { Width = NativeSkin.S(130), Dock = DockStyle.Left }; var kinds = KindName.Keys.ToList();
            kind.SetItems(KindName.Values.ToList(), Math.Max(0, kinds.IndexOf(c.Kind)));
            var key = NK.Input(c.Kind == "include" ? "Include\\X.txt" : c.Kind == "option" ? "tên option" : c.Kind == "wcommand" ? "xgroup" : "Report\\Config\\X.ent | TênEntity"); key.Text = c.Key; key.Dock = DockStyle.Fill;
            var exp = NK.Input("INCLUDE / IGNORE / giá trị"); exp.Text = c.Expected; exp.Width = NativeSkin.S(170); exp.Dock = DockStyle.Right;
            var del = NK.Button("✕", () => { _conds.Controls.Remove(row); row.Dispose(); }, tip: "Bỏ điều kiện"); del.AutoWidth = false; del.Width = NativeSkin.S(34); del.Dock = DockStyle.Right;
            row.Controls.Add(key); row.Controls.Add(exp); row.Controls.Add(del); row.Controls.Add(kind);
            row.Tag = (kind, key, exp, kinds);
            _conds.Controls.Add(row); _conds.Controls.SetChildIndex(row, 0);
        }

        public void OnPasted(JsonElement d)
        {
            var n = 0;
            var have = new HashSet<string>(_conds.Controls.Cast<Panel>().Select(RowCond).Select(c => c.Kind + "|" + c.Key.ToLowerInvariant()));
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("conds", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                {
                    var cond = new Cond { Kind = c.TryGetProperty("kind", out var k) ? k.GetString() ?? "" : "", Key = c.TryGetProperty("key", out var kk) ? kk.GetString() ?? "" : "", Expected = c.TryGetProperty("expected", out var e) ? e.GetString() ?? "" : "" };
                    if (have.Add(cond.Kind + "|" + cond.Key.ToLowerInvariant())) { AddCond(cond); n++; }
                }
            if (_guide.Text.Trim().Length == 0 && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("guide", out var g) && g.ValueKind == JsonValueKind.Array) _guide.Text = string.Join("\r\n", g.EnumerateArray().Select(x => x.GetString() ?? ""));
            Text = n > 0 ? $"Đã tách {n} điều kiện" : "Không nhận ra điều kiện nào — thêm tay bằng các nút bên dưới";
        }

        private static Cond RowCond(Panel row)
        {
            var (kind, key, exp, kinds) = ((DropDownButton, RoundedInput, RoundedInput, List<string>))row.Tag!;
            return new Cond { Kind = kinds[Math.Max(0, kind.SelectedIndex)], Key = key.Text.Trim(), Expected = exp.Text.Trim() };
        }

        private void Save()
        {
            if (_title.Text.Trim().Length == 0) { Text = "Nhập tên tính năng."; _title.Inner.Focus(); return; }
            var f = new Feature { Group = _group.Text.Trim(), Title = _title.Text.Trim(), Version = _ver.Text.Trim(), Id = _id.Text.Trim(), Products = _prod.Text.Trim() };
            if (f.Group.Length == 0) { var m = System.Text.RegularExpressions.Regex.Match(f.Version, @"^[A-Za-z]+\d+(\.\d+)?"); f.Group = m.Success ? m.Value : f.Version.Length > 0 ? f.Version : "Khac"; }
            f.Conds = _conds.Controls.Cast<Panel>().Select(RowCond).Where(c => c.Key.Length > 0).Reverse().ToList();
            f.Guide = _guide.Text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            Saved = f; DialogResult = DialogResult.OK;
        }
    }
}
