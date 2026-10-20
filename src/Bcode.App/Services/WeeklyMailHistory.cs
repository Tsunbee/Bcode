using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using MimeKit.Utils;

namespace Bcode.App.Services;

/// <summary>Thông tin kèm 1 lần gửi của tool Mail kế hoạch tuần (để lưu vào mục "Đã gửi").</summary>
public sealed class WeeklyMailSendMeta
{
    public string Project { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string TemplateName { get; set; } = "";
    /// <summary>mail / resend / reply.</summary>
    public string Kind { get; set; } = "mail";
    /// <summary>Chuỗi thư (thư trả lời) — null = thư mới, tự thành chuỗi riêng.</summary>
    public string? ThreadId { get; set; }
}

/// <summary>Bản nháp thư trả lời: người nhận / tiêu đề gợi ý + phần trích dẫn thư gốc + chữ ký của mẫu.</summary>
public sealed class WeeklyMailReplyDraft
{
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Signature { get; set; } = "";
    public string QuoteHtml { get; set; } = "";
    public string QuoteText { get; set; } = "";
}

/// <summary>Nội dung 1 thư để xem (đã gửi hoặc phản hồi).</summary>
public sealed class WeeklyMailView
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Bcc { get; set; } = "";
    public string Date { get; set; } = "";
    public string Subject { get; set; } = "";
    /// <summary>HTML (ảnh nhúng cid: đã đổi thành data:) — trống nếu thư chỉ có chữ thuần.</summary>
    public string Html { get; set; } = "";
    public string Text { get; set; } = "";
    public List<string> Attachments { get; set; } = new();
}

/// <summary>
/// Mục "Đã gửi & phản hồi" của tool Mail kế hoạch tuần: lưu bản sao mọi thư gửi thành công (.eml đầy đủ + index.json ở %AppData%\Bcode\weeklymail-sent\),
/// tuỳ chọn chép thư lên thư mục Sent của hộp thư qua IMAP (SMTP không tự lưu vào Sent nên Outlook / webmail không thấy thư Bcode gửi),
/// dò phản hồi ở INBOX (In-Reply-To / References trùng Message-ID thư đã gửi; dự phòng theo tiêu đề "Re:/TL: + tiêu đề gốc"), soạn thư trả lời.
/// Dùng chung cho WeeklyMailControl (Web) và NativeWeeklyMailControl (App).
/// </summary>
public static class WeeklyMailHistory
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private static readonly object Gate = new();
    /// <summary>Dò phản hồi tối đa bao nhiêu ngày trở lại (thư cũ hơn coi như đã xong).</summary>
    private const int MaxDays = 120;

    public static string Dir => Path.Combine(BcodePaths.AppData, "Bcode", "weeklymail-sent");
    private static string IndexPath => Path.Combine(Dir, "index.json");

    // =====================================================================  index

    public static WeeklyMailIndex Load()
    {
        lock (Gate)
        {
            try { if (File.Exists(IndexPath)) return JsonSerializer.Deserialize<WeeklyMailIndex>(File.ReadAllText(IndexPath), JsonOpts) ?? new(); }
            catch { /* index hỏng → coi như trống (file .eml vẫn còn) */ }
            return new();
        }
    }

    private static WeeklyMailIndex Update(Action<WeeklyMailIndex> change)
    {
        lock (Gate)
        {
            var idx = Load();
            change(idx);
            Directory.CreateDirectory(Dir);
            var tmp = IndexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(idx, JsonOpts));
            File.Move(tmp, IndexPath, overwrite: true);
            return idx;
        }
    }

    private static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    private static string Norm(string? messageId) => (messageId ?? "").Trim().Trim('<', '>').Trim();
    private static string Addresses(InternetAddressList list) => string.Join("; ", list.Mailboxes.Select(a => a.Address));
    private static string Display(InternetAddressList list) => string.Join("; ", list.Mailboxes.Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.Address : $"{a.Name} <{a.Address}>"));

    public static string PathOf(string kind, string id)
    {
        var idx = Load();
        var file = kind == "reply" ? idx.Replies.FirstOrDefault(r => r.Id == id)?.File : idx.Sent.FirstOrDefault(s => s.Id == id)?.File;
        return string.IsNullOrEmpty(file) ? "" : Path.Combine(Dir, file);
    }

    public static MimeMessage? LoadMessage(string kind, string id)
    {
        var p = PathOf(kind, id);
        try { return p.Length > 0 && File.Exists(p) ? MimeMessage.Load(p) : null; }
        catch { return null; }
    }

    private static WeeklyMailSentEntry SaveSent(MimeMessage m, WeeklyMailSendMeta meta)
    {
        var id = NewId();
        Directory.CreateDirectory(Dir);
        m.WriteTo(Path.Combine(Dir, id + ".eml"));
        var e = new WeeklyMailSentEntry
        {
            Id = id, MessageId = Norm(m.MessageId), SentAt = m.Date == DateTimeOffset.MinValue ? DateTimeOffset.Now : m.Date,
            Project = meta.Project ?? "", TemplateId = meta.TemplateId ?? "", TemplateName = meta.TemplateName ?? "",
            From = Addresses(m.From), To = Addresses(m.To), Cc = Addresses(m.Cc), Bcc = Addresses(m.Bcc), Subject = m.Subject ?? "",
            File = id + ".eml", Kind = meta.Kind ?? "mail", ThreadId = string.IsNullOrEmpty(meta.ThreadId) ? id : meta.ThreadId, InReplyTo = Norm(m.InReplyTo),
        };
        Update(i => i.Sent.Add(e));
        return e;
    }

    /// <summary>Xoá 1 thư khỏi mục Đã gửi (kèm file .eml). Thư gốc → xoá cả chuỗi (thư trả lời + phản hồi). Không đụng hộp thư trên máy chủ.</summary>
    public static void Delete(string kind, string id)
    {
        var files = new List<string>();
        Update(i =>
        {
            if (kind == "reply")
            {
                files.AddRange(i.Replies.Where(r => r.Id == id).Select(r => r.File));
                i.Replies.RemoveAll(r => r.Id == id);
                return;
            }
            var e = i.Sent.FirstOrDefault(s => s.Id == id);
            if (e is null) return;
            if (e.ThreadId == e.Id)
            {
                files.AddRange(i.Sent.Where(s => s.ThreadId == e.Id).Select(s => s.File));
                files.AddRange(i.Replies.Where(r => r.ThreadId == e.Id).Select(r => r.File));
                i.Sent.RemoveAll(s => s.ThreadId == e.Id);
                i.Replies.RemoveAll(r => r.ThreadId == e.Id);
            }
            else { files.Add(e.File); i.Sent.Remove(e); }
        });
        foreach (var f in files) try { if (f.Length > 0) File.Delete(Path.Combine(Dir, f)); } catch { /* file đang mở */ }
    }

    public static void MarkSeen(string id) => Update(i => { foreach (var r in i.Replies.Where(r => r.Id == id)) r.Seen = true; });

    // =====================================================================  gửi + lưu

    /// <summary>Gửi qua SMTP (y như <see cref="MailTestService.RunAsync"/>), thành công thì lưu bản sao vào mục Đã gửi và (nếu tick) chép lên thư mục Sent qua IMAP.
    /// Lưu / chép lỗi chỉ ghi cảnh báo — thư đã đi rồi nên vẫn tính là gửi thành công. Gọi SAU khi đã hỏi xác nhận người nhận.</summary>
    public static async Task<bool> SendAndSaveAsync(MailConfig c, WeeklyMailSendMeta meta, WeeklyMailImapSettings imap, Action<MimeMessage>? prepare, Action<string, string> log, CancellationToken ct)
    {
        MimeMessage? sent = null;
        var ok = await MailTestService.RunAsync(c, sendMail: true, log, ct, m =>
        {
            var at = c.SenderEmail.IndexOf('@');
            m.MessageId = MimeUtils.GenerateMessageId(at >= 0 ? c.SenderEmail[(at + 1)..].Trim() : "bcode.local");
            m.Date = DateTimeOffset.Now;
            prepare?.Invoke(m);
        }, m => sent = m);
        if (!ok || sent is null) return ok;
        WeeklyMailSentEntry? entry = null;
        try { entry = SaveSent(sent, meta); log("Đã lưu bản sao vào mục “Đã gửi” của Bcode.", "ok"); }
        catch (Exception ex) { log("Không lưu được bản sao thư đã gửi: " + ex.Message, "err"); }
        if (imap.CopyToSent && await AppendToSentAsync(c, imap, sent, log, ct) && entry is not null)
            try { Update(i => { foreach (var e in i.Sent.Where(e => e.Id == entry.Id)) e.CopiedToServer = true; }); } catch { /* chỉ là cờ hiển thị */ }
        return true;
    }

    /// <summary>Cấu hình gửi lại 1 thư cũ: cùng người nhận / tiêu đề / nội dung (Message-ID mới, chuỗi mới).</summary>
    public static MailConfig? ResendConfig(string id, MailConfig server)
    {
        var m = LoadMessage("sent", id);
        if (m is null) return null;
        var html = m.HtmlBody;
        server.To = Addresses(m.To); server.Cc = Addresses(m.Cc); server.Bcc = Addresses(m.Bcc); server.Subject = m.Subject ?? "";
        server.Html = html is not null; server.Body = html ?? m.TextBody ?? "";
        return server;
    }

    public static WeeklyMailSendMeta MetaOf(string kind, string id, string newKind)
    {
        var idx = Load();
        var thread = kind == "reply" ? idx.Replies.FirstOrDefault(r => r.Id == id)?.ThreadId : idx.Sent.FirstOrDefault(s => s.Id == id)?.ThreadId;
        var root = idx.Sent.FirstOrDefault(s => s.Id == thread) ?? idx.Sent.FirstOrDefault(s => s.Id == id);
        return new WeeklyMailSendMeta
        {
            Project = root?.Project ?? "", TemplateId = root?.TemplateId ?? "", TemplateName = root?.TemplateName ?? "", Kind = newKind,
            ThreadId = newKind == "reply" ? thread : null,
        };
    }

    // =====================================================================  IMAP

    private static string Explain(Exception ex) => ex switch
    {
        MailKit.Security.AuthenticationException => $"lỗi đăng nhập ({ex.Message}) — kiểm tra tài khoản / mật khẩu",
        SslHandshakeException => $"lỗi SSL/TLS ({ex.Message}) — 993 = SSL, 143 = STARTTLS; hoặc tick “Bỏ qua lỗi chứng chỉ”",
        System.Net.Sockets.SocketException or TimeoutException or IOException => $"không kết nối được ({ex.Message.TrimEnd('.', ' ')}) — kiểm tra host / cổng IMAP",
        OperationCanceledException => "đã huỷ",
        _ => ex.Message,
    };

    private static async Task<ImapClient> ConnectImapAsync(MailConfig c, WeeklyMailImapSettings s, Action<string, string> log, CancellationToken ct)
    {
        var host = string.IsNullOrWhiteSpace(s.Host) ? c.Host.Trim() : s.Host.Trim();
        if (host.Length == 0) throw new InvalidOperationException("chưa khai báo máy chủ IMAP");
        var user = string.IsNullOrWhiteSpace(c.Account) ? c.SenderEmail.Trim() : c.Account.Trim();
        if (user.Length == 0) throw new InvalidOperationException("chưa nhập tài khoản / email gửi");
        if (string.IsNullOrWhiteSpace(c.Password)) throw new InvalidOperationException("chưa nhập mật khẩu (IMAP luôn cần đăng nhập)");
        var port = s.Port is > 0 and <= 65535 ? s.Port : 993;
        var opt = !s.Ssl ? SecureSocketOptions.None : port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        var client = new ImapClient { Timeout = Math.Clamp(c.TimeoutSeconds, 5, 300) * 1000 };
        if (c.IgnoreCert) client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        try
        {
            log($"IMAP: kết nối {host}:{port}...", "info");
            await client.ConnectAsync(host, port, opt, ct);
            await client.AuthenticateAsync(user, c.Password.Trim(), ct);
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static readonly string[] SentNames = { "Sent", "Sent Items", "Sent Messages", "Sent Mail", "Đã gửi", "Thư đã gửi", "Mục đã gửi" };

    private static async Task<IMailFolder?> FindSentFolderAsync(ImapClient client, CancellationToken ct)
    {
        if ((client.Capabilities & (ImapCapabilities.SpecialUse | ImapCapabilities.XList)) != 0)
            try { if (client.GetFolder(SpecialFolder.Sent) is { } f) return f; } catch { /* máy chủ báo hỗ trợ nhưng không đánh dấu Sent */ }
        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var all = await client.GetFoldersAsync(ns, false, ct);
        return all.FirstOrDefault(f => (f.Attributes & FolderAttributes.Sent) != 0)
            ?? SentNames.Select(n => all.FirstOrDefault(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(f => f is not null)
            ?? all.FirstOrDefault(f => f.Name.StartsWith("Sent", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Chép thư lên thư mục Sent của hộp thư (đánh dấu đã đọc). Lỗi → ghi log, trả false.</summary>
    public static async Task<bool> AppendToSentAsync(MailConfig c, WeeklyMailImapSettings s, MimeMessage m, Action<string, string> log, CancellationToken ct)
    {
        try
        {
            using var client = await ConnectImapAsync(c, s, log, ct);
            var folder = await FindSentFolderAsync(client, ct);
            if (folder is null) { log("Không tìm thấy thư mục Sent trên hộp thư — thư chỉ lưu trong Bcode.", "err"); return false; }
            await folder.AppendAsync(new AppendRequest(m, MessageFlags.Seen) { InternalDate = m.Date }, ct);
            log($"Đã chép thư lên thư mục “{folder.FullName}” của hộp thư (Outlook / webmail sẽ thấy).", "ok");
            try { await client.DisconnectAsync(true, CancellationToken.None); } catch { /* đã chép xong */ }
            return true;
        }
        catch (Exception ex)
        {
            log($"Không chép được lên thư mục Sent của hộp thư: {Explain(ex)}. Thư vẫn đã gửi và đã lưu trong Bcode.", "err");
            return false;
        }
    }

    private static readonly Regex ReplyPrefix = new(@"^\s*((re|tl|trả\s*lời|aw|sv|antw|r)\s*(\[\d+\])?\s*[:：]\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AnyPrefix = new(@"^\s*((re|tl|trả\s*lời|aw|sv|antw|r|fw|fwd|tv|chuyển\s*tiếp)\s*(\[\d+\])?\s*[:：]\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string Bare(string? subject) => Regex.Replace(AnyPrefix.Replace(subject ?? "", ""), @"\s+", " ").Trim();
    public static bool IsReplySubject(string? subject) => ReplyPrefix.IsMatch(subject ?? "");

    /// <summary>Kiểm tra INBOX: thư có In-Reply-To / References trùng Message-ID thư đã gửi (dự phòng: tiêu đề "Re:/TL: + tiêu đề gốc"), tải về weeklymail-sent\replies\.</summary>
    /// <returns>Số phản hồi mới.</returns>
    public static async Task<int> CheckRepliesAsync(MailConfig c, WeeklyMailImapSettings s, Action<string, string> log, CancellationToken ct)
    {
        var idx = Load();
        if (idx.Sent.Count == 0) { log("Chưa có thư nào trong mục Đã gửi để dò phản hồi.", "info"); return 0; }
        using var client = await ConnectImapAsync(c, s, log, ct);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
        var floor = DateTimeOffset.Now.AddDays(-MaxDays);
        var earliest = idx.Sent.Min(e => e.SentAt);
        var since = (earliest < floor ? floor : earliest).LocalDateTime.Date.AddDays(-1);
        log($"IMAP: đã mở INBOX ({inbox.Count:N0} thư). Dò thư nhận từ {since:dd/MM/yyyy}...", "info");
        var uids = await inbox.SearchAsync(SearchQuery.DeliveredAfter(since), ct);
        var items = uids.Count == 0 ? new List<IMessageSummary>() : (await inbox.FetchAsync(uids, MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.References | MessageSummaryItems.InternalDate, ct)).ToList();

        var byMid = idx.Sent.Where(e => e.MessageId.Length > 0).GroupBy(e => e.MessageId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(idx.Replies.Select(r => r.MessageId), StringComparer.OrdinalIgnoreCase);
        var roots = idx.Sent.Where(e => e.ThreadId == e.Id).ToList();
        var found = new List<(IMessageSummary Item, WeeklyMailSentEntry Sent, bool ByHeader, string Mid)>();
        foreach (var it in items)
        {
            var env = it.Envelope;
            if (env is null) continue;
            var mid = Norm(env.MessageId);
            if (mid.Length == 0) mid = $"imap:{inbox.UidValidity}:{it.UniqueId.Id}";
            if (byMid.ContainsKey(mid) || known.Contains(mid)) continue;   // chính thư mình (tự Cc) / đã tải
            WeeklyMailSentEntry? hit = null;
            var byHeader = true;
            if (byMid.TryGetValue(Norm(env.InReplyTo), out var h)) hit = h;
            else if (it.References is { Count: > 0 } refs)
                for (var i = refs.Count - 1; i >= 0 && hit is null; i--) if (byMid.TryGetValue(Norm(refs[i]), out h)) hit = h;
            if (hit is null && IsReplySubject(env.Subject))
            {
                // Mẫu tuần nào cũng cùng tiêu đề → lấy thư gốc cùng tiêu đề gửi gần nhất TRƯỚC ngày nhận.
                var bare = Bare(env.Subject);
                var date = env.Date ?? it.InternalDate ?? DateTimeOffset.Now;
                hit = roots.Where(e => e.SentAt <= date && string.Equals(Bare(e.Subject), bare, StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.SentAt).FirstOrDefault();
                byHeader = false;
            }
            if (hit is null) continue;
            known.Add(mid);
            found.Add((it, hit, byHeader, mid));
        }

        var added = new List<WeeklyMailReplyEntry>();
        if (found.Count > 0)
        {
            var dir = Path.Combine(Dir, "replies");
            Directory.CreateDirectory(dir);
            foreach (var f in found)
            {
                ct.ThrowIfCancellationRequested();
                var msg = await inbox.GetMessageAsync(f.Item.UniqueId, ct);
                var id = NewId();
                msg.WriteTo(Path.Combine(dir, id + ".eml"));
                added.Add(new WeeklyMailReplyEntry
                {
                    Id = id, MessageId = f.Mid, ThreadId = f.Sent.ThreadId, SentId = f.Sent.Id, From = Display(msg.From), To = Addresses(msg.To), Cc = Addresses(msg.Cc),
                    Date = msg.Date == DateTimeOffset.MinValue ? f.Item.InternalDate ?? DateTimeOffset.Now : msg.Date, Subject = msg.Subject ?? "", File = "replies/" + id + ".eml", ByHeader = f.ByHeader,
                });
            }
        }
        Update(i =>
        {
            var have = new HashSet<string>(i.Replies.Select(r => r.MessageId), StringComparer.OrdinalIgnoreCase);
            i.Replies.AddRange(added.Where(a => have.Add(a.MessageId)));
            i.LastCheck = DateTimeOffset.Now;
        });
        try { await client.DisconnectAsync(true, CancellationToken.None); } catch { /* đã tải xong */ }
        var guessed = added.Count(a => !a.ByHeader);
        log(added.Count == 0 ? $"Không có phản hồi mới (đã xem {items.Count:N0} thư)." : $"Có {added.Count} phản hồi mới" + (guessed > 0 ? $" ({guessed} thư khớp theo tiêu đề — có thể nhầm tuần)." : "."), added.Count > 0 ? "ok" : "info");
        return added.Count;
    }

    // =====================================================================  xem / trả lời

    /// <summary>HTML → chữ thuần (xem trước ở chế độ App, trích dẫn dạng chữ).</summary>
    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html ?? "", @"<(script|style|head)\b[^>]*>.*?</\1\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|tr|li|h\d|blockquote|table)\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s).Replace(" ", " ");
        s = Regex.Replace(s, @"[ \t]+\n", "\n");
        return Regex.Replace(s, @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>Đổi ảnh nhúng "cid:" thành data: để xem được trong khung HTML (bỏ qua ảnh &gt; 3 MB).</summary>
    private static string InlineCid(MimeMessage m, string html)
    {
        if (!html.Contains("cid:", StringComparison.OrdinalIgnoreCase)) return html;
        foreach (var part in m.BodyParts.OfType<MimePart>().Where(p => !string.IsNullOrEmpty(p.ContentId)))
        {
            try
            {
                using var ms = new MemoryStream();
                part.Content?.DecodeTo(ms);
                if (ms.Length == 0 || ms.Length > 3_000_000) continue;
                html = html.Replace("cid:" + part.ContentId, $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}", StringComparison.OrdinalIgnoreCase);
            }
            catch { /* ảnh hỏng */ }
        }
        return html;
    }

    public static WeeklyMailView? View(string kind, string id)
    {
        var m = LoadMessage(kind, id);
        if (m is null) return null;
        if (kind == "reply") try { MarkSeen(id); } catch { /* chỉ là cờ đã xem */ }
        var html = m.HtmlBody;
        return new WeeklyMailView
        {
            From = Display(m.From), To = Display(m.To), Cc = Display(m.Cc), Bcc = Display(m.Bcc), Date = m.Date.LocalDateTime.ToString("dd/MM/yyyy HH:mm"), Subject = m.Subject ?? "",
            Html = html is null ? "" : InlineCid(m, html), Text = m.TextBody ?? (html is null ? "" : HtmlToText(html)),
            Attachments = m.Attachments.Select(a => a is MimePart p ? p.FileName ?? "(tệp)" : "(thư đính kèm)").ToList(),
        };
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Gợi ý thư trả lời: Reply = người gửi (thư của chính mình → người nhận cũ); Reply all = thêm To/Cc của thư gốc, bỏ chính mình.
    /// Tiêu đề "Re: ..." (giữ nguyên nếu đã có Re:/TL:), trích dẫn thư gốc, chữ ký của mẫu.</summary>
    public static WeeklyMailReplyDraft? DraftReply(string kind, string id, bool all, string myEmail)
    {
        var t = LoadMessage(kind, id);
        if (t is null) return null;
        var me = (myEmail ?? "").Trim();
        var mine = me.Length > 0 && t.From.Mailboxes.Any(a => Same(a.Address, me));
        var to = (mine ? t.To.Mailboxes : t.ReplyTo.Mailboxes.Any() ? t.ReplyTo.Mailboxes : t.From.Mailboxes).Select(a => a.Address).ToList();
        var cc = new List<string>();
        if (all)
        {
            if (!mine) to.AddRange(t.To.Mailboxes.Select(a => a.Address));
            cc.AddRange(t.Cc.Mailboxes.Select(a => a.Address));
        }
        bool NotMe(string a) => me.Length == 0 || !Same(a, me);
        to = to.Where(NotMe).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (to.Count == 0 && mine) to = t.To.Mailboxes.Select(a => a.Address).ToList();   // tự gửi cho mình
        cc = cc.Where(a => NotMe(a) && !to.Contains(a, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var meta = MetaOf(kind, id, "reply");
        var sig = WeeklyMailStore.LoadTemplates().FirstOrDefault(x => x.Id == meta.TemplateId)?.Signature ?? "";
        var subject = t.Subject ?? "";
        return new WeeklyMailReplyDraft
        {
            To = string.Join("; ", to), Cc = string.Join("; ", cc), Subject = IsReplySubject(subject) ? subject : "Re: " + subject, Signature = sig,
            QuoteHtml = QuoteHtml(t), QuoteText = QuoteHeaderText(t) + "\n\n" + (t.TextBody ?? HtmlToText(t.HtmlBody ?? "")),
        };
    }

    private static string QuoteHeaderText(MimeMessage t) =>
        $"From: {Display(t.From)}\nSent: {t.Date.LocalDateTime:dd/MM/yyyy HH:mm}\nTo: {Display(t.To)}" + (t.Cc.Count > 0 ? $"\nCc: {Display(t.Cc)}" : "") + $"\nSubject: {t.Subject}";

    /// <summary>Khối trích dẫn kiểu Outlook: đường kẻ + From/Sent/To/Cc/Subject + nội dung thư gốc (phần &lt;body&gt;).</summary>
    private static string QuoteHtml(MimeMessage t)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        string original;
        if (t.HtmlBody is { } h)
        {
            var b = Regex.Match(h, @"<body\b[^>]*>(.*)</body\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            original = InlineCid(t, b.Success ? b.Groups[1].Value : h);
            original = Regex.Replace(original, @"<script\b[^>]*>.*?</script\s*>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }
        else original = E(t.TextBody ?? "").Replace("\r\n", "\n").Replace("\n", "<br>\n");
        var head = $"<b>From:</b> {E(Display(t.From))}<br>\n<b>Sent:</b> {E(t.Date.LocalDateTime.ToString("dd/MM/yyyy HH:mm"))}<br>\n<b>To:</b> {E(Display(t.To))}<br>\n"
            + (t.Cc.Count > 0 ? $"<b>Cc:</b> {E(Display(t.Cc))}<br>\n" : "") + $"<b>Subject:</b> {E(t.Subject ?? "")}";
        return "<div style=\"border:none;border-top:solid #E1E1E1 1.0pt;padding:3.0pt 0 0 0;margin-top:12pt;font-family:Calibri,Arial,sans-serif;font-size:11pt\">" + head + "</div>\n<br>\n"
            + "<div>" + original + "</div>";
    }

    /// <summary>Nội dung HTML thư trả lời = chữ người dùng gõ (Text → &lt;br&gt;) + chữ ký (nếu chọn) + trích dẫn thư gốc.</summary>
    public static string ComposeReply(string text, string signature, string quoteHtml) =>
        WebUtility.HtmlEncode(text ?? "").Replace("\r\n", "\n").Replace("\n", "<br>\n")
        + (string.IsNullOrWhiteSpace(signature) ? "" : "<br>\n" + signature) + "<br>\n" + quoteHtml;

    /// <summary>Gắn In-Reply-To + References (chuỗi References của thư gốc + Message-ID thư gốc) vào thư trả lời.</summary>
    public static Action<MimeMessage>? ReplyHeaders(string kind, string id)
    {
        var t = LoadMessage(kind, id);
        if (t is null || string.IsNullOrEmpty(t.MessageId)) return null;
        return m =>
        {
            m.InReplyTo = t.MessageId;
            foreach (var r in t.References) m.References.Add(r);
            m.References.Add(t.MessageId);
        };
    }

    /// <summary>Danh sách thư cho giao diện: thư gốc (mỗi chuỗi 1 dòng, kèm số phản hồi / chưa xem) + mọi thư trong chuỗi.</summary>
    public static object ForUi()
    {
        var idx = Load();
        var threads = idx.Sent.Where(e => e.ThreadId == e.Id).OrderByDescending(e => e.SentAt).Select(e =>
        {
            var reps = idx.Replies.Where(r => r.ThreadId == e.Id).ToList();
            return new
            {
                e.Id, e.Subject, e.Project, e.TemplateName, e.To, e.Cc, e.Kind, SentAt = e.SentAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm"), e.CopiedToServer,
                Replies = reps.Count, Unseen = reps.Count(r => !r.Seen),
                Items = idx.Sent.Where(s => s.ThreadId == e.Id).Select(s => new { Type = "sent", s.Id, From = s.From, s.To, s.Subject, At = s.SentAt, Date = s.SentAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm"), Seen = true, ByHeader = true, s.Kind })
                    .Concat(reps.Select(r => new { Type = "reply", r.Id, From = r.From, r.To, r.Subject, At = r.Date, Date = r.Date.LocalDateTime.ToString("dd/MM/yyyy HH:mm"), r.Seen, r.ByHeader, Kind = "reply" }))
                    .OrderBy(x => x.At).ToList(),
            };
        }).ToList();
        return new { threads, lastCheck = idx.LastCheck?.LocalDateTime.ToString("dd/MM/yyyy HH:mm") ?? "" };
    }
}
