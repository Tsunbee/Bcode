using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>Bản WinForms của khung xem file (chế độ App): Scintilla chỉ đọc thay Monaco — cùng API LoadContent / Clear / ShowFind / F12 (ResolveF12) / Ctrl+G; thanh Find nhỏ phía trên.</summary>
public partial class MonacoPreviewControl
{
    private Scintilla? _ed;
    private Panel _findBar = null!;
    private RoundedInput _findBox = null!;
    private Label _findStatus = null!;

    private void BuildNative()
    {
        _ed = new Scintilla { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None, ReadOnly = true };
        SqlScintilla.Configure(_ed); SqlScintilla.ApplyTheme(_ed); SetLang(_ed, "XML");
        _ed.ReadOnly = true;
        _ed.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F12) { e.Handled = e.SuppressKeyPress = true; F12Native(); }
            else if (e.Control && e.KeyCode == Keys.G) { e.Handled = e.SuppressKeyPress = true; GoToNative(); }
            else if (e.Control && e.KeyCode == Keys.F) { e.Handled = e.SuppressKeyPress = true; ShowFindNative(); }
        };
        ThemeManager.Subscribe(this, () => { if (_ed is { IsDisposed: false }) { SqlScintilla.ApplyTheme(_ed); if (_lang != "None") SetLang(_ed, _lang); ApplyHotNative(); } });

        _findBox = NK.Input("Tìm... (Enter = tiếp, Shift+Enter = lùi, Esc = đóng)"); _findBox.Dock = DockStyle.Fill;
        _findStatus = NK.Muted(""); _findStatus.Dock = DockStyle.Right; _findStatus.AutoSize = false; _findStatus.Width = 120; _findStatus.TextAlign = ContentAlignment.MiddleLeft;
        _findBar = new Panel { Dock = DockStyle.Top, Height = NativeSkin.RowHeight(this, 36), Padding = new Padding(8, 4, 8, 4), Visible = false };
        _findBar.Controls.Add(_findBox); _findBar.Controls.Add(_findStatus);
        _findBox.InputKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; FindNext(!e.Shift); }
            else if (e.KeyCode == Keys.Escape) { e.Handled = e.SuppressKeyPress = true; _findBar.Visible = false; _ed.Focus(); }
        };
        Controls.Add(_ed); Controls.Add(_findBar);
    }

    private string _lang = "XML";

    private void LoadNative(string? path, string content, string? entityResolveBasePath)
    {
        var ed = _ed!;
        CurrentPath = path;
        _entityResolveBasePath = entityResolveBasePath ?? path;
        if (path is not null) ScriptEditorControl.PrewarmEntityIndex(_entityResolveBasePath, content);
        _text = path is null ? "" : content.Replace("\r\n", "\n").Replace('\r', '\n');
        var show = path is null ? content : _text;
        var lang = path is null ? "None" : show.TrimStart().StartsWith('<') ? "XML" : "SQL";
        if (lang != _lang) { _lang = lang; if (lang == "None") { FcodeLexer.Detach(ed); ed.Lexer = Lexer.Null; } else SetLang(ed, lang); }
        ed.ReadOnly = false; ed.Text = show; ed.ReadOnly = true;
        FcodeLexer.Restyle(ed);   // tô FCode ngay (file XML) — không đợi StyleNeeded
        ApplyHotNative();
        ed.GotoPosition(0);
    }

    private const int HotIndicatorA = 16, HotIndicatorB = 17, HotMarkerA = 20, HotMarkerB = 21;

    /// <summary>Tô mục hay dùng của Ctrl+G ngay trong khung xem (như BcodeViewer / chế độ Web — goto.js refreshHot): tên chữ cam / đỏ
    /// (command Inserted / Updated, on$…$ExecuteCommand, on$…$ResponseComplete, onChange$Grid…) + nền nhạt cả dòng. Danh sách mục: <see cref="GoToBox.Scan"/>.</summary>
    private void ApplyHotNative()
    {
        if (_ed is not { IsDisposed: false } ed) return;
        var bg = ed.Styles[Style.Default].BackColor;
        Color Tint(Color c) => Color.FromArgb((bg.R * 88 + c.R * 12) / 100, (bg.G * 88 + c.G * 12) / 100, (bg.B * 88 + c.B * 12) / 100);
        foreach (var (ind, mark, color) in new[] { (HotIndicatorA, HotMarkerA, GoToBox.HotA), (HotIndicatorB, HotMarkerB, GoToBox.HotB) })
        {
            ed.Indicators[ind].Style = IndicatorStyle.TextFore;
            ed.Indicators[ind].ForeColor = color;
            ed.Markers[mark].Symbol = MarkerSymbol.Background;
            ed.Markers[mark].SetBackColor(Tint(color));
            ed.IndicatorCurrent = ind;
            ed.IndicatorClearRange(0, ed.TextLength);
            ed.MarkerDeleteAll(mark);
        }
        if (CurrentPath is null || _lang != "XML") return;
        foreach (var items in GoToBox.Scan(_text).Values)
            foreach (var it in items.Where(i => i.Hot != '\0'))
            {
                var start = Pos(it.NameOffset);
                ed.IndicatorCurrent = it.Hot == 'a' ? HotIndicatorA : HotIndicatorB;
                ed.IndicatorFillRange(start, Pos(it.NameOffset + it.Label.Length) - start);
                ed.Lines[ed.LineFromPosition(start)].MarkerAdd(it.Hot == 'a' ? HotMarkerA : HotMarkerB);
            }
    }

    /// <summary>File XML/FCode: bộ tô FCode (SQL / JS trong từng CDATA, như Monaco của chế độ Web); còn lại: lexer Scintilla thường.</summary>
    private static void SetLang(Scintilla ed, string lang)
    {
        if (lang == "XML") FcodeLexer.Apply(ed);
        else { FcodeLexer.Detach(ed); SqlScintilla.SetLanguage(ed, lang); }
    }

    /// <summary>Vị trí trong khung = vị trí ký tự trong <see cref="_text"/>: API của ScintillaNET (GotoPosition, CurrentPosition, IndicatorFillRange...) tính theo KÝ TỰ,
    /// tự đổi sang byte bên trong. Trước đây đổi thêm 1 lần sang byte UTF-8 → file có tiếng Việt phía trước (gần như mọi file FCode) thì Ctrl+G nhảy lệch,
    /// F12 đọc sai chữ dưới con trỏ (đo: tên hàm ở ký tự 7825, byte 7903).</summary>
    private int Pos(int offset) => Math.Clamp(offset, 0, _text.Length);

    private void F12Native()
    {
        if (CurrentPath is null || _ed is null) return;
        var s = _ed.SelectionStart; var e = _ed.SelectionEnd;
        OnF12(Pos(_ed.CurrentPosition), e > s ? Pos(s) : -1, e > s ? Pos(e) : -1);
    }

    private void GoToNative()
    {
        if (CurrentPath is null || _ed is null) return;
        if (ScriptEditorControl.ShowGoToDialog(FindForm(), _text, Pos(_ed.CurrentPosition)) is int target)
        {
            _ed.GotoPosition(Pos(target)); _ed.ScrollCaret();
        }
        _ed.Focus();
    }

    private void ShowFindNative()
    {
        _findBar.Visible = true;
        if (_ed!.SelectionEnd > _ed.SelectionStart && _ed.SelectionEnd - _ed.SelectionStart < 200) _findBox.Text = _ed.SelectedText;
        _findBox.Inner.Focus(); _findBox.Inner.SelectAll();
    }

    private void FindNext(bool forward)
    {
        var ed = _ed!; var term = _findBox.Text;
        if (term.Length == 0) return;
        ed.SearchFlags = SearchFlags.None;
        int pos;
        if (forward) { ed.TargetStart = ed.SelectionEnd; ed.TargetEnd = ed.TextLength; pos = ed.SearchInTarget(term); if (pos < 0) { ed.TargetStart = 0; ed.TargetEnd = ed.TextLength; pos = ed.SearchInTarget(term); _findStatus.Text = pos >= 0 ? "Quay lại đầu" : "Không tìm thấy"; } else _findStatus.Text = ""; }
        else { ed.TargetStart = ed.SelectionStart; ed.TargetEnd = 0; pos = ed.SearchInTarget(term); if (pos < 0) { ed.TargetStart = ed.TextLength; ed.TargetEnd = 0; pos = ed.SearchInTarget(term); _findStatus.Text = pos >= 0 ? "Quay lại cuối" : "Không tìm thấy"; } else _findStatus.Text = ""; }
        if (pos < 0) return;
        ed.SetSelection(ed.TargetStart, ed.TargetEnd); ed.ScrollCaret();
    }
}
