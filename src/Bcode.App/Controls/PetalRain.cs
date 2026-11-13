using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Hiệu ứng "hoa đào rơi" hai bên cửa sổ Bcode (Giao diện (Template) → Theme & màu → Hiệu ứng hoa đào). Chạy cho cả chế độ Web và App:
/// 2 cửa sổ lớp (WS_EX_LAYERED | TRANSPARENT | NOACTIVATE — chuột bấm xuyên qua, không bao giờ nhận focus) owned bởi MainForm, phủ dải sát mép trái / phải
/// vùng client. Mỗi dải vẽ thẳng vào 1 DIB 32bpp giữ suốt (Bitmap bọc trên bộ nhớ DIB, PArgb) rồi UpdateLayeredWindow — không cấp phát mỗi khung.
/// Cánh hoa hồng / trắng, rơi chéo theo gió, lật (co giãn ngang) và xoay; mờ dần về phía giữa màn hình. Thu nhỏ / ẩn / tắt thì dừng hẳn bộ đếm giờ.
/// </summary>
public sealed class PetalRain : IDisposable
{
    private const int StripDesign = 150;   // bề ngang mỗi dải (px thiết kế, nhân DPI)

    private sealed class Petal
    {
        public float X, Y, Vy, Size, Rot, RotV, Flip, FlipV, Sway, SwayPh, SwayF;
        public int Color;
        public bool Blossom;
    }

    private static readonly Color[] Colors =
    {
        Color.FromArgb(255, 0xff, 0xb7, 0xc5), Color.FromArgb(255, 0xff, 0xc8, 0xd6), Color.FromArgb(255, 0xff, 0x9e, 0xb8),
        Color.FromArgb(255, 0xf8, 0x8f, 0xab), Color.FromArgb(255, 0xff, 0xf4, 0xf7), Color.FromArgb(255, 0xff, 0xfb, 0xfc),
    };
    private static readonly Color Edge = Color.FromArgb(150, 0xe8, 0x7a, 0x98);   // viền nhạt — cánh trắng vẫn thấy trên nền sáng

    private readonly Form _owner;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly Random _rnd = new();
    private readonly Strip _left, _right;
    private readonly GraphicsPath _petalPath = MakePetalPath();
    private bool _on, _hidden, _active = true;
    private int _density = 2;
    private double _last;
    private float _wind, _windT, _gust;

    public PetalRain(Form owner)
    {
        _owner = owner;
        _left = new Strip(this, leftSide: true);
        _right = new Strip(this, leftSide: false);
        _timer.Tick += (_, _) => Tick();
        owner.Move += (_, _) => Reposition();
        owner.Resize += (_, _) => { Reposition(); Update(); };
        owner.VisibleChanged += (_, _) => Update();
        owner.FormClosing += (_, _) => Dispose();
    }

    /// <summary>Bật / tắt + mật độ (1 = thưa, 2 = vừa, 3 = dày).</summary>
    public void Apply(bool on, int density)
    {
        _on = on;
        _density = Math.Clamp(density, 1, 3);
        Update();
    }

    /// <summary>Tạm ẩn (đang chia sẻ màn hình — cùng luật với thú cưng).</summary>
    public bool Hidden { get => _hidden; set { if (_hidden == value) return; _hidden = value; Update(); } }

    /// <summary>Bcode đang được dùng: 30 hình/giây; không thì ~15 hình/giây.</summary>
    public bool Active { get => _active; set { _active = value; _timer.Interval = value ? 33 : 66; } }

    private bool Running => _on && !_hidden && !_owner.IsDisposed && _owner.Visible && _owner.WindowState != FormWindowState.Minimized && _owner.IsHandleCreated;

    private void Update()
    {
        if (Running)
        {
            Reposition();
            if (!_timer.Enabled) { _clock.Restart(); _last = 0; _timer.Start(); }
        }
        else
        {
            _timer.Stop();
            _left.Hide(); _right.Hide();
        }
    }

    private float K => NativeSkin.Dens(_owner, 100) / 100f;

    private void Reposition()
    {
        if (!Running) return;
        var client = _owner.RectangleToScreen(_owner.ClientRectangle);
        var w = Math.Min((int)(StripDesign * K), client.Width / 3);
        _left.Place(new Rectangle(client.Left, client.Top, w, client.Height));
        _right.Place(new Rectangle(client.Right - w, client.Top, w, client.Height));
    }

    private void Tick()
    {
        if (!Running) { Update(); return; }
        var now = _clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Min(now - _last, 0.1); _last = now;
        // Gió: đổi hướng chậm + thỉnh thoảng một cơn gió mạnh.
        _windT -= dt;
        if (_windT <= 0) { _windT = Rnd(4, 9); _wind = Rnd(-12, 18); if (_rnd.NextDouble() < 0.25) _gust = Rnd(25, 45); }
        _gust = Math.Max(0, _gust - dt * 12);
        var target = 6 + 8 * _density;   // số cánh mỗi dải: 14 / 22 / 30
        _left.Step(dt, target);
        _right.Step(dt, target);
    }

    private float Rnd(float a, float b) => a + (float)_rnd.NextDouble() * (b - a);

    private Petal NewPetal(int w, int h, bool top)
    {
        var k = K;
        return new Petal
        {
            X = Rnd(0, w), Y = top ? Rnd(-40, -8) * k : Rnd(0, h),
            Vy = Rnd(28, 62) * k, Size = Rnd(4.5f, 8.5f) * k,
            Rot = Rnd(0, 360), RotV = Rnd(-90, 90),
            Flip = Rnd(0, MathF.PI * 2), FlipV = Rnd(1.5f, 4.5f),
            Sway = Rnd(8, 22) * k, SwayPh = Rnd(0, MathF.PI * 2), SwayF = Rnd(0.6f, 1.6f),
            Color = _rnd.Next(Colors.Length), Blossom = _rnd.NextDouble() < 0.10,
        };
    }

    private static GraphicsPath MakePetalPath()
    {
        // Cánh hoa đào: bầu tròn, đầu có khía chữ V (hệ toạ độ đơn vị, gốc ở chân cánh (0, 1), đầu ở trên).
        var p = new GraphicsPath();
        p.AddBezier(0f, 1f, -0.95f, 0.55f, -0.85f, -0.75f, -0.22f, -1f);
        p.AddLine(-0.22f, -1f, 0f, -0.72f);
        p.AddLine(0f, -0.72f, 0.22f, -1f);
        p.AddBezier(0.22f, -1f, 0.85f, -0.75f, 0.95f, 0.55f, 0f, 1f);
        p.CloseFigure();
        return p;
    }

    private void DrawPetal(Graphics g, Petal p, float alpha)
    {
        var st = g.Save();
        g.TranslateTransform(p.X, p.Y);
        g.RotateTransform(p.Rot);
        var flip = Math.Max(0.18f, Math.Abs(MathF.Cos(p.Flip)));   // lật cánh: co ngang theo cos
        var c = Colors[p.Color];
        var a = (int)(c.A * alpha);
        using var fill = new SolidBrush(Color.FromArgb(a, c));
        using var edge = new Pen(Color.FromArgb((int)(Edge.A * alpha), Edge), 0.08f);
        if (p.Blossom)
        {
            // Bông hoa 5 cánh + nhuỵ hồng đậm — nhìn nghiêng nên dẹt (co dọc còn 45%).
            g.ScaleTransform(p.Size * 0.5f * flip, p.Size * 0.5f * 0.45f);
            for (var i = 0; i < 5; i++)
            {
                var ps = g.Save();
                g.RotateTransform(i * 72);
                g.TranslateTransform(0, -1.05f);
                g.FillPath(fill, _petalPath);
                g.DrawPath(edge, _petalPath);
                g.Restore(ps);
            }
            using var heart = new SolidBrush(Color.FromArgb(a, 0xe0, 0x4a, 0x7a));
            g.FillEllipse(heart, -0.45f, -0.45f, 0.9f, 0.9f);
            using var dot = new SolidBrush(Color.FromArgb(a, 0xff, 0xe0, 0x70));
            g.FillEllipse(dot, -0.18f, -0.18f, 0.36f, 0.36f);
        }
        else
        {
            g.ScaleTransform(p.Size * 0.36f * flip, p.Size);   // cánh dẹt, thon dài (bề ngang ≈ 36% chiều dài)
            g.FillPath(fill, _petalPath);
            g.DrawPath(edge, _petalPath);
            using var vein = new Pen(Color.FromArgb((int)(70 * alpha), 0xe0, 0x5a, 0x86), 0.07f);
            g.DrawLine(vein, 0f, 0.85f, 0f, -0.5f);
        }
        g.Restore(st);
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _left.Dispose();
        _right.Dispose();
        _petalPath.Dispose();
    }

    /// <summary>1 dải sát mép: cửa sổ lớp + DIB vẽ + danh sách cánh hoa.</summary>
    private sealed class Strip : NativeWindow, IDisposable
    {
        private readonly PetalRain _r;
        private readonly bool _leftSide;
        private readonly List<Petal> _petals = new();
        private Rectangle _rect;
        private IntPtr _memDc, _dib, _oldBmp;
        private Bitmap? _bmp;
        private Size _dibSize;
        private bool _shown;

        public Strip(PetalRain r, bool leftSide) { _r = r; _leftSide = leftSide; }

        private void EnsureHandle()
        {
            if (Handle != IntPtr.Zero) return;
            CreateHandle(new CreateParams
            {
                Caption = "",
                Style = unchecked((int)0x80000000),                          // WS_POPUP
                ExStyle = 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000,   // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE
                Parent = _r._owner.Handle,                                    // owned bởi MainForm: luôn nằm trên nó, ẩn / hiện cùng nó
                Width = 1, Height = 1,
            });
        }

        public void Place(Rectangle rect)
        {
            if (rect.Width < 8 || rect.Height < 8) { Hide(); return; }
            if (rect.Size != _rect.Size)
            {
                // Đổi cỡ: giữ cánh hoa theo tỉ lệ, bỏ cánh nằm ngoài.
                _petals.RemoveAll(p => p.X > rect.Width + 10);
            }
            _rect = rect;
        }

        public void Step(float dt, int target)
        {
            if (_rect.Width < 8) return;
            int w = _rect.Width, h = _rect.Height;
            while (_petals.Count < target) _petals.Add(_r.NewPetal(w, h, top: _petals.Count >= target / 2 || _shown));
            if (_petals.Count > target) _petals.RemoveRange(target, _petals.Count - target);
            var k = _r.K;
            var wind = (_r._wind + _r._gust) * k * (_leftSide ? 1 : -1) * 0.6f;   // gió đẩy cánh vào trong một chút ở cả 2 bên
            foreach (var p in _petals)
            {
                p.SwayPh += p.SwayF * dt;
                p.Y += p.Vy * dt;
                p.X += (MathF.Cos(p.SwayPh) * p.Sway * p.SwayF + wind) * dt;
                p.Rot += p.RotV * dt;
                p.Flip += p.FlipV * dt;
                if (p.Y > h + 12 * k || p.X < -20 * k || p.X > w + 20 * k)
                {
                    var n = _r.NewPetal(w, h, top: true);
                    p.X = n.X; p.Y = n.Y; p.Vy = n.Vy; p.Size = n.Size; p.Color = n.Color; p.Blossom = n.Blossom; p.Sway = n.Sway; p.SwayF = n.SwayF;
                }
            }
            Render();
        }

        private void Render()
        {
            EnsureHandle();
            if (Handle == IntPtr.Zero) return;
            var size = _rect.Size;
            if (_bmp is null || _dibSize != size) MakeSurface(size);
            if (_bmp is null) return;
            using (var g = Graphics.FromImage(_bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.CompositingQuality = CompositingQuality.HighSpeed;
                foreach (var p in _petals)
                {
                    // Mờ dần về phía giữa màn hình: sát mép = đậm, mép trong dải = nhạt.
                    var fromEdge = _leftSide ? p.X / size.Width : 1 - p.X / size.Width;
                    var alpha = Math.Clamp(1 - fromEdge * fromEdge * 0.75f, 0.15f, 1f) * 0.92f;
                    // Vào / ra mép trên-dưới mượt.
                    if (p.Y < 0) alpha *= Math.Clamp(1 + p.Y / 20f, 0, 1);
                    _r.DrawPetal(g, p, alpha);
                }
            }
            var screen = GetDC(IntPtr.Zero);
            try
            {
                var sz = new SIZE { cx = size.Width, cy = size.Height };
                var src = new POINT();
                var dst = new POINT { x = _rect.X, y = _rect.Y };
                var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref dst, ref sz, _memDc, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
            if (!_shown) { ShowWindow(Handle, 4 /*SW_SHOWNOACTIVATE*/); _shown = true; }
        }

        private void MakeSurface(Size size)
        {
            FreeSurface();
            var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = size.Width, biHeight = -size.Height, biPlanes = 1, biBitCount = 32 };
            _dib = CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
            if (_dib == IntPtr.Zero) return;
            _memDc = CreateCompatibleDC(IntPtr.Zero);
            _oldBmp = SelectObject(_memDc, _dib);
            // Bitmap PArgb dùng CHÍNH bộ nhớ DIB: GDI+ vẽ xong là UpdateLayeredWindow đọc được ngay (alpha đã nhân sẵn — đúng AC_SRC_ALPHA).
            _bmp = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, bits);
            _dibSize = size;
        }

        private void FreeSurface()
        {
            _bmp?.Dispose(); _bmp = null;
            if (_memDc != IntPtr.Zero) { SelectObject(_memDc, _oldBmp); DeleteDC(_memDc); _memDc = IntPtr.Zero; }
            if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }
            _dibSize = Size.Empty;
        }

        public void Hide()
        {
            if (_shown && Handle != IntPtr.Zero) ShowWindow(Handle, 0);
            _shown = false;
            _petals.Clear();
            FreeSurface();   // tắt thì trả bộ nhớ ngay
        }

        public void Dispose()
        {
            FreeSurface();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    // ---------- Win32 ----------
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
