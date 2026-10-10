using System.Text.RegularExpressions;

namespace Bcode.App.Services.Rpt.Builder;

/// <summary>Một báo cáo mẫu của Fast giống với báo cáo đang thiết kế (theo các cột của Grid).</summary>
public sealed record SampleHit(string Controller, int Total, List<string> Matched, List<string> Missing, bool HasFilter, bool HasReport, double Score);

/// <summary>
/// Tìm trong source mẫu của Fast (<c>Templates\fileSource\Source\Grid</c>) các báo cáo có cột giống báo cáo đang thiết kế nhất — để người dùng xem Fast làm báo cáo tương tự ra sao
/// và biết còn thiếu những cột nào. Chỉ ĐỌC: lập chỉ mục tên cột của mọi file Grid một lần (lười), sau đó so khớp theo hệ số Dice.
/// </summary>
internal static class SampleFinder
{
    private static readonly Regex FieldRx = new(@"<field\s+name\s*=\s*""(?<n>[^""]+)""(?<rest>[^>]*)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "sysorder", "sysprint", "systotal", "stt_rec", "ma_ct", "stt", "xrow", "xcolumn", "xheader" };
    private static Dictionary<string, HashSet<string>>? _index;
    private static string _indexDir = "";
    private static readonly object Gate = new();

    /// <summary>Thư mục Source\ cạnh BuildReport (rỗng nếu không có).</summary>
    public static string SourceDir
    {
        get
        {
            var t = Bcode.ReportBuilder.ReportBuilderEnv.TemplateDir;
            if (string.IsNullOrWhiteSpace(t)) return "";
            var d = Path.GetFullPath(Path.Combine(t, "..", "Source"));
            return Directory.Exists(Path.Combine(d, "Grid")) ? d : "";
        }
    }

    private static Dictionary<string, HashSet<string>> Index(string dir)
    {
        lock (Gate)
        {
            if (_index is not null && _indexDir == dir) return _index;
            var idx = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in Directory.EnumerateFiles(Path.Combine(dir, "Grid"), "*.xml"))
            {
                try
                {
                    var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Match m in FieldRx.Matches(File.ReadAllText(f)))
                    {
                        if (Regex.IsMatch(m.Groups["rest"].Value, @"width\s*=\s*""0""")) continue;          // cột ẩn
                        var n = Regex.Replace(m.Groups["n"].Value, "%[a-z]$", "", RegexOptions.IgnoreCase);
                        if (!Skip.Contains(n)) set.Add(n);
                    }
                    if (set.Count > 0) idx[Path.GetFileNameWithoutExtension(f)] = set;
                }
                catch { /* file hỏng: bỏ qua */ }
            }
            _indexDir = dir; _index = idx; return idx;
        }
    }

    public static List<SampleHit> Find(IEnumerable<string> columns, int top = 8)
    {
        var dir = SourceDir; if (dir.Length == 0) return new();
        var mine = new HashSet<string>(columns.Select(c => Regex.Replace(c ?? "", "%[a-z]$", "", RegexOptions.IgnoreCase)).Where(c => c.Length > 0 && !Skip.Contains(c)), StringComparer.OrdinalIgnoreCase);
        if (mine.Count == 0) return new();
        var hits = new List<SampleHit>();
        foreach (var (ctl, set) in Index(dir))
        {
            var inter = mine.Count(set.Contains);
            if (inter < Math.Min(2, mine.Count)) continue;
            var score = 2.0 * inter / (mine.Count + set.Count);
            hits.Add(new SampleHit(ctl, set.Count, mine.Where(set.Contains).ToList(), set.Where(c => !mine.Contains(c)).Take(14).ToList(),
                File.Exists(Path.Combine(dir, "Filter", ctl + ".xml")), File.Exists(Path.Combine(dir, "Report", ctl + ".xml")), Math.Round(score, 3)));
        }
        return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Controller).Take(top).ToList();
    }
}
