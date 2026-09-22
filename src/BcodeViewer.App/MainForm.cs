using System.IO.Pipes;
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
    private TreeNode? _hotNode; // row currently under the mouse — shows the copy/close icons, like a VSCode list row
    private readonly Dictionary<TreeNode, (Rectangle Copy, Rectangle Close)> _rowIcons = new();
    private readonly ToolTip _toolTip = new();

    public MainForm(string? initialFile, string projectName)
    {
        _initialFile = initialFile;
        _projectName = string.IsNullOrWhiteSpace(projectName) ? "#Other" : projectName;
        Text = "BcodeViewer";
        Width = 1400;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;

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
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("Settings...", null, (_, _) => OpenSettings()));
        menu.Items.Add(fileMenu);
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

        var leftPanel = new Panel { Dock = DockStyle.Fill };
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

        ThemeManager.Apply(this);
        _breadcrumb.BackColor = AppColors.PanelAlt;
        leftPanel.BackColor = AppColors.Panel;
        _projectsHeader.BackColor = AppColors.PanelAlt;
        _projectsHeader.ForeColor = AppColors.TextMuted;

        Load += MainForm_Load;
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

        // 3. XỬ LÝ NEW WINDOW: Không tự ý Navigate đè lên trang chính nếu là URL rỗng hoặc OAuth background
        _claudeWebView.CoreWebView2.NewWindowRequested += (sender, args) =>
        {
            var uri = args.Uri;
            if (!string.IsNullOrWhiteSpace(uri) && uri.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                // Nếu là link đăng nhập google/accounts thì cho mở trong form hoặc navigate, 
                // còn nếu là link nội bộ claude thì không can thiệp để tránh phá vỡ Single Page App
                if (uri.Contains("accounts.google.com") || uri.Contains("anthropic.com"))
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

        var indexPath = Path.Combine(AppContext.BaseDirectory, "Web", "index.html");
        _webView.CoreWebView2.Navigate(new Uri(indexPath).AbsoluteUri);

        _webView.CoreWebView2.NavigationCompleted += async (_, args) =>
        {
            if (!args.IsSuccess) return;
            if (_initialFile is not null && File.Exists(_initialFile))
                await OpenFileInPageAsync(_initialFile); // triggers editor.js's own NotifyFileOpened
            else
                RefreshProjectTree(); // still show prior history even with nothing to open now

            // Only now is window.bcodeViewer guaranteed to exist — flush anything a second
            // launch handed off (see Program.cs/StartPipeServer) that arrived before the
            // page finished loading, instead of silently dropping it.
            _pageReady = true;
            while (_pendingExternalOpens.Count > 0)
            {
                var (path, project) = _pendingExternalOpens.Dequeue();
                _projectName = project;
                await OpenFileInPageAsync(path);
            }
        };

        StartPipeServer();
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
    }

    private void OnDirtyChanged(string path, bool isDirty)
    {
        Text = isDirty ? $"BcodeViewer — {Path.GetFileName(path)} •" : $"BcodeViewer — {Path.GetFileName(path)}";

        // The tree is the file switcher (no tab strip — see the class doc comment), so an
        // unsaved file needs its own visual flag there, same idea as an editor's tab dot/
        // asterisk: yellow text on the node until the file is saved back to disk.
        if (FindFileNode(path) is { } node)
        {
            node.ForeColor = isDirty ? Color.FromArgb(229, 192, 123) : AppColors.Text;
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
    private void RenderBreadcrumb(string path)
    {
        _breadcrumb.Controls.Clear();
        var isUnc = path.StartsWith(@"\\");
        var parts = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var accum = isUnc ? @"\\" : "";

        for (var i = 0; i < parts.Length; i++)
        {
            accum = accum.EndsWith('\\') ? accum + parts[i] : accum + '\\' + parts[i];
            var isLast = i == parts.Length - 1;

            var link = new LinkLabel { Text = parts[i], AutoSize = true, Margin = new Padding(0, 2, 0, 0), BackColor = Color.Transparent };
            if (isLast)
            {
                link.LinkColor = AppColors.Text;
                link.LinkBehavior = LinkBehavior.NeverUnderline;
                link.Enabled = false; // the file itself — nothing to navigate to
                link.Font = new Font(link.Font, FontStyle.Bold);
            }
            else
            {
                link.LinkColor = Color.FromArgb(78, 165, 240);
                link.ActiveLinkColor = AppColors.AccentHover;
                var target = accum;
                link.Click += (_, _) => _bridge?.OpenFolder(target);
            }
            _breadcrumb.Controls.Add(link);

            if (!isLast)
                _breadcrumb.Controls.Add(new Label { Text = "  ›  ", AutoSize = true, Margin = new Padding(0, 2, 0, 0), ForeColor = AppColors.TextMuted, BackColor = Color.Transparent });
        }
    }

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

    private void RemoveTreeNode(TreeNode node)
    {
        switch (node.Tag)
        {
            case string path:
                _recentFiles.Remove(path);
                break;
            case ProjectGroupTag group:
                _recentFiles.RemoveProject(group.ProjectName);
                break;
            default:
                return;
        }
        RefreshProjectTree();
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
        using var dialog = new SettingsForm(_settings);
        dialog.ShowDialog(this);
    }

    private void OpenHintCode()
    {
        using var dialog = new HintCodeForm(code => _ = ExecJsAsync($"insertTextAtCursor({JsonSerializer.Serialize(code)})"));
        dialog.ShowDialog(this);
    }
}
