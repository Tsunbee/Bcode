using Bcode.App.Models;
using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>Khung "xem nhanh" (F12) nổi trên editor SQL Query chế độ App — tương đương window.showPeekMulti của bản Web (sqleditor.html): hiện cấu trúc bảng /
/// nội dung procedure, function, view, trigger ngay trong tab, không mở tab mới. Nhiều object (bôi đen nhiều tên) chuyển bằng nút ◀ ▶. Chủ (NativeSqlControl) lo tra object
/// và phím F12 / Ctrl+F12 / Esc; khung chỉ hiển thị.</summary>
internal sealed class SqlPeekPanel : Panel
{
    /// <summary>Obj = null: mục không phải object (vd. khai báo biến); Line &gt; 0: dòng (từ 1) cần cuộn tới và tô sáng; Needle: chữ cần đặt con trỏ trên dòng đó.</summary>
    internal sealed record Item(string Title, string Text, SqlObjectInfo? Obj, int Line = 0, string? Needle = null);

    private readonly Scintilla _ed = new() { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
    private readonly Label _title = new() { AutoSize = true, Margin = new Padding(0, 7, 12, 0), BackColor = Color.Transparent, UseMnemonic = false };
    private readonly Label _hint = new() { AutoSize = true, Margin = new Padding(0, 7, 12, 0), BackColor = Color.Transparent, UseMnemonic = false };
    private readonly BarFlow _head = new() { Dock = DockStyle.Top, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 3, 6, 3) };
    private readonly PillButton _prev, _next, _open, _close, _mode;
    // Dải chip từng object (như bản Web): bấm chip = xem object đó; ẩn khi chỉ 1 object hoặc đang gộp 1 trang.
    private readonly BarFlow _chips = new() { Dock = DockStyle.Top, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 2, 6, 3), Visible = false };
    private IReadOnlyList<Item> _items = Array.Empty<Item>();
    private int _index;
    /// <summary>Nhiều object: true = gộp 1 trang (mỗi object 1 khối có dòng tiêu đề), false = tách tab — nhớ trong phiên (bản Web nhớ ở localStorage 'bcode.peekMode').</summary>
    private static bool _mergeAll;

    public Scintilla Editor => _ed;
    public Item? Current => _index >= 0 && _index < _items.Count ? _items[_index] : null;
    public int Depth { get; set; }

    public event Action? CloseRequested;
    public event Action<Item>? OpenTabRequested;

    public SqlPeekPanel()
    {
        BackColor = Color.FromArgb(0x2F, 0xB2, 0x6A);   // viền xanh lá như khung peek của Web
        Padding = new Padding(2);
        Visible = false;

        SqlScintilla.Configure(_ed);
        FcodeLexer.ApplySql(_ed);
        _ed.CaretLineVisible = false;   // để dòng khai báo tô xanh không bị màu "dòng con trỏ" che

        _prev = Btn("◀", "Object trước", () => Go(-1));
        _next = Btn("▶", "Object kế", () => Go(1));
        _open = Btn("Mở tab mới", "Mở object đang xem ở tab SQL Query mới (Ctrl+F12)", () => { if (Current is { } c) OpenTabRequested?.Invoke(c); });
        _close = Btn("✕", "Đóng (Esc)", () => CloseRequested?.Invoke());
        _mode = Btn("", "Gộp mọi object vào 1 trang / tách từng object (Ctrl+PgUp / Ctrl+PgDn chuyển object)", () => { _mergeAll = !_mergeAll; Render(); _ed.Focus(); });
        _head.Controls.AddRange(new Control[] { _title, _hint, _prev, _next, _mode, _open, _close });
        _ed.KeyDown += (_, e) =>
        {
            // Ctrl+PgDn / Ctrl+PgUp: object kế / trước (như ped.addCommand của sqleditor.html).
            if (e.Control && !e.Shift && !e.Alt && e.KeyCode is Keys.PageDown or Keys.PageUp && _items.Count > 1)
            {
                e.Handled = e.SuppressKeyPress = true;
                if (_mergeAll) _mergeAll = false;
                Go(e.KeyCode == Keys.PageDown ? 1 : -1);
            }
        };

        var inner = new Panel { Dock = DockStyle.Fill };
        inner.Controls.Add(_ed);
        inner.Controls.Add(_chips);
        inner.Controls.Add(_head);   // thêm sau → Dock Top xếp trước (thanh tiêu đề, rồi dải chip), editor lấp phần còn lại
        Controls.Add(inner);
    }

    private PillButton Btn(string text, string tip, Action onClick)
    {
        var b = PillButton.Flat(text);
        b.OnPanelAlt = true;
        b.Margin = new Padding(0, 0, 4, 0);
        NK.Tip(b, tip);
        b.Click += (_, _) => onClick();
        return b;
    }

    public void ApplyTheme()
    {
        SqlScintilla.ApplyTheme(_ed);
        FcodeLexer.ApplySql(_ed);
        _ed.CaretLineVisible = false;   // ApplyTheme bật lại dòng con trỏ — tắt để dòng khai báo tô xanh không bị che
        _title.ForeColor = AppColors.Text;
        _hint.ForeColor = AppColors.TextMuted;
    }

    public void ShowItems(IReadOnlyList<Item> items, bool hasBack)
    {
        _items = items;
        _index = 0;
        _hint.Text = "F12: xem tiếp · Ctrl+F12: mở tab mới · Esc: " + (hasBack ? "quay lại bước trước" : "đóng");
        ApplyTheme();
        Render();
        Visible = true;
        BringToFront();
        _ed.Focus();
    }

    private void Go(int delta)
    {
        if (_items.Count == 0) return;
        _index = (_index + delta + _items.Count) % _items.Count;
        Render();
    }

    private void Render()
    {
        var item = Current;
        var all = _items.Count > 1 && _mergeAll;
        _title.Text = item is null ? "" : all ? $"{_items.Count} object (gộp 1 trang)" : (_items.Count > 1 ? $"[{_index + 1}/{_items.Count}] " : "") + item.Title;
        _prev.Visible = _next.Visible = _items.Count > 1 && !all;
        _mode.Visible = _items.Count > 1;
        _mode.Text = all ? "Đang gộp 1 trang — bấm để tách tab" : "Đang tách tab — bấm để gộp 1 trang";
        RenderChips(all);
        _ed.ReadOnly = false;
        _ed.Text = all ? string.Join("\n\n\n", _items.Select(i => "-- ===================== " + i.Title + " =====================\n" + i.Text)) : item?.Text ?? "";
        _ed.EmptyUndoBuffer();
        _ed.ReadOnly = true;
        _ed.GotoPosition(0);
        _ed.MarkerDeleteAll(DeclMarker);
        if (!all && item is { Line: > 0 } && item.Line <= _ed.Lines.Count)
        {
            var line = _ed.Lines[item.Line - 1];
            var bg = _ed.Styles[Style.Default].BackColor;
            _ed.Markers[DeclMarker].Symbol = MarkerSymbol.Background;
            _ed.Markers[DeclMarker].SetBackColor(Color.FromArgb((int)(bg.R * 0.65 + 0x2F * 0.35), (int)(bg.G * 0.65 + 0xB2 * 0.35), (int)(bg.B * 0.65 + 0x6A * 0.35)));
            line.MarkerAdd(DeclMarker);
            var at = item.Needle is { Length: > 0 } n ? line.Text.IndexOf(n, StringComparison.OrdinalIgnoreCase) : -1;
            var caret = at >= 0 ? line.Position + System.Text.Encoding.UTF8.GetByteCount(line.Text[..at]) : line.Position;
            _revealLine = item.Line - 1;
            _revealCaret = caret;
            BeginInvoke(new Action(RevealDeclaration));   // sau khi khung có kích thước (LinesOnScreen) mới cuộn giữa màn hình được
        }
    }

    private void RenderChips(bool all)
    {
        _chips.SuspendLayout();
        var old = _chips.Controls.Cast<Control>().ToList();
        _chips.Controls.Clear();
        foreach (var c in old) c.Dispose();
        // Không đọc lại _chips.Visible: khung peek chưa hiện (lần vẽ đầu trong ShowItems) thì Visible luôn trả false.
        var show = _items.Count > 1 && !all;
        _chips.Visible = show;
        if (show)
            for (var i = 0; i < _items.Count; i++)
            {
                var idx = i;
                var chip = PillButton.Flat(_items[i].Title.Split("   (")[0]);
                chip.OnPanelAlt = true;
                chip.IsPrimary = i == _index;
                chip.Margin = new Padding(0, 0, 4, 2);
                chip.Click += (_, _) => { _index = idx; Render(); _ed.Focus(); };
                _chips.Controls.Add(chip);
            }
        _chips.ResumeLayout(true);
    }

    private const int DeclMarker = 4;
    private int _revealLine = -1, _revealCaret;

    private void RevealDeclaration()
    {
        if (IsDisposed || _revealLine < 0) return;
        _ed.FirstVisibleLine = Math.Max(0, _revealLine - Math.Max(1, _ed.LinesOnScreen) / 2);
        _ed.GotoPosition(_revealCaret);
        _revealLine = -1;
    }

    public bool IsEditorFocused => _ed.Focused;
}
