using System.ComponentModel;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// "Copy source standard" — WCommand tree's right-click action for cloning a menu item's
/// own source files (resolved the same way Gen Update/File Lookup do, via
/// FileLookupService.BuildTreeForMenuItem) into new files with new names, right next to
/// the originals — e.g. cloning SVTran.xml into SVTranCustom.xml as a starting point for a
/// customized copy. This is the "tick files, clone with a new name" part of FCode's own
/// WCommand > Copy Standard Source; unlike that screen, it doesn't touch any live
/// FastBusiness runtime or menu registration — it only clones files on disk (see
/// WCommandTreeControl's class doc comment for why the rest isn't reproduced here).
/// </summary>
public class CopySourceStandardForm : Form
{
    private sealed class CloneRow
    {
        public string SourcePath { get; init; } = "";
        public string Directory { get; init; } = "";
        public string OriginalName { get; init; } = "";
        public string NewName { get; set; } = "";
    }

    private readonly TreeView _sourceTree;
    private readonly CheckBox _checkAllBox;
    private readonly DataGridView _grid;
    private readonly TextBox _batchNameBox;
    private readonly BindingList<CloneRow> _rows = new();

    public CopySourceStandardForm(FileLookupService fileLookupService, string sourceRoot, WCommandItem item)
    {
        Text = $"Copy source standard — {item.Bar}";
        Width = 940;
        Height = 600;
        MinimumSize = new Size(760, 440);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        // Cho phép phóng to/thu nhỏ và kéo giãn tự do — trước đây khoá cả hai nên cửa sổ
        // mở lên to sẵn (do SplitterDistance cố định 300px cộng khung trái quá rộng) mà
        // không có cách nào thu lại hay Restore Down.
        MinimizeBox = true;
        MaximizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;

        // FixedPanel = Panel1: khung trái (cây file) giữ nguyên bề rộng khi kéo giãn/phóng to
        // cửa sổ, phần rộng thêm ra dồn hết cho khung phải (grid) — tránh tình trạng khung
        // trái phình to che mất khung phải khi resize.
        //
        // Panel1MinSize/Panel2MinSize/SplitterDistance KHÔNG được đặt ngay ở đây: lúc này
        // SplitContainer chưa được gắn vào Form (chưa Controls.Add), nên Width của nó vẫn
        // đang là giá trị mặc định rất nhỏ (~150px) — đặt SplitterDistance=240 lúc Width=150
        // ném ngay ArgumentOutOfRangeException ("SplitterDistance must be between
        // Panel1MinSize and Width - Panel2MinSize"), đúng lỗi vừa gặp. Ba dòng đó dời xuống
        // Form Load bên dưới, khi Form + SplitContainer đã có kích thước thật.
        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1 };

        // ---- Left: checkbox tree of the menu item's related source files ----
        var left = new Panel { Dock = DockStyle.Fill };
        var leftTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false };
        leftTop.Controls.Add(new Label
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
        addButton.Click += (_, _) => AddCheckedToGrid();
        leftTop.Controls.Add(_checkAllBox);
        leftTop.Controls.Add(addButton);

        _sourceTree = new TreeView { Dock = DockStyle.Fill, CheckBoxes = true, HideSelection = false };
        left.Controls.Add(_sourceTree);
        left.Controls.Add(leftTop);

        var root = fileLookupService.BuildTreeForMenuItem(sourceRoot, item.Link, item.SysId);
        _sourceTree.Nodes.Add(ToTreeNode(root));
        _sourceTree.ExpandAll();

        // ---- Right: batch grid — New Name is editable, Clone runs the actual File.Copy ----
        var right = new Panel { Dock = DockStyle.Fill };
        var rightLabel = new Label
        {
            Text = "File sẽ clone (sửa New Name rồi bấm Clone):",
            Dock = DockStyle.Top,
            Height = 22,
            Padding = new Padding(8, 4, 0, 0)
        };

        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            Margin = new Padding(8, 0, 8, 0)
        };
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Thư mục",
            DataPropertyName = nameof(CloneRow.Directory),
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "File gốc",
            DataPropertyName = nameof(CloneRow.OriginalName),
            ReadOnly = true,
            Width = 170
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "New Name",
            DataPropertyName = nameof(CloneRow.NewName),
            Width = 170
        });
        _grid.DataSource = _rows;

        // Đổi tên hàng loạt: gõ 1 tên chung, mỗi dòng vẫn giữ đuôi file gốc của nó —
        // vd 3 dòng SVTran.xml/SVTran.f/SVTran.txt gõ "SVTranCustom" ra SVTranCustom.xml/
        // SVTranCustom.f/SVTranCustom.txt. Không đụng tới dòng nào không có trong _rows.
        var batchBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, WrapContents = false, Padding = new Padding(8, 2, 8, 2) };
        batchBar.Controls.Add(new Label { Text = "Đổi tên hàng loạt:", AutoSize = true, Padding = new Padding(0, 6, 6, 0) });
        _batchNameBox = new TextBox { Width = 220, Margin = new Padding(0, 2, 6, 0) };
        batchBar.Controls.Add(_batchNameBox);
        var applyBatchButton = PillButton.Flat("Áp dụng cho tất cả");
        applyBatchButton.Click += (_, _) => ApplyBatchName();
        batchBar.Controls.Add(applyBatchButton);

        var removeButton = PillButton.Flat("Bỏ dòng đã chọn");
        removeButton.Click += (_, _) => RemoveSelectedRows();

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10) };
        var cloneButton = PillButton.Flat("Clone", primary: true);
        cloneButton.Click += (_, _) => DoClone();
        var closeButton = PillButton.Flat("Đóng");
        closeButton.Margin = new Padding(8, 0, 0, 0);
        closeButton.Click += (_, _) => Close();
        bottom.Controls.Add(cloneButton);
        bottom.Controls.Add(closeButton);
        bottom.Controls.Add(removeButton);

        right.Controls.Add(_grid);
        right.Controls.Add(batchBar);
        right.Controls.Add(rightLabel);

        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        Controls.Add(split);
        Controls.Add(bottom);
        ThemeManager.Apply(this);

        // Chỉ tới đây Form và SplitContainer mới có kích thước thật (940x600 trừ bottom bar),
        // nên đặt min-size + splitter distance ở Load thay vì trong object initializer phía
        // trên — xem chú thích chỗ khởi tạo split.
        Load += (_, _) =>
        {
            split.Panel1MinSize = 200;
            split.Panel2MinSize = 380;
            split.SplitterDistance = 240;
        };
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

    private void AddCheckedToGrid()
    {
        foreach (TreeNode root in _sourceTree.Nodes)
            CollectChecked(root);
    }

    private void CollectChecked(TreeNode node)
    {
        if (node.Checked && node.Tag is FileLookupNode { IsDirectory: false } file)
        {
            if (!_rows.Any(r => r.SourcePath == file.FullPath))
            {
                var dir = System.IO.Path.GetDirectoryName(file.FullPath) ?? "";
                var name = System.IO.Path.GetFileNameWithoutExtension(file.FullPath);
                var ext = System.IO.Path.GetExtension(file.FullPath);
                _rows.Add(new CloneRow
                {
                    SourcePath = file.FullPath,
                    Directory = dir,
                    OriginalName = System.IO.Path.GetFileName(file.FullPath),
                    NewName = $"{name}_Copy{ext}"
                });
            }
        }
        foreach (TreeNode child in node.Nodes)
            CollectChecked(child);
    }

    private void RemoveSelectedRows()
    {
        foreach (DataGridViewRow row in _grid.SelectedRows.Cast<DataGridViewRow>().OrderByDescending(r => r.Index))
            if (row.DataBoundItem is CloneRow cr) _rows.Remove(cr);
    }

    /// <summary>Sets every row's New Name to the same typed base name, keeping each row's own
    /// original extension — so ticking SVTran.xml + SVTran.f and typing "SVTranCustom" here
    /// gives SVTranCustom.xml and SVTranCustom.f, not one shared filename.</summary>
    private void ApplyBatchName()
    {
        _grid.EndEdit();
        var baseName = _batchNameBox.Text.Trim();
        if (baseName.Length == 0)
        {
            MessageBox.Show(this, "Nhập tên chung trước khi áp dụng.", "Bcode — Copy source standard",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_rows.Count == 0)
        {
            MessageBox.Show(this, "Chưa có file nào — tick chọn ở Source File rồi bấm Add.", "Bcode — Copy source standard");
            return;
        }

        foreach (var row in _rows)
            row.NewName = baseName + System.IO.Path.GetExtension(row.OriginalName);

        // BindingList<CloneRow> không tự phát hiện thay đổi field vì CloneRow không cài
        // INotifyPropertyChanged — ResetBindings ép grid đọc lại toàn bộ giá trị hiện có.
        _rows.ResetBindings();
    }

    private void DoClone()
    {
        _grid.EndEdit();
        if (_rows.Count == 0)
        {
            MessageBox.Show(this, "Chưa có file nào — tick chọn ở Source File rồi bấm Add.", "Bcode — Copy source standard");
            return;
        }
        if (_rows.Any(r => string.IsNullOrWhiteSpace(r.NewName)))
        {
            MessageBox.Show(this, "Có dòng chưa nhập New Name.", "Bcode — Copy source standard",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var destinations = _rows.Select(r => System.IO.Path.Combine(r.Directory, r.NewName)).ToList();
        var existing = destinations.Where(File.Exists).ToList();
        if (existing.Count > 0)
        {
            var confirm = MessageBox.Show(this,
                $"{existing.Count} file đích đã tồn tại, ghi đè?\n" + string.Join("\n", existing),
                "Bcode — Copy source standard", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
        }

        var copied = 0;
        var errors = new List<string>();
        for (var i = 0; i < _rows.Count; i++)
        {
            try
            {
                File.Copy(_rows[i].SourcePath, destinations[i], overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                errors.Add($"{_rows[i].OriginalName}: {ex.Message}");
            }
        }

        if (errors.Count > 0)
        {
            MessageBox.Show(this, $"Đã clone {copied}/{_rows.Count} file.\nLỗi:\n" + string.Join("\n", errors),
                "Bcode — Copy source standard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            MessageBox.Show(this, $"Đã clone {copied} file thành công.",
                "Bcode — Copy source standard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}