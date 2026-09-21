using Bcode.App.Controls;
using Bcode.App.UI;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>"Compare Text" tool: side-by-side diff of two arbitrary text blocks/scripts.</summary>
public class CompareTextForm : Bcode.App.UI.ThemedForm
{
    private readonly TextBox _left, _right;
    private readonly RichTextBox _result;
    private readonly DiffService _diff = new();

    public CompareTextForm()
    {
        Text = "Compare Text";
        Width = 1100;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 260 };

        var topSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        _left = MakeBox();
        _right = MakeBox();
        topSplit.Panel1.Controls.Add(WithLabel(_left, "Bên trái (cũ)"));
        topSplit.Panel2.Controls.Add(WithLabel(_right, "Bên phải (mới)"));

        var bottom = new Panel { Dock = DockStyle.Fill };
        _result = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = ThemeManager.MonoFont, WordWrap = false };
        // Compare moved out of the middle of the window (it used to be a full-width strip
        // wedged between the two input panes and the result) down to the window's own action
        // bar, where every other tool's primary action lives.
        var actions = new WebActionBar { DefaultActionId = "run", CancelActionId = "close" };
        actions.Add("close", "Đóng", WebActionKind.Quiet)
               .Add("run", "Compare", WebActionKind.Primary);
        actions.Invoked += id =>
        {
            if (id == "run") RunCompare();
            else Close();
        };
        bottom.Controls.Add(_result);

        split.Panel1.Controls.Add(topSplit);
        split.Panel2.Controls.Add(bottom);
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

    private static Control WithLabel(Control inner, string label)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var lbl = new Label { Text = label, Dock = DockStyle.Top, Height = 20 };
        panel.Controls.Add(inner);
        panel.Controls.Add(lbl);
        return panel;
    }

    private void RunCompare()
    {
        _result.Clear();
        foreach (var line in _diff.Diff(_left.Text, _right.Text))
        {
            var color = line.Kind switch
            {
                DiffKind.Added => Color.DarkGreen,
                DiffKind.Removed => Color.Firebrick,
                _ => Color.Black
            };
            var prefix = line.Kind switch
            {
                DiffKind.Added => "+ ",
                DiffKind.Removed => "- ",
                _ => "  "
            };

            _result.SelectionStart = _result.TextLength;
            _result.SelectionColor = color;
            _result.AppendText(prefix + line.Text + Environment.NewLine);
        }
    }
}
