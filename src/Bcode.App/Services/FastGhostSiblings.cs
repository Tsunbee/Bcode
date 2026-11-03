using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// "Store anh em" cho ghost text Fast của SQL Query: 3 procedure / function trong database đang kết nối có tập tên (bảng, cột, biến, bảng tạm — bỏ từ khoá SQL)
/// giống store đang viết nhất (Jaccard ≥ 0,2; bỏ chính nó theo tên ở dòng CREATE / ALTER). Store cùng nghiệp vụ trong Fast viết theo cùng khuôn
/// (rs_… / fs_…, bản chép z…), nên dòng đang gõ hay đã có y hệt ở đó → <see cref="FastGhostRules"/> dùng như "theo chính file" (nguồn "sib").
/// Đo trên KOG (store đo chưa dùng để học, chọn anh em theo 30% đầu store, 928 / 1.111 store có anh em), chỉ bù chỗ đang im lặng:
/// giữa dòng có gợi ý 39,0% → 44,5% (đúng 92,0% → 91,8%), dòng trống 13,5% → 31,7% (92,4% → 91,5%); gõ hộ 6,2% → 11,3% / 3,4% → 17,3% ký tự.
/// Chỉ đọc: SELECT sys.sql_modules — lúc dựng chỉ mục chỉ giữ tập tên của từng store, định nghĩa chỉ tải của store được chọn (nhớ 30 store).
/// Chỉ mục dựng ở nền lần đầu dùng (KOG 2.296 store ~ vài giây), quá 15 phút thì dựng lại ở nền (store mới / vừa sửa).
/// </summary>
public static class FastGhostSiblings
{
    /// <summary>Văn bản các store anh em (nối nhau) + các dòng code của chúng (đã bỏ comment "--": nguyên văn đã cắt khoảng trắng, dạng chuẩn để so).</summary>
    public sealed record Set(string Text, IReadOnlyList<(string Raw, string Norm)> Code);

    private sealed class Index(List<(string Name, int Id, HashSet<string> Names)> mods, DateTime built)
    {
        public List<(string Name, int Id, HashSet<string> Names)> Mods { get; } = mods;
        public DateTime Built { get; } = built;
        public ConcurrentDictionary<int, Task<string?>> Defs { get; } = new();
        public (string Key, Set Value)? Last;
    }

    private static readonly ConcurrentDictionary<string, Task<Index>> Indexes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task> Rebuilds = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(15);

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "select","from","where","and","or","not","null","is","as","on","join","left","right","inner","outer","cross","insert","into","update","delete","set",
        "declare","begin","end","if","else","while","exec","execute","case","when","then","order","by","group","having","union","all","distinct","top","with",
        "nolock","create","alter","procedure","proc","function","returns","return","table","drop","values","in","exists","like","between","nvarchar","varchar",
        "char","int","numeric","decimal","datetime","bit","max","isnull","rtrim","ltrim","replace","convert","cast","sum","count","len","substring","object_id",
        "tempdb","dbo","nocount","ansi_nulls","quoted_identifier","off","print","go","output","cursor","fetch","next","open","close","deallocate","for","tran",
    };
    private static readonly Regex NameRx = new(@"[@#]?[A-Za-z_][\w$#]{2,}", RegexOptions.Compiled);
    private static readonly Regex ObjectNameRx = new(@"\b(?:CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?(?:PROC|PROCEDURE|FUNCTION)\s+(?:\[?\w+\]?\.)?\[?(?<n>[\w$#]+)\]?", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static HashSet<string> NamesOf(string text, Dictionary<string, string>? pool = null)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in NameRx.Matches(text))
        {
            var v = m.Value.ToLowerInvariant();
            if (Keywords.Contains(v)) continue;
            if (pool is not null) { if (!pool.TryGetValue(v, out var p)) pool[v] = p = v; v = p; }
            res.Add(v);
        }
        return res;
    }

    /// <summary>Store anh em của <paramref name="text"/> trong database <paramref name="dbKey"/>; null = chưa có (chỉ mục / định nghĩa đang tải ở nền,
    /// hoặc không có store nào đủ giống). Gọi được từ luồng nền.</summary>
    public static Set? For(string dbKey, Func<SqlConnection> connect, string text)
    {
        var task = Indexes.GetOrAdd(dbKey, _ => Task.Run(() => BuildAsync(connect)));
        if (!task.IsCompletedSuccessfully) return null;
        var idx = task.Result;
        // cũ quá → dựng lại ở nền; trong lúc chờ vẫn dùng bản cũ (đọc lỗi thì bản rỗng — cũng thử lại sau MaxAge)
        if (DateTime.UtcNow - idx.Built > MaxAge)
            Rebuilds.GetOrAdd(dbKey, _ => Task.Run(async () =>
            {
                var fresh = await BuildAsync(connect);
                Indexes[dbKey] = Task.FromResult(fresh);
                Rebuilds.TryRemove(dbKey, out Task? _);
            }));
        if (idx.Mods.Count == 0) return null;
        var probe = NamesOf(text);
        if (probe.Count < 5) return null;
        var self = ObjectNameRx.Match(text) is { Success: true } om ? om.Groups["n"].Value : "";
        var top = idx.Mods.Where(m => !m.Name.Equals(self, StringComparison.OrdinalIgnoreCase))
            .Select(m =>
            {
                var inter = probe.Count <= m.Names.Count ? probe.Count(m.Names.Contains) : m.Names.Count(probe.Contains);
                return (m.Id, J: inter / (double)(m.Names.Count + probe.Count - inter));
            })
            .Where(x => x.J >= 0.2).OrderByDescending(x => x.J).Take(3).Select(x => x.Id).ToList();
        if (top.Count == 0) return null;
        var key = string.Join("|", top);
        if (idx.Last is { } last && last.Key == key) return last.Value;
        if (idx.Defs.Count > 30) idx.Defs.Clear();
        var defs = top.Select(id => idx.Defs.GetOrAdd(id, i => Task.Run(() => FetchAsync(connect, i)))).ToList();
        if (defs.Any(d => !d.IsCompleted)) return null;   // lần gõ sau sẽ có
        var texts = defs.Select(d => d.IsCompletedSuccessfully ? d.Result : null).Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToList();
        if (texts.Count == 0) return null;
        var set = Of(texts);
        idx.Last = (key, set);
        return set;
    }

    /// <summary>Bộ store anh em từ định nghĩa của chúng (dùng cả khi đo ngoài app).</summary>
    public static Set Of(IEnumerable<string> definitions)
    {
        var texts = definitions.Select(d => d.Replace("\r\n", "\n")).ToList();
        var code = texts.SelectMany(d => d.Split('\n')).Select(StripLineComment)
            .Select(l => (Raw: l.Trim(), Norm: FastGhostMiner.Norm(l))).Where(x => x.Norm.Length > 0).ToList();
        return new Set(string.Join("\n", texts), code);
    }

    /// <summary>Tập tên của 1 đoạn SQL như lúc chọn anh em (dùng cả khi đo ngoài app).</summary>
    public static HashSet<string> Names(string text) => NamesOf(text);

    private static async Task<Index> BuildAsync(Func<SqlConnection> connect)
    {
        var mods = new List<(string, int, HashSet<string>)>();
        try
        {
            var pool = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var conn = connect();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT o.name, o.object_id, m.definition FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.type IN ('P','FN','IF','TF') AND m.definition IS NOT NULL", conn) { CommandTimeout = 180 };
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var names = NamesOf(r.GetString(2), pool);
                if (names.Count >= 5) mods.Add((r.GetString(0), r.GetInt32(1), names));
            }
        }
        catch { mods.Clear(); }   // không đọc được (quyền / mất kết nối) → không có nguồn anh em, thử lại sau MaxAge
        return new Index(mods, DateTime.UtcNow);
    }

    private static async Task<string?> FetchAsync(Func<SqlConnection> connect, int id)
    {
        try
        {
            await using var conn = connect();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand("SELECT definition FROM sys.sql_modules WHERE object_id = @id", conn) { CommandTimeout = 30 };
            cmd.Parameters.AddWithValue("@id", id);
            return await cmd.ExecuteScalarAsync() as string;
        }
        catch { return null; }
    }

    private static string StripLineComment(string l)
    {
        var inS = false;
        for (var i = 0; i < l.Length - 1; i++) { if (l[i] == '\'') inS = !inS; else if (!inS && l[i] == '-' && l[i + 1] == '-') return l[..i]; }
        return l;
    }
}
