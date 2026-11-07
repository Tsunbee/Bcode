using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// "🎲 Điền mẫu" ở tab FSG FBO (Ctrl+4): với các ô TRỐNG của chứng từ / danh mục đang mở, chọn giá trị mẫu từ dữ liệu CÓ THẬT trong database App của dự án
/// để form lưu được (mã tra cứu phải tồn tại). CHỈ ĐỌC — SELECT TOP … WITH (NOLOCK), không ghi / không đổi gì trong database.
///  • Mã (ma_xxx, tk, tk_xxx…): lấy từ danh mục tương ứng — ma_kh → dmkh, ma_vt → dmvt, tk → dmtk… (ưu tiên dòng status = 1).
///  • Ngày (cột kiểu ngày / tên ngay_…): hôm nay, dạng dd/MM/yyyy như ô ngày của FBO.
///  • Số / chữ khác: lấy 1 giá trị đã có trong bảng chứa cột đó (ưu tiên bảng dữ liệu, không phải danh mục).
///  • Bỏ qua: stt_rec, so_ct, ma_ct, status, datetime/user_id hệ thống, và ten_xxx khi form có ma_xxx (FBO tự điền tên khi tra mã).
/// </summary>
public static class FboSampleDataService
{
    /// <param name="Grid">Ô thuộc lưới chi tiết (lấy giá trị hay dùng từ bảng của lưới, vd d21$…, thay vì bảng chính m21$…).</param>
    public sealed record Field(string Name, int MaxLength, bool Grid = false);
    public sealed record Pick(string Field, string Value, string Source);

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
    {
        "stt_rec", "stt_rec0", "stt_rec_hd", "so_ct", "ma_ct", "ma_ct0", "status", "datetime0", "datetime2", "user_id0", "user_id2", "ma_dvcs", "line_nbr",
    };

    public static bool IsKey(string f) =>
        f.StartsWith("ma_", StringComparison.OrdinalIgnoreCase) || f.Equals("tk", StringComparison.OrdinalIgnoreCase)
        || f.StartsWith("tk_", StringComparison.OrdinalIgnoreCase);

    private static bool IsDateType(string t) => t is "date" or "datetime" or "smalldatetime" or "datetime2" or "datetimeoffset";
    private static bool IsNumType(string t) => t is "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real";

    /// <summary>Bảng nên lấy giá trị cho <paramref name="field"/> trong số các bảng có cột đó. null = không có bảng nào.</summary>
    public static string? ChooseTable(string field, IReadOnlyCollection<string> tables) => RankTables(field, tables).FirstOrDefault();

    /// <summary>Các bảng có cột <paramref name="field"/>, xếp theo độ hợp (thử lần lượt tới khi có dữ liệu):
    /// mã → danh mục cùng hậu tố (ma_kh → dmkh) → danh mục khác → bảng khác; còn lại → bảng dữ liệu trước danh mục.</summary>
    public static List<string> RankTables(string field, IReadOnlyCollection<string> tables)
    {
        static bool Dm(string t) => t.StartsWith("dm", StringComparison.OrdinalIgnoreCase);
        static bool Plain(string t) => !t.Contains('$') && !t.StartsWith("sys", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("wrk", StringComparison.OrdinalIgnoreCase);
        var f = field.ToLowerInvariant();
        var suffix = f.StartsWith("tk") ? "tk" : f.StartsWith("ma_") ? f[3..] : "";
        int Rank(string t) => IsKey(field)
            ? (t.Equals("dm" + suffix, StringComparison.OrdinalIgnoreCase) ? 0
              : Dm(t) && Plain(t) && suffix.Length > 0 && t.Contains(suffix, StringComparison.OrdinalIgnoreCase) ? 1
              : Dm(t) && Plain(t) ? 2 : Plain(t) ? 3 : 4)
            : (!Dm(t) && Plain(t) ? 0 : Plain(t) ? 1 : 2);
        return tables.OrderBy(Rank).ThenBy(t => t.Length).ThenBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Chọn giá trị mẫu cho các ô. Ô không tìm được nguồn thì bỏ qua (không bịa). Trả về các giá trị theo đúng thứ tự ô (mã trước để FBO tra tên).</summary>
    /// <param name="primary">Danh mục đang THÊM MỚI (Thêm vật tư / khách hàng…): mã chính của danh mục (ma_vt…) — phải là mã MỚI chưa có
    /// (lấy mã lớn nhất rồi tăng phần số cuối), và tên (ten_vt…) phải điền chứ không chờ FBO tra. null = chứng từ (mọi mã lấy mã có thật).</param>
    /// <param name="form">Tra cứu của từng ô đọc từ controller của form (FboFormLookups) — có thì mã lấy đúng theo điều kiện key của ô.</param>
    /// <param name="formValues">Giá trị đang có trên form (để thay {$%c[x]} trong điều kiện key).</param>
    public static async Task<List<Pick>> PickAsync(SqlConnection conn, IReadOnlyList<Field> fields, string? primary = null,
        FboFormLookups.Form? form = null, IReadOnlyDictionary<string, string>? formValues = null, string sysDatabase = "", CancellationToken ct = default,
        string appDatabase = "")
    {
        var names = fields.Select(f => f.Name).Where(n => !Skip.Contains(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        FboFormLookups.Lookup? LookupOf(string n) => form is not null && form.Fields.TryGetValue(n, out var l) ? l : null;
        bool IsLookup(string n) => IsKey(n) || LookupOf(n) is not null;
        var keys = new HashSet<string>(names.Where(IsKey), StringComparer.OrdinalIgnoreCase);
        string? primaryName = primary is not null && primary.StartsWith("ma_", StringComparison.OrdinalIgnoreCase) ? "ten_" + primary[3..] : null;
        // Ô tên FBO TỰ điền khi tra mã: có controller → đúng các ô là reference của 1 ô tra cứu sẽ được điền (ma_kh reference="ten_kh%l");
        // không đọc được controller → đoán ten_x khi form có ma_x. Tên của chính danh mục đang thêm (ten_vt khi thêm vật tư) thì vẫn điền.
        var autoNames = form is not null
            ? new HashSet<string>(names.Select(LookupOf).Where(l => l is { Reference.Length: > 0 }).Select(l => l!.Reference), StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(names.Where(n => n.StartsWith("ten_", StringComparison.OrdinalIgnoreCase) && keys.Contains("ma_" + n[4..].TrimEnd('2'))), StringComparer.OrdinalIgnoreCase);
        names = names.Where(n => !(autoNames.Contains(n) && !(primaryName is not null && n.TrimEnd('2').Equals(primaryName, StringComparison.OrdinalIgnoreCase)))).ToList();
        // Danh mục phân cấp đang thêm: mã mẹ (ma_vv_me…) thường phải là đầu của mã con — mã mới không theo cây nên để trống, người dùng tự chọn.
        if (primary is not null) names.RemoveAll(n => n.EndsWith("_me", StringComparison.OrdinalIgnoreCase));
        if (names.Count == 0) return new();

        // Cột → (bảng, kiểu) trong các BẢNG thật của database App; kèm cột status để lọc danh mục còn dùng.
        var cols = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);   // field -> table -> data_type
        var hasStatus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var extra = new[] { "status", "tk", "loai_tk", "ma_ct", "ma_dvcs", "ds_dvcs" };   // ds_dvcs: danh mục gán theo đơn vị ("#01#03#", rỗng = mọi đơn vị)   // ma_dvcs: đơn vị hay dùng cho @@unit / {[$ma_dvcs]}   // ma_ct: danh mục theo loại chứng từ (dmmagd…) lọc đúng chứng từ đang mở   // status: lọc danh mục còn dùng; dmtk.tk + loai_tk: mọi ô tài khoản lấy tài khoản chi tiết
        var schemaCols = names.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var inList = string.Join(",", schemaCols.Select((_, i) => "@c" + i));
        var sql = $@"SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS c
JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME AND t.TABLE_TYPE = 'BASE TABLE'
WHERE c.TABLE_SCHEMA = 'dbo' AND c.COLUMN_NAME IN ({inList})";
        await using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 20 })
        {
            for (var i = 0; i < schemaCols.Count; i++) cmd.Parameters.AddWithValue("@c" + i, schemaCols[i]);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var table = rd.GetString(0); var col = rd.GetString(1); var type = rd.GetString(2).ToLowerInvariant();
                if (col.Equals("status", StringComparison.OrdinalIgnoreCase)) hasStatus.Add(table);
                if (!cols.TryGetValue(col, out var m)) cols[col] = m = new(StringComparer.OrdinalIgnoreCase);
                m[table] = type;
            }
        }
        // Số dòng từng bảng (metadata, không quét bảng): bỏ bảng rỗng; ô không phải mã thì ưu tiên bảng nhiều dữ liệu (kể cả bảng phân kỳ m81$202605...).
        var rows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var cmd = new SqlCommand(@"SELECT t.name, SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
WHERE SCHEMA_NAME(t.schema_id) = 'dbo' GROUP BY t.name", conn) { CommandTimeout = 20 };
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) rows[rd.GetString(0)] = rd.GetInt64(1);
        }
        catch { /* không đọc được metadata — xếp theo tên như cũ */ }
        long Rows(string t) => rows.Count == 0 ? 1 : rows.TryGetValue(t, out var n) ? n : 0;
        var dmtkOk = cols.TryGetValue("tk", out var tkTables) && tkTables.ContainsKey("dmtk");
        var dmtkDetail = dmtkOk && cols.TryGetValue("loai_tk", out var lt) && lt.ContainsKey("dmtk");

        var maxLen = fields.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Max(f => f.MaxLength), StringComparer.OrdinalIgnoreCase);
        var today = DateTime.Today.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        var picks = new List<Pick>();
        var picked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);   // mã đã chọn — để lọc mã phụ thuộc (vị trí theo kho…)
        // Mã chính của danh mục đang thêm: mã MỚI chưa có trong bảng.
        if (primary is not null && cols.TryGetValue(primary, out var pTables))
        {
            var pTable = form is { IsVoucher: false } && form.Code.Equals(primary, StringComparison.OrdinalIgnoreCase) && pTables.ContainsKey(form.Table)
                ? form.Table : RankTables(primary, pTables.Keys).FirstOrDefault();
            if (pTable is not null && names.Contains(primary, StringComparer.OrdinalIgnoreCase))
            {
                maxLen.TryGetValue(primary, out var pml);
                var code = await NewCodeAsync(conn, pTable, primary, pml, ct);
                if (code is not null) picks.Add(new Pick(primary, code, pTable + " (mã mới)"));
                names.RemoveAll(n => n.Equals(primary, StringComparison.OrdinalIgnoreCase));
            }
        }
        string? ValueOf(string n) => picked.TryGetValue(n, out var v) ? v : formValues is not null && formValues.TryGetValue(n, out var fv) ? fv : null;
        var gridNames = new HashSet<string>(fields.Where(f => f.Grid).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        // Giá trị hay dùng nhất của cột ở bảng dữ liệu của CHÍNH chứng từ này: bảng chính (m21$000000 → m21, m21$202611…) hoặc bảng lưới (d21$…),
        // 2 bảng kỳ mới nhất có dữ liệu, chỉ dòng ma_ct = mã chứng từ (dir id), đọc tối đa 5000 dòng mỗi bảng (NOLOCK). Danh mục / không đọc được → rỗng.
        async Task<List<(string Value, string Table)>> FrequentAsync(string col, bool grid)
        {
            var res = new List<(string, string)>();
            if (form is not { IsVoucher: true } || !cols.TryGetValue(col, out var colTables)) return res;
            static string Base(string t) => t.Split('$')[0];
            var bases = (grid ? form.GridTables : new[] { form.Table }).Where(t => t.Length > 0).Select(Base).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (bases.Count == 0) return res;
            var tables = colTables.Keys.Where(t => bases.Contains(Base(t)) && Rows(t) > 0 && !IsLogTable(t))
                .OrderByDescending(t => t, StringComparer.OrdinalIgnoreCase).Take(2).ToList();
            var numeric = IsNumType(colTables.Values.FirstOrDefault() ?? "");
            var counts = new Dictionary<string, (long N, string Table)>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables)
            {
                var filter = $"[{col}] IS NOT NULL" + (numeric ? $" AND [{col}] <> 0" : $" AND LTRIM(RTRIM(CAST([{col}] AS nvarchar(4000)))) <> ''");
                if (form.VoucherId.Length > 0 && cols.TryGetValue("ma_ct", out var ctT) && ctT.ContainsKey(t))
                    filter += $" AND RTRIM([ma_ct]) = N'{form.VoucherId.Replace("'", "''")}'";
                try
                {
                    await using var cmd = new SqlCommand($"SELECT TOP 30 v, COUNT(*) FROM (SELECT TOP 5000 [{col}] v FROM [dbo].[{t.Replace("]", "]]")}] WITH (NOLOCK) WHERE {filter}) x GROUP BY v ORDER BY COUNT(*) DESC", conn) { CommandTimeout = 10 };
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct))
                    {
                        var v = rd.GetValue(0) switch
                        {
                            decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
                            double d => d.ToString("0.##", CultureInfo.InvariantCulture),
                            DateTime d => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                            bool b => b ? "1" : "0",
                            var o => Convert.ToString(o, CultureInfo.InvariantCulture)?.Trim() ?? "",
                        };
                        if (v.Length == 0) continue;
                        var n = rd.GetInt32(1);
                        counts[v] = counts.TryGetValue(v, out var c) ? (c.N + n, c.Table) : (n, t);
                    }
                }
                catch { /* bảng khoá / timeout — thử bảng khác */ }
            }
            return counts.OrderByDescending(kv => kv.Value.N).Select(kv => (kv.Key, kv.Value.Table)).ToList();
        }
        // Đơn vị làm việc cho @@unit / {[$ma_dvcs]}: ma_dvcs đang có trên form → đơn vị dùng nhiều nhất ở chính chứng từ này → đơn vị có nhiều kho nhất
        // → đơn vị đầu tiên. (Trước: luôn lấy đơn vị đầu tiên trong dmdvcs — KOG '00' không có kho nào → mọi ô kho trống.)
        var unit = ValueOf("ma_dvcs") ?? "";
        if (unit.Length == 0 && (await FrequentAsync("ma_dvcs", false)).FirstOrDefault() is { Value.Length: > 0 } fu) unit = fu.Value;
        if (unit.Length == 0)
            foreach (var q in new[] { "SELECT TOP 1 RTRIM(ma_dvcs) FROM dmkho WITH (NOLOCK) GROUP BY ma_dvcs ORDER BY COUNT(*) DESC", "SELECT TOP 1 RTRIM(ma_dvcs) FROM dmdvcs WITH (NOLOCK)" })
            {
                try { await using var cmd = new SqlCommand(q, conn) { CommandTimeout = 10 }; if (await cmd.ExecuteScalarAsync(ct) is string u && u.Length > 0) { unit = u; break; } }
                catch { /* bảng không có */ }
            }
        if (unit.Length > 0)
        {
            // trả kèm để ô ma_dvcs trống trên form (nếu có) điền đúng đơn vị đã dùng cho điều kiện tra cứu
            if (ValueOf("ma_dvcs") is null) picks.Add(new Pick("ma_dvcs", unit, "đơn vị làm việc"));
            picked["ma_dvcs"] = unit;
        }
        string? Sql(string expr) => FboFormLookups.ToSql(expr, ValueOf, form?.VoucherId ?? "", sysDatabase, appDatabase, unit);
        // Danh mục gán theo đơn vị (ds_dvcs = "#01#03#", rỗng = mọi đơn vị — FastBusiness$System$GetUnitFilter): chỉ lấy mã thấy được ở đơn vị đang làm việc.
        string UnitCond(string table) => unit.Length > 0 && cols.TryGetValue("ds_dvcs", out var dsT) && dsT.ContainsKey(table)
            ? $"(LTRIM(RTRIM(ISNULL([ds_dvcs], ''))) = '' OR [ds_dvcs] LIKE N'%#{unit.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]")}#%')"
            : "";
        string LkTable(FboFormLookups.Lookup l) =>
            l.Sys && sysDatabase.Length > 0 ? $"[{sysDatabase.Replace("]", "]]")}].[dbo].[{l.Table.Replace("]", "]]")}]" : l.Table;
        // Điều kiện "giá trị của ô cha có dòng con": với mỗi ô tra cứu khác có key/check dạng  cột = '{…[cha]}'  →
        // RTRIM([mã cha]) IN (SELECT RTRIM([cột]) FROM bảng tra cứu của ô con). requiredOnly: chỉ xét ô con bắt buộc.
        string ChildRestriction(string parent, string parentCode, bool requiredOnly)
        {
            if (form is null) return "";
            var parts = new List<string>();
            var rx = new Regex(@"\[?([A-Za-z_]\w*)\]?\s*=\s*'\{\s*(?:\$%c(?:\$%r\.)?\[|\[\$)" + Regex.Escape(parent) + @"\]\s*\}'", RegexOptions.IgnoreCase);
            foreach (var c in names)
            {
                if (c.Equals(parent, StringComparison.OrdinalIgnoreCase) || LookupOf(c) is not { } cl) continue;
                if (requiredOnly && !(gridNames.Contains(c) ? form.GridRequired : form.Required).Contains(c)) continue;
                var expr = cl.Key;
                var m = rx.Match(expr);
                if (!m.Success) m = rx.Match(expr = cl.Check);
                if (!m.Success) continue;
                var col = m.Groups[1].Value;
                // áp luôn phần còn lại của điều kiện ô con (status = '1'…): giá trị cha = chính cột [col] của dòng con
                const string P = "\u0001PARENT\u0001";
                var childCond = FboFormLookups.ToSql(expr, n => n.Equals(parent, StringComparison.OrdinalIgnoreCase) ? P : ValueOf(n), form.VoucherId, sysDatabase, appDatabase, unit);
                var more = childCond is { Length: > 0 } ? " AND (" + childCond.Replace("'" + P + "'", $"[{col}]") + ")" : "";
                parts.Add($"LTRIM(RTRIM(CAST([{parentCode}] AS nvarchar(400)))) IN (SELECT LTRIM(RTRIM(CAST([{col}] AS nvarchar(400)))) FROM {Tref(LkTable(cl))} WITH (NOLOCK) WHERE [{col}] IS NOT NULL{more})");
            }
            return string.Join(" AND ", parts);
        }

        // Thứ tự chọn: ô tra cứu trước; ô mà điều kiện (key / check / filter) tham chiếu ô khác ({$%c[ma_phi]}) đi SAU các ô đó (sắp xếp theo phụ thuộc,
        // không chỉ đếm số tham chiếu — APTran chọn ma_td2 trước ma_phi thì key "ma_phi = '{…[ma_phi]}'" luôn rỗng).
        var order = new List<string>();
        {
            var nameSet = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            IEnumerable<string> Deps(string n) => LookupOf(n) is { } l
                ? FboFormLookups.Refs(l.Key).Concat(FboFormLookups.Refs(l.Check)).Concat(FboFormLookups.Refs(l.Filter)).Where(r => nameSet.Contains(r) && !r.Equals(n, StringComparison.OrdinalIgnoreCase))
                : Enumerable.Empty<string>();
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // 1 = đang thăm (vòng lặp), 2 = xong
            void Visit(string n)
            {
                if (state.TryGetValue(n, out var s)) return;
                state[n] = 1;
                foreach (var d in Deps(n)) Visit(d);
                state[n] = 2;
                order.Add(n);
            }
            foreach (var n in names.OrderBy(n => IsLookup(n) ? 0 : 1)) Visit(n);
        }
        // Mã tự đặt BẮT BUỘC của chính bảng chứng từ (TS2Tran: dmccdc.ma_dc, ZM1Tran.ma_lo_trinh, ma_lsx…) — không phải ô tra cứu: để vòng "ô bắt buộc"
        // bên dưới sinh MÃ MỚI; lấy giá trị hay dùng / có sẵn thì Lưu báo trùng.
        var mainBase0 = form?.Table.Split('$')[0] ?? "";
        bool IsSelfCode(string n) => form is not null && mainBase0.Length > 0 && LookupOf(n) is null && IsKey(n) && !gridNames.Contains(n)
            && form.Required.Contains(n) && cols.TryGetValue(n, out var ts) && ts.Keys.Any(t => t.Split('$')[0].Equals(mainBase0, StringComparison.OrdinalIgnoreCase));
        foreach (var name in order)
        {
            if (IsSelfCode(name)) continue;
            cols.TryGetValue(name, out var tables);
            var type = tables?.Values.FirstOrDefault() ?? "";
            if (IsDateType(type) || (tables is null && name.StartsWith("ngay", StringComparison.OrdinalIgnoreCase)))
            {
                picks.Add(new Pick(name, today, "hôm nay"));
                continue;
            }
            // Chứng từ: giá trị dùng NHIỀU NHẤT ở chính loại chứng từ này (bảng dữ liệu m21$… / lưới d21$…, lọc ma_ct = mã chứng từ) —
            // sát cách dự án đang nhập (loai_ct hay dùng, không phải loại hiếm). Ô tra cứu vẫn phải thoả điều kiện key của ô.
            var freq = await FrequentAsync(name, gridNames.Contains(name));
            // Ô tra cứu có trong controller: lấy mã từ đúng bảng tra cứu theo đúng điều kiện key (không được thì check) của ô.
            // Điều kiện chạy được mà không có dòng nào → bỏ ô (lấy mã khác FBO sẽ báo không tồn tại); điều kiện lỗi / không dịch được → cách cũ.
            if (LookupOf(name) is { } lk)
            {
                // key → không dịch được thì check; ô tra cứu KHÔNG khai key lẫn check = mọi dòng của bảng tra cứu (không phải "không dịch được").
                // Cộng filter riêng của controller Lookup. Bảng ở DB Sys (database="Sys") → [Sys].[dbo].[bảng].
                var w = string.IsNullOrWhiteSpace(lk.Key) && string.IsNullOrWhiteSpace(lk.Check) ? "" : Sql(lk.Key) ?? Sql(lk.Check);
                if (w is not null && lk.Filter.Length > 0)
                    w = Sql(lk.Filter) is { } fw ? (w.Length > 0 && fw.Length > 0 ? $"({w}) AND ({fw})" : w + fw) : null;
                var lkTable = LkTable(lk);
                if (w is not null && UnitCond(lk.Table) is { Length: > 0 } uc) w = w.Length > 0 ? $"({w}) AND {uc}" : uc;
                // Ô CHA của ô phụ thuộc (ma_vt → dvt "ma_vt = '{…[ma_vt]}'", ma_phi → ma_td2, ma_kho → ma_vi_tri): chỉ lấy giá trị CÓ dòng con trong bảng
                // tra cứu của ô con, không thì ô con luôn rỗng (vật tư chưa khai đơn vị tính…). Ưu tiên đủ mọi ô con → chỉ ô con bắt buộc → như cũ.
                if (w is not null)
                    foreach (var restrict in new[] { ChildRestriction(name, lk.Code, false), ChildRestriction(name, lk.Code, true) }.Where(r => r.Length > 0).Distinct())
                    {
                        var wr = w.Length > 0 ? $"({w}) AND {restrict}" : restrict;
                        string? rv = freq.Count > 0 ? await FirstValidAsync(conn, lkTable, lk.Code, wr, freq.Select(x => x.Value).ToList(), ct) : null;
                        if (rv is null && await SampleAsync(conn, lkTable, lk.Code, wr, false, ct) is { Count: > 0 } rl) rv = rl[Random.Shared.Next(rl.Count)];
                        if (rv is null) continue;
                        picks.Add(new Pick(name, rv, $"{lk.Table} theo key {lk.Controller} · có dữ liệu cho ô phụ thuộc"));
                        picked[name] = rv;
                        break;
                    }
                if (picked.ContainsKey(name)) continue;
                if (w is not null)
                {
                    if (freq.Count > 0 && await FirstValidAsync(conn, lkTable, lk.Code, w, freq.Select(x => x.Value).ToList(), ct) is { } fv)
                    {
                        picks.Add(new Pick(name, fv, $"hay dùng nhất ở {freq[0].Table} · hợp lệ theo key {lk.Controller}"));
                        picked[name] = fv;
                        continue;
                    }
                    var lv = await SampleAsync(conn, lkTable, lk.Code, w, false, ct);
                    if (lv is not null)
                    {
                        if (lv.Count == 0) continue;
                        var lp = lv[Random.Shared.Next(lv.Count)];
                        picks.Add(new Pick(name, lp, $"{lk.Table} theo key {lk.Controller}"));
                        picked[name] = lp;
                        continue;
                    }
                }
            }
            if (freq.Count > 0)
            {
                // Không có tra cứu trong controller: ô thường lấy thẳng giá trị hay dùng; ô mã thì phải còn trong danh mục (còn dùng) của nó.
                string? fv = null;
                if (!IsKey(name)) fv = freq[0].Value;
                else if (tables is not null && RankTables(name, tables.Keys).FirstOrDefault(t => t.StartsWith("dm", StringComparison.OrdinalIgnoreCase) && Rows(t) > 0) is { } dmT)
                    fv = await FirstValidAsync(conn, dmT, name, hasStatus.Contains(dmT) ? "CAST([status] AS varchar(10)) = '1'" : "", freq.Select(x => x.Value).ToList(), ct);
                if (fv is not null)
                {
                    if (maxLen.TryGetValue(name, out var fml) && fml > 0 && fv.Length > fml) fv = fv[..fml];
                    picks.Add(new Pick(name, fv, $"hay dùng nhất ở {freq[0].Table}"));
                    picked[name] = fv;
                    continue;
                }
            }
            if (tables is null) continue;   // không có cột này trong database → không bịa
            // Nguồn thử lần lượt (tối đa 5 bảng) tới khi có dữ liệu. Ô tài khoản (tk, tk_xxx): luôn lấy tài khoản CHI TIẾT trong dmtk trước.
            var sources = new List<(string Table, string Column, string Where)>();
            if (name.StartsWith("tk", StringComparison.OrdinalIgnoreCase) && dmtkOk)
                sources.Add(("dmtk", "tk", dmtkDetail ? "loai_tk = 1" : ""));
            var ranked = IsKey(name)
                ? RankTables(name, tables.Keys).Where(t => Rows(t) > 0)
                : tables.Keys.Where(t => Rows(t) > 0 && !IsLogTable(t) && !t.StartsWith("sys", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("wrk", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(t => t.StartsWith("dm", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ThenByDescending(Rows);
            foreach (var t in ranked.Take(5))
            {
                var dm = t.StartsWith("dm", StringComparison.OrdinalIgnoreCase);
                var where = dm && hasStatus.Contains(t) ? "CAST([status] AS varchar(10)) = '1'" : "";
                if (dm && UnitCond(t) is { Length: > 0 } uc2) where += (where.Length > 0 ? " AND " : "") + uc2;
                // chứng từ: danh mục có cột ma_ct (mã giao dịch dmmagd…) chỉ lấy dòng của đúng loại chứng từ (dir id="HDA")
                if (IsKey(name) && dm && form is { IsVoucher: true, VoucherId.Length: > 0 } && cols.TryGetValue("ma_ct", out var ctTables) && ctTables.ContainsKey(t))
                    where += (where.Length > 0 ? " AND " : "") + $"RTRIM([ma_ct]) = N'{form.VoucherId.Replace("'", "''")}'";
                // Mã phụ thuộc mã đã chọn: danh mục của ô này có cột trùng 1 mã đã điền (dmvitri.ma_kho khi đã chọn kho) → chỉ lấy dòng khớp,
                // không thì FBO báo mã không hợp lệ (vị trí không thuộc kho). Không có dòng khớp thì thôi, không lấy bừa.
                if (IsKey(name))
                    foreach (var (dep, val) in picked)
                        if (!dep.Equals(name, StringComparison.OrdinalIgnoreCase) && cols.TryGetValue(dep, out var depTables) && depTables.ContainsKey(t))
                            where += (where.Length > 0 ? " AND " : "") + $"[{dep}] = N'{val.Replace("'", "''")}'";
                sources.Add((t, name, where));
            }
            foreach (var (table, column, extraWhere) in sources)
            {
                var values = await SampleAsync(conn, table, column, extraWhere, IsNumType(type), ct);
                if (values is null || values.Count == 0) continue;
                var pick = values[Random.Shared.Next(values.Count)];
                // tên của danh mục đang thêm (ten_vt khi thêm vật tư): lấy 1 tên có thật rồi ghi rõ là mẫu
                if (primaryName is not null && name.TrimEnd('2').Equals(primaryName, StringComparison.OrdinalIgnoreCase)) pick += " - mẫu";
                if (maxLen.TryGetValue(name, out var ml) && ml > 0 && pick.Length > ml) pick = pick[..ml];
                picks.Add(new Pick(name, pick, column.Equals(name, StringComparison.OrdinalIgnoreCase) ? table : $"{table}.{column}"));
                picked[name] = pick;
                break;
            }
        }

        // Ô BẮT BUỘC (allowNulls="false") vẫn chưa có giá trị và không phải ô tra cứu:
        //  • mã tự đặt của chính chứng từ (ma_lsx, ma_bom… là cột của bảng chính) → mã MỚI không trùng / không lồng — lấy mã có sẵn thì Lưu báo trùng;
        //  • ô thường mà database không có dữ liệu mẫu (tu_so, gio…) → giá trị giữ chỗ theo kiểu cột (số 1, ngày hôm nay, chữ "MAU") để form lưu được.
        if (form is not null)
        {
            var have = new HashSet<string>(picks.Select(p => p.Field), StringComparer.OrdinalIgnoreCase);
            var mainBase = form.Table.Split('$')[0];
            foreach (var name in order)
            {
                if (have.Contains(name) || LookupOf(name) is not null) continue;
                if (!(gridNames.Contains(name) ? form.GridRequired : form.Required).Contains(name)) continue;
                cols.TryGetValue(name, out var tb);
                var type = tb?.Values.FirstOrDefault() ?? "";
                maxLen.TryGetValue(name, out var ml);
                string? v; string src;
                if (IsKey(name) && !gridNames.Contains(name) && mainBase.Length > 0
                    && tb?.Keys.FirstOrDefault(t => t.Split('$')[0].Equals(mainBase, StringComparison.OrdinalIgnoreCase) && !IsLogTable(t)) is { } mt)
                {
                    v = await NewCodeAsync(conn, mt, name, ml, ct);
                    src = mt + " (mã mới)";
                }
                else if (IsNumType(type)) { v = "1"; src = "giữ chỗ (ô bắt buộc, chưa có dữ liệu mẫu)"; }
                else if (IsDateType(type)) { v = today; src = "hôm nay"; }
                else { v = "MAU"; src = "giữ chỗ (ô bắt buộc, chưa có dữ liệu mẫu)"; }
                if (v is null) continue;
                if (ml > 0 && v.Length > ml) v = v[..ml];
                picks.Add(new Pick(name, v, src));
                picked[name] = v;
            }
        }
        return picks;
    }

    /// <summary>
    /// Mã mới chưa có trong <paramref name="table"/>: lấy mã lớn nhất có phần số ở cuối (21.000156) rồi tăng dần (21.000157…) tới khi chưa tồn tại;
    /// bảng chưa có mã kiểu đó thì "BC" + ngày giờ. Chỉ đọc (SELECT), cắt theo độ dài tối đa của ô.
    /// </summary>
    private static async Task<string?> NewCodeAsync(SqlConnection conn, string table, string column, int maxLen, CancellationToken ct)
    {
        var t = table.Replace("]", "]]");
        async Task<bool> ExistsAsync(string v)
        {
            try
            {
                // trùng hẳn, hoặc LỒNG: mã mới là đầu của mã có sẵn / mã có sẵn là đầu của mã mới (FBO báo "mã không được lồng nhau")
                await using var cmd = new SqlCommand($@"SELECT TOP 1 1 FROM [dbo].[{t}] WITH (NOLOCK) WHERE RTRIM([{column}]) <> ''
  AND (RTRIM([{column}]) = @v OR LEFT(@v, LEN(RTRIM([{column}]))) = RTRIM([{column}]) OR LEFT(RTRIM([{column}]), LEN(@v)) = @v)", conn) { CommandTimeout = 15 };
                cmd.Parameters.AddWithValue("@v", v);
                return await cmd.ExecuteScalarAsync(ct) is not null;
            }
            catch { return true; }   // không chắc → coi như trùng, thử mã khác
        }
        string Fit(string v) => maxLen > 0 && v.Length > maxLen ? v[^maxLen..] : v;
        string? top = null;
        try
        {
            // mã dài nhất rồi lớn nhất — "21.000156" thắng "9"
            await using var cmd = new SqlCommand($"SELECT TOP 1 RTRIM([{column}]) FROM [dbo].[{t}] WITH (NOLOCK) WHERE [{column}] LIKE '%[0-9]' ORDER BY LEN([{column}]) DESC, [{column}] DESC", conn) { CommandTimeout = 10 };
            top = await cmd.ExecuteScalarAsync(ct) as string;
        }
        catch { /* dùng mã theo giờ */ }
        if (!string.IsNullOrEmpty(top))
        {
            var m = System.Text.RegularExpressions.Regex.Match(top, @"^(.*?)(\d+)$");
            if (m.Success)
            {
                var digits = m.Groups[2].Value;
                var n = System.Numerics.BigInteger.Parse(digits, CultureInfo.InvariantCulture);
                for (var i = 0; i < 40; i++)
                {
                    n += 1;
                    var cand = m.Groups[1].Value + n.ToString(CultureInfo.InvariantCulture).PadLeft(digits.Length, '0');
                    if (maxLen > 0 && cand.Length > maxLen) break;
                    if (!await ExistsAsync(cand)) return cand;
                }
            }
        }
        for (var i = 0; i < 5; i++)
        {
            var cand = Fit("BC" + DateTime.Now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture) + (i > 0 ? i.ToString(CultureInfo.InvariantCulture) : ""));
            if (!await ExistsAsync(cand)) return cand;
        }
        return null;
    }

    /// <summary>Tên bảng trong SQL: "[Sys].[dbo].[userinfo2]" (đã đủ, bảng tra cứu ở DB Sys) giữ nguyên; tên trần → [dbo].[tên].</summary>
    private static string Tref(string table) => table.StartsWith('[') ? table : $"[dbo].[{table.Replace("]", "]]")}]";

    /// <summary>Bảng nhật ký ($log, _log) — không lấy giá trị mẫu từ đó (dữ liệu sửa/xoá, không phải dữ liệu đang dùng).</summary>
    private static bool IsLogTable(string t) => t.Contains("$log", StringComparison.OrdinalIgnoreCase) || t.EndsWith("_log", StringComparison.OrdinalIgnoreCase);

    /// <summary>Câu SQL lỗi nặng (timeout / severity cao) làm SQL Server đóng kết nối — mở lại để ô sau / chứng từ sau không lỗi dây chuyền.</summary>
    private static async Task EnsureOpenAsync(SqlConnection conn, CancellationToken ct)
    {
        if (conn.State == System.Data.ConnectionState.Open) return;
        try { if (conn.State != System.Data.ConnectionState.Closed) conn.Close(); } catch { }
        await conn.OpenAsync(ct);
    }

    /// <summary>Ứng viên đầu tiên (theo thứ tự truyền vào) còn có trong <paramref name="table"/> thoả <paramref name="where"/> — mã hay dùng nhưng đã
    /// ngừng dùng / không thoả điều kiện tra cứu của ô thì bỏ. null = không ứng viên nào hợp lệ / lỗi.</summary>
    private static async Task<string?> FirstValidAsync(SqlConnection conn, string table, string column, string where, IReadOnlyList<string> candidates, CancellationToken ct)
    {
        var list = candidates.Take(30).ToList();
        if (list.Count == 0) return null;
        try
        {
            await EnsureOpenAsync(conn, ct);
            var ps = string.Join(",", list.Select((_, i) => "@v" + i));
            // LTRIM + RTRIM: số chứng từ / số lệnh của FBO lưu căn phải ('           4') — chỉ RTRIM thì so trượt
            var cond = $"LTRIM(RTRIM(CAST([{column}] AS nvarchar(4000)))) IN ({ps})" + (where.Length > 0 ? " AND (" + where + ")" : "");
            await using var cmd = new SqlCommand($"SELECT LTRIM(RTRIM(CAST([{column}] AS nvarchar(4000)))) FROM {Tref(table)} WITH (NOLOCK) WHERE {cond}", conn) { CommandTimeout = 10 };
            for (var i = 0; i < list.Count; i++) cmd.Parameters.AddWithValue("@v" + i, list[i].Trim());
            var ok = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) if (!rd.IsDBNull(0)) ok.Add(rd.GetString(0));
            return list.FirstOrDefault(c => ok.Contains(c.Trim()))?.Trim();
        }
        catch { return null; }
    }

    /// <summary>Tối đa 200 giá trị khác rỗng của 1 cột (đọc NOLOCK, dừng ở 200 dòng — không quét cả bảng lớn). Lỗi (khoá / quyền / timeout) = rỗng.</summary>
    private static async Task<List<string>?> SampleAsync(SqlConnection conn, string table, string column, string extraWhere, bool numeric, CancellationToken ct)
    {
        var values = new List<string>();
        try
        {
            await EnsureOpenAsync(conn, ct);
            var where = $"[{column}] IS NOT NULL" + (numeric ? "" : $" AND LTRIM(RTRIM(CAST([{column}] AS nvarchar(4000)))) <> ''")
                + (extraWhere.Length > 0 ? " AND (" + extraWhere + ")" : "");
            await using var cmd = new SqlCommand($"SELECT TOP 200 [{column}] FROM {Tref(table)} WITH (NOLOCK) WHERE {where}", conn) { CommandTimeout = 10 };
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var v = rd.GetValue(0) switch
                {
                    decimal d => d.ToString("0.##", CultureInfo.InvariantCulture),
                    double d => d.ToString("0.##", CultureInfo.InvariantCulture),
                    float f => f.ToString("0.##", CultureInfo.InvariantCulture),
                    DateTime d => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                    bool b => b ? "1" : "0",
                    var o => Convert.ToString(o, CultureInfo.InvariantCulture)?.Trim() ?? "",
                };
                if (v.Length > 0 && !(numeric && v == "0")) values.Add(v);
            }
        }
        catch { return null; /* bảng khoá / quyền / timeout / điều kiện key lỗi — thử nguồn khác */ }
        return values.Distinct().ToList();
    }
}
