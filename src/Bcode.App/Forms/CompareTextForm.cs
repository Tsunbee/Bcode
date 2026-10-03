using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

public class CompareTextControl : UserControl
{
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _topBarWeb = new();

    private readonly RichTextBox _content1Box;
    private readonly RichTextBox _content2Box;
    private readonly Panel _pnlLeft;
    private readonly Panel _pnlRight;

    private readonly SyncRichTextBox _diffLeftBox;
    private readonly SyncRichTextBox _diffRightBox;
    private readonly Panel _diffPnlLeft;
    private readonly Panel _diffPnlRight;
    private bool _syncingScroll;

    private readonly SplitContainer _split;
    private readonly DiffService _diffService = new();

    private List<int> _blockStarts = new();
    private int _currentBlock = -1;
    private string _path1 = "";
    private string _path2 = "";

    public event Action? FloatWindowRequested;

    public CompareTextControl()
    {
        Dock = DockStyle.Fill;
        BackColor = AppColors.Background;

        _topBarWeb.Dock = DockStyle.Top;
        _topBarWeb.Height = 85; 

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
        Controls.Add(_topBarWeb);
        _ = InitWebViewAsync();
    }

    private async Task InitWebViewAsync()
    {
        await Bcode.App.UI.WebViewEnvironment.InitAsync(_topBarWeb);
        const string host = Bcode.App.UI.WebViewEnvironment.Host;

        _topBarWeb.CoreWebView2.WebMessageReceived += (_, e) =>
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = doc.RootElement;
            switch (root.GetProperty("action").GetString())
            {
                case "float": FloatWindowRequested?.Invoke(); break;
                case "compare": RunCompare(); break;
                case "edit": ShowEditMode(); break;
                case "prev": GoToBlock(_currentBlock - 1); break;
                case "next": GoToBlock(_currentBlock + 1); break;
                case "browse1": ChooseFile(1, _content1Box); break;
                case "browse2": ChooseFile(2, _content2Box); break;
                case "update-path":
                    if (root.GetProperty("which").GetString() == "file1") _path1 = root.GetProperty("value").GetString() ?? "";
                    else _path2 = root.GetProperty("value").GetString() ?? "";
                    break;
            }
        };

        _topBarWeb.CoreWebView2.NavigationCompleted += (_, _) =>
        {
            var isDark = AppColors.IsDark ? "true" : "false";
            _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
        };

        _topBarWeb.CoreWebView2.Navigate($"https://{host}/comparebar.html");
    }

    private Panel MakeLabeledPanel(string title, out RichTextBox box)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var lbl = new Label { Text = title, Dock = DockStyle.Top, Height = 22, BackColor = AppColors.PanelAlt, ForeColor = AppColors.Text, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
        box = new RichTextBox { Dock = DockStyle.Fill, BackColor = AppColors.Panel, ForeColor = AppColors.Text, Font = ThemeManager.MonoFont, BorderStyle = BorderStyle.None, WordWrap = false };
        panel.Controls.Add(box); panel.Controls.Add(lbl); return panel;
    }

    private Panel MakeLabeledDiffPanel(string title, out SyncRichTextBox box)
    {
        var panel = new Panel { Dock = DockStyle.Fill };
        var lbl = new Label { Text = title, Dock = DockStyle.Top, Height = 22, BackColor = AppColors.PanelAlt, ForeColor = AppColors.Text, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(4, 0, 0, 0) };
        box = new SyncRichTextBox { Dock = DockStyle.Fill, BackColor = AppColors.Panel, ForeColor = AppColors.Text, Font = ThemeManager.MonoFont, BorderStyle = BorderStyle.None, WordWrap = false, ReadOnly = true };
        panel.Controls.Add(box); panel.Controls.Add(lbl); return panel;
    }

    private void ChooseFile(int fileIndex, RichTextBox contentBox)
    {
        using var ofd = new OpenFileDialog { Filter = "All files (*.*)|*.*|SQL files (*.sql)|*.sql" };
        if (ofd.ShowDialog() == DialogResult.OK)
        {
            try
            {
                contentBox.Text = File.ReadAllText(ofd.FileName);
                var pathStr = $"\"{ofd.FileName.Replace("\\", "\\\\")}\""; 
                if (fileIndex == 1) { _path1 = ofd.FileName; _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setPaths({pathStr}, null)"); }
                else { _path2 = ofd.FileName; _topBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setPaths(null, {pathStr})"); }
            }
            catch (Exception ex) { MessageBox.Show(this, "Không đọc được file: " + ex.Message); }
        }
    }

    // THÊM BIẾN LƯU VỊ TRÍ KÝ TỰ BỊ THAY ĐỔI
    private readonly record struct RenderLine(int? LineNo, string Text, DiffKind Kind, bool Placeholder, int? HighlightStart = null, int? HighlightLen = null);

private void RunCompare()
    {
        var diff = _diffService.Diff(_content1Box.Text, _content2Box.Text);
        var left = new List<RenderLine>();
        var right = new List<RenderLine>();
        _blockStarts.Clear(); // Nơi lưu trữ các điểm khác biệt

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
                // BẢN FIX: FCode đếm TỪNG DÒNG, nên ta thêm mọi dòng vào danh sách điều hướng
                _blockStarts.Add(left.Count);

                string? leftText = k < removed.Count ? removed[k].Text : null;
                string? rightText = k < added.Count ? added[k].Text : null;

                int? leftStart = null, leftLen = null, rightStart = null, rightLen = null;

                // THUẬT TOÁN INLINE-DIFF (Chỉ ra chính xác Text gì đang khác biệt)
                if (leftText != null && rightText != null)
                {
                    int pre = 0;
                    while (pre < leftText.Length && pre < rightText.Length && leftText[pre] == rightText[pre]) pre++;
                    int suf = 0;
                    while (suf < leftText.Length - pre && suf < rightText.Length - pre && leftText[leftText.Length - 1 - suf] == rightText[rightText.Length - 1 - suf]) suf++;

                    // Chống lỗi dính chữ khi từ quá ngắn
                    if (pre + suf > leftText.Length) suf = leftText.Length - pre;
                    if (pre + suf > rightText.Length) suf = rightText.Length - pre;

                    leftStart = pre; leftLen = leftText.Length - pre - suf;
                    rightStart = pre; rightLen = rightText.Length - pre - suf;
                }

                left.Add(leftText != null ? new RenderLine(removed[k].LeftLineNo, leftText, DiffKind.Removed, false, leftStart, leftLen) : new RenderLine(null, "", DiffKind.Equal, true));
                right.Add(rightText != null ? new RenderLine(added[k].RightLineNo, rightText, DiffKind.Added, false, rightStart, rightLen) : new RenderLine(null, "", DiffKind.Equal, true));
            }
        }

        RenderSide(_diffLeftBox, left);
        RenderSide(_diffRightBox, right);

        _currentBlock = -1;
        var summary = _blockStarts.Count == 0 ? "Nội dung hoàn toàn giống nhau." : $"{_blockStarts.Count} chỗ khác biệt.";
        ShowDiffMode(summary);
        if (_blockStarts.Count > 0) GoToBlock(0);
    }

    private void UpdateNavButtons(string summary)
    {
        if (_topBarWeb.CoreWebView2 != null)
        {
            var script = $@"
                window.setMode(true, '{summary}');
                var showNav = {_blockStarts.Count} > 0; // Đổi thành > 0 để luôn hiện nút Next/Prev nếu có khác biệt
                var btnPrev = document.getElementById('btnPrev');
                var btnNext = document.getElementById('btnNext');
                if (btnPrev) btnPrev.style.display = showNav ? '' : 'none';
                if (btnNext) btnNext.style.display = showNav ? '' : 'none';
            ";
            _topBarWeb.CoreWebView2.ExecuteScriptAsync(script);
        }
    }
    private static string GutterPrefix(int? lineNo) => (lineNo?.ToString() ?? "").PadLeft(5) + " │ ";

    private void RenderSide(RichTextBox box, List<RenderLine> rows)
    {
        box.Clear();
        // Màu nhạt cho nguyên dòng
        var addedTint = Blend(AppColors.Success, AppColors.Panel, 0.15);
        var removedTint = Blend(AppColors.Danger, AppColors.Panel, 0.15);
        
        // Màu đậm hơn để focus chính xác vào từng ký tự bị sửa (Inline Diff)
        var addedDeep = Blend(AppColors.Success, AppColors.Panel, 0.45);
        var removedDeep = Blend(AppColors.Danger, AppColors.Panel, 0.45);
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

            if (box.TextLength > 400_000) return;

            for (var r = 0; r < rows.Count; r++)
            {
                var color = rows[r].Placeholder ? placeholderTint : rows[r].Kind == DiffKind.Added ? addedTint : rows[r].Kind == DiffKind.Removed ? removedTint : box.BackColor;
                if (color == box.BackColor) continue;

                var start = box.GetFirstCharIndexFromLine(r);
                if (start < 0) continue;
                
                // 1. Tô màu nhạt toàn dòng
                box.Select(start, box.Lines.Length > r ? box.Lines[r].Length : 0);
                box.SelectionBackColor = color;

                // 2. Tô màu đậm chính xác từ bị thay đổi
                if (rows[r].HighlightStart.HasValue && rows[r].HighlightLen.HasValue && rows[r].HighlightLen.Value > 0)
                {
                    var prefixLen = GutterPrefix(rows[r].LineNo).Length;
                    box.Select(start + prefixLen + rows[r].HighlightStart.Value, rows[r].HighlightLen.Value);
                    box.SelectionBackColor = rows[r].Kind == DiffKind.Added ? addedDeep : removedDeep;
                }
            }
            box.Select(0, 0);
        }
        finally { box.ResumeLayout(); }
    }

    private static Color Blend(Color fg, Color bg, double fgWeight)
    {
        return Color.FromArgb((int)(fg.R * fgWeight + bg.R * (1 - fgWeight)), (int)(fg.G * fgWeight + bg.G * (1 - fgWeight)), (int)(fg.B * fgWeight + bg.B * (1 - fgWeight)));
    }


    private void GoToBlock(int index)
    {
        if (_blockStarts.Count == 0) return;
        _currentBlock = ((index % _blockStarts.Count) + _blockStarts.Count) % _blockStarts.Count;
        var row = _blockStarts[_currentBlock];
        ScrollToRow(_diffLeftBox, row);
        ScrollToRow(_diffRightBox, row);
        UpdateNavButtons($"Khác biệt {_currentBlock + 1}/{_blockStarts.Count}");
    }

    private static void ScrollToRow(RichTextBox box, int row) { if (row >= 0 && row < box.Lines.Length && box.GetFirstCharIndexFromLine(row) >= 0) { box.Select(box.GetFirstCharIndexFromLine(row), 0); box.ScrollToCaret(); } }
    private void SyncScroll(SyncRichTextBox target, int firstVisibleLine) { if (_syncingScroll) return; _syncingScroll = true; try { ScrollToRow(target, firstVisibleLine); } finally { _syncingScroll = false; } }
    private void ShowDiffMode(string summary) { _split.Panel1.Controls.Clear(); _split.Panel2.Controls.Clear(); _split.Panel1.Controls.Add(_diffPnlLeft); _split.Panel2.Controls.Add(_diffPnlRight); UpdateNavButtons(summary); }
    private void ShowEditMode() { _split.Panel1.Controls.Clear(); _split.Panel2.Controls.Clear(); _split.Panel1.Controls.Add(_pnlLeft); _split.Panel2.Controls.Add(_pnlRight); _topBarWeb.CoreWebView2?.ExecuteScriptAsync("window.setMode(false, '')"); }

    private sealed class SyncRichTextBox : RichTextBox
    {
        public event Action<int>? UserScrolled;
        protected override void WndProc(ref Message m) { base.WndProc(ref m); if (m.Msg == 0x0115 || m.Msg == 0x020A) UserScrolled?.Invoke(GetLineFromCharIndex(GetCharIndexFromPosition(new Point(1, 1)))); }
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