using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Copy source standard (copysourcestandard.html): cây file có ô tick bên trái, bảng đổi tên bên phải.</summary>
internal sealed class CopySourceStandardPage : DialogPage
{
    private sealed class Row { public int Id; public string NewName = ""; }

    private readonly TreeView _tree = new() { CheckBoxes = true, HideSelection = false, Dock = DockStyle.Fill };
    private readonly DataGridView _grid;
    private readonly RoundedInput _batch = NK.Input("vd SVTranCustom — giữ đuôi file gốc");
    private readonly CheckBox _all = NK.Check("Check All");
    private readonly List<Row> _rows = new();
    private List<(string Dir, string Orig)> _files = new();
    private bool _busy;

    public CopySourceStandardPage()
    {
        var addBtn = NK.Button("Add", AddChecked, primary: true);
        var head = NK.Row(NK.Text("Source File"), _all, addBtn); head.Dock = DockStyle.Top;
        var left = new Panel { Dock = DockStyle.Left, Width = NativeSkin.S(360), Padding = new Padding(8) };
        left.Controls.Add(_tree); left.Controls.Add(head);

        var batchBtn = NK.Button("Áp dụng cho tất cả", ApplyBatch);
        _batch.Width = NativeSkin.S(280);
        var batchRow = NK.Row(NK.Muted("Đổi tên hàng loạt:"), _batch, batchBtn); batchRow.Dock = DockStyle.Top;
        _grid = NK.Grid(("dir", "Thư mục", 0), ("orig", "File gốc", 200), ("nn", "New Name", 220), ("x", "", 36));
        _grid.Columns["dir"]!.ReadOnly = true; _grid.Columns["orig"]!.ReadOnly = true; _grid.Columns["x"]!.ReadOnly = true;
        _grid.ReadOnly = false;
        _grid.CellValueChanged += (_, e) => { if (e.RowIndex >= 0 && e.RowIndex < _rows.Count && _grid.Columns[e.ColumnIndex].Name == "nn") _rows[e.RowIndex].NewName = (_grid.Rows[e.RowIndex].Cells["nn"].Value as string) ?? ""; };
        _grid.CellContentClick += (_, e) => { if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == "x") { _rows.RemoveAt(e.RowIndex); Render(); } };
        _grid.CellClick += (_, e) => { if (e.RowIndex >= 0 && _grid.Columns[e.ColumnIndex].Name == "x") { _rows.RemoveAt(e.RowIndex); Render(); } };
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var cap = NK.Muted("File sẽ clone (sửa New Name rồi bấm Clone):"); cap.Dock = DockStyle.Top;
        right.Controls.Add(_grid); right.Controls.Add(batchRow); right.Controls.Add(cap);

        var main = new Panel();
        main.Controls.Add(right); main.Controls.Add(left);

        _tree.AfterCheck += (_, e) =>
        {
            if (_busy || e.Action == TreeViewAction.Unknown) return;
            _busy = true; SetChildren(e.Node!, e.Node!.Checked); _busy = false;
        };
        _all.CheckedChanged += (_, _) => { _busy = true; foreach (TreeNode n in _tree.Nodes) { n.Checked = _all.Checked; SetChildren(n, _all.Checked); } _busy = false; };

        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("Đóng", () => Post(new { action = "close" })));
        Bottom.Controls.Add(NK.Button("Clone", () => { _grid.EndEdit(); Post(new { action = "clone", rows = _rows.Select(r => new { id = r.Id, newName = r.NewName }).ToList() }); }, primary: true));
        Layout(null, main);
    }

    private static void SetChildren(TreeNode n, bool v) { foreach (TreeNode c in n.Nodes) { c.Checked = v; SetChildren(c, v); } }

    private TreeNode Build(JsonElement n)
    {
        var isDir = PB(n, "dir");
        var node = new TreeNode(P(n, "name")) { Tag = n.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt32() : -1 };
        if (isDir) node.NodeFont = new Font(ThemeManager.BaseFont, FontStyle.Bold);
        foreach (var c in PA(n, "children")) node.Nodes.Add(Build(c));
        return node;
    }

    private void AddChecked()
    {
        void Walk(TreeNodeCollection ns)
        {
            foreach (TreeNode n in ns)
            {
                if (n.Tag is int id && id >= 0 && n.Checked && _rows.All(r => r.Id != id))
                {
                    var orig = _files[id].Orig; var dot = orig.LastIndexOf('.');
                    _rows.Add(new Row { Id = id, NewName = (dot > 0 ? orig[..dot] : orig) + "_Copy" + (dot > 0 ? orig[dot..] : "") });
                }
                Walk(n.Nodes);
            }
        }
        Walk(_tree.Nodes); Render();
    }

    private void ApplyBatch()
    {
        var b = _batch.Text.Trim();
        if (b.Length == 0) { Say("Nhập tên chung trước khi áp dụng.", "err"); return; }
        if (_rows.Count == 0) { Say("Chưa có file nào — tick chọn ở Source File rồi bấm Add.", "err"); return; }
        foreach (var r in _rows) { var o = _files[r.Id].Orig; var dot = o.LastIndexOf('.'); r.NewName = b + (dot > 0 ? o[dot..] : ""); }
        Render();
    }

    private void Render()
    {
        _grid.Rows.Clear();
        foreach (var r in _rows) _grid.Rows.Add(_files[r.Id].Dir, _files[r.Id].Orig, r.NewName, "✕");
    }

    protected override bool HandleKey(Keys key) { if (key == Keys.Escape && !_grid.IsCurrentCellInEditMode) { Post(new { action = "close" }); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "copyStd.init":
            {
                var d = Arg(a, 0);
                _files = PA(d, "files").Select(f => (P(f, "dir"), P(f, "orig"))).ToList();
                _tree.BeginUpdate(); _tree.Nodes.Clear();
                var root = Build(PO(d, "tree"));
                _tree.Nodes.Add(root); root.Expand();
                _tree.EndUpdate();
                Say(_files.Count + " file.");
                break;
            }
            case "copyStd.onStatus": Say(Str(a, 0), Str(a, 1)); break;
        }
    }
}

/// <summary>Key bản quyền (licensekey.html).</summary>
internal sealed class LicenseKeyPage : DialogPage
{
    private readonly Label _state = new() { AutoSize = false, Height = NativeSkin.S(34), Dock = DockStyle.Top, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 0, 0) };
    private readonly Label _reason = new() { AutoSize = false, Height = 0, Dock = DockStyle.Top, ForeColor = AppColors.Danger, Padding = new Padding(10, 4, 0, 0) };
    private readonly RoundedInput _machine = NK.Input("", mono: true), _key = NK.Memo(3, "Dán key vào đây...");
    private readonly PillButton _bSave, _bClear, _bPaste;

    public LicenseKeyPage()
    {
        _machine.ReadOnly = true;
        var body = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(14, 8, 14, 8) };
        var h = new Label { Text = "Key bản quyền", AutoSize = true, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 3, FontStyle.Bold), BackColor = Color.Transparent };
        var feats = NK.Muted("Key mở khoá: Decrypt SQL Object (giải mã object WITH ENCRYPTION) · Create RPT & XML (Excel / Pivot Excel mẫu + header report XML) · Excel → FRX (Excel mẫu in → FastReport .frx) · Tạo báo cáo · Báo cáo yêu cầu FSG · Thiết kế màn hình · Khai báo API · Tạo cấu trúc API · Tạo nhanh danh mục · Clone danh mục.", true);
        var s1 = NK.Muted("Bước 1. Gửi Mã máy này cho người cấp key (mỗi máy một mã riêng):", true);
        _bPaste = NK.Button("Dán từ clipboard", () => Post(new { action = "paste" })); _bSave = NK.Button("Xác nhận", SaveKey, primary: true); _bClear = NK.Button("Xoá key đã lưu", () => Post(new { action = "clear" }));
        var s2 = NK.Muted("Bước 2. Dán key nhận lại rồi bấm Xác nhận. Chỉ cần dán một lần: key được nhớ lại. Key chỉ dùng được trên máy đã gửi mã.", true);
        var copyRow = NK.Row(_machine, NK.Button("Copy mã máy", () => { Post(new { action = "copy", text = _machine.Text }); Say("Đã copy mã máy.", "ok"); }));
        _machine.Width = NativeSkin.S(380);
        foreach (var c in new Control[] { h, feats, s1, copyRow, s2, _key, NK.Row(_bPaste, _bSave, _bClear) }) body.Controls.Add(c);
        Resize += (_, _) => { var w = Math.Max(200, body.ClientSize.Width - 40); feats.MaximumSize = s1.MaximumSize = s2.MaximumSize = new Size(w, 0); _key.Width = w; };
        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("Đóng", () => Post(new { action = "close" })));
        var top = new Panel { Dock = DockStyle.Top, AutoSize = true };
        top.Controls.Add(_state); top.Controls.Add(_reason);
        Layout(top, body);
    }

    private void SaveKey()
    {
        if (_key.Text.Trim().Length == 0) { Say("Chưa dán key.", "err"); return; }
        Busy(true); Say("Đang kiểm tra...");
        Post(new { action = "save", key = _key.Text });
    }

    private void Busy(bool b) { _bSave.Enabled = _bClear.Enabled = _bPaste.Enabled = !b; }
    protected override bool HandleKey(Keys k) { if (k == Keys.Escape) { Post(new { action = "close" }); return true; } if (k == (Keys.Control | Keys.Enter)) { SaveKey(); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "licenseKey.init":
            {
                var o = Arg(a, 0);
                Busy(false);
                var unlocked = PB(o, "unlocked"); var hasKey = PB(o, "hasKey");
                _state.Text = unlocked ? "✓ Đã kích hoạt trên máy này." : hasKey ? "✗ Key đã lưu không còn hợp lệ trên máy này — gửi lại Mã máy để xin key mới. Các tính năng đang bị khoá." : "✗ Chưa kích hoạt — các tính năng đang bị khoá.";
                _state.ForeColor = unlocked ? AppColors.Success : AppColors.Accent;
                _machine.Text = P(o, "machine");
                var reason = P(o, "reason"); _reason.Text = reason; _reason.Height = reason.Length > 0 ? 42 : 0;
                if (unlocked) _key.Text = "";
                var msg = P(o, "message"); if (msg.Length > 0) Say(msg, PB(o, "ok") ? "ok" : "err");
                _bClear.Enabled = hasKey;
                break;
            }
            case "licenseKey.onPaste": { var t = Str(a, 0).Trim(); _key.Text = t; Say(t.Length > 0 ? "" : "Clipboard không có chữ.", t.Length > 0 ? null : "err"); break; }
            case "licenseKey.onError": Busy(false); Say(Str(a, 0), "err"); break;
        }
    }
}
