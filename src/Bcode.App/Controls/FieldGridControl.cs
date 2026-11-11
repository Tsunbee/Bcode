using Bcode.App.Services;
using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>
/// "Chế độ triển khai" — khai field dạng LƯỚI như FSD Studio (F1Customize) thay cho sửa XML: chuột phải file Dir / Grid / Filter / Report ở File Lookup
/// hoặc menu ở cây WCommand → tab này mở cả bộ file của chứng từ (SysID). Bố cục: hàng tab file · 1 thanh công cụ (Fields / Views, thêm / xoá / chuyển,
/// chọn cột, khung XML, nạp / lưu / View Script…, SysID) · 1 dòng Title (titleV/E, subTitleV/E) · lưới (trái) + khung XML của dòng đang chọn (phải, ẩn được)
/// · dòng trạng thái. Lưới Fields: mỗi thuộc tính 1 cột (true/false = ô tick, gom 1 nhóm, tiêu đề tô riêng), cột name cố định khi cuộn ngang, cột trống tự ẩn
/// (tắt được), chọn cột ẩn/hiện (nhớ ở AppSettings); field trong &amp;X; include tô nền nhạt + cột Nguồn. Mọi thay đổi sửa thẳng chuỗi gốc
/// (Services/FieldGridDocument.cs) — định dạng, comment, entity, khối &lt;Encrypted&gt; giữ nguyên. Dùng chung cho chế độ Web và App (WinForms thuần).
/// Hiệu năng: lưới double-buffer, màu dòng tô trong CellFormatting (không tạo style riêng cho từng dòng), đổ lại chỉ ghi ô đổi giá trị.
/// </summary>
public sealed class FieldGridControl : UserControl
{
    private sealed record Col(string Key, string Header, bool Bool, bool DefaultTrue = false);

    /// <summary>Mở file bằng editor thường — MainForm mở thành tab trong Bcode (không bật cửa sổ BcodeViewer).</summary>
    public event Action<string>? OpenFileRequested;

    /// <summary>View Script: (tiêu đề, nội dung diff) — MainForm mở thành tab chỉ đọc trong Bcode. Không ai nghe thì hiện hộp riêng.</summary>
    public event Action<string, string>? ScriptViewRequested;

    /// <summary>Người dùng đổi cột ẩn / "Ẩn cột trống" — MainForm ghi vào AppSettings.</summary>
    public event Action? ColumnPrefsChanged;

    /// <summary>Tên cột (thuộc tính) người dùng chọn ẩn — dùng chung mọi loại file.</summary>
    public HashSet<string> HiddenColumns { get; } = new(StringComparer.Ordinal);

    /// <summary>Tự ẩn cột không dòng nào có giá trị (trừ các cột chính name / header / ô tick hay dùng).</summary>
    public bool HideEmptyColumns { get; set; } = true;

    /// <summary>Cột luôn hiện khi "Ẩn cột trống" (vẫn ẩn được bằng tay).</summary>
    private static readonly HashSet<string> CoreColumns = new(StringComparer.Ordinal)
        { "name", "@via", "headerV", "headerE", "width", "allowNulls", "readOnly", "disabled", "hidden", "inactivate" };

    private readonly IReadOnlyList<string> _readOnlyRoots;
    private FgSet _set = new();
    private FgDoc? _doc;
    private string? _clicked;
    private bool _filling;
    private bool _viewsMode;
    private List<Col> _cols = new();
    private string _colSig = "";
    private Color _incBack, _incBackAlt, _boolHead;

    private readonly FlowLayoutPanel _fileBar = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(4, 3, 4, 0) };
    private readonly ToolStrip _toolbar = new() { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden, CanOverflow = true, Padding = new Padding(4, 1, 4, 1) };
    private readonly FlowLayoutPanel _titleRow = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(6, 2, 4, 2) };
    private readonly TextBox[] _titleBoxes = Enumerable.Range(0, 4).Select(_ => new TextBox { Width = NativeSkin.S(190), BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle }).ToArray();
    private readonly Label _banner = new() { Dock = DockStyle.Top, AutoSize = false, Visible = false, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0), AutoEllipsis = true };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
    private readonly DataGridView _grid = NewGrid();
    private readonly DataGridView _viewGrid = NewGrid();
    private readonly Scintilla _xml = new() { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
    private readonly Label _xmlLabel = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
    private readonly Button _xmlApply = new() { Text = "Áp dụng", AutoSize = true, Dock = DockStyle.Right };
    private readonly Panel _statusBar = new() { Dock = DockStyle.Bottom };
    private readonly LinkLabel _pathLink = new() { AutoSize = true, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 10, 0) };
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };

    private readonly ToolStripButton _tabFields = new("Fields") { DisplayStyle = ToolStripItemDisplayStyle.Text, Checked = true };
    private readonly ToolStripButton _tabViews = new("Views") { DisplayStyle = ToolStripItemDisplayStyle.Text };
    private readonly ToolStripButton _btnAdd = new("＋ Thêm") { ToolTipText = "Thêm field mới ngay dưới dòng đang chọn (F4 / Insert) rồi gõ tên." };
    private readonly ToolStripButton _btnCopy = new("⧉ Nhân bản") { ToolTipText = "Nhân bản field đang chọn (chép cả XML) ngay bên dưới rồi gõ tên mới." };
    private readonly ToolStripButton _btnDelete = new("✕ Xoá") { ToolTipText = "Xoá field / dòng view đang chọn (F8) — Ctrl+Z để hoàn tác." };
    private readonly ToolStripButton _btnUp = new("↑") { ToolTipText = "Đưa lên trên (Alt+↑). Kéo tiêu đề dòng (cột số) để đổi chỗ nhanh." };
    private readonly ToolStripButton _btnDown = new("↓") { ToolTipText = "Đưa xuống dưới (Alt+↓)." };
    private readonly ToolStripButton _btnToView = new("→ View") { ToolTipText = "Thêm field đang chọn vào <views> của Grid (F7)." };
    private readonly ToolStripButton _btnUndo = new("↶ Hoàn tác") { ToolTipText = "Hoàn tác 1 bước (Ctrl+Z)." };
    private readonly ToolStripDropDownButton _btnCols = new("Cột") { ToolTipText = "Chọn cột hiện / ẩn; \"Ẩn cột trống\" tự ẩn cột không dòng nào có giá trị." };
    private readonly ToolStripButton _btnXml = new("XML") { Checked = true, ToolTipText = "Hiện / ẩn khung XML của dòng đang chọn." };
    private readonly ToolStripButton _btnSave = new("Save") { ToolTipText = "Lưu mọi file đã sửa (Ctrl+S) — giữ encoding gốc, chép bản cũ vào %AppData%\\Bcode\\field-grid-backup." };
    private readonly ToolStripTextBox _sysId = new() { AutoSize = false, Width = NativeSkin.S(150), ToolTipText = "Gõ SysID khác rồi Enter để mở bộ file khác cùng thư mục Controllers." };
    private readonly ToolStripLabel _voucherCode = new("") { ToolTipText = "Voucher Code (mã chứng từ của menu)" };
    private int _dragRow = -1, _dropRow = -1, _dropSrc = -1;
    private Point _dragStart;

    internal FieldGridControl(IEnumerable<string>? readOnlyRoots = null)
    {
        _readOnlyRoots = readOnlyRoots?.ToList() ?? new List<string>();
        Font = ThemeManager.BaseFont;
        BuildLayout();
        WireEvents();
        ThemeManager.Subscribe(this, () => BeginInvoke(new Action(() =>
        {
            if (IsDisposed) return;
            ApplyColors();
            SqlScintilla.ApplyTheme(_xml);
            FcodeLexer.Apply(_xml);
        })));
    }

    // ------------------------------------------------------------------ dựng giao diện

    private static DataGridView NewGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            SelectionMode = DataGridViewSelectionMode.CellSelect,
            MultiSelect = false,
            EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ShowCellToolTips = true,
        };
        ControlPerf.EnableDoubleBuffering(g);   // cuộn không nháy / không vẽ lại từng phần

        // Tô sáng NGUYÊN dòng đang chọn: lưới để CellSelect (sửa từng ô) nên mặc định chỉ 1 ô sáng → khó thấy đang focus dòng nào.
        // Đổ nền cả dòng theo màu nhấn (nhạt hơn ô đang chọn), ô đang chọn vẫn sáng đậm bên trên.
        int hiRow = -1;
        void Highlight()
        {
            var r = g.CurrentCell?.RowIndex ?? -1;
            if (r == hiRow) return;
            if (hiRow >= 0 && hiRow < g.Rows.Count) g.Rows[hiRow].DefaultCellStyle.BackColor = Color.Empty;
            hiRow = r;
            if (r >= 0 && r < g.Rows.Count) g.Rows[r].DefaultCellStyle.BackColor = Blend(AppColors.Panel, AppColors.Accent, 0.25);
        }
        g.CurrentCellChanged += (_, _) => Highlight();
        g.DataBindingComplete += (_, _) => { hiRow = -1; Highlight(); };
        return g;
    }

    private void BuildLayout()
    {
        // thanh công cụ: 1 hàng duy nhất
        _toolbar.Items.AddRange(new ToolStripItem[]
        {
            _tabFields, _tabViews, new ToolStripSeparator(),
            _btnAdd, _btnCopy, _btnDelete, _btnUp, _btnDown, _btnToView, _btnUndo, new ToolStripSeparator(),
            _btnCols, _btnXml, new ToolStripSeparator(),
            Btn("Load Data", "Nạp lại file của tab đang xem từ đĩa (bỏ thay đổi chưa lưu của tab này).", () => ReloadCurrent()),
            Btn("Reload All", "Tìm lại và nạp lại cả bộ file của SysID (bỏ mọi thay đổi chưa lưu).", () => ReloadAll(_sysId.Text.Trim())),
            _btnSave,
            Btn("View Script", "Xem những dòng XML sẽ đổi so với file trên đĩa (chưa ghi) — mở tab chỉ đọc trong Bcode.", ViewScript),
            Btn("Copy File", "Chép file của tab đang xem vào clipboard (dán vào Explorer / gói cập nhật).", CopyFile),
            Btn("Mở XML", "Mở file của tab đang xem dạng XML trong một tab Bcode.", () => { if (_doc is not null) OpenFileRequested?.Invoke(_doc.Main.Path); }),
        });
        // bên phải (thêm theo thứ tự phải → trái)
        _voucherCode.Alignment = ToolStripItemAlignment.Right;
        _sysId.Alignment = ToolStripItemAlignment.Right;
        var sysLabel = new ToolStripLabel("SysID") { Alignment = ToolStripItemAlignment.Right };
        _toolbar.Items.Add(_voucherCode);
        _toolbar.Items.Add(_sysId);
        _toolbar.Items.Add(sysLabel);
        _btnSave.Font = new Font(_toolbar.Font, FontStyle.Bold);
        _toolbar.Renderer = FlatToolStripRenderer.Shared;

        // dòng Title
        var labels = new[] { "Title", "E", "SubTitle", "E" };
        for (var i = 0; i < 4; i++)
        {
            _titleRow.Controls.Add(new Label { Text = labels[i], AutoSize = true, Margin = new Padding(i % 2 == 0 && i > 0 ? 14 : 2, 5, 2, 0) });
            _titleRow.Controls.Add(_titleBoxes[i]);
            NK.Tip(_titleBoxes[i], new[] { "titleV (tiếng Việt)", "titleE (tiếng Anh)", "subTitle v", "subTitle e" }[i] + " — Enter / rời ô để ghi.");
        }

        // khung XML bên phải
        SqlScintilla.Configure(_xml);
        SqlScintilla.ApplyTheme(_xml);
        FcodeLexer.Apply(_xml);
        var xmlHead = new Panel { Dock = DockStyle.Top, Height = NativeSkin.S(28) };
        xmlHead.Controls.Add(_xmlLabel);
        xmlHead.Controls.Add(_xmlApply);
        _split.Panel2.Controls.Add(_xml);
        _split.Panel2.Controls.Add(xmlHead);
        _viewGrid.Visible = false;
        _split.Panel1.Controls.Add(_grid);
        _split.Panel1.Controls.Add(_viewGrid);

        _statusBar.Height = NativeSkin.S(24);
        _statusBar.Controls.Add(_status);
        _statusBar.Controls.Add(_pathLink);

        _banner.Height = NativeSkin.S(24);
        // tên entity &X; phải hiện nguyên dấu & (Label / nút mặc định ăn & làm phím tắt)
        _banner.UseMnemonic = _xmlLabel.UseMnemonic = _status.UseMnemonic = _pathLink.UseMnemonic = false;
        Controls.Add(_split);
        Controls.Add(_banner);
        Controls.Add(_titleRow);
        Controls.Add(_toolbar);
        Controls.Add(_fileBar);
        Controls.Add(_statusBar);
        NK.Tip(_xmlApply, "Ghi XML đã sửa ở khung này vào file (Ctrl+Enter).");
    }

    private static ToolStripButton Btn(string text, string tip, Action act)
    {
        var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = tip };
        b.Click += (_, _) => act();
        return b;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // sau ThemeManager.Apply của tab (nó đặt kiểu lưới chung) → đặt lại kiểu riêng của lưới field
        BeginInvoke(new Action(() =>
        {
            try
            {
                _split.Panel2MinSize = NativeSkin.S(160);
                _split.SplitterDistance = Math.Max(NativeSkin.S(300), Width * 70 / 100);
            }
            catch (Exception) { /* kích thước chưa có */ }
            ApplyColors();
        }));
    }

    private static Color Blend(Color a, Color b, double t)
        => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    private void ApplyColors()
    {
        _banner.BackColor = Blend(AppColors.Panel, AppColors.Warning, 0.12);
        _banner.ForeColor = AppColors.Text;
        _statusBar.BackColor = AppColors.PanelAlt;
        _status.ForeColor = AppColors.TextMuted;
        _pathLink.LinkColor = AppColors.Accent;
        _pathLink.ActiveLinkColor = AppColors.AccentHover;
        _toolbar.BackColor = AppColors.Panel;
        _toolbar.ForeColor = AppColors.Text;
        foreach (ToolStripItem it in _toolbar.Items) it.ForeColor = AppColors.Text;
        _sysId.BackColor = AppColors.Input;
        _sysId.ForeColor = AppColors.Text;
        _titleRow.BackColor = AppColors.Panel;
        foreach (var rb in _fileBar.Controls.OfType<RadioButton>()) PaintToggle(rb);
        _fileBar.BackColor = AppColors.Background;
        _incBack = Blend(AppColors.Panel, AppColors.Accent, 0.09);
        _incBackAlt = Blend(AppColors.PanelAlt, AppColors.Accent, 0.09);
        _boolHead = Blend(AppColors.PanelAlt, AppColors.Accent, 0.16);
        StyleGrid(_grid);
        StyleGrid(_viewGrid);
        ApplyBoolHeaderColors();
        _grid.Invalidate();
        _viewGrid.Invalidate();
    }

    /// <summary>Kiểu riêng của lưới field (đặt sau kiểu chung của ThemeManager): kẻ ô, xen màu dòng rõ hơn, dòng thấp gọn, tiêu đề vừa chữ.</summary>
    private void StyleGrid(DataGridView g)
    {
        g.BackgroundColor = AppColors.Panel;
        g.BorderStyle = System.Windows.Forms.BorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.Single;
        g.GridColor = Blend(AppColors.Panel, AppColors.Border, 0.75);
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.RowHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.DefaultCellStyle.BackColor = AppColors.Panel;
        g.DefaultCellStyle.ForeColor = AppColors.Text;
        g.DefaultCellStyle.SelectionBackColor = AppColors.Selection;
        g.DefaultCellStyle.SelectionForeColor = AppColors.Text;
        g.DefaultCellStyle.Padding = new Padding(NativeSkin.Px(g, 3), 0, NativeSkin.Px(g, 3), 0);
        g.AlternatingRowsDefaultCellStyle.BackColor = Blend(AppColors.Panel, AppColors.Text, 0.045);
        g.AlternatingRowsDefaultCellStyle.SelectionBackColor = AppColors.Selection;
        g.AlternatingRowsDefaultCellStyle.SelectionForeColor = AppColors.Text;
        g.ColumnHeadersDefaultCellStyle.BackColor = AppColors.PanelAlt;
        g.ColumnHeadersDefaultCellStyle.ForeColor = AppColors.Text;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = AppColors.PanelAlt;
        g.ColumnHeadersDefaultCellStyle.Font = ThemeManager.BaseFont;
        g.ColumnHeadersDefaultCellStyle.Padding = new Padding(NativeSkin.Px(g, 3), 0, NativeSkin.Px(g, 3), 0);
        g.RowHeadersDefaultCellStyle.BackColor = AppColors.PanelAlt;
        g.RowHeadersDefaultCellStyle.ForeColor = AppColors.TextMuted;
        g.RowHeadersDefaultCellStyle.SelectionBackColor = AppColors.Selection;
        g.RowHeadersWidth = NativeSkin.Px(g, 40);
        g.ColumnHeadersHeight = NativeSkin.Dens(g, 26);
        var h = NativeSkin.Dens(g, 23);
        g.RowTemplate.Height = h;
        foreach (DataGridViewRow r in g.Rows) if (r.Height != h) r.Height = h;
    }

    private void ApplyBoolHeaderColors()
    {
        for (var i = 0; i < _cols.Count && i < _grid.Columns.Count; i++)
            _grid.Columns[i].HeaderCell.Style.BackColor = _cols[i].Bool ? _boolHead : Color.Empty;
    }

    private static void PaintToggle(RadioButton rb)
    {
        rb.FlatAppearance.BorderSize = 0;
        rb.FlatAppearance.CheckedBackColor = AppColors.Panel;
        rb.FlatAppearance.MouseOverBackColor = AppColors.PanelAlt;
        rb.BackColor = rb.Checked ? AppColors.Panel : AppColors.Background;
        rb.ForeColor = rb.Checked ? AppColors.Accent : AppColors.TextMuted;
        rb.Font = new Font(rb.Font, rb.Checked ? FontStyle.Bold : FontStyle.Regular);
        rb.Margin = new Padding(0, 0, 2, 0);
        rb.Padding = new Padding(8, 2, 8, 2);
    }

    private void WireEvents()
    {
        _tabFields.Click += (_, _) => SetViewsMode(false);
        _tabViews.Click += (_, _) => SetViewsMode(true);
        _btnAdd.Click += (_, _) => AddField(copy: false);
        _btnCopy.Click += (_, _) => AddField(copy: true);
        _btnDelete.Click += (_, _) => DeleteCurrent();
        _btnUp.Click += (_, _) => MoveCurrent(-1);
        _btnDown.Click += (_, _) => MoveCurrent(1);
        _btnToView.Click += (_, _) => AddToView();
        _btnUndo.Click += (_, _) => DoUndo();
        _btnSave.Click += (_, _) => { EndEdits(); SaveAll(); };
        _btnXml.Click += (_, _) => { _btnXml.Checked = !_btnXml.Checked; _split.Panel2Collapsed = !_btnXml.Checked; };
        _btnCols.DropDownOpening += (_, _) => BuildColumnMenu();
        _xmlApply.Click += (_, _) => ApplyXml();
        _pathLink.LinkClicked += (_, _) => { if (_doc is not null) OpenFileRequested?.Invoke(_doc.Main.Path); };
        _sysId.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ReloadAll(_sysId.Text.Trim()); } };
        for (var i = 0; i < 4; i++)
        {
            var idx = i;
            _titleBoxes[i].KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnTitleChanged(idx); } };
            _titleBoxes[i].Leave += (_, _) => OnTitleChanged(idx);
        }

        foreach (var g in new[] { _grid, _viewGrid })
        {
            g.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (g.IsCurrentCellDirty && g.CurrentCell is DataGridViewCheckBoxCell) g.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            g.DataError += (_, e) => e.ThrowException = false;
        }
        _grid.CellValueChanged += (_, e) => { if (!_filling && e.RowIndex >= 0) OnFieldCellChanged(e.RowIndex, e.ColumnIndex); };
        _viewGrid.CellValueChanged += (_, e) => { if (!_filling && e.RowIndex >= 0) OnViewCellChanged(e.RowIndex, e.ColumnIndex); };
        _grid.CurrentCellChanged += (_, _) => { if (!_filling) ShowXmlOfCurrent(); };
        _viewGrid.CurrentCellChanged += (_, _) => { if (!_filling) ShowXmlOfCurrent(); };
        // màu dòng tô lúc vẽ (không tạo DataGridViewCellStyle riêng cho từng dòng — đỡ tốn khi cuộn)
        _grid.CellFormatting += (_, e) =>
        {
            if (_doc is null || e.RowIndex < 0 || e.RowIndex >= _doc.Fields.Count || e.CellStyle is null) return;
            var f = _doc.Fields[e.RowIndex];
            if (f.Via is not null) e.CellStyle.BackColor = e.RowIndex % 2 == 0 ? _incBack : _incBackAlt;
            if (f.ReadOnlyReason is not null || (e.ColumnIndex < _cols.Count && _cols[e.ColumnIndex].Key == "@via")) e.CellStyle.ForeColor = AppColors.TextMuted;
        };
        _viewGrid.CellFormatting += (_, e) =>
        {
            if (_doc is null || e.RowIndex < 0 || e.RowIndex >= _doc.ViewItems.Count || e.CellStyle is null) return;
            var v = _doc.ViewItems[e.RowIndex];
            if (v.Via is not null) e.CellStyle.BackColor = e.RowIndex % 2 == 0 ? _incBack : _incBackAlt;
            if (v.ReadOnlyReason is not null || e.ColumnIndex is 0 or 1 or 4) e.CellStyle.ForeColor = AppColors.TextMuted;
        };
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
            g.DragOver += (_, e) =>
            {
                if (e.Data?.GetData(typeof(int)) is not int src) { e.Effect = DragDropEffects.None; return; }
                e.Effect = DragDropEffects.Move;
                var p = g.PointToClient(new Point(e.X, e.Y));
                // tự cuộn khi rê sát mép trên / dưới
                var rowH = g.RowTemplate.Height;
                if (p.Y < g.ColumnHeadersHeight + rowH && g.FirstDisplayedScrollingRowIndex > 0) g.FirstDisplayedScrollingRowIndex--;
                else if (p.Y > g.ClientSize.Height - rowH && g.FirstDisplayedScrollingRowIndex + g.DisplayedRowCount(false) < g.Rows.Count) g.FirstDisplayedScrollingRowIndex++;
                var target = g.HitTest(p.X, p.Y).RowIndex;
                if (target < 0) target = p.Y > g.ColumnHeadersHeight ? g.Rows.Count - 1 : 0;
                if (target != _dropRow || src != _dropSrc) { _dropRow = target; _dropSrc = src; g.Invalidate(); }
            };
            g.DragLeave += (_, _) => { _dropRow = -1; g.Invalidate(); };
            g.DragDrop += (_, e) =>
            {
                var target = _dropRow;
                _dropRow = -1;
                g.Invalidate();
                if (e.Data?.GetData(typeof(int)) is not int src) return;
                if (target == src || target < 0) return;
                if (ReferenceEquals(g, _grid)) DragField(src, target); else DragView(src, target);
            };
            // vạch chỉ chỗ thả: trên dòng đích khi kéo lên, dưới dòng đích khi kéo xuống
            g.Paint += (_, e) =>
            {
                if (_dropRow < 0 || _dropRow >= g.Rows.Count || _dropRow == _dropSrc) return;
                var r = g.GetRowDisplayRectangle(_dropRow, false);
                if (r.Height == 0) return;
                var y = _dropRow > _dropSrc ? r.Bottom - 2 : r.Top;
                using var b = new SolidBrush(AppColors.Accent);
                e.Graphics.FillRectangle(b, 0, y - 1, g.ClientSize.Width, NativeSkin.Px(g, 3));
            };
        }
        _xml.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; ApplyXml(); } };
    }

    private void BuildColumnMenu()
    {
        var dd = _btnCols.DropDownItems;
        dd.Clear();
        var hideEmpty = new ToolStripMenuItem("Ẩn cột trống") { Checked = HideEmptyColumns, ToolTipText = "Cột không dòng nào có giá trị thì ẩn (trừ name, header, width, các ô tick chính)." };
        hideEmpty.Click += (_, _) => { HideEmptyColumns = !HideEmptyColumns; ColumnPrefsChanged?.Invoke(); ApplyColumnVisibility(); };
        dd.Add(hideEmpty);
        var showAll = new ToolStripMenuItem("Hiện tất cả cột");
        showAll.Click += (_, _) => { HiddenColumns.Clear(); HideEmptyColumns = false; ColumnPrefsChanged?.Invoke(); ApplyColumnVisibility(); };
        dd.Add(showAll);
        dd.Add(new ToolStripSeparator());
        foreach (var c in _cols)
        {
            if (c.Key == "name") continue;
            var empty = HideEmptyColumns && !CoreColumns.Contains(c.Key) && IsEmptyColumn(c);
            var item = new ToolStripMenuItem(c.Header + (c.Bool ? "  ☑" : "") + (empty ? "  (trống — đang ẩn)" : "")) { Checked = !HiddenColumns.Contains(c.Key), CheckOnClick = false };
            item.Click += (_, _) =>
            {
                if (!HiddenColumns.Remove(c.Key)) HiddenColumns.Add(c.Key);
                ColumnPrefsChanged?.Invoke();
                ApplyColumnVisibility();
            };
            dd.Add(item);
        }
        ThemeManager.ApplyMenu(_btnCols.DropDown);
    }

    private bool IsEmptyColumn(Col c)
    {
        if (_doc is null) return true;
        foreach (var f in _doc.Fields)
        {
            if (f.Placeholder is not null) continue;
            if (!string.IsNullOrEmpty(GetValue(f, c))) return false;
        }
        return true;
    }

    private void ApplyColumnVisibility()
    {
        for (var i = 0; i < _cols.Count && i < _grid.Columns.Count; i++)
        {
            var c = _cols[i];
            var show = c.Key == "name" || (!HiddenColumns.Contains(c.Key) && !(HideEmptyColumns && !CoreColumns.Contains(c.Key) && IsEmptyColumn(c)));
            if (_grid.Columns[i].Visible != show) _grid.Columns[i].Visible = show;
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        var inXml = _xml.Focused;
        var editing = _grid.IsCurrentCellInEditMode || _viewGrid.IsCurrentCellInEditMode || _sysId.Focused || _titleBoxes.Any(t => t.Focused);
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
        foreach (var g in new[] { _grid, _viewGrid }) if (g.IsCurrentCellInEditMode) g.EndEdit();
        for (var i = 0; i < 4; i++) if (_titleBoxes[i].Focused) OnTitleChanged(i);
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
        _voucherCode.Text = string.IsNullOrWhiteSpace(voucherCode) ? "" : "Voucher: " + voucherCode;
        LoadSet(controllersRoot, sysId, null);
    }

    /// <summary>Tab này đang mở đúng bộ (thư mục Controllers + SysID) — MainForm chuyển sang tab cũ thay vì mở tab mới.</summary>
    public bool IsSet(string? controllersRoot, string sysId)
        => string.Equals(_set.SysId, sysId, StringComparison.OrdinalIgnoreCase)
           && string.Equals(_set.ControllersRoot.TrimEnd('\\'), (controllersRoot ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public string TitleText => "Lưới field · " + (_set.SysId.Length > 0 ? _set.SysId : "?");

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
            var dirty = d.Main.Dirty || d.Fields.Any(f => f.File.Dirty) || d.ViewItems.Any(v => v.File.Dirty) || d.TitleFile?.Dirty == true;
            var t = (dirty ? "● " : "") + d.TabTitle;
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
        NK.Tip(_pathLink, d is null ? "" : d.Main.Path + " — bấm để mở XML");
        FillAll();
        SetViewsMode(_viewsMode);
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
        var msgs = new List<string>();
        if (_doc is not null)
        {
            if (_doc.Main.ReadOnlyReason is { } ro) msgs.Add(ro);
            var incRo = _doc.Fields.Where(f => f.Via is not null && f.ReadOnlyReason is not null).Select(f => f.Via).Distinct().ToList();
            if (incRo.Count > 0) msgs.Add("Field trong " + string.Join(", ", incRo) + " chỉ xem — " + _doc.Fields.First(f => f.Via == incRo[0] && f.ReadOnlyReason is not null).ReadOnlyReason);
            msgs.AddRange(_doc.Problems);
            if (_doc.FieldsNode is null) msgs.Add("Không tìm thấy <fields> (ở cấp gốc hay trong entity ngay dưới thẻ gốc).");
            // phần thân nằm trong file include (thường dùng chung cho nhiều file): nói rõ sửa là sửa file đó
            if (_doc.FieldsFile is { } ff && !ReferenceEquals(ff, _doc.Main))
                msgs.Add($"<fields> nằm trong {_doc.FieldsVia} → {ShortPath(ff.Path)} — sửa ở đây là sửa file include này (file khác include nó cũng đổi theo).");
            if (_doc.Main.HasEncrypted && _doc.Main.ReadOnlyReason is null)
                msgs.Add("Có khối <Encrypted>: khối mã hoá giữ nguyên từng ký tự — field chứa nó sửa được thuộc tính / header, khung XML chỉ xem.");
            var roAttr = _doc.Fields.Select(f => f.File).Append(_doc.Main).Where(f => f.ReadOnlyAttr && f.ReadOnlyReason is null).Select(f => Path.GetFileName(f.Path)).Distinct().ToList();
            if (roAttr.Count > 0)
                msgs.Add("Đặt Read-only: " + string.Join(", ", roAttr) + " — khi lưu Bcode tạm gỡ rồi đặt lại.");
        }
        _banner.Text = msgs.Count == 0 ? "" : "⚠ " + string.Join("   ·   ", msgs);
        _banner.Visible = msgs.Count > 0;
        NK.Tip(_banner, string.Join("\n", msgs));
    }

    private (FgFile? File, FgNode? Node, string Attr) TitleSlot(int i) => i switch
    {
        0 => (_doc?.TitleFile, _doc?.Title, "v"),
        1 => (_doc?.TitleFile, _doc?.Title, "e"),
        2 => (_doc?.SubTitleFile, _doc?.SubTitle, "v"),
        _ => (_doc?.SubTitleFile, _doc?.SubTitle, "e"),
    };

    private void FillTitle()
    {
        for (var i = 0; i < 4; i++)
        {
            var (file, node, attr) = TitleSlot(i);
            var a = node?.Attr(attr);
            var box = _titleBoxes[i];
            var text = a is null || file is null ? "" : file.Text[a.ValueStart..a.ValueEnd];
            if (box.Text != text) box.Text = text;
            box.ReadOnly = node is null || file is null || file.ReadOnlyReason is not null;
            box.Tag = text;   // giá trị đã đổ — để Leave không ghi lại khi không đổi
            box.BackColor = box.ReadOnly ? AppColors.PanelAlt : AppColors.Input;
            box.ForeColor = box.ReadOnly ? AppColors.TextMuted : AppColors.Text;
            if (node is null) NK.Tip(box, "File không có thẻ <" + (i < 2 ? "title" : "subTitle") + ">.");
        }
    }

    private List<Col> ColumnsFor(FgDoc d)
    {
        var cols = new List<Col> { new("name", "name", false), new("@via", "Nguồn", false), new("headerV", "headerV", false), new("headerE", "headerE", false) };
        switch (d.Kind)
        {
            case "Grid":
                cols.AddRange(new Col[] { new("width", "width", false), new("clientDefault", "clientDefault", false), new("defaultValue", "defaultValue", false),
                    new("dataFormatString", "dataFormatString", false), new("align", "align", false), new("aliasName", "aliasName", false), new("type", "type", false),
                    new("allowNulls", "allowNulls", true, true), new("readOnly", "readOnly", true), new("hidden", "hidden", true), new("inactivate", "inactivate", true),
                    new("external", "external", true), new("allowFilter", "allowFilter", true), new("allowSorting", "allowSorting", true), new("isPrimaryKey", "isPrimaryKey", true) });
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
        // thuộc tính khác có trong file: mỗi cái 1 cột; toàn true/false thì là ô tick (mặc định false) — ô tick xếp liền nhóm ô tick
        var known = new HashSet<string>(cols.Select(c => c.Key), StringComparer.Ordinal);
        var extra = new List<string>();
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var hasFooter = d.Kind == "Grid";
        foreach (var f in d.Fields)
        {
            if (f.Placeholder is not null) continue;
            foreach (var a in f.Node.Attrs)
            {
                if (known.Contains(a.Name)) continue;
                if (!values.TryGetValue(a.Name, out var list)) { values[a.Name] = list = new(); extra.Add(a.Name); }
                list.Add(f.File.Text[a.ValueStart..a.ValueEnd]);
            }
            if (f.Node.Child("footer") is not null) hasFooter = true;
        }
        var lastBool = cols.FindLastIndex(c => c.Bool);
        foreach (var name in extra)
        {
            var isBool = values[name].All(v => v is "true" or "false" or "True" or "False");
            if (isBool && lastBool >= 0) cols.Insert(++lastBool, new Col(name, name, true));
            else cols.Add(new Col(name, name, isBool));
        }
        if (hasFooter) { cols.Add(new("footerV", "footerV", false)); cols.Add(new("footerE", "footerE", false)); }
        return cols;
    }

    private string? GetValue(FgField f, Col c) => c.Key switch
    {
        "headerV" => f.Sub("header", "v"),
        "headerE" => f.Sub("header", "e"),
        "footerV" => f.Sub("footer", "v"),
        "footerE" => f.Sub("footer", "e"),
        "@via" => f.Via is null ? "" : f.Via.TrimEnd(';').Split(';')[^1] + ";",
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
            var headFont = g.ColumnHeadersDefaultCellStyle.Font ?? Font;
            foreach (var c in cols)
            {
                // bề rộng tối thiểu = đủ chữ tiêu đề + chỗ cho viền; ô tick hẹp, gom 1 nhóm (tiêu đề tô riêng)
                var head = TextRenderer.MeasureText(c.Header, headFont).Width + NativeSkin.S(12);
                DataGridViewColumn col = c.Bool
                    ? new DataGridViewCheckBoxColumn { Width = Math.Max(NativeSkin.S(46), head), FlatStyle = FlatStyle.Flat }
                    : new DataGridViewTextBoxColumn { Width = Math.Max(head, NativeSkin.S(c.Key switch { "name" => 130, "headerV" or "headerE" => 150, "@via" => 110, "aliasName" => 120, _ => 80 })) };
                col.Name = c.Key;
                col.HeaderText = c.Header;
                col.ToolTipText = c.Key == "@via" ? "Field nằm trong entity include nào (trống = khai ngay trong file)" : c.Header + (c.Bool ? " (ô tick" + (c.DefaultTrue ? ", mặc định true)" : ")") : "");
                col.SortMode = DataGridViewColumnSortMode.NotSortable;
                if (c.Key == "@via") col.ReadOnly = true;
                g.Columns.Add(col);
            }
            if (g.Columns.Count > 0) g.Columns[0].Frozen = true;   // name cố định khi cuộn ngang
            _cols = cols;
            _colSig = sig;
            ApplyBoolHeaderColors();
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
            var num = (r + 1).ToString();
            if (!Equals(row.HeaderCell.Value, num)) row.HeaderCell.Value = num;
            var ro = f.ReadOnlyReason is not null;
            if (row.ReadOnly != ro) row.ReadOnly = ro;
            for (var c = 0; c < _cols.Count; c++)
            {
                var v = GetValue(f, _cols[c]);
                object val = _cols[c].Bool ? BoolOf(v, _cols[c].DefaultTrue) : v ?? "";
                if (!Equals(row.Cells[c].Value, val)) row.Cells[c].Value = val;
            }
        }
        ApplyColumnVisibility();
        if (g.Rows.Count > 0)
        {
            try
            {
                var rr = Math.Min(keepRow, g.Rows.Count - 1);
                var cc = Math.Min(keepCol, g.Columns.Count - 1);
                if (!g.Columns[cc].Visible) cc = 0;
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
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "ctx", HeaderText = "View", Width = NativeSkin.S(100), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "tag", HeaderText = "Thẻ", Width = NativeSkin.S(50), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "value", HeaderText = "name / value", Width = NativeSkin.S(480), SortMode = DataGridViewColumnSortMode.NotSortable });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "xml", HeaderText = "XML", Width = NativeSkin.S(360), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            g.Columns.Add(new DataGridViewTextBoxColumn { Name = "via", HeaderText = "Nguồn", Width = NativeSkin.S(140), ReadOnly = true, SortMode = DataGridViewColumnSortMode.NotSortable });
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
            row.Cells[4].Value = v.Via is null ? "" : v.Via.TrimEnd(';').Split(';')[^1] + ";";
            row.ReadOnly = v.ReadOnlyReason is not null;
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
            label = "Dòng view" + (v.Via is not null ? " · " + Path.GetFileName(v.File.Path) : "") + (v.ReadOnlyReason is not null ? " — " + v.ReadOnlyReason : "");
            ro = v.ReadOnlyReason is not null || v.Node.Name == "&";
        }
        else if (!_viewsMode && CurrentField is { } f)
        {
            text = f.Placeholder ?? (FgSet.IndentOf(f.File.Text, f.Node.Start) ?? "") + f.Xml;
            label = f.Name + (f.Via is not null ? " · " + f.Via + " → " + Path.GetFileName(f.File.Path) : "")
                + (f.ReadOnlyReason is not null ? " — " + f.ReadOnlyReason
                   : f.Encrypted ? " — có khối <Encrypted>: khung XML chỉ xem, sửa thuộc tính / header trên lưới" : "");
            ro = f.ReadOnlyReason is not null || f.Placeholder is not null || f.Encrypted;
        }
        _xml.ReadOnly = false;
        _xml.Text = text;
        _xml.EmptyUndoBuffer();
        _xml.ReadOnly = ro;
        FcodeLexer.Restyle(_xml);
        _xmlLabel.Text = label;
        NK.Tip(_xmlLabel, label);
        _xmlApply.Enabled = !ro;
    }

    private void SetViewsMode(bool views)
    {
        EndEdits();
        _viewsMode = views;
        _tabFields.Checked = !views;
        _tabViews.Checked = views;
        _grid.Visible = !views;
        _viewGrid.Visible = views;
        _btnAdd.Visible = _btnCopy.Visible = !views;
        _btnToView.Visible = !views && _doc?.Kind == "Grid";
        _btnCols.Visible = !views;
        ShowXmlOfCurrent();
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _btnUndo.Enabled = _set.CanUndo;
        var dirty = _set.AllFiles.Where(f => f.Dirty).Select(f => Path.GetFileName(f.Path)).ToList();
        _btnSave.Text = dirty.Count > 0 ? "● Save" : "Save";
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

    private void OnTitleChanged(int i)
    {
        var box = _titleBoxes[i];
        var (file, node, attr) = TitleSlot(i);
        if (_doc is null || node is null || file is null || box.ReadOnly) return;
        var value = box.Text.Trim();
        if (value == (box.Tag as string ?? "")) return;   // không đổi gì
        box.Tag = value;
        Run(() => _set.SetAttr(file, node, attr, value));
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
            else if (_doc.FieldsNode is not null && _doc.FieldsFile is { ReadOnlyReason: null } ff) _set.InsertAfter(ff, null, _doc.FieldsNode, xml);
            else throw new InvalidOperationException("Không thêm được field: " + (_doc.FieldsFile?.ReadOnlyReason ?? "file không có <fields>."));
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
        var vf = _doc.ViewsFile ?? _doc.Main;
        Run(() => _set.InsertAfter(vf, last, last is null ? view : null, $"<field name=\"{f.Name}\"/>"));
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
        if (ScriptViewRequested is not null) { ScriptViewRequested("View Script · " + _set.SysId, text); return; }
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
