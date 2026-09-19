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

        var sql = @"
SELECT wmenu_id, wmenu_id0, menu_id, bar, bar2, link, parameter, icon_url, status, icon, sysid, type
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
            {
                all.Add(new WCommandItem
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
                });
            }
        }

        return BuildHierarchy(all);
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
}
