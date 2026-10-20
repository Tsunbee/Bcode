using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Thanh công cụ của File Lookup (filelookupbar.html): Path/Load · đuôi/Only Show/SearchBox/lọc tên · Copy to/Tick/Cấp source.</summary>
internal sealed class FileLookupBarPage : NativePage
{
    private readonly RoundedInput _path = NK.Input(@"\\server\CustomerPro\...\App_Data"), _search = NK.Input("Search...");
    private readonly DropDownButton _ext = new() { Width = NativeSkin.S(130) };
    private readonly PillButton _only, _toggleSb, _copyTo;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 200 };
    private static readonly string[] Exts = { ".*", ".f", ".xml", ".aspx", ".xlsx", ".rpt" };
    private bool _loading;

    public FileLookupBarPage()
    {
        _ext.SetItems(new[] { ".* (tất cả)", ".f", ".xml", ".aspx", ".xlsx", ".rpt" }, 0);
        _ext.SelectionChanged += i => { if (!_loading) Post(new { action = "ext", value = Exts[Math.Max(0, i)] }); };
        _only = NK.Button("Only Show .*", () => Post(new { action = "toggle-only-show" })); _only.IsPrimary = true;
        _toggleSb = NK.Button("SearchBox ▾", () => Post(new { action = "toggle-searchbox" }));
        _copyTo = NK.Button("Copy to (0)", () => Post(new { action = "copy-to" }), tip: "Copy các file đã tick (hoặc file đang chọn) sang thư mục khác, có đổi tên");
        _path.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Load(); } };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Post(new { action = "search", value = _search.Text }); };
        _search.TextChanged += (_, _) => { if (_loading) return; _debounce.Stop(); _debounce.Start(); };
        Disposed += (_, _) => _debounce.Dispose();

        var pathRow = NK.InputWithButton(_path, NK.Button("Load", Load, primary: true)); pathRow.Dock = DockStyle.Fill;
        var row1 = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 42), Padding = new Padding(10, 6, 10, 4) };
        var lbl = NK.Muted("Path:"); lbl.Dock = DockStyle.Left; lbl.AutoSize = false; lbl.Width = NativeSkin.S(44); lbl.TextAlign = ContentAlignment.MiddleLeft;
        row1.Controls.Add(pathRow); row1.Controls.Add(lbl);
        // Mỗi hàng là lưới chia cột theo tỉ lệ (NK.GridRow): nút luôn nằm trên 1 hàng, rộng đều, co theo bề ngang cột — không còn rớt từng nút xuống dòng.
        var row2 = Padded(NK.GridRow(6, (_ext, 38), (_only, 31), (_toggleSb, 31)), 3, 3);
        var rowS = Padded(NK.GridRow(6, (_search, 100)), 3, 3);
        var row3 = Padded(NK.GridRow(6,
            (_copyTo, 28),
            (NK.Button("Tick hết", () => Post(new { action = "check-all", value = true }), tip: "Tick mọi file đang hiện trên cây"), 22),
            (NK.Button("Bỏ tick", () => Post(new { action = "check-all", value = false }), tip: "Bỏ mọi dấu tick"), 22),
            (NK.Button("Cấp source", () => Post(new { action = "grant-source" }), tip: "Cấp source (.xml) từ kho SourceCollection cho các file đã tick (hoặc file đang chọn), tự chọn theo phiên bản"), 28)), 3, 8);
        Controls.Add(row3); Controls.Add(rowS); Controls.Add(row2); Controls.Add(row1);
    }

    private void Load() => Post(new { action = "load", path = _path.Text });

    /// <summary>Bọc 1 hàng: lề trái/phải 10px như hàng Path, lề trên/dưới theo tham số — các hàng cách đều nhau.</summary>
    private static Panel Padded(Control row, int top, int bottom)
    {
        var p = new Panel { Dock = DockStyle.Top, Padding = new Padding(10, top, 10, bottom), BackColor = Color.Transparent };
        row.Dock = DockStyle.Fill;
        p.Height = row.Height + top + bottom;
        p.Controls.Add(row);
        return p;
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "window.setCheckedCount": _copyTo.Text = $"Copy to ({Str(a, 0)})"; break;
            case "window.setOnlyShowLabel": _only.Text = Str(a, 0); break;
            case "window.setOnlyShowChecked": _only.IsPrimary = Bool(a, 0); _only.Invalidate(); break;
            case "window.setSearchBoxToggleText": _toggleSb.Text = Str(a, 0); break;
            case "window.setPath": _loading = true; _path.Text = Str(a, 0); _loading = false; break;
        }
    }

    public override void FocusPrimary() => _path.Inner.Focus();
}

/// <summary>Search Box của File Lookup (searchbox.html): tìm theo NỘI DUNG nhiều file — File Type / Search in / String search / Match Case / Show Pattern.</summary>
internal sealed class FileLookupSearchBoxPage : NativePage
{
    private readonly RoundedInput _type = NK.Input("*.*"), _in = NK.Input(@"\\server\CustomerPro\...\App_Data"), _text = NK.Input("");
    private readonly CheckBox _case = NK.Check("Match Case"), _pattern = NK.Check("Show Pattern");
    private readonly PillButton _go;
    private bool _running, _loading;

    public FileLookupSearchBoxPage()
    {
        _type.Text = "*.*";
        _go = NK.Button("Search", GoOrCancel, primary: true);
        var browse = NK.Button("...", () => { PushState(); Post(new { action = "browse" }); }, tip: "Chọn thư mục"); browse.Dock = DockStyle.Right;
        _in.Dock = DockStyle.Fill;
        var inRow = new Panel { Height = NativeSkin.RowHeight(this, 28) }; inRow.Controls.Add(_in); inRow.Controls.Add(browse);
        var optRow = new BarFlow { AutoSize = true }; optRow.Controls.AddRange(new Control[] { _case, _pattern, _go });
        var title = NK.Muted("Search Box"); title.Dock = DockStyle.Top; title.Padding = new Padding(10, 6, 10, 0);
        var grid = NK.FormGrid(100, ("File Type", _type), ("Search in", inRow), ("String search", _text), ("", optRow));
        grid.Dock = DockStyle.Top; grid.Padding = new Padding(8, 0, 8, 0);
        Controls.Add(grid); Controls.Add(title);
        foreach (var c in new[] { _type, _in, _text }) c.TextChanged += (_, _) => { if (!_loading) PushState(); };
        _case.CheckedChanged += (_, _) => { if (!_loading) PushState(); }; _pattern.CheckedChanged += (_, _) => { if (!_loading) PushState(); };
        foreach (var c in new[] { _type, _in, _text }) c.InputKeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Post(State("search")); } };
    }

    private object State(string action) => new { action, fileType = _type.Text, searchIn = _in.Text, text = _text.Text, matchCase = _case.Checked, showPattern = _pattern.Checked };
    private void PushState() => Post(State("state"));
    private void GoOrCancel() { if (_running) Post(new { action = "cancel" }); else Post(State("search")); }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _running) { Post(new { action = "cancel" }); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "window.setSearchIn": _loading = true; _in.Text = Str(a, 0); _loading = false; PushState(); break;
            case "window.setRunning": _running = Bool(a, 0); _go.Text = _running ? "Cancel" : "Search"; break;
            case "window.focusText": _text.Inner.Focus(); _text.Inner.SelectAll(); break;
        }
    }

    public override void FocusPrimary() => _text.Inner.Focus();
}

/// <summary>Thanh trên khung xem file của File Lookup (filelookuppreview.html): đường dẫn, ngày sửa, breadcrumb + nút "Edit in BcodeViewer".</summary>
internal sealed class FileLookupPreviewBarPage : NativePage
{
    private readonly Label _path = new() { Text = "(chưa chọn file nào)", AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent };
    private readonly Label _mod = NK.Muted(""), _bread = NK.Muted("");
    private readonly PillButton _edit, _grantOne;
    // Source sắp cấp cho file .f đang chọn (window.setSource) — bấm nhãn = xem file trong kho, nút = cấp nhanh.
    private readonly Label _src = new() { AutoSize = false, AutoEllipsis = true, BackColor = Color.Transparent, UseMnemonic = false, Margin = new Padding(6, 9, 6, 3), Cursor = Cursors.Hand };
    private readonly ToolTip _tip = new();
    private bool _srcViewable;

    public FileLookupPreviewBarPage()
    {
        _edit = NK.Button("Edit in BcodeViewer", () => Post(new { action = "edit" }), primary: true); _edit.Enabled = false; _edit.Dock = DockStyle.Right;
        _grantOne = NK.Button("Cấp source", () => Post(new { action = "grant-one" }), tip: "Cấp file source này vào dự án (hỏi xác nhận trước khi ghi)"); _grantOne.Visible = false;
        _src.Click += (_, _) => { if (_srcViewable) Post(new { action = "view-source" }); };
        Disposed += (_, _) => _tip.Dispose();
        var row1 = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 30), Padding = new Padding(8, 3, 8, 0) };
        row1.Controls.Add(_path); row1.Controls.Add(_edit);
        var row2 = new BarFlow { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 0, 8, 2) };
        row2.Controls.Add(_mod); row2.Controls.Add(_src); row2.Controls.Add(_grantOne); row2.Controls.Add(_bread);
        Controls.Add(row2); Controls.Add(row1);
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path == "window.setSource")
        {
            var s = Arg(a, 0);
            var on = s.ValueKind == JsonValueKind.Object;
            var kind = on ? P(s, "kind") : "";
            _src.Text = !on ? "" : (P(s, "version").Length > 0 ? "Source " + P(s, "version") + " " : "") + P(s, "text");
            // 1 dòng: rộng theo chữ, tối đa ~620px (dài hơn thì "…", đường dẫn đủ ở tooltip).
            _src.Size = new Size(Math.Min(TextRenderer.MeasureText(_src.Text, _src.Font).Width + 4, NativeSkin.S(620)), _mod.PreferredHeight);
            _src.ForeColor = kind switch { "ok" => AppColors.Success, "fb" => AppColors.Warning, _ => AppColors.TextMuted };
            _srcViewable = on && kind is "ok" or "fb";
            _src.Cursor = _srcViewable ? Cursors.Hand : Cursors.Default;
            _tip.SetToolTip(_src, on ? P(s, "tip") : "");
            _grantOne.Visible = on; _grantOne.Enabled = on && PB(s, "can");
            return;
        }
        if (path != "window.setPreview") return;
        _path.Text = Str(a, 0); _mod.Text = Str(a, 1); _bread.Text = Str(a, 2); _edit.Enabled = Bool(a, 3);
    }
}
