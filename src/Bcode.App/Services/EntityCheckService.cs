using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// Kiểm tra 1 file controller (Dir/Grid/Filter/...) có thiếu entity không — cùng ý với panel
/// Problems của BcodeViewer, để File Lookup tô đỏ file lỗi ngay khi click 1 menu thay vì đợi tới
/// lúc chạy mới thấy "form trắng":
///  - <c>&amp;Name;</c> được dùng nhưng không có <c>&lt;!ENTITY Name ...&gt;</c> nào trong file hoặc
///    chuỗi file nó include.
///  - <c>%Name;</c> trong DOCTYPE/.ent dùng mà chưa khai báo.
///  - <c>&lt;!ENTITY X SYSTEM "path"&gt;</c> trỏ tới file không tồn tại.
///
/// CHỈ XÉT PHẦN LIÊN QUAN ĐẾN CHỨNG TỪ NÀY — cùng quy ước như FileLookupService.
/// ResolveReferencedIncludes: .ent dùng chung khai báo include/giá trị cho NHIỀU chứng từ, nên
///  - <c>% X SYSTEM</c> (parameter entity) luôn được nạp (đó là cách DTD được ráp từ file dùng chung);
///  - <c>X SYSTEM</c> thường chỉ được nạp khi <c>&amp;X;</c> thật sự được dùng;
///  - <c>&lt;!ENTITY A "... &amp;B; ..."&gt;</c> chỉ bắt B có khai báo khi A được dùng (XML chỉ khai
///    triển giá trị của A lúc A được tham chiếu).
/// Entity của chứng từ khác nằm trong cùng file dùng chung nhưng không được chứng từ này gọi tới
/// thì không bị báo. Chỉ xét file có &lt;!DOCTYPE&gt; (file gốc). Regex thuần, không parse XML.
/// </summary>
public static class EntityCheckService
{
    private const int MaxIncludeDepth = 64;
    private static readonly string[] BuiltIn = { "amp", "lt", "gt", "quot", "apos" };

    private static readonly Regex DeclRegex = new(
        @"<!ENTITY\s+(?<p>%\s+)?(?<name>[A-Za-z_][\w.:$-]*)\s+(?<sys>SYSTEM\s+)?(?:""(?<v1>[^""]*)""|'(?<v2>[^']*)')",
        RegexOptions.Compiled);
    private static readonly Regex GeneralRefRegex = new(@"&(?<n>[A-Za-z_][\w.:$-]*);", RegexOptions.Compiled);
    private static readonly Regex ParamRefRegex = new(@"%(?<n>[A-Za-z_][\w.:$-]*);", RegexOptions.Compiled);
    private static readonly Regex CdataOrCommentRegex = new(@"<!\[CDATA\[.*?\]\]>|<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex QuotedRegex = new(@"""[^""]*""|'[^']*'", RegexOptions.Compiled);

    private sealed class GeneralDecl
    {
        public string DeclFile = "";
        public string? SystemPath;                     // != null: SYSTEM entity (include file)
        public List<string> ValueRefs = new();         // Value entity: các &X; nằm trong giá trị
    }

    /// <summary>Danh sách vấn đề (mỗi phần tử 1 dòng tiếng Việt); rỗng = file ổn hoặc không phải file gốc.</summary>
    /// <param name="reader">Nếu có, mọi lần đọc file đi qua hàm này (trả null = thiếu/không đọc được) thay cho đọc đĩa trực tiếp —
    /// File Lookup dùng để ghi lại các file đã đọc cho cache.</param>
    public static List<string> Analyze(string filePath, ConcurrentDictionary<string, string?>? readCache = null, Func<string, string?>? reader = null)
    {
        readCache ??= new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<string>();

        var mainText = Read(filePath, readCache, reader);
        if (mainText is null || !Regex.IsMatch(mainText, @"<!DOCTYPE\b", RegexOptions.IgnoreCase)) return issues;

        var declaredParam = new HashSet<string>(StringComparer.Ordinal);
        var paramRefs = new Dictionary<string, string>(StringComparer.Ordinal);
        var generalDecls = new Dictionary<string, List<GeneralDecl>>(StringComparer.Ordinal);
        var activated = new Dictionary<string, string>(StringComparer.Ordinal); // &X; được dùng → file dùng đầu tiên
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? ResolveInclude(string declFile, string rel)
        {
            try { return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(declFile) ?? "", rel.Replace('/', '\\'))); }
            catch (Exception) { return null; }
        }

        // Nạp 1 file include: thiếu file thì báo, có thì xử lý nội dung.
        void Include(string declFile, string entityLabel, string rel, bool isDtd, int depth)
        {
            if (ResolveInclude(declFile, rel) is not { } resolved) return;
            var text = Read(resolved, readCache, reader);
            if (text is null)
            {
                if (missingFiles.Add(resolved))
                    issues.Add($"Thiếu file include '{resolved}' (<!ENTITY {entityLabel} SYSTEM> trong {Path.GetFileName(declFile)})");
                return;
            }
            if (depth < MaxIncludeDepth) Process(resolved, text, isDtd, depth + 1);
        }

        // &X; được dùng: mở rộng mọi khai báo của X (include file / các &Y; trong giá trị).
        void Activate(string name, string usedIn, int depth)
        {
            if (Array.IndexOf(BuiltIn, name) >= 0 || activated.ContainsKey(name)) return;
            activated[name] = usedIn;
            if (!generalDecls.TryGetValue(name, out var decls)) return;
            foreach (var d in decls.ToList()) Expand(name, d, depth);
        }

        void Expand(string name, GeneralDecl d, int depth)
        {
            if (d.SystemPath != null) Include(d.DeclFile, name, d.SystemPath, isDtd: false, depth);
            foreach (var r in d.ValueRefs) Activate(r, d.DeclFile, depth);
        }

        void Process(string path, string text, bool isDtdFile, int depth)
        {
            if (!processed.Add(path)) return;

            var body = CdataOrCommentRegex.Replace(text, m => new string(' ', m.Length));

            // 1) Khai báo. Phần khai báo bị che đi khi quét tham chiếu "dùng thật" bên dưới.
            var content = body.ToCharArray();
            foreach (Match d in DeclRegex.Matches(body))
            {
                for (var i = d.Index; i < d.Index + d.Length; i++) content[i] = ' ';

                var name = d.Groups["name"].Value;
                var value = d.Groups["v1"].Success ? d.Groups["v1"].Value : d.Groups["v2"].Value;

                if (d.Groups["p"].Success)
                {
                    declaredParam.Add(name);
                    // % X SYSTEM luôn được nạp: DTD được ráp từ các file dùng chung.
                    if (d.Groups["sys"].Success) Include(path, "% " + name, value, isDtd: true, depth);
                    continue;
                }

                var decl = new GeneralDecl { DeclFile = path };
                if (d.Groups["sys"].Success) decl.SystemPath = value;
                else decl.ValueRefs.AddRange(GeneralRefRegex.Matches(value).Select(m => m.Groups["n"].Value));

                if (!generalDecls.TryGetValue(name, out var list)) generalDecls[name] = list = new List<GeneralDecl>();
                list.Add(decl);
                if (activated.ContainsKey(name)) Expand(name, decl, depth); // đã được dùng từ trước, khai báo đến sau
            }

            // 2) &X; nằm trong nội dung (ngoài khai báo, ngoài CDATA/comment) = "dùng thật".
            var contentText = new string(content);
            foreach (Match m in GeneralRefRegex.Matches(contentText))
                Activate(m.Groups["n"].Value, path, depth);

            // 3) %X; — trong DOCTYPE (file gốc) hoặc cả file nếu nó là file DTD (.ent/.dtd).
            var dtd = isDtdFile || Path.GetExtension(path).Equals(".ent", StringComparison.OrdinalIgnoreCase)
                ? contentText
                : ExtractDoctype(contentText);
            if (dtd.Length > 0)
            {
                var noStrings = QuotedRegex.Replace(dtd, m => new string(' ', m.Length));
                foreach (Match m in ParamRefRegex.Matches(noStrings))
                {
                    var n = m.Groups["n"].Value;
                    if (!paramRefs.ContainsKey(n)) paramRefs[n] = path;
                }
            }
        }

        Process(filePath, mainText, isDtdFile: false, depth: 0);

        var missingEntities = new List<string>();
        foreach (var (name, usedIn) in activated.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (!generalDecls.ContainsKey(name))
                missingEntities.Add($"Thiếu entity &{name}; (dùng trong {Path.GetFileName(usedIn)})");
        foreach (var (name, usedIn) in paramRefs.OrderBy(k => k.Key, StringComparer.Ordinal))
            if (!declaredParam.Contains(name))
                missingEntities.Add($"Thiếu entity %{name}; (dùng trong {Path.GetFileName(usedIn)})");

        // File include bị thiếu kéo theo nhiều entity "thiếu" (đều là hệ quả) — chỉ liệt kê một
        // phần để danh sách còn đọc được, phần còn lại gom thành 1 dòng.
        const int MaxEntityLines = 25;
        issues.AddRange(missingEntities.Take(MaxEntityLines));
        if (missingEntities.Count > MaxEntityLines)
            issues.Add($"... và {missingEntities.Count - MaxEntityLines} entity khác" +
                (missingFiles.Count > 0 ? " (nhiều khả năng do các file include ở trên bị thiếu)" : ""));

        return issues;
    }

    /// <summary>Phần <c>&lt;!DOCTYPE ... [ ... ]&gt;</c> (khai báo entity chứa '&gt;' riêng nên không cắt ở '&gt;' đầu tiên).</summary>
    private static string ExtractDoctype(string text)
    {
        var start = Regex.Match(text, @"<!DOCTYPE\b", RegexOptions.IgnoreCase);
        if (!start.Success) return "";
        var bracket = text.IndexOf('[', start.Index);
        var gt = text.IndexOf('>', start.Index);
        if (bracket >= 0 && (gt < 0 || bracket < gt))
        {
            var close = text.IndexOf("]>", bracket, StringComparison.Ordinal);
            return close < 0 ? text[start.Index..] : text[start.Index..(close + 2)];
        }
        return gt < 0 ? text[start.Index..] : text[start.Index..(gt + 1)];
    }

    private static string? Read(string path, ConcurrentDictionary<string, string?> cache, Func<string, string?>? reader) =>
        cache.GetOrAdd(path, p =>
        {
            if (reader is not null) return reader(p);
            try { return File.Exists(p) ? File.ReadAllText(p) : null; }
            catch (Exception) { return null; } // khoá/rớt share — coi như không đọc được
        });
}
