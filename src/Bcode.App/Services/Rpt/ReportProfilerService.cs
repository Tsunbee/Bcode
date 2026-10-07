using System.Data;
using System.Text.RegularExpressions;

namespace Bcode.App.Services.Rpt;

/// <summary>Chạy câu lệnh profiler của báo cáo (declare @0.. ; select ... ; exec proc ...) trên App Data của WS đang chọn và trả
/// cấu trúc từng result set: d (tham số, !1.), d1 (dữ liệu, !2.)... Chỉ đọc cấu trúc cột + số dòng; không giữ dữ liệu.</summary>
public class ReportProfilerService
{
    private static readonly Regex ExecRx = new(@"\bexec(?:ute)?\s+(?:\[?dbo\]?\.)?\[?([A-Za-z_][\w$]*)\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    public const int SampleRows = 200;
    private readonly RawSqlService _sql;

    public ReportProfilerService(RawSqlService sql) => _sql = sql;

    /// <summary>Tên proc ở lệnh exec CUỐI của script (thường trùng tên trang Main / controller, vd zrpt_O017).</summary>
    public static string GuessName(string sql)
    {
        var ms = ExecRx.Matches(sql ?? "");
        return ms.Count == 0 ? "" : ms[^1].Groups[1].Value;
    }

    public async Task<List<RptTable>> ProfileAsync(string sql)
    {
        var batches = await _sql.ExecuteScriptAsync(sql, useSysDatabase: false);
        var err = batches.FirstOrDefault(b => b.Error is not null)?.Error;
        if (err is not null) throw new InvalidOperationException(err);

        var tables = new List<RptTable>();
        foreach (var dt in batches.SelectMany(b => b.Tables).Where(t => t.Columns.Count > 0))
        {
            var idx = tables.Count + 1; // !1. = bảng đầu (d), !2. = bảng thứ hai (d1)...
            tables.Add(new RptTable(idx == 1 ? "d" : "d" + (idx - 1), idx,
                dt.Columns.Cast<DataColumn>().Select(c => new RptColumn(c.ColumnName, IsNumeric(c.DataType), IsDate(c.DataType))).ToList(), dt.Rows.Count, Sample(dt)));
        }
        return tables;
    }

    private static List<string?[]> Sample(DataTable dt) =>
        dt.Rows.Cast<DataRow>().Take(SampleRows).Select(r => r.ItemArray.Select(Cell).ToArray()).ToList();

    private static string? Cell(object? v) => v switch
    {
        null or DBNull => null,
        DateTime d => d.ToString("yyyy-MM-dd"),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        byte[] => "(binary)",
        var o => o.ToString() is { } s && s.Length > 60 ? s[..60] : o.ToString(),
    };

    private static bool IsDate(Type t) => (Nullable.GetUnderlyingType(t) ?? t) == typeof(DateTime) || (Nullable.GetUnderlyingType(t) ?? t) == typeof(DateTimeOffset);

    private static bool IsNumeric(Type t) => Type.GetTypeCode(Nullable.GetUnderlyingType(t) ?? t) is
        TypeCode.Byte or TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 or TypeCode.UInt16 or TypeCode.UInt32 or TypeCode.UInt64
        or TypeCode.Decimal or TypeCode.Double or TypeCode.Single;
}
