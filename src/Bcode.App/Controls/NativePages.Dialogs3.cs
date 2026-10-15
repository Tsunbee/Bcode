using System.Text.Json;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Cầu nối cho các form dùng WebView2 trực tiếp: ở chế độ App gắn <see cref="WebBarHost"/> (trang native) vào form và nối thông điệp / sự kiện sẵn sàng.</summary>
internal static class NativeForm
{
    public static WebBarHost Attach(Control form, string page, Action<JsonElement> onMessage, Action onReady)
    {
        var host = new WebBarHost(page) { Dock = DockStyle.Fill };
        form.Controls.Add(host);
        host.Message += root => onMessage(root.Clone());
        host.Ready += onReady;
        return host;
    }
}

/// <summary>Edit Project (editproject.html): kết nối SQL Server, database, dự án, đường dẫn.</summary>
internal sealed class EditProjectPage : DialogPage
{
    private static readonly string[] Fields = { "server", "user", "pass", "sysDb", "appDb", "id", "wlink", "programPath", "sourcePath", "mobilePath", "workingPath", "registry" };
    private readonly Dictionary<string, RoundedInput> _f = new();
    private readonly RoundedInput _dbAccess = NK.Input("");
    private readonly Label _title = new() { AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 3, FontStyle.Bold), Padding = new Padding(0, 0, 0, 4) };
    private readonly PillButton _test;

    public EditProjectPage()
    {
        foreach (var k in Fields) _f[k] = NK.Input("");
        _f["pass"].Inner.UseSystemPasswordChar = true;
        _dbAccess.ReadOnly = true;
        var show = NK.Button("Hiện", () => { }, tip: "Hiện/ẩn mật khẩu");
        show.Click += (_, _) => { var on = !_f["pass"].Inner.UseSystemPasswordChar; _f["pass"].Inner.UseSystemPasswordChar = on; show.Text = on ? "Hiện" : "Ẩn"; };
        var passRow = new Panel { Height = NativeSkin.RowHeight(this, 28), Dock = DockStyle.Fill };
        show.Dock = DockStyle.Right; _f["pass"].Dock = DockStyle.Fill; passRow.Controls.Add(_f["pass"]); passRow.Controls.Add(show);

        var c1 = new CardPanel("Kết nối SQL Server") { Dock = DockStyle.Top };
        c1.Body.Controls.Add(NK.FormGrid(150, ("Server Name", _f["server"]), ("Login User", _f["user"]), ("Password", passRow)));
        var c2 = new CardPanel("Database") { Dock = DockStyle.Top };
        c2.Body.Controls.Add(NK.FormGrid(150, ("Sys Data", _f["sysDb"]), ("App Data", _f["appDb"]), ("DB Access (tự ghép)", _dbAccess)));
        var c3 = new CardPanel("Dự án") { Dock = DockStyle.Top };
        c3.Body.Controls.Add(NK.FormGrid(150, ("ID", _f["id"]), ("Login WLink", _f["wlink"])));
        var c4 = new CardPanel("Đường dẫn") { Dock = DockStyle.Top };
        c4.Body.Controls.Add(NK.FormGrid(150, ("Program Path", _f["programPath"]), ("Source Path", _f["sourcePath"]), ("Mobile Path", _f["mobilePath"]), ("Working Path", _f["workingPath"]), ("Registry Name", _f["registry"])));

        void UpdateAccess() => _dbAccess.Text = _f["sysDb"].Text.Trim() + ", " + _f["appDb"].Text.Trim();
        _f["sysDb"].TextChanged += (_, _) => UpdateAccess(); _f["appDb"].TextChanged += (_, _) => UpdateAccess();

        var body = new Panel { AutoScroll = true, Padding = new Padding(14, 10, 14, 8) };
        _title.Dock = DockStyle.Top;
        // Dock Top: điều khiển thêm SAU nằm TRÊN → thêm theo thứ tự ngược
        body.Controls.Add(c4); body.Controls.Add(c3); body.Controls.Add(c2); body.Controls.Add(c1); body.Controls.Add(_title);

        _test = NK.Button("Test Connection", () => { Say("Đang kiểm tra..."); _test!.Enabled = false; Post(new { action = "test", data = Collect() }); });
        Bottom.FlowDirection = FlowDirection.LeftToRight;
        Bottom.Controls.Clear();
        Bottom.Controls.Add(_test);
        Bottom.Controls.Add(StatusLabel);
        var right = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Right, FlowDirection = FlowDirection.LeftToRight, BackColor = Color.Transparent };
        right.Controls.Add(NK.Button("Cancel", () => Post(new { action = "cancel" }))); right.Controls.Add(NK.Button("OK", () => Post(new { action = "ok", data = Collect() }), primary: true));
        Bottom.Controls.Add(right);
        Layout(null, body);
    }

    private Dictionary<string, string> Collect() => _f.ToDictionary(k => k.Key, k => k.Value.Text);

    protected override bool HandleKey(Keys k)
    {
        if (k == Keys.Escape) { Post(new { action = "cancel" }); return true; }
        if (k == Keys.Enter) { Post(new { action = "ok", data = Collect() }); return true; }
        return false;
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        switch (path)
        {
            case "window.init":
            {
                var s = Arg(a, 0);
                _title.Text = P(s, "title");
                var item = PO(s, "item");
                foreach (var k in Fields) _f[k].Text = P(item, k);
                _f["server"].Inner.Focus(); _f["server"].Inner.SelectAll();
                break;
            }
            case "window.setStatus": Say(Str(a, 0), Str(a, 1)); break;
            case "window.testDone": Say(Str(a, 0), Bool(a, 1) ? "ok" : "err"); _test.Enabled = true; break;
        }
    }

    public override void FocusPrimary() { _f["server"].Inner.Focus(); }
}

/// <summary>Lịch sử gợi ý AI (aihistory.html).</summary>
internal sealed class AiHistoryPage : DialogPage
{
    private sealed class Entry { public string Id = "", Kind = "", Engine = "", Time = "", Instruction = "", Context = "", Result = ""; }

    private readonly RoundedInput _q = NK.Input("Lọc theo nội dung gợi ý / yêu cầu...");
    private readonly Label _count = NK.Muted("");
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly Label _meta = NK.Muted("");
    private readonly Label _insL = NK.Muted("Yêu cầu (Ctrl+I)"), _ctxL = NK.Muted("Dòng đang gõ lúc đó"), _resL = NK.Muted("Đoạn AI đã gợi ý");
    private readonly RoundedInput _ins = NK.Memo(3, ""), _ctx = NK.Memo(3, ""), _res = NK.Memo(10, "");
    private readonly PillButton _bCopy, _bInsert, _bDel;
    private List<Entry> _all = new(), _shown = new();
    private Entry? _sel;
    private bool _fill;

    public AiHistoryPage()
    {
        _ins.ReadOnly = _ctx.ReadOnly = _res.ReadOnly = true;
        _q.Dock = DockStyle.Fill;
        var clear = NK.Button("Xoá tất cả", () => Post(new { action = "clear" }), tip: "Xoá toàn bộ lịch sử"); clear.Dock = DockStyle.Right;
        var top = new Panel { Height = NativeSkin.RowHeight(this, 38), Padding = new Padding(10, 6, 10, 4) };
        _count.Dock = DockStyle.Right; _count.TextAlign = ContentAlignment.MiddleRight; _count.AutoSize = false; _count.Width = NativeSkin.S(90);
        top.Controls.Add(_q); top.Controls.Add(_count); top.Controls.Add(clear);

        _list.DrawMode = DrawMode.OwnerDrawFixed; _list.ItemHeight = (int)(ThemeManager.BaseFont.Height * 2.3);
        _list.DrawItem += DrawItem;
        _list.SelectedIndexChanged += (_, _) => { if (!_fill && _list.SelectedIndex >= 0 && _list.SelectedIndex < _shown.Count) Select(_shown[_list.SelectedIndex]); };
        _q.TextChanged += (_, _) => RenderList();

        _bCopy = NK.Button("Copy", () => { if (_sel is not null) { Post(new { action = "copy", data = new { text = _sel.Result } }); Say("Đã copy.", "ok"); } }, primary: true);
        _bInsert = NK.Button("Chèn vào editor", () => { if (_sel is not null) { Post(new { action = "insert", data = new { text = _sel.Result } }); Say("Đã chèn vào editor.", "ok"); } }, tip: "Chèn vào vị trí con trỏ trong SQL Query");
        _bDel = NK.Button("Xoá mục này", () => { if (_sel is not null) Post(new { action = "delete", data = new { id = _sel.Id } }); });

        var detail = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 6, 10, 6) };
        foreach (var c in new Control[] { _res, _resL, _ctx, _ctxL, _ins, _insL, _meta }) { c.Dock = c == _res ? DockStyle.Fill : DockStyle.Top; detail.Controls.Add(c); }
        _res.Dock = DockStyle.Fill; detail.Controls.SetChildIndex(_res, 0);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_list); split.Panel2.Controls.Add(detail);
        Shown(split);

        Bottom.FlowDirection = FlowDirection.LeftToRight;
        Bottom.Controls.Add(_bCopy); Bottom.Controls.Add(_bInsert); Bottom.Controls.Add(_bDel);
        Bottom.Controls.Add(NK.Button("Đóng", () => Post(new { action = "close" })));
        Layout(top, split);
    }

    private void Shown(SplitContainer split) => HandleCreated += (_, _) => BeginInvoke(() => { try { split.SplitterDistance = Math.Max(200, (int)(Width * 0.34)); } catch { } });

    private void DrawItem(object? s, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _shown.Count) return;
        var it = _shown[e.Index]; var sel = (e.State & DrawItemState.Selected) != 0;
        using (var b = new SolidBrush(sel ? AppColors.Accent : AppColors.Panel)) e.Graphics.FillRectangle(b, e.Bounds);
        var fore = sel ? AppColors.OnAccent : AppColors.Text; var muted = sel ? AppColors.OnAccent : AppColors.TextMuted;
        var f = ThemeManager.BaseFont;
        var line1 = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 2, e.Bounds.Width - 16, f.Height);
        TextRenderer.DrawText(e.Graphics, it.Kind + "   " + Fmt(it.Time), f, line1, muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        var line2 = new Rectangle(e.Bounds.X + 8, e.Bounds.Y + 2 + f.Height, e.Bounds.Width - 16, f.Height);
        var text = System.Text.RegularExpressions.Regex.Replace(it.Instruction.Length > 0 ? it.Instruction : it.Result, @"\s+", " ").Trim();
        TextRenderer.DrawText(e.Graphics, text, f, line2, fore, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private static string Fmt(string iso) => DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") : iso;

    private void RenderList()
    {
        var q = _q.Text.Trim().ToLowerInvariant();
        _shown = _all.Where(e => q.Length == 0 || (e.Result + " " + e.Instruction + " " + e.Context).ToLowerInvariant().Contains(q)).ToList();
        _fill = true;
        _list.BeginUpdate(); _list.Items.Clear();
        foreach (var e in _shown) _list.Items.Add(e.Kind);
        _list.EndUpdate();
        if (_sel is not null) { var i = _shown.FindIndex(e => e.Id == _sel.Id); if (i >= 0) _list.SelectedIndex = i; }
        _fill = false;
        _count.Text = $"{_shown.Count}/{_all.Count} mục";
    }

    private void Select(Entry? e)
    {
        _sel = e;
        _meta.Text = e is null ? "" : $"{e.Kind} · {e.Engine} · {Fmt(e.Time)}";
        _ins.Text = e?.Instruction ?? ""; _ctx.Text = e?.Context ?? ""; _res.Text = e?.Result ?? "";
        _ins.Visible = _insL.Visible = e is not null && e.Instruction.Length > 0;
        _ctx.Visible = _ctxL.Visible = e is not null && e.Context.Length > 0;
        _bCopy.Enabled = _bInsert.Enabled = _bDel.Enabled = e is not null;
        Say("");
    }

    protected override bool HandleKey(Keys k) { if (k == Keys.Escape) { Post(new { action = "close" }); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "window.setItems") return;
        _all = (a.Count > 0 && a[0].ValueKind == JsonValueKind.Array ? a[0].EnumerateArray() : Enumerable.Empty<JsonElement>())
            .Select(e => new Entry { Id = P(e, "id"), Kind = P(e, "kind"), Engine = P(e, "engine"), Time = P(e, "time"), Instruction = P(e, "instruction"), Context = P(e, "context"), Result = P(e, "result") }).ToList();
        var keep = _sel is null ? null : _all.FirstOrDefault(e => e.Id == _sel.Id);
        _sel = keep;
        RenderList();
        var pick = keep ?? _all.FirstOrDefault();
        _sel = pick; Select(pick);
        if (pick is not null) { var i = _shown.FindIndex(e => e.Id == pick.Id); if (i >= 0) { _fill = true; _list.SelectedIndex = i; _fill = false; } }
    }
}
