using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Bcode.Chat.Net;

/// <summary>Khung tin trên TCP: 4 byte độ dài (little-endian) + JSON UTF-8 — cùng kiểu với Xem chung / Giải lao. Giới hạn 256 KB / tin.
/// Cổng riêng của Chat LAN (khác Watch 47830/47831, Game) để chạy cùng lúc không đụng nhau.</summary>
public static class Wire
{
    public const int MaxFrame = 256 * 1024;
    /// <summary>UDP: mỗi máy báo "đang online" + nghe máy khác báo.</summary>
    public const int DiscoveryPort = 47860;
    /// <summary>TCP nhận tin (bận thì +1..+9).</summary>
    public const int DefaultPort = 47861;
    public const string Protocol = "bcchat1";

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // giữ nguyên tiếng Việt
    };

    public static byte[] Encode(object msg) => JsonSerializer.SerializeToUtf8Bytes(msg, Json);

    public static byte[] Frame(byte[] json)
    {
        var frame = new byte[json.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Đọc đủ 1 khung; null = đầu kia đã đóng.</summary>
    public static async Task<byte[]?> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[4];
        if (!await ReadExactAsync(s, head, ct)) return null;
        var len = BinaryPrimitives.ReadInt32LittleEndian(head);
        if (len < 0 || len > MaxFrame) throw new InvalidDataException("Khung tin quá lớn: " + len);
        var body = new byte[len];
        if (!await ReadExactAsync(s, body, ct)) return null;
        return body;
    }

    public static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var off = 0;
        while (off < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(off), ct);
            if (n == 0) return false;
            off += n;
        }
        return true;
    }

    public static string Text(byte[] utf8) => Encoding.UTF8.GetString(utf8);
}

/// <summary>Một kết nối TCP có hàng đợi gửi riêng (ghi tuần tự trên 1 task) — gọi <see cref="Send"/> từ luồng nào cũng được, không chặn.</summary>
public sealed class FramedConnection : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly Channel<byte[]> _out = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private int _closed;

    public string Remote { get; }
    public System.Net.IPAddress? RemoteIp { get; }
    /// <summary>Id người ở đầu kia (sau khi chào "hello").</summary>
    public string PeerId { get; set; } = "";
    public bool IsClosed => _closed != 0;
    public event Action<FramedConnection, byte[]>? Received;
    public event Action<FramedConnection, string>? Closed;

    public FramedConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        Remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
        RemoteIp = (tcp.Client.RemoteEndPoint as System.Net.IPEndPoint)?.Address;
        if (RemoteIp is { IsIPv4MappedToIPv6: true }) RemoteIp = RemoteIp.MapToIPv4();
    }

    public void Start()
    {
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
    }

    public void Send(object msg) => SendRaw(Wire.Encode(msg));

    public void SendRaw(byte[] json)
    {
        if (_closed != 0) return;
        _out.Writer.TryWrite(Wire.Frame(json));
    }

    private async Task ReadLoop()
    {
        var reason = "Mất kết nối";
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var f = await Wire.ReadFrameAsync(_stream, _cts.Token);
                if (f is null) { reason = "Đầu kia đã đóng kết nối"; break; }
                Received?.Invoke(this, f);
            }
        }
        catch (OperationCanceledException) { reason = "Đã ngắt"; }
        catch (Exception ex) { reason = "Mất kết nối: " + ex.Message; }
        Close(reason);
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var frame in _out.Reader.ReadAllAsync(_cts.Token))
                await _stream.WriteAsync(frame, _cts.Token);
        }
        catch { Close("Mất kết nối khi gửi"); }
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { _cts.Cancel(); } catch { }
        _out.Writer.TryComplete();
        try { _tcp.Close(); } catch { }
        Closed?.Invoke(this, reason);
    }

    public void Dispose() => Close("Đã đóng");
}
