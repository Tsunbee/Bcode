using System.Net;
using System.Net.Sockets;
using Bcode.Game.Logic;

namespace Bcode.Game.Net;

/// <summary>Máy chủ phòng: TcpListener (không dùng HttpListener — bind ra ngoài localhost cần quyền admin / netsh urlacl) + trả lời tìm phòng UDP.
/// Mỗi kết nối đẩy tin vào <see cref="GameRoom"/>; tin đầu tiên phải là "hello" trong 5 giây.</summary>
public sealed class GameServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly RoomBeacon? _beacon;
    private readonly CancellationTokenSource _cts = new();
    public GameRoom Room { get; }
    public int Port { get; }
    public string? BeaconError { get; }

    private GameServer(TcpListener listener, int port, GameRoom room, IPAddress bind)
    {
        _listener = listener;
        Port = port;
        Room = room;
        try { _beacon = new RoomBeacon(bind, () => new RoomInfo(Wire.Protocol, room.RoomName, Environment.MachineName, Port, room.PlayerCount, GameRoom.MaxPlayers, room.Phase == Phase.Lobby ? "lobby" : "playing")); }
        catch (SocketException ex) { BeaconError = $"Không mở được cổng UDP {Wire.DiscoveryPort} để người khác tìm thấy phòng ({ex.SocketErrorCode}) — người chơi vẫn vào được bằng cách gõ IP."; }
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>Mở phòng ở cổng <paramref name="port"/> (bận thì thử 10 cổng kế tiếp).</summary>
    /// <param name="loopbackOnly">Chỉ nghe 127.0.0.1 (thử nhiều bản trên 1 máy — không bật hộp hỏi tường lửa).</param>
    public static GameServer Start(string roomName, int port = Wire.DefaultGamePort, int minPlayers = 5, bool loopbackOnly = false)
    {
        SocketException? last = null;
        var bind = loopbackOnly ? IPAddress.Loopback : IPAddress.Any;
        for (var p = port; p < port + 10; p++)
        {
            var l = new TcpListener(bind, p);
            l.Server.ExclusiveAddressUse = true;
            try { l.Start(); }
            catch (SocketException ex) { last = ex; continue; }
            return new GameServer(l, p, new GameRoom(roomName, minPlayers), bind);
        }
        throw new InvalidOperationException(last?.SocketErrorCode == SocketError.AccessDenied
            ? $"Windows không cho mở cổng {port} (AccessDenied) — có thể do chính sách tường lửa / phần mềm bảo mật."
            : $"Không mở được cổng TCP {port}–{port + 9}: {last?.Message}");
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
            conn.Received += data => { greeted = true; Room.Enqueue(conn, data); };
            conn.Closed += _ => Room.Enqueue(conn, null);
            conn.Start();
            _ = Task.Delay(5000).ContinueWith(_ => { if (!greeted) conn.Close("Không chào hỏi"); });
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _beacon?.Dispose();
        Room.Dispose();
    }
}

/// <summary>Máy khách: 1 kết nối TCP tới chủ phòng (kể cả chủ phòng cũng tự nối vào 127.0.0.1 — 1 đường code cho mọi người).</summary>
public sealed class GameClient : IDisposable
{
    private FramedConnection? _conn;
    public event Action<byte[]>? Message;
    public event Action<string>? Closed;

    public static async Task<GameClient> ConnectAsync(string host, int port, string name, string? hostToken, int timeoutMs = 4000)
    {
        var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await tcp.ConnectAsync(host, port, cts.Token); }
        catch (OperationCanceledException)
        {
            tcp.Dispose();
            throw new TimeoutException($"Không nối được tới {host}:{port} sau {timeoutMs / 1000} giây — kiểm tra IP, cùng mạng LAN, và tường lửa máy chủ phòng (cho phép BcodeGame trên mạng Private).");
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
        var c = new GameClient();
        var conn = new FramedConnection(tcp);
        conn.Received += d => c.Message?.Invoke(d);
        conn.Closed += r => c.Closed?.Invoke(r);
        c._conn = conn;
        conn.Start();
        conn.Send(new { t = "hello", proto = Wire.Protocol, name, token = hostToken });
        return c;
    }

    public void SendRaw(byte[] json) => _conn?.SendRaw(json);
    public void Send(object msg) => _conn?.Send(msg);
    public void Dispose() => _conn?.Close("Đã rời phòng");
}
