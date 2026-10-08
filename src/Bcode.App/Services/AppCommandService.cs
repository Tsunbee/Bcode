using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Một cột của bảng command: tên, nhóm kiểu hiển thị (String/Byte/Decimal/DateTime) và có cho NULL không.</summary>
public sealed record CommandColumn(string Name, string Kind, string SqlType, bool Nullable, int MaxLength);

/// <summary>
/// CRUD tổng quát cho bảng <c>command</c> của sản phẩm dạng APP (FBFF...) — không có wcommand, form "COMMAND - Edit"
/// của FCode liệt kê mọi cột. Cột đọc động từ INFORMATION_SCHEMA nên sản phẩm nào thiếu/thừa cột cũng chạy.
/// Bảng nằm ở Sys Data hoặc App Data tuỳ sản phẩm: thử Sys trước (giống WCommandService.LoadAppCommandAsync).
/// Ngoài ra đọc bảng <c>reports</c> (Sys Data) lọc theo sysid để hiện mẫu in / khai báo màn hình báo cáo.
/// </summary>
public sealed class AppCommandService
{
    private readonly DbConnectionService _connections;
    private bool? _useSys;
    private List<CommandColumn>? _columns;

    public AppCommandService(DbConnectionService connections) => _connections = connections;

    /// <summary>Tên chương trình từ cột exe: bỏ mọi thứ sau khoảng trắng (kể cả tab/NBSP) và đuôi .exe — "zinctpnh.exe PNH" → "zinctpnh".</summary>
    public static string ExeStem(string? exe)
    {
        var token = (exe ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return Path.GetFileNameWithoutExtension(token.Trim());
    }

    /// <summary>{Source Path}\{stem} nếu có (chỉ kiểm đúng đường dẫn đó, không quét cây qua UNC).</summary>
    public static string? FindSourceFolder(string sourceRoot, string stem)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || stem.Length == 0) return null;
        try { var p = Path.Combine(sourceRoot, stem); return Directory.Exists(p) ? p : null; }
        catch { return null; }
    }


    /// <summary>Xoá cache DB/cột (khi đổi workspace hoặc cấu trúc bảng đổi).</summary>
    public void Reset() { _useSys = null; _columns = null; }

    private async Task<(SqlConnection Conn, bool UseSys)> OpenCommandDbAsync()
    {
        foreach (var useSys in _useSys is { } known ? new[] { known } : new[] { true, false })
        {
            var conn = _connections.CreateConnection(useSysDatabase: useSys);
            try
            {
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'command' AND COLUMN_NAME IN ('menu_id','menu_id0','bar')", conn);
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) >= 3)
                {
                    _useSys = useSys;
                    return (conn, useSys);
                }
            }
            catch (SqlException) { }
            await conn.DisposeAsync();
        }
        throw new InvalidOperationException("Không tìm thấy bảng command (menu_id, menu_id0, bar) ở Sys Data / App Data của workspace hiện tại.");
    }

    public async Task<IReadOnlyList<CommandColumn>> GetColumnsAsync()
    {
        if (_columns is not null) return _columns;
        var (conn, _) = await OpenCommandDbAsync();
        await using (conn)
        {
            var list = new List<CommandColumn>();
            await using var cmd = new SqlCommand(
                "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, ISNULL(CHARACTER_MAXIMUM_LENGTH, 0) FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_NAME = 'command' AND TABLE_SCHEMA = 'dbo' ORDER BY ORDINAL_POSITION", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var type = r.GetString(1).ToLowerInvariant();
                var kind = type switch
                {
                    "tinyint" or "smallint" or "int" or "bigint" or "bit" => "Byte",
                    "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "Decimal",
                    "datetime" or "smalldatetime" or "date" or "datetime2" => "DateTime",
                    _ => "String",
                };
                list.Add(new CommandColumn(r.GetString(0), kind, type, r.GetString(2) == "YES", r.GetInt32(3)));
            }
            return _columns = list;
        }
    }

    /// <summary>Đọc 1 dòng command theo menu_id (tên cột → chuỗi; NULL → null). Không có → null.</summary>
    public async Task<Dictionary<string, string?>?> LoadRowAsync(string menuId)
    {
        var cols = await GetColumnsAsync();
        var (conn, _) = await OpenCommandDbAsync();
        await using (conn)
        {
            await using var cmd = new SqlCommand("SELECT * FROM dbo.command WHERE menu_id = @id", conn);
            cmd.Parameters.AddWithValue("@id", menuId);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync()) return null;
            var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in cols)
            {
                var ord = r.GetOrdinal(c.Name);
                if (r.IsDBNull(ord)) { row[c.Name] = null; continue; }
                var v = r.GetValue(ord);
                row[c.Name] = v switch
                {
                    DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                    IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                    _ => v.ToString()?.TrimEnd(),
                };
            }
            return row;
        }
    }

    public async Task<bool> ExistsAsync(string menuId)
    {
        var (conn, _) = await OpenCommandDbAsync();
        await using (conn)
        {
            await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.command WHERE menu_id = @id", conn);
            cmd.Parameters.AddWithValue("@id", menuId);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
        }
    }

    /// <summary>Lưu: Edit (originalId != null) = xoá dòng cũ + chèn dòng mới trong 1 transaction; New = chèn.
    /// Chuỗi rỗng ở cột không phải String (và cột String cho phép NULL mà người dùng gõ "NULL") → NULL.</summary>
    public async Task SaveAsync(string? originalId, IReadOnlyDictionary<string, string?> values)
    {
        var cols = await GetColumnsAsync();
        var (conn, _) = await OpenCommandDbAsync();
        await using (conn)
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            if (!string.IsNullOrEmpty(originalId))
            {
                await using var del = new SqlCommand("DELETE FROM dbo.command WHERE menu_id = @id", conn, tx);
                del.Parameters.AddWithValue("@id", originalId);
                await del.ExecuteNonQueryAsync();
            }
            var names = new List<string>();
            await using var ins = new SqlCommand { Connection = conn, Transaction = tx };
            foreach (var c in cols)
            {
                values.TryGetValue(c.Name, out var raw);
                names.Add(c.Name);
                ins.Parameters.AddWithValue("@p" + (names.Count - 1), ToDbValue(c, raw));
            }
            ins.CommandText = $"INSERT INTO dbo.command ({string.Join(", ", names.Select(n => "[" + n + "]"))}) " +
                              $"VALUES ({string.Join(", ", names.Select((_, i) => "@p" + i))})";
            await ins.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
    }

    public async Task DeleteAsync(string menuId)
    {
        var (conn, _) = await OpenCommandDbAsync();
        await using (conn)
        {
            await using var cmd = new SqlCommand("DELETE FROM dbo.command WHERE menu_id = @id", conn);
            cmd.Parameters.AddWithValue("@id", menuId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private static object ToDbValue(CommandColumn c, string? raw)
    {
        var s = raw ?? "";
        switch (c.Kind)
        {
            case "String":
                if (raw is null) return c.Nullable ? DBNull.Value : "";
                return s;
            case "Byte":
                if (s.Trim().Length == 0 || s.Trim().Equals("NULL", StringComparison.OrdinalIgnoreCase))
                    return c.Nullable ? DBNull.Value : 0;
                if (!long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
                    throw new InvalidOperationException($"Cột {c.Name} phải là số nguyên.");
                return c.SqlType switch { "tinyint" => (byte)n, "smallint" => (short)n, "int" => (int)n, "bit" => n != 0, _ => n };
            case "Decimal":
                if (s.Trim().Length == 0 || s.Trim().Equals("NULL", StringComparison.OrdinalIgnoreCase))
                    return c.Nullable ? DBNull.Value : 0m;
                if (!decimal.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    throw new InvalidOperationException($"Cột {c.Name} phải là số.");
                return d;
            default: // DateTime
                if (s.Trim().Length == 0 || s.Trim().Equals("NULL", StringComparison.OrdinalIgnoreCase))
                    return c.Nullable ? DBNull.Value : new DateTime(1900, 1, 1);
                if (!DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) &&
                    !DateTime.TryParse(s.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out dt))
                    throw new InvalidOperationException($"Cột {c.Name} phải là ngày giờ (yyyy-MM-dd HH:mm:ss) hoặc NULL.");
                return dt;
        }
    }

    // ------------------------------------------------------------ reports
    /// <summary>Tên file báo cáo/mẫu in liên quan tới một menu APP: command.rep_file/rep_form + reports.rep_file/rep_id của sysid.</summary>
    public async Task<List<string>> GetReportNamesAsync(string menuId, string sysId)
    {
        var names = new List<string>();
        try
        {
            if (await LoadRowAsync(menuId) is { } row)
                foreach (var k in new[] { "rep_file", "rep_form" })
                    if (row.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) names.Add(v!);
        }
        catch { }
        try
        {
            var r = await LoadReportsAsync(sysId);
            foreach (var k in new[] { "rep_file", "rep_id" })
            {
                var i = r.Columns.FindIndex(c => c.Equals(k, StringComparison.OrdinalIgnoreCase));
                if (i < 0) continue;
                foreach (var row in r.Rows) if (!string.IsNullOrWhiteSpace(row[i])) names.Add(row[i]);
            }
        }
        catch { }
        return names;
    }


    public sealed record ReportsResult(List<string> Columns, List<List<string>> Rows, string Note);

    /// <summary>Các dòng bảng <c>reports</c> (Sys Data trước, App Data sau) có form = sysid (không có thì rep_id = sysid).</summary>
    public async Task<ReportsResult> LoadReportsAsync(string sysId)
    {
        sysId = (sysId ?? "").Trim();
        if (sysId.Length == 0) return new ReportsResult(new(), new(), "Menu chưa có Sysid.");
        foreach (var useSys in new[] { true, false })
        {
            try
            {
                await using var conn = _connections.CreateConnection(useSysDatabase: useSys);
                await conn.OpenAsync();
                foreach (var col in new[] { "form", "rep_id" })
                {
                    await using var cmd = new SqlCommand($"SELECT * FROM dbo.reports WHERE [{col}] = @s", conn);
                    cmd.Parameters.AddWithValue("@s", sysId);
                    await using var r = await cmd.ExecuteReaderAsync();
                    var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
                    var rows = new List<List<string>>();
                    while (await r.ReadAsync())
                    {
                        var row = new List<string>(cols.Count);
                        for (var i = 0; i < cols.Count; i++)
                            row.Add(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)?.TrimEnd() ?? "");
                        rows.Add(row);
                    }
                    if (rows.Count > 0) return new ReportsResult(cols, rows, $"{rows.Count} dòng reports ({(useSys ? "Sys" : "App")} Data, {col} = {sysId})");
                }
            }
            catch (SqlException) { /* bảng reports không có ở DB này — thử DB còn lại */ }
        }
        return new ReportsResult(new(), new(), $"Không có dòng reports nào cho '{sysId}'.");
    }
}
