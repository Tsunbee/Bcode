using System.ComponentModel;
using System.Diagnostics;

namespace Bcode.Watch.Net;

/// <summary>Tường lửa Windows trên máy CHỦ PHÒNG — lỗi LAN hay gặp nhất: máy khác "hết giờ" khi nối tới cổng phòng (gói tin bị bỏ, không bị từ chối).
/// Thường do lần đầu mở phòng bấm Huỷ / không có quyền admin ở hộp hỏi tường lửa → Windows tự tạo luật CHẶN BcodeWatch.exe.
/// Check: chỉ ĐỌC luật qua COM HNetCfg.FwPolicy2 (không cần admin). Open: người dùng bấm nút → hộp UAC → netsh thêm luật cho phép (gỡ luật chặn cũ của exe).
/// Máy công ty đặt chính sách nhóm (GPO) bỏ qua luật trên máy thì nút cũng vô ích — báo rõ để nhờ IT hoặc đổi máy làm chủ phòng.</summary>
internal static class Firewall
{
    public sealed record Status(string Level, string Text);   // Level: ok | warn | bad

    private const int ProfDomain = 1, ProfPrivate = 2, ProfPublic = 4;
    private const string RuleName = "Bcode Xem chung";

    public static Status Check(string exePath, int tcpPort)
    {
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (type is null) return new("warn", "Không đọc được tường lửa Windows trên máy này.");
            dynamic pol = Activator.CreateInstance(type)!;
            int cur = pol.CurrentProfileTypes;
            var profs = new[] { ProfDomain, ProfPrivate, ProfPublic }.Where(p => (cur & p) != 0).ToArray();
            var on = profs.Where(p => (bool)pol.FirewallEnabled[p]).ToArray();
            var curName = string.Join(" + ", profs.Select(p => p == ProfDomain ? "Domain" : p == ProfPrivate ? "Private" : "Public"));
            if (on.Length == 0) return new("ok", $"Tường lửa đang tắt ({curName}) — máy khác vào được.");
            if (on.Any(p => (bool)pol.BlockAllInboundTraffic[p]))
                return new("bad", $"Tường lửa đang CHẶN MỌI kết nối vào máy này (mạng {curName}). Tắt tuỳ chọn \"Block all incoming connections\" hoặc để máy khác làm chủ phòng.");
            if (on.All(p => (int)pol.DefaultInboundAction[p] == 1)) return new("ok", "Tường lửa cho phép kết nối vào theo mặc định.");

            int mask = on.Aggregate(0, (a, p) => a | p);
            bool allowTcp = false, allowUdp = false, blocked = false;
            var exe = Norm(exePath);
            foreach (dynamic r in pol.Rules)
            {
                try
                {
                    if ((int)r.Direction != 1 || !(bool)r.Enabled || ((int)r.Profiles & mask) == 0) continue;
                    string? app = r.ApplicationName;
                    bool appMatch = !string.IsNullOrEmpty(app) && Norm(app!) == exe;
                    if (!string.IsNullOrEmpty(app) && !appMatch) continue;              // luật của chương trình khác
                    int proto = r.Protocol;
                    string ports = (string?)r.LocalPorts ?? "*";
                    bool tcp = (proto == 6 || proto == 256) && PortIn(ports, tcpPort) && PortIn(ports, Wire.MediaPort);
                    bool udp = (proto == 17 || proto == 256) && PortIn(ports, Wire.DiscoveryPort);
                    if (!appMatch && !tcp && !udp) continue;
                    if ((int)r.Action == 0) { if (appMatch || tcp) blocked = true; }
                    else { allowTcp |= appMatch || tcp; allowUdp |= appMatch || udp; }
                }
                catch { }
            }
            bool gp = false;
            try { gp = (int)pol.LocalPolicyModifyState == 1; } catch { }
            if (blocked) return new("bad", "Tường lửa đang có luật CHẶN BcodeWatch (thường do lần đầu bấm Huỷ ở hộp hỏi tường lửa) — máy khác sẽ báo \"không nối được\". Bấm 🛡 Mở tường lửa.");
            if (!allowTcp) return new("bad", $"Chưa có luật tường lửa cho phép BcodeWatch (mạng {curName}) — máy khác sẽ báo \"không nối được\". Bấm 🛡 Mở tường lửa.");
            if (gp) return new("warn", "Đã có luật cho phép, nhưng máy này theo chính sách công ty (GPO) có thể bỏ qua luật trên máy — nếu máy khác vẫn không vào được thì nhờ IT hoặc đổi máy làm chủ phòng.");
            if (!allowUdp) return new("warn", "Vào bằng IP được, nhưng gói tìm phòng (UDP 47830) có thể bị chặn — máy khác gõ IP để vào. Bấm 🛡 Mở tường lửa để mở luôn.");
            return new("ok", "Tường lửa đã cho phép BcodeWatch.");
        }
        catch (Exception ex) { return new("warn", "Không kiểm tra được tường lửa: " + ex.Message); }
    }

    /// <summary>Hộp UAC → netsh: gỡ mọi luật cũ (kể cả luật CHẶN) của exe này, thêm luật cho phép exe + cổng TCP 47811–47820 + UDP 47830, mọi loại mạng.
    /// Trả null nếu xong, chuỗi lỗi nếu không.</summary>
    public static async Task<string?> OpenAsync(string exePath)
    {
        var lo = Wire.DefaultPort; var hi = Wire.MediaPort + 9;
        var cmds = new[]
        {
            $"netsh advfirewall firewall delete rule name=all program=\"{exePath}\"",
            $"netsh advfirewall firewall delete rule name=\"{RuleName}\"",
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exePath}\" enable=yes profile=any",
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={lo}-{hi} enable=yes profile=any",
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=UDP localport={Wire.DiscoveryPort} enable=yes profile=any",
        };
        var psi = new ProcessStartInfo("cmd.exe", "/c " + string.Join(" & ", cmds.Take(2)) + " & " + string.Join(" && ", cmds.Skip(2)))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return "Không chạy được netsh.";
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? null : $"netsh báo lỗi (mã {p.ExitCode}) — có thể máy theo chính sách công ty không cho sửa tường lửa; nhờ IT hoặc đổi máy làm chủ phòng.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Đã huỷ hộp xin quyền admin — tường lửa chưa đổi."; }
        catch (Exception ex) { return "Không mở được tường lửa: " + ex.Message; }
    }

    private static string Norm(string path)
    {
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)).TrimEnd('\\').ToLowerInvariant(); }
        catch { return path.ToLowerInvariant(); }
    }

    private static bool PortIn(string spec, int port)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part == "*") return true;
            var dash = part.IndexOf('-');
            if (dash > 0 && int.TryParse(part[..dash], out var a) && int.TryParse(part[(dash + 1)..], out var b)) { if (port >= a && port <= b) return true; }
            else if (int.TryParse(part, out var n) && n == port) return true;
        }
        return false;
    }
}
