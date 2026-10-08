using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Check LCTT / CĐKT": người dùng đính kèm bảng kê chứng từ, bảng cân đối phát sinh, báo cáo CĐKT, báo cáo LCTT hiện tại và 2 file khai báo chỉ tiêu
/// (CĐKT, LCTT) — tool chỉ ra chỉ tiêu lưu chuyển tiền tệ và chỉ tiêu cân đối kế toán lệch ở đâu (xem <see cref="CashFlowDiagnosticService"/>).
/// Giao diện là trang WebView2 (Web/Shell/cfsdiag.html) tự co giãn; control này chỉ chọn file, chạy kiểm tra ở nền và trả kết quả.
/// </summary>
public class CashFlowCheckControl : UserControl
{
    private static readonly string[] Slots = { "journal", "tb", "bsRep", "cfRep", "bsCfg", "cfCfg", "dirRep", "dirCfg" };
    private static readonly string[] CheckKeys = { "chkIndirect", "chkDirect", "chkBalance" };

    private readonly WebBarHost _web = new("cfsdiag.html") { Dock = DockStyle.Fill };
    private readonly Dictionary<string, string> _paths = new();
    private bool _running;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);
    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "cfs-diag.json");

    public CashFlowCheckControl()
    {
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        LoadPaths();
        _web.Message += root => _ = HandleAsync(root.GetRawText());
        _web.Ready += () => _web.Call($"cfsDiag.init({J(new { paths = _paths, checks = ChecksOf() })})");
    }

    private bool IsOn(string key) => !_paths.TryGetValue(key, out var v) || v != "0";   // mặc định bật
    private object ChecksOf() => new { indirect = IsOn("chkIndirect"), direct = IsOn("chkDirect"), balance = IsOn("chkBalance") };

    private void LoadPaths()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StatePath));
            if (saved is null) return;
            foreach (var k in Slots.Concat(CheckKeys)) if (saved.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) _paths[k] = v;
        }
        catch { /* file nhớ đường dẫn hỏng — bỏ qua, người dùng chọn lại */ }
    }

    private void SavePaths()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(_paths));
        }
        catch { /* không lưu được thì chỉ mất việc nhớ đường dẫn */ }
    }

    private async Task HandleAsync(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            switch (root.GetProperty("action").GetString())
            {
                case "pick":
                {
                    var slot = root.GetProperty("slot").GetString() ?? "";
                    if (Array.IndexOf(Slots, slot) < 0) break;
                    using var dlg = new OpenFileDialog
                    {
                        Title = "Chọn file Excel (.xlsx)",
                        Filter = "Excel (*.xlsx)|*.xlsx|Tất cả|*.*",
                        InitialDirectory = _paths.TryGetValue(slot, out var cur) && File.Exists(cur) ? Path.GetDirectoryName(cur)! : "",
                    };
                    if (dlg.ShowDialog(FindForm()) != DialogResult.OK) break;
                    _paths[slot] = dlg.FileName;
                    SavePaths();
                    _web.Call($"cfsDiag.onPicked({J(slot)}, {J(dlg.FileName)})");
                    break;
                }

                case "paths":
                    foreach (var k in Slots)
                        if (root.TryGetProperty("paths", out var p) && p.TryGetProperty(k, out var v) && !string.IsNullOrWhiteSpace(v.GetString())) _paths[k] = v.GetString()!.Trim().Trim('"');
                        else _paths.Remove(k);
                    if (root.TryGetProperty("checks", out var ck))
                    {
                        _paths["chkIndirect"] = ck.TryGetProperty("indirect", out var a) && a.ValueKind == JsonValueKind.False ? "0" : "1";
                        _paths["chkDirect"] = ck.TryGetProperty("direct", out var b) && b.ValueKind == JsonValueKind.False ? "0" : "1";
                        _paths["chkBalance"] = ck.TryGetProperty("balance", out var c) && c.ValueKind == JsonValueKind.False ? "0" : "1";
                    }
                    SavePaths();
                    break;

                case "run":
                    await RunAsync();
                    break;

                case "copy":
                    try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { /* clipboard bận */ }
                    break;

                case "save":
                {
                    using var dlg = new SaveFileDialog { Title = "Lưu kết quả", Filter = "Text (*.txt)|*.txt", FileName = "ketqua-check-lctt-cdkt.txt" };
                    if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
                        File.WriteAllText(dlg.FileName, root.GetProperty("text").GetString() ?? "", new System.Text.UTF8Encoding(true));
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _web.Call($"cfsDiag.onError({J(ex.Message)})");
        }
    }

    private async Task RunAsync()
    {
        if (_running) return;
        _running = true;
        _web.Call("cfsDiag.busy(true)");
        try
        {
            var inp = new CfsInputs
            {
                Journal = Get("journal"), TrialBalance = Get("tb"), BalanceReport = Get("bsRep"),
                CashFlowReport = Get("cfRep"), BalanceConfig = Get("bsCfg"), CashFlowConfig = Get("cfCfg"),
                DirectReport = Get("dirRep"), DirectConfig = Get("dirCfg"),
                RunIndirect = IsOn("chkIndirect"), RunDirect = IsOn("chkDirect"), RunBalance = IsOn("chkBalance"),
            };
            var result = await Task.Run(() => CashFlowDiagnosticService.Run(inp));
            if (!IsDisposed) _web.Call($"cfsDiag.onResult({J(result)})");
        }
        catch (Exception ex)
        {
            _web.Call($"cfsDiag.onError({J(ex.Message)})");
        }
        finally
        {
            _running = false;
            if (!IsDisposed) _web.Call("cfsDiag.busy(false)");
        }

        string? Get(string slot) => _paths.TryGetValue(slot, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }
}
