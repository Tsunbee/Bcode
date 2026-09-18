using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Note" / "Note (New)" tool — small per-workspace scratch notes saved to
/// disk (see NoteService). One NoteControl instance edits one note at a
/// time; "Note (New)" in MainForm opens a fresh NoteControl with a new,
/// not-yet-used name, "Note" reopens/creates the default one.
/// </summary>
public class NoteControl : UserControl
{
    private readonly ComboBox _nameCombo;
    private readonly Button _saveButton;
    private readonly Label _statusLabel;
    private readonly TextBox _textBox;
    private readonly NoteService _service;
    private readonly string _workspaceName;
    private bool _dirty;

    public string NoteName => _nameCombo.Text.Trim();

    public NoteControl(NoteService service, string workspaceName, string noteName)
    {
        _service = service;
        _workspaceName = workspaceName;
        Dock = DockStyle.Fill;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, WrapContents = false, Padding = new Padding(4, 4, 0, 0) };
        _nameCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 220, Text = noteName };
        _nameCombo.Items.AddRange(_service.ListNotes(_workspaceName).Cast<object>().ToArray());
        _nameCombo.SelectedIndexChanged += (_, _) => LoadCurrent();
        var loadBtn = new Button { Text = "Mở" };
        loadBtn.Click += (_, _) => LoadCurrent();
        _saveButton = new Button { Text = "💾 Save" };
        _saveButton.Click += (_, _) => SaveCurrent();

        top.Controls.Add(new Label { Text = "Note:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
        top.Controls.Add(_nameCombo);
        top.Controls.Add(loadBtn);
        top.Controls.Add(_saveButton);

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };

        _textBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            Font = new Font("Segoe UI", 10f),
            AcceptsTab = true,
            AcceptsReturn = true
        };
        _textBox.TextChanged += (_, _) => { _dirty = true; _statusLabel.Text = "Chưa lưu..."; };

        Controls.Add(_textBox);
        Controls.Add(_statusLabel);
        Controls.Add(top);

        LoadCurrent();
    }

    private void LoadCurrent()
    {
        var name = NoteName;
        if (string.IsNullOrWhiteSpace(name)) return;
        _textBox.Text = _service.LoadNote(_workspaceName, name);
        _dirty = false;
        _statusLabel.Text = "";
    }

    private void SaveCurrent()
    {
        var name = NoteName;
        if (string.IsNullOrWhiteSpace(name))
        {
            _statusLabel.Text = "Nhập tên note trước khi lưu.";
            return;
        }
        _service.SaveNote(_workspaceName, name, _textBox.Text);
        if (!_nameCombo.Items.Contains(name)) _nameCombo.Items.Add(name);
        _dirty = false;
        _statusLabel.ForeColor = Color.DarkGreen;
        _statusLabel.Text = "Đã lưu.";
    }

    /// <summary>Whether there are unsaved edits — MainForm's tab-close confirm reuses this
    /// the same way it does for ScriptEditorControl.IsDirty.</summary>
    public bool IsDirty => _dirty;
}
