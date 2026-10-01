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

        // Luôn lấy toàn bộ bảng — không lọc bằng WHERE ở SQL nữa. Trước đây lọc thẳng
        // ở SQL (bar/bar2/link LIKE) khiến một menu con khớp filter nhưng có menu cha
        // KHÔNG khớp sẽ bị mất luôn hàng cha (cha không được SELECT về), nên
        // BuildHierarchy không tìm thấy cha và đẩy menu con đó lên thành node gốc rời
        // rạc — nhìn như tìm kiếm theo tên "không hoạt động" dù thật ra vẫn trả kết
        // quả. Giờ dựng cây đầy đủ trước (SELECT * FROM dbo.wcommand), rồi mới lọc
        // bằng FilterTree bên dưới để giữ đúng vị trí lồng cha/con.
        const string sql = "SELECT * FROM dbo.wcommand";

        await using var cmd = new SqlCommand(sql, conn);

        var all = new List<WCommandItem>();
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                all.Add(ReadFullRow(reader));
        }

        var roots = BuildHierarchy(all);
        if (string.IsNullOrWhiteSpace(filterLike)) return roots;

        return FilterTree(roots, filterLike.Trim());
    }

    /// <summary>Giữ lại một node nếu chính nó khớp filter (theo Bar, Bar2, Link hoặc
    /// WMenuId) hoặc có ít nhất một menu con — ở bất kỳ cấp nào — khớp. Nhờ vậy kết
    /// quả tìm theo tên bar vẫn hiện đúng lồng trong menu cha thật của nó, thay vì
    /// trở thành node rời rạc ở cấp gốc.</summary>
    private static List<WCommandItem> FilterTree(List<WCommandItem> nodes, string filter)
    {
        var result = new List<WCommandItem>();
        foreach (var node in nodes)
        {
            var matchingChildren = FilterTree(node.Children, filter);
            var selfMatches = Matches(node, filter);
            if (!selfMatches && matchingChildren.Count == 0) continue;

            // Mỗi lần LoadTreeAsync chạy đều SELECT lại toàn bộ bảng và dựng WCommandItem
            // mới hoàn toàn, nên các node này không bị chia sẻ/dùng lại giữa các lần gọi
            // — rút gọn thẳng Children trên node hiện có là an toàn, không cần clone.
            node.Children.Clear();
            node.Children.AddRange(matchingChildren);
            result.Add(node);
        }
        return result;
    }

    private static bool Matches(WCommandItem item, string filter) =>
        item.Bar.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        item.Bar2.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        item.Link.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        item.WMenuId.Contains(filter, StringComparison.OrdinalIgnoreCase);

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

    /// <summary>Tồn tại gì rồi — để chặn New ghi đè nhầm menu khác (SaveAsync luôn DELETE theo
    /// id trước khi INSERT, nên New với id đã dùng sẽ lặng lẽ xóa menu cũ).
    /// <paramref name="ignoreWMenuId"/> = id gốc của dòng đang Edit, không tính là trùng.</summary>
    public async Task<(bool WMenuIdExists, bool MenuIdInCommand, bool MenuIdInOtherWCommand)> CheckIdsAsync(
        string wmenuId, string menuId, string? ignoreWMenuId = null)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase: true);
        await conn.OpenAsync();

        async Task<bool> Exists(string sql, params (string, object)[] ps)
        {
            await using var cmd = new SqlCommand(sql, conn);
            foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
            return await cmd.ExecuteScalarAsync() is not null;
        }

        var wExists = ignoreWMenuId is not null && string.Equals(ignoreWMenuId, wmenuId, StringComparison.OrdinalIgnoreCase)
            ? false
            : await Exists("SELECT TOP 1 1 FROM wcommand WHERE wmenu_id = @w", ("@w", wmenuId));
        var inCommand = await Exists("SELECT TOP 1 1 FROM command WHERE menu_id = @m", ("@m", menuId));
        var inWc = await Exists("SELECT TOP 1 1 FROM wcommand WHERE menu_id = @m AND wmenu_id <> @w", ("@m", menuId), ("@w", ignoreWMenuId ?? wmenuId));
        return (wExists, inCommand, inWc);
    }

    /// <summary>
    /// Gợi ý <c>menu_id</c> (vd 06.01.04) chưa có ở cả <c>command</c> lẫn <c>wcommand</c>.
    /// Dạng "nhóm.mục.số" như wmenu_id. Tiền tố (2 đoạn đầu) lấy từ menu_id lớn nhất trong
    /// các menu anh em cùng cha (<paramref name="parentWMenuId"/>), rồi đến menu_id đang gõ
    /// (<paramref name="seedMenuId"/>); không có gì thì dùng nhóm trống đầu tiên NN.01.01.
    /// Số cuối = lớn nhất đang dùng dưới tiền tố đó + 1, giữ nguyên độ rộng chữ số.
    /// </summary>
    public async Task<string> SuggestNextMenuIdAsync(string? parentWMenuId, string? seedMenuId)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase: true);
        await conn.OpenAsync();

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var siblings = new List<string>();
        var parent = (parentWMenuId ?? "").Trim();

        await using (var cmd = new SqlCommand(
            "SELECT menu_id, '' AS p FROM command UNION ALL SELECT menu_id, wmenu_id0 FROM wcommand", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var id = (reader.IsDBNull(0) ? "" : reader.GetString(0)).Trim();
                if (id.Length == 0) continue;
                used.Add(id);
                var p = (reader.IsDBNull(1) ? "" : reader.GetString(1)).Trim();
                if (parent.Length > 0 && p.Equals(parent, StringComparison.OrdinalIgnoreCase)) siblings.Add(id);
            }
        }

        static string[]? Parts(string id)
        {
            var p = id.Split('.');
            return p.Length == 3 && p.All(s => s.Length is > 0 and <= 2 && s.All(char.IsDigit)) ? p : null;
        }

        string? prefix = null;
        var seedParts = string.IsNullOrWhiteSpace(seedMenuId) ? null : Parts(seedMenuId.Trim());
        var best = siblings.Select(s => (id: s, p: Parts(s))).Where(x => x.p != null)
            .OrderByDescending(x => x.id, StringComparer.Ordinal).FirstOrDefault();
        if (best.p != null) prefix = $"{best.p[0]}.{best.p[1]}";
        else if (seedParts != null) prefix = $"{seedParts[0]}.{seedParts[1]}";

        if (prefix == null)
        {
            for (var g = 1; g <= 99; g++)
            {
                var candidate = $"{g:D2}.01.01";
                if (!used.Contains(candidate) && !used.Any(u => u.StartsWith($"{g:D2}.", StringComparison.Ordinal))) return candidate;
            }
            return "99.99.99";
        }

        // menu_id là char(8) → đúng dạng NN.NN.NN, mỗi đoạn tối đa 2 chữ số. Không bao giờ sinh
        // quá 8 ký tự: hết số ở đoạn cuối thì sang mục kế (NN.MM+1.01), hết nữa thì sang nhóm
        // kế, cuối cùng quét toàn bộ không gian 99×99×99 tìm id còn trống.
        var max = 0;
        foreach (var id in used)
        {
            var p = Parts(id);
            if (p is null || $"{p[0]}.{p[1]}" != prefix) continue;
            if (int.TryParse(p[2], out var n) && n > max) max = n;
        }
        var g0 = int.Parse(prefix.Split('.')[0]);
        var s0 = int.Parse(prefix.Split('.')[1]);
        for (var n = max + 1; n <= 99; n++)
        {
            var candidate = $"{g0:D2}.{s0:D2}.{n:D2}";
            if (!used.Contains(candidate)) return candidate;
        }
        for (var g = g0; g <= 99; g++)
            for (var s = g == g0 ? s0 + 1 : 1; s <= 99; s++)
                for (var l = 1; l <= 99; l++)
                {
                    var candidate = $"{g:D2}.{s:D2}.{l:D2}";
                    if (!used.Contains(candidate)) return candidate;
                }
        return "";
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