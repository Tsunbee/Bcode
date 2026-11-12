namespace Bcode.Game.Logic;

public readonly record struct Rect(float X, float Y, float W, float H)
{
    public bool Contains(float px, float py) => px >= X && px <= X + W && py >= Y && py <= Y + H;
    public float Cx => X + W / 2;
    public float Cy => Y + H / 2;
}

public sealed record MapRoom(int Id, string Kind, Rect Rect, int Module);          // Kind: module | dev | test | meet | hall
public sealed record Station(int Id, string Kind, float X, float Y, int Room, int Num); // Kind: task | dev | test | button

/// <summary>Bản đồ văn phòng "tầng triển khai": 5 phòng phân hệ phía trên + 6 phía dưới (mỗi phòng = 1 sheet câu hỏi), giữa là Khu Dev (5 máy),
/// Phòng họp (bàn + nút họp khẩn / bàn giao) và Khu Tester (2 máy). Vách kính: tầm nhìn chỉ tính theo bán kính, không bị tường che.
/// Đi được = nằm trong hợp các hình chữ nhật <see cref="Walk"/> (phòng + hành lang + cửa) — khách dùng CHÍNH dữ liệu này để đoán trước bước đi.</summary>
public sealed class GameMap
{
    public const float Width = 2640, Height = 1760;
    public const float PlayerHalf = 16;
    public const int ModuleRooms = 11;

    public List<Rect> Walk { get; } = new();
    public List<Rect> Doors { get; } = new();
    public List<Rect> Halls { get; } = new();
    public List<MapRoom> Rooms { get; } = new();
    public List<Station> Stations { get; } = new();
    public List<(float X, float Y)> Seats { get; } = new();
    public (float X, float Y) Table { get; }

    public GameMap()
    {
        var id = 0;
        // Hành lang trên / dưới + 2 hành lang dọc nối giữa.
        foreach (var h in new[] { new Rect(40, 420, 2560, 100), new Rect(40, 1240, 2560, 100), new Rect(916, 520, 68, 720), new Rect(1656, 520, 68, 720) })
        { Walk.Add(h); Halls.Add(h); }
        Rooms.Add(new MapRoom(id++, "hall", new Rect(40, 420, 2560, 100), -1));
        Rooms.Add(new MapRoom(id++, "hall", new Rect(40, 1240, 2560, 100), -1));

        // 5 phòng phân hệ phía trên (module 0..4)
        for (var i = 0; i < 5; i++)
        {
            var r = new Rect(40 + i * 520, 40, 480, 320);
            AddRoom(ref id, "module", r, i);
            AddDoor(new Rect(r.Cx - 55, 360, 110, 60));                 // cửa xuống hành lang trên
        }
        // 6 phòng phân hệ phía dưới (module 5..10)
        for (var i = 0; i < 6; i++)
        {
            var r = new Rect(40 + i * 432, 1400, 400, 320);
            AddRoom(ref id, "module", r, 5 + i);
            AddDoor(new Rect(r.Cx - 55, 1340, 110, 60));                // cửa lên hành lang dưới
        }
        // Khu giữa
        var dev = new Rect(40, 580, 860, 600);
        var meet = new Rect(1000, 580, 640, 600);
        var test = new Rect(1740, 580, 860, 600);
        var devId = AddRoom(ref id, "dev", dev, -1);
        var meetId = AddRoom(ref id, "meet", meet, -1);
        var testId = AddRoom(ref id, "test", test, -1);
        foreach (var r in new[] { dev, meet, test })
        {
            foreach (var fx in r == meet ? new[] { 0.5f } : new[] { 0.25f, 0.75f })
            {
                var cx = r.X + r.W * fx;
                AddDoor(new Rect(cx - 55, 520, 110, 60));                   // cửa lên
                AddDoor(new Rect(cx - 55, 1180, 110, 60));                  // cửa xuống
            }
        }
        // Cửa hông phòng họp ra 2 hành lang dọc
        AddDoor(new Rect(984, 830, 16, 100));
        AddDoor(new Rect(1640, 830, 16, 100));
        AddDoor(new Rect(900, 830, 16, 100));
        AddDoor(new Rect(1724, 830, 16, 100));

        var sid = 0;
        foreach (var room in Rooms.Where(r => r.Kind == "module"))
            Stations.Add(new Station(sid++, "task", room.Rect.Cx, room.Rect.Cy + 30, room.Id, room.Module));
        for (var i = 0; i < 5; i++)
            Stations.Add(new Station(sid++, "dev", dev.X + 110 + i * 160, dev.Y + (i % 2 == 0 ? 170 : 400), devId, i + 1));
        Stations.Add(new Station(sid++, "test", test.X + 250, test.Y + 230, testId, 1));
        Stations.Add(new Station(sid++, "test", test.X + 610, test.Y + 230, testId, 2));
        Table = (meet.Cx, meet.Cy);
        Stations.Add(new Station(sid, "button", meet.Cx, meet.Cy, meetId, 0));
        for (var i = 0; i < 8; i++)
        {
            var a = Math.PI * 2 * i / 8 - Math.PI / 2;
            Seats.Add(((float)(meet.Cx + Math.Cos(a) * 200), (float)(meet.Cy + Math.Sin(a) * 200)));
        }
    }

    private void AddDoor(Rect r) { Walk.Add(r); Doors.Add(r); }

    private int AddRoom(ref int id, string kind, Rect r, int module)
    {
        Rooms.Add(new MapRoom(id, kind, r, module));
        Walk.Add(r);
        return id++;
    }

    public bool PointFree(float x, float y)
    {
        foreach (var r in Walk) if (r.Contains(x, y)) return true;
        return false;
    }

    /// <summary>Cả 4 góc ô vuông của người chơi đều nằm trong vùng đi được.</summary>
    public bool CanStand(float x, float y) =>
        PointFree(x - PlayerHalf, y - PlayerHalf) && PointFree(x + PlayerHalf, y - PlayerHalf) &&
        PointFree(x - PlayerHalf, y + PlayerHalf) && PointFree(x + PlayerHalf, y + PlayerHalf);

    /// <summary>Bước đi trượt theo tường: thử cả 2 trục, kẹt trục nào thì giữ trục đó.</summary>
    public (float X, float Y) Move(float x, float y, float dx, float dy)
    {
        // Chia nhỏ bước để không xuyên cửa hẹp khi khung hình dài.
        var steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)) / 8));
        var sx = dx / steps; var sy = dy / steps;
        for (var i = 0; i < steps; i++)
        {
            if (CanStand(x + sx, y)) x += sx;
            if (CanStand(x, y + sy)) y += sy;
        }
        return (x, y);
    }

    public MapRoom? RoomAt(float x, float y)
    {
        foreach (var r in Rooms) if (r.Kind != "hall" && r.Rect.Contains(x, y)) return r;
        return null;
    }

    public object ToDto(IReadOnlyList<string> moduleNames) => new
    {
        w = Width, h = Height, half = PlayerHalf,
        walk = Walk.Select(r => new[] { r.X, r.Y, r.W, r.H }),
        halls = Halls.Select(r => new[] { r.X, r.Y, r.W, r.H }),
        doors = Doors.Select(r => new[] { r.X, r.Y, r.W, r.H }),
        rooms = Rooms.Where(r => r.Kind != "hall").Select(r => new
        {
            id = r.Id, kind = r.Kind, rect = new[] { r.Rect.X, r.Rect.Y, r.Rect.W, r.Rect.H }, module = r.Module,
            name = r.Kind switch
            {
                "module" => r.Module < moduleNames.Count ? moduleNames[r.Module] : "Kho lưu trữ",
                "dev" => "Khu Dev",
                "test" => "Khu Tester",
                _ => "Phòng họp",
            },
        }),
        stations = Stations.Select(s => new { id = s.Id, kind = s.Kind, x = s.X, y = s.Y, room = s.Room, num = s.Num }),
        seats = Seats.Select(s => new[] { s.X, s.Y }),
    };
}
