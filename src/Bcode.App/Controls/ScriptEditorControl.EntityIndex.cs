using System.Collections.Concurrent;

namespace Bcode.App.Controls;

/// <summary>
/// Chỉ mục entity cho F12 (khung xem / sửa XML). Trước đây MỖI lần F12 đi lại cả cây include từ ổ mạng (Dir\SVTran.xml: 676 file, 1,1 MB) —
/// trung bình 1,6 s, có lần 20 s. Giờ cây include của 1 file được đọc MỘT lần (song song, từng tầng) thành bảng tên → khai báo, F12 sau đó chỉ tra bảng.
///
/// Thứ tự "khai báo nào thắng" giữ y như cách đi cũ: khai báo của chính file trước, rồi lần lượt từng file include (đi sâu hết nhánh này rồi mới tới nhánh sau),
/// trùng tên thì cái gặp trước thắng — nhưng KHÔNG còn giới hạn 4 cấp (vòng include tự chặn bằng tập file đã đi).
///
/// Thứ tự tra cho 1 lần F12 (<see cref="LookupEntity"/>): (1) khai báo ngay trong đoạn chữ đang xem (file chưa lưu / giá trị entity đang peek);
/// (2) chỉ mục của file gốc (basePath); (3) chỉ mục của file nào đã mở mà cây include CHỨA basePath — để F12 trong 1 file .txt/.ent mở ra từ F12
/// (hoặc trong giá trị entity đang peek) vẫn thấy entity khai báo ở file Dir/Grid cha ("entity trong entity trong entity").
///
/// Cũ/mới: nội dung file đọc được nhớ kèm giờ sửa; chỉ mục quá <see cref="RefreshAfter"/> thì lần F12 kế dùng ngay bản đang có và kiểm tra lại ở nền
/// (chỉ đọc lại file đã đổi). Tra không thấy mà chỉ mục đã cũ quá 2 giây thì dựng lại ngay rồi tra lần nữa (vừa thêm khai báo ở tool khác vẫn ra).
///
/// Khối điều kiện &lt;![%Conditional.X;[ … ]]&gt; / &lt;![INCLUDE[ ]]&gt; / &lt;![IGNORE[ ]]&gt;: xử lý y như BcodeViewer (Web/entity.js applyConditionals /
/// lookupParamValue) — khối IGNORE bị bỏ (thay bằng khoảng trắng giữ nguyên độ dài để offset không lệch), nên khai báo đứng sau khối mới là cái có hiệu lực
/// như FBO. Công tắc %X; khai ở chính file thì tra trong file; khai ở file khác thì tra theo cây include của file GỐC (độ sâu 8, khai báo đầu thắng).
/// Trước đây Bcode bỏ qua khối điều kiện nên F12 hay ra khai báo nằm trong khối IGNORE (khác BcodeViewer / FBO).
/// </summary>
public partial class ScriptEditorControl
{
    private sealed class EntityIndex
    {
        public required string Root { get; init; }
        public required string Key { get; init; }
        public required Dictionary<string, ResolvedEntity> ByName { get; init; }
        public required HashSet<string> Files { get; init; }
        public DateTime BuiltAt { get; init; }
        public DateTime UsedAt { get; set; }
    }

    /// <summary>RawDecls = mọi khai báo (kể cả trong khối IGNORE — dùng để đọc trước cả cây include); Decls = sau khi áp khối điều kiện CHỈ bằng công tắc khai
    /// trong chính file (công tắc khai ở file khác coi như IGNORE); NeedsGlobal = có công tắc khai ở file khác → khai báo phụ thuộc file gốc, dựng lại lúc lập chỉ mục.</summary>
    private sealed record CachedFile(DateTime Stamp, string Text, List<EntityDecl> RawDecls, List<EntityDecl> Decls, bool NeedsGlobal);

    private static readonly System.Text.RegularExpressions.Regex ConditionalStartRegex =
        new(@"<!\[\s*(?:%([A-Za-z_][\w.:$-]*);|(INCLUDE|IGNORE))\s*\[", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Chỉ cần nội dung công tắc BẮT ĐẦU bằng INCLUDE: nhiều file công tắc (vd Include\Unit.txt) có thêm rác / ký tự NULL phía sau mà FBO vẫn hiểu là INCLUDE.</summary>
    private static readonly System.Text.RegularExpressions.Regex SwitchIncludeRegex =
        new(@"^[\s﻿\u0000]*INCLUDE(?![A-Za-z0-9_])", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Bản C# của applyConditionals (BcodeViewer Web/entity.js): khối IGNORE → khoảng trắng (giữ '\n' và độ dài), khối INCLUDE giữ nguyên và quét tiếp bên trong.
    /// Công tắc %X; không khai trong <paramref name="text"/> → <paramref name="lookupParam"/> (null = coi là IGNORE) và <paramref name="needsGlobal"/> = true.</summary>
    private static string ApplyConditionals(string text, string path, Func<string, string>? lookupParam, out bool needsGlobal)
    {
        needsGlobal = false;
        if (text.IndexOf("<![", StringComparison.Ordinal) < 0) return text;
        List<EntityDecl>? parms = null;
        char[]? buf = null;
        var m = ConditionalStartRegex.Match(text);
        while (m.Success)
        {
            // Tìm ]]> khớp, tính cả khối lồng nhau.
            int depth = 1, i = m.Index + m.Length;
            while (i < text.Length && depth > 0)
            {
                int open = text.IndexOf("<![", i, StringComparison.Ordinal);
                int close = text.IndexOf("]]>", i, StringComparison.Ordinal);
                if (close < 0) { i = text.Length; break; }
                if (open >= 0 && open < close) { depth++; i = open + 3; } else { depth--; i = close + 3; }
            }
            int end = i;

            bool include;
            if (m.Groups[2].Success) include = m.Groups[2].Value == "INCLUDE";
            else
            {
                var name = m.Groups[1].Value;
                parms ??= ParseEntityDeclarations(text).Where(d => d.IsParam).ToList();
                var idx = parms.FindIndex(d => d.Name == name);
                string value = "";
                if (idx >= 0)
                {
                    var d = parms[idx];
                    value = d.IsSystem
                        ? (IncludePath(path, d) is { } f ? ReadCached(f, restat: false)?.File.Text ?? "" : "")
                        : d.Value ?? "";
                }
                else
                {
                    needsGlobal = true;
                    if (lookupParam is not null) value = lookupParam(name) ?? "";
                }
                include = SwitchIncludeRegex.IsMatch(value);
            }

            if (include) m = ConditionalStartRegex.Match(text, m.Index + m.Length);
            else
            {
                buf ??= text.ToCharArray();
                for (int k = m.Index; k < end; k++) if (buf[k] != '\n') buf[k] = ' ';
                m = end < text.Length ? ConditionalStartRegex.Match(text, end) : System.Text.RegularExpressions.Match.Empty;
            }
        }
        return buf is null ? text : new string(buf);
    }

    private static readonly TimeSpan RefreshAfter = TimeSpan.FromSeconds(20);
    private static readonly ConcurrentDictionary<string, CachedFile> FileCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<Task<EntityIndex>>> Indexes = new(StringComparer.Ordinal);
    private const int MaxIndexes = 40;

    /// <summary>Dựng sẵn chỉ mục ở nền khi vừa mở 1 file (để F12 đầu tiên cũng tức thì).</summary>
    internal static void PrewarmEntityIndex(string? basePath, string? text)
    {
        if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(text) || text.IndexOf("<!ENTITY", StringComparison.OrdinalIgnoreCase) < 0) return;
        _ = GetIndexAsync(basePath, text, forceFresh: false);
    }

    private static string IndexKey(string basePath, string text)
    {
        var (_, prog) = ProjectRoots?.Invoke() ?? default;
        return basePath.ToLowerInvariant() + "|" + (prog ?? "").Trim().ToLowerInvariant() + "|" + text.Length + ":" + string.GetHashCode(text.AsSpan());
    }

    private static Task<EntityIndex> GetIndexAsync(string basePath, string text, bool forceFresh)
    {
        var key = IndexKey(basePath, text);
        if (!forceFresh && Indexes.TryGetValue(key, out var lazy)) return lazy.Value;
        var fresh = new Lazy<Task<EntityIndex>>(() => Task.Run(() => Build(basePath, text, key)));
        Indexes[key] = fresh;
        if (Indexes.Count > MaxIndexes) Trim();
        return fresh.Value;
    }

    private static void Trim()
    {
        var done = Indexes.Where(kv => kv.Value.IsValueCreated && kv.Value.Value.IsCompletedSuccessfully)
            .OrderBy(kv => kv.Value.Value.Result.UsedAt).Take(Indexes.Count - MaxIndexes).Select(kv => kv.Key).ToList();
        foreach (var k in done) Indexes.TryRemove(k, out _);
    }

    /// <summary>Đọc 1 file (dùng bản nhớ nếu giờ sửa không đổi); không có trong source thì lấy bản ở Program Path. null = không đọc được.</summary>
    private static (string Path, CachedFile File)? ReadCached(string path, bool restat)
    {
        string? actual = File.Exists(path) ? path : ProgramFallback(path);
        if (actual is null) return null;
        try
        {
            if (!restat && FileCache.TryGetValue(actual, out var hit)) return (actual, hit);
            var stamp = File.GetLastWriteTimeUtc(actual);
            if (FileCache.TryGetValue(actual, out var c) && c.Stamp == stamp) return (actual, c);
            var text = File.ReadAllText(actual);
            var raw = ParseEntityDeclarations(text);
            var applied = ApplyConditionals(text, actual, null, out var needsGlobal);
            var entry = new CachedFile(stamp, text, raw, ReferenceEquals(applied, text) ? raw : ParseEntityDeclarations(applied), needsGlobal);
            // Giới hạn bộ nhớ: quá ~4000 file (vài chục MB) thì bỏ hết bản nhớ — chỉ mục đã dựng vẫn giữ khai báo của nó, lần dựng sau đọc lại từ đĩa.
            if (FileCache.Count > 4000) FileCache.Clear();
            FileCache[actual] = entry;
            return (actual, entry);
        }
        catch { return null; }
    }

    private static string? IncludePath(string fromFile, EntityDecl d)
    {
        if (!d.IsSystem || d.SystemPath is null) return null;
        try { return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fromFile)!, d.SystemPath.Replace('/', '\\'))); }
        catch { return null; }
    }

    private static EntityIndex Build(string basePath, string rootText, string key)
    {
        var restat = FileCache.Count > 0;   // lần dựng lại: kiểm tra giờ sửa (chỉ đọc lại file đã đổi)
        // 1) Đọc cả cây include, song song theo từng tầng (ổ mạng: đọc nối tiếp 676 file mất ~2 s, song song nhanh hơn nhiều).
        var texts = new ConcurrentDictionary<string, (string Path, CachedFile File)?>(StringComparer.OrdinalIgnoreCase);
        // Đọc theo khai báo THÔ (cả include nằm trong khối điều kiện): khối nào có hiệu lực chỉ biết sau khi tra công tắc, đọc thừa vài file không sao.
        var rootRaw = ParseEntityDeclarations(rootText);
        var frontier = rootRaw.Select(d => IncludePath(basePath, d)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        while (frontier.Count > 0)
        {
            var next = new ConcurrentBag<string>();
            Parallel.ForEach(frontier, new ParallelOptions { MaxDegreeOfParallelism = 16 }, p =>
            {
                if (!texts.TryAdd(p, null)) return;
                var r = ReadCached(p, restat);
                texts[p] = r;
                if (r is null) return;
                foreach (var d in r.Value.File.RawDecls)
                    if (IncludePath(r.Value.Path, d) is { } inc && !texts.ContainsKey(inc)) next.Add(inc);
            });
            frontier = next.Distinct(StringComparer.OrdinalIgnoreCase).Where(p => !texts.ContainsKey(p)).ToList();
        }

        // 2) Công tắc %X; khai ở FILE KHÁC: tra theo cây include của file gốc như lookupParamValue của BcodeViewer — mỗi file chỉ xét khai báo đã áp
        //    công tắc của chính nó (Decls), khai báo tham số đầu tiên gặp được thắng, độ sâu 8. Nhớ theo tên trong lần dựng này.
        var rootNoGlobal = ParseEntityDeclarations(ApplyConditionals(rootText, basePath, null, out var rootNeedsGlobal));
        var paramMemo = new Dictionary<string, string>(StringComparer.Ordinal);
        string LookupParam(string name)
        {
            if (paramMemo.TryGetValue(name, out var v)) return v;
            var seenP = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { basePath };
            string? WalkParam(string path, List<EntityDecl> decls, int depth)
            {
                foreach (var d in decls)
                    if (d.IsParam && d.Name == name)
                        return d.IsSystem
                            ? (IncludePath(path, d) is { } f ? ReadCached(f, restat: false)?.File.Text ?? "" : "")
                            : d.Value ?? "";
                if (depth <= 0) return null;
                foreach (var d in decls)
                {
                    if (!d.IsSystem || IncludePath(path, d) is not { } inc || !seenP.Add(inc)) continue;
                    var r = texts.TryGetValue(inc, out var t) ? t : ReadCached(inc, restat: false);
                    if (r is null) continue;
                    if (WalkParam(r.Value.Path, r.Value.File.Decls, depth - 1) is { } hit) return hit;
                }
                return null;
            }
            v = WalkParam(basePath, rootNoGlobal, 8) ?? "";
            paramMemo[name] = v;
            return v;
        }
        var rootDecls = rootNeedsGlobal ? ParseEntityDeclarations(ApplyConditionals(rootText, basePath, LookupParam, out _)) : rootNoGlobal;

        // 3) Dựng bảng theo đúng thứ tự cũ: file trước, rồi từng include đi sâu; trùng tên → cái gặp trước thắng.
        var byName = new Dictionary<string, ResolvedEntity>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFullPath(basePath) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { basePath };
        void Walk(string path, string text, List<EntityDecl> decls)
        {
            foreach (var d in decls) byName.TryAdd(d.Name, new ResolvedEntity(d, path, text));
            foreach (var d in decls)
            {
                if (IncludePath(path, d) is not { } inc || !seen.Add(inc)) continue;
                if (!texts.TryGetValue(inc, out var r) || r is null) continue;
                var (actual, f) = r.Value;
                files.Add(inc); files.Add(actual);
                if (!actual.Equals(inc, StringComparison.OrdinalIgnoreCase) && !seen.Add(actual)) continue;
                Walk(actual, f.Text, f.NeedsGlobal ? ParseEntityDeclarations(ApplyConditionals(f.Text, actual, LookupParam, out _)) : f.Decls);
            }
        }
        Walk(basePath, rootText, rootDecls);
        var now = DateTime.UtcNow;
        return new EntityIndex { Root = basePath, Key = key, ByName = byName, Files = files, BuiltAt = now, UsedAt = now };
    }

    /// <summary>Tra 1 entity cho F12 — xem thứ tự tra ở đầu file.</summary>
    private static ResolvedEntity? LookupEntity(string name, string basePath, string text)
    {
        if (BuiltinEntities.Contains(name)) return null;   // &lt; &amp;... của XML: không có khai báo — khỏi dựng lại chỉ mục vô ích
        // (1)+(2) chỉ mục dựng từ đoạn đang xem: khai báo của chính đoạn này đứng đầu bảng (đã áp khối điều kiện — trước đây tra thô ở đây nên ra cả khai báo
        // trong khối IGNORE), rồi tới cây include. Đoạn đang xem không có khai báo nào (giá trị entity đang peek) → chỉ mục dựng từ nội dung file trên đĩa.
        var rootText = text;
        var onDisk = ReadCached(basePath, restat: false);
        if (onDisk is { } od && !ReferenceEquals(od.File.Text, text) && ParseEntityDeclarations(text).Count == 0 && od.File.RawDecls.Count > 0) rootText = od.File.Text;
        EntityIndex idx;
        try { idx = GetIndexAsync(basePath, rootText, forceFresh: false).GetAwaiter().GetResult(); }
        catch { return null; }
        idx.UsedAt = DateTime.UtcNow;
        if (DateTime.UtcNow - idx.BuiltAt > RefreshAfter) _ = Task.Run(() => GetIndexAsync(basePath, rootText, forceFresh: true));
        if (idx.ByName.TryGetValue(name, out var hit)) return hit;

        // (3) file đang xem nằm trong cây include của 1 file đã mở trước đó (mở ra từ F12) → tra ở file cha
        var full = SafeFull(basePath);
        foreach (var other in Indexes.Values.Where(l => l.IsValueCreated && l.Value.IsCompletedSuccessfully).Select(l => l.Value.Result)
                     .Where(i => !ReferenceEquals(i, idx) && i.Files.Contains(full)).OrderByDescending(i => i.UsedAt))
            if (other.ByName.TryGetValue(name, out var parentHit)) { other.UsedAt = DateTime.UtcNow; return parentHit; }

        // Không thấy mà chỉ mục đã cũ (vừa thêm khai báo ở tool khác) → dựng lại ngay rồi tra lần nữa.
        if (DateTime.UtcNow - idx.BuiltAt > TimeSpan.FromSeconds(2))
        {
            try
            {
                var fresh = GetIndexAsync(basePath, rootText, forceFresh: true).GetAwaiter().GetResult();
                if (fresh.ByName.TryGetValue(name, out var again)) return again;
            }
            catch { }
        }
        return null;
    }

    private static string SafeFull(string p) { try { return Path.GetFullPath(p); } catch { return p; } }
}
