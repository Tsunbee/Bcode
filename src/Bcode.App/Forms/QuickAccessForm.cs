using Bcode.App.Models;

using Bcode.App.UI;
namespace Bcode.App.Forms;

/// <summary>
/// "Quick Access" customizer — lets the user hide tools they don't use so the
/// Tools toolbar doesn't keep growing every time a new tool is added.
/// </summary>
public class QuickAccessForm : ThemedForm
{
    private readonly CheckedListBox _list;
    public HashSet<string> HiddenKeys { get; }

    public QuickAccessForm(IEnumerable<(string key, string label)> allTools, HashSet<string> hiddenKeys)
    {
        HiddenKeys = new HashSet<string>(hiddenKeys);

        Text = "Quick Access — chọn tính năng hiển thị trên toolbar";
        Width = 420;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8),
            Text = "Bỏ chọn để ẩn bớt nút khỏi toolbar (vẫn có thể bật lại bất cứ lúc nào)."
        };

        _list = new CheckedListBox { Dock = DockStyle.Fill, CheckOnClick = true };
        foreach (var (key, label) in allTools)
        {
            var index = _list.Items.Add(label);
            _list.SetItemChecked(index, !HiddenKeys.Contains(key));
        }
        _keys = allTools.Select(t => t.key).ToList();

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft };
        var okBtn = new Button { Text = "OK" };
        okBtn.Click += (_, _) =>
        {
            HiddenKeys.Clear();
            for (var i = 0; i < _list.Items.Count; i++)
                if (!_list.GetItemChecked(i)) HiddenKeys.Add(_keys[i]);
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancelBtn = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
        var showAllBtn = new Button { Text = "Hiện tất cả" };
        showAllBtn.Click += (_, _) =>
        {
            for (var i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true);
        };
        buttons.Controls.Add(okBtn);
        buttons.Controls.Add(cancelBtn);
        buttons.Controls.Add(showAllBtn);

        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(buttons);

        AcceptButton = okBtn;
        CancelButton = cancelBtn;
    }

    private readonly List<string> _keys;
}
