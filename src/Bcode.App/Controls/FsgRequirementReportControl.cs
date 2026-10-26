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
        // Thông báo y/c (chuông 🔔) kéo nền xong và cập nhật fsg-requirements.json → vẽ lại dữ liệu đã lưu (giữ các lọc đang chọn).
        FsgNotifyService.StoreUpdated += OnStoreUpdated;
        Disposed += (_, _) => FsgNotifyService.StoreUpdated -= OnStoreUpdated;
    }

    private void OnStoreUpdated()
    {
        if (IsDisposed || !_web.IsReady || _busy) return;
        SendAll(LoadStore(), null, cached: true);
    }

    // Bấm 1 dòng ở khung Thông báo → mở khung chi tiết của y/c đó (chờ trang sẵn sàng như ShowForMenu).
    private (string MaDa, string Fcode1)? _pendingReq;

    /// <summary>Khung Thông báo y/c FSG: mở khung chi tiết của y/c (mã dự án + mã YC) — có nút 🤖 AI tạo danh mục như thường.</summary>
    public void ShowRequirement(string maDa, string fcode1)
    {
        if (!_web.IsReady) { _pendingReq = (maDa, fcode1); return; }
        Js($"fsgReq.openReq({J(new { maDa, fcode1 })})");
    }

    /// <summary>"🤖 AI tạo danh mục" cho y/c đang mở ở khung chi tiết (mã y/c, nội dung) — MainForm mở Tạo nhanh danh mục, như Note (New).</summary>
    public event Action<string, string>? AiCatalogRequested;

    /// <summary>Phím tắt note.aiCatalog: bấm nút của khung chi tiết (chưa mở y/c nào thì trang nhắc).</summary>
    public void RequestAiCatalog() => Js("window.fsgReq && fsgReq.aiCatalog && fsgReq.aiCatalog()");

    /// <summary>"🔎 AI phân tích" cho y/c đang mở ở khung chi tiết (mã y/c, nội dung) — MainForm mở Tạo nhanh danh mục ở bước xác nhận bản phân tích.</summary>
    public event Action<string, string>? AiAnalyzeRequested;

    /// <summary>Phím tắt note.aiAnalyze: bấm nút 🔎 AI phân tích của khung chi tiết.</summary>
    public void RequestAiAnalyze() => Js("window.fsgReq && fsgReq.aiAnalyze && fsgReq.aiAnalyze()");

    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    /// <summary>Menu đang chọn trên cây WCommand (chuột phải → "Yêu cầu liên quan (QLYC)…"): wmenu_id, tên Bar / Bar2, wmenu_id các menu con
    /// và menu cha, đường dẫn hiển thị. Trang lọc y/c theo quy tắc ở fsgreq.html (menuWhy): menu_id của y/c FSG CÓ DẠNG wmenu_id ("09.30.03").</summary>
    public sealed record MenuQuery(string WMenuId, string Bar, string Bar2, List<string> ChildIds, List<string> AncestorIds, string Path);

    // Chờ trang sẵn sàng (tab vừa mở). Trang gửi "ready" TRƯỚC khi WebBarHost nhận lệnh JS (NavigationCompleted) nên xét _web.IsReady.
    private MenuQuery? _pendingMenu;

    private string ProjectCode()
    {
        var ws = _workspace();
        return ws is null ? "" : (string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.Name : ws.ProjectId).Trim();
    }

    /// <summary>Lọc sẵn theo menu cho dự án đang chọn. Ưu tiên dữ liệu đã lưu (fsg-requirements.json); dự án chưa kéo lần nào thì tự kéo
    /// (chỉ theo mã dự án, chỉ đọc, KHÔNG đổi các ô lọc đã lưu của tab).</summary>
    public void ShowForMenu(MenuQuery menu)
    {
        if (!_web.IsReady) { _pendingMenu = menu; return; }
        ApplyMenu(menu);
    }

    private void ApplyMenu(MenuQuery menu)
    {
        var code = ProjectCode();
        Js($"fsgReq.menuFilter({J(new { maDa = code, id = menu.WMenuId, bar = menu.Bar, bar2 = menu.Bar2, childIds = menu.ChildIds, ancestorIds = menu.AncestorIds, path = menu.Path })})");
        if (code.Length == 0) return;
        var st = LoadStore();
        if (!st.Projects.ContainsKey(code)) _ = FetchAsync(code, "", "", "", "", "", "", keepFilters: true);
    }

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    // ---- Dữ liệu đã lưu theo từng dự án (%AppData%\Bcode\fsg-requirements.json) ----
    internal sealed record Req(string MaDa, string MenuId, string TenMenuNgan, string TrangTlks, string TlksYn, string BpLt, string Fcode1, string NoiDung, string MaLt1, string MaNv1, string TrangThai, string? NgayHt, string GhiChu = "");

    internal sealed class ProjectData
    {
        public string FetchedAt { get; set; } = "";
        public List<Req> Rows { get; set; } = new();
    }

    internal sealed class Store
    {
        public Dictionary<string, string> Filters { get; set; } = new();
        public Dictionary<string, ProjectData> Projects { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string CachePath => Path.Combine(BcodePaths.AppData, "Bcode", "fsg-requirements.json");

    internal static Store LoadStore()
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

    internal static void SaveStore(Store st)
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
        var code = ProjectCode();
        var st = LoadStore();
        if (st.Projects.Count > 0)
        {
            Js($"fsgReq.init({J(st.Filters.Count > 0 ? st.Filters : new Dictionary<string, string> { ["maDa"] = code, ["programmer"] = _programmer() ?? "" })})");
            SendAll(st, null, cached: true);
        }
        else Js($"fsgReq.init({J(new { maDa = code, programmer = _programmer() ?? "" })})");
        Js($"fsgReq.setAuto && fsgReq.setAuto({FsgNotifyService.Instance?.AutoPullMinutes ?? 0})");
        if (_web.IsReady && _pendingMenu is { } m) { _pendingMenu = null; ApplyMenu(m); }
        if (_web.IsReady && _pendingReq is { } q) { _pendingReq = null; Js($"fsgReq.openReq({J(new { maDa = q.MaDa, fcode1 = q.Fcode1 })})"); }
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
                case "autoPull":    // ô "Tự làm mới": kéo ngầm tự động cả khi đóng tab (FsgNotifyService), 0 = tắt
                    FsgNotifyService.Instance?.SetAutoPull(root.TryGetProperty("minutes", out var mv) && mv.TryGetInt32(out var mi) ? mi : 0);
                    break;
                case "fetch": await FetchAsync(Str("maDa"), Str("bpLt"), Str("programmer"), Str("deployer"), Str("person"), Str("fcode"), Str("content")); break;
                case "drop":    // bỏ dữ liệu đã lưu của một dự án
                {
                    var st = LoadStore(); st.Projects.Remove(Str("ma")); SaveStore(st); SendAll(st, null, cached: true);
                    break;
                }
                case "dropMany":    // bỏ dữ liệu đã lưu của nhiều dự án (các dự án đang khớp ô tìm) hoặc tất cả
                {
                    Store st;
                    lock (FsgNotifyService.StoreLock)   // kéo ngầm (thông báo / tự làm mới) cũng đọc-sửa-ghi file này
                    {
                        st = LoadStore();
                        if (root.TryGetProperty("all", out var allV) && allV.ValueKind == JsonValueKind.True) st.Projects.Clear();
                        else if (root.TryGetProperty("mas", out var mas) && mas.ValueKind == JsonValueKind.Array)
                            foreach (var m in mas.EnumerateArray()) if (m.ValueKind == JsonValueKind.String) st.Projects.Remove(m.GetString() ?? "");
                        SaveStore(st);
                    }
                    SendAll(st, null, cached: true);
                    break;
                }
                case "copy": try { Clipboard.SetText(Str("text")); } catch { /* clipboard bận */ } break;
                case "aiCatalog": AiCatalogRequested?.Invoke(Str("name"), Str("content")); break;
                case "aiAnalyze": AiAnalyzeRequested?.Invoke(Str("name"), Str("content")); break;
            }
        }
        catch (Exception ex) { Js($"fsgReq.onData({J(new { rows = (object?)null, error = ex.Message })})"); }
    }

    internal static Req ToReq(FsgProjectLookupService.RequirementRow r) =>
        new(r.MaDa, r.MenuId, r.TenMenuNgan, r.TrangTlks, r.TlksYn, r.BpLt, r.Fcode1, r.NoiDung, r.MaLt1, r.MaNv1, r.TrangThai, r.NgayHt?.ToString("yyyy-MM-dd"), r.GhiChu);

    /// <summary>Ghép lần kéo vào bản lưu: chỉ thay dữ liệu của các dự án vừa kéo (có trong kết quả hoặc nằm trong ô Mã dự án); dự án khác đã lưu giữ nguyên.
    /// Dùng chung cho "Kéo về" và kéo ngầm tự động (<see cref="FsgNotifyService"/>).</summary>
    internal static void ApplyFetched(Store st, string maDa, List<Req> data, string now)
    {
        foreach (var code in maDa.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) st.Projects.Remove(code);
        foreach (var g in data.GroupBy(r => r.MaDa, StringComparer.OrdinalIgnoreCase))
            st.Projects[g.Key] = new ProjectData { FetchedAt = now, Rows = g.ToList() };
    }

    /// <param name="keepFilters">true = kéo ngầm cho "Yêu cầu liên quan" — không ghi đè các ô lọc đã lưu của tab.</param>
    private async Task FetchAsync(string maDa, string bpLt, string programmer, string deployer, string person, string fcode, string content, bool keepFilters = false)
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
                var data = rows.Select(ToReq).ToList();
                // Thông báo y/c (chuông 🔔): so lần kéo tay này với dữ liệu đã lưu trước đó — thay đổi trên y/c của mình không bị "nuốt" mất vì mốc so đã dời.
                FsgNotifyService.Instance?.RecordFetch(st, data);
                ApplyFetched(st, maDa, data, now);
                // Kéo ngầm cho "Yêu cầu liên quan" mà FSG không có y/c nào: lưu mục rỗng — trang báo "không có" thay vì kéo lại mỗi lần chuột phải.
                if (keepFilters && data.Count == 0 && maDa.Trim().Length > 0) st.Projects[maDa.Trim()] = new ProjectData { FetchedAt = now };
                if (!keepFilters) st.Filters = new Dictionary<string, string> { ["maDa"] = maDa, ["bpLt"] = bpLt, ["person"] = person, ["programmer"] = programmer, ["deployer"] = deployer, ["fcode"] = fcode, ["content"] = content };
                SaveStore(st);
            }
            SendAll(st, error, cached: false);
        }
        finally { _busy = false; Js("fsgReq.setBusy(false)"); }
    }
}
