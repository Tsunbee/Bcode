using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Bcode.Game.Net;

/// <summary>Khung tin trên TCP: 4 byte độ dài (little-endian) + JSON UTF-8. Giới hạn 1 MB / tin để khách lạ không làm tràn bộ nhớ.</summary>
public static class Wire
{
    public const int MaxFrame = 1 << 20;
    public const int DiscoveryPort = 47810;
    public const int DefaultGamePort = 47811;
    public const string Protocol = "bcgame2";   // bcgame2: phòng tối đa 10 người (thêm 2 Triển khai) — bản cũ bị báo khác phiên bản

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // giữ nguyên tiếng Việt (gói nhỏ hơn \uXXXX)
    };

    public static byte[] Encode(object msg) => JsonSerializer.SerializeToUtf8Bytes(msg, Json);

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

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
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
    private readonly Channel<byte[]> _out = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(512) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private int _closed;

    public string Remote { get; }
    public event Action<byte[]>? Received;
    public event Action<string>? Closed;

    public FramedConnection(TcpClient tcp)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        Remote = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
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
        var frame = new byte[json.Length + 4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        _out.Writer.TryWrite(frame);
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
                Received?.Invoke(f);
            }
        }
        catch (OperationCanceledException) { reason = "Đã ngắt"; }
        catch (Exception ex) { reason = ex is IOException { InnerException: SocketException se } ? $"Mất kết nối ({se.SocketErrorCode})" : "Mất kết nối: " + ex.Message; }
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
        Closed?.Invoke(reason);
    }

    public void Dispose() => Close("Đã đóng");
}
