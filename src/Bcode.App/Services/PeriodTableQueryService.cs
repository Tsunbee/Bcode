using System.Text.RegularExpressions;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Reproduces the FCode "$000000" convenience: FastBusiness-style transaction
/// tables are split per accounting period, e.g. a base table "m21$000000"
/// physically becomes "m21$202601", "m21$202602", ... "m21$202612" (one per
/// month/period, naming convention is site-specific — this looks for any
/// suffix after "$" on the base name and does not assume YYYYMM specifically).
///
/// Referencing "m21$000000" in a query is a request to see ALL periods without
/// hand-writing a UNION ALL over every period table — this service discovers
/// the sibling tables and builds that UNION ALL automatically.
/// </summary>
public class PeriodTableQueryService
{
    private static readonly Regex BasePattern = new(@"^\[?(?<schema>[\w]+)\]?\.\[?(?<name>[\w]+)\]?\$000000$|^\[?(?<name2>[\w]+)\]?\$000000$", RegexOptions.IgnoreCase);

    /// <summary>True when the given (unqualified or schema-qualified) table name ends in "$000000".</summary>
    public bool IsPeriodPlaceholder(string tableRef) => BasePattern.IsMatch(tableRef.Trim());

    /// <summary>
    /// Extracts the base name (without "$000000") and optional schema from a table reference.
    /// </summary>
    public (string schema, string baseName) ParsePlaceholder(string tableRef)
    {
        var m = BasePattern.Match(tableRef.Trim());
        if (!m.Success) throw new ArgumentException($"'{tableRef}' không phải dạng bảng phân kỳ ...$000000");

        var name = m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["name2"].Value;
        var schema = m.Groups["schema"].Success ? m.Groups["schema"].Value : "dbo";
        return (schema, name);
    }

    /// <summary>
    /// Queries sys.tables for every physical table matching "{baseName}$&lt;period&gt;"
    /// in the given schema, excluding the "$000000" template table itself.
    /// </summary>
    public async Task<List<PeriodTableInfo>> DiscoverPeriodTablesAsync(SqlConnection conn, string schema, string baseName, bool includeBaseTemplate = false)
    {
        const string sql = @"
SELECT s.name AS SchemaName, t.name AS TableName
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE s.name = @schema
  AND t.name LIKE @pattern ESCAPE '\'
ORDER BY t.name;";

        var results = new List<PeriodTableInfo>();
        PeriodTableInfo? baseTable = null; // chính bảng "base$000000" nếu nó tồn tại
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        // escape any existing wildcard/escape chars in baseName, then match "base$" + one-or-more chars
        var escapedBase = baseName.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        cmd.Parameters.AddWithValue("@pattern", $"{escapedBase}$[0-9][0-9][0-9][0-9][0-9][0-9]");

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var tableName = reader.GetString(1);
            var period = tableName.Substring(tableName.LastIndexOf('$') + 1);
            var info = new PeriodTableInfo
            {
                SchemaName = reader.GetString(0),
                TableName = tableName,
                Period = period
            };
            if (period == "000000")
            {
                baseTable = info;
                if (!includeBaseTemplate) continue;
            }
            results.Add(info);
        }

        // Bảng KHÔNG phân kỳ: chỉ có "base$000000", không có "base$YYYYMM" nào. Trước đây báo lỗi
        // "không tìm thấy bảng kỳ"; giờ chỉ truy vấn đúng bảng đó (không cần UNION). Khi CÓ bảng kỳ
        // thì "$000000" vẫn bị loại như cũ (nó là bảng mẫu, trộn vào sẽ dư dữ liệu).
        if (results.Count == 0 && baseTable != null) results.Add(baseTable);

        return results;
    }

    /// <summary>
    /// Builds "(SELECT * FROM [schema].[base$202601] UNION ALL ... ) AS alias"
    /// for use in place of the "base$000000" placeholder in a FROM clause.
    /// </summary>
    public string BuildUnionSubquery(IEnumerable<PeriodTableInfo> periodTables, string alias)
    {
        var tables = periodTables.ToList();
        if (tables.Count == 0)
            throw new InvalidOperationException("Không tìm thấy bảng ...$000000 hay bảng kỳ nào khớp mẫu trong database hiện tại.");

        var parts = tables.Select(t => $"SELECT * FROM {t.QualifiedName}");
        var union = string.Join("\n    UNION ALL\n    ", parts);
        return $"(\n    {union}\n) AS [{alias}]";
    }
}
