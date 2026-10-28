using System.Data;
using System.Net.Http;
using System.Text.RegularExpressions;
using Bcode.App.Controls;
using Bcode.App.Forms;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Controls;

public class RawSqlControl : UserControl, ISqlTab
{
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _barWeb = new();
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _editorWeb = new();
    private bool _editorReady;
    private bool _editorReadyDeferred;   // chế độ gộp: editor-ready tới trước khi khung sẵn sàng — chạy lại khi _edPage.Ready (xem InitMergedAsync)
    private SplitContainer? _split; // editor (Panel1) | kết quả + message (Panel2)
    private string? _pendingScriptText;
    private readonly AppSettings _settings = AppSettings.Load();
    private bool _wordWrap;
    private bool _useSysDatabase;
    private bool _suggestOn = true;
    private bool _resetConnOn = true;
    private bool _resultTabOn;
    private List<DataTable>? _lastResultSchema;   // cấu trúc (không dữ liệu) các bảng kết quả lần chạy gần nhất — cho Write Schema (.xsd)
    private bool _debugStepOn;
    private StepPlan? _plan;          // phiên debug từng bước đang mở (null = không debug)
    private string _planText = "";    // nội dung script lúc dựng _plan — khác nội dung editor = đã sửa trong lúc debug
    private Dictionary<string, string>? _planValues;   // giá trị tham số đã nhập (dùng lại khi dựng lại kế hoạch)
    private int _executedLine;        // dòng cuối cùng đã chạy tới (0 = chưa chạy gì)
    private System.Data.DataTable? _lastVars;   // bảng Variables của bước vừa chạy — cho nút "🧩 SQL động"
    private int _lastOkLine, _errorFrom, _errorLine;   // bước chạy được gần nhất; đoạn dòng của bước vừa lỗi (0 = không lỗi) — tô đỏ trên editor
    private bool _stepRunning;
    private bool _pendingDebugRequested;  // "Debug store/function" mở tab khi editor chưa sẵn sàng — bắt đầu debug ngay khi editor-ready
    private string? _pendingDebugCall;
    private readonly HashSet<int> _breakpoints = new();
    private readonly MultiResultView _resultView;
    private readonly TextBox _statusLabel;
    private string? _lastScript;     // script của lần Execute gần nhất (cho tab Pivot → Tạo file Excel pivot)
    private readonly SqlResultTabs _tabs; // 2 tab kết quả: Grid Result | Message (PRINT/RAISERROR/lỗi, tô màu)
    private readonly RawSqlService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly LookupService _lookupService;
    private readonly SnippetLibraryService _snippets;
    private static DateTime _rateLimitCooldownUntil = DateTime.MinValue;
    private string? _currentFilePath;
    private SqlConnection? _persistentConn;
    /// <summary>Đang có 1 lần chạy script — chặn bấm Ctrl+Enter/F5/Run liên tiếp: trước đây mỗi lần bấm
    /// chạy lại cả script song song (INSERT/UPDATE bị thực thi nhiều lần; với "Reset Connection" tắt thì
    /// 2 lệnh dùng chung 1 SqlConnection → lỗi DataReader đang mở).</summary>
    private bool _running;
    private CancellationTokenSource? _runCts;   // lần Execute đang chạy — Cancel() để dừng (nút Cancel / phím tắt editor.cancel)
    private string _persistentConnStamp = "";
    private bool _persistentConnUsesSys;

    private static readonly HttpClient _aiHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    
    private static readonly Regex TableRefRegex = new(
        @"\b(?:FROM|JOIN|UPDATE|INTO)\s+(\[?[\w$]+\]?(?:\.\[?[\w$]+\]?)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Tab SQL Query dựng sẵn (pre-warm) đang chờ ở nền: chưa nạp danh sách bảng của database cho tới khi được dùng (<see cref="BeginUse"/>) — để không
    /// tự kết nối database lúc người dùng chưa mở tab.</summary>
    private bool _deferTables;
    private bool _barReady;

    /// <summary>Cả thanh Execute lẫn editor Monaco đã nạp xong — tab sẵn sàng gõ ngay.</summary>
    public bool IsReady => _editorReady && _barReady;

    // ---- Đo thời gian mở tab (apply-timing.log): các chặng tính từ lúc tạo control, ghi 1 dòng khi thanh nút + editor đều đã sẵn sàng ----
    private readonly System.Diagnostics.Stopwatch _openWatch = System.Diagnostics.Stopwatch.StartNew();
    private readonly long _openEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();   // cùng đồng hồ với Date.now() của trang
    private readonly List<string> _openMarks = new();
    private bool _openLogged, _tablesTimed;

    // Màn che lúc mở tab: thanh nút và editor Monaco nạp lệch nhau (thanh trước, Monaco sau ~0,2 s) nên trước đây tab hiện khung trống → thanh →
    // vùng editor đen → chữ. Che cả tab bằng nền theme cho tới khi CẢ HAI sẵn sàng (IsReady) rồi gỡ một lượt — thanh + editor hiện cùng lúc.
    private readonly Panel _curtain = new() { Dock = DockStyle.Fill, BackColor = Bcode.App.UI.AppColors.PanelAlt };
    private readonly System.Windows.Forms.Timer _curtainTimer = new() { Interval = 2500 };

    private void Reveal()
    {
        _curtainTimer.Stop();
        _curtainTimer.Dispose();
        if (_curtain.IsDisposed) return;
        var ready = IsReady;
        _curtain.Dispose();
        MarkOpen(ready ? "mở màn" : "mở màn (hết giờ chờ)");
    }

    private void RevealIfReady() { if (IsReady) Reveal(); }

    private void MarkOpen(string what)
    {
        if (_openLogged) return;
        _openMarks.Add($"{what} {_openWatch.ElapsedMilliseconds}");
        if (!_editorReady || !_barReady || !_curtain.IsDisposed) return;
        _openLogged = true;
        Bcode.App.UI.ThemeManager.LogTiming($"SQL Query{(_deferTables ? " (dựng sẵn)" : "")}: thanh + editor sẵn sàng sau {_openWatch.ElapsedMilliseconds} ms — {string.Join(", ", _openMarks)}");
    }

    private int _barRetries, _editorRetries;
    private bool _splitUserMoved;     // người dùng đã tự kéo thanh chia editor | kết quả
    private int? _restoreSplit;       // vị trí thanh chia cần đặt lại sau "ngủ đông"
    private string? _pendingViewState; // vị trí con trỏ / cuộn của Monaco cần đặt lại khi editor sẵn sàng
    private bool _pushTogglesOnBarReady; // tab dựng lại từ "ngủ đông": đẩy trạng thái các nút bật/tắt lên thanh khi thanh nạp xong

    /// <summary>0x80004004 (E_ABORT): khởi tạo WebView2 bị huỷ vì control bị đổi cha / tạo lại cửa sổ giữa chừng — thường qua đi nếu thử lại.</summary>
    private static bool IsAbort(Exception ex) => ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80004004) } || ex.HResult == unchecked((int)0x80004004);

    /// <summary>Tab dựng sẵn được lấy ra dùng: nạp danh sách bảng cho gợi ý như một tab mới bình thường.</summary>
    public void BeginUse()
    {
        if (!_deferTables) return;
        _deferTables = false;
        Bcode.App.UI.ThemeManager.LogTiming($"SQL Query: lấy tab dựng sẵn (tạo cách đây {_openWatch.ElapsedMilliseconds} ms, sẵn sàng: {IsReady})");
        if (_editorReady) _ = LoadTablesForEditorAsync(); // chưa sẵn sàng thì editor-ready sẽ tự nạp
        // Đã nạp xong từ trước (lúc còn ở khung ẩn nên chưa nhận focus) → sau khi được gắn vào thanh tab thì đưa con trỏ vào editor.
        if (_editorReady && IsHandleCreated)
            BeginInvoke(new Action(() => { if (Visible && FindForm() is { } f && ReferenceEquals(Form.ActiveForm, f)) FocusEditor(); }));
    }

    public RawSqlControl(RawSqlService service, SqlObjectBrowserService sqlObjectService, LookupService lookupService, SnippetLibraryService snippets, bool prewarm = false)
    {
        _deferTables = prewarm;
        _service = service;
        _sqlObjectService = sqlObjectService;
        _lookupService = lookupService;
        _snippets = snippets;
        Dock = DockStyle.Fill;

        _barWeb.Dock = DockStyle.Top;
        _barWeb.Height = 40;
        // (gộp thanh + editor: xem _mergeBarEditor / InitMergedAsync)

        _statusLabel = new TextBox 
        { 
            Dock = DockStyle.Top, 
            Height = 22, 
            ForeColor = Color.DimGray, 
            ReadOnly = true,               // Cho phép bôi đen copy nhưng không được gõ thêm
            BorderStyle = BorderStyle.None // Ẩn khung viền để trông giống hệt Label
        };

        // Kết quả tách 2 tab như FCode: "Grid Result" (các bảng) và "Message" (PRINT/RAISERROR/lỗi, tô màu để dễ thấy) — cả hai là trang WebView2,
        // xem SqlResultTabs. Sau mỗi lần chạy tab được chọn tự động (có lỗi → Message; không có bảng mà có message → Message; còn lại → Grid Result).
        _tabs = new SqlResultTabs(merge: Bcode.App.UI.PerformanceProfile.Resolve(_settings).MergeSqlResultFrames) { Dock = DockStyle.Fill };
        _resultView = _tabs.Grid;
        _tabs.GotoLineRequested += line =>
        {
            if (EdAlive) _ = EdExec($"window.gotoLine && window.gotoLine({line})");
        };
        _tabs.CreateRptRequested += () => CreateRptRequested?.Invoke(_lastScript ?? "");

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        _split = split;
        split.Panel1MinSize = 80;
        split.Panel2MinSize = 80;

        _mergeBarEditor = Bcode.App.UI.PerformanceProfile.Resolve(_settings).MergeSqlBarEditor && WebFrameHost.CanHost("sqlquerybar.html", "sqleditor.html");
        if (_mergeBarEditor)
        {
            _host = new WebFrameHost { Dock = DockStyle.Fill };
            _barPage = _host.AddFrame("bar", "sqlquerybar.html", height: 40);
            _edPage = _host.AddFrame("editor", "sqleditor.html");
            split.Panel1.Controls.Add(_host);
        }
        else
        {
            _editorWeb.Dock = DockStyle.Fill;
            split.Panel1.Controls.Add(_editorWeb);
        }

        split.Panel2.Controls.Add(_tabs);
        split.Panel2.Controls.Add(_statusLabel);
        // Mở tab lên: editor chiếm ~72% chiều cao, khung kết quả ~28% (trước đây cố định 260px nên màn hình cao thì khung kết quả quá to).
        // Còn theo tỉ lệ đó khi đổi cỡ cửa sổ cho tới khi người dùng tự kéo thanh chia.
        var splitApplying = false;
        void ApplySplit()
        {
            if (_restoreSplit is { } want && split.Height >= 200)
            {
                // Tab vừa dựng lại sau "ngủ đông": về đúng chỗ người dùng đã kéo (SplitterMoved bên dưới đánh dấu là người dùng kéo).
                _restoreSplit = null;
                try { split.SplitterDistance = Math.Clamp(want, split.Panel1MinSize, Math.Max(split.Panel1MinSize, split.Height - split.Panel2MinSize - split.SplitterWidth)); }
                catch { /* chưa đủ chỗ — giữ tỉ lệ mặc định */ }
                return;
            }
            if (_splitUserMoved || split.Height < 200) return;
            splitApplying = true;
            try { split.SplitterDistance = Math.Clamp((int)(split.Height * 0.72), split.Panel1MinSize, Math.Max(split.Panel1MinSize, split.Height - split.Panel2MinSize - split.SplitterWidth)); }
            catch { /* chưa đủ chỗ để chia — lần đổi cỡ sau sẽ thử lại */ }
            finally { splitApplying = false; }
        }
        split.HandleCreated += (_, _) => ApplySplit();
        split.SizeChanged += (_, _) => ApplySplit();
        split.SplitterMoved += (_, _) => { if (!splitApplying) _splitUserMoved = true; };

        // Khung Claude/Gemini nhúng bên phải (ẩn mặc định; bật bằng Settings "Claude/Gemini nhúng vào SQL Query") — xem ShowAi.
        _aiSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6, Panel2Collapsed = true };
        _aiSplit.Panel1.Controls.Add(split);
        Controls.Add(_aiSplit);
        if (!_mergeBarEditor) Controls.Add(_barWeb);
        Controls.Add(_curtain);
        _curtain.BringToFront();
        _curtainTimer.Tick += (_, _) => Reveal();   // trang lỗi / không báo sẵn sàng: vẫn mở màn, không che mãi
        _curtainTimer.Start();

        Bcode.App.UI.ThemeManager.ThemeChanged += PushThemeToAll;
        EditorFontSizeChanged += OnEditorFontSizeChanged;
        SqlBarLayout.Changed += OnBarLayoutChanged;

        MarkOpen("dựng control");
        if (_mergeBarEditor) _ = InitMergedAsync();
        else
        {
            _ = InitBarWebAsync();
            _ = InitEditorWebAsync();
        }

        Disposed += (_, _) =>
        {
            _curtainTimer.Dispose();
            DisposePersistentConnection();
            Bcode.App.UI.ThemeManager.ThemeChanged -= PushThemeToAll;
            EditorFontSizeChanged -= OnEditorFontSizeChanged;
            SqlBarLayout.Changed -= OnBarLayoutChanged;
        };

        async Task InitBarWebAsync()
        {
            try
            {
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_barWeb);
                MarkOpen("WebView2 thanh");

                _barWeb.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    OnBarMessage(doc.RootElement);
                };

                _barWeb.CoreWebView2.NavigationCompleted += (_, _) => OnBarLoaded();

                await _barWeb.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BarInitScript());
                _barWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("sqlquerybar.html"));
            }
            catch (Exception ex)
            {
                // E_ABORT: control bị chuyển chỗ / tạo lại cửa sổ lúc WebView2 đang khởi tạo (vd khôi phục tab ngay khi mở) — thử lại thay vì báo lỗi.
                if (IsAbort(ex) && _barRetries++ < 3 && !IsDisposed) { await Task.Delay(400); _ = InitBarWebAsync(); return; }
                MessageBox.Show(this, "Không khởi tạo được Toolbar WebView2: " + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        async Task InitEditorWebAsync()
        {
            try
            {
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_editorWeb);
                MarkOpen("WebView2 editor");
                _editorWeb.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                await InstallHintCatalogScriptAsync();

                _editorWeb.CoreWebView2.WebMessageReceived += async (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    await OnEditorMessage(doc.RootElement);
                };

                _editorWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("sqleditor.html"));
            }
            catch (Exception ex)
            {
                if (IsAbort(ex) && _editorRetries++ < 3 && !IsDisposed) { await Task.Delay(400); _ = InitEditorWebAsync(); return; }
                MessageBox.Show(this, "Không khởi tạo được Monaco SQL Editor: " + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // ---------------- Thanh Execute + editor: 2 WebView2 riêng, hoặc gộp 1 WebView2 (2 iframe) ----------------
    // Chế độ hiệu năng "Gộp thanh nút + editor" (PerformanceProfile.MergeSqlBarEditor): thanh sqlquerybar.html và editor sqleditor.html nằm
    // chung 1 WebView2 (WebFrameHost) trong Panel1 của thanh chia — bớt 1 tiến trình renderer (~18MB) mỗi tab SQL. Mọi chỗ trong file này
    // chạm trang đều đi qua EdExec / BarExec / EdAlive / BarAlive / EdControl / BarControl / BarHeight để chạy được cả hai cách.

    private WebFrameHost? _host;
    private IWebPage? _barPage, _edPage;
    private bool _mergeBarEditor;
    private int _barHeightPx = 40;

    private bool EdAlive => _mergeBarEditor ? _edPage is { IsReady: true } : _editorWeb.CoreWebView2 is not null;
    private bool BarAlive => _mergeBarEditor ? _barPage is { IsReady: true } : _barWeb.CoreWebView2 is not null;
    private Control EdControl => _mergeBarEditor ? _host! : _editorWeb;
    private Control BarControl => _mergeBarEditor ? _host! : _barWeb;
    private int BarHeight => _mergeBarEditor ? _barHeightPx : _barWeb.Height;

    private Task<string> EdExec(string js) => _mergeBarEditor ? _host!.EvalAsync(_edPage!, js) : _editorWeb.CoreWebView2.ExecuteScriptAsync(js);
    private Task<string> BarExec(string js) => _mergeBarEditor ? _host!.EvalAsync(_barPage!, js) : _barWeb.CoreWebView2.ExecuteScriptAsync(js);

    /// <summary>Chiều cao thật của thanh (px thiết bị) — theo UiScale/DPI và khi nội dung xuống dòng.</summary>
    private void SetBarHeight(int devicePx)
    {
        var h = Math.Clamp(devicePx + 1, Bcode.App.UI.DpiScale.Px(this, 40), Bcode.App.UI.DpiScale.Px(this, 260));
        if (_mergeBarEditor) { _barHeightPx = h; _host?.SetFrameHeightDevice(_barPage!, h); }
        else _barWeb.Height = h;
    }

    private async Task InitMergedAsync()
    {
        _host!.CoreInitializing = async core =>
        {
            MarkOpen("WebView2");
            core.Settings.AreDefaultContextMenusEnabled = false;
            await InstallHintCatalogScriptAsync(core);
            await core.AddScriptToExecuteOnDocumentCreatedAsync(BarInitScript());
        };
        _host.HostLoaded += () => MarkOpen("framehost");
        _barPage!.Message += root => OnBarMessage(root);
        _barPage.Ready += OnBarLoaded;
        _edPage!.Message += root => { var copy = root.Clone(); _ = OnEditorMessage(copy); };
        // Khung editor được đánh dấu sẵn sàng (__frame-ready, lúc iframe 'load' xong) SAU khi trang đã báo editor-ready (Monaco nạp bằng script động nên
        // 'load' chờ cả Monaco) → lúc editor-ready mọi EdExec đều rơi vì khung "chưa sẵn sàng": tab mở lần đầu trống trơn (nội dung object, vị trí, wrap...).
        // editor-ready tới sớm thì hoãn (xem OnEditorMessage) và chạy lại ở đây.
        _edPage.Ready += () =>
        {
            if (!_editorReadyDeferred) return;
            _editorReadyDeferred = false;
            using var doc = System.Text.Json.JsonDocument.Parse("{\"action\":\"editor-ready\"}");
            _ = OnEditorMessage(doc.RootElement.Clone());
        };
        await Task.CompletedTask;
    }

    /// <summary>Một tin từ trang thanh Execute (sqlquerybar.html) — dùng chung cho cách tách riêng và cách gộp vào WebFrameHost.</summary>
    private void OnBarMessage(System.Text.Json.JsonElement root2)
    {
            switch (root2.GetProperty("action").GetString())
            {
                case "__height": MarkOpen("cao " + root2.GetProperty("height").GetInt32()); SetBarHeight(root2.GetProperty("height").GetInt32()); break;
                case "open": OpenFile(); break;
                case "save": SaveFile(); break;
                case "run": _ = RunAsync(); break;
                case "cancel": CancelRun(); break;
                case "debug-target": _ = PickDebugTargetAsync(); break;
                case "write-schema": _ = WriteSchemaAsync(); break;
                case "check-fields": _ = CheckFieldsAsync(); break;
                case "comment": ToggleComment(true); break;
                case "uncomment": ToggleComment(false); break;
                case "beauty": BeautyFormat(); break;
                case "toggle-wrap":
                    _scriptBoxWordWrapToggle(root2.GetProperty("value").GetBoolean());
                    break;
                case "options": BuildOptionsMenu().Show(BarControl, 10, BarHeight); break;
                // Ô % phóng to chữ nằm trên thanh Execute: các nút của nó gửi lệnh về editor (xem sqlquerybar.html / sqleditor.html zoomAct).
                case "zoom-set": _ = EdExec($"window.zoomSet && window.zoomSet({root2.GetProperty("pct").GetInt32()})"); break;
                case "zoom-act":
                    { var zoomWhat = root2.GetProperty("what").GetString(); if (zoomWhat is "minus" or "plus" or "reset") _ = EdExec($"window.zoomAct && window.zoomAct('{zoomWhat}')"); }
                    break;
                case "history": BeginInvoke(new Action(OpenSqlHistory)); break;
                case "save-history": _ = SaveToQueryHistoryAsync(); break;
                case "ask-ai": _ = SendToAiAsync(root2.GetProperty("engine").GetString() ?? "claude"); break;
                case "toggle-results": BeginInvoke(new Action(ToggleResultPanel)); break;
                case "default-type": _ = ApplyDefaultTypeChoiceAsync(root2.GetProperty("value").GetInt32()); break;
                case "db":
                    _useSysDatabase = root2.GetProperty("value").GetInt32() == 1;
                    DisposePersistentConnection();
                    _ = LoadTablesForEditorAsync();
                    break;
                case "toggle":
                    ToggleOption(root2.GetProperty("which").GetString() ?? "");
                    break;
                case "bar-group-menu": ShowBarGroupMenu(root2.Clone()); break;
                case "customize-bar": { var req = root2.Clone(); BeginInvoke(new Action(() => CustomizeBar(req))); break; }
            }

    }

    private void OnBarLoaded()
    {
        _barReady = true;
        PushThemeToAll();
        PushDatabaseToBar();
        if (_pushTogglesOnBarReady) PushTogglesToBar();
        PushBarLayout(SqlBarLayout.Load());
        MarkOpen("thanh nạp xong");
        RevealIfReady();
    }

    /// <summary>Một tin từ trang editor (sqleditor.html) — dùng chung cho cách tách riêng và cách gộp vào WebFrameHost.</summary>
    private async Task OnEditorMessage(System.Text.Json.JsonElement root)
    {
        var action = root.GetProperty("action").GetString();

        switch (action)
        {
            case "editor-ready":
                if (root.TryGetProperty("t", out var pt) && pt.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    long At(string n) => pt.TryGetProperty(n, out var v) && v.TryGetInt64(out var ms) ? ms - _openEpoch : -1;
                    MarkOpen($"[trang editor: bắt đầu {At("origin")}, nạp Monaco {At("req")}, Monaco xong {At("main")}, dựng xong {At("ready")}]");
                }
                MarkOpen(_mergeBarEditor && _edPage is { IsReady: false } ? "editor-ready (chờ khung)" : "editor-ready");
                // Chế độ gộp (MergeSqlBarEditor): khung chưa được đánh dấu sẵn sàng thì mọi lệnh xuống editor đều rơi — hoãn tới _edPage.Ready.
                if (_mergeBarEditor && _edPage is { IsReady: false }) { _editorReadyDeferred = true; break; }
                _editorReady = true;
                PushCopilotAuto();
                if (!_deferTables) _ = LoadTablesForEditorAsync();
                PushThemeToAll();
                if (_pendingScriptText is { } pendingText)
                {
                    // Lấy ra TRƯỚC khi gửi: SetScriptTextAsync có thể đưa lại vào hàng chờ (khung chưa sẵn sàng) — trước đây gửi xong mới xoá nên xoá mất luôn.
                    _pendingScriptText = null;
                    await SetScriptTextAsync(pendingText);
                }
                if (_pendingViewState is { } vs)
                {
                    _pendingViewState = null;
                    _ = EdExec($"window.restoreViewState && window.restoreViewState({System.Text.Json.JsonSerializer.Serialize(vs)})");
                }
                if (_wordWrap) _scriptBoxWordWrapToggle(true);
                MarkOpen("editor dùng được");
                if (_curtain is { IsDisposed: false }) { try { await EdExec("0"); } catch { /* tab đóng giữa chừng */ } }   // chờ các lệnh theme / chữ ở trên chạy xong trong trang rồi mới mở màn
                RevealIfReady();
                // Tab vừa mở/gắn vào: editor sẵn sàng thì nhận focus bàn phím luôn (nếu cửa sổ đang là cửa sổ làm việc).
                // KHÔNG focus khi đây là tab dựng sẵn đang nằm ở khung ẩn (_deferTables): Visible vẫn true ở đó, nên trước đây tab dự phòng nạp xong
                // (~1,2 giây sau khi mở 1 tab SQL) cướp focus bàn phím của editor đang gõ → mất con trỏ, phải bấm chuột mới hiện lại.
                if (!_deferTables && Visible && IsHandleCreated && FindForm() is { } owner && ReferenceEquals(Form.ActiveForm, owner)) FocusEditor();
                if (_pendingDebugRequested)
                {
                    _pendingDebugRequested = false;
                    await StartStepDebugAsync(_pendingDebugCall);
                }
                break;

            // Trang editor báo tab này chưa có danh sách mẫu gợi ý (rsfilter, rsrep...) — đẩy lại (xem sqleditor.html: askHintsIfMissing).
            case "need-hints":
                _ = LoadTablesForEditorAsync();
                break;

            case "run":
                await RunAsync();
                break;

            case "cancel":
                CancelRun();
                break;

            // Ctrl+I: AI sửa/sinh SQL theo yêu cầu (có kèm cấu trúc bảng)
            case "ai-edit":
                _ = HandleAiEditAsync(
                    root.GetProperty("requestId").GetInt32(),
                    root.GetProperty("instruction").GetString() ?? "",
                    root.TryGetProperty("selection", out var selProp) ? selProp.GetString() ?? "" : "",
                    root.TryGetProperty("script", out var scrProp) ? scrProp.GetString() ?? "" : "");
                break;

            case "beauty":
                BeautyFormat();
                break;

            // Gợi ý code: trang xin danh sách cột của một bảng (gõ  a.  sau alias)
            case "hint-columns":
                _ = SendHintColumnsAsync(root.GetProperty("table").GetString() ?? "", root.GetProperty("reqId").GetInt32());
                break;

            // Debug từng bước (thanh nổi trong editor + F10 / Shift+F5 / Ctrl+F10 + chấm đỏ ở lề)
            case "debug-next": await DebugCommandAsync("step"); break;
            case "debug-continue": await DebugCommandAsync("continue"); break;
            case "debug-to-cursor": await DebugCommandAsync("to-cursor", root.TryGetProperty("line", out var dl) ? dl.GetInt32() : 0); break;
            case "debug-stop": StopStepDebug(); break;
            case "debug-back": await DebugCommandAsync("back"); break;
            case "debug-find-error": await DebugFindErrorAsync(); break;
            case "debug-dynsql": await ShowDynamicSqlAsync(); break;
            case "debug-edited":
                // Sửa code trong lúc debug: bỏ tô đỏ cũ ngay, lần bấm Step / Continue / Tìm lỗi kế tiếp chạy lại từ đầu với code mới.
                if (_plan is not null && _errorLine > 0)
                {
                    _errorFrom = _errorLine = 0;
                    _statusLabel.ForeColor = Color.DarkOrange;
                    _statusLabel.Text = "Script đã sửa — F10 / F5 / Tìm lỗi sẽ chạy lại từ đầu với code mới (giữ giá trị tham số).";
                    PushDebugState();
                }
                break;
            // Chức năng của thanh Execute gọi bằng phím tắt khai báo trong Template giao diện (Phím tắt → Editor SQL).
            case "bar-action": RunBarAction(root.GetProperty("name").GetString() ?? ""); break;
            case "debug-breakpoints":
                _breakpoints.Clear();
                foreach (var b in root.GetProperty("lines").EnumerateArray()) _breakpoints.Add(b.GetInt32());
                break;

            case "toggle-wrap-key":
                _scriptBoxWordWrapToggle(!_wordWrap);
                break;

            case "fast-ghost-mode":
                BeginInvoke(ToggleFastGhostLong);
                break;

            case "close-context-menu":
                this.BeginInvoke(() => WebMenu.CloseActive());
                break;

            case "show-context-menu":
                var x = root.TryGetProperty("x", out var xProp) ? xProp.GetInt32() : 0;
                var y = root.TryGetProperty("y", out var yProp) ? yProp.GetInt32() : 0;
                ShowEditorContextMenu(x, y);
                break;

            case "peek-var-missing":
                _statusLabel.ForeColor = Color.DarkOrange;
                _statusLabel.Text = "F12: không thấy khai báo của biến " + (root.TryGetProperty("word", out var mv) ? mv.GetString() : "") + " trong lô script hiện tại (tham số store hoặc DECLARE).";
                break;

            case "peek-object":
                {
                    var peekMode = root.TryGetProperty("mode", out var pm) ? pm.GetString() ?? "peek" : "peek";
                    var peekWords = root.TryGetProperty("words", out var pw) && pw.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? pw.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList()
                        : new List<string> { root.TryGetProperty("word", out var p1) ? p1.GetString() ?? "" : "" };
                    _ = PeekObjectsAsync(peekWords, peekMode);
                }
                break;

            case "open-proc":
                var procName = root.GetProperty("word").GetString();
                if (!string.IsNullOrWhiteSpace(procName))
                {
                    var currentSql = await GetScriptTextAsync();
                    OpenProcedureWithQueryRequested?.Invoke(procName, UseSysDatabase, currentSql);
                }
                break;

            // GỢI Ý GHOST TEXT COPILOT
            // Ghost text theo cấu trúc Fast (không AI): trang hỏi trước khi gọi AI, trả rỗng = không có luật nào khớp.
            case "fast-ghost":
            {
                var fid = root.GetProperty("requestId").GetInt32();
                // long = gọi tay sau khi đổi chế độ dài ⇄ ngắn (chạy cả khi đã tắt ghost Fast tự động); chế độ lấy theo Tùy chọn
                var manual = root.TryGetProperty("long", out var lp) && lp.ValueKind == System.Text.Json.JsonValueKind.True;
                string? ghost = null;
                if (_settings.EnableFastGhost || manual)
                {
                    var types = FastGhostRules.LoadColumnTypes(GhostDbKey, () => _service.CreateConnection(UseSysDatabase));
                    var (ft, fo) = (root.GetProperty("text").GetString() ?? "", root.GetProperty("offset").GetInt32());
                    try { ghost = FastGhostRules.SuggestByMode(_settings.FastGhostLong, ft, fo, types, FastGhostRules.KnowledgeFor(GhostDbKey)); }
                    catch { /* luật lỗi thì coi như không gợi ý */ }
                }
                _ = EdExec($"window.fastGhostDone && window.fastGhostDone({fid}, {System.Text.Json.JsonSerializer.Serialize(ghost ?? "")})");
                break;
            }

            case "copilot-suggest":
                var reqId = root.GetProperty("requestId").GetInt32();
                var prefix = root.GetProperty("prefix").GetString() ?? "";
                var suffix = root.TryGetProperty("suffix", out var sProp) ? sProp.GetString() ?? "" : "";
                _ = HandleCopilotSuggestAsync(reqId, prefix, suffix);
                break;

            case "toggle-results": BeginInvoke(new Action(ToggleResultPanel)); break;

            // Ctrl+lăn chuột / Tăng-Giảm cỡ chữ / Ctrl+0 (0 = về mặc định FCode) — lưu settings.json, các tab SQL khác theo luôn.
            case "font-size":
                SaveEditorFontSize(root.GetProperty("size").GetDouble());
                break;

            // Editor báo mức phóng to hiện tại (%) → hiện ở ô % trên thanh Execute, cùng hàng với "Tùy chọn".
            case "zoom-show":
                _ = BarExec($"window.showZoom && window.showZoom({root.GetProperty("pct").GetInt32()})");
                break;

            case "global-key":
                var k = root.GetProperty("key").GetString();
                if (!string.IsNullOrEmpty(k) && Enum.TryParse<Keys>(k, true, out var parsedKey))
                {
                    var combinedKey = parsedKey | Keys.Control | Keys.Shift;
                    this.BeginInvoke(() =>
                    {
                        if (this.FindForm() is MainForm mainForm)
                        {
                            mainForm.HandleGlobalShortcut(combinedKey);
                        }
                    });
                }
                break;
        }

    }

    // ---------------- Cỡ chữ editor (lưu settings.json, dùng chung mọi tab SQL Query) ----------------

    private static double? s_editorFontSize;
    private static event Action<double, RawSqlControl>? EditorFontSizeChanged;

    /// <summary>Cỡ chữ đã lưu (0 = mặc định FCode) — đọc settings.json 1 lần rồi giữ trong bộ nhớ, vì _settings của
    /// từng tab được nạp lúc tab mở nên có thể cũ hơn cỡ chữ vừa đổi ở tab khác.</summary>
    private static double EditorFontSize => s_editorFontSize ??= AppSettings.Load().SqlEditorFontSize;

    private void PushEditorFontSize()
    {
        if (!EdAlive) return;
        var size = EditorFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ = EdExec($"window.setUserFontSize && window.setUserFontSize({size})");
    }

    private void SaveEditorFontSize(double size)
    {
        size = size > 0 ? Math.Clamp(size, 8, 72) : 0;
        if (s_editorFontSize == size) return;
        s_editorFontSize = size;
        _settings.SqlEditorFontSize = size;
        try
        {
            // Nạp lại bản mới nhất rồi mới ghi, để không đè mất thay đổi settings khác từ lúc tab này mở.
            var fresh = AppSettings.Load();
            fresh.SqlEditorFontSize = size;
            fresh.Save();
        }
        catch
        {
            // Không ghi được settings.json — cỡ chữ vẫn áp cho phiên hiện tại.
        }
        EditorFontSizeChanged?.Invoke(size, this);
    }

    private void OnEditorFontSizeChanged(double size, RawSqlControl source)
    {
        if (ReferenceEquals(source, this) || IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(PushEditorFontSize));
    }

    private void PushThemeToAll()
    {
        var isDark = Bcode.App.UI.AppColors.IsDark ? "true" : "false";
        if (BarAlive)
            _ = BarExec($"window.setTheme && window.setTheme({isDark})");
        if (EdAlive)
        {
            // Theme/màu người dùng chọn (UiThemes) cũng áp cho Monaco; chưa đổi gì thì null → vs / vs-dark mặc định.
            var pal = Bcode.App.UI.UiTemplate.Current.IsPaletteCustomized
                ? System.Text.Json.JsonSerializer.Serialize(new
                {
                    panel = Bcode.App.UI.UiThemes.Hex(Bcode.App.UI.AppColors.Panel), panelAlt = Bcode.App.UI.UiThemes.Hex(Bcode.App.UI.AppColors.PanelAlt),
                    text = Bcode.App.UI.UiThemes.Hex(Bcode.App.UI.AppColors.Text), textMuted = Bcode.App.UI.UiThemes.Hex(Bcode.App.UI.AppColors.TextMuted),
                    accent = Bcode.App.UI.UiThemes.Hex(Bcode.App.UI.AppColors.Accent), selection = Bcode.App.UI.UiThemes.Hex(Bcode.App.UI.AppColors.Selection),
                    border = Bcode.App.UI.UiThemes.Hex(Bcode.App.UI.AppColors.Border), input = Bcode.App.UI.UiThemes.Hex(Bcode.App.UI.AppColors.Input),
                })
                : "null";
            _ = EdExec($"window.setPalette ? window.setPalette({pal}, {isDark}) : (window.setTheme && window.setTheme({isDark}))");
            // Cỡ chữ mặc định của editor theo "Font gốc / Cỡ (pt)" của Template (9,5pt = cỡ cũ): đổi cỡ gốc thì editor SQL đổi theo, không còn đứng yên.
            var scale = Bcode.App.UI.UiTemplate.Current.FontSize / Bcode.App.UI.UiTemplate.DefaultFontSize;
            _ = EdExec($"window.setEditorScale && window.setEditorScale({scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, {System.Text.Json.JsonSerializer.Serialize(Bcode.App.UI.UiTemplate.Current.FontFamily.Equals(Bcode.App.UI.UiTemplate.DefaultFontFamily, StringComparison.OrdinalIgnoreCase) ? "" : Bcode.App.UI.UiTemplate.Current.FontFamily)})");
            PushEditorFontSize(); // trước setEditorStyle: nó lấy cỡ chữ đã lưu làm mặc định khi Template không quy định cỡ chữ
            // Khu vực "Vùng soạn thảo SQL" của Template giao diện: font / cỡ / đậm / màu chữ / màu nền của Monaco (null = như cũ).
            var edCss = Bcode.App.UI.UiTemplate.AreaStyle("editor") is { } edStyle ? Bcode.App.UI.UiTemplate.ToInlineCss(edStyle) : null;
            _ = EdExec($"window.setEditorStyle && window.setEditorStyle({System.Text.Json.JsonSerializer.Serialize(edCss)})");
            var ut = Bcode.App.UI.UiTemplate.Current;
            var edOpts = System.Text.Json.JsonSerializer.Serialize(new { lineSpacing = ut.EditorLineSpacing, minimap = ut.EditorMinimap, lineNumbers = ut.EditorLineNumbers, whitespace = ut.EditorWhitespace });
            _ = EdExec($"window.setEditorOptions && window.setEditorOptions({edOpts})");
        }
    }

    // ---------------- Tương tác Copilot Inline ----------------

    private async Task HandleCopilotSuggestAsync(int reqId, string prefix, string suffix)
        {
            var suggestion = _running ? "" : await QueryCopilotAiAsync(prefix, suffix);
            if (!EdAlive) return;

            this.BeginInvoke(() =>
            {
                if (EdAlive)
                {
                    var serialized = System.Text.Json.JsonSerializer.Serialize(suggestion);
                    _ = EdExec(
                        $"window.setCopilotSuggestion && window.setCopilotSuggestion({reqId}, {serialized});");
                }
            });
        }

    // ---------------- Thông tin bảng database cho AI ----------------

    private static readonly Dictionary<string, string> _schemaCache = new();

    private const string FastHint =
        "Context: FastBusiness (Fast) ERP database on SQL Server. Table and column names are lowercase Vietnamese abbreviations " +
        "(e.g. dmkh = customer list, dmvt = item list, ma_kh, ten_kh, ma_vt, stt_rec, ngay_ct, so_ct, ma_dvcs, ma_nt, tien_nt, tien, so_luong); " +
        "voucher data is split into master (m..$yyyymm / m21$000000), detail (d..$) and period tables. " +
        "Only use tables and columns that appear in the schema below when it is given; if something is not listed, do not invent it.";

    /// <summary>
    /// Cấu trúc (cột, kiểu, khoá chính) của các bảng đang được dùng trong script — chỉ những bảng xuất hiện sau
    /// FROM/JOIN/UPDATE/INTO (tối đa 8) chứ không gửi cả database. Lấy từ sys.columns của Sys/App Data đang chọn,
    /// nhớ lại theo từng bảng để lần gợi ý sau không phải truy vấn lại. Lỗi/không có bảng → bỏ qua, AI vẫn chạy được.
    /// </summary>
    /// <summary>
    /// Gợi ý chèn vào giữa dòng hay lặp lại đúng phần chữ đã có phía sau con trỏ ("ELSE| RTRIM(status) END WHERE..." → gợi ý
    /// "RTRIM(status) END WHERE flag = 1" nên hiện thành chữ xám nối đuôi chữ cũ, vô nghĩa). Cắt phần gợi ý trùng với đầu
    /// phần còn lại của file; nếu gợi ý chỉ toàn là phần đó thì bỏ luôn. Cũng thêm 1 khoảng trắng nếu gợi ý dính liền chữ vừa gõ.
    /// </summary>
    private static string TrimSuggestionOverlap(string text, string prefix, string suffix)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var rest = suffix.TrimStart(' ', '\t');
        var restLine = rest.Split('\n')[0].TrimEnd('\r');

        if (restLine.Length > 0)
        {
            // Gợi ý bắt đầu bằng đúng phần còn lại của dòng → không có gì mới để chèn.
            if (text.TrimStart().StartsWith(restLine.Trim(), StringComparison.OrdinalIgnoreCase)) return "";
            // Đuôi gợi ý trùng đầu phần còn lại của dòng → cắt đuôi đó.
            var t = restLine.Trim();
            var cut = text.TrimEnd().LastIndexOf(t, StringComparison.OrdinalIgnoreCase);
            if (cut >= 0 && cut + t.Length == text.TrimEnd().Length) text = text[..cut];
        }

        // Trùng nhiều dòng: đuôi gợi ý trùng với phần đầu của suffix (tối thiểu 4 ký tự).
        var tail = text.TrimEnd();
        for (var len = Math.Min(tail.Length, rest.Length); len >= 4; len--)
        {
            if (string.Compare(tail, tail.Length - len, rest, 0, len, StringComparison.OrdinalIgnoreCase) == 0)
            {
                text = tail[..^len];
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(text)) return "";
        // "ELSE" + "RTRIM(...)" dính liền → thêm khoảng trắng.
        if (prefix.Length > 0 && char.IsLetterOrDigit(prefix[^1]) && char.IsLetterOrDigit(text[0])) text = " " + text;
        return text;
    }

    private async Task<string> BuildSchemaContextAsync(string sql)
    {
        try
        {
            var names = TableRefRegex.Matches(sql)
                .Select(m => m.Groups[1].Value.Replace("[", "").Replace("]", ""))
                .Where(n => n.Length > 0 && !n.StartsWith('#') && !n.StartsWith('@'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
            if (names.Count == 0) return "";

            var useSys = UseSysDatabase;
            var sb = new System.Text.StringBuilder();
            foreach (var name in names)
            {
                var key = $"{useSys}|{name.ToLowerInvariant()}";
                string? line;
                lock (_schemaCache) _schemaCache.TryGetValue(key, out line);
                if (line is null)
                {
                    line = await ReadTableSchemaAsync(useSys, name);
                    lock (_schemaCache) _schemaCache[key] = line;
                }
                if (line.Length > 0) sb.AppendLine(line);
            }
            return sb.ToString();
        }
        catch { return ""; }
    }

    private async Task<string> ReadTableSchemaAsync(bool useSys, string table)
    {
        try
        {
            await using var conn = _service.CreateConnection(useSys);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(@"
SELECT c.name, ty.name, c.max_length, c.precision, c.scale,
       CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                         WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END
FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(@n) ORDER BY c.column_id", conn);
            cmd.Parameters.AddWithValue("@n", table);
            await using var r = await cmd.ExecuteReaderAsync();

            var cols = new List<string>();
            while (await r.ReadAsync() && cols.Count < 80)
            {
                var type = r.GetString(1);
                var len = r.GetInt16(2);
                var typeText = type.ToLowerInvariant() switch
                {
                    "varchar" or "char" => $"{type}({(len < 0 ? "max" : len.ToString())})",
                    "nvarchar" or "nchar" => $"{type}({(len < 0 ? "max" : (len / 2).ToString())})",
                    "decimal" or "numeric" => $"{type}({r.GetByte(3)},{r.GetByte(4)})",
                    _ => type,
                };
                cols.Add($"{r.GetString(0)} {typeText}{(r.GetInt32(5) == 1 ? " PK" : "")}");
            }
            return cols.Count == 0 ? "" : $"{table}({string.Join(", ", cols)})";
        }
        catch { return ""; }
    }

    /// <summary>Dòng đang gõ (đoạn cuối của prefix) — để nhận ra gợi ý trong Lịch sử gợi ý AI.</summary>
    private static string CurrentLineOf(string prefix)
    {
        var line = prefix.Split('\n')[^1].Trim();
        return line.Length > 300 ? line[^300..] : line;
    }

    /// <summary>Ctrl+I trong SQL Query: sửa đoạn đang chọn (hoặc sinh SQL tại con trỏ) theo yêu cầu, có kèm cấu trúc bảng.
    /// Dùng Claude — cần Anthropic API key (menu ⚙). Kết quả trả về trang qua window.setAiEditResult.</summary>
    private async Task HandleAiEditAsync(int requestId, string instruction, string selection, string script)
    {
        string text = "", error = "";
        try
        {
            // Chọn engine: theo "Dùng Claude / Gemini cho gợi ý SQL" (CopilotEngine); engine được chọn chưa có key mà engine kia có thì dùng engine kia.
            var hasClaude = !string.IsNullOrWhiteSpace(_settings.AnthropicApiKey);
            var hasGemini = !string.IsNullOrWhiteSpace(_settings.GeminiApiKey);
            if (!hasClaude && !hasGemini)
                throw new InvalidOperationException("Chưa có API key — vào menu ⚙ > cấu hình Claude (Anthropic) hoặc Gemini API Key.");
            var useGemini = hasGemini && (!hasClaude || !UseClaudeEngine);

            var schema = await BuildSchemaContextAsync(script + "\n" + selection);
            var system =
                "You are an expert T-SQL developer working inside an editor for SQL Server scripts. " + FastHint + "\n" +
                (selection.Length > 0
                    ? "The user selected part of their script and wants it changed. Reply with ONLY the full replacement for the selected part."
                    : "The user wants new SQL inserted at the caret. Reply with ONLY the SQL to insert.") +
                " No markdown fences, no explanations; keep the user's style and keep unchanged parts as they are." +
                (schema.Length > 0 ? "\n\nSchema of tables used in the script:\n" + schema : "");

            var script2 = script.Length > 30000 ? script[..30000] + "\n-- ...(truncated)" : script;
            var user = $"--- FULL SCRIPT ---\n{script2}\n\n" +
                       (selection.Length > 0 ? $"--- SELECTED PART (to be replaced) ---\n{selection}\n\n" : "") +
                       $"--- REQUEST ---\n{instruction}";

            text = useGemini ? await CallGeminiEditAsync(system, user) : await CallClaudeEditAsync(system, user);
            text = Regex.Replace(text.Trim(), @"^```[\w-]*\r?\n|\r?\n?```\s*$", "");
            // Lưu lại ngay khi có kết quả — đóng hộp thoại mà chưa Insert vẫn lấy lại được ở "Lịch sử gợi ý AI".
            AiHistoryStore.Add("Ctrl+I", useGemini ? "Gemini" : "Claude", instruction, selection.Length > 300 ? selection[..300] + "…" : selection, text);
        }
        catch (TaskCanceledException) { error = "Hết thời gian chờ AI (90s)."; }
        catch (Exception ex) { error = ex.Message; }

        this.BeginInvoke(() =>
        {
            if (!EdAlive) return;
            _ = EdExec(
                $"window.setAiEditResult && window.setAiEditResult({requestId}, {System.Text.Json.JsonSerializer.Serialize(text)}, {System.Text.Json.JsonSerializer.Serialize(error)});");
        });
    }

    /// <summary>Ctrl+I bằng Claude (Anthropic Messages API) — gọi qua <see cref="AiChatService"/> dùng chung (hẹn giờ 90s).</summary>
    private Task<string> CallClaudeEditAsync(string system, string user) =>
        AiChatService.ClaudeAsync(_settings.AnthropicApiKey, _settings.ClaudeEditModel, system, user, maxTokens: 4096);

    /// <summary>Ctrl+I bằng Gemini (generateContent) — cùng prompt với bản Claude; system prompt đi qua systemInstruction.</summary>
    private Task<string> CallGeminiEditAsync(string system, string user) =>
        AiChatService.GeminiAsync(_settings.GeminiApiKey, system, user, maxTokens: 8192);

    /// <summary>Hiện tab: lấy lại tuỳ chọn gợi ý / engine từ settings.json (tab khác có thể vừa đổi) rồi báo trang editor.</summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible || !EdAlive) return;
        _settings.SyncEditorFlagsFromDisk();
        PushCopilotAuto();
    }

    private void PushCopilotAuto()
    {
        if (!EdAlive) return;
        _ = EdExec($"window.setCopilotAuto && window.setCopilotAuto({(_settings.EnableCopilotSuggest ? "true" : "false")}); window.fastGhost = {(_settings.EnableFastGhost ? "true" : "false")};");
    }

    private bool UseClaudeEngine =>
        string.Equals(_settings?.CopilotEngine, "claude", StringComparison.OrdinalIgnoreCase);

    /// <summary>Đổi ghost Fast dài ⇄ ngắn (lưu Tùy chọn cho mọi tab), báo ở dòng trạng thái rồi gợi ý lại ngay theo chế độ mới.</summary>
    private void ToggleFastGhostLong()
    {
        _settings.FastGhostLong = AppSettings.Update(s => s.FastGhostLong = !s.FastGhostLong).FastGhostLong;
        SetCopilotStatus(_settings.FastGhostLong ? "Gợi ý Fast: DÀI (cả dòng theo chính file)" : "Gợi ý Fast: NGẮN (chỉ gợi ý chắc ≥ 90%)", Color.SteelBlue);
        if (EdAlive) _ = EdExec("editor && editor.focus(); window._fastLong = true; editor && editor.trigger('menu', 'editor.action.inlineSuggest.trigger', {})");
    }

    private void SetCopilotStatus(string text, Color color)
    {
        this.BeginInvoke(() => { if (_running) return; _statusLabel.ForeColor = color; _statusLabel.Text = text; });
    }

    /// <summary>
    /// Gợi ý inline bằng Claude (Anthropic Messages API): cùng ý tưởng fill-in-the-middle như bản Gemini — đưa CẢ code trước
    /// và sau con trỏ để model đọc được tham số, biến, bảng tạm và không viết lặp phần đã có phía dưới. Không stream, chỉ lấy
    /// vài dòng; nhận lỗi 429/529 thì tự tạm dừng như bản Gemini.
    /// </summary>
    private async Task<string> QueryClaudeCopilotAsync(string prefix, string suffix)
    {
        var apiKey = _settings?.AnthropicApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return "";

        if (DateTime.Now < _rateLimitCooldownUntil)
        {
            var remaining = (int)(_rateLimitCooldownUntil - DateTime.Now).TotalSeconds;
            SetCopilotStatus($"Copilot: Tạm dừng {remaining}s để hồi quota...", Color.OrangeRed);
            return "";
        }

        try
        {
            SetCopilotStatus("Copilot (Claude): Đang phân tích SQL...", Color.DimGray);
            var lastWordMatch = Regex.Match(prefix, @"[@#\w$]+$", RegexOptions.RightToLeft);
            var lastWord = lastWordMatch.Success ? lastWordMatch.Value : "";

            var schema = await BuildSchemaContextAsync(prefix + "\n" + suffix);

            // Còn chữ phía sau con trỏ trên CÙNG dòng = đang sửa giữa dòng: chỉ gợi ý đoạn ngắn chèn vào giữa, không viết lại dòng.
            var restOfLine = suffix.Split('\n')[0].Trim();
            var midLine = restOfLine.Length > 0;
            var system =
                "You are an expert inline T-SQL autocomplete engine for SQL Server (stored procedures, functions, scripts in the " +
                "FastBusiness ERP codebase). The developer is editing at the position marked /* [CURSOR] */.\n" + FastHint + "\n" +
                "Rules: 1) Continue seamlessly from the cursor with the exact next lines needed. " +
                "2) Use the parameters, declared variables and temp tables visible in the code before the cursor. " +
                "3) Read the code after the cursor: never duplicate it and never close a block that is already closed there. " +
                "4) Reply with ONLY the raw SQL to insert — no markdown fences, no explanation, and do not repeat the last typed word. " +
                "5) Stay relevant to the statement the cursor is in (its tables, aliases, columns, the CASE/JOIN/WHERE being written); never start a new unrelated statement." +
                (midLine
                    ? $" 6) The cursor is in the MIDDLE of a line: this text already follows it on the same line and must NOT be repeated: [{restOfLine}]. Suggest only the short piece (usually a few words) that fits between the cursor and that text."
                    : "") +
                (schema.Length > 0 ? "\n\nSchema of tables used in the script:\n" + schema : "");

            var payload = new
            {
                model = string.IsNullOrWhiteSpace(_settings!.ClaudeModel) ? "claude-haiku-4-5-20251001" : _settings.ClaudeModel,
                max_tokens = midLine ? 100 : 400,
                temperature = 0.1,
                system,
                messages = new[]
                {
                    new { role = "user", content = $"--- CODE BEFORE CURSOR ---\n{prefix}\n/* [CURSOR] */\n--- CODE AFTER CURSOR ---\n{suffix}" },
                },
                // Claude từ chối stop sequence chỉ gồm khoảng trắng (như "\n\n\n" của bản Gemini) → chỉ giữ dòng GO.
                stop_sequences = new[] { "\nGO\n", "\nGO\r\n" },
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

            var res = await _aiHttpClient.SendAsync(req);
            var body = await res.Content.ReadAsStringAsync();

            if ((int)res.StatusCode is 429 or 529)
            {
                _rateLimitCooldownUntil = DateTime.Now.AddSeconds((int)res.StatusCode == 429 ? 30 : 10);
                SetCopilotStatus($"Copilot lỗi ({(int)res.StatusCode}): quá tải/hạn mức — tạm dừng ít giây.", Color.Firebrick);
                return "";
            }
            if (!res.IsSuccessStatusCode)
            {
                var detail = res.ReasonPhrase ?? "Error";
                try
                {
                    using var err = System.Text.Json.JsonDocument.Parse(body);
                    if (err.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
                        detail = m.GetString() ?? detail;
                }
                catch { }
                SetCopilotStatus($"Copilot (Claude) lỗi ({(int)res.StatusCode}): {detail}", Color.Firebrick);
                return "";
            }

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var text = "";
            if (doc.RootElement.TryGetProperty("content", out var blocks))
                foreach (var b in blocks.EnumerateArray())
                    if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var tx))
                        text += tx.GetString();

            text = text.Replace("```sql", "").Replace("```", "").TrimStart('\r', '\n');
            if (!string.IsNullOrEmpty(lastWord) && text.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase))
                text = text.Substring(lastWord.Length);
            text = TrimSuggestionOverlap(text, prefix, suffix);

            if (!string.IsNullOrWhiteSpace(text))
            {
                AiHistoryStore.Add("Ghost", "Claude", "", CurrentLineOf(prefix), text);
                SetCopilotStatus("Copilot (Claude): Đã có gợi ý (bấm Tab để nhận)", Color.DarkGreen);
                return text;
            }
            SetCopilotStatus("Copilot: Không có gợi ý phù hợp", Color.DimGray);
            return "";
        }
        catch (Exception ex)
        {
            SetCopilotStatus("Copilot (Claude): " + ex.Message, Color.Firebrick);
            return "";
        }
    }

    private async Task<string> QueryCopilotAiAsync(string prefix, string suffix)
    {
        if (UseClaudeEngine) return await QueryClaudeCopilotAsync(prefix, suffix);

        var apiKey = _settings?.GeminiApiKey;
        if (string.IsNullOrWhiteSpace(apiKey)) return "";

        if (DateTime.Now < _rateLimitCooldownUntil)
        {
            var remaining = (int)(_rateLimitCooldownUntil - DateTime.Now).TotalSeconds;
            this.BeginInvoke(() =>
            {
                _statusLabel.ForeColor = Color.OrangeRed;
                if (!_running) _statusLabel.Text = $"Copilot: Tạm dừng {remaining}s để hồi quota (tránh spam 429)...";
            });
            return "";
        }

        try
        {
            this.BeginInvoke(() =>
            {
                _statusLabel.ForeColor = Color.DimGray;
                if (!_running) _statusLabel.Text = "Copilot: Đang phân tích Procedure...";
            });

            var lastWordMatch = Regex.Match(prefix, @"[@#\w$]+$", RegexOptions.RightToLeft);
            var lastWord = lastWordMatch.Success ? lastWordMatch.Value : "";

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent?key={apiKey}";

            // Prompt FIM (Fill-In-The-Middle): Đưa cả code trước và code sau vào để AI điền vào giữa
            var promptText = $@"You are an expert inline T-SQL autocomplete engine for SQL Server Stored Procedures and Functions.
                            The developer is currently editing the procedure at the position marked by /* [CURSOR] */.

                            Requirements:
                            1. Continue seamlessly from the cursor. Predict the exact next lines of code needed.
                            2. Read the parameters, declared variables, and temp tables in CODE BEFORE CURSOR.
                            3. Check CODE AFTER CURSOR carefully: do NOT duplicate any code, do NOT close blocks prematurely if they are already closed after cursor.
                            4. Return ONLY the raw SQL code to insert at /* [CURSOR] */. Do NOT output markdown code blocks (```). Do NOT repeat the last word.

                            --- CODE BEFORE CURSOR ---
                            {prefix}
                            /* [CURSOR] */
                            --- CODE AFTER CURSOR ---
                            {suffix}";

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[] { new { text = promptText } }
                    }
                },
                generationConfig = new
                {
                    temperature = 0.1,
                    maxOutputTokens = 1024,
                    thinkingConfig = new { thinkingBudget = 0 },
                    stopSequences = new[] { "\n\n\n", "GO\r\n", "GO\n" }
                }
            };

            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            var res = await _aiHttpClient.PostAsync(url, content);
            var respBody = await res.Content.ReadAsStringAsync();

            if ((int)res.StatusCode == 429)
            {
                _rateLimitCooldownUntil = DateTime.Now.AddSeconds(30);
                this.BeginInvoke(() =>
                {
                    _statusLabel.ForeColor = Color.Firebrick;
                    if (!_running) _statusLabel.Text = "Copilot lỗi (429): Quá hạn mức request. Tự động tạm dừng 30 giây.";
                });
                return "";
            }
            if ((int)res.StatusCode == 503)
            {
                _rateLimitCooldownUntil = DateTime.Now.AddSeconds(10);
                this.BeginInvoke(() =>
                {
                    _statusLabel.ForeColor = Color.OrangeRed;
                    if (!_running) _statusLabel.Text = "Copilot: Server Gemini đang quá tải, tạm dừng 10 giây...";
                });
                return "";
            }

            if (!res.IsSuccessStatusCode)
            {
                string errorDetail = res.ReasonPhrase ?? "Error";
                try
                {
                    using var errDoc = System.Text.Json.JsonDocument.Parse(respBody);
                    if (errDoc.RootElement.TryGetProperty("error", out var errObj) &&
                        errObj.TryGetProperty("message", out var msgObj))
                    {
                        errorDetail = msgObj.GetString() ?? errorDetail;
                    }
                }
                catch { }

                this.BeginInvoke(() =>
                {
                    _statusLabel.ForeColor = Color.Firebrick;
                    if (!_running) _statusLabel.Text = $"Copilot lỗi ({(int)res.StatusCode}): {errorDetail}";
                });
                return "";
            }

            using var doc = System.Text.Json.JsonDocument.Parse(respBody);
            if (doc.RootElement.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var first = candidates[0];
                if (first.TryGetProperty("content", out var contentElem) &&
                    contentElem.TryGetProperty("parts", out var parts) &&
                    parts.GetArrayLength() > 0)
                {
                    var text = parts[0].GetProperty("text").GetString() ?? "";
                    text = text.Replace("```sql", "").Replace("```", "").TrimStart('\r', '\n');

                    // Khử lặp từ đang gõ dở
                    if (!string.IsNullOrEmpty(lastWord) && text.StartsWith(lastWord, StringComparison.OrdinalIgnoreCase))
                    {
                        text = text.Substring(lastWord.Length);
                    }
                    text = TrimSuggestionOverlap(text, prefix, suffix);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        AiHistoryStore.Add("Ghost", "Gemini", "", CurrentLineOf(prefix), text);
                        this.BeginInvoke(() =>
                        {
                            _statusLabel.ForeColor = Color.DarkGreen;
                            if (!_running) _statusLabel.Text = "Copilot: Đã có gợi ý (bấm Tab để nhận)";
                        });
                        return text;
                    }
                }
            }

            this.BeginInvoke(() =>
            {
                _statusLabel.ForeColor = Color.DimGray;
                if (!_running) _statusLabel.Text = "Copilot: Không có gợi ý phù hợp";
            });

            return "";
        }
        catch (Exception ex)
        {
            this.BeginInvoke(() =>
            {
                _statusLabel.ForeColor = Color.Firebrick;
                if (!_running) _statusLabel.Text = "Copilot: " + ex.Message;
            });
            return "";
        }
    }

    // ---------------- Tương tác trực tiếp với Monaco Editor ----------------

    public async Task<string> GetScriptTextAsync()
    {
        if (!_editorReady || !EdAlive) return "";
        var json = await EdExec("window.getEditorText()");
        return System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
    }

    /// <summary>Số phiên bản nội dung editor (tăng mỗi lần sửa) — rất nhẹ so với lấy cả script.</summary>
    public async Task<long> GetEditorVersionAsync()
    {
        if (!_editorReady || !EdAlive) return -1;
        var json = await EdExec("window.getEditorVersion ? window.getEditorVersion() : -1");
        return long.TryParse(json, out var v) ? v : -1;
    }

    public async Task<string> GetSelectedTextAsync()
    {
        if (!_editorReady || !EdAlive) return "";
        var json = await EdExec("window.getSelectedText()");
        return System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
    }

    public async Task SetScriptTextAsync(string text)
    {
        if (!_editorReady || !EdAlive)
        {
            _pendingScriptText = text;
            return;
        }
        var jsonText = System.Text.Json.JsonSerializer.Serialize(text);
        await EdExec($"window.setEditorText({jsonText})");
    }

    public void SetScriptText(string text)
    {
        _ = SetScriptTextAsync(text);
    }

    /// <summary>Chèn định nghĩa procedure/function lên đầu script đang gõ (kèm GO) và nhảy lên đó — dùng cho Ctrl + chuột phải.
    /// Đã có sẵn trong script thì chỉ nhảy tới, không chèn lặp (xem window.prependDefinition trong sqleditor.html).</summary>
    public async Task PrependDefinitionAsync(string definition)
    {
        if (!_editorReady || !EdAlive) return;
        var json = System.Text.Json.JsonSerializer.Serialize(definition);
        await EdExec($"window.prependDefinition({json})");
        FocusEditor();
    }

    private async Task InsertTextAtCaretAsync(string text)
    {
        if (!EdAlive) return;
        var jsonText = System.Text.Json.JsonSerializer.Serialize(text);
        await EdExec($"window.replaceSelection({jsonText})");
    }

    private void _scriptBoxWordWrapToggle(bool wrap)
    {
        _wordWrap = wrap;
        var wrapStr = wrap ? "true" : "false";

        if (EdAlive)
            _ = EdExec($"window.setWordWrap({wrapStr})");

        if (BarAlive)
            _ = BarExec($"var chk = document.getElementById('chkWordWrap'); if(chk) chk.checked = {wrapStr};");
    }

    private WebMenu BuildOptionsMenu()
    {
        // tab giữ bản settings riêng → lấy trạng thái mới nhất trước khi vẽ dấu tick (tab khác có thể vừa đổi)
        _settings.SyncEditorFlagsFromDisk();
        return new WebMenu()
        .Add("Word Wrap", () => _scriptBoxWordWrapToggle(!_wordWrap), @checked: _wordWrap)
        .AddCaption("Copilot / AI")
        .Add("Cấu hình Gemini API Key...", () =>
        {
            var currentKey = _settings?.GeminiApiKey ?? "";
            var key = SimplePromptForm.Show(this, "Gemini API Key", "Nhập Google Gemini API Key:", currentKey);
            if (key is not null && _settings is not null)
            {
                _settings.GeminiApiKey = AppSettings.Update(s => s.GeminiApiKey = key.Trim()).GeminiApiKey;
                MessageBox.Show(this, "Đã lưu Gemini API Key thành công!", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        })
        .Add("Cấu hình Claude (Anthropic) API Key...", () =>
        {
            var key = SimplePromptForm.Show(this, "Claude API Key", "Nhập Anthropic API Key (sk-ant-...):", _settings?.AnthropicApiKey ?? "");
            if (key is not null && _settings is not null)
            {
                // Có key rồi thì dùng luôn Claude cho gợi ý SQL; đổi lại Gemini ở mục bên dưới.
                var saved = AppSettings.Update(s => { s.AnthropicApiKey = key.Trim(); if (s.AnthropicApiKey.Length > 0) s.CopilotEngine = "claude"; });
                (_settings.AnthropicApiKey, _settings.CopilotEngine) = (saved.AnthropicApiKey, saved.CopilotEngine);
                MessageBox.Show(this, "Đã lưu Claude API Key. Gợi ý SQL sẽ dùng Claude.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        })
        .Add("Lịch sử sửa procedure/function...", () => BeginInvoke(new Action(OpenSqlHistory)))
        .Add("Lịch sử gợi ý AI...", () =>
        {
            using var form = new AiHistoryForm(InsertTextAtCaretAsync);
            form.ShowDialog(this);
        })
        .Add("Gợi ý theo cấu trúc Fast khi gõ (không AI)", () =>
        {
            _settings.EnableFastGhost = AppSettings.Update(s => s.EnableFastGhost = !s.EnableFastGhost).EnableFastGhost;
            PushCopilotAuto();
        }, @checked: _settings.EnableFastGhost)
        .Add("Gợi ý Fast dài (tắt = ngắn, chỉ gợi ý chắc ≥ 90%)", ToggleFastGhostLong, @checked: _settings.FastGhostLong, shortcut: ShortcutRegistry.Display("editor.fastLong"))
        .Add("Học gợi ý Fast từ database này (chỉ đọc)…", () => BeginInvoke(new Action(() => _ = FastGhostLearnUi.RunAsync(this, GhostDbKey, () => _service.CreateConnection(UseSysDatabase)))))
        .Add("Gợi ý tự động khi gõ", () =>
        {
            _settings.EnableCopilotSuggest = AppSettings.Update(s => s.EnableCopilotSuggest = !s.EnableCopilotSuggest).EnableCopilotSuggest;
            PushCopilotAuto();
        }, @checked: _settings.EnableCopilotSuggest)
        .Add("Gọi gợi ý ngay", () =>
        {
            if (EdAlive)
                _ = EdExec("editor && editor.focus(); editor && editor.trigger('menu', 'editor.action.inlineSuggest.trigger', {})");
        }, shortcut: "Alt+\\")
        .Add("AI sửa/sinh SQL...", () =>
        {
            if (EdAlive) _ = EdExec("window.showAiEdit && window.showAiEdit()");
        }, shortcut: "Ctrl+I")
        .Add("Dùng Claude cho gợi ý SQL", () => _settings.CopilotEngine = AppSettings.Update(s => s.CopilotEngine = "claude").CopilotEngine,
            @checked: UseClaudeEngine)
        .Add("Dùng Gemini cho gợi ý SQL", () => _settings.CopilotEngine = AppSettings.Update(s => s.CopilotEngine = "gemini").CopilotEngine,
            @checked: !UseClaudeEngine)
        .Add("Claude/Gemini nhúng vào tab SQL Query (tắt = tab riêng)", () => { AppSettings.AiEmbedded = !AppSettings.AiEmbedded; if (!AppSettings.AiEmbedded && IsAiShown) HideAi(); }, @checked: AppSettings.AiEmbedded)
        .Add("Gửi script sang Claude (web)", () => _ = SendToAiAsync("claude"))
        .Add("Gửi script sang Gemini (web)", () => _ = SendToAiAsync("gemini"))
        .AddCaption("Cỡ chữ")
        .Add("Tăng cỡ chữ", () => { if (EdAlive) _ = EdExec("window.setFontSize(1)"); })
        .Add("Giảm cỡ chữ", () => { if (EdAlive) _ = EdExec("window.setFontSize(-1)"); });
    }

    public event Action<DataTable>? ResultReady;
    /// <summary>Tab Pivot → "Tạo file Excel pivot…": mở Create RPT &amp; XML ở chế độ Pivot Excel với câu SQL vừa chạy.</summary>
    public event Action<string>? CreateRptRequested;
    /// <summary>Một lần Execute xong (kể cả lỗi): script, dùng Sys Data, thành công?, mili giây, số dòng kết quả — để ghi vào Lịch sử SQL.</summary>
    public event Action<string, bool, bool, int, int>? ScriptExecuted;
    /// <summary>Bấm "Lưu lịch sử" ở thanh Execute: script (phần chọn, không có thì cả script) + đang dùng Sys Data. Chỉ lưu khi người dùng bấm.</summary>
    public event Action<string, bool>? SaveHistoryRequested;
    /// <summary>Tên dự án (workspace) hiện tại — menu chuột phải chỉ hiện snippet chung + snippet của dự án này.</summary>
    public Func<string>? CurrentProject { get; set; }

    /// <summary>Khoá database cho ghost text Fast (kiểu cột + ma trận đã học): dự án + app/sys.</summary>
    private string GhostDbKey => (CurrentProject?.Invoke() ?? "") + (UseSysDatabase ? "|sys" : "|app");

    /// <summary>Chèn một đoạn (snippet) vào editor tại vị trí con trỏ.</summary>
    public Task InsertSnippetAsync(string text) => InsertTextAtCaretAsync(text);
    public event Action<List<DataTable>, string>? OpenResultInNewTabRequested;
    public event Action<string, bool, string>? OpenProcedureWithQueryRequested;

    /// <summary>Ctrl+F12 trên 1 object: (tên tab, có dùng Sys Data, nội dung) — MainForm mở tab SQL Query mới với nội dung đó.</summary>
    public event Action<string, bool, string>? OpenObjectInNewTabRequested;

    /// <summary>Nạp nội dung vào editor (tab mới mở: chờ editor sẵn sàng rồi mới nạp).</summary>
    public Task OpenScriptAsync(string text)
    {
        if (!_editorReady || !EdAlive) { _pendingScriptText = text; return Task.CompletedTask; }
        return SetScriptTextAsync(text);
    }

    /// <summary>F12 / Ctrl+F12 trên 1 hoặc NHIỀU tên object (bôi đen nhiều tên). Bảng → cấu trúc; procedure / function / view / trigger → nội dung. Tìm ở database đang chọn trước, không thấy thì database còn lại.
    /// F12 = 1 khung peek (nhiều object: tab / gộp), Ctrl+F12 = mỗi object 1 tab SQL Query mới.</summary>
    private async Task PeekObjectsAsync(List<string> words, string mode)
    {
        var found = new List<(SqlObjectInfo Obj, string Text)>(); var missing = new List<string>();
        try
        {
            _statusLabel.ForeColor = Color.DimGray;
            if (words.Count > 1) _statusLabel.Text = "F12: đang tải " + words.Count + " object…";
            // Mỗi tên tra + đọc nội dung ĐỒNG THỜI (tối đa 6 kết nối cùng lúc) thay vì lần lượt từng cái; kết quả giữ đúng thứ tự bôi đen.
            using var gate = new SemaphoreSlim(6);
            async Task<(string Raw, SqlObjectInfo? Obj, string Text, bool Skip)> One(string word)
            {
                var raw = (word ?? "").Trim().Replace("[", "").Replace("]", "").Trim('.');
                if (raw.Length == 0) return (raw, null, "", true);
                if (raw.StartsWith('#') || raw.StartsWith('@') || raw.Contains("..#")) return (raw + (words.Count == 1 ? " (bảng tạm / biến)" : ""), null, "", words.Count > 1);
                await gate.WaitAsync();
                try
                {
                    var parts = raw.Split('.');
                    var name = parts[^1]; var schema = parts.Length >= 2 ? parts[^2] : null;
                    SqlObjectInfo? hit = null;
                    foreach (var sys in new[] { UseSysDatabase, !UseSysDatabase })
                    {
                        var all = (await _sqlObjectService.ListObjectsAsync(sys, name)).Where(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                        if (all.Count == 0) continue;
                        hit = (schema is not null ? all.FirstOrDefault(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase)) : null) ?? all[0];
                        break;
                    }
                    if (hit is null) return (raw, null, "", false);
                    var text = hit.Kind == SqlObjectKind.Table ? await _sqlObjectService.GetTableStructureAsync(hit) : await _sqlObjectService.GetDefinitionAsync(hit);
                    return (raw, hit, text, false);
                }
                finally { gate.Release(); }
            }
            var results = await Task.WhenAll(words.Select(One));
            foreach (var r in results)
            {
                if (r.Skip) continue;
                if (r.Obj is null) { missing.Add(r.Raw); continue; }
                if (found.Any(f => f.Obj.FromSysDatabase == r.Obj.FromSysDatabase && string.Equals(f.Obj.QualifiedName, r.Obj.QualifiedName, StringComparison.OrdinalIgnoreCase))) continue;
                found.Add((r.Obj, r.Text));
            }
            if (words.Count > 1) _statusLabel.Text = "";
            if (found.Count == 0)
            {
                _statusLabel.ForeColor = Color.DarkOrange;
                _statusLabel.Text = "F12: không thấy object " + string.Join(", ", missing.Take(5)) + " (đã tìm cả App Data và Sys Data).";
                return;
            }
            if (mode == "open") { foreach (var (o, t) in found) OpenObjectInNewTabRequested?.Invoke(o.QualifiedName, o.FromSysDatabase, t); }
            else if (EdAlive)
            {
                var items = found.Select(f => new { title = f.Obj.QualifiedName + "   (" + f.Obj.Kind + (f.Obj.FromSysDatabase ? ", Sys Data" : ", App Data") + ")", text = f.Text }).ToList();
                await EdExec($"window.showPeekMulti && window.showPeekMulti({System.Text.Json.JsonSerializer.Serialize(items)})");
            }
            if (missing.Count > 0) { _statusLabel.ForeColor = Color.DarkOrange; _statusLabel.Text = "F12: không thấy " + string.Join(", ", missing.Take(5)) + (missing.Count > 5 ? "…" : "") + "."; }
        }
        catch (Exception ex) { _statusLabel.ForeColor = Color.DarkRed; _statusLabel.Text = "F12: " + ex.Message; }
    }
    public event Action<Bcode.App.Models.SqlObjectInfo, string?>? DebugTargetChosen;
    /// <summary>Gửi script (phần đang chọn, không có thì cả script) sang tab Claude/Gemini web: (engine "claude"|"gemini", text).</summary>
    public event Action<string, string>? AskAiRequested;

    public bool UseSysDatabase => _useSysDatabase;

    /// <summary>Đưa con trỏ về editor SQL (focus WebView2 + focus Monaco) — dùng khi focus vừa nằm ở thanh trên của cửa sổ chính.</summary>
    public void FocusEditor()
    {
        if (IsDisposed || !EdAlive) return;
        EdControl.Focus();
        _ = EdExec("window.editor && window.editor.focus && window.editor.focus()");
    }

    public void SetDatabase(bool useSysDatabase)
    {
        _useSysDatabase = useSysDatabase;
        DisposePersistentConnection();
        PushDatabaseToBar();
        if (!_deferTables) _ = LoadTablesForEditorAsync();
    }

    public void RefreshSchema()
    {
        lock (_schemaCache) _schemaCache.Clear();   // cấu trúc bảng gửi cho AI
        if (_deferTables || !_editorReady) return;   // tab dựng sẵn / editor chưa sẵn sàng: lúc dùng sẽ tự nạp
        PostToEditorUi(() => { if (EdAlive) _ = EdExec("window.BcodeHints && (BcodeHints.cols = {});"); });   // cột đã hỏi trong trang
        _ = LoadTablesForEditorAsync();   // danh sách bảng + chữ ký procedure/function
    }

    private void PushDatabaseToBar()
    {
        if (!BarAlive) return;
        var arg = System.Text.Json.JsonSerializer.Serialize(_useSysDatabase ? 1 : 0);
        _ = BarExec($"window.setDatabase && window.setDatabase({arg})");
    }

    // ---------------- Debug Store/Function ----------------

    private async Task PickDebugTargetAsync()
    {
        var scanner = new DebugTargetScanner(_sqlObjectService);
        var script = await GetScriptTextAsync();
        List<Bcode.App.Models.DebugCandidate> candidates;
        try
        {
            candidates = await scanner.ScanAsync(script, UseSysDatabase);
        }
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

    /// <summary>Bật/tắt 1 tuỳ chọn của thanh Execute (suggest, reset-conn, result-tab, debug-step) rồi báo lại thanh để nút hiện đúng trạng thái.</summary>
    private void ToggleOption(string which)
    {
        bool now;
        switch (which)
        {
            case "suggest": now = _suggestOn = !_suggestOn; break;
            case "reset-conn":
                now = _resetConnOn = !_resetConnOn;
                if (_resetConnOn) DisposePersistentConnection();
                break;
            case "result-tab": now = _resultTabOn = !_resultTabOn; break;
            case "debug-step":
                now = _debugStepOn = !_debugStepOn;
                if (!_debugStepOn) StopStepDebug();
                break;
            default: return;
        }
        SetBarToggle(which, now);
    }

    private async Task SaveToQueryHistoryAsync()
    {
        var script = await GetSelectedTextAsync();
        if (string.IsNullOrWhiteSpace(script)) script = await GetScriptTextAsync();
        if (string.IsNullOrWhiteSpace(script)) { _statusLabel.ForeColor = Color.DarkOrange; _statusLabel.Text = "Script trống — không có gì để lưu vào Lịch sử SQL."; return; }
        SaveHistoryRequested?.Invoke(script, UseSysDatabase);
        _statusLabel.ForeColor = AppColors.Success;
        _statusLabel.Text = "Đã lưu vào Lịch sử SQL (mở tab \"Lịch sử SQL\" để gắn nhãn / thẻ).";
    }

    /// <summary>Chạy 1 chức năng của thanh Execute từ phím tắt (editor gửi {action:"bar-action", name}) — cùng đường với bấm nút trên thanh.</summary>
    private void RunBarAction(string name)
    {
        switch (name)
        {
            case "open": OpenFile(); break;
            case "save-history": _ = SaveToQueryHistoryAsync(); break;
            case "save": SaveFile(); break;
            case "debug-target": _ = PickDebugTargetAsync(); break;
            case "write-schema": _ = WriteSchemaAsync(); break;
            case "check-fields": _ = CheckFieldsAsync(); break;
            case "comment": ToggleComment(true); break;
            case "uncomment": ToggleComment(false); break;
            case "suggest": case "reset-conn": case "result-tab": case "debug-step": ToggleOption(name); break;
            case "font-up": if (EdAlive) _ = EdExec("window.setFontSize(1)"); break;
            case "font-down": if (EdAlive) _ = EdExec("window.setFontSize(-1)"); break;
            case "options": BuildOptionsMenu().Show(BarControl, 10, BarHeight); break;
            case "db-app": SetDatabase(false); break;
            case var g when g.StartsWith("bar-group-", StringComparison.Ordinal) && int.TryParse(g["bar-group-".Length..], out var gi):
                if (BarAlive) _ = BarExec($"window.openGroup && window.openGroup({gi - 1})");
                break;
            case "db-sys": SetDatabase(true); break;
        }
    }

    // ---------------- Debug từng bước ----------------
    //
    // Phiên debug giữ 1 StepPlan (xem SqlStepPlanner): mỗi bước chạy script từ đầu tới hết dòng đích trong 1 transaction rồi ROLLBACK,
    // kèm SELECT các biến đã khai báo. Dòng "sắp chạy" được tô vàng trong editor; F10 = bước kế, F5/Execute = chạy tới breakpoint (chấm đỏ
    // ở lề, bấm vào lề để đặt/bỏ) hoặc hết, Ctrl+F10 = chạy tới dòng con trỏ, Shift+F5 = dừng.

    /// <summary>"Debug store/function": nạp định nghĩa store/function vào editor rồi bắt đầu debug từng bước ngay.</summary>
    public async Task LoadAndDebugAsync(string definition, string? callText)
    {
        StopStepDebug();
        if (!_editorReady || !EdAlive)
        {
            _pendingScriptText = definition;
            _pendingDebugCall = callText;
            _pendingDebugRequested = true;
            return;
        }
        await SetScriptTextAsync(definition);
        await StartStepDebugAsync(callText);
    }

    /// <summary>Bắt đầu phiên debug cho script đang có trong editor. <paramref name="callText"/> (nếu có) = câu EXEC đã chọn ở
    /// "Debug store/function" — dùng điền sẵn giá trị tham số.</summary>
    public async Task StartStepDebugAsync(string? callText = null)
    {
        var text = await GetScriptTextAsync();
        if (string.IsNullOrWhiteSpace(text)) return;

        var (name, parameters, _) = SqlStepPlanner.ParseHeader(text.Replace("\r\n", "\n").Replace("\r", "\n"));
        Dictionary<string, string>? values = null;
        if (parameters.Any(p => !p.ReadOnly))
        {
            await Task.Yield(); // thoát hẳn khỏi callback của WebView2 (GetScriptTextAsync) trước khi mở hộp tham số (WebView2 mới) — không thì hộp trắng trơn
            using var form = new StepParamsForm(name ?? "Script", parameters, StepParamsForm.InitialValues(text, name, callText, parameters));
            if (form.ShowDialog(this) != DialogResult.OK) { SetBarToggle("debug-step", _debugStepOn = false); return; }
            values = form.Values;
        }

        var plan = SqlStepPlanner.Build(text, values);
        if (plan.SafeLines.Count == 0)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Không tìm thấy dòng nào có thể dừng để debug từng bước trong script này.";
            SetBarToggle("debug-step", _debugStepOn = false);
            return;
        }

        _plan = plan;
        _planText = text;
        _planValues = values;
        _executedLine = _lastOkLine = _errorFrom = _errorLine = 0;
        _lastVars = null;
        _debugStepOn = true;
        SetBarToggle("debug-step", true);
        _resultView.Clear();
        _tabs.ClearMessages();
        _statusLabel.ForeColor = AppColors.Success;
        _statusLabel.Text = $"Debug từng bước{(name is null ? "" : " — " + name)}: {plan.SafeLines.Count} điểm dừng. F10 bước kế · F5 chạy tiếp · Ctrl+F10 chạy tới con trỏ · Shift+F5 dừng.";
        PushDebugState();
    }

    // ---------------- Bố cục nhóm của thanh Execute (SqlBarLayout) ----------------

    private void OnBarLayoutChanged(SqlBarLayout? layout)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(new Action(() => PushBarLayout(layout)));
    }

    /// <summary>Theme + bố cục nhóm gắn vào trang thanh TRƯỚC mọi script của nó (sqlquerybar.html áp ngay lúc dựng): lần vẽ đầu đã đúng bố cục,
    /// không còn vẽ bố cục gốc 2 hàng rồi mới gom nhóm khi <see cref="PushBarLayout"/> tới.</summary>
    private static string BarInitScript()
    {
        var layout = SqlBarLayout.Load();
        return $"window.__sqlBarInit = {{ dark: {(Bcode.App.UI.AppColors.IsDark ? "true" : "false")}, layout: {(layout is null ? "null" : layout.ToJson())} }};";
    }

    private void PushBarLayout(SqlBarLayout? layout)
    {
        if (!_barReady || !BarAlive) return;
        _ = BarExec($"window.setLayout && window.setLayout({(layout is null ? "null" : layout.ToJson())})");
    }

    /// <summary>Menu của 1 nhóm trên thanh (trang gửi danh sách mục + toạ độ nút): bấm mục nào thì trang bấm hộ đúng nút gốc (window.barInvoke).</summary>
    private void ShowBarGroupMenu(System.Text.Json.JsonElement req)
    {
        if (WebMenu.JustDismissed) return;   // bấm lại nút nhóm khi menu đang mở = đóng
        void Invoke(string key, string? value) =>
            _ = BarExec($"window.barInvoke({System.Text.Json.JsonSerializer.Serialize(key)}, {System.Text.Json.JsonSerializer.Serialize(value)})");
        static string? Str(System.Text.Json.JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        static bool Bool(System.Text.Json.JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;

        var menu = new WebMenu().AddCaption(Str(req, "name") ?? "");
        if (req.TryGetProperty("items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var it in items.EnumerateArray())
            {
                var key = Str(it, "key") ?? "";
                var label = Str(it, "label") ?? key;
                var sc = Str(it, "shortcut");
                if (Str(it, "kind") == "select" && it.TryGetProperty("options", out var opts))
                {
                    var options = opts.EnumerateArray().Select(o => (Value: Str(o, "value") ?? "", Label: Str(o, "label") ?? "", Checked: Bool(o, "checked"), Shortcut: Str(o, "shortcut"))).ToList();
                    menu.AddSub(label, sub =>
                    {
                        foreach (var o in options) { var v = o.Value; sub.Add(o.Label, () => Invoke(key, v), shortcut: string.IsNullOrEmpty(o.Shortcut) ? null : o.Shortcut, @checked: o.Checked); }
                    });
                }
                else menu.Add(label, () => Invoke(key, null), shortcut: string.IsNullOrEmpty(sc) ? null : sc, @checked: Bool(it, "checked"));
            }
        menu.AddSeparator().Add("Tùy chỉnh thanh… (chuột phải lên thanh)", () => _ = BarExec("window.customizeBar && window.customizeBar()"));
        var x = req.TryGetProperty("x", out var xe) && xe.TryGetInt32(out var xi) ? xi : 10;
        var y = req.TryGetProperty("y", out var ye) && ye.TryGetInt32(out var yi) ? yi : BarHeight;
        menu.Show(BarControl, x, y);
    }

    /// <summary>"Tùy chỉnh thanh…": dùng lại màn Quick Access (Ghim / Nhóm / Ẩn, kéo thả) cho các nút của thanh Execute; lưu SqlBarLayout → mọi tab SQL áp ngay.</summary>
    private void CustomizeBar(System.Text.Json.JsonElement req)
    {
        static List<string> Keys(System.Text.Json.JsonElement l, string n) => l.TryGetProperty(n, out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
        static List<ToolBarGroup> Groups(System.Text.Json.JsonElement l) => l.TryGetProperty("groups", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array
            ? a.EnumerateArray().Select(g => new ToolBarGroup { Name = g.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "", Keys = Keys(g, "keys") }).ToList() : new List<ToolBarGroup>();
        if (IsDisposed || !req.TryGetProperty("items", out var itemsEl) || !req.TryGetProperty("layout", out var cur) || !req.TryGetProperty("defaults", out var def)) return;

        var labels = itemsEl.EnumerateArray().ToDictionary(i => i.GetProperty("key").GetString() ?? "", i => (Label: i.GetProperty("label").GetString() ?? "", Shortcut: i.TryGetProperty("shortcut", out var s) ? s.GetString() ?? "" : ""));
        var curGroups = Groups(cur);
        var order = Keys(cur, "pinned").Concat(curGroups.SelectMany(g => g.Keys)).Concat(Keys(cur, "hidden")).Where(labels.ContainsKey).Distinct().ToList();
        order.AddRange(labels.Keys.Where(k => !order.Contains(k)));
        var defGroups = Groups(def);
        var defOrder = Keys(def, "pinned").Concat(defGroups.SelectMany(g => g.Keys)).ToList();

        using var form = new Bcode.App.Forms.QuickAccessForm(order.Select(k => (k, labels[k].Label)), new HashSet<string>(Keys(cur, "hidden")), defOrder,
            curGroups, defGroups,
            title: "Tùy chỉnh thanh SQL Query",
            intro: "<b>Kéo thả</b> nút giữa các vùng: <b>Ghim</b> = nút riêng trên thanh, <b>nhóm</b> = gom vào 1 nút “Tên ▾” bấm ra menu, <b>Ẩn</b> = không hiện. "
                 + "Áp cho mọi tab SQL Query. Phím mở nhóm thứ 1…9: Template → Phím tắt → “Mở nhóm thứ N trên thanh SQL Query”. Phím tắt riêng của từng nút (F5, F7, Alt+Z…) không đổi. "
                 + "“Gợi ý nhóm” = bố cục mặc định; “Mặc định” = hiện mọi nút, không nhóm.",
            toolShortcut: k => labels.TryGetValue(k, out var v) ? v.Shortcut : "",
            groupShortcut: i => Bcode.App.UI.ShortcutRegistry.Display($"editor.barGroup{i}"));
        if (form.ShowDialog(FindForm()) != DialogResult.OK) return;
        var grouped = new HashSet<string>(form.Groups.SelectMany(g => g.Keys));
        SqlBarLayout.Save(new SqlBarLayout
        {
            Pinned = form.OrderedKeys.Where(k => !form.HiddenKeys.Contains(k) && !grouped.Contains(k)).ToList(),
            Groups = form.Groups,
            Hidden = form.HiddenKeys.ToList(),
        });
    }

    private void PushTogglesToBar()
    {
        SetBarToggle("suggest", _suggestOn);
        SetBarToggle("reset-conn", _resetConnOn);
        SetBarToggle("result-tab", _resultTabOn);
        SetBarToggle("debug-step", _debugStepOn);
        if (_wordWrap) _scriptBoxWordWrapToggle(true);
    }

    // ---------------- Ngủ đông (Chế độ hiệu năng) ----------------

    /// <summary>Chụp toàn bộ trạng thái tab để giải phóng WebView2 rồi dựng lại sau (xem MainForm.Hibernate.cs). null = tab đang bận
    /// hoặc có thứ không chụp lại được (đang chạy / debug / còn kết nối giữ transaction / breakpoint / khung AI mở) — khi đó KHÔNG ngủ đông.</summary>
    public async Task<SqlTabSnapshot?> TryCaptureSnapshotAsync()
    {
        if (!IsReady || IsDisposed || _running || _stepRunning || _plan is not null || _debugStepOn || _pendingDebugRequested
            || _persistentConn is not null || _breakpoints.Count > 0 || !_aiSplit.Panel2Collapsed || !EdAlive)
            return null;
        string text, viewState;
        try
        {
            text = await GetScriptTextAsync();
            var vsJson = await EdExec("window.getViewState ? window.getViewState() : ''");
            viewState = System.Text.Json.JsonSerializer.Deserialize<string>(vsJson) ?? "";
        }
        catch { return null; }
        // Trong lúc chờ editor trả lời có thể đã bắt đầu chạy / debug — kiểm lại.
        if (IsDisposed || _running || _stepRunning || _plan is not null || _persistentConn is not null) return null;
        return new SqlTabSnapshot(text, _useSysDatabase, viewState, _wordWrap, _suggestOn, _resetConnOn, _resultTabOn,
            _split?.Panel2Collapsed ?? false, _splitUserMoved ? _split?.SplitterDistance : null,
            _currentFilePath, _lastScript, _statusLabel.Text, _statusLabel.ForeColor, _tabs.Capture());
    }

    /// <summary>Đặt lại trạng thái đã chụp bằng <see cref="TryCaptureSnapshotAsync"/> vào tab mới dựng (nội dung + database đã đặt riêng).</summary>
    public void RestoreSnapshot(SqlTabSnapshot s)
    {
        _wordWrap = s.WordWrap;
        _suggestOn = s.Suggest;
        _resetConnOn = s.ResetConn;
        _resultTabOn = s.ResultTab;
        _currentFilePath = s.FilePath;
        _lastScript = s.LastScript;
        _statusLabel.Text = s.StatusText;
        _statusLabel.ForeColor = s.StatusColor;
        if (_split is not null) _split.Panel2Collapsed = s.ResultsCollapsed;
        if (s.SplitDistance is { } d) { _restoreSplit = d; _splitUserMoved = true; }
        _pendingViewState = string.IsNullOrEmpty(s.ViewState) ? null : s.ViewState;
        _tabs.Restore(s.Results);
        _pushTogglesOnBarReady = true;
        if (_barReady) PushTogglesToBar();
        if (_editorReady && _pendingViewState is { } vs && EdAlive)
        {
            // Tab dự phòng đã sẵn sàng từ trước: chữ vừa gán qua SetScriptText — đặt vị trí sau khi chữ vào.
            _pendingViewState = null;
            BeginInvoke(new Action(() => _ = EdExec($"window.restoreViewState && window.restoreViewState({System.Text.Json.JsonSerializer.Serialize(vs)})")));
        }
    }

    private void SetBarToggle(string key, bool on)
    {
        if (BarAlive)
            _ = BarExec($"window.setToggle && window.setToggle({System.Text.Json.JsonSerializer.Serialize(key)}, {(on ? "true" : "false")})");
    }

    private void PushDebugState()
    {
        if (!EdAlive) return;
        var next = _plan?.NextSafe(_executedLine) ?? 0;
        var state = System.Text.Json.JsonSerializer.Serialize(new
        {
            active = _plan is not null,
            next,
            executed = _executedLine,
            errorFrom = _errorFrom,
            errorLine = _errorLine,
            safe = _plan?.SafeLines,   // các dòng dừng được — trang đánh dấu ở lề
            dyn = _plan is null ? 0 : DynamicSqlInspector.Candidates(_lastVars).Count,   // số biến chứa câu SQL — bật nút 🧩 SQL động
            remaining = _plan is null ? 0 : _plan.SafeLines.Count(l => l > _executedLine),
        });
        _ = EdExec($"window.setDebugState && window.setDebugState({state})");
    }

    private void StopStepDebug()
    {
        _plan = null;
        _executedLine = _lastOkLine = _errorFrom = _errorLine = 0;
        _lastVars = null;
        _debugStepOn = false;
        SetBarToggle("debug-step", false);
        PushDebugState();
        if (_statusLabel.Text.StartsWith("Debug từng bước", StringComparison.Ordinal) || _statusLabel.Text.StartsWith("Đã chạy", StringComparison.Ordinal))
            _statusLabel.Text = "Đã dừng debug từng bước.";
    }

    /// <summary>"step" = chạy dòng kế; "continue" = tới ngay trước breakpoint kế tiếp (hoặc hết); "to-cursor" = tới ngay trước dòng con trỏ
    /// (con trỏ ở đoạn đã chạy qua thì lùi về đó); "back" = lùi về điểm dừng trước. Chưa có phiên debug thì (với "continue") bắt đầu phiên mới.</summary>
    private async Task DebugCommandAsync(string cmd, int cursorLine = 0)
    {
        if (_plan is null)
        {
            if (cmd == "continue") await StartStepDebugAsync();
            return;
        }
        if (_stepRunning) return;
        if (await RefreshPlanIfEditedAsync() && cmd == "back") return;   // code mới: về đầu; lùi thì dừng ở đó

        // Lùi: mỗi bước luôn chạy lại từ đầu rồi rollback, nên lùi = chạy lại tới điểm dừng trước đó (về trước điểm đầu = chưa chạy gì).
        if (cmd == "back" || (cmd == "to-cursor" && cursorLine > 0 && cursorLine <= _executedLine))
        {
            if (_executedLine == 0) return;
            var limit = cmd == "back" ? _executedLine : cursorLine;
            var prev = _plan.SafeLines.Where(l => l < limit).DefaultIfEmpty(0).Max();
            if (prev > 0) { await ExecuteStepAsync(prev); return; }
            _executedLine = _lastOkLine = _errorFrom = _errorLine = 0;
            _lastVars = null;
            _resultView.Clear();
            _tabs.ClearMessages();
            _statusLabel.ForeColor = AppColors.Success;
            _statusLabel.Text = $"Đã lùi về đầu — sắp chạy dòng {_plan.NextSafe(0)}.";
            PushDebugState();
            return;
        }

        var next = _plan.NextSafe(_executedLine);
        if (next == 0)
        {
            _statusLabel.ForeColor = AppColors.Success;
            _statusLabel.Text = "Đã chạy hết script. Bấm Shift+F5 để thoát debug.";
            return;
        }

        var plan = _plan;
        int LastSafeBefore(int line) => plan.SafeLines.Where(l => l < line).DefaultIfEmpty(next).Max();
        var target = next;
        if (cmd == "continue")
        {
            var stops = _breakpoints.Where(b => b > next).OrderBy(b => b).ToList();
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
        await RefreshPlanIfEditedAsync();
        if (_plan is null) return;
        var plan = _plan;
        var lines = plan.SafeLines;
        var runs = 0;
        async Task<bool> Fails(int line)
        {
            runs++;
            _statusLabel.ForeColor = Color.DarkOrange;
            _statusLabel.Text = $"Đang tìm lỗi: lần chạy {runs} — tới dòng {line}... (đã rollback, không lưu dữ liệu)";
            await using var conn = _service.CreateConnection(UseSysDatabase);
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
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Không tìm được lỗi: " + ex.Message;
            return;
        }
        finally { _stepRunning = false; }

        if (_plan != plan) return;
        if (bad < 0)
        {
            await ExecuteStepAsync(lines[^1]);
            _statusLabel.Text = $"Chạy hết tới dòng {lines[^1]} không có lỗi ({runs} lần chạy, đã rollback).";
            return;
        }
        _lastOkLine = bad > 0 ? lines[bad - 1] : 0;
        await ExecuteStepAsync(lines[bad]);   // hiện kết quả + tô đỏ đoạn lỗi (_lastOkLine, dòng bad]
        _statusLabel.Text = $"Tìm thấy lỗi sau {runs + 1} lần chạy: " + _statusLabel.Text;
    }

    /// <summary>
    /// Đang debug mà code trong editor đã sửa (vd sửa chỗ lỗi vừa tìm được): dựng lại kế hoạch từ code mới (giữ giá trị tham số đã nhập)
    /// và chạy lại từ đầu — không phải tắt debug rồi bật lại. Trả true nếu đã dựng lại.
    /// </summary>
    private async Task<bool> RefreshPlanIfEditedAsync()
    {
        if (_plan is null) return false;
        var text = await GetScriptTextAsync();
        if (string.IsNullOrWhiteSpace(text) || text == _planText) return false;
        var plan = SqlStepPlanner.Build(text, _planValues);
        if (plan.SafeLines.Count == 0) return false;   // code mới không dừng được ở đâu — giữ kế hoạch cũ
        _plan = plan;
        _planText = text;
        _executedLine = _lastOkLine = _errorFrom = _errorLine = 0;
        _lastVars = null;
        _resultView.Clear();
        _tabs.ClearMessages();
        _statusLabel.ForeColor = AppColors.Success;
        _statusLabel.Text = $"Script đã sửa — dựng lại debug từ code mới ({plan.SafeLines.Count} điểm dừng), chạy lại từ đầu.";
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
            _statusLabel.ForeColor = Color.Firebrick; _statusLabel.Text = "Chưa có biến nào chứa câu SQL ở điểm dừng này — chạy (F10) tới sau chỗ ghép @q rồi bấm lại.";
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
            await using var conn = _service.CreateConnection(UseSysDatabase);
            err = await DynamicSqlInspector.ParseAsync(conn, prepared);
        }
        catch (Exception ex) { checkedOk = false; _statusLabel.ForeColor = Color.Firebrick; _statusLabel.Text = "Không kiểm tra được cú pháp SQL động: " + ex.Message; }
        var stop = _executedLine;
        OpenObjectInNewTabRequested?.Invoke($"SQL động {v.Name} (dòng {stop})", UseSysDatabase, DynamicSqlInspector.Annotate(v.Name, stop, prepared, err));
        if (!checkedOk) return;
        if (err is { } e) { _statusLabel.ForeColor = Color.Firebrick; _statusLabel.Text = $"SQL động {v.Name}: lỗi cú pháp — {e.Message} (đã mở tab, dòng lỗi có đánh dấu ✖)."; }
        else { _statusLabel.ForeColor = AppColors.Success; _statusLabel.Text = $"SQL động {v.Name}: cú pháp hợp lệ (đã mở tab). Lỗi còn lại nếu có là tên bảng / cột hoặc dữ liệu."; }
    }

    private async Task ExecuteStepAsync(int target)
    {
        if (_plan is null) return;
        _stepRunning = true;
        _statusLabel.ForeColor = Color.DarkOrange;
        _statusLabel.Text = $"Đang chạy tới dòng {target}...";
        try
        {
            var (sql, watchNames) = _plan.BuildBatch(target);
            List<RawSqlService.BatchResult> results;
            await using (var conn = _service.CreateConnection(UseSysDatabase))
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
            _lastVars = reachedWatch ? tables.FirstOrDefault(t => t.TableName == "Variables") : null;
            if (tables.Count > 0) _resultView.SetTables(tables); else _resultView.Clear();

            var remaining = _plan.SafeLines.Count(l => l > _executedLine);
            if (errorBatch is not null)
            {
                // Bước trước (tới dòng _lastOkLine) chạy được → lỗi nằm trong đoạn vừa thêm: tô đỏ đoạn đó thay vì chỉ dời mũi tên sang dòng kế.
                _errorLine = target;
                _errorFrom = _plan.SafeLines.Where(l => l > _lastOkLine && l <= target).DefaultIfEmpty(target).Min();
                var where = _errorFrom == target ? $"dòng {target}" : $"đoạn dòng {_errorFrom}–{target}";
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = $"Lỗi ở {where} (tô đỏ; bước trước{(_lastOkLine > 0 ? $" tới dòng {_lastOkLine}" : "")} chạy được) — xem tab Message. ⤴ Back để xem lại, Shift+F5 dừng.";
                _tabs.SetMessages($"LỖI: {errorBatch.Error}", messages, tables.Count);
            }
            else
            {
                _lastOkLine = target;
                _errorFrom = _errorLine = 0;
                _statusLabel.ForeColor = AppColors.Success;
                _statusLabel.Text = remaining > 0
                    ? $"Đã chạy tới dòng {target} — còn {remaining} điểm dừng. (đã rollback, không lưu dữ liệu)"
                    : $"Đã chạy hết tới dòng {target}. (đã rollback, không lưu dữ liệu)";
                _tabs.SetMessages(null, messages, tables.Count);
            }
        }
        catch (Exception ex)
        {
            _executedLine = target;
            _errorLine = target;
            _errorFrom = _plan?.SafeLines.Where(l => l > _lastOkLine && l <= target).DefaultIfEmpty(target).Min() ?? target;
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi khi debug từng bước.";
            _tabs.SetMessages($"LỖI: {ex.Message}", null, 0);
        }
        finally
        {
            _stepRunning = false;
            PushDebugState();
        }
    }

    // ---------------- Execute ----------------

    /// <summary>Ctrl+R: ẩn / hiện khung kết quả (lưới + message) để editor rộng hết cỡ. Chạy script (Execute) khi đang ẩn thì tự hiện lại để thấy kết quả.</summary>
    public void ToggleResultPanel()
    {
        if (_split is null || IsDisposed) return;
        _split.Panel2Collapsed = !_split.Panel2Collapsed;
        if (_split.Panel2Collapsed) EdControl.Focus();
    }

    /// <summary>Phím tắt khi focus đang ở control WinForms của tab này (lưới kết quả, ô message...): Ctrl+R giống như khi đang ở editor.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.R)) { ToggleResultPanel(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

/// <summary>Lần đầu gặp một object: lưu bản đang có trong database TRƯỚC khi script ghi đè nó, để luôn có "bản gốc" để so sánh.</summary>
    private async Task CaptureHistoryBaselineAsync(List<Bcode.App.Services.SqlTrackedObject> tracked, bool useSys)
    {
        try
        {
            if (_service.Connections.Current is not { } ws) return;
            foreach (var obj in tracked)
            {
                // Thư mục lịch sử mặc định nằm trên ổ mạng (Source Path\History): mọi thao tác thư mục/file chạy ở luồng nền,
                // không thì share chậm hoặc rớt làm cả Bcode "Not responding".
                if (await Task.Run(() => Bcode.App.Services.SqlHistoryService.HasAny(ws, useSys, obj))) continue;
                var def = await _service.GetObjectDefinitionAsync(obj.Qualified, useSys);
                if (!string.IsNullOrWhiteSpace(def))
                {
                    var normalized = Bcode.App.Services.SqlHistoryService.NormalizeHeader(def);
                    await Task.Run(() => Bcode.App.Services.SqlHistoryService.Record(ws, useSys, obj, normalized, "BASELINE"));
                }
            }
        }
        catch { /* object chưa tồn tại (CREATE mới), không có quyền, mất kết nối... — không ảnh hưởng việc chạy script */ }
    }

    private void RecordHistory(List<Bcode.App.Services.SqlTrackedObject> tracked, bool useSys)
    {
        if (_service.Connections.Current is not { } ws) return;
        // Ghi lịch sử (đọc/ghi/xoá file trên ổ mạng) ở luồng nền: kết quả script đã có, không bắt người dùng chờ share.
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var obj in tracked)
                {
                    var act = System.Text.RegularExpressions.Regex.IsMatch(obj.Batch, @"^\s*(--[^\n]*\n\s*|/\*.*?\*/\s*)*CREATE\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline)
                        && !System.Text.RegularExpressions.Regex.IsMatch(obj.Batch, @"CREATE\s+OR\s+ALTER", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "CREATE" : "ALTER";
                    Bcode.App.Services.SqlHistoryService.Record(ws, useSys, obj, obj.Batch, act);
                }
            }
            catch { }
        });
    }

    /// <summary>Mở màn hình lịch sử sửa procedure/function — chọn sẵn object đầu tiên có trong script đang soạn.</summary>
    private async void OpenSqlHistory()
    {
        try
        {
            var script = await GetScriptTextAsync();
            await Task.Yield(); // thoát hẳn khỏi callback của WebView2 trước khi tạo WebView2 mới (nếu không: "Class not registered")
            var focus = Bcode.App.Services.SqlHistoryService.Detect(script).FirstOrDefault();
            using var form = new SqlHistoryForm(_service, focus, UseSysDatabase, text => SetScriptTextAsync(text));
            form.ShowDialog(this);
        }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được lịch sử: " + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    /// <summary>Cancel (như nút Cancel Executing Query của SSMS): dừng lệnh đang chạy trên máy chủ. Lệnh UPDATE/INSERT đang dở được SQL Server tự rollback phần của câu lệnh đó;
    /// transaction do script tự mở (BEGIN TRAN) thì vẫn còn mở trên connection nếu đang tắt Reset Connection.</summary>
    private void CancelRun()
    {
        if (!_running || _runCts is null || _runCts.IsCancellationRequested) return;
        _statusLabel.ForeColor = Color.DarkOrange;
        _statusLabel.Text = "Đang huỷ lệnh…";
        try { _runCts.Cancel(); } catch { /* đã xong */ }
    }

    /// <summary>Báo cho thanh công cụ biết đang chạy hay không: đang chạy thì nút Execute đổi thành "■ Cancel".</summary>
    private void PushRunningToBar(bool running)
    {
        try { _ = BarExec("window.setRunning && window.setRunning(" + (running ? "true" : "false") + ")"); } catch { /* thanh công cụ chưa sẵn sàng */ }
    }

    private async Task RunAsync()
    {
        // Bật "Debug từng bước": Execute không chạy cả script mà bắt đầu phiên debug (hoặc chạy tiếp tới breakpoint/hết khi đang debug).
        if (_debugStepOn || _plan is not null)
        {
            await DebugCommandAsync("continue");
            return;
        }
        if (_running)
        {
            _statusLabel.ForeColor = Color.DarkOrange;
            _statusLabel.Text = "Lệnh trước còn đang chạy — bỏ qua lần bấm này (chờ chạy xong rồi bấm lại).";
            return;
        }
        _running = true;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var runToken = _runCts.Token;
        PushRunningToBar(true);
        if (_split is { Panel2Collapsed: true }) _split.Panel2Collapsed = false; // đang ẩn khung kết quả: hiện lại để thấy kết quả
        _statusLabel.Text = "Đang chạy...";
        string? ranScript = null;
        var ranSys = false;
        var runWatch = System.Diagnostics.Stopwatch.StartNew();
        // Báo cáo nặng chạy lâu: hiện thời gian đã chạy mỗi giây để biết Bcode vẫn đang làm việc (giao diện không bị khoá).
        var tick = new System.Windows.Forms.Timer { Interval = 1000 };
        tick.Tick += (_, _) => { if (_running) { _statusLabel.ForeColor = Color.DarkOrange; _statusLabel.Text = runToken.IsCancellationRequested ? $"Đang huỷ lệnh… {runWatch.Elapsed.TotalSeconds:0}s" : $"Đang chạy… {runWatch.Elapsed.TotalSeconds:0}s — bấm ■ Cancel để dừng (server đang xử lý / đang đọc kết quả — vẫn thao tác được các tab khác)"; } };
        tick.Start();
        try
        {
            var script = await GetSelectedTextAsync();
            if (string.IsNullOrWhiteSpace(script))
                script = await GetScriptTextAsync();

            if (string.IsNullOrWhiteSpace(script)) return;

            var useSys = UseSysDatabase;
            ranScript = script; ranSys = useSys;
            var tracked = await Task.Run(() => Bcode.App.Services.SqlHistoryService.Detect(script)); // CREATE/ALTER procedure/function/view/trigger trong script (script lớn: regex không chặn giao diện)
            if (tracked.Count > 0) await CaptureHistoryBaselineAsync(tracked, useSys);
            var results = _resetConnOn
                ? await _service.ExecuteScriptAsync(script, useSys, runToken)
                : await RunWithPersistentConnectionAsync(script, useSys, runToken);

            var errorBatch = results.FirstOrDefault(r => r.Error is not null);
            ScriptExecuted?.Invoke(script, useSys, errorBatch is null, (int)runWatch.ElapsedMilliseconds, results.SelectMany(r => r.Tables).Sum(t => t.Rows.Count));
            if (errorBatch is null && tracked.Count > 0) RecordHistory(tracked, useSys);
            var allTables = results.SelectMany(r => r.Tables).ToList();
            var lastTable = allTables.LastOrDefault();
            _lastResultSchema = allTables.Select(t => t.Clone()).ToList();

            if (allTables.Count > 0)
            {
                if (_resultTabOn)
                    OpenResultInNewTabRequested?.Invoke(allTables, "Command Result");
                else
                    _resultView.SetTables(allTables);

                if (lastTable is not null)
                    ResultReady?.Invoke(lastTable);
            }
            else if (!_resultTabOn)
            {
                _resultView.Clear();
            }

            var totalAffected = results.Sum(r => r.RowsAffected);
            var summary = $"{results.Count} batch đã chạy" +
                           (allTables.Count > 0
                               ? allTables.Count == 1
                                   ? $" · {allTables[0].Rows.Count} dòng kết quả"
                                   : $" · {allTables.Count} bảng kết quả ({allTables.Sum(t => t.Rows.Count)} dòng)"
                               : "") +
                           (totalAffected > 0 ? $" · {totalAffected} dòng bị ảnh hưởng (INSERT/UPDATE/DELETE)" : "") +
                           (_resetConnOn ? "" : " · [Reset Connection tắt: giữ nguyên connection/#temp table]") +
                           RunTimeText(results);

            // Xử lý thông báo (PRINT/RAISERROR) và LỖI
            var messagesList = results.Select(r => r.Messages).Where(m => !string.IsNullOrEmpty(m)).ToList();
            var printed = string.Join(Environment.NewLine, messagesList);

            if (errorBatch is not null && errorBatch.Error == RawSqlService.CancelledMessage)
            {
                // Bấm Cancel: không phải lỗi — báo đã huỷ, vẫn giữ các bảng đã đọc xong và các dòng PRINT.
                _statusLabel.ForeColor = Color.DarkOrange;
                _statusLabel.Text = "Đã huỷ lệnh (Cancel)" + (allTables.Count > 0 ? $" · giữ {allTables.Count} bảng kết quả đã đọc xong" : "") + RunTimeText(results);
                _tabs.SetMessages(null, (printed.Length > 0 ? printed + Environment.NewLine : "") + RawSqlService.CancelledMessage, allTables.Count);
            }
            else if (errorBatch is not null)
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Có lỗi xảy ra (xem chi tiết ở tab Message)." + RunTimeText(results);
                
                // Nếu có lỗi, ghép chi tiết lỗi (đã chứa sẵn line number từ SqlException do RawSqlService bắt) 
                // vào đầu danh sách message để in ra.
                // Lỗi (đỏ) + các dòng PRINT trước khi lỗi (màu nhấn) hiện ở tab Message và tự chuyển sang tab đó.
                _tabs.SetMessages($"LỖI: {errorBatch.Error}", printed, allTables.Count);
                
                // ĐÃ XÓA MessageBox.Show ở đây
            }
            else
            {
                // Màu thành công theo theme + chữ đậm: DimGray trước đây mờ gần như tàng hình trên nền tối.
                _statusLabel.ForeColor = AppColors.Success;
                if (!_statusLabel.Font.Bold) _statusLabel.Font = new Font(_statusLabel.Font, FontStyle.Bold);
                _statusLabel.Text = summary;
                
                // Không lỗi: PRINT hiện ở tab Message (màu nhấn); có bảng thì vẫn ở Grid Result, chỉ chuyển sang Message khi không có bảng nào.
                _tabs.SetMessages(null, printed, allTables.Count);
            }

            UpdatePivotTab(allTables, script);
        }
        catch (SqlException ex)
        {
            if (ranScript is not null) ScriptExecuted?.Invoke(ranScript, ranSys, false, (int)runWatch.ElapsedMilliseconds, 0);
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi SQL.";
            
            // Lấy dòng lỗi chính xác từ SqlException
            var errorLines = new List<string>();
            foreach (SqlError err in ex.Errors)
            {
                errorLines.Add($"Lỗi ở dòng {err.LineNumber}: {err.Message}");
            }
            var fullErrorText = string.Join(Environment.NewLine, errorLines);
            
            _tabs.SetMessages($"LỖI SQL TRỰC TIẾP:{Environment.NewLine}{fullErrorText}", null, 0);
            // ĐÃ XÓA MessageBox.Show ở đây
        }
        catch (Exception ex)
        {
            if (ranScript is not null) ScriptExecuted?.Invoke(ranScript, ranSys, false, (int)runWatch.ElapsedMilliseconds, 0);
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi hệ thống.";
            
            _tabs.SetMessages($"LỖI HỆ THỐNG:{Environment.NewLine}{ex.Message}", null, 0);
            // ĐÃ XÓA MessageBox.Show ở đây
        }
        finally
        {
            tick.Stop(); tick.Dispose();
            _running = false;
            PushRunningToBar(false);
        }
    }

    /// <summary>" · ⏱ 15,8 s" — tổng thời gian các batch (máy chủ chạy + tải kết quả); nhiều batch thì thêm batch chậm nhất. Thời gian từng bảng kết quả hiện ở đầu mỗi bảng.</summary>
    private static string RunTimeText(List<RawSqlService.BatchResult> results)
    {
        if (results.Count == 0) return "";
        var total = results.Sum(r => r.ElapsedMs);
        var text = " · ⏱ " + FormatDuration(total);
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

    /// <summary>Nhận diện kết quả pivot (theo &lt;pivot&gt; của Grid controller nếu tìm thấy, không thì theo tên cột xRow/xColumn...) rồi bật / ẩn tab "Pivot".
    /// Đọc controller trên share nên chạy nền, không làm chậm việc hiện kết quả.</summary>
    private void UpdatePivotTab(List<DataTable> tables, string script)
    {
        _lastScript = script;
        if (tables.Count == 0) { _tabs.SetPivot(null); return; }
        var sourcePath = _service.Connections.Current?.SourcePath;
        var name = Bcode.App.Services.Rpt.ReportProfilerService.GuessName(script);
        _ = Task.Run(() =>
        {
            Bcode.App.Services.Rpt.ControllerInfo? info = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(sourcePath) && name.Length > 0)
                {
                    var loaded = new Bcode.App.Services.Rpt.GridControllerReader().Load(sourcePath, name);
                    if (loaded.GridPath is not null) info = loaded;
                }
            }
            catch { /* không đọc được controller (share chậm / không có quyền): vẫn đoán theo tên cột */ }
            Bcode.App.Services.Rpt.PivotPayload? payload = null;
            try { payload = Bcode.App.Services.Rpt.PivotDetector.Detect(tables, info); } catch { /* kết quả lạ: coi như không phải pivot */ }
            try { if (!IsDisposed) BeginInvoke(() => _tabs.SetPivot(payload)); } catch { /* tab đã đóng */ }
        });
    }

    private async Task<List<RawSqlService.BatchResult>> RunWithPersistentConnectionAsync(string script, bool useSys, CancellationToken ct = default)
    {
        // Connection giữ lại (Reset Connection tắt) phải bám theo workspace: đổi workspace thì bỏ đi và mở
        // lại, nếu không các lệnh vẫn chạy vào server/DB CŨ trong khi giao diện đã hiện workspace mới.
        var stamp = _service.CurrentStamp(useSys);
        if (_persistentConn is null || _persistentConnUsesSys != useSys || _persistentConn.State != ConnectionState.Open
            || _persistentConnStamp != stamp)
        {
            DisposePersistentConnection();
            _persistentConn = _service.CreateConnection(useSys);
            await _persistentConn.OpenAsync();
            _persistentConnUsesSys = useSys;
            _persistentConnStamp = stamp;
        }
        return await _service.ExecuteScriptOnAsync(script, _persistentConn, ct);
    }

    private void DisposePersistentConnection()
    {
        _persistentConn?.Dispose();
        _persistentConn = null;
    }

    // ---------------- Open / Save ----------------

    private async void OpenFile()
    {
        using var ofd = new OpenFileDialog { Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*" };
        if (ofd.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var text = await File.ReadAllTextAsync(ofd.FileName);
            _currentFilePath = ofd.FileName;
            await SetScriptTextAsync(text);
            _statusLabel.Text = $"Đã mở {ofd.FileName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Open", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
            var text = await GetScriptTextAsync();
            await File.WriteAllTextAsync(_currentFilePath, text);
            _statusLabel.Text = $"Đã lưu {_currentFilePath}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Save", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------------- Write Schema ----------------

    /// <summary>Tạo file .xsd cho Crystal Report từ các bảng kết quả lần chạy gần nhất (d1, d2, d3…) — Services/Rpt/XsdSchemaWriter, dùng chung với chế độ App.</summary>
    private async Task WriteSchemaAsync()
    {
        var script = await GetScriptTextAsync();
        await Task.Yield(); // thoát hẳn khỏi callback của WebView2 trước khi mở hộp RptSchemaForm (WebView2 mới) — không thì trang trắng
        var status = Bcode.App.Services.Rpt.XsdSchemaWriter.SaveWithDialog(this, _lastResultSchema, script, _service.Connections.Current?.SourcePath);
        if (status is null) return;
        _statusLabel.ForeColor = Color.DarkGreen;
        _statusLabel.Text = status;
    }

    // ---------------- Check Fields ----------------

    private async Task CheckFieldsAsync()
    {
        _statusLabel.Text = "Đang kiểm tra (SET NOEXEC ON)...";
        try
        {
            var script = await GetScriptTextAsync();
            var error = await _service.CheckFieldsAsync(script, UseSysDatabase);
            if (error is null)
            {
                _statusLabel.ForeColor = Color.DarkGreen;
                _statusLabel.Text = "Check Fields: OK — mọi bảng/cột trong script đều hợp lệ.";
            }
            else
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = $"Check Fields: {error}";
                MessageBox.Show(this, error, "Bcode — Check Fields", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi Check Fields.";
            MessageBox.Show(this, ex.Message, "Bcode — Check Fields", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---------------- Comment / Uncomment ----------------

    private void ToggleComment(bool comment)
    {
        if (EdAlive)
            _ = EdExec($"window.toggleComment && window.toggleComment({(comment ? "true" : "false")})");
    }

    // ---------------- Default Type ----------------

    private async Task ApplyDefaultTypeChoiceAsync(int choice)
    {
        if (choice <= 0) return;
        var toUpper = choice == 1;
        var script = await GetScriptTextAsync();
        var transformed = SqlSyntaxHighlighter.TransformKeywordCase(script, toUpper);
        await SetScriptTextAsync(transformed);
    }

    // ---------------- Beauty Format ----------------

    private async void BeautyFormat()
    {
        var selected = await GetSelectedTextAsync();
        var formatter = new SqlFormatterService();

        if (!string.IsNullOrWhiteSpace(selected))
        {
            var formatted = formatter.Format(selected);
            await InsertTextAtCaretAsync(formatted);
        }
        else
        {
            var all = await GetScriptTextAsync();
            if (string.IsNullOrWhiteSpace(all)) return;
            var formatted = formatter.Format(all);
            await SetScriptTextAsync(formatted);
        }
        _statusLabel.ForeColor = Color.DarkGreen;
        _statusLabel.Text = "Đã format lại câu lệnh SQL.";
    }

    // ---- Claude / Gemini nhúng bên phải tab SQL Query (dùng chung AiWebPanel với tab riêng) ----
    private SplitContainer _aiSplit = null!;
    private readonly Dictionary<Bcode.Shared.AiSite, AiWebPanel> _aiPanels = new();
    private Panel? _aiHead;

    private Button? _aiBtnClaude, _aiBtnGemini, _aiBtnFast, _aiBtnClose;
    private Bcode.Shared.AiSite _aiActive = Bcode.Shared.AiSite.Claude;
    /// <summary>Khung "Hỏi Fast" nhúng (MainForm tạo khi mở lần đầu ở tab này) và đang hiện nó thay vì Claude / Gemini.</summary>
    private Control? _fastPanel;
    private bool _fastActive;
    public event Action? FastAskRequested;

    private void StyleAiHead()
    {
        if (_aiHead is null || _aiHead.IsDisposed) return;
        _aiHead.BackColor = Bcode.App.UI.AppColors.PanelAlt;
        void Paint(Button? b, bool active)
        {
            if (b is null) return;
            b.BackColor = active ? Bcode.App.UI.AppColors.Accent : Bcode.App.UI.AppColors.PanelAlt;
            b.ForeColor = active ? Bcode.App.UI.AppColors.OnAccent : Bcode.App.UI.AppColors.Text;
            b.FlatAppearance.MouseOverBackColor = active ? Bcode.App.UI.AppColors.AccentHover : Bcode.App.UI.AppColors.ButtonBack;
        }
        Paint(_aiBtnClaude, !_fastActive && _aiActive == Bcode.Shared.AiSite.Claude);
        Paint(_aiBtnGemini, !_fastActive && _aiActive == Bcode.Shared.AiSite.Gemini);
        Paint(_aiBtnFast, _fastActive);
        Paint(_aiBtnClose, false);
    }

    /// <summary>Hiện "Hỏi Fast" trong khung nhúng bên phải (cùng chỗ Claude / Gemini; chuyển qua lại vẫn giữ cuộc hỏi).</summary>
    public void ShowFastAsk(Control panel)
    {
        EnsureAiHead();
        if (_fastPanel != panel)
        {
            panel.Dock = DockStyle.Fill;
            _aiSplit.Panel2.Controls.Add(panel);
            panel.BringToFront();
            _fastPanel = panel;
        }
        _fastActive = true;
        foreach (var kv in _aiPanels) kv.Value.Visible = false;
        panel.Visible = true;
        StyleAiHead();
        ExpandAiSplit();
    }

    private void ExpandAiSplit()
    {
        if (!_aiSplit.Panel2Collapsed) return;
        _aiSplit.Panel2Collapsed = false;
        try { _aiSplit.SplitterDistance = Math.Max(200, (int)(_aiSplit.Width * 0.55)); } catch { }
    }

    /// <summary>Hiện khung Claude/Gemini bên phải editor (tạo lần đầu, các lần sau chỉ chuyển qua lại — giữ nguyên cuộc trò chuyện).</summary>
    AiWebPanel ISqlTab.ShowAi(Bcode.Shared.AiSite site) => ShowAi(site);
    internal AiWebPanel ShowAi(Bcode.Shared.AiSite site)
    {
        EnsureAiHead();
        if (!_aiPanels.TryGetValue(site, out var panel))
        {
            panel = new AiWebPanel(site) { Dock = DockStyle.Fill };
            _aiPanels[site] = panel;
            _aiSplit.Panel2.Controls.Add(panel);
            panel.BringToFront();
        }
        _aiActive = site;
        _fastActive = false;
        if (_fastPanel is not null) _fastPanel.Visible = false;
        StyleAiHead();
        foreach (var kv in _aiPanels) kv.Value.Visible = kv.Key == site;
        ExpandAiSplit();
        panel.FocusWeb();
        return panel;
    }

    private void EnsureAiHead()
    {
        if (_aiHead is null)
        {
            // Thanh đầu khung: cao theo cỡ chữ thật (không cố định px) nên không bị cắt chữ khi UiScale/DPI khác nhau; nút tự vừa chữ, hiện nút đang dùng bằng màu nhấn.
            _aiHead = new Panel { Dock = DockStyle.Top, Height = Font.Height + 14 };
            Button Btn(string text, DockStyle dock, Action click)
            {
                var b = new Button { Text = text, Dock = dock, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 0, 8, 0),
                    FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Cursor = Cursors.Hand, TabStop = false };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (_, _) => click();
                return b;
            }
            _aiBtnClaude = Btn("Claude", DockStyle.Left, () => ShowAi(Bcode.Shared.AiSite.Claude));
            _aiBtnGemini = Btn("Gemini", DockStyle.Left, () => ShowAi(Bcode.Shared.AiSite.Gemini));
            _aiBtnFast = Btn("Hỏi Fast", DockStyle.Left, () => { if (_fastPanel is not null) ShowFastAsk(_fastPanel); else FastAskRequested?.Invoke(); });
            _aiBtnClose = Btn("✕", DockStyle.Right, HideAi);
            // Thứ tự thêm: nút Dock=Left thêm sau nằm bên trái các nút thêm trước → thêm Hỏi Fast, Gemini rồi Claude để thành Claude · Gemini · Hỏi Fast.
            _aiHead.Controls.Add(_aiBtnClose);
            _aiHead.Controls.Add(_aiBtnFast);
            _aiHead.Controls.Add(_aiBtnGemini);
            _aiHead.Controls.Add(_aiBtnClaude);
            _aiSplit.Panel2.Controls.Add(_aiHead);
            Bcode.App.UI.ThemeManager.ThemeChanged += StyleAiHead;
            Disposed += (_, _) => Bcode.App.UI.ThemeManager.ThemeChanged -= StyleAiHead;
        }
    }

    /// <summary>Ẩn khung Claude/Gemini nhúng (trang vẫn giữ trong bộ nhớ nên mở lại không phải tải/đăng nhập lại).</summary>
    internal void HideAi()
    {
        _aiSplit.Panel2Collapsed = true;
        FocusEditor();
    }

    internal bool IsAiShown => !_aiSplit.Panel2Collapsed;

    /// <summary>Lấy phần đang chọn (không có thì cả script) rồi báo cho MainForm mở Claude/Gemini web và đưa text vào ô chat.</summary>
    private async Task SendToAiAsync(string engine)
    {
        var text = await GetSelectedTextAsync();
        if (string.IsNullOrWhiteSpace(text)) text = await GetScriptTextAsync();
        if (string.IsNullOrWhiteSpace(text)) { _statusLabel.Text = "Chưa có script để gửi."; return; }
        AskAiRequested?.Invoke(engine, text);
    }

    // ---------------- Change Field to… (phần bôi đen là danh sách cột) ----------------

    /// <summary>Thay phần đang bôi đen bằng kết quả biến đổi và GIỮ NGUYÊN vùng chọn mới → áp tiếp được nhiều bước (thêm alias a. rồi MIN…). Ctrl+Z hoàn tác từng bước.</summary>
    private async Task ApplyFieldChangeAsync(Func<string, string> transform)
    {
        if (!EdAlive) return;
        var selected = await GetSelectedTextAsync();
        if (string.IsNullOrWhiteSpace(selected)) return;
        string result;
        try { result = transform(selected); } catch { return; }
        if (result == selected) return;
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        await EdExec($"window.replaceSelectionKeep({json})");
    }

    /// <summary>"Change Field to:" — CHỈ thêm vào menu khi đang bôi đen (quét khối) một danh sách cột (xem ShowEditorContextMenu). Danh sách phẳng như FCode,
    /// riêng Alias và các biến đổi còn lại gói trong 2 menu con.</summary>
    private void AddChangeFieldItems(WebMenu menu)
    {
        menu.AddSeparator();
        menu.AddCaption("Change Field to:");
        foreach (var fn in new[] { "MAX", "MIN", "SUM", "COUNT", "AVG" })
        {
            var f = fn;
            menu.Add(f, () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Wrap(s, f + "({0})")));
        }
        menu.AddSub("Alias", al =>
        {
            foreach (var x in new[] { "a", "b", "c", "d", "e", "f" })
            {
                var pre = x + ".";
                al.Add(pre, () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Prefix(s, pre)));
            }
            al.AddSeparator();
            al.Add("Bỏ tiền tố (a.x → x)", () => _ = ApplyFieldChangeAsync(SqlFieldTransform.StripPrefix));
        });
        menu.AddSub("More...", w => w
            .Add("ISNULL(x, 0)", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Wrap(s, "ISNULL({0}, 0)")))
            .Add("ISNULL(x, '')", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Wrap(s, "ISNULL({0}, '')")))
            .Add("LTRIM(RTRIM(x))", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Wrap(s, "LTRIM(RTRIM({0}))")))
            .Add("UPPER(x)", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Wrap(s, "UPPER({0})")))
            .Add("[x]", () => _ = ApplyFieldChangeAsync(SqlFieldTransform.Bracket))
            .Add("@x", () => _ = ApplyFieldChangeAsync(SqlFieldTransform.Variable))
            .Add("Thêm AS tên cột", () => _ = ApplyFieldChangeAsync(SqlFieldTransform.AddAlias))
            .AddSeparator()
            .Add("x = b.x   (SET của UPDATE)", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Compare(s, "", "b.", and: false)))
            .Add("a.x = b.x   (nối AND)", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Compare(s, "a.", "b.", and: true)))
            .Add("x = a.x   (SET của UPDATE)", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Compare(s, "", "a.", and: false))));
    }

    private async void ShowEditorContextMenu(int x, int y)
    {
        var selected = await GetSelectedTextAsync();
        var hasSelection = !string.IsNullOrWhiteSpace(selected);

        try
        {
            _snippets?.Load();
        }
        catch { }

        var menu = new WebMenu();

        var project = CurrentProject?.Invoke() ?? "";
        var visible = _snippets?.Snippets.Where(s => s.AppliesTo(project)).ToList() ?? new();
        if (visible.Count > 0)
        {
            // Mỗi nhóm snippet (Tools, ...) là 1 mục có menu con bung ra bên phải — danh sách dài không còn kéo dài menu chuột phải.
            foreach (var group in visible.GroupBy(sn => string.IsNullOrWhiteSpace(sn.Category) ? "Tools" : sn.Category))
            {
                var grp = group.ToList();
                menu.AddSub(group.Key, sub =>
                {
                    foreach (var snippet in grp)
                    {
                        var content = snippet.Content;
                        sub.Add(string.IsNullOrWhiteSpace(snippet.Project) ? snippet.Name : "★ " + snippet.Name, () => _ = InsertTextAtCaretAsync(content));
                    }
                });
            }
            menu.AddSeparator();
        }
        else
        {
            menu.AddSub("Tools", sub => sub.Add("(Chưa có cấu hình - Mở Library...)", () => { }, enabled: false));
            menu.AddSeparator();
        }

        menu.Add("Cut", () =>
        {
            if (EdAlive)
                _ = EdExec("document.execCommand('cut')");
        }, enabled: hasSelection);

        menu.Add("Copy", () =>
        {
            if (EdAlive)
                _ = EdExec("document.execCommand('copy')");
        }, enabled: hasSelection);

        menu.Add("Paste", async () =>
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                await InsertTextAtCaretAsync(text);
            }
        }, enabled: Clipboard.ContainsText());

        menu.AddSeparator();
        menu.Add("Beauty Format", BeautyFormat);
        if (hasSelection) AddChangeFieldItems(menu);
        menu.AddSub("Hỏi AI (chưa gửi — gõ câu hỏi rồi Enter)", ai => ai
            .Add(hasSelection ? "Gửi phần chọn sang Claude" : "Gửi script sang Claude", () => _ = SendToAiAsync("claude"))
            .Add(hasSelection ? "Gửi phần chọn sang Gemini" : "Gửi script sang Gemini", () => _ = SendToAiAsync("gemini")));

        var clientPoint = EdControl.PointToClient(Cursor.Position);
        menu.Show(EdControl, clientPoint.X, clientPoint.Y);
    }
    /// <summary>
    /// Nạp danh sách bảng/view của Database hiện tại truyền xuống Monaco Editor để phục vụ gợi ý bảng
    /// </summary>
    private static SqlHintService? _hintService;

    /// <summary>Đẩy dữ liệu gợi ý sang editor: mẫu + cách gọi quen thuộc + snippet Library của dự án (tĩnh, nhỏ) rồi chữ ký procedure/function + options của
    /// database đang chọn (lấy từ cache, chỉ nạp lại khi có object mới / đổi). Xem Web/Shell/sqlhints.js.</summary>
    /// <summary>Chạy <paramref name="action"/> trên luồng giao diện qua cửa sổ của WebView2 editor. Dùng cho việc đẩy dữ liệu gợi ý: <c>this.BeginInvoke</c> ném
    /// InvalidOperationException ("...until the window handle has been created") khi RawSqlControl chưa được tạo cửa sổ (tab dựng sẵn ở khung ẩn, tab vừa mở),
    /// còn WebView2 editor thì đã có cửa sổ (nó đã khởi tạo xong) — lỗi cũ bị nuốt nên các tab đó không bao giờ nhận được mẫu gợi ý (xem hint-error.log).</summary>
    private void PostToEditorUi(Action action)
    {
        try
        {
            if (EdControl.IsHandleCreated && !EdControl.IsDisposed) EdControl.BeginInvoke(action);
            else if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
        }
        catch (InvalidOperationException) { /* cửa sổ đang đóng — bỏ qua */ }
    }

    /// <summary>Gắn danh sách mẫu gợi ý tĩnh (rsfilter, rsrep, unit...) vào trang NGAY khi trang được tạo (trước mọi script của trang): trang áp dụng nó ngay lúc dựng editor,
    /// nên mọi tab — kể cả tab procedure mở sau cùng — có mẫu mà không phải chờ lần đẩy của <see cref="LoadHintsForEditorAsync"/> (lần đẩy đó vẫn chạy để cập nhật mẫu Library và chữ ký procedure).</summary>
    private async Task InstallHintCatalogScriptAsync(Microsoft.Web.WebView2.Core.CoreWebView2? core = null)
    {
        try
        {
            var catalog = System.Text.Json.JsonSerializer.Serialize(SqlHintCatalog.ToEditorPayload());
            await (core ?? _editorWeb.CoreWebView2).AddScriptToExecuteOnDocumentCreatedAsync("window.__hintCatalog = " + catalog + ";");
        }
        catch (Exception ex) { LogHintError("gắn danh sách mẫu vào trang", ex); }
    }

    /// <summary>Ghi lỗi nạp gợi ý vào %AppData%\Bcode\hint-error.log (trước đây bị nuốt im lặng nên không biết vì sao một tab không có gợi ý).</summary>
    private static void LogHintError(string what, Exception ex)
    {
        try
        {
            var path = Path.Combine(BcodePaths.AppData, "Bcode", "hint-error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {what}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
        }
        catch { /* log chỉ để chẩn đoán */ }
    }

    private async Task LoadHintsForEditorAsync()
    {
        try
        {
            if (_sqlObjectService == null) return;
            var project = CurrentProject?.Invoke() ?? "";
            var user = _snippets?.Snippets.Where(s => s.AppliesTo(project)).Select(s => new { n = s.Name, c = s.Category, b = s.Content, proj = s.Project }).ToList();
            var catalog = System.Text.Json.JsonSerializer.Serialize(SqlHintCatalog.ToEditorPayload());
            var userJson = System.Text.Json.JsonSerializer.Serialize(user);
            PostToEditorUi(() => { if (EdAlive) _ = EdExec($"window.setHintCatalog && window.setHintCatalog({catalog}, {userJson});"); });

            _hintService ??= new SqlHintService(_sqlObjectService.Connections);
            var json = await _hintService.GetPayloadJsonAsync(UseSysDatabase);
            PostToEditorUi(() => { if (EdAlive) _ = EdExec($"window.setHintRoutines && window.setHintRoutines({json});"); });
        }
        catch (Exception ex) { LogHintError("nạp gợi ý vào editor", ex); /* gợi ý là phần phụ — lỗi (offline...) thì editor vẫn dùng bình thường */ }
    }

    private async Task SendHintColumnsAsync(string table, int reqId)
    {
        var rows = "[]";
        try
        {
            if (table.StartsWith('#'))
            {
                // Bảng tạm: chỉ có trong connection đang giữ (Reset Connection tắt). Không có / đang chạy lệnh khác thì trả rỗng, để trang tự thử cách khác.
                var conn = _persistentConn;
                if (conn is not null && conn.State == ConnectionState.Open)
                {
                    var names = new List<object[]>();
                    await using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT c.name FROM tempdb.sys.columns c WHERE c.object_id = OBJECT_ID('tempdb..' + @n) ORDER BY c.column_id", conn) { CommandTimeout = 10 };
                    cmd.Parameters.AddWithValue("@n", table);
                    await using var rd = await cmd.ExecuteReaderAsync();
                    while (await rd.ReadAsync()) names.Add(new object[] { rd.GetString(0), false });
                    rows = System.Text.Json.JsonSerializer.Serialize(names);
                }
            }
            else
            {
                var dot = table.LastIndexOf('.');
                var schema = dot > 0 ? table[..dot] : "dbo";
                var name = dot > 0 ? table[(dot + 1)..] : table;
                var cols = await _sqlObjectService.GetColumnsAsync(UseSysDatabase, schema, name);
                rows = System.Text.Json.JsonSerializer.Serialize(cols.Select(c => new object[] { c.Name, c.IsPrimaryKey }));
            }
        }
        catch { /* bảng không có / lỗi — trả danh sách rỗng */ }
        var tableJson = System.Text.Json.JsonSerializer.Serialize(table);
        PostToEditorUi(() => { if (EdAlive) _ = EdExec($"window.BcodeHints && BcodeHints.setColumns({reqId}, {tableJson}, {rows});"); });
    }

    public async Task LoadTablesForEditorAsync()
    {
        _ = LoadHintsForEditorAsync();
        try
        {
            if (_sqlObjectService == null) return;
            var timed = !_tablesTimed;
            _tablesTimed = true;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var objs = await _sqlObjectService.ListObjectsAsync(UseSysDatabase, "");
            var tables = objs
                .Where(o => o.Kind == SqlObjectKind.Table || o.Kind == SqlObjectKind.View)
                .Select(o => new { name = o.Name, kind = o.Kind == SqlObjectKind.Table ? "Table" : "View" })
                .ToList();
            var tList = sw.ElapsedMilliseconds;

            PostToEditorUi(async () =>
            {
                if (EdAlive)
                {
                    var tUi = sw.ElapsedMilliseconds;
                    var json = System.Text.Json.JsonSerializer.Serialize(tables);
                    var tJson = sw.ElapsedMilliseconds;
                    try { await EdExec($"window.setDatabaseTables && window.setDatabaseTables({json});"); }
                    catch { return; /* tab đóng giữa chừng */ }
                    if (timed)
                        Bcode.App.UI.ThemeManager.LogTiming($"  SQL Query: danh sách {tables.Count} bảng vào editor sau {sw.ElapsedMilliseconds} ms (đọc {tList}, chờ luồng UI {tUi - tList}, JSON {tJson - tUi} ms / {json.Length / 1024} KB, trang xử lý {sw.ElapsedMilliseconds - tJson}) — {_openWatch.ElapsedMilliseconds} ms từ lúc tạo tab");
                }
            });
        }
        catch { }
    }
}