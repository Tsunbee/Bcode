using Bcode.App.Models;
using Microsoft.Data.SqlClient;
using System.Text;
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

    public DbConnectionService Connections => _connections;

    public SqlObjectBrowserService(DbConnectionService connections)
    {
        _connections = connections;
    }

    /// <param name="hidePeriodTables">true: bỏ các bảng phân kỳ dạng "m21$202601" (tên kết thúc bằng $ + 6 chữ số khác 000000), chỉ giữ bảng gốc "m21$000000" và bảng thường — cây SQL Object dùng để đỡ lag.</param>
    public async Task<List<SqlObjectInfo>> ListObjectsAsync(bool useSysDatabase, string? nameFilter = null, bool hidePeriodTables = false)
    {
        const string sql = @"
SELECT s.name AS SchemaName, o.name AS ObjectName, o.type AS ObjectType
FROM sys.objects o
JOIN sys.schemas s ON s.schema_id = o.schema_id

WHERE o.type IN ('U','V','P','FN','IF','TF','TR')
  AND o.is_ms_shipped = 0
  AND (@filter IS NULL OR o.name LIKE @filter)
  AND (@hidePeriod = 0 OR NOT (o.type = 'U' AND o.name LIKE '%$[0-9][0-9][0-9][0-9][0-9][0-9]' AND o.name NOT LIKE '%$000000'))
ORDER BY o.type, s.name, o.name;";

        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@filter", (object?)(string.IsNullOrWhiteSpace(nameFilter) ? null : $"%{nameFilter}%") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hidePeriod", hidePeriodTables ? 1 : 0);

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

    // ---- Cache danh sách object (cây SQL Object) --------------------------------------------------------------------
    // Danh sách được lưu ra đĩa theo (server, database). Lần sau hiện ngay từ cache, rồi chỉ hỏi 1 câu rất nhẹ ("chữ ký" = số object + ngày
    // sửa/tạo mới nhất): khớp thì giữ cache, KHÁC (có procedure/function/view/bảng mới hoặc vừa sửa/xoá) mới nạp lại cả danh sách.

    /// <summary>Chữ ký trạng thái các object (đã bỏ bảng phân kỳ — tháng mới sinh thêm bảng kỳ thì không tính là "có thay đổi").</summary>
    public async Task<string> GetSignatureAsync(bool useSysDatabase)
    {
        const string sql = @"
SELECT COUNT_BIG(*), ISNULL(CONVERT(varchar(30), MAX(o.modify_date), 126), ''), ISNULL(CONVERT(varchar(30), MAX(o.create_date), 126), '')
FROM sys.objects o
WHERE o.type IN ('U','V','P','FN','IF','TF','TR') AND o.is_ms_shipped = 0
  AND NOT (o.type = 'U' AND o.name LIKE '%$[0-9][0-9][0-9][0-9][0-9][0-9]' AND o.name NOT LIKE '%$000000');";
        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
        await using var r = await cmd.ExecuteReaderAsync();
        await r.ReadAsync();
        return $"{r.GetInt64(0)}|{r.GetString(1)}|{r.GetString(2)}";
    }

    public sealed record ObjectCache(string Signature, List<SqlObjectInfo> Items);

    private sealed record CacheRow(string S, string N, int K);
    private sealed record CacheFile(string Signature, List<CacheRow> Rows);

    private string CachePath(bool useSys)
    {
        var ws = _connections.Current ?? throw new InvalidOperationException("Chưa chọn Workspace (WS).");
        var db = useSys ? ws.SysDatabase : ws.EffectiveAppDatabase;
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes((ws.Server + "|" + db).ToLowerInvariant())))[..16];
        return Path.Combine(BcodePaths.AppData, "Bcode", "sql-objects", key + ".json");
    }

    public ObjectCache? LoadCache(bool useSys)
    {
        try
        {
            var path = CachePath(useSys);
            if (!File.Exists(path)) return null;
            var file = System.Text.Json.JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(path));
            if (file is null) return null;
            return new ObjectCache(file.Signature, file.Rows.Select(r => new SqlObjectInfo
            {
                Schema = r.S, Name = r.N, Kind = (SqlObjectKind)r.K, FromSysDatabase = useSys,
            }).ToList());
        }
        catch (InvalidOperationException) { throw; } // chưa chọn workspace
        catch { return null; /* cache hỏng — coi như chưa có */ }
    }

    public void SaveCache(bool useSys, string signature, List<SqlObjectInfo> items)
    {
        try
        {
            var path = CachePath(useSys);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new CacheFile(signature, items.Select(o => new CacheRow(o.Schema, o.Name, (int)o.Kind)).ToList());
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(file));
        }
        catch { /* không lưu được cache thì lần sau nạp lại */ }
    }

    /// <summary>Trong số <paramref name="names"/>, những table có thật trong database (schema bất kỳ) và số dòng ước tính (từ sys.partitions — không đếm thật nên rất nhanh).</summary>
    public async Task<Dictionary<string, long>> GetTableRowCountsAsync(bool useSysDatabase, IReadOnlyCollection<string> names)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return result;
        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();
        // Chia lô để không vượt giới hạn tham số.
        foreach (var batch in names.Chunk(500))
        {
            var ps = batch.Select((_, i) => "@p" + i).ToList();
            var sql = "SELECT t.name, SUM(CASE WHEN p.index_id IN (0,1) THEN p.rows ELSE 0 END) FROM sys.tables t " +
                      "LEFT JOIN sys.partitions p ON p.object_id = t.object_id WHERE t.name IN (" + string.Join(",", ps) + ") GROUP BY t.name";
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            for (var i = 0; i < batch.Length; i++) cmd.Parameters.AddWithValue(ps[i], batch[i]);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) result[r.GetString(0)] = r.IsDBNull(1) ? 0 : Convert.ToInt64(r.GetValue(1));
        }
        return result;
    }

    /// <summary>Trong số <paramref name="names"/>, những tên là VIEW có thật trong database (schema bất kỳ).</summary>
    public async Task<HashSet<string>> GetViewNamesAsync(bool useSysDatabase, IReadOnlyCollection<string> names)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (names.Count == 0) return result;
        await using var conn = _connections.CreateConnection(useSysDatabase);
        await conn.OpenAsync();
        foreach (var batch in names.Chunk(500))
        {
            var ps = batch.Select((_, i) => "@p" + i).ToList();
            await using var cmd = new SqlCommand("SELECT name FROM sys.views WHERE name IN (" + string.Join(",", ps) + ")", conn) { CommandTimeout = 30 };
            for (var i = 0; i < batch.Length; i++) cmd.Parameters.AddWithValue(ps[i], batch[i]);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) result.Add(r.GetString(0));
        }
        return result;
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

    // ---- Bản lưu (cache) định nghĩa procedure / function / view / trigger -------------------------------------------
    // Mở object: hiện NGAY bản đã lưu trên máy, rồi hỏi database 1 câu rất nhẹ (sys.objects.modify_date); đổi mới tải lại. Bảng KHÔNG cache (script bảng gồm cả
    // index / trigger, ngày sửa của bảng không chắc phản ánh hết) — luôn lấy mới. Object mã hoá không cache.

    public sealed record DefinitionCache(string Text, long ModifyTicks);

    private static bool Cacheable(SqlObjectInfo o) => o.Kind != SqlObjectKind.Table;

    private string DefCachePath(SqlObjectInfo o)
    {
        var stamp = Regex.Replace(_connections.CurrentStamp(o.FromSysDatabase), @"[^\w.\-]+", "_");
        if (stamp.Length > 120) stamp = stamp[^120..];
        var file = Regex.Replace(o.QualifiedName, @"[^\w.$\-]+", "_");
        return Path.Combine(BcodePaths.AppData, "Bcode", "def-cache", stamp, file + ".sql");
    }

    public DefinitionCache? TryGetCachedDefinition(SqlObjectInfo o)
    {
        if (!Cacheable(o)) return null;
        try
        {
            var path = DefCachePath(o);
            if (!File.Exists(path) || !File.Exists(path + ".meta") || !long.TryParse(File.ReadAllText(path + ".meta").Trim(), out var ticks)) return null;
            return new DefinitionCache(File.ReadAllText(path, System.Text.Encoding.UTF8), ticks);
        }
        catch { return null; }
    }

    private void SaveDefinitionCache(SqlObjectInfo o, string text, long ticks)
    {
        if (!Cacheable(o) || text.StartsWith("-- (Object được tạo WITH ENCRYPTION", StringComparison.Ordinal)) return;
        try
        {
            var path = DefCachePath(o);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text, System.Text.Encoding.UTF8);
            File.WriteAllText(path + ".meta", ticks.ToString());
        }
        catch { /* không lưu được thì lần sau lấy từ database */ }
    }

    private async Task<long?> GetModifyTicksAsync(SqlObjectInfo o)
    {
        await using var conn = _connections.CreateConnection(o.FromSysDatabase);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT modify_date FROM sys.objects WHERE object_id = OBJECT_ID(@n);", conn);
        cmd.Parameters.AddWithValue("@n", o.QualifiedName);
        return await cmd.ExecuteScalarAsync() is DateTime d ? d.Ticks : null;
    }

    /// <summary>Lấy định nghĩa từ database và lưu bản cache (dùng khi chưa có bản lưu).</summary>
    public async Task<string> GetDefinitionAndCacheAsync(SqlObjectInfo o)
    {
        var ticks = Cacheable(o) ? await GetModifyTicksAsync(o) : null;
        var text = await GetDefinitionAsync(o);
        if (ticks is { } t) SaveDefinitionCache(o, text, t);
        return text;
    }

    /// <summary>Hỏi database xem object đã đổi so với bản lưu chưa; đổi thì tải bản mới, lưu lại và trả về.</summary>
    public async Task<(bool Changed, string Text)> RefreshIfChangedAsync(SqlObjectInfo o, DefinitionCache cached)
    {
        var ticks = await GetModifyTicksAsync(o);
        if (ticks is null || ticks == cached.ModifyTicks) return (false, cached.Text);
        var text = await GetDefinitionAsync(o);
        SaveDefinitionCache(o, text, ticks.Value);
        return (text != cached.Text, text);
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

    /// <summary>Cấu trúc bảng để xem nhanh (F12 trong SQL Query), dạng như FCode: CREATE TABLE đủ cột + "/* Index */" (tên: [cột…]) + "/* List columns */" (tên cột cách nhau dấu phẩy, xuống dòng theo chiều rộng).</summary>
    public async Task<string> GetTableStructureAsync(SqlObjectInfo table)
    {
        await using var conn = _connections.CreateConnection(table.FromSysDatabase);
        await conn.OpenAsync();
        var full = "[" + table.Schema + "].[" + table.Name + "]";
        var cols = new List<string>(); var colDecl = new List<string>();
        await using (var cmd = new SqlCommand(@"
SELECT c.name, t.name AS tname, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity
FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(@full) ORDER BY c.column_id;", conn))
        {
            cmd.Parameters.AddWithValue("@full", full);
            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                var name = rd.GetString(0); var tn = rd.GetString(1).ToUpperInvariant(); int ml = rd.GetInt16(2), pr = rd.GetByte(3), sc = rd.GetByte(4);
                var type = tn switch
                {
                    "VARCHAR" or "CHAR" or "VARBINARY" or "BINARY" => tn + "(" + (ml == -1 ? "MAX" : ml.ToString()) + ")",
                    "NVARCHAR" or "NCHAR" => tn + "(" + (ml == -1 ? "MAX" : (ml / 2).ToString()) + ")",
                    "NUMERIC" or "DECIMAL" => tn + "(" + pr + ", " + sc + ")",
                    _ => tn,
                };
                cols.Add(name);
                colDecl.Add("\t" + name + " " + type + (rd.GetBoolean(6) ? " IDENTITY" : "") + (rd.GetBoolean(5) ? "" : " NOT NULL"));
            }
        }
        if (cols.Count == 0) return "-- Không thấy bảng " + full + ".";

        var idx = new List<string>();
        await using (var cmd = new SqlCommand(@"
SELECT i.name, i.is_primary_key, i.is_unique, i.type_desc, c.name, ic.is_descending_key
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(@full) AND i.type > 0 AND i.is_hypothetical = 0
ORDER BY i.index_id, ic.key_ordinal;", conn))
        {
            cmd.Parameters.AddWithValue("@full", full);
            await using var rd = await cmd.ExecuteReaderAsync();
            var order = new List<string>(); var map = new Dictionary<string, (string Flags, List<string> Cols)>();
            while (await rd.ReadAsync())
            {
                var iname = rd.IsDBNull(0) ? "(heap)" : rd.GetString(0);
                if (!map.TryGetValue(iname, out var e))
                {
                    var flags = new List<string>();
                    if (rd.GetBoolean(1)) flags.Add("PRIMARY KEY"); else if (rd.GetBoolean(2)) flags.Add("UNIQUE");
                    if (rd.GetString(3).StartsWith("CLUSTERED", StringComparison.OrdinalIgnoreCase)) flags.Add("CLUSTERED");
                    map[iname] = e = (string.Join(", ", flags), new List<string>()); order.Add(iname);
                }
                e.Cols.Add(rd.GetString(4) + (rd.GetBoolean(5) ? " DESC" : ""));
            }
            foreach (var n in order) idx.Add(n + ": [" + string.Join(", ", map[n].Cols) + "]" + (map[n].Flags.Length > 0 ? "   -- " + map[n].Flags : ""));
        }

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(full).Append(" (\n").Append(string.Join(",\n", colDecl)).Append("\n)\n\n");
        sb.Append("/* Index\n").Append(idx.Count > 0 ? string.Join("\n", idx) : "(không có index)").Append("\n*/\n\n");
        sb.Append("/* List columns\n");
        var line = new StringBuilder();
        for (var i = 0; i < cols.Count; i++)
        {
            var piece = (i == 0 ? "" : ", ") + cols[i];
            if (line.Length > 0 && line.Length + piece.Length > 110) { sb.Append(line).Append('\n'); line.Clear(); piece = ", " + cols[i]; }
            line.Append(piece);
        }
        sb.Append(line).Append("\n*/");
        return sb.ToString();
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
                lines.Add($"ALTER TABLE [{table.Schema}].[{table.Name}] ADD CONSTRAINT [{idx.Name}] PRIMARY KEY {idx.TypeDesc.Replace("_", " ")} ({cols});");
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
