using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>"String Beauty" tool: reformats a pasted SQL block with clause-based line breaks/indent.</summary>
public class StringBeautyForm : Bcode.App.UI.ThemedForm
{
    private readonly TextBox _input;
    private readonly TextBox _output;
    private readonly SqlFormatterService _formatter = new();

    public StringBeautyForm()
    {
        Text = "String Beauty";
        Width = 900;
        Height = 650;
        StartPosition = FormStartPosition.CenterParent;

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        _input = MakeBox();
        _output = MakeBox();
        _output.ReadOnly = true;
        split.Panel1.Controls.Add(_input);
        split.Panel2.Controls.Add(_output);

        var runBtn = new Button { Text = "Beautify →", Dock = DockStyle.Bottom, Height = 32 };
        runBtn.Click += (_, _) => _output.Text = _formatter.Format(_input.Text);

        Controls.Add(split);
        Controls.Add(runBtn);
    }

    private static TextBox MakeBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        Font = new Font("Consolas", 10f),
        WordWrap = false
    };
}
