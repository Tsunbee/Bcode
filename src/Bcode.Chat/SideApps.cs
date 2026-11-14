using System.Diagnostics;
using Bcode.Chat.Net;

namespace Bcode.Chat;

/// <summary>Mở Xem chung (BcodeWatch.exe) / Giải lao (BcodeGame.exe) của Bcode từ lời mời trong Bhoo.
/// Bản cài: Bhoo\Bhoo.exe, Watch\BcodeWatch.exe, Game\BcodeGame.exe nằm cạnh nhau dưới thư mục Bcode;
/// lúc build thử: src\Bcode.Chat\bin\… cạnh src\Bcode.Watch\bin\… — tìm cả hai kiểu.</summary>
internal static class SideApps
{
    public static string Title(string app) => app == "watch" ? "Xem chung" : "Giải lao";
    public static int DefaultPort(string app) => app == "watch" ? 47831 : 47811;
    private static string Exe(string app) => app == "watch" ? "BcodeWatch" : "BcodeGame";

    public static bool Running(string app)
    {
        var ps = Process.GetProcessesByName(Exe(app));
        try { return ps.Length > 0; } finally { foreach (var p in ps) p.Dispose(); }
    }

    public static string? Find(string app)
    {
        var exe = Exe(app) + ".exe";
        var folder = app == "watch" ? "Watch" : "Game";
        var project = app == "watch" ? "Bcode.Watch" : "Bcode.Game";
        var bas = AppContext.BaseDirectory;
        var cands = new List<string>
        {
            Path.GetFullPath(Path.Combine(bas, "..", folder, exe)),   // Bcode\Bhoo\ → Bcode\Watch\
            Path.Combine(bas, folder, exe),
            Path.Combine(bas, exe),
        };
        // build thử: …\src\Bcode.Chat\bin\<cấu hình>\net8.0-windows\ → …\src\Bcode.Watch\bin\<cấu hình>\net8.0-windows\
        var d = new DirectoryInfo(bas);
        var tfm = d.Name; var cfg = d.Parent?.Name; var src = d.Parent?.Parent?.Parent?.Parent;
        if (src is not null && cfg is not null) cands.Add(Path.Combine(src.FullName, project, "bin", cfg, tfm, exe));
        return cands.FirstOrDefault(File.Exists);
    }

    /// <summary>Mở ứng dụng với tham số (--host --room … / --join ip:cổng). Trả lỗi (chuỗi) hoặc null.</summary>
    public static string? Launch(string app, params string[] args)
    {
        var exe = Find(app);
        if (exe is null) return $"Không thấy {Exe(app)}.exe cạnh Bhoo — mở {Title(app)} từ Bcode (Giải trí) một lần hoặc build lại Bcode.";
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return null;
        }
        catch (Exception ex) { return $"Không mở được {Title(app)}: {ex.Message}"; }
    }
}
