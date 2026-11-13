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
    private readonly CheckBox _seasonal = NK.Check("Trang trí theo mùa: cây + hoa + hạt rơi / bay (Tết: cây mai · Trung thu: đèn lồng, hoa cúc · Noel: cây thông, tuyết, mũ) và theo giờ");
    private readonly DropDownButton _seasonMode = new() { Width = NativeSkin.S(260) };
    private readonly Label _seasonHint = NK.Muted("");
    private readonly CheckBox _reactSql = NK.Check("Phản ứng khi chạy SQL Query: xong thì thả tim, lỗi thì giật mình, chạy quá 5 giây thì mèo ngủ chờ");
    private readonly CheckBox _tips = NK.Check("Bấm vào con thú để xem mẹo dùng Bcode; rê chuột lên để xem tên");
    private readonly DropDownButton _rest = new() { Width = NativeSkin.S(110) };
    private readonly FlowLayoutPanel _wsList = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 4, 0, 4), BackColor = Color.Transparent };
    private readonly CheckBox _hideOnShare = NK.Check("Tạm ẩn thú cưng khi đang chạy chương trình:");
    private readonly RoundedInput _shareApps = NK.Input("BcodeWatch, CptHost");
    private readonly Dictionary<string, RoundedInput> _names = new();
    private string? _currentWs;
    private readonly DropDownButton _speed = new() { Width = NativeSkin.S(120) };
    private readonly TableLayoutPanel _kinds = new() { AutoSize = true, ColumnCount = 4, Margin = new Padding(0, 4, 0, 4) };
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
        for (var i = 0; i < 4; i++) _kinds.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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
            var nm = NK.Input(StatusPetSettings.DefaultNames.GetValueOrDefault(key, key));
            nm.Width = NativeSkin.S(110); nm.Margin = new Padding(8, 3, 0, 0);
            NK.Tip(nm, "Tên (hiện khi rê chuột lên con thú) — để trống = " + StatusPetSettings.DefaultNames.GetValueOrDefault(key, key));
            nm.Inner.MaxLength = 24;
            nm.Inner.TextChanged += (_, _) =>
            {
                if (_loading) return;
                if (string.IsNullOrWhiteSpace(nm.Text)) _s.Names.Remove(k); else _s.Names[k] = nm.Text;
                Changed();
            };
            _names[key] = nm;
            _kinds.Controls.Add(l); _kinds.Controls.Add(h); _kinds.Controls.Add(step); _kinds.Controls.Add(nm);
        }

        var opts = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0), BackColor = Color.Transparent };
        opts.Controls.Add(Section("Con vật (số con mỗi loại · tên)"));
        opts.Controls.Add(_kinds);
        opts.Controls.Add(Section("Tuỳ chọn"));
        var speedRow = NK.Row(NK.Text("Tốc độ"), _speed);
        opts.Controls.Add(speedRow);
        opts.Controls.Add(_mouse); opts.Controls.Add(_flowers); opts.Controls.Add(_trail); opts.Controls.Add(_seasonal);
        _seasonMode.Height = NativeSkin.RowHeight(_seasonMode, 26);
        _seasonMode.SetItems(StatusPetSettings.SeasonModes.Select(x => x.Label).ToList(), 0);
        _seasonHint.Margin = new Padding(0, 9, 0, 0);
        opts.Controls.Add(NK.Row(NK.Text("Mùa"), _seasonMode, _seasonHint));
        opts.Controls.Add(Section("Báo việc"));
        opts.Controls.Add(_reactSql); opts.Controls.Add(_tips);
        _rest.Height = NativeSkin.RowHeight(_rest, 26);
        _rest.SetItems(StatusPetSettings.RestChoices.Select(v => v == 0 ? "Tắt" : v + " phút").ToList(), 0);
        opts.Controls.Add(NK.Row(NK.Text("Nhắc nghỉ sau"), _rest, NK.Text("dùng Bcode liên tục — mèo ra nhắc, bấm vào để mở Giải lao. Rời máy ≥ 5 phút thì tính lại.")));
        opts.Controls.Add(Section("Database thật của khách"));
        opts.Controls.Add(NK.Muted("Đánh dấu workspace là DB thật: khi đang chọn workspace đó, thanh trạng thái có vạch đỏ và thú cưng thỉnh thoảng giơ \"!\".", wrap: true));
        opts.Controls.Add(_wsList);
        opts.Controls.Add(Section("Khi chia sẻ màn hình"));
        opts.Controls.Add(_hideOnShare);
        _shareApps.Width = NativeSkin.S(420);
        opts.Controls.Add(_shareApps);
        opts.Controls.Add(NK.Muted("Tên tiến trình, cách nhau dấu phẩy. Mặc định: BcodeWatch (Xem chung), CptHost (Zoom đang chia sẻ màn hình). Có thể thêm UltraViewer_Desktop, AnyDesk, TeamViewer…", wrap: true));
        _opts = opts;
        _seasonal.CheckedChanged += (_, _) => { if (_loading) return; _s.Seasonal = _seasonal.Checked; Changed(); };
        _seasonMode.SelectionChanged += i => { if (_loading) return; _s.SeasonMode = StatusPetSettings.SeasonModes[Math.Clamp(i, 0, StatusPetSettings.SeasonModes.Length - 1)].Key; Changed(); };
        _reactSql.CheckedChanged += (_, _) => { if (_loading) return; _s.ReactSql = _reactSql.Checked; Changed(); };
        _tips.CheckedChanged += (_, _) => { if (_loading) return; _s.Tips = _tips.Checked; Changed(); };
        _hideOnShare.CheckedChanged += (_, _) => { if (_loading) return; _s.HideOnShare = _hideOnShare.Checked; Changed(); };
        _rest.SelectionChanged += i => { if (_loading) return; _s.RestMinutes = StatusPetSettings.RestChoices[Math.Clamp(i, 0, StatusPetSettings.RestChoices.Length - 1)]; Changed(); };
        _shareApps.Inner.TextChanged += (_, _) => { if (_loading) return; _s.ShareApps = _shareApps.Text; Changed(); };

        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(16, 12, 16, 8) };
        body.Controls.Add(_enabled);
        body.Controls.Add(opts);
        body.Controls.Add(Section("Xem trước — rê chuột vào đây để thử"));
        body.Controls.Add(_preview);
        foreach (var d in new[] { _edStyle, _edKind, _edScale }) d.Height = NativeSkin.RowHeight(d, 26);
        _edStyle.SetItems(StatusPetSettings.EditorStyles.Select(x => x.Label).ToList(), 0);
        _edKind.SetItems(EdKinds.Select(x => x.Label).ToList(), Math.Max(0, Array.FindIndex(EdKinds, x => x.Key == "cat")));
        _edScale.SetItems(Scales.Select(v => v + "%").ToList(), 2);
        var fxNote = NK.Muted("🌸 Hoa đào rơi hai bên màn hình + thanh trên màu hoa đào: chỉnh ở ⚙ Settings → Giao diện (Template) → tab Theme & màu.", wrap: true);
        fxNote.Margin = new Padding(0, 16, 0, 2);
        body.Controls.Add(fxNote);
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
            _preview.Width = w; _edPreview.Width = w; _edHint.Width = w; fxNote.Width = w;
        };
        _edStyle.SelectionChanged += i => { if (_loading) return; _s.EditorStyle = StatusPetSettings.EditorStyles[Math.Clamp(i, 0, StatusPetSettings.EditorStyles.Length - 1)].Key; Changed(); };
        _edKind.SelectionChanged += i => { if (_loading) return; _s.EditorKind = EdKinds[Math.Clamp(i, 0, EdKinds.Length - 1)].Key; Changed(); };
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
        // Giữ thêm: tên thú cưng, ngày bắt đầu nuôi, danh sách DB khách, thời gian nhắc nghỉ (lựa chọn của bạn, không phải kiểu trang trí).
        Bottom.Controls.Add(NK.Button("Mặc định", () =>
        {
            _s = new StatusPetSettings
            {
                Enabled = _s.Enabled, EditorStyle = _s.EditorStyle, EditorKind = _s.EditorKind, EditorScale = _s.EditorScale,
                Names = new Dictionary<string, string>(_s.Names), AdoptedOn = _s.AdoptedOn, WarnWorkspaces = new List<string>(_s.WarnWorkspaces), RestMinutes = _s.RestMinutes,
                PetalRain = _s.PetalRain, PetalDensity = _s.PetalDensity, PetalBar = _s.PetalBar,
            };
            Changed();
        }));
        Layout(null, body);
    }

    private static readonly (string Key, string Label, string Hint)[] EdKinds =
        StatusPetSettings.Kinds.Where(k => StatusPetSettings.EditorKinds.Contains(k.Key)).ToArray();

    private void BuildWorkspaces(IReadOnlyList<string> names)
    {
        _wsList.SuspendLayout();
        foreach (Control c in _wsList.Controls.Cast<Control>().ToList()) c.Dispose();
        if (names.Count == 0) _wsList.Controls.Add(NK.Muted("(chưa có workspace nào)"));
        foreach (var n in names)
        {
            var name = n;
            var cur = _currentWs is not null && string.Equals(name, _currentWs, StringComparison.OrdinalIgnoreCase);
            var c = NK.Check(cur ? name + "   (đang chọn)" : name);
            c.Tag = name; c.Margin = new Padding(0, 2, 10, 0);
            c.CheckedChanged += (_, _) =>
            {
                if (_loading) return;
                _s.WarnWorkspaces.RemoveAll(w => string.Equals(w, name, StringComparison.OrdinalIgnoreCase));
                if (c.Checked) _s.WarnWorkspaces.Add(name);
                Changed();
            };
            _wsList.Controls.Add(c);
        }
        _wsList.ResumeLayout(true);
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
            _seasonMode.Select(Math.Max(0, Array.FindIndex(StatusPetSettings.SeasonModes, x => x.Key == _s.SeasonMode)), raise: false);
            _seasonMode.Enabled = _s.Seasonal;
            var today = StatusPetEnv.SeasonOf(DateTime.Now);
            _seasonHint.Text = _s.SeasonMode != "auto" ? "cố định quanh năm"
                : today.Length > 0 ? "hôm nay: " + StatusPetSettings.SeasonModes.First(x => x.Key == today).Label
                : "hôm nay không rơi vào mùa nào — chưa có cây / hoa theo mùa";
            _seasonal.Checked = _s.Seasonal; _reactSql.Checked = _s.ReactSql; _tips.Checked = _s.Tips; _hideOnShare.Checked = _s.HideOnShare;
            _rest.Select(Math.Max(0, Array.IndexOf(StatusPetSettings.RestChoices, _s.RestMinutes)), raise: false);
            if (!_shareApps.Inner.Focused && _shareApps.Text != _s.ShareApps) _shareApps.Text = _s.ShareApps;
            foreach (var c in _wsList.Controls.OfType<CheckBox>()) c.Checked = _s.IsWarnWorkspace(c.Tag as string);
            foreach (var (key, nm) in _names)
            {
                var v = _s.Names.GetValueOrDefault(key, "");
                if (!nm.Inner.Focused && nm.Text != v) nm.Text = v;
            }
            var si = Array.IndexOf(Speeds, _s.Speed);
            _speed.Select(si >= 0 ? si : Array.FindLastIndex(Speeds, v => v <= _s.Speed), raise: false);
            foreach (var (key, lbl) in _counts) lbl.Text = _s.CountOf(key).ToString();
            _preview.Set(_s);
            _preview.SetEnv(new StatusPetEnv
            {
                Warn = _s.IsWarnWorkspace(_currentWs), Season = _s.EffectiveSeason(DateTime.Now), Night = StatusPetEnv.IsNight(DateTime.Now),
                Tips = new[] { "Mẹo sẽ hiện ở đây khi bấm vào con thú trên thanh trạng thái." },
            });
            _edStyle.Select(Math.Max(0, Array.FindIndex(StatusPetSettings.EditorStyles, x => x.Key == _s.EditorStyle)), raise: false);
            _edKind.Select(Math.Max(0, Array.FindIndex(EdKinds, x => x.Key == _s.EditorKind)), raise: false);
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
        var names = new List<string>();
        try
        {
            if (d.TryGetProperty("workspaces", out var ws) && ws.ValueKind == JsonValueKind.Array)
                names.AddRange(ws.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0));
            _currentWs = d.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.String ? cur.GetString() : null;
        }
        catch { /* thiếu dữ liệu workspace — chỉ ẩn danh sách */ }
        BuildWorkspaces(names);
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
