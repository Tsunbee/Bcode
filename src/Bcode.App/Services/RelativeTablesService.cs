using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// "View Relative Tables" (như FCode): với một chứng từ (node WCommand có <c>syscode</c>), liệt kê các bảng dữ liệu liên quan tới chứng từ đó
/// kèm điều kiện lọc (WHERE) sẵn cho từng bảng — để bấm vào là mở tab Table đã lọc đúng chứng từ.
///
/// Tiêu chí "bảng liên quan" lấy NGUYÊN của FCode (Config/Table.xml, node <c>view_table</c>): một DANH SÁCH BẢNG CHUẨN cố định theo loại
/// (FBO cho chứng từ web/wcommand, FBFF cho sản phẩm dạng command), chia 2 database App / Sys, mỗi bảng một mẫu WHERE. Danh sách này được lọc
/// lại chỉ giữ bảng THẬT SỰ CÓ trong database (sys.objects), rồi thay các chỗ giữ chỗ trong WHERE:
/// <list type="bullet">
///  <item><c>{syscode}</c> = mã chứng từ (ma_ct), vd HDA.</item>
///  <item><c>{sysid}</c> = SysId (controller master), vd SVTran.</item>
///  <item><c>{controller}</c> = SysId viết HOA bỏ "TRAN" (SV), <c>{controllerM}</c> = SysId.</item>
///  <item><c>{menu_id}</c> / <c>{wmenu_id}</c> = id menu trên cây.</item>
///  <item><c>{table_index}</c> = số lấy từ tiền tố bảng m$ của dmct9 (m74$ → 74).</item>
/// </list>
/// Phần "huy hiệu" (c$/m$/d$/i$ + url) đọc từ bảng dmct9 (App) của chứng từ — giống dải nhãn màu trên đầu màn "Table for …" của FCode.
/// </summary>
public sealed class RelativeTablesService
{
    private readonly DbConnectionService _connections;

    public RelativeTablesService(DbConnectionService connections) => _connections = connections;

    /// <summary>Một bảng liên quan. <paramref name="Sys"/> = bảng ở Sys Data (ngược lại App Data).</summary>
    public sealed record RelTable(bool Sys, string Table, string Where);

    /// <summary>Huy hiệu tiền tố bảng của chứng từ (c$/m$/d$/i$) — hiện trên đầu như dải nhãn màu của FCode; bấm = xem bảng <paramref name="Table"/>
    /// (như FCode: có "$" thì thêm "000000", vd m81$ → m81$000000) lọc theo <paramref name="Where"/> (ma_ct = chứng từ, rỗng nếu bảng không có cột ma_ct).</summary>
    public sealed record Badge(string Kind, string Text, string Table, string Where);

    public sealed record Result(string VoucherCode, string SysId, string VoucherName, string? Url,
        List<Badge> Badges, List<RelTable> Tables, List<string> Warnings);

    // --- Cấu hình view_table lấy nguyên từ FCode Config/Table.xml (2 node: FBO/V, FBFF/V). Mỗi mục = (tên bảng, mẫu WHERE). ---
    // Thứ tự khớp y như mảng tables/key được zip theo chỉ số của FCode — giữ nguyên kể cả chỗ trông "lệch" (vd gndmttcttg lọc theo gndmloaiduyet).

    private static readonly (string Table, string Where)[] FboApp =
    {
        ("dmct", "ma_ct = '{syscode}'"), ("dmct0", "ma_ct = '{syscode}'"), ("dmct4", "ma_ct = '{syscode}'"),
        ("dmct5", "ma_ct = '{syscode}'"), ("dmct6", "ma_ct = '{syscode}'"), ("dmct7", "ma_ct = '{syscode}'"),
        ("dmct8", "ma_ct = '{syscode}'"), ("dmct9", "ma_ct = '{syscode}'"), ("dmctkks", "ma_ct = '{syscode}'"),
        ("dmcttransfer", "ma_ct = '{syscode}'"), ("dmloaict", "ma_ct = '{syscode}'"), ("dmmagd", "ma_ct = '{syscode}'"),
        ("dmmd", "ma_ct = '{syscode}'"), ("dmttct", "ma_ct = '{syscode}'"), ("dmttct2", "ma_ct = '{syscode}'"),
        ("dmxlct", "ma_ct = '{syscode}'"), ("dmxlct2", "ma_ct = '{syscode}'"),
        ("gndmttcttg", "loai_duyet in (select loai_duyet from gndmloaiduyet where ma_ct='{syscode}')"),
        ("sodmttcttg", "loai_duyet in (select loai_duyet from sodmloaiduyet where ma_ct='{syscode}')"),
        ("gndmloaiduyet", "ma_ct = '{syscode}'"), ("sodmloaiduyet", "ma_ct = '{syscode}'"),
        ("syscredit", "ma_ct = '{syscode}'"), ("sysflowinfo", "ma_ct = '{syscode}'"), ("voucher", "ma_ct = '{syscode}'"),
        ("vouchertypeinfo", "ma_ct = '{syscode}'"), ("systransfer", "xcode = '{syscode}'"),
        ("sysspdetailinfo", "xid = '{syscode}'"), ("sysspmasterinfo", "xid = '{syscode}'"),
        ("dmctduyet", "ma_ct = '{syscode}'"),
        ("sysgendata", "basetable LIKE N'%{table_index}%' AND primefilegroup = 'Voucher'"),
    };

    private static readonly (string Table, string Where)[] FboSys =
    {
        ("wcommand", "syscode = '{syscode}'"), ("command", "syscode = '{syscode}'"), ("voucherinfo", "ma_ct = '{syscode}'"),
        ("sysvouchertype", "ma_ct = '{syscode}'"), ("freefunctions", "ma_ct = '{syscode}'"),
        ("syscheckfields", "controller in ('{controller}Detail', '{controller}Tran', '{controller}Tax', '{controller}Charge', '{controller}Deductible')"),
        ("freecolumns", "controller in ('{controller}Detail', '{controller}Tran', '{controller}Tax', '{controller}Charge', '{controller}Deductible')"),
        ("freecolumns_default", "controller = '{controllerM}'"), ("freelistcolumns", "controller = '{controllerM}'"),
        ("sysfilterdeclares", "controller = '{controllerM}'"), ("notifygroup", "controller = '{controllerM}'"),
    };

    private static readonly (string Table, string Where)[] FbffApp =
    {
        ("dmct", "ma_ct = '{syscode}'"), ("dmloaict", "ma_ct = '{syscode}'"), ("dmmagd", "ma_ct = '{syscode}'"),
        ("dmttct", "ma_ct = '{syscode}'"), ("dmxlct", "ma_ct = '{syscode}'"), ("sysflowinfo", "ma_ct = '{syscode}'"),
        ("sysinventory", "CHARINDEX('{syscode}', xvoucher) > 0"), ("sysspmasterinfo", "xid = '{syscode}'"),
        ("sysspdetailinfo", "xid = '{syscode}'"),
    };

    private static readonly (string Table, string Where)[] FbffSys =
    {
        ("message", "form = '{sysid}'"), ("reports", "form = '{sysid}'"), ("repinfo", "form = '{sysid}'"),
        ("command", "sysid = '{sysid}' AND syscode = '{syscode}'"), ("voucherinfo", "ma_ct = '{syscode}'"),
        ("grid", " code IN ({2})"), ("popup", "CHARINDEX('{syscode}', vouchers) > 0"),
        ("sysgridinfo", "ma_ct = '{syscode}'"), ("sysstatusinfo", "ma_ct = '{syscode}'"), ("sysuserdef", "ma_ct = '{syscode}'"),
        ("freecodeinfo", "CHARINDEX('{syscode}', vouchers) > 0"), ("freefieldsinfo", "CHARINDEX('{syscode}', vouchers) > 0"),
    };

    /// <param name="item">Node WCommand đang chọn. <see cref="WCommandItem.SysCode"/> là mã chứng từ.</param>
    /// <param name="fbff">true = dùng danh sách FBFF (sản phẩm dạng command) thay cho FBO.</param>
    public async Task<Result> BuildAsync(WCommandItem item, bool fbff, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        var syscode = (item.SysCode ?? "").Trim();
        var sysid = (item.SysId ?? "").Trim();
        var name = (item.Bar ?? "").Trim();

        var badges = new List<Badge>();
        string? url = null;
        var tableIndex = "";

        // Huy hiệu + table_index: đọc dmct9 (App) của chứng từ. Không có dmct9 / không kết nối được thì bỏ qua (vẫn liệt kê bảng).
        if (!fbff && syscode.Length > 0)
        {
            try
            {
                await using var conn = _connections.CreateConnection(useSysDatabase: false);
                await conn.OpenAsync(ct);
                await using var cmd = new SqlCommand(
                    "IF OBJECT_ID('dbo.dmct9') IS NOT NULL SELECT [c$], [m$], [d$], [i$], url FROM dbo.dmct9 WHERE ma_ct = @c", conn) { CommandTimeout = 30 };
                cmd.Parameters.AddWithValue("@c", syscode);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (await r.ReadAsync(ct))
                {
                    string Val(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i))?.Trim() ?? "";
                    var c = Val(0); var m = Val(1); var d = Val(2); var ii = Val(3); url = Val(4);
                    if (c.Length > 0) badges.Add(BadgeOf("c", c));
                    if (m.Length > 0 && !string.Equals(m, c, StringComparison.Ordinal))
                    {
                        badges.Add(BadgeOf("m", m));
                        // m74$ → 74 (bỏ ký tự đầu, cắt tới dấu $ kế tiếp) — nguồn của {table_index}.
                        if (m.EndsWith("$", StringComparison.Ordinal) && m.Length > 1)
                        {
                            var t = m.Substring(1);
                            var dollar = t.IndexOf('$');
                            tableIndex = dollar >= 0 ? t.Remove(dollar) : t;
                        }
                    }
                    if (d.Length > 0) badges.Add(BadgeOf("d", d));
                    if (ii.Length > 0) badges.Add(BadgeOf("i", ii));
                    if (url.Length == 0) url = null;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* dmct9 không có / lỗi — vẫn liệt kê bảng theo config */ }
            if (badges.Count > 0) badges = await WithMaCtFilterAsync(badges, syscode, ct);
        }

        // Thay chỗ giữ chỗ trong WHERE (y như FCode YQk9u0R06v).
        var controller = sysid.ToUpperInvariant().Replace("TRAN", "");
        string Fill(string where) => where
            .Replace("{syscode}", syscode)
            .Replace("{sysid}", sysid)
            .Replace("{wmenu_id}", (item.WMenuId ?? "").Trim())
            .Replace("{menu_id}", (item.MenuId ?? "").Trim())
            .Replace("{table_index}", tableIndex)
            .Replace("{controller}", controller)
            .Replace("{controllerM}", sysid);

        var appCfg = fbff ? FbffApp : FboApp;
        var sysCfg = fbff ? FbffSys : FboSys;

        var tables = new List<RelTable>();
        await AddExistingAsync(tables, appCfg, sys: false, Fill, warnings, ct);
        await AddExistingAsync(tables, sysCfg, sys: true, Fill, warnings, ct);

        if (tables.Count == 0)
            warnings.Add("Không thấy bảng liên quan nào tồn tại trong database (đúng danh sách chuẩn FCode nhưng chưa tạo ở database này).");

        return new Result(syscode, sysid, name, url, badges, tables, warnings);
    }

    /// <summary>Huy hiệu → bảng như FCode (urViewTableData: bấm nhãn mở tab Table với tên + "000000" nếu có "$"): m81$ → m81$000000, dmcc → dmcc.</summary>
    private static Badge BadgeOf(string kind, string text) =>
        new(kind, text, text.IndexOf('$') > 0 ? text + "000000" : text, "");

    /// <summary>Thêm điều kiện ma_ct = chứng từ cho huy hiệu nào mà bảng (hoặc các bảng kỳ cùng tiền tố, vd m81$202601) có cột ma_ct —
    /// bảng chứng từ gộp mọi kỳ có thể rất lớn, lọc đúng chứng từ cho nhẹ. Không kiểm được thì giữ nguyên (không lọc).</summary>
    private async Task<List<Badge>> WithMaCtFilterAsync(List<Badge> badges, string syscode, CancellationToken ct)
    {
        try
        {
            await using var conn = _connections.CreateConnection(useSysDatabase: false);
            await conn.OpenAsync(ct);
            var outList = new List<Badge>();
            foreach (var b in badges)
            {
                var dollar = b.Table.IndexOf('$');
                var pattern = dollar > 0 ? b.Table[..(dollar + 1)] + "[0-9]%" : b.Table;
                await using var cmd = new SqlCommand(
                    "SELECT TOP 1 1 FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id WHERE c.name = 'ma_ct' AND (t.name = @t OR t.name LIKE @p)", conn) { CommandTimeout = 30 };
                cmd.Parameters.AddWithValue("@t", b.Table);
                cmd.Parameters.AddWithValue("@p", pattern);
                var has = await cmd.ExecuteScalarAsync(ct) is not null;
                outList.Add(has ? b with { Where = "ma_ct = '" + syscode.Replace("'", "''") + "'" } : b);
            }
            return outList;
        }
        catch (OperationCanceledException) { throw; }
        catch { return badges; }
    }

    /// <summary>Key số dòng — khớp ckOf ở relativetables.html: "A|table|where" (App) / "S|table|where" (Sys).</summary>
    public static string CountKey(RelTable t) => (t.Sys ? "S" : "A") + "|" + t.Table + "|" + t.Where;

    /// <summary>Số dòng (theo WHERE) của từng bảng liên quan — nạp NỀN để trang điền vào danh sách (không chặn danh sách). Key = <see cref="CountKey"/>:
    /// số ≥ 0 = số dòng; -1 = không đếm được (bảng/WHERE lỗi, quá giờ, hoặc mất kết nối). Gom theo database 1 kết nối, đếm lần lượt, mỗi câu
    /// CommandTimeout 15s để bảng lớn không treo cả loạt. COUNT_BIG để bảng lớn không tràn int.</summary>
    public async Task<Dictionary<string, long>> CountAllAsync(IReadOnlyList<RelTable> tables, CancellationToken ct = default)
    {
        var result = new Dictionary<string, long>();
        foreach (var t in tables) result[CountKey(t)] = -1;   // mặc định "?"; đếm được thì ghi đè (trang hết hiện "…")
        foreach (var grp in tables.GroupBy(t => t.Sys))
        {
            try
            {
                await using var conn = _connections.CreateConnection(useSysDatabase: grp.Key);
                await conn.OpenAsync(ct);
                foreach (var t in grp)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var where = string.IsNullOrWhiteSpace(t.Where) ? "" : " WHERE " + t.Where;
                        await using var cmd = new SqlCommand("SELECT COUNT_BIG(*) FROM [dbo].[" + t.Table.Replace("]", "]]") + "]" + where, conn) { CommandTimeout = 15 };
                        if (await cmd.ExecuteScalarAsync(ct) is long n) result[CountKey(t)] = n;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* bảng/WHERE lỗi hoặc quá giờ — để -1 ("?"), danh sách vẫn hiện bình thường */ }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* không mở được database — cả nhóm để -1 */ }
        }
        return result;
    }

    /// <summary>Lọc danh sách config chỉ giữ bảng có thật trong database (dbo.&lt;tên&gt;), giữ nguyên thứ tự config.</summary>
    private async Task AddExistingAsync(List<RelTable> into, (string Table, string Where)[] cfg, bool sys,
        Func<string, string> fill, List<string> warnings, CancellationToken ct)
    {
        try
        {
            await using var conn = _connections.CreateConnection(useSysDatabase: sys);
            await conn.OpenAsync(ct);
            var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Một câu: lấy tên bảng/view thật trong danh sách config.
            var names = cfg.Select((t, i) => "@t" + i).ToArray();
            await using var cmd = new SqlCommand(
                "SELECT name FROM sys.objects WHERE type IN ('U','V') AND name IN (" + string.Join(",", names) + ")", conn) { CommandTimeout = 30 };
            for (var i = 0; i < cfg.Length; i++) cmd.Parameters.AddWithValue("@t" + i, cfg[i].Table);
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) exists.Add(r.GetString(0));
            foreach (var (table, where) in cfg)
                if (exists.Contains(table)) into.Add(new RelTable(sys, table, fill(where)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warnings.Add((sys ? "Sys Data" : "App Data") + ": không kiểm được bảng tồn tại — " + ex.Message); }
    }
}
