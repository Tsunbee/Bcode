using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.Services.Rpt;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Create RPT &amp; XML": dán profiler của báo cáo → tự tìm Grid controller → chọn field → thiết kế bố cục Excel mẫu (kéo thả) →
/// sinh file .xlsx mẫu (?h_xxx, !2.field) + bổ sung các biến h_xxx còn thiếu vào Report .xml. Giao diện là trang WebView2
/// (Web/Shell/createrpt.html); control này chỉ nối trang với các service (đọc controller, chạy profiler, dựng xml, ghi Excel) và
/// không chứa logic nghiệp vụ. Phần Crystal Report (.rpt) chưa hỗ trợ — Bcode không có SDK Crystal.
/// </summary>
public class CreateRptControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };

    private readonly WebBarHost _web = new("createrpt.html") { Dock = DockStyle.Fill };
    private readonly Func<Workspace?> _workspace;
    private readonly GridControllerReader _reader;
    private readonly ReportProfilerService _profiler;
    private readonly RptXmlBuilder _xml;
    private readonly ExcelTemplateWriter _excel;
    private readonly PivotXlsxWriter _pivot;

    private ControllerInfo? _info;
    private string? _sourceXmlText; // report xml hiện hữu trong source project (chỉ đọc)

    public CreateRptControl(Func<Workspace?> workspace, ReportProfilerService profiler, GridControllerReader? reader = null,
        RptXmlBuilder? xml = null, ExcelTemplateWriter? excel = null)
    {
        _workspace = workspace;
        _profiler = profiler;
        _reader = reader ?? new GridControllerReader();
        _xml = xml ?? new RptXmlBuilder();
        _excel = excel ?? new ExcelTemplateWriter();
        _pivot = new PivotXlsxWriter(_excel);

        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => _ = HandleAsync(root.GetRawText());
        _web.Ready += SendInit;
    }

    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    private void SendInit()
    {
        var ws = _workspace();
        Js($"createRpt.init({J(new
        {
            sourcePath = ws?.SourcePath ?? "",
            programPath = ws?.ProgramPath ?? "",
            desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            workspace = ws?.Name ?? "",
        })})");
    }

    private async Task HandleAsync(string raw)
    {
        string action = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            action = root.GetProperty("action").GetString() ?? "";
            switch (action)
            {
                case "ready": SendInit(); break;
                case "guess": await GuessAsync(Str(root, "sql")); break;
                case "analyze": await AnalyzeAsync(Str(root, "sql"), Str(root, "controller")); break;
                case "preview": Preview(root); break;
                case "create": Create(root); break;
                case "openFolder": OpenFolder(Str(root, "path")); break;
                case "copy": try { Clipboard.SetText(Str(root, "text")); } catch { /* clipboard bận */ } break;
            }
        }
        catch (Exception ex)
        {
            Js($"createRpt.onError({J(action)}, {J(ex.Message)})");
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Đoán tên controller từ lệnh exec cuối của SQL (và Main\*.aspx nếu có) để điền sẵn ô Grid Controller khi vừa dán profiler.</summary>
    private async Task GuessAsync(string sql)
    {
        var name = ReportProfilerService.GuessName(sql);
        var sourcePath = _workspace()?.SourcePath;
        var info = await Task.Run(() => name.Length == 0 ? null : _reader.Load(sourcePath, name));
        Js($"createRpt.onGuess({J(new { name = info?.Controller ?? name, found = info?.GridPath is not null, note = info?.Note })})");
    }

    private async Task AnalyzeAsync(string sql, string controller)
    {
        var ws = _workspace();
        if (ws is null) throw new InvalidOperationException("Chưa chọn Workspace (WS).");
        if (string.IsNullOrWhiteSpace(sql)) throw new InvalidOperationException("Chưa có SQL Statement (profiler) để lấy danh sách field.");
        if (string.IsNullOrWhiteSpace(controller)) controller = ReportProfilerService.GuessName(sql);

        var tables = await _profiler.ProfileAsync(sql);
        if (tables.Count == 0) throw new InvalidOperationException("Câu lệnh không trả về result set nào.");

        _info = await Task.Run(() => _reader.Load(ws.SourcePath, controller));
        _sourceXmlText = _info.ReportPath is null ? null : await Task.Run(() => File.ReadAllText(_info.ReportPath));
        var existing = _xml.ParseExisting(_sourceXmlText).ToDictionary(k => k.Key, k => new { v = k.Value.V, e = k.Value.E });
        Js($"createRpt.onAnalyzed({J(new { info = _info, tables, existing })})");
    }

    private sealed record VarsPayload(List<RptVar> Vars, string Name, string Title, string TitleE, string SavePath);

    private string BuildXml(JsonElement root, out string xmlPath)
    {
        var p = root.Deserialize<VarsPayload>(JsonOpts) ?? throw new InvalidOperationException("Dữ liệu không hợp lệ.");
        xmlPath = Path.Combine(p.SavePath, p.Name + ".xml");
        // Ưu tiên file xml đã có ở thư mục lưu (làm tiếp trên bản đã sinh trước đó); không có thì lấy bản của source project.
        var existing = File.Exists(xmlPath) ? File.ReadAllText(xmlPath) : _sourceXmlText;
        return _xml.Build(existing, p.Vars, p.Name, p.Title, p.TitleE);
    }

    private void Preview(JsonElement root) => Js($"createRpt.onXml({J(BuildXml(root, out _))})");

    private void Create(JsonElement root)
    {
        var xml = BuildXml(root, out var xmlPath);
        var layout = root.GetProperty("layout").Deserialize<SheetLayout>(JsonOpts) ?? throw new InvalidOperationException("Chưa có bố cục Excel.");
        var dir = Path.GetDirectoryName(xmlPath)!;
        var name = Path.GetFileNameWithoutExtension(xmlPath);
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidOperationException("Tên file không hợp lệ.");
        Directory.CreateDirectory(dir);

        var xlsxPath = Path.Combine(dir, name + ".xlsx");
        Backup(xlsxPath); Backup(xmlPath);
        if (layout.Pivot is not null) _pivot.Write(layout, xlsxPath);   // báo cáo pivot: 2 sheet Main + Main - Pivot
        else _excel.Write(layout, xlsxPath);
        File.WriteAllText(xmlPath, xml, new System.Text.UTF8Encoding(true));
        Js($"createRpt.onCreated({J(new { xlsxPath, xmlPath })})");
    }

    /// <summary>File đích đã có thì giữ lại bản cũ cạnh bên (.bak) trước khi ghi đè.</summary>
    private static void Backup(string path)
    {
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
    }

    private static void OpenFolder(string path)
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }
}
