using System.Runtime.InteropServices;

namespace Bcode.App.UI;

/// <summary>
/// Line-number gutter tối ưu hiệu năng cao cho RichTextBox:
/// - Loại bỏ hoàn toàn _target.Lines để không cấp phát mảng chuỗi gây đơ RAM khi có hàng chục ngàn dòng.
/// - Chỉ quét và vẽ các dòng nằm trong vùng hiển thị (Viewport) thay vì lặp qua toàn bộ file.
/// </summary>
public class LineNumberGutter : Control
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wp, IntPtr lp);

    private const int EM_GETLINECOUNT = 0x00BA;
    private const int EM_GETFIRSTVISIBLELINE = 0x00CE;
    private const int EM_LINEINDEX = 0x00BB;
    private const int EM_CHARFROMPOS = 0x00D7;
    private const int EM_EXLINEFROMCHAR = 0x0436;

    private RichTextBox? _target;

    public HashSet<int>? BreakpointLines { get; set; }
    public int? CurrentLine { get; set; }

    public LineNumberGutter()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Width = 40;
        BackColor = AppColors.PanelAlt;
        ForeColor = AppColors.TextMuted;
    }

    public void Attach(RichTextBox target)
    {
        _target = target;
        _target.VScroll += (_, _) => Invalidate();
        _target.TextChanged += (_, _) => { UpdateWidth(); Invalidate(); };
        _target.FontChanged += (_, _) => { UpdateWidth(); Invalidate(); };
        _target.Resize += (_, _) => Invalidate();
        UpdateWidth();
    }

    public void RefreshMarkers() => Invalidate();

    private int GetTotalLineCount()
    {
        if (_target is null || !_target.IsHandleCreated) return 0;
        return (int)SendMessage(_target.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero);
    }

    private void UpdateWidth()
    {
        if (_target is null) return;
        var lineCount = GetTotalLineCount();
        var digits = Math.Max(2, lineCount.ToString().Length);
        var calculatedWidth = TextRenderer.MeasureText(new string('9', digits), _target.Font).Width + 16 + 14;
        if (Width != calculatedWidth)
        {
            Width = calculatedWidth;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_target is null || !_target.IsHandleCreated) return;

        using var back = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(back, ClientRectangle);

        var totalLines = GetTotalLineCount();
        if (totalLines <= 0) return;

        var lineHeight = _target.Font.Height;
        var dotDiameter = Math.Max(6, lineHeight / 2);

        // 1. Chỉ lấy dòng đầu tiên đang nhìn thấy
        var firstVisibleLine = (int)SendMessage(_target.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);

        // 2. Tính số lượng dòng có thể vừa vặn trên chiều cao của cửa sổ
        var visibleCount = (Height / lineHeight) + 2;
        var lastVisibleLine = Math.Min(totalLines - 1, firstVisibleLine + visibleCount);

        using var breakpointBrush = new SolidBrush(Color.FromArgb(220, 80, 80));
        using var currentLineBrush = new SolidBrush(AppColors.Accent);

        // CHỈ LẶP QUA CÁC DÒNG HIỂN THỊ TRÊN MÀN HÌNH (khoảng 30 - 60 dòng)
        for (var i = firstVisibleLine; i <= lastVisibleLine; i++)
        {
            var charIndex = (int)SendMessage(_target.Handle, EM_LINEINDEX, (IntPtr)i, IntPtr.Zero);
            if (charIndex < 0) continue;

            var pos = _target.GetPositionFromCharIndex(charIndex);
            if (pos.Y > Height) break; // Vượt quá cạnh dưới thì dừng ngay

            var lineNumber = i + 1;
            var isCurrent = CurrentLine == lineNumber;

            if (isCurrent)
            {
                e.Graphics.FillRectangle(currentLineBrush, new Rectangle(0, pos.Y, Width, lineHeight));
            }
            else if (BreakpointLines is { Count: > 0 } && BreakpointLines.Contains(lineNumber))
            {
                var dotY = pos.Y + (lineHeight - dotDiameter) / 2;
                e.Graphics.FillEllipse(breakpointBrush, new Rectangle(2, dotY, dotDiameter, dotDiameter));
            }

            var textColor = isCurrent ? Color.Black : ForeColor;
            TextRenderer.DrawText(e.Graphics, lineNumber.ToString(), _target.Font,
                new Rectangle(14, pos.Y, Width - 6 - 14, lineHeight + 2),
                textColor, TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }
    }
}