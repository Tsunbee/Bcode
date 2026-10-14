using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Khung gợi ý code SQL của editor native: cửa sổ nổi KHÔNG nhận focus (con trỏ ở lại editor để gõ tiếp), mỗi dòng có ô loại (S mẫu / ƒ hàm / P procedure / ▫ cột...) +
/// tên + chi tiết mờ bên phải, phía dưới là xem trước (documentation) của dòng đang chọn — ví dụ nội dung mẫu sẽ chèn. Phím ↑↓/PgUp/PgDn/Tab/Enter/Esc do editor chuyển vào
/// (<see cref="MoveSelection"/>, <see cref="Selected"/>, <see cref="HidePopup"/>); chuột chọn bằng <see cref="Picked"/>.
/// </summary>
internal sealed class HintPopup : Form
{
    private const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    private const int MaxRows = 10;

    private readonly NoFocusList _list = new();
    private readonly Label _doc = new() { Dock = DockStyle.Bottom, Height = 0, Padding = new Padding(8, 5, 8, 5), AutoEllipsis = false };
    private List<HintItem> _items = new();

    public event Action<HintItem>? Picked;

    public HintPopup()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Padding = new Padding(1);
        _list.Dock = DockStyle.Fill;
        _list.BorderStyle = BorderStyle.None;
        _list.IntegralHeight = false;
        _list.DrawMode = DrawMode.OwnerDrawFixed;
        _list.Font = ThemeManager.BaseFont;
        _list.ItemHeight = TextRenderer.MeasureText("Ag", _list.Font).Height + 6;
        _list.DrawItem += Draw;
        _list.SelectedIndexChanged += (_, _) => UpdateDoc();
        _list.ItemClicked += i => { if (i >= 0 && i < _items.Count) Picked?.Invoke(_items[i]); };
        _doc.Font = new Font(ThemeManager.MonoFont.FontFamily, Math.Max(8f, ThemeManager.BaseFont.Size - 1f));
        Controls.Add(_list);
        Controls.Add(_doc);
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW; return cp; } }
    protected override void WndProc(ref Message m) { if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; } base.WndProc(ref m); }

    public bool IsOpen => Visible && _items.Count > 0;
    public HintItem? Selected => IsOpen && _list.SelectedIndex >= 0 && _list.SelectedIndex < _items.Count ? _items[_list.SelectedIndex] : null;

    /// <param name="caretTop">Toạ độ màn hình của đỉnh dòng chứa con trỏ; <paramref name="caretBottom"/> = đáy dòng (hiện ngay dưới, hoặc lật lên trên nếu hết chỗ).</param>
    public void ShowItems(IWin32Window owner, Point caretTop, int caretBottomY, IReadOnlyList<HintItem> items)
    {
        if (items.Count == 0) { HidePopup(); return; }
        _items = items.ToList();
        BackColor = AppColors.Border;
        _list.BackColor = AppColors.Input; _list.ForeColor = AppColors.Text;
        _doc.BackColor = AppColors.PanelAlt; _doc.ForeColor = AppColors.TextMuted;

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var it in _items) _list.Items.Add(it.Label);
        _list.SelectedIndex = 0;
        _list.EndUpdate();
        UpdateDoc();

        var rows = Math.Min(_items.Count, MaxRows);
        var scroll = _items.Count > MaxRows ? SystemInformation.VerticalScrollBarWidth : 0;
        var widest = _items.Take(60).Max(i => TextRenderer.MeasureText(i.Label, _list.Font).Width + TextRenderer.MeasureText(Short(i.Detail), _list.Font).Width);
        var width = Math.Clamp(widest + 90 + scroll, 380, 680);
        var listH = rows * _list.ItemHeight;
        var height = listH + _doc.Height + Padding.Vertical;
        var wa = Screen.FromPoint(caretTop).WorkingArea;
        var x = Math.Min(caretTop.X, wa.Right - width);
        var y = caretBottomY + 2;
        if (y + height > wa.Bottom) y = Math.Max(wa.Top, caretTop.Y - height - 2);
        Bounds = new Rectangle(new Point(Math.Max(wa.Left, x), y), new Size(width, height));
        if (!Visible) Show(owner);
    }

    private void UpdateDoc()
    {
        var sel = _list.SelectedIndex >= 0 && _list.SelectedIndex < _items.Count ? _items[_list.SelectedIndex] : null;
        var text = sel?.Doc ?? "";
        // documentation kiểu Markdown của trang web: bỏ rào ```sql và dấu `
        text = text.Replace("```sql", "").Replace("```", "").Replace("`", "").Trim();
        if (text.Length == 0) { _doc.Height = 0; _doc.Text = ""; }
        else
        {
            var lines = text.Split('\n');
            _doc.Text = string.Join("\n", lines.Take(7)) + (lines.Length > 7 ? "\n…" : "");
            _doc.Height = Math.Min(8, Math.Min(lines.Length, 7) + (lines.Length > 7 ? 1 : 0)) * (_doc.Font.Height + 1) + 12;
        }
        if (Visible)
        {
            var rows = Math.Min(_items.Count, MaxRows);
            Height = rows * _list.ItemHeight + _doc.Height + Padding.Vertical;
        }
    }

    public void MoveSelection(int delta)
    {
        if (!IsOpen) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + delta, 0, _list.Items.Count - 1);
    }

    public void HidePopup() { if (Visible) Hide(); _items = new(); }

    private static string Short(string s) { var i = s.IndexOf("  ·  ", StringComparison.Ordinal); return (i > 0 ? s[..i] : s).Trim(); }

    private static (string Glyph, Color Color) Icon(int kind) => kind switch
    {
        27 => ("S", AppColors.Accent), 1 => ("ƒ", AppColors.Success), 0 => ("P", AppColors.Success), 3 => ("▫", AppColors.TextMuted), 14 => ("C", AppColors.Warning),
        100 => ("K", AppColors.TextMuted), 101 => ("T", AppColors.Accent), _ => ("•", AppColors.TextMuted),
    };

    private void Draw(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _items.Count) return;
        var it = _items[e.Index];
        var sel = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(sel ? AppColors.Selection : AppColors.Input)) e.Graphics.FillRectangle(back, e.Bounds);
        var g = e.Graphics;
        var (glyph, color) = Icon(it.Kind);
        var box = new Rectangle(e.Bounds.X + 6, e.Bounds.Y + (e.Bounds.Height - 16) / 2, 16, 16);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var p = FlatToolStripRenderer.RoundedRect(box, 4)) { using var f = new SolidBrush(Color.FromArgb(40, color)); g.FillPath(f, p); }
        TextRenderer.DrawText(g, glyph, _list.Font, box, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        var detail = Short(it.Detail);
        var detailW = Math.Min(TextRenderer.MeasureText(detail, _list.Font).Width + 8, e.Bounds.Width / 2);
        var nameRect = new Rectangle(box.Right + 6, e.Bounds.Y, e.Bounds.Width - box.Width - 20 - detailW, e.Bounds.Height);
        var fore = sel ? (AppColors.IsDark ? Color.White : Color.Black) : AppColors.Text;
        TextRenderer.DrawText(g, it.Label, _list.Font, nameRect, fore, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, detail, _list.Font, new Rectangle(e.Bounds.Right - detailW - 6, e.Bounds.Y, detailW, e.Bounds.Height), AppColors.TextMuted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    /// <summary>ListBox không tự lấy focus khi click (xem SuggestPopup).</summary>
    private sealed class NoFocusList : ListBox
    {
        private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONDBLCLK = 0x0203;
        public event Action<int>? ItemClicked;
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            if (m.Msg is WM_LBUTTONDOWN or WM_LBUTTONDBLCLK)
            {
                var lp = m.LParam.ToInt64();
                var index = IndexFromPoint(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                if (index >= 0) { SelectedIndex = index; ItemClicked?.Invoke(index); }
                return;
            }
            base.WndProc(ref m);
        }
    }
}
