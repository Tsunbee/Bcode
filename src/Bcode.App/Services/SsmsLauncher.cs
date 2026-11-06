using System.Diagnostics;
using System.Text;
using Bcode.App.Models;
using Microsoft.Win32;

namespace Bcode.App.Services;

/// <summary>Actions → "SQL SMS" / tool "Mở SSMS": mở SQL Server Management Studio đăng nhập sẵn vào server + database đang chọn của workspace
/// (thay tool LoginSSMS cũ). SSMS 18+ bỏ tham số -P, nên: dòng lệnh -S/-d/-U (hoặc -E) để SSMS điền sẵn hộp Connect, rồi
/// Assets/ssms/ssmslogin.ps1 (Windows PowerShell 5.1 có sẵn UI Automation — Bcode .NET 8 không bật UseWPF) điền mật khẩu, tick
/// Trust Server Certificate, bấm Connect. Mật khẩu chuyển qua stdin, không nằm trên dòng lệnh.</summary>
internal static class SsmsLauncher
{
    /// <summary>Đường dẫn SSMS.exe: <paramref name="configured"/> (AppSettings.SqlSmsPath) nếu là file có thật, không thì App Paths
    /// trong registry, rồi bản mới nhất trong Program Files (SSMS 21+ ở ...\Release\Common7\IDE, bản cũ ở ...\Common7\IDE). Null = không thấy.</summary>
    public static string? FindExe(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\ssms.exe");
                if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
            }
            catch { /* không đọc được registry → dò thư mục */ }
        }
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        var found = new List<(int Ver, string Path)>();
        foreach (var root in roots.Where(r => r.Length > 0 && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dir in SafeDirs(root, "Microsoft SQL Server Management Studio*"))
            {
                var ver = int.TryParse(new string(Path.GetFileName(dir).Where(char.IsDigit).ToArray()), out var v) ? v : 0;
                foreach (var rel in new[] { @"Release\Common7\IDE\SSMS.exe", @"Common7\IDE\Ssms.exe" })
                    if (File.Exists(Path.Combine(dir, rel))) found.Add((ver, Path.Combine(dir, rel)));
            }
            // SSMS 17/18 cài trong thư mục "Microsoft SQL Server\<số>\Tools\Binn\ManagementStudio".
            foreach (var dir in SafeDirs(Path.Combine(root, "Microsoft SQL Server"), "*"))
            {
                var p = Path.Combine(dir, @"Tools\Binn\ManagementStudio\Ssms.exe");
                if (File.Exists(p)) found.Add((int.TryParse(Path.GetFileName(dir), out var v) ? v / 10 : 0, p));
            }
        }
        return found.OrderByDescending(f => f.Ver).Select(f => f.Path).FirstOrDefault();
    }

    private static IEnumerable<string> SafeDirs(string root, string pattern)
    {
        try { return Directory.Exists(root) ? Directory.GetDirectories(root, pattern) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Mở SSMS vào <paramref name="database"/> của workspace và tự điền hộp Connect. Trả câu báo cho thanh trạng thái
    /// (chờ SSMS khởi động + điền xong — có thể vài chục giây, gọi không chặn giao diện).</summary>
    public static async Task<string> OpenAsync(string exe, Workspace ws, string database)
    {
        var sql = !ws.IntegratedSecurity;
        var major = MajorVersion(exe);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in CommandLine(major, ws.Server, database, sql ? ws.User : null)) psi.ArgumentList.Add(a);
        using var ssms = Process.Start(psi) ?? throw new InvalidOperationException("Không chạy được " + exe);
        // SSMS 21+ + Windows Authentication: -S -d -C đủ để vào thẳng Object Explorer (đã thử SSMS 22), không có hộp Connect để điền.
        if (!sql && major >= 21) return $"Đã mở SSMS: {ws.Server} / {database} (Windows Authentication).";

        var fill = new ProcessStartInfo(PowerShellPath())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath(), "-ProcId", ssms.Id.ToString(), "-Server", ws.Server })
            fill.ArgumentList.Add(a);
        // Chuỗi rỗng sau -File bị PowerShell coi là THIẾU giá trị (lỗi "Missing an argument") → chỉ truyền khi có.
        if (sql && !string.IsNullOrEmpty(ws.User)) { fill.ArgumentList.Add("-User"); fill.ArgumentList.Add(ws.User); }
        if (!string.IsNullOrEmpty(database)) { fill.ArgumentList.Add("-Database"); fill.ArgumentList.Add(database); }
        if (sql) fill.ArgumentList.Add("-Sql");
        using var p = Process.Start(fill) ?? throw new InvalidOperationException("Không chạy được Windows PowerShell.");
        if (sql) await p.StandardInput.WriteLineAsync(ws.Password ?? "");
        p.StandardInput.Close();
        var outText = p.StandardOutput.ReadToEndAsync();
        var errText = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } return "SSMS đã mở nhưng chờ hộp Connect quá lâu — tự đăng nhập trong SSMS."; }

        var line = ((await outText).Trim().Split('\n').LastOrDefault() ?? "").Trim();
        var target = $"{ws.Server} / {database}";
        if (line == "OK") return $"SSMS: đã điền hộp Connect và bấm Connect ({target}).";
        if (line == "NODIALOG") return $"SSMS đã mở ({target}) nhưng không thấy hộp Connect — có thể SSMS đã tự kết nối (mật khẩu đã nhớ) hoặc đang đặt mở môi trường trống.";
        var err = line.StartsWith("ERR:") ? line[4..].Trim() : ((await errText).Trim() is { Length: > 0 } e ? e : line);
        return $"SSMS đã mở ({target}) nhưng không tự điền được: {err}";
    }

    /// <summary>Tham số dòng lệnh theo đời SSMS (đọc từ hướng dẫn "Invalid Command Line" của SSMS 22): từ SSMS 21 có -C (trust server
    /// certificate — server nội bộ dùng chứng chỉ tự ký, mặc định Encrypt = Mandatory) và BỎ -E (không -U = Windows Authentication);
    /// SSMS 20 trở về trước dùng -E cho Windows Authentication, chưa có -C (script vẫn tick ô Trust Server Certificate).</summary>
    internal static List<string> CommandLine(int major, string server, string? database, string? sqlUser)
    {
        // SSMS 21+ kèm -U (không có mật khẩu) thì TỰ KẾT NỐI luôn bằng mật khẩu rỗng (đã thử SSMS 22): 1 lần đăng nhập sai trong log
        // server + hộp Error chặn nút Connect. Nên chỉ mở SSMS, để ssmslogin.ps1 điền đủ hộp Connect hiện ra lúc khởi động.
        if (sqlUser is not null && major >= 21) return new List<string> { "-nosplash" };
        var args = new List<string> { "-S", server };
        if (!string.IsNullOrWhiteSpace(database)) { args.Add("-d"); args.Add(database); }
        if (sqlUser is not null) { args.Add("-U"); args.Add(sqlUser); }
        else if (major is > 0 and < 21) args.Add("-E");
        if (major >= 21) args.Add("-C");
        args.Add("-nosplash");
        return args;
    }

    private static int MajorVersion(string exe)
    {
        try { return FileVersionInfo.GetVersionInfo(exe).ProductMajorPart; } catch { return 0; }
    }

    /// <summary>Chép script nhúng ra thư mục tạm (tên theo mã băm nội dung), UTF-8 CÓ BOM cho PowerShell 5.1 — như RptSchemaService.</summary>
    private static string ScriptPath()
    {
        using var s = typeof(SsmsLauncher).Assembly.GetManifestResourceStream("ssms.ssmslogin.ps1")
                      ?? throw new InvalidOperationException("Thiếu script ssms.ssmslogin.ps1 trong Bcode.dll.");
        using var sr = new StreamReader(s, Encoding.UTF8);
        var text = sr.ReadToEnd();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12];
        var dir = Path.Combine(Path.GetTempPath(), "Bcode", "ssms");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ssmslogin_" + hash + ".ps1");
        if (!File.Exists(path)) File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private static string PowerShellPath()
    {
        var sys = Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), sys, "WindowsPowerShell", "v1.0", "powershell.exe");
    }
}
