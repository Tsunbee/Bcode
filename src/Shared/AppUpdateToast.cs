using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Bcode.Shared;

/// <summary>Màu của thông báo — lấy từ theme của app gọi.</summary>
public sealed record ToastPalette(Color Back, Color Text, Color Muted, Color Accent, Color Border);

/// <summary>
/// Thông báo "Có bản &lt;app&gt; mới" KHÔNG tự tắt (dùng chung Bcode / BcodeViewer / Screen Designer): hộp nhỏ nổi ở góc dưới phải cửa sổ chính,
/// đi theo khi cửa sổ di chuyển / đổi cỡ, ẩn theo khi thu nhỏ, không giành focus. Nút <b>Xem</b> = mở hộp Cập nhật; <b>Để sau</b> / ✕ = ẩn,
/// tự hiện lại sau <see cref="SnoozeFor"/> nếu vẫn chưa cập nhật. Là form con (owned) nên vẽ được đè lên WebView2.
/// </summary>
public sealed class AppUpdateToast : IDisposable
{
    public static readonly TimeSpan SnoozeFor = TimeSpan.FromHours(4);

    private readonly Form _owner;
    private readonly Func<ToastPalette> _palette;
    private readonly Action _onView;
    private Popup? _popup;
    private System.Windows.Forms.Timer? _snooze;
    private string _title = "", _detail = "";

    /// <param name="owner">Cửa sổ chính.</param>
    /// <param name="palette">Màu hiện hành (đọc lại mỗi lần hiện — đổi theme vẫn đúng).</param>
    /// <param name="onView">Bấm "Xem": mở hộp Cập nhật.</param>
    public AppUpdateToast(Form owner, Func<ToastPalette> palette, Action onView)
    {
        _owner = owner; _palette = palette; _onView = onView;
        owner.Move += (_, _) => Place();
        owner.Resize += (_, _) => Place();
        owner.FormClosed += (_, _) => Dispose();
    }

    /// <summary>Đang có thông báo (hiện hoặc đang tạm ẩn chờ hiện lại).</summary>
    public bool Active { get; private set; }

    /// <summary>Hiện (hoặc cập nhật nội dung) thông báo.</summary>
    public void Show(string title, string detail)
    {
        if (_owner.IsDisposed) return;
        if (_owner.InvokeRequired) { _owner.BeginInvoke(() => Show(title, detail)); return; }
        _title = title; _detail = detail; Active = true;
        _snooze?.Stop();
        _popup?.Dispose();
        _popup = new Popup(_palette(), title, detail, View, Snooze, _owner.DeviceDpi);
        Place();   // đặt chỗ + hiện (cửa sổ đang thu nhỏ thì hiện khi mở lại — owner.Resize → Place)
    }

    /// <summary>Bỏ hẳn (đã mở hộp Cập nhật / đã cập nhật / đổi thư mục phát hành).</summary>
    public void Clear()
    {
        if (_owner.InvokeRequired) { _owner.BeginInvoke(Clear); return; }
        Active = false;
        _snooze?.Stop();
        _popup?.Dispose(); _popup = null;
    }

    /// <summary>Đã xem hộp Cập nhật nhưng chưa cập nhật: không hiện ngay, nhắc lại sau <see cref="SnoozeFor"/>.</summary>
    public void Later(string title, string detail)
    {
        if (_owner.IsDisposed) return;
        _title = title; _detail = detail; Active = true;
        Snooze();
    }

    private void View() { Clear(); _onView(); }

    private void Snooze()
    {
        _popup?.Dispose(); _popup = null;
        _snooze ??= new System.Windows.Forms.Timer { Interval = (int)SnoozeFor.TotalMilliseconds };
        _snooze.Tick -= OnSnoozeTick; _snooze.Tick += OnSnoozeTick;
        _snooze.Stop(); _snooze.Start();
    }

    private void OnSnoozeTick(object? s, EventArgs e) { _snooze?.Stop(); if (Active) Show(_title, _detail); }

    private void Place()
    {
        if (_popup is null || _popup.IsDisposed || _owner.IsDisposed) return;
        if (_owner.WindowState == FormWindowState.Minimized) return;
        var area = _owner.RectangleToScreen(_owner.ClientRectangle);
        var m = (int)Math.Round(16 * _owner.DeviceDpi / 96.0);
        var bottom = (int)Math.Round(34 * _owner.DeviceDpi / 96.0);   // chừa thanh trạng thái
        _popup.Location = new Point(area.Right - _popup.Width - m, area.Bottom - _popup.Height - bottom);
        if (!_popup.Visible && _owner.Visible) try { _popup.Show(_owner); } catch { /* owner đang đóng */ }
    }

    public void Dispose()
    {
        _snooze?.Dispose(); _snooze = null;
        _popup?.Dispose(); _popup = null;
    }

    /// <summary>Cửa sổ nổi không viền, không giành focus, không hiện trên taskbar.</summary>
    private sealed class Popup : Form
    {
        private readonly ToastPalette _p;
        private readonly float _s;

        public Popup(ToastPalette p, string title, string detail, Action view, Action later, int dpi)
        {
            _p = p; _s = dpi / 96f;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = p.Back;
            ForeColor = p.Text;
            DoubleBuffered = true;
            int S(int v) => (int)Math.Round(v * _s);
            Size = new Size(S(360), S(112));
            Padding = new Padding(S(1));

            var titleFont = new Font("Segoe UI Semibold", 10f);
            var bodyFont = new Font("Segoe UI", 9f);
            var icon = new Label
            {
                Text = "⬆", Font = new Font("Segoe UI Symbol", 13f, FontStyle.Bold), ForeColor = p.Accent, BackColor = Color.Transparent,
                Location = new Point(S(14), S(12)), Size = new Size(S(26), S(28)), TextAlign = ContentAlignment.MiddleCenter,
            };
            var t = new Label
            {
                Text = title, Font = titleFont, ForeColor = p.Text, BackColor = Color.Transparent, AutoEllipsis = true,
                Location = new Point(S(46), S(12)), Size = new Size(S(280), S(22)),
            };
            var d = new Label
            {
                Text = detail, Font = bodyFont, ForeColor = p.Muted, BackColor = Color.Transparent, AutoEllipsis = true,
                Location = new Point(S(46), S(34)), Size = new Size(S(300), S(36)),
            };
            var close = new Label
            {
                Text = "✕", Font = bodyFont, ForeColor = p.Muted, BackColor = Color.Transparent, Cursor = Cursors.Hand,
                Location = new Point(S(334), S(8)), Size = new Size(S(18), S(18)), TextAlign = ContentAlignment.MiddleCenter,
            };
            var bView = MakeButton("Xem", primary: true);
            var bLater = MakeButton("Để sau", primary: false);
            bView.Size = bLater.Size = new Size(S(84), S(28));
            bView.Location = new Point(S(360 - 14 - 84), S(74));
            bLater.Location = new Point(S(360 - 14 - 84 - 8 - 84), S(74));
            bView.Click += (_, _) => view();
            bLater.Click += (_, _) => later();
            close.Click += (_, _) => later();
            // bấm vào chữ cũng = Xem
            foreach (var c in new Control[] { icon, t, d }) { c.Cursor = Cursors.Hand; c.Click += (_, _) => view(); }
            var tip = new ToolTip();
            tip.SetToolTip(d, detail);
            tip.SetToolTip(close, "Để sau (nhắc lại sau " + SnoozeFor.TotalHours + " giờ)");
            Controls.AddRange(new Control[] { icon, t, d, close, bView, bLater });
        }

        private Button MakeButton(string text, bool primary)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9f, primary ? FontStyle.Bold : FontStyle.Regular),
                BackColor = primary ? _p.Accent : _p.Back, ForeColor = primary ? (Brightness(_p.Accent) > 0.6 ? Color.FromArgb(27, 28, 31) : Color.White) : _p.Text,
                TabStop = false,
            };
            b.FlatAppearance.BorderColor = primary ? _p.Accent : _p.Border;
            b.FlatAppearance.BorderSize = 1;
            return b;
        }

        private static double Brightness(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080;   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
                cp.ClassStyle |= 0x00020000;              // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            using var pen = new Pen(_p.Border);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using var bar = new SolidBrush(_p.Accent);
            g.FillRectangle(bar, 0, 0, Math.Max(3, (int)Math.Round(3 * _s)), Height);   // vạch nhấn bên trái
        }
    }
}
