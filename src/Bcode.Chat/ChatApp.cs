using System.Runtime.InteropServices;
using Bcode.Chat.Net;
using Microsoft.Web.WebView2.Core;

namespace Bcode.Chat;

/// <summary>
/// Vỏ ứng dụng Bhoo: biểu tượng dưới khay đồng hồ + màn đăng nhập (<see cref="LoginForm"/>) + phiên đang đăng nhập (<see cref="Session"/>).
/// Đăng nhập = chọn Bhoo ID (nickname) → mở dữ liệu của ID đó, bật mạng, chờ ~2 s xem có máy nào trong LAN đang online bằng đúng ID này không
/// (trùng thì huỷ, báo lên màn đăng nhập). Đăng xuất = đóng mọi cửa sổ chat, báo "offline" rồi quay về màn đăng nhập.
/// </summary>
internal sealed class ChatApp : ApplicationContext
{
    private readonly Dictionary<string, string> _opts;
    private readonly NotifyIcon _tray;
    private readonly EventWaitHandle _showEvent;
    private readonly SynchronizationContext _ui;
    private LoginForm? _login;
    private Task<CoreWebView2Environment>? _env;
    private bool _quitting;

    public LoginPrefs Login { get; }
    public Session? Session { get; private set; }
    public Icon AppIcon { get; }
    public bool Quitting => _quitting;

    public ChatApp(Dictionary<string, string> opts, EventWaitHandle showEvent)
    {
        _opts = opts;
        _showEvent = showEvent;
        Login = LoginPrefs.Load(opts.GetValueOrDefault("data"));
        AppIcon = IconArt.Make();
        _login = new LoginForm(this);   // Control đầu tiên → có SynchronizationContext của WinForms
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new NotifyIcon { Icon = AppIcon, Text = "Bhoo — chat LAN", Visible = true };
        _tray.ContextMenuStrip = new ContextMenuStrip();
        _tray.ContextMenuStrip.Opening += (_, _) => BuildTrayMenu();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        _tray.BalloonTipClicked += (_, _) => { if (_tray.Tag is string conv && Session is { } s) s.OpenIm(conv, activate: true); else ShowMain(); };

        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Ui(ShowMain), null, -1, false);

        // --name: vào thẳng (thử nhiều bản trên 1 máy); tự đăng nhập: vào ID lần trước
        var auto = opts.TryGetValue("name", out var n) && Nick.Valid(n) ? n : Login.AutoLogin && Nick.Valid(Login.LastId) ? Login.LastId : null;
        _login.Show();
        if (auto is not null) _ = SignInAsync(auto, Login.Invisible, Login.Remember, Login.AutoLogin);
    }

    private void BuildTrayMenu()
    {
        var m = _tray.ContextMenuStrip!;
        m.Items.Clear();
        if (Session is { } s)
        {
            m.Items.Add($"Mở danh sách bạn ({s.Store.Prefs.Name})", null, (_, _) => ShowMain());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("🟢 Online", null, (_, _) => s.SetMyStatus(Status.Online));
            m.Items.Add("🔴 Bận", null, (_, _) => s.SetMyStatus(Status.Busy));
            m.Items.Add("🟡 Vắng", null, (_, _) => s.SetMyStatus(Status.Away));
            m.Items.Add("⚪ Ẩn (hiện offline)", null, (_, _) => s.SetMyStatus(Status.Invisible));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Đăng xuất", null, (_, _) => SignOut());
        }
        else m.Items.Add("Đăng nhập…", null, (_, _) => ShowMain());
        m.Items.Add("Thoát Bhoo", null, (_, _) => Quit());
    }

    public void Ui(Action a)
    {
        if (_quitting) return;
        _ui.Post(_ => { try { if (!_quitting) a(); } catch { } }, null);
    }

    public Task<CoreWebView2Environment> WebEnv()
    {
        return _env ??= Create();
        static async Task<CoreWebView2Environment> Create()
        {
            var profile = Path.Combine(Path.GetTempPath(), "Bhoo.WebView2", Environment.ProcessId.ToString());
            // Gọi thoại / video (WebRTC) đi thẳng máy-máy trong LAN, không máy chủ STUN / TURN: tắt việc Chromium giấu IP LAN thành tên *.local (mDNS) —
            // mạng văn phòng hay chặn mDNS nên hai máy không thấy nhau (giống Xem chung). Phát tiếng người kia không cần bấm thêm.
            const string args = "--autoplay-policy=no-user-gesture-required --disable-features=WebRtcHideLocalIpsWithMdns";
            CoreWebView2Environment env;
            try { env = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions(args)); }
            catch { env = await CoreWebView2Environment.CreateAsync(userDataFolder: profile); }
            Bcode.Shared.WebViewProfileCleanup.DeleteStaleInBackground(Path.GetDirectoryName(profile)!);
            return env;
        }
    }

    public void ShowMain()
    {
        Form f = Session?.Buddy is { IsDisposed: false } b ? b : (_login ??= new LoginForm(this));
        if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
        f.Show();
        f.Activate();
    }

    public void Balloon(string title, string text, string? conv)
    {
        _tray.Tag = conv;
        _tray.ShowBalloonTip(3000, title, text, ToolTipIcon.None);
    }

    /// <summary>Đăng nhập: null = xong; chuỗi = lỗi để màn đăng nhập hiện.</summary>
    public async Task<string?> SignInAsync(string nick, bool invisible, bool remember, bool autoLogin)
    {
        nick = (nick ?? "").Trim();
        if (!Nick.Valid(nick)) { _login?.Post(new { t = "error", text = Nick.Rule }); return Nick.Rule; }
        if (Session is not null) return null;
        _login?.Post(new { t = "signing", nick });
        var store = new ChatStore(Login.UserDir(nick));
        store.Prefs.Id = Nick.IdOf(nick);
        store.Prefs.Name = nick;
        if (invisible) store.Prefs.Status = Status.Invisible;
        else if (store.Prefs.Status == Status.Invisible) store.Prefs.Status = Status.Online;
        var disc = _opts.TryGetValue("disc", out var ds) && int.TryParse(ds, out var dv) ? dv : Wire.DiscoveryPort;
        var port = _opts.TryGetValue("port", out var ps) && int.TryParse(ps, out var pv) ? pv : Wire.DefaultPort;
        var node = new ChatNode(store, disc, port, _opts.ContainsKey("loopback"));
        if (_opts.TryGetValue("peer", out var peer)) try { node.AddManualPeer(peer); } catch { }
        string? conflict = null;
        void OnConflict(string ip) => conflict ??= ip;
        node.NickConflict += OnConflict;
        try
        {
            node.Start();
            // hỏi cả LAN rồi chờ ~2 s: máy đang dùng nick này sẽ trả lời
            for (var i = 0; i < 4 && conflict is null; i++) { node.Probe(); await Task.Delay(500); }
        }
        catch (Exception ex) { node.Dispose(); var err = ex.Message; _login?.Post(new { t = "error", text = err }); return err; }
        node.NickConflict -= OnConflict;
        if (conflict is not null)
        {
            node.Dispose();
            var err = $"Bhoo ID \"{nick}\" đang được dùng ở máy {conflict} trong mạng LAN. Chọn ID khác, hoặc đăng xuất ở máy kia trước.";
            _login?.Post(new { t = "error", text = err });
            return err;
        }
        Login.Remember = remember;
        Login.AutoLogin = remember && autoLogin;
        Login.Invisible = invisible;
        Login.Remembered(nick);
        Session = new Session(this, store, node);
        _tray.Text = "Bhoo — " + nick;
        var login = _login;
        _login = null;
        Session.Buddy.Show();
        Session.Buddy.Activate();
        if (login is not null) { login.AllowClose = true; login.Close(); }
        return null;
    }

    public void SignOut()
    {
        if (Session is not { } s) return;
        Session = null;
        s.Close();
        _tray.Text = "Bhoo — chat LAN";
        Login.AutoLogin = false;   // đăng xuất tay thì lần mở sau không tự vào lại
        Login.Save();
        _login = new LoginForm(this);
        _login.Show();
    }

    public void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _tray.Visible = false;
        try { Session?.Close(); } catch { }
        if (_login is not null) { _login.AllowClose = true; try { _login.Close(); } catch { } }
        _tray.Dispose();
        ExitThread();
    }
}

/// <summary>Biểu tượng khay / cửa sổ của Bhoo: mặt cười vàng trong bong bóng chat xanh mực (vẽ bằng GDI lúc chạy — không có file .ico).</summary>
internal static class IconArt
{
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    public static Icon Make()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bubble = new System.Drawing.Drawing2D.GraphicsPath();
            bubble.AddEllipse(1, 1, 29, 25);
            bubble.AddPolygon(new[] { new Point(6, 20), new Point(3, 31), new Point(14, 24) });
            using (var b = new SolidBrush(Color.FromArgb(31, 72, 130))) g.FillPath(b, bubble);
            using (var b = new SolidBrush(Color.FromArgb(255, 196, 0))) g.FillEllipse(b, 7, 4, 18, 18);
            using (var pen = new Pen(Color.FromArgb(120, 70, 0), 1.6f)) g.DrawEllipse(pen, 7, 4, 18, 18);
            using (var b = new SolidBrush(Color.FromArgb(40, 30, 0))) { g.FillEllipse(b, 11, 9, 3, 4); g.FillEllipse(b, 18, 9, 3, 4); }
            using (var pen = new Pen(Color.FromArgb(40, 30, 0), 1.6f)) g.DrawArc(pen, 11, 10, 10, 8, 20, 140);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }
}
