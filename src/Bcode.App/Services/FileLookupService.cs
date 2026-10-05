
using System.Collections.Concurrent;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

public class FileLookupNode
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public List<FileLookupNode> Children { get; } = new();

    /// <summary>Vấn đề entity của chính file này (xem <see cref="EntityCheckService"/>) — chỉ có
    /// ở chế độ menu; rỗng = ổn hoặc chưa kiểm.</summary>
    public List<string> Issues { get; } = new();

    /// <summary>File này hoặc bất kỳ file con nào có vấn đề — để tô đỏ cả thư mục chứa nó.</summary>
    public bool HasIssuesInTree => Issues.Count > 0 || Children.Any(c => c.HasIssuesInTree);
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
    /// <summary>Một lần quét toàn bộ thư mục (mọi folder + file bên dưới một gốc), giữ trong
    /// bộ nhớ để gõ search / đổi extension / bật tắt Only Show chỉ lọc lại danh sách này thay
    /// vì đi lại cả cây qua UNC mỗi lần — trước đây mỗi thay đổi nhỏ (kể cả mỗi lần gõ phím
    /// trong ô Search) đều gọi GetDirectories/GetFiles lại cho từng thư mục một.</summary>
    private sealed class FileIndex
    {
        public FileIndex(DateTime builtAtUtc, List<string> dirs, List<string> files)
        {
            BuiltAtUtc = builtAtUtc;
            Dirs = dirs;
            Files = files;
        }

        public DateTime BuiltAtUtc { get; }
        public List<string> Dirs { get; }
        public List<string> Files { get; }

        private ILookup<string, string>? _byName;
        /// <summary>Tên file không đuôi → các file mang tên đó. Tra theo tên là O(1) thay vì lọc cả danh sách file ở mỗi vòng.</summary>
        public ILookup<string, string> ByName =>
            _byName ??= Files.ToLookup(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);
    }

    // Lưới an toàn khi file trên site đổi mà không qua Bcode (người khác deploy, copy tay...):
    // hết hạn thì lần build kế tiếp tự quét lại. Bấm Load trên File Lookup luôn quét lại ngay.
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(2);

    // Lazy (ExecutionAndPublication) để hai yêu cầu cùng lúc cho cùng một gốc (vd gõ search
    // trong khi lần quét đầu còn chạy) dùng chung một lần quét thay vì quét UNC hai lần.
    private readonly ConcurrentDictionary<string, Lazy<FileIndex?>> _indexCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Bỏ toàn bộ danh sách file đã cache — lần build kế tiếp quét lại ổ đĩa. Gọi sau
    /// khi Bcode tự tạo/sửa file trong source, hoặc khi người dùng chủ động bấm Load.</summary>
    public void InvalidateCache() => _indexCache.Clear();

    /// <summary>Xoá cache kết quả phân tích file của dự án này (xem <see cref="FileParseCache"/>) — nút Load. Cache ấy tự kiểm lại
    /// mtime/size nên các chỗ khác (Bcode tự tạo file) không cần xoá.</summary>
    public void ResetParseCache(string sourceRootPath)
    {
        if (!string.IsNullOrWhiteSpace(sourceRootPath)) FileParseCache.Reset(sourceRootPath);
    }

    public FileLookupNode BuildTree(string sourceRootPath, string extensionFilter = ".f", string? searchText = null, bool onlyShowFiltered = true)
    {
        var root = new FileLookupNode { Name = Path.GetFileName(sourceRootPath.TrimEnd('\\', '/')), FullPath = sourceRootPath, IsDirectory = true };
        var rootKey = NormalizeDir(sourceRootPath);
        if (GetIndex(rootKey) is not { } index) return root;

        // A folder should be hidden once it has no matching descendant — that must happen
        // whenever an extension filter OR a search term is active, not just the extension
        // filter alone (otherwise a search still shows every folder in the whole tree).
        var pruneEmptyFolders = onlyShowFiltered || !string.IsNullOrWhiteSpace(searchText);
        var files = index.Files.Where(file =>
        {
            if (onlyShowFiltered && !string.IsNullOrEmpty(extensionFilter)
                && !Path.GetExtension(file).Equals(extensionFilter, StringComparison.OrdinalIgnoreCase))
                return false;
            return string.IsNullOrWhiteSpace(searchText)
                || Path.GetFileName(file).IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
        });
        FillFromPaths(root, rootKey, pruneEmptyFolders ? Array.Empty<string>() : index.Dirs, files);
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
        var mode = CacheMode;
        LastBuildNote = null;
        if (mode == FileLookupCacheMode.Off)
            return BuildMenuTreeCore(sourceRootPath, link, sysId, onlyFInGridFilterDir, onlyF, null);

        var session = new ReadSession(FileParseCache.For(sourceRootPath));
        var cached = BuildMenuTreeCore(sourceRootPath, link, sysId, onlyFInGridFilterDir, onlyF, session);
        LastBuildNote = $"cache: {session.Hits} hit / {session.Misses} miss";
        session.Cache.SaveInBackground();
        return cached;
    }

    /// <summary>Chế độ dùng cache khi bấm menu — đặt từ AppSettings.FileLookupCacheMode lúc khởi động.</summary>
    public static FileLookupCacheMode CacheMode { get; set; } = FileLookupCacheMode.On;

    /// <summary>Ghi chú về lần dựng cây menu gần nhất (số hit/miss của cache) để hiện trên thanh trạng thái.</summary>
    public string? LastBuildNote { get; private set; }

    /// <param name="session">null = cách cũ (đọc thẳng đĩa, lọc tuần tự); khác null = dùng <see cref="FileParseCache"/> và chỉ mục theo tên.</param>
    private FileLookupNode BuildMenuTreeCore(string sourceRootPath, string link, string sysId, bool onlyFInGridFilterDir, bool onlyF, ReadSession? session)
    {
        var useCache = session is not null;
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
            var controllersKey = NormalizeDir(controllersDir);
            if (GetIndex(controllersKey) is { } index)
            {
                var sysIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { sysId };
                // Chases the chain of explicitly-declared related controllers as far as it
                // goes — e.g. Dir\SVTran.xml's <items style="Grid" controller="SVDetail">
                // pulls in SVDetail, and SVDetail's OWN file in turn has
                // g.showForm('zSVSI2Filter') and a GridController entity naming
                // "zSVSI2MultiGrid", so those need a second pass over SVDetail's newly
                // found file, not just the original SVTran files. Keeps expanding until a
                // pass finds nothing new (capped so a reference cycle can't loop forever).
                // Mỗi pass chỉ lọc lại danh sách file đã quét sẵn, không đi lại cây Controllers.
                var scannedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var pass = 0; pass < 5; pass++)
                {
                    var filesToScan = (useCache
                        ? sysIds.SelectMany(n => index.ByName[n])
                        : index.Files.Where(f => sysIds.Contains(Path.GetFileNameWithoutExtension(f)))).ToList();
                    if (mainPath is { } main) filesToScan.Add(main);

                    var newFiles = filesToScan.Where(f => scannedFiles.Add(f)).ToList();
                    if (newFiles.Count == 0) break; // nothing left unscanned

                    // Cache bật: kiểm/đọc song song (phần tốn là chờ UNC); gộp tên vào sysIds tuần tự sau đó.
                    var relatedLists = new IEnumerable<string>[newFiles.Count];
                    if (useCache)
                        Parallel.For(0, newFiles.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                            relatedLists[i] = !ScannableExtensions.Contains(Path.GetExtension(newFiles[i]))
                                ? Array.Empty<string>() // .aspx... không đọc nên cũng không cần cache
                                : session!.Cache.GetOrCompute("rel", newFiles[i], session, rd => ExtractRelatedControllerNames(newFiles[i], rd)));
                    else
                        for (var i = 0; i < newFiles.Count; i++)
                            relatedLists[i] = ExtractRelatedControllerNames(newFiles[i], DirectRead);

                    var addedAny = false;
                    foreach (var list in relatedLists)
                        foreach (var related in list)
                            if (sysIds.Add(related))
                                addedAny = true;

                    if (!addedAny) break; // fixed point — no new controller names discovered
                }

                // File .rpt/Excel không mang tên sysId (vd Report\SVTran.xml khai
                // reportFile="SVTran_02", templateFile="SVTran_02FC") — lấy theo đúng những tên
                // được khai trong các file khai báo ở thư mục Report của menu này, tìm trong
                // Controllers\Templates (Rpt\*.rpt, Excel\*.xlsx trên site thật).
                var reportPrefix = Path.Combine(controllersKey, "Report") + Path.DirectorySeparatorChar;
                var templateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var reportFile in scannedFiles.Where(f => f.StartsWith(reportPrefix, StringComparison.OrdinalIgnoreCase)))
                    templateNames.UnionWith(useCache
                        ? session!.Cache.GetOrCompute("tpl", reportFile, session, rd => ExtractReportTemplateNames(reportFile, rd))
                        : ExtractReportTemplateNames(reportFile, DirectRead));

                var templatesPrefix = Path.Combine(controllersKey, "Templates") + Path.DirectorySeparatorChar;
                var templateFiles = templateNames.Count == 0
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : (useCache
                        ? templateNames.SelectMany(n => index.ByName[n])
                            .Where(f => f.StartsWith(templatesPrefix, StringComparison.OrdinalIgnoreCase))
                        : index.Files
                            .Where(f => f.StartsWith(templatesPrefix, StringComparison.OrdinalIgnoreCase)
                                        && templateNames.Contains(Path.GetFileNameWithoutExtension(f))))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                // File upload (Templates\Upload\SVTran.xml) kéo field/template từ các file
                // include qua SYSTEM entity — trực tiếp (Include\SVTranFields.txt) hoặc qua .ent
                // dùng chung (..\..\Include\DiscountRate.ent → ..\Templates\Upload\Include\
                // SVTranFieldsCompact.dct). Chỉ hiện những include nằm trong chính thư mục
                // Upload đó; plumbing .ent dùng chung ở Controllers\Include thì không.
                foreach (var uploadFile in scannedFiles.Where(f => IsUnderFolderNamed(controllersKey, f, "Upload")))
                {
                    var uploadDirPrefix = (Path.GetDirectoryName(uploadFile) ?? controllersKey) + Path.DirectorySeparatorChar;
                    foreach (var included in (IEnumerable<string>)(useCache
                        ? session!.Cache.GetOrCompute("inc", uploadFile, session, rd => ResolveReferencedIncludes(uploadFile, rd))
                        : ResolveReferencedIncludes(uploadFile, DirectRead)))
                        if (included.StartsWith(uploadDirPrefix, StringComparison.OrdinalIgnoreCase))
                            templateFiles.Add(included);
                }

                // File upload "ngược chiều": Templates\Upload\CBMaster.xml không mang tên sysId nên dò theo tên không ra, nhưng tự khai
                // <!ENTITY TransferID "CBTran"> trỏ về menu — FCode lấy cả những file này (CBDetail thì có tên nằm trong Dir nên đã có sẵn).
                var uploadCandidates = index.Files
                    .Where(f => IsUnderFolderNamed(controllersKey, f, "Upload") && ScannableExtensions.Contains(Path.GetExtension(f)))
                    .ToList();
                var transferIds = new string[uploadCandidates.Count][];
                if (useCache)
                    Parallel.For(0, uploadCandidates.Count, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                        transferIds[i] = session!.Cache.GetOrCompute("xfer", uploadCandidates[i], session, rd => ExtractTransferIds(uploadCandidates[i], rd)));
                else
                    for (var i = 0; i < uploadCandidates.Count; i++)
                        transferIds[i] = ExtractTransferIds(uploadCandidates[i], DirectRead).ToArray();
                for (var i = 0; i < uploadCandidates.Count; i++)
                {
                    if (!transferIds[i].Any(sysIds.Contains)) continue;
                    var uploadFile = uploadCandidates[i];
                    templateFiles.Add(uploadFile);
                    // include của file này: cùng quy tắc như các file Upload ở trên (chỉ lấy include nằm trong thư mục Upload của nó)
                    var uploadDirPrefix = (Path.GetDirectoryName(uploadFile) ?? controllersKey) + Path.DirectorySeparatorChar;
                    foreach (var included in (IEnumerable<string>)(useCache
                        ? session!.Cache.GetOrCompute("inc", uploadFile, session, rd => ResolveReferencedIncludes(uploadFile, rd))
                        : ResolveReferencedIncludes(uploadFile, DirectRead)))
                        if (included.StartsWith(uploadDirPrefix, StringComparison.OrdinalIgnoreCase))
                            templateFiles.Add(included);
                }

                // Once a path passes through a Grid/Filter/Dir folder with onlyFInGridFilterDir
                // set (or onlyF is on for the whole menu), only ".f" files are kept there.
                bool RequiresF(string file)
                {
                    if (onlyF) return true;
                    if (!onlyFInGridFilterDir) return false;
                    var relativeDir = Path.GetRelativePath(controllersKey, Path.GetDirectoryName(file) ?? controllersKey);
                    return relativeDir != "." && relativeDir
                        .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Any(FOnlyFolderNames.Contains);
                }

                var candidates = useCache
                    ? sysIds.SelectMany(n => index.ByName[n]).Concat(templateFiles).Distinct(StringComparer.OrdinalIgnoreCase)
                    : index.Files.Where(f => sysIds.Contains(Path.GetFileNameWithoutExtension(f)) || templateFiles.Contains(f));
                var matched = candidates.Where(f =>
                    !RequiresF(f) || Path.GetExtension(f).Equals(".f", StringComparison.OrdinalIgnoreCase));

                var controllersNode = new FileLookupNode { Name = "Controllers", FullPath = controllersDir, IsDirectory = true };
                var matchedList = matched.ToList();
                FillFromPaths(controllersNode, controllersKey, Array.Empty<string>(), matchedList);
                AttachEntityIssues(controllersNode, matchedList, session);
                if (controllersNode.Children.Count > 0)
                    root.Children.Add(controllersNode);
            }
        }

        return root;
    }

    /// <summary>Kiểm entity cho từng file .xml của menu (song song — đọc qua UNC là phần tốn thời
    /// gian) rồi gắn kết quả vào node tương ứng. Lỗi khi kiểm 1 file chỉ bỏ qua file đó, không
    /// làm hỏng cả cây.</summary>
    private static void AttachEntityIssues(FileLookupNode root, List<string> files, ReadSession? session)
    {
        var results = new ConcurrentDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var readCache = new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(
            files.Where(f => Path.GetExtension(f).Equals(".xml", StringComparison.OrdinalIgnoreCase)),
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            file =>
            {
                try
                {
                    var issues = session is null
                        ? EntityCheckService.Analyze(file, readCache)
                        : session.Cache.GetOrCompute("ent", file, session, rd =>
                            EntityCheckService.Analyze(file, new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase), rd)).ToList();
                    if (issues.Count > 0) results[file] = issues;
                }
                catch (Exception) { /* bỏ qua file này */ }
            });
        if (results.IsEmpty) return;

        void Walk(FileLookupNode node)
        {
            if (!node.IsDirectory && results.TryGetValue(node.FullPath, out var issues)) node.Issues.AddRange(issues);
            foreach (var child in node.Children) Walk(child);
        }
        Walk(root);
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
    /// Files are read in parallel (reading dominates over UNC); <paramref name="cancellationToken"/>
    /// stops a search the user has already replaced with a newer one. <paramref name="onMatch"/> (nếu có) được gọi từ luồng nền ngay khi
    /// thấy 1 file khớp để giao diện hiện kết quả dần thay vì đợi quét xong.
    /// </summary>
    public FileLookupNode SearchFileContents(string searchInPath, string fileTypePattern, string searchText, bool matchCase, bool useWildcardPattern, CancellationToken cancellationToken = default, Action<string>? onMatch = null)
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
        // Win32 match type keeps "*.*" meaning "every file" like the old
        // Directory.EnumerateFiles overload; IgnoreInaccessible skips a locked subfolder
        // instead of aborting the whole enumeration halfway through.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            MatchType = MatchType.Win32
        };

        // "*.*" / "*" = người dùng không chọn loại file: bỏ qua file nhị phân (.rpt, .xlsx, .dll, ảnh...) và file quá lớn — đọc cả file qua UNC
        // rồi giải mã thành text chỉ tốn thời gian mà không bao giờ khớp. Gõ rõ đuôi (vd "*.rpt") thì vẫn quét đúng như cũ.
        var genericPattern = pattern is "*.*" or "*";
        var needle = Encoding.UTF8.GetBytes(searchText);
        var needleAscii = needle.All(b => b < 0x80);
        var needleLower = needleAscii ? needle.Select(AsciiLower).ToArray() : needle;

        bool IsMatch(byte[] bytes)
        {
            // Regex ("Show Pattern") cần chuỗi; file UTF-16/32 có BOM cũng phải giải mã đúng như File.ReadAllText từng làm.
            if (patternRegex is not null || HasWideBom(bytes))
                return DecodeText(bytes) is var text && (patternRegex?.IsMatch(text) ?? text.Contains(searchText, matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
            // Còn lại tìm thẳng trên byte UTF-8 (có vector hoá) — không cần giải mã cả file thành string.
            if (matchCase) return bytes.AsSpan().IndexOf(needle) >= 0;
            if (needleAscii) return ContainsAsciiIgnoreCase(bytes, needleLower);
            return DecodeText(bytes).Contains(searchText, StringComparison.OrdinalIgnoreCase); // chữ có dấu, không phân biệt hoa/thường
        }

        var matches = new ConcurrentBag<string>();
        try
        {
            // NoBuffering: giao từng file cho từng luồng (mặc định gom thành cụm tăng dần → vài luồng ôm hết việc, luồng khác ngồi chờ);
            // đọc qua UNC chủ yếu là chờ mạng nên nhiều luồng hơn số nhân CPU vẫn có lợi.
            var files = Partitioner.Create(
                ResolveSearchRoots(searchInPath).SelectMany(dir => Directory.EnumerateFiles(dir, pattern, options)),
                EnumerablePartitionerOptions.NoBuffering);
            Parallel.ForEach(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = SearchParallelism, CancellationToken = cancellationToken },
                file =>
                {
                    var extension = Path.GetExtension(file);
                    if (NeverSearchedExtensions.Contains(extension) || (genericPattern && BinaryExtensions.Contains(extension))) return;
                    if (genericPattern && extension.Equals(".f", StringComparison.OrdinalIgnoreCase) && IsInCompiledFolder(file)) return;

                    byte[] bytes;
                    try
                    {
                        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
                        if (genericPattern && stream.Length > MaxGenericSearchBytes) return;
                        bytes = new byte[stream.Length];
                        var read = 0;
                        while (read < bytes.Length)
                        {
                            var n = stream.Read(bytes, read, bytes.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        if (read < bytes.Length) Array.Resize(ref bytes, read);
                    }
                    catch (Exception) { return; } // locked/unreadable — skip rather than abort the whole search

                    if (IsMatch(bytes))
                    {
                        matches.Add(file);
                        onMatch?.Invoke(file);
                    }
                });
        }
        catch (OperationCanceledException) { throw; }
        catch { /* bad pattern or an inaccessible/down UNC path — show what was found rather than throw */ }

        foreach (var file in matches.OrderBy(f => f))
            root.Children.Add(new FileLookupNode { Name = Path.GetFileName(file), FullPath = file, IsDirectory = false });
        return root;
    }

    private const int SearchParallelism = 16;
    private const long MaxGenericSearchBytes = 20L * 1024 * 1024;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rpt", ".xlsx", ".xls", ".xlsm", ".xlsb", ".doc", ".docx", ".ppt", ".pptx", ".pdf",
        ".dll", ".exe", ".pdb", ".obj", ".lib", ".so", ".bin", ".dat", ".cache",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tif", ".tiff", ".svgz",
        ".zip", ".rar", ".7z", ".gz", ".tar", ".nupkg", ".bak", ".mdf", ".ldf",
        ".ttf", ".otf", ".woff", ".woff2", ".eot", ".mp3", ".mp4", ".avi", ".wav",
    };

    // Luôn bỏ qua dù File Type là gì: không phải mã nguồn cần tìm.
    private static readonly HashSet<string> NeverSearchedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".htm" };

    // Search in trỏ vào site/App_Data (có thư mục Controllers) thì chỉ tìm trong các thư mục con này của Controllers.
    private static readonly string[] SearchFolderNames = { "Dir", "Filter", "Grid", "Include", "List", "Lookup", "Options", "Templates" };

    /// <summary>Các thư mục thật sự quét. Search in là site gốc, App_Data hoặc chính Controllers → chỉ các thư mục con trong
    /// <see cref="SearchFolderNames"/> của Controllers. Search in đã nằm sâu hơn (vd Controllers\Grid) hoặc không có Controllers
    /// → quét đúng thư mục đó như cũ, để vẫn tìm được chỗ khác khi cần.</summary>
    private static List<string> ResolveSearchRoots(string searchInPath)
    {
        var trimmed = searchInPath.TrimEnd('\\', '/');
        string? controllers = null;
        if (Path.GetFileName(trimmed).Equals("Controllers", StringComparison.OrdinalIgnoreCase)) controllers = trimmed;
        else
        {
            foreach (var candidate in new[] { Path.Combine(trimmed, "Controllers"), Path.Combine(trimmed, "App_Data", "Controllers") })
                if (Directory.Exists(candidate)) { controllers = candidate; break; }
        }
        if (controllers is null) return new List<string> { searchInPath };

        var roots = SearchFolderNames.Select(n => Path.Combine(controllers, n)).Where(Directory.Exists).ToList();
        return roots.Count > 0 ? roots : new List<string> { searchInPath };
    }

    // Controllers\Dir, \Filter, \Grid: file .f ở đó là bản đã biên dịch/mã hoá của .xml cùng tên — tìm chữ trong đó vô ích (xem cả FOnlyFolderNames).
    private static readonly string[] CompiledFolderNames = { "Dir", "Filter", "Grid" };

    /// <summary>File nằm dưới Controllers\Dir, Controllers\Filter hoặc Controllers\Grid (ở bất kỳ độ sâu nào).</summary>
    private static bool IsInCompiledFolder(string file)
    {
        var parts = file.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var controllers = Array.FindLastIndex(parts, p => p.Equals("Controllers", StringComparison.OrdinalIgnoreCase));
        return controllers >= 0 && controllers + 1 < parts.Length - 1
            && CompiledFolderNames.Contains(parts[controllers + 1], StringComparer.OrdinalIgnoreCase);
    }

    private static byte AsciiLower(byte b) => b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;

    /// <summary>File UTF-16/UTF-32 (có BOM): tìm thẳng trên byte UTF-8 sẽ không thấy, phải giải mã như File.ReadAllText.</summary>
    private static bool HasWideBom(byte[] b) =>
        b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF));

    private static string DecodeText(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Tìm chuỗi ASCII (<paramref name="needleLower"/> đã hạ chữ thường) không phân biệt hoa/thường trên byte: nhảy tới ký tự đầu
    /// bằng IndexOfAny (vector hoá) rồi so phần còn lại. Chữ không ASCII trong file không bao giờ khớp.</summary>
    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> haystack, byte[] needleLower)
    {
        var first = needleLower[0];
        var firstUpper = first is >= (byte)'a' and <= (byte)'z' ? (byte)(first - 32) : first;
        var position = 0;
        while (position <= haystack.Length - needleLower.Length)
        {
            var window = haystack[position..];
            var index = first == firstUpper ? window.IndexOf(first) : window.IndexOfAny(first, firstUpper);
            if (index < 0) return false;
            var start = position + index;
            if (haystack.Length - start < needleLower.Length) return false;
            if (Ascii.EqualsIgnoreCase(haystack.Slice(start, needleLower.Length), needleLower)) return true;
            position = start + 1;
        }
        return false;
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

    /// <summary>Đọc file thẳng từ đĩa (cách cũ, không cache); null khi thiếu/khoá/rớt share.</summary>
    private static string? DirectRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception) { return null; }
    }

    private static List<string> ExtractRelatedControllerNames(string filePath, Func<string, string?> read)
    {
        var found = new List<string>();
        if (!ScannableExtensions.Contains(Path.GetExtension(filePath))) return found;

        // unreadable/locked — just skip discovering related controllers from it
        if (read(filePath) is not { } content) return found;

        foreach (Match tag in GridItemsTagRegex.Matches(content))
        {
            if (!StyleGridRegex.IsMatch(tag.Value)) continue;
            var controllerMatch = ControllerAttrRegex.Match(tag.Value);
            if (controllerMatch.Success)
                found.Add(controllerMatch.Groups[1].Value);
        }

        foreach (Match m in ShowFormRegex.Matches(content))
            found.Add(m.Groups[1].Value);

        // Một file trong Filter khai <!ENTITY Identity "SVIssue"> thì form nó mở là
        // Filter\SVIssueForm, Filter\SVIssueMultiForm cùng Grid\SVIssueGrid, Grid\SVIssueMultiGrid (vd ...on$&Identity;Filter$Retrieve$
        // QueryComplete(..., '&Identity;MultiForm', ...)) — tên đó chỉ ghép lúc chạy nên không
        // regex nào ở trên bắt được.
        var isFilterFile = string.Equals(Path.GetFileName(Path.GetDirectoryName(filePath)), "Filter", StringComparison.OrdinalIgnoreCase);
        foreach (Match m in PlainEntityRegex.Matches(content))
        {
            var name = m.Groups[1].Value;
            var value = m.Groups[2].Value.Trim();
            if (name.Equals("GridController", StringComparison.OrdinalIgnoreCase))
                found.Add(value);
            else if (isFilterFile && name.Equals("Identity", StringComparison.OrdinalIgnoreCase) && IdentifierRegex.IsMatch(value))
            {
                found.Add(value + "Form");
                found.Add(value + "MultiForm");
                // ...and the grids those forms show: Grid\SVIssueGrid, Grid\SVIssueMultiGrid.
                found.Add(value + "Grid");
                found.Add(value + "MultiGrid");
            }
        }
    
        return found;
    }

    private static readonly Regex IdentifierRegex = new(@"^[A-Za-z0-9_$]+$", RegexOptions.Compiled);
    // <!ENTITY ISTran SYSTEM ".\Include\ISTranBI.xml"> / <!ENTITY % External SYSTEM ".\Config\SVTran.ent">
    private static readonly Regex SystemEntityRegex = new(@"<!ENTITY\s+(%\s+)?[A-Za-z0-9_.$]+\s+SYSTEM\s+""([^""]*)""", RegexOptions.Compiled);
    // Like PlainEntityRegex but also allows dotted names (&Sign.Function.Code;) — report DTDs use them.
    private static readonly Regex DottedPlainEntityRegex = new(@"<!ENTITY\s+([A-Za-z0-9_.$]+)\s+""([^""]*)""", RegexOptions.Compiled);
    private static readonly Regex TemplateAttrRegex = new(@"\b(?:reportFile|templateFile)\s*=\s*""([^""]*)""", RegexOptions.Compiled);
    // select 'SVTran_03_xk' as reportFile — a report file picked in the report's own SQL.
    private static readonly Regex SqlTemplateRegex = new(@"'([A-Za-z0-9_$]+)'\s+as\s+(?:reportFile|templateFile)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlTemplateVariableRegex = new(@"(@[A-Za-z0-9_$]+)\s+as\s+(?:reportFile|templateFile)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SqlStringLiteralRegex = new(@"'([A-Za-z0-9_$]+)'", RegexOptions.Compiled);
    private static readonly Regex EntityRefRegex = new(@"&([A-Za-z0-9_.$]+);", RegexOptions.Compiled);

    private static bool IsUnderFolderNamed(string rootKey, string file, string folderName)
    {
        var relativeDir = Path.GetRelativePath(rootKey, Path.GetDirectoryName(file) ?? rootKey);
        return relativeDir != "." && relativeDir
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals(folderName, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Regex TransferIdRegex = new(@"<!ENTITY\s+TransferID\s+""([^""]+)""", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Các mã menu mà một file upload khai báo là của mình qua <c>&lt;!ENTITY TransferID "CBTran"&gt;</c> (rỗng nếu không có).</summary>
    private static IEnumerable<string> ExtractTransferIds(string filePath, Func<string, string?> read)
    {
        if (!ScannableExtensions.Contains(Path.GetExtension(filePath)) || read(filePath) is not { } content) return Array.Empty<string>();
        return TransferIdRegex.Matches(content).Select(m => m.Groups[1].Value.Trim()).Where(v => v.Length > 0).ToList();
    }

    // Captures (%)? name and path: <!ENTITY SVTranFields SYSTEM "Include\SVTranFields.txt">
    private static readonly Regex NamedSystemEntityRegex = new(@"<!ENTITY\s+(%\s+)?([A-Za-z0-9_.$]+)\s+SYSTEM\s+""([^""]*)""", RegexOptions.Compiled);

    /// <summary>Files that <paramref name="mainFile"/> really pulls in through general SYSTEM
    /// entities. Parameter entities (%X;) are always followed — they're how the DTD itself is
    /// assembled from shared .ent files — and a relative path resolves against the file that
    /// declares it (..\Templates\Upload\Include\... in Controllers\Include\DiscountRate.ent).
    /// A general entity counts only once &amp;Name; is actually used by the main file or by an
    /// include it already uses: a shared .ent declares includes for several vouchers
    /// (ARTranFields.dct next to SVTranFields.dct), and only this voucher's belong here. An
    /// entity declared more than once (INCLUDE/IGNORE sections) contributes every declaration.</summary>
    /// <summary>File trong Controllers\Include (mọi thư mục con) mà tên file chứa 1 trong <paramref name="tokens"/>
    /// (không phân biệt hoa/thường) — vd Extender.ZVCTran, Revert.ZVCTran.ent, ZVCReference.ent: tên controller đứng giữa/sau
    /// nên không khớp theo "tên không đuôi" và cũng không được xml nào khai entity trực tiếp.</summary>
    public List<string> FindIncludeFilesByName(string sourceRootPath, IReadOnlyCollection<string> tokens)
    {
        if (tokens.Count == 0) return new List<string>();
        var includeKey = NormalizeDir(Path.Combine(sourceRootPath, "App_Data", "Controllers", "Include"));
        if (GetIndex(includeKey) is not { } index) return new List<string>();
        return index.Files
            .Where(f => tokens.Any(t => Path.GetFileName(f).Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>File .ent trong Controllers\Include mà NỘI DUNG nhắc tới <paramref name="controller"/> như 1 từ nguyên vẹn — vd
    /// Extender.ent có dòng &lt;!ENTITY % Conditional.Extender.List.ZVCTran "INCLUDE"&gt; (file đăng ký dùng chung: tên không chứa
    /// tên controller và không xml nào khai entity tới nó, nhưng thiếu dòng đó thì controller mới không chạy).</summary>
    public List<string> FindIncludeEntFilesMentioning(string sourceRootPath, string controller)
    {
        var includeKey = NormalizeDir(Path.Combine(sourceRootPath, "App_Data", "Controllers", "Include"));
        if (GetIndex(includeKey) is not { } index) return new List<string>();
        var word = new Regex(@"(?<![A-Za-z0-9_$])" + Regex.Escape(controller) + @"(?![A-Za-z0-9_$])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var hits = new ConcurrentBag<string>();
        Parallel.ForEach(index.Files.Where(f => Path.GetExtension(f).Equals(".ent", StringComparison.OrdinalIgnoreCase)),
            new ParallelOptions { MaxDegreeOfParallelism = 8 }, f =>
            {
                var text = DirectRead(f);
                if (text is not null && word.IsMatch(text)) hits.Add(f);
            });
        return hits.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Các file include/entity mà <paramref name="mainFile"/> thực sự tham chiếu (dùng cho Advance Note → Gen All).</summary>
    public static List<string> GetReferencedIncludes(string mainFile) => ResolveReferencedIncludes(mainFile, DirectRead);

    private static List<string> ResolveReferencedIncludes(string mainFile, Func<string, string?> read)
    {
        var declarations = new List<(string Name, string Path)>();
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? Read(string path)
        {
            if (!readFiles.Add(path)) return null;
            return read(path); // missing/unreadable → null — skip, keep the rest
        }

        void AddReferences(string content)
        {
            foreach (Match m in EntityRefRegex.Matches(content))
                references.Add(m.Groups[1].Value);
        }

        void LoadDtd(string path, int depth)
        {
            if (depth > 4 || Read(path) is not { } content) return;
            if (depth == 0) AddReferences(content);
            var dir = Path.GetDirectoryName(path) ?? "";
            foreach (Match m in NamedSystemEntityRegex.Matches(content))
            {
                string resolved;
                try { resolved = Path.GetFullPath(Path.Combine(dir, m.Groups[3].Value)); }
                catch (Exception) { continue; }
                if (m.Groups[1].Success) LoadDtd(resolved, depth + 1);
                else declarations.Add((m.Groups[2].Value, resolved));
            }
        }
        LoadDtd(mainFile, 0);

        // An include can itself use more entities (&VoucherGoodsTypeImportFields; inside a
        // fields .txt) — keep going until no newly used include turns up.
        var result = new List<string>();
        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool grew;
        do
        {
            grew = false;
            foreach (var (name, path) in declarations)
            {
                if (!references.Contains(name) || added.Contains(path)) continue;
                added.Add(path);
                if (Read(path) is not { } content) continue; // declared but not on disk
                result.Add(path);
                AddReferences(content);
                grew = true;
            }
        } while (grew);
        return result;
    }

    /// <summary>Every reportFile/templateFile name a Report\*.xml declaration names — from the
    /// file itself and from the files it pulls in through SYSTEM entities (&amp;ISTran; →
    /// .\Include\ISTranBI.xml carries more forms; %PrintVATDetail; → .\Config\*.ent declares
    /// the &amp;PrintVATFile; value used as reportFile="&amp;PrintVATFile;"). An entity declared
    /// more than once (INCLUDE/IGNORE conditional sections) contributes all of its values —
    /// at worst that lists one extra template that exists on disk.</summary>
    private static HashSet<string> ExtractReportTemplateNames(string reportFilePath, Func<string, string?> read)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!ScannableExtensions.Contains(Path.GetExtension(reportFilePath))) return names;

        var contents = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Load(string path, int depth)
        {
            if (depth > 2 || !visited.Add(path)) return;
            if (read(path) is not { } content) return; // missing/unreadable include — skip it, keep the rest
            contents.Add(content);

            var dir = Path.GetDirectoryName(path) ?? "";
            foreach (Match m in SystemEntityRegex.Matches(content))
            {
                string included;
                try { included = Path.GetFullPath(Path.Combine(dir, m.Groups[2].Value)); }
                catch (Exception) { continue; }
                var ext = Path.GetExtension(included);
                if (ext.Equals(".xml", StringComparison.OrdinalIgnoreCase) || ext.Equals(".txt", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".ent", StringComparison.OrdinalIgnoreCase))
                    Load(included, depth + 1);
            }
        }
        Load(reportFilePath, 0);

        var entityValues = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var content in contents)
            foreach (Match m in DottedPlainEntityRegex.Matches(content))
            {
                if (!entityValues.TryGetValue(m.Groups[1].Value, out var list))
                    entityValues[m.Groups[1].Value] = list = new List<string>();
                list.Add(m.Groups[2].Value.Trim());
            }

        void AddName(string raw)
        {
            raw = raw.Trim();
            var entity = EntityRefRegex.Match(raw);
            if (entity.Success && entity.Value.Length == raw.Length)
            {
                if (entityValues.TryGetValue(entity.Groups[1].Value, out var values))
                    foreach (var v in values)
                        if (IdentifierRegex.IsMatch(v)) names.Add(v);
            }
            else if (IdentifierRegex.IsMatch(raw))
                names.Add(raw);
        }

        foreach (var content in contents)
        {
            foreach (Match m in TemplateAttrRegex.Matches(content)) AddName(m.Groups[1].Value);
            foreach (Match m in SqlTemplateRegex.Matches(content)) AddName(m.Groups[1].Value);

            // select @$isReportPortait = case when @@form = '610' then 'ISTran_02' ... end
            // ... select @$isReportPortait as reportFile — every name literal in that case
            // expression (form ids like '610' are all digits and skipped; a name that isn't
            // a real file under Templates just matches nothing).
            foreach (Match v in SqlTemplateVariableRegex.Matches(content))
            {
                var assignment = new Regex(Regex.Escape(v.Groups[1].Value) + @"\s*=\s*case\b(.*?)\bend\b",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
                foreach (Match a in assignment.Matches(content))
                    foreach (Match literal in SqlStringLiteralRegex.Matches(a.Groups[1].Value))
                        if (!literal.Groups[1].Value.All(char.IsDigit))
                            AddName(literal.Groups[1].Value);
            }
        }
        return names;
    }

    // Folder names under Controllers whose files are already the compiled/encrypted "*.f"
    // deployable — matched only when the caller opts in via onlyFInGridFilterDir (Gen Update).
    private static readonly HashSet<string> FOnlyFolderNames = new(StringComparer.OrdinalIgnoreCase) { "Grid", "Filter", "Dir" };

    private static string NormalizeDir(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch (Exception) { return path.Trim(); } // malformed path — GetIndex then just finds nothing there
    }

    /// <summary>Danh sách folder/file dưới <paramref name="rootKey"/> (đã NormalizeDir): lấy từ
    /// cache nếu còn hạn, cắt ra từ index của một thư mục cha đã quét nếu có (vd Controllers
    /// nằm trong gốc App_Data đã load ở File Lookup), còn không thì quét ổ đĩa. Null khi thư
    /// mục không tồn tại — không cache kết quả đó để lần sau (UNC lên lại) còn thử lại.</summary>
    private FileIndex? GetIndex(string rootKey)
    {
        if (_indexCache.TryGetValue(rootKey, out var cached) && IsFresh(cached))
            return cached.Value;

        foreach (var (ancestorKey, ancestor) in _indexCache)
        {
            if (!ancestor.IsValueCreated || !IsFresh(ancestor) || ancestor.Value is not { } parent) continue;
            if (!rootKey.StartsWith(ancestorKey + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;

            var prefix = rootKey + Path.DirectorySeparatorChar;
            var sub = new FileIndex(
                parent.BuiltAtUtc,
                parent.Dirs.Where(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList(),
                parent.Files.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList());
            _indexCache[rootKey] = new Lazy<FileIndex?>(sub);
            return sub;
        }

        var fresh = new Lazy<FileIndex?>(() => ScanDirectory(rootKey), LazyThreadSafetyMode.ExecutionAndPublication);
        var entry = _indexCache.AddOrUpdate(rootKey, fresh, (_, existing) => IsFresh(existing) ? existing : fresh);
        var result = entry.Value;
        if (result is null)
            _indexCache.TryRemove(KeyValuePair.Create(rootKey, entry));
        return result;
    }

    // An entry whose scan is still running counts as fresh — callers wait on it instead of
    // starting a second scan of the same folder.
    private static bool IsFresh(Lazy<FileIndex?> entry) =>
        !entry.IsValueCreated || (entry.Value is { } index && DateTime.UtcNow - index.BuiltAtUtc < IndexLifetime);

    /// <summary>One recursive enumeration of the whole folder — a single directory listing
    /// per folder (the old walk issued GetDirectories AND GetFiles for each one), and
    /// IgnoreInaccessible skips a permission-denied subfolder instead of dropping it silently
    /// mid-walk. AttributesToSkip = 0 keeps hidden/system entries, like GetDirectories/GetFiles did.</summary>
    private static FileIndex? ScanDirectory(string rootKey)
    {
        if (!Directory.Exists(rootKey)) return null;

        var dirs = new List<string>();
        var files = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        try
        {
            var entries = new FileSystemEnumerable<(string Path, bool IsDirectory)>(
                rootKey, (ref FileSystemEntry e) => (e.ToFullPath(), e.IsDirectory), options);
            foreach (var (path, isDirectory) in entries)
                (isDirectory ? dirs : files).Add(path);
        }
        catch (Exception)
        {
            // UNC path dropped mid-scan — keep whatever was listed so far rather than crash the tree build
        }
        return new FileIndex(DateTime.UtcNow, dirs, files);
    }

    private static readonly Comparer<FileLookupNode> NodeOrder = Comparer<FileLookupNode>.Create((a, b) =>
        a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : string.Compare(a.FullPath, b.FullPath, StringComparison.CurrentCulture));

    /// <summary>Hangs <paramref name="files"/> (and <paramref name="dirs"/>, for folders that
    /// should show even when empty) under <paramref name="root"/>, creating each intermediate
    /// folder node on demand — so passing no dirs automatically prunes every folder without a
    /// matching file. Children end up folders-first, each sorted by path, like the old walk.</summary>
    private static void FillFromPaths(FileLookupNode root, string rootKey, IEnumerable<string> dirs, IEnumerable<string> files)
    {
        var dirNodes = new Dictionary<string, FileLookupNode>(StringComparer.OrdinalIgnoreCase) { [rootKey] = root };

        FileLookupNode DirNode(string dir)
        {
            if (dirNodes.TryGetValue(dir, out var existing)) return existing;
            var parentPath = Path.GetDirectoryName(dir);
            // Not under rootKey (shouldn't happen for index paths) — attach straight to root.
            if (parentPath is null || dir.Length <= rootKey.Length) return root;

            var node = new FileLookupNode { Name = Path.GetFileName(dir), FullPath = dir, IsDirectory = true };
            DirNode(parentPath).Children.Add(node);
            dirNodes[dir] = node;
            return node;
        }

        foreach (var dir in dirs)
            DirNode(dir);
        foreach (var file in files)
            DirNode(Path.GetDirectoryName(file) ?? rootKey).Children.Add(
                new FileLookupNode { Name = Path.GetFileName(file), FullPath = file, IsDirectory = false });

        foreach (var node in dirNodes.Values)
            node.Children.Sort(NodeOrder);
    }
}
