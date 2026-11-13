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
    // Trong editor SQL Query (EditorPets — Controls/StatusPets.Editor.cs)
    private static readonly int[] Scales = { 100, 125, 150, 175, 200, 250 };
    private readonly DropDownButton _edStyle = new() { Width = NativeSkin.S(220) };
    private readonly DropDownButton _edKind = new() { Width = NativeSkin.S(120) };
    private readonly DropDownButton _edScale = new() { Width = NativeSkin.S(90) };
    private readonly Label _edHint = NK.Muted("", wrap: true);
    private readonly EditorPetPreview _edPreview = new() { Margin = new Padding(0, 4, 0, 4) };
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
        foreach (var d in new[] { _edStyle, _edKind, _edScale }) d.Height = NativeSkin.RowHeight(d, 26);
        _edStyle.SetItems(StatusPetSettings.EditorStyles.Select(x => x.Label).ToList(), 0);
        _edKind.SetItems(StatusPetSettings.Kinds.Select(x => x.Label).ToList(), 4);
        _edScale.SetItems(Scales.Select(v => v + "%").ToList(), 2);
        var edSec = Section("Trong editor SQL Query");
        edSec.Margin = new Padding(0, 20, 0, 2);
        body.Controls.Add(edSec);
        body.Controls.Add(NK.Row(NK.Text("Kiểu"), _edStyle, NK.Text("Con vật"), _edKind, NK.Text("Cỡ"), _edScale));
        body.Controls.Add(_edHint);
        body.Controls.Add(_edPreview);
        EditorPets.Attach(_edPreview);
        var note = NK.Muted("Thay đổi áp dụng ngay lên thanh trạng thái và các tab SQL Query; bấm Hủy để trả lại như cũ.");
        body.Controls.Add(note);
        body.Resize += (_, _) =>
        {
            var w = Math.Max(NativeSkin.S(200), body.ClientSize.Width - body.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            _preview.Width = w; _edPreview.Width = w; _edHint.Width = w;
        };
        _edStyle.SelectionChanged += i => { if (_loading) return; _s.EditorStyle = StatusPetSettings.EditorStyles[Math.Clamp(i, 0, StatusPetSettings.EditorStyles.Length - 1)].Key; Changed(); };
        _edKind.SelectionChanged += i => { if (_loading) return; _s.EditorKind = StatusPetSettings.Kinds[Math.Clamp(i, 0, StatusPetSettings.Kinds.Length - 1)].Key; Changed(); };
        _edScale.SelectionChanged += i => { if (_loading) return; _s.EditorScale = Scales[Math.Clamp(i, 0, Scales.Length - 1)]; Changed(); };

        _enabled.CheckedChanged += (_, _) => { if (_loading) return; _s.Enabled = _enabled.Checked; Changed(); };
        _mouse.CheckedChanged += (_, _) => { if (_loading) return; _s.Mouse = _mouse.Checked; Changed(); };
        _flowers.CheckedChanged += (_, _) => { if (_loading) return; _s.Flowers = _flowers.Checked; Changed(); };
        _trail.CheckedChanged += (_, _) => { if (_loading) return; _s.Trail = _trail.Checked; Changed(); };
        _speed.SelectionChanged += i => { if (_loading) return; _s.Speed = Speeds[Math.Clamp(i, 0, Speeds.Length - 1)]; Changed(); };

        Bottom.FlowDirection = FlowDirection.RightToLeft;
        Bottom.Controls.Add(NK.Button("OK", () => Post(new { action = "ok", value = _s }), primary: true));
        Bottom.Controls.Add(NK.Button("Hủy", () => Post(new { action = "cancel" })));
        // Mặc định: trả tuỳ chọn thanh trạng thái về gốc, giữ 2 công tắc (thanh trạng thái, editor) + loài / cỡ trong editor.
        Bottom.Controls.Add(NK.Button("Mặc định", () =>
        {
            _s = new StatusPetSettings { Enabled = _s.Enabled, EditorStyle = _s.EditorStyle, EditorKind = _s.EditorKind, EditorScale = _s.EditorScale };
            Changed();
        }));
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
            _edStyle.Select(Math.Max(0, Array.FindIndex(StatusPetSettings.EditorStyles, x => x.Key == _s.EditorStyle)), raise: false);
            _edKind.Select(Math.Max(0, Array.FindIndex(StatusPetSettings.Kinds, x => x.Key == _s.EditorKind)), raise: false);
            var ci = Array.IndexOf(Scales, _s.EditorScale);
            _edScale.Select(ci >= 0 ? ci : Math.Max(0, Array.FindLastIndex(Scales, v => v <= _s.EditorScale)), raise: false);
            _edKind.Enabled = _edScale.Enabled = _s.EditorOn;
            _edHint.Text = _s.EditorStyle switch
            {
                "follow" => "Con thú chạy / bay theo chuột, đứng lệch dưới-phải mũi chuột; chữ I giữ nguyên nên đặt con trỏ / bôi chọn vẫn chính xác. Chuột đứng yên thì nghỉ (mèo ngủ zZ).",
                "cursor" => "Con trỏ chuột trong vùng chữ thành hình con thú; chấm đỏ ở mũi là điểm bấm. Lề số dòng / thanh cuộn vẫn dùng con trỏ thường.",
                _ => "Tắt: editor SQL Query giữ con trỏ chuột bình thường.",
            };
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

/// <summary>Khung thử thú cưng trong editor (cửa sổ cài đặt, chế độ App) — vài dòng SQL mẫu, con trỏ chữ I; gắn vào <see cref="EditorPets"/>
/// nên chạy đúng như trong tab SQL Query (cài đặt đang sửa được áp ngay qua MainForm.PushPets).</summary>
internal sealed class EditorPetPreview : Panel
{
    private static readonly (string Text, Color Color)[][] Lines =
    {
        new[] { ("-- rê chuột / bôi chọn ở đây để thử", Color.FromArgb(0x6a, 0x99, 0x55)) },
        new[] { ("select", Color.FromArgb(0x56, 0x9c, 0xd6)), (" ma_vt, ten_vt, sum(so_luong) ", Color.Empty), ("as", Color.FromArgb(0x56, 0x9c, 0xd6)), (" sl", Color.Empty) },
        new[] { ("from", Color.FromArgb(0x56, 0x9c, 0xd6)), (" dmvt ", Color.Empty), ("where", Color.FromArgb(0x56, 0x9c, 0xd6)), (" ma_vt ", Color.Empty), ("like", Color.FromArgb(0x56, 0x9c, 0xd6)), (" ", Color.Empty), ("'A%'", Color.FromArgb(0xce, 0x91, 0x78)) },
        new[] { ("group by", Color.FromArgb(0x56, 0x9c, 0xd6)), (" ma_vt, ten_vt", Color.Empty) },
    };
    private Font? _mono;

    public EditorPetPreview()
    {
        DoubleBuffered = true;
        Cursor = Cursors.IBeam;
        Height = NativeSkin.Dens(this, 120);
    }

    protected override void Dispose(bool disposing) { if (disposing) _mono?.Dispose(); base.Dispose(disposing); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var b = new SolidBrush(AppColors.PanelAlt);
        e.Graphics.FillRectangle(b, ClientRectangle);
        using var pn = new Pen(AppColors.Border);
        e.Graphics.DrawRectangle(pn, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        _mono ??= new Font("Consolas", 9f);
        var lh = TextRenderer.MeasureText("Ag", _mono).Height + 2;
        const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        for (var i = 0; i < Lines.Length; i++)
        {
            var x = NativeSkin.S(10);
            foreach (var (text, color) in Lines[i])
            {
                TextRenderer.DrawText(e.Graphics, text, _mono, new Point(x, NativeSkin.S(6) + i * lh), color.IsEmpty ? AppColors.Text : color, F);
                x += TextRenderer.MeasureText(e.Graphics, text, _mono, Size.Empty, F).Width;
            }
        }
    }
}
