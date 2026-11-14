using System.Text;
using System.Text.Json;

namespace Bcode.Chat.Net;

/// <summary>Lựa chọn + danh bạ nhớ giữa các lần mở: users\<id>\chat-lan.json trong thư mục dữ liệu Bhoo (xem LoginPrefs).</summary>
public sealed class ChatPrefs
{
    /// <summary>Mã riêng của máy + tài khoản Windows này (sinh 1 lần). Bạn bè nhận ra nhau bằng mã này, đổi tên vẫn giữ lịch sử.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Environment.UserName;
    public string Status { get; set; } = Net.Status.Online;
    public string Text { get; set; } = "";
    public string Color { get; set; } = "#3a6ea5";
    /// <summary>IP gõ tay (máy khác mạng con — broadcast không tới).</summary>
    public List<string> ManualPeers { get; set; } = new();
    public List<Buddy> Buddies { get; set; } = new();
    public List<GroupMeta> Groups { get; set; } = new();
    public bool Sound { get; set; } = true;
    /// <summary>Bấm X thì thu xuống khay đồng hồ (mặc định) thay vì thoát.</summary>
    public bool CloseToTray { get; set; } = true;
    /// <summary>Thông báo trượt lên ở góc màn hình (bạn online, tin mới khi cửa sổ chat không đang mở trước mặt).</summary>
    public bool Popups { get; set; } = true;
    /// <summary>Tự chuyển "Vắng" sau N phút không đụng chuột / phím (0 = tắt).</summary>
    public int AutoAwayMinutes { get; set; } = 10;
    /// <summary>Kiểu chữ lần trước của thanh font: đậm / nghiêng / gạch chân, màu, cỡ.</summary>
    public string FontStyle { get; set; } = "";
    public string FontColor { get; set; } = "#000000";
    public int FontSize { get; set; } = 13;
    /// <summary>Mã ảnh đại diện của mình (12 ký tự hex của SHA-256 file _me.jpg); rỗng = không đặt ảnh.</summary>
    public string AvatarHash { get; set; } = "";
    /// <summary>Vị trí cửa sổ danh sách bạn.</summary>
    public int[]? ListBounds { get; set; }
}

/// <summary>Lưu trữ trên máy: chat-lan.json (lựa chọn + danh bạ + nhóm), outbox.json (tin chờ gửi), offers.json (file đưa ra),
/// seen.txt (Mid đã nhận — bỏ tin trùng khi người gửi gửi lại), history\*.jsonl (lịch sử từng cuộc chat, mỗi dòng 1 tin).
/// Mọi hàm đều tự khoá — gọi từ luồng mạng hay luồng giao diện đều được.</summary>
public sealed class ChatStore
{
    private const int MaxSeen = 5000;
    private readonly object _lock = new();
    private readonly string _dir;
    private readonly HashSet<string> _seen = new();
    private readonly Queue<string> _seenOrder = new();
    public ChatPrefs Prefs { get; }
    public List<OutItem> Outbox { get; }
    public List<FileOffer> Offers { get; }
    public string Dir => _dir;
    /// <summary>Ảnh đại diện: _me.jpg (của mình) + &lt;id&gt;.jpg (bạn bè, tải qua LAN). Trang đọc qua https://av.chat.local/.</summary>
    public string AvatarDir => Path.Combine(_dir, "avatars");
    public string MyAvatarPath => Path.Combine(AvatarDir, "_me.jpg");

    private static readonly JsonSerializerOptions FileJson = new(Wire.Json) { WriteIndented = true };

    public ChatStore(string? dir)
    {
        _dir = dir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "chat");
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "history"));
        Directory.CreateDirectory(AvatarDir);
        Prefs = Load<ChatPrefs>("chat-lan.json") ?? new ChatPrefs();
        if (string.IsNullOrWhiteSpace(Prefs.Id)) Prefs.Id = Guid.NewGuid().ToString("N");
        foreach (var b in Prefs.Buddies) b.Status = Net.Status.Offline;   // mở lên: chưa ai online cho tới khi nghe thấy
        Outbox = Load<List<OutItem>>("outbox.json") ?? new();
        Offers = Load<List<FileOffer>>("offers.json") ?? new();
        Offers.RemoveAll(o => DateTime.UtcNow - o.Created > TimeSpan.FromDays(30) || !File.Exists(o.Path));
        try
        {
            var p = Path.Combine(_dir, "seen.txt");
            if (File.Exists(p))
                foreach (var l in File.ReadAllLines(p).TakeLast(MaxSeen))
                    if (l.Length > 0 && _seen.Add(l)) _seenOrder.Enqueue(l);
        }
        catch { }
    }

    private T? Load<T>(string name) where T : class
    {
        try
        {
            var p = Path.Combine(_dir, name);
            return File.Exists(p) ? JsonSerializer.Deserialize<T>(File.ReadAllText(p), Wire.Json) : null;
        }
        catch { return null; }
    }

    private void Write(string name, object data)
    {
        try
        {
            var p = Path.Combine(_dir, name);
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, FileJson));
            File.Move(tmp, p, true);
        }
        catch { /* không ghi được thì thôi — lần sau ghi lại */ }
    }

    public void SavePrefs() { lock (_lock) Write("chat-lan.json", Prefs); }
    public void SaveOutbox() { lock (_lock) Write("outbox.json", Outbox); }
    public void SaveOffers() { lock (_lock) Write("offers.json", Offers); }

    /// <summary>true = Mid mới (lần đầu thấy) — ghi lại để lần sau bỏ trùng.</summary>
    public bool MarkSeen(string mid)
    {
        lock (_lock)
        {
            if (!_seen.Add(mid)) return false;
            _seenOrder.Enqueue(mid);
            var rewrite = false;
            while (_seenOrder.Count > MaxSeen) { _seen.Remove(_seenOrder.Dequeue()); rewrite = true; }
            try
            {
                var p = Path.Combine(_dir, "seen.txt");
                if (rewrite) File.WriteAllLines(p, _seenOrder);
                else File.AppendAllText(p, mid + "\n");
            }
            catch { }
            return true;
        }
    }

    private string HistoryPath(string conv)
    {
        var safe = new StringBuilder();
        foreach (var c in conv) safe.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '_');
        return Path.Combine(_dir, "history", safe + ".jsonl");
    }

    public void AppendHistory(string conv, ChatMessage m)
    {
        lock (_lock)
        {
            try { File.AppendAllText(HistoryPath(conv), JsonSerializer.Serialize(m, Wire.Json) + "\n", Encoding.UTF8); }
            catch { }
        }
    }

    /// <summary><paramref name="max"/> tin cuối của cuộc chat (cũ → mới).</summary>
    public List<ChatMessage> LoadHistory(string conv, int max = 200)
    {
        lock (_lock)
        {
            var list = new List<ChatMessage>();
            try
            {
                var p = HistoryPath(conv);
                if (!File.Exists(p)) return list;
                foreach (var l in File.ReadLines(p, Encoding.UTF8).TakeLast(max))
                {
                    try { if (JsonSerializer.Deserialize<ChatMessage>(l, Wire.Json) is { } m) list.Add(m); } catch { }
                }
            }
            catch { }
            return list;
        }
    }

    /// <summary>Mọi cuộc chat có lịch sử trên máy (cho cửa sổ xem lại lịch sử). Tên file đã bị "làm sạch" nên không suy ngược được —
    /// đoán theo danh bạ / phòng hiện có, file còn lại (phòng đã rời, bạn đã xoá) thì đọc tin đầu để biết cuộc chat.</summary>
    public List<(string Conv, DateTime Modified, long Bytes)> HistoryIndex(string myId)
    {
        lock (_lock)
        {
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in Prefs.Buddies) known[HistoryPath("u:" + b.Id)] = "u:" + b.Id;
            foreach (var g in Prefs.Groups) known[HistoryPath("g:" + g.Gid)] = "g:" + g.Gid;
            var list = new List<(string, DateTime, long)>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(Path.Combine(_dir, "history"), "*.jsonl"))
                {
                    var conv = known.TryGetValue(f, out var k) ? k : null;
                    if (conv is null)
                        foreach (var l in File.ReadLines(f, Encoding.UTF8).Take(50))
                        {
                            try
                            {
                                var m = JsonSerializer.Deserialize<ChatMessage>(l, Wire.Json);
                                if (m?.Group is { Gid.Length: > 0 } g) { conv = "g:" + g.Gid; break; }
                                if (m is not null && m.From != myId && m.From.Length > 0) { conv = "u:" + m.From; break; }
                            }
                            catch { }
                        }
                    if (conv is not null) { var fi = new FileInfo(f); list.Add((conv, fi.LastWriteTimeUtc, fi.Length)); }
                }
            }
            catch { }
            return list;
        }
    }

    public void ClearHistory(string conv)
    {
        lock (_lock) { try { File.Delete(HistoryPath(conv)); } catch { } }
    }

    /// <summary>Thư mục lưu file nhận: Downloads\Bhoo (Windows không có Downloads thì Documents).</summary>
    public static string DownloadDir()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dl = Path.Combine(home, "Downloads");
        if (!Directory.Exists(dl)) dl = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(dl)) dl = Path.GetTempPath();
        return Path.Combine(dl, "Bhoo");
    }
}
