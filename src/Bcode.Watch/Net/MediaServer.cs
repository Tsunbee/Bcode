using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Bcode.Watch.Net;

/// <summary>Chủ phòng phát file trên máy (MP4 / MP3 / WebM…) cho cả phòng: HTTP/1.1 tối giản trên TcpListener (không HttpListener — bind ra ngoài
/// localhost cần quyền admin). Chỉ GET / HEAD "/m/{mã}" của file chủ phòng đã chọn (mã ngẫu nhiên, không lộ đường dẫn), có Range (206) để
/// thẻ &lt;video&gt; tua được, giữ kết nối (keep-alive) vì trình duyệt gửi nhiều yêu cầu Range liên tiếp.</summary>
public sealed class MediaServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, string> _files = new();
    public int Port { get; }

    private MediaServer(TcpListener l, int port) { _listener = l; Port = port; _ = Task.Run(AcceptLoop); }

    public static MediaServer Start(int port, bool loopbackOnly)
    {
        SocketException? last = null;
        var bind = loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        for (var p = port; p < port + 10; p++)
        {
            var l = new TcpListener(bind, p);
            l.Server.ExclusiveAddressUse = true;
            try { l.Start(); return new MediaServer(l, p); }
            catch (SocketException ex) { last = ex; }
        }
        throw new InvalidOperationException($"Không mở được cổng phát file {port}–{port + 9}: {last?.Message}");
    }

    /// <summary>Đăng ký file → mã dùng trong URL "/m/{mã}". Cùng file đăng ký lại thì trả mã cũ.</summary>
    public string Register(string path)
    {
        var full = Path.GetFullPath(path);
        var old = _files.FirstOrDefault(kv => string.Equals(kv.Value, full, StringComparison.OrdinalIgnoreCase));
        if (old.Key != null) return old.Key;
        var id = Guid.NewGuid().ToString("N")[..16];
        _files[id] = full;
        return id;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => Serve(c));
        }
    }

    private async Task Serve(TcpClient c)
    {
        using var _ = c;
        c.NoDelay = true;
        var s = c.GetStream();
        var buf = new byte[8192];
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var head = await ReadHead(s, buf);
                if (head is null) return;
                var lines = head.Split("\r\n");
                var req = lines[0].Split(' ');
                if (req.Length < 2) return;
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ln in lines.Skip(1))
                {
                    var i = ln.IndexOf(':');
                    if (i > 0) headers[ln[..i].Trim()] = ln[(i + 1)..].Trim();
                }
                var keep = !(headers.TryGetValue("Connection", out var conn) && conn.Equals("close", StringComparison.OrdinalIgnoreCase));
                var ok = await Respond(s, req[0], req[1], headers, keep);
                if (!ok || !keep) return;
            }
        }
        catch { /* trình duyệt huỷ yêu cầu khi tua / đổi bài — bình thường */ }
    }

    private static async Task<string?> ReadHead(NetworkStream s, byte[] buf)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < 16384)
        {
            var n = await s.ReadAsync(one);
            if (n == 0) return null;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb[^1] == '\n' && sb[^2] == '\r' && sb[^3] == '\n' && sb[^4] == '\r') return sb.ToString(0, sb.Length - 4);
        }
        return null;
    }

    private async Task<bool> Respond(NetworkStream s, string method, string target, Dictionary<string, string> headers, bool keep)
    {
        var path = target.Split('?')[0];
        if (method == "OPTIONS") { await Write(s, 204, "No Content", new() { ["Access-Control-Allow-Origin"] = "*", ["Access-Control-Allow-Headers"] = "Range", ["Content-Length"] = "0" }, keep); return true; }
        if ((method != "GET" && method != "HEAD") || !path.StartsWith("/m/") || !_files.TryGetValue(path[3..], out var file) || !File.Exists(file))
        {
            await Write(s, 404, "Not Found", new() { ["Content-Length"] = "0", ["Access-Control-Allow-Origin"] = "*" }, keep);
            return true;
        }
        await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
        var len = fs.Length;
        long from = 0, to = len - 1;
        var partial = false;
        if (headers.TryGetValue("Range", out var range) && range.StartsWith("bytes="))
        {
            var spec = range[6..].Split(',')[0].Trim();
            var dash = spec.IndexOf('-');
            if (dash >= 0)
            {
                var a = spec[..dash]; var b = spec[(dash + 1)..];
                if (a.Length == 0 && long.TryParse(b, out var suffix)) { from = Math.Max(0, len - suffix); }
                else
                {
                    long.TryParse(a, out from);
                    if (b.Length > 0 && long.TryParse(b, out var e)) to = Math.Min(e, len - 1);
                }
                if (from >= len || from > to)
                {
                    await Write(s, 416, "Range Not Satisfiable", new() { ["Content-Range"] = $"bytes */{len}", ["Content-Length"] = "0", ["Access-Control-Allow-Origin"] = "*" }, keep);
                    return true;
                }
                partial = true;
            }
        }
        var count = to - from + 1;
        var h = new Dictionary<string, string>
        {
            ["Content-Type"] = Mime(file),
            ["Content-Length"] = count.ToString(),
            ["Accept-Ranges"] = "bytes",
            ["Access-Control-Allow-Origin"] = "*",
            ["Cache-Control"] = "no-store",
        };
        if (partial) h["Content-Range"] = $"bytes {from}-{to}/{len}";
        await Write(s, partial ? 206 : 200, partial ? "Partial Content" : "OK", h, keep);
        if (method == "HEAD") return true;
        fs.Seek(from, SeekOrigin.Begin);
        var buf = new byte[1 << 16];
        var left = count;
        while (left > 0)
        {
            var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), _cts.Token);
            if (n <= 0) return false;
            await s.WriteAsync(buf.AsMemory(0, n), _cts.Token);
            left -= n;
        }
        return true;
    }

    private static async Task Write(NetworkStream s, int code, string text, Dictionary<string, string> headers, bool keep)
    {
        var sb = new StringBuilder($"HTTP/1.1 {code} {text}\r\n");
        foreach (var kv in headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
        sb.Append(keep ? "Connection: keep-alive\r\n" : "Connection: close\r\n").Append("\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    public static readonly string[] Extensions = { ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".mp3", ".m4a", ".aac", ".wav", ".ogg", ".oga", ".opus", ".flac" };

    private static string Mime(string file) => Path.GetExtension(file).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".m4a" or ".aac" => "audio/mp4",
        ".wav" => "audio/wav",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/opus",
        ".flac" => "audio/flac",
        _ => "application/octet-stream",
    };

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
