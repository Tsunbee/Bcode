using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

public class SchemaCompareResult
{
    public List<SchemaColumn> OnlyInLeft { get; } = new();
    public List<SchemaColumn> OnlyInRight { get; } = new();
    public List<(SchemaColumn left, SchemaColumn right)> Changed { get; } = new();
    public List<SchemaColumn> Unchanged { get; } = new();
}

/// <summary>
/// "Compare Structure" tool: diffs the column list of two tables (typically
/// the same table name across two workspaces/databases, e.g. to check a
/// customer's DB against a reference/master schema before an update).
/// </summary>
public class SchemaCompareService
{
    public async Task<List<SchemaColumn>> GetColumnsAsync(SqlConnection conn, string schema, string tableName)
    {
        const string sql = @"
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, ORDINAL_POSITION
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
ORDER BY ORDINAL_POSITION;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", tableName);

        var columns = new List<SchemaColumn>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(new SchemaColumn
            {
                ColumnName = reader.GetString(0),
                DataType = reader.GetString(1),
                MaxLength = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                IsNullable = reader.GetString(3) == "YES",
                OrdinalPosition = reader.GetInt32(4)
            });
        }
        return columns;
    }

    public SchemaCompareResult Compare(List<SchemaColumn> left, List<SchemaColumn> right)
    {
        var result = new SchemaCompareResult();
        var leftMap = left.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        var rightMap = right.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        foreach (var lc in left)
        {
            if (!rightMap.TryGetValue(lc.ColumnName, out var rc))
            {
                result.OnlyInLeft.Add(lc);
            }
            else if (lc.Signature != rc.Signature)
            {
                result.Changed.Add((lc, rc));
            }
            else
            {
                result.Unchanged.Add(lc);
            }
        }

        foreach (var rc in right)
        {
            if (!leftMap.ContainsKey(rc.ColumnName))
                result.OnlyInRight.Add(rc);
        }

        return result;
    }
}
