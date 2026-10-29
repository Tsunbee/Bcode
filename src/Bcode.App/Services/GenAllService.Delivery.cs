using System.IO.Compression;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>Kết quả bước chép gói lên đường dẫn mạng (có thể nén .zip). Lỗi thì ghi vào <see cref="Errors"/> — gói ở Save At Path đã tạo xong vẫn giữ nguyên.</summary>
public sealed class PackageDelivery
{
    /// <summary>File .zip hoặc thư mục gói đã chép lên mạng; null = không chép / chép lỗi.</summary>
    public string? NetworkPath { get; set; }
    public List<string> Errors { get; } = new();
}

public partial class GenAllService
{
    /// <summary>Thư mục gốc mặc định của tuỳ chọn "Gen vào đường dẫn mạng" — gói được chép vào &lt;gốc&gt;\&lt;tên máy&gt;\.</summary>
    public const string DefaultNetworkRoot = @"\\172.168.5.14\Send\";

    /// <summary>Chờ tối đa bấy nhiêu giây để biết thư mục mạng có tới được không (UNC không tới được có thể treo cả phút).</summary>
    private const int NetworkProbeSeconds = 15;

    /// <summary>Đọc 2 tuỳ chọn gói (gen vào đường dẫn mạng / nén zip) từ settings.json mới nhất — mỗi tab giữ bản AppSettings riêng nên không dùng bản cũ.</summary>
    public static (bool Zip, bool Network, string NetworkRoot) LoadPackageOptions()
    {
        var s = AppSettings.Load();
        return (s.PackageZip, s.PackageToNetwork, string.IsNullOrWhiteSpace(s.PackageNetworkRoot) ? DefaultNetworkRoot : s.PackageNetworkRoot);
    }

    /// <summary>Ghi 2 tuỳ chọn gói vào settings.json (AppSettings.Update — không đè thay đổi của tab khác). Lỗi ghi thì bỏ qua.</summary>
    public static void SavePackageOptions(bool zip, bool network, string? networkRoot)
    {
        var root = string.IsNullOrWhiteSpace(networkRoot) ? DefaultNetworkRoot : networkRoot.Trim();
        try
        {
            var cur = AppSettings.Load();
            if (cur.PackageZip == zip && cur.PackageToNetwork == network && cur.PackageNetworkRoot == root) return;
            AppSettings.Update(s => { s.PackageZip = zip; s.PackageToNetwork = network; s.PackageNetworkRoot = root; });
        }
        catch { /* không lưu được thì lần sau về giá trị cũ */ }
    }

    /// <summary>
    /// Bước thêm sau <see cref="CreatePackage"/>: chép gói ở <paramref name="destRoot"/> lên &lt;networkRoot&gt;\&lt;tên máy&gt;\ (thư mục tên máy chưa có thì tạo).
    /// <paramref name="zip"/> = chỉ đưa lên mạng file &lt;Folder Name&gt;.zip (nén ở %TEMP% rồi chép, trong zip có sẵn thư mục gói) — Save At Path vẫn chỉ có
    /// thư mục gói; không zip = chép cả thư mục gói. Không ném lỗi: lỗi ghi vào <see cref="PackageDelivery.Errors"/>.
    /// </summary>
    public static async Task<PackageDelivery> DeliverPackageAsync(string destRoot, bool zip, string? networkRoot)
    {
        var result = new PackageDelivery();
        destRoot = destRoot.TrimEnd('\\', '/');
        var folderName = Path.GetFileName(destRoot);
        var root = string.IsNullOrWhiteSpace(networkRoot) ? DefaultNetworkRoot : networkRoot.Trim();
        string? tempZip = null;
        try
        {
            var probe = Task.Run(() => Directory.Exists(root));
            if (await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(NetworkProbeSeconds))) != probe)
                throw new IOException($"Không kết nối được {root} sau {NetworkProbeSeconds} giây (kiểm tra mạng / VPN).");
            if (!await probe)
                throw new IOException($"Không truy cập được {root} (không có mạng / VPN, sai đường dẫn hoặc tài khoản Windows không có quyền).");

            var machineDir = Path.Combine(root, Environment.MachineName);
            result.NetworkPath = await Task.Run(() =>
            {
                Directory.CreateDirectory(machineDir);   // đã có thì dùng luôn
                if (zip)
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "Bcode", "PackageZip");
                    Directory.CreateDirectory(tempDir);
                    tempZip = Path.Combine(tempDir, folderName + ".zip");
                    if (File.Exists(tempZip)) File.Delete(tempZip);
                    ZipFile.CreateFromDirectory(destRoot, tempZip, CompressionLevel.Optimal, includeBaseDirectory: true);
                    var target = Path.Combine(machineDir, folderName + ".zip");
                    File.Copy(tempZip, target, overwrite: true);
                    return target;
                }
                var dir = Path.Combine(machineDir, folderName);
                CopyDirectory(destRoot, dir);
                return dir;
            });
        }
        catch (UnauthorizedAccessException ex) { result.Errors.Add($"Không có quyền ghi vào {root}: {ex.Message}"); }
        catch (Exception ex) { result.Errors.Add($"Không gen được vào đường dẫn mạng: {ex.Message}"); }
        finally
        {
            if (tempZip is not null) try { File.Delete(tempZip); } catch { /* file tạm, để lại cũng không sao */ }
        }
        return result;
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
