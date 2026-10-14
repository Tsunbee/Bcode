using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Gen Update (genupdate.html): cấu hình gói, gen nhanh Store / Table, cây Source File để tick rồi Add, danh sách file trong gói và Create Update File.</summary>
internal sealed class GenUpdatePage : NativePage
{
    private sealed class TblRow
    {
        public string Name = ""; public bool InApp, InSys, IsView, Sys, Structure, Data; public string Where = ""; public string RowsApp = "", RowsSys = "", Controllers = "";
        public ComboBox? Db; public CheckBox? St, Da; public RoundedInput? Wh; public Label? Info;
    }
    private sealed class BatchItem { public string Key = "", Name = "", Rel = "", Kind = ""; }

    private readonly Label _ws = NK.Muted(""), _status = NK.Muted("");
    private readonly RoundedInput _pProgram = NK.Input(""), _pMobile = NK.Input(""), _pSave = NK.Input(""), _pFolder = NK.Input(""), _pDesc = NK.Memo(2, "Mô tả gói update (ghi vào description.txt)", mono: false);
    private readonly SegmentedBar _db = new(("app", "App"), ("sys", "Sys"), ("other", "Other"));
    private readonly RoundedInput _storeName = NK.Input("Tên stored procedure..."), _tblCtrl = NK.Input("Controller / SysId (vd SVTran, ARTran)..."), _tblName = NK.Input("Hoặc nhập tên table / view..."), _nameBox = NK.Input("Tên / SysId (khớp chứa)..."), _treeFilter = NK.Input("Lọc trong cây...");
    private readonly AutoCompleteStringCollection _procSrc = new(), _tblSrc = new(), _nameSrc = new();
    private readonly DropDownButton _cat = new() { Width = NativeSkin.S(170) };
    private readonly FlowLayoutPanel _tblBox = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private readonly Label _tblCount = NK.Muted(""), _treeCount = NK.Muted(""), _treeTitle = NK.Muted(""), _checkedCount = NK.Muted(""), _batchCount = NK.Muted("0"), _batchSummary = NK.Muted("");
    private readonly Label _treeEmpty = new MutedLabel { AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.Transparent, Visible = false };
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false, ShowNodeToolTips = true };
    private readonly CheckBox _checkAll = NK.Check("Check All"), _batchAll = NK.Check("Chọn tất cả");
    private readonly ListView _batchList = new() { Dock = DockStyle.Fill, CheckBoxes = true, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, BorderStyle = BorderStyle.None };
    private readonly PillButton _bAdd, _bRemove, _bClear, _bCreate;
    private readonly Label _resTitle = NK.Muted(""), _resPath = NK.Muted("", true);
    private readonly Panel _result = new() { Dock = DockStyle.Bottom, AutoSize = true, Visible = false, Padding = new Padding(8) };
    private readonly List<TblRow> _tbls = new();
    private List<BatchItem> _batch = new();
    private List<string> _cats = new();
    private string _folderPrefix = "", _lastPath = "", _treeJson = "";
    private bool _busy, _loading;
    private JsonElement _root;
    private int _leafCount;

    public GenUpdatePage()
    {
        // ---- cột trái ----
        var c1 = new CardPanel("Đường dẫn & kết nối") { Dock = DockStyle.Top };
        var saveRow = NK.InputWithButton(_pSave, NK.Button("📂", () => Post(new { action = "open", path = _pSave.Text }), tip: "Mở thư mục lưu"), gap: 6);
        var folRow = NK.InputWithButton(_pFolder, NK.Button("↻", () => _pFolder.Text = NewFolderName(), tip: "Đổi sang thời điểm hiện tại"), gap: 6);
        c1.Body.Controls.Add(NK.FormGrid(104, ("Program Path", _pProgram), ("Mobile Path", _pMobile), ("Save At Path", saveRow), ("Folder Name", folRow), ("Description", _pDesc)));

        var c2 = new CardPanel("Gen nhanh Store (Procedure)") { Dock = DockStyle.Top };
        _storeName.Inner.AutoCompleteMode = AutoCompleteMode.SuggestAppend; _storeName.Inner.AutoCompleteSource = AutoCompleteSource.CustomSource; _storeName.Inner.AutoCompleteCustomSource = _procSrc;
        var bGenStore = NK.Button("Gen Script", GenStore, primary: true);
        NK.Stack(c2.Body, _db, NK.Spacer(10), Wrap(_storeName, bGenStore), NK.Spacer(6), NK.Muted("Script ALTER được thêm thẳng vào gói tại Script\\app hoặc Script\\sys theo App/Sys đã chọn.", true));
        _storeName.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; GenStore(); } };

        var c3 = new CardPanel("Gen nhanh Table") { Dock = DockStyle.Top };
        _tblCtrl.Inner.AutoCompleteMode = AutoCompleteMode.SuggestAppend; _tblCtrl.Inner.AutoCompleteSource = AutoCompleteSource.CustomSource; _tblCtrl.Inner.AutoCompleteCustomSource = _nameSrc;
        _tblName.Inner.AutoCompleteMode = AutoCompleteMode.SuggestAppend; _tblName.Inner.AutoCompleteSource = AutoCompleteSource.CustomSource; _tblName.Inner.AutoCompleteCustomSource = _tblSrc;
        _tblCtrl.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; LoadTbl(); } };
        _tblName.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; AddTbl(); } };
        _tblCount.Visible = false;   // chỉ hiện khi đã có table (trước đây chiếm 1 dòng trống ở đầu thẻ)
        c3.RightBar.Controls.Add(_tblCount);
        NK.Stack(c3.Body,
            Wrap(_tblCtrl, NK.Button("Tải table", LoadTbl, tip: "Liệt kê các table khai báo trong file liên quan tới controller")), NK.Spacer(8),
            Wrap(_tblName, NK.Button("＋ Thêm", AddTbl)), NK.Spacer(6), _tblBox,
            NK.Muted("Cấu trúc = script tạo table (gộp vào APP_Script.sql) hoặc tạo lại view; Dữ liệu = xoá rồi nạp lại dòng (lọc bằng Where, trống = cả bảng, quá 50.000 dòng cần Where).", true),
            NK.Spacer(8), NK.Row(NK.Button("Gen Script", GenTables, primary: true)));
        var left = new Panel { Dock = DockStyle.Left, Width = NativeSkin.S(430), AutoScroll = true, Padding = new Padding(12, 10, 8, 12) };
        NK.Stack(left, c1, c2, c3);

        // ---- giữa: cây source ----
        _cat.SetItems(new[] { "(chưa có)" }, 0);
        _nameBox.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Search(); } };
        _nameBox.Inner.AutoCompleteMode = AutoCompleteMode.SuggestAppend; _nameBox.Inner.AutoCompleteSource = AutoCompleteSource.CustomSource; _nameBox.Inner.AutoCompleteCustomSource = _nameSrc;
        _bAdd = NK.Button("＋ Add", AddChecked, primary: true); _bAdd.Enabled = false;
        var tools = TopBar(); tools.Controls.AddRange(new Control[] { _cat, _nameBox, NK.Button("Tìm", Search, primary: true) });
        var tbar = TopBar(); _treeFilter.Width = NativeSkin.S(200);
        tbar.Controls.AddRange(new Control[] { _checkAll, NK.Button("⊞", () => { _tree.ExpandAll(); }, tip: "Mở rộng tất cả"), NK.Button("⊟", () => { _tree.CollapseAll(); _tree.Nodes.OfType<TreeNode>().FirstOrDefault()?.Expand(); }, tip: "Thu gọn tất cả"), _treeFilter, _checkedCount, _bAdd });
        var cm = new CardPanel("Source File") { Dock = DockStyle.Fill };
        cm.RightBar.Controls.Add(_treeCount);
        _tree.AfterCheck += OnTreeCheck; _treeFilter.TextChanged += (_, _) => RebuildTree();
        _checkAll.Click += (_, _) => { _loading = true; foreach (TreeNode n in _tree.Nodes) SetChecked(n, _checkAll.Checked); _loading = false; UpdateCounts(); };
        var mid = new Panel { Dock = DockStyle.Fill };
        _treeEmpty.Dock = DockStyle.Fill;
        mid.Controls.Add(_tree); mid.Controls.Add(_treeEmpty); mid.Controls.Add(tbar); mid.Controls.Add(tools);
        NK.ThemeTree(_tree);
        cm.Body.Controls.Add(mid); cm.Body.Dock = DockStyle.Fill;

        // ---- phải: gói update ----
        _batchList.Columns.Add("Tên", 200); _batchList.Columns.Add("Loại", 70); _batchList.Columns.Add("Đường dẫn", 260);
        NK.ThemeList(_batchList);
        _batchList.ItemChecked += (_, _) => { if (!_loading) SyncBatchAll(); };
        _batchAll.Click += (_, _) => { _loading = true; foreach (ListViewItem i in _batchList.Items) i.Checked = _batchAll.Checked; _loading = false; SyncBatchAll(); };
        _bRemove = NK.Button("Bỏ file đã chọn", () => Post(new { action = "removeBatch", keys = _batchList.CheckedItems.Cast<ListViewItem>().Select(i => (string)i.Tag!).ToList() })); _bRemove.ForeOverride = AppColors.Danger;
        _bClear = NK.Button("Xoá hết", () => { if (_batch.Count > 0) Post(new { action = "clearBatch" }); }); _bClear.ForeOverride = AppColors.Danger;
        _bCreate = NK.Button("⚙ Create Update File", Create, primary: true);
        var btools = TopBar(); btools.Controls.AddRange(new Control[] { _batchAll, _bRemove, _bClear, _batchSummary });
        _result.Controls.Add(_resPath); _result.Controls.Add(_resTitle);
        _resTitle.Dock = DockStyle.Top; _resPath.Dock = DockStyle.Top;
        var resBtn = NK.Row(NK.Button("Copy link", () => { Post(new { action = "copy", text = _lastPath }); Toast("Đã copy link.", "ok"); }), NK.Button("Mở thư mục", () => Post(new { action = "open", path = _lastPath }))); resBtn.Dock = DockStyle.Top;
        _result.Controls.Add(resBtn); _result.Controls.SetChildIndex(resBtn, 0);
        var createBox = new Panel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8) };
        _bCreate.Dock = DockStyle.Top; _result.Dock = DockStyle.Top;
        createBox.Controls.Add(_result); createBox.Controls.Add(_bCreate);
        var cr = new CardPanel("File đã thêm vào gói update") { Dock = DockStyle.Fill };
        cr.RightBar.Controls.Add(_batchCount);
        var rbody = new Panel { Dock = DockStyle.Fill };
        rbody.Controls.Add(_batchList); rbody.Controls.Add(btools); rbody.Controls.Add(createBox);
        cr.Body.Controls.Add(rbody); cr.Body.Dock = DockStyle.Fill;

        // KHÔNG đặt Panel1MinSize/Panel2MinSize trong initializer: lúc vừa tạo SplitContainer chỉ rộng 150px mặc định, gán MinSize 200 ném ngay
        // "SplitterDistance must be between Panel1MinSize and Width - Panel2MinSize" từ constructor (lỗi mở Gen Update ở chế độ App). Đặt sau khi đã có kích thước thật.
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        var p1 = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 10, 6, 12) }; p1.Controls.Add(cm);
        var p2 = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 10, 12, 12) }; p2.Controls.Add(cr);
        split.Panel1.Controls.Add(p1); split.Panel2.Controls.Add(p2);
        HandleCreated += (_, _) => BeginInvoke(() =>
        {
            try
            {
                split.SplitterDistance = Math.Max(220, split.Width / 2);
                if (split.Width >= 200 + 200 + split.SplitterWidth) { split.Panel1MinSize = 200; split.Panel2MinSize = 200; }
            }
            catch { }
        });

        var head = new BarFlow { Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 4), AutoSize = true };
        head.Controls.AddRange(new Control[] { new Label { Text = "Gen Update", AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 2, FontStyle.Bold), Margin = new Padding(0, 4, 10, 0) }, _ws, _status });
        Controls.Add(split); Controls.Add(left); Controls.Add(head);
        _tree.NodeMouseDoubleClick += (_, _) => { };
    }

    private static Control Wrap(RoundedInput input, PillButton btn)
    {
        return NK.InputWithButton(input, btn);
    }

    private static BarFlow TopBar() => new() { WrapContents = true, AutoSize = true, Padding = new Padding(10, 8, 10, 6), Dock = DockStyle.Top };

    private string NewFolderName() => (_folderPrefix.Length > 0 ? _folderPrefix + "_" : "") + DateTime.Now.ToString("yyyyMMddHHmmss");
    private void Toast(string t, string? kind = null) { _status.Text = t; _status.ForeColor = kind == "err" ? AppColors.Danger : kind == "ok" ? AppColors.Success : AppColors.TextMuted; }
    private void SetBusy(bool on, string? msg = null) { _busy = on; Cursor = on ? Cursors.AppStarting : Cursors.Default; if (on && msg is not null) Toast(msg); }

    // ---- hành động ----
    private void Search() { SetBusy(true, "Đang quét file..."); Post(new { action = "search", category = _cats.Count > 0 ? _cats[Math.Max(0, _cat.SelectedIndex)] : "", name = _nameBox.Text }); }
    private void GenStore()
    {
        var name = _storeName.Text.Trim();
        if (name.Length == 0) { Toast("Nhập tên stored procedure trước.", "err"); return; }
        if (_db.Value == "other") { Toast("Chọn App hoặc Sys trước khi Gen Script (Other không áp dụng).", "err"); return; }
        SetBusy(true, "Đang lấy script..."); Post(new { action = "genStore", name, db = _db.Value });
    }
    private void LoadTbl() { var v = _tblCtrl.Text.Trim(); if (v.Length == 0) { Toast("Nhập controller / SysId trước.", "err"); return; } SetBusy(true, "Đang dò table liên quan..."); Post(new { action = "loadTables", controllers = v }); }
    private void AddTbl() { var v = _tblName.Text.Trim(); if (v.Length == 0) return; Post(new { action = "lookupTable", name = v }); _tblName.Text = ""; }
    private void GenTables()
    {
        var tables = _tbls.Where(t => t.Structure || t.Data).Select(t => new { name = t.Name, sys = t.Sys, structure = t.Structure, data = t.Data, where = t.Where.Trim() }).ToList();
        if (tables.Count == 0) { Toast("Tick Cấu trúc và/hoặc Dữ liệu ở ít nhất 1 table.", "err"); return; }
        SetBusy(true, "Đang sinh script table..."); Post(new { action = "genTables", tables });
    }
    private void Create()
    {
        if (_batch.Count == 0) { Toast("Chưa có file nào trong gói update — tick file bên Source File rồi bấm Add.", "err"); return; }
        if (_pSave.Text.Trim().Length == 0) { Toast("Chưa nhập Save At Path.", "err"); return; }
        if (_pFolder.Text.Trim().Length == 0) { Toast("Chưa nhập Folder Name.", "err"); return; }
        SetBusy(true, "Đang tạo gói update..."); Post(new { action = "create", savePath = _pSave.Text, folder = _pFolder.Text, description = _pDesc.Text });
    }
    private void AddChecked()
    {
        var paths = new List<string>(); void Walk(TreeNode n) { if (n.Tag is string p && n.Checked) paths.Add(p); foreach (TreeNode c in n.Nodes) Walk(c); }
        foreach (TreeNode n in _tree.Nodes) Walk(n);
        if (paths.Count > 0) Post(new { action = "add", paths });
    }

    // ---- cây ----
    private void BuildTree(TreeNodeCollection into, JsonElement n, string filter, ref int leaves)
    {
        var name = P(n, "name");
        if (PB(n, "dir"))
        {
            var node = new TreeNode(name) { ImageKey = "folder", SelectedImageKey = "folder" };
            foreach (var c in PA(n, "children")) BuildTree(node.Nodes, c, filter, ref leaves);
            if (node.Nodes.Count == 0 && (filter.Length > 0 || !PA(n, "children").Any())) return;
            node.Text = $"{name}  ({CountLeaves(node)})";
            into.Add(node);
            return;
        }
        if (filter.Length > 0 && !name.ToLowerInvariant().Contains(filter)) return;
        leaves++;
        var issues = PA(n, "issues").Select(i => i.GetString() ?? "").ToList();
        into.Add(new TreeNode(name) { ImageKey = "file", SelectedImageKey = "file", Tag = P(n, "path"), ToolTipText = P(n, "path") + (issues.Count > 0 ? "\n" + string.Join("\n", issues) : ""), ForeColor = issues.Count > 0 ? AppColors.Danger : AppColors.Text });
    }

    private static int CountLeaves(TreeNode n) => n.Nodes.Count == 0 ? (n.Tag is string ? 1 : 0) : n.Nodes.Cast<TreeNode>().Sum(CountLeaves);

    private void RebuildTree()
    {
        var checkedPaths = new HashSet<string>(); void Collect(TreeNode n) { if (n.Tag is string p && n.Checked) checkedPaths.Add(p); foreach (TreeNode c in n.Nodes) Collect(c); }
        foreach (TreeNode n in _tree.Nodes) Collect(n);
        _loading = true;
        _tree.BeginUpdate(); _tree.Nodes.Clear();
        if (_root.ValueKind == JsonValueKind.Object)
        {
            var leaves = 0; var filter = _treeFilter.Text.Trim().ToLowerInvariant();
            BuildTree(_tree.Nodes, _root, filter, ref leaves);
            _leafCount = leaves;
            void Restore(TreeNode n) { if (n.Tag is string p && checkedPaths.Contains(p)) n.Checked = true; foreach (TreeNode c in n.Nodes) Restore(c); }
            foreach (TreeNode n in _tree.Nodes) Restore(n);
            if (_leafCount > 150 && filter.Length == 0) { foreach (TreeNode n in _tree.Nodes) { n.Expand(); foreach (TreeNode c in n.Nodes) c.Collapse(); } } else _tree.ExpandAll();
        }
        _tree.EndUpdate();
        // Nạp cây mới (bấm menu / Tìm): cuộn về ĐẦU cây — ExpandAll để thanh cuộn ở cuối danh sách.
        if (_tree.Nodes.Count > 0) { _tree.TopNode = _tree.Nodes[0]; _tree.SelectedNode = null; }
        _loading = false;
        UpdateCounts();
        // Cây trống: hiện chữ hướng dẫn ở giữa (trước đây là 1 nút giả có ô tick, bị tô như đang chọn).
        var empty = _tree.Nodes.Count == 0;
        _treeEmpty.Text = _root.ValueKind == JsonValueKind.Object ? "Không có file nào khớp." : "Double-click 1 menu trong cây WCommand bên trái,\nhoặc gõ tên rồi bấm “Tìm”.";
        _treeEmpty.Visible = empty; _tree.Visible = !empty;
    }

    private void SetChecked(TreeNode n, bool v) { n.Checked = v; foreach (TreeNode c in n.Nodes) SetChecked(c, v); }

    private void OnTreeCheck(object? s, TreeViewEventArgs e)
    {
        if (_loading || e.Node is null) return;
        _loading = true; foreach (TreeNode c in e.Node.Nodes) SetChecked(c, e.Node.Checked); _loading = false;
        UpdateCounts();
    }

    private void UpdateCounts()
    {
        var total = 0; var chk = 0;
        void Walk(TreeNode n) { if (n.Tag is string) { total++; if (n.Checked) chk++; } foreach (TreeNode c in n.Nodes) Walk(c); }
        foreach (TreeNode n in _tree.Nodes) Walk(n);
        _treeCount.Text = total > 0 ? total + " file" : ""; _checkedCount.Text = chk > 0 ? "đã tick " + chk : "";
        _loading = true; _checkAll.Checked = total > 0 && chk == total; _loading = false;
        _bAdd.Enabled = chk > 0;
    }

    // ---- gói + bảng ----
    private void RenderBatch()
    {
        _loading = true;
        _batchList.BeginUpdate(); _batchList.Items.Clear();
        foreach (var b in _batch) _batchList.Items.Add(new ListViewItem(new[] { b.Name, b.Kind == "script" ? "script" : "file", b.Rel }) { Tag = b.Key, ToolTipText = b.Rel });
        _batchList.EndUpdate();
        _loading = false;
        _batchCount.Text = _batch.Count.ToString();
        var scripts = _batch.Count(b => b.Kind == "script");
        _batchSummary.Text = _batch.Count > 0 ? $"{_batch.Count - scripts} file · {scripts} script" : "";
        SyncBatchAll();
    }

    private void SyncBatchAll()
    {
        var n = _batchList.CheckedItems.Count; var t = _batch.Count;
        _loading = true; _batchAll.Checked = t > 0 && n == t; _loading = false;
        _bRemove.Enabled = n > 0; _bClear.Enabled = t > 0;
    }

    private void RenderTbls()
    {
        _tblBox.SuspendLayout(); _tblBox.Controls.Clear();
        _tblCount.Text = _tbls.Count > 0 ? _tbls.Count + " table" : ""; _tblCount.Visible = _tbls.Count > 0;
        foreach (var t in _tbls)
        {
            var missing = !t.InApp && !t.InSys;
            var card = new Panel { Width = Math.Max(300, _tblBox.Width - 6), Height = NativeSkin.RowHeight(this, 66), Margin = new Padding(0, 4, 0, 4) };
            var name = new Label { Text = t.Name, AutoSize = true, Left = 4, Top = 4, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont, FontStyle.Bold), ForeColor = missing ? AppColors.TextMuted : AppColors.Text };
            // Ô chọn App/Sys bo tròn theo theme (DropDownButton) thay ComboBox trắng của hệ điều hành.
            var dbItems = new List<string>(); if (t.InApp || !t.InSys) dbItems.Add("App"); if (t.InSys) dbItems.Add("Sys");
            var db = new DropDownButton { Width = 72, Left = 190, Top = 0, Height = NativeSkin.RowHeight(this, 26) };
            db.SetItems(dbItems, Math.Max(0, t.Sys && t.InSys ? dbItems.Count - 1 : 0));
            var rows = t.Sys ? t.RowsSys : t.RowsApp;
            var info = new Label { Text = missing ? "không thấy" : t.IsView ? "view" : (rows.Length > 0 ? rows + " dòng" : ""), AutoSize = true, Left = 262, Top = 6, BackColor = Color.Transparent, ForeColor = AppColors.TextMuted };
            var rm = NK.Button("✕", () => { _tbls.Remove(t); RenderTbls(); }, tip: "Bỏ khỏi danh sách"); rm.AutoWidth = false; rm.Width = NativeSkin.S(30); rm.Left = card.Width - 36; rm.Top = 0; rm.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var st = new CheckBox { Text = "Cấu trúc", AutoSize = true, Left = 4, Top = 32, Checked = t.Structure, Enabled = !missing };
            var da = new CheckBox { Text = "Dữ liệu", AutoSize = true, Left = 90, Top = 32, Checked = t.Data && !t.IsView, Enabled = !missing && !t.IsView };
            var wh = NK.Input("Where, vd ma_maubc = 'abc'", mono: true); wh.Left = 170; wh.Top = 28; wh.Width = card.Width - 176; wh.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; wh.Text = t.Where; wh.Enabled = t.Data && !t.IsView;
            db.SelectionChanged += i => { t.Sys = dbItems[i] == "Sys"; info.Text = missing ? "không thấy" : t.IsView ? "view" : ((t.Sys ? t.RowsSys : t.RowsApp) is { Length: > 0 } r ? r + " dòng" : ""); };
            st.CheckedChanged += (_, _) => t.Structure = st.Checked;
            da.CheckedChanged += (_, _) => { t.Data = da.Checked; wh.Enabled = da.Checked; };
            wh.TextChanged += (_, _) => t.Where = wh.Text;
            if (missing) card.Tag = "Không thấy table này trong database App / Sys";
            card.Controls.AddRange(new Control[] { name, db, info, rm, st, da, wh });
            _tblBox.Controls.Add(card);
        }
        _tblBox.ResumeLayout(true);
    }

    private static void Fill(AutoCompleteStringCollection c, IEnumerable<JsonElement> items) { c.Clear(); c.AddRange(items.Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()); }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "genUpdate.init":
            {
                var d = Arg(a, 0);
                _ws.Text = P(d, "workspace"); _pProgram.Text = P(d, "program"); _pMobile.Text = P(d, "mobile"); _pSave.Text = P(d, "save");
                _folderPrefix = P(d, "folderPrefix"); if (_pFolder.Text.Length == 0) _pFolder.Text = NewFolderName();
                _cats = PA(d, "categories").Select(c => P(c, "value")).ToList();
                _cat.SetItems(_cats.Count > 0 ? PA(d, "categories").Select(c => P(c, "label")).ToList() : new List<string> { "(chưa có)" }, 0);
                _batch = PA(d, "batch").Select(b => new BatchItem { Key = P(b, "key"), Name = P(b, "name"), Rel = P(b, "rel"), Kind = P(b, "kind") }).ToList(); RenderBatch();
                if (d.TryGetProperty("tree", out var tr) && tr.ValueKind == JsonValueKind.Object) { _root = tr.Clone(); _treeTitle.Text = P(d, "treeTitle"); RebuildTree(); }
                else RebuildTree();
                break;
            }
            case "genUpdate.onTree":
                SetBusy(false); _root = a.Count > 0 && a[0].ValueKind == JsonValueKind.Object ? a[0].Clone() : default; _treeTitle.Text = Str(a, 1); _treeFilter.Text = ""; RebuildTree();
                break;
            case "genUpdate.onBatch":
                if (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array) { _batch = a[0].EnumerateArray().Select(b => new BatchItem { Key = P(b, "key"), Name = P(b, "name"), Rel = P(b, "rel"), Kind = P(b, "kind") }).ToList(); RenderBatch(); }
                if (Str(a, 1).Length > 0) Toast(Str(a, 1), "ok");
                break;
            case "genUpdate.onNames": if (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array) Fill(_nameSrc, a[0].EnumerateArray()); break;
            case "genUpdate.onProcs": if (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array) Fill(_procSrc, a[0].EnumerateArray()); break;
            case "genUpdate.onTableNames": if (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array) Fill(_tblSrc, a[0].EnumerateArray()); break;
            case "genUpdate.onTables":
            {
                SetBusy(false);
                var merge = Bool(a, 1);
                var list = (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>()).ToList();
                var old = _tbls.ToDictionary(t => t.Name.ToLowerInvariant());
                var next = merge ? _tbls.ToList() : new List<TblRow>();
                foreach (var t in list)
                {
                    var name = P(t, "name");
                    if (old.TryGetValue(name.ToLowerInvariant(), out var prev))
                    {
                        prev.InApp = PB(t, "inApp"); prev.InSys = PB(t, "inSys"); prev.RowsApp = P(t, "rowsApp"); prev.RowsSys = P(t, "rowsSys"); prev.IsView = PB(t, "isView");
                        if (!merge) next.Add(prev);
                        continue;
                    }
                    next.Add(new TblRow { Name = name, InApp = PB(t, "inApp"), InSys = PB(t, "inSys"), IsView = PB(t, "isView"), RowsApp = P(t, "rowsApp"), RowsSys = P(t, "rowsSys"), Sys = !PB(t, "inApp") && PB(t, "inSys"), Controllers = string.Join(", ", PA(t, "controllers").Select(x => x.GetString())) });
                }
                _tbls.Clear(); _tbls.AddRange(next); RenderTbls();
                if (!merge) Toast(_tbls.Count > 0 ? _tbls.Count + " table liên quan — tick Cấu trúc / Dữ liệu rồi bấm Gen Script." : "Không thấy table nào khai báo trong file của controller này.", _tbls.Count > 0 ? "ok" : "err");
                else if (list.Count > 0 && !PB(list[0], "inApp") && !PB(list[0], "inSys")) Toast("Không thấy table \"" + P(list[0], "name") + "\" trong App / Sys.", "err");
                break;
            }
            case "genUpdate.onCreated":
            {
                SetBusy(false); var r = Arg(a, 0);
                if (!PB(r, "ok")) { Toast(P(r, "message").Length > 0 ? P(r, "message") : "Không tạo được gói update.", "err"); break; }
                _lastPath = P(r, "path"); _result.Visible = true;
                _resTitle.Text = $"✔ Đã tạo {P(r, "count")} file tại:"; _resPath.Text = _lastPath; _pFolder.Text = NewFolderName();
                Toast($"Đã tạo gói update ({P(r, "count")} file).", "ok");
                break;
            }
            case "genUpdate.toast": Toast(Str(a, 0), Str(a, 1)); break;
            case "genUpdate.busy": SetBusy(Bool(a, 0), Str(a, 1)); break;
            case "genUpdate.setStatus": Toast(Str(a, 0), Str(a, 1)); break;
            case "genUpdate.onError": SetBusy(false); Toast(Str(a, 0), "err"); break;
        }
    }
}
