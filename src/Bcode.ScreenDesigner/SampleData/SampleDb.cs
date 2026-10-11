using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.ScreenDesigner.SampleData;

/// <summary>
/// Đọc DB FastBusiness trên MÁY NÀY để lấy dữ liệu mẫu — CHỈ SELECT (ApplicationIntent=ReadOnly, timeout ngắn), tên bảng/cột lấy từ XML đều được kiểm
/// bằng regex trước khi ghép vào câu lệnh. Không bao giờ ghi.
/// </summary>
public sealed class SampleDb : IDisposable
{
    private static readonly Regex Ident = new(@"^[A-Za-z_][A-Za-z0-9_$]*$", RegexOptions.Compiled);
    // Chỉ SQL Server trên máy này. Không dò LocalDB: máy không cài thì chờ hết ~4s mới báo lỗi, và DB FBO không nằm ở đó.
    private static readonly string[] LocalServers = { ".", @".\SQLEXPRESS" };

    private readonly SqlConnection _cn;
    private readonly Dictionary<string, HashSet<string>?> _cols = new(StringComparer.OrdinalIgnoreCase);
    public string Database { get; }

    private SampleDb(SqlConnection cn, string db) { _cn = cn; Database = db; }

    private static string Cs(string server, string db) => new SqlConnectionStringBuilder
    {
        DataSource = server, InitialCatalog = db, IntegratedSecurity = true, TrustServerCertificate = true,
        ConnectTimeout = 4, ApplicationIntent = ApplicationIntent.ReadOnly, ApplicationName = "Bcode Screen Designer (sample data)",
    }.ConnectionString;

    /// <summary>Chỉ mở DB trên máy này (".", ".\SQLEXPRESS") — không đụng server khách.</summary>
    public static SampleDb? TryOpen(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var p = id.Split('|');
        if (p.Length != 2 || !LocalServers.Contains(p[0], StringComparer.OrdinalIgnoreCase) || !Ident.IsMatch(p[1])) return null;
        try { var cn = new SqlConnection(Cs(p[0], p[1])); cn.Open(); return new SampleDb(cn, p[1]); }
        catch { return null; }
    }

    /// <summary>Các DB FastBusiness (có dmtk + dmvt) trên SQL Server của máy này.</summary>
    public static List<SampleSource> ListLocal()
    {
        // Dò các instance song song (instance không có thì chờ hết ConnectTimeout) rồi gộp theo thứ tự cố định.
        return LocalServers.AsParallel().AsOrdered().SelectMany(ListServer).ToList();
    }

    private static List<SampleSource> ListServer(string server)
    {
        var list = new List<SampleSource>();
        {
            try
            {
                using var cn = new SqlConnection(Cs(server, "master"));
                cn.Open();
                var dbs = new List<string>();
                using (var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE database_id > 4 AND state = 0 ORDER BY name", cn) { CommandTimeout = 5 })
                using (var rd = cmd.ExecuteReader()) while (rd.Read()) dbs.Add(rd.GetString(0));
                foreach (var db in dbs.Where(d => Ident.IsMatch(d)))
                {
                    try
                    {
                        using var cmd = new SqlCommand($@"IF OBJECT_ID('[{db}].dbo.dmtk') IS NULL OR OBJECT_ID('[{db}].dbo.dmvt') IS NULL SELECT -1, -1
                            ELSE SELECT (SELECT SUM(p.rows) FROM [{db}].sys.partitions p WHERE p.object_id = OBJECT_ID('[{db}].dbo.dmvt') AND p.index_id IN (0,1)),
                                        (SELECT SUM(p.rows) FROM [{db}].sys.partitions p WHERE p.object_id = OBJECT_ID('[{db}].dbo.dmkh') AND p.index_id IN (0,1))", cn) { CommandTimeout = 5 };
                        using var rd = cmd.ExecuteReader();
                        if (!rd.Read() || rd.IsDBNull(0) || Convert.ToInt64(rd[0]) < 0) continue;
                        var items = Convert.ToInt32(rd[0]);
                        var cust = rd.IsDBNull(1) ? 0 : Convert.ToInt32(rd[1]);
                        var hasData = items > 0 || cust > 0;
                        list.Add(new SampleSource(server + "|" + db, server, db, hasData, db.StartsWith("Release", StringComparison.OrdinalIgnoreCase) || !hasData, items, cust));
                    }
                    catch { /* không đọc được DB này — bỏ qua */ }
                }
            }
            catch { /* không có instance này */ }
        }
        return list;
    }

    public HashSet<string>? Columns(string table)
    {
        if (!Ident.IsMatch(table)) return null;
        if (_cols.TryGetValue(table, out var c)) return c;
        c = null;
        try
        {
            var dt = Query("SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(@t)", ("@t", "dbo.[" + table + "]"));
            if (dt.Rows.Count > 0) c = new HashSet<string>(dt.Rows.Cast<DataRow>().Select(r => (string)r[0]), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return _cols[table] = c;
    }

    public bool Has(string table, string column) => Columns(table)?.Contains(column) == true;

    /// <summary>Vài dòng ngẫu nhiên của 1 danh mục (đang dùng: status = '1' nếu có cột status).</summary>
    public List<Dictionary<string, object?>> Random(string table, int top, string? where = null, params (string, object)[] args)
    {
        if (Columns(table) is not { } cols) return new();
        var conds = new List<string>();
        if (cols.Contains("status")) conds.Add("status = '1'");
        if (where is not null) conds.Add(where);
        var sql = $"SELECT TOP (@n) * FROM dbo.[{table}]" + (conds.Count > 0 ? " WHERE " + string.Join(" AND ", conds) : "") + " ORDER BY NEWID()";
        return Rows(Query(sql, args.Append(("@n", (object)top)).ToArray()));
    }

    /// <summary>1 dòng danh mục theo mã.</summary>
    public Dictionary<string, object?>? Find(string table, string codeColumn, string value)
    {
        if (!Has(table, codeColumn) || !Ident.IsMatch(codeColumn)) return null;
        return Rows(Query($"SELECT TOP 1 * FROM dbo.[{table}] WHERE [{codeColumn}] = @v", ("@v", value))).FirstOrDefault();
    }

    /// <summary>Tài khoản chi tiết (không có tài khoản con) đầu tiên bắt đầu bằng <paramref name="prefix"/>.</summary>
    public string? LeafAccount(string prefix)
    {
        if (Columns("dmtk") is not { } c || !c.Contains("tk_me")) return null;
        var dt = Query("SELECT TOP 1 RTRIM(a.tk) FROM dbo.dmtk a WHERE a.tk LIKE @p AND NOT EXISTS (SELECT 1 FROM dbo.dmtk b WHERE b.tk_me = a.tk) ORDER BY a.tk", ("@p", prefix + "%"));
        return dt.Rows.Count > 0 ? (string)dt.Rows[0][0] : null;
    }

    /// <summary>Chứng từ gần nhất có thật của <paramref name="masterBase"/> (vd "m81"): phần đầu + chi tiết ở bảng "d" cùng kỳ. null = không có.</summary>
    public (Dictionary<string, object?> Master, List<Dictionary<string, object?>> Detail)? LatestVoucher(string masterBase, string? voucherId, int maxRows)
    {
        if (!Ident.IsMatch(masterBase) || masterBase.Length < 2 || masterBase[0] is not ('m' or 'M')) return null;
        var detailBase = "d" + masterBase[1..];
        // Lọc theo tên ở sys.tables (DB FBO có hàng chục nghìn bảng kỳ — nối sys.partitions mất ~2s), kỳ mới nhất trước; bảng trống thì SELECT TOP 1 trả ngay.
        var parts = Query("SELECT name FROM sys.tables WHERE name LIKE @p ORDER BY name DESC", ("@p", masterBase + "$[0-9][0-9][0-9][0-9][0-9][0-9]"));
        foreach (DataRow pr in parts.Rows)
        {
            var mt = (string)pr[0];
            var dt = detailBase + mt[masterBase.Length..];
            if (!Ident.IsMatch(mt) || Columns(mt) is not { } mc) continue;
            var where = voucherId is not null && mc.Contains("ma_ct") && Ident.IsMatch(voucherId) ? " WHERE ma_ct = @id" : "";
            var order = mc.Contains("ngay_ct") ? " ORDER BY ngay_ct DESC" + (mc.Contains("so_ct") ? ", so_ct DESC" : "") : "";
            var m = Rows(Query($"SELECT TOP 1 * FROM dbo.[{mt}]{where}{order}", ("@id", voucherId ?? ""))).FirstOrDefault();
            if (m is null) continue;
            var detail = new List<Dictionary<string, object?>>();
            if (Columns(dt) is { } dc && dc.Contains("stt_rec") && m.TryGetValue("stt_rec", out var sr) && sr is not null)
                detail = Rows(Query($"SELECT TOP (@n) * FROM dbo.[{dt}] WHERE stt_rec = @s" + (dc.Contains("line_nbr") ? " ORDER BY line_nbr" : ""), ("@n", maxRows), ("@s", sr)));
            return (m, detail);
        }
        return null;
    }

    private DataTable Query(string sql, params (string Name, object Value)[] args)
    {
        using var cmd = new SqlCommand(sql, _cn) { CommandTimeout = 8 };
        foreach (var (n, v) in args) if (sql.Contains(n)) cmd.Parameters.AddWithValue(n, v);
        var dt = new DataTable();
        using (var rd = cmd.ExecuteReader()) dt.Load(rd);
        return dt;
    }

    private static List<Dictionary<string, object?>> Rows(DataTable dt) =>
        dt.Rows.Cast<DataRow>().Select(r => dt.Columns.Cast<DataColumn>().ToDictionary(c => c.ColumnName, c => r[c] is DBNull ? null : r[c] is string s ? (object)s.TrimEnd() : r[c], StringComparer.OrdinalIgnoreCase)).ToList();

    public void Dispose() => _cn.Dispose();
}
