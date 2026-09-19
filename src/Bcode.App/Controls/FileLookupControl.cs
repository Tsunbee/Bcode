using System.Diagnostics;
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
    private readonly TextBox _pathBox;
    private readonly TextBox _searchBox;
    private readonly CheckBox _onlyShowFiltered;
    private readonly ComboBox _extensionCombo;
    private readonly Button _goButton;
    private readonly Label _statusLabel;
    private readonly FileLookupService _service;
    private readonly ScriptFileService _scriptFileService;

    private readonly AppSettings _settings;

    // Preview pane (right side)
    private readonly Label _previewPathLabel;
    private readonly Label _previewModifiedLabel;
    private readonly Label _previewBreadcrumbLabel;
    private readonly Button _editButton;
    private readonly ScriptEditorControl _previewEditor;

    // Bumped on every PreviewFile call; a background read only applies its result if it's
    // still the current one when it finishes — otherwise a slow read for a file the user
    // already clicked past would overwrite whatever they clicked next.
    private int _previewRequestVersion;

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

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 56, ColumnCount = 1, RowCount = 2 };
        // Was a plain FlowLayoutPanel (WrapContents = false) with a fixed Width = 320 path
        // box — the row's total content width (label + 320px box + Load button) is wider
        // than the tree pane itself whenever it's narrower than ~400px (its default is 320,
        // see desiredTreeWidth below), and a non-wrapping FlowLayoutPanel doesn't shrink its
        // children to fit — it just lets the overflow get clipped by the panel's edge
        // instead, which is what cut off the Path box and "Load" button. Same Fill-in-the-
        // middle pattern as previewRow1/row2 below fixes it: the path box now stretches or
        // shrinks with whatever width is actually available, with the label and button
        // pinned to the edges.
        var row1 = new Panel { Dock = DockStyle.Top, Height = 26 };
        var row1Label = new Label { Text = "Path:", Dock = DockStyle.Left, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
        _goButton = new Button { Text = "Load", Dock = DockStyle.Right, Width = 50 };
        _goButton.Click += (_, _) => { _menuMode = false; Reload(); };
        _pathBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = @"\\server\CustomerPro\...\App_Data" };
        row1.Controls.Add(_pathBox);
        row1.Controls.Add(row1Label);
        row1.Controls.Add(_goButton);

        // A plain FlowLayoutPanel here left everything packed to the left with a big dead
        // gap after Search filling the rest of the row. Splitting it into a Left-docked
        // FlowLayoutPanel for the fixed-width controls plus a Fill-docked search box makes
        // the search box stretch to close that gap instead of leaving it empty.
        var row2 = new Panel { Dock = DockStyle.Top, Height = 26 };
        var row2Controls = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false };
        // Was Width = 70 — a DropDownList ComboBox this narrow reserves most of that for its
        // dropdown-arrow button, leaving barely enough room for a 2-char item like ".f" and
        // none at all for the 5-char ones (".aspx"/".xlsx"), so the tail of whatever's
        // selected gets clipped by the box's own edge. 90 gives every item in the list room
        // to draw in full.
        _extensionCombo = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
        _extensionCombo.Items.AddRange(new object[] { ".f", ".xml", ".aspx", ".xlsx", ".rpt" });
        _extensionCombo.SelectedIndex = 0;
        _extensionCombo.SelectedIndexChanged += (_, _) => { _menuMode = false; Reload(); };
        _onlyShowFiltered = new CheckBox { Text = "Only Show *.ext", Checked = true, AutoSize = true };
        _onlyShowFiltered.CheckedChanged += (_, _) => { _menuMode = false; Reload(); };
        row2Controls.Controls.Add(_extensionCombo);
        row2Controls.Controls.Add(_onlyShowFiltered);

        _searchBox = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Search..." };
        _searchBox.TextChanged += (_, _) => { _menuMode = false; Reload(); };
        row2.Controls.Add(_searchBox);
        row2.Controls.Add(row2Controls);

        top.Controls.Add(row1, 0, 0);
        top.Controls.Add(row2, 0, 1);

        _statusLabel = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0),
            ForeColor = SystemColors.GrayText,
            Text = ""
        };

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        // Bee's own icon in front of file nodes, a drawn folder glyph in front of
        // directory nodes — was the bee icon for every node regardless of type, which (a)
        // didn't read as a folder at a glance and (b) went missing entirely for a while
        // (see AppIcons.FileTreeBitmap/FolderTreeBitmap — the underlying embedded resource
        // wiring had been dropped by an unrelated git merge). ToTreeNode below picks the
        // key per node based on FileLookupNode.IsDirectory.
        var images = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        if (AppIcons.FileTreeBitmap is { } beeIcon) images.Images.Add("bee", beeIcon);
        if (AppIcons.FolderTreeBitmap is { } folderIcon) images.Images.Add("folder", folderIcon);
        if (images.Images.Count > 0) _tree.ImageList = images;
        _tree.AfterSelect += (_, e) =>
        {
            if (e.Node?.Tag is FileLookupNode { IsDirectory: false } node)
                PreviewFile(node.FullPath, e.Node);
        };
        _tree.NodeMouseDoubleClick += (_, e) =>
        {
            if (e.Node?.Tag is FileLookupNode { IsDirectory: false } node)
                FileActivated?.Invoke(node.FullPath);
        };

        var leftPanel = new Panel { Dock = DockStyle.Fill };
        leftPanel.Controls.Add(_tree);
        leftPanel.Controls.Add(_statusLabel);
        leftPanel.Controls.Add(top);

        // ---- Right preview pane ----
        _previewPathLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Font = new Font(Font, FontStyle.Bold)
        };
        // Viewing stays inline (read-only) here; editing opens BcodeViewer (a separate
        // Monaco/WebView2-based editor with an AI chat panel) on the file instead of
        // toggling in-place RichTextBox editing — see OpenInViewer.
        _editButton = new Button { Text = "Edit in BcodeViewer", Dock = DockStyle.Right, Width = 130, Enabled = false };
        _editButton.Click += (_, _) => OpenInViewer();

        // Plain Dock (Fill added first, then the Right-docked control) instead of a
        // TableLayoutPanel with AutoSize columns — the same pattern ScriptEditorControl's
        // own top bar already uses successfully; the AutoSize-column version rendered the
        // path label squeezed into a small box instead of spanning the row.
        var previewRow1 = new Panel { Dock = DockStyle.Top, Height = 26 };
        previewRow1.Controls.Add(_previewPathLabel);
        previewRow1.Controls.Add(_editButton);

        _previewModifiedLabel = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = SystemColors.GrayText
        };
        _previewBreadcrumbLabel = new Label
        {
            Dock = DockStyle.Right,
            AutoSize = false,
            Width = 260,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = SystemColors.GrayText,
            AutoEllipsis = true
        };
        var previewRow2 = new Panel { Dock = DockStyle.Top, Height = 20 };
        previewRow2.Controls.Add(_previewModifiedLabel);
        previewRow2.Controls.Add(_previewBreadcrumbLabel);

        _previewEditor = new ScriptEditorControl { ShowPathBar = false, ReadOnly = true };
        // F12 on an &Entity; reference opens a separate "peek" popup instead of replacing
        // the current preview — the file being read is usually why the user pressed F12 in
        // the first place, so it should stay on screen, not get swapped out.
        _previewEditor.EntityNavigationRequested += ShowEntityPopup;

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(_previewEditor);
        rightPanel.Controls.Add(previewRow2);
        rightPanel.Controls.Add(previewRow1);
        ShowNoSelection();

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
    }

    public void SetRootPath(string path)
    {
        _menuMode = false;
        _pathBox.Text = path;
        Reload();
    }

    /// <summary>
    /// Used when a WCommand menu node is activated: shows exactly the source for that
    /// menu item, the way the menu itself is wired to source — <paramref name="link"/>
    /// is the page file under "Main", <paramref name="sysId"/> is the controller folder
    /// under App_Data\Controllers holding its source files.
    /// </summary>
    public void ShowForMenuItem(string sourceRootPath, string link, string sysId)
    {
        _menuMode = true;
        _menuLink = link;
        _menuSysId = sysId;
        _pathBox.Text = sourceRootPath;
        Reload();
    }

    public void Reload()
    {
        _tree.Nodes.Clear();
        ShowNoSelection();
        if (string.IsNullOrWhiteSpace(_pathBox.Text)) return;

        var sw = Stopwatch.StartNew();
        FileLookupNode root;
        if (_menuMode)
        {
            root = _service.BuildTreeForMenuItem(_pathBox.Text.Trim(), _menuLink, _menuSysId);
        }
        else
        {
            root = _service.BuildTree(
                _pathBox.Text.Trim(),
                _extensionCombo.SelectedItem?.ToString() ?? ".f",
                string.IsNullOrWhiteSpace(_searchBox.Text) ? null : _searchBox.Text.Trim(),
                _onlyShowFiltered.Checked);
        }
        sw.Stop();

        _tree.Nodes.Add(ToTreeNode(root));
        if (_menuMode || !string.IsNullOrWhiteSpace(_searchBox.Text))
            _tree.ExpandAll();
        else
            _tree.Nodes[0].Expand();

        var fileCount = CountFiles(root);
        _statusLabel.Text = $"Kết quả {fileCount} file(s) — {sw.ElapsedMilliseconds} ms";
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
        _previewBreadcrumbLabel.Text = treeNode is not null
            ? BuildBreadcrumb(treeNode)
            : Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        _previewPathLabel.Text = path;
        _previewModifiedLabel.Text = "Đang tải...";
        _editButton.Enabled = false;
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
                        _previewModifiedLabel.Text = "Last Modified: " + modified.ToString("dd/MM/yyyy HH:mm:ss");
                        _previewEditor.LoadContent(path, content!);
                        _editButton.Enabled = true;
                    }
                    else
                    {
                        _previewModifiedLabel.Text = "";
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

    /// <summary>F12 "peek": shows an Include/related file in its own floating window
    /// instead of swapping it into the main preview — the file the user was just reading
    /// is exactly why they pressed F12, so it should stay put. Non-modal (owned by the main
    /// window so it doesn't get lost behind it) and re-entrant: F12 works inside the popup
    /// too, opening another popup on top for a chained lookup.</summary>
    private void ShowEntityPopup(string path)
    {
        var popup = new Form
        {
            Text = Path.GetFileName(path),
            Width = 900,
            Height = 650,
            StartPosition = FormStartPosition.CenterParent,
            ShowIcon = false
        };

        var editor = new ScriptEditorControl { ShowPathBar = true, ReadOnly = true };
        editor.EntityNavigationRequested += ShowEntityPopup;
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

    private void ShowNoSelection()
    {
        _previewPathLabel.Text = "(chưa chọn file nào)";
        _previewModifiedLabel.Text = "";
        _previewBreadcrumbLabel.Text = "";
        _editButton.Enabled = false;
        _previewEditor.Clear();
    }

    /// <summary>Launches BcodeViewer (a standalone Monaco/WebView2 editor with an AI chat
    /// panel) on the currently previewed file, prompting once to locate BcodeViewer.exe if
    /// it isn't configured yet (and remembering the choice in AppSettings, same as the
    /// existing VSAppPath/SqlSmsPath external-tool settings).</summary>
    private void OpenInViewer()
    {
        if (_previewEditor.CurrentPath is not { } path) return;

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

    /// <summary>"SVTran.xml / Dir / Controllers" — leaf-first, matching FCode's own preview breadcrumb.</summary>
    private static string BuildBreadcrumb(TreeNode node)
    {
        var parts = new List<string>();
        for (var n = node; n is not null; n = n.Parent)
            parts.Add(n.Text);
        return string.Join(" / ", parts);
    }

    private static TreeNode ToTreeNode(FileLookupNode node)
    {
        var key = node.IsDirectory ? "folder" : "bee";
        var treeNode = new TreeNode(node.Name) { Tag = node, ImageKey = key, SelectedImageKey = key };
        foreach (var child in node.Children)
            treeNode.Nodes.Add(ToTreeNode(child));
        return treeNode;
    }
}
