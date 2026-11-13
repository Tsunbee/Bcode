using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Bcode.Game.Data;
using Bcode.Game.Net;
using Bcode.Shared;

namespace Bcode.Game.Logic;

/// <summary>Trạng thái THẬT của một phòng — chỉ chạy trên máy chủ phòng. Mọi thay đổi xảy ra trên 1 luồng tick (20 lần/giây):
/// tin của khách xếp hàng ở <see cref="_inbox"/> rồi được xử lý đầu mỗi tick, nên luật chơi không cần khoá.
/// Chống gian lận: khách chỉ nhận vai của CHÍNH mình (Lập trình / Bảo hành biết đồng đội), câu hỏi không kèm đáp án (chủ phòng tự chấm),
/// vị trí người khác chỉ gửi khi trong tầm nhìn; vai mọi người chỉ lộ ở màn kết thúc.</summary>
public sealed partial class GameRoom : IDisposable
{
    public const int MaxPlayers = 8;
    public const double Tick = 0.05;
    public const float Speed = 230, Vision = 360, KillRange = 95, ReportRange = 120, UseRange = 85, ButtonRange = 120;

    private readonly ConcurrentQueue<(FramedConnection Conn, byte[]? Data)> _inbox = new();
    private readonly Dictionary<FramedConnection, Player> _byConn = new();
    private readonly List<Player> _players = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Thread _thread;
    private volatile bool _stop;
    private readonly Random _rng = new();
    private int _nextId = 1;

    public string RoomName { get; }
    public string HostToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    public int MinPlayers { get; }
    public GameMap Map { get; } = new();
    public GameSettings Settings { get; } = new();
    public QuestionBank? Bank { get; private set; }
    public Phase Phase { get; private set; } = Phase.Lobby;

    // Trạng thái 1 ván
    private double _now;                 // giây kể từ khi tạo phòng (đồng hồ của tick)
    private double _projectLeft;         // giây còn lại của hạn dự án (dừng khi họp)
    private readonly List<Body> _bodies = new();
    private readonly List<Bug> _bugs = new();
    private int _taskSeq, _bugSeq;
    private double _emergencyReadyAt, _pmReadyAt, _handoverReadyAt;
    private double _discussEnds, _voteEnds, _resultEnds;
    private (double Ends, bool Rejected)? _review;
    private List<string> _moduleNames = new();
    private object? _mapDto;

    /// <summary>Số người + trạng thái cho gói trả lời tìm phòng (đọc từ luồng UDP — chỉ đọc số nguyên, không cần khoá).</summary>
    public int PlayerCount { get; private set; }
    public event Action<string>? Log;

    /// <summary>Phòng có mật khẩu: khách (trừ chủ phòng) phải trả lời đúng challenge mới được vào — xem Shared/RoomAuth.cs. null = phòng mở.
    /// Giữ tên người chơi trong lúc chờ trả lời.</summary>
    private readonly RoomGate<string>? _gate;
    public bool Locked => _gate is not null;

    public GameRoom(string roomName, int minPlayers = 5, string? password = null)
    {
        RoomName = roomName;
        _gate = RoomGate<string>.Create(password, Wire.Protocol);
        MinPlayers = Math.Clamp(minPlayers, 1, MaxPlayers);
        _thread = new Thread(Run) { IsBackground = true, Name = "GameRoom tick" };
        _thread.Start();
    }

    public void Enqueue(FramedConnection conn, byte[]? data) => _inbox.Enqueue((conn, data));

    /// <summary>Nạp bộ câu hỏi (gọi từ luồng UI của chủ phòng); áp dụng ở tick kế tiếp.</summary>
    public void SetBank(QuestionBank bank) => _pending.Enqueue(() => { Bank = bank; BroadcastLobby(); });

    private readonly ConcurrentQueue<Action> _pending = new();

    private void Run()
    {
        var next = _clock.Elapsed.TotalSeconds;
        while (!_stop)
        {
            try
            {
                _now = _clock.Elapsed.TotalSeconds;
                while (_pending.TryDequeue(out var a)) a();
                while (_inbox.TryDequeue(out var item)) HandleRaw(item.Conn, item.Data);
                Step();
            }
            catch (Exception ex) { Log?.Invoke("Lỗi tick: " + ex); }
            next += Tick;
            var wait = next - _clock.Elapsed.TotalSeconds;
            if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
            else if (wait < -1) next = _clock.Elapsed.TotalSeconds;   // tụt quá xa (máy treo) thì bỏ, không chạy bù
        }
    }

    public void Dispose()
    {
        _stop = true;
        foreach (var p in _players.ToList()) p.Conn?.Close("Chủ phòng đã đóng phòng");
    }

    // ───────────────────────── Nhận tin ─────────────────────────

    private void HandleRaw(FramedConnection conn, byte[]? data)
    {
        _byConn.TryGetValue(conn, out var p);
        if (data is null)
        {
            if (p is not null) Disconnected(p);
            else _gate?.Drop(conn);
            return;
        }
        JsonElement m;
        try { m = JsonDocument.Parse(data).RootElement; }
        catch { return; }
        var t = Str(m, "t");
        if (p is null)
        {
            if (t == "hello") Hello(conn, m);
            else if (t == "auth") Auth(conn, m);
            return;
        }
        try { Handle(p, t, m); }
        catch (Exception ex) { Log?.Invoke($"Lỗi xử lý '{t}' của {p.Name}: {ex.Message}"); }
    }

    private void Hello(FramedConnection conn, JsonElement m)
    {
        if (Str(m, "proto") != Wire.Protocol) { conn.Send(new { t = "reject", reason = "Khác phiên bản game — cập nhật Bcode để chơi cùng." }); conn.Close("proto"); return; }
        if (Phase != Phase.Lobby) { conn.Send(new { t = "reject", reason = "Phòng đang chơi ván khác — chờ hết ván rồi vào lại." }); conn.Close("busy"); return; }
        if (_players.Count >= MaxPlayers) { conn.Send(new { t = "reject", reason = "Phòng đã đủ 8 người." }); conn.Close("full"); return; }
        if (_gate is not null && _gate.IsPending(conn)) return;   // đang chờ trả lời mật khẩu: hello lặp lại không cấp nonce mới
        var name = CleanName(Str(m, "name"));
        var isHost = Str(m, "token") == HostToken;
        if (_gate is not null && !isHost)
        {
            if (!RoomAuth.Supports(Str(m, "auth"))) { conn.Send(new { t = "reject", reason = RoomAuth.OldClientText }); _ = Task.Delay(300).ContinueWith(_ => conn.Close("old")); return; }
            _gate.Challenge(conn, conn.Send, conn.Close, RoomName, name);
            return;
        }
        Admit(conn, name, isHost);
    }

    /// <summary>Khách trả lời challenge mật khẩu (luồng tick).</summary>
    private void Auth(FramedConnection conn, JsonElement m)
    {
        if (_gate is null) return;
        switch (_gate.Answer(conn, Str(m, "proof"), conn.Send, RoomName, out var name))
        {
            case GateResult.Ok:
                // trong lúc gõ mật khẩu phòng có thể đã vào ván / đủ người
                if (Phase != Phase.Lobby) { conn.Send(new { t = "reject", reason = "Phòng đang chơi ván khác — chờ hết ván rồi vào lại." }); conn.Close("busy"); return; }
                if (_players.Count >= MaxPlayers) { conn.Send(new { t = "reject", reason = "Phòng đã đủ 8 người." }); conn.Close("full"); return; }
                Admit(conn, name!, false);
                break;
            case GateResult.TooMany:
                conn.Send(new { t = "reject", reason = $"Sai mật khẩu {RoomGate<string>.MaxTries} lần — đã bị ngắt. Hỏi lại chủ phòng rồi vào lại." });
                _ = Task.Delay(300).ContinueWith(_ => conn.Close("badpass"));
                break;
        }
    }

    private void Admit(FramedConnection conn, string name, bool isHost)
    {
        var baseName = name; var k = 2;
        while (_players.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) name = $"{baseName} {k++}";
        var used = _players.Select(x => x.Color).ToHashSet();
        var p = new Player
        {
            Id = _nextId++, Name = name, Conn = conn,
            Color = Enumerable.Range(0, MaxPlayers).First(c => !used.Contains(c)),
            IsHost = isHost,
        };
        _players.Add(p);
        _byConn[conn] = p;
        PlayerCount = _players.Count;
        p.Send(new { t = "welcome", id = p.Id, host = p.IsHost, room = RoomName, name = p.Name });
        BroadcastLobby();
    }

    private static string CleanName(string? s)
    {
        s = new string((s ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (s.Length == 0) s = "Ẩn danh";
        return s.Length > 18 ? s[..18] : s;
    }

    private void Disconnected(Player p)
    {
        _byConn.Remove(p.Conn!);
        p.Conn = null;
        if (Phase == Phase.Lobby)
        {
            _players.Remove(p);
            PlayerCount = _players.Count;
            BroadcastLobby();
            return;
        }
        p.Left = true;
        if (p.Alive)
        {
            p.Alive = false;
            p.Q = null; p.Msg = null; p.Act = null;
            Feed($"🚪 {p.Name} đã rời phòng.");
            if (Phase == Phase.Meeting) CheckAllVoted();
            CheckWin();
        }
        if (_players.All(x => x.Left)) BackToLobby();
    }

    private void Handle(Player p, string? t, JsonElement m)
    {
        switch (t)
        {
            case "input": Input(p, m); break;
            case "chat": Chat(p, Str(m, "text")); break;
            case "settings" when p.IsHost && Phase == Phase.Lobby: ApplySettings(m); break;
            case "start" when p.IsHost && Phase == Phase.Lobby: StartGame(p); break;
            case "lobby" when p.IsHost && Phase == Phase.Over: BackToLobby(); break;
            case "kick" when p.IsHost && Phase == Phase.Lobby: Kick(Int(m, "id")); break;
            case "task": OpenTask(p); break;
            case "answer": Answer(p, Str(m, "token"), m); break;
            case "closeq": CloseQuestion(p); break;
            case "kill": Kill(p, Int(m, "target")); break;
            case "report": Report(p); break;
            case "emergency": Emergency(p); break;
            case "plant": StartAct(p, "dev"); break;
            case "check": StartAct(p, "test"); break;
            case "handover": Handover(p); break;
            case "bhreport": BhReport(p); break;
            case "bhreject": BhReject(p); break;
            case "vote": CastVote(p, m.TryGetProperty("target", out var tg) && tg.ValueKind == JsonValueKind.Number ? tg.GetInt32() : -1); break;
        }
    }

    private void Kick(int id)
    {
        var p = _players.FirstOrDefault(x => x.Id == id && !x.IsHost);
        if (p?.Conn is null) return;
        p.Send(new { t = "reject", reason = "Chủ phòng đã mời bạn ra khỏi phòng." });
        p.Conn.Close("kick");
    }

    private void ApplySettings(JsonElement m)
    {
        if (m.TryGetProperty("s", out var s))
        {
            int Get(string n, int d) => s.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : d;
            Settings.Minutes = Get("minutes", Settings.Minutes);
            Settings.TasksPer = Get("tasksPer", Settings.TasksPer);
            Settings.QPerTask = Get("qPerTask", Settings.QPerTask);
            Settings.Discuss = Get("discuss", Settings.Discuss);
            Settings.Vote = Get("vote", Settings.Vote);
            Settings.KillCd = Get("killCd", Settings.KillCd);
            Settings.BugLimit = Get("bugLimit", Settings.BugLimit);
            Settings.Clamp();
        }
        BroadcastLobby();
    }

    private void Input(Player p, JsonElement m)
    {
        var dx = (float)(m.TryGetProperty("dx", out var a) && a.TryGetDouble(out var x) ? x : 0);
        var dy = (float)(m.TryGetProperty("dy", out var b) && b.TryGetDouble(out var y) ? y : 0);
        if (!float.IsFinite(dx) || !float.IsFinite(dy)) { dx = dy = 0; }
        var len = MathF.Sqrt(dx * dx + dy * dy);
        if (len > 1) { dx /= len; dy /= len; }
        p.Dx = dx; p.Dy = dy;
    }

    private void Chat(Player p, string? text)
    {
        text = new string((text ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length == 0 || _now < p.ChatReadyAt) return;
        if (text.Length > 200) text = text[..200];
        p.ChatReadyAt = _now + 0.6;
        if (Phase is Phase.Lobby or Phase.Over)
        {
            foreach (var q in _players) q.Send(new { t = "chat", from = p.Id, name = p.Name, color = p.Color, text });
            return;
        }
        // Đang chơi: người sống chỉ chat trong cuộc họp; "ma" chat riêng với nhau lúc nào cũng được.
        if (p.Alive && Phase != Phase.Meeting) return;
        foreach (var q in _players)
            if (p.Alive || !q.Alive) q.Send(new { t = "chat", from = p.Id, name = p.Name, color = p.Color, text, ghost = !p.Alive });
    }

    // ───────────────────────── Sảnh ─────────────────────────

    private void BroadcastLobby()
    {
        var msg = new
        {
            t = "lobby", room = RoomName, min = MinPlayers, max = MaxPlayers, phase = Phase.ToString().ToLowerInvariant(),
            players = _players.Select(x => new { id = x.Id, name = x.Name, color = x.Color, host = x.IsHost }),
            settings = Settings,
            bank = Bank is null ? null : new
            {
                file = Path.GetFileName(Bank.FilePath), total = Bank.Total, multi = Bank.MultiCount,
                modules = Bank.Modules.Select(md => new { name = md.Sheet, count = md.QuestionCount, biz = md.Businesses.Count }),
            },
        };
        foreach (var p in _players) p.Send(msg);
    }

    private void BackToLobby()
    {
        Phase = Phase.Lobby;
        _players.RemoveAll(x => x.Left || x.Conn is null);
        foreach (var p in _players) ResetPlayer(p);
        PlayerCount = _players.Count;
        _bodies.Clear(); _bugs.Clear(); _review = null;
        BroadcastLobby();
    }

    private static void ResetPlayer(Player p)
    {
        p.Alive = true; p.Pm = false; p.PmUsed = false; p.Act = null; p.Q = null; p.Msg = null; p.Vote = null;
        p.Tasks.Clear(); p.Used.Clear(); p.Emergencies = 1; p.BhReports = 2; p.Dx = p.Dy = 0; p.LockUntil = 0;
    }

    private void StartGame(Player host)
    {
        if (Bank is null) { host.Send(new { t = "toast", text = "Chưa chọn file câu hỏi Excel." }); return; }
        var live = _players.Where(x => x.Conn is not null).ToList();
        if (live.Count < MinPlayers) { host.Send(new { t = "toast", text = $"Cần ít nhất {MinPlayers} người (đang có {live.Count})." }); return; }
        Settings.Clamp();
        _moduleNames = Bank.Modules.Take(GameMap.ModuleRooms).Select(md => md.Sheet).ToList();
        _mapDto = Map.ToDto(_moduleNames);
        _bodies.Clear(); _bugs.Clear(); _review = null; _taskSeq = 0; _bugSeq = 0;

        var roles = Roles.Table(live.Count).OrderBy(_ => _rng.Next()).ToList();
        for (var i = 0; i < live.Count; i++)
        {
            var p = live[i];
            ResetPlayer(p);
            p.Role = roles[i];
            (p.X, p.Y) = Map.Seats[i % Map.Seats.Count];
            p.KillReadyAt = _now + 15;
            p.PlantReadyAt = _now + 20;
            p.BhReadyAt = _now + 40;
            p.NextMsgAt = _now + 40 + _rng.Next(30);
        }
        var tks = live.Where(x => x.Role == Role.TrienKhai).ToList();
        if (tks.Count > 0) tks[_rng.Next(tks.Count)].Pm = true;
        foreach (var p in live)
            if (p.Role != Role.Tester)
                for (var k = 0; k < Settings.TasksPer; k++) AddTask(p, fake: !p.IsGood);

        Phase = Phase.Play;
        _projectLeft = Settings.Minutes * 60;
        _emergencyReadyAt = _now + 20;
        _pmReadyAt = _now + 30;
        _handoverReadyAt = 0;
        var roster = live.Select(x => new { id = x.Id, name = x.Name, color = x.Color }).ToList();
        foreach (var p in live)
        {
            var mates = p.Role switch
            {
                Role.LapTrinh => live.Where(x => x != p && x.Role == Role.LapTrinh).Select(x => x.Id).ToList(),
                Role.BaoHanh => live.Where(x => x.Role == Role.LapTrinh).Select(x => x.Id).ToList(),
                _ => new List<int>(),
            };
            p.Send(new
            {
                t = "start", you = p.Id, role = Roles.Code(p.Role), pm = p.Pm, mates, players = roster, map = _mapDto,
                settings = Settings, tasks = TaskDto(p), counts = Roles.Table(live.Count).GroupBy(Roles.Code).ToDictionary(g => g.Key, g => g.Count()),
            });
        }
        Log?.Invoke($"Bắt đầu ván: {live.Count} người.");
    }

    /// <summary>Thêm 1 nhiệm vụ ở phân hệ (sheet) chưa có trong danh sách của người đó nếu được.</summary>
    private GTask? AddTask(Player p, bool fake)
    {
        if (Bank is null || _moduleNames.Count == 0) return null;
        var mods = Enumerable.Range(0, _moduleNames.Count).Where(i => Bank.Modules[i].QuestionCount > 0).ToList();
        if (mods.Count == 0) return null;
        var free = mods.Where(i => !p.Tasks.Any(t => t.Module == i && !t.Done)).ToList();
        var mod = (free.Count > 0 ? free : mods)[_rng.Next((free.Count > 0 ? free : mods).Count)];
        var bizs = Bank.Modules[mod].Businesses.Where(b => b.Questions.Count > 0).ToList();
        var biz = bizs[_rng.Next(bizs.Count)];
        var t = new GTask { Id = ++_taskSeq, Module = mod, Biz = biz, Need = Math.Min(Settings.QPerTask, biz.Questions.Count), Fake = fake };
        p.Tasks.Add(t);
        return t;
    }

    private object TaskDto(Player p) => p.Tasks.Select(t => new
    {
        id = t.Id, module = t.Module, room = RoomOfModule(t.Module), name = t.Biz.Name, code = t.Biz.Code,
        need = t.Need, got = t.Got, done = t.Done, hint = t.Hints > 0,
    }).ToList();

    private int RoomOfModule(int module) => Map.Rooms.First(r => r.Kind == "module" && r.Module == module).Id;

    // ───────────────────────── Tiện ích ─────────────────────────

    private static string? Str(JsonElement m, string n) => m.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int Int(JsonElement m, string n) => m.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : -1;

    private void Feed(string text, string kind = "info")
    {
        foreach (var p in _players) p.Send(new { t = "feed", text, kind });
    }

    private static void Toast(Player p, string text) => p.Send(new { t = "toast", text });

    private static float Dist(Player a, float x, float y) => MathF.Sqrt((a.X - x) * (a.X - x) + (a.Y - y) * (a.Y - y));

    private Station? NearStation(Player p, string kind, float range)
    {
        Station? best = null; var bd = range;
        foreach (var s in Map.Stations)
        {
            if (s.Kind != kind) continue;
            var d = Dist(p, s.X, s.Y);
            if (d <= bd) { bd = d; best = s; }
        }
        return best;
    }

    private IEnumerable<Player> InGame => _players.Where(x => !x.Left);
    private int DevAlive => _players.Count(x => x.Alive && x.Role == Role.LapTrinh);
    private int GoodAlive => _players.Count(x => x.Alive && x.IsGood);
    private int OpenBugs => _bugs.Count(b => b.Open);

    private (int Done, int Total) Progress()
    {
        int done = 0, total = 0;
        foreach (var p in _players)
            if (!p.Left)   // người rời phòng: bỏ nhiệm vụ của họ khỏi tiến độ (không ai làm thay được)
            foreach (var t in p.Tasks)
                if (!t.Fake) { total++; if (t.Done) done++; }
        return (done, total);
    }
}
