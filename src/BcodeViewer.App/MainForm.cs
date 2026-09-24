using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BcodeViewer.App.Host;
using BcodeViewer.App.Settings;
using BcodeViewer.App.UI;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BcodeViewer.App;

/// <summary>Tags a project-group header TreeNode — see RefreshProjectTree/BuildTreeContextMenu.</summary>
internal sealed record ProjectGroupTag(string ProjectName);

/// <summary>
/// Shell window: a left panel listing previously-opened files grouped by project — same
/// layout as FCodeViewer's own ("VPMILK (2)", "VTC (3)", ... each expandable with its
/// files listed underneath, "Projects: N - Files: M" summary above it) — and a WebView2
/// filling the rest, hosting index.html (a single-document Monaco editor plus an AI chat
/// panel — no tab strip, matching FCodeViewer: opening another file replaces what's
/// shown, and the left tree here is the only file switcher). Also matches FCodeViewer's
/// own toolbar (Save/Save As/Undo/Redo/Comment/Bookmark/Next), file-path breadcrumb, and
/// status bar (Ln/Col, language, last-modified) — this form's C# side owns all of that
/// native WinForms chrome, calling into the page's Monaco actions via ExecuteScriptAsync;
/// the bridge object is what the page calls back into for file I/O, AI, and native pickers
/// (Save As needs a real file dialog, which only this side can show).
/// </summary>
public class MainForm : Form
{
    /// <summary>Host name the page is served under (see the WebView2 setup in
    /// MainForm_Load). Must not be a real registrable domain — WebView2 intercepts it
    /// before any DNS lookup, and a name that also exists publicly would be confusing to
    /// anyone reading a stack trace.</summary>
    private const string WebVirtualHost = "bcodeviewer.local";

    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly WebView2 _claudeWebView = new() { Dock = DockStyle.Fill }; 
    private readonly TreeView _tree = new() { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true };
    private readonly Label _projectsHeader = new()
    {
        Dock = DockStyle.Top, Height = 22, TextAlign = ContentAlignment.MiddleLeft,
        Padding = new Padding(6, 0, 0, 0)
    };
    private readonly FlowLayoutPanel _breadcrumb = new() { Dock = DockStyle.Top, Height = 26, WrapContents = false, Padding = new Padding(6, 4, 0, 0) };
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _posLabel = new("Ln 1, Col 1");
    private readonly ToolStripStatusLabel _langLabel = new("");
    private readonly ToolStripStatusLabel _modifiedLabel = new("");
    private readonly ViewerSettings _settings = ViewerSettings.Load();
    private readonly RecentFilesStore _recentFiles = RecentFilesStore.Load();
    private readonly string? _initialFile;
    private string _projectName; // mutable — an external open (see OpenExternalRequest) can arrive from a different project than this window started with
    private EditorBridge? _bridge;
    private string? _activePath; // currently-open file — used to keep the tree's highlight on it across rebuilds
    private bool _pageReady; // true once the WebView2 page has finished its first navigation and window.bcodeViewer exists
    private readonly Queue<(string Path, string ProjectName)> _pendingExternalOpens = new();
    /// <summary>What to do once the page confirms it closed the document — see
    /// RemoveTreeNode/OnFileClosed. Null when the close wasn't requested by the host.</summary>
    private Action? _afterClose;
    private Panel? _leftPanel; // sidebar — re-coloured by ApplyTheme on every theme switch
    private readonly ToolStripMenuItem _themeMenu = new("Theme");
    private TreeNode? _hotNode; // row currently under the mouse — shows the copy/close icons, like a VSCode list row
    private readonly Dictionary<TreeNode, (Rectangle Copy, Rectangle Close)> _rowIcons = new();
    private readonly ToolTip _toolTip = new();

    public MainForm(string? initialFile, string projectName)
    {
        _initialFile = initialFile;
        _projectName = string.IsNullOrWhiteSpace(projectName) ? "#Other" : projectName;

        // Before any control is built: every control's colours are read from
        // ThemeManager.Current as it's constructed, so the saved theme has to be active
        // first or the window is built in Dark+ and only corrected on the first switch.
        ThemeManager.SetTheme(_settings.ThemeId, _settings.FollowSystemTheme);

        Text = "BcodeViewer";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        
        // <ApplicationIcon> in the csproj only sets the .exe file's own icon (Explorer,
        // taskbar pin) — the running window's titlebar/taskbar icon needs Form.Icon set
        // explicitly, so the icon file also ships next to the exe (see the csproj) to load
        // here at runtime.
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (File.Exists(iconPath)) Icon = new Icon(iconPath);
        }
        catch { /* missing/corrupt icon file — fall back to the default WinForms icon */ }

        _projectsHeader.Font = new Font(Font, FontStyle.Bold);

        var menu = new MenuStrip();
        var fileMenu = new ToolStripMenuItem("File");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("New from Template...", null, (_, _) => NewFromTemplate())
        {
            ShortcutKeys = Keys.Control | Keys.N,
        });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Lịch sử file...", null, (_, _) => _ = ExecJsAsync("openHistory()"))
        {
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.H,
            // The editor registers the same chord itself (see editor.js), for when focus is
            // inside the WebView2 and never reaches this menu's shortcut handling.
            ShortcutKeyDisplayString = "Ctrl+Shift+H",
        });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()));
        menu.Items.Add(fileMenu);

        // Everything that shows or hides a panel, in one menu. The editor registers the
        // same chords itself (see editor.js) for when focus is inside the WebView2 and the
        // key never reaches this menu's shortcut handling — the ShortcutKeyDisplayString
        // here is the discoverable half of that pair, not a second binding.
        var viewMenu = new ToolStripMenuItem("View");
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Tìm trong project...", null, (_, _) => _ = ExecJsAsync("openSearch()"))
        {
            ShortcutKeyDisplayString = "Ctrl+Shift+F",
        });
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Bảng Problems", null, (_, _) => _ = ExecJsAsync("toggleProblemsPanel()"))
        {
            ShortcutKeyDisplayString = "Ctrl+Shift+M",
        });
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Outline", null, (_, _) => _ = ExecJsAsync("toggleOutlinePanel()"))
        {
            ShortcutKeyDisplayString = "Ctrl+Shift+U",
        });
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Chạy SQL tại con trỏ", null, (_, _) => _ = ExecJsAsync("runSql()"))
        {
            ShortcutKeyDisplayString = "Ctrl+Enter",
        });
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Tách đôi khung soạn thảo", null, (_, _) => _ = ExecJsAsync("toggleSplit()"))
        {
            ShortcutKeyDisplayString = "Ctrl+\\",
        });
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Tìm nơi sử dụng", null, (_, _) => _ = ExecJsAsync("findReferences()"))
        {
            ShortcutKeyDisplayString = "Shift+F12",
        });
        menu.Items.Add(viewMenu);

        var helpMenu = new ToolStripMenuItem("Help");
        helpMenu.DropDownItems.Add(new ToolStripMenuItem("Giới thiệu...", null, (_, _) => ShowAbout()));
        menu.Items.Add(helpMenu);

        // A top-level "Theme" menu rather than burying it in Settings: it's the one setting
        // people change on a whim (bright room, screen share, time of day) and it applies
        // instantly, so it shouldn't need a modal and an OK button.
        BuildThemeMenu();
        menu.Items.Add(_themeMenu);

        MainMenuStrip = menu;

        var toolStrip = new ToolStrip();
        toolStrip.Items.Add(new ToolStripButton("Save", null, (_, _) => _ = ExecJsAsync("saveActive()")));
        toolStrip.Items.Add(new ToolStripButton("Save As", null, (_, _) => _ = ExecJsAsync("saveActiveAs()")));
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(new ToolStripButton("Undo", null, (_, _) => _ = ExecJsAsync("undo()")));
        toolStrip.Items.Add(new ToolStripButton("Redo", null, (_, _) => _ = ExecJsAsync("redo()")));
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(new ToolStripButton("Comment", null, (_, _) => _ = ExecJsAsync("toggleComment()")));
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(new ToolStripButton("Bookmark", null, (_, _) => _ = ExecJsAsync("toggleBookmark()")));
        toolStrip.Items.Add(new ToolStripButton("● Next", null, (_, _) => _ = ExecJsAsync("nextBookmark()")));
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(new ToolStripButton("Refresh", null, (_, _) => _ = ExecJsAsync("refreshActive()")));
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(new ToolStripButton("Hint", null, (_, _) => OpenHintCode()));
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(new ToolStripButton("Tìm project", null, (_, _) => _ = ExecJsAsync("openSearch()")) { ToolTipText = "Tìm trong toàn bộ project (Ctrl+Shift+F)" });
        toolStrip.Items.Add(new ToolStripButton("Problems", null, (_, _) => _ = ExecJsAsync("toggleProblemsPanel()")) { ToolTipText = "Bảng lỗi/cảnh báo (Ctrl+Shift+M)" });
        toolStrip.Items.Add(new ToolStripButton("Outline", null, (_, _) => _ = ExecJsAsync("toggleOutlinePanel()")) { ToolTipText = "Cấu trúc file (Ctrl+Shift+U)" });
        // No leading ▶ glyph: Segoe UI has no arrow there, so WinForms falls back to a font
        // for the whole caption that drops the Vietnamese diacritics — "Chạy" rendered as
        // "Chay".
        toolStrip.Items.Add(new ToolStripButton("Chạy SQL", null, (_, _) => _ = ExecJsAsync("runSql()")) { ToolTipText = "Chạy câu SQL tại con trỏ trên WS Bcode đang chọn (Ctrl+Enter)" });
        toolStrip.Items.Add(new ToolStripButton("Tách đôi", null, (_, _) => _ = ExecJsAsync("toggleSplit()")) { ToolTipText = @"Tách đôi khung soạn thảo (Ctrl+\)" });
        
        toolStrip.Items.Add(new ToolStripSeparator());
        var toggleClaudeBtn = new ToolStripButton("🤖 Claude Sidebar", null, (_, _) => { });
        toolStrip.Items.Add(toggleClaudeBtn);

        _statusStrip.Items.Add(_posLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true }); // pushes the rest to the right
        _statusStrip.Items.Add(_langLabel);
        _statusStrip.Items.Add(new ToolStripSeparator());
        _statusStrip.Items.Add(_modifiedLabel);

        // One click switches files — matching the request: no need to double-click, and the
        // clicked node stays highlighted (see the DrawNode handler below) so it's obvious
        // which file is currently open, the same way FCodeViewer's own left panel does.
        _tree.NodeMouseClick += (_, e) =>
        {
            if (e.Node != null && _rowIcons.TryGetValue(e.Node, out var icons))
            {
                if (icons.Close.Contains(e.Location)) { RemoveTreeNode(e.Node); return; }
                if (icons.Copy.Contains(e.Location)) { CopyGenUpdateScriptPath(e.Node); return; }
            }
            if (e.Button == MouseButtons.Right)
            {
                _tree.SelectedNode = e.Node;
                BuildTreeContextMenu(e.Node).Show(_tree, e.Location);
                return;
            }
            _tree.SelectedNode = e.Node;
            if (e.Node?.Tag is string path && File.Exists(path))
                _ = OpenFileInPageAsync(path);
        };
        _tree.AfterSelect += (_, _) => _tree.Invalidate();

        // The copy/close icons only show on the row under the mouse (plus the selected row,
        // drawn in DrawNode below) — same "appears on hover" behavior as the reference
        // screenshot, rather than cluttering every row all the time.
        _tree.MouseMove += (_, e) =>
        {
            var node = _tree.GetNodeAt(e.Location);
            if (node == _hotNode) return;
            InvalidateRow(_hotNode);
            _hotNode = node;
            InvalidateRow(_hotNode);
        };
        _tree.MouseLeave += (_, _) =>
        {
            if (_hotNode is null) return;
            InvalidateRow(_hotNode);
            _hotNode = null;
        };

        // TreeView's own selection box (system-drawn, HideSelection=false) doesn't follow the
        // dark theme and reads as barely visible — OwnerDrawText only replaces the text/
        // background painting (icons and expand glyphs are still native), giving a full-width
        // accent-colored highlight bar on whichever node is selected, plus honoring the
        // per-node ForeColor OnDirtyChanged sets for the unsaved-changes yellow.
        _tree.DrawMode = TreeViewDrawMode.OwnerDrawText;
        _tree.DrawNode += (_, e) =>
        {
            if (e.Node is null) return;
            var selected = e.Node == _tree.SelectedNode;
            var rowBounds = new Rectangle(0, e.Bounds.Top, _tree.ClientSize.Width, e.Bounds.Height);
            using (var bg = new SolidBrush(selected ? AppColors.Selection : AppColors.Panel))
                e.Graphics.FillRectangle(bg, rowBounds);
            if (selected)
                using (var accentPen = new Pen(AppColors.Accent))
                    e.Graphics.DrawRectangle(accentPen, rowBounds.X, rowBounds.Y, rowBounds.Width - 1, rowBounds.Height - 1);

            var isGroup = e.Node.Tag is ProjectGroupTag;
            var textColor = e.Node.ForeColor != Color.Empty ? e.Node.ForeColor
                : isGroup ? AppColors.Text
                : selected ? AppColors.Text : AppColors.TextMuted;
            TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont ?? _tree.Font,
                new Point(e.Bounds.Left, e.Bounds.Top), textColor, Color.Transparent);

            // Copy (.f script path) + ✕ (remove from list) icons, right-aligned — only for
            // file rows, and only while hovered or selected, matching the reference
            // screenshot's "icons appear on the active row" behavior instead of always-on
            // clutter. Rectangles are recorded for NodeMouseClick's hit test above.
            if (!isGroup && (selected || e.Node == _hotNode))
            {
                const int iconSize = 14, gap = 4, rightPad = 6;
                var iconTop = rowBounds.Top + (rowBounds.Height - iconSize) / 2;
                var closeRect = new Rectangle(rowBounds.Right - rightPad - iconSize, iconTop, iconSize, iconSize);
                var copyRect = new Rectangle(closeRect.Left - gap - iconSize, iconTop, iconSize, iconSize);
                _rowIcons[e.Node] = (copyRect, closeRect);

                using var iconPen = new Pen(AppColors.TextMuted);
                var backRect = new Rectangle(copyRect.X, copyRect.Y + 3, copyRect.Width - 3, copyRect.Height - 3);
                var frontRect = new Rectangle(copyRect.X + 3, copyRect.Y, copyRect.Width - 3, copyRect.Height - 3);
                e.Graphics.DrawRectangle(iconPen, backRect);
                using (var eraseBrush = new SolidBrush(selected ? AppColors.Selection : AppColors.Panel))
                    e.Graphics.FillRectangle(eraseBrush, frontRect);
                e.Graphics.DrawRectangle(iconPen, frontRect);

                TextRenderer.DrawText(e.Graphics, "✕", _tree.Font, closeRect, AppColors.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
            {
                _rowIcons.Remove(e.Node);
            }
        };
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete || _tree.SelectedNode is null) return;
            e.Handled = true;
            RemoveTreeNode(_tree.SelectedNode);
        };

        // Held as a field, not just a local, because ApplyTheme has to re-colour it on
        // every theme switch (ThemeManager.Apply paints plain Panels with the window
        // background; this one is the sidebar and wants the panel colour).
        var leftPanel = _leftPanel = new Panel { Dock = DockStyle.Fill };
        leftPanel.Controls.Add(_tree);
        leftPanel.Controls.Add(_projectsHeader);

        // BƯỚC MỚI: Tách thêm 1 cấp SplitContainer để chứa Editor và Claude Web
        var editorSplit = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6, Orientation = Orientation.Vertical };
        editorSplit.Panel1.Controls.Add(_webView);
        editorSplit.Panel2.Controls.Add(_claudeWebView);
        editorSplit.Panel1MinSize = 100;
        editorSplit.Panel2MinSize = 100;

        // Mặc định ẩn Web Sidebar đi cho gọn, khi nào cần mới bấm nút hiện ra
        editorSplit.Panel2Collapsed = true;

        // Đợi khi Form thực sự được vẽ lên màn hình và có kích thước chuẩn mới chia tỷ lệ (70% Editor, 30% Claude)
        editorSplit.HandleCreated += (_, _) =>
        {
            try { editorSplit.SplitterDistance = (int)(editorSplit.Width * 0.7); } catch { }
        };

        // Xử lý sự kiện bấm nút Ẩn/Hiện Claude
        toggleClaudeBtn.Click += (_, _) => {
            editorSplit.Panel2Collapsed = !editorSplit.Panel2Collapsed;

            // Focus vào ô chat Claude nếu vừa mở ra
            if (!editorSplit.Panel2Collapsed)
            {
                _claudeWebView.Focus();

                // Claude Sidebar chỉ là trang claude.ai thật nhúng vào — bản thân trang
                // không có cách nào tự biết Bee đang mở file nào trong BcodeViewer (khác với
                // extension Chrome/Excel chính chủ, có quyền đọc nội dung tab/sheet đang mở).
                // Nên mỗi lần mở panel ra, tự dán nội dung file đang mở vào ô chat để Bee chỉ
                // cần gõ câu hỏi rồi gửi — xem InjectFileContextIntoClaudeAsync.
                // BeginInvoke: đợi panel vừa hiện ra được layout xong rồi mới focus + dán, để
                // WebView thật sự nhận focus (cần cho claude.ai gói nội dung thành thẻ PASTED).
                if (_activePath is { } pathToInject)
                    BeginInvoke(() =>
                    {
                        _claudeWebView.Focus();
                        _ = InjectFileContextIntoClaudeAsync(pathToInject);
                    });
            }
        };

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(leftPanel);
        split.Panel2.Controls.Add(editorSplit); // Add editorSplit thay vì _webView
        split.Panel1MinSize = 0;
        split.Panel2MinSize = 0;
        split.SizeChanged += (_, _) =>
        {
            if (split.Width <= 0) return;
            var clamped = Math.Max(0, Math.Min(260, split.Width - split.SplitterWidth - 200));
            if (split.SplitterDistance != clamped) split.SplitterDistance = clamped;
        };

        // Same-Dock controls claim their edge in the order they're added — last added
        // ends up outermost — so this list is deliberately in reverse of how it reads
        // top-to-bottom on screen (Menu, Toolbar, Breadcrumb, then the split filling
        // the rest, then the status bar at the very bottom).
        Controls.Add(split);
        Controls.Add(_statusStrip);
        Controls.Add(_breadcrumb);
        Controls.Add(toolStrip);
        Controls.Add(menu);

        ApplyTheme();

        // Static event — without the unsubscribe this form would be kept alive (and still
        // re-theming into disposed controls) after it closes, which matters here because
        // BcodeViewer is launched repeatedly, one process per file.
        ThemeManager.ThemeChanged += OnThemeChanged;
        FormClosed += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;

        Load += MainForm_Load;
    }

    /// <summary>
    /// Re-skins the native chrome. Called once from the constructor and again on every
    /// theme change, rather than the tweaks below living inline at construction — they
    /// have to run after ThemeManager.Apply each time, since Apply paints generic Panels
    /// with the window background and knows nothing about which of them is the sidebar.
    /// </summary>
    private void ApplyTheme()
    {
        ThemeManager.Apply(this);
        _breadcrumb.BackColor = AppColors.PanelAlt;
        if (_leftPanel is not null) _leftPanel.BackColor = AppColors.Panel;
        _projectsHeader.BackColor = AppColors.PanelAlt;
        _projectsHeader.ForeColor = AppColors.TextMuted;
    }

    /// <summary>
    /// Rebuilds the Theme menu, including which entry is ticked. Rebuilt rather than just
    /// re-ticked because the whole drop-down also has to be re-skinned after a switch — its
    /// items keep the colours they were given when they were created.
    /// </summary>
    private void BuildThemeMenu()
    {
        _themeMenu.DropDownItems.Clear();

        // Grouped rather than one flat list: at this many themes a single column is a wall
        // of names, and "which of these is a light one" is the question someone reaching
        // for this menu is actually asking. Headers are disabled items, so they read as
        // labels and can't be clicked by accident.
        AddThemeGroup("Tối", isDark: true);
        _themeMenu.DropDownItems.Add(new ToolStripSeparator());
        AddThemeGroup("Sáng", isDark: false);

        _themeMenu.DropDownItems.Add(new ToolStripSeparator());
        _themeMenu.DropDownItems.Add(new ToolStripMenuItem(
            "Theo Windows (sáng/tối)", null, (_, _) => ThemeManager.SetTheme(null, followSystem: true))
        {
            Checked = ThemeManager.FollowSystem,
            CheckOnClick = false,
        });

        ThemeManager.ApplyMenu(_themeMenu.DropDown);
    }

    private void AddThemeGroup(string header, bool isDark)
    {
        _themeMenu.DropDownItems.Add(new ToolStripMenuItem(header) { Enabled = false });

        foreach (var theme in ThemeCatalog.All.Where(t => t.IsDark == isDark))
        {
            var id = theme.Id;
            _themeMenu.DropDownItems.Add(new ToolStripMenuItem("   " + theme.Name, null, (_, _) => ThemeManager.SetTheme(id))
            {
                // Ticked only when it's the active theme AND it was chosen explicitly:
                // under "theo Windows" the resolved theme is a consequence, not a choice,
                // and ticking it there would suggest clicking it again is a no-op when it
                // actually turns following off.
                Checked = !ThemeManager.FollowSystem && ThemeManager.Current.Id == id,
                CheckOnClick = false,
            });
        }
    }

    private void OnThemeChanged()
    {
        if (InvokeRequired) { BeginInvoke(OnThemeChanged); return; }
        if (IsDisposed) return;

        ApplyTheme();

        // The tree is owner-drawn (see the DrawNode handler) so its rows pick up the new
        // palette on the next paint, but node ForeColors set outside that handler — the
        // amber "unsaved" marker in OnDirtyChanged — are stored on the node and have to be
        // rebuilt. RefreshProjectTree does both, and re-renders the breadcrumb's labels,
        // whose colours are likewise assigned per-label as they're created.
        RefreshProjectTree();
        if (_activePath is not null) RenderBreadcrumb(_activePath);
        _tree.Invalidate();

        // ...and the WebView2 half, which holds its own copy of the palette as CSS
        // variables plus a Monaco theme (see Web/theme.js).
        _ = ExecJsAsync("reloadTheme()");

        _settings.ThemeId = ThemeManager.Current.Id;
        _settings.FollowSystemTheme = ThemeManager.FollowSystem;
        _settings.Save();
        BuildThemeMenu();
    }

    private async void MainForm_Load(object? sender, EventArgs e)
    {
        _bridge = new EditorBridge(_settings, ChooseSaveAsPath);

        // Each process gets its own WebView2 profile folder. Left unspecified, WebView2
        // defaults to one folder shared by every instance of this exe (keyed off the exe's
        // own path) — that folder is held under an exclusive OS lock for as long as any
        // instance is running, so opening a second BcodeViewer window while the first is
        // still open failed immediately with COMException 0x800700AA ("The requested
        // resource is in use"). BcodeViewer is meant to be launched once per file (from
        // Bcode.App's File Lookup, or by hand) — more than one running at once is the
        // normal case here, not a rare edge case, so profiles must not collide.
        var profileRoot = Path.Combine(Path.GetTempPath(), "BcodeViewer.WebView2");
        CleanupStaleProfiles(profileRoot);
        var userDataFolder = Path.Combine(profileRoot, Environment.ProcessId.ToString());
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);

        await _webView.EnsureCoreWebView2Async(environment);
        _webView.CoreWebView2.AddHostObjectToScript("host", _bridge);
        _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

        // ---- THÊM ĐOẠN KHỞI TẠO CLAUDE WEB ----
        // Tạo một thư mục riêng biệt cố định để lưu phiên đăng nhập (Cookie) của Claude
        var claudeProfileDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "ClaudeWebProfile");
        
        // Cho phép bật devtools và các tính năng web hiện đại
        var claudeEnv = await CoreWebView2Environment.CreateAsync(userDataFolder: claudeProfileDir);
        await _claudeWebView.EnsureCoreWebView2Async(claudeEnv);

        // 1. Giữ User-Agent Chrome chuẩn
        _claudeWebView.CoreWebView2.Settings.UserAgent = 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        // 2. Bật quyền script & DOM storage
        _claudeWebView.CoreWebView2.Settings.IsScriptEnabled = true;
        _claudeWebView.CoreWebView2.Settings.IsWebMessageEnabled = true;

        // 2.5. CoreWebView2Settings.UserAgent ở trên chỉ đổi được navigator.userAgent (và header
        // User-Agent kiểu cũ) — nó KHÔNG đổi được "Client Hints" (các header Sec-CH-UA*, và
        // navigator.userAgentData phía JS), vốn vẫn báo đúng runtime Chromium/WebView2 thật bên
        // dưới. Kết quả là trang claude.ai nhận được hai nguồn thông tin trình duyệt mâu thuẫn
        // nhau: UA nói "Chrome 126" nhưng Client Hints lại nói "WebView2/Edge phiên bản khác" —
        // đây đúng là dấu hiệu trình duyệt tự động/nhúng mà nhiều app web (kể cả claude.ai) dùng
        // để âm thầm tắt bớt tính năng thay vì chặn hẳn, khớp với triệu chứng Bee gặp: đăng nhập
        // vẫn vào được bình thường, nhưng ô chat không hiện lên như khi mở bằng Chrome/Edge thật
        // (nơi UA và Client Hints luôn khớp nhau) hoặc như bên extension Chrome/Excel (chạy trong
        // chính trình duyệt/host thật, không cần giả UA).
        // Cách xử lý: chặn mọi request tới claude.ai/anthropic.com và xoá hẳn các header
        // Sec-CH-UA* trước khi gửi đi, để trang không còn cơ sở nào để so sánh UA cổ điển với
        // Client Hints nữa (không có Client Hints thì không mâu thuẫn).
        _claudeWebView.CoreWebView2.AddWebResourceRequestedFilter("https://*.claude.ai/*", CoreWebView2WebResourceContext.All);
        _claudeWebView.CoreWebView2.AddWebResourceRequestedFilter("https://*.anthropic.com/*", CoreWebView2WebResourceContext.All);
        _claudeWebView.CoreWebView2.WebResourceRequested += (_, args) =>
        {
            var headers = args.Request.Headers;
            foreach (var name in new[]
            {
                "sec-ch-ua", "sec-ch-ua-mobile", "sec-ch-ua-platform",
                "sec-ch-ua-full-version", "sec-ch-ua-full-version-list", "sec-ch-ua-platform-version",
            })
            {
                if (headers.Contains(name)) headers.RemoveHeader(name);
            }
        };

        // 3. XỬ LÝ NEW WINDOW: Không tự ý Navigate đè lên trang chính nếu là URL rỗng hoặc OAuth background
        _claudeWebView.CoreWebView2.NewWindowRequested += (sender, args) =>
        {
            var uri = args.Uri;
            if (!string.IsNullOrWhiteSpace(uri) && uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // QUAN TRỌNG: accounts.google.com (gồm cả trang gsi/transform của luồng "Sign in
                // with Google") BẮT BUỘC phải mở như một popup window THẬT (window.open() thật) —
                // thư viện Google Identity Services báo kết quả đăng nhập về trang chính qua
                // window.opener.postMessage(...) và tự đóng chính nó bằng window.close() khi xong,
                // cả hai chỉ hoạt động khi trình duyệt công nhận cửa sổ đó "do script mở ra".
                // Trước đây code này chặn popup lại (args.Handled = true) rồi Navigate ngay trên
                // _claudeWebView chính — khiến panel chính (không phải popup do script mở) bị điều
                // hướng thẳng sang trang gsi/transform và MẮC KẸT ở đó vĩnh viễn: trang transform cố
                // window.close() nhưng bị trình duyệt từ chối ("Scripts may close only the windows
                // that were opened by them" — xem trong DevTools), và vì không có window.opener nên
                // không báo được kết quả đăng nhập về claude.ai. Đây chính là lý do "đăng nhập thành
                // công nhưng ô chat không hiện lên": panel đang đứng ở trang Google, không bao giờ
                // quay lại claude.ai.
                // Vì vậy không can thiệp gì với accounts.google.com nữa — để WebView2 tự mở popup
                // thật theo mặc định. Chỉ còn hijack link anthropic.com (vd "Tìm hiểu thêm") để mở
                // ngay trên panel chính thay vì bật popup gây rối mắt — link đó không cần cơ chế
                // opener/close như OAuth nên hijack không sao.
                if (uri.Contains("anthropic.com") && !uri.Contains("accounts.google.com"))
                {
                    args.Handled = true;
                    _claudeWebView.CoreWebView2.Navigate(uri);
                }
            }
        };

        // 4. Mở trang chính thức claude.ai
        _claudeWebView.CoreWebView2.Navigate("https://claude.ai/");
        // The page is a single-document editor (see editor.js) — every file it opens
        // (the initial one, or any later one via F12/Open File Config/the left tree)
        // raises this the same way, so there's one path that updates the recent-files
        // tree, breadcrumb, and window title instead of duplicating that logic per
        // open-site.
        _bridge.FileOpened += path =>
        {
            if (InvokeRequired) { BeginInvoke(() => OnFileOpened(path)); return; }
            OnFileOpened(path);
        };
        _bridge.FileClosed += path =>
        {
            if (InvokeRequired) { BeginInvoke(() => OnFileClosed(path)); return; }
            OnFileClosed(path);
        };
        _bridge.DirtyChanged += (path, isDirty) =>
        {
            if (InvokeRequired) { BeginInvoke(() => OnDirtyChanged(path, isDirty)); return; }
            OnDirtyChanged(path, isDirty);
        };
        _bridge.CursorChanged += (line, col) =>
        {
            if (InvokeRequired) { BeginInvoke(() => _posLabel.Text = $"Ln {line}, Col {col}"); return; }
            _posLabel.Text = $"Ln {line}, Col {col}";
        };

        // Served over a virtual host rather than the file:// path this used to navigate to.
        // The reason is web workers: Chromium treats every file:// document as an opaque
        // origin and refuses to construct a Worker from one, so Monaco's language services
        // (vs/language/typescript, json, css, html — all already vendored in Web/vs) were
        // silently falling back to running on the page's main thread. That fallback works,
        // but it does the parsing/IntelliSense for a .js or .json file on the same thread
        // that paints the editor, which is exactly what makes typing feel sticky in a big
        // file. Under https://<virtual host>/ they load as real workers off-thread.
        //
        // The mapping exposes only the Web folder, read-only-by-convention (nothing writes
        // through it — file I/O still goes through EditorBridge), and needs no network:
        // WebView2 resolves this host name internally and never leaves the machine.
        var webFolder = Path.Combine(AppContext.BaseDirectory, "Web");
        _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            WebVirtualHost, webFolder, CoreWebView2HostResourceAccessKind.Allow);
        _webView.CoreWebView2.Navigate($"https://{WebVirtualHost}/index.html");

        // Driven by the page telling us the editor object exists (see EditorBridge.PageReady
        // for why NavigationCompleted is the wrong signal), and marshalled onto the UI
        // thread because host object calls arrive on whatever thread WebView2 picks.
        _bridge.PageReady += () =>
        {
            if (InvokeRequired) { BeginInvoke(OnPageReady); return; }
            OnPageReady();
        };

        StartPipeServer();
    }

    /// <summary>
    /// The page is up: open whatever this window was launched for, then flush anything a
    /// second launch handed off in the meantime (see Program.cs/StartPipeServer) instead of
    /// silently dropping it.
    /// </summary>
    private async void OnPageReady()
    {
        if (IsDisposed) return;

        if (_initialFile is not null && File.Exists(_initialFile))
            await OpenFileInPageAsync(_initialFile); // triggers editor.js's own NotifyFileOpened
        else
            RefreshProjectTree(); // still show prior history even with nothing to open now

        _pageReady = true;
        while (_pendingExternalOpens.Count > 0)
        {
            var (path, project) = _pendingExternalOpens.Dequeue();
            _projectName = project;
            await OpenFileInPageAsync(path);
        }
    }

    private async Task OpenFileInPageAsync(string path)
    {
        var json = JsonSerializer.Serialize(path);
        await _webView.ExecuteScriptAsync($"window.bcodeViewer && window.bcodeViewer.openFile({json});");
    }

    /// <summary>Listens for hand-offs from a second BcodeViewer launch (see Program.cs's
    /// Mutex check) so that launch's file opens in THIS window instead of a new one —
    /// BcodeViewer is meant to behave as one app instance, not one window per file, matching
    /// the recent-files tree already being the file switcher rather than a tab strip.
    /// Runs for the lifetime of the window; a connection failure just retries the loop.</summary>
    private void StartPipeServer()
    {
        _ = Task.Run(async () =>
        {
            while (!IsDisposed)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        Program.SingleInstancePipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var line = await reader.ReadLineAsync();
                    if (line is null) continue;

                    var parts = line.Split('|', 2);
                    var path = parts[0];
                    var project = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "#Other";
                    if (File.Exists(path)) BeginInvoke(() => OpenExternalRequest(path, project));
                }
                catch
                {
                    if (IsDisposed) return;
                    await Task.Delay(500); // pipe briefly unavailable — don't spin
                }
            }
        });
    }

    /// <summary>Brings this window to the foreground and opens <paramref name="path"/> in
    /// it, same as any other "open a file" trigger (F12, the left tree, Open File Config) —
    /// called from the pipe server above once a second launch hands its file off here.</summary>
    private void OpenExternalRequest(string path, string projectName)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        // Plain Activate() often loses to Windows' foreground-lock (a background process
        // can't just steal focus from whatever the user is currently in) — toggling TopMost
        // is the standard workaround to force this window to the front regardless.
        TopMost = true;
        Activate();
        TopMost = false;

        if (!_pageReady) { _pendingExternalOpens.Enqueue((path, projectName)); return; }
        _projectName = projectName;
        _ = OpenFileInPageAsync(path);
    }

    /// <summary>Fire-and-forget call into one of editor.js's BcodeEditor methods — backs
    /// every toolbar button (Save/Save As/Undo/Redo/Comment/Bookmark/Next/Refresh).</summary>
    /// <summary>
    /// Tears the two WebView2 controls down deliberately, on the UI thread, as the window
    /// closes — instead of leaving it to whatever order Dispose and the finalizer happen to
    /// run in.
    ///
    /// The link being cut first is the one that crosses the native/managed boundary:
    /// AddHostObjectToScript hands the browser process a COM reference to
    /// <see cref="EditorBridge"/>, which in turn holds event handlers that capture this
    /// form. Releasing a COM object from the finalizer thread rather than the STA thread
    /// that created it is a well-known way to deadlock a WinForms process during shutdown,
    /// and the symptom matches what was seen here: the window closes, MainWindowHandle
    /// drops to 0, and the process stays alive — which then locks BcodeViewer.exe so the
    /// next build fails with MSB3021, keeps the single-instance mutex held, and leaves its
    /// WebView2 profile folder behind in %TEMP%.
    ///
    /// Honest about what this is: the hang is intermittent and was not reproduced on
    /// demand, so this is not a proven fix. It is the correct shutdown sequence regardless
    /// — releasing these on the thread that owns them is right whether or not it turns out
    /// to be the cause.
    /// </summary>
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);

        try { _webView.CoreWebView2?.RemoveHostObjectFromScript("host"); }
        catch { /* already torn down, or the browser process is gone */ }

        foreach (var view in new[] { _webView, _claudeWebView })
        {
            try { view.Dispose(); }
            catch { /* disposing twice, or mid-teardown — nothing left to do either way */ }
        }
    }

    /// <summary>
    /// Help &gt; Giới thiệu. Every line is read from the assembly's own attributes rather
    /// than typed here, so the names and the version in this box and the ones Windows shows
    /// on the .exe's Details tab come from the single declaration in the csproj.
    /// </summary>
    private void ShowAbout()
    {
        var assembly = Assembly.GetExecutingAssembly();
        string Meta<T>(Func<T, string> pick) where T : Attribute =>
            assembly.GetCustomAttribute<T>() is { } a ? pick(a) : "";

        var product = Meta<AssemblyProductAttribute>(a => a.Product);
        var authors = Meta<AssemblyCompanyAttribute>(a => a.Company);
        var description = Meta<AssemblyDescriptionAttribute>(a => a.Description);
        var copyright = Meta<AssemblyCopyrightAttribute>(a => a.Copyright);
        // InformationalVersion carries what <Version> was set to; AssemblyVersion is padded
        // to four parts and reads as a different number to anyone comparing them.
        var version = Meta<AssemblyInformationalVersionAttribute>(a => a.InformationalVersion);
        // The SDK appends the source-control hash ("1.0.0+9ec0f65…") when the repo is
        // available; useful in a bug report, noise in a title.
        var plus = version.IndexOf('+');
        var build = plus >= 0 ? version[(plus + 1)..] : "";
        if (plus >= 0) version = version[..plus];

        using var dialog = new Form
        {
            Text = "Giới thiệu",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            Icon = Icon,
            // Lets the box fit whatever the description and the author line turn out to be,
            // instead of a fixed size that clips the moment either grows.
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(420, 0),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top, ColumnCount = 2, Padding = new Padding(16),
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = product, AutoSize = true, Margin = new Padding(0, 0, 0, 2),
            Font = new Font(Font.FontFamily, Font.Size + 4, FontStyle.Bold),
        };
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);

        var row = 1;
        void AddRow(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            layout.Controls.Add(new Label { Text = label, AutoSize = true, ForeColor = AppColors.TextMuted, Margin = new Padding(0, 6, 8, 0) }, 0, row);
            layout.Controls.Add(new Label { Text = value, AutoSize = true, MaximumSize = new Size(300, 0), Margin = new Padding(0, 6, 0, 0) }, 1, row);
            row++;
        }

        AddRow("Tác giả:", authors);
        AddRow("Phiên bản:", version);
        AddRow("Bản dựng:", build);
        AddRow("Mô tả:", description);
        AddRow("", copyright);

        var close = new Button { Text = "Đóng", Width = 90, DialogResult = DialogResult.OK, Anchor = AnchorStyles.Right };
        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(12) };
        buttonRow.Controls.Add(close);

        dialog.Controls.Add(layout);
        dialog.Controls.Add(buttonRow);
        dialog.AcceptButton = close;
        dialog.CancelButton = close;

        ThemeManager.Apply(dialog);
        title.ForeColor = AppColors.Text;
        dialog.ShowDialog(this);
    }

    private Task ExecJsAsync(string call) =>
        _webView.CoreWebView2 is null ? Task.CompletedTask
            : _webView.ExecuteScriptAsync($"window.bcodeViewer && window.bcodeViewer.{call};");

    private void OnFileOpened(string path)
    {
        _activePath = path;
        _recentFiles.Touch(_projectName, path);
        RefreshProjectTree();
        RenderBreadcrumb(path);
        Text = $"BcodeViewer — {Path.GetFileName(path)}";
        _langLabel.Text = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        try { _modifiedLabel.Text = "Modified at " + File.GetLastWriteTime(path).ToString("dd/MM/yyyy HH:mm"); }
        catch { _modifiedLabel.Text = ""; }

        // CHỦ Ý không tự dán lại nội dung file mới vào ô chat mỗi khi Bee chuyển file trong
        // lúc panel Claude đang mở sẵn: làm vậy sẽ ghi đè (selectAll + insertText) ngay cả
        // khi Bee đang gõ dở câu hỏi trong ô chat — phiền hơn là giúp. Việc dán nội dung file
        // chỉ xảy ra đúng một lần, tại thời điểm Bee chủ động bật panel Claude Sidebar lên —
        // xem toggleClaudeBtn.Click ở trên (nơi duy nhất gọi InjectFileContextIntoClaudeAsync).
    }

    /// <summary>
    /// Dán nội dung <paramref name="filePath"/> vào ô nhập chat của trang claude.ai đang nhúng
    /// trong _claudeWebView, để Bee chỉ cần gõ câu hỏi phía sau rồi gửi — bù lại việc panel này
    /// (khác với extension Chrome/Excel chính chủ) không có quyền tự đọc nội dung tab/file đang
    /// mở.
    ///
    /// claude.ai không cung cấp API nào để làm việc này — đây là một script "đoán" cấu trúc
    /// trang: tìm ô contenteditable lớn nhất đang hiển thị ngoài vùng nav/header, gõ dòng giới
    /// thiệu bằng document.execCommand('insertText', ...) rồi phát một sự kiện 'paste' giả lập
    /// mang nội dung file — claude.ai xử lý như Ctrl+V thật nên gói text dài thành thẻ "PASTED".
    /// Trang không nhận paste giả lập thì quay về gõ thẳng nội dung như trước. Nếu Anthropic đổi giao diện trang web, phần dò
    /// ô chat này có thể không còn đúng nữa — nhưng vì chạy độc lập, không được thì chỉ im lặng
    /// bỏ qua (catch rỗng), không làm hỏng gì khác trong app.
    /// </summary>
    private async Task InjectFileContextIntoClaudeAsync(string filePath)
    {
        if (_claudeWebView.CoreWebView2 is null) return;

        string content;
        try
        {
            if (!File.Exists(filePath)) return;
            // Giới hạn mềm ~200KB: dán nguyên cả file to hơn mức này vào ô chat gây rối hơn là
            // giúp ích (và một số trang có giới hạn độ dài input), nên cắt bớt và ghi chú rõ.
            const int maxChars = 200_000;
            content = File.ReadAllText(filePath);
            if (content.Length > maxChars)
                content = content[..maxChars] + "\n\n[... nội dung bị cắt bớt vì file quá dài ...]";
        }
        catch { return; }

        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        // Dòng giới thiệu gõ vào ô chat; nội dung file thì DÁN (paste) để claude.ai tự gói thành
        // thẻ "PASTED" giống hệt lúc Bee tự Ctrl+V — thay vì trước đây gõ thẳng cả file vào ô
        // chat thành một khối chữ dài (execCommand('insertText') chỉ là "gõ chữ", trang không
        // coi đó là paste nên không gói lại).
        var introJson = JsonSerializer.Serialize($"File đang mở trong BcodeViewer: {filePath}\n");
        var contentJson = JsonSerializer.Serialize(content);
        // Dự phòng khi trang không nhận sự kiện paste giả lập (đổi giao diện…): quay về cách cũ.
        var fallbackJson = JsonSerializer.Serialize($"```{ext}\n{content}\n```\n\n");

        var js = $$"""
        (function() {
            var intro = {{introJson}};
            var content = {{contentJson}};
            var fallback = {{fallbackJson}};

            function findComposer() {
                var candidates = Array.prototype.slice.call(
                    document.querySelectorAll('div[contenteditable="true"], textarea'));
                var best = null, bestArea = 0;
                for (var i = 0; i < candidates.length; i++) {
                    var el = candidates[i];
                    var rect = el.getBoundingClientRect();
                    if (rect.width < 100 || rect.height < 20) continue;
                    if (el.closest('nav, header')) continue;
                    var area = rect.width * rect.height;
                    if (area > bestArea) { bestArea = area; best = el; }
                }
                return best;
            }
            function setTextareaValue(el, text) {
                var setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set;
                setter.call(el, text);
                el.dispatchEvent(new Event('input', { bubbles: true }));
            }
            // Giả lập đúng một lần Ctrl+V: sự kiện 'paste' mang theo clipboardData chứa nội dung
            // file. claude.ai xử lý nó như paste thật — text dài thì gói thành thẻ PASTED. Trả về
            // true nếu trang đã nhận xử lý (preventDefault), false nếu không ai xử lý.
            function pasteText(el, text) {
                try {
                    var dt = new DataTransfer();
                    dt.setData('text/plain', text);
                    var ev = new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true });
                    el.dispatchEvent(ev);
                    return ev.defaultPrevented;
                } catch (e) {
                    return false;
                }
            }
            // Đặt con trỏ vào cuối ô chat. claude.ai chỉ gói text dài thành thẻ PASTED khi ô
            // chat thật sự đang giữ focus lúc nhận paste — nếu không, trình soạn thảo tự dán nó
            // như chữ thường. Lần mở Sidebar thứ hai trở đi, trang đã tải sẵn nên script chạy
            // ngay khi panel vừa hiện ra, lúc WebView chưa kịp nhận focus -> ra khối chữ dài.
            function focusComposer(el) {
                el.focus();
                try {
                    var range = document.createRange();
                    range.selectNodeContents(el);
                    range.collapse(false);
                    var sel = window.getSelection();
                    sel.removeAllRanges();
                    sel.addRange(range);
                } catch (e) { }
            }
            function composerHasFocus(el) {
                var active = document.activeElement;
                return document.hasFocus() && active && (active === el || el.contains(active));
            }
            function inject(el) {
                if (el.tagName === 'TEXTAREA' || el.tagName === 'INPUT') {
                    el.focus();
                    setTextareaValue(el, intro + fallback);
                    return;
                }
                // Xoá nội dung cũ trong ô chat rồi gõ dòng giới thiệu.
                focusComposer(el);
                document.execCommand('selectAll', false, null);
                document.execCommand('delete', false, null);
                document.execCommand('insertText', false, intro);
                if (!pasteText(el, content)) {
                    document.execCommand('insertText', false, fallback);
                }
            }
            // Chờ tới khi ô chat thật sự có focus (tối đa ~3 giây) rồi mới dán; hết thời gian
            // mà vẫn chưa có thì vẫn dán (kết quả có thể là chữ thường, nhưng không mất gì).
            function injectWhenFocused(el) {
                var waited = 0;
                (function tryInject() {
                    focusComposer(el);
                    if (composerHasFocus(el) || waited >= 3000) {
                        // thêm một nhịp nhỏ cho trình soạn thảo cập nhật trạng thái focus của nó
                        setTimeout(function() { inject(el); }, 150);
                        return;
                    }
                    waited += 100;
                    setTimeout(tryInject, 100);
                })();
            }

            var attempts = 0;
            var timer = setInterval(function() {
                attempts++;
                var el = findComposer();
                if (el) {
                    clearInterval(timer);
                    injectWhenFocused(el);
                } else if (attempts > 20) {
                    clearInterval(timer);
                }
            }, 250);
        })();
        """;

        try { await _claudeWebView.ExecuteScriptAsync(js); }
        catch { /* claude.ai chưa load xong hoặc đổi cấu trúc trang — bỏ qua, không phá vỡ app chính */ }
    }

    private void OnDirtyChanged(string path, bool isDirty)
    {
        Text = isDirty ? $"BcodeViewer — {Path.GetFileName(path)} •" : $"BcodeViewer — {Path.GetFileName(path)}";

        // The tree is the file switcher (no tab strip — see the class doc comment), so an
        // unsaved file needs its own visual flag there, same idea as an editor's tab dot/
        // asterisk: yellow text on the node until the file is saved back to disk.
        if (FindFileNode(path) is { } node)
        {
            node.ForeColor = isDirty ? AppColors.DirtyMarker : AppColors.Text;
            node.Text = isDirty ? Path.GetFileName(path) + " •" : Path.GetFileName(path);
        }

        if (!isDirty)
        {
            // Save just completed — the on-disk timestamp the status bar shows was captured
            // when the file was opened, so without this it keeps reading the pre-save time
            // until the next file switch. DirtyChanged only ever fires for the currently
            // open document (single-document editor — see editor.js), so this is always
            // the right file to re-stat.
            try { _modifiedLabel.Text = "Modified at " + File.GetLastWriteTime(path).ToString("dd/MM/yyyy HH:mm"); }
            catch { /* file briefly locked right after write — leave the previous label */ }
        }
    }

    private TreeNode? FindFileNode(string path)
    {
        foreach (TreeNode group in _tree.Nodes)
            foreach (TreeNode file in group.Nodes)
                if (file.Tag is string p && string.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                    return file;
        return null;
    }

    /// <summary>"\\server > CustomerPro > FBO > VTC > SP226 > App_Data > ... > file.xml" —
    /// each segment (but the last, the file itself) is clickable and opens that folder in
    /// Explorer, matching FCodeViewer's own clickable File Path bar.</summary>
    /// <summary>
    /// The path strip above the editor. Deliberately not the whole path: a controller lives
    /// at \\server\CustomerPro\FBO\VTC\SP226\App_Data\Controllers\Dir\SVTran.xml, and
    /// spelling all of that out pushes the part that identifies the file — the last three
    /// segments — off the right-hand edge while the left half says the same thing on every
    /// file you open.
    ///
    /// So it starts at the site folder (the one above App_Data, which is what distinguishes
    /// one customer's copy from another) and collapses everything before it into a single
    /// "…" that still opens that folder and carries the full path in its tooltip. A path
    /// with no App_Data in it falls back to the last few segments under the same rule.
    ///
    /// Every segment opens something, including the file itself: clicking it opens the
    /// containing folder with the file selected, which is the thing people were reaching
    /// for when they clicked its name and nothing happened.
    /// </summary>
    private void RenderBreadcrumb(string path)
    {
        _breadcrumb.Controls.Clear();
        var isUnc = path.StartsWith(@"\\");
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        // Absolute path of parts[0..i], so every chip knows what it opens.
        var full = new string[parts.Length];
        var accum = isUnc ? @"\\" : "";
        for (var i = 0; i < parts.Length; i++)
        {
            accum = accum.EndsWith('\\') ? accum + parts[i] : accum + '\\' + parts[i];
            full[i] = accum;
        }

        var appData = Array.FindIndex(parts, p => string.Equals(p, "App_Data", StringComparison.OrdinalIgnoreCase));
        var first = appData > 0 ? appData - 1 : Math.Max(0, parts.Length - VisibleBreadcrumbSegments);

        if (first > 0)
        {
            AddBreadcrumbLink("…", full[first - 1], path, bold: false);
            AddBreadcrumbSeparator();
        }

        for (var i = first; i < parts.Length; i++)
        {
            var isFile = i == parts.Length - 1;
            // The file chip opens its folder with itself selected — OpenFolder already does
            // the /select, form when handed a file.
            AddBreadcrumbLink(parts[i], full[i], full[i], bold: isFile);
            if (!isFile) AddBreadcrumbSeparator();
        }
    }

    /// <summary>How many trailing segments to show when the path has no App_Data to anchor
    /// on. Four covers "…\Controllers\Dir\file.xml" with one level of context.</summary>
    private const int VisibleBreadcrumbSegments = 4;

    private void AddBreadcrumbLink(string text, string target, string tooltip, bool bold)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 0),
            BackColor = Color.Transparent,
            LinkColor = bold ? AppColors.Text : Color.FromArgb(78, 165, 240),
            ActiveLinkColor = AppColors.AccentHover,
            LinkBehavior = LinkBehavior.HoverUnderline,
        };
        if (bold) link.Font = new Font(link.Font, FontStyle.Bold);
        link.Click += (_, _) => _bridge?.OpenFolder(target);
        _toolTip.SetToolTip(link, tooltip);
        _breadcrumb.Controls.Add(link);
    }

    private void AddBreadcrumbSeparator() =>
        _breadcrumb.Controls.Add(new Label
        {
            Text = "  ›  ", AutoSize = true, Margin = new Padding(0, 2, 0, 0),
            ForeColor = AppColors.TextMuted, BackColor = Color.Transparent,
        });

    /// <summary>"VPMILK (2)" / "VTC (3)" / ... — same grouping FCodeViewer's own left
    /// panel shows, sourced from <see cref="_recentFiles"/> rather than browsing the
    /// filesystem: this is a history of what's been opened, not a directory listing.
    /// <see cref="_projectsHeader"/> mirrors FCodeViewer's own "Projects: N - Files: M".</summary>
    private void RefreshProjectTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        // Old TreeNode instances are gone after this rebuild — drop anything keyed to them
        // so DrawNode/hit-testing never looks at a stale row.
        _rowIcons.Clear();
        _hotNode = null;
        var groups = _recentFiles.GroupedByProject();
        _projectsHeader.Text = $"Projects: {groups.Count} - Files: {groups.Sum(g => g.Files.Count)}";

        foreach (var (projectName, files) in groups)
        {
            // Tagged with the group's own record type (not a plain string) so context-menu/
            // Delete-key handling can tell a project header apart from a file node (whose
            // Tag is the file path itself) with a simple pattern match.
            var groupNode = new TreeNode($"{projectName} ({files.Count})") { Tag = new ProjectGroupTag(projectName) };
            foreach (var entry in files)
                groupNode.Nodes.Add(new TreeNode(Path.GetFileName(entry.Path)) { Tag = entry.Path, ToolTipText = entry.Path });
            groupNode.Expand();
            _tree.Nodes.Add(groupNode);
        }

        // The tree is rebuilt from scratch on every file switch (it's sourced from
        // _recentFiles, not mutated in place), which would otherwise drop the selection
        // highlight right when it matters most — re-select whichever node is the currently
        // open file so the highlight always tracks it.
        if (_activePath is not null && FindFileNode(_activePath) is { } activeNode)
        {
            _tree.SelectedNode = activeNode;
            activeNode.EnsureVisible();
        }
        _tree.EndUpdate();
    }

    /// <summary>Right-click on the recent-files tree: "Remove from list" for a file node,
    /// "Remove all in this project" for a group header. Only forgets the entry/entries from
    /// BcodeViewer's own history — never touches the file on disk.</summary>
    private ContextMenuStrip BuildTreeContextMenu(TreeNode? node)
    {
        var menu = new ContextMenuStrip();
        if (node is null) return menu;

        switch (node.Tag)
        {
            case string path:
                menu.Items.Add("Remove from list", null, (_, _) => RemoveTreeNode(node));
                menu.Items.Add("Open containing folder", null, (_, _) => _bridge?.OpenFolder(path));
                break;
            case ProjectGroupTag group:
                menu.Items.Add($"Remove all in \"{group.ProjectName}\"", null, (_, _) => RemoveTreeNode(node));
                break;
        }
        return menu;
    }

    private void InvalidateRow(TreeNode? node)
    {
        if (node is null) return;
        _tree.Invalidate(new Rectangle(0, node.Bounds.Top, _tree.ClientSize.Width, node.Bounds.Height));
    }

    /// <summary>Copies the path of the sibling ".f" script FastBusiness expects next to a
    /// Dir/Filter controller's .xml (same folder, same base name — e.g. Controllers\Dir\
    /// SVTran.xml + SVTran.f, per the real File Lookup evidence this tool was built from) —
    /// the path FCode's own "Generate Update Package" tool needs after editing the XML, so
    /// the user doesn't have to retype/browse for it by hand. Copies the computed path
    /// regardless of whether that .f file exists yet (gen-update may be about to create it),
    /// but says so in the tooltip rather than claiming a plain "Copied".</summary>
    private void CopyGenUpdateScriptPath(TreeNode node)
    {
        if (node.Tag is not string path) return;
        var dir = Path.GetDirectoryName(path);
        if (dir is null) return;
        var fPath = Path.Combine(dir, Path.GetFileNameWithoutExtension(path) + ".f");

        try { Clipboard.SetText(fPath); }
        catch { return; } // clipboard held by another app — nothing useful to do

        var message = (File.Exists(fPath) ? "Copied: " : "Copied (not created yet): ") + fPath;
        _toolTip.Show(message, _tree, node.Bounds.Left, node.Bounds.Bottom + 2, 2000);
    }

    /// <summary>
    /// The ✕ on a row (and the tree's "Remove from list" menu). Removing an entry that
    /// happens to be the file currently open has to close it in the EDITOR too — before
    /// this, ✕ only forgot the entry while the document stayed loaded, editable and the
    /// target of Ctrl+S, with the external-change poll still running against it.
    ///
    /// When the open file is involved, the list is not touched here: the page is asked to
    /// close, and the removal happens in <see cref="OnFileClosed"/> once it confirms. That
    /// ordering is what makes "Cancel" at the unsaved-changes prompt leave both sides
    /// agreeing, instead of dropping the row for a file that is still on screen.
    /// </summary>
    private void RemoveTreeNode(TreeNode node)
    {
        switch (node.Tag)
        {
            case string path:
                if (IsActiveFile(path))
                {
                    _afterClose = () => { _recentFiles.Remove(path); RefreshProjectTree(); };
                    _ = ExecJsAsync("closeActive()");
                    return;
                }
                // Not the file on screen, but it may still be open in a background tab —
                // the recent-files list and the open tabs are different sets. Closing it
                // there keeps the ✕ meaning one thing: this file is gone from the window.
                _ = ExecJsAsync($"closeDoc({JsonSerializer.Serialize(path)})");
                _recentFiles.Remove(path);
                break;

            case ProjectGroupTag group:
                // Same rule for a whole project: if the open file lives in it, close first.
                if (_activePath is not null &&
                    _recentFiles.Entries.Any(e =>
                        string.Equals(e.ProjectName, group.ProjectName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(e.Path, _activePath, StringComparison.OrdinalIgnoreCase)))
                {
                    var projectName = group.ProjectName;
                    _afterClose = () => { _recentFiles.RemoveProject(projectName); RefreshProjectTree(); };
                    _ = ExecJsAsync("closeActive()");
                    return;
                }
                _recentFiles.RemoveProject(group.ProjectName);
                break;

            default:
                return;
        }
        RefreshProjectTree();
    }

    private bool IsActiveFile(string path) =>
        _activePath is not null && string.Equals(_activePath, path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The page finished closing the document — reset everything that was describing it.
    /// Each of these is set in OnFileOpened and has no other owner, so leaving any of them
    /// behind is how a closed file goes on looking open: the title still names it, the
    /// breadcrumb still points at its folder, the status bar still shows its language and
    /// modified time.
    /// </summary>
    private void OnFileClosed(string path)
    {
        // With a tab strip, this also fires for files that were never the one on screen.
        // Wiping the title/breadcrumb/status bar for a background tab would describe the
        // window as empty while the active document is still right there.
        if (!IsActiveFile(path))
        {
            RefreshProjectTree();
            return;
        }

        _activePath = null;
        Text = "BcodeViewer";
        _breadcrumb.Controls.Clear();
        _langLabel.Text = "";
        _modifiedLabel.Text = "";
        _posLabel.Text = "Ln 1, Col 1";

        var pending = _afterClose;
        _afterClose = null;
        if (pending is not null) pending();
        else RefreshProjectTree(); // closed from the editor side — the row stays, just unselected
    }

    /// <summary>Backs "Save As" — must run on the UI thread (WinForms dialogs require it)
    /// even though EditorBridge is called from whatever background thread WebView2 uses
    /// for host object calls; Invoke (not BeginInvoke) blocks that calling thread until the
    /// user closes the dialog, which is what lets JS simply await the chosen path.</summary>
    private string? ChooseSaveAsPath(string suggestedPath)
    {
        if (InvokeRequired)
            return (string?)Invoke(new Func<string?>(() => ChooseSaveAsPath(suggestedPath)));

        using var dialog = new SaveFileDialog
        {
            FileName = Path.GetFileName(suggestedPath),
            InitialDirectory = Path.GetDirectoryName(suggestedPath),
            Filter = "Tất cả file (*.*)|*.*"
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>Deletes previous runs' per-process profile folders. Best-effort: a folder
    /// still in use by another currently-running BcodeViewer instance simply fails to
    /// delete and is left alone (no harm — it's a live, legitimate profile), while ones
    /// left behind by a process that already exited get cleaned up so this doesn't grow
    /// unbounded over time.</summary>
    private static void CleanupStaleProfiles(string profileRoot)
    {
        try
        {
            if (!Directory.Exists(profileRoot)) return;
            foreach (var dir in Directory.GetDirectories(profileRoot))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* still locked by a running instance — leave it */ }
            }
        }
        catch { /* profileRoot itself inaccessible — not worth failing startup over */ }
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsForm(_settings, () => _bridge?.DescribeSqlStatus() ?? "");
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // The shared-template folder and the AI/SQL toggles all feed IntelliSense, so a
        // change here has to reach both sides: the bridge re-reads the libraries and drops
        // the cached schema, then the page re-pulls snippets and feature flags.
        _bridge?.ReloadSnippets();
        _bridge?.InvalidateSqlSchema();
        _ = ExecJsAsync("reloadSnippets()");
    }

    private void OpenHintCode()
    {
        using (var dialog = new HintCodeForm(
                   _settings,
                   code => _ = ExecJsAsync($"insertTextAtCursor({JsonSerializer.Serialize(code)})")))
        {
            dialog.ShowDialog(this);
        }

        // A snippet saved in that dialog should be suggestable immediately — without this
        // the library Monaco holds stays the one pulled at page load (see completion.js's
        // note on why it's cached in the page at all).
        _bridge?.ReloadSnippets();
        _ = ExecJsAsync("reloadSnippets()");
    }

    /// <summary>
    /// "New from Template" — whole-file scaffolds, the third kind of template alongside
    /// inline snippets and the shared library (see FileTemplateStore). Kept on the WinForms
    /// side because it needs a real Save dialog: a new file has no path yet, and the page
    /// can't show a native picker.
    /// </summary>
    private void NewFromTemplate()
    {
        var templates = FileTemplateStore.Load(_settings.SharedTemplatePath);
        if (templates.Count == 0)
        {
            MessageBox.Show(this,
                "Chưa có template file nào.\n\n" +
                $"Đặt file mẫu vào:\n  {FileTemplateStore.PersonalFolder}\n" +
                (string.IsNullOrWhiteSpace(_settings.SharedTemplatePath)
                    ? "hoặc cấu hình thư mục dùng chung ở File > Settings."
                    : $"hoặc thư mục dùng chung:\n  {Path.Combine(_settings.SharedTemplatePath, "files")}"),
                "New from Template", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var picker = new TemplatePickerForm(templates);
        if (picker.ShowDialog(this) != DialogResult.OK || picker.Selected is null) return;

        var template = picker.Selected;
        using var save = new SaveFileDialog
        {
            FileName = template.SuggestedFileName,
            Filter = "Tất cả file (*.*)|*.*",
            InitialDirectory = _activePath is null ? null : Path.GetDirectoryName(_activePath),
        };
        if (save.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(save.FileName, template.Render(save.FileName, _projectName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không tạo được file:\n{ex.Message}", "New from Template",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _ = OpenFileInPageAsync(save.FileName);
    }
}
