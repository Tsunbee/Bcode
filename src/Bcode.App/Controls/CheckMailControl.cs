using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Check Mail": khai báo host / port / SSL-TLS / tài khoản gửi rồi Test kết nối hoặc gửi thử 1 email để biết cấu hình SMTP có chạy không
/// (giống màn Check Mail của FCode). Giao diện là trang WebView2 (Web/Shell/checkmail.html — ăn theo Template giao diện, tự co giãn theo
/// màn hình); control này lưu cấu hình (KHÔNG lưu mật khẩu) và chạy <see cref="MailTestService"/>, đẩy từng dòng tiến trình về trang.
/// </summary>
public class CheckMailControl : UserControl
{
    private readonly WebBarHost _web = new("checkmail.html") { Dock = DockStyle.Fill };
    private CancellationTokenSource? _cts;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private static string ConfigPath => Path.Combine(BcodePaths.AppData, "Bcode", "checkmail.json");

    private readonly Func<Bcode.App.Models.Workspace?> _workspace;

    /// <param name="workspace">Project đang chọn — để lấy sẵn cấu hình mail từ App_DataControllersOptionsMessage.xml của source.</param>
    public CheckMailControl(Func<Bcode.App.Models.Workspace?>? workspace = null)
    {
        _workspace = workspace ?? (() => null);
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => _ = HandleAsync(root.GetRawText());
        _web.Ready += SendInit;
        Disposed += (_, _) => { try { _cts?.Cancel(); } catch { /* đã huỷ */ } };
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
        MailConfig cfg;
        try { cfg = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<MailConfig>(File.ReadAllText(ConfigPath), JsonOpts) ?? new MailConfig() : new MailConfig(); }
        catch { cfg = new MailConfig(); }
        cfg.Password = ""; // mật khẩu không lưu
        Js($"checkMail.init({J(cfg)})");
        // Mặc định theo project đang chọn (Message.xml): host/port/SSL/tài khoản... — đè lên giá trị đã lưu lần trước; người dùng vẫn sửa tay được.
        LoadFromProject(announceFailure: false);
    }

    /// <summary>Đọc Message.xml (và EmailConfig nếu có) của project đang chọn rồi điền vào form.</summary>
    private void LoadFromProject(bool announceFailure)
    {
        var ws = _workspace();
        var sourcePath = ws?.SourcePath;
        _ = Task.Run(() =>
        {
            var (cfg, note) = MailSettingsReader.Read(sourcePath);
            if (cfg is not null) Js($"checkMail.applyConfig({J(cfg)}, {J(note)})");
            else if (announceFailure) Js($"checkMail.onLog({J(note)}, 'err')");
        });
    }

    private async Task HandleAsync(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            switch (root.GetProperty("action").GetString())
            {
                case "ready": SendInit(); break;

                case "loadProject":
                    LoadFromProject(announceFailure: true);
                    break;

                case "save":
                    SaveConfig(root.GetProperty("cfg").Deserialize<MailConfig>(JsonOpts) ?? new MailConfig());
                    break;

                case "send":
                case "test":
                {
                    var cfg = root.GetProperty("cfg").Deserialize<MailConfig>(JsonOpts) ?? new MailConfig();
                    SaveConfig(cfg);
                    await RunAsync(cfg, sendMail: root.GetProperty("action").GetString() == "send");
                    break;
                }

                case "cancel":
                    try { _cts?.Cancel(); } catch { /* đã xong */ }
                    break;

                case "copy":
                    try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { /* clipboard bận */ }
                    break;
            }
        }
        catch (Exception ex)
        {
            Js($"checkMail.onLog({J(ex.Message)}, 'err'); checkMail.onDone(false)");
        }
    }

    private async Task RunAsync(MailConfig cfg, bool sendMail)
    {
        if (_cts is not null) return; // đang chạy 1 lần rồi
        _cts = new CancellationTokenSource();
        Js("checkMail.onStart()");
        var ok = false;
        try
        {
            ok = await MailTestService.RunAsync(cfg, sendMail, (line, kind) => Js($"checkMail.onLog({J(line)}, {J(kind)})"), _cts.Token);
        }
        catch (Exception ex)
        {
            Js($"checkMail.onLog({J(ex.Message)}, 'err')");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Js($"checkMail.onDone({(ok ? "true" : "false")})");
        }
    }

    private static void SaveConfig(MailConfig cfg)
    {
        try
        {
            cfg.Password = ""; // không ghi mật khẩu ra đĩa
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch { /* không lưu được thì lần sau nhập lại */ }
    }
}
