using System.Globalization;
using System.Text;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Loads the menu tree from the wcommand table and assembles it into a
/// parent/child hierarchy for display in a TreeView (left panel of the
/// WCommand tab in FCode). Also backs the WCommand right-click menu: New/
/// Edit/Delete save straight to wcommand+command, "Check WCommand" flags
/// rows that don't line up between the two tables, and "Gen Script Menu"
/// builds the same DELETE-then-INSERT script FCode itself generates.
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

        var sql = @"
SELECT wmenu_id, wmenu_id0, menu_id, bar, bar2, link, parameter, icon_url, status, icon, sysid, type,
       syscode, msys, target, xtype, edition, expl_icon
FROM wcommand
" + (string.IsNullOrWhiteSpace(filterLike) ? "" : "WHERE wmenu_id LIKE @f ") + @"
ORDER BY wmenu_id;";

        await using var cmd = new SqlCommand(sql, conn);
        if (!string.IsNullOrWhiteSpace(filterLike))
            cmd.Parameters.AddWithValue("@f", filterLike);

        var all = new List<WCommandItem>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                all.Add(ReadFullRow(reader));
        }

        return BuildHierarchy(all);
    }

    /// <summary>
    /// "Suggest" next to WMenu Id in the New dialog — proposes a wmenu_id that doesn't
    /// exist yet, so declaring a new menu doesn't mean guessing a free number by hand.
    /// wcommand's own ids follow a 2-level "group.leaf" numbering everywhere we've seen it
    /// (e.g. "07.00.00" is the group row for "07.*"; its menus are "07.10.06", "07.70.10",
    /// ...) — same shape <see cref="InferParentId"/> already relies on. Given the chosen
    /// parent's first segment ("07"), this finds the highest existing 2nd-segment number
    /// under that group and proposes the next one (leaf "01"); with no parent chosen yet,
    /// it proposes the next free group id ("NN.00.00") instead. Either way the result is
    /// checked against every wmenu_id actually in the table, so it's always free right now
    /// — just not guaranteed to still be free by the time Save runs if someone else grabs
    /// it first (same as any other form).
    /// </summary>
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
                var id = reader["wmenu_id"]?.ToString()?.Trim();
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
            if (parts[1] == "00") continue; // the group's own placeholder row ("07.00.00")
            if (int.TryParse(parts[1], out var n) && n > maxLeaf) maxLeaf = n;
        }

        for (var n = maxLeaf + 1; n <= 99; n++)
        {
            var candidate = $"{group}.{n:D2}.01";
            if (!all.Contains(candidate)) return candidate;
        }

        return $"{group}.99.99";
    }

    /// <summary>
    /// wcommand doesn't always carry an explicit parent id for the top groups (they're
    /// often inferred from the wmenu_id prefix, e.g. "07.10.06" belongs under "07.00.00").
    /// This groups by the leading two-segment prefix ("07.00.00") when wmenu_id0 is blank.
    /// </summary>
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

    /// <summary>"07.10.06" -&gt; "07.00.00"; "07.00.00" -&gt; "" (already a root group).</summary>
    private static string InferParentId(string wmenuId)
    {
        var segments = wmenuId.Split('.');
        if (segments.Length < 3) return "";
        if (segments[1] == "00" && segments[2] == "00") return "";
        return $"{segments[0]}.00.00";
    }

    /// <summary>
    /// Saves a wcommand (+ command) row. Follows FCode's own convention for this data —
    /// visible in its "Gen Script Menu" output, which always emits DELETE-then-INSERT,
    /// never UPDATE — so a live Save produces exactly what replaying that same edit's
    /// generated script would. <paramref name="originalWMenuId"/> is the id to delete
    /// under before inserting (needed when editing and the user renamed wmenu_id itself);
    /// pass null for a brand-new row (nothing to delete first).
    /// </summary>
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

    /// <summary>
    /// "Check WCommand" — flags wcommand rows that don't line up with the command table,
    /// mirroring FCode's own "Duplicate Menu" check: rows with no matching command row at
    /// all, and rows where the two tables disagree on sysid for the same menu_id. Also
    /// folds in an actual duplicate-id check (two wcommand rows sharing one wmenu_id, which
    /// silently breaks the tree since BuildHierarchy only keeps the first) into the first tab.
    /// </summary>
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

    /// <summary>
    /// "Gen Script Menu" — builds the exact DELETE-then-INSERT script FCode itself shows
    /// for a menu (wcommand row + its companion command row), each statement terminated
    /// with a "GO" batch separator the way FCode's script does. Pure string building, no
    /// DB round-trip — the caller already has the full WCommandItem in hand.
    /// </summary>
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

    private static WCommandItem ReadFullRow(SqlDataReader reader) => new()
    {
        WMenuId = reader["wmenu_id"]?.ToString()?.Trim() ?? "",
        WMenuId0 = reader["wmenu_id0"]?.ToString()?.Trim() ?? "",
        MenuId = reader["menu_id"]?.ToString()?.Trim() ?? "",
        Bar = reader["bar"]?.ToString() ?? "",
        Bar2 = reader["bar2"]?.ToString() ?? "",
        Link = reader["link"]?.ToString() ?? "",
        Parameter = reader["parameter"]?.ToString() ?? "",
        IconUrl = reader["icon_url"]?.ToString() ?? "",
        Status = reader["status"]?.ToString() ?? "",
        Icon = reader["icon"]?.ToString() ?? "",
        SysId = reader["sysid"]?.ToString() ?? "",
        Type = reader["type"]?.ToString() ?? "",
        SysCode = reader["syscode"]?.ToString() ?? "",
        Msys = reader["msys"] is DBNull ? 0 : Convert.ToDecimal(reader["msys"]),
        Target = reader["target"]?.ToString() ?? "",
        XType = reader["xtype"]?.ToString() ?? "",
        Edition = reader["edition"]?.ToString() ?? "",
        ExplIcon = reader["expl_icon"] is DBNull ? (byte)0 : Convert.ToByte(reader["expl_icon"]),
    };

    /// <summary>Lighter row shape for the "Check WCommand" grids — only the columns the
    /// Duplicate Menu dialog actually displays (wmenu_id/bar/menu_id/link/sysid).</summary>
    private static WCommandItem ReadSummaryRow(SqlDataReader reader) => new()
    {
        WMenuId = reader["wmenu_id"]?.ToString()?.Trim() ?? "",
        WMenuId0 = reader["wmenu_id0"]?.ToString()?.Trim() ?? "",
        MenuId = reader["menu_id"]?.ToString()?.Trim() ?? "",
        Bar = reader["bar"]?.ToString() ?? "",
        Bar2 = reader["bar2"]?.ToString() ?? "",
        Link = reader["link"]?.ToString() ?? "",
        SysId = reader["sysid"]?.ToString() ?? "",
    };
}

/// <summary>Result of "Check WCommand" — mirrors FCode's "Duplicate Menu" dialog's two tabs.</summary>
public class WCommandDuplicateResult
{
    public List<WCommandItem> NotExistsInCommand { get; } = new();
    public List<WCommandItem> DifferenceSysid { get; } = new();
}
