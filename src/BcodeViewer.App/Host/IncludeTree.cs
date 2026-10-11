using System.Collections.Concurrent;
using System.Text;

namespace BcodeViewer.App.Host;

/// <summary>
/// Đọc file include của controller FBO theo lô / theo cả cây (entity.js: BeginReadFiles, BeginReadIncludeTree) — DÙNG CHUNG cho BcodeViewer (EditorBridge)
/// và Bcode Screen Designer (DesignerBridge, link file này): mỗi bên truyền hàm đọc 1 file của mình (cache đĩa riêng). Sửa ở đây là cả hai ăn theo.
/// Đo SVTran: 584 file / 108 lượt gọi ≈ 1,7s → 1 lượt ≈ 80ms.
/// </summary>
public static class IncludeTree
{
    /// <summary>Đọc song song nhiều file. Trả { đường dẫn: nội dung | null (không đọc được) }.</summary>
    public static IDictionary<string, string?> ReadMany(IEnumerable<string> paths, Func<string, string?> read)
    {
        var results = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        Parallel.ForEach(paths.Distinct(StringComparer.Ordinal), new ParallelOptions { MaxDegreeOfParallelism = 8 }, p =>
        {
            string? text;
            try { text = read(p); }
            catch { text = null; } // include thiếu — Problems panel đã báo
            results[p] = text;
        });
        return results;
    }

    /// <summary>Đọc CẢ CÂY include của 1 file: từ text của file gốc (<paramref name="rootText"/> — bản đang soạn, có thể chưa lưu), theo các khai báo
    /// SYSTEM/PUBLIC, đọc từng tầng song song rồi đi tiếp xuống tầng dưới. Chỉ NẠP TRƯỚC: phía trang vẫn tự quyết thứ tự ưu tiên khai báo,
    /// conditional section... như cũ; khoản đọc thừa (vd. file nằm trong khối IGNORE) chỉ tốn thêm 1 lần đọc. Trả { đường dẫn (cùng dạng entity.js resolvePath): nội dung | null }.</summary>
    public static IDictionary<string, string?> ReadTree(string rootPath, string rootText, Func<string, string?> read)
    {
        const int MaxDepth = 64, MaxFiles = 5000;
        var results = new ConcurrentDictionary<string, string?>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootPath };
        var level = IncludePathsOf(rootPath, rootText, read).Where(seen.Add).ToList();
        for (var depth = 0; depth < MaxDepth && level.Count > 0 && seen.Count < MaxFiles; depth++)
        {
            var texts = ReadMany(level, read);
            var next = new List<string>();
            foreach (var p in level)
            {
                var text = texts[p];
                results[p] = text;
                if (text is null) continue;
                foreach (var child in IncludePathsOf(p, text, read))
                    if (seen.Add(child)) next.Add(child);
            }
            level = next;
        }
        return results;
    }

    private sealed record EntityDecl(string Name, bool IsParam, bool IsSystem, string Quoted);

    /// <summary>Quét khai báo &lt;!ENTITY ...&gt; giống entity.js parseDeclarations (bỏ qua nội dung trong ngoặc kép của khai báo trước).</summary>
    private static List<EntityDecl> ScanEntityDecls(string text)
    {
        var result = new List<EntityDecl>();
        var pos = 0;
        while (true)
        {
            var at = text.IndexOf("<!ENTITY", pos, StringComparison.Ordinal);
            if (at < 0) break;
            var i = at + 8;
            if (i >= text.Length || !char.IsWhiteSpace(text[i])) { pos = at + 1; continue; }
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            var isParam = false;
            if (i < text.Length && text[i] == '%')
            {
                isParam = true;
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            }
            if (i >= text.Length || !(char.IsAsciiLetter(text[i]) || text[i] == '_')) { pos = at + 1; continue; }
            var nameEnd = i + 1;
            while (nameEnd < text.Length && (char.IsAsciiLetterOrDigit(text[nameEnd]) || text[nameEnd] is '_' or '.' or ':' or '$' or '-')) nameEnd++;
            var name = text[i..nameEnd];
            i = nameEnd;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

            var system = false;
            if (IsWord(text, i, "SYSTEM")) { system = true; i += 6; }
            else if (IsWord(text, i, "PUBLIC"))
            {
                i += 6;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                var skipped = ReadQuotedEnd(text, i);
                if (skipped < 0) { pos = at + 1; continue; }
                i = skipped;
                system = true;
            }
            while (system && i < text.Length && char.IsWhiteSpace(text[i])) i++;

            var end = ReadQuotedEnd(text, i);
            if (end < 0) { pos = at + 1; continue; }
            result.Add(new EntityDecl(name, isParam, system, text.Substring(i + 1, end - i - 2)));
            pos = end;
        }
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex ConditionalStart = new(
        @"<!\[\s*(?:%([A-Za-z_][\w.:$-]*);|(INCLUDE|IGNORE))\s*\[",
        System.Text.RegularExpressions.RegexOptions.ECMAScript | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex IncludeWord = new(
        @"^\s*﻿?\s*INCLUDE\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.ECMAScript);

    /// <summary>Đường dẫn các file include (SYSTEM / PUBLIC "id" "đường dẫn") còn HIỆU LỰC trong <paramref name="text"/>: bỏ các khối conditional bị IGNORE
    /// (&lt;![%Tham.so;[ ... ]]&gt; mà file tham số không chứa chữ INCLUDE) giống entity.js applyConditionals, để không nạp thừa cả nhánh .ent bị tắt.</summary>
    public static List<string> IncludePathsOf(string path, string text, Func<string, string?> read)
    {
        var dir = JsDirName(path);
        var decls = ScanEntityDecls(text);
        if (text.Contains("<![", StringComparison.Ordinal))
        {
            var blanked = new StringBuilder(text);
            var pos = 0;
            while (true)
            {
                var m = ConditionalStart.Match(text, pos);
                if (!m.Success) break;
                var depth = 1;
                var i = m.Index + m.Length;
                while (i < text.Length && depth > 0)
                {
                    var open = text.IndexOf("<![", i, StringComparison.Ordinal);
                    var close = text.IndexOf("]]>", i, StringComparison.Ordinal);
                    if (close < 0) { i = text.Length; break; }
                    if (open >= 0 && open < close) { depth++; i = open + 3; } else { depth--; i = close + 3; }
                }
                var end = i;
                bool ignore;
                if (m.Groups[2].Success) ignore = m.Groups[2].Value == "IGNORE";
                else
                {
                    var decl = decls.FirstOrDefault(d => d.IsParam && d.Name == m.Groups[1].Value);
                    var value = "";
                    if (decl is { IsSystem: false }) value = decl.Quoted;
                    else if (decl is not null)
                    {
                        try { value = read(JsResolvePath(dir, decl.Quoted.Replace('/', '\\'))) ?? ""; }
                        catch { value = ""; }
                    }
                    ignore = !IncludeWord.IsMatch(value);
                }
                if (ignore)
                {
                    for (var k = m.Index; k < end && k < blanked.Length; k++)
                        if (blanked[k] != '\n') blanked[k] = ' ';
                    pos = end;
                }
                else pos = m.Index + m.Length; // INCLUDE: giữ nguyên, quét tiếp BÊN TRONG
            }
            decls = ScanEntityDecls(blanked.ToString());
        }
        return decls.Where(d => d.IsSystem).Select(d => JsResolvePath(dir, d.Quoted.Replace('/', '\\'))).ToList();
    }

    private static bool IsWord(string text, int i, string word) =>
        string.Compare(text, i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) == 0
        && (i + word.Length >= text.Length || !(char.IsAsciiLetterOrDigit(text[i + word.Length]) || text[i + word.Length] == '_'));

    /// <summary>Vị trí ngay sau dấu ngoặc kép đóng của chuỗi bắt đầu tại <paramref name="i"/>, hoặc -1.</summary>
    private static int ReadQuotedEnd(string text, int i)
    {
        if (i >= text.Length || (text[i] != '"' && text[i] != '\'')) return -1;
        var end = text.IndexOf(text[i], i + 1);
        return end < 0 ? -1 : end + 1;
    }

    private static string JsDirName(string path)
    {
        var i = Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/'));
        return i >= 0 ? path[..i] : "";
    }

    /// <summary>Y hệt resolvePath trong editor.js (kể cả cách giữ tiền tố UNC), để khoá trả về khớp khoá fileCache phía trang.</summary>
    public static string JsResolvePath(string baseDir, string relative)
    {
        var stack = new List<string>();
        foreach (var part in (baseDir + "\\" + relative).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
            else stack.Add(part);
        }
        return (baseDir.StartsWith("\\\\") ? "\\\\" : "") + string.Join('\\', stack);
    }
}
