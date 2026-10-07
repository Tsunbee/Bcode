using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>1 dòng trong gói update: copy nguyên 1 file có sẵn (SourceFilePath) hoặc ghi nội dung sinh ra (GeneratedContent).</summary>
/// <summary>
/// Cấu trúc thư mục của gói update (dùng chung Advance Note "Gen All" và tab Gen Update để 2 nơi ra cùng 1 dạng):
///   Script\app\&lt;tên&gt;.sql , Script\sys\&lt;tên&gt;.sql   — script SQL của App Data / Sys Data
///   Web\App_Data\... , Web\Main\...                       — file source của site (Dir, Grid, Filter, Report, Templates, aspx...)
/// </summary>
public static class PackageLayout
{
    public static string Script(bool sys, string fileName) => Path.Combine("Script", sys ? "sys" : "app", fileName);

    /// <summary>Đường dẫn file source (tương đối so với gốc site: App_Data\..., Main\...) → trong gói nằm dưới Web\.</summary>
    public static string Web(string relativeToSiteRoot) => Path.Combine("Web", relativeToSiteRoot);
}

public sealed class PackageItem
{
    public required string Origin { get; init; }            // nguồn: "Gen All · SVTran", "File Path", "SQL Object"...
    public required string RelativeDestPath { get; init; }  // đường dẫn tương đối trong thư mục gói update
    public string? SourceFilePath { get; init; }
    public string? GeneratedContent { get; init; }
    public bool IsScript => GeneratedContent is not null;
}

public sealed class GenAllResult
{
    public List<PackageItem> Items { get; } = new();
    public List<string> Warnings { get; } = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Thêm 1 mục; trùng đường dẫn đích thì bỏ (mục đến trước thắng).</summary>
    public bool Add(PackageItem item)
    {
        if (!_seen.Add(item.RelativeDestPath)) return false;
        Items.Add(item);
        return true;
    }
}

/// <summary>
/// Dựng danh sách mục của gói update cho 1 Request (Advance Note):
///   • Gen All &lt;controller&gt;: toàn bộ file liên quan (cùng cơ chế File Lookup — Main\*.aspx, các file cùng tên trong
///     Controllers, controller đi cặp...) + các file include/entity mà controller tham chiếu + procedure được gọi
///     trong controller Filter + script menu (wcommand, command).
///   • File Path: đường dẫn file nhập tay.
///   • SQL Object: tên procedure nhập tay (script ALTER lấy từ database App/Sys).
///   • SQL Top Script: script đầu gói.
/// </summary>
/// <summary>1 table tìm thấy trong các file liên quan tới controller (xem <see cref="GenAllService.FindTablesAsync"/>).</summary>
public sealed class RelatedTable
{
    public string Name { get; set; } = "";
    public List<string> Controllers { get; set; } = new();
    public bool InApp { get; set; }
    public bool InSys { get; set; }
    public long RowsApp { get; set; }
    public long RowsSys { get; set; }
}

public class GenAllService
{
    /// <summary>Đọc dữ liệu table để sinh script dữ liệu (gán sau khi các service được tạo — xem MainForm).</summary>
    public TableDataService? TableData { get; set; }
    public DataScriptService? DataScript { get; set; }

    /// <summary>Quá số dòng này mà không có Where thì không sinh script dữ liệu (file script sẽ quá lớn) — chỉ cảnh báo.</summary>
    public const int MaxDataRows = 50000;

    private readonly FileLookupService _files;
    private readonly SqlObjectBrowserService _sql;
    private readonly WCommandService _wcommand;

    public GenAllService(FileLookupService files, SqlObjectBrowserService sql, WCommandService wcommand)
    {
        _files = files;
        _sql = sql;
        _wcommand = wcommand;
    }

    private static readonly Regex ExecRegex = new(
        @"\bexec(?:ute)?\s+(?:\[?[A-Za-z_]\w*\]?\.)?\[?([A-Za-z_][\w$#]*)\]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<GenAllResult> ResolveAsync(Workspace ws, AdvanceRequest req)
    {
        var result = new GenAllResult();

        foreach (var name in SplitNames(req.GenAll))
            await ResolveControllerAsync(ws, name, result);

        foreach (var path in SplitLines(req.FilePaths))
            AddFilePath(ws, path, result);

        foreach (var proc in SplitNames(req.Procedures))
        {
            var found = await AddSqlObjectAsync(proc, req.UseApp, req.UseSys, "SQL Object", result);
            if (!found) result.Warnings.Add($"SQL Object \"{proc}\": không tìm thấy trong {DbLabel(req.UseApp, req.UseSys)}.");
        }

        foreach (var t in req.Tables.Where(t => t.Structure || t.Data))
            await AddTableAsync(t, result);

        if (!string.IsNullOrWhiteSpace(req.TopScript))
        {
            var any = false;
            foreach (var useSys in new[] { false, true })
            {
                if (useSys ? !req.UseSys : !req.UseApp) continue;
                result.Add(new PackageItem
                {
                    Origin = "SQL Top Script",
                    RelativeDestPath = PackageLayout.Script(useSys, "00_top.sql"),
                    GeneratedContent = req.TopScript,
                });
                any = true;
            }
            if (!any) result.Warnings.Add("SQL Top Script có nội dung nhưng chưa chọn database App/Sys nào nên không được đưa vào gói.");
        }

        return result;
    }

    // ---- Table liên quan ----------------------------------------------------------------------

    private static readonly Regex TableAttrRegex = new(
        @"<(?:dir|grid|lookup|report|filter)\b[^>]*?\btable\s*=\s*""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TableNameRegex = new(@"^[A-Za-z_][\w$#]*$", RegexOptions.Compiled);

    /// <summary>Các table khai báo (table="...") trong những file liên quan tới các controller này — cùng tập file mà Gen All lấy (Dir, Grid, Filter,
    /// Lookup, Report, Upload...). Kết quả đọc file được nhớ trong FileParseCache nên lần sau gần như tức thì; rồi hỏi database xem table có
    /// ở App hay Sys và bao nhiêu dòng.</summary>
    public async Task<List<RelatedTable>> FindTablesAsync(Workspace ws, IEnumerable<string> controllers)
    {
        var found = new Dictionary<string, RelatedTable>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(ws.SourcePath)) return new List<RelatedTable>();

        foreach (var name in controllers)
        {
            var menus = new List<WCommandItem>();
            try { menus = await _wcommand.FindByControllerAsync(name); } catch { /* không đọc được wcommand — vẫn dò theo tên */ }
            var links = menus.Select(m => (m.Link ?? "").Split('?')[0].Trim()).Where(l => l.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (links.Count == 0) links.Add("");

            var tables = await Task.Run(() =>
            {
                var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var link in links)
                    Collect(_files.BuildTreeForMenuItem(ws.SourcePath, link, name, onlyFInGridFilterDir: false), files);
                var cache = FileParseCache.For(ws.SourcePath);
                var session = new ReadSession(cache);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Parallel.ForEach(files.Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".f", StringComparison.OrdinalIgnoreCase)),
                    new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
                    {
                        foreach (var t in cache.GetOrCompute("tables", f, session, rd => ExtractTableNames(rd(f))))
                            lock (names) names.Add(t);
                    });
                cache.SaveInBackground();
                return names;
            });

            foreach (var t in tables)
            {
                if (!found.TryGetValue(t, out var info)) found[t] = info = new RelatedTable { Name = t };
                if (!info.Controllers.Contains(name, StringComparer.OrdinalIgnoreCase)) info.Controllers.Add(name);
            }
        }

        var all = found.Keys.ToList();
        foreach (var sys in new[] { false, true })
        {
            try
            {
                var rows = await _sql.GetTableRowCountsAsync(sys, all);
                foreach (var (n, c) in rows)
                {
                    var info = found[n];
                    if (sys) { info.InSys = true; info.RowsSys = c; } else { info.InApp = true; info.RowsApp = c; }
                }
            }
            catch { /* database không kết nối được — vẫn trả danh sách, chỉ không biết table ở đâu */ }
        }
        return found.Values.OrderByDescending(t => t.InApp || t.InSys).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> ExtractTableNames(string? text)
    {
        if (text is null) return Array.Empty<string>();
        return TableAttrRegex.Matches(text).Select(m => m.Groups[1].Value.Trim())
            .Where(n => TableNameRegex.IsMatch(n) && !n.Contains("partition", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Script của 1 table được chọn: Structure → CREATE TABLE (+ index/trigger) ở 10_table_*.sql; Data → DELETE rồi nạp lại dữ liệu ở 20_data_*.sql.</summary>
    private async Task AddTableAsync(TableSelection t, GenAllResult result)
    {
        var origin = $"Table · {t.Name}";
        var db = t.Sys ? "Sys" : "App";
        var safe = string.Concat(t.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        try
        {
            var obj = (await _sql.ListObjectsAsync(t.Sys, t.Name))
                .FirstOrDefault(o => o.Kind == SqlObjectKind.Table && string.Equals(o.Name, t.Name, StringComparison.OrdinalIgnoreCase));
            if (obj is null) { result.Warnings.Add($"{origin}: không tìm thấy trong database {db}."); return; }

            if (t.Structure)
            {
                var script = await _sql.GetDefinitionAsync(obj);
                result.Add(new PackageItem
                {
                    Origin = origin + " · cấu trúc",
                    RelativeDestPath = PackageLayout.Script(t.Sys, $"10_table_{safe}.sql"),
                    GeneratedContent = "-- Tạo table " + obj.QualifiedName + " (chỉ chạy khi table chưa tồn tại)\r\n" + script,
                });
            }

            if (t.Data)
            {
                if (TableData is null || DataScript is null) { result.Warnings.Add($"{origin}: chưa có dịch vụ đọc dữ liệu."); return; }
                if (!obj.Schema.Equals("dbo", StringComparison.OrdinalIgnoreCase)) { result.Warnings.Add($"{origin}: table thuộc schema {obj.Schema} — chưa hỗ trợ sinh dữ liệu."); return; }
                var where = (t.Where ?? "").Trim();
                if (where.Length == 0)
                {
                    var counts = await _sql.GetTableRowCountsAsync(t.Sys, new[] { t.Name });
                    if (counts.TryGetValue(t.Name, out var rows) && rows > MaxDataRows)
                    {
                        result.Warnings.Add($"{origin}: {rows:N0} dòng (quá {MaxDataRows:N0}) — nhập Where để lọc bớt rồi mới sinh dữ liệu.");
                        return;
                    }
                }
                var data = await TableData.LoadTableAsync(t.Sys, obj.Schema, obj.Name, 0, "*", where.Length == 0 ? null : where, null);
                if (data.Rows.Count == 0) { result.Warnings.Add($"{origin}: không có dòng dữ liệu nào" + (where.Length > 0 ? " khớp Where." : ".")); return; }
                var script = DataScript.GenerateDeleteAndReloadScript(data, obj.Name, where.Length == 0 ? null : where);
                result.Add(new PackageItem
                {
                    Origin = origin + $" · dữ liệu ({data.Rows.Count:N0} dòng)",
                    RelativeDestPath = PackageLayout.Script(t.Sys, $"20_data_{safe}.sql"),
                    GeneratedContent = script,
                });
            }
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"{origin} ({db}): {ex.Message}");
        }
    }

    // ---- Gen All -----------------------------------------------------------------------------

    private async Task ResolveControllerAsync(Workspace ws, string name, GenAllResult result)
    {
        var origin = $"Gen All · {name}";
        if (string.IsNullOrWhiteSpace(ws.SourcePath))
        {
            result.Warnings.Add($"{origin}: workspace chưa khai báo Source Path.");
            return;
        }

        // Các dòng menu (wcommand) trỏ tới controller này: cho ra Link (Main\*.aspx) và là nguồn sinh script menu.
        var menus = new List<WCommandItem>();
        try { menus = await _wcommand.FindByControllerAsync(name); }
        catch (Exception ex) { result.Warnings.Add($"{origin}: không đọc được wcommand ({ex.Message})."); }

        var links = menus.Select(m => (m.Link ?? "").Split('?')[0].Trim())
            .Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (links.Count == 0) links.Add("");

        var allFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // gồm cả .xml nguồn — để đọc entity/procedure
        var packFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // theo quy ước Gen Update: Grid/Filter/Dir chỉ lấy .f
        var includeFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var filterProcs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);   // procedure được gọi trong controller Filter
        var tokens = OwnTokens(name);
        // Mọi thứ phải đọc từ file (include của từng xml, procedure trong Filter) đi qua FileParseCache: lưu ra đĩa, kiểm lại bằng
        // giờ sửa/kích thước — file không đổi thì lần Gen All sau (kể cả mở lại app) chỉ tốn 1 lần stat thay vì đọc qua UNC.
        var cache = FileParseCache.For(ws.SourcePath);
        var session = new ReadSession(cache);

        await Task.Run(() =>
        {
            // Dựng cây 1 lần cho mỗi Link (đọc UNC + kiểm entity rất tốn thời gian), rồi tự lọc tập "gói" như Gen Update.
            foreach (var link in links)
                Collect(_files.BuildTreeForMenuItem(ws.SourcePath, link, name, onlyFInGridFilterDir: false), allFiles);

            // Trang Main\*.aspx khai báo Controller="<tên>" nhưng không có dòng wcommand nào trỏ tới.
            foreach (var aspx in FindMainPagesForController(ws.SourcePath, name))
                allFiles.Add(aspx);

            foreach (var f in allFiles)
                if (!RequiresF(ws.SourcePath, f) || f.EndsWith(".f", StringComparison.OrdinalIgnoreCase))
                    packFiles.Add(f);

            foreach (var xml in allFiles.Where(f => f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                List<string> includes;
                try { includes = FileLookupService.GetReferencedIncludesCached(session, xml); }
                catch { continue; }
                // Include dùng chung của hệ thống (Include\Javascript, Include\Command, Include\XML\Flow*...) đã có sẵn trên site
                // chuẩn — chỉ lấy include riêng của controller/module (tên chứa tên controller hay tiền tố module, hoặc Config\Fields).
                foreach (var inc in includes) if (File.Exists(inc) && IsOwnInclude(inc, tokens)) includeFiles.Add(inc);

                if (IsInFolder(ws.SourcePath, xml, "Filter"))
                    foreach (var p in cache.GetOrCompute("execprocs", xml, session, rd => ExtractFilterProcs(xml, rd)))
                        filterProcs.Add(p);
            }

            // Extender.ZVCTran, Revert.ZVCTran.ent, ZVCReference.ent...: file Include mang tên controller/module nhưng không
            // xml nào khai entity trực tiếp (hoặc tên controller đứng sau dấu chấm) — quét theo tên.
            foreach (var inc in _files.FindIncludeFilesByName(ws.SourcePath, tokens)) includeFiles.Add(inc);

            // Extender.ent...: file .ent đăng ký dùng chung có dòng nhắc tới tên controller (Conditional.Extender.List.ZVCTran).
            foreach (var inc in _files.FindIncludeEntFilesMentioning(ws.SourcePath, name)) includeFiles.Add(inc);
            cache.SaveInBackground();
        });

        if (packFiles.Count == 0 && allFiles.Count == 0 && menus.Count == 0)
            result.Warnings.Add($"{origin}: không tìm thấy file hay dòng menu nào cho controller này — kiểm tra lại tên.");

        foreach (var file in packFiles.Concat(includeFiles))
            AddFile(ws, file, origin, result);

        // Procedure được gọi trong controller Filter: gen luôn (chỉ lấy cái có thật trong App/Sys; bỏ qua procedure hệ thống).
        foreach (var proc in filterProcs)
            await AddSqlObjectAsync(proc, useApp: true, useSys: true, $"{origin} · Filter", result);

        // sysmenu: script DELETE/INSERT cho wcommand + command.
        if (menus.Count > 0)
        {
            var script = string.Join(Environment.NewLine, menus.Select(WCommandService.GenerateScript));
            result.Add(new PackageItem
            {
                Origin = origin + " · sysmenu",
                RelativeDestPath = PackageLayout.Script(sys: true, $"sysmenu_{name}.sql"),
                GeneratedContent = script,
            });
        }
        else
        {
            result.Warnings.Add($"{origin}: không có dòng wcommand nào (sysid/link) nên không sinh script sysmenu.");
        }
    }

    /// <summary>Cùng luật onlyFInGridFilterDir của FileLookupService: file nằm trong thư mục Grid/Filter/Dir của Controllers
    /// thì bản .xml nguồn không đưa vào gói (đã có .f biên dịch).</summary>
    private static bool RequiresF(string sourceRoot, string file)
    {
        var controllers = Path.Combine(sourceRoot, "App_Data", "Controllers");
        string rel;
        try { rel = Path.GetRelativePath(controllers, Path.GetDirectoryName(file) ?? controllers); }
        catch { return false; }
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return false;
        return rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(seg => seg.Equals("Grid", StringComparison.OrdinalIgnoreCase)
                     || seg.Equals("Filter", StringComparison.OrdinalIgnoreCase)
                     || seg.Equals("Dir", StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex ModulePrefixRegex = new(@"^[A-Z]+(?=[A-Z][a-z]|[^A-Za-z]|$)", RegexOptions.Compiled);

    /// <summary>Từ khoá nhận ra file Include "của" controller: chính tên controller và tiền tố module viết hoa
    /// ("ZVCTran" → "ZVC"; chỉ dùng khi ≥ 3 ký tự để khỏi khớp lan man).</summary>
    private static List<string> OwnTokens(string controller)
    {
        var tokens = new List<string> { controller };
        var m = ModulePrefixRegex.Match(controller);
        if (m.Success && m.Length >= 3 && m.Length < controller.Length) tokens.Add(m.Value);
        return tokens;
    }

    private static bool IsOwnInclude(string file, IReadOnlyCollection<string> tokens)
    {
        var fileName = Path.GetFileName(file);
        if (tokens.Any(t => fileName.Contains(t, StringComparison.OrdinalIgnoreCase))) return true;
        return (Path.GetDirectoryName(file) ?? "")
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals("Config", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSystemProcedure(string name) =>
        name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("FastBusiness$", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextInclude(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".txt" or ".ent" or ".xml" or ".dct" or ".sql";
    }

    /// <summary>Tên procedure được EXEC trong 1 file Filter (và các include dạng text của nó), bỏ procedure hệ thống. Đọc qua <paramref name="read"/> để cache theo dõi phụ thuộc.</summary>
    private static IEnumerable<string> ExtractFilterProcs(string xml, Func<string, string?> read)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Scan(string? text)
        {
            if (text is null) return;
            foreach (Match m in ExecRegex.Matches(text))
            {
                var p = m.Groups[1].Value;
                if (!IsSystemProcedure(p)) names.Add(p);
            }
        }
        Scan(read(xml));
        foreach (var inc in FileLookupService.ResolveIncludesWith(xml, read).Where(IsTextInclude)) Scan(read(inc));
        return names;
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch { return ""; }
    }

    private static bool IsInFolder(string sourceRoot, string file, string folderName)
    {
        try
        {
            var rel = Path.GetRelativePath(Path.Combine(sourceRoot, "App_Data", "Controllers", folderName), file);
            return !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
        }
        catch { return false; }
    }

    private static void Collect(FileLookupNode node, HashSet<string> into)
    {
        if (!node.IsDirectory) { into.Add(node.FullPath); return; }
        foreach (var child in node.Children) Collect(child, into);
    }

    // ---- Trang Main\*.aspx theo Controller="..." ----------------------------------------------------

    private static readonly Regex ControllerAttr = new(@"\bController\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly object MainLock = new();
    private static string? _mainCacheRoot;
    private static DateTime _mainCacheTime;
    private static Dictionary<string, List<string>> _mainCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Các Main\*.aspx có Controller="&lt;tên&gt;" (trang dựng ra controller đó). Quét MỘT lần cả thư mục Main
    /// (song song) rồi nhớ kết quả 10 phút — đọc từng aspx qua UNC rất chậm nên không quét lại cho mỗi controller.</summary>
    private static List<string> FindMainPagesForController(string sourceRoot, string controller)
    {
        lock (MainLock)
        {
            if (_mainCacheRoot != sourceRoot || DateTime.Now - _mainCacheTime > TimeSpan.FromMinutes(10))
            {
                var map = new ConcurrentDictionary<string, ConcurrentBag<string>>(StringComparer.OrdinalIgnoreCase);
                var main = Path.Combine(sourceRoot, "Main");
                try
                {
                    if (Directory.Exists(main))
                    {
                        // Đọc 1600+ file aspx qua UNC mất cả phút (tuần tự) — nên nhớ "controller nào" của từng file trong FileParseCache
                        // (lưu ra đĩa, kiểm bằng giờ sửa/kích thước): từ lần sau chỉ stat file, và chỉ đọc lại file mới/đổi.
                        var cache = FileParseCache.For(sourceRoot);
                        var session = new ReadSession(cache);
                        Parallel.ForEach(Directory.EnumerateFiles(main, "*.aspx", SearchOption.TopDirectoryOnly),
                            new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
                            {
                                var controllers = cache.GetOrCompute("aspxctl", f, session, rd =>
                                    ControllerAttr.Matches(rd(f) ?? "").Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase));
                                foreach (var c in controllers)
                                    map.GetOrAdd(c, _ => new ConcurrentBag<string>()).Add(f);
                            });
                        cache.SaveInBackground();
                    }
                }
                catch { /* Main không đọc được — bỏ qua, Gen All vẫn dùng Link từ wcommand */ }
                _mainCache = map.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
                _mainCacheRoot = sourceRoot;
                _mainCacheTime = DateTime.Now;
            }
            return _mainCache.TryGetValue(controller, out var list) ? list.ToList() : new List<string>();
        }
    }

    // ---- File / SQL Object -----------------------------------------------------------------------

    private static void AddFile(Workspace ws, string file, string origin, GenAllResult result)
    {
        // File nằm trong site (App_Data\..., Main\...) → Web\<tương đối>; file ngoài site thì để riêng ở "other\" cho khỏi lẫn vào Web.
        var relative = !string.IsNullOrWhiteSpace(ws.SourcePath) && IsUnder(ws.SourcePath, file)
            ? PackageLayout.Web(Path.GetRelativePath(ws.SourcePath, file))
            : Path.Combine("other", Path.GetFileName(file));
        result.Add(new PackageItem { Origin = origin, RelativeDestPath = relative, SourceFilePath = file });
    }

    private static bool IsUnder(string root, string path)
    {
        try
        {
            var rel = Path.GetRelativePath(root, path);
            return !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
        }
        catch { return false; }
    }

    private static void AddFilePath(Workspace ws, string path, GenAllResult result)
    {
        path = path.Trim().Trim('"');
        if (path.Length == 0) return;
        if (File.Exists(path)) { AddFile(ws, path, "File Path", result); return; }
        if (Directory.Exists(path))
        {
            var count = 0;
            foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (++count > 500) { result.Warnings.Add($"File Path \"{path}\": thư mục có quá 500 file, chỉ lấy 500 file đầu."); break; }
                AddFile(ws, f, "File Path", result);
            }
            return;
        }
        result.Warnings.Add($"File Path \"{path}\": không tồn tại.");
    }

    private static string DbLabel(bool app, bool sys) =>
        app && sys ? "App Data và Sys Data" : sys ? "Sys Data" : app ? "App Data" : "(chưa chọn database nào)";

    private async Task<bool> AddSqlObjectAsync(string name, bool useApp, bool useSys, string origin, GenAllResult result)
    {
        var found = false;
        foreach (var sys in new[] { false, true })
        {
            if (sys ? !useSys : !useApp) continue;
            try
            {
                var objects = await _sql.ListObjectsAsync(sys, name);
                var obj = objects.FirstOrDefault(o => o.Kind != SqlObjectKind.Table
                    && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
                if (obj is null) continue;
                var script = await _sql.GetDefinitionAsync(obj);
                result.Add(new PackageItem
                {
                    Origin = origin,
                    RelativeDestPath = PackageLayout.Script(sys, obj.Name + ".sql"),
                    GeneratedContent = script,
                });
                found = true;
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"{origin} \"{name}\" ({(sys ? "Sys" : "App")}): {ex.Message}");
            }
        }
        return found;
    }

    // ---- Tách chuỗi nhập ---------------------------------------------------------------------------

    /// <summary>Tên (controller/procedure): tách theo dòng, dấu phẩy, chấm phẩy hoặc khoảng trắng.</summary>
    public static List<string> SplitNames(string? text) =>
        Regex.Split(text ?? "", @"[\s,;]+").Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Đường dẫn: chỉ tách theo dòng (đường dẫn có thể chứa khoảng trắng, dấu phẩy).</summary>
    public static List<string> SplitLines(string? text) =>
        Regex.Split(text ?? "", @"\r?\n").Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    // ---- Tạo gói update -------------------------------------------------------------------------------

    /// <summary>Ghi các mục ra thư mục gói (cùng cách Gen Update hiện có: copy file / ghi script). Trả về số mục đã ghi.</summary>
    public static int CreatePackage(string destRoot, IEnumerable<PackageItem> items, string? description)
    {
        Directory.CreateDirectory(destRoot);
        var count = 0;
        foreach (var item in items)
        {
            var dest = Path.Combine(destRoot, item.RelativeDestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (item.GeneratedContent is not null) File.WriteAllText(dest, item.GeneratedContent);
            else File.Copy(item.SourceFilePath!, dest, overwrite: true);
            count++;
        }
        if (!string.IsNullOrWhiteSpace(description))
            File.WriteAllText(Path.Combine(destRoot, "description.txt"), description);
        return count;
    }
}
