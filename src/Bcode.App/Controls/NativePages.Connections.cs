using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Connection Settings (connections.html): danh sách project bên trái, thẻ Kết nối / Project / Database bên phải, Test / Apply / Save & Close, FSG đồng bộ.</summary>
internal sealed class ConnectionsPage : DialogPage
{
    private static readonly string[] Text = { "name", "server", "user", "pass", "sysDb", "appDb", "id", "wlink", "programPath", "sourcePath", "mobilePath", "workingPath", "registry", "versionCode", "dbAccess" };

    private readonly Dictionary<string, RoundedInput> _f = new();
    private readonly CheckBox _integrated = NK.Check("Integrated Security (Windows Auth)");
    private readonly RoundedInput _filter = NK.Input("Lọc project...");
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly Panel _cards = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 6, 10, 6) };
    private readonly Label _emptyLabel = NK.Muted("Chưa có project nào — bấm \"+ New\".", true);
    private readonly CardPanel _syncCard;
    private readonly PillButton _bNew, _bDel, _bTest, _bApply, _bSave, _bSync, _bSyncMenu;
    private List<string> _names = new();
    private List<int> _view = new();
    private int _sel = -1;
    private bool _allowSync, _busy, _loading;

    public ConnectionsPage()
    {
        foreach (var k in Text) _f[k] = NK.Input(k == "sourcePath" ? @"UNC, dùng cho File Lookup — \\server\...\FBISP23" : k == "dbAccess" ? "SYS_S, APP_A, MADA_eInv" : k == "versionCode" ? "vd FBOR2SP22.6.4 hoặc 22.6.4" : "");
        _f["pass"].Inner.UseSystemPasswordChar = true;
        _integrated.CheckedChanged += (_, _) => ToggleAuth();

        var c1 = new CardPanel("Kết nối") { Dock = DockStyle.Top };
        c1.Body.Controls.Add(NK.FormGrid(150, ("Tên WS", _f["name"]), ("Server Name", _f["server"]), ("", _integrated), ("Login User", _f["user"]), ("Password", _f["pass"])));
        var c2 = new CardPanel("Project") { Dock = DockStyle.Top };
        c2.Body.Controls.Add(NK.FormGrid(150, ("ID", _f["id"]), ("Login WLink", _f["wlink"]), ("Program Path", _f["programPath"]), ("Source Path", _f["sourcePath"]), ("Mobile Path", _f["mobilePath"]), ("Working Path", _f["workingPath"]), ("Registry Name", _f["registry"])));
        var c3 = new CardPanel("Database") { Dock = DockStyle.Top };
        c3.Body.Controls.Add(NK.FormGrid(150, ("Sys Data", _f["sysDb"]), ("App Data", _f["appDb"]), ("DB Access", _f["dbAccess"]), ("Mã phiên bản", _f["versionCode"])));
        _syncCard = new CardPanel("FSG — Đồng bộ dự án") { Dock = DockStyle.Top, Visible = false };
        _bSync = NK.Button("⇅ Synchronize... (Ctrl+F5)", OpenSync); _bSyncMenu = NK.Button("☰ Sync menu", () => Send("sync-menu"));
        _syncCard.Body.Controls.Add(NK.Row(_bSync, _bSyncMenu, NK.Muted("Thêm dự án mới / ghi đè dự án đã có / chỉ 1 mã dự án — lấy từ danh mục dự án FSG.")));
        foreach (var c in new Control[] { _syncCard, c3, c2, c1 }) _cards.Controls.Add(c);   // Dock Top: thêm ngược
        _emptyLabel.Dock = DockStyle.Fill; _emptyLabel.Padding = new Padding(20); _emptyLabel.Visible = false;

        // trái
        _filter.Dock = DockStyle.Top;
        _bNew = NK.Button("+ New", () => Post(new { action = "new", data = new { from = _sel, item = Collect() } }));
        _bDel = NK.Button("Delete", () => { if (_sel >= 0) Post(new { action = "delete", data = new { index = _sel } }); }); _bDel.ForeOverride = AppColors.Danger;
        var sideActs = NK.Row(_bNew, _bDel); sideActs.Dock = DockStyle.Bottom; sideActs.Padding = new Padding(6, 4, 6, 4);
        var fHost = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 38), Padding = new Padding(6, 6, 6, 4) }; fHost.Controls.Add(_filter);
        var left = new Panel { Dock = DockStyle.Left, Width = NativeSkin.S(250) };
        left.Controls.Add(_list); left.Controls.Add(sideActs); left.Controls.Add(fHost);
        _list.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _list.SelectedIndex < 0 || _list.SelectedIndex >= _view.Count) return;
            var to = _view[_list.SelectedIndex];
            if (to == _sel || _busy) return;
            Post(new { action = "select", data = new { from = _sel, to, item = Collect() } });
        };
        _filter.TextChanged += (_, _) => RenderList();

        var main = new Panel();
        main.Controls.Add(_cards); main.Controls.Add(_emptyLabel); main.Controls.Add(left);

        _bTest = NK.Button("Test Connection", () => { Say("Đang kiểm tra kết nối..."); SetBusy(true); Send("test", false); });
        _bApply = NK.Button("Apply", () => Send("apply")); _bSave = NK.Button("Save & Close", () => Send("save"), primary: true);
        Bottom.FlowDirection = FlowDirection.LeftToRight;
        Bottom.Controls.Clear();
        Bottom.Controls.Add(StatusLabel);
        var right = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right, BackColor = Color.Transparent };
        right.Controls.Add(_bTest); right.Controls.Add(NK.Button("Đóng", () => Post(new { action = "close" }))); right.Controls.Add(_bApply); right.Controls.Add(_bSave);
        Bottom.Controls.Add(right);
        Layout(null, main);
    }

    private object Collect()
    {
        var o = _f.ToDictionary(k => k.Key, k => (object)k.Value.Text);
        o["integrated"] = _integrated.Checked;
        return o;
    }

    private void Send(string action, bool clearStatus = true) { if (clearStatus) Say(""); Post(new { action, data = new { index = _sel, item = Collect() } }); }
    private void ToggleAuth() { var on = _integrated.Checked; _f["user"].Enabled = !on; _f["pass"].Enabled = !on; }
    private void SetBusy(bool b) { _busy = b; _bTest.Enabled = _bApply.Enabled = _bSave.Enabled = _bSync.Enabled = _bSyncMenu.Enabled = _bNew.Enabled = _bDel.Enabled = !b; }

    private void Fill(JsonElement item)
    {
        _loading = true;
        foreach (var k in Text) _f[k].Text = P(item, k);
        _integrated.Checked = item.ValueKind != JsonValueKind.Object || !(item.TryGetProperty("integrated", out var v) && v.ValueKind == JsonValueKind.False);
        _loading = false;
        ToggleAuth();
    }

    private void RenderList()
    {
        _loading = true;
        var q = _filter.Text.Trim().ToLowerInvariant();
        _list.BeginUpdate(); _list.Items.Clear(); _view.Clear();
        for (var i = 0; i < _names.Count; i++)
        {
            if (q.Length > 0 && !_names[i].ToLowerInvariant().Contains(q) && i != _sel) continue;
            _view.Add(i); _list.Items.Add(_names[i].Length == 0 ? "(chưa đặt tên)" : _names[i]);
        }
        _list.EndUpdate();
        var at = _view.IndexOf(_sel); if (at >= 0) _list.SelectedIndex = at;
        var none = _sel < 0;
        _cards.Visible = !none; _emptyLabel.Visible = none;
        _loading = false;
    }

    private void OpenSync()
    {
        if (!_allowSync || _busy) return;
        using var dlg = new SyncDialog(_sel >= 0 ? _f["id"].Text.Trim() : "");
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        Post(new { action = "sync", data = new { index = _sel, item = Collect(), mode = dlg.Mode, code = dlg.Code } });
    }

    protected override bool HandleKey(Keys k)
    {
        if (k == (Keys.Control | Keys.F5)) { OpenSync(); return true; }
        if (k == Keys.Escape && !_busy) { Post(new { action = "close" }); return true; }
        return false;
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "window.init":
            {
                var s = Arg(a, 0);
                _allowSync = PB(s, "allowSync"); _syncCard.Visible = _allowSync;
                SetList(PA(s, "names").Select(n => n.GetString() ?? "").ToList(), PI(s, "sel", -1), PO(s, "item"));
                if (PB(s, "focusId")) _f["id"].Inner.Focus();
                break;
            }
            case "window.setList": SetList(a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray().Select(n => n.GetString() ?? "").ToList() : new List<string>(), Int(a, 1, -1), Arg(a, 2)); break;
            case "window.setStatus": Say(Str(a, 0), Str(a, 1)); break;
            case "window.setBusy": SetBusy(Bool(a, 0)); break;
            case "window.testDone": Say(Str(a, 0), Bool(a, 1) ? "ok" : "err"); SetBusy(false); break;
            case "window.openSync": OpenSync(); break;
            case "document.getElementById": if (_f.TryGetValue(Str(a, 0), out var i)) { i.Inner.Focus(); i.Inner.SelectAll(); } break;
        }
    }

    private void SetList(List<string> names, int sel, JsonElement item)
    {
        _names = names; _sel = sel; Fill(item); RenderList(); Say("");
    }

    /// <summary>Hộp "Synchronize": chọn chế độ đồng bộ dự án từ FSG.</summary>
    private sealed class SyncDialog : ThemedForm
    {
        private readonly RadioButton _add = new() { Text = "Thêm mới 1 mã dự án (ma_da)", AutoSize = true, Checked = true }, _new = new() { Text = "Chỉ sync dự án mới — chỉ thêm những project chưa có trong danh sách.", AutoSize = true }, _all = new() { Text = "Overwrite tất cả — project đã có thì cập nhật, chưa có thì thêm.", AutoSize = true };
        private readonly RoundedInput _code = NK.Input("Mã dự án");
        public string Mode => _add.Checked ? "AddOne" : _new.Checked ? "NewOnly" : "OverwriteAll";
        public string Code => _code.Text.Trim();

        public SyncDialog(string code)
        {
            Text = "Synchronize"; Width = NativeSkin.S(580); Height = NativeSkin.S(320); StartPosition = FormStartPosition.CenterParent; ShowIcon = false; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            _code.Text = code;
            // Xếp dọc bằng Dock (không dùng FlowLayoutPanel TopDown: nhãn tự xuống dòng ở đó không có bề rộng → thành cột hẹp, chữ tràn khỏi hộp).
            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 8) };
            foreach (var r in new[] { _add, _new, _all }) { r.AutoSize = false; r.Height = NativeSkin.RowHeight(this, 28); }
            _code.Dock = DockStyle.Left; _code.Width = NativeSkin.S(320);
            var codeRow = new Panel { Height = _code.NaturalHeight + NativeSkin.S(4), Padding = new Padding(NativeSkin.S(20), 0, 0, 0) };   // thụt vào thẳng chữ của nút chọn
            codeRow.Controls.Add(_code);
            NK.Stack(body, _add, codeRow, NK.Spacer(NativeSkin.S(10)), _new, _all, NK.Spacer(NativeSkin.S(12)),
                NK.Muted("Chế độ cập nhật chỉ ghi đè bằng những giá trị có; mật khẩu web, Mobile Path và cấu hình Profiler được giữ nguyên.", true));
            var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10, 7, 10, 7) };
            var ok = NK.Button("Đồng bộ", () => { if (_add.Checked && Code.Length == 0) { _code.Inner.Focus(); return; } DialogResult = DialogResult.OK; }, primary: true);
            foot.Controls.Add(ok); foot.Controls.Add(NK.Button("Hủy", () => DialogResult = DialogResult.Cancel));
            Controls.Add(body); Controls.Add(foot);
            _add.CheckedChanged += (_, _) => _code.Enabled = _add.Checked;
            AcceptButton = null; KeyPreview = true;
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.Handled = true; ok.PerformClick(); } else if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
            Shown += (_, _) => _code.Inner.Focus();
        }
    }

    public override void FocusPrimary() => _filter.Inner.Focus();
}
