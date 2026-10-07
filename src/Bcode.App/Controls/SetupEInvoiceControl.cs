using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Setup eInvoice (FE)": khai báo thông tin hoá đơn điện tử rồi xem script (Step 1), chạy script vào Proxy / App database (Step 2) hoặc gọi API UpdateKey.
/// Giao diện là trang WebView2 (Web/Shell/setupeinvoice.html — ăn theo Template giao diện, tự co giãn theo màn hình); logic nằm ở <see cref="EInvoiceSetupService"/>
/// (dùng chung với form cũ). Nhớ lại các ô nhập lần trước ở %AppData%\Bcode\einvoice.json (KHÔNG lưu mật khẩu).
/// </summary>
public class SetupEInvoiceControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly WebBarHost _web = new("setupeinvoice.html") { Dock = DockStyle.Fill };
    private readonly EInvoiceSetupService _service;
    private bool _busy;

    // Giá trị mặc định giống form cũ.
    private static EInvoiceInputs Defaults => new("KOG", "FBO", "05", "007986", "006129", "", "https://tportal.fast.com.vn/AppService/FastEInvoice.PortalService.asmx",
        "hddt@namkimcorp.vn", "", "http://dev.fast.com.vn/Fast-EInvoice-Crypto/Fast-Api.asmx/UpdateKey", "", true);

    private static string ConfigPath => Path.Combine(BcodePaths.AppData, "Bcode", "einvoice.json");

    public SetupEInvoiceControl(DbConnectionService connections)
    {
        _service = new EInvoiceSetupService(connections);
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
        EInvoiceInputs inputs;
        try { inputs = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<EInvoiceInputs>(File.ReadAllText(ConfigPath), JsonOpts) ?? Defaults : Defaults; }
        catch { inputs = Defaults; }
        Js($"setupEInvoice.init({J(inputs with { Password = "" })})");
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
                Js($"setupEInvoice.onScript({J(EInvoiceSetupService.BuildScript(i))})");
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
                var (ok, text) = await _service.UpdateKeyAsync(i, line => Js($"setupEInvoice.onLog({J(line)}, 'info')"));
                Js($"setupEInvoice.onLog({J(text)}, {(ok ? "'ok'" : "'err'")}); setupEInvoice.onDone({(ok ? "true" : "false")})");
                break;
            }
        }
    }
}
