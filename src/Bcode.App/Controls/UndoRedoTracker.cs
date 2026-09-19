namespace Bcode.App.Controls;

/// <summary>
/// Custom Undo/Redo for a syntax-highlighted RichTextBox, replacing the box's native Undo
/// (which is permanently disabled via <see cref="SqlSyntaxHighlighter.DisableNativeUndo"/> —
/// see that method's doc-comment for why the native mechanism can't be used here: the
/// EM_SETUNDOLIMIT trick meant to keep highlighting out of the Undo queue turned out to
/// clear the *entire* queue — including the user's real edits — every time it ran).
///
/// Tracks plain-text checkpoints instead of individual keystrokes: one snapshot per "pause
/// in typing" (the same debounce boundary the highlighter itself uses to re-color), which
/// also reads as a more natural undo granularity than raw per-character native undo.
/// </summary>
public sealed class UndoRedoTracker
{
    private readonly RichTextBox _box;
    private readonly Stack<string> _undo = new();
    private readonly Stack<string> _redo = new();
    private string _baseline;
    private bool _suppress;

    public UndoRedoTracker(RichTextBox box)
    {
        _box = box;
        _baseline = box.Text;
    }

    /// <summary>Call on every highlight-debounce tick (i.e. roughly once per pause in typing),
    /// and right after any programmatic text mutation the user should be able to undo
    /// (Comment/Uncomment, Default Type, Write Schema insert, ...). Checkpoints whatever edit
    /// just happened, if the text actually changed since the last checkpoint.</summary>
    public void Checkpoint()
    {
        if (_suppress) return;
        if (_box.Text == _baseline) return;
        _undo.Push(_baseline);
        _redo.Clear();
        _baseline = _box.Text;
    }

    /// <summary>Call when content is replaced wholesale and the previous content shouldn't be
    /// reachable via Undo (Open, New) — this becomes a fresh starting point instead.</summary>
    public void ResetBaseline()
    {
        _baseline = _box.Text;
        _undo.Clear();
        _redo.Clear();
    }

    public void Undo()
    {
        Checkpoint(); // flush an in-progress (not yet debounced) burst so it becomes its own step
        if (_undo.Count == 0) return;
        _redo.Push(_box.Text);
        Set(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_box.Text);
        Set(_redo.Pop());
    }

    private void Set(string text)
    {
        _suppress = true;
        try
        {
            var caret = Math.Min(_box.SelectionStart, text.Length);
            _box.Text = text;
            _box.SelectionStart = caret;
            _box.SelectionLength = 0;
            _baseline = text;
            SqlSyntaxHighlighter.Apply(_box);
        }
        finally
        {
            _suppress = false;
        }
    }
}
