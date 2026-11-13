namespace Bcode.App.UI;

/// <summary>
/// "Thanh trên màu hoa đào": khi bật hiệu ứng hoa đào rơi (StatusPetSettings.PetalRain + PetalBar), thanh trên của Bcode (NativeTopBar / topbar.html)
/// và dải công cụ ngay dưới (MainForm._toolsBar) nhuốm hồng đào. Chỉ những control đánh dấu là thanh (<see cref="MarkBar"/> hoặc <see cref="IBar"/>) và
/// con cháu của chúng mới đổi màu — BarFlow / PillButton dùng ở chỗ khác giữ nguyên. Màu = màu gốc của theme pha hồng (giữ độ sáng tối của theme).
/// </summary>
public static class PeachTheme
{
    /// <summary>Control là "thanh" nhuốm hồng khi bật (NativeTopBar).</summary>
    public interface IBar { }

    private static readonly HashSet<Control> Marked = new();

    public static bool On { get; private set; }

    /// <summary>Bật / tắt đổi — thanh vẽ lại (MainForm cũng đẩy window.setPeach xuống topbar.html).</summary>
    public static event Action? Changed;

    public static void Set(bool on)
    {
        if (On == on) return;
        On = on;
        Changed?.Invoke();
    }

    /// <summary>Đánh dấu thêm 1 control là thanh (ToolStrip dải công cụ của MainForm).</summary>
    public static void MarkBar(Control c)
    {
        Marked.Add(c);
        c.Disposed += (_, _) => Marked.Remove(c);
        c.LocationChanged += (_, _) => Moved(c);   // nền sóng neo theo form: đổi chỗ thì tô lại
        c.SizeChanged += (_, _) => Moved(c);
    }

    public static bool InBar(Control? c)
    {
        for (; c is not null; c = c.Parent) if (c is IBar || Marked.Contains(c)) return true;
        return false;
    }

    /// <summary>Đang bật và <paramref name="c"/> nằm trong thanh.</summary>
    public static bool Applies(Control? c) => On && InBar(c);

    // Hồng đào: theme tối pha hồng đậm, theme sáng pha hồng phấn.
    private static Color Pink => AppColors.IsDark ? Color.FromArgb(0xc2, 0x3b, 0x72) : Color.FromArgb(0xff, 0x9e, 0xbe);

    public static Color Mix(Color a, Color b, float k) =>
        Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    /// <summary>Nền thanh.</summary>
    public static Color Bar => Mix(AppColors.PanelAlt, Pink, AppColors.IsDark ? 0.24f : 0.30f);
    /// <summary>Nền nút thường trên thanh.</summary>
    public static Color Button => Mix(AppColors.ButtonBack, Pink, AppColors.IsDark ? 0.26f : 0.32f);
    /// <summary>Nền nút khi rê chuột.</summary>
    public static Color Hover => Mix(AppColors.PanelAlt, Pink, AppColors.IsDark ? 0.42f : 0.50f);
    /// <summary>Viền / vạch ngăn.</summary>
    public static Color Line => Mix(AppColors.Border, Pink, 0.55f);
    /// <summary>Nút chính (Quick Access, App / Sys đang chọn) — hồng đào đậm thay xanh.</summary>
    public static Color Accent => AppColors.IsDark ? Color.FromArgb(0xe0, 0x5a, 0x8e) : Color.FromArgb(0xe0, 0x4f, 0x86);
    public static Color AccentHover => AppColors.IsDark ? Color.FromArgb(0xec, 0x74, 0xa2) : Color.FromArgb(0xc9, 0x3f, 0x74);

    /// <summary>Nền cho control: trong thanh và đang bật → <see cref="Bar"/>, không thì <paramref name="normal"/>.</summary>
    public static Color BarOr(Control? c, Color normal) => Applies(c) ? Bar : normal;


    // ---- Nền "chuyển màu cong": hồng đào loang từ góc dưới-trái và góc trên-phải, trắng ở giữa, 1 đường cong mảnh như mép cánh hoa ----
    // Không lặp ô được (phụ thuộc bề rộng cửa sổ) nên vẽ 1 TẤM nền phủ cả cụm thanh (rộng = vùng client form, cao = tới đáy thanh thấp nhất
    // đã đánh dấu), mọi thanh (NativeTopBar, BarFlow, nút, nhãn, ToolStrip dải công cụ, topbar.html) lấy đúng phần của mình theo vị trí trong form
    // → nền liền mạch qua ranh các control và giữa thanh trên với dải công cụ.

    /// <summary>Cửa sổ có thanh tiêu đề tô màu hoa đào (MainForm) — Windows chỉ cho 1 màu phẳng nên lấy hồng phấn nhạt.</summary>
    public static Form? TitleForm { get; set; }

    /// <summary>Nền trắng ở giữa (theme tối: nền tối của theme ánh hồng rất nhẹ).</summary>
    private static Color BgWhite => AppColors.IsDark ? Mix(AppColors.PanelAlt, Pink, 0.06f) : Mix(Color.White, Pink, 0.02f);
    /// <summary>Hồng đào đậm nhất ở 2 góc (sáng: hồng phấn; tối: nền tối pha hồng — chữ sáng vẫn rõ).</summary>
    private static Color GlowPink => AppColors.IsDark ? Mix(AppColors.PanelAlt, Pink, 0.42f) : Color.FromArgb(0xff, 0xb9, 0xd1);
    private static Color GlowSoft => AppColors.IsDark ? Mix(AppColors.PanelAlt, Pink, 0.24f) : Color.FromArgb(0xff, 0xda, 0xe7);

    /// <summary>Màu thanh tiêu đề Windows khi bật (1 màu phẳng, hài hoà với nền).</summary>
    public static Color Caption => AppColors.IsDark ? Mix(AppColors.PanelAlt, Pink, 0.22f) : Color.FromArgb(0xff, 0xe4, 0xee);

    /// <summary>Nút thường trên nền: lớp kính trong mờ để nền vẫn hiện qua mà chữ luôn rõ (kể cả chỗ hồng đậm ở góc).</summary>
    public static Color ButtonGlass => AppColors.IsDark ? Color.FromArgb(60, 0, 0, 0) : Color.FromArgb(90, 255, 255, 255);

    /// <summary>Tấm nền đang giữ (chỉ 1 tấm — MainForm) và cỡ đã báo lần trước.</summary>
    private static Bitmap? _canvas;
    private static (int W, int H, int Dpi, int A, int B, int C) _canvasKey;
    private static (Form? F, Size S) _lastSize;

    /// <summary>Tấm nền đổi cỡ (cửa sổ rộng / hẹp, dải công cụ xuống dòng): bản Web gửi lại ảnh nền xuống topbar.html.</summary>
    public static event Action<Form>? CanvasChanged;

    /// <summary>Cỡ tấm nền của form: rộng = vùng client, cao = đáy thấp nhất của các thanh đã đánh dấu (đang hiện) trong form.</summary>
    public static Size CanvasSize(Form f)
    {
        var h = 0;
        foreach (var m in Marked)
            if (m.Visible && m.IsHandleCreated && m.FindForm() == f) h = Math.Max(h, FormOffset(m).Y + m.Height);
        return new Size(Math.Max(1, f.ClientSize.Width), Math.Max(1, h));
    }

    /// <summary>Tấm nền của form <paramref name="f"/> (dựng lại khi đổi cỡ / màu / DPI; vài ms cho 1 tấm ~1900×140).</summary>
    public static Bitmap Canvas(Form f, Size? min = null)
    {
        var s = CanvasSize(f);
        if (min is { } m) s = new Size(Math.Max(s.Width, m.Width), Math.Max(s.Height, m.Height));
        var key = (s.Width, s.Height, f.DeviceDpi, BgWhite.ToArgb(), GlowPink.ToArgb(), GlowSoft.ToArgb());
        if (_canvas is not null && _canvasKey == key) return _canvas;
        var old = _canvas;
        _canvas = RenderCanvas(s.Width, s.Height, f.DeviceDpi / 96f);
        _canvasKey = key;
        old?.Dispose();
        return _canvas;
    }

    private static Bitmap RenderCanvas(int w, int h, float scale)
    {
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.Clear(BgWhite);
        // Hai quầng hồng hình elip: tâm ở góc dưới-trái và góc trên-phải, nhạt dần ra trong suốt (vùng giữa còn trắng).
        void Glow(float cx, float cy, float rx, float ry, Color core, Color mid)
        {
            using var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddEllipse(cx - rx, cy - ry, 2 * rx, 2 * ry);
            using var br = new System.Drawing.Drawing2D.PathGradientBrush(path) { CenterPoint = new PointF(cx, cy) };
            br.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend
            {
                Colors = new[] { Color.FromArgb(0, mid), Color.FromArgb(255, mid), core },
                Positions = new[] { 0f, 0.38f, 1f },   // 0 = mép elip, 1 = tâm
            };
            g.FillRectangle(br, 0, 0, w, h);
        }
        var rx = Math.Max(w * 0.50f, 260 * scale);
        Glow(0, h, rx, h * 1.15f, GlowPink, GlowSoft);
        Glow(w, 0, rx, h * 1.15f, Mix(GlowPink, BgWhite, 0.12f), GlowSoft);
        // Đường cong mảnh như mép cánh hoa: 1 vệt trắng mềm + 1 nét hồng mảnh ngay dưới, chạy chéo nhẹ lên về bên phải.
        PointF[] Curve(float dy)
        {
            var n = Math.Max(16, w / 12);
            var p = new PointF[n + 1];
            for (var i = 0; i <= n; i++)
            {
                var x = (float)i / n * (w + 40) - 20;
                p[i] = new PointF(x, h * 0.64f + dy - h * 0.25f * x / w + h * 0.15f * MathF.Sin(2 * MathF.PI * x / Math.Max(w, 1) + 0.2f));
            }
            return p;
        }
        using (var white = new Pen(Color.FromArgb(AppColors.IsDark ? 40 : 200, 255, 255, 255), 9f * scale) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            g.DrawCurve(white, Curve(0));
        using (var pink = new Pen(Color.FromArgb(AppColors.IsDark ? 150 : 190, AppColors.IsDark ? Color.FromArgb(0xe0, 0x6a, 0x98) : Color.FromArgb(0xff, 0xc4, 0xd8)), 2.5f * scale))
            g.DrawCurve(pink, Curve(11f * scale));
        return bmp;
    }

    /// <summary>Gốc vùng client của <paramref name="c"/> tính trong vùng client của form chứa nó (mọi thanh neo nền chung 1 gốc).</summary>
    public static Point FormOffset(Control c)
    {
        var f = c.FindForm();
        if (f is null || f == c || !c.IsHandleCreated || !f.IsHandleCreated) return Point.Empty;
        return f.PointToClient(c.PointToScreen(Point.Empty));
    }

    /// <summary>Tô phần nền hoa đào của <paramref name="c"/> vào <paramref name="r"/> (toạ độ của <paramref name="c"/>).</summary>
    public static void PaintWaves(Graphics g, Control c, Rectangle r)
    {
        if (c.FindForm() is not { } f) { using var b = new SolidBrush(Bar); g.FillRectangle(b, r); return; }
        var o = FormOffset(c);
        var canvas = Canvas(f, new Size(o.X + c.Width, o.Y + c.Height));   // control nằm ngoài tấm (thanh chưa đánh dấu) thì nới tấm cho đủ
        using var tb = new TextureBrush(canvas, System.Drawing.Drawing2D.WrapMode.Clamp);
        tb.TranslateTransform(-o.X, -o.Y);
        g.FillRectangle(tb, r);
    }

    /// <summary>Nền thanh: đang bật và trong thanh → nền hoa đào; không thì tô màu phẳng <paramref name="normal"/>.</summary>
    public static void PaintBarOr(Graphics g, Control c, Rectangle r, Color normal)
    {
        if (Applies(c)) { PaintWaves(g, c, r); return; }
        using var b = new SolidBrush(normal);
        g.FillRectangle(b, r);
    }

    /// <summary>Control trong thanh đổi chỗ / cỡ → tô lại theo gốc mới (Windows chép nguyên điểm ảnh cũ sang chỗ mới nên nền lệch);
    /// tấm nền đổi cỡ thì tô lại mọi thanh của form và báo <see cref="CanvasChanged"/>.</summary>
    public static void Moved(Control c)
    {
        if (!Applies(c) || !c.IsHandleCreated) return;
        c.Invalidate(true);
        if (c.FindForm() is not { } f) return;
        var s = CanvasSize(f);
        if (_lastSize.F == f && _lastSize.S == s) return;
        _lastSize = (f, s);
        foreach (var m in Marked.ToArray()) if (m.FindForm() == f && m.IsHandleCreated) m.Invalidate(true);
        CanvasChanged?.Invoke(f);
    }

    /// <summary>Phần tấm nền dưới <paramref name="c"/> dạng PNG base64 (cho topbar.html — cùng hình với bản GDI+ để thanh web nối liền dải công cụ native).</summary>
    public static string CanvasPngBase64(Control c, out Size size)
    {
        var f = c.FindForm()!;
        var o = FormOffset(c);
        var canvas = Canvas(f, new Size(o.X + c.Width, o.Y + c.Height));
        var part = Rectangle.Intersect(new Rectangle(o, c.Size), new Rectangle(Point.Empty, canvas.Size));
        if (part.Width <= 0 || part.Height <= 0) part = new Rectangle(0, 0, 1, 1);
        using var crop = canvas.Clone(part, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var ms = new MemoryStream();
        crop.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        size = part.Size;
        return Convert.ToBase64String(ms.ToArray());
    }
}
