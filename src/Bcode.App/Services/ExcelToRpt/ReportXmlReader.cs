using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bcode.App.Services.ExcelToRpt;

/// <summary>
/// Đọc THAM SỐ báo cáo từ file .xml định nghĩa report của Fast (port của report_fields.py):
/// <c>&lt;fields&gt;&lt;field name="h_so_ct"&gt;&lt;header v="Số: " e="Number:"/&gt;</c> — chính là các
/// ô ?h_so_ct trong mẫu Excel, kèm chữ tiếng Việt thật để vẽ lên bản thiết kế / PDF xem trước.
/// </summary>
internal static class ReportXmlReader
{
    private static readonly XNamespace Ns = "urn:schemas-fast-com:data-report";

    /// <summary>
    /// Bỏ DOCTYPE và các entity tham chiếu file ngoài (&lt;!ENTITY b SYSTEM ".\Include\..."&gt;,
    /// &amp;b; &amp;s;...) để parser XML chạy được — phần &lt;fields&gt; cần đọc nằm ngay trong file.
    /// </summary>
    public static string Clean(string xml)
    {
        xml = Regex.Replace(xml, @"<!DOCTYPE.*?\]\s*>", "", RegexOptions.Singleline);
        return Regex.Replace(xml, @"&(?!(?:amp|lt|gt|quot|apos);)(?:%)?[A-Za-z_][\w.\-]*;", "");
    }

    private static IEnumerable<XElement> FindAll(XElement parent, string tag)
    {
        var withNs = parent.Elements(Ns + tag).ToList();
        return withNs.Count > 0 ? withNs : parent.Elements(tag);
    }

    private static JsonObject ReadField(XElement el)
    {
        var h = el.Element(Ns + "header") ?? el.Element("header");
        return new JsonObject
        {
            ["name"] = (string?)el.Attribute("name"),
            ["type"] = string.IsNullOrEmpty((string?)el.Attribute("type")) ? "String" : (string?)el.Attribute("type"),
            ["v"] = (string?)h?.Attribute("v") ?? "",
            ["e"] = (string?)h?.Attribute("e") ?? "",
        };
    }

    public static JsonObject Parse(string path, string? formId = null)
    {
        var text = new UTF8Encoding(false, false).GetString(File.ReadAllBytes(path)).TrimStart('﻿');
        var root = XDocument.Parse(Clean(text)).Root!;

        // --- tham số dùng chung cho mọi form ---
        var parameters = new Dictionary<string, JsonObject>();
        var order = new List<string>();
        foreach (var fs in FindAll(root, "fields"))
            foreach (var f in FindAll(fs, "field"))
            {
                var d = ReadField(f);
                var name = (string?)d["name"];
                if (string.IsNullOrEmpty(name)) continue;
                if (!parameters.ContainsKey(name)) order.Add(name);
                parameters[name] = d;
            }

        // --- các form, mỗi form có thể khai đè thêm tham số riêng ---
        var forms = new List<(JsonObject info, List<JsonObject> fields)>();
        foreach (var fsec in FindAll(root, "forms"))
            foreach (var fm in FindAll(fsec, "form"))
            {
                var hdr = fm.Element(Ns + "header") ?? fm.Element("header");
                var own = FindAll(fm, "fields").SelectMany(fs => FindAll(fs, "field"))
                    .Where(f => !string.IsNullOrEmpty((string?)f.Attribute("name"))).Select(ReadField).ToList();
                forms.Add((new JsonObject
                {
                    ["id"] = (string?)fm.Attribute("id"),
                    ["reportFile"] = (string?)fm.Attribute("reportFile") ?? "",
                    ["templateFile"] = (string?)fm.Attribute("templateFile") ?? "",
                    ["header"] = (string?)hdr?.Attribute("v") ?? "",
                }, own));
            }

        // Chọn form cụ thể -> tham số riêng của nó đè lên tham số chung
        string? chosen = null;
        if (!string.IsNullOrEmpty(formId))
        {
            var hit = forms.FirstOrDefault(f => (string?)f.info["id"] == formId);
            if (hit.info is not null)
            {
                chosen = formId;
                foreach (var d in hit.fields)
                {
                    var name = (string)d["name"]!;
                    if (!parameters.ContainsKey(name)) order.Add(name);
                    parameters[name] = (JsonObject)d.DeepClone();
                }
            }
        }

        return new JsonObject
        {
            ["source"] = Path.GetFileName(path),
            ["form"] = chosen,
            ["parameters"] = new JsonArray(order.Select(n => (JsonNode)parameters[n].DeepClone()).ToArray()),
            ["forms"] = new JsonArray(forms.Select(f => (JsonNode)f.info.DeepClone()).ToArray()),
        };
    }
}
