using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.Services.Rpt.Builder;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Tạo báo cáo": người dùng không cần biết code chọn bảng, kéo trường vào các ô (Cột / Hàng / Giá trị / Bộ lọc) như Power BI; Bcode sinh sẵn procedure
/// (hiện ngay khi thao tác), Filter, Grid (bảng hoặc pivot), Report (Excel), Main và mẫu Excel — rồi cho xem trước form lọc + lưới, chạy thử trên dữ liệu thật,
/// và lưu vào source (diff + backup). Giao diện là trang WebView2 (Web/Shell/reportbuilder.html); mọi logic sinh nằm ở <see cref="ReportGenerator"/> và các service
/// trong Services/Rpt/Builder. Procedure chỉ được SINH (mở trong tab SQL để bạn xem rồi tự chạy) — Bcode không tự tạo procedure trong database.
/// </summary>
public class ReportBuilderControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private readonly WebBarHost _web = new("reportbuilder.html") { Dock = DockStyle.Fill };
    private readonly Func<Workspace?> _workspace;
    private readonly ReportGenerator _gen = new();
    private readonly ReportMetaService _meta;
    private readonly ReportFilesDeployService _deploy = new();
    private ReportBuildResult? _last;
    private ReportSpec? _lastSpec;
    private CancellationTokenSource? _runCts;

    /// <summary>(script, dùng Sys Data, tiêu đề tab) — mở procedure / script menu trong tab SQL.</summary>
    public event Action<string, bool, string>? OpenSqlRequested;

    public ReportBuilderControl(DbConnectionService connections, Func<Workspace?> workspace)
    {
        _workspace = workspace;
        _meta = new ReportMetaService(connections);
        Dock = DockStyle.Fill;
        Controls.Add(_web);
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
        var ws = _workspace();
        Js($"rb.init({J(new { workspace = ws?.Name ?? "", hasSource = !string.IsNullOrWhiteSpace(ws?.SourcePath), catalog = new { filters = ReportCatalog.Instance.FilterCount, grid = ReportCatalog.Instance.GridCount } })})");
        SendDrafts();
        try
        {
            var tables = await _meta.ListTablesAsync();
            Js($"rb.onTables({J(tables)})");
        }
        catch (Exception ex) { Js($"rb.onError({J("Không đọc được danh sách bảng: " + ex.Message)})"); }
    }

    // ---- bản nháp (lưu cấu hình đang làm để mở lại sửa) ----
    private static string DraftDir => Path.Combine(BcodePaths.AppData, "Bcode", "report-drafts");

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
                    var spec = ReadSpec(r);
                    if (string.IsNullOrWhiteSpace(spec.CoreCode)) { Js($"rb.onError({J("Đặt mã báo cáo trước khi lưu nháp.")})"); break; }
                    Directory.CreateDirectory(DraftDir);
                    File.WriteAllText(Path.Combine(DraftDir, Safe(spec.CoreCode) + ".json"), r.GetProperty("spec").GetRawText());
                    SendDrafts(); Js($"rb.onNote({J("Đã lưu nháp " + spec.CoreCode)})");
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
        var plan = _deploy.Plan(_workspace()?.SourcePath, files);
        Js($"rb.onPlan({J(new { problem = plan.Problem, root = plan.SourceRoot, files = plan.Files.Select(f => new { f.Kind, f.Rel, f.Target, f.Exists, f.Same, f.Size, f.TargetSize, f.TargetTime, f.Diff, f.Added, f.Removed, f.IsText }) })})");
    }

    private void Deploy(ReportSpec spec, HashSet<string> kinds)
    {
        var files = FilesFor(spec, out _);
        if (files is null) return;
        var ws = _workspace();
        var res = _deploy.Deploy(ws?.SourcePath, ws?.Name ?? "", files, kinds);
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
        var item = new WCommandItem
        {
            WMenuId = Str(r, "wmenuId"), WMenuId0 = Str(r, "parentId"), MenuId = Str(r, "menuId").Length > 0 ? Str(r, "menuId") : Str(r, "wmenuId"),
            Bar = Str(r, "barVi"), Bar2 = Str(r, "barEn"), Link = spec.MainFile + ".aspx", SysId = spec.Controller, Status = "1",
        };
        if (string.IsNullOrWhiteSpace(item.WMenuId) || string.IsNullOrWhiteSpace(item.Bar)) { Js($"rb.onError({J("Nhập WMenu Id và tên menu (Việt).")})"); return; }
        OpenSqlRequested?.Invoke(WCommandService.GenerateScript(item), true, "Menu " + spec.Controller);
    }
}
