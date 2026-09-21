using System.Data;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Controls;

/// <summary>
/// "Command" tool — a free-form, multi-statement SQL script runner (batches
/// separated by "GO", like SSMS/sqlcmd), as opposed to "SQL Query" which is
/// the structured SELECT/FROM/WHERE/ORDER BY builder. Reuses the same
/// syntax-highlighted RichTextBox as ScriptEditorControl.
///
/// Toolbar mirrors FCode's "Query" tab per the user's screenshots:
/// Open | Save | Execute | Write Schema | Check Fields | Comment | Uncomment |
/// Options... | Default Type ▾ | Suggest Param/Caret ☑ | Reset Connection ☑ | Result Tab ☐
///
/// A few of these are necessarily assumption-based (FCode's exact internal
/// behavior wasn't fully visible in the screenshots) — see the doc-comment on
/// each handler below and the README's "Cập nhật gần đây" for what was assumed.
/// </summary>
public class RawSqlControl : UserControl
{
    private readonly RichTextBox _scriptBox;
    // Toolbar (Open/Save/Execute/Debug/Write Schema/Check Fields/Comment/Uncomment/Options/
    // Default Type/DB/Suggest/Reset Connection/Result Tab) is now the HTML/CSS/JS page
    // sqlquerybar.html rendered in a small WebView2 strip — same "modern like Fiddler, keep
    // it fast" chrome-vs-content split as MainForm's own top bar/icon rail/status bar. The
    // script box, gutter, splitter and result grid below stay 100% native WinForms, since
    // those are exactly the "data-heavy" parts speed depends on. Every checkbox-style
    // ToolStripButton and the two combo boxes that used to hold their own state now do so in
    // these plain fields instead, updated from the WebView2 message handler.
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _barWeb = new();
    private bool _wordWrap;
    private bool _useSysDatabase;
    private bool _suggestOn = true;
    private bool _resetConnOn = true;
    private bool _resultTabOn;
    private bool _debugStepOn;
    private readonly MultiResultView _resultView;
    private readonly Label _statusLabel;
    private LineNumberGutter _lineGutter = null!;
    private readonly RawSqlService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly LookupService _lookupService;

    private readonly SnippetLibraryService _snippets;
    private readonly System.Windows.Forms.Timer _highlightDebounce;

    private UndoRedoTracker _undoRedo = null!;
    private string? _currentFilePath;
    private SqlConnection? _persistentConn;
    private bool _persistentConnUsesSys;
    private List<string> _tableNameCache = new();
    private readonly Dictionary<string, List<string>> _columnCache = new();
    private ListBox? _suggestPopup;

    private static readonly Regex TableRefRegex = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO)\s+(\[?[\w$]+\]?(?:\.\[?[\w$]+\]?)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RawSqlControl(RawSqlService service, SqlObjectBrowserService sqlObjectService, LookupService lookupService, SnippetLibraryService snippets)
    {
        _service = service;
        _sqlObjectService = sqlObjectService;
        _lookupService = lookupService;
        _snippets = snippets; // LƯU LẠI BIẾN
        Dock = DockStyle.Fill;

        // ---- Toolbar (WebView2, Web/Shell/sqlquerybar.html) — Open/Save/Execute/Debug/Write
        // Schema/Check Fields/Comment/Uncomment/Options/Default Type/DB/Suggest/Reset
        // Connection/Result Tab, all in one modern HTML strip (same chrome-vs-content split
        // as MainForm's top bar: this row is "static, low-data" chrome, the script box/grid
        // below stay 100% native WinForms for speed). ----
        _barWeb.Dock = DockStyle.Top;
        _barWeb.Height = 40;

        // ---- Script box ----
        _scriptBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ScrollBars = RichTextBoxScrollBars.Both,
            WordWrap = false,
            Font = ThemeManager.MonoFont,
            AcceptsTab = true,
            Text = "-- Viết 1 hoặc nhiều câu lệnh SQL, cách nhau bằng dòng GO nếu cần nhiều batch.\r\nSELECT TOP 100 * FROM sys.tables;"
        };
        _undoRedo = new UndoRedoTracker(_scriptBox);
        _highlightDebounce = new System.Windows.Forms.Timer { Interval = 400 };
        _highlightDebounce.Tick += async (_, _) =>
        {
            _highlightDebounce.Stop();
            // ApplyAsync (not the synchronous Apply used elsewhere in this file) — pasting a
            // few hundred lines was visibly laggier here than FCode's own paste, since Apply()
            // builds the RTF string on the UI thread before its (unavoidably blocking) native
            // box.Rtf assignment. ApplyAsync moves that prep off-thread.
            await SqlSyntaxHighlighter.ApplyAsync(_scriptBox);
            _undoRedo.Checkpoint(); // one undo step per "pause in typing"
            if (_safeLinePreviewOn) ToggleSafeLinePreview(true); // keep the breakpoint-dot preview live as the script changes
        };
        _scriptBox.HandleCreated += (_, _) =>
        {
            SqlSyntaxHighlighter.DisableNativeUndo(_scriptBox);
            SqlSyntaxHighlighter.Apply(_scriptBox);
        };
        _scriptBox.TextChanged += (_, _) =>
        {
            _highlightDebounce.Stop();
            _highlightDebounce.Start();
        };



        _scriptBox.KeyDown += async (_, e) =>
        {
            // Native RichTextBox Undo is disabled (see SqlSyntaxHighlighter.DisableNativeUndo) —
            // UndoRedoTracker is the only thing handling Ctrl+Z/Ctrl+Y now, so intercept both here.
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
                return;
            }

            if (_suggestPopup is { Visible: true })
            {
                if (e.KeyCode == Keys.Down) { e.Handled = true; MoveSuggestSelection(1); return; }
                if (e.KeyCode == Keys.Up) { e.Handled = true; MoveSuggestSelection(-1); return; }
                if (e.KeyCode is Keys.Enter or Keys.Tab) { e.Handled = true; e.SuppressKeyPress = true; AcceptSuggestion(); return; }
                if (e.KeyCode == Keys.Escape) { e.Handled = true; HideSuggestions(); return; }
            }

            if (e.Control && e.KeyCode == Keys.Space)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await ShowSuggestionsAsync();
                return;
            }

            if (e.KeyCode == Keys.F5 || (e.Control && e.KeyCode == Keys.Enter))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                await RunAsync();
                return;
            }
        };
        _scriptBox.KeyPress += (_, _) => HideSuggestions();
        _scriptBox.LostFocus += (_, _) => HideSuggestions();

        // "Thêm chức năng ctrl + Chuột phải vào tên procedure sẽ tự mở procedure đó và đưa
        // query hiện tại vào cuối procedure tương tự fcode." Assigning a ContextMenuStrip
        // replaces RichTextBox's built-in OS Undo/Cut/Copy/Paste/Select All menu, so this one
        // doubles as that (a plain right-click still needs *some* menu) — it just cancels
        // itself when Ctrl is held, so it doesn't pop up on top of the Ctrl+Right-click action
        // handled by MouseDown below instead.
        WebMenu.AttachTo(_scriptBox, () =>
        {
            // Ctrl+Right-click is the "open this procedure" gesture handled in MouseDown
            // below — suppress the menu so it doesn't pop up on top of that.
            if (Control.ModifierKeys == Keys.Control) return null;

            var hasSelection = _scriptBox.SelectionLength > 0;
            var menu = new WebMenu()
                .Add("Undo", () => _undoRedo.Undo())
                .AddSeparator()
                .Add("Cut", () => _scriptBox.Cut(), enabled: hasSelection)
                .Add("Copy", () => _scriptBox.Copy(), enabled: hasSelection)
                .Add("Paste", () => _scriptBox.Paste(), enabled: Clipboard.ContainsText())
                .Add("Select All", () => _scriptBox.SelectAll())
                .AddSeparator()
                .Add("Beauty Format", BeautyFormat);

            // Library snippets used to hide under a "Tools" submenu (and a second level per
            // category). Inline with a caption per category instead — one right-click reaches
            // any snippet, which is the whole point of having them here.
            if (_snippets is null || _snippets.Snippets.Count == 0)
            {
                menu.AddCaption("Tools");
                menu.Add("(Chưa có cấu hình - Mở Library...)", () => { }, enabled: false);
            }
            else
            {
                foreach (var group in _snippets.Snippets.GroupBy(s => s.Category))
                {
                    menu.AddCaption(string.IsNullOrWhiteSpace(group.Key) ? "Tools" : group.Key);
                    foreach (var snippet in group)
                    {
                        var content = snippet.Content;
                        menu.Add(snippet.Name, () => InsertText(content));
                    }
                }
            }
            return menu;
        });

        _scriptBox.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || Control.ModifierKeys != Keys.Control) return;
            var charIndex = _scriptBox.GetCharIndexFromPosition(e.Location);
            var identifier = GetIdentifierAt(charIndex);
            if (string.IsNullOrWhiteSpace(identifier)) return;
            OpenProcedureWithQueryRequested?.Invoke(identifier, UseSysDatabase, _scriptBox.Text);
        };

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };

        // "nếu 1 procedure có trả ra nhiều kết quả nhiều bảng thì Bcode hiện chỉ trả ra có 1
        // bảng" — a single batch (e.g. one EXEC of a procedure with several SELECTs inside it)
        // can produce several result sets; MultiResultView stacks one grid per table (each
        // already AutoSizeColumnsMode.None + GridDisplayHelper.BindOptimized internally, so the
        // earlier "đơ khi cuộn" scroll fix still applies to every one of them, not just a
        // single grid).
        _resultView = new MultiResultView { Dock = DockStyle.Fill };

        // "ô query ko cho kéo lại size giữa ô query và tab result nhỉ" — the script box used to
        // be a fixed-Height (220px) Panel with the grid just filling whatever was left below
        // it, so there was no boundary to drag. A SplitContainer gives that boundary: Panel1
        // hosts the script box, Panel2 hosts the status label + result grid, and the splitter
        // between them is user-draggable. SplitterDistance can't be set until the control has
        // a real size (throws otherwise, since it validates against current Width/Height), so
        // it's set once on HandleCreated instead, guarded in case the host still hasn't laid it
        // out yet at that point either.
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        split.Panel1MinSize = 80;
        split.Panel2MinSize = 80;
        split.Panel1.Controls.Add(_scriptBox);
        // "sql query cho thêm số dòng bên trái như fcode để dễ debug" — LineNumberGutter
        // already existed (its own doc-comment even calls out RawSqlControl's script box as
        // the intended target: "Assumes WordWrap is off on the target (RawSqlControl's script
        // box already sets this)") but was never actually wired up anywhere. Dock=Left, added
        // after _scriptBox so it claims the left edge and the script box fills what's left.
        _lineGutter = new LineNumberGutter { Dock = DockStyle.Left };
        _lineGutter.Attach(_scriptBox);
        split.Panel1.Controls.Add(_lineGutter);
        split.Panel2.Controls.Add(_resultView);
        split.Panel2.Controls.Add(_statusLabel);
        split.HandleCreated += (_, _) =>
        {
            try { split.SplitterDistance = 220; } catch { /* control not sized yet on some hosts — default 50/50 split is fine */ }
        };

        Controls.Add(split);
        Controls.Add(_barWeb);

        _suggestPopup = new ListBox { Width = 240, Height = 150, Visible = false };
        _suggestPopup.Click += (_, _) => AcceptSuggestion();
        Controls.Add(_suggestPopup);
        _suggestPopup.BringToFront();

        // Theme toggling happens from MainForm's top bar, outside this control's own tree, so
        // Apply(root)'s recursive walk never reaches this tab's toolbar WebView2 — subscribe to
        // the static event instead (see ThemeManager.ThemeChanged) and unsubscribe on Dispose,
        // since a forgotten unsubscribe would keep every closed "SQL Query" tab alive forever.
        Bcode.App.UI.ThemeManager.ThemeChanged += PushThemeToBar;

        _ = InitBarWebAsync();

        Disposed += (_, _) =>
        {
            _highlightDebounce.Stop();
            _highlightDebounce.Dispose();
            DisposePersistentConnection();
            Bcode.App.UI.ThemeManager.ThemeChanged -= PushThemeToBar;
        };

        // Local async function so the WebView2 setup lives right next to the toolbar it drives,
        // same pattern as MainForm.InitShellWebViewsAsync.
        async Task InitBarWebAsync()
        {
            try
            {
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_barWeb);

                _barWeb.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var root2 = doc.RootElement;
                    switch (root2.GetProperty("action").GetString())
                    {
                        case "open": OpenFile(); break;
                        case "save": SaveFile(); break;
                        case "run": _ = RunAsync(); break;
                        case "debug-target": _ = PickDebugTargetAsync(); break;
                        case "write-schema": _ = WriteSchemaAsync(); break;
                        case "check-fields": _ = CheckFieldsAsync(); break;
                        case "comment": ToggleComment(true); break;
                        case "uncomment": ToggleComment(false); break;
                        case "options": BuildOptionsMenu().Show(_barWeb, 10, _barWeb.Height); break;
                        case "default-type": ApplyDefaultTypeChoice(root2.GetProperty("value").GetInt32()); break;
                        case "db":
                            _useSysDatabase = root2.GetProperty("value").GetInt32() == 1;
                            DisposePersistentConnection();
                            break;
                        case "toggle":
                            switch (root2.GetProperty("which").GetString())
                            {
                                case "suggest": _suggestOn = !_suggestOn; break;
                                case "reset-conn":
                                    _resetConnOn = !_resetConnOn;
                                    if (_resetConnOn) DisposePersistentConnection();
                                    break;
                                case "result-tab": _resultTabOn = !_resultTabOn; break;
                                case "debug-step": _debugStepOn = !_debugStepOn; ToggleSafeLinePreview(_debugStepOn); break;
                            }
                            break;
                    }
                };

                _barWeb.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    PushThemeToBar();
                    PushDatabaseToBar();
                };

                _barWeb.CoreWebView2.Navigate($"https://{Bcode.App.UI.WebViewEnvironment.Host}/sqlquerybar.html");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Không khởi tạo được thanh công cụ (dùng WebView2).\n" +
                    "Kiểm tra máy đã có WebView2 Runtime chưa (thường có sẵn qua Edge trên Windows 10/11).\n\n" +
                    "Chi tiết lỗi: " + ex.Message,
                    "Bcode — WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void PushThemeToBar()
        {
            if (_barWeb.CoreWebView2 is null) return;
            var isDark = Bcode.App.UI.AppColors.IsDark ? "true" : "false";
            _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
        }
    }

    /// <summary>The gear ("Options...") dropdown — HTML/CSS (Controls/WebMenu.cs), same look
    /// as the toolbar it hangs off, and rebuilt per open so Word Wrap's check mark always
    /// shows the editor's real state instead of a menu item's own remembered one.</summary>
    private WebMenu BuildOptionsMenu() => new WebMenu()
        .Add("Word Wrap", () => _scriptBoxWordWrapToggle(!_wordWrap), @checked: _wordWrap)
        .AddCaption("Cỡ chữ")
        .Add("Tăng cỡ chữ", () => _scriptBox.Font = new Font(_scriptBox.Font.FontFamily, _scriptBox.Font.Size + 1f))
        .Add("Giảm cỡ chữ", () => _scriptBox.Font = new Font(_scriptBox.Font.FontFamily, Math.Max(6f, _scriptBox.Font.Size - 1f)));

    private void _scriptBoxWordWrapToggle(bool wrap)
    {
        _wordWrap = wrap;
        _scriptBox.WordWrap = wrap;
        _scriptBox.ScrollBars = wrap ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
    }

    /// <summary>Fired after a run produces at least one result set — carries the LAST table
    /// across every batch/result set that ran, for callers like Gen Insert/Update or Create
    /// *.xlsx that only ever work off a single DataTable. When a run produced more than one
    /// table (see OpenResultInNewTabRequested/MultiResultView for the full set), which table
    /// ends up "last" depends on script order — e.g. an EXEC returning a small trailing
    /// summary result after its main data table means that summary table is what these
    /// single-table callers would see, not the main one; there's no independent signal here
    /// for which table the user actually means as "the" result.</summary>
    public event Action<DataTable>? ResultReady;

    /// <summary>Fired when "Result Tab" is checked and a run returns at least one result set —
    /// MainForm opens a new document tab hosting every table (in order) instead of showing
    /// them in this control's inline MultiResultView.</summary>
    public event Action<List<DataTable>, string>? OpenResultInNewTabRequested;

    /// <summary>Ctrl+Right-click on an identifier in the script (typically a stored-procedure
    /// name inside an EXEC call) — carries the clicked identifier, which database (App/Sys
    /// Data) this tab is currently pointed at, and this control's current script text.
    /// MainForm resolves the identifier to a SqlObjectInfo, opens/reuses its definition tab,
    /// and appends the script to the end of it — matches FCode's own "mở procedure đó và đưa
    /// query hiện tại vào cuối procedure".</summary>
    public event Action<string, bool, string>? OpenProcedureWithQueryRequested;

    /// <summary>Fired when the user picks a candidate in the "Debug store/function" dialog —
    /// carries the resolved target (a stored procedure or function found via EXEC/call
    /// somewhere in this script). MainForm opens it in its own runnable tab, same as the
    /// Ctrl+Right-click "open procedure" flow, but WITHOUT appending this script's own text —
    /// debugging jumps into the target's own body, it doesn't test it against this caller.</summary>
    public event Action<Bcode.App.Models.SqlObjectInfo>? DebugTargetChosen;

    private bool UseSysDatabase => _useSysDatabase;

    // ---------------- Debug store/function ----------------

    private async Task PickDebugTargetAsync()
    {
        var scanner = new DebugTargetScanner(_sqlObjectService);
        List<Bcode.App.Models.DebugCandidate> candidates;
        try
        {
            candidates = await scanner.ScanAsync(_scriptBox.Text, UseSysDatabase);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Debug store/function", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (candidates.Count == 0)
        {
            MessageBox.Show(this,
                "Không tìm thấy câu EXEC store hoặc gọi function nào trong script hiện tại để debug.",
                "Bcode — Debug store/function", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var form = new Bcode.App.Forms.ChooseDebugTargetForm(candidates);
        if (form.ShowDialog(this) == DialogResult.OK && form.Selected is { } chosen)
            DebugTargetChosen?.Invoke(chosen.Target);
    }

    /// <summary>Toggles a preview of SqlLineAnalyzer.FindSafeLines in the gutter (red dots on
    /// every line the future Step/Continue engine would be allowed to stop at) — lets this be
    /// checked against real, already-written procedures before the actual stepping engine is
    /// built on top of it. Not the real debugger yet: clicking a dot here doesn't run anything.</summary>
    private bool _safeLinePreviewOn;
    private void ToggleSafeLinePreview(bool on)
    {
        _safeLinePreviewOn = on;
        if (on)
        {
            var safeLines = SqlLineAnalyzer.FindSafeLines(_scriptBox.Text);
            _lineGutter.BreakpointLines = safeLines;
            _statusLabel.ForeColor = Color.DimGray;
            _statusLabel.Text = $"Xem trước điểm dừng debug: {safeLines.Count} dòng an toàn để dừng (chưa chạy thật — phần Step/Continue sẽ làm ở bản sau).";
        }
        else
        {
            _lineGutter.BreakpointLines = null;
        }
        _lineGutter.RefreshMarkers();
    }

    // ---------------- Execute ----------------

    private async Task RunAsync()
    {
        _statusLabel.Text = "Đang chạy...";
        try
        {
            var useSys = UseSysDatabase;
            var results = _resetConnOn
                ? await RunWithFreshConnectionAsync(useSys)
                : await RunWithPersistentConnectionAsync(useSys);

            var errorBatch = results.FirstOrDefault(r => r.Error is not null);
            // Flatten every result set from every batch, in order — a single batch (e.g. one
            // EXEC of a procedure with several SELECTs inside it) can itself contribute more
            // than one table; see RawSqlService.RunBatchesAsync.
            var allTables = results.SelectMany(r => r.Tables).ToList();
            var lastTable = allTables.LastOrDefault();

            if (allTables.Count > 0)
            {
                if (_resultTabOn)
                    OpenResultInNewTabRequested?.Invoke(allTables, "Command Result");
                else
                    _resultView.SetTables(allTables);
                if (lastTable is not null)
                    ResultReady?.Invoke(lastTable);
            }
            else if (!_resultTabOn)
            {
                _resultView.Clear();
            }

            var totalAffected = results.Sum(r => r.RowsAffected);
            var summary = $"{results.Count} batch đã chạy" +
                           (allTables.Count > 0
                               ? allTables.Count == 1
                                   ? $" · {allTables[0].Rows.Count} dòng kết quả"
                                   : $" · {allTables.Count} bảng kết quả ({allTables.Sum(t => t.Rows.Count)} dòng)"
                               : "") +
                           (totalAffected > 0 ? $" · {totalAffected} dòng bị ảnh hưởng (INSERT/UPDATE/DELETE)" : "") +
                           (_resetConnOn ? "" : " · [Reset Connection tắt: giữ nguyên connection/#temp table giữa các lần chạy]");

            if (errorBatch is not null)
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = $"Lỗi ở batch: {errorBatch.Error}";
                MessageBox.Show(this, errorBatch.Error, "Bcode — Command", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                _statusLabel.ForeColor = Color.DimGray;
                _statusLabel.Text = summary;
            }
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — Command", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private Task<List<RawSqlService.BatchResult>> RunWithFreshConnectionAsync(bool useSys)
    {
        DisposePersistentConnection();
        return _service.ExecuteScriptAsync(_scriptBox.Text, useSys);
    }

    private async Task<List<RawSqlService.BatchResult>> RunWithPersistentConnectionAsync(bool useSys)
    {
        if (_persistentConn is null || _persistentConnUsesSys != useSys || _persistentConn.State != ConnectionState.Open)
        {
            DisposePersistentConnection();
            _persistentConn = _service.CreateConnection(useSys);
            await _persistentConn.OpenAsync();
            _persistentConnUsesSys = useSys;
        }
        return await _service.ExecuteScriptOnAsync(_scriptBox.Text, _persistentConn);
    }

    private void DisposePersistentConnection()
    {
        _persistentConn?.Dispose();
        _persistentConn = null;
    }

    /// <summary>Replaces the script text programmatically — used by MainForm's Ctrl+Right-click
    /// "open procedure + append query" feature to fill a freshly-created (or reused) tab with
    /// the procedure's definition plus the appended test query, same as OpenFile above does for
    /// a file it just read from disk (re-highlights, and resets undo so Ctrl+Z doesn't try to
    /// undo back to whatever was here before, e.g. blank on a brand-new tab).</summary>
    public void SetScriptText(string text)
    {
        _scriptBox.Text = text;
        SqlSyntaxHighlighter.Apply(_scriptBox);
        _undoRedo.ResetBaseline();
    }

    /// <summary>Switches the "App Data"/"Sys Data" combo — used by the same Ctrl+Right-click
    /// feature so the opened tab targets whichever database the resolved procedure actually
    /// lives in, instead of always defaulting to "App Data".</summary>
    public void SetDatabase(bool useSysDatabase)
    {
        _useSysDatabase = useSysDatabase;
        DisposePersistentConnection(); // a stale persistent conn would target the wrong DB
        PushDatabaseToBar();
    }

    private void PushDatabaseToBar()
    {
        if (_barWeb.CoreWebView2 is null) return;
        var arg = System.Text.Json.JsonSerializer.Serialize(_useSysDatabase ? 1 : 0);
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setDatabase && window.setDatabase({arg})");
    }

    // ---------------- Open / Save ----------------

    private void OpenFile()
    {
        using var ofd = new OpenFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _scriptBox.Text = File.ReadAllText(ofd.FileName);
            _currentFilePath = ofd.FileName;
            SqlSyntaxHighlighter.Apply(_scriptBox);
            _undoRedo.ResetBaseline(); // opening a different file isn't an "undo-able edit" of the old one
            _statusLabel.Text = $"Đã mở {ofd.FileName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Open", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveFile()
    {
        if (_currentFilePath is null)
        {
            using var sfd = new SaveFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*", FileName = "script.sql" };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            _currentFilePath = sfd.FileName;
        }
        try
        {
            File.WriteAllText(_currentFilePath, _scriptBox.Text);
            _statusLabel.Text = $"Đã lưu {_currentFilePath}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Save", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------------- Write Schema ----------------

    /// <summary>
    /// Inserts the CREATE TABLE (+ indexes/triggers) script of a referenced table as a
    /// comment block at the caret — handy to check column names/types while writing the
    /// rest of the script. Guesses the table from the first FROM/JOIN/UPDATE/INTO in the
    /// script; falls back to asking, since FCode's exact trigger for this wasn't visible
    /// in the screenshots (assumption — see README).
    /// </summary>
    private async Task WriteSchemaAsync()
    {
        var guess = TableRefRegex.Match(_scriptBox.Text) is { Success: true } m
            ? m.Groups[1].Value.Replace("[", "").Replace("]", "")
            : "";

        var input = Bcode.App.Forms.SimplePromptForm.Show(this, "Write Schema", "Tên bảng cần lấy CREATE TABLE:", guess);
        if (string.IsNullOrWhiteSpace(input)) return;

        var (schema, table) = ParseTableRef(input);
        try
        {
            var obj = new SqlObjectInfo { Schema = schema, Name = table, Kind = SqlObjectKind.Table, FromSysDatabase = UseSysDatabase };
            var ddl = await _sqlObjectService.GetDefinitionAsync(obj);
            var block = "\r\n-- ===== Write Schema: " + obj.QualifiedName + " =====\r\n" +
                        string.Join("\r\n", ddl.Split('\n').Select(l => "-- " + l.TrimEnd('\r'))) +
                        "\r\n-- ===== hết Write Schema =====\r\n";
            _scriptBox.Select(_scriptBox.SelectionStart, 0);
            _scriptBox.SelectedText = block;
            SqlSyntaxHighlighter.Apply(_scriptBox);
            _undoRedo.Checkpoint(); // Write Schema insert is its own undo step (Ctrl+Z removes it)
            _statusLabel.Text = $"Đã chèn schema của {obj.QualifiedName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Write Schema", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static (string schema, string table) ParseTableRef(string raw)
    {
        raw = raw.Trim().Replace("[", "").Replace("]", "");
        var parts = raw.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
    }

    // ---------------- Check Fields ----------------

    private async Task CheckFieldsAsync()
    {
        _statusLabel.Text = "Đang kiểm tra (SET NOEXEC ON)...";
        try
        {
            var error = await _service.CheckFieldsAsync(_scriptBox.Text, UseSysDatabase);
            if (error is null)
            {
                _statusLabel.ForeColor = Color.DarkGreen;
                _statusLabel.Text = "Check Fields: OK — mọi bảng/cột trong script đều hợp lệ (chưa chạy thật câu lệnh).";
            }
            else
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = $"Check Fields: {error}";
                MessageBox.Show(this, error, "Bcode — Check Fields", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi Check Fields.";
            MessageBox.Show(this, ex.Message, "Bcode — Check Fields", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------------- Comment / Uncomment ----------------

    private void ToggleComment(bool comment)
    {
        if (_scriptBox.TextLength == 0) return;
        var startLine = _scriptBox.GetLineFromCharIndex(_scriptBox.SelectionStart);
        var endCharIndex = _scriptBox.SelectionStart + Math.Max(_scriptBox.SelectionLength - 1, 0);
        var endLine = _scriptBox.GetLineFromCharIndex(endCharIndex);

        var lines = _scriptBox.Lines;
        for (var i = startLine; i <= endLine && i < lines.Length; i++)
        {
            if (comment)
            {
                if (!lines[i].TrimStart().StartsWith("--"))
                    lines[i] = "-- " + lines[i];
            }
            else
            {
                var trimStart = lines[i].Length - lines[i].TrimStart().Length;
                var trimmed = lines[i].TrimStart();
                if (trimmed.StartsWith("-- "))
                    lines[i] = lines[i][..trimStart] + trimmed[3..];
                else if (trimmed.StartsWith("--"))
                    lines[i] = lines[i][..trimStart] + trimmed[2..];
            }
        }

        var selStart = _scriptBox.SelectionStart;
        _scriptBox.Lines = lines;
        SqlSyntaxHighlighter.Apply(_scriptBox);
        _undoRedo.Checkpoint(); // Comment/Uncomment is its own undo step
        _scriptBox.SelectionStart = Math.Min(selStart, _scriptBox.TextLength);
        _scriptBox.SelectionLength = 0;
        _scriptBox.Focus();
    }

    // ---------------- Default Type (keyword case) ----------------

    /// <summary>
    /// "Default Type" dropdown: choosing UPPER/lower rewrites every SQL keyword in the
    /// current script to that case right away (a one-off transform, not a persistent
    /// typing mode — FCode's exact semantics weren't visible in the screenshot beyond the
    /// 3 item labels, so this is the assumed behavior; see README).
    /// </summary>
    private void ApplyDefaultTypeChoice(int choice)
    {
        if (choice <= 0) return; // "Default Type" = no-op / neutral
        var toUpper = choice == 1; // 1 = UPPER Keyword, 2 = lower Keyword
        var selStart = _scriptBox.SelectionStart;
        _scriptBox.Text = SqlSyntaxHighlighter.TransformKeywordCase(_scriptBox.Text, toUpper);
        SqlSyntaxHighlighter.Apply(_scriptBox);
        _undoRedo.Checkpoint(); // Default Type case-rewrite is its own undo step
        _scriptBox.SelectionStart = Math.Min(selStart, _scriptBox.TextLength);
    }

    // ---------------- Suggest Param/Caret (autocomplete) ----------------

    private async Task EnsureTableNamesLoadedAsync()
    {
        if (_tableNameCache.Count > 0) return;
        try
        {
            var names = new List<string>();
            foreach (var useSys in new[] { false, true })
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys);
                names.AddRange(objs.Select(o => o.QualifiedName));
                names.AddRange(objs.Select(o => o.Name));
            }
            _tableNameCache = names.Distinct().ToList();
        }
        catch { /* chưa kết nối — suggest vẫn dùng được với riêng danh sách keyword */ }
    }

    private async Task<List<string>> GetReferencedColumnsAsync(bool useSys)
    {
        var cols = new List<string>();
        foreach (Match m in TableRefRegex.Matches(_scriptBox.Text))
        {
            var (schema, table) = ParseTableRef(m.Groups[1].Value);
            var cacheKey = $"{useSys}:{schema}.{table}";
            if (!_columnCache.TryGetValue(cacheKey, out var tableCols))
            {
                try { tableCols = await _lookupService.GetColumnsAsync(useSys, schema, table); }
                catch { tableCols = new(); }
                _columnCache[cacheKey] = tableCols;
            }
            cols.AddRange(tableCols);
        }
        return cols.Distinct().ToList();
    }

    private int GetCurrentWordStart()
    {
        var text = _scriptBox.Text;
        var caret = _scriptBox.SelectionStart;
        var i = caret;
        while (i > 0 && (char.IsLetterOrDigit(text[i - 1]) || text[i - 1] == '_' || text[i - 1] == '$')) i--;
        return i;
    }

    /// <summary>The identifier touching <paramref name="charIndex"/> (same word-char rule as
    /// GetCurrentWordStart above), plus a leading "schema." qualifier immediately before it
    /// when there is one — e.g. clicking anywhere in "rs_rptTransactionList" inside
    /// "exec dbo.rs_rptTransactionList" returns "dbo.rs_rptTransactionList", so ParseTableRef
    /// can split it normally. Returns null when the click didn't land on/against a word (e.g.
    /// whitespace, punctuation).</summary>
    private string? GetIdentifierAt(int charIndex)
    {
        var text = _scriptBox.Text;
        if (text.Length == 0) return null;
        charIndex = Math.Clamp(charIndex, 0, text.Length - 1);

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '#';

        if (!IsWordChar(text[charIndex]))
        {
            if (charIndex > 0 && IsWordChar(text[charIndex - 1])) charIndex--;
            else return null;
        }

        var start = charIndex;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        var end = charIndex;
        while (end < text.Length - 1 && IsWordChar(text[end + 1])) end++;
        end++; // make exclusive

        var word = text[start..end];

        if (start > 0 && text[start - 1] == '.')
        {
            var qEnd = start - 1;
            var qStart = qEnd;
            while (qStart > 0 && IsWordChar(text[qStart - 1])) qStart--;
            if (qStart < qEnd)
                word = text[qStart..qEnd] + "." + word;
        }

        return word;
    }

    private async Task ShowSuggestionsAsync()
    {
        if (!_suggestOn) return;
        await EnsureTableNamesLoadedAsync();
        var cols = await GetReferencedColumnsAsync(UseSysDatabase);

        var wordStart = GetCurrentWordStart();
        var caret = _scriptBox.SelectionStart;
        var currentWord = caret > wordStart ? _scriptBox.Text[wordStart..caret] : "";

        var candidates = SqlSyntaxHighlighter.KeywordList
            .Concat(_tableNameCache)
            .Concat(cols)
            .Distinct()
            .Where(c => currentWord.Length == 0 || c.StartsWith(currentWord, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToList();

        if (candidates.Count == 0 || _suggestPopup is null) { HideSuggestions(); return; }

        _suggestPopup.Items.Clear();
        _suggestPopup.Items.AddRange(candidates.Cast<object>().ToArray());
        _suggestPopup.SelectedIndex = 0;

        var caretScreenPos = _scriptBox.PointToScreen(_scriptBox.GetPositionFromCharIndex(caret));
        var localPos = PointToClient(caretScreenPos);
        _suggestPopup.Location = new Point(localPos.X, localPos.Y + 18);
        _suggestPopup.Visible = true;
        _suggestPopup.BringToFront();
    }

    private void MoveSuggestSelection(int delta)
    {
        if (_suggestPopup is null || _suggestPopup.Items.Count == 0) return;
        var idx = _suggestPopup.SelectedIndex + delta;
        if (idx < 0) idx = 0;
        if (idx >= _suggestPopup.Items.Count) idx = _suggestPopup.Items.Count - 1;
        _suggestPopup.SelectedIndex = idx;
    }

    private void AcceptSuggestion()
    {
        if (_suggestPopup is not { Visible: true } || _suggestPopup.SelectedItem is not string chosen)
        {
            HideSuggestions();
            return;
        }

        var wordStart = GetCurrentWordStart();
        var caret = _scriptBox.SelectionStart;
        _scriptBox.Select(wordStart, caret - wordStart);
        _scriptBox.SelectedText = chosen;
        HideSuggestions();
        _scriptBox.Focus();
    }

    private void HideSuggestions()
    {
        if (_suggestPopup is not null) _suggestPopup.Visible = false;
    }

    private void BeautyFormat()
    {
        if (_scriptBox.TextLength == 0) return;
        var formatter = new SqlFormatterService();
        
        // Nếu đang bôi đen, chỉ format vùng bôi đen. Nếu không, format toàn bộ.
        if (_scriptBox.SelectionLength > 0)
        {
            var selStart = _scriptBox.SelectionStart;
            var formatted = formatter.Format(_scriptBox.SelectedText);
            _scriptBox.SelectedText = formatted;
            SqlSyntaxHighlighter.Apply(_scriptBox);
            _undoRedo.Checkpoint();
            _scriptBox.Select(selStart, formatted.Length);
        }
        else
        {
            var formatted = formatter.Format(_scriptBox.Text);
            _scriptBox.Text = formatted;
            SqlSyntaxHighlighter.Apply(_scriptBox);
            _undoRedo.Checkpoint();
        }
    }

    private void InsertText(string text)
    {
        // Chèn script vào vị trí con trỏ chuột hiện tại
        _scriptBox.SelectedText = text;
        SqlSyntaxHighlighter.Apply(_scriptBox);
        _undoRedo.Checkpoint();
        _scriptBox.Focus();
    }
}
