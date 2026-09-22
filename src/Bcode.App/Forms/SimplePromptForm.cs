using Bcode.App.Controls;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>Small reusable single-line input dialog (stand-in for VB's InputBox).</summary>
public class SimplePromptForm : ThemedForm
{
    private readonly TextBox _textBox;

    private SimplePromptForm(string title, string prompt, string defaultValue)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        Width = 460;
        Height = 190;

        var label = new Label { Text = prompt, Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 8, 8, 0) };
        _textBox = new TextBox { Dock = DockStyle.Top, Text = defaultValue, Margin = new Padding(8) };
        _textBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                DialogResult = DialogResult.OK;
                Close();
            }
            else if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };

        // Footer buttons sử dụng Native Panel + PillButton tránh lỗi load trễ của WebView2
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(8) };
        var rightFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        var cancelBtn = PillButton.Flat("Cancel");
        cancelBtn.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        var okBtn = PillButton.Flat("OK", primary: true);
        okBtn.Click += (_, _) =>
        {
            DialogResult = DialogResult.OK;
            Close();
        };

        rightFlow.Controls.Add(cancelBtn);
        rightFlow.Controls.Add(okBtn);
        bottomBar.Controls.Add(rightFlow);

        Controls.Add(_textBox);
        Controls.Add(bottomBar);
        Controls.Add(label);

        AcceptButton = null;
        CancelButton = null;
    }

    public static string? Show(IWin32Window owner, string title, string prompt, string defaultValue = "")
    {
        using var form = new SimplePromptForm(title, prompt, defaultValue);
        return form.ShowDialog(owner) == DialogResult.OK ? form._textBox.Text : null;
    }
}