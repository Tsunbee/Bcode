using Bcode.App.Services;
using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>
/// "Chế độ triển khai" — khai field dạng LƯỚI như FSD Studio (F1Customize) thay cho sửa XML: chuột phải file Dir / Grid / Filter / Report ở File Lookup
/// hoặc menu ở cây WCommand → tab này mở cả bộ file của chứng từ (SysID). Lưới Title (titleV/E, subTitleV/E), lưới Fields (mỗi thuộc tính 1 cột, thuộc tính
/// true/false = ô tick; field trong &amp;X; include cũng hiện và sửa vào file include), tab Views (&lt;field name&gt; của Grid / &lt;item value&gt; của Dir, Filter),
/// khung XML của field đang chọn (sửa được). Mọi thay đổi sửa thẳng chuỗi gốc (Services/FieldGridDocument.cs) — định dạng, comment, entity giữ nguyên.
/// Dùng chung cho chế độ Web và App (WinForms thuần, không WebView2).
/// </summary>
public sealed class FieldGridControl : UserControl
{
    private sealed record Col(string Key, string Header, bool Bool, bool DefaultTrue = false);

    /// <summary>Mở file bằng editor thường (BcodeViewer / tab mở file).</summary>
    public event Action<string>? OpenFileRequested;

    private readonly IReadOnlyList<string> _readOnlyRoots;
    private FgSet _set = new();
    private FgDoc? _doc;
    private string? _clicked;
    private bool _filling;
    private bool _viewsMode;
    private List<Col> _cols = new();
    private string _colSig = "";

    private readonly FlowLayoutPanel _fileBar = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(4, 4, 4, 2) };
    private readonly Label _banner = new() { Dock = DockStyle.Top, AutoSize = false, Visible = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly SplitContainer _topSplit = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
    private readonly DataGridView _titleGrid = NewGrid();
    private readonly DataGridView _grid = NewGrid();
    private readonly DataGridView _viewGrid = NewGrid();
    private readonly Scintilla _xml = new() { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
    private readonly Label _xmlLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button _xmlApply = new() { Text = "Áp dụng XML", AutoSize = true, Dock = DockStyle.Right };
    private readonly FlowLayoutPanel _tools = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(2) };
    private readonly RadioButton _tabFields = new() { Text = "Fields", Appearance = Appearance.Button, AutoSize = true, Checked = true, FlatStyle = FlatStyle.Flat };
    private readonly RadioButton _tabViews = new() { Text = "Views", Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.Flat };
    private readonly Button _btnAdd = new() { Text = "Thêm (F4)", AutoSize = true };
    private readonly Button _btnCopy = new() { Text = "Nhân bản", AutoSize = true };
    private readonly Button _btnDelete = new() { Text = "Xoá (F8)", AutoSize = true };
    private readonly Button _btnUp = new() { Text = "↑ Lên", AutoSize = true };
    private readonly Button _btnDown = new() { Text = "↓ Xuống", AutoSize = true };
    private readonly Button _btnToView = new() { Text = "Thêm vào view (F7)", AutoSize = true };
    private readonly Button _btnUndo = new() { Text = "Hoàn tác (Ctrl+Z)", AutoSize = true };
    private readonly TextBox _sysId = new() { Width = 140 };
    private readonly TextBox _voucherCode = new() { Width = 70, ReadOnly = true };
    private readonly LinkLabel _pathLink = new() { AutoSize = true, Margin = new Padding(8, 7, 8, 0) };
    private readonly Label _status = new() { AutoSize = true, Margin = new Padding(8, 7, 0, 0) };
    private int _dragRow = -1;
    private Point _dragStart;

    internal FieldGridControl(IEnumerable<string>? readOnlyRoots = null)
    {
        _readOnlyRoots = readOnlyRoots?.ToList() ?? new List<string>();
        Font = ThemeManager.BaseFont;
        BuildLayout();
        WireEvents();
        ThemeManager.Subscribe(this, () => { ApplyColors(); SqlScintilla.ApplyTheme(_xml); FcodeLexer.Apply(_xml); });
    }

    // ------------------------------------------------------------------ dựng giao diện

    private static DataGridView NewGrid() => new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersWidth = 28,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        MultiSelect = false,
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
    };

    private void BuildLayout()
    {
        // khung XML field đang chọn
        SqlScintilla.Configure(_xml);
        SqlScintilla.ApplyTheme(_xml);
        FcodeLexer.Apply(_xml);
        var xmlHead = new Panel { Dock = DockStyle.Top, Height = NativeSkin.S(30) };
        xmlHead.Controls.Add(_xmlLabel);
        xmlHead.Controls.Add(_xmlApply);
        var xmlPanel = new Panel { Dock = DockStyle.Fill };
        xmlPanel.Controls.Add(_xml);
        xmlPanel.Controls.Add(xmlHead);
        _topSplit.Panel1.Controls.Add(_titleGrid);
        _topSplit.Panel2.Controls.Add(xmlPanel);
        _split.Panel1.Controls.Add(_topSplit);

        foreach (var c in new Control[] { _tabFields, _tabViews, Spacer(), _btnAdd, _btnCopy, _btnDelete, _btnUp, _btnDown, _btnToView, _btnUndo })
            _tools.Controls.Add(c);
        _viewGrid.Visible = false;
        _split.Panel2.Controls.Add(_grid);
        _split.Panel2.Controls.Add(_viewGrid);
        _split.Panel2.Controls.Add(_tools);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(4) };
        bottom.Controls.Add(new Label { Text = "SysID", AutoSize = true, Margin = new Padding(4, 7, 2, 0) });
        bottom.Controls.Add(_sysId);
        bottom.Controls.Add(new Label { Text = "Voucher Code", AutoSize = true, Margin = new Padding(10, 7, 2, 0) });
        bottom.Controls.Add(_voucherCode);
        bottom.Controls.Add(_pathLink);
        foreach (var (text, tip, act) in new (string, string, Action)[]
        {
            ("Load Data", "Nạp lại file của tab đang xem từ đĩa (bỏ thay đổi chưa lưu của tab này).", () => ReloadCurrent()),
            ("Reload All", "Tìm lại và nạp lại cả bộ file của SysID (bỏ mọi thay đổi chưa lưu).", () => ReloadAll(_sysId.Text.Trim())),
            ("Save", "Lưu mọi file đã sửa (Ctrl+S) — giữ encoding gốc, chép bản cũ vào %AppData%\\Bcode\\field-grid-backup.", () => SaveAll()),
            ("View Script", "Xem những dòng XML sẽ đổi so với file trên đĩa (chưa ghi).", ViewScript),
            ("Copy File", "Chép file của tab đang xem vào clipboard (dán vào Explorer / gói cập nhật).", CopyFile),
            ("Mở XML", "Mở file của tab đang xem bằng editor thường (BcodeViewer).", () => { if (_doc is not null) OpenFileRequested?.Invoke(_doc.Main.Path); }),
        })
        {
            var b = new Button { Text = text, AutoSize = true };
            b.Click += (_, _) => act();
            NK.Tip(b, tip);
            bottom.Controls.Add(b);
        }
        bottom.Controls.Add(_status);

        _banner.Height = NativeSkin.S(26);
        // tên entity &X; phải hiện nguyên dấu & (Label / nút mặc định ăn & làm phím tắt)
        _banner.UseMnemonic = _xmlLabel.UseMnemonic = _status.UseMnemonic = _pathLink.UseMnemonic = false;
        Controls.Add(_split);
        Controls.Add(_banner);
        Controls.Add(_fileBar);
        Controls.Add(bottom);

        NK.Tip(_btnAdd, "Thêm field mới ngay dưới dòng đang chọn (F4 / Insert) rồi gõ tên.");
        NK.Tip(_btnCopy, "Nhân bản field đang chọn (chép cả XML) ngay bên dưới rồi gõ tên mới.");
        NK.Tip(_btnDelete, "Xoá field / dòng view đang chọn (F8) — Ctrl+Z để hoàn tác.");
        NK.Tip(_btnUp, "Đưa lên trên (Alt+↑). Kéo tiêu đề dòng để đổi chỗ nhanh.");
        NK.Tip(_btnDown, "Đưa xuống dưới (Alt+↓).");
        NK.Tip(_btnToView, "Thêm field đang chọn vào <views> của Grid (F7).");
        NK.Tip(_sysId, "Gõ SysID khác rồi Enter để mở bộ file khác cùng thư mục Controllers.");
        NK.Tip(_xmlApply, "Ghi XML đã sửa ở khung này vào file (Ctrl+Enter).");
    }

    private static Control Spacer() => new Label { Width = NativeSkin.S(12), AutoSize = false, Height = 4 };

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        BeginInvoke(new Action(() =>
        {
            try
            {
                _split.Panel1MinSize = NativeSkin.S(60);
                _split.SplitterDistance = Math.Max(NativeSkin.S(90), Math.Min(NativeSkin.S(150), Height / 4));
                _topSplit.SplitterDistance = Math.Max(NativeSkin.S(200), Width * 45 / 100);
            }
            catch (Exception) { /* kích thước chưa có */ }
            ApplyColors();
        }));
    }

    private void ApplyColors()
    {
        _banner.BackColor = AppColors.PanelAlt;
        _banner.ForeColor = AppColors.Warning;
        foreach (var rb in _fileBar.Controls.OfType<RadioButton>().Concat(new[] { _tabFields, _tabViews })) PaintToggle(rb);
        _status.ForeColor = AppColors.TextMuted;
        _pathLink.LinkColor = AppColors.Accent;
        _pathLink.ActiveLinkColor = AppColors.AccentHover;
    }

    private static void PaintToggle(RadioButton rb)
    {
        rb.FlatAppearance.BorderColor = AppColors.Border;
        rb.FlatAppearance.CheckedBackColor = AppColors.Accent;
        rb.BackColor = rb.Checked ? AppColors.Accent : AppColors.ButtonBack;
        rb.ForeColor = rb.Checked ? AppColors.OnAccent : AppColors.Text;
        rb.Margin = new Padding(2);
        rb.Padding = new Padding(6, 1, 6, 1);
    }

    private void WireEvents()
    {
        _tabFields.CheckedChanged += (_, _) => { if (_tabFields.Checked) SetViewsMode(false); PaintToggle(_tabFields); };
        _tabViews.CheckedChanged += (_, _) => { if (_tabViews.Checked) SetViewsMode(true); PaintToggle(_tabViews); };
        _btnAdd.Click += (_, _) => AddField(copy: false);
        _btnCopy.Click += (_, _) => AddField(copy: true);
        _btnDelete.Click += (_, _) => DeleteCurrent();
        _btnUp.Click += (_, _) => MoveCurrent(-1);
        _btnDown.Click += (_, _) => MoveCurrent(1);
        _btnToView.Click += (_, _) => AddToView();
        _btnUndo.Click += (_, _) => DoUndo();
        _xmlApply.Click += (_, _) => ApplyXml();
        _pathLink.LinkClicked += (_, _) => { if (_doc is not null) OpenFileRequested?.Invoke(_doc.Main.Path); };
        _sysId.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ReloadAll(_sysId.Text.Trim()); } };

        foreach (var g in new[] { _grid, _titleGrid, _viewGrid })
        {
            g.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewCheckBoxCell) g.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            g.DataError += (_, e) => e.ThrowException = false;
        }
        _grid.CellValueChanged += (_, e) => { if (!_filling && e.RowIndex >= 0) OnFieldCellChanged(e.RowIndex, e.ColumnIndex); };
        _titleGrid.CellValueChanged += (_, e) => { if (!_filling && e.RowIndex >= 0) OnTitleCellChanged(e.ColumnIndex); };
        _viewGrid.CellValueChanged += (_, e) => { if (!_filling && e.RowIndex >= 0) OnViewCellChanged(e.RowIndex, e.ColumnIndex); };
        _grid.CurrentCellChanged += (_, _) => { if (!_filling) ShowXmlOfCurrent(); };
        _grid.CellToolTipTextNeeded += (_, e) =>
        {
            if (_doc is null || e.RowIndex < 0 || e.RowIndex >= _doc.Fields.Count) return;
            var f = _doc.Fields[e.RowIndex];
            e.ToolTipText = f.ReadOnlyReason ?? (f.Via is not null ? "Field nằm trong " + f.Via + " → " + f.File.Path : "");
        };

        // kéo tiêu đề dòng để đổi thứ tự (Fields và Views)
        foreach (var g in new[] { _grid, _viewGrid })
        {
            g.AllowDrop = true;
            g.MouseDown += (_, e) =>
            {
                var hit = g.HitTest(e.X, e.Y);
                _dragRow = e.Button == MouseButtons.Left && hit.Type == DataGridViewHitTestType.RowHeader ? hit.RowIndex : -1;
                _dragStart = e.Location;
            };
            g.MouseMove += (_, e) =>
            {
                if (_dragRow < 0 || e.Button != MouseButtons.Left) return;
                if (Math.Abs(e.X - _dragStart.X) + Math.Abs(e.Y - _dragStart.Y) < 6) return;
                var row = _dragRow;
                _dragRow = -1;
                g.DoDragDrop(row, DragDropEffects.Move);
            };
            g.DragOver += (_, e) => e.Effect = e.Data?.GetDataPresent(typeof(int)) == true ? DragDropEffects.Move : DragDropEffects.None;
            g.DragDrop += (_, e) =>
            {
                if (e.Data?.GetData(typeof(int)) is not int src) return;
                var p = g.PointToClient(new Point(e.X, e.Y));
                var target = g.HitTest(p.X, p.Y).RowIndex;
                if (target < 0) target = g.Rows.Count - 1;
                if (target == src || target < 0) return;
                if (ReferenceEquals(g, _grid)) DragField(src, target); else DragView(src, target);
            };
        }
        _xml.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyXml(); } };
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var inXml = _xml.Focused;
        var editing = _grid.IsCurrentCellInEditMode || _titleGrid.IsCurrentCellInEditMode || _viewGrid.IsCurrentCellInEditMode || _sysId.Focused;
        switch (keyData)
        {
            case Keys.Control | Keys.S: EndEdits(); SaveAll(); return true;
            case Keys.Control | Keys.Z when !inXml && !editing: DoUndo(); return true;
            case Keys.F4 when !inXml:
            case Keys.Insert when !inXml && !editing: if (!_viewsMode) AddField(copy: false); return true;
            case Keys.F8 when !inXml:
            case Keys.Delete when !inXml && !editing && (_viewsMode || _grid.CurrentCell?.OwningColumn?.Index == 0): DeleteCurrent(); return true;
            case Keys.F7 when !inXml: AddToView(); return true;
            case Keys.Alt | Keys.Up when !inXml: MoveCurrent(-1); return true;
            case Keys.Alt | Keys.Down when !inXml: MoveCurrent(1); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void EndEdits()
    {
        foreach (var g in new[] { _grid, _titleGrid, _viewGrid }) if (g.IsCurrentCellInEditMode) g.EndEdit();
    }

    // ------------------------------------------------------------------ mở / nạp

    /// <summary>Có thay đổi chưa lưu (MainForm hỏi trước khi đóng tab).</summary>
    public bool IsDirty => _set.Dirty;

    /// <summary>Mở bộ file của chứng từ chứa <paramref name="clickedFile"/> (Dir/Grid/Filter/Report…); file nằm ngoài các thư mục đó thì mở riêng nó.</summary>
    public void OpenFile(string clickedFile)
    {
        _clicked = clickedFile;
        var (root, _) = FgSet.Locate(clickedFile);
        LoadSet(root, Path.GetFileNameWithoutExtension(clickedFile), clickedFile);
    }

    /// <summary>Mở theo menu (SysID) — từ cây WCommand.</summary>
    public void OpenSysId(string controllersRoot, string sysId, string? voucherCode)
    {
        _clicked = null;
        _voucherCode.Text = voucherCode ?? "";
        LoadSet(controllersRoot, sysId, null);
    }

    /// <summary>Tab này đang mở đúng bộ (thư mục Controllers + SysID) — MainForm chuyển sang tab cũ thay vì mở tab mới.</summary>
    public bool IsSet(string? controllersRoot, string sysId)
        => string.Equals(_set.SysId, sysId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(_set.ControllersRoot.TrimEnd('\\'), (controllersRoot ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public string TitleText =>"Lưới field · " + (_set.SysId.Length > 0 ? _set.SysId : "?");

    private void LoadSet(string? root, string sysId, string? clicked)
    {
        _set = new FgSet(_readOnlyRoots);
        try
        {
            if (root is null && clicked is not null) _set.AddDoc(clicked, "");
            else if (root is not null) _set.LoadSet(root, sysId, clicked);
            _set.SysId = sysId;
        }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), "Không đọc được file:\n" + ex.Message, "Bcode — Lưới field", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _sysId.Text = sysId;
        BuildFileBar();
        var first = (clicked is not null ? _set.Docs.FirstOrDefault(d => string.Equals(d.Main.Path, clicked, StringComparison.OrdinalIgnoreCase)) : null) ?? _set.Docs.FirstOrDefault();
        SelectDoc(first);
        if (_set.Docs.Count == 0) SetStatus("Không tìm thấy file Dir / Grid / Filter / Report nào tên " + sysId + (root is null ? "" : " trong " + root));
        if (Parent is TabPage tp) tp.Text = TitleText;
    }

    private void BuildFileBar()
    {
        _fileBar.SuspendLayout();
        foreach (Control c in _fileBar.Controls.Cast<Control>().ToList()) { _fileBar.Controls.Remove(c); c.Dispose(); }
        foreach (var d in _set.Docs)
        {
            var rb = new RadioButton { Text = d.TabTitle, Appearance = Appearance.Button, AutoSize = true, FlatStyle = FlatStyle.Flat, Tag = d, Font = Font, UseMnemonic = false };
            rb.CheckedChanged += (_, _) => { PaintToggle(rb); if (rb.Checked && !ReferenceEquals(_doc, d)) SelectDoc(d); };
            NK.Tip(rb, d.Main.Path);
            PaintToggle(rb);
            _fileBar.Controls.Add(rb);
        }
        _fileBar.ResumeLayout();
    }

    private void RefreshFileBarMarks()
    {
        foreach (var rb in _fileBar.Controls.OfType<RadioButton>())
        {
            if (rb.Tag is not FgDoc d) continue;
            var dirty = d.Main.Dirty || d.Fields.Any(f => f.File.Dirty) || d.ViewItems.Any(v => v.File.Dirty);
            var t = (dirty ? "*" : "") + d.TabTitle;
            if (rb.Text != t) rb.Text = t;
            if (rb.Checked != ReferenceEquals(d, _doc)) rb.Checked = ReferenceEquals(d, _doc);
        }
    }

    private void SelectDoc(FgDoc? d)
    {
        EndEdits();
        _doc = d;
        _colSig = "";
        _pathLink.Text = d is null ? "" : ShortPath(d.Main.Path);
        NK.Tip(_pathLink, d?.Main.Path ?? "");
        _btnToView.Visible = d?.Kind == "Grid";
        FillAll();
        RefreshFileBarMarks();
    }

    private string ShortPath(string p)
    {
        var root = _set.ControllersRoot;
        return root.Length > 0 && p.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? p[root.Length..].TrimStart('\\') : p;
    }

    private void ReloadCurrent()
    {
        if (_doc is null) return;
        if (_set.Dirty && MessageBox.Show(FindForm(), "Nạp lại từ đĩa sẽ bỏ thay đổi chưa lưu của tab này. Tiếp tục?", "Bcode — Lưới field",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _set.Reload(_doc);
        FillAll();
        RefreshFileBarMarks();
        SetStatus("Đã nạp lại " + Path.GetFileName(_doc.Main.Path) + " (" + DateTime.Now.ToString("HH:mm:ss") + ").");
    }

    private void ReloadAll(string sysId)
    {
        if (sysId.Length == 0) return;
        if (_set.Dirty && MessageBox.Show(FindForm(), "Có thay đổi chưa lưu — nạp lại sẽ bỏ hết. Tiếp tục?", "Bcode — Lưới field",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var root = _set.ControllersRoot.Length > 0 ? _set.ControllersRoot : null;
        var clicked = _clicked is not null && string.Equals(Path.GetFileNameWithoutExtension(_clicked), sysId, StringComparison.OrdinalIgnoreCase) ? _clicked : null;
        LoadSet(root, sysId, clicked);
    }

    // ------------------------------------------------------------------ đổ dữ liệu

    private void FillAll()
    {
        _filling = true;
        try
        {
            FillBanner();
            FillTitle();
            FillFields();
            FillViews();
        }
        finally { _filling = false; }
        ShowXmlOfCurrent();
        UpdateButtons();
    }

    private void FillBanner()
    {
        string? msg = null;
        if (_doc is not null)
        {
            msg = _doc.Main.ReadOnlyReason;
            var incRo = _doc.Fields.Where(f => f.Via is not null && f.ReadOnlyReason is not null).Select(f => f.Via).Distinct().ToList();
            if (msg is null && incRo.Count > 0) msg = "Field trong " + string.Join(", ", incRo) + " chỉ xem (tô xám) — " + _doc.Fields.First(f => f.Via == incRo[0] && f.ReadOnlyReason is not null).ReadOnlyReason;
            if (msg is null && _doc.Problems.Count > 0) msg = string.Join("  ", _doc.Problems);
            if (msg is null && _doc.FieldsNode is null) msg = "File không có <fields> ở cấp gốc.";
        }
        _banner.Text = msg ?? "";
        _banner.Visible = msg is not null;
    }

    private void FillTitle()
    {
        var g = _titleGrid;
        if (g.Columns.Count == 0)
        {
            foreach (var (k, h) in new[] { ("titleV", "titleV"), ("titleE", "titleE"), ("subTitleV", "subTitleV"), ("subTitleE", "subTitleE") })
                g.Columns.Add(new DataGridViewTextBoxColumn { Name = k, HeaderText = h, Width = NativeSkin.S(160), SortMode = DataGridViewColumnSortMode.NotSortable });
        }
        g.Rows.Clear();
        if (_doc is null) return;
        var i = g.Rows.Add();
        var row = g.Rows[i];
        void Cell(string col, FgNode? node, string attr)
        {
            var a = node?.Attr(attr);
            row.Cells[col].Value = a is null ? "" : _doc!.Main.Text[a.ValueStart..a.ValueEnd];
            var ro = node is null || _doc!.Main.ReadOnlyReason is not null;
            row.Cells[col].ReadOnly = ro;
            row.Cells[col].Style.BackColor = ro ? AppColors.PanelAlt : Color.Empty;
            row.Cells[col].ToolTipText = node is null ? "File không có thẻ này." : "";
        }
        Cell("titleV", _doc.Title, "v");
        Cell("titleE", _doc.Title, "e");
        Cell("subTitleV", _doc.SubTitle, "v");
        Cell("subTitleE", _doc.SubTitle, "e");
    }

    private List<Col> ColumnsFor(FgDoc d)
    {
        var cols = new List<Col> { new("name", "name", false), new("headerV", "headerV", false), new("headerE", "headerE", false) };
        switch (d.Kind)
        {
            case "Grid":
                cols.AddRange(new Col[] { new("width", "width", false), new("clientDefault", "clientDefault", false), new("defaultValue", "defaultValue", false),
                    new("dataFormatString", "dataFormatString", false), new("align", "align", false), new("aliasName", "aliasName", false), new("type", "type", false),
                    new("allowNulls", "allowNulls", true, true), new("readOnly", "readOnly", true), new("hidden", "hidden", true), new("inactivate", "inactivate", true),
                    new("external", "external", true), new("allowFilter", "allowFilter", true), new("allowSorting", "allowSorting", true), new("isPrimaryKey", "isPrimaryKey", true),
                    new("footerV", "footerV", false), new("footerE", "footerE", false) });
                break;
            case "Dir" or "Filter" or "Lookup" or "":
                cols.AddRange(new Col[] { new("clientDefault", "clientDefault", false), new("defaultValue", "defaultValue", false), new("dataFormatString", "dataFormatString", false),
                    new("align", "align", false), new("categoryIndex", "categoryIndex", false), new("aliasName", "aliasName", false), new("type", "type", false),
                    new("allowNulls", "allowNulls", true, true), new("readOnly", "readOnly", true), new("disabled", "disabled", true), new("hidden", "hidden", true),
                    new("inactivate", "inactivate", true), new("external", "external", true), new("isPrimaryKey", "isPrimaryKey", true) });
                break;
            default:   // Report…
                cols.Add(new("type", "type", false));
                break;
        }
        // thuộc tính khác có trong file: mỗi cái 1 cột; toàn true/false thì là ô tick (mặc định false)
        var known = new HashSet<string>(cols.Select(c => c.Key), StringComparer.Ordinal);
        var extra = new List<string>();
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in d.Fields)
        {
            if (f.Placeholder is not null) continue;
            foreach (var a in f.Node.Attrs)
            {
                if (known.Contains(a.Name)) continue;
                if (!values.TryGetValue(a.Name, out var list)) { values[a.Name] = list = new(); extra.Add(a.Name); }
                list.Add(f.File.Text[a.ValueStart..a.ValueEnd]);
            }
            if (f.Node.Child("footer") is not null && !known.Contains("footerV")) { known.Add("footerV"); known.Add("footerE"); }
        }
        foreach (var name in extra)
        {
            var isBool = values[name].All(v => v is "true" or "false" or "True" or "False");
            cols.Add(new Col(name, name, isBool));
        }
        if (known.Contains("footerV") && !cols.Any(c => c.Key == "footerV")) { cols.Add(new("footerV", "footerV", false)); cols.Add(new("footerE", "footerE", false)); }
        cols.Add(new Col("@via", "Nguồn", false));
        return cols;
    }

    private string? GetValue(FgField f, Col c) => c.Key switch
    {
        "headerV" => f.Sub("header", "v"),
        "headerE" => f.Sub("header", "e"),
        "footerV" => f.Sub("footer", "v"),
        "footerE" => f.Sub("footer", "e"),
        "@via" => f.Via is null ? "" : f.Via + " (" + Path.GetFileName(f.File.Path) + ")",
        _ => f.Placeholder is not null ? (c.Key == "name" ? f.Placeholder : null) : f.Get(c.Key),
    };

    private static bool BoolOf(string? v, bool def) => v is null ? def : v.Trim() is "true" or "True" or "1";

    private void FillFields()
    {
        var g = _grid;
        if (_doc is null) { g.Rows.Clear(); g.Columns.Clear(); _colSig = ""; return; }
        var cols = ColumnsFor(_doc);
        var sig = string.Join("|", cols.Select(c => c.Key + (c.Bool ? "?" : "")));
        var newDoc = _colSig.Length == 0;   // vừa đổi tab file → về dòng đầu
        var keepRow = newDoc ? 0 : g.CurrentCell?.RowIndex ?? 0;
        var keepCol = newDoc ? 0 : g.CurrentCell?.ColumnIndex ?? 0;
        var keepTop = newDoc ? 0 : g.FirstDisplayedScrollingRowIndex;
        if (sig != _colSig)
        {
            g.Rows.Clear();
            g.Columns.Clear();
            var headFont = g.ColumnHeadersDefaultCellStyle.Font ?? ThemeManager.BoldFont;
            foreach (var c in cols)
            {
                // bề rộng tối thiểu = đủ chữ tiêu đề (đậm) + chỗ cho viền
                var head = TextRenderer.MeasureText(c.Header, headFont).Width + NativeSkin.S(16);
                DataGridViewColumn col = c.Bool
                    ? new DataGridViewCheckBoxColumn { Width = Math.Max(NativeSkin.S(56), head) }
                    : new DataGridViewTextBoxColumn { Width = Math.Max(head, NativeSkin.S(c.Key switch { "name" => 130, "headerV" or "headerE" => 150, "@via" => 220, "aliasName" => 120, _ => 90 })) };
                col.Name = c.Key;
                col.HeaderText = c.Header;
                col.SortMode = DataGridViewColumnSortMode.NotSortable;
                if (c.Key == "@via") col.ReadOnly = true;
                g.Columns.Add(col);
            }
            if (g.Columns.Count > 0) g.Columns[0].Frozen = true;
            _cols = cols;
            _colSig = sig;
        }
        if (g.Rows.Count != _doc.Fields.Count)
        {
            g.Rows.Clear();
            if (_doc.Fields.Count > 0) g.Rows.Add(_doc.Fields.Count);
        }
        for (var r = 0; r < _doc.Fields.Count; r++)
        {
            var f = _doc.Fields[r];
            var row = g.Rows[r];
            row.HeaderCell.Value = (r + 1).ToString();
            var ro = f.ReadOnlyReason is not null;
            row.ReadOnly = ro;
            row.DefaultCellStyle.ForeColor = ro ? AppColors.TextMuted : f.Via is not null ? AppColors.Accent : Color.Empty;
            for (var c = 0; c < _cols.Count; c++)
            {
                var v = GetValue(f, _cols[c]);
                object val = _cols[c].Bool ? BoolOf(v, _cols[c].DefaultTrue) : v ?? "";
                if (!Equals(row.Cells[c].Value, val)) row.Cells[c].Value = val;
                // header/footer chưa có thẻ: vẫn gõ được (tự chèn thẻ); field tự đóng thì cũng được
            }
        }
        if (g.Rows.Count > 0)
        {
            try
            {
                var rr = Math.Min(keepRow, g.Rows.Count - 1);
                var cc = Math.Min(keepCol, g.Columns.Count - 1);
                if (g.CurrentCell is null || g.CurrentCell.RowIndex != rr || g.CurrentCell.ColumnIndex != cc) g.CurrentCell = g.Rows[rr].Cells[cc];
                if (keepTop >= 0 && keepTop < g.Rows.Count) g.FirstDisplayedScrollingRowIndex = keepTop;
            }
            catch (Exception) { }
        }
    }

    private void FillViews()
    {
        var g = _viewGrid;
        if (g.Columns.Count == 0)
        {
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "ctx", HeaderText = "View", Width = NativeSkin.S(110), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "tag", HeaderText = "Thẻ", Width = NativeSkin.S(60), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "name / value", Width = NativeSkin.S(520), SortMode = DataGridViewColumnSortMode.NotSortable });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "xml", HeaderText = "XML", Width = NativeSkin.S(420), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
        }
        var keepRow = g.CurrentCell?.RowIndex ?? 0;
        var keepTop = g.FirstDisplayedScrollingRowIndex;
        var items = _doc?.ViewItems ?? new List<FgViewItem>();
        if (g.Rows.Count != items.Count) { g.Rows.Clear(); if (items.Count > 0) g.Rows.Add(items.Count); }
        for (var r = 0; r < items.Count; r++)
        {
            var v = items[r];
            var row = g.Rows[r];
            row.HeaderCell.Value = (r + 1).ToString();
            row.Cells[0].Value = v.Context;
            row.Cells[1].Value = v.Node.Name == "&" ? "entity" : v.Node.Name;
            row.Cells[2].Value = v.Value;
            row.Cells[3].Value = v.Node.Name == "&" ? "" : v.File.Text[v.Node.Start..v.Node.End].Replace("\r", " ").Replace("\n", " ");
            row.ReadOnly = v.ReadOnlyReason is not null;
            row.DefaultCellStyle.ForeColor = v.ReadOnlyReason is not null ? AppColors.TextMuted : Color.Empty;
        }
        if (g.Rows.Count > 0)
        {
            try
            {
                g.CurrentCell = g.Rows[Math.Min(keepRow, g.Rows.Count - 1)].Cells[2];
                if (keepTop >= 0 && keepTop < g.Rows.Count) g.FirstDisplayedScrollingRowIndex = keepTop;
            }
            catch (Exception) { }
        }
        _tabViews.Text = "Views (" + items.Count + ")";
        _tabFields.Text = "Fields (" + (_doc?.Fields.Count ?? 0) + ")";
    }

    private FgField? CurrentField => _doc is not null && _grid.CurrentCell is { RowIndex: var r } && r >= 0 && r < _doc.Fields.Count ? _doc.Fields[r] : null;
    private FgViewItem? CurrentView => _doc is not null && _viewGrid.CurrentCell is { RowIndex: var r } && r >= 0 && r < _doc.ViewItems.Count ? _doc.ViewItems[r] : null;

    private void ShowXmlOfCurrent()
    {
        string text = "", label = "";
        var ro = true;
        if (_viewsMode && CurrentView is { } v)
        {
            text = v.Node.Name == "&" ? "&" + v.Node.Entity + ";" : (FgSet.IndentOf(v.File.Text, v.Node.Start) ?? "") + v.File.Text[v.Node.Start..v.Node.End];
            label = "XML dòng view" + (v.ReadOnlyReason is not null ? " — " + v.ReadOnlyReason : "");
            ro = v.ReadOnlyReason is not null || v.Node.Name == "&";
        }
        else if (!_viewsMode && CurrentField is { } f)
        {
            text = f.Placeholder ?? (FgSet.IndentOf(f.File.Text, f.Node.Start) ?? "") + f.Xml;
            label = "XML field: " + f.Name + (f.Via is not null ? "  (trong " + f.Via + " → " + Path.GetFileName(f.File.Path) + ")" : "")
                + (f.ReadOnlyReason is not null ? " — " + f.ReadOnlyReason : "");
            ro = f.ReadOnlyReason is not null || f.Placeholder is not null;
        }
        _xml.ReadOnly = false;
        _xml.Text = text;
        _xml.EmptyUndoBuffer();
        _xml.ReadOnly = ro;
        FcodeLexer.Restyle(_xml);
        _xmlLabel.Text = label;
        _xmlApply.Enabled = !ro;
    }

    private void SetViewsMode(bool views)
    {
        EndEdits();
        _viewsMode = views;
        _grid.Visible = !views;
        _viewGrid.Visible = views;
        _btnAdd.Visible = _btnCopy.Visible = !views;
        _btnToView.Visible = !views && _doc?.Kind == "Grid";
        ShowXmlOfCurrent();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _btnUndo.Enabled = _set.CanUndo;
        var dirty = _set.AllFiles.Where(f => f.Dirty).Select(f => Path.GetFileName(f.Path)).ToList();
        if (dirty.Count > 0) SetStatus("Chưa lưu: " + string.Join(", ", dirty) + " (Ctrl+S)");
        else if (_status.Text.StartsWith("Chưa lưu", StringComparison.Ordinal)) SetStatus("");
    }

    private void SetStatus(string text) => _status.Text = text;

    // ------------------------------------------------------------------ sửa

    /// <summary>Chạy 1 thao tác sửa; lỗi (file chỉ đọc…) thì báo; xong thì đổ lại lưới (hoãn — đang trong sự kiện của lưới).</summary>
    private void Run(Action act)
    {
        try { act(); }
        catch (Exception ex) { SetStatus(ex.Message); System.Media.SystemSounds.Beep.Play(); }
        BeginInvoke(new Action(() => { if (IsDisposed) return; FillAll(); RefreshFileBarMarks(); }));
    }

    private void OnFieldCellChanged(int row, int col)
    {
        if (_doc is null || row >= _doc.Fields.Count || col >= _cols.Count) return;
        var f = _doc.Fields[row];
        var c = _cols[col];
        if (f.Placeholder is not null || f.ReadOnlyReason is not null || c.Key == "@via") { Run(() => { }); return; }
        var cell = _grid.Rows[row].Cells[col].Value;
        Run(() =>
        {
            if (c.Bool)
            {
                var on = cell is true;
                _set.SetAttr(f.File, f.Node, c.Key, on == c.DefaultTrue ? null : on ? "true" : "false");
                return;
            }
            var text = (cell as string ?? "").Trim();
            switch (c.Key)
            {
                case "headerV": _set.SetChildAttr(f, "header", "v", text); break;
                case "headerE": _set.SetChildAttr(f, "header", "e", text); break;
                case "footerV": _set.SetChildAttr(f, "footer", "v", text); break;
                case "footerE": _set.SetChildAttr(f, "footer", "e", text); break;
                case "name": Rename(f, text); break;
                default: _set.SetAttr(f.File, f.Node, c.Key, text.Length == 0 ? null : text); break;
            }
        });
    }

    /// <summary>Đổi tên field + sửa luôn chỗ gọi trong &lt;views&gt; của tab đang xem (Grid: &lt;field name&gt;, Dir/Filter: [tên] trong &lt;item value&gt;).</summary>
    private void Rename(FgField f, string name)
    {
        var old = f.Name;
        if (name.Length == 0) throw new InvalidOperationException("Tên field không được trống.");
        if (name == old) return;
        if (_doc!.Fields.Any(x => !ReferenceEquals(x, f) && x.Name == name)) throw new InvalidOperationException("Đã có field " + name + ".");
        _set.SetAttr(f.File, f.Node, "name", name);
        var changed = 0;
        for (var guard = 0; guard < 500; guard++)
        {
            var hit = _doc.ViewItems.FirstOrDefault(v => v.ReadOnlyReason is null && v.Node.Name != "&"
                && (v.Node.Name == "field" ? v.Value == old : v.Value.Contains("[" + old + "]", StringComparison.Ordinal)));
            if (hit is null) break;
            _set.SetAttr(hit.File, hit.Node, hit.KeyAttr, hit.Node.Name == "field" ? name : hit.Value.Replace("[" + old + "]", "[" + name + "]"));
            changed++;
        }
        if (changed > 0) SetStatus($"Đổi tên {old} → {name}, sửa {changed} chỗ trong <views>.");
    }

    private void OnTitleCellChanged(int col)
    {
        if (_doc is null) return;
        var node = col < 2 ? _doc.Title : _doc.SubTitle;
        if (node is null) return;
        var value = (_titleGrid.Rows[0].Cells[col].Value as string ?? "").Trim();
        Run(() => _set.SetAttr(_doc.Main, node, col % 2 == 0 ? "v" : "e", value));
    }

    private void OnViewCellChanged(int row, int col)
    {
        if (_doc is null || row >= _doc.ViewItems.Count || col != 2) return;
        var v = _doc.ViewItems[row];
        if (v.ReadOnlyReason is not null || v.Node.Name == "&") { Run(() => { }); return; }
        var value = (_viewGrid.Rows[row].Cells[col].Value as string ?? "").Trim();
        Run(() => _set.SetAttr(v.File, v.Node, v.KeyAttr, value));
    }

    private void AddField(bool copy)
    {
        if (_doc is null || _viewsMode) return;
        EndEdits();
        var cur = CurrentField;
        if (copy && (cur is null || cur.Placeholder is not null)) return;
        var names = new HashSet<string>(_doc.Fields.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        var baseName = copy ? cur!.Name + "_copy" : "field_moi";
        var name = baseName;
        for (var i = 2; names.Contains(name); i++) name = baseName + i;
        string xml;
        if (copy)
        {
            var src = cur!.Xml;
            var a = cur.Node.Attr("name")!;
            xml = src[..(a.ValueStart - cur.Node.Start)] + name + src[(a.ValueEnd - cur.Node.Start)..];
            xml = DedentContinuation(xml, FgSet.IndentOf(cur.File.Text, cur.Node.Start) ?? "");
        }
        else xml = $"<field name=\"{name}\">\n  <header v=\"\" e=\"\"></header>\n</field>";
        // chèn sau field đang chọn (cùng file, kể cả file include); chọn field chỉ đọc / chưa có field thì chèn cuối <fields> của file chính
        Run(() =>
        {
            if (cur is not null && cur.Placeholder is null && cur.ReadOnlyReason is null) _set.InsertAfter(cur.File, cur.Node, null, xml);
            else if (_doc.FieldsNode is not null && _doc.Main.ReadOnlyReason is null) _set.InsertAfter(_doc.Main, null, _doc.FieldsNode, xml);
            else throw new InvalidOperationException("Không thêm được field: " + (_doc.Main.ReadOnlyReason ?? "file không có <fields>."));
        });
        BeginInvoke(new Action(() =>
        {
            var r = _doc.Fields.FindIndex(x => x.Name == name);
            if (r < 0) return;
            _grid.CurrentCell = _grid.Rows[r].Cells[0];
            _grid.BeginEdit(true);
        }));
    }

    /// <summary>XML cắt từ file: dòng 2 trở đi đã có thụt của field gốc — bỏ phần thụt đó để InsertAfter thụt lại theo chỗ chèn.</summary>
    private static string DedentContinuation(string xml, string indent)
    {
        if (indent.Length == 0) return xml.Replace("\r\n", "\n");
        var lines = xml.Replace("\r\n", "\n").Split('\n');
        for (var i = 1; i < lines.Length; i++) if (lines[i].StartsWith(indent, StringComparison.Ordinal)) lines[i] = lines[i][indent.Length..];
        return string.Join("\n", lines);
    }

    private void DeleteCurrent()
    {
        if (_doc is null) return;
        EndEdits();
        if (_viewsMode)
        {
            if (CurrentView is not { } v) return;
            if (v.ReadOnlyReason is not null) { SetStatus(v.ReadOnlyReason); return; }
            Run(() => _set.DeleteNode(v.File, v.Node));
            SetStatus("Đã xoá dòng view " + v.Value + " (Ctrl+Z để hoàn tác).");
            return;
        }
        if (CurrentField is not { } f) return;
        if (f.Placeholder is not null || f.ReadOnlyReason is not null) { SetStatus(f.ReadOnlyReason ?? "Không xoá được dòng này."); return; }
        var inView = _doc.ViewItems.Any(x => x.Node.Name == "field" ? x.Value == f.Name : x.Value.Contains("[" + f.Name + "]", StringComparison.Ordinal));
        if (inView && MessageBox.Show(FindForm(), $"Field {f.Name} đang được dùng trong <views>. Vẫn xoá khai báo field?", "Bcode — Lưới field",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        Run(() => _set.DeleteNode(f.File, f.Node));
        SetStatus("Đã xoá field " + f.Name + " (Ctrl+Z để hoàn tác).");
    }

    private static List<FgNode> Items(List<FgNode> siblings, params string[] names) => siblings.Where(x => names.Contains(x.Name)).ToList();

    private void MoveCurrent(int dir)
    {
        if (_doc is null) return;
        EndEdits();
        FgFile file; List<FgNode> sib; FgNode node; int keepGridRow;
        if (_viewsMode)
        {
            if (CurrentView is not { ReadOnlyReason: null } v) return;
            (file, sib, node) = (v.File, v.Siblings, v.Node);
            keepGridRow = _viewGrid.CurrentCell!.RowIndex;
        }
        else
        {
            if (CurrentField is not { ReadOnlyReason: null, Placeholder: null } f) return;
            (file, sib, node) = (f.File, f.Siblings, f.Node);
            keepGridRow = _grid.CurrentCell!.RowIndex;
        }
        var items = Items(sib, "field", "item", "&");
        var i = items.IndexOf(node);
        if (i < 0) return;
        FgNode? before;
        if (dir < 0) { if (i == 0) return; before = items[i - 1]; }
        else { if (i == items.Count - 1) return; before = i + 2 < items.Count ? items[i + 2] : null; }
        var movedXml = file.Text[node.Start..node.End];
        var col = Math.Max(0, (_viewsMode ? _viewGrid : _grid).CurrentCell?.ColumnIndex ?? 0);
        string? err = null;
        Run(() => err = _set.Move(file, sib, node, before));
        BeginInvoke(new Action(() =>
        {
            if (err is not null) { SetStatus(err); return; }
            // giữ con trỏ trên chính mục vừa chuyển (tìm theo chữ XML của nó — không đổi khi chuyển)
            var g = _viewsMode ? _viewGrid : _grid;
            var idx = _viewsMode
                ? _doc.ViewItems.FindIndex(x => ReferenceEquals(x.File, file) && x.Node.Name != "&" && x.File.Text[x.Node.Start..x.Node.End] == movedXml)
                : _doc.Fields.FindIndex(x => ReferenceEquals(x.File, file) && x.Placeholder is null && x.Xml == movedXml);
            if (idx < 0) idx = Math.Clamp(keepGridRow + dir, 0, g.Rows.Count - 1);
            if (idx >= 0 && idx < g.Rows.Count) g.CurrentCell = g.Rows[idx].Cells[Math.Min(col, g.Columns.Count - 1)];
        }));
    }

    private string _lastMovedXml = "";

    private void DragField(int src, int target)
    {
        if (_doc is null) return;
        var s = _doc.Fields[src];
        var t = _doc.Fields[target];
        if (s.Placeholder is not null || s.ReadOnlyReason is not null) { SetStatus(s.ReadOnlyReason ?? "Không chuyển được dòng này."); return; }
        // đích trong cùng danh sách → đặt trước / sau nó; đích là field trong &X; của file chính → dùng dòng &X; đó
        FgNode? anchor = ReferenceEquals(t.Siblings, s.Siblings) ? t.Node : s.Siblings.Contains(t.TopItem) ? t.TopItem : null;
        if (anchor is null) { SetStatus("Chỉ đổi thứ tự trong cùng một file (field trong include chỉ kéo trong include đó)."); return; }
        var items = Items(s.Siblings, "field", "item", "&");
        var ti = items.IndexOf(anchor);
        var before = target > src ? (ti + 1 < items.Count ? items[ti + 1] : null) : anchor;
        _lastMovedXml = s.Xml;
        string? err = null;
        Run(() => err = _set.Move(s.File, s.Siblings, s.Node, before));
        BeginInvoke(new Action(() => { if (err is not null) SetStatus(err); SelectFieldByXml(s.File); }));
    }

    private void DragView(int src, int target)
    {
        if (_doc is null) return;
        var s = _doc.ViewItems[src];
        var t = _doc.ViewItems[target];
        if (s.ReadOnlyReason is not null) { SetStatus(s.ReadOnlyReason); return; }
        if (!ReferenceEquals(s.Siblings, t.Siblings)) { SetStatus("Chỉ đổi thứ tự trong cùng một <view> / <category>."); return; }
        var items = Items(s.Siblings, "field", "item", "&");
        var ti = items.IndexOf(t.Node);
        var before = target > src ? (ti + 1 < items.Count ? items[ti + 1] : null) : t.Node;
        string? err = null;
        Run(() => err = _set.Move(s.File, s.Siblings, s.Node, before));
        BeginInvoke(new Action(() => { if (err is not null) SetStatus(err); }));
    }

    private void SelectFieldByXml(FgFile file)
    {
        if (_doc is null) return;
        var idx = _doc.Fields.FindIndex(x => ReferenceEquals(x.File, file) && x.Xml == _lastMovedXml);
        if (idx >= 0 && idx < _grid.Rows.Count) _grid.CurrentCell = _grid.Rows[idx].Cells[0];
    }

    private void AddToView()
    {
        if (_doc is not { Kind: "Grid" } || _viewsMode || CurrentField is not { Placeholder: null } f) return;
        EndEdits();
        if (_doc.ViewItems.Any(v => v.Node.Name == "field" && v.Value == f.Name)) { SetStatus(f.Name + " đã có trong <views>."); return; }
        var view = _doc.Views?.Children.FirstOrDefault(c => c.Name == "view");
        if (view is null) { SetStatus("Grid không có <views><view>."); return; }
        var last = view.Children.LastOrDefault(c => c.Name is "field" or "&");
        Run(() => _set.InsertAfter(_doc.Main, last, last is null ? view : null, $"<field name=\"{f.Name}\"/>"));
        SetStatus("Đã thêm " + f.Name + " vào cuối <view>.");
    }

    private void ApplyXml()
    {
        if (_doc is null || _xml.ReadOnly) return;
        var text = _xml.Text;
        var parsed = FgXml.Parse(text.Trim());
        if (_viewsMode)
        {
            if (CurrentView is not { } v) return;
            if (parsed.Count != 1 || parsed[0].Name != v.Node.Name) { SetStatus("XML phải là đúng 1 thẻ <" + v.Node.Name + ">."); return; }
            Run(() => _set.ReplaceNode(v.File, v.Node, DedentContinuation(text.TrimStart(' ', '\t'), FgSet.IndentOf(v.File.Text, v.Node.Start) ?? "")));
            return;
        }
        if (CurrentField is not { } f) return;
        if (parsed.Count != 1 || parsed[0].Name != "field" || parsed[0].End != text.Trim().Length)
        { SetStatus("XML phải là đúng 1 thẻ <field>…</field> (đủ thẻ đóng)."); return; }
        var indent = FgSet.IndentOf(f.File.Text, f.Node.Start) ?? "";
        // khung hiện kèm thụt của file → bỏ thụt đó ở các dòng sau, ReplaceNode giữ nguyên xuống dòng của file
        var body = DedentContinuation(text.TrimStart(' ', '\t'), indent);
        var reindented = string.Join("\n", body.Split('\n').Select((l, i) => i == 0 || l.Length == 0 ? l : indent + l));
        Run(() => _set.ReplaceNode(f.File, f.Node, reindented));
        SetStatus("Đã áp dụng XML field " + f.Name + ".");
    }

    private void DoUndo()
    {
        EndEdits();
        if (!_set.Undo()) { SetStatus("Không còn gì để hoàn tác."); return; }
        FillAll();
        RefreshFileBarMarks();
        SetStatus("Đã hoàn tác 1 bước.");
    }

    // ------------------------------------------------------------------ lưu / xem / chép

    private void SaveAll()
    {
        var dirty = _set.AllFiles.Where(f => f.Dirty).ToList();
        if (dirty.Count == 0) { SetStatus("Không có gì để lưu."); return; }
        var changed = dirty.Where(FgSet.ChangedOnDisk).ToList();
        if (changed.Count > 0 && MessageBox.Show(FindForm(),
                "File đã bị sửa ở nơi khác sau khi mở:\n" + string.Join("\n", changed.Select(f => f.Path)) + "\n\nVẫn ghi đè?", "Bcode — Lưới field",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        try
        {
            var saved = _set.Save();
            SetStatus($"Đã lưu {saved.Count} file ({DateTime.Now:HH:mm:ss}): " + string.Join(", ", saved.Select(Path.GetFileName)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), "Không lưu được:\n" + ex.Message, "Bcode — Lưới field", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        FillAll();
        RefreshFileBarMarks();
    }

    private void ViewScript()
    {
        EndEdits();
        var dirty = _set.AllFiles.Where(f => f.Dirty).ToList();
        var text = dirty.Count == 0
            ? "Chưa sửa gì — file giống hệt trên đĩa."
            : string.Join("\r\n", dirty.Select(f => "==== " + f.Path + "\r\n" + FgSet.Diff(f).Replace("\n", "\r\n")));
        using var form = new Form
        {
            Text = "View Script — thay đổi chưa lưu",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(NativeSkin.S(1000), NativeSkin.S(640)),
            ShowInTaskbar = false,
        };
        var ed = new Scintilla { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
        SqlScintilla.Configure(ed);
        SqlScintilla.ApplyTheme(ed);
        ed.Text = text;
        ed.ReadOnly = true;
        form.Controls.Add(ed);
        ThemeManager.Apply(form);
        form.KeyPreview = true;
        form.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) form.Close(); };
        form.ShowDialog(FindForm());
    }

    private void CopyFile()
    {
        if (_doc is null) return;
        var files = new System.Collections.Specialized.StringCollection { _doc.Main.Path };
        foreach (var inc in _doc.Fields.Select(f => f.File).Where(f => f.Dirty || f.DiskText != f.Text).Select(f => f.Path).Distinct())
            if (!files.Contains(inc) && File.Exists(inc)) files.Add(inc);
        try
        {
            Clipboard.SetFileDropList(files);
            SetStatus("Đã chép " + files.Count + " file vào clipboard.");
        }
        catch (Exception ex) { SetStatus("Không chép được: " + ex.Message); }
    }
}
