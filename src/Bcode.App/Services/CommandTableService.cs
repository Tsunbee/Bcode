using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Tra bảng dữ liệu (master "m…" / detail "d…") của 1 menu command theo Sysid, đọc từ source của dự án:
/// bảng master khai báo ở <c>App_Data\Controllers\Dir\{sysid}.f</c> (hoặc .xml) dạng <c>&lt;dir table="m21$000000" ...&gt;</c>;
/// bảng detail nằm ở file Grid mà Dir đó trỏ tới qua <c>&lt;items style="Grid" controller="ARDetail"&gt;</c> →
/// <c>Controllers\Grid\ARDetail.f</c> dạng <c>&lt;grid table="d21$000000" ...&gt;</c>.
/// </summary>
public static class CommandTableService
{
    private static readonly Regex DirTableRegex = new(@"<dir\b[^>]*?\btable\s*=\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex GridTableRegex = new(@"<grid\b[^>]*?\btable\s*=\s*""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ItemsTagRegex = new(@"<items\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StyleGridRegex = new(@"\bstyle\s*=\s*""Grid""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ControllerRegex = new(@"\bcontroller\s*=\s*""([A-Za-z0-9_]+)""", RegexOptions.Compiled);

    public sealed record Result(string Master, List<string> Details, string Note)
    {
        /// <summary>File Dir đã đọc (null = không thấy).</summary>
        public string? DirFile { get; init; }
        /// <summary>Từng Grid có khai table (cùng thứ tự <see cref="Details"/>): controller + file — để đọc field / tiêu đề (Tạo cấu trúc API từ menu).</summary>
        public List<(string Controller, string File, string Table)> Grids { get; init; } = new();
    }

    /// <summary>Bảng khai ở thẻ gốc &lt;dir table="…"&gt; / &lt;grid table="…"&gt; của nội dung controller ("" = không khai).</summary>
    public static string TableOf(string text) =>
        DirTableRegex.Match(text) is { Success: true } d ? d.Groups[1].Value
        : GridTableRegex.Match(text) is { Success: true } g ? g.Groups[1].Value : "";

    /// <summary>Chạy đồng bộ (đọc file qua UNC có thể chậm) — gọi từ Task.Run.</summary>
    public static Result Resolve(string? sourcePath, string? sysId)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(sysId))
            return new Result("", new(), "");
        return ResolveIn(Path.Combine(sourcePath, "App_Data", "Controllers"), sysId);
    }

    /// <summary>Như <see cref="Resolve"/> nhưng chỉ thẳng thư mục chứa Dir\ và Grid\ (vd MobileService\App_Data\Controllers\Mobile).</summary>
    public static Result ResolveIn(string controllers, string? sysId)
    {
        if (string.IsNullOrWhiteSpace(controllers) || string.IsNullOrWhiteSpace(sysId))
            return new Result("", new(), "");
        var dirFile = FindFile(Path.Combine(controllers, "Dir"), sysId.Trim());
        if (dirFile is null) return new Result("", new(), $"Không thấy Dir/{sysId.Trim()}.f hoặc .xml trong source.");

        var dirText = Read(dirFile);
        var master = dirText is null ? "" : DirTableRegex.Match(dirText) is { Success: true } m ? m.Groups[1].Value : "";

        var details = new List<string>();
        var grids = new List<(string Controller, string File, string Table)>();
        if (dirText is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match tag in ItemsTagRegex.Matches(dirText))
            {
                if (!StyleGridRegex.IsMatch(tag.Value)) continue;
                var c = ControllerRegex.Match(tag.Value);
                if (!c.Success || !seen.Add(c.Groups[1].Value)) continue;

                var gridFile = FindFile(Path.Combine(controllers, "Grid"), c.Groups[1].Value);
                var gridText = gridFile is null ? null : Read(gridFile);
                if (gridText is not null && GridTableRegex.Match(gridText) is { Success: true } g
                    && !details.Contains(g.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                {
                    details.Add(g.Groups[1].Value);
                    grids.Add((c.Groups[1].Value, gridFile!, g.Groups[1].Value));
                }
            }
        }
        return new Result(master, details, master.Length == 0 && details.Count == 0 ? "File Dir không khai báo table." : "") { DirFile = dirFile, Grids = grids };
    }

    private static string? FindFile(string folder, string name)
    {
        foreach (var ext in new[] { ".f", ".xml" })
        {
            var p = Path.Combine(folder, name + ext);
            try { if (File.Exists(p)) return p; } catch { /* share rớt */ }
        }
        return null;
    }

    private static string? Read(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
    }
}
