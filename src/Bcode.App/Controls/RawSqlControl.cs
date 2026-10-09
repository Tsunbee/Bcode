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

public class RawSqlControl : UserControl
{
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _barWeb = new();
    private readonly Microsoft.Web.WebView2.WinForms.WebView2 _editorWeb = new();
    private bool _editorReady;
    private SplitContainer? _split; // editor (Panel1) | kết quả + message (Panel2)
    private string? _pendingScriptText;
    private readonly AppSettings _settings = AppSettings.Load();
    private bool _wordWrap;
    private bool _useSysDatabase;
    private bool _suggestOn = true;
    private bool _resetConnOn = true;
    private bool _resultTabOn;
    private bool _debugStepOn;
    private StepPlan? _plan;          // phiên debug từng bước đang mở (null = không debug)
    private int _executedLine;        // dòng cuối cùng đã chạy tới (0 = chưa chạy gì)
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

    private int _barRetries, _editorRetries;

    /// <summary>0x80004004 (E_ABORT): khởi tạo WebView2 bị huỷ vì control bị đổi cha / tạo lại cửa sổ giữa chừng — thường qua đi nếu thử lại.</summary>
    private static bool IsAbort(Exception ex) => ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80004004) } || ex.HResult == unchecked((int)0x80004004);

    /// <summary>Tab dựng sẵn được lấy ra dùng: nạp danh sách bảng cho gợi ý như một tab mới bình thường.</summary>
    public void BeginUse()
    {
        if (!_deferTables) return;
        _deferTables = false;
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

        _statusLabel = new TextBox 
        { 
            Dock = DockStyle.Top, 
            Height = 22, 
            ForeColor = Color.DimGray, 
            ReadOnly = true,               // Cho phép bôi đen copy nhưng không được gõ thêm
            BorderStyle = BorderStyle.None // Ẩn khung viền để trông giống hệt Label
        };
        _resultView = new MultiResultView { Dock = DockStyle.Fill };

        // Kết quả tách 2 tab như FCode: "Grid Result" (các bảng) và "Message" (PRINT/RAISERROR/lỗi, tô màu để dễ thấy) — cả hai là trang WebView2,
        // xem SqlResultTabs. Sau mỗi lần chạy tab được chọn tự động (có lỗi → Message; không có bảng mà có message → Message; còn lại → Grid Result).
        _tabs = new SqlResultTabs(_resultView) { Dock = DockStyle.Fill };
        _tabs.GotoLineRequested += line =>
        {
            if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.gotoLine && window.gotoLine({line})");
        };
        _tabs.CreateRptRequested += () => CreateRptRequested?.Invoke(_lastScript ?? "");

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        _split = split;
        split.Panel1MinSize = 80;
        split.Panel2MinSize = 80;

        _editorWeb.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(_editorWeb);

        split.Panel2.Controls.Add(_tabs);
        split.Panel2.Controls.Add(_statusLabel);
        // Mở tab lên: editor chiếm ~72% chiều cao, khung kết quả ~28% (trước đây cố định 260px nên màn hình cao thì khung kết quả quá to).
        // Còn theo tỉ lệ đó khi đổi cỡ cửa sổ cho tới khi người dùng tự kéo thanh chia.
        var splitUserMoved = false;
        var splitApplying = false;
        void ApplySplit()
        {
            if (splitUserMoved || split.Height < 200) return;
            splitApplying = true;
            try { split.SplitterDistance = Math.Clamp((int)(split.Height * 0.72), split.Panel1MinSize, Math.Max(split.Panel1MinSize, split.Height - split.Panel2MinSize - split.SplitterWidth)); }
            catch { /* chưa đủ chỗ để chia — lần đổi cỡ sau sẽ thử lại */ }
            finally { splitApplying = false; }
        }
        split.HandleCreated += (_, _) => ApplySplit();
        split.SizeChanged += (_, _) => ApplySplit();
        split.SplitterMoved += (_, _) => { if (!splitApplying) splitUserMoved = true; };

        // Khung Claude/Gemini nhúng bên phải (ẩn mặc định; bật bằng Settings "Claude/Gemini nhúng vào SQL Query") — xem ShowAi.
        _aiSplit = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6, Panel2Collapsed = true };
        _aiSplit.Panel1.Controls.Add(split);
        Controls.Add(_aiSplit);
        Controls.Add(_barWeb);

        Bcode.App.UI.ThemeManager.ThemeChanged += PushThemeToAll;
        EditorFontSizeChanged += OnEditorFontSizeChanged;

        _ = InitBarWebAsync();
        _ = InitEditorWebAsync();

        Disposed += (_, _) =>
        {
            DisposePersistentConnection();
            Bcode.App.UI.ThemeManager.ThemeChanged -= PushThemeToAll;
            EditorFontSizeChanged -= OnEditorFontSizeChanged;
        };

        async Task InitBarWebAsync()
        {
            try
            {
                await Bcode.App.UI.WebViewEnvironment.InitAsync(_barWeb);

                _barWeb.CoreWebView2.WebMessageReceived += async (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var root2 = doc.RootElement;
                    switch (root2.GetProperty("action").GetString())
                    {
                        case "__height":
                            // Chiều cao thật của thanh (px thiết bị) — theo UiScale/DPI và khi nội dung xuống dòng.
                            _barWeb.Height = Math.Clamp(root2.GetProperty("height").GetInt32() + 1, Bcode.App.UI.DpiScale.Px(this, 40), Bcode.App.UI.DpiScale.Px(this, 260));
                            break;
                        case "open": OpenFile(); break;
                        case "save": SaveFile(); break;
                        case "run": _ = RunAsync(); break;
                        case "debug-target": _ = PickDebugTargetAsync(); break;
                        case "write-schema": _ = WriteSchemaAsync(); break;
                        case "check-fields": _ = CheckFieldsAsync(); break;
                        case "comment": ToggleComment(true); break;
                        case "uncomment": ToggleComment(false); break;
                        case "beauty": BeautyFormat(); break;
                        case "toggle-wrap":
                            _scriptBoxWordWrapToggle(root2.GetProperty("value").GetBoolean());
                            break;
                        case "options": BuildOptionsMenu().Show(_barWeb, 10, _barWeb.Height); break;
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
                    }
                };

                _barWeb.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    _barReady = true;
                    PushThemeToAll();
                    PushDatabaseToBar();
                };

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
                _editorWeb.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

                _editorWeb.CoreWebView2.WebMessageReceived += async (_, e) =>
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(e.TryGetWebMessageAsString());
                    var root = doc.RootElement;
                    var action = root.GetProperty("action").GetString();

                    switch (action)
                    {
                        case "editor-ready":
                            _editorReady = true;
                            PushCopilotAuto();
                            if (!_deferTables) _ = LoadTablesForEditorAsync();
                            PushThemeToAll();
                            if (_pendingScriptText is not null)
                            {
                                await SetScriptTextAsync(_pendingScriptText);
                                _pendingScriptText = null;
                            }
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

                        case "run":
                            await RunAsync();
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
                        // Chức năng của thanh Execute gọi bằng phím tắt khai báo trong Template giao diện (Phím tắt → Editor SQL).
                        case "bar-action": RunBarAction(root.GetProperty("name").GetString() ?? ""); break;
                        case "debug-breakpoints":
                            _breakpoints.Clear();
                            foreach (var b in root.GetProperty("lines").EnumerateArray()) _breakpoints.Add(b.GetInt32());
                            break;

                        case "toggle-wrap-key":
                            _scriptBoxWordWrapToggle(!_wordWrap);
                            break;

                        case "close-context-menu":
                            this.BeginInvoke(() => WebMenu.CloseActive());
                            break;

                        case "show-context-menu":
                            var x = root.TryGetProperty("x", out var xProp) ? xProp.GetInt32() : 0;
                            var y = root.TryGetProperty("y", out var yProp) ? yProp.GetInt32() : 0;
                            ShowEditorContextMenu(x, y);
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

    // ---------------- Cỡ chữ editor (lưu settings.json, dùng chung mọi tab SQL Query) ----------------

    private static double? s_editorFontSize;
    private static event Action<double, RawSqlControl>? EditorFontSizeChanged;

    /// <summary>Cỡ chữ đã lưu (0 = mặc định FCode) — đọc settings.json 1 lần rồi giữ trong bộ nhớ, vì _settings của
    /// từng tab được nạp lúc tab mở nên có thể cũ hơn cỡ chữ vừa đổi ở tab khác.</summary>
    private static double EditorFontSize => s_editorFontSize ??= AppSettings.Load().SqlEditorFontSize;

    private void PushEditorFontSize()
    {
        if (_editorWeb.CoreWebView2 is null) return;
        var size = EditorFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setUserFontSize && window.setUserFontSize({size})");
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
        if (_barWeb.CoreWebView2 is not null)
            _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
        if (_editorWeb.CoreWebView2 is not null)
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
            _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setPalette ? window.setPalette({pal}, {isDark}) : (window.setTheme && window.setTheme({isDark}))");
            // Cỡ chữ mặc định của editor theo "Font gốc / Cỡ (pt)" của Template (9,5pt = cỡ cũ): đổi cỡ gốc thì editor SQL đổi theo, không còn đứng yên.
            var scale = Bcode.App.UI.UiTemplate.Current.FontSize / Bcode.App.UI.UiTemplate.DefaultFontSize;
            _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setEditorScale && window.setEditorScale({scale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}, {System.Text.Json.JsonSerializer.Serialize(Bcode.App.UI.UiTemplate.Current.FontFamily.Equals(Bcode.App.UI.UiTemplate.DefaultFontFamily, StringComparison.OrdinalIgnoreCase) ? "" : Bcode.App.UI.UiTemplate.Current.FontFamily)})");
            PushEditorFontSize(); // trước setEditorStyle: nó lấy cỡ chữ đã lưu làm mặc định khi Template không quy định cỡ chữ
            // Khu vực "Vùng soạn thảo SQL" của Template giao diện: font / cỡ / đậm / màu chữ / màu nền của Monaco (null = như cũ).
            var edCss = Bcode.App.UI.UiTemplate.AreaStyle("editor") is { } edStyle ? Bcode.App.UI.UiTemplate.ToInlineCss(edStyle) : null;
            _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setEditorStyle && window.setEditorStyle({System.Text.Json.JsonSerializer.Serialize(edCss)})");
            var ut = Bcode.App.UI.UiTemplate.Current;
            var edOpts = System.Text.Json.JsonSerializer.Serialize(new { lineSpacing = ut.EditorLineSpacing, minimap = ut.EditorMinimap, lineNumbers = ut.EditorLineNumbers, whitespace = ut.EditorWhitespace });
            _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setEditorOptions && window.setEditorOptions({edOpts})");
        }
    }

    // ---------------- Tương tác Copilot Inline ----------------

    private async Task HandleCopilotSuggestAsync(int reqId, string prefix, string suffix)
        {
            var suggestion = await QueryCopilotAiAsync(prefix, suffix);
            if (_editorWeb.CoreWebView2 is null) return;

            this.BeginInvoke(() =>
            {
                if (_editorWeb.CoreWebView2 is not null)
                {
                    var serialized = System.Text.Json.JsonSerializer.Serialize(suggestion);
                    _ = _editorWeb.CoreWebView2.ExecuteScriptAsync(
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

    private static readonly HttpClient _aiEditHttpClient = new() { Timeout = TimeSpan.FromSeconds(90) };

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
            if (_editorWeb.CoreWebView2 is null) return;
            _ = _editorWeb.CoreWebView2.ExecuteScriptAsync(
                $"window.setAiEditResult && window.setAiEditResult({requestId}, {System.Text.Json.JsonSerializer.Serialize(text)}, {System.Text.Json.JsonSerializer.Serialize(error)});");
        });
    }

    /// <summary>Ctrl+I bằng Claude (Anthropic Messages API).</summary>
    private async Task<string> CallClaudeEditAsync(string system, string user)
    {
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(_settings.ClaudeEditModel) ? "claude-sonnet-5-5" : _settings.ClaudeEditModel,
            max_tokens = 4096,
            system,
            messages = new[] { new { role = "user", content = user } },
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", _settings.AnthropicApiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");

        var res = await _aiEditHttpClient.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        if (!res.IsSuccessStatusCode)
        {
            var msg = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : res.ReasonPhrase;
            throw new InvalidOperationException($"Lỗi Claude API ({(int)res.StatusCode}): {msg}");
        }
        var text = "";
        if (doc.RootElement.TryGetProperty("content", out var blocks))
            foreach (var b in blocks.EnumerateArray())
                if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var tx))
                    text += tx.GetString();
        return text;
    }

    /// <summary>Ctrl+I bằng Gemini (generateContent) — cùng prompt với bản Claude; system prompt đi qua systemInstruction.</summary>
    private async Task<string> CallGeminiEditAsync(string system, string user)
    {
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3.6-flash:generateContent?key={_settings.GeminiApiKey}";
        var payload = new
        {
            systemInstruction = new { parts = new[] { new { text = system } } },
            contents = new[] { new { role = "user", parts = new[] { new { text = user } } } },
            generationConfig = new { temperature = 0.2, maxOutputTokens = 8192 },
        };
        using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
        var res = await _aiEditHttpClient.PostAsync(url, content);
        var body = await res.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        if (!res.IsSuccessStatusCode)
        {
            var msg = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m) ? m.GetString() : res.ReasonPhrase;
            throw new InvalidOperationException($"Lỗi Gemini API ({(int)res.StatusCode}): {msg}");
        }
        var text = "";
        if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0
            && cands[0].TryGetProperty("content", out var c) && c.TryGetProperty("parts", out var parts))
            foreach (var part in parts.EnumerateArray())
                if (part.TryGetProperty("text", out var tx)) text += tx.GetString();
        if (text.Length == 0) throw new InvalidOperationException("Gemini không trả về nội dung (có thể bị chặn bởi bộ lọc an toàn hoặc hết quota).");
        return text;
    }

    private void PushCopilotAuto()
    {
        if (_editorWeb.CoreWebView2 is null) return;
        _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setCopilotAuto && window.setCopilotAuto({(_settings.EnableCopilotSuggest ? "true" : "false")})");
    }

    private bool UseClaudeEngine =>
        string.Equals(_settings?.CopilotEngine, "claude", StringComparison.OrdinalIgnoreCase);

    private void SetCopilotStatus(string text, Color color)
    {
        this.BeginInvoke(() => { _statusLabel.ForeColor = color; _statusLabel.Text = text; });
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
                _statusLabel.Text = $"Copilot: Tạm dừng {remaining}s để hồi quota (tránh spam 429)...";
            });
            return "";
        }

        try
        {
            this.BeginInvoke(() =>
            {
                _statusLabel.ForeColor = Color.DimGray;
                _statusLabel.Text = "Copilot: Đang phân tích Procedure...";
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
                    _statusLabel.Text = "Copilot lỗi (429): Quá hạn mức request. Tự động tạm dừng 30 giây.";
                });
                return "";
            }
            if ((int)res.StatusCode == 503)
            {
                _rateLimitCooldownUntil = DateTime.Now.AddSeconds(10);
                this.BeginInvoke(() =>
                {
                    _statusLabel.ForeColor = Color.OrangeRed;
                    _statusLabel.Text = "Copilot: Server Gemini đang quá tải, tạm dừng 10 giây...";
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
                    _statusLabel.Text = $"Copilot lỗi ({(int)res.StatusCode}): {errorDetail}";
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
                            _statusLabel.Text = "Copilot: Đã có gợi ý (bấm Tab để nhận)";
                        });
                        return text;
                    }
                }
            }

            this.BeginInvoke(() =>
            {
                _statusLabel.ForeColor = Color.DimGray;
                _statusLabel.Text = "Copilot: Không có gợi ý phù hợp";
            });

            return "";
        }
        catch (Exception ex)
        {
            this.BeginInvoke(() =>
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Copilot: " + ex.Message;
            });
            return "";
        }
    }

    // ---------------- Tương tác trực tiếp với Monaco Editor ----------------

    public async Task<string> GetScriptTextAsync()
    {
        if (!_editorReady || _editorWeb.CoreWebView2 is null) return "";
        var json = await _editorWeb.CoreWebView2.ExecuteScriptAsync("window.getEditorText()");
        return System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
    }

    public async Task<string> GetSelectedTextAsync()
    {
        if (!_editorReady || _editorWeb.CoreWebView2 is null) return "";
        var json = await _editorWeb.CoreWebView2.ExecuteScriptAsync("window.getSelectedText()");
        return System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "";
    }

    public async Task SetScriptTextAsync(string text)
    {
        if (!_editorReady || _editorWeb.CoreWebView2 is null)
        {
            _pendingScriptText = text;
            return;
        }
        var jsonText = System.Text.Json.JsonSerializer.Serialize(text);
        await _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setEditorText({jsonText})");
    }

    public void SetScriptText(string text)
    {
        _ = SetScriptTextAsync(text);
    }

    /// <summary>Chèn định nghĩa procedure/function lên đầu script đang gõ (kèm GO) và nhảy lên đó — dùng cho Ctrl + chuột phải.
    /// Đã có sẵn trong script thì chỉ nhảy tới, không chèn lặp (xem window.prependDefinition trong sqleditor.html).</summary>
    public async Task PrependDefinitionAsync(string definition)
    {
        if (!_editorReady || _editorWeb.CoreWebView2 is null) return;
        var json = System.Text.Json.JsonSerializer.Serialize(definition);
        await _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.prependDefinition({json})");
        FocusEditor();
    }

    private async Task InsertTextAtCaretAsync(string text)
    {
        if (_editorWeb.CoreWebView2 is null) return;
        var jsonText = System.Text.Json.JsonSerializer.Serialize(text);
        await _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.replaceSelection({jsonText})");
    }

    private void _scriptBoxWordWrapToggle(bool wrap)
    {
        _wordWrap = wrap;
        var wrapStr = wrap ? "true" : "false";

        if (_editorWeb.CoreWebView2 is not null)
            _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setWordWrap({wrapStr})");

        if (_barWeb.CoreWebView2 is not null)
            _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"var chk = document.getElementById('chkWordWrap'); if(chk) chk.checked = {wrapStr};");
    }

    private WebMenu BuildOptionsMenu() => new WebMenu()
        .Add("Word Wrap", () => _scriptBoxWordWrapToggle(!_wordWrap), @checked: _wordWrap)
        .AddCaption("Copilot / AI")
        .Add("Cấu hình Gemini API Key...", () =>
        {
            var currentKey = _settings?.GeminiApiKey ?? "";
            var key = SimplePromptForm.Show(this, "Gemini API Key", "Nhập Google Gemini API Key:", currentKey);
            if (key is not null && _settings is not null)
            {
                _settings.GeminiApiKey = key.Trim();
                _settings.Save();
                MessageBox.Show(this, "Đã lưu Gemini API Key thành công!", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        })
        .Add("Cấu hình Claude (Anthropic) API Key...", () =>
        {
            var key = SimplePromptForm.Show(this, "Claude API Key", "Nhập Anthropic API Key (sk-ant-...):", _settings?.AnthropicApiKey ?? "");
            if (key is not null && _settings is not null)
            {
                _settings.AnthropicApiKey = key.Trim();
                // Có key rồi thì dùng luôn Claude cho gợi ý SQL; đổi lại Gemini ở mục bên dưới.
                if (_settings.AnthropicApiKey.Length > 0) _settings.CopilotEngine = "claude";
                _settings.Save();
                MessageBox.Show(this, "Đã lưu Claude API Key. Gợi ý SQL sẽ dùng Claude.", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        })
        .Add("Lịch sử sửa procedure/function...", () => BeginInvoke(new Action(OpenSqlHistory)))
        .Add("Lịch sử gợi ý AI...", () =>
        {
            using var form = new AiHistoryForm(InsertTextAtCaretAsync);
            form.ShowDialog(this);
        })
        .Add("Gợi ý tự động khi gõ", () =>
        {
            _settings.EnableCopilotSuggest = !_settings.EnableCopilotSuggest;
            _settings.Save();
            PushCopilotAuto();
        }, @checked: _settings.EnableCopilotSuggest)
        .Add("Gọi gợi ý ngay", () =>
        {
            if (_editorWeb.CoreWebView2 is not null)
                _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("editor && editor.focus(); editor && editor.trigger('menu', 'editor.action.inlineSuggest.trigger', {})");
        }, shortcut: "Alt+\\")
        .Add("AI sửa/sinh SQL...", () =>
        {
            if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("window.showAiEdit && window.showAiEdit()");
        }, shortcut: "Ctrl+I")
        .Add("Dùng Claude cho gợi ý SQL", () => { if (_settings is not null) { _settings.CopilotEngine = "claude"; _settings.Save(); } },
            @checked: UseClaudeEngine)
        .Add("Dùng Gemini cho gợi ý SQL", () => { if (_settings is not null) { _settings.CopilotEngine = "gemini"; _settings.Save(); } },
            @checked: !UseClaudeEngine)
        .Add("Claude/Gemini nhúng vào tab SQL Query (tắt = tab riêng)", () => { AppSettings.AiEmbedded = !AppSettings.AiEmbedded; if (!AppSettings.AiEmbedded && IsAiShown) HideAi(); }, @checked: AppSettings.AiEmbedded)
        .Add("Gửi script sang Claude (web)", () => _ = SendToAiAsync("claude"))
        .Add("Gửi script sang Gemini (web)", () => _ = SendToAiAsync("gemini"))
        .AddCaption("Cỡ chữ")
        .Add("Tăng cỡ chữ", () => { if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("window.setFontSize(1)"); })
        .Add("Giảm cỡ chữ", () => { if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("window.setFontSize(-1)"); });

    public event Action<DataTable>? ResultReady;
    /// <summary>Tab Pivot → "Tạo file Excel pivot…": mở Create RPT &amp; XML ở chế độ Pivot Excel với câu SQL vừa chạy.</summary>
    public event Action<string>? CreateRptRequested;
    /// <summary>Một lần Execute xong (kể cả lỗi): script, dùng Sys Data, thành công?, mili giây, số dòng kết quả — để ghi vào Lịch sử SQL.</summary>
    public event Action<string, bool, bool, int, int>? ScriptExecuted;
    /// <summary>Bấm "Lưu lịch sử" ở thanh Execute: script (phần chọn, không có thì cả script) + đang dùng Sys Data. Chỉ lưu khi người dùng bấm.</summary>
    public event Action<string, bool>? SaveHistoryRequested;
    /// <summary>Tên dự án (workspace) hiện tại — menu chuột phải chỉ hiện snippet chung + snippet của dự án này.</summary>
    public Func<string>? CurrentProject { get; set; }

    /// <summary>Chèn một đoạn (snippet) vào editor tại vị trí con trỏ.</summary>
    public Task InsertSnippetAsync(string text) => InsertTextAtCaretAsync(text);
    public event Action<List<DataTable>, string>? OpenResultInNewTabRequested;
    public event Action<string, bool, string>? OpenProcedureWithQueryRequested;

    /// <summary>Ctrl+F12 trên 1 object: (tên tab, có dùng Sys Data, nội dung) — MainForm mở tab SQL Query mới với nội dung đó.</summary>
    public event Action<string, bool, string>? OpenObjectInNewTabRequested;

    /// <summary>Nạp nội dung vào editor (tab mới mở: chờ editor sẵn sàng rồi mới nạp).</summary>
    public Task OpenScriptAsync(string text)
    {
        if (!_editorReady || _editorWeb.CoreWebView2 is null) { _pendingScriptText = text; return Task.CompletedTask; }
        return SetScriptTextAsync(text);
    }

    /// <summary>F12 / Ctrl+F12 trên 1 hoặc NHIỀU tên object (bôi đen nhiều tên). Bảng → cấu trúc; procedure / function / view / trigger → nội dung. Tìm ở database đang chọn trước, không thấy thì database còn lại.
    /// F12 = 1 khung peek (nhiều object: tab / gộp), Ctrl+F12 = mỗi object 1 tab SQL Query mới.</summary>
    private async Task PeekObjectsAsync(List<string> words, string mode)
    {
        var found = new List<(SqlObjectInfo Obj, string Text)>(); var missing = new List<string>();
        try
        {
            foreach (var word in words)
            {
                var raw = (word ?? "").Trim().Replace("[", "").Replace("]", "").Trim('.');
                if (raw.Length == 0) continue;
                if (raw.StartsWith('#') || raw.StartsWith('@') || raw.Contains("..#")) { if (words.Count == 1) missing.Add(raw + " (bảng tạm / biến)"); continue; }
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
                if (hit is null) { missing.Add(raw); continue; }
                if (found.Any(f => f.Obj.FromSysDatabase == hit.FromSysDatabase && string.Equals(f.Obj.QualifiedName, hit.QualifiedName, StringComparison.OrdinalIgnoreCase))) continue;
                found.Add((hit, hit.Kind == SqlObjectKind.Table ? await _sqlObjectService.GetTableStructureAsync(hit) : await _sqlObjectService.GetDefinitionAsync(hit)));
            }
            if (found.Count == 0)
            {
                _statusLabel.ForeColor = Color.DarkOrange;
                _statusLabel.Text = "F12: không thấy object " + string.Join(", ", missing.Take(5)) + " (đã tìm cả App Data và Sys Data).";
                return;
            }
            if (mode == "open") { foreach (var (o, t) in found) OpenObjectInNewTabRequested?.Invoke(o.QualifiedName, o.FromSysDatabase, t); }
            else if (_editorWeb.CoreWebView2 is not null)
            {
                var items = found.Select(f => new { title = f.Obj.QualifiedName + "   (" + f.Obj.Kind + (f.Obj.FromSysDatabase ? ", Sys Data" : ", App Data") + ")", text = f.Text }).ToList();
                await _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.showPeekMulti && window.showPeekMulti({System.Text.Json.JsonSerializer.Serialize(items)})");
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
        if (IsDisposed || _editorWeb.CoreWebView2 is null) return;
        _editorWeb.Focus();
        _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("window.editor && window.editor.focus && window.editor.focus()");
    }

    public void SetDatabase(bool useSysDatabase)
    {
        _useSysDatabase = useSysDatabase;
        DisposePersistentConnection();
        PushDatabaseToBar();
        if (!_deferTables) _ = LoadTablesForEditorAsync();
    }

    private void PushDatabaseToBar()
    {
        if (_barWeb.CoreWebView2 is null) return;
        var arg = System.Text.Json.JsonSerializer.Serialize(_useSysDatabase ? 1 : 0);
        _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setDatabase && window.setDatabase({arg})");
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
            case "font-up": if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("window.setFontSize(1)"); break;
            case "font-down": if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("window.setFontSize(-1)"); break;
            case "options": BuildOptionsMenu().Show(_barWeb, 10, _barWeb.Height); break;
            case "db-app": SetDatabase(false); break;
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
        if (!_editorReady || _editorWeb.CoreWebView2 is null)
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
            using var form = new StepParamsForm(name ?? "Script", parameters, StepParamsForm.ParseCallArgs(callText, parameters));
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
        _executedLine = 0;
        _debugStepOn = true;
        SetBarToggle("debug-step", true);
        _resultView.Clear();
        _tabs.ClearMessages();
        _statusLabel.ForeColor = AppColors.Success;
        _statusLabel.Text = $"Debug từng bước{(name is null ? "" : " — " + name)}: {plan.SafeLines.Count} điểm dừng. F10 bước kế · F5 chạy tiếp · Ctrl+F10 chạy tới con trỏ · Shift+F5 dừng.";
        PushDebugState();
    }

    private void SetBarToggle(string key, bool on)
    {
        if (_barWeb.CoreWebView2 is not null)
            _ = _barWeb.CoreWebView2.ExecuteScriptAsync($"window.setToggle && window.setToggle({System.Text.Json.JsonSerializer.Serialize(key)}, {(on ? "true" : "false")})");
    }

    private void PushDebugState()
    {
        if (_editorWeb.CoreWebView2 is null) return;
        var next = _plan?.NextSafe(_executedLine) ?? 0;
        var state = System.Text.Json.JsonSerializer.Serialize(new
        {
            active = _plan is not null,
            next,
            executed = _executedLine,
            remaining = _plan is null ? 0 : _plan.SafeLines.Count(l => l > _executedLine),
        });
        _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setDebugState && window.setDebugState({state})");
    }

    private void StopStepDebug()
    {
        _plan = null;
        _executedLine = 0;
        _debugStepOn = false;
        SetBarToggle("debug-step", false);
        PushDebugState();
        if (_statusLabel.Text.StartsWith("Debug từng bước", StringComparison.Ordinal) || _statusLabel.Text.StartsWith("Đã chạy", StringComparison.Ordinal))
            _statusLabel.Text = "Đã dừng debug từng bước.";
    }

    /// <summary>"step" = chạy dòng kế; "continue" = tới ngay trước breakpoint kế tiếp (hoặc hết); "to-cursor" = tới ngay trước dòng con trỏ.
    /// Chưa có phiên debug thì (với "continue") bắt đầu phiên mới.</summary>
    private async Task DebugCommandAsync(string cmd, int cursorLine = 0)
    {
        if (_plan is null)
        {
            if (cmd == "continue") await StartStepDebugAsync();
            return;
        }
        if (_stepRunning) return;

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
            if (tables.Count > 0) _resultView.SetTables(tables); else _resultView.Clear();

            var remaining = _plan.SafeLines.Count(l => l > _executedLine);
            if (errorBatch is not null)
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = $"Lỗi khi chạy tới dòng {target} (xem tab Message). F10 thử bước kế, Shift+F5 dừng.";
                _tabs.SetMessages($"LỖI: {errorBatch.Error}", messages, tables.Count);
            }
            else
            {
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
        if (_split.Panel2Collapsed) _editorWeb.Focus();
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
                if (Bcode.App.Services.SqlHistoryService.HasAny(ws, useSys, obj)) continue;
                var def = await _service.GetObjectDefinitionAsync(obj.Qualified, useSys);
                if (!string.IsNullOrWhiteSpace(def)) Bcode.App.Services.SqlHistoryService.Record(ws, useSys, obj, Bcode.App.Services.SqlHistoryService.NormalizeHeader(def), "BASELINE");
            }
        }
        catch { /* object chưa tồn tại (CREATE mới), không có quyền, mất kết nối... — không ảnh hưởng việc chạy script */ }
    }

    private void RecordHistory(List<Bcode.App.Services.SqlTrackedObject> tracked, bool useSys)
    {
        try
        {
            if (_service.Connections.Current is not { } ws) return;
            foreach (var obj in tracked)
            {
                var act = System.Text.RegularExpressions.Regex.IsMatch(obj.Batch, @"^\s*(--[^\n]*\n\s*|/\*.*?\*/\s*)*CREATE\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline)
                    && !System.Text.RegularExpressions.Regex.IsMatch(obj.Batch, @"CREATE\s+OR\s+ALTER", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? "CREATE" : "ALTER";
                Bcode.App.Services.SqlHistoryService.Record(ws, useSys, obj, obj.Batch, act);
            }
        }
        catch { }
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
        if (_split is { Panel2Collapsed: true }) _split.Panel2Collapsed = false; // đang ẩn khung kết quả: hiện lại để thấy kết quả
        _statusLabel.Text = "Đang chạy...";
        string? ranScript = null;
        var ranSys = false;
        var runWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var script = await GetSelectedTextAsync();
            if (string.IsNullOrWhiteSpace(script))
                script = await GetScriptTextAsync();

            if (string.IsNullOrWhiteSpace(script)) return;

            var useSys = UseSysDatabase;
            ranScript = script; ranSys = useSys;
            var tracked = Bcode.App.Services.SqlHistoryService.Detect(script); // CREATE/ALTER procedure/function/view/trigger trong script
            if (tracked.Count > 0) await CaptureHistoryBaselineAsync(tracked, useSys);
            var results = _resetConnOn
                ? await _service.ExecuteScriptAsync(script, useSys)
                : await RunWithPersistentConnectionAsync(script, useSys);

            var errorBatch = results.FirstOrDefault(r => r.Error is not null);
            ScriptExecuted?.Invoke(script, useSys, errorBatch is null, (int)runWatch.ElapsedMilliseconds, results.SelectMany(r => r.Tables).Sum(t => t.Rows.Count));
            if (errorBatch is null && tracked.Count > 0) RecordHistory(tracked, useSys);
            var allTables = results.SelectMany(r => r.Tables).ToList();
            var lastTable = allTables.LastOrDefault();

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
                           (_resetConnOn ? "" : " · [Reset Connection tắt: giữ nguyên connection/#temp table]");

            // Xử lý thông báo (PRINT/RAISERROR) và LỖI
            var messagesList = results.Select(r => r.Messages).Where(m => !string.IsNullOrEmpty(m)).ToList();
            var printed = string.Join(Environment.NewLine, messagesList);

            if (errorBatch is not null)
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = "Có lỗi xảy ra (xem chi tiết ở tab Message).";
                
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
            _running = false;
        }
    }

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

    private async Task<List<RawSqlService.BatchResult>> RunWithPersistentConnectionAsync(string script, bool useSys)
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
        return await _service.ExecuteScriptOnAsync(script, _persistentConn);
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

    private async Task WriteSchemaAsync()
    {
        var script = await GetScriptTextAsync();
        var guess = TableRefRegex.Match(script) is { Success: true } m
            ? m.Groups[1].Value.Replace("[", "").Replace("]", "")
            : "";

        var input = Bcode.App.Forms.SimplePromptForm.Show(this, "Write Schema", "Tên bảng cần lấy CREATE TABLE:", guess);
        if (string.IsNullOrWhiteSpace(input)) return;

        var (schema, table) = ParseTableRef(input);
        try
        {
            var obj = new SqlObjectInfo { Schema = schema, Name = table, Kind = SqlObjectKind.Table, FromSysDatabase = UseSysDatabase };
            var ddl = await _sqlObjectService.GetDefinitionAsync(obj);
            var block = "\r\n-- ===== Write Schema: " + obj.QualifiedName + " =====\r\n" +
                        string.Join("\r\n", ddl.Split('\n').Select(l => "-- " + l.TrimEnd('\r'))) +
                        "\r\n-- ===== hết Write Schema =====\r\n";
            await InsertTextAtCaretAsync(block);
            _statusLabel.Text = $"Đã chèn schema của {obj.QualifiedName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Bcode — Write Schema", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static (string schema, string table) ParseTableRef(string raw)
    {
        raw = raw.Trim().Replace("[", "").Replace("]", "");
        var parts = raw.Split('.', 2);
        return parts.Length == 2 ? (parts[0], parts[1]) : ("dbo", parts[0]);
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
        if (_editorWeb.CoreWebView2 is not null)
            _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.toggleComment && window.toggleComment({(comment ? "true" : "false")})");
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

    private Button? _aiBtnClaude, _aiBtnGemini, _aiBtnClose;
    private Bcode.Shared.AiSite _aiActive = Bcode.Shared.AiSite.Claude;

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
        Paint(_aiBtnClaude, _aiActive == Bcode.Shared.AiSite.Claude);
        Paint(_aiBtnGemini, _aiActive == Bcode.Shared.AiSite.Gemini);
        Paint(_aiBtnClose, false);
    }

    /// <summary>Hiện khung Claude/Gemini bên phải editor (tạo lần đầu, các lần sau chỉ chuyển qua lại — giữ nguyên cuộc trò chuyện).</summary>
    internal AiWebPanel ShowAi(Bcode.Shared.AiSite site)
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
            _aiBtnClose = Btn("✕", DockStyle.Right, HideAi);
            // Thứ tự thêm: nút Dock=Left thêm sau nằm bên trái các nút thêm trước → thêm Gemini rồi Claude để Claude đứng đầu.
            _aiHead.Controls.Add(_aiBtnClose);
            _aiHead.Controls.Add(_aiBtnGemini);
            _aiHead.Controls.Add(_aiBtnClaude);
            _aiSplit.Panel2.Controls.Add(_aiHead);
            Bcode.App.UI.ThemeManager.ThemeChanged += StyleAiHead;
            Disposed += (_, _) => Bcode.App.UI.ThemeManager.ThemeChanged -= StyleAiHead;
        }
        if (!_aiPanels.TryGetValue(site, out var panel))
        {
            panel = new AiWebPanel(site) { Dock = DockStyle.Fill };
            _aiPanels[site] = panel;
            _aiSplit.Panel2.Controls.Add(panel);
            panel.BringToFront();
        }
        _aiActive = site;
        StyleAiHead();
        foreach (var kv in _aiPanels) kv.Value.Visible = kv.Key == site;
        if (_aiSplit.Panel2Collapsed)
        {
            _aiSplit.Panel2Collapsed = false;
            try { _aiSplit.SplitterDistance = Math.Max(200, (int)(_aiSplit.Width * 0.55)); } catch { }
        }
        panel.FocusWeb();
        return panel;
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
        if (_editorWeb.CoreWebView2 is null) return;
        var selected = await GetSelectedTextAsync();
        if (string.IsNullOrWhiteSpace(selected)) return;
        string result;
        try { result = transform(selected); } catch { return; }
        if (result == selected) return;
        var json = System.Text.Json.JsonSerializer.Serialize(result);
        await _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.replaceSelectionKeep({json})");
    }

    private void AddChangeFieldItems(WebMenu menu)
    {
        menu.AddSeparator();
        menu.AddCaption("Change Field to:");
        foreach (var fn in new[] { "MAX", "MIN", "SUM", "COUNT", "AVG" })
        {
            var f = fn;
            menu.Add(f, () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Wrap(s, f + "({0})")));
        }
        menu.Add("Alias  ▸", () => BeginInvoke(() => ShowFieldSubMenu(alias: true)));
        menu.Add("More…  ▸", () => BeginInvoke(() => ShowFieldSubMenu(alias: false)));
    }

    private void ShowFieldSubMenu(bool alias)
    {
        var sub = new WebMenu();
        if (alias)
        {
            sub.AddCaption("Thêm tiền tố alias:");
            foreach (var a in new[] { "a", "b", "c", "d", "e", "f" })
            {
                var p = a + ".";
                sub.Add(p, () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Prefix(s, p)));
            }
            sub.AddSeparator();
            sub.Add("Bỏ tiền tố (a.x → x)", () => _ = ApplyFieldChangeAsync(SqlFieldTransform.StripPrefix));
        }
        else
        {
            sub.AddCaption("Bọc / đổi dạng:");
            sub.Add("ISNULL(x, 0)", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Wrap(s, "ISNULL({0}, 0)")));
            sub.Add("ISNULL(x, '')", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Wrap(s, "ISNULL({0}, '')")));
            sub.Add("LTRIM(RTRIM(x))", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Wrap(s, "LTRIM(RTRIM({0}))")));
            sub.Add("UPPER(x)", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Wrap(s, "UPPER({0})")));
            sub.Add("[x]", () => _ = ApplyFieldChangeAsync(SqlFieldTransform.Bracket));
            sub.Add("@x", () => _ = ApplyFieldChangeAsync(SqlFieldTransform.Variable));
            sub.Add("Thêm AS tên cột", () => _ = ApplyFieldChangeAsync(SqlFieldTransform.AddAlias));
            sub.AddSeparator();
            sub.AddCaption("So sánh / gán:");
            sub.Add("x = b.x   (SET của UPDATE)", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Compare(s, "", "b.", and: false)));
            sub.Add("a.x = b.x   (nối AND)", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Compare(s, "a.", "b.", and: true)));
            sub.Add("x = a.x   (SET của UPDATE)", () => _ = ApplyFieldChangeAsync(s => SqlFieldTransform.Compare(s, "", "a.", and: false)));
        }
        var pt = _editorWeb.PointToClient(Cursor.Position);
        sub.Show(_editorWeb, pt.X, pt.Y);
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
            foreach (var group in visible.GroupBy(s => string.IsNullOrWhiteSpace(s.Category) ? "Tools" : s.Category))
            {
                menu.AddCaption(group.Key);
                foreach (var snippet in group)
                {
                    var content = snippet.Content;
                    menu.Add(string.IsNullOrWhiteSpace(snippet.Project) ? snippet.Name : "★ " + snippet.Name, () => _ = InsertTextAtCaretAsync(content));
                }
            }
            menu.AddSeparator();
        }
        else
        {
            menu.AddCaption("Tools");
            menu.Add("(Chưa có cấu hình - Mở Library...)", () => { }, enabled: false);
            menu.AddSeparator();
        }

        menu.Add("Cut", () =>
        {
            if (_editorWeb.CoreWebView2 is not null)
                _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("document.execCommand('cut')");
        }, enabled: hasSelection);

        menu.Add("Copy", () =>
        {
            if (_editorWeb.CoreWebView2 is not null)
                _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("document.execCommand('copy')");
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
        menu.AddCaption("Hỏi AI (chưa gửi — gõ câu hỏi rồi Enter)");
        menu.Add(hasSelection ? "Gửi phần chọn sang Claude" : "Gửi script sang Claude", () => _ = SendToAiAsync("claude"));
        menu.Add(hasSelection ? "Gửi phần chọn sang Gemini" : "Gửi script sang Gemini", () => _ = SendToAiAsync("gemini"));

        var clientPoint = _editorWeb.PointToClient(Cursor.Position);
        menu.Show(_editorWeb, clientPoint.X, clientPoint.Y);
    }
    /// <summary>
    /// Nạp danh sách bảng/view của Database hiện tại truyền xuống Monaco Editor để phục vụ gợi ý bảng
    /// </summary>
    private static SqlHintService? _hintService;

    /// <summary>Đẩy dữ liệu gợi ý sang editor: mẫu + cách gọi quen thuộc + snippet Library của dự án (tĩnh, nhỏ) rồi chữ ký procedure/function + options của
    /// database đang chọn (lấy từ cache, chỉ nạp lại khi có object mới / đổi). Xem Web/Shell/sqlhints.js.</summary>
    private async Task LoadHintsForEditorAsync()
    {
        try
        {
            if (_sqlObjectService == null) return;
            var project = CurrentProject?.Invoke() ?? "";
            var user = _snippets?.Snippets.Where(s => s.AppliesTo(project)).Select(s => new { n = s.Name, c = s.Category, b = s.Content, proj = s.Project }).ToList();
            var catalog = System.Text.Json.JsonSerializer.Serialize(SqlHintCatalog.ToEditorPayload());
            var userJson = System.Text.Json.JsonSerializer.Serialize(user);
            BeginInvoke(() => { if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setHintCatalog && window.setHintCatalog({catalog}, {userJson});"); });

            _hintService ??= new SqlHintService(_sqlObjectService.Connections);
            var json = await _hintService.GetPayloadJsonAsync(UseSysDatabase);
            BeginInvoke(() => { if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setHintRoutines && window.setHintRoutines({json});"); });
        }
        catch { /* gợi ý là phần phụ — lỗi (offline...) thì editor vẫn dùng bình thường */ }
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
        BeginInvoke(() => { if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.BcodeHints && BcodeHints.setColumns({reqId}, {tableJson}, {rows});"); });
    }

    public async Task LoadTablesForEditorAsync()
    {
        _ = LoadHintsForEditorAsync();
        try
        {
            if (_sqlObjectService == null) return;
            var objs = await _sqlObjectService.ListObjectsAsync(UseSysDatabase, "");
            var tables = objs
                .Where(o => o.Kind == SqlObjectKind.Table || o.Kind == SqlObjectKind.View)
                .Select(o => new { name = o.Name, kind = o.Kind == SqlObjectKind.Table ? "Table" : "View" })
                .ToList();

            this.BeginInvoke(() =>
            {
                if (_editorWeb.CoreWebView2 is not null)
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(tables);
                    _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setDatabaseTables && window.setDatabaseTables({json});");
                }
            });
        }
        catch { }
    }
}