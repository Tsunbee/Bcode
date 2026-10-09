using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Services;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Check Include": so khai báo INCLUDE / IGNORE (file trong App_Data\Controllers\Include), options (App DB) và wcommand9 (Sys DB) của dự án
/// với catalog tính năng từng phiên bản. Giao diện là trang WebView2 (Web/Shell/includecheck.html); logic ở <see cref="IncludeCheckService"/>.
/// </summary>
public class IncludeCheckControl : UserControl
{
    private readonly WebBarHost _web = new("includecheck.html") { Dock = DockStyle.Fill };
    private readonly DbConnectionService _connections;
    private string _controllers = "";
    private bool _running;
    private static string J(object? o) => JsonSerializer.Serialize(o, IncludeCheckService.Json);

    public IncludeCheckControl(DbConnectionService connections)
    {
        _connections = connections;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => HandleSafe(root.GetRawText());
        _web.Ready += SendInit;
    }

    private void SendInit()
    {
        var ws = _connections.Current;
        _web.Call($"incheck.init({J(new { catalog = IncludeCheckService.LoadAll(), project = ws?.Name ?? "", source = ws?.SourcePath ?? "", userFolder = IncludeCheckService.UserFolder })})");
    }

    private void HandleSafe(string raw)
    {
        try { Handle(raw); }
        catch (Exception ex) { _web.Call($"incheck.onError({J(ex.Message)})"); }
    }

    private void Handle(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        string S(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        switch (S("action"))
        {
            case "ready": SendInit(); break;

            case "analyze": _ = AnalyzeAsync(S("source"), root.TryGetProperty("useDb", out var u) && u.ValueKind == JsonValueKind.True); break;

            case "browse":
            {
                using var dlg = new FolderBrowserDialog { Description = "Chọn thư mục dự án (thư mục chứa App_Data\\Controllers)", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(S("source")) ? S("source") : "" };
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) _web.Call($"incheck.onPicked({J(dlg.SelectedPath)})");
                break;
            }

            case "fixInclude":
            {
                if (_controllers.Length == 0) throw new InvalidOperationException("Chưa phân tích — chưa biết thư mục dự án.");
                var rel = S("rel"); var state = S("state");
                if (MessageBox.Show(FindForm(), $"Sửa file {rel} thành {state}?\n(Tự lưu bản .bak của file gốc 1 lần.)", "Check Include", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) break;
                IncludeCheckService.SetIncludeState(_controllers, rel, state);
                _web.Call($"incheck.onFixed({J(new { rel, state })})");
                break;
            }

            case "copy":
                try { Clipboard.SetText(S("text")); } catch { /* clipboard bận */ }
                break;

            case "saveFeature":
            {
                var f = root.GetProperty("feature").Deserialize<IncFeature>(IncludeCheckService.Json) ?? new IncFeature();
                IncludeCheckService.SaveFeature(f, root.TryGetProperty("original", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null);
                _web.Call($"incheck.onCatalog({J(IncludeCheckService.LoadAll())}, {J("Đã lưu \"" + f.Title + "\" vào nhóm " + f.Group)})");
                break;
            }

            case "deleteFeature":
                IncludeCheckService.DeleteFeature(S("group"), S("title"));
                _web.Call($"incheck.onCatalog({J(IncludeCheckService.LoadAll())}, {J("Đã xoá khỏi catalog của bạn.")})");
                break;

            case "resetGroup":
                if (MessageBox.Show(FindForm(), $"Bỏ mọi chỉnh sửa của nhóm \"{S("group")}\" và dùng lại catalog có sẵn?", "Check Include", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) break;
                IncludeCheckService.ResetGroup(S("group"));
                _web.Call($"incheck.onCatalog({J(IncludeCheckService.LoadAll())}, {J("Đã khôi phục nhóm " + S("group"))})");
                break;

            case "importXlsx":
            {
                using var dlg = new OpenFileDialog { Title = "Import catalog từ Excel (FBO_Catalog.xlsx)", Filter = "Excel (*.xlsx)|*.xlsx" };
                if (dlg.ShowDialog(FindForm()) != DialogResult.OK) break;
                var feats = IncludeCheckService.ImportXlsx(dlg.FileName);
                foreach (var g in feats.GroupBy(f => f.Group))
                    foreach (var f in g) IncludeCheckService.SaveFeature(f, null);
                _web.Call($"incheck.onCatalog({J(IncludeCheckService.LoadAll())}, {J("Đã import " + feats.Count + " tính năng từ " + Path.GetFileName(dlg.FileName))})");
                break;
            }

            case "openFolder":
                Directory.CreateDirectory(IncludeCheckService.UserFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{IncludeCheckService.UserFolder}\"") { UseShellExecute = true });
                break;

            case "openFile":
            {
                var p = Path.Combine(_controllers, S("rel").Replace('/', '\\').TrimStart('\\'));
                if (File.Exists(p)) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true });
                break;
            }

            case "revealFile":
            {
                var p = Path.Combine(_controllers, S("rel").Replace('/', '\\').TrimStart('\\'));
                if (File.Exists(p)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true });
                break;
            }

            case "parsePaste":
            {
                var (conds, guide) = IncludeCheckService.ParseGuidePaste(S("text"));
                _web.Call($"incheck.onPasted({J(new { conds, guide })})");
                break;
            }

            case "refs":
            {
                var rel = S("rel"); var ctl = _controllers;
                _ = Task.Run(() =>
                {
                    var hits = ctl.Length == 0 ? new List<(string Rel, int Line)>() : IncludeCheckService.FindReferences(ctl, rel);
                    if (!IsDisposed) BeginInvoke(() => _web.Call($"incheck.onRefs({J(new { rel, hits = hits.Select(h => new { file = h.Rel, line = h.Line }) })})"));
                });
                break;
            }
        }
    }

    private async Task AnalyzeAsync(string source, bool useDb)
    {
        if (_running) return;
        _running = true;
        _web.Call("incheck.busy(true)");
        try
        {
            var controllers = IncludeCheckService.ControllersOf(source);
            _controllers = controllers;
            var catalog = IncludeCheckService.LoadAll();
            Dictionary<string, string?>? options = null; Dictionary<string, List<string>>? wcs = null; var dbNote = "";
            if (useDb)
            {
                if (_connections.Current is null) dbNote = "Chưa chọn workspace — bỏ qua điều kiện option / wcommand.";
                else
                {
                    try { options = await ReadOptionsAsync(catalog.SelectMany(f => f.Conditions).Where(c => c.Kind == "option").Select(c => c.Key).Distinct().ToList()); }
                    catch (Exception ex) { dbNote += "Không đọc được bảng options (App DB): " + ex.Message + "  "; }
                    try { wcs = await ReadWcommandsAsync(catalog.SelectMany(f => f.Conditions).Where(c => c.Kind == "wcommand").Select(c => c.Key).Distinct().ToList()); }
                    catch (Exception ex) { dbNote += "Không đọc được bảng wcommand9 (Sys DB): " + ex.Message; }
                }
            }
            var (results, files) = await Task.Run(() => (IncludeCheckService.Evaluate(catalog, controllers, options, wcs), IncludeCheckService.ListSwitchFiles(controllers, catalog)));
            var note = controllers.Length == 0 ? "Không thấy thư mục App_Data\\Controllers\\Include trong: " + source : dbNote;
            if (!IsDisposed) _web.Call($"incheck.onResult({J(new { results, files, controllers, note })})");
        }
        catch (Exception ex) { _web.Call($"incheck.onError({J(ex.Message)})"); }
        finally { _running = false; if (!IsDisposed) _web.Call("incheck.busy(false)"); }
    }

    private async Task<Dictionary<string, string?>> ReadOptionsAsync(List<string> names)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return d;
        await using var conn = _connections.CreateConnection(false); await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT RTRIM(name), RTRIM(CAST(val AS NVARCHAR(4000))) FROM options WHERE RTRIM(name) IN (" + string.Join(",", names.Select((_, i) => "@p" + i)) + ")", conn) { CommandTimeout = 30 };
        for (var i = 0; i < names.Count; i++) cmd.Parameters.AddWithValue("@p" + i, names[i]);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync()) d[rd.GetString(0)] = rd.IsDBNull(1) ? "" : rd.GetString(1);
        return d;
    }

    private async Task<Dictionary<string, List<string>>> ReadWcommandsAsync(List<string> groups)
    {
        var d = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (groups.Count == 0) return d;
        await using var conn = _connections.CreateConnection(true); await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT RTRIM(xgroup), RTRIM(CAST(status AS VARCHAR(10))) FROM wcommand9 WHERE RTRIM(xgroup) IN (" + string.Join(",", groups.Select((_, i) => "@p" + i)) + ")", conn) { CommandTimeout = 30 };
        for (var i = 0; i < groups.Count; i++) cmd.Parameters.AddWithValue("@p" + i, groups[i]);
        await using var rd = await cmd.ExecuteReaderAsync();
        while (await rd.ReadAsync())
        {
            var g = rd.GetString(0);
            if (!d.TryGetValue(g, out var l)) d[g] = l = new List<string>();
            l.Add(rd.IsDBNull(1) ? "" : rd.GetString(1));
        }
        return d;
    }
}
