using System.Drawing.Drawing2D;

namespace Bcode.Chat;

/// <summary>
/// Thông báo trượt lên ở góc phải dưới màn hình như messenger ngày xưa ("X vừa online", tin mới…). Tự vẽ bằng GDI, không giành focus,
/// nhiều cái thì xếp chồng lên nhau; để chuột lên thì đứng lại; bấm vào thì chạy <c>onClick</c> (mở cửa sổ chat).
/// </summary>
internal sealed class Popup : Form
{
    private const int W = 260, H = 92, Gap = 6, ShowMs = 6000;
    private static readonly List<Popup> Open = new();

    private readonly string _title, _text, _caption;
    private readonly Image? _avatar;
    private readonly Color _color;
    private readonly char _letter;
    private readonly Action? _onClick;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 15 };
    private int _targetY, _life;
    private bool _closing, _hover;

    /// <param name="caption">Dòng nhỏ trên thanh tiêu đề ("Bạn bè online", "Tin nhắn mới"…).</param>
    /// <param name="avatarPath">Ảnh đại diện (không có → chữ cái đầu trên nền <paramref name="color"/>).</param>
    public static void Show(string caption, string title, string text, string? avatarPath, string color, Action? onClick)
    {
        // tối đa 4 cái cùng lúc — cái cũ nhất nhường chỗ
        while (Open.Count >= 4) Open[0].Dismiss();
        var p = new Popup(caption, title, text, avatarPath, color, onClick);
        Open.Add(p);
        p.Reflow(initial: true);
        p.Show();
    }

    private Popup(string caption, string title, string text, string? avatarPath, string color, Action? onClick)
    {
        _caption = caption;
        _title = title;
        _text = text.Length > 120 ? text[..117] + "…" : text;
        _onClick = onClick;
        _letter = title.Length > 0 ? char.ToUpperInvariant(title[0]) : '?';
        try { _color = ColorTranslator.FromHtml(color); } catch { _color = Color.FromArgb(58, 110, 165); }
        if (avatarPath is not null && File.Exists(avatarPath))
            try { using var fs = File.OpenRead(avatarPath); _avatar = Image.FromStream(fs); } catch { }   // đọc vào bộ nhớ — không khoá file

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = new Size(W, H);
        DoubleBuffered = true;
        Cursor = Cursors.Hand;
        Font = new Font("Tahoma", 8.25f);
        _anim.Tick += (_, _) => Step();
        MouseEnter += (_, _) => _hover = true;
        MouseLeave += (_, _) => _hover = false;
        MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && !CloseBox.Contains(e.Location)) _onClick?.Invoke();
            Dismiss();
        };
        FormClosed += (_, _) => { _anim.Dispose(); _avatar?.Dispose(); Open.Remove(this); foreach (var o in Open) o.Reflow(false); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08 | 0x80 | 0x08000000;   // TOPMOST | TOOLWINDOW (không hiện Alt+Tab) | NOACTIVATE
            cp.ClassStyle |= 0x20000;                 // CS_DROPSHADOW
            return cp;
        }
    }

    private static Rectangle Area => Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
    private Rectangle CloseBox => new(W - 18, 3, 14, 14);

    /// <summary>Tính lại chỗ đứng (cái mới nhất ở dưới cùng). Lần đầu thì bắt đầu từ dưới mép màn hình rồi trượt lên.</summary>
    private void Reflow(bool initial)
    {
        var idx = Open.Count - 1 - Open.IndexOf(this);
        _targetY = Area.Bottom - (H + Gap) * (idx + 1);
        if (initial) Location = new Point(Area.Right - W - 10, Area.Bottom);
        _anim.Start();
    }

    private void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _anim.Start();
    }

    private void Step()
    {
        if (_closing)
        {
            Opacity -= 0.12;
            if (Opacity <= 0.05) Close();
            return;
        }
        if (Top != _targetY)
        {
            var d = _targetY - Top;
            Top += Math.Sign(d) * Math.Max(1, Math.Abs(d) / 4);
        }
        if (!_hover) _life += _anim.Interval;
        if (_life > ShowMs) Dismiss();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        // khung + thanh tiêu đề xanh kiểu XP
        using (var bg = new LinearGradientBrush(ClientRectangle, Color.White, Color.FromArgb(232, 238, 248), 90f)) g.FillRectangle(bg, ClientRectangle);
        var bar = new Rectangle(0, 0, W, 20);
        using (var hb = new LinearGradientBrush(bar, Color.FromArgb(77, 139, 222), Color.FromArgb(31, 72, 130), 90f)) g.FillRectangle(hb, bar);
        using (var pen = new Pen(Color.FromArgb(15, 42, 82))) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);
        using var bold = new Font("Tahoma", 8.25f, FontStyle.Bold);
        TextRenderer.DrawText(g, "Bhoo  ·  " + _caption, bold, new Rectangle(6, 2, W - 30, 16), Color.White, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "×", bold, CloseBox, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        // ảnh đại diện
        var pic = new Rectangle(10, 28, 54, 54);
        if (_avatar is not null) g.DrawImage(_avatar, pic);
        else
        {
            using var pb = new LinearGradientBrush(pic, _color, ControlPaint.Dark(_color, .3f), 45f);
            g.FillRectangle(pb, pic);
            using var big = new Font("Tahoma", 22f, FontStyle.Bold);
            TextRenderer.DrawText(g, _letter.ToString(), big, pic, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        using (var pp = new Pen(Color.FromArgb(15, 42, 82))) g.DrawRectangle(pp, pic);

        var tx = new Rectangle(72, 27, W - 80, 16);
        using var titleFont = new Font("Tahoma", 9f, FontStyle.Bold);
        TextRenderer.DrawText(g, _title, titleFont, tx, Color.FromArgb(31, 72, 130), TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, _text, Font, new Rectangle(72, 45, W - 80, 40), Color.FromArgb(34, 34, 34),
            TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}
