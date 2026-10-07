using System.Text.RegularExpressions;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Một object trong database có nhắc tới tên cần tìm (procedure / function / view / trigger). <see cref="Snippets"/> = vài dòng chứa tên đó.</summary>
public record DbUsage(bool Sys, string Schema, string Name, SqlObjectKind Kind, int Hits, List<UsageLine> Snippets);

/// <summary>Một dòng trích: số dòng trong định nghĩa / file + nội dung đã cắt gọn.</summary>
public record UsageLine(int Line, string Text);

public record SourceUsage(string File, int Hits, List<UsageLine> Snippets);

/// <summary>
/// "Ai đang dùng procedure / bảng này?": (1) quét định nghĩa mọi procedure, function, view, trigger ở CẢ HAI database (App Data + Sys Data) bằng
/// <c>sys.sql_modules</c> — tìm theo văn bản nên bắt được cả SQL động, chuỗi tên trong câu lệnh; (2) quét file trong source (App_Data) bằng
/// <see cref="FileReferenceService"/>. Cả hai lọc lại theo ranh giới tên (không bắt "rs_Foo" khi tìm "rs_Foo2"). Chỉ ĐỌC, không ghi gì.
/// </summary>
public class UsageSearchService
{
    private const int MaxSnippets = 4, MaxSourceHits = 400, SnippetWidth = 220;

    private readonly DbConnectionService _connections;
    private readonly FileReferenceService _files;

    public UsageSearchService(DbConnectionService connections, FileReferenceService files)
    {
        _connections = connections;
        _files = files;
    }

    public static Regex NamePattern(string name) =>
        new(@"(?<![\w$])" + Regex.Escape(name) + @"(?![\w$])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string Trim(string line)
    {
        var t = line.Trim();
        return t.Length <= SnippetWidth ? t : t[..SnippetWidth] + "…";
    }

    /// <summary>Tìm trong định nghĩa của một database. Bỏ qua chính object đang tìm.</summary>
    public async Task<List<DbUsage>> FindInDatabaseAsync(SqlObjectInfo target, bool inSysDatabase, CancellationToken ct)
    {
        const string sql = @"
SELECT s.name, o.name, o.type, m.definition
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0 AND m.definition LIKE @p ESCAPE '\';";

        var like = "%" + Regex.Replace(target.Name, @"[%_\[\\]", m => "\\" + m.Value) + "%";
        var pattern = NamePattern(target.Name);
        var found = new List<DbUsage>();

        await using var conn = _connections.CreateConnection(inSysDatabase);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        cmd.Parameters.AddWithValue("@p", like);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var schema = reader.GetString(0);
            var name = reader.GetString(1);
            if (inSysDatabase == target.FromSysDatabase && string.Equals(schema, target.Schema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(name, target.Name, StringComparison.OrdinalIgnoreCase)) continue;      // chính nó
            var def = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var (hits, snippets) = Scan(def, pattern);
            if (hits == 0) continue;
            var kind = reader.GetString(2).Trim() switch
            {
                "P" => SqlObjectKind.StoredProcedure,
                "V" => SqlObjectKind.View,
                "TR" => SqlObjectKind.Trigger,
                _ => SqlObjectKind.Function,
            };
            found.Add(new DbUsage(inSysDatabase, schema, name, kind, hits, snippets));
        }
        return found.OrderBy(f => f.Kind).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (int Hits, List<UsageLine> Snippets) Scan(string text, Regex pattern)
    {
        var snippets = new List<UsageLine>();
        var hits = 0;
        var lineNo = 0;
        foreach (var line in text.Split('\n'))
        {
            lineNo++;
            if (!pattern.IsMatch(line)) continue;
            hits++;
            if (snippets.Count < MaxSnippets) snippets.Add(new UsageLine(lineNo, Trim(line)));
        }
        return (hits, snippets);
    }

    /// <summary>Quét file trong source. <paramref name="sourceRoot"/> nên là thư mục App_Data (nhỏ hơn cả source nhiều).</summary>
    public List<SourceUsage> FindInSource(string sourceRoot, string name, CancellationToken ct)
    {
        var pattern = NamePattern(name);
        var byFile = new Dictionary<string, (int Hits, List<UsageLine> Snippets)>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        foreach (var m in _files.FindReferences(sourceRoot, name))
        {
            ct.ThrowIfCancellationRequested();
            if (!pattern.IsMatch(m.LineText)) continue;
            if (!byFile.TryGetValue(m.FilePath, out var e)) e = (0, new List<UsageLine>());
            e.Hits++;
            if (e.Snippets.Count < MaxSnippets) e.Snippets.Add(new UsageLine(m.LineNumber, Trim(m.LineText)));
            byFile[m.FilePath] = e;
            if (++total >= MaxSourceHits) break;
        }
        return byFile.Select(kv => new SourceUsage(kv.Key, kv.Value.Hits, kv.Value.Snippets))
            .OrderBy(f => f.File, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
