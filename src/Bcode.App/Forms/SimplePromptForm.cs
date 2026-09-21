using Bcode.App.Controls;
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
        Height = 180;

        var label = new Label { Text = prompt, Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 8, 8, 0) };
        _textBox = new TextBox { Dock = DockStyle.Top, Text = defaultValue, Margin = new Padding(8) };
        // HTML/CSS button row (Controls/WebActionBar.cs) — Enter/Esc still work, the bar
        // re-creates what AcceptButton/CancelButton used to give a real WinForms Button.
        var buttons = new WebActionBar { DefaultActionId = "ok", CancelActionId = "cancel" };
        buttons.Add("cancel", "Cancel", WebActionKind.Quiet)
               .Add("ok", "OK", WebActionKind.Primary);
        buttons.Invoked += id =>
        {
            DialogResult = id == "ok" ? DialogResult.OK : DialogResult.Cancel;
            Close();
        };

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
