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
        var runBtn = new Button { Text = "Compare", Dock = DockStyle.Top, Height = 30 };
        _result = new RichTextBox { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 10f), WordWrap = false };
        runBtn.Click += (_, _) => RunCompare();
        bottom.Controls.Add(_result);
        bottom.Controls.Add(runBtn);

        split.Panel1.Controls.Add(topSplit);
        split.Panel2.Controls.Add(bottom);
        Controls.Add(split);
    }

    private static TextBox MakeBox() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        Font = new Font("Consolas", 10f),
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
