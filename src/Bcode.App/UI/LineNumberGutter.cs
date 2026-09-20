namespace Bcode.App.UI;

/// <summary>
/// Line-number gutter for a RichTextBox — plain RichTextBox has no line-number support built
/// in, unlike a real code editor (Monaco in BcodeViewer, VSCode, etc.). Dock this to the
/// Left of the RichTextBox it's paired with and call Attach once the RichTextBox exists.
/// Assumes WordWrap is off on the target (RawSqlControl's script box already sets this) —
/// with wrapping off, one physical line is exactly one visual line, which keeps the
/// Y-position math below correct without tracking wrapped-line counts separately.
/// </summary>
public class LineNumberGutter : Control
{
    private RichTextBox? _target;

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

    private void UpdateWidth()
    {
        if (_target is null) return;
        var digits = Math.Max(2, _target.Lines.Length.ToString().Length);
        Width = TextRenderer.MeasureText(new string('9', digits), _target.Font).Width + 16;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_target is null) return;

        using var back = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(back, ClientRectangle);

        var lineCount = _target.Lines.Length;
        var lineHeight = _target.Font.Height;
        for (var i = 0; i < lineCount; i++)
        {
            var charIndex = _target.GetFirstCharIndexFromLine(i);
            if (charIndex < 0) continue;
            var pos = _target.GetPositionFromCharIndex(charIndex);
            if (pos.Y < -lineHeight || pos.Y > Height) continue; // skip lines scrolled out of view

            TextRenderer.DrawText(e.Graphics, (i + 1).ToString(), _target.Font,
                new Rectangle(0, pos.Y, Width - 6, lineHeight + 2),
                ForeColor, TextFormatFlags.Right | TextFormatFlags.Top | TextFormatFlags.NoPadding);
        }
    }
}
