using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>1 lỗi đã ghi log. <see cref="Detail"/> = ex.ToString() (thông điệp + stack trace + inner) đã che mật khẩu.</summary>
public sealed record ErrorEntry(DateTime Time, string Version, string Project, string Feature, string Source, string Message, string Detail);

/// <summary>
/// Log lỗi tập trung cho MỌI chức năng của Bcode — ghi <c>&lt;thư mục dữ liệu&gt;\Bcode\error.log</c> (tôn trọng BCODE_DATA_DIR qua BcodePaths;
/// quá 2 MB đổi thành error.old.log) và giữ "lỗi gần nhất" trong bộ nhớ cho phím Gửi lỗi (F1, xem UI/ErrorReporter).
///
/// Lỗi được bắt ở 3 tầng, không phải sửa từng chỗ catch của từng tính năng:
/// 1. Lỗi chưa xử lý: Application.ThreadException (vẫn hiện hộp lỗi chuẩn của WinForms như trước), AppDomain.UnhandledException, TaskScheduler.UnobservedTaskException.
/// 2. Lỗi ném ra từ handler của trang web (WebBarHost / WebFrameHost nhận tin từ trang) — ghi rồi ném lại, hành vi giữ nguyên.
/// 3. Lỗi đã bắt rồi BÁO cho người dùng: mọi exception vừa ném (FirstChanceException) nằm tạm trong 1 vòng đệm ~20 giây; khi có chữ hiện ra
///    (MessageBox, lệnh gửi xuống trang web, thanh trạng thái — <see cref="NoticeDisplayed"/>) chứa thông điệp của 1 exception trong vòng đệm thì
///    exception đó được ghi kèm stack trace. Exception bắt im lặng (thử đọc file, dò cấu hình...) không hiện ra nên không vào log.
/// </summary>
public static class ErrorLog
{
    public static string Folder => Path.Combine(BcodePaths.AppData, "Bcode");
    public static string FilePath => Path.Combine(Folder, "error.log");
    private const long MaxBytes = 2 * 1024 * 1024;

    /// <summary>Lỗi được coi là "vừa xảy ra" (F1 gửi lỗi bắt phím) trong khoảng này sau khi ghi, nếu chưa gửi / chưa bỏ qua.</summary>
    public static readonly TimeSpan PendingWindow = TimeSpan.FromMinutes(15);

    private static readonly object Gate = new();
    private static readonly ConditionalWeakTable<Exception, object> Logged = new();
    private static readonly object Mark = new();

    public static ErrorEntry? Last { get; private set; }
    private static bool _lastHandled;

    /// <summary>Có lỗi vừa ghi (trong <see cref="PendingWindow"/>) mà người dùng chưa gửi / chưa bấm Không.</summary>
    public static bool HasPending => Last is { } e && !_lastHandled && DateTime.Now - e.Time < PendingWindow;

    /// <summary>Đánh dấu lỗi gần nhất đã gửi / người dùng bỏ qua — phím F1 trả về chức năng cũ.</summary>
    public static void MarkHandled() => _lastHandled = true;

    /// <summary>Báo khi vừa ghi 1 lỗi (gọi trên luồng đã ghi — có thể là luồng nền).</summary>
    public static event Action<ErrorEntry>? Recorded;

    /// <summary>(dự án, chức năng/tab) đang dùng — MainForm đăng ký. Hàm phải an toàn khi gọi từ luồng nền.</summary>
    public static Func<(string Project, string Feature)>? ContextProvider;

    /// <summary>Ghi 1 dòng thông tin (không phải lỗi) vào log — vd dấu thuỷ ấn lúc khởi động.</summary>
    public static void Note(string text)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            lock (Gate) File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [info]  {text}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch { /* không ghi được thì bỏ qua */ }
    }

    // ------------------------------------------------------------------ cài đặt (Program.Main)

    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Application.ThreadException += OnThreadException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Record(ex, e.IsTerminating ? "Lỗi chưa xử lý (chương trình phải đóng)" : "Lỗi chưa xử lý (luồng nền)");
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Không SetObserved — giữ nguyên hành vi (net8 không đóng chương trình vì lỗi này).
            var ex = e.Exception.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception;
            Record(ex, "Lỗi trong tác vụ nền (Task)");
        };
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
    }

    /// <summary>Thay hộp lỗi mặc định của WinForms bằng: ghi log → vẫn hiện đúng hộp ThreadExceptionDialog như trước (Tiếp tục / Thoát).</summary>
    private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
    {
        Record(e.Exception, "Lỗi chưa xử lý (giao diện)");
        try
        {
            using var dlg = new ThreadExceptionDialog(e.Exception);
            if (dlg.ShowDialog() == DialogResult.Abort) Application.Exit();
        }
        catch { /* không hiện được hộp lỗi — bỏ qua như trước khi có log */ }
    }

    // ------------------------------------------------------------------ vòng đệm exception vừa ném

    private static readonly (Exception Ex, long Tick)[] Ring = new (Exception, long)[32];
    private static int _ringNext;
    private static long _lastThrowTick;
    private const long RingMs = 20_000;
    [ThreadStatic] private static bool _inside;

    private static void OnFirstChance(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        if (_inside) return;
        var ex = e.Exception;
        if (ex is OperationCanceledException or ThreadAbortException or ThreadInterruptedException) return;
        var now = Environment.TickCount64;
        lock (Ring)
        {
            Ring[_ringNext] = (ex, now);
            _ringNext = (_ringNext + 1) % Ring.Length;
        }
        _lastThrowTick = now;
    }

    /// <summary>
    /// Có chữ vừa hiện cho người dùng (MessageBox, script gửi xuống trang web, thanh trạng thái): nếu chứa thông điệp của 1 exception mới ném (≤ 20 s)
    /// thì ghi exception đó (kèm stack). Trả true nếu đã khớp 1 exception (kể cả exception đã ghi trước đó). Rất rẻ khi không có exception gần đây.
    /// </summary>
    public static bool NoticeDisplayed(string? text, string source)
    {
        if (string.IsNullOrEmpty(text) || _inside) return false;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastThrowTick) > RingMs) return false;
        if (text.Length > 40_000) return false;   // dữ liệu lớn (bảng kết quả...) — thông báo lỗi không nằm ở đây
        List<Exception> recent;
        lock (Ring)
        {
            recent = new List<Exception>(Ring.Length);
            for (var i = 1; i <= Ring.Length; i++)
            {
                var (ex, tick) = Ring[(_ringNext - i + Ring.Length) % Ring.Length];
                if (ex is null || now - tick > RingMs) continue;
                recent.Add(ex);
            }
        }
        // Khung Message của SQL Query (sqlmessages.html): lỗi SQL của chính script người dùng chạy (sai cú pháp, sai tên bảng...) — không phải lỗi
        // Bcode, không ghi (log ngập + phím F1 bị chiếm sau mỗi lần chạy lỗi). Lỗi khác hiện ở đó vẫn ghi.
        var userSqlPane = source.Contains("sqlmessages.html", StringComparison.OrdinalIgnoreCase);
        foreach (var ex in recent)
        {
            if (userSqlPane && ex.GetType().Name == "SqlException") continue;
            var msg = ex.Message?.Trim() ?? "";
            if (msg.Length < 8) continue;
            var key = msg.Length > 120 ? msg[..120] : msg;
            if (!text.Contains(key, StringComparison.Ordinal) && !text.Contains(JsonInner(key), StringComparison.Ordinal)) continue;
            Record(ex, source);
            return true;
        }
        return false;
    }

    private static string JsonInner(string s) { var j = JsonSerializer.Serialize(s); return j.Length >= 2 ? j[1..^1] : j; }

    // ------------------------------------------------------------------ ghi log

    /// <summary>Ghi 1 exception (mỗi exception chỉ ghi 1 lần). <paramref name="source"/> = ở đâu / hiện ra thế nào ("Trang sqleditor.html", "Hộp thông báo: ...").</summary>
    public static ErrorEntry? Record(Exception ex, string source)
    {
        if (ex is null || ex is OperationCanceledException) return null;
        lock (Gate)
        {
            if (Logged.TryGetValue(ex, out _)) return null;
            Logged.Add(ex, Mark);
        }
        return Write(Mask(ex.Message ?? ex.GetType().Name), Mask(ex.ToString()), source);
    }

    /// <summary>Ghi 1 lỗi chỉ có chữ (vd hộp thông báo biểu tượng lỗi mà không tìm được exception tương ứng).</summary>
    public static ErrorEntry? RecordText(string message, string source) => string.IsNullOrWhiteSpace(message) ? null : Write(Mask(message.Trim()), Mask(message.Trim()), source);

    private static ErrorEntry? Write(string message, string detail, string source)
    {
        if (_inside) return null;
        _inside = true;
        try
        {
            var (project, feature) = SafeContext();
            var entry = new ErrorEntry(DateTime.Now, Version, project, feature, source, message, detail);
            lock (Gate)
            {
                Last = entry;
                _lastHandled = false;
                try
                {
                    Directory.CreateDirectory(Folder);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        var old = Path.Combine(Folder, "error.old.log");
                        File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, Format(entry), new UTF8Encoding(false));
                }
                catch { /* không ghi được log thì thôi — không được làm hỏng chức năng đang chạy */ }
            }
            try { Recorded?.Invoke(entry); } catch { /* người nghe lỗi */ }
            return entry;
        }
        catch { return null; }
        finally { _inside = false; }
    }

    private static (string, string) SafeContext()
    {
        try { return ContextProvider?.Invoke() ?? ("", ""); } catch { return ("", ""); }
    }

    private static string Format(ErrorEntry e) =>
        $"==== {e.Time:yyyy-MM-dd HH:mm:ss} | Bcode {e.Version} | Dự án: {Or(e.Project)} | Chức năng: {Or(e.Feature)} | Nguồn: {e.Source}{Environment.NewLine}" +
        e.Detail + Environment.NewLine + Environment.NewLine;

    private static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "(không rõ)" : s;

    private static string? _version;
    public static string Version => _version ??= ReadVersion();
    private static string ReadVersion()
    {
        try
        {
            var a = typeof(ErrorLog).Assembly;
            var v = a.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? a.GetName().Version?.ToString() ?? "";
            var plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
        catch { return ""; }
    }

    /// <summary>Nhãn phiên bản hiện ở góc phải thanh trên: "1.9-beta" → "Beta v1.9", "2.0" → "v2.0". Số lấy từ &lt;Version&gt; trong Bcode.App.csproj.</summary>
    public static string VersionLabel
    {
        get
        {
            var v = Version;
            if (v.Length == 0) return "";
            var dash = v.IndexOf('-');
            if (dash <= 0) return "v" + v;
            var tag = v[(dash + 1)..];
            return (tag.Length > 0 ? char.ToUpperInvariant(tag[0]) + tag[1..] + " " : "") + "v" + v[..dash];
        }
    }

    // ------------------------------------------------------------------ che dữ liệu nhạy cảm

    private static readonly Regex SecretRx = new(@"(?i)\b(password|pwd|passwd|mật khẩu|apikey|api[_-]?key|token)\s*[=:]\s*(""[^""]*""|'[^']*'|[^;,\s'""]+)", RegexOptions.Compiled);
    private static readonly Regex DpapiRx = new(@"dpapi:[A-Za-z0-9+/=]+", RegexOptions.Compiled);

    /// <summary>Che mật khẩu / key trong chuỗi kết nối, URL... (Password=*** ; dpapi:***). Tên server / database giữ nguyên để biết lỗi ở đâu.</summary>
    public static string Mask(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        s = SecretRx.Replace(s, m => m.Groups[1].Value + "=***");
        return DpapiRx.Replace(s, "dpapi:***");
    }

    // ------------------------------------------------------------------ gửi lỗi đi

    /// <summary>Tóm tắt để người dùng xem trước khi gửi.</summary>
    public static string Preview(ErrorEntry e, int maxDetail = 600)
    {
        var d = e.Detail.Length > maxDetail ? e.Detail[..maxDetail] + " …" : e.Detail;
        return $"Thời gian: {e.Time:dd/MM/yyyy HH:mm:ss}\nMáy: {Environment.MachineName} ({Environment.UserName})\nDự án: {Or(e.Project)}\nChức năng: {Or(e.Feature)}\nNguồn: {e.Source}\n\n{d}";
    }

    private const int MaxDetailSend = 8000;   // 1 ô Google Sheet tối đa 50.000 ký tự

    private static Dictionary<string, string> Fields(ErrorEntry e) => new()
    {
        ["time"] = e.Time.ToString("yyyy-MM-dd HH:mm:ss"),
        ["version"] = e.Version,
        ["machine"] = Environment.MachineName,
        ["user"] = Environment.UserName,
        ["project"] = e.Project,
        ["feature"] = e.Feature,
        ["source"] = e.Source,
        ["message"] = e.Message,
        ["detail"] = e.Detail.Length > MaxDetailSend ? e.Detail[..MaxDetailSend] + " …" : e.Detail,
    };

    /// <summary>1 dòng TSV (cùng thứ tự cột với mã Apps Script) để dán thẳng vào Google Sheet. Ô có xuống dòng / tab / ngoặc kép được bọc "...".</summary>
    public static string ToTsvRow(ErrorEntry e) => string.Join("\t", Fields(e).Values.Select(v =>
        v.IndexOfAny(new[] { '\t', '\n', '\r', '"' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v));

    /// <summary>JSON gửi đi = 9 trường của <see cref="Fields"/> + app ("Bcode") + title / content soạn sẵn để máy nhận kiểu forum (TK2 Help) đăng thành bài.
    /// Máy nhận dùng trường nào tuỳ ý; trả 2xx + "ok" (hoặc JSON bất kỳ, không phải trang HTML) = thành công.</summary>
    private static Dictionary<string, string> Payload(ErrorEntry e)
    {
        var f = Fields(e);
        var msg = e.Message.Replace('\r', ' ').Replace('\n', ' ');
        f["app"] = "Bcode";
        f["title"] = "[Bcode lỗi] " + (msg.Length > 150 ? msg[..150] + "…" : msg);
        f["content"] = $"Thời gian: {f["time"]}\nPhiên bản: Bcode {f["version"]}\nMáy: {f["machine"]} ({f["user"]})\nDự án: {Or(e.Project)}\nChức năng: {Or(e.Feature)}\nNguồn: {e.Source}\n\n{f["detail"]}";
        return f;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25) };

    /// <summary>POST JSON tới URL nhận lỗi (Google Apps Script web app <c>…/exec</c> — xem <see cref="AppsScriptCode"/>). Trả (thành công, mô tả).</summary>
    public static async Task<(bool Ok, string Info)> SendAsync(ErrorEntry e, string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return (false, "URL nhận lỗi không hợp lệ: " + url);
        if (uri.Host.Equals("docs.google.com", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.Contains("/spreadsheets/", StringComparison.OrdinalIgnoreCase))
            return (false, "Đây là link mở Google Sheet, không nhận ghi trực tiếp — cần URL web app Apps Script (…/exec).");
        try
        {
            var json = JsonSerializer.Serialize(Payload(e));
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await Http.PostAsync(uri, content).ConfigureAwait(false);
            var body = (await resp.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
            if (!resp.IsSuccessStatusCode) return (false, $"Máy nhận trả lỗi HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
            // Apps Script chưa mở quyền trả 200 + trang đăng nhập Google → coi là thất bại. Máy nhận khác (forum) trả HTML 2xx vẫn tính là nhận được.
            var google = uri.Host.Contains("google", StringComparison.OrdinalIgnoreCase) || (resp.RequestMessage?.RequestUri?.Host.Contains("google", StringComparison.OrdinalIgnoreCase) ?? false);
            if (google && (body.Contains("<html", StringComparison.OrdinalIgnoreCase) || body.Contains("<!doctype", StringComparison.OrdinalIgnoreCase)))
                return (false, "Máy nhận trả về trang HTML (thường do web app Apps Script chưa cho \"Bất kỳ ai\" truy cập, hoặc URL không phải …/exec).");
            if (body.StartsWith('<')) return (true, "");
            return (true, body.Length > 200 ? body[..200] : body);
        }
        catch (TaskCanceledException) { return (false, "Quá thời gian chờ (25 giây) — kiểm tra mạng / URL."); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Mã Google Apps Script gắn vào Google Sheet nhận lỗi (Tiện ích mở rộng → Apps Script → dán → Triển khai → Ứng dụng web,
    /// Thực thi: Tôi, Ai có quyền truy cập: Bất kỳ ai → chép URL …/exec vào Template → Đường dẫn → Báo lỗi).</summary>
    public const string AppsScriptCode = """
// Bcode — nhận lỗi gửi từ phím F1 (Gửi lỗi gần nhất) và ghi thêm 1 dòng vào sheet đầu tiên.
function doPost(e) {
  var d = JSON.parse(e.postData.contents);
  var sh = SpreadsheetApp.getActiveSpreadsheet().getSheets()[0];
  if (sh.getLastRow() === 0)
    sh.appendRow(['Thời gian', 'Phiên bản', 'Máy', 'Người dùng', 'Dự án', 'Chức năng', 'Nguồn', 'Lỗi', 'Chi tiết']);
  sh.appendRow([d.time, d.version, d.machine, d.user, d.project, d.feature, d.source, d.message, d.detail]);
  return ContentService.createTextOutput('ok');
}
""";
}
