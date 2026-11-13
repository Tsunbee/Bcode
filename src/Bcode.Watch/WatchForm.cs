using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Bcode.Shared;
using Bcode.Watch.Net;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.Watch;

/// <summary>Cửa sổ "Xem chung": 1 WebView2 chạy Web\index.html (sảnh + rạp). Trang nói với C# bằng postMessage:
/// tin "ui.*" xử lý tại đây (mở / tìm / vào phòng, chọn file trên máy, tường lửa), tin "net" chuyển nguyên văn cho máy chủ phòng;
/// tin máy chủ gửi về được đẩy thẳng xuống trang (PostWebMessageAsJson).
/// Trang chạy ở http://watch.local (KHÔNG https): file chủ phòng phát qua http://ip:cổng — trang https sẽ chặn "mixed content".</summary>
public sealed class WatchForm : Form
{
    private const string VirtualHost = "watch.local";
    private const string Title = "Rạp Bcode — Xem chung";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, string> _opts;
    private readonly Prefs _prefs;
    private CoreWebView2? _core;
    private WatchServer? _server;
    private MediaServer? _media;
    private WatchClient? _client;
    private RoomFinder? _finder;
    private bool _leaving;
    private readonly bool _hidden;
    private FormWindowState _preFullState;
    private FormBorderStyle _preFullBorder;

    protected override bool ShowWithoutActivation => _hidden;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (_hidden) cp.ExStyle |= 0x08000000 | 0x80;   // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW: không giành focus, không có trong Alt+Tab
            return cp;
        }
    }

    public WatchForm(Dictionary<string, string> opts)
    {
        _opts = opts;
        _prefs = Prefs.Load(opts.TryGetValue("data", out var d) ? d : null);
        if (opts.TryGetValue("name", out var n)) _prefs.Name = n;
        Text = Title;
        Width = 1400; Height = 900;
        MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(12, 10, 16);
        if (opts.TryGetValue("pos", out var pos) && pos.Split(',').Select(s => int.TryParse(s, out var v) ? (int?)v : null).ToArray() is { Length: 4 } r && r.All(v => v.HasValue))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(r[0]!.Value, r[1]!.Value, Math.Max(960, r[2]!.Value), Math.Max(600, r[3]!.Value));
        }
        _hidden = opts.ContainsKey("hidden");
        if (_hidden)
        {
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Location = new Point(SystemInformation.VirtualScreen.Right + 4000, SystemInformation.VirtualScreen.Bottom + 4000);
        }
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
        FormClosing += OnClosing;
        FormClosed += (_, _) =>
        {
            // chờ (≤ 1,6 s) người xem nhận được "Chủ phòng đã kết thúc buổi chiếu" — thoát ngay thì tin chưa kịp gửi, họ chỉ thấy "Mất kết nối"
            var closing = LeaveRoom();
            try { closing.Wait(1600); } catch { }
            try { _web.Dispose(); } catch { }
        };
    }

    private async Task InitAsync()
    {
        try
        {
            var profile = Path.Combine(Path.GetTempPath(), "BcodeWatch.WebView2", Environment.ProcessId.ToString());
            // Local Network Access / Private Network Access: trang watch.local (không có IP thật) tải video từ IP LAN / 127.0.0.1 của chủ phòng — tắt kiểm tra
            // để Chromium không chặn hay hỏi quyền. Phát tự động không cần bấm (đồng bộ theo chủ phòng).
            // WebRtcHideLocalIpsWithMdns: chia sẻ màn hình (WebRTC) đi thẳng máy-máy trong LAN — để mặc định Chromium giấu IP LAN thành tên *.local (mDNS),
            // mạng văn phòng hay chặn mDNS → hai máy không thấy nhau. Không dùng máy chủ STUN / TURN ngoài Internet.
            // unsafely-treat-insecure-origin-as-secure: navigator.mediaDevices (getDisplayMedia — chia sẻ màn hình) chỉ có ở trang "an toàn" (https / localhost);
            // trang phải ở http://watch.local (xem ghi chú lớp) nên chỉ coi RIÊNG nguồn này là an toàn.
            var args = $"--host-resolver-rules=\"MAP {VirtualHost} ~NOTFOUND\" --autoplay-policy=no-user-gesture-required " +
                       $"--unsafely-treat-insecure-origin-as-secure=http://{VirtualHost} " +
                       "--disable-features=BlockInsecurePrivateNetworkRequests,PrivateNetworkAccessSendPreflights,PrivateNetworkAccessRespectPreflightResults,LocalNetworkAccessChecks,WebRtcHideLocalIpsWithMdns";
            CoreWebView2Environment env;
            try { env = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions(args)); }
            catch { env = await CoreWebView2Environment.CreateAsync(userDataFolder: profile); }
            await _web.EnsureCoreWebView2Async(env);
            var core = _core = _web.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsReputationCheckingRequired = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, e) => { try { OnPage(e.WebMessageAsJson); } catch (Exception ex) { Post(new { t = "ui.error", text = ex.Message }); } };
            core.NewWindowRequested += (_, e) =>
            {
                // Link trong trình phát YouTube (logo, "xem trên YouTube") → trình duyệt mặc định, không mở cửa sổ WebView2 mới.
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
                    try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
            };
            core.ContainsFullScreenElementChanged += (_, _) => SetFullScreen(core.ContainsFullScreenElement);
            if (_opts.ContainsKey("muted")) core.IsMuted = true;
            core.Navigate($"http://{VirtualHost}/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không khởi tạo được WebView2:\n" + ex.Message + "\n\nCần cài Microsoft Edge WebView2 Runtime.", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    /// <summary>Trang xin toàn màn hình (requestFullscreen) → cửa sổ bỏ viền + phóng to; thoát (Esc) → trả lại như cũ.</summary>
    private void SetFullScreen(bool on)
    {
        if (_hidden) return;
        if (on)
        {
            _preFullState = WindowState; _preFullBorder = FormBorderStyle;
            FormBorderStyle = FormBorderStyle.None;
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            FormBorderStyle = _preFullBorder;
            WindowState = _preFullState;
        }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_hidden || _server is null || _server.ViewerCount <= 1) return;   // --hidden (thử tự động): không bật hộp hỏi lên màn hình người dùng
        var ans = MessageBox.Show(this, "Bạn đang làm chủ phòng — đóng sẽ kết thúc buổi chiếu cho mọi người. Đóng?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ans != DialogResult.Yes) e.Cancel = true;
    }

    // ───────────────────────── Trang → C# ─────────────────────────

    private void OnPage(string json)
    {
        var m = JsonNode.Parse(json)?.AsObject();
        if (m is null) return;
        switch ((string?)m["t"])
        {
            case "net":
                if (m["m"] is JsonNode inner) _client?.SendRaw(Encoding.UTF8.GetBytes(inner.ToJsonString()));
                break;
            case "ui.init":
                Post(new
                {
                    t = "ui.init", name = _prefs.Name, room = _prefs.Room, lastIp = _prefs.LastIp, roomPass = _prefs.RoomPass,
                    ips = RoomFinder.LocalIPv4(), machine = Environment.MachineName, version = Application.ProductVersion.Split('+')[0],
                    auto = _opts.ContainsKey("host") ? "host" : _opts.ContainsKey("join") ? "join" : null,
                    autoJoin = _opts.GetValueOrDefault("join"), autoRoom = _opts.GetValueOrDefault("room"),
                    volume = _prefs.Volume, mode = _prefs.Mode, side = _prefs.Side,
                    fakeShare = _opts.ContainsKey("fakeshare") ? true : (bool?)null,
                });
                break;
            case "ui.find":
                if (_finder is null)
                {
                    try
                    {
                        _finder = new RoomFinder();
                        _finder.Changed += rooms => UI(() => Post(new { t = "ui.rooms", rooms, error = _finder?.Error }));
                    }
                    catch (Exception ex) { Post(new { t = "ui.rooms", rooms = Array.Empty<object>(), error = "Không mở được UDP để tìm phòng: " + ex.Message }); }
                }
                break;
            case "ui.stopfind":
                _finder?.Dispose(); _finder = null;
                break;
            case "ui.host":
                _ = HostAsync((string?)m["name"], (string?)m["room"], (string?)m["pass"]);
                break;
            case "ui.pass":
                // mật khẩu người vào phòng gõ ở hộp 🔒 — chỉ giữ trong RoomKeyClient tới khi vào được, không lưu, không log
                if ((string?)m["pass"] is { Length: > 0 } pw) _client?.SubmitPassword(pw);
                break;
            case "ui.join":
                _ = JoinAsync((string?)m["name"], (string?)m["ip"]);
                break;
            case "ui.pickfile":
                PickFile();
                break;
            case "ui.firewall":
                _ = OpenFirewallAsync();
                break;
            case "ui.prefs":
                if ((double?)m["volume"] is double vol) _prefs.Volume = Math.Clamp(vol, 0, 1);
                if ((string?)m["mode"] is "theater" or "screen") _prefs.Mode = (string)m["mode"]!;
                if ((bool?)m["side"] is bool side) _prefs.Side = side;
                _prefs.Save();
                break;
            case "ui.open":
                if (Uri.TryCreate((string?)m["url"], UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
                    try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
                break;
            case "ui.leave":
                LeaveRoom();
                Post(new { t = "ui.left" });
                break;
            case "ui.copy":
                // link mời → clipboard (Clipboard của WinForms: trang watch.local không chắc có quyền navigator.clipboard)
                if ((string?)m["text"] is { Length: > 0 and < 2000 } text)
                    try { if (!_hidden) Clipboard.SetText(text); Post(new { t = "ui.copied" }); }   // --hidden (thử tự động): không đè clipboard người dùng
                    catch (Exception ex) { Post(new { t = "ui.error", text = "Không chép được vào clipboard: " + ex.Message }); }
                break;
        }
    }

    private void Post(object msg)
    {
        if (_core is null || IsDisposed) return;
        try { _core.PostWebMessageAsJson(JsonSerializer.Serialize(msg, Wire.Json)); } catch { }
    }

    private void PostRaw(string json)
    {
        if (_core is null || IsDisposed) return;
        try { _core.PostWebMessageAsJson(json); } catch { }
    }

    private void UI(Action a)
    {
        if (IsDisposed) return;
        try { BeginInvoke(a); } catch { }
    }

    // ───────────────────────── Phòng ─────────────────────────

    private async Task HostAsync(string? name, string? room, string? pass)
    {
        LeaveRoom();
        _prefs.Name = Clean(name, _prefs.Name);
        _prefs.Room = Clean(room, _prefs.Room);
        // --pass (thử tự động) thắng ô trên trang. Nhớ mật khẩu phòng của chủ phòng — trong file là bản DPAPI (Prefs.RoomPassStored).
        pass = _opts.TryGetValue("pass", out var op) ? op : (pass ?? "");
        if (pass.Length > 64) pass = pass[..64];
        _prefs.RoomPass = pass;
        _prefs.Save();
        var loop = _opts.ContainsKey("loopback");
        var port = _opts.TryGetValue("port", out var ps) && int.TryParse(ps, out var pp) ? pp : Wire.DefaultPort;
        try
        {
            _server = WatchServer.Start(_prefs.Room, port, loop, pass);
            _media = MediaServer.Start(Wire.MediaPort + (port - Wire.DefaultPort), loop);
            _server.MediaPort = _media.Port;
        }
        catch (Exception ex) { LeaveRoom(); Post(new { t = "ui.error", text = ex.Message }); return; }
        _server.Log += s => Debug.WriteLine(s);
        var srv = _server;
        _media.Invite = () => new MediaServer.InviteInfo(srv.RoomName, Environment.MachineName, srv.Port, srv.Locked);
        var inviteIp = loop ? "127.0.0.1" : RoomFinder.PrimaryIPv4();
        var invite = inviteIp is null ? null : MediaServer.InviteUrl(inviteIp, _media.Port, _server.Port);
        Post(new { t = "ui.hosted", port = _server.Port, ips = RoomFinder.LocalIPv4(), warn = _server.BeaconError, locked = _server.Locked, invite });
        await ConnectAsync("127.0.0.1", _server.Port, _server.HostToken);
        if (!loop) _ = CheckFirewallAsync();
        // --file <đường dẫn>: chiếu luôn file trên máy (thử tự động — hộp chọn file không bấm được khi chạy ngầm)
        if (_opts.TryGetValue("file", out var f) && File.Exists(f) && _media != null && _client != null)
            _client.SendRaw(Wire.Encode(new { t = "addfile", media = _media.Register(f), title = Path.GetFileName(f) }));
    }

    private async Task JoinAsync(string? name, string? ip)
    {
        LeaveRoom();
        _prefs.Name = Clean(name, _prefs.Name);
        ip = Invite.Parse(ip ?? "");   // dán link mời (http://…/join hoặc bcwatch://join/…) cũng được
        if (ip.Length == 0) { Post(new { t = "ui.error", text = "Nhập IP máy chủ phòng." }); return; }
        _prefs.LastIp = ip;
        _prefs.Save();
        var host = ip; var port = Wire.DefaultPort;
        var colon = ip.LastIndexOf(':');
        if (colon > 0 && int.TryParse(ip[(colon + 1)..], out var p)) { host = ip[..colon]; port = p; }
        await ConnectAsync(host, port, null, _opts.GetValueOrDefault("pass"));
    }

    private async Task ConnectAsync(string host, int port, string? token, string? password = null)
    {
        try
        {
            var c = await WatchClient.ConnectAsync(host, port, _prefs.Name, token, password);
            var answered = 0;
            c.NeedPassword += (text, room) => { answered = 1; UI(() => { if (_client == c) Post(new { t = "ui.needpass", text, room }); }); };
            _client = c;
            _leaving = false;
            c.Message += data => { answered = 1; var s = Encoding.UTF8.GetString(data); UI(() => PostRaw(s)); };
            c.Closed += reason => UI(() =>
            {
                if (_client != c) return;
                _client = null;
                if (_leaving) return;
                // Chủ phòng mất kết nối tới chính phòng mình: trang về sảnh nhưng máy chủ vẫn chạy → phòng "ma" (vẫn hiện trong danh sách,
                // không ai điều khiển). Đóng luôn phòng, người xem được báo kết thúc.
                if (_server != null) { LeaveRoom(); reason += " — đã đóng phòng của bạn"; }
                Post(new { t = "ui.closed", reason });
            });
            // Kết nối được mà phòng không trả lời gì (máy chủ phòng treo / bản lỗi): báo rõ thay vì đứng im ở sảnh.
            _ = Task.Delay(8000).ContinueWith(_ => UI(() =>
            {
                if (Volatile.Read(ref answered) != 0 || _client != c) return;
                LeaveRoom();
                Post(new { t = "reject", text = $"Phòng {host}:{port} không trả lời — chủ phòng có thể đã đóng phòng / treo. Thử lại sau, hoặc hỏi chủ phòng." });
                Post(new { t = "ui.closed", reason = "không trả lời" });
            }));
            // Địa chỉ chủ phòng như máy này thấy — trang dựng link file http://host:cổng/m/mã từ đây (cổng có trong "welcome"), và link mời.
            Post(new { t = "ui.connected", host, port });
        }
        catch (Exception ex)
        {
            if (_server != null) LeaveRoom();
            Post(new { t = "ui.error", text = ex.Message });
        }
    }

    /// <summary>Rời phòng; là chủ phòng thì đóng phòng: tắt tìm phòng UDP + cổng TCP + cổng phát file, báo người xem. Trả về việc gửi lời báo
    /// (đóng chương trình thì chờ nó).</summary>
    private Task LeaveRoom()
    {
        _leaving = true;
        try { _client?.Dispose(); } catch { }
        _client = null;
        var closing = Task.CompletedTask;
        try { _server?.Dispose(); closing = _server?.Closing ?? closing; } catch { }
        _server = null;
        try { _media?.Dispose(); } catch { }
        _media = null;
        return closing;
    }

    private void PickFile()
    {
        if (_media is null || _client is null) { Post(new { t = "ui.error", text = "Chỉ chủ phòng chiếu được file trên máy." }); return; }
        var exts = string.Join(";", MediaServer.Extensions.Select(e => "*" + e));
        using var dlg = new OpenFileDialog
        {
            Title = "Chọn video / nhạc để chiếu cho cả phòng",
            Filter = $"Video / nhạc ({exts})|{exts}|Mọi file (*.*)|*.*",
            Multiselect = true,
        };
        if (!string.IsNullOrEmpty(_prefs.LastDir)) try { dlg.InitialDirectory = _prefs.LastDir; } catch { }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _prefs.LastDir = Path.GetDirectoryName(dlg.FileNames[0]) ?? "";
        _prefs.Save();
        foreach (var f in dlg.FileNames)
        {
            var id = _media.Register(f);
            _client.SendRaw(Wire.Encode(new { t = "addfile", media = id, title = Path.GetFileName(f) }));
        }
    }

    private async Task CheckFirewallAsync()
    {
        var server = _server;
        if (server is null) return;
        var st = await Task.Run(() => Firewall.Check(Environment.ProcessPath ?? Application.ExecutablePath, server.Port));
        if (_server == server) Post(new { t = "ui.fw", level = st.Level, text = st.Text });
    }

    private async Task OpenFirewallAsync()
    {
        Post(new { t = "ui.fw", level = "warn", text = "Đang chờ bạn đồng ý hộp xin quyền admin của Windows…", busy = true });
        var err = await Firewall.OpenAsync(Environment.ProcessPath ?? Application.ExecutablePath);
        if (err != null) { Post(new { t = "ui.fw", level = "bad", text = err }); return; }
        await CheckFirewallAsync();
    }

    private static string Clean(string? s, string fallback)
    {
        s = (s ?? "").Trim();
        return s.Length == 0 ? fallback : s.Length > 40 ? s[..40] : s;
    }
}

/// <summary>Lựa chọn nhớ giữa các lần mở: %AppData%\Bcode\watch-lan.json (hoặc --data). Không lưu danh sách chiếu hay bình luận.</summary>
internal sealed class Prefs
{
    public string Name { get; set; } = Environment.UserName;
    public string Room { get; set; } = "Rạp của " + Environment.UserName;
    public string LastIp { get; set; } = "";
    public string LastDir { get; set; } = "";
    /// <summary>Mật khẩu phòng lần trước của chủ phòng — trong file chỉ có bản DPAPI ("dpapi:…", chỉ đúng tài khoản Windows này giải được).</summary>
    private readonly ProtectedText _roomPass = new();
    [JsonIgnore] public string RoomPass { get => _roomPass.Plain; set => _roomPass.Plain = value; }
    [JsonPropertyName("RoomPass")] public string RoomPassStored { get => _roomPass.Stored; set => _roomPass.Stored = value; }
    public double Volume { get; set; } = 0.8;
    public string Mode { get; set; } = "theater";
    /// <summary>Hiện cột bình luận bên phải.</summary>
    public bool Side { get; set; } = true;
    private string _path = "";

    public static Prefs Load(string? dir)
    {
        dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode");
        var path = Path.Combine(dir, "watch-lan.json");
        Prefs p;
        try { p = File.Exists(path) ? JsonSerializer.Deserialize<Prefs>(File.ReadAllText(path)) ?? new Prefs() : new Prefs(); }
        catch { p = new Prefs(); }
        p._path = path;
        return p;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* không ghi được thì thôi */ }
    }
}
