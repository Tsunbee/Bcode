using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Bcode.App.Services.ExcelToRpt;

/// <summary>
/// Đọc danh sách bảng / trường từ file .xsd của DataSet (port của xsd_fields.py) — để đối
/// chiếu mẫu Excel và báo trường sai TRƯỚC khi tạo .rpt, không cần Crystal. JSON trả về giữ
/// đúng dạng bản Python vì giao diện (index.html) đọc thẳng nó.
/// </summary>
internal static class XsdFieldReader
{
    private static readonly XNamespace Xs = "http://www.w3.org/2001/XMLSchema";
    private static readonly XNamespace MsData = "urn:schemas-microsoft-com:xml-msdata";

    private static readonly Dictionary<string, string> TypeMap = new()
    {
        ["string"] = "string", ["normalizedString"] = "string", ["token"] = "string",
        ["int"] = "number", ["integer"] = "number", ["long"] = "number", ["short"] = "number",
        ["byte"] = "number", ["unsignedInt"] = "number", ["unsignedLong"] = "number",
        ["decimal"] = "number", ["float"] = "number", ["double"] = "number",
        ["boolean"] = "boolean",
        ["date"] = "date", ["dateTime"] = "datetime", ["time"] = "time",
    };

    private static string FieldType(XElement el)
    {
        var t = (string?)el.Attribute("type") ?? "";
        if (t.Contains(':')) t = t[(t.IndexOf(':') + 1)..];
        if (t.Length > 0) return TypeMap.GetValueOrDefault(t, "string");
        // Kiểu lồng: <xs:simpleType><xs:restriction base="xs:string">
        foreach (var r in el.Descendants(Xs + "restriction"))
        {
            var b = ((string?)r.Attribute("base") ?? "").Split(':')[^1];
            return TypeMap.GetValueOrDefault(b, "string");
        }
        return "string";
    }

    /// <summary>Các trường trực tiếp của một bảng (bỏ qua bảng lồng bảng).</summary>
    private static List<(string name, string type)> FieldsOf(XElement table)
    {
        var output = new List<(string, string)>();
        var ct = table.Element(Xs + "complexType");
        if (ct is null) return output;
        foreach (var seq in ct.Elements())
        {
            if (seq.Name.LocalName is not ("sequence" or "all" or "choice")) continue;
            foreach (var f in seq.Elements())
            {
                if (f.Name.LocalName != "element") continue;
                var name = (string?)f.Attribute("name");
                if (string.IsNullOrEmpty(name)) continue;
                // Phần tử có complexType lồng là BẢNG con, không phải trường
                if (f.Element(Xs + "complexType") is not null && f.Attribute("type") is null) continue;
                output.Add((name, FieldType(f)));
            }
        }
        return output;
    }

    public static JsonObject Parse(string path)
    {
        var root = XDocument.Load(path).Root!;

        var ds = root.Descendants(Xs + "element").FirstOrDefault(el => (string?)el.Attribute(MsData + "IsDataSet") == "true");

        var tables = new JsonArray();
        var seen = new HashSet<string>();
        void Add(XElement el)
        {
            var name = (string?)el.Attribute("name");
            if (string.IsNullOrEmpty(name) || seen.Contains(name)) return;
            var fs = FieldsOf(el);
            if (fs.Count == 0) return;
            seen.Add(name);
            tables.Add(new JsonObject
            {
                ["index"] = tables.Count + 1,
                ["alias"] = name,
                ["name"] = name,
                ["fields"] = new JsonArray(fs.Select(f => (JsonNode)new JsonObject { ["name"] = f.name, ["type"] = f.type }).ToArray()),
            });
        }

        if (ds?.Element(Xs + "complexType") is { } dsType)
            foreach (var ch in dsType.Descendants(Xs + "element")) Add(ch);
        // Không có dấu IsDataSet -> lấy mọi element cấp cao có trường bên trong
        if (tables.Count == 0)
        {
            foreach (var el in root.Elements(Xs + "element")) Add(el);
            if (tables.Count == 0)
                foreach (var el in root.Descendants(Xs + "element")) Add(el);
        }

        return new JsonObject
        {
            ["source"] = Path.GetFileName(path),
            ["tables"] = tables,
            ["parameters"] = new JsonArray(),     // XSD không mô tả tham số report
        };
    }
}
