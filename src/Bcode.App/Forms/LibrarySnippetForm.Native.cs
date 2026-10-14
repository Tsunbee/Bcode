using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Giao diện WinForms của "Library (Snippets)" (chế độ App, thay trang library.html): bên trái ô lọc + danh sách gom theo Category (chỉ hiện snippet chung hoặc của dự án đang chọn),
/// bên phải sửa Name / Category / Dự án / Nội dung, thanh dưới hiện ĐƯỜNG DẪN ĐẦY ĐỦ của snippets.json (Copy / Mở thư mục) cùng Delete / Đóng / Save (Ctrl+S) / Insert.
/// Form giữ bản sao đang sửa; chỉ khi Save mới ghi xuống <see cref="Services.SnippetLibraryService"/> (lỗi ghi thì trả lại nguyên trạng).
/// </summary>
public partial class LibrarySnippetForm
{
    private sealed class Item { public string Name = "", Category = "General", Content = "", Project = ""; }

    private readonly List<Item> _items = new();
    private Item? _sel;
    private bool _dirty, _fill;
    private RoundedInput _q = null!, _name = null!, _cat = null!, _proj = null!, _content = null!;
    private TreeView _tree = null!;
    private Label _status = null!;
    private PillButton _bDelete = null!, _bInsert = null!;

    private void BuildNative()
    {
        foreach (var s in _service.Snippets) _items.Add(new Item { Name = s.Name, Category = string.IsNullOrWhiteSpace(s.Category) ? "General" : s.Category, Content = s.Content, Project = s.Project });
        KeyPreview = true;

        // ---- trái ----
        _q = NK.Input("Lọc snippet...");
        _q.Dock = DockStyle.Fill; _q.TextChanged += (_, _) => RenderTree();
        var newBtn = NK.Button("＋ New", NewItem, tip: "Thêm snippet mới"); newBtn.Dock = DockStyle.Right;
        var top = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 40), Padding = new Padding(8, 7, 8, 5) };
        top.Controls.Add(_q); top.Controls.Add(newBtn);
        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
        _tree.AfterSelect += (_, e) => { if (_fill) return; Flush(); Select(e.Node?.Tag as Item); };
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_tree); left.Controls.Add(top);

        // ---- phải ----
        _name = NK.Input(); _cat = NK.Input(); _proj = NK.Input("mọi dự án");
        _content = NK.Memo(18, "", mono: true); _content.Dock = DockStyle.Fill;
        var fields = NK.FormGrid(150, ("Name", _name), ("Category", _cat), ("Dự án (trống = mọi dự án)", _proj));
        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 8, 10, 8) };
        var contentLabel = NK.Muted("Nội dung"); contentLabel.Dock = DockStyle.Top;
        right.Controls.Add(_content); right.Controls.Add(contentLabel); right.Controls.Add(fields);
        foreach (var r in new[] { _name, _cat, _proj, _content }) r.TextChanged += (_, _) => { if (_fill) return; Flush(); MarkDirty(); if (r != _content) RenderTree(); };
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(left); split.Panel2.Controls.Add(right);
        Shown += (_, _) => { try { split.SplitterDistance = Math.Max(220, (int)(Width * 0.3)); } catch { } };

        // ---- thanh dưới ----
        var path = new Label { Text = _service.FilePath, AutoSize = true, BackColor = Color.Transparent, Font = ThemeManager.MonoFont, Margin = new Padding(0, 7, 8, 0), AutoEllipsis = true };
        var pathBar = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 5, 8, 5) };
        pathBar.Controls.Add(NK.Muted("📁 Lưu tại:")); pathBar.Controls.Add(path);
        pathBar.Controls.Add(NK.Button("Copy", () => { NK.CopyText(_service.FilePath); SetStatus("Đã copy đường dẫn.", ok: true); }, tip: "Copy đường dẫn đầy đủ"));
        pathBar.Controls.Add(NK.Button("Mở thư mục", OpenFolderNative, tip: "Mở thư mục chứa file"));

        _status = new Label { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 8, 14, 0) };
        _bDelete = NK.Button("Delete", DeleteItem); _bInsert = NK.Button("Insert vào Script Editor", InsertItem, primary: true, tip: "Chèn nội dung vào Script Editor đang mở");
        var footer = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 6, 8, 6) };
        footer.Controls.Add(_status); footer.Controls.Add(_bDelete);
        footer.Controls.Add(NK.Button("Đóng", CloseNative)); footer.Controls.Add(NK.Button("💾 Save", SaveNative, tip: "Lưu toàn bộ snippet xuống file (Ctrl+S)")); footer.Controls.Add(_bInsert);

        Controls.Add(split); Controls.Add(pathBar); Controls.Add(footer);
        KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.S) { e.Handled = true; SaveNative(); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = true; CloseNative(); }
        };
        FormClosing += (_, e) => { if (DialogResult == DialogResult.None && _dirty && !NK.Confirm(this, "Có thay đổi chưa lưu. Đóng và bỏ qua?", "Bcode — Library")) e.Cancel = true; };
        Shown += (_, _) => { RenderTree(); SelectFirst(); };
    }

    private bool OnlyCurrent => _currentProject.Length > 0;

    private void RenderTree()
    {
        var q = _q.Text.Trim().ToLowerInvariant();
        _fill = true;
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var groups = _items
            .Where(it => (q.Length == 0 || (it.Name + " " + it.Category + " " + it.Content + " " + it.Project).ToLowerInvariant().Contains(q))
                         && (!OnlyCurrent || it.Project.Length == 0 || string.Equals(it.Project, _currentProject, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(it => it.Category);
        foreach (var g in groups)
        {
            var cat = new TreeNode(g.Key) { NodeFont = new Font(ThemeManager.BaseFont, FontStyle.Bold) };
            foreach (var it in g)
            {
                var n = new TreeNode((it.Name.Length == 0 ? "(chưa đặt tên)" : it.Name) + (it.Project.Length > 0 ? "  ·  " + it.Project : "")) { Tag = it, ToolTipText = it.Category + " / " + it.Name };
                cat.Nodes.Add(n);
                if (it == _sel) _tree.SelectedNode = n;
            }
            _tree.Nodes.Add(cat); cat.Expand();
        }
        if (_tree.Nodes.Count == 0) _tree.Nodes.Add(new TreeNode(_items.Count > 0 ? "Không có snippet nào khớp." : "Chưa có snippet. Bấm ＋ New."));
        _tree.EndUpdate();
        _fill = false;
    }

    private void SelectFirst()
    {
        var first = _tree.Nodes.Cast<TreeNode>().SelectMany(c => c.Nodes.Cast<TreeNode>()).FirstOrDefault();
        if (first is not null) _tree.SelectedNode = first; else Select(null);
    }

    private void Select(Item? it)
    {
        _sel = it;
        _fill = true;
        _name.Text = it?.Name ?? ""; _cat.Text = it?.Category ?? ""; _proj.Text = it?.Project ?? ""; _content.Text = it?.Content ?? "";
        foreach (var c in new Control[] { _name, _cat, _proj, _content }) c.Enabled = it is not null;
        _bDelete.Enabled = it is not null; _bInsert.Enabled = it is not null;
        _fill = false;
    }

    private void Flush()
    {
        if (_sel is null) return;
        _sel.Name = _name.Text.Trim();
        _sel.Category = _cat.Text.Trim().Length > 0 ? _cat.Text.Trim() : "General";
        _sel.Project = _proj.Text.Trim();
        _sel.Content = _content.Text;
    }

    private void MarkDirty() { if (!_dirty) { _dirty = true; SetStatus("● Có thay đổi chưa lưu"); _status.ForeColor = AppColors.Warning; } }

    private void SetStatus(string text, bool error = false, bool ok = false)
    {
        _status.Text = text;
        _status.ForeColor = error ? AppColors.Danger : ok ? AppColors.Success : AppColors.TextMuted;
    }

    private void NewItem()
    {
        Flush();
        var it = new Item { Name = "New Snippet", Category = _cat.Text.Trim().Length > 0 ? _cat.Text.Trim() : "General", Project = OnlyCurrent ? _currentProject : _proj.Text.Trim() };
        _items.Add(it); _sel = it; MarkDirty(); RenderTree(); Select(it); _name.Inner.Focus(); _name.Inner.SelectAll();
    }

    private void DeleteItem()
    {
        if (_sel is null) return;
        if (!NK.Confirm(this, $"Xoá snippet \"{_sel.Name}\"?\n\n(Chỉ ghi xuống file khi bấm Save.)", "Bcode — Library")) return;
        _items.Remove(_sel); _sel = null; MarkDirty(); RenderTree(); SelectFirst();
    }

    private void SaveNative()
    {
        Flush();
        var list = _items.Select(i => new Snippet { Name = i.Name.Length == 0 ? "Snippet" : i.Name, Category = i.Category.Length == 0 ? "General" : i.Category, Content = i.Content, Project = i.Project.Trim() }).ToList();
        // Thay danh sách rồi ghi file; lỗi ghi thì trả lại nguyên trạng để người dùng không tưởng đã lưu.
        var backup = _service.Snippets.ToList();
        _service.Snippets.Clear(); _service.Snippets.AddRange(list);
        try { _service.Save(); _dirty = false; SetStatus($"Đã lưu {list.Count} snippet.", ok: true); }
        catch (Exception ex)
        {
            _service.Snippets.Clear(); _service.Snippets.AddRange(backup);
            SetStatus($"Không lưu được vào {_service.FilePath}: {ex.Message}", error: true);
        }
    }

    private void InsertItem()
    {
        Flush();
        SelectedContentToInsert = _sel?.Content ?? "";
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CloseNative() { Close(); }

    private void OpenFolderNative() => OpenFolder();
}
