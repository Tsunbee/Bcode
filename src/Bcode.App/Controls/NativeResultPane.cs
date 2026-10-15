using System.Data;
using System.Text.RegularExpressions;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Vùng kết quả của tab SQL Query ở chế độ App/WinForms — cùng bố cục với <see cref="SqlResultTabs"/> (Grid Result | Message) nhưng toàn bộ là control WinForms:
/// MỌI bảng kết quả xếp chồng như trang web (mỗi bảng 1 thẻ "Bảng N · n dòng", thu gọn được; bảng nhỏ cao vừa số dòng, bảng nhiều dòng nhất lấp phần còn lại,
/// không đủ chỗ thì cuộn) — trước đây 1 lưới dùng chung + nút đổi bảng nên chỉ thấy 1 bảng mỗi lúc. Một ô Message tô màu (lỗi đỏ, PRINT màu nhấn;
/// bấm vào dòng "Lỗi ở dòng N" để nhảy tới dòng N trong editor). Lưới dùng lại <see cref="GridDisplayHelper"/> + <see cref="ResultGridMenu"/> như các lưới native khác.
/// Chưa có tab Pivot (chế độ Web mới có).
/// </summary>
public sealed class NativeResultPane : UserControl
{
    private readonly BarFlow _strip = new() { Dock = DockStyle.Top, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 4, 6, 4) };
    private readonly PillButton _btnGrid, _btnMsg, _btnPivot, _mStack, _mTabs;
    private readonly BarFlow _tabPills = new() { WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(10, 0, 0, 0), BackColor = Color.Transparent };
    private string _mode = "stack";   // "stack" = xếp chồng mọi bảng; "tabs" = từng tab (như resultview.html: nút Xếp chồng / Từng tab)
    private int _activeTable;
    // Tab Pivot: dùng nguyên trang sqlpivot.html (WebView2) — chỉ tạo khi bấm Pivot lần đầu (chế độ App không dựng WebView nếu không cần).
    private WebBarHost? _pivotHost;
    private string? _pivotJson;
    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    /// <summary>Bấm "Tạo file Excel pivot…" trong tab Pivot.</summary>
    public event Action? PivotMakeRequested;
    private readonly Panel _stack = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(6) };
    private readonly List<(Panel Card, DataGridView Grid, PillButton Toggle, int Rows)> _cards = new();
    private readonly HashSet<int> _collapsed = new();
    private readonly ToolTip _tip = new();   // dùng chung (mỗi ToolTip là 1 cửa sổ Windows — tạo mới mỗi lần chạy SQL là rò handle)
    private readonly Dictionary<int, int> _manual = new();   // chiều cao người dùng tự kéo (như .rsz của trang web); bấm đúp dải kéo = về tự động
    private readonly RichTextBox _msg = new() { Dock = DockStyle.Fill, ReadOnly = true, Visible = false, DetectUrls = false, WordWrap = false, ScrollBars = RichTextBoxScrollBars.Both };
    private List<DataTable> _tables = new();
    private string _active = "grid";
    private int _lines;
    private string _kind = "none";

    /// <summary>Bấm dòng lỗi "Lỗi ở dòng N…" ở tab Message: nhảy tới dòng N trong editor.</summary>
    public event Action<int>? GotoLineRequested;

    public int TableCount => _tables.Count;

    public NativeResultPane()
    {
        Dock = DockStyle.Fill;
        _btnGrid = Tab("Grid Result", () => Select("grid"));
        _btnMsg = Tab("Message", () => Select("msg"));
        _btnPivot = Tab("Pivot", () => Select("pivot")); _btnPivot.Visible = false;
        _mStack = Tab("Xếp chồng", () => SetMode("stack")); _mTabs = Tab("Từng tab", () => SetMode("tabs"));
        _mStack.Margin = new Padding(14, 0, 2, 0);
        _strip.Controls.Add(_btnGrid);
        _strip.Controls.Add(_btnMsg);
        _strip.Controls.Add(_btnPivot);
        _strip.Controls.Add(_mStack);
        _strip.Controls.Add(_mTabs);
        _strip.Controls.Add(_tabPills);
        _stack.Resize += (_, _) => LayoutCards();

        _msg.Font = ThemeManager.MonoFont;
        _msg.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) TryGoto(_msg.GetCharIndexFromPosition(e.Location)); };

        Controls.Add(_stack);
        Controls.Add(_msg);
        Controls.Add(_strip);
        ThemeManager.ThemeChanged += Restyle;
        Disposed += (_, _) => { ThemeManager.ThemeChanged -= Restyle; _tip.Dispose(); };
        UpdateStrip();
    }

    private PillButton Tab(string text, System.Action onClick)
    {
        var b = PillButton.Flat(text);
        b.OnPanelAlt = true;
        b.Margin = new Padding(0, 0, 4, 0);
        b.Height = NativeSkin.RowHeight(this, 24);
        b.Click += (_, _) => onClick();
        return b;
    }

    private void Restyle() { if (_msg.TextLength > 0) Invalidate(true); }

    public void Select(string id)
    {
        _active = id == "msg" ? "msg" : id == "pivot" && _pivotJson is not null ? "pivot" : "grid";
        if (_active == "pivot" && _pivotHost is null)
        {
            _pivotHost = new WebBarHost("sqlpivot.html") { Dock = DockStyle.Fill, Visible = false };
            _pivotHost.Message += root => { if (root.TryGetProperty("action", out var a) && a.GetString() == "make") PivotMakeRequested?.Invoke(); };
            _pivotHost.Ready += PushPivot;
            Controls.Add(_pivotHost); _pivotHost.BringToFront(); _strip.SendToBack();
        }
        _stack.Visible = _active == "grid";
        _msg.Visible = _active == "msg";
        if (_pivotHost is not null) _pivotHost.Visible = _active == "pivot";
        UpdateStrip();
    }

    private void UpdateStrip()
    {
        _btnGrid.Text = _tables.Count > 0 ? $"Grid Result ({_tables.Count})" : "Grid Result";
        _btnMsg.Text = _lines > 0 ? $"Message ({_lines})" : "Message";
        _btnGrid.IsPrimary = _active == "grid";
        _btnMsg.IsPrimary = _active == "msg";
        _btnMsg.ForeOverride = _active != "msg" && _kind == "error" ? AppColors.Danger : null;
        _btnPivot.Visible = _pivotJson is not null;
        _btnPivot.IsPrimary = _active == "pivot";
        var multi = _tables.Count > 1 && _active == "grid";
        _mStack.Visible = _mTabs.Visible = multi;
        _mStack.IsPrimary = _mode == "stack"; _mTabs.IsPrimary = _mode == "tabs";
        _tabPills.Visible = multi && _mode == "tabs";
        for (var i = 0; i < _tabPills.Controls.Count; i++) if (_tabPills.Controls[i] is PillButton pb) { pb.IsPrimary = i == _activeTable; pb.Invalidate(); }
        foreach (var b in new[] { _btnGrid, _btnMsg, _btnPivot, _mStack, _mTabs }) b.Invalidate();
    }

    private void SetMode(string mode) { _mode = mode; LayoutCards(); UpdateStrip(); }
    private void ShowTable(int i) { _activeTable = i; LayoutCards(); UpdateStrip(); }

    /// <summary>Tab Pivot: <paramref name="payload"/> = null khi kết quả không phải báo cáo pivot (ẩn nút Pivot) — như SqlResultTabs.SetPivot của chế độ Web.</summary>
    public void SetPivot(Bcode.App.Services.Rpt.PivotPayload? payload)
    {
        _pivotJson = payload is null ? null : System.Text.Json.JsonSerializer.Serialize(payload, JsonOpts);
        if (_pivotJson is null && _active == "pivot") Select("grid");
        PushPivot();
        UpdateStrip();
    }

    private void PushPivot() { if (_pivotJson is not null && _pivotHost is not null) _pivotHost.Call($"window.sqlPivot && sqlPivot.set({_pivotJson})"); }

    public void SetTables(List<DataTable> tables)
    {
        _tables = tables;
        _collapsed.Clear(); _manual.Clear();
        _activeTable = 0;
        _tabPills.SuspendLayout();
        _tabPills.Controls.Clear();
        for (var i = 0; i < tables.Count && tables.Count > 1; i++) { var idx = i; _tabPills.Controls.Add(Tab($"Bảng {i + 1} · {tables[i].Rows.Count:N0} dòng", () => ShowTable(idx))); }
        _tabPills.ResumeLayout(true);
        BuildCards();
        UpdateStrip();
    }

    /// <summary>Mỗi bảng 1 thẻ: thanh tiêu đề (Bảng N · n dòng, nút thu gọn) + lưới. 1 bảng thì lưới lấp cả vùng (không có thanh tiêu đề, như trước).</summary>
    private void BuildCards()
    {
        _stack.SuspendLayout();
        foreach (var c in _cards) { c.Grid.DataSource = null; c.Card.Dispose(); }
        _cards.Clear();
        _stack.Controls.Clear();
        for (var i = 0; i < _tables.Count; i++)
        {
            var idx = i;
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect, ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText, BorderStyle = BorderStyle.None };
            ResultGridMenu.Attach(grid);
            var card = new Panel { Dock = DockStyle.Top, Padding = new Padding(1) };
            PillButton? toggle = null;
            if (_tables.Count > 1)
            {
                var head = new BarFlow { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(6, 3, 6, 3) };
                toggle = Tab("▾", () => { if (!_collapsed.Remove(idx)) _collapsed.Add(idx); LayoutCards(); });
                toggle.AutoWidth = false; toggle.Width = NativeSkin.S(30);
                var title = new Label { Text = $"Bảng {i + 1} · {_tables[i].Rows.Count:N0} dòng · {_tables[i].Columns.Count} cột", AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(4, 7, 0, 0), UseMnemonic = false };
                head.Controls.Add(toggle); head.Controls.Add(title);
                card.Controls.Add(grid); card.Controls.Add(head); card.Controls.Add(Grip(card, idx));
            }
            else card.Controls.Add(grid);
            _cards.Add((card, grid, toggle!, _tables[i].Rows.Count));
        }
        // Dock Top: control thêm SAU nằm TRÊN → thêm ngược (bảng 1 ở trên cùng), xen khe 8px.
        for (var i = _cards.Count - 1; i >= 0; i--)
        {
            _stack.Controls.Add(_cards[i].Card);
            if (i > 0) _stack.Controls.Add(new Panel { Dock = DockStyle.Top, Height = NativeSkin.S(8) });
        }
        ThemeManager.Apply(_stack);
        for (var i = 0; i < _cards.Count; i++) GridDisplayHelper.BindOptimized(_cards[i].Grid, _tables[i]);
        _stack.ResumeLayout(true);
        LayoutCards();
    }

    /// <summary>Chia chiều cao như trang web: 1 bảng = lấp đầy; nhiều bảng = mỗi bảng cao vừa đủ số dòng (tối đa ~60% khung), bảng nhiều dòng nhất
    /// nhận phần còn lại (tối thiểu ~200px); tổng vượt khung thì vùng kết quả cuộn. Thẻ thu gọn chỉ còn thanh tiêu đề.</summary>
    private void LayoutCards()
    {
        if (_cards.Count == 0) return;
        if (_cards.Count == 1) { _cards[0].Card.Dock = DockStyle.Fill; return; }
        if (_mode == "tabs")
        {
            // Từng tab: chỉ bảng đang chọn, lấp đầy khung (chọn bảng bằng dải nút "Bảng N · n dòng" trên thanh).
            _stack.SuspendLayout();
            for (var i = 0; i < _cards.Count; i++)
            {
                var c = _cards[i];
                c.Card.Visible = i == _activeTable;
                c.Grid.Visible = true;
                if (i == _activeTable) c.Card.Dock = DockStyle.Fill;
            }
            foreach (Control gap in _stack.Controls) if (gap is Panel p && !_cards.Any(c => c.Card == p)) p.Visible = false;
            _stack.ResumeLayout(true);
            return;
        }
        foreach (Control gap in _stack.Controls) gap.Visible = true;
        var avail = Math.Max(NativeSkin.S(200), _stack.ClientSize.Height - _stack.Padding.Vertical - (_cards.Count - 1) * NativeSkin.S(8));
        var heights = new int[_cards.Count];
        var big = 0;
        for (var i = 1; i < _cards.Count; i++) if (_cards[i].Rows > _cards[big].Rows) big = i;
        for (var i = 0; i < _cards.Count; i++)
        {
            var (card, grid, toggle, rows) = _cards[i];
            var head = card.Controls.Count > 1 ? card.Controls[1].Height : 0;
            if (_collapsed.Contains(i)) { heights[i] = head + 2; continue; }
            if (_manual.TryGetValue(i, out var mh)) { heights[i] = mh; continue; }
            var natural = head + grid.ColumnHeadersHeight + Math.Max(1, rows) * grid.RowTemplate.Height + SystemInformation.HorizontalScrollBarHeight + 6 + NativeSkin.S(7);
            heights[i] = Math.Min(natural, Math.Max(NativeSkin.S(120), (int)(avail * 0.6)));
        }
        if (!_collapsed.Contains(big) && !_manual.ContainsKey(big))
        {
            var others = heights.Where((_, k) => k != big).Sum();
            var (_, g, _, r) = _cards[big];
            var natural = (_cards[big].Card.Controls.Count > 1 ? _cards[big].Card.Controls[1].Height : 0) + g.ColumnHeadersHeight + Math.Max(1, r) * g.RowTemplate.Height + SystemInformation.HorizontalScrollBarHeight + 6 + NativeSkin.S(7);
            heights[big] = Math.Min(natural, Math.Max(NativeSkin.S(200), avail - others));
        }
        _stack.SuspendLayout();
        for (var i = 0; i < _cards.Count; i++)
        {
            var c = _cards[i];
            c.Card.Dock = DockStyle.Top; c.Card.Visible = true;
            if (c.Card.Height != heights[i]) c.Card.Height = heights[i];
            c.Grid.Visible = !_collapsed.Contains(i);
            if (c.Toggle is not null) { c.Toggle.Text = _collapsed.Contains(i) ? "▸" : "▾"; c.Toggle.Invalidate(); }
        }
        _stack.ResumeLayout(true);
    }

    /// <summary>Dải kéo ở mép dưới thẻ bảng: kéo lên/xuống đổi chiều cao bảng đó (vùng kết quả cuộn nếu tổng vượt khung); bấm đúp = trả về tự động.</summary>
    private Control Grip(Panel card, int idx)
    {
        var g = new Panel { Dock = DockStyle.Bottom, Height = NativeSkin.S(7), Cursor = Cursors.SizeNS };
        var dragging = false; var startY = 0; var startH = 0;
        g.Paint += (_, e) =>
        {
            using var pen = new Pen(AppColors.Border, 2);
            var cx = g.Width / 2; var cy = g.Height / 2;
            e.Graphics.DrawLine(pen, cx - NativeSkin.S(18), cy, cx + NativeSkin.S(18), cy);
        };
        g.MouseDown += (_, e) => { if (e.Button != MouseButtons.Left) return; dragging = true; startY = Cursor.Position.Y; startH = card.Height; g.Capture = true; };
        g.MouseMove += (_, _) =>
        {
            if (!dragging) return;
            var head = card.Controls.Count > 1 ? card.Controls[1].Height : 0;
            var h = Math.Max(head + NativeSkin.S(60), startH + Cursor.Position.Y - startY);
            _manual[idx] = h; _collapsed.Remove(idx);
            if (card.Height != h) card.Height = h;
        };
        g.MouseUp += (_, _) => { if (!dragging) return; dragging = false; g.Capture = false; LayoutCards(); };
        g.DoubleClick += (_, _) => { _manual.Remove(idx); LayoutCards(); };
        _tip.SetToolTip(g, "Kéo để đổi chiều cao bảng · bấm đúp để về tự động");
        return g;
    }

    public void Clear()
    {
        _tables = new();
        _collapsed.Clear(); _manual.Clear();
        BuildCards();
        UpdateStrip();
    }

    /// <summary>Đặt nội dung tab Message. <paramref name="error"/> = lỗi (đỏ), <paramref name="printed"/> = PRINT/RAISERROR (màu nhấn);
    /// tự chọn tab: có lỗi hoặc không có bảng nào mà có message → Message, còn lại → Grid Result.</summary>
    public void SetMessages(string? error, string? printed, int tables)
    {
        _msg.Clear();
        void Add(string? text, Color color)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            _msg.SelectionStart = _msg.TextLength; _msg.SelectionLength = 0; _msg.SelectionColor = color;
            _msg.AppendText(text.TrimEnd() + Environment.NewLine);
        }
        Add(error, AppColors.Danger);
        Add(printed, AppColors.Accent);
        _lines = Count(error) + Count(printed);
        _kind = !string.IsNullOrWhiteSpace(error) ? "error" : !string.IsNullOrWhiteSpace(printed) ? "info" : "none";
        Select(_kind == "error" || (tables == 0 && _lines > 0) ? "msg" : "grid");
    }

    public void ClearMessages() => SetMessages(null, null, _tables.Count);

    private static int Count(string? s) => s is null ? 0 : s.Split('\n').Count(l => l.Trim().Length > 0 && l.Trim() != "---");

    private static readonly Regex LineRef = new(@"d[oò]ng\s+(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void TryGoto(int charIndex)
    {
        if (charIndex < 0 || charIndex >= _msg.TextLength) return;
        var line = _msg.GetLineFromCharIndex(charIndex);
        if (line < 0 || line >= _msg.Lines.Length) return;
        if (LineRef.Match(_msg.Lines[line]) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var n)) GotoLineRequested?.Invoke(n);
    }
}
