using System.Text.Json.Serialization;

namespace Bcode.Chat.Net;

/// <summary>Gói UDP "tôi đang online" (broadcast ~3 s / lần). Q = hỏi lại (máy nhận trả lời unicast gói của mình — dùng cho IP gõ tay khác mạng con).
/// Bye = sắp thoát. Không mang gì bí mật.</summary>
public sealed record Presence(string Proto, string Id, string Name, string Status, string Text, string Color, int Port, bool Q = false, bool Bye = false, string Ver = "",
    string Av = "", List<RoomAd>? Rooms = null);

/// <summary>Phòng chat công cộng mình đang ngồi — gửi kèm gói Presence để cả LAN dựng danh sách phòng (không có máy chủ giữ danh sách).</summary>
public sealed record RoomAd(string G, string T, string Topic = "");

/// <summary>Trạng thái: online | busy (Bận) | away (Vắng) | invisible (Ẩn — người khác thấy mình offline nhưng mình vẫn nhắn được).
/// Tự chuyển away khi không đụng chuột / phím một lúc (Session).</summary>
public static class Status
{
    public const string Online = "online", Busy = "busy", Away = "away", Invisible = "invisible", Offline = "offline";
    public static string Norm(string? s) => s is Busy or Away or Invisible ? s : Online;
}

/// <summary>Bhoo ID (nickname): 3–32 ký tự chữ không dấu / số / "_" / "." , bắt đầu bằng chữ. Không phân biệt hoa thường (Id = chữ thường).
/// Không có máy chủ nên không "giữ chỗ" được nick — chỉ chặn khi nick đang có người online trong LAN (ChatNode.NickConflict).</summary>
public static class Nick
{
    private static readonly System.Text.RegularExpressions.Regex Re = new("^[A-Za-z][A-Za-z0-9_.]{2,31}$");
    public static bool Valid(string? s) => s is not null && Re.IsMatch(s);
    public static string IdOf(string nick) => nick.Trim().ToLowerInvariant();
    public const string Rule = "Bhoo ID dài 3–32 ký tự, bắt đầu bằng chữ cái, chỉ gồm chữ không dấu, số, dấu _ và dấu chấm.";
}

/// <summary>Một người trong danh sách bạn — gồm cả người đã offline (nhớ trong chat-lan.json, hiện mờ như danh bạ messenger cũ).</summary>
public sealed class Buddy
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#3a6ea5";
    public string Status { get; set; } = Net.Status.Offline;
    public string Text { get; set; } = "";
    /// <summary>Nhóm trong danh sách bạn (cây "Bạn bè", "Đồng nghiệp"…). Chỉ máy mình biết.</summary>
    public string Group { get; set; } = "Bạn bè";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public DateTime LastSeen { get; set; }
    /// <summary>Mã ảnh đại diện đã tải về máy (avatars\&lt;id&gt;.jpg); rỗng = chưa có / bạn không đặt ảnh.</summary>
    public string AvHash { get; set; } = "";
    /// <summary>Mã ảnh bạn đang báo (gói Presence / hello) — khác <see cref="AvHash"/> thì hỏi tải ảnh mới.</summary>
    [JsonIgnore] public string AvWant { get; set; } = "";
    /// <summary>Phòng công cộng bạn đang ngồi (từ gói Presence gần nhất).</summary>
    [JsonIgnore] public List<RoomAd> Rooms { get; set; } = new();
    /// <summary>Đã ẩn khỏi danh sách (vẫn nhận tin nếu họ nhắn).</summary>
    public bool Hidden { get; set; }
    [JsonIgnore] public bool Online => Status != Net.Status.Offline;
}

/// <summary>Thành viên phòng chat nhóm.</summary>
public sealed record Member(string Id, string Name);

/// <summary>Phòng chat nhóm (kiểu "conference" ngày xưa) — không có chủ phòng: mỗi tin gửi thẳng tới từng thành viên, ai cũng giữ danh sách thành viên.</summary>
public sealed class GroupMeta
{
    public string Gid { get; set; } = "";
    public string Title { get; set; } = "";
    public List<Member> Members { get; set; } = new();
}

/// <summary>File gửi kèm tin (người nhận bấm "Nhận" mới tải: nối TCP mới tới máy người gửi, đưa Fid + Token).</summary>
public sealed class FileMeta
{
    public string Fid { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Token { get; set; } = "";
}

/// <summary>Một tin. Kind: text | buzz | file | sys (thông báo: tạo nhóm, mời thêm) | leave (rời nhóm).
/// Conv KHÔNG đi trên dây: người nhận tự suy ra (có Group → "g:gid", không → "u:id người gửi").</summary>
public sealed class ChatMessage
{
    public string Mid { get; set; } = "";
    public string From { get; set; } = "";
    public string FromName { get; set; } = "";
    public string Kind { get; set; } = "text";
    public string Text { get; set; } = "";
    /// <summary>Kiểu chữ người gửi chọn (thanh font / màu chữ): "b" đậm, "i" nghiêng, "u" gạch chân; màu #rrggbb; cỡ 10–20.</summary>
    public string? Style { get; set; }
    public string? Fg { get; set; }
    public int? Size { get; set; }
    /// <summary>Giờ gửi (ms Unix, đồng hồ máy người gửi).</summary>
    public long At { get; set; }
    public GroupMeta? Group { get; set; }
    /// <summary>Tin trong phòng công cộng (không có Group). Không lưu lịch sử, không xếp hàng chờ người offline.</summary>
    public RoomAd? Room { get; set; }
    /// <summary>Kind "invite": rủ vào Xem chung / Giải lao. Máy nhận nối tới IP người mời + <see cref="InviteMeta.Port"/>.</summary>
    public InviteMeta? Invite { get; set; }
    public FileMeta? File { get; set; }
}

/// <summary>Lời mời vào ứng dụng LAN khác của Bcode. App: "watch" (Xem chung, cổng 47831) | "game" (Giải lao, cổng 47811).</summary>
public sealed record InviteMeta(string App, int Port, string Room = "");

/// <summary>Tin chờ gửi tới 1 người (họ offline / chưa nối được). Giữ trong outbox.json tới khi họ báo "ack".</summary>
public sealed class OutItem
{
    public string To { get; set; } = "";
    public ChatMessage Msg { get; set; } = new();
    public DateTime Queued { get; set; }
    /// <summary>Kết nối đã gửi tin này (để không gửi lại trên cùng kết nối; nối lại thì gửi lại — bên nhận bỏ trùng theo Mid).</summary>
    [JsonIgnore] public FramedConnection? SentOn { get; set; }
}

/// <summary>File mình đã đưa ra cho người khác tải (theo Fid). Lưu để tải được cả sau khi mở lại Chat LAN (tin offline có file).</summary>
public sealed class FileOffer
{
    public string Fid { get; set; } = "";
    public string Path { get; set; } = "";
    public string Token { get; set; } = "";
    public DateTime Created { get; set; }
}
