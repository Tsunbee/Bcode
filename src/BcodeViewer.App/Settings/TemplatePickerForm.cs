using BcodeViewer.App.UI;

namespace BcodeViewer.App.Settings;

/// <summary>Picker for File &gt; New from Template — the list on the left, the template's
/// actual content on the right. The preview is the point: these files differ by a few lines
/// buried in a hundred, and a name alone ("Mau - Dir Controller") is not enough to tell two
/// of them apart at the moment of choosing.</summary>
public class TemplatePickerForm : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false, BorderStyle = BorderStyle.None };
    private readonly TextBox _preview = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false,
        ScrollBars = ScrollBars.Both, Font = new Font("Consolas", 9),
    };
    private readonly List<FileTemplate> _templates;

    public FileTemplate? Selected { get; private set; }

    public TemplatePickerForm(List<FileTemplate> templates)
    {
        _templates = templates;
        Text = "New from Template";
        Width = 900;
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 260, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(_preview);

        foreach (var t in templates) _list.Items.Add(t.IsShared ? $"{t.Name}   (dùng chung)" : t.Name);
        _list.SelectedIndexChanged += (_, _) => ShowPreview();
        _list.DoubleClick += (_, _) => Accept();

        var buttonRow = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 44, Padding = new Padding(8) };
        var okButton = new Button { Text = "Tạo file", Width = 100 };
        var cancelButton = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        var folderButton = new Button { Text = "Mở thư mục template", Width = 160 };
        okButton.Click += (_, _) => Accept();
        folderButton.Click += (_, _) =>
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{FileTemplateStore.PersonalFolder}\""); }
            catch { /* Explorer unavailable — nothing worth interrupting the user over */ }
        };
        buttonRow.Controls.Add(cancelButton);
        buttonRow.Controls.Add(okButton);
        buttonRow.Controls.Add(folderButton);

        Controls.Add(split);
        Controls.Add(buttonRow);
        CancelButton = cancelButton;

        ThemeManager.Apply(this);
        _preview.BackColor = AppColors.Panel;
        okButton.BackColor = AppColors.Accent;

        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void ShowPreview()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _templates.Count) { _preview.Text = ""; return; }
        // Placeholders are shown unrendered — the target path isn't chosen yet, and seeing
        // ${Name} in the preview is how anyone learns the placeholders exist at all.
        _preview.Text = _templates[_list.SelectedIndex].ReadContent().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
    }

    private void Accept()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _templates.Count) return;
        Selected = _templates[_list.SelectedIndex];
        DialogResult = DialogResult.OK;
        Close();
    }
}
