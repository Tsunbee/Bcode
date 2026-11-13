using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Bcode.App.Models;

namespace Bcode.App.Controls;

// Thú cưng trong editor SQL Query, chế độ App (Scintilla) — bản native của statuspets.js StatusPets.editor (giữ 2 bản giống nhau khi sửa:
// cùng tên hàm, cùng con số). Hình vẽ mượn của StatusPets (DrawBee, DrawCat...). Cài đặt: StatusPetSettings.EditorStyle / EditorKind / EditorScale.

public sealed partial class StatusPets
{
    /// <summary>Bản chỉ để mượn hình vẽ (không có control chủ, không chạy bộ đếm giờ).</summary>
    private StatusPets() { _host = null!; }

    private void DrawOne(Graphics g, Pet p, float t)
    {
        _g = g; _t = t; _alpha = 1;
        switch (p.Kind)
        {
            case "bee": DrawBee(p); break;
            case "butterfly": DrawButterfly(p); break;
            case "bird": DrawBird(p); break;
            case "ladybug": DrawLadybug(p); break;
            case "cat": DrawCat(p); break;
            case "snail": DrawSnail(p); break;
        }
    }

    /// <summary>1 con thú trong editor: trạng thái + bước + vẽ. Toạ độ = px màn hình; k = cỡ (EditorScale) × DPI.</summary>
    internal sealed class EditorEngine
    {
        private static readonly Dictionary<string, (float X, float Y)> Nose = new()   // mũi (hình quay phải)
        {
            ["bee"] = (17.4f, 9.5f), ["butterfly"] = (16, 9.5f), ["bird"] = (20, 7), ["ladybug"] = (14, 7), ["cat"] = (23, 8.6f), ["snail"] = (17.5f, 3),
        };
        private static readonly Dictionary<string, float> Follow = new()   // tốc độ tối đa khi theo chuột (px thiết kế / giây)
        {
            ["bee"] = 420, ["butterfly"] = 300, ["bird"] = 520, ["ladybug"] = 160, ["cat"] = 360, ["snail"] = 60,
        };
        private static readonly Dictionary<string, string> RestMode = new()
        {
            ["bee"] = "rest", ["butterfly"] = "rest", ["bird"] = "hop", ["ladybug"] = "idle", ["cat"] = "sit", ["snail"] = "idle",
        };
        private static readonly HashSet<string> FlierKinds = new() { "bee", "butterfly", "bird" };
        public const int Cur = 32;   // cỡ ảnh con trỏ (px thiết kế, nhân DPI)
        public const int Pad = 20;   // lề quanh con thú trên ảnh 'follow' (chỗ cho tim / zZ)

        private readonly StatusPets _core = new();
        private readonly Random _rnd = new();
        private readonly List<Part> _parts = new();
        private Pet? _p;
        private float _t, _mouseAt = -100;
        private PointF? _mouse;

        public string? Kind => _p?.Kind;
        public bool Moving => _p?.Moving == true;
        public float T => _t;
        public float Idle => _t - _mouseAt;

        private float Rnd(float a, float b) => a + (float)_rnd.NextDouble() * (b - a);

        public void Advance(float dt) => _t += dt;

        /// <summary>Chuột trong editor (px màn hình); <paramref name="cursorStyle"/> = kiểu 'cursor' (quay mặt theo hướng rê).</summary>
        public void MouseAt(PointF m, bool cursorStyle)
        {
            if (_mouse is { } old && _p is { } p)
            {
                p.AccX += m.X - old.X;
                if (Math.Abs(p.AccX) > 6) { if (cursorStyle) p.Face = p.AccX > 0 ? 1 : -1; p.AccX = 0; }
            }
            if (_mouse is not { } o || Math.Abs(m.X - o.X) + Math.Abs(m.Y - o.Y) > 0.5f) _mouseAt = _t;
            _mouse = m;
        }

        public void NewPet(string kind, RectangleF r, float k)
        {
            var sz = Size[kind];
            var c = kind == "butterfly" ? Pick(Bfly, _rnd) : kind == "bird" ? Pick(BirdC, _rnd) : kind == "cat" ? Pick(CatC, _rnd) : null;
            _p = new Pet
            {
                Kind = kind, W = sz.W, H = sz.H, Face = -1, Mode = RestMode[kind], Ph = Rnd(0, 10), Rest = 1,
                X = _mouse is { } m ? m.X + 10 * k : r.Right - sz.W * k - 30 * k,
                Y = _mouse is { } m2 ? m2.Y + 12 * k : r.Bottom - sz.H * k - 8 * k,
                C1 = c?[0] ?? "", C2 = c?[1] ?? "", Eye = c is { Length: > 2 } ? c[2] : "",
            };
            _parts.Clear();
        }

        private void SleepZ(Pet p, float dt, float bw, float k)
        {
            p.Cd -= dt;
            if (p.Cd <= 0) { p.Cd = 1.1f; _parts.Add(new Part { K = "z", X = p.X + (p.Face > 0 ? bw - 4 * k : 4 * k), Y = p.Y + 6 * k, Vx = p.Face * 3 * k, Vy = -6 * k, Life = 2, Max = 2 }); }
        }

        private void BirdIdle(Pet p, float dt)
        {
            if (p.Kind != "bird" || p.Mode != "hop") return;
            p.Cd -= dt;
            if (p.Cd <= 0) { p.Cd = Rnd(0.8f, 2.2f); p.Peck = 0.25f; }
            if (p.Peck > 0) { p.Peck -= dt; p.Tilt = 0.5f; }
        }

        /// <summary>Theo chuột: đích = lệch dưới-phải mũi chuột (chừa chữ I), kẹp trong editor <paramref name="r"/>.</summary>
        public void StepFollow(float dt, RectangleF r, float k)
        {
            if (_p is not { } p) return;
            float bw = p.W * k, bh = p.H * k, idle = _t - _mouseAt;
            float tx = _mouse is { } m ? m.X + 10 * k : p.X, ty = _mouse is { } m2 ? m2.Y + 12 * k : p.Y;
            tx = Clamp(tx, r.Left, Math.Max(r.Left, r.Right - bw)); ty = Clamp(ty, r.Top, Math.Max(r.Top, r.Bottom - bh - 2 * k));
            float dx = tx - p.X, dy = ty - p.Y, dist = Hypot(dx, dy);
            var walker = !FlierKinds.Contains(p.Kind);
            if (p.Mode == "sleep")
            {
                if (idle > 0.3f) { SleepZ(p, dt, bw, k); return; }
                p.Mode = "wake"; p.Cd = 0.5f;
                _parts.Add(new Part { K = "heart", X = p.X + bw / 2, Y = p.Y, Vx = 0, Vy = -14 * k, Life = 1.2f, Max = 1.2f });
            }
            if (p.Mode == "wake") { p.Cd -= dt; if (p.Cd > 0) return; p.Mode = RestMode[p.Kind]; }
            if (!p.Moving && dist > 8 * k) p.Moving = true;
            if (p.Moving && dist < 2 * k && (walker || Hypot(p.Vx, p.Vy) < 40 * k)) { p.Moving = false; p.Rest = 0; }
            if (p.Moving)
            {
                var max = Follow[p.Kind] * k;
                p.Mode = p.Kind == "cat" ? (dist > 40 * k ? "chase" : "walk")
                    : p.Kind == "ladybug" ? (dist > 120 * k || (p.Mode == "air" && dist > 10 * k) ? "air" : "walk")
                    : p.Kind == "snail" ? "walk" : "fly";
                if (p.Mode is "fly" or "air")   // lò xo hơi lố + rung nhẹ (như FlyTo)
                {
                    var wob = MathF.Sin((_t + p.Ph) * 5) * 2 * k;
                    p.Vx += (dx * 9 - p.Vx * 4.5f) * dt; p.Vy += ((dy + wob) * 9 - p.Vy * 4.5f) * dt;
                    var sp = Hypot(p.Vx, p.Vy);
                    if (sp > max) { p.Vx *= max / sp; p.Vy *= max / sp; }
                    p.X += p.Vx * dt; p.Y += p.Vy * dt;
                    p.Tilt = Clamp(MathF.Atan2(p.Vy, Math.Abs(p.Vx) + 20 * k), -0.45f, 0.45f);
                    if (Math.Abs(p.Vx) > 10 * k) p.Face = p.Vx > 0 ? 1 : -1;
                }
                else                            // bò / chạy thẳng tới đích
                {
                    var s = Math.Min(dist, max * dt);
                    p.Vx = dx / dist * max; p.Vy = dy / dist * max;
                    p.X += dx / dist * s; p.Y += dy / dist * s; p.Tilt = 0;
                    if (Math.Abs(dx) > 1) p.Face = dx > 0 ? 1 : -1;
                }
                return;
            }
            p.Vx = p.Vy = 0; p.Tilt = 0; p.Rest += dt;
            if (p.Rest > 0.5f && p.Mode != "sleep")
            {
                if (p.Mode is "fly" or "air" or "walk" or "chase") p.Mode = RestMode[p.Kind];
                if (_mouse is { } mm) p.Face = mm.X < p.X + bw / 2 ? -1 : 1;   // ngoảnh về phía chuột
            }
            if (p.Kind == "cat" && p.Mode == "sit" && idle > 20) { p.Mode = "sleep"; p.Cd = 0.5f; }
            BirdIdle(p, dt);
        }

        /// <summary>Thay con trỏ: chuột vừa di chuyển = chạy / bay, đứng yên = nghỉ, lâu = mèo ngủ.</summary>
        public void StepCursor(float dt)
        {
            if (_p is not { } p) return;
            var idle = _t - _mouseAt;
            p.Moving = idle < 0.25f; p.Tilt = 0;
            if (p.Moving) { p.Mode = p.Kind == "cat" ? "chase" : p.Kind is "ladybug" or "snail" ? "walk" : "fly"; p.Rest = 0; return; }
            p.Rest += dt;
            p.Mode = p.Kind == "cat" && idle > 20 ? "sleep" : RestMode[p.Kind];
            BirdIdle(p, dt);
        }

        public void StepParts(float dt)
        {
            for (var i = _parts.Count - 1; i >= 0; i--)
            {
                var q = _parts[i];
                q.Life -= dt;
                if (q.Life <= 0) { _parts.RemoveAt(i); continue; }
                q.X += q.Vx * dt; q.Y += q.Vy * dt;
            }
        }

        private void DrawBody(Graphics g, float x, float y, float k)
        {
            var p = _p!;
            var bob = p.Mode == "rest" ? MathF.Sin(_t * 9) * 0.3f * k : 0;
            var st = g.Save();
            g.TranslateTransform(x + p.W * k / 2, y + p.H * k / 2 + bob);
            g.RotateTransform(p.Tilt * p.Face * 180 / MathF.PI);
            g.ScaleTransform(p.Face * k, k);
            g.TranslateTransform(-p.W / 2, -p.H / 2);
            _core.DrawOne(g, p, _t);
            g.Restore(st);
        }

        private static readonly Dictionary<int, Font> Fonts = new();
        private static Font FontOf(bool heart, float px)
        {
            var key = (int)MathF.Round(px * 4) * 2 + (heart ? 1 : 0);
            if (!Fonts.TryGetValue(key, out var f))
                Fonts[key] = f = heart ? new Font("Segoe UI Symbol", px, FontStyle.Regular, GraphicsUnit.Pixel) : new Font("Segoe UI", px, FontStyle.Bold, GraphicsUnit.Pixel);
            return f;
        }

        /// <summary>Ảnh kiểu 'follow' (ARGB, có lề <see cref="Pad"/>); <paramref name="topLeft"/> = góc trên trái trên màn hình. <paramref name="dk"/> = DPI.</summary>
        public Bitmap RenderFollow(float k, float dk, out Point topLeft)
        {
            var p = _p!;
            float bw = p.W * k, bh = p.H * k, pad = Pad * dk;
            int cw = (int)MathF.Ceiling(bw + pad * 2), ch = (int)MathF.Ceiling(bh + pad * 2);
            int ox = (int)MathF.Round(p.X - pad), oy = (int)MathF.Round(p.Y - pad);
            topLeft = new Point(ox, oy);
            var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Far };
            foreach (var q in _parts)
            {
                var a = Math.Min(1, q.Life / q.Max * 2);
                var heart = q.K == "heart";
                using var b = new SolidBrush(Color.FromArgb((int)(255 * a), heart ? Color.FromArgb(0xff, 0x5a, 0x8a) : Color.FromArgb(0x8a, 0xa0, 0xb8)));
                g.DrawString(heart ? "♥" : "z", FontOf(heart, (heart ? 12 : 10) * dk), b, q.X - ox, q.Y - oy + 2 * dk, center);
            }
            DrawBody(g, p.X - ox, p.Y - oy, k);
            return bmp;
        }

        /// <summary>Ảnh con trỏ (ARGB, cạnh = <see cref="Cur"/> × DPI): mũi con thú = điểm bấm (chấm đỏ), quay theo hướng rê chuột.</summary>
        public Bitmap RenderCursor(float scale, float dk, out int hx, out int hy)
        {
            var p = _p!;
            float cur = Cur * dk, k = Math.Min(scale * dk, Math.Min((cur - 2 * dk) / p.W, (cur - 2 * dk) / p.H));
            var px = (int)MathF.Round(cur);
            float bw = p.W * k, bh = p.H * k;
            var nose = Nose[p.Kind];
            float ox = p.Face > 0 ? cur - dk - bw : dk, oy = (cur - bh) / 2;
            hx = (int)MathF.Round(Clamp(ox + (p.Face > 0 ? nose.X : p.W - nose.X) * k, dk, cur - 2 * dk));
            hy = (int)MathF.Round(Clamp(oy + nose.Y * k, dk, cur - 2 * dk));
            var bmp = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            DrawBody(g, ox, oy, k);
            using (var w = new SolidBrush(Color.White)) g.FillEllipse(w, hx + 0.5f - 2 * dk, hy + 0.5f - 2 * dk, 4 * dk, 4 * dk);
            using (var r = new SolidBrush(Color.FromArgb(0xe0, 0x26, 0x2b))) g.FillEllipse(r, hx + 0.5f - 1.2f * dk, hy + 0.5f - 1.2f * dk, 2.4f * dk, 2.4f * dk);
            return bmp;
        }
    }
}

/// <summary>
/// Thú cưng trong editor SQL Query ở chế độ App — Settings → "Thú cưng (thanh trạng thái, SQL Query)...". Một bộ đếm giờ chung cho mọi
/// editor đã <see cref="Attach"/> (NativeSqlControl gắn Scintilla; cửa sổ cài đặt gắn khung xem trước); kiểu "off" thì dừng hẳn, không tốn gì.
/// · 'follow': 1 cửa sổ lớp nhỏ (WS_EX_LAYERED | WS_EX_TRANSPARENT — chuột xuyên qua, không bao giờ nhận focus), owned bởi form chứa editor,
///   chỉ hiện khi form đó đang active; dời theo chuột bằng UpdateLayeredWindow — không vẽ đè lên Scintilla nên không làm khựng khi gõ.
/// · 'cursor': HCURSOR vẽ từ hình con thú (CreateIconIndirect), đặt lại SAU Scintilla ở WM_SETCURSOR / WM_MOUSEMOVE trong vùng chữ
///   (lề số dòng giữ con trỏ gốc). Chế độ Web: statuspets.js StatusPets.editor trong sqleditor.html (RawSqlControl.PushEditorPet).
/// </summary>
public static class EditorPets
{
    /// <summary>Cài đặt hiện tại (MainForm.PushPets gọi <see cref="Apply"/> lúc mở + mỗi lần đổi trong cửa sổ cài đặt).</summary>
    public static StatusPetSettings Settings { get; private set; } = new();

    /// <summary>Kiểu / loài / cỡ trong editor vừa đổi — RawSqlControl đẩy xuống trang editor.</summary>
    public static event Action? Changed;

    private sealed class Host
    {
        public Control C = null!;
        public Func<Point, bool> InText = null!;
        public CursorHook? Hook;
    }

    private static readonly List<Host> Hosts = new();
    private static readonly System.Windows.Forms.Timer Timer = new() { Interval = 30 };
    private static readonly System.Diagnostics.Stopwatch Clock = new();
    private static StatusPets.EditorEngine? _eng;
    private static Host? _cur;
    private static PetLayer? _layer;
    private static IntPtr _hcur;
    private static readonly List<IntPtr> OldCursors = new();
    private static double _last;
    private static bool _inside;
    private static float _nextCursor;

    static EditorPets() { Timer.Tick += (_, _) => Tick(); }

    public static void Apply(StatusPetSettings s)
    {
        var n = s.Clone().Normalize();
        var same = n.EditorStyle == Settings.EditorStyle && n.EditorKind == Settings.EditorKind && n.EditorScale == Settings.EditorScale;
        Settings = n;
        if (same) return;
        Changed?.Invoke();
        Restart();
    }

    /// <summary>Gắn 1 editor; <paramref name="inText"/> (toạ độ client) = điểm thuộc vùng chữ — kiểu 'cursor' chỉ thay con trỏ ở đó.</summary>
    public static void Attach(Control c, Func<Point, bool>? inText = null)
    {
        var h = new Host { C = c, InText = inText ?? (_ => true) };
        Hosts.Add(h);
        void Hook() { if (h.Hook is null && c.IsHandleCreated) { h.Hook = new CursorHook(h); h.Hook.AssignHandle(c.Handle); } }
        c.HandleCreated += (_, _) => Hook();
        c.HandleDestroyed += (_, _) => { h.Hook?.ReleaseHandle(); h.Hook = null; };
        c.Disposed += (_, _) =>
        {
            Hosts.Remove(h);
            if (_cur == h) { _cur = null; _layer?.Hide(); }
            if (Hosts.Count == 0) Restart();
        };
        Hook();
        Restart();
    }

    /// <summary>Vùng chữ của Scintilla = bên phải các lề (số dòng, glyph...).</summary>
    public static Func<Point, bool> ScintillaTextArea(Control ed) => pt =>
    {
        if (!ed.IsHandleCreated) return false;
        var hw = ed.Handle;
        var x = (int)SendMessage(hw, 2156 /*SCI_GETMARGINLEFT*/, 0, 0);
        var n = (int)SendMessage(hw, 2253 /*SCI_GETMARGINS*/, 0, 0);
        for (var i = 0; i < n; i++) x += (int)SendMessage(hw, 2243 /*SCI_GETMARGINWIDTHN*/, i, 0);
        return pt.X >= x && pt.Y >= 0 && pt.X < ed.ClientSize.Width && pt.Y < ed.ClientSize.Height;
    };

    private static string Style => Settings.EditorStyle;

    private static void Restart()
    {
        var on = Settings.EditorOn && Hosts.Count > 0;
        if (Style != "follow") _layer?.Hide();
        if (Style != "cursor") DropCursor();
        if (!on) { Timer.Stop(); _eng = null; return; }
        if (_eng is null || _eng.Kind != Settings.EditorKind)
        {
            _eng ??= new StatusPets.EditorEngine();
            var r = _cur is { } c && c.C.IsHandleCreated ? c.C.RectangleToScreen(c.C.ClientRectangle) : Rectangle.Empty;
            _eng.NewPet(Settings.EditorKind, r, K(_cur?.C));
        }
        _nextCursor = 0;
        if (!Timer.Enabled) { Clock.Restart(); _last = 0; Timer.Interval = 30; Timer.Start(); }
    }

    private static float Dk(Control? c) => (c?.DeviceDpi ?? 96) / 96f;
    private static float K(Control? c) => Dk(c) * Settings.EditorScale / 100f;

    private static void Tick()
    {
        if (_eng is null || !Settings.EditorOn) { Timer.Stop(); return; }
        var now = Clock.Elapsed.TotalSeconds;
        var dt = (float)Math.Min(now - _last, Style == "cursor" ? 0.4 : 0.05); _last = now;
        _eng.Advance(dt);
        _eng.StepParts(dt);

        // Editor dưới chuột (đang hiện, đúng cửa sổ dưới chuột) → editor hiện hành.
        var mp = Cursor.Position;
        var under = WindowFromPoint(mp);
        Host? over = null;
        foreach (var h in Hosts)
        {
            if (h.C.IsDisposed || !h.C.IsHandleCreated || !h.C.Visible) continue;
            if (!h.C.RectangleToScreen(h.C.ClientRectangle).Contains(mp)) continue;
            if (under != h.C.Handle && !IsChild(h.C.Handle, under)) continue;
            over = h; break;
        }
        if (over is not null)
        {
            _eng.MouseAt(mp, Style == "cursor");
            if (_cur != over && Style == "follow") _eng.NewPet(Settings.EditorKind, over.C.RectangleToScreen(over.C.ClientRectangle), K(over.C));   // sang editor khác: hiện ngay cạnh chuột
            _cur = over; _inside = true;
        }
        else _inside = false;
        if (_cur is { } cur && (cur.C.IsDisposed || !cur.C.Visible)) { _cur = null; _layer?.Hide(); }
        if (_cur is null) { Timer.Interval = 120; return; }

        if (Style == "follow")
        {
            Timer.Interval = 30;
            var form = _cur.C.FindForm();
            if (form is null || Form.ActiveForm != form || form.WindowState == FormWindowState.Minimized) { _layer?.Hide(); return; }
            var k = K(_cur.C);
            _eng.StepFollow(dt, _cur.C.RectangleToScreen(_cur.C.ClientRectangle), k);
            using var bmp = _eng.RenderFollow(k, Dk(_cur.C), out var at);
            if (_layer is null || _layer.Owner != form.Handle) { _layer?.Dispose(); _layer = new PetLayer(form.Handle); }
            _layer.Show(bmp, at);
            return;
        }

        // 'cursor'
        _eng.StepCursor(dt);
        _nextCursor -= dt;
        if (!_inside) { Timer.Interval = 120; return; }
        Timer.Interval = _eng.Moving ? 90 : 240;
        if (_nextCursor > 0) return;
        _nextCursor = _eng.Moving ? 0.08f : 0.22f;
        using (var bmp = _eng.RenderCursor(Settings.EditorScale / 100f, Dk(_cur.C), out var hx, out var hy))
        {
            var hc = MakeCursor(bmp, hx, hy);
            if (hc == IntPtr.Zero) return;
            var old = _hcur;
            _hcur = hc;
            if (_cur.InText(_cur.C.PointToClient(mp))) SetCursor(hc);
            if (old != IntPtr.Zero) OldCursors.Add(old);
        }
        FreeOldCursors();
    }

    /// <summary>Con trỏ đang dùng cho editor (kiểu 'cursor', điểm trong vùng chữ) — 0 = để Scintilla tự đặt.</summary>
    private static IntPtr CursorFor(Host h, Point client) =>
        Style == "cursor" && _hcur != IntPtr.Zero && h.InText(client) ? _hcur : IntPtr.Zero;

    private static void DropCursor()
    {
        if (_hcur == IntPtr.Zero) return;
        var old = _hcur;
        _hcur = IntPtr.Zero;
        if (GetCursor() == old) SetCursor(Cursors.IBeam.Handle);
        OldCursors.Add(old);
        FreeOldCursors();
    }

    private static void FreeOldCursors()
    {
        var shown = GetCursor();
        for (var i = OldCursors.Count - 1; i >= 0; i--)
            if (OldCursors[i] != shown) { DestroyIcon(OldCursors[i]); OldCursors.RemoveAt(i); }
    }

    /// <summary>Đặt lại con trỏ sau khi Scintilla tự đặt chữ I (nó gọi SetCursor cả ở WM_MOUSEMOVE, không chỉ WM_SETCURSOR).</summary>
    private sealed class CursorHook : NativeWindow
    {
        private readonly Host _h;
        public CursorHook(Host h) => _h = h;
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            const int WM_SETCURSOR = 0x0020, WM_MOUSEMOVE = 0x0200, HTCLIENT = 1;
            if (m.Msg == WM_SETCURSOR ? ((int)(long)m.LParam & 0xFFFF) != HTCLIENT : m.Msg != WM_MOUSEMOVE) return;
            if (_hcur == IntPtr.Zero || Style != "cursor") return;
            var hc = CursorFor(_h, _h.C.PointToClient(Cursor.Position));
            if (hc == IntPtr.Zero) return;
            SetCursor(hc);
            if (m.Msg == WM_SETCURSOR) m.Result = (IntPtr)1;
        }
    }

    /// <summary>Cửa sổ lớp trong suốt cho kiểu 'follow' — chuột xuyên qua, không nhận focus, không hiện trên taskbar.</summary>
    private sealed class PetLayer : NativeWindow, IDisposable
    {
        public readonly IntPtr Owner;
        private bool _shown;
        public PetLayer(IntPtr owner)
        {
            Owner = owner;
            CreateHandle(new CreateParams
            {
                Caption = "",
                Style = unchecked((int)0x80000000),                        // WS_POPUP
                ExStyle = 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000, // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE
                Parent = owner,                                             // popup + Parent = owner: luôn nằm trên form chứa editor
                Width = 1, Height = 1,
            });
        }

        public void Show(Bitmap bmp, Point at)
        {
            if (Handle == IntPtr.Zero) return;
            var screen = GetDC(IntPtr.Zero);
            var mem = CreateCompatibleDC(screen);
            var hbm = MakeDib(bmp);
            var old = SelectObject(mem, hbm);
            try
            {
                var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
                var src = new POINT();
                var dst = new POINT { x = at.X, y = at.Y };
                var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2 /*ULW_ALPHA*/);
            }
            finally
            {
                SelectObject(mem, old);
                DeleteObject(hbm);
                DeleteDC(mem);
                ReleaseDC(IntPtr.Zero, screen);
            }
            if (!_shown) { ShowWindow(Handle, 4 /*SW_SHOWNOACTIVATE*/); _shown = true; }
        }

        public void Hide() { if (_shown && Handle != IntPtr.Zero) ShowWindow(Handle, 0); _shown = false; }

        public void Dispose() { if (Handle != IntPtr.Zero) DestroyHandle(); }
    }

    // ---------- Win32 ----------
    /// <summary>Bitmap → DIB 32bpp top-down (giữ nguyên kênh alpha; Format32bppPArgb thì dữ liệu đã nhân alpha — đúng cho UpdateLayeredWindow).</summary>
    private static IntPtr MakeDib(Bitmap bmp)
    {
        var bi = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = bmp.Width, biHeight = -bmp.Height, biPlanes = 1, biBitCount = 32 };
        var hbm = CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
        if (hbm == IntPtr.Zero) return IntPtr.Zero;
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            var row = new byte[bmp.Width * 4];
            for (var y = 0; y < bmp.Height; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                Marshal.Copy(row, 0, bits + y * bmp.Width * 4, row.Length);
            }
        }
        finally { bmp.UnlockBits(data); }
        return hbm;
    }

    private static IntPtr MakeCursor(Bitmap bmp, int hx, int hy)
    {
        var color = MakeDib(bmp);
        if (color == IntPtr.Zero) return IntPtr.Zero;
        var stride = (bmp.Width + 15) / 16 * 2;
        var mask = CreateBitmap(bmp.Width, bmp.Height, 1, 1, new byte[stride * bmp.Height]);
        try
        {
            var ii = new ICONINFO { fIcon = false, xHotspot = hx, yHotspot = hy, hbmMask = mask, hbmColor = color };
            return CreateIconIndirect(ref ii);
        }
        finally { DeleteObject(color); DeleteObject(mask); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO { [MarshalAs(UnmanagedType.Bool)] public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point pt);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr SetCursor(IntPtr hCursor);
    [DllImport("user32.dll")] private static extern IntPtr GetCursor();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr hIcon);   // con trỏ tạo bằng CreateIconIndirect huỷ bằng DestroyIcon
    [DllImport("user32.dll")] private static extern IntPtr CreateIconIndirect(ref ICONINFO info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateBitmap(int w, int h, uint planes, uint bpp, byte[] bits);
}
