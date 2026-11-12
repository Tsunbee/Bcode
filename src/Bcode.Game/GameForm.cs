using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bcode.Game.Data;
using Bcode.Game.Net;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.Game;

/// <summary>Cửa sổ game: 1 WebView2 chạy Web\index.html (sảnh + canvas). Trang nói với C# bằng postMessage:
/// tin "ui.*" xử lý tại đây (mở phòng, tìm phòng, vào phòng, chọn file câu hỏi), tin "net" chuyển nguyên văn cho máy chủ phòng;
/// tin máy chủ gửi về được đẩy thẳng xuống trang (PostWebMessageAsJson).</summary>
public sealed class GameForm : Form
{
    private const string VirtualHost = "game.local";
    private const string Title = "Đêm Go-Live — Giải lao";
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, string> _opts;
    private readonly Prefs _prefs;
    private CoreWebView2? _core;
    private GameServer? _server;
    private GameClient? _client;
    private RoomFinder? _finder;
    private bool _leaving;
    private readonly bool _hidden;

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

    public GameForm(Dictionary<string, string> opts)
    {
        _opts = opts;
        _prefs = Prefs.Load(opts.TryGetValue("data", out var d) ? d : null);
        if (opts.TryGetValue("name", out var n)) _prefs.Name = n;
        Text = Title;
        Width = 1400; Height = 900;
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 22, 30);
        if (opts.TryGetValue("pos", out var pos) && pos.Split(',').Select(s => int.TryParse(s, out var v) ? (int?)v : null).ToArray() is { Length: 4 } r && r.All(v => v.HasValue))
        {
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(r[0]!.Value, r[1]!.Value, Math.Max(960, r[2]!.Value), Math.Max(640, r[3]!.Value));
        }
        // Chạy ngầm khi thử nhiều bản trên máy đang có người dùng: nằm ngoài mọi màn hình, không lên taskbar, không giành focus.
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
        FormClosed += (_, _) => { LeaveRoom(); try { _web.Dispose(); } catch { } };
    }

    private async Task InitAsync()
    {
        try
        {
            // Mỗi cửa sổ 1 profile riêng: chạy nhiều bản game cùng lúc (thử nhiều người trên 1 máy) không tranh nhau thư mục dữ liệu WebView2.
            var profile = Path.Combine(Path.GetTempPath(), "BcodeGame.WebView2", Environment.ProcessId.ToString());
            CoreWebView2Environment env;
            try { env = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions($"--host-resolver-rules=\"MAP {VirtualHost} ~NOTFOUND\" --autoplay-policy=no-user-gesture-required")); }
            catch { env = await CoreWebView2Environment.CreateAsync(userDataFolder: profile); }
            await _web.EnsureCoreWebView2Async(env);
            var core = _core = _web.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsReputationCheckingRequired = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping(VirtualHost, Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, e) => { try { OnPage(e.WebMessageAsJson); } catch (Exception ex) { Post(new { t = "ui.error", text = ex.Message }); } };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.Navigate($"https://{VirtualHost}/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không khởi tạo được WebView2:\n" + ex.Message + "\n\nCần cài Microsoft Edge WebView2 Runtime.", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_server is null || _server.Room.PlayerCount <= 1) return;
        var ans = MessageBox.Show(this, "Bạn đang làm chủ phòng — đóng game sẽ kết thúc phòng cho mọi người. Đóng?", Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ans != DialogResult.Yes) e.Cancel = true;
    }

    // ───────────────────────── Trang → C# ─────────────────────────

    private void OnPage(string json)
    {
        var m = JsonNode.Parse(json)?.AsObject();
        if (m is null) return;
        var t = (string?)m["t"];
        switch (t)
        {
            case "net":
                if (m["m"] is JsonNode inner) _client?.SendRaw(Encoding.UTF8.GetBytes(inner.ToJsonString()));
                break;
            case "ui.init":
                Post(new
                {
                    t = "ui.init", name = _prefs.Name, room = _prefs.Room, lastIp = _prefs.LastIp, lastFile = _prefs.LastFile,
                    ips = RoomFinder.LocalIPv4(), machine = Environment.MachineName, version = Application.ProductVersion.Split('+')[0],
                    auto = _opts.ContainsKey("host") ? "host" : _opts.ContainsKey("join") ? "join" : null,
                    autoJoin = _opts.GetValueOrDefault("join"), autoRoom = _opts.GetValueOrDefault("room"),
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
                _ = HostAsync((string?)m["name"], (string?)m["room"]);
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
            case "ui.leave":
                LeaveRoom();
                Post(new { t = "ui.left" });
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

    private async Task HostAsync(string? name, string? room)
    {
        LeaveRoom();
        _prefs.Name = Clean(name, _prefs.Name);
        _prefs.Room = Clean(room, _prefs.Room);
        _prefs.Save();
        var port = _opts.TryGetValue("port", out var ps) && int.TryParse(ps, out var pp) ? pp : Wire.DefaultGamePort;
        var min = _opts.TryGetValue("min-players", out var mp) && int.TryParse(mp, out var mpi) ? mpi : 5;
        try { _server = GameServer.Start(_prefs.Room, port, min, _opts.ContainsKey("loopback")); }
        catch (Exception ex) { Post(new { t = "ui.error", text = ex.Message }); return; }
        _server.Room.Log += s => Debug.WriteLine(s);
        Post(new { t = "ui.hosted", port = _server.Port, ips = RoomFinder.LocalIPv4(), warn = _server.BeaconError });
        await ConnectAsync("127.0.0.1", _server.Port, _server.Room.HostToken);
        if (!_opts.ContainsKey("loopback")) _ = CheckFirewallAsync();
        var file = _opts.GetValueOrDefault("file") ?? _prefs.LastFile;
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) file = FirstXlsx(Path.Combine(AppContext.BaseDirectory, "CauHoi")) ?? FirstXlsx(AppContext.BaseDirectory);
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) file = await SharedBankFileAsync();
        if (!string.IsNullOrEmpty(file) && File.Exists(file)) await LoadBankAsync(file);
    }

    /// <summary>Chủ phòng: đọc luật tường lửa (không cần admin) → báo ở phòng chờ trước khi máy khác gặp lỗi "không nối được".</summary>
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

    /// <summary>Thư mục câu hỏi dùng chung trên ổ mạng văn phòng (cùng máy với thư mục phát hành trong build.bat) — máy nào mở phòng cũng tự lấy,
    /// không phải chép file. Ghi đè bằng "BankDir" trong game-lan.json. File là tài liệu nội bộ: chỉ để trên ổ chung, KHÔNG vào repo / bản phát hành.</summary>
    private const string SharedBankDir = @"\\172.168.5.17\Users\HAOTN\BcodeGame\CauHoi";

    /// <summary>Dò ổ chung có hạn giờ: máy ngoài mạng văn phòng thì Directory.Exists trên đường dẫn UNC có thể treo vài chục giây.</summary>
    private async Task<string?> SharedBankFileAsync()
    {
        var dir = string.IsNullOrWhiteSpace(_prefs.BankDir) ? SharedBankDir : _prefs.BankDir;
        var probe = Task.Run(() => FirstXlsx(dir));
        return await Task.WhenAny(probe, Task.Delay(4000)) == probe ? probe.Result : null;
    }

    /// <summary>File .xlsx đầu tiên (theo tên) trong thư mục — bỏ file khoá tạm "~$..." của Excel. Thư mục không có / không đọc được → null.</summary>
    private static string? FirstXlsx(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "*.xlsx").Where(p => !Path.GetFileName(p).StartsWith("~$")).OrderBy(p => p).FirstOrDefault();
        }
        catch { return null; }
    }

    private async Task JoinAsync(string? name, string? ip)
    {
        LeaveRoom();
        _prefs.Name = Clean(name, _prefs.Name);
        ip = (ip ?? "").Trim();
        if (ip.Length == 0) { Post(new { t = "ui.error", text = "Nhập IP máy chủ phòng." }); return; }
        _prefs.LastIp = ip;
        _prefs.Save();
        var host = ip; var port = Wire.DefaultGamePort;
        var colon = ip.LastIndexOf(':');
        if (colon > 0 && int.TryParse(ip[(colon + 1)..], out var p)) { host = ip[..colon]; port = p; }
        await ConnectAsync(host, port, null);
    }

    private async Task ConnectAsync(string host, int port, string? token)
    {
        try
        {
            var c = await GameClient.ConnectAsync(host, port, _prefs.Name, token);
            _leaving = false;
            c.Message += bytes => { var s = Encoding.UTF8.GetString(bytes); UI(() => PostRaw(s)); };
            c.Closed += reason => UI(() => { if (!_leaving && _client == c) { _client = null; Post(new { t = "ui.closed", reason }); } });
            _client = c;
        }
        catch (Exception ex)
        {
            Post(new { t = "ui.error", text = ex.Message });
            if (token is not null) { _server?.Dispose(); _server = null; }
        }
    }

    private void LeaveRoom()
    {
        _leaving = true;
        _finder?.Dispose(); _finder = null;
        _client?.Dispose(); _client = null;
        _server?.Dispose(); _server = null;
    }

    private void PickFile()
    {
        if (_server is null) return;
        using var dlg = new OpenFileDialog
        {
            Title = "Chọn file câu hỏi trắc nghiệm (Excel)",
            Filter = "Excel (*.xlsx)|*.xlsx|Tất cả|*.*",
        };
        if (!string.IsNullOrEmpty(_prefs.LastFile)) try { dlg.InitialDirectory = Path.GetDirectoryName(_prefs.LastFile); } catch { }
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _ = LoadBankAsync(dlg.FileName);
    }

    private async Task LoadBankAsync(string path)
    {
        Post(new { t = "ui.bank", loading = true, file = Path.GetFileName(path) });
        try
        {
            var bank = await Task.Run(() => QuestionBank.Load(path));
            if (_server is null) return;
            _server.Room.SetBank(bank);
            _prefs.LastFile = path;
            _prefs.Save();
            Post(new { t = "ui.bank", ok = true, file = Path.GetFileName(path), total = bank.Total });
        }
        catch (IOException ex) { Post(new { t = "ui.bank", ok = false, error = "Không đọc được file (đang bị khoá?): " + ex.Message }); }
        catch (Exception ex) { Post(new { t = "ui.bank", ok = false, error = "File không đúng mẫu câu hỏi: " + ex.Message }); }
    }

    private static string Clean(string? s, string fallback)
    {
        s = (s ?? "").Trim();
        return s.Length == 0 ? fallback : s.Length > 24 ? s[..24] : s;
    }
}

/// <summary>Lựa chọn nhớ giữa các lần mở: %AppData%\Bcode\game-lan.json (hoặc --data). KHÔNG lưu nội dung câu hỏi — chỉ đường dẫn file của chủ phòng.</summary>
internal sealed class Prefs
{
    public string Name { get; set; } = Environment.UserName;
    public string Room { get; set; } = "Phòng của " + Environment.UserName;
    public string LastIp { get; set; } = "";
    public string LastFile { get; set; } = "";
    /// <summary>Thư mục câu hỏi dùng chung (trống = GameForm.SharedBankDir).</summary>
    public string BankDir { get; set; } = "";
    private string _path = "";

    public static Prefs Load(string? dir)
    {
        dir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode");
        var path = Path.Combine(dir, "game-lan.json");
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
