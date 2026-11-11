using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Setup eInvoice (FE)": khai báo thông tin hoá đơn điện tử rồi xem script (Step 1), chạy script vào App / Proxy database (Step 2) hoặc gọi API UpdateProxy.
/// Giao diện là trang WebView2 (Web/Shell/setupeinvoice.html — ăn theo Template giao diện, tự co giãn theo màn hình); logic nằm ở <see cref="EInvoiceSetupService"/>
/// (dùng chung với form cũ). Nhớ lại các ô nhập lần trước ở %AppData%\Bcode\einvoice.json (KHÔNG lưu mật khẩu).
/// </summary>
public class SetupEInvoiceControl : UserControl, ISleepableTab
{
    /// <summary>Ngủ đông khi tab ẩn lâu (xem <see cref="WebBarHost.Sleepable"/>).</summary>
    public Task<bool> SleepAsync() => _web.SleepAsync();

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly WebBarHost _web = new("setupeinvoice.html") { Dock = DockStyle.Fill, Sleepable = true };
    private readonly EInvoiceSetupService _service;
    private readonly DbConnectionService _connections;
    private bool _busy;

    // Dự án chưa cài lần nào: chỉ điền hằng số chung của Fast (sản phẩm FBO, Portal Key, Link Portal, link API) + CSDL {mã_dự_án}_eInv; các ô riêng của khách
    // (đơn vị, mã DN, nhóm dịch vụ, tài khoản portal) để trống để khỏi cài nhầm số của dự án khác. "Nhóm dịch vụ riêng" tick sẵn, ProxyCode trống.
    private static EInvoiceInputs Defaults(string project) => new(project, "FBO", "", "", "", EInvoiceSetupService.FastPortalPublicKey,
        EInvoiceSetupService.DefaultPortalLink, "", "", EInvoiceSetupService.DefaultMiddleLink, EInvoiceSetupService.DefaultProxyDatabase(project), true, true);

    /// <summary>%AppData%\Bcode\einvoice.json — {"projects": {"&lt;mã dự án&gt;": {...các ô...}}}: nhớ riêng từng dự án (bản cũ 1 bộ dùng chung → chuyển thành của dự án đã lưu).</summary>
    private static string ConfigPath => Path.Combine(BcodePaths.AppData, "Bcode", "einvoice.json");

    /// <summary>Mã dự án đã nạp lên trang lần gần nhất — đổi workspace sang dự án khác mới nạp lại cả trang (cùng dự án thì giữ các ô đang gõ).</summary>
    private string? _shownProject;

    public SetupEInvoiceControl(DbConnectionService connections)
    {
        _service = new EInvoiceSetupService(connections);
        _connections = connections;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => _ = HandleAsync(root.GetRawText());
        _web.Ready += SendInit;
        connections.WorkspaceChanged += OnWorkspaceChanged;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _connections.WorkspaceChanged -= OnWorkspaceChanged;   // DbConnectionService sống suốt phiên — không gỡ thì tab đã đóng sống mãi
        base.Dispose(disposing);
    }

    /// <summary>ID (ProjectId) của project đang vào; null khi chưa chọn / chưa khai ID.</summary>
    private string? ProjectIdOf() => _connections.Current?.ProjectId is { Length: > 0 } id ? id.Trim() : null;

    /// <summary>Đổi project ở ô WS sang dự án khác → nạp lại cả trang theo dự án mới (giá trị đã lưu của dự án đó, chưa có thì để trống — không mang số của dự án cũ sang).
    /// Cùng dự án: chỉ cập nhật ô CSDL Proxy (nếu project có khai database eInv trong DB Access) và gợi ý Đơn vị.</summary>
    private void OnWorkspaceChanged()
    {
        if (!string.Equals(ProjectIdOf() ?? "", _shownProject ?? "", StringComparison.OrdinalIgnoreCase)) { SendInit(); return; }
        if (EInvoiceSetupService.ProxyDatabaseOf(_connections.Current) is { } db) Js($"setupEInvoice.setDbProxy({J(db)})");
        _ = SendUnitsAsync();
    }

    /// <summary>Gợi ý ô Đơn vị: danh mục đơn vị cơ sở (dmdvcs) của App Data đang chọn — gửi sau init, đọc DB chạy nền nên không chặn lúc mở tab.</summary>
    private async Task SendUnitsAsync()
    {
        var units = await _service.LoadUnitsAsync();
        Js($"setupEInvoice.setUnits({J(units.Select(u => new { code = u.Code, name = u.Name }))})");
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
        // Mã dự án = ID project đang vào (Edit Project). Project chưa khai ID: để trống cả trang (không đoán dự án — dễ cài nhầm số của khách khác).
        var project = ProjectIdOf() ?? "";
        _shownProject = project;
        var inputs = project.Length > 0 && LoadStore().TryGetValue(project, out var saved) ? saved : Defaults(project);
        // Giá trị lưu cũ: link .../UpdateKey → .../UpdateProxy, http://dev.fast.com.vn → https; ô hằng số trống thì điền lại mặc định của Fast.
        // CSDL Proxy: ưu tiên database eInv khai trong DB Access của project, rồi giá trị đã lưu của chính dự án này, cuối cùng {mã_dự_án}_eInv.
        inputs = inputs with { Password = "", Project = project, MiddleLink = EInvoiceSetupService.NormalizeMiddleLink(inputs.MiddleLink ?? ""),
            PortalKey = string.IsNullOrWhiteSpace(inputs.PortalKey) ? EInvoiceSetupService.FastPortalPublicKey : inputs.PortalKey,
            PortalLink = string.IsNullOrWhiteSpace(inputs.PortalLink) ? EInvoiceSetupService.DefaultPortalLink : inputs.PortalLink,
            Product = string.IsNullOrWhiteSpace(inputs.Product) ? "FBO" : inputs.Product,
            DbProxy = EInvoiceSetupService.ProxyDatabaseOf(_connections.Current)
                ?? (string.IsNullOrWhiteSpace(inputs.DbProxy) ? EInvoiceSetupService.DefaultProxyDatabase(project) : inputs.DbProxy) };
        Js($"setupEInvoice.init({J(inputs)})");
        _ = SendUnitsAsync();
    }

    /// <summary>Các ô đã lưu theo mã dự án (không phân biệt hoa thường). einvoice.json bản cũ (1 bộ dùng chung) → coi là của dự án ghi trong đó.</summary>
    private static Dictionary<string, EInvoiceInputs> LoadStore()
    {
        var map = new Dictionary<string, EInvoiceInputs>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(ConfigPath)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return map;
            if (root.TryGetProperty("projects", out var ps))
            {
                if (ps.ValueKind == JsonValueKind.Object)
                    foreach (var p in ps.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.Object && p.Value.Deserialize<EInvoiceInputs>(JsonOpts) is { } i) map[p.Name.Trim()] = i;
            }
            else if (root.Deserialize<EInvoiceInputs>(JsonOpts) is { Project: { Length: > 0 } old } legacy) map[old.Trim()] = legacy;
        }
        catch { /* file hỏng: coi như chưa lưu gì */ }
        return map;
    }

    /// <summary>Nhớ các ô của dự án này (theo ô Mã dự án; trống thì không lưu). KHÔNG ghi mật khẩu ra đĩa.</summary>
    private static void Save(EInvoiceInputs i)
    {
        var key = i.Project?.Trim() ?? "";
        if (key.Length == 0) return;
        try
        {
            var map = LoadStore();
            map[key] = i with { Password = "" };
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new { projects = map }, JsonOpts));
        }
        catch { /* không lưu được thì lần sau nhập lại */ }
    }

    private async Task HandleAsync(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString();
            switch (action)
            {
                case "ready": SendInit(); break;
                case "copy": try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { /* clipboard bận */ } break;
                case "step1":
                case "step2":
                case "update":
                case "clear":
                {
                    if (_busy) return;
                    var inputs = root.GetProperty("inputs").Deserialize<EInvoiceInputs>(JsonOpts) ?? Defaults("");
                    Save(inputs);
                    _busy = true; Js("setupEInvoice.onBusy(true)");
                    try { await RunAsync(action!, inputs); }
                    finally { _busy = false; Js("setupEInvoice.onBusy(false)"); }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Js($"setupEInvoice.onLog({J("Exception: " + ex.Message)}, 'err')");
        }
    }

    private async Task RunAsync(string action, EInvoiceInputs i)
    {
        switch (action)
        {
            case "step1":
                if (EInvoiceSetupService.ValidateRequired(i) is { } missing) { Js($"setupEInvoice.onLog({J(missing)}, 'err')"); return; }
                Js($"setupEInvoice.onScript({J(await _service.BuildScriptAsync(i))})");
                break;

            case "step2":
            {
                Js("setupEInvoice.onClear()");
                var ok = await _service.ExecuteScriptsAsync(i, (line, kind) => Js($"setupEInvoice.onLog({J(line)}, {J(kind)})"));
                Js($"setupEInvoice.onDone({(ok ? "true" : "false")})");
                break;
            }

            case "update":
            {
                Js("setupEInvoice.onClear()");
                var ok = await _service.UpdateKeyAndRunAsync(i, (line, kind) => Js($"setupEInvoice.onLog({J(line)}, {J(kind)})"));
                Js($"setupEInvoice.onDone({(ok ? "true" : "false")})");
                break;
            }

            case "clear":   // xoá hết cấu hình HĐĐT để cài lại (đã xác nhận trong trang)
            {
                Js("setupEInvoice.onClear()");
                var ok = await _service.ClearAsync(i, (line, kind) => Js($"setupEInvoice.onLog({J(line)}, {J(kind)})"));
                Js($"setupEInvoice.onDone({(ok ? "true" : "false")})");
                break;
            }

            case "checkurl":   // kiểm Web.config: chưa có urlEInvoice thì khai luôn
            {
                Js("setupEInvoice.onClear()");
                var ok = await _service.EnsureUrlEInvoiceAsync(i, (line, kind) => Js($"setupEInvoice.onLog({J(line)}, {J(kind)})"));
                Js($"setupEInvoice.onDone({(ok ? "true" : "false")})");
                break;
            }
        }
    }
}
