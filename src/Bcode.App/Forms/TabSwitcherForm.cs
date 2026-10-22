using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// Hộp chọn nhanh tab: hiện NGAY khi bấm phím tắt tab.next / tab.prev có Ctrl (mặc định Ctrl+Tab / Ctrl+Shift+Tab) — xem MainForm.OpenTabSwitcher.
/// Liệt kê mọi tab đang mở; giữ Ctrl bấm Tab (Shift+Tab) / ↑ ↓ để chọn, THẢ Ctrl là đi (như Alt+Tab của Windows); Enter hoặc bấm chuột cũng đi;
/// Esc hoặc bấm ra ngoài thì thôi. Chỉ trả về vị trí tab được chọn, việc chuyển tab do MainForm làm.
/// </summary>
public class TabSwitcherForm : DpiForm
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly ListBox _list = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false,
        DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28,
    };
    private readonly int _current;
    private readonly bool _commitOnCtrlUp;
    // Dò trạng thái THẬT của phím Ctrl: keyup của Ctrl hay đi lạc (vào WebView2 — tiến trình trình duyệt riêng — khi hộp chưa kịp nhận focus,
    // hoặc thả trước khi hộp kịp hiện) nên không chỉ trông vào OnKeyUp — trước đây thả Ctrl đôi lúc không chuyển, phải bấm Enter.
    private readonly System.Windows.Forms.Timer _ctrlWatch = new() { Interval = 30 };
    private bool _done;

    /// <summary>Vị trí tab được chọn; -1 nếu huỷ.</summary>
    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Phím Ctrl (trái hoặc phải) đang được giữ — theo trạng thái phím thật, không theo hàng đợi message.</summary>
    public static bool CtrlHeld => (GetAsyncKeyState(0x11) & 0x8000) != 0;

    /// <param name="initialIndex">Mục được chọn sẵn khi mở (tab mà Ctrl+Tab bấm nhanh sẽ tới).</param>
    /// <param name="commitOnCtrlUp">Mở bằng tổ hợp có Ctrl: thả Ctrl là chọn mục đang sáng.</param>
    public TabSwitcherForm(IReadOnlyList<string> tabs, int currentIndex, int initialIndex, bool commitOnCtrlUp)
    {
        _current = currentIndex;
        _commitOnCtrlUp = commitOnCtrlUp;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = AppColors.Border;
        Padding = new Padding(1); // viền 1px

        var header = new Label
        {
            Text = commitOnCtrlUp
                ? "Chuyển tab  —  giữ Ctrl, bấm Tab / ↑ ↓ để chọn, thả Ctrl để đi, Esc để đóng"
                : "Chuyển tab  —  ↑ ↓ hoặc Tab để chọn, Enter hoặc bấm chuột để đi, Esc để đóng",
            Dock = DockStyle.Top, Height = 30, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(10, 0, 8, 0),
            BackColor = AppColors.PanelAlt, ForeColor = AppColors.TextMuted, AutoEllipsis = true,
        };
        _list.BackColor = AppColors.Panel;
        _list.ForeColor = AppColors.Text;
        _list.Font = ThemeManager.BaseFont;
        foreach (var t in tabs) _list.Items.Add(t);
        if (_list.Items.Count > 0) _list.SelectedIndex = Math.Clamp(initialIndex, 0, _list.Items.Count - 1);

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
        Deactivate += (_, _) => { if (!_done) Cancel(); };
        _ctrlWatch.Tick += (_, _) => { if (!CtrlHeld) Accept(); };
        FormClosed += (_, _) => _ctrlWatch.Stop();
    }

    /// <summary>Đặt hộp ở giữa cửa sổ <paramref name="owner"/>.</summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (Owner is { } o)
            Location = new Point(o.Left + (o.Width - Width) / 2, o.Top + Math.Max(40, (o.Height - Height) / 3));
        _list.Focus();
        if (!_commitOnCtrlUp) return;
        if (!CtrlHeld) { Accept(); return; } // Ctrl đã nhả trong lúc hộp đang mở lên
        _ctrlWatch.Start();
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

    /// <summary>Chọn mục đang sáng rồi đóng. Gọi được cả trước khi hộp hiện (Ctrl đã nhả trước đó) — khi đó không cần hiện hộp.</summary>
    public void Accept()
    {
        if (_done) return;
        _done = true;
        _ctrlWatch.Stop();
        SelectedIndex = _list.SelectedIndex;
        if (Visible) Close();
    }

    private void Cancel()
    {
        if (_done) return;
        _done = true;
        _ctrlWatch.Stop();
        SelectedIndex = -1;
        if (Visible) Close();
    }

    /// <summary>Đi tới mục kế (+1) / trước (-1), vòng tròn — bấm lại phím tắt tab.next / tab.prev khi hộp đang mở (kể cả lúc phím tới trang WebView2 trước khi hộp kịp nhận focus).</summary>
    public void Step(int step)
    {
        if (_done || _list.Items.Count == 0) return;
        _list.SelectedIndex = (_list.SelectedIndex + step + _list.Items.Count) % _list.Items.Count;
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData & ~Keys.Control)   // đang giữ Ctrl (hộp mở bằng Ctrl+Tab) thì ↑ ↓ Enter cũng tới đây kèm Ctrl
        {
            case Keys.Tab: case Keys.Down: Step(1); return true;
            case Keys.Shift | Keys.Tab: case Keys.Up: Step(-1); return true;
            case Keys.Enter: Accept(); return true;
            case Keys.Escape: Cancel(); return true;
        }
        // Phím tắt chuyển tab người dùng tự gán (Template → Phím tắt) bấm lại khi hộp đang mở: đi tiếp / lùi như Tab.
        switch (ShortcutRegistry.FromKeys(keyData) is { } combo ? ShortcutRegistry.AppIdFor(combo) : null)
        {
            case "tab.next": Step(1); return true;
            case "tab.prev": Step(-1); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (e.KeyCode == Keys.ControlKey && _commitOnCtrlUp) Accept();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _ctrlWatch.Dispose();
        base.Dispose(disposing);
    }
}
