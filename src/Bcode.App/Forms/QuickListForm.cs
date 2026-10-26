using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// "Tạo nhanh danh mục" — chọn 1 bảng có sẵn trong App Data, khai báo khoá / tiêu đề / cột nào hiện ở Grid, Dir, Lookup,
/// Filter, Import (và tuỳ chọn 1 bảng chi tiết nhúng dạng lưới trong Dir) rồi sinh bộ file danh mục từ source mẫu FCode
/// <c>Templates\fileSource\CreateList</c> (Dir, Grid, Grid chi tiết, Lookup, Filter, Import + Upload, Main.aspx). Phần sinh file nằm trong <see cref="QuickListService"/>;
/// giao diện là trang WebView2 Web/Shell/quicklist.html.
/// </summary>
public class QuickListForm : WebDialogForm
{
    private const string Title = "Tạo nhanh danh mục";

    private readonly SqlObjectBrowserService _sqlObjects;
    private readonly TableDataService _tableData;
    private readonly DbConnectionService _connections;
    private readonly QuickListService _service = new();
    private readonly ColumnHeaderGuesser _headers = new();
    private readonly WCommandService _wcommand;
    private readonly AppSettings _settings;
    private List<WCommandItem>? _menus;

    // ---- "AI tạo danh mục" (bản thử): yêu cầu đang làm + câu hỏi/trả lời các lượt trước + bảng AI đề xuất chưa có trong DB ----
    private (string Name, string Content)? _aiRequest;
    private readonly List<(string Question, string Answer)> _aiAnswers = new();
    private List<string> _aiQuestions = new();
    private AiCatalogService? _aiService;
    private bool _aiBusy, _ready;
    /// <summary>Người dùng đã bấm "Theo luật Fast": các lượt sau (Gửi trả lời) cũng đi theo luật, cho tới khi bấm "Hỏi lại AI" / mở y/c khác.</summary>
    private bool _aiRulesMode;
    /// <summary>"AI phân tích": đang ở bước AI viết lại y/c theo mẫu luật Fast, chờ người dùng sửa / xác nhận (chưa điền spec).</summary>
    private bool _aiAnalyzeMode;
    /// <summary>Nội dung y/c gốc (trước khi xác nhận bản AI phân tích) — để "Phân tích lại".</summary>
    private string _aiOriginal = "";
    private readonly Dictionary<string, List<AiTableColumn>> _aiNewTables = new(StringComparer.OrdinalIgnoreCase);

    // ---- Kiểm tra chuẩn Fast (FastCatalogCheck): bảng App Data + kiểu cột của bảng nguồn tra cứu, nhớ trong phiên ----
    private HashSet<string>? _tableSet;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _typeCache = new(StringComparer.OrdinalIgnoreCase);

    // ---- Tab Script (bảng mới / cột thêm): cột của bảng nguồn khi clone (biết cột nào chép được dữ liệu), nhớ theo tên bảng ----
    private readonly Dictionary<string, ISet<string>> _sourceColumns = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tab Script → "Add Script": thêm script vào Script Cart của Bcode (MainForm.AddGeneratedScript).</summary>
    public event Action<string>? AddScriptRequested;
    /// <summary>Tab Script → "Mở SQL Query": tab SQL Query mới (App Data) chứa script (script, tiêu đề tab).</summary>
    public event Action<string, string>? OpenSqlRequested;

    public QuickListForm(SqlObjectBrowserService sqlObjects, TableDataService tableData, DbConnectionService connections,
        WCommandService wcommand, AppSettings settings)
        : base(Title, "quicklist.html", 1400, 880, 900, 600)
    {
        _sqlObjects = sqlObjects;
        _tableData = tableData;
        _connections = connections;
        _wcommand = wcommand;
        _settings = settings;

        // Mở song song với Bcode (MainForm.OpenQuickList dùng Show, không ShowDialog): là 1 cửa sổ riêng trên taskbar,
        // thu nhỏ được, để vừa làm danh mục vừa quay lại Bcode tra bảng / xem source.
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = true;
        ShowInTaskbar = true;
        if (Bcode.App.UI.AppIcons.AppIcon is { } icon) { Icon = icon; ShowIcon = true; }
    }

    /// <summary>Khoá workspace để tách cấu hình đã lưu (cùng tên bảng ở dự án khác có thể khác hẳn).</summary>
    private string WorkspaceKey => _connections.Current is { } w ? (w.Name.Length > 0 ? w.Name : w.AppDatabase) : "";

    protected override void OnReady()
    {
        var src = _connections.Current?.SourcePath ?? "";
        Js($"quick.init({J(new { template = QuickListService.DefaultTemplateDir, output = src, oldKeyFormat = QuickListService.DefaultOldKeyFormat })})");
        _ = LoadTablesAsync();
        _ = SourceFieldIndex.GetAsync(src);   // field source dự án — đọc sẵn ở nền cho luật Fast / AI / kiểm tra
        _ready = true;
        if (_aiRequest is not null) _ = RunCurrentAsync();
        // Từ điển field FCode (header.xml ~2MB) nạp sẵn ở nền để lúc "Nạp cột" không phải chờ.
        _ = Task.Run(() =>
        {
            if (FieldDictionaryService.Instance.LoadError is { } err)
                Js($"quick.onStatus({J("Không đọc được từ điển field (header.xml / parameters.xml): " + err)}, 'err')");
        });
    }

    protected override async Task OnActionAsync(string action, JsonElement msg)
    {
        switch (action)
        {
            case "loadColumns":
                await LoadColumnsAsync((msg.GetProperty("table").GetString() ?? "").Trim(),
                    msg.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.True);
                break;
            case "pickFolder":
            {
                var target = msg.GetProperty("target").GetString() ?? "";
                var current = msg.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                BeginInvoke(new Action(() =>
                {
                    using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(current) ? current : "" };
                    if (dlg.ShowDialog(this) == DialogResult.OK) Js($"quick.onFolder({J(target)}, {J(dlg.SelectedPath)})");
                }));
                break;
            }
            case "preview": Preview(msg); break;
            case "guessHeaders": GuessHeaders(msg); break;
            case "translate": await TranslateAsync(msg); break;
            case "copy":
            {
                var text = msg.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                BeginInvoke(new Action(() =>
                {
                    try { Clipboard.SetText(text); Js($"quick.onStatus({J("Đã copy nội dung file.")}, 'ok')"); }
                    catch { /* clipboard bận */ }
                }));
                break;
            }
            case "generate":
            {
                var issues = await FastCheckAsync(msg);
                BeginInvoke(new Action(() => Generate(msg, issues)));
                break;
            }
            case "fastCheck": await FastCheckAsync(msg); break;
            case "openFolder": OpenFolder(msg.GetProperty("path").GetString() ?? ""); break;
            case "openViewer":
            {
                var path = msg.GetProperty("path").GetString() ?? "";
                BeginInvoke(new Action(() => OpenInViewer(path)));
                break;
            }
            case "loadMenus": await LoadMenusAsync(msg.TryGetProperty("reload", out var rl) && rl.ValueKind == JsonValueKind.True); break;
            case "findMenus": await FindMenusAsync(msg); break;
            case "editMenu": await EditMenuAsync((msg.GetProperty("id").GetString() ?? "").Trim()); break;
            case "createMenu": await CreateMenuAsync(msg); break;
            case "aiAnswer":
            {
                // Trả lời câu hỏi lượt trước (trang gửi theo đúng thứ tự câu hỏi) rồi hỏi lại AI.
                var answers = msg.TryGetProperty("answers", out var arr) && arr.ValueKind == JsonValueKind.Array
                    ? arr.EnumerateArray().Select(a => a.GetString() ?? "").ToList() : new List<string>();
                for (var i = 0; i < _aiQuestions.Count; i++) _aiAnswers.Add((_aiQuestions[i], i < answers.Count ? answers[i] : ""));
                _aiQuestions = new();
                await RunAiAsync();
                break;
            }
            case "aiRetry": _aiRulesMode = false; _aiAnalyzeMode = false; await RunAiAsync(); break;
            case "aiRules": _aiRulesMode = true; _aiAnalyzeMode = false; await RunAiAsync(); break;   // gợi ý theo luật Fast, không gọi AI (kể cả khi đã có API key)
            case "aiAnalyze":
                // "Phân tích lại": AI viết lại y/c gốc theo mẫu luật Fast.
                if (_aiRequest is { } cur) { _aiRequest = (cur.Name, _aiOriginal.Length > 0 ? _aiOriginal : cur.Content); _aiAnalyzeMode = true; await RunAnalyzeAsync(); }
                break;
            case "aiConfirm":
            {
                // Người dùng đã sửa / xác nhận bản phân tích → luật Fast dựng danh mục từ đúng nội dung đó (không gọi AI nữa).
                var text = msg.TryGetProperty("text", out var tx) ? (tx.GetString() ?? "").Trim() : "";
                if (_aiRequest is not { } r0 || text.Length == 0) break;
                _aiRequest = (r0.Name, text);
                _aiAnswers.Clear();
                _aiQuestions = new();
                _aiAnalyzeMode = false;
                _aiRulesMode = true;
                await RunAiAsync();
                break;
            }
            case "aiClose": _aiRequest = null; break;
            case "script": await ScriptAsync(msg); break;
            case "designColumns": BeginInvoke(new Action(() => DesignColumns(msg))); break;
            case "addScript":
            {
                var text = msg.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                if (text.Trim().Length == 0) break;
                BeginInvoke(new Action(() =>
                {
                    AddScriptRequested?.Invoke(text);
                    Js($"quick.onStatus({J("Đã thêm script vào Script Cart của Bcode (và copy vào clipboard) — View Script Cart để xem.")}, 'ok')");
                }));
                break;
            }
            case "openSql":
            {
                var text = msg.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                var table = msg.TryGetProperty("table", out var tb) ? (tb.GetString() ?? "").Trim() : "";
                if (text.Trim().Length == 0) break;
                BeginInvoke(new Action(() => OpenSqlRequested?.Invoke(text, table.Length > 0 ? "Script " + table : "Script danh mục")));
                break;
            }
        }
    }

    // ---- Tab Script: bảng mới (CREATE TABLE) / cột thêm (ALTER TABLE ADD) ------------------------------

    /// <summary>Sinh script bảng theo spec trang đang khai (QuickListService.BuildTableScript) — chỉ hiện để copy / Add Script / mở SQL Query, không chạy.</summary>
    private async Task ScriptAsync(JsonElement msg)
    {
        try
        {
            var spec = ReadSpec(msg.GetProperty("spec"));
            foreach (var src in new[] { spec.NewTable ? spec.SourceTable : "", spec.DetailNew ? spec.DetailSourceTable : "" })
                if (src.Length > 0 && !_sourceColumns.ContainsKey(src))
                    _sourceColumns[src] = (await _sqlObjects.GetColumnsAsync(false, "dbo", src)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var script = QuickListService.BuildTableScript(spec, t => _sourceColumns.TryGetValue(t, out var set) ? set : new HashSet<string>());
            Js($"quick.onScript({J(script)})");
        }
        catch (Exception ex) { Js($"quick.onScript({J("-- Không sinh được script: " + ex.Message)})"); }
    }

    /// <summary>
    /// "+ Thêm cột" / ✎ sửa cột thêm: mở hộp Add New Column của tab Table (ColumnDesignForm: tên, kiểu, độ dài, Not NULL) rồi trả cột về trang
    /// (kèm type FastBusiness đoán từ kiểu SQL + các tra cứu chuẩn của header.xml như cột nạp từ bảng). Không chạy gì xuống DB.
    /// </summary>
    private void DesignColumns(JsonElement msg)
    {
        var detail = msg.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.True;
        var table = msg.TryGetProperty("table", out var t) ? (t.GetString() ?? "").Trim() : "";
        JsonElement? edit = msg.TryGetProperty("edit", out var e) && e.ValueKind == JsonValueKind.Object ? e : null;
        string S(JsonElement x, string n) => x.TryGetProperty(n, out var p) ? p.GetString() ?? "" : "";
        var initial = edit is { } x
            ? new[] { ColumnDesignForm.FromSqlType(S(x, "name"), S(x, "sqlType"), x.TryGetProperty("notNull", out var nn) && nn.ValueKind == JsonValueKind.True) }
            : Array.Empty<ColumnDesignForm.Col>();
        var hint = edit is null
            ? "Thêm cột (trường) vào danh mục — bảng mới: vào script CREATE TABLE; bảng có sẵn: script ALTER TABLE ADD (tab Script). Không tự chạy."
            : "Sửa tên / kiểu / độ dài / Not NULL của cột thêm — script ở tab Script tự cập nhật.";
        using var form = new ColumnDesignForm(edit is null, table, initial, hint);
        if (form.ShowDialog(this) != DialogResult.OK || form.Result.Count == 0) return;
        var cols = form.Result.Select(c => new
        {
            name = c.Name, sqlType = c.Type, notNull = c.NotNull, type = CatalogCloneService.GuessFieldType(c.Type),
            lookups = FieldDictionaryService.Instance.Lookups(c.Name).Select(l => new { l.Controller, l.Reference, l.Key, l.Check, l.Information }),
        });
        var index = edit is { } ed && ed.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var i) ? i : -1;
        Js($"quick.onDesigned({J(new { detail, index, columns = cols })})");
    }

    // ---- AI tạo danh mục ------------------------------------------------------------------------

    /// <summary>
    /// Bắt đầu "AI tạo danh mục" cho 1 yêu cầu (từ Note (New)): form đã mở thì chạy ngay, chưa thì chạy khi trang sẵn sàng.
    /// AI chỉ điền các ô của màn này — không ghi file, không chạy SQL; người dùng duyệt rồi bấm Tạo file như thường.
    /// </summary>
    /// <param name="rules">true = "⚙ Theo luật Fast": điền theo luật, không gọi AI kể cả khi có API key.</param>
    public void StartAi(string requestName, string content, bool rules = false)
    {
        _aiRequest = (requestName, content);
        _aiAnswers.Clear();
        _aiQuestions = new();
        _aiService = null;   // đọc lại danh sách bảng (có thể đã đổi workspace)
        _aiRulesMode = rules;
        _aiAnalyzeMode = false;
        _aiOriginal = content;
        if (_ready) _ = RunAiAsync();
    }

    /// <summary>
    /// "AI phân tích" cho 1 yêu cầu: AI viết lại y/c theo mẫu câu luật Fast đọc chuẩn (<see cref="AiCatalogService.AnalyzeAsync"/>) và dừng ở đó —
    /// khung AI hiện nội dung để người dùng sửa, bấm "Xác nhận & tạo theo luật Fast" thì luật Fast mới điền danh mục (action aiConfirm).
    /// </summary>
    public void StartAnalyze(string requestName, string content)
    {
        _aiRequest = (requestName, content);
        _aiAnswers.Clear();
        _aiQuestions = new();
        _aiService = null;
        _aiRulesMode = false;
        _aiAnalyzeMode = true;
        _aiOriginal = content;
        if (_ready) _ = RunAnalyzeAsync();
    }

    /// <summary>"⚙ Theo luật Fast" từ Note (New) / Yêu cầu FSG: như <see cref="StartAi"/> nhưng điền ngay theo luật Fast, không gọi AI (kể cả khi có API key).</summary>
    public void StartRules(string requestName, string content) => StartAi(requestName, content, rules: true);

    private Task RunCurrentAsync() => _aiAnalyzeMode ? RunAnalyzeAsync() : RunAiAsync();

    private async Task RunAnalyzeAsync()
    {
        if (_aiRequest is not { } req || _aiBusy) return;
        _aiBusy = true;
        void State(object o) => Js($"quick.aiState({J(o)})");
        State(new { name = req.Name, content = req.Content, busy = true, mode = "analyze", message = "Đang chuẩn bị..." });
        try
        {
            if (_connections.Current is null) throw new InvalidOperationException("Chưa chọn workspace.");
            _aiService ??= new AiCatalogService(_sqlObjects, _tableData, _settings, _connections.Current.SourcePath ?? "");
            var a = await _aiService.AnalyzeAsync(req.Name, req.Content, m => State(new { name = req.Name, content = req.Content, busy = true, mode = "analyze", message = m }));
            if (_aiRequest != req || !_aiAnalyzeMode) return;
            var notKind = a.Kind == "khac";
            State(new
            {
                name = req.Name, content = req.Content, busy = false, mode = "analyze", kind = notKind ? "khac" : "phan_tich", engine = a.Engine,
                summary = a.Summary, notes = a.Notes, analysis = a.Text,
                message = notKind
                    ? "AI cho rằng y/c này không phải tạo màn mới (danh mục / khai báo số liệu). Vẫn muốn thử thì tự ghi nội dung theo mẫu rồi Xác nhận."
                    : $"{a.Engine} đã phân tích theo mẫu luật Fast — sửa lại nội dung nếu cần rồi bấm Xác nhận & tạo theo luật Fast.",
            });
        }
        catch (TaskCanceledException) { if (_aiRequest == req) State(new { name = req.Name, content = req.Content, busy = false, mode = "analyze", kind = "loi", message = "Hết thời gian chờ AI — bấm “Theo luật Fast” để gợi ý không qua AI." }); }
        catch (Exception ex)
        {
            if (_aiRequest == req) State(new { name = req.Name, content = req.Content, busy = false, mode = "analyze", kind = "loi", message = ex.Message + (ex is InvalidOperationException ? "" : " — bấm “Theo luật Fast” để gợi ý theo cấu trúc Fast, không qua AI.") });
        }
        finally
        {
            _aiBusy = false;
            if (_aiRequest is { } next && next != req) _ = RunCurrentAsync();
        }
    }

    private async Task RunAiAsync()
    {
        if (_aiRequest is not { } req || _aiBusy) return;
        _aiBusy = true;
        void State(object o) => Js($"quick.aiState({J(o)})");
        State(new { name = req.Name, content = req.Content, busy = true, message = "Đang chuẩn bị..." });
        try
        {
            if (_connections.Current is null) throw new InvalidOperationException("Chưa chọn workspace.");
            _aiService ??= new AiCatalogService(_sqlObjects, _tableData, _settings, _connections.Current.SourcePath ?? "");
            var r = await _aiService.SuggestAsync(req.Name, req.Content, _aiAnswers.ToList(),
                m => State(new { name = req.Name, content = req.Content, busy = true, message = m }), forceRules: _aiRulesMode);
            if (_aiRequest != req) return;   // trong lúc chờ đã chọn y/c khác / đóng khung AI → bỏ kết quả cũ
            _aiQuestions = r.Questions;
            foreach (var (t, cols) in r.NewTables) _aiNewTables[t] = cols;
            var by = r.Engine != FastCatalogRules.Engine ? "theo AI" : _aiRulesMode ? "theo luật Fast (không AI)" : "theo luật Fast (chưa có API key)";
            State(new
            {
                name = req.Name, content = req.Content, busy = false, kind = r.Kind, engine = r.Engine, summary = r.Summary,
                notes = r.Notes, questions = r.Questions, createSql = r.CreateTableSql, hasSpec = r.Spec is not null,
                answered = _aiAnswers.Select(a => new { q = a.Question, a = a.Answer }),
                message = r.Kind switch
                {
                    "loi" => r.Summary,
                    "khac" => "Yêu cầu này không phải tạo danh mục mới — bản thử mới hỗ trợ loại \"tạo danh mục\".",
                    _ when r.Spec is null => "Chưa điền được spec — trả lời câu hỏi rồi bấm Gửi trả lời.",
                    _ when r.Questions.Count > 0 => $"Đã điền nháp {by} — còn câu hỏi cần trả lời; trả lời để điền lại, hoặc tự sửa rồi Tạo file.",
                    _ => $"Đã điền {by} — kiểm tra lại cột / tiêu đề / tuỳ chọn rồi bấm Tạo file.",
                },
            });
            if (r.Spec is not null)
            {
                // Đánh dấu gợi ý gốc của từng cột (trang giữ theo cột kể cả khi đổi tên) — lúc Tạo file so lại để nhớ những gì bạn sửa.
                foreach (var prop in new[] { "columns", "detailColumns" })
                    foreach (var c in (r.Spec[prop] as System.Text.Json.Nodes.JsonArray ?? new()).OfType<System.Text.Json.Nodes.JsonObject>())
                        c["ai"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["v"] = c["headerV"]?.ToString() ?? "", ["name"] = c["name"]?.ToString() ?? "", ["sqlType"] = c["sqlType"]?.ToString() ?? "",
                            ["ctl"] = c["lookup"]?["controller"]?.ToString() ?? "",
                        };
                Js($"quick.aiLoad({r.Spec.ToJsonString()})");
            }
        }
        catch (TaskCanceledException) { if (_aiRequest == req) State(new { name = req.Name, content = req.Content, busy = false, kind = "loi", message = "Hết thời gian chờ AI." }); }
        catch (Exception ex)
        {
            // AI lỗi (quá tải 503, hết hạn mức, mạng...): gợi ý dùng luật Fast thay vì bắt chờ.
            var hint = _aiRulesMode ? "" : " — bấm “Theo luật Fast” để gợi ý theo cấu trúc Fast, không qua AI.";
            if (_aiRequest == req) State(new { name = req.Name, content = req.Content, busy = false, kind = "loi", message = ex.Message + hint });
        }
        finally
        {
            _aiBusy = false;
            if (_aiRequest is { } next && next != req) _ = RunCurrentAsync();   // y/c mới đến trong lúc đang chạy
        }
    }

    private async Task LoadTablesAsync()
    {
        if (_connections.Current is null) { Js($"quick.onStatus({J("Chưa chọn workspace.")}, 'err')"); return; }
        try
        {
            var objs = await _sqlObjects.ListObjectsAsync(useSysDatabase: false);
            var names = objs.Where(o => o.Kind == SqlObjectKind.Table).Select(o => o.Name).OrderBy(n => n).ToArray();
            _tableSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            Js($"quick.onTables({J(names)})");
            Js($"quick.onStatus({J($"{names.Length} bảng trong App Data.")}, '')");
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không nạp được danh sách bảng: " + ex.Message)}, 'err')"); }
    }

    private async Task LoadColumnsAsync(string table, bool detail)
    {
        if (table.Length == 0) { Js($"quick.onStatus({J("Chọn bảng trước.")}, 'err')"); return; }
        try
        {
            var cols = await _sqlObjects.GetColumnsAsync(false, "dbo", table);
            Dictionary<string, string> types;
            bool isNew = false, standard = false;
            if (cols.Count == 0 && _aiNewTables.TryGetValue(table, out var aiCols))
            {
                // Bảng mới AI đề xuất (chưa có trong App Data): nạp cột theo spec AI để duyệt / xem XML; script tạo bảng ở khung AI + tab Script.
                cols = aiCols.Select(c => (c.Name, c.IsPrimaryKey)).ToList();
                types = aiCols.ToDictionary(c => c.Name, c => c.SqlType, StringComparer.OrdinalIgnoreCase);
                isNew = true;
            }
            else if (cols.Count == 0)
            {
                // Bảng chưa có trong App Data = bảng mới: danh mục bắt đầu bằng cột chuẩn FBO (trang giữ nguyên cột đang có nếu vừa nạp bảng khác = clone),
                // chi tiết thì trang tự dựng từ khoá danh mục + line_nbr. Script CREATE TABLE ở tab Script — không tự chạy.
                if (!System.Text.RegularExpressions.Regex.IsMatch(table, @"^[A-Za-z_][A-Za-z0-9_$]*$"))
                { Js($"quick.onStatus({J($"Không thấy bảng '{table}' — tên bảng mới chỉ gồm chữ, số, _ (không bắt đầu bằng số).")}, 'err')"); return; }
                if (detail)
                {
                    Js($"quick.onDetailColumns({J(new { table, keys = Array.Empty<string>(), columns = Array.Empty<object>(), isNew = true })})");
                    Js($"quick.onStatus({J($"Bảng chi tiết '{table}' chưa có trong App Data → bảng mới (script ở tab Script).")}, 'ok')");
                    return;
                }
                var std = FastCatalogRules.StandardColumns(table, _settings.NewCatalogTablePrefix);
                cols = std.Select(c => (c.Name, c.IsPrimaryKey)).ToList();
                types = std.ToDictionary(c => c.Name, c => c.SqlType, StringComparer.OrdinalIgnoreCase);
                isNew = standard = true;
            }
            else types = await _tableData.GetColumnTypesAsync(false, "dbo", table);
            var keys = cols.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList();
            if (keys.Count == 0) keys.Add(cols[0].Name);
            var list = cols.Select(c =>
            {
                types.TryGetValue(c.Name, out var sqlType);
                return new
                {
                    name = c.Name, sqlType = sqlType ?? "", type = CatalogCloneService.GuessFieldType(sqlType ?? ""), added = isNew,
                    // Các biến thể tra cứu chuẩn của FCode cho cột này (header.xml) — trang cho chọn, mặc định biến thể đầu.
                    lookups = FieldDictionaryService.Instance.Lookups(c.Name)
                        .Select(l => new { l.Controller, l.Reference, l.Key, l.Check, l.Information }),
                };
            });
            // Bảng danh mục: kèm cấu hình đã lưu lần trước (nếu có) để trang hỏi khôi phục.
            var saved = detail ? null : QuickListConfigStore.Load(WorkspaceKey, table);
            Js($"quick.{(detail ? "onDetailColumns" : "onColumns")}({J(new { table, keys, columns = list, saved, isNew, standard })})");
            Js($"quick.onStatus({J(isNew ? $"Bảng '{table}' chưa có trong App Data → bảng mới (script CREATE TABLE ở tab Script, không tự chạy)." : $"Đã nạp {cols.Count} cột của '{table}'.")}, 'ok')");
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không nạp được cột: " + ex.Message)}, 'err')"); }
    }

    private static QuickListSpec ReadSpec(JsonElement s) => QuickListService.FromPageJson(s);

    private List<(string Target, string Content)>? Render(QuickListSpec spec, string template, bool showErrors)
    {
        var error = QuickListService.Validate(spec);
        if (error != null)
        {
            if (showErrors) MessageBox.Show(this, error, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else Js($"quick.onStatus({J(error)}, 'err')");
            return null;
        }
        var files = _service.Render(spec, template, out var leftovers);
        Js(leftovers.Count == 0
            ? $"quick.onStatus({J($"{files.Count} file.")}, 'ok')"
            : $"quick.onStatus({J("Mẫu có placeholder chưa biết (đã để trống): " + string.Join(", ", leftovers))}, 'err')");
        return files;
    }

    private void Preview(JsonElement msg)
    {
        var template = (msg.GetProperty("template").GetString() ?? "").Trim();
        try
        {
            var files = Render(ReadSpec(msg.GetProperty("spec")), template, showErrors: false);
            if (files != null) Js($"quick.onPreview({J(files.Select(f => new { target = f.Target, content = f.Content }))})");
        }
        catch (Exception ex) { Js($"quick.onStatus({J(ex.Message)}, 'err')"); }
    }

    /// <summary>Gợi ý header cho cột danh mục + chi tiết (xem <see cref="ColumnHeaderGuesser"/>); trang chỉ áp vào ô người dùng chưa sửa.</summary>
    private void GuessHeaders(JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) ? (p.GetString() ?? "").Trim() : "";
        string[] Names(string n) => msg.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : Array.Empty<string>();

        // Chi tiết dùng chung ngữ cảnh danh mục: cột liên kết (vd ma_quay) trong bảng chi tiết vẫn là "Mã quầy".
        var ctx = new ColumnHeaderGuesser.Context(S("table"),
            S("keys").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), S("titleV"), S("titleE"));
        object Map(string[] names) => names
            .Select(n => (n, g: _headers.Guess(n, ctx)))
            .Where(x => x.g != null)
            .Select(x => new { name = x.n, v = x.g!.Value.V, e = x.g!.Value.E });
        Js($"quick.onHeaders({J(new { master = Map(Names("master")), detail = Map(Names("detail")) })})");
    }

    /// <summary>Nút "Dịch": dịch các ô tiếng Việt trang gửi lên (id ô đích → chữ Việt) sang tiếng Anh — cùng hàm dịch với form tạo command.</summary>
    private async Task TranslateAsync(JsonElement msg)
    {
        var items = msg.GetProperty("items").EnumerateArray()
            .Select(i => (Id: i.GetProperty("id").GetString() ?? "", Text: (i.GetProperty("text").GetString() ?? "").Trim()))
            .Where(i => i.Id.Length > 0 && i.Text.Length > 0).ToList();
        string? error = null;
        var map = new Dictionary<string, string>();
        foreach (var (id, text) in items)
        {
            var (result, err) = await GoogleTranslateService.TranslateAsync(text);
            if (err != null) { error = err; break; }
            map[id] = result ?? "";
        }
        Js($"quick.onTranslated({J(map)}, {J(error ?? "")})");
    }

    // ---- Sau khi tạo file -----------------------------------------------------------------------

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không mở được thư mục: " + ex.Message)}, 'err')"); }
    }

    /// <summary>Mở 1 file trong BcodeViewer — cùng cách File Lookup "Edit" (hỏi đường dẫn BcodeViewer.exe 1 lần, nhớ vào settings).</summary>
    private void OpenInViewer(string path)
    {
        if (!File.Exists(path)) { Js($"quick.onStatus({J("Không thấy file: " + path)}, 'err')"); return; }
        if (string.IsNullOrWhiteSpace(_settings.ViewerExePath) || !File.Exists(_settings.ViewerExePath))
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Chọn BcodeViewer.exe",
                Filter = "BcodeViewer (BcodeViewer.exe)|BcodeViewer.exe|Tất cả file (*.exe)|*.exe"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _settings.ViewerExePath = dialog.FileName;
            _settings.Save();
        }
        try
        {
            var project = _connections.Current?.Name ?? "";
            var arguments = project.Length == 0 ? $"\"{path}\"" : $"\"{path}\" \"{project}\"";
            Process.Start(new ProcessStartInfo(_settings.ViewerExePath, arguments) { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, "Không mở được BcodeViewer:\n" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    // ---- Bước tạo menu (wcommand) ------------------------------------------------------------

    private async Task<List<WCommandItem>> MenusAsync()
    {
        if (_menus != null) return _menus;
        var flat = new List<WCommandItem>();
        void Walk(IEnumerable<WCommandItem> items) { foreach (var x in items) { flat.Add(x); Walk(x.Children); } }
        Walk(await _wcommand.LoadTreeAsync());
        return _menus = flat.Where(x => !x.IsAppCommand).ToList();
    }

    /// <summary>Danh sách menu web (wcommand) để chọn "menu mẫu": menu mới chép toàn bộ thông tin của nó (menu cha, type, icon...).</summary>
    private async Task LoadMenusAsync(bool reload = false)
    {
        try
        {
            if (reload) _menus = null;
            var menus = await MenusAsync();
            Js($"quick.onMenus({J(menus.OrderBy(m => m.WMenuId, StringComparer.Ordinal).Select(m => new
            {
                id = m.WMenuId, parent = m.WMenuId0, bar = m.Bar, bar2 = m.Bar2, link = m.Link, menuId = m.MenuId,
                type = m.Type, icon = m.Icon, sysId = m.SysId,
            }))})");
            if (menus.Count == 0) Js($"quick.onStatus({J("Sản phẩm này không có menu web (wcommand).")}, 'err')");
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không đọc được wcommand: " + ex.Message)}, 'err')"); }
    }

    /// <summary>
    /// Mở form WCOMMAND có sẵn ở chế độ New, điền trước từ menu mẫu (cùng menu cha, type, icon...) + tên/link/sysid của danh mục
    /// vừa khai báo; form tự gợi ý WMenu Id / Menu Id còn trống. Chưa ghi gì vào database — người dùng bấm Save trong form đó mới ghi.
    /// Đã có menu trỏ tới controller / file Main này thì hỏi mở menu đó để sửa thay vì tạo trùng.
    /// </summary>
    private async Task CreateMenuAsync(JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) ? (p.GetString() ?? "").Trim() : "";
        var controller = S("controller");
        var main = S("main");
        if (controller.Length == 0 || main.Length == 0) { Js($"quick.onStatus({J("Nhập Controller và File Main trước khi tạo menu.")}, 'err')"); return; }

        try
        {
            var found = await ExistingMenusAsync(controller, main);
            var menus = await MenusAsync();
            var templateId = S("templateId");
            var source = menus.FirstOrDefault(m => m.WMenuId.Equals(templateId, StringComparison.OrdinalIgnoreCase));
            if (templateId.Length > 0 && source == null) { Js($"quick.onStatus({J($"Không thấy menu mẫu '{templateId}'.")}, 'err')"); return; }

            BeginInvoke(new Action(() =>
            {
                WCommandItem? existing = null;
                if (found.Count > 0)
                {
                    var list = string.Join("\n", found.Select(m => $"{m.WMenuId}  {m.Bar}  ({m.Link})"));
                    var answer = MessageBox.Show(this, $"Đã có menu trỏ tới danh mục này:\n\n{list}\n\nYes = mở menu đó để sửa\nNo = vẫn tạo menu mới",
                        Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (answer == DialogResult.Cancel) return;
                    if (answer == DialogResult.Yes) existing = found[0];
                }

                WCommandItem? template = null;
                if (existing == null)
                {
                    template = source == null ? new WCommandItem { Status = "1" } : Clone(source);
                    template.Bar = S("titleV");
                    template.Bar2 = S("titleE");
                    template.Link = main + ".aspx";
                    template.SysId = controller;
                    template.Parameter = "";
                    // Trang đã tính menu cha (chọn 1 nhóm = đặt menu mới bên trong nhóm đó; chọn 1 menu = cùng cha với nó).
                    if (msg.TryGetProperty("parentId", out var pid) && pid.ValueKind == JsonValueKind.String)
                        template.WMenuId0 = pid.GetString() ?? "";
                }
                using var form = new WCommandEditForm(_wcommand, existing, template, _connections.Current?.SourcePath);
                form.ShowDialog(this);
                _menus = null; // có thể vừa thêm/sửa menu — lần sau đọc lại
                Js("quick.onMenuFormClosed()");
            }));
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không tạo được menu: " + ex.Message)}, 'err')"); }
    }

    private async Task<List<WCommandItem>> ExistingMenusAsync(string controller, string main)
    {
        var found = new List<WCommandItem>();
        if (controller.Length > 0) found.AddRange(await _wcommand.FindByControllerAsync(controller));
        if (main.Length > 0 && !main.Equals(controller, StringComparison.OrdinalIgnoreCase)) found.AddRange(await _wcommand.FindByControllerAsync(main));
        return found.GroupBy(m => m.WMenuId).Select(g => g.First()).ToList();
    }

    /// <summary>Menu đã trỏ tới danh mục này (sysid = controller hoặc link = &lt;main&gt;.aspx...) — để tab Menu báo trước, tránh tạo trùng.</summary>
    private async Task FindMenusAsync(JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) ? (p.GetString() ?? "").Trim() : "";
        try
        {
            var found = await ExistingMenusAsync(S("controller"), S("main"));
            Js($"quick.onExistingMenus({J(found.Select(m => new { id = m.WMenuId, parent = m.WMenuId0, bar = m.Bar, link = m.Link, menuId = m.MenuId, sysId = m.SysId }))})");
        }
        catch (Exception ex) { Js($"quick.onExistingMenus([], {J("Không tra được wcommand: " + ex.Message)})"); }
    }

    /// <summary>Mở 1 menu có sẵn trong form WCOMMAND ở chế độ Edit.</summary>
    private async Task EditMenuAsync(string id)
    {
        try
        {
            var menu = (await MenusAsync()).FirstOrDefault(m => m.WMenuId.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (menu == null) { Js($"quick.onStatus({J($"Không thấy menu '{id}'.")}, 'err')"); return; }
            BeginInvoke(new Action(() =>
            {
                using var form = new WCommandEditForm(_wcommand, menu, null, _connections.Current?.SourcePath);
                form.ShowDialog(this);
                _menus = null;
                Js("quick.onMenuFormClosed()");
            }));
        }
        catch (Exception ex) { Js($"quick.onStatus({J("Không mở được menu: " + ex.Message)}, 'err')"); }
    }

    private static WCommandItem Clone(WCommandItem m) => new()
    {
        WMenuId = m.WMenuId, WMenuId0 = m.WMenuId0, MenuId = m.MenuId, Bar = m.Bar, Bar2 = m.Bar2, Link = m.Link, Parameter = m.Parameter,
        IconUrl = m.IconUrl, Status = m.Status, Icon = m.Icon, SysId = m.SysId, Type = m.Type, SysCode = m.SysCode, Msys = m.Msys,
        Target = m.Target, XType = m.XType, Edition = m.Edition, ExplIcon = m.ExplIcon,
    };

    // ---- Kiểm tra chuẩn Fast + nhớ những gì bạn sửa -----------------------------------------------

    /// <summary>Soát spec trang đang khai theo chuẩn Fast (<see cref="FastCatalogCheck"/>) rồi gửi danh sách lưu ý về trang (nút "⚡ N lưu ý Fast").</summary>
    private async Task<List<FastIssue>> FastCheckAsync(JsonElement msg)
    {
        try
        {
            var spec = ReadSpec(msg.GetProperty("spec"));
            var output = msg.TryGetProperty("output", out var o) ? (o.GetString() ?? "").Trim() : "";
            var issues = await FastCatalogCheck.CheckAsync(spec, _tableSet, TypesAsync, output, _connections.Current?.SourcePath ?? "", _settings.NewCatalogTablePrefix);
            Js($"quick.onFastCheck({J(issues.Select(i => new { level = i.Level, el = i.El, msg = i.Message }))})");
            return issues;
        }
        catch { return new(); }   // chỉ là lưu ý — lỗi đọc thì bỏ qua, không chặn Tạo file
    }

    private async Task<IReadOnlyDictionary<string, string>> TypesAsync(string table)
    {
        if (_typeCache.TryGetValue(table, out var t)) return t;
        try { t = await _tableData.GetColumnTypesAsync(false, "dbo", table); }
        catch { t = new Dictionary<string, string>(); }
        return _typeCache[table] = t;
    }

    /// <summary>
    /// Cột AI / luật Fast gợi ý (trang gửi kèm "ai" = gợi ý gốc) mà bạn đã đổi tên field, kiểu hoặc tra cứu trước khi Tạo file
    /// → nhớ "tiêu đề gợi ý → field bạn chốt" (<see cref="CatalogFieldMemory"/>) để lần sau luật điền đúng luôn, AI được nhắc.
    /// </summary>
    private static int LearnEdits(JsonElement spec)
    {
        var learned = new List<(string, CatalogFieldMemory.Item)>();
        string Top(string n) => spec.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
        // Mã / tên / tên 2 / cột hệ thống của chính danh mục đổi theo từng danh mục — không phải "tiêu đề → field" dùng lại được.
        var own = new HashSet<string>(Top("keys").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase)
            { Top("nameField"), Top("nameField") + "2", "datetime0", "datetime2", "user_id0", "user_id2", "status", "line_nbr" };
        foreach (var prop in new[] { "columns", "detailColumns" })
        {
            if (!spec.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var c in arr.EnumerateArray())
            {
                if (!c.TryGetProperty("ai", out var ai) || ai.ValueKind != JsonValueKind.Object) continue;
                string S(JsonElement x, string n) => x.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
                var v = S(ai, "v");
                var name = S(c, "name");
                if (v.Length == 0 || name.Length == 0 || v == S(ai, "name") || own.Contains(S(ai, "name")) || own.Contains(name)) continue;
                var lk = c.TryGetProperty("lookup", out var l) && l.ValueKind == JsonValueKind.Object ? l : default;
                string L(string n) => lk.ValueKind == JsonValueKind.Object ? S(lk, n) : "";
                var sqlType = S(c, "sqlType");
                var changed = !name.Equals(S(ai, "name"), StringComparison.OrdinalIgnoreCase)
                    || !L("controller").Equals(S(ai, "ctl"), StringComparison.OrdinalIgnoreCase)
                    || (S(ai, "sqlType").Length > 0 && sqlType.Length > 0 && !Regex.Replace(sqlType, @"\s+", "").Equals(Regex.Replace(S(ai, "sqlType"), @"\s+", ""), StringComparison.OrdinalIgnoreCase));
                if (!changed) continue;
                learned.Add((v, new CatalogFieldMemory.Item
                {
                    Name = name, SqlType = sqlType, E = S(c, "headerE"), V = v,
                    Controller = L("controller"), Reference = L("reference"), Key = L("key"), Check = L("check"), Information = L("information"),
                }));
            }
        }
        return CatalogFieldMemory.Learn(learned);
    }

    private void Generate(JsonElement msg, IReadOnlyList<FastIssue> issues)
    {
        var template = (msg.GetProperty("template").GetString() ?? "").Trim();
        var output = (msg.GetProperty("output").GetString() ?? "").Trim();
        if (output.Length == 0) { MessageBox.Show(this, "Chưa chọn thư mục source để lưu.", Title, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }

        try
        {
            var spec = ReadSpec(msg.GetProperty("spec"));
            var files = Render(spec, template, showErrors: true);
            if (files == null) return;

            // Có lưu ý mức "warn" (dễ lỗi khi chạy / ghi đè nhầm) → hỏi trước; chỉ "info" thì không chặn.
            var warns = issues.Where(i => i.Level == "warn").ToList();
            if (warns.Count > 0)
            {
                var text = string.Join("\n", warns.Select(i => "⚠ " + i.Message))
                    + (issues.Count > warns.Count ? $"\n\n(+{issues.Count - warns.Count} lưu ý nhỏ — xem nút ⚡ ở thanh dưới)" : "");
                if (MessageBox.Show(this, "Kiểm tra theo chuẩn Fast:\n\n" + text + "\n\nVẫn tạo file?", Title,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            }

            var existing = files.Select(f => Path.Combine(output, f.Target)).Where(File.Exists).ToList();
            if (existing.Count > 0 &&
                MessageBox.Show(this, "Các file sau đã tồn tại, ghi đè?\n\n" + string.Join("\n", existing), "Xác nhận ghi đè",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            var written = _service.Write(files, output);
            _headers.Remember(spec.Columns.Concat(spec.DetailColumns));
            var learned = LearnEdits(msg.GetProperty("spec"));
            QuickListConfigStore.Save(WorkspaceKey, spec.Table, msg.GetProperty("spec"));
            Js($"quick.onStatus({J($"Đã tạo {written.Count} file." + (learned > 0 ? $" Đã nhớ {learned} cột bạn sửa so với gợi ý — lần sau luật Fast / AI điền theo." : ""))}, 'ok')");
            Js($"quick.onGenerated({J(new { folder = output, files = written.Select(p => new { path = p, name = Path.GetRelativePath(output, p) }) })})");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không tạo được file:\n" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
