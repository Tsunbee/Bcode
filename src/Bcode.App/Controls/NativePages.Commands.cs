using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>WCommand New/Edit (wcommandedit.html): các cột của bảng wcommand chia nhóm Định danh / Tên menu / Liên kết / Hệ thống / Bảng dữ liệu / Biểu tượng.</summary>
internal sealed class WCommandEditPage : DialogPage
{
    private static readonly string[] Fields = { "wmenuId", "wmenuId0", "menuId", "bar", "bar2", "link", "parameter", "iconUrl", "status", "icon", "sysId", "type", "sysCode", "msys", "target", "xtype", "edition", "explIcon" };
    private static readonly (string Code, string Name)[] Langs = { ("en", "English"), ("zh-CN", "中文 (giản thể)"), ("zh-TW", "中文 (phồn thể)"), ("ja", "日本語"), ("ko", "한국어"), ("th", "ไทย"), ("fr", "Français"), ("de", "Deutsch"), ("es", "Español"), ("id", "Indonesia"), ("ru", "Русский") };
    private static readonly Dictionary<string, int> MaxLen = new() { ["wmenuId"] = 8, ["wmenuId0"] = 8, ["menuId"] = 8, ["status"] = 1, ["target"] = 16, ["sysId"] = 64, ["sysCode"] = 3, ["type"] = 1, ["xtype"] = 8, ["edition"] = 1, ["icon"] = 50 };

    private readonly Dictionary<string, RoundedInput> _f = new();
    private readonly RoundedInput _tableM = NK.Input(""), _tableD = NK.Input("");
    private readonly Label _hintW = NK.Muted(""), _hintM = NK.Muted(""), _hintT = NK.Muted(""), _mode = new AccentLabel { AutoSize = true, BackColor = Color.Transparent };
    private readonly DropDownButton _lang = new() { Width = NativeSkin.S(150) };
    private readonly PillButton _sugW, _sugM, _tr, _save, _del;
    private readonly System.Windows.Forms.Timer _tblTimer = new() { Interval = 400 };
    private bool _isNew = true, _busy, _loading;

    public WCommandEditPage()
    {
        foreach (var k in Fields) { var i = NK.Input(""); if (MaxLen.TryGetValue(k, out var m)) i.Inner.MaxLength = m; _f[k] = i; i.TextChanged += (_, _) => { if (_loading) return; i.Inner.ForeColor = AppColors.Text; if (k == "wmenuId") _hintW.Text = ""; if (k == "menuId") _hintM.Text = ""; }; }
        _tableM.ReadOnly = _tableD.ReadOnly = true;
        _lang.SetItems(Langs.Select(l => l.Name).ToList(), 0);
        _sugW = NK.Button("Suggest", () => Post(new { action = "suggest-wmenu", data = Collect() }), tip: "Gợi ý WMenu Id chưa tồn tại theo menu cha");
        _sugM = NK.Button("Suggest", () => Post(new { action = "suggest-menu", data = Collect() }), tip: "Gợi ý Menu Id chưa có trong command/wcommand");
        _tr = NK.Button("🌐 Dịch", Translate, tip: "Dịch Bar (tiếng Việt) sang ngôn ngữ chọn bằng Google Dịch rồi điền vào Bar2");
        Control With(RoundedInput i, params Control[] extra) { var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Color.Transparent }; i.Width = NativeSkin.S(200); p.Controls.Add(i); p.Controls.AddRange(extra); return p; }
        Control Wide(RoundedInput i) => i;

        var c1 = new CardPanel("Định danh") { Dock = DockStyle.Top };
        var idPanelW = new Panel { Height = NativeSkin.RowHeight(this, 28) }; _sugW.Dock = DockStyle.Right; _f["wmenuId"].Dock = DockStyle.Fill; idPanelW.Controls.Add(_f["wmenuId"]); idPanelW.Controls.Add(_sugW);
        var idPanelM = new Panel { Height = NativeSkin.RowHeight(this, 28) }; _sugM.Dock = DockStyle.Right; _f["menuId"].Dock = DockStyle.Fill; idPanelM.Controls.Add(_f["menuId"]); idPanelM.Controls.Add(_sugM);
        c1.Body.Controls.Add(NK.FormGrid(180, ("WMenu Id", idPanelW), ("", _hintW), ("WMenu Id0 (menu cha)", _f["wmenuId0"]), ("Menu Id", idPanelM), ("", _hintM), ("Status", _f["status"])));
        var c2 = new CardPanel("Tên menu") { Dock = DockStyle.Top };
        var bar2 = new Panel { Height = NativeSkin.RowHeight(this, 28) };
        _tr.Dock = DockStyle.Right; _lang.Dock = DockStyle.Right; _f["bar2"].Dock = DockStyle.Fill; bar2.Controls.Add(_f["bar2"]); bar2.Controls.Add(_lang); bar2.Controls.Add(_tr);
        c2.Body.Controls.Add(NK.FormGrid(180, ("Bar (tên tiếng Việt)", _f["bar"]), ("Bar2 (English name)", bar2)));
        var c3 = new CardPanel("Liên kết") { Dock = DockStyle.Top };
        c3.Body.Controls.Add(NK.FormGrid(180, ("Link", _f["link"]), ("Parameter", _f["parameter"]), ("Target", _f["target"])));
        var c4 = new CardPanel("Hệ thống (bảng command)") { Dock = DockStyle.Top };
        c4.Body.Controls.Add(NK.FormGrid(180, ("Sysid", _f["sysId"]), ("Syscode", _f["sysCode"]), ("Type", _f["type"]), ("Msys", _f["msys"]), ("Xtype", _f["xtype"]), ("Edition", _f["edition"])));
        var c5 = new CardPanel("Bảng dữ liệu (đọc từ file Dir)") { Dock = DockStyle.Top };
        c5.Body.Controls.Add(NK.FormGrid(180, ("Bảng m (master)", _tableM), ("Bảng d (detail)", _tableD), ("", _hintT)));
        var c6 = new CardPanel("Biểu tượng") { Dock = DockStyle.Top };
        c6.Body.Controls.Add(NK.FormGrid(180, ("Icon", _f["icon"]), ("Expl Icon (0-255)", _f["explIcon"]), ("Icon Url", _f["iconUrl"])));

        var body = new Panel { AutoScroll = true, Padding = new Padding(14, 8, 14, 8) };
        var head = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent };
        head.Controls.Add(new Label { Text = "WCOMMAND", AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 3, FontStyle.Bold), Margin = new Padding(0, 0, 8, 6) }); head.Controls.Add(_mode);
        foreach (var c in new Control[] { c6, c5, c4, c3, c2, c1, head }) body.Controls.Add(c);   // Dock Top: thêm ngược

        _sysTimer();
        _save = NK.Button("Save", () => Post(new { action = "save", data = Collect() }), primary: true);
        _del = NK.Button("Delete", () => Post(new { action = "delete", data = Collect() }));
        _del.ForeOverride = AppColors.Danger;
        Bottom.FlowDirection = FlowDirection.LeftToRight;
        Bottom.Controls.Clear();
        Bottom.Controls.Add(_del); Bottom.Controls.Add(StatusLabel);
        var right = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right, BackColor = Color.Transparent };
        right.Controls.Add(NK.Button("Close", () => Post(new { action = "close" }))); right.Controls.Add(_save);
        Bottom.Controls.Add(right);
        Layout(null, body);
    }

    private void _sysTimer()
    {
        _tblTimer.Tick += (_, _) => { _tblTimer.Stop(); Post(new { action = "lookup-tables", data = new { sysId = _f["sysId"].Text } }); };
        _f["sysId"].TextChanged += (_, _) => { if (_loading) return; _tblTimer.Stop(); _tblTimer.Start(); };
        Disposed += (_, _) => _tblTimer.Dispose();
    }

    private Dictionary<string, string> Collect() => _f.ToDictionary(k => k.Key, k => k.Value.Text);

    private void Translate()
    {
        var text = _f["bar"].Text.Trim();
        if (text.Length == 0) { Say("Nhập Bar (tiếng Việt) trước rồi mới dịch.", "err"); _f["bar"].Inner.Focus(); return; }
        _tr.Enabled = false; Say("Đang dịch…");
        Post(new { action = "translate", data = new { text, lang = Langs[Math.Max(0, _lang.SelectedIndex)].Code } });
    }

    private void SetBusy(bool b) { _busy = b; _save.Enabled = _del.Enabled = _sugW.Enabled = _sugM.Enabled = !b; }

    protected override bool HandleKey(Keys k)
    {
        if (_busy) return false;
        if (k == Keys.Escape) { Post(new { action = "close" }); return true; }
        if (k == Keys.Enter && ActiveControl is not null) { Post(new { action = "save", data = Collect() }); return true; }
        return false;
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "window.init":
            {
                var s = Arg(a, 0);
                _isNew = PB(s, "isNew");
                _mode.Text = _isNew ? "New" : "Edit";
                _loading = true;
                var item = PO(s, "item");
                foreach (var k in Fields) _f[k].Text = P(item, k);
                _loading = false;
                _del.Visible = !_isNew; _sugW.Visible = _isNew;
                _f["bar"].Inner.Focus();
                break;
            }
            case "window.setValue": { var id = Str(a, 0); if (_f.TryGetValue(id, out var i)) { i.Text = Str(a, 1); i.Inner.Focus(); i.Inner.SelectAll(); } break; }
            case "window.setFieldValue": { if (_f.TryGetValue(Str(a, 0), out var i)) i.Text = Str(a, 1); break; }
            case "window.setHint":
            {
                var l = Str(a, 0) == "w" ? _hintW : _hintM; l.Text = Str(a, 1);
                l.ForeColor = Str(a, 2) switch { "ok" => AppColors.Success, "err" => AppColors.Danger, _ => AppColors.TextMuted };
                break;
            }
            case "window.setStatus": Say(Str(a, 0), Str(a, 1)); break;
            case "window.setBusy": SetBusy(Bool(a, 0)); break;
            case "window.setInvalid": { if (_f.TryGetValue(Str(a, 0), out var i)) { i.Inner.ForeColor = AppColors.Danger; i.Inner.Focus(); } break; }
            case "window.setTables": _tableM.Text = Str(a, 0); _tableD.Text = Str(a, 1); _hintT.Text = Str(a, 2); break;
            case "window.setTranslated":
                _tr.Enabled = true;
                if (a.Count > 1 && a[1].ValueKind == JsonValueKind.String) { Say(a[1].GetString() ?? "", "err"); break; }
                _f["bar2"].Text = Str(a, 0); _f["bar2"].Inner.ForeColor = AppColors.Text; Say("Đã dịch — kiểm tra lại Bar2 rồi Save.", "ok");
                break;
            case "document.querySelectorAll": foreach (var i in _f.Values) i.Inner.ForeColor = AppColors.Text; break;
            case "document.getElementById": if (_f.TryGetValue(Str(a, 0), out var fi)) fi.Inner.Focus(); break;
        }
    }

    public override void FocusPrimary() => _f["bar"].Inner.Focus();
}

/// <summary>COMMAND New/Edit (appcommandedit.html): form động theo cột bảng command + bảng Reports (Sys Data) theo Sysid.</summary>
internal sealed class AppCommandEditPage : DialogPage
{
    private sealed class Fld { public string Name = "", Kind = "String"; public bool Nullable; public RoundedInput Ctl = null!; public bool IsNull; public bool IsNullCapable() => Kind != "String" || Nullable; }

    private static readonly string[] Top = { "menu_id", "menu_id0", "bar", "bar2" };
    private readonly List<Fld> _fields = new();
    private readonly TableLayoutPanel _topGrid = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Padding = new Padding(0, 0, 0, 6) };
    private readonly TableLayoutPanel _restGrid = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4 };
    private readonly DataGridView _rep = NK.Grid();
    private readonly Label _repSys = NK.Muted(""), _repNote = NK.Muted(""), _mode = new AccentLabel { AutoSize = true, BackColor = Color.Transparent };
    private readonly PillButton _bSave, _bNew, _bDel, _bRefresh;
    private readonly System.Windows.Forms.Timer _repTimer = new() { Interval = 500 };
    private bool _isNew = true, _busy, _loading;

    public AppCommandEditPage()
    {
        foreach (var g in new[] { _topGrid, _restGrid })
        {
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170)); g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        }
        _bRefresh = NK.Button("Refresh Data", () => Post(new { action = "refresh" }), tip: "Nạp lại dòng này từ database");
        _bSave = NK.Button("Save", () => Post(new { action = "save", data = Collect() }), primary: true);
        _bNew = NK.Button("New", () => Post(new { action = "new", data = Collect() }), tip: "Làm menu mới (nhân bản dòng đang mở, bỏ menu_id)");
        _bDel = NK.Button("Delete", () => Post(new { action = "delete" })); _bDel.ForeOverride = AppColors.Danger;
        var tools = TopBar();
        tools.Controls.AddRange(new Control[] { _bRefresh, _bSave, _bNew, _bDel, NK.Button("Close", () => Post(new { action = "close" })), StatusLabel });
        Bottom.Visible = false;

        _rep.Visible = false; _rep.Dock = DockStyle.Top; _rep.Height = NativeSkin.S(220); _rep.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
        var title = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent };
        title.Controls.Add(new Label { Text = "COMMAND", AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 3, FontStyle.Bold), Margin = new Padding(0, 0, 8, 6) }); title.Controls.Add(_mode);
        var repHead = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Color.Transparent, Padding = new Padding(0, 10, 0, 0) };
        repHead.Controls.Add(new Label { Text = "Reports (Sys Data) theo Sysid", AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont, FontStyle.Bold) }); repHead.Controls.Add(_repSys);
        _repNote.Dock = DockStyle.Top;

        var body = new Panel { AutoScroll = true, Padding = new Padding(14, 8, 14, 8) };
        foreach (var c in new Control[] { _rep, _repNote, repHead, _restGrid, _topGrid, title }) body.Controls.Add(c);
        _repTimer.Tick += (_, _) => { _repTimer.Stop(); var s = _fields.FirstOrDefault(f => f.Name.Equals("sysid", StringComparison.OrdinalIgnoreCase)); Post(new { action = "reports", data = new { sysid = s?.Ctl.Text ?? "" } }); };
        Disposed += (_, _) => _repTimer.Dispose();
        Layout(tools, body);
    }

    private Dictionary<string, string?> Collect() => _fields.ToDictionary(f => f.Name, f => (string?)(f.IsNullCapable() && f.Ctl.Text.Length == 0 ? null : f.Ctl.Text));

    private void QueueReports(int ms = 500) { _repTimer.Stop(); _repTimer.Interval = Math.Max(1, ms); _repTimer.Start(); }
    private void SetBusy(bool b) { _busy = b; _bSave.Enabled = _bDel.Enabled = _bNew.Enabled = _bRefresh.Enabled = !b; }

    protected override bool HandleKey(Keys k)
    {
        if (_busy) return false;
        if (k == Keys.Escape) { Post(new { action = "close" }); return true; }
        if (k == (Keys.Control | Keys.S) || k == Keys.Enter) { Post(new { action = "save", data = Collect() }); return true; }
        return false;
    }

    private void AddField(TableLayoutPanel g, int slot, Fld f, string? value)
    {
        var lb = new MutedLabel { Text = f.Name + " (" + f.Kind + ")", AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 9, 6, 0), AutoEllipsis = true };
        var inp = NK.Input(""); inp.Dock = DockStyle.Fill; inp.Margin = new Padding(0, 3, 12, 3);
        f.IsNull = value is null;
        inp.Text = value ?? (f.Kind == "String" ? "" : "NULL");
        if (f.IsNull && f.Kind != "String") inp.Inner.ForeColor = AppColors.TextMuted;
        inp.Inner.Enter += (_, _) => { if (f.IsNull && inp.Text == "NULL") inp.Text = ""; };
        inp.Inner.Leave += (_, _) => { if (inp.Text.Length == 0 && f.Kind != "String" && f.IsNullCapable()) { inp.Text = "NULL"; f.IsNull = true; inp.Inner.ForeColor = AppColors.TextMuted; } };
        inp.TextChanged += (_, _) => { if (_loading) return; inp.Inner.ForeColor = AppColors.Text; if (f.Name.Equals("sysid", StringComparison.OrdinalIgnoreCase)) QueueReports(); };
        f.Ctl = inp;
        if (slot % 2 == 0) g.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        g.Controls.Add(lb); g.Controls.Add(inp);
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "window.init":
            {
                var st = Arg(a, 0);
                _isNew = PB(st, "isNew"); _mode.Text = _isNew ? "New" : "Edit";
                var cols = PA(st, "columns").Select(c => new Fld { Name = P(c, "name"), Kind = P(c, "kind"), Nullable = PB(c, "nullable") }).ToList();
                var values = PO(st, "values");
                string? Val(string n) => values.ValueKind == JsonValueKind.Object && values.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
                _loading = true;
                foreach (var g in new[] { _topGrid, _restGrid }) { g.SuspendLayout(); g.Controls.Clear(); g.RowStyles.Clear(); }
                _fields.Clear();
                var slot = 0;
                foreach (var n in Top) { var c = cols.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase)); if (c is null) continue; _fields.Add(c); AddField(_topGrid, slot++, c, Val(c.Name)); }
                slot = 0;
                foreach (var c in cols.Where(x => !Top.Contains(x.Name.ToLowerInvariant()))) { _fields.Add(c); AddField(_restGrid, slot++, c, Val(c.Name)); }
                foreach (var g in new[] { _topGrid, _restGrid }) g.ResumeLayout(true);
                _loading = false;
                _bDel.Visible = _bRefresh.Visible = !_isNew;
                var first = _fields.FirstOrDefault(f => f.Name == (_isNew ? "menu_id" : "bar")) ?? _fields.FirstOrDefault();
                first?.Ctl.Inner.Focus();
                QueueReports(1);
                break;
            }
            case "window.setReports":
            {
                var r = Arg(a, 0);
                _repNote.Text = P(r, "note");
                var columns = PA(r, "columns").Select(c => c.GetString() ?? "").ToList();
                var rows = PA(r, "rows").ToList();
                _rep.Visible = rows.Count > 0;
                if (rows.Count == 0) break;
                _rep.Columns.Clear(); _rep.Rows.Clear();
                foreach (var c in columns) _rep.Columns.Add(c, c);
                foreach (var row in rows) _rep.Rows.Add(row.EnumerateArray().Select(v => (object)(v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString())).ToArray());
                break;
            }
            case "window.setStatus": Say(Str(a, 0), Str(a, 1)); break;
            case "window.setBusy": SetBusy(Bool(a, 0)); break;
            case "window.setInvalid": { var f = _fields.FirstOrDefault(x => x.Name == Str(a, 0)); if (f is not null) { f.Ctl.Inner.ForeColor = AppColors.Danger; f.Ctl.Inner.Focus(); } break; }
        }
    }
}
