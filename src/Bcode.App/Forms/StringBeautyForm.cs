using Bcode.App.Controls;
using Bcode.App.UI;
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

        var actions = new WebActionBar { DefaultActionId = "run", CancelActionId = "close" };
        actions.Add("copy", "Copy kết quả", WebActionKind.Normal, left: true)
               .Add("close", "Đóng", WebActionKind.Quiet)
               .Add("run", "Beautify →", WebActionKind.Primary);
        actions.Invoked += id =>
        {
            switch (id)
            {
                case "run": _output.Text = _formatter.Format(_input.Text); break;
                case "copy":
                    if (_output.TextLength > 0) Clipboard.SetText(_output.Text);
                    actions.SetStatus(_output.TextLength > 0 ? "Đã copy kết quả." : "Chưa có kết quả để copy.", ok: _output.TextLength > 0);
                    break;
                case "close": Close(); break;
            }
        };

        Controls.Add(split);
        Controls.Add(actions);
    }

    private static TextBox MakeBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        Font = ThemeManager.MonoFont,
        WordWrap = false
    };
}
