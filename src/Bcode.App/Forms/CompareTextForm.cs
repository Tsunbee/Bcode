using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

/// <summary>
/// "Compare Text" — was a stub (RunCompare just did string.Equals and popped a MessageBox
/// saying "same"/"different", no actual diff). Now backed by the already-existing
/// DiffService/DiffLine (a proper LCS line diff — see Services/DiffService.cs, whose own doc
/// comment already said "for the Compare Text tool" but was never wired up anywhere), with a
/// side-by-side colored render similar in spirit to BcodeViewer's "Lịch sử file" diff view
/// (Web/history.js, a Monaco diff editor): removed lines highlighted on the left, added lines
/// on the right, aligned with blank filler rows so both sides line up, line numbers in the
/// gutter, synced scrolling between the two panes, and Prev/Next-difference navigation.
///
/// This is a NATIVE WinForms render (RichTextBox + manual per-line coloring), not an embedded
/// Monaco/WebView2 diff editor like BcodeViewer's — same underlying idea (aligned side-by-side,
/// colored, navigable) but without that extra infrastructure. A pixel-identical Monaco version
/// is possible later (Bcode.App already hosts WebView2 pages elsewhere, e.g.
/// WCommandTreeControl's wcommandbar.html) if that fidelity is ever actually needed.
/// </summary>
public class CompareTextControl : UserControl
{
    private readonly TextBox _file1Box;
    private readonly TextBox _file2Box;
    private readonly Button _btnBrowse1;
    private readonly Button _btnBrowse2;

    // Chế độ Edit: khung soạn thảo/dán nội dung thô.
    private readonly RichTextBox _content1Box;
    private readonly RichTextBox _content2Box;
    private readonly Panel _pnlLeft;
    private readonly Panel _pnlRight;

    // Chế độ Diff: 2 khung kết quả, chỉ đọc, có số dòng + tô màu, cuộn đồng bộ.
    private readonly SyncRichTextBox _diffLeftBox;
    private readonly SyncRichTextBox _diffRightBox;
    private readonly Panel _diffPnlLeft;
    private readonly Panel _diffPnlRight;
    private bool _syncingScroll;

    private readonly SplitContainer _split;
    private readonly ToolStripButton _btnEdit;
    private readonly ToolStripButton _btnPrev;
    private readonly ToolStripButton _btnNext;
    private readonly ToolStripLabel _summaryLabel;
    private readonly DiffService _diffService = new();

    private List<int> _blockStarts = new(); // chỉ số dòng (đã căn hàng) bắt đầu mỗi cụm khác biệt
    private int _currentBlock = -1;

    public event Action? FloatWindowRequested;

    public CompareTextControl()
    {
        Dock = DockStyle.Fill;
        BackColor = AppColors.Background;

        // ---- 1. Toolbar trên cùng (Float window, Compare, điều hướng diff) ----
        var toolStrip = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            BackColor = AppColors.PanelAlt
        };
        var btnFloat = new ToolStripButton("⧉ Float window") { ForeColor = AppColors.Text };
        btnFloat.Click += (_, _) => FloatWindowRequested?.Invoke();

        var btnCompare = new ToolStripButton("📄 Compare") { ForeColor = AppColors.Text };
        btnCompare.Click += (_, _) => RunCompare();

        _btnEdit = new ToolStripButton("✎ Sửa lại") { ForeColor = AppColors.Text, Visible = false };
        _btnEdit.Click += (_, _) => ShowEditMode();

        _btnPrev = new ToolStripButton("◀ Khác biệt trước") { ForeColor = AppColors.Text, Visible = false };
        _btnPrev.Click += (_, _) => GoToBlock(_currentBlock - 1);

        _btnNext = new ToolStripButton("Khác biệt tiếp ▶") { ForeColor = AppColors.Text, Visible = false };
        _btnNext.Click += (_, _) => GoToBlock(_currentBlock + 1);

        _summaryLabel = new ToolStripLabel("") { ForeColor = AppColors.TextMuted };

        toolStrip.Items.Add(btnFloat);
        toolStrip.Items.Add(btnCompare);
        toolStrip.Items.Add(_btnEdit);
        toolStrip.Items.Add(new ToolStripSeparator());
        toolStrip.Items.Add(_btnPrev);
        toolStrip.Items.Add(_btnNext);
        toolStrip.Items.Add(_summaryLabel);

        // ---- 2. Vùng Declare (File 1, File 2) ----
        var declarePanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 112,
            Padding = new Padding(12, 6, 12, 6),
            BackColor = AppColors.Panel
        };

        var lblDeclare = new Label
        {
            Text = "Declare",
            ForeColor = AppColors.Accent,
            Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
            Dock = DockStyle.Top,
            Height = 22
        };

        var declareTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 52,
            ColumnCount = 3,
            RowCount = 2
        };
        declareTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        declareTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        declareTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));

        _file1Box = new TextBox { Dock = DockStyle.Fill, BackColor = AppColors.Input, ForeColor = AppColors.Text };
        _file2Box = new TextBox { Dock = DockStyle.Fill, BackColor = AppColors.Input, ForeColor = AppColors.Text };

        _btnBrowse1 = new Button { Text = "...", Dock = DockStyle.Fill, BackColor = AppColors.Accent, ForeColor = AppColors.OnAccent, FlatStyle = FlatStyle.Flat };
        _btnBrowse2 = new Button { Text = "...", Dock = DockStyle.Fill, BackColor = AppColors.Accent, ForeColor = AppColors.OnAccent, FlatStyle = FlatStyle.Flat };

        _btnBrowse1.Click += (_, _) => ChooseFile(_file1Box, _content1Box);
        _btnBrowse2.Click += (_, _) => ChooseFile(_file2Box, _content2Box);

        declareTable.Controls.Add(new Label { Text = "File 1", ForeColor = AppColors.Text, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        declareTable.Controls.Add(_file1Box, 1, 0);
        declareTable.Controls.Add(_btnBrowse1, 2, 0);

        declareTable.Controls.Add(new Label { Text = "File 2", ForeColor = AppColors.Text, TextAlign = ContentAlignment.MiddleLeft }, 0, 1);
        declareTable.Controls.Add(_file2Box, 1, 1);
        declareTable.Controls.Add(_btnBrowse2, 2, 1);

        var lblHint = new Label
        {
            Text = "* If you have a text, you can skip entering the path",
            ForeColor = AppColors.Warning,
            Font = new Font(Font.FontFamily, 8f, FontStyle.Italic),
            Dock = DockStyle.Bottom,
            Height = 18
        };

        declarePanel.Controls.Add(lblHint);
        declarePanel.Controls.Add(declareTable);
        declarePanel.Controls.Add(lblDeclare);

        // ---- 3. Vùng nội dung: Edit (soạn thảo) hoặc Diff (kết quả), đổi qua lại bằng cách
        // gắn/gỡ control khỏi 2 panel của cùng 1 SplitContainer — giữ nguyên nội dung đã gõ
        // ở _content1Box/_content2Box khi đang xem Diff, để "Sửa lại" quay về không mất gì. ----
        _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 4 };

        _pnlLeft = MakeLabeledPanel("Content 1", out _content1Box);
        _pnlRight = MakeLabeledPanel("Content 2", out _content2Box);

        _diffPnlLeft = MakeLabeledDiffPanel("Content 1", out _diffLeftBox);
        _diffPnlRight = MakeLabeledDiffPanel("Content 2", out _diffRightBox);
        _diffLeftBox.UserScrolled += line => SyncScroll(_diffRightBox, line);
        _diffRightBox.UserScrolled += line => SyncScroll(_diffLeftBox, line);

        _split.Panel1.Controls.Add(_pnlLeft);
        _split.Panel2.Controls.Add(_pnlRight);

        _split.HandleCreated += (_, _) =>
        {
            try
            {
                if (_split.Width > 100)
                    _split.SplitterDistance = _split.Width / 2;
            }
            catch { }
        };

        Controls.Add(_split);
        Controls.Add(declarePanel);
        Controls.Add(toolStrip);
    }

    private Panel MakeLabeledPanel(string title, out RichTextBox box)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var lbl = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 22,
            BackColor = AppColors.PanelAlt,
            ForeColor = AppColors.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0)
        };
        box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = AppColors.Panel,
            ForeColor = AppColors.Text,
            Font = ThemeManager.MonoFont,
            BorderStyle = BorderStyle.None,
            WordWrap = false
        };
        panel.Controls.Add(box);
        panel.Controls.Add(lbl);
        return panel;
    }

    private Panel MakeLabeledDiffPanel(string title, out SyncRichTextBox box)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var lbl = new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 22,
            BackColor = AppColors.PanelAlt,
            ForeColor = AppColors.Text,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 0, 0, 0)
        };
        box = new SyncRichTextBox
        {
            Dock = DockStyle.Fill,
            BackColor = AppColors.Panel,
            ForeColor = AppColors.Text,
            Font = ThemeManager.MonoFont,
            BorderStyle = BorderStyle.None,
            WordWrap = false,
            ReadOnly = true
        };
        panel.Controls.Add(box);
        panel.Controls.Add(lbl);
        return panel;
    }

    private void ChooseFile(TextBox targetBox, RichTextBox contentBox)
    {
        using var ofd = new OpenFileDialog { Filter = "All files (*.*)|*.*|SQL files (*.sql)|*.sql" };
        if (ofd.ShowDialog() == DialogResult.OK)
        {
            targetBox.Text = ofd.FileName;
            try
            {
                contentBox.Text = File.ReadAllText(ofd.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Không đọc được file: " + ex.Message);
            }
        }
    }

    // ---- Diff: dựng cụm căn hàng trái/phải từ DiffService, tô màu, chuyển sang chế độ xem ----

    private readonly record struct RenderLine(int? LineNo, string Text, DiffKind Kind, bool Placeholder);

    private void RunCompare()
    {
        var text1 = _content1Box.Text;
        var text2 = _content2Box.Text;

        var diff = _diffService.Diff(text1, text2);

        var left = new List<RenderLine>();
        var right = new List<RenderLine>();
        _blockStarts.Clear();

        var i = 0;
        while (i < diff.Count)
        {
            if (diff[i].Kind == DiffKind.Equal)
            {
                left.Add(new RenderLine(diff[i].LeftLineNo, diff[i].Text, DiffKind.Equal, false));
                right.Add(new RenderLine(diff[i].RightLineNo, diff[i].Text, DiffKind.Equal, false));
                i++;
                continue;
            }

            // Gom 1 cụm khác biệt liên tiếp (Removed/Added xen nhau) rồi tách riêng 2 nhóm để
            // căn hàng cạnh nhau — dòng thiếu bên nào thì bên đó là dòng trống (Placeholder).
            _blockStarts.Add(left.Count);
            var removed = new List<DiffLine>();
            var added = new List<DiffLine>();
            while (i < diff.Count && diff[i].Kind != DiffKind.Equal)
            {
                if (diff[i].Kind == DiffKind.Removed) removed.Add(diff[i]);
                else added.Add(diff[i]);
                i++;
            }

            var max = Math.Max(removed.Count, added.Count);
            for (var k = 0; k < max; k++)
            {
                left.Add(k < removed.Count
                    ? new RenderLine(removed[k].LeftLineNo, removed[k].Text, DiffKind.Removed, false)
                    : new RenderLine(null, "", DiffKind.Equal, true));
                right.Add(k < added.Count
                    ? new RenderLine(added[k].RightLineNo, added[k].Text, DiffKind.Added, false)
                    : new RenderLine(null, "", DiffKind.Equal, true));
            }
        }

        RenderSide(_diffLeftBox, left);
        RenderSide(_diffRightBox, right);

        _currentBlock = -1;
        _summaryLabel.Text = _blockStarts.Count == 0
            ? "Nội dung hai bên hoàn toàn giống nhau."
            : $"{_blockStarts.Count} chỗ khác biệt.";

        ShowDiffMode();
        if (_blockStarts.Count > 0) GoToBlock(0);
    }

    private static string GutterPrefix(int? lineNo) =>
        (lineNo?.ToString() ?? "").PadLeft(5) + " │ ";

    private void RenderSide(RichTextBox box, List<RenderLine> rows)
    {
        box.Clear();
        var addedTint = Blend(AppColors.Success, AppColors.Panel, 0.75);
        var removedTint = Blend(AppColors.Danger, AppColors.Panel, 0.75);
        var placeholderTint = AppColors.PanelAlt;

        box.SuspendLayout();
        try
        {
            var sb = new System.Text.StringBuilder();
            for (var r = 0; r < rows.Count; r++)
            {
                sb.Append(GutterPrefix(rows[r].LineNo)).Append(rows[r].Text);
                if (r < rows.Count - 1) sb.Append('\n');
            }
            box.Text = sb.ToString();

            // Không tô màu nếu quá lớn — tránh đứng máy trên file vài chục nghìn dòng (tương
            // tự giới hạn MaxHighlightLength của SqlSyntaxHighlighter cho cùng lý do).
            if (box.TextLength > 400_000) return;

            // Lấy vị trí ký tự đầu dòng qua GetFirstCharIndexFromLine thay vì tự cộng dồn
            // độ dài + 1 cho mỗi '\n' — RichTextBox có thể lưu xuống dòng nội bộ dưới dạng
            // "\r\n" khác với chuỗi "\n" đã gán vào Text, nên tự cộng dồn dễ lệch vị trí tô
            // màu trên những dòng ở giữa/cuối file. GetFirstCharIndexFromLine luôn đúng vì nó
            // hỏi thẳng control, bất kể cách control lưu trữ nội bộ.
            for (var r = 0; r < rows.Count; r++)
            {
                var color = rows[r].Placeholder ? placeholderTint
                    : rows[r].Kind == DiffKind.Added ? addedTint
                    : rows[r].Kind == DiffKind.Removed ? removedTint
                    : box.BackColor;

                if (color == box.BackColor) continue;

                var start = box.GetFirstCharIndexFromLine(r);
                if (start < 0) continue;
                var lineLen = box.Lines.Length > r ? box.Lines[r].Length : 0;
                box.Select(start, lineLen);
                box.SelectionBackColor = color;
            }
            box.Select(0, 0);
        }
        finally
        {
            box.ResumeLayout();
        }
    }

    private static Color Blend(Color fg, Color bg, double fgWeight)
    {
        var r = (int)(fg.R * fgWeight + bg.R * (1 - fgWeight));
        var g = (int)(fg.G * fgWeight + bg.G * (1 - fgWeight));
        var b = (int)(fg.B * fgWeight + bg.B * (1 - fgWeight));
        return Color.FromArgb(r, g, b);
    }

    private void GoToBlock(int index)
    {
        if (_blockStarts.Count == 0) return;
        _currentBlock = ((index % _blockStarts.Count) + _blockStarts.Count) % _blockStarts.Count;
        var row = _blockStarts[_currentBlock];
        ScrollToRow(_diffLeftBox, row);
        ScrollToRow(_diffRightBox, row);
        _summaryLabel.Text = $"Khác biệt {_currentBlock + 1}/{_blockStarts.Count}";
    }

    private static void ScrollToRow(RichTextBox box, int row)
    {
        if (row < 0 || row >= box.Lines.Length) return;
        var idx = box.GetFirstCharIndexFromLine(row);
        if (idx < 0) return;
        box.Select(idx, 0);
        box.ScrollToCaret();
    }

    private void SyncScroll(SyncRichTextBox target, int firstVisibleLine)
    {
        if (_syncingScroll) return;
        _syncingScroll = true;
        try { ScrollToRow(target, firstVisibleLine); }
        finally { _syncingScroll = false; }
    }

    private void ShowDiffMode()
    {
        _split.Panel1.Controls.Clear();
        _split.Panel2.Controls.Clear();
        _split.Panel1.Controls.Add(_diffPnlLeft);
        _split.Panel2.Controls.Add(_diffPnlRight);
        _btnEdit.Visible = true;
        _btnPrev.Visible = true;
        _btnNext.Visible = true;
    }

    private void ShowEditMode()
    {
        _split.Panel1.Controls.Clear();
        _split.Panel2.Controls.Clear();
        _split.Panel1.Controls.Add(_pnlLeft);
        _split.Panel2.Controls.Add(_pnlRight);
        _btnEdit.Visible = false;
        _btnPrev.Visible = false;
        _btnNext.Visible = false;
        _summaryLabel.Text = "";
    }

    /// <summary>RichTextBox that reports its own first-visible-line whenever the user
    /// scrolls (mouse wheel, scrollbar, or keyboard) — WinForms' RichTextBox has no native
    /// Scroll event, so this catches the relevant window messages instead. Used to keep the
    /// two diff panes' scroll position in lockstep.</summary>
    private sealed class SyncRichTextBox : RichTextBox
    {
        public event Action<int>? UserScrolled;

        private const int WM_VSCROLL = 0x0115;
        private const int WM_MOUSEWHEEL = 0x020A;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_VSCROLL || m.Msg == WM_MOUSEWHEEL)
            {
                var idx = GetCharIndexFromPosition(new Point(1, 1));
                UserScrolled?.Invoke(GetLineFromCharIndex(idx));
            }
        }
    }
}

/// <summary>
/// Cửa sổ Form Compare Text độc lập để tương thích với các lệnh mở Form truyền thống
/// </summary>
public class CompareTextForm : Form
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