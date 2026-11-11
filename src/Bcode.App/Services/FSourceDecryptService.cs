using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Bcode.App.Services;

/// <summary>
/// Xử lý nội dung file controller .f của Fast để Sơ đồ phụ thuộc / File Lookup đọc được mã nguồn (tên store / procedure…). Gọi EXE RIÊNG
/// <c>FDecrypt\Bcode.FDecrypt.exe</c> (tiến trình tách — dùng thành phần riêng <c>Bcode.FSystem.dll</c>, tự chứa; chạy tách để lỗi/treo không làm sập Bcode).
/// <para>Khoá (hằng số ở <see cref="LicenseSecret.FHashKey"/> — Secrets/LocalSecrets.cs, gitignore) truyền cho helper qua stdin. Chỉ bật khi máy đã có key
/// (<see cref="LicenseService.IsDecryptFUnlocked"/>) và bản build có khoá. Kết quả nhớ theo (đường dẫn + ngày sửa) để không chạy lại helper nhiều lần.</para>
/// </summary>
public sealed class FSourceDecryptService
{
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static string Exe => Path.Combine(AppContext.BaseDirectory, "FDecrypt", "Bcode.FDecrypt.exe");
    private static string HashKey => LicenseSecret.FHashKey ?? "";

    /// <summary>Có thể dùng không: có helper + bản build có khoá + máy đã có key. Không thì nơi gọi khỏi mất công.</summary>
    public bool Available => File.Exists(Exe) && HashKey.Length > 0 && LicenseService.IsDecryptFUnlocked;

    /// <summary>Nội dung XML của 1 file .f; null = không xử lý được (không có helper / chưa có key / lỗi / không đổi gì). Chạy đồng bộ — nơi gọi tự bọc Task.Run.</summary>
    public string? TryDecrypt(string file)
    {
        if (!Available) return null;
        string key;
        try { key = file + "|" + File.GetLastWriteTimeUtc(file).Ticks; } catch { return null; }
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var result = Run(file).Text;
        _cache[key] = result;
        return result;
    }

    /// <summary>Như <see cref="TryDecrypt"/> nhưng kèm lý do khi không mở được — cho mục chuột phải "Xem XML" (báo người dùng thiếu gì).
    /// Chỉ đọc file; kết quả chỉ để hiện, không ghi ra đâu cả. Chạy đồng bộ — nơi gọi tự bọc Task.Run.</summary>
    public (string? Text, string? Error) DecryptForView(string file)
    {
        if (!File.Exists(Exe)) return (null, "Thiếu thành phần FDecrypt\\Bcode.FDecrypt.exe cạnh Bcode.exe (build lại / chép thư mục FDecrypt).");
        if (HashKey.Length == 0) return (null, "Bản Bcode này build thiếu khoá (Secrets\\LocalSecrets.cs → FHashKey) — không xử lý được file.");
        if (!LicenseService.IsDecryptFUnlocked) return (null, "Máy chưa có key phù hợp.");
        var r = Run(file);
        if (r.Text is not null)
            try { _cache[file + "|" + File.GetLastWriteTimeUtc(file).Ticks] = r.Text; } catch { }
        return r;
    }

    private (string? Text, string? Error) Run(string file)
    {
        try
        {
            var psi = new ProcessStartInfo(Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(file);
            using var p = Process.Start(psi);
            if (p is null) return (null, "Không chạy được Bcode.FDecrypt.exe.");
            // khoá đưa qua stdin (không để lộ ở dòng lệnh). Đọc stderr song song: đọc tuần tự stdout rồi stderr có thể kẹt.
            try { p.StandardInput.Write(HashKey); } catch { } finally { try { p.StandardInput.Close(); } catch { } }
            var errTask = p.StandardError.ReadToEndAsync();
            var outText = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } return (null, "Quá 20 giây — đã dừng."); }
            var err = errTask.Result.Trim();
            if (p.ExitCode == 5)   // helper không nhận được khoá
                return (null, "Không truyền được khoá cho thành phần xử lý.");
            if (p.ExitCode == 6)   // bản build không có thành phần thật (chỉ FEngine.Empty.cs)
                return (null, "Bản build thiếu thành phần xử lý (Bcode.FSystem/FEngine.cs) — chép file đó vào rồi build lại.");
            if (p.ExitCode != 0) return (null, "Lỗi: " + (err.Length > 0 ? err : "mã thoát " + p.ExitCode));
            // Chỉ nhận khi thật sự xử lý được; helper trả nguyên văn khi không có gì để làm.
            if (outText.IndexOf("<Encrypted>", StringComparison.OrdinalIgnoreCase) >= 0)
                return (null, "Không xử lý được nội dung file này (khoá không hợp lệ hoặc khác khoá).");
            return (outText, null);
        }
        catch (Exception ex) { return (null, "Lỗi: " + ex.Message); }
    }
}
