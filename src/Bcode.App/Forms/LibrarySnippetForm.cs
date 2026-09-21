using Bcode.App.Controls;
using Bcode.App.UI;
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

        var listButtons = new WebActionBar { Height = 46 };
        listButtons.Add("new", "+ New", WebActionKind.Normal, left: true)
                   .Add("delete", "Delete", WebActionKind.Danger, left: true);
        listButtons.Invoked += id =>
        {
            if (id == "new") AddNew();
            else if (id == "delete") DeleteSelected();
        };

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

        _contentBox = new TextBox { Multiline = true, ScrollBars = ScrollBars.Both, Font = ThemeManager.MonoFont, Dock = DockStyle.Fill };
        var contentPanel = new Panel { Dock = DockStyle.Fill };
        contentPanel.Controls.Add(_contentBox);
        right.Controls.Add(contentPanel, 0, 3);
        right.SetColumnSpan(contentPanel, 2);
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // Insert is what the user opened the Library for, so it is the primary action; Save
        // only persists the edit and now reports it inline instead of silently doing nothing
        // visible.
        var bottomButtons = new WebActionBar { DefaultActionId = "insert", CancelActionId = "close" };
        bottomButtons.Add("close", "Đóng", WebActionKind.Quiet)
                     .Add("save", "Save", WebActionKind.Normal)
                     .Add("insert", "Insert vào Script Editor", WebActionKind.Primary);
        bottomButtons.Invoked += id =>
        {
            switch (id)
            {
                case "insert":
                    SelectedContentToInsert = _contentBox.Text;
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
                case "save":
                    SaveCurrentEdit();
                    _service.Save();
                    bottomButtons.SetStatus("Đã lưu snippet.", ok: true);
                    break;
                case "close":
                    DialogResult = DialogResult.Cancel;
                    Close();
                    break;
            }
        };

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
