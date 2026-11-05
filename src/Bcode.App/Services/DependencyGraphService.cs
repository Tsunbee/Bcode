using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Một nút của sơ đồ phụ thuộc. Kind: menu | file | proc | func | view | table | trigger.
/// Key duy nhất: "app:dbo.ten" / "sys:dbo.ten" cho object database, "file:đường dẫn", "menu:wmenu_id".</summary>
public sealed record DepNode(string Kind, string Key, string Label, bool Sys = false, string Schema = "dbo", string? Path = null, string? Note = null);

/// <summary>Các nút quanh 1 nút: Up = được dùng bởi (ai gọi / ai trỏ tới nó), Down = dùng tới (nó gọi / đọc / include gì).</summary>
public sealed record DepNeighbors(DepNode Center, List<DepNode> Up, List<DepNode> Down, List<string> Warnings);

/// <summary>1 dòng của "Phân tích ảnh hưởng": nút ở tầng Level (1 = dùng trực tiếp), Via = nút ở tầng trên dẫn tới nó.</summary>
public sealed record DepImpact(int Level, DepNode Node, string Via);

/// <summary>
/// Sơ đồ phụ thuộc menu → controller → include → procedure / function / view → bảng. Không dựng trước cả đồ thị: chỉ tra QUANH nút đang xem, khi cần,
/// dùng lại những gì Bcode đã có — chỉ ĐỌC, không cài trigger, không gắn gì vào lúc sửa procedure:
///  · object database, "dùng tới": <c>sys.sql_expression_dependencies</c> (SQL Server tự ghi khi tạo / sửa object) + dò tên object trong định nghĩa (bắt cả tên nằm
///    trong chuỗi SQL động). "Được dùng bởi": quét định nghĩa như "Ai đang dùng" (<see cref="UsageSearchService"/>) + chỉ mục source (<see cref="SourceIndexService"/>).
///  · file controller: đọc file, lấy include (<!ENTITY … SYSTEM "…">) và tên object database trong file; "được dùng bởi" = menu trỏ tới file + file khác nhắc tới tên file.
///  · menu: file controller theo SysId / Link (cùng quy tắc với "Ai đang dùng").
/// Kết quả nhớ trong bộ nhớ theo chữ ký database (số object + ngày sửa mới nhất) và ngày sửa file — mở lại sơ đồ không hỏi database lại nếu không có gì đổi.
/// </summary>
public sealed class DependencyGraphService
{
    private readonly DbConnectionService _connections;
    private readonly SqlObjectBrowserService _browser;
    private readonly UsageSearchService _usages;
    private readonly Func<IEnumerable<(WCommandItem Item, string Path)>> _menus;
    private readonly SourceIndexService _index = new();

    private static readonly ConcurrentDictionary<string, DepNeighbors> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex Token = new(@"[\w$#]+", RegexOptions.Compiled);
    private static readonly Regex EntityInclude = new(@"<!ENTITY\s+(?:%\s*)?[\w.\-]+\s+SYSTEM\s+""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private const int MaxImpactNodes = 3000, MaxImpactDepth = 4;

    public DependencyGraphService(DbConnectionService connections, SqlObjectBrowserService browser, UsageSearchService usages,
        Func<IEnumerable<(WCommandItem Item, string Path)>> menus)
    {
        _connections = connections;
        _browser = browser;
        _usages = usages;
        _menus = menus;
    }

    private string? SourceRoot => string.IsNullOrWhiteSpace(_connections.Current?.SourcePath) ? null : _connections.Current!.SourcePath;

    // ---- Nút ---------------------------------------------------------------------------------------------------------------

    public static string KindOf(SqlObjectKind k) => k switch
    {
        SqlObjectKind.StoredProcedure => "proc",
        SqlObjectKind.View => "view",
        SqlObjectKind.Table => "table",
        SqlObjectKind.Trigger => "trigger",
        _ => "func",
    };

    private static SqlObjectKind ObjectKind(string kind) => kind switch
    {
        "proc" => SqlObjectKind.StoredProcedure,
        "view" => SqlObjectKind.View,
        "table" => SqlObjectKind.Table,
        "trigger" => SqlObjectKind.Trigger,
        _ => SqlObjectKind.Function,
    };

    private static string KindOfType(string type) => type.Trim() switch
    {
        "P" => "proc",
        "V" => "view",
        "U" => "table",
        "TR" => "trigger",
        _ => "func",
    };

    public static DepNode ObjectNode(SqlObjectInfo o, string? note = null) =>
        new(KindOf(o.Kind), (o.FromSysDatabase ? "sys:" : "app:") + o.QualifiedName, o.QualifiedName, o.FromSysDatabase, o.Schema, null, note);

    public DepNode FileNode(string path, string? note = null)
    {
        var root = SourceRoot is { } r ? SourceIndexService.ControllersDir(r) : null;
        string label;
        try { label = root is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(root, path) : Path.GetFileName(path); }
        catch { label = Path.GetFileName(path); }
        return new("file", "file:" + path, label, Path: path, Note: note);
    }

    private static DepNode MenuNode(WCommandItem item, string path) =>
        new("menu", "menu:" + item.WMenuId, item.Bar + " (" + item.WMenuId + ")", Path: path.Length > 0 ? path : null);

    public static SqlObjectInfo ToObject(DepNode n) => new() { Schema = n.Schema, Name = n.Label.Contains('.') ? n.Label[(n.Label.IndexOf('.') + 1)..] : n.Label, Kind = ObjectKind(n.Kind), FromSysDatabase = n.Sys };

    /// <summary>Menu WCommand trỏ tới file controller: tên file (không đuôi) trùng SysId (hoặc SysId_ / SysId.) hoặc trùng tên file trang trong Link.
    /// Cùng quy tắc với tab "Ai đang dùng".</summary>
    public static bool MenuMatchesFile(WCommandItem item, string file)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var sys = (item.SysId ?? "").Trim();
        var link = Path.GetFileNameWithoutExtension((item.Link ?? "").Replace('/', '\\').Trim());
        return (sys.Length > 0 && (name.Equals(sys, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(sys + "_", StringComparison.OrdinalIgnoreCase) || name.StartsWith(sys + ".", StringComparison.OrdinalIgnoreCase)))
               || (link.Length > 0 && name.Equals(link, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Tên object database (để dò tên trong định nghĩa / file) ----------------------------------------------------------

    private (string Key, Dictionary<string, List<SqlObjectInfo>> ByName)? _names;

    /// <summary>Tên mọi object của 2 database (lấy từ cache của cây SQL Object — không hỏi database). Chưa mở cây lần nào thì có thể thiếu.</summary>
    private Dictionary<string, List<SqlObjectInfo>> ObjectNames()
    {
        string key;
        try { key = _browser.CacheId(false) + "|" + _browser.CacheId(true); } catch { return new(StringComparer.OrdinalIgnoreCase); }
        var sig = key + "|" + (_browser.LoadCache(false)?.Signature ?? "") + "|" + (_browser.LoadCache(true)?.Signature ?? "");
        if (_names is { } n && n.Key == sig) return n.ByName;
        var map = new Dictionary<string, List<SqlObjectInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sys in new[] { false, true })
            foreach (var o in _browser.LoadCache(sys)?.Items ?? new List<SqlObjectInfo>())
            {
                if (o.Name.Length < 3) continue;
                if (!map.TryGetValue(o.Name, out var l)) map[o.Name] = l = new List<SqlObjectInfo>();
                l.Add(o);
            }
        _names = (sig, map);
        return map;
    }

    /// <summary>Tên object database xuất hiện trong văn bản (theo ranh giới từ). Cùng tên ở cả 2 database: ưu tiên database <paramref name="preferSys"/>.</summary>
    private List<SqlObjectInfo> NamesIn(string text, bool? preferSys)
    {
        var map = ObjectNames();
        var found = new Dictionary<string, SqlObjectInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Token.Matches(text))
        {
            if (m.Length < 3 || found.ContainsKey(m.Value) || !map.TryGetValue(m.Value, out var l)) continue;
            found[m.Value] = (preferSys is bool p ? l.FirstOrDefault(o => o.FromSysDatabase == p) : null) ?? l[0];
        }
        return found.Values.ToList();
    }

    // ---- Tra quanh 1 nút ---------------------------------------------------------------------------------------------------

    /// <param name="force">true = bỏ kết quả đã nhớ, tra lại (nút Làm mới).</param>
    public async Task<DepNeighbors> NeighborsAsync(DepNode node, bool force, Action<string> progress, CancellationToken ct)
    {
        var stamp = await StampAsync(node, ct);
        var cacheKey = node.Key + "|" + stamp;
        if (!force && Cache.TryGetValue(cacheKey, out var hit)) return hit;
        var result = node.Kind switch
        {
            "menu" => MenuNeighbors(node),
            "file" => await FileNeighborsAsync(node, force, progress, ct),
            _ => await ObjectNeighborsAsync(node, force, progress, ct),
        };
        Cache[cacheKey] = result;
        return result;
    }

    /// <summary>"Dấu" để biết kết quả đã nhớ còn đúng: chữ ký 2 database (1 câu rất nhẹ trên sys.objects) + ngày sửa file. Lỗi kết nối thì dùng dấu rỗng.</summary>
    private async Task<string> StampAsync(DepNode node, CancellationToken ct)
    {
        var parts = new List<string> { _connections.Current?.Name ?? "" };
        try
        {
            parts.Add(await _browser.GetSignatureAsync(false));
            parts.Add(await _browser.GetSignatureAsync(true));
        }
        catch { parts.Add("offline"); }
        if (node.Path is { } p && node.Kind == "file") try { parts.Add(File.GetLastWriteTimeUtc(p).Ticks.ToString()); } catch { }
        return string.Join("|", parts);
    }

    private async Task<DepNeighbors> ObjectNeighborsAsync(DepNode node, bool force, Action<string> progress, CancellationToken ct)
    {
        var obj = ToObject(node);
        var up = new List<DepNode>();
        var down = new List<DepNode>();
        var warn = new List<string>();

        // Dùng tới: phụ thuộc SQL Server ghi sẵn + tên object trong định nghĩa (SQL động / chuỗi)
        progress("Đang đọc phụ thuộc của " + obj.QualifiedName + "…");
        try
        {
            await using var conn = _connections.CreateConnection(obj.FromSysDatabase);
            await conn.OpenAsync(ct);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var cmd = new SqlCommand(@"
SELECT DISTINCT ISNULL(d.referenced_schema_name, ISNULL(SCHEMA_NAME(o.schema_id), 'dbo')), d.referenced_entity_name, o.type
FROM sys.sql_expression_dependencies d
LEFT JOIN sys.objects o ON o.object_id = d.referenced_id
WHERE d.referencing_id = OBJECT_ID(@full) AND d.referenced_database_name IS NULL AND d.referenced_entity_name IS NOT NULL;", conn) { CommandTimeout = 30 })
            {
                cmd.Parameters.AddWithValue("@full", obj.QualifiedName);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var schema = r.GetString(0); var name = r.GetString(1);
                    if (name.Equals(obj.Name, StringComparison.OrdinalIgnoreCase) || !known.Add(name)) continue;
                    var kind = r.IsDBNull(2) ? "table" : KindOfType(r.GetString(2));
                    down.Add(ObjectNode(new SqlObjectInfo { Schema = schema, Name = name, Kind = ObjectKind(kind), FromSysDatabase = obj.FromSysDatabase }, r.IsDBNull(2) ? "chưa có trong database" : null));
                }
            }
            if (obj.Kind == SqlObjectKind.Table)
            {
                // Bảng: trigger gắn trên bảng
                await using var tc = new SqlCommand("SELECT SCHEMA_NAME(o.schema_id), o.name FROM sys.triggers t JOIN sys.objects o ON o.object_id = t.object_id WHERE t.parent_id = OBJECT_ID(@full);", conn);
                tc.Parameters.AddWithValue("@full", obj.QualifiedName);
                await using var r = await tc.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    down.Add(ObjectNode(new SqlObjectInfo { Schema = r.GetString(0), Name = r.GetString(1), Kind = SqlObjectKind.Trigger, FromSysDatabase = obj.FromSysDatabase }, "trigger trên bảng"));
            }
            else
            {
                string def;
                await using (var dc = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(@full));", conn) { CommandTimeout = 30 })
                {
                    dc.Parameters.AddWithValue("@full", obj.QualifiedName);
                    def = Convert.ToString(await dc.ExecuteScalarAsync(ct)) ?? "";
                }
                foreach (var o in NamesIn(StripComments(def), obj.FromSysDatabase))
                    if (!o.Name.Equals(obj.Name, StringComparison.OrdinalIgnoreCase) && known.Add(o.Name))
                        down.Add(ObjectNode(o, "tên có trong nội dung (có thể là SQL động)"));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { warn.Add("Không đọc được phụ thuộc: " + ex.Message); }

        // Được dùng bởi: object trong 2 database (quét định nghĩa — bắt cả SQL động) + file controller trong source
        progress("Đang tìm ai dùng " + obj.Name + "…");
        async Task Db(bool sys)
        {
            try
            {
                foreach (var u in await _usages.FindInDatabaseAsync(obj, sys, ct))
                    lock (up) up.Add(ObjectNode(new SqlObjectInfo { Schema = u.Schema, Name = u.Name, Kind = u.Kind, FromSysDatabase = u.Sys }, u.Hits + " chỗ"));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lock (warn) warn.Add((sys ? "Sys Data" : "App Data") + ": " + ex.Message); }
        }
        async Task<SourceIndexService.Result?> Src() => SourceRoot is { } root ? await _index.FindAsync(root, obj.Name, force, progress, ct) : null;
        var files = Src();
        await Task.WhenAll(Db(false), Db(true), files);
        var res = await files;
        if (res is null) warn.Add("Project chưa khai Source Path — không tìm được file controller.");
        else if (res.Warning is not null) warn.Add(res.Warning);
        else foreach (var f in res.Files) up.Add(FileNode(f.File, f.Hits + " chỗ"));

        return new DepNeighbors(node, Sort(up), Sort(down), warn);
    }

    private async Task<DepNeighbors> FileNeighborsAsync(DepNode node, bool force, Action<string> progress, CancellationToken ct)
    {
        var path = node.Path ?? "";
        var up = new List<DepNode>();
        var down = new List<DepNode>();
        var warn = new List<string>();
        string text = "";
        try { text = await File.ReadAllTextAsync(path, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { warn.Add("Không đọc được file: " + ex.Message); }

        // Dùng tới: include (<!ENTITY … SYSTEM "…">) + tên object database có trong file
        var dir = Path.GetDirectoryName(path) ?? "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in EntityInclude.Matches(text))
        {
            string full;
            try { full = Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value.Replace('/', '\\'))); } catch { continue; }
            if (seen.Add(full)) down.Add(FileNode(full, File.Exists(full) ? "include" : "include — không thấy file"));
        }
        foreach (var o in NamesIn(text, null)) down.Add(ObjectNode(o));

        // Được dùng bởi: menu trỏ tới file + file khác nhắc tới tên file (include, controller gọi controller)
        foreach (var (item, mpath) in _menus())
            if (MenuMatchesFile(item, path)) up.Add(MenuNode(item, mpath));
        if (SourceRoot is { } root)
        {
            var res = await _index.FindAsync(root, Path.GetFileNameWithoutExtension(path), force, progress, ct);
            if (res.Warning is not null) warn.Add(res.Warning);
            else foreach (var f in res.Files) if (!f.File.Equals(path, StringComparison.OrdinalIgnoreCase)) up.Add(FileNode(f.File, f.Hits + " chỗ"));
        }
        return new DepNeighbors(node, Sort(up), Sort(down), warn);
    }

    private DepNeighbors MenuNeighbors(DepNode node)
    {
        var id = node.Key["menu:".Length..];
        var down = new List<DepNode>();
        var warn = new List<string>();
        var item = _menus().Select(x => x.Item).FirstOrDefault(i => i.WMenuId == id);
        if (item is null) warn.Add("Không thấy menu " + id + " trong cây WCommand đã nạp.");
        else if (SourceRoot is not { } root) warn.Add("Project chưa khai Source Path — không tìm được file controller.");
        else
        {
            var controllers = SourceIndexService.ControllersDir(root);
            foreach (var folder in SourceIndexService.Folders)
            {
                var d = Path.Combine(controllers, folder);
                if (!Directory.Exists(d)) continue;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.TopDirectoryOnly))
                        if (MenuMatchesFile(item, f)) down.Add(FileNode(f, folder));
                }
                catch (Exception ex) { warn.Add(folder + ": " + ex.Message); }
            }
        }
        return new DepNeighbors(node, new List<DepNode>(), Sort(down), warn);
    }

    // ---- Phân tích ảnh hưởng (dò ngược nhiều tầng) -------------------------------------------------------------------------

    /// <summary>
    /// Dò ngược từ <paramref name="start"/> tới tận menu. Tầng 1 tra đủ như sơ đồ (quét định nghĩa — bắt cả SQL động — và chỉ mục source có xác nhận).
    /// Các tầng sau tra THEO LÔ cho nhẹ: mỗi tầng 1 câu sys.sql_expression_dependencies cho mỗi database (IN theo lô 500 tên) + chỉ mục source ngay trong
    /// bộ nhớ (không mở lại file). Mỗi file tìm được quy ra menu trỏ tới nó ngay. Tối đa <see cref="MaxImpactDepth"/> tầng, <see cref="MaxImpactNodes"/> nút.
    /// </summary>
    public async Task<(List<DepImpact> Rows, bool Truncated, List<string> Warnings)> ImpactAsync(DepNode start, Action<string> progress, CancellationToken ct)
    {
        var rows = new List<DepImpact>();
        var warn = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.Key };
        var menus = _menus().ToList();
        var truncated = false;

        // thêm 1 nút; file thì thêm luôn các menu trỏ tới nó (tầng kế)
        bool Add(DepNode n, int level, string via, List<DepNode> next)
        {
            if (truncated || !seen.Add(n.Key)) return false;
            rows.Add(new DepImpact(level, n, via));
            if (n.Kind != "menu") next.Add(n);
            if (n.Kind == "file" && n.Path is { } p)
                foreach (var (item, mpath) in menus)
                    if (MenuMatchesFile(item, p)) { var m = MenuNode(item, mpath); if (seen.Add(m.Key)) rows.Add(new DepImpact(level + 1, m, n.Label)); }
            if (rows.Count >= MaxImpactNodes) truncated = true;
            return true;
        }

        progress("Tầng 1: đang tìm ai dùng " + start.Label + "…");
        var frontier = new List<DepNode>();
        if (start.Kind != "menu")
            foreach (var u in (await NeighborsAsync(start, false, progress, ct)).Up) Add(u, 1, start.Label, frontier);

        for (var level = 2; level <= MaxImpactDepth && frontier.Count > 0 && !truncated; level++)
        {
            progress($"Tầng {level}: {frontier.Count} nút…");
            var next = new List<DepNode>();
            var dbNodes = frontier.Where(n => n.Kind is not ("file" or "menu")).ToList();
            var fileNodes = frontier.Where(n => n.Kind == "file").ToList();

            // object database dùng các object ở tầng trên (phụ thuộc SQL Server ghi sẵn)
            foreach (var sys in new[] { false, true })
            {
                var names = dbNodes.Select(n => ToObject(n).Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var (refName, o) in await DependentsAsync(sys, names, ct, warn))
                {
                    var via = dbNodes.FirstOrDefault(n => ToObject(n).Name.Equals(refName, StringComparison.OrdinalIgnoreCase))?.Label ?? refName;
                    Add(ObjectNode(o), level, via, next);
                }
            }

            // file controller nhắc tới tên object / tên file ở tầng trên (chỉ mục source trong bộ nhớ)
            if (SourceRoot is { } root)
            {
                var wanted = dbNodes.Select(n => (Name: ToObject(n).Name, n.Label))
                    .Concat(fileNodes.Select(n => (Name: Path.GetFileNameWithoutExtension(n.Path ?? n.Label), n.Label))).ToList();
                var hits = await _index.CandidateFilesAsync(root, wanted.Select(w => w.Name).ToList(), progress, ct);
                foreach (var (name, label) in wanted)
                    if (hits.TryGetValue(name, out var files))
                        foreach (var f in files) Add(FileNode(f), level, label, next);
            }
            frontier = next;
        }
        return (rows, truncated, warn);
    }

    /// <summary>Object (procedure / function / view / trigger) có phụ thuộc vào một trong <paramref name="names"/> — 1 câu cho mỗi lô 500 tên.</summary>
    private async Task<List<(string RefName, SqlObjectInfo Obj)>> DependentsAsync(bool sys, List<string> names, CancellationToken ct, List<string> warn)
    {
        var list = new List<(string, SqlObjectInfo)>();
        if (names.Count == 0) return list;
        try
        {
            await using var conn = _connections.CreateConnection(sys);
            await conn.OpenAsync(ct);
            foreach (var chunk in names.Chunk(500))
            {
                await using var cmd = new SqlCommand { Connection = conn, CommandTimeout = 60 };
                var ps = chunk.Select((n, i) => { cmd.Parameters.Add("@n" + i, System.Data.SqlDbType.NVarChar, 256).Value = n; return "@n" + i; });
                cmd.CommandText = @"
SELECT DISTINCT d.referenced_entity_name, SCHEMA_NAME(o.schema_id), o.name, o.type
FROM sys.sql_expression_dependencies d
JOIN sys.objects o ON o.object_id = d.referencing_id
WHERE o.is_ms_shipped = 0 AND d.referenced_entity_name IN (" + string.Join(", ", ps) + ");";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    list.Add((r.GetString(0), new SqlObjectInfo { Schema = r.GetString(1), Name = r.GetString(2), Kind = ObjectKind(KindOfType(r.GetString(3))), FromSysDatabase = sys }));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { var m = (sys ? "Sys Data: " : "App Data: ") + ex.Message; if (!warn.Contains(m)) warn.Add(m); }
        return list;
    }

    // ---- Tiện ích ----------------------------------------------------------------------------------------------------------

    private static readonly Regex Comments = new(@"--[^\r\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
    private static string StripComments(string sql) => Comments.Replace(sql ?? "", " ");

    private static readonly string[] KindOrder = { "menu", "file", "proc", "func", "view", "trigger", "table" };
    private static List<DepNode> Sort(List<DepNode> nodes) =>
        nodes.GroupBy(n => n.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
             .OrderBy(n => Array.IndexOf(KindOrder, n.Kind)).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase).ToList();
}
