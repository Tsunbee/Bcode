using System.Text;
using Bcode.App.Controls;
using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Forms;

/// <summary>Giao diện WinForms của khung xem nhiều entity (chế độ App, thay entitypeek.html): Scintilla chỉ đọc, 2 cách xem "Một trang" / "Từng entity" (◀ ▶ / Alt+← Alt+→ / chip tên), Copy, Mở entity, F12 trong khung.</summary>
public sealed partial class EntityMultiPeekForm
{
    private Scintilla _ed = null!;
    private Label _nTitle = null!, _nSub = null!, _nCounter = null!, _nHint = null!;
    private PillButton _bAll = null!, _bEach = null!, _bPrev = null!, _bNext = null!, _bCopy = null!, _bOpen = null!;
    private FlowLayoutPanel _chips = null!;
    private Panel _nav = null!;
    private bool _each;
    private int _idx;

    private static string Norm(string s) => s.Replace("\r\n", "\n").Replace('\r', '\n');

    private void BuildNative()
    {
        KeyPreview = true;
        _each = string.Equals(_settings?.EntityPeekMode, "each", StringComparison.OrdinalIgnoreCase);

        _nTitle = new Label { AutoSize = true, BackColor = Color.Transparent, Font = new Font(ThemeManager.BaseFont.FontFamily, ThemeManager.BaseFont.Size + 2, FontStyle.Bold) };
        _nSub = NK.Muted("");
        var titles = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Color.Transparent };
        titles.Controls.Add(_nTitle); titles.Controls.Add(_nSub);
        _bAll = NK.Button("Một trang", () => SetMode(false), tip: "Xem toàn bộ nội dung các entity trong 1 trang");
        _bEach = NK.Button("Từng entity", () => SetMode(true), tip: "Mỗi entity một trang (Alt+←  Alt+→)");
        var modes = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true, BackColor = Color.Transparent };
        modes.Controls.Add(_bAll); modes.Controls.Add(_bEach);
        var head = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 56), Padding = new Padding(12, 6, 12, 4) };
        head.Controls.Add(titles); head.Controls.Add(modes);

        _bPrev = NK.Button("◀", () => Step(-1), tip: "Entity trước (Alt+←)"); _bNext = NK.Button("▶", () => Step(1), tip: "Entity sau (Alt+→)");
        _nCounter = NK.Muted(""); _chips = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, MaximumSize = new Size(900, 0) };
        var navFlow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, BackColor = Color.Transparent };
        navFlow.Controls.Add(_bPrev); navFlow.Controls.Add(_nCounter); navFlow.Controls.Add(_bNext); navFlow.Controls.Add(_chips);
        _nav = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(12, 0, 12, 4) };
        _nav.Controls.Add(navFlow);

        _ed = new Scintilla { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
        SqlScintilla.Configure(_ed); SqlScintilla.ApplyTheme(_ed); _ed.ReadOnly = true;
        _ed.KeyDown += (_, e) => { if (e.KeyCode == Keys.F12) { e.Handled = e.SuppressKeyPress = true; F12Native(); } };
        ThemeManager.Subscribe(this, () => { if (!_ed.IsDisposed) { SqlScintilla.ApplyTheme(_ed); SqlScintilla.SetLanguage(_ed, _lang); } });

        _nHint = NK.Muted("");
        _bCopy = NK.Button("Copy", CopyNative); _bOpen = NK.Button("Mở entity này", OpenNative, primary: true);
        var foot = new BarFlow { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(10, 7, 10, 7), FlowDirection = FlowDirection.RightToLeft };
        foot.Controls.Add(_bOpen); foot.Controls.Add(_bCopy); foot.Controls.Add(_nHint);

        Controls.Add(_ed); Controls.Add(foot); Controls.Add(_nav); Controls.Add(head);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; Close(); }
            else if (_each && e.Alt && e.KeyCode == Keys.Left) { e.Handled = true; Step(-1); }
            else if (_each && e.Alt && e.KeyCode == Keys.Right) { e.Handled = true; Step(1); }
        };
        Shown += (_, _) => { RenderNative(); _ed.Focus(); };
    }

    private string _lang = "SQL";

    private void SetMode(bool each)
    {
        _each = each;
        if (_settings is not null) { _settings.EntityPeekMode = each ? "each" : "all"; try { _settings.Save(); } catch { /* không lưu được thì chỉ mất ghi nhớ */ } }
        RenderNative();
    }

    private void Step(int d) { var n = _idx + d; if (_each && n >= 0 && n < _items.Count) { _idx = n; RenderNative(); } }

    private string AllText()
    {
        var first = _items.Select(i => i.Text.TrimStart()).FirstOrDefault(t => t.Length > 0) ?? "";
        var xml = first.StartsWith('<');
        return string.Join("\n\n", _items.Select(it =>
        {
            var label = "===== " + it.Title + "  (" + it.Subtitle + ") =====";
            return (xml ? "<!-- " + label.Replace("--", "- -") + " -->" : "-- " + label) + "\n" + Norm(it.Text);
        }));
    }

    private static bool LooksXml(string t) => t.TrimStart().StartsWith('<');

    private void RenderNative()
    {
        if (_items.Count == 0) return;
        _bAll.IsPrimary = !_each; _bEach.IsPrimary = _each; _bAll.Invalidate(); _bEach.Invalidate();
        _nav.Visible = _each;
        string text;
        if (_each)
        {
            var it = _items[_idx];
            text = Norm(it.Text);
            _nTitle.Text = $"{it.Title}   ({_idx + 1}/{_items.Count})"; _nSub.Text = it.Subtitle;
            _nCounter.Text = $"{_idx + 1} / {_items.Count}";
            _bPrev.Enabled = _idx > 0; _bNext.Enabled = _idx < _items.Count - 1;
            _chips.Controls.Clear();
            for (var i = 0; i < _items.Count; i++)
            {
                var x = _items[i]; var at = i;
                var c = NK.Button(x.Name, () => { _idx = at; RenderNative(); }, primary: i == _idx, tip: x.Subtitle);
                if (x.Missing) c.ForeOverride = AppColors.Warning;
                _chips.Controls.Add(c);
            }
            _nHint.Text = "Chỉ xem — Alt+←/→ chuyển entity, F12 trong khung để xem tiếp, Esc đóng.";
            _bCopy.Text = "Copy entity này"; _bOpen.Visible = !it.Missing;
        }
        else
        {
            text = AllText();
            _nTitle.Text = _items.Count + " entity đã chọn"; _nSub.Text = string.Join("  ", _items.Select(x => x.Title));
            _nHint.Text = "Chỉ xem — nội dung các entity nối theo thứ tự đã chọn.";
            _bCopy.Text = "Copy tất cả"; _bOpen.Visible = false;
        }
        _lang = LooksXml(text) ? "XML" : "SQL";
        SqlScintilla.SetLanguage(_ed, _lang);
        _ed.ReadOnly = false; _ed.Text = text; _ed.ReadOnly = true;
        _ed.GotoPosition(0);
    }

    private void CopyNative()
    {
        var text = _each ? Norm(_items[_idx].Text) : AllText();
        try { Clipboard.SetText(text.Replace("\n", "\r\n")); _bCopy.Text = "Đã copy"; } catch { /* clipboard bận */ }
    }

    private void OpenNative()
    {
        if (_idx < 0 || _idx >= _items.Count) return;
        var it = _items[_idx];
        if (it.FilePath is not null) _openFile(it.FilePath);
        else if (!it.Missing) _peekValue(it.Name, it.Text, it.DeclaringPath ?? "");
    }

    /// <summary>Vị trí byte của Scintilla (UTF-8) → vị trí ký tự (UTF-16) như Monaco getOffsetAt.</summary>
    private int CharOffset(int bytePos)
    {
        var bytes = Encoding.UTF8.GetBytes(_ed.Text);
        bytePos = Math.Clamp(bytePos, 0, bytes.Length);
        return Encoding.UTF8.GetString(bytes, 0, bytePos).Length;
    }

    private void F12Native()
    {
        if (!_each || _idx < 0 || _idx >= _items.Count) return;
        var selS = _ed.SelectionStart; var selE = _ed.SelectionEnd;
        OnF12(_items[_idx], CharOffset(_ed.CurrentPosition), selE > selS ? CharOffset(selS) : -1, selE > selS ? CharOffset(selE) : -1);
    }
}
