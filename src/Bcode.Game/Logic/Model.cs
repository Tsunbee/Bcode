using Bcode.Game.Data;
using Bcode.Game.Net;

namespace Bcode.Game.Logic;

public enum Role { TrienKhai, Tester, LapTrinh, BaoHanh }
public enum Phase { Lobby, Play, Meeting, Result, Over }

public static class Roles
{
    public static string Code(Role r) => r switch
    {
        Role.TrienKhai => "tk",
        Role.Tester => "tester",
        Role.LapTrinh => "dev",
        _ => "bh",
    };

    public static bool IsGood(Role r) => r is Role.TrienKhai or Role.Tester;

    /// <summary>Bảng chia vai đã chốt (5–8 người). Ít hơn 5 chỉ dùng khi thử (--min-players): 1 Lập trình, 1 Tester, còn lại Triển khai.</summary>
    public static List<Role> Table(int n)
    {
        var (tk, tester, dev, bh) = n switch
        {
            5 => (3, 1, 1, 0),
            6 => (3, 1, 1, 1),
            7 => (4, 1, 1, 1),
            8 => (4, 1, 2, 1),
            _ => (Math.Max(1, n - 2), n >= 3 ? 1 : 0, 1, 0),
        };
        var list = new List<Role>();
        list.AddRange(Enumerable.Repeat(Role.TrienKhai, tk));
        list.AddRange(Enumerable.Repeat(Role.Tester, tester));
        list.AddRange(Enumerable.Repeat(Role.LapTrinh, dev));
        list.AddRange(Enumerable.Repeat(Role.BaoHanh, bh));
        while (list.Count > n) list.RemoveAt(0);
        return list;
    }
}

public sealed class GameSettings
{
    public int Minutes { get; set; } = 15;      // hạn dự án
    public int TasksPer { get; set; } = 4;      // số nhiệm vụ mỗi Triển khai
    public int QPerTask { get; set; } = 2;      // số câu đúng để xong 1 nhiệm vụ
    public int Discuss { get; set; } = 60;      // giây thảo luận trước khi được vote
    public int Vote { get; set; } = 30;         // giây bỏ phiếu
    public int KillCd { get; set; } = 25;       // hồi chiêu giết của Lập trình
    public int BugLimit { get; set; } = 4;      // số lỗi mở cùng lúc thì phe xấu thắng

    public void Clamp()
    {
        Minutes = Math.Clamp(Minutes, 3, 40);
        TasksPer = Math.Clamp(TasksPer, 1, 8);
        QPerTask = Math.Clamp(QPerTask, 1, 4);
        Discuss = Math.Clamp(Discuss, 10, 180);
        Vote = Math.Clamp(Vote, 10, 120);
        KillCd = Math.Clamp(KillCd, 10, 90);
        BugLimit = Math.Clamp(BugLimit, 2, 10);
    }
}

public sealed class GTask
{
    public int Id;
    public int Module;
    public required Business Biz;
    public int Need;
    public int Got;
    public bool Done;
    public bool Fake;        // nhiệm vụ "giả vờ" của phe xấu: làm được nhưng không tính tiến độ
    public int Hints;        // lượt gợi ý (loại bớt phương án sai) còn lại
}

public sealed class OpenQuestion
{
    public required string Token;
    public required Question Q;
    public required int[] Shown;   // chỉ số phương án GỐC được gửi cho khách (gợi ý = bớt phương án sai)
    public GTask? Task;            // null = tin nhắn khách hàng
    public double Expires;
}

public sealed class Act
{
    public required string Kind;   // dev | test
    public required Station Station;
    public double Until;
}

public sealed class Player
{
    public int Id;
    public string Name = "";
    public int Color;
    public bool IsHost;
    public FramedConnection? Conn;
    public bool Left;

    public Role Role;
    public bool Pm;          // Trưởng dự án
    public bool PmUsed;
    public bool Alive = true;
    public float X, Y, Dx, Dy;
    public Act? Act;
    public OpenQuestion? Q;
    public OpenQuestion? Msg;
    public double LockUntil;
    public double KillReadyAt, PlantReadyAt, BhReadyAt, NextMsgAt, ChatReadyAt;
    public int Emergencies = 1;
    public int BhReports = 2;
    public int? Vote;        // -1 = phiếu trắng
    public List<GTask> Tasks = new();
    public HashSet<Question> Used = new();

    public bool IsGood => Roles.IsGood(Role);
    public void Send(object msg) => Conn?.Send(msg);
}

public sealed class Bug
{
    public int Id;
    public int Pc;
    public double At;
    public int By;
    public bool Open = true;
}

public sealed record Body(int Id, float X, float Y);
