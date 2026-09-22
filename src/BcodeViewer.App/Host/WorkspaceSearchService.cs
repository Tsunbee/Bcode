using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BcodeViewer.App.Host;

/// <summary>
/// "Find in Files" across the whole project folder — the one thing Ctrl+F could never do,
/// since Monaco's find widget only ever sees the single model that is open.
///
/// Runs entirely on this side rather than handing the page a file list to grep itself: a
/// FastBusiness site's App_Data holds thousands of controller files on a UNC share, and
/// reading each one through an IDispatch round trip would cost one marshaled call per file.
/// Here it is one call in, one JSON array out.
///
/// Scope rules worth knowing:
/// <list type="bullet">
/// <item>The root is the App_Data folder above the open file when there is one (see
/// <see cref="ResolveRoot"/>) — that is the unit people mean by "the project" here, and it
/// keeps a search from wandering up into an entire customer share.</item>
/// <item>Only text-ish extensions are read (<see cref="DefaultExtensions"/>). Without that
/// filter the first .rar or .pdf in the tree turns into megabytes of binary noise scanned
/// line by line.</item>
/// <item>Results are capped. A two-letter query against a large site matches essentially
/// everything, and a result list nobody can read is worth less than a truncated one that
/// says it was truncated.</item>
/// </list>
/// </summary>
internal static class WorkspaceSearchService
{
    /// <summary>Extensions worth reading as text. Deliberately an allowlist, not a
    /// binary-sniffing denylist: what lives under App_Data is known, and a new extension
    /// showing up silently unsearchable is a smaller problem than scanning a 2 GB backup.</summary>
    private static readonly HashSet<string> DefaultExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xml", ".f", ".ent", ".sql", ".js", ".css", ".html", ".htm", ".aspx", ".ascx",
        ".json", ".txt", ".config", ".cs", ".vb", ".md", ".code-snippets",
    };

    /// <summary>Folders never worth searching — build output and VCS metadata, which would
    /// otherwise dominate the results with copies of files the user already has open.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".vs", "bin", "obj", "node_modules", "packages", "__history",
    };

    private const int MaxFileBytes = 8 * 1024 * 1024;

    /// <summary>
    /// The folder a search rooted at <paramref name="anchor"/> should cover: the App_Data
    /// directory above it when the path has one (the same anchor F12/Open Folder already
    /// use — see editor.js's appDataRoot), otherwise the containing folder.
    /// </summary>
    public static string ResolveRoot(string anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor)) return "";
        var idx = anchor.IndexOf(@"\App_Data\", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) return anchor[..(idx + @"\App_Data".Length)];
        if (Directory.Exists(anchor)) return anchor;
        try { return Path.GetDirectoryName(anchor) ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// Searches <paramref name="root"/> and returns
    /// <c>{root, truncated, filesScanned, matches:[{path, name, line, column, length, preview}]}</c>,
    /// or <c>{error}</c>. Never throws: an unreadable file or a dropped share mid-walk skips
    /// that entry rather than losing every match found so far.
    /// </summary>
    /// <param name="includeGlobs">Comma-separated filename patterns ("*.xml, *.sql"). Empty
    /// means every searchable extension.</param>
    public static string Search(
        string root, string query, bool useRegex, bool caseSensitive, bool wholeWord,
        string? includeGlobs, int maxResults)
    {
        if (string.IsNullOrEmpty(query)) return JsonSerializer.Serialize(new { error = "Chưa nhập từ khóa." });
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return JsonSerializer.Serialize(new { error = $"Không tìm thấy thư mục: {root}" });
        if (maxResults <= 0) maxResults = 2000;

        Regex regex;
        try
        {
            regex = BuildRegex(query, useRegex, caseSensitive, wholeWord);
        }
        catch (ArgumentException ex)
        {
            // An invalid regex is something the user typed, not a failure — report it in the
            // results panel instead of leaving the panel empty and unexplained.
            return JsonSerializer.Serialize(new { error = "Regex không hợp lệ: " + ex.Message });
        }

        var patterns = ParseGlobs(includeGlobs);
        var matches = new List<object>();
        var filesScanned = 0;
        var truncated = false;

        foreach (var file in EnumerateFiles(root))
        {
            if (truncated) break;
            if (!IsSearchable(file, patterns)) continue;

            string[] lines;
            try
            {
                var info = new FileInfo(file);
                if (info.Length > MaxFileBytes) continue;
                lines = File.ReadAllLines(file, Encoding.UTF8);
            }
            catch
            {
                continue; // locked, deleted between the walk and the read, or not text
            }

            filesScanned++;
            var name = Path.GetFileName(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // How much the preview trims off the front, so the page can place the
                // highlight: the result row shows the trimmed line, and its columns no
                // longer line up with the file's.
                var indent = lines[i].Length - lines[i].TrimStart().Length;
                foreach (Match m in regex.Matches(lines[i]))
                {
                    // A zero-width pattern (e.g. "^" or "\b") matches at every position and
                    // would fill the cap with results that point at nothing.
                    if (m.Length == 0) continue;
                    matches.Add(new
                    {
                        path = file,
                        name,
                        line = i + 1,
                        column = m.Index + 1,
                        length = m.Length,
                        preview = Preview(lines[i]),
                        previewOffset = indent,
                    });
                    if (matches.Count >= maxResults) { truncated = true; break; }
                }
                if (truncated) break;
            }
        }

        return JsonSerializer.Serialize(new { root, truncated, filesScanned, matches });
    }

    /// <summary>
    /// Applies <paramref name="replacement"/> to every match of the same query in the given
    /// files and returns <c>{filesChanged, replacements, errors:[...]}</c>.
    ///
    /// Each file is written through <see cref="Settings.LocalHistoryStore"/> first (same path
    /// a normal save takes), because this is the one action here that can change dozens of
    /// files a user never opened — "undo" has to mean something afterwards, and Ctrl+Z only
    /// covers the one document in the editor.
    /// </summary>
    public static string Replace(
        string[] paths, string query, string replacement,
        bool useRegex, bool caseSensitive, bool wholeWord)
    {
        Regex regex;
        try { regex = BuildRegex(query, useRegex, caseSensitive, wholeWord); }
        catch (ArgumentException ex) { return JsonSerializer.Serialize(new { error = "Regex không hợp lệ: " + ex.Message }); }

        var errors = new List<string>();
        var filesChanged = 0;
        var replacements = 0;

        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var original = File.ReadAllText(path);
                var count = regex.Matches(original).Count(m => m.Length > 0);
                if (count == 0) continue;

                // $1/$& only make sense when the user typed a real pattern; in plain-text
                // mode "$" is a dollar sign they typed and must survive verbatim.
                var updated = useRegex
                    ? regex.Replace(original, replacement)
                    : regex.Replace(original, m => replacement);
                if (string.Equals(updated, original, StringComparison.Ordinal)) continue;

                try { Settings.LocalHistoryStore.Snapshot(path, original); }
                catch { /* a missing backup must not block the edit the user asked for */ }

                File.WriteAllText(path, updated);
                filesChanged++;
                replacements += count;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return JsonSerializer.Serialize(new { filesChanged, replacements, errors });
    }

    private static Regex BuildRegex(string query, bool useRegex, bool caseSensitive, bool wholeWord)
    {
        var pattern = useRegex ? query : Regex.Escape(query);
        if (wholeWord) pattern = $@"\b(?:{pattern})\b";
        var options = RegexOptions.CultureInvariant;
        if (!caseSensitive) options |= RegexOptions.IgnoreCase;
        // A pathological pattern against thousands of files would otherwise hang the search
        // thread with no way back; a timeout turns that into one skipped line.
        return new Regex(pattern, options, TimeSpan.FromSeconds(2));
    }

    private static string[] ParseGlobs(string? includeGlobs) =>
        (includeGlobs ?? "")
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.Length > 0)
            .ToArray();

    private static bool IsSearchable(string file, string[] patterns)
    {
        var name = Path.GetFileName(file);
        if (patterns.Length > 0)
            return patterns.Any(p => MatchesGlob(name, p));
        return DefaultExtensions.Contains(Path.GetExtension(file));
    }

    /// <summary>Filename-only glob with <c>*</c> and <c>?</c> — enough for "*.xml" style
    /// filters without pulling in a matcher library for one text box.</summary>
    private static bool MatchesGlob(string name, string pattern)
    {
        var rx = "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
        return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Depth-first walk that survives an unreadable folder. Directory.EnumerateFiles with
    /// AllDirectories aborts the whole enumeration on the first access-denied subfolder,
    /// which on a customer share is close to guaranteed.
    /// </summary>
    private static IEnumerable<string> EnumerateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { continue; }
            foreach (var f in files) yield return f;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var d in subdirs)
            {
                if (SkippedFolders.Contains(Path.GetFileName(d))) continue;
                stack.Push(d);
            }
        }
    }

    /// <summary>One result row's text. Trimmed of leading indentation (deeply nested XML is
    /// mostly whitespace) and clipped, so a minified line can't blow up the panel.</summary>
    private static string Preview(string line)
    {
        var text = line.TrimStart();
        return text.Length > 240 ? text[..240] + "…" : text;
    }
}
