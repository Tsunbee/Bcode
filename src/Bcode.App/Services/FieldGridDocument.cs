using System.Text;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

// Lưới khai field kiểu FSD Studio ("chế độ triển khai"): đọc <fields> / <title> / <views> của file Dir / Grid / Filter / Report thành dòng,
// sửa trên lưới thì ghi ngược vào CHUỖI gốc tại đúng vị trí (giữ định dạng, comment, thứ tự thuộc tính, DOCTYPE, entity) — như FSD Studio
// (clsCustomize / FValue: sửa chuỗi tại chỗ, không serializer) nhưng khác ở: thuộc tính chỉ tìm trong THẺ MỞ <field ...> (FSD tìm cả khối nên
// bắt nhầm thuộc tính của <items>), field nằm trong &X; (file SYSTEM) cũng hiện và sửa được — ghi vào file include đó, giữ encoding / BOM gốc.

/// <summary>Một nút của cây XML đọc lỏng (giữ vị trí ký tự trong chuỗi gốc). Name "&amp;" = tham chiếu entity &amp;X; ở phần nội dung.</summary>
internal sealed class FgNode
{
    public string Name = "";
    public string? Entity;
    /// <summary>Start = '&lt;' thẻ mở; StartTagEnd = sau '&gt;' thẻ mở; ContentEnd = '&lt;' thẻ đóng; End = sau thẻ đóng.</summary>
    public int Start, StartTagEnd, ContentEnd, End;
    public bool SelfClosing;
    public readonly List<FgAttr> Attrs = new();
    public readonly List<FgNode> Children = new();
    public FgAttr? Attr(string name) => Attrs.Find(a => a.Name == name);
    public FgNode? Child(string name) => Children.Find(c => c.Name == name);
}

internal sealed class FgAttr
{
    public string Name = "";
    public int Start, End, ValueStart, ValueEnd;
}

/// <summary>Bộ đọc XML lỏng chỉ để lấy vị trí: bỏ qua comment, CDATA, &lt;?..?&gt;, DOCTYPE (cả phần [ ... ] trong); không kiểm tính hợp lệ.</summary>
internal static class FgXml
{
    private static readonly HashSet<string> Predefined = new(StringComparer.Ordinal) { "lt", "gt", "amp", "quot", "apos" };

    public static List<FgNode> Parse(string s)
    {
        var list = new List<FgNode>();
        ParseContent(s, 0, list, null);
        return list;
    }

    private static int ParseContent(string s, int i, List<FgNode> output, FgNode? parent)
    {
        var n = s.Length;
        while (i < n)
        {
            var c = s[i];
            if (c == '<')
            {
                if (At(s, i, "<!--")) { i = Skip(s, i + 4, "-->"); continue; }
                if (At(s, i, "<![CDATA[")) { i = Skip(s, i + 9, "]]>"); continue; }
                if (At(s, i, "<?")) { i = Skip(s, i + 2, "?>"); continue; }
                if (At(s, i, "<!DOCTYPE")) { i = SkipDoctype(s, i); continue; }
                if (At(s, i, "<!")) { i = Skip(s, i + 2, ">"); continue; }
                if (i + 1 < n && s[i + 1] == '/')
                {
                    var gt = s.IndexOf('>', i);
                    var end = gt < 0 ? n : gt + 1;
                    if (parent is not null) { parent.ContentEnd = i; parent.End = end; return end; }
                    i = end;   // thẻ đóng lạc ở mức ngoài cùng (mảnh include) — bỏ qua
                    continue;
                }
                var node = ParseStartTag(s, i);
                if (node is null) { i++; continue; }
                output.Add(node);
                if (node.SelfClosing) { node.ContentEnd = node.End = node.StartTagEnd; i = node.End; continue; }
                i = ParseContent(s, node.StartTagEnd, node.Children, node);
                if (node.End == 0) { node.ContentEnd = node.End = n; }
                continue;
            }
            if (c == '&')
            {
                var k = i + 1;
                while (k < n && (char.IsLetterOrDigit(s[k]) || s[k] is '_' or '.' or '$' or ':' or '-')) k++;
                if (k < n && s[k] == ';' && k > i + 1)
                {
                    var name = s[(i + 1)..k];
                    if (!Predefined.Contains(name))
                        output.Add(new FgNode { Name = "&", Entity = name, Start = i, StartTagEnd = k + 1, ContentEnd = k + 1, End = k + 1, SelfClosing = true });
                    i = k + 1;
                    continue;
                }
            }
            i++;
        }
        return n;
    }

    private static FgNode? ParseStartTag(string s, int i)
    {
        var n = s.Length;
        var k = i + 1;
        while (k < n && !char.IsWhiteSpace(s[k]) && s[k] != '>' && s[k] != '/') k++;
        if (k == i + 1) return null;
        var node = new FgNode { Name = s[(i + 1)..k], Start = i };
        while (k < n)
        {
            while (k < n && char.IsWhiteSpace(s[k])) k++;
            if (k >= n) break;
            if (s[k] == '>') { node.StartTagEnd = k + 1; return node; }
            if (s[k] == '/' && k + 1 < n && s[k + 1] == '>') { node.SelfClosing = true; node.StartTagEnd = k + 2; return node; }
            var a0 = k;
            while (k < n && !char.IsWhiteSpace(s[k]) && s[k] != '=' && s[k] != '>' && s[k] != '/') k++;
            var aName = s[a0..k];
            while (k < n && char.IsWhiteSpace(s[k])) k++;
            if (k >= n || s[k] != '=') { if (aName.Length == 0) k++; continue; }
            k++;
            while (k < n && char.IsWhiteSpace(s[k])) k++;
            if (k >= n || (s[k] != '"' && s[k] != '\'')) continue;
            var q = s[k];
            var close = s.IndexOf(q, k + 1);
            if (close < 0) break;
            node.Attrs.Add(new FgAttr { Name = aName, Start = a0, ValueStart = k + 1, ValueEnd = close, End = close + 1 });
            k = close + 1;
        }
        var gt = s.IndexOf('>', i);
        node.StartTagEnd = gt < 0 ? n : gt + 1;
        node.SelfClosing = gt > 0 && s[gt - 1] == '/';
        return node;
    }

    private static bool At(string s, int i, string what) => string.CompareOrdinal(s, i, what, 0, what.Length) == 0;

    private static int Skip(string s, int from, string end)
    {
        var e = s.IndexOf(end, from, StringComparison.Ordinal);
        return e < 0 ? s.Length : e + end.Length;
    }

    /// <summary>Hết &lt;!DOCTYPE x [ ... ]&gt;: trong phần [ ] bỏ qua comment và từng khai báo &lt;!...&gt; (giá trị entity có thể chứa ]&gt;).</summary>
    public static int SkipDoctype(string s, int i)
    {
        var n = s.Length;
        var k = i + 9;
        while (k < n && s[k] != '[' && s[k] != '>')
        {
            if (s[k] is '"' or '\'') { var e = s.IndexOf(s[k], k + 1); k = e < 0 ? n : e + 1; continue; }
            k++;
        }
        if (k >= n) return n;
        if (s[k] == '>') return k + 1;
        k++;
        while (k < n)
        {
            if (At(s, k, "<!--")) { k = Skip(s, k + 4, "-->"); continue; }
            if (At(s, k, "<!"))
            {
                var q = '\0';
                var j = k + 2;
                for (; j < n; j++)
                {
                    var ch = s[j];
                    if (q != '\0') { if (ch == q) q = '\0'; }
                    else if (ch is '"' or '\'') q = ch;
                    else if (ch == '>') break;
                }
                k = j + 1;
                continue;
            }
            if (s[k] == ']')
            {
                var gt = s.IndexOf('>', k);
                return gt < 0 ? n : gt + 1;
            }
            k++;
        }
        return n;
    }
}

/// <summary>Một file thật trên đĩa (file chính hoặc file include): chuỗi đang sửa + bản trên đĩa + encoding / BOM gốc để ghi lại y vậy.</summary>
internal sealed class FgFile
{
    public string Path = "";
    public string Text = "";
    public string DiskText = "";
    public Encoding Encoding = new UTF8Encoding(false);
    public byte[] Preamble = Array.Empty<byte>();
    public DateTime DiskTime;
    /// <summary>Khác null = không cho sửa (mã hoá, kho source, Read-only, bảng mã lạ, giá trị entity literal…).</summary>
    public string? ReadOnlyReason;
    /// <summary>File ảo = giá trị &lt;!ENTITY X "..."&gt; literal (không có trên đĩa).</summary>
    public bool Virtual;
    private string? _parsedText;
    private List<FgNode>? _nodes;

    public bool Dirty => !Virtual && !string.Equals(Text, DiskText, StringComparison.Ordinal);
    public string NewLine => Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    public List<FgNode> Nodes()
    {
        if (_nodes is null || !ReferenceEquals(_parsedText, Text)) { _nodes = FgXml.Parse(Text); _parsedText = Text; }
        return _nodes;
    }

    public static FgFile Load(string path)
    {
        var f = new FgFile { Path = path };
        f.Reload();
        return f;
    }

    public void Reload()
    {
        var bytes = File.ReadAllBytes(Path);
        DiskTime = File.GetLastWriteTimeUtc(Path);
        ReadOnlyReason = null;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        { Encoding = new UTF8Encoding(false); Preamble = bytes[..3]; Text = Encoding.GetString(bytes, 3, bytes.Length - 3); }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        { Encoding = new UnicodeEncoding(false, false); Preamble = bytes[..2]; Text = Encoding.GetString(bytes, 2, bytes.Length - 2); }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        { Encoding = new UnicodeEncoding(true, false); Preamble = bytes[..2]; Text = Encoding.GetString(bytes, 2, bytes.Length - 2); }
        else
        {
            Preamble = Array.Empty<byte>();
            Encoding = new UTF8Encoding(false);
            try { Text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                Text = Encoding.GetString(bytes);
                ReadOnlyReason = "File không phải UTF-8 / UTF-16 (bảng mã lạ) — chỉ xem, không ghi lại để khỏi hỏng chữ.";
            }
        }
        DiskText = Text;
        // Thuộc tính Read-only (bản cài của khách hay đặt cho .f): như FSD Studio — vẫn sửa, lúc lưu tạm gỡ rồi đặt lại (FgSet.Save).
        ReadOnlyAttr = File.GetAttributes(Path).HasFlag(FileAttributes.ReadOnly);
        // Khối <Encrypted> (mã hoá theo license máy của Fast): KHÔNG khoá cả file — lưới chỉ sửa thuộc tính / header / thứ tự, khối mã hoá giữ
        // nguyên từng ký tự (như FSD Studio); field chứa <Encrypted> thì khung XML của nó chỉ xem (FgField.Encrypted).
        HasEncrypted = Text.Contains("<Encrypted>", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>File có khối &lt;Encrypted&gt; ở đâu đó (script / command…) — chỉ để báo.</summary>
    public bool HasEncrypted;

    /// <summary>File đặt thuộc tính Read-only lúc nạp — lưu thì tạm gỡ rồi đặt lại.</summary>
    public bool ReadOnlyAttr;

    public byte[] Bytes()
    {
        // File nguồn Fast (.xml/.f/.ent): ghi như FCode — UTF-8 có BOM + <?xml ... encoding="utf-8"?>. Thiếu BOM thì Fast biên dịch .xml → .f
        // ra chữ Trung Quốc (Bcode.Core.FastFileEncoding). File khác (mảnh include .txt…) giữ bảng mã / BOM như lúc đọc.
        if (Bcode.Core.FastFileEncoding.IsFastSource(Path))
        {
            var utf8 = Bcode.Core.FastFileEncoding.Utf8Bom;
            return [.. utf8.GetPreamble(), .. utf8.GetBytes(Bcode.Core.FastFileEncoding.NormalizeDeclaration(Text))];
        }
        var body = Encoding.GetBytes(Text);
        if (Preamble.Length == 0) return body;
        var all = new byte[Preamble.Length + body.Length];
        Preamble.CopyTo(all, 0);
        body.CopyTo(all, Preamble.Length);
        return all;
    }
}

/// <summary>Một dòng của lưới Fields: &lt;field&gt; trong file chính hoặc trong file include (<see cref="Via"/> = chuỗi &amp;X; dẫn tới).</summary>
internal sealed class FgField
{
    public required FgFile File;
    public required FgNode Node;
    /// <summary>Danh sách mục cùng cấp chứa field (để đổi thứ tự / chèn): con của &lt;fields&gt; hoặc cấp ngoài cùng của file include.</summary>
    public required List<FgNode> Siblings;
    /// <summary>Mục ở cấp &lt;fields&gt; của file chính sinh ra dòng này (chính nó, hoặc &amp;X; chứa nó).</summary>
    public required FgNode TopItem;
    public string? Via;
    /// <summary>Dòng giả cho &amp;X; không đọc được.</summary>
    public string? Placeholder;
    public string? ReadOnlyReason;
    /// <summary>Field có khối &lt;Encrypted&gt; bên trong (clientScript…): sửa thuộc tính / header / chuyển / xoá được, khung XML chỉ xem.</summary>
    public bool Encrypted;

    public string Name => Placeholder ?? Get("name") ?? "";
    public string? Get(string attr) => Node.Attr(attr) is { } a ? File.Text[a.ValueStart..a.ValueEnd] : null;
    public string? Sub(string child, string attr) => Node.Child(child)?.Attr(attr) is { } a ? File.Text[a.ValueStart..a.ValueEnd] : null;
    public string Xml => Placeholder is null ? File.Text[Node.Start..Node.End] : "";
}

/// <summary>Một dòng của tab Views: &lt;field name&gt; (Grid) hoặc &lt;item value&gt; (Dir / Filter) trong &lt;view&gt; / &lt;category&gt;.</summary>
internal sealed class FgViewItem
{
    public required FgFile File;
    public required FgNode Node;
    public required List<FgNode> Siblings;
    public string Context = "";
    public string? Via;
    public string? ReadOnlyReason;
    public string KeyAttr => Node.Name == "field" ? "name" : "value";
    public string Value => Node.Name == "&" ? "&" + Node.Entity + ";" : Node.Attr(KeyAttr) is { } a ? File.Text[a.ValueStart..a.ValueEnd] : "";
}

/// <summary>Một tab của lưới (1 file Dir / Grid / Filter / Report).</summary>
internal sealed class FgDoc
{
    public required FgSet Set;
    public required FgFile Main;
    /// <summary>Thư mục trong Controllers: Dir / Grid / Filter / Report / Lookup … ("" = file lẻ).</summary>
    public string Kind = "";
    public FgNode? Root, Title, SubTitle, FieldsNode, Views;
    /// <summary>File chứa thẻ tương ứng: thường là <see cref="Main"/>; khác khi cả phần thân nằm trong entity ngay dưới thẻ gốc
    /// (Grid\CustomerParameterDetail.f = &lt;grid&gt;&amp;BIParameterGrid;&lt;/grid&gt; → title / fields / views ở Include\XML\BIParameterGrid.txt).</summary>
    public FgFile? TitleFile, SubTitleFile, FieldsFile, ViewsFile;
    /// <summary>Chuỗi &amp;X; dẫn tới &lt;fields&gt; / &lt;views&gt; khi chúng nằm trong file include (null = trong file chính).</summary>
    public string? FieldsVia, ViewsVia;
    public readonly List<FgField> Fields = new();
    public readonly List<FgViewItem> ViewItems = new();
    public readonly List<string> Problems = new();
    public Dictionary<string, (string? Literal, string? Path)> Entities = new();
    public string TabTitle => $"[{(Kind.Length > 0 ? Kind : "File")}] {Path.GetFileName(Main.Path)}";

    public void LoadEntities()
    {
        try { Entities = DtdIncludeResolver.GeneralEntities(Main.Path, Main.Text, p => Set.ReadRaw(p)); }
        catch (Exception) { Entities = new(); }
    }

    public void Reparse()
    {
        Fields.Clear(); ViewItems.Clear(); Problems.Clear();
        var nodes = Main.Nodes();
        Root = nodes.FirstOrDefault(x => x.Name != "&");
        (TitleFile, Title, _) = FindPart("title");
        (SubTitleFile, SubTitle, _) = FindPart("subTitle");
        (FieldsFile, FieldsNode, FieldsVia) = FindPart("fields");
        (ViewsFile, Views, ViewsVia) = FindPart("views");
        if (FieldsNode is not null) WalkFields(FieldsFile!, FieldsNode.Children, FieldsVia, null, 0);
        if (Views is not null) WalkViews(ViewsFile!, Views.Children, "views", "", ViewsVia, 0);
    }

    /// <summary>Thẻ con <paramref name="name"/> của thẻ gốc; không có thì tìm trong các &amp;X; đứng ngay dưới thẻ gốc (và &amp;Y; ở cấp ngoài cùng
    /// của file đó, tối đa 6 lớp).</summary>
    private (FgFile? File, FgNode? Node, string? Via) FindPart(string name)
    {
        if (Root is null) return (null, null, null);
        var queue = new Queue<(FgFile File, List<FgNode> Items, string? Via, int Depth)>();
        queue.Enqueue((Main, Root.Children, null, 0));
        while (queue.Count > 0)
        {
            var (file, items, via, depth) = queue.Dequeue();
            if (items.Find(x => x.Name == name) is { } hit) return (file, hit, via);
            if (depth >= 6) continue;
            foreach (var e in items)
                if (e.Name == "&" && e.Entity is { } ent && ResolveEntity(ent) is { } inc)
                    queue.Enqueue((inc, inc.Nodes(), (via ?? "") + "&" + ent + ";", depth + 1));
        }
        return (null, null, null);
    }

    private void WalkFields(FgFile file, List<FgNode> items, string? via, FgNode? top, int depth)
    {
        foreach (var item in items)
        {
            if (item.Name == "field")
            {
                var f = new FgField { File = file, Node = item, Siblings = items, TopItem = top ?? item, Via = via };
                f.ReadOnlyReason = file.ReadOnlyReason;
                f.Encrypted = f.Xml.Contains("<Encrypted>", StringComparison.OrdinalIgnoreCase);
                Fields.Add(f);
            }
            else if (item.Name == "&" && item.Entity is { } ent)
            {
                var chain = (via ?? "") + "&" + ent + ";";
                var inc = depth < 12 ? ResolveEntity(ent) : null;
                if (inc is null)
                {
                    Problems.Add($"&{ent}; trong <fields> không đọc được (chưa khai báo hoặc thiếu file).");
                    Fields.Add(new FgField { File = file, Node = item, Siblings = items, TopItem = top ?? item, Via = via, Placeholder = "&" + ent + ";",
                        ReadOnlyReason = "Không đọc được entity &" + ent + "; — sửa ở file XML." });
                    continue;
                }
                WalkFields(inc, inc.Nodes(), chain, top ?? item, depth + 1);
            }
        }
    }

    /// <summary>Dòng của &lt;views&gt;: &lt;field&gt; / &lt;item&gt; trong &lt;view&gt; / &lt;category&gt;. &amp;X; ở bất kỳ cấp nào (cả &lt;views&gt;&amp;FlowMultiFormView;&lt;/views&gt;
    /// mà file include chứa nguyên &lt;view&gt;) được mở ra — dòng bên trong sửa vào file include; không đọc được thì 1 dòng chỉ xem.</summary>
    private void WalkViews(FgFile file, List<FgNode> items, string parentName, string ctx, string? via, int depth)
    {
        foreach (var item in items)
        {
            if (item.Name is "view" or "category")
            {
                var c = item.Name + (item.Attr("id") is { } id ? " " + file.Text[id.ValueStart..id.ValueEnd]
                    : item.Attr("index") is { } ix ? " " + file.Text[ix.ValueStart..ix.ValueEnd] : "");
                WalkViews(file, item.Children, item.Name, c, via, depth);
                continue;
            }
            if (item.Name == "&" && item.Entity is { } ent)
            {
                var inc = depth < 8 ? ResolveEntity(ent) : null;
                if (inc is not null) { WalkViews(inc, inc.Nodes(), parentName, ctx, (via ?? "") + "&" + ent + ";", depth + 1); continue; }
                if (parentName is "view" or "category" or "views")
                    ViewItems.Add(new FgViewItem { File = file, Node = item, Siblings = items, Context = ctx, Via = via,
                        ReadOnlyReason = "Không đọc được entity &" + ent + "; — sửa ở file XML." });
                continue;
            }
            if (parentName is not ("view" or "category") || item.Name is not ("field" or "item")) continue;
            ViewItems.Add(new FgViewItem { File = file, Node = item, Siblings = items, Context = ctx, Via = via,
                ReadOnlyReason = file.ReadOnlyReason ?? (file.Text[item.Start..item.End].Contains("<Encrypted>", StringComparison.OrdinalIgnoreCase) ? "Dòng có khối <Encrypted> — không sửa." : null) });
        }
    }

    /// <summary>&amp;X; → file include (SYSTEM) hoặc file ảo chỉ đọc (giá trị literal).</summary>
    private FgFile? ResolveEntity(string name)
    {
        if (!Entities.TryGetValue(name, out var e)) return null;
        if (e.Path is not null) return Set.GetFile(e.Path);
        if (e.Literal is null) return null;
        return Set.LiteralFile(name, e.Literal);
    }
}

/// <summary>Bộ file của 1 chứng từ (tab Dir / Grid / Filter / Report cùng SysID) + bộ đệm file dùng chung (file include sửa ở 1 tab thì tab khác thấy ngay),
/// thao tác sửa chuỗi, Undo, Lưu.</summary>
internal sealed class FgSet
{
    public static readonly string[] Folders = { "Dir", "Filter", "Grid", "Report", "Lookup" };
    private static readonly Regex GridItemsRegex = new(@"<items\b[^>]*\bstyle\s*=\s*""Grid""[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ControllerRegex = new(@"\bcontroller\s*=\s*""([^""&]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ShowFormRegex = new(@"showForm\s*\(\s*['""]([A-Za-z0-9_$]+)['""]", RegexOptions.Compiled);

    private readonly Dictionary<string, FgFile?> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FgFile> _literals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _raw = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<(FgFile File, string Text)> _undo = new();
    private readonly List<string> _readOnlyRoots;

    public string ControllersRoot = "";
    public string SysId = "";
    public readonly List<FgDoc> Docs = new();

    public FgSet(IEnumerable<string>? readOnlyRoots = null) => _readOnlyRoots = readOnlyRoots?.Where(r => !string.IsNullOrWhiteSpace(r)).ToList() ?? new();

    public IEnumerable<FgFile> AllFiles => _files.Values.Where(f => f is not null)!;
    public bool Dirty => AllFiles.Any(f => f.Dirty);
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Đọc chữ thô (cho DtdIncludeResolver — .ent khai báo), nhớ theo lượt mở.</summary>
    public string? ReadRaw(string path)
    {
        if (_raw.TryGetValue(path, out var t)) return t;
        try { t = File.Exists(path) ? File.ReadAllText(path) : null; } catch (Exception) { t = null; }
        return _raw[path] = t;
    }

    public FgFile? GetFile(string path)
    {
        if (_files.TryGetValue(path, out var f)) return f;
        try { f = File.Exists(path) ? FgFile.Load(path) : null; } catch (Exception) { f = null; }
        if (f is not null && f.ReadOnlyReason is null && InReadOnlyRoot(path)) f.ReadOnlyReason = "File nằm trong kho source (chỉ đọc) — Bcode không ghi đè.";
        return _files[path] = f;
    }

    public FgFile LiteralFile(string entity, string literal)
    {
        if (_literals.TryGetValue(entity, out var f)) return f;
        f = new FgFile { Path = "&" + entity + ";", Text = literal, DiskText = literal, Virtual = true,
            ReadOnlyReason = "Field nằm trong giá trị <!ENTITY " + entity + " \"...\"> khai ngay ở DOCTYPE — sửa ở file XML." };
        return _literals[entity] = f;
    }

    private bool InReadOnlyRoot(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return _readOnlyRoots.Any(r => full.StartsWith(Path.GetFullPath(r).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    public FgDoc AddDoc(string path, string kind)
    {
        var existing = Docs.FirstOrDefault(d => string.Equals(d.Main.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;
        var file = GetFile(path) ?? throw new FileNotFoundException("Không đọc được file", path);
        var doc = new FgDoc { Set = this, Main = file, Kind = kind };
        doc.LoadEntities();
        doc.Reparse();
        Docs.Add(doc);
        return doc;
    }

    public void ReparseAll() { foreach (var d in Docs) d.Reparse(); }

    /// <summary>Thư mục Controllers chứa file (cha của Dir / Grid / Filter …) và loại file; không nằm trong các thư mục đó → (null, "").</summary>
    public static (string? Root, string Kind) Locate(string path)
    {
        var dir = Path.GetDirectoryName(path);
        var name = Path.GetFileName(dir ?? "");
        var kind = Folders.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        return kind is null ? (null, "") : (Path.GetDirectoryName(dir), kind);
    }

    /// <summary>File của loại <paramref name="kind"/> tên <paramref name="name"/>: ưu tiên file người dùng bấm, rồi .xml, rồi .f (như FSD Studio).</summary>
    public static string? Pick(string root, string kind, string name, string? preferred)
    {
        if (preferred is not null && Locate(preferred) is { Root: { } r, Kind: var k }
            && string.Equals(k, kind, StringComparison.OrdinalIgnoreCase) && string.Equals(r, root, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileNameWithoutExtension(preferred), name, StringComparison.OrdinalIgnoreCase) && File.Exists(preferred)) return preferred;
        foreach (var ext in new[] { ".xml", ".f" })
        {
            var p = Path.Combine(root, kind, name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Mở cả bộ chứng từ <paramref name="sysId"/> như FSD Studio: Dir (kèm Grid con khai &lt;items style="Grid" controller&gt;), Filter (kèm Grid con,
    /// form showForm đầu tiên), Grid cùng tên, Report. <paramref name="clicked"/> (nếu có) luôn có mặt và được ưu tiên đúng file đó.</summary>
    public void LoadSet(string root, string sysId, string? clicked)
    {
        ControllersRoot = root;
        SysId = sysId;
        void Add(string? path, string kind)
        {
            if (path is null) return;
            try { AddDoc(path, kind); } catch (Exception) { /* file hỏng / mất quyền đọc: bỏ tab đó */ }
        }
        void AddChildren(FgDoc? d)
        {
            if (d is null) return;
            foreach (var f in d.Fields)
            {
                if (f.Placeholder is not null) continue;
                foreach (Match m in GridItemsRegex.Matches(f.Xml))
                    if (ControllerRegex.Match(m.Value) is { Success: true } c) Add(Pick(root, "Grid", c.Groups[1].Value, clicked), "Grid");
            }
        }
        FgDoc? Find(string? path) => path is null ? null : Docs.FirstOrDefault(x => string.Equals(x.Main.Path, path, StringComparison.OrdinalIgnoreCase));

        var dir = Pick(root, "Dir", sysId, clicked);
        Add(dir, "Dir");
        AddChildren(Find(dir));
        var filter = Pick(root, "Filter", sysId, clicked);
        Add(filter, "Filter");
        AddChildren(Find(filter));
        // form lọc mở bằng showForm('X') trong Dir (lần đầu tiên) — như FSD Studio
        if (Find(dir) is { } dd && ShowFormRegex.Match(dd.Main.Text) is { Success: true } sf && !string.Equals(sf.Groups[1].Value, sysId, StringComparison.OrdinalIgnoreCase))
            Add(Pick(root, "Filter", sf.Groups[1].Value, clicked), "Filter");
        Add(Pick(root, "Grid", sysId, clicked), "Grid");
        Add(Pick(root, "Report", sysId, clicked), "Report");
        if (clicked is not null && Find(clicked) is null) Add(clicked, Locate(clicked).Kind);
    }

    // ---------------------------------------------------------------- sửa chuỗi

    private void Replace(FgFile f, int start, int end, string text)
    {
        if (f.ReadOnlyReason is not null) throw new InvalidOperationException(f.ReadOnlyReason);
        _undo.Push((f, f.Text));
        f.Text = string.Concat(f.Text.AsSpan(0, start), text, f.Text.AsSpan(end));
        ReparseAll();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        var (f, t) = _undo.Pop();
        f.Text = t;
        ReparseAll();
        return true;
    }

    /// <summary>Giá trị thuộc tính ghi vào XML: chỉ thoát " và &lt; (giữ &amp;X; / &amp;amp; người dùng gõ như đang thấy trên lưới).</summary>
    public static string EncodeAttr(string v) => v.Replace("\"", "&quot;").Replace("<", "&lt;");

    /// <summary>Đặt / thêm / bỏ thuộc tính của thẻ mở. <paramref name="value"/> null = bỏ thuộc tính (xoá cả khoảng trắng đứng trước).</summary>
    public void SetAttr(FgFile f, FgNode node, string name, string? value)
    {
        var a = node.Attr(name);
        if (value is null)
        {
            if (a is null) return;
            var s = a.Start;
            while (s > node.Start && char.IsWhiteSpace(f.Text[s - 1]) && f.Text[s - 1] is not ('\r' or '\n')) s--;
            if (s > node.Start && f.Text[s - 1] is '\r' or '\n') s = a.Start;   // thuộc tính đứng đầu dòng: giữ xuống dòng, chỉ xoá nó
            Replace(f, s, a.End, "");
            return;
        }
        var enc = EncodeAttr(value);
        if (a is not null)
        {
            if (f.Text[a.ValueStart..a.ValueEnd] == enc) return;
            var q = f.Text[a.ValueStart - 1];
            Replace(f, a.ValueStart, a.ValueEnd, q == '\'' ? enc.Replace("'", "&apos;") : enc);
            return;
        }
        var at = node.Attrs.Count > 0 ? node.Attrs[^1].End : node.Start + 1 + node.Name.Length;
        Replace(f, at, at, " " + name + "=\"" + enc + "\"");
    }

    /// <summary>&lt;header v e&gt; / &lt;footer v e&gt; của field: có thẻ thì đặt thuộc tính (không bỏ khi rỗng — giữ v="" e=""), chưa có thì chèn thẻ mới.</summary>
    public void SetChildAttr(FgField fld, string child, string attr, string value)
    {
        var f = fld.File;
        var node = fld.Node;
        if (node.Child(child) is { } c) { SetAttr(f, c, attr, value); return; }
        if (value.Length == 0) return;
        var tag = $"<{child} {(attr == "v" ? $"v=\"{EncodeAttr(value)}\" e=\"\"" : $"v=\"\" e=\"{EncodeAttr(value)}\"")}></{child}>";
        var nl = f.NewLine;
        var indent = IndentOf(f.Text, node.Start) ?? "";
        var childIndent = indent + "  ";
        if (node.SelfClosing)
        {
            // <field name="x"/> → <field name="x">\n  <header .../>\n</field>
            var slash = f.Text.LastIndexOf('/', node.StartTagEnd - 1);
            Replace(f, slash, node.StartTagEnd, ">" + nl + childIndent + tag + nl + indent + "</field>");
            return;
        }
        // footer đặt sau header nếu có
        var after = child == "footer" && node.Child("header") is { } h ? h.End : node.StartTagEnd;
        Replace(f, after, after, nl + childIndent + tag);
    }

    /// <summary>Thụt đầu dòng của nút nếu nó đứng đầu dòng (chỉ có khoảng trắng phía trước), không thì null.</summary>
    public static string? IndentOf(string t, int pos)
    {
        var ls = pos;
        while (ls > 0 && t[ls - 1] is ' ' or '\t') ls--;
        return ls == 0 || t[ls - 1] == '\n' ? t[ls..pos] : null;
    }

    /// <summary>Khối dòng của nút: từ đầu dòng tới hết xuống dòng, nếu nút đứng riêng dòng (Full); không thì đúng phạm vi nút.</summary>
    public static (int Start, int End, bool Full) Block(string t, FgNode n)
    {
        var ls = n.Start;
        while (ls > 0 && t[ls - 1] is ' ' or '\t') ls--;
        var startOk = ls == 0 || t[ls - 1] == '\n';
        var le = n.End;
        while (le < t.Length && t[le] is ' ' or '\t') le++;
        var endOk = le >= t.Length || t[le] is '\r' or '\n';
        if (!startOk || !endOk) return (n.Start, n.End, false);
        if (le < t.Length && t[le] == '\r') le++;
        if (le < t.Length && t[le] == '\n') le++;
        return (ls, le, true);
    }

    public void DeleteNode(FgFile f, FgNode n)
    {
        var b = Block(f.Text, n);
        Replace(f, b.Start, b.End, "");
    }

    /// <summary>Chuyển mục <paramref name="item"/> tới trước mục <paramref name="before"/> (null = sau mục cuối) trong cùng danh sách <paramref name="siblings"/>.</summary>
    public string? Move(FgFile f, List<FgNode> siblings, FgNode item, FgNode? before)
    {
        if (ReferenceEquals(item, before)) return null;
        var t = f.Text;
        var bi = Block(t, item);
        if (!bi.Full) return "Mục đang nằm chung dòng với mục khác — đổi thứ tự ở khung XML.";
        int at;
        if (before is not null)
        {
            var bb = Block(t, before);
            if (!bb.Full) return "Mục đích nằm chung dòng với mục khác — đổi thứ tự ở khung XML.";
            at = bb.Start;
        }
        else
        {
            var last = siblings.LastOrDefault(x => x.Name is "field" or "item" or "&");
            if (last is null || ReferenceEquals(last, item)) return null;
            var bl = Block(t, last);
            if (!bl.Full) return "Mục cuối nằm chung dòng với mục khác — đổi thứ tự ở khung XML.";
            at = bl.End;
            if (at > 0 && t[at - 1] != '\n') return "Mục cuối không có xuống dòng sau — đổi thứ tự ở khung XML.";
        }
        if (at >= bi.Start && at <= bi.End) return null;
        var block = t[bi.Start..bi.End];
        if (!block.EndsWith('\n')) block += f.NewLine;
        if (f.ReadOnlyReason is not null) return f.ReadOnlyReason;
        _undo.Push((f, f.Text));
        var removed = string.Concat(t.AsSpan(0, bi.Start), t.AsSpan(bi.End));
        var at2 = at > bi.Start ? at - (bi.End - bi.Start) : at;
        f.Text = removed.Insert(at2, block);
        ReparseAll();
        return null;
    }

    /// <summary>Chèn <paramref name="xml"/> (1 phần tử, xuống dòng nội bộ dùng "\n") thành dòng mới ngay sau <paramref name="after"/>; after null = cuối
    /// nội dung của <paramref name="container"/> (trước thẻ đóng).</summary>
    public void InsertAfter(FgFile f, FgNode? after, FgNode? container, string xml)
    {
        var t = f.Text;
        var nl = f.NewLine;
        string indent;
        int at;
        string prefix = "", suffix = "";
        if (after is not null)
        {
            indent = IndentOf(t, after.Start) ?? "    ";
            var b = Block(t, after);
            if (b.Full && b.End > 0 && t[b.End - 1] == '\n') { at = b.End; suffix = nl; }   // dòng mới ngay dưới
            else { at = after.End; prefix = nl; }                                          // cuối file / chung dòng: xuống dòng rồi chèn
        }
        else if (container is not null)
        {
            var closeIndent = IndentOf(t, container.ContentEnd);
            indent = (closeIndent ?? IndentOf(t, container.Start) ?? "") + "  ";
            if (closeIndent is not null) { at = container.ContentEnd - closeIndent.Length; suffix = nl; }   // trước dòng </fields>
            else { at = container.ContentEnd; prefix = nl; suffix = nl + (IndentOf(t, container.Start) ?? ""); }
        }
        else throw new InvalidOperationException("Không có chỗ chèn.");
        var body = indent + xml.Replace("\r\n", "\n").Replace("\n", nl + indent);
        Replace(f, at, at, prefix + body + suffix);
    }

    /// <summary>Thay cả phần tử bằng chữ người dùng sửa ở khung XML (bỏ thụt dòng đầu — đã có sẵn trong file).</summary>
    public void ReplaceNode(FgFile f, FgNode n, string xml)
    {
        if (f.Text[n.Start..n.End].Contains("<Encrypted>", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Phần tử có khối <Encrypted> — không thay bằng XML gõ tay (sửa thuộc tính trên lưới).");
        var nl = f.NewLine;
        var text = xml.TrimStart(' ', '\t').TrimEnd().Replace("\r\n", "\n").Replace("\n", nl);
        Replace(f, n.Start, n.End, text);
    }

    // ---------------------------------------------------------------- lưu

    /// <summary>File trên đĩa đã bị sửa nơi khác kể từ lúc nạp (so giờ sửa).</summary>
    public static bool ChangedOnDisk(FgFile f)
    {
        try { return File.Exists(f.Path) && File.GetLastWriteTimeUtc(f.Path) != f.DiskTime; } catch (Exception) { return false; }
    }

    /// <summary>Ghi các file đã sửa (giữ encoding + BOM gốc); trước khi ghi chép bản cũ vào %AppData%\Bcode\field-grid-backup\yyyyMMdd-HHmmss\.
    /// Trả danh sách file đã ghi.</summary>
    public List<string> Save()
    {
        var saved = new List<string>();
        string? backupDir = null;
        foreach (var f in AllFiles.Where(x => x.Dirty).ToList())
        {
            if (f.ReadOnlyReason is not null) continue;
            backupDir ??= MakeBackupDir();
            try { File.Copy(f.Path, Path.Combine(backupDir, saved.Count.ToString("00") + "_" + Path.GetFileName(f.Path)), overwrite: true); } catch (Exception) { /* không chép được bản cũ vẫn ghi */ }
            // Read-only (bản cài của khách): tạm gỡ, ghi, đặt lại như cũ — như FSD Studio
            var attrs = File.GetAttributes(f.Path);
            var ro = attrs.HasFlag(FileAttributes.ReadOnly);
            if (ro) File.SetAttributes(f.Path, attrs & ~FileAttributes.ReadOnly);
            try { File.WriteAllBytes(f.Path, f.Bytes()); }
            finally { if (ro) File.SetAttributes(f.Path, attrs); }
            f.DiskText = f.Text;
            f.DiskTime = File.GetLastWriteTimeUtc(f.Path);
            saved.Add(f.Path);
        }
        if (backupDir is not null && saved.Count > 0)
            try { File.WriteAllLines(Path.Combine(backupDir, "files.txt"), saved); } catch (Exception) { }
        _undo.Clear();
        return saved;
    }

    private static string MakeBackupDir()
    {
        var root = Path.Combine(BcodePaths.AppData, "Bcode", "field-grid-backup");
        var dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        for (var i = 1; Directory.Exists(dir); i++) dir = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + i);
        Directory.CreateDirectory(dir);
        // giữ 30 ngày
        try
        {
            foreach (var d in Directory.GetDirectories(root))
                if (Directory.GetCreationTime(d) < DateTime.Now.AddDays(-30)) Directory.Delete(d, true);
        }
        catch (Exception) { }
        return dir;
    }

    /// <summary>Nạp lại từ đĩa các file của <paramref name="doc"/> (file chính + include nó dùng); null = cả bộ. Bỏ Undo.</summary>
    public void Reload(FgDoc? doc)
    {
        var files = doc is null ? AllFiles.ToList()
            : doc.Fields.Select(x => x.File).Concat(doc.ViewItems.Select(x => x.File)).Append(doc.Main).Append(doc.TitleFile).Append(doc.SubTitleFile).Append(doc.FieldsFile).Append(doc.ViewsFile)
                .OfType<FgFile>().Where(x => !x.Virtual).Distinct().ToList();
        foreach (var f in files) { try { f.Reload(); if (f.ReadOnlyReason is null && InReadOnlyRoot(f.Path)) f.ReadOnlyReason = "File nằm trong kho source (chỉ đọc) — Bcode không ghi đè."; } catch (Exception) { } }
        _raw.Clear();
        _literals.Clear();
        foreach (var d in Docs) d.LoadEntities();
        _undo.Clear();
        ReparseAll();
    }

    /// <summary>So từng dòng bản trên đĩa với bản đang sửa (để "View Script" — xem diff trước khi lưu).</summary>
    public static string Diff(FgFile f)
    {
        var a = f.DiskText.Replace("\r\n", "\n").Split('\n');
        var b = f.Text.Replace("\r\n", "\n").Split('\n');
        int pre = 0;
        while (pre < a.Length && pre < b.Length && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Length - pre && suf < b.Length - pre && a[a.Length - 1 - suf] == b[b.Length - 1 - suf]) suf++;
        var am = a[pre..(a.Length - suf)];
        var bm = b[pre..(b.Length - suf)];
        var sb = new StringBuilder();
        if (am.Length * (long)bm.Length > 4_000_000)
        {
            sb.Append("@@ dòng ").Append(pre + 1).Append('\n');
            foreach (var l in am) sb.Append("- ").Append(l).Append('\n');
            foreach (var l in bm) sb.Append("+ ").Append(l).Append('\n');
            return sb.ToString();
        }
        // LCS trên phần giữa
        var dp = new int[am.Length + 1, bm.Length + 1];
        for (var i = am.Length - 1; i >= 0; i--)
            for (var j = bm.Length - 1; j >= 0; j--)
                dp[i, j] = am[i] == bm[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
        int x = 0, y = 0, lastPrinted = -10;
        var lines = new List<(char Op, string Text, int Line)>();
        while (x < am.Length || y < bm.Length)
        {
            if (x < am.Length && y < bm.Length && am[x] == bm[y]) { lines.Add((' ', am[x], pre + x + 1)); x++; y++; }
            else if (x < am.Length && (y >= bm.Length || dp[x + 1, y] >= dp[x, y + 1])) { lines.Add(('-', am[x], pre + x + 1)); x++; }
            else { lines.Add(('+', bm[y], pre + x + 1)); y++; }
        }
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Op == ' ')
            {
                var near = false;
                for (var k = Math.Max(0, i - 2); k <= Math.Min(lines.Count - 1, i + 2); k++) if (lines[k].Op != ' ') { near = true; break; }
                if (!near) continue;
            }
            if (i - lastPrinted > 1) sb.Append("@@ dòng ").Append(lines[i].Line).Append('\n');
            sb.Append(lines[i].Op).Append(' ').Append(lines[i].Text).Append('\n');
            lastPrinted = i;
        }
        return sb.ToString();
    }
}
