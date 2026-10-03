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
    private readonly Panel _messagesPanel;
    private readonly TextBox _messagesBox;
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

    public RawSqlControl(RawSqlService service, SqlObjectBrowserService sqlObjectService, LookupService lookupService, SnippetLibraryService snippets)
    {
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

        // ĐÃ THÊM: khu vực "Message" luôn hiển thị (giống tab Message của SSMS) để soi nội
        // dung PRINT/RAISERROR, kể cả khi batch chạy THÀNH CÔNG (trước đây chỉ show trong
        // MessageBox lỗi — Bee báo chạy thành công thì không thấy PRINT đâu cả).
        // Ẩn mặc định, chỉ hiện khi có message; nằm dưới cùng Panel2 (Dock=Bottom).
        var messagesHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 20,
            Text = "Message",
            Padding = new Padding(4, 2, 0, 0),
            Font = new Font(Font, FontStyle.Bold),
        };
        _messagesBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Font = new Font(FontFamily.GenericMonospace, 9f),
        };
        _messagesPanel = new Panel { Dock = DockStyle.Bottom, Height = 150, Visible = false };
        _messagesPanel.Controls.Add(_messagesBox);
        _messagesPanel.Controls.Add(messagesHeader);
        Bcode.App.UI.ThemeManager.Apply(_messagesPanel);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        split.Panel1MinSize = 80;
        split.Panel2MinSize = 80;

        _editorWeb.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(_editorWeb);

        split.Panel2.Controls.Add(_resultView);
        split.Panel2.Controls.Add(_messagesPanel);
        split.Panel2.Controls.Add(_statusLabel);
        split.HandleCreated += (_, _) =>
        {
            try { split.SplitterDistance = 260; } catch { }
        };

        Controls.Add(split);
        Controls.Add(_barWeb);

        Bcode.App.UI.ThemeManager.ThemeChanged += PushThemeToAll;

        _ = InitBarWebAsync();
        _ = InitEditorWebAsync();

        Disposed += (_, _) =>
        {
            DisposePersistentConnection();
            Bcode.App.UI.ThemeManager.ThemeChanged -= PushThemeToAll;
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
                    PushThemeToAll();
                    PushDatabaseToBar();
                };

                _barWeb.CoreWebView2.Navigate(Bcode.App.UI.UiOverrides.UrlFor("sqlquerybar.html"));
            }
            catch (Exception ex)
            {
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
                            _ = LoadTablesForEditorAsync();
                            PushThemeToAll();
                            if (_pendingScriptText is not null)
                            {
                                await SetScriptTextAsync(_pendingScriptText);
                                _pendingScriptText = null;
                            }
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
                MessageBox.Show(this, "Không khởi tạo được Monaco SQL Editor: " + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
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
            var apiKey = _settings.AnthropicApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new InvalidOperationException("Chưa có Claude API key — vào menu ⚙ > Cấu hình Claude (Anthropic) API Key.");

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

            var payload = new
            {
                model = string.IsNullOrWhiteSpace(_settings.ClaudeEditModel) ? "claude-sonnet-5-5" : _settings.ClaudeEditModel,
                max_tokens = 4096,
                system,
                messages = new[] { new { role = "user", content = user } },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", apiKey);
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
            if (doc.RootElement.TryGetProperty("content", out var blocks))
                foreach (var b in blocks.EnumerateArray())
                    if (b.TryGetProperty("type", out var t) && t.GetString() == "text" && b.TryGetProperty("text", out var tx))
                        text += tx.GetString();
            text = Regex.Replace(text.Trim(), @"^```[\w-]*\r?\n|\r?\n?```\s*$", "");
            // Lưu lại ngay khi có kết quả — đóng hộp thoại mà chưa Insert vẫn lấy lại được ở "Lịch sử gợi ý AI".
            AiHistoryStore.Add("Ctrl+I", "Claude", instruction, selection.Length > 300 ? selection[..300] + "…" : selection, text);
        }
        catch (TaskCanceledException) { error = "Hết thời gian chờ Claude (90s)."; }
        catch (Exception ex) { error = ex.Message; }

        this.BeginInvoke(() =>
        {
            if (_editorWeb.CoreWebView2 is null) return;
            _ = _editorWeb.CoreWebView2.ExecuteScriptAsync(
                $"window.setAiEditResult && window.setAiEditResult({requestId}, {System.Text.Json.JsonSerializer.Serialize(text)}, {System.Text.Json.JsonSerializer.Serialize(error)});");
        });
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
        .AddCaption("Cỡ chữ")
        .Add("Tăng cỡ chữ", () => { if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("window.setFontSize(1)"); })
        .Add("Giảm cỡ chữ", () => { if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("window.setFontSize(-1)"); });

    public event Action<DataTable>? ResultReady;
    public event Action<List<DataTable>, string>? OpenResultInNewTabRequested;
    public event Action<string, bool, string>? OpenProcedureWithQueryRequested;
    public event Action<Bcode.App.Models.SqlObjectInfo, string?>? DebugTargetChosen;

    private bool UseSysDatabase => _useSysDatabase;

    public void SetDatabase(bool useSysDatabase)
    {
        _useSysDatabase = useSysDatabase;
        DisposePersistentConnection();
        PushDatabaseToBar();
        _ = LoadTablesForEditorAsync();
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

    /// <summary>Chạy 1 chức năng của thanh Execute từ phím tắt (editor gửi {action:"bar-action", name}) — cùng đường với bấm nút trên thanh.</summary>
    private void RunBarAction(string name)
    {
        switch (name)
        {
            case "open": OpenFile(); break;
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
        _messagesPanel.Visible = false;
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
                var err = $"LỖI: {errorBatch.Error}";
                _messagesBox.Text = string.IsNullOrEmpty(messages) ? err : $"{err}{Environment.NewLine}---{Environment.NewLine}{messages}";
                _messagesBox.ForeColor = Color.Red;
                _messagesPanel.Visible = true;
            }
            else
            {
                _statusLabel.ForeColor = AppColors.Success;
                _statusLabel.Text = remaining > 0
                    ? $"Đã chạy tới dòng {target} — còn {remaining} điểm dừng. (đã rollback, không lưu dữ liệu)"
                    : $"Đã chạy hết tới dòng {target}. (đã rollback, không lưu dữ liệu)";
                _messagesBox.Text = messages;
                _messagesBox.ForeColor = SystemColors.WindowText;
                _messagesPanel.Visible = messages.Length > 0;
            }
        }
        catch (Exception ex)
        {
            _executedLine = target;
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi khi debug từng bước.";
            _messagesBox.Text = $"LỖI: {ex.Message}";
            _messagesBox.ForeColor = Color.Red;
            _messagesPanel.Visible = true;
        }
        finally
        {
            _stepRunning = false;
            PushDebugState();
        }
    }

    // ---------------- Execute ----------------

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
        _statusLabel.Text = "Đang chạy...";
        try
        {
            var script = await GetSelectedTextAsync();
            if (string.IsNullOrWhiteSpace(script))
                script = await GetScriptTextAsync();

            if (string.IsNullOrWhiteSpace(script)) return;

            var useSys = UseSysDatabase;
            var results = _resetConnOn
                ? await _service.ExecuteScriptAsync(script, useSys)
                : await RunWithPersistentConnectionAsync(script, useSys);

            var errorBatch = results.FirstOrDefault(r => r.Error is not null);
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
                var errorMessage = $"LỖI: {errorBatch.Error}";
                printed = string.IsNullOrEmpty(printed) ? errorMessage : $"{errorMessage}{Environment.NewLine}---{Environment.NewLine}{printed}";
                
                // Hiển thị nội dung lỗi và đổi màu chữ thành đỏ
                _messagesBox.Text = printed;
                _messagesBox.ForeColor = Color.Red;
                _messagesPanel.Visible = true;
                
                // ĐÃ XÓA MessageBox.Show ở đây
            }
            else
            {
                // Màu thành công theo theme + chữ đậm: DimGray trước đây mờ gần như tàng hình trên nền tối.
                _statusLabel.ForeColor = AppColors.Success;
                if (!_statusLabel.Font.Bold) _statusLabel.Font = new Font(_statusLabel.Font, FontStyle.Bold);
                _statusLabel.Text = summary;
                
                // Nếu không có lỗi, in PRINT bình thường với màu mặc định
                _messagesBox.Text = printed;
                _messagesBox.ForeColor = SystemColors.WindowText; // Màu mặc định cho text thành công
                _messagesPanel.Visible = printed.Length > 0;
            }
        }
        catch (SqlException ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi SQL.";
            
            // Lấy dòng lỗi chính xác từ SqlException
            var errorLines = new List<string>();
            foreach (SqlError err in ex.Errors)
            {
                errorLines.Add($"Lỗi ở dòng {err.LineNumber}: {err.Message}");
            }
            var fullErrorText = string.Join(Environment.NewLine, errorLines);
            
            _messagesBox.Text = $"LỖI SQL TRỰC TIẾP:{Environment.NewLine}{fullErrorText}";
            _messagesBox.ForeColor = Color.Red;
            _messagesPanel.Visible = true;
            // ĐÃ XÓA MessageBox.Show ở đây
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi hệ thống.";
            
            _messagesBox.Text = $"LỖI HỆ THỐNG:{Environment.NewLine}{ex.Message}";
            _messagesBox.ForeColor = Color.Red;
            _messagesPanel.Visible = true;
            // ĐÃ XÓA MessageBox.Show ở đây
        }
        finally
        {
            _running = false;
        }
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

        if (_snippets is not null && _snippets.Snippets.Count > 0)
        {
            foreach (var group in _snippets.Snippets.GroupBy(s => string.IsNullOrWhiteSpace(s.Category) ? "Tools" : s.Category))
            {
                menu.AddCaption(group.Key);
                foreach (var snippet in group)
                {
                    var content = snippet.Content;
                    menu.Add(snippet.Name, () => _ = InsertTextAtCaretAsync(content));
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

        var clientPoint = _editorWeb.PointToClient(Cursor.Position);
        menu.Show(_editorWeb, clientPoint.X, clientPoint.Y);
    }
    /// <summary>
    /// Nạp danh sách bảng/view của Database hiện tại truyền xuống Monaco Editor để phục vụ gợi ý bảng
    /// </summary>
    public async Task LoadTablesForEditorAsync()
    {
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