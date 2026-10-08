using System.Runtime.InteropServices;

namespace Bcode.App.UI;

/// <summary>
/// Tự cuộn lưới khi đang kéo chuột để chọn nhiều ô mà con trỏ chạm mép lưới. DataGridView chỉ tự cuộn khi con trỏ ra NGOÀI lưới — nhưng lưới của
/// Bcode thường sát mép màn hình / cửa sổ nên con trỏ không thể ra ngoài, kéo chọn sang cột khác thì màn hình đứng yên. Ở đây: giữ chuột trái và rê vào
/// dải 28px sát mép phải/trái/trên/dưới của lưới thì cuộn dần (càng sát mép càng nhanh), rồi báo cho lưới một lần "chuột vừa di chuyển" để vùng chọn
/// kéo dài theo ô đang nằm dưới con trỏ. Gắn một lần cho mỗi lưới (<see cref="Attach"/>).
/// </summary>
public static class GridDragScroll
{
    private const int Edge = 28;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const int MK_LBUTTON = 0x0001;

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DataGridView, object> Wired = new();

    public static void Attach(DataGridView grid)
    {
        if (Wired.TryGetValue(grid, out _)) return;
        Wired.Add(grid, new object());

        var timer = new System.Windows.Forms.Timer { Interval = 35 };
        var dragging = false;
        grid.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left && grid.HitTest(e.X, e.Y).Type == DataGridViewHitTestType.Cell) { dragging = true; timer.Start(); } };
        grid.MouseUp += (_, _) => { dragging = false; timer.Stop(); };
        grid.Leave += (_, _) => { dragging = false; timer.Stop(); };
        grid.Disposed += (_, _) => timer.Dispose();
        timer.Tick += (_, _) =>
        {
            if (!dragging || grid.IsDisposed || (Control.MouseButtons & MouseButtons.Left) == 0) { dragging = false; timer.Stop(); return; }
            var p = grid.PointToClient(Control.MousePosition);
            var dx = 0; var dy = 0;
            var vScrollW = grid.Controls.OfType<VScrollBar>().FirstOrDefault(s => s.Visible)?.Width ?? 0;
            var hScrollH = grid.Controls.OfType<HScrollBar>().FirstOrDefault(s => s.Visible)?.Height ?? 0;
            var right = grid.ClientSize.Width - vScrollW;
            var bottom = grid.ClientSize.Height - hScrollH;
            var left = grid.RowHeadersVisible ? grid.RowHeadersWidth : 0;
            var top = grid.ColumnHeadersVisible ? grid.ColumnHeadersHeight : 0;
            if (p.X > right - Edge) dx = Math.Max(1, (p.X - (right - Edge)) / 2 + 6);
            else if (p.X < left + Edge) dx = -Math.Max(1, ((left + Edge) - p.X) / 2 + 6);
            if (p.Y > bottom - Edge) dy = 1;
            else if (p.Y < top + Edge) dy = -1;
            if (dx == 0 && dy == 0) return;

            var moved = false;
            if (dx != 0)
            {
                var max = grid.Columns.GetColumnsWidth(DataGridViewElementStates.Visible) - (grid.ClientSize.Width - left - vScrollW);
                var next = Math.Max(0, Math.Min(Math.Max(0, max), grid.HorizontalScrollingOffset + dx * 3));
                if (next != grid.HorizontalScrollingOffset) { grid.HorizontalScrollingOffset = next; moved = true; }
            }
            if (dy != 0 && grid.RowCount > 0)
            {
                var first = grid.FirstDisplayedScrollingRowIndex;
                var nextRow = Math.Max(0, Math.Min(grid.RowCount - 1, first + dy));
                if (first >= 0 && nextRow != first) { grid.FirstDisplayedScrollingRowIndex = nextRow; moved = true; }
            }
            // Cuộn xong con trỏ đứng yên nên không có sự kiện MouseMove — tự gửi một cái để vùng chọn kéo dài theo ô mới nằm dưới con trỏ.
            if (moved && grid.IsHandleCreated)
                SendMessage(grid.Handle, WM_MOUSEMOVE, (IntPtr)MK_LBUTTON, (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)));
        };
    }
}
