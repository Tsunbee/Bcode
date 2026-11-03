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

    /// <summary>
    /// Như <see cref="BuildUnionSubquery"/> nhưng cột theo đúng bảng mẫu <c>{baseName}$000000</c> (thứ tự CREATE TABLE, như
    /// <c>SELECT * FROM {baseName}$000000</c> bên SQL Query): mỗi bảng kỳ SELECT theo TÊN cột của bảng mẫu, cột bảng kỳ thiếu
    /// = <c>CAST(NULL AS kiểu)</c>, cột bảng kỳ thừa bỏ đi. <c>SELECT *</c> ghép UNION ALL theo VỊ TRÍ cột nên bảng kỳ lệch thứ tự
    /// thì tên cột lấy theo bảng kỳ đầu, dữ liệu lệch cột; bảng mẫu có cột mà các bảng kỳ chưa có thì lưới thiếu cột đó.
    /// Mọi bảng kỳ đã giống hệt bảng mẫu (hoặc không có bảng mẫu) → câu <c>SELECT *</c> cũ.
    /// </summary>
    public async Task<string> BuildAlignedUnionSubqueryAsync(SqlConnection conn, string schema, string baseName,
        IEnumerable<PeriodTableInfo> periodTables, string alias)
    {
        var tables = periodTables.ToList();
        if (tables.Count == 0) return BuildUnionSubquery(tables, alias);

        const string sql = @"
SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @schema AND TABLE_NAME LIKE @pattern ESCAPE '\'
ORDER BY TABLE_NAME, ORDINAL_POSITION;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        var escapedBase = baseName.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").Replace("[", "\\[");
        cmd.Parameters.AddWithValue("@pattern", $"{escapedBase}$[0-9][0-9][0-9][0-9][0-9][0-9]");

        var columns = new Dictionary<string, List<(string Name, string Type)>>(StringComparer.OrdinalIgnoreCase);
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync())
            {
                var t = r.GetString(0);
                if (!columns.TryGetValue(t, out var cols)) columns[t] = cols = new();
                int? maxLen = r.IsDBNull(3) ? null : r.GetInt32(3);
                int? precision = r.IsDBNull(4) ? null : Convert.ToInt32(r.GetValue(4));
                int? scale = r.IsDBNull(5) ? null : Convert.ToInt32(r.GetValue(5));
                cols.Add((r.GetString(1), CastType(r.GetString(2), maxLen, precision, scale)));
            }

        if (!columns.TryGetValue($"{baseName}$000000", out var template) || template.Count == 0)
            return BuildUnionSubquery(tables, alias);

        bool SameAsTemplate(PeriodTableInfo t) =>
            columns.TryGetValue(t.TableName, out var cols)
            && cols.Select(c => c.Name).SequenceEqual(template.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        if (tables.All(SameAsTemplate)) return BuildUnionSubquery(tables, alias);

        static string Q(string name) => "[" + name.Replace("]", "]]") + "]";
        var parts = tables.Select(t =>
        {
            var have = columns.TryGetValue(t.TableName, out var cols)
                ? new HashSet<string>(cols.Select(c => c.Name), StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = template.Select(c => have.Contains(c.Name) ? Q(c.Name) : $"CAST(NULL AS {c.Type}) AS {Q(c.Name)}");
            return $"SELECT {string.Join(", ", list)} FROM {t.QualifiedName}";
        });
        var union = string.Join("\n    UNION ALL\n    ", parts);
        return $"(\n    {union}\n) AS [{alias}]";
    }

    /// <summary>Kiểu cho <c>CAST(NULL AS ...)</c> từ INFORMATION_SCHEMA.COLUMNS (cột bảng kỳ còn thiếu so với bảng mẫu).</summary>
    private static string CastType(string dataType, int? maxLen, int? precision, int? scale) => dataType.ToLowerInvariant() switch
    {
        "char" or "varchar" or "nchar" or "nvarchar" or "binary" or "varbinary" =>
            maxLen is -1 ? $"{dataType}(max)" : $"{dataType}({maxLen})",
        "decimal" or "numeric" => $"{dataType}({precision},{scale})",
        "timestamp" or "rowversion" => "binary(8)",
        _ => dataType
    };
}
