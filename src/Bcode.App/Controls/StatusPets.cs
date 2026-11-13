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
    private static readonly string[] Order = { "snail", "ladybug", "cat", "bird", "butterfly", "bee" };
    private static readonly Dictionary<string, (float W, float H)> Size = new()
    {
        ["bee"] = (18, 16), ["butterfly"] = (18, 16), ["bird"] = (20, 14), ["ladybug"] = (14, 10), ["cat"] = (24, 18), ["snail"] = (18, 12),
    };
    private static readonly string[][] Bfly = { new[] { "#ff9a2e", "#ffcf6b" }, new[] { "#5aa9ff", "#b9dcff" }, new[] { "#ff6fae", "#ffc2dc" }, new[] { "#b07cff", "#dcc6ff" } };
    private static readonly string[][] BirdC = { new[] { "#5b8def", "#3d6fd0" }, new[] { "#e0703a", "#b5532a" }, new[] { "#7a8a99", "#58646f" } };
    private static readonly string[][] CatC = { new[] { "#f0a04b", "#c9792c", "#222222" }, new[] { "#9aa0a6", "#6f757b", "#222222" }, new[] { "#3a3a3a", "#1f1f1f", "#c8f060" }, new[] { "#f3efe6", "#cfc7b6", "#4a7fd0" } };

    private sealed class Pet
    {
        public string Kind = "", Mode = "fly", C1 = "", C2 = "", Eye = "";
        public float W, H, X, Y, Vx, Vy, ModeT, Ph, Tx, Ty, Cd, TrailCd, HeartCd, Tilt, Jump, Peck, LoopA, LoopCx, LoopCy, LandX;
        public float Rest, AccX;   // chỉ thú cưng trong editor dùng
        public int Face = 1;
        public bool Moving;
    }
    private sealed class Part { public string K = "dot", C = ""; public float X, Y, Vx, Vy, Life, Max, R; }
    private sealed class Flower { public float X, Grow, Life = 1; public bool Leaving; public Pet? By; }

    private readonly Control _host;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 30 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly Random _rnd = new();
    private StatusPetSettings _s = new();
    private List<Pet> _pets = new();
    private readonly List<Part> _parts = new();
    private Flower? _flower;
    private float _flowerCd = 6, _W, _H, _t, _mouseAt = -10, _k = 1;
    private double _last;
    private PointF? _mouse;
    private Rectangle _dirty;

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
            _timer.Stop(); _pets.Clear(); _parts.Clear(); _flower = null;
            if (!_dirty.IsEmpty) _host.Invalidate(_dirty, true);
            _dirty = Rectangle.Empty;
            return;
        }
        if (!_s.Trail) _parts.RemoveAll(q => q.K == "dot");
        Measure();
        Sync();
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
        var c = kind == "butterfly" ? Pick(Bfly, _rnd) : kind == "bird" ? Pick(BirdC, _rnd) : kind == "cat" ? Pick(CatC, _rnd) : null;
        if (c is not null) { p.C1 = c[0]; p.C2 = c[1]; p.Eye = c.Length > 2 ? c[2] : ""; }
        if (kind is "bee" or "butterfly" or "bird") { p.Y = Rnd(0, Math.Max(0, _H - p.H)); PickTarget(p); }
        else { p.Mode = "walk"; p.Y = Ground(p); p.ModeT = Rnd(2, 6); }
        return p;
    }

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
        if (MouseActive() && p.Mode is "walk" or "stalk" or "sleep")
        {
            if (p.Mode == "sleep") { p.Mode = "wake"; p.ModeT = 0.8f; } else p.Mode = "chase";
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
            if (p.Mode == "walk")
            {
                if (r < 0.45) { p.Mode = "sit"; p.ModeT = Rnd(2, 5); }
                else if (r < 0.65) { p.Mode = "sleep"; p.ModeT = Rnd(8, 18); p.Cd = 0.5f; }
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

    private void Step(Pet p, float dt)
    {
        switch (p.Kind)
        {
            case "bee": case "butterfly": StepNectar(p, dt); break;
            case "bird": StepBird(p, dt); break;
            case "ladybug": StepLadybug(p, dt); break;
            case "cat": StepCat(p, dt); break;
            case "snail": StepSnail(p, dt); break;
        }
    }

    private void World(float dt)
    {
        var lovers = _pets.Any(p => p.Kind is "bee" or "butterfly");
        _flowerCd -= dt;
        if (_s.Flowers && lovers && _flower is null && _flowerCd <= 0 && _W > 120) _flower = new Flower { X = Rnd(30, _W - 30) };
        if (_flower is { } f)
        {
            f.Grow = Math.Min(1, f.Grow + dt * 1.5f);
            if (f.Leaving) f.Life -= dt * 0.8f;
            if (f.By is not null && !_pets.Contains(f.By)) f.By = null;
            if (f.Life <= 0 || !_s.Flowers || !lovers) { _flower = null; _flowerCd = Rnd(15, 35); }
        }
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

    private void Tick()
    {
        if (_host.IsDisposed || !_host.Visible || _host.FindForm() is null or { WindowState: FormWindowState.Minimized }) return;
        var now = _clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Min(now - _last, 0.05); _last = now; _t += dt;
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
        var d = dt * _s.Speed / 100f;
        foreach (var p in _pets.ToList()) Step(p, d);
        World(d);
        // Vùng cần vẽ lại = mọi con + hạt + hoa (cũ ∪ mới).
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        void Grow(float x1, float y1, float x2, float y2) { minX = Math.Min(minX, x1); minY = Math.Min(minY, y1); maxX = Math.Max(maxX, x2); maxY = Math.Max(maxY, y2); }
        foreach (var p in _pets) Grow(p.X - 6, p.Y - 6, p.X + p.W + 6, p.Y + p.H + 6);
        foreach (var q in _parts) Grow(q.X - 6, q.Y - 10, q.X + 6, q.Y + 4);
        if (_flower is { } f) Grow(f.X - 6, _H - 14, f.X + 6, _H);
        var r = minX > maxX ? Rectangle.Empty : Rectangle.FromLTRB((int)(minX * _k) - 1, (int)(minY * _k) - 1, (int)(maxX * _k) + 2, (int)(maxY * _k) + 2);
        var inv = _dirty.IsEmpty ? r : r.IsEmpty ? _dirty : Rectangle.Union(_dirty, r);
        if (!inv.IsEmpty) _host.Invalidate(inv, true);
        _dirty = r;
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

    private static Font? _heartFont, _zFont;

    /// <summary>Gọi trong OnPaint của control chủ.</summary>
    public void Paint(Graphics g)
    {
        if (!_s.Enabled || _dirty.IsEmpty) return;
        _g = g;
        var st = g.Save();
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.ScaleTransform(_k, _k);
        if (_flower is { } fl)
        {
            float s = fl.Grow * Math.Max(0, fl.Life), cx = fl.X, cy = _H - 3 - 6 * s;
            _alpha = Clamp(fl.Life * 1.5f, 0, 1);
            L(cx, _H, cx, cy, "#4c9a3a", 1);
            for (var k = 0; k < 5; k++) { var a = k * MathF.PI * 2 / 5 + _t * 0.3f; E(cx + MathF.Cos(a) * 2.6f * s, cy + MathF.Sin(a) * 2.6f * s, 1.9f * s, 1.9f * s, "#ff8fb8"); }
            E(cx, cy, 1.6f * s, 1.6f * s, "#ffd23f");
            _alpha = 1;
        }
        _heartFont ??= new Font("Segoe UI Symbol", 10f, FontStyle.Regular, GraphicsUnit.Pixel);
        _zFont ??= new Font("Segoe UI", 8f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
        foreach (var q in _parts)
        {
            var a = q.Life / q.Max;
            if (q.K == "dot") { _alpha = Math.Min(1, a) * 0.55f; E(q.X, q.Y, q.R, q.R, q.C); }
            else
            {
                _alpha = Math.Min(1, a * 2);
                using var b = new SolidBrush(Col(q.K == "heart" ? "#ff5a8a" : "#8aa0b8"));
                g.DrawString(q.K == "heart" ? "♥" : "z", q.K == "heart" ? _heartFont : _zFont, b, q.X, q.Y + 2, center);
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
            }
            g.Restore(ps);
        }
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
