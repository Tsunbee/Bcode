using Bcode.App.Models;

namespace Bcode.App.Forms;

/// <summary>"Chọn store/function để debug" — lists every EXEC/function call
/// DebugTargetScanner found in the current script, so "Debug store/function" can jump
/// straight to debugging whichever one the user actually meant instead of guessing from a
/// click position. Matches FCode's own picker dialog (same title, same "Dòng N [tag] name —
/// call text" row shape).</summary>
public class ChooseDebugTargetForm : Bcode.App.UI.ThemedForm
{
    private readonly ListView _list;

    public DebugCandidate? Selected { get; private set; }

    public ChooseDebugTargetForm(List<DebugCandidate> candidates)
    {
        Text = "Chọn store/function để debug";
        Width = 760;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;

        var header = new Label
        {
            Text = "Query có ứng viên (EXEC store / gọi function) — chọn để debug:",
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(8, 6, 0, 0)
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
        };
        _list.Columns.Add("Dòng", 70);
        _list.Columns.Add("Loại", 60);
        _list.Columns.Add("Đối tượng", 220);
        _list.Columns.Add("Câu gọi", 380);
        _list.DoubleClick += (_, _) => Accept();

        foreach (var c in candidates)
        {
            var tag = c.Target.Kind == SqlObjectKind.Function ? "f" : "sp";
            var item = new ListViewItem(new[] { $"Dòng {c.Line}", tag, c.Target.QualifiedName, c.CallText })
            {
                Tag = c
            };
            _list.Items.Add(item);
        }
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
        var okBtn = new Button { Text = "Debug", DialogResult = DialogResult.OK };
        var cancelBtn = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel };
        okBtn.Click += (_, _) => Accept();
        buttons.Controls.Add(cancelBtn);
        buttons.Controls.Add(okBtn);

        Controls.Add(_list);
        Controls.Add(buttons);
        Controls.Add(header);
        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }

    private void Accept()
    {
        if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is DebugCandidate c)
        {
            Selected = c;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
