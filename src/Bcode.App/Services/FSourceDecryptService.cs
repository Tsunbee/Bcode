using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Bcode.App.Services;

/// <summary>
/// Giải mã file controller .f của Fast (khối <c>&lt;Encrypted&gt;</c>) để Sơ đồ phụ thuộc đọc được tên store / procedure bên trong. Gọi EXE RIÊNG
/// <c>FDecrypt\Bcode.FDecrypt.exe</c> (tiến trình tách — nó nạp fsystem.dll ĐÃ CÀI của FCode; chạy tách để lỗi/treo của fsystem không làm sập Bcode).
/// Khoá là license Fast ghi theo máy (<c>%windir%\&lt;Máy&gt;.c</c>) — máy không có license thì helper trả mã 2 và ở đây trả null (nơi gọi quay về dò
/// kho source chuẩn). Kết quả nhớ theo (đường dẫn + ngày sửa) để không chạy lại helper nhiều lần.
/// </summary>
public sealed class FSourceDecryptService
{
    private readonly Func<string?> _fcodeDir;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FSourceDecryptService(Func<string?> fcodeDir) => _fcodeDir = fcodeDir;

    private static string Exe => Path.Combine(AppContext.BaseDirectory, "FDecrypt", "Bcode.FDecrypt.exe");

    /// <summary>Có thể thử giải mã không (có helper + đã khai / dò được thư mục FCode). Không thì nơi gọi khỏi mất công.</summary>
    public bool Available => File.Exists(Exe) && _fcodeDir() is { Length: > 0 };

    /// <summary>Nội dung đã giải mã của 1 file .f; null = không giải được (không có helper / không license / lỗi / không đổi gì). Chạy đồng bộ — nơi gọi tự bọc Task.Run.</summary>
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

    /// <summary>Như <see cref="TryDecrypt"/> nhưng kèm lý do khi không giải được — cho mục chuột phải "Xem bản giải mã" (báo người dùng thiếu gì).
    /// Chỉ đọc file; kết quả chỉ để hiện, không ghi ra đâu cả. Chạy đồng bộ — nơi gọi tự bọc Task.Run.</summary>
    public (string? Text, string? Error) DecryptForView(string file)
    {
        if (!File.Exists(Exe)) return (null, "Thiếu chương trình giải mã FDecrypt\\Bcode.FDecrypt.exe cạnh Bcode.exe (build lại / chép thư mục FDecrypt).");
        if (_fcodeDir() is not { Length: > 0 }) return (null, "Chưa có thư mục FCode (chứa fsystem.dll) — khai ở Template → Đường dẫn → FCode.");
        var r = Run(file);
        if (r.Text is not null)
            try { _cache[file + "|" + File.GetLastWriteTimeUtc(file).Ticks] = r.Text; } catch { }
        return r;
    }

    private (string? Text, string? Error) Run(string file)
    {
        var dir = _fcodeDir();
        if (dir is null) return (null, "Chưa có thư mục FCode.");
        try
        {
            var psi = new ProcessStartInfo(Exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add(file);
            using var p = Process.Start(psi);
            if (p is null) return (null, "Không chạy được Bcode.FDecrypt.exe.");
            // Đọc stderr song song: đọc tuần tự stdout rồi stderr có thể kẹt khi helper ghi nhiều ra stderr.
            var errTask = p.StandardError.ReadToEndAsync();
            var outText = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } return (null, "Giải mã quá 20 giây — đã dừng."); }
            var err = errTask.Result.Trim();
            if (p.ExitCode == 2)   // máy không có license Fast
            {
                return (null, "Máy này chưa có license Fast (" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), Environment.MachineName + ".c")
                              + ") hoặc license không khớp máy — không giải mã được.");
            }
            if (p.ExitCode != 0) return (null, "Giải mã lỗi: " + (err.Length > 0 ? err : "mã thoát " + p.ExitCode));
            // Chỉ nhận khi thật sự giải được (không còn <Encrypted>); helper trả nguyên văn khi không có gì để giải.
            if (outText.IndexOf("<Encrypted>", StringComparison.OrdinalIgnoreCase) >= 0)
                return (null, "fsystem.dll không giải được khối <Encrypted> của file này (license không hợp lệ hoặc khoá khác).");
            return (outText, null);
        }
        catch (Exception ex) { return (null, "Giải mã lỗi: " + ex.Message); }
    }
}
