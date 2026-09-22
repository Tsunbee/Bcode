using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// "Quick Access" customizer — cho phép ẩn/hiện các tính năng trên toolbar.
/// </summary>
public class QuickAccessForm : ThemedForm
{
    private readonly CheckedListBox _list;
    private readonly List<string> _keys;
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
        var toolsList = allTools.ToList();
        foreach (var (key, label) in toolsList)
        {
            var index = _list.Items.Add(label);
            _list.SetItemChecked(index, !HiddenKeys.Contains(key));
        }
        _keys = toolsList.Select(t => t.key).ToList();

        // Sử dụng Panel native ở dưới đáy thay vì WebActionBar để đảm bảo hiện nút 100% không bị vùng đen
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8) };
        
        var showAllBtn = PillButton.Flat("Hiện tất cả");
        showAllBtn.Dock = DockStyle.Left;
        showAllBtn.Click += (_, _) => {
            for (var i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, true);
        };

        var rightButtonFlow = new FlowLayoutPanel 
        { 
            Dock = DockStyle.Right, 
            AutoSize = true, 
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };

        var cancelBtn = PillButton.Flat("Cancel");
        cancelBtn.Click += (_, _) => {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        var okBtn = PillButton.Flat("OK", primary: true);
        okBtn.Click += (_, _) => {
            HiddenKeys.Clear();
            for (var i = 0; i < _list.Items.Count; i++)
                if (!_list.GetItemChecked(i)) HiddenKeys.Add(_keys[i]);
            DialogResult = DialogResult.OK;
            Close();
        };

        rightButtonFlow.Controls.Add(cancelBtn);
        rightButtonFlow.Controls.Add(okBtn);

        bottomBar.Controls.Add(showAllBtn);
        bottomBar.Controls.Add(rightButtonFlow);

        Controls.Add(_list);
        Controls.Add(hint);
        Controls.Add(bottomBar);
        
        _list.BringToFront();
    }
}