using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Bcode.App.Models;

namespace Bcode.App.Services.Api;

/// <summary>
/// Form / trường API theo dự án — đọc file ánh xạ cột của FastAPIService (mẫu LT3): <c>&lt;API Path&gt;\Config\ColumnMappings.xml</c>
/// (hoặc đường dẫn khoá appSettings <c>ColumnMappingXmlPath</c> trong Web.config của service, như service tự đọc). Lớp này CHỈ ĐỌC — ghi file chỉ qua
/// "Word ▾ → Nhập từ Word" (<see cref="ApiMappingWord"/>, người dùng xác nhận, có sao lưu).
/// <para>Cấu trúc (giống FastAPIs.Config.XmlMapping.XmlFormConfig của service):</para>
/// <code>
/// &lt;Mappings&gt;
///   &lt;!-- tiêu đề form --&gt;
///   &lt;Form name="setXxx" table="bảng_đích" store="fs_sync_xxx"&gt;
///     &lt;Columns&gt;&lt;Column Param="TênJSON" Field="cột_sql" SqlDataType="VARCHAR(32)" Required="true" Default="1" DataType="date|decimal" Description="…"/&gt;&lt;/Columns&gt;
///     &lt;ChildTables&gt;                       ← có = chứng từ (api/SyncVoucher), không = danh mục (api/SyncData)
///       &lt;ChildTable name="detail" table="d71"&gt;&lt;ParentField&gt;ID&lt;/ParentField&gt;&lt;ChildField&gt;ID&lt;/ChildField&gt;
///         &lt;Columns&gt;&lt;Column Parent="ID" Field="voucher_id"/&gt; (lấy từ cha, không gửi) &lt;Column Param="ItemCode" …/&gt;&lt;/Columns&gt;
///       &lt;/ChildTable&gt;
///     &lt;/ChildTables&gt;
///   &lt;/Form&gt;
/// &lt;/Mappings&gt;
/// </code>
/// Body JSON: <c>{"form": name, "data": [{Param…, "&lt;tên ChildTable&gt;": [{Param…}]}]}</c>. Service bắt buộc trường Required không trống (cột có Parent bỏ qua).
/// Mỗi form chuyển thành <see cref="Lt3Templates.FormDoc"/> (Source = "mapping") để dùng chung màn Khai báo API (chọn form, Tài liệu form, Kiểm tra, Sinh JSON).
/// </summary>
public static class ApiColumnMappings
{
    public const string Source = "mapping";

    /// <summary>Kết quả đọc mapping của 1 Project: <paramref name="File"/> = file đã thử đọc ("" = chưa khai báo API Path), <paramref name="Error"/> = lý do không dùng được.</summary>
    public sealed record Info(string Project, string ApiPath, string File, List<Lt3Templates.FormDoc> Forms, string? Error)
    {
        public bool Ok => Error is null && Forms.Count > 0;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Stamp, long Size, List<Lt3Templates.FormDoc> Forms)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>File mapping của thư mục API: API Path trỏ thẳng file .xml → file đó; Web.config có ColumnMappingXmlPath → theo đó; mặc định Config\ColumnMappings.xml.</summary>
    public static string MappingFile(string apiPath)
    {
        var root = (apiPath ?? "").Trim().Trim('"');
        if (root.Length == 0) return "";
        if (root.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return root;
        try
        {
            var webConfig = Path.Combine(root, "Web.config");
            if (File.Exists(webConfig))
            {
                var add = XDocument.Load(webConfig).Descendants("appSettings").Elements("add")
                    .FirstOrDefault(e => string.Equals((string?)e.Attribute("key"), "ColumnMappingXmlPath", StringComparison.OrdinalIgnoreCase));
                var v = ((string?)add?.Attribute("value") ?? "").Trim();
                if (v.Length > 0) return Path.IsPathRooted(v) ? v : Path.Combine(root, v.TrimStart('~', '/', '\\').Replace('/', '\\'));
            }
        }
        catch { /* Web.config hỏng / không đọc được — dùng mặc định như service */ }
        return Path.Combine(root, "Config", "ColumnMappings.xml");
    }

    /// <summary>Đọc mapping của Project (chạy nền — đường dẫn UNC có thể chậm). Không có API Path / không có file → Info.Ok = false (dùng chuẩn LT3).</summary>
    public static Task<Info> LoadAsync(Workspace? ws) => Task.Run(() => Load(ws));

    public static Info Load(Workspace? ws)
    {
        var project = ws is null ? "" : string.IsNullOrWhiteSpace(ws.ProjectId) ? ws.Name : ws.ProjectId;
        var apiPath = ws?.ApiPath?.Trim() ?? "";
        if (ws is null) return new Info("", "", "", new(), "Chưa chọn Project");
        if (apiPath.Length == 0) return new Info(project, "", "", new(), $"Project {project} chưa khai báo API Path");
        var file = MappingFile(apiPath);
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists) return new Info(project, apiPath, file, new(), "Không thấy " + file);
            lock (Gate)
                if (Cache.TryGetValue(fi.FullName, out var c) && c.Stamp == fi.LastWriteTimeUtc && c.Size == fi.Length)
                    return new Info(project, apiPath, file, c.Forms, null);
            string text;
            using (var fs = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs, detectEncodingFromByteOrderMarks: true))
                text = sr.ReadToEnd();
            var forms = Parse(text, file);
            lock (Gate) Cache[fi.FullName] = (fi.LastWriteTimeUtc, fi.Length, forms);
            return new Info(project, apiPath, file, forms, forms.Count == 0 ? "Không có <Form> nào trong " + file : null);
        }
        catch (Exception ex) { return new Info(project, apiPath, file, new(), "Không đọc được " + file + ": " + ex.Message); }
    }

    /// <summary>Chuyển nội dung ColumnMappings.xml thành danh sách form (bỏ form trùng tên — giữ bản đầu, như service đọc vào từ điển).</summary>
    public static List<Lt3Templates.FormDoc> Parse(string xml, string docPath = "")
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var list = new List<Lt3Templates.FormDoc>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var form in doc.Descendants("Form"))
        {
            var name = ((string?)form.Attribute("name") ?? "").Trim();
            if (name.Length == 0 || !seen.Add(name)) continue;
            var table = (string?)form.Attribute("table") ?? "";
            var store = (string?)form.Attribute("store") ?? "";
            var columns = Columns(form.Element("Columns"));
            var children = (form.Element("ChildTables")?.Elements("ChildTable") ?? Enumerable.Empty<XElement>())
                .Select(ct => (Name: ((string?)ct.Attribute("name") ?? "").Trim(), Table: (string?)ct.Attribute("table") ?? "",
                               ParentField: ((string?)ct.Element("ParentField") ?? "").Trim(), ChildField: ((string?)ct.Element("ChildField") ?? "").Trim(),
                               Columns: Columns(ct.Element("Columns"))))
                .Where(ct => ct.Name.Length > 0).ToList();
            var voucher = children.Count > 0;
            var kind = voucher ? "SyncVoucher" : "SyncData";

            var headFields = columns.Where(c => c.Parent.Length == 0 && c.Param.Length > 0).Select(Field).ToList();
            headFields.AddRange(children.Select(ct => new Lt3Templates.FieldDoc(ct.Name, "List[Object]", false, $"Danh sách {ct.Name} → bảng {ct.Table}")));
            var sections = new List<Lt3Templates.SectionDoc>
            {
                new(voucher ? "header" : "data", (voucher ? "Header" : "Các trường") + (table.Length > 0 ? " — bảng " + table : ""), headFields),
            };
            foreach (var ct in children)
            {
                var auto = ct.Columns.Where(c => c.Parent.Length > 0).Select(c => c.Field + " ← " + c.Parent).ToList();
                sections.Add(new(ct.Name, ct.Name + (ct.Table.Length > 0 ? " — bảng " + ct.Table : "") + (ct.ParentField.Length > 0 ? $" (khoá cha {ct.ParentField})" : "")
                    + (auto.Count > 0 ? " · tự lấy từ cha: " + string.Join(", ", auto) : ""),
                    ct.Columns.Where(c => c.Parent.Length == 0 && c.Param.Length > 0).Select(Field).ToList()));
            }

            var parentKeys = new HashSet<string>(children.Select(c => c.ParentField).Where(x => x.Length > 0), StringComparer.OrdinalIgnoreCase);
            var row = Row(columns, parentKeys);
            foreach (var ct in children) row[ct.Name] = new JsonArray(Row(ct.Columns, new HashSet<string>()));
            var sample = new JsonObject { ["form"] = name, ["data"] = new JsonArray(row) };

            list.Add(new Lt3Templates.FormDoc(name, kind, "api/" + kind, Title(form) ?? (table.Length > 0 ? "Bảng " + table : name),
                "Theo ColumnMappings.xml của dự án" + (table.Length > 0 ? " — bảng " + table : "") + (store.Length > 0 ? ", store " + store : "") + ".",
                sections, sample.ToJsonString(Indented), docPath) { Source = Source });
        }
        return list;
    }

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Col(string Param, string Field, string SqlType, string DataType, bool Required, string? Default, string Parent, string Description);

    private static List<Col> Columns(XElement? cols) =>
        (cols?.Elements("Column") ?? Enumerable.Empty<XElement>()).Select(c => new Col(
            ((string?)c.Attribute("Param") ?? "").Trim(), ((string?)c.Attribute("Field") ?? "").Trim(),
            ((string?)c.Attribute("SqlDataType") ?? "").Trim(), ((string?)c.Attribute("DataType") ?? "").Trim(),
            bool.TryParse((string?)c.Attribute("Required"), out var r) && r,   // như service: chỉ thuộc tính "Required" (đúng hoa/thường)
            (string?)c.Attribute("Default"), ((string?)c.Attribute("Parent") ?? "").Trim(), ((string?)c.Attribute("Description") ?? "").Trim()))
        .Where(c => c.Field.Length > 0 || c.Param.Length > 0).ToList();

    private static Lt3Templates.FieldDoc Field(Col c) => new(c.Param,
        c.SqlType.Length > 0 ? c.SqlType : c.DataType,
        c.Required,
        string.Join(" · ", new[] { c.Description, c.Field.Length > 0 ? "cột " + c.Field : "", c.Default is null ? "" : "mặc định " + c.Default }.Where(x => x.Length > 0)));

    /// <summary>Chú thích &lt;!-- … --&gt; ngay trước thẻ Form (tiêu đề form trong file của dự án).</summary>
    private static string? Title(XElement form)
    {
        for (var n = form.PreviousNode; n is not null; n = n.PreviousNode)
        {
            if (n is XComment cm) { var t = cm.Value.Trim(); return t.Length > 0 ? t : null; }
            if (n is XElement) return null;
        }
        return null;
    }

    /// <summary>1 dòng dữ liệu mẫu: giá trị mặc định của mapping nếu có; khoá cha (ParentField của ChildTable) = {{key}}; ngày = {{today}};
    /// số = 0; Status chưa có mặc định = 1; chữ = "" (trường bắt buộc còn trống → Kiểm tra nhắc điền).</summary>
    private static JsonObject Row(List<Col> cols, HashSet<string> parentKeys)
    {
        var o = new JsonObject();
        foreach (var c in cols.Where(c => c.Parent.Length == 0 && c.Param.Length > 0))
        {
            if (o.ContainsKey(c.Param)) continue;
            var t = TypeOf(c);
            o[c.Param] = parentKeys.Contains(c.Param) ? "{{key}}"
                : c.Default is not null ? (t == 'n' && decimal.TryParse(c.Default, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : JsonValue.Create(c.Default))
                : t == 'd' ? JsonValue.Create("{{today}}")
                : string.Equals(c.Param, "Status", StringComparison.OrdinalIgnoreCase) ? (t == 'n' ? JsonValue.Create(1) : JsonValue.Create("1"))
                : t == 'n' ? JsonValue.Create(0)
                : JsonValue.Create("");
        }
        return o;
    }

    /// <summary>'d' ngày, 'n' số, 's' chữ — theo SqlDataType (hoặc DataType của service).</summary>
    private static char TypeOf(Col c)
    {
        var s = (c.SqlType.Length > 0 ? c.SqlType : c.DataType).ToUpperInvariant();
        var p = s.IndexOf('('); if (p >= 0) s = s[..p];
        s = s.Trim();
        if (s is "DATE" or "DATETIME" or "DATETIME2" or "SMALLDATETIME" or "DATETIMEOFFSET") return 'd';
        if (s is "DECIMAL" or "NUMERIC" or "MONEY" or "SMALLMONEY" or "FLOAT" or "REAL" or "INT" or "INTEGER" or "BIGINT" or "SMALLINT" or "TINYINT" or "BIT" or "LONG" or "DOUBLE" or "BYTE" or "INT32" or "INT64") return 'n';
        return 's';
    }

    /// <summary>Danh mục form dùng cho Khai báo API: form của mapping trước, rồi form chuẩn LT3 chưa có trong mapping (vd GetData).</summary>
    public static List<Lt3Templates.FormDoc> Merge(Info? map)
    {
        var std = Lt3Templates.Catalog();
        if (map is not { Ok: true }) return std;
        var names = new HashSet<string>(map.Forms.Select(f => f.Form), StringComparer.OrdinalIgnoreCase);
        return map.Forms.Concat(std.Where(f => !names.Contains(f.Form))).ToList();
    }
}
