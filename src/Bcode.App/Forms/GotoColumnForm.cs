using Bcode.App.Controls;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>Hộp "Goto Column" (Ctrl+G) có gợi ý: gõ tới đâu danh sách bên dưới lọc các cột đang có trong lưới tới đó (tên bắt đầu bằng chữ gõ lên trước, rồi tên chứa chữ đó);
/// ↑/↓ chọn, Tab điền tên cột đang chọn, Enter đi tới, Esc đóng, nhấp đúp một dòng cũng đi tới. Bố cục Dock nên tự co giãn theo màn hình/DPI như các hộp thoại khác.</summary>
public sealed class GotoColumnForm : ThemedForm
{
    private readonly TextBox _box = new() { Dock = DockStyle.Top };
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly List<string> _names;

    private GotoColumnForm(IEnumerable<string> names)
    {
        _names = names.ToList();
        Text = "Goto Column";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        Width = 460;
        Height = 380;

        var label = new Label { Text = "Tên cột (hoặc số thứ tự cột, bắt đầu từ 1):", Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 8, 8, 0) };
        var boxHost = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8, 4, 8, 4) };
        boxHost.Controls.Add(_box);
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 8) };
        listHost.Controls.Add(_list);
        Controls.Add(listHost);
        Controls.Add(boxHost);
        Controls.Add(label);

        _box.TextChanged += (_, _) => Refill();
        _box.KeyDown += OnBoxKey;
        _list.DoubleClick += (_, _) => { if (_list.SelectedItem is string s) { _box.Text = s; Accept(); } };
        Shown += (_, _) => { _box.Focus(); };
        Refill();
    }

    private void Refill()
    {
        var q = _box.Text.Trim();
        var hits = _names
            .Select((n, i) => (n, i))
            .Where(x => q.Length == 0 || x.n.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => !x.n.StartsWith(q, StringComparison.OrdinalIgnoreCase)).ThenBy(x => x.i)
            .Select(x => x.n).ToList();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var h in hits) _list.Items.Add(h);
        _list.EndUpdate();
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
    }

    private void Accept() { DialogResult = DialogResult.OK; Close(); }

    private void OnBoxKey(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Down: e.Handled = true; e.SuppressKeyPress = true; if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1); break;
            case Keys.Up: e.Handled = true; e.SuppressKeyPress = true; if (_list.Items.Count > 0) _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1); break;
            case Keys.Tab:
                e.Handled = true; e.SuppressKeyPress = true;
                if (_list.SelectedItem is string t) { _box.Text = t; _box.SelectionStart = _box.TextLength; }
                break;
            case Keys.Enter:
                e.Handled = true; e.SuppressKeyPress = true;
                // Chưa gõ gì / đang chọn 1 gợi ý → lấy gợi ý đang chọn; gõ số thứ tự cột thì giữ nguyên số.
                if (_list.SelectedItem is string s && !int.TryParse(_box.Text.Trim(), out _)) _box.Text = s;
                Accept();
                break;
            case Keys.Escape: e.Handled = true; DialogResult = DialogResult.Cancel; Close(); break;
        }
    }

    /// <summary>Hiện hộp thoại; trả về chuỗi người dùng chọn/gõ (tên cột hoặc số thứ tự), null nếu huỷ.</summary>
    public static string? Show(IWin32Window owner, IEnumerable<string> columnNames)
    {
        using var form = new GotoColumnForm(columnNames);
        return form.ShowDialog(owner) == DialogResult.OK ? form._box.Text : null;
    }
}
