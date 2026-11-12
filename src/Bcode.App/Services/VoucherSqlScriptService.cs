using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>1 nhóm dòng cấu hình của chứng từ nguồn trong 1 bảng (DELETE + INSERT cho mã mới).</summary>
public record VoucherSqlBlock(bool Sys, string Table, string Where, int Rows, string Script, bool Proc = false);

/// <summary>
/// Phần "Sql Declaration" của Tạo chứng từ — làm theo đúng gói FCode sinh ra khi tạo chứng từ (Script\App\AppScript.sql +
/// Script\Sys\SysScript.sql):
/// <list type="bullet">
/// <item>Chép dòng cấu hình của chứng từ nguồn sang mã mới ở các bảng khai báo theo ma_ct / controller (dmct, dmct0..9, dmloaict,
///   dmmagd, dmttct, dmxlct, voucherinfo, sysvouchertype, freefunctions, freecolumns, sysfilterdeclares...), mỗi bảng 1 khối
///   DELETE (theo mã mới) + INSERT; giá trị chuỗi được đổi mã / SysID / tiền tố / số bảng như file.</item>
/// <item>Tạo bảng mới: CREATE TABLE theo đúng định nghĩa bảng nguồn (kiểu, null, default, identity, khoá chính, index) cho mọi bảng
///   [chữ]{số}$000000 và $log, sysgendata, rồi khối FastBusiness$App$Dynamic$AddTable tạo các kỳ trong khoảng dmstt.ngay_gh1..2.</item>
/// </list>
/// Chỉ sinh script — không chạy gì trên database. Bảng nào không có trong database thì bỏ qua.
/// </summary>
public class VoucherSqlScriptService
{
    private readonly DbConnectionService _connections;
    public VoucherSqlScriptService(DbConnectionService connections) => _connections = connections;

    /// <summary>(bảng, cột khoá, loại khoá): "code" = mã ct, "sys" = SysID, "ctrl" = danh sách controller của chứng từ, "approve" = loại duyệt, "codeapprove" = mã ct nhưng chỉ chép khi có khai loại duyệt.</summary>
    private static readonly (string Table, string Key, string Kind)[] ConfigTables =
    {
        ("voucherinfo", "ma_ct", "code"), ("sysvouchertype", "ma_ct", "code"), ("freefunctions", "ma_ct", "code"),
        ("freecolumns", "controller", "ctrl"), ("freecolumns_default", "controller", "sys"), ("freelistcolumns", "controller", "sys"),
        ("sysfilterdeclares", "controller", "sys"), ("notifygroup", "controller", "sys"),
        ("dmct", "ma_ct", "code"), ("dmct0", "ma_ct", "code"), ("dmct4", "ma_ct", "code"), ("dmct5", "ma_ct", "code"),
        ("dmct6", "ma_ct", "code"), ("dmct7", "ma_ct", "code"), ("dmct8", "ma_ct", "code"), ("dmct9", "ma_ct", "code"),
        ("dmctkks", "ma_ct", "code"), ("dmcttransfer", "ma_ct", "code"), ("dmloaict", "ma_ct", "code"), ("dmmagd", "ma_ct", "code"),
        ("dmmd", "ma_ct", "code"), ("dmttct", "ma_ct", "code"), ("dmttct2", "ma_ct", "code"), ("dmxlct", "ma_ct", "code"),
        ("dmxlct2", "ma_ct", "code"), ("gndmttcttg", "loai_duyet", "approve"), ("sodmttcttg", "loai_duyet", "approve"),
        ("gndmloaiduyet", "ma_ct", "codeapprove"), ("syscredit", "ma_ct", "code"), ("sysflowinfo", "ma_ct", "code"), ("voucher", "ma_ct", "code"),
        ("sysspdetailinfo", "xid", "code"), ("dmctduyet", "ma_ct", "codeapprove"),
        // Khai báo loại duyệt theo 3 nhóm (dm = mua hàng, so = bán hàng, gn = chung). Không chép người duyệt / người nhận / uỷ quyền
        // (dmduyet, dmnttduyet, dmuqduyet, dmnsdtd...): đó là phân công người cụ thể, khai lại cho loại duyệt mới.
        ("dmloaiduyet", "loai_duyet", "approve"), ("sodmloaiduyet", "loai_duyet", "approve"), ("dmttcttg", "loai_duyet", "approve"),
        ("dmquyen", "loai_duyet", "approve"), ("sodmquyen", "loai_duyet", "approve"), ("gndmquyen", "loai_duyet", "approve"),
        ("dmquyenct", "loai_duyet", "approve"), ("sodmquyenct", "loai_duyet", "approve"), ("gndmquyenct", "loai_duyet", "approve"),
        ("dmquyentd", "loai_duyet", "approve"), ("sodmquyentd", "loai_duyet", "approve"), ("gndmquyentd", "loai_duyet", "approve"),
        ("dmquyentdct", "loai_duyet", "approve"), ("sodmquyentdct", "loai_duyet", "approve"), ("gndmquyentdct", "loai_duyet", "approve"),
        ("dmquyentdct2", "loai_duyet", "approve"), ("sodmquyentdct2", "loai_duyet", "approve"), ("gndmquyentdct2", "loai_duyet", "approve"),
        ("dmkieuduyet", "loai_duyet", "approve"), ("sodmkieuduyet", "loai_duyet", "approve"), ("gndmkieuduyet", "loai_duyet", "approve"),
        ("ctkieuduyet", "loai_duyet", "approve"), ("soctkieuduyet", "loai_duyet", "approve"), ("gnctkieuduyet", "loai_duyet", "approve"),
        ("dmthaydoiloai", "loai_duyet", "approve"), ("sodmthaydoiloai", "loai_duyet", "approve"), ("gndmthaydoiloai", "loai_duyet", "approve"),
    };

    /// <summary>Đổi 1 giá trị chuỗi của dòng cấu hình sang chứng từ mới (như nội dung file + số bảng trong tên CT48 / PH48 / d48log, id 08048...).</summary>
    public static string MapValue(string table, string col, string value, VoucherCloneSpec s, string? srcApprove, string? dstApprove)
    {
        if (srcApprove is { Length: > 0 } && dstApprove is { Length: > 0 } && col.Equals("loai_duyet", StringComparison.OrdinalIgnoreCase)
            && value.Trim().Equals(srcApprove, StringComparison.OrdinalIgnoreCase)) return dstApprove;
        if (s.DstNotify.Length > 0 && table.Equals("notifygroup", StringComparison.OrdinalIgnoreCase) && col.Equals("code", StringComparison.OrdinalIgnoreCase)
            && value.Trim().Equals(s.SrcNotify, StringComparison.OrdinalIgnoreCase)) return s.DstNotify;
        if (value.Trim().Equals(s.SrcCode, StringComparison.OrdinalIgnoreCase)) return s.DstCode;
        // Loại duyệt dùng chung nhiều chứng từ: ma_ct là danh sách (PO1, PO2).
        if (col.Equals("ma_ct", StringComparison.OrdinalIgnoreCase) && table.EndsWith("loaiduyet", StringComparison.OrdinalIgnoreCase))
            return Regex.Replace(value, @"(?<![A-Za-z0-9_])" + Regex.Escape(s.SrcCode) + @"(?![A-Za-z0-9_])", s.DstCode.Replace("$", "$$"), RegexOptions.IgnoreCase);
        // Tên chứng từ (dmct.ten_ct / ten_ct2 / tieu_de_ct viết HOA, freefunctions.description...): trùng tiêu đề nguồn → tiêu đề mới, giữ kiểu HOA.
        static bool IsUpper(string x) => x.Any(char.IsLetter) && x == x.ToUpperInvariant();
        foreach (var (src, dst) in new[] { (s.SrcTitleV, s.DstTitleV), (s.SrcTitleE, s.DstTitleE) })
            if (src.Length > 0 && dst.Length > 0 && value.Trim().Equals(src.Trim(), StringComparison.CurrentCultureIgnoreCase))
                return IsUpper(value) ? dst.ToUpper(CultureInfo.CurrentCulture) : dst;
        var v = VoucherCloneService.Transform(value, s);
        if (s.NewTables && s.SrcTableNo.Length > 0 && s.DstTableNo.Length > 0)
        {
            var no = Regex.Escape(s.SrcTableNo);
            v = Regex.Replace(v, @"\b(CT|PH)" + no + @"\b", "${1}" + s.DstTableNo, RegexOptions.IgnoreCase);       // m_ctdbf / m_phdbf
            v = Regex.Replace(v, @"\b([a-z]{1,3})" + no + @"log\b", "${1}" + s.DstTableNo + "log", RegexOptions.IgnoreCase); // dmct5.xtable
            if (table.Equals("freefunctions", StringComparison.OrdinalIgnoreCase) && col.Equals("id", StringComparison.OrdinalIgnoreCase)
                && Regex.IsMatch(v, @"^\d{5}$") && v.EndsWith(s.SrcTableNo.PadLeft(3, '0'), StringComparison.Ordinal))
                v = v[..^3] + s.DstTableNo.PadLeft(3, '0');                                                                // 08048 → 08164 (2 số nhóm + 3 số bảng)
        }
        return v;
    }

    private static string Lit(object? v, string sqlType) => v switch
    {
        null or DBNull => "NULL",
        string str => "N'" + str.Replace("'", "''") + "'",
        bool b => b ? "1" : "0",
        DateTime d => "'" + d.ToString("yyyyMMdd HH:mm:ss", CultureInfo.InvariantCulture) + "'",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        Guid g => "'" + g + "'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => "N'" + v.ToString()!.Replace("'", "''") + "'",
    };

    /// <summary>Các khối DELETE + INSERT cấu hình chứng từ (Sys trước, App sau — như FCode). Bảng không có / không có dòng nào: vẫn ra DELETE cho sạch.</summary>
    public async Task<List<VoucherSqlBlock>> ConfigBlocksAsync(VoucherCloneSpec s, IReadOnlyCollection<string> controllers, string? srcApprove, string? dstApprove)
    {
        var blocks = new List<VoucherSqlBlock>();
        // Loại duyệt đích trùng nguồn: script sẽ xoá rồi ghi lại khai báo duyệt của chính chứng từ nguồn → coi như không chép.
        if (string.Equals(srcApprove?.Trim(), dstApprove?.Trim(), StringComparison.OrdinalIgnoreCase)) srcApprove = dstApprove = null;
        async Task<HashSet<string>> TablesOf(bool sys)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var c = _connections.CreateConnection(sys);
            await c.OpenAsync();
            await using var cmd = new SqlCommand("SELECT name FROM sys.tables", c);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) set.Add(r.GetString(0));
            return set;
        }
        var inSys = await TablesOf(true);
        var inApp = await TablesOf(false);

        foreach (var sys in new[] { true, false })   // Sys trước, App sau — như FCode
        {
            await using var conn = _connections.CreateConnection(sys);
            await conn.OpenAsync();
            foreach (var (table, key, kind) in ConfigTables)
            {
                // Bảng có ở DB nào thì lấy DB đó; có ở cả 2 thì bảng hệ thống (free*, sys*, voucherinfo, notifygroup) theo Sys, còn lại theo App.
                var sysTable = table.StartsWith("free", StringComparison.OrdinalIgnoreCase) || table.StartsWith("sys", StringComparison.OrdinalIgnoreCase)
                               || table is "voucherinfo" or "notifygroup";
                bool? home = inSys.Contains(table) && inApp.Contains(table) ? sysTable : inSys.Contains(table) ? true : inApp.Contains(table) ? false : null;
                if (home != sys) continue;
                // Không có mã thông báo mới: chứng từ mới dùng chung nhóm của nguồn, chép dòng sẽ trùng notifygroup.code.
                if (table == "notifygroup" && (s.DstNotify.Length == 0 || s.DstNotify.Equals(s.SrcNotify, StringComparison.OrdinalIgnoreCase))) continue;

                string[] srcKeys, dstKeys;
                switch (kind)
                {
                    case "ctrl":
                        srcKeys = controllers.ToArray();
                        dstKeys = controllers.Select(c => VoucherCloneService.Transform(c, s)).ToArray();
                        break;
                    case "sys": srcKeys = new[] { s.SrcSysId }; dstKeys = new[] { s.DstSysId }; break;
                    case "approve":
                        if (srcApprove is not { Length: > 0 } || dstApprove is not { Length: > 0 }) continue;
                        srcKeys = new[] { srcApprove }; dstKeys = new[] { dstApprove }; break;
                    case "codeapprove":
                        if (srcApprove is not { Length: > 0 } || dstApprove is not { Length: > 0 }) continue;
                        srcKeys = new[] { s.SrcCode }; dstKeys = new[] { s.DstCode }; break;
                    default: srcKeys = new[] { s.SrcCode }; dstKeys = new[] { s.DstCode }; break;
                }
                if (srcKeys.Length == 0) continue;
                var (columns, generated) = await ColumnsAsync(conn, table);
                if (!columns.Contains(key)) continue;   // phiên bản này không có cột khoá: bỏ bảng này, không làm hỏng cả bước

                var where = srcKeys.Length == 1 ? $"{key} = '{dstKeys[0].Replace("'", "''")}'"
                    : $"{key} in ({string.Join(", ", dstKeys.Select(k => "'" + k.Replace("'", "''") + "'"))})";
                var sb = new StringBuilder($"DELETE {table} WHERE  {where}\r\n");
                var rows = 0;
                await using (var cmd = new SqlCommand($"SELECT * FROM dbo.[{table}] WHERE [{key}] IN ({string.Join(", ", srcKeys.Select((_, i) => "@k" + i))})", conn))
                {
                    for (var i = 0; i < srcKeys.Length; i++) cmd.Parameters.AddWithValue("@k" + i, srcKeys[i]);
                    await using var r = await cmd.ExecuteReaderAsync();
                    var cols = Enumerable.Range(0, r.FieldCount).Where(i => !generated.Contains(r.GetName(i)))
                        .Select(i => (Index: i, Name: r.GetName(i), Type: r.GetDataTypeName(i))).ToList();
                    while (await r.ReadAsync())
                    {
                        var vals = cols.Select(c =>
                        {
                            var v = r.IsDBNull(c.Index) ? null : r.GetValue(c.Index);
                            if (v is string str) v = MapValue(table, c.Name, str, s, srcApprove, dstApprove);
                            return Lit(v, c.Type);
                        });
                        sb.Append($"INSERT INTO {table}({string.Join(", ", cols.Select(c => "[" + c.Name + "]"))}) VALUES({string.Join(", ", vals)})\r\n");
                        rows++;
                    }
                }
                blocks.Add(new VoucherSqlBlock(sys, table, where, rows, sb.Append("GO\r\n").ToString()));
            }
        }
        return blocks;
    }

    /// <summary>
    /// Store riêng của chứng từ (<see cref="VoucherCloneSpec.StoreRenames"/>: post / unpost / duyệt / thông báo): tạo store mới theo định nghĩa store nguồn,
    /// nội dung đổi như file (tên store, mã ct, loại duyệt, số bảng...). Tìm ở App trước rồi Sys; không thấy (hoặc mã hoá) thì khối chỉ có ghi chú, Rows = 0.
    /// </summary>
    public async Task<List<VoucherSqlBlock>> StoreBlocksAsync(VoucherCloneSpec s)
    {
        var blocks = new List<VoucherSqlBlock>();
        foreach (var (from, to) in s.StoreRenames)
        {
            string? def = null;
            var sys = false;
            foreach (var inSys in new[] { false, true })
            {
                await using var conn = _connections.CreateConnection(inSys);
                await conn.OpenAsync();
                await using var cmd = new SqlCommand("SELECT m.definition FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.type = 'P' AND m.object_id = OBJECT_ID(@n)", conn);
                cmd.Parameters.AddWithValue("@n", "dbo.[" + from.Replace("]", "]]") + "]");
                if (await cmd.ExecuteScalarAsync() is string d && d.Trim().Length > 0) { def = d; sys = inSys; break; }
            }
            var name = to.Replace("]", "]]");
            var sb = new StringBuilder();
            if (def == null)
                sb.Append($"-- Không thấy store nguồn {from} trong database (hoặc store mã hoá): tự tạo {to} cho chứng từ mới.\r\n");
            else
            {
                sb.Append($"IF OBJECT_ID(N'dbo.[{name}]', 'P') IS NOT NULL DROP PROCEDURE [dbo].[{name}]\r\nGO\r\n");
                sb.Append(VoucherCloneService.Transform(def.Trim(), s)).Append("\r\nGO\r\n");
            }
            blocks.Add(new VoucherSqlBlock(sys, to, from, def == null ? 0 : 1, sb.ToString(), Proc: true));
        }
        return blocks;
    }

    /// <summary>Cột của bảng + các cột không INSERT được: identity, computed, timestamp (SQL Server tự sinh).</summary>
    private static async Task<(HashSet<string> All, HashSet<string> Generated)> ColumnsAsync(SqlConnection conn, string table)
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand(@"
SELECT c.name, CAST(CASE WHEN c.is_identity = 1 OR c.is_computed = 1 OR t.name = 'timestamp' THEN 1 ELSE 0 END AS bit)
FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(@t)", conn);
        cmd.Parameters.AddWithValue("@t", "dbo." + table);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            all.Add(r.GetString(0));
            if (r.GetBoolean(1)) generated.Add(r.GetString(0));
        }
        return (all, generated);
    }

    // ---- Bảng mới ---------------------------------------------------------------------------------------

    /// <summary>Bảng của chứng từ nguồn: [chữ]{số}$000000 và $log (vd c48, m48, m48$log, d48, d48$log, i48, t48...); không phân kỳ thì phz29, ctz29, iz29 (+ $log).</summary>
    public async Task<List<string>> SourceTablesAsync(string tableNo, bool noPartition = false)
    {
        var list = new List<string>();
        if (tableNo.Length == 0) return list;
        await using var conn = _connections.CreateConnection(false);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT name FROM sys.tables ORDER BY name", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var re = noPartition
            ? new Regex("^(?:" + VoucherCloneService.TablePrefixes(tableNo) + ")" + Regex.Escape(tableNo) + @"(\$log)?$", RegexOptions.IgnoreCase)
            : new Regex("^[a-z]{1,3}" + Regex.Escape(tableNo) + @"\$(000000|log)$", RegexOptions.IgnoreCase);
        while (await r.ReadAsync()) { var n = r.GetString(0); if (re.IsMatch(n)) list.Add(n); }
        // c, m, m$log, d, d$log, i, t, t$log... như thứ tự FCode
        return list.OrderBy(n => Order(n)).ThenBy(n => n.EndsWith("$log", StringComparison.OrdinalIgnoreCase)).ThenBy(n => n).ToList();
        static int Order(string n) => "cmdit".IndexOf(char.ToLowerInvariant(n[0])) is var i and >= 0 ? i : 9;
    }

    public static string NewName(string table, string srcNo, string dstNo) =>
        Regex.Replace(table, @"^([a-z]{1,5})" + Regex.Escape(srcNo) + @"(?=\$|$)", "${1}" + dstNo, RegexOptions.IgnoreCase);

    /// <summary>CREATE TABLE + khoá chính + index theo đúng định nghĩa bảng nguồn (dạng nhiều dòng như FCode, không kiểm tra bảng đã có), sysgendata và khối tạo các kỳ.</summary>
    public async Task<string> NewTablesScriptAsync(IReadOnlyList<string> sourceTables, VoucherCloneSpec s)
    {
        var sb = new StringBuilder();
        await using var conn = _connections.CreateConnection(false);
        await conn.OpenAsync();
        foreach (var src in sourceTables)
        {
            var dst = NewName(src, s.SrcTableNo, s.DstTableNo);
            var cols = new List<string>();
            await using (var cmd = new SqlCommand(@"
SELECT c.name, t.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, dc.definition, cc.definition,
       CONVERT(varchar(40), ic.seed_value), CONVERT(varchar(40), ic.increment_value)
FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
WHERE c.object_id = OBJECT_ID(@t) ORDER BY c.column_id", conn))
            {
                cmd.Parameters.AddWithValue("@t", "dbo." + src);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    if (!r.IsDBNull(8)) { cols.Add($"[{r.GetString(0)}] AS {r.GetString(8)}"); continue; }   // cột computed
                    var type = r.GetString(1).ToUpperInvariant();
                    var len = r.GetInt16(2);
                    type = type switch
                    {
                        "CHAR" or "VARCHAR" or "BINARY" or "VARBINARY" => $"{type}({(len == -1 ? "MAX" : len.ToString())})",
                        "NCHAR" or "NVARCHAR" => $"{type}({(len == -1 ? "MAX" : (len / 2).ToString())})",
                        "DECIMAL" or "NUMERIC" => $"{type}({r.GetByte(3)}, {r.GetByte(4)})",
                        _ => type,
                    };
                    var def = new StringBuilder($"[{r.GetString(0)}] {type}");
                    if (r.GetBoolean(6)) def.Append($" IDENTITY({(r.IsDBNull(9) ? "1" : r.GetString(9))}, {(r.IsDBNull(10) ? "1" : r.GetString(10))})");
                    if (!r.GetBoolean(5)) def.Append(" NOT NULL");
                    if (!r.IsDBNull(7)) def.Append(" DEFAULT " + r.GetString(7));
                    cols.Add(def.ToString());
                }
            }
            if (cols.Count == 0) continue;

            var indexes = new List<(string Name, bool Pk, bool Unique, bool Clustered, List<string> Keys, List<string> Includes)>();
            await using (var cmd = new SqlCommand(@"
SELECT i.name, i.is_primary_key, i.is_unique, i.type, c.name, ic.is_descending_key, ic.is_included_column
FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID(@t) AND i.type IN (1, 2) ORDER BY i.index_id, ic.is_included_column, ic.key_ordinal", conn))
            {
                cmd.Parameters.AddWithValue("@t", "dbo." + src);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    var name = r.GetString(0);
                    var ix = indexes.FirstOrDefault(x => x.Name == name);
                    if (ix.Name == null) { ix = (name, r.GetBoolean(1), r.GetBoolean(2), r.GetByte(3) == 1, new(), new()); indexes.Add(ix); }
                    var col = "[" + r.GetString(4) + "]" + (r.GetBoolean(5) ? " DESC" : "");
                    if (r.GetBoolean(6)) ix.Includes.Add(col); else ix.Keys.Add(col);
                }
            }

            sb.AppendLine("/*");
            sb.AppendLine($"IF EXISTS (SELECT * FROM dbo.sysobjects WHERE id = OBJECT_ID(N'dbo.{dst}') AND xType = 'U')");
            sb.AppendLine($"DROP TABLE [dbo].[{dst}]");
            sb.AppendLine("*/");
            sb.AppendLine("GO");
            sb.AppendLine($"CREATE TABLE [dbo].[{dst}] (");
            sb.AppendLine(string.Join("," + Environment.NewLine, cols.Select(c => "    " + c)));
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("GO");
            sb.AppendLine();
            var pk = indexes.FirstOrDefault(x => x.Pk);
            if (pk.Name != null)
            {
                sb.AppendLine($"ALTER TABLE dbo.{dst} ADD CONSTRAINT [PK_{dst}] PRIMARY KEY {(pk.Clustered ? "CLUSTERED" : "NONCLUSTERED")}");
                sb.AppendLine("(");
                sb.AppendLine(string.Join("," + Environment.NewLine, pk.Keys.Select(k => "    " + k)));
                sb.AppendLine(") ON [PRIMARY]");
                sb.AppendLine();
                sb.AppendLine("GO");
                sb.AppendLine();
            }
            foreach (var ix in indexes.Where(x => !x.Pk))
            {
                sb.AppendLine($"CREATE {(ix.Unique ? "UNIQUE " : "")}{(ix.Clustered ? "CLUSTERED " : "")}INDEX [{ix.Name}] ON dbo.{dst}({string.Join(", ", ix.Keys)})" +
                              (ix.Includes.Count > 0 ? $" INCLUDE ({string.Join(", ", ix.Includes)})" : "") + " ON [PRIMARY]");
                sb.AppendLine("GO");
                sb.AppendLine();
            }
        }

        // sysgendata: các bảng gốc $000000 để FastBusiness sinh kỳ; rồi tạo luôn các kỳ trong khoảng ngày khoá sổ.
        // Chỉ bảng nguồn thật sự phân kỳ (m, d, i, t...): c / a{số}$000000 là bảng dữ liệu chung mọi kỳ (store chuẩn đọc thẳng c94$000000),
        // đưa vào sysgendata sẽ sinh thêm c{số}$yyyymm thừa.
        var partitioned = await PartitionedBasesAsync(conn, sourceTables, s.SrcTableNo);
        var bases = partitioned.Select(t => NewName(t, s.SrcTableNo, s.DstTableNo)).ToList();
        if (bases.Count > 0)
        {
            // Lọc đúng danh sách bảng mới: LIKE '%70%' sẽ khớp cả m170$000000 của chứng từ khác.
            var names = string.Join(", ", bases.OrderBy(x => x).Select(b => $"N'{b}'"));
            sb.AppendLine($"DELETE sysgendata WHERE  basetable IN ({names}) AND primefilegroup = 'Voucher'");
            foreach (var b in bases.OrderBy(x => x))
                sb.AppendLine($"INSERT INTO sysgendata([basetable], [primetable], [refprimetable], [primefilegroup], [status], [type]) VALUES(N'{b}', N'{b.Replace("$000000", "$")}', N'', N'Voucher', N'1', 1)");
            sb.AppendLine("GO");
            sb.AppendLine();
            var cursor = "currGen" + Regex.Replace(s.DstCode, @"\W", "");
            sb.AppendLine("declare @dFrom smalldatetime, @dTo smalldatetime, @q nvarchar(4000) = '', @dyn char(6)");
            sb.AppendLine("select top 1 @dFrom = ngay_gh1, @dTo = ngay_gh2 FROM dmstt");
            sb.AppendLine("declare @tblName varchar(33), @tblSrc varchar(33)");
            sb.AppendLine($"declare {cursor} cursor for select basetable from sysgendata where basetable IN ({names}) AND primefilegroup = 'Voucher'");
            sb.AppendLine($"open {cursor}");
            sb.AppendLine($"fetch next from {cursor} into @tblSrc");
            sb.AppendLine("while @@fetch_status = 0 begin ");
            sb.AppendLine("set @tblName = replace(@tblSrc, '$000000', '$')");
            sb.AppendLine("exec FastBusiness$App$Dynamic$AddTable @tblSrc, @tblName, '', 'PRIMARY', 1, @dFrom, @dTo, 1");
            sb.AppendLine($"fetch next from {cursor} into  @tblSrc");
            sb.AppendLine("end");
            sb.AppendLine($"close {cursor}");
            sb.AppendLine($"deallocate {cursor}");
            sb.AppendLine("GO");
        }
        return sb.ToString();
    }

    /// <summary>
    /// Bảng $000000 của chứng từ nguồn có phân kỳ: đã khai trong sysgendata của nguồn, hoặc đã có bảng kỳ [chữ]{số}$yyyymm.
    /// Không xác định được (DB chưa sinh kỳ, chưa khai) thì bỏ c / a — 2 bảng không phân kỳ của chứng từ.
    /// </summary>
    private static async Task<List<string>> PartitionedBasesAsync(SqlConnection conn, IReadOnlyList<string> sourceTables, string srcNo)
    {
        var bases = sourceTables.Where(t => t.EndsWith("$000000", StringComparison.OrdinalIgnoreCase)).ToList();
        if (bases.Count == 0) return bases;
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using (var cmd = new SqlCommand("IF OBJECT_ID('dbo.sysgendata') IS NOT NULL SELECT basetable FROM sysgendata WHERE primefilegroup = 'Voucher' AND basetable LIKE @p", conn))
            {
                cmd.Parameters.AddWithValue("@p", "%" + srcNo + "$000000");
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync()) found.Add(r.GetString(0).Trim());
            }
            var period = new Regex("^([a-z]{1,3}" + Regex.Escape(srcNo) + @")\$\d{6}$", RegexOptions.IgnoreCase);
            await using (var cmd = new SqlCommand("SELECT name FROM sys.tables WHERE name LIKE @p", conn))
            {
                cmd.Parameters.AddWithValue("@p", "%" + srcNo + "$[0-9][0-9][0-9][0-9][0-9][0-9]");
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    if (period.Match(r.GetString(0)) is { Success: true } m && !r.GetString(0).EndsWith("$000000")) found.Add(m.Groups[1].Value + "$000000");
            }
        }
        catch { /* không đọc được: dùng quy tắc bỏ c / a */ }
        var result = bases.Where(found.Contains).ToList();
        return result.Count > 0
            ? result
            : bases.Where(t => !Regex.IsMatch(t, "^[ca]" + Regex.Escape(srcNo) + @"\$", RegexOptions.IgnoreCase)).ToList();
    }

    /// <summary>Loại duyệt (loai_duyet) của chứng từ nguồn trong gndmloaiduyet / sodmloaiduyet / dmloaiduyet, nếu có.</summary>
    public async Task<string?> SourceApproveTypeAsync(string code)
    {
        foreach (var sys in new[] { false, true })
            foreach (var table in new[] { "gndmloaiduyet", "sodmloaiduyet", "dmloaiduyet" })
            {
                try
                {
                    await using var conn = _connections.CreateConnection(sys);
                    await conn.OpenAsync();
                    await using var cmd = new SqlCommand($"IF COL_LENGTH('dbo.{table}', 'loai_duyet') IS NOT NULL AND COL_LENGTH('dbo.{table}', 'ma_ct') IS NOT NULL SELECT TOP 1 loai_duyet FROM dbo.{table} WHERE ma_ct = @c", conn);
                    cmd.Parameters.AddWithValue("@c", code);
                    if (await cmd.ExecuteScalarAsync() is string v && v.Trim().Length > 0) return v.Trim();
                }
                catch { /* thử bảng / DB khác */ }
            }
        return null;
    }
}
