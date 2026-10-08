using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services.Rpt.Builder;

public sealed record MetaTable(string Name, string Kind, bool Partitioned);
public sealed record MetaColumn(string Name, string SqlType, int Length, bool IsKey);
public sealed record JoinSuggestion(string Column, string Table, string RightColumn, bool HasNameVi, string NameColumn, string NameColumn2);
public sealed record ColumnHit(string Table, bool Partitioned, bool IsFirstColumn, bool IsKey);
public sealed record PreviewSet(List<string> Columns, List<string?[]> Rows, bool Truncated);
public sealed record PreviewResult(List<PreviewSet> Sets, long Millis, string? Error);

/// <summary>
/// Đọc cấu trúc database đang chọn (App Data) cho màn hình "Tạo báo cáo": danh sách bảng (bảng phân kỳ r00$000000 gộp thành "r00"), cột + kiểu, gợi ý bảng danh mục
/// để nối lấy tên (ma_kh → dmkh…), và CHẠY THỬ báo cáo: chạy thân procedure vừa sinh như một batch (không tạo procedure nào trong database) để xem dữ liệu thật.
/// Chỉ đọc: bước chạy thử từ chối mọi câu lệnh ghi vào bảng không phải bảng tạm (#).
/// </summary>
public sealed class ReportMetaService
{
    private readonly Func<bool, SqlConnection> _connect;
    public ReportMetaService(Func<bool, SqlConnection> connect) => _connect = connect;

    private static readonly Regex PeriodTable = new(@"\$\d{6}$", RegexOptions.Compiled);

    public async Task<List<MetaTable>> ListTablesAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT o.name, o.type FROM sys.objects o WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0 ORDER BY o.name;";
        await using var conn = _connect(false);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var all = new List<(string Name, string Type)>();
        while (await r.ReadAsync(ct)) all.Add((r.GetString(0), r.GetString(1).Trim()));
        var result = new List<MetaTable>();
        foreach (var (name, type) in all)
        {
            if (name.EndsWith("$000000", StringComparison.Ordinal)) result.Add(new MetaTable(name[..^7], "Table", true));   // bảng phân kỳ → tên gốc
            else if (PeriodTable.IsMatch(name)) continue;                                                                      // m21$202601…: bỏ (đã có gốc)
            else if (!name.Contains('$')) result.Add(new MetaTable(name, type == "V" ? "View" : "Table", false));
        }
        return result;
    }

    public async Task<List<MetaColumn>> GetColumnsAsync(string table, bool partitioned, CancellationToken ct = default)
    {
        const string sql = @"
SELECT c.name, ty.name, c.max_length,
       CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                         WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END
FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
WHERE c.object_id = OBJECT_ID(@t) ORDER BY c.column_id;";
        await using var conn = _connect(false);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@t", "dbo." + (partitioned ? table + "$000000" : table));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var cols = new List<MetaColumn>();
        while (await r.ReadAsync(ct)) cols.Add(new MetaColumn(r.GetString(0), r.GetString(1), r.GetInt16(2), r.GetInt32(3) == 1));
        return cols;
    }

    /// <summary>Gợi ý nối: với mỗi cột của bảng chính, tìm bảng danh mục dm* có CỘT ĐẦU TIÊN trùng tên (vd ma_kh ↔ dmkh.ma_kh) và có cột ten_… để hiển thị.</summary>
    public async Task<List<JoinSuggestion>> SuggestJoinsAsync(IEnumerable<string> mainColumns, CancellationToken ct = default)
    {
        var names = mainColumns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count == 0) return new();
        const string sql = @"
SELECT o.name AS tbl, c.name AS firstcol
FROM sys.objects o JOIN sys.columns c ON c.object_id = o.object_id AND c.column_id = 1
WHERE o.type = 'U' AND o.is_ms_shipped = 0 AND o.name LIKE 'dm%' AND o.name NOT LIKE '%$%';";
        await using var conn = _connect(false);
        await conn.OpenAsync(ct);
        var firstCol = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 })
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) firstCol[r.GetString(0)] = r.GetString(1);
        var matches = new List<(string Col, string Table)>();
        foreach (var col in names)
            foreach (var (t, fc) in firstCol)
                if (fc.Equals(col, StringComparison.OrdinalIgnoreCase)) matches.Add((col, t));
        var result = new List<JoinSuggestion>();
        foreach (var (col, table) in matches)
        {
            var cols = await GetColumnsAsync(table, false, ct);
            var nameCol = cols.FirstOrDefault(c => c.Name.StartsWith("ten_", StringComparison.OrdinalIgnoreCase) && !c.Name.EndsWith("2") && !c.Name.StartsWith("ten_ngan", StringComparison.OrdinalIgnoreCase))?.Name ?? "";
            var nameCol2 = nameCol.Length > 0 && cols.Any(c => c.Name.Equals(nameCol + "2", StringComparison.OrdinalIgnoreCase)) ? nameCol + "2" : "";
            result.Add(new JoinSuggestion(col, table, col, nameCol.Length > 0, nameCol, nameCol2));
        }
        return result.OrderBy(j => j.Table.Equals("dm" + Regex.Replace(j.Column, @"^ma_", ""), StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(j => j.Table).ToList();
    }

    /// <summary>"Cột này nằm ở bảng nào?": liệt kê bảng có cột tên đó, ưu tiên bảng nhận cột làm khoá (cột đầu / khoá chính — thường là danh mục để nối).</summary>
    public async Task<List<ColumnHit>> FindTablesByColumnAsync(string column, CancellationToken ct = default)
    {
        const string sql = @"
SELECT o.name, c.column_id,
       CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                         WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END
FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id
WHERE o.type IN ('U','V') AND o.is_ms_shipped = 0 AND c.name = @c;";
        await using var conn = _connect(false);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        cmd.Parameters.AddWithValue("@c", column.Trim());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var hits = new List<ColumnHit>();
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            if (name.EndsWith("$000000", StringComparison.Ordinal)) hits.Add(new ColumnHit(name[..^7], true, r.GetInt32(1) == 1, r.GetInt32(2) == 1));
            else if (!name.Contains('$')) hits.Add(new ColumnHit(name, false, r.GetInt32(1) == 1, r.GetInt32(2) == 1));
        }
        var exact = "dm" + System.Text.RegularExpressions.Regex.Replace(column.Trim(), "^ma_", "");
        return hits.OrderBy(h => h.IsFirstColumn || h.IsKey ? 0 : 1).ThenBy(h => h.Table.Equals(exact, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(h => h.Table.StartsWith("dm", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(h => h.Table).Take(60).ToList();
    }

    // ---- chạy thử ----

    /// <summary>true nếu mọi lệnh ghi (INSERT/CREATE/SELECT … INTO/UPDATE/DELETE/DROP) chỉ nhằm vào bảng tạm # — dùng để chắc chắn bước chạy thử không ghi gì lên bảng thật.</summary>
    public static bool IsReadOnlySafe(string text, out string reason)
    {
        reason = "";
        var t = Regex.Replace(Regex.Replace(text, @"--[^\r\n]*", " "), @"/\*.*?\*/", " ", RegexOptions.Singleline);
        t = Regex.Replace(t, @"'(?:[^']|'')*'", "''");                       // bỏ nội dung chuỗi (kể cả SQL động trong @q — đó là SELECT)
        foreach (Match m in Regex.Matches(t, @"\b(?:insert\s+(?:into\s+)?|update\s+|delete\s+(?:from\s+)?|truncate\s+table\s+|drop\s+(?:table|procedure|function|view)\s+|alter\s+\w+\s+|create\s+(?:table|procedure|function|view|index|unique)\s+)(?<t>[#@\w\[\]\.\$]+)", RegexOptions.IgnoreCase))
        {
            var target = m.Groups["t"].Value;
            if (target.StartsWith('#') || target.StartsWith('@')) continue;
            if (Regex.IsMatch(m.Value, @"^create\s+(?:procedure|proc)\b", RegexOptions.IgnoreCase)) continue;   // phần CREATE PROCEDURE đã bị cắt trước khi chạy
            reason = "câu lệnh ghi vào bảng không phải bảng tạm: " + m.Value.Trim(); return false;
        }
        foreach (Match m in Regex.Matches(t, @"\binto\s+(?<t>[\w#\[\]\.\$]+)", RegexOptions.IgnoreCase))
            if (!m.Groups["t"].Value.StartsWith('#')) { reason = "SELECT … INTO bảng thật: " + m.Value; return false; }
        return true;
    }

    public async Task<PreviewResult> RunPreviewAsync(string procedure, IReadOnlyList<ProcParam> ps, IReadOnlyDictionary<string, string> values, int maxRows, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var at = procedure.IndexOf("\r\nAS\r\nBEGIN", StringComparison.Ordinal);
        if (at < 0) return new PreviewResult(new(), 0, "Không tách được thân procedure.");
        var body = procedure[(at + "\r\nAS\r\nBEGIN".Length)..];
        body = body[..body.LastIndexOf("END", StringComparison.Ordinal)];
        if (!IsReadOnlySafe(body, out var why)) return new PreviewResult(new(), 0, "Từ chối chạy thử: " + why);

        string Lit(ProcParam p)
        {
            var v = values.TryGetValue(p.Name, out var x) ? x : p.Sample.Trim('\'');
            if (p.SqlType.StartsWith("SMALLDATETIME") || p.SqlType.StartsWith("DATETIME")) return "'" + (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.ToString("yyyyMMdd") : "20260101") + "'";
            if (p.SqlType is "INT" or "BIT" or "TINYINT" || p.SqlType.StartsWith("NUMERIC")) return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n.ToString(CultureInfo.InvariantCulture) : "0";
            return "N'" + v.Replace("'", "''") + "'";
        }
        var decl = string.Join("\n", ps.Select(p => $"DECLARE @{p.Name} {p.SqlType}; SET @{p.Name} = {Lit(p)};"));
        var sets = new List<PreviewSet>();
        try
        {
            await using var conn = _connect(false);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(decl + "\n" + body, conn) { CommandTimeout = 90 };
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            do
            {
                var cols = Enumerable.Range(0, rd.FieldCount).Select(rd.GetName).ToList();
                var rows = new List<string?[]>(); var trunc = false;
                while (await rd.ReadAsync(ct))
                {
                    if (rows.Count >= maxRows) { trunc = true; break; }
                    var row = new string?[cols.Count];
                    for (var i = 0; i < cols.Count; i++)
                    {
                        if (await rd.IsDBNullAsync(i, ct)) { row[i] = null; continue; }
                        var v = rd.GetValue(i);
                        row[i] = v switch { DateTime dt => dt.ToString("yyyy-MM-dd"), decimal dec => dec.ToString(CultureInfo.InvariantCulture), double db => db.ToString(CultureInfo.InvariantCulture), _ => v.ToString() };
                    }
                    rows.Add(row);
                }
                sets.Add(new PreviewSet(cols, rows, trunc));
            } while (await rd.NextResultAsync(ct));
            return new PreviewResult(sets, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) { return new PreviewResult(sets, sw.ElapsedMilliseconds, ex.Message); }
    }
}
