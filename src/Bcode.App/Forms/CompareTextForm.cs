using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Windows.Forms;
using Bcode.App.Controls;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using ScintillaNET;

namespace Bcode.App.Forms;

/// <summary>
/// Compare Text: 2 khung nhập (Scintilla, tô màu theo ngôn ngữ — tự nhận khi dán, hoặc chọn ở ô "Tô màu" trên thanh) → 📄 Compare → 2 khung so sánh chỉ đọc,
/// lăn cùng nhau: nền dòng xoá (trái, đỏ) / thêm (phải, xanh) / dòng trống chèn cho thẳng hàng (xám), từ khác nhau trong dòng sửa tô đậm hơn,
/// lề = số dòng gốc + dấu − / + / ~, thanh tổng quan bên phải (bấm = nhảy tới chỗ khác), ◀ ▶ hoặc F8 / Shift+F8 = chỗ khác trước / tiếp.
/// Dùng chung cho chế độ Web và App (chỉ thanh trên khác nhau: comparebar.html / CompareBarPage).
/// </summary>
public class CompareTextControl : UserControl
{
    private readonly WebBarHost _topBarWeb = new("comparebar.html", 85);

    private readonly Scintilla _content1Box;
    private readonly Scintilla _content2Box;
    private readonly Label _lbl1, _lbl2, _diffLbl1, _diffLbl2;
    private readonly Panel _pnlLeft;
    private readonly Panel _pnlRight;

    private readonly Scintilla _diffLeftBox;
    private readonly Scintilla _diffRightBox;
    private readonly Panel _diffPnlLeft;
    private readonly Panel _diffPnlRight;
    private readonly DiffOverviewBar _overview = new() { Dock = DockStyle.Right, Visible = false };
    private bool _syncingScroll;

    private readonly SplitContainer _split;
    private readonly DiffService _diffService = new();
    private readonly System.Windows.Forms.Timer _detectTimer = new() { Interval = 350 };

    private List<int> _blockStarts = new();
    private int _currentBlock = -1;
    private int _currentRow = -1;
    private RowMark[] _leftMarks = Array.Empty<RowMark>();
    private RowMark[] _rightMarks = Array.Empty<RowMark>();
    private List<Row> _leftRows = new(), _rightRows = new();
    private bool[] _leftMarginDone = Array.Empty<bool>(), _rightMarginDone = Array.Empty<bool>();
    private string _summaryCounts = "";
    private string _path1 = "";
    private string _path2 = "";

    /// <summary>"auto" = tự nhận theo nội dung (<see cref="TextLanguageDetector"/>), hoặc một mã trong <see cref="TextLanguageDetector.All"/>.</summary>
    private string _langChoice = "auto";
    private string _lang1 = TextLanguageDetector.Text, _lang2 = TextLanguageDetector.Text;
    private string _diffLang1 = "", _diffLang2 = "";

    public event Action? FloatWindowRequested;

    public CompareTextControl()
    {
        Dock = DockStyle.Fill;
        BackColor = AppColors.Background;

        _topBarWeb.Dock = DockStyle.Top;
        _topBarWeb.Height = 85;

        _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 4 };

        _content1Box = NewEditor(readOnly: false); _content2Box = NewEditor(readOnly: false);
        _pnlLeft = MakeLabeledPanel(_content1Box, out _lbl1);
        _pnlRight = MakeLabeledPanel(_content2Box, out _lbl2);

        _diffLeftBox = NewEditor(readOnly: true); _diffRightBox = NewEditor(readOnly: true);
        _diffLeftBox.Tag = "left"; _diffRightBox.Tag = "right";   // ApplyDiffStyles: trái tô đỏ, phải tô xanh
        _diffPnlLeft = MakeLabeledPanel(_diffLeftBox, out _diffLbl1);
        _diffPnlRight = MakeLabeledPanel(_diffRightBox, out _diffLbl2);

        _diffLeftBox.UpdateUI += (_, e) => OnDiffUpdateUI(_diffLeftBox, _diffRightBox, e);
        _diffRightBox.UpdateUI += (_, e) => OnDiffUpdateUI(_diffRightBox, _diffLeftBox, e);
        _diffLeftBox.Resize += (_, _) => { _overview.Invalidate(); FillMargins(); };
        _overview.Viewport = () => (_diffLeftBox.FirstVisibleLine, _diffLeftBox.LinesOnScreen);
        _overview.RowPicked += OnOverviewPicked;

        _content1Box.TextChanged += (_, _) => { _detectTimer.Stop(); _detectTimer.Start(); };
        _content2Box.TextChanged += (_, _) => { _detectTimer.Stop(); _detectTimer.Start(); };
        _detectTimer.Tick += (_, _) => { _detectTimer.Stop(); RefreshLanguages(); };
        Disposed += (_, _) => _detectTimer.Dispose();

        _split.Panel1.Controls.Add(_pnlLeft);
        _split.Panel2.Controls.Add(_pnlRight);

        _split.HandleCreated += (_, _) =>
        {
            try { if (_split.Width > 100) _split.SplitterDistance = _split.Orientation == Orientation.Vertical ? _split.Width / 2 : _split.Height / 2; }
            catch { }
        };

        this.Resize += (_, _) =>
        {
            try
            {
                bool shouldBeVertical = this.Width >= 800;
                if (shouldBeVertical && _split.Orientation == Orientation.Horizontal)
                {
                    _split.Orientation = Orientation.Vertical;
                    _split.SplitterDistance = _split.Width / 2;
                }
                else if (!shouldBeVertical && _split.Orientation == Orientation.Vertical)
                {
                    _split.Orientation = Orientation.Horizontal;
                    _split.SplitterDistance = _split.Height / 2;
                }
            }
            catch { }
        };

        Controls.Add(_split);
        Controls.Add(_overview);
        Controls.Add(_topBarWeb);
        WireTopBar();

        ApplyThemeAll();
        ThemeManager.Subscribe(this, ApplyThemeAll);
        UpdateHeaders();
    }

    private void WireTopBar()
    {
        _topBarWeb.Message += root =>
        {
            switch (root.GetProperty("action").GetString())
            {
                case "float": FloatWindowRequested?.Invoke(); break;
                case "compare": RunCompare(); break;
                case "edit": ShowEditMode(); break;
                case "prev": GoToBlock(_currentBlock - 1); break;
                case "next": GoToBlock(_currentBlock + 1); break;
                case "browse1": ChooseFile(1, _content1Box); break;
                case "browse2": ChooseFile(2, _content2Box); break;
                case "lang":
                    _langChoice = root.TryGetProperty("value", out var v) ? v.GetString() ?? "auto" : "auto";
                    RefreshLanguages();
                    break;
                case "update-path":
                    if (root.GetProperty("which").GetString() == "file1") _path1 = root.GetProperty("value").GetString() ?? "";
                    else _path2 = root.GetProperty("value").GetString() ?? "";
                    break;
            }
        };
    }

    private Scintilla NewEditor(bool readOnly)
    {
        var ed = new Scintilla { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
        SqlScintilla.Configure(ed);
        ed.ScrollWidthTracking = true;
        if (readOnly)
        {
            ed.ReadOnly = true;
            ed.KeyDown += (_, e) =>
            {
                if (e.KeyCode != Keys.F8 || e.Control || e.Alt) return;
                e.Handled = e.SuppressKeyPress = true;
                GoToBlock(_currentBlock + (e.Shift ? -1 : 1));
            };
        }
        return ed;
    }

    private static Panel MakeLabeledPanel(Scintilla box, out Label lbl)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        lbl = new Label { Dock = DockStyle.Top, Height = 22, BackColor = AppColors.PanelAlt, ForeColor = AppColors.Text, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0), AutoEllipsis = true };
        panel.Controls.Add(box); panel.Controls.Add(lbl); return panel;
    }

    private void ChooseFile(int fileIndex, Scintilla contentBox)
    {
        using var ofd = new OpenFileDialog { Filter = "All files (*.*)|*.*|SQL files (*.sql)|*.sql" };
        if (ofd.ShowDialog() == DialogResult.OK)
        {
            try
            {
                contentBox.Text = File.ReadAllText(ofd.FileName);
                contentBox.EmptyUndoBuffer();
                var pathStr = JsonSerializer.Serialize(ofd.FileName);
                if (fileIndex == 1) { _path1 = ofd.FileName; _topBarWeb.Call($"window.setPaths({pathStr}, null)"); }
                else { _path2 = ofd.FileName; _topBarWeb.Call($"window.setPaths(null, {pathStr})"); }
                RefreshLanguages();
            }
            catch (Exception ex) { MessageBox.Show(this, "Không đọc được file: " + ex.Message); }
        }
    }

    // ---------------- ngôn ngữ tô màu ----------------

    private string LangFor(Scintilla box, string path) =>
        _langChoice != "auto" ? _langChoice : TextLanguageDetector.Detect(box.Text, path);

    /// <summary>Đoán lại ngôn ngữ 2 khung nhập (sau khi gõ / dán / chọn file, hoặc đổi ô "Tô màu"); đổi thì tô lại. Đang ở màn so sánh thì 2 khung so sánh theo luôn.</summary>
    private void RefreshLanguages()
    {
        var l1 = LangFor(_content1Box, _path1);
        var l2 = LangFor(_content2Box, _path2);
        if (l1 != _lang1) { _lang1 = l1; StyleEditor(_content1Box, l1, diff: false); }
        if (l2 != _lang2) { _lang2 = l2; StyleEditor(_content2Box, l2, diff: false); }
        if (_diffPnlLeft.Parent != null)
        {
            if (_diffLang1 != l1) { _diffLang1 = l1; StyleEditor(_diffLeftBox, l1, diff: true); }
            if (_diffLang2 != l2) { _diffLang2 = l2; StyleEditor(_diffRightBox, l2, diff: true); }
        }
        UpdateHeaders();
    }

    private void UpdateHeaders()
    {
        string Lang(string l) => l + (_langChoice == "auto" ? " (tự nhận)" : "");
        _lbl1.Text = "Content 1  —  " + Lang(_lang1);
        _lbl2.Text = "Content 2  —  " + Lang(_lang2);
        var del = _leftMarks.Count(m => m == RowMark.Changed);
        var ins = _rightMarks.Count(m => m == RowMark.Changed);
        _diffLbl1.Text = "Content 1  —  " + Lang(_diffLang1) + (del > 0 ? $"  ·  {del} dòng xoá/sửa" : "");
        _diffLbl2.Text = "Content 2  —  " + Lang(_diffLang2) + (ins > 0 ? $"  ·  {ins} dòng thêm/sửa" : "");
    }

    /// <summary>Màu chữ theo theme + ngôn ngữ (dựng lại từ đầu mỗi lần: bộ tô FCode và lexer Scintilla dùng chung số style nên không đè lên nhau được), rồi màu diff.</summary>
    private static void StyleEditor(Scintilla ed, string lang, bool diff)
    {
        SqlScintilla.ApplyTheme(ed);
        switch (lang)
        {
            case TextLanguageDetector.Xml: FcodeLexer.Apply(ed); break;      // XML + SQL/JS trong CDATA như File Lookup
            case TextLanguageDetector.Sql: FcodeLexer.ApplySql(ed); break;   // cùng bộ tô T-SQL của tab SQL Query
            case TextLanguageDetector.Text: FcodeLexer.Detach(ed); ed.Lexer = Lexer.Null; break;
            default: FcodeLexer.Detach(ed); SqlScintilla.SetLanguage(ed, lang); break;
        }
        if (diff) ApplyDiffStyles(ed);
        FcodeLexer.Restyle(ed);
    }

    private void ApplyThemeAll()
    {
        BackColor = AppColors.Background;
        foreach (var l in new[] { _lbl1, _lbl2, _diffLbl1, _diffLbl2 }) { l.BackColor = AppColors.PanelAlt; l.ForeColor = AppColors.Text; }
        StyleEditor(_content1Box, _lang1, diff: false);
        StyleEditor(_content2Box, _lang2, diff: false);
        StyleEditor(_diffLeftBox, _diffLang1.Length > 0 ? _diffLang1 : _lang1, diff: true);
        StyleEditor(_diffRightBox, _diffLang2.Length > 0 ? _diffLang2 : _lang2, diff: true);
        _overview.Invalidate();
    }

    // ---------------- màu diff ----------------

    // Marker 10..14 (25..31 là của folding); indicator 18 (16/17 = mục hay dùng của khung xem file); style lề 230/231 (ngoài dải style của lexer và 32..39).
    private const int MarkChanged = 10, MarkEmpty = 11, MarkChangedCur = 12, MarkEmptyCur = 13, MarkArrow = 14;
    private const int IndChanged = 18;
    private const int StyleNum = 230, StyleNumChanged = 231;

    private enum RowMark : byte { None, Changed, Empty }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    private static Color RemovedColor => AppColors.IsDark ? Color.FromArgb(240, 80, 80) : Color.FromArgb(220, 40, 40);
    private static Color AddedColor => AppColors.IsDark ? Color.FromArgb(70, 200, 100) : Color.FromArgb(30, 160, 60);

    /// <summary>Khung bên trái tô đỏ (bản cũ), bên phải tô xanh (bản mới): nền nhạt cả dòng khác, nền đậm hơn đúng những TỪ khác trong dòng sửa,
    /// số dòng trên nền màu, dòng đang đứng (◀ ▶ / F8) nền đậm hơn nữa + mũi tên ở lề. Dòng trống chèn cho thẳng hàng = nền xám.</summary>
    private static void ApplyDiffStyles(Scintilla ed)
    {
        var left = ed.Tag as string == "left";
        var dark = AppColors.IsDark;
        var bg = ed.Styles[Style.Default].BackColor;
        var c = left ? RemovedColor : AddedColor;
        ed.CaretLineVisible = false;   // nền dòng đang đứng đè mất nền diff

        ed.Markers[MarkChanged].Symbol = MarkerSymbol.Background; ed.Markers[MarkChanged].SetBackColor(Mix(bg, c, dark ? 0.17 : 0.13));
        ed.Markers[MarkChangedCur].Symbol = MarkerSymbol.Background; ed.Markers[MarkChangedCur].SetBackColor(Mix(bg, c, dark ? 0.30 : 0.24));
        var gray = Mix(bg, AppColors.TextMuted, dark ? 0.10 : 0.12);
        ed.Markers[MarkEmpty].Symbol = MarkerSymbol.Background; ed.Markers[MarkEmpty].SetBackColor(gray);
        ed.Markers[MarkEmptyCur].Symbol = MarkerSymbol.Background; ed.Markers[MarkEmptyCur].SetBackColor(Mix(bg, AppColors.TextMuted, dark ? 0.20 : 0.22));
        ed.Markers[MarkArrow].Symbol = MarkerSymbol.ShortArrow; ed.Markers[MarkArrow].SetBackColor(AppColors.Accent); ed.Markers[MarkArrow].SetForeColor(AppColors.Accent);

        var ind = ed.Indicators[IndChanged];
        ind.Style = IndicatorStyle.FullBox; ind.ForeColor = c; ind.Under = true;
        ind.Alpha = dark ? 105 : 80; ind.OutlineAlpha = dark ? 160 : 130;

        var num = ed.Styles[Style.LineNumber];
        ed.Styles[StyleNum].Font = num.Font; ed.Styles[StyleNum].Size = num.Size; ed.Styles[StyleNum].BackColor = num.BackColor; ed.Styles[StyleNum].ForeColor = num.ForeColor;
        ed.Styles[StyleNumChanged].Font = num.Font; ed.Styles[StyleNumChanged].Size = num.Size; ed.Styles[StyleNumChanged].Bold = true;
        ed.Styles[StyleNumChanged].BackColor = Mix(num.BackColor, c, dark ? 0.45 : 0.35); ed.Styles[StyleNumChanged].ForeColor = AppColors.Text;

        // Lề 0 = số dòng GỐC (dòng trống chèn thêm không có số) + dấu − / + / ~; lề 1 = mũi tên dòng đang đứng. Marker nền không được nằm trong mặt nạ
        // của lề nào đang hiện, không thì Scintilla vẽ nó ở lề thay vì làm nền dòng.
        ed.Margins[0].Type = MarginType.RightText; ed.Margins[0].Mask = 0;
        ed.Margins[0].Width = ed.TextWidth(StyleNumChanged, "99999 ~ ") + 4;
        ed.Margins[1].Type = MarginType.Symbol; ed.Margins[1].Mask = 1u << MarkArrow;
        ed.Margins[1].Width = ed.TextWidth(Style.LineNumber, "99") + 2;
        ed.Margins[2].Width = 0; ed.Margins[2].Mask = 0;
    }

    // ---------------- so sánh ----------------

    private readonly record struct Row(int? LineNo, string Text, RowMark Mark, char Sign, List<(int Start, int Length)>? Ranges = null);

    private void RunCompare()
    {
        RefreshLanguages();
        var diff = _diffService.Diff(_content1Box.Text, _content2Box.Text);
        var left = new List<Row>();
        var right = new List<Row>();
        _blockStarts.Clear(); // Nơi lưu trữ các điểm khác biệt
        int modified = 0, added = 0, removed = 0, hunks = 0;

        var i = 0;
        while (i < diff.Count)
        {
            if (diff[i].Kind == DiffKind.Equal)
            {
                left.Add(new Row(diff[i].LeftLineNo, diff[i].Text, RowMark.None, ' '));
                right.Add(new Row(diff[i].RightLineNo, diff[i].Text, RowMark.None, ' '));
                i++;
                continue;
            }

            var removedLines = new List<DiffLine>();
            var addedLines = new List<DiffLine>();
            while (i < diff.Count && diff[i].Kind != DiffKind.Equal)
            {
                if (diff[i].Kind == DiffKind.Removed) removedLines.Add(diff[i]);
                else addedLines.Add(diff[i]);
                i++;
            }
            hunks++;

            var max = Math.Max(removedLines.Count, addedLines.Count);
            for (var k = 0; k < max; k++)
            {
                // FCode đếm TỪNG DÒNG, nên mọi dòng khác đều vào danh sách điều hướng
                _blockStarts.Add(left.Count);

                var l = k < removedLines.Count ? removedLines[k] : null;
                var r = k < addedLines.Count ? addedLines[k] : null;
                if (l != null && r != null)
                {
                    // Dòng sửa: tô đúng những TỪ khác nhau ở cả 2 bên.
                    var (lr, rr) = DiffService.InlineDiff(l.Text, r.Text);
                    left.Add(new Row(l.LeftLineNo, l.Text, RowMark.Changed, '~', lr));
                    right.Add(new Row(r.RightLineNo, r.Text, RowMark.Changed, '~', rr));
                    modified++;
                }
                else if (l != null)
                {
                    // Chỉ có ở bên trái (bị xoá): bên phải chèn dòng trống để thẳng hàng.
                    left.Add(new Row(l.LeftLineNo, l.Text, RowMark.Changed, '−'));
                    right.Add(new Row(null, "", RowMark.Empty, ' '));
                    removed++;
                }
                else
                {
                    left.Add(new Row(null, "", RowMark.Empty, ' '));
                    right.Add(new Row(r!.RightLineNo, r.Text, RowMark.Changed, '+'));
                    added++;
                }
            }
        }

        _diffLang1 = _lang1; _diffLang2 = _lang2;
        StyleEditor(_diffLeftBox, _diffLang1, diff: true);
        StyleEditor(_diffRightBox, _diffLang2, diff: true);
        _leftRows = left; _rightRows = right;
        _leftMarginDone = new bool[left.Count]; _rightMarginDone = new bool[right.Count];
        _leftMarks = RenderSide(_diffLeftBox, left);
        _rightMarks = RenderSide(_diffRightBox, right);
        _overview.LeftMarks = _leftMarks; _overview.RightMarks = _rightMarks;
        _overview.LeftColor = RemovedColor; _overview.RightColor = AddedColor;

        _currentBlock = -1; _currentRow = -1;
        _summaryCounts = _blockStarts.Count == 0 ? "" : string.Join(", ", new[]
        {
            modified > 0 ? $"~{modified} dòng sửa" : null,
            added > 0 ? $"+{added} dòng thêm" : null,
            removed > 0 ? $"−{removed} dòng xoá" : null,
        }.Where(s => s != null)) + $" · {hunks} cụm";
        var summary = _blockStarts.Count == 0 ? "Nội dung hoàn toàn giống nhau." : $"{_blockStarts.Count} chỗ khác biệt ({_summaryCounts}).";
        ShowDiffMode(summary);
        UpdateHeaders();
        if (_blockStarts.Count > 0) GoToBlock(0);
        else { _diffLeftBox.FirstVisibleLine = 0; _diffRightBox.FirstVisibleLine = 0; FillMargins(); }
    }

    private void UpdateNavButtons(string summary)
    {
        _topBarWeb.Call($"window.setMode(true, {JsonSerializer.Serialize(summary)}, {(_blockStarts.Count > 0 ? "true" : "false")})");
    }

    /// <summary>Nạp 1 bên vào khung so sánh: chữ (dòng trống chèn thêm để 2 bên thẳng hàng), số dòng gốc + dấu ở lề, nền dòng, từ khác nhau.</summary>
    private static RowMark[] RenderSide(Scintilla ed, List<Row> rows)
    {
        var marks = new RowMark[rows.Count];
        ed.ReadOnly = false;
        ed.Text = string.Join("\n", rows.Select(r => r.Text));
        ed.ReadOnly = true;
        ed.EmptyUndoBuffer();
        ed.MarkerDeleteAll(-1);
        ed.IndicatorCurrent = IndChanged;
        ed.IndicatorClearRange(0, ed.TextLength);
        ed.DirectMessage(2536, IntPtr.Zero, IntPtr.Zero);   // SCI_MARGINTEXTCLEARALL

        var maxNo = rows.Count == 0 ? 0 : rows.Max(r => r.LineNo ?? 0);
        ed.Margins[0].Width = ed.TextWidth(StyleNumChanged, new string('9', Math.Max(3, maxNo.ToString().Length)) + " ~ ") + 4;
        for (var r = 0; r < rows.Count && r < ed.Lines.Count; r++)
        {
            var row = rows[r];
            var line = ed.Lines[r];
            marks[r] = row.Mark;
            if (row.Mark == RowMark.Changed) line.MarkerAdd(MarkChanged);
            else if (row.Mark == RowMark.Empty) line.MarkerAdd(MarkEmpty);
            if (row.Ranges is { Count: > 0 })
            {
                var start = line.Position;
                foreach (var (s, len) in row.Ranges) if (len > 0) ed.IndicatorFillRange(start + s, len);
            }
        }
        FcodeLexer.Restyle(ed);
        return marks;
    }

    /// <summary>Số dòng gốc + dấu ở lề chỉ ghi cho đoạn đang thấy (± 1 trang), lăn tới đâu ghi tới đó: mỗi lần ghi chữ lề Scintilla tốn vài chục µs
    /// nên ghi hết 2 bên của văn bản 10.000 dòng mất ~1,5 giây (đo bằng chương trình thử), còn ghi đoạn đang thấy thì không đáng kể.</summary>
    private void FillMargins()
    {
        Fill(_diffLeftBox, _leftRows, _leftMarginDone);
        Fill(_diffRightBox, _rightRows, _rightMarginDone);

        static void Fill(Scintilla ed, List<Row> rows, bool[] done)
        {
            var page = Math.Max(20, ed.LinesOnScreen);
            var to = Math.Min(Math.Min(rows.Count, done.Length), Math.Min(ed.Lines.Count, ed.FirstVisibleLine + 2 * page));
            for (var r = Math.Max(0, ed.FirstVisibleLine - page); r < to; r++)
            {
                if (done[r]) continue;
                done[r] = true;
                if (rows[r].LineNo is not int no) continue;
                var line = ed.Lines[r];
                line.MarginStyle = rows[r].Mark == RowMark.Changed ? StyleNumChanged : StyleNum;
                line.MarginText = no + " " + rows[r].Sign + " ";
            }
        }
    }

    private void GoToBlock(int index)
    {
        if (_blockStarts.Count == 0) return;
        _currentBlock = ((index % _blockStarts.Count) + _blockStarts.Count) % _blockStarts.Count;
        var row = _blockStarts[_currentBlock];
        SetCurrentRow(row);
        _syncingScroll = true;
        try
        {
            foreach (var box in new[] { _diffLeftBox, _diffRightBox })
                if (row < box.Lines.Count) box.GotoPosition(box.Lines[row].Position);
            var first = Math.Max(0, row - 3);
            _diffLeftBox.FirstVisibleLine = first; _diffRightBox.FirstVisibleLine = first;
            _diffLeftBox.XOffset = 0; _diffRightBox.XOffset = 0;
            // Từ khác nằm xa bên phải (dòng dài): lăn ngang cho thấy nó, bên kia lăn theo.
            var (ed, other, ranges) = _leftRows.ElementAtOrDefault(row).Ranges is { Count: > 0 } lr ? (_diffLeftBox, _diffRightBox, lr)
                : (_diffRightBox, _diffLeftBox, _rightRows.ElementAtOrDefault(row).Ranges);
            if (ranges is { Count: > 0 } && row < ed.Lines.Count)
            {
                var pos = ed.Lines[row].Position + ranges[0].Start;
                ed.ScrollRange(pos, pos + ranges[0].Length);
                other.XOffset = ed.XOffset;
            }
        }
        finally { _syncingScroll = false; }
        FillMargins();
        _overview.Current = row;
        _overview.Invalidate();
        UpdateNavButtons($"Khác biệt {_currentBlock + 1}/{_blockStarts.Count} ({_summaryCounts})");
    }

    /// <summary>Dòng đang đứng: nền đậm hơn + mũi tên ở lề, cả 2 bên.</summary>
    private void SetCurrentRow(int row)
    {
        void Mark(Scintilla ed, RowMark[] marks, int r, bool on)
        {
            if (r < 0 || r >= marks.Length || r >= ed.Lines.Count) return;
            var line = ed.Lines[r];
            line.MarkerDelete(-1);
            if (marks[r] == RowMark.Changed) line.MarkerAdd(on ? MarkChangedCur : MarkChanged);
            else if (marks[r] == RowMark.Empty) line.MarkerAdd(on ? MarkEmptyCur : MarkEmpty);
            if (on) line.MarkerAdd(MarkArrow);
        }
        Mark(_diffLeftBox, _leftMarks, _currentRow, false); Mark(_diffRightBox, _rightMarks, _currentRow, false);
        _currentRow = row;
        Mark(_diffLeftBox, _leftMarks, row, true); Mark(_diffRightBox, _rightMarks, row, true);
    }

    /// <summary>Lăn một bên thì bên kia lăn theo đúng cùng dòng (2 bên luôn cùng số dòng nhờ dòng trống chèn thêm) và cùng độ lệch ngang.</summary>
    private void OnDiffUpdateUI(Scintilla source, Scintilla target, UpdateUIEventArgs e)
    {
        if ((e.Change & (UpdateChange.VScroll | UpdateChange.HScroll)) == 0) return;
        _overview.Invalidate();
        if (_syncingScroll) return;
        _syncingScroll = true;
        try
        {
            if (target.FirstVisibleLine != source.FirstVisibleLine) target.FirstVisibleLine = source.FirstVisibleLine;
            if (target.XOffset != source.XOffset) target.XOffset = source.XOffset;
        }
        finally { _syncingScroll = false; }
        if ((e.Change & UpdateChange.VScroll) != 0) FillMargins();
    }

    /// <summary>Bấm thanh tổng quan: gần một vạch khác biệt thì nhảy tới chỗ đó (◀ ▶ đi tiếp từ đây), không thì lăn tới vị trí bấm; kéo = lăn theo.</summary>
    private void OnOverviewPicked(int row, int tolerance, bool click)
    {
        if (click && _blockStarts.Count > 0)
        {
            var best = -1;
            for (var k = 0; k < _blockStarts.Count; k++)
                if (Math.Abs(_blockStarts[k] - row) <= tolerance && (best < 0 || Math.Abs(_blockStarts[k] - row) < Math.Abs(_blockStarts[best] - row))) best = k;
            if (best >= 0) { GoToBlock(best); return; }
        }
        var first = Math.Max(0, row - _diffLeftBox.LinesOnScreen / 2);
        _syncingScroll = true;
        try { _diffLeftBox.FirstVisibleLine = first; _diffRightBox.FirstVisibleLine = first; }
        finally { _syncingScroll = false; }
        FillMargins();
        _overview.Invalidate();
    }

    private void ShowDiffMode(string summary)
    {
        _split.Panel1.Controls.Clear(); _split.Panel2.Controls.Clear(); _split.Panel1.Controls.Add(_diffPnlLeft); _split.Panel2.Controls.Add(_diffPnlRight);
        _overview.Visible = true;
        UpdateNavButtons(summary);
    }

    private void ShowEditMode()
    {
        _split.Panel1.Controls.Clear(); _split.Panel2.Controls.Clear(); _split.Panel1.Controls.Add(_pnlLeft); _split.Panel2.Controls.Add(_pnlRight);
        _overview.Visible = false;
        _topBarWeb.Call("window.setMode(false, '')");
    }

    /// <summary>Thanh tổng quan dọc bên phải màn so sánh: cả văn bản thu vào chiều cao thanh — nửa trái vạch đỏ (dòng xoá/sửa bên trái),
    /// nửa phải vạch xanh (dòng thêm/sửa bên phải), khung mờ = đoạn đang thấy, vạch ngang màu nhấn = chỗ khác đang đứng.</summary>
    private sealed class DiffOverviewBar : Control
    {
        public RowMark[] LeftMarks = Array.Empty<RowMark>(), RightMarks = Array.Empty<RowMark>();
        public Color LeftColor, RightColor;
        public int Current = -1;
        public Func<(int First, int Count)>? Viewport;
        /// <summary>(dòng, sai số theo dòng ứng với vài pixel, true = bấm / false = kéo).</summary>
        public event Action<int, int, bool>? RowPicked;

        public DiffOverviewBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Width = Math.Max(10, (int)Math.Round(14 * UiScale.Factor));
            Cursor = Cursors.Hand;
        }

        private int Rows => Math.Max(LeftMarks.Length, RightMarks.Length);
        // Văn bản ngắn hơn khung (vd 12 dòng): thanh ứng với số dòng THẤY ĐƯỢC để vạch nằm ngang đúng dòng của nó, không bị kéo giãn cả chiều cao.
        private int Span => Math.Max(Rows, Viewport?.Invoke().Count ?? 0);
        private int RowAt(int y) => Rows == 0 ? 0 : Math.Clamp((int)((long)y * Span / Math.Max(1, Height)), 0, Rows - 1);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(AppColors.PanelAlt);
            var n = Span;
            if (n == 0 || Height <= 0) return;
            float h = Height, per = h / n;
            if (Viewport?.Invoke() is { } vp && vp.Count < Rows)   // cả văn bản đã thấy hết thì khỏi vẽ khung
            {
                using var vb = new SolidBrush(Color.FromArgb(AppColors.IsDark ? 45 : 35, AppColors.Text));
                g.FillRectangle(vb, 0, vp.First * per, Width, Math.Max(6f, vp.Count * per));
            }
            var half = Width / 2f;
            void Side(RowMark[] marks, float x, Color color)
            {
                using var b = new SolidBrush(color);
                for (var r = 0; r < marks.Length; r++)
                {
                    if (marks[r] != RowMark.Changed) continue;
                    var s = r;
                    while (r + 1 < marks.Length && marks[r + 1] == RowMark.Changed) r++;
                    g.FillRectangle(b, x + 1, s * per, half - 2, Math.Max(2f, (r - s + 1) * per));
                }
            }
            Side(LeftMarks, 0, LeftColor);
            Side(RightMarks, half, RightColor);
            if (Current >= 0 && Current < n)
            {
                using var cb = new SolidBrush(AppColors.Accent);
                g.FillRectangle(cb, 0, Math.Min(h - 3, Current * per), Width, 3);
            }
            using var border = new Pen(AppColors.Border);
            g.DrawLine(border, 0, 0, 0, Height);
        }

        private int Tolerance => Rows == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(4.0 * Span / Math.Max(1, Height)));

        private int _downY = -1;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Rows == 0) return;
            _downY = e.Y;
            RowPicked?.Invoke(RowAt(e.Y), Tolerance, true);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            // Chỉ coi là kéo khi chuột đã đi quá vài pixel — không thì lần bấm vừa nhảy tới chỗ khác bị kéo lệch ngay.
            if (e.Button != MouseButtons.Left || Rows == 0 || _downY < 0 || Math.Abs(e.Y - _downY) < 3) return;
            _downY = int.MinValue / 2;
            RowPicked?.Invoke(RowAt(e.Y), Tolerance, false);
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _downY = -1; }
    }
}
public class CompareTextForm : Bcode.App.UI.DpiForm
{
    public CompareTextForm()
    {
        Text = "Compare Text";
        Width = 1100;
        Height = 720;
        MinimumSize = new Size(760, 440);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = AppColors.Background;

        var ctrl = new CompareTextControl { Dock = DockStyle.Fill };
        ctrl.FloatWindowRequested += () => { };
        Controls.Add(ctrl);

        ThemeManager.Apply(this);
    }
}
