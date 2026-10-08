using System.Security.Cryptography;
using System.Text;

namespace Bcode.Shared;

/// <summary>
/// Cách tạo / kiểm tra mã bản quyền gắn với MỘT máy: mã = bcrypt( SHA-256( "{key gốc}|{TÊN MÁY}|{IP}" ) ).
/// Dùng chung (file được LINK vào cả Bcode.App và công cụ cấp mã Bcode.KeyGen) để hai bên luôn tính cùng một công thức.
/// SHA-256 trước khi đưa vào bcrypt vì bcrypt chỉ đọc 72 byte đầu — tên máy / IP nằm cuối chuỗi sẽ bị bỏ qua nếu không băm trước.
/// Mã máy người dùng gửi cho người cấp có dạng <c>TÊN_MÁY|IP</c> (xem <see cref="MachineCode"/>).
/// </summary>
internal static class LicenseCodec
{
    public const int WorkFactor = 11;

    /// <summary>"TÊNMÁY|IP" — chuẩn hoá hoa/thường và khoảng trắng để cấp và kiểm tra khớp nhau.</summary>
    public static string MachineCode(string machineName, string ip) => $"{(machineName ?? "").Trim().ToUpperInvariant()}|{(ip ?? "").Trim()}";

    /// <summary>Tách "TÊNMÁY|IP"; false nếu sai dạng.</summary>
    public static bool TryParseMachineCode(string? code, out string machineName, out string ip)
    {
        machineName = ip = "";
        var parts = (code ?? "").Trim().Split('|');
        if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0) return false;
        machineName = parts[0].Trim().ToUpperInvariant();
        ip = parts[1].Trim();
        return true;
    }

    // ---- Mã máy dạng chuỗi mờ: người dùng chỉ thấy "BM1-xxxx", người cấp (có key gốc) mới giải ra được "TÊN_MÁY|IP" -------------------------------

    private const string MachinePrefix = "BM1-";

    private static byte[] MachineKey(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes("machine|" + secret.Trim()));

    /// <summary>"TÊN_MÁY|IP" → chuỗi mờ (AES-GCM, khoá từ key gốc). Cùng máy + cùng IP luôn ra cùng một chuỗi (nonce suy từ nội dung) nên không đổi mỗi lần mở.</summary>
    public static string EncodeMachine(string secret, string machineCode)
    {
        var key = MachineKey(secret);
        var plain = Encoding.UTF8.GetBytes(machineCode);
        var nonce = HMACSHA256.HashData(key, plain)[..12];
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var gcm = new AesGcm(key, 16)) gcm.Encrypt(nonce, plain, cipher, tag);
        var blob = new byte[28 + cipher.Length];
        nonce.CopyTo(blob, 0); tag.CopyTo(blob, 12); cipher.CopyTo(blob, 28);
        return MachinePrefix + Convert.ToBase64String(blob).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Chuỗi mờ (hoặc "TÊN_MÁY|IP" thô) → "TÊN_MÁY|IP". False nếu sai dạng / sai key gốc.</summary>
    public static bool TryDecodeMachine(string secret, string? text, out string machineCode)
    {
        machineCode = "";
        var t = (text ?? "").Trim();
        if (t.Contains('|')) { machineCode = t; return true; }
        if (!t.StartsWith(MachinePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var b64 = t[MachinePrefix.Length..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var raw = Convert.FromBase64String(b64);
            if (raw.Length <= 28) return false;
            var plain = new byte[raw.Length - 28];
            using var gcm = new AesGcm(MachineKey(secret), 16);
            gcm.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(28), raw.AsSpan(12, 16), plain);
            machineCode = Encoding.UTF8.GetString(plain);
            return true;
        }
        catch { return false; }
    }

    private static string Material(string secret, string machineCode) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret.Trim() + "|" + machineCode)));

    /// <summary>Cấp mã cho máy <paramref name="machineCode"/> (mỗi lần gọi ra chuỗi khác nhau nhưng đều hợp lệ — bcrypt có salt ngẫu nhiên).</summary>
    public static string Issue(string secret, string machineCode) =>
        BCrypt.Net.BCrypt.HashPassword(Material(secret, machineCode), WorkFactor);

    /// <summary>Mã dán vào có hợp lệ cho một trong các (tên máy, IP) của máy này không.</summary>
    public static bool Verify(string secret, string? license, string machineName, IEnumerable<string> ips)
    {
        var lic = (license ?? "").Trim();
        if (lic.Length == 0 || secret.Trim().Length == 0) return false;
        try
        {
            foreach (var ip in ips)
                if (BCrypt.Net.BCrypt.Verify(Material(secret, MachineCode(machineName, ip)), lic)) return true;
        }
        catch { /* chuỗi bcrypt hỏng */ }
        return false;
    }
}
