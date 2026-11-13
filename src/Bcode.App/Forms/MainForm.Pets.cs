using System.Diagnostics;
using System.Runtime.InteropServices;
using Bcode.App.Controls;
using Bcode.App.Models;

namespace Bcode.App.Forms;

/// <summary>
/// Thú cưng trên thanh trạng thái — phần "sống" ngoài engine (Controls/StatusPets.cs, Web/Shell/statuspets.js):
/// <list type="bullet">
/// <item>Môi trường (<see cref="StatusPetEnv"/>): workspace là DB thật của khách, mùa / giờ, Bcode có đang được dùng, tạm ẩn khi chia sẻ màn hình, mẹo.</item>
/// <item>Phản ứng khi chạy SQL Query (ISqlTab.RunningChanged / ScriptExecuted → <see cref="PetEvent"/>).</item>
/// <item>Nhắc nghỉ: đếm thời gian dùng Bcode liên tục (máy rảnh &gt; 5 phút thì tính lại từ đầu); bấm con thú → Giải lao.</item>
/// </list>
/// Một bộ đếm 5 giây làm mọi việc kiểm tra; chỉ gửi xuống thanh trạng thái khi có thay đổi.
/// </summary>
public partial class MainForm
{
    private readonly System.Windows.Forms.Timer _petTimer = new() { Interval = 5000 };
    private string _petEnvSent = "";
    private bool _petHiddenByShare;
    private DateTime _petShareCheckedAt = DateTime.MinValue;
    private double _petUsedSeconds;
    private bool _petRestAsked;
    private string[]? _petTips;
    private int _petSqlRunning;
    private PetalRain? _petalRain;

    private const int PetIdleResetSeconds = 5 * 60;   // máy không có thao tác ≥ 5 phút = đã nghỉ → tính lại thời gian

    /// <summary>Gọi 1 lần lúc mở MainForm (sau khi thanh trạng thái được tạo).</summary>
    private bool _petsInited;

    private void InitPets()
    {
        if (_petsInited) return;
        _petsInited = true;
        // Bật thú cưng từ trước khi có "ngày bắt đầu nuôi": tính từ hôm nay.
        if (_settings.StatusPets is { Enabled: true, AdoptedOn: null } sp)
        {
            sp.AdoptedOn = DateTime.Today;
            try { _settings.Save(); } catch { /* chỉ áp cho phiên này */ }
        }
        Bcode.App.UI.PeachTheme.MarkBar(_toolsBar);   // dải công cụ dưới thanh trên cũng nhuốm hồng đào
        Bcode.App.UI.ThemeManager.ThemeChanged += () => { if (Bcode.App.UI.PeachTheme.On) PushPeach(); };   // đổi sáng / tối: pha lại màu
        _petalRain = new PetalRain(this);   // hoa đào rơi 2 bên (Controls/PetalRain.cs) — bật / tắt theo StatusPetSettings.PetalRain
        Disposed += (_, _) => _petalRain?.Dispose();
        _petTimer.Tick += (_, _) => PetTick();
        _petTimer.Start();
        Disposed += (_, _) => _petTimer.Dispose();
        Activated += (_, _) => PushPetsEnv();
        Deactivate += (_, _) => BeginInvoke(new Action(PushPetsEnv));   // sau Deactivate, Form.ActiveForm mới đúng (null khi sang chương trình khác)
        if (_statusNative is not null) _statusNative.PetRestClicked += () => BeginInvoke(new Action(OnPetRestClicked));
    }

    private StatusPetSettings PetSettings => _settings.StatusPets ?? new StatusPetSettings();

    /// <summary>Gắn tab SQL Query vào thú cưng: bắt đầu chạy / chạy xong / lỗi.</summary>
    private void HookPetsToSqlTab(ISqlTab control)
    {
        control.RunningChanged += running => BeginInvoke(new Action(() =>
        {
            _petSqlRunning = Math.Max(0, _petSqlRunning + (running ? 1 : -1));
            if (running) PetEvent("run");
        }));
        control.ScriptExecuted += (_, _, ok, ms, _) => BeginInvoke(new Action(() =>
        {
            PetEvent(ok ? "ok" : "err", ms);
            if (_petSqlRunning > 1) PetEvent("run");   // tab khác còn đang chạy: mèo vẫn chờ
        }));
    }

    private void PetEvent(string name, int ms = 0)
    {
        if (!PetSettings.Enabled) return;
        if (_statusNative is not null) { _statusNative.PetEvent(name, ms); return; }
        if (_statusBarWeb.CoreWebView2 is null) return;
        _ = _statusBarWeb.CoreWebView2.ExecuteScriptAsync($"window.petEvent && window.petEvent('{name}', {{ ms: {ms} }})");
    }

    /// <summary>Tính lại môi trường; khác lần gửi trước (hoặc <paramref name="force"/>) thì gửi xuống thanh trạng thái.</summary>
    private void PushPetsEnv() => PushPetsEnv(false, null);

    /// <summary>Gửi lại môi trường theo cài đặt <paramref name="s"/> (cửa sổ cài đặt đang thử, chưa lưu) — gọi sau mỗi lần PushPets.</summary>
    private void PushPetsEnvFor(StatusPetSettings s) => PushPetsEnv(true, s);

    private void PushPetsEnv(bool force, StatusPetSettings? over)
    {
        if (IsDisposed) return;
        var s = over ?? PetSettings;
        var now = DateTime.Now;
        var env = new StatusPetEnv
        {
            Warn = s.IsWarnWorkspace(_connections.Current?.Name),
            Season = s.EffectiveSeason(now),   // mùa đã chọn (hoặc theo ngày)
            Night = StatusPetEnv.IsNight(now),
            Active = Form.ActiveForm is not null && WindowState != FormWindowState.Minimized,
            Hidden = s.HideOnShare && _petHiddenByShare,
            Tips = _petTips ??= BuildPetTips(),
        };
        if (_petalRain is not null)
        {
            _petalRain.Apply(s.PetalRain, s.PetalDensity);
            _petalRain.Hidden = s.HideOnShare && _petHiddenByShare;
            _petalRain.Active = env.Active;
        }
        var peach = s.PetalRain && s.PetalBar;
        if (peach != Bcode.App.UI.PeachTheme.On || force) { Bcode.App.UI.PeachTheme.Set(peach); PushPeach(); }
        var key = $"{env.Warn}|{env.Season}|{env.Night}|{env.Active}|{env.Hidden}|{env.Tips.Length}";
        if (!force && key == _petEnvSent) return;
        _petEnvSent = key;
        if (_statusNative is not null) { _statusNative.SetPetsEnv(env); return; }
        if (_statusBarWeb.CoreWebView2 is null) { _petEnvSent = ""; return; }
        var json = System.Text.Json.JsonSerializer.Serialize(env, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        _ = _statusBarWeb.CoreWebView2.ExecuteScriptAsync($"window.setPetsEnv && window.setPetsEnv({json})");
    }

    private void PetTick()
    {
        var s = PetSettings;
        if (!s.Enabled && !s.PetalRain) { _petUsedSeconds = 0; _petRestAsked = false; return; }

        // Chia sẻ màn hình / Xem chung: dò tiến trình mỗi 10 giây (GetProcessesByName theo tên, không duyệt cả máy).
        if (s.HideOnShare && (DateTime.Now - _petShareCheckedAt).TotalSeconds >= 10)
        {
            _petShareCheckedAt = DateTime.Now;
            var names = s.ShareAppList().ToList();
            _ = Task.Run(() =>
            {
                var found = false;
                foreach (var n in names)
                {
                    try { var ps = Process.GetProcessesByName(n); found = ps.Length > 0; foreach (var p in ps) p.Dispose(); }
                    catch { /* không đọc được danh sách tiến trình */ }
                    if (found) break;
                }
                return found;
            }).ContinueWith(t =>
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(new Action(() => { _petHiddenByShare = t.Result; PushPetsEnv(); }));
            }, TaskScheduler.Default);
        }
        else if (!s.HideOnShare) _petHiddenByShare = false;

        if (!s.Enabled) { _petUsedSeconds = 0; _petRestAsked = false; PushPetsEnv(); return; }   // chỉ bật hoa đào: không nhắc nghỉ

        // Nhắc nghỉ.
        var idle = IdleSeconds();
        if (idle >= PetIdleResetSeconds)
        {
            _petUsedSeconds = 0;
            if (_petRestAsked) { _petRestAsked = false; PetEvent("rest-off"); }   // đã đi nghỉ rồi
        }
        else if (s.RestMinutes > 0 && Form.ActiveForm is not null)
        {
            _petUsedSeconds += _petTimer.Interval / 1000.0;
            if (!_petRestAsked && _petUsedSeconds >= s.RestMinutes * 60) { _petRestAsked = true; PetEvent("rest"); }
        }
        if (s.RestMinutes <= 0 && _petRestAsked) { _petRestAsked = false; PetEvent("rest-off"); }

        PushPetsEnv();
    }

    /// <summary>Thanh trên màu hoa đào: bản App tự vẽ lại theo PeachTheme.Changed; bản Web gửi màu xuống topbar.html (window.setPeach), dải công cụ (ToolStrip) vẽ lại.</summary>
    private void PushPeach()
    {
        _toolsBar.Invalidate();
        TopBarControl.Invalidate(true);
        if (_topBarWeb?.CoreWebView2 is not { } wv) return;
        string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        var P = Bcode.App.UI.PeachTheme.On;
        var json = !P ? "null" : System.Text.Json.JsonSerializer.Serialize(new
        {
            bar = Hex(Bcode.App.UI.PeachTheme.Bar), button = Hex(Bcode.App.UI.PeachTheme.Button), hover = Hex(Bcode.App.UI.PeachTheme.Hover),
            line = Hex(Bcode.App.UI.PeachTheme.Line), accent = Hex(Bcode.App.UI.PeachTheme.Accent), accentHover = Hex(Bcode.App.UI.PeachTheme.AccentHover), onAccent = "#ffffff",
        });
        _ = wv.ExecuteScriptAsync($"window.setPeach && window.setPeach({json})");
    }

    private void OnPetRestClicked()
    {
        _petRestAsked = false;
        _petUsedSeconds = 0;
        LaunchGame();
    }

    /// <summary>Mẹo khi bấm vào con thú: phím tắt đang dùng (theo Settings → Phím tắt) + vài cách dùng hay bị bỏ qua. Xáo thứ tự mỗi lần mở Bcode.</summary>
    private static string[] BuildPetTips()
    {
        var tips = new List<string>
        {
            "Ctrl+P: tìm nhanh mọi thứ — tab, công cụ, menu (@), object SQL (#), snippet ($).",
            "Chuột phải object SQL / menu WCommand → Sơ đồ phụ thuộc… để xem sửa chỗ này ảnh hưởng tới đâu.",
            "Lưu file trong BcodeViewer có lịch sử cục bộ — lỡ tay ghi đè thì mở History để lấy lại bản cũ.",
            "Giao diện (Template) → Theme & màu → Thú cưng…: đánh dấu workspace là DB thật của khách để thanh trạng thái có vạch đỏ nhắc.",
            "File Lookup: gõ đúng tên trang .aspx là ra toàn bộ file của menu dùng trang đó.",
            "Giải lao (Tool mở rộng): game LAN cho cả nhóm lúc nghỉ trưa.",
            "Tab SQL Query: bôi đen một đoạn rồi Execute là chỉ chạy đoạn đó.",
        };
        try
        {
            foreach (var d in Bcode.App.UI.ShortcutRegistry.All)
            {
                var key = Bcode.App.UI.ShortcutRegistry.Display(d.Id);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(d.Text)) continue;
                tips.Add($"{key}: {d.Text}");
            }
        }
        catch { /* bảng phím tắt chưa sẵn sàng — dùng mẹo cố định */ }
        var rnd = new Random();
        return tips.Distinct().OrderBy(_ => rnd.Next()).ToArray();
    }

    // ---- thời gian máy không có thao tác (chuột / phím) — GetLastInputInfo ----
    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    private static double IdleSeconds()
    {
        var li = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref li)) return 0;
        return unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0;
    }
}
