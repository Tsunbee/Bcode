using System.Globalization;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Một cột của bảng command: tên, nhóm kiểu hiển thị (String/Byte/Decimal/DateTime) và có cho NULL không.</summary>
public sealed record CommandColumn(string Name, string Kind, string SqlType, bool Nullable, int MaxLength);

/// <summary>Bảng menu mà <see cref="AppCommandService"/> sửa: tên bảng, cột khoá, cột cha, tiêu đề form, các cột xếp trên đầu form.
/// <see cref="Command"/> = bảng command của sản phẩm APP (hành vi cũ); <see cref="DvCommand"/> = menu mobile (Sys Data, khoá wmenu_id).</summary>
public sealed record MenuTableSpec(string Table, string KeyColumn, string ParentColumn, string Title, string[] TopColumns,
    bool SysOnly, bool ShowReports, bool Translate, bool SuggestKey)
{
    public static readonly MenuTableSpec Command = new("command", "menu_id", "menu_id0", "COMMAND", new[] { "menu_id", "menu_id0", "bar", "bar2" },
        SysOnly: false, ShowReports: true, Translate: false, SuggestKey: false);

    /// <summary>Menu app mobile (FBO MobileService): wmenu_id / wmenu_id0 / menu_id / bar / bar2 / icon_url / icon_color / status / sysid / action / xtype / xgroup.
    /// menu_id mượn của bảng command (nhiều dòng dùng chung) nên chỉ sửa dvcommand, không đụng command.</summary>
    public static readonly MenuTableSpec DvCommand = new("dvcommand", "wmenu_id", "wmenu_id0", "DVCOMMAND", new[] { "wmenu_id", "wmenu_id0", "bar", "bar2" },
        SysOnly: true, ShowReports: false, Translate: true, SuggestKey: true);
}

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

    public AppCommandService(DbConnectionService connections, MenuTableSpec? spec = null)
    {
        _connections = connections;
        Spec = spec ?? MenuTableSpec.Command;
    }

    /// <summary>Bảng đang sửa (mặc định command).</summary>
    public MenuTableSpec Spec { get; }

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
        foreach (var useSys in _useSys is { } known ? new[] { known } : Spec.SysOnly ? new[] { true } : new[] { true, false })
        {
            var conn = _connections.CreateConnection(useSysDatabase: useSys);
            try
            {
                await conn.OpenAsync();
                await using var cmd = new SqlCommand(
                    "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = @t AND COLUMN_NAME IN (@k, @p, 'bar')", conn);
                cmd.Parameters.AddWithValue("@t", Spec.Table);
                cmd.Parameters.AddWithValue("@k", Spec.KeyColumn);
                cmd.Parameters.AddWithValue("@p", Spec.ParentColumn);
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync()) >= 3)
                {
                    _useSys = useSys;
                    return (conn, useSys);
                }
            }
            catch (SqlException) { }
            await conn.DisposeAsync();
        }
        throw new InvalidOperationException($"Không tìm thấy bảng {Spec.Table} ({Spec.KeyColumn}, {Spec.ParentColumn}, bar) ở " +
            (Spec.SysOnly ? "Sys Data" : "Sys Data / App Data") + " của workspace hiện tại.");
    }

    private string Tbl => "dbo.[" + Spec.Table + "]";
    private string Key => "[" + Spec.KeyColumn + "]";

    public async Task<IReadOnlyList<CommandColumn>> GetColumnsAsync()
    {
        if (_columns is not null) return _columns;
        var (conn, _) = await OpenCommandDbAsync();
        await using (conn)
        {
            var list = new List<CommandColumn>();
            await using var cmd = new SqlCommand(
                "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, ISNULL(CHARACTER_MAXIMUM_LENGTH, 0) FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_NAME = @t AND TABLE_SCHEMA = 'dbo' ORDER BY ORDINAL_POSITION", conn);
            cmd.Parameters.AddWithValue("@t", Spec.Table);
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
            await using var cmd = new SqlCommand($"SELECT * FROM {Tbl} WHERE {Key} = @id", conn);
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
            await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM {Tbl} WHERE {Key} = @id", conn);
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
                await using var del = new SqlCommand($"DELETE FROM {Tbl} WHERE {Key} = @id", conn, tx);
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
            ins.CommandText = $"INSERT INTO {Tbl} ({string.Join(", ", names.Select(n => "[" + n + "]"))}) " +
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
            await using var cmd = new SqlCommand($"DELETE FROM {Tbl} WHERE {Key} = @id", conn);
            cmd.Parameters.AddWithValue("@id", menuId);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ------------------------------------------------------------ cây menu (dvcommand)

    /// <summary>Mọi dòng của bảng thành <see cref="Models.WCommandItem"/> phẳng (cho cây menu): khoá → WMenuId, cột cha → WMenuId0.
    /// Bảng không có ở database này → danh sách rỗng.</summary>
    public async Task<List<Models.WCommandItem>> LoadMenuItemsAsync()
    {
        Reset();   // đổi workspace: database / cột có thể khác
        SqlConnection conn;
        try { (conn, _) = await OpenCommandDbAsync(); }
        catch (InvalidOperationException) { return new(); }
        await using (conn)
        {
            await using var cmd = new SqlCommand($"SELECT * FROM {Tbl}", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            var names = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
            string Get(string col) { var i = names.FindIndex(n => n.Equals(col, StringComparison.OrdinalIgnoreCase)); return i < 0 || r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)?.Trim() ?? ""; }
            var list = new List<Models.WCommandItem>();
            while (await r.ReadAsync())
            {
                var id = Get(Spec.KeyColumn);
                if (id.Length == 0) continue;
                list.Add(new Models.WCommandItem
                {
                    WMenuId = id, WMenuId0 = Get(Spec.ParentColumn), MenuId = Get("menu_id"), Bar = Get("bar"), Bar2 = Get("bar2"),
                    IconUrl = Get("icon_url"), Status = Get("status"), SysId = Get("sysid"), XType = Get("xtype"), Type = Get("action"),
                });
            }
            return list;
        }
    }

    /// <summary>Gợi ý khoá chưa dùng cho menu mới dưới <paramref name="parentId"/> (dạng GG.SS.LL): đoạn cuối = lớn nhất trong các menu anh em + bước
    /// (5 nếu các menu anh em đều chia hết cho 5 như dvcommand chuẩn, không thì 1). Không có cha → nhóm gốc kế tiếp. Trả "" nếu không gợi ý được.</summary>
    public async Task<string> SuggestNextKeyAsync(string? parentId)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var siblings = new List<string>();
        var parent = (parentId ?? "").Trim();
        var (conn, _) = await OpenCommandDbAsync();
        await using (conn)
        {
            await using var cmd = new SqlCommand($"SELECT {Key}, [{Spec.ParentColumn}] FROM {Tbl}", conn);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var id = (r.IsDBNull(0) ? "" : Convert.ToString(r.GetValue(0)) ?? "").Trim();
                if (id.Length == 0) continue;
                used.Add(id);
                if ((r.IsDBNull(1) ? "" : Convert.ToString(r.GetValue(1)) ?? "").Trim().Equals(parent, StringComparison.OrdinalIgnoreCase)) siblings.Add(id);
            }
        }
        static int[]? Parts(string id) { var p = id.Split('.'); return p.Length == 3 && p.All(x => x.Length == 2 && x.All(char.IsDigit)) ? p.Select(int.Parse).ToArray() : null; }
        if (parent.Length == 0)
        {
            var roots = siblings.Select(Parts).Where(p => p is not null).ToList();
            var g = roots.Count == 0 ? 10 : roots.Max(p => p![0]) + 10;
            for (; g <= 99; g++) if (!used.Contains($"{g:D2}.00.00")) return $"{g:D2}.00.00";
            return "";
        }
        var pp = Parts(parent);
        if (pp is null) return "";
        var kids = siblings.Select(Parts).Where(p => p is not null && p[0] == pp[0] && p[1] == pp[1]).Select(p => p![2]).ToList();
        var step = kids.Count > 0 && kids.All(n => n % 5 == 0) ? 5 : kids.Count == 0 ? 5 : 1;
        for (var n = (kids.Count == 0 ? 0 : kids.Max()) + step; n <= 99; n += step)
        {
            var c = $"{pp[0]:D2}.{pp[1]:D2}.{n:D2}";
            if (!used.Contains(c)) return c;
        }
        return "";
    }

    /// <summary>Script DELETE + INSERT của 1 dòng (đủ mọi cột của bảng ở database này) — Gen Script Menu của cây mobile.</summary>
    public async Task<string> GenerateScriptAsync(string id)
    {
        var cols = await GetColumnsAsync();
        var row = await LoadRowAsync(id) ?? throw new InvalidOperationException($"Không thấy dòng {Spec.KeyColumn} = '{id}' trong {Spec.Table}.");
        string Lit(CommandColumn c)
        {
            row.TryGetValue(c.Name, out var v);
            if (v is null) return "NULL";
            return c.Kind is "Byte" or "Decimal" ? v : "N'" + v.Replace("'", "''") + "'";
        }
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"DELETE {Spec.Table} WHERE {Spec.KeyColumn} = '{id.Replace("'", "''")}'");
        sb.AppendLine("GO");
        sb.AppendLine($"INSERT INTO {Spec.Table}({string.Join(", ", cols.Select(c => c.Name))})");
        sb.AppendLine($"VALUES({string.Join(", ", cols.Select(Lit))})");
        sb.AppendLine("GO");
        return sb.ToString();
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
