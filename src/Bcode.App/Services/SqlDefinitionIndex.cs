using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>
/// Chỉ mục nội dung của Function / Store / View / Trigger để Lookup tìm "chữ có trong procedure" nhanh (như khung Search Text của FCode).
/// Cách làm: giữ toàn bộ định nghĩa (sys.sql_modules) trong bộ nhớ + một bản gz trên đĩa (%AppData%\Bcode\sql-definitions) theo (server, database).
/// Mỗi lần tìm chỉ hỏi 1 câu nhẹ — danh sách object kèm modify_date — rồi nạp lại ĐÚNG những object mới / vừa sửa (xoá thì bỏ khỏi chỉ mục); phần còn lại dùng cache,
/// nên lần đầu mới phải nạp cả database, các lần sau (kể cả mở lại Bcode) gần như tức thì.
/// </summary>
public class SqlDefinitionIndex
{
    private readonly DbConnectionService _connections;

    public SqlDefinitionIndex(DbConnectionService connections) => _connections = connections;

    private sealed class Entry
    {
        public int Id { get; set; }
        public string Modify { get; set; } = "";
        public int Kind { get; set; }
        public string Schema { get; set; } = "";
        public string Name { get; set; } = "";
        public string Text { get; set; } = "";
    }

    private sealed class Index
    {
        public Dictionary<int, Entry> Items { get; } = new();
        public bool LoadedFromDisk;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private readonly Dictionary<string, Index> _indexes = new(StringComparer.OrdinalIgnoreCase);

    public sealed record Hit(SqlObjectInfo Info, int Matches);

    private (string Key, string Path) KeyAndPath(bool useSys)
    {
        var ws = _connections.Current ?? throw new InvalidOperationException("Chưa chọn Workspace (WS).");
        var db = useSys ? ws.SysDatabase : ws.EffectiveAppDatabase;
        var key = (ws.Server + "|" + db).ToLowerInvariant();
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..16];
        return (key, System.IO.Path.Combine(BcodePaths.AppData, "Bcode", "sql-definitions", hash + ".gz"));
    }

    private Index GetIndex(string key)
    {
        lock (_indexes) { if (!_indexes.TryGetValue(key, out var ix)) _indexes[key] = ix = new Index(); return ix; }
    }

    private static SqlObjectKind KindOf(int k) => (SqlObjectKind)k;

    private static void LoadDisk(Index ix, string path)
    {
        ix.LoadedFromDisk = true;
        try
        {
            if (!File.Exists(path)) return;
            using var fs = File.OpenRead(path);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            using var rd = new StreamReader(gz, Encoding.UTF8);
            string? line;
            while ((line = rd.ReadLine()) is not null)
            {
                var e = JsonSerializer.Deserialize<Entry>(line);
                if (e is not null) ix.Items[e.Id] = e;
            }
        }
        catch { ix.Items.Clear(); /* cache hỏng — nạp lại từ DB */ }
    }

    private static void SaveDisk(Index ix, string path)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
            using (var wr = new StreamWriter(gz, new UTF8Encoding(false)))
                foreach (var e in ix.Items.Values) wr.WriteLine(JsonSerializer.Serialize(e));
            File.Move(tmp, path, overwrite: true);
        }
        catch { /* không lưu được thì lần sau nạp lại */ }
    }

    /// <summary>Đồng bộ chỉ mục với database: nạp lại object mới / đã sửa, bỏ object đã xoá. Trả (số object nạp lại, tổng số object trong chỉ mục).</summary>
    public async Task<(int Reloaded, int Total)> SyncAsync(bool useSys, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var (key, path) = KeyAndPath(useSys);
        var ix = GetIndex(key);
        await ix.Gate.WaitAsync(ct);
        try
        {
            if (!ix.LoadedFromDisk) await Task.Run(() => LoadDisk(ix, path), ct);

            await using var conn = _connections.CreateConnection(useSys);
            await conn.OpenAsync(ct);

            // 1) metadata: rất nhẹ
            var live = new Dictionary<int, (string Modify, int Kind, string Schema, string Name)>();
            await using (var cmd = new SqlCommand(@"
SELECT o.object_id, CONVERT(varchar(30), o.modify_date, 126), o.type, s.name, o.name
FROM sys.objects o JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.type IN ('V','P','FN','IF','TF','TR') AND o.is_ms_shipped = 0;", conn) { CommandTimeout = 60 })
            await using (var rd = await cmd.ExecuteReaderAsync(ct))
                while (await rd.ReadAsync(ct))
                {
                    var kind = rd.GetString(2).Trim() switch { "V" => SqlObjectKind.View, "P" => SqlObjectKind.StoredProcedure, "TR" => SqlObjectKind.Trigger, _ => SqlObjectKind.Function };
                    live[rd.GetInt32(0)] = (rd.GetString(1), (int)kind, rd.GetString(3), rd.GetString(4));
                }

            var changed = false;
            foreach (var id in ix.Items.Keys.Where(id => !live.ContainsKey(id)).ToList()) { ix.Items.Remove(id); changed = true; }

            var todo = live.Where(kv => !ix.Items.TryGetValue(kv.Key, out var e) || e.Modify != kv.Value.Modify || e.Name != kv.Value.Name || e.Schema != kv.Value.Schema)
                           .Select(kv => kv.Key).ToList();

            // 2) chỉ nạp định nghĩa của những object mới / đổi, từng lô 150 object
            const int batch = 150;
            for (var i = 0; i < todo.Count; i += batch)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = todo.Skip(i).Take(batch).ToList();
                progress?.Report($"Đang cập nhật chỉ mục nội dung… {Math.Min(i + batch, todo.Count)}/{todo.Count}");
                await using var cmd = new SqlCommand("SELECT m.object_id, m.definition FROM sys.sql_modules m WHERE m.object_id IN (" + string.Join(",", chunk) + ");", conn) { CommandTimeout = 120 };
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                var got = new HashSet<int>();
                while (await rd.ReadAsync(ct))
                {
                    var id = rd.GetInt32(0);
                    var m = live[id];
                    ix.Items[id] = new Entry { Id = id, Modify = m.Modify, Kind = m.Kind, Schema = m.Schema, Name = m.Name, Text = rd.IsDBNull(1) ? "" : rd.GetString(1) };
                    got.Add(id);
                }
                foreach (var id in chunk.Where(id => !got.Contains(id)))   // WITH ENCRYPTION / không đọc được: vẫn ghi nhận để lần sau không hỏi lại
                {
                    var m = live[id];
                    ix.Items[id] = new Entry { Id = id, Modify = m.Modify, Kind = m.Kind, Schema = m.Schema, Name = m.Name, Text = "" };
                }
                changed = true;
            }

            if (changed) await Task.Run(() => SaveDisk(ix, path), ct);
            return (todo.Count, ix.Items.Count);
        }
        finally { ix.Gate.Release(); }
    }

    /// <summary>Tìm <paramref name="term"/> (không phân biệt hoa thường) trong nội dung các object thuộc <paramref name="kinds"/>. Gọi <see cref="SyncAsync"/> trước.</summary>
    public List<Hit> Search(bool useSys, string term, ISet<SqlObjectKind> kinds, int limit = 1000)
    {
        var (key, _) = KeyAndPath(useSys);
        var ix = GetIndex(key);
        Entry[] snapshot;
        lock (ix.Items) snapshot = ix.Items.Values.ToArray();
        var found = new System.Collections.Concurrent.ConcurrentBag<Hit>();
        Parallel.ForEach(snapshot, e =>
        {
            if (!kinds.Contains(KindOf(e.Kind)) || e.Text.Length == 0) return;
            var count = 0; var pos = 0;
            while ((pos = e.Text.IndexOf(term, pos, StringComparison.OrdinalIgnoreCase)) >= 0) { count++; pos += term.Length; if (count >= 9999) break; }
            if (count > 0)
                found.Add(new Hit(new SqlObjectInfo { Schema = e.Schema, Name = e.Name, Kind = KindOf(e.Kind), FromSysDatabase = useSys }, count));
        });
        return found.OrderBy(h => h.Info.Kind).ThenBy(h => h.Info.QualifiedName, StringComparer.OrdinalIgnoreCase).Take(limit).ToList();
    }

    /// <summary>Xoá chỉ mục (bộ nhớ + đĩa) của database này — lần tìm kế tiếp nạp lại từ đầu.</summary>
    public void Reset(bool useSys)
    {
        var (key, path) = KeyAndPath(useSys);
        lock (_indexes) _indexes.Remove(key);
        try { File.Delete(path); } catch { /* không xoá được */ }
    }
}
