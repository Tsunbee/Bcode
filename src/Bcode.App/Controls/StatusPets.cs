using System.Drawing.Drawing2D;
using Bcode.App.Models;
using Bcode.App.UI;

namespace Bcode.App.Controls;

/// <summary>
/// Thú cưng trên thanh trạng thái (ong, bướm, chim, bọ rùa, mèo, ốc sên) — bản native của Web/Shell/statuspets.js (giữ 2 bản giống nhau khi sửa:
/// cùng tên hàm, cùng con số). Gắn vào 1 control chủ: chủ gọi <see cref="Paint"/> trong OnPaint (hình hiện xuyên qua các label nền trong suốt);
/// bộ đếm giờ tự cập nhật + Invalidate vùng có thú. Toạ độ tính theo px thiết kế, nhân <c>_k</c> (mật độ giao diện) khi vẽ.
/// </summary>
public sealed partial class StatusPets : IDisposable   // phần thú cưng trong editor SQL Query: StatusPets.Editor.cs
{
    private static readonly string[] Order = { "snail", "ladybug", "cat", "fish", "bird", "butterfly", "bee", "firefly" };
    private static readonly Dictionary<string, (float W, float H)> Size = new()
    {
        ["bee"] = (18, 16), ["butterfly"] = (18, 16), ["bird"] = (20, 14), ["ladybug"] = (14, 10), ["cat"] = (24, 18), ["snail"] = (18, 12),
        ["fish"] = (18, 10), ["firefly"] = (10, 8),
    };
    private static readonly string[][] Bfly = { new[] { "#ff9a2e", "#ffcf6b" }, new[] { "#5aa9ff", "#b9dcff" }, new[] { "#ff6fae", "#ffc2dc" }, new[] { "#b07cff", "#dcc6ff" } };
    private static readonly string[][] BirdC = { new[] { "#5b8def", "#3d6fd0" }, new[] { "#e0703a", "#b5532a" }, new[] { "#7a8a99", "#58646f" } };
    private static readonly string[][] CatC = { new[] { "#f0a04b", "#c9792c", "#222222" }, new[] { "#9aa0a6", "#6f757b", "#222222" }, new[] { "#3a3a3a", "#1f1f1f", "#c8f060" }, new[] { "#f3efe6", "#cfc7b6", "#4a7fd0" } };
    private static readonly string[][] FishC = { new[] { "#ff8c3a", "#ffb36b" }, new[] { "#3aa0ff", "#8fd0ff" }, new[] { "#ff5a7a", "#ffa0b4" } };
    /// <summary>Đỉnh đầu (hình quay phải) — chỗ đội mũ Noel.</summary>
    private static readonly Dictionary<string, (float X, float Y)> Hat = new()
    {
        ["bee"] = (15, 7.2f), ["butterfly"] = (14.4f, 8.2f), ["bird"] = (15, 3.4f), ["ladybug"] = (12, 5.1f), ["cat"] = (19, 4.4f), ["snail"] = (16.2f, 2),
        ["fish"] = (14.5f, 2.4f), ["firefly"] = (8, 2.3f),
    };
    private const float StepMax = 0.05f;   // bước mô phỏng tối đa — khung thưa thì chia nhỏ

    private sealed class Pet
    {
        public string Kind = "", Mode = "fly", C1 = "", C2 = "", Eye = "";
        public float W, H, X, Y, Vx, Vy, ModeT, Ph, Tx, Ty, Cd, TrailCd, HeartCd, Tilt, Jump, Peck, LoopA, LoopCx, LoopCy, LandX;
        public float Rest, AccX;   // chỉ thú cưng trong editor dùng
        public int Face = 1;
        public bool Moving, RunSleep;
    }
    private sealed class Part { public string K = "dot", C = "", Txt = ""; public float X, Y, Vx, Vy, Life, Max, R; }
    private sealed class Flower { public float X, Grow, Life = 1; public bool Leaving; public Pet? By; }
    private sealed class Bubble { public Pet Pet = null!; public string Text = "", Kind = ""; public float Life; }

    private readonly Control _host;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly Random _rnd = new();
    private StatusPetSettings _s = new();
    private StatusPetEnv _env = new();
    private List<Pet> _pets = new();
    private readonly List<Part> _parts = new();
    private Flower? _flower;
    private float _flowerCd = 6, _W, _H, _t, _mouseAt = -10, _k = 1;
    private double _last;
    private PointF? _mouse;
    private Rectangle _dirty;
    private Bubble? _bubble;
    private Pet? _hoverPet, _restPet;
    private float _hoverT, _burst, _runSince, _seasonCd, _warnCd = 4;
    private int _tipIdx;
    private bool _running, _restAsk;

    /// <summary>Bấm vào con thú lúc đang nhắc nghỉ (MainForm mở Giải lao).</summary>
    public event Action? RestClicked;

    public StatusPets(Control host)
    {
        _host = host;
        _timer.Tick += (_, _) => Tick();
    }

    public bool Enabled => _s.Enabled;

    public void Set(StatusPetSettings s)
    {
        _s = s.Clone().Normalize();
        if (!_s.Enabled)
        {
            _timer.Stop(); _pets.Clear(); _parts.Clear(); _flower = null; _bubble = null; _restPet = null;
            ClearDirty();
            return;
        }
        if (!_s.Trail) _parts.RemoveAll(q => q.K == "dot");
        if (!_s.Tips && _bubble is { Kind: not "rest" }) _bubble = null;
        Measure();
        Sync();
        Schedule();
    }

    /// <summary>Trạng thái lúc chạy (workspace DB khách, mùa, Bcode có đang active, tạm ẩn, mẹo) — MainForm.PushPetsEnv.</summary>
    public void SetEnv(StatusPetEnv e)
    {
        _env = e.Clone();
        if (_env.Hidden) { _timer.Stop(); ClearDirty(); return; }
        Schedule();
    }

    private void ClearDirty()
    {
        if (!_dirty.IsEmpty && !_host.IsDisposed) _host.Invalidate(_dirty, true);
        _dirty = Rectangle.Empty;
    }

    /// <summary>Nhịp vẽ (như schedule() bên web): Bcode đang được dùng → 30 ms (vẽ thưa lúc rảnh trông giật); Bcode không active → 50 ms.</summary>
    private void Schedule()
    {
        if (!_s.Enabled || _env.Hidden) { _timer.Stop(); return; }
        var busy = _env.Active || MouseActive() || _bubble is not null || _burst > 0;
        var iv = busy ? 30 : 50;
        if (_timer.Interval != iv) _timer.Interval = iv;
        if (!_timer.Enabled) { _clock.Restart(); _last = 0; _timer.Start(); }
    }

    public void Dispose() => _timer.Dispose();

    private void Measure()
    {
        _k = NativeSkin.Dens(_host, 18) / 18f;
        _W = _host.ClientSize.Width / _k; _H = _host.ClientSize.Height / _k;
    }

    // ---------- tiện ích ----------
    private float Rnd(float a, float b) => a + (float)_rnd.NextDouble() * (b - a);
    private static T Pick<T>(T[] a, Random r) => a[r.Next(a.Length)];
    private static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;
    private static float Hypot(float x, float y) => MathF.Sqrt(x * x + y * y);
    private float Ground(Pet p) => _H - p.H;
    private void PickTarget(Pet p) { p.Tx = Rnd(2, Math.Max(4, _W - p.W - 2)); p.Ty = Rnd(0, Math.Max(0, _H - p.H - (p.Kind == "bird" ? 4 : 0))); }
    private static bool Near(Pet p, float d) => Hypot(p.Tx - p.X, p.Ty - p.Y) < d;
    private bool MouseActive() => _s.Mouse && _mouse is not null && _t - _mouseAt < 2.5f;
    private float DistMouse(Pet p) => _mouse is { } m ? Hypot(m.X - (p.X + p.W / 2), m.Y - (p.Y + p.H / 2)) : 1e9f;

    private void Trail(Pet p, float dt, string color, float every, float life, float dy, float vy, float r)
    {
        if (!_s.Trail) return;
        p.TrailCd -= dt;
        if (p.TrailCd > 0) return;
        p.TrailCd = every;
        _parts.Add(new Part { K = "dot", X = p.X + (p.Face > 0 ? 2 : p.W - 2), Y = p.Y + dy + Rnd(-1, 1), Vy = vy, Life = life, Max = life, C = color, R = r });
    }

    private void Heart(Pet p, float dt)
    {
        p.HeartCd -= dt;
        if (p.HeartCd <= 0 && DistMouse(p) < 24) { p.HeartCd = 1.2f; _parts.Add(new Part { K = "heart", X = p.X + p.W / 2, Y = p.Y, Vx = Rnd(-6, 6), Vy = -14, Life = 1.4f, Max = 1.4f }); }
    }

    private Pet MakePet(string kind)
    {
        var (w, h) = Size[kind];
        var p = new Pet { Kind = kind, W = w, H = h, X = Rnd(0, Math.Max(1, _W - w)), Face = _rnd.NextDouble() < 0.5 ? 1 : -1, Ph = Rnd(0, 10) };
        var c = kind == "butterfly" ? Pick(Bfly, _rnd) : kind == "bird" ? Pick(BirdC, _rnd) : kind == "cat" ? Pick(CatC, _rnd) : kind == "fish" ? Pick(FishC, _rnd) : null;
        if (c is not null) { p.C1 = c[0]; p.C2 = c[1]; p.Eye = c.Length > 2 ? c[2] : ""; }
        if (kind == "fish") { p.Mode = "swim"; p.Y = Rnd(FishTop(), Math.Max(FishTop(), _H - p.H - 1)); PickFishTarget(p); }
        else if (kind is "bee" or "butterfly" or "bird" or "firefly") { p.Y = Rnd(0, Math.Max(0, _H - p.H)); PickTarget(p); }
        else { p.Mode = "walk"; p.Y = Ground(p); p.ModeT = Rnd(2, 6); }
        return p;
    }

    private float FishTop() => Math.Max(0, _H * 0.3f);
    private void PickFishTarget(Pet p) { p.Tx = Rnd(2, Math.Max(4, _W - p.W - 2)); p.Ty = Rnd(FishTop(), Math.Max(FishTop(), _H - p.H - 1)); }

    private float FlyTo(Pet p, float dt, float max, float wobA, float wobF)
    {
        var wob = MathF.Sin((_t + p.Ph) * wobF) * wobA;
        var ax = (p.Tx - p.X) * 5 - p.Vx * 2.6f + Rnd(-30, 30);
        var ay = (p.Ty + wob - p.Y) * 7 - p.Vy * 3 + Rnd(-25, 25);
        p.Vx += ax * dt; p.Vy += ay * dt;
        var sp = Hypot(p.Vx, p.Vy);
        if (sp > max) { p.Vx *= max / sp; p.Vy *= max / sp; sp = max; }
        p.X = Clamp(p.X + p.Vx * dt, 0, Math.Max(0, _W - p.W));
        p.Y = Clamp(p.Y + p.Vy * dt, -2, Math.Max(-2, _H - p.H));
        if (Math.Abs(p.Vx) > 10) p.Face = p.Vx > 0 ? 1 : -1;
        p.Tilt = Clamp(MathF.Atan2(p.Vy, Math.Abs(p.Vx) + 20), -0.45f, 0.45f);
        return sp;
    }

    private void WalkStep(Pet p, float dt, float speed, Action next)
    {
        p.Y = Ground(p); p.Tilt = 0; p.ModeT -= dt;
        p.Moving = p.Mode == "walk";
        if (p.Moving)
        {
            p.X += p.Face * speed * dt;
            if (p.X <= 0) { p.X = 0; p.Face = 1; }
            if (p.X >= _W - p.W) { p.X = Math.Max(0, _W - p.W); p.Face = -1; }
        }
        if (p.ModeT <= 0) next();
    }

    private void ReleaseFlower(Pet p, bool done) { if (_flower is { } f && f.By == p) { if (done) f.Leaving = true; else f.By = null; } }

    // ---------- hành vi từng loài (xem statuspets.js) ----------
    private void StepNectar(Pet p, float dt)
    {
        var bf = p.Kind == "butterfly"; var max = bf ? 55f : 85f;
        var chase = MouseActive();
        if (chase && p.Mode != "loop")
        {
            var m = _mouse!.Value;
            if (p.Mode == "rest") { p.Mode = "fly"; ReleaseFlower(p, false); }
            p.Tx = m.X + (m.X > p.X + p.W / 2 ? -20 : 4) + MathF.Sin((_t + p.Ph) * 3) * 8;
            p.Ty = Clamp(m.Y - 8 + MathF.Cos((_t + p.Ph) * 3) * 3, 0, Math.Max(0, _H - p.H));
            Heart(p, dt);
            max *= 1.8f;
        }
        if (p.Mode == "rest")
        {
            p.ModeT -= dt; p.Vx = p.Vy = 0; p.Tilt = 0;
            if (_flower is { } f && f.By == p) f.Life -= dt * 0.25f;
            if (p.ModeT <= 0) { p.Mode = "fly"; PickTarget(p); p.Vy = -40; ReleaseFlower(p, true); }
            return;
        }
        if (p.Mode == "loop")
        {
            p.LoopA += dt * 7;
            p.X = p.LoopCx + MathF.Sin(p.LoopA) * 7 * p.Face; p.Y = p.LoopCy - (1 - MathF.Cos(p.LoopA)) * 5; p.Tilt = -p.LoopA;
            if (p.LoopA >= MathF.PI * 2) { p.Mode = "fly"; p.Vx = p.Face * 50; p.Vy = 0; p.Tilt = 0; PickTarget(p); }
            return;
        }
        var sp = FlyTo(p, dt, max, bf ? 4 : 2.5f, bf ? 3 : 5.3f);
        if (sp > 25) Trail(p, dt, bf ? p.C2 : "#f5b400", bf ? 0.09f : 0.045f, 0.9f, p.H * 0.7f, 4, 1.1f);
        if (chase) return;
        var mine = _flower is { Leaving: false } fl && (fl.By == p || fl.By is null);
        if (mine) { _flower!.By = p; p.Tx = _flower.X - p.W / 2; p.Ty = Math.Max(0, _H - p.H - 4); }
        if (Near(p, 5))
        {
            if (mine) { p.Mode = "rest"; p.ModeT = Rnd(2.5f, 4); p.X = p.Tx; p.Y = p.Ty; return; }
            var r = _rnd.NextDouble();
            if (r < 0.2) { p.Mode = "rest"; p.ModeT = Rnd(1, 2.5f); p.Y = Math.Max(0, _H - p.H); }
            else if (!bf && r < 0.38 && _W > 60) { p.Mode = "loop"; p.LoopA = 0; p.LoopCx = p.X; p.LoopCy = p.Y + 5; }
            else PickTarget(p);
        }
    }

    private void Flee(Pet p, float awayX)
    {
        p.Mode = "flee"; p.ModeT = 1.6f; p.Vy = -90;
        p.Tx = awayX > p.X + p.W / 2 ? Rnd(0, Math.Max(0, p.X - 60)) : Rnd(Math.Min(Math.Max(0, _W - p.W), p.X + 60), Math.Max(0, _W - p.W));
        p.Ty = Rnd(0, 3);
    }

    private void StepBird(Pet p, float dt)
    {
        if (_s.Mouse && _mouse is { } m && p.Mode != "flee" && DistMouse(p) < 45) Flee(p, m.X);
        if (p.Mode == "hop")
        {
            p.ModeT -= dt; p.Cd -= dt; p.Tilt = 0;
            if (p.Jump > 0)
            {
                p.Jump -= dt;
                p.X = Clamp(p.X + p.Face * 16 * dt, 0, Math.Max(0, _W - p.W));
                p.Y = Ground(p) - MathF.Sin(Clamp(1 - p.Jump / 0.25f, 0, 1) * MathF.PI) * 3;
            }
            else
            {
                p.Y = Ground(p);
                if (p.Peck > 0) { p.Peck -= dt; p.Tilt = 0.5f; }
            }
            if (p.Cd <= 0 && p.Jump <= 0)
            {
                p.Cd = Rnd(0.3f, 0.9f);
                var r = _rnd.NextDouble();
                if (r < 0.45) p.Jump = 0.25f; else if (r < 0.75) p.Peck = 0.25f; else p.Face = -p.Face;
                if (p.X < 2) p.Face = 1;
                if (p.X > _W - p.W - 2) p.Face = -1;
            }
            if (p.ModeT <= 0) { p.Mode = "fly"; p.Vy = -70; PickTarget(p); }
            return;
        }
        if (p.Mode == "flee") { p.ModeT -= dt; if (p.ModeT <= 0) { p.Mode = "fly"; PickTarget(p); } }
        if (p.Mode == "land") { p.Tx = p.LandX; p.Ty = Ground(p); }
        FlyTo(p, dt, p.Mode == "flee" ? 220 : 140, p.Mode == "land" ? 0 : 2, 4);
        if (p.Mode == "land" && Math.Abs(p.Y - Ground(p)) < 1.5f && Math.Abs(p.Tx - p.X) < 6)
        {
            p.Mode = "hop"; p.ModeT = Rnd(3, 8); p.Vx = p.Vy = 0; p.Cd = 0.3f; p.Jump = 0; p.Peck = 0;
            return;
        }
        if (p.Mode == "fly" && Near(p, 8))
        {
            if (_rnd.NextDouble() < 0.35) { p.Mode = "land"; p.LandX = Clamp(p.X + p.Face * Rnd(10, 40), 0, Math.Max(0, _W - p.W)); }
            else PickTarget(p);
        }
    }

    private void StepLadybug(Pet p, float dt)
    {
        if (p.Mode == "air")
        {
            p.ModeT -= dt;
            if (p.ModeT <= 0) p.Ty = Ground(p);
            FlyTo(p, dt, 40, 1.5f, 6);
            if (p.ModeT <= 0 && Math.Abs(p.Y - Ground(p)) < 1) { p.Mode = "walk"; p.ModeT = Rnd(3, 8); p.Vx = p.Vy = 0; p.Tilt = 0; }
            else if (p.ModeT > 0 && Near(p, 4)) PickTarget(p);
            return;
        }
        WalkStep(p, dt, 10, () =>
        {
            var r = _rnd.NextDouble();
            if (p.Mode == "walk" && r < 0.3) { p.Mode = "idle"; p.ModeT = Rnd(0.8f, 2.5f); }
            else if (p.Mode == "walk" && r < 0.5) { p.Mode = "air"; p.ModeT = Rnd(1.5f, 3); p.Vy = -30; p.Moving = false; PickTarget(p); }
            else { p.Mode = "walk"; p.ModeT = Rnd(3, 8); if (_rnd.NextDouble() < 0.4) p.Face = -p.Face; }
        });
    }

    private static void Pounce(Pet p) { p.Mode = "pounce"; p.ModeT = 0.45f; p.Vx = p.Face * 30; p.Moving = false; }

    private void StepCat(Pet p, float dt)
    {
        p.Tilt = 0;
        // SQL Query chạy lâu (> 5 giây): mèo nằm ngủ chờ; chạy xong (OnEvent ok / err) thì thức dậy.
        if (_running && _s.ReactSql && _t - _runSince > 5 && p.Mode is not ("sleep" or "pounce") && !MouseActive())
        {
            p.Mode = "sleep"; p.ModeT = 1e6f; p.Cd = 0.5f; p.Moving = false; p.RunSleep = true;
        }
        if (MouseActive() && p.Mode is "walk" or "stalk" or "sleep")
        {
            if (p.Mode == "sleep") { p.Mode = "wake"; p.ModeT = 0.8f; p.RunSleep = false; } else p.Mode = "chase";
        }
        if (p.Mode == "wake")
        {
            p.Y = Ground(p); p.Moving = false; p.ModeT -= dt;
            if (p.ModeT <= 0) { p.Mode = MouseActive() ? "chase" : "sit"; p.ModeT = 1; }
            return;
        }
        if (p.Mode == "chase")
        {
            p.Y = Ground(p);
            if (!MouseActive()) { p.Mode = "sit"; p.ModeT = Rnd(1, 2); p.Moving = false; return; }
            var dx = _mouse!.Value.X - p.W / 2 - p.X;
            p.Face = dx > 0 ? 1 : -1;
            if (Math.Abs(dx) < 10) Pounce(p);
            else { p.X = Clamp(p.X + p.Face * Math.Min(90, Math.Abs(dx) * 3) * dt, 0, Math.Max(0, _W - p.W)); p.Moving = true; }
            return;
        }
        if (p.Mode == "pounce")
        {
            p.ModeT -= dt;
            var k = Clamp(1 - p.ModeT / 0.45f, 0, 1);
            p.Y = Ground(p) - MathF.Sin(k * MathF.PI) * 6;
            p.X = Clamp(p.X + p.Vx * dt, 0, Math.Max(0, _W - p.W));
            p.Tilt = -0.3f * MathF.Cos(k * MathF.PI);
            if (p.ModeT <= 0)
            {
                p.Mode = "sit"; p.ModeT = Rnd(1.2f, 2.5f); p.Y = Ground(p); p.Tilt = 0;
                _parts.Add(new Part { K = "heart", X = p.X + p.W / 2 + p.Face * 8, Y = p.Y + 2, Vy = -12, Life = 1.2f, Max = 1.2f });
            }
            return;
        }
        if (p.Mode is "walk" or "stalk")
        {
            var b = _pets.FirstOrDefault(q => q.Kind == "bird" && q.Mode == "hop");
            if (b is not null)
            {
                p.Mode = "stalk"; p.Y = Ground(p);
                var d = b.X - p.X;
                p.Face = d > 0 ? 1 : -1;
                p.X = Clamp(p.X + p.Face * 28 * dt, 0, Math.Max(0, _W - p.W)); p.Moving = true;
                if (Math.Abs(d) < 22) { Flee(b, p.X + p.W / 2); Pounce(p); }
                return;
            }
            if (p.Mode == "stalk") { p.Mode = "sit"; p.ModeT = 1.5f; }
        }
        if (p.Mode == "sleep")
        {
            p.Y = Ground(p); p.Moving = false; p.ModeT -= dt; p.Cd -= dt;
            if (p.Cd <= 0) { p.Cd = 1.1f; _parts.Add(new Part { K = "z", X = p.X + (p.Face > 0 ? p.W - 4 : 4), Y = p.Y + 6, Vx = p.Face * 3, Vy = -6, Life = 2, Max = 2 }); }
            if (p.ModeT <= 0) { p.Mode = "sit"; p.ModeT = 1; }
            return;
        }
        WalkStep(p, dt, 22, () =>
        {
            var r = _rnd.NextDouble();
            var sleepy = _s.Seasonal && _env.Night;   // buổi tối: mèo buồn ngủ hơn
            if (p.Mode == "walk")
            {
                if (r < (sleepy ? 0.3 : 0.45)) { p.Mode = "sit"; p.ModeT = Rnd(2, 5); }
                else if (r < (sleepy ? 0.75 : 0.65)) { p.Mode = "sleep"; p.ModeT = Rnd(8, 18) * (sleepy ? 1.6f : 1); p.Cd = 0.5f; }
                else { p.ModeT = Rnd(3, 7); p.Face = -p.Face; }
            }
            else { p.Mode = "walk"; p.ModeT = Rnd(3, 8); if (_rnd.NextDouble() < 0.5) p.Face = -p.Face; }
        });
    }

    private void StepSnail(Pet p, float dt)
    {
        p.Y = Ground(p); p.Tilt = 0;
        if (_s.Mouse && _mouse is not null && DistMouse(p) < 30) { p.Mode = "hide"; p.ModeT = 2.5f; }
        if (p.Mode == "hide") { p.Moving = false; p.ModeT -= dt; if (p.ModeT <= 0) { p.Mode = "walk"; p.ModeT = Rnd(5, 12); } return; }
        WalkStep(p, dt, 4, () =>
        {
            if (p.Mode == "walk" && _rnd.NextDouble() < 0.35) { p.Mode = "idle"; p.ModeT = Rnd(1.5f, 4); }
            else { p.Mode = "walk"; p.ModeT = Rnd(5, 12); if (_rnd.NextDouble() < 0.3) p.Face = -p.Face; }
        });
        if (p.Moving) Trail(p, dt, "#bec8d2", 0.35f, 6, p.H - 0.8f, 0, 0.9f);
    }

    private void StepFish(Pet p, float dt)
    {
        if (_s.Mouse && _mouse is { } m && p.Mode != "dart" && DistMouse(p) < 35)
        {
            p.Mode = "dart"; p.ModeT = 0.9f;
            p.Tx = m.X > p.X + p.W / 2 ? Rnd(0, Math.Max(0, p.X - 70)) : Rnd(Math.Min(Math.Max(0, _W - p.W), p.X + 70), Math.Max(0, _W - p.W));
            p.Ty = Rnd(FishTop(), Math.Max(FishTop(), _H - p.H - 1));
        }
        if (p.Mode == "dart") { p.ModeT -= dt; if (p.ModeT <= 0) { p.Mode = "swim"; PickFishTarget(p); } }
        var sp = FlyTo(p, dt, p.Mode == "dart" ? 170 : 38, 1.6f, 2.2f);
        p.Y = Clamp(p.Y, FishTop() - 1, Math.Max(FishTop(), _H - p.H));
        p.Tilt *= 0.4f;
        p.Moving = sp > 6;
        if (p.Mode == "swim" && Near(p, 6)) PickFishTarget(p);
        p.Cd -= dt;
        if (p.Cd <= 0) { p.Cd = Rnd(1.2f, 3.5f); var life = 1.6f; _parts.Add(new Part { K = "bubble", X = p.X + (p.Face > 0 ? p.W - 2 : 2), Y = p.Y + 3, Vx = Rnd(-1, 1), Vy = -9, Life = life, Max = life, R = Rnd(0.9f, 1.6f) }); }
    }

    private void StepFirefly(Pet p, float dt)
    {
        if (_s.Mouse && _mouse is { } m && DistMouse(p) < 28) { p.Tx = Clamp(p.X + (p.X + p.W / 2 > m.X ? 40 : -40), 0, Math.Max(0, _W - p.W)); p.Ty = Rnd(0, Math.Max(0, _H - p.H)); }
        FlyTo(p, dt, 28, 3, 1.4f);
        if (Near(p, 4)) PickTarget(p);
    }

    private void Step(Pet p, float dt)
    {
        switch (p.Kind)
        {
            case "bee": case "butterfly": StepNectar(p, dt); break;
            case "bird": StepBird(p, dt); break;
            case "ladybug": StepLadybug(p, dt); break;
            case "cat": StepCat(p, dt); break;
            case "snail": StepSnail(p, dt); break;
            case "fish": StepFish(p, dt); break;
            case "firefly": StepFirefly(p, dt); break;
        }
    }

    // ---------- bong bóng chữ, bấm / rê chuột lên con thú (xem statuspets.js) ----------
    private Pet? HitPet(float x, float y)
    {
        for (var i = _pets.Count - 1; i >= 0; i--)
        {
            var p = _pets[i];
            if (x >= p.X - 4 && x <= p.X + p.W + 4 && y >= p.Y - 4 && y <= p.Y + p.H + 4) return p;
        }
        return null;
    }

    private string PetName(Pet p)
    {
        var same = _pets.Where(q => q.Kind == p.Kind).ToList();
        var n = _s.NameOf(p.Kind);
        return same.Count > 1 ? n + " " + (same.IndexOf(p) + 1) : n;
    }

    private int PetDays() => _s.AdoptedOn is { } a ? Math.Max(1, (DateTime.Today - a.Date).Days + 1) : 0;

    private void Say(Pet p, string text, string kind, float life) { _bubble = new Bubble { Pet = p, Text = text, Kind = kind, Life = life }; _burst = Math.Max(_burst, 0.5f); Schedule(); }

    private const string RestText = "☕ Làm lâu rồi, nghỉ chút nhé — bấm vào mình để Giải lao";

    /// <summary>Con trỏ tay khi rê lên con thú (bấm được). <paramref name="pt"/> = toạ độ client của control chủ.</summary>
    public bool IsOverPet(Point pt) => _s.Enabled && !_env.Hidden && (_s.Tips || _restAsk) && HitPet(pt.X / _k, pt.Y / _k) is not null;

    /// <summary>Bấm vào thanh (toạ độ client của control chủ): trúng con thú thì hiện mẹo / mở Giải lao khi đang nhắc nghỉ. Trả true nếu trúng.</summary>
    public bool Click(Point pt)
    {
        if (!_s.Enabled || _env.Hidden) return false;
        var p = HitPet(pt.X / _k, pt.Y / _k);
        if (p is null) return false;
        if (_restAsk)
        {
            _restAsk = false; _bubble = null;
            _parts.Add(new Part { K = "heart", X = p.X + p.W / 2, Y = p.Y, Vy = -14, Life = 1.4f, Max = 1.4f });
            RestClicked?.Invoke();
            return true;
        }
        if (!_s.Tips) return false;
        var tips = _env.Tips ?? Array.Empty<string>();
        Say(p, tips.Length > 0 ? "💡 " + tips[_tipIdx++ % tips.Length] : "💡 Chưa có mẹo nào.", "tip", 8);
        _parts.Add(new Part { K = "heart", X = p.X + p.W / 2, Y = p.Y, Vy = -14, Life = 1.2f, Max = 1.2f });
        return true;
    }

    private void Hover(float dt)
    {
        var p = _s.Tips && _mouse is { } m && _bubble is null ? HitPet(m.X, m.Y) : null;
        if (p is null || p != _hoverPet) { _hoverPet = p; _hoverT = 0; return; }
        _hoverT += dt;
        if (_hoverT > 0.7f)
        {
            var days = PetDays();
            Say(p, PetName(p) + (days > 0 ? $" · ở với bạn {days} ngày" : "") + " — bấm vào mình để xem mẹo", "name", 3.5f);
            _hoverT = -1e6f;
        }
    }

    // ---------- sự kiện từ ngoài (chạy SQL, nhắc nghỉ) ----------
    private void Mark(Pet p, string text, string color) =>
        _parts.Add(new Part { K = "mark", X = p.X + p.W / 2, Y = p.Y + 2, Vx = Rnd(-3, 3), Vy = -12, Life = 1.4f, Max = 1.4f, Txt = text, C = color });

    /// <summary>"run" (bắt đầu chạy SQL), "ok" / "err" (chạy xong, <paramref name="ms"/> = thời gian), "rest" / "rest-off" (nhắc nghỉ).</summary>
    public void OnEvent(string name, int ms = 0)
    {
        if (!_s.Enabled) return;
        switch (name)
        {
            case "run":
                if (_s.ReactSql) { _running = true; _runSince = _t; }
                return;
            case "ok":
            case "err":
                _running = false;
                foreach (var p in _pets.Where(q => q.RunSleep)) { p.RunSleep = false; p.Mode = "wake"; p.ModeT = 0.8f; }
                if (!_s.ReactSql) return;
                var big = ms > 10000;
                foreach (var p in _pets)
                {
                    if (name == "ok")
                    {
                        for (var k = 0; k < (big ? 3 : 1); k++) _parts.Add(new Part { K = "heart", X = p.X + p.W / 2 + Rnd(-4, 4), Y = p.Y - k * 4, Vx = Rnd(-6, 6), Vy = -14 - k * 3, Life = 1.6f, Max = 1.6f });
                        if (p.Kind == "bee" && p.Mode == "fly" && _W > 60) { p.Mode = "loop"; p.LoopA = 0; p.LoopCx = p.X; p.LoopCy = p.Y + 5; }
                        if (p.Kind == "cat" && p.Mode is "walk" or "sleep") { p.Mode = "sit"; p.ModeT = Rnd(1.5f, 3); }
                    }
                    else
                    {
                        Mark(p, "!", "#e0262b");
                        if (p.Kind == "bird") Flee(p, p.X + p.W / 2 + (_rnd.NextDouble() < 0.5 ? -1 : 1));
                        else if (p.Kind == "snail") { p.Mode = "hide"; p.ModeT = 4; }
                        else if (p.Kind == "cat" && p.Mode != "pounce") { p.Y = Ground(p); Pounce(p); p.Vx = 0; }
                        else if (p.Kind is "bee" or "butterfly" or "firefly") { if (p.Mode != "loop") { p.Mode = "fly"; ReleaseFlower(p, false); p.Vy = -80; PickTarget(p); } }
                        else if (p.Kind == "fish") { p.Mode = "dart"; p.ModeT = 0.9f; PickFishTarget(p); }
                    }
                }
                _burst = 3; Schedule();
                return;
            case "rest":
                _restAsk = true;
                _restPet = _pets.FirstOrDefault(p => p.Kind == "cat") ?? _pets.FirstOrDefault();
                if (_restPet is { } rp)
                {
                    if (rp.Kind == "cat") { rp.Mode = "sit"; rp.ModeT = 1e6f; }
                    Say(rp, RestText, "rest", 1e9f);
                }
                return;
            case "rest-off":
                _restAsk = false;
                if (_restPet is { Kind: "cat", Mode: "sit" } c && c.ModeT > 1e5f) c.ModeT = 1;
                _restPet = null;
                if (_bubble is { Kind: "rest" }) _bubble = null;
                return;
        }
    }

    private void World(float dt)
    {
        var lovers = _pets.Any(p => p.Kind is "bee" or "butterfly");
        var season = _s.Seasonal ? _env.Season : "";
        // Có mùa thì hoa mọc cả khi không có ong / bướm (trang trí), nở một lúc rồi tàn, mọc bông khác.
        var blooms = _s.Flowers && (lovers || season.Length > 0);
        _flowerCd -= dt;
        if (blooms && _flower is null && _flowerCd <= 0 && _W > 120) _flower = new Flower { X = FlowerSpot() };
        if (_flower is { } f)
        {
            f.Grow = Math.Min(1, f.Grow + dt * 1.5f);
            if (f.Leaving) f.Life -= dt * 0.8f;
            else if (!lovers) f.Life -= dt * 0.04f;   // không ai hút mật: tự tàn sau ~25 giây
            if (f.By is not null && !_pets.Contains(f.By)) f.By = null;
            if (f.Life <= 0 || !blooms) { _flower = null; _flowerCd = lovers ? Rnd(15, 35) : Rnd(3, 8); }
        }
        // Theo mùa: Tết cánh mai rơi, Noel tuyết rơi, Trung thu đèn lồng bay lên.
        _seasonCd -= dt;
        if (season.Length > 0 && _seasonCd <= 0 && _W > 60)
        {
            var fall = (_H + 4) / 6;
            if (season == "tet") { _seasonCd = Rnd(0.9f, 1.8f); _parts.Add(new Part { K = "petal", X = Rnd(0, _W), Y = -2, Vx = Rnd(-5, 5), Vy = Rnd(5, 9), Life = fall, Max = fall, R = Rnd(1, 1.6f), C = "#ffd23f" }); }
            else if (season == "noel") { _seasonCd = Rnd(0.25f, 0.5f); _parts.Add(new Part { K = "snow", X = Rnd(0, _W), Y = -2, Vx = Rnd(-3, 3), Vy = Rnd(6, 11), Life = fall, Max = fall, R = Rnd(0.8f, 1.5f) }); }
            else if (season == "trungthu") { _seasonCd = Rnd(6, 10); var rise = (_H + 12) / 3.2f; _parts.Add(new Part { K = "lantern", X = Rnd(20, Math.Max(21, _W - 20)), Y = _H + 4, Vx = Rnd(-2, 2), Vy = -3.2f, Life = rise, Max = rise }); }
        }
        // Workspace là DB thật của khách: thỉnh thoảng 1 con giơ "!".
        if (_env.Warn && _pets.Count > 0)
        {
            _warnCd -= dt;
            if (_warnCd <= 0) { _warnCd = Rnd(6, 11); Mark(_pets[_rnd.Next(_pets.Count)], "!", "#e0262b"); }
        }
        if (_bubble is { } bb)
        {
            bb.Life -= dt;
            if (bb.Life <= 0 || !_pets.Contains(bb.Pet)) _bubble = null;
        }
        if (_restPet is not null && !_pets.Contains(_restPet)) { _restPet = _pets.FirstOrDefault(); if (_restAsk && _restPet is { } np) Say(np, RestText, "rest", 1e9f); }
        if (_burst > 0) _burst -= dt;
        for (var i = _parts.Count - 1; i >= 0; i--)
        {
            var q = _parts[i];
            q.Life -= dt;
            if (q.Life <= 0) { _parts.RemoveAt(i); continue; }
            q.X += q.Vx * dt; q.Y += q.Vy * dt;
        }
    }

    private void Sync()
    {
        foreach (var kind in Order)
        {
            var want = _s.CountOf(kind);
            var have = _pets.Where(p => p.Kind == kind).ToList();
            for (var i = have.Count; i < want; i++) _pets.Add(MakePet(kind));
            for (var j = want; j < have.Count; j++) _pets.Remove(have[j]);
        }
        _pets = _pets.OrderBy(p => Array.IndexOf(Order, p.Kind)).ToList();
        if (_flower?.By is { } by && !_pets.Contains(by)) _flower.By = null;
    }

    private bool _hadBubble;

    private void Tick()
    {
        if (_host.IsDisposed || !_host.Visible || _host.FindForm() is null or { WindowState: FormWindowState.Minimized }) { _last = _clock.Elapsed.TotalSeconds; return; }
        var now = _clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Min(now - _last, 0.5); _last = now;
        Measure();
        if (_W < 30) return;
        var mp = _host.PointToClient(Cursor.Position);
        if (_host.ClientRectangle.Contains(mp))
        {
            var m = new PointF(mp.X / _k, mp.Y / _k);
            if (_mouse is not { } old || Math.Abs(old.X - m.X) + Math.Abs(old.Y - m.Y) > 0.5f) _mouseAt = _t;
            _mouse = m;
        }
        else _mouse = null;
        // Khung thưa thì chia nhỏ bước mô phỏng (≤ 50 ms) — chuyển động giữ đúng tốc độ, lò xo không bị bật nảy.
        var n = Math.Max(1, (int)MathF.Ceiling(dt / StepMax));
        var sub = dt / n;
        for (var i = 0; i < n; i++)
        {
            _t += sub;
            var d = sub * _s.Speed / 100f;
            foreach (var p in _pets.ToList()) Step(p, d);
            World(d);
        }
        Hover(dt);
        // Vùng cần vẽ lại = mọi con + hạt + hoa (cũ ∪ mới); có bong bóng chữ thì cả thanh.
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Grow(float x1, float y1, float x2, float y2) { minX = Math.Min(minX, x1); minY = Math.Min(minY, y1); maxX = Math.Max(maxX, x2); maxY = Math.Max(maxY, y2); }
        foreach (var p in _pets) Grow(p.X - 6, p.Y - 6, p.X + p.W + 6, p.Y + p.H + 6);
        foreach (var q in _parts) Grow(q.X - 6, q.Y - 10, q.X + 6, q.Y + 6);
        if (_flower is { } f) Grow(f.X - 6, _H - 14, f.X + 6, _H);
        if (_s.Seasonal && _env.Season.Length > 0 && _W > 120) { var tx = TreeX(); Grow(tx - 9, _H - 22, tx + 22, _H); }   // cây theo mùa (đèn nháy, đèn lồng đung đưa)
        if (_bubble is not null || _hadBubble) Grow(0, 0, _W, _H);
        _hadBubble = _bubble is not null;
        var r = minX > maxX ? Rectangle.Empty : Rectangle.FromLTRB((int)(minX * _k) - 1, (int)(minY * _k) - 1, (int)(maxX * _k) + 2, (int)(maxY * _k) + 2);
        var inv = _dirty.IsEmpty ? r : r.IsEmpty ? _dirty : Rectangle.Union(_dirty, r);
        if (!inv.IsEmpty) _host.Invalidate(inv, true);
        _dirty = r;
        Schedule();
    }

    // ---------- vẽ ----------
    private Graphics _g = null!;
    private float _alpha = 1;
    private static readonly Dictionary<string, Color> ColorCache = new();

    private Color Col(string hex)
    {
        if (!ColorCache.TryGetValue(hex, out var c))
        {
            var h = hex.TrimStart('#');
            int V(int i) => Convert.ToInt32(h.Substring(i, 2), 16);
            c = Color.FromArgb(h.Length >= 8 ? V(6) : 255, V(0), V(2), V(4));
            ColorCache[hex] = c;
        }
        return _alpha >= 1 ? c : Color.FromArgb((int)(c.A * Clamp(_alpha, 0, 1)), c);
    }

    private void E(float cx, float cy, float rx, float ry, string? fill, string? stroke = null, float sw = 1)
    {
        if (rx <= 0 || ry <= 0) return;
        if (fill is not null) { using var b = new SolidBrush(Col(fill)); _g.FillEllipse(b, cx - rx, cy - ry, 2 * rx, 2 * ry); }
        if (stroke is not null) { using var pn = new Pen(Col(stroke), sw); _g.DrawEllipse(pn, cx - rx, cy - ry, 2 * rx, 2 * ry); }
    }
    private void L(float x1, float y1, float x2, float y2, string c, float w) { using var pn = new Pen(Col(c), w); _g.DrawLine(pn, x1, y1, x2, y2); }
    private void Q(float x1, float y1, float cx, float cy, float x2, float y2, string c, float w)
    {
        using var pn = new Pen(Col(c), w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        // bậc 2 → bậc 3
        _g.DrawBezier(pn, x1, y1, x1 + 2f / 3 * (cx - x1), y1 + 2f / 3 * (cy - y1), x2 + 2f / 3 * (cx - x2), y2 + 2f / 3 * (cy - y2), x2, y2);
    }
    private void P(float[] pts, string fill)
    {
        var a = new PointF[pts.Length / 2];
        for (var i = 0; i < a.Length; i++) a[i] = new PointF(pts[2 * i], pts[2 * i + 1]);
        using var b = new SolidBrush(Col(fill));
        _g.FillPolygon(b, a);
    }

    private void DrawBee(Pet p)
    {
        var f = p.Mode == "rest" ? 0.6f + 0.4f * MathF.Abs(MathF.Sin(_t * 6)) : 0.35f + 0.65f * MathF.Abs(MathF.Sin((_t + p.Ph) * 35));
        float h1 = 7.2f * f, h2 = 6 * f;
        E(7, 7.5f - h1 / 2, 3.2f, h1 / 2, "#dff3ffd9", "#8fb8d0", 0.6f);
        E(10.5f, 7.5f - h2 / 2, 2.6f, h2 / 2, "#dff3ffcc", "#8fb8d0", 0.6f);
        E(9, 10, 6.2f, 4.2f, "#ffc928", "#3a2a00", 0.7f);
        L(7, 6.3f, 7, 13.7f, "#2b2100", 1.6f); L(10, 6, 10, 14, "#2b2100", 1.6f);
        L(3, 10, 0.8f, 10.2f, "#2b2100", 1);
        E(15, 9.5f, 2.4f, 2.4f, "#2b2100"); E(15.8f, 9, 0.6f, 0.6f, "#ffffff");
        Q(15.5f, 7.4f, 16.1f, 5.4f, 17.3f, 5.1f, "#2b2100", 0.6f); Q(14.6f, 7.3f, 14.6f, 5.3f, 15.5f, 4.7f, "#2b2100", 0.6f);
    }

    private void DrawButterfly(Pet p)
    {
        var f = p.Mode == "rest" ? 0.3f + 0.7f * MathF.Abs(MathF.Sin(_t * 2)) : 0.2f + 0.8f * MathF.Abs(MathF.Sin((_t + p.Ph) * 9));
        E(6.5f, 9.5f - 2.6f * f, 3.2f, 3.4f * f, p.C2, "#3a2a2a", 0.5f);
        E(9, 10, 5.5f, 1.4f, "#3b2a20");
        E(14.4f, 9.6f, 1.5f, 1.5f, "#3b2a20");
        Q(14.8f, 8.6f, 15.5f, 6, 17, 5.2f, "#3b2a20", 0.5f); Q(14.2f, 8.6f, 14.4f, 6, 15.4f, 4.8f, "#3b2a20", 0.5f);
        E(9, 9.5f - 4.6f * f, 4.4f, 4.8f * f, p.C1, "#3a2a2a", 0.5f);
        E(9.6f, 9.5f - 6 * f, 1.1f, 1.1f * f, "#ffffffcc");
        E(7.8f, 9.5f - 3.8f * f, 0.8f, 0.8f * f, "#ffffffaa");
    }

    private void DrawBird(Pet p)
    {
        var flying = p.Mode != "hop";
        P(new[] { 4, 7.5f, -0.5f, 5.2f, 0.2f, 9.8f }, p.C2);
        if (!flying) { L(9, 11.5f, 8.6f, 14, "#a0522d", 0.8f); L(11, 11.5f, 11.4f, 14, "#a0522d", 0.8f); }
        E(9, 8.5f, 6.5f, 4.2f, p.C1);
        E(10.3f, 10, 4, 2.3f, "#f3e6c8");
        E(15, 6.5f, 3.2f, 3.2f, p.C1);
        E(16, 6, 0.8f, 0.8f, "#111111"); E(16.25f, 5.75f, 0.25f, 0.25f, "#ffffff");
        P(new[] { 17.8f, 6.1f, 20, 7, 17.8f, 7.8f }, "#f5a623");
        if (flying)
        {
            var s = MathF.Sin((_t + p.Ph) * 14);
            if (MathF.Sin((_t + p.Ph) * 1.7f) > 0.6f) s = 0.25f;   // lướt
            P(new[] { 6, 8, 12, 8, 8.5f, 8 - 7 * s }, p.C2);
        }
        else E(8.3f, 8, 4.2f, 2.1f, p.C2);
    }

    private void DrawLadybug(Pet p)
    {
        var air = p.Mode == "air";
        if (!air)
            for (var i = 0; i < 3; i++)
            {
                float x = 4 + i * 2.8f, sw = p.Moving ? MathF.Sin((_t + p.Ph) * 16 + i * 2) * 1.1f : 0;
                L(x, 8.5f, x + sw, 10, "#1a1a1a", 0.7f);
            }
        E(12, 7, 2, 2, "#1a1a1a"); E(12.9f, 6.5f, 0.45f, 0.45f, "#ffffff");
        if (air) { var f = 0.3f + 0.7f * MathF.Abs(MathF.Sin((_t + p.Ph) * 30)); E(5, 4 - 2 * f, 4.5f, 2.2f * f + 0.3f, "#ffffffb0", "#9ab0c0", 0.4f); }
        E(6.5f, 6.3f, 5.5f, 3.6f, "#e0262b", "#3a0a0a", 0.5f);
        E(4.3f, 5.2f, 1, 1, "#1a1a1a"); E(7.6f, 4.6f, 1, 1, "#1a1a1a"); E(8.6f, 7.4f, 0.9f, 0.9f, "#1a1a1a"); E(3.8f, 7.9f, 0.8f, 0.8f, "#1a1a1a");
        E(5.2f, 4.2f, 1.4f, 0.7f, "#ffffff55");
    }

    private void DrawCat(Pet p)
    {
        string c1 = p.C1, c2 = p.C2;
        if (p.Mode == "sleep")
        {
            Q(4, 14.5f, 1, 17.5f, 9, 17.3f, c1, 2);
            E(11, 14.2f, 8, 3.6f, c1);
            L(9, 11, 9.4f, 12.6f, c2, 1); L(12, 10.8f, 12.3f, 12.4f, c2, 1);
            E(18.3f, 13.3f, 3.5f, 3.2f, c1);
            P(new[] { 16, 11.4f, 16.3f, 8.4f, 18, 10.3f }, c1); P(new[] { 19, 10.2f, 20.8f, 8.2f, 21.2f, 11.4f }, c1);
            L(18.8f, 13.3f, 20.2f, 13.5f, "#222222", 0.6f);
            E(21.6f, 14.4f, 0.45f, 0.45f, "#e88888");
            return;
        }
        var sit = p.Mode is "sit" or "wake";
        float by = sit ? 1.5f : 0, sway = sit ? 3 : 2;
        Q(4, 10 + by, 0.5f, 9 + MathF.Sin(_t * 3 + p.Ph) * sway, 1.5f, 3 + MathF.Sin(_t * 2 + p.Ph) * sway, c1, 1.8f);
        if (!sit)
        {
            var ph = (_t + p.Ph) * (p.Mode == "chase" ? 18 : 11);
            float[] lx = { 6, 8.5f, 14, 16.5f };
            for (var i = 0; i < 4; i++)
            {
                var o = p.Moving ? MathF.Sin(ph + (i % 2 == 1 ? MathF.PI : 0) + (i > 1 ? MathF.PI / 2 : 0)) * 1.6f : 0;
                L(lx[i], 13, lx[i] + o, 17.6f, c1, 1.6f);
            }
        }
        else { L(14, 13, 14, 17.6f, c1, 1.6f); L(16.3f, 13, 16.3f, 17.6f, c1, 1.6f); E(7, 15, 3.5f, 2.5f, c1); }
        E(11, 11 + by, 7.5f, 4.2f, c1);
        L(9, 7.2f + by, 9.5f, 9.5f + by, c2, 1); L(12, 7 + by, 12.3f, 9.3f + by, c2, 1);
        E(19, 8, 3.8f, 3.8f, c1);
        P(new[] { 16.3f, 5.6f, 17, 1.8f, 18.6f, 4.6f }, c1); P(new[] { 19.6f, 4.4f, 21.4f, 2, 21.8f, 5.8f }, c1);
        P(new[] { 16.9f, 5, 17.2f, 3, 18.1f, 4.6f }, "#f2a0a0");
        if (MathF.Sin(_t * 0.9f + p.Ph) > 0.97f) L(20, 7.4f, 21.2f, 7.4f, p.Eye, 0.6f);   // chớp mắt
        else E(20.6f, 7.4f, 0.75f, 0.85f, p.Eye);
        E(22.6f, 8.6f, 0.5f, 0.45f, "#e88888");
        L(22, 9.2f, 24, 8.8f, "#00000055", 0.3f); L(22, 9.6f, 24, 9.9f, "#00000055", 0.3f);
    }

    private void DrawSnail(Pet p)
    {
        var hide = p.Mode == "hide";
        if (!hide)
        {
            float wv = MathF.Sin((_t + p.Ph) * 2) * 0.5f, st = p.Moving ? MathF.Sin((_t + p.Ph) * 3) * 0.4f : 0;
            E(9.5f, 10.4f, 8 + st, 1.6f, "#c9b28a");
            E(15.4f, 8.4f, 1.7f, 2.8f, "#c9b28a");
            L(15.9f, 6, 17, 2.6f + wv, "#a8936c", 0.7f); L(15, 6, 15.5f, 2.9f - wv, "#a8936c", 0.7f);
            E(17, 2.6f + wv, 0.75f, 0.75f, "#333333"); E(15.5f, 2.9f - wv, 0.7f, 0.7f, "#333333");
        }
        else E(8, 10.6f, 5, 1.3f, "#c9b28a");
        var sy = hide ? 1f : 0f;
        E(8, 6 + sy, 4.8f, 4.8f, "#b5651d", "#6b3b12", 0.8f);
        E(8.6f, 6.4f + sy, 2.6f, 2.6f, null, "#6b3b12", 0.7f);
        E(9, 6.7f + sy, 1.1f, 1.1f, null, "#6b3b12", 0.6f);
        E(6.5f, 4 + sy, 1.2f, 0.6f, "#ffffff44");
    }

    private void DrawFish(Pet p)
    {
        var wag = MathF.Sin((_t + p.Ph) * (p.Mode == "dart" ? 22 : 8)) * 1.8f;
        P(new[] { 4, 5, -0.5f, 1.2f + wag, -0.5f, 8.8f + wag }, p.C2);
        P(new[] { 8, 1.8f, 11.5f, -0.2f, 12, 2.4f }, p.C2);
        E(10, 5, 6.8f, 3.7f, p.C1);
        E(9.6f, 6.6f, 2.4f, 1, p.C2);
        L(13, 2.2f, 13, 7.8f, "#00000022", 0.6f);
        E(15, 4.2f, 1, 1, "#111111"); E(15.3f, 3.9f, 0.3f, 0.3f, "#ffffff");
        E(8, 3.4f, 2.2f, 0.8f, "#ffffff55");
    }

    private void DrawFirefly(Pet p)
    {
        var glow = 0.35f + 0.65f * Math.Max(0, MathF.Sin((_t + p.Ph) * 2.2f));
        var a0 = _alpha;
        _alpha = a0 * 0.28f * glow; E(2.4f, 4.8f, 5.5f * glow + 1, 5 * glow + 1, "#fff078");
        _alpha = a0 * (0.45f + 0.55f * glow); E(2.4f, 4.8f, 2.2f, 1.8f, "#ffec5a");
        _alpha = a0;
        var f = 0.4f + 0.6f * MathF.Abs(MathF.Sin((_t + p.Ph) * 26));
        E(5, 2.6f - 1.4f * f, 2.6f, 1.6f * f + 0.2f, "#ffffff88", "#9ab0c0", 0.3f);
        E(5.2f, 4.3f, 2.8f, 1.8f, "#4a3a22");
        E(8.2f, 3.9f, 1.4f, 1.4f, "#5a3a2a");
        Q(8.8f, 3, 9.6f, 1.6f, 10, 1.2f, "#4a3a22", 0.4f);
    }

    // ---------- cây + màu hoa theo mùa (xem statuspets.js) ----------
    /// <summary>Hoa: (cánh, nhuỵ) — thường ngày hồng; Tết hoa mai; Trung thu hoa cúc; Noel hoa trạng nguyên.</summary>
    private static readonly Dictionary<string, (string Petal, string Heart)> FlowerColors = new()
    {
        [""] = ("#ff8fb8", "#ffd23f"), ["tet"] = ("#ffd23f", "#e07b00"), ["trungthu"] = ("#ffb030", "#8a5a20"), ["noel"] = ("#d81b2c", "#ffd23f"),
    };

    private float TreeX() => MathF.Round(Clamp(_W * 0.58f, 40, Math.Max(40, _W - 40)));

    /// <summary>Hoa không mọc đè lên cây.</summary>
    private float FlowerSpot()
    {
        var season = _s.Seasonal ? _env.Season : "";
        var tx = TreeX();
        for (var i = 0; i < 6; i++) { var x = Rnd(30, _W - 30); if (season.Length == 0 || Math.Abs(x - tx - 6) > 22) return x; }
        return Rnd(30, Math.Max(31, tx - 30));
    }

    /// <summary>Cây đứng cố định ở ~58% bề ngang thanh, chân chạm đáy, cao ≤ 20 px thiết kế.</summary>
    private void DrawTree(string season)
    {
        float x = TreeX(), b = _H;
        if (season == "noel")
        {
            using (var br = new SolidBrush(Col("#6b3b12"))) _g.FillRectangle(br, x - 1.2f, b - 3.5f, 2.4f, 3.5f);
            P(new[] { x - 7, b - 3.5f, x + 7, b - 3.5f, x, b - 11 }, "#2e7d32");
            P(new[] { x - 5.6f, b - 8.5f, x + 5.6f, b - 8.5f, x, b - 15 }, "#388e3c");
            P(new[] { x - 4.2f, b - 13, x + 4.2f, b - 13, x, b - 19 }, "#43a047");
            float[][] lights = { new[] { -4f, -5f }, new[] { 2.5f, -6.5f }, new[] { -2f, -9.5f }, new[] { 3f, -11.5f }, new[] { -2.2f, -14f }, new[] { 1.4f, -16f } };
            string[] col = { "#ff5252", "#ffd23f", "#40c4ff", "#ff80ab" };
            for (var i = 0; i < lights.Length; i++)
            {
                var on = MathF.Sin(_t * 3 + i * 1.7f) > -0.2f;
                E(x + lights[i][0], b + lights[i][1], 0.9f, 0.9f, on ? col[i % 4] : "#1b5e20");
            }
            E(x, b - 19.5f, 1.6f, 1.6f, "#ffd23f", "#e0a000", 0.4f);
        }
        else if (season == "tet")
        {
            P(new[] { x - 4, b - 4, x + 4, b - 4, x + 3, b, x - 3, b }, "#b5651d");
            L(x - 4.5f, b - 4, x + 4.5f, b - 4, "#8a4a12", 0.8f);
            Q(x, b - 4, x - 1, b - 11, x + 1, b - 19, "#5d3a1a", 1.3f);
            Q(x - 0.5f, b - 9, x - 4, b - 11, x - 6, b - 15, "#5d3a1a", 0.8f);
            Q(x + 0.5f, b - 12, x + 4, b - 13, x + 6, b - 17, "#5d3a1a", 0.8f);
            float[][] fl = { new[] { x + 1, b - 19 }, new[] { x - 6, b - 15 }, new[] { x + 6, b - 17 }, new[] { x - 3, b - 12.5f }, new[] { x + 3.5f, b - 14 }, new[] { x - 1, b - 16 } };
            for (var i = 0; i < fl.Length; i++)
            {
                var s = 1 + 0.12f * MathF.Sin(_t * 1.5f + i);
                for (var k = 0; k < 5; k++) { var a = k * MathF.PI * 2 / 5; E(fl[i][0] + MathF.Cos(a) * 1.2f * s, fl[i][1] + MathF.Sin(a) * 1.2f * s, 0.9f * s, 0.9f * s, "#ffd23f"); }
                E(fl[i][0], fl[i][1], 0.6f, 0.6f, "#e07b00");
            }
        }
        else if (season == "trungthu")
        {
            L(x, b, x, b - 19, "#8a6a2a", 1.4f);
            Q(x, b - 18, x + 9, b - 14, x + 18, b - 17, "#6b4a1a", 0.5f);
            float[][] ls = { new[] { 5f, -15.6f }, new[] { 10.5f, -14.6f }, new[] { 15.5f, -15.8f } };
            for (var i = 0; i < ls.Length; i++)
            {
                float sw = MathF.Sin(_t * 1.6f + i * 1.3f) * 0.8f, lx = x + ls[i][0] + sw, ly = b + ls[i][1];
                L(x + ls[i][0], ly, lx, ly + 1.6f, "#6b4a1a", 0.4f);
                E(lx, ly + 4, 4, 4, "#ffb03038");
                E(lx, ly + 4, 2, 2.5f, i == 1 ? "#ffb030" : "#e0262b", "#ffd23f", 0.35f);
                L(lx - 1, ly + 6.6f, lx + 1, ly + 6.6f, "#ffd23f", 0.6f);
            }
        }
    }

    private void DrawHat(Pet p)
    {
        var (x, y) = p.Kind == "cat" && p.Mode == "sleep" ? (18.3f, 10.2f) : Hat.TryGetValue(p.Kind, out var h) ? h : (-1f, -1f);
        if (x < 0) return;
        P(new[] { x - 3, y + 0.6f, x + 3, y + 0.6f, x - 1.2f, y - 4.6f }, "#e0262b");
        E(x - 1.4f, y - 4.6f, 1, 1, "#ffffff");
        E(x, y + 0.6f, 3.4f, 0.9f, "#ffffff");
    }

    private static Font? _heartFont, _zFont, _bubbleFont;

    private void DrawBubble(Graphics g)
    {
        var b = _bubble!; var p = b.Pet;
        // Chữ 11 px thiết kế = 11·_k px thật (TextRenderer không theo phép biến đổi của Graphics).
        if (_bubbleFont is null || Math.Abs(_bubbleFont.Size - 11f * _k) > 0.01f) { _bubbleFont?.Dispose(); _bubbleFont = new Font("Segoe UI", 11f * _k, FontStyle.Regular, GraphicsUnit.Pixel); }
        var font = _bubbleFont;
        const TextFormatFlags F = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        float maxW = Math.Max(80, Math.Min(_W * 0.62f, 560));
        var text = b.Text;
        float Measure(string s) => TextRenderer.MeasureText(g, s, font, System.Drawing.Size.Empty, F).Width / _k;   // px thật → px thiết kế
        var tw = Measure(text);
        if (tw > maxW) { while (text.Length > 4 && Measure(text + "…") > maxW) text = text[..^1]; text += "…"; tw = Measure(text); }
        float bw = tw + 14, bh = Math.Min(17, _H - 2);
        var right = p.X + p.W / 2 < _W / 2;
        var x = Clamp(right ? p.X + p.W + 4 : p.X - bw - 4, 1, Math.Max(1, _W - bw - 1));
        var y = Math.Max(1, (_H - bh) / 2);
        var col = b.Kind == "rest" ? ("#e8f6ff", "#5aa9ff", "#0b3a66") : b.Kind == "tip" ? ("#fff8dc", "#d9b54a", "#4a3b00") : ("#ffffff", "#b8c4d0", "#333333");
        _alpha = Clamp(b.Life * 3, 0, 1) * 0.97f;
        using (var path = new GraphicsPath())
        {
            var rr = 8f;
            path.AddArc(x, y, rr, rr, 180, 90); path.AddArc(x + bw - rr, y, rr, rr, 270, 90);
            path.AddArc(x + bw - rr, y + bh - rr, rr, rr, 0, 90); path.AddArc(x, y + bh - rr, rr, rr, 90, 90); path.CloseFigure();
            using var fill = new SolidBrush(Col(col.Item1)); g.FillPath(fill, path);
            using var pen = new Pen(Col(col.Item2), 1f / _k); g.DrawPath(pen, path);
        }
        // Chữ vẽ ở px thật (bỏ phép nhân _k) để nét chữ sắc như chữ của thanh trạng thái.
        var st = g.Save();
        g.ScaleTransform(1 / _k, 1 / _k);
        TextRenderer.DrawText(g, text, font, new Rectangle((int)((x + 7) * _k), (int)(y * _k), (int)((bw - 7) * _k) + 2, (int)(bh * _k)), Col(col.Item3), F | TextFormatFlags.VerticalCenter);
        g.Restore(st);
        _alpha = 1;
    }

    /// <summary>Gọi trong OnPaint của control chủ.</summary>
    public void Paint(Graphics g)
    {
        if (!_s.Enabled || _env.Hidden) return;
        if (_env.Warn) { using var wb = new SolidBrush(Color.FromArgb(217, 0xe0, 0x26, 0x2b)); g.FillRectangle(wb, 0, 0, _host.ClientSize.Width, Math.Max(2, (int)(2 * _k))); }   // DB thật của khách
        if (_dirty.IsEmpty) return;
        _g = g;
        var st = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(_k, _k);
        var season = _s.Seasonal ? _env.Season : "";
        if (season.Length > 0 && _W > 120) DrawTree(season);
        if (_flower is { } fl)
        {
            float s = fl.Grow * Math.Max(0, fl.Life), cx = fl.X, cy = _H - 3 - 6 * s;
            var (petal, heart) = FlowerColors.TryGetValue(season, out var fc) ? fc : FlowerColors[""];   // màu hoa theo mùa
            _alpha = Clamp(fl.Life * 1.5f, 0, 1);
            L(cx, _H, cx, cy, "#4c9a3a", 1);
            for (var k = 0; k < 5; k++) { var a = k * MathF.PI * 2 / 5 + _t * 0.3f; E(cx + MathF.Cos(a) * 2.6f * s, cy + MathF.Sin(a) * 2.6f * s, 1.9f * s, 1.9f * s, petal); }
            E(cx, cy, 1.6f * s, 1.6f * s, heart);
            _alpha = 1;
        }
        _heartFont ??= new Font("Segoe UI Symbol", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
        _zFont ??= new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
        foreach (var q in _parts)
        {
            var a = q.Life / q.Max;
            switch (q.K)
            {
                case "dot": _alpha = Math.Min(1, a) * 0.55f; E(q.X, q.Y, q.R, q.R, q.C); break;
                case "petal": _alpha = Math.Min(1, a * 3) * 0.9f; E(q.X, q.Y, q.R * 1.3f, q.R, q.C, "#e0a000", 0.3f); break;
                case "snow": _alpha = Math.Min(1, a * 3) * 0.95f; E(q.X, q.Y, q.R, q.R, "#ffffff", "#9fb0c2", 0.35f); break;
                case "bubble": _alpha = Math.Min(1, a * 2) * 0.8f; E(q.X, q.Y, q.R, q.R, "#ffffff33", "#7fb8e0", 0.5f); break;
                case "lantern":
                    _alpha = Math.Min(Math.Min(1, a * 2), (1 - a) * 4 + 0.2f);
                    E(q.X, q.Y, 5, 5, "#ffb03040");
                    L(q.X, q.Y - 4.5f, q.X, q.Y - 3, "#8a5a20", 0.5f);
                    E(q.X, q.Y, 2.4f, 3, "#e0262b", "#ffd23f", 0.4f);
                    L(q.X - 1.2f, q.Y + 3, q.X + 1.2f, q.Y + 3, "#ffd23f", 0.8f);
                    break;
                default:
                {
                    _alpha = Math.Min(1, a * 2);
                    using var b = new SolidBrush(Col(q.K == "heart" ? "#ff5a8a" : q.K == "mark" ? q.C : "#8aa0b8"));
                    g.DrawString(q.K == "heart" ? "♥" : q.K == "mark" ? q.Txt : "z", q.K == "heart" ? _heartFont : _zFont, b, q.X, q.Y + 2, center);
                    break;
                }
            }
        }
        _alpha = 1;
        foreach (var p in _pets)
        {
            var bob = p.Mode == "rest" ? MathF.Sin(_t * 9) * 0.3f : 0;
            var ps = g.Save();
            g.TranslateTransform(p.X + p.W / 2, p.Y + p.H / 2 + bob);
            g.RotateTransform(p.Tilt * p.Face * 180 / MathF.PI);
            g.ScaleTransform(p.Face, 1);
            g.TranslateTransform(-p.W / 2, -p.H / 2);
            switch (p.Kind)
            {
                case "bee": DrawBee(p); break;
                case "butterfly": DrawButterfly(p); break;
                case "bird": DrawBird(p); break;
                case "ladybug": DrawLadybug(p); break;
                case "cat": DrawCat(p); break;
                case "snail": DrawSnail(p); break;
                case "fish": DrawFish(p); break;
                case "firefly": DrawFirefly(p); break;
            }
            if (season == "noel" && !(p.Kind == "snail" && p.Mode == "hide")) DrawHat(p);
            g.Restore(ps);
        }
        if (_bubble is not null) DrawBubble(g);
        g.Restore(st);
    }
}

/// <summary>Khung xem trước thú cưng (cửa sổ cài đặt, chế độ App) — trông như thanh trạng thái thu nhỏ.</summary>
public sealed class StatusPetsPreview : Panel
{
    private readonly StatusPets _pets;
    public StatusPetsPreview()
    {
        DoubleBuffered = true;
        Height = NativeSkin.Dens(this, 26);
        _pets = new StatusPets(this);
        Disposed += (_, _) => _pets.Dispose();
    }
    public void Set(StatusPetSettings s) { var c = s.Clone(); c.Enabled = true; _pets.Set(c); }
    public void SetEnv(StatusPetEnv e) { _pets.SetEnv(e); Invalidate(); }
    protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (e.Button == MouseButtons.Left) _pets.Click(e.Location); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); Cursor = _pets.IsOverPet(e.Location) ? Cursors.Hand : Cursors.Default; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var b = new SolidBrush(AppColors.PanelAlt);
        e.Graphics.FillRectangle(b, ClientRectangle);
        using var pn = new Pen(AppColors.Border);
        e.Graphics.DrawRectangle(pn, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, "Workspace: … — Server: …", Font, new Rectangle(10, 0, Width - 20, Height), AppColors.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); _pets.Paint(e.Graphics); }
}
