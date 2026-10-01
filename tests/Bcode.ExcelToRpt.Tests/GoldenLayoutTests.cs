using System.Text.Json.Nodes;
using Bcode.App.Services.ExcelToRpt;
using Xunit;

namespace Bcode.ExcelToRpt.Tests;

/// <summary>
/// layout.json của bản C# phải giống bản Python (excel2rpt_parser.py) — đó là hợp đồng với
/// RptGenerator.exe. So theo CẤU TRÚC (thứ tự khoá không quan trọng), số phải khớp tuyệt đối.
///
/// Thêm mẫu mới: chép file .xlsx vào Golden\ rồi chạy
///   py excel2rpt_parser.py Golden\mau.xlsx Golden\mau.layout.json
/// </summary>
public class GoldenLayoutTests
{
    private static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "Golden");

    public static IEnumerable<object[]> GoldenFiles() =>
        Directory.GetFiles(GoldenDir, "*.xlsx")
            .Where(x => File.Exists(Path.ChangeExtension(x, ".layout.json")))
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => new object[] { Path.GetFileName(x) });

    [Theory, MemberData(nameof(GoldenFiles))]
    public void Layout_matches_python(string xlsx)
    {
        var path = Path.Combine(GoldenDir, xlsx);
        var actual = JsonNode.Parse(ExcelLayoutParser.ConvertToJson(path));
        var expected = JsonNode.Parse(File.ReadAllText(Path.ChangeExtension(path, ".layout.json")));
        var diff = JsonDiff.First(expected, actual, "$", tolerance: 0);
        if (diff is not null)
        {
            // Để lại bản C# cạnh bản Python cho dễ so khi test đỏ
            var dump = Path.Combine(AppContext.BaseDirectory, "GoldenActual");
            Directory.CreateDirectory(dump);
            File.WriteAllText(Path.Combine(dump, Path.ChangeExtension(xlsx, ".layout.json")), actual!.ToJsonString(LayoutJson.Options));
        }
        Assert.True(diff is null, diff);
    }
}

internal static class JsonDiff
{
    /// <summary>Đường dẫn JSON đầu tiên khác nhau (vd $.sections.Detail.rows[0].items[3].border.bottom), hoặc null.</summary>
    public static string? First(JsonNode? e, JsonNode? a, string path, double tolerance)
    {
        if (e is null || a is null)
            return e is null && a is null ? null : $"{path}: mong doi {e?.ToJsonString() ?? "null"}, nhan {a?.ToJsonString() ?? "null"}";
        switch (e)
        {
            case JsonObject eo:
                if (a is not JsonObject ao) return $"{path}: mong doi object, nhan {a.ToJsonString()}";
                foreach (var (k, v) in eo)
                {
                    if (!ao.ContainsKey(k)) return $"{path}.{k}: thieu (mong doi {v?.ToJsonString() ?? "null"})";
                    if (First(v, ao[k], $"{path}.{k}", tolerance) is { } d) return d;
                }
                foreach (var (k, v) in ao)
                    if (!eo.ContainsKey(k)) return $"{path}.{k}: thua ({v?.ToJsonString() ?? "null"})";
                return null;
            case JsonArray ea:
                if (a is not JsonArray aa) return $"{path}: mong doi mang, nhan {a.ToJsonString()}";
                for (var i = 0; i < Math.Min(ea.Count, aa.Count); i++)
                    if (First(ea[i], aa[i], $"{path}[{i}]", tolerance) is { } d) return d;
                return ea.Count == aa.Count ? null : $"{path}: mong doi {ea.Count} phan tu, nhan {aa.Count}";
            default:
                var ev = e.AsValue();
                var av = a.AsValue();
                if (ev.TryGetValue<double>(out var ed) && av.TryGetValue<double>(out var ad))
                    return Math.Abs(ed - ad) <= tolerance ? null : $"{path}: mong doi {ed}, nhan {ad}";
                return e.ToJsonString() == a.ToJsonString() ? null : $"{path}: mong doi {e.ToJsonString()}, nhan {a.ToJsonString()}";
        }
    }
}
