using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace Bcode.App.Forms;

public class ApiSchemaBuilderForm : ThemedForm
{
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly TableDataService _tableDataService;

    public ApiSchemaBuilderForm(SqlObjectBrowserService sqlObjectService, TableDataService tableDataService)
    {
        _sqlObjectService = sqlObjectService;
        _tableDataService = tableDataService;

        Text = "Tạo Cấu Trúc API & Đặc Tả Word";
        Width = 1050;
        Height = 700;
        StartPosition = FormStartPosition.CenterParent;
        Controls.Add(_web);

        Load += async (_, _) =>
        {
            var shellDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web", "Shell");
            var env = await CoreWebView2Environment.CreateAsync();
            await _web.EnsureCoreWebView2Async(env);

            _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "app.bcode",
                shellDir,
                CoreWebView2HostResourceAccessKind.Allow);

            _web.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            _web.CoreWebView2.Navigate("https://app.bcode/apischema.html");

            _web.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                await _web.CoreWebView2.ExecuteScriptAsync("window.initFirstApi()");
                await LoadTablesAsync(false);
            };
        };
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
                        ExportWord(apisProp);
                    }
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
            await _web.CoreWebView2.ExecuteScriptAsync($"window.setTables({json})");
        }
        catch { }
    }

    private async Task LoadColumnsAsync(bool isSys, string rawTable)
    {
        if (string.IsNullOrWhiteSpace(rawTable)) return;
        var parts = rawTable.Replace("[", "").Replace("]", "").Split('.');
        var schema = parts.Length == 2 ? parts[0] : "dbo";
        var table = parts.Length == 2 ? parts[1] : parts[0];

        try
        {
            var types = await _tableDataService.GetColumnTypesAsync(isSys, schema, table);
            var list = types.Select(kvp => new
            {
                selected = true,
                colName = kvp.Key,
                dataType = kvp.Value,
                fieldDesc = kvp.Key.ToLower(),
                logicDesc = ""
            }).ToList();

            var json = JsonSerializer.Serialize(list);
            await _web.CoreWebView2.ExecuteScriptAsync($"window.setColumns({json})");
        }
        catch { }
    }

    private void ExportWord(JsonElement apis)
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
        foreach (var api in apis.EnumerateArray())
        {
            var name = api.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var table = api.TryGetProperty("table", out var t) ? t.GetString() ?? "" : "";
            var desc = api.TryGetProperty("desc", out var d) ? d.GetString() ?? "" : "";
            
            bool isSys = false;
            if (api.TryGetProperty("db", out var dbProp))
            {
                isSys = dbProp.ValueKind == JsonValueKind.Number ? dbProp.GetInt32() == 1 : dbProp.GetString() == "1";
            }

            sb.AppendLine($"<h2>{count++}. {name}</h2>");
            sb.AppendLine($"<p><b>Database:</b> {(isSys ? "Sys Data" : "App Data")} &nbsp;|&nbsp; <b>Bảng DB:</b> <code>{table}</code></p>");
            sb.AppendLine($"<div class='desc-box'>{System.Net.WebUtility.HtmlEncode(desc).Replace("\n", "<br/>")}</div>");

            sb.AppendLine("<table>");
            sb.AppendLine("<thead><tr><th style='width:25%;'>Cột CSDL</th><th style='width:20%;'>Kiểu dữ liệu</th><th style='width:25%;'>Tên trường API</th><th>Quy tắc / Logic</th></tr></thead><tbody>");

            var sampleDict = new Dictionary<string, object>();
            if (api.TryGetProperty("columns", out var cols) && cols.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in cols.EnumerateArray())
                {
                    bool isSelected = c.TryGetProperty("selected", out var selProp) && selProp.GetBoolean();
                    if (!isSelected) continue;

                    var colName = c.TryGetProperty("colName", out var cn) ? cn.GetString() ?? "" : "";
                    var dt = c.TryGetProperty("dataType", out var dtp) ? dtp.GetString() ?? "" : "";
                    var field = c.TryGetProperty("fieldDesc", out var fd) ? fd.GetString() ?? colName : colName;
                    var logic = c.TryGetProperty("logicDesc", out var ld) ? ld.GetString() ?? "" : "";

                    sb.AppendLine($"<tr><td>{colName}</td><td>{dt}</td><td><b>{field}</b></td><td>{logic}</td></tr>");

                    var lowerT = dt.ToLower();
                    if (lowerT.Contains("int") || lowerT.Contains("numeric") || lowerT.Contains("decimal")) sampleDict[field] = 0;
                    else if (lowerT.Contains("bit")) sampleDict[field] = true;
                    else if (lowerT.Contains("date")) sampleDict[field] = "2026-01-01";
                    else sampleDict[field] = "string";
                }
            }
            sb.AppendLine("</tbody></table>");

            if (sampleDict.Count > 0)
            {
                sb.AppendLine("<h4>Ví dụ Payload JSON Request mẫu:</h4>");
                var json = JsonSerializer.Serialize(sampleDict, new JsonSerializerOptions { WriteIndented = true });
                sb.AppendLine($"<div class='json-box'>{json}</div>");
            }

            sb.AppendLine("<br/><br/>");
        }

        sb.AppendLine("</body></html>");
        File.WriteAllText(sfd.FileName, sb.ToString(), Encoding.UTF8);
        MessageBox.Show(this, "Đã xuất tài liệu Word thành công!", "Bcode", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
}