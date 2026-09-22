using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Gen Update" document tab (WCommand &gt; Gen Update in FCode) — NOT to be confused with
/// the older "Gen Update (dòng đã chọn)" SQL UPDATE-statement generator already in SQL
/// Query/Command's result-grid context menu (<see cref="GenUpdateService"/>,
/// Ctrl+Shift+U) — that turns selected grid rows into UPDATE statements and is unrelated.
/// This tab instead packages SOURCE FILES (the .f/.xml/.xlsx under App_Data) tied to one
/// or more WCommand menu items into an "update" folder to send to a client site, matching
/// FCode's own WCommand &gt; Gen Update screen.
///
/// Double-click a node in the WCommand tree while this tab is the active document tab (see
/// MainForm.OpenWCommandItem) to populate the Source File tree on the right; otherwise
/// double-click still opens/targets File Lookup as before.
///
/// Scope note (see README "Cập nhật gần đây"): FCode's real screen also has a "Declaration
/// for Generation" / "Content for Generation" section for declaring ad-hoc SQL objects
/// (Category/File/SELECT FROM/WHERE/Top Script/Bottom Script) inside the same update
/// package — none of the screenshots showed that grid populated, and the description of
/// what was wanted here only covered the file side ("double-click menu -> files show on
/// the right -> Add -> name + path -> Create Update File"), so only the file-packaging
/// half is built. The SQL-object-declaration half is left out until it's actually needed.
/// </summary>
public class GenUpdatePackageControl : UserControl
{
    private readonly FileLookupService _fileLookupService;

    private readonly TextBox _programPathBox;
    private readonly TextBox _mobilePathBox;
    private readonly TextBox _saveAtPathBox;
    private readonly TextBox _folderNameBox;
    private readonly TextBox _descriptionBox;
    private readonly RadioButton _appRadio;
    private readonly RadioButton _sysRadio;
    private readonly RadioButton _otherRadio;

    private readonly TreeView _sourceTree;
    private readonly CheckBox _checkAllBox;

    private readonly ListBox _batchList; // full file paths queued for the update package
    private readonly Label _resultPathLabel;
    private readonly Button _copyLinkButton;
    private string? _lastCreatedPath; // destRoot from the most recent successful "Create Update File"

    private readonly string? _sourceRoot; // site root (Workspace.SourcePath) for the workspace this tab was opened with —
                                           // BuildTreeForMenuItem below resolves both Main\ and App_Data\Controllers\ from this

    public GenUpdatePackageControl(FileLookupService fileLookupService, Workspace? workspace)
    {
        _fileLookupService = fileLookupService;
        Dock = DockStyle.Fill;

        // ---- Left: Declaration Path and Connection + batch of files queued ----
        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(8)
        };

        var sectionTitle = new Label
        {
            Text = "Declaration Path and Connection",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        left.Controls.Add(sectionTitle);

        left.Controls.Add(Labeled("Program Path", _programPathBox = new TextBox { Width = 300, Text = workspace?.ProgramPath ?? "" }));
        left.Controls.Add(Labeled("Mobile Path", _mobilePathBox = new TextBox { Width = 300, Text = workspace?.MobilePath ?? "" }));
        left.Controls.Add(Labeled("Save At Path", _saveAtPathBox = new TextBox { Width = 300, Text = workspace?.WorkingPath ?? "" }));

        var defaultFolder = string.Join("_", new[]
        {
            workspace?.ProjectId,
            Environment.MachineName,
            DateTime.Now.ToString("yyyyMMddHHmmss")
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        left.Controls.Add(Labeled("+ Folder Name", _folderNameBox = new TextBox { Width = 300, Text = defaultFolder }));
        left.Controls.Add(Labeled("Description", _descriptionBox = new TextBox { Width = 300 }));

        var radioPanel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 10) };
        _appRadio = new RadioButton { Text = "App", Checked = true, AutoSize = true };
        _sysRadio = new RadioButton { Text = "Sys", AutoSize = true, Margin = new Padding(10, 0, 0, 0) };
        _otherRadio = new RadioButton { Text = "Other", AutoSize = true, Margin = new Padding(10, 0, 0, 0) };
        radioPanel.Controls.Add(_appRadio);
        radioPanel.Controls.Add(_sysRadio);
        radioPanel.Controls.Add(_otherRadio);
        left.Controls.Add(radioPanel);

        left.Controls.Add(new Label { Text = "File đã thêm vào gói update:", AutoSize = true, Margin = new Padding(0, 4, 0, 2) });
        _batchList = new ListBox { Width = 600, Height = 240, HorizontalScrollbar = true };
        left.Controls.Add(_batchList);

        var batchButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0) };
        var removeSelectedButton = PillButton.Flat("Bỏ file đã chọn");
        removeSelectedButton.Click += (_, _) => RemoveSelectedFromBatch();
        var clearBatchButton = PillButton.Flat("Xoá hết");
        clearBatchButton.Margin = new Padding(6, 0, 0, 0);
        clearBatchButton.Click += (_, _) => _batchList.Items.Clear();
        batchButtons.Controls.Add(removeSelectedButton);
        batchButtons.Controls.Add(clearBatchButton);
        left.Controls.Add(batchButtons);

        var createButton = PillButton.Flat("Create Update File", primary: true);
        createButton.Margin = new Padding(0, 14, 0, 4);
        createButton.Click += (_, _) => CreateUpdateFiles();
        left.Controls.Add(createButton);

        _resultPathLabel = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(300, 0) };
        left.Controls.Add(_resultPathLabel);

        _copyLinkButton = PillButton.Flat("Copy link");
        _copyLinkButton.Margin = new Padding(0, 4, 0, 0);
        _copyLinkButton.Enabled = false;
        _copyLinkButton.Click += (_, _) => CopyResultPath();
        left.Controls.Add(_copyLinkButton);

        // ---- Right: Source File tree (populated per double-clicked WCommand menu item) ----
        var right = new Panel { Dock = DockStyle.Fill };
        var rightTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false };
        rightTop.Controls.Add(new Label
        {
            Text = "Source File",
            AutoSize = true,
            Padding = new Padding(0, 6, 12, 0),
            Font = new Font(Font, FontStyle.Bold)
        });
        _checkAllBox = new CheckBox { Text = "Check All", AutoSize = true, Padding = new Padding(0, 4, 0, 0) };
        _checkAllBox.CheckedChanged += (_, _) => SetAllChecked(_checkAllBox.Checked);
        var addButton = PillButton.Flat("Add", primary: true);
        addButton.Margin = new Padding(10, 2, 0, 0);
        addButton.Click += (_, _) => AddCheckedToBatch();
        rightTop.Controls.Add(_checkAllBox);
        rightTop.Controls.Add(addButton);

        _sourceTree = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };

        right.Controls.Add(_sourceTree);
        right.Controls.Add(rightTop);

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 340 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);
        Controls.Add(split);

        if (workspace is not null && !string.IsNullOrWhiteSpace(workspace.SourcePath))
            _sourceRoot = workspace.SourcePath;
    }

    private static Control Labeled(string label, Control input)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.TopDown,
            Margin = new Padding(0, 0, 0, 8)
        };
        panel.Controls.Add(new Label { Text = label, AutoSize = true });
        panel.Controls.Add(input);
        return panel;
    }

    /// <summary>Called when a WCommand menu node is double-clicked while this tab is active —
    /// rebuilds the Source File tree from that item's Link/SysId, the same precise resolution
    /// File Lookup itself uses (FileLookupService.BuildTreeForMenuItem: Main\&lt;link&gt; plus the
    /// related App_Data\Controllers\&lt;sysId&gt; files) instead of a naive filename-substring search.</summary>
    public void LoadForMenuItem(WCommandItem item)
    {
        if (_sourceRoot is null)
        {
            MessageBox.Show(this, "Workspace hiện tại chưa khai báo Source Path (UNC). Vào File > Choose Server để thêm.",
                "Bcode — Gen Update");
            return;
        }
        if (string.IsNullOrWhiteSpace(item.Link) && string.IsNullOrWhiteSpace(item.SysId))
        {
            MessageBox.Show(this, $"Menu \"{item.Bar}\" không có Link/SysId gắn với source (có thể là mục nhóm/menu cha).",
                "Bcode — Gen Update");
            return;
        }

        // onlyFInGridFilterDir: true — Grid/Filter/Dir already contain the compiled/encrypted
        // ".f" version, so the matching ".xml" source is left out of the update package.
        var root = _fileLookupService.BuildTreeForMenuItem(_sourceRoot, item.Link, item.SysId, onlyFInGridFilterDir: true);

        _sourceTree.Nodes.Clear();
        _sourceTree.Nodes.Add(ToTreeNode(root));
        _sourceTree.ExpandAll();
        _checkAllBox.Checked = false;
    }

    private static TreeNode ToTreeNode(FileLookupNode node)
    {
        var treeNode = new TreeNode(node.Name) { Tag = node };
        foreach (var child in node.Children)
            treeNode.Nodes.Add(ToTreeNode(child));
        return treeNode;
    }

    private void SetAllChecked(bool value)
    {
        foreach (TreeNode root in _sourceTree.Nodes)
            SetChecked(root, value);
    }

    private static void SetChecked(TreeNode node, bool value)
    {
        node.Checked = value;
        foreach (TreeNode child in node.Nodes)
            SetChecked(child, value);
    }

    private void AddCheckedToBatch()
    {
        var added = 0;
        foreach (TreeNode root in _sourceTree.Nodes)
            added += CollectChecked(root);

        if (added > 0)
        {
            MessageBox.Show(this, $"Đã thêm {added} file vào gói update.", "Bcode — Gen Update",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private int CollectChecked(TreeNode node)
    {
        var count = 0;
        if (node.Checked && node.Tag is FileLookupNode { IsDirectory: false } file)
        {
            var existing = _batchList.Items.Cast<string>();
            if (!existing.Contains(file.FullPath))
            {
                _batchList.Items.Add(file.FullPath);
                count++;
            }
        }
        foreach (TreeNode child in node.Nodes)
            count += CollectChecked(child);
        return count;
    }

    private void RemoveSelectedFromBatch()
    {
        for (var i = _batchList.SelectedIndices.Count - 1; i >= 0; i--)
            _batchList.Items.RemoveAt(_batchList.SelectedIndices[i]);
    }

    private void CreateUpdateFiles()
    {
        if (_batchList.Items.Count == 0)
        {
            MessageBox.Show(this, "Chưa có file nào trong gói update — check file bên Source File rồi bấm Add.",
                "Bcode — Gen Update");
            return;
        }
        if (string.IsNullOrWhiteSpace(_saveAtPathBox.Text))
        {
            MessageBox.Show(this, "Chưa nhập Save At Path.", "Bcode — Gen Update");
            return;
        }
        if (string.IsNullOrWhiteSpace(_folderNameBox.Text))
        {
            MessageBox.Show(this, "Chưa nhập Folder Name.", "Bcode — Gen Update");
            return;
        }
        if (_sourceRoot is null)
        {
            MessageBox.Show(this, "Không xác định được Source Path để tính đường dẫn tương đối trong gói update.",
                "Bcode — Gen Update");
            return;
        }

        var destRoot = Path.Combine(_saveAtPathBox.Text.Trim(), _folderNameBox.Text.Trim());
        try
        {
            Directory.CreateDirectory(destRoot);
            var copied = 0;
            foreach (string sourceFile in _batchList.Items)
            {
                // _sourceRoot is now the site root, so relative already starts with "Main\..." or
                // "App_Data\Controllers\...", matching FCode's own Gen Update folder layout.
                var relative = Path.GetRelativePath(_sourceRoot, sourceFile);
                var destFile = Path.Combine(destRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(sourceFile, destFile, overwrite: true);
                copied++;
            }

            if (!string.IsNullOrWhiteSpace(_descriptionBox.Text))
                File.WriteAllText(Path.Combine(destRoot, "description.txt"), _descriptionBox.Text);

            _resultPathLabel.Text = $"Đã tạo {copied} file tại: {destRoot}";
            _lastCreatedPath = destRoot;
            _copyLinkButton.Enabled = true;
            MessageBox.Show(this, _resultPathLabel.Text, "Bcode — Gen Update",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Create Update File", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Copies the last "Create Update File" destination folder path to the clipboard
    /// so it can be pasted straight into a chat/email to send to the client site.</summary>
    private void CopyResultPath()
    {
        if (string.IsNullOrWhiteSpace(_lastCreatedPath)) return;
        try
        {
            Clipboard.SetText(_lastCreatedPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Copy link", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
