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
/// Đã có: tô màu SQL theo theme, số dòng, F5 / Ctrl+Enter chạy (chạy phần bôi đen nếu có), Alt+F5 huỷ, F7 Beauty, Alt+Z wrap, Ctrl+R ẩn/hiện kết quả,
/// F12 / Ctrl+F12 mở object dưới con trỏ ở tab mới, gợi ý từ khoá + tên bảng/view/procedure khi gõ, Default Type (UPPER/lower), Comment/Uncomment,
/// Open/Save .sql, Lưu lịch sử SQL, lịch sử sửa procedure, Reset Connection, Result Tab, tab Claude/Gemini nhúng, zoom Ctrl+lăn chuột.
/// Chưa có (chỉ ở chế độ Web): Debug từng bước, gợi ý AI ghost-text/AI sửa SQL, gợi ý cột theo ngữ cảnh, F12 dạng khung peek (ở đây mở tab mới), tab Pivot,
/// "ngủ đông" tab (không cần — tab native rất nhẹ).
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

    public event Action<DataTable>? ResultReady;
    public event Action<string>? CreateRptRequested;
    public event Action<string, bool, bool, int, int>? ScriptExecuted;
    public event Action<string, bool>? SaveHistoryRequested;
    public Func<string>? CurrentProject { get; set; }
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
        _bar.Controls.Add(_btnRun);
        _bar.Controls.Add(_db);
        _bar.Controls.Add(Btn("✦ Beauty", "Làm đẹp SQL (F7) — phần bôi đen hoặc cả script", BeautyFormat));
        _bar.Controls.Add(_defaultType);
        _bar.Controls.Add(MenuBtn("Script ▾", BuildScriptMenu));
        _bar.Controls.Add(MenuBtn("Lịch sử ▾", BuildHistoryMenu));
        _bar.Controls.Add(MenuBtn("File ▾", BuildFileMenu));
        _bar.Controls.Add(MenuBtn("Tùy chọn ▾", BuildOptionsMenu));

        // ---- editor ----
        ConfigureEditor();
        _ed.TextChanged += (_, _) => _version++;
        _hints = new NativeSqlHints(_ed, this, () => _useSys, () => CurrentProject?.Invoke() ?? "", snippets, _sqlObjectService,
            () => _running ? null : _persistentConn, () => _objectNames, () => _tableNames, () => _service.Connections.Current is not null);
        _ed.MouseWheel += (_, e) => { };
        WebMenu.AttachTo(_ed, BuildEditorMenu);
        _ed.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right && (ModifierKeys & Keys.Control) != 0) OnCtrlRightClick(e.Location); };

        // ---- bố cục: [thanh nút] / [editor | kết quả + trạng thái], bên phải là khung AI (ẩn) ----
        _split.Panel1.Controls.Add(_ed);
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

    private void ConfigureEditor() { SqlScintilla.Configure(_ed); FcodeLexer.ApplySql(_ed); }   // tô T-SQL đầy đủ như Monaco (hàm, dbo.X(...), chuỗi N'..')

    private void ApplyEditorTheme() { if (!IsDisposed) { SqlScintilla.ApplyTheme(_ed); FcodeLexer.ApplySql(_ed); } }

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

    /// <summary>Debug từng bước chỉ có ở chế độ Web: ở đây nạp định nghĩa vào editor và báo rõ.</summary>
    public Task LoadAndDebugAsync(string definition, string? callText)
    {
        SetScriptText(definition + (string.IsNullOrWhiteSpace(callText) ? "" : Environment.NewLine + "GO" + Environment.NewLine + callText));
        SetStatus("Debug từng bước chưa có ở chế độ App — đã nạp store/function vào editor; mở chế độ Web (Settings → Chế độ giao diện) để debug.", AppColors.Warning);
        return Task.CompletedTask;
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
        var combo = ShortcutRegistry.FromKeys(keyData);
        if (combo is not null)
        {
            if (combo == ShortcutRegistry.Get("editor.run") || combo == ShortcutRegistry.Get("editor.run2")) { if (!_running) _ = RunAsync(); return true; }
            if (combo == ShortcutRegistry.Get("editor.cancel")) { CancelRun(); return true; }
            if (combo == ShortcutRegistry.Get("editor.beauty")) { BeautyFormat(); return true; }
            if (combo == ShortcutRegistry.Get("editor.wrap")) { ToggleWrap(); return true; }
            if (combo == ShortcutRegistry.Get("editor.saveHistory")) { SaveToHistory(); return true; }
            if (combo == ShortcutRegistry.Get("editor.peek") || combo == ShortcutRegistry.Get("editor.openObject")) { _ = OpenObjectUnderCaretAsync(); return true; }
            if (combo == ShortcutRegistry.Get("editor.open")) { OpenFile(); return true; }
            if (combo == ShortcutRegistry.Get("editor.save")) { SaveFile(); return true; }
            if (combo == ShortcutRegistry.Get("editor.find")) { ShowFind(); return true; }
            if (combo == ShortcutRegistry.Get("editor.comment")) { ToggleComment(true); return true; }
            if (combo == ShortcutRegistry.Get("editor.uncomment")) { ToggleComment(false); return true; }
            if (combo == ShortcutRegistry.Get("editor.writeSchema")) { WriteSchema(); return true; }
        }
        if (keyData == (Keys.Control | Keys.R)) { _split.Panel2Collapsed = !_split.Panel2Collapsed; return true; }
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
        .Add("Comment (chú thích dòng đang chọn)", () => ToggleComment(true), shortcut: ShortcutRegistry.Display("editor.comment"))
        .Add("Uncomment (bỏ chú thích)", () => ToggleComment(false), shortcut: ShortcutRegistry.Display("editor.uncomment"))
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

    private WebMenu BuildOptionsMenu() => new WebMenu()
        .Add("Word Wrap", ToggleWrap, shortcut: ShortcutRegistry.Display("editor.wrap"), @checked: _wordWrap)
        .Add("Gợi ý code khi gõ (mẫu, hàm + tham số, cột, từ khoá, bảng) — Ctrl+Space gọi ngay", () => { _suggestOn = !_suggestOn; if (_hints is not null) _hints.Enabled = _suggestOn; }, @checked: _suggestOn)
        .Add("Reset Connection mỗi lần chạy", () => { _resetConnOn = !_resetConnOn; if (_resetConnOn) DisposePersistentConnection(); }, @checked: _resetConnOn)
        .Add("Result Tab (kết quả ra tab mới)", () => _resultTabOn = !_resultTabOn, @checked: _resultTabOn)
        .Add("Ẩn / hiện khung kết quả", () => _split.Panel2Collapsed = !_split.Panel2Collapsed, shortcut: "Ctrl+R")
        .AddCaption("Cỡ chữ")
        .Add("Tăng cỡ chữ", () => _ed.Zoom = Math.Min(20, _ed.Zoom + 1))
        .Add("Giảm cỡ chữ", () => _ed.Zoom = Math.Max(-8, _ed.Zoom - 1))
        .AddCaption("Claude / Gemini")
        .Add("Gửi script sang Claude (web)", () => SendToAi("claude"))
        .Add("Gửi script sang Gemini (web)", () => SendToAi("gemini"))
        .Add("Claude/Gemini nhúng vào tab SQL Query (tắt = tab riêng)", () => AppSettings.AiEmbedded = !AppSettings.AiEmbedded, @checked: AppSettings.AiEmbedded);

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

    // =====================================================================  Claude / Gemini nhúng

    AiWebPanel ISqlTab.ShowAi(Bcode.Shared.AiSite site)
    {
        if (!_aiPanels.TryGetValue(site, out var panel))
        {
            panel = new AiWebPanel(site) { Dock = DockStyle.Fill };
            _aiPanels[site] = panel;
        }
        _aiHost.Controls.Clear();
        var head = new BarFlow { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, Padding = new Padding(4) };
        foreach (var s in new[] { Bcode.Shared.AiSite.Claude, Bcode.Shared.AiSite.Gemini })
        {
            var target = s;
            var b = PillButton.Flat(s == Bcode.Shared.AiSite.Claude ? "Claude" : "Gemini", s == site);
            b.OnPanelAlt = true; b.Margin = new Padding(0, 0, 4, 0);
            b.Click += (_, _) => ((ISqlTab)this).ShowAi(target);
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
        return panel;
    }
}
