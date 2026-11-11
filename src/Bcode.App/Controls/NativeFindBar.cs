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
    /// <summary>(chữ cần tìm, thay bằng, thay hết?) — chỉ khi đã bật hàng Thay thế (<see cref="ShowReplace"/>).</summary>
    public event Action<string, string, bool>? Replace;

    private readonly RoundedInput _r = NK.Input("Thay bằng...");
    private readonly Panel _replaceRow;

    /// <summary>Hiện / ẩn hàng Thay thế (thanh cao thêm 1 hàng).</summary>
    public void ShowReplace(bool on)
    {
        if (_replaceRow.Visible == on) return;
        _replaceRow.Visible = on;
        Height = NativeSkin.RowHeight(this, on ? 72 : 38);
    }
    public bool ReplaceShown => _replaceRow.Visible;
    public void FocusReplace() { _r.Inner.Focus(); _r.Inner.SelectAll(); }

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
        // Hàng Thay thế (chỉ hiện khi host gọi ShowReplace — SQL Query Ctrl+H như find widget của Monaco): ô "Thay bằng", Thay (Enter), Thay hết (Ctrl+Alt+Enter).
        _replaceRow = new Panel { Dock = DockStyle.Bottom, Height = NativeSkin.RowHeight(this, 32), Visible = false, BackColor = Color.Transparent, Padding = new Padding(0, 4, 0, 0) };
        var one = NK.Button("Thay", () => { Replace?.Invoke(_q.Text, _r.Text, false); _r.Inner.Focus(); }, tip: "Thay kết quả đang chọn rồi sang kết quả kế (Enter ở ô Thay bằng)");
        var all = NK.Button("Thay hết", () => { Replace?.Invoke(_q.Text, _r.Text, true); _r.Inner.Focus(); }, tip: "Thay mọi kết quả (Ctrl+Alt+Enter) — 1 lần Ctrl+Z là hoàn tác");
        one.Dock = DockStyle.Right; all.Dock = DockStyle.Right; one.Margin = all.Margin = new Padding(2, 0, 0, 0);
        _r.Dock = DockStyle.Fill;
        _r.InputKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; Replace?.Invoke(_q.Text, _r.Text, e.Control && e.Alt); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = e.SuppressKeyPress = true; CloseRequested?.Invoke(); }
        };
        _replaceRow.Controls.Add(_r); _replaceRow.Controls.Add(one); _replaceRow.Controls.Add(all);
        // WinForms dock control CUỐI danh sách trước: hàng Thay thế (Bottom), ✕ ngoài cùng phải, rồi ↓ ↑ "3/17", ô tìm (Fill) lấp phần còn lại.
        Controls.Add(_q); Controls.Add(_info); Controls.Add(prev); Controls.Add(next); Controls.Add(close); Controls.Add(_replaceRow);
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

    /// <summary>Màu nền nằm dưới thanh (tô 4 góc ngoài vùng bo) khi thanh nổi trên 1 control khác cha (vd editor Scintilla); null = nền của cha.</summary>
    public Func<Color>? UnderColor { get; set; }

    /// <summary>Thẻ nổi: nền panel, viền bo tròn (như .findbar của trang web) — vẽ bằng GDI+, không bitmap.</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var under = new SolidBrush(UnderColor?.Invoke() ?? Parent?.BackColor ?? AppColors.Background)) g.FillRectangle(under, ClientRectangle);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var path = FlatToolStripRenderer.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), Math.Min(NativeSkin.CardRadius(this), (Height - 2) / 2));
        using (var fill = new SolidBrush(AppColors.PanelAlt)) g.FillPath(fill, path);
        using var pen = new Pen(AppColors.Border, NativeSkin.BorderWidth(this));
        g.DrawPath(pen, path);
    }
}
