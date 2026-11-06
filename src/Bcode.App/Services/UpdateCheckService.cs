using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Bcode.App.Services;

/// <summary>Dấu 1 bản build: thời điểm build (UTC) + commit git — từ bcode-version.json do target WriteBuildStamp (Bcode.App.csproj) ghi cạnh Bcode.exe.</summary>
public sealed record BuildStamp(DateTime BuiltUtc, string Commit, string Config)
{
    public string Label => BuiltUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) + (Commit is { Length: > 0 } c && c != "unknown" ? " · " + c : "");
}

/// <summary>
/// "Kiểm tra bản Bcode mới": so dấu phiên bản của bản đang chạy với bcode-version.json trong thư mục phát hành (Settings → Thư mục phát hành bản mới).
/// Chỉ ĐỌC 1 file nhỏ trên thư mục mạng (có hạn giờ — UNC treo không làm treo Bcode); không vào được thì bỏ qua, không chặn gì.
/// Cập nhật chỉ chạy khi người dùng đồng ý: 1 script PowerShell tách riêng chờ mọi tiến trình Bcode chạy từ thư mục này thoát, robocopy bản mới vào
/// (/XO: file ở máy mới hơn bản phát hành — vd mẫu tự sửa — thì giữ), rồi mở lại Bcode. Dữ liệu người dùng nằm ở %AppData%\Bcode nên không bị đụng.
/// </summary>
public static class UpdateCheckService
{
    public const string StampFileName = "bcode-version.json";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>Dấu của bản đang chạy; thiếu file (build cũ) thì lấy giờ sửa Bcode.exe.</summary>
    public static BuildStamp Local => Read(Path.Combine(ExeDir, StampFileName))
        ?? new BuildStamp(File.GetLastWriteTimeUtc(Environment.ProcessPath ?? Path.Combine(ExeDir, "Bcode.exe")), "", "");

    public static BuildStamp? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            var built = r.TryGetProperty("built", out var b) && DateTime.TryParse(b.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : (DateTime?)null;
            if (built is null) return null;
            return new BuildStamp(built.Value, r.TryGetProperty("commit", out var c) ? c.GetString() ?? "" : "", r.TryGetProperty("config", out var g) ? g.GetString() ?? "" : "");
        }
        catch { return null; }
    }

    /// <summary>Đọc dấu phiên bản trong thư mục phát hành. Error != null khi không đọc được (mất mạng, thư mục sai, thiếu file).</summary>
    public static async Task<(BuildStamp? Remote, string? Error)> CheckAsync(string? releaseFolder)
    {
        var folder = (releaseFolder ?? "").Trim();
        if (folder.Length == 0) return (null, "Chưa đặt thư mục phát hành (Settings → Thư mục phát hành bản mới).");
        var path = Path.Combine(folder, StampFileName);
        var work = Task.Run(() => Directory.Exists(folder) ? (Read(path), File.Exists(path)) : ((BuildStamp?)null, false));
        if (await Task.WhenAny(work, Task.Delay(Timeout)) != work) return (null, $"Không vào được {folder} (quá {Timeout.TotalSeconds:0} giây).");
        try
        {
            var (stamp, exists) = await work;
            if (stamp is not null) return (stamp, null);
            return (null, exists ? $"{path} không đúng định dạng." : $"Không thấy {StampFileName} trong {folder} — bản phát hành phải là thư mục build có file này.");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>Bản phát hành mới hơn bản đang chạy (lệch dưới 1 phút coi như cùng bản).</summary>
    public static bool IsNewer(BuildStamp remote, BuildStamp local) => remote.BuiltUtc > local.BuiltUtc.AddMinutes(1);

    /// <summary>Thư mục phát hành chính là thư mục đang chạy (hoặc nằm trong nhau) thì không cập nhật được.</summary>
    public static bool SameFolder(string releaseFolder)
    {
        static string N(string p) => Path.GetFullPath(p).TrimEnd('\\', '/') + "\\";
        try
        {
            var a = N(releaseFolder); var b = N(ExeDir);
            return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) || b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    public static string LogPath => Path.Combine(BcodePaths.AppData, "Bcode", "update.log");

    /// <summary>Chạy script cập nhật (tiến trình riêng, ẩn) — gọi xong thì đóng Bcode. Script chờ mọi Bcode / BcodeViewer / BcodeNote / ScreenDesigner
    /// chạy từ thư mục này thoát (tối đa 15 phút), chép bản mới, ghi log, mở lại Bcode.exe.</summary>
    public static void StartUpdater(string releaseFolder)
    {
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var src = Path.GetFullPath(releaseFolder.Trim()).TrimEnd('\\');
        var dst = Path.GetFullPath(ExeDir).TrimEnd('\\');
        var exe = Environment.ProcessPath ?? Path.Combine(dst, "Bcode.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        var script = new StringBuilder()
            .AppendLine("$ErrorActionPreference = 'Continue'")
            .AppendLine($"$src = {Q(src)}; $dst = {Q(dst)}; $exe = {Q(exe)}; $log = {Q(LogPath)}")
            .AppendLine("function L($m) { Add-Content -Path $log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + '  ' + $m) -Encoding UTF8 }")
            .AppendLine("L ('Bắt đầu cập nhật từ ' + $src)")
            .AppendLine("$deadline = (Get-Date).AddMinutes(15)")
            .AppendLine("do {")
            .AppendLine("  $busy = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dst + '\\', [StringComparison]::OrdinalIgnoreCase) })")
            .AppendLine("  if ($busy.Count -gt 0) { Start-Sleep -Seconds 1 }")
            .AppendLine("} while ($busy.Count -gt 0 -and (Get-Date) -lt $deadline)")
            .AppendLine("if ($busy.Count -gt 0) { L ('Hết giờ chờ — còn mở: ' + (($busy | ForEach-Object { $_.ProcessName }) -join ', ') + '. Không cập nhật.'); exit 1 }")
            .AppendLine("robocopy $src $dst /E /XO /R:3 /W:2 /NP /NFL /NDL /NJH | Out-Null")
            .AppendLine("$rc = $LASTEXITCODE")
            // /XO giữ file ở máy mới hơn — riêng dấu phiên bản luôn chép đè, không thì lần sau vẫn báo "có bản mới".
            .AppendLine($"if ($rc -lt 8) {{ Copy-Item -LiteralPath (Join-Path $src '{StampFileName}') -Destination $dst -Force -ErrorAction SilentlyContinue }}")
            .AppendLine("if ($rc -ge 8) { L ('robocopy lỗi mã ' + $rc + ' — bản cũ vẫn giữ (có thể chép dở, chạy lại cập nhật).') } else { L ('Chép xong (robocopy ' + $rc + ').') }")
            .AppendLine("Start-Process -FilePath $exe")
            .ToString();
        var ps1 = Path.Combine(Path.GetTempPath(), "bcode-update-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".ps1");
        File.WriteAllText(ps1, script, new UTF8Encoding(true));   // BOM: PowerShell 5.1 đọc đúng tiếng Việt
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{ps1}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
        });
    }
}
