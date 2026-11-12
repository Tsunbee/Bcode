using System.Security.Cryptography;
using System.Text.Json;
using Bcode.Game.Data;

namespace Bcode.Game.Logic;

/// <summary>Luật chơi: đi lại, nhiệm vụ câu hỏi, giết / Trưởng dự án, gài lỗi / kiểm lỗi, Bảo hành, họp + vote, bàn giao, điều kiện thắng.</summary>
public sealed partial class GameRoom
{
    private const double PlantTime = 4, PlantCd = 40, TestTimeTester = 5, TestTimeOther = 9, WrongLock = 3, MsgLimit = 30, ReviewTime = 10, ResultTime = 6;

    // ───────────────────────── Tick ─────────────────────────

    private void Step()
    {
        switch (Phase)
        {
            case Phase.Play: StepPlay(); break;
            case Phase.Meeting when _now >= _voteEnds: EndVote(); break;
            case Phase.Result when _now >= _resultEnds: EndResult(); break;
        }
        if (Phase is Phase.Play or Phase.Meeting or Phase.Result)
            foreach (var p in _players)
                if (p.Conn is not null) SendSnapshot(p);
    }

    private void StepPlay()
    {
        _projectLeft -= Tick;
        foreach (var p in _players)
        {
            if (p.Left) continue;
            if (p.Act is not null && (p.Dx != 0 || p.Dy != 0)) p.Act = null;   // đi là bỏ dở
            if (p.Act is not null && _now >= p.Act.Until) CompleteAct(p);
            if (p.Act is null && p.Q is null && (p.Dx != 0 || p.Dy != 0))
            {
                var step = Speed * (float)Tick;
                if (p.Alive) (p.X, p.Y) = Map.Move(p.X, p.Y, p.Dx * step, p.Dy * step);
                else
                {   // "ma" đi xuyên vách, chỉ giữ trong bản đồ
                    p.X = Math.Clamp(p.X + p.Dx * step * 1.15f, 20, GameMap.Width - 20);
                    p.Y = Math.Clamp(p.Y + p.Dy * step * 1.15f, 20, GameMap.Height - 20);
                }
            }
            StepCustomerMessage(p);
        }
        if (_review is { } rv && _now >= rv.Ends) EndReview();
        if (Phase == Phase.Play && _projectLeft <= 0) CheckWin();
    }

    private void SendSnapshot(Player v)
    {
        var ps = new List<int[]>();
        foreach (var o in _players)
        {
            if (o == v || o.Left) continue;
            if (!o.Alive && v.Alive) continue;                                   // người sống không thấy "ma"
            if (v.Alive && Dist(v, o.X, o.Y) > Vision) continue;                 // ngoài tầm nhìn: không gửi
            var flags = (o.Alive ? 0 : 1) | (o.Act?.Kind == "dev" ? 2 : 0) | (o.Act?.Kind == "test" ? 4 : 0);
            ps.Add(new[] { o.Id, (int)o.X, (int)o.Y, flags });
        }
        var bodies = _bodies.Where(b => !v.Alive || Dist(v, b.X, b.Y) <= Vision).Select(b => new[] { b.Id, (int)b.X, (int)b.Y }).ToList();
        var (done, total) = Progress();
        var cd = new Dictionary<string, double>();
        if (v.Alive)
        {
            cd["e"] = v.Emergencies;
            cd["ec"] = Math.Max(0, _emergencyReadyAt - _now);
            if (v.Role == Role.LapTrinh) { cd["k"] = Math.Max(0, v.KillReadyAt - _now); cd["pl"] = Math.Max(0, v.PlantReadyAt - _now); }
            if (v.Pm) cd["pk"] = v.PmUsed ? -1 : Math.Max(0, _pmReadyAt - _now);
            if (v.Role == Role.BaoHanh) { cd["bh"] = v.BhReports; cd["bhc"] = Math.Max(0, v.BhReadyAt - _now); }
            if (CanCheck(v) && NearStation(v, "test", UseRange * 1.6f) is not null) cd["bugs"] = OpenBugs;
            if (_handoverReadyAt > _now) cd["hc"] = _handoverReadyAt - _now;
        }
        v.Send(new
        {
            t = "s",
            ph = Phase switch { Phase.Play => "play", Phase.Meeting => "meeting", Phase.Result => "result", _ => "over" },
            clock = Math.Max(0, (int)Math.Ceiling(_projectLeft)),
            prog = new[] { done, total },
            me = new
            {
                x = (int)v.X, y = (int)v.Y, alive = v.Alive, q = v.Q is not null,
                act = v.Act?.Kind, actLeft = v.Act is null ? 0 : Math.Round(v.Act.Until - _now, 2), actTotal = v.Act is null ? 0 : ActDuration(v, v.Act.Kind),
            },
            ps, bodies, cd,
            rv = _review is { } r ? Math.Max(0, Math.Round(r.Ends - _now, 1)) : (double?)null,
            vt = Phase == Phase.Meeting ? new[] { Math.Max(0, Math.Round(_discussEnds - _now, 1)), Math.Max(0, Math.Round(_voteEnds - _now, 1)) } : null,
        });
    }

    // ───────────────────────── Nhiệm vụ & câu hỏi ─────────────────────────

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8));

    private Question PickQuestion(Player p, IReadOnlyList<Question> pool)
    {
        var fresh = pool.Where(q => !p.Used.Contains(q)).ToList();
        if (fresh.Count == 0) { foreach (var q in pool) p.Used.Remove(q); fresh = pool.ToList(); }
        var pick = fresh[_rng.Next(fresh.Count)];
        p.Used.Add(pick);
        return pick;
    }

    private void SendQuestion(Player p, OpenQuestion oq, string kind, string title, string? sub, double? limit, bool hint)
    {
        p.Send(new
        {
            t = "q", token = oq.Token, kind, title, sub, text = oq.Q.Text, menu = oq.Q.Menu, limit, hint,
            opts = oq.Shown.Select(i => new { i, text = oq.Q.Options[i] }),
        });
    }

    private void OpenTask(Player p)
    {
        if (Phase != Phase.Play || p.Q is not null || p.Act is not null) return;
        var st = NearStation(p, "task", UseRange);
        if (st is null) return;
        if (_now < p.LockUntil) { Toast(p, $"Bình tĩnh đọc lại tài liệu… thử lại sau {Math.Ceiling(p.LockUntil - _now)} giây."); return; }
        var task = p.Tasks.FirstOrDefault(t => t.Module == st.Num && !t.Done);
        if (task is null)
        {
            Toast(p, p.Role == Role.Tester ? "Tester không viết khảo sát — việc của bạn ở Khu Tester." : "Bạn không có nhiệm vụ đang mở ở phân hệ này.");
            return;
        }
        var q = PickQuestion(p, task.Biz.Questions);
        var shown = Enumerable.Range(0, q.Options.Length).ToList();
        var hint = false;
        if (task.Hints > 0)
        {
            task.Hints--;
            hint = true;
            var wrong = shown.Where(i => !q.Correct[i]).OrderBy(_ => _rng.Next()).ToList();
            var remove = Math.Min(wrong.Count - 1, (wrong.Count + 1) / 2);
            foreach (var i in wrong.Take(Math.Max(0, remove))) shown.Remove(i);
        }
        p.Q = new OpenQuestion { Token = NewToken(), Q = q, Shown = shown.ToArray(), Task = task };
        p.Dx = p.Dy = 0;
        SendQuestion(p, p.Q, "task", $"{_moduleNames[task.Module]} — {task.Biz.Name}", $"Câu {task.Got + 1}/{task.Need}", null, hint);
    }

    private void Answer(Player p, string? token, JsonElement m)
    {
        if (token is null) return;
        var picks = new HashSet<int>();
        if (m.TryGetProperty("picks", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
                if (e.TryGetInt32(out var i)) picks.Add(i);

        OpenQuestion? oq = p.Q?.Token == token ? p.Q : p.Msg?.Token == token ? p.Msg : null;
        if (oq is null) return;
        picks.IntersectWith(oq.Shown);
        var correct = Enumerable.Range(0, oq.Q.Correct.Length).Where(i => oq.Q.Correct[i]).ToArray();
        var ok = picks.SetEquals(correct);

        if (oq == p.Q)
        {
            p.Q = null;
            var t = oq.Task!;
            if (ok && !t.Done)
            {
                t.Got++;
                if (t.Got >= t.Need) t.Done = true;
            }
            else if (!ok) p.LockUntil = _now + WrongLock;
            p.Send(new { t = "qres", token, ok, correct, done = t.Done, got = t.Got, need = t.Need, more = !t.Done && Phase == Phase.Play });
            p.Send(new { t = "tasks", tasks = TaskDto(p) });
            if (ok && t.Done) Toast(p, $"✅ Xong tài liệu khảo sát “{t.Biz.Name}”.");
        }
        else
        {
            p.Msg = null;
            p.NextMsgAt = _now + 50 + _rng.Next(40);
            string text;
            if (ok)
            {
                var open = p.Tasks.Where(x => !x.Done && !x.Fake).ToList();
                if (open.Count > 0)
                {
                    var t = open[_rng.Next(open.Count)];
                    t.Hints++;
                    text = $"Khách hàng hài lòng 👍 — gợi ý cho nhiệm vụ “{t.Biz.Name}”: lần mở tới sẽ loại bớt phương án sai.";
                    p.Send(new { t = "tasks", tasks = TaskDto(p) });
                }
                else text = "Khách hàng hài lòng 👍";
            }
            else text = "Khách hàng chưa hài lòng với câu trả lời.";
            p.Send(new { t = "qres", token, ok, correct, msg = text });
        }
    }

    private void CloseQuestion(Player p)
    {
        if (p.Q is null) return;
        p.Q = null;
        p.LockUntil = Math.Max(p.LockUntil, _now + 1);
    }

    private void StepCustomerMessage(Player p)
    {
        if (p.Role != Role.TrienKhai || !p.Alive || Bank is null) return;
        if (p.Msg is not null)
        {
            if (_now >= p.Msg.Expires)
            {
                p.Send(new { t = "qclose", token = p.Msg.Token, text = "Khách hàng đã rời cuộc trò chuyện." });
                p.Msg = null;
                p.NextMsgAt = _now + 50 + _rng.Next(40);
            }
            return;
        }
        if (_now < p.NextMsgAt) return;
        if (p.Q is not null || p.Act is not null) { p.NextMsgAt = _now + 5; return; }
        var mods = Bank.Modules.Where(md => md.QuestionCount > 0).ToList();
        var md = mods[_rng.Next(mods.Count)];
        var q = PickQuestion(p, md.Businesses.SelectMany(b => b.Questions).ToList());
        p.Msg = new OpenQuestion { Token = NewToken(), Q = q, Shown = Enumerable.Range(0, q.Options.Length).ToArray(), Expires = _now + MsgLimit };
        SendQuestion(p, p.Msg, "msg", "💬 Tin nhắn khách hàng", md.Sheet, MsgLimit, false);
    }

    // ───────────────────────── Giết / report / họp khẩn ─────────────────────────

    private void Kill(Player p, int targetId)
    {
        if (Phase != Phase.Play || !p.Alive) return;
        var v = _players.FirstOrDefault(x => x.Id == targetId && x.Alive && !x.Left && x != p);
        if (v is null || Dist(p, v.X, v.Y) > KillRange) return;
        if (p.Role == Role.LapTrinh)
        {
            if (v.Role == Role.LapTrinh || _now < p.KillReadyAt) return;
            p.KillReadyAt = _now + Settings.KillCd;
            Die(v, "Bạn đã bị hạ gục. Giờ bạn là \"ma\": vẫn đi lại và làm nhiệm vụ được (nếu là Triển khai), chỉ chat với ma khác.");
        }
        else if (p.Pm && !p.PmUsed)
        {
            if (_now < _pmReadyAt) return;
            p.PmUsed = true;
            if (v.IsGood)
            {
                Die(p, "Bạn đã xử lý NHẦM người phe mình — Trưởng dự án bị loại.");
            }
            else
            {
                Die(v, "Trưởng dự án đã xử lý bạn.");
                Toast(p, "🎯 Xử lý đúng người phe xấu!");
            }
        }
        else return;
        CheckWin();
    }

    private void Die(Player v, string why)
    {
        v.Alive = false;
        v.Act = null;
        if (v.Q is not null) { v.Send(new { t = "qclose", token = v.Q.Token }); v.Q = null; }
        if (v.Msg is not null) { v.Send(new { t = "qclose", token = v.Msg.Token }); v.Msg = null; }
        _bodies.Add(new Body(v.Id, v.X, v.Y));
        v.Send(new { t = "dead", text = why });
    }

    private void Report(Player p)
    {
        if (Phase != Phase.Play || !p.Alive) return;
        var b = _bodies.Where(x => Dist(p, x.X, x.Y) <= ReportRange).OrderBy(x => Dist(p, x.X, x.Y)).FirstOrDefault();
        if (b is null) return;
        StartMeeting(p, "body", _players.First(x => x.Id == b.Id));
    }

    private void Emergency(Player p)
    {
        if (Phase != Phase.Play || !p.Alive || NearStation(p, "button", ButtonRange) is null) return;
        if (p.Emergencies <= 0) { Toast(p, "Bạn đã dùng hết lượt họp khẩn."); return; }
        if (_now < _emergencyReadyAt) { Toast(p, $"Nút họp khẩn đang nghỉ — còn {Math.Ceiling(_emergencyReadyAt - _now)} giây."); return; }
        p.Emergencies--;
        StartMeeting(p, "emergency", null);
    }

    // ───────────────────────── Gài lỗi / kiểm lỗi ─────────────────────────

    private bool CanCheck(Player p) =>
        p.Alive && (p.Role == Role.Tester || (p.IsGood && !_players.Any(x => x.Role == Role.Tester && x.Alive && !x.Left)));

    private double ActDuration(Player p, string kind) => kind == "dev" ? PlantTime : p.Role == Role.Tester ? TestTimeTester : TestTimeOther;

    private void StartAct(Player p, string kind)
    {
        if (Phase != Phase.Play || !p.Alive || p.Q is not null || p.Act is not null) return;
        var st = NearStation(p, kind, UseRange);
        if (st is null) return;
        if (kind == "dev" && p.Role == Role.LapTrinh && _now < p.PlantReadyAt)
        {
            Toast(p, $"Vừa gài xong, đang bị để ý — chờ {Math.Ceiling(p.PlantReadyAt - _now)} giây nữa.");
            return;
        }
        if (kind == "test" && !CanCheck(p)) { Toast(p, "Máy test: cần tài khoản Tester."); return; }
        p.Dx = p.Dy = 0;
        p.Act = new Act { Kind = kind, Station = st, Until = _now + ActDuration(p, kind) };
    }

    private void CompleteAct(Player p)
    {
        var act = p.Act!;
        p.Act = null;
        if (act.Kind == "dev")
        {
            if (p.Role == Role.LapTrinh)
            {
                p.PlantReadyAt = _now + PlantCd;
                CreateBug(p, act.Station.Num);
                Toast(p, $"🐞 Đã gài lỗi từ máy Dev số {act.Station.Num}.");
            }
            else Toast(p, "Bạn xem code một lúc… không thấy gì lạ.");
            return;
        }
        var bug = _bugs.Where(b => b.Open).OrderBy(b => b.At).FirstOrDefault();
        if (bug is null) { Toast(p, "Không phát hiện lỗi nào đang mở."); return; }
        bug.Open = false;
        Feed($"🔍 Bắt được 1 lỗi: gài từ máy Dev số {bug.Pc}, {Ago(_now - bug.At)}.", "clue");
        Toast(p, OpenBugs == 0 ? "Đã sạch lỗi đang mở." : $"Còn {OpenBugs} lỗi đang mở.");
    }

    /// <summary>"khoảng 2 phút trước" — làm tròn 30 giây cho mờ vừa đủ.</summary>
    private static string Ago(double secs)
    {
        if (secs < 45) return "chưa tới 1 phút trước";
        var half = Math.Round(secs / 30) / 2;
        return $"khoảng {half:0.#} phút trước";
    }

    private void CreateBug(Player by, int pc)
    {
        _bugs.Add(new Bug { Id = ++_bugSeq, Pc = pc, At = _now, By = by.Id });
        var done = InGame.SelectMany(o => o.Tasks.Where(t => t.Done && !t.Fake).Select(t => (Owner: o, Task: t))).ToList();
        if (done.Count > 0 && _rng.Next(2) == 0)
        {
            var (owner, t) = done[_rng.Next(done.Count)];
            t.Done = false; t.Got = 0;
            Toast(owner, $"⚠ Lỗi phát sinh: tài liệu “{t.Biz.Name}” ({_moduleNames[t.Module]}) phải làm lại.");
            owner.Send(new { t = "tasks", tasks = TaskDto(owner) });
        }
        else
        {
            var tks = InGame.Where(o => o.Role == Role.TrienKhai).OrderByDescending(o => o.Alive).ToList();
            var alive = tks.Where(o => o.Alive).ToList();
            var owner = (alive.Count > 0 ? alive : tks).OrderBy(_ => _rng.Next()).FirstOrDefault();
            if (owner is not null && AddTask(owner, fake: false) is { } t)
            {
                Toast(owner, $"📨 Khách hàng hỏi thêm: nhiệm vụ mới “{t.Biz.Name}” ở {_moduleNames[t.Module]}.");
                owner.Send(new { t = "tasks", tasks = TaskDto(owner) });
            }
        }
        CheckWin();
    }

    // ───────────────────────── Bảo hành & bàn giao ─────────────────────────

    private void BhReport(Player p)
    {
        if (Phase != Phase.Play || !p.Alive || p.Role != Role.BaoHanh) return;
        if (p.BhReports <= 0) { Toast(p, "Hết lượt report lỗi."); return; }
        if (_now < p.BhReadyAt) { Toast(p, $"Khách vừa được chăm sóc — chờ {Math.Ceiling(p.BhReadyAt - _now)} giây nữa."); return; }
        var room = Map.RoomAt(p.X, p.Y);
        if (room is null || room.Kind != "module") { Toast(p, "Phải đứng trong một phòng phân hệ để report lỗi."); return; }
        var done = InGame.SelectMany(o => o.Tasks.Where(t => t.Done && !t.Fake && t.Module == room.Module).Select(t => (Owner: o, Task: t))).ToList();
        if (done.Count == 0) { Toast(p, "Phân hệ này chưa có tài liệu nào xong để report."); return; }
        var (owner, task) = done[_rng.Next(done.Count)];
        task.Done = false; task.Got = 0;
        p.BhReports--;
        p.BhReadyAt = _now + 30;
        owner.Send(new { t = "tasks", tasks = TaskDto(owner) });
        Feed($"📞 Bảo hành nhận report lỗi ở {_moduleNames[room.Module]}: tài liệu “{task.Biz.Name}” phải làm lại.", "bad");
        Toast(p, $"Đã report (còn {p.BhReports} lượt).");
    }

    private void Handover(Player p)
    {
        if (Phase != Phase.Play || !p.Alive || NearStation(p, "button", ButtonRange) is null) return;
        var (done, total) = Progress();
        if (total == 0 || done < total) { Toast(p, $"Chưa xong hết tài liệu ({done}/{total}) — chưa bàn giao được."); return; }
        if (_review is not null) return;
        if (_now < _handoverReadyAt) { Toast(p, $"Khách hàng vừa từ chối — chờ {Math.Ceiling(_handoverReadyAt - _now)} giây."); return; }
        if (OpenBugs > 0)
        {
            _projectLeft -= 30;
            _handoverReadyAt = _now + 15;
            var noTester = !_players.Any(x => x.Role == Role.Tester && x.Alive && !x.Left);
            Feed($"📋 Khách hàng kiểm tra bàn giao: hệ thống còn {OpenBugs} lỗi chưa xử lý — CHƯA nghiệm thu (trễ thêm 30 giây)." +
                 (noTester ? " Không còn Tester: Triển khai phải tự kiểm lỗi ở Khu Tester." : ""), "bad");
            CheckWin();
            return;
        }
        _review = (_now + ReviewTime, false);
        Feed($"📋 {p.Name} gửi bàn giao — khách hàng đang nghiệm thu ({ReviewTime:0} giây)…", "info");
        foreach (var b in _players.Where(x => x.Role == Role.BaoHanh && x.Alive && x.BhReports > 0))
            b.Send(new { t = "bhprompt", secs = ReviewTime, left = b.BhReports });
    }

    private void BhReject(Player p)
    {
        if (_review is not { Rejected: false } rv || !p.Alive || p.Role != Role.BaoHanh || p.BhReports <= 0) return;
        _review = (rv.Ends, true);
        p.BhReports--;
        Toast(p, $"Đã từ chối nghiệm thu (còn {p.BhReports} lượt report).");
    }

    private void EndReview()
    {
        var rejected = _review!.Value.Rejected;
        _review = null;
        if (!rejected) { GameOver("good", "Bàn giao thành công — khách hàng ký biên bản nghiệm thu!"); return; }
        _handoverReadyAt = _now + 10;
        var done = InGame.SelectMany(o => o.Tasks.Where(t => t.Done && !t.Fake).Select(t => (Owner: o, Task: t))).ToList();
        if (done.Count == 0) { Feed("📞 Khách hàng (bảo hành) chưa nghiệm thu.", "bad"); return; }
        var (owner, task) = done[_rng.Next(done.Count)];
        task.Done = false; task.Got = 0;
        owner.Send(new { t = "tasks", tasks = TaskDto(owner) });
        Feed($"📞 Khách hàng (bảo hành) CHƯA nghiệm thu: tài liệu “{task.Biz.Name}” ở {_moduleNames[task.Module]} phải làm lại.", "bad");
    }

    // ───────────────────────── Họp & vote ─────────────────────────

    private void StartMeeting(Player caller, string reason, Player? body)
    {
        Phase = Phase.Meeting;
        _discussEnds = _now + Settings.Discuss;
        _voteEnds = _discussEnds + Settings.Vote;
        _bodies.Clear();
        if (_review is not null) { _review = null; Feed("Nghiệm thu bị gián đoạn vì cuộc họp — gửi bàn giao lại sau.", "info"); }
        var seat = 0;
        foreach (var p in _players)
        {
            p.Act = null; p.Vote = null; p.Dx = p.Dy = 0;
            if (p.Q is not null) { p.Send(new { t = "qclose", token = p.Q.Token }); p.Q = null; }
            if (p.Msg is not null) { p.Send(new { t = "qclose", token = p.Msg.Token }); p.Msg = null; }
            if (p.Alive && !p.Left) { (p.X, p.Y) = Map.Seats[seat % Map.Seats.Count]; seat++; }
        }
        var msg = new
        {
            t = "meeting", by = caller.Id, byName = caller.Name, reason, body = body?.Id, bodyName = body?.Name,
            discuss = Settings.Discuss, vote = Settings.Vote,
            alive = _players.Where(x => x.Alive && !x.Left).Select(x => x.Id),
        };
        foreach (var p in _players) p.Send(msg);
    }

    private void CastVote(Player p, int target)
    {
        if (Phase != Phase.Meeting || !p.Alive || p.Left || p.Vote is not null) return;
        if (_now < _discussEnds) { Toast(p, "Chưa hết giờ thảo luận."); return; }
        if (target != -1 && !_players.Any(x => x.Id == target && x.Alive && !x.Left)) return;
        p.Vote = target;
        foreach (var q in _players) q.Send(new { t = "voted", id = p.Id });
        CheckAllVoted();
    }

    private void CheckAllVoted()
    {
        if (Phase == Phase.Meeting && _players.Where(x => x.Alive && !x.Left).All(x => x.Vote is not null)) EndVote();
    }

    private void EndVote()
    {
        var voters = _players.Where(x => x.Alive && !x.Left && x.Vote is not null).ToList();
        var skip = voters.Count(x => x.Vote == -1);
        var tally = voters.Where(x => x.Vote != -1).GroupBy(x => x.Vote!.Value).Select(g => (Id: g.Key, N: g.Count())).OrderByDescending(x => x.N).ToList();
        Player? out_ = null;
        if (tally.Count > 0 && tally[0].N > skip && (tally.Count == 1 || tally[1].N < tally[0].N))
            out_ = _players.First(x => x.Id == tally[0].Id);
        if (out_ is not null)
        {
            out_.Alive = false;
            out_.Send(new { t = "dead", text = "Bạn bị cả đội bỏ phiếu loại khỏi dự án. Giờ bạn là \"ma\"." });
        }
        Phase = Phase.Result;
        _resultEnds = _now + ResultTime;
        var msg = new
        {
            t = "result", ejected = out_?.Id, name = out_?.Name, dev = out_?.Role == Role.LapTrinh, skip,
            votes = voters.Select(x => new { from = x.Id, to = x.Vote }), secs = ResultTime,
        };
        foreach (var p in _players) p.Send(msg);
    }

    private void EndResult()
    {
        Phase = Phase.Play;
        foreach (var p in _players)
        {
            if (p.Role == Role.LapTrinh) p.KillReadyAt = _now + Settings.KillCd;
            p.NextMsgAt = Math.Max(p.NextMsgAt, _now + 20);
            p.Dx = p.Dy = 0;
        }
        _emergencyReadyAt = _now + 15;
        foreach (var p in _players) p.Send(new { t = "resume" });
        CheckWin();
    }

    // ───────────────────────── Thắng / thua ─────────────────────────

    private void CheckWin()
    {
        if (Phase is Phase.Lobby or Phase.Over) return;
        if (Phase == Phase.Result) return;   // chờ xem xong kết quả vote (EndResult gọi lại)
        if (DevAlive == 0) GameOver("good", "Đã loại hết Lập trình gài lỗi — dự án về đích an toàn!");
        else if (DevAlive >= GoodAlive) GameOver("bad", "Lập trình áp đảo — đội dự án không còn đủ người.");
        else if (OpenBugs >= Settings.BugLimit) GameOver("bad", $"Lỗi tồn đọng quá nhiều ({OpenBugs}) — khách hàng dừng hợp đồng.");
        else if (_projectLeft <= 0) GameOver("bad", "Trễ hạn dự án — hết giờ mà chưa bàn giao được.");
    }

    private void GameOver(string winner, string reason)
    {
        Phase = Phase.Over;
        _review = null;
        var msg = new
        {
            t = "over", winner, reason,
            roles = _players.Select(x => new { id = x.Id, name = x.Name, color = x.Color, role = Roles.Code(x.Role), pm = x.Pm, alive = x.Alive && !x.Left, left = x.Left }),
        };
        foreach (var p in _players) p.Send(msg);
        Log?.Invoke($"Hết ván: {winner} — {reason}");
    }
}
