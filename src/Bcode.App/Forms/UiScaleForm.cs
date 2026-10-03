using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>Chọn tỉ lệ giao diện: Tự động (theo cỡ màn hình) hoặc một mức cố định.</summary>
public class UiScaleForm : ThemedForm
{
    private readonly ComboBox _combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };

    public string SelectedMode => _combo.SelectedIndex <= 0 ? UiScale.Auto : UiScale.Options[_combo.SelectedIndex];

    public UiScaleForm(string currentMode)
    {
        Text = "Tỉ lệ giao diện";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = MaximizeBox = false;
        ClientSize = new Size(420, 170);

        foreach (var o in UiScale.Options)
            _combo.Items.Add(o == UiScale.Auto ? $"Tự động (đang dùng {UiScale.Factor:P0})" : o + "%");
        var idx = Array.FindIndex(UiScale.Options, o => o.Equals(currentMode, StringComparison.OrdinalIgnoreCase));
        _combo.SelectedIndex = idx < 0 ? 0 : idx;

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 62, Padding = new Padding(0, 8, 0, 0),
            Text = "Tự động: chữ và nội dung co giãn để vừa khung màn hình hiện tại (khung chuẩn 1600×900). " +
                   "Chọn một mức cố định nếu muốn tự điều chỉnh."
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Width = 90, Height = 30 };
        var cancel = new Button { Text = "Hủy", DialogResult = DialogResult.Cancel, Width = 90, Height = 30 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 44, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        AcceptButton = ok;
        CancelButton = cancel;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14) };
        body.Controls.Add(hint);
        body.Controls.Add(_combo);
        Controls.Add(body);
        Controls.Add(buttons);
    }
}
