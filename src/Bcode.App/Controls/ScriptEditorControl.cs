namespace Bcode.App.Controls;

/// <summary>
/// Right-hand script/content viewer-editor (used for SQL Object scripts, File
/// Lookup file contents, etc.). A RichTextBox with basic SQL syntax
/// highlighting (see SqlSyntaxHighlighter) instead of a flat monochrome
/// TextBox — enough to view, edit and save .f / .xml / .sql / .aspx text
/// content while reading closer to FCode's own colored script viewer.
/// </summary>
public class ScriptEditorControl : UserControl
{
    private readonly RichTextBox _textBox;
    private readonly Panel _topBar;
    private readonly Label _pathLabel;
    private readonly Button _hideBarButton;
    private readonly System.Windows.Forms.Timer _highlightDebounce;

    public string? CurrentPath { get; private set; }
    public bool IsDirty { get; private set; }

    public event Action? DirtyChanged;

    /// <summary>Raised when the user clicks ✕ to hide the "(nội dung tạm)" bar —
    /// MainForm listens to this to remember the choice (AppSettings.ShowTempContentBar)
    /// so newly opened generated-content tabs stay collapsed too.</summary>
    public event Action? TempBarHidden;

    public ScriptEditorControl()
    {
        Dock = DockStyle.Fill;

        _pathLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            AutoEllipsis = true,
            Text = "(chưa mở file nào)"
        };

        _hideBarButton = new Button
        {
            Text = "✕",
            Dock = DockStyle.Right,
            Width = 24,
            FlatStyle = FlatStyle.Flat,
            TabStop = false
        };
        _hideBarButton.FlatAppearance.BorderSize = 0;
        _hideBarButton.Click += (_, _) =>
        {
            ShowPathBar = false;
            TempBarHidden?.Invoke();
        };
        var hideTip = new ToolTip();
        hideTip.SetToolTip(_hideBarButton, "Ẩn dòng này (áp dụng cho các tab nội dung tạm mở sau này)");

        _topBar = new Panel { Dock = DockStyle.Top, Height = 22 };
        _topBar.Controls.Add(_pathLabel);
        _topBar.Controls.Add(_hideBarButton);

        _textBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 10f),
            AcceptsTab = true
        };

        // Re-highlight is debounced (not on every keystroke) so typing in a long
        // script doesn't lag from re-running the regex passes on every character.
        _highlightDebounce = new System.Windows.Forms.Timer { Interval = 400 };
        _highlightDebounce.Tick += (_, _) =>
        {
            _highlightDebounce.Stop();
            if (_textBox.IsDisposed) return;
            SqlSyntaxHighlighter.Apply(_textBox);
        };
        // Fix: "Cannot access a disposed object (RichTextBox)" — closing a tab right after
        // typing (e.g. via View Script -> close) disposed the RichTextBox while this Timer
        // still had a pending 400ms Tick queued; the Timer itself isn't a child Control so
        // TabPage.Dispose() never stopped it on its own. Stop/dispose it explicitly here.
        Disposed += (_, _) => { _highlightDebounce.Stop(); _highlightDebounce.Dispose(); };

        _textBox.TextChanged += (_, _) =>
        {
            IsDirty = true;
            DirtyChanged?.Invoke();
            _highlightDebounce.Stop();
            _highlightDebounce.Start();
        };
        // LoadContent is normally called right after `new ScriptEditorControl()`,
        // before this control is added to the visible tree (no window handle yet,
        // which SqlSyntaxHighlighter needs for its WM_SETREDRAW call) — so also
        // catch up once the handle exists.
        _textBox.HandleCreated += (_, _) => SqlSyntaxHighlighter.Apply(_textBox);

        Controls.Add(_textBox);
        Controls.Add(_topBar);
    }

    /// <summary>Show/hide the top bar (path, or "(nội dung tạm)" for generated content).
    /// The ✕ button only makes sense to offer for temp/generated content, so it's hidden
    /// once a real file path is loaded and it doesn't apply there.</summary>
    public bool ShowPathBar
    {
        get => _topBar.Visible;
        set => _topBar.Visible = value;
    }

    public void LoadContent(string? path, string content)
    {
        CurrentPath = path;
        _pathLabel.Text = path ?? "(nội dung tạm)";
        _hideBarButton.Visible = path is null; // only offer to hide for generated/temp content
        _textBox.Text = content;
        IsDirty = false;
        DirtyChanged?.Invoke();
        _highlightDebounce.Stop();
        if (_textBox.IsHandleCreated) SqlSyntaxHighlighter.Apply(_textBox);
        // else: the _textBox.HandleCreated subscription in the constructor will catch up.
    }

    public string Content
    {
        get => _textBox.Text;
        set { _textBox.Text = value; IsDirty = true; DirtyChanged?.Invoke(); }
    }

    public void MarkSaved()
    {
        IsDirty = false;
        DirtyChanged?.Invoke();
    }

    public void Clear()
    {
        CurrentPath = null;
        _pathLabel.Text = "(chưa mở file nào)";
        _textBox.Clear();
        IsDirty = false;
        DirtyChanged?.Invoke();
    }

    public void CopyToClipboard()
    {
        if (!string.IsNullOrEmpty(_textBox.Text))
            Clipboard.SetText(_textBox.Text);
    }
}
