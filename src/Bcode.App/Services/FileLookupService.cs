
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

public class FileLookupNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public List<FileLookupNode> Children { get; } = new();
}

/// <summary>
/// Browses the FastBusiness web source tree over a UNC path (App_Data and
/// whatever it contains — the exact subfolder layout varies by site, e.g.
/// App_Data/Structure/{App,Dir,Ext,Filter,Grid,Lookup,Sys} on one real KOG
/// project, so this does not hardcode a specific convention), with an
/// "Only show *.ext" extension filter and free-text search box.
/// </summary>
public class FileLookupService
{
    public FileLookupNode BuildTree(string sourceRootPath, string extensionFilter = ".f", string? searchText = null, bool onlyShowFiltered = true)
    {
        var root = new FileLookupNode { Name = Path.GetFileName(sourceRootPath.TrimEnd('\\', '/')), FullPath = sourceRootPath, IsDirectory = true };
        if (!Directory.Exists(sourceRootPath)) return root;

        // A folder should be hidden once it has no matching descendant — that must happen
        // whenever an extension filter OR a search term is active, not just the extension
        // filter alone (otherwise a search still shows every folder in the whole tree).
        var pruneEmptyFolders = onlyShowFiltered || !string.IsNullOrWhiteSpace(searchText);
        PopulateRecursive(root, extensionFilter, searchText, onlyShowFiltered, pruneEmptyFolders);
        return root;
    }

    /// <summary>
    /// Builds the File Lookup tree for a clicked wcommand menu item, using how the menu
    /// itself is wired to source: <paramref name="link"/> is the exact web page file under
    /// the site's "Main" folder (sibling of App_Data), and <paramref name="sysId"/> is the
    /// base file name (no extension) that this menu's source files carry throughout
    /// App_Data\Controllers\{Dir,Filter,Grid,Lookup,Report,Templates,...} — e.g. sysid
    /// "SVTran" matches Controllers\Dir\SVTran.f, Controllers\Report\SVTran.xml, etc.
    /// Unlike <see cref="BuildTree"/>, this targets those two known locations directly
    /// instead of a free-text search over the whole source tree.
    /// </summary>
    /// <param name="onlyFInGridFilterDir">When true, the Controllers\Grid, \Filter and \Dir
    /// subfolders (and anything nested under them) only keep .f files — those are already
    /// the compiled/encrypted deployable version, so the matching .xml source has nothing
    /// to add to an update package. File Lookup (plain double-click) leaves this false and
    /// still shows every matching extension; only Gen Update opts in.</param>
    /// <param name="onlyF">File Lookup's own "Only Show *.f" checkbox (distinct from
    /// <paramref name="onlyFInGridFilterDir"/>'s Gen-Update-specific, folder-scoped
    /// restriction) — when true, restricts the ENTIRE menu tree to .f files, matching real
    /// FCodeViewer's "Only Show *.f" (only .f of that menu) vs "Show *.f" (all extensions of
    /// that menu, not the whole program) checkboxes.</param>
    public FileLookupNode BuildTreeForMenuItem(string sourceRootPath, string link, string sysId, bool onlyFInGridFilterDir = false, bool onlyF = false)
    {
        var root = new FileLookupNode { Name = Path.GetFileName(sourceRootPath.TrimEnd('\\', '/')), FullPath = sourceRootPath, IsDirectory = true };

        string? mainPath = null;
        if (!string.IsNullOrWhiteSpace(link))
        {
            var mainFolder = Path.Combine(sourceRootPath, "Main");
            var candidate = Path.Combine(mainFolder, link);
            if (File.Exists(candidate))
            {
                mainPath = candidate;
                var mainNode = new FileLookupNode { Name = "Main", FullPath = mainFolder, IsDirectory = true };
                mainNode.Children.Add(new FileLookupNode { Name = Path.GetFileName(mainPath), FullPath = mainPath, IsDirectory = false });
                root.Children.Add(mainNode);
            }
        }

        if (!string.IsNullOrWhiteSpace(sysId))
        {
            var controllersDir = Path.Combine(sourceRootPath, "App_Data", "Controllers");
            if (Directory.Exists(controllersDir))
            {
                var sysIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sysId };

                // Các file "anh em" cùng module (vd SRTran, SRDetail, SRBillFilter,
                // SRIssueFilter, SRPhysicalMultiForm...) không hề tham chiếu tới nhau, nhưng
                // đều dùng chung 2 ký tự đầu làm mã module theo quy ước đặt tên FastBusiness
                // ("SR", "GL", "IR", "SV"...). Bee đề xuất gộp theo TÊN FILE giống nhau (kiểu
                // SQL LIKE '%SR%') thay vì đọc nội dung từng file để tìm quan hệ cha/con — rẻ
                // hơn nhiều vì chỉ liệt kê tên file, không cần mở đọc file nào cả. Gộp thẳng
                // mọi file cùng mã module vào sysIds trước khi dò tham chiếu xuôi bên dưới, để
                // Controllers\Filter\SRBillFilter.f (không ai tham chiếu tới) vẫn lọt vào cây.
                var modulePrefix = GetModulePrefix(sysId);
                if (modulePrefix is not null)
                {
                    List<string> allControllerFiles;
                    try
                    {
                        allControllerFiles = Directory.EnumerateFiles(controllersDir, "*", SearchOption.AllDirectories).ToList();
                    }
                    catch (Exception)
                    {
                        allControllerFiles = new List<string>(); // UNC path lỗi/không truy cập được — bỏ qua bước gộp theo tên
                    }

                    foreach (var file in allControllerFiles)
                    {
                        var name = Path.GetFileNameWithoutExtension(file);
                        if (name.StartsWith(modulePrefix, StringComparison.OrdinalIgnoreCase))
                            sysIds.Add(name);
                    }
                }

                // Chases the chain of explicitly-declared related controllers as far as it
                // goes — e.g. Dir\SVTran.xml's <items style="Grid" controller="SVDetail">
                // pulls in SVDetail, and SVDetail's OWN file in turn has
                // g.showForm('zSVSI2Filter') and a GridController entity naming
                // "zSVSI2MultiGrid", so those need a second pass over SVDetail's newly
                // found file, not just the original SVTran files. Keeps expanding until a
                // pass finds nothing new (capped so a reference cycle can't loop forever).
                var scannedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var pass = 0; pass < 5; pass++)
                {
                    var filesToScan = CollectMatchingFiles(controllersDir, sysIds);
                    if (mainPath is { } main) filesToScan.Add(main);

                    var newFiles = filesToScan.Where(f => scannedFiles.Add(f)).ToList();
                    if (newFiles.Count == 0) break; // nothing left unscanned

                    var addedAny = false;
                    foreach (var file in newFiles)
                        foreach (var related in ExtractRelatedControllerNames(file))
                            if (sysIds.Add(related))
                                addedAny = true;

                    if (!addedAny) break; // fixed point — no new controller names discovered
                }

                var controllersNode = new FileLookupNode { Name = "Controllers", FullPath = controllersDir, IsDirectory = true };
                PopulateBySysId(controllersNode, sysIds, onlyFInGridFilterDir, restrictToF: onlyF);
                if (controllersNode.Children.Count > 0)
                    root.Children.Add(controllersNode);
            }
        }

        return root;
    }

    /// <summary>Rút "mã module" 2 ký tự đầu của <paramref name="sysId"/> nếu cả hai đều là chữ
    /// cái (vd "SRTran" → "SR", "GLTranReport" → "GL", "SVDetail" → "SV") — quy ước đặt tên phổ
    /// biến trong FastBusiness cho các controller cùng nhóm nghiệp vụ. Trả về null khi sysId
    /// ngắn hơn 2 ký tự hoặc 2 ký tự đầu không phải chữ cái, để BuildTreeForMenuItem bỏ qua
    /// bước gộp theo tên một cách an toàn thay vì đoán bừa trên một sysId không theo quy ước
    /// này (ví dụ một mã số thuần).</summary>
    private static string? GetModulePrefix(string sysId)
    {
        if (sysId.Length < 2) return null;
        if (!char.IsLetter(sysId[0]) || !char.IsLetter(sysId[1])) return null;
        return sysId[..2];
    }

    /// <summary>
    /// FCodeViewer's own "Search Box" (File Type / Search in / String search / Match Case /
    /// Show Pattern) — a real content search, not a filename search: every file under
    /// <paramref name="searchInPath"/> matching <paramref name="fileTypePattern"/> (a plain
    /// .NET file-search pattern, e.g. "*.f" or "*.*") is read and checked for
    /// <paramref name="searchText"/>. <paramref name="useWildcardPattern"/> ("Show Pattern")
    /// lets that text use <c>*</c>/<c>?</c> wildcards instead of a plain substring match.
    /// </summary>
    public FileLookupNode SearchFileContents(string searchInPath, string fileTypePattern, string searchText, bool matchCase, bool useWildcardPattern)
    {
        var root = new FileLookupNode { Name = "Search results", FullPath = searchInPath, IsDirectory = true };
        if (!Directory.Exists(searchInPath) || string.IsNullOrEmpty(searchText)) return root;

        var pattern = string.IsNullOrWhiteSpace(fileTypePattern) ? "*.*" : fileTypePattern.Trim();
        Regex? patternRegex = null;
        if (useWildcardPattern)
        {
            var regexSource = Regex.Escape(searchText).Replace("\\*", ".*").Replace("\\?", ".");
            patternRegex = new Regex(regexSource, matchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
        }
        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(searchInPath, pattern, SearchOption.AllDirectories); }
        catch { return root; } // bad pattern or an inaccessible/down UNC path — show 0 results rather than throw

        foreach (var file in files)
        {
            string content;
            try { content = File.ReadAllText(file); }
            catch { continue; } // locked/binary/unreadable — skip rather than abort the whole search

            var isMatch = patternRegex?.IsMatch(content) ?? content.Contains(searchText, comparison);
            if (isMatch)
                root.Children.Add(new FileLookupNode { Name = Path.GetFileName(file), FullPath = file, IsDirectory = false });
        }
        return root;
    }

    // Only scanned for related-controller references — binary/generated formats
    // (.xlsx, .xsd, .ent handled separately as pure Include plumbing) aren't worth reading.
    private static readonly HashSet<string> ScannableExtensions = new(StringComparer.OrdinalIgnoreCase) { ".xml", ".f", ".txt" };

    // <field name="d81" ...><items style="Grid" controller="SVDetail" .../></field> —
    // a field that embeds a sub-grid names its own controller this way. Deliberately
    // narrower than matching every controller="..." attribute: the far more common
    // <items style="AutoComplete" controller="Item"/> (per-field lookup/autocomplete,
    // dozens per file) is NOT a "this page also uses" reference and must NOT be pulled
    // in — only style="Grid" (an embedded sub-grid) counts.
    private static readonly Regex GridItemsTagRegex = new(@"<items\b[^>]*>", RegexOptions.Compiled);
    private static readonly Regex StyleGridRegex = new(@"\bstyle\s*=\s*""Grid""", RegexOptions.Compiled);
    private static readonly Regex ControllerAttrRegex = new(@"\bcontroller\s*=\s*""([A-Za-z0-9_]+)""", RegexOptions.Compiled);

    private static readonly Regex ShowFormRegex = new(@"showForm\s*\(\s*['""]([A-Za-z0-9_]+)['""]", RegexOptions.Compiled);
    // Plain (non-SYSTEM, non-parameter) entity declarations: <!ENTITY Name "value">. The
    // one name this code specifically looks for is "GridController" — the convention
    // show$FlowMulti$Form(...) uses to name the grid form it opens, e.g.
    // <!ENTITY GridController "zSVSI2MultiGrid">.
    private static readonly Regex PlainEntityRegex = new(@"<!ENTITY\s+([A-Za-z0-9_]+)\s+""([^""]*)""", RegexOptions.Compiled);

    private static IEnumerable<string> ExtractRelatedControllerNames(string filePath)
    {
        if (!ScannableExtensions.Contains(Path.GetExtension(filePath))) yield break;

        string content;
        try
        {
            content = File.ReadAllText(filePath);
        }
        catch (Exception)
        {
            yield break; // unreadable/locked — just skip discovering related controllers from it
        }

        foreach (Match tag in GridItemsTagRegex.Matches(content))
        {
            if (!StyleGridRegex.IsMatch(tag.Value)) continue;
            var controllerMatch = ControllerAttrRegex.Match(tag.Value);
            if (controllerMatch.Success)
                yield return controllerMatch.Groups[1].Value;
        }

        foreach (Match m in ShowFormRegex.Matches(content))
            yield return m.Groups[1].Value;

        foreach (Match m in PlainEntityRegex.Matches(content))
            if (m.Groups[1].Value.Equals("GridController", StringComparison.OrdinalIgnoreCase))
                yield return m.Groups[2].Value;
    }

    private static List<string> CollectMatchingFiles(string dir, HashSet<string> sysIds)
    {
        var result = new List<string>();
        List<string> dirs, files;
        try
        {
            dirs = Directory.GetDirectories(dir).ToList();
            files = Directory.GetFiles(dir).ToList();
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var d in dirs)
            result.AddRange(CollectMatchingFiles(d, sysIds));
        foreach (var f in files)
            if (sysIds.Contains(Path.GetFileNameWithoutExtension(f)))
                result.Add(f);
        return result;
    }

    // Folder names under Controllers whose files are already the compiled/encrypted "*.f"
    // deployable — matched only when the caller opts in via onlyFInGridFilterDir (Gen Update).
    private static readonly HashSet<string> FOnlyFolderNames = new(StringComparer.OrdinalIgnoreCase) { "Grid", "Filter", "Dir" };

    /// <summary>Recurses through Controllers' subfolders, keeping only files whose base
    /// name (without extension) matches one of <paramref name="sysIds"/>, and pruning any
    /// subfolder left with no matching descendant. Once recursion enters a Grid/Filter/Dir
    /// folder with <paramref name="onlyFInGridFilterDir"/> set, <paramref name="restrictToF"/>
    /// turns on for it and everything nested below it, keeping only ".f" files there.</summary>
    private static void PopulateBySysId(FileLookupNode node, HashSet<string> sysIds, bool onlyFInGridFilterDir, bool restrictToF)
    {
        List<string> dirs, files;
        try
        {
            dirs = Directory.GetDirectories(node.FullPath).OrderBy(d => d).ToList();
            files = Directory.GetFiles(node.FullPath).OrderBy(f => f).ToList();
        }
        catch (Exception)
        {
            return; // permission or path issue — leave node empty rather than crash the tree build
        }

        foreach (var dir in dirs)
        {
            var childRestrict = restrictToF || (onlyFInGridFilterDir && FOnlyFolderNames.Contains(Path.GetFileName(dir)));
            var childNode = new FileLookupNode { Name = Path.GetFileName(dir), FullPath = dir, IsDirectory = true };
            PopulateBySysId(childNode, sysIds, onlyFInGridFilterDir, childRestrict);
            if (childNode.Children.Count > 0)
                node.Children.Add(childNode);
        }

        foreach (var file in files)
        {
            if (restrictToF && !Path.GetExtension(file).Equals(".f", StringComparison.OrdinalIgnoreCase))
                continue;
            if (sysIds.Contains(Path.GetFileNameWithoutExtension(file)))
                node.Children.Add(new FileLookupNode { Name = Path.GetFileName(file), FullPath = file, IsDirectory = false });
        }
    }

    private void PopulateRecursive(FileLookupNode node, string extensionFilter, string? searchText, bool onlyShowFiltered, bool pruneEmptyFolders)
    {
        List<string> dirs, files;
        try
        {
            dirs = Directory.GetDirectories(node.FullPath).OrderBy(d => d).ToList();
            files = Directory.GetFiles(node.FullPath).OrderBy(f => f).ToList();
        }
        catch (Exception)
        {
            return; // permission or path issue — leave node empty rather than crash the tree build
        }

        foreach (var dir in dirs)
        {
            var childNode = new FileLookupNode { Name = Path.GetFileName(dir), FullPath = dir, IsDirectory = true };
            PopulateRecursive(childNode, extensionFilter, searchText, onlyShowFiltered, pruneEmptyFolders);
            if (!pruneEmptyFolders || childNode.Children.Count > 0)
                node.Children.Add(childNode);
        }

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            if (onlyShowFiltered && !string.IsNullOrEmpty(extensionFilter) && !ext.Equals(extensionFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            var name = Path.GetFileName(file);
            if (!string.IsNullOrWhiteSpace(searchText) && name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            node.Children.Add(new FileLookupNode { Name = name, FullPath = file, IsDirectory = false });
        }
    }
}
