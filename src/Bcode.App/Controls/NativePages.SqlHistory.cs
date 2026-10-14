using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Lịch sử sửa (sqlhistory.html): chọn dự án / object, danh sách phiên bản bên trái, khung diff bên phải (so liền trước / liền sau / hiện tại), khôi phục, theo dõi ngoài Bcode.</summary>
internal sealed class SqlHistoryPage : DialogPage
{
    private sealed class Obj { public string Project = "", Database = "", Key = ""; public int Count; }
    private sealed class Ver { public string Id = "", SavedAt = "", Action = "", Machine = "", User = "", Ip = "", Server = "", Source = "", App = ""; public long Length; }

    private List<Obj> _objects = new();
    private (string Project, string Db, string Key) _current;
    private List<Obj> _objList = new();
    private List<string> _projects = new();
    private List<Ver> _versions = new();
    private string? _selId;
    private string? _currentText; private string _currentNote = "";
    private readonly Dictionary<string, string> _texts = new();
    private readonly HashSet<string> _requested = new();
    private List<int> _blocks = new(); private int _nav = -1;
    private bool _loading;

    private readonly DropDownButton _project = new() { Width = NativeSkin.S(200) }, _object = new() { Dock = DockStyle.Top }, _mode = new() { Width = NativeSkin.S(300) };
    private readonly RoundedInput _q = NK.Input("Lọc tên object...");
    private readonly Label _path = NK.Muted(""), _changes = NK.Muted(""), _hLeft = NK.Muted("Bản lúc —"), _hRight = NK.Muted("");
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly DiffView _diff = new() { Dock = DockStyle.Fill };
    private readonly Label _empty = NK.Muted("Chưa có lịch sử nào. Lịch sử được lưu mỗi lần chạy CREATE/ALTER PROCEDURE | FUNCTION | VIEW | TRIGGER trong SQL Query.", true);
    private TrackingDialog? _trk;

    public SqlHistoryPage()
    {
        _mode.SetItems(new[] { "Bản liền trước (so sánh liền kề)", "Bản liền sau", "Hiện tại (trong database)" }, 0);
        _list.ItemHeight = (int)(ThemeManager.BaseFont.Height * 3.3);
        _list.DrawItem += DrawVersion;
        _list.SelectedIndexChanged += (_, _) => { if (!_loading && _list.SelectedIndex >= 0 && _list.SelectedIndex < _versions.Count) { _selId = _versions[_list.SelectedIndex].Id; RenderDiff(); } };
        _project.SelectionChanged += _ => { if (!_loading) FillObjects(null); };
        _q.TextChanged += (_, _) => { if (!_loading) FillObjects(_objIdx >= 0 && _objIdx < _objList.Count ? _objList[_objIdx] : null); };
        _object.SelectionChanged += i => { if (_loading) return; _objIdx = i; LoadVersions(); };
        _mode.SelectionChanged += _ => RenderDiff();

        // trái
        var pick = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(8, 8, 8, 4) };
        _project.Dock = DockStyle.None; _object.Dock = DockStyle.None; _q.Dock = DockStyle.None;
        pick.Controls.AddRange(new Control[] { _project, _q, _object });
        pick.SizeChanged += (_, _) => { _q.Width = _object.Width = _project.Width = Math.Max(120, pick.ClientSize.Width - 16); };
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_list); left.Controls.Add(pick);

        // phải
        var bar = TopBar();
        bar.Controls.AddRange(new Control[] { NK.Text("So sánh với:"), _mode, _changes, NK.Button("▲ Khác biệt trước", () => Jump(false), tip: "Shift+Alt+F5"), NK.Button("▼ Khác biệt sau", () => Jump(true), tip: "Alt+F5") });
        var heads = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 2, Height = NativeSkin.RowHeight(this, 24), Padding = new Padding(10, 2, 10, 0) };
        heads.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); heads.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        heads.Controls.Add(_hLeft); heads.Controls.Add(_hRight);
        _empty.Dock = DockStyle.Fill; _empty.Padding = new Padding(20); _empty.Visible = false;
        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_diff); right.Controls.Add(_empty); right.Controls.Add(heads); right.Controls.Add(bar);
        _empty.BringToFront();

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        HandleCreated += (_, _) => BeginInvoke(() => { try { split.SplitterDistance = Math.Max(220, (int)(Width * 0.27)); } catch { } });

        var top = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 30), Padding = new Padding(12, 6, 12, 0) };
        _path.Dock = DockStyle.Fill; top.Controls.Add(_path);

        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("Khôi phục bản này", () => { var t = SelectedText(); if (t is not null) Post(new { action = "restore", data = new { text = t } }); }, primary: true, tip: "Đưa nội dung bản này vào editor SQL Query (chưa chạy)"));
        Bottom.Controls.Add(NK.Button("Copy bản này", () => { var t = SelectedText(); if (t is not null) { Post(new { action = "copy", data = new { text = t } }); Say("Đã copy.", "ok"); } }));
        Bottom.Controls.Add(NK.Button("Mở thư mục lịch sử", () => { var o = CurObj(); Post(new { action = "open-folder", data = new { project = o?.Project ?? "", db = o?.Database ?? "", key = o?.Key ?? "" } }); }));
        Bottom.Controls.Add(NK.Button("Theo dõi ngoài Bcode", OpenTracking, tip: "Ghi lại cả thay đổi do SSMS / công cụ khác (DDL trigger trong database)"));
        Bottom.Controls.Add(NK.Button("Đóng", () => Post(new { action = "close" })));
        Bottom.Controls.Add(NK.Muted("Alt+F5 / Shift+Alt+F5: nhảy tới khác biệt tiếp / trước đó"));
        Layout(top, split);
    }

    private int _objIdx = -1;
    private Obj? CurObj() => _objIdx >= 0 && _objIdx < _objList.Count ? _objList[_objIdx] : null;

    protected override bool HandleKey(Keys k)
    {
        if (k == (Keys.Alt | Keys.F5)) { Jump(true); return true; }
        if (k == (Keys.Alt | Keys.Shift | Keys.F5)) { Jump(false); return true; }
        if (k == Keys.Escape) { if (_trk is { IsDisposed: false }) _trk.Close(); else Post(new { action = "close" }); return true; }
        return false;
    }

    private string SelectedProject => _projects.Count > 0 && _project.SelectedIndex >= 0 ? _projects[_project.SelectedIndex] : "";

    private void FillProjects()
    {
        _loading = true;
        _projects = _objects.Select(o => o.Project).Distinct().ToList();
        if (_current.Project.Length > 0 && !_projects.Contains(_current.Project)) _projects.Insert(0, _current.Project);
        var idx = Math.Max(0, _projects.IndexOf(_current.Project));
        _project.SetItems(_projects.Count > 0 ? _projects : new List<string> { "" }, idx);
        _loading = false;
        FillObjects(null);
    }

    private void FillObjects(Obj? keep)
    {
        _loading = true;
        var q = _q.Text.Trim().ToLowerInvariant(); var proj = SelectedProject;
        _objList = _objects.Where(o => o.Project == proj && (q.Length == 0 || o.Key.ToLowerInvariant().Contains(q))).ToList();
        var want = keep ?? (_current.Project == proj ? _objList.FirstOrDefault(o => o.Database == _current.Db && o.Key == _current.Key) : null);
        _objIdx = want is not null ? _objList.IndexOf(want) : (_objList.Count > 0 ? 0 : -1);
        _object.SetItems(_objList.Count > 0 ? _objList.Select(o => $"[{o.Database}] {o.Key}  ({o.Count})").ToList() : new List<string> { "(không có object)" }, Math.Max(0, _objIdx));
        _empty.Visible = _objList.Count == 0; _diff.Visible = _objList.Count > 0;
        _loading = false;
        LoadVersions();
    }

    private void LoadVersions()
    {
        _versions = new(); _selId = null; _currentText = null; _texts.Clear(); _requested.Clear();
        var o = CurObj();
        _path.Text = o is null ? "" : $"{o.Project} › {o.Database} › {o.Key}";
        _loading = true; _list.Items.Clear(); _loading = false;
        if (o is null) { RenderDiff(); return; }
        Post(new { action = "versions", data = new { project = o.Project, db = o.Database, key = o.Key } });
        Post(new { action = "current", data = new { project = o.Project, db = o.Database, key = o.Key } });
    }

    private string? Text(string id)
    {
        if (_texts.TryGetValue(id, out var t)) return t;
        if (_requested.Add(id)) { var o = CurObj(); if (o is not null) Post(new { action = "load", data = new { id, project = o.Project, db = o.Database, key = o.Key } }); }
        return null;
    }

    private string? SelectedText() => _selId is null ? null : Text(_selId);

    private void RenderDiff()
    {
        var idx = _versions.FindIndex(v => v.Id == _selId);
        if (idx < 0) { _diff.Clear(); _hLeft.Text = "Bản lúc —"; _hRight.Text = ""; _changes.Text = ""; _blocks.Clear(); return; }
        var v = _versions[idx]; var mode = _mode.SelectedIndex;
        string? l, r; string ll, rl;
        if (mode == 0)
        {
            var older = idx + 1 < _versions.Count ? _versions[idx + 1] : null;
            var cur = Text(v.Id);
            if (older is not null) { l = Text(older.Id); r = cur; ll = "Bản lúc " + older.SavedAt; rl = "Bản lúc " + v.SavedAt; }
            else { l = cur; r = cur; ll = "Bản lúc " + v.SavedAt; rl = "Bản lúc " + v.SavedAt + " (bản cũ nhất — chưa có bản trước để so)"; }
        }
        else if (mode == 1)
        {
            var newer = idx > 0 ? _versions[idx - 1] : null;
            l = Text(v.Id); ll = "Bản lúc " + v.SavedAt;
            if (newer is not null) { r = Text(newer.Id); rl = "Bản lúc " + newer.SavedAt; } else { r = l; rl = "Bản lúc " + v.SavedAt + " (bản mới nhất)"; }
        }
        else
        {
            l = Text(v.Id); ll = "Bản lúc " + v.SavedAt;
            if (_currentText is not null) { r = _currentText; rl = "Hiện tại"; }
            else { r = _versions.Count > 0 ? Text(_versions[0].Id) : l; rl = "Bản mới nhất đã lưu" + (_currentNote.Length > 0 ? " (" + _currentNote + ")" : ""); }
        }
        _hLeft.Text = ll; _hRight.Text = rl;
        if (l is null || r is null) { _changes.Text = "Đang nạp..."; return; }   // chờ setVersion rồi vẽ lại
        var lines = new DiffService().Diff(l, r);
        _blocks = new List<int>(); var inBlock = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var ch = lines[i].Kind != DiffKind.Equal;
            if (ch && !inBlock) _blocks.Add(i);
            inBlock = ch;
        }
        _diff.Show(lines.Select(x => (x.Kind == DiffKind.Added ? "+" : x.Kind == DiffKind.Removed ? "-" : " ", (int?)(x.Kind == DiffKind.Removed ? x.LeftLineNo : x.RightLineNo ?? x.LeftLineNo), x.Text)));
        _nav = -1;
        _changes.Text = _blocks.Count + " khác biệt";
    }

    private void Jump(bool next)
    {
        if (_blocks.Count == 0) return;
        _nav = next ? (_nav + 1) % _blocks.Count : (_nav <= 0 ? _blocks.Count - 1 : _nav - 1);
        var line = Math.Min(_blocks[_nav], _diff.Lines.Count - 1);
        _diff.ReadOnly = false; _diff.Lines[line].Goto(); _diff.ReadOnly = true;
        _diff.ScrollCaret();
        _changes.Text = $"{_nav + 1}/{_blocks.Count} khác biệt";
    }

    private void DrawVersion(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _versions.Count) return;
        var v = _versions[e.Index]; var sel = (e.State & DrawItemState.Selected) != 0;
        using (var br = new SolidBrush(sel ? AppColors.Accent : AppColors.Panel)) e.Graphics.FillRectangle(br, e.Bounds);
        var fore = sel ? AppColors.OnAccent : AppColors.Text; var muted = sel ? AppColors.OnAccent : AppColors.TextMuted;
        var f = _list.Font; var lh = f.Height; var x = e.Bounds.X + 8; var w = e.Bounds.Width - 16;
        var badge = v.Action == "BASELINE" ? "bản gốc" : v.Action;
        if (v.Source == "external") badge += "  ·  ngoài Bcode";
        TextRenderer.DrawText(e.Graphics, v.SavedAt, f, new Rectangle(x, e.Bounds.Y + 2, w, lh), fore, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var bw = TextRenderer.MeasureText(badge, f).Width;
        TextRenderer.DrawText(e.Graphics, badge, f, new Rectangle(e.Bounds.Right - bw - 10, e.Bounds.Y + 2, bw + 4, lh), muted, TextFormatFlags.NoPrefix);
        var l2 = (v.Length / 1024.0).ToString("0.0") + " KB" + (v.Machine.Length > 0 ? " · " + v.Machine : "") + (v.User.Length > 0 ? " · " + v.User : "");
        TextRenderer.DrawText(e.Graphics, l2, f, new Rectangle(x, e.Bounds.Y + 2 + lh, w, lh), muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var l3 = (v.Ip.Length > 0 ? v.Ip + " · " : "") + (v.Source == "external" && v.App.Length > 0 ? v.App : v.Server);
        TextRenderer.DrawText(e.Graphics, l3, f, new Rectangle(x, e.Bounds.Y + 2 + lh * 2, w, lh), muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    // ---- theo dõi ngoài Bcode ----
    private void OpenTracking()
    {
        _trk = new TrackingDialog((a, w) => Post(new { action = a, data = new { which = w } }));
        _trk.FormClosed += (_, _) => { var o = CurObj(); if (o is not null) Post(new { action = "versions", data = new { project = o.Project, db = o.Database, key = o.Key } }); _trk = null; };
        Post(new { action = "tracking-status" });
        _trk.Show(FindForm());
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "window.init":
            {
                var s = Arg(a, 0);
                _objects = PA(s, "objects").Select(o => new Obj { Project = P(o, "project"), Database = P(o, "database"), Key = P(o, "key"), Count = PI(o, "count") }).ToList();
                var c = PO(s, "current"); _current = (P(c, "project"), P(c, "db"), P(c, "key"));
                FillProjects();
                break;
            }
            case "window.setVersions":
            {
                _versions = (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>())
                    .Select(v => new Ver { Id = P(v, "id"), SavedAt = P(v, "savedAt"), Action = P(v, "action"), Machine = P(v, "machine"), User = P(v, "user"), Ip = P(v, "ip"), Server = P(v, "server"), Source = P(v, "source"), App = P(v, "app"), Length = long.TryParse(P(v, "length"), out var n) ? n : 0 }).ToList();
                _loading = true; _list.Items.Clear(); foreach (var v in _versions) _list.Items.Add(v.SavedAt); _loading = false;
                if (_versions.Count > 0) { _selId = _versions[0].Id; _loading = true; _list.SelectedIndex = 0; _loading = false; }
                else _selId = null;
                RenderDiff();
                break;
            }
            case "window.setCurrent": _currentText = a.Count > 0 && a[0].ValueKind == JsonValueKind.String ? a[0].GetString() : null; _currentNote = Str(a, 1); RenderDiff(); break;
            case "window.setVersion": _texts[Str(a, 0)] = Str(a, 1); RenderDiff(); break;
            case "window.setStatus": Say(Str(a, 0), Str(a, 1)); _trk?.SetStatus(Str(a, 0)); break;
            case "window.setTracking": _trk?.SetState(Arg(a, 0)); break;
        }
    }

    /// <summary>Hộp "Theo dõi thay đổi ngoài Bcode": trạng thái DDL trigger từng database + Cài / Gỡ.</summary>
    private sealed class TrackingDialog : ThemedForm
    {
        private readonly FlowLayoutPanel _list = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(12) };
        private readonly Label _status = NK.Muted("", true);
        private readonly Action<string, string> _act;

        public TrackingDialog(Action<string, string> act)
        {
            _act = act;
            Text = "Theo dõi thay đổi ngoài Bcode"; Width = NativeSkin.S(760); Height = NativeSkin.S(560); StartPosition = FormStartPosition.CenterParent; ShowIcon = false; KeyPreview = true;
            var info = NK.Muted("Mặc định Bcode chỉ lưu lịch sử những lần sửa qua Bcode. Cài theo dõi để cả thay đổi do SSMS, tool deploy hay công cụ khác cũng được ghi lại (kèm login, tên máy, ứng dụng và IP). Bcode tạo trong database một bảng log và một DDL trigger (có TRY/CATCH, không bao giờ chặn câu lệnh của người khác). Cài riêng cho từng database của dự án; gỡ lúc nào cũng được, bảng log giữ nguyên.", true);
            info.Dock = DockStyle.Top; info.Padding = new Padding(12, 10, 12, 0);
            var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 7, 10, 7), FlowDirection = FlowDirection.RightToLeft };
            foot.Controls.Add(NK.Button("Đóng", Close, primary: true)); foot.Controls.Add(NK.Button("Cài cả hai", () => _act("tracking-install", "both"))); foot.Controls.Add(NK.Button("Làm mới", () => _act("tracking-status", "")));
            foot.Controls.Add(_status);
            Controls.Add(_list); Controls.Add(info); Controls.Add(foot);
            Resize += (_, _) => info.MaximumSize = new Size(ClientSize.Width - 24, 0);
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            SetState(default);
        }

        public void SetStatus(string t) => _status.Text = t;

        public void SetState(JsonElement st)
        {
            _list.Controls.Clear();
            if (st.ValueKind != JsonValueKind.Object) { _list.Controls.Add(NK.Muted("Đang kiểm tra...")); return; }
            string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            if (S(st, "note").Length > 0) { _list.Controls.Add(NK.Muted(S(st, "note"), true)); return; }
            _list.Controls.Add(NK.Muted("Dự án: " + S(st, "project") + " — server " + S(st, "server")));
            if (!st.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return;
            foreach (var it in items.EnumerateArray())
            {
                string stateText; Color col;
                var rows = it.TryGetProperty("rows", out var rv) && rv.ValueKind == JsonValueKind.Number ? rv.GetInt32() : 0;
                var trigger = B(it, "trigger"); var enabled = B(it, "enabled"); var table = B(it, "table");
                var canSee = !it.TryGetProperty("canSee", out var cs) || cs.ValueKind != JsonValueKind.False;
                if (S(it, "error").Length > 0) { stateText = "Không kiểm tra được: " + S(it, "error"); col = AppColors.Danger; }
                else if (trigger && enabled) { var found = S(it, "found"); stateText = "Đã cài và đang bật" + (found.Length > 0 && found != "bcode_trg_ddl_history" ? $" (trigger \"{found}\")" : "") + " — " + (rows < 0 ? "không đếm được sự kiện (tài khoản không có quyền đọc bảng log)" : rows + " sự kiện đã ghi"); col = AppColors.Success; }
                else if (trigger) { stateText = "Đã cài nhưng trigger đang bị tắt"; col = AppColors.Warning; }
                else if (!canSee) { stateText = "Chưa xác định: tài khoản đang dùng không có quyền xem trigger mức database nên không biết người khác đã cài chưa. Nếu đã cài thì không cần cài lại; bấm \"Cài\" sẽ ghi đè bản cũ."; col = AppColors.Danger; }
                else if (table) { stateText = $"Chưa bật (đã gỡ trigger, còn bảng log với {rows} sự kiện)"; col = AppColors.Warning; }
                else { stateText = "Chưa cài"; col = AppColors.Warning; }
                var card = new CardPanel(S(it, "label") + " — " + S(it, "database")) { Width = Math.Max(300, _list.ClientSize.Width - 30) };
                var d = new Label { Text = stateText, AutoSize = true, MaximumSize = new Size(card.Width - 30, 0), ForeColor = col, BackColor = Color.Transparent, Dock = DockStyle.Top };
                var acts = NK.Row(NK.Button(trigger ? "Cài lại" : "Cài", () => _act("tracking-install", S(it, "which"))));
                if (trigger) acts.Controls.Add(NK.Button("Gỡ", () => _act("tracking-remove", S(it, "which"))));
                acts.Dock = DockStyle.Top;
                card.Body.Controls.Add(acts); card.Body.Controls.Add(d);
                _list.Controls.Add(card);
            }
        }
    }
}
