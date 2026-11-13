using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Bcode.Watch.Net;

/// <summary>Một mục trong danh sách chiếu. Kind: yt (Src = mã video YouTube) | url (Src = link MP4/MP3/WebM… trên mạng) | file (Src = mã file chủ phòng phát qua <see cref="MediaServer"/>).
/// Suggested = người xem đề xuất, chờ chủ phòng chiếu.</summary>
public sealed class Item
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Src { get; init; } = "";
    public string Title { get; set; } = "";
    public string By { get; init; } = "";
    public double Dur { get; set; }
    public bool Suggested { get; set; }
}

/// <summary>Máy chủ phòng chiếu: TcpListener (khung 4 byte + JSON như game) + trả lời tìm phòng UDP. Tin đầu tiên phải là "hello" trong 5 giây.
/// Trạng thái chiếu THẬT nằm ở đây (đang chiếu mục nào, chạy / dừng, giây thứ mấy) — mọi máy, kể cả chủ phòng, chỉ bám theo.
/// CHỈ CHỦ PHÒNG điều khiển (chiếu, dừng, tua, chuyển, xoá, sắp xếp); người xem được bình luận, thả cảm xúc, đổi ghế, đề xuất link.</summary>
public sealed class WatchServer : IDisposable
{
    public const int MaxViewers = 24;
    private readonly TcpListener _listener;
    private readonly RoomBeacon? _beacon;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly List<Viewer> _viewers = new();
    private readonly List<Item> _queue = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Threading.Timer _heartbeat;
    private int _nextViewer = 1, _nextItem = 1;
    private string? _cur;
    private bool _playing;
    /// <summary>Người đang chia sẻ màn hình (Id người xem, 0 = không ai). Mỗi lúc chỉ 1 người; hình / tiếng đi thẳng máy-máy bằng WebRTC,
    /// máy chủ phòng chỉ chuyển tin bắt tay ("rtc": offer / answer / ice) giữa người chia sẻ và từng người xem.</summary>
    private int _sharer;
    private double _pos, _anchor;   // vị trí (giây) tại thời điểm _anchor (giây của _clock)

    public string RoomName { get; }
    public int Port { get; }
    public string HostToken { get; } = Guid.NewGuid().ToString("N");
    public string? BeaconError { get; }
    /// <summary>Cổng HTTP phát file của chủ phòng (<see cref="MediaServer"/>) — báo cho người xem trong "welcome" để dựng link http://ip-chủ-phòng:cổng/m/mã.</summary>
    public int MediaPort { get; set; }
    public int ViewerCount { get { lock (_lock) return _viewers.Count; } }
    public event Action<string>? Log;

    private static readonly string[] Colors = { "#e8504a", "#3f8ef0", "#3cb371", "#f2b134", "#9b6cf0", "#ef7fb0", "#2ec4c4", "#f08a3c", "#8bc34a", "#c0c6d0", "#795548", "#5c6bc0" };

    private sealed class Viewer
    {
        public int Id; public string Name = ""; public string Color = ""; public int Seat; public bool Host;
        public FramedConnection Conn = null!;
        public DateTime LastChat;
    }

    private WatchServer(TcpListener listener, int port, string roomName, IPAddress bind)
    {
        _listener = listener;
        Port = port;
        RoomName = roomName;
        try { _beacon = new RoomBeacon(bind, () => new RoomInfo(Wire.Protocol, RoomName, Environment.MachineName, Port, ViewerCount, MaxViewers, NowTitle())); }
        catch (SocketException ex) { BeaconError = $"Không mở được cổng UDP {Wire.DiscoveryPort} để người khác tìm thấy phòng ({ex.SocketErrorCode}) — người xem vẫn vào được bằng cách gõ IP."; }
        _ = Task.Run(AcceptLoop);
        _heartbeat = new System.Threading.Timer(_ => { lock (_lock) if (_playing) BroadcastState(); }, null, 3000, 3000);
    }

    /// <summary>Mở phòng ở cổng <paramref name="port"/> (bận thì thử 10 cổng kế tiếp).</summary>
    public static WatchServer Start(string roomName, int port, bool loopbackOnly)
    {
        SocketException? last = null;
        var bind = loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        for (var p = port; p < port + 10; p++)
        {
            var l = new TcpListener(bind, p);
            l.Server.ExclusiveAddressUse = true;
            try { l.Start(); }
            catch (SocketException ex) { last = ex; continue; }
            return new WatchServer(l, p, roomName, bind);
        }
        throw new InvalidOperationException(last?.SocketErrorCode == SocketError.AccessDenied
            ? $"Windows không cho mở cổng {port} (AccessDenied) — có thể do chính sách tường lửa / phần mềm bảo mật."
            : $"Không mở được cổng TCP {port}–{port + 9}: {last?.Message}");
    }

    private string NowTitle()
    {
        lock (_lock) return _queue.FirstOrDefault(i => i.Id == _cur)?.Title is { Length: > 0 } t ? (_playing ? "▶ " : "⏸ ") + t : "chưa chiếu";
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            var conn = new FramedConnection(tcp);
            var greeted = false;
            conn.Received += data => { greeted = true; OnMessage(conn, data); };
            conn.Closed += _ => OnClosed(conn);
            conn.Start();
            _ = Task.Delay(5000).ContinueWith(_ => { if (!greeted) conn.Close("Không chào hỏi"); });
        }
    }

    private double Now => _clock.Elapsed.TotalSeconds;
    private double Position => _playing ? _pos + (Now - _anchor) : _pos;

    // ───────────────────────── tin từ máy khách ─────────────────────────

    private void OnMessage(FramedConnection conn, byte[] data)
    {
        JsonObject? m;
        try { m = JsonNode.Parse(data)?.AsObject(); } catch { return; }
        if (m is null) return;
        var t = (string?)m["t"];
        lock (_lock)
        {
            var v = _viewers.FirstOrDefault(x => x.Conn == conn);
            if (v is null)
            {
                if (t == "hello") Hello(conn, m);
                return;
            }
            try { Handle(v, t, m); }
            catch (Exception ex) { Log?.Invoke("Lỗi xử lý " + t + ": " + ex.Message); }
        }
    }

    private void Hello(FramedConnection conn, JsonObject m)
    {
        if ((string?)m["proto"] != Wire.Protocol) { conn.Send(new { t = "reject", text = "Khác phiên bản Xem chung — cập nhật Bcode cho giống chủ phòng." }); conn.Close("Khác phiên bản"); return; }
        if (_viewers.Count >= MaxViewers) { conn.Send(new { t = "reject", text = $"Phòng đã đủ {MaxViewers} người." }); conn.Close("Đầy"); return; }
        var name = Clean((string?)m["name"], 24);
        if (name.Length == 0) name = "Khách";
        var baseName = name;
        for (var k = 2; _viewers.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); k++) name = $"{baseName} ({k})";
        var isHost = (string?)m["token"] == HostToken && !_viewers.Any(x => x.Host);
        var used = _viewers.Select(x => x.Seat).ToHashSet();
        var seat = Enumerable.Range(0, MaxViewers).First(i => !used.Contains(i));
        var usedColors = _viewers.Select(x => x.Color).ToHashSet();
        var color = Colors.FirstOrDefault(c => !usedColors.Contains(c)) ?? Colors[_nextViewer % Colors.Length];
        var v = new Viewer { Id = _nextViewer++, Name = name, Color = color, Seat = seat, Host = isHost, Conn = conn };
        _viewers.Add(v);
        conn.Send(new { t = "welcome", id = v.Id, host = v.Host, name = v.Name, room = RoomName, maxSeats = MaxViewers, mediaPort = MediaPort });
        BroadcastViewers();
        conn.Send(StateMsg());
        if (_sharer != 0) conn.Send(ShareMsg());
        Sys($"{v.Name} đã vào phòng.");
    }

    private void OnClosed(FramedConnection conn)
    {
        lock (_lock)
        {
            var v = _viewers.FirstOrDefault(x => x.Conn == conn);
            if (v is null) return;
            _viewers.Remove(v);
            BroadcastViewers();
            Sys($"{v.Name} đã rời phòng.");
            if (_sharer == v.Id) StopShare(null, v.Name);
        }
    }

    private void Handle(Viewer v, string? t, JsonObject m)
    {
        switch (t)
        {
            case "chat":
            {
                var text = Clean((string?)m["text"], 300);
                if (text.Length == 0 || (DateTime.UtcNow - v.LastChat).TotalMilliseconds < 300) return;
                v.LastChat = DateTime.UtcNow;
                Broadcast(new { t = "chat", from = v.Id, name = v.Name, color = v.Color, text, at = Position });
                return;
            }
            case "react":
            {
                var e = Clean((string?)m["e"], 8);
                if (e.Length == 0) return;
                Broadcast(new { t = "react", from = v.Id, e });
                return;
            }
            case "seat":
            {
                var s = (int?)m["i"] ?? -1;
                if (s < 0 || s >= MaxViewers || _viewers.Any(x => x.Seat == s)) return;
                v.Seat = s;
                BroadcastViewers();
                return;
            }
            case "add":
            {
                var item = Parse((string?)m["url"], v.Name, out var err);
                if (item is null) { v.Conn.Send(new { t = "error", text = err }); return; }
                if (_queue.Count >= 200) { v.Conn.Send(new { t = "error", text = "Danh sách đã đủ 200 mục." }); return; }
                item.Suggested = !v.Host;
                _queue.Add(item);
                var announce = v.Host ? null : $"{v.Name} đề xuất: ";
                if (item.Kind == "yt") _ = FetchYoutubeTitleAsync(item, announce);   // báo đề xuất sau khi có tên video thật
                else if (announce != null) Sys(announce + item.Title);
                if (v.Host && _cur is null && _sharer == 0) Select(item.Id);
                else BroadcastState();
                return;
            }
            case "meta":
            {
                var it = _queue.FirstOrDefault(i => i.Id == (string?)m["id"]);
                if (it is null) return;
                var changed = false;
                var dur = (double?)m["dur"] ?? 0;
                if (dur > 0 && double.IsFinite(dur) && Math.Abs(it.Dur - dur) > 0.5) { it.Dur = dur; changed = true; }
                var title = Clean((string?)m["title"], 160);
                if (title.Length > 0 && it.Kind == "yt" && it.Title.StartsWith("YouTube ")) { it.Title = title; changed = true; }
                if (changed) BroadcastState();
                return;
            }
            case "share.start":
            {
                // Ai trong phòng cũng chia sẻ được (như Discord), nhưng mỗi lúc 1 người. Đang chiếu thì dừng danh sách lại để khỏi đè tiếng.
                if (_sharer == v.Id) return;
                if (_sharer != 0) { v.Conn.Send(new { t = "error", text = $"{SharerName()} đang chia sẻ màn hình — chờ họ dừng (chủ phòng tắt được)." }); return; }
                _sharer = v.Id;
                if (_playing) { _pos = Position; _playing = false; BroadcastState(); }
                Broadcast(ShareMsg());
                Sys($"{v.Name} đang chia sẻ màn hình.");
                return;
            }
            case "share.stop":
                if (_sharer == 0) return;
                if (_sharer != v.Id && !v.Host) { v.Conn.Send(new { t = "error", text = "Chỉ người đang chia sẻ hoặc chủ phòng tắt được." }); return; }
                StopShare(_sharer == v.Id ? null : v.Name);
                return;
            case "rtc":
            {
                // Chuyển tin bắt tay WebRTC — chỉ giữa người đang chia sẻ và người xem (không cho dùng làm kênh nhắn riêng).
                var to = (int?)m["to"] ?? 0;
                if (_sharer == 0 || (v.Id != _sharer && to != _sharer)) return;
                var target = _viewers.FirstOrDefault(x => x.Id == to);
                if (target is null || target == v || m["data"] is not JsonObject data) return;
                target.Conn.Send(new { t = "rtc", from = v.Id, data });
                return;
            }
        }

        if (!v.Host) { v.Conn.Send(new { t = "error", text = "Chỉ chủ phòng được điều khiển." }); return; }
        if (_sharer != 0 && t is "play" or "select" or "next" or "prev")
        {
            v.Conn.Send(new { t = "error", text = $"{SharerName()} đang chia sẻ màn hình — dừng chia sẻ trước rồi chiếu." });
            return;
        }
        switch (t)
        {
            case "addfile":
            {
                var item = new Item { Id = "i" + _nextItem++, Kind = "file", Src = Clean((string?)m["media"], 40), Title = Clean((string?)m["title"], 160), By = v.Name };
                if (item.Src.Length == 0) return;
                _queue.Add(item);
                if (_cur is null && _sharer == 0) Select(item.Id); else BroadcastState();
                break;
            }
            case "play":
                if (_cur is null) { var first = _queue.FirstOrDefault(); if (first != null) Select(first.Id); break; }
                if (!_playing)
                {
                    var it = _queue.FirstOrDefault(i => i.Id == _cur);
                    if (it is { Dur: > 0 } && _pos >= it.Dur - 0.5) _pos = 0;   // hết bài rồi bấm chiếu = chiếu lại từ đầu
                    _anchor = Now; _playing = true;
                }
                BroadcastState();
                break;
            case "pause":
                if (_playing) { _pos = Position; _playing = false; }
                BroadcastState();
                break;
            case "seek":
            {
                var p = (double?)m["pos"] ?? 0;
                if (!double.IsFinite(p)) return;
                _pos = Math.Max(0, p); _anchor = Now;
                BroadcastState();
                break;
            }
            case "select":
                if (_queue.Any(i => i.Id == (string?)m["id"])) Select((string)m["id"]!);
                break;
            case "next":
            case "prev":
            {
                var idx = _queue.FindIndex(i => i.Id == _cur);
                var to = t == "next" ? idx + 1 : idx - 1;
                if (t == "prev" && Position > 5) { _pos = 0; _anchor = Now; BroadcastState(); break; }   // đang giữa bài: ⏮ = về đầu bài
                if (to >= 0 && to < _queue.Count) Select(_queue[to].Id);
                break;
            }
            case "ended":
                // Chỉ tin trình phát của chủ phòng báo hết bài → sang bài kế (đề xuất chưa duyệt thì bỏ qua); hết danh sách thì dừng ở cuối.
                if ((string?)m["id"] != _cur) return;
                {
                    var idx = _queue.FindIndex(i => i.Id == _cur);
                    var next = _queue.Skip(idx + 1).FirstOrDefault(i => !i.Suggested);
                    if (next != null) Select(next.Id);
                    else { _pos = Position; _playing = false; BroadcastState(); }
                }
                break;
            case "remove":
            {
                var id = (string?)m["id"];
                var idx = _queue.FindIndex(i => i.Id == id);
                if (idx < 0) return;
                _queue.RemoveAt(idx);
                if (id == _cur) { _cur = null; _playing = false; _pos = 0; }
                BroadcastState();
                break;
            }
            case "move":
            {
                var idx = _queue.FindIndex(i => i.Id == (string?)m["id"]);
                var to = idx + ((int?)m["dir"] ?? 0);
                if (idx < 0 || to < 0 || to >= _queue.Count || to == idx) return;
                (_queue[idx], _queue[to]) = (_queue[to], _queue[idx]);
                BroadcastState();
                break;
            }
            case "approve":
            {
                var it = _queue.FirstOrDefault(i => i.Id == (string?)m["id"]);
                if (it is null) return;
                it.Suggested = false;
                BroadcastState();
                break;
            }
            case "clearchat":
                Broadcast(new { t = "clearchat" });
                break;
        }
    }

    private void Select(string id)
    {
        _cur = id; _pos = 0; _anchor = Now; _playing = true;
        var it = _queue.First(i => i.Id == id);
        it.Suggested = false;
        BroadcastState();
        Sys($"Đang chiếu: {it.Title}");
    }

    // ───────────────────────── link ─────────────────────────

    private static readonly Regex YtRx = new(@"(?:youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|embed/|live/|v/)|youtu\.be/|music\.youtube\.com/watch\?(?:.*&)?v=)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase);

    /// <summary>Link → mục chiếu: YouTube (watch / youtu.be / shorts / embed / live) hoặc link http(s) tới file media. Đường dẫn trên máy KHÔNG nhận ở đây
    /// (chủ phòng thêm file bằng nút 📂 — máy khác không đọc được ổ của chủ phòng).</summary>
    private Item? Parse(string? raw, string by, out string err)
    {
        err = "";
        var url = (raw ?? "").Trim();
        if (url.Length == 0) { err = "Chưa dán link."; return null; }
        if (url.Length > 2000) { err = "Link quá dài."; return null; }
        var yt = YtRx.Match(url);
        if (yt.Success) return new Item { Id = "i" + _nextItem++, Kind = "yt", Src = yt.Groups[1].Value, Title = "YouTube " + yt.Groups[1].Value, By = by };
        if (Regex.IsMatch(url, @"^[A-Za-z0-9_-]{11}$")) return new Item { Id = "i" + _nextItem++, Kind = "yt", Src = url, Title = "YouTube " + url, By = by };
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
        {
            err = url.Contains(":\\") || url.StartsWith("\\\\")
                ? "Đường dẫn file trên máy: chủ phòng bấm 📂 File trên máy để chiếu (máy khác không đọc được ổ của bạn)."
                : "Link không hợp lệ — dán link YouTube hoặc link http(s) tới file MP4 / MP3 / WebM.";
            return null;
        }
        var name = Uri.UnescapeDataString(u.Segments.LastOrDefault() ?? "").Trim('/');
        return new Item { Id = "i" + _nextItem++, Kind = "url", Src = u.AbsoluteUri, Title = name.Length > 0 ? name : u.Host, By = by };
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>Tên video YouTube qua oEmbed (công khai, không cần khoá) — máy chủ phòng không vào được Internet thì giữ "YouTube &lt;mã&gt;",
    /// trình phát của chủ phòng sẽ gửi tên thật ("meta") khi tải xong.</summary>
    private async Task FetchYoutubeTitleAsync(Item item, string? announce)
    {
        try
        {
            var json = await Http.GetStringAsync("https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=" + item.Src));
            var title = Clean((string?)JsonNode.Parse(json)?["title"], 160);
            if (title.Length > 0)
                lock (_lock)
                {
                    if (item.Title.StartsWith("YouTube ")) { item.Title = title; BroadcastState(); }
                }
        }
        catch { /* không có mạng / video riêng tư: giữ "YouTube <mã>" */ }
        if (announce != null) lock (_lock) Sys(announce + item.Title);
    }

    // ───────────────────────── gửi ─────────────────────────

    private object StateMsg() => new
    {
        t = "state", cur = _cur, playing = _playing, pos = Math.Round(Position, 3),
        queue = _queue.Select(i => new { i.Id, i.Kind, i.Src, i.Title, i.By, dur = Math.Round(i.Dur, 1), sug = i.Suggested }).ToList(),
    };

    private void BroadcastState() => Broadcast(StateMsg());

    private string SharerName() => _viewers.FirstOrDefault(x => x.Id == _sharer)?.Name ?? "Người khác";

    private object ShareMsg() => new { t = "share", id = _sharer, name = _sharer == 0 ? "" : SharerName() };

    /// <param name="by">Chủ phòng tắt hộ (null = người chia sẻ tự dừng / rời phòng).</param>
    /// <param name="name">Tên người chia sẻ khi họ đã bị gỡ khỏi danh sách (rời phòng).</param>
    private void StopShare(string? by, string? name = null)
    {
        name ??= SharerName();
        _sharer = 0;
        Broadcast(ShareMsg());
        Sys(by is null ? $"{name} đã dừng chia sẻ màn hình." : $"{by} đã tắt chia sẻ màn hình của {name}.");
    }

    private void BroadcastViewers() =>
        Broadcast(new { t = "viewers", list = _viewers.OrderBy(x => x.Seat).Select(x => new { x.Id, x.Name, x.Color, x.Seat, x.Host }).ToList() });

    private void Sys(string text) => Broadcast(new { t = "chat", sys = true, text });

    private void Broadcast(object msg)
    {
        var json = Wire.Encode(msg);
        foreach (var v in _viewers) v.Conn.SendRaw(json);
    }

    private static string Clean(string? s, int max)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        s = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return s.Length > max ? s[..max] : s;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _heartbeat.Dispose();
        try { _listener.Stop(); } catch { }
        _beacon?.Dispose();
        List<Viewer> left;
        lock (_lock)
        {
            Broadcast(new { t = "reject", text = "Chủ phòng đã kết thúc buổi chiếu." });
            left = _viewers.ToList();
        }
        // chờ tin trên kịp gửi đi rồi mới cắt kết nối
        _ = Task.Delay(400).ContinueWith(_ => { foreach (var v in left) v.Conn.Close("Chủ phòng đã đóng phòng"); });
    }
}

/// <summary>Máy khách: 1 kết nối TCP tới chủ phòng (chủ phòng cũng tự nối vào 127.0.0.1 — 1 đường code cho mọi người).</summary>
public sealed class WatchClient : IDisposable
{
    private FramedConnection? _conn;
    public event Action<byte[]>? Message;
    public event Action<string>? Closed;

    public static async Task<WatchClient> ConnectAsync(string host, int port, string name, string? hostToken, int timeoutMs = 4000)
    {
        var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await tcp.ConnectAsync(host, port, cts.Token); }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            throw new TimeoutException($"Không nối được tới {host}:{port} sau {timeoutMs / 1000} giây — thường do tường lửa máy chủ phòng chặn: nhờ chủ phòng bấm 🛡 Mở tường lửa trong phòng (hoặc đổi máy khác làm chủ phòng). Cũng kiểm tra lại IP / cùng mạng LAN.");
        }
        catch (SocketException ex)
        {
            tcp.Dispose();
            throw new InvalidOperationException(ex.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => $"{host}:{port} từ chối kết nối — phòng đã đóng hoặc sai cổng.",
                SocketError.HostNotFound or SocketError.NoData => $"Không tìm thấy máy \"{host}\".",
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => $"Không tới được {host} (khác mạng / VPN?).",
                _ => $"Không nối được tới {host}:{port} ({ex.SocketErrorCode}).",
            });
        }
        var c = new WatchClient();
        var conn = new FramedConnection(tcp);
        conn.Received += d => c.Message?.Invoke(d);
        conn.Closed += r => c.Closed?.Invoke(r);
        c._conn = conn;
        conn.Start();
        conn.Send(new { t = "hello", proto = Wire.Protocol, name, token = hostToken });
        return c;
    }

    public void SendRaw(byte[] json) => _conn?.SendRaw(json);
    public void Dispose() => _conn?.Close("Đã rời phòng");
}
