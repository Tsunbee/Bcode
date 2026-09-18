namespace Bcode.App.Forms;

/// <summary>Small reusable single-line input dialog (stand-in for VB's InputBox).</summary>
public class SimplePromptForm : Bcode.App.UI.ThemedForm
{
    private readonly TextBox _textBox;

    private SimplePromptForm(string title, string prompt, string defaultValue)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        Width = 420;
        Height = 140;

        var label = new Label { Text = prompt, Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 8, 8, 0) };
        _textBox = new TextBox { Dock = DockStyle.Top, Text = defaultValue, Margin = new Padding(8) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(_textBox);
        Controls.Add(buttons);
        Controls.Add(label);
    }

    public static string? Show(IWin32Window owner, string title, string prompt, string defaultValue = "")
    {
        using var form = new SimplePromptForm(title, prompt, defaultValue);
        return form.ShowDialog(owner) == DialogResult.OK ? form._textBox.Text : null;
    }
}
