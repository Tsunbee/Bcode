using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bcode.Chat.Net;

/// <summary>
/// Một "nút" Chat LAN — mỗi máy chạy đúng 1 cái, KHÔNG có máy chủ:
/// • UDP <see cref="Wire.DiscoveryPort"/>: ~3 s broadcast gói <see cref="Presence"/> 1 lần (tên, trạng thái, cổng TCP); nghe gói của máy khác → danh sách bạn.
///   Bạn im quá 12 s (và không có kết nối TCP) → offline. Thoát thì gửi Bye để máy khác thấy offline ngay.
/// • TCP <see cref="Wire.DefaultPort"/>(+0..9): tin đi thẳng máy-máy. Mỗi cặp 1 kết nối, ai nhắn trước thì người đó nối; tin đầu là "hello".
/// • Mọi tin (chữ, BUZZ, file, nhóm) vào outbox trước, có "ack" mới bỏ → người nhận offline thì tin nằm chờ (lưu ra đĩa), họ online lại là tự gửi.
///   Bên nhận bỏ tin trùng theo Mid (gửi lại sau khi mất kết nối).
/// • File: tin "file" chỉ mang tên + cỡ + mã; người nhận bấm Nhận thì mở kết nối TCP MỚI tới máy gửi ("get" + Fid + Token) và tải thẳng.
/// Sự kiện bắn từ luồng mạng — giao diện tự chuyển về luồng UI.
/// Giới hạn (nói rõ cho người dùng): tin đi dạng chữ thường trong LAN (không mã hoá), không có tài khoản — ai trong LAN cũng đặt được tên bất kỳ;
/// mã Id chỉ giúp phân biệt máy, không chống giả mạo.
/// </summary>
public sealed class ChatNode : IDisposable
{
    private const int MaxText = 4000;
    private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan DeadConnAfter = TimeSpan.FromSeconds(50);

    private readonly ChatStore _store;
    private readonly object _lock = new();
    private readonly Dictionary<string, FramedConnection> _conns = new();
    private readonly HashSet<string> _connecting = new();
    private readonly HashSet<FramedConnection> _allConns = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _discoveryPort;
    private readonly int _wantPort;
    private readonly bool _loopbackOnly;
    private TcpListener? _listener;
    private UdpClient? _udp;
    private System.Threading.Timer? _timer;
    private int _tick;
    private bool _disposed;

    public ChatPrefs Prefs => _store.Prefs;
    public ChatStore Store => _store;
    public string MyId => _store.Prefs.Id;
    public int Port { get; private set; }
    public string? UdpError { get; private set; }

    /// <summary>Danh sách bạn đổi (có người online / offline / đổi tên, trạng thái).</summary>
    public event Action? BuddiesChanged;
    /// <summary>Bạn vừa online (để phát tiếng "gõ cửa").</summary>
    public event Action<Buddy>? BuddyOnline;
    /// <summary>Tin của mình vừa gửi đi (conv, tin) — kể cả thông báo tự sinh (tạo / mời / đổi tên phòng). Đã ghi lịch sử.</summary>
    public event Action<string, ChatMessage>? Sent;
    /// <summary>Tin mới tới (conv, tin). Đã ghi lịch sử.</summary>
    public event Action<string, ChatMessage>? MessageReceived;
    /// <summary>Tin của mình đã tới máy người nhận (conv, mid, id người nhận).</summary>
    public event Action<string, string, string>? Delivered;
    /// <summary>Ai đó đang gõ (conv, id người gõ).</summary>
    public event Action<string, string>? Typing;
    /// <summary>Tiến độ tải file (fid, đã nhận, tổng, trạng thái: run | done | err, đường dẫn hoặc lỗi).</summary>
    public event Action<string, long, long, string, string>? FileProgress;
    /// <summary>Có người đã tải xong file mình gửi (fid, id người tải).</summary>
    public event Action<string, string>? FileServed;
    /// <summary>Phòng chat nhóm đổi (thành viên / tên) hoặc mới có.</summary>
    public event Action<GroupMeta>? GroupChanged;
    /// <summary>Có máy khác trong LAN đang online bằng đúng Bhoo ID của mình (IP của máy đó).</summary>
    public event Action<string>? NickConflict;
    public event Action<string>? Log;

    /// <param name="discoveryPort">Cổng UDP tìm bạn (thử nhiều bản trên 1 máy thì đổi).</param>
    /// <param name="port">Cổng TCP muốn dùng (bận thì +1..+9).</param>
    /// <param name="loopbackOnly">Chỉ nghe 127.0.0.1 (thử tự động — không bật hộp hỏi tường lửa, không broadcast ra LAN).</param>
    public ChatNode(ChatStore store, int discoveryPort = Wire.DiscoveryPort, int port = Wire.DefaultPort, bool loopbackOnly = false)
    {
        _store = store;
        _discoveryPort = discoveryPort;
        _wantPort = port;
        _loopbackOnly = loopbackOnly;
    }

    public void Start()
    {
        var bind = _loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        for (var p = _wantPort; p < _wantPort + 10; p++)
        {
            try
            {
                var l = new TcpListener(bind, p);
                l.Start();
                _listener = l; Port = p;
                break;
            }
            catch (SocketException) { }
        }
        if (_listener is null) throw new InvalidOperationException($"Không mở được cổng TCP {_wantPort}–{_wantPort + 9} (đang có chương trình khác dùng?).");
        _ = Task.Run(AcceptLoop);

        try
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.EnableBroadcast = true;
            _udp.Client.Bind(new IPEndPoint(bind, _discoveryPort));
            _ = Task.Run(UdpLoop);
        }
        catch (Exception ex)
        {
            UdpError = $"Không nghe được cổng tìm bạn UDP {_discoveryPort}: {ex.Message}. Vẫn nhắn được qua IP gõ tay.";
            Log?.Invoke(UdpError);
        }
        _timer = new System.Threading.Timer(_ => OnTimer(), null, 200, 1000);
    }

    // ───────────────────────── Trạng thái của mình ─────────────────────────

    public void SetMe(string? name = null, string? status = null, string? text = null, string? color = null)
    {
        var p = _store.Prefs;
        if (name is not null) p.Name = Clip(name.Trim(), 32) is { Length: > 0 } n ? n : p.Name;
        var wasInvisible = p.Status == Net.Status.Invisible;
        if (status is not null) p.Status = Net.Status.Norm(status);
        if (!wasInvisible && p.Status == Net.Status.Invisible) Announce(bye: true);   // vừa chuyển sang Ẩn: báo "offline" ngay
        if (text is not null) p.Text = Clip(text.Trim(), 100);
        if (color is not null && System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$")) p.Color = color;
        _store.SavePrefs();
        Announce();
        // máy đã nối TCP nhận cập nhật ngay (kể cả khi UDP bị chặn)
        var hello = Hello();
        foreach (var c in Conns()) c.Send(hello);
    }

    private Presence MyPresence(bool q = false, bool bye = false) =>
        new(Wire.Protocol, MyId, Prefs.Name, Prefs.Status, Prefs.Text, Prefs.Color, Port, q, bye, "1", Prefs.AvatarHash, bye ? null : RoomAds());

    private object Hello() => new { t = "hello", id = MyId, name = Prefs.Name, status = Prefs.Status, text = Prefs.Text, color = Prefs.Color, port = Port, av = Prefs.AvatarHash };

    // ───────────────────────── UDP: tìm bạn ─────────────────────────

    private IEnumerable<IPEndPoint> PresenceTargets()
    {
        if (!_loopbackOnly)
        {
            yield return new IPEndPoint(IPAddress.Broadcast, _discoveryPort);
            foreach (var b in SubnetBroadcasts()) yield return new IPEndPoint(b, _discoveryPort);
        }
    }

    private IEnumerable<IPEndPoint> ManualTargets()
    {
        foreach (var s in Prefs.ManualPeers.ToList())
            if (ParseEndpoint(s, _discoveryPort) is { } ep) yield return ep;
    }

    /// <summary>"ip" hoặc "ip:cổng UDP" → điểm đến; sai thì null.</summary>
    public static IPEndPoint? ParseEndpoint(string s, int defPort)
    {
        s = (s ?? "").Trim();
        var port = defPort;
        var colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out var p) && p is > 0 and < 65536) { port = p; s = s[..colon]; }
        return IPAddress.TryParse(s, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(ip, port) : null;
    }

    private static IEnumerable<IPAddress> SubnetBroadcasts()
    {
        var list = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add(new IPAddress(b));
                }
            }
        }
        catch { /* card mạng đang đổi: lần sau */ }
        return list.Distinct();
    }

    /// <summary>IPv4 LAN của máy (hiện cho người khác gõ tay khi broadcast không qua được).</summary>
    public static List<string> LocalIPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                if (nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                            list.Add(ua.Address.ToString());
        }
        catch { }
        return list.Distinct().ToList();
    }

    private static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip) || LocalIPv4().Contains(ip.ToString());
    }

    /// <summary>Hỏi cả LAN "ai đang online" (mọi máy trả lời ngay) — lúc đăng nhập để biết sớm nick có bị trùng không.</summary>
    public void Probe()
    {
        if (_udp is null) return;
        var ask = Wire.Encode(MyPresence(q: true));
        foreach (var ep in PresenceTargets().Concat(ManualTargets()))
            try { _udp.Send(ask, ask.Length, ep); } catch { }
    }

    /// <summary>Báo "đang online" ngay (broadcast + IP gõ tay có hỏi lại).</summary>
    public void Announce(bool bye = false)
    {
        if (_udp is null) return;
        if (!bye && Prefs.Status == Net.Status.Invisible) return;   // ẩn: không báo online (người khác thấy offline)
        var data = Wire.Encode(MyPresence(bye: bye));
        var ask = Wire.Encode(MyPresence(q: !bye, bye: bye));
        var sent = 0;
        foreach (var ep in PresenceTargets())
            try { _udp.Send(data, data.Length, ep); sent++; } catch { }
        foreach (var ep in ManualTargets())
            try { _udp.Send(ask, ask.Length, ep); sent++; } catch { }
        if (!bye && !_loopbackOnly && sent == 0) UdpError = "Không gửi được gói báo online (mạng / tường lửa).";
    }

    public void AddManualPeer(string ipPort)
    {
        if (ParseEndpoint(ipPort, _discoveryPort) is null) throw new ArgumentException("IP không hợp lệ: " + ipPort);
        var s = ipPort.Trim();
        if (!Prefs.ManualPeers.Contains(s)) { Prefs.ManualPeers.Add(s); _store.SavePrefs(); }
        Announce();
    }

    public void RemoveManualPeer(string ipPort)
    {
        if (Prefs.ManualPeers.Remove(ipPort.Trim())) _store.SavePrefs();
    }

    private async Task UdpLoop()
    {
        var udp = _udp!;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(_cts.Token);
                Presence? p;
                try { p = JsonSerializer.Deserialize<Presence>(r.Buffer, Wire.Json); } catch { continue; }
                if (p is null || p.Proto != Wire.Protocol || string.IsNullOrEmpty(p.Id)) continue;
                if (p.Id == MyId)
                {
                    // gói của chính mình (broadcast vòng lại) thì bỏ; cùng ID mà khác cổng / khác máy = trùng nick
                    if (p.Port == Port && IsLocal(r.RemoteEndPoint.Address)) continue;
                    if (p.Bye) continue;
                    NickConflict?.Invoke(r.RemoteEndPoint.Address.ToString());
                    // máy kia đang hỏi (lúc đăng nhập): trả lời để nó biết nick đã có người dùng
                    if (p.Q) try { await udp.SendAsync(Wire.Encode(MyPresence()), r.RemoteEndPoint, _cts.Token); } catch { }
                    continue;
                }
                if (p.Q && Prefs.Status == Net.Status.Invisible) { Seen(p.Id, p.Name, p.Status, p.Text, p.Color, r.RemoteEndPoint.Address, p.Port, p.Av, p.Rooms); continue; }
                if (p.Bye) { SetOffline(p.Id); continue; }
                Seen(p.Id, p.Name, p.Status, p.Text, p.Color, r.RemoteEndPoint.Address, p.Port, p.Av, p.Rooms);
                if (p.Q)
                {
                    var reply = Wire.Encode(MyPresence());
                    try { await udp.SendAsync(reply, r.RemoteEndPoint, _cts.Token); } catch { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* gói lạ / ICMP "cổng đóng" trên Windows: bỏ qua */ }
        }
    }

    // ───────────────────────── Danh sách bạn ─────────────────────────

    public List<Buddy> Buddies() { lock (_lock) return Prefs.Buddies.Select(Clone).ToList(); }

    private static Buddy Clone(Buddy b) => new()
    {
        Id = b.Id, Name = b.Name, Color = b.Color, Status = b.Status, Text = b.Text, Group = b.Group,
        Ip = b.Ip, Port = b.Port, LastSeen = b.LastSeen, Hidden = b.Hidden, AvHash = b.AvHash, AvWant = b.AvWant, Rooms = b.Rooms,
    };

    public Buddy? FindBuddy(string id) { lock (_lock) return Prefs.Buddies.FirstOrDefault(b => b.Id == id) is { } b ? Clone(b) : null; }

    /// <summary>Nghe thấy bạn (UDP hoặc "hello" TCP): cập nhật + nếu vừa online thì gửi các tin đang chờ.</summary>
    private void Seen(string id, string? name, string? status, string? text, string? color, IPAddress ip, int port, string? av = null, List<RoomAd>? rooms = null)
    {
        var wantAvatar = false;
        var roomsChanged = false;
        bool cameOnline, changed;
        Buddy snapshot;
        lock (_lock)
        {
            var b = Prefs.Buddies.FirstOrDefault(x => x.Id == id);
            var isNew = b is null;
            if (b is null) { b = new Buddy { Id = id }; Prefs.Buddies.Add(b); }
            var st = Net.Status.Norm(status);
            if (st == Net.Status.Invisible) st = Net.Status.Offline;   // bạn đang Ẩn: vẫn nhận tin qua kết nối họ mở, nhưng hiện offline
            cameOnline = !b.Online && st != Net.Status.Offline;
            var nm = Clip((name ?? "").Trim(), 32);
            var tx = Clip((text ?? "").Trim(), 100);
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var ipS = ip.ToString();
            // cùng máy: gói có thể tới từ 127.0.0.1 lẫn IP LAN — giữ IP LAN
            if (IPAddress.IsLoopback(ip) && b.Ip.Length > 0 && !b.Ip.StartsWith("127.") && b.Port == port) ipS = b.Ip;
            changed = isNew || cameOnline || b.Status != st || (nm.Length > 0 && b.Name != nm) || b.Text != tx || (color is not null && b.Color != color) || b.Ip != ipS || b.Port != port;
            b.Status = st;
            if (nm.Length > 0) b.Name = nm;
            b.Text = tx;
            if (color is not null && System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$")) b.Color = color;
            b.Ip = ipS;
            if (port > 0) b.Port = port;
            if (av is not null && System.Text.RegularExpressions.Regex.IsMatch(av, "^[0-9a-f]{0,16}$"))
            {
                b.AvWant = av;
                if (av.Length == 0 && b.AvHash.Length > 0) { b.AvHash = ""; changed = true; try { File.Delete(AvatarPath(id)); } catch { } }
                wantAvatar = av.Length > 0 && av != b.AvHash;
            }
            if (rooms is not null)
            {
                var clean = rooms.Where(r => r is not null && r.G.Length is > 0 and <= 32).Take(8)
                    .Select(r => new RoomAd(r.G, Clip(r.T ?? "", 60), Clip(r.Topic ?? "", 100))).ToList();
                if (!clean.SequenceEqual(b.Rooms)) { b.Rooms = clean; roomsChanged = true; }
            }
            b.LastSeen = DateTime.UtcNow;
            snapshot = Clone(b);
        }
        if (changed) { _store.SavePrefs(); BuddiesChanged?.Invoke(); }
        if (roomsChanged) RoomsChanged?.Invoke();
        if (cameOnline) { BuddyOnline?.Invoke(snapshot); Flush(id); }
        if (wantAvatar) AskAvatar(id);
    }

    private void SetOffline(string id)
    {
        FramedConnection? c;
        bool hadRooms;
        lock (_lock)
        {
            var b = Prefs.Buddies.FirstOrDefault(x => x.Id == id);
            if (b is null || !b.Online) return;
            b.Status = Net.Status.Offline;
            hadRooms = b.Rooms.Count > 0;
            b.Rooms = new();
            _conns.Remove(id, out c);
        }
        c?.Close("Bạn đã offline");
        BuddiesChanged?.Invoke();
        if (hadRooms) RoomsChanged?.Invoke();
    }

    public void SetBuddyGroup(string id, string group)
    {
        lock (_lock) { if (Prefs.Buddies.FirstOrDefault(b => b.Id == id) is { } b) b.Group = Clip(group.Trim(), 40) is { Length: > 0 } g ? g : "Bạn bè"; }
        _store.SavePrefs(); BuddiesChanged?.Invoke();
    }

    public bool IsIgnored(string id) { lock (_lock) return Prefs.Ignored.Contains(id); }

    /// <summary>Chặn / bỏ chặn 1 nick. Chặn không cắt kết nối: tin của họ vẫn được "ack" (máy họ không xếp hàng gửi lại mãi) nhưng bị bỏ.</summary>
    public void SetIgnored(string id, bool ignored)
    {
        id = Nick.IdOf(id);
        if (id.Length == 0 || id == MyId) return;
        lock (_lock)
        {
            Prefs.Ignored.Remove(id);
            if (ignored) Prefs.Ignored.Add(id);
        }
        _store.SavePrefs(); BuddiesChanged?.Invoke();
    }

    public void HideBuddy(string id, bool hidden)
    {
        lock (_lock) { if (Prefs.Buddies.FirstOrDefault(b => b.Id == id) is { } b) b.Hidden = hidden; }
        _store.SavePrefs(); BuddiesChanged?.Invoke();
    }

    private void OnTimer()
    {
        if (_disposed) return;
        _tick++;
        if (_tick % 3 == 1) Announce();
        var now = DateTime.UtcNow;
        var gone = new List<string>();
        var dead = new List<FramedConnection>();
        lock (_lock)
        {
            foreach (var b in Prefs.Buddies)
            {
                if (!b.Online) continue;
                var hasConn = _conns.ContainsKey(b.Id);
                if (now - b.LastSeen > (hasConn ? DeadConnAfter : OfflineAfter)) gone.Add(b.Id);
            }
            if (_tick % 15 == 0) dead.AddRange(_allConns);   // ping giữ kết nối + phát hiện kết nối chết (ghi lỗi → Closed)
        }
        foreach (var id in gone) SetOffline(id);
        foreach (var c in dead) c.Send(new { t = "p" });
        if (_tick % 5 == 0) FlushAll();   // thử lại tin đang chờ cho người đang online
    }

    // ───────────────────────── TCP: kết nối ─────────────────────────

    private List<FramedConnection> Conns() { lock (_lock) return _conns.Values.ToList(); }

    private async Task AcceptLoop()
    {
        var l = _listener!;
        while (!_cts.IsCancellationRequested)
        {
            TcpClient tcp;
            try { tcp = await l.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { continue; }
            _ = Task.Run(() => HandleIncoming(tcp));
        }
    }

    private async Task HandleIncoming(TcpClient tcp)
    {
        try
        {
            tcp.NoDelay = true;
            var stream = tcp.GetStream();
            using var first = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            first.CancelAfter(5000);
            var f = await Wire.ReadFrameAsync(stream, first.Token);
            if (f is null) { tcp.Close(); return; }
            var m = JsonNode.Parse(f)?.AsObject();
            switch ((string?)m?["t"])
            {
                case "get":
                    await ServeFile(tcp, stream, m!);
                    return;
                case "hello":
                    var conn = new FramedConnection(tcp);
                    Attach(conn);
                    conn.Start();
                    conn.Send(Hello());
                    OnHello(conn, m!);
                    return;
                default:
                    tcp.Close();
                    return;
            }
        }
        catch { try { tcp.Close(); } catch { } }
    }

    private void Attach(FramedConnection conn)
    {
        lock (_lock) _allConns.Add(conn);
        conn.Received += OnFrame;
        conn.Closed += (c, _) =>
        {
            lock (_lock)
            {
                _allConns.Remove(c);
                if (c.PeerId.Length > 0 && _conns.TryGetValue(c.PeerId, out var cur) && cur == c) _conns.Remove(c.PeerId);
            }
        };
    }

    private void OnHello(FramedConnection conn, JsonObject m)
    {
        var id = (string?)m["id"] ?? "";
        if (id.Length == 0 || id == MyId) { conn.Close("Chào sai"); return; }
        if (conn.PeerId.Length > 0 && conn.PeerId != id) { conn.Close("Nhầm người"); return; }   // nối tới IP cũ mà gặp máy khác
        conn.PeerId = id;
        lock (_lock) _conns[id] = conn;
        var port = (int?)m["port"] ?? 0;
        Seen(id, (string?)m["name"], (string?)m["status"], (string?)m["text"], (string?)m["color"], conn.RemoteIp ?? IPAddress.Loopback, port, (string?)m["av"] ?? "", null);
        Flush(id);
        AskAvatar(id);
    }

    /// <summary>Nối tới bạn nếu chưa có kết nối (không chặn; xong thì tự gửi tin đang chờ).</summary>
    private void EnsureConnection(string id)
    {
        string ip; int port;
        lock (_lock)
        {
            if (_conns.TryGetValue(id, out var c) && !c.IsClosed) return;
            if (_connecting.Contains(id)) return;
            var b = Prefs.Buddies.FirstOrDefault(x => x.Id == id);
            if (b is null || !b.Online || b.Port <= 0 || b.Ip.Length == 0) return;
            ip = b.Ip; port = b.Port;
            _connecting.Add(id);
        }
        _ = Task.Run(async () =>
        {
            var tcp = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                using var to = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                to.CancelAfter(4000);
                await tcp.ConnectAsync(IPAddress.Parse(ip), port, to.Token);
                var conn = new FramedConnection(tcp) { PeerId = id };
                Attach(conn);
                lock (_lock) _conns[id] = conn;
                conn.Start();
                conn.Send(Hello());
                Flush(id);
                AskAvatar(id);
            }
            catch (Exception ex)
            {
                try { tcp.Dispose(); } catch { }
                Log?.Invoke($"Không nối được tới {ip}:{port} — {ex.Message}");
            }
            finally { lock (_lock) _connecting.Remove(id); }
        });
    }

    private void OnFrame(FramedConnection conn, byte[] f)
    {
        JsonObject? m;
        try { m = JsonNode.Parse(f)?.AsObject(); } catch { return; }
        if (m is null) return;
        var t = (string?)m["t"];
        if (t == "hello") { OnHello(conn, m); return; }
        if (conn.PeerId.Length == 0) return;   // chưa chào thì không nhận gì
        lock (_lock) { if (Prefs.Buddies.FirstOrDefault(b => b.Id == conn.PeerId) is { } b) b.LastSeen = DateTime.UtcNow; }
        switch (t)
        {
            case "p": break;
            case "av?":
                if (Prefs.AvatarHash.Length > 0 && File.Exists(_store.MyAvatarPath))
                    try { conn.Send(new { t = "av", h = Prefs.AvatarHash, d = Convert.ToBase64String(File.ReadAllBytes(_store.MyAvatarPath)) }); } catch { }
                break;
            case "av":
                OnAvatar(conn.PeerId, (string?)m["h"] ?? "", (string?)m["d"] ?? "");
                break;
            case "msg":
                ChatMessage? msg;
                try { msg = m["m"]?.Deserialize<ChatMessage>(Wire.Json); } catch { return; }
                if (msg is null || msg.Mid.Length == 0 || msg.Mid.Length > 64) return;
                if (msg.From != conn.PeerId) return;   // chỉ nhận tin của chính người ở đầu kết nối
                conn.Send(new { t = "ack", mid = msg.Mid });
                if (IsIgnored(msg.From) && msg.Kind is not ("sys" or "leave")) return;   // đã chặn: bỏ (vẫn nhận thông báo vào / rời phòng để danh sách phòng đúng)
                if (_store.MarkSeen(msg.Mid)) Deliver(msg);
                break;
            case "ack":
                var mid = (string?)m["mid"] ?? "";
                OutItem? done;
                lock (_lock)
                {
                    done = _store.Outbox.FirstOrDefault(o => o.To == conn.PeerId && o.Msg.Mid == mid);
                    if (done is not null) _store.Outbox.Remove(done);
                }
                if (done is not null)
                {
                    _store.SaveOutbox();
                    Delivered?.Invoke(ConvOf(done.Msg, mine: true, to: conn.PeerId), mid, conn.PeerId);
                }
                break;
            case "typing":
                if (IsIgnored(conn.PeerId)) break;
                var gid = (string?)m["gid"];
                Typing?.Invoke(gid is { Length: > 0 } ? "g:" + gid : "u:" + conn.PeerId, conn.PeerId);
                break;
        }
    }

    // ───────────────────────── Ảnh đại diện ─────────────────────────

    public const int MaxAvatarBytes = 150 * 1024;
    public event Action? RoomsChanged;

    public string AvatarPath(string id) => Path.Combine(_store.AvatarDir, id + ".jpg");

    /// <summary>Phòng công cộng mình đang ngồi, gửi kèm Presence (danh sách rỗng = không ngồi phòng nào — để máy khác biết mình đã rời).</summary>
    private List<RoomAd> RoomAds() { lock (_lock) return _rooms.Values.ToList(); }

    // ───────────────────────── Phòng công cộng ─────────────────────────
    // Không có máy chủ giữ phòng: ai đang ngồi phòng nào thì gói Presence của họ mang tên phòng đó.
    // Danh sách phòng = gộp từ mọi người đang online; thành viên = ai đang báo ngồi phòng đó. Tin gửi thẳng tới từng thành viên,
    // không lưu lịch sử, không chờ người offline (như phòng chat công cộng ngày xưa: vào thì thấy từ lúc vào).

    private readonly Dictionary<string, RoomAd> _rooms = new();
    private readonly List<(string To, ChatMessage Msg, DateTime At)> _roomOut = new();

    /// <summary>Mã phòng từ tên: chữ thường không dấu, số, "-" (≤ 24 ký tự). "Cà phê chiều" → "ca-phe-chieu".</summary>
    public static string RoomKey(string title)
    {
        var norm = (title ?? "").Trim().ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in norm)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var k = sb.ToString().Trim('-');
        return k.Length > 24 ? k[..24].TrimEnd('-') : k;
    }

    public static bool ValidRoomKey(string? g) => g is { Length: > 0 and <= 24 } && System.Text.RegularExpressions.Regex.IsMatch(g, "^[a-z0-9-]+$");

    public bool InRoom(string g) { lock (_lock) return _rooms.ContainsKey(g); }

    /// <summary>Vào phòng (chưa ai mở thì coi như mở phòng mới). Trả mã phòng.</summary>
    public string JoinRoom(string title, string? topic = null)
    {
        var g = RoomKey(title);
        if (!ValidRoomKey(g)) throw new ArgumentException("Tên phòng cần có chữ cái hoặc số.");
        lock (_lock)
        {
            // phòng đã có người: dùng tên + chủ đề họ đang báo, để mọi người thấy giống nhau
            var known = Prefs.Buddies.Where(b => b.Online).SelectMany(b => b.Rooms).FirstOrDefault(r => r.G == g);
            _rooms[g] = known ?? new RoomAd(g, Clip(title.Trim(), 40), Clip((topic ?? "").Trim(), 100));
        }
        Announce();
        RoomsChanged?.Invoke();
        return g;
    }

    public void LeaveRoom(string g)
    {
        bool had;
        lock (_lock) had = _rooms.Remove(g);
        if (!had) return;
        Announce();
        RoomsChanged?.Invoke();
    }

    /// <summary>Mọi phòng đang có người (kể cả phòng mình ngồi), đông trước.</summary>
    public List<(RoomAd Room, int Count, bool Joined)> PublicRooms()
    {
        lock (_lock)
        {
            var all = new Dictionary<string, (RoomAd R, int N)>();
            foreach (var b in Prefs.Buddies.Where(b => b.Online && !Prefs.Ignored.Contains(b.Id)))
                foreach (var r in b.Rooms)
                    all[r.G] = all.TryGetValue(r.G, out var x) ? (x.R, x.N + 1) : (r, 1);
            foreach (var r in _rooms.Values)
                all[r.G] = all.TryGetValue(r.G, out var x) ? (r, x.N + 1) : (r, 1);
            return all.Values.Select(x => (x.R, x.N, _rooms.ContainsKey(x.R.G))).OrderByDescending(x => x.Item2).ThenBy(x => x.R.T).ToList();
        }
    }

    /// <summary>Ai đang ngồi phòng <paramref name="g"/> (mình đứng đầu nếu đang ngồi).</summary>
    public List<Member> RoomMembers(string g)
    {
        lock (_lock)
        {
            var list = new List<Member>();
            if (_rooms.ContainsKey(g)) list.Add(new Member(MyId, Prefs.Name));
            list.AddRange(Prefs.Buddies.Where(b => b.Online && !Prefs.Ignored.Contains(b.Id) && b.Rooms.Any(r => r.G == g)).OrderBy(b => b.Name).Select(b => new Member(b.Id, b.Name)));
            return list;
        }
    }

    public RoomAd? FindRoom(string g)
    {
        lock (_lock)
            return _rooms.TryGetValue(g, out var r) ? r : Prefs.Buddies.Where(b => b.Online).SelectMany(b => b.Rooms).FirstOrDefault(x => x.G == g);
    }

    private ChatMessage SendRoom(string g, string kind, string text, string? style, string? fg, int? size)
    {
        RoomAd room;
        lock (_lock)
        {
            if (!_rooms.TryGetValue(g, out var r)) throw new InvalidOperationException("Bạn đã rời phòng này.");
            room = r;
        }
        var msg = new ChatMessage
        {
            Mid = Guid.NewGuid().ToString("N"), From = MyId, FromName = Prefs.Name, Kind = kind, Text = Clip(text ?? "", MaxText),
            Style = style, Fg = fg, Size = size is >= 10 and <= 22 ? size : null, At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Room = room,
        };
        _store.MarkSeen(msg.Mid);
        var to = RoomMembers(g).Select(x => x.Id).Where(x => x != MyId).ToList();
        lock (_lock) foreach (var id in to) _roomOut.Add((id, msg, DateTime.UtcNow));
        Sent?.Invoke("r:" + g, msg);
        foreach (var id in to) Flush(id);
        return msg;
    }

    /// <summary>Gửi tin phòng công cộng đang chờ kết nối (bỏ nếu quá 30 giây — người đó chắc đã đi).</summary>
    private void FlushRoom(string id, FramedConnection c)
    {
        lock (_lock)
        {
            _roomOut.RemoveAll(x => DateTime.UtcNow - x.At > TimeSpan.FromSeconds(30));
            foreach (var x in _roomOut.Where(x => x.To == id).ToList()) { c.Send(new { t = "msg", m = x.Msg }); _roomOut.Remove(x); }
        }
    }

    /// <summary>Đặt ảnh đại diện (JPEG / PNG đã thu nhỏ ở giao diện; null = bỏ ảnh). Bạn bè thấy mã ảnh mới trong gói Presence / hello rồi tự hỏi tải.</summary>
    public void SetAvatar(byte[]? image)
    {
        if (image is { Length: > 0 })
        {
            if (image.Length > MaxAvatarBytes || !LooksLikeImage(image)) throw new ArgumentException("Ảnh đại diện phải là JPEG / PNG dưới 150 KB.");
            File.WriteAllBytes(_store.MyAvatarPath, image);
            Prefs.AvatarHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image))[..12].ToLowerInvariant();
        }
        else
        {
            try { File.Delete(_store.MyAvatarPath); } catch { }
            Prefs.AvatarHash = "";
        }
        _store.SavePrefs();
        Announce();
        var hello = Hello();
        foreach (var c in Conns()) c.Send(hello);
    }

    private static bool LooksLikeImage(byte[] b) =>
        b.Length > 8 && ((b[0] == 0xFF && b[1] == 0xD8) || (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47));

    /// <summary>Bạn báo mã ảnh khác ảnh đã có → xin ảnh (nối nếu chưa có kết nối; nối xong tự hỏi lại).</summary>
    private void AskAvatar(string id)
    {
        FramedConnection? c;
        lock (_lock)
        {
            var b = Prefs.Buddies.FirstOrDefault(x => x.Id == id);
            if (b is null || b.AvWant.Length == 0 || b.AvWant == b.AvHash) return;
            _conns.TryGetValue(id, out c);
        }
        if (c is { IsClosed: false }) c.Send(new { t = "av?" });
        else EnsureConnection(id);
    }

    private void OnAvatar(string id, string hash, string b64)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9a-f]{1,16}$") || b64.Length > MaxAvatarBytes * 4 / 3 + 8) return;
        byte[] data;
        try { data = Convert.FromBase64String(b64); } catch { return; }
        if (!LooksLikeImage(data)) return;
        try
        {
            var tmp = AvatarPath(id) + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, AvatarPath(id), true);
        }
        catch { return; }
        lock (_lock) { if (Prefs.Buddies.FirstOrDefault(x => x.Id == id) is { } b) b.AvHash = hash; }
        _store.SavePrefs();
        BuddiesChanged?.Invoke();
    }

    // ───────────────────────── Tin nhắn ─────────────────────────

    /// <summary>Cuộc chat của tin: nhóm → "g:gid"; 1-1 → "u:id người kia" (tin mình gửi thì người kia = người nhận).</summary>
    private string ConvOf(ChatMessage m, bool mine, string to) =>
        m.Room is { } r ? "r:" + r.G : m.Group is { } g ? "g:" + g.Gid : "u:" + (mine ? to : m.From);

    private void Deliver(ChatMessage msg)
    {
        msg.Text = Clip(msg.Text, MaxText);
        msg.FromName = Clip(msg.FromName, 32);
        var conv = ConvOf(msg, mine: false, to: "");
        if (msg.Room is { } room)
        {
            // phòng công cộng: chỉ nhận khi mình đang ngồi phòng đó; không lưu lịch sử
            if (msg.Kind != "text" || !ValidRoomKey(room.G) || !InRoom(room.G)) return;
            msg.Group = null; msg.File = null;
            MessageReceived?.Invoke(conv, msg);
            return;
        }
        if (msg.Group is { } g && g.Gid.Length > 0)
        {
            GroupMeta? changed = null;
            lock (_lock)
            {
                var local = Prefs.Groups.FirstOrDefault(x => x.Gid == g.Gid);
                if (msg.Kind == "leave")
                {
                    if (local is not null) { local.Members.RemoveAll(x => x.Id == msg.From); changed = local; }
                }
                else if (local is null)
                {
                    local = new GroupMeta { Gid = g.Gid, Title = Clip(g.Title, 60), Members = g.Members.Take(50).ToList() };
                    Prefs.Groups.Add(local); changed = local;
                }
                else if (msg.Kind == "sys")   // tạo / mời thêm / đổi tên: lấy danh sách mới nhất của người gửi
                {
                    local.Title = Clip(g.Title, 60);
                    local.Members = g.Members.Take(50).ToList();
                    changed = local;
                }
            }
            if (changed is not null) { _store.SavePrefs(); GroupChanged?.Invoke(changed); }
        }
        _store.AppendHistory(conv, msg);
        MessageReceived?.Invoke(conv, msg);
    }

    /// <summary>Người nhận của cuộc chat (trừ mình).</summary>
    public List<string> Recipients(string conv)
    {
        if (conv.StartsWith("u:")) return new() { conv[2..] };
        if (conv.StartsWith("g:"))
            lock (_lock)
                return Prefs.Groups.FirstOrDefault(g => g.Gid == conv[2..])?.Members.Select(x => x.Id).Where(x => x != MyId).Distinct().ToList() ?? new();
        return new();
    }

    public GroupMeta? FindGroup(string gid) { lock (_lock) return Prefs.Groups.FirstOrDefault(g => g.Gid == gid) is { } g ? CloneGroup(g) : null; }
    public List<GroupMeta> Groups() { lock (_lock) return Prefs.Groups.Select(CloneGroup).ToList(); }
    private static GroupMeta CloneGroup(GroupMeta g) => new() { Gid = g.Gid, Title = g.Title, Members = g.Members.ToList() };

    /// <summary>Gửi 1 tin vào cuộc chat. Ghi lịch sử của mình, vào outbox từng người nhận rồi gửi ngay ai đang online.</summary>
    public ChatMessage Send(string conv, string kind, string text, string? style = null, string? fg = null, int? size = null, FileMeta? file = null, GroupMeta? groupOverride = null)
    {
        var msg = new ChatMessage
        {
            Mid = Guid.NewGuid().ToString("N"), From = MyId, FromName = Prefs.Name, Kind = kind, Text = Clip(text ?? "", MaxText),
            Style = style, Fg = fg, Size = size is >= 10 and <= 22 ? size : null, At = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), File = file,
        };
        List<string> to;
        if (conv.StartsWith("g:"))
        {
            msg.Group = groupOverride ?? FindGroup(conv[2..]);
            if (msg.Group is null) throw new InvalidOperationException("Không còn phòng chat nhóm này.");
            to = msg.Group.Members.Select(x => x.Id).Where(x => x != MyId).Distinct().ToList();
        }
        else if (conv.StartsWith("r:")) return SendRoom(conv[2..], kind == "text" ? kind : "text", text ?? "", style, fg, size);
        else to = Recipients(conv);
        _store.MarkSeen(msg.Mid);
        _store.AppendHistory(conv, msg);
        lock (_lock)
            foreach (var id in to)
                _store.Outbox.Add(new OutItem { To = id, Msg = msg, Queued = DateTime.UtcNow });
        _store.SaveOutbox();
        Sent?.Invoke(conv, msg);
        foreach (var id in to) Flush(id);
        return msg;
    }

    /// <summary>Người nhận còn chờ tin này (chưa ack) — để giao diện hiện "⏳ chờ X online".</summary>
    public List<string> Pending(string mid) { lock (_lock) return _store.Outbox.Where(o => o.Msg.Mid == mid).Select(o => o.To).ToList(); }

    public int PendingCount(string conv)
    {
        lock (_lock) return _store.Outbox.Count(o => ConvOf(o.Msg, mine: true, to: o.To) == conv);
    }

    /// <summary>Gửi các tin đang chờ cho <paramref name="id"/> nếu có kết nối; chưa có mà họ online thì nối.</summary>
    private void Flush(string id)
    {
        FramedConnection? c;
        List<OutItem> items;
        lock (_lock)
        {
            items = _store.Outbox.Where(o => o.To == id).ToList();
            var room = _roomOut.Any(x => x.To == id);
            if (items.Count == 0 && !room) return;
            _conns.TryGetValue(id, out c);
            if (room && c is { IsClosed: false }) FlushRoom(id, c);
            if (c is { IsClosed: false })
                foreach (var o in items.Where(o => o.SentOn != c)) { o.SentOn = c; c.Send(new { t = "msg", m = o.Msg }); }
        }
        if (c is null or { IsClosed: true }) EnsureConnection(id);
    }

    private void FlushAll()
    {
        List<string> ids;
        lock (_lock) ids = _store.Outbox.Select(o => o.To).Distinct().ToList();
        foreach (var id in ids) Flush(id);
    }

    public void SendTyping(string conv)
    {
        var gid = conv.StartsWith("g:") ? conv[2..] : null;
        foreach (var id in Recipients(conv))
        {
            FramedConnection? c;
            lock (_lock) _conns.TryGetValue(id, out c);
            if (c is null) { EnsureConnection(id); continue; }   // lần gõ sau sẽ có kết nối
            c.Send(new { t = "typing", gid });
        }
    }

    // ───────────────────────── Chat nhóm ─────────────────────────

    public GroupMeta CreateGroup(string title, IEnumerable<string> memberIds)
    {
        var g = new GroupMeta { Gid = Guid.NewGuid().ToString("N")[..12], Title = Clip(title.Trim(), 60) is { Length: > 0 } t ? t : "Phòng chat của " + Prefs.Name };
        g.Members.Add(new Member(MyId, Prefs.Name));
        foreach (var id in memberIds.Distinct())
            if (id != MyId && FindBuddy(id) is { } b) g.Members.Add(new Member(b.Id, b.Name));
        lock (_lock) Prefs.Groups.Add(g);
        _store.SavePrefs();
        Send("g:" + g.Gid, "sys", $"{Prefs.Name} đã mở phòng chat \"{g.Title}\" với {string.Join(", ", g.Members.Where(m => m.Id != MyId).Select(m => m.Name))}.");
        GroupChanged?.Invoke(CloneGroup(g));
        return CloneGroup(g);
    }

    public void InviteToGroup(string gid, IEnumerable<string> memberIds)
    {
        GroupMeta g;
        var added = new List<string>();
        lock (_lock)
        {
            var local = Prefs.Groups.FirstOrDefault(x => x.Gid == gid) ?? throw new InvalidOperationException("Không còn phòng chat nhóm này.");
            foreach (var id in memberIds.Distinct())
            {
                if (local.Members.Any(m => m.Id == id)) continue;
                var b = Prefs.Buddies.FirstOrDefault(x => x.Id == id);
                if (b is null) continue;
                local.Members.Add(new Member(b.Id, b.Name)); added.Add(b.Name);
            }
            g = CloneGroup(local);
        }
        if (added.Count == 0) return;
        _store.SavePrefs();
        Send("g:" + gid, "sys", $"{Prefs.Name} đã mời {string.Join(", ", added)} vào phòng.");
        GroupChanged?.Invoke(g);
    }

    public void RenameGroup(string gid, string title)
    {
        GroupMeta g;
        lock (_lock)
        {
            var local = Prefs.Groups.FirstOrDefault(x => x.Gid == gid) ?? throw new InvalidOperationException("Không còn phòng chat nhóm này.");
            local.Title = Clip(title.Trim(), 60) is { Length: > 0 } t ? t : local.Title;
            g = CloneGroup(local);
        }
        _store.SavePrefs();
        Send("g:" + gid, "sys", $"{Prefs.Name} đã đổi tên phòng thành \"{g.Title}\".");
        GroupChanged?.Invoke(g);
    }

    public void LeaveGroup(string gid)
    {
        var g = FindGroup(gid);
        if (g is null) return;
        Send("g:" + gid, "leave", $"{Prefs.Name} đã rời phòng.", groupOverride: g);
        lock (_lock) Prefs.Groups.RemoveAll(x => x.Gid == gid);
        _store.SavePrefs();
    }

    // ───────────────────────── File ─────────────────────────

    /// <summary>Gửi file: tin "file" (tên + cỡ + mã) đi như tin thường — người nhận offline thì nhận tin khi online lại, bấm Nhận để tải
    /// (máy mình phải đang mở Chat LAN lúc đó).</summary>
    public ChatMessage OfferFile(string conv, string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) throw new FileNotFoundException("Không thấy file", path);
        var offer = new FileOffer { Fid = Guid.NewGuid().ToString("N"), Path = fi.FullName, Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), Created = DateTime.UtcNow };
        lock (_lock) _store.Offers.Add(offer);
        _store.SaveOffers();
        return Send(conv, "file", fi.Name, file: new FileMeta { Fid = offer.Fid, Name = fi.Name, Size = fi.Length, Token = offer.Token });
    }

    private async Task ServeFile(TcpClient tcp, NetworkStream stream, JsonObject m)
    {
        using var _ = tcp;
        var fid = (string?)m["fid"] ?? "";
        var token = (string?)m["token"] ?? "";
        var who = (string?)m["id"] ?? "";
        FileOffer? offer;
        lock (_lock) offer = _store.Offers.FirstOrDefault(o => o.Fid == fid);
        if (IsIgnored(who)) offer = null;
        if (offer is null || !CryptoEquals(offer.Token, token) || !File.Exists(offer.Path))
        {
            await stream.WriteAsync(Wire.Frame(Wire.Encode(new { t = "err", text = offer is null || offer.Token != token ? "File không còn được chia sẻ." : "Người gửi đã xoá / chuyển file." })));
            return;
        }
        await using var fs = new FileStream(offer.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, useAsync: true);
        await stream.WriteAsync(Wire.Frame(Wire.Encode(new { t = "file", size = fs.Length })), _cts.Token);
        await fs.CopyToAsync(stream, 1 << 16, _cts.Token);
        await stream.FlushAsync(_cts.Token);
        // chờ bên nhận đóng (đọc đủ rồi) — đóng sớm có thể cắt đuôi dữ liệu còn trong bộ đệm
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            wait.CancelAfter(10000);
            await stream.ReadAsync(new byte[1], wait.Token);
        }
        catch { }
        FileServed?.Invoke(fid, who);
    }

    private static bool CryptoEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>Tải file của tin <paramref name="msg"/> về <paramref name="dir"/> (mặc định Downloads\Bcode Chat). Tiến độ qua <see cref="FileProgress"/>.</summary>
    public async Task<string?> DownloadAsync(ChatMessage msg, string? dir = null)
    {
        var f = msg.File ?? throw new InvalidOperationException("Tin không có file.");
        var b = FindBuddy(msg.From);
        if (b is null || !b.Online || b.Port <= 0) { FileProgress?.Invoke(f.Fid, 0, f.Size, "err", $"{msg.FromName} đang offline — chờ họ mở Chat LAN rồi bấm Nhận lại."); return null; }
        dir ??= ChatStore.DownloadDir();
        Directory.CreateDirectory(dir);
        var target = UniquePath(dir, SafeName(f.Name));
        var part = target + ".part";
        using var tcp = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using (var to = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
            {
                to.CancelAfter(5000);
                await tcp.ConnectAsync(IPAddress.Parse(b.Ip), b.Port, to.Token);
            }
            var stream = tcp.GetStream();
            await stream.WriteAsync(Wire.Frame(Wire.Encode(new { t = "get", fid = f.Fid, token = f.Token, id = MyId })), _cts.Token);
            var head = await Wire.ReadFrameAsync(stream, _cts.Token) ?? throw new IOException("Người gửi đóng kết nối.");
            var hm = JsonNode.Parse(head)!.AsObject();
            if ((string?)hm["t"] != "file") throw new IOException((string?)hm["text"] ?? "Người gửi từ chối.");
            var size = (long?)hm["size"] ?? 0;
            var buf = new byte[1 << 16];
            long got = 0, lastReport = 0;
            var lastAt = Environment.TickCount64;
            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                while (got < size)
                {
                    var n = await stream.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, size - got)), _cts.Token);
                    if (n == 0) throw new IOException("Mất kết nối giữa chừng.");
                    await fs.WriteAsync(buf.AsMemory(0, n), _cts.Token);
                    got += n;
                    if (got - lastReport > (1 << 20) || Environment.TickCount64 - lastAt > 250)
                    {
                        lastReport = got; lastAt = Environment.TickCount64;
                        FileProgress?.Invoke(f.Fid, got, size, "run", "");
                    }
                }
            }
            File.Move(part, target, true);
            FileProgress?.Invoke(f.Fid, got, size, "done", target);
            return target;
        }
        catch (Exception ex)
        {
            try { File.Delete(part); } catch { }
            FileProgress?.Invoke(f.Fid, 0, f.Size, "err", ex is OperationCanceledException ? "Hết giờ chờ máy người gửi." : ex.Message);
            return null;
        }
    }

    private static string SafeName(string name)
    {
        name = Path.GetFileName(name ?? "");
        foreach (var c in Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':' })) name = name.Replace(c, '_');
        name = name.Trim().TrimEnd('.');
        return name.Length == 0 || name is "." or ".." ? "file" : Clip(name, 120);
    }

    private static string UniquePath(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; File.Exists(p) || File.Exists(p + ".part"); i++) p = Path.Combine(dir, $"{stem} ({i}){ext}");
        return p;
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Announce(bye: true); } catch { }
        try { _timer?.Dispose(); } catch { }
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        try { _udp?.Dispose(); } catch { }
        List<FramedConnection> all;
        lock (_lock) all = _allConns.ToList();
        foreach (var c in all) c.Close("Thoát");
        _store.SavePrefs();
        _store.SaveOutbox();
    }
}
