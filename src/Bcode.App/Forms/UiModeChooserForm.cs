using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Hộp chọn hiện lúc mở Bcode: dạng App (WinForms thuần, ít RAM) hay Web (WebView2, như trước). Chỉ dùng control WinForms —
/// không khởi động WebView2 để hộp này không tự tốn RAM của chế độ mà nó đang hỏi. Chọn xong lưu lại; tick "Hỏi lại mỗi lần mở" để lần sau vẫn hỏi.
/// </summary>
public sealed class UiModeChooserForm : ThemedForm
{
    private readonly CheckBox _ask = new() { Text = "Hỏi lại mỗi lần mở Bcode (bỏ tick = vào thẳng chế độ đã chọn; đổi lại ở Settings)", AutoSize = true };
    public UiModeKind Chosen { get; private set; }
    public bool AskNextTime => _ask.Checked;

    public UiModeChooserForm(UiModeKind preselect, bool ask)
    {
        Text = "Bcode — chọn chế độ giao diện";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = MinimizeBox = false;
        ShowIcon = false;
        ClientSize = new Size(720, 330);
        _ask.Checked = ask;
        if (AppIcons.AppIcon is { } icon) { Icon = icon; ShowIcon = true; }

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(16) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var title = new Label { Text = "Bạn muốn dùng Bcode ở chế độ nào?", AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        grid.Controls.Add(title, 0, 0); grid.SetColumnSpan(title, 2);
        grid.Controls.Add(Card(UiModeKind.Native, "App / WinForms",
            "Giao diện ứng dụng Windows.\r\n\r\n• Nhẹ, mở nhanh, dùng ít bộ nhớ", preselect), 0, 1);
        grid.Controls.Add(Card(UiModeKind.Web, "Web / WebView2",
            "Giao diện dạng trang web.\r\n\r\n• Đầy đủ tính năng nhất", preselect), 1, 1);
        _ask.Margin = new Padding(0, 10, 0, 4);
        grid.Controls.Add(_ask, 0, 2); grid.SetColumnSpan(_ask, 2);
        var hint = new Label { Text = "Mẹo: chạy Bcode.exe --native hoặc --web để vào thẳng một chế độ, không hỏi.", AutoSize = true };
        grid.Controls.Add(hint, 0, 3); grid.SetColumnSpan(hint, 2);
        Controls.Add(grid);
    }

    /// <summary>Thẻ bấm được (Panel + Label, không dùng Button vì ThemeManager tự co Button theo chữ): bấm đâu trong thẻ cũng chọn.</summary>
    private Control Card(UiModeKind kind, string head, string body, UiModeKind preselect)
    {
        var card = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 10, 0), Padding = new Padding(2), Cursor = Cursors.Hand, BackColor = AppColors.Border };
        var text = new Label
        {
            Dock = DockStyle.Fill, Padding = new Padding(12, 10, 12, 10), Cursor = Cursors.Hand, BackColor = AppColors.PanelAlt, ForeColor = AppColors.Text,
            Text = head + (kind == preselect ? "   (lần trước)" : "") + "\r\n\r\n" + body,
        };
        card.Controls.Add(text);
        void Pick(object? s, EventArgs e) { Chosen = kind; DialogResult = DialogResult.OK; Close(); }
        card.Click += Pick; text.Click += Pick;
        text.MouseEnter += (_, _) => text.BackColor = AppColors.Selection;
        text.MouseLeave += (_, _) => text.BackColor = AppColors.PanelAlt;
        return card;
    }
}
