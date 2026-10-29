using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BcodeViewer.App.Host.FastSource;

/// <summary>1 tài liệu của Hỏi Fast (BcodeViewer): loại (controller / field / lookup / entity / include / func / button / version / release / topic),
/// khoá, tiêu đề, phần để tìm (từ đồng nghĩa: tiêu đề tiếng Việt, tên tách chữ hoa), câu trả lời + đoạn code mẫu, file nguồn.</summary>
public sealed record AskDoc(string Kind, string Key, string Title, string Body, string Text, string Code = "", string Path = "", string Source = "");

/// <summary>Kho tìm kiếm BM25 — cùng cách với <c>Bcode.App/Services/FastAskIndex.cs</c> (chữ không dấu, tách chữ hoa / gạch dưới, cặp 2 từ liền nhau).</summary>
public sealed class AskIndex
{
    private readonly List<AskDoc> _docs = new();
    private readonly List<Dictionary<string, int>> _tf = new();
    private readonly List<int> _len = new();
    private readonly Dictionary<string, int> _df = new(StringComparer.Ordinal);
    private double _avg;
    private const double K1 = 1.2, B = 0.75;

    public int Count => _docs.Count;
    public IReadOnlyList<AskDoc> Docs => _docs;

    public void Add(AskDoc d, int titleWeight = 2)
    {
        var terms = Terms(string.Join(" ", Enumerable.Repeat(d.Title, titleWeight)) + " " + d.Body);
        var tf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in terms) tf[t] = tf.GetValueOrDefault(t) + 1;
        foreach (var t in tf.Keys) _df[t] = _df.GetValueOrDefault(t) + 1;
        _docs.Add(d); _tf.Add(tf); _len.Add(terms.Count);
        _avg = 0;
    }

    public static string Fold(string s)
    {
        var n = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(n.Length);
        foreach (var c in n) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Replace('đ', 'd').Replace('Đ', 'D').ToLowerInvariant();
    }

    public static List<string> Terms(string text)
    {
        var split = Regex.Replace(text, @"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " ");
        var words = Regex.Matches(Fold(split).Replace('_', ' ').Replace('$', ' ').Replace('%', ' '), @"[a-z0-9#@]+").Select(m => m.Value).Where(w => !Stop.Contains(w)).ToList();
        var res = new List<string>(words);
        for (var i = 1; i < words.Count; i++) res.Add(words[i - 1] + "_" + words[i]);
        return res;
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "va", "cua", "cho", "cac", "nhung", "mot", "la", "co", "khong", "duoc", "theo", "tu", "den", "ve", "trong", "voi", "nao", "gi", "nhu", "hay", "minh", "toi",
        "xem", "tra", "tim", "giup", "nhe", "a", "di", "of", "for", "by", "and", "to", "in", "on", "the", "the_nao", "nao", "o", "thi", "can", "phai", "lam", "sao",
    };

    /// <summary>Điểm BM25 của mọi tài liệu khớp (chưa xếp).</summary>
    public List<(AskDoc Doc, double Score)> Score(string query, Func<AskDoc, bool>? filter = null)
    {
        if (_avg == 0) _avg = _len.Count == 0 ? 1 : _len.Average();
        var q = Terms(query).Distinct().ToList();
        var res = new List<(AskDoc, double)>();
        if (q.Count == 0) return res;
        for (var i = 0; i < _docs.Count; i++)
        {
            if (filter is not null && !filter(_docs[i])) continue;
            double s = 0;
            foreach (var t in q)
            {
                if (!_tf[i].TryGetValue(t, out var f)) continue;
                var idf = Math.Log(1 + (_docs.Count - _df[t] + 0.5) / (_df[t] + 0.5));
                s += (t.Contains('_') ? 2 : 1) * idf * f * (K1 + 1) / (f + K1 * (1 - B + B * _len[i] / _avg));
            }
            if (s > 0) res.Add((_docs[i], s));
        }
        return res;
    }
}

/// <summary>
/// Hỏi Fast trong BcodeViewer (không AI): hỏi về controller, field, lookup, include / entity, toolbar, hàm JS mẫu và lịch sử chỉnh sửa theo
/// phiên bản source Fast. Kiến thức = tài liệu rút từ source (mẫu đi kèm <c>Assets\fastsource\ask.json</c> + source Fast mới nhất + source dự án),
/// xếp bằng BM25 cộng 2 gợi ý mềm: loại tài liệu câu hỏi nhắm tới (lookup / include / phiên bản / js…) và tên riêng có trong câu (ma_kh, AIDetail).
/// </summary>
public sealed class SourceAsk
{
    private readonly AskIndex _index = new();
    private readonly Dictionary<string, AskDoc> _byKey = new(StringComparer.OrdinalIgnoreCase);
    public int Count => _index.Count;

    /// <summary>Bật / tắt từng phần cải tiến (để đo trước – sau).</summary>
    public bool UseIntent { get; set; } = true;
    public bool UseNames { get; set; } = true;

    /// <summary>Thêm tài liệu; trùng khoá thì nguồn đứng trước trong danh sách gọi (ưu tiên cao hơn) thắng.</summary>
    public void AddRange(IEnumerable<AskDoc> docs)
    {
        foreach (var d in docs)
        {
            var k = d.Kind + ":" + d.Key;
            if (_byKey.ContainsKey(k)) continue;
            _byKey[k] = d;
            _index.Add(d, d.Kind is "topic" ? 3 : 2);
        }
    }

    public AskDoc? Get(string kindKey) => _byKey.GetValueOrDefault(kindKey);

    // ---------------------------------------------------------------- hiểu câu hỏi

    private static readonly (string Kind, Regex Rx)[] Intents =
    {
        ("lookup", new(@"\b(lookup|tra cuu|autocomplete|items|danh muc tra)\b")),
        ("import", new(@"\b(import|upload|nhap excel|nhap tu excel|nhap du lieu|clipboard|dan tu excel)\b")),
        ("importfield", new(@"\b(import|upload|nhap excel|nhap tu excel)\b")),
        ("entity", new(@"\b(include|entity|entities|ent|khai bao trong file|file nao khai bao)\b")),
        ("include", new(@"\b(include|file ent|\.ent)\b")),
        ("version", new(@"\b(phien ban|ban sp|sp ?\d|sua gi|thay doi|lich su|doi tu ban|them tu ban|tu ban nao|ban nao)\b")),
        ("release", new(@"\b(phien ban|ban sp|sp ?\d|sua nhung file|nhung file nao)\b")),
        ("func", new(@"\b(js|javascript|ham|function|script|vi du)\b")),
        ("controller", new(@"\b(controller|luu bang|luu vao bang|bang nao|man hinh|grid|dir|filter|report)\b")),
        ("field", new(@"\b(field|truong|cot)\b")),
        ("button", new(@"\b(toolbar|nut|button)\b")),
        ("topic", new(@"\b(cach|the nao|lam sao|quy uoc|huong dan)\b")),
    };

    public static HashSet<string> IntentOf(string query)
    {
        var f = AskIndex.Fold(query);
        return Intents.Where(x => x.Rx.IsMatch(f)).Select(x => x.Kind).ToHashSet();
    }

    /// <summary>Tên riêng trong câu (ma_kh, AIDetail, DetailTax, SP22.6…): có gạch dưới / chữ hoa giữa từ / chữ số.</summary>
    public static HashSet<string> NamesOf(string query) =>
        Regex.Matches(query, @"[A-Za-z_$][\w$%.]*").Select(m => m.Value.Trim('.')).Where(w => w.Length >= 3 &&
            !Regex.IsMatch(w, @"^(FBOR\d*)?SP\d", RegexOptions.IgnoreCase) &&
            (w.Contains('_') || Regex.IsMatch(w, @"[a-z][A-Z]|[A-Z]{2}[a-z]|\d") || Regex.IsMatch(w, @"^[A-Z][a-z]+[A-Za-z0-9]*$")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tên phiên bản trong câu ("SP22.6", "sp 24.2.1", "FBOR2SP22.6") → dạng thư mục.</summary>
    public static string? VersionOf(string query) =>
        Regex.Match(query, @"(?:FBOR2)?SP\s?(\d+(?:\.\d+)*)", RegexOptions.IgnoreCase) is { Success: true } m ? "FBOR2SP" + m.Groups[1].Value : null;

    /// <summary>Xếp hạng: BM25 × (1.6 nếu đúng loại câu hỏi nhắm tới — 2.5 với câu hỏi lịch sử) × (2.5 nếu khoá trùng tên riêng trong câu).</summary>
    public List<(AskDoc Doc, double Score)> Search(string query, int top = 5)
    {
        var intents = UseIntent ? IntentOf(query) : new HashSet<string>();
        var names = UseNames ? NamesOf(query) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ver = VersionOf(query);
        // câu nêu tên cụ thể (ma_kh, Customer) là hỏi về đối tượng đó, không phải hỏi quy ước chung
        if (names.Count > 0) intents.Remove("topic");
        // "field import ma_cong …", "field ma_kh của PDTran đổi từ bản nào": chữ "field" chỉ nêu đối tượng, ý chính là import / lịch sử
        if (intents.Contains("import") || intents.Contains("version")) intents.Remove("field");
        // "import SVTran sửa gì ở SP24": hỏi lịch sử (tài liệu lịch sử gộp theo tên đã gồm Upload/SVTran)
        if (intents.Contains("version")) { intents.Remove("import"); intents.Remove("importfield"); }
        var importAsked = intents.Contains("import");
        var scored = _index.Score(query).Select(x =>
        {
            var s = x.Score;
            // hỏi lịch sử ("sửa gì", "từ bản nào") là ý rất rõ → nặng hơn các loại khác
            if (intents.Contains(x.Doc.Kind)) s *= x.Doc.Kind == "version" ? 2.5 : 1.6;
            // tài liệu import ngắn nên BM25 cao — chỉ cho thắng khi câu hỏi nói tới import / upload / nhập Excel
            else if (UseIntent && !importAsked && x.Doc.Kind is "import" or "importfield") s *= 0.6;
            if (names.Count > 0)
            {
                var last = x.Doc.Key.Contains('/') ? x.Doc.Key[(x.Doc.Key.LastIndexOf('/') + 1)..] : x.Doc.Key;
                last = Regex.Replace(last, @"\.(xml|ent|txt|aspx|js)$", "", RegexOptions.IgnoreCase);
                if (names.Contains(last) || names.Contains(x.Doc.Key)) s *= 3;
            }
            // "bản SP23 sửa những file nào" → tài liệu của bản; có tên controller đi kèm thì tên đó quyết định
            if (ver is not null && names.Count == 0 && x.Doc.Kind == "release" && string.Equals(x.Doc.Key, ver, StringComparison.OrdinalIgnoreCase)) s *= 2.5;
            return (x.Doc, s);
        });
        return scored.OrderByDescending(x => x.s).Take(top).ToList();
    }

    // ---------------------------------------------------------------- dựng tài liệu từ source

    public static string Attr(string tag, string name) => Regex.Match(tag, @"\b" + name + @"\s*=\s*""(?<x>[^""]*)""") is { Success: true } m ? m.Groups["x"].Value : "";

    private static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim();
    private static string Cut(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    /// <summary>Chuẩn hoá khối XML để in: bỏ thụt lề chung.</summary>
    private static string Dedent(string block)
    {
        var lines = block.Replace("\r\n", "\n").Split('\n').ToList();
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        var ind = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select((l, i) => i == 0 ? l.TrimStart() : l.Length >= ind ? l[ind..] : l.TrimStart()));
    }

    private sealed class Agg
    {
        public int N;
        public readonly Dictionary<string, int> Variants = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Sample = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Headers = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Items = new(StringComparer.Ordinal);
        public readonly List<string> Files = new();
        public void Var(string norm, string raw) { Variants[norm] = Variants.GetValueOrDefault(norm) + 1; Sample.TryAdd(norm, raw); }
        public string Top(Dictionary<string, int> d) => d.Count == 0 ? "" : d.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key;
    }

    private static string KindOfControllerPath(string path)
    {
        var p = path.Replace('\\', '/');
        // khai báo import danh mục / chứng từ từ Excel (Templates\Upload, kể cả Upload\Include) và dán từ Excel (Templates\Clipboard)
        if (Regex.Match(p, @"/Templates/(Upload|Clipboard)/", RegexOptions.IgnoreCase) is { Success: true } t)
            return char.ToUpperInvariant(t.Groups[1].Value[0]) + t.Groups[1].Value[1..].ToLowerInvariant();
        var m = Regex.Match(p, @"/(Dir|Grid|Filter|Report|Lookup|List|Query|Include)/", RegexOptions.IgnoreCase);
        return m.Success ? char.ToUpperInvariant(m.Groups[1].Value[0]) + m.Groups[1].Value[1..].ToLowerInvariant() : "";
    }

    /// <summary>Tài liệu rút từ các file source (controller XML, .ent, include). <paramref name="source"/> = nhãn nguồn (mẫu / Fast SPxx / dự án).</summary>
    public static List<AskDoc> Build(IEnumerable<(string Path, string Text)> files, string source)
    {
        var docs = new List<AskDoc>();
        var fields = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
        var lookups = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
        var entities = new Dictionary<string, Agg>(StringComparer.Ordinal);
        var entUse = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var includes = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
        var funcs = new Dictionary<string, Agg>(StringComparer.Ordinal);
        var buttons = new Dictionary<string, Agg>(StringComparer.Ordinal);
        var importFields = new Dictionary<string, Agg>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, raw) in files)
        {
            var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".aspx" or ".ascx" or ".master" or ".js") continue;
            var text = raw.Replace("\r\n", "\n");
            var name = System.IO.Path.GetFileNameWithoutExtension(path);
            var kind = KindOfControllerPath(path);
            var rel = kind.Length > 0 ? kind + "/" + System.IO.Path.GetFileName(path) : System.IO.Path.GetFileName(path);

            // entity khai báo trong file (cả .ent / include) + nơi dùng
            foreach (Match m in Regex.Matches(text, @"<!ENTITY\s+(?<pct>%\s+)?(?<n>[\w.$-]+)\s+(?<v>SYSTEM\s+""[^""]*""|""[^""]*""|'[^']*')\s*>"))
            {
                var n = m.Groups["n"].Value;
                if (!entities.TryGetValue(n, out var a)) entities[n] = a = new Agg();
                a.N++;
                var v = m.Groups["v"].Value;
                var isSys = v.StartsWith("SYSTEM");
                var target = isSys ? Regex.Match(v, @"""([^""]*)""").Groups[1].Value : "";
                a.Var(isSys ? "SYSTEM " + target : Cut(Norm(v), 160), Cut(m.Value, 1200));
                if (a.Files.Count < 30) a.Files.Add(rel);
                if (isSys)
                {
                    var inc = System.IO.Path.GetFileName(target.Replace('\\', '/'));
                    if (inc.Length > 0)
                    {
                        if (!includes.TryGetValue(inc, out var ia)) includes[inc] = ia = new Agg();
                        ia.N++; ia.Var(target, m.Value); ia.Headers[n] = ia.Headers.GetValueOrDefault(n) + 1;
                        if (ia.Files.Count < 40) ia.Files.Add(rel);
                    }
                }
                else if (ext is ".ent" or ".txt" || kind == "Include")
                {
                    // khai trong 1 file include: ghi lại "khai trong file X"
                    a.Items[System.IO.Path.GetFileName(path)] = a.Items.GetValueOrDefault(System.IO.Path.GetFileName(path)) + 1;
                }
            }
            foreach (Match m in Regex.Matches(text, @"[&%]([\w.$-]+);"))
            {
                if (!entUse.TryGetValue(m.Groups[1].Value, out var l)) entUse[m.Groups[1].Value] = l = new();
                if (l.Count < 30 && !l.Contains(rel)) l.Add(rel);
            }

            if (kind is "Upload" or "Clipboard")
            {
                if (BuildImport(text, rel, kind, source, importFields) is { } imp) docs.Add(imp);
                continue;
            }

            var root = Regex.Match(text, @"<(dir|grid|filter|report|lookup|list|query)\b[^>]*>");
            if (!root.Success) continue;
            var rootTag = root.Value;
            var title = Regex.Match(text[root.Index..], @"<title\s+v=""(?<v>[^""]*)""\s+e=""(?<e>[^""]*)""");
            var tv = title.Success ? title.Groups["v"].Value : "";
            var te = title.Success ? title.Groups["e"].Value : "";
            var table = Attr(rootTag, "table");
            var fieldsPart = Regex.Match(text, @"<fields\b[^>]*>([\s\S]*?)</fields>") is { Success: true } fp ? fp.Groups[1].Value : "";
            var fieldNames = new List<string>();
            foreach (Match m in Regex.Matches(fieldsPart, @"<field\s+name=""(?<n>[^""]+)""(?<a>[^>]*?)(/>|>(?<body>[\s\S]*?)</field>)"))
            {
                var fn = m.Groups["n"].Value;
                if (fn.Contains('&')) continue;
                fieldNames.Add(fn);
                if (!fields.TryGetValue(fn, out var a)) fields[fn] = a = new Agg();
                a.N++;
                a.Var(Norm(m.Value), Dedent(text.Substring(fieldsPart.Length > 0 ? 0 : 0, 0) + m.Value));
                if (a.Files.Count < 40) a.Files.Add(rel);
                var body = m.Groups["body"].Value;
                if (Regex.Match(body, @"<header\s+v=""(?<v>[^""]*)""\s+e\s*=\s*""(?<e>[^""]*)""") is { Success: true } h && h.Groups["v"].Value.Length > 0)
                    a.Headers[h.Groups["v"].Value + " | " + h.Groups["e"].Value] = a.Headers.GetValueOrDefault(h.Groups["v"].Value + " | " + h.Groups["e"].Value) + 1;
                foreach (Match it in Regex.Matches(body, @"<items\b[^>]*>"))
                {
                    var itv = Norm(it.Value);
                    a.Items[itv] = a.Items.GetValueOrDefault(itv) + 1;
                    var lk = Attr(it.Value, "controller");
                    // chỉ ô tra cứu (AutoComplete); style="Grid" controller="AIDetail" là lưới con, không phải lookup
                    if (lk.Length == 0 || !Attr(it.Value, "style").Contains("AutoComplete", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!lookups.TryGetValue(lk, out var la)) lookups[lk] = la = new Agg();
                    la.N++; la.Var(itv, it.Value); la.Headers[fn] = la.Headers.GetValueOrDefault(fn) + 1;
                    if (la.Files.Count < 30 && !la.Files.Contains(rel)) la.Files.Add(rel);
                }
            }
            var ents = Regex.Matches(text, @"<!ENTITY\s+(?:%\s+)?([\w.$-]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
            var tb = Regex.Match(text, @"<toolbar\b[^>]*>([\s\S]*?)</toolbar>");
            var btns = tb.Success ? Regex.Matches(tb.Groups[1].Value, @"<button\b[^>]*?\bcommand=""(?<n>[^""]+)""[^>]*>").Select(m => m.Groups["n"].Value).Where(b => b != "-").Distinct().ToList() : new();
            if (tb.Success)
                foreach (Match bm in Regex.Matches(tb.Groups[1].Value, @"<button\b[^>]*?\bcommand=""(?<n>[^""]+)""[^>]*?(/>|>[\s\S]*?</button>)"))
                {
                    var bn = bm.Groups["n"].Value;
                    if (bn == "-") continue;
                    if (!buttons.TryGetValue(bn, out var ba)) buttons[bn] = ba = new Agg();
                    ba.N++; ba.Var(Norm(bm.Value), Dedent(bm.Value));
                    if (ba.Files.Count < 20) ba.Files.Add(rel);
                }
            var events = Regex.Matches(text, @"<(command|query)\s+event=""([^""]+)""").Select(m => m.Groups[1].Value + " " + m.Groups[2].Value).Distinct().ToList();
            var actions = Regex.Matches(text, @"<action\s+id=""([^""]+)""").Select(m => m.Groups[1].Value).Distinct().ToList();
            var forms = Regex.Matches(text, @"<form\s+id=""(?<id>[^""]+)""[^>]*>\s*<header\s+v=""(?<v>[^""]*)""").Select(m => m.Groups["id"].Value + " (" + m.Groups["v"].Value + ")").Distinct().Take(12).ToList();
            var partition = Regex.Match(text, @"<partition\b[^>]*>").Value;

            var sb = new StringBuilder();
            sb.Append($"Controller {rel}");
            if (tv.Length > 0) sb.Append($" — {tv}{(te.Length > 0 ? " / " + te : "")}");
            sb.Append('\n');
            if (table.Length > 0) sb.Append($"Lưu bảng: {table}{(Attr(rootTag, "type") is { Length: > 0 } ty ? " (type=\"" + ty + "\")" : "")}{(Attr(rootTag, "id") is { Length: > 0 } id ? ", mã chứng từ id=\"" + id + "\"" : "")}\n");
            if (partition.Length > 0) sb.Append($"Phân kỳ: {Attr(partition, "table")} (prime {Attr(partition, "prime")}, inquiry {Attr(partition, "inquiry")}, theo {Attr(partition, "field")})\n");
            if (fieldNames.Count > 0) sb.Append($"{fieldNames.Count} field: {string.Join(", ", fieldNames.Take(30))}{(fieldNames.Count > 30 ? ", …" : "")}\n");
            if (ents.Count > 0) sb.Append($"Entity / include: {string.Join(", ", ents.Take(20))}{(ents.Count > 20 ? ", …" : "")}\n");
            if (btns.Count > 0) sb.Append($"Toolbar: {string.Join(", ", btns)}\n");
            if (events.Count > 0) sb.Append($"Sự kiện: {string.Join(", ", events)}\n");
            if (actions.Count > 0) sb.Append($"Action (response): {string.Join(", ", actions.Take(15))}\n");
            if (forms.Count > 0) sb.Append($"Mẫu in: {string.Join(", ", forms)}\n");
            docs.Add(new AskDoc("controller", rel.Replace(".xml", ""),
                $"{name} {kind} {tv} {te}",
                $"bảng {table} {Attr(rootTag, "type")} {Attr(rootTag, "id")} {Attr(partition, "table")} " + string.Join(" ", forms.Take(6)),
                sb.ToString().TrimEnd(), Cut(rootTag, 400), path, source));
        }

        // field
        foreach (var (fn, a) in fields)
        {
            var top = a.Top(a.Variants);
            var head = a.Top(a.Headers);
            var item = a.Top(a.Items);
            var lk = item.Length > 0 ? Attr(item, "controller") : "";
            var t = new StringBuilder($"Field {fn}{(head.Length > 0 ? " — " + head.Replace(" | ", " / ") : "")}: khai ở {a.N} chỗ ({string.Join(", ", a.Files.Distinct().Take(8))}{(a.Files.Distinct().Count() > 8 ? ", …" : "")}).");
            if (item.Length > 0) t.Append($"\nHay dùng ({a.Items[item]}/{a.N}): {item}");
            if (a.Variants.Count > 1) t.Append($"\nCách khai hay gặp nhất ({a.Variants[top]}/{a.N} chỗ):");
            docs.Add(new AskDoc("field", fn, $"field {fn} {head.Replace(" | ", " ")}",
                $"{(lk.Length > 0 ? "lookup " + lk : "")} {string.Join(" ", a.Headers.Keys.Take(3)).Replace(" | ", " ")}",
                t.ToString(), a.Sample[top], "", source));
        }
        // lookup
        foreach (var (lk, a) in lookups)
        {
            var top = a.Top(a.Variants);
            var fs = a.Headers.OrderByDescending(x => x.Value).Select(x => x.Key).Take(8).ToList();
            docs.Add(new AskDoc("lookup", lk, $"lookup {lk} AutoComplete tra cứu",
                $"field {string.Join(" ", fs)}",
                $"Lookup controller=\"{lk}\": dùng ở {a.N} field ({string.Join(", ", fs)}) trong {string.Join(", ", a.Files.Take(6))}{(a.Files.Count > 6 ? ", …" : "")}.\nKhai báo hay gặp nhất ({a.Variants[top]}/{a.N}):",
                a.Sample[top], "", source));
        }
        // entity
        foreach (var (en, a) in entities)
        {
            var top = a.Top(a.Variants);
            var where = a.Items.Keys.ToList();
            var used = entUse.GetValueOrDefault(en) ?? new();
            var t = new StringBuilder($"Entity {en}: khai ở {a.N} chỗ");
            if (top.StartsWith("SYSTEM ")) t.Append($", trỏ tới file {top[7..]}");
            if (where.Count > 0) t.Append($"; khai trong file include {string.Join(", ", where.Take(5))}");
            t.Append('.');
            if (used.Count > 0) t.Append($"\nDùng ở: {string.Join(", ", used.Take(10))}{(used.Count > 10 ? ", …" : "")}");
            docs.Add(new AskDoc("entity", en, $"entity {en} &{en}; %{en};",
                $"{(top.StartsWith("SYSTEM ") ? "include " + System.IO.Path.GetFileName(top[7..].Replace('\\', '/')) : "")} {string.Join(" ", where.Take(3))}",
                t.ToString(), a.Sample[top], "", source));
        }
        // include
        foreach (var (inc, a) in includes)
        {
            var names = a.Headers.OrderByDescending(x => x.Value).Select(x => x.Key).Take(6).ToList();
            docs.Add(new AskDoc("include", inc, $"include {inc} {System.IO.Path.GetFileNameWithoutExtension(inc)}",
                $"entity {string.Join(" ", names)}",
                $"File include {inc} ({a.Top(a.Variants)}): khai bằng entity {string.Join(", ", names)} ở {a.N} controller ({string.Join(", ", a.Files.Take(8))}{(a.Files.Count > 8 ? ", …" : "")}).\nCách khai:",
                a.Sample[a.Top(a.Variants)], "", source));
        }
        // field của khai báo import (cột Excel → field, kiểm tra + mã lỗi)
        foreach (var (fn, a) in importFields)
        {
            var top = a.Top(a.Variants);
            var checks = a.Items.OrderByDescending(x => x.Value).Take(3).Select(x => x.Key).ToList();
            docs.Add(new AskDoc("importfield", fn, $"import field {fn} nhập excel upload",
                string.Join(" ", a.Headers.Keys.Take(4)),
                $"Field import {fn}: có ở {a.N} chỗ ({string.Join(", ", a.Files.Distinct().Take(8))}{(a.Files.Distinct().Count() > 8 ? ", …" : "")})."
                    + (checks.Count > 0 ? "\nKiểm tra hay dùng: " + string.Join(" | ", checks) : "") + "\nCách khai hay gặp nhất:",
                a.Sample[top], "", source));
        }
        // toolbar
        foreach (var (bn, a) in buttons)
        {
            var top = a.Top(a.Variants);
            docs.Add(new AskDoc("button", bn, $"toolbar nút button {bn}", "",
                $"Nút toolbar command=\"{bn}\": có ở {a.N} controller ({string.Join(", ", a.Files.Take(6))}{(a.Files.Count > 6 ? ", …" : "")}).\nKhai báo hay gặp nhất:",
                a.Sample[top], "", source));
        }
        return docs;
    }

    /// <summary>1 file khai báo import (Templates\Upload — nhập danh mục / chứng từ từ Excel) hoặc clipboard (Templates\Clipboard — dán từ Excel):
    /// tài liệu "import" (loại, dòng bắt đầu, bảng gốc, bảng tạm, field + kiểm tra, thông báo lỗi); field của nó gộp vào <paramref name="importFields"/>.
    /// File include chỉ có danh sách &lt;field …/&gt; (Upload\Include\SVTranFieldsCompact.txt) chỉ góp field.</summary>
    private static AskDoc? BuildImport(string text, string rel, string kind, string source, Dictionary<string, Agg> importFields)
    {
        var root = Regex.Match(text, @"<(import|clipboard)\b[^>]*>");
        var fieldsPart = Regex.Match(text, @"<fields\b[^>]*>([\s\S]*?)</fields>") is { Success: true } fp ? fp.Groups[1].Value : root.Success ? "" : text;
        var names = new List<string>();
        var checks = new List<string>();
        foreach (Match m in Regex.Matches(fieldsPart, @"<field\s+name=""(?<n>[^""]+)""(?<a>[^>]*?)(/>|>(?<body>[\s\S]*?)</field>)"))
        {
            var fn = m.Groups["n"].Value;
            if (fn.Contains('&')) continue;
            names.Add(fn);
            if (!importFields.TryGetValue(fn, out var a)) importFields[fn] = a = new Agg();
            a.N++;
            a.Var(Norm(m.Value), Dedent(m.Value));
            if (a.Files.Count < 40 && !a.Files.Contains(rel)) a.Files.Add(rel);
            var ck = Attr(m.Value, "check");
            if (ck.Length > 0)
            {
                var c = Cut(System.Net.WebUtility.HtmlDecode(ck).Replace("&And;", " | "), 160) + (Attr(m.Value, "errorCode") is { Length: > 0 } ec ? $" (lỗi {ec.Replace("&And;", ", ")})" : "");
                a.Items[c] = a.Items.GetValueOrDefault(c) + 1;
                if (checks.Count < 12) checks.Add($"{fn}: {c}");
            }
            foreach (var attr in new[] { "resource", "type" })
                if (Attr(m.Value, attr) is { Length: > 0 } v) a.Headers[attr + " " + v] = a.Headers.GetValueOrDefault(attr + " " + v) + 1;
        }
        if (!root.Success) return null;
        string Set(string tag) => Regex.Match(text, @"<" + tag + @"\s+value=""(?<v>[^""]*)""") is { Success: true } m ? m.Groups["v"].Value : "";
        var name = System.IO.Path.GetFileNameWithoutExtension(rel);
        var type = Set("type");
        var vars = Regex.Matches(text, @"<var\s+id=""(?<id>[^""]+)"">\s*<header\s+v=""(?<v>[^""]*)""").Select(m => m.Groups["id"].Value + " " + m.Groups["v"].Value).Take(10).ToList();
        var ents = Regex.Matches(text, @"<!ENTITY\s+(?:%\s+)?([\w.$-]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        var sb = new StringBuilder(kind == "Upload"
            ? $"Import {rel} — nhập {(type == "Voucher" ? "chứng từ" : type == "List" ? "danh mục" : type.Length > 0 ? type : "dữ liệu")} từ Excel"
            : $"Clipboard {rel} — dán dữ liệu từ Excel vào lưới");
        sb.Append('\n');
        foreach (var (tag, label) in new[] { ("type", "Loại"), ("startRow", "Dòng bắt đầu"), ("baseTable", "Bảng gốc"), ("listTable", "Bảng tạm"), ("temporary", "Bảng tạm"), ("dependence", "Bảng liên quan"), ("alias", "Bí danh"), ("allowFileExtension", "Đuôi file") })
            if (Set(tag) is { Length: > 0 } v) sb.Append($"{label}: {v}\n");
        if (names.Count > 0) sb.Append($"{names.Count} field: {string.Join(", ", names.Take(30))}{(names.Count > 30 ? ", …" : "")}\n");
        if (checks.Count > 0) sb.Append("Kiểm tra: " + string.Join("; ", checks.Take(8)) + "\n");
        if (vars.Count > 0) sb.Append("Thông báo lỗi: " + string.Join("; ", vars) + "\n");
        if (ents.Count > 0) sb.Append($"Entity / include: {string.Join(", ", ents.Take(15))}{(ents.Count > 15 ? ", …" : "")}\n");
        var setting = Regex.Match(text, @"<setting>[\s\S]*?</setting>").Value;
        return new AskDoc("import", rel.Replace(".xml", ""), $"import {name} {kind} nhập excel {(type == "Voucher" ? "chứng từ" : "danh mục")}",
            $"{Set("baseTable")} {Set("listTable")} {type}", sb.ToString().TrimEnd(), Cut(Dedent(setting), 1500), "", source);
    }

    /// <summary>Hàm JS (trong &lt;script&gt; của controller): mỗi tên hàm 1 tài liệu kèm 1 ví dụ (bản ngắn nhất trong các bản hay gặp).</summary>
    public static List<AskDoc> BuildFunctions(IEnumerable<(string Path, string Text)> files, string source)
    {
        var funcs = new Dictionary<string, (int N, List<string> Files, string Body)>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
        {
            if (!path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = KindOfControllerPath(path) is { Length: > 0 } k ? k + "/" + System.IO.Path.GetFileName(path) : System.IO.Path.GetFileName(path);
            foreach (var script in SourceGhost.ScriptParts(text.Replace("\r\n", "\n")))
                foreach (Match m in Regex.Matches(script, @"(?m)^[ \t]*function\s+(?<n>[\w$]+)\s*\([^)]*\)\s*\{"))
                {
                    var end = Regex.Match(script[(m.Index + m.Length)..], @"(?m)^[ \t]*function\s+[\w$]+\s*\(");
                    var body = Dedent(end.Success ? script.Substring(m.Index, m.Length + end.Index) : script[m.Index..]);
                    var lines = body.Split('\n');
                    if (lines.Length > 30) body = string.Join("\n", lines.Take(30)) + "\n…";
                    var n = m.Groups["n"].Value;
                    var generic = Regex.Replace(n, @"\$[A-Z]\w*?(Grid|Dir|Report|Filter)?\w*\$", "$…$");
                    if (!funcs.TryGetValue(n, out var f)) f = (0, new List<string>(), body);
                    if (f.Files.Count < 20) f.Files.Add(rel);
                    funcs[n] = (f.N + 1, f.Files, body.Length < f.Body.Length ? body : f.Body);
                }
        }
        return funcs.Select(x => new AskDoc("func", x.Key, $"js function {x.Key}", "javascript ví dụ hàm",
            $"Hàm JS {x.Key}: có ở {x.Value.N} chỗ ({string.Join(", ", x.Value.Files.Distinct().Take(6))}{(x.Value.Files.Distinct().Count() > 6 ? ", …" : "")}). Ví dụ:",
            x.Value.Body, "", source)).ToList();
    }

    /// <summary>Tài liệu lịch sử chỉnh sửa: mỗi file 1 tài liệu (các bản đã sửa + chi tiết), mỗi bản 1 tài liệu (sửa những file nào).</summary>
    public static List<AskDoc> BuildVersions(SourceVersions v)
    {
        var docs = new List<AskDoc>();
        // gộp theo tên controller: AITran có ở Dir / Grid / Filter / Report — 1 câu hỏi "AITran sửa gì" trả lời cả bộ
        foreach (var g in v.Changes.Where(c => !c.F.Contains(':')).GroupBy(c => BaseName(c.F), StringComparer.OrdinalIgnoreCase))
        {
            var name = g.Key;
            var vers = g.Select(c => c.V).Distinct().ToList();
            var files = g.Select(c => c.F).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var sb = new StringBuilder($"Lịch sử sửa {name} ({string.Join(", ", files)}): {vers.Count} bản ({string.Join(", ", vers.TakeLast(12))}).");
            foreach (var vg in g.GroupBy(c => c.V).Reverse().Take(14))
                foreach (var fg in vg.GroupBy(c => c.F))
                {
                    sb.Append($"\n■ {vg.Key} · {fg.Key}: ");
                    sb.Append(string.Join("; ", fg.Take(14).Select(Describe)));
                    if (fg.Count() > 14) sb.Append($"; … (+{fg.Count() - 14})");
                }
            var names = string.Join(" ", g.Where(c => c.K != "file").Select(c => c.N).Distinct().Take(40));
            var main = files.OrderBy(f => FolderRank(f)).First();
            docs.Add(new AskDoc("version", name, $"lịch sử phiên bản {name}", "sửa thay đổi " + names, sb.ToString(),
                "", v.Latest.TryGetValue(main, out var l) ? l[1] : "", "Fast các phiên bản"));
        }
        foreach (var g in v.Changes.GroupBy(c => c.V, StringComparer.OrdinalIgnoreCase))
        {
            var files = g.GroupBy(c => c.F).OrderByDescending(x => x.Count()).ToList();
            var added = g.Count(c => c.K == "file" && c.Op == "+");
            var sb = new StringBuilder($"Bản {g.Key}: {files.Count} file ({added} lần đầu có trong gói), {g.Count(c => c.K != "file")} thay đổi chi tiết.");
            foreach (var f in files.Where(f => f.Any(c => c.K != "file")).Take(25))
                sb.Append($"\n■ {f.Key}: {string.Join("; ", f.Take(5).Select(Describe))}{(f.Count() > 5 ? $"; … (+{f.Count() - 5})" : "")}");
            docs.Add(new AskDoc("release", g.Key, $"phiên bản {g.Key} {Regex.Replace(g.Key, @"^FBOR2", "")}", string.Join(" ", files.Take(40).Select(f => System.IO.Path.GetFileNameWithoutExtension(f.Key))),
                sb.ToString(), "", "", "Fast các phiên bản"));
        }
        return docs;
    }

    /// <summary>Tên controller của 1 file trong lịch sử: "Grid/AIDetail.xml" → "AIDetail".</summary>
    public static string BaseName(string relKey) => Regex.Replace(System.IO.Path.GetFileName(relKey), @"\.(xml|ent|txt|aspx|ascx|js)$", "", RegexOptions.IgnoreCase);

    private static int FolderRank(string f) => f.Split('/')[0].ToLowerInvariant() switch { "dir" => 0, "grid" => 1, "filter" => 2, "report" => 3, "lookup" => 4, _ => 9 };

    public static string Describe(SourceVersions.Change c) => c.K == "file"
        ? (c.Op == "+" ? "có file" : "sửa file") + (c.D.Length > 0 ? " (" + c.D + ")" : "")
        : $"{(c.Op == "+" ? "thêm" : c.Op == "-" ? "bỏ" : "sửa")} {c.K} {c.N}{(c.Op == "~" && c.D.Length > 0 ? " [" + Cut(c.D, 160) + "]" : "")}";

    /// <summary>Quy ước viết controller Fast (viết tay, ngắn) — trả lời các câu "cách khai … thế nào".</summary>
    public static List<AskDoc> Topics() => new()
    {
        new("topic", "lookup", "cách khai báo lookup AutoComplete cho field", "items style AutoComplete controller reference key check information tra cứu danh mục",
            "Lookup (ô tra cứu) của 1 field = thẻ <items style=\"AutoComplete\"> trong khối <field>: controller = tên controller Lookup (Customer, Item…), reference = field tên hiện cạnh (ten_kh%l, khai thêm field external=\"true\"), key = điều kiện lọc danh sách, check = điều kiện kiểm tra khi gõ, information = mã$bảng.tên để hiện thông tin. Hỏi \"field ma_kh khai báo lookup thế nào\" để xem cách khai thật hay gặp nhất.",
            "<field name=\"ma_kh\" width=\"100\" aliasName=\"a\">\n  <header v=\"Mã khách\" e=\"Customer\"></header>\n  <items style=\"AutoComplete\" controller=\"Customer\" reference=\"ten_kh%l\" key=\"status = '1'\" check=\"1 = 1\" information=\"ma_kh$dmkh.ten_kh%l\"/>\n</field>\n<field name=\"ten_kh%l\" readOnly=\"true\" external=\"true\" defaultValue=\"''\" hidden=\"true\">\n  <header v=\"\" e=\"\"></header>\n</field>"),
        new("topic", "entity", "cách khai báo entity include file .ent SYSTEM", "DOCTYPE ENTITY % SYSTEM include dùng lại đoạn code",
            "Entity khai ở <!DOCTYPE … [ ]> đầu file. Entity thường (&Ten;) thay bằng chữ: <!ENTITY Ten \"…\"> hoặc lấy từ file: <!ENTITY Ten SYSTEM \"..\\Include\\X.txt\">. Entity tham số (%Ten;) nạp cả 1 file .ent chứa nhiều khai báo: <!ENTITY % Ten SYSTEM \"..\\Include\\X.ent\"> rồi gọi %Ten; ngay sau. Đường dẫn tính từ thư mục controller (Grid, Dir…). F12 trên &Ten; để mở chỗ khai.",
            "<!DOCTYPE grid [\n  <!ENTITY % DetailTax SYSTEM \"..\\Include\\DetailTax.ent\">\n  %DetailTax;\n  <!ENTITY XMLStandardDetailToolbar SYSTEM \"..\\Include\\XML\\StandardDetailToolbar.xml\">\n  <!ENTITY VisibleFieldController \"AIDetail\">\n]>"),
        new("topic", "clientscript", "cách viết clientScript sự kiện onchange của field", "clientScript CDATA onchange onblur javascript field",
            "Sự kiện phía trình duyệt của 1 field: <clientScript><![CDATA[onchange=\"tênHàm(this);\"]]></clientScript> trong khối <field>; hàm viết trong <script><text><![CDATA[ … ]]></text></script> của controller. Lấy / gán giá trị: f.getItemValue('ma_kh'), f.setItemValue('ma_kh', v); trên lưới: g._getItemValue(o.row, 'field').",
            "<clientScript><![CDATA[onchange=\"onChange$GridAIDetail$ItemType(this);\"]]></clientScript>"),
        new("topic", "toolbar", "cách khai báo toolbar nút button", "toolbar button command New Edit Delete Search Export Freeze separator",
            "Thanh nút của controller: <toolbar> gồm <button command=\"New|Edit|Delete|View|Search|Export|Freeze|…\"> (command=\"-\" = vạch ngăn); controller chi tiết thường dùng entity chung &XMLStandardDetailToolbar;. Hỏi \"nút Export khai thế nào\" để xem cách khai hay gặp.",
            "<toolbar>\n  <button command=\"New\"/>\n  <button command=\"Edit\"/>\n  <button command=\"-\"/>\n  <button command=\"Export\"/>\n</toolbar>"),
        new("topic", "partition", "bảng phân kỳ partition khai báo", "partition table prime inquiry field ngay_ct expression increase bảng phân kỳ $yyyymm",
            "Bảng phân kỳ (xxx$yyyymm): <partition table=\"…\" prime=\"…\" inquiry=\"…\" field=\"ngay_ct\" …/> ngay sau <title> của Grid / Dir chứng từ — prime là bảng gốc, inquiry là bảng tra cứu, field là cột ngày để chọn kỳ.",
            "<partition table=\"cthddc\" prime=\"cthddc\" inquiry=\"ihddc\" field=\"ngay_ct\" expression=\"''\" increase=\"{0}\" default=\"\"/>"),
        new("topic", "command", "command event sự kiện Loading Showing Closing response action query", "commands command event response action query SQL",
            "SQL phía máy chủ: <commands><command event=\"Loading|Showing|Closing|Inserting|Updating|Deleting…\"><text><![CDATA[ … ]]></text></command></commands>; hàm gọi từ JS (f.request) nằm ở <response><action id=\"…\">; câu lấy dữ liệu lưới ở <query event=\"Loading\">.",
            "<command event=\"Loading\">\n  <text>\n    <![CDATA[\n    ]]>\n  </text>\n</command>"),
        new("topic", "import", "cách khai báo import danh mục chứng từ từ excel upload", "import upload setting type startRow baseTable listTable dependence alias field column resource check errorCode var nhập excel clipboard",
            "Nhập danh mục / chứng từ từ Excel = file App_Data\\Controllers\\Templates\\Upload\\<X>.xml (gốc <import>), mẫu Excel ở Templates\\Excel. <setting>: type (List = danh mục, Voucher = chứng từ, Other), startRow (dòng dữ liệu đầu tiên của file Excel), baseTable (bảng ghi vào), listTable / temporary (bảng tạm #…), dependence + alias (master, detail, inquiry…), allowFileExtension, onProcessComplete / onProcessFail (hàm JS của form import). <variable><var id=\"C001\"><header v e/> = thông báo lỗi. Mỗi cột Excel = <field name column=\".\" …/>: allowNulls, upperCase, type, resource (bảng nhận: master / detail / inquiry), reference, check = điều kiện SAI (nhiều điều kiện nối bằng &And;) kèm errorCode cùng thứ tự. Danh sách field dài thường để ở Upload\\Include\\<X>Fields.txt rồi gọi bằng entity. Dán từ Excel vào lưới = Templates\\Clipboard\\<X>.xml (gốc <clipboard>, <field defaultValue> + <check generate=\"true\"/>). Hỏi \"import SVTran khai thế nào\", \"field import ma_kh kiểm tra gì\" để xem khai báo thật.",
            "<import xmlns=\"urn:schemas-fast-com:data-import\">\n  <setting>\n    <type value=\"List\"/>\n    <startRow value=\"6\"/>\n    <allowFileExtension value=\"^(.XLSX)$\"/>\n    <baseTable value=\"dmxx\"/>\n    <listTable value=\"#m1\"/>\n    <alias value=\"master\"/>\n  </setting>\n  <fields identity=\"true\" name=\"stt\">\n    <field name=\"ma_xx\" column=\".\" allowNulls=\"false\" upperCase=\"true\" resource=\"master\" check=\"ma_xx in (select ma_xx from dmxx)\" errorCode=\"00001\"/>\n    <field name=\"ten_xx\" column=\".\" resource=\"master\"/>\n  </fields>\n</import>"),
        new("topic", "views","dòng giao diện view item value mặt nạ", "views view item value mặt nạ [field].Label category",
            "Bố cục Dir: <views><view id=\"…\"><item value=\"110: [ma_kh].Label, [ma_kh]\"/> — chữ số đầu là mặt nạ độ rộng / vị trí, [field] là ô, [field].Label là nhãn. Alt+P xem trước màn hình.",
            "<item value=\"110: [ma_kh].Label, [ma_kh]\"/>"),
    };

    // ---------------------------------------------------------------- ghi / đọc tài liệu đi kèm

    public static string ToJson(IEnumerable<AskDoc> docs) => JsonSerializer.Serialize(docs, ViewerKnowledge.Json);
    public static List<AskDoc> FromJson(string json) => JsonSerializer.Deserialize<List<AskDoc>>(json) ?? new();
}
