using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Forms;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Chế độ "Từ procedure có sẵn" của cửa sổ Tạo báo cáo (<see cref="Forms.ReportStudioForm"/>), 3 bước: (1) chọn procedure → tham số thành ô lọc;
/// (2) chạy thử procedure → cột kết quả thành cột lưới; (3) Filter / Grid / Report / Main từ source mẫu <c>Templates\fileSource\CreateReport</c> + mẫu Excel,
/// xem khác biệt từng file rồi mới ghi vào source (bản cũ sao lưu trước). Gợi ý tiêu đề / tra cứu / định dạng lấy từ điển của chế độ "Thiết kế từ bảng".
/// Phần sinh file nằm trong <see cref="QuickReportService"/>; giao diện là trang WebView2 Web/Shell/quickreport.html.
/// </summary>
public class QuickReportControl : UserControl
{
    private const string Title = "Tạo báo cáo";
    private const string ConfigFolder = "quickreport";
    private const int SampleRows = 100;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebBarHost _web = new("quickreport.html");
    private readonly SqlObjectBrowserService _sqlObjects;
    private readonly DbConnectionService _connections;
    private readonly QuickReportService _service = new();
    private readonly ColumnHeaderGuesser _headers = new();
    private CancellationTokenSource? _runCts;
    private string? _pendingProc;
    private readonly WCommandService _wcommand;
    private List<WCommandItem>? _menus;

    /// <summary>Người dùng muốn thiết kế procedure mới (chưa có store) → cửa sổ chuyển sang chế độ "Thiết kế từ bảng".</summary>
    public event Action? DesignProcedureRequested;

    public QuickReportControl(SqlObjectBrowserService sqlObjects, DbConnectionService connections)
    {
        _sqlObjects = sqlObjects;
        _connections = connections;
        _wcommand = new WCommandService(connections);
        Dock = DockStyle.Fill;
        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Ready += OnReady;
        _web.Message += root =>
        {
            var copy = root.Clone();   // JsonElement chỉ hợp lệ trong handler
            var action = copy.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            _ = DispatchAsync(action, copy);
        };
        Disposed += (_, _) => { _runCts?.Cancel(); _runCts?.Dispose(); };
    }

    /// <summary>Nạp sẵn một procedure (vd vừa sinh ở chế độ Thiết kế từ bảng rồi chạy trong tab SQL).</summary>
    public void LoadProcedure(string proc)
    {
        if (!_web.IsReady) { _pendingProc = proc; return; }
        _ = LoadProcAsync(proc);
    }

    /// <summary>"Chuyển mẫu chạy thử" từ chế độ Thiết kế: nạp procedure (chưa tạo trong database thì chỉ điền tên và nhắc chạy script CREATE rồi Nạp lại) và điền sẵn tiêu đề báo cáo.</summary>
    public void LoadProcedure(string proc, string titleV, string titleE)
    {
        if (!_web.IsReady) { _pendingProc = proc; _pendingTitles = (titleV, titleE); return; }
        _ = LoadWithTitlesAsync(proc, titleV, titleE);
    }

    private (string V, string E)? _pendingTitles;

    private async Task LoadWithTitlesAsync(string proc, string titleV, string titleE)
    {
        await LoadProcAsync(proc);
        Js($"qr.setTitles({J(titleV)}, {J(titleE)})");
    }

    private Workspace? Ws => _connections.Current;
    private string WorkspaceKey => Ws is { } w ? (w.Name.Length > 0 ? w.Name : w.AppDatabase) : "";
    private static string J(object? value) => JsonSerializer.Serialize(value, JsonOpts);

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    private void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(a); else a();
    }

    private void OnReady()
    {
        Js($"qr.init({J(new { template = QuickReportService.DefaultTemplateDir, output = Ws?.SourcePath ?? "" })})");
        _ = LoadProcsAsync();
        if (_pendingProc is { } p)
        {
            _pendingProc = null;
            if (_pendingTitles is { } t) { _pendingTitles = null; _ = LoadWithTitlesAsync(p, t.V, t.E); } else _ = LoadProcAsync(p);
        }
        _ = Task.Run(() =>
        {
            if (FieldDictionaryService.Instance.LoadError is { } err)
                Js($"qr.onStatus({J("Không đọc được từ điển field (header.xml / parameters.xml): " + err)}, 'err')");
        });
    }

    private async Task DispatchAsync(string action, JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
        try
        {
            switch (action)
            {
                case "loadProc": await LoadProcAsync(S("proc")); break;
                case "fieldInfo": FieldInfo(S("field"), S("name")); break;
                case "run": await RunAsync(msg); break;
                case "cancelRun": _runCts?.Cancel(); break;
                case "preview": Preview(msg); break;
                case "plan": Ui(() => Plan(msg)); break;
                case "deploy": Ui(() => Deploy(msg)); break;
                case "translate": Translate(msg); break;
                case "loadMenus": await LoadMenusAsync(msg.TryGetProperty("reload", out var rl) && rl.ValueKind == JsonValueKind.True); break;
                case "findMenus": await FindMenusAsync(S("controller"), S("main")); break;
                case "editMenu": await EditMenuAsync(S("id")); break;
                case "createMenu": await CreateMenuAsync(msg); break;
                case "designProc": Ui(() => DesignProcedureRequested?.Invoke()); break;
                case "pickFolder":
                {
                    var target = S("target"); var current = S("path");
                    Ui(() =>
                    {
                        using var dlg = new FolderBrowserDialog { SelectedPath = Directory.Exists(current) ? current : "" };
                        if (dlg.ShowDialog(FindForm()) == DialogResult.OK) Js($"qr.onFolder({J(target)}, {J(dlg.SelectedPath)})");
                    });
                    break;
                }
                case "openFolder": OpenFolder(S("path")); break;
                case "copy":
                    Ui(() =>
                    {
                        try { Clipboard.SetText(msg.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : ""); Js($"qr.onStatus({J("Đã copy nội dung file.")}, 'ok')"); }
                        catch { /* clipboard bận */ }
                    });
                    break;
            }
        }
        catch (Exception ex) { Js($"qr.onStatus({J(ex.Message)}, 'err')"); }
    }

    // ---- Bước 1: procedure → ô lọc ----------------------------------------------------------------

    private async Task LoadProcsAsync()
    {
        if (Ws is null) { Js($"qr.onStatus({J("Chưa chọn workspace.")}, 'err')"); return; }
        try
        {
            var objs = await _sqlObjects.ListObjectsAsync(useSysDatabase: false);
            var names = objs.Where(o => o.Kind == SqlObjectKind.StoredProcedure).Select(o => o.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            Js($"qr.onProcs({J(names)})");
            Js($"qr.onStatus({J($"{names.Length} procedure trong App Data. Gõ tên procedure báo cáo rồi Enter — chưa có thì bấm “＋ Thiết kế procedure mới…”.")}, '')");
        }
        catch (Exception ex) { Js($"qr.onStatus({J("Không nạp được danh sách procedure: " + ex.Message)}, 'err')"); }
    }

    private async Task LoadProcAsync(string proc)
    {
        if (proc.Length == 0) { Js($"qr.onStatus({J("Nhập tên procedure.")}, 'err')"); return; }
        if (Ws is null) { Js($"qr.onStatus({J("Chưa chọn workspace.")}, 'err')"); return; }
        try
        {
            await using var conn = _connections.CreateConnection(false);
            await conn.OpenAsync();
            await using (var chk = new Microsoft.Data.SqlClient.SqlCommand("select object_name(object_id(@p)), objectproperty(object_id(@p), 'IsProcedure')", conn))
            {
                chk.Parameters.AddWithValue("@p", proc);
                await using var rd = await chk.ExecuteReaderAsync();
                await rd.ReadAsync();
                if (rd.IsDBNull(0) || rd.IsDBNull(1) || rd.GetInt32(1) != 1)
                {
                    Js($"qr.setProc({J(proc)})");
                    Js($"qr.onStatus({J($"Chưa có procedure '{proc}' trong App Data — nếu vừa sinh ở “Thiết kế từ bảng” thì chạy script CREATE trong tab SQL (F5) rồi Nạp lại.")}, 'err')");
                    return;
                }
                proc = rd.GetString(0);   // đúng hoa/thường như trong database
            }
            var ctx = new ColumnHeaderGuesser.Context(proc, Array.Empty<string>(), "", "");
            var ps = (await QuickReportService.ReadParamsAsync(conn, proc)).Select(x =>
            {
                var p = QuickReportService.SuggestParam(x.Name, x.SqlType, x.IsOutput);
                if (p.IsFilter && p.HeaderV.Length == 0 && _headers.Guess(p.Field, ctx) is { } g) { p.HeaderV = g.V; p.HeaderE = QuickReportService.English(g.V, g.E); }
                return ParamDto(p);
            }).ToList();
            var saved = QuickListConfigStore.Load(WorkspaceKey, proc, ConfigFolder);
            Js($"qr.onProc({J(new { proc, controller = DefaultController(proc), @params = ps, saved })})");
            Js($"qr.onStatus({J($"{proc}: {ps.Count} tham số. Kiểm tra ô lọc, nhập giá trị thử rồi bấm “▶ Chạy store”.")}, 'ok')");
        }
        catch (Exception ex) { Js($"qr.onStatus({J("Không đọc được procedure: " + ex.Message)}, 'err')"); }
    }

    /// <summary>zrs_O067 → zrpt_O067 (quy ước của chế độ Thiết kế từ bảng); tên khác giữ nguyên (procedure FBO thường trùng tên controller).</summary>
    private static string DefaultController(string proc) =>
        proc.StartsWith("zrs_", StringComparison.OrdinalIgnoreCase) ? "zrpt_" + proc[4..] : proc;

    private static object ParamDto(QuickReportParam p) => new
    {
        name = p.Name, sqlType = p.SqlType, role = p.Role, arg = p.Arg, field = p.Field, headerV = p.HeaderV, headerE = p.HeaderE, type = p.Type, test = p.Test,
        lookup = p.Lookup is { } l ? LookupDto(l) : null,
        lookups = p.Field.Length > 0 ? QuickReportService.Lookups(p.Field).Select(LookupDto).ToList() : new List<object>(),
    };

    private static object LookupDto(FieldLookup l) => new { controller = l.Controller, reference = l.Reference, key = l.Key, check = l.Check, information = l.Information };

    /// <summary>Đổi tên field lọc → header + các cách tra cứu chuẩn của field mới.</summary>
    private void FieldInfo(string field, string param)
    {
        var h = QuickReportService.FilterHeader(field) ?? _headers.Guess(field, new ColumnHeaderGuesser.Context("", Array.Empty<string>(), "", ""));
        Js($"qr.onFieldInfo({J(new { name = param, field, headerV = h?.V ?? "", headerE = h is { } x ? QuickReportService.English(x.V, x.E) : "", lookups = QuickReportService.Lookups(field).Select(LookupDto) })})");
    }

    /// <summary>Nút "Dịch V → E": dịch các tiêu đề Việt trang gửi lên (bộ dịch chạy trên máy, xem <see cref="HeaderTranslator"/>).</summary>
    private void Translate(JsonElement msg)
    {
        var items = msg.TryGetProperty("items", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Trim().Length > 0).Distinct().ToList() : new List<string>();
        var map = new Dictionary<string, string>();
        var missing = new List<string>();
        foreach (var v in items)
            if (HeaderTranslator.Instance.Translate(v) is { } e) map[v] = e; else missing.Add(v);
        Js($"qr.onTranslate({J(new { map, missing, all = msg.TryGetProperty("all", out var al) && al.ValueKind == JsonValueKind.True })})");
    }

    // ---- Bước 2: chạy procedure → cột lưới --------------------------------------------------------

    private async Task RunAsync(JsonElement msg)
    {
        var spec = ReadSpec(msg.GetProperty("spec"));
        if (spec.ProcName.Length == 0) { Js($"qr.onStatus({J("Chưa nạp procedure.")}, 'err')"); return; }
        _runCts?.Cancel();
        var cts = _runCts = new CancellationTokenSource();
        Js("qr.onRunStart()");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var conn = _connections.CreateConnection(false);
            await conn.OpenAsync(cts.Token);
            var sets = await QuickReportService.RunAsync(conn, spec, Ws?.SysDatabase ?? "", SampleRows, cts.Token);
            if (!ReferenceEquals(_runCts, cts)) return;   // đã có lần chạy mới hơn
            var ctx = new ColumnHeaderGuesser.Context(spec.ProcName, Array.Empty<string>(), spec.TitleV, spec.TitleE);
            Js($"qr.onRun({J(new
            {
                ms = sw.ElapsedMilliseconds,
                sets = sets.Select(s => new
                {
                    columns = s.Columns.Select(c =>
                    {
                        var col = QuickReportService.SuggestColumn(c.Name, c.Type);
                        if (col.HeaderV.Length == 0 && _headers.Guess(c.Name, ctx) is { } g) { col.HeaderV = g.V; col.HeaderE = QuickReportService.English(g.V, g.E); }
                        return col;
                    }),
                    rows = s.Rows, truncated = s.Truncated,
                }),
            })})");
        }
        catch (Exception) when (cts.IsCancellationRequested) { if (ReferenceEquals(_runCts, cts)) Js($"qr.onRun({J(new { error = "Đã dừng chạy thử." })})"); }
        catch (Exception ex) { Js($"qr.onRun({J(new { error = ex.Message })})"); }
    }

    // ---- Bước 3: sinh file từ source mẫu, xem khác biệt, ghi -----------------------------------------

    private static QuickReportSpec ReadSpec(JsonElement s)
    {
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
        bool B(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.True;
        int I(JsonElement e, string n, int d)
        {
            if (!e.TryGetProperty(n, out var p)) return d;
            if (p.ValueKind == JsonValueKind.Number) return p.TryGetInt32(out var i) ? i : d;
            return p.ValueKind == JsonValueKind.String && int.TryParse(p.GetString(), out var j) ? j : d;
        }
        IEnumerable<JsonElement> Arr(string n) => s.TryGetProperty(n, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray() : Enumerable.Empty<JsonElement>();

        return new QuickReportSpec
        {
            ProcName = S(s, "proc"), Controller = S(s, "controller"), MainName = S(s, "main"), TitleV = S(s, "titleV"), TitleE = S(s, "titleE"),
            ResultSet = I(s, "resultSet", 0), Excel = B(s, "excel"),
            Params = Arr("params").Select(p => new QuickReportParam
            {
                Name = S(p, "name"), SqlType = S(p, "sqlType"), Role = S(p, "role") == "system" ? "system" : "filter", Arg = S(p, "arg"),
                Field = S(p, "field"), HeaderV = S(p, "headerV"), HeaderE = S(p, "headerE"), Type = S(p, "type"),
                Test = p.TryGetProperty("test", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "",
                Lookup = p.TryGetProperty("lookup", out var l) && l.ValueKind == JsonValueKind.Object && S(l, "controller") != ""
                    ? new FieldLookup(S(l, "controller"), S(l, "reference"), S(l, "key"), S(l, "check"), S(l, "information")) : null,
            }).ToList(),
            Columns = Arr("columns").Select(c => new QuickReportColumn
            {
                Name = S(c, "name"), HeaderV = S(c, "headerV"), HeaderE = S(c, "headerE"), Type = S(c, "type"), Width = I(c, "width", 120), Format = S(c, "format"),
                Sum = B(c, "sum"), InGrid = B(c, "inGrid"), Hidden = B(c, "hidden"),
            }).ToList(),
        };
    }

    private sealed record Built(string Kind, string Target, byte[] Bytes, string? Text);

    /// <summary>Kiểm tra + dựng toàn bộ file (chưa ghi). Lỗi đặc tả → báo trên thanh trạng thái, trả null.</summary>
    private List<Built>? Build(JsonElement msg, out QuickReportSpec spec)
    {
        spec = ReadSpec(msg.GetProperty("spec"));
        var error = QuickReportService.Validate(spec);
        if (error != null) { Js($"qr.onStatus({J(error)}, 'err')"); return null; }
        var template = msg.TryGetProperty("template", out var t) ? (t.GetString() ?? "").Trim() : "";
        var kinds = new[] { "Filter", "Grid", "Report", "Main" };
        var files = _service.Render(spec, template.Length > 0 ? template : QuickReportService.DefaultTemplateDir, out var leftovers)
            .Select((f, i) => new Built(kinds[i], f.Target, QuickReportService.TextBytes(f.Content), f.Content)).ToList();
        if (spec.Excel) files.Add(new Built("Excel", QuickReportService.ExcelTarget(spec), QuickReportService.ExcelBytes(spec), null));
        Js(leftovers.Count == 0
            ? $"qr.onStatus({J($"{files.Count} file.")}, 'ok')"
            : $"qr.onStatus({J("Mẫu có placeholder chưa biết (đã để trống): " + string.Join(", ", leftovers))}, 'warn')");
        return files;
    }

    private void Preview(JsonElement msg)
    {
        var files = Build(msg, out var spec);
        if (files == null) return;
        // file chữ: nội dung; mẫu Excel: bố cục sheet (ô, gộp ô, độ rộng cột) để trang vẽ lại như Excel
        Js($"qr.onPreview({J(files.Select(f => new
        {
            kind = f.Kind, target = f.Target, size = f.Bytes.Length, content = f.Text,
            sheet = f.Kind == "Excel" ? QuickReportService.ExcelLayout(spec) : null,
        }))})");
    }

    private static string OutputOf(JsonElement msg) => msg.TryGetProperty("output", out var o) ? (o.GetString() ?? "").Trim() : "";

    /// <summary>"Lưu vào source…": so từng file với bản đang có (mới / trùng hệt / ghi đè + diff) để người dùng chọn file nào ghi — giống chế độ Thiết kế từ bảng.</summary>
    private void Plan(JsonElement msg)
    {
        var files = Build(msg, out _);
        if (files == null) return;
        var output = OutputOf(msg);
        var (problem, plan) = QuickReportService.Plan(output, files.Select(f => (f.Kind, f.Target, f.Bytes, f.Text != null)).ToList());
        Js($"qr.onPlan({J(new { problem, root = output, files = plan })})");
    }

    private void Deploy(JsonElement msg)
    {
        var files = Build(msg, out var spec);
        if (files == null) return;
        var output = OutputOf(msg);
        var kinds = msg.TryGetProperty("kinds", out var k) && k.ValueKind == JsonValueKind.Array
            ? k.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : new HashSet<string>();
        var pick = files.Where(f => kinds.Contains(f.Kind)).ToList();
        if (pick.Count == 0) { Js($"qr.onStatus({J("Chưa chọn file nào để ghi.")}, 'err')"); return; }
        try
        {
            var backup = Path.Combine(BcodePaths.AppData, "Bcode", "quickreport-backup", DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + spec.Controller);
            var existed = pick.Count(f => File.Exists(Path.Combine(output, f.Target)));
            var written = QuickReportService.Write(pick.Select(f => (f.Target, f.Bytes)), output, backup);
            _headers.Remember(spec.Columns.Where(c => c.HeaderV.Length > 0).Select(c => new CatalogColumn { Name = c.Name, HeaderV = c.HeaderV, HeaderE = c.HeaderE }));
            QuickListConfigStore.Save(WorkspaceKey, spec.ProcName, msg.GetProperty("spec"), ConfigFolder);
            Js($"qr.onStatus({J($"Đã ghi {written.Count} file vào source." + (existed > 0 ? " Bản cũ sao lưu ở " + backup : ""))}, 'ok')");
            Js($"qr.onGenerated({J(new { folder = output, backup = existed > 0 ? backup : "", files = written.Select(p => new { path = p, name = Path.GetRelativePath(output, p) }) })})");
        }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(), "Không ghi được file:\n" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ---- Menu (wcommand): chọn vị trí trong cây menu rồi mở form WCOMMAND điền sẵn — cùng cách "Tạo nhanh danh mục" ----------------

    private async Task<List<WCommandItem>> MenusAsync()
    {
        if (_menus != null) return _menus;
        var flat = _wcommand.LoadCachedFlat();                       // bản lưu menu của Bcode: gần như tức thì
        if (flat is null || flat.Count == 0)
        {
            flat = new List<WCommandItem>();
            void Walk(IEnumerable<WCommandItem> items) { foreach (var x in items) { flat.Add(x); Walk(x.Children); } }
            Walk(await _wcommand.LoadTreeAsync());
        }
        return _menus = flat.Where(x => !x.IsAppCommand).ToList();
    }

    private async Task LoadMenusAsync(bool reload)
    {
        try
        {
            if (reload) _menus = null;
            var menus = await MenusAsync();
            Js($"qr.onMenus({J(menus.OrderBy(m => m.WMenuId, StringComparer.Ordinal).Select(m => new
            {
                id = m.WMenuId, parent = m.WMenuId0, bar = m.Bar, bar2 = m.Bar2, link = m.Link, menuId = m.MenuId, type = m.Type, icon = m.Icon, sysId = m.SysId,
            }))})");
            if (menus.Count == 0) Js($"qr.onStatus({J("Sản phẩm này không có menu web (wcommand).")}, 'err')");
        }
        catch (Exception ex) { Js($"qr.onStatus({J("Không đọc được wcommand: " + ex.Message)}, 'err')"); }
    }

    private async Task<List<WCommandItem>> ExistingMenusAsync(string controller, string main)
    {
        var found = new List<WCommandItem>();
        if (controller.Length > 0) found.AddRange(await _wcommand.FindByControllerAsync(controller));
        if (main.Length > 0 && !main.Equals(controller, StringComparison.OrdinalIgnoreCase)) found.AddRange(await _wcommand.FindByControllerAsync(main));
        return found.GroupBy(m => m.WMenuId).Select(g => g.First()).ToList();
    }

    /// <summary>Menu đã trỏ tới báo cáo này (sysid = controller hoặc link = &lt;main&gt;.aspx) — báo trước để khỏi tạo trùng.</summary>
    private async Task FindMenusAsync(string controller, string main)
    {
        try
        {
            var found = await ExistingMenusAsync(controller, main);
            Js($"qr.onExistingMenus({J(found.Select(m => new { id = m.WMenuId, parent = m.WMenuId0, bar = m.Bar, link = m.Link, menuId = m.MenuId, sysId = m.SysId }))})");
        }
        catch (Exception ex) { Js($"qr.onExistingMenus([], {J("Không tra được wcommand: " + ex.Message)})"); }
    }

    /// <summary>Mở form WCOMMAND (New) điền trước từ menu mẫu + tên / link / sysid của báo cáo; form tự gợi ý WMenu Id / Menu Id còn trống và chỉ ghi database khi bấm Save.</summary>
    private async Task CreateMenuAsync(JsonElement msg)
    {
        string S(string n) => msg.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "").Trim() : "";
        var controller = S("controller"); var main = S("main");
        if (controller.Length == 0 || main.Length == 0) { Js($"qr.onStatus({J("Nhập Controller và File Main trước khi tạo menu.")}, 'err')"); return; }
        try
        {
            var found = await ExistingMenusAsync(controller, main);
            var menus = await MenusAsync();
            var templateId = S("templateId");
            var source = menus.FirstOrDefault(m => m.WMenuId.Equals(templateId, StringComparison.OrdinalIgnoreCase));
            if (templateId.Length > 0 && source == null) { Js($"qr.onStatus({J($"Không thấy menu mẫu '{templateId}'.")}, 'err')"); return; }
            Ui(() =>
            {
                WCommandItem? existing = null;
                if (found.Count > 0)
                {
                    var list = string.Join("\n", found.Select(m => $"{m.WMenuId}  {m.Bar}  ({m.Link})"));
                    var answer = MessageBox.Show(FindForm(), $"Đã có menu trỏ tới báo cáo này:\n\n{list}\n\nYes = mở menu đó để sửa\nNo = vẫn tạo menu mới", Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (answer == DialogResult.Cancel) return;
                    if (answer == DialogResult.Yes) existing = found[0];
                }
                WCommandItem? template = null;
                if (existing == null)
                {
                    template = source == null ? new WCommandItem { Status = "1" } : CloneMenu(source);
                    template.Bar = S("titleV"); template.Bar2 = S("titleE"); template.Link = main + ".aspx"; template.SysId = controller; template.Parameter = "";
                    if (msg.TryGetProperty("parentId", out var pid) && pid.ValueKind == JsonValueKind.String) template.WMenuId0 = pid.GetString() ?? "";
                }
                using var form = new WCommandEditForm(_wcommand, existing, template, _connections.Current?.SourcePath);
                form.ShowDialog(FindForm());
                _menus = null;                                         // có thể vừa thêm / sửa menu — lần sau đọc lại
                Js("qr.onMenuFormClosed()");
            });
        }
        catch (Exception ex) { Js($"qr.onStatus({J("Không tạo được menu: " + ex.Message)}, 'err')"); }
    }

    private async Task EditMenuAsync(string id)
    {
        try
        {
            var menu = (await MenusAsync()).FirstOrDefault(m => m.WMenuId.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (menu == null) { Js($"qr.onStatus({J($"Không thấy menu '{id}'.")}, 'err')"); return; }
            Ui(() =>
            {
                using var form = new WCommandEditForm(_wcommand, menu, null, _connections.Current?.SourcePath);
                form.ShowDialog(FindForm());
                _menus = null;
                Js("qr.onMenuFormClosed()");
            });
        }
        catch (Exception ex) { Js($"qr.onStatus({J("Không mở được menu: " + ex.Message)}, 'err')"); }
    }

    private static WCommandItem CloneMenu(WCommandItem m) => new()
    {
        WMenuId = m.WMenuId, WMenuId0 = m.WMenuId0, MenuId = m.MenuId, Bar = m.Bar, Bar2 = m.Bar2, Link = m.Link, Parameter = m.Parameter,
        IconUrl = m.IconUrl, Status = m.Status, Icon = m.Icon, SysId = m.SysId, Type = m.Type, SysCode = m.SysCode, Msys = m.Msys,
        Target = m.Target, XType = m.XType, Edition = m.Edition, ExplIcon = m.ExplIcon,
    };

    private void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Js($"qr.onStatus({J("Không mở được thư mục: " + ex.Message)}, 'err')"); }
    }
}
