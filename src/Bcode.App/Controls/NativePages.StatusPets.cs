using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>"Thú cưng trên thanh trạng thái" (statuspets.html) — bản native: bật/tắt, số con mỗi loại, tốc độ, tuỳ chọn, khung xem trước.</summary>
internal sealed class StatusPetsPage : DialogPage
{
    private static readonly int[] Speeds = { 25, 50, 75, 100, 125, 150, 200, 250, 300 };
    private readonly CheckBox _enabled = NK.Check("Bật thú cưng trên thanh trạng thái");
    private readonly CheckBox _mouse = NK.Check("Phản ứng với chuột (ong/bướm bay theo, mèo vồ, chim bay mất, ốc rụt vỏ)");
    private readonly CheckBox _flowers = NK.Check("Mọc hoa cho ong / bướm hút mật");
    private readonly CheckBox _trail = NK.Check("Vệt phấn hoa / vệt nhớt");
    private readonly DropDownButton _speed = new() { Width = NativeSkin.S(120) };
    private readonly TableLayoutPanel _kinds = new() { AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 4, 0, 4) };
    private readonly Dictionary<string, Label> _counts = new();
    private readonly StatusPetsPreview _preview = new() { Margin = new Padding(0, 4, 0, 4) };
    private readonly Control _opts;
    private StatusPetSettings _s = new();
    private bool _loading;

    public StatusPetsPage()
    {
        _enabled.Font = new Font(_enabled.Font, FontStyle.Bold);
        _speed.Height = NativeSkin.RowHeight(_speed, 26);
        _speed.SetItems(Speeds.Select(v => v + "%").ToList(), 3);
        _kinds.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _kinds.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _kinds.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        foreach (var (key, label, hint) in StatusPetSettings.Kinds)
        {
            var n = new Label { AutoSize = false, Width = NativeSkin.S(22), TextAlign = ContentAlignment.MiddleCenter, Text = "0", Margin = new Padding(2, 6, 2, 0), BackColor = Color.Transparent };
            _counts[key] = n;
            var k = key;
            var minus = NK.Button("−", () => Bump(k, -1)); minus.AutoWidth = false; minus.Width = NativeSkin.S(28);
            var plus = NK.Button("+", () => Bump(k, 1)); plus.AutoWidth = false; plus.Width = NativeSkin.S(28);
            var step = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), BackColor = Color.Transparent };
            step.Controls.AddRange(new Control[] { minus, n, plus });
            var l = NK.Text(label); l.Margin = new Padding(0, 8, 12, 0);
            var h = NK.Muted(hint); h.Margin = new Padding(0, 8, 12, 0);
            _kinds.Controls.Add(l); _kinds.Controls.Add(h); _kinds.Controls.Add(step);
        }

        var opts = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
        opts.Controls.Add(Section("Con vật (số con mỗi loại)"));
        opts.Controls.Add(_kinds);
        opts.Controls.Add(Section("Tuỳ chọn"));
        var speedRow = NK.Row(NK.Text("Tốc độ"), _speed);
        opts.Controls.Add(speedRow);
        opts.Controls.Add(_mouse); opts.Controls.Add(_flowers); opts.Controls.Add(_trail);
        _opts = opts;

        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16, 12, 16, 8) };
        body.Controls.Add(_enabled);
        body.Controls.Add(opts);
        body.Controls.Add(Section("Xem trước — rê chuột vào đây để thử"));
        body.Controls.Add(_preview);
        var note = NK.Muted("Thay đổi áp dụng ngay lên thanh trạng thái; bấm Hủy để trả lại như cũ.");
        body.Controls.Add(note);
        body.Resize += (_, _) => _preview.Width = Math.Max(NativeSkin.S(200), body.ClientSize.Width - body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);

        _enabled.CheckedChanged += (_, _) => { if (_loading) return; _s.Enabled = _enabled.Checked; Changed(); };
        _mouse.CheckedChanged += (_, _) => { if (_loading) return; _s.Mouse = _mouse.Checked; Changed(); };
        _flowers.CheckedChanged += (_, _) => { if (_loading) return; _s.Flowers = _flowers.Checked; Changed(); };
        _trail.CheckedChanged += (_, _) => { if (_loading) return; _s.Trail = _trail.Checked; Changed(); };
        _speed.SelectionChanged += i => { if (_loading) return; _s.Speed = Speeds[Math.Clamp(i, 0, Speeds.Length - 1)]; Changed(); };

        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("OK", () => Post(new { action = "ok", value = _s }), primary: true));
        Bottom.Controls.Add(NK.Button("Hủy", () => Post(new { action = "cancel" })));
        Bottom.Controls.Add(NK.Button("Mặc định", () => { _s = new StatusPetSettings { Enabled = _s.Enabled }; Changed(); }));
        Layout(null, body);
    }

    private static Label Section(string text) { var l = NK.Text(text); l.Font = new Font(l.Font, FontStyle.Bold); l.Margin = new Padding(0, 12, 0, 2); return l; }

    private void Bump(string key, int d)
    {
        _s.Counts[key] = Math.Clamp(_s.CountOf(key) + d, 0, StatusPetSettings.MaxPerKind);
        Changed();
    }

    private void Changed()
    {
        Render();
        Post(new { action = "change", value = _s });
    }

    private void Render()
    {
        _loading = true;
        try
        {
            _enabled.Checked = _s.Enabled;
            _opts.Enabled = _s.Enabled;
            _mouse.Checked = _s.Mouse; _flowers.Checked = _s.Flowers; _trail.Checked = _s.Trail;
            var si = Array.IndexOf(Speeds, _s.Speed);
            _speed.Select(si >= 0 ? si : Array.FindLastIndex(Speeds, v => v <= _s.Speed), raise: false);
            foreach (var (key, lbl) in _counts) lbl.Text = _s.CountOf(key).ToString();
            _preview.Set(_s);
        }
        finally { _loading = false; }
    }

    protected override bool HandleKey(Keys key) { if (key == Keys.Escape) { Post(new { action = "cancel" }); return true; } return false; }

    public override void Dispatch(string path, IReadOnlyList<JsonElement> a)
    {
        if (path != "pets.init") return;
        var d = Arg(a, 0);
        try { _s = (JsonSerializer.Deserialize<StatusPetSettings>(PO(d, "settings").GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new()).Normalize(); }
        catch { _s = new(); }
        Render();
    }
}
