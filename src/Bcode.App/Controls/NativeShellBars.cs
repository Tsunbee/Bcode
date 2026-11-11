using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Panel/Flow tự tô nền PanelAlt (ThemeManager đặt BackColor = Background cho mọi Panel nên không dựa vào BackColor) và kẻ viền ở 1 cạnh.</summary>
internal sealed class BarFlow : FlowLayoutPanel
{
    public BarFlow() { DoubleBuffered = true; }
    protected override void OnPaintBackground(PaintEventArgs e) { using var b = new SolidBrush(AppColors.PanelAlt); e.Graphics.FillRectangle(b, ClientRectangle); }
}

/// <summary>Label luôn dùng màu chữ phụ của theme (ThemeManager.Apply đặt ForeColor = Text cho mọi Label nên phải chặn ở đây).
/// Riêng màu trạng thái (Danger / Success / Warning / Accent — dòng báo lỗi, "Đã lưu"...) thì nhận, như .err/.ok của trang web — trước đây bị bỏ qua
/// nên mọi dòng trạng thái ở chế độ App đều xám. Màu nhớ theo vai trò nên đổi sáng/tối vẫn đúng.</summary>
public sealed class MutedLabel : Label
{
    private StatusTone.Kind _tone;
    public override Color ForeColor { get => StatusTone.Resolve(_tone); set { var t = StatusTone.Of(value); if (t != _tone) { _tone = t; Invalidate(); } } }
    /// <summary>Dòng trạng thái của trang native hiện thông điệp của exception vừa ném → ghi error.log (như WebBarHost.Call của bản Web; rất rẻ khi không có lỗi gần đây).</summary>
    public override string Text { get => base.Text; set { base.Text = value; Bcode.App.Services.ErrorLog.NoticeDisplayed(value, "Trang native"); } }
}

/// <summary>Màu trạng thái mà <see cref="MutedLabel"/> / <see cref="WrapLabel"/> nhận khi gán ForeColor; màu khác (Text của theme, màu vùng...) = chữ phụ.</summary>
internal static class StatusTone
{
    public enum Kind { Muted, Danger, Success, Warning, Accent }
    public static Kind Of(Color c) =>
        c.IsEmpty ? Kind.Muted
        : c.ToArgb() == AppColors.Danger.ToArgb() ? Kind.Danger
        : c.ToArgb() == AppColors.Success.ToArgb() ? Kind.Success
        : c.ToArgb() == AppColors.Warning.ToArgb() ? Kind.Warning
        : c.ToArgb() == AppColors.Accent.ToArgb() ? Kind.Accent
        : Kind.Muted;
    public static Color Resolve(Kind k) => k switch
    {
        Kind.Danger => AppColors.Danger, Kind.Success => AppColors.Success, Kind.Warning => AppColors.Warning, Kind.Accent => AppColors.Accent, _ => AppColors.TextMuted,
    };
}

/// <summary>Nhãn chữ phụ tự xuống dòng: rộng theo chỗ được cấp (Dock, hoặc <see cref="Control.MaximumSize"/>.Width khi không Dock), cao tự tính theo số dòng
/// thực tế — dùng cho chú thích dài trong thẻ (như &lt;p class="muted"&gt; của trang web).</summary>
public sealed class WrapLabel : Label
{
    private StatusTone.Kind _tone;
    public override Color ForeColor { get => StatusTone.Resolve(_tone); set { var t = StatusTone.Of(value); if (t != _tone) { _tone = t; Invalidate(); } } }

    public WrapLabel() { AutoSize = false; BackColor = Color.Transparent; UseMnemonic = false; }

    private bool _fitting;
    private void Fit()
    {
        if (_fitting) return;
        _fitting = true;
        try
        {
            if (Dock == DockStyle.None && MaximumSize.Width > 0 && Width != MaximumSize.Width) Width = MaximumSize.Width;
            var w = Math.Max(20, Width - Padding.Horizontal);
            var h = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, new Size(w, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix).Height + Padding.Vertical + 3;   // +3: chừa phần chân chữ dòng cuối (Label vẽ lệch ~2px so với MeasureText)
            if (Height != h) Height = h;
        }
        finally { _fitting = false; }
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Fit(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Fit(); Bcode.App.Services.ErrorLog.NoticeDisplayed(Text, "Trang native"); }   // như MutedLabel.Text
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
    protected override void OnDockChanged(EventArgs e) { base.OnDockChanged(e); Fit(); }
    public override Size MaximumSize { get => base.MaximumSize; set { base.MaximumSize = value; Fit(); } }
}

/// <summary>Label luôn dùng màu nhấn của theme (logo chữ "Bcode").</summary>
public sealed class AccentLabel : Label
{
    public override Color ForeColor { get => AppColors.Accent; set { } }
}

/// <summary>
/// Thanh trên của chế độ App/WinForms — cùng bố cục và cùng "ngôn ngữ" thông điệp với Web/Shell/topbar.html, nhưng là control WinForms
/// (không WebView2): nhóm trái xuống dòng khi hẹp (thanh cao thêm, báo qua <see cref="BarHeightChanged"/>), nhóm phải (WS, Sys/App, theme, Quick Access) bám mép phải.
/// MainForm chỉ nghe sự kiện và gọi Set*; cách đưa dữ liệu vào y hệt các hàm Push* dành cho trang web.
/// </summary>
public sealed class NativeTopBar : Panel
{
    private readonly BarFlow _left = new() { Dock = DockStyle.Fill, WrapContents = true, AutoSize = false, Padding = new Padding(8, 4, 0, 4) };
    private readonly BarFlow _right = new() { Dock = DockStyle.Right, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 4, 8, 4) };
    private readonly Dictionary<string, PillButton> _scriptButtons = new();
    private readonly Dictionary<string, string> _scriptOrigText = new();
    private readonly Dictionary<string, PillButton> _toolButtons = new();   // nút thanh công cụ kéo lên thanh trên, khoá "tool:key"
    private readonly DropDownButton _ws = new() { Width = NativeSkin.S(260), OnPanelAlt = true };
    private readonly PillButton _sys, _app, _theme, _bell;
    private readonly Label _dbName = new MutedLabel { AutoSize = true, Margin = new Padding(2, 8, 4, 0), BackColor = Color.Transparent };
    private string _sysName = "", _appName = "";
    private bool _sysView;

    /// <summary>Nút bấm trong thanh: settings | actions | deploy | ext | quickaccess | theme | refresh-db | notify.</summary>
    public event Action<string>? Action;
    public event Action<string>? ToolClicked;      // claude_web / gemini_web / help / key nút công cụ đã kéo lên thanh trên
    public event Action<string>? ScriptClicked;    // add | view | clear | save | copy
    public event Action<int>? WorkspaceSelected;
    public event Action<bool>? DatabaseSelected;   // true = Sys
    public event Action? BarHeightChanged;

    public NativeTopBar()
    {
        Dock = DockStyle.Top;
        Height = NativeSkin.Dens(this, 34);
        DoubleBuffered = true;

        var brand = new AccentLabel { Text = "Bcode", AutoSize = true, Margin = new Padding(0, 8, 8, 0), BackColor = Color.Transparent, Font = ThemeManager.BoldFont };
        _left.Controls.Add(brand);
        _left.Controls.Add(Btn("", "Settings (File / Actions)", () => Action?.Invoke("settings"), icon: IconGlyph.Gear, width: 30));
        _left.Controls.Add(Btn("Actions ▾", "Các hành động nhanh", () => Action?.Invoke("actions")));
        foreach (var (id, text) in UiTemplate.ScriptButtons)
        {
            var b = Btn(text, null, () => ScriptClicked?.Invoke(id));
            _scriptButtons[id] = b; _scriptOrigText[id] = text;
            _left.Controls.Add(b);
        }
        // Nút cố định sau nút Script (id theo UiTemplate.TopFixedButtons) — cũng đổi thứ tự / chữ / màu được ở Template như nút Script.
        void Fixed(string id, string text, string tip, System.Action onClick)
        {
            var b = Btn(text, tip, onClick);
            _scriptButtons[id] = b; _scriptOrigText[id] = text;
            _left.Controls.Add(b);
        }
        Fixed("deploy", "Triển khai ▾", "Công cụ triển khai: Check LCTT / CĐKT, Biên bản, Thiết kế màn hình, Khai báo API, Tạo cấu trúc API", () => Action?.Invoke("deploy"));
        Fixed("ext", "Tool mở rộng ▾", "Công cụ mở rộng: Check Include, Tạo báo cáo, Tạo nhanh danh mục", () => Action?.Invoke("ext"));
        Fixed("claude", "Claude", "Mở Claude trong một tab", () => ToolClicked?.Invoke("claude_web"));
        Fixed("gemini", "Gemini", "Mở Gemini trong một tab", () => ToolClicked?.Invoke("gemini_web"));
        Fixed("help", "Trợ giúp", "Hướng dẫn sử dụng từng tính năng (Ctrl+F1 — mở đúng mục của tab đang dùng)", () => ToolClicked?.Invoke("help"));

        _ws.SelectionChanged += i => WorkspaceSelected?.Invoke(i);
        _ws.Height = NativeSkin.Dens(_ws, 26);
        _ws.Margin = new Padding(0, 0, 3, 0);
        var refresh = Btn("", "Refresh database — nạp lại danh sách object / bảng / cột của project hiện tại", () => Action?.Invoke("refresh-db"), icon: IconGlyph.Refresh, width: 30);
        refresh.Margin = new Padding(0, 0, 6, 0);
        _sys = Btn("Sys", null, () => { _sysView = true; RenderDb(); DatabaseSelected?.Invoke(true); });
        _app = Btn("App", null, () => { _sysView = false; RenderDb(); DatabaseSelected?.Invoke(false); });
        _theme = Btn("", "Đổi giao diện sáng/tối", () => Action?.Invoke("theme"), icon: AppColors.IsDark ? IconGlyph.Moon : IconGlyph.Sun, width: 30);
        _bell = Btn("", BellTip, () => Action?.Invoke("notify"), icon: IconGlyph.Bell, width: 30);
        var quick = Btn("Quick Access", "Chọn tính năng hiển thị trên toolbar", () => Action?.Invoke("quickaccess"), icon: IconGlyph.Star, primary: true, pill: true);
        // Nhãn phiên bản góc phải (Beta v1.9) — như #verBadge của topbar.html; số ở <Version> trong Bcode.App.csproj.
        var ver = new VersionBadge { Text = Bcode.App.Services.ErrorLog.VersionLabel, Margin = new Padding(3, 0, 0, 0), Visible = Bcode.App.Services.ErrorLog.VersionLabel.Length > 0 };
        ver.Height = NativeSkin.Dens(ver, 26);
        NK.Tip(ver, VersionTip);
        foreach (var c in new Control[] { new MutedLabel { Text = "WS:", AutoSize = true, Margin = new Padding(0, 8, 2, 0), BackColor = Color.Transparent }, _ws, refresh, _sys, _app, _dbName, _theme, _bell, quick, ver })
            _right.Controls.Add(c);

        Controls.Add(_left);
        Controls.Add(_right);
        ThemeManager.ThemeChanged += OnTheme;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= OnTheme;
        RenderDb();
    }

    private PillButton Btn(string text, string? tip, System.Action onClick, IconGlyph? icon = null, bool primary = false, int? width = null, bool pill = false)
    {
        var b = PillButton.Flat(text, primary);
        if (pill) b.CornerRadius = null;
        b.Icon = icon;
        b.OnPanelAlt = true;
        b.Margin = new Padding(0, 0, 3, 0);
        b.Height = NativeSkin.Dens(b, 26);
        if (width is { } w) { b.AutoWidth = false; b.Width = NativeSkin.Px(b, w); }
        b.Click += (_, _) => onClick();
        if (tip is not null) NK.Tip(b, tip);
        return b;
    }

    private void OnTheme() { _theme.Icon = AppColors.IsDark ? IconGlyph.Moon : IconGlyph.Sun; Invalidate(true); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var b = new SolidBrush(AppColors.PanelAlt);
        e.Graphics.FillRectangle(b, ClientRectangle);
        using var p = new Pen(AppColors.Border);
        e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        // Chiều cao theo nội dung nhóm trái khi xuống dòng (giống topbar.html tự cao).
        var avail = Math.Max(100, Width - _right.Width);
        var want = Math.Max(NativeSkin.Dens(this, 34), Math.Max(_left.GetPreferredSize(new Size(avail, 0)).Height, _right.GetPreferredSize(Size.Empty).Height) + 1);
        if (want == Height) return;
        Height = want;
        // Đổi Height ngay trong OnLayout: lượt layout do resize bị WinForms bỏ qua (đang trong layout) → _left (Dock Fill) giữ chiều cao cũ,
        // hàng dưới bị cắt. Dock lại các con theo chiều cao mới.
        base.OnLayout(levent);
        BarHeightChanged?.Invoke();
    }

    private void RenderDb()
    {
        _dbName.Text = _sysView ? _sysName : _appName;
        _sys.IsPrimary = _sysView; _app.IsPrimary = !_sysView;
        _sys.Invalidate(); _app.Invalidate();
    }

    public void SetWorkspaces(IReadOnlyList<string> names, int selected, bool sysView)
    {
        _ws.SetItems(names, selected);
        _sysView = sysView; RenderDb();
    }

    private const string BellTip = "Thông báo y/c FSG: ghi chú mới, đổi trạng thái / hạn, y/c mới giao cho bạn";

    /// <summary>Tooltip nhãn phiên bản góc phải — dùng chung cho topbar.html (MainForm đẩy qua window.setVersion).</summary>
    public static string VersionTip => "Phiên bản Bcode đang chạy: " + Bcode.App.Services.ErrorLog.Version;

    /// <summary>Nhãn phiên bản dạng viên thuốc viền mảnh, chữ nhỏ màu phụ (như #verBadge của topbar.html). Tự vẽ chữ bằng font nhỏ hơn font chung
    /// (ThemeManager.Apply đặt lại Font của mọi control nên không dựa vào Font gán sẵn).</summary>
    private sealed class VersionBadge : Control
    {
        public VersionBadge()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
        }

        private Font SmallFont() => new(Font.FontFamily, Math.Max(6f, Font.Size * 0.85f), FontStyle.Regular, Font.Unit);

        private void FitWidth()
        {
            using var f = SmallFont();
            var w = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + NativeSkin.Px(this, 16);
            if (Width != w) Width = w;
        }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitWidth(); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(AppColors.PanelAlt)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var f = SmallFont();
            var th = TextRenderer.MeasureText("Ag", f, Size.Empty, TextFormatFlags.NoPadding).Height;
            var pillH = Math.Min(Height - 1, th + NativeSkin.Px(this, 6));
            var r = new Rectangle(0, (Height - pillH) / 2, Width - 1, pillH);
            using (var path = FlatToolStripRenderer.RoundedRect(r, pillH / 2))
            using (var pen = new Pen(AppColors.Border))
                g.DrawPath(pen, path);
            TextRenderer.DrawText(g, Text, f, r, AppColors.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Số thông báo y/c FSG chưa xem: &gt; 0 thì chuông thành nút nhấn (màu nhấn) kèm số.</summary>
    public void SetNotifyCount(int n)
    {
        var text = n > 0 ? (n > 99 ? "99+" : n.ToString()) : "";
        if (_bell.Text == text) return;
        _bell.IsPrimary = n > 0;
        _bell.AutoWidth = n > 0;      // trước khi đổi Text: PillButton tự đo lại bề rộng khi Text đổi
        _bell.Text = text;
        if (n == 0) _bell.Width = NativeSkin.Px(_bell, 30); // như các nút icon khác của thanh (Btn width: 30)
        NK.Tip(_bell, (n > 0 ? n + " thông báo chưa xem — " : "") + BellTip);
        _bell.Invalidate();
        _right.PerformLayout();
    }

    public void SetDbNames(string sys, string app) { _sysName = sys ?? ""; _appName = app ?? ""; RenderDb(); }

    /// <summary>Template giao diện: thứ tự + chữ + font/màu các nút thanh trên (nút có sẵn + nút thanh công cụ "tool:key" đã kéo lên).
    /// <paramref name="toolOf"/>(id) = chữ + tooltip của nút công cụ, null nếu id không phải nút công cụ.</summary>
    public void SetScriptLayout(IReadOnlyList<string> order, Func<string, ControlStyle?> styleOf, Func<string, (string Label, string Tip)?> toolOf, ICollection<string>? hidden = null)
    {
        var ids = order.Where(id => _scriptButtons.ContainsKey(id) || toolOf(id) is not null).ToList();
        foreach (var id in _scriptButtons.Keys) if (!ids.Contains(id)) ids.Add(id);
        _left.SuspendLayout();
        // Nút công cụ không còn trên thanh trên: bỏ đi.
        foreach (var id in _toolButtons.Keys.Where(id => !ids.Contains(id)).ToList())
        {
            _left.Controls.Remove(_toolButtons[id]);
            _toolButtons[id].Dispose();
            _toolButtons.Remove(id);
        }
        var index = 3; // sau brand, gear, Actions
        foreach (var id in ids)
        {
            var tool = toolOf(id);
            if (!_scriptButtons.TryGetValue(id, out var b) && !_toolButtons.TryGetValue(id, out b))
            {
                var key = id[UiTemplate.TopToolPrefix.Length..];
                b = Btn(tool!.Value.Label, null, () => ToolClicked?.Invoke(key));
                _toolButtons[id] = b;
                _left.Controls.Add(b);
            }
            if (tool is { } info) NK.Tip(b, info.Tip);
            _left.Controls.SetChildIndex(b, index++);
            var st = styleOf(id);
            b.Text = string.IsNullOrEmpty(st?.Text) ? (tool?.Label ?? _scriptOrigText[id]) : st!.Text!;
            b.ForeOverride = UiTemplate.ParseColor(st?.ForeColor);
            b.BackOverride = UiTemplate.ParseColor(st?.BackColor);
            if (st is not null && (st.FontFamily is not null || st.FontSize is not null || st.Bold is not null))
            {
                var fam = string.IsNullOrWhiteSpace(st.FontFamily) ? ThemeManager.BaseFont.FontFamily.Name : st.FontFamily;
                var size = st.FontSize is { } sz ? (float)(sz * UiScale.Factor) : ThemeManager.BaseFont.Size;
                try { b.Font = new Font(fam, size, st.Bold == true ? FontStyle.Bold : FontStyle.Regular); } catch { }
            }
            b.Visible = hidden is null || !hidden.Contains(id);   // nút có sẵn đã kéo xuống thanh công cụ (Template)
            b.Invalidate();
        }
        _left.ResumeLayout(true);
    }
}

/// <summary>Thanh trạng thái native (tương đương statusbar.html): chữ trạng thái bên trái; bên phải link web đăng nhập, nút "▾ Project", tên máy.</summary>
public sealed class NativeStatusBar : Panel
{
    private readonly Label _text = new MutedLabel { AutoEllipsis = true, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, BackColor = Color.Transparent, Text = "Chưa chọn workspace" };
    private readonly LinkLabel _link = new() { AutoSize = true, Visible = false, BackColor = Color.Transparent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(0, 4, 8, 0) };
    private readonly PillButton _menu = PillButton.Flat("▾ Project");
    private readonly Label _machine = new MutedLabel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(8, 4, 0, 0) };
    private readonly FlowLayoutPanel _right = new() { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
    // Link sau chữ trạng thái (vd "Mở file" / "Mở thư mục" sau khi Save as CSV / Excel ở tab Result); trống thì ẩn.
    private readonly FlowLayoutPanel _links = new() { Dock = DockStyle.Right, AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Visible = false, Padding = new Padding(0, 0, 12, 0) };

    public event Action? ProjectWebClicked;
    /// <summary>Toạ độ màn hình ngay trên nút "▾ Project" để thả menu.</summary>
    public event Action<Point>? ProjectMenuRequested;

    public NativeStatusBar()
    {
        Dock = DockStyle.Bottom;
        Height = NativeSkin.Dens(this, 26);
        DoubleBuffered = true;
        Padding = new Padding(10, 0, 8, 0);
        _menu.Height = NativeSkin.Dens(_menu, 20);
        _menu.OnPanelAlt = true;
        _menu.Margin = new Padding(0, 2, 0, 0);
        _menu.Click += (_, _) => ProjectMenuRequested?.Invoke(_menu.PointToScreen(new Point(0, 0)));
        NK.Tip(_menu, "Thông tin project: mở nhanh Program / Source / Working / Mobile, copy project info");
        _link.LinkClicked += (_, _) => ProjectWebClicked?.Invoke();
        _right.Controls.AddRange(new Control[] { _link, _menu, _machine });
        Controls.Add(_text);
        Controls.Add(_links);
        Controls.Add(_right);
        ThemeManager.ThemeChanged += Recolor;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= Recolor;
        Recolor();
    }

    private void Recolor()
    {
        _link.LinkColor = AppColors.Text; _link.ActiveLinkColor = AppColors.Accent;
        foreach (var l in _links.Controls.OfType<LinkLabel>()) { l.LinkColor = AppColors.Accent; l.ActiveLinkColor = AppColors.Accent; }
        Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var b = new SolidBrush(AppColors.PanelAlt);
        e.Graphics.FillRectangle(b, ClientRectangle);
        using var p = new Pen(AppColors.Border);
        e.Graphics.DrawLine(p, 0, 0, Width, 0);
    }

    public void SetStatus(string text, IReadOnlyList<(string Label, Action Click)>? links = null)
    {
        _text.Text = text;
        _links.SuspendLayout();
        foreach (Control c in _links.Controls.Cast<Control>().ToList()) c.Dispose();
        foreach (var (label, click) in links ?? Array.Empty<(string, Action)>())
        {
            var l = new LinkLabel { Text = label, AutoSize = true, BackColor = Color.Transparent, LinkBehavior = LinkBehavior.HoverUnderline, Margin = new Padding(8, 4, 0, 0),
                LinkColor = AppColors.Accent, ActiveLinkColor = AppColors.Accent };
            l.LinkClicked += (_, _) => click();
            _links.Controls.Add(l);
        }
        _links.Visible = _links.Controls.Count > 0;
        _links.ResumeLayout(true);
    }

    public void SetProject(string link, string machine)
    {
        _link.Text = link; _link.Visible = !string.IsNullOrEmpty(link);
        _machine.Text = machine;
    }
}
