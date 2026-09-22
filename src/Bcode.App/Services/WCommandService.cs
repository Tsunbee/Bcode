using System.Globalization;
using System.Text;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Loads the menu tree from the wcommand table and assembles it into a
/// parent/child hierarchy for display in a TreeView (left panel of the
/// WCommand tab in FCode).
/// </summary>
public class WCommandService
{
    private readonly DbConnectionService _connections;

    public WCommandService(DbConnectionService connections)
    {
        _connections = connections;
    }

    public async Task<List<WCommandItem>> LoadTreeAsync(string? filterLike = null)
    {
        // wcommand (menu tree) lives in Sys Data, not App Data.
        await using var conn = _connections.CreateConnection(useSysDatabase: true);
        await conn.OpenAsync();

        // SELECT * để lấy toàn bộ cột sẵn có của bảng wcommand
        var sql = "SELECT * FROM dbo.wcommand";
        if (!string.IsNullOrWhiteSpace(filterLike))
            sql += " WHERE bar LIKE @f OR bar2 LIKE @f OR link LIKE @f";

        await using var cmd = new SqlCommand(sql, conn);
        if (!string.IsNullOrWhiteSpace(filterLike))
            cmd.Parameters.AddWithValue("@f", "%" + filterLike.Trim() + "%");

        var all = new List<WCommandItem>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                all.Add(ReadFullRow(reader));
        }

        return BuildHierarchy(all);
    }

    public async Task<string> SuggestNextWMenuIdAsync(string? parentWMenuId)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase: true);
        await conn.OpenAsync();

        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand("SELECT wmenu_id FROM wcommand", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var id = SafeGet(reader, "wmenu_id")?.Trim();
                if (!string.IsNullOrEmpty(id)) all.Add(id);
            }
        }

        var parent = (parentWMenuId ?? "").Trim();
        var group = parent.Length > 0 ? parent.Split('.')[0] : "";

        if (string.IsNullOrEmpty(group))
        {
            for (var g = 1; g <= 99; g++)
            {
                var candidate = $"{g:D2}.00.00";
                if (!all.Contains(candidate)) return candidate;
            }
            return "99.99.99";
        }

        var maxLeaf = 0;
        foreach (var id in all)
        {
            var parts = id.Split('.');
            if (parts.Length != 3 || parts[0] != group) continue;
            if (parts[1] == "00") continue;
            if (int.TryParse(parts[1], out var n) && n > maxLeaf) maxLeaf = n;
        }

        for (var n = maxLeaf + 1; n <= 99; n++)
        {
            var candidate = $"{group}.{n:D2}.01";
            if (!all.Contains(candidate)) return candidate;
        }

        return $"{group}.99.99";
    }

    private static List<WCommandItem> BuildHierarchy(List<WCommandItem> all)
    {
        var byId = all.Where(i => !string.IsNullOrEmpty(i.WMenuId))
                       .GroupBy(i => i.WMenuId)
                       .ToDictionary(g => g.Key, g => g.First());

        var roots = new List<WCommandItem>();

        foreach (var item in all)
        {
            var parentId = !string.IsNullOrWhiteSpace(item.WMenuId0)
                ? item.WMenuId0
                : InferParentId(item.WMenuId);

            if (!string.IsNullOrEmpty(parentId) && byId.TryGetValue(parentId, out var parent) && parent != item)
            {
                parent.Children.Add(item);
            }
            else
            {
                roots.Add(item);
            }
        }

        return roots.OrderBy(r => r.WMenuId).ToList();
    }

    private static string InferParentId(string wmenuId)
    {
        var segments = wmenuId.Split('.');
        if (segments.Length < 3) return "";
        if (segments[1] == "00" && segments[2] == "00") return "";
        return $"{segments[0]}.00.00";
    }

    public async Task SaveAsync(WCommandItem item, string? originalWMenuId, string? originalMenuId)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase: true);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        try
        {
            var deleteWMenuId = originalWMenuId ?? item.WMenuId;
            var deleteMenuId = originalMenuId ?? item.MenuId;

            await using (var delWc = new SqlCommand("DELETE wcommand WHERE wmenu_id = @id", conn, tx))
            {
                delWc.Parameters.AddWithValue("@id", deleteWMenuId);
                await delWc.ExecuteNonQueryAsync();
            }
            await using (var delCmd = new SqlCommand("DELETE command WHERE menu_id = @id", conn, tx))
            {
                delCmd.Parameters.AddWithValue("@id", deleteMenuId);
                await delCmd.ExecuteNonQueryAsync();
            }

            await using (var insWc = new SqlCommand(InsertWCommandSql, conn, tx))
            {
                AddWCommandParameters(insWc, item);
                await insWc.ExecuteNonQueryAsync();
            }
            await using (var insCmd = new SqlCommand(InsertCommandSql, conn, tx))
            {
                AddCommandParameters(insCmd, item);
                await insCmd.ExecuteNonQueryAsync();
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task DeleteAsync(WCommandItem item)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase: true);
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        try
        {
            await using (var delWc = new SqlCommand("DELETE wcommand WHERE wmenu_id = @id", conn, tx))
            {
                delWc.Parameters.AddWithValue("@id", item.WMenuId);
                await delWc.ExecuteNonQueryAsync();
            }
            await using (var delCmd = new SqlCommand("DELETE command WHERE menu_id = @id", conn, tx))
            {
                delCmd.Parameters.AddWithValue("@id", item.MenuId);
                await delCmd.ExecuteNonQueryAsync();
            }
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<WCommandDuplicateResult> FindDuplicatesAsync()
    {
        await using var conn = _connections.CreateConnection(useSysDatabase: true);
        await conn.OpenAsync();

        var result = new WCommandDuplicateResult();

        const string notExistsSql = @"
SELECT wmenu_id, wmenu_id0, menu_id, bar, bar2, link, sysid
FROM wcommand w
WHERE NOT EXISTS (SELECT 1 FROM command c WHERE c.menu_id = w.menu_id)
   OR EXISTS (SELECT 1 FROM wcommand d WHERE d.wmenu_id = w.wmenu_id GROUP BY d.wmenu_id HAVING COUNT(*) > 1)
ORDER BY wmenu_id;";

        const string diffSysidSql = @"
SELECT w.wmenu_id, w.wmenu_id0, w.menu_id, w.bar, w.bar2, w.link, w.sysid
FROM wcommand w
JOIN command c ON c.menu_id = w.menu_id
WHERE ISNULL(w.sysid, '') <> ISNULL(c.sysid, '')
ORDER BY w.wmenu_id;";

        await using (var cmd = new SqlCommand(notExistsSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                result.NotExistsInCommand.Add(ReadSummaryRow(reader));
        }

        await using (var cmd = new SqlCommand(diffSysidSql, conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                result.DifferenceSysid.Add(ReadSummaryRow(reader));
        }

        return result;
    }

    public static string GenerateScript(WCommandItem item)
    {
        string Lit(string? s) => "N'" + (s ?? "").Replace("'", "''") + "'";
        string Num(decimal d) => d.ToString(CultureInfo.InvariantCulture);

        var sb = new StringBuilder();
        sb.AppendLine($"DELETE wcommand WHERE wmenu_id in ('{(item.WMenuId ?? "").Replace("'", "''")}')");
        sb.AppendLine("GO");
        sb.AppendLine("INSERT INTO wcommand(wmenu_id, wmenu_id0, menu_id, bar, bar2, link, parameter, icon_url, status, icon, sysid, type, syscode, msys, target, xtype, edition, expl_icon)");
        sb.AppendLine("VALUES(" +
            $"{Lit(item.WMenuId)}, {Lit(item.WMenuId0)}, {Lit(item.MenuId)}, {Lit(item.Bar)}, {Lit(item.Bar2)}, " +
            $"{Lit(item.Link)}, {Lit(item.Parameter)}, {Lit(item.IconUrl)}, {Lit(item.Status)}, {Lit(item.Icon)}, " +
            $"{Lit(item.SysId)}, {Lit(item.Type)}, {Lit(item.SysCode)}, {Num(item.Msys)}, {Lit(item.Target)}, " +
            $"{Lit(item.XType)}, {Lit(item.Edition)}, {item.ExplIcon})");
        sb.AppendLine("GO");
        sb.AppendLine($"DELETE command WHERE menu_id = '{(item.MenuId ?? "").Replace("'", "''")}'");
        sb.AppendLine("GO");
        sb.AppendLine($"INSERT INTO command([menu_id], [sysid], [syscode], [msys]) VALUES({Lit(item.MenuId)}, {Lit(item.SysId)}, {Lit(item.SysCode)}, {Num(item.Msys)})");
        sb.AppendLine("GO");
        return sb.ToString();
    }

    private const string InsertWCommandSql = @"
INSERT INTO wcommand(wmenu_id, wmenu_id0, menu_id, bar, bar2, link, parameter, icon_url, status, icon, sysid, type, syscode, msys, target, xtype, edition, expl_icon)
VALUES(@wmenu_id, @wmenu_id0, @menu_id, @bar, @bar2, @link, @parameter, @icon_url, @status, @icon, @sysid, @type, @syscode, @msys, @target, @xtype, @edition, @expl_icon);";

    private const string InsertCommandSql = @"
INSERT INTO command([menu_id], [sysid], [syscode], [msys])
VALUES(@menu_id, @sysid, @syscode, @msys);";

    private static void AddWCommandParameters(SqlCommand cmd, WCommandItem item)
    {
        cmd.Parameters.AddWithValue("@wmenu_id", item.WMenuId);
        cmd.Parameters.AddWithValue("@wmenu_id0", item.WMenuId0);
        cmd.Parameters.AddWithValue("@menu_id", item.MenuId);
        cmd.Parameters.AddWithValue("@bar", item.Bar);
        cmd.Parameters.AddWithValue("@bar2", item.Bar2);
        cmd.Parameters.AddWithValue("@link", item.Link);
        cmd.Parameters.AddWithValue("@parameter", item.Parameter);
        cmd.Parameters.AddWithValue("@icon_url", item.IconUrl);
        cmd.Parameters.AddWithValue("@status", item.Status);
        cmd.Parameters.AddWithValue("@icon", item.Icon);
        cmd.Parameters.AddWithValue("@sysid", item.SysId);
        cmd.Parameters.AddWithValue("@type", item.Type);
        cmd.Parameters.AddWithValue("@syscode", item.SysCode);
        cmd.Parameters.AddWithValue("@msys", item.Msys);
        cmd.Parameters.AddWithValue("@target", item.Target);
        cmd.Parameters.AddWithValue("@xtype", item.XType);
        cmd.Parameters.AddWithValue("@edition", item.Edition);
        cmd.Parameters.AddWithValue("@expl_icon", item.ExplIcon);
    }

    private static void AddCommandParameters(SqlCommand cmd, WCommandItem item)
    {
        cmd.Parameters.AddWithValue("@menu_id", item.MenuId);
        cmd.Parameters.AddWithValue("@sysid", item.SysId);
        cmd.Parameters.AddWithValue("@syscode", item.SysCode);
        cmd.Parameters.AddWithValue("@msys", item.Msys);
    }

    // --- HÀM HỖ TRỢ ĐỌC CỘT AN TOÀN TRÁNH LỖI THIẾU CỘT GIỮA CÁC DB ---
    private static string SafeGet(SqlDataReader reader, string colName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), colName, StringComparison.OrdinalIgnoreCase))
                return reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString()?.Trim() ?? "";
        }
        return "";
    }

    private static decimal SafeGetDecimal(SqlDataReader reader, string colName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), colName, StringComparison.OrdinalIgnoreCase))
                return reader.IsDBNull(i) ? 0 : Convert.ToDecimal(reader.GetValue(i));
        }
        return 0;
    }

    private static byte SafeGetByte(SqlDataReader reader, string colName)
    {
        for (int i = 0; i < reader.FieldCount; i++)
        {
            if (string.Equals(reader.GetName(i), colName, StringComparison.OrdinalIgnoreCase))
                return reader.IsDBNull(i) ? (byte)0 : Convert.ToByte(reader.GetValue(i));
        }
        return 0;
    }

    private static WCommandItem ReadFullRow(SqlDataReader reader) => new()
    {
        WMenuId = SafeGet(reader, "wmenu_id"),
        WMenuId0 = SafeGet(reader, "wmenu_id0"),
        MenuId = SafeGet(reader, "menu_id"),
        Bar = SafeGet(reader, "bar"),
        Bar2 = SafeGet(reader, "bar2"),
        Link = SafeGet(reader, "link"),
        Parameter = SafeGet(reader, "parameter"),
        IconUrl = SafeGet(reader, "icon_url"),
        Status = SafeGet(reader, "status"),
        Icon = SafeGet(reader, "icon"),
        SysId = SafeGet(reader, "sysid"),
        Type = SafeGet(reader, "type"),
        SysCode = SafeGet(reader, "syscode"),
        Msys = SafeGetDecimal(reader, "msys"),
        Target = SafeGet(reader, "target"),
        XType = SafeGet(reader, "xtype"),
        Edition = SafeGet(reader, "edition"),
        ExplIcon = SafeGetByte(reader, "expl_icon"),
    };

    private static WCommandItem ReadSummaryRow(SqlDataReader reader) => new()
    {
        WMenuId = SafeGet(reader, "wmenu_id"),
        WMenuId0 = SafeGet(reader, "wmenu_id0"),
        MenuId = SafeGet(reader, "menu_id"),
        Bar = SafeGet(reader, "bar"),
        Bar2 = SafeGet(reader, "bar2"),
        Link = SafeGet(reader, "link"),
        SysId = SafeGet(reader, "sysid"),
    };
}

public class WCommandDuplicateResult
{
    public List<WCommandItem> NotExistsInCommand { get; } = new();
    public List<WCommandItem> DifferenceSysid { get; } = new();
}