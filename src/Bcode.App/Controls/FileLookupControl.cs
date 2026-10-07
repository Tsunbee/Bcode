using System.Diagnostics;
using Bcode.App.Forms;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Left-hand "File Lookup" tree: browses the UNC source path of the current
/// workspace rooted at App_Data (whatever it contains — layout varies by
/// site, e.g. App_Data/{Include,Request,Structure,Templates}), with an
/// extension filter ("Only Show *.ext") and free-text search, matching the
/// FCode File Lookup tab. Selecting a file previews its content on the right
/// (path, last-modified time, breadcrumb) read-only — an "Edit in BcodeViewer"
/// button launches the standalone Monaco/WebView2 editor (with its own AI
/// chat panel) on that file for actual editing, rather than editing in place.
/// </summary>
public class FileLookupControl : UserControl
{
    private readonly TreeView _tree;
    /// <summary>Right-click menu on <see cref="_tree"/> — "Go to File/Folder" and "Copy
    /// File(s) to..." (see GoToFileOrFolder/ShowCopyFileToDialog), matching those two items
    /// from FCode's own (larger) File Lookup context menu.</summary>
    private readonly ContextMenuStrip _fileContextMenu = new();
    // Path/Load, extension filter, "Only Show *.ext", "SearchBox ▾" toggle and the
    // filename search box are now a small WebView2 strip (Web/Shell/filelookupbar.html) —
    // same chrome-vs-content split as everywhere else: this bar is static/low-data, the
    // tree below (can be hundreds of file nodes) stays 100% native WinForms. The plain
    // fields below are this bar's state, since it no longer lives in native controls C#
    // can just read .Text/.Checked off of.
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _barWeb = new();
    private string _pathText = "";
    private string _extensionText = ".f";
    private bool _onlyShowFilteredOn = true;
    private string _searchText = "";
    private readonly Label _statusLabel;
    /// <summary>Chi tiết vấn đề entity của file đang chọn (đỏ) — chỉ hiện khi file đó có lỗi.</summary>
    private readonly TextBox _issueBox;
    private string _summaryText = "";
    private bool _summaryHasIssues;
    private readonly FileLookupService _service;
    private readonly ScriptFileService _scriptFileService;

    // "SearchBox ▾" — FCodeViewer's own expandable content-search panel (see
    // BuildSearchBoxPanel/ToggleSearchBoxPanel/RunContentSearch). Giờ là 1 WebView2 (Web/Shell/searchbox.html) như thanh lọc
    // phía trên; giá trị các ô được trang HTML gửi sang (action "state"/"search") và nhớ ở các trường _sb* dưới đây. Không
    // `readonly`: gán trong BuildSearchBoxPanel (hàm gọi từ constructor), C# không cho readonly kiểu này (CS0191).
    private Microsoft.Web.WebView2.WinForms.WebView2 _searchBoxPanel = null!;
    private bool _searchBoxWebStarted;
    private string _sbFileTypeText = "*.*";
    private string _sbSearchInText = "";
    private string _sbStringText = "";
    private bool _sbMatchCaseOn;
    private bool _sbShowPatternOn;

    private readonly AppSettings _settings;

    // Preview pane (right side) — the path/Edit button + modified/breadcrumb header row is
    // now a small WebView2 strip too (Web/Shell/filelookuppreview.html); _previewEditor
    // (the actual file content, syntax-highlighted) stays native.
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _previewBarWeb = new();
    private readonly MonacoPreviewControl _previewEditor;

    // Bumped on every PreviewFile call; a background read only applies its result if it's
    // still the current one when it finishes — otherwise a slow read for a file the user
    // already clicked past would overwrite whatever they clicked next.
    private int _previewRequestVersion;

    // Same idea for the tree itself: bumped on every Reload/RunContentSearch, and a
    // background build only replaces the tree if it's still the latest request.
    private int _loadVersion;
    private CancellationTokenSource? _contentSearchCts;

    // Set when the tree is showing a wcommand menu item's source (main page + its
    // Controllers/{sysid} folder) rather than a free browse/search of the whole tree —
    // see ShowForMenuItem.
    private bool _menuMode;
    private string _menuLink = "";
    private string _menuSysId = "";

    /// <summary>Current workspace's display name — kept in sync by MainForm (OpenFileLookupTab)
    /// whenever this tab is (re)opened or the active workspace changes. Passed to BcodeViewer
    /// as its args[1] so its recent-files panel groups this file under the right project
    /// instead of falling back to "#Other" (BcodeViewer's own catch-all for a launch with no
    /// project name — see Program.cs there).</summary>
    public string ProjectName { get; set; } = "";

    public event Action<string>? FileActivated; // full path

    public FileLookupControl(FileLookupService service, ScriptFileService scriptFileService, AppSettings settings)
    {
        _service = service;
        _scriptFileService = scriptFileService;
        _settings = settings;
        Dock = DockStyle.Fill;

        _barWeb.Dock = DockStyle.Top;
        _barWeb.Height = 150; // 3 hàng: Path/Load · ext/Only Show/Search · Copy to/Tick

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = ""
        };

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, ShowNodeToolTips = true, CheckBoxes = true };
        _tree.AfterCheck += OnTreeAfterCheck;
        _issueBox = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 96,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            ForeColor = AppColors.Danger,
            Visible = false,
        };
        // "Fcode's lookup bars are smooth — check what makes them not lag." Same fix as
        // WCommandTreeControl's own tree: TreeView doesn't double-buffer itself by default
        // (unlike a DataGridView bound through GridDisplayHelper, which already gets this),
        // so rebuilding/expanding a tree with a few hundred+ file nodes visibly flickered.
        ControlPerf.EnableDoubleBuffering(_tree);
        // Bee's own icon in front of file nodes, a drawn folder glyph in front of
        // directory nodes — was the bee icon for every node regardless of type, which (a)
        // didn't read as a folder at a glance and (b) went missing entirely for a while
        // (see AppIcons.FileTreeBitmap/FolderTreeBitmap — the underlying embedded resource
        // wiring had been dropped by an unrelated git merge). ToTreeNode below picks the
        // key per node based on FileLookupNode.IsDirectory.
        var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        if (AppIcons.FileTreeBitmap is { } beeIcon) images.Images.Add("bee", beeIcon);
        if (AppIcons.FolderTreeBitmap is { } folderIcon) images.Images.Add("folder", folderIcon);
        images.Images.Add("excel", AppIcons.ExcelTreeBitmap);   // .xlsx/.xls — biểu tượng Excel
        images.Images.Add("rpt", AppIcons.ReportTreeBitmap);    // .rpt — biểu tượng Crystal Reports
        if (images.Images.Count > 0) _tree.ImageList = images;
        _tree.AfterSelect += (_, e) =>
        {
            ShowIssuesFor(e.Node?.Tag as FileLookupNode);
            if (e.Node?.Tag is FileLookupNode { IsDirectory: false } node)
                PreviewFile(node.FullPath, e.Node);
        };
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is FileLookupNode { IsDirectory: false } node)
                FileActivated?.Invoke(node.FullPath);
        };

        // Right-click doesn't select a node on its own in a plain TreeView — hit-test and
        // select it first so the context menu below acts on the node actually under the
        // cursor, not whatever was selected before (same fix as WCommandTreeControl's tree).
        _tree.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            var node = _tree.GetNodeAt(e.Location);
            if (node is not null) _tree.SelectedNode = node;
        };
        _fileContextMenu.Opening += (_, e) =>
        {
            if (_tree.SelectedNode?.Tag is not FileLookupNode) { e.Cancel = true; return; }
            _fileContextMenu.Items.Clear();
            _fileContextMenu.Items.Add("Go to File/Folder", null, (_, _) => GoToFileOrFolder());
            var copyItem = _fileContextMenu.Items.Add("Copy File(s) to...", null, (_, _) => ShowCopyFileToDialog());
            copyItem.Enabled = _tree.SelectedNode.Tag is FileLookupNode { IsDirectory: false };
            _fileContextMenu.Items.Add(new ToolStripSeparator());
            _fileContextMenu.Items.Add("Copy path", null, (_, _) => CopyPath());
            _fileContextMenu.Items.Add("Get Hash Source", null, async (_, _) => await GetHashSourceAsync());
            _fileContextMenu.Items.Add("Cấp source (Add Source)...", null, (_, _) => ShowAddSource());
            var cloneItem = _fileContextMenu.Items.Add("Clone files...", null, (_, _) => CloneFiles());
            cloneItem.Enabled = copyItem.Enabled;
            var deleteItem = _fileContextMenu.Items.Add("Delete file", null, (_, _) => DeleteFile());
            deleteItem.Enabled = copyItem.Enabled;
            _fileContextMenu.Items.Add(new ToolStripSeparator());
            _fileContextMenu.Items.Add(new ToolStripMenuItem("Refresh", null, (_, _) => RefreshNewFiles()) { ShortcutKeyDisplayString = "F5" });
        };
        _tree.ContextMenuStrip = _fileContextMenu;
        // Ctrl+F khi đang chọn file ở cây: tìm chữ trong file đang xem trước (focus vẫn ở cây nên khung xem trước chưa nhận được phím).
        _tree.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5 && !e.Control && !e.Shift && !e.Alt) { e.Handled = e.SuppressKeyPress = true; RefreshNewFiles(); return; }
            if (e.Control && e.KeyCode == Keys.F && _previewEditor.CurrentPath is not null)
            {
                e.Handled = e.SuppressKeyPress = true;
                _previewEditor.ShowFind();
            }
        };

        _searchBoxPanel = BuildSearchBoxPanel();

        var leftPanel = new Panel { Dock = DockStyle.Fill };
        leftPanel.Controls.Add(_tree);
        leftPanel.Controls.Add(_issueBox); // trước _statusLabel để nằm ngay phía trên dòng trạng thái
        leftPanel.Controls.Add(_statusLabel);
        // _searchBoxPanel added before _barWeb so it lands directly below the filter bar
        // when visible (Dock=Top controls stack with the last-added ending up outermost).
        leftPanel.Controls.Add(_searchBoxPanel);
        leftPanel.Controls.Add(_barWeb);

        // ---- Right preview pane ----
        _previewBarWeb.Dock = DockStyle.Top;
        _previewBarWeb.Height = 46;

        _previewEditor = new MonacoPreviewControl(); // Monaco như BcodeViewer — xem MonacoPreviewControl
        // F12 on an &Entity; reference opens a separate "peek" popup instead of replacing
        // the current preview — the file being read is usually why the user pressed F12 in
        // the first place, so it should stay on screen, not get swapped out. A SYSTEM entity
        // (or a directly-clicked Include path) opens the target FILE (ShowEntityPopup); a
        // VALUE entity — most "&Name;" references in a controller are this kind, its
        // declaration IS the code rather than a file reference — shows its text instead
        // (ShowEntityValuePeek), since there's no file to open.
        _previewEditor.EntityNavigationRequested += ShowEntityPopup;
        _previewEditor.EntityValuePeekRequested += ShowEntityValuePeek;
        _previewEditor.EntityMultiPeekRequested += ShowEntityMultiPeek;

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_previewEditor);
        rightPanel.Controls.Add(_previewBarWeb);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6
        };
        split.Panel1.Controls.Add(leftPanel);
        split.Panel2.Controls.Add(rightPanel);
        // Panel1 (tree) keeps a fixed pixel width; Panel2 (preview) absorbs the rest.
        split.FixedPanel = FixedPanel.Panel1;
        // 0 is always a valid MinSize regardless of the container's current Width, unlike
        // any positive value (which throws immediately if it doesn't fit — the earlier
        // version's 150/200 minimums crashed for exactly that reason while the control was
        // still at its tiny pre-layout size). SplitterDistance is clamped by hand below
        // instead, which is what actually needs to adapt to the real width.
        split.Panel1MinSize = 0;
        split.Panel2MinSize = 0;

        // Was 320 — the search row (extension combo + "Only Show *.ext" checkbox +
        // Search box) reads cramped right at that width. Now that row1's Path box is
        // Fill-docked (see above) it no longer overflows/clips at 320, but a bit more
        // room still makes both rows comfortable to read.
        const int desiredTreeWidth = 360;
        // How much width Panel2 (preview) keeps no matter how narrow the tab gets — the
        // previous version instead gave up entirely below a combined-minimum threshold and
        // never set SplitterDistance at all, which is what left Panel2 at 0 width forever
        // (a completely blank File Lookup): "vẫn bị lỗi ... không thấy khung xem file bên
        // phải đâu cả". This always assigns something, so Panel2 can shrink but never
        // vanishes as long as there's more than a sliver of width to work with.
        const int minPreviewWidth = 120;
        void ApplySplitterDistance()
        {
            if (split.Width <= 0) return;
            var maxTreeWidth = Math.Max(0, split.Width - split.SplitterWidth - minPreviewWidth);
            var clamped = Math.Max(0, Math.Min(desiredTreeWidth, maxTreeWidth));
            if (split.SplitterDistance != clamped)
                split.SplitterDistance = clamped;
        }
        split.SizeChanged += (_, _) => ApplySplitterDistance();

        Controls.Add(split);

        ShowNoSelection();

        Bcode.App.UI.ThemeManager.ThemeChanged += PushThemeToBars;
        Bcode.App.UI.ThemeManager.ThemeChanged += RecolorTreeOnTheme;
        Disposed += (_, _) =>
        {
            Bcode.App.UI.ThemeManager.ThemeChanged -= PushThemeToBars;
            Bcode.App.UI.ThemeManager.ThemeChanged -= RecolorTreeOnTheme;
            _contentSearchCts?.Cancel();
        };
        _ = InitBarsAsync();

        async Task InitBarsAsync()
        {
            try
            {
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_barWeb);
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_previewBarWeb);

                _barWeb.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var root = doc.RootElement;
                    switch (root.GetProperty("action").GetString())
                    {
                        case "load":
                            _menuMode = false;
                            _pathText = root.GetProperty("path").GetString() ?? "";
                            // An explicit Load is the user's "refresh" — rescan the disk instead
                            // of reusing the cached file list (which may predate a deploy).
                            _service.InvalidateCache();
                            _service.ResetParseCache(_pathText);
                            Reload();
                            break;
                        case "ext":
                            _extensionText = root.GetProperty("value").GetString() ?? ".f";
                            Reload();
                            break;
                        case "toggle-only-show":
                            _onlyShowFilteredOn = !_onlyShowFilteredOn;
                            Reload();
                            break;
                        case "toggle-searchbox":
                            ToggleSearchBoxPanel();
                            break;
                        // Không tạo WebView2 mới ngay trong callback của WebView2 khác ("Class not registered") → BeginInvoke.
                        case "copy-to":
                            BeginInvoke(new Action(ShowCopyMultiDialog));
                            break;
                        case "check-all":
                            SetAllChecked(root.TryGetProperty("value", out var cv) && cv.ValueKind == System.Text.Json.JsonValueKind.True);
                            break;
                        case "search":
                            _menuMode = false;
                            _searchText = root.GetProperty("value").GetString() ?? "";
                            Reload();
                            break;
                    }
                };

                _previewBarWeb.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    if (doc.RootElement.GetProperty("action").GetString() == "edit") OpenInViewer();
                };

                _barWeb.CoreWebView2.NavigationCompleted += (_, _) => PushThemeToBars();
                _previewBarWeb.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    PushThemeToBars();
                    ShowNoSelection();
                };

                _barWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("filelookupbar.html"));
                _previewBarWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("filelookuppreview.html"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Không khởi tạo được thanh công cụ (dùng WebView2).\nChi tiết lỗi: " + ex.Message,
                    "Bcode — WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void PushThemeToBars()
        {
            var isDark = Bcode.App.UI.AppColors.IsDark ? "true" : "false";
            if (_barWeb.CoreWebView2 is not null)
                _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
            if (_previewBarWeb.CoreWebView2 is not null)
                _ = _previewBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
            PushSearchBoxTheme();
        }
    }

    public void SetRootPath(string path)
    {
        _menuMode = false;
        _pathText = path;
        PushPathToBar();
        Reload();
    }

    private void PushPathToBar()
    {
        if (_barWeb.CoreWebView2 is null) return;
        var arg = System.Text.Json.JsonSerializer.Serialize(_pathText);
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setPath && window.setPath({arg})");
    }

    private void PushOnlyShowState(string label)
    {
        if (_barWeb.CoreWebView2 is null) return;
        var labelArg = System.Text.Json.JsonSerializer.Serialize(label);
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setOnlyShowLabel && window.setOnlyShowLabel({labelArg})");
        var checkedArg = _onlyShowFilteredOn ? "true" : "false";
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setOnlyShowChecked && window.setOnlyShowChecked({checkedArg})");
    }

    private void PushPreview(string path, string modified, string breadcrumb, bool editEnabled)
    {
        if (_previewBarWeb.CoreWebView2 is null) return;
        var pathArg = System.Text.Json.JsonSerializer.Serialize(path);
        var modifiedArg = System.Text.Json.JsonSerializer.Serialize(modified);
        var breadcrumbArg = System.Text.Json.JsonSerializer.Serialize(breadcrumb);
        var editArg = editEnabled ? "true" : "false";
        _ = _previewBarWeb.CoreWebView2.ExecuteScriptAsync(
            $"window.setPreview && window.setPreview({pathArg},{modifiedArg},{breadcrumbArg},{editArg})");
    }

    /// <summary>
    /// Used when a WCommand menu node is activated: shows exactly the source for that
    /// menu item, the way the menu itself is wired to source — <paramref name="link"/>
    /// is the page file under "Main", <paramref name="sysId"/> is the controller folder
    /// under App_Data\Controllers holding its source files.
    /// </summary>
    public void ShowForMenuItem(string sourceRootPath, string link, string sysId)
    {
        // Reset to "show every file type for this menu" — the shared checkbox may still be
        // checked from a previous free-browse extension filter. That used to be harmless in
        // menu mode (onlyF was always false there), but now genuinely restricts results to
        // .f only, and would otherwise make clicking a menu item show fewer files than
        // before for no reason visible to the user.
        _onlyShowFilteredOn = false;
        _menuMode = true;
        _menuLink = link;
        _menuSysId = sysId;
        _pathText = sourceRootPath;
        PushPathToBar();
        Reload();
    }

    public void Reload() => _ = ReloadAsync();

    /// <summary>Refresh (chuột phải / F5): quét lại danh sách file để thấy file MỚI, nhưng KHÔNG xoá cache phân tích — file cũ vẫn dùng
    /// kết quả đã cache (cache tự kiểm lại mtime/size), chỉ file mới/đổi mới phải đọc nên vẫn nhanh. Khác nút Load (xoá hết cache, chậm).</summary>
    private void RefreshNewFiles()
    {
        if (string.IsNullOrWhiteSpace(_pathText)) return;
        _service.InvalidateCache();
        Reload();
    }

    /// <summary>Builds the tree off the UI thread (the first build of a folder scans it over
    /// UNC; later ones just filter FileLookupService's cached file list), so typing in the
    /// search box or switching extensions never freezes the window. The old tree stays on
    /// screen until the new one is ready, and a result that a newer Reload/RunContentSearch
    /// has already superseded (see <see cref="_loadVersion"/>) is simply dropped.</summary>
    private async Task ReloadAsync()
    {
        var version = ++_loadVersion;
        _contentSearchCts?.Cancel();
        if (string.IsNullOrWhiteSpace(_pathText))
        {
            _tree.Nodes.Clear();
            ShowNoSelection();
            return;
        }

        // Menu mode's own checkbox is always fixed to ".f" in real FCodeViewer ("Only Show
        // *.f"/"Show *.f"), independent of whichever extension the free-browse dropdown
        // happens to have selected — that dropdown only matters in the `else` branch below.
        PushOnlyShowState(_menuMode ? "Only Show *.f" : $"Only Show {_extensionText}");

        // Snapshot the bar state — the background build must not read fields the UI thread
        // may change (next keystroke) while it runs.
        var menuMode = _menuMode;
        var path = _pathText.Trim();
        var menuLink = _menuLink;
        var menuSysId = _menuSysId;
        var extension = _extensionText;
        var search = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText.Trim();
        var onlyShow = _onlyShowFilteredOn;

        _statusLabel.Text = "Đang tải...";
        var sw = Stopwatch.StartNew();
        TreeNode rootNode;
        int fileCount;
        try
        {
            // TreeNodes are built here too — they aren't attached to _tree yet, so creating
            // them off the UI thread is safe and keeps a few thousand node allocations off it.
            (rootNode, fileCount) = await Task.Run(() =>
            {
                var root = menuMode
                    ? _service.BuildTreeForMenuItem(path, menuLink, menuSysId, onlyF: onlyShow)
                    : _service.BuildTree(path, extension, search, onlyShow);
                return (ToTreeNode(root), CountFiles(root));
            });
        }
        catch (Exception ex)
        {
            if (version == _loadVersion && !IsDisposed) _statusLabel.Text = "Lỗi khi tải cây thư mục: " + ex.Message;
            return;
        }
        sw.Stop();
        if (version != _loadVersion || IsDisposed) return;

        ShowNoSelection();
        // BeginUpdate/EndUpdate around the population + ExpandAll below — was missing here
        // (WCommandTreeControl's own tree already does this for its load). Without it, every
        // node add/expand repaints individually instead of once at the end, which is what
        // made loading/searching a folder with a lot of matches visibly flicker/lag.
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();
            _tree.Nodes.Add(rootNode);
            if (menuMode || search is not null)
                _tree.ExpandAll();
            else
                rootNode.Expand();
        }
        finally
        {
            _tree.EndUpdate();
        }
        // ExpandAll để TreeView cuộn xuống tận node cuối (thanh cuộn nằm dưới cùng) → đưa về đầu cây.
        _tree.TopNode = rootNode;
        PushCheckedCount(); // cây mới dựng: chưa tick file nào

        var problemFiles = CountFilesWithIssues(rootNode);
        _summaryHasIssues = problemFiles > 0;
        _summaryText = $"Kết quả {fileCount} file(s) — {sw.ElapsedMilliseconds} ms"
            + (problemFiles > 0 ? $" — ⚠ {problemFiles} file lỗi entity/include (tô đỏ, chọn file để xem chi tiết)" : "")
            + (menuMode && _service.LastBuildNote is { } note ? $" — {note}" : "");
        SetStatus(_summaryText, _summaryHasIssues);
    }

    private void SetStatus(string text, bool isIssue)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = isIssue ? AppColors.Danger : SystemColors.GrayText;
    }

    private static int CountFilesWithIssues(TreeNode node)
    {
        var n = node.Tag is FileLookupNode { IsDirectory: false, Issues.Count: > 0 } ? 1 : 0;
        foreach (TreeNode child in node.Nodes) n += CountFilesWithIssues(child);
        return n;
    }

    /// <summary>Hiện danh sách entity/include thiếu của file vừa chọn; thư mục hoặc file ổn thì ẩn khung.</summary>
    private void ShowIssuesFor(FileLookupNode? node)
    {
        if (node is { IsDirectory: false, Issues.Count: > 0 })
        {
            _issueBox.Text = string.Join(Environment.NewLine, node.Issues.Select(i => "✖ " + i));
            _issueBox.Visible = true;
        }
        else
        {
            _issueBox.Visible = false;
        }
    }

    /// <summary>FCodeViewer's own "Search Box" panel — File Type / Search in (+ browse) /
    /// String search / Match Case / Show Pattern / Search — a real content search across
    /// files, as opposed to the filter bar's plain filename-only search box. WebView2 (searchbox.html), khởi tạo khi mở lần đầu
    /// (WebView2 trong control đang ẩn không tạo được cửa sổ).</summary>
    private Microsoft.Web.WebView2.WinForms.WebView2 BuildSearchBoxPanel() =>
        new() { Dock = DockStyle.Top, Height = 158, Visible = false };

    private async void EnsureSearchBoxWeb()
    {
        if (_searchBoxWebStarted) return;
        _searchBoxWebStarted = true;
        try
        {
            await Bcode.App.UI.WebViewEnvironment.InitAsync(_searchBoxPanel);
            _searchBoxPanel.CoreWebView2.WebMessageReceived += OnSearchBoxMessage;
            _searchBoxPanel.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                PushSearchBoxTheme();
                PushSearchBoxState();
            };
            _searchBoxPanel.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("searchbox.html"));
        }
        catch (Exception ex)
        {
            _searchBoxWebStarted = false;
            MessageBox.Show(this, "Không khởi tạo được Search Box (dùng WebView2).\nChi tiết lỗi: " + ex.Message,
                "Bcode — WebView2", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnSearchBoxMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
        var root = doc.RootElement;
        if (root.TryGetProperty("fileType", out var fileType)) _sbFileTypeText = fileType.GetString() ?? "";
        if (root.TryGetProperty("searchIn", out var searchIn)) _sbSearchInText = searchIn.GetString() ?? "";
        if (root.TryGetProperty("text", out var text)) _sbStringText = text.GetString() ?? "";
        if (root.TryGetProperty("matchCase", out var matchCase)) _sbMatchCaseOn = matchCase.GetBoolean();
        if (root.TryGetProperty("showPattern", out var showPattern)) _sbShowPatternOn = showPattern.GetBoolean();

        switch (root.GetProperty("action").GetString())
        {
            case "search": RunContentSearch(); break;
            case "cancel": CancelContentSearch(); break;
            case "browse": BrowseSearchInFolder(); break;
        }
    }

    private void PushSearchBoxTheme()
    {
        if (_searchBoxPanel.CoreWebView2 is null) return;
        var isDark = Bcode.App.UI.AppColors.IsDark ? "true" : "false";
        _ = _searchBoxPanel.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
    }

    /// <summary>Đẩy lại giá trị đang nhớ vào trang (sau khi trang nạp xong) — chỉ ô Search in và trạng thái nút Search/Cancel.</summary>
    private void PushSearchBoxState()
    {
        if (_searchBoxPanel.CoreWebView2 is null) return;
        if (!string.IsNullOrWhiteSpace(_sbSearchInText))
            _ = _searchBoxPanel.CoreWebView2.ExecuteScriptAsync(
                $"window.setSearchIn && window.setSearchIn({System.Text.Json.JsonSerializer.Serialize(_sbSearchInText)})");
        _ = _searchBoxPanel.CoreWebView2.ExecuteScriptAsync(
            $"window.setRunning && window.setRunning({(_contentSearchRunning ? "true" : "false")})");
    }

    private void ToggleSearchBoxPanel()
    {
        _searchBoxPanel.Visible = !_searchBoxPanel.Visible;
        if (_barWeb.CoreWebView2 is not null)
        {
            var arg = System.Text.Json.JsonSerializer.Serialize(_searchBoxPanel.Visible ? "SearchBox ▴" : "SearchBox ▾");
            _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setSearchBoxToggleText && window.setSearchBoxToggleText({arg})");
        }
        if (!_searchBoxPanel.Visible) return;

        if (string.IsNullOrWhiteSpace(_sbSearchInText)) _sbSearchInText = _pathText.Trim();
        EnsureSearchBoxWeb();
        if (_searchBoxPanel.CoreWebView2 is not null)
        {
            PushSearchBoxState();
            _ = _searchBoxPanel.CoreWebView2.ExecuteScriptAsync("window.focusText && window.focusText()");
        }
    }

    private void BrowseSearchInFolder()
    {
        using var dialog = new FolderBrowserDialog();
        if (Directory.Exists(_sbSearchInText)) dialog.SelectedPath = _sbSearchInText;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _sbSearchInText = dialog.SelectedPath;
        PushSearchBoxState();
    }

    private bool _contentSearchRunning;

    /// <summary>Nút Search (trong trang searchbox.html) đổi thành "Cancel" trong lúc đang tìm — bấm lại hoặc Esc để dừng, không phải đợi quét xong.</summary>
    private void SetSearchRunning(bool running)
    {
        _contentSearchRunning = running;
        if (_searchBoxPanel.CoreWebView2 is not null)
            _ = _searchBoxPanel.CoreWebView2.ExecuteScriptAsync($"window.setRunning && window.setRunning({(running ? "true" : "false")})");
    }

    private void CancelContentSearch() => _contentSearchCts?.Cancel();

    /// <summary>Runs the real content search and replaces the tree with its results — a
    /// distinct view from the menu/browse tree above (leaving <see cref="_menuMode"/> so the
    /// Only Show/extension controls don't reinterpret these results as a menu's file set).</summary>
    private async void RunContentSearch()
    {
        if (string.IsNullOrWhiteSpace(_sbSearchInText) || string.IsNullOrWhiteSpace(_sbStringText))
        {
            _statusLabel.Text = "Nhập \"Search in\" và \"String search\" trước khi tìm.";
            return;
        }

        _menuMode = false;
        var version = ++_loadVersion;
        // A content search reads every file — unlike a tree build it's worth actually
        // stopping the previous one rather than just ignoring its result.
        _contentSearchCts?.Cancel();
        var cts = _contentSearchCts = new CancellationTokenSource();

        var searchIn = _sbSearchInText.Trim();
        var fileType = string.IsNullOrWhiteSpace(_sbFileTypeText) ? "*.*" : _sbFileTypeText.Trim();
        var searchText = _sbStringText;
        var matchCase = _sbMatchCaseOn;
        var showPattern = _sbShowPatternOn;

        _tree.Nodes.Clear();
        ShowNoSelection();
        _statusLabel.Text = $"Đang tìm \"{searchText}\"...";
        SetSearchRunning(true);

        // Hiện file khớp dần ngay khi tìm thấy (từ luồng nền → gom vào hàng đợi, 1 lần BeginInvoke xả cả cụm) thay vì đợi quét hết;
        // khi xong, cây cuối cùng (đã sắp xếp) thay thế cây tạm này.
        var pending = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var flushScheduled = 0;
        var foundSoFar = 0;
        var liveRoot = new TreeNode("Search results")
        {
            ImageKey = "folder", SelectedImageKey = "folder",
            Tag = new FileLookupNode { Name = "Search results", FullPath = searchIn, IsDirectory = true },
        };
        _tree.Nodes.Add(liveRoot);

        void Flush()
        {
            Interlocked.Exchange(ref flushScheduled, 0);
            if (version != _loadVersion || IsDisposed) return;
            _tree.BeginUpdate();
            try
            {
                while (pending.TryDequeue(out var path))
                {
                    liveRoot.Nodes.Add(ToTreeNode(new FileLookupNode { Name = Path.GetFileName(path), FullPath = path }));
                    foundSoFar++;
                }
                liveRoot.Expand();
            }
            finally { _tree.EndUpdate(); }
            if (!cts.IsCancellationRequested) _statusLabel.Text = $"Đang tìm \"{searchText}\"... đã thấy {foundSoFar} file";
        }

        void OnMatch(string path)
        {
            if (cts.IsCancellationRequested) return; // đã bấm Cancel: kết quả đến muộn từ các luồng đang đọc dở thì bỏ
            pending.Enqueue(path);
            if (Interlocked.Exchange(ref flushScheduled, 1) != 0 || IsDisposed) return;
            try { BeginInvoke(Flush); }
            catch (InvalidOperationException) { /* control đang đóng */ }
        }

        var sw = Stopwatch.StartNew();
        TreeNode rootNode;
        int fileCount;
        try
        {
            var work = Task.Run(() =>
            {
                var root = _service.SearchFileContents(searchIn, fileType, searchText, matchCase, showPattern, cts.Token, OnMatch);
                return (ToTreeNode(root), CountFiles(root));
            });
            // Cancel phải trả giao diện NGAY, không đợi các luồng đang kẹt đọc dở file qua mạng: chờ "xong việc" hoặc "bị huỷ", cái nào tới trước.
            var cancelled = new TaskCompletionSource();
            using var cancelRegistration = cts.Token.Register(() => cancelled.TrySetResult());
            if (await Task.WhenAny(work, cancelled.Task) != work)
            {
                _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // luồng nền kết thúc muộn, nuốt lỗi
                throw new OperationCanceledException(cts.Token);
            }
            (rootNode, fileCount) = await work;
        }
        catch (OperationCanceledException)
        {
            // Huỷ do bấm Cancel (cts vẫn là cts hiện hành, version không đổi) → giữ kết quả đã thấy và báo rõ; bị search/reload mới thay thì im lặng.
            if (ReferenceEquals(_contentSearchCts, cts)) SetSearchRunning(false);
            if (version == _loadVersion && !IsDisposed)
            {
                Flush();
                _statusLabel.Text = $"Đã huỷ tìm \"{searchText}\" sau {sw.ElapsedMilliseconds} ms — hiện {foundSoFar} file đã thấy (chưa quét hết)";
            }
            return;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_contentSearchCts, cts)) SetSearchRunning(false);
            if (version == _loadVersion && !IsDisposed) _statusLabel.Text = "Lỗi khi tìm: " + ex.Message;
            return;
        }
        sw.Stop();
        if (ReferenceEquals(_contentSearchCts, cts)) SetSearchRunning(false);
        if (version != _loadVersion || IsDisposed) return;

        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear(); // bỏ cây tạm đang hiện dần, thay bằng kết quả cuối đã sắp xếp
            _tree.Nodes.Add(rootNode);
            _tree.ExpandAll();
        }
        finally
        {
            _tree.EndUpdate();
        }
        _tree.TopNode = rootNode;
        PushCheckedCount(); // cây mới dựng: chưa tick file nào
        _statusLabel.Text = $"Kết quả {fileCount} file(s) chứa \"{searchText}\" — {sw.ElapsedMilliseconds} ms";
    }

    private static int CountFiles(FileLookupNode node) =>
        (node.IsDirectory ? 0 : 1) + node.Children.Sum(CountFiles);

    /// <summary><paramref name="treeNode"/> is null when navigating here via F12 from
    /// inside the preview (the target file may not even be part of the current tree) —
    /// the breadcrumb then just shows the containing folder instead of the full tree path.
    ///
    /// The actual file read happens off the UI thread: <c>path</c> is usually a UNC network
    /// path, and reading it synchronously on the UI thread (the previous version) is what
    /// made clicking through files feel choppy — every click blocked the whole window for
    /// however long that read took. <see cref="_previewRequestVersion"/> makes a slow read
    /// for a file the user already clicked past a no-op instead of clobbering whatever they
    /// clicked next.</summary>
    private void PreviewFile(string path, TreeNode? treeNode)
    {
        var breadcrumb = treeNode is not null
            ? BuildBreadcrumb(treeNode)
            : Path.GetFileName(Path.GetDirectoryName(path) ?? "");

        // *.rpt/*.xlsx không phải text — đọc bằng ScriptFileService.ReadFile bên dưới chỉ ra
        // toàn ký tự rác. Hiện gợi ý nhấn đúp/Edit để mở bằng ứng dụng hỗ trợ thật (xem
        // NativeAppLauncher) thay vì cố hiển thị nội dung binary trong khung xem này.
        if (NativeAppLauncher.IsNativeAppFile(path))
        {
            PushPreview(path, "Last Modified: " + File.GetLastWriteTime(path).ToString("dd/MM/yyyy HH:mm:ss"),
                breadcrumb, editEnabled: true);
            _previewEditor.LoadContent(null,
                $"File \"{Path.GetFileName(path)}\" không phải file text — nhấn đúp hoặc bấm nút Edit để mở bằng ứng dụng hỗ trợ (Crystal Reports/Excel...).");
            return;
        }

        PushPreview(path, "Đang tải...", breadcrumb, editEnabled: false);
        _previewEditor.LoadContent(path, "");

        var version = ++_previewRequestVersion;
        Task.Run(() =>
        {
            string? content = null;
            DateTime modified = default;
            Exception? error = null;
            try
            {
                content = _scriptFileService.ReadFile(path);
                modified = File.GetLastWriteTime(path);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (IsDisposed) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (version != _previewRequestVersion) return; // superseded by a later click

                    if (error is null)
                    {
                        PushPreview(path, "Last Modified: " + modified.ToString("dd/MM/yyyy HH:mm:ss"), breadcrumb, editEnabled: true);
                        _previewEditor.LoadContent(path, content!);
                    }
                    else
                    {
                        PushPreview(path, "", breadcrumb, editEnabled: false);
                        _previewEditor.LoadContent(null, $"Không đọc được file:\r\n{error.Message}");
                    }
                    _previewEditor.MarkSaved();
                });
            }
            catch (ObjectDisposedException)
            {
                // control was closed while the read was in flight — nothing to update
            }
        });
    }

    /// <summary>F12 "peek" for a SYSTEM entity (or a directly-clicked Include path): shows the
    /// target file in its own floating window instead of swapping it into the main preview —
    /// the file the user was just reading is exactly why they pressed F12, so it should stay
    /// put. Non-modal (owned by the main window so it doesn't get lost behind it) and
    /// re-entrant: F12 works inside the popup too, for both a further SYSTEM file
    /// (another ShowEntityPopup, opening on top) and a VALUE entity (ShowEntityValuePeek).
    ///
    /// Resolution itself (which file/entity F12 lands on) is entirely ScriptEditorControl's
    /// job now — it walks the SYSTEM-include chain fresh from disk on every press, so this
    /// popup doesn't need to be handed any inherited state; it just shows whatever path it's
    /// given.</summary>
    private void ShowEntityPopup(string path)
    {
        var popup = new Bcode.App.UI.DpiForm
        {
            Text = Path.GetFileName(path),
            Width = 900,
            Height = 650,
            StartPosition = FormStartPosition.CenterParent,
            ShowIcon = false
        };

        var editor = new ScriptEditorControl { ShowPathBar = true, ReadOnly = true };
        editor.EntityNavigationRequested += ShowEntityPopup;
        editor.EntityValuePeekRequested += ShowEntityValuePeek;
        editor.EntityMultiPeekRequested += ShowEntityMultiPeek;
        editor.LoadContent(path, "Đang tải...");
        popup.Controls.Add(editor);
        // New control tree created outside the normal tab-open path (AddDocumentTab already
        // themes new tabs) — ThemeManager.Apply must be called explicitly here or this popup
        // keeps WinForms' default white/black colors while the syntax highlighter still
        // paints its text in the app's (typically dark) theme colors, reading as broken.
        ThemeManager.Apply(popup);
        popup.Show(FindForm());

        Task.Run(() =>
        {
            string? content = null;
            Exception? error = null;
            try
            {
                content = _scriptFileService.ReadFile(path);
            }
            catch (Exception ex)
            {
                error = ex;
            }

            if (popup.IsDisposed) return;
            try
            {
                popup.BeginInvoke(() =>
                {
                    if (popup.IsDisposed) return;
                    editor.LoadContent(path, error is null ? content! : $"Không đọc được file:\r\n{error.Message}");
                    editor.ReadOnly = true;
                    editor.MarkSaved();
                });
            }
            catch (ObjectDisposedException)
            {
                // popup was closed while the read was in flight — nothing to update
            }
        });
    }

    /// <summary>F12 "peek" for a VALUE entity — most "&amp;Name;" references in a FastBusiness
    /// controller are this kind: the declaration itself IS the code (a whole SQL routine or
    /// JS block), not a reference to a file. There's nothing to open, so this shows the
    /// declared text directly in the same kind of floating window ShowEntityPopup uses for
    /// SYSTEM entities — read-only, themed, non-modal — except loaded from
    /// <paramref name="value"/> straight away (no async file read needed) and with
    /// <paramref name="declaringPath"/> passed as the resolve base so F12 pressed again inside
    /// this peeked text can keep resolving whatever entities IT references, walking onward
    /// from the file that declared this one.</summary>
    private void ShowEntityValuePeek(string name, string value, string declaringPath)
    {
        var popup = new Bcode.App.UI.DpiForm
        {
            Text = $"&{name}; — {Path.GetFileName(declaringPath)}",
            Width = 900,
            Height = 650,
            StartPosition = FormStartPosition.CenterParent,
            ShowIcon = false
        };

        var editor = new ScriptEditorControl { ShowPathBar = true, ReadOnly = true };
        editor.EntityNavigationRequested += ShowEntityPopup;
        editor.EntityValuePeekRequested += ShowEntityValuePeek;
        editor.EntityMultiPeekRequested += ShowEntityMultiPeek;
        editor.LoadContent(null, value, declaringPath);
        editor.ReadOnly = true;
        popup.Controls.Add(editor);
        ThemeManager.Apply(popup);
        popup.Show(FindForm());
    }

    /// <summary>F12 khi bôi đen nhiều entity: cửa sổ xem trước nội dung từng entity (1 trang hoặc từng trang) — xem EntityMultiPeekForm.</summary>
    private void ShowEntityMultiPeek(List<EntityPreviewItem> items)
    {
        // BeginInvoke: F12 đến từ callback của trang WebView2 (khung xem trước) — tạo cửa sổ có WebView2 mới NGAY trong callback đó là lỗi
        // "Class not registered"/E_ABORT, nên mở sau khi callback trả về.
        BeginInvoke(new Action(() =>
        {
            var popup = new Bcode.App.Forms.EntityMultiPeekForm(items, _settings, ShowEntityPopup, ShowEntityValuePeek, ShowEntityMultiPeek);
            popup.Show(FindForm());
        }));
    }

    private void ShowNoSelection()
    {
        PushPreview("(chưa chọn file nào)", "", "", editEnabled: false);
        _previewEditor.Clear();
    }

    /// <summary>Launches BcodeViewer (a standalone Monaco/WebView2 editor with an AI chat
    /// panel) on the currently previewed file, prompting once to locate BcodeViewer.exe if
    /// it isn't configured yet (and remembering the choice in AppSettings, same as the
    /// existing VSAppPath/SqlSmsPath external-tool settings).</summary>
    private void OpenInViewer()
    {
        if (_previewEditor.CurrentPath is not { } path) return;

        // *.rpt (Crystal Reports), *.xlsx (Excel) — mở bằng đúng ứng dụng hỗ trợ thay vì
        // BcodeViewer, xem NativeAppLauncher.
        if (NativeAppLauncher.TryOpenWithNativeApp(this, path)) return;

        if (string.IsNullOrWhiteSpace(_settings.ViewerExePath) || !File.Exists(_settings.ViewerExePath))
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Chọn BcodeViewer.exe",
                Filter = "BcodeViewer (BcodeViewer.exe)|BcodeViewer.exe|Tất cả file (*.exe)|*.exe"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _settings.ViewerExePath = dialog.FileName;
            _settings.Save();
        }

        try
        {
            // args[1] (project name) is what lets BcodeViewer's recent-files panel group this
            // file under the current workspace instead of its own "#Other" catch-all group.
            var arguments = string.IsNullOrWhiteSpace(ProjectName)
                ? $"\"{path}\""
                : $"\"{path}\" \"{ProjectName}\"";
            Process.Start(new ProcessStartInfo(_settings.ViewerExePath, arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không mở được BcodeViewer:\n{ex.Message}", "Bcode — File Lookup", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>File Lookup context menu — "Go to File/Folder": opens Windows Explorer with
    /// the selected file highlighted (or the folder itself, for a directory node) — quick
    /// access instead of copying the path and pasting it into Explorer by hand.</summary>
    private void GoToFileOrFolder()
    {
        if (_tree.SelectedNode?.Tag is not FileLookupNode node) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", node.IsDirectory
                ? $"\"{node.FullPath}\""
                : $"/select,\"{node.FullPath}\"")
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không mở được Explorer:\n{ex.Message}", "Bcode — File Lookup",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>"Copy path": đường dẫn đầy đủ của file/thư mục đang chọn vào clipboard.</summary>
    private void CopyPath()
    {
        if (_tree.SelectedNode?.Tag is not FileLookupNode node) return;
        try { Clipboard.SetText(node.FullPath); _statusLabel.Text = "Đã copy path: " + node.FullPath; }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bcode — File Lookup", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    /// <summary>"Get Hash Source": JSON [{fullpath, subpath, name, ext, size, date, hash}] (SHA-256 hoa + giờ ghi file cuối) để so
    /// hash/ngày sửa bản cũ với mới. Ở chế độ menu = TOÀN BỘ file đang hiện trên cây (cả chuỗi controller của menu); chọn 1 thư mục
    /// (chế độ duyệt tự do) = các file dưới thư mục đó; chọn 1 file ngoài chế độ menu = riêng file đó.</summary>
    private async Task GetHashSourceAsync()
    {
        if (_tree.SelectedNode?.Tag is not FileLookupNode selected) return;
        var scope = _menuMode && _tree.Nodes.Count > 0 && _tree.Nodes[0].Tag is FileLookupNode rootNode ? rootNode : selected;

        var paths = new List<string>();
        void Collect(FileLookupNode n)
        {
            if (!n.IsDirectory) paths.Add(n.FullPath);
            foreach (var c in n.Children) Collect(c);
        }
        Collect(scope);

        _statusLabel.Text = $"Đang tính hash {paths.Count} file...";
        string json;
        try
        {
            json = await Task.Run(() =>
            {
                var rows = new List<object>();
                foreach (var f in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    var info = new FileInfo(f);
                    if (!info.Exists) continue;
                    using var fs = info.OpenRead();
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));

                    // subpath tính từ gốc site (phần đứng trước App_Data hoặc Main).
                    var dir = Path.GetDirectoryName(f) ?? "";
                    var parts = dir.Split('\\', '/');
                    var cut = Array.FindIndex(parts, p => p.Equals("App_Data", StringComparison.OrdinalIgnoreCase)
                                                          || p.Equals("Main", StringComparison.OrdinalIgnoreCase));
                    var sub = cut >= 0 ? string.Join("\\", parts.Skip(cut)) : "";

                    rows.Add(new
                    {
                        fullpath = f,
                        subpath = sub,
                        name = info.Name,
                        ext = info.Extension.ToLowerInvariant(),
                        size = info.Length,
                        date = info.LastWriteTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"),
                        hash,
                    });
                }
                return System.Text.Json.JsonSerializer.Serialize(rows, new System.Text.Json.JsonSerializerOptions
                {
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
            });
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "";
            MessageBox.Show(this, $"Không tính được hash:\n{ex.Message}", "Bcode — Get Hash Source", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        _statusLabel.Text = $"Đã tính hash {paths.Count} file.";

        using var form = new Bcode.App.UI.DpiForm
        {
            Text = $"Get Hash Source — {paths.Count} file", Width = 900, Height = 600, StartPosition = FormStartPosition.CenterParent,
        };
        var box = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Text = json };
        var copy = new Button { Text = "Copy", Dock = DockStyle.Bottom, Height = 32 };
        copy.Click += (_, _) => { Clipboard.SetText(json); copy.Text = "Đã copy ✓"; };
        form.Controls.Add(box);
        form.Controls.Add(copy);
        box.SelectionStart = 0;
        form.ShowDialog(this);
    }

    /// <summary>"Cấp source": mẫu tên = tên gốc của file đang chọn + "*" (vd SVTran.xml → "SVTran*"); đích = gốc site đang duyệt
    /// (phần đứng trước App_Data / Main của đường dẫn).</summary>
    private void ShowAddSource()
    {
        if (_tree.SelectedNode?.Tag is not FileLookupNode node) return;
        var parts = (node.FullPath).Split('\\', '/');
        var cut = Array.FindIndex(parts, p => p.Equals("App_Data", StringComparison.OrdinalIgnoreCase)
                                              || p.Equals("Main", StringComparison.OrdinalIgnoreCase));
        var dest = cut > 0 ? string.Join("\\", parts.Take(cut)) : _pathText.Trim();
        if (node.FullPath.StartsWith(@"\\") && cut > 0) dest = @"\\" + dest.TrimStart('\\');
        var pattern = (node.IsDirectory ? node.Name : Path.GetFileNameWithoutExtension(node.Name)) + "*";

        if (!AddSourceLauncher.EnsureCollectionPath(this, _settings)) return;
        using var form = new AddSourceForm(_settings.SourceCollectionPath, pattern, dest, () => Reload());
        form.ShowDialog(this);
    }

    /// <summary>"Delete file": xoá hẳn file đang chọn (sau khi xác nhận) — file nằm trên UNC nên không qua Thùng rác được.</summary>
    private void DeleteFile()
    {
        if (_tree.SelectedNode?.Tag is not FileLookupNode { IsDirectory: false } node) return;
        var ok = MessageBox.Show(this, $"Xoá file này?\n\n{node.FullPath}\n\nKhông thể khôi phục.",
            "Bcode — Delete file", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (ok != DialogResult.Yes) return;
        try
        {
            File.SetAttributes(node.FullPath, FileAttributes.Normal); // file read-only thì File.Delete báo lỗi
            File.Delete(node.FullPath);
            _statusLabel.Text = "Đã xoá: " + node.FullPath;
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không xoá được file:\n{ex.Message}", "Bcode — Delete file", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>"Clone files": nhân bản file đang chọn ngay trong thư mục của nó với tên mới (vd SVTran.xml → SVTran2.xml). Nếu
    /// cùng thư mục còn file khác cùng tên gốc (SVTran.f ...) thì hỏi có nhân bản luôn cả nhóm không. Không ghi đè file có sẵn.
    /// Chỉ đổi TÊN file — nội dung bên trong (tên controller, entity...) giữ nguyên, người dùng tự sửa.</summary>
    private void CloneFiles()
    {
        if (_tree.SelectedNode?.Tag is not FileLookupNode { IsDirectory: false } node) return;
        var dir = Path.GetDirectoryName(node.FullPath)!;
        var oldBase = Path.GetFileNameWithoutExtension(node.FullPath);

        var newBase = SimplePromptForm.Show(this, "Clone files", $"Tên mới (không có đuôi) cho bản nhân của \"{oldBase}\":", oldBase + "2")?.Trim();
        if (string.IsNullOrEmpty(newBase) || newBase.Equals(oldBase, StringComparison.OrdinalIgnoreCase)) return;
        if (newBase.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(this, "Tên có ký tự không hợp lệ.", "Bcode — Clone files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var sources = new List<string> { node.FullPath };
        try
        {
            var siblings = Directory.GetFiles(dir)
                .Where(f => !f.Equals(node.FullPath, StringComparison.OrdinalIgnoreCase)
                            && Path.GetFileNameWithoutExtension(f).Equals(oldBase, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (siblings.Count > 0 && MessageBox.Show(this,
                    $"Cùng thư mục còn {siblings.Count} file cùng tên gốc:\n{string.Join("\n", siblings.Select(Path.GetFileName))}\n\nNhân bản luôn cả những file này?",
                    "Bcode — Clone files", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                sources.AddRange(siblings);

            var targets = sources.Select(s => Path.Combine(dir, newBase + Path.GetExtension(s))).ToList();
            var exists = targets.Where(File.Exists).ToList();
            if (exists.Count > 0)
            {
                MessageBox.Show(this, "Đã có sẵn, không ghi đè:\n" + string.Join("\n", exists.Select(Path.GetFileName)),
                    "Bcode — Clone files", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            for (var i = 0; i < sources.Count; i++) File.Copy(sources[i], targets[i]);
            _statusLabel.Text = $"Đã nhân bản {sources.Count} file → {newBase}.*";
            Reload();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Không nhân bản được:\n{ex.Message}", "Bcode — Clone files", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- Tick chọn nhiều file + Copy to... (CopyMultiFileForm) ----
    private bool _suspendCheck;

    /// <summary>Tick một thư mục thì tick (hoặc bỏ tick) mọi thứ bên trong; chỉ phản ứng với thao tác của người dùng, không với lần đặt bằng code.</summary>
    private void OnTreeAfterCheck(object? sender, TreeViewEventArgs e)
    {
        if (_suspendCheck || e.Action == TreeViewAction.Unknown) return;
        _suspendCheck = true;
        try { SetCheckedRecursive(e.Node, e.Node.Checked); }
        finally { _suspendCheck = false; }
        PushCheckedCount();
    }

    private static void SetCheckedRecursive(TreeNode node, bool value)
    {
        foreach (TreeNode child in node.Nodes)
        {
            child.Checked = value;
            SetCheckedRecursive(child, value);
        }
    }

    private void SetAllChecked(bool value)
    {
        _suspendCheck = true;
        try
        {
            void Walk(TreeNodeCollection nodes)
            {
                foreach (TreeNode n in nodes) { n.Checked = value; Walk(n.Nodes); }
            }
            Walk(_tree.Nodes);
        }
        finally { _suspendCheck = false; }
        PushCheckedCount();
    }

    /// <summary>Các file (không phải thư mục) đang được tick trên cây.</summary>
    private List<string> CheckedFiles()
    {
        var list = new List<string>();
        void Walk(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Checked && n.Tag is FileLookupNode { IsDirectory: false } f && !string.IsNullOrWhiteSpace(f.FullPath)) list.Add(f.FullPath);
                Walk(n.Nodes);
            }
        }
        Walk(_tree.Nodes);
        return list;
    }

    private void PushCheckedCount()
    {
        if (_barWeb.CoreWebView2 is null) return;
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setCheckedCount && window.setCheckedCount({CheckedFiles().Count})");
    }

    /// <summary>Copy to... nhiều file: các file đã tick; chưa tick gì thì lấy file đang chọn trên cây.</summary>
    private void ShowCopyMultiDialog()
    {
        var files = CheckedFiles();
        if (files.Count == 0 && _tree.SelectedNode?.Tag is FileLookupNode { IsDirectory: false } sel) files.Add(sel.FullPath);
        if (files.Count == 0)
        {
            MessageBox.Show(this, "Chưa tick file nào trên cây. Tick các file cần copy (tick thư mục để chọn cả thư mục) rồi bấm Copy to...", "Bcode — Copy to", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var form = new CopyMultiFileForm(files, _pathText.Trim(), _settings);
        form.ShowDialog(this);
    }

    /// <summary>File Lookup context menu — "Copy File(s) to...": clones the selected file into
    /// another configured project (see CopyFileToForm). _pathText is the root File Lookup is
    /// currently browsing — the file's relative position under it (e.g.
    /// App_Data\Controllers\Grid\...) is what gets mirrored under the destination project.</summary>
    private void ShowCopyFileToDialog()
    {
        if (_tree.SelectedNode?.Tag is not FileLookupNode { IsDirectory: false } node) return;
        if (string.IsNullOrWhiteSpace(_pathText)) return;

        using var form = new CopyFileToForm(_settings.Workspaces, _pathText.Trim(), new[] { node.FullPath });
        form.ShowDialog(this);
    }

    /// <summary>"SVTran.xml / Dir / Controllers" — leaf-first, matching FCode's own preview breadcrumb.</summary>
    private static string BuildBreadcrumb(TreeNode node)
    {
        var parts = new List<string>();
        for (var n = node; n is not null; n = n.Parent)
            parts.Add(n.Text);
        return string.Join(" / ", parts);
    }

    /// <summary>Màu chữ theo đuôi file để phân biệt nhanh: .f (script đã biên dịch) = cam, .xml (nguồn) = xanh dương. Đuôi khác = màu mặc
    /// định (Color.Empty). Chọn sắc độ riêng cho nền tối/sáng để luôn đọc rõ.</summary>
    private static Color ExtensionColor(string fileName)
    {
        // Tuỳ chọn ở Template giao diện → "Màu cây": none = màu chữ của theme; custom = màu tự chọn theo đuôi; auto = mặc định bên dưới.
        var opt = Bcode.App.UI.TreeColorOptions.Current;
        if (opt.FileMode == Bcode.App.UI.TreeColorOptions.None) return Color.Empty;
        if (opt.FileMode == Bcode.App.UI.TreeColorOptions.Custom)
        {
            var picked = Path.GetExtension(fileName).ToLowerInvariant() switch { ".f" => opt.FileF, ".xml" => opt.FileXml, _ => opt.FileOther };
            return Bcode.App.UI.UiTemplate.ParseColor(picked) ?? Color.Empty;
        }
        var dark = AppColors.IsDark;
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".f" => dark ? Color.FromArgb(255, 170, 51) : Color.FromArgb(176, 92, 0),
            ".xml" => dark ? Color.FromArgb(94, 190, 255) : Color.FromArgb(0, 95, 170),
            _ => Color.Empty,
        };
    }

    /// <summary>Màu chữ thư mục: chỉ khi chọn "Tự chọn màu" và có khai báo; còn lại màu chữ của theme.</summary>
    private static Color FolderColor()
    {
        var opt = Bcode.App.UI.TreeColorOptions.Current;
        return opt.FileMode == Bcode.App.UI.TreeColorOptions.Custom ? Bcode.App.UI.UiTemplate.ParseColor(opt.FileFolder) ?? Color.Empty : Color.Empty;
    }

    /// <summary>Đổi theme sáng/tối thì tô lại màu theo đuôi (file đang báo lỗi giữ màu đỏ).</summary>
    private void RecolorTreeOnTheme() => RecolorTreeByExtension(_tree.Nodes.Cast<TreeNode>());

    private void RecolorTreeByExtension(IEnumerable<TreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            if (n.Tag is FileLookupNode { IsDirectory: false } f && !f.HasIssuesInTree) n.ForeColor = ExtensionColor(f.Name);
            else if (n.Tag is FileLookupNode { IsDirectory: true } d && !d.HasIssuesInTree) n.ForeColor = FolderColor();
            RecolorTreeByExtension(n.Nodes.Cast<TreeNode>());
        }
    }

    private static TreeNode ToTreeNode(FileLookupNode node)
    {
        var key = node.IsDirectory ? "folder" : Path.GetExtension(node.Name).ToLowerInvariant() switch
        {
            ".xlsx" or ".xls" or ".xlsm" or ".xlsb" => "excel",
            ".rpt" => "rpt",
            _ => "bee",
        };
        var treeNode = new TreeNode(node.Name) { Tag = node, ImageKey = key, SelectedImageKey = key };
        // File thiếu entity/include: tô đỏ + tooltip liệt kê; thư mục chứa nó cũng đỏ để thấy
        // ngay từ cây thu gọn.
        if (node.HasIssuesInTree)
            treeNode.ForeColor = AppColors.Danger;
        else if (!node.IsDirectory)
            treeNode.ForeColor = ExtensionColor(node.Name);
        else
            treeNode.ForeColor = FolderColor();
        if (node.Issues.Count > 0)
        {
            treeNode.NodeFont = null;
            treeNode.ToolTipText = string.Join(Environment.NewLine, node.Issues);
        }
        foreach (var child in node.Children)
            treeNode.Nodes.Add(ToTreeNode(child));
        return treeNode;
    }
}