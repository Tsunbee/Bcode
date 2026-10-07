using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>Kết quả tìm file source chuẩn cho 1 file trong dự án.</summary>
public sealed class GrantRow
{
    public required string ProjectFile { get; init; }    // file đang tick trên cây (vd ...\App_Data\Controllers\Dir\SVTran.f)
    public required string RelativeSource { get; init; } // đường dẫn tương đối của file source cần cấp (App_Data\Controllers\Dir\SVTran.xml)
    public string? SourcePath { get; set; }              // file tìm được trong kho (null = không có ở version nào)
    public string? FromVersion { get; set; }
    public DateTime? Modified { get; set; }
    public bool FromFallback { get; set; }                // không có ở version đã chọn — lấy từ version khác (bản sửa gần nhất)
    public bool DestExists { get; set; }
    public string? DestPath { get; set; }
}

/// <summary>
/// "Cấp source" tự động: với các file đang tick trong dự án, tìm file source (.xml) tương ứng trong kho SourceCollection theo PHIÊN BẢN.
///   • Tên thư mục phiên bản trong kho (FBOR2SP22.5.4.1, FBISP24, FBOR1...): các con số cuối tên là số phiên bản (22.5.4.1).
///   • Tự bắt phiên bản hiện tại theo thư mục source của dự án (vd FBISP24 → thư mục cùng số 24 trong kho); người dùng đổi được.
///   • File .f → cấp bản .xml cùng đường dẫn tương đối; file khác (aspx, txt, ent...) → cấp chính file đó.
///   • Không có ở phiên bản đã chọn → tìm ở các phiên bản khác, lấy bản có giờ sửa (modified) MỚI NHẤT.
/// Chỉ ĐỌC kho; ghi vào thư mục đích khi gọi <see cref="CopyRows"/>.
/// </summary>
public static class SourceGrantService
{
    private static readonly Regex VersionRegex = new(@"\d+(?:\.\d+)*", RegexOptions.Compiled);

    /// <summary>Các số của phiên bản trong tên thư mục: "FBOR2SP22.5.4.1" → [22,5,4,1]; "FBOR2SP17Ex" → [17]; không có số → rỗng.</summary>
    public static int[] ParseVersion(string name)
    {
        var m = VersionRegex.Matches(name);
        if (m.Count == 0) return Array.Empty<int>();
        return m[^1].Value.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    }

    public static int CompareVersion(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    /// <summary>Các thư mục phiên bản trong kho, mới nhất (số lớn) trước.</summary>
    public static List<string> ListVersions(string collectionRoot)
    {
        var names = Directory.GetDirectories(collectionRoot).Select(Path.GetFileName).OfType<string>().ToList();
        names.Sort((a, b) =>
        {
            var c = CompareVersion(ParseVersion(b), ParseVersion(a));
            return c != 0 ? c : string.Compare(b, a, StringComparison.OrdinalIgnoreCase);
        });
        return names;
    }

    /// <summary>Phiên bản trong kho khớp với thư mục source của dự án (vd "FBISP24"): trùng tên → dùng luôn; nếu không thì cùng số phiên bản và
    /// cùng dòng sản phẩm (3 chữ đầu FBO/FBI), rồi cùng số phiên bản bất kể dòng. null = không đoán được.</summary>
    /// <param name="versionCode">Mã phiên bản đã khai báo của dự án (ma_pbsp / tự nhập) — ưu tiên trước cách đoán theo tên thư mục.</param>
    public static string? DetectVersion(string projectRoot, IReadOnlyList<string> versions, out string note, string? versionCode = null)
    {
        note = "";
        var code = (versionCode ?? "").Trim();
        if (code.Length > 0)
        {
            var byName = versions.FirstOrDefault(v => v.Equals(code, StringComparison.OrdinalIgnoreCase))
                         ?? versions.FirstOrDefault(v => v.Contains(code, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) { note = $"theo mã phiên bản đã khai báo \"{code}\""; return byName; }
            var cv = ParseVersion(code);
            if (cv.Length > 0)
            {
                var byNumber = versions.FirstOrDefault(v => CompareVersion(ParseVersion(v), cv) == 0);
                if (byNumber is not null) { note = $"theo số phiên bản {string.Join('.', cv)} của mã đã khai báo \"{code}\""; return byNumber; }
            }
        }
        var projectName = Path.GetFileName(projectRoot.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(projectName)) return null;

        var exact = versions.FirstOrDefault(v => v.Equals(projectName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) { note = $"khớp tên thư mục dự án \"{projectName}\""; return exact; }

        var pv = ParseVersion(projectName);
        if (pv.Length > 0)
        {
            var sameNumber = versions.Where(v => CompareVersion(ParseVersion(v), pv) == 0).ToList();
            var family = projectName.Length >= 3 ? projectName[..3] : projectName;
            var pick = sameNumber.FirstOrDefault(v => v.StartsWith(family, StringComparison.OrdinalIgnoreCase)) ?? sameNumber.FirstOrDefault();
            if (pick is not null) { note = $"cùng số phiên bản {string.Join('.', pv)} với \"{projectName}\""; return pick; }
        }
        note = $"không tìm thấy phiên bản khớp \"{projectName}\" — chọn thủ công";
        return null;
    }

    /// <summary>Cắt đường dẫn file dự án tại App_Data / Main: trả về (gốc site, phần tương đối). null nếu file không nằm trong 2 thư mục đó.</summary>
    public static (string Root, string Relative)? SplitProjectPath(string file)
    {
        var parts = file.Split('\\', '/');
        var cut = Array.FindIndex(parts, p => p.Equals("App_Data", StringComparison.OrdinalIgnoreCase) || p.Equals("Main", StringComparison.OrdinalIgnoreCase));
        if (cut <= 0) return null;
        var root = string.Join("\\", parts.Take(cut));
        if (file.StartsWith(@"\\", StringComparison.Ordinal)) root = @"\\" + root.TrimStart('\\');
        return (root, string.Join("\\", parts.Skip(cut)));
    }

    /// <summary>Đường dẫn tương đối của file source cần cấp cho 1 file dự án: .f → .xml; còn lại giữ nguyên.</summary>
    public static string SourceRelative(string relative) =>
        relative.EndsWith(".f", StringComparison.OrdinalIgnoreCase) ? relative[..^2] + ".xml" : relative;

    /// <summary>Tìm file source cho từng file: phiên bản đã chọn trước; không có thì lấy bản sửa mới nhất ở phiên bản khác.</summary>
    public static List<GrantRow> Resolve(string collectionRoot, string chosenVersion, IReadOnlyList<string> allVersions, IEnumerable<string> projectFiles, string destRoot)
    {
        var rows = new List<GrantRow>();
        var others = allVersions.Where(v => !v.Equals(chosenVersion, StringComparison.OrdinalIgnoreCase)).ToList();
        var chosenVer = ParseVersion(chosenVersion);

        foreach (var file in projectFiles)
        {
            var split = SplitProjectPath(file);
            var rel = split is null ? Path.GetFileName(file) : split.Value.Relative;
            var srcRel = SourceRelative(rel);
            var row = new GrantRow { ProjectFile = file, RelativeSource = srcRel };

            var inChosen = Path.Combine(collectionRoot, chosenVersion, srcRel);
            if (SafeExists(inChosen))
            {
                row.SourcePath = inChosen; row.FromVersion = chosenVersion; row.Modified = SafeTime(inChosen);
            }
            else
            {
                // Không có ở phiên bản đã chọn: duyệt các phiên bản khác (song song), lấy bản có giờ sửa mới nhất;
                // cùng giờ thì ưu tiên phiên bản gần số phiên bản đã chọn.
                var hits = new System.Collections.Concurrent.ConcurrentBag<(string Version, string Path, DateTime Time)>();
                Parallel.ForEach(others, new ParallelOptions { MaxDegreeOfParallelism = 8 }, v =>
                {
                    var p = Path.Combine(collectionRoot, v, srcRel);
                    if (SafeExists(p) && SafeTime(p) is { } t) hits.Add((v, p, t));
                });
                // "Truy về" các phiên bản CŨ HƠN (hoặc bằng) trước — dự án đang ở phiên bản này không nên nhận bản của phiên bản mới hơn khi
                // còn bản cũ để dùng; chỉ khi không có bản cũ nào mới xét tới các phiên bản mới hơn.
                var older = hits.Where(h => CompareVersion(ParseVersion(h.Version), chosenVer) <= 0).ToList();
                var pool = older.Count > 0 ? older : hits.ToList();
                var best = pool
                    .OrderByDescending(h => h.Time)
                    .ThenBy(h => Math.Abs(Distance(ParseVersion(h.Version), chosenVer)))
                    .FirstOrDefault();
                if (best.Path is not null)
                {
                    row.SourcePath = best.Path; row.FromVersion = best.Version; row.Modified = best.Time; row.FromFallback = true;
                }
            }

            row.DestPath = Path.Combine(destRoot, srcRel);
            row.DestExists = SafeExists(row.DestPath);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>Khoảng cách thô giữa hai phiên bản (để chọn bản gần nhất khi giờ sửa trùng nhau).</summary>
    private static double Distance(int[] a, int[] b)
    {
        double d = 0, w = 1;
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++, w /= 100)
            d += ((i < a.Length ? a[i] : 0) - (i < b.Length ? b[i] : 0)) * w;
        return d;
    }

    private static bool SafeExists(string path) { try { return File.Exists(path); } catch { return false; } }
    private static DateTime? SafeTime(string path) { try { return File.GetLastWriteTime(path); } catch { return null; } }

    /// <summary>Copy các dòng đã chọn vào thư mục đích. File đích đã có chỉ bị ghi đè khi <paramref name="overwrite"/>.</summary>
    public static (int Copied, int Skipped, List<string> Errors) CopyRows(IEnumerable<GrantRow> rows, bool overwrite)
    {
        int copied = 0, skipped = 0;
        var errors = new List<string>();
        foreach (var r in rows)
        {
            if (r.SourcePath is null || r.DestPath is null) continue;
            try
            {
                if (File.Exists(r.DestPath))
                {
                    if (!overwrite) { skipped++; continue; }
                    File.SetAttributes(r.DestPath, FileAttributes.Normal); // file đích read-only thì ghi đè báo lỗi
                }
                Directory.CreateDirectory(Path.GetDirectoryName(r.DestPath)!);
                File.Copy(r.SourcePath, r.DestPath, overwrite: true);
                copied++;
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(r.DestPath)}: {ex.Message}"); }
        }
        return (copied, skipped, errors);
    }
}
