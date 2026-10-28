using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// "🧩 SQL động" của Debug từng bước: store Fast hay ghép câu lệnh vào biến (@q) rồi EXEC / sp_executesql /
/// FastBusiness$Partition$Execute — lỗi khi đó chỉ báo "dòng 1" của chuỗi động. Lớp này lấy giá trị biến tại điểm dừng (bảng Variables),
/// thay placeholder của Fast (r00$%Partition → r00$000000 = bảng gộp các kỳ, %[ … ]% → ( … )), xuống dòng theo từ khoá cho dễ đọc
/// (để lỗi có số dòng có nghĩa) rồi kiểm tra cú pháp bằng SET PARSEONLY (chỉ phân tích, không chạy, không cần bảng tồn tại).
/// </summary>
public static class DynamicSqlInspector
{
    private static readonly Regex LooksLikeSql = new(@"\b(select|insert|update|delete|exec|execute|merge|with)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Biến chuỗi trong bảng Variables có giá trị trông như câu SQL (dài ≥ 15 ký tự), dài trước.</summary>
    public static List<(string Name, string Value)> Candidates(DataTable? vars)
    {
        var list = new List<(string, string)>();
        if (vars is null || vars.Rows.Count == 0) return list;
        foreach (DataColumn c in vars.Columns)
        {
            if (vars.Rows[0][c] is not string v || v.Trim().Length < 15 || !LooksLikeSql.IsMatch(v)) continue;
            list.Add((c.ColumnName, v));
        }
        return list.OrderByDescending(x => x.Item2.Length).ToList();
    }

    /// <summary>Thay placeholder của Fast và (câu đang nằm trên ít dòng) xuống dòng trước các từ khoá chính — ngoài chuỗi '…'.</summary>
    public static string Prepare(string sql)
    {
        var s = sql.Replace("\r\n", "\n").Replace('\r', '\n');
        s = Regex.Replace(s, @"%Partition\b", "000000", RegexOptions.IgnoreCase);
        s = s.Replace("%[", "(").Replace("]%", ")");
        if (s.Count(ch => ch == '\n') > s.Length / 200) return s.Trim();   // đã có xuống dòng đủ — giữ nguyên

        var masked = SqlStepPlanner.MaskedCode(s);
        var rx = new Regex(@"(?<![\w.@#$])(select|from|where|left\s+(?:outer\s+)?join|right\s+(?:outer\s+)?join|inner\s+join|full\s+(?:outer\s+)?join|cross\s+join|cross\s+apply|outer\s+apply|(?<!(?:left|right|inner|outer|full|cross)\s+)join|group\s+by|order\s+by|having|union(?:\s+all)?|insert\s+into|update|delete|values|exec(?:ute)?)\b",
            RegexOptions.IgnoreCase);
        var cuts = rx.Matches(masked).Select(m => m.Index).Where(i => i > 0).ToList();
        var sb = new StringBuilder(s.Length + cuts.Count * 2);
        var last = 0;
        foreach (var i in cuts)
        {
            sb.Append(s, last, i - last).Append('\n');
            last = i;
        }
        sb.Append(s, last, s.Length - last);
        return string.Join("\n", sb.ToString().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length > 0));
    }

    /// <summary>Kiểm tra cú pháp (SET PARSEONLY): null = hợp lệ, không thì (dòng trong <paramref name="sql"/>, thông báo lỗi đầu tiên).</summary>
    public static async Task<(int Line, string Message)?> ParseAsync(SqlConnection conn, string sql)
    {
        if (conn.State != ConnectionState.Open) await conn.OpenAsync();
        await using (var on = new SqlCommand("SET PARSEONLY ON", conn)) await on.ExecuteNonQueryAsync();
        try
        {
            await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 30 };
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (SqlException ex)
        {
            var e = ex.Errors.Cast<SqlError>().FirstOrDefault(x => x.Class > 10) ?? ex.Errors[0];
            return (Math.Max(1, e.LineNumber), e.Message);
        }
        finally
        {
            try { await using var off = new SqlCommand("SET PARSEONLY OFF", conn); await off.ExecuteNonQueryAsync(); } catch { /* connection sắp đóng */ }
        }
    }

    /// <summary>Nội dung tab mở ra: dòng chú thích kết quả kiểm tra ở đầu + câu SQL, dòng lỗi có thêm "-- ✖ …" ở cuối.</summary>
    public static string Annotate(string varName, int stopLine, string prepared, (int Line, string Message)? error)
    {
        var lines = prepared.Split('\n').ToList();
        var header = new List<string>
        {
            $"-- SQL động {varName} tại điểm dừng dòng {stopLine} (Bcode đã thay r00$%Partition → r00$000000, %[ … ]% → ( … ) để kiểm tra).",
        };
        if (error is { } e)
        {
            var at = Math.Min(e.Line, lines.Count);
            lines[at - 1] += "   -- ✖ " + e.Message;
            var hint = SwallowedCode(prepared);
            // Số dòng trong tab = dòng trong câu SQL + số dòng chú thích phía trên (2, hoặc 3 khi có gợi ý dấu nháy).
            header.Add($"-- ✖ Lỗi cú pháp ở dòng {at + (hint is null ? 2 : 3)} của tab này: {e.Message}");
            if (hint is not null) header.Add("-- ⚠ " + hint);
        }
        else header.Add("-- ✔ Cú pháp hợp lệ (SET PARSEONLY — chưa kiểm tra tên bảng / cột).");
        return string.Join(Environment.NewLine, header.Concat(lines));
    }

    /// <summary>
    /// Lỗi hay gặp khi ghép chuỗi: thừa / thiếu 1 dấu nháy làm 1 chuỗi '…' "nuốt" luôn code SQL phía sau (vd "a.ma_dvcs, '' '" trong store).
    /// Tìm chuỗi đầu tiên có nội dung trông như code (từ khoá SQL / a.cột) — trả câu chỉ chỗ dấu nháy mở, null nếu không thấy.
    /// </summary>
    private static string? SwallowedCode(string sql)
    {
        var codeLike = new Regex(@"\b(select|from|where|join|over|partition|order\s+by|group\s+by)\b|,\s*[a-z]\w*\.\w+", RegexOptions.IgnoreCase);
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] != '\'') continue;
            var j = i + 1;
            while (j < sql.Length && !(sql[j] == '\'' && (j + 1 >= sql.Length || sql[j + 1] != '\''))) j += sql[j] == '\'' ? 2 : 1;
            var content = sql[(i + 1)..Math.Min(j, sql.Length)];
            if (content.Length >= 25 && codeLike.IsMatch(content))
            {
                var before = sql[Math.Max(0, i - 30)..i].Replace('\n', ' ');
                var inside = content.Replace('\n', ' ');
                return $"Có thể thừa / thiếu dấu nháy: chuỗi mở ngay sau «…{before}» nuốt cả code «{(inside.Length > 60 ? inside[..60] + "…" : inside)}» — kiểm tra chỗ ghép chuỗi tương ứng trong store.";
            }
            i = j;
        }
        return null;
    }
}
