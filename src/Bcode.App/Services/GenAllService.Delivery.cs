using System.IO.Compression;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>Kết quả các bước thêm sau khi tạo gói (nén .zip / chép lên đường dẫn mạng). Lỗi ở bước nào thì ghi vào <see cref="Errors"/> —
/// gói ở Save At Path đã tạo xong vẫn giữ nguyên.</summary>
public sealed class PackageDelivery
{
    public string? ZipPath { get; set; }
    public string? NetworkPath { get; set; }
    public List<string> Errors { get; } = new();
}

public partial class GenAllService
{
    /// <summary>Thư mục gốc mặc định của tuỳ chọn "Gen vào đường dẫn mạng" — gói được chép vào &lt;gốc&gt;\&lt;tên máy&gt;\.</summary>
    public const string DefaultNetworkRoot = @"\\172.168.5.14\Send\";

    /// <summary>Chờ tối đa bấy nhiêu giây để biết thư mục mạng có tới được không (UNC không tới được có thể treo cả phút).</summary>
    private const int NetworkProbeSeconds = 15;

    /// <summary>Đọc 2 tuỳ chọn gói (nén zip / gen vào đường dẫn mạng) từ settings.json mới nhất — mỗi tab giữ bản AppSettings riêng nên không dùng bản cũ.</summary>
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
    /// Bước thêm sau <see cref="CreatePackage"/>: <paramref name="zip"/> = nén <paramref name="destRoot"/> thành &lt;destRoot&gt;.zip (cạnh thư mục gói,
    /// trong zip có sẵn thư mục gói); <paramref name="toNetwork"/> = chép lên &lt;networkRoot&gt;\&lt;tên máy&gt;\ (thư mục tên máy chưa có thì tạo) —
    /// có zip thì chép file .zip, không thì chép cả thư mục gói. Không ném lỗi: lỗi từng bước ghi vào <see cref="PackageDelivery.Errors"/>.
    /// </summary>
    public static async Task<PackageDelivery> DeliverPackageAsync(string destRoot, bool zip, bool toNetwork, string? networkRoot)
    {
        var result = new PackageDelivery();
        destRoot = destRoot.TrimEnd('\\', '/');
        var folderName = Path.GetFileName(destRoot);

        if (zip)
        {
            var zipPath = destRoot + ".zip";
            try
            {
                await Task.Run(() =>
                {
                    if (File.Exists(zipPath)) File.Delete(zipPath);
                    ZipFile.CreateFromDirectory(destRoot, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
                });
                result.ZipPath = zipPath;
            }
            catch (Exception ex) { result.Errors.Add($"Không nén được file zip {zipPath}: {ex.Message}"); }
        }

        if (toNetwork)
        {
            var root = string.IsNullOrWhiteSpace(networkRoot) ? DefaultNetworkRoot : networkRoot.Trim();
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
                    if (result.ZipPath is not null)
                    {
                        var target = Path.Combine(machineDir, Path.GetFileName(result.ZipPath));
                        File.Copy(result.ZipPath, target, overwrite: true);
                        return target;
                    }
                    var dir = Path.Combine(machineDir, folderName);
                    CopyDirectory(destRoot, dir);
                    return dir;
                });
            }
            catch (UnauthorizedAccessException ex) { result.Errors.Add($"Không có quyền ghi vào {root}: {ex.Message}"); }
            catch (Exception ex) { result.Errors.Add($"Không gen được vào đường dẫn mạng: {ex.Message}"); }
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
