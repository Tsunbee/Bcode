using System.Text.RegularExpressions;
using Bcode.App.UI;

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

    // Highlighting now assigns box.Rtf directly (see SqlSyntaxHighlighter) instead of the
    // old per-match SelectionColor calls — but unlike those, setting .Rtf DOES raise
    // TextChanged. Without this guard, that TextChanged handler below would restart the
    // highlight debounce, which reassigns .Rtf again 400ms later, which fires TextChanged
    // again — an infinite highlight loop, and since assigning .Rtf resets the scroll
    // position, the visible symptom was exactly "scroll down, and it keeps snapping back
    // to the top." This flag makes ApplyHighlight()'s own .Rtf assignment invisible to
    // that handler.
    private bool _suppressTextChanged;

    // name -> SYSTEM path, parsed out of this file's own <!ENTITY name SYSTEM "path"> /
    // <!ENTITY % name SYSTEM "path"> declarations, so F12 on a usage elsewhere in the
    // same file (e.g. &XMLWhenVoucherInit; or %CheckSerialNumber;) can resolve it.
    private readonly Dictionary<string, string> _entityDeclarations = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex EntityDeclRegex = new(
        @"<!ENTITY\s+%?\s*([A-Za-z0-9_]+)\s+SYSTEM\s+""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public string? CurrentPath { get; private set; }
    public bool IsDirty { get; private set; }

    /// <summary>When true, the content is view-only (used by File Lookup's inline
    /// preview, which defaults to read-only until the user opts into "Edit Mode").</summary>
    public bool ReadOnly
    {
        get => _textBox.ReadOnly;
        set => _textBox.ReadOnly = value;
    }

    public event Action? DirtyChanged;

    /// <summary>Raised when the user clicks ✕ to hide the "(nội dung tạm)" bar —
    /// MainForm listens to this to remember the choice (AppSettings.ShowTempContentBar)
    /// so newly opened generated-content tabs stay collapsed too.</summary>
    public event Action? TempBarHidden;

    /// <summary>Raised when F12 is pressed on a resolvable entity reference (its
    /// declaration's SYSTEM path resolves to a file that exists on disk) — the caller
    /// decides how to show it (open a tab, or navigate File Lookup's own preview).</summary>
    public event Action<string>? EntityNavigationRequested;

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
            AcceptsTab = true,
            DetectUrls = false // avoids an extra scan over large pasted/loaded content
        };

        // Re-highlight is debounced (not on every keystroke) so typing in a long
        // script doesn't lag from re-running the regex passes on every character.
        _highlightDebounce = new System.Windows.Forms.Timer { Interval = 400 };
        _highlightDebounce.Tick += (_, _) =>
        {
            _highlightDebounce.Stop();
            if (_textBox.IsDisposed) return;
            ApplyHighlight();
        };
        // Fix: "Cannot access a disposed object (RichTextBox)" — closing a tab right after
        // typing (e.g. via View Script -> close) disposed the RichTextBox while this Timer
        // still had a pending 400ms Tick queued; the Timer itself isn't a child Control so
        // TabPage.Dispose() never stopped it on its own. Stop/dispose it explicitly here.
        Disposed += (_, _) => { _highlightDebounce.Stop(); _highlightDebounce.Dispose(); };

        _textBox.TextChanged += (_, _) =>
        {
            if (_suppressTextChanged) return; // our own ApplyHighlight() reassigning .Rtf — not a real edit
            IsDirty = true;
            DirtyChanged?.Invoke();
            _highlightDebounce.Stop();
            _highlightDebounce.Start();
        };
        // LoadContent is normally called right after `new ScriptEditorControl()`,
        // before this control is added to the visible tree (no window handle yet,
        // which SqlSyntaxHighlighter needs for its WM_SETREDRAW call) — so also
        // catch up once the handle exists.
        _textBox.HandleCreated += (_, _) => ApplyHighlight();

        _textBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F12)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                TryNavigateToEntityAtCaret();
            }
            else if (e.Control && e.KeyCode == Keys.G)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                ShowGoToDialog();
            }
        };

        Controls.Add(_textBox);
        Controls.Add(_topBar);
    }

    private void ApplyHighlight()
    {
        _suppressTextChanged = true;
        try
        {
            SqlSyntaxHighlighter.Apply(_textBox);
        }
        finally
        {
            _suppressTextChanged = false;
        }
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
        if (_textBox.IsHandleCreated) ApplyHighlight();
        // else: the _textBox.HandleCreated subscription in the constructor will catch up.

        _entityDeclarations.Clear();
        foreach (Match m in EntityDeclRegex.Matches(content))
            _entityDeclarations[m.Groups[1].Value] = m.Groups[2].Value;
    }

    /// <summary>F12: resolves the entity name under the caret against this file's own
    /// ENTITY declarations (see <see cref="_entityDeclarations"/>) and, if it points at a
    /// file that actually exists, raises <see cref="EntityNavigationRequested"/> with the
    /// resolved full path. Silently does nothing if there's no file context, no entity
    /// name at the caret, or the declared path doesn't resolve to a real file.</summary>
    private void TryNavigateToEntityAtCaret()
    {
        if (CurrentPath is null) return; // nothing to resolve a relative Include path against

        var name = GetWordAt(_textBox.Text, _textBox.SelectionStart);
        if (string.IsNullOrEmpty(name) || !_entityDeclarations.TryGetValue(name, out var relPath)) return;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(CurrentPath)!, relPath.Replace('/', '\\')));
        }
        catch (Exception)
        {
            return; // malformed path in the declaration
        }
        if (File.Exists(fullPath))
            EntityNavigationRequested?.Invoke(fullPath);
    }

    // ---- Ctrl+G "Go to" — structural navigation for FastBusiness Dir/Grid XML files ----
    // (<fields>, <views>, <commands><command event="...">, <script> function names,
    // <response><action id="...">, and DOCTYPE <!ENTITY> declarations) — matches FCode's
    // own "Go to" dialog: a TAG list of section kinds on the left, and on the right the
    // named items within whichever TAG is selected; picking one jumps the caret there.

    private static readonly Regex FieldNameRegex = new(@"<field\s+name=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex ViewIdRegex = new(@"<view\s+id=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex CommandEventRegex = new(@"<command\s+event=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex ScriptFunctionRegex = new(@"function\s+([A-Za-z0-9_$]+)\s*\(", RegexOptions.Compiled);
    private static readonly Regex ResponseActionRegex = new(@"<action\s+id=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex EntityNameRegex = new(@"<!ENTITY\s+%?\s*([A-Za-z0-9_.]+)", RegexOptions.Compiled);

    // Preferred display order — matches the order FCode's own dialog lists them in;
    // any other category (there shouldn't normally be one) is appended after.
    private static readonly string[] GoToCategoryOrder = { "fields", "views", "command", "script", "response", "ENTITY" };

    private readonly record struct GoToItem(string Label, int Position);

    private Dictionary<string, List<GoToItem>> BuildGoToIndex()
    {
        var text = _textBox.Text;
        var result = new Dictionary<string, List<GoToItem>>();

        void Add(string category, string label, int pos)
        {
            if (!result.TryGetValue(category, out var list)) result[category] = list = new List<GoToItem>();
            list.Add(new GoToItem(label, pos));
        }

        foreach (Match m in FieldNameRegex.Matches(text)) Add("fields", m.Groups[1].Value, m.Index);

        foreach (Match m in ViewIdRegex.Matches(text)) Add("views", m.Groups[1].Value, m.Index);
        var categoriesIdx = text.IndexOf("<categories", StringComparison.OrdinalIgnoreCase);
        if (categoriesIdx >= 0) Add("views", "Categories", categoriesIdx);
        var endViewsIdx = text.IndexOf("</views>", StringComparison.OrdinalIgnoreCase);
        if (endViewsIdx >= 0) Add("views", "End Views", endViewsIdx);

        foreach (Match m in CommandEventRegex.Matches(text)) Add("command", m.Groups[1].Value, m.Index);
        foreach (Match m in ScriptFunctionRegex.Matches(text)) Add("script", m.Groups[1].Value, m.Index);
        foreach (Match m in ResponseActionRegex.Matches(text)) Add("response", m.Groups[1].Value, m.Index);
        foreach (Match m in EntityNameRegex.Matches(text)) Add("ENTITY", m.Groups[1].Value, m.Index);

        return result;
    }

    /// <summary>Which category the caret currently sits inside, going by which top-level
    /// section tag started most recently before it — used to default-select that TAG (and
    /// mark it with "●") when the dialog opens, like FCode's own does.</summary>
    private string? CurrentGoToCategory()
    {
        var text = _textBox.Text;
        var caret = _textBox.SelectionStart;
        (string Cat, int Start)[] sections =
        {
            ("fields", text.IndexOf("<fields", StringComparison.OrdinalIgnoreCase)),
            ("views", text.IndexOf("<views", StringComparison.OrdinalIgnoreCase)),
            ("command", text.IndexOf("<commands", StringComparison.OrdinalIgnoreCase)),
            ("script", text.IndexOf("<script", StringComparison.OrdinalIgnoreCase)),
            ("response", text.IndexOf("<response", StringComparison.OrdinalIgnoreCase)),
        };
        return sections.Where(s => s.Start >= 0 && s.Start <= caret)
                        .OrderByDescending(s => s.Start)
                        .Select(s => s.Cat)
                        .FirstOrDefault();
    }

    private void ShowGoToDialog()
    {
        var index = BuildGoToIndex();
        if (index.Count == 0) return; // not a file this navigator understands — nothing to show

        var current = CurrentGoToCategory();

        using var dialog = new Form
        {
            Text = "Go to",
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.CenterParent,
            Width = 620,
            Height = 420,
            MinimumSize = new Size(420, 260),
            ShowInTaskbar = false,
            ShowIcon = false,
            KeyPreview = true
        };

        var tagList = new ListBox { Dock = DockStyle.Left, Width = 200, IntegralHeight = false };
        var detailList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

        foreach (var cat in GoToCategoryOrder)
            if (index.ContainsKey(cat)) tagList.Items.Add(cat == current ? "●  " + cat : cat);
        foreach (var cat in index.Keys)
            if (!GoToCategoryOrder.Contains(cat)) tagList.Items.Add(cat == current ? "●  " + cat : cat);

        var currentDetails = new List<GoToItem>();

        void RefreshDetails()
        {
            detailList.Items.Clear();
            if (tagList.SelectedItem is not string label) return;
            var cat = label.StartsWith('●') ? label[3..] : label;
            if (!index.TryGetValue(cat, out var items)) return;
            currentDetails = items;
            foreach (var item in items) detailList.Items.Add(item.Label);
        }

        tagList.SelectedIndexChanged += (_, _) => RefreshDetails();

        void JumpAndClose()
        {
            if (detailList.SelectedIndex < 0 || detailList.SelectedIndex >= currentDetails.Count) return;
            dialog.Tag = currentDetails[detailList.SelectedIndex].Position;
            dialog.DialogResult = DialogResult.OK;
        }

        detailList.DoubleClick += (_, _) => JumpAndClose();
        detailList.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) JumpAndClose(); };
        dialog.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) dialog.DialogResult = DialogResult.Cancel; };

        dialog.Controls.Add(detailList);
        dialog.Controls.Add(tagList);

        ThemeManager.Apply(dialog);

        var initialTagIndex = current is not null
            ? tagList.Items.Cast<string>().ToList().FindIndex(t => t.EndsWith(current))
            : 0;
        tagList.SelectedIndex = initialTagIndex >= 0 ? initialTagIndex : (tagList.Items.Count > 0 ? 0 : -1);

        if (dialog.ShowDialog(FindForm()) == DialogResult.OK && dialog.Tag is int position)
        {
            _textBox.Select(position, 0);
            _textBox.ScrollToCaret();
            _textBox.Focus();
        }
    }

    /// <summary>The identifier (letters/digits/underscore) touching <paramref name="index"/> —
    /// works whether the caret sits inside the name or right against its edge (e.g. just
    /// before the trailing ';' of "&amp;Name;", where the caret itself is on the ';').</summary>
    private static string GetWordAt(string text, int index)
    {
        if (text.Length == 0) return "";
        index = Math.Clamp(index, 0, text.Length - 1);

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        if (!IsWordChar(text[index]))
        {
            if (index > 0 && IsWordChar(text[index - 1])) index--;
            else return "";
        }

        var start = index;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var end = index;
        while (end < text.Length - 1 && IsWordChar(text[end + 1])) end++;
        return text.Substring(start, end - start + 1);
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
