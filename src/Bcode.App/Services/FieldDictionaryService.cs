using System.Xml.Linq;

namespace Bcode.App.Services;

/// <summary>1 cách tra cứu (AutoComplete) cho 1 field — lấy từ từ điển field FCode (header.xml).</summary>
public record FieldLookup(string Controller, string Reference, string Key, string Check, string Information)
{
    /// <summary>"ma_bp$dmbp.ten_bp%l" → bảng "dmbp", cột mã "ma_bp" (để join lấy tên trong lưới chi tiết). Trống nếu không đọc được.</summary>
    public (string Table, string KeyColumn) Source
    {
        get
        {
            var dollar = Information.IndexOf('$');
            var dot = Information.IndexOf('.', dollar + 1);
            return dollar > 0 && dot > dollar ? (Information[(dollar + 1)..dot], Information[..dollar]) : ("", "");
        }
    }

    /// <summary>Biến thể "mặc định": không ràng buộc gì thêm ngoài status = '1'.</summary>
    public bool IsSimple =>
        Check.Replace(" ", "") is "" or "1=1" &&
        Key.Replace(" ", "").Replace("'", "") is "" or "status=1";
}

/// <summary>
/// Từ điển field chuẩn của FCode/FastBusiness: <c>Templates\fileSource\header.xml</c> (UTF-16; mỗi field: header v/e, type, width
/// và lookup controller/reference/key/check/information — 1 tên field có thể có nhiều biến thể lookup) và
/// <c>Templates\fileSource\parameters.xml</c> (header tham số báo cáo <c>h_xxx</c> — chỉ dùng làm nguồn header phụ, bỏ "h_").
/// Nạp 1 lần, giữ cho cả tiến trình.
/// </summary>
public sealed class FieldDictionaryService
{
    public record Entry(string Name, string HeaderV, string HeaderE, string Type, int? Width, FieldLookup? Lookup);

    private static readonly Lazy<FieldDictionaryService> _instance = new(() => new FieldDictionaryService());
    public static FieldDictionaryService Instance => _instance.Value;

    private static string Dir => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Templates", "fileSource");

    private readonly Dictionary<string, List<Entry>> _fields = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string V, string E)> _parameters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lỗi khi nạp (thiếu file / XML hỏng) — để báo lên form, không ném ra.</summary>
    public string? LoadError { get; }

    private FieldDictionaryService()
    {
        try
        {
            var header = Path.Combine(Dir, "header.xml");
            if (File.Exists(header))
                foreach (var f in XDocument.Load(header).Root?.Elements("field") ?? Enumerable.Empty<XElement>())
                {
                    string El(string n) => ((string?)f.Element(n) ?? "").Trim();
                    var name = ((string?)f.Attribute("name") ?? "").Trim();
                    if (name.Length == 0) continue;
                    var controller = El("controller");
                    var lookup = controller.Length == 0 ? null
                        : new FieldLookup(controller, El("reference"), El("key"), El("check"), El("information"));
                    var entry = new Entry(name, El("header"), El("header2"), ((string?)f.Attribute("type") ?? "").Trim(),
                        int.TryParse((string?)f.Attribute("width"), out var w) && w > 0 ? w : null, lookup);
                    if (!_fields.TryGetValue(name, out var list)) _fields[name] = list = new();
                    list.Add(entry);
                }

            var parameters = Path.Combine(Dir, "parameters.xml");
            if (File.Exists(parameters))
                foreach (var f in XDocument.Load(parameters).Root?.Elements("field") ?? Enumerable.Empty<XElement>())
                {
                    var name = ((string?)f.Attribute("name") ?? "").Trim();
                    if (!name.StartsWith("h_", StringComparison.OrdinalIgnoreCase)) continue;
                    var v = ((string?)f.Attribute("header") ?? "").Trim().TrimEnd(':').Trim();
                    var e = ((string?)f.Attribute("header2") ?? "").Trim().TrimEnd(':').Trim();
                    if (v.Length > 0) _parameters.TryAdd(name[2..], (v, e.Length > 0 ? e : v));
                }
        }
        catch (Exception ex) { LoadError = ex.Message; }
    }

    /// <summary>Header chuẩn của field: header.xml (bản đầu tiên có header) rồi tới parameters.xml.</summary>
    public (string V, string E)? Header(string name)
    {
        if (_fields.TryGetValue(name, out var list) && list.FirstOrDefault(x => x.HeaderV.Length > 0) is { } e)
            return (e.HeaderV, e.HeaderE.Length > 0 ? e.HeaderE : e.HeaderV);
        return _parameters.TryGetValue(name, out var p) ? p : null;
    }

    /// <summary>Các biến thể lookup của field (không trùng nhau), biến thể đơn giản (status = '1') lên đầu.</summary>
    public IReadOnlyList<FieldLookup> Lookups(string name) =>
        !_fields.TryGetValue(name, out var list) ? Array.Empty<FieldLookup>() :
        list.Where(x => x.Lookup != null).Select(x => x.Lookup!).Distinct()
            .Select((l, i) => (l, i)).OrderBy(x => x.l.IsSimple ? 0 : 1).ThenBy(x => x.i).Select(x => x.l).ToList();
}
