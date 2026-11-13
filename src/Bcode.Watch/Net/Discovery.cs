using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Bcode.Watch.Net;

/// <summary>Thông tin phòng chiếu chủ phòng trả lời khi được hỏi qua UDP. Lock = phòng có mật khẩu — gói này KHÔNG mang mật khẩu / salt / hash (xem Shared/RoomAuth.cs).
/// Bản cũ đọc gói mới vẫn được (bỏ qua trường lạ).</summary>
public sealed record RoomInfo(string Proto, string Name, string Host, int Port, int Players, int Max, string State, string Ip = "", bool Lock = false);

/// <summary>Chủ phòng: nghe UDP <see cref="Wire.DiscoveryPort"/>, ai gửi "bcwatch1?" thì trả lời (unicast) thông tin phòng.
/// Bind ReuseAddress để nhiều bản trên cùng máy (thử nghiệm) cùng nghe được.</summary>
public sealed class RoomBeacon : IDisposable
{
    private readonly UdpClient _udp;
    private readonly Func<RoomInfo> _info;
    private readonly CancellationTokenSource _cts = new();

    public RoomBeacon(IPAddress bind, Func<RoomInfo> info)
    {
        _info = info;
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(bind, Wire.DiscoveryPort));
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                if (Encoding.ASCII.GetString(r.Buffer) != Wire.Protocol + "?") continue;
                var reply = JsonSerializer.SerializeToUtf8Bytes(_info(), Wire.Json);
                await _udp.SendAsync(reply, r.RemoteEndPoint, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* gói lạ / máy khách đã đóng cổng: bỏ qua */ }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch { }
    }
}

/// <summary>Máy khách: gửi "bcwatch1?" broadcast (255.255.255.255 + broadcast từng mạng con + 127.0.0.1) mỗi 1,5 s, gom phòng trả lời.</summary>
public sealed class RoomFinder : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, (RoomInfo Room, DateTime Seen)> _rooms = new();
    public event Action<IReadOnlyList<RoomInfo>>? Changed;
    public string? Error { get; private set; }

    public RoomFinder()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(ProbeLoop);
    }

    private static IEnumerable<IPAddress> Targets()
    {
        yield return IPAddress.Broadcast;
        yield return IPAddress.Loopback;
        foreach (var nic in SafeNics())
        {
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(b);
            }
        }
    }

    internal static IEnumerable<NetworkInterface> SafeNics()
    {
        NetworkInterface[] all;
        try { all = NetworkInterface.GetAllNetworkInterfaces(); } catch { yield break; }
        foreach (var n in all)
            if (n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                yield return n;
    }

    /// <summary>IPv4 của máy (để chủ phòng đọc cho người khác gõ tay khi broadcast không qua được).</summary>
    public static List<string> LocalIPv4()
    {
        var list = new List<string>();
        foreach (var nic in SafeNics())
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    list.Add(ua.Address.ToString());
        return list.Distinct().ToList();
    }

    /// <summary>IP LAN "chính" để dựng link mời: card có default gateway (mạng văn phòng thật) — card ảo Hyper-V / VMware / WSL thường không có;
    /// Ethernet / Wi-Fi trước, VPN sau. Không có thì IP đầu tiên; không có IP LAN nào thì null.</summary>
    public static string? PrimaryIPv4()
    {
        try
        {
            var best = SafeNics()
                .Select(n => (Nic: n, Props: n.GetIPProperties()))
                .Where(x => x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .OrderBy(x => x.Nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .SelectMany(x => x.Props.UnicastAddresses)
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (best != null) return best.ToString();
        }
        catch { }
        return LocalIPv4().FirstOrDefault();
    }

    private async Task ProbeLoop()
    {
        var probe = Encoding.ASCII.GetBytes(Wire.Protocol + "?");
        while (!_cts.IsCancellationRequested)
        {
            var sent = 0;
            // Liệt kê card mạng có thể ném lỗi (card vừa tắt / VPN đang nối) — trước đây ngoài try nên vòng hỏi chết hẳn:
            // không gửi gói hỏi, không bỏ phòng cũ nữa → danh sách giữ mãi phòng đã đóng.
            List<IPAddress> targets;
            try { targets = Targets().Distinct().ToList(); }
            catch { targets = new() { IPAddress.Broadcast, IPAddress.Loopback }; }
            foreach (var t in targets)
            {
                try { await _udp.SendAsync(probe, new IPEndPoint(t, Wire.DiscoveryPort)); sent++; }
                catch { /* mạng con không gửi được: bỏ qua */ }
            }
            Error = sent == 0 ? "Không gửi được gói tìm phòng (mạng / tường lửa)." : null;
            // Phòng không trả lời quá 5 s thì bỏ khỏi danh sách.
            bool changed;
            lock (_rooms)
            {
                var old = _rooms.Where(kv => DateTime.UtcNow - kv.Value.Seen > TimeSpan.FromSeconds(5)).Select(kv => kv.Key).ToList();
                foreach (var k in old) _rooms.Remove(k);
                changed = old.Count > 0;
            }
            if (changed) Raise();
            try { await Task.Delay(1500, _cts.Token); } catch { break; }
        }
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await _udp.ReceiveAsync(_cts.Token);
                var room = JsonSerializer.Deserialize<RoomInfo>(r.Buffer, Wire.Json);
                if (room is null || room.Proto != Wire.Protocol) continue;
                var ip = r.RemoteEndPoint.Address.ToString();
                // Cùng máy: trả lời có thể đến từ 127.0.0.1 lẫn IP LAN — gộp theo cổng + tên.
                var key = room.Host + "|" + room.Port + "|" + room.Name;
                lock (_rooms)
                {
                    var keepIp = _rooms.TryGetValue(key, out var prev) && !IPAddress.IsLoopback(IPAddress.Parse(prev.Room.Ip)) ? prev.Room.Ip : ip;
                    _rooms[key] = (room with { Ip = keepIp }, DateTime.UtcNow);
                }
                Raise();
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { /* gói lạ */ }
        }
    }

    private void Raise()
    {
        List<RoomInfo> list;
        lock (_rooms) list = _rooms.Values.Select(v => v.Room).OrderBy(r => r.Name).ToList();
        Changed?.Invoke(list);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _udp.Dispose(); } catch { }
    }
}
