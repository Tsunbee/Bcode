using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Quick Access (quickaccess.html): sắp xếp công cụ giữa các vùng Ghim / Nhóm / Ẩn bằng kéo thả hoặc nút ▲▼ + "chuyển tới vùng".</summary>
internal sealed class QuickAccessPage : DialogPage
{
    private sealed class Tool { public string Label = "", Shortcut = ""; }
    private sealed class Grp { public string Name = ""; public bool Sep; public List<string> Keys = new(); }

    private readonly Dictionary<string, Tool> _tools = new();
    private List<string> _defaults = new(), _groupKeys = new();
    private List<Grp> _suggested = new(), _groups = new();
    private List<string> _pinned = new(), _hidden = new();
    private readonly Label _title = new() { AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 3, FontStyle.Bold) };
    private readonly Label _intro;
    private readonly Label _preview = NK.Muted("", true), _count = NK.Muted("");
    private readonly FlowLayoutPanel _zones = new() { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(10, 4, 10, 4) };
    private string? _selKey;

    public QuickAccessPage()
    {
        _intro = NK.Muted("", true);
        var head = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 8, 12, 2) };
        _intro.Dock = DockStyle.Top; _title.Dock = DockStyle.Top;
        var tools = TopBar();
        tools.Controls.AddRange(new Control[]
        {
            NK.Button("+ Nhóm mới", () => { _groups.Add(new Grp { Name = "Nhóm " + (_groups.Count + 1) }); Render(); }, primary: true),
            NK.Button("Gợi ý nhóm", Suggest, tip: "Chia sẵn theo vùng chức năng"),
            NK.Button("Bỏ hết nhóm", () => { foreach (var g in _groups) _pinned.AddRange(g.Keys); _groups.Clear(); Render(); }, tip: "Bỏ mọi nhóm, công cụ trong nhóm về lại Ghim"),
            NK.Button("Mặc định", ResetDefault, tip: "Trả về thanh mặc định: không nhóm, hiện tất cả, thứ tự gốc"), _count,
        });
        _preview.Dock = DockStyle.Top; _preview.Padding = new Padding(12, 0, 12, 4);
        head.Controls.Add(_intro); head.Controls.Add(_title);
        var topAll = new Panel { Dock = DockStyle.Top, AutoSize = true };
        tools.Dock = DockStyle.Top; head.Dock = DockStyle.Top;
        topAll.Controls.Add(_preview); topAll.Controls.Add(tools); topAll.Controls.Add(head);

        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("OK", Ok, primary: true)); Bottom.Controls.Add(NK.Button("Cancel", () => Post(new { action = "cancel" })));
        Bottom.Controls.Add(NK.Muted("Thứ tự trong vùng Ghim = thứ tự nút từ trái sang phải; trong nhóm = thứ tự trong menu."));
        Layout(topAll, _zones);
        _zones.Resize += (_, _) => { foreach (Control c in _zones.Controls) c.Width = Math.Max(200, _zones.ClientSize.Width - 24); };
    }

    // ---- model ----
    private List<string> ZoneList(string z) => z == "pin" ? _pinned : z == "hid" ? _hidden : _groups[int.Parse(z[1..])].Keys;
    private string ZoneOf(string key) => _pinned.Contains(key) ? "pin" : _hidden.Contains(key) ? "hid" : "g" + _groups.FindIndex(g => g.Keys.Contains(key));
    private string ZoneName(string z) => z == "pin" ? "Ghim" : z == "hid" ? "Ẩn" : (_groups[int.Parse(z[1..])].Name is { Length: > 0 } n ? n : "Nhóm " + (int.Parse(z[1..]) + 1));

    private void MoveKey(string key, string toZone, int? index)
    {
        var from = ZoneList(ZoneOf(key));
        var fi = from.IndexOf(key); if (fi >= 0) from.RemoveAt(fi);
        var to = ZoneList(toZone);
        var at = index is null || index > to.Count ? to.Count : Math.Max(0, index.Value);
        to.Insert(at, key);
        _selKey = key;
        Render();
    }

    private string Label(string key) => _tools.TryGetValue(key, out var t) ? t.Label : key;

    private void Suggest()
    {
        var inGroup = _suggested.SelectMany(g => g.Keys).ToHashSet();
        _groups = _suggested.Select(g => new Grp { Name = g.Name, Sep = g.Sep, Keys = g.Keys.Where(k => _tools.ContainsKey(k) && !_hidden.Contains(k)).ToList() }).ToList();
        _pinned = _tools.Keys.Where(k => !inGroup.Contains(k) && !_hidden.Contains(k)).OrderBy(k => _defaults.IndexOf(k)).ToList();
        Render();
    }

    private void ResetDefault()
    {
        _groups.Clear(); _hidden.Clear();
        _pinned = _defaults.Where(k => _tools.ContainsKey(k)).Concat(_tools.Keys.Where(k => !_defaults.Contains(k))).ToList();
        Render();
    }

    private void Ok()
    {
        var named = _groups.Where(g => g.Name.Trim().Length > 0).ToList();
        var unnamed = _groups.Where(g => g.Name.Trim().Length == 0).SelectMany(g => g.Keys);
        var pin = _pinned.Concat(unnamed);
        var items = pin.Select(k => new { key = k, visible = true })
            .Concat(named.SelectMany(g => g.Keys.Select(k => new { key = k, visible = true })))
            .Concat(_hidden.Select(k => new { key = k, visible = false })).ToList();
        Post(new { action = "ok", data = new { items, groups = named.Select(g => new { name = g.Name.Trim(), keys = g.Keys, sep = g.Sep }).ToList() } });
    }

    protected override bool HandleKey(Keys k) { if (k == Keys.Escape && !(ActiveControl is TextBoxBase)) { Post(new { action = "cancel" }); return true; } return false; }

    // ---- giao diện ----
    private void Render()
    {
        _zones.SuspendLayout();
        foreach (Control c in _zones.Controls) c.Dispose();
        _zones.Controls.Clear();
        _zones.Controls.Add(ZoneCard("pin"));
        for (var i = 0; i < _groups.Count; i++) _zones.Controls.Add(ZoneCard("g" + i));
        _zones.Controls.Add(ZoneCard("hid"));
        foreach (Control c in _zones.Controls) c.Width = Math.Max(200, _zones.ClientSize.Width - 24);
        _zones.ResumeLayout(true);
        RenderPreview();
    }

    private void RenderPreview()
    {
        var named = _groups.Where(g => g.Name.Trim().Length > 0).ToList();
        var parts = _pinned.Select(Label).ToList();
        var sb = string.Join("  |  ", parts);
        if (named.Count > 0) sb += (parts.Count > 0 ? "   ┃   " : "") + string.Join("   ", named.Select(g => g.Name.Trim() + " ▾")) + "   ⌕";
        _preview.Text = "Thanh công cụ:  " + sb;
        var n = _pinned.Count + (named.Count > 0 ? named.Count + 1 : 0);
        _count.Text = $"{n} nút trên thanh · {_hidden.Count} ẩn · {_tools.Count} công cụ";
    }

    private Control ZoneCard(string zone)
    {
        var list = ZoneList(zone);
        var title = zone == "pin" ? $"📌 Ghim trên thanh ({list.Count})" : zone == "hid" ? $"🚫 Ẩn ({list.Count})" : "";
        var card = new CardPanel(title) { Margin = new Padding(0, 0, 0, 8) };
        var lb = new ListBox { Dock = DockStyle.Top, IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, AllowDrop = true, ItemHeight = (int)(ThemeManager.BaseFont.Height * 1.7) };
        foreach (var k in list) lb.Items.Add(k);
        lb.Height = Math.Max(2, Math.Min(list.Count, 14)) * lb.ItemHeight + 6;
        lb.DrawItem += (_, e) =>
        {
            if (e.Index < 0) return;
            var key = (string)lb.Items[e.Index]; var sel = (e.State & DrawItemState.Selected) != 0;
            using (var br = new SolidBrush(sel ? AppColors.Accent : AppColors.Input)) e.Graphics.FillRectangle(br, e.Bounds);
            var fore = sel ? AppColors.OnAccent : AppColors.Text; var muted = sel ? AppColors.OnAccent : AppColors.TextMuted;
            TextRenderer.DrawText(e.Graphics, "⋮⋮", lb.Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, 24, e.Bounds.Height), muted, TextFormatFlags.VerticalCenter);
            var sc = _tools.TryGetValue(key, out var t) ? t.Shortcut : "";
            var scW = sc.Length > 0 ? TextRenderer.MeasureText(sc, lb.Font).Width + 8 : 0;
            TextRenderer.DrawText(e.Graphics, Label(key), lb.Font, new Rectangle(e.Bounds.X + 32, e.Bounds.Y, e.Bounds.Width - 40 - scW, e.Bounds.Height), fore, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (scW > 0) TextRenderer.DrawText(e.Graphics, sc, lb.Font, new Rectangle(e.Bounds.Right - scW - 6, e.Bounds.Y, scW, e.Bounds.Height), muted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        };
        if (_selKey is not null) { var i = list.IndexOf(_selKey); if (i >= 0) lb.SelectedIndex = i; }
        lb.SelectedIndexChanged += (_, _) => { if (lb.SelectedIndex >= 0) _selKey = (string)lb.SelectedItem!; };
        lb.MouseDown += (_, e) => { var i = lb.IndexFromPoint(e.Location); if (i >= 0 && e.Button == MouseButtons.Left) { lb.SelectedIndex = i; lb.Tag = e.Location; } else lb.Tag = null; };
        lb.MouseMove += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || lb.Tag is not Point p || lb.SelectedItem is not string key) return;
            if (Math.Abs(e.X - p.X) + Math.Abs(e.Y - p.Y) < 6) return;
            lb.Tag = null; lb.DoDragDrop(key, DragDropEffects.Move);
        };
        lb.DragOver += (_, e) => e.Effect = e.Data?.GetDataPresent(typeof(string)) == true ? DragDropEffects.Move : DragDropEffects.None;
        lb.DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(typeof(string)) is not string key) return;
            var pt = lb.PointToClient(new Point(e.X, e.Y)); var i = lb.IndexFromPoint(pt);
            int? to = i < 0 ? null : i;
            if (to is not null && ZoneOf(key) == zone && list.IndexOf(key) < to) to--;   // bỏ phần tử cũ đi trước vị trí đích
            BeginInvoke(() => MoveKey(key, zone, to));
        };

        // thanh công cụ của vùng
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = new Padding(0) };
        if (zone.StartsWith('g'))
        {
            var gi = int.Parse(zone[1..]); var g = _groups[gi];
            var name = NK.Input("Tên nhóm"); name.Text = g.Name; name.Width = NativeSkin.S(200);
            name.TextChanged += (_, _) => { g.Name = name.Text; RenderPreview(); };
            name.Inner.Leave += (_, _) => Render();
            var kbd = NK.Muted(gi < _groupKeys.Count && _groupKeys[gi].Length > 0 ? _groupKeys[gi] : "—");
            bar.Controls.Add(name); bar.Controls.Add(kbd);
            var sep = NK.Button("|", () => { g.Sep = !g.Sep; Render(); }, tip: gi == 0 ? "Nhóm đầu: vạch ngăn với vùng Ghim đã tự có" : g.Sep ? "Đang có vạch ngăn bên trái nhóm này — bấm để bỏ" : "Thêm vạch ngăn bên trái nhóm này");
            sep.Enabled = gi > 0; sep.AutoWidth = false; sep.Width = NativeSkin.S(34); if (g.Sep) sep.IsPrimary = true;
            var l = NK.Button("◀", () => { (_groups[gi - 1], _groups[gi]) = (_groups[gi], _groups[gi - 1]); Render(); }, tip: "Đưa nhóm sang trái"); l.Enabled = gi > 0; l.AutoWidth = false; l.Width = NativeSkin.S(34);
            var r = NK.Button("▶", () => { (_groups[gi + 1], _groups[gi]) = (_groups[gi], _groups[gi + 1]); Render(); }, tip: "Đưa nhóm sang phải"); r.Enabled = gi < _groups.Count - 1; r.AutoWidth = false; r.Width = NativeSkin.S(34);
            var d = NK.Button("✕", () => { _pinned.AddRange(g.Keys); _groups.RemoveAt(gi); Render(); }, tip: "Xoá nhóm (công cụ về lại Ghim)"); d.AutoWidth = false; d.Width = NativeSkin.S(34);
            bar.Controls.AddRange(new Control[] { sep, l, r, d });
        }
        else bar.Controls.Add(NK.Muted(zone == "pin" && list.Count > 6 ? $"Đang ghim {list.Count} công cụ — nên 4–6 để thanh ngắn; kéo bớt vào nhóm." : ""));

        var up = NK.Button("▲", () => { if (_selKey is not null && list.IndexOf(_selKey) > 0) MoveKey(_selKey, zone, list.IndexOf(_selKey) - 1); }, tip: "Lên"); up.AutoWidth = false; up.Width = NativeSkin.S(34);
        var dn = NK.Button("▼", () => { if (_selKey is not null && list.IndexOf(_selKey) is var i && i >= 0 && i < list.Count - 1) MoveKey(_selKey, zone, i + 1); }, tip: "Xuống"); dn.AutoWidth = false; dn.Width = NativeSkin.S(34);
        var zoneIds = new List<string> { "pin" }; zoneIds.AddRange(Enumerable.Range(0, _groups.Count).Select(i => "g" + i)); zoneIds.Add("hid");
        var mv = new DropDownButton { Width = NativeSkin.S(180) };
        mv.SetItems(new[] { "Chuyển tới vùng…" }.Concat(zoneIds.Where(z => z != zone).Select(ZoneName)).ToList(), 0);
        mv.SelectionChanged += idx => { if (idx > 0 && _selKey is not null && ZoneOf(_selKey) == zone) MoveKey(_selKey, zoneIds.Where(z => z != zone).ToList()[idx - 1], null); };
        bar.Controls.Add(up); bar.Controls.Add(dn); bar.Controls.Add(mv);

        if (list.Count == 0) lb.Height = lb.ItemHeight + 6;
        card.Body.Controls.Add(lb); card.Body.Controls.Add(bar);
        card.Body.Controls.SetChildIndex(bar, 1);
        if (list.Count == 0) { var e = NK.Muted("Kéo công cụ vào đây (hoặc dùng \"Chuyển tới vùng…\")"); e.Dock = DockStyle.Top; card.Body.Controls.Add(e); }
        return card;
    }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "window.init") return;
        var s = Arg(a, 0);
        _tools.Clear();
        var items = PA(s, "items").ToList();
        foreach (var x in items) _tools[P(x, "key")] = new Tool { Label = P(x, "label"), Shortcut = P(x, "shortcut") };
        var defs = PA(s, "defaults").Select(x => x.GetString() ?? "").ToList();
        _defaults = defs.Count > 0 ? defs : items.Select(x => P(x, "key")).ToList();
        _suggested = PA(s, "suggested").Select(g => new Grp { Name = P(g, "name"), Sep = PB(g, "sep"), Keys = PA(g, "keys").Select(k => k.GetString() ?? "").ToList() }).ToList();
        _groupKeys = PA(s, "groupShortcuts").Select(k => k.GetString() ?? "").ToList();
        if (P(s, "title").Length > 0) _title.Text = P(s, "title"); else _title.Text = "Quick Access";
        var intro = P(s, "intro");
        _intro.Text = intro.Length > 0 ? Regex.Replace(Regex.Replace(intro, "<br ?/?>", "\n"), "<[^>]+>", "") : "Kéo thả công cụ giữa các vùng: Ghim = nút riêng trên thanh, nhóm = gom vào 1 nút \"Tên ▾\", Ẩn = không hiện trên thanh. Mọi công cụ vẫn tìm được bằng ⌕ / Ctrl+P và phím tắt riêng.";
        _hidden = items.Where(x => !PB2(x, "visible")).Select(x => P(x, "key")).ToList();
        var used = new HashSet<string>(_hidden);
        _groups = PA(s, "groups").Select(g => new Grp { Name = P(g, "name"), Sep = PB(g, "sep"), Keys = PA(g, "keys").Select(k => k.GetString() ?? "").Where(k => _tools.ContainsKey(k) && used.Add(k)).ToList() }).ToList();
        _pinned = items.Where(x => PB2(x, "visible") && !used.Contains(P(x, "key"))).Select(x => P(x, "key")).ToList();
        Render();
    }

    private static bool PB2(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.False;
}
