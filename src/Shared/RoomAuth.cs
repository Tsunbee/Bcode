using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bcode.Shared;

/// <summary>
/// Mật khẩu phòng LAN (Xem chung + game Giải lao) — kiểm bằng challenge–response, KHÔNG gửi mật khẩu hay hash dùng lại được trên dây:
/// <list type="number">
/// <item>Gói tìm phòng UDP chỉ có cờ <c>lock</c> (không salt, không gì suy ra được mật khẩu).</item>
/// <item>Khách gửi "hello" kèm <c>auth = <see cref="Cap"/></c>. Phòng có khoá → chủ phòng trả <c>{t:"challenge", salt, nonce, iter}</c>
///   (nonce 16 byte ngẫu nhiên, MỚI cho mỗi lần thử, chỉ dùng 1 lần).</item>
/// <item>Khách tính khoá = PBKDF2-SHA256(mật khẩu NFC, salt, iter) rồi gửi <c>{t:"auth", proof = HMAC-SHA256(khoá, "bcroom-auth1|proto|salt|nonce")}</c>.</item>
/// <item>Chủ phòng so (thời gian hằng). Sai → nonce mới + "badpass" (trễ 0,5 s, tối đa <see cref="RoomGate{T}.MaxTries"/> lần / kết nối);
///   đúng → cho vào như cũ ("welcome"). Gói "auth" chép lại (replay) không qua được vì nonce đã đổi.</item>
/// </list>
/// Tương thích: khách bản cũ không có <c>auth</c> → phòng có khoá từ chối kèm lời nhắc cập nhật; phòng không khoá chạy y như cũ.
/// Chủ phòng tự nối vào 127.0.0.1 bằng HostToken nên không phải nhập mật khẩu. Không ghi mật khẩu vào log.
/// File LINK (không chép) vào Bcode.Game và Bcode.Watch.
/// </summary>
internal static class RoomAuth
{
    public const string Cap = "pass1";
    public const int Iterations = 100_000;
    private const int MinIter = 10_000, MaxIter = 2_000_000;   // khách không tin số vòng do máy lạ đưa (chống làm treo máy)

    /// <summary>Hello của khách có hỗ trợ mật khẩu không (khách bản cũ: không).</summary>
    public static bool Supports(string? auth) => auth == Cap;

    public const string OldClientText = "Phòng có mật khẩu — máy bạn đang chạy bản cũ chưa hỗ trợ, cập nhật Bcode rồi vào lại.";

    public static byte[] DeriveKey(string password, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)), salt,
            Math.Clamp(iterations, MinIter, MaxIter), HashAlgorithmName.SHA256, 32);

    public static byte[] Proof(byte[] key, string proto, byte[] salt, byte[] nonce) =>
        HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"bcroom-auth1|{proto}|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(nonce)}"));

    internal static byte[]? FromB64(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 200) return null;
        try { return Convert.FromBase64String(s); } catch { return null; }
    }
}

public enum GateResult { NotPending, Ok, Wrong, TooMany }

/// <summary>Phía chủ phòng: giữ khoá suy từ mật khẩu (không giữ mật khẩu) + các kết nối đang chờ trả lời challenge.
/// <typeparamref name="T"/> = thông tin "hello" của khách để cho vào khi trả lời đúng. Gọi từ luồng nào cũng được (có khoá riêng).</summary>
internal sealed class RoomGate<T> where T : class
{
    public const int MaxTries = 5;
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);
    private readonly string _proto;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _key;
    private readonly Dictionary<object, Pending> _pending = new();

    private sealed class Pending { public T Hello = null!; public byte[] Nonce = null!; public int Tries; }

    private RoomGate(string password, string proto)
    {
        _proto = proto;
        _key = RoomAuth.DeriveKey(password, _salt, RoomAuth.Iterations);
    }

    /// <summary>Mật khẩu rỗng → null (phòng mở như cũ).</summary>
    public static RoomGate<T>? Create(string? password, string proto) => string.IsNullOrEmpty(password) ? null : new RoomGate<T>(password, proto);

    /// <summary>Ghi nhận khách đang chờ + gửi challenge. Quá <see cref="Timeout"/> chưa vào được thì cắt kết nối.</summary>
    public void Challenge(object connKey, Action<object> send, Action<string> close, string room, T hello)
    {
        var p = new Pending { Hello = hello, Nonce = RandomNumberGenerator.GetBytes(16) };
        lock (_pending) _pending[connKey] = p;
        send(Msg("challenge", p.Nonce, room, null));
        _ = Task.Delay(Timeout).ContinueWith(_ =>
        {
            bool still;
            lock (_pending) still = _pending.TryGetValue(connKey, out var cur) && cur == p;
            if (!still) return;
            Drop(connKey);
            send(new { t = "reject", text = "Quá lâu chưa nhập mật khẩu.", reason = "Quá lâu chưa nhập mật khẩu." });
            _ = Task.Delay(300).ContinueWith(_ => close("Hết giờ nhập mật khẩu"));
        });
    }

    /// <summary>Kiểm tin {t:"auth", proof}. Ok → <paramref name="hello"/> = thông tin để cho vào. Wrong → đã tự gửi "badpass" (kèm nonce mới, trễ 0,5 s).
    /// TooMany → bên gọi gửi reject + đóng.</summary>
    public GateResult Answer(object connKey, string? proofB64, Action<object> send, string room, out T? hello)
    {
        hello = null;
        Pending? p;
        object msg;
        lock (_pending)
        {
            if (!_pending.TryGetValue(connKey, out p)) return GateResult.NotPending;
            var expect = RoomAuth.Proof(_key, _proto, _salt, p.Nonce);
            var got = RoomAuth.FromB64(proofB64);
            // nonce cũ bỏ ngay — dù đúng hay sai, gói này không dùng lại được
            p.Nonce = RandomNumberGenerator.GetBytes(16);
            if (got is not null && CryptographicOperations.FixedTimeEquals(expect, got))
            {
                _pending.Remove(connKey);
                hello = p.Hello;
                return GateResult.Ok;
            }
            if (++p.Tries >= MaxTries) { _pending.Remove(connKey); return GateResult.TooMany; }
            msg = Msg("badpass", p.Nonce, room, $"Sai mật khẩu (còn {MaxTries - p.Tries} lần thử).");
        }
        _ = Task.Delay(500).ContinueWith(_ => send(msg));
        return GateResult.Wrong;
    }

    public bool IsPending(object connKey) { lock (_pending) return _pending.ContainsKey(connKey); }

    public void Drop(object connKey) { lock (_pending) _pending.Remove(connKey); }

    private object Msg(string t, byte[] nonce, string room, string? text) =>
        new { t, salt = Convert.ToBase64String(_salt), nonce = Convert.ToBase64String(nonce), iter = RoomAuth.Iterations, room, text };
}

/// <summary>Phía khách: chặn tin "challenge" / "badpass" trước khi vào phòng, tự trả lời nếu đã có mật khẩu, không thì báo
/// <see cref="NeedPassword"/> để trang hỏi người dùng. Sau "welcome" không đọc tin nữa (không tốn công parse mỗi tin).</summary>
internal sealed class RoomKeyClient
{
    private readonly string _proto;
    private readonly Action<object> _send;
    private string? _password;
    private byte[]? _salt, _nonce;
    private int _iter = RoomAuth.Iterations;
    private string _room = "";
    private volatile bool _admitted;

    private Action<string?, string>? _need;
    private (string? Text, string Room)? _missed;

    /// <summary>(lời báo lỗi hoặc null, tên phòng) — gọi từ luồng mạng. Challenge tới trước khi kịp đăng ký (loopback rất nhanh)
    /// thì lúc đăng ký được gọi bù ngay.</summary>
    public event Action<string?, string>? NeedPassword
    {
        add
        {
            (string? Text, string Room)? missed;
            lock (this) { _need += value; missed = _missed; _missed = null; }
            if (missed is { } x) value?.Invoke(x.Text, x.Room);
        }
        remove { lock (this) _need -= value; }
    }

    private void RaiseNeed(string? text, string room)
    {
        Action<string?, string>? h;
        lock (this) { h = _need; if (h is null) _missed = (text, room); }
        h?.Invoke(text, room);
    }

    public RoomKeyClient(string proto, string? password, Action<object> send)
    {
        _proto = proto;
        _password = string.IsNullOrEmpty(password) ? null : password;
        _send = send;
    }

    /// <summary>true = tin đã xử lý ở đây (không chuyển cho trang).</summary>
    public bool Intercept(byte[] frame)
    {
        if (_admitted) return false;
        JsonObject? m;
        try { m = JsonNode.Parse(frame)?.AsObject(); } catch { return false; }
        var t = (string?)m?["t"];
        if (t == "welcome") { _admitted = true; _password = null; return false; }
        if (t != "challenge" && t != "badpass") return false;
        lock (this)
        {
            _salt = RoomAuth.FromB64((string?)m!["salt"]);
            _nonce = RoomAuth.FromB64((string?)m["nonce"]);
            _iter = (int?)m["iter"] ?? RoomAuth.Iterations;
            _room = (string?)m["room"] ?? "";
            if (t == "badpass") _password = null;
        }
        if (_salt is null || _nonce is null) return true;
        if (_password is not null) Respond(_password);
        else RaiseNeed(t == "badpass" ? (string?)m!["text"] ?? "Sai mật khẩu." : null, _room);
        return true;
    }

    /// <summary>Người dùng nhập mật khẩu (sau <see cref="NeedPassword"/>).</summary>
    public void Submit(string password)
    {
        if (_admitted || string.IsNullOrEmpty(password)) return;
        lock (this) _password = password;
        Respond(password);
    }

    private void Respond(string password)
    {
        byte[] salt, nonce; int iter;
        lock (this)
        {
            if (_salt is null || _nonce is null) return;   // chưa có challenge: giữ mật khẩu, trả lời khi challenge tới
            salt = _salt; nonce = _nonce; iter = _iter;
            _nonce = null;                                    // mỗi nonce chỉ trả lời 1 lần
        }
        _ = Task.Run(() =>
        {
            var proof = RoomAuth.Proof(RoomAuth.DeriveKey(password, salt, iter), _proto, salt, nonce);
            _send(new { t = "auth", proof = Convert.ToBase64String(proof) });
        });
    }
}
