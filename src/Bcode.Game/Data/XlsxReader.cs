using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Bcode.Game.Data;

/// <summary>Đọc giá trị ô của file .xlsx — chỉ đủ cho bảng câu hỏi (chuỗi dùng chung, chuỗi nội tuyến, số). Không công thức, không định dạng.
/// Tự viết bằng ZipArchive + XmlReader để game không phải kéo thêm thư viện Excel (ClosedXML ~ vài MB).
/// Mỗi &lt;si&gt; / &lt;c&gt; đọc trọn bằng XNode.ReadFrom (đã tự đưa reader sang nút kế — vòng lặp không gọi Read() thêm, tránh nhảy cóc nút).</summary>
public static class XlsxReader
{
    private static readonly XNamespace NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    public sealed record Sheet(string Name, List<string?[]> Rows);

    public static List<Sheet> Read(string path)
    {
        // FileShare.ReadWrite: file đang mở trong Excel vẫn đọc được.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        var shared = ReadSharedStrings(zip);
        var rels = ReadRels(zip, "xl/_rels/workbook.xml.rels");
        var result = new List<Sheet>();
        var wb = zip.GetEntry("xl/workbook.xml") ?? throw new InvalidDataException("Không phải file .xlsx (thiếu xl/workbook.xml).");
        using (var r = XmlReader.Create(wb.Open()))
        {
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element || r.LocalName != "sheet") continue;
                var name = r.GetAttribute("name") ?? "";
                var rid = r.GetAttribute("id", NsRel);
                if (rid is null || !rels.TryGetValue(rid, out var target)) continue;
                var entryPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                var entry = zip.GetEntry(entryPath);
                if (entry is null) continue;
                result.Add(new Sheet(name, ReadSheet(entry, shared)));
            }
        }
        return result;
    }

    private static Dictionary<string, string> ReadRels(ZipArchive zip, string path)
    {
        var map = new Dictionary<string, string>();
        var e = zip.GetEntry(path);
        if (e is null) return map;
        using var r = XmlReader.Create(e.Open());
        while (r.Read())
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship" && r.GetAttribute("Id") is { } id && r.GetAttribute("Target") is { } t)
                map[id] = t;
        return map;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var e = zip.GetEntry("xl/sharedStrings.xml");
        if (e is null) return list;
        using var r = XmlReader.Create(e.Open());
        r.MoveToContent();
        while (!r.EOF)
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "si")
            {
                var si = (XElement)XNode.ReadFrom(r);
                list.Add(RichText(si));
                continue;
            }
            r.Read();
        }
        return list;
    }

    /// <summary>Chữ của &lt;si&gt; / &lt;is&gt;: mọi &lt;t&gt; (cả trong các run &lt;r&gt;), bỏ phiên âm &lt;rPh&gt;.</summary>
    private static string RichText(XElement el)
    {
        var sb = new StringBuilder();
        foreach (var t in el.Descendants(NsMain + "t"))
            if (t.Parent?.Name != NsMain + "rPh") sb.Append(t.Value);
        return sb.ToString();
    }

    private static List<string?[]> ReadSheet(ZipArchiveEntry entry, List<string> shared)
    {
        var rows = new List<string?[]>();
        using var r = XmlReader.Create(entry.Open());
        List<string?>? cur = null;
        r.MoveToContent();
        while (!r.EOF)
        {
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "row")
            {
                var rn = int.TryParse(r.GetAttribute("r"), out var n) ? n - 1 : rows.Count;
                while (rows.Count < rn) rows.Add(Array.Empty<string?>());   // dòng trống bị bỏ trong XML
                if (r.IsEmptyElement) { rows.Add(Array.Empty<string?>()); cur = null; }
                else cur = new List<string?>();
                r.Read();
                continue;
            }
            if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "row")
            {
                if (cur is not null) rows.Add(cur.ToArray());
                cur = null;
                r.Read();
                continue;
            }
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "c" && cur is not null)
            {
                var c = (XElement)XNode.ReadFrom(r);
                var col = ColIndex((string?)c.Attribute("r"), cur.Count);
                var type = (string?)c.Attribute("t");
                string? value;
                if (type == "inlineStr") value = c.Element(NsMain + "is") is { } isEl ? RichText(isEl) : null;
                else
                {
                    value = c.Element(NsMain + "v")?.Value;
                    if (type == "s" && value is not null) value = int.TryParse(value, out var si) && si >= 0 && si < shared.Count ? shared[si] : null;
                }
                while (cur.Count < col) cur.Add(null);
                if (cur.Count == col) cur.Add(value); else cur[col] = value;
                continue;
            }
            r.Read();
        }
        return rows;
    }

    /// <summary>"AB12" → 27 (tính từ 0). Không có tham chiếu thì lấy ô kế tiếp.</summary>
    private static int ColIndex(string? cellRef, int fallback)
    {
        if (string.IsNullOrEmpty(cellRef)) return fallback;
        var n = 0;
        foreach (var ch in cellRef)
        {
            if (ch is >= 'A' and <= 'Z') n = n * 26 + (ch - 'A' + 1);
            else break;
        }
        return n == 0 ? fallback : n - 1;
    }
}
