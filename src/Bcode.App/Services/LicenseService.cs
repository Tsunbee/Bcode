using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Bcode.Shared;

namespace Bcode.App.Services;

/// <summary>
/// Bản quyền cho các tính năng: key chung mở <b>Create RPT &amp; XML</b>, <b>Excel → FRX</b>, Tạo nhanh danh mục…; <b>Decrypt SQL Object</b> cần key RIÊNG
/// (<see cref="Kind.Decrypt"/>, file license-decrypt.key, công thức bcrypt ghép khác) — key chung không mở được Decrypt.
/// Mã bản quyền gắn với TỪNG MÁY: mỗi máy có "Mã máy" = <c>TÊN_MÁY|ĐỊNH_DANH</c> (xem <see cref="MachineCode"/>); người phát hành dùng công cụ Bcode.KeyGen
/// (key gốc trong <see cref="LicenseSecret.Key"/> + mã máy) để sinh chuỗi bcrypt cho đúng máy đó (công thức ở <c>Shared/LicenseCodec.cs</c>).
/// Người dùng dán MỘT LẦN ở Settings → "Key bản quyền"; mã được nhớ ở %AppData%\Bcode\license.key và kiểm tra lại mỗi lần mở Bcode.
///
/// ĐỊNH_DANH đi theo máy, không theo mạng (laptop mang về nhà / đổi Wi-Fi / VPN vẫn giữ key): <c>G-&lt;MachineGuid của Windows&gt;</c>,
/// máy không đọc được thì <c>U-&lt;UUID bo mạch (SMBIOS)&gt;</c>; cả hai đều không có mới dùng IP như bản cũ. MachineGuid đổi khi cài lại Windows
/// (và trùng nhau giữa các máy ảo nhân bản không sysprep) → khi đó xin cấp lại.
/// Key cấp theo IP trước đây VẪN dùng được: kiểm tra khớp với định danh phần cứng VÀ mọi IPv4 hiện có của máy.
/// Đổi key gốc trong source + build mới, hoặc đổi tên máy / cài lại Windows (mã không còn khớp) thì mã cũ hết hiệu lực — xin cấp lại.
/// </summary>
public static class LicenseService
{
    /// <summary>Hai loại key, cấp và lưu riêng: key chung (Excel → FRX, Create RPT &amp; XML, Tạo nhanh danh mục, Tạo báo cáo…) và key riêng cho
    /// Decrypt SQL Object + Theo dõi ngoài Bcode (công thức bcrypt ghép khác — <see cref="LicenseCodec.Kind"/>).</summary>
    public enum Kind { General, Decrypt, DecryptF }

    private static readonly Dictionary<Kind, bool?> _unlocked = new() { [Kind.General] = null, [Kind.Decrypt] = null, [Kind.DecryptF] = null };

    /// <summary>Báo khi key được lưu hoặc xoá (để làm mới nhãn menu).</summary>
    public static event Action? Changed;

    private static string KeyPath(Kind kind) => Path.Combine(BcodePaths.AppData, "Bcode",
        kind switch { Kind.Decrypt => "license-decrypt.key", Kind.DecryptF => "license-decryptf.key", _ => "license.key" });

    private static LicenseCodec.Kind CodecKind(Kind kind) =>
        kind switch { Kind.Decrypt => LicenseCodec.Kind.Decrypt, Kind.DecryptF => LicenseCodec.Kind.DecryptF, _ => LicenseCodec.Kind.General };

    /// <summary>Tên hiển thị + các tính năng mở khoá của từng loại key (hộp thoại Key bản quyền).</summary>
    public static (string Title, string[] Features) Describe(Kind kind) => kind switch
    {
        Kind.DecryptF => ("Key đặc biệt", new[] { "Mở chức năng đặc biệt" }),
        Kind.Decrypt => ("Key Decrypt SQL Object", new[] { "Decrypt SQL Object (giải mã object WITH ENCRYPTION)", "Theo dõi ngoài Bcode (cài / gỡ DDL trigger ghi thay đổi procedure / function do SSMS, tool khác)" }),
        _ => ("Key bản quyền", new[]
        {
            "Create RPT & XML (Excel / Pivot Excel mẫu + header report XML)", "Excel → FRX / RPT (mẫu in Excel → FastReport / Crystal)",
            "Tạo báo cáo · Báo cáo yêu cầu FSG · Thiết kế màn hình", "Khai báo API · Tạo cấu trúc API", "Tạo nhanh danh mục · Clone danh mục",
        }),
    };

    /// <summary>Key chung đã lưu có hợp lệ cho máy này không (tính 1 lần rồi nhớ — bcrypt chạy chậm có chủ đích).</summary>
    public static bool IsUnlocked => IsUnlockedFor(Kind.General);

    /// <summary>Key Decrypt SQL Object (key riêng) đã lưu có hợp lệ cho máy này không.</summary>
    public static bool IsDecryptUnlocked => IsUnlockedFor(Kind.Decrypt);

    /// <summary>Key đặc biệt (key riêng) đã lưu có hợp lệ cho máy này không.</summary>
    public static bool IsDecryptFUnlocked => IsUnlockedFor(Kind.DecryptF);

    public static bool IsUnlockedFor(Kind kind)
    {
        lock (_unlocked) if (_unlocked[kind] is bool b) return b;
        var ok = Check(LoadKey(kind), kind);
        lock (_unlocked) _unlocked[kind] = ok;
        return ok;
    }

    public static bool HasSavedKey => HasSavedKeyFor(Kind.General);
    public static bool HasSavedKeyFor(Kind kind) => LoadKey(kind).Length > 0;

    /// <summary>Mã máy để gửi cho người cấp key: chuỗi mờ "BM1-…" (bên trong là TÊN_MÁY|G-MachineGuid — hoặc U-UUID bo mạch, cùng lắm là IP —
    /// chỉ người có key gốc giải ra được).</summary>
    public static string MachineCode => LicenseCodec.EncodeMachine(LicenseSecret.Key,
        LicenseCodec.MachineCode(Environment.MachineName, HardwareIds.FirstOrDefault() ?? LocalIps().FirstOrDefault() ?? "0.0.0.0"));

    /// <summary>Định danh phần cứng của máy, ưu tiên trước: "G-&lt;MachineGuid&gt;", "U-&lt;UUID bo mạch&gt;". Đọc 1 lần (không đổi trong lúc chạy).</summary>
    internal static IReadOnlyList<string> HardwareIds => _hardwareIds.Value;

    private static readonly Lazy<IReadOnlyList<string>> _hardwareIds = new(() =>
    {
        var ids = new List<string>();
        if (WindowsMachineGuid() is { } g) ids.Add("G-" + g);
        if (BoardUuid() is { } u) ids.Add("U-" + u);
        return ids;
    });

    /// <summary>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid (tạo lúc cài Windows) — đọc nhánh 64-bit để Bcode 32/64-bit ra cùng giá trị; không cần quyền admin.</summary>
    private static string? WindowsMachineGuid()
    {
        try
        {
            using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") is string s && Guid.TryParse(s, out var g) && g != Guid.Empty ? g.ToString("D") : null;
        }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId, byte[]? buffer, uint bufferSize);

    /// <summary>
    /// UUID hệ thống trong SMBIOS (bảng loại 1 — cùng giá trị "UUID" của Win32_ComputerSystemProduct), đi theo bo mạch, còn nguyên khi cài lại Windows.
    /// Bo mạch ghi UUID rỗng (toàn 00 / toàn FF) thì bỏ.
    /// </summary>
    private static string? BoardUuid()
    {
        try
        {
            const uint rsmb = 0x52534D42; // 'RSMB'
            var size = GetSystemFirmwareTable(rsmb, 0, null, 0);
            if (size < 8) return null;
            var buf = new byte[size];
            if (GetSystemFirmwareTable(rsmb, 0, buf, size) != size) return null;
            // RawSMBIOSData: 4 byte phiên bản + 4 byte độ dài, sau đó là các bảng (phần định dạng + chuỗi kết thúc bằng 2 byte 0).
            var end = Math.Min(buf.Length, 8 + BitConverter.ToInt32(buf, 4));
            for (var pos = 8; pos + 4 <= end;)
            {
                int type = buf[pos], len = buf[pos + 1];
                if (len < 4) break;
                if (type == 1 && len >= 0x19 && pos + 0x18 <= end)
                {
                    var raw = buf.AsSpan(pos + 8, 16);
                    if (raw.IndexOfAnyExcept((byte)0) < 0 || raw.IndexOfAnyExcept((byte)0xFF) < 0) return null;
                    return new Guid(raw).ToString("D");   // 3 trường đầu little-endian (SMBIOS ≥ 2.6) — khớp cách Guid đọc byte
                }
                if (type == 127) break;
                var p = pos + len;
                while (p + 1 < end && (buf[p] != 0 || buf[p + 1] != 0)) p++;
                pos = p + 2;
            }
        }
        catch { /* không đọc được firmware table → không có UUID */ }
        return null;
    }

    /// <summary>Các IPv4 của máy, địa chỉ có gateway (đang ra mạng thật) xếp trước; bỏ loopback và link-local 169.254.x.x.</summary>
    private static List<string> LocalIps()
    {
        var withGateway = new List<string>();
        var others = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var u in props.UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = u.Address.ToString();
                    if (s.StartsWith("169.254.", StringComparison.Ordinal) || s.StartsWith("127.", StringComparison.Ordinal)) continue;
                    (hasGateway ? withGateway : others).Add(s);
                }
            }
        }
        catch { /* không đọc được card mạng: danh sách rỗng → mã máy dùng 0.0.0.0, không mã nào khớp */ }
        return withGateway.Concat(others).Distinct().ToList();
    }

    public static string LoadKey(Kind kind = Kind.General)
    {
        try { var p = KeyPath(kind); return File.Exists(p) ? File.ReadAllText(p).Trim() : ""; }
        catch { return ""; }
    }

    /// <summary>Kiểm tra mã vừa dán; khớp thì lưu (dán 1 lần, lần sau không cần dán lại) và mở khoá.</summary>
    public static (bool Ok, string Message) Save(string? pasted, Kind kind = Kind.General)
    {
        var key = (pasted ?? "").Trim();
        if (key.Length == 0) return (false, "Chưa dán mã.");
        if (!Check(key, kind))
        {
            // dán nhầm loại key (key chung vào ô Decrypt hay ngược lại): báo rõ thay vì chỉ "không đúng"
            var other = kind == Kind.Decrypt ? Kind.General : Kind.Decrypt;
            return (false, Check(key, other)
                ? $"Đây là {Describe(other).Title.ToLowerInvariant()}, không phải {Describe(kind).Title.ToLowerInvariant()} — dán vào đúng mục ở Settings."
                : "Mã không đúng cho máy này. Kiểm tra lại chuỗi đã dán, hoặc gửi lại Mã máy để được cấp mã mới.");
        }
        lock (_unlocked) _unlocked[kind] = true;
        try
        {
            var p = KeyPath(kind);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, key);
        }
        catch (Exception ex)
        {
            Changed?.Invoke();
            return (true, "Đã kích hoạt nhưng chưa nhớ được mã cho lần mở sau (" + ex.Message + ").");
        }
        Changed?.Invoke();
        return (true, kind == Kind.Decrypt
            ? "Đã kích hoạt: có thể dùng Decrypt SQL Object và Theo dõi ngoài Bcode. Mã được nhớ lại, lần sau không cần dán lại."
            : "Đã kích hoạt: có thể dùng Create RPT & XML, Excel → FRX, Tạo nhanh danh mục… Mã được nhớ lại, lần sau không cần dán lại.");
    }

    /// <summary>Xoá mã đã lưu (khoá lại các tính năng của loại key đó).</summary>
    public static void Clear(Kind kind = Kind.General)
    {
        try { var p = KeyPath(kind); if (File.Exists(p)) File.Delete(p); } catch { /* không xoá được: cờ dưới đây vẫn khoá ngay lúc này */ }
        lock (_unlocked) _unlocked[kind] = false;
        Changed?.Invoke();
    }

    /// <summary>Mã có hợp lệ cho máy này không (không lưu gì): khớp định danh phần cứng (key mới) hoặc một IPv4 hiện có (key cấp theo IP trước đây).</summary>
    public static bool Check(string? pasted, Kind kind = Kind.General) =>
        LicenseCodec.Verify(LicenseSecret.Key, pasted, Environment.MachineName, HardwareIds.Concat(LocalIps()), CodecKind(kind));
}
