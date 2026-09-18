using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>"Library" tool: browse/edit/insert reusable SQL/JS/HTML snippets.</summary>
public class LibrarySnippetForm : Bcode.App.UI.ThemedForm
{
    private readonly SnippetLibraryService _service;
    private readonly ListBox _list;
    private readonly TextBox _nameBox, _categoryBox;
    private readonly TextBox _contentBox;

    public string? SelectedContentToInsert { get; private set; }

    public LibrarySnippetForm(SnippetLibraryService service)
    {
        _service = service;
        Text = "Library (Snippets)";
        Width = 800;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;

        _list = new ListBox { Dock = DockStyle.Left, Width = 220 };
        _list.SelectedIndexChanged += (_, _) => LoadSelected();
        RefreshList();

        var listButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 34 };
        var addBtn = new Button { Text = "+ New" };
        var delBtn = new Button { Text = "Delete" };
        addBtn.Click += (_, _) => AddNew();
        delBtn.Click += (_, _) => DeleteSelected();
        listButtons.Controls.Add(addBtn);
        listButtons.Controls.Add(delBtn);

        var leftPanel = new Panel { Dock = DockStyle.Left, Width = 220 };
        leftPanel.Controls.Add(_list);
        leftPanel.Controls.Add(listButtons);
        _list.Dock = DockStyle.Fill;

        var right = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 2, Padding = new Padding(8) };
        right.Controls.Add(new Label { Text = "Name", AutoSize = true }, 0, 0);
        _nameBox = new TextBox { Width = 300 };
        right.Controls.Add(_nameBox, 1, 0);
        right.Controls.Add(new Label { Text = "Category", AutoSize = true }, 0, 1);
        _categoryBox = new TextBox { Width = 300 };
        right.Controls.Add(_categoryBox, 1, 1);

        _contentBox = new TextBox { Multiline = true, ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 10f), Dock = DockStyle.Fill };
        var contentPanel = new Panel { Dock = DockStyle.Fill };
        contentPanel.Controls.Add(_contentBox);
        right.Controls.Add(contentPanel, 0, 3);
        right.SetColumnSpan(contentPanel, 2);
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var bottomButtons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        var insertBtn = new Button { Text = "Insert vào Script Editor" };
        insertBtn.Click += (_, _) => { SelectedContentToInsert = _contentBox.Text; DialogResult = DialogResult.OK; Close(); };
        var saveBtn = new Button { Text = "Save" };
        saveBtn.Click += (_, _) => { SaveCurrentEdit(); _service.Save(); };
        bottomButtons.Controls.Add(insertBtn);
        bottomButtons.Controls.Add(saveBtn);

        var rightPanel = new Panel { Dock = DockStyle.Fill };
        rightPanel.Controls.Add(right);
        rightPanel.Controls.Add(bottomButtons);

        Controls.Add(rightPanel);
        Controls.Add(leftPanel);

        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void RefreshList()
    {
        _list.Items.Clear();
        foreach (var s in _service.Snippets) _list.Items.Add(s);
    }

    private Snippet? Selected => _list.SelectedItem as Snippet;

    private void LoadSelected()
    {
        if (Selected is not { } s) return;
        _nameBox.Text = s.Name;
        _categoryBox.Text = s.Category;
        _contentBox.Text = s.Content;
    }

    private void SaveCurrentEdit()
    {
        if (Selected is not { } s) return;
        s.Name = _nameBox.Text.Trim();
        s.Category = string.IsNullOrWhiteSpace(_categoryBox.Text) ? "General" : _categoryBox.Text.Trim();
        s.Content = _contentBox.Text;
        var idx = _list.SelectedIndex;
        RefreshList();
        _list.SelectedIndex = idx;
    }

    private void AddNew()
    {
        var s = new Snippet { Name = "New Snippet" };
        _service.Snippets.Add(s);
        RefreshList();
        _list.SelectedItem = s;
    }

    private void DeleteSelected()
    {
        if (Selected is not { } s) return;
        _service.Snippets.Remove(s);
        RefreshList();
    }
}
