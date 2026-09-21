using Bcode.App.Models;
using Microsoft.Data.SqlClient;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Backs the "SQL Object" tab: lists tables/views/procedures/functions and
/// fetches an object's definition (view/proc/function body via
/// OBJECT_DEFINITION, or a generated CREATE TABLE for tables). A workspace
/// has two databases (Sys Data / App Data), so every call says which one to
/// search — this is what lets the tab "switch between databases to filter
/// info", same as in FCode.
/// </summary>
public class SqlObjectBrowserService
{
    private readonly DbConnectionService _connections;

    public SqlObjectBrowserService(DbConnectionService connections)
    {
        _connections = connections;
    }

    public async Task<List<SqlObjectInfo>> ListObjectsAsync(bool useSysDatabase, string? nameFilter = null)
    {
        const string sql = @"
SELECT s.name AS SchemaName, o.name AS ObjectName, o.type AS ObjectType
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id

WHERE o.type IN ('U','V','P','FN','IF','TF','TR')
  AND o.is_ms_shipped = 0
  AND (@filter IS NULL OR o.name LIKE @filter)
ORDER BY o.type, s.name, o.name;";

        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@filter", (object?)(string.IsNullOrWhiteSpace(nameFilter) ? null : $"%{nameFilter}%") ?? DBNull.Value);

        var results = new List<SqlObjectInfo>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var type = reader.GetString(2).Trim();
            var kind = type switch
            {
                "U" => SqlObjectKind.Table,
                "V" => SqlObjectKind.View,
                "P" => SqlObjectKind.StoredProcedure,
                "FN" => SqlObjectKind.Function,
                "IF" => SqlObjectKind.Function,
                "TF" => SqlObjectKind.Function,
                "TR" => SqlObjectKind.Trigger,
                _ => SqlObjectKind.Function
            };
            results.Add(new SqlObjectInfo { Schema = reader.GetString(0), Name = reader.GetString(1), Kind = kind, FromSysDatabase = useSysDatabase });
        }
        return results;
    }

    /// <summary>Column names of a table in ordinal order, each flagged whether it's part of
    /// the primary key — backs the "field list, tick to build SELECT" checklist next to
    /// Command (matches FCode showing a table's structure this way). A plain
    /// (string Name, bool IsPrimaryKey) tuple list, not a query result — safe to call for
    /// any table name the user typed, even one that doesn't exist (comes back empty).</summary>
    public async Task<List<(string Name, bool IsPrimaryKey)>> GetColumnsAsync(bool useSysDatabase, string schema, string table)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();

        var pkColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        const string pkSql = @"
SELECT c.name
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(@qualified);";
        await using (var pkCmd = new SqlCommand(pkSql, conn))
        {
            pkCmd.Parameters.AddWithValue("@qualified", $"[{schema}].[{table}]");
            await using var pkReader = await pkCmd.ExecuteReaderAsync();
            while (await pkReader.ReadAsync()) pkColumns.Add(pkReader.GetString(0));
        }

        const string colSql = @"
SELECT COLUMN_NAME
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
ORDER BY ORDINAL_POSITION;";
        var result = new List<(string Name, bool IsPrimaryKey)>();
        await using (var colCmd = new SqlCommand(colSql, conn))
        {
            colCmd.Parameters.AddWithValue("@schema", schema);
            colCmd.Parameters.AddWithValue("@table", table);
            await using var reader = await colCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                result.Add((name, pkColumns.Contains(name)));
            }
        }
        return result;
    }

    public async Task<string> GetDefinitionAsync(SqlObjectInfo obj)
        {
            await using var conn = _connections.CreateConnection(obj.FromSysDatabase);
            await conn.OpenAsync();

            if (obj.Kind == SqlObjectKind.Table)
                return await GenerateCreateTableAsync(conn, obj);

            const string sql = "SELECT OBJECT_DEFINITION(OBJECT_ID(@name));";
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@name", obj.QualifiedName);
            var result = await cmd.ExecuteScalarAsync();
            
            var rawScript = result as string;
            
            if (string.IsNullOrWhiteSpace(rawScript))
                return "-- (Object được tạo WITH ENCRYPTION, hoặc không tìm thấy definition. Xem tool Decrypt SQL Object.)";

            // Tìm chuỗi CREATE đi kèm với PROCEDURE, PROC, FUNCTION, VIEW, hoặc TRIGGER
            // Tham số '1' đảm bảo chỉ thay thế chữ CREATE đầu tiên của cú pháp khai báo, 
            // bảo toàn mọi lệnh CREATE TABLE (bảng tạm) bên trong thân script.
            var regex = new Regex(@"\bCREATE\s+(PROCEDURE|PROC|FUNCTION|VIEW|TRIGGER)\b", RegexOptions.IgnoreCase);
            return regex.Replace(rawScript, "ALTER $1", 1);
        }

    private async Task<string> GenerateCreateTableAsync(SqlConnection conn, SqlObjectInfo table)
    {
        const string sql = @"
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, IS_NULLABLE, NUMERIC_PRECISION, NUMERIC_SCALE
FROM INFORMATION_SCHEMA.COLUMNS
WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table
ORDER BY ORDINAL_POSITION;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@schema", table.Schema);
        cmd.Parameters.AddWithValue("@table", table.Name);

        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var colName = reader.GetString(0);
            var dataType = reader.GetString(1);
            var maxLen = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
            var nullable = reader.GetString(3) == "YES";

            var typeText = maxLen switch
            {
                null => dataType,
                -1 => $"{dataType}(MAX)",
                _ => $"{dataType}({maxLen})"
            };
            lines.Add($"    [{colName}] {typeText} {(nullable ? "NULL" : "NOT NULL")}");
        }
        await reader.DisposeAsync();

        var script = $"CREATE TABLE [{table.Schema}].[{table.Name}] (\n{string.Join(",\n", lines)}\n);";

        var indexScript = await GenerateIndexScriptAsync(conn, table);
        if (!string.IsNullOrEmpty(indexScript))
            script += "\n\n" + indexScript;

        var triggerScript = await GenerateTriggerScriptAsync(conn, table);
        if (!string.IsNullOrEmpty(triggerScript))
            script += "\n\n" + triggerScript;

        return script;
    }

    /// <summary>Rebuilds CREATE INDEX / PRIMARY KEY / UNIQUE statements for a table from
    /// sys.indexes + sys.index_columns (kept as plain per-row reads, not STRING_AGG, so this
    /// still works against older SQL Server versions some FastBusiness sites run on).</summary>
    private async Task<string> GenerateIndexScriptAsync(SqlConnection conn, SqlObjectInfo table)
    {
        const string sql = @"
SELECT i.name AS IndexName, i.is_primary_key, i.is_unique, i.type_desc,
       c.name AS ColumnName, ic.is_descending_key
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(@qualified) AND i.type IN (1, 2)
ORDER BY i.name, ic.key_ordinal;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@qualified", $"[{table.Schema}].[{table.Name}]");

        var byIndex = new List<(string Name, bool IsPk, bool IsUnique, string TypeDesc, List<string> Cols)>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var isPk = reader.GetBoolean(1);
                var isUnique = reader.GetBoolean(2);
                var typeDesc = reader.GetString(3);
                var col = reader.GetString(4) + (reader.GetBoolean(5) ? " DESC" : " ASC");

                var existing = byIndex.FirstOrDefault(x => x.Name == name);
                if (existing.Name is null)
                {
                    existing = (name, isPk, isUnique, typeDesc, new List<string>());
                    byIndex.Add(existing);
                }
                existing.Cols.Add(col);
            }
        }

        if (byIndex.Count == 0) return "";

        var lines = new List<string> { "-- Indexes" };
        foreach (var idx in byIndex)
        {
            var cols = string.Join(", ", idx.Cols);
            if (idx.IsPk)
                lines.Add($"ALTER TABLE [{table.Schema}].[{table.Name}] ADD CONSTRAINT [{idx.Name}] PRIMARY KEY ({idx.TypeDesc.Replace("_", " ")}) ({cols});");
            else if (idx.IsUnique)
                lines.Add($"CREATE UNIQUE {idx.TypeDesc.Replace("_", " ")} INDEX [{idx.Name}] ON [{table.Schema}].[{table.Name}] ({cols});");
            else
                lines.Add($"CREATE {idx.TypeDesc.Replace("_", " ")} INDEX [{idx.Name}] ON [{table.Schema}].[{table.Name}] ({cols});");
        }
        return string.Join("\n", lines);
    }

    /// <summary>Lists every trigger attached to the table with its full body
    /// (via OBJECT_DEFINITION), same as FCode showing triggers alongside a table.</summary>
    private async Task<string> GenerateTriggerScriptAsync(SqlConnection conn, SqlObjectInfo table)
    {
        const string sql = @"
SELECT tr.name, OBJECT_DEFINITION(tr.object_id)
FROM sys.triggers tr
WHERE tr.parent_id = OBJECT_ID(@qualified)
ORDER BY tr.name;";

        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@qualified", $"[{table.Schema}].[{table.Name}]");

        var lines = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            var body = reader.IsDBNull(1) ? "-- (WITH ENCRYPTION, không đọc được body)" : reader.GetString(1);
            lines.Add($"-- Trigger: {name}\n{body.Trim()}");
        }

        return lines.Count == 0 ? "" : "-- Triggers\n" + string.Join("\n\n", lines);
    }
}
