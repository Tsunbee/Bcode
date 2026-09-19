using System.Data;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Backs the "Lookup" tool — a quick "find the row(s) matching this key value
/// in this table" search, simpler/faster than opening "Table" or writing a
/// full "SQL Query"/"Command": pick a table + column, type a value, get
/// matching rows back immediately (e.g. "which customer has code KH0001").
/// </summary>
public class LookupService
{
    private readonly DbConnectionService _connections;

    public LookupService(DbConnectionService connections)
    {
        _connections = connections;
    }

    public async Task<DataTable> SearchAsync(bool useSysDatabase, string schema, string table, string column, string value, bool exactMatch)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();

        var op = exactMatch ? "=" : "LIKE";
        var sql = $"SELECT TOP 200 * FROM [{schema}].[{table}] WHERE [{column}] {op} @value ORDER BY [{column}];";
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        cmd.Parameters.AddWithValue("@value", exactMatch ? value : $"%{value}%");

        await using var reader = await cmd.ExecuteReaderAsync();
        var result = new DataTable(table);
        result.Load(reader);
        return result;
    }

    public async Task<List<string>> GetColumnsAsync(bool useSysDatabase, string schema, string table)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();

        const string sql = @"SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table ORDER BY ORDINAL_POSITION;";
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", schema);
        cmd.Parameters.AddWithValue("@table", table);

        var cols = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) cols.Add(reader.GetString(0));
        return cols;
    }
}
