using System.Text.RegularExpressions;

namespace SqlWatch;

// partial: Bcode nhúng form này vào tab Ctrl+3 (phần nối nằm ở Bcode.App/Controls/SqlWatchControl.Form.cs).
public sealed partial class MainForm : Form
{
    private const int MaxRows = 2000;

    private readonly ComboBox _project = new() { DropDownStyle = ComboBoxStyle.DropDown, IntegralHeight = false, DropDownHeight = 360 };
    private readonly TextBox _server = new() { ReadOnly = true, TabStop = false };
    private readonly TextBox _databases = new() { ReadOnly = true, TabStop = false };
    // Login SQL: mặc định là user / pass cứng (SqlLogin), sửa được khi dùng bên khách hàng — nhớ theo dự án.
    private readonly TextBox _login = new();
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly Button _loginDefault = new() { Text = "Mặc định" };
    private readonly Dictionary<string, (string User, string Password)> _loginMemory = new(StringComparer.OrdinalIgnoreCase);
    private string _note = "";
    // Bộ lọc nhớ theo dự án: _filtersProject = dự án đang nạp bộ lọc vào các ô; đổi ô thì 0,8 giây sau mới lưu (khỏi ghi file mỗi phím).
    private readonly Dictionary<string, WatchFilters> _filterMemory = new(StringComparer.OrdinalIgnoreCase);
    private readonly System.Windows.Forms.Timer _filterSave = new() { Interval = 800 };
    private readonly ToolTip _tips = new();
    private string _filtersProject = "";
    private string _loadedProject = "";
    private bool _applyingFilters;
    // Nút xử lý dòng đang chọn (như thanh SQL Profiler của Bcode): Copy SQL Command / Debug store.
    private readonly Button _copySql = new() { Text = "📋 Copy SQL Command" };
    private readonly Button _debugSql = new() { Text = "🐞 Debug store", Visible = false };
    private Action<string>? _debugStore;
    private string _flash = "";
    private DateTime _flashUntil;
    // Nhịp đọc chỉ chọn 200 / 250 / 300 ms (mặc định 200): DMV chỉ giữ câu cuối của mỗi phiên, nên Fast gửi vài câu liền nhau trong
    // 1 nhịp là sót các câu trước (README "Giới hạn") — nhịp ngắn sót ít hơn. Bcode nhớ lựa chọn (AppSettings.SqlWatchIntervalMs).
    public static readonly int[] PollIntervals = [200, 250, 300];
    public const int DefaultPollInterval = 200;
    private readonly ComboBox _interval = new() { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat };
    private readonly TextBox _loginFilter = new();
    private readonly TextBox _appFilter = new();
    private readonly TextBox _textFilter = new();
    private readonly Button _start = new() { Text = "Bắt đầu" };
    private readonly Button _pause = new() { Text = "Tạm dừng", Enabled = false };
    private readonly Button _stop = new() { Text = "Dừng", Enabled = false };
    private readonly Button _clear = new() { Text = "Xóa lưới" };
    private readonly Button _events = new() { Text = "Sự kiện: tất cả", TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripDropDown _eventMenu = new() { Padding = Padding.Empty };
    private readonly Dictionary<string, ToolStripMenuItem> _eventItems = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CapturedEvent> _captured = [];
    private readonly CheckBox _autoScroll = new() { Text = "Tự cuộn", Checked = true, AutoSize = true };
    private readonly DataGridView _grid = new();
    // Tiêu đề khung Chi tiết — đổi chữ khi dòng SP:Starting đang hiện cả batch gọi nó (ShowSelected).
    private readonly Label _detailCaption = new() { Text = DetailCaption };
    private readonly TextBox _detail = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 10f),
        Dock = DockStyle.Fill
    };
    private readonly Label _status = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Dictionary<string, BcodeWorkspace> _projects = new(StringComparer.OrdinalIgnoreCase);

    // Màu: mọi chỗ tô màu đăng ký vào _restyle để đổi bảng màu (Bcode đổi sáng / tối) là tô lại hết, không dựng lại form.
    private Palette _pal = Palette.Light;
    private readonly List<Action> _restyle = [];

    /// <summary>Nơi chứa (Bcode) đọc login đã lưu của dự án: null / chuỗi rỗng = dùng mặc định.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, (string User, string Password)?>? LoginLookup { get; set; }

    /// <summary>Báo nơi chứa lưu login của dự án khi bấm Bắt đầu ("" = mặc định).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string, string, string>? LoginSaved { get; set; }

    /// <summary>Nơi chứa (Bcode) mở tab SQL Query mới có sẵn câu vừa copy (câu SQL, database của dòng đang chọn). Chạy riêng (null) thì chỉ copy vào clipboard.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string, string>? OpenInSqlQuery { get; set; }

    /// <summary>Nơi chứa (Bcode) mở debug store được gọi trong câu SQL; có giá trị thì mới hiện nút "Debug store".</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string>? DebugStore
    {
        get => _debugStore;
        set { _debugStore = value; _debugSql.Visible = value is not null; }
    }

    /// <summary>Nơi chứa (Bcode) đọc bộ lọc đã lưu của dự án; null = chưa lưu lần nào (USER ID tự điền theo user đang theo dõi).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, WatchFilters?>? FiltersLookup { get; set; }

    /// <summary>Báo nơi chứa lưu bộ lọc của dự án.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<string, WatchFilters>? FiltersSaved { get; set; }

    /// <summary>Nơi chứa (Bcode) tra u_id của user đang theo dõi ở SQL Profiler (vsysuser) cho dự án — điền sẵn ô USER ID.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, Task<string?>>? UserIdLookup { get; set; }

    /// <summary>Nơi chứa (Bcode) ghi lỗi tạm thời vào log lỗi chung (exception, nguồn) — lỗi này chỉ hiện trên thanh trạng thái, không bật hộp.
    /// Chạy riêng (null) thì chỉ hiện trên thanh trạng thái.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<Exception, string>? ErrorLogged { get; set; }

    /// <summary>Nhịp đọc (ms) — Bcode đặt theo giá trị đã lưu. Giá trị không thuộc <see cref="PollIntervals"/> (bản cũ / sửa tay settings.json) về 200.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int PollInterval
    {
        get => _interval.SelectedItem is int ms ? ms : DefaultPollInterval;
        set => _interval.SelectedItem = NormalizePollInterval(value);
    }

    /// <summary>Báo nơi chứa lưu nhịp đọc khi người dùng chọn giá trị khác.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<int>? PollIntervalSaved { get; set; }

    public static int NormalizePollInterval(int ms) => Array.IndexOf(PollIntervals, ms) >= 0 ? ms : DefaultPollInterval;

    // Lỗi đọc tạm thời (TraceWatcher.IsTransient): hiện trên thanh trạng thái tới khi đọc lại được; nhịp sau thử lại, lùi dần tới 30 giây.
    private string _pollError = "";
    private int _pollFailures;
    private DateTime _retryAt;
    private string _lastLoggedError = "";
    private DateTime _lastLoggedAt;

    private TraceWatcher? _watcher;
    // Chuỗi kết nối lần bắt gần nhất — đã Dừng mà xem / copy dòng câu dài chưa đủ (FullPending) thì mở kết nối riêng để tìm cả câu.
    private string _connectionString = "";
    private CancellationTokenSource? _lifetime;
    private bool _busy;
    private bool _paused;
    private bool _loading = true;
    private bool _rebuilding;
    private int _shown;

    public MainForm()
    {
        Text = "SqlWatch";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(S(980), S(640));
        ClientSize = new Size(S(1200), S(760));
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true;
        Themed(this, form => { form.BackColor = _pal.Page; form.ForeColor = _pal.Ink; });

        StyleInput(_server, readOnly: true);
        StyleInput(_databases, readOnly: true);
        StyleInput(_loginFilter, readOnly: false);
        StyleInput(_appFilter, readOnly: false);
        StyleInput(_textFilter, readOnly: false);
        StyleInput(_login, readOnly: false);
        StyleInput(_password, readOnly: false);
        _project.FlatStyle = FlatStyle.Flat;
        _project.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
        _project.AutoCompleteSource = AutoCompleteSource.ListItems;
        Themed(_project, box => { box.BackColor = _pal.Input; box.ForeColor = _pal.Ink; });
        foreach (var ms in PollIntervals)
            _interval.Items.Add(ms);
        _interval.SelectedItem = DefaultPollInterval;
        _interval.Width = S(72);
        Themed(_interval, box => { box.BackColor = _pal.Input; box.ForeColor = _pal.Ink; });
        StyleButton(_start, primary: true);
        StyleButton(_pause, primary: false);
        StyleButton(_stop, primary: false);
        StyleButton(_clear, primary: false);
        StyleButton(_events, primary: false);
        StyleButton(_loginDefault, primary: false);
        StyleButton(_copySql, primary: false);
        StyleButton(_debugSql, primary: false);
        _loginDefault.MinimumSize = new Size(S(72), S(26));
        _events.Text += "  ▾";
        _autoScroll.Anchor = AnchorStyles.Left;
        _autoScroll.Margin = new Padding(S(10), 0, 0, 0);
        Themed(_autoScroll, box => box.ForeColor = _pal.Ink);
        BuildEventMenu();
        BuildGrid();

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        Themed(root, panel => panel.BackColor = _pal.Page);
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(136)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, S(26)));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildBody(), 0, 1);
        root.Controls.Add(BuildStatusBar(), 0, 2);
        Controls.Add(root);

        _project.SelectedIndexChanged += (_, _) => ApplyProject(showError: false);
        _project.TextChanged += (_, _) => { if (!_loading) ApplyProject(showError: false); };
        _start.Click += async (_, _) => await StartAsync();
        _stop.Click += async (_, _) => await StopAsync();
        _pause.Click += (_, _) => TogglePause();
        _clear.Click += (_, _) => ClearGrid();
        _copySql.Click += async (_, _) => await CopySqlCommandAsync();
        _debugSql.Click += async (_, _) => await DebugSelectedAsync();
        _loginDefault.Click += (_, _) => { _login.Text = SqlLogin.User; _password.Text = SqlLogin.Password; };
        _events.Click += (_, _) => _eventMenu.Show(_events, new Point(0, _events.Height));
        _loginFilter.TextChanged += (_, _) => { RebuildGrid(); OnFiltersChanged(); };
        _appFilter.TextChanged += (_, _) => { RebuildGrid(); OnFiltersChanged(); };
        _textFilter.TextChanged += (_, _) => { RebuildGrid(); OnFiltersChanged(); };
        _filterSave.Tick += (_, _) => FlushFilters();
        _tips.SetToolTip(_appFilter, "u_id của người dùng Fast (ApplicationName). Tự điền theo user đang theo dõi ở SQL Profiler; lọc ngay khi đọc trên server. Để trống = mọi user.");
        _interval.SelectedIndexChanged += (_, _) =>
        {
            if (_timer.Enabled)
                _timer.Interval = PollInterval;
            UpdateStatus();
        };
        // Chỉ lưu khi người dùng tự chọn (SelectionChangeCommitted) — Bcode đặt PollInterval lúc mở tab thì không ghi lại settings.
        _interval.SelectionChangeCommitted += (_, _) => PollIntervalSaved?.Invoke(PollInterval);
        _tips.SetToolTip(_interval, "Bao lâu đọc DMV 1 lần. SQL Server chỉ giữ câu cuối của mỗi phiên: ứng dụng gửi vài câu liền nhau trong 1 nhịp thì chỉ bắt được câu cuối — nhịp ngắn bắt sót ít hơn.");
        _timer.Tick += async (_, _) => await PollAsync();
        AcceptButton = _start;
        FormClosing += (_, _) => { FlushFilters(); CloseWatcher(); };
        Load += (_, _) => LoadProjects();
    }

    /// <summary>Đổi bảng màu (Bcode đổi theme sáng / tối) — tô lại mọi phần, kể cả màu chữ các dòng đã bắt.</summary>
    public void ApplyPalette(Palette palette)
    {
        _pal = palette;
        foreach (var restyle in _restyle)
            restyle();
        foreach (DataGridViewRow row in _grid.Rows)
            row.DefaultCellStyle.ForeColor = EventColor(row.Cells[1].Value as string);
        Invalidate(true);
    }

    private T Themed<T>(T control, Action<T> apply)
    {
        _restyle.Add(() => apply(control));
        apply(control);
        return control;
    }

    /// <summary>px thiết kế ở 96 DPI → px thật theo DPI màn hình.</summary>
    private int S(int px) => (int)Math.Round(px * DeviceDpi / 96.0);

    // ---- Thanh trên: 3 hàng — nút điều khiển + nhịp / project, server, database / lọc ----
    private Control BuildHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = new Padding(S(12), S(6), S(12), S(6))
        };
        Themed(header, panel => panel.BackColor = _pal.Surface);
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 3; i++)
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3f));
        header.Controls.Add(BuildToolbar(), 0, 0);
        header.Controls.Add(BuildConnectionRow(), 0, 1);
        header.Controls.Add(BuildFilterRow(), 0, 2);
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(_pal.Line);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };
        return header;
    }

    private Control BuildToolbar()
    {
        var left = Flow();
        left.Controls.Add(_start);
        left.Controls.Add(_pause);
        left.Controls.Add(_stop);
        left.Controls.Add(Separator());
        left.Controls.Add(_clear);
        left.Controls.Add(_autoScroll);
        left.Controls.Add(Separator());
        left.Controls.Add(_copySql);
        left.Controls.Add(_debugSql);

        var right = Flow();
        right.AutoSize = true;
        right.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        right.Dock = DockStyle.None;
        right.Anchor = AnchorStyles.Right;
        right.Controls.Add(Caption("Nhịp đọc", first: true));
        _interval.Anchor = AnchorStyles.Left;
        _interval.Margin = new Padding(S(6), 0, 0, 0);
        right.Controls.Add(_interval);
        right.Controls.Add(Caption("ms", first: false, gap: 4));

        var row = Row();
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(left, 0, 0);
        row.Controls.Add(right, 1, 0);
        return row;
    }

    private Control BuildConnectionRow()
    {
        var row = Row();
        var widths = new[] { -1, 200, -1, 190, -1, 100, -1, 100, -1, -1, 0 };   // -1 = vừa nội dung, 0 = phần còn lại
        foreach (var w in widths)
            row.ColumnStyles.Add(w switch
            {
                -1 => new ColumnStyle(SizeType.AutoSize),
                0 => new ColumnStyle(SizeType.Percent, 100),
                _ => new ColumnStyle(SizeType.Absolute, S(w))
            });
        row.Controls.Add(Caption("Project", first: true), 0, 0);
        row.Controls.Add(Field(_project), 1, 0);
        row.Controls.Add(Caption("Server", first: false), 2, 0);
        row.Controls.Add(Field(_server), 3, 0);
        row.Controls.Add(Caption("Login", first: false), 4, 0);
        row.Controls.Add(Field(_login), 5, 0);
        row.Controls.Add(Caption("Pass", first: false, gap: 8), 6, 0);
        row.Controls.Add(Field(_password), 7, 0);
        _loginDefault.Margin = new Padding(S(6), 0, 0, 0);
        row.Controls.Add(_loginDefault, 8, 0);
        row.Controls.Add(Caption("Database", first: false), 9, 0);
        row.Controls.Add(Field(_databases), 10, 0);
        return row;
    }

    private Control BuildFilterRow()
    {
        var row = Row();
        // Nhãn đặt trước từng ô (chữ gợi ý mờ trong ô gần như không đọc được trên nền tối).
        foreach (var style in new[] { SizeType.AutoSize, SizeType.AutoSize, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.Percent, SizeType.AutoSize, SizeType.Percent })
            row.ColumnStyles.Add(style == SizeType.AutoSize ? new ColumnStyle(SizeType.AutoSize) : new ColumnStyle(SizeType.Percent, 100f / 3f));
        row.Controls.Add(Caption("Lọc", first: true), 0, 0);
        _events.Anchor = AnchorStyles.Left;
        _events.Margin = new Padding(S(8), 0, 0, 0);
        row.Controls.Add(_events, 1, 0);
        row.Controls.Add(Caption("Login chứa", first: false), 2, 0);
        row.Controls.Add(Field(_loginFilter), 3, 0);
        row.Controls.Add(Caption("USER ID", first: false), 4, 0);   // Fast gắn ApplicationName = u_id người dùng
        row.Controls.Add(Field(_appFilter), 5, 0);
        row.Controls.Add(Caption("Nội dung chứa", first: false), 6, 0);
        row.Controls.Add(Field(_textFilter), 7, 0);
        return row;
    }

    // ---- Lưới + khung Chi tiết (kéo thanh chia để đổi chiều cao) ----
    private Control BuildBody()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            FixedPanel = FixedPanel.Panel2,
            SplitterWidth = S(8),
            Margin = new Padding(S(12), S(10), S(12), S(8))
        };
        Themed(split, s => { s.BackColor = _pal.Page; s.Panel1.BackColor = _pal.Page; s.Panel2.BackColor = _pal.Page; });

        var gridFrame = Frame();
        gridFrame.Dock = DockStyle.Fill;
        gridFrame.Controls.Add(_grid);
        split.Panel1.Controls.Add(gridFrame);

        var detailFrame = Frame();
        detailFrame.Dock = DockStyle.Fill;
        var detailCaption = _detailCaption;
        detailCaption.Dock = DockStyle.Top;
        detailCaption.Height = S(26);
        detailCaption.Padding = new Padding(S(10), S(6), 0, 0);
        Themed(detailCaption, label => { label.ForeColor = _pal.Muted; label.BackColor = _pal.Surface; });
        var detailBody = new Panel { Dock = DockStyle.Fill, Padding = new Padding(S(10), S(2), S(4), S(4)) };
        Themed(detailBody, panel => panel.BackColor = _pal.Surface);
        Themed(_detail, box => { box.BackColor = _pal.Surface; box.ForeColor = _pal.Ink; });
        detailBody.Controls.Add(_detail);
        detailFrame.Controls.Add(detailBody);
        detailFrame.Controls.Add(detailCaption);
        split.Panel2.Controls.Add(detailFrame);

        // Chiều cao khung Chi tiết đặt 1 lần khi đã có kích thước thật (đặt sớm SplitContainer báo lỗi vì còn quá nhỏ).
        var sized = false;
        split.SizeChanged += (_, _) =>
        {
            if (sized || split.Height < S(320))
                return;
            sized = true;
            split.Panel1MinSize = S(120);
            split.Panel2MinSize = S(70);
            split.SplitterDistance = split.Height - S(190) - split.SplitterWidth;
        };
        return split;
    }

    private Control BuildStatusBar()
    {
        var bar = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(S(12), 0, S(12), 0) };
        Themed(bar, panel => panel.BackColor = _pal.Surface);
        Themed(_status, label => { label.ForeColor = _pal.Muted; label.BackColor = _pal.Surface; });
        bar.Controls.Add(_status);
        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(_pal.Line);
            e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
        };
        return bar;
    }

    // ---- Khối dựng ----
    private TableLayoutPanel Row()
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Themed(row, panel => panel.BackColor = _pal.Surface);
        return row;
    }

    private FlowLayoutPanel Flow()
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty };
        Themed(flow, panel => panel.BackColor = _pal.Surface);
        return flow;
    }

    private Label Caption(string text, bool first, int gap = 16)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(first ? 0 : S(gap), 0, 0, 0)
        };
        Themed(label, l => { l.ForeColor = _pal.Muted; l.BackColor = _pal.Surface; });
        return label;
    }

    /// <summary>Ô nhập trong 1 hàng: giãn ngang theo cột, căn giữa theo chiều dọc.</summary>
    private Control Field(Control input, int gap = 8)
    {
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        input.Margin = new Padding(S(gap), 0, 0, 0);
        return input;
    }

    private Control Separator()
    {
        var line = new Panel { Width = 1, Height = S(22), Anchor = AnchorStyles.Left, Margin = new Padding(S(6), 0, S(10), 0) };
        Themed(line, panel => panel.BackColor = _pal.Line);
        return line;
    }

    private Panel Frame()
    {
        var panel = new Panel { Padding = new Padding(1) };
        Themed(panel, p => p.BackColor = _pal.Surface);
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(_pal.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };
        return panel;
    }

    private void StyleInput(TextBox box, bool readOnly)
    {
        box.BorderStyle = BorderStyle.FixedSingle;
        Themed(box, b =>
        {
            b.BackColor = readOnly ? _pal.ReadOnly : _pal.Input;
            b.ForeColor = _pal.Ink;
        });
    }

    /// <summary>Nền nổi của nút "Copy SQL Command" (mặc định #80d3a1); null = nút thường như các nút khác.
    /// Bcode đặt theo AppSettings.SqlWatchCopyColor (Template → Cửa sổ → SQL Profiler).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color? CopyHighlight
    {
        get => _copyHighlight;
        set { _copyHighlight = value; ApplyPalette(_pal); }
    }
    private Color? _copyHighlight = ColorTranslator.FromHtml("#80d3a1");

    private void StyleButton(Button button, bool primary)
    {
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(S(primary ? 96 : 80), S(30));
        button.Padding = new Padding(S(8), 0, S(8), 0);
        button.Margin = new Padding(0, 0, S(6), 0);
        button.Anchor = AnchorStyles.Left;
        button.FlatStyle = FlatStyle.Flat;
        button.Cursor = Cursors.Hand;
        button.UseVisualStyleBackColor = false;
        if (primary)
            button.Font = new Font(Font, FontStyle.Bold);
        // Nút bị khoá (vd Bắt đầu khi đang bắt) tô nhạt hẳn — nút Flat mặc định vẫn giữ nền cũ nên trông như còn bấm được.
        void Apply(Button b)
        {
            var on = b.Enabled;
            b.FlatAppearance.BorderColor = _pal.Line;
            b.FlatAppearance.MouseOverBackColor = primary ? _pal.AccentHover : _pal.ButtonHover;
            b.FlatAppearance.MouseDownBackColor = primary ? _pal.AccentHover : _pal.ButtonHover;
            b.FlatAppearance.BorderSize = primary && on ? 0 : 1;
            b.BackColor = !on || !primary ? _pal.Button : _pal.Accent;
            b.ForeColor = !on ? _pal.Muted : primary ? _pal.OnAccent : _pal.Ink;
            // Nút có màu nổi riêng (Copy SQL Command): nền màu đó, chữ đen / trắng theo độ sáng nền, rê chuột thì sẫm hơn chút.
            if (on && ReferenceEquals(b, _copySql) && _copyHighlight is { } hl)
            {
                var light = hl.R * 299 + hl.G * 587 + hl.B * 114 > 140_000;
                var hover = Color.FromArgb(hl.R * 85 / 100, hl.G * 85 / 100, hl.B * 85 / 100);
                b.BackColor = hl;
                b.ForeColor = light ? Color.FromArgb(24, 24, 24) : Color.White;
                b.FlatAppearance.BorderColor = hover;
                b.FlatAppearance.MouseOverBackColor = hover;
                b.FlatAppearance.MouseDownBackColor = hover;
            }
        }
        Themed(button, Apply);
        button.EnabledChanged += (_, _) => Apply(button);
        // Nút bị khoá: Windows vẽ chữ xám cố định (gần như chìm trên nền tối) — vẽ đè chữ bằng màu phụ của bảng màu.
        button.Paint += (_, e) =>
        {
            if (button.Enabled)
                return;
            var inner = button.ClientRectangle;
            inner.Inflate(-1, -1);
            using var back = new SolidBrush(button.BackColor);
            e.Graphics.FillRectangle(back, inner);
            TextRenderer.DrawText(e.Graphics, button.Text, button.Font, button.ClientRectangle, _pal.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
    }

    private void BuildGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        // Chọn nhiều dòng như SQL Server Profiler: Chi tiết / Copy ghép câu của các dòng đã chọn (SelectedCommand).
        _grid.MultiSelect = true;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        _grid.BorderStyle = BorderStyle.None;
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersHeight = S(32);
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.RowTemplate.Height = S(26);
        Themed(_grid, grid =>
        {
            grid.BackgroundColor = _pal.Surface;
            grid.GridColor = _pal.Line;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = _pal.Header,
                ForeColor = _pal.Muted,
                SelectionBackColor = _pal.Header,
                SelectionForeColor = _pal.Muted,
                Font = new Font("Segoe UI Semibold", 8.5f),
                Padding = new Padding(S(4), 0, 0, 0)
            };
            var cell = new DataGridViewCellStyle
            {
                BackColor = _pal.Surface,
                ForeColor = _pal.Ink,
                SelectionBackColor = _pal.Selected,
                SelectionForeColor = _pal.Ink,
                Padding = new Padding(S(4), 0, 0, 0)
            };
            grid.DefaultCellStyle = cell;
            grid.RowsDefaultCellStyle = cell;
            grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle(cell) { BackColor = _pal.AltRow };
        });
        AddColumn("Thời điểm", 138);
        AddColumn("Sự kiện", 128);
        var textColumn = AddColumn("Nội dung", 320);
        textColumn.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        textColumn.MinimumWidth = S(220);
        AddColumn("Database", 180);
        AddColumn("SPID", 54);
        AddColumn("Login", 110);
        AddColumn("Máy", 110);
        AddColumn("USER ID", 140);
        AddColumn("Đối tượng", 150);
        _grid.SelectionChanged += (_, _) => ShowSelected();
        // Bấm lại dòng đang chọn (lựa chọn không đổi → không có SelectionChanged) cũng hiện nội dung.
        _grid.CellClick += (_, _) => ShowSelected();
        _grid.CellDoubleClick += async (_, _) => await CopyDetailAsync();
        // Ctrl+C trên lưới: cả câu của dòng đang chọn (mặc định lưới chỉ chép chữ trong ô — cột Nội dung đã rút còn 1 dòng).
        _grid.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.C) { _ = CopyDetailAsync(); e.Handled = e.SuppressKeyPress = true; }
        };
        // Ctrl+C trong khung Chi tiết khi chưa bôi đen gì hoặc đã Ctrl+A: cả câu, kể cả phần không hiện ra.
        _detail.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.C || (_detail.SelectionLength > 0 && _detail.SelectionLength < _detail.TextLength)) return;
            _ = CopyDetailAsync();
            e.Handled = e.SuppressKeyPress = true;
        };
    }

    private Color EventColor(string? eventName) => eventName switch
    {
        "RPC:Starting" => _pal.Rpc,
        "SP:Starting" => _pal.Sp,
        _ => _pal.Ink
    };

    private void LoadProjects()
    {
        try
        {
            var catalog = BcodeCatalog.Load();
            _projects.Clear();
            _project.BeginUpdate();
            _project.Items.Clear();
            foreach (var workspace in catalog.Workspaces)
            {
                _projects[workspace.Name] = workspace;
                _project.Items.Add(workspace.Name);
            }
            _project.EndUpdate();

            _loading = false;
            if (_projects.TryGetValue(catalog.LastWorkspace, out var last))
                _project.SelectedItem = last.Name;
            else if (_project.Items.Count > 0)
                _project.SelectedIndex = 0;
            ApplyProject(showError: false);
            SetIdleStatus();
        }
        catch (Exception ex)
        {
            _loading = false;
            _status.Text = "Không đọc được settings Bcode. " + ex.Message;
        }
    }

    private bool ApplyProject(bool showError)
    {
        var name = _project.Text.Trim();
        if (!_projects.TryGetValue(name, out var workspace))
        {
            _server.Clear();
            _databases.Clear();
            if (showError)
                MessageBox.Show(this, "Chọn một project trong settings Bcode.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        _server.Text = workspace.Server;
        _databases.Text = string.Join(", ", workspace.Databases);
        if (!workspace.Name.Equals(_loadedProject, StringComparison.OrdinalIgnoreCase))
        {
            _loadedProject = workspace.Name;
            LoadLogin(workspace.Name);
            LoadFilters(workspace.Name);
        }
        if (!showError)
            return workspace.Server.Length > 0 && workspace.Databases.Count > 0;

        if (workspace.Server.Length == 0)
        {
            MessageBox.Show(this, "Project không có server.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (workspace.Databases.Count == 0)
        {
            MessageBox.Show(this, "Project không có database.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private DataGridViewTextBoxColumn AddColumn(string header, int width)
    {
        var column = new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            Width = S(width),
            SortMode = DataGridViewColumnSortMode.NotSortable
        };
        _grid.Columns.Add(column);
        return column;
    }

    private async Task StartAsync()
    {
        if (_watcher is not null)
            return;
        if (!ApplyProject(showError: true))
            return;

        string[] databases;
        try
        {
            databases = TraceWatcher.ParseDatabases(_databases.Text);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var user = _login.Text.Trim().Length > 0 ? _login.Text.Trim() : SqlLogin.User;
        var password = _password.Text.Length > 0 ? _password.Text : SqlLogin.Password;
        SaveLogin(_project.Text.Trim(), user, password);
        var builder = TraceWatcher.CreateBuilder(_server.Text, user, password, "master", TraceWatcher.ApplicationName);
        var watcher = new TraceWatcher(builder.ConnectionString, databases) { UserIdFilter = _appFilter.Text.Trim() };
        _connectionString = builder.ConnectionString;
        try
        {
            UseWaitCursor = true;
            _lifetime = new CancellationTokenSource();
            await watcher.OpenAsync(_lifetime.Token);
        }
        catch (Exception ex)
        {
            await watcher.DisposeAsync();
            _lifetime?.Dispose();
            _lifetime = null;
            UseWaitCursor = false;
            if (TraceWatcher.IsTransient(ex))
            {
                // Mất mạng / server chưa sẵn sàng lúc bấm Bắt đầu (hay chế độ Tự động): báo trên thanh trạng thái + log, không bật hộp chặn.
                try { ErrorLogged?.Invoke(ex, "SqlWatch: không kết nối được lúc Bắt đầu (lỗi tạm thời)"); } catch { }
                ReportStatus("⚠ Chưa bắt được — không kết nối được server: " + OneLine(ex) + " — bấm Bắt đầu để thử lại.");
                return;
            }
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _watcher = watcher;
        _pollFailures = 0;
        _retryAt = DateTime.MinValue;
        _pollError = "";
        _note = watcher.MissingDatabases.Count == 0 ? "" : "bỏ qua (không có trên server này): " + string.Join(", ", watcher.MissingDatabases);
        _paused = false;
        _pause.Text = "Tạm dừng";
        SetRunning(true);
        _timer.Interval = PollInterval;
        _timer.Start();
        UseWaitCursor = false;
        UpdateStatus();
    }

    private async Task StopAsync()
    {
        CloseWatcher();
        if (!IsDisposed)
            SetRunning(false);
        await Task.CompletedTask;
    }

    private void CloseWatcher()
    {
        _timer.Stop();
        _busy = false;
        if (_lifetime is not null)
        {
            _lifetime.Cancel();
            _lifetime.Dispose();
            _lifetime = null;
        }

        if (_watcher is not null)
        {
            _watcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _watcher = null;
        }

        _paused = false;
        if (!IsDisposed)
            _pause.Text = "Tạm dừng";
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _pause.Text = _paused ? "Tiếp tục" : "Tạm dừng";
        UpdateStatus();
        CaptureStateChanged?.Invoke();
    }

    private async Task PollAsync()
    {
        if (_busy || _watcher is null || _lifetime is null || DateTime.Now < _retryAt)
            return;

        _busy = true;
        var watcher = _watcher;
        try
        {
            var rows = await watcher.PollAsync(_lifetime.Token);
            if (IsDisposed)
                return;
            if (_pollFailures > 0)
                ClearPollError();
            Append(rows);
        }
        catch (OperationCanceledException)
        {
        }
        // Lỗi tạm thời (database không ONLINE, mất mạng / kết nối rớt, hết giờ, deadlock…): trước đây bật hộp lỗi chặn người dùng và dừng bắt.
        // Giờ chỉ báo trên thanh trạng thái + ghi log lỗi chung của Bcode, bắt tiếp ở nhịp sau (kết nối rớt thì TraceWatcher tự mở lại).
        catch (Exception ex) when (!IsDisposed && ReferenceEquals(watcher, _watcher)
                                   && (TraceWatcher.IsTransient(ex) || ex is InvalidOperationException && !watcher.IsConnected))
        {
            OnPollError(ex);
        }
        catch (Exception) when (!ReferenceEquals(watcher, _watcher))
        {
            // đã Dừng (hoặc bắt lại) trong lúc nhịp này đang đọc — lỗi của kết nối cũ, bỏ qua
        }
        catch (Exception ex)
        {
            _timer.Stop();
            if (!IsDisposed)
            {
                UpdateStatus();
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                await StopAsync();
            }
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnPollError(Exception ex)
    {
        _pollFailures++;
        var wait = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(_pollFailures - 1, 5)));
        _retryAt = DateTime.Now + wait;
        var message = OneLine(ex);
        _pollError = $"⚠ lỗi đọc lúc {DateTime.Now:HH:mm:ss}: {message} — tự thử lại sau {wait.TotalSeconds:0} giây"
            + (_pollFailures > 1 ? $" (lần {_pollFailures})" : "");
        // Ghi log: lỗi khác lần trước hoặc đã quá 10 phút — server tắt lâu thì không ghi mỗi nhịp.
        if (!message.Equals(_lastLoggedError, StringComparison.Ordinal) || DateTime.Now - _lastLoggedAt > TimeSpan.FromMinutes(10))
        {
            _lastLoggedError = message;
            _lastLoggedAt = DateTime.Now;
            try { ErrorLogged?.Invoke(ex, "SqlWatch: lỗi tạm thời khi đọc câu SQL (không dừng bắt, tự thử lại)"); }
            catch { /* log hỏng không được làm dừng bắt */ }
        }
        UpdateStatus();
    }

    private void ClearPollError()
    {
        _pollFailures = 0;
        _retryAt = DateTime.MinValue;
        _pollError = "";
        UpdateStatus();
    }

    private static string OneLine(Exception ex)
    {
        var text = Regex.Replace(ex.Message, @"\s+", " ").Trim();
        return text.Length > 200 ? text[..200] + "…" : text;
    }

    private void BuildEventMenu()
    {
        _eventMenu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                e.Cancel = true;
        };
        // ToolStripDropDown thường không có cột vẽ dấu tick → trước đây 3 mục trông như nhau, không biết mục nào đang bật.
        // Ghi ô ☑ / ☐ ngay trong chữ của mục (rõ ở cả theme sáng lẫn tối).
        static string Label(string name, bool on) => (on ? "☑  " : "☐  ") + name;
        foreach (var name in ProcCalls.Events)
        {
            var item = new ToolStripMenuItem(Label(name, true)) { Checked = true, CheckOnClick = true };
            item.CheckedChanged += (_, _) =>
            {
                item.Text = Label(name, item.Checked);
                UpdateEventButton();
                RebuildGrid();
                OnFiltersChanged();
            };
            _eventItems[name] = item;
            _eventMenu.Items.Add(item);
        }
        Themed(_eventMenu, menu =>
        {
            menu.BackColor = _pal.Surface;
            menu.ForeColor = _pal.Ink;
            foreach (ToolStripItem item in menu.Items)
                item.ForeColor = _pal.Ink;
        });
    }

    private void UpdateEventButton()
    {
        var selected = _eventItems.Where(item => item.Value.Checked).Select(item => item.Key).ToList();
        _events.Text = selected.Count == _eventItems.Count
            ? "Sự kiện: tất cả"
            : selected.Count == 0
                ? "Sự kiện: không"
                : "Sự kiện: " + string.Join(", ", selected.Select(ShortEvent));
        _events.Text += "  ▾";
    }

    private static string ShortEvent(string name) => name switch
    {
        "SQL:BatchStarting" => "Batch",
        "RPC:Starting" => "RPC",
        "SP:Starting" => "SP",
        _ => name
    };

    private void Append(IReadOnlyList<CapturedEvent> rows)
    {
        if (rows.Count == 0)
        {
            UpdateStatus();
            return;
        }

        var trimmed = false;
        foreach (var row in rows)
        {
            if (_paused)
                continue;
            _captured.Add(row);
        }

        while (_captured.Count > MaxRows)
        {
            _captured.RemoveAt(0);
            trimmed = true;
        }

        if (trimmed)
            RebuildGrid();
        else
        {
            var added = false;
            foreach (var row in rows)
            {
                if (_paused || !Matches(row))
                    continue;
                AddGridRow(row);
                added = true;
            }
            if (added && _autoScroll.Checked && _grid.Rows.Count > 0)
                _grid.FirstDisplayedScrollingRowIndex = _grid.Rows.Count - 1;
            UpdateStatus();
        }
    }

    private void RebuildGrid()
    {
        if (_rebuilding)
            return;
        _rebuilding = true;
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            _detail.Clear();
            _detailCaption.Text = DetailCaption;
            _shown = 0;
            foreach (var row in _captured)
            {
                if (Matches(row))
                    AddGridRow(row);
            }
            if (_autoScroll.Checked && _grid.Rows.Count > 0)
                _grid.FirstDisplayedScrollingRowIndex = _grid.Rows.Count - 1;
        }
        finally
        {
            _grid.ResumeLayout();
            _rebuilding = false;
        }
        UpdateStatus();
    }

    private void AddGridRow(CapturedEvent row)
    {
        var index = _grid.Rows.Add(
            row.Time.ToString("dd/MM HH:mm:ss.fff"),
            row.EventName,
            OneLine(row.Text),
            row.DatabaseName,
            row.Spid.ToString(),
            row.LoginName,
            row.HostName,
            row.ApplicationName,
            row.ObjectName);
        var gridRow = _grid.Rows[index];
        gridRow.Tag = row;
        gridRow.DefaultCellStyle.ForeColor = EventColor(row.EventName);
        // Dòng đầu tiên lưới tự chọn ngay trong Rows.Add — lúc đó chưa có Tag nên SelectionChanged bỏ qua; hiện lại khi đã gắn nội dung.
        if (ReferenceEquals(_grid.CurrentRow, gridRow))
            ShowSelected();
        _shown++;
    }

    private bool Matches(CapturedEvent row)
    {
        if (_eventItems.TryGetValue(row.EventName, out var item) && !item.Checked)
            return false;
        return Contains(_loginFilter.Text, row.LoginName)
            // USER ID (= ApplicationName mà Fast gắn bằng u_id): khớp đúng — gõ 11 không ra 110 / 211.
            && (string.IsNullOrWhiteSpace(_appFilter.Text) || row.ApplicationName.Trim().Equals(_appFilter.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            && (Contains(_textFilter.Text, row.Text) || Contains(_textFilter.Text, row.ObjectName));
    }

    private static bool Contains(string filter, string value) =>
        string.IsNullOrWhiteSpace(filter)
        || value.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string OneLine(string text)
    {
        var line = text.Replace("\r", " ").Replace("\n", " ").Trim();
        return line.Length <= 500 ? line : line[..500];
    }

    private void ShowSelected()
    {
        // Khung Chi tiết hiện đúng câu mà Copy SQL Command / Debug store sẽ dùng.
        // Câu rất dài chỉ hiện phần đầu (khung khỏi nặng); Copy / double-click / Ctrl+C / Copy SQL Command / Debug store luôn lấy cả câu.
        if (SelectedEvent() is not { } row)
            return;
        var sql = SelectedCommand();
        var many = _grid.SelectedRows.Count > 1;
        // Câu dài chưa đủ (SQL 2008 / 2012, lúc bắt không quét plan cache): bấm 1 dòng thì đi tìm cả câu rồi hiện lại.
        if (!many && PendingFor([row]).Count > 0)
            _ = ResolveAndShowAsync();
        var shown = sql.Length <= DetailMaxLength
            ? sql
            : sql[..DetailMaxLength] + "\n\n-- ... còn " + (sql.Length - DetailMaxLength).ToString("N0") + " ký tự nữa (cả câu "
              + sql.Length.ToString("N0") + " ký tự) — " + (many ? "" : "double-click dòng / ") + "Ctrl+C / Copy SQL Command lấy đủ cả câu";
        // Chỉ để hiện: mọi kiểu xuống dòng (CR lẻ, LF lẻ, U+2028…) thành xuống dòng thật, dồn chuỗi dòng trống còn 1 dòng —
        // batch của Fast có thể chèn nhiều dòng trống trước "exec …" làm câu gọi rơi xuống dưới khung. Copy vẫn lấy nguyên văn.
        var text = LineBreak.Replace(shown, Environment.NewLine);
        text = BlankRun.Replace(text, Environment.NewLine + Environment.NewLine);
        _detail.Text = text;
        if (many)
        {
            _detailCaption.Text = $"Chi tiết  ·  {_grid.SelectedRows.Count} dòng đã chọn — ghép theo thứ tự thời gian, cách nhau GO  ·  Ctrl+C / Copy SQL Command lấy cả chuỗi";
            return;
        }

        // Dòng SP:Starting hiện cả batch đã gọi nó (có declare / gán biến thì mới chạy lại / debug được) — cuộn tới câu exec của
        // dòng này cho thấy ngay, tiêu đề khung ghi rõ đang xem batch.
        var batchOfCall = row.EventName == "SP:Starting" && !sql.Equals(row.Text.Trim(), StringComparison.Ordinal);
        _detailCaption.Text = batchOfCall
            ? $"Chi tiết  ·  cả batch gọi {row.ObjectName} (cả declare + exec — chạy lại / debug được)  ·  double-click một dòng để copy"
            : DetailCaption;
        if (!batchOfCall)
            return;
        var call = LineBreak.Replace(row.Text.Trim(), "\n").Split('\n')[0].Trim();
        var at = call.Length == 0 ? -1 : text.IndexOf(call[..Math.Min(call.Length, 80)], StringComparison.OrdinalIgnoreCase);
        // Câu exec đã nằm trong khung (batch ngắn) thì giữ nguyên — thấy cả declare lẫn exec. Không thì cuộn hết xuống cuối rồi cuộn
        // ngược lên dòng trên exec 2 dòng: dòng exec chắc chắn hiện trong khung (đo trên batch 33 dòng: trước đây nằm ngoài khung).
        var lineHeight = TextRenderer.MeasureText("X", _detail.Font).Height;
        if (at < 0 || _detail.GetPositionFromCharIndex(at).Y + lineHeight * 2 <= _detail.ClientSize.Height)
            return;
        var top = _detail.GetFirstCharIndexFromLine(Math.Max(0, _detail.GetLineFromCharIndex(at) - 2));
        _detail.Select(_detail.TextLength, 0);
        _detail.ScrollToCaret();
        _detail.Select(Math.Max(0, top), 0);
        _detail.ScrollToCaret();
    }

    /// <summary>Dòng đang chọn trên lưới. Lấy SelectedRows trước CurrentRow: đổi dòng bằng phím / code (Tự cuộn chọn dòng mới)
    /// thì SelectionChanged chạy khi CurrentRow vẫn là dòng CŨ → Chi tiết từng hiện nội dung dòng trước.</summary>
    private CapturedEvent? SelectedEvent() =>
        (_grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0] : _grid.CurrentRow)?.Tag as CapturedEvent;

    /// <summary>Các dòng đang chọn (Ctrl / Shift + click, kéo chuột, Shift+↑↓, Ctrl+A) theo thứ tự trên lưới = thứ tự thời gian bắt.</summary>
    private List<CapturedEvent> SelectedEvents()
    {
        var rows = new List<DataGridViewRow>(_grid.SelectedRows.Count);
        foreach (DataGridViewRow row in _grid.SelectedRows)
            rows.Add(row);
        rows.Sort((a, b) => a.Index.CompareTo(b.Index));
        return rows.Select(row => row.Tag).OfType<CapturedEvent>().ToList();
    }

    private const int DetailMaxLength = 20000;
    private const string DetailCaption = "Chi tiết  ·  double-click một dòng để copy";
    private static readonly Regex LineBreak = new(@"\r\n|\r|\n| | |\u0085", RegexOptions.Compiled);
    private static readonly Regex BlankRun = new(@"\r\n(?:[ \t]*\r\n){2,}", RegexOptions.Compiled);

    private async Task CopyDetailAsync()
    {
        await EnsureFullAsync(SelectedEvents());
        var sql = SelectedCommand();
        if (sql.Length == 0)
            return;
        try
        {
            Clipboard.SetText(sql.Replace("\r\n", "\n").Replace("\n", Environment.NewLine));
            var many = _grid.SelectedRows.Count > 1 ? $"{_grid.SelectedRows.Count} dòng ghép, " : "";
            ReportStatus($"Đã copy cả câu ({many}{sql.Length:N0} ký tự).");
        }
        catch (Exception ex)
        {
            ReportStatus("Không ghi được clipboard: " + ex.Message);
        }
    }

    private void ClearGrid()
    {
        _captured.Clear();
        _grid.Rows.Clear();
        _detail.Clear();
        _detailCaption.Text = DetailCaption;
        _shown = 0;
        UpdateStatus();
    }

    private void SetRunning(bool running)
    {
        _start.Enabled = !running;
        _stop.Enabled = running;
        _pause.Enabled = running;
        _project.Enabled = !running;
        // ReadOnly thay vì Enabled = false: ô bị tắt Windows vẽ chữ xám mờ, khó đọc trên nền tối.
        _login.ReadOnly = running;
        _password.ReadOnly = running;
        _loginDefault.Enabled = !running;
        AcceptButton = running ? _stop : _start;
        if (!running)
            SetIdleStatus();
        CaptureStateChanged?.Invoke();
    }

    /// <summary>Bắt đầu / Dừng / Tạm dừng / Tiếp tục — nơi nhúng (Bcode) đổi dấu hiệu trên tab.</summary>
    public event Action? CaptureStateChanged;

    private void SetIdleStatus()
    {
        if (ShowingFlash())
            return;
        var name = _project.Text.Trim();
        var databases = string.IsNullOrWhiteSpace(_databases.Text) ? "chưa có database" : _databases.Text;
        _status.Text = name.Length == 0
            ? "Sẵn sàng."
            : $"Sẵn sàng  ·  {name}  ·  {databases}";
    }

    private void UpdateStatus()
    {
        if (_watcher is null)
        {
            SetIdleStatus();
            return;
        }

        if (ShowingFlash())
            return;
        var state = _paused ? "Tạm dừng" : "Đang bắt";
        _status.Text = $"{state}  ·  {_project.Text.Trim()}  ·  hiện {_shown}/{_captured.Count}  ·  nhịp {PollInterval} ms"
            + (_pollError.Length > 0 ? "  ·  " + _pollError : "")
            + (_note.Length > 0 ? "  ·  " + _note : "");
    }

    /// <summary>Login của dự án: đã nhập trong phiên này → đã lưu ở nơi chứa (Bcode) → mặc định (SqlLogin).</summary>
    private void LoadLogin(string project)
    {
        (string User, string Password)? saved = _loginMemory.TryGetValue(project, out var memory) ? memory : LoginLookup?.Invoke(project);
        _login.Text = saved is { User.Length: > 0 } ? saved.Value.User : SqlLogin.User;
        _password.Text = saved is { Password.Length: > 0 } ? saved.Value.Password : SqlLogin.Password;
    }

    private void SaveLogin(string project, string user, string password)
    {
        if (project.Length == 0)
            return;
        var isDefault = user == SqlLogin.User && password == SqlLogin.Password;
        if (isDefault)
            _loginMemory.Remove(project);
        else
            _loginMemory[project] = (user, password);
        LoginSaved?.Invoke(project, isDefault ? "" : user, isDefault ? "" : password);
    }

    /// <summary>Báo 1 dòng lên thanh trạng thái, giữ 6 giây (nhịp đọc không ghi đè ngay). Bcode dùng để báo kết quả Debug store.</summary>
    public void ReportStatus(string text)
    {
        _flash = text;
        _flashUntil = DateTime.Now.AddSeconds(6);
        _status.Text = text;
    }

    private bool ShowingFlash()
    {
        if (DateTime.Now >= _flashUntil)
            return false;
        _status.Text = _flash;
        return true;
    }

    /// <summary>Câu SQL để chạy lại / debug từ dòng đang chọn (cũng là nội dung khung Chi tiết):
    /// - Batch: nguyên câu client gửi (declare + gán biến + exec …).
    /// - SP:Starting: batch gần nhất trước đó cùng SPID CÓ GỌI chính procedure này (như TextData của Profiler) — không lấy batch / RPC
    ///   khác cùng thời điểm (trước đây bấm zrpt_69_new lại ra zrs_xcthspb$Generate).
    /// - Chỉ có "EXEC tên" (RPC trên SQL 2008, không đọc được giá trị tham số): dựng khung declare + exec từ tham số trong định nghĩa.</summary>
    private string SelectedCommand()
    {
        var rows = SelectedEvents();
        if (rows.Count <= 1)
            return SelectedEvent() is { } row ? CommandFor(row, out _) : "";
        // Chọn nhiều dòng (như SQL Server Profiler): ghép câu của từng dòng theo thứ tự trên lưới (= thứ tự bắt), cách nhau dòng GO.
        // Dòng SP:Starting lấy batch gọi nó — batch đó đã có (chính dòng batch cũng được chọn, hoặc SP khác cùng batch) thì không lặp lại.
        var used = new HashSet<CapturedEvent>(ReferenceEqualityComparer.Instance);
        var parts = new List<string>();
        foreach (var row in rows)
        {
            var sql = CommandFor(row, out var source);
            if (sql.Length > 0 && used.Add(source))
                parts.Add(sql);
        }
        return string.Join(Environment.NewLine + "GO" + Environment.NewLine, parts);
    }

    /// <summary>Câu SQL của 1 dòng (xem SelectedCommand); source = dòng thật sự cho ra câu đó (SP:Starting → dòng batch đã gọi nó).</summary>
    private string CommandFor(CapturedEvent row, out CapturedEvent source)
    {
        source = row;
        var name = row.ObjectName;
        if (row.EventName == "SP:Starting" && name.Length > 0)
        {
            var at = _captured.LastIndexOf(row);
            for (int i = (at < 0 ? _captured.Count : at) - 1, seen = 0; i >= 0 && seen < 500; i--, seen++)
            {
                var e = _captured[i];
                if (e.Spid != row.Spid || e.EventName == "SP:Starting" || e.Time > row.Time || (row.Time - e.Time).TotalMinutes > 30)
                    continue;
                if (IsBareExec(e) || !ProcCalls.Calls(e.Text, name))
                    continue;
                source = e;
                return e.Text.Trim();
            }
        }
        if (IsBareExec(row) && name.Length > 0)
        {
            var module = row.ModuleText.Length > 0 ? row.ModuleText
                : _captured.LastOrDefault(e => e.ModuleText.Length > 0 && e.ObjectName.Equals(name, StringComparison.OrdinalIgnoreCase))?.ModuleText ?? "";
            return ProcCalls.CallSkeleton(name, module);
        }
        return row.Text.Trim();
    }

    // ---- Câu dài trên SQL 2008 / 2012 (FullPending): lúc bắt không quét plan cache (nặng cho server), xem / copy dòng đó mới tìm cả câu ----
    private const int ResolveMax = 20;
    private readonly Dictionary<CapturedEvent, Task<CapturedEvent>> _resolving = new(ReferenceEqualityComparer.Instance);

    /// <summary>Các dòng FullPending mà câu của các dòng đã cho cần tới: chính dòng, batch mà SP:Starting lấy, hoặc (SP:Starting chưa ghép được
    /// batch nào — lời gọi nằm ở phần bị cắt) batch câu dài gần nhất trước nó cùng SPID.</summary>
    private List<CapturedEvent> PendingFor(IEnumerable<CapturedEvent> rows)
    {
        var pending = new List<CapturedEvent>();
        var seen = new HashSet<CapturedEvent>(ReferenceEqualityComparer.Instance);
        void Add(CapturedEvent e)
        {
            if (e.FullPending && seen.Add(e))
                pending.Add(e);
        }
        foreach (var row in rows)
        {
            Add(row);
            CommandFor(row, out var source);
            Add(source);
            if (row.EventName != "SP:Starting" || !ReferenceEquals(source, row))
                continue;
            var at = _captured.FindLastIndex(e => ReferenceEquals(e, row));
            for (int i = (at < 0 ? _captured.Count : at) - 1, n = 0; i >= 0 && n < 500; i--, n++)
            {
                var e = _captured[i];
                if (e.Spid != row.Spid || e.EventName == "SP:Starting" || e.Time > row.Time)
                    continue;
                if (e.FullPending)
                    Add(e);
                break;
            }
        }
        return pending;
    }

    private async Task EnsureFullAsync(IEnumerable<CapturedEvent> rows)
    {
        var pending = PendingFor(rows);
        if (pending.Count == 0)
            return;
        var skipped = Math.Max(0, pending.Count - ResolveMax);
        ReportStatus($"Đang tìm cả câu dài (máy chủ chỉ cho đọc 4000 ký tự đầu) — {Math.Min(pending.Count, ResolveMax)} dòng...");
        UseWaitCursor = true;
        try
        {
            foreach (var old in pending.Take(ResolveMax))
            {
                if (!_resolving.TryGetValue(old, out var task))
                {
                    task = ResolveOneAsync(old);
                    _resolving[old] = task;
                }
                ReplaceEvent(old, await task);
                _resolving.Remove(old);
            }
        }
        finally
        {
            UseWaitCursor = false;
        }
        if (skipped > 0)
            ReportStatus($"{skipped} dòng câu dài còn lại giữ 4000 ký tự đầu (mỗi lần tìm tối đa {ResolveMax} dòng — chọn ít dòng hơn).");
        else
        {
            _flashUntil = DateTime.MinValue;
            UpdateStatus();
        }
    }

    private async Task<CapturedEvent> ResolveOneAsync(CapturedEvent row)
    {
        try
        {
            var token = _lifetime?.Token ?? CancellationToken.None;
            if (_watcher is { } watcher)
                return await watcher.ResolveFullAsync(row, token);
            if (_connectionString.Length > 0)
                return await TraceWatcher.ResolveFullAsync(_connectionString, row, token);
        }
        catch (Exception ex)
        {
            ReportStatus("Không tìm được cả câu: " + ex.Message);
        }
        return row with { FullPending = false };   // không tìm lại mãi (ShowSelected tự tìm khi bấm dòng)
    }

    /// <summary>Thay dòng đã bắt bằng bản đã tìm cả câu: trong _captured (SP:Starting ghép batch theo danh sách này) và Tag của dòng lưới.</summary>
    private void ReplaceEvent(CapturedEvent old, CapturedEvent fresh)
    {
        if (ReferenceEquals(old, fresh))
            return;
        var at = _captured.FindIndex(e => ReferenceEquals(e, old));
        if (at >= 0)
            _captured[at] = fresh;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (!ReferenceEquals(row.Tag, old))
                continue;
            row.Tag = fresh;
            row.Cells[2].Value = OneLine(fresh.Text);
            row.Cells[8].Value = fresh.ObjectName;
            break;
        }
    }

    private async Task ResolveAndShowAsync()
    {
        await EnsureFullAsync(SelectedEvents());
        if (_grid.SelectedRows.Count == 1)
            ShowSelected();
    }

    /// <summary>Dòng chỉ có "EXEC tên" (không tham số / giá trị).</summary>
    private static bool IsBareExec(CapturedEvent e)
    {
        var parts = e.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0].Equals("EXEC", StringComparison.OrdinalIgnoreCase);
    }

    private async Task CopySqlCommandAsync()
    {
        await EnsureFullAsync(SelectedEvents());
        var sql = SelectedCommand();
        if (sql.Length == 0)
        {
            ReportStatus("Chọn 1 dòng trên lưới rồi bấm Copy SQL Command.");
            return;
        }
        try
        {
            Clipboard.SetText(sql);
        }
        catch (Exception ex)
        {
            ReportStatus("Không ghi được clipboard: " + ex.Message);
            return;
        }
        // Batch dài trên SQL 2008 / 2012 mà không tìm được bản đầy đủ: chỉ có 4000 ký tự đầu — báo rõ là câu chưa đủ, chạy sẽ lỗi.
        var cut = sql.Contains(TraceWatcher.TruncatedNote, StringComparison.Ordinal)
            ? " — ⚠ CÂU CHƯA ĐỦ: máy chủ chỉ cho đọc 4000 ký tự đầu, không lấy được phần còn lại (chạy nguyên câu sẽ lỗi)"
            : "";
        if (OpenInSqlQuery is null)
        {
            ReportStatus($"Đã copy SQL Command ({sql.Length} ký tự){cut}.");
            return;
        }
        ReportStatus($"Đã copy SQL Command ({sql.Length} ký tự){cut} — đã mở SQL Query và dán vào.");
        // Nhiều dòng: database của dòng đầu (sớm nhất) trong các dòng đã chọn.
        var database = _grid.SelectedRows.Count > 1 ? SelectedEvents().FirstOrDefault()?.DatabaseName : SelectedEvent()?.DatabaseName;
        OpenInSqlQuery(sql, database ?? "");
    }

    private async Task DebugSelectedAsync()
    {
        // Debug 1 store: chọn nhiều dòng thì lấy dòng đang trỏ (dòng bấm sau cùng), không lấy chuỗi ghép.
        var many = _grid.SelectedRows.Count > 1 && _grid.CurrentRow is { Selected: true, Tag: CapturedEvent };
        await EnsureFullAsync(many ? [(CapturedEvent)_grid.CurrentRow!.Tag] : SelectedEvents());
        var sql = _grid.SelectedRows.Count > 1 && _grid.CurrentRow is { Selected: true, Tag: CapturedEvent current }
            ? CommandFor(current, out _)
            : SelectedCommand();
        if (sql.Length == 0)
        {
            ReportStatus("Chọn 1 dòng có gọi store (exec …) rồi bấm Debug store.");
            return;
        }
        ReportStatus($"Đã lấy câu SQL ({sql.Length} ký tự) — đang tìm store được gọi để mở debug...");
        _debugStore?.Invoke(sql);
    }

    /// <summary>Nạp bộ lọc đã nhớ của dự án vào các ô. Chưa nhớ lần nào: để trống, bật đủ Sự kiện và tra u_id của user đang theo dõi.</summary>
    private void LoadFilters(string project)
    {
        FlushFilters();
        _filtersProject = project;
        var saved = _filterMemory.TryGetValue(project, out var memory) ? memory : FiltersLookup?.Invoke(project);
        _applyingFilters = true;
        try
        {
            _loginFilter.Text = saved?.Login ?? "";
            _appFilter.Text = saved?.UserId ?? "";
            _textFilter.Text = saved?.Text ?? "";
            foreach (var (name, item) in _eventItems)
                item.Checked = saved is null || saved.Events.Contains(name, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _applyingFilters = false;
        }
        if (_watcher is not null)
            _watcher.UserIdFilter = _appFilter.Text.Trim();
        if (saved is null && UserIdLookup is not null)
            _ = AutoUserIdAsync(project);
    }

    private async Task AutoUserIdAsync(string project)
    {
        try
        {
            var uid = (await UserIdLookup!(project))?.Trim();
            if (string.IsNullOrEmpty(uid) || IsDisposed || _filtersProject != project || _appFilter.Text.Length > 0)
                return;
            _appFilter.Text = uid;
            ReportStatus($"USER ID = {uid} (u_id của user đang theo dõi ở SQL Profiler) — xoá ô để xem mọi user.");
        }
        catch
        {
            // không tra được (không kết nối được App Data...) thì để trống như cũ
        }
    }

    private void OnFiltersChanged()
    {
        if (_watcher is not null)
            _watcher.UserIdFilter = _appFilter.Text.Trim();
        if (_applyingFilters || _filtersProject.Length == 0)
            return;
        _filterSave.Stop();
        _filterSave.Start();
    }

    /// <summary>Lưu ngay bộ lọc đang chờ (đổi dự án, đóng tab / form).</summary>
    public void FlushFilters()
    {
        if (!_filterSave.Enabled)
            return;
        _filterSave.Stop();
        if (_filtersProject.Length == 0)
            return;
        var filters = new WatchFilters(_loginFilter.Text.Trim(), _appFilter.Text.Trim(), _textFilter.Text.Trim(),
            _eventItems.Where(item => item.Value.Checked).Select(item => item.Key).ToList());
        _filterMemory[_filtersProject] = filters;
        FiltersSaved?.Invoke(_filtersProject, filters);
    }
}
