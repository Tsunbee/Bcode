using System.Data;
using System.Globalization;

namespace Bcode.App.Services.Rpt;

/// <summary>Kết quả nhận diện báo cáo pivot trong các bảng kết quả của 1 lần chạy SQL.</summary>
public record PivotPlan(int TableIndex, string RowKey, string ColKey, string? ColHeader, List<string> Rows, List<string> Data,
    Dictionary<string, string> Headers, string Source, string? Controller);

/// <summary>Payload gửi cho trang Pivot: bảng dữ liệu (dạng chuỗi, tối đa <see cref="MaxRows"/> dòng) + kế hoạch pivot.</summary>
public record PivotPayload(List<RptColumn> Columns, List<string?[]> Rows, int TotalRows, PivotPlan Plan);

/// <summary>
/// Khi nào 1 procedure "pivot được" theo quy ước Fast (rút ra từ các mẫu): bảng dữ liệu dạng DÀI có cặp cột khoá dòng/cột
/// (<c>xRow</c>+<c>xColumn</c>, <c>sysRow</c>+<c>sysColumn</c>), thường kèm cột tiêu đề cột (<c>xHeader</c>, <c>sysColumnName</c>) và 1 bảng định nghĩa
/// (<c>id, name, header</c>, name dạng <c>so_luong$1</c>). Nếu Grid controller của báo cáo có khai <c>&lt;pivot rowField columnField dataFields&gt;</c>
/// thì lấy theo đó (chính xác nhất), không thì đoán theo tên cột.
/// </summary>
public static class PivotDetector
{
    public const int MaxRows = 5000;
    private static readonly (string Row, string Col)[] KeyPairs = { ("xRow", "xColumn"), ("sysRow", "sysColumn"), ("KeyRow", "KeyCol") };
    private static readonly string[] SystemCols = { "sysorder", "sysprint", "systotal", "stt", "line_nbr", "stt_rec", "stt_rec0" };

    public static PivotPayload? Detect(IReadOnlyList<DataTable> tables, ControllerInfo? info)
    {
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < tables.Count; i++)
            {
                var t = tables[i];
                string? rk = null, ck = null; var source = "";
                if (pass == 0 && info?.Pivot is { } p && p.RowField.Length > 0 && p.ColumnField.Length > 0
                    && Find(t, p.RowField) is { } r0 && Find(t, p.ColumnField) is { } c0)
                { rk = r0; ck = c0; source = $"cấu hình <pivot> của controller {info.Controller}"; }
                else if (pass == 1)
                    foreach (var (r, c) in KeyPairs)
                        if (Find(t, r) is { } r1 && Find(t, c) is { } c1) { rk = r1; ck = c1; source = "đoán theo tên cột khoá (" + r1 + " / " + c1 + ")"; break; }
                if (rk is null || ck is null) continue;
                return Build(tables, i, rk, ck, info, source);
            }
        return null;
    }

    private static PivotPayload Build(IReadOnlyList<DataTable> tables, int idx, string rk, string ck, ControllerInfo? info, string source)
    {
        var t = tables[idx];
        var header = new[] { ck + "Name", "xHeader", "sysColumnName", "xColumnName", "header" }.Select(n => Find(t, n)).FirstOrDefault(n => n is not null && n != rk && n != ck);

        // field giá trị: controller → bảng định nghĩa (name = <field>$N) → cột số còn lại
        var data = new List<string>();
        if (info?.Pivot is { DataFields.Count: > 0 } pv)
        {
            var df = pv.DataFields.Select(d => Find(t, d)).Where(d => d is not null).Select(d => d!).ToList();
            data = df.Where(d => !(d.EndsWith("_nt", StringComparison.OrdinalIgnoreCase) && df.Any(x => x.Equals(d[..^3], StringComparison.OrdinalIgnoreCase)))).ToList();
        }
        if (data.Count == 0)
            foreach (var other in tables.Where(x => x != t))
            {
                var nameCol = Find(other, "name");
                if (nameCol is null || Find(other, "id") is null) continue;
                data = other.Rows.Cast<DataRow>().Select(r => Convert.ToString(r[nameCol]) ?? "").Where(s => s.Contains('$')).Select(s => s[..s.IndexOf('$')])
                    .Distinct(StringComparer.OrdinalIgnoreCase).Select(d => Find(t, d)).Where(d => d is not null).Select(d => d!).ToList();
                if (data.Count > 0) break;
            }
        var keys = new HashSet<string>(new[] { rk, ck, header }.Where(s => s is not null)!, StringComparer.OrdinalIgnoreCase);
        if (data.Count == 0)
            data = t.Columns.Cast<DataColumn>().Where(c => IsNumeric(c.DataType) && !keys.Contains(c.ColumnName) && !IsSystem(c.ColumnName)).Select(c => c.ColumnName).Take(1).ToList();

        // field dòng hiển thị: view Grid của controller; không có thì các cột chữ/ngày còn lại (bỏ cột hệ thống, khoá, giá trị)
        var skip = new HashSet<string>(keys.Concat(data), StringComparer.OrdinalIgnoreCase);
        var rows = (info?.ViewFields ?? new()).Select(v => Find(t, v.Replace("%l", ""))).Where(c => c is not null && !skip.Contains(c!)).Select(c => c!).ToList();
        if (rows.Count == 0)
            rows = t.Columns.Cast<DataColumn>().Where(c => !skip.Contains(c.ColumnName) && !IsSystem(c.ColumnName) && !IsNumeric(c.DataType)).Select(c => c.ColumnName).Take(8).ToList();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in info?.GridFields ?? new())
            if (!string.IsNullOrWhiteSpace(g.HeaderV)) headers[g.Name.Replace("%l", "")] = g.HeaderV;

        var cols = t.Columns.Cast<DataColumn>().Select(c => new RptColumn(c.ColumnName, IsNumeric(c.DataType), (Nullable.GetUnderlyingType(c.DataType) ?? c.DataType) == typeof(DateTime))).ToList();
        var sample = t.Rows.Cast<DataRow>().Take(MaxRows).Select(r => r.ItemArray.Select(Cell).ToArray()).ToList();
        return new PivotPayload(cols, sample, t.Rows.Count, new PivotPlan(idx + 1, rk, ck, header, rows, data, headers, source, info?.Controller));
    }

    private static string? Find(DataTable t, string name) =>
        t.Columns.Cast<DataColumn>().FirstOrDefault(c => c.ColumnName.Equals(name, StringComparison.OrdinalIgnoreCase))?.ColumnName;

    private static bool IsSystem(string n) => n.StartsWith("sys", StringComparison.OrdinalIgnoreCase) || SystemCols.Contains(n, StringComparer.OrdinalIgnoreCase);

    private static bool IsNumeric(Type t) => Type.GetTypeCode(Nullable.GetUnderlyingType(t) ?? t) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64
        or TypeCode.Decimal or TypeCode.Double or TypeCode.Single;

    private static string? Cell(object? v) => v switch
    {
        null or DBNull => null,
        DateTime d => d.ToString("yyyy-MM-dd"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        byte[] => "(binary)",
        var o => o.ToString() is { } s && s.Length > 80 ? s[..80] : o.ToString(),
    };
}
