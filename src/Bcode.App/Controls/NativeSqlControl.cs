using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Data.SqlClient;
using ScintillaNET;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "SQL Query" của chế độ App/WinForms: editor <b>Scintilla</b> (native, vài MB thay vì ~125MB của 4 WebView2 + Monaco), thanh nút bằng control WinForms,
/// kết quả ở <see cref="NativeResultPane"/> (lưới DataGridView + Message). Chạy script bằng đúng <see cref="RawSqlService"/> như bản Web nên cùng kết nối,
/// cùng Cancel, cùng lịch sử sửa procedure; MainForm gọi qua <see cref="ISqlTab"/> nên mở tab / phiên / mở object dùng chung với bản Web.
///
/// Đã có (ngang bản Web): tô màu SQL theo theme, số dòng, F5 / Ctrl+Enter chạy (chạy phần bôi đen nếu có), Alt+F5 huỷ, F7 Beauty, Alt+Z wrap, Ctrl+R ẩn/hiện kết quả,
/// F12 xem nhanh object trong khung peek (F12 xem tiếp, Ctrl+F12 mở tab mới, Esc quay lại), gợi ý code (cùng sqlhints.js với Web: từ khoá, bảng, cột theo ngữ cảnh, hàm + tham số),
/// Default Type, Comment/Uncomment, Check Fields, Write Schema, Pivot, Open/Save .sql, Lưu lịch sử SQL, lịch sử sửa procedure, Reset Connection, Result Tab, tab Claude/Gemini nhúng,
/// Debug từng bước (F10 / Ctrl+F10 / Shift+F5, chấm đỏ breakpoint ở lề), AI Ctrl+I sửa/sinh SQL + gợi ý ghost-text (Tab nhận, Alt+\ gọi tay), thanh nút tuỳ biến (ghim / nhóm / ẩn,
/// chung file bố cục với Web), cỡ chữ lưu chung (Ctrl+lăn chuột, Ctrl+0).
/// Khác Web chủ ý: ghost-text hiện thành khối chữ xám dưới dòng con trỏ (Scintilla không có ghost chèn giữa dòng); không cần "ngủ đông" tab (tab native rất nhẹ).
/// </summary>
public sealed class NativeSqlControl : UserControl, ISqlTab
{
    // ---- dịch vụ ----
    private readonly RawSqlService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly AppSettings _settings = AppSettings.Load();

    // ---- giao diện ----
    private readonly Scintilla _ed = new() { Dock = DockStyle.Fill, BorderStyle = ScintillaNET.BorderStyle.None };
    private readonly BarFlow _bar = new() { Dock = DockStyle.Top, WrapContents = true, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 4, 6, 4) };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly SplitContainer _outer = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
    private readonly NativeResultPane _results = new();
    private readonly StatusText _status = new() { Dock = DockStyle.Bottom, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0) };
    private readonly PillButton _btnRun;
    private readonly DropDownButton _db, _defaultType;
    private readonly Panel _aiHost = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<Bcode.Shared.AiSite, AiWebPanel> _aiPanels = new();

    // ---- trạng thái ----
    private bool _useSys;
    private bool _suggestOn = true;
    private bool _resetConnOn = true;
    private bool _resultTabOn;
    private List<DataTable>? _lastResultSchema;   // cấu trúc (không dữ liệu) các bảng kết quả lần chạy gần nhất — cho Write Schema (.xsd)
    private bool _wordWrap;
    private bool _running;
    private CancellationTokenSource? _runCts;
    private string? _currentFilePath;
    private string? _lastScript;
    private long _version;
    private bool _namesLoaded;
    private List<string> _objectNames = new(), _tableNames = new();
    private NativeSqlHints? _hints;
    private SqlConnection? _persistentConn;
    private string _persistentConnStamp = "";
    private bool _persistentConnUsesSys;
    private bool _themeHooked;

    // ---- Debug từng bước (như bản Web: RawSqlControl) ----
    private const int MarkBreakpoint = 1, MarkNextArrow = 2, MarkNextLine = 3, MarkErrorLine = 4, MarkSafeLine = 5;
    private StepPlan? _plan;
    private string _planText = "";    // nội dung script lúc dựng _plan — khác nội dung editor = đã sửa trong lúc debug
    private Dictionary<string, string>? _planValues;   // giá trị tham số đã nhập (dùng lại khi dựng lại kế hoạch)
    private int _executedLine;
    private System.Data.DataTable? _lastVars;   // bảng Variables của bước vừa chạy — cho nút "🧩 SQL động"
    private int _lastOkLine, _errorFrom, _errorLine;   // bước chạy được gần nhất; đoạn dòng của bước vừa lỗi (0 = không lỗi) — tô đỏ trên editor
    private bool _debugStepOn, _stepRunning;
    private readonly BarFlow _dbgBar = new() { Dock = DockStyle.Top, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 3, 6, 3), Visible = false };
    private readonly MutedLabel _dbgInfo = new() { AutoSize = true, Margin = new Padding(0, 7, 10, 0), Text = "Debug", BackColor = Color.Transparent };
    private PillButton? _dbgBack, _dbgNext, _dbgCont, _dbgCursor, _dbgDyn;

    // ---- F12: khung xem nhanh object (peek) ----
    private SqlPeekPanel? _peek;
    private IReadOnlyList<SqlPeekPanel.Item>? _peekCurrent;
    private readonly Stack<IReadOnlyList<SqlPeekPanel.Item>> _peekStack = new();

    // ---- AI: Ctrl+I + gợi ý ghost-text (như bản Web) ----
    private const int GhostStyle = 38;   // style Scintilla dùng cho khối chữ xám của gợi ý
    private SqlAiAssist? _ai;
    private string? _ghostText;          // gợi ý đang hiện (null = không có)
    private int _ghostPos;               // vị trí (byte) con trỏ lúc gợi ý — con trỏ dời đi thì bỏ gợi ý
    private int _ghostLine;
    private long _ghostVersion;
    private CancellationTokenSource? _ghostCts;
    private readonly System.Windows.Forms.Timer _ghostTimer = new() { Interval = 600 };

    public event Action<DataTable>? ResultReady;
    public event Action<string>? CreateRptRequested;
    public event Action<string, bool, bool, int, int>? ScriptExecuted;
    public event Action<string, bool>? SaveHistoryRequested;
    public Func<string>? CurrentProject { get; set; }

    /// <summary>Khoá database cho ghost text Fast (kiểu cột + ma trận đã học): dự án + app/sys.</summary>
    private string GhostDbKey => (CurrentProject?.Invoke() ?? "") + (_useSys ? "|sys" : "|app");
    public event Action<List<DataTable>, string>? OpenResultInNewTabRequested;
    public event Action<string, bool, string>? OpenProcedureWithQueryRequested;
    public event Action<string, bool, string>? OpenObjectInNewTabRequested;
    public event Action<SqlObjectInfo, string?>? DebugTargetChosen;
    public event Action<string, string>? AskAiRequested;

    public bool IsReady => IsHandleCreated;
    public bool UseSysDatabase => _useSys;

    /// <summary>Nhãn trạng thái giữ màu riêng (ThemeManager.Apply đặt ForeColor = Text cho mọi Label nên phải chặn ở đây).</summary>
    private sealed class StatusText : Label
    {
        public Color? StatusColor { get; set; }
        public override Color ForeColor { get => StatusColor ?? AppColors.TextMuted; set { } }
        protected override void OnPaintBackground(PaintEventArgs e) { using var b = new SolidBrush(AppColors.PanelAlt); e.Graphics.FillRectangle(b, ClientRectangle); }
    }

    public NativeSqlControl(RawSqlService service, SqlObjectBrowserService sqlObjectService, LookupService lookupService, SnippetLibraryService snippets)
    {
        _service = service;
        _sqlObjectService = sqlObjectService;
        Dock = DockStyle.Fill;

        // ---- thanh nút ----
        _btnRun = Btn("▶ Execute", "Chạy script (F5 / Ctrl+Enter) — chạy phần bôi đen nếu có", () => { if (_running) CancelRun(); else _ = RunAsync(); }, primary: true);
        _db = new DropDownButton { Width = NativeSkin.RowHeight(this, 128), Height = NativeSkin.RowHeight(this), OnPanelAlt = true, Margin = new Padding(0, 0, 4, 0) };
        _db.SetItems(new[] { "App Data", "Sys Data" }, 0);
        _db.SelectionChanged += i => { _useSys = i == 1; ReloadNames(); };
        _defaultType = new DropDownButton { Width = NativeSkin.RowHeight(this, 150), Height = NativeSkin.RowHeight(this), OnPanelAlt = true, Margin = new Padding(0, 0, 4, 0) };
        _defaultType.SetItems(new[] { "Default Type", "UPPER Keyword", "lower Keyword" }, 0);
        _defaultType.SelectionChanged += i => { if (i > 0) ApplyDefaultType(i == 1); };   // nút giữ hiển thị kiểu đã chọn (UPPER / lower); "Default Type" = không đổi gì
        RebuildBar();   // thanh nút dựng theo bố cục (SqlBarLayout: ghim / nhóm / ẩn) — "Tùy chỉnh thanh…"
        SqlBarLayout.Changed += OnBarLayoutChanged;
        WebMenu.AttachTo(_bar, () => new WebMenu().Add("Tùy chỉnh thanh…", CustomizeBar));

        // ---- editor ----
        ConfigureEditor();
        _ed.TextChanged += (_, _) =>
        {
            _version++;
            // Sửa code trong lúc debug: bỏ tô đỏ cũ ngay, lần bấm Step / Continue / Tìm lỗi kế tiếp chạy lại từ đầu với code mới.
            if (_plan is not null && _errorLine > 0)
            {
                _errorFrom = _errorLine = 0;
                SetStatus("Script đã sửa — F10 / F5 / Tìm lỗi sẽ chạy lại từ đầu với code mới (giữ giá trị tham số).", AppColors.Warning);
                PushDebugState();
            }
        };
        _hints = new NativeSqlHints(_ed, this, () => _useSys, () => CurrentProject?.Invoke() ?? "", snippets, _sqlObjectService,
            () => _running ? null : _persistentConn, () => _objectNames, () => _tableNames, () => _service.Connections.Current is not null);
        _ed.MouseWheel += (_, e) => { };
        WebMenu.AttachTo(_ed, BuildEditorMenu);
        _ed.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && (ModifierKeys & Keys.Control) != 0) OnCtrlRightClick(e.Location); };

        // ---- bố cục: [thanh nút] / [editor | kết quả + trạng thái], bên phải là khung AI (ẩn) ----
        _split.Panel1.Controls.Add(_ed);
        BuildDebugBar();
        _split.Panel1.Controls.Add(_dbgBar);   // sau _ed: Dock Top chiếm phía trên, editor lấp phần còn lại
        var resultHost = new Panel { Dock = DockStyle.Fill };
        resultHost.Controls.Add(_results);
        resultHost.Controls.Add(_status);
        _split.Panel2.Controls.Add(resultHost);
        _results.GotoLineRequested += GotoLine;
        _results.PivotMakeRequested += () => CreateRptRequested?.Invoke(_lastScript ?? "");   // "Tạo file Excel pivot…" như chế độ Web
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_split);
        left.Controls.Add(_bar);
        _outer.Panel1.Controls.Add(left);
        _outer.Panel2.Controls.Add(_aiHost);
        _outer.Panel2Collapsed = true;
        Controls.Add(_outer);
        _status.Height = NativeSkin.RowHeight(this, 22);

        HandleCreated += (_, _) =>
        {
            BeginInvoke(new Action(() =>
            {
                _split.SplitterDistance = Math.Max(80, (int)(_split.Height * 0.55));
                ApplyEditorTheme();
                ReloadNames();
                if (_focusPending) FocusEditor();
            }));
        };
        ThemeManager.ThemeChanged += ApplyEditorTheme;
        _themeHooked = true;
        Disposed += (_, _) =>
        {
            if (_themeHooked) ThemeManager.ThemeChanged -= ApplyEditorTheme;
            SqlBarLayout.Changed -= OnBarLayoutChanged;
            _hints?.Dispose();
            _runCts?.Cancel(); _runCts?.Dispose();
            DisposePersistentConnection();
        };
        SetStatus("", null);
    }

    // =====================================================================  giao diện

    private PillButton Btn(string text, string tip, System.Action onClick, bool primary = false)
    {
        var b = PillButton.Flat(text, primary);
        b.OnPanelAlt = true;
        b.Margin = new Padding(0, 0, 4, 0);
        b.Height = NativeSkin.RowHeight(this);
        b.Click += (_, _) => onClick();
        NK.Tip(b, tip);
        return b;
    }

    private PillButton MenuBtn(string text, Func<WebMenu> build)
    {
        PillButton? b = null;
        b = Btn(text, "", () => { if (WebMenu.JustDismissed) return; build().Show(b!, 0, b!.Height + 2); });
        return b;
    }

    private void SetStatus(string text, Color? color)
    {
        _status.StatusColor = color;
        _status.Text = text;
        _status.Invalidate();
    }

    private void ConfigureEditor()
    {
        SqlScintilla.Configure(_ed); FcodeLexer.ApplySql(_ed);   // tô T-SQL đầy đủ như Monaco (hàm, dbo.X(...), chuỗi N'..')
        ConfigureDebugMargin();
        ConfigureGhost();
        ConfigureZoom();
        ConfigureFileDrop();
    }

    // ---- kéo thả file .sql / văn bản vào editor (như bản Web): editor trống → thay luôn, đã có chữ → chèn tại con trỏ ----

    private void ConfigureFileDrop()
    {
        _ed.AllowDrop = true;
        _ed.DragEnter += OnFileDragOver;
        _ed.DragOver += OnFileDragOver;
        _ed.DragDrop += OnFileDrop;
    }

    private static void OnFileDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
    }

    private void OnFileDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is not string[] paths) return;   // thả chữ thường: Scintilla tự xử lý
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) return;
        if (files.Any(f => new FileInfo(f).Length >= 50L * 1024 * 1024))
        {
            MessageBox.Show(this, "File quá lớn (từ 50 MB trở lên) — không đưa vào editor.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var texts = new List<string>();
        foreach (var f in files)
        {
            try { texts.Add(DecodeSqlFile(File.ReadAllBytes(f))); }
            catch (Exception ex) { SetStatus($"Không đọc được {Path.GetFileName(f)}: {ex.Message}", AppColors.Danger); }
        }
        if (texts.Count == 0) return;
        var content = string.Join("\r\n\r\n", texts);
        if (string.IsNullOrWhiteSpace(_ed.Text)) SetScriptText(content);
        else _ed.ReplaceSelection(content);
        FocusEditor();
    }

    /// <summary>Đoán encoding file .sql: BOM UTF-16 LE/BE, BOM UTF-8; không BOM thì UTF-8 hợp lệ, nếu không phải thì ANSI tiếng Việt (Windows-1258) — như bản Web.</summary>
    internal static string DecodeSqlFile(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException)
        {
            var ansi = CodePagesEncodingProvider.Instance.GetEncoding(1258) ?? Encoding.Latin1;
            return ansi.GetString(b);
        }
    }

    // ---- cỡ chữ editor: lưu settings.json, dùng chung mọi tab SQL Query (như bản Web) ----
    private static int? s_zoom;
    private static event Action<int, NativeSqlControl>? ZoomShared;

    private void ConfigureZoom()
    {
        _ed.Zoom = s_zoom ??= AppSettings.Load().SqlEditorZoom;
        ZoomShared += OnZoomShared;
        Disposed += (_, _) => ZoomShared -= OnZoomShared;
        _ed.UpdateUI += (_, _) => { var before = s_zoom; SaveZoomIfChanged(); if (s_zoom != before) ShowZoomBar(); };   // Ctrl+lăn chuột đổi Zoom của Scintilla
    }

    private void SaveZoomIfChanged()
    {
        if (_ed.Zoom == s_zoom) return;
        s_zoom = _ed.Zoom;
        try
        {
            var fresh = AppSettings.Load();   // nạp lại bản mới nhất rồi mới ghi, để không đè thay đổi settings khác
            fresh.SqlEditorZoom = _ed.Zoom;
            fresh.Save();
        }
        catch { /* không ghi được settings.json — vẫn áp cho phiên này */ }
        ZoomShared?.Invoke(_ed.Zoom, this);
    }

    private void OnZoomShared(int zoom, NativeSqlControl source)
    {
        if (ReferenceEquals(source, this) || IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(() => { if (_ed.Zoom != zoom) _ed.Zoom = zoom; }));
    }

    private void ChangeZoom(int delta)
    {
        _ed.Zoom = delta == 0 ? 0 : Math.Clamp(_ed.Zoom + delta, -8, 20);
        SaveZoomIfChanged();
        ShowZoomBar();
    }

    // ---- thanh phần trăm phóng to (hiện góc phải trên editor khi Ctrl + lăn chuột / Tăng-Giảm cỡ chữ / Ctrl+0; tự ẩn sau ~2,5 giây) ----
    private Panel? _zoomPanel;
    private Label? _zoomPct;
    private readonly System.Windows.Forms.Timer _zoomHide = new() { Interval = 2500 };

    private void ShowZoomBar()
    {
        if (IsDisposed || _ed.Parent is not { } host) return;
        if (_zoomPanel is null)
        {
            _zoomHide.Tick += (_, _) => { _zoomHide.Stop(); if (_zoomPanel is not null) _zoomPanel.Visible = false; };
            Disposed += (_, _) => _zoomHide.Dispose();
            _zoomPct = new Label { AutoSize = false, Width = 52, Height = 26, Left = 10, Top = 4, TextAlign = ContentAlignment.MiddleLeft };
            Button Btn(string text, int left, int width, Action act, bool accent = false)
            {
                var b = new Button { Text = text, Left = left, Top = 4, Width = width, Height = 26, FlatStyle = FlatStyle.Flat, TabStop = false, Cursor = Cursors.Hand };
                b.FlatAppearance.BorderSize = accent ? 1 : 0;
                b.Click += (_, _) => { act(); _ed.Focus(); };
                return b;
            }
            _zoomPanel = new Panel { Width = 62 + 30 + 30 + 78 + 10, Height = 34, Visible = false };
            _zoomPanel.Controls.AddRange(new Control[] { _zoomPct, Btn("−", 62, 30, () => ChangeZoom(-1)), Btn("+", 92, 30, () => ChangeZoom(1)), Btn("Đặt lại", 124, 78, () => ChangeZoom(0), accent: true) });
            _zoomPanel.MouseEnter += (_, _) => _zoomHide.Stop();
            foreach (Control c in _zoomPanel.Controls) c.MouseEnter += (_, _) => _zoomHide.Stop();
            _zoomPanel.MouseLeave += (_, _) => { _zoomHide.Stop(); _zoomHide.Start(); };
            host.Controls.Add(_zoomPanel);
        }
        _zoomPanel.BackColor = Bcode.App.UI.AppColors.PanelAlt;
        _zoomPct!.ForeColor = Bcode.App.UI.AppColors.Text;
        foreach (var b in _zoomPanel.Controls.OfType<Button>())
        {
            b.BackColor = Bcode.App.UI.AppColors.PanelAlt;
            b.ForeColor = b.FlatAppearance.BorderSize > 0 ? Bcode.App.UI.AppColors.Accent : Bcode.App.UI.AppColors.Text;
            b.FlatAppearance.BorderColor = Bcode.App.UI.AppColors.Accent;
        }
        var size = Math.Max(1, _ed.Styles[Style.Default].Size);   // 100% = cỡ chữ gốc; Zoom của Scintilla = số điểm cộng thêm
        _zoomPct.Text = Math.Max(10, (int)Math.Round((size + _ed.Zoom) * 100.0 / size)) + "%";
        _zoomPanel.Location = new Point(Math.Max(0, _ed.Right - _zoomPanel.Width - 30), _ed.Top + 6);
        _zoomPanel.Visible = true;
        _zoomPanel.BringToFront();
        _zoomHide.Stop(); _zoomHide.Start();
    }

    private void ApplyEditorTheme() { if (!IsDisposed) { SqlScintilla.ApplyTheme(_ed); FcodeLexer.ApplySql(_ed); ApplyNextLineColor(); ApplyGhostStyle(); } }

    // =====================================================================  ISqlTab — nội dung editor

    public void BeginUse() => ReloadNames();

    public void SetDatabase(bool useSysDatabase)
    {
        if (_useSys == useSysDatabase) return;
        _useSys = useSysDatabase;
        _db.Select(useSysDatabase ? 1 : 0, raise: false);
        ReloadNames();
    }

    public void RefreshSchema()
    {
        _hints?.ResetCache();
        ReloadNames();
    }

    private bool _focusPending;

    /// <summary>Đưa con trỏ về editor. Gọi khi tab chưa hiện / chưa có handle (vừa mở) thì nhớ lại và focus ngay khi hiện.</summary>
    public void FocusEditor()
    {
        try
        {
            if (IsHandleCreated && _ed.IsHandleCreated && Visible) { _ed.Select(); _ed.Focus(); _focusPending = false; }
            else _focusPending = true;
        }
        catch { /* đang đóng */ }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) _settings.SyncEditorFlagsFromDisk();   // tab khác có thể vừa đổi tuỳ chọn gợi ý / engine
        if (Visible && _focusPending && IsHandleCreated) BeginInvoke(new Action(FocusEditor));
    }

    public Task<string> GetScriptTextAsync() => Task.FromResult(_ed.Text ?? "");
    public Task<string> GetSelectedTextAsync() => Task.FromResult(_ed.SelectedText ?? "");
    public Task<long> GetEditorVersionAsync() => Task.FromResult(_version);

    public void SetScriptText(string text)
    {
        _ed.Text = text ?? "";
        _ed.EmptyUndoBuffer();
        _ed.GotoPosition(0);
    }

    public Task SetScriptTextAsync(string text) { SetScriptText(text); return Task.CompletedTask; }
    public Task OpenScriptAsync(string text) => SetScriptTextAsync(text);

    public Task InsertSnippetAsync(string text) { _ed.ReplaceSelection(text); FocusEditor(); return Task.CompletedTask; }

    /// <summary>Nhận diện kết quả pivot ở luồng nền (đọc controller trên share) rồi bật / ẩn nút Pivot — như chế độ Web.</summary>
    private void UpdatePivot(List<DataTable> tables, string script)
    {
        if (tables.Count == 0 || _resultTabOn) { _results.SetPivot(null); return; }
        var sourcePath = _service.Connections.Current?.SourcePath;
        _ = Task.Run(() =>
        {
            var payload = Bcode.App.Services.Rpt.PivotDetector.DetectForScript(tables, script, sourcePath);
            try { if (!IsDisposed) BeginInvoke(() => _results.SetPivot(payload)); } catch { /* tab đã đóng */ }
        });
    }

    /// <summary>Ctrl + chuột phải vào tên procedure / function / view / trigger (như chế độ Web và FCode): lấy CẢ tên đầy đủ dưới chuột
    /// (dbo.FastBusiness$Function$X — '$' và '.' là một phần tên) rồi nhờ MainForm tìm object và chèn định nghĩa lên đầu script, script đang gõ nằm dưới GO.</summary>
    private void OnCtrlRightClick(Point at)
    {
        var pos = _ed.CharPositionFromPoint(at.X, at.Y);
        if (pos < 0 || pos > _ed.TextLength) return;
        var line = _ed.Lines[_ed.LineFromPosition(pos)];
        var text = line.Text;
        var col = Math.Clamp(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(text), 0, Math.Clamp(pos - line.Position, 0, Encoding.UTF8.GetByteCount(text))).Length, 0, text.Length);
        static bool IsId(char c) => char.IsLetterOrDigit(c) || c is '_' or '$' or '#' or '.' or '@' or '[' or ']';
        int from = col, to = col;
        while (from > 0 && IsId(text[from - 1])) from--;
        while (to < text.Length && IsId(text[to])) to++;
        var ident = text[from..to].TrimStart('@').Trim('.');
        if (ident.Length == 0) return;
        OpenProcedureWithQueryRequested?.Invoke(ident, _useSys, _ed.Text);
    }

    /// <summary>Chèn định nghĩa procedure/function lên đầu script (kèm GO); đã có sẵn thì chỉ nhảy tới.</summary>
    public Task PrependDefinitionAsync(string definition)
    {
        var def = (definition ?? "").TrimEnd();
        if (def.Length == 0) return Task.CompletedTask;
        _ed.TargetStart = 0; _ed.TargetEnd = _ed.TextLength;
        _ed.SearchFlags = SearchFlags.None;
        var at = _ed.SearchInTarget(def);
        if (at >= 0) { _ed.GotoPosition(at); FocusEditor(); return Task.CompletedTask; }
        _ed.InsertText(0, def + Environment.NewLine + "GO" + Environment.NewLine + Environment.NewLine);
        _ed.GotoPosition(0);
        FocusEditor();
        return Task.CompletedTask;
    }

    /// <summary>"Debug store/function": nạp định nghĩa store/function vào editor rồi bắt đầu debug từng bước ngay (hỏi tham số nếu có).</summary>
    public async Task LoadAndDebugAsync(string definition, string? callText)
    {
        StopStepDebug();
        SetScriptText(definition);
        await StartStepDebugAsync(callText);
    }

    // =====================================================================  Thanh nút tuỳ biến (như sqlquerybar.html)
    //
    // Bố cục lưu ở SqlBarLayout (sqlbar_layout.json) — DÙNG CHUNG với bản Web, cùng khoá nút: mỗi nút được ghim ngoài thanh, gom vào nhóm "Tên ▾" (bấm ra menu) hoặc ẩn.
    // "Tùy chỉnh thanh…" (chuột phải lên thanh, hoặc menu Tùy chọn) mở màn Quick Access để kéo thả; lưu xong mọi tab SQL Query đang mở áp ngay.

    private sealed record BarItem(string Key, string Label, string Kind, Action<Control>? Run, Func<bool>? On, string? Kb);

    private readonly Dictionary<string, Control> _barCtl = new();      // khoá nút -> control trên thanh (ghim)
    private readonly List<PillButton> _groupBtns = new();               // nút nhóm "Tên ▾", theo thứ tự trên thanh
    private List<BarItem>? _barItems;

    private List<BarItem> BarItems() => _barItems ??= new List<BarItem>
    {
        new("run", "▶ Execute", "button", c => { if (_running) CancelRun(); else _ = RunAsync(); }, null, "editor.run"),
        new("db", "Database", "select", null, null, null),
        new("beauty", "✦ Beauty", "button", _ => BeautyFormat(), null, "editor.beauty"),
        new("debug-target", "Debug store/function", "button", c => { _ = PickDebugTargetAsync(); }, null, "editor.debugTarget"),
        new("debug-step", "Debug từng bước", "toggle", _ => ToggleDebugStep(), () => _debugStepOn || _plan is not null, "editor.debugStep"),
        new("comment", "Comment", "button", _ => ToggleComment(true), null, "editor.comment"),
        new("uncomment", "Uncomment", "button", _ => ToggleComment(false), null, "editor.uncomment"),
        new("default-type", "Default Type (chữ HOA / thường)", "select", null, null, null),
        new("write-schema", "Write Schema", "button", _ => WriteSchema(), null, "editor.writeSchema"),
        new("check-fields", "Check Fields", "button", c => { _ = CheckFieldsAsync(); }, null, "editor.checkFields"),
        new("save-history", "Lưu lịch sử", "button", _ => SaveToHistory(), null, "editor.saveHistory"),
        new("history", "Lịch sử sửa procedure / function", "button", _ => BeginInvoke(new Action(OpenSqlHistory)), null, null),
        new("ask-claude", "→ Claude", "button", _ => SendToAi("claude"), null, null),
        new("ask-gemini", "→ Gemini", "button", _ => SendToAi("gemini"), null, null),
        new("open", "Open", "button", _ => OpenFile(), null, "editor.open"),
        new("save", "Save", "button", _ => SaveFile(), null, "editor.save"),
        new("suggest", "Suggest Param/Caret", "toggle", _ => ToggleSuggest(), () => _suggestOn, "editor.toggleSuggest"),
        new("reset-conn", "Reset Connection", "toggle", _ => ToggleResetConn(), () => _resetConnOn, "editor.toggleResetConn"),
        new("result-tab", "Result Tab", "toggle", _ => ToggleResultTab(), () => _resultTabOn, "editor.toggleResultTab"),
        new("wrap", "Wrap (tự xuống dòng)", "check", _ => ToggleWrap(), () => _wordWrap, "editor.wrap"),
        new("options", "Options…", "button", anchor => { if (!WebMenu.JustDismissed) BuildOptionsMenu().Show(anchor, 0, anchor.Height + 2); }, null, "editor.options"),
    };

    /// <summary>Bố cục gợi ý (giữ dáng thanh cũ của tab native: Execute · Database · Beauty · Default Type · Options · nhóm Script / Lịch sử / File); các nút còn lại ẩn
    /// vì đã nằm trong menu Options. Dùng khi chưa tuỳ biến lần nào và làm "Gợi ý nhóm" trong màn tuỳ chỉnh.</summary>
    private static SqlBarLayout DefaultBarLayout() => new()
    {
        Pinned = new List<string> { "run", "db", "beauty", "default-type", "options" },
        Groups = new List<ToolBarGroup>
        {
            new() { Name = "Script", Keys = new List<string> { "comment", "uncomment", "write-schema", "check-fields" } },
            new() { Name = "Lịch sử", Keys = new List<string> { "save-history", "history" } },
            new() { Name = "File", Keys = new List<string> { "open", "save" } },
        },
        Hidden = new List<string> { "debug-target", "debug-step", "suggest", "reset-conn", "result-tab", "wrap", "ask-claude", "ask-gemini" },
    };

    /// <summary>Bố cục đang dùng: file đã lưu (hoặc mặc định), bỏ khoá lạ / trùng; nút mới chưa có trong bố cục thì ghim (như bản Web).</summary>
    private SqlBarLayout EffectiveBarLayout()
    {
        var saved = SqlBarLayout.Load() ?? DefaultBarLayout();
        var known = BarItems().Select(i => i.Key).ToList();
        var seen = new HashSet<string>();
        bool Keep(string k) => known.Contains(k) && seen.Add(k);
        var layout = new SqlBarLayout
        {
            Pinned = saved.Pinned.Where(Keep).ToList(),
            Groups = saved.Groups.Select(g => new ToolBarGroup { Name = g.Name, Keys = g.Keys.Where(Keep).ToList() }).Where(g => g.Keys.Count > 0).ToList(),
            Hidden = saved.Hidden.Where(Keep).ToList(),
        };
        layout.Pinned.AddRange(known.Where(k => !seen.Contains(k)));
        return layout;
    }

    private string BarTip(BarItem it) => it.Kb is { } kb && ShortcutRegistry.Display(kb) is { Length: > 0 } sc ? $"{it.Label}  ({sc})" : it.Label;

    private Control BarControlFor(BarItem it)
    {
        if (_barCtl.TryGetValue(it.Key, out var existing)) return existing;
        Control ctl = it.Key switch
        {
            "run" => _btnRun,
            "db" => _db,
            "default-type" => _defaultType,
            _ => MakeBarButton(it),
        };
        _barCtl[it.Key] = ctl;
        return ctl;
    }

    private PillButton MakeBarButton(BarItem it)
    {
        PillButton? b = null;
        b = Btn(it.Label, BarTip(it), () => it.Run?.Invoke(b!));
        return b;
    }

    private void RebuildBar()
    {
        var layout = EffectiveBarLayout();
        var items = BarItems().ToDictionary(i => i.Key);
        _bar.SuspendLayout();
        _bar.Controls.Clear();
        foreach (var gb in _groupBtns) gb.Dispose();
        _groupBtns.Clear();
        foreach (var key in layout.Pinned) _bar.Controls.Add(BarControlFor(items[key]));
        foreach (var g in layout.Groups)
        {
            var group = g;
            PillButton? btn = null;
            btn = MenuBtn(group.Name + " ▾", () => BuildGroupMenu(group, btn!));
            NK.Tip(btn, string.Join(", ", group.Keys.Select(k => items[k].Label)));
            _groupBtns.Add(btn);
            _bar.Controls.Add(btn);
        }
        _bar.ResumeLayout(true);
        RefreshBarToggles();
    }

    private WebMenu BuildGroupMenu(ToolBarGroup group, Control anchor)
    {
        var menu = new WebMenu().AddCaption(group.Name);
        foreach (var key in group.Keys)
        {
            var it = BarItems().First(i => i.Key == key);
            var sc = it.Kb is { } kb ? ShortcutRegistry.Display(kb) : "";
            if (it.Key == "db")
                menu.AddSub(it.Label, sub =>
                {
                    sub.Add("App Data", () => _db.Select(0, raise: true), shortcut: ShortcutRegistry.Display("editor.dbApp"), @checked: !_useSys);
                    sub.Add("Sys Data", () => _db.Select(1, raise: true), shortcut: ShortcutRegistry.Display("editor.dbSys"), @checked: _useSys);
                });
            else if (it.Key == "default-type")
                menu.AddSub(it.Label, sub =>
                {
                    sub.Add("UPPER Keyword", () => ApplyDefaultType(true));
                    sub.Add("lower Keyword", () => ApplyDefaultType(false));
                });
            else
                menu.Add(it.Label, () => it.Run?.Invoke(anchor), shortcut: string.IsNullOrEmpty(sc) ? null : sc, @checked: it.On?.Invoke() ?? false);
        }
        menu.AddSeparator().Add("Tùy chỉnh thanh…", CustomizeBar);
        return menu;
    }

    /// <summary>Nút bật/tắt (Debug từng bước, Suggest, Reset Connection, Result Tab, Wrap) sáng màu khi đang bật.</summary>
    private void RefreshBarToggles()
    {
        foreach (var it in BarItems())
            if (it.On is not null && _barCtl.TryGetValue(it.Key, out var c) && c is PillButton pb)
            {
                var on = it.On();
                if (pb.IsPrimary != on) { pb.IsPrimary = on; pb.Invalidate(); }
            }
    }

    private void OpenBarGroup(int index)
    {
        if (index < 1 || index > _groupBtns.Count) return;
        var b = _groupBtns[index - 1];
        b.PerformClick();
    }

    private void OnBarLayoutChanged(SqlBarLayout? layout)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(RebuildBar));
    }

    /// <summary>"Tùy chỉnh thanh…": dùng lại màn Quick Access (Ghim / Nhóm / Ẩn, kéo thả); lưu SqlBarLayout → mọi tab SQL Query áp ngay.</summary>
    private void CustomizeBar()
    {
        var items = BarItems();
        var labels = items.ToDictionary(i => i.Key, i => (Label: i.Label, Shortcut: i.Kb is { } kb ? ShortcutRegistry.Display(kb) : ""));
        var cur = EffectiveBarLayout();
        var def = DefaultBarLayout();
        var order = cur.Pinned.Concat(cur.Groups.SelectMany(g => g.Keys)).Concat(cur.Hidden).Where(labels.ContainsKey).Distinct().ToList();
        order.AddRange(labels.Keys.Where(k => !order.Contains(k)));
        var defOrder = def.Pinned.Concat(def.Groups.SelectMany(g => g.Keys)).ToList();

        using var form = new Bcode.App.Forms.QuickAccessForm(order.Select(k => (k, labels[k].Label)), new HashSet<string>(cur.Hidden), defOrder,
            cur.Groups, def.Groups,
            title: "Tùy chỉnh thanh SQL Query",
            intro: "<b>Kéo thả</b> nút giữa các vùng: <b>Ghim</b> = nút riêng trên thanh, <b>nhóm</b> = gom vào 1 nút “Tên ▾” bấm ra menu, <b>Ẩn</b> = không hiện. "
                 + "Áp cho mọi tab SQL Query. Phím mở nhóm thứ 1…9: Template → Phím tắt → “Mở nhóm thứ N trên thanh SQL Query”. Phím tắt riêng của từng nút (F5, F7, Alt+Z…) không đổi. "
                 + "“Gợi ý nhóm” = bố cục mặc định.",
            toolShortcut: k => labels.TryGetValue(k, out var v) ? v.Shortcut : "",
            groupShortcut: i => ShortcutRegistry.Display($"editor.barGroup{i}"));
        if (form.ShowDialog(FindForm()) != DialogResult.OK) return;
        var grouped = new HashSet<string>(form.Groups.SelectMany(g => g.Keys));
        SqlBarLayout.Save(new SqlBarLayout
        {
            Pinned = form.OrderedKeys.Where(k => !form.HiddenKeys.Contains(k) && !grouped.Contains(k)).ToList(),
            Groups = form.Groups,
            Hidden = form.HiddenKeys.ToList(),
        });
    }

    private void ToggleSuggest()
    {
        _suggestOn = !_suggestOn;
        if (_hints is not null) _hints.Enabled = _suggestOn;
        RefreshBarToggles();
    }

    private void ToggleResetConn()
    {
        _resetConnOn = !_resetConnOn;
        if (_resetConnOn) DisposePersistentConnection();
        RefreshBarToggles();
    }

    private void ToggleResultTab()
    {
        _resultTabOn = !_resultTabOn;
        RefreshBarToggles();
    }

    /// <summary>Check Fields: SET NOEXEC ON rồi chạy thử script để SQL Server báo bảng / cột sai mà không thực thi gì.</summary>
    private async Task CheckFieldsAsync()
    {
        SetStatus("Đang kiểm tra (SET NOEXEC ON)...", AppColors.TextMuted);
        try
        {
            var error = await _service.CheckFieldsAsync(_ed.Text, _useSys);
            if (error is null) SetStatus("Check Fields: OK — mọi bảng/cột trong script đều hợp lệ.", AppColors.Success);
            else
            {
                SetStatus($"Check Fields: {error}", AppColors.Danger);
                MessageBox.Show(this, error, "Bcode — Check Fields", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            SetStatus("Lỗi Check Fields.", AppColors.Danger);
            MessageBox.Show(this, ex.Message, "Bcode — Check Fields", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // =====================================================================  AI: Ctrl+I sửa/sinh SQL + gợi ý ghost-text
    //
    // Cùng prompt / engine / cách kèm cấu trúc bảng như bản Web (xem SqlAiAssist). Scintilla không có ghost-text chèn giữa dòng nên gợi ý hiện thành khối chữ xám nghiêng
    // ngay DƯỚI dòng con trỏ (annotation): Tab = nhận (chèn tại con trỏ), Esc / gõ tiếp / dời con trỏ = bỏ. Tự gợi ý khi ngừng gõ ~0,6s nhưng chỉ trong script
    // CREATE/ALTER procedure/function (như Web); Alt+\ gọi tay ở mọi loại SQL.

    private SqlAiAssist Ai => _ai ??= new SqlAiAssist(_service, _settings);

    private void ConfigureGhost()
    {
        _ed.AnnotationVisible = ScintillaNET.Annotation.Standard;
        ApplyGhostStyle();
        _ed.CharAdded += (_, _) =>
        {
            ClearGhost();
            if (_settings.EnableFastGhost) { _fastGhostTimer.Stop(); _fastGhostTimer.Start(); }
            if (_settings.EnableCopilotSuggest) { _ghostTimer.Stop(); _ghostTimer.Start(); }
        };
        // Ghost text theo cấu trúc Fast (không AI): ngừng gõ ~0,15s là xét luật, khớp thì hiện ngay (AI 0,6s sau thấy đang có gợi ý thì thôi).
        _fastGhostTimer.Tick += (_, _) => { _fastGhostTimer.Stop(); ShowFastGhost(); };
        Disposed += (_, _) => { _fastGhostTimer.Stop(); _fastGhostTimer.Dispose(); };
        _ed.UpdateUI += (_, _) => { if (_ghostText is not null && _ed.CurrentPosition != _ghostPos) ClearGhost(); };
        _ed.TextChanged += (_, _) => { if (_ghostText is not null && _version != _ghostVersion) ClearGhost(); };
        _ghostTimer.Tick += (_, _) => { _ghostTimer.Stop(); _ = SuggestAsync(explicitCall: false); };
        Disposed += (_, _) => { _ghostTimer.Stop(); _ghostTimer.Dispose(); _ghostCts?.Cancel(); _ghostCts?.Dispose(); };
    }

    private readonly System.Windows.Forms.Timer _fastGhostTimer = new() { Interval = 150 };

    /// <param name="manual">Gọi tay (sau khi đổi dài ⇄ ngắn): chạy cả khi tắt ghost Fast tự động, thay ghost đang hiện. Chế độ lấy theo Tùy chọn.</param>
    private void ShowFastGhost(bool manual = false)
    {
        if (manual) ClearGhost();
        if (IsDisposed || (!_settings.EnableFastGhost && !manual) || _ghostText is not null || _ed.SelectionStart != _ed.SelectionEnd) return;
        var pos = _ed.CurrentPosition;
        var caret = _ed.GetTextRange(0, pos).Length;   // vị trí Scintilla là byte UTF-8 → đổi sang vị trí ký tự
        var types = FastGhostRules.LoadColumnTypes(GhostDbKey, () => _service.CreateConnection(_useSys));
        string? text = null;
        try { text = FastGhostRules.SuggestByMode(_settings.FastGhostLong, _ed.Text, caret, types, FastGhostRules.KnowledgeFor(GhostDbKey)); }
        catch { /* luật lỗi thì coi như không gợi ý */ }
        if (!string.IsNullOrEmpty(text) && _ed.CurrentPosition == pos) ShowGhost(text);
    }

    /// <summary>Đổi ghost Fast dài ⇄ ngắn (lưu Tùy chọn cho mọi tab), báo ở dòng trạng thái rồi gợi ý lại ngay theo chế độ mới.</summary>
    private void ToggleFastGhostLong()
    {
        _settings.FastGhostLong = AppSettings.Update(s => s.FastGhostLong = !s.FastGhostLong).FastGhostLong;
        SetStatus(_settings.FastGhostLong ? "Gợi ý Fast: DÀI (cả dòng theo chính file)" : "Gợi ý Fast: NGẮN (chỉ gợi ý chắc ≥ 90%)", null);
        FocusEditor();
        ShowFastGhost(manual: true);
    }

    private void ApplyGhostStyle()
    {
        var st = _ed.Styles[GhostStyle];
        st.ForeColor = AppColors.TextMuted;
        st.BackColor = _ed.Styles[Style.Default].BackColor;
        st.Italic = true;
        st.Font = _ed.Styles[Style.Default].Font;
        st.Size = _ed.Styles[Style.Default].Size;
    }

    private void ClearGhost()
    {
        _ghostTimer.Stop();
        _ghostCts?.Cancel();
        if (_ghostText is null) return;
        if (_ghostLine >= 0 && _ghostLine < _ed.Lines.Count) _ed.Lines[_ghostLine].AnnotationText = null;
        _ghostText = null;
    }

    private void ShowGhost(string text)
    {
        var line = _ed.CurrentLine;
        _ghostText = text;
        _ghostPos = _ed.CurrentPosition;
        _ghostLine = line;
        _ghostVersion = _version;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var shown = lines.Length > 12 ? lines.Take(12).Append($"… (+{lines.Length - 12} dòng)").ToArray() : lines;
        _ed.Lines[line].AnnotationText = "Tab: nhận gợi ý · Esc: bỏ\n" + string.Join("\n", shown);
        _ed.Lines[line].AnnotationStyle = GhostStyle;
        _ed.AnnotationVisible = ScintillaNET.Annotation.Standard;
    }

    private bool AcceptGhost()
    {
        if (_ghostText is not { } text) return false;
        var pos = _ed.CurrentPosition;
        ClearGhost();
        text = text.Replace("\r\n", "\n").Replace("\n", _ed.EolMode == Eol.CrLf ? "\r\n" : _ed.EolMode == Eol.Cr ? "\r" : "\n");
        _ed.InsertText(pos, text);
        _ed.GotoPosition(pos + Encoding.UTF8.GetByteCount(text));
        return true;
    }

    private static readonly Regex ProcOrFuncRegex = new(@"\b(CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(PROC|PROCEDURE|FUNCTION)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Hỏi AI gợi ý tại con trỏ. Tự động (đang gõ): cần bật "Gợi ý AI tự động", có API key, không đang chạy script / chọn chữ, và script là procedure/function.</summary>
    private async Task SuggestAsync(bool explicitCall)
    {
        if (IsDisposed || _running) return;
        if (!Ai.HasKey)
        {
            if (explicitCall) SetStatus("Chưa có API key — Tùy chọn > Cấu hình Claude / Gemini API Key.", AppColors.Warning);
            return;
        }
        if (_ed.SelectionStart != _ed.SelectionEnd) return;
        if (!explicitCall && (!_settings.EnableCopilotSuggest || !ProcOrFuncRegex.IsMatch(_ed.Text))) return;
        if (!explicitCall && _ghostText is not null) return;   // đang có gợi ý theo luật Fast — không gọi AI đè lên

        // Cắt ngữ cảnh như bản Web: file dài (>80 dòng) thì giữ 40 dòng đầu (tham số / DECLARE) + 50 dòng sát con trỏ; phía sau lấy 40 dòng.
        var pos = _ed.CurrentPosition;
        var caretLine = _ed.LineFromPosition(pos);
        string prefix;
        if (caretLine + 1 > 80)
        {
            var header = _ed.GetTextRange(0, _ed.Lines[39].Position - 0);
            var nearStart = _ed.Lines[Math.Max(40, caretLine - 50)].Position;
            prefix = header + "\n\n-- ... [code omitted] ...\n\n" + _ed.GetTextRange(nearStart, pos - nearStart);
        }
        else prefix = _ed.GetTextRange(0, pos);
        if (string.IsNullOrWhiteSpace(prefix)) return;
        var endPos = _ed.Lines[Math.Min(_ed.Lines.Count - 1, caretLine + 40)].EndPosition;
        var suffix = _ed.GetTextRange(pos, Math.Max(0, endPos - pos));

        _ghostCts?.Cancel();
        _ghostCts?.Dispose();
        _ghostCts = new CancellationTokenSource();
        var token = _ghostCts.Token;
        var version = _version;
        SetStatus($"Copilot ({Ai.Engine}): Đang phân tích SQL...", AppColors.TextMuted);
        var (text, status, isError) = await Ai.SuggestAsync(prefix, suffix, _useSys, token);
        if (IsDisposed || token.IsCancellationRequested) return;
        if (!_running && status.Length > 0) SetStatus(status, isError ? AppColors.Danger : text.Length > 0 ? AppColors.Success : AppColors.TextMuted);
        // Gõ tiếp / dời con trỏ trong lúc chờ AI → gợi ý đã cũ.
        if (text.Length > 0 && _version == version && _ed.CurrentPosition == pos) ShowGhost(text);
    }

    private void ConfigureApiKey(bool gemini)
    {
        var key = Bcode.App.Forms.SimplePromptForm.Show(this, gemini ? "Gemini API Key" : "Claude API Key",
            gemini ? "Nhập Google Gemini API Key:" : "Nhập Anthropic API Key (sk-ant-...):", gemini ? _settings.GeminiApiKey : _settings.AnthropicApiKey);
        if (key is null) return;
        // ghi trên bản settings mới nhất (tab giữ bản riêng — ghi cả bản cũ sẽ đè thay đổi ở tab / màn khác)
        var saved = AppSettings.Update(s =>
        {
            if (gemini) s.GeminiApiKey = key.Trim();
            else { s.AnthropicApiKey = key.Trim(); if (s.AnthropicApiKey.Length > 0) s.CopilotEngine = "claude"; }   // có key Claude thì dùng luôn Claude cho gợi ý SQL
        });
        (_settings.GeminiApiKey, _settings.AnthropicApiKey, _settings.CopilotEngine) = (saved.GeminiApiKey, saved.AnthropicApiKey, saved.CopilotEngine);
        MessageBox.Show(this, gemini ? "Đã lưu Gemini API Key." : "Đã lưu Claude API Key. Gợi ý SQL sẽ dùng Claude.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>Ctrl+I: hộp "AI: Sửa/Sinh SQL" — sửa đoạn đang chọn hoặc sinh SQL tại con trỏ theo yêu cầu, rồi Insert.</summary>
    private void ShowAiEdit()
    {
        if (!Ai.HasKey)
        {
            MessageBox.Show(this, "Chưa có API key — vào Tùy chọn > Cấu hình Claude (Anthropic) hoặc Gemini API Key.", "Bcode — AI", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        ClearGhost();
        var selStart = Math.Min(_ed.SelectionStart, _ed.SelectionEnd);
        var selEnd = Math.Max(_ed.SelectionStart, _ed.SelectionEnd);
        var selected = _ed.GetTextRange(selStart, selEnd - selStart);
        var script = _ed.Text;
        var useSys = _useSys;
        using var form = new Bcode.App.Forms.SqlAiEditForm(instruction => Ai.EditAsync(instruction, selected, script, useSys), selected.Length > 0, Ai.Engine ?? "AI");
        if (form.ShowDialog(FindForm()) != DialogResult.OK || form.ResultText.Length == 0) { FocusEditor(); return; }
        // Có chọn đoạn → thay đoạn đó; không chọn → chèn tại con trỏ (vị trí lúc mở hộp).
        _ed.SetSelection(selEnd, selStart);
        _ed.ReplaceSelection(form.ResultText);
        FocusEditor();
    }

    // =====================================================================  Debug từng bước
    //
    // Cùng cách chạy với bản Web (RawSqlControl): phiên debug giữ 1 StepPlan (SqlStepPlanner) — mỗi bước chạy script từ đầu tới hết dòng đích trong 1 transaction rồi
    // ROLLBACK, kèm SELECT các biến đã khai báo. Dòng "sắp chạy" tô vàng + mũi tên ở lề, F10 = bước kế, F5 / Execute = chạy tới breakpoint (chấm đỏ ở lề, bấm lề để
    // đặt/bỏ) hoặc hết, Ctrl+F10 = chạy tới dòng con trỏ, Shift+F5 = dừng.

    private void ConfigureDebugMargin()
    {
        var m = _ed.Margins[1];
        m.Type = MarginType.Symbol;
        m.Width = NativeSkin.S(16);
        m.Sensitive = true;
        m.Cursor = MarginCursor.Arrow;
        m.Mask = (1u << MarkBreakpoint) | (1u << MarkNextArrow) | (1u << MarkSafeLine);
        // Dòng debug dừng được: vạch vàng mờ ở lề (breakpoint / mũi tên vẽ đè lên).
        _ed.Markers[MarkSafeLine].Symbol = MarkerSymbol.LeftRect;
        _ed.Markers[MarkSafeLine].SetBackColor(Color.FromArgb(0xC8, 0xA0, 0x30));
        _ed.Markers[MarkSafeLine].SetForeColor(Color.FromArgb(0xC8, 0xA0, 0x30));
        _ed.Markers[MarkBreakpoint].Symbol = MarkerSymbol.Circle;
        _ed.Markers[MarkBreakpoint].SetForeColor(Color.FromArgb(0xE5, 0x14, 0x00));
        _ed.Markers[MarkBreakpoint].SetBackColor(Color.FromArgb(0xE5, 0x14, 0x00));
        _ed.Markers[MarkNextArrow].Symbol = MarkerSymbol.ShortArrow;
        _ed.Markers[MarkNextArrow].SetForeColor(Color.FromArgb(0xFF, 0xCC, 0x00));
        _ed.Markers[MarkNextArrow].SetBackColor(Color.FromArgb(0xFF, 0xCC, 0x00));
        _ed.Markers[MarkNextLine].Symbol = MarkerSymbol.Background;
        ApplyNextLineColor();
        _ed.MarginClick += (_, e) =>
        {
            if (e.Margin != 1) return;
            var line = _ed.LineFromPosition(e.Position);
            if ((_ed.Lines[line].MarkerGet() & (1u << MarkBreakpoint)) != 0) _ed.Lines[line].MarkerDelete(MarkBreakpoint);
            else _ed.Lines[line].MarkerAdd(MarkBreakpoint);
        };
    }

    /// <summary>Nền dòng "sắp chạy": vàng pha 22% vào màu nền hiện tại của editor (marker Background của Scintilla.NET 5.3 không có lớp mờ nên pha sẵn — vẫn đọc được chữ, theo theme sáng/tối).</summary>
    private void ApplyNextLineColor()
    {
        var bg = _ed.Styles[Style.Default].BackColor;
        const double k = 0.22;
        int Mix(int under, int over) => (int)Math.Round(under * (1 - k) + over * k);
        _ed.Markers[MarkNextLine].Symbol = MarkerSymbol.Background;
        _ed.Markers[MarkNextLine].SetBackColor(Color.FromArgb(Mix(bg.R, 0xFF), Mix(bg.G, 0xCC), Mix(bg.B, 0x00)));
        // Đoạn của bước debug vừa lỗi: đỏ pha cùng tỉ lệ.
        _ed.Markers[MarkErrorLine].Symbol = MarkerSymbol.Background;
        _ed.Markers[MarkErrorLine].SetBackColor(Color.FromArgb(Mix(bg.R, 0xE5), Mix(bg.G, 0x14), Mix(bg.B, 0x00)));
    }

    private void BuildDebugBar()
    {
        _dbgBar.Controls.Add(_dbgInfo);
        var find = Btn("🔍 Tìm lỗi", "Tự tìm bước đầu tiên bị lỗi rồi dừng ở đó (Alt+F10) — chia đôi các điểm dừng, mỗi lần chạy đều rollback", () => { _ = DebugFindErrorAsync(); FocusEditor(); });
        _dbgBar.Controls.Add(find);
        _dbgDyn = Btn("🧩 SQL động", "Xem + kiểm tra cú pháp câu SQL đang ghép trong biến (vd @q) ở điểm dừng này — mở tab mới, dòng lỗi có đánh dấu (Alt+Shift+F10)", () => { _ = ShowDynamicSqlAsync(); });
        _dbgBar.Controls.Add(_dbgDyn);
        _dbgBack = Btn("⤴ Back", "Lùi về điểm dừng trước (Ctrl+Shift+F10) — chạy lại từ đầu tới đó, đã rollback nên không ảnh hưởng dữ liệu", () => { _ = DebugCommandAsync("back"); FocusEditor(); });
        _dbgBar.Controls.Add(_dbgBack);
        _dbgNext = Btn("⤵ Step", "Chạy dòng kế (F10)", () => { _ = DebugCommandAsync("step"); FocusEditor(); }, primary: true);
        _dbgCont = Btn("▶ Continue", "Chạy tiếp tới breakpoint / hết (F5)", () => { _ = DebugCommandAsync("continue"); FocusEditor(); });
        _dbgCursor = Btn("⇥ To cursor", "Chạy tới dòng con trỏ (Ctrl+F10)", () => { _ = DebugCommandAsync("to-cursor", _ed.CurrentLine + 1); FocusEditor(); });
        var stop = Btn("⏹ Stop", "Dừng debug (Shift+F5)", () => { StopStepDebug(); FocusEditor(); });
        _dbgBar.Controls.AddRange(new Control[] { _dbgNext, _dbgCont, _dbgCursor, stop });
    }

    /// <summary>Số dòng (từ 1) đang có chấm breakpoint — Scintilla giữ chấm bám theo dòng khi sửa chữ, nên đọc thẳng từ marker.</summary>
    private List<int> BreakpointLines()
    {
        var result = new List<int>();
        var mask = 1u << MarkBreakpoint;
        for (var l = _ed.Lines[0].MarkerNext(mask); l >= 0; l = l + 1 < _ed.Lines.Count ? _ed.Lines[l + 1].MarkerNext(mask) : -1) result.Add(l + 1);
        return result;
    }

    /// <summary>Chọn câu EXEC store / gọi function trong script rồi mở debug (tab riêng cho store/function đó) — nút "Debug store/function".</summary>
    private async Task PickDebugTargetAsync()
    {
        var scanner = new DebugTargetScanner(_sqlObjectService);
        List<Bcode.App.Models.DebugCandidate> candidates;
        try { candidates = await scanner.ScanAsync(_ed.Text, _useSys); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Debug store/function", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        if (candidates.Count == 0)
        {
            MessageBox.Show(this, "Không tìm thấy câu EXEC store hoặc gọi function nào trong script hiện tại để debug.", "Bcode — Debug store/function", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var form = new Bcode.App.Forms.ChooseDebugTargetForm(candidates);
        if (form.ShowDialog(this) == DialogResult.OK && form.Selected is { } chosen)
            DebugTargetChosen?.Invoke(chosen.Target, chosen.CallText);
    }

    private void ToggleDebugStep()
    {
        if (_debugStepOn || _plan is not null) { StopStepDebug(); return; }
        _debugStepOn = true;
        _ = StartStepDebugAsync();
    }

    /// <summary>Bắt đầu phiên debug cho script đang có trong editor. <paramref name="callText"/> (nếu có) = câu EXEC đã chọn ở "Debug store/function" — điền sẵn giá trị tham số.</summary>
    public async Task StartStepDebugAsync(string? callText = null)
    {
        var text = _ed.Text;
        if (string.IsNullOrWhiteSpace(text)) { _debugStepOn = false; return; }

        var (name, parameters, _) = SqlStepPlanner.ParseHeader(text.Replace("\r\n", "\n").Replace("\r", "\n"));
        Dictionary<string, string>? values = null;
        if (parameters.Any(p => !p.ReadOnly))
        {
            using var form = new Bcode.App.Forms.StepParamsForm(name ?? "Script", parameters, Bcode.App.Forms.StepParamsForm.InitialValues(text, name, callText, parameters));
            if (form.ShowDialog(this) != DialogResult.OK) { _debugStepOn = false; return; }
            values = form.Values;
        }

        var plan = SqlStepPlanner.Build(text, values);
        if (plan.SafeLines.Count == 0)
        {
            SetStatus("Không tìm thấy dòng nào có thể dừng để debug từng bước trong script này.", AppColors.Danger);
            _debugStepOn = false;
            return;
        }

        _plan = plan;
        _planText = text;
        _planValues = values;
        _executedLine = _lastOkLine = _errorFrom = _errorLine = 0;
        _lastVars = null;
        _debugStepOn = true;
        _results.Clear();
        _results.ClearMessages();
        SetStatus($"Debug từng bước{(name is null ? "" : " — " + name)}: {plan.SafeLines.Count} điểm dừng. F10 bước kế · F5 chạy tiếp · Ctrl+F10 chạy tới con trỏ · Shift+F5 dừng.", AppColors.Success);
        PushDebugState();
        await Task.CompletedTask;
    }

    private void PushDebugState()
    {
        if (IsDisposed) return;
        RefreshBarToggles();
        var next = _plan?.NextSafe(_executedLine) ?? 0;
        var active = _plan is not null;
        _ed.MarkerDeleteAll(MarkNextArrow);
        _ed.MarkerDeleteAll(MarkNextLine);
        _ed.MarkerDeleteAll(MarkErrorLine);
        _ed.MarkerDeleteAll(MarkSafeLine);
        _dbgBar.Visible = active;
        if (!active) return;
        foreach (var l in _plan!.SafeLines) if (l <= _ed.Lines.Count) _ed.Lines[l - 1].MarkerAdd(MarkSafeLine);
        // Đoạn của bước vừa lỗi: nền đỏ (bước trước chạy được nên lỗi nằm trong đoạn này).
        for (var l = _errorFrom; l > 0 && l <= _errorLine && l <= _ed.Lines.Count; l++) _ed.Lines[l - 1].MarkerAdd(MarkErrorLine);
        var remaining = _plan!.SafeLines.Count(l => l > _executedLine);
        _dbgInfo.Text = next > 0 ? $"Sắp chạy dòng {next} · còn {remaining} điểm dừng" : "Đã chạy hết script";
        foreach (var b in new[] { _dbgNext, _dbgCont }) if (b is not null) b.Enabled = next > 0;
        if (_dbgBack is not null) _dbgBack.Enabled = _executedLine > 0;
        if (_dbgDyn is not null) _dbgDyn.Enabled = DynamicSqlInspector.Candidates(_lastVars).Count > 0;
        // To cursor vẫn dùng được khi đã chạy hết: con trỏ ở đoạn đã chạy qua thì lùi về đó.
        if (_dbgCursor is not null) _dbgCursor.Enabled = next > 0 || _executedLine > 0;
        if (next > 0 && next <= _ed.Lines.Count)
        {
            var line = _ed.Lines[next - 1];
            line.MarkerAdd(MarkNextArrow);
            line.MarkerAdd(MarkNextLine);
            var first = _ed.FirstVisibleLine;
            if (next - 1 < first || next - 1 >= first + _ed.LinesOnScreen)
                _ed.FirstVisibleLine = Math.Max(0, next - 1 - _ed.LinesOnScreen / 2);   // như revealLineInCenterIfOutsideViewport
        }
    }

    private void StopStepDebug()
    {
        var wasActive = _plan is not null || _debugStepOn;
        _plan = null;
        _executedLine = _lastOkLine = _errorFrom = _errorLine = 0;
        _lastVars = null;
        _debugStepOn = false;
        PushDebugState();
        if (wasActive && (_status.Text.StartsWith("Debug từng bước", StringComparison.Ordinal) || _status.Text.StartsWith("Đã chạy", StringComparison.Ordinal)))
            SetStatus("Đã dừng debug từng bước.", AppColors.TextMuted);
    }

    /// <summary>"step" = chạy dòng kế; "continue" = tới ngay trước breakpoint kế tiếp (hoặc hết); "to-cursor" = tới ngay trước dòng con trỏ (số dòng từ 1;
    /// con trỏ ở đoạn đã chạy qua thì lùi về đó); "back" = lùi về điểm dừng trước. Chưa có phiên debug thì (với "continue") bắt đầu phiên mới.</summary>
    private async Task DebugCommandAsync(string cmd, int cursorLine = 0)
    {
        if (_plan is null)
        {
            if (cmd == "continue") await StartStepDebugAsync();
            return;
        }
        if (_stepRunning) return;
        if (RefreshPlanIfEdited() && cmd == "back") return;   // code mới: về đầu; lùi thì dừng ở đó

        // Lùi: mỗi bước luôn chạy lại từ đầu rồi rollback, nên lùi = chạy lại tới điểm dừng trước đó (về trước điểm đầu = chưa chạy gì).
        if (cmd == "back" || (cmd == "to-cursor" && cursorLine > 0 && cursorLine <= _executedLine))
        {
            if (_executedLine == 0) return;
            var limit = cmd == "back" ? _executedLine : cursorLine;
            var prev = _plan.SafeLines.Where(l => l < limit).DefaultIfEmpty(0).Max();
            if (prev > 0) { await ExecuteStepAsync(prev); return; }
            _executedLine = _lastOkLine = _errorFrom = _errorLine = 0;
            _lastVars = null;
            _results.Clear();
            _results.ClearMessages();
            SetStatus($"Đã lùi về đầu — sắp chạy dòng {_plan.NextSafe(0)}.", AppColors.Success);
            PushDebugState();
            return;
        }

        var next = _plan.NextSafe(_executedLine);
        if (next == 0)
        {
            SetStatus("Đã chạy hết script. Bấm Shift+F5 để thoát debug.", AppColors.Success);
            return;
        }

        var plan = _plan;
        int LastSafeBefore(int line) => plan.SafeLines.Where(l => l < line).DefaultIfEmpty(next).Max();
        var target = next;
        if (cmd == "continue")
        {
            var stops = BreakpointLines().Where(b => b > next).OrderBy(b => b).ToList();
            target = stops.Count > 0 ? Math.Max(next, LastSafeBefore(stops[0])) : plan.SafeLines[^1];
        }
        else if (cmd == "to-cursor" && cursorLine > next)
        {
            target = Math.Max(next, LastSafeBefore(cursorLine));
        }
        await ExecuteStepAsync(target);
    }

    /// <summary>
    /// "🔍 Tìm lỗi": tìm bước đầu tiên bị lỗi rồi dừng ở đó (tô đỏ đoạn lỗi, hiện biến). Mỗi bước chạy lại từ đầu nên đã lỗi ở bước k thì
    /// mọi bước sau cũng lỗi → chia đôi các điểm dừng (~log2(n) lần chạy thay vì n). Chạy tới cuối không lỗi thì dừng ở cuối.
    /// </summary>
    private async Task DebugFindErrorAsync()
    {
        if (_plan is null || _stepRunning) return;
        RefreshPlanIfEdited();
        var plan = _plan;
        var lines = plan.SafeLines;
        var runs = 0;
        async Task<bool> Fails(int line)
        {
            runs++;
            SetStatus($"Đang tìm lỗi: lần chạy {runs} — tới dòng {line}... (đã rollback, không lưu dữ liệu)", AppColors.Warning);
            await using var conn = _service.CreateConnection(_useSys);
            await conn.OpenAsync();
            try { return (await _service.ExecuteScriptOnAsync(plan.BuildBatch(line).Sql, conn)).Any(r => r.Error is not null); }
            finally
            {
                try { await using var rb = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRAN;", conn); await rb.ExecuteNonQueryAsync(); }
                catch { /* connection hỏng — server tự rollback */ }
            }
        }

        _stepRunning = true;
        int bad;
        try
        {
            if (!await Fails(lines[^1])) bad = -1;
            else
            {
                int lo = 0, hi = lines.Count - 1;   // hi luôn là chỉ số lỗi
                while (lo < hi)
                {
                    var mid = (lo + hi) / 2;
                    if (await Fails(lines[mid])) hi = mid; else lo = mid + 1;
                    if (_plan != plan) return;   // đã Stop / bắt đầu phiên khác trong lúc tìm
                }
                bad = hi;
            }
        }
        catch (Exception ex)
        {
            SetStatus("Không tìm được lỗi: " + ex.Message, AppColors.Danger);
            return;
        }
        finally { _stepRunning = false; }

        if (_plan != plan) return;
        if (bad < 0)
        {
            await ExecuteStepAsync(lines[^1]);
            SetStatus($"Chạy hết tới dòng {lines[^1]} không có lỗi ({runs} lần chạy, đã rollback).", AppColors.Success);
            return;
        }
        _lastOkLine = bad > 0 ? lines[bad - 1] : 0;
        await ExecuteStepAsync(lines[bad]);   // hiện kết quả + tô đỏ đoạn lỗi (_lastOkLine, dòng bad]
        SetStatus($"Tìm thấy lỗi sau {runs + 1} lần chạy: " + _status.Text, AppColors.Danger);
    }

    /// <summary>
    /// Đang debug mà code trong editor đã sửa (vd sửa chỗ lỗi vừa tìm được): dựng lại kế hoạch từ code mới (giữ giá trị tham số đã nhập)
    /// và chạy lại từ đầu — không phải tắt debug rồi bật lại. Trả true nếu đã dựng lại.
    /// </summary>
    private bool RefreshPlanIfEdited()
    {
        if (_plan is null) return false;
        var text = _ed.Text;
        if (string.IsNullOrWhiteSpace(text) || text == _planText) return false;
        var plan = SqlStepPlanner.Build(text, _planValues);
        if (plan.SafeLines.Count == 0) return false;   // code mới không dừng được ở đâu — giữ kế hoạch cũ
        _plan = plan;
        _planText = text;
        _executedLine = _lastOkLine = _errorFrom = _errorLine = 0;
        _lastVars = null;
        _results.Clear();
        _results.ClearMessages();
        SetStatus($"Script đã sửa — dựng lại debug từ code mới ({plan.SafeLines.Count} điểm dừng), chạy lại từ đầu.", AppColors.Success);
        PushDebugState();
        return true;
    }

    /// <summary>
    /// "🧩 SQL động": biến chuỗi đang chứa câu SQL ở điểm dừng (vd @q trước EXEC FastBusiness$Partition$Execute) → mở tab mới với câu đó đã
    /// thay placeholder Fast, xuống dòng theo từ khoá và kiểm tra cú pháp (SET PARSEONLY), dòng lỗi có đánh dấu. Nhiều biến thì cho chọn.
    /// </summary>
    private async Task ShowDynamicSqlAsync()
    {
        var cands = DynamicSqlInspector.Candidates(_lastVars);
        if (cands.Count == 0)
        {
            SetStatus("Chưa có biến nào chứa câu SQL ở điểm dừng này — chạy (F10) tới sau chỗ ghép @q rồi bấm lại.", AppColors.Danger);
            return;
        }
        await Task.Yield();   // thoát khỏi callback trước khi mở tab mới
        if (cands.Count == 1) { await OpenDynamicSqlAsync(cands[0]); return; }
        var menu = new ContextMenuStrip { Renderer = Bcode.App.UI.FlatToolStripRenderer.Shared };
        foreach (var c in cands) menu.Items.Add($"{c.Name}  ({c.Value.Length:N0} ký tự)", null, (_, _) => _ = OpenDynamicSqlAsync(c));
        menu.Closed += (_, _) => BeginInvoke(new Action(menu.Dispose));
        menu.Show(Cursor.Position);
    }

    private async Task OpenDynamicSqlAsync((string Name, string Value) v)
    {
        var prepared = DynamicSqlInspector.Prepare(v.Value);
        (int Line, string Message)? err = null;
        var checkedOk = true;
        try
        {
            await using var conn = _service.CreateConnection(_useSys);
            err = await DynamicSqlInspector.ParseAsync(conn, prepared);
        }
        catch (Exception ex) { checkedOk = false; SetStatus("Không kiểm tra được cú pháp SQL động: " + ex.Message, AppColors.Danger); }
        var stop = _executedLine;
        OpenObjectInNewTabRequested?.Invoke($"SQL động {v.Name} (dòng {stop})", _useSys, DynamicSqlInspector.Annotate(v.Name, stop, prepared, err));
        if (!checkedOk) return;
        if (err is { } e) { SetStatus($"SQL động {v.Name}: lỗi cú pháp — {e.Message} (đã mở tab, dòng lỗi có đánh dấu ✖).", AppColors.Danger); }
        else { SetStatus($"SQL động {v.Name}: cú pháp hợp lệ (đã mở tab). Lỗi còn lại nếu có là tên bảng / cột hoặc dữ liệu.", AppColors.Success); }
    }

    private async Task ExecuteStepAsync(int target)
    {
        if (_plan is null) return;
        _stepRunning = true;
        SetStatus($"Đang chạy tới dòng {target}...", AppColors.Warning);
        try
        {
            var (sql, watchNames) = _plan.BuildBatch(target);
            List<RawSqlService.BatchResult> results;
            await using (var conn = _service.CreateConnection(_useSys))
            {
                await conn.OpenAsync();
                try { results = await _service.ExecuteScriptOnAsync(sql, conn); }
                finally
                {
                    // Mỗi bước nằm trong 1 transaction không bao giờ commit: luôn ROLLBACK (đóng connection cũng tự rollback, đây là chắc ăn).
                    try { await using var rb = new SqlCommand("IF @@TRANCOUNT > 0 ROLLBACK TRAN;", conn); await rb.ExecuteNonQueryAsync(); }
                    catch { /* connection đã hỏng — server tự rollback khi mất connection */ }
                }
            }

            _executedLine = target;
            var errorBatch = results.FirstOrDefault(r => r.Error is not null);
            var tables = results.SelectMany(r => r.Tables).ToList();
            var messages = string.Join(Environment.NewLine, results.Select(r => r.Messages).Where(m => !string.IsNullOrEmpty(m)));

            // Đoạn watch chỉ chạy khi script chưa kết thúc sớm (RETURN/lỗi) — có dấu hiệu PRINT thì mới coi N bảng cuối là biến.
            var reachedWatch = messages.Contains(StepPlan.WatchSentinel);
            messages = string.Join(Environment.NewLine, messages.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Where(l => !l.Contains(StepPlan.WatchSentinel)));
            if (reachedWatch && tables.Count >= watchNames.Count)
            {
                for (var i = 0; i < watchNames.Count; i++) tables[tables.Count - watchNames.Count + i].TableName = watchNames[i];
                var real = tables.Count - watchNames.Count;
                for (var i = 0; i < real; i++) tables[i].TableName = $"Kết quả {i + 1}";
            }
            if (_split.Panel2Collapsed) _split.Panel2Collapsed = false;
            _lastVars = reachedWatch ? tables.FirstOrDefault(t => t.TableName == "Variables") : null;
            if (tables.Count > 0) _results.SetTables(tables); else _results.Clear();

            var remaining = _plan.SafeLines.Count(l => l > _executedLine);
            if (errorBatch is not null)
            {
                // Bước trước (tới dòng _lastOkLine) chạy được → lỗi nằm trong đoạn vừa thêm: tô đỏ đoạn đó thay vì chỉ dời mũi tên sang dòng kế.
                _errorLine = target;
                _errorFrom = _plan.SafeLines.Where(l => l > _lastOkLine && l <= target).DefaultIfEmpty(target).Min();
                var where = _errorFrom == target ? $"dòng {target}" : $"đoạn dòng {_errorFrom}–{target}";
                SetStatus($"Lỗi ở {where} (tô đỏ; bước trước{(_lastOkLine > 0 ? $" tới dòng {_lastOkLine}" : "")} chạy được) — xem tab Message. ⤴ Back để xem lại, Shift+F5 dừng.", AppColors.Danger);
                _results.SetMessages($"LỖI: {errorBatch.Error}", messages, tables.Count);
            }
            else
            {
                _lastOkLine = target;
                _errorFrom = _errorLine = 0;
                SetStatus(remaining > 0
                    ? $"Đã chạy tới dòng {target} — còn {remaining} điểm dừng. (đã rollback, không lưu dữ liệu)"
                    : $"Đã chạy hết tới dòng {target}. (đã rollback, không lưu dữ liệu)", AppColors.Success);
                _results.SetMessages(null, messages, tables.Count);
            }
        }
        catch (Exception ex)
        {
            _executedLine = target;
            _errorLine = target;
            _errorFrom = _plan?.SafeLines.Where(l => l > _lastOkLine && l <= target).DefaultIfEmpty(target).Min() ?? target;
            SetStatus("Lỗi khi debug từng bước.", AppColors.Danger);
            _results.SetMessages($"LỖI: {ex.Message}", null, 0);
        }
        finally
        {
            _stepRunning = false;
            PushDebugState();
        }
    }

    /// <summary>Tab native không cần "ngủ đông" (không tốn WebView2) nên không chụp trạng thái.</summary>
    public Task<SqlTabSnapshot?> TryCaptureSnapshotAsync() => Task.FromResult<SqlTabSnapshot?>(null);
    public void RestoreSnapshot(SqlTabSnapshot snapshot) { }

    private void GotoLine(int line)
    {
        var l = Math.Clamp(line - 1, 0, Math.Max(0, _ed.Lines.Count - 1));
        _ed.Lines[l].Goto();
        _ed.SetSelection(_ed.Lines[l].Position, _ed.Lines[l].EndPosition);
        FocusEditor();
    }

    // =====================================================================  phím tắt

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_hints is not null && _hints.HandleKey(keyData)) return true;   // gợi ý đang mở / đang điền ô mẫu / Ctrl+Space
        // Chuỗi phím như VS Code: Ctrl+K rồi Ctrl+C = comment, Ctrl+K rồi Ctrl+U = uncomment (chờ phím thứ hai tối đa 2,5 giây).
        if (_ed.Focused)
        {
            var pending = _chordK > 0 && Environment.TickCount64 - _chordK < 2500;
            _chordK = 0;
            if (pending && keyData == (Keys.Control | Keys.C)) { ToggleComment(true); return true; }
            if (pending && keyData == (Keys.Control | Keys.U)) { ToggleComment(false); return true; }
            if (keyData == (Keys.Control | Keys.K)) { _chordK = Environment.TickCount64; return true; }
        }
        if (_ghostText is not null && _ed.Focused)
        {
            if (keyData == Keys.Tab && AcceptGhost()) return true;   // Tab = nhận gợi ý AI (ghost-text)
            if (keyData == Keys.Escape) { ClearGhost(); return true; }
        }
        var combo = ShortcutRegistry.FromKeys(keyData);
        if (combo is not null)
        {
            if (combo == ShortcutRegistry.Get("editor.run") || combo == ShortcutRegistry.Get("editor.run2")) { if (!_running) _ = RunAsync(); return true; }
            if (combo == ShortcutRegistry.Get("editor.cancel")) { CancelRun(); return true; }
            if (combo == ShortcutRegistry.Get("editor.beauty")) { BeautyFormat(); return true; }
            if (combo == ShortcutRegistry.Get("editor.wrap")) { ToggleWrap(); return true; }
            if (combo == ShortcutRegistry.Get("editor.saveHistory")) { SaveToHistory(); return true; }
            if (combo == ShortcutRegistry.Get("editor.peek")) { _ = PeekAsync(); return true; }   // F12: xem nhanh (đang ở trong khung peek thì xem tiếp từ chữ dưới con trỏ của khung)
            if (combo == ShortcutRegistry.Get("editor.openObject"))
            {
                if (_peek is { Visible: true } && _peek.Current is { } cur) { OpenPeekItemInTab(cur); return true; }
                _ = OpenObjectUnderCaretAsync();
                return true;
            }
            if (combo == ShortcutRegistry.Get("editor.open")) { OpenFile(); return true; }
            if (combo == ShortcutRegistry.Get("editor.save")) { SaveFile(); return true; }
            if (combo == ShortcutRegistry.Get("editor.find")) { ShowFind(); return true; }
            if (combo == ShortcutRegistry.Get("editor.toggleComment")) { ToggleCommentAuto(); return true; }
            if (combo == ShortcutRegistry.Get("editor.blockComment")) { ToggleBlockComment(); return true; }
            if (combo == ShortcutRegistry.Get("editor.comment")) { ToggleComment(true); return true; }
            if (combo == ShortcutRegistry.Get("editor.uncomment")) { ToggleComment(false); return true; }
            if (combo == ShortcutRegistry.Get("editor.writeSchema")) { WriteSchema(); return true; }
            if (combo == ShortcutRegistry.Get("editor.ai")) { ShowAiEdit(); return true; }
            if (combo == ShortcutRegistry.Get("editor.suggest")) { _ = SuggestAsync(explicitCall: true); return true; }
            if (combo == ShortcutRegistry.Get("editor.fastLong")) { ToggleFastGhostLong(); return true; }
            if (combo == ShortcutRegistry.Get("editor.checkFields")) { _ = CheckFieldsAsync(); return true; }
            if (combo == ShortcutRegistry.Get("editor.dbApp")) { _db.Select(0, raise: true); return true; }
            if (combo == ShortcutRegistry.Get("editor.dbSys")) { _db.Select(1, raise: true); return true; }
            if (combo == ShortcutRegistry.Get("editor.toggleSuggest")) { ToggleSuggest(); return true; }
            if (combo == ShortcutRegistry.Get("editor.toggleResetConn")) { ToggleResetConn(); return true; }
            if (combo == ShortcutRegistry.Get("editor.toggleResultTab")) { ToggleResultTab(); return true; }
            if (combo == ShortcutRegistry.Get("editor.options") && _barCtl.TryGetValue("options", out var optBtn)) { BuildOptionsMenu().Show(optBtn, 0, optBtn.Height + 2); return true; }
            for (var g = 1; g <= 9; g++)
                if (combo == ShortcutRegistry.Get("editor.barGroup" + g)) { OpenBarGroup(g); return true; }
            if (combo == ShortcutRegistry.Get("editor.debugTarget")) { _ = PickDebugTargetAsync(); return true; }
            if (combo == ShortcutRegistry.Get("editor.debugStep")) { ToggleDebugStep(); return true; }
            if (combo == ShortcutRegistry.Get("debug.next")) { _ = DebugCommandAsync("step"); return true; }
            if (combo == ShortcutRegistry.Get("debug.cursor")) { _ = DebugCommandAsync("to-cursor", _ed.CurrentLine + 1); return true; }
            if (combo == ShortcutRegistry.Get("debug.stop")) { StopStepDebug(); return true; }
            if (combo == ShortcutRegistry.Get("debug.back")) { _ = DebugCommandAsync("back"); return true; }
            if (combo == ShortcutRegistry.Get("debug.findError")) { _ = DebugFindErrorAsync(); return true; }
            if (combo == ShortcutRegistry.Get("debug.dynSql")) { _ = ShowDynamicSqlAsync(); return true; }
        }
        if (keyData == (Keys.Control | Keys.R)) { _split.Panel2Collapsed = !_split.Panel2Collapsed; return true; }
        if (keyData == (Keys.Control | Keys.D0) && _ed.Focused) { ChangeZoom(0); return true; }
        if (keyData == Keys.Escape && _peek is { Visible: true }) { PeekBack(); return true; }
        if (keyData == Keys.Escape && _find is { Visible: true } && _ed.Focused) { HideFind(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // =====================================================================  tìm trong editor (thanh tìm nổi như Monaco)

    private NativeFindBar? _find;

    private void ShowFind()
    {
        if (_find is null)
        {
            // Thanh tìm NỔI ở góc trên-phải editor (như find widget của Monaco / ScriptEditorControl), không dock chiếm cả bề ngang.
            _find = new NativeFindBar { Visible = false, UnderColor = () => _ed.Styles[Style.Default].BackColor };   // góc bo trùng nền editor bên dưới
            _find.Find += FindInEditor;
            _find.CloseRequested += HideFind;
            _split.Panel1.Controls.Add(_find);
            _split.Panel1.Resize += (_, _) => PositionFind();
        }
        // Chữ đang bôi đen trên 1 dòng → điền sẵn (như Monaco).
        var sel = _ed.SelectedText;
        if (sel.Length > 0 && sel.IndexOfAny(new[] { '\r', '\n' }) < 0) _find.SetText(sel);
        PositionFind();
        _find.Visible = true;
        _find.BringToFront();
        _find.FocusInput();
    }

    /// <summary>Góc trên-phải khung editor, chừa chỗ thanh cuộn dọc; rộng tối đa 420px thiết kế (co lại khi khung hẹp).</summary>
    private void PositionFind()
    {
        if (_find is null) return;
        var host = _split.Panel1.ClientSize;
        var width = Math.Min(NativeSkin.S(420), Math.Max(NativeSkin.S(240), host.Width - NativeSkin.S(80)));
        _find.Width = width;
        _find.Location = new Point(Math.Max(0, host.Width - width - SystemInformation.VerticalScrollBarWidth - NativeSkin.S(10)), NativeSkin.S(6));
    }

    private void HideFind()
    {
        if (_find is null) return;
        _find.Visible = false;
        FocusEditor();
    }

    /// <summary>Tìm không phân biệt hoa thường; hết văn bản thì quay vòng; báo "vị trí/tổng" trên thanh tìm.</summary>
    private void FindInEditor(string text, bool forward, bool typing)
    {
        if (_find is null) return;
        if (text.Length == 0) { _find.SetInfo("", false); return; }
        _ed.SearchFlags = SearchFlags.None;
        // Đang gõ: tìm lại từ đầu vùng chọn (chữ dài thêm vẫn khớp chỗ cũ); Enter / ↓: sau vùng chọn; ↑: trước vùng chọn.
        var from = forward ? (typing ? _ed.SelectionStart : _ed.SelectionEnd) : _ed.SelectionStart;
        var pos = SearchRange(text, from, forward ? _ed.TextLength : 0);
        if (pos < 0) pos = SearchRange(text, forward ? 0 : _ed.TextLength, from);   // quay vòng
        if (pos < 0) { _find.SetInfo("Không có kết quả", true); return; }
        _ed.SetSelection(_ed.TargetEnd, _ed.TargetStart);
        _ed.ScrollRange(_ed.TargetStart, _ed.TargetEnd);
        _find.SetInfo(MatchInfo(text, pos), false);
    }

    private int SearchRange(string text, int start, int end)
    {
        _ed.TargetStart = start;
        _ed.TargetEnd = end;   // start > end = tìm lùi
        return _ed.SearchInTarget(text);
    }

    /// <summary>"3/17": thứ tự của kết quả đang chọn trên tổng số kết quả (đếm tối đa 9999).</summary>
    private string MatchInfo(string text, int current)
    {
        int count = 0, index = 0, at = 0;
        while (count < 9999)
        {
            var p = SearchRange(text, at, _ed.TextLength);
            if (p < 0) break;
            count++;
            if (p == current) index = count;
            at = Math.Max(_ed.TargetEnd, p + 1);
        }
        return count >= 9999 ? $"{index}/9999+" : $"{index}/{count}";
    }

    // =====================================================================  chạy script

    private void CancelRun()
    {
        if (!_running || _runCts is null || _runCts.IsCancellationRequested) return;
        SetStatus("Đang huỷ lệnh…", AppColors.Warning);
        try { _runCts.Cancel(); } catch { /* đã xong */ }
    }

    private void SetRunningUi(bool running)
    {
        _btnRun.Text = running ? "■ Cancel" : "▶ Execute";
        _btnRun.Invalidate();
    }

    private async Task RunAsync()
    {
        ClearGhost();
        // Bật "Debug từng bước": Execute không chạy cả script mà bắt đầu phiên debug (hoặc chạy tiếp tới breakpoint/hết khi đang debug).
        if (_debugStepOn || _plan is not null) { await DebugCommandAsync("continue"); return; }
        if (_running) { SetStatus("Lệnh trước còn đang chạy — chờ chạy xong rồi bấm lại.", AppColors.Warning); return; }
        _running = true;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var token = _runCts.Token;
        SetRunningUi(true);
        if (_split.Panel2Collapsed) _split.Panel2Collapsed = false;
        SetStatus("Đang chạy...", AppColors.Warning);
        string? ranScript = null; var ranSys = false;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var tick = new System.Windows.Forms.Timer { Interval = 1000 };
        tick.Tick += (_, _) => { if (_running) SetStatus(token.IsCancellationRequested ? $"Đang huỷ lệnh… {watch.Elapsed.TotalSeconds:0}s" : $"Đang chạy… {watch.Elapsed.TotalSeconds:0}s — bấm ■ Cancel để dừng", AppColors.Warning); };
        tick.Start();
        try
        {
            var script = _ed.SelectedText;
            if (string.IsNullOrWhiteSpace(script)) script = _ed.Text;
            if (string.IsNullOrWhiteSpace(script)) { SetStatus("", null); return; }
            var useSys = _useSys;
            ranScript = script; ranSys = useSys;
            var tracked = await Task.Run(() => SqlHistoryService.Detect(script));
            if (tracked.Count > 0) await CaptureHistoryBaselineAsync(tracked, useSys);
            var results = _resetConnOn
                ? await _service.ExecuteScriptAsync(script, useSys, token)
                : await RunWithPersistentConnectionAsync(script, useSys, token);

            var errorBatch = results.FirstOrDefault(r => r.Error is not null);
            ScriptExecuted?.Invoke(script, useSys, errorBatch is null, (int)watch.ElapsedMilliseconds, results.SelectMany(r => r.Tables).Sum(t => t.Rows.Count));
            if (errorBatch is null && tracked.Count > 0) RecordHistory(tracked, useSys);
            var allTables = results.SelectMany(r => r.Tables).ToList();
            _lastResultSchema = allTables.Select(t => t.Clone()).ToList();
            if (allTables.Count > 0)
            {
                if (_resultTabOn) OpenResultInNewTabRequested?.Invoke(allTables, "Command Result");
                else _results.SetTables(allTables);
                if (allTables.LastOrDefault() is { } last) ResultReady?.Invoke(last);
            }
            else if (!_resultTabOn) _results.Clear();

            var totalAffected = results.Sum(r => r.RowsAffected);
            var summary = $"{results.Count} batch đã chạy" +
                          (allTables.Count > 0 ? (allTables.Count == 1 ? $" · {allTables[0].Rows.Count} dòng kết quả" : $" · {allTables.Count} bảng kết quả ({allTables.Sum(t => t.Rows.Count)} dòng)") : "") +
                          (totalAffected > 0 ? $" · {totalAffected} dòng bị ảnh hưởng (INSERT/UPDATE/DELETE)" : "") +
                          (_resetConnOn ? "" : " · [Reset Connection tắt: giữ nguyên connection/#temp table]") + RunTimeText(results);
            var printed = string.Join(Environment.NewLine, results.Select(r => r.Messages).Where(m => !string.IsNullOrEmpty(m)));

            if (errorBatch is not null && errorBatch.Error == RawSqlService.CancelledMessage)
            {
                SetStatus("Đã huỷ lệnh (Cancel)" + (allTables.Count > 0 ? $" · giữ {allTables.Count} bảng kết quả đã đọc xong" : "") + RunTimeText(results), AppColors.Warning);
                _results.SetMessages(null, (printed.Length > 0 ? printed + Environment.NewLine : "") + RawSqlService.CancelledMessage, allTables.Count);
            }
            else if (errorBatch is not null)
            {
                SetStatus("Có lỗi xảy ra (xem chi tiết ở tab Message)." + RunTimeText(results), AppColors.Danger);
                _results.SetMessages($"LỖI: {errorBatch.Error}", printed, allTables.Count);
            }
            else
            {
                SetStatus(summary, AppColors.Success);
                _results.SetMessages(null, printed, allTables.Count);
            }
            _lastScript = script;
            UpdatePivot(allTables, script);
        }
        catch (SqlException ex)
        {
            if (ranScript is not null) ScriptExecuted?.Invoke(ranScript, ranSys, false, (int)watch.ElapsedMilliseconds, 0);
            SetStatus("Lỗi SQL.", AppColors.Danger);
            _results.SetMessages("LỖI SQL TRỰC TIẾP:" + Environment.NewLine + string.Join(Environment.NewLine, ex.Errors.Cast<SqlError>().Select(e => $"Lỗi ở dòng {e.LineNumber}: {e.Message}")), null, 0);
        }
        catch (Exception ex)
        {
            if (ranScript is not null) ScriptExecuted?.Invoke(ranScript, ranSys, false, (int)watch.ElapsedMilliseconds, 0);
            SetStatus("Lỗi hệ thống.", AppColors.Danger);
            _results.SetMessages("LỖI HỆ THỐNG:" + Environment.NewLine + ex.Message, null, 0);
        }
        finally
        {
            tick.Stop(); tick.Dispose();
            _running = false;
            SetRunningUi(false);
        }
    }

    private static string RunTimeText(List<RawSqlService.BatchResult> results)
    {
        if (results.Count == 0) return "";
        var text = " · ⏱ " + FormatDuration(results.Sum(r => r.ElapsedMs));
        if (results.Count > 1)
        {
            var slow = results.Select((r, i) => (r.ElapsedMs, No: i + 1)).MaxBy(x => x.ElapsedMs);
            text += $" (chậm nhất: batch {slow.No} — {FormatDuration(slow.ElapsedMs)})";
        }
        return text;
    }

    private static string FormatDuration(long ms) =>
        ms < 1000 ? $"{ms} ms"
        : ms < 60_000 ? (ms / 1000.0).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("vi-VN")) + " s"
        : $"{ms / 60_000} phút {(ms % 60_000 + 500) / 1000} s";

    private async Task<List<RawSqlService.BatchResult>> RunWithPersistentConnectionAsync(string script, bool useSys, CancellationToken ct)
    {
        var stamp = _service.CurrentStamp(useSys);
        if (_persistentConn is null || _persistentConnUsesSys != useSys || _persistentConn.State != ConnectionState.Open || _persistentConnStamp != stamp)
        {
            DisposePersistentConnection();
            _persistentConn = _service.CreateConnection(useSys);
            await _persistentConn.OpenAsync();
            _persistentConnUsesSys = useSys;
            _persistentConnStamp = stamp;
        }
        return await _service.ExecuteScriptOnAsync(script, _persistentConn, ct);
    }

    private void DisposePersistentConnection() { _persistentConn?.Dispose(); _persistentConn = null; }

    /// <summary>Lần đầu gặp một object: lưu bản đang có trong database TRƯỚC khi script ghi đè nó (để luôn có "bản gốc" so sánh).</summary>
    private async Task CaptureHistoryBaselineAsync(List<SqlTrackedObject> tracked, bool useSys)
    {
        try
        {
            if (_service.Connections.Current is not { } ws) return;
            foreach (var obj in tracked)
            {
                if (await Task.Run(() => SqlHistoryService.HasAny(ws, useSys, obj))) continue;
                var def = await _service.GetObjectDefinitionAsync(obj.Qualified, useSys);
                if (!string.IsNullOrWhiteSpace(def))
                {
                    var normalized = SqlHistoryService.NormalizeHeader(def);
                    await Task.Run(() => SqlHistoryService.Record(ws, useSys, obj, normalized, "BASELINE"));
                }
            }
        }
        catch { /* object chưa tồn tại / không có quyền / mất kết nối — không ảnh hưởng việc chạy script */ }
    }

    private void RecordHistory(List<SqlTrackedObject> tracked, bool useSys)
    {
        if (_service.Connections.Current is not { } ws) return;
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var obj in tracked)
                {
                    var isCreate = Regex.IsMatch(obj.Batch, @"^\s*(--[^\n]*\n\s*|/\*.*?\*/\s*)*CREATE\b", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                                   && !Regex.IsMatch(obj.Batch, @"CREATE\s+OR\s+ALTER", RegexOptions.IgnoreCase);
                    SqlHistoryService.Record(ws, useSys, obj, obj.Batch, isCreate ? "CREATE" : "ALTER");
                }
            }
            catch { }
        });
    }

    // =====================================================================  menu / thao tác

    private WebMenu BuildScriptMenu() => new WebMenu()
        .Add("Bật / tắt chú thích dòng", ToggleCommentAuto, shortcut: ShortcutRegistry.Display("editor.toggleComment"))
        .Add("Comment (chú thích dòng đang chọn)", () => ToggleComment(true), shortcut: ShortcutRegistry.Display("editor.comment") is { Length: > 0 } c1 ? c1 : "Ctrl+K Ctrl+C")
        .Add("Uncomment (bỏ chú thích)", () => ToggleComment(false), shortcut: ShortcutRegistry.Display("editor.uncomment") is { Length: > 0 } c2 ? c2 : "Ctrl+K Ctrl+U")
        .Add("Chú thích khối /* ... */", ToggleBlockComment, shortcut: ShortcutRegistry.Display("editor.blockComment"))
        .AddSeparator()
        .Add("Làm đẹp SQL (Beauty)", BeautyFormat, shortcut: ShortcutRegistry.Display("editor.beauty"))
        .Add("UPPER Keyword", () => ApplyDefaultType(true))
        .Add("lower Keyword", () => ApplyDefaultType(false))
        .AddSeparator()
        .Add("Write Schema (.xsd cho Crystal Report từ bảng kết quả)...", WriteSchema, shortcut: ShortcutRegistry.Display("editor.writeSchema"))
        .AddSeparator()
        .Add("Mở object dưới con trỏ ở tab mới", () => _ = OpenObjectUnderCaretAsync(), shortcut: ShortcutRegistry.Display("editor.openObject"));

    /// <summary>Tạo file .xsd cho Crystal Report từ các bảng kết quả lần chạy gần nhất (d1, d2, d3…) — như nút Write Schema chế độ Web (Services/Rpt/XsdSchemaWriter).</summary>
    private void WriteSchema()
    {
        var status = Bcode.App.Services.Rpt.XsdSchemaWriter.SaveWithDialog(this, _lastResultSchema, _ed.Text, _service.Connections.Current?.SourcePath);
        if (status is not null) SetStatus(status, AppColors.Success);
    }

    private WebMenu BuildHistoryMenu() => new WebMenu()
        .Add("Lưu script vào Lịch sử SQL", SaveToHistory, shortcut: ShortcutRegistry.Display("editor.saveHistory"))
        .Add("Lịch sử sửa procedure/function...", () => BeginInvoke(new Action(OpenSqlHistory)));

    private WebMenu BuildFileMenu() => new WebMenu()
        .Add("Open (mở file .sql)...", OpenFile)
        .Add("Save (lưu file .sql)...", SaveFile);

    private WebMenu BuildOptionsMenu()
    {
        _settings.SyncEditorFlagsFromDisk();   // dấu tick theo trạng thái mới nhất
        return new WebMenu()
        .Add("Word Wrap", ToggleWrap, shortcut: ShortcutRegistry.Display("editor.wrap"), @checked: _wordWrap)
        .Add("Gợi ý code khi gõ (mẫu, hàm + tham số, cột, từ khoá, bảng) — Ctrl+Space gọi ngay", ToggleSuggest, @checked: _suggestOn)
        .Add("Reset Connection mỗi lần chạy", ToggleResetConn, @checked: _resetConnOn)
        .Add("Result Tab (kết quả ra tab mới)", ToggleResultTab, @checked: _resultTabOn)
        .Add("Ẩn / hiện khung kết quả", () => _split.Panel2Collapsed = !_split.Panel2Collapsed, shortcut: "Ctrl+R")
        .AddCaption("Debug từng bước")
        .Add("Debug từng bước (Execute = chạy tới breakpoint; bấm lề trái để đặt chấm đỏ)", ToggleDebugStep, shortcut: ShortcutRegistry.Display("editor.debugStep"), @checked: _debugStepOn || _plan is not null)
        .Add("Debug store/function… (chọn câu EXEC / gọi function trong script)", () => _ = PickDebugTargetAsync(), shortcut: ShortcutRegistry.Display("editor.debugTarget"))
        .AddCaption("AI (Copilot)")
        .Add("AI: sửa / sinh SQL theo yêu cầu…", ShowAiEdit, shortcut: ShortcutRegistry.Display("editor.ai"))
        .Add("Gọi gợi ý AI ngay", () => _ = SuggestAsync(explicitCall: true), shortcut: ShortcutRegistry.Display("editor.suggest"))
        .Add("Gợi ý theo cấu trúc Fast khi gõ (không AI)", () => { _settings.EnableFastGhost = AppSettings.Update(s => s.EnableFastGhost = !s.EnableFastGhost).EnableFastGhost; if (!_settings.EnableFastGhost) ClearGhost(); }, @checked: _settings.EnableFastGhost)
        .Add("Gợi ý Fast dài (tắt = ngắn, chỉ gợi ý chắc ≥ 90%)", ToggleFastGhostLong, @checked: _settings.FastGhostLong, shortcut: ShortcutRegistry.Display("editor.fastLong"))
        .Add("Học gợi ý Fast từ database này (chỉ đọc)…", () => BeginInvoke(new Action(() => _ = FastGhostLearnUi.RunAsync(this, GhostDbKey, () => _service.CreateConnection(_useSys)))))
        .Add("Gợi ý AI tự động khi gõ (chỉ trong procedure / function)", () => { _settings.EnableCopilotSuggest = AppSettings.Update(s => s.EnableCopilotSuggest = !s.EnableCopilotSuggest).EnableCopilotSuggest; if (!_settings.EnableCopilotSuggest) ClearGhost(); }, @checked: _settings.EnableCopilotSuggest)
        .Add("Cấu hình Gemini API Key…", () => ConfigureApiKey(gemini: true))
        .Add("Cấu hình Claude (Anthropic) API Key…", () => ConfigureApiKey(gemini: false))
        .Add("Lịch sử gợi ý AI…", () => { using var form = new Bcode.App.Forms.AiHistoryForm(text => { _ed.ReplaceSelection(text); FocusEditor(); return Task.CompletedTask; }); form.ShowDialog(FindForm()); })
        .AddSeparator()
        .Add("Tùy chỉnh thanh nút…", CustomizeBar)
        .AddCaption("Cỡ chữ")
        .Add("Tăng cỡ chữ", () => ChangeZoom(1))
        .Add("Giảm cỡ chữ", () => ChangeZoom(-1))
        .Add("Cỡ chữ mặc định", () => ChangeZoom(0), shortcut: "Ctrl+0")
        .AddCaption("Claude / Gemini")
        .Add("Gửi script sang Claude (web)", () => SendToAi("claude"))
        .Add("Gửi script sang Gemini (web)", () => SendToAi("gemini"))
        .Add("Claude/Gemini nhúng vào tab SQL Query (tắt = tab riêng)", () => AppSettings.AiEmbedded = !AppSettings.AiEmbedded, @checked: AppSettings.AiEmbedded);
    }

    private WebMenu? BuildEditorMenu()
    {
        if ((ModifierKeys & Keys.Control) != 0) return null;   // Ctrl + chuột phải = chèn định nghĩa object (OnCtrlRightClick), không mở menu
        var hasSel = _ed.SelectionStart != _ed.SelectionEnd;
        return new WebMenu()
            .Add("Execute", () => _ = RunAsync(), shortcut: ShortcutRegistry.Display("editor.run"))
            .AddSeparator()
            .Add("Cut", () => _ed.Cut(), shortcut: "Ctrl+X", enabled: hasSel)
            .Add("Copy", () => _ed.Copy(), shortcut: "Ctrl+C", enabled: hasSel)
            .Add("Paste", () => _ed.Paste(), shortcut: "Ctrl+V")
            .Add("Select All", () => _ed.SelectAll(), shortcut: "Ctrl+A")
            .AddSeparator()
            .Add("Comment", () => ToggleComment(true))
            .Add("Uncomment", () => ToggleComment(false))
            .Add("Beauty", BeautyFormat, shortcut: ShortcutRegistry.Display("editor.beauty"))
            .AddSeparator()
            .Add("Mở object dưới con trỏ ở tab mới", () => _ = OpenObjectUnderCaretAsync(), shortcut: ShortcutRegistry.Display("editor.openObject"));
    }

    private void ToggleWrap()
    {
        _wordWrap = !_wordWrap;
        _ed.WrapMode = _wordWrap ? WrapMode.Word : WrapMode.None;
        RefreshBarToggles();
    }

    private long _chordK;   // thời điểm bấm Ctrl+K (chờ phím thứ hai của chuỗi phím)

    /// <summary>Ctrl+/ như VS Code: mọi dòng (không trống) trong vùng chọn đều đang chú thích "--" thì bỏ chú thích, còn không thì chú thích cả vùng.</summary>
    private void ToggleCommentAuto()
    {
        var a = _ed.LineFromPosition(_ed.SelectionStart);
        var bPos = _ed.SelectionEnd;
        var b = _ed.LineFromPosition(bPos);
        if (b > a && _ed.Lines[b].Position == bPos) b--;
        var allCommented = true; var any = false;
        for (var l = a; l <= b; l++)
        {
            var t = _ed.Lines[l].Text.TrimEnd('\r', '\n');
            if (t.Trim().Length == 0) continue;
            any = true;
            if (!Regex.IsMatch(t, @"^\s*--")) { allCommented = false; break; }
        }
        ToggleComment(!(any && allCommented));
    }

    /// <summary>Shift+Alt+A như VS Code: bọc vùng chọn bằng /* ... */ (đã bọc sẵn thì gỡ); không chọn gì thì bọc nội dung dòng hiện tại.</summary>
    private void ToggleBlockComment()
    {
        int start = _ed.SelectionStart, end = _ed.SelectionEnd;
        if (start == end)
        {
            var line = _ed.Lines[_ed.CurrentLine];
            start = line.Position;
            end = line.Position + line.Length - (line.Text.EndsWith("\r\n") ? 2 : line.Text.EndsWith("\n") || line.Text.EndsWith("\r") ? 1 : 0);
            if (end <= start) return;
        }
        var text = _ed.GetTextRange(start, end - start);
        var m = Regex.Match(text, @"^(\s*)/\*\s?(.*?)\s?\*/(\s*)$", RegexOptions.Singleline);
        var replaced = m.Success ? m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value : "/* " + text + " */";
        _ed.TargetStart = start;
        _ed.TargetEnd = end;
        _ed.ReplaceTarget(replaced);
        _ed.SetSelection(start, start + Encoding.UTF8.GetByteCount(replaced));
    }

    private void ToggleComment(bool comment)
    {
        var a = _ed.LineFromPosition(_ed.SelectionStart);
        var bPos = _ed.SelectionEnd;
        var b = _ed.LineFromPosition(bPos);
        if (b > a && _ed.Lines[b].Position == bPos) b--;   // chọn tới đầu dòng kế thì không tính dòng đó
        var sb = new StringBuilder();
        for (var l = a; l <= b; l++)
        {
            var line = _ed.Lines[l].Text;
            var eol = line.EndsWith("\r\n") ? "\r\n" : line.EndsWith("\n") ? "\n" : line.EndsWith("\r") ? "\r" : "";
            var body = line[..(line.Length - eol.Length)];
            if (comment) body = "-- " + body;
            else
            {
                var m = Regex.Match(body, @"^(\s*)--\s?");
                if (m.Success) body = m.Groups[1].Value + body[m.Length..];
            }
            sb.Append(body).Append(eol);
        }
        _ed.TargetStart = _ed.Lines[a].Position;
        _ed.TargetEnd = _ed.Lines[b].EndPosition;
        _ed.ReplaceTarget(sb.ToString());
    }

    private void ApplyDefaultType(bool toUpper)
    {
        var sel = _ed.SelectedText;
        if (!string.IsNullOrEmpty(sel)) _ed.ReplaceSelection(SqlSyntaxHighlighter.TransformKeywordCase(sel, toUpper));
        else
        {
            var pos = _ed.CurrentPosition;
            _ed.Text = SqlSyntaxHighlighter.TransformKeywordCase(_ed.Text ?? "", toUpper);
            _ed.GotoPosition(Math.Min(pos, _ed.TextLength));
        }
    }

    private void BeautyFormat()
    {
        var formatter = new SqlFormatterService();
        var sel = _ed.SelectedText;
        if (!string.IsNullOrWhiteSpace(sel)) _ed.ReplaceSelection(formatter.Format(sel));
        else
        {
            var all = _ed.Text ?? "";
            if (string.IsNullOrWhiteSpace(all)) return;
            _ed.Text = formatter.Format(all);
        }
        SetStatus("Đã format lại câu lệnh SQL.", AppColors.Success);
    }

    private void SaveToHistory()
    {
        var script = _ed.Text ?? "";
        if (string.IsNullOrWhiteSpace(script)) return;
        SaveHistoryRequested?.Invoke(script, _useSys);
        SetStatus("Đã lưu script vào Lịch sử SQL.", AppColors.Success);
    }

    private void OpenSqlHistory()
    {
        try
        {
            var focus = SqlHistoryService.Detect(_ed.Text ?? "").FirstOrDefault();
            using var form = new Forms.SqlHistoryForm(_service, focus, _useSys, text => SetScriptTextAsync(text));
            form.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được lịch sử: " + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private async void OpenFile()
    {
        using var ofd = new OpenFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            SetScriptText(await File.ReadAllTextAsync(ofd.FileName));
            _currentFilePath = ofd.FileName;
            SetStatus($"Đã mở {ofd.FileName}.", null);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bcode — Open", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private async void SaveFile()
    {
        if (_currentFilePath is null)
        {
            using var sfd = new SaveFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*", FileName = "script.sql" };
            if (sfd.ShowDialog(this) != DialogResult.OK) return;
            _currentFilePath = sfd.FileName;
        }
        try
        {
            await File.WriteAllTextAsync(_currentFilePath, _ed.Text ?? "");
            SetStatus($"Đã lưu {_currentFilePath}.", AppColors.Success);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Bcode — Save", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void SendToAi(string engine)
    {
        var text = _ed.SelectedText;
        if (string.IsNullOrWhiteSpace(text)) text = _ed.Text ?? "";
        if (!string.IsNullOrWhiteSpace(text)) AskAiRequested?.Invoke(engine, text);
    }

    // =====================================================================  gợi ý + mở object

    private static readonly string[] BaseKeywords = SqlSyntaxHighlighter.KeywordList.ToArray();

    private void ReloadNames()
    {
        if (!IsHandleCreated || _service.Connections.Current is null) return;
        var sys = _useSys;
        _ = Task.Run(async () =>
        {
            try
            {
                var objs = await _sqlObjectService.ListObjectsAsync(sys, "");
                var names = objs.Select(o => o.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                var tabs = objs.Where(o => o.Kind is SqlObjectKind.Table or SqlObjectKind.View).Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!IsDisposed && sys == _useSys) BeginInvoke(new Action(() => { _objectNames = names; _tableNames = tabs; _namesLoaded = true; }));
            }
            catch { /* chưa kết nối được — gợi ý chỉ còn từ khoá */ }
        });
    }

    /// <summary>F12 / Ctrl+F12: tên object dưới con trỏ (hoặc các tên đang bôi đen) → mở nội dung ở tab SQL Query mới. Tìm ở database đang chọn trước, không thấy thì database còn lại.</summary>
    private async Task OpenObjectUnderCaretAsync()
    {
        var words = new List<string>();
        var sel = _ed.SelectedText;
        if (!string.IsNullOrWhiteSpace(sel)) words.AddRange(Regex.Matches(sel, @"[\w\$\.\[\]#@]+").Select(m => m.Value));
        else
        {
            var line = _ed.Lines[_ed.CurrentLine];
            var col = _ed.CurrentPosition - line.Position;
            var text = line.Text;
            foreach (Match m in Regex.Matches(text, @"[\w\$\.\[\]#@]+"))
                if (m.Index <= col && col <= m.Index + m.Length) { words.Add(m.Value); break; }
        }
        if (words.Count == 0) return;
        var found = 0; var missing = new List<string>();
        foreach (var word in words.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var raw = word.Replace("[", "").Replace("]", "").Trim('.');
            if (raw.Length == 0 || raw.StartsWith('#') || raw.StartsWith('@')) continue;
            var parts = raw.Split('.');
            var name = parts[^1]; var schema = parts.Length >= 2 ? parts[^2] : null;
            try
            {
                SqlObjectInfo? hit = null;
                foreach (var sys in new[] { _useSys, !_useSys })
                {
                    var all = (await _sqlObjectService.ListObjectsAsync(sys, name)).Where(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (all.Count == 0) continue;
                    hit = (schema is not null ? all.FirstOrDefault(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)) : null) ?? all[0];
                    break;
                }
                if (hit is null) { missing.Add(raw); continue; }
                var body = hit.Kind == SqlObjectKind.Table ? await _sqlObjectService.GetTableStructureAsync(hit) : await _sqlObjectService.GetDefinitionAsync(hit);
                OpenObjectInNewTabRequested?.Invoke(hit.QualifiedName, hit.FromSysDatabase, body);
                found++;
            }
            catch (Exception ex) { SetStatus("F12: " + ex.Message, AppColors.Danger); return; }
        }
        if (found == 0 && missing.Count > 0) SetStatus("F12: không thấy object " + string.Join(", ", missing.Take(5)) + " (đã tìm cả App Data và Sys Data).", AppColors.Warning);
    }

    // =====================================================================  F12: xem nhanh object (peek)

    private static List<string> WordsAt(Scintilla ed)
    {
        var sel = ed.SelectedText;
        if (!string.IsNullOrWhiteSpace(sel)) return Regex.Matches(sel, @"[\w\$\.\[\]#@]+").Select(m => m.Value).ToList();
        var line = ed.Lines[ed.CurrentLine];
        var col = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(line.Text), 0, Math.Clamp(ed.CurrentPosition - line.Position, 0, Encoding.UTF8.GetByteCount(line.Text))).Length;
        foreach (Match m in Regex.Matches(line.Text, @"[\w\$\.\[\]#@]+"))
            if (m.Index <= col && col <= m.Index + m.Length) return new List<string> { m.Value };
        return new List<string>();
    }

    /// <summary>Tra object theo tên (có thể kèm schema) ở database đang chọn rồi database kia; đọc cấu trúc (bảng) hoặc nội dung (procedure/function/view/trigger).</summary>
    private async Task<(SqlObjectInfo? Obj, string Text)> ResolveObjectAsync(string raw)
    {
        var parts = raw.Split('.');
        var name = parts[^1]; var schema = parts.Length >= 2 ? parts[^2] : null;
        SqlObjectInfo? hit = null;
        foreach (var sys in new[] { _useSys, !_useSys })
        {
            var all = (await _sqlObjectService.ListObjectsAsync(sys, name)).Where(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (all.Count == 0) continue;
            hit = (schema is not null ? all.FirstOrDefault(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)) : null) ?? all[0];
            break;
        }
        if (hit is null) return (null, "");
        var text = hit.Kind == SqlObjectKind.Table ? await _sqlObjectService.GetTableStructureAsync(hit) : await _sqlObjectService.GetDefinitionAsync(hit);
        return (hit, text);
    }

    /// <summary>F12: xem nhanh object dưới con trỏ / các tên đang bôi đen. Đang ở trong khung peek thì xem tiếp theo chữ dưới con trỏ của khung (Esc quay lại bước trước).</summary>
    private async Task PeekAsync()
    {
        var fromPeek = _peek is { Visible: true } && _peek.IsEditorFocused;
        var source = fromPeek ? _peek!.Editor : _ed;
        var words = WordsAt(source).Select(w => w.Replace("[", "").Replace("]", "").Trim('.')).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (words.Count == 0) return;
        if (words.Count == 1 && VarNameRegex.IsMatch(words[0])) { PeekVariable(source, words[0], fromPeek); return; }   // @biến → hiện chỗ khai báo
        var items = new List<SqlPeekPanel.Item>(); var missing = new List<string>();
        try
        {
            SetStatus(words.Count > 1 ? $"F12: đang tải {words.Count} object…" : "", AppColors.TextMuted);
            // Mỗi tên tra + đọc nội dung đồng thời (tối đa 6 kết nối cùng lúc), kết quả giữ đúng thứ tự bôi đen.
            using var gate = new SemaphoreSlim(6);
            async Task<(string Raw, SqlObjectInfo? Obj, string Text, bool Skip)> One(string raw)
            {
                if (raw.StartsWith('#') || raw.StartsWith('@') || raw.Contains("..#")) return (raw, null, "", words.Count > 1);
                await gate.WaitAsync();
                try { var (o, t) = await ResolveObjectAsync(raw); return (raw, o, t, false); }
                finally { gate.Release(); }
            }
            foreach (var r in await Task.WhenAll(words.Select(One)))
            {
                if (r.Skip) continue;
                if (r.Obj is null) { missing.Add(r.Raw); continue; }
                if (items.Any(f => f.Obj.FromSysDatabase == r.Obj.FromSysDatabase && string.Equals(f.Obj.QualifiedName, r.Obj.QualifiedName, StringComparison.OrdinalIgnoreCase))) continue;
                items.Add(new SqlPeekPanel.Item(r.Obj.QualifiedName + "   (" + r.Obj.Kind + (r.Obj.FromSysDatabase ? ", Sys Data" : ", App Data") + ")", r.Text, r.Obj));
            }
        }
        catch (Exception ex) { SetStatus("F12: " + ex.Message, AppColors.Danger); return; }

        if (items.Count == 0)
        {
            SetStatus("F12: không thấy object " + string.Join(", ", missing.Take(5)) + " (đã tìm cả App Data và Sys Data).", AppColors.Warning);
            return;
        }
        SetStatus(missing.Count > 0 ? "F12: không thấy " + string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? "…" : "") + " (đã tìm cả App Data và Sys Data)." : "", AppColors.Warning);
        EnsurePeek();
        if (fromPeek && _peekCurrent is not null) _peekStack.Push(_peekCurrent);
        _peekCurrent = items;
        PositionPeek();
        _peek!.ShowItems(items, _peekStack.Count > 0);
    }

    private static readonly Regex VarNameRegex = new(@"^@[A-Za-z_#$][\w#$]*$", RegexOptions.Compiled);
    private const string VarTypes = "int|bigint|smallint|tinyint|bit|decimal|numeric|money|smallmoney|float|real|char|varchar|nchar|nvarchar|text|ntext|date|datetime|datetime2|smalldatetime|time|datetimeoffset|uniqueidentifier|xml|varbinary|binary|image|sql_variant|table|cursor|sysname|timestamp|rowversion|hierarchyid|geography|geometry";

    /// <summary>Dòng khai báo (từ 1) của biến <paramref name="name"/> (tham số store hoặc DECLARE) trong lô — khoảng giữa hai dòng GO — chứa dòng <paramref name="caretLine"/>:
    /// khai báo gần nhất phía trên con trỏ, không có thì khai báo đầu tiên của lô; 0 = không thấy. Cùng luật với findVarDecl của sqleditor.html.</summary>
    internal static int FindVarDecl(string text, string name, int caretLine)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        int s = 0, e = lines.Length - 1;
        for (var i = caretLine - 2; i >= 0; i--) if (i < lines.Length && Regex.IsMatch(lines[i], @"^\s*GO\s*$", RegexOptions.IgnoreCase)) { s = i + 1; break; }
        for (var j = caretLine; j < lines.Length; j++) if (Regex.IsMatch(lines[j], @"^\s*GO\s*$", RegexOptions.IgnoreCase)) { e = j - 1; break; }
        var re = new Regex(@"(^|[\s,(])" + Regex.Escape(name) + @"(?![\w@#$])\s+(?:as\s+)?(?:" + VarTypes + @")\b", RegexOptions.IgnoreCase);
        int before = 0, first = 0;
        for (var k = s; k <= e && k < lines.Length; k++)
        {
            if (Regex.IsMatch(lines[k], @"^\s*--") || !re.IsMatch(lines[k])) continue;
            if (first == 0) first = k + 1;
            if (k + 1 <= caretLine) before = k + 1;
        }
        return before != 0 ? before : first;
    }

    /// <summary>F12 trên biến @x: khung peek chứa cả script, cuộn tới dòng khai báo và tô sáng (như FCode).</summary>
    private void PeekVariable(Scintilla source, string name, bool fromPeek)
    {
        var text = source.Text;
        var at = FindVarDecl(text, name, source.CurrentLine + 1);
        if (at == 0)
        {
            SetStatus($"F12: không thấy khai báo của biến {name} trong lô script hiện tại (tham số store hoặc DECLARE).", AppColors.Warning);
            return;
        }
        SetStatus("", AppColors.Warning);
        EnsurePeek();
        var items = new List<SqlPeekPanel.Item> { new($"{name} — khai báo ở dòng {at}", text, null, at, name) };
        if (fromPeek && _peekCurrent is not null) _peekStack.Push(_peekCurrent);
        _peekCurrent = items;
        PositionPeek();
        _peek!.ShowItems(items, _peekStack.Count > 0);
    }

    private void EnsurePeek()
    {
        if (_peek is not null) return;
        _peek = new SqlPeekPanel();
        _peek.CloseRequested += ClosePeek;
        _peek.OpenTabRequested += OpenPeekItemInTab;
        _split.Panel1.Controls.Add(_peek);
        _split.Panel1.Resize += (_, _) => PositionPeek();
    }

    /// <summary>Phủ gần hết khung editor (chừa lề 48px trái / 24px phải / 36px trên dưới) như khung peek của bản Web.</summary>
    private void PositionPeek()
    {
        if (_peek is null) return;
        var host = _split.Panel1.ClientSize;
        var l = NativeSkin.S(48); var r = NativeSkin.S(24); var v = NativeSkin.S(36);
        _peek.SetBounds(l, v, Math.Max(200, host.Width - l - r), Math.Max(120, host.Height - 2 * v));
    }

    private void ClosePeek()
    {
        _peekStack.Clear();
        _peekCurrent = null;
        if (_peek is not null) _peek.Visible = false;
        FocusEditor();
    }

    /// <summary>Esc: quay lại bước peek trước đó; hết bước thì đóng.</summary>
    private void PeekBack()
    {
        if (_peek is null) return;
        if (_peekStack.Count > 0)
        {
            _peekCurrent = _peekStack.Pop();
            _peek.ShowItems(_peekCurrent, _peekStack.Count > 0);
            return;
        }
        ClosePeek();
    }

    private void OpenPeekItemInTab(SqlPeekPanel.Item item)
    {
        if (item.Obj is null) return;   // khung xem khai báo biến: không có object để mở thành tab
        OpenObjectInNewTabRequested?.Invoke(item.Obj.QualifiedName, item.Obj.FromSysDatabase, item.Text);
        ClosePeek();
    }

    // =====================================================================  Claude / Gemini nhúng

    AiWebPanel ISqlTab.ShowAi(Bcode.Shared.AiSite site)
    {
        if (!_aiPanels.TryGetValue(site, out var panel))
        {
            panel = new AiWebPanel(site) { Dock = DockStyle.Fill };
            _aiPanels[site] = panel;
        }
        ShowSide(panel, site == Bcode.Shared.AiSite.Claude ? "Claude" : "Gemini");
        return panel;
    }

    /// <summary>Khung "Hỏi Fast" nhúng (MainForm tạo khi mở lần đầu ở tab này).</summary>
    private Control? _fastPanel;
    public event Action? FastAskRequested;

    /// <summary>Hiện "Hỏi Fast" trong khung nhúng bên phải (cùng chỗ Claude / Gemini).</summary>
    public void ShowFastAsk(Control panel)
    {
        panel.Dock = DockStyle.Fill;
        _fastPanel = panel;
        ShowSide(panel, "Hỏi Fast");
    }

    /// <summary>Đặt <paramref name="panel"/> vào khung bên phải với thanh đầu Claude · Gemini · Hỏi Fast · ✕ (nút <paramref name="active"/> sáng).</summary>
    private void ShowSide(Control panel, string active)
    {
        _aiHost.Controls.Clear();
        var head = new BarFlow { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(4) };
        foreach (var name in new[] { "Claude", "Gemini", "Hỏi Fast" })
        {
            var b = PillButton.Flat(name, name == active);
            b.OnPanelAlt = true; b.Margin = new Padding(0, 0, 4, 0);
            b.Click += (_, _) =>
            {
                if (name == "Hỏi Fast") { if (_fastPanel is not null) ShowFastAsk(_fastPanel); else FastAskRequested?.Invoke(); }
                else ((ISqlTab)this).ShowAi(name == "Claude" ? Bcode.Shared.AiSite.Claude : Bcode.Shared.AiSite.Gemini);
            };
            head.Controls.Add(b);
        }
        var close = PillButton.Flat("✕");
        close.OnPanelAlt = true; close.Click += (_, _) => { _outer.Panel2Collapsed = true; };
        head.Controls.Add(close);
        _aiHost.Controls.Add(panel);
        _aiHost.Controls.Add(head);
        if (_outer.Panel2Collapsed)
        {
            _outer.Panel2Collapsed = false;
            _outer.SplitterDistance = Math.Max(200, (int)(_outer.Width * 0.6));
        }
    }
}
