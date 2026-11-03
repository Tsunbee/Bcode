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

    // Giá trị mặc định giống form cũ; nhóm dịch vụ / Portal Key / link API theo tool Setup HDDT (VB).
    private static EInvoiceInputs Defaults => new("KOG", "FBO", "05", "007986", EInvoiceSetupService.FastProxyCode, EInvoiceSetupService.FastPortalPublicKey,
        EInvoiceSetupService.DefaultPortalLink, "hddt@namkimcorp.vn", "", EInvoiceSetupService.DefaultMiddleLink, "", true, false);

    private static string ConfigPath => Path.Combine(BcodePaths.AppData, "Bcode", "einvoice.json");

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

    /// <summary>Đổi project ở ô WS → ô CSDL Proxy lấy database eInv của project mới (nếu project có khai trong DB Access).</summary>
    private void OnWorkspaceChanged()
    {
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
        EInvoiceInputs inputs;
        try { inputs = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<EInvoiceInputs>(File.ReadAllText(ConfigPath), JsonOpts) ?? Defaults : Defaults; }
        catch { inputs = Defaults; }
        // einvoice.json cũ: link .../UpdateKey → .../UpdateProxy; chưa có Portal Key thì điền khoá Portal Fast.
        // CSDL Proxy: project đang chọn có khai database eInv trong DB Access thì lấy luôn (einvoice.json dùng chung mọi project nên giá trị cũ dễ là của project khác).
        inputs = inputs with { Password = "", MiddleLink = EInvoiceSetupService.NormalizeMiddleLink(inputs.MiddleLink ?? ""),
            PortalKey = string.IsNullOrWhiteSpace(inputs.PortalKey) ? EInvoiceSetupService.FastPortalPublicKey : inputs.PortalKey,
            DbProxy = EInvoiceSetupService.ProxyDatabaseOf(_connections.Current) ?? inputs.DbProxy ?? "" };
        Js($"setupEInvoice.init({J(inputs)})");
        _ = SendUnitsAsync();
    }

    private static void Save(EInvoiceInputs i)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(i with { Password = "" }, JsonOpts));   // không ghi mật khẩu ra đĩa
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
                {
                    if (_busy) return;
                    var inputs = root.GetProperty("inputs").Deserialize<EInvoiceInputs>(JsonOpts) ?? Defaults;
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
        }
    }
}
