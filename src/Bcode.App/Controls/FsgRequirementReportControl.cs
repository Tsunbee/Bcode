using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Báo cáo yêu cầu FSG" (menu Triển khai): kéo yêu cầu từ database FSG (<see cref="FsgProjectLookupService.FetchRequirementReportAsync"/>) về lưới,
/// lọc theo nhóm trạng thái / chữ, tô màu yêu cầu tới hạn chưa hoàn thành. Giao diện là trang WebView2 (Web/Shell/fsgreq.html — ăn theo Template giao diện);
/// control này chỉ lấy dữ liệu (CHỈ ĐỌC) và đẩy về trang. Dữ liệu đã kéo được LƯU THEO TỪNG DỰ ÁN (kéo KOG không làm mất VPMILK đã kéo trước đó).
/// </summary>
public class FsgRequirementReportControl : UserControl
{
    private readonly WebBarHost _web = new("fsgreq.html") { Dock = DockStyle.Fill };
    private readonly Func<Bcode.App.Models.Workspace?> _workspace;
    private readonly Func<string> _programmer;
    private bool _busy;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <param name="workspace">Project đang chọn — điền sẵn mã dự án.</param>
    /// <param name="programmer">Tên lập trình đã khai ở Note (New) — điền sẵn ô lọc lập trình.</param>
    public FsgRequirementReportControl(Func<Bcode.App.Models.Workspace?> workspace, Func<string> programmer)
    {
        _workspace = workspace;
        _programmer = programmer;
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

    // ---- Dữ liệu đã lưu theo từng dự án (%AppData%\Bcode\fsg-requirements.json) ----
    private sealed record Req(string MaDa, string MenuId, string TenMenuNgan, string TrangTlks, string TlksYn, string BpLt, string Fcode1, string NoiDung, string MaLt1, string MaNv1, string TrangThai, string? NgayHt);

    private sealed class ProjectData
    {
        public string FetchedAt { get; set; } = "";
        public List<Req> Rows { get; set; } = new();
    }

    private sealed class Store
    {
        public Dictionary<string, string> Filters { get; set; } = new();
        public Dictionary<string, ProjectData> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string CachePath => Path.Combine(BcodePaths.AppData, "Bcode", "fsg-requirements.json");

    private static Store LoadStore()
    {
        try
        {
            if (!File.Exists(CachePath)) return new Store();
            using var doc = JsonDocument.Parse(File.ReadAllText(CachePath));
            var root = doc.RootElement;
            var st = new Store();
            if (root.TryGetProperty("filters", out var f) && f.ValueKind == JsonValueKind.Object)
                st.Filters = f.Deserialize<Dictionary<string, string>>(JsonOpts) ?? new();
            if (root.TryGetProperty("projects", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var kv in p.EnumerateObject()) st.Projects[kv.Name] = kv.Value.Deserialize<ProjectData>(JsonOpts) ?? new();
            else if (root.TryGetProperty("rows", out var rows))      // bản lưu cũ (một lần kéo duy nhất) → tách theo dự án
            {
                var at = root.TryGetProperty("fetchedAt", out var a) ? a.GetString() ?? "" : "";
                foreach (var g in (rows.Deserialize<List<Req>>(JsonOpts) ?? new()).GroupBy(r => r.MaDa, StringComparer.OrdinalIgnoreCase))
                    st.Projects[g.Key] = new ProjectData { FetchedAt = at, Rows = g.ToList() };
            }
            return st;
        }
        catch { return new Store(); /* file hỏng: coi như chưa có */ }
    }

    private static void SaveStore(Store st)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(st, JsonOpts));
        }
        catch { /* không lưu được thì lần sau kéo lại */ }
    }

    /// <summary>Đẩy TOÀN BỘ dữ liệu đã lưu (mọi dự án) về trang, kèm danh sách dự án đã lưu (giờ kéo, số yêu cầu).</summary>
    private void SendAll(Store st, string? error, bool cached)
    {
        var rows = st.Projects.Values.SelectMany(p => p.Rows).ToList();
        var projects = st.Projects.Select(kv => new { ma = kv.Key, at = kv.Value.FetchedAt, n = kv.Value.Rows.Count }).OrderBy(x => x.ma, StringComparer.OrdinalIgnoreCase).ToList();
        var latest = st.Projects.Values.Select(p => p.FetchedAt).Where(a => a.Length > 0).DefaultIfEmpty("").Max() ?? "";
        Js($"fsgReq.onData({J(new { rows, projects, error, cached, today = DateTime.Today.ToString("yyyy-MM-dd"), fetchedAt = latest })})");
    }

    private void SendInit()
    {
        var ws = _workspace();
        var code = ws is null ? "" : (string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.Name : ws.ProjectId).Trim();
        var st = LoadStore();
        if (st.Projects.Count > 0)
        {
            Js($"fsgReq.init({J(st.Filters.Count > 0 ? st.Filters : new Dictionary<string, string> { ["maDa"] = code, ["programmer"] = _programmer() ?? "" })})");
            SendAll(st, null, cached: true);
            return;
        }
        Js($"fsgReq.init({J(new { maDa = code, programmer = _programmer() ?? "" })})");
    }

    private async Task HandleAsync(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            string Str(string n) => root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            switch (root.GetProperty("action").GetString())
            {
                case "ready": SendInit(); break;
                case "fetch": await FetchAsync(Str("maDa"), Str("bpLt"), Str("programmer"), Str("deployer"), Str("person"), Str("fcode"), Str("content")); break;
                case "drop":    // bỏ dữ liệu đã lưu của một dự án
                {
                    var st = LoadStore(); st.Projects.Remove(Str("ma")); SaveStore(st); SendAll(st, null, cached: true);
                    break;
                }
                case "copy": try { Clipboard.SetText(Str("text")); } catch { /* clipboard bận */ } break;
            }
        }
        catch (Exception ex) { Js($"fsgReq.onData({J(new { rows = (object?)null, error = ex.Message })})"); }
    }

    private async Task FetchAsync(string maDa, string bpLt, string programmer, string deployer, string person, string fcode, string content)
    {
        if (_busy) return;
        _busy = true;
        Js("fsgReq.setBusy(true)");
        try
        {
            var (rows, error) = await new FsgProjectLookupService().FetchRequirementReportAsync(maDa, bpLt, programmer, deployer, person, fcode, content);
            var st = LoadStore();
            if (error is null)
            {
                var now = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
                var data = rows.Select(r => new Req(r.MaDa, r.MenuId, r.TenMenuNgan, r.TrangTlks, r.TlksYn, r.BpLt, r.Fcode1, r.NoiDung, r.MaLt1, r.MaNv1, r.TrangThai, r.NgayHt?.ToString("yyyy-MM-dd"))).ToList();
                // Chỉ thay dữ liệu của các dự án vừa kéo (có trong kết quả hoặc nằm trong ô Mã dự án); dự án khác đã lưu giữ nguyên.
                foreach (var code in maDa.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) st.Projects.Remove(code);
                foreach (var g in data.GroupBy(r => r.MaDa, StringComparer.OrdinalIgnoreCase))
                    st.Projects[g.Key] = new ProjectData { FetchedAt = now, Rows = g.ToList() };
                st.Filters = new Dictionary<string, string> { ["maDa"] = maDa, ["bpLt"] = bpLt, ["person"] = person, ["programmer"] = programmer, ["deployer"] = deployer, ["fcode"] = fcode, ["content"] = content };
                SaveStore(st);
            }
            SendAll(st, error, cached: false);
        }
        finally { _busy = false; Js("fsgReq.setBusy(false)"); }
    }
}
