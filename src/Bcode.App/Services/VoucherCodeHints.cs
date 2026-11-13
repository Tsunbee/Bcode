using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Một chứng từ trong dmct (+ dmct9): tên, bảng chứng từ C / master M / detail D / I, URL màn hình.</summary>
internal sealed record VoucherCodeInfo(string Code, string Name, string C, string M, string D, string I, string Url)
{
    /// <summary>Các dòng tooltip như FCode: tên chứng từ rồi C: / M: / D: / I: / URL: (dòng trống bỏ).</summary>
    public IReadOnlyList<string> Lines()
    {
        var l = new List<string> { Name.Length > 0 ? Name : Code };
        void Add(string label, string v) { if (v.Length > 0) l.Add(label + v); }
        Add("C: ", C); Add("M: ", M); Add("D: ", D); Add("I: ", I); Add("URL: ", Url);
        return l;
    }

    public string Tooltip => string.Join("\n", Lines());
}

/// <summary>
/// Rê chuột lên mã chứng từ trong chuỗi SQL (vd 'HD1') → tooltip như FCode, lấy từ dmct (ten_ct) + dmct9 (c$, m$, d$, i$, url) của App Data.
/// Đọc 1 lần / database (khoá = CurrentStamp App Data), chỉ SELECT; Refresh database (ISqlTab.RefreshSchema) gọi <see cref="Forget"/>.
/// Dùng chung cho editor Web (sqleditor.html, hover provider) và App (NativeSqlHints.OnDwell).
/// </summary>
internal static class VoucherCodeHints
{
    private static readonly ConcurrentDictionary<string, Task<IReadOnlyDictionary<string, VoucherCodeInfo>?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Danh sách mã chứng từ của database (null = không đọc được, vd chưa có dmct — lần sau thử lại).</summary>
    public static Task<IReadOnlyDictionary<string, VoucherCodeInfo>?> LoadAsync(string key, Func<SqlConnection> connect)
    {
        if (string.IsNullOrEmpty(key)) return Task.FromResult<IReadOnlyDictionary<string, VoucherCodeInfo>?>(null);
        var task = Cache.GetOrAdd(key, _ => Task.Run(() => ReadAsync(connect)));
        // đọc lỗi (mất kết nối…) thì không giữ kết quả rỗng: lần rê chuột sau đọc lại
        _ = task.ContinueWith(t => { if (t.Result is null) Cache.TryRemove(new KeyValuePair<string, Task<IReadOnlyDictionary<string, VoucherCodeInfo>?>>(key, task)); },
            TaskContinuationOptions.OnlyOnRanToCompletion);
        return task;
    }

    /// <summary>Tra một mã (không phân biệt hoa thường); không có trong dmct → null.</summary>
    public static async Task<VoucherCodeInfo?> FindAsync(string key, Func<SqlConnection> connect, string code)
    {
        code = (code ?? "").Trim();
        if (!LooksLikeCode(code)) return null;
        var map = await LoadAsync(key, connect).ConfigureAwait(false);
        return map is not null && map.TryGetValue(code, out var v) ? v : null;
    }

    public static void Forget(string key) { if (!string.IsNullOrEmpty(key)) Cache.TryRemove(key, out _); }

    /// <summary>ma_ct là char(3) (một số bản dài hơn chút): chữ/số, 2..8 ký tự — lọc trước để khỏi tra mọi từ.</summary>
    public static bool LooksLikeCode(string s) => s.Length is >= 2 and <= 8 && s.All(char.IsLetterOrDigit);

    /// <summary>Vị trí <paramref name="index"/> trên dòng có nằm trong chuỗi '...' không (đếm dấu ' phía trước — '' trong SQL động vẫn đúng).</summary>
    public static bool InsideStringLiteral(string line, int index)
    {
        var n = 0;
        for (var i = 0; i < index && i < line.Length; i++) if (line[i] == '\'') n++;
        return n % 2 == 1;
    }

    private static async Task<IReadOnlyDictionary<string, VoucherCodeInfo>?> ReadAsync(Func<SqlConnection> connect)
    {
        // dmct9 có từ bản FBO; bản cũ không có thì chỉ có tên. Cột thiếu (bản lạ) → câu dự phòng chỉ đọc dmct.
        const string full = """
            SET NOCOUNT ON;
            IF OBJECT_ID('dbo.dmct') IS NULL RETURN;
            IF OBJECT_ID('dbo.dmct9') IS NOT NULL
                EXEC('SELECT a.ma_ct, a.ten_ct, b.[c$], b.[m$], b.[d$], b.[i$], b.url FROM dbo.dmct a LEFT JOIN dbo.dmct9 b ON b.ma_ct = a.ma_ct')
            ELSE
                SELECT ma_ct, ten_ct, NULL, NULL, NULL, NULL, NULL FROM dbo.dmct
            """;
        const string basic = "SET NOCOUNT ON; IF OBJECT_ID('dbo.dmct') IS NOT NULL SELECT ma_ct, ten_ct, NULL, NULL, NULL, NULL, NULL FROM dbo.dmct";
        try
        {
            await using var conn = connect();
            await conn.OpenAsync().ConfigureAwait(false);
            try { return await QueryAsync(conn, full).ConfigureAwait(false); }
            catch (SqlException) { return await QueryAsync(conn, basic).ConfigureAwait(false); }
        }
        catch { return null; }
    }

    private static async Task<IReadOnlyDictionary<string, VoucherCodeInfo>> QueryAsync(SqlConnection conn, string sql)
    {
        var d = new Dictionary<string, VoucherCodeInfo>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 20 };
        await using var r = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        string S(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i))?.Trim() ?? "";
        while (await r.ReadAsync().ConfigureAwait(false))
        {
            var code = S(0);
            if (code.Length == 0 || d.ContainsKey(code)) continue;
            d[code] = new VoucherCodeInfo(code, S(1), S(2), S(3), S(4), S(5), S(6));
        }
        return d;
    }
}
