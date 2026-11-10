using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Cột khoá chính của bảng nguồn cho 1 bảng kết quả SQL Query — "Generate Design Fields" thêm <c>isPrimaryKey="true"</c> (DesignFieldsGenerator).
/// <list type="bullet">
/// <item>Lúc chạy (RawSqlService.RunBatchesAsync): batch ra ĐÚNG 1 bảng kết quả → <see cref="Tag"/> gắn câu SQL của batch + 1 bản sao kết nối
/// (chưa mở, giữ cả mật khẩu) + database lúc bắt đầu batch vào <c>DataTable.ExtendedProperties["src"]</c>. Batch ra nhiều bảng (nhiều SELECT,
/// EXEC procedure) → không gắn → sinh như cũ.</item>
/// <item>Khi bấm Generate (<see cref="PrimaryKeyColumnsAsync"/>): bảng/cột gốc của từng cột kết quả lấy bằng
/// <c>sys.dm_exec_describe_first_result_set(batch, NULL, 1)</c> (SQL 2012+, CHỈ biên dịch, không chạy câu lệnh); server cũ (SQL 2008, chưa có hàm
/// này) và batch là SELECT thuần → <c>CommandBehavior.SchemaOnly | KeyInfo</c> (SET FMTONLY ON: chỉ trả metadata, không đọc/sửa dòng nào).
/// Mọi cột có nguồn cùng thuộc 1 bảng (user table, cùng database) → lấy khoá chính bảng đó (sys.indexes is_primary_key) → đánh dấu các cột kết quả
/// lấy thẳng từ cột khoá (khoá nhiều cột thì đủ các cột có mặt). Join nhiều bảng, view, #temp, lỗi biên dịch, mất kết nối… → null (như cũ).</item>
/// </list>
/// </summary>
public static class ResultSourceKeys
{
    private const string Key = "src";

    private sealed record Source(string Batch, SqlConnection Template, string Database);
    private readonly record struct Origin(string? Db, string? Schema, string? Table, string? Column);

    /// <summary>Gắn nguồn cho bảng kết quả duy nhất của 1 batch. <paramref name="database"/>: database của kết nối lúc bắt đầu batch.</summary>
    public static void Tag(DataTable table, string batch, SqlConnection conn, string database)
    {
        try { table.ExtendedProperties[Key] = new Source(batch, (SqlConnection)((ICloneable)conn).Clone(), database); }
        catch { /* không sao chép được kết nối → không đánh dấu khoá, như cũ */ }
    }

    /// <summary>Tên cột (đúng tên cột của <paramref name="table"/>) là khoá chính của bảng nguồn; null = không xác định được bảng nguồn.</summary>
    public static async Task<HashSet<string>?> PrimaryKeyColumnsAsync(DataTable table, CancellationToken ct = default)
    {
        if (table.ExtendedProperties[Key] is not Source src) return null;
        try
        {
            await using var conn = (SqlConnection)((ICloneable)src.Template).Clone();
            await conn.OpenAsync(ct);
            if (src.Database.Length > 0 && !string.Equals(conn.Database, src.Database, StringComparison.OrdinalIgnoreCase))
                await conn.ChangeDatabaseAsync(src.Database, ct);

            var cols = await DescribeAsync(conn, src.Batch, ct);
            if (cols is null || cols.Count != table.Columns.Count) return null;

            var sources = cols.Where(c => !string.IsNullOrEmpty(c.Table))
                .Select(c => (Db: c.Db ?? "", Schema: string.IsNullOrEmpty(c.Schema) ? "dbo" : c.Schema, Table: c.Table!))
                .Distinct(TupleComparer.Instance).ToList();
            if (sources.Count != 1) return null;   // join nhiều bảng / không cột nào lấy thẳng từ bảng
            var (db, schema, name) = sources[0];
            if (db.Length > 0 && !string.Equals(db, conn.Database, StringComparison.OrdinalIgnoreCase)) return null;

            // Khoá chính của bảng đó (OBJECT_ID kiểu 'U': view / hàm → không có).
            var pk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new SqlCommand(@"
SELECT c.name
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.is_primary_key = 1 AND i.object_id = OBJECT_ID(@qualified, 'U');", conn) { CommandTimeout = 15 })
            {
                cmd.Parameters.AddWithValue("@qualified", $"[{schema.Replace("]", "]]")}].[{name.Replace("]", "]]")}]");
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) pk.Add(r.GetString(0));
            }

            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < cols.Count; i++)
                if (!string.IsNullOrEmpty(cols[i].Table) && cols[i].Column is { } c && pk.Contains(c)) result.Add(table.Columns[i].ColumnName);
            return result;
        }
        catch { return null; }   // mất kết nối, hết giờ, không có quyền… → như cũ
    }

    /// <summary>Nguồn của từng cột hiện (bỏ cột ẩn mà chế độ browse tự thêm), theo thứ tự cột; null = không mô tả được.</summary>
    private static async Task<List<Origin>?> DescribeAsync(SqlConnection conn, string batch, CancellationToken ct)
    {
        var cols = new List<Origin>();
        try
        {
            await using var cmd = new SqlCommand(@"
SELECT error_number, is_hidden, source_database, source_schema, source_table, source_column
FROM sys.dm_exec_describe_first_result_set(@tsql, NULL, 1)
ORDER BY column_ordinal;", conn) { CommandTimeout = 15 };
            cmd.Parameters.Add("@tsql", SqlDbType.NVarChar, -1).Value = batch;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!r.IsDBNull(0)) return null;   // lỗi biên dịch (#temp, câu động…) → không xác định được
                if (!r.IsDBNull(1) && r.GetBoolean(1)) continue;
                string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
                cols.Add(new(S(2), S(3), S(4), S(5)));
            }
            return cols;
        }
        catch (SqlException ex) when (ex.Number == 208)   // SQL 2008: chưa có sys.dm_exec_describe_first_result_set
        {
            return IsPlainSelect(batch) ? await DescribeLegacyAsync(conn, batch, ct) : null;
        }
    }

    /// <summary>SQL 2008: SchemaOnly (SET FMTONLY ON — không chạy, chỉ trả metadata) + KeyInfo (browse mode → BaseTableName/BaseColumnName).</summary>
    private static async Task<List<Origin>?> DescribeLegacyAsync(SqlConnection conn, string batch, CancellationToken ct)
    {
        await using var cmd = new SqlCommand(batch, conn) { CommandTimeout = 15 };
        await using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SchemaOnly | CommandBehavior.KeyInfo, ct);
        if (r.GetSchemaTable() is not { } st) return null;
        string? S(DataRow row, string col) => st.Columns.Contains(col) && row[col] is string s && s.Length > 0 ? s : null;
        var cols = new List<Origin>();
        foreach (DataRow row in st.Rows)
        {
            if (st.Columns.Contains("IsHidden") && row["IsHidden"] is true) continue;
            cols.Add(new(S(row, "BaseCatalogName"), S(row, "BaseSchemaName"), S(row, "BaseTableName"), S(row, "BaseColumnName")));
        }
        return cols;
    }

    private static readonly Regex Literals = new(@"--[^\r\n]*|/\*[\s\S]*?\*/|N?'(?:[^']|'')*'|""[^""]*""|\[[^\]]*\]", RegexOptions.Compiled);
    private static readonly Regex NotSelect = new(@"\b(INSERT|UPDATE|DELETE|MERGE|EXEC|EXECUTE|DROP|CREATE|ALTER|TRUNCATE|GRANT|REVOKE|DENY|BACKUP|RESTORE|DBCC|INTO|SET|DECLARE|USE|WAITFOR|KILL|SHUTDOWN|RECONFIGURE|OPENROWSET|OPENQUERY|OPENDATASOURCE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Chỉ đúng 1 câu SELECT (hoặc WITH … SELECT), không lệnh nào khác — điều kiện để dùng đường SchemaOnly cho server cũ.</summary>
    internal static bool IsPlainSelect(string batch)
    {
        var s = Literals.Replace(batch, " ").Trim().TrimEnd(';').Trim();
        return Regex.IsMatch(s, @"^(SELECT|WITH)\b", RegexOptions.IgnoreCase) && !s.Contains(';') && !NotSelect.IsMatch(s);
    }

    private sealed class TupleComparer : IEqualityComparer<(string, string, string)>
    {
        public static readonly TupleComparer Instance = new();
        private static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;
        public bool Equals((string, string, string) a, (string, string, string) b) => C.Equals(a.Item1, b.Item1) && C.Equals(a.Item2, b.Item2) && C.Equals(a.Item3, b.Item3);
        public int GetHashCode((string, string, string) t) => HashCode.Combine(C.GetHashCode(t.Item1), C.GetHashCode(t.Item2), C.GetHashCode(t.Item3));
    }
}
