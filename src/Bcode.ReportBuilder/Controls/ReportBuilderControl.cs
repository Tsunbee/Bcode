using System.Text.Json;
using Bcode.App.Services.Rpt.Builder;

namespace Bcode.ReportBuilder;

/// <summary>
/// Tab "Tạo báo cáo": người dùng không cần biết code chọn bảng, kéo trường vào các ô (Cột / Hàng / Giá trị / Bộ lọc) như Power BI; Bcode sinh sẵn procedure
/// (hiện ngay khi thao tác), Filter, Grid (bảng hoặc pivot), Report (Excel), Main và mẫu Excel — rồi cho xem trước form lọc + lưới, chạy thử trên dữ liệu thật,
/// và lưu vào source (diff + backup). Giao diện là trang WebView2 (Web/Shell/reportbuilder.html); mọi logic sinh nằm ở <see cref="ReportGenerator"/> và các service
/// trong Services/Rpt/Builder. Procedure chỉ được SINH (mở trong tab SQL để bạn xem rồi tự chạy) — Bcode không tự tạo procedure trong database.
/// </summary>
public class ReportBuilderControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private readonly IWebPage _web;
    private readonly IReportHost _host;
    private readonly ReportGenerator _gen = new();
    private readonly ReportMetaService _meta;
    private readonly ExistingReportService _existing;
    private readonly ReportFilesDeployService _deploy = new();
    private ReportBuildResult? _last;
    private ReportSpec? _lastSpec;
    private CancellationTokenSource? _runCts;

    /// <summary>(script, dùng Sys Data, tiêu đề tab) — mở procedure / script menu trong tab SQL.</summary>
    public event Action<string, bool, string>? OpenSqlRequested;

    public ReportBuilderControl(IReportHost host, string pageHtml)
    {
        _host = host;
        _meta = new ReportMetaService(host.CreateConnection);
        _existing = new ExistingReportService(host.CreateConnection, () => host.SourcePath, _meta, host.GetMenuAsync);
        _web = host.CreatePage("reportbuilder.html", pageHtml);
        _web.View.Dock = DockStyle.Fill;
        Dock = DockStyle.Fill;
        Controls.Add(_web.View);
        _web.Message += root => _ = HandleAsync(root.GetRawText());
        _web.Ready += () => _ = InitAsync();
        Disposed += (_, _) => { _runCts?.Cancel(); _runCts?.Dispose(); };
    }

    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    private static string Str(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static bool Bool(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    private ReportSpec ReadSpec(JsonElement r) =>
        JsonSerializer.Deserialize<ReportSpec>(r.GetProperty("spec").GetRawText(), JsonOpts) ?? new ReportSpec();

    // ---- khởi tạo ----
    private async Task InitAsync()
    {
        Js($"rb.init({J(new { workspace = _host.WorkspaceName, hasSource = !string.IsNullOrWhiteSpace(_host.SourcePath), catalog = new { filters = ReportCatalog.Instance.FilterCount, grid = ReportCatalog.Instance.GridCount } })})");
        SendDrafts();
        try
        {
            var tables = await _meta.ListTablesAsync();
            Js($"rb.onTables({J(tables)})");
        }
        catch (Exception ex) { Js($"rb.onError({J("Không đọc được danh sách bảng: " + ex.Message)})"); }
    }

    // ---- bản nháp (lưu cấu hình đang làm để mở lại sửa) ----
    private static string DraftDir => Path.Combine(ReportBuilderEnv.AppData, "Bcode", "report-drafts");

    private void SendDrafts()
    {
        try
        {
            var list = Directory.Exists(DraftDir)
                ? Directory.GetFiles(DraftDir, "*.json").OrderByDescending(File.GetLastWriteTime).Select(f => new { name = Path.GetFileNameWithoutExtension(f), at = File.GetLastWriteTime(f) }).ToList()
                : new();
            Js($"rb.onDrafts({J(list)})");
        }
        catch { /* không đọc được thư mục nháp */ }
    }

    private async Task HandleAsync(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            switch (r.GetProperty("action").GetString())
            {
                case "columns": await ColumnsAsync(Str(r, "table"), Bool(r, "partitioned"), Str(r, "tag")); break;
                case "joins":
                {
                    var cols = r.GetProperty("columns").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
                    Js($"rb.onJoins({J(Str(r, "alias"))}, {J(await _meta.SuggestJoinsAsync(cols))})");
                    break;
                }
                case "listReports":
                {
                    var list = await _existing.ListAsync();
                    Js($"rb.onReports({J(list)})");                                                    // hiện ngay (chưa phân loại)
                    _ = Task.Run(() => { try { var kinds = _existing.WithKinds(list); Js($"rb.onReports({J(kinds)})"); } catch { /* không đọc được Source: giữ danh sách đầy đủ */ } });
                    break;
                }
                case "loadReport":
                {
                    var an = await _existing.AnalyzeAsync(Str(r, "controller"), Str(r, "link"), Str(r, "title"));
                    Js($"rb.onExisting({J(new { controller = an.Controller, mainFile = an.MainFile, procName = an.ProcName, error = an.Error, encrypted = an.Encrypted, pivot = an.Pivot, tables = an.Tables, mapped = an.Mapped, unmapped = an.Unmapped, otherTables = an.OtherTables, notes = an.Notes, gridColumns = an.GridColumns, spec = an.Spec })})");
                    break;
                }
                case "findTables": Js($"rb.onFindTables({J(Str(r, "column"))}, {J(await _meta.FindTablesByColumnAsync(Str(r, "column")))})"); break;
                case "build": Build(ReadSpec(r)); break;
                case "run": await RunAsync(r); break;
                case "plan": Plan(ReadSpec(r)); break;
                case "deploy": Deploy(ReadSpec(r), r.GetProperty("kinds").EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet()); break;
                case "openProc":
                {
                    var spec = ReadSpec(r); var b = _gen.Build(spec);
                    if (b.HasErrors) { Js($"rb.onError({J(string.Join("\n", b.Warnings))})"); break; }
                    OpenSqlRequested?.Invoke(b.Procedure, false, spec.ProcName);
                    break;
                }
                case "menuScript": MenuScript(ReadSpec(r), r); break;
                case "saveDraft":
                {
                    // name = "_tu_luu" + silent: tự lưu bản đang làm dở (không cần mã báo cáo, không báo thông báo); còn lại: lưu "mẫu của tôi" theo mã báo cáo
                    var spec = ReadSpec(r); var silent = Bool(r, "silent"); var name = Str(r, "name");
                    if (name.Length == 0)
                    {
                        if (string.IsNullOrWhiteSpace(spec.CoreCode)) { Js($"rb.onError({J("Đặt mã báo cáo trước khi lưu thành mẫu.")})"); break; }
                        name = spec.CoreCode;
                    }
                    Directory.CreateDirectory(DraftDir);
                    File.WriteAllText(Path.Combine(DraftDir, Safe(name) + ".json"), r.GetProperty("spec").GetRawText());
                    SendDrafts(); if (!silent) Js($"rb.onNote({J("Đã lưu thành mẫu của tôi: " + name)})");
                    break;
                }
                case "loadDraft":
                {
                    var p = Path.Combine(DraftDir, Safe(Str(r, "name")) + ".json");
                    if (File.Exists(p)) Js($"rb.onLoadDraft({File.ReadAllText(p)})");
                    break;
                }
                case "deleteDraft":
                {
                    var p = Path.Combine(DraftDir, Safe(Str(r, "name")) + ".json");
                    if (File.Exists(p)) File.Delete(p);
                    SendDrafts();
                    break;
                }
                case "copy":
                    try { Clipboard.SetText(Str(r, "text")); } catch { /* clipboard bận */ }
                    break;
                case "saveExcel": SaveExcel(ReadSpec(r)); break;
            }
        }
        catch (Exception ex) { Js($"rb.onError({J(ex.Message)})"); }
    }

    private static string Safe(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ---- siêu dữ liệu ----
    private async Task ColumnsAsync(string table, bool partitioned, string tag)
    {
        var cols = await _meta.GetColumnsAsync(table, partitioned);
        var cat = ReportCatalog.Instance;
        var items = cols.Select(c =>
        {
            var s = cat.SuggestColumn("a." + c.Name, c.SqlType); var f = cat.SuggestFilter("a." + c.Name, c.SqlType);
            return new
            {
                name = c.Name, type = c.SqlType, len = c.Length, key = c.IsKey, hv = s.HeaderVi, he = s.HeaderEn, w = s.Width, fmt = s.Format, gtype = s.Type,
                filter = f.Controller.Length > 0 ? new { f.Style, f.Controller, f.Reference, f.Key, f.Check, f.Information, hv = f.HeaderVi, he = f.HeaderEn, op = f.Op } : null,
            };
        }).ToList();
        var flags = new
        {
            hasStatus = cols.Any(c => c.Name.Equals("status", StringComparison.OrdinalIgnoreCase)),
            unitColumn = cols.Any(c => c.Name.Equals("ma_dvcs", StringComparison.OrdinalIgnoreCase)) ? "ma_dvcs" : "",
            dateField = cols.FirstOrDefault(c => c.Name.Equals("ngay_ct", StringComparison.OrdinalIgnoreCase))?.Name
                        ?? cols.FirstOrDefault(c => c.SqlType.Contains("date", StringComparison.OrdinalIgnoreCase))?.Name ?? "",
        };
        Js($"rb.onColumns({J(tag)}, {J(table)}, {J(items)}, {J(flags)})");
    }

    // ---- sinh ----
    private void Build(ReportSpec spec)
    {
        var b = _gen.Build(spec);
        _last = b; _lastSpec = spec;
        Js($"rb.onBuilt({J(new { warnings = b.Warnings, ok = !b.HasErrors, procedure = b.Procedure, filterXml = b.FilterXml, gridXml = b.GridXml, reportXml = b.ReportXml, mainAspx = b.MainAspx, @params = b.Params, controller = b.Controller, procName = b.ProcName, mainFile = b.MainFile })})");
    }

    private async Task RunAsync(JsonElement r)
    {
        var spec = ReadSpec(r);
        var b = _gen.Build(spec);
        if (b.HasErrors) { Js($"rb.onRun({J(new { error = string.Join("\n", b.Warnings) })})"); return; }
        var values = new Dictionary<string, string>();
        if (r.TryGetProperty("values", out var vs) && vs.ValueKind == JsonValueKind.Object) foreach (var p in vs.EnumerateObject()) values[p.Name] = p.Value.GetString() ?? "";
        _runCts?.Cancel();
        var cts = _runCts = new CancellationTokenSource();
        Js("rb.onRunStart()");
        var res = await _meta.RunPreviewAsync(b.Procedure, b.Params, values, 200, cts.Token);
        if (!cts.IsCancellationRequested) Js($"rb.onRun({J(new { sets = res.Sets.Select(s => new { columns = s.Columns, rows = s.Rows, truncated = s.Truncated }), ms = res.Millis, error = res.Error })})");
    }

    // ---- lưu vào source ----
    private List<BuildFile>? FilesFor(ReportSpec spec, out ReportBuildResult? built)
    {
        built = _gen.Build(spec);
        if (built.HasErrors) { Js($"rb.onError({J(string.Join("\n", built.Warnings))})"); return null; }
        var tmp = Path.Combine(Path.GetTempPath(), "bcode_rb_" + Guid.NewGuid().ToString("N") + ".xlsx");
        try { ReportExcelLayout.Write(spec, tmp); return ReportFilesDeployService.FilesFor(spec, built, File.ReadAllBytes(tmp)); }
        finally { try { File.Delete(tmp); } catch { /* file tạm */ } }
    }

    private void Plan(ReportSpec spec)
    {
        var files = FilesFor(spec, out _);
        if (files is null) return;
        var plan = _deploy.Plan(_host.SourcePath, files);
        Js($"rb.onPlan({J(new { problem = plan.Problem, root = plan.SourceRoot, files = plan.Files.Select(f => new { f.Kind, f.Rel, f.Target, f.Exists, f.Same, f.Size, f.TargetSize, f.TargetTime, f.Diff, f.Added, f.Removed, f.IsText }) })})");
    }

    private void Deploy(ReportSpec spec, HashSet<string> kinds)
    {
        var files = FilesFor(spec, out _);
        if (files is null) return;
        var res = _deploy.Deploy(_host.SourcePath, _host.WorkspaceName, files, kinds);
        Js($"rb.onDeployed({J(new { res.Ok, res.Lines, res.BackupDir })})");
    }

    private void SaveExcel(ReportSpec spec)
    {
        var b = _gen.Build(spec);
        if (b.HasErrors) { Js($"rb.onError({J(string.Join("\n", b.Warnings))})"); return; }
        using var dlg = new SaveFileDialog { Filter = "Excel (*.xlsx)|*.xlsx", FileName = spec.Controller + ".xlsx" };
        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
        ReportExcelLayout.Write(spec, dlg.FileName);
        Js($"rb.onNote({J("Đã lưu mẫu Excel: " + dlg.FileName)})");
    }

    // ---- menu ----
    private void MenuScript(ReportSpec spec, JsonElement r)
    {
        var wmenu = Str(r, "wmenuId"); var bar = Str(r, "barVi");
        if (string.IsNullOrWhiteSpace(wmenu) || string.IsNullOrWhiteSpace(bar)) { Js($"rb.onError({J("Nhập WMenu Id và tên menu (Việt).")})"); return; }
        var menuId = Str(r, "menuId").Length > 0 ? Str(r, "menuId") : wmenu;
        OpenSqlRequested?.Invoke(_host.MenuScript(wmenu, Str(r, "parentId"), menuId, bar, Str(r, "barEn"), spec.MainFile + ".aspx", spec.Controller), true, "Menu " + spec.Controller);
    }
}
