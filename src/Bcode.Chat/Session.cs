using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Bcode.Chat.Net;

namespace Bcode.Chat;

/// <summary>
/// Một phiên đăng nhập Bhoo: 1 <see cref="ChatNode"/> + cửa sổ danh sách bạn (<see cref="BuddyForm"/>) + mỗi cuộc chat 1 cửa sổ riêng (<see cref="ImForm"/>)
/// như messenger ngày xưa. Tin mới tới mà chưa mở cửa sổ thì tự mở (không giành focus) + nháy taskbar + kêu; BUZZ thì rung cửa sổ.
/// Mọi sự kiện mạng được chuyển về luồng giao diện (<see cref="ChatApp.Ui"/>).
/// </summary>
internal sealed class Session
{
    private readonly ChatApp _app;
    private readonly ChatNode _node;
    private readonly ChatStore _store;
    private readonly Dictionary<string, ImForm> _ims = new();
    private readonly System.Windows.Forms.Timer _idleTimer = new() { Interval = 15000 };
    private readonly long _startedAt = Environment.TickCount64;
    private readonly Dictionary<string, string> _downloaded = new();   // fid → đường dẫn đã tải (trong lần chạy này)
    private long _lastConflictWarn;
    private bool _autoAway;
    private bool _closed;

    public ChatApp App => _app;
    public ChatNode Node => _node;
    public ChatStore Store => _store;
    public BuddyForm Buddy { get; }

    public Session(ChatApp app, ChatStore store, ChatNode node)
    {
        _app = app;
        _store = store;
        _node = node;
        Sounds.Enabled = store.Prefs.Sound;
        Buddy = new BuddyForm(this);

        _node.BuddiesChanged += () => Ui(() => Broadcast(new { t = "buddies", list = BuddyList() }));
        _node.BuddyOnline += b => Ui(() => OnBuddyOnline(b));
        _node.MessageReceived += (conv, m) => Ui(() => OnMessage(conv, m));
        _node.Sent += (conv, m) => Ui(() =>
        {
            FindIm(conv)?.Post(new { t = "msg", m, mine = true, pending = _node.Pending(m.Mid) });
            Buddy.Post(new { t = "buddies", list = BuddyList() });   // số tin chờ gửi
        });
        _node.Delivered += (conv, mid, to) => Ui(() =>
        {
            FindIm(conv)?.Post(new { t = "delivered", mid, to });
            Buddy.Post(new { t = "buddies", list = BuddyList() });
        });
        _node.Typing += (conv, from) => Ui(() => FindIm(conv)?.Post(new { t = "typing", from, name = _node.FindBuddy(from)?.Name ?? "" }));
        _node.FileProgress += (fid, got, total, state, info) => Ui(() =>
        {
            if (state == "done") _downloaded[fid] = info;
            foreach (var f in _ims.Values) f.Post(new { t = "file", fid, got, total, state, info });
            if (state == "done") Sounds.Play("file");
        });
        _node.FileServed += (fid, who) => Ui(() =>
        {
            var name = _node.FindBuddy(who)?.Name ?? "Bạn";
            foreach (var f in _ims.Values) f.Post(new { t = "served", fid, name });
        });
        _node.GroupChanged += g => Ui(() =>
        {
            Broadcast(new { t = "groups", list = _node.Groups() });
            if (FindIm("g:" + g.Gid) is { } f) f.UpdateTitle();
        });
        _node.NickConflict += ip => Ui(() =>
        {
            // có máy khác đang thử dùng (hoặc cùng lúc dùng) nick của mình — chỉ báo, không đá ai ra
            if (Environment.TickCount64 - _lastConflictWarn < 60_000) return;
            _lastConflictWarn = Environment.TickCount64;
            Buddy.Post(new { t = "warn", text = $"Máy {ip} vừa thử đăng nhập bằng Bhoo ID của bạn." });
            _app.Balloon("Bhoo", $"Máy {ip} vừa thử đăng nhập bằng Bhoo ID của bạn.", null);
        });

        _idleTimer.Tick += (_, _) => CheckIdle();
        _idleTimer.Start();
    }

    private void Ui(Action a) => _app.Ui(() => { if (!_closed) a(); });

    public void Broadcast(object msg)
    {
        Buddy.Post(msg);
        foreach (var f in _ims.Values) f.Post(msg);
    }

    public object Me() => new
    {
        id = _node.MyId, name = _store.Prefs.Name, status = _store.Prefs.Status, text = _store.Prefs.Text, color = _store.Prefs.Color,
        port = _node.Port, ips = ChatNode.LocalIPv4(), udpError = _node.UdpError, peers = _store.Prefs.ManualPeers,
        sound = _store.Prefs.Sound, closeToTray = _store.Prefs.CloseToTray, autoAway = _store.Prefs.AutoAwayMinutes,
        font = new { style = _store.Prefs.FontStyle, fg = _store.Prefs.FontColor, size = _store.Prefs.FontSize },
    };

    public List<object> BuddyList() => _node.Buddies().Select(b => (object)new
    {
        id = b.Id, name = b.Name, status = b.Status, text = b.Text, color = b.Color, group = b.Group, ip = b.Ip, hidden = b.Hidden,
        lastSeen = b.LastSeen == default ? 0 : new DateTimeOffset(DateTime.SpecifyKind(b.LastSeen, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
        pending = _node.PendingCount("u:" + b.Id),
    }).ToList();

    public void SetMyStatus(string status, string? text = null)
    {
        _autoAway = false;
        _node.SetMe(status: status, text: text);
        Broadcast(new { t = "me", me = Me() });
    }

    private void OnBuddyOnline(Buddy b)
    {
        if (Environment.TickCount64 - _startedAt < 6000) return;   // mới đăng nhập: cả danh sách cùng "online", không kêu loạn
        Sounds.Play("online");
        if (_store.Prefs.Status != Status.Busy) _app.Balloon(b.Name, "vừa online" + (b.Text.Length > 0 ? " — " + b.Text : ""), "u:" + b.Id);
    }

    private void OnMessage(string conv, ChatMessage m)
    {
        var f = FindIm(conv) ?? OpenIm(conv, activate: false);
        f.Post(new { t = "msg", m, mine = false });
        if (m.Kind == "buzz")
        {
            Sounds.Play("buzz");
            f.Shake();
        }
        else
        {
            if (_store.Prefs.Status != Status.Busy) Sounds.Play(m.Kind == "file" ? "file" : "msg");   // Bận: chỉ nháy, không kêu
            if (!f.ContainsFocus) f.Flash();
        }
        Buddy.Post(new { t = "buddies", list = BuddyList() });
    }

    public ImForm? FindIm(string conv) => _ims.TryGetValue(conv, out var f) && !f.IsDisposed ? f : null;

    public ImForm OpenIm(string conv, bool activate)
    {
        if (FindIm(conv) is { } f)
        {
            if (activate)
            {
                if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
                f.Show(); f.Activate();
            }
            return f;
        }
        f = new ImForm(this, conv, activate);
        _ims[conv] = f;
        f.FormClosed += (_, _) => { if (_ims.TryGetValue(conv, out var cur) && cur == f) _ims.Remove(conv); };
        f.Show();
        if (!activate) f.Flash();
        return f;
    }

    public string Title(string conv)
    {
        if (conv.StartsWith("g:")) return _node.FindGroup(conv[2..])?.Title ?? "Phòng chat nhóm";
        return _node.FindBuddy(conv[2..])?.Name ?? conv[2..];
    }

    /// <summary>Lịch sử 1 cuộc chat cho cửa sổ vừa mở: tin + ai còn chờ nhận + file đã tải.</summary>
    public object History(string conv) => _store.LoadHistory(conv).Select(m => (object)new
    {
        m, mine = m.From == _node.MyId,
        pending = m.From == _node.MyId ? _node.Pending(m.Mid) : null,
        saved = m.File is { } fm && _downloaded.TryGetValue(fm.Fid, out var p) ? p : null,
    }).ToList();

    public void SetPrefs(JsonObject m)
    {
        var p = _store.Prefs;
        if (m["sound"] is JsonValue s) Sounds.Enabled = p.Sound = (bool)s;
        if (m["closeToTray"] is JsonValue c) p.CloseToTray = (bool)c;
        if (m["autoAway"] is JsonValue a) p.AutoAwayMinutes = Math.Clamp((int)a, 0, 240);
        _store.SavePrefs();
        Broadcast(new { t = "me", me = Me() });
    }

    public void SaveFont(string? style, string? fg, int? size)
    {
        var p = _store.Prefs;
        p.FontStyle = style ?? "";
        if (fg is not null && System.Text.RegularExpressions.Regex.IsMatch(fg, "^#[0-9a-fA-F]{6}$")) p.FontColor = fg;
        if (size is >= 10 and <= 22) p.FontSize = size.Value;
        _store.SavePrefs();
    }

    public async Task<object> FirewallStatus()
    {
        var st = await Task.Run(() => Firewall.Check(Environment.ProcessPath ?? Application.ExecutablePath, _node.Port));
        return new { t = "fw", level = st.Level, text = st.Text };
    }

    public async Task<object> OpenFirewall()
    {
        var err = await Firewall.OpenAsync(Environment.ProcessPath ?? Application.ExecutablePath);
        if (err is not null) return new { t = "fw", level = "bad", text = err };
        return await FirewallStatus();
    }

    // ───────────── Tự "Vắng" khi không đụng máy ─────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    private void CheckIdle()
    {
        var mins = _store.Prefs.AutoAwayMinutes;
        var li = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref li)) return;
        var idleMs = (uint)Environment.TickCount - li.dwTime;
        if (mins > 0 && !_autoAway && _store.Prefs.Status == Status.Online && idleMs > mins * 60_000u)
        {
            _node.SetMe(status: Status.Away);
            _autoAway = true;
            Broadcast(new { t = "me", me = Me() });
        }
        else if (_autoAway && idleMs < 20_000)
        {
            _autoAway = false;
            _node.SetMe(status: Status.Online);
            Broadcast(new { t = "me", me = Me() });
        }
    }

    /// <summary>Đăng xuất / thoát: đóng mọi cửa sổ, báo "offline" cho cả LAN, ghi dữ liệu.</summary>
    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _idleTimer.Stop();
        foreach (var f in _ims.Values.ToList()) { try { f.Close(); } catch { } }
        Buddy.AllowClose = true;
        try { Buddy.Close(); } catch { }
        _node.Dispose();
    }
}
