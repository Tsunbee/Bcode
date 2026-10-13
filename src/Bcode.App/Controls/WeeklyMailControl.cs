using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Mail kế hoạch tuần" (menu Triển khai): khai báo sẵn mẫu mail báo cáo/kế hoạch tuần theo từng dự án (nhiều dự án, mỗi dự án nhiều mẫu; người nhận To/Cc/Bcc như mail thường),
/// điền ngày tuần vừa rồi / tuần tới tự động rồi gửi. Gửi qua cùng cấu hình SMTP và cùng <see cref="MailTestService"/> với tab Check Mail (checkmail.json — host/port/SSL/tài khoản dùng chung,
/// mật khẩu không lưu). Giao diện là trang WebView2 Web/Shell/weeklymail.html (tự co giãn theo màn hình); control này chỉ giữ dữ liệu + gửi.
/// </summary>
public class WeeklyMailControl : UserControl
{
    private readonly WebBarHost _web = new("weeklymail.html") { Dock = DockStyle.Fill };
    private readonly Func<IReadOnlyList<string>> _projects;
    private readonly Func<string?> _currentProject;
    private CancellationTokenSource? _cts;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <param name="projects">Tên các dự án (workspace) để gợi ý ở ô "Dự án".</param>
    /// <param name="currentProject">Dự án đang chọn — danh sách mẫu mặc định lọc theo dự án này.</param>
    public WeeklyMailControl(Func<IReadOnlyList<string>> projects, Func<string?> currentProject)
    {
        _projects = projects;
        _currentProject = currentProject;
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
        var data = new
        {
            templates = WeeklyMailStore.LoadTemplates(),
            smtp = WeeklyMailStore.LoadSmtp(),
            projects = _projects(),
            current = _currentProject() ?? "",
        };
        Js($"weeklyMail.init({J(data)})");
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

                case "saveTemplates":
                    WeeklyMailStore.SaveTemplates(root.GetProperty("templates").Deserialize<List<WeeklyMailTemplate>>(JsonOpts) ?? new());
                    Js("weeklyMail.onSaved()");
                    break;

                case "saveSmtp":
                    WeeklyMailStore.SaveSmtp(root.GetProperty("cfg").Deserialize<MailConfig>(JsonOpts) ?? new MailConfig());
                    break;

                case "send":
                    await SendAsync(root.GetProperty("cfg").Deserialize<MailConfig>(JsonOpts) ?? new MailConfig());
                    break;

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
            Js($"weeklyMail.onLog({J(ex.Message)}, 'err'); weeklyMail.onDone(false)");
        }
    }

    private async Task SendAsync(MailConfig cfg)
    {
        if (_cts is not null) return; // đang gửi
        // Gửi mail ra ngoài là thao tác không thu hồi được → luôn hỏi lại, hiện rõ người nhận.
        var summary = $"Người gửi: {cfg.SenderEmail}\nTo: {cfg.To}" + (string.IsNullOrWhiteSpace(cfg.Cc) ? "" : $"\nCc: {cfg.Cc}") + (string.IsNullOrWhiteSpace(cfg.Bcc) ? "" : $"\nBcc: {cfg.Bcc}") + $"\nTiêu đề: {cfg.Subject}";
        if (MessageBox.Show(FindForm(), "Gửi mail này?\n\n" + summary, "Bcode — Mail kế hoạch tuần", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            Js("weeklyMail.onLog('Đã huỷ — chưa gửi.', 'dim')");
            return;
        }
        WeeklyMailStore.SaveSmtp(cfg);
        _cts = new CancellationTokenSource();
        Js("weeklyMail.onStart()");
        var ok = false;
        try { ok = await MailTestService.RunAsync(cfg, sendMail: true, (line, kind) => Js($"weeklyMail.onLog({J(line)}, {J(kind)})"), _cts.Token); }
        catch (Exception ex) { Js($"weeklyMail.onLog({J(ex.Message)}, 'err')"); }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Js($"weeklyMail.onDone({(ok ? "true" : "false")})");
        }
    }
}
