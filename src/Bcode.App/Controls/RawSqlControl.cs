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
    private readonly MultiResultView _resultView;
    private readonly Label _statusLabel;
    private readonly RawSqlService _service;
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly LookupService _lookupService;
    private readonly SnippetLibraryService _snippets;
    private static DateTime _rateLimitCooldownUntil = DateTime.MinValue;
    private string? _currentFilePath;
    private SqlConnection? _persistentConn;
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

        _statusLabel = new Label { Dock = DockStyle.Top, Height = 22, ForeColor = Color.DimGray, Padding = new Padding(4, 2, 0, 0) };
        _resultView = new MultiResultView { Dock = DockStyle.Fill };

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        split.Panel1MinSize = 80;
        split.Panel2MinSize = 80;

        _editorWeb.Dock = DockStyle.Fill;
        split.Panel1.Controls.Add(_editorWeb);

        split.Panel2.Controls.Add(_resultView);
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
                            switch (root2.GetProperty("which").GetString())
                            {
                                case "suggest": _suggestOn = !_suggestOn; break;
                                case "reset-conn":
                                    _resetConnOn = !_resetConnOn;
                                    if (_resetConnOn) DisposePersistentConnection();
                                    break;
                                case "result-tab": _resultTabOn = !_resultTabOn; break;
                                case "debug-step": _debugStepOn = !_debugStepOn; break;
                            }
                            break;
                    }
                };

                _barWeb.CoreWebView2.NavigationCompleted += (_, _) =>
                {
                    PushThemeToAll();
                    PushDatabaseToBar();
                };

                _barWeb.CoreWebView2.Navigate($"https://{Bcode.App.UI.WebViewEnvironment.Host}/sqlquerybar.html");
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
                            _ = LoadTablesForEditorAsync();
                            PushThemeToAll();
                            if (_pendingScriptText is not null)
                            {
                                await SetScriptTextAsync(_pendingScriptText);
                                _pendingScriptText = null;
                            }
                            break;

                        case "run":
                            await RunAsync();
                            break;

                        case "beauty":
                            BeautyFormat();
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

                _editorWeb.CoreWebView2.Navigate($"https://{Bcode.App.UI.WebViewEnvironment.Host}/sqleditor.html");
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
            _ = _editorWeb.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({isDark})");
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

    private async Task<string> QueryCopilotAiAsync(string prefix, string suffix)
    {
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

                    if (!string.IsNullOrWhiteSpace(text))
                    {
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
        .AddCaption("Cỡ chữ")
        .Add("Tăng cỡ chữ", () => { if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("window.setFontSize(1)"); })
        .Add("Giảm cỡ chữ", () => { if (_editorWeb.CoreWebView2 is not null) _ = _editorWeb.CoreWebView2.ExecuteScriptAsync("window.setFontSize(-1)"); });

    public event Action<DataTable>? ResultReady;
    public event Action<List<DataTable>, string>? OpenResultInNewTabRequested;
    public event Action<string, bool, string>? OpenProcedureWithQueryRequested;
    public event Action<Bcode.App.Models.SqlObjectInfo>? DebugTargetChosen;

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
            DebugTargetChosen?.Invoke(chosen.Target);
    }

    // ---------------- Execute ----------------

    private async Task RunAsync()
    {
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

            if (errorBatch is not null)
            {
                _statusLabel.ForeColor = Color.Firebrick;
                _statusLabel.Text = $"Lỗi ở batch: {errorBatch.Error}";
                MessageBox.Show(this, errorBatch.Error, "Bcode — Command", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                _statusLabel.ForeColor = Color.DimGray;
                _statusLabel.Text = summary;
            }
        }
        catch (Exception ex)
        {
            _statusLabel.ForeColor = Color.Firebrick;
            _statusLabel.Text = "Lỗi.";
            MessageBox.Show(this, ex.Message, "Bcode — Command", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task<List<RawSqlService.BatchResult>> RunWithPersistentConnectionAsync(string script, bool useSys)
    {
        if (_persistentConn is null || _persistentConnUsesSys != useSys || _persistentConn.State != ConnectionState.Open)
        {
            DisposePersistentConnection();
            _persistentConn = _service.CreateConnection(useSys);
            await _persistentConn.OpenAsync();
            _persistentConnUsesSys = useSys;
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