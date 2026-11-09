using System.Text.RegularExpressions;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// "Note (New)" (Advance Note) ở chế độ App/WinForms — cùng chức năng với trang advnote.html (<see cref="AdvanceNoteControl"/>) nhưng bằng control WinForms, không WebView2:
/// danh sách Request (tick để Generate nhiều y/c), mỗi y/c khai báo Gen All / File Path / SQL Object / SQL Top-Bottom Script (App và Sys riêng) / Table liên quan,
/// Xem trước gói (+ cây gói, bấm xem nội dung từng mục, mở script vào tab Query), Generate Update, lịch sử gói đã gen, Sync yêu cầu từ quản lý yêu cầu, Request Content.
/// Tự lưu sau 0,7 giây không gõ (Ctrl+S lưu ngay). Dữ liệu + nghiệp vụ dùng chung <see cref="AdvanceNoteService"/> / <see cref="GenAllService"/> / <see cref="AdvancePackageBuilder"/>.
/// </summary>
public sealed class NativeAdvanceNoteControl : UserControl
{
    private readonly AdvanceNoteService _store;
    private readonly GenAllService _genAll;
    private readonly Func<Workspace?> _workspace;
    private readonly AppSettings? _settings;

    private List<AdvanceRequest> _requests = new();
    private string _loadedFor = "";
    private AdvanceRequest? _cur;
    private readonly HashSet<string> _checked = new();
    private bool _loading;                 // đang đổ dữ liệu vào ô — không coi là người dùng sửa
    private bool _scriptSys;               // đang xem phần SQL Object / Top / Bottom của database Sys
    private bool _folderTouched;
    private string _defaultFolder = "";
    private List<RelatedTable> _tblList = new();
    private List<string>? _tableNames;
    private List<PackageItem> _preview = new();

    // ---- giao diện ----
    private readonly BarFlow _toolbar = new() { Dock = DockStyle.Top, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 5, 8, 5) };
    private readonly RoundedInput _syncProg = new() { PlaceholderText = "Tên lập trình (ma_lt1)", Width = NativeSkin.S(190) };
    private readonly Label _status = new() { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(12, 8, 0, 0) };
    private readonly Label _listTitle = new() { Dock = DockStyle.Top, Height = NativeSkin.S(28), Padding = new Padding(10, 6, 0, 0), Text = "Request List", AutoEllipsis = true };
    private readonly RoundedInput _listFilter = NK.Input("Lọc mã y/c (nhiều mã: cách nhau dấu cách / phẩy)…");
    private readonly System.Windows.Forms.Timer _listFilterTimer = new() { Interval = 120 };
    private readonly DataGridView _list = new() { Dock = DockStyle.Fill };
    private readonly Panel _pkgPanel = new() { Dock = DockStyle.Bottom, Height = NativeSkin.S(260), Visible = false };
    private readonly TreeView _pkgTree = new() { Dock = DockStyle.Fill };
    private readonly Label _pkgHint = new() { Dock = DockStyle.Top, Height = NativeSkin.S(28), Padding = new Padding(10, 6, 0, 0), Text = "Cây gói update" };
    private readonly Label _noSel = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "Chọn 1 y/c bên trái, hoặc bấm “＋ New Request”." };
    private readonly FlowLayoutPanel _stack = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12, 10, 12, 20), Visible = false };
    private readonly PillButton _btnDelete;
    private readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 700 };

    private readonly RoundedInput _fName = NK.Input();
    private readonly CheckBox _fMobile = NK.Check("Mobile"), _fDone = NK.Check("Done");
    private readonly RoundedInput _fGenAll = NK.Memo(10, "SVTran\r\nARTran"), _fFilePaths = NK.Memo(10, @"\\server\...\App_Data\Controllers\Dir\SVTran.f"), _fProcedures = NK.Memo(10, "fs_PostARTran"),
        _fTop = NK.Memo(10, "-- script chạy đầu tiên"), _fBottom = NK.Memo(10, "-- script chạy sau cùng");
    private readonly PillButton _swApp, _swSys, _btnBottom, _btnOpenAll;
    private int _viewIdx = -1;             // mục gói đang bung nội dung (bấm lại cùng dòng = thu gọn, như trang web)
    private readonly Label _hDb = NK.Muted("");
    private readonly Label _tObj = new() { AutoSize = true, BackColor = Color.Transparent }, _tTop = new() { AutoSize = true, BackColor = Color.Transparent }, _tBottom = new() { AutoSize = true, BackColor = Color.Transparent };
    private readonly CardPanel _cBottom, _cObj, _cTop;
    private readonly RoundedInput _tblAddName = NK.Input("Nhập tên table / view để thêm tay... (bảng:điều kiện = lấy dữ liệu theo điều kiện)");
    private readonly Label _tblInfo = NK.Muted("Nhập controller ở Gen All (hoặc bấm “Tải table”) để liệt kê các table liên quan.");
    private readonly DataGridView _tblGrid = new() { Height = NativeSkin.S(220) };
    private readonly Label _previewSummary = NK.Muted("Bấm “Xem trước gói” để xem những file / script sẽ được đưa vào gói update của y/c này.");
    private readonly DataGridView _items = NK.Grid(("kind", "Loại", 80), ("origin", "Nguồn", 190), ("rel", "Đường dẫn trong gói", 0), ("open", "", 60));
    private readonly Panel _viewHost = new() { Height = NativeSkin.S(220), Visible = false };
    private readonly ScintillaNET.Scintilla _viewer = new() { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
    private readonly FlowLayoutPanel _warnings = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
    private readonly RoundedInput _gSave = NK.Input("", mono: true), _gFolder = NK.Input("", mono: true);
    private readonly CheckBox _gSplit = NK.Check("Tách theo từng y/c");
    private readonly CheckBox _gDone = NK.Check("Done sau khi gen");   // Generate xong tự đánh dấu Done (AppSettings.NoteDoneAfterGen)
    private readonly CheckBox _gZip = NK.Check("Nén .zip khi gen lên mạng"), _gNet = NK.Check("Gen vào đường dẫn mạng");
    private readonly RoundedInput _gNetRoot = NK.Input("", mono: true);
    private readonly Label _gNetHint = NK.Muted("");
    private readonly FlowLayoutPanel _result = new() { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Visible = false };
    private readonly FlowLayoutPanel _history = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Color.Transparent };
    private bool _busy;

    // ---- 👤 Note của: xem note của người dùng KHÁC cùng dự án (chỉ đọc) — như advnote.html ----
    /// <summary>null = note của mình; khác null = tên (máy) người có note đang xem — _requests là note của họ, KHÔNG bao giờ ghi file đó.</summary>
    private string? _viewOwner;
    private DateTime? _viewModified;
    private int _viewVersion;
    private readonly PillButton _btnOwner;
    private readonly FlowLayoutPanel _roBar = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Visible = false, Padding = new Padding(8, 3, 8, 3) };
    private readonly Label _roText = new() { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 7, 10, 0) };
    private readonly List<Control> _roOff = new();   // nút / ô bị khoá khi đang xem note người khác

    /// <summary>Yêu cầu mở script (theo thứ tự chạy) vào các tab SQL Query — MainForm tạo tab.</summary>
    public event Action<IReadOnlyList<(string Title, bool Sys, string Text)>>? OpenScriptsRequested;

    public NativeAdvanceNoteControl(AdvanceNoteService store, GenAllService genAll, Func<Workspace?> workspace, AppSettings? settings = null)
    {
        _store = store; _genAll = genAll; _workspace = workspace; _settings = settings;
        Dock = DockStyle.Fill;

        // ---- thanh công cụ ----
        _btnOwner = NK.Button("👤 Note của: tôi ▾", () => _ = ShowOwnersAsync(),
            tip: "Chọn xem note của người khác cùng dự án (file <dự án>@<máy>.json trong thư mục AdvanceNotes) — CHỈ ĐỌC, vẫn Xem trước gói / Generate Update được (vd người kia nghỉ, bạn gen thay). Mặc định là note của bạn.");
        _toolbar.Controls.Add(_btnOwner);
        var btnNew = NK.Button("＋ New Request", () => { Flush(); NewRequest(); }, tip: "Tạo y/c mới");
        _toolbar.Controls.Add(btnNew);
        _btnDelete = NK.Button("🗑 Xoá", DeleteClick, tip: "Xoá y/c đang chọn khỏi Request List (không đụng database)");
        _toolbar.Controls.Add(_btnDelete);
        _syncProg.Margin = new Padding(8, 0, 6, 0);
        _toolbar.Controls.Add(_syncProg);
        var btnSync = NK.Button("⟳ Sync yêu cầu", () => { Flush(); _ = SyncAsync(); }, tip: "Kéo các yêu cầu của lập trình này trong project đang vào từ quản lý yêu cầu, tạo y/c mới cho mã chưa có");
        _toolbar.Controls.Add(btnSync);
        _roOff.AddRange(new Control[] { btnNew, _btnDelete, _syncProg, btnSync });
        _roBar.BackColor = AppColors.PanelAlt; _roText.ForeColor = AppColors.Warning;
        _roBar.Controls.Add(_roText);
        _roBar.Controls.Add(NK.Button("📥 Chép về note của tôi", CopyToMine, tip: "Chép các y/c đã tick (không tick = y/c đang chọn) về note của bạn để sửa tiếp — file note của người kia không bị đụng tới"));
        _roBar.Controls.Add(NK.Button("↩ Về note của tôi", () => _ = ViewOwnerAsync(""), tip: "Quay về note của bạn"));
        _toolbar.Controls.Add(NK.Button("📄 Request Content", OpenContent, tip: "Xem / sửa nội dung y/c & những gì đã sửa — Ctrl+Shift+4"));
        _toolbar.Controls.Add(_noteButton = NK.Button("📝 Note", OpenNote, tip: "Lưu ý cho người triển khai — Generate Update ghi vào note.txt trong thư mục gói update"));
        _toolbar.Controls.Add(NK.Button("⚙ Theo luật Fast", () => RequestCatalogAssist("rules"),
            tip: "Mở Tạo nhanh danh mục điền ngay theo luật cấu trúc Fast cho y/c đang chọn — không gọi AI, không cần API key — Ctrl+Alt+Shift+F"));
        _toolbar.Controls.Add(NK.Button("🔎 AI phân tích", () => RequestCatalogAssist("analyze"),
            tip: "AI đọc y/c đang chọn và viết lại theo mẫu luật Fast (danh mục, các trường, lookup, bắt buộc nhập…) — bạn sửa / xác nhận ở Tạo nhanh danh mục rồi luật Fast mới tạo. Cần API key — Ctrl+Alt+Shift+I"));
        _toolbar.Controls.Add(NK.Button("🤖 AI tạo danh mục", RequestAiCatalog,
            tip: "Bản thử: AI đọc nội dung y/c đang chọn, nếu là y/c tạo danh mục mới thì mở Tạo nhanh danh mục điền sẵn để duyệt (không tự ghi file / chạy SQL)"));
        _toolbar.Controls.Add(NK.Button("🗂 Nhiều dự án", () => { Flush(); NoteHubRequested?.Invoke(); },
            tip: "Mở BcodeNote — Note (New) cho nhiều dự án, chọn menu để gợi ý các ô — vào sẵn dự án đang chọn (dùng chung dữ liệu với tab này)"));
        _toolbar.Controls.Add(NK.Button("👥 Cả nhóm", () => { Flush(); TeamRequested?.Invoke(); },
            tip: "Xem note của người khác cùng dự án và các gói update trong thư mục update của dự án (chỉ đọc; chép được y/c về note của bạn)"));
        _toolbar.Controls.Add(_status);
        _syncProg.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Flush(); _ = SyncAsync(); } };

        // ---- cột trái: danh sách ----
        _list.ReadOnly = false; _list.AllowUserToAddRows = false; _list.RowHeadersVisible = false; _list.SelectionMode = DataGridViewSelectionMode.FullRowSelect; _list.MultiSelect = false;
        _list.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; _list.BorderStyle = BorderStyle.None; _list.AllowUserToResizeRows = false;
        _list.Columns.Add(new DataGridViewCheckBoxColumn { Name = "ck", HeaderText = "", Width = NativeSkin.S(36), SortMode = DataGridViewColumnSortMode.NotSortable, ToolTipText = "Tick / bỏ tick tất cả y/c" });
        _list.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Name", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 90 });
        // Cột nhỏ tự vừa chữ (cả tiêu đề) theo font / DPI — bề rộng cố định trước đây cắt mất "Mobile", "Done" và giờ của ngày.
        var fit = DataGridViewAutoSizeColumnMode.AllCells;
        var center = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter };
        _list.Columns.Add(new DataGridViewTextBoxColumn { Name = "mobile", HeaderText = "Mobile", ReadOnly = true, AutoSizeMode = fit, DefaultCellStyle = center });
        _list.Columns.Add(new DataGridViewTextBoxColumn { Name = "begin", HeaderText = "Begin", ReadOnly = true, AutoSizeMode = fit });
        _list.Columns.Add(new DataGridViewTextBoxColumn { Name = "done", HeaderText = "Done", ReadOnly = true, AutoSizeMode = fit, DefaultCellStyle = center });
        _list.Columns.Add(new DataGridViewTextBoxColumn { Name = "updated", HeaderText = "Updated", ReadOnly = true, AutoSizeMode = fit });
        foreach (DataGridViewColumn c in _list.Columns) c.SortMode = DataGridViewColumnSortMode.NotSortable;   // bỏ chỗ trống dành cho mũi tên sắp xếp ở tiêu đề
        _list.ScrollBars = ScrollBars.Vertical;
        _list.SizeChanged += (_, _) => FitListColumns();
        _list.CurrentCellDirtyStateChanged += (_, _) => { if (_list.IsCurrentCellDirty) _list.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        // Bấm ô Done ở Request List: đánh dấu / bỏ Done ngay (như trang web), không cần mở y/c.
        _list.CellClick += (_, e) =>
        {
            if (_loading || e.RowIndex < 0 || e.ColumnIndex < 0 || _list.Columns[e.ColumnIndex].Name != "done" || _list.Rows[e.RowIndex].Tag is not string did) return;
            ToggleDone(did);
        };
        _list.CellValueChanged += (_, e) =>
        {
            if (_loading || e.ColumnIndex != 0 || e.RowIndex < 0 || _list.Rows[e.RowIndex].Tag is not string id) return;
            if (_list.Rows[e.RowIndex].Cells[0].Value is true) _checked.Add(id); else _checked.Remove(id);
            UpdateDeleteLabel(); RefreshFolder(); _list.InvalidateCell(0, -1);
        };
        // Ô tiêu đề cột ✓ = checkbox "tick tất cả" (như trang web): tất cả / một phần (gạch) / không.
        _list.CellPainting += (_, e) =>
        {
            if (e.RowIndex != -1 || e.ColumnIndex != 0 || e.Graphics is null) return;
            e.PaintBackground(e.CellBounds, false);
            var vis = VisibleIds(); var n = vis.Count(_checked.Contains);
            var state = n == 0 ? System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal
                : n == vis.Count ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal : System.Windows.Forms.VisualStyles.CheckBoxState.MixedNormal;
            var sz = CheckBoxRenderer.GetGlyphSize(e.Graphics, state);
            CheckBoxRenderer.DrawCheckBox(e.Graphics, new Point(e.CellBounds.X + (e.CellBounds.Width - sz.Width) / 2, e.CellBounds.Y + (e.CellBounds.Height - sz.Height) / 2), state);
            e.Handled = true;
        };
        _list.ColumnHeaderMouseClick += (_, e) =>
        {
            var vis = VisibleIds();   // chỉ các y/c đang hiện theo ô lọc mã y/c
            if (e.ColumnIndex != 0 || vis.Count == 0) return;
            var all = vis.Count(_checked.Contains) < vis.Count;
            foreach (var id in vis) _checked.Remove(id);
            if (all) foreach (var id in vis) _checked.Add(id);
            _loading = true;
            foreach (DataGridViewRow row in _list.Rows) row.Cells[0].Value = all;
            _loading = false;
            UpdateDeleteLabel(); RefreshFolder(); _list.InvalidateCell(0, -1);
        };
        _list.SelectionChanged += (_, _) => { if (!_loading && _list.CurrentRow?.Tag is string id && id != _cur?.Id) { Flush(); Select(id); } };
        var leftTop = new Panel { Dock = DockStyle.Fill };
        // Ô lọc mã y/c (như advnote.html): nhiều mã cách nhau dấu cách / phẩy, Esc xoá lọc, Enter mở y/c đầu tiên khớp.
        var filterRow = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this) + NativeSkin.S(10), Padding = new Padding(NativeSkin.S(8), NativeSkin.S(5), NativeSkin.S(8), NativeSkin.S(5)) };
        _listFilter.Dock = DockStyle.Fill;
        filterRow.Controls.Add(_listFilter);
        _listFilterTimer.Tick += (_, _) => { _listFilterTimer.Stop(); RenderList(); };
        Disposed += (_, _) => _listFilterTimer.Dispose();
        _listFilter.TextChanged += (_, _) => { _listFilterTimer.Stop(); _listFilterTimer.Start(); };
        _listFilter.InputKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = e.SuppressKeyPress = true; _listFilter.Text = ""; RenderList(); }
            else if (e.KeyCode == Keys.Enter && _list.Rows.Count > 0 && _list.Rows[0].Tag is string first) { e.Handled = e.SuppressKeyPress = true; Flush(); Select(first); }
        };
        leftTop.Controls.Add(_list); leftTop.Controls.Add(filterRow); leftTop.Controls.Add(_listTitle);
        _pkgPanel.Controls.Add(_pkgTree); _pkgPanel.Controls.Add(_pkgHint);
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(leftTop); left.Controls.Add(_pkgPanel);
        NK.ThemeTree(_pkgTree);
        _pkgTree.NodeMouseDoubleClick += (_, e) => { if (e.Node?.Tag is int i && i >= 0 && i < _preview.Count && _preview[i].IsScript) OpenScripts(i); };
        _pkgTree.AfterSelect += (_, e) => { if (e.Node?.Tag is int i && i >= 0 && i < _preview.Count) ShowItemText(i); };

        // ---- cột phải: các thẻ ----
        var cName = new CardPanel("Request", "ghi chú y/c đã chỉnh sửa");
        cName.RightBar.Controls.Add(NK.Button("⤢ Request Content", OpenContent, tip: "Mở rộng khung nội dung y/c — Ctrl+Shift+4"));
        // 1 hàng chia cột: [Tên y/c][ô tên ⟷ giãn][Mobile][Done] — trước đây Mobile/Done neo phải trong FlowLayout nên thẻ hẹp bị cắt mất "Done".
        NK.Stack(cName.Body, NK.GridRow(12, (NK.Muted("Tên y/c"), 0), (_fName, 100), (_fMobile, 0), (_fDone, 0)));

        var cGen = new CardPanel("Gen All", "nhập tên controller (mỗi dòng 1 tên)");
        // Chú thích xếp TRÊN ô nhập (trước đây nhãn không Dock nằm đè lên dòng đầu của ô → gõ tên controller không thấy chữ).
        NK.Stack(cGen.Body, NK.Muted("Gen toàn bộ file liên quan (Main\\*.aspx, file cùng tên trong Controllers, controller đi cặp, file include/entity được tham chiếu), procedure gọi trong controller Filter, và script sysmenu (wcommand, command).", true), NK.Spacer(6), _fGenAll);
        var cFile = new CardPanel("File Path", "đường dẫn file muốn gen update");
        _fFilePaths.Dock = DockStyle.Top;
        var browseRow = NK.Row(NK.Button("Chọn file…", BrowseFiles),
            NK.Button("⚙ Gen update tất cả", () => { Flush(); _ = GeneratePathsAsync(true); }, tip: "Tạo gói update CHỈ gồm mọi đường dẫn trong ô File Path (không Gen All / SQL Object / table / script) — dùng Save At Path, Folder Name và tuỳ chọn mạng ở thẻ Generate Update. Không đánh Done y/c."),
            NK.Button("⚙ Gen update dòng đang chọn", () => { Flush(); _ = GeneratePathsAsync(false); }, tip: "Tạo gói update CHỈ gồm đường dẫn ở dòng đang đặt con trỏ (bôi đen nhiều dòng = các dòng đó) — dùng Save At Path, Folder Name và tuỳ chọn mạng ở thẻ Generate Update."),
            NK.Muted("mỗi dòng 1 đường dẫn (thư mục = lấy mọi file bên trong)")); browseRow.Dock = DockStyle.Top;
        cFile.Body.Controls.Add(browseRow); cFile.Body.Controls.Add(_fFilePaths);
        var two1 = Two(cGen, cFile);

        _swApp = NK.Button("App", () => SwitchDb(false)); _swSys = NK.Button("Sys", () => SwitchDb(true));
        var dbBar = NK.Row(NK.Muted("Database của SQL Object / Top Script / Bottom Script:"), _swApp, _swSys, _hDb);
        dbBar.Margin = new Padding(0, 0, 0, 6);

        _cObj = new CardPanel("SQL Object", "table / view / stored / function / trigger — tự xếp đúng thứ tự update");
        _fProcedures.Dock = DockStyle.Top;
        _cObj.Body.Controls.Add(_fProcedures);
        _cTop = new CardPanel("SQL Top Script", "đầu file script — chạy trước");
        _btnBottom = NK.Button("▾ Bottom Script", () => ShowBottom(!_cBottom!.Visible), tip: "Hiện / ẩn khung SQL Bottom Script (chạy sau)");
        _cTop.RightBar.Controls.Add(_btnBottom);
        _fTop.Dock = DockStyle.Top;
        _cTop.Body.Controls.Add(_fTop);
        var two2 = Two(_cObj, _cTop);
        _cBottom = new CardPanel("SQL Bottom Script", "cuối file script — chạy sau các script khác") { Visible = false };
        _fBottom.Dock = DockStyle.Top; _cBottom.Body.Controls.Add(_fBottom);

        var cTbl = new CardPanel("Table liên quan", "table khai báo trong file của các controller ở Gen All — chọn để đưa script tạo table / dữ liệu vào gói");
        cTbl.RightBar.Controls.Add(NK.Button("⟳ Tải table", () => { Flush(); _ = LoadTablesAsync(); }, tip: "Đọc lại các file liên quan để lấy danh sách table"));
        SetupTableGrid();
        _tblAddName.Width = NativeSkin.S(360);
        var addRow = NK.Row(_tblAddName, NK.Button("＋ Thêm", () => _ = AddTableByNameAsync())); addRow.Dock = DockStyle.Top;
        _tblInfo.Dock = DockStyle.Top;
        _tblGrid.Dock = DockStyle.Top;
        cTbl.Body.Controls.Add(_tblGrid); cTbl.Body.Controls.Add(_tblInfo); cTbl.Body.Controls.Add(addRow);
        // Gợi ý tên table / view như trang web: khớp đầu trước, chứa sau, tối đa 14 dòng; ↑/↓ chọn, Enter thêm, Esc đóng.
        _tblAddName.InputKeyDown += (_, e) =>
        {
            var open = _sugg is { IsDisposed: false, IsOpen: true };
            switch (e.KeyCode)
            {
                case Keys.Enter:
                    e.Handled = e.SuppressKeyPress = true;
                    _ = AddTableByNameAsync(open ? _sugg!.SelectedText : null);
                    break;
                case Keys.Down when open: e.Handled = e.SuppressKeyPress = true; _sugg!.MoveSelection(1); break;
                case Keys.Up when open: e.Handled = e.SuppressKeyPress = true; _sugg!.MoveSelection(-1); break;
                case Keys.Escape when open: e.Handled = e.SuppressKeyPress = true; HideSugg(); break;
                // Ô 1 dòng chỉ giữ dòng đầu khi dán → dán nhiều dòng (danh sách bảng:điều kiện như FCode) thì thêm luôn từng dòng.
                case Keys.V when e.Control && !e.Alt:
                case Keys.Insert when e.Shift && !e.Control:
                    var clip = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                    if (clip.Trim().IndexOfAny(new[] { '\r', '\n' }) >= 0 && !_tblAddName.ReadOnly) { e.Handled = e.SuppressKeyPress = true; _ = AddTableByNameAsync(clip); }
                    break;
            }
        };
        _tblAddName.Inner.TextChanged += (_, _) => ShowSugg();
        _tblAddName.Inner.Leave += (_, _) => HideSugg();
        Disposed += (_, _) => _sugg?.Dispose();

        var cPkg = new CardPanel("Gói update");
        _btnOpenAll = NK.Button("▶ Mở tất cả script vào tab Query", () => OpenScripts(-1), tip: "Mở mọi script của gói vào các tab SQL Query theo thứ tự chạy — chạy thử ngay");
        _btnOpenAll.Visible = false;
        cPkg.RightBar.Controls.Add(_btnOpenAll);
        cPkg.RightBar.Controls.Add(NK.Button("🔍 Xem trước gói", () => { Flush(); _ = PreviewAsync(); }));
        _items.Height = NativeSkin.S(200); _items.Dock = DockStyle.Top; _items.Visible = false;
        _items.CellClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _preview.Count) return;
            if (_items.Columns[e.ColumnIndex].Name == "open") { if (_preview[e.RowIndex].IsScript) OpenScripts(e.RowIndex); return; }
            if (_viewHost.Visible && _viewIdx == e.RowIndex) { _viewHost.Visible = false; _viewIdx = -1; return; }
            ShowItemText(e.RowIndex);
        };
        _items.CellFormatting += (_, e) => { if (e.ColumnIndex == _items.Columns["open"]!.Index && e.Value is "Mở") e.CellStyle!.ForeColor = AppColors.Accent; };
        _previewSummary.Dock = DockStyle.Top; _warnings.Dock = DockStyle.Top;
        SqlScintilla.Configure(_viewer); _viewer.ReadOnly = true; _viewer.Margins[0].Width = 0;
        _viewHost.Controls.Add(_viewer); _viewHost.Dock = DockStyle.Top;
        cPkg.Body.Controls.Add(_warnings); cPkg.Body.Controls.Add(_viewHost); cPkg.Body.Controls.Add(_items); cPkg.Body.Controls.Add(_previewSummary);

        var cGenUp = new CardPanel("Generate Update", "gói của các y/c đã tick (không tick = y/c đang chọn)");
        var saveRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2 };
        saveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); saveRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _gSave.Dock = DockStyle.Fill; _gFolder.Dock = DockStyle.Fill; _gSave.Margin = new Padding(0, 0, 8, 6); _gFolder.Margin = new Padding(0, 0, 0, 6);
        saveRow.Controls.Add(NK.Muted("Save At Path"), 0, 0); saveRow.Controls.Add(NK.Muted("Folder Name"), 1, 0);
        saveRow.Controls.Add(_gSave, 0, 1); saveRow.Controls.Add(_gFolder, 1, 1);
        var genRow = NK.Row(_gSplit, _gDone, NK.Button("⚙ Generate Update", () => { Flush(); _ = GenerateAsync(); }, primary: true));
        genRow.Dock = DockStyle.Top;
        NK.Tip(_gSplit, "Tick nhiều y/c: bật = mỗi y/c 1 thư mục con riêng (1585\\, 1586\\...); tắt = gộp tất cả vào 1 gói chung (1 file APP_Script.sql).");
        // Nén zip / Gen vào đường dẫn mạng: lưu vào settings.json ngay khi đổi (dùng chung với Gen Update và BcodeNote.exe).
        var pkgRow = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 2, BackColor = Color.Transparent };
        pkgRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); pkgRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); pkgRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _gNetRoot.Dock = DockStyle.Fill; _gNetRoot.Margin = new Padding(0, 0, 0, 4);
        pkgRow.Controls.Add(_gNet, 0, 0); pkgRow.Controls.Add(_gZip, 1, 0); pkgRow.Controls.Add(_gNetRoot, 2, 0);
        pkgRow.Controls.Add(_gNetHint, 2, 1);
        NK.Tip(_gZip, "Chỉ đưa lên mạng file <Folder Name>.zip (thay cho thư mục gói). Save At Path vẫn chỉ có thư mục gói, không nén.");
        NK.Tip(_gNet, "Tạo gói xong thì chép thêm vào <đường dẫn mạng>\\<tên máy>\\ (thư mục tên máy chưa có thì tạo). Gói ở Save At Path vẫn giữ.");
        _result.Dock = DockStyle.Top; _history.Dock = DockStyle.Top;
        cGenUp.Body.Controls.Add(_history); cGenUp.Body.Controls.Add(_result); cGenUp.Body.Controls.Add(pkgRow); cGenUp.Body.Controls.Add(genRow); cGenUp.Body.Controls.Add(saveRow);
        _gFolder.Inner.TextChanged += (_, _) => { if (!_loading) _folderTouched = true; };

        var cards = new Control[] { cName, two1, dbBar, two2, _cBottom, cTbl, cPkg, cGenUp };
        foreach (var c in cards) _stack.Controls.Add(c);
        NK.FitWidth(_stack, cards);

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_stack); right.Controls.Add(_noSel);
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        Controls.Add(split); Controls.Add(_roBar); Controls.Add(_toolbar);
        HandleCreated += (_, _) => BeginInvoke(new Action(() => { try { split.SplitterDistance = Math.Min(Math.Max(NativeSkin.S(430), (int)(Width * 0.3)), Width / 2); /* như #left 430px của trang web (nhân tỉ lệ) */ } catch { } Init(); }));

        foreach (var m in new[] { _fName, _fGenAll, _fFilePaths, _fProcedures, _fTop, _fBottom }) m.TextChanged += (_, _) => Dirty();
        _fMobile.CheckedChanged += (_, _) => Dirty(); _fDone.CheckedChanged += (_, _) => Dirty();
        // Gõ xong tên controller (rời ô Gen All) thì tự tải danh sách table — chỉ khi nội dung đổi (như sự kiện change của trang web), không tải lại mỗi lần rời ô.
        var genAllAtEnter = "";
        _fGenAll.Inner.Enter += (_, _) => genAllAtEnter = _fGenAll.Text;
        _fGenAll.Inner.Leave += (_, _) => { if (_cur is not null && _fGenAll.Text != genAllAtEnter && _fGenAll.Text.Trim().Length > 0) { Flush(); _ = LoadTablesAsync(); } };
        _fFilePaths.Inner.KeyDown += (_, e) =>
        {
            // Ctrl+V file ở Explorer → chèn ĐƯỜNG DẪN của các file đó (như FCode): trên clipboard là danh sách file chứ không có chữ.
            if (e.Control && e.KeyCode == Keys.V && Clipboard.ContainsFileDropList() && !Clipboard.ContainsText())
            {
                e.Handled = e.SuppressKeyPress = true;
                AddPaths(Clipboard.GetFileDropList().Cast<string>());
            }
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Flush(); };
        // "Tách theo từng y/c" nhớ lần chọn trước (trang web nhớ trong localStorage)
        _gSplit.Checked = _settings?.NoteSplit ?? true;
        _gSplit.CheckedChanged += (_, _) =>
        {
            if (_settings is null || _settings.NoteSplit == _gSplit.Checked) return;
            _settings.NoteSplit = _gSplit.Checked;
            try { _settings.Save(); } catch { /* không lưu được thì lần sau về mặc định */ }
        };
        // "Done sau khi gen": nhớ chung settings.json với trang web / BcodeNote.
        _gDone.Checked = _settings?.NoteDoneAfterGen ?? true;
        NK.Tip(_gDone, "Generate Update xong thì tự đánh dấu Done các y/c vừa gen (bỏ tick nếu muốn tự đánh dấu). Không áp khi đang xem note của người khác.");
        _gDone.CheckedChanged += (_, _) =>
        {
            if (_settings is null || _settings.NoteDoneAfterGen == _gDone.Checked) return;
            _settings.NoteDoneAfterGen = _gDone.Checked;
            try { _settings.Save(); } catch { /* không lưu được thì lần sau về mặc định */ }
        };
        var pkg = GenAllService.LoadPackageOptions();
        _gZip.Checked = pkg.Zip; _gNet.Checked = pkg.Network; _gNetRoot.Text = pkg.NetworkRoot; ShowNetRow();
        _gZip.CheckedChanged += (_, _) => { ShowNetRow(); SavePkgOpts(); };
        _gNet.CheckedChanged += (_, _) => { ShowNetRow(); SavePkgOpts(); };
        _gNetRoot.Inner.TextChanged += (_, _) => ShowNetRow();
        _gNetRoot.Inner.Leave += (_, _) => SavePkgOpts();
        Disposed += (_, _) => { try { Flush(); } catch { } _saveTimer.Dispose(); };
    }

    private TableLayoutPanel Two(Control a, Control b)
    {
        var t = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        a.Dock = DockStyle.Fill; b.Dock = DockStyle.Fill; a.Margin = new Padding(0, 0, 8, 10); b.Margin = new Padding(0, 0, 0, 10);
        t.Controls.Add(a, 0, 0); t.Controls.Add(b, 1, 0);
        return t;
    }

    // =====================================================================  dữ liệu

    private string WorkspaceName => _workspace()?.Name ?? "";

    private void EnsureLoaded()
    {
        if (_loadedFor == WorkspaceName) return;   // workspace đổi giữa chừng (Choose Server) → nạp lại danh sách của workspace mới
        _requests = _store.Load(_workspace(), _settings);
        _loadedFor = WorkspaceName;
        if (_viewOwner is not null) { _viewOwner = null; _viewModified = null; ApplyView(); }   // nạp lại từ file = về note của mình
    }

    // =====================================================================  xem note của người khác (chỉ đọc)

    private bool ReadOnlyGuard()
    {
        if (_viewOwner is null) return false;
        SetStatus($"Đang xem note của {_viewOwner} (chỉ đọc) — không sửa / xoá được. Chọn \"Tôi\" ở nút 👤 để về note của bạn, hoặc 📥 Chép về note của tôi.", true);
        return true;
    }

    /// <summary>Khoá / mở các ô sửa và hiện dải "đang xem note của …" theo <see cref="_viewOwner"/>.</summary>
    private void ApplyView()
    {
        var ro = _viewOwner is not null;
        foreach (var t in new[] { _fName, _fGenAll, _fFilePaths, _fProcedures, _fTop, _fBottom, _tblAddName }) t.ReadOnly = ro;
        _fMobile.Enabled = _fDone.Enabled = !ro;
        foreach (var c in _roOff) c.Enabled = !ro;
        _btnOwner.Text = "👤 Note của: " + (_viewOwner ?? "tôi") + " ▾"; _btnOwner.Invalidate();
        _roText.Text = ro ? $"👁 Đang xem note của {_viewOwner}" + (_viewModified is { } m ? $" (sửa lần cuối {Fmt(m)})" : "") +
            " — CHỈ ĐỌC: không sửa / xoá được. Xem trước gói và ⚙ Generate Update vẫn dùng được (không ghi lịch sử gen vào note của họ)." : "";
        _roBar.Visible = ro;
    }

    /// <summary>Nút 👤: đọc danh sách người có note của dự án (luồng nền) rồi hiện menu chọn.</summary>
    private async Task ShowOwnersAsync()
    {
        Flush();
        var ws = _workspace();
        SetStatus("⏳ Đang đọc danh sách note của dự án...");
        var (owners, error) = await Task.Run(() => _store.ListOwners(ws, _settings));
        if (IsDisposed) return;
        SetStatus(error ?? $"{owners.Count} người khác có note trong dự án.", error is not null);
        var menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        var me = new ToolStripMenuItem($"👤 Tôi ({AdvanceNoteService.Owner})") { Checked = _viewOwner is null };
        me.Click += (_, _) => { if (_viewOwner is not null) _ = ViewOwnerAsync(""); };
        menu.Items.Add(me);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var o in owners)
        {
            var owner = o.Owner;
            var it = new ToolStripMenuItem($"{owner}{(o.Programmers.Length > 0 ? "  · 👤 " + o.Programmers : "")}   —   {o.Count} y/c, {o.Pending} chưa xong · sửa {Fmt(o.Modified)}") { Checked = _viewOwner == owner };
            it.Click += (_, _) => _ = ViewOwnerAsync(owner);   // chọn lại đúng người đang xem = đọc lại file của họ
            menu.Items.Add(it);
        }
        if (error is not null) menu.Items.Add(new ToolStripMenuItem("⚠ " + error) { Enabled = false });
        else if (owners.Count == 0) menu.Items.Add(new ToolStripMenuItem("Chưa có note của người khác trong dự án này.") { Enabled = false });
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(_btnOwner, new Point(0, _btnOwner.Height));
    }

    private bool _inited;
    private (string Owner, string? Id)? _pendingView;
    private (string Id, string Message)? _pendingMine;

    /// <summary>Ghi ngay phần đang gõ (trước khi tiến trình khác ghi file note của mình — vd tab Cả nhóm dán y/c vào).</summary>
    public void FlushPending() { if (_inited) Flush(); }

    /// <summary>Về note của MÌNH, nạp lại file, chọn y/c <paramref name="id"/> (vừa dán từ note người khác — "⚙ Tạo gói update…" ở tab Cả nhóm)
    /// rồi tự Xem trước gói. Control chưa Init thì làm sau Init.</summary>
    public void OpenMineRequest(string id, string message)
    {
        if (!_inited) { _pendingMine = (id, message); return; }
        ++_viewVersion;
        _saveTimer.Stop();
        _loadedFor = ""; _cur = null; _checked.Clear(); _tblList = new();
        EnsureLoaded();   // nạp lại file; đang xem note người khác thì tự về note của mình
        ClearPreview();
        RenderList();
        Select(_requests.Any(r => r.Id == id) ? id : _requests.OrderByDescending(r => r.Begin).FirstOrDefault()?.Id);
        _ = PreviewThenSayAsync(message);
    }

    private async Task PreviewThenSayAsync(string message)
    {
        await PreviewAsync();
        if (!IsDisposed) SetStatus(message + (_preview.Count > 0 ? $" (gói có {_preview.Count} mục)" : ""), ok: true);
    }

    /// <summary>Mở note của <paramref name="owner"/> (chỉ đọc) và chọn sẵn y/c <paramref name="id"/> — từ "📝 Mở ở Note (New)" của tab Cả nhóm.
    /// Control chưa Init (vừa tạo tab) thì làm sau Init.</summary>
    public void ShowOwnerRequest(string owner, string? id)
    {
        if (!_inited) { _pendingView = (owner, id); return; }
        _ = ViewOwnerAsync(owner, id);
    }

    /// <summary>Chuyển sang xem note của <paramref name="owner"/> ("" = của mình). Đọc ở luồng nền; lỗi thì giữ nguyên và báo.</summary>
    private async Task ViewOwnerAsync(string owner, string? selectId = null)
    {
        var v = ++_viewVersion;
        _saveTimer.Stop();
        if (owner.Length == 0 || string.Equals(owner, AdvanceNoteService.Owner, StringComparison.OrdinalIgnoreCase))
        {
            _loadedFor = ""; _checked.Clear(); _cur = null;
            Init();   // EnsureLoaded nạp lại note của mình + bỏ chế độ chỉ đọc
            SetStatus("Đang xem note của bạn.", ok: true);
            return;
        }
        var ws = _workspace();
        var path = AdvanceNoteService.OtherFile(ws, _settings, owner);
        if (path is null) { SetStatus("Không xác định được file note của người này (dự án chưa có Working Path?).", true); return; }
        if (BusyGuard()) return;
        SetBusy(true, $"Đang đọc note của {owner}...");
        try
        {
            var (list, modified) = await Task.Run(() => AdvanceNoteService.ReadOther(path));
            if (v != _viewVersion || IsDisposed) return;
            Flush();   // phần đang gõ của note mình (nếu đang xem note mình) — trước khi thay danh sách
            _viewOwner = owner; _viewModified = modified;
            _requests = list; _loadedFor = WorkspaceName;
            _checked.Clear(); _cur = null; _tblList = new();
            ApplyView();
            ClearPreview();
            RenderList();
            Select(selectId is not null && _requests.Any(r => r.Id == selectId) ? selectId : _requests.OrderByDescending(r => r.Begin).FirstOrDefault()?.Id);
            SetStatus($"Đang xem note của {owner} ({list.Count} y/c, sửa lần cuối {Fmt(modified)}) — chỉ đọc.", ok: true);
        }
        catch (Exception ex)
        {
            if (v == _viewVersion && !IsDisposed) SetStatus($"Không đọc được note của {owner}: {ex.Message}", true);
        }
        finally { SetBusy(false); }
    }

    /// <summary>📥 Chép các y/c đã tick (không tick = y/c đang chọn) của người đang xem về note của mình — file của người kia không bị đụng tới.</summary>
    private void CopyToMine()
    {
        if (_viewOwner is null) return;
        var picked = PickedForPreview();
        if (picked.Count == 0) { SetStatus("Tick y/c cần chép (hoặc chọn 1 y/c) trước.", true); return; }
        var sample = string.Join(", ", picked.Take(5).Select(r => r.Name)) + (picked.Count > 5 ? ", ..." : "");
        if (!NK.Confirm(this, $"Chép {picked.Count} y/c của {_viewOwner} về note của bạn?\n\n{sample}\n\nMỗi y/c thành 1 y/c mới tên \"<tên> (từ {_viewOwner})\" trong note của bạn (chưa Done, không kèm lịch sử gen). Note của {_viewOwner} KHÔNG bị sửa.")) return;
        try
        {
            var ws = _workspace();
            var mine = _store.Load(ws, _settings);
            var names = new List<string>();
            foreach (var src in picked)
            {
                var copy = AdvanceNoteService.CloneForMine(src, _viewOwner, mine);
                mine.Insert(0, copy);
                names.Add(copy.Name);
            }
            _store.Save(ws, _settings, mine);
            SetStatus($"Đã chép {picked.Count} y/c về note của bạn: " + string.Join(", ", names.Take(5)) + (names.Count > 5 ? ", ..." : "") + (_store.LastSaveWarning is { } w ? " " + w : ""), ok: true);
        }
        catch (Exception ex) { SetStatus("Không chép được: " + ex.Message, true); }
    }

    private void Init()
    {
        EnsureLoaded();
        var ws = _workspace();
        _defaultFolder = string.Join("_", new[] { ws?.ProjectId, Environment.MachineName, DateTime.Now.ToString("yyyyMMddHHmmss") }.Where(s => !string.IsNullOrWhiteSpace(s)));
        _loading = true;
        _gSave.Text = ws?.WorkingPath ?? "";
        _syncProg.Text = _settings?.NoteProgrammer ?? "";
        _folderTouched = false;
        _loading = false;
        SetStatus(ws is null ? "Chưa chọn workspace" : "Workspace: " + ws.Name, ws is null);
        RenderList();
        var first = _requests.OrderByDescending(r => r.Begin).FirstOrDefault();
        Select(first?.Id);
        _ = LoadTableNamesAsync();
        _inited = true;
        if (_pendingView is { } pv) { _pendingView = null; _ = ViewOwnerAsync(pv.Owner, pv.Id); }
        if (_pendingMine is { } pm) { _pendingMine = null; OpenMineRequest(pm.Id, pm.Message); }
    }

    private void SetStatus(string text, bool error = false, bool ok = false)
    {
        _status.Text = text;
        _status.ForeColor = error ? AppColors.Danger : ok ? AppColors.Success : AppColors.TextMuted;
    }

    private void SetBusy(bool busy, string? msg = null)
    {
        _busy = busy;
        UseWaitCursor = busy;
        if (busy && msg is not null) SetStatus("⏳ " + msg);
    }

    /// <summary>Đang chạy 1 việc nền (tải table / xem trước / generate / sync) — như lớp phủ "Đang xử lý..." của trang web: không cho bấm chồng việc thứ hai.</summary>
    private bool BusyGuard()
    {
        if (!_busy) return false;
        System.Media.SystemSounds.Beep.Play();
        return true;
    }

    private int TickedCount() => _requests.Count(r => _checked.Contains(r.Id));

    private async Task LoadTableNamesAsync()
    {
        try { _tableNames ??= await _genAll.ListTableNamesAsync(); } catch { /* không kết nối được DB — không có gợi ý */ }
    }

    private SuggestPopup? _sugg;

    private void ShowSugg()
    {
        var q = _tblAddName.Text.Trim();
        if (q.Length == 0 || q.Contains(':') || _tableNames is not { Count: > 0 } || !_tblAddName.Inner.Focused || FindForm() is not { } owner) { HideSugg(); return; }
        var starts = new List<string>(); var has = new List<string>();
        foreach (var n in _tableNames)
        {
            var p = n.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (p == 0) starts.Add(n); else if (p > 0) has.Add(n);
            if (starts.Count + has.Count >= 400) break;
        }
        var items = starts.Concat(has).Take(14).ToList();
        if (items.Count == 0 || (items.Count == 1 && items[0].Equals(q, StringComparison.OrdinalIgnoreCase))) { HideSugg(); return; }
        if (_sugg is not { IsDisposed: false })
        {
            _sugg = new SuggestPopup();
            _sugg.Picked += name => _ = AddTableByNameAsync(name);
            // popup là cửa sổ nổi riêng — cửa sổ chính di chuyển / đổi cỡ / mất focus thì đóng để không lơ lửng lệch chỗ
            EventHandler hide = (_, _) => HideSugg();
            owner.Move += hide; owner.Resize += hide; owner.Deactivate += hide;
            Disposed += (_, _) => { owner.Move -= hide; owner.Resize -= hide; owner.Deactivate -= hide; };
            _stack.Scroll += (_, _) => HideSugg(); _stack.MouseWheel += (_, _) => HideSugg();
        }
        _sugg.ShowItems(owner, _tblAddName.PointToScreen(new Point(0, _tblAddName.Height + 2)), _tblAddName.Width, items);
    }

    private void HideSugg() { if (_sugg is { IsDisposed: false }) _sugg.HidePopup(); }

    // =====================================================================  danh sách y/c

    private void RenderList()
    {
        _loading = true;
        try
        {
            var all = _requests.OrderByDescending(r => r.Begin).ToList();
            var tokens = Fold(_listFilter.Text).Split(new[] { ' ', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var rows = tokens.Length == 0 ? all : all.Where(r => tokens.Any(t => Fold(r.Name).Contains(t, StringComparison.Ordinal))).ToList();
            _listTitle.Text = $"Request List ({(tokens.Length > 0 ? rows.Count + "/" : "")}{all.Count})  — tick ✓ để Generate nhiều y/c cùng lúc";
            _list.Rows.Clear();
            foreach (var r in rows)
            {
                var i = _list.Rows.Add(_checked.Contains(r.Id), r.Name, r.Mobile ? "☑" : "☐", Fmt(r.Begin), r.Done ? "☑" : "☐", r.Updated is { } u ? Fmt(u) : "");
                _list.Rows[i].Tag = r.Id;
                _list.Rows[i].Cells["name"].ToolTipText = r.Name + "\nBegin: " + Fmt(r.Begin) + (r.Updated is { } up ? "   ·   Updated: " + Fmt(up) : "") + (r.Mobile ? "\nMobile" : "") + (r.Done ? "\nDone" : "");
                if (r.Done) _list.Rows[i].DefaultCellStyle.ForeColor = AppColors.TextMuted;
                if (r.Id == _cur?.Id) { _list.ClearSelection(); _list.Rows[i].Selected = true; try { _list.CurrentCell = _list.Rows[i].Cells[1]; } catch { } }
            }
            if (_cur is null || !rows.Contains(_cur)) _list.ClearSelection();   // y/c đang mở bị ô lọc ẩn: không tô nhầm dòng đầu
        }
        finally { _loading = false; }
        FitListColumns();
        _checked.RemoveWhere(id => !_requests.Any(r => r.Id == id));   // y/c đã xoá / đổi workspace thì bỏ tick (như onList của trang web)
        _list.InvalidateCell(0, -1);
        UpdateDeleteLabel();
        RefreshFolder();
    }

    private static string Fmt(DateTime d) => d.ToString("dd/MM/yy HH:mm");

    /// <summary>Id các y/c đang hiện trong danh sách (sau ô lọc mã y/c).</summary>
    private List<string> VisibleIds() => _list.Rows.Cast<DataGridViewRow>().Select(r => r.Tag as string).OfType<string>().ToList();

    /// <summary>Chữ thường bỏ dấu (đ → d) — so ô lọc mã y/c giống advnote.html.</summary>
    private static string Fold(string? s)
    {
        var d = (s ?? "").Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) != System.Globalization.UnicodeCategory.NonSpacingMark) sb.Append(ch == 'đ' || ch == 'Đ' ? 'd' : ch);
        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>Cột trái hẹp: ẩn dần Updated → Begin → Mobile để cột Name luôn đủ rộng đọc được (không cuộn ngang, không cắt chữ tiêu đề);
    /// rê chuột lên dòng vẫn xem đủ thông tin.</summary>
    private void FitListColumns()
    {
        if (_list.Columns.Count < 6 || _list.ClientSize.Width <= 0) return;
        var avail = _list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - _list.Columns["ck"]!.Width;
        var minName = NativeSkin.S(110);
        foreach (var name in new[] { "mobile", "begin", "done", "updated" }) _list.Columns[name]!.Visible = true;
        int Used() => _list.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible && c.Name is not ("ck" or "name")).Sum(c => c.GetPreferredWidth(DataGridViewAutoSizeColumnMode.AllCells, true));
        foreach (var name in new[] { "updated", "begin", "mobile" })
        {
            if (avail - Used() >= minName) break;
            _list.Columns[name]!.Visible = false;
        }
    }

    private void UpdateDeleteLabel()
    {
        var n = TickedCount();
        _btnDelete.Text = n > 0 ? $"🗑 Xoá {n} đã tick" : "🗑 Xoá";
        _btnDelete.Invalidate();
    }

    // Tên thư mục gói update = mặc định (dự án_máy_thời gian) + "_" + tên y/c (các y/c đã tick, không tick thì y/c đang chọn). Người dùng tự sửa ô thì không ghi đè.
    private static string SafeName(string? n)
    {
        var s = Regex.Replace((n ?? "").Trim(), @"[\\/:*?""<>|]+", " ").Trim();
        s = Regex.Replace(s, @"\s+", "_");
        return s.Length > 40 ? s[..40] : s;
    }

    private string FolderRequestName()
    {
        var ticked = _requests.Where(r => _checked.Contains(r.Id)).ToList();
        if (ticked.Count == 0) return _cur is null ? "" : SafeName(_cur.Name);
        var names = ticked.Select(r => SafeName(r.Name)).Where(s => s.Length > 0).OrderBy(s => Regex.Replace(s, @"\d+", m => m.Value.PadLeft(12, '0')), StringComparer.CurrentCultureIgnoreCase).ToList();
        if (names.Count <= 1) return names.FirstOrDefault() ?? "";
        var joined = string.Join("_", names);
        return names.Count <= 5 && joined.Length <= 40 ? joined : names[0] + "_To_" + names[^1];
    }

    private void RefreshFolder()
    {
        if (_folderTouched || _defaultFolder.Length == 0) return;
        var n = FolderRequestName();
        _loading = true;
        _gFolder.Text = _defaultFolder + (n.Length > 0 ? "_" + n : "");
        _loading = false;
    }

    private void NewRequest()
    {
        if (ReadOnlyGuard()) return;
        EnsureLoaded();
        var r = new AdvanceRequest { Name = AdvanceNoteService.SuggestName(_requests) };
        _requests.Add(r);
        _store.Save(_workspace(), _settings, _requests);
        RenderList();
        Select(r.Id);
    }

    private void DeleteClick()
    {
        if (ReadOnlyGuard()) return;
        var ticked = _requests.Where(r => _checked.Contains(r.Id)).ToList();
        if (ticked.Count > 0)
        {
            var sample = string.Join(", ", ticked.Take(5).Select(r => r.Name)) + (ticked.Count > 5 ? ", ..." : "");
            if (!NK.Confirm(this, $"Xoá {ticked.Count} y/c đã tick khỏi Request List?\n\n{sample}\n\nChỉ xoá trong Bcode (danh sách y/c trên máy này) — KHÔNG ảnh hưởng quản lý yêu cầu / database.")) return;
            var ids = ticked.Select(r => r.Id).ToHashSet();
            _requests.RemoveAll(x => ids.Contains(x.Id));
            foreach (var id in ids) _checked.Remove(id);
        }
        else
        {
            if (_cur is null) return;
            if (!NK.Confirm(this, $"Xoá y/c \"{_cur.Name}\" khỏi Request List?\n\nChỉ xoá trong Bcode, KHÔNG ảnh hưởng database.")) return;
            _requests.Remove(_cur);
            _checked.Remove(_cur.Id);
        }
        _store.Save(_workspace(), _settings, _requests);
        _cur = null;
        RenderList();
        Select(_requests.OrderByDescending(r => r.Begin).FirstOrDefault()?.Id);
    }

    // =====================================================================  chọn / lưu y/c

    private void Select(string? id)
    {
        var changed = id != _cur?.Id;
        _cur = id is null ? null : _requests.FirstOrDefault(r => r.Id == id);
        _noSel.Visible = _cur is null;
        _stack.Visible = _cur is not null;
        if (_cur is not null)
        {
            _loading = true;
            try
            {
                var r = _cur;
                r.MigrateScripts();
                if (!r.ObjectsSplit) { r.ObjectsSplit = true; if (r.UseSys) { if (string.IsNullOrWhiteSpace(r.ProceduresSys)) r.ProceduresSys = r.Procedures; if (!r.UseApp) r.Procedures = ""; } }
                _fName.Text = r.Name; _fMobile.Checked = r.Mobile; _fDone.Checked = r.Done;
                _fGenAll.Text = r.GenAll; _fFilePaths.Text = r.FilePaths;
                if (changed) _scriptSys = !HasScript(r, false) && HasScript(r, true);   // mở y/c: đứng ở database đang có script (ưu tiên App)
                ShowScripts(r);
                if (changed) { ClearPreview(); _tblList = new(); }
                RenderTables();
                RenderHistory();
            }
            finally { _loading = false; }
        }
        PaintNoteButton();
        RenderList();
    }

    private static string Procs(AdvanceRequest r, bool sys) => sys ? r.ProceduresSys : r.Procedures;
    private static string Top(AdvanceRequest r, bool sys) => sys ? r.TopScriptSys : r.TopScript;
    private static string Bottom(AdvanceRequest r, bool sys) => sys ? r.BottomScriptSys : r.BottomScript;
    private static bool HasScript(AdvanceRequest r, bool sys) => (Procs(r, sys) + Top(r, sys) + Bottom(r, sys)).Trim().Length > 0;

    private void ShowScripts(AdvanceRequest r)
    {
        var name = _scriptSys ? "Sys" : "App";
        _fProcedures.Text = Procs(r, _scriptSys); _fTop.Text = Top(r, _scriptSys); _fBottom.Text = Bottom(r, _scriptSys);
        _fProcedures.PlaceholderText = _scriptSys ? "object của database Sys, mỗi dòng 1 tên" : "fs_PostARTran";
        _fTop.PlaceholderText = "-- script chạy đầu tiên trên database " + name; _fBottom.PlaceholderText = "-- script chạy sau cùng trên database " + name;
        _cObj.TitleLabel.Text = "SQL Object  [" + name + "]"; _cTop.TitleLabel.Text = "SQL Top Script  [" + name + "]"; _cBottom.TitleLabel.Text = "SQL Bottom Script  [" + name + "]";
        _cTop.SetHint("đầu file script của " + name + " — chạy trước"); _cBottom.SetHint("cuối file script của " + name + " — chạy sau các script khác");
        _hDb.Text = "đang xem phần của database " + name + (HasScript(r, !_scriptSys) ? " — bên " + (_scriptSys ? "App" : "Sys") + " cũng đang có nội dung (●)" : "");
        PaintSwitch(r);
        ShowBottom(Bottom(r, _scriptSys).Trim().Length > 0);
    }

    private void PaintSwitch(AdvanceRequest r)
    {
        _swApp.IsPrimary = !_scriptSys; _swSys.IsPrimary = _scriptSys;
        _swApp.Text = "App" + (HasScript(r, false) ? " ●" : ""); _swSys.Text = "Sys" + (HasScript(r, true) ? " ●" : "");
        _swApp.Invalidate(); _swSys.Invalidate();
    }

    private void SwitchDb(bool sys)
    {
        if (_cur is null || _scriptSys == sys) return;
        ReadEditor();   // cất nội dung đang gõ vào đúng bản của database đang xem trước khi chuyển
        _scriptSys = sys;
        _loading = true; ShowScripts(_cur); _loading = false;
    }

    private void ShowBottom(bool on)
    {
        _cBottom.Visible = on;
        _btnBottom.Text = (on ? "▴" : "▾") + " Bottom Script";
        _btnBottom.Invalidate();
    }

    private AdvanceRequest? ReadEditor()
    {
        var r = _cur;
        if (r is null) return null;
        r.Name = _fName.Text.Trim().Length > 0 ? _fName.Text.Trim() : r.Name;
        r.Mobile = _fMobile.Checked; r.Done = _fDone.Checked;
        r.GenAll = _fGenAll.Text; r.FilePaths = _fFilePaths.Text;
        if (_scriptSys) { r.ProceduresSys = _fProcedures.Text; r.TopScriptSys = _fTop.Text; r.BottomScriptSys = _fBottom.Text; }
        else { r.Procedures = _fProcedures.Text; r.TopScript = _fTop.Text; r.BottomScript = _fBottom.Text; }
        r.ObjectsSplit = true; r.ScriptsSplit = true;
        CollectTables();
        PaintSwitch(r);
        return r;
    }

    /// <summary>Bấm ô Done ở Request List: đảo Done của y/c đó rồi lưu (y/c đang mở thì cập nhật cả ô Done trong khung Request).</summary>
    private void ToggleDone(string id)
    {
        if (ReadOnlyGuard()) return;
        Flush();   // cất phần đang gõ trước
        var r = _requests.FirstOrDefault(x => x.Id == id);
        if (r is null) return;
        r.Done = !r.Done;
        if (r == _cur) { _loading = true; _fDone.Checked = r.Done; _loading = false; }
        _store.Save(_workspace(), _settings, _requests);
        BeginInvoke(new Action(RenderList));   // đang trong sự kiện của lưới — dựng lại sau
        SetStatus(r.Done ? $"Đã đánh dấu Done: {r.Name}" : $"Đã bỏ Done: {r.Name}", ok: true);
    }

    private void Dirty() { if (_loading || _cur is null || _viewOwner is not null) return; _saveTimer.Stop(); _saveTimer.Start(); }

    private void Flush()
    {
        _saveTimer.Stop();
        if (_viewOwner is not null) return;   // note của người khác: không bao giờ ghi
        if (ReadEditor() is null) return;
        _store.Save(_workspace(), _settings, _requests);
        // cập nhật dòng đang chọn trong danh sách (tên / Mobile / Done) mà không dựng lại cả lưới
        foreach (DataGridViewRow row in _list.Rows)
            if (row.Tag is string id && id == _cur!.Id) { _loading = true; row.Cells["name"].Value = _cur.Name; row.Cells["mobile"].Value = _cur.Mobile ? "☑" : "☐"; row.Cells["done"].Value = _cur.Done ? "☑" : "☐"; _loading = false; }
        RefreshFolder();
        SetStatus("Đã lưu.", ok: true);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.S)) { if (!ReadOnlyGuard()) Flush(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.D4)) { OpenContent(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // =====================================================================  lịch sử gói đã gen

    private void RenderHistory()
    {
        _history.Controls.Clear();
        var gens = _cur?.Generations ?? new();
        if (gens.Count == 0) return;
        _history.Controls.Add(NK.Muted($"Đã gen trước đó ({gens.Count}):"));
        foreach (var g in gens)
        {
            var path = g.Path;
            var row = NK.Row(new MutedLabel { Text = Fmt(g.Time), AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) },
                new Label { Text = path, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0), Font = ThemeManager.MonoFont },
                NK.Button("Copy link", () => { NK.CopyText(path); SetStatus("Đã copy link.", ok: true); }), NK.Button("Mở thư mục", () => NK.OpenFolder(path)));
            if (_viewOwner is null) row.Controls.Add(NK.Button("🗑 Xoá", () => DeletePackage(path), tip: "Xoá thư mục gói này để gen lại"));   // note người khác: không sửa lịch sử của họ
            _history.Controls.Add(row);
        }
    }

    /// <summary>Xoá thư mục gói đã gen (ở Save At Path) để gen lại; bỏ luôn link khỏi lịch sử. Bản đã chép lên đường dẫn mạng không bị xoá.</summary>
    private void DeletePackage(string path)
    {
        if (ReadOnlyGuard()) return;
        if (!NK.Confirm(this, $"Xoá thư mục gói update này để gen lại?\n\n{path}\n\nXoá hẳn thư mục trên ổ đĩa (không vào Recycle Bin). Bản đã chép lên đường dẫn mạng (nếu có) KHÔNG bị xoá.")) return;
        try
        {
            if (!AdvanceNoteService.DeletePackage(_requests, path)) { SetStatus("Đường dẫn không có trong lịch sử gen — không xoá.", true); return; }
            _store.Save(_workspace(), _settings, _requests);
            if (_lastGenPath == path) { ClearPreview(); _lastGenPath = null; }
            RenderHistory();
            SetStatus("Đã xoá gói: " + path, ok: true);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private string? _lastGenPath;

    // =====================================================================  Table liên quan

    private void SetupTableGrid()
    {
        var g = _tblGrid;
        g.AllowUserToAddRows = false; g.AllowUserToDeleteRows = false; g.RowHeadersVisible = false; g.AllowUserToResizeRows = false; g.BorderStyle = BorderStyle.None;
        g.SelectionMode = DataGridViewSelectionMode.CellSelect; g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "table", HeaderText = "Table", ReadOnly = true, Width = NativeSkin.S(170) });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "ctl", HeaderText = "Controller", ReadOnly = true, Width = NativeSkin.S(170) });
        g.Columns.Add(new DataGridViewComboBoxColumn { Name = "db", HeaderText = "DB", Width = NativeSkin.S(70), FlatStyle = FlatStyle.Flat });
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "rows", HeaderText = "Số dòng", ReadOnly = true, Width = NativeSkin.S(90) });
        // Cột tick vừa đúng chữ tiêu đề (không xuống dòng / bị cắt "Cấu trúc" khi phóng to)
        g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "st", HeaderText = "Cấu trúc", AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader, MinimumWidth = NativeSkin.S(70) });
        g.Columns.Add(new DataGridViewCheckBoxColumn { Name = "da", HeaderText = "Dữ liệu", AutoSizeMode = DataGridViewAutoSizeColumnMode.ColumnHeader, MinimumWidth = NativeSkin.S(70) });
        g.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        g.Columns.Add(new DataGridViewTextBoxColumn { Name = "wh", HeaderText = "Where (lọc dữ liệu)", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 140 });
        g.CurrentCellDirtyStateChanged += (_, _) => { if (g.IsCurrentCellDirty) g.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        g.CellValueChanged += (_, e) =>
        {
            if (_loading || e.RowIndex < 0) return;
            var name = g.Columns[e.ColumnIndex].Name;
            if (name == "da") { var on = g.Rows[e.RowIndex].Cells["da"].Value is true; g.Rows[e.RowIndex].Cells["wh"].ReadOnly = !on; }
            CollectTables(); Dirty();
            if (name == "db") BeginInvoke(new Action(RenderTables));   // đổi DB → hiện lại số dòng của DB đó
        };
        g.DataError += (_, e) => e.ThrowException = false;
        g.CellBeginEdit += (_, e) => { if (_viewOwner is not null) e.Cancel = true; };   // đang xem note người khác: không tick / sửa table
        // Ô Where trống: chữ gợi ý mờ như placeholder của trang web; ô bị khoá (chưa tick Dữ liệu) tô nền dịu để thấy rõ là không sửa được.
        g.CellPainting += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != g.Columns["wh"]!.Index || e.Graphics is null) return;
            var cell = g.Rows[e.RowIndex].Cells[e.ColumnIndex];
            if (cell.ReadOnly) e.CellStyle!.BackColor = AppColors.PanelAlt;
            if (cell.Value is string s && s.Length > 0 || cell.ReadOnly || g.IsCurrentCellInEditMode && g.CurrentCell == cell) return;
            e.Paint(e.CellBounds, DataGridViewPaintParts.All & ~DataGridViewPaintParts.ContentForeground);
            TextRenderer.DrawText(e.Graphics, "vd ma_maubc = 'bcnsky'", g.Font, Rectangle.Inflate(e.CellBounds, -4, 0), AppColors.TextMuted, (e.State & DataGridViewElementStates.Selected) != 0 ? e.CellStyle!.SelectionBackColor : e.CellStyle!.BackColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            e.Handled = true;
        };
    }

    private void RenderTables()
    {
        var r = _cur;
        var chosen = (r?.Tables ?? new()).ToDictionary(t => t.Name.ToLowerInvariant(), t => t);
        var list = _tblList.ToList();
        foreach (var t in chosen.Values)
            if (!list.Any(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)))
                list.Add(new RelatedTable { Name = t.Name, InApp = !t.Sys, InSys = t.Sys });
        var wasLoading = _loading; _loading = true;
        _tblGrid.Rows.Clear();
        foreach (var t in list)
        {
            var has = chosen.TryGetValue(t.Name.ToLowerInvariant(), out var c);
            var sys = has ? c!.Sys : (!t.InApp && t.InSys);
            var missing = !t.InApp && !t.InSys && _tblList.Count > 0;
            var i = _tblGrid.Rows.Add();
            var row = _tblGrid.Rows[i];
            row.Tag = t.Name;
            row.Cells["table"].Value = t.Name;
            row.Cells["ctl"].Value = string.Join(", ", t.Controllers);
            var db = (DataGridViewComboBoxCell)row.Cells["db"];
            if (t.InApp || !t.InSys) db.Items.Add("App");
            if (t.InSys) db.Items.Add("Sys");
            db.Value = sys ? "Sys" : "App";
            row.Cells["rows"].Value = t.IsView ? "view" : (sys ? t.RowsSys : t.RowsApp) is var n && n > 0 ? n.ToString("N0") : "";
            row.Cells["st"].Value = has && c!.Structure;
            row.Cells["da"].Value = has && c!.Data && !t.IsView;
            row.Cells["wh"].Value = has ? c!.Where : "";
            row.Cells["wh"].ReadOnly = !(has && c!.Data && !t.IsView);
            if (missing)
            {
                row.DefaultCellStyle.ForeColor = AppColors.TextMuted; row.Cells["st"].ReadOnly = true; row.Cells["da"].ReadOnly = true;
                foreach (DataGridViewCell cell in row.Cells) cell.ToolTipText = "Không thấy table này trong database App / Sys";
            }
            if (t.IsView) row.Cells["da"].ReadOnly = true;
        }
        _loading = wasLoading;
        _tblGrid.Height = Math.Min(NativeSkin.S(300), _tblGrid.ColumnHeadersHeight + Math.Max(1, _tblGrid.Rows.Count) * _tblGrid.RowTemplate.Height + 4);
        _tblGrid.Visible = list.Count > 0;
        _tblInfo.Text = _tblList.Count > 0
            ? $"{_tblList.Count} table liên quan" + (chosen.Count > 0 ? $" — đã chọn {chosen.Count}" : "") + ". Cấu trúc = script tạo table; Dữ liệu = xoá rồi nạp lại dòng (lọc bằng Where, trống = cả bảng; quá 50.000 dòng cần Where)."
            : chosen.Count > 0 ? $"Đã chọn {chosen.Count} table (bấm “Tải table” để xem lại danh sách đầy đủ)." : "Nhập controller ở Gen All (hoặc bấm “Tải table”) để liệt kê các table liên quan.";
    }

    private void CollectTables()
    {
        if (_cur is null || _loading) return;
        _cur.Tables = _tblGrid.Rows.Cast<DataGridViewRow>().Where(r => r.Tag is string).Select(r => new TableSelection
        {
            Name = (string)r.Tag!, Sys = Equals(r.Cells["db"].Value, "Sys"), Structure = r.Cells["st"].Value is true, Data = r.Cells["da"].Value is true, Where = (r.Cells["wh"].Value as string ?? "").Trim(),
        }).Where(t => t.Structure || t.Data).ToList();
    }

    private async Task LoadTablesAsync()
    {
        var ws = _workspace();
        if (_cur is null) return;
        var names = GenAllService.SplitNames(_cur.GenAll);
        if (ws is null) { SetStatus("Chưa chọn workspace.", true); return; }
        if (names.Count == 0) { _tblList = new(); RenderTables(); return; }
        if (BusyGuard()) return;
        try
        {
            SetBusy(true, "Đang đọc table liên quan...");
            _tblList = await _genAll.FindTablesAsync(ws, names);
            if (IsDisposed) return;
            _loading = true; RenderTables(); _loading = false;
            SetStatus($"{_tblList.Count} table liên quan.", ok: true);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    /// <summary>Thêm tay table: "bảng" như cũ; "bảng:điều kiện" (như ô SQL Data của FCode) → tick Dữ liệu + Where = điều kiện (bảng đã chọn thì chỉ
    /// cập nhật Where, giữ Cấu trúc / DB); nhiều dòng (dán danh sách) = nhiều bảng — luật tách ở <see cref="GenAllService.ParseTableSpecs"/>.</summary>
    private async Task AddTableByNameAsync(string? picked = null)
    {
        var specs = GenAllService.ParseTableSpecs(picked ?? _tblAddName.Text);
        HideSugg();
        if (specs.Count == 0) return;
        _tblAddName.Text = "";
        HideSugg();
        try
        {
            foreach (var (name, where) in specs)
            {
                var t = await _genAll.LookupTableAsync(name);
                if (IsDisposed) return;
                if (!t.InApp && !t.InSys) SetStatus($"Không thấy table / view \"{t.Name}\" trong App hoặc Sys.", true);
                var i = _tblList.FindIndex(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
                if (i >= 0) _tblList[i] = t; else _tblList.Add(t);
                if (where is not null) ApplyTableWhere(t, where);
                RenderTables();
            }
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
    }

    private void ApplyTableWhere(RelatedTable t, string where)
    {
        if (_cur is null || _viewOwner is not null || !t.InApp && !t.InSys) return;
        if (t.IsView) { SetStatus($"\"{t.Name}\" là view — không nạp dữ liệu theo điều kiện được (chỉ tick Cấu trúc).", true); return; }
        var old = _cur.Tables.FirstOrDefault(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
        if (old is not null) { old.Data = true; old.Where = where; }
        else _cur.Tables.Add(new TableSelection { Name = t.Name, Sys = !t.InApp && t.InSys, Structure = false, Data = true, Where = where });
        Dirty();
        SetStatus((old is not null ? "Đã cập nhật Where của " : "Đã thêm ") + t.Name + " — Dữ liệu " + (where.Length > 0 ? "WHERE " + where : "(cả bảng)") + ".", ok: true);
    }

    // =====================================================================  Xem trước / Generate

    private List<AdvanceRequest> PickedForPreview()
    {
        var ids = _checked.Count > 0 ? _checked : (_cur is null ? new HashSet<string>() : new HashSet<string> { _cur.Id });
        return _requests.Where(r => ids.Contains(r.Id)).ToList();
    }

    private async Task PreviewAsync()
    {
        var ws = _workspace();
        if (_cur is null || ws is null) { SetStatus("Chưa chọn workspace.", true); return; }
        if (BusyGuard()) return;
        try
        {
            SetBusy(true, "Đang quét file / procedure...");
            var pick = _checked.Count > 0 ? _requests.Where(r => _checked.Contains(r.Id)).ToList() : new List<AdvanceRequest> { _cur };
            var result = await AdvancePackageBuilder.BuildAsync(_genAll, ws, pick, _gSplit.Checked);
            _preview = AdvancePackageBuilder.OrderForRun(result.Items);
            ShowPreview(result.Warnings, "xem trước — bấm đôi file để mở vào tab Query");
            SetStatus($"{_preview.Count} mục trong gói.", ok: true);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    private void ShowPreview(List<string> warnings, string treeHint)
    {
        var nScript = _preview.Count(i => i.IsScript);
        _previewSummary.Text = $"{_preview.Count} mục trong gói ({nScript} script, {_preview.Count - nScript} file). Bấm 1 dòng để xem nội dung" + (nScript > 0 ? "; cột “Mở” / bấm đôi trong cây = mở script vào tab Query." : ".");
        _items.Rows.Clear();
        foreach (var it in _preview)
        {
            var i = _items.Rows.Add(it.IsScript ? "script" : "file", it.Origin, GenAllService.PackagePath(it.RelativeDestPath), it.IsScript ? "Mở" : "");
            var tip = it.SourceFilePath ?? it.Origin;   // như title của dòng trên trang web: đường dẫn nguồn
            foreach (DataGridViewCell c in _items.Rows[i].Cells) c.ToolTipText = c.OwningColumn!.Name == "open" && it.IsScript ? "Mở script này vào tab SQL Query" : tip;
        }
        _items.Visible = _preview.Count > 0;
        _btnOpenAll.Visible = nScript > 0;
        _viewIdx = -1;
        _items.Height = Math.Min(NativeSkin.S(300), _items.ColumnHeadersHeight + Math.Max(1, _preview.Count) * _items.RowTemplate.Height + 4);   // theo cỡ dòng thật (DPI / tỉ lệ), đủ hết các mục nếu ít
        _warnings.Controls.Clear();
        foreach (var w in warnings) _warnings.Controls.Add(new Label { Text = "⚠ " + w, AutoSize = true, ForeColor = AppColors.Warning, BackColor = Color.Transparent, Margin = new Padding(0, 2, 0, 2) });
        _viewHost.Visible = false;
        OrderPkg();
        BuildTree(treeHint);
    }

    /// <summary>Thứ tự trong thẻ "Gói update" (Dock Top: chỉ số lớn nằm trên): tóm tắt → danh sách mục → nội dung mục đang bấm (ngay dưới danh sách, như dòng bung của trang web) → cảnh báo.
    /// Đặt lại mỗi lần hiện vì WinForms có thể đảo z-order khi control đổi Visible / tạo handle (khung nội dung từng nhảy lên trên danh sách).</summary>
    private void OrderPkg()
    {
        var body = _items.Parent;
        if (body is null) return;
        body.SuspendLayout();
        body.Controls.SetChildIndex(_warnings, 0); body.Controls.SetChildIndex(_viewHost, 1); body.Controls.SetChildIndex(_items, 2); body.Controls.SetChildIndex(_previewSummary, 3);
        body.ResumeLayout(true);
    }

    private void BuildTree(string hint)
    {
        _pkgTree.BeginUpdate();
        _pkgTree.Nodes.Clear();
        for (var n = 0; n < _preview.Count; n++) NK.AddPath(_pkgTree.Nodes, GenAllService.PackagePath(_preview[n].RelativeDestPath), n, _preview[n].Origin);
        NK.SortTree(_pkgTree.Nodes);   // như trang web: thư mục trước, file sau, thứ tự tự nhiên
        _pkgTree.ExpandAll();
        if (_pkgTree.Nodes.Count > 0) _pkgTree.TopNode = _pkgTree.Nodes[0];
        _pkgTree.EndUpdate();
        _pkgHint.Text = "Cây gói update — " + hint;
        _pkgPanel.Visible = _preview.Count > 0;
    }

    private void ShowItemText(int i)
    {
        if (i < 0 || i >= _preview.Count) return;
        var text = AdvancePackageBuilder.ItemText(_preview[i]);
        // Tô màu như trang web / xem trước file: script → SQL (giống tab SQL Query); file XML/FCode → bộ tô FCode; còn lại chữ thường. Màu nền theo theme.
        SqlScintilla.ApplyTheme(_viewer); _viewer.Margins[0].Width = 0;
        var lang = _preview[i].IsScript || _preview[i].RelativeDestPath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) ? "SQL" : text.TrimStart().StartsWith('<') ? "XML" : "None";
        if (lang == "SQL") FcodeLexer.ApplySql(_viewer);
        else if (lang == "XML") FcodeLexer.Apply(_viewer);
        else { FcodeLexer.Detach(_viewer); _viewer.Lexer = ScintillaNET.Lexer.Null; }
        _viewer.ReadOnly = false; _viewer.Text = text; _viewer.ReadOnly = true; _viewer.GotoPosition(0);
        FcodeLexer.Restyle(_viewer);
        _viewHost.Visible = true; _viewIdx = i;
        OrderPkg();
    }

    private void ClearPreview()
    {
        _preview = new();
        _items.Rows.Clear(); _items.Visible = false; _viewHost.Visible = false; _viewIdx = -1; _btnOpenAll.Visible = false; _warnings.Controls.Clear(); _pkgPanel.Visible = false; _pkgTree.Nodes.Clear();
        _previewSummary.Text = "Bấm “Xem trước gói” để xem những file / script sẽ được đưa vào gói update của y/c này.";
        _result.Visible = false;
    }

    private void OpenScripts(int only)
    {
        var scripts = new List<(string Title, bool Sys, string Text)>();
        for (var i = 0; i < _preview.Count; i++)
        {
            var it = _preview[i];
            if (it.GeneratedContent is null || (only >= 0 && i != only)) continue;
            var sys = it.RelativeDestPath.Replace('/', '\\').Contains("\\sys\\", StringComparison.OrdinalIgnoreCase);
            scripts.Add((Path.GetFileNameWithoutExtension(it.RelativeDestPath) + (sys ? " [Sys]" : ""), sys, it.GeneratedContent));
        }
        if (scripts.Count == 0) SetStatus("Chưa có script nào — bấm Xem trước gói trước.", true);
        else OpenScriptsRequested?.Invoke(scripts);
    }

    private async Task GenerateAsync()
    {
        var ws = _workspace();
        if (ws is null) { SetStatus("Chưa chọn workspace.", true); return; }
        var ids = _checked.Count > 0 ? _checked.ToHashSet() : (_cur is null ? new HashSet<string>() : new HashSet<string> { _cur.Id });
        var savePath = _gSave.Text.Trim(); var folder = _gFolder.Text.Trim();
        if (ids.Count == 0) { SetStatus("Chưa chọn y/c nào để Generate (tick cột đầu danh sách, hoặc chọn 1 y/c).", true); return; }
        if (savePath.Length == 0) { SetStatus("Chưa nhập Save At Path.", true); return; }
        if (folder.Length == 0) { SetStatus("Chưa nhập Folder Name.", true); return; }
        var picked = _requests.Where(r => ids.Contains(r.Id)).ToList();
        var description = string.Join("\n\n", picked.Select(r => r.Name + (r.Content.Length > 0 ? "\n" + r.Content : "")));
        if (BusyGuard()) return;
        try
        {
            SetBusy(true, "Đang tạo gói update...");
            var merged = await AdvancePackageBuilder.BuildAsync(_genAll, ws, picked, _gSplit.Checked);
            if (merged.Items.Count == 0)
            {
                ShowResult(false, "Không có mục nào để đưa vào gói update.", "");
                _preview = new(); ShowPreview(merged.Warnings, "");
                return;
            }
            var dest = Path.Combine(savePath, folder);
            var note = AdvancePackageBuilder.NoteText(picked);
            var count = await Task.Run(() => GenAllService.CreatePackage(dest, merged.Items, description, note));
            _preview = AdvancePackageBuilder.OrderForRun(merged.Items);
            // Chép lên đường dẫn mạng (tick zip = chỉ đưa .zip lên mạng): lỗi chỉ báo, gói ở Save At Path đã tạo xong vẫn giữ.
            SavePkgOpts();
            if (_gNet.Checked) SetBusy(true, "Đang chép gói lên đường dẫn mạng...");
            var extra = _gNet.Checked ? await GenAllService.DeliverPackageAsync(dest, _gZip.Checked, _gNetRoot.Text) : new PackageDelivery();
            var now = DateTime.Now;
            var viewing = _viewOwner;   // gen từ note người khác: chỉ tạo gói — KHÔNG ghi lịch sử Generate / cột Updated vào note của họ
            if (viewing is null && _gDone.Checked) foreach (var req in picked) req.Done = true;   // ô "Done sau khi gen"
            if (viewing is null)
            {
                foreach (var req in picked)
                {
                    req.Updated = now;
                    req.Generations.Insert(0, new GenerationRecord { Path = dest, Time = now });   // nhớ link gói vừa tạo cho từng y/c đã tick
                    if (req.Generations.Count > AdvanceRequest.MaxGenerations) req.Generations.RemoveRange(AdvanceRequest.MaxGenerations, req.Generations.Count - AdvanceRequest.MaxGenerations);
                }
                _store.Save(_workspace(), _settings, _requests);
            }
            ShowResult(true, $"Đã tạo {count} mục tại:", dest, extra);
            _defaultFolder = Regex.Replace(_defaultFolder, @"\d{14}$", "") + DateTime.Now.ToString("yyyyMMddHHmmss");
            _folderTouched = false; RefreshFolder();
            ShowPreview(merged.Warnings, "gói vừa tạo — bấm đôi file để mở vào tab Query");
            _previewSummary.Text = "Gói vừa tạo — bấm 1 dòng để xem nội dung:";
            RenderList(); RenderHistory();
            if (extra.Errors.Count > 0) SetStatus("Đã tạo gói update nhưng: " + string.Join(" | ", extra.Errors), true);
            else SetStatus(viewing is null ? "Đã tạo gói update." : $"Đã tạo gói update từ note của {viewing} (note của họ không bị ghi lịch sử gen).", ok: true);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    /// <summary>
    /// ⚙ Gen update tất cả / dòng đang chọn (thẻ File Path) — như trang web (AdvanceNoteControl.GeneratePathsAsync): gói CHỈ gồm các đường dẫn đó,
    /// cùng Save At Path / Folder Name / tuỳ chọn mạng; ghi link vào lịch sử gen của y/c đang mở, KHÔNG đánh Done / Updated; xem note người khác thì không ghi.
    /// </summary>
    private async Task GeneratePathsAsync(bool all)
    {
        var ws = _workspace();
        if (ws is null) { SetStatus("Chưa chọn workspace.", true); return; }
        if (_cur is null) { SetStatus("Chọn 1 y/c trước.", true); return; }
        var tb = _fFilePaths.Inner;
        var paths = all ? GenAllService.SplitLines(tb.Text) : AdvancePackageBuilder.LinesAt(tb.Text, tb.SelectionStart, tb.SelectionStart + tb.SelectionLength);
        if (paths.Count == 0) { SetStatus(all ? "Ô File Path chưa có đường dẫn nào." : "Đặt con trỏ vào dòng đường dẫn cần gen (hoặc bôi đen các dòng) rồi bấm lại.", true); return; }
        var savePath = _gSave.Text.Trim(); var folder = _gFolder.Text.Trim();
        if (savePath.Length == 0) { SetStatus("Chưa nhập Save At Path.", true); return; }
        if (folder.Length == 0) { SetStatus("Chưa nhập Folder Name.", true); return; }
        var req = _cur;
        var description = req.Name + (req.Content.Length > 0 ? "\n" + req.Content : "");
        if (BusyGuard()) return;
        try
        {
            SetBusy(true, "Đang tạo gói update từ File Path...");
            var res = await AdvancePackageBuilder.BuildFilePathsAsync(_genAll, ws, paths);
            if (res.Items.Count == 0)
            {
                ShowResult(false, "Không có file nào để đưa vào gói update (đường dẫn không tồn tại?).", "");
                _preview = new(); ShowPreview(res.Warnings, "");
                return;
            }
            var dest = Path.Combine(savePath, folder);
            var count = await Task.Run(() => GenAllService.CreatePackage(dest, res.Items, description, null));
            _preview = AdvancePackageBuilder.OrderForRun(res.Items);
            SavePkgOpts();
            if (_gNet.Checked) SetBusy(true, "Đang chép gói lên đường dẫn mạng...");
            var extra = _gNet.Checked ? await GenAllService.DeliverPackageAsync(dest, _gZip.Checked, _gNetRoot.Text) : new PackageDelivery();
            if (_viewOwner is null)
            {
                req.Generations.Insert(0, new GenerationRecord { Path = dest, Time = DateTime.Now });   // nhớ link gói, không Done / Updated
                if (req.Generations.Count > AdvanceRequest.MaxGenerations) req.Generations.RemoveRange(AdvanceRequest.MaxGenerations, req.Generations.Count - AdvanceRequest.MaxGenerations);
                _store.Save(_workspace(), _settings, _requests);
            }
            ShowResult(true, $"Đã tạo {count} mục tại:", dest, extra);
            _defaultFolder = Regex.Replace(_defaultFolder, @"\d{14}$", "") + DateTime.Now.ToString("yyyyMMddHHmmss");
            _folderTouched = false; RefreshFolder();
            ShowPreview(res.Warnings, "gói vừa tạo — bấm đôi file để mở vào tab Query");
            _previewSummary.Text = "Gói vừa tạo — bấm 1 dòng để xem nội dung:";
            RenderHistory();
            if (extra.Errors.Count > 0) SetStatus("Đã tạo gói update nhưng: " + string.Join(" | ", extra.Errors), true);
            else SetStatus($"Đã tạo gói update từ {paths.Count} đường dẫn ở File Path (chỉ file, không script).", ok: true);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    private void SavePkgOpts() =>GenAllService.SavePackageOptions(_gZip.Checked, _gNet.Checked, _gNetRoot.Text);

    private void ShowNetRow()
    {
        _gNetRoot.Visible = _gZip.Visible = _gNet.Checked;
        var root = _gNetRoot.Text.Trim().TrimEnd('\\', '/');
        _gNetHint.Text = _gNet.Checked ? $"Gói được chép vào {(root.Length > 0 ? root : "...")}\\{Environment.MachineName}\\{(_gZip.Checked ? " (file .zip)" : "")}" : "";
        _gNetHint.Visible = _gNet.Checked;
    }

    private void ShowResult(bool ok, string message, string path, PackageDelivery? extra = null)
    {
        _result.Controls.Clear();
        _result.Controls.Add(new Label { Text = message, AutoSize = true, ForeColor = ok ? AppColors.Success : AppColors.Danger, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0) });
        if (ok)
        {
            _result.Controls.Add(new Label { Text = path, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0), Font = ThemeManager.MonoFont });
            _result.Controls.Add(NK.Button("Copy link", () => NK.CopyText(path))); _result.Controls.Add(NK.Button("Mở thư mục", () => NK.OpenFolder(path)));
            if (_viewOwner is null) _result.Controls.Add(NK.Button("🗑 Xoá", () => DeletePackage(path), tip: "Xoá thư mục gói vừa tạo để gen lại"));
            _lastGenPath = path;
        }
        if (extra is not null)
        {
            Label Line(string text, Color? color = null)
            {
                var l = new Label { Text = text, AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 6, 8, 0), ForeColor = color ?? AppColors.TextMuted };
                _result.Controls.Add(l); _result.SetFlowBreak(l, true);
                return l;
            }
            if (_result.Controls.Count > 0) _result.SetFlowBreak(_result.Controls[^1], true);
            if (extra.WebLink is { } link)
            {
                Line("Link: " + link, AppColors.Text);
                var copy = NK.Button("Copy link web", () => { try { Clipboard.SetText(link); } catch { /* clipboard bận */ } });
                _result.Controls.Add(copy); _result.SetFlowBreak(copy, true);
            }
            if (extra.NetworkPath is not null) Line((extra.WebLink is null ? "Mạng: " : "File trên mạng: ") + extra.NetworkPath);
            foreach (var e in extra.Errors) Line("⚠ " + e, AppColors.Danger);
        }
        _result.Visible = true;
    }

    // =====================================================================  Sync / file / nội dung

    private async Task SyncAsync()
    {
        if (ReadOnlyGuard()) return;
        EnsureLoaded();
        if (BusyGuard()) return;
        var programmer = _syncProg.Text.Trim();
        if (programmer.Length > 0 && _settings is not null && _settings.NoteProgrammer != programmer)
        {
            _settings.NoteProgrammer = programmer;
            try { _settings.Save(); } catch { /* không lưu được thì chỉ phải nhập lại lần sau */ }
        }
        SetBusy(true, "Đang đồng bộ yêu cầu...");
        try
        {
            var (ok, message, changed) = await AdvancePackageBuilder.SyncAsync(_requests, _workspace(), programmer);
            if (changed) _store.Save(_workspace(), _settings, _requests);
            RenderList();
            Select(_cur?.Id);
            SetStatus(message, !ok, ok);
        }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { SetBusy(false); }
    }

    /// <summary>"🤖 AI tạo danh mục" cho y/c đang chọn (tên, nội dung) — MainForm mở Tạo nhanh danh mục và hỏi AI ở đó.</summary>
    public event Action<string, string>? AiCatalogRequested;

    /// <summary>Nút "🗂 Nhiều dự án": MainForm mở BcodeNote.exe vào dự án đang chọn.</summary>
    public event Action? NoteHubRequested;

    /// <summary>Nút "👥 Cả nhóm": mở tab xem note của người khác + gói update của dự án (TeamNotesControl).</summary>
    public event Action? TeamRequested;

    /// <summary>Nạp lại danh sách từ file (vd vừa chép 1 y/c của người khác về tab Cả nhóm).</summary>
    public void ReloadFromStore() { if (_viewOwner is not null) return; /* đang xem note người khác: về note mình sẽ tự nạp lại */ _loadedFor = ""; Init(); }

    /// <summary>Nút "AI tạo danh mục" / phím tắt note.aiCatalog: lưu y/c đang sửa rồi gửi tên + nội dung (như trang advnote.html).</summary>
    public void RequestAiCatalog()
    {
        Flush();
        if (_cur is null) { SetStatus("Chọn 1 y/c trước.", true); return; }
        if (string.IsNullOrWhiteSpace(_cur.Content)) { SetStatus("Y/c chưa có nội dung — Sync yêu cầu hoặc nhập Request Content trước.", true); return; }
        AiCatalogRequested?.Invoke(_cur.Name ?? "", _cur.Content);
    }

    /// <summary>"🔎 AI phân tích" (mode "analyze") / "⚙ Theo luật Fast" (mode "rules") cho y/c đang chọn (tên, nội dung, mode)
    /// — MainForm mở Tạo nhanh danh mục ở đúng chế độ.</summary>
    public event Action<string, string, string>? CatalogAssistRequested;

    /// <summary>Nút / phím tắt note.aiAnalyze, note.fastRules: lưu y/c đang sửa rồi gửi tên + nội dung + mode (như trang advnote.html).</summary>
    public void RequestCatalogAssist(string mode)
    {
        Flush();
        if (_cur is null) { SetStatus("Chọn 1 y/c trước.", true); return; }
        if (string.IsNullOrWhiteSpace(_cur.Content)) { SetStatus("Y/c chưa có nội dung — Sync yêu cầu hoặc nhập Request Content trước.", true); return; }
        CatalogAssistRequested?.Invoke(_cur.Name ?? "", _cur.Content, mode);
    }

    private void BrowseFiles()
    {
        using var dlg = new OpenFileDialog { Multiselect = true, Title = "Chọn file đưa vào gói update", CheckFileExists = true };
        var ws = _workspace();
        if (!string.IsNullOrWhiteSpace(ws?.SourcePath) && Directory.Exists(ws.SourcePath)) dlg.InitialDirectory = ws.SourcePath;
        if (ReadOnlyGuard() || dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        AddPaths(dlg.FileNames);
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        if (ReadOnlyGuard()) return;
        var cur = _fFilePaths.Text.TrimEnd();
        _fFilePaths.Text = (cur.Length > 0 ? cur + "\r\n" : "") + string.Join("\r\n", paths);
    }

    private void OpenContent()
    {
        if (_cur is null) return;
        using var dlg = new RequestContentDialog(_cur.Name, _cur.Content);
        dlg.ShowDialog(FindForm());
        if (_viewOwner is null && dlg.Text.Length >= 0 && dlg.ResultText != _cur.Content) { _cur.Content = dlg.ResultText; Dirty(); Flush(); }
    }

    private PillButton? _noteButton;

    private void OpenNote()
    {
        if (_cur is null) return;
        using var dlg = new RequestContentDialog(_cur.Name, _cur.Note ?? "", plain: true);
        dlg.ShowDialog(FindForm());
        if (_viewOwner is null && dlg.ResultText != (_cur.Note ?? "")) { _cur.Note = dlg.ResultText; Dirty(); Flush(); }
        PaintNoteButton();
    }

    /// <summary>Y/c đang chọn có Note → nút thành "📝 Note ●" (như nút App / Sys có script).</summary>
    private void PaintNoteButton()
    {
        if (_noteButton is null) return;
        _noteButton.Text = string.IsNullOrWhiteSpace(_cur?.Note) ? "📝 Note" : "📝 Note ●";
        _noteButton.Invalidate();
    }
}

/// <summary>Khung "Request Content" phóng to: xem định dạng (dòng Menu / Ghi chú / thông tin bộ phận tô màu) hoặc sửa chữ thuần; Copy; Esc đóng.</summary>
internal sealed class RequestContentDialog : ThemedForm
{
    protected override DpiLayout DpiLayoutMode => DpiLayout.PreScaled;   // kích thước dựng bằng NK / NativeSkin.S (đã gồm DPI × UiScale)
    private readonly RichTextBox _rich = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None };
    private readonly TextBox _edit = new() { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, WordWrap = true, Visible = false };
    private readonly PillButton _toggle;

    public string ResultText => _edit.Text;

    /// <param name="plain">true = khung Note (lưu ý cho người triển khai → note.txt): chỉ sửa chữ thuần, không có chế độ xem định dạng.</param>
    public RequestContentDialog(string name, string content, bool plain = false)
    {
        Text = (plain ? "Note (note.txt) — " : "Request Content — ") + name;
        Width = NativeSkin.S(980); Height = NativeSkin.S(700); StartPosition = FormStartPosition.CenterParent; KeyPreview = true; ShowIcon = false;
        _edit.Text = content;
        var bar = new BarFlow { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 6) };
        _toggle = NK.Button("✏ Sửa", () => SetMode(!_edit.Visible));
        if (!plain) bar.Controls.Add(_toggle);
        bar.Controls.Add(NK.Button("Copy", () => NK.CopyText(_edit.Text), tip: "Copy toàn bộ nội dung"));
        bar.Controls.Add(NK.Button("Đóng", Close, primary: true));
        Controls.Add(_rich); Controls.Add(_edit); Controls.Add(bar);
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) => SetMode(plain || string.IsNullOrWhiteSpace(_edit.Text));   // có nội dung → xem định dạng; trống (hoặc Note) thì vào luôn chế độ sửa
    }

    private void SetMode(bool edit)
    {
        _edit.Visible = edit; _rich.Visible = !edit;
        _toggle.Text = edit ? "👁 Xem đẹp" : "✏ Sửa"; _toggle.Invalidate();
        if (edit) { _edit.Focus(); _edit.SelectionStart = 0; _edit.SelectionLength = 0; } else Render();
    }

    // Cùng cách dựng với richreq.js (trang web): tách khối Menu / nội dung / Ghi chú / --- Bộ phận rồi tô màu từng phần.
    private Font? _base, _bold, _small, _smallBold;

    private void Render()
    {
        var dark = AppColors.IsDark;
        Color proj = dark ? ColorTranslator.FromHtml("#d9904f") : ColorTranslator.FromHtml("#8a4b12"), last = dark ? ColorTranslator.FromHtml("#5aa9ff") : ColorTranslator.FromHtml("#0b3fd0");
        Color t1 = dark ? ColorTranslator.FromHtml("#6fa0e8") : ColorTranslator.FromHtml("#0047ab"), t2 = dark ? ColorTranslator.FromHtml("#e0884f") : ColorTranslator.FromHtml("#b3501a"), lt = dark ? ColorTranslator.FromHtml("#58b27a") : ColorTranslator.FromHtml("#1d7a43");
        var baseFont = _base ??= new Font("Segoe UI", 10.5f);
        _rich.Font = baseFont;
        _bold ??= new Font(baseFont, FontStyle.Bold); _small ??= new Font("Segoe UI", 9.5f); _smallBold ??= new Font(_small, FontStyle.Bold);
        _rich.Clear();
        _rich.BackColor = AppColors.Input; _rich.ForeColor = AppColors.Text;
        _rich.SelectionIndent = 8; _rich.SelectionRightIndent = 8;

        void Put(string s, Color c, Font? f = null) { _rich.SelectionStart = _rich.TextLength; _rich.SelectionLength = 0; _rich.SelectionColor = c; _rich.SelectionFont = f ?? baseFont; _rich.AppendText(s); }

        var (menu, body, notes, info) = Parse(_edit.Text);
        if (menu.Length > 0)
        {
            var s = menu; var p = ""; var tail = "";
            var m = Regex.Match(s, @"^\[([^\]]+)\]\s*");
            if (m.Success) { p = m.Groups[1].Value; s = s[m.Length..]; }
            m = Regex.Match(s, @"((?:\s*\[[^\]]+\])+)\s*$");
            if (m.Success) { tail = m.Groups[1].Value.Trim(); s = s[..m.Index].Trim(); }
            if (p.Length > 0) Put("[" + p + "] ", proj, _bold);
            var parts = s.Split('\\').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            for (var i = 0; i < parts.Count; i++)
            {
                if (i > 0) Put(" \\ ", AppColors.TextMuted);
                Put(parts[i], i == parts.Count - 1 ? last : AppColors.Text, _bold);
            }
            if (tail.Length > 0) Put("    " + tail, AppColors.TextMuted, _bold);
            Put("\n\n", AppColors.Text);
        }
        if (body.Length > 0) Put(body + "\n\n", AppColors.Text);
        var lines = notes.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count > 0)
        {
            Put("Ghi chú\n", AppColors.TextMuted, _smallBold);
            foreach (var l in lines)
            {
                var m = Regex.Match(l, @"^\s*(\[[^\]]*\])(\[[^\]]*\])?\s*:?\s*([\s\S]*)$");
                if (!m.Success) { Put(l + "\n", AppColors.Text, _small); continue; }
                Put(m.Groups[1].Value, t1, _smallBold);
                if (m.Groups[2].Success) Put(m.Groups[2].Value, t2, _smallBold);
                Put(": ", AppColors.Text, _small);
                var rest = m.Groups[3].Value; var at = 0;
                foreach (Match x in Regex.Matches(rest, @"\[LT:[^\]]*\]")) { Put(rest[at..x.Index], AppColors.Text, _small); Put(x.Value, lt, _small); at = x.Index + x.Length; }
                Put(rest[at..] + "\n", AppColors.Text, _small);
            }
            Put("\n", AppColors.Text);
        }
        if (info.Length > 0) { Put("────────────────────\n", AppColors.Border, _small); Put(info + "\n", AppColors.TextMuted, _small); }
        _rich.SelectionStart = 0; _rich.ScrollToCaret();
    }

    /// <summary>Như RichReq.parse: khối đầu "Menu: …", khối "Ghi chú: …", khối "---\nBộ phận: …"; còn lại là nội dung.</summary>
    private static (string Menu, string Body, string Notes, string Info) Parse(string text)
    {
        var blocks = Regex.Split(text.Replace("\r", ""), @"\n[ \t]*\n");
        string menu = "", notes = "", info = ""; var body = new List<string>();
        for (var i = 0; i < blocks.Length; i++)
        {
            var t = blocks[i].Trim('\n');
            if (t.Trim().Length == 0) continue;
            Match m;
            if (i == 0 && (m = Regex.Match(t, @"^\s*Menu:\s*([\s\S]*)$")).Success) { menu = m.Groups[1].Value.Trim(); continue; }
            if ((m = Regex.Match(t, @"^\s*Ghi chú:\s*([\s\S]*)$")).Success) { notes = m.Groups[1].Value.Trim(); continue; }
            if ((m = Regex.Match(t, @"^\s*---\s*\n\s*(Bộ phận:[\s\S]*)$")).Success) { info = m.Groups[1].Value.Trim(); continue; }
            body.Add(t);
        }
        return (menu, string.Join("\n\n", body), notes, info);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _base?.Dispose(); _bold?.Dispose(); _small?.Dispose(); _smallBold?.Dispose(); }
        base.Dispose(disposing);
    }
}
