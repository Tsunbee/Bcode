using System.Text.RegularExpressions;

namespace Bcode.App.Services.Rpt.Builder;

/// <summary>Không đọc được file mẫu source (Templates\fileSource\BuildReport) — báo rõ file nào, ở đâu để người dùng chép lại / sửa.</summary>
public sealed class TemplateMissingException : Exception
{
    public TemplateMissingException(string message) : base(message) { }
}

/// <summary>
/// Đọc và "điền" các file mẫu source của "Tạo báo cáo" — thư mục <c>Templates\fileSource\BuildReport</c> cạnh Bcode.exe (Bcode truyền đường dẫn qua
/// <see cref="Bcode.ReportBuilder.IReportHost.TemplateDir"/>). Không có XML / T-SQL cố định trong code: Filter, Grid, Main, khung procedure và các mảnh
/// (trường lọc, cột lưới, dòng nhóm...) đều là file rời, sửa được mà không cần build lại. Cùng kiểu đánh dấu với "Tạo nhanh danh mục / báo cáo":
/// <list type="bullet">
/// <item><c>[#TEN#]</c> viết HOA = giá trị; đứng riêng một dòng mà giá trị rỗng thì xoá cả dòng, giá trị nhiều dòng thì giữ nguyên thụt lề của giá trị.</item>
/// <item><c>[#Ten#]…[#Ten#]</c> theo cặp (chữ thường lẫn hoa) = khối tuỳ chọn — bật thì giữ nội dung (bỏ dấu), tắt thì xoá cả khối.</item>
/// </list>
/// Kết quả luôn xuống dòng kiểu Windows (CRLF).
/// </summary>
internal static class TemplateStore
{
    // một lượt cho cả mốc đứng riêng một dòng (own) lẫn mốc chen giữa dòng (in)
    private static readonly Regex Combined = new(
        @"^[ \t]*\[#(?<own>[A-Za-z0-9_]+)#\][ \t]*(?<nl>\n|$)|\[#(?<in>[A-Za-z0-9_]+)#\]",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public static string Dir => Bcode.ReportBuilder.ReportBuilderEnv.TemplateDir;

    public static string Load(string rel)
    {
        var dir = Dir;
        if (string.IsNullOrWhiteSpace(dir)) throw new TemplateMissingException("Chưa khai thư mục source mẫu (Templates\\fileSource\\BuildReport).");
        var path = Path.Combine(dir, rel);
        if (!File.Exists(path)) throw new TemplateMissingException($"Không thấy file mẫu source: {path}");
        return File.ReadAllText(path).TrimStart('﻿').Replace("\r\n", "\n");
    }

    /// <summary>Điền file mẫu <paramref name="rel"/>. Mốc <c>[#...#]</c> còn sót (không có trong <paramref name="values"/>) là lỗi mẫu → ném <see cref="TemplateMissingException"/>.</summary>
    public static string Render(string rel, IReadOnlyDictionary<string, string> values, params (string Name, bool On)[] toggles)
    {
        var text = Load(rel);
        foreach (var (name, on) in toggles) text = ApplyToggle(text, name, on);

        // MỘT lượt duy nhất: chữ vừa điền vào không bị quét lại (T-SQL có thể chứa "[#...#]").
        var unknown = new List<string>();
        text = Combined.Replace(text, m =>
        {
            var own = m.Groups["own"].Success;
            var name = own ? m.Groups["own"].Value : m.Groups["in"].Value;
            if (!values.TryGetValue(name, out var v)) { unknown.Add($"[#{name}#]"); return m.Value; }
            v = v.Replace("\r\n", "\n");
            if (own) return v.Length == 0 ? "" : v + m.Groups["nl"].Value;
            return v;
        });
        if (unknown.Count > 0) throw new TemplateMissingException($"File mẫu {rel} còn mốc chưa có giá trị: {string.Join(", ", unknown.Distinct())}");
        return text.Replace("\n", "\r\n");
    }

    /// <summary>Khối <c>[#Ten#]…[#Ten#]</c>: bật = giữ nội dung, tắt = xoá cả khối.</summary>
    private static string ApplyToggle(string text, string name, bool on)
    {
        var marker = Regex.Escape($"[#{name}#]");
        return Regex.Replace(text, marker + "(.*?)" + marker, m => on ? m.Groups[1].Value : "", RegexOptions.Singleline);
    }

    /// <summary>Điền một mảnh nhỏ (fragment) — cùng quy tắc nhưng KHÔNG xuống dòng kiểu Windows và bỏ dòng trống cuối (để ghép vào mẫu lớn rồi mới đổi một lần).</summary>
    public static string Fragment(string rel, IReadOnlyDictionary<string, string> values, params (string Name, bool On)[] toggles) =>
        Render("Fragments\\" + rel, values, toggles).Replace("\r\n", "\n").TrimEnd('\n');
}
