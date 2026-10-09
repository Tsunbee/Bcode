using System.Xml.Linq;

namespace BcodeViewer.App.Settings;

/// <summary>
/// Từ điển field chuẩn của FCode/FastBusiness (<c>header.xml</c>, UTF-16) — nguồn cho gợi ý "f.ma_kh" + Enter
/// (xem completion.js provideFieldTemplates). Mỗi &lt;field&gt;: header/header2, type, format, align, width, footer và
/// lookup controller/reference/key/check/information. 1 tên field có thể có nhiều khối (nhiều biến thể lookup) —
/// giữ hết, trang hiện mỗi biến thể 1 dòng. File gốc nằm ở Bcode.App\Templates\fileSource, csproj link sang
/// Assets\fields của viewer.
/// </summary>
public static class FcodeFieldDictionary
{
    public record Field(string Name, string HeaderV, string HeaderE, string Type, string Format, string Align, string Width,
        string Footer, string Controller, string Reference, string Key, string Check, string Information);

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Assets", "fields", "header.xml");

    /// <summary>Thiếu file / XML hỏng → danh sách rỗng (chỉ mất phần gợi ý này, không làm hỏng editor).</summary>
    public static List<Field> Load(string path)
    {
        var list = new List<Field>();
        try
        {
            if (!File.Exists(path)) return list;
            // XDocument.Load tự nhận BOM UTF-16 của file.
            foreach (var f in XDocument.Load(path).Root?.Elements("field") ?? Enumerable.Empty<XElement>())
            {
                string El(string n) => ((string?)f.Element(n) ?? "").Trim();
                string At(string n) => ((string?)f.Attribute(n) ?? "").Trim();
                var name = At("name");
                if (name.Length == 0) continue;
                list.Add(new Field(name, El("header"), El("header2"), At("type"), At("format"), At("align"), At("width"),
                    El("footer"), El("controller"), El("reference"), El("key"), El("check"), El("information")));
            }
        }
        catch { list.Clear(); }
        return list;
    }
}
