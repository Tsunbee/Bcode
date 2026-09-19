using System.Data;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Backs the "Table" tool — FCode's direct, Excel-like edit-a-table view (as
/// opposed to "SQL Query"/"Command" where you write SELECT/SQL yourself).
/// Loads a table's rows, tracks primary-key columns so edits can be written
/// back, and applies DataTable changes (added/modified/deleted rows) as real
/// UPDATE/INSERT/DELETE statements.
/// </summary>
public class TableDataService
{
    private readonly DbConnectionService _connections;
    private readonly PeriodTableQueryService _periods;

    public TableDataService(DbConnectionService connections, PeriodTableQueryService periods)
    {
        _connections = connections;
        _periods = periods;
    }

    /// <summary>True when schema.table is a "...$000000" period placeholder (see
    /// PeriodTableQueryService) — TableEditControl uses this to block Save, since a
    /// UNION-ALL-over-every-period result isn't one physical table to write back to.</summary>
    public bool IsPeriodPlaceholder(string schema, string table) => _periods.IsPeriodPlaceholder($"{schema}.{table}");

    /// <summary>
    /// Fix: "Table" was querying "$000000" literally (the near-empty template table),
    /// always showing 0 rows, instead of the "$000000 = every period, UNION ALL'd
    /// together" behavior that SQL Query/Command already had (PeriodTableQueryService).
    /// Resolves the FROM source accordingly; a non-placeholder name passes through as
    /// a normal "[schema].[table]" reference.
    /// </summary>
    private async Task<string> ResolveTableSourceAsync(SqlConnection conn, string schema, string table)
    {
        if (!_periods.IsPeriodPlaceholder($"{schema}.{table}"))
            return $"[{schema}].[{table}]";

        var (resolvedSchema, baseName) = _periods.ParsePlaceholder($"{schema}.{table}");
        var periodTables = await _periods.DiscoverPeriodTablesAsync(conn, resolvedSchema, baseName);
        return _periods.BuildUnionSubquery(periodTables, $"{baseName}$000000");
    }

    /// <summary>Primary-key column names for a table, in key order (empty if the table has no PK —
    /// editing is still allowed but the caller must ask the user which column(s) to key on).
    /// Opens and manages its own short-lived connection (same pattern as LoadTableAsync).</summary>
    public async Task<List<string>> GetPrimaryKeyColumnsAsync(bool useSysDatabase, string schema, string table)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();
        return await GetPrimaryKeyColumnsAsync(conn, schema, table);
    }

    /// <summary>Same as above but reuses an already-open connection (e.g. inside SaveChangesAsync's transaction).</summary>
    public async Task<List<string>> GetPrimaryKeyColumnsAsync(SqlConnection conn, string schema, string table)
    {
        const string sql = @"
SELECT c.name
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(@qualified)
ORDER BY ic.key_ordinal;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@qualified", $"[{schema}].[{table}]");

        var keys = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) keys.Add(reader.GetString(0));
        return keys;
    }

    /// <param name="topN">Row cap for safety on large transaction tables — 500 by default
    /// (matching the Top box's own prefilled value), but 0 or negative means no cap at all:
    /// "Table" is meant for directly viewing/editing a table's own data (a customer catalog,
    /// a reference list, ...), and those are often smaller, complete lists the user
    /// genuinely wants to see/edit in full, not just a transaction-table-sized preview — so
    /// unlike Command/SQL Query's own hard 500-row cap (SqlQueryService.MaxRows), this one
    /// stays a suggestion the user can clear.</param>
    public async Task<DataTable> LoadTableAsync(bool useSysDatabase, string schema, string table, int topN = 500)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();

        var source = await ResolveTableSourceAsync(conn, schema, table);
        var topClause = topN > 0 ? $"TOP {topN} " : "";
        var sql = $"SELECT {topClause}* FROM {source};";
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await using var reader = await cmd.ExecuteReaderAsync();

        var result = new DataTable(table);
        result.Load(reader);
        return result;
    }

    /// <summary>
    /// Writes every added/modified/deleted row of <paramref name="table"/> (as tracked by
    /// DataTable's own row-state) back to the real table, keyed on <paramref name="keyColumns"/>.
    /// Returns the number of statements executed.
    /// </summary>
    public async Task<int> SaveChangesAsync(bool useSysDatabase, string schema, string table, List<string> keyColumns, DataTable data)
    {
        if (keyColumns.Count == 0)
            throw new InvalidOperationException("Bảng này không có Primary Key — chọn cột khoá thủ công trước khi lưu (mục Change Key Columns).");

        var changes = data.GetChanges();
        if (changes is null) return 0;

        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        var executed = 0;
        try
        {
            foreach (DataRow row in changes.Rows)
            {
                switch (row.RowState)
                {
                    case DataRowState.Added:
                        await ExecuteInsertAsync(conn, tx, schema, table, row);
                        executed++;
                        break;
                    case DataRowState.Modified:
                        await ExecuteUpdateAsync(conn, tx, schema, table, keyColumns, row);
                        executed++;
                        break;
                    case DataRowState.Deleted:
                        await ExecuteDeleteAsync(conn, tx, schema, table, keyColumns, row);
                        executed++;
                        break;
                }
            }
            await tx.CommitAsync();
            data.AcceptChanges();
            return executed;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    private static async Task ExecuteInsertAsync(SqlConnection conn, SqlTransaction tx, string schema, string table, DataRow row)
    {
        var columns = row.Table.Columns.Cast<DataColumn>().ToList();
        var cmd = new SqlCommand { Connection = conn, Transaction = tx };
        var colList = string.Join(", ", columns.Select(c => $"[{c.ColumnName}]"));
        var paramList = string.Join(", ", columns.Select((c, i) => $"@p{i}"));
        cmd.CommandText = $"INSERT INTO [{schema}].[{table}] ({colList}) VALUES ({paramList});";
        for (var i = 0; i < columns.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", row[columns[i], DataRowVersion.Current] ?? DBNull.Value);
        await using (cmd) await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteUpdateAsync(SqlConnection conn, SqlTransaction tx, string schema, string table, List<string> keyColumns, DataRow row)
    {
        var columns = row.Table.Columns.Cast<DataColumn>()
            .Where(c => !keyColumns.Contains(c.ColumnName, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var cmd = new SqlCommand { Connection = conn, Transaction = tx };
        var setClause = string.Join(", ", columns.Select((c, i) => $"[{c.ColumnName}] = @s{i}"));
        var whereClause = string.Join(" AND ", keyColumns.Select((k, i) => $"[{k}] = @k{i}"));
        cmd.CommandText = $"UPDATE [{schema}].[{table}] SET {setClause} WHERE {whereClause};";
        for (var i = 0; i < columns.Count; i++)
            cmd.Parameters.AddWithValue($"@s{i}", row[columns[i], DataRowVersion.Current] ?? DBNull.Value);
        for (var i = 0; i < keyColumns.Count; i++)
            cmd.Parameters.AddWithValue($"@k{i}", row[keyColumns[i], DataRowVersion.Original] ?? DBNull.Value);
        await using (cmd) await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteDeleteAsync(SqlConnection conn, SqlTransaction tx, string schema, string table, List<string> keyColumns, DataRow row)
    {
        var cmd = new SqlCommand { Connection = conn, Transaction = tx };
        var whereClause = string.Join(" AND ", keyColumns.Select((k, i) => $"[{k}] = @k{i}"));
        cmd.CommandText = $"DELETE FROM [{schema}].[{table}] WHERE {whereClause};";
        for (var i = 0; i < keyColumns.Count; i++)
            cmd.Parameters.AddWithValue($"@k{i}", row[keyColumns[i], DataRowVersion.Original] ?? DBNull.Value);
        await using (cmd) await cmd.ExecuteNonQueryAsync();
    }
}
