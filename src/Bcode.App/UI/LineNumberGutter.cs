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

    /// <summary>1-based line numbers to draw a breakpoint dot next to — the debugger's "safe to
    /// stop here" set (see SqlLineAnalyzer.FindSafeLines) while previewing/choosing a
    /// breakpoint, or the actual set of user-placed breakpoints once a debug session is
    /// running. Null/empty draws no dots at all (the gutter's original, pre-debug look).</summary>
    public HashSet<int>? BreakpointLines { get; set; }

    /// <summary>1-based line the debugger is currently paused at, or null when no debug session
    /// is active — drawn as a highlighted arrow/marker distinct from a plain breakpoint dot,
    /// matching the yellow current-line highlight in FCode/SQLdebug.exe's own screenshots.</summary>
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

    /// <summary>Repaints to reflect a new BreakpointLines/CurrentLine value — call after
    /// changing either property (they don't auto-invalidate on their own, since they're plain
    /// auto-properties, not something that can raise a changed event).</summary>
    public void RefreshMarkers() => Invalidate();

    private void UpdateWidth()
    {
        if (_target is null) return;
        var digits = Math.Max(2, _target.Lines.Length.ToString().Length);
        // +14 reserves room for the breakpoint dot to the left of the number itself, beyond the
        // existing text-measurement padding — a no-op visually when BreakpointLines is unset.
        Width = TextRenderer.MeasureText(new string('9', digits), _target.Font).Width + 16 + 14;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_target is null) return;

        using var back = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(back, ClientRectangle);

        var lineCount = _target.Lines.Length;
        var lineHeight = _target.Font.Height;
        var dotDiameter = Math.Max(6, lineHeight / 2);

        using var breakpointBrush = new SolidBrush(Color.FromArgb(220, 80, 80));
        using var currentLineBrush = new SolidBrush(AppColors.Accent);

        for (var i = 0; i < lineCount; i++)
        {
            var charIndex = _target.GetFirstCharIndexFromLine(i);
            if (charIndex < 0) continue;
            var pos = _target.GetPositionFromCharIndex(charIndex);
            if (pos.Y < -lineHeight || pos.Y > Height) continue; // skip lines scrolled out of view

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
