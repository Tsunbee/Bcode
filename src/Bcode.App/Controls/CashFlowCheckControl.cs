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
    private void SendTplList() => _web.Call($"cfsDiag.onTplList({J(CfsFasts.Registry().Where(r => r.Id.StartsWith("cust-", StringComparison.Ordinal)).ToList())})");

    private static readonly string[] Slots = { "journal", "tb", "bsRep", "cfRep", "bsCfg", "cfCfg", "dirRep", "dirCfg", "dmtk" };
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

                // Khung dán kết quả select bộ chỉ tiêu (copy từ lưới kết quả SQL / Excel): lưu thành file rồi dùng như file đã chọn
                case "pasteCfg":
                {
                    var slot = root.GetProperty("slot").GetString() ?? "";
                    var text = root.GetProperty("text").GetString() ?? "";
                    if (Array.IndexOf(Slots, slot) < 0 || !(slot.EndsWith("Cfg", StringComparison.Ordinal) || slot == "dmtk") || text.Trim().Length == 0) break;
                    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "cfs-pasted");
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, slot + ".txt");
                    File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
                    _paths[slot] = file; SavePaths();
                    _web.Call($"cfsDiag.onPicked({J(slot)}, {J(file)})");
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

                // Lưu khai báo LCTT gián tiếp đang chọn làm "mẫu đã chạy đúng" của khách — lần sau tool đối chiếu theo mẫu này
                case "saveTpl":
                {
                    var customer = (root.TryGetProperty("customer", out var cu) ? cu.GetString() : "")?.Trim() ?? "";
                    var cfg = _paths.TryGetValue("cfCfg", out var cp) ? cp : "";
                    if (customer.Length == 0) break;
                    if (string.IsNullOrEmpty(cfg) || !File.Exists(cfg)) { _web.Call($"cfsDiag.onError({J("Chưa chọn / dán khai báo chỉ tiêu LCTT gián tiếp.")})"); break; }
                    var (_, msg) = CashFlowDiagnosticService.SaveCustomerTemplate(cfg, customer, _paths.TryGetValue("cfRep", out var rp) ? rp : null);
                    _web.Call($"cfsDiag.onError({J(msg)})");
                    break;
                }

                // ---- Thư viện mẫu chỉ tiêu của khách (import từ file / dán, không cần database của khách) ----
                case "tplList":
                    SendTplList();
                    break;

                case "importTpl":
                {
                    var customer = (root.TryGetProperty("customer", out var cu) ? cu.GetString() : "")?.Trim() ?? "";
                    var kind = root.TryGetProperty("kind", out var kd) ? kd.GetString() ?? "cf-indirect" : "cf-indirect";
                    var verified = root.TryGetProperty("verified", out var vf) && vf.ValueKind == JsonValueKind.True;
                    var text = root.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                    if (customer.Length == 0) { _web.Call($"cfsDiag.onError({J("Nhập tên khách hàng / database.")})"); break; }
                    string file;
                    if (text.Trim().Length > 0)
                    {
                        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "cfs-pasted");
                        Directory.CreateDirectory(dir);
                        file = Path.Combine(dir, "import-" + kind + ".txt");
                        File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
                    }
                    else
                    {
                        using var dlg = new OpenFileDialog { Title = "Chọn file bộ chỉ tiêu (Excel hoặc kết quả select lưu .txt)", Filter = "Excel / văn bản (*.xlsx;*.txt;*.tsv;*.csv)|*.xlsx;*.txt;*.tsv;*.csv|Tất cả|*.*" };
                        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) break;
                        file = dlg.FileName;
                    }
                    var (_, msg) = CashFlowDiagnosticService.ImportTemplate(file, customer, kind, verified);
                    _web.Call($"cfsDiag.onError({J(msg)})");
                    SendTplList();
                    break;
                }

                case "delTpl":
                {
                    var id = root.TryGetProperty("id", out var di) ? di.GetString() ?? "" : "";
                    if (id.StartsWith("cust-", StringComparison.Ordinal)) CfsFasts.Delete(id);
                    SendTplList();
                    break;
                }

                case "run":
                    await RunAsync();
                    break;

                // ---- Mẫu chuẩn (TT99 làm gốc): xem, ghi nhận bổ sung, khôi phục ----
                case "stdList":
                    _web.Call($"cfsDiag.onStd({J(CfsStandards.All().Select(CfsStandards.ToView))}, null)");
                    _web.Call($"cfsDiag.onFastForms({J(CfsFasts.Registry())})");
                    break;

                case "stdLearn":
                {
                    var id = root.GetProperty("id").GetString() ?? "";
                    var code = root.GetProperty("code").GetString() ?? "";
                    var std = CfsStandards.Get(id);
                    var line = std?.Line(code);
                    if (std is null || line is null) { _web.Call($"cfsDiag.onError({J("Không thấy chỉ tiêu " + code + " trong mẫu chuẩn.")})"); break; }
                    var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? line.Status : line.Status;
                    var note = root.TryGetProperty("note", out var nt) ? nt.GetString() ?? "" : line.Note;
                    var src = root.TryGetProperty("source", out var sc) ? sc.GetString() ?? "" : "";
                    var accs = root.TryGetProperty("acc", out var ac) ? (ac.GetString() ?? "") : null;
                    var what = $"Chỉ tiêu {code}: " + (status != line.Status ? $"trạng thái {line.Status} → {status}; " : "") + (note != line.Note ? "cập nhật ghi chú; " : "")
                               + (accs != null ? "cập nhật TK nguồn " + accs + "; " : "") + (src.Length > 0 ? "nguồn: " + src : "");
                    line.Status = status; line.Note = note;
                    if (accs != null && line.Sources.Count > 0)
                        line.Sources[0].Acc = accs.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    CfsStandards.SaveUser(std, what.TrimEnd(' ', ';'), src.Length > 0 ? src : "Người dùng ghi nhận");
                    _web.Call($"cfsDiag.onStd({J(CfsStandards.All().Select(CfsStandards.ToView))}, {J("Đã ghi nhận vào mẫu chuẩn (bản của bạn): " + code)})");
                    break;
                }

                // Khai báo lại / thêm chỉ tiêu (khi có thông tư mới hoặc chỉ tiêu bị thiếu)
                case "stdSaveLine":
                {
                    var id = root.GetProperty("id").GetString() ?? "";
                    var std = CfsStandards.Get(id);
                    if (std is null) break;
                    var j = root.GetProperty("line");
                    string S(string n) => j.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var code = S("code").Trim();
                    if (code.Length == 0) { _web.Call($"cfsDiag.onError({J("Chưa nhập mã số chỉ tiêu.")})"); break; }
                    var old = std.Line(code);
                    var line = new CfsStdLine
                    {
                        Code = code, Name = S("name").Trim(), Level = j.TryGetProperty("level", out var lv) && lv.TryGetInt32(out var lvi) ? lvi : 2, Status = S("status").Length > 0 ? S("status") : "std",
                        Formula = S("formula").Split(new[] { '+', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                        Negative = j.TryGetProperty("negative", out var ng) && ng.ValueKind == JsonValueKind.True, Pair = S("pair").Trim(), Flow = S("flow").Trim(), Note = S("note").Trim(),
                    };
                    // Nguồn: mỗi dòng "N|131,331|ngan|ghi chú" (N/C = dư Nợ/Có; với LCTT: bên Nợ/Có của TK tiền đối ứng) — trống = chỉ tiêu tổng / chưa có nguồn
                    foreach (var ln in S("sources").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var p = ln.Split('|');
                        var accs = (p.Length > 1 ? p[1] : p[0]).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                        if (accs.Count == 0) continue;
                        line.Sources.Add(new CfsStdSrc { Side = p.Length > 1 && p[0].Trim().ToUpperInvariant() is "N" or "C" ? p[0].Trim().ToUpperInvariant() : "N", Acc = accs, Term = p.Length > 2 ? p[2].Trim() : "", Note = p.Length > 3 ? p[3].Trim() : "" });
                    }
                    var after = S("after").Trim();
                    CfsStandards.UpsertLine(std, line, after, (old is null ? "Thêm chỉ tiêu " : "Khai báo lại chỉ tiêu ") + code + (S("source").Length > 0 ? " — nguồn: " + S("source") : ""));
                    _web.Call($"cfsDiag.onStd({J(CfsStandards.All().Select(CfsStandards.ToView))}, {J((old is null ? "Đã thêm chỉ tiêu " : "Đã khai báo lại chỉ tiêu ") + code + " vào mẫu " + id)})");
                    break;
                }

                case "stdDeleteLine":
                {
                    var std = CfsStandards.Get(root.GetProperty("id").GetString() ?? "");
                    if (std is null) break;
                    CfsStandards.DeleteLine(std, root.GetProperty("code").GetString() ?? "");
                    _web.Call($"cfsDiag.onStd({J(CfsStandards.All().Select(CfsStandards.ToView))}, {J("Đã xoá chỉ tiêu.")})");
                    break;
                }

                case "stdClone":
                {
                    var created = CfsStandards.Clone(root.GetProperty("from").GetString() ?? "", root.GetProperty("id").GetString() ?? "", root.GetProperty("title").GetString() ?? "", root.GetProperty("circular").GetString() ?? "");
                    _web.Call($"cfsDiag.onStd({J(CfsStandards.All().Select(CfsStandards.ToView))}, {J("Đã tạo mẫu mới: " + created.Title)}, {J(created.Id)})");
                    break;
                }

                case "stdActive":
                {
                    var std = CfsStandards.Get(root.GetProperty("id").GetString() ?? "");
                    if (std is null) break;
                    CfsStandards.SetActive(std.Kind, std.Id);
                    _web.Call($"cfsDiag.onStd({J(CfsStandards.All().Select(CfsStandards.ToView))}, {J("Từ lần Kiểm tra sau, " + std.Title + " (" + std.Circular + ") là mẫu chuẩn đang dùng.")})");
                    break;
                }

                case "stdReset":
                    CfsStandards.ResetUser(root.GetProperty("id").GetString() ?? "");
                    _web.Call($"cfsDiag.onStd({J(CfsStandards.All().Select(CfsStandards.ToView))}, {J("Đã khôi phục mẫu chuẩn gốc.")})");
                    break;

                case "stdOpenDir":
                    Directory.CreateDirectory(CfsStandards.UserDir);
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{CfsStandards.UserDir}\"") { UseShellExecute = true }); } catch { /* không mở được Explorer */ }
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
                DirectReport = Get("dirRep"), DirectConfig = Get("dirCfg"), AccountCatalog = Get("dmtk"),
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
