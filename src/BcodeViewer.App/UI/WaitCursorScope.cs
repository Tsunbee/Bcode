namespace BcodeViewer.App.UI;

/// <summary>
/// Shows the hourglass for the duration of a <c>using</c> block, and puts the previous
/// cursor back however the block exits.
///
/// Used around the awaits that replaced blocking loads of the shared template/snippet
/// folder (MainForm.NewFromTemplate, MainForm.OpenHintCode). Those no longer freeze the
/// window, which is the point — but "no longer frozen" also means nothing on screen says
/// the app is busy, and a menu click that appears to do nothing for two seconds reads as a
/// bug. This is the smallest honest signal: the window stays live and the pointer says wait.
/// </summary>
internal sealed class WaitCursorScope : IDisposable
{
    private readonly Form _form;
    private readonly Cursor _previous;

    public WaitCursorScope(Form form)
    {
        _form = form;
        _previous = form.Cursor;
        form.Cursor = Cursors.WaitCursor;
    }

    public void Dispose()
    {
        // The form can be gone by the time the awaited work comes back.
        if (!_form.IsDisposed) _form.Cursor = _previous;
    }
}
