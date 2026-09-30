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
    private UndoRedoTracker _undoRedo = null!;

    // ---- "File changed on another machine" banner — same idea as BcodeViewer.App's
    // editor.js checkExternalChange/showExternalChangeBanner, ported here so File Lookup's
    // preview and the Add Script/Script Cart tabs (anything that opens a real file through
    // this control) get the same warning. Polls the file's last-write time every 4s and
    // compares it against the write time captured when this control's content was last
    // loaded from — or saved to — disk; a genuine external write (not our own save) shows
    // an amber bar offering to reload.
    private readonly Panel _externalChangeBar;
    private readonly Label _externalChangeLabel;
    private readonly Button _reloadButton;
    private readonly Button _externalChangeDismissButton;
    private readonly System.Windows.Forms.Timer _externalChangeTimer;
    private DateTime? _loadedWriteTimeUtc;
    private DateTime? _dismissedWriteTimeUtc;
    private DateTime? _pendingExternalWriteTimeUtc;

    // Highlighting now assigns box.Rtf directly (see SqlSyntaxHighlighter) instead of the
    // old per-match SelectionColor calls — but unlike those, setting .Rtf DOES raise
    // TextChanged. Without this guard, that TextChanged handler below would restart the
    // highlight debounce, which reassigns .Rtf again 400ms later, which fires TextChanged
    // again — an infinite highlight loop, and since assigning .Rtf resets the scroll
    // position, the visible symptom was exactly "scroll down, and it keeps snapping back
    // to the top." This flag makes ApplyHighlight()'s own .Rtf assignment invisible to
    // that handler.
    private bool _suppressTextChanged;

    // Bumped on every ApplyHighlight() call — guards the async version below (see its
    // remarks) the same way FileLookupControl's own _previewRequestVersion guards its
    // background file reads: a slow highlight pass for content that's since been replaced
    // (LoadContent called again for a new file, or a debounce tick superseded by another
    // edit) must not clear _suppressTextChanged out from under whichever call is current.
    private int _highlightRequestVersion;

    // ---- Entity resolution (F12) -----------------------------------------------------
    // Ported from BcodeViewer.App's Web/entity.js, which itself fixed the same 1-level-only
    // problem this control used to have: instead of parsing "this file's own declarations,
    // optionally topped up with whatever the caller manually passed down from its parent"
    // (fragile — anything opened directly, not via a chain of F12s, only ever saw its own
    // DOCTYPE), F12 now walks the SYSTEM-include chain fresh, from disk, every time it's
    // pressed: this file's own declarations first, then each file it SYSTEM-includes,
    // breadth-first, up to EntityIncludeDepth levels — the same order an XML parser would
    // actually apply them in. No state to keep in sync between popups, and it resolves
    // correctly even for a controller opened straight from the tree.
    //
    // It also resolves VALUE entities now, not just SYSTEM ones. Most "&Name;" references
    // in a FastBusiness controller are entities whose declaration IS the code
    // (<!ENTITY Name "…whole SQL or JS routine…">), not a SYSTEM file reference — BcodeViewer's
    // own comment on this: "which is what most &Name; references in a controller are". The
    // old SYSTEM-only version of this control silently did nothing on every one of those,
    // which is most of what F12 gets pressed on in practice.
    private const int EntityIncludeDepth = 4;

    /// <summary>One &lt;!ENTITY ...&gt; declaration, either SYSTEM (Value is null, SystemPath
    /// set) or a value entity (Value set, SystemPath is null).</summary>
    private readonly record struct EntityDecl(string Name, bool IsSystem, string? Value, string? SystemPath, int Offset);

    /// <summary>Result of resolving a name: which declaration it is, and the path/text of the
    /// file that actually declared it (may be a SYSTEM-included file several hops away from
    /// the file the user pressed F12 in).</summary>
    private readonly record struct ResolvedEntity(EntityDecl Decl, string DeclaringPath, string DeclaringText);

    /// <summary>Base path F12/entity resolution walks from — normally the same as
    /// CurrentPath, but for a "peek" popup showing a VALUE entity's own text (see
    /// <see cref="EntityValuePeekRequested"/>) CurrentPath is null (there's no real file on
    /// screen, just the entity's value) while this still points at the file that declared it,
    /// so F12 pressed inside a peeked value can keep resolving further entities it itself
    /// references.</summary>
    private string? _entityResolveBasePath;

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

    /// <summary>Raised when F12 resolves to a SYSTEM entity (or the caret sits directly on a
    /// quoted file path) whose target file exists on disk — the argument is that file's full
    /// path. The caller decides how to show it (open a tab, or navigate File Lookup's own
    /// preview / a floating popup).</summary>
    public event Action<string>? EntityNavigationRequested;

    /// <summary>Raised when F12 resolves to a VALUE entity — i.e. the entity's "code" is the
    /// declaration's own text, not a file. Arguments: the entity name, its declared value, and
    /// the full path of the file that actually declared it (for the popup's subtitle / "open
    /// declaration" support). The caller shows this in a read-only peek window rather than
    /// trying to open a file that doesn't exist.</summary>
    public event Action<string, string, string>? EntityValuePeekRequested;

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

        _hideBarButton = new PillButton
        {
            Text = "✕",
            CornerRadius = 4,
            Dock = DockStyle.Right,
            Width = 24,
            TabStop = false
        };
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

        _externalChangeLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            AutoEllipsis = true,
            BackColor = AppColors.Warning,
            ForeColor = AppColors.OnAccent
        };
        // PillButton (owner-drawn, reads AppColors live in OnPaint) rather than a flat Button
        // with BackColor assigned once here — the assigned-once version kept the old theme's
        // amber after a light/dark toggle.
        _reloadButton = new PillButton
        {
            Text = "Reload",
            IsPrimary = true,
            CornerRadius = 6,
            Dock = DockStyle.Right,
            Width = 72,
            TabStop = false,
            Margin = new Padding(2),
        };
        _reloadButton.Click += (_, _) => ReloadFromDisk();
        _externalChangeDismissButton = new PillButton
        {
            Text = "✕",
            CornerRadius = 4,
            Dock = DockStyle.Right,
            Width = 24,
            TabStop = false
        };
        _externalChangeDismissButton.FlatAppearance.BorderSize = 0;
        _externalChangeDismissButton.Click += (_, _) =>
        {
            _dismissedWriteTimeUtc = _pendingExternalWriteTimeUtc;
            HideExternalChangeBar();
        };
        _externalChangeBar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 24,
            BackColor = AppColors.Warning,
            Visible = false
        };
        _externalChangeBar.Controls.Add(_externalChangeLabel);
        _externalChangeBar.Controls.Add(_reloadButton);
        _externalChangeBar.Controls.Add(_externalChangeDismissButton);

        // The warning bar's own background/foreground are plain assigned colors, so they need
        // re-applying when the theme flips (the two buttons on it are PillButtons and repaint
        // themselves). Static event — unsubscribed on Dispose below, or every closed tab would
        // be kept alive by it.
        void ApplyBarTheme()
        {
            _externalChangeBar.BackColor = AppColors.Warning;
            _externalChangeLabel.BackColor = AppColors.Warning;
            _externalChangeLabel.ForeColor = AppColors.OnAccent;
        }
        ThemeManager.ThemeChanged += ApplyBarTheme;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= ApplyBarTheme;

        _externalChangeTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        _externalChangeTimer.Tick += (_, _) => CheckExternalChange();
        _externalChangeTimer.Start();
        Disposed += (_, _) => { _externalChangeTimer.Stop(); _externalChangeTimer.Dispose(); };

        _textBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false,
            Font = ThemeManager.MonoFont,
            AcceptsTab = true,
            DetectUrls = false // avoids an extra scan over large pasted/loaded content
        };

        _undoRedo = new UndoRedoTracker(_textBox);

        // Re-highlight is debounced (not on every keystroke) so typing in a long
        // script doesn't lag from re-running the regex passes on every character.
        _highlightDebounce = new System.Windows.Forms.Timer { Interval = 400 };
        _highlightDebounce.Tick += (_, _) =>
        {
            _highlightDebounce.Stop();
            if (_textBox.IsDisposed) return;
            ApplyHighlight();
        };

        // Native RichTextBox Undo is disabled — see SqlSyntaxHighlighter.DisableNativeUndo —
        // and replaced with UndoRedoTracker, intercepted here.
        _textBox.KeyDown += (_, e) =>
        {
            if (e.Control && !e.Shift && e.KeyCode == Keys.Z)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _undoRedo.Undo();
                return;
            }
            if ((e.Control && e.KeyCode == Keys.Y) || (e.Control && e.Shift && e.KeyCode == Keys.Z))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                _undoRedo.Redo();
            }
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
        // catch up once the handle exists. Also disables native Undo here (matching
        // RawSqlControl) — this call was missing, which left the native Undo queue
        // enabled-but-unused for this control instead of fully replaced by UndoRedoTracker.
        _textBox.HandleCreated += (_, _) =>
        {
            SqlSyntaxHighlighter.DisableNativeUndo(_textBox);
            ApplyHighlight();
        };

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
        Controls.Add(_externalChangeBar);
    }

    /// <summary>"Fcode's lookup/preview bars are smooth — check what makes them not lag."
    /// This control's own File Lookup preview use (and the Command/Add Script viewers that
    /// share it) had exactly the bug already chased down and fixed in RawSqlControl:
    /// SqlSyntaxHighlighter.Apply() builds the RTF string on the UI thread before its
    /// (unavoidably blocking) native box.Rtf assignment, so clicking a file/loading content
    /// big enough to matter visibly stalled the window. ApplyAsync moves that prep off-
    /// thread — same fix, same reasoning, just applied here too.
    ///
    /// async void (not async Task) because every call site here is a fire-and-forget event
    /// handler already (a Timer Tick, a HandleCreated handler, LoadContent — none of them
    /// await this), so there's no Task for a caller to await either way; the version guard
    /// below is what keeps overlapping calls safe instead of relying on ordering.</summary>
    private async void ApplyHighlight()
    {
        var version = ++_highlightRequestVersion;
        _suppressTextChanged = true;
        try
        {
            await SqlSyntaxHighlighter.ApplyAsync(_textBox);
        }
        finally
        {
            // Only the still-current request clears the flag — a superseded call (LoadContent
            // ran again, or another debounce tick fired, while this one's background RTF build
            // was in flight) finishing late must not turn suppression off underneath the newer
            // call that's still relying on it.
            if (version == _highlightRequestVersion) _suppressTextChanged = false;
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

    /// <param name="path">Real file path, or null for generated/temp content (a diff result,
    /// a raw SQL preview, an entity VALUE being peeked — see <paramref name="entityResolveBasePath"/>).</param>
    /// <param name="entityResolveBasePath">Where F12/entity resolution should walk from when
    /// it isn't simply <paramref name="path"/> — used when peeking a VALUE entity's own text
    /// (path is null there, nothing to load from disk, but F12 inside that text should still
    /// resolve starting from the file that declared it). Omit to default to
    /// <paramref name="path"/>, which is what every normal file load wants.</param>
    public void LoadContent(string? path, string content, string? entityResolveBasePath = null)
    {
        CurrentPath = path;
        _entityResolveBasePath = entityResolveBasePath ?? path;
        _pathLabel.Text = path ?? "(nội dung tạm)";
        _hideBarButton.Visible = path is null; // only offer to hide for generated/temp content
        _textBox.Text = content;
        IsDirty = false;
        DirtyChanged?.Invoke();
        _highlightDebounce.Stop();
        if (_textBox.IsHandleCreated) ApplyHighlight();
        // else: the _textBox.HandleCreated subscription in the constructor will catch up.

        _dismissedWriteTimeUtc = null;
        HideExternalChangeBar();
        _loadedWriteTimeUtc = TryGetWriteTimeUtc(path);
    }

    // ---- Entity parsing/resolution (F12) --------------------------------------------------

    /// <summary>Every &lt;!ENTITY ...&gt; declaration in one document's text.
    ///
    /// Hand-scanned rather than matched with one regex: a declaration is
    /// "&lt;!ENTITY [%] name (SYSTEM|PUBLIC ...)? "quoted"&gt;", the quoted part may be
    /// single- or double-quoted, and a VALUE can run for hundreds of lines and contain '>'
    /// freely (e.g. a SQL "if x > 0") — which is exactly what defeats the obvious
    /// "&lt;!ENTITY[^&gt;]*&gt;" pattern. Ported from BcodeViewer.App's Web/entity.js
    /// parseDeclarations, which had to solve the same problem for the same file format.</summary>
    private static List<EntityDecl> ParseEntityDeclarations(string text)
    {
        var decls = new List<EntityDecl>();
        int i = 0;
        while (true)
        {
            int at = text.IndexOf("<!ENTITY", i, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;

            int p = at + 8; // "<!ENTITY".Length
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            if (p < text.Length && text[p] == '%')
            {
                p++;
                while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            }

            if (p >= text.Length || !(char.IsLetter(text[p]) || text[p] == '_'))
            {
                i = at + 8; // not a real declaration (or malformed) — keep scanning past it
                continue;
            }
            int nameStart = p;
            p++;
            while (p < text.Length && IsEntityNameChar(text[p])) p++;
            string name = text.Substring(nameStart, p - nameStart);
            while (p < text.Length && char.IsWhiteSpace(text[p])) p++;

            bool isSystem = false;
            if (p + 6 <= text.Length && string.Compare(text, p, "SYSTEM", 0, 6, StringComparison.OrdinalIgnoreCase) == 0)
            {
                isSystem = true;
                p += 6;
                while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
            }
            else if (p + 6 <= text.Length && string.Compare(text, p, "PUBLIC", 0, 6, StringComparison.OrdinalIgnoreCase) == 0)
            {
                // PUBLIC takes two quoted strings; the second is the system id. Skipping the
                // first cleanly beats mis-reading it as the value.
                p += 6;
                while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
                var skippedFirst = ReadQuoted(text, p);
                if (skippedFirst is null) { i = at + 8; continue; }
                p = skippedFirst.Value.End;
                while (p < text.Length && char.IsWhiteSpace(text[p])) p++;
                isSystem = true;
            }

            var quoted = ReadQuoted(text, p);
            if (quoted is null) { i = at + 8; continue; }

            decls.Add(new EntityDecl(
                name,
                isSystem,
                isSystem ? null : quoted.Value.Text,
                isSystem ? quoted.Value.Text : null,
                at));

            // Resume past the value: a value holding its own "<!ENTITY" text (SQL that
            // builds a DOCTYPE, which does happen) must not be read as a second declaration.
            i = quoted.Value.End;
        }
        return decls;
    }

    /// <summary>FastBusiness entity names use letters/digits/underscore plus '.', ':', '$'
    /// and '-' (e.g. "Conditional.MovingStock", "Combo.SVTran.AfterUpdate") — matches
    /// BcodeViewer's own [\w.:$-] name character class.</summary>
    private static bool IsEntityNameChar(char c) =>
        char.IsLetterOrDigit(c) || c is '_' or '.' or ':' or '$' or '-';

    /// <summary>The quoted string starting at <paramref name="i"/> (its opening quote), or
    /// null. XML entity values have no backslash escaping — an embedded quote is written
    /// "&amp;quot;" — so the first matching quote genuinely ends the value.</summary>
    private static (string Text, int End)? ReadQuoted(string text, int i)
    {
        if (i >= text.Length) return null;
        char quote = text[i];
        if (quote != '"' && quote != '\'') return null;
        int end = text.IndexOf(quote, i + 1);
        if (end < 0) return null;
        return (text.Substring(i + 1, end - i - 1), end + 1);
    }

    /// <summary>Finds <paramref name="name"/> in <paramref name="path"/>'s own declarations,
    /// then in each file it SYSTEM-includes, breadth-first (nearest declaration wins — the
    /// same order an XML parser applies them in), up to <paramref name="depth"/> levels. A
    /// file that includes itself (directly or indirectly) is skipped via <paramref
    /// name="seen"/> rather than turning one F12 into an endless walk.</summary>
    private static ResolvedEntity? ResolveEntity(string name, string path, string text, HashSet<string> seen, int depth)
    {
        var key = path.ToLowerInvariant();
        if (seen.Contains(key)) return null;
        seen.Add(key);

        var decls = ParseEntityDeclarations(text);
        foreach (var d in decls)
            if (string.Equals(d.Name, name, StringComparison.Ordinal))
                return new ResolvedEntity(d, path, text);

        if (depth <= 0) return null;

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return null;

        foreach (var include in decls)
        {
            if (!include.IsSystem || include.SystemPath is null) continue;

            string resolved;
            try
            {
                resolved = Path.GetFullPath(Path.Combine(dir, include.SystemPath.Replace('/', '\\')));
            }
            catch (Exception)
            {
                continue; // malformed path in that declaration — skip it, keep looking elsewhere
            }
            if (seen.Contains(resolved.ToLowerInvariant())) continue;

            var includedText = TryReadFile(resolved);
            if (includedText is null) continue; // missing/unreadable include — not this branch's answer

            var found = ResolveEntity(name, resolved, includedText, seen, depth - 1);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? TryReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The file path inside the quoted string under the caret on the current line,
    /// if any — lets F12 open a SYSTEM include straight away when the caret sits on part of
    /// its path (e.g. the "Fields" in "..\Include\XML\Config\Fields\SVGrid.ent") rather than
    /// requiring it to sit exactly on the entity's own name. Only matches a quoted string that
    /// actually looks like a file path (contains \ or / and ends in a short extension) — a
    /// plain quoted word like "SVDetail" still falls through to name-based resolution below.</summary>
    private string? QuotedPathAtCaret()
    {
        int index = Math.Clamp(_textBox.SelectionStart, 0, Math.Max(0, _textBox.TextLength - 1));
        int lineNo = _textBox.GetLineFromCharIndex(index);
        var lines = _textBox.Lines;
        if (lineNo < 0 || lineNo >= lines.Length) return null;
        string line = lines[lineNo];
        int lineStart = _textBox.GetFirstCharIndexFromLine(lineNo);
        int caret = index - lineStart;

        foreach (Match m in QuotedStringRegex.Matches(line))
        {
            int start = m.Index + 1;
            int end = start + m.Groups[2].Length;
            if (caret < start || caret > end) continue;
            var value = m.Groups[2].Value.Trim();
            if (value.Length == 0) return null;
            if (Regex.IsMatch(value, @"[\\/]") && Regex.IsMatch(value, @"\.[A-Za-z0-9]{1,6}$"))
                return value;
            return null;
        }
        return null;
    }

    private static readonly Regex QuotedStringRegex = new(@"([""'])([^""'\r\n]*)\1", RegexOptions.Compiled);

    /// <summary>F12: resolves whatever's under the caret — a quoted Include path, or an
    /// entity name — against <see cref="_entityResolveBasePath"/> and everything it
    /// (transitively) SYSTEM-includes. A SYSTEM entity (or a directly-clicked path) opens the
    /// target file via <see cref="EntityNavigationRequested"/>; a VALUE entity is shown via
    /// <see cref="EntityValuePeekRequested"/> instead, since its "code" is the declaration's
    /// own text, not a file. Shows a small message when nothing resolves, so a genuinely
    /// missing Include file (declared, but the .txt was never created) doesn't look
    /// indistinguishable from a bug.</summary>
    private void TryNavigateToEntityAtCaret()
    {
        if (_entityResolveBasePath is null) return; // nothing to resolve a relative Include path against

        var quotedPath = QuotedPathAtCaret();
        if (quotedPath is not null)
        {
            var normalized = quotedPath.Replace('/', '\\');
            string? target = null;
            try
            {
                target = Path.IsPathRooted(normalized)
                    ? normalized
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_entityResolveBasePath)!, normalized));
            }
            catch (Exception)
            {
                // malformed path text under the caret — fall through to name-based resolution
            }
            if (target is not null && File.Exists(target))
            {
                EntityNavigationRequested?.Invoke(target);
                return;
            }
        }

        var name = GetWordAt(_textBox.Text, _textBox.SelectionStart);
        if (string.IsNullOrEmpty(name)) return;

        var found = ResolveEntity(name, _entityResolveBasePath, _textBox.Text, new HashSet<string>(), EntityIncludeDepth);
        if (found is null)
        {
            MessageBox.Show(this,
                $"Không tìm thấy khai báo cho entity \"{name}\" (đã tìm trong file này và tối đa {EntityIncludeDepth} cấp Include).",
                "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var decl = found.Value.Decl;
        if (decl.IsSystem)
        {
            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(found.Value.DeclaringPath)!, decl.SystemPath!.Replace('/', '\\')));
            }
            catch (Exception)
            {
                return; // malformed path in the declaration
            }
            if (File.Exists(fullPath))
            {
                EntityNavigationRequested?.Invoke(fullPath);
            }
            else
            {
                MessageBox.Show(this,
                    $"Entity \"{name}\" khai báo trỏ tới file không tồn tại:\n{fullPath}",
                    "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        else
        {
            EntityValuePeekRequested?.Invoke(name, decl.Value ?? "", found.Value.DeclaringPath);
        }
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

    /// <summary>The identifier touching <paramref name="index"/> — works whether the caret
    /// sits inside the name or right against its edge (e.g. just before the trailing ';' of
    /// "&amp;Name;", where the caret itself is on the ';'). Includes '.', ':', '$' and '-'
    /// alongside letters/digits/underscore so a dotted entity name like
    /// "Conditional.MovingStock" is captured whole rather than split at the dot — matching
    /// FastBusiness's own entity naming (see <see cref="IsEntityNameChar"/>).</summary>
    private static string GetWordAt(string text, int index)
    {
        if (text.Length == 0) return "";
        index = Math.Clamp(index, 0, text.Length - 1);

        static bool IsWordChar(char c) => IsEntityNameChar(c);

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
        set { _textBox.Text = value; IsDirty = true; DirtyChanged?.Invoke(); _undoRedo.ResetBaseline(); }
    }

    public void MarkSaved()
    {
        IsDirty = false;
        DirtyChanged?.Invoke();
        _dismissedWriteTimeUtc = null;
        HideExternalChangeBar();
        _loadedWriteTimeUtc = TryGetWriteTimeUtc(CurrentPath);
    }

    private static DateTime? TryGetWriteTimeUtc(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Polled every 4s (see _externalChangeTimer). Compares the file's current
    /// on-disk write time against the one captured at the last LoadContent/MarkSaved — a
    /// mismatch means someone else (another machine, another app) wrote the file since we
    /// last read or saved it here. Ignores a write time the user already dismissed via ✕,
    /// so the bar doesn't keep popping back up for the same external change.</summary>
    private void CheckExternalChange()
    {
        if (CurrentPath is null || IsDisposed) return;

        var diskTime = TryGetWriteTimeUtc(CurrentPath);
        if (diskTime is null) return; // file missing/locked — nothing to report

        if (_loadedWriteTimeUtc is not null && diskTime.Value == _loadedWriteTimeUtc.Value) return;
        if (_dismissedWriteTimeUtc is not null && diskTime.Value == _dismissedWriteTimeUtc.Value) return;

        ShowExternalChangeBar(diskTime.Value);
    }

    private void ShowExternalChangeBar(DateTime diskWriteTimeUtc)
    {
        _pendingExternalWriteTimeUtc = diskWriteTimeUtc;
        _externalChangeLabel.Text = IsDirty
            ? "File đã thay đổi từ máy khác. Bạn đang có thay đổi chưa lưu — Reload sẽ mất các thay đổi này."
            : "File đã thay đổi từ máy khác.";
        _externalChangeBar.Visible = true;
    }

    private void HideExternalChangeBar()
    {
        _externalChangeBar.Visible = false;
        _pendingExternalWriteTimeUtc = null;
    }

    private void ReloadFromDisk()
    {
        if (CurrentPath is null) return;
        try
        {
            var content = File.ReadAllText(CurrentPath);
            LoadContent(CurrentPath, content);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không đọc lại được file:\n{ex.Message}", "Bcode",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void Clear()
    {
        CurrentPath = null;
        _entityResolveBasePath = null;
        _pathLabel.Text = "(chưa mở file nào)";
        _textBox.Clear();
        IsDirty = false;
        DirtyChanged?.Invoke();
        _undoRedo.ResetBaseline();
        _loadedWriteTimeUtc = null;
        _dismissedWriteTimeUtc = null;
        HideExternalChangeBar();
    }

    public void CopyToClipboard()
    {
        if (!string.IsNullOrEmpty(_textBox.Text))
            Clipboard.SetText(_textBox.Text);
    }
}