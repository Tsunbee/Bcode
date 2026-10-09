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

    /// <summary>CRUD bảng command + reports cho sản phẩm dạng APP (không có wcommand).</summary>
    public AppCommandService AppCommands { get; }

    public WCommandService(DbConnectionService connections)
    {
        _connections = connections;
        AppCommands = new AppCommandService(connections);
    }

    // ---- Bản lưu (cache) cây menu: lưu danh sách phẳng theo workspace ở %AppData%\Bcode\menu-cache\ ----------------------------------

    /// <summary>Chữ ký của lần tải gần nhất từ database — so với <see cref="SignatureOf"/> của bản lưu để biết cây có đổi không.</summary>
    public string? LastSignature { get; private set; }

    private string CachePath()
    {
        var stamp = _connections.CurrentStamp(true);
        var safe = System.Text.RegularExpressions.Regex.Replace(stamp, @"[^\w.\-]+", "_");
        if (safe.Length > 120) safe = safe[^120..];
        return Path.Combine(BcodePaths.AppData, "Bcode", "menu-cache", safe + ".json");
    }

    public static string SignatureOf(List<WCommandItem> all)
    {
        var sb = new StringBuilder();
        foreach (var i in all)
            sb.Append(i.WMenuId).Append('|').Append(i.WMenuId0).Append('|').Append(i.MenuId).Append('|').Append(i.Bar).Append('|').Append(i.Bar2).Append('|')
              .Append(i.Link).Append('|').Append(i.Parameter).Append('|').Append(i.Status).Append('|').Append(i.SysId).Append('|').Append(i.Type).Append('\n');
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Danh sách phẳng đã lưu của workspace hiện tại (null nếu chưa có / hỏng).</summary>
    public List<WCommandItem>? LoadCachedFlat()
    {
        try
        {
            if (_connections.Current is null) return null;
            var path = CachePath();
            return File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<List<WCommandItem>>(File.ReadAllText(path, Encoding.UTF8)) : null;
        }
        catch { return null; }
    }

    private void SaveCacheFlat(List<WCommandItem> all, string? lightSig = null)
    {
        if (all.Count == 0 || _connections.Current is null) return;
        var path = CachePath();
        var sigPath = LightSigPath();
        var json = System.Text.Json.JsonSerializer.Serialize(all);      // Children là chỉ-đọc nên không bị ghi; BuildHierarchy dựng lại
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, json, Encoding.UTF8);
                File.Move(tmp, path, overwrite: true);
                if (lightSig is not null) File.WriteAllText(sigPath, lightSig, Encoding.UTF8);
                else if (File.Exists(sigPath)) File.Delete(sigPath);   // không có chữ ký nhẹ (APP...) → lần sau tải lại như cũ
            }
            catch { /* không lưu được thì lần sau tải từ database */ }
        });
    }

    // ---- Chữ ký nhẹ: database tự tính (COUNT + CHECKSUM_AGG) nên không phải tải cả bảng chỉ để biết "có đổi không" ----------------------

    private string LightSigPath() => Path.ChangeExtension(CachePath(), ".sig");

    /// <summary>Chữ ký nhẹ của bảng wcommand ngay lúc này; null nếu không tính được (sản phẩm dạng APP không có wcommand, lỗi mạng...).</summary>
    private static async Task<string?> QueryLightSignatureAsync(SqlConnection conn)
    {
        try
        {
            await using var cmd = new SqlCommand("SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM dbo.wcommand", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;
            return reader.GetInt64(0) + ":" + (reader.IsDBNull(1) ? "n" : reader.GetInt32(1).ToString());
        }
        catch { return null; }
    }

    /// <summary>Bản lưu trên máy còn đúng với database không? Chỉ true khi hỏi được chữ ký nhẹ VÀ nó trùng chữ ký đã lưu cùng bản lưu.
    /// Mọi trường hợp còn lại (chưa có chữ ký, khác, lỗi) trả false để gọi tải lại toàn bảng như cũ.</summary>
    public async Task<bool> IsCacheCurrentAsync()
    {
        try
        {
            var path = LightSigPath();
            if (!File.Exists(path)) return false;
            var saved = File.ReadAllText(path, Encoding.UTF8).Trim();
            if (saved.Length == 0) return false;
            await using var conn = _connections.CreateConnection(useSysDatabase: true);
            await conn.OpenAsync();
            var now = await QueryLightSignatureAsync(conn);
            var same = now is not null && now == saved;
            return same;
        }
        catch { return false; }
    }

    /// <summary>Dựng cây từ danh sách phẳng (dùng cho bản lưu).</summary>
    public List<WCommandItem> TreeFromFlat(List<WCommandItem> flat) => BuildHierarchy(flat);

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

        var lightSig = await QueryLightSignatureAsync(conn);   // lấy TRƯỚC khi đọc bảng: nếu bảng đổi giữa chừng thì lần sau lệch → tải lại (an toàn)
        var all = new List<WCommandItem>();
        try
        {
            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                all.Add(ReadFullRow(reader));
        }
        catch (SqlException ex) when (ex.Number == 208) // Invalid object name 'wcommand' — sản phẩm không có menu web
        {
            all.Clear();
        }

        // Sản phẩm dạng APP (FBFF...) không có menu web: wcommand không tồn tại hoặc rỗng, menu nằm ở bảng
        // `command` (menu_id / menu_id0 / bar / bar2) — như tab Command của FCode.
        if (all.Count == 0)
            all = await LoadAppCommandAsync();

        LastSignature = SignatureOf(all);
        SaveCacheFlat(all, all.Count > 0 && !all[0].IsAppCommand ? lightSig : null);          // bản lưu trên máy: lần sau hiện cây ngay, không đợi database
        var roots = BuildHierarchy(all);
        if (string.IsNullOrWhiteSpace(filterLike)) return roots;

        return FilterTree(roots, filterLike.Trim());
    }

    /// <summary>Menu của sản phẩm APP: bảng <c>command</c> có đủ cột menu_id0 + bar. Thử Sys Data trước rồi
    /// tới App Data (tuỳ sản phẩm đặt bảng ở đâu); không nơi nào có thì trả rỗng.</summary>
    private async Task<List<WCommandItem>> LoadAppCommandAsync()
    {
        AppCommands.Reset();
        foreach (var useSys in new[] { true, false })
        {
            try
            {
                await using var conn = _connections.CreateConnection(useSysDatabase: useSys);
                await conn.OpenAsync();

                await using var cmd = new SqlCommand("SELECT * FROM dbo.command", conn);
                await using var reader = await cmd.ExecuteReaderAsync();

                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++) names.Add(reader.GetName(i));
                if (!names.Contains("menu_id0") || !names.Contains("bar")) continue;

                var items = new List<WCommandItem>();
                while (await reader.ReadAsync())
                {
                    var id = SafeGet(reader, "menu_id");
                    if (string.IsNullOrEmpty(id)) continue;
                    var exe = SafeGet(reader, "exe");
                    items.Add(new WCommandItem
                    {
                        WMenuId = id,
                        MenuId = id,
                        WMenuId0 = SafeGet(reader, "menu_id0"),
                        Bar = SafeGet(reader, "bar"),
                        Bar2 = SafeGet(reader, "bar2"),
                        Link = string.IsNullOrEmpty(exe) ? SafeGet(reader, "rep_file") : exe,
                        SysId = SafeGet(reader, "sysid"),
                        SysCode = SafeGet(reader, "syscode"),
                        Type = SafeGet(reader, "type"),
                        Icon = SafeGet(reader, "icon"),
                        IsAppCommand = true,
                        Exe = exe,
                    });
                }
                if (items.Count > 0) return items;
            }
            catch (SqlException)
            {
                // bảng/cột không có ở DB này — thử DB còn lại
            }
        }
        return new List<WCommandItem>();
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
    /// Gợi ý <c>menu_id</c> chưa có ở cả <c>command</c> lẫn <c>wcommand</c>.
    /// Cột là char(8) nên luôn đúng dạng <c>GG.SS.LL</c> (2 ký tự mỗi đoạn): <c>GG</c> = nhóm,
    /// là 2 chữ số (06) hoặc 2 chữ cái in hoa (AA, AB, ... ZZ — dành cho menu tự thêm, tránh
    /// đụng dải số của chuẩn); <c>SS</c>, <c>LL</c> luôn là 2 chữ số 01..99.
    /// Tiền tố <c>GG.SS</c> lấy từ menu_id lớn nhất trong các menu anh em cùng cha
    /// (<paramref name="parentWMenuId"/>), rồi đến menu_id đang gõ (<paramref name="seedMenuId"/>);
    /// LL = lớn nhất đang dùng dưới tiền tố đó + 1. Hết chỗ thì sang mục kế, rồi nhóm kế
    /// (01..99 rồi AA..ZZ), cuối cùng quay lại quét các nhóm phía trước. Trả "" nếu hết sạch.
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

        // Nhóm = 2 chữ số hoặc 2 chữ cái; mục/số cuối = 1-2 chữ số.
        static string[]? Parts(string id)
        {
            var p = id.Trim().ToUpperInvariant().Split('.');
            if (p.Length != 3) return null;
            var g = p[0];
            var okGroup = g.Length == 2 && (g.All(char.IsDigit) || g.All(c => c is >= 'A' and <= 'Z'));
            var okRest = p[1].Length is 1 or 2 && p[1].All(char.IsDigit) && p[2].Length is 1 or 2 && p[2].All(char.IsDigit);
            return okGroup && okRest ? p : null;
        }

        // Thứ tự nhóm: 01..99, rồi AA..ZZ.
        var groups = new List<string>();
        for (var i = 1; i <= 99; i++) groups.Add(i.ToString("D2"));
        for (var a = 'A'; a <= 'Z'; a++)
            for (var b = 'A'; b <= 'Z'; b++) groups.Add($"{a}{b}");

        string? FirstFree(string group, int fromSub, int fromLeaf)
        {
            for (var s = fromSub; s <= 99; s++)
                for (var l = s == fromSub ? fromLeaf : 1; l <= 99; l++)
                {
                    var candidate = $"{group}.{s:D2}.{l:D2}";
                    if (!used.Contains(candidate)) return candidate;
                }
            return null;
        }

        string? group0 = null;
        var sub0 = 1;
        var best = siblings.Select(s => Parts(s)).Where(p => p != null)
            .OrderByDescending(p => string.Join('.', p!), StringComparer.Ordinal).FirstOrDefault();
        var seedParts = string.IsNullOrWhiteSpace(seedMenuId) ? null : Parts(seedMenuId);
        var from = best ?? seedParts;
        if (from != null) { group0 = from[0]; sub0 = int.Parse(from[1]); }

        if (group0 == null)
        {
            // Không có gợi ý nào để bám: nhóm số đầu tiên chưa có menu_id nào.
            foreach (var g in groups)
                if (!used.Any(u => u.StartsWith(g + ".", StringComparison.OrdinalIgnoreCase)) && FirstFree(g, 1, 1) is { } c) return c;
            return "";
        }

        // 1) Cùng mục (GG.SS): số cuối = max + 1.
        var max = 0;
        foreach (var id in used)
        {
            var p = Parts(id);
            if (p != null && p[0] == group0 && int.TryParse(p[1], out var s) && s == sub0 && int.TryParse(p[2], out var n) && n > max) max = n;
        }
        if (max < 99 && FirstFree(group0, sub0, max + 1) is { } sameSection && sameSection.StartsWith($"{group0}.{sub0:D2}.", StringComparison.Ordinal))
            return sameSection;

        // 2) Cùng nhóm, mục kế tiếp.
        if (sub0 < 99 && FirstFree(group0, sub0 + 1, 1) is { } sameGroup) return sameGroup;

        // 3) Các nhóm sau nhóm hiện tại, rồi quay lại các nhóm đứng trước.
        var idx = groups.IndexOf(group0);
        var order = idx < 0 ? groups : groups.Skip(idx + 1).Concat(groups.Take(idx)).ToList();
        foreach (var g in order)
            if (FirstFree(g, 1, 1) is { } c) return c;
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

        // Ẩn menu mồ côi: ở cấp gốc mà không có menu con (cha khai báo không tồn tại → bị đẩy lên gốc, hoặc nhóm rỗng,
        // gồm cả các dòng "-" ngăn cách) — không có ích khi duyệt cây.
        return roots.Where(r => r.Children.Count > 0).OrderBy(r => r.WMenuId).ToList();
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

            // Mỗi dự án có thể thiếu một số cột (edition, expl_icon, xtype...) — chỉ chèn những cột bảng thật sự có.
            var wcCols = await GetColumnNamesAsync(conn, tx, "wcommand");
            var cmdCols = await GetColumnNamesAsync(conn, tx, "command");

            await using (var insWc = new SqlCommand("", conn, tx))
            {
                AddWCommandParameters(insWc, item);
                insWc.CommandText = BuildInsertSql("wcommand", insWc, wcCols);
                await insWc.ExecuteNonQueryAsync();
            }
            await using (var insCmd = new SqlCommand("", conn, tx))
            {
                AddCommandParameters(insCmd, item);
                insCmd.CommandText = BuildInsertSql("command", insCmd, cmdCols);
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

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqlConnection conn, SqlTransaction tx, string table)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t)", conn, tx);
        cmd.Parameters.AddWithValue("@t", "dbo." + table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add(r.GetString(0));
        return set;
    }

    /// <summary>INSERT chỉ gồm các tham số (@cột) mà bảng thật sự có cột đó; tham số thừa bị gỡ khỏi lệnh.</summary>
    private static string BuildInsertSql(string table, SqlCommand cmd, HashSet<string> existingColumns)
    {
        var names = cmd.Parameters.Cast<SqlParameter>().Select(p => p.ParameterName.TrimStart('@')).ToList();
        var keep = names.Where(existingColumns.Contains).ToList();
        foreach (var n in names.Where(n => !existingColumns.Contains(n)).ToList()) cmd.Parameters.RemoveAt("@" + n);
        return $"INSERT INTO {table}({string.Join(", ", keep.Select(n => "[" + n + "]"))}) VALUES({string.Join(", ", keep.Select(n => "@" + n))});";
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

    /// <summary>Các dòng wcommand trỏ tới 1 controller: sysid trùng tên, hoặc link bắt đầu bằng "&lt;tên&gt;.aspx" — đủ cột
    /// để GenerateScript sinh được script DELETE/INSERT cho wcommand + command (Advance Note → Gen All → sysmenu).</summary>
    public async Task<List<WCommandItem>> FindByControllerAsync(string controller)
    {
        await using var conn = _connections.CreateConnection(useSysDatabase: true);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT * FROM dbo.wcommand WHERE sysid = @n OR link LIKE @l ORDER BY wmenu_id", conn);
        cmd.Parameters.AddWithValue("@n", controller);
        cmd.Parameters.AddWithValue("@l", controller + ".aspx%");
        var list = new List<WCommandItem>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) list.Add(ReadFullRow(reader));
        return list;
    }

    /// <summary>
    /// Script DELETE + INSERT cho 1 menu (Gen Script Menu). <paramref name="wcommandColumns"/> / <paramref name="commandColumns"/> =
    /// các cột bảng thật sự có (xem <see cref="GetMenuColumns"/>) — cột nào bảng không có thì bỏ khỏi INSERT, giống
    /// <see cref="SaveAsync"/> (mỗi dự án có thể thiếu edition, expl_icon, xtype, target...). null = không biết cấu trúc → ghi đủ cột.
    /// </summary>
    public static string GenerateScript(WCommandItem item, ISet<string>? wcommandColumns = null, ISet<string>? commandColumns = null)
    {
        string Lit(string? s) => "N'" + (s ?? "").Replace("'", "''") + "'";
        string Num(decimal d) => d.ToString(CultureInfo.InvariantCulture);

        var wc = new (string Col, string Val)[]
        {
            ("wmenu_id", Lit(item.WMenuId)), ("wmenu_id0", Lit(item.WMenuId0)), ("menu_id", Lit(item.MenuId)), ("bar", Lit(item.Bar)),
            ("bar2", Lit(item.Bar2)), ("link", Lit(item.Link)), ("parameter", Lit(item.Parameter)), ("icon_url", Lit(item.IconUrl)),
            ("status", Lit(item.Status)), ("icon", Lit(item.Icon)), ("sysid", Lit(item.SysId)), ("type", Lit(item.Type)),
            ("syscode", Lit(item.SysCode)), ("msys", Num(item.Msys)), ("target", Lit(item.Target)), ("xtype", Lit(item.XType)),
            ("edition", Lit(item.Edition)), ("expl_icon", item.ExplIcon.ToString(CultureInfo.InvariantCulture)),
        };
        var cmd = new (string Col, string Val)[]
        {
            ("menu_id", Lit(item.MenuId)), ("sysid", Lit(item.SysId)), ("syscode", Lit(item.SysCode)), ("msys", Num(item.Msys)),
        };
        static (string Col, string Val)[] Keep((string Col, string Val)[] all, ISet<string>? existing) =>
            existing is null || existing.Count == 0 ? all : all.Where(x => existing.Contains(x.Col)).ToArray();
        wc = Keep(wc, wcommandColumns);
        cmd = Keep(cmd, commandColumns);

        var sb = new StringBuilder();
        sb.AppendLine($"DELETE wcommand WHERE wmenu_id in ('{(item.WMenuId ?? "").Replace("'", "''")}')");
        sb.AppendLine("GO");
        sb.AppendLine($"INSERT INTO wcommand({string.Join(", ", wc.Select(x => x.Col))})");
        sb.AppendLine($"VALUES({string.Join(", ", wc.Select(x => x.Val))})");
        sb.AppendLine("GO");
        sb.AppendLine($"DELETE command WHERE menu_id = '{(item.MenuId ?? "").Replace("'", "''")}'");
        sb.AppendLine("GO");
        sb.AppendLine($"INSERT INTO command({string.Join(", ", cmd.Select(x => "[" + x.Col + "]"))}) VALUES({string.Join(", ", cmd.Select(x => x.Val))})");
        sb.AppendLine("GO");
        return sb.ToString();
    }

    /// <summary>Gen Script Menu theo đúng cấu trúc bảng wcommand / command của database đang chọn. Không đọc được cấu trúc thì ghi đủ cột + 1 dòng chú thích.</summary>
    public string GenerateScriptForCurrentDb(WCommandItem item)
    {
        var cols = GetMenuColumns(out var error);
        var script = GenerateScript(item, cols?.WCommand, cols?.Command);
        return error is null ? script : $"-- Không đọc được cấu trúc bảng wcommand/command ({error}) — script ghi đủ cột, kiểm tra lại trước khi chạy.\r\n" + script;
    }

    // Cấu trúc bảng menu theo từng database (khoá = CurrentStamp) — đọc 1 lần, dùng lại cho mọi lần Gen Script.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (HashSet<string> WCommand, HashSet<string> Command)> MenuColumnsCache = new();

    /// <summary>Tên cột thật của wcommand và command (Sys Data). Đồng bộ (không async) vì Tạo báo cáo gọi qua interface đồng bộ — truy vấn rất nhẹ và có cache.</summary>
    public (HashSet<string> WCommand, HashSet<string> Command)? GetMenuColumns(out string? error)
    {
        error = null;
        var stamp = _connections.CurrentStamp(true);
        if (MenuColumnsCache.TryGetValue(stamp, out var cached)) return cached;
        try
        {
            using var conn = _connections.CreateConnection(useSysDatabase: true);
            conn.Open();
            HashSet<string> Read(string table)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using var c = new SqlCommand("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@t)", conn);
                c.Parameters.AddWithValue("@t", "dbo." + table);
                using var r = c.ExecuteReader();
                while (r.Read()) set.Add(r.GetString(0));
                return set;
            }
            var result = (Read("wcommand"), Read("command"));
            if (result.Item1.Count == 0) { error = "không thấy bảng wcommand"; return null; }
            MenuColumnsCache[stamp] = result;
            return result;
        }
        catch (Exception ex) { error = ex.Message; return null; }
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