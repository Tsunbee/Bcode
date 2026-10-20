using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Mail kế hoạch tuần" (menu Triển khai): khai báo sẵn mẫu mail báo cáo/kế hoạch tuần theo từng dự án (nhiều dự án, mỗi dự án nhiều mẫu; người nhận To/Cc/Bcc như mail thường),
/// điền ngày tuần vừa rồi / tuần tới tự động rồi gửi. Gửi qua cùng cấu hình SMTP và cùng <see cref="MailTestService"/> với tab Check Mail (checkmail.json — host/port/SSL/tài khoản dùng chung,
/// mật khẩu không lưu trừ khi tick "Nhớ mật khẩu" — mã hoá DPAPI). Giao diện là trang WebView2 Web/Shell/weeklymail.html (tự co giãn theo màn hình); control này chỉ giữ dữ liệu + gửi.
/// Tab con "Đã gửi &amp; phản hồi": thư gửi thành công được lưu .eml + chép lên Sent qua IMAP (tuỳ chọn), kiểm tra phản hồi ở INBOX, xem, Reply / Reply all, gửi lại
/// — logic ở <see cref="WeeklyMailHistory"/>.
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
            imap = ImapForUi(),
            password = WeeklyMailStore.LoadRememberedPassword(),
            history = WeeklyMailHistory.ForUi(),
        };
        Js($"weeklyMail.init({J(data)})");
    }

    private static object ImapForUi() { var s = WeeklyMailStore.LoadImap(); return new { s.Host, s.Port, s.Ssl, s.CopyToSent, s.RememberPassword }; }

    private void SendHistory() => Js($"weeklyMail.onHistory({J(WeeklyMailHistory.ForUi())})");

    private static string S(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>Cấu hình IMAP trang gửi kèm (mục Máy chủ mail); lưu lại luôn, kèm mật khẩu nếu đang tick "Nhớ mật khẩu".</summary>
    private static WeeklyMailImapSettings ReadImap(JsonElement root, string? password)
    {
        var s = root.TryGetProperty("imap", out var v) ? v.Deserialize<WeeklyMailImapSettings>(JsonOpts) ?? new() : WeeklyMailStore.LoadImap();
        try { WeeklyMailStore.SaveImap(s, password); } catch { /* không lưu được thì lần sau nhập lại */ }
        return s;
    }

    private static string Summary(MailConfig c) =>
        $"Người gửi: {c.SenderEmail}\nTo: {c.To}" + (string.IsNullOrWhiteSpace(c.Cc) ? "" : $"\nCc: {c.Cc}") + (string.IsNullOrWhiteSpace(c.Bcc) ? "" : $"\nBcc: {c.Bcc}") + $"\nTiêu đề: {c.Subject}";

    /// <summary>Chạy 1 việc dài (gửi lại / trả lời / kiểm tra phản hồi) — 1 việc 1 lúc, có nút Huỷ, log về trang.</summary>
    private async Task RunOpAsync(string busy, Func<Action<string, string>, CancellationToken, Task<string?>> op)
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        Js($"weeklyMail.onStart({J(busy)})");
        string? done = null;
        try { done = await op((line, kind) => Js($"weeklyMail.onLog({J(line)}, {J(kind)})"), _cts.Token); }
        catch (OperationCanceledException) { Js("weeklyMail.onLog('Đã huỷ.', 'err')"); }
        catch (Exception ex) { Js($"weeklyMail.onLog({J(ex.Message)}, 'err')"); }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Js($"weeklyMail.onDone({(done is not null ? "true" : "false")}, {J(done ?? "")})");
            SendHistory();
        }
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
                {
                    var cfg = root.GetProperty("cfg").Deserialize<MailConfig>(JsonOpts) ?? new MailConfig();
                    var meta = new WeeklyMailSendMeta { Project = S(root, "project"), TemplateId = S(root, "templateId"), TemplateName = S(root, "templateName") };
                    await SendAsync(cfg, meta, ReadImap(root, cfg.Password));
                    break;
                }

                case "saveImap":
                    ReadImap(root, S(root, "password"));
                    break;

                case "history": SendHistory(); break;

                case "view":
                    Js($"weeklyMail.onView({J(S(root, "kind"))}, {J(S(root, "id"))}, {J(WeeklyMailHistory.View(S(root, "kind"), S(root, "id")))})");
                    break;

                case "draftReply":
                    Js($"weeklyMail.onDraft({J(WeeklyMailHistory.DraftReply(S(root, "kind"), S(root, "id"), root.TryGetProperty("all", out var all) && all.GetBoolean(), S(root, "me")))})");
                    break;

                case "checkReplies":
                {
                    var cfg = root.GetProperty("cfg").Deserialize<MailConfig>(JsonOpts) ?? new MailConfig();
                    var imap = ReadImap(root, cfg.Password);
                    await RunOpAsync("Đang kiểm tra phản hồi...", async (log, ct) =>
                    {
                        try { var n = await WeeklyMailHistory.CheckRepliesAsync(cfg, imap, log, ct); return n > 0 ? $"✔ {n} phản hồi mới" : "Không có phản hồi mới"; }
                        catch (Exception ex) when (ex is not OperationCanceledException) { log("Không kiểm tra được phản hồi: " + ex.Message, "err"); return null; }
                    });
                    break;
                }

                case "resend":
                {
                    var cfg = root.GetProperty("cfg").Deserialize<MailConfig>(JsonOpts) ?? new MailConfig();
                    var imap = ReadImap(root, cfg.Password);
                    var id = S(root, "id");
                    if (WeeklyMailHistory.ResendConfig(id, cfg) is not { } c) { Js("weeklyMail.onLog('Không đọc được file .eml của thư này.', 'err')"); break; }
                    if (MessageBox.Show(FindForm(), "Gửi lại thư này?\n\n" + Summary(c), "Bcode — Mail kế hoạch tuần", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { Js("weeklyMail.onLog('Đã huỷ — chưa gửi.', 'dim')"); break; }
                    WeeklyMailStore.SaveSmtp(c);
                    await RunOpAsync("Đang gửi lại...", async (log, ct) => await WeeklyMailHistory.SendAndSaveAsync(c, WeeklyMailHistory.MetaOf("sent", id, "resend"), imap, null, log, ct) ? "✔ Đã gửi lại" : null);
                    break;
                }

                case "reply":
                {
                    var cfg = root.GetProperty("cfg").Deserialize<MailConfig>(JsonOpts) ?? new MailConfig();
                    var imap = ReadImap(root, cfg.Password);
                    string kind = S(root, "kind"), id = S(root, "id");
                    var draft = WeeklyMailHistory.DraftReply(kind, id, false, cfg.SenderEmail);
                    if (draft is null) { Js("weeklyMail.onLog('Không đọc được thư gốc.', 'err')"); break; }
                    cfg.Html = true;
                    cfg.Body = WeeklyMailHistory.ComposeReply(S(root, "text"), root.TryGetProperty("withSig", out var ws) && ws.GetBoolean() ? draft.Signature : "", draft.QuoteHtml);
                    if (MessageBox.Show(FindForm(), "Gửi thư trả lời này?\n\n" + Summary(cfg), "Bcode — Mail kế hoạch tuần", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { Js("weeklyMail.onLog('Đã huỷ — chưa gửi.', 'dim')"); break; }
                    WeeklyMailStore.SaveSmtp(cfg);
                    var headers = WeeklyMailHistory.ReplyHeaders(kind, id);
                    await RunOpAsync("Đang gửi trả lời...", async (log, ct) =>
                    {
                        var ok = await WeeklyMailHistory.SendAndSaveAsync(cfg, WeeklyMailHistory.MetaOf(kind, id, "reply"), imap, headers, log, ct);
                        if (ok) Js("weeklyMail.onReplied()");
                        return ok ? "✔ Đã gửi trả lời" : null;
                    });
                    break;
                }

                case "openEml":
                {
                    var p = WeeklyMailHistory.PathOf(S(root, "kind"), S(root, "id"));
                    if (p.Length > 0 && File.Exists(p)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true });
                    else Js("weeklyMail.onLog('Không thấy file .eml.', 'err')");
                    break;
                }

                case "openFolder":
                    Directory.CreateDirectory(WeeklyMailHistory.Dir);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(WeeklyMailHistory.Dir) { UseShellExecute = true });
                    break;

                case "delete":
                {
                    string kind = S(root, "kind"), id = S(root, "id");
                    var root2 = kind != "reply" && WeeklyMailHistory.Load().Sent.FirstOrDefault(e => e.Id == id) is { } e2 && e2.ThreadId == e2.Id;
                    var ask = kind == "reply" ? "Xoá phản hồi này khỏi Bcode?" : root2 ? "Xoá thư này cùng cả chuỗi trả lời / phản hồi khỏi mục Đã gửi của Bcode?" : "Xoá thư này khỏi mục Đã gửi của Bcode?";
                    if (MessageBox.Show(FindForm(), ask + "\n\n(Chỉ xoá bản lưu trong Bcode, không đụng hộp thư trên máy chủ.)", "Bcode — Mail kế hoạch tuần", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) break;
                    WeeklyMailHistory.Delete(kind, id);
                    SendHistory();
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
            Js($"weeklyMail.onLog({J(ex.Message)}, 'err'); weeklyMail.onDone(false)");
        }
    }

    private async Task SendAsync(MailConfig cfg, WeeklyMailSendMeta meta, WeeklyMailImapSettings imap)
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
        try { ok = await WeeklyMailHistory.SendAndSaveAsync(cfg, meta, imap, null, (line, kind) => Js($"weeklyMail.onLog({J(line)}, {J(kind)})"), _cts.Token); }
        catch (Exception ex) { Js($"weeklyMail.onLog({J(ex.Message)}, 'err')"); }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            Js($"weeklyMail.onDone({(ok ? "true" : "false")})");
            SendHistory();
        }
    }
}
