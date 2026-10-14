using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>Thanh tìm nổi (Ctrl+F) của <see cref="ScriptEditorControl"/> ở chế độ App — bản WinForms của findbar.html: ô tìm bo tròn, "3/17", ↑ ↓ ✕.
/// Enter / F3 = tìm tiếp, Shift+Enter / Shift+F3 = tìm lùi, Esc = đóng; gõ là tìm ngay (typing = true, như trang web).</summary>
internal sealed class NativeFindBar : Panel
{
    private readonly RoundedInput _q = NK.Input("Tìm trong file...");
    private readonly Label _info = NK.Muted("");

    /// <summary>(chữ cần tìm, tìm xuôi?, đang gõ?)</summary>
    public event Action<string, bool, bool>? Find;
    public event Action? CloseRequested;

    public NativeFindBar()
    {
        DoubleBuffered = true;
        Padding = new Padding(8, 5, 6, 5);
        _q.TextChanged += (_, _) => { if (!_setting) Find?.Invoke(_q.Text, true, true); };
        _q.InputKeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Enter or Keys.F3) { e.Handled = e.SuppressKeyPress = true; Find?.Invoke(_q.Text, !e.Shift, false); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = e.SuppressKeyPress = true; CloseRequested?.Invoke(); }
        };
        var prev = Small("↑", "Tìm lùi (Shift+Enter / Shift+F3)", () => { Find?.Invoke(_q.Text, false, false); _q.Inner.Focus(); });
        var next = Small("↓", "Tìm tiếp (Enter / F3)", () => { Find?.Invoke(_q.Text, true, false); _q.Inner.Focus(); });
        var close = Small("✕", "Đóng (Esc)", () => CloseRequested?.Invoke());
        _info.AutoSize = true; _info.Dock = DockStyle.Right; _info.Padding = new Padding(6, 0, 4, 0); _info.TextAlign = ContentAlignment.MiddleRight;
        _q.Dock = DockStyle.Fill;
        // WinForms dock control CUỐI danh sách trước: ✕ ngoài cùng phải, rồi ↓ ↑ "3/17", ô tìm (Fill) lấp phần còn lại.
        Controls.Add(_q); Controls.Add(_info); Controls.Add(prev); Controls.Add(next); Controls.Add(close);
        Height = NativeSkin.RowHeight(this, 38);
        ThemeManager.ThemeChanged += Invalidate;
        Disposed += (_, _) => ThemeManager.ThemeChanged -= Invalidate;
    }

    private PillButton Small(string text, string tip, System.Action click)
    {
        var b = NK.Button(text, click, tip: tip);
        b.AutoWidth = false; b.Width = NativeSkin.RowHeight(this, 28); b.Dock = DockStyle.Right; b.Margin = new Padding(2, 0, 0, 0);
        return b;
    }

    private bool _setting;
    /// <summary>Điền sẵn ô tìm (không tìm ngay — như window.setText của trang web).</summary>
    public void SetText(string text) { _setting = true; _q.Text = text; _setting = false; }
    public void SetInfo(string text, bool none) { _info.Text = text; _info.ForeColor = none ? AppColors.Danger : AppColors.TextMuted; }
    public void FocusInput() { _q.Inner.Focus(); _q.Inner.SelectAll(); }

    /// <summary>Thẻ nổi: nền panel, viền bo tròn (như .findbar của trang web) — vẽ bằng GDI+, không bitmap.</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var under = new SolidBrush(Parent?.BackColor ?? AppColors.Background)) g.FillRectangle(under, ClientRectangle);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = FlatToolStripRenderer.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min(NativeSkin.CardRadius(this), (Height - 2) / 2));
        using (var fill = new SolidBrush(AppColors.PanelAlt)) g.FillPath(fill, path);
        using var pen = new Pen(AppColors.Border, NativeSkin.BorderWidth(this));
        g.DrawPath(pen, path);
    }
}
