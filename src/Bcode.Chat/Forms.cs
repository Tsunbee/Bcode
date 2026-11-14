using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bcode.Chat.Net;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.Chat;

/// <summary>Cửa sổ có 1 WebView2 chạy 1 trang trong Web\ (https://chat.local/...). Trang nói với C# bằng postMessage {t:...};
/// C# đẩy xuống bằng <see cref="Post"/>. Tin gửi trước khi trang sẵn sàng thì bỏ (trang tự xin lại khi "ui.init").</summary>
internal abstract class WebForm : Form
{
    protected const string Host = "chat.local";
    protected readonly ChatApp App;
    protected readonly WebView2 Web = new() { Dock = DockStyle.Fill };
    protected CoreWebView2? Core;
    private readonly bool _noActivate;

    protected WebForm(ChatApp app, bool noActivate = false)
    {
        App = app;
        _noActivate = noActivate;
        Icon = app.AppIcon;
        BackColor = Color.FromArgb(236, 233, 216);
        Font = new Font("Tahoma", 8.25f);
        Controls.Add(Web);
        Load += async (_, _) => await InitAsync();
        FormClosed += (_, _) => { try { Web.Dispose(); } catch { } };
    }

    protected override bool ShowWithoutActivation => _noActivate;

    protected abstract string Page { get; }
    /// <summary>Thư mục ảnh đại diện, trang đọc qua https://av.chat.local/&lt;id&gt;.jpg (null = trang không cần ảnh).</summary>
    protected virtual string? AvatarDir => null;
    protected abstract void OnPage(JsonObject m, CoreWebView2WebMessageReceivedEventArgs e);

    private async Task InitAsync()
    {
        try
        {
            await Web.EnsureCoreWebView2Async(await App.WebEnv());
            var core = Core = Web.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.SetVirtualHostNameToFolderMapping(Host, Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.Allow);
            if (AvatarDir is { } avDir) core.SetVirtualHostNameToFolderMapping("av." + Host, avDir, CoreWebView2HostResourceAccessKind.Allow);
            core.WebMessageReceived += (_, e) =>
            {
                try { if (JsonNode.Parse(e.WebMessageAsJson) is JsonObject m) OnPage(m, e); }
                catch (Exception ex) { Post(new { t = "error", text = ex.Message }); }
            };
            // gọi thoại / video: micro + camera chỉ mở khi người dùng bấm gọi / nghe máy → cho phép luôn với trang của Bhoo (khỏi hộp hỏi mỗi lần)
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind is not (CoreWebView2PermissionKind.Microphone or CoreWebView2PermissionKind.Camera)) return;
                e.State = Uri.TryCreate(e.Uri, UriKind.Absolute, out var pu) && pu.Host == Host ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            };
            core.NewWindowRequested += (_, e) =>
            {
                // link trong tin nhắn → trình duyệt mặc định
                e.Handled = true;
                OpenUrl(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Host == Host) return;
                e.Cancel = true;   // không cho trang đi chỗ khác (thả file vào → trình duyệt mở file)
                OpenUrl(e.Uri);
            };
            core.Navigate($"https://{Host}/{Page}");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không khởi tạo được WebView2:\n" + ex.Message + "\n\nCần cài Microsoft Edge WebView2 Runtime.", "Bhoo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected static void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "https" || u.Scheme == "http"))
            try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    public void Post(object msg)
    {
        if (Core is null || IsDisposed) return;
        try { Core.PostWebMessageAsJson(JsonSerializer.Serialize(msg, Wire.Json)); } catch { }
    }

    // ───────────── Nháy taskbar ─────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>Nháy nút trên taskbar tới khi người dùng bấm vào cửa sổ.</summary>
    public void Flash()
    {
        if (!IsHandleCreated) return;
        var fi = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = Handle, dwFlags = 3 | 12, uCount = 0, dwTimeout = 0 };   // ALL | TIMERNOFG
        FlashWindowEx(ref fi);
    }
}

/// <summary>Cửa sổ danh sách bạn (Web\buddy.html) — dọc, hẹp như messenger cũ. Bấm X thì thu xuống khay (tuỳ chọn).</summary>
internal sealed class BuddyForm : WebForm
{
    public bool AllowClose { get; set; }
    protected override string Page => "buddy.html";
    protected override string? AvatarDir => S.Store.AvatarDir;

    private readonly Session S;

    public BuddyForm(Session s) : base(s.App)
    {
        S = s;
        Text = "Bhoo";
        Width = 300; Height = 620;
        MinimumSize = new Size(240, 380);
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(wa.Right - Width - 24, wa.Top + 40);
        if (s.Store.Prefs.ListBounds is { Length: 4 } r)
        {
            var rect = new Rectangle(r[0], r[1], Math.Max(240, r[2]), Math.Max(380, r[3]));
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(rect))) Bounds = rect;
        }
        FormClosing += (_, e) =>
        {
            if (WindowState == FormWindowState.Normal) { s.Store.Prefs.ListBounds = new[] { Left, Top, Width, Height }; s.Store.SavePrefs(); }
            if (AllowClose || s.App.Quitting) return;
            e.Cancel = true;
            if (s.Store.Prefs.CloseToTray) Hide();
            else BeginInvoke(s.App.Quit);   // không gọi Quit ngay trong FormClosing (Quit tự đóng lại form này)
        };
    }

    protected override void OnPage(JsonObject m, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var node = S.Node;
        switch ((string?)m["t"])
        {
            case "ui.init":
                Post(new { t = "init", me = S.Me(), list = S.BuddyList(), groups = node.Groups(), rooms = S.RoomList() });
                _ = CheckFirewall();
                break;
            case "me.set":
                node.SetMe((string?)m["name"], (string?)m["status"], (string?)m["text"], (string?)m["color"]);
                S.Broadcast(new { t = "me", me = S.Me() });
                break;
            case "status":
                S.SetMyStatus((string?)m["status"] ?? Status.Online);
                break;
            case "avatar.pick":
            {
                using var dlg = new OpenFileDialog { Title = "Chọn ảnh đại diện", Filter = "Ảnh|*.jpg;*.jpeg;*.png;*.bmp;*.gif|Mọi file|*.*" };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    try { S.SetAvatar(dlg.FileName); } catch (Exception ex) { Post(new { t = "error", text = "Không dùng được ảnh này: " + ex.Message }); }
                break;
            }
            case "avatar.clear":
                S.SetAvatar(null);
                break;
            case "open":
                if ((string?)m["conv"] is { Length: > 2 } conv) S.OpenIm(conv, activate: true);
                break;
            case "history":
                S.OpenHistory((string?)m["conv"]);
                break;
            case "room.join":
                try { S.OpenRoom((string?)m["title"] ?? "", (string?)m["topic"]); }
                catch (Exception ex) { Post(new { t = "error", text = ex.Message }); }
                break;
            case "group.new":
                var ids = (m["ids"] as JsonArray)?.Select(x => (string?)x ?? "").Where(x => x.Length > 0).ToList() ?? new();
                if (ids.Count == 0) { Post(new { t = "error", text = "Chọn ít nhất 1 người để mở phòng chat." }); break; }
                var g = node.CreateGroup((string?)m["title"] ?? "", ids);
                S.OpenIm("g:" + g.Gid, activate: true);
                break;
            case "peer.add":
                try { node.AddManualPeer((string?)m["ip"] ?? ""); S.Broadcast(new { t = "me", me = S.Me() }); }
                catch (Exception ex) { Post(new { t = "error", text = ex.Message }); }
                break;
            case "peer.del":
                node.RemoveManualPeer((string?)m["ip"] ?? "");
                S.Broadcast(new { t = "me", me = S.Me() });
                break;
            case "buddy.group":
                node.SetBuddyGroup((string?)m["id"] ?? "", (string?)m["group"] ?? "");
                break;
            case "buddy.ignore":
                node.SetIgnored((string?)m["id"] ?? "", (bool?)m["ignored"] ?? true);
                break;
            case "buddy.hide":
                node.HideBuddy((string?)m["id"] ?? "", (bool?)m["hidden"] ?? true);
                break;
            case "prefs":
                S.SetPrefs(m);
                break;
            case "fw.check":
                _ = CheckFirewall();
                break;
            case "fw.open":
                _ = OpenFirewall();
                break;
            case "signout":
                S.App.SignOut();
                break;
            case "quit":
                S.App.Quit();
                break;
        }
    }

    private async Task CheckFirewall() => Post(await S.FirewallStatus());
    private async Task OpenFirewall() => Post(await S.OpenFirewall());
}

/// <summary>Cửa sổ 1 cuộc chat (Web\im.html?c=conv): 1-1 hoặc phòng nhóm. BUZZ = rung cửa sổ + kêu.</summary>
internal sealed class ImForm : WebForm
{
    public string Conv { get; }
    private readonly System.Windows.Forms.Timer _shake = new() { Interval = 16 };
    private int _shakeStep;
    private Point _shakeOrigin;
    private long _lastShake;
    protected override string Page => "im.html?c=" + Uri.EscapeDataString(Conv);
    protected override string? AvatarDir => S.Store.AvatarDir;

    private readonly Session S;

    public ImForm(Session s, string conv, bool activate) : base(s.App, noActivate: !activate)
    {
        S = s;
        Conv = conv;
        Width = 560; Height = 500;
        MinimumSize = new Size(400, 360);
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        var offset = (Application.OpenForms.OfType<ImForm>().Count() % 6) * 26;
        Location = new Point(Math.Max(wa.Left, wa.Right - 300 - Width - 40 - offset), wa.Top + 80 + offset);
        UpdateTitle();
        _shake.Tick += (_, _) => ShakeTick();
        Activated += (_, _) => Post(new { t = "focus" });
    }

    /// <summary>Đang có cuộc gọi trong cửa sổ này mà đóng → báo người kia gác máy.</summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (!e.Cancel) S.CallWindowClosed(Conv);
    }

    public void UpdateTitle()
    {
        var title = S.Title(Conv);
        Text = (Conv.StartsWith("g:") ? "Phòng chat: " : Conv.StartsWith("r:") ? "Phòng công cộng: " : "") + title + " — Bhoo";
        Post(new { t = "title", title, group = Conv.StartsWith("g:") ? S.Node.FindGroup(Conv[2..]) : null });
    }

    /// <summary>BUZZ: hiện cửa sổ lên trên cùng rồi rung ~0,6 s. Hai lần BUZZ cách nhau &lt; 1,5 s thì chỉ rung 1 lần.</summary>
    public void Shake()
    {
        var now = Environment.TickCount64;
        if (now - _lastShake < 1500 || _shake.Enabled) return;
        _lastShake = now;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!Visible) Show();
        TopMost = true;
        BringToFront();
        _shakeOrigin = Location;
        _shakeStep = 0;
        _shake.Start();
        Post(new { t = "buzzfx" });
    }

    private void ShakeTick()
    {
        const int steps = 36;
        if (_shakeStep >= steps)
        {
            _shake.Stop();
            Location = _shakeOrigin;
            TopMost = false;
            if (!ContainsFocus) Flash();
            return;
        }
        var amp = 12 * (1 - (double)_shakeStep / steps);
        var dx = (int)(amp * Math.Sin(_shakeStep * 2.3));
        var dy = (int)(amp * 0.7 * Math.Cos(_shakeStep * 3.1));
        Location = new Point(_shakeOrigin.X + dx, _shakeOrigin.Y + dy);
        _shakeStep++;
    }

    protected override void OnPage(JsonObject m, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var node = S.Node;
        switch ((string?)m["t"])
        {
            case "ui.init":
                Post(new
                {
                    t = "init", conv = Conv, me = S.Me(), title = S.Title(Conv), list = S.BuddyList(),
                    group = Conv.StartsWith("g:") ? node.FindGroup(Conv[2..]) : null,
                    room = Conv.StartsWith("r:") ? S.RoomInfo(Conv[2..]) : null,
                    history = Conv.StartsWith("r:") ? new List<object>() : S.History(Conv),
                });
                break;
            case "send":
            {
                var text = ((string?)m["text"] ?? "").TrimEnd();
                if (text.Trim().Length == 0) break;
                var size = m["size"] is JsonValue sv && sv.TryGetValue<int>(out var si) ? si : (int?)null;
                Send(() => node.Send(Conv, "text", text, (string?)m["style"], (string?)m["fg"], size));
                Sounds.Play("send");
                break;
            }
            case "buzz" or "file.pick" or "file.drop" or "env" or "group.invite" or "typing" or "invite" when Conv.StartsWith("r:"):
                break;   // phòng công cộng: chỉ nhắn chữ
            case "buzz":
                Send(() => node.Send(Conv, "buzz", "BUZZ!!!"));
                // người gửi cũng thấy cửa sổ mình rung nhẹ — như ngày xưa
                Shake();
                Sounds.Play("buzz");
                break;
            case "invite":
            {
                // rủ Xem chung / Giải lao: mình chưa mở thì mở luôn ở chế độ làm chủ phòng, rồi gửi lời mời (kèm cổng mặc định của phòng)
                var app = (string?)m["app"] == "game" ? "game" : "watch";
                var room = "Phòng của " + S.Store.Prefs.Name;
                if (!SideApps.Running(app) && SideApps.Launch(app, "--host", "--room", room) is { } err) { Post(new { t = "error", text = err }); break; }
                var what = SideApps.Title(app);
                Send(() => node.Send(Conv, "invite", $"mời bạn vào {what}", invite: new InviteMeta(app, SideApps.DefaultPort(app), room)));
                break;
            }
            case "rtc":
                // gọi thoại / video 1-1: tín hiệu (đổ chuông, SDP, ICE) đi qua kết nối Bhoo, tiếng / hình đi thẳng WebRTC giữa 2 máy
                if (Conv.StartsWith("u:")) S.CallSignal(Conv, m);
                break;
            case "invite.accept":
            {
                var mid = (string?)m["mid"] ?? "";
                var msg = S.Store.LoadHistory(Conv, 2000).LastOrDefault(x => x.Mid == mid);
                if (msg?.Invite is not { } inv || inv.App is not ("watch" or "game")) { Post(new { t = "error", text = "Không thấy lời mời này nữa." }); break; }
                var ip = node.FindBuddy(msg.From)?.Ip ?? "";
                if (ip.Length == 0) { Post(new { t = "error", text = "Không biết IP của người mời (họ đang offline?)." }); break; }
                var port = inv.Port is > 0 and < 65536 ? inv.Port : SideApps.DefaultPort(inv.App);
                if (SideApps.Launch(inv.App, "--join", port == SideApps.DefaultPort(inv.App) ? ip : ip + ":" + port) is { } err2) Post(new { t = "error", text = err2 });
                break;
            }
            case "env":
                // khung cảnh: gửi như 1 tin (cả cuộc chat cùng đổi, người offline mở lên vẫn thấy)
                var scene = (string?)m["scene"] ?? "";
                if (System.Text.RegularExpressions.Regex.IsMatch(scene, "^[a-z]{0,12}$")) Send(() => node.Send(Conv, "env", scene));
                break;
            case "typing":
                node.SendTyping(Conv);
                break;
            case "file.pick":
            {
                using var dlg = new OpenFileDialog { Title = "Chọn file gửi qua Bhoo", Multiselect = true };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    foreach (var p in dlg.FileNames) SendFile(p);
                break;
            }
            case "file.drop":
                // thả file từ Explorer vào cửa sổ: trang gửi kèm File objects → đường dẫn thật
                if (e.AdditionalObjects is { } objs)
                    foreach (var o in objs)
                        if (o is CoreWebView2File f && File.Exists(f.Path)) SendFile(f.Path);
                break;
            case "file.get":
            {
                var mid = (string?)m["mid"] ?? "";
                var msg = S.Store.LoadHistory(Conv, 2000).LastOrDefault(x => x.Mid == mid);
                if (msg?.File is null) { Post(new { t = "error", text = "Không thấy tin có file này nữa." }); break; }
                _ = node.DownloadAsync(msg);
                break;
            }
            case "file.open":
                if ((string?)m["path"] is { Length: > 0 } fp && File.Exists(fp))
                    try { Process.Start(new ProcessStartInfo(fp) { UseShellExecute = true }); } catch (Exception ex) { Post(new { t = "error", text = ex.Message }); }
                break;
            case "file.folder":
                if ((string?)m["path"] is { Length: > 0 } fp2 && File.Exists(fp2))
                    try { Process.Start("explorer.exe", "/select,\"" + fp2 + "\""); } catch { }
                break;
            case "font":
                S.SaveFont((string?)m["style"], (string?)m["fg"], m["size"] is JsonValue fs && fs.TryGetValue<int>(out var fsi) ? fsi : null);
                break;
            case "group.invite":
            {
                var ids = (m["ids"] as JsonArray)?.Select(x => (string?)x ?? "").Where(x => x.Length > 0).ToList() ?? new();
                if (Conv.StartsWith("u:"))
                {
                    // từ cửa sổ 1-1 mời thêm người → mở phòng nhóm mới gồm cả người đang chat (như "mời vào hội thoại" ngày xưa)
                    ids.Add(Conv[2..]);
                    var g = node.CreateGroup((string?)m["title"] ?? "", ids);
                    S.OpenIm("g:" + g.Gid, activate: true);
                }
                else Send(() => node.InviteToGroup(Conv[2..], ids));
                break;
            }
            case "group.rename":
                if (Conv.StartsWith("g:")) Send(() => node.RenameGroup(Conv[2..], (string?)m["title"] ?? ""));
                break;
            case "group.leave":
                if (Conv.StartsWith("g:"))
                {
                    node.LeaveGroup(Conv[2..]);
                    S.Broadcast(new { t = "groups", list = node.Groups() });
                    Close();
                }
                break;
            case "history.clear":
                S.Store.ClearHistory(Conv);
                break;
            case "history.view":
                S.OpenHistory(Conv);
                break;
            case "open.buddy":
                if ((string?)m["id"] is { Length: > 0 } bid && bid != node.MyId) S.OpenIm("u:" + bid, activate: true);
                break;
            case "close":
                Close();
                break;
        }
    }

    private void SendFile(string path)
    {
        try
        {
            S.Node.OfferFile(Conv, path);
        }
        catch (Exception ex) { Post(new { t = "error", text = "Không gửi được file: " + ex.Message }); }
    }

    /// <summary>Gửi tin / đổi phòng — tin của mình hiện lên trang qua <see cref="ChatNode.Sent"/> (ChatApp); lỗi thì báo xuống trang.</summary>
    private void Send(Action f)
    {
        try { f(); }
        catch (Exception ex) { Post(new { t = "error", text = ex.Message }); }
    }
}

/// <summary>Màn đăng nhập Bhoo (Web\login.html): Bhoo ID (nickname) + nhớ ID / tự đăng nhập / đăng nhập ẩn. Không có mật khẩu (không có máy chủ).</summary>
internal sealed class LoginForm : WebForm
{
    public bool AllowClose { get; set; }
    protected override string Page => "login.html";

    public LoginForm(ChatApp app) : base(app)
    {
        Text = "Bhoo";
        Width = 300; Height = 560;
        MinimumSize = new Size(260, 460);
        StartPosition = FormStartPosition.Manual;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        Location = new Point(wa.Right - Width - 24, wa.Top + 40);
        FormClosing += (_, e) =>
        {
            if (AllowClose || app.Quitting) return;
            e.Cancel = true;
            BeginInvoke(app.Quit);   // chưa đăng nhập mà đóng màn đăng nhập = thoát hẳn
        };
    }

    protected override void OnPage(JsonObject m, CoreWebView2WebMessageReceivedEventArgs e)
    {
        switch ((string?)m["t"])
        {
            case "ui.init":
                var l = App.Login;
                Post(new { t = "init", ids = l.Ids, last = l.LastId, remember = l.Remember, auto = l.AutoLogin, invisible = l.Invisible, rule = Nick.Rule, version = Application.ProductVersion.Split('+')[0] });
                break;
            case "signin":
                _ = App.SignInAsync((string?)m["nick"] ?? "", (bool?)m["invisible"] ?? false, (bool?)m["remember"] ?? true, (bool?)m["auto"] ?? false);
                break;
            case "forget":
                App.Login.Ids.RemoveAll(x => string.Equals(x, (string?)m["nick"], StringComparison.OrdinalIgnoreCase));
                App.Login.Save();
                break;
            case "quit":
                App.Quit();
                break;
        }
    }
}

/// <summary>Cửa sổ xem lại lịch sử chat (Web\hist.html): trái = các cuộc chat, phải = tin; lọc ngày / người gửi / tìm chữ ở trang,
/// "tìm trong mọi cuộc chat" thì C# tìm. Lưu ra .txt được.</summary>
internal sealed class HistForm : WebForm
{
    private readonly Session S;
    private readonly string? _first;
    protected override string Page => "hist.html";
    protected override string? AvatarDir => S.Store.AvatarDir;

    public HistForm(Session s, string? conv) : base(s.App)
    {
        S = s;
        _first = conv;
        Text = "Lịch sử chat — Bhoo";
        Width = 780; Height = 560;
        MinimumSize = new Size(560, 380);
        StartPosition = FormStartPosition.CenterScreen;
    }

    protected override void OnPage(JsonObject m, CoreWebView2WebMessageReceivedEventArgs e)
    {
        switch ((string?)m["t"])
        {
            case "ui.init":
                Post(new { t = "init", me = S.Me(), convs = S.HistoryIndex(), select = _first });
                break;
            case "load":
            {
                var conv = (string?)m["conv"] ?? "";
                if (conv.Length < 3) break;
                Post(new { t = "msgs", conv, title = S.Title(conv), list = S.Store.LoadHistory(conv, 20000) });
                break;
            }
            case "search":
            {
                var q = ((string?)m["q"] ?? "").Trim();
                if (q.Length < 2) { Post(new { t = "error", text = "Gõ ít nhất 2 chữ để tìm." }); break; }
                Post(new { t = "found", q, list = S.SearchAll(q) });
                break;
            }
            case "open":
                if ((string?)m["conv"] is { Length: > 2 } c) S.OpenIm(c, activate: true);
                break;
            case "export":
            {
                var conv = (string?)m["conv"] ?? "";
                if (conv.Length < 3) break;
                var title = S.Title(conv);
                using var dlg = new SaveFileDialog { Title = "Lưu lịch sử chat", Filter = "Văn bản|*.txt", FileName = string.Concat(("Bhoo - " + title).Split(Path.GetInvalidFileNameChars())) + ".txt" };
                if (dlg.ShowDialog(this) != DialogResult.OK) break;
                var sb = new System.Text.StringBuilder($"Lịch sử chat Bhoo — {title}\r\n\r\n");
                var day = "";
                foreach (var x in S.Store.LoadHistory(conv, int.MaxValue))
                {
                    var at = DateTimeOffset.FromUnixTimeMilliseconds(x.At).LocalDateTime;
                    var d = at.ToString("dd/MM/yyyy");
                    if (d != day) { sb.Append($"\r\n——— {d} ———\r\n"); day = d; }
                    var body = x.Kind switch { "file" => "[file] " + x.File?.Name, "buzz" => "BUZZ!!!", "env" => "[đổi khung cảnh]", _ => x.Text };
                    sb.Append(x.Kind is "sys" or "leave" ? $"[{at:HH:mm}] * {body}\r\n" : $"[{at:HH:mm}] {x.FromName}: {body}\r\n");
                }
                try { File.WriteAllText(dlg.FileName, sb.ToString(), new System.Text.UTF8Encoding(true)); Post(new { t = "error", text = "Đã lưu " + Path.GetFileName(dlg.FileName) }); }
                catch (Exception ex) { Post(new { t = "error", text = ex.Message }); }
                break;
            }
            case "close":
                Close();
                break;
        }
    }
}
