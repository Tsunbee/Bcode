using Bcode.App.Controls;
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

        // Button row is HTML/CSS (see Controls/WebActionBar.cs) — "Hiện tất cả" is a
        // secondary action and now sits on the left, away from the OK/Cancel pair, instead
        // of being one of three identical-looking buttons crammed together on the right.
        var actions = new WebActionBar { DefaultActionId = "ok", CancelActionId = "cancel" };
        actions.Add("show-all", "Hiện tất cả", WebActionKind.Quiet, left: true)
               .Add("cancel", "Cancel", WebActionKind.Quiet)
               .Add("ok", "OK", WebActionKind.Primary);
        actions.Invoked += id =>
        {
            switch (id)
            {
                case "show-all":
                    for (var i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true);
                    break;
                case "cancel":
                    DialogResult = DialogResult.Cancel;
                    Close();
                    break;
                case "ok":
                    HiddenKeys.Clear();
                    for (var i = 0; i < _list.Items.Count; i++)
                        if (!_list.GetItemChecked(i)) HiddenKeys.Add(_keys[i]);
                    DialogResult = DialogResult.OK;
                    Close();
                    break;
            }
        };

        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(actions);
    }

    private readonly List<string> _keys;
}
