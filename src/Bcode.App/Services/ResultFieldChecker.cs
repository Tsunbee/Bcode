using System.Text;
using System.Text.RegularExpressions;
using Kind = Bcode.App.Services.DesignFieldsGenerator.Kind;

namespace Bcode.App.Services;

/// <summary>
/// "Check Fields" của SQL Query (như FCode urQuery — giải mã 11/2026): đối chiếu 1 danh sách field người dùng dán vào với các cột của bảng kết quả
/// đang được chọn, báo field nào KHÔNG có trong bảng.
/// <list type="bullet">
/// <item>Danh sách: tách theo dấu phẩy (như FCode) và cả chấm phẩy / xuống dòng / Tab / khoảng trắng; bỏ ngoặc vuông, nháy, dấu ~ (FCode bỏ ~),
/// bỏ tiền tố bí danh "a.ma_kh" → ma_kh; trùng tên chỉ tính 1 lần. Không phân biệt hoa thường (như DataTable.Columns.Contains của FCode).</item>
/// <item>Field đuôi %l (ten_kh%l) = phải có cả ten_kh và ten_kh2 (quy ước song ngữ của Fast, FCode làm vậy).</item>
/// <item>Dán đường dẫn 1 file Grid / Dir / Filter (.xml / .f) thay cho danh sách → lấy các &lt;field name&gt; trong &lt;fields&gt; của file (bỏ external="true")
/// đối chiếu với bảng, kèm kiểm kiểu như FCode (Decimal/Int → cột số, Date/DateTime → cột ngày, Boolean → cột bit, còn lại → cột chuỗi).</item>
/// </list>
/// Chỉ so tên cột của kết quả đã có trong Bcode — không chạy gì xuống SQL Server.
/// </summary>
public static class ResultFieldChecker
{
    /// <summary>1 cột của bảng kết quả: tên + nhóm kiểu + tên kiểu để hiện trong thông báo.</summary>
    public sealed record Column(string Name, Kind Kind, string TypeName);

    /// <summary>Kết quả: <paramref name="Ok"/> = khớp hết; <paramref name="Text"/> = nội dung thông báo (có thể dài khi kiểm theo file).</summary>
    public sealed record Report(bool Ok, string Text, int Errors, int Total);

    private static readonly char[] Separators = [',', ';', '\r', '\n', '\t', ' '];

    /// <summary>Tách danh sách field (thứ tự giữ nguyên, bỏ trùng không phân biệt hoa thường).</summary>
    public static List<string> SplitFields(string input)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in (input ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var s = raw.Trim().Trim('[', ']', '"', '\'', '`', '(', ')').Replace("~", "").Replace("[", "").Replace("]", "");
            var dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s[(dot + 1)..];   // a.ma_kh / dbo.dmkh.ma_kh → ma_kh
            s = s.Trim();
            if (s.Length > 0 && seen.Add(s)) list.Add(s);
        }
        return list;
    }

    /// <summary>Ô nhập là đường dẫn 1 file có thật (bỏ nháy bao ngoài) → trả đường dẫn đó, không thì null.</summary>
    public static string? AsFilePath(string input)
    {
        var p = (input ?? "").Trim().Trim('"').Trim();
        if (p.Length < 4 || p.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || p.IndexOf(',') >= 0) return null;
        try { return File.Exists(p) ? Path.GetFullPath(p) : null; } catch { return null; }
    }

    /// <summary>Đối chiếu ô nhập (danh sách field hoặc đường dẫn file Grid / Dir / Filter) với các cột của bảng.</summary>
    public static Report Check(string input, IReadOnlyList<Column> columns)
    {
        var file = AsFilePath(input);
        return file is not null ? CheckFile(file, columns) : CheckList(SplitFields(input), columns);
    }

    public static Report CheckList(IReadOnlyList<string> fields, IReadOnlyList<Column> columns)
    {
        var names = new HashSet<string>(columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var expanded = new List<string>();
        foreach (var f in fields)
        {
            if (f.EndsWith("%l", StringComparison.OrdinalIgnoreCase) && f.Length > 2) { expanded.Add(f[..^2]); expanded.Add(f[..^2] + "2"); }
            else expanded.Add(f);
        }
        expanded = expanded.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (expanded.Count == 0) return new Report(false, "Chưa nhập field nào (ngăn cách bằng dấu phẩy, chấm phẩy hoặc xuống dòng).", 1, 0);
        var missing = expanded.Where(f => !names.Contains(f)).ToList();
        var total = expanded.Count;
        var matched = total - missing.Count;
        if (missing.Count == 0)
            return new Report(true, $"Matched — khớp hết {total}/{total} field.", 0, total);
        var text = $"Not matched ({missing.Count}/{total}):\r\n{string.Join(", ", missing)}\r\n\r\nKhớp {matched}/{total} field.";
        return new Report(false, text, missing.Count, total);
    }

    /// <summary>Kiểm theo file Grid / Dir / Filter: mỗi &lt;field name&gt; (trừ external="true") phải có cột cùng tên và kiểu hợp (như FCode).</summary>
    public static Report CheckFile(string path, IReadOnlyList<Column> columns)
    {
        string xml;
        try { xml = File.ReadAllText(path); }
        catch (Exception ex) { return new Report(false, $"Không đọc được file {path}:\r\n{ex.Message}", 1, 0); }
        var name = Path.GetFileName(path);
        if (xml.Contains("<Encrypted", StringComparison.OrdinalIgnoreCase))
            return new Report(false, $"File {name} đang mã hoá (<Encrypted>) — không đọc được <fields>.", 1, 0);
        xml = Regex.Replace(xml, @"<!--.*?-->", "", RegexOptions.Singleline);   // bỏ phần đã comment (như GridControllerReader)
        var fields = Rpt.GridControllerReader.ParseFields(xml)
            .Where(f => !string.Equals(f.Attrs.GetValueOrDefault("external"), "true", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (fields.Count == 0)
            return new Report(false, $"Không thấy <field name=\"…\"> trong khối <fields> của {name}.\r\nChỉ đọc được file Grid / Dir / Filter.", 1, 0);

        var byName = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in columns) byName.TryAdd(c.Name, c);
        var missing = new List<string>();
        var badType = new List<string>();
        var errorFields = 0;
        foreach (var f in fields)
        {
            var field = f.Name;
            var before = missing.Count + badType.Count;
            Column? col = null;
            if (field.EndsWith("%l", StringComparison.OrdinalIgnoreCase) && field.Length > 2)
            {
                var b = field[..^2];
                if (!byName.TryGetValue(b, out var c1)) missing.Add(b); else col = c1;
                if (!byName.TryGetValue(b + "2", out var c2)) missing.Add(b + "2"); else col ??= c2;
            }
            else if (byName.TryGetValue(field, out var c)) col = c;
            else missing.Add(field);

            if (col is not null)
            {
                var type = (f.Attrs.GetValueOrDefault("type") ?? "").Trim();
                var bad = type switch
                {
                    "Decimal" or "Int32" or "Int16" or "Byte" => col.Kind != Kind.Numeric ? "Numeric" : null,
                    "Date" or "DateTime" => col.Kind != Kind.DateTime ? "DateTime" : null,
                    "Boolean" => col.Kind != Kind.Boolean ? "Boolean" : null,
                    _ => col.Kind != Kind.Other ? "String" : null,
                };
                if (bad is not null) badType.Add($"{field} — file khai {(type.Length > 0 ? type : "String")}, cột [{col.Name}] là {col.TypeName}");
            }
            if (missing.Count + badType.Count > before) errorFields++;
        }
        var total = fields.Count;
        if (errorFields == 0)
            return new Report(true, $"Fields matched — {total}/{total} field của {name} đều có trong bảng, đúng kiểu.", 0, total);
        var sb = new StringBuilder();
        sb.Append($"{name}: {errorFields} field lỗi / {total} field (khớp {total - errorFields}/{total}).");
        if (missing.Count > 0) sb.Append($"\r\n\r\nNot matched ({missing.Count}):\r\n{string.Join(", ", missing)}");
        if (badType.Count > 0) sb.Append($"\r\n\r\nSai kiểu ({badType.Count}):\r\n{string.Join("\r\n", badType)}");
        return new Report(false, sb.ToString(), errorFields, total);
    }

    private static readonly Regex DirId = new(@"<dir\b[^>]*?\bid\s*=\s*""(\d+)""", RegexOptions.Compiled);

    /// <summary>File Grid (…\Controllers\Grid\X.xml|.f) → <c>&lt;dir id="N"&gt;</c> của …\Controllers\Filter\X.xml|.f = thứ tự bảng (từ 0) mà
    /// procedure báo cáo trả ra để đổ lên lưới (vd trả 3 bảng 0,1,2, id="1" là bảng thứ 2). null = không phải file Grid / không có Filter / không có id.</summary>
    public static int? FilterTableIndex(string gridPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(gridPath);
            if (dir is null || !string.Equals(Path.GetFileName(dir), "Grid", StringComparison.OrdinalIgnoreCase)) return null;
            var baseName = Path.Combine(Path.GetDirectoryName(dir)!, "Filter", Path.GetFileNameWithoutExtension(gridPath));
            var filter = File.Exists(baseName + ".xml") ? baseName + ".xml" : File.Exists(baseName + ".f") ? baseName + ".f" : null;
            if (filter is null) return null;
            var m = DirId.Match(Regex.Replace(File.ReadAllText(filter), @"<!--.*?-->", "", RegexOptions.Singleline));
            return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
        }
        catch { return null; }
    }

    /// <summary>Cột từ DataTable (chế độ App / bảng còn dữ liệu gốc).</summary>
    public static List<Column> ColumnsOf(System.Data.DataTable table) =>
        table.Columns.Cast<System.Data.DataColumn>().Select(c => new Column(c.ColumnName, DesignFieldsGenerator.KindOf(c.DataType), c.DataType.Name)).ToList();
}
