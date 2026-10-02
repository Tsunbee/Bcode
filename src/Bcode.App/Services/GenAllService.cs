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
public class GenAllService
{
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
        var filterTexts = new List<string>();

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
                try { includes = FileLookupService.GetReferencedIncludes(xml); }
                catch { continue; }
                foreach (var inc in includes) if (File.Exists(inc)) includeFiles.Add(inc);

                if (IsInFolder(ws.SourcePath, xml, "Filter"))
                {
                    filterTexts.Add(SafeRead(xml));
                    foreach (var inc in includes.Where(IsTextInclude)) filterTexts.Add(SafeRead(inc));
                }
            }
        });

        if (packFiles.Count == 0 && allFiles.Count == 0 && menus.Count == 0)
            result.Warnings.Add($"{origin}: không tìm thấy file hay dòng menu nào cho controller này — kiểm tra lại tên.");

        foreach (var file in packFiles.Concat(includeFiles))
            AddFile(ws, file, origin, result);

        // Procedure được gọi trong controller Filter: gen luôn (chỉ lấy cái có thật trong App/Sys; bỏ qua procedure hệ thống).
        var procs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in filterTexts)
            foreach (Match m in ExecRegex.Matches(text))
            {
                var p = m.Groups[1].Value;
                if (IsSystemProcedure(p)) continue;
                procs.Add(p);
            }
        foreach (var proc in procs)
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

    private static bool IsSystemProcedure(string name) =>
        name.StartsWith("sp_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("xp_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("FastBusiness$", StringComparison.OrdinalIgnoreCase);

    private static bool IsTextInclude(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".txt" or ".ent" or ".xml" or ".dct" or ".sql";
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
                        Parallel.ForEach(Directory.EnumerateFiles(main, "*.aspx", SearchOption.TopDirectoryOnly),
                            new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
                            {
                                foreach (Match m in ControllerAttr.Matches(SafeRead(f)))
                                    map.GetOrAdd(m.Groups[1].Value, _ => new ConcurrentBag<string>()).Add(f);
                            });
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
