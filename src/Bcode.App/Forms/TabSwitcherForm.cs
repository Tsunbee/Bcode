using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Hộp chọn nhanh tab: hiện khi GIỮ phím Ctrl một lúc RỒI bấm Tab (bấm Ctrl+Tab ngay thì chuyển tab luôn, không hiện hộp) — xem MainForm.ShowTabSwitcher. Liệt kê mọi tab đang mở; chọn bằng chuột (bấm), bàn phím
/// (↑ ↓ / Tab / Shift+Tab rồi Enter hoặc thả Ctrl) — Esc hoặc bấm ra ngoài thì thôi. Chỉ trả về vị trí tab được chọn, việc chuyển tab do MainForm làm.
/// </summary>
public class TabSwitcherForm : DpiForm
{
    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28,
    };
    private readonly int _current;
    private bool _usedTab; // đã dùng Tab/mũi tên khi đang giữ Ctrl → thả Ctrl là chọn luôn (giống Ctrl+Tab của Visual Studio)

    /// <summary>Vị trí tab được chọn; -1 nếu huỷ.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <param name="initialStep">Bước đi ngay khi mở (+1 = tab kế, -1 = tab trước; 0 = đứng ở tab hiện tại) — mở bằng "giữ Ctrl rồi bấm Tab" thì đã đi một bước, thả Ctrl là chọn.</param>
    public TabSwitcherForm(IReadOnlyList<string> tabs, int currentIndex, int initialStep = 0)
    {
        _current = currentIndex;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = AppColors.Border;
        Padding = new Padding(1); // viền 1px

        var header = new Label
        {
            Text = "Chuyển tab  —  ↑ ↓ hoặc Tab để chọn, Enter hoặc bấm chuột để đi, Esc để đóng",
            Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 8, 0),
            BackColor = AppColors.PanelAlt, ForeColor = AppColors.TextMuted, AutoEllipsis = true,
        };
        _list.BackColor = AppColors.Panel;
        _list.ForeColor = AppColors.Text;
        _list.Font = ThemeManager.BaseFont;
        foreach (var t in tabs) _list.Items.Add(t);
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Clamp(currentIndex, 0, _list.Items.Count - 1);
        if (initialStep != 0) _list.SelectedIndex = (Math.Clamp(currentIndex, 0, _list.Items.Count - 1) + initialStep + _list.Items.Count) % _list.Items.Count;   // đứng ở tab kế / trước; _usedTab giữ false

        _list.DrawItem += DrawRow;
        _list.MouseMove += (_, e) =>
        {
            var i = _list.IndexFromPoint(e.Location);
            if (i >= 0 && i != _list.SelectedIndex) _list.SelectedIndex = i; // rê chuột tới đâu sáng tới đó
        };
        _list.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && _list.IndexFromPoint(e.Location) >= 0) Accept();
        };

        Controls.Add(_list);
        Controls.Add(header);

        var rows = Math.Clamp(Math.Max(1, tabs.Count), 1, 14);
        ClientSize = new Size(460, 30 + rows * _list.ItemHeight + 2);
        Deactivate += (_, _) => { if (SelectedIndex < 0) Close(); };
    }

    /// <summary>Đặt hộp ở giữa cửa sổ <paramref name="owner"/>.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner is { } o)
            Location = new Point(o.Left + (o.Width - Width) / 2, o.Top + Math.Max(40, (o.Height - Height) / 3));
        _list.Focus();
    }

    private void DrawRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? AppColors.Accent : AppColors.Panel))
            e.Graphics.FillRectangle(bg, e.Bounds);

        var isCurrent = e.Index == _current;
        var font = isCurrent ? new Font(_list.Font, FontStyle.Bold) : _list.Font;
        try
        {
            var fore = selected ? AppColors.OnAccent : AppColors.Text;
            var num = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, 28, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, (e.Index + 1).ToString(), _list.Font, num, selected ? AppColors.OnAccent : AppColors.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            var text = new Rectangle(e.Bounds.X + 38, e.Bounds.Y, e.Bounds.Width - 46, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, _list.Items[e.Index]?.ToString() ?? "", font, text, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (isCurrent) // dấu chấm: tab đang mở
            {
                using var dot = new SolidBrush(selected ? AppColors.OnAccent : AppColors.Accent);
                e.Graphics.FillEllipse(dot, e.Bounds.Right - 16, e.Bounds.Y + (e.Bounds.Height - 7) / 2, 7, 7);
            }
        }
        finally { if (isCurrent) font.Dispose(); }
    }

    private void Accept()
    {
        SelectedIndex = _list.SelectedIndex;
        Close();
    }

    private void Move(int step)
    {
        if (_list.Items.Count == 0) return;
        _usedTab = true;
        _list.SelectedIndex = (_list.SelectedIndex + step + _list.Items.Count) % _list.Items.Count;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Tab: case Keys.Control | Keys.Tab: case Keys.Down: Move(1); return true;
            case Keys.Shift | Keys.Tab: case Keys.Control | Keys.Shift | Keys.Tab: case Keys.Up: Move(-1); return true;
            case Keys.Enter: Accept(); return true;
            case Keys.Escape: Close(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.ControlKey && _usedTab) Accept();
    }
}
