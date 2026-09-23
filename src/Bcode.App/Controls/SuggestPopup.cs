using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Danh sách gợi ý nổi (autocomplete) hiện ngay dưới một ô nhập nằm trong thanh công cụ
/// WebView2 — ví dụ ô "Table" của TableEditControl. Không vẽ gợi ý bằng HTML được vì mỗi thanh
/// WebView2 chỉ cao ~40px: mọi thứ tràn xuống dưới đều bị cắt mất. Đây là một cửa sổ native
/// riêng, KHÔNG BAO GIỜ nhận focus/kích hoạt (WS_EX_NOACTIVATE + chặn WM_MOUSEACTIVATE + ListBox
/// không tự SetFocus khi click), nên con trỏ vẫn nằm nguyên trong ô nhập để gõ tiếp; phím
/// ↑/↓/Enter/Esc do trang HTML bắt rồi chuyển sang C# gọi <see cref="MoveSelection"/> /
/// <see cref="SelectedText"/> / <see cref="HidePopup"/>.
/// </summary>
internal sealed class SuggestPopup : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const int MaxVisibleItems = 12;

    private readonly NoFocusListBox _list;

    /// <summary>Người dùng click chuột chọn một dòng gợi ý.</summary>
    public event Action<string>? Picked;

    public SuggestPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Padding = new Padding(1); // viền 1px = màu BackColor của form lộ ra quanh ListBox

        _list = new NoFocusListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            Font = ThemeManager.BaseFont,
        };
        _list.ItemHeight = TextRenderer.MeasureText("Ag", _list.Font).Height + 6;
        _list.DrawItem += DrawListItem;
        _list.ItemClicked += index =>
        {
            if (index >= 0 && index < _list.Items.Count)
                Picked?.Invoke((string)_list.Items[index]);
        };
        Controls.Add(_list);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    public bool IsOpen => Visible && _list.Items.Count > 0;

    public string? SelectedText => IsOpen && _list.SelectedIndex >= 0 ? (string)_list.SelectedItem! : null;

    /// <param name="screenLocation">Góc trên-trái của popup (toạ độ màn hình) — ngay dưới ô nhập.</param>
    /// <param name="minWidth">Tối thiểu rộng bằng ô nhập.</param>
    public void ShowItems(IWin32Window owner, Point screenLocation, int minWidth, IReadOnlyList<string> items)
    {
        if (items.Count == 0)
        {
            HidePopup();
            return;
        }

        BackColor = AppColors.Border;
        _list.BackColor = AppColors.Input;
        _list.ForeColor = AppColors.Text;

        _list.BeginUpdate();
        _list.Items.Clear();
        var widest = 0;
        foreach (var item in items)
        {
            _list.Items.Add(item);
            widest = Math.Max(widest, TextRenderer.MeasureText(item, _list.Font).Width);
        }
        _list.SelectedIndex = 0;
        _list.EndUpdate();

        var visibleRows = Math.Min(items.Count, MaxVisibleItems);
        var scrollbar = items.Count > MaxVisibleItems ? SystemInformation.VerticalScrollBarWidth : 0;
        var width = Math.Max(minWidth, widest + 20 + scrollbar);
        var height = visibleRows * _list.ItemHeight + Padding.Vertical;
        Bounds = new Rectangle(screenLocation, new Size(width, height));

        if (!Visible) Show(owner);
    }

    public void MoveSelection(int delta)
    {
        if (!IsOpen) return;
        var index = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
        _list.SelectedIndex = index;
    }

    public void HidePopup()
    {
        if (Visible) Hide();
    }

    private void DrawListItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _list.Items.Count) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? AppColors.Selection : AppColors.Input))
            e.Graphics.FillRectangle(back, e.Bounds);

        var textColor = selected && AppColors.IsDark ? Color.White : AppColors.Text;
        if (selected && !AppColors.IsDark) textColor = Color.Black;
        var textRect = new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, (string)_list.Items[e.Index], _list.Font, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    /// <summary>ListBox không tự lấy focus khi click (xử lý WM_LBUTTONDOWN thủ công, không gọi
    /// base) — nếu để mặc định, ListBox sẽ SetFocus vào chính nó, kéo focus ra khỏi ô nhập trong
    /// WebView2 và làm popup bị đóng ngay (ô nhập nhận sự kiện blur).</summary>
    private sealed class NoFocusListBox : ListBox
    {
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONDBLCLK = 0x0203;

        public event Action<int>? ItemClicked;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            if (m.Msg is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK)
            {
                var lp = m.LParam.ToInt64();
                var point = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
                var index = IndexFromPoint(point);
                if (index >= 0)
                {
                    SelectedIndex = index;
                    ItemClicked?.Invoke(index);
                }
                return;
            }
            base.WndProc(ref m);
        }
    }
}
