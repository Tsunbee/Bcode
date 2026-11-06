using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Bcode.App.Models;
using Microsoft.Data.SqlClient;

namespace Bcode.App.Services;

/// <summary>Một nút của sơ đồ phụ thuộc. Kind: menu | file | proc | func | view | table | trigger | unknown (tên không thấy trong database nào, không đoán được loại).
/// Key duy nhất: "app:dbo.ten" / "sys:dbo.ten" cho object database, "file:đường dẫn", "menu:wmenu_id".
/// Encrypted = object tạo WITH ENCRYPTION (không đọc được nội dung). StdVersion = file lấy từ kho source chuẩn (phiên bản này) thay cho file .f mã hoá
/// của chương trình — Path là file .xml trong kho.</summary>
public sealed record DepNode(string Kind, string Key, string Label, bool Sys = false, string Schema = "dbo", string? Path = null, string? Note = null, bool Encrypted = false,
    string? StdVersion = null);

/// <summary>Các nút quanh 1 nút: Up = được dùng bởi (ai gọi / ai trỏ tới nó), Down = dùng tới (nó gọi / đọc / include gì).</summary>
/// <param name="StdPending">true = chỉ mục source chuẩn (cho file .f mã hoá) đang dựng nền — kết quả chưa đủ, không nhớ; sơ đồ tự tải lại khi xong.</param>
public sealed record DepNeighbors(DepNode Center, List<DepNode> Up, List<DepNode> Down, List<string> Warnings, bool StdPending = false);

/// <summary>1 dòng của "Phân tích ảnh hưởng": nút ở tầng Level (1 = dùng trực tiếp), Via = nút ở tầng trên dẫn tới nó.</summary>
public sealed record DepImpact(int Level, DepNode Node, string Via);

/// <summary>
/// Sơ đồ phụ thuộc menu → controller → include → procedure / function / view → bảng. Không dựng trước cả đồ thị: chỉ tra QUANH nút đang xem, khi cần,
/// dùng lại những gì Bcode đã có — chỉ ĐỌC, không cài trigger, không gắn gì vào lúc sửa procedure:
///  · object database, "dùng tới": <c>sys.sql_expression_dependencies</c> (SQL Server tự ghi khi tạo / sửa object) + dò tên object trong định nghĩa (bắt cả tên nằm
///    trong chuỗi SQL động). "Được dùng bởi": quét định nghĩa như "Ai đang dùng" (<see cref="UsageSearchService"/>) + object mã hoá gọi tới (theo sys.sql_expression_dependencies — không quét được định nghĩa của chúng)
///    + chỉ mục source (<see cref="SourceIndexService"/>).
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

    /// <summary>Loại nút theo sys.objects.type của object ĐƯỢC THAM CHIẾU (kể cả object hệ thống / mã hoá). null = không có type (không thấy object).</summary>
    private static string? KindOfRefType(string? type) => type?.Trim() switch
    {
        null or "" => null,
        "P" or "X" or "PC" or "RF" => "proc",
        "FN" or "IF" or "TF" or "FS" or "FT" or "AF" => "func",
        "V" => "view",
        "U" or "S" or "IT" => "table",
        "TR" or "TA" => "trigger",
        _ => "unknown",
    };

    public static DepNode ObjectNode(SqlObjectInfo o, string? note = null) =>
        new(KindOf(o.Kind), (o.FromSysDatabase ? "sys:" : "app:") + o.QualifiedName, o.QualifiedName, o.FromSysDatabase, o.Schema, null, note);

    private static DepNode DbNode(string kind, string schema, string name, bool sys, string? note, bool encrypted = false) =>
        new(kind, (sys ? "sys:" : "app:") + schema + "." + name, schema + "." + name, sys, schema, null, note, encrypted);

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

    /// <summary>Menu mở sơ đồ từ cây WCommand (cả menu nhóm, menu con) — nhớ lại để tra được cả menu không phải lá.</summary>
    private readonly ConcurrentDictionary<string, WCommandItem> _menuItems = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Nút sơ đồ cho 1 menu trên cây WCommand (chuột phải → Sơ đồ phụ thuộc…).</summary>
    public DepNode MenuNodeOf(WCommandItem item)
    {
        _menuItems[item.WMenuId] = item;
        return MenuNode(item, "");
    }

    /// <summary>File của 1 menu đúng như File Lookup hiện khi bấm menu (MainForm gán = FileLookupService.BuildTreeForMenuItem).
    /// null = dò theo SysId / Link trong các thư mục controller như trước.</summary>
    public Func<WCommandItem, IReadOnlyList<string>>? MenuFiles { get; set; }

    // ---- Source chuẩn cho file .f mã hoá ---------------------------------------------------------------------------------

    /// <summary>Kho source (Template → kho SourceCollection + kho bổ sung) và mã phiên bản đã khai của project — MainForm gán. null / kho rỗng = không dò.</summary>
    public Func<(IReadOnlyList<string> Roots, string? VersionCode)>? StandardSource { get; set; }

    /// <summary>Giải mã 1 file .f mã hoá qua helper FDecrypt (dùng fsystem.dll của FCode). Trả nội dung đã giải, null = không giải được — MainForm gán.
    /// Có cái này thì file .f mã hoá đọc THẲNG bản giải mã của chính file (đúng bản khách đang chạy), không cần dò kho source chuẩn.</summary>
    public Func<string, string?>? DecryptF { get; set; }

    private static readonly object VersionsLock = new();
    private static (string Key, DateTime At, List<string> Versions)? _versions;

    /// <summary>Kho + phiên bản source chuẩn của project (đoán như Cấp source: mã phiên bản đã khai, rồi tên thư mục). null = không dò được (<paramref name="note"/> nói vì sao).</summary>
    private (IReadOnlyList<string> Roots, string Version, List<string> All)? StdContext(out string? note)
    {
        note = null;
        if (StandardSource?.Invoke() is not { Roots.Count: > 0 } s || SourceRoot is not { } root) return null;
        var vkey = string.Join(";", s.Roots);
        List<string> versions;
        lock (VersionsLock)
        {
            if (_versions is { } c && c.Key == vkey && DateTime.Now - c.At < TimeSpan.FromMinutes(10)) versions = c.Versions;
            else
            {
                try { versions = SourceGrantService.ListVersions(s.Roots); }
                catch (Exception ex) { note = "Không đọc được kho source (" + ex.Message + ") — không dò được các file .f mã hoá."; return null; }
                _versions = (vkey, DateTime.Now, versions);
            }
        }
        var chosen = SourceGrantService.DetectVersion(root, versions, out var why, s.VersionCode);
        if (chosen is null) { note = "Chưa rõ phiên bản source chuẩn của project (" + why + ") — khai Mã phiên bản của project để dò các file .f mã hoá."; return null; }
        return (s.Roots, chosen, versions);
    }

    /// <summary>Nút file cho bản source chuẩn của 1 file .f mã hoá: nhãn theo đường dẫn trong chương trình (đuôi .xml), Path = file trong kho.</summary>
    private DepNode StdFileNode(string projectFile, string stdFile, string version, string? note)
    {
        var label = FileNode(projectFile).Label;
        if (label.EndsWith(".f", StringComparison.OrdinalIgnoreCase)) label = label[..^2] + ".xml";
        return new DepNode("file", "file:" + stdFile, label, Path: stdFile, Note: note, StdVersion: version);
    }

    /// <summary>File .f mã hoá (có &lt;Encrypted&gt;, không có .xml cạnh nó) → nội dung đọc được: ưu tiên GIẢI MÃ chính file qua helper FDecrypt (đúng bản khách
    /// đang chạy); không giải được thì lấy bản .xml trong kho source chuẩn (chỉ mục nếu đã dựng, không thì tìm thẳng 1 file như Cấp source nhanh).
    /// Version = "giải mã" khi lấy từ helper, hoặc tên phiên bản khi lấy từ kho.</summary>
    private (string Path, string Version, string Text)? StandardTextFor(string file, string text)
    {
        if (!file.EndsWith(".f", StringComparison.OrdinalIgnoreCase) || text.IndexOf("<Encrypted>", StringComparison.OrdinalIgnoreCase) < 0) return null;
        try { if (File.Exists(file[..^2] + ".xml")) return null; } catch { return null; }
        // 1) Giải mã chính file (bản thật của khách) nếu có helper + license
        if (DecryptF?.Invoke(file) is { Length: > 0 } decrypted) return (file, "giải mã", decrypted);
        // 2) Không giải được → bản .xml trong kho source chuẩn
        if (SourceRoot is not { } root || StdContext(out _) is not { } sc) return null;
        string? std = null, ver = null;
        if (_index.StandardOf(root, sc.Roots, sc.Version, file) is { } hit) (std, ver) = hit;
        else if (SourceGrantService.SplitProjectPath(file) is { } sp)
        {
            var row = SourceGrantService.ResolveNearestLower(sc.Roots, sc.Version, sc.All, file, sp.Root);
            (std, ver) = (row.SourcePath, row.FromVersion);
        }
        if (std is null || ver is null) return null;
        try { return (std, ver, File.ReadAllText(std)); } catch { return null; }
    }

    private const int MaxMenuFilesRead = 300;
    /// <summary>File nhị phân không dò tên object (đọc ra toàn rác). .rpt vẫn dò — Crystal Report có tên bảng / store trong file.</summary>
    private static readonly HashSet<string> BinaryExt = new(StringComparer.OrdinalIgnoreCase)
        { ".xlsx", ".xls", ".xlsm", ".xlsb", ".docx", ".doc", ".pdf", ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".zip", ".rar", ".7z", ".dll", ".exe", ".pdb" };
    private const long MaxMenuFileBytes = 8L << 20;

    private static string MenuInfo(WCommandItem m)
    {
        var parts = new List<string> { "wmenu_id " + m.WMenuId };
        void Add(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) parts.Add(k + " " + v.Trim()); }
        Add("menu_id", m.MenuId); Add("sysid", m.SysId); Add("link", m.Link); Add("parameter", m.Parameter); Add("type", m.Type);
        return string.Join(" · ", parts);
    }

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
        var dbStamp = await DbStampAsync();
        var stamp = dbStamp;
        if (node.Path is { } p && node.Kind == "file") try { stamp += "|" + File.GetLastWriteTimeUtc(p).Ticks; } catch { }
        var cacheKey = node.Key + "|" + stamp;
        if (!force && Cache.TryGetValue(cacheKey, out var hit)) return hit;
        var result = node.Kind switch
        {
            "menu" => await MenuNeighborsAsync(node, progress, ct),
            "file" => await FileNeighborsAsync(node, force, progress, ct),
            _ => await ObjectNeighborsAsync(node, force, dbStamp, progress, ct),
        };
        result = await MarkEncryptedAsync(result, dbStamp, ct);
        if (!result.StdPending) Cache[cacheKey] = result;   // đang dựng chỉ mục source chuẩn: không nhớ, lần tải lại sau khi xong sẽ tra lại
        return result;
    }

    /// <summary>"Dấu" để biết kết quả đã nhớ còn đúng: chữ ký 2 database (1 câu rất nhẹ trên sys.objects). Lỗi kết nối thì dùng dấu rỗng.
    /// (Nút file cộng thêm ngày sửa file.)</summary>
    private async Task<string> DbStampAsync()
    {
        var parts = new List<string> { _connections.Current?.Name ?? "" };
        try
        {
            parts.Add(await _browser.GetSignatureAsync(false));
            parts.Add(await _browser.GetSignatureAsync(true));
        }
        catch { parts.Add("offline"); }
        return string.Join("|", parts);
    }

    // ---- Object mã hoá (WITH ENCRYPTION) -----------------------------------------------------------------------------------

    private static readonly ConcurrentDictionary<string, HashSet<string>> EncryptedCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tên đầy đủ (schema.tên) các object mã hoá của 1 database — 1 câu nhẹ trên sys.objects, nhớ theo chữ ký database. Lỗi thì trả tập rỗng.</summary>
    private async Task<HashSet<string>> EncryptedNamesAsync(bool sys, string dbStamp, CancellationToken ct)
    {
        var key = dbStamp + "|" + (sys ? "sys" : "app");
        if (EncryptedCache.TryGetValue(key, out var hit)) return hit;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = _connections.CreateConnection(sys);
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand(@"
SELECT SCHEMA_NAME(o.schema_id), o.name FROM sys.objects o
WHERE o.type IN ('P','FN','IF','TF','V','TR') AND o.is_ms_shipped = 0 AND OBJECTPROPERTY(o.object_id, 'IsEncrypted') = 1;", conn) { CommandTimeout = 30 };
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) set.Add(r.GetString(0) + "." + r.GetString(1));
            EncryptedCache[key] = set;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* không tra được thì thôi không gắn nhãn "mã hoá" */ }
        return set;
    }

    private static readonly string[] CodeKinds = { "proc", "func", "view", "trigger" };

    private static DepNode Mark(DepNode n, HashSet<string> app, HashSet<string> sys) =>
        !n.Encrypted && CodeKinds.Contains(n.Kind) && (n.Sys ? sys : app).Contains(n.Label) ? n with { Encrypted = true } : n;

    /// <summary>Gắn nhãn "mã hoá" cho các nút procedure / function / view / trigger tạo WITH ENCRYPTION. Nút giữa mã hoá → nhắc "Dùng tới" chỉ theo phụ thuộc SQL Server ghi sẵn.</summary>
    private async Task<DepNeighbors> MarkEncryptedAsync(DepNeighbors r, string dbStamp, CancellationToken ct)
    {
        if (!new[] { r.Center }.Concat(r.Up).Concat(r.Down).Any(n => CodeKinds.Contains(n.Kind))) return r;
        var app = await EncryptedNamesAsync(false, dbStamp, ct);
        var sys = await EncryptedNamesAsync(true, dbStamp, ct);
        var center = Mark(r.Center, app, sys);
        var warn = r.Warnings;
        if (center.Encrypted)
            warn = new List<string>(warn) { "Object mã hoá (WITH ENCRYPTION): không đọc được nội dung — \"Dùng tới\" chỉ gồm phụ thuộc SQL Server ghi sẵn, không dò được tên nằm trong SQL động." };
        return new DepNeighbors(center, r.Up.Select(n => Mark(n, app, sys)).ToList(), r.Down.Select(n => Mark(n, app, sys)).ToList(), warn, r.StdPending);
    }

    private async Task<DepNeighbors> ObjectNeighborsAsync(DepNode node, bool force, string dbStamp, Action<string> progress, CancellationToken ct)
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
            // referenced_id NULL không có nghĩa là bảng: "exec ten_proc" / "ten_func(…)" không ghi schema được SQL Server lưu dạng caller-dependent (referenced_id NULL)
            // → tra lại theo tên trong sys.all_objects (có cả object hệ thống, object mã hoá vẫn có type). Vẫn không thấy thì tra database kia, rồi mới đoán.
            var unresolved = new List<(string Schema, string Name, bool SchemaGiven)>();
            await using (var cmd = new SqlCommand(@"
SELECT DISTINCT COALESCE(d.referenced_schema_name, SCHEMA_NAME(o.schema_id), SCHEMA_NAME(x.schema_id), 'dbo'), d.referenced_entity_name,
       COALESCE(o.type, x.type), CASE WHEN d.referenced_schema_name IS NULL THEN 0 ELSE 1 END
FROM sys.sql_expression_dependencies d
LEFT JOIN sys.objects o ON o.object_id = d.referenced_id
OUTER APPLY (SELECT TOP 1 a.schema_id, a.type FROM sys.all_objects a
             WHERE d.referenced_id IS NULL AND a.name = d.referenced_entity_name
               AND (d.referenced_schema_name IS NULL OR a.schema_id = SCHEMA_ID(d.referenced_schema_name))
             ORDER BY CASE WHEN a.schema_id = SCHEMA_ID('dbo') THEN 0 ELSE 1 END, a.is_ms_shipped) x
WHERE d.referencing_id = OBJECT_ID(@full) AND d.referenced_database_name IS NULL AND d.referenced_entity_name IS NOT NULL;", conn) { CommandTimeout = 30 })
            {
                cmd.Parameters.AddWithValue("@full", obj.QualifiedName);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var schema = r.GetString(0); var name = r.GetString(1);
                    if (name.Equals(obj.Name, StringComparison.OrdinalIgnoreCase) || !known.Add(name)) continue;
                    if (KindOfRefType(r.IsDBNull(2) ? null : r.GetString(2)) is { } kind)
                        down.Add(DbNode(kind, schema, name, obj.FromSysDatabase, kind == "unknown" ? "loại " + r.GetString(2).Trim() : null));
                    else unresolved.Add((schema, name, r.GetInt32(3) == 1));
                }
            }
            var def = "";
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
                await using (var dc = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(@full));", conn) { CommandTimeout = 30 })
                {
                    dc.Parameters.AddWithValue("@full", obj.QualifiedName);
                    def = Convert.ToString(await dc.ExecuteScalarAsync(ct)) ?? "";
                }
                foreach (var o in NamesIn(StripComments(def), obj.FromSysDatabase))
                    if (!o.Name.Equals(obj.Name, StringComparison.OrdinalIgnoreCase) && known.Add(o.Name))
                        down.Add(ObjectNode(o, "tên có trong nội dung (có thể là SQL động)"));
            }
            if (unresolved.Count > 0) down.AddRange(await ResolveElsewhereAsync(obj, unresolved, StripComments(def), ct));
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
        // Object mã hoá không quét được định nghĩa → lấy ai gọi tới từ phụ thuộc SQL Server ghi sẵn (chỉ thêm object mã hoá; object thường đã có ở trên)
        async Task EncryptedCallers(bool sys)
        {
            var enc = await EncryptedNamesAsync(sys, dbStamp, ct);
            if (enc.Count == 0) return;
            var w = new List<string>();
            foreach (var (_, o) in await DependentsAsync(sys, new List<string> { obj.Name }, ct, w))
                if (enc.Contains(o.QualifiedName) && !(o.FromSysDatabase == obj.FromSysDatabase && o.QualifiedName.Equals(obj.QualifiedName, StringComparison.OrdinalIgnoreCase)))
                    lock (up) up.Add(ObjectNode(o, "mã hoá — theo phụ thuộc SQL Server ghi sẵn") with { Encrypted = true });
            lock (warn) foreach (var m in w) if (!warn.Contains(m)) warn.Add(m);
        }
        async Task<SourceIndexService.Result?> Src() => SourceRoot is { } root ? await _index.FindAsync(root, obj.Name, force, progress, ct) : null;
        var files = Src();
        await Task.WhenAll(Db(false), Db(true), EncryptedCallers(false), EncryptedCallers(true), files);
        var res = await files;
        var stdPending = false;
        if (res is null) warn.Add("Project chưa khai Source Path — không tìm được file controller.");
        else if (res.Warning is not null) warn.Add(res.Warning);
        else
        {
            foreach (var f in res.Files) up.Add(FileNode(f.File, f.Hits + " chỗ"));
            // File .f mã hoá của chương trình: xét theo bản .xml trong kho source chuẩn (chỉ mục dựng nền — chưa có thì sơ đồ tự tải lại khi xong)
            if (StdContext(out var stdNote) is { } sc)
            {
                var already = new HashSet<string>(res.Files.Select(f => f.File), StringComparer.OrdinalIgnoreCase);
                string? stdStatus = null;
                var stdHits = await Task.Run(() => _index.FindInStandard(SourceRoot!, sc.Roots, sc.Version, sc.All, obj.Name, ct, out stdStatus), ct);
                if (stdStatus is not null) { warn.Add(stdStatus); stdPending = true; }
                foreach (var h in stdHits ?? new())
                    if (!already.Contains(h.ProjectFile)) up.Add(StdFileNode(h.ProjectFile, h.StdFile, h.Version, h.Count + " chỗ · .f mã hoá"));
            }
            else if (stdNote is not null && _index.IsEncryptedIndexedAny(SourceRoot!)) warn.Add(stdNote);
        }

        return new DepNeighbors(node, Sort(up), Sort(down), warn, stdPending);
    }

    /// <summary>Trạng thái dựng chỉ mục source chuẩn của project đang chọn: Building = đang dựng (chưa dùng được), Text = dòng báo kèm % —
    /// sơ đồ hỏi định kỳ để cập nhật dòng báo và tự tải lại khi xong. null = không dò source chuẩn.</summary>
    public (bool Building, string Text)? StandardBuildState()
    {
        if (SourceRoot is not { } root || StdContext(out _) is not { } sc) return null;
        var (building, status, done, total) = _index.StdState(root, sc.Roots, sc.Version);
        return (building, SourceIndexService.StdStatusText(sc.Version, status, done, total));
    }

    /// <summary>Tên phụ thuộc không thấy trong database của object: tra database kia (Sys ↔ App); vẫn không thấy thì ĐOÁN theo cách gọi trong nội dung
    /// (exec X → procedure, X(…) → function) và ghi rõ là đoán; không đoán được thì để "Không xác định" — không mặc định là bảng.</summary>
    private async Task<List<DepNode>> ResolveElsewhereAsync(SqlObjectInfo obj, List<(string Schema, string Name, bool SchemaGiven)> names, string def, CancellationToken ct)
    {
        var other = !obj.FromSysDatabase;
        var found = new Dictionary<string, List<(string Schema, string Type, bool Enc)>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await using var conn = _connections.CreateConnection(other);
            await conn.OpenAsync(ct);
            foreach (var chunk in names.Select(n => n.Name).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(500))
            {
                await using var cmd = new SqlCommand { Connection = conn, CommandTimeout = 30 };
                var ps = chunk.Select((n, i) => { cmd.Parameters.Add("@n" + i, System.Data.SqlDbType.NVarChar, 256).Value = n; return "@n" + i; }).ToList();
                cmd.CommandText = @"
SELECT o.name, SCHEMA_NAME(o.schema_id), o.type, CASE WHEN OBJECTPROPERTY(o.object_id, 'IsEncrypted') = 1 THEN 1 ELSE 0 END
FROM sys.objects o WHERE o.is_ms_shipped = 0 AND o.name IN (" + string.Join(", ", ps) + @")
ORDER BY CASE WHEN o.schema_id = SCHEMA_ID('dbo') THEN 0 ELSE 1 END;";
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    if (!found.TryGetValue(r.GetString(0), out var l)) found[r.GetString(0)] = l = new();
                    l.Add((r.GetString(1), r.GetString(2), r.GetInt32(3) == 1));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* database kia không mở được — chuyển sang đoán */ }

        var otherName = other ? "Sys Data" : "App Data";
        var list = new List<DepNode>();
        foreach (var (schema, name, given) in names)
        {
            var hit = found.TryGetValue(name, out var l) ? l.FirstOrDefault(x => !given || x.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase)) : default;
            if (hit.Type is not null && KindOfRefType(hit.Type) is { } kind)
                list.Add(DbNode(kind, hit.Schema, name, other, "chỉ có ở " + otherName, hit.Enc));
            else if (GuessKind(def, name) is { } g)
                list.Add(DbNode(g.Kind, schema, name, obj.FromSysDatabase, "đoán là " + g.How + " — không thấy trong database"));
            else
                list.Add(DbNode("unknown", schema, name, obj.FromSysDatabase, "không thấy trong database"));
        }
        return list;
    }

    private static readonly HashSet<string> NotFuncBefore = new(StringComparer.OrdinalIgnoreCase) { "INTO", "TABLE", "INSERT", "MERGE", "REFERENCES", "ON", "EXEC", "EXECUTE", "PROCEDURE", "PROC", "FUNCTION", "VIEW" };

    /// <summary>Đoán loại theo cách gọi trong nội dung (đã bỏ comment): <c>exec [schema.]X</c> → procedure; <c>[schema.]X(</c> (không đứng sau INTO / TABLE / ON…) → function.</summary>
    private static (string Kind, string How)? GuessKind(string def, string name)
    {
        if (def.Length == 0) return null;
        var target = @"(?:\[?[\w$#]+\]?\s*\.\s*)?\[?" + Regex.Escape(name) + @"\]?(?![\w$#])";
        if (Regex.IsMatch(def, @"\bEXEC(?:UTE)?\s+(?:@[\w$#]+\s*=\s*)?" + target, RegexOptions.IgnoreCase))
            return ("proc", "procedure (có exec)");
        foreach (Match m in Regex.Matches(def, @"(?<![\w$#.\[\]@])" + target + @"\s*\(", RegexOptions.IgnoreCase))
        {
            var before = Regex.Match(def[..m.Index], @"([A-Za-z_]+)\s*$");
            if (!before.Success || !NotFuncBefore.Contains(before.Groups[1].Value))
                return ("func", "function (có tên(…))");
        }
        return null;
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
        var names = NamesIn(text, null);
        foreach (var o in names) down.Add(ObjectNode(o));
        // .f mã hoá: tên store / procedure nằm trong khối <Encrypted> → lấy thêm từ bản .xml trong kho source chuẩn
        if (await Task.Run(() => StandardTextFor(path, text), ct) is { } std)
        {
            var have = new HashSet<string>(names.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
            var isDec = std.Version == "giải mã";
            foreach (var o in NamesIn(std.Text, null))
                if (have.Add(o.Name)) down.Add(ObjectNode(o, isDec ? "giải mã từ .f" : "theo source " + std.Version));
            warn.Add(isDec ? "File .f mã hoá — đã giải mã, lấy thêm tên object trong khối mã hoá."
                           : "File .f mã hoá — tên object lấy thêm từ source chuẩn " + std.Version + ": " + std.Path);
        }

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

    /// <summary>Quanh 1 menu: thông tin menu (wmenu_id / menu_id / sysid / link — hiện ở thẻ giữa), "Dùng tới" = menu con + file của menu (như File Lookup)
    /// + object database có tên trong các file đó (store / procedure / bảng / view…, ghi "trong &lt;file&gt;").</summary>
    private async Task<DepNeighbors> MenuNeighborsAsync(DepNode node, Action<string> progress, CancellationToken ct)
    {
        var id = node.Key["menu:".Length..];
        var down = new List<DepNode>();
        var warn = new List<string>();
        var item = _menus().Select(x => x.Item).FirstOrDefault(i => i.WMenuId == id) ?? (_menuItems.TryGetValue(id, out var remembered) ? remembered : null);
        if (item is null)
        {
            warn.Add("Không thấy menu " + id + " trong cây WCommand đã nạp.");
            return new DepNeighbors(node, new List<DepNode>(), down, warn);
        }
        var center = node with { Note = MenuInfo(item) };
        foreach (var c in item.Children.OrderBy(c => c.WMenuId, StringComparer.OrdinalIgnoreCase))
        {
            _menuItems[c.WMenuId] = c;
            down.Add(MenuNode(c, "") with { Note = c.Children.Count > 0 ? "menu nhóm con" : "menu con" });
        }
        if (string.IsNullOrWhiteSpace(item.Link) && string.IsNullOrWhiteSpace(item.SysId))
        {
            if (item.Children.Count == 0) warn.Add("Menu không có Link / SysId — không tìm được file controller.");
            return new DepNeighbors(center, new List<DepNode>(), down, warn);
        }
        if (SourceRoot is not { } root)
        {
            warn.Add("Project chưa khai Source Path — không tìm được file controller.");
            return new DepNeighbors(center, new List<DepNode>(), down, warn);
        }

        progress("Đang tìm file của menu " + item.WMenuId + "…");
        var files = new List<string>();
        if (MenuFiles is { } menuFiles)
        {
            try { files.AddRange(await Task.Run(() => menuFiles(item), ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { warn.Add("Không lấy được file của menu: " + ex.Message); }
        }
        else
        {
            var controllers = SourceIndexService.ControllersDir(root);
            foreach (var folder in SourceIndexService.Folders)
            {
                var d = Path.Combine(controllers, folder);
                if (!Directory.Exists(d)) continue;
                try { files.AddRange(Directory.EnumerateFiles(d, "*", SearchOption.TopDirectoryOnly).Where(f => MenuMatchesFile(item, f))); }
                catch (Exception ex) { warn.Add(folder + ": " + ex.Message); }
            }
        }
        files = files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var f in files) down.Add(FileNode(f, Path.GetFileName(Path.GetDirectoryName(f))));

        // Object database có tên trong các file của menu (store / procedure / function / bảng / view…) — dò như "Dùng tới" của 1 file
        progress("Đang đọc " + files.Count + " file của menu…");
        var stdUsed = new List<string>(); var decUsed = new List<string>();
        foreach (var (o, inFiles) in await ObjectsInFilesAsync(files, ct, (f, ver) => { lock (stdUsed) (ver == "giải mã" ? decUsed : stdUsed).Add(Path.GetFileName(f) + (ver == "giải mã" ? "" : " (" + ver + ")")); }))
            down.Add(ObjectNode(o, "trong " + Path.GetFileName(inFiles[0])));
        if (decUsed.Count > 0) warn.Add($"{decUsed.Count} file .f mã hoá — đã giải mã để lấy tên object: " + string.Join(", ", decUsed.Distinct().Take(8)) + (decUsed.Count > 8 ? "…" : ""));
        if (stdUsed.Count > 0) warn.Add($"{stdUsed.Count} file .f mã hoá — tên object lấy thêm từ source chuẩn: " + string.Join(", ", stdUsed.Take(8)) + (stdUsed.Count > 8 ? "…" : ""));
        if (files.Count > MaxMenuFilesRead) warn.Add($"Menu có {files.Count} file — chỉ dò tên object trong {MaxMenuFilesRead} file đầu.");
        if (files.Count == 0) warn.Add("Không thấy file controller nào của menu (theo SysId / Link).");
        return new DepNeighbors(center, new List<DepNode>(), Sort(down), warn);
    }

    /// <summary>
    /// Object database (store / function / bảng / view…) có tên trong các file — cách sơ đồ dò "Dùng tới" của 1 menu, dùng chung cho
    /// "Gợi ý theo menu" của BcodeNote (Table liên quan). Bỏ file nhị phân, file quá <see cref="MaxMenuFileBytes"/>, chỉ đọc <see cref="MaxMenuFilesRead"/> file đầu.
    /// Mỗi object kèm các file có tên nó (theo thứ tự <paramref name="files"/>). Tên lấy từ cache cây SQL Object — xem <see cref="EnsureObjectNamesAsync"/>.
    /// </summary>
    /// <param name="onStandard">Khác null = file .f mã hoá (không có .xml cạnh nó) thì dò thêm trong bản .xml của kho source chuẩn
    /// (<see cref="StandardTextFor"/>); gọi lại với (file, phiên bản source) mỗi lần dùng. null = chỉ đọc chính file (như trước).</param>
    public async Task<List<(SqlObjectInfo Obj, List<string> Files)>> ObjectsInFilesAsync(IEnumerable<string> files, CancellationToken ct = default,
        Action<string, string>? onStandard = null)
    {
        var found = new Dictionary<string, (SqlObjectInfo Obj, List<string> Files)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var f in files.Where(f => !BinaryExt.Contains(Path.GetExtension(f))).Take(MaxMenuFilesRead))
        {
            ct.ThrowIfCancellationRequested();
            string text;
            try { if (new FileInfo(f).Length > MaxMenuFileBytes) continue; text = await File.ReadAllTextAsync(f, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
            if (onStandard is not null && await Task.Run(() => StandardTextFor(f, text), ct) is { } std)
            {
                text += "\n" + std.Text;
                onStandard(f, std.Version);
            }
            foreach (var o in NamesIn(text, null))
            {
                var key = (o.FromSysDatabase ? "sys:" : "app:") + o.QualifiedName;
                if (found.TryGetValue(key, out var hit)) { hit.Files.Add(f); continue; }
                found[key] = (o, new List<string> { f });
                order.Add(key);
            }
        }
        return order.Select(k => found[k]).ToList();
    }

    /// <summary>
    /// Bảo đảm cache danh sách object của 2 database (cái <see cref="ObjectsInFilesAsync"/> dò tên theo) có và còn đúng — nơi không có cây SQL Object
    /// (BcodeNote) gọi trước khi dò. Chữ ký database khác cache (hoặc chưa có cache) thì nạp lại như cây SQL Object (bỏ bảng phân kỳ) rồi lưu cache.
    /// Trả lỗi kết nối (nếu có) để báo; khi đó vẫn dò bằng cache cũ. <paramref name="includeSys"/> = false: chỉ App (Sys giữ cache đang có).
    /// </summary>
    public async Task<List<string>> EnsureObjectNamesAsync(bool includeSys = true)
    {
        var errors = new List<string>();
        foreach (var sys in includeSys ? new[] { false, true } : new[] { false })
        {
            try
            {
                var sig = await _browser.GetSignatureAsync(sys);
                if (_browser.LoadCache(sys)?.Signature == sig) continue;
                _browser.SaveCache(sys, sig, await _browser.ListObjectsAsync(sys, null, hidePeriodTables: true));
            }
            catch (Exception ex) { errors.Add((sys ? "Sys Data: " : "App Data: ") + ex.Message); }
        }
        return errors;
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
                // file .f mã hoá: theo chỉ mục source chuẩn (đã dựng nền; chưa xong thì bỏ qua)
                if (StdContext(out _) is { } sc)
                {
                    var std = _index.StandardCandidates(root, sc.Roots, sc.Version, sc.All, wanted.Select(w => w.Name).ToList());
                    foreach (var (name, label) in wanted)
                        if (std.TryGetValue(name, out var sh))
                            foreach (var h in sh)
                                if (!seen.Contains("file:" + h.ProjectFile)) Add(StdFileNode(h.ProjectFile, h.StdFile, h.Version, ".f mã hoá"), level, label, next);
                }
            }
            frontier = next;
        }
        if (rows.Any(r => CodeKinds.Contains(r.Node.Kind)))
        {
            var dbStamp = await DbStampAsync();
            var app = await EncryptedNamesAsync(false, dbStamp, ct);
            var sys = await EncryptedNamesAsync(true, dbStamp, ct);
            rows = rows.Select(r => r with { Node = Mark(r.Node, app, sys) }).ToList();
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

    private static readonly string[] KindOrder = { "menu", "file", "proc", "func", "view", "trigger", "table", "unknown" };
    private static List<DepNode> Sort(List<DepNode> nodes) =>
        nodes.GroupBy(n => n.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
             .OrderBy(n => Array.IndexOf(KindOrder, n.Kind)).ThenBy(n => n.Label, StringComparer.OrdinalIgnoreCase).ToList();
}
