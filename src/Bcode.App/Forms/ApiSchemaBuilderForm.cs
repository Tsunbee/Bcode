using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

public partial class ApiSchemaBuilderForm : ThemedForm
{
    // Chế độ App: thân form dựng bằng NK / NativeSkin (đã gồm DPI × UiScale) → chỉ co cỡ cửa sổ; chế độ Web: px thiết kế như cũ.
    protected override DpiLayout DpiLayoutMode => UiMode.IsNative ? DpiLayout.WindowOnly : DpiLayout.Design;

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly TableDataService _tableDataService;
    // Dự án đang mở: khoá file lưu các API đã tạo (ApiSchemaStore) + Source Path để tìm controller Dir / tên tiếng Việt của bảng.
    private readonly string _wsKey;
    private readonly string _sourcePath;
    // Tra bảng → menu wcommand (sysid = controller của tên hàm "Set<sysid>"); null = chỉ theo controller Dir trong source.
    private readonly TableMenuService? _tableMenus;
    private bool _webReady, _savedOnClose;

    /// <summary>Dòng thông báo hiện khi mở (chuột phải menu / file → "Tạo cấu trúc API…": API vừa dựng, ghi chú).</summary>
    private readonly string _startInfo;

    public ApiSchemaBuilderForm(SqlObjectBrowserService sqlObjectService, TableDataService tableDataService, Workspace? workspace = null, TableMenuService? tableMenus = null, string? startInfo = null)
    {
        _tableMenus = tableMenus;
        _startInfo = startInfo ?? "";
        _sqlObjectService = sqlObjectService;
        _tableDataService = tableDataService;
        _wsKey = workspace is null ? "" : (workspace.Name.Length > 0 ? workspace.Name : workspace.AppDatabase ?? "");
        _sourcePath = workspace?.SourcePath ?? "";
        if (_sourcePath.Length > 0) _ = Bcode.App.Services.SourceFieldIndex.GetAsync(_sourcePath);   // đọc sẵn controller Dir ở nền

        Text = "Tạo Cấu Trúc API & Đặc Tả Word";
        Width = 1050;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;
        if (UiMode.IsNative) { BuildNative(); return; }
        Controls.Add(_web);

        Load += async (_, _) =>
        {
            var shellDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web", "Shell");
            // Môi trường WebView2 dùng chung với các trang Bcode (trước đây CreateAsync() mặc định = thêm 1 bộ tiến trình Edge ~80–150 MB
            // + thư mục Bcode.exe.WebView2 cạnh exe). Trang không dùng localStorage nên đổi thư mục dữ liệu không mất gì.
            var env = await Bcode.App.UI.WebViewEnvironment.GetAsync();
            await _web.EnsureCoreWebView2Async(env);
            Bcode.App.UI.UiScale.BindZoom(_web);

            // host bcode.shell như mọi trang Bcode: có luật host-resolver nên không chờ DNS mỗi lần điều hướng
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                Bcode.App.UI.WebViewEnvironment.Host,
                shellDir,
                CoreWebView2HostResourceAccessKind.Allow);

            _web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _web.CoreWebView2.Navigate($"https://{Bcode.App.UI.WebViewEnvironment.Host}/apischema.html");

            _web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                // Mở lại: nạp các API đã tạo lần trước của dự án; chưa có thì 1 API trống.
                var saved = Bcode.App.Services.Api.ApiSchemaStore.Load(_wsKey);
                await _web.CoreWebView2.ExecuteScriptAsync(saved is null ? "window.initFirstApi()"
                    : $"window.initApis({JsonSerializer.Serialize(saved, Bcode.App.Services.Api.ApiSchemaStore.Json)})");
                _webReady = true;
                if (_startInfo.Length > 0) await _web.CoreWebView2.ExecuteScriptAsync($"window.setInfo && window.setInfo({JsonSerializer.Serialize(_startInfo)})");
                // Danh sách bảng của cả App và Sys cho ô lọc / gợi ý bảng (tải song song, không chặn giao diện).
                _ = LoadTablesAsync(false);
                _ = LoadTablesAsync(true);
            };
        };

        // Đóng form (nút Đóng, X, Esc): lấy trạng thái mới nhất từ trang rồi mới đóng — trang cũng tự lưu sau mỗi lần sửa.
        FormClosing += async (_, e) =>
        {
            if (_savedOnClose || !_webReady || _web.CoreWebView2 is null) return;
            e.Cancel = true;
            _savedOnClose = true;
            try
            {
                var raw = await _web.CoreWebView2.ExecuteScriptAsync("JSON.stringify(window.getState ? window.getState() : null)");
                if (JsonSerializer.Deserialize<string>(raw) is { Length: > 4 } json) SaveState(json);
            }
            catch { /* không lưu được thì vẫn đóng */ }
            BeginInvoke(new Action(Close));
        };
    }

    /// <summary>Lưu trạng thái trang (JSON model của apischema.html) vào file của dự án. Lỗi ghi file chỉ báo ở thanh trạng thái trang.</summary>
    private void SaveState(string json)
    {
        try
        {
            if (JsonSerializer.Deserialize<Bcode.App.Services.Api.ApiSchemaState>(json, Bcode.App.Services.Api.ApiSchemaStore.Json) is { Apis.Count: > 0 } st)
                Bcode.App.Services.Api.ApiSchemaStore.Save(_wsKey, st);
        }
        catch (Exception ex)
        {
            if (_webReady && !IsDisposed) _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setInfo && window.setInfo({JsonSerializer.Serialize("Không lưu được danh sách API: " + ex.Message)})");
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var rawJson = e.WebMessageAsJson;
            if (rawJson.StartsWith("\"") && rawJson.EndsWith("\""))
            {
                using var jsonDoc = JsonDocument.Parse(rawJson);
                if (jsonDoc.RootElement.ValueKind == JsonValueKind.String)
                {
                    rawJson = jsonDoc.RootElement.GetString() ?? rawJson;
                }
            }

            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            if (!root.TryGetProperty("action", out var actionProp)) return;
            var action = actionProp.GetString();

            switch (action)
            {
                case "load-tables":
                    var isSys = GetBooleanDb(root, "db");
                    await LoadTablesAsync(isSys);
                    break;
                case "load-columns":
                    var isSysCol = GetBooleanDb(root, "db");
                    var table = root.TryGetProperty("table", out var tProp) ? tProp.GetString() ?? "" : "";
                    await LoadColumnsAsync(isSysCol, table);
                    break;
                case "export-word":
                    if (root.TryGetProperty("apis", out var apisProp))
                    {
                        ExportWord(apisProp, root.TryGetProperty("hideSource", out var hs) && hs.ValueKind == JsonValueKind.True);
                    }
                    break;
                case "table-info":
                {
                    // Nút ↻ Tự đặt: tra lại menu / controller của bảng (không nạp lại cột — giữ phần đã sửa).
                    var isSysInfo = GetBooleanDb(root, "db");
                    var tbl = root.TryGetProperty("table", out var tiProp) ? tiProp.GetString() ?? "" : "";
                    var colNames = root.TryGetProperty("cols", out var cnProp) && cnProp.ValueKind == JsonValueKind.Array ? cnProp.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : new List<string>();
                    if (tbl.Length > 0) await PushTableInfoAsync((isSysInfo ? "1" : "0") + "|" + tbl, tbl, colNames);
                    break;
                }
                case "save":
                    if (root.TryGetProperty("state", out var stProp)) SaveState(stProp.GetRawText());
                    break;
                case "close":
                    Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Lỗi xử lý sự kiện: " + ex.Message, "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool GetBooleanDb(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var prop)) return false;
        if (prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt32() == 1;
        }
        if (prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() == "1";
        }
        return false;
    }

    private async Task LoadTablesAsync(bool isSys)
    {
        try
        {
            var objects = await _sqlObjectService.ListObjectsAsync(isSys);
            var tables = objects
                .Where(o => o.Kind == SqlObjectKind.Table || o.Kind == SqlObjectKind.View)
                .Select(o => o.QualifiedName)
                .ToList();

            var json = JsonSerializer.Serialize(tables);
            await _web.CoreWebView2.ExecuteScriptAsync($"window.setTables({json}, '{(isSys ? 1 : 0)}')");
        }
        catch { }
    }

    private async Task LoadColumnsAsync(bool isSys, string rawTable)
    {
        if (string.IsNullOrWhiteSpace(rawTable)) return;
        var parts = rawTable.Replace("[", "").Replace("]", "").Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : parts[0];

        var key = (isSys ? "1" : "0") + "|" + rawTable;
        try
        {
            List<object> list;
            List<string> names;
            try
            {
                var desc = await _tableDataService.GetColumnDescriptionsAsync(isSys, schema, table);
                names = desc.Select(c => c.Name).ToList();
                list = desc.Select(c => (object)new
                {
                    selected = true,
                    colName = c.Name,
                    dataType = c.Type,
                    isPk = c.IsKey,
                    nullable = c.Nullable,
                    identity = c.Identity,
                    required = false,
                    fieldDesc = c.Name.ToLower(),
                    logicDesc = c.Description ?? ""
                }).ToList();
            }
            catch
            {
                // Không đọc được metadata đầy đủ (quyền / view lạ): lấy tên + kiểu như trước, khoá chính hỏi riêng.
                var types = await _tableDataService.GetColumnTypesAsync(isSys, schema, table);
                var pks = new HashSet<string>(await _tableDataService.GetPrimaryKeyColumnsAsync(isSys, schema, table), StringComparer.OrdinalIgnoreCase);
                names = types.Select(kvp => kvp.Key).ToList();
                list = types.Select(kvp => (object)new
                {
                    selected = true, colName = kvp.Key, dataType = kvp.Value, isPk = pks.Contains(kvp.Key),
                    nullable = true, identity = false, required = false, fieldDesc = kvp.Key.ToLower(), logicDesc = ""
                }).ToList();
            }

            var json = JsonSerializer.Serialize(list);
            await _web.CoreWebView2.ExecuteScriptAsync($"window.setColumns({json}, {JsonSerializer.Serialize(key)})");

            // Sau đó (source trên ổ mạng có thể chậm): menu dùng bảng → sysid → tên hàm "Set<sysid>", tên tiếng Việt từng cột.
            await PushTableInfoAsync(key, rawTable, names);
        }
        catch { }
    }

    /// <summary>Controller của bảng (= sysid của menu dùng bảng, không có thì controller Dir) + tên tiếng Việt → trang (window.setTableInfo).</summary>
    private async Task PushTableInfoAsync(string key, string rawTable, IEnumerable<string> names)
    {
        var table = rawTable.Replace("[", "").Replace("]", "");
        if (table.LastIndexOf('.') is var dot and >= 0) table = table[(dot + 1)..];
        var info = await Bcode.App.Services.Api.ApiSchemaStore.TableInfoAsync(_sourcePath, rawTable, names, _tableMenus);
        if (IsDisposed || _web.CoreWebView2 is null) return;
        var payload = JsonSerializer.Serialize(new { controller = info.Controller, others = info.Others, vi = info.Vi, menu = info.Menu, func = Bcode.App.Services.Api.ApiSchemaStore.FuncName(info.Controller.Length > 0 ? info.Controller : table) },
            Bcode.App.Services.Api.ApiSchemaStore.Json);
        await _web.CoreWebView2.ExecuteScriptAsync($"window.setTableInfo({JsonSerializer.Serialize(key)}, {payload})");
    }

    /// <param name="hideSource">true = bản gửi đối tác: không có dòng "Bảng DB", cột Bảng nguồn / Cột CSDL — mỗi trường định nghĩa bằng tên tiếng Việt.</param>
    private void ExportWord(JsonElement apis, bool hideSource = false)
    {
        using var sfd = new SaveFileDialog
        {
            Filter = "Word Document (*.doc)|*.doc",
            FileName = $"Tai_Lieu_Tong_Hop_API_{DateTime.Now:yyyyMMdd_HHmmss}.doc"
        };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;

        var sb = new StringBuilder();
        sb.AppendLine("<html xmlns:o='urn:schemas-microsoft-com:office:office' xmlns:w='urn:schemas-microsoft-com:office:word' xmlns='http://www.w3.org/TR/REC-html40'>");
        sb.AppendLine("<head><meta charset='utf-8'><title>Tài liệu đặc tả tổng hợp API</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; color: #222; margin: 20px; }");
        sb.AppendLine("h1 { color: #D97706; border-bottom: 2px solid #D97706; padding-bottom: 6px; }");
        sb.AppendLine("h2 { color: #1E3A8A; margin-top: 30px; border-bottom: 1px solid #CCC; padding-bottom: 4px; }");
        sb.AppendLine(".desc-box { background: #F8FAFC; border-left: 4px solid #F5A623; padding: 10px; margin: 10px 0; font-size: 13px; }");
        sb.AppendLine(".json-box { background: #1E1E1E; color: #DCDCDC; padding: 12px; font-family: Consolas, monospace; font-size: 12px; border-radius: 4px; white-space: pre-wrap; }");
        sb.AppendLine("table { border-collapse: collapse; width: 100%; margin-top: 10px; }");
        sb.AppendLine("th, td { border: 1px solid #CBD5E1; padding: 7px 10px; font-size: 12.5px; }");
        sb.AppendLine("th { background-color: #F1F5F9; font-weight: bold; text-align: left; }");
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<h1>TÀI LIỆU ĐẶC TẢ TỔNG HỢP API</h1>");
        sb.AppendLine($"<p><i>Ngày xuất tài liệu: {DateTime.Now:dd/MM/yyyy HH:mm:ss}</i></p><hr/>");

        int count = 1;
        static string H(string s) => System.Net.WebUtility.HtmlEncode(s ?? "");
        foreach (var api in apis.EnumerateArray())
        {
            var name = api.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var desc = api.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
            var func = api.TryGetProperty("func", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() ?? "" : "";

            var tableNames = new List<string>();
            if (api.TryGetProperty("tables", out var tbs) && tbs.ValueKind == JsonValueKind.Array)
                foreach (var tb in tbs.EnumerateArray())
                {
                    var tn = tb.TryGetProperty("table", out var tnp) ? tnp.GetString() ?? "" : "";
                    var isSysT = tb.TryGetProperty("db", out var dbp) && (dbp.ValueKind == JsonValueKind.Number ? dbp.GetInt32() == 1 : dbp.GetString() == "1");
                    if (tn.Length > 0) tableNames.Add($"{tn} ({(isSysT ? "Sys Data" : "App Data")})");
                }

            sb.AppendLine($"<h2>{count++}. {H(name)}</h2>");
            if (func.Length > 0) sb.AppendLine($"<p><b>Tên hàm API:</b> <code>{H(func)}</code></p>");
            if (!hideSource) sb.AppendLine($"<p><b>Bảng DB:</b> {(tableNames.Count == 0 ? "—" : string.Join(" &nbsp;+&nbsp; ", tableNames.Select(x => "<code>" + H(x) + "</code>")))}</p>");
            sb.AppendLine($"<div class='desc-box'>{H(desc).Replace("\n", "<br/>")}</div>");

            sb.AppendLine("<table>");
            sb.AppendLine(hideSource
                ? "<thead><tr><th style='width:20%;'>Tên trường API</th><th style='width:24%;'>Tên trường (tiếng Việt)</th><th style='width:12%;'>Kiểu dữ liệu</th><th style='width:7%;text-align:center;'>Khóa chính</th><th style='width:8%;text-align:center;'>Bắt buộc</th><th>Quy tắc / Logic</th></tr></thead><tbody>"
                : "<thead><tr><th style='width:12%;'>Bảng nguồn</th><th style='width:13%;'>Cột CSDL</th><th style='width:11%;'>Kiểu dữ liệu</th><th style='width:7%;text-align:center;'>Khóa chính</th><th style='width:8%;text-align:center;'>Bắt buộc</th><th style='width:16%;'>Tên trường API</th><th style='width:16%;'>Tên trường (tiếng Việt)</th><th>Quy tắc / Logic</th></tr></thead><tbody>");

            var sampleDict = new Dictionary<string, object>();
            var requiredFields = new List<string>();
            var keyFields = new List<string>();
            if (api.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cols.EnumerateArray())
                {
                    bool isSelected = c.TryGetProperty("selected", out var selProp) && selProp.ValueKind == JsonValueKind.True;
                    if (!isSelected) continue;

                    var src = c.TryGetProperty("tname", out var sp) ? sp.GetString() ?? "" : "";
                    var colName = c.TryGetProperty("colName", out var cn) ? cn.GetString() ?? "" : "";
                    var dt = c.TryGetProperty("dataType", out var dtp) ? dtp.GetString() ?? "" : "";
                    var field = c.TryGetProperty("fieldDesc", out var fd) ? fd.GetString() ?? colName : colName;
                    var logic = c.TryGetProperty("logicDesc", out var ld) ? ld.GetString() ?? "" : "";
                    var vi = c.TryGetProperty("viName", out var vn) && vn.ValueKind == JsonValueKind.String ? vn.GetString() ?? "" : "";
                    var isPk = c.TryGetProperty("isPk", out var pkp) && pkp.ValueKind == JsonValueKind.True;
                    var isReq = c.TryGetProperty("required", out var rqp) && rqp.ValueKind == JsonValueKind.True;
                    if (isPk) keyFields.Add(field);
                    if (isReq) requiredFields.Add(field);

                    var pkCell = $"<td style='text-align:center;'>{(isPk ? "✔" : "")}</td><td style='text-align:center;'>{(isReq ? "✔" : "")}</td>";
                    sb.AppendLine(hideSource
                        ? $"<tr><td><b>{H(field)}</b></td><td>{H(vi)}</td><td>{H(dt)}</td>{pkCell}<td>{H(logic)}</td></tr>"
                        : $"<tr><td>{H(src)}</td><td>{H(colName)}</td><td>{H(dt)}</td>{pkCell}<td><b>{H(field)}</b></td><td>{H(vi)}</td><td>{H(logic)}</td></tr>");

                    var lowerT = dt.ToLower();
                    if (lowerT.Contains("int") || lowerT.Contains("numeric") || lowerT.Contains("decimal")) sampleDict[field] = 0;
                    else if (lowerT.Contains("bit")) sampleDict[field] = true;
                    else if (lowerT.Contains("date")) sampleDict[field] = "2026-01-01";
                    else sampleDict[field] = "string";
                }
            }
            sb.AppendLine("</tbody></table>");
            if (keyFields.Count > 0) sb.AppendLine($"<p><b>Khóa chính:</b> {H(string.Join(", ", keyFields))}</p>");
            if (requiredFields.Count > 0) sb.AppendLine($"<p><b>Trường bắt buộc nhập:</b> {H(string.Join(", ", requiredFields))}</p>");

            if (sampleDict.Count > 0)
            {
                sb.AppendLine("<h4>Ví dụ Payload JSON Request mẫu:</h4>");
                var json = JsonSerializer.Serialize(sampleDict, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                sb.AppendLine($"<div class='json-box'>{H(json)}</div>");
            }

            sb.AppendLine("<br/><br/>");
        }

        sb.AppendLine("</body></html>");
        File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
        MessageBox.Show(this, "Đã xuất tài liệu Word thành công!", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}
