using System.Collections.Concurrent;
using System.Text.Json;

namespace BcodeViewer.App.Host.FastSource;

/// <summary>
/// File "anh em" cho nguồn ghost "sib" (<see cref="SourceGhost"/>): controller Fast lặp cấu trúc rất nhiều giữa các màn hình cùng kiểu (SOTran ↔ SVTran ↔ POTran…),
/// nên dòng của vài file cùng loại (Dir / Grid / Filter…, cùng đuôi) có nhiều tên field trùng nhất với file đang sửa là nguồn gợi ý tốt — như "theo chính file"
/// nhưng mở ra ngoài file. Đo trên file mẫu Fast: ngắn +348 gợi ý đúng, dài +456 (chỉ 1/3 file mẫu có anh em đủ giống — source dự án thường nhiều hơn).
/// Chỉ mục mỗi dự án = (đường dẫn, loại, tập tên field) — rút lúc học source dự án, lưu <c>viewer\ghost\&lt;dự án&gt;.&lt;Format&gt;.sib.json</c>;
/// nội dung file anh em đọc từ đĩa khi cần và nhớ vài file gần nhất.
/// </summary>
public sealed partial class FastSourceService
{
    private sealed record SibEntry(string Path, string Kind, string Root, HashSet<string> Fields);

    private readonly ConcurrentDictionary<string, List<SibEntry>> _sibIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string[]> _sibLines = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime At, int Fields, IReadOnlyList<string[]> Sibs)> _sibPick = new(StringComparer.OrdinalIgnoreCase);
    private const int SiblingCount = 3;
    private const double SiblingMinJaccard = 0.2;

    private static string SibIndexFile(string project) => ViewerKnowledge.Viewer("ghost", ViewerKnowledge.SafeName(project) + "." + Format + ".sib.json");

    /// <summary>Dựng + lưu chỉ mục từ các file vừa đọc lúc học source dự án.</summary>
    private void BuildSiblingIndex(string project, IReadOnlyList<(string Path, string Text)> files)
    {
        var list = new List<SibEntry>();
        foreach (var (path, text) in files)
        {
            var kind = SourceGhost.KindOfPath(path);
            if (kind != "xml") continue;
            var fields = SourceGhost.FieldNamesOf(text);
            if (fields.Count > 0) list.Add(new SibEntry(path, kind, SourceGhost.RootOf(path, text), fields));
        }
        _sibIndex[project] = list;
        try
        {
            ViewerKnowledge.WriteAtomic(SibIndexFile(project),
                JsonSerializer.Serialize(list.Select(e => new { p = e.Path, k = e.Kind, r = e.Root, f = e.Fields }), ViewerKnowledge.Json));
        }
        catch { /* không ghi được: lần sau học lại */ }
    }

    /// <summary>Nạp chỉ mục đã lưu (kiến thức dự án đọc từ bản lưu, không đọc lại source).</summary>
    private void LoadSiblingIndex(string project)
    {
        try
        {
            var file = SibIndexFile(project);
            if (!File.Exists(file)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            _sibIndex[project] = doc.RootElement.EnumerateArray().Select(e => new SibEntry(
                e.GetProperty("p").GetString() ?? "", e.GetProperty("k").GetString() ?? "", e.GetProperty("r").GetString() ?? "",
                e.GetProperty("f").EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet())).ToList();
        }
        catch { /* bản lưu hỏng — không có anh em tới lần học sau */ }
    }

    /// <summary>Dòng (gộp khoảng trắng) của tối đa 3 file anh em của file đang sửa; null = chưa có chỉ mục / không file nào đủ giống.
    /// Chọn lại sau 30 giây hoặc khi số field của file đổi (đang khai thêm field).</summary>
    private IReadOnlyList<string[]>? SiblingsFor(string? path, string before, string after)
    {
        if (path is null || SourceGhost.KindOfPath(path) != "xml") return null;
        var (project, _) = SafeResolve(path);
        if (project is null || !_sibIndex.TryGetValue(project, out var index) || index.Count == 0) return null;
        var fields = SourceGhost.FieldNamesOf(before + "\n" + after);
        if (fields.Count == 0) return null;
        if (_sibPick.TryGetValue(path, out var hit) && (DateTime.UtcNow - hit.At).TotalSeconds < 30 && hit.Fields == fields.Count) return hit.Sibs;

        var root = SourceGhost.RootOf(path, before);
        var full = SafeFull(path);
        var picked = index.Where(e => e.Root == root && !string.Equals(SafeFull(e.Path), full, StringComparison.OrdinalIgnoreCase))
            .Select(e => (e, J: e.Fields.Count(fields.Contains) / (double)(e.Fields.Count + fields.Count - e.Fields.Count(fields.Contains))))
            .Where(x => x.J >= SiblingMinJaccard).OrderByDescending(x => x.J).Take(SiblingCount)
            .Select(x => LinesOf(x.e.Path)).OfType<string[]>().ToList();
        if (_sibPick.Count > 50) _sibPick.Clear();
        _sibPick[path] = (DateTime.UtcNow, fields.Count, picked);
        return picked.Count > 0 ? picked : null;
    }

    private string[]? LinesOf(string path)
    {
        if (_sibLines.TryGetValue(path, out var l)) return l;
        try
        {
            var lines = File.ReadAllText(path).Replace("\r\n", "\n").Split('\n').Select(SourceGhost.Norm).ToArray();
            if (_sibLines.Count > 30) _sibLines.Clear();
            _sibLines[path] = lines;
            return lines;
        }
        catch { return null; }
    }

    private static string SafeFull(string p) { try { return Path.GetFullPath(p); } catch { return p; } }
}
