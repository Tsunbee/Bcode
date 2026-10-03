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

    public sealed record Result(string Master, List<string> Details, string Note);

    /// <summary>Chạy đồng bộ (đọc file qua UNC có thể chậm) — gọi từ Task.Run.</summary>
    public static Result Resolve(string? sourcePath, string? sysId)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(sysId))
            return new Result("", new(), "");

        var controllers = Path.Combine(sourcePath, "App_Data", "Controllers");
        var dirFile = FindFile(Path.Combine(controllers, "Dir"), sysId.Trim());
        if (dirFile is null) return new Result("", new(), $"Không thấy Dir/{sysId.Trim()}.f hoặc .xml trong source.");

        var dirText = Read(dirFile);
        var master = dirText is null ? "" : DirTableRegex.Match(dirText) is { Success: true } m ? m.Groups[1].Value : "";

        var details = new List<string>();
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
                    details.Add(g.Groups[1].Value);
            }
        }
        return new Result(master, details, master.Length == 0 && details.Count == 0 ? "File Dir không khai báo table." : "");
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
