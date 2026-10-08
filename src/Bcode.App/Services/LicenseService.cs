using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Bcode.Shared;

namespace Bcode.App.Services;

/// <summary>
/// Bản quyền cho các tính năng: <b>Decrypt SQL Object</b>, <b>Create RPT &amp; XML</b> (tạo Excel / Pivot Excel mẫu) và <b>Excel → FRX</b>.
/// Mã bản quyền gắn với TỪNG MÁY: mỗi máy có "Mã máy" = <c>TÊN_MÁY|IP</c> (xem <see cref="MachineCode"/>); người phát hành dùng công cụ Bcode.KeyGen
/// (key gốc trong <see cref="LicenseSecret.Key"/> + mã máy) để sinh chuỗi bcrypt cho đúng máy đó (công thức ở <c>Shared/LicenseCodec.cs</c>).
/// Người dùng dán MỘT LẦN ở Settings → "Key bản quyền"; mã được nhớ ở %AppData%\Bcode\license.key và kiểm tra lại mỗi lần mở Bcode.
/// Đổi key gốc trong source + build mới, hoặc đổi máy / đổi IP (mã không còn khớp) thì mã cũ hết hiệu lực — xin cấp lại.
/// Kiểm tra khớp với MỌI IPv4 hiện có của máy (không chỉ IP đang hiện ở "Mã máy"), nên máy nhiều card mạng / VPN vẫn dùng được.
/// </summary>
public static class LicenseService
{
    private static bool? _unlocked;

    /// <summary>Báo khi key được lưu hoặc xoá (để làm mới nhãn menu).</summary>
    public static event Action? Changed;

    private static string KeyPath => Path.Combine(BcodePaths.AppData, "Bcode", "license.key");

    /// <summary>Mã đã lưu có hợp lệ cho máy này không (tính 1 lần rồi nhớ — bcrypt chạy chậm có chủ đích).</summary>
    public static bool IsUnlocked => _unlocked ??= Check(LoadKey());

    public static bool HasSavedKey => LoadKey().Length > 0;

    /// <summary>Mã máy để gửi cho người cấp key: chuỗi mờ "BM1-…" (bên trong là TÊN_MÁY|IP chính của máy, chỉ người có key gốc giải ra được).</summary>
    public static string MachineCode => LicenseCodec.EncodeMachine(LicenseSecret.Key, LicenseCodec.MachineCode(Environment.MachineName, LocalIps().FirstOrDefault() ?? "0.0.0.0"));

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

    public static string LoadKey()
    {
        try { return File.Exists(KeyPath) ? File.ReadAllText(KeyPath).Trim() : ""; }
        catch { return ""; }
    }

    /// <summary>Kiểm tra mã vừa dán; khớp thì lưu (dán 1 lần, lần sau không cần dán lại) và mở khoá.</summary>
    public static (bool Ok, string Message) Save(string? pasted)
    {
        var key = (pasted ?? "").Trim();
        if (key.Length == 0) return (false, "Chưa dán mã.");
        if (!Check(key)) return (false, "Mã không đúng cho máy này (hoặc IP / tên máy đã đổi). Kiểm tra lại chuỗi đã dán, hoặc gửi lại Mã máy để được cấp mã mới.");
        _unlocked = true;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
            File.WriteAllText(KeyPath, key);
        }
        catch (Exception ex)
        {
            Changed?.Invoke();
            return (true, "Đã kích hoạt nhưng chưa nhớ được mã cho lần mở sau (" + ex.Message + ").");
        }
        Changed?.Invoke();
        return (true, "Đã kích hoạt: có thể dùng Decrypt SQL Object, Create RPT & XML và Excel → FRX. Mã được nhớ lại, lần sau không cần dán lại.");
    }

    /// <summary>Xoá mã đã lưu (khoá lại các tính năng).</summary>
    public static void Clear()
    {
        try { if (File.Exists(KeyPath)) File.Delete(KeyPath); } catch { /* không xoá được: cờ dưới đây vẫn khoá ngay lúc này */ }
        _unlocked = false;
        Changed?.Invoke();
    }

    /// <summary>Mã có hợp lệ cho máy này không (không lưu gì).</summary>
    public static bool Check(string? pasted) =>
        LicenseCodec.Verify(LicenseSecret.Key, pasted, Environment.MachineName, LocalIps());
}
