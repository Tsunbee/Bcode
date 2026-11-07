using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.UI;

namespace Bcode.App.Services;

/// <summary>
/// Kiến thức về CHÍNH Bcode cho Hỏi Fast: "menu X ở đâu", "muốn làm Y thì mở gì", hỏi chung chung ("so sánh object", "sơ đồ phụ thuộc").
/// Nguồn — lấy từ code / tài liệu thật để không lệch khi Bcode thêm tính năng:
/// <list type="number">
/// <item>Mục sống do MainForm cấp (<see cref="LiveProvider"/>): nút công cụ <c>_toolSpecs</c> kèm chỗ đang nằm (thanh công cụ / nhóm / Triển khai ▾ /
/// Tool mở rộng ▾ / thanh trên / đang ẩn), mục menu ⚙ Settings và Actions ▾. Nút "Mở ngay" chạy đúng hành động đó (<see cref="Opener"/>).</item>
/// <item>Bảng phím tắt <see cref="ShortcutRegistry"/>: lệnh phạm vi App (mở được) + phím theo vùng (editor SQL, lưới Table, cây menu…).</item>
/// <item>Tài liệu Trợ giúp <c>Help\HuongDanSuDung.md</c> (cạnh exe): mỗi mục ## / ### và mỗi dòng bảng có tên in đậm ở ô đầu → mô tả; nút mở Trợ giúp đúng mục.</item>
/// <item><c>Templates\fastask\bcode-features.json</c>: từ đồng nghĩa / viết tắt, từ khoá + mô tả thêm cho từng mục (id "tool:key", "sc:id", "menu:…"),
/// vài chức năng chỉ có ở menu chuột phải. Chỉ để hiểu câu hỏi tốt hơn — thiếu mục nào thì mục đó vẫn có mặt từ nguồn 1–3.</item>
/// </list>
/// </summary>
public static class BcodeFeatureCatalog
{
    /// <summary>1 mục MainForm báo lên: id mở được ("tool:key", "menu:settings:…", "menu:actions:…"), tên, chỗ nằm, phím tắt, loại (tool / menu).</summary>
    public sealed record LiveItem(string Id, string Title, string Where, string Shortcut, string Kind, string How = "");

    /// <summary>1 chức năng: id, tên, ở đâu, cách mở, mô tả, từ khoá (không dấu), id để mở ("" = không mở được), mục Trợ giúp, loại.</summary>
    public sealed record Feature(string Id, string Title, string Where, string How, string Desc, string Keys, string OpenId, string Help, string Kind);

    /// <summary>MainForm gán: danh sách mục sống (tự chuyển về luồng UI).</summary>
    public static Func<IReadOnlyList<LiveItem>>? LiveProvider { get; set; }

    /// <summary>MainForm gán: mở theo id ("tool:…", "sc:…", "menu:…", "help:…") — true = đã mở.</summary>
    public static Func<string, bool>? Opener { get; set; }

    public static string HelpPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Help", "HuongDanSuDung.md");
    public static string CuratedPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "Templates", "fastask", "bcode-features.json");

    public const string Source = "Chức năng Bcode";
    public const string HelpSource = "Trợ giúp Bcode";

    // ---------------------------------------------------------------- dữ liệu đã dựng

    private sealed class Catalog
    {
        public required List<Feature> Items;
        public required FastAskIndex Index;
        public required Dictionary<string, Feature> ById;
        public required List<string[]> Synonyms;
        public required string Stamp;
        public DateTime At;
    }

    private sealed record Curated(string Id, string Title, string Where, string How, string Desc, string Keys, string Open, string Help);

    private static Catalog? _cache;
    private static readonly object Gate = new();

    private static string FileStamp(string p) { try { return File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks.ToString() : "0"; } catch { return "0"; } }

    private static Catalog Get()
    {
        var stamp = FileStamp(HelpPath) + "|" + FileStamp(CuratedPath);
        lock (Gate)
        {
            // mục sống (vị trí nút, menu) đổi khi người dùng sắp lại thanh công cụ → dựng lại sau 30 giây
            if (_cache is { } c && c.Stamp == stamp && (DateTime.UtcNow - c.At).TotalSeconds < 30) return c;
        }
        var built = Build(stamp);
        lock (Gate) _cache = built;
        return built;
    }

    /// <summary>Bỏ bộ nhớ (vd sau khi đổi thanh công cụ / Template) — lần hỏi sau dựng lại.</summary>
    public static void Invalidate() { lock (Gate) _cache = null; }

    private static Catalog Build(string stamp)
    {
        var (syn, curated, ignore) = LoadCurated();
        var items = new List<Feature>();
        var byTitle = new Dictionary<string, int>(StringComparer.Ordinal);   // tên không dấu → vị trí (gộp dòng bảng Trợ giúp vào nút cùng tên)
        var help = ParseHelp();

        void AddItem(Feature f)
        {
            items.Add(f);
            byTitle.TryAdd(Key(f.Title), items.Count - 1);
        }

        // 1. mục sống: nút công cụ + menu Settings / Actions
        IReadOnlyList<LiveItem> live = Array.Empty<LiveItem>();
        try { live = LiveProvider?.Invoke() ?? Array.Empty<LiveItem>(); } catch { /* cửa sổ đang đóng */ }
        var toolTitles = new HashSet<string>(live.Where(l => l.Kind == "tool").Select(l => Key(l.Title)), StringComparer.Ordinal);
        foreach (var l in live)
        {
            var leaf = l.Kind == "menu" ? Leaf(l.Title) : l.Title;
            if (ignore.Contains(Key(leaf))) continue;
            if (l.Kind != "tool" && toolTitles.Contains(Key(leaf))) continue;   // Actions → Tools ▸ lặp lại nút công cụ
            curated.TryGetValue(l.Id, out var cu);
            var row = help.Rows.FirstOrDefault(r => Key(r.Title) == Key(leaf));
            var how = l.How.Length > 0 ? l.How : HowFor(l);
            AddItem(new Feature(l.Id, cu?.Title is { Length: > 0 } t ? t : l.Title, l.Where, how,
                Join(cu?.Desc, row?.Desc), cu?.Keys ?? "", l.Id, cu?.Help is { Length: > 0 } h ? h : row?.Anchor ?? "", l.Kind));
        }

        // 2. phím tắt: phạm vi App (mở được bằng RunAppShortcut) + theo vùng (chỉ chỉ phím)
        foreach (var d in ShortcutRegistry.All)
        {
            if (d.Id.StartsWith("tool:", StringComparison.Ordinal) || Regex.IsMatch(d.Id, @"^(tab\.goto|bar\.group|editor\.barGroup)\d")) continue;
            if (ignore.Contains(Key(d.Text))) continue;
            var id = "sc:" + d.Id;
            curated.TryGetValue(id, out var cu);
            var combo = ShortcutRegistry.Display(d.Id);
            var app = d.Scope == ShortcutScope.App;
            var where = cu?.Where is { Length: > 0 } w ? w : WhereForGroup(d.Group, d.Scope);
            var how = cu?.How is { Length: > 0 } hw ? hw
                : combo.Length > 0 ? $"Phím {combo}" + (app ? $" · hoặc {PaletteKey()} gõ \"{Short(d.Text)}\"" : "")
                : app ? $"{PaletteKey()} gõ \"{Short(d.Text)}\" (chưa gán phím — gán ở Template → Phím tắt)" : "Chưa gán phím — gán ở Template → Phím tắt";
            AddItem(new Feature(id, cu?.Title is { Length: > 0 } t ? t : d.Text, where, how, cu?.Desc ?? "", cu?.Keys ?? "",
                app ? id : "", cu?.Help ?? "", "shortcut"));
        }

        // 3. mục chỉ có trong JSON (menu chuột phải…): mở được nếu trỏ tới id có thật
        var known = new HashSet<string>(items.Select(i => i.Id), StringComparer.Ordinal);
        foreach (var cu in curated.Values.Where(c => !known.Contains(c.Id) && !c.Id.StartsWith("tool:") && !c.Id.StartsWith("sc:") && !c.Id.StartsWith("menu:")))
        {
            var open = cu.Open.Length > 0 && (known.Contains(cu.Open) || cu.Open.StartsWith("help:")) ? cu.Open : "";
            AddItem(new Feature(cu.Id, cu.Title, cu.Where, cu.How, cu.Desc, cu.Keys, open, cu.Help, "context"));
        }

        // 4. Trợ giúp: dòng bảng chưa gộp vào nút nào + từng mục ## / ###
        var n = 0;
        foreach (var r in help.Rows)
        {
            if (byTitle.ContainsKey(Key(r.Title))) continue;
            AddItem(new Feature($"helprow:{n++}", r.Title, r.Part + (r.Section.Length > 0 ? " — " + r.Section : ""), r.How, r.Desc, "", r.Anchor.Length > 0 ? "help:" + r.Anchor : "", r.Anchor, "helprow"));
        }
        foreach (var s in help.Sections)
            AddItem(new Feature($"help:{s.Anchor}#{n++}", s.Title, $"Trợ giúp ({TrHelpKey()}) → mục \"{s.Title}\"" + (s.Part.Length > 0 ? $" ({s.Part})" : ""), "",
                s.Text, "", s.Anchor.Length > 0 ? "help:" + s.Anchor : "", s.Anchor, "help"));

        var ix = new FastAskIndex();
        foreach (var f in items)
        {
            var help0 = f.Kind is "help";
            ix.Add(new FastAskIndex.Doc(f.Kind, f.Id, f.Title, f.Keys + " " + (help0 ? Trunc(f.Desc, 900) : f.Desc)), help0 ? 2 : 3);
        }
        return new Catalog { Items = items, Index = ix, ById = items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First()), Synonyms = syn, Stamp = stamp, At = DateTime.UtcNow };
    }

    private static string HowFor(LiveItem l)
    {
        var name = l.Kind == "menu" ? Leaf(l.Title) : l.Title;
        var how = l.Kind == "tool" ? $"Bấm nút \"{name}\"" : $"Bấm mục \"{name}\"";
        if (l.Shortcut.Length > 0) how += $" · phím {l.Shortcut}";
        if (l.Kind == "tool") how += $" · hoặc {PaletteKey()} gõ \"{name}\" · hoặc Actions ▾ → Tools ▸";
        return how;
    }

    private static string PaletteKey() { var k = ShortcutRegistry.Display("app.palette"); return k.Length > 0 ? k : "Command Palette"; }
    private static string TrHelpKey() { var k = ShortcutRegistry.Display("tool:help"); return k.Length > 0 ? k : "Ctrl+F1"; }

    private static string WhereForGroup(string group, ShortcutScope scope) => group switch
    {
        "Editor SQL" => "Tab SQL Query — khi con trỏ ở editor SQL",
        "Debug từng bước" => "Tab SQL Query — Debug từng bước",
        "Lưới Table" => "Tab Table — khi con trỏ ở lưới dữ liệu",
        "Lưới kết quả SQL Query" => "Tab SQL Query — lưới kết quả (Result)",
        "Cây menu WCommand" => "Cây menu WCommand (cột trái) — chuột phải / phím khi đang ở cây",
        "Cây file File Lookup" => "Tab File Lookup — cây file",
        "Khung xem file" => "Khung xem file (File Lookup / F12)",
        "Khai báo API" => "Màn hình Khai báo API (Triển khai ▾ → Khai báo API)",
        "FSG FBO" => "Tab FSG FBO (trang web của dự án)",
        _ => scope == ShortcutScope.App ? $"Lệnh toàn cửa sổ — nhóm \"{group}\" (Template → Phím tắt)" : $"Nhóm phím \"{group}\"",
    };

    // ---------------------------------------------------------------- JSON bổ sung

    private static (List<string[]> Syn, Dictionary<string, Curated> Items, HashSet<string> Ignore) LoadCurated()
    {
        var syn = new List<string[]>();
        var items = new Dictionary<string, Curated>(StringComparer.Ordinal);
        var ignore = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(CuratedPath)) return (syn, items, ignore);
            using var doc = JsonDocument.Parse(File.ReadAllText(CuratedPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var r = doc.RootElement;
            if (r.TryGetProperty("synonyms", out var s))
                foreach (var g in s.EnumerateArray())
                    syn.Add(g.EnumerateArray().Select(x => Norm(x.GetString() ?? "")).Where(x => x.Trim().Length > 0).Distinct().ToArray());   // giữ dấu cách 2 đầu: so nguyên chữ
            if (r.TryGetProperty("ignore", out var ig))
                foreach (var x in ig.EnumerateArray()) ignore.Add(Key(x.GetString() ?? ""));
            if (r.TryGetProperty("features", out var fs))
                foreach (var f in fs.EnumerateArray())
                {
                    string P(string n) => f.TryGetProperty(n, out var v) ? v.ValueKind == JsonValueKind.Array ? string.Join(", ", v.EnumerateArray().Select(e => e.GetString())) : v.GetString() ?? "" : "";
                    var id = P("id");
                    if (id.Length > 0) items[id] = new Curated(id, P("title"), P("where"), P("how"), P("desc"), P("keys"), P("open"), P("help"));
                }
        }
        catch { /* JSON hỏng → chỉ dùng nguồn từ code + Trợ giúp */ }
        return (syn, items, ignore);
    }

    // ---------------------------------------------------------------- tài liệu Trợ giúp

    private sealed record HelpRow(string Title, string Desc, string How, string Anchor, string Section, string Part);
    private sealed record HelpSection(string Title, string Text, string Anchor, string Part);

    private static (List<HelpRow> Rows, List<HelpSection> Sections) ParseHelp()
    {
        var rows = new List<HelpRow>();
        var sections = new List<HelpSection>();
        string[] lines;
        try { lines = File.Exists(HelpPath) ? File.ReadAllLines(HelpPath) : Array.Empty<string>(); } catch { lines = Array.Empty<string>(); }
        string anchor = "", part = "", title = "", secAnchor = "";
        var text = new StringBuilder();
        string[]? header = null;
        void Flush()
        {
            if (title.Length > 0 && !Regex.IsMatch(Fold(title), @"^(phan i|phan ii|muc luc|phu luc)")) sections.Add(new HelpSection(title, Clean(text.ToString()), secAnchor, part));
            text.Clear();
        }
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (Regex.Match(line, @"<a id=""([^""]+)""") is { Success: true } am) { anchor = am.Groups[1].Value; continue; }
            if (Regex.Match(line, @"^#\s+(.+)") is { Success: true } pm)
            {
                Flush(); title = "";
                var pf = Fold(pm.Groups[1].Value);
                part = pf.StartsWith("phan ii") ? "BcodeViewer" : pf.StartsWith("phan i") ? "Bcode" : "";
                continue;
            }
            if (Regex.Match(line, @"^#{2,3}\s+(.+)") is { Success: true } hm) { Flush(); title = Clean(hm.Groups[1].Value); secAnchor = anchor; header = null; continue; }
            if (line.StartsWith("|"))
            {
                var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (i + 1 < lines.Length && Regex.IsMatch(lines[i + 1], @"^\|\s*:?-{2,}")) { header = cells.Select(Fold).ToArray(); continue; }
                if (Regex.IsMatch(line, @"^\|\s*:?-{2,}")) continue;
                if (Regex.Match(cells[0], @"^\*\*([^*]+)\*\*") is { Success: true } bm && cells.Length >= 2)
                {
                    var how = "";
                    var desc = new List<string>();
                    for (var c = 1; c < cells.Length; c++)
                    {
                        var h = header is not null && c < header.Length ? header[c] : "";
                        if (Regex.IsMatch(h, @"^(mo|cach mo|phim)\b")) how = Clean(cells[c]); else desc.Add(Clean(cells[c]));
                    }
                    // "**Triển khai ▾**, **Tool mở rộng ▾**" → mỗi tên 1 dòng
                    foreach (Match nm in Regex.Matches(cells[0], @"\*\*([^*]+)\*\*"))
                        rows.Add(new HelpRow(Clean(nm.Groups[1].Value), string.Join(" — ", desc.Where(d => d.Length > 0)), how, anchor, title, part.Length > 0 ? part : "Bcode"));
                }
            }
            text.AppendLine(line);
        }
        Flush();
        return (rows, sections);
    }

    /// <summary>Markdown → chữ thường: bỏ ảnh, "Trên ảnh: …", link giữ chữ, bỏ ** ` và thẻ HTML.</summary>
    private static string Clean(string md)
    {
        var s = Regex.Replace(md, @"!\[[^\]]*\]\([^)]*\)", "");
        s = Regex.Replace(s, @"(?m)^Trên ảnh:.*$", "");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = s.Replace("**", "").Replace("`", "").Replace("\\|", "|");
        s = Regex.Replace(s, @"(?m)^\s*(\|.*\|)\s*$", m => Regex.IsMatch(m.Value, @"^\s*\|\s*:?-{2,}") ? "" : m.Groups[1].Value.Trim('|').Replace(" | ", " — "));
        s = Regex.Replace(s, @"(?m)^\s*>\s*", "");
        s = Regex.Replace(s, @"[ \t]+", " ");
        return Regex.Replace(s, @"\n\s*\n+", "\n").Trim();
    }

    // ---------------------------------------------------------------- hiểu câu hỏi

    public static string Fold(string s) => FastAskService.Fold(s);

    /// <summary>Không dấu, chỉ chữ / số, cách nhau 1 dấu cách, có cách ở 2 đầu — để so cụm từ nguyên chữ bằng Contains(" cụm ").</summary>
    private static string Norm(string s) => " " + Regex.Replace(Fold(s), @"[^a-z0-9#$]+", " ").Trim() + " ";

    private static string Key(string s) => Norm(s).Trim();

    private static string Leaf(string path) { var i = path.LastIndexOf(" → ", StringComparison.Ordinal); return i < 0 ? path : path[(i + 3)..]; }

    /// <summary>Câu hỏi rõ là hỏi chỗ / cách mở 1 chức năng của Bcode.</summary>
    public static bool IsExplicit(string folded) =>
        Regex.IsMatch(folded, @"\bbcode\b|\b(o|nam|nam o|tim o) ?(dau|cho nao)\b|\bcho nao\b|\b(mo|bat|vao|dung|chon|bam|an) (gi|cai gi|chuc nang nao|tinh nang nao|menu nao|tab nao|tool nao|cong cu nao|nut nao|o dau|the nao|ra sao)\b"
                                + @"|\b(chuc nang|tinh nang|menu|nut|tool|cong cu|phim tat)\b.*\b(nao|o dau|dau|gi)\b|\b(lam sao|lam the nao|cach nao|the nao|bang cach nao)\b.*\b(mo|bat|goi|dung)\b|\bphim tat\b|\bnut nao\b");

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "o", "dau", "nam", "cho", "nao", "the", "sao", "lam", "muon", "can", "minh", "toi", "em", "anh", "bcode", "menu", "chuc", "nang", "tinh", "nut", "mo", "bat", "vao",
        "gi", "thi", "de", "hay", "giup", "nhe", "vay", "a", "ha", "cai", "phai", "duoc", "xem", "cach", "co", "khong", "ko", "la", "va", "cua", "trong", "voi", "tool", "cong", "cu",
        "tab", "bam", "an", "chon", "ra", "nhu", "nhung", "cac", "mot", "nay", "kia", "do", "dang", "su", "ai", "biet", "hoi", "oi", "ban", "chi", "giu", "len", "di", "toi", "dc",
        "tim", "o dau", "the nao", "lam sao", "phim", "tat", "ben", "trai", "phai", "tren", "duoi", "dung",
    };

    /// <summary>Khái niệm của câu hỏi: nhóm đồng nghĩa khớp (cả cụm) + từng từ còn lại (bỏ từ hỏi chung).</summary>
    private static List<string[]> Concepts(string text, List<string[]> syn, out string expanded)
    {
        var q = Norm(text);
        var res = new List<string[]>();
        var extra = new StringBuilder();
        // cụm dài trước ("duoc dung o dau" trước "dung")
        foreach (var (g, p) in syn.SelectMany(g => g.Select(p => (g, p))).OrderByDescending(x => x.p.Length))
        {
            if (!q.Contains(p)) continue;
            q = q.Replace(p, " | ");
            if (!res.Contains(g)) { res.Add(g); extra.Append(' ').Append(string.Join(" ", g)); }
        }
        foreach (var w in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            if (w != "|" && !Noise.Contains(w) && (w.Length > 2 || char.IsDigit(w[0])) && res.All(c => !(c.Length == 1 && c[0] == " " + w + " "))) res.Add(new[] { " " + w + " " });
        expanded = text + extra;
        return res;
    }

    /// <summary>Tỉ lệ khái niệm có trong chữ của chức năng (cụm nguyên chữ).</summary>
    private static double Coverage(List<string[]> concepts, string normText) =>
        concepts.Count == 0 ? 0 : (double)concepts.Count(c => c.Any(p => normText.Contains(p))) / concepts.Count;

    /// <summary>Kết quả tra: các chức năng xếp hạng + độ khớp của mục đầu (tên + từ khoá) / (cả mô tả).</summary>
    public sealed record FindResult(List<Feature> Hits, double TitleCoverage, double FullCoverage, int ConceptCount);

    public static FindResult Find(string text, int top = 4)
    {
        var cat = Get();
        var concepts = Concepts(text, cat.Synonyms, out var expanded);
        if (concepts.Count == 0) return new FindResult(new(), 0, 0, 0);
        var hits = cat.Index.Search(expanded, 30);
        if (hits.Count == 0) return new FindResult(new(), 0, 0, concepts.Count);
        var best = hits[0].Score;
        var ranked = hits
            .Select(h => cat.ById.TryGetValue(h.Doc.Key, out var f) ? f : null).OfType<Feature>()
            .Select(f =>
            {
                var tk = Norm(f.Title + " " + f.Keys);
                var tc = Coverage(concepts, tk);
                var fc = Coverage(concepts, Norm(f.Title + " " + f.Keys + " " + f.Desc));
                var bm = hits.First(h => h.Doc.Key == f.Id).Score / best;
                // tên / từ khoá khớp trọn câu quan trọng hơn điểm BM25; mục mở được (nút, menu, phím) hơn đoạn Trợ giúp
                var s = 0.35 * bm + 0.45 * tc + 0.2 * fc + (f.Kind is "help" ? -0.08 : f.Kind is "tool" or "context" ? 0.05 : 0) + (f.OpenId.Length > 0 ? 0.03 : 0);
                return (f, s, tc, fc);
            })
            .OrderByDescending(x => x.s).ToList();
        var minFc = Math.Max(0.5, ranked[0].fc * 0.75);   // thẻ phụ phải khớp gần bằng thẻ đầu
        var list = ranked.Where((x, i) => i == 0 || x.fc >= minFc).Select(x => x.f).DistinctBy(f => Key(f.Title)).Take(top).ToList();
        return new FindResult(list, ranked[0].tc, ranked[0].fc, concepts.Count);
    }

    // ---------------------------------------------------------------- trả lời

    public static FastAskService.AskCard Card(Feature f)
    {
        var text = new StringBuilder();
        if (f.Where.Length > 0) text.Append("Ở đâu: ").Append(f.Where);
        if (f.How.Length > 0) text.Append(text.Length > 0 ? "\n" : "").Append("Cách mở: ").Append(f.How);
        if (f.Desc.Length > 0) text.Append(text.Length > 0 ? "\n" : "").Append(Trunc(f.Desc, f.Kind is "help" ? 420 : 360));
        var actions = new List<FastAskService.AskAction>();
        if (f.OpenId.Length > 0 && !f.OpenId.StartsWith("help:")) actions.Add(new("bcode-open", "▶ Mở ngay", f.OpenId));
        if (f.Help.Length > 0) actions.Add(new("bcode-open", "📖 Xem hướng dẫn", "help:" + f.Help));
        else if (f.OpenId.StartsWith("help:")) actions.Add(new("bcode-open", "📖 Mở Trợ giúp mục này", f.OpenId));
        return new FastAskService.AskCard(f.Title, text.ToString(), "", actions, f.Kind is "help" or "helprow" ? HelpSource : Source) { Ref = "bcode|" + f.Id };
    }

    /// <summary>Trả lời câu hỏi về Bcode nếu đủ chắc: hỏi rõ chỗ / cách mở (≥ 50% khái niệm khớp) hoặc câu gần như trùng tên / từ khoá 1 chức năng (≥ 75%).
    /// <paramref name="allowImplicit"/> = false (vd câu đang vào luồng tạo báo cáo của Fast) thì chỉ nhận câu hỏi rõ. null = để Hỏi Fast tra tiếp.</summary>
    public static FastAskService.AskReply? Answer(string text, bool allowImplicit = true)
    {
        var f = Fold(text);
        var explicitAsk = IsExplicit(f);
        if (explicitAsk && Regex.IsMatch(f, @"\bbcode\b") && Regex.IsMatch(f, @"\b(nhung|cac|tat ca|danh sach|liet ke|gom|lam duoc)\b.*\b(chuc nang|tinh nang|gi|nao|tool|cong cu)\b|\bco gi\b"))
            return Overview();
        var m = Find(text);
        if (m.Hits.Count == 0) return null;
        var ok = explicitAsk ? Math.Max(m.TitleCoverage, m.FullCoverage) >= 0.5 : allowImplicit && m.TitleCoverage >= 0.75 && m.ConceptCount <= 5;
        if (!ok) return null;
        var cards = m.Hits.Take(explicitAsk ? 4 : 3).Select(Card).ToList();
        var lead = m.Hits[0].OpenId.Length > 0 && m.Hits[0].Kind is not ("help" or "helprow")
            ? $"Trong Bcode: \"{m.Hits[0].Title}\" — {m.Hits[0].Where}. Bấm ▶ Mở ngay để mở."
            : "Chức năng Bcode liên quan:";
        return new FastAskService.AskReply(lead, cards, new List<FastAskService.AskChoice> { new("Bcode có những chức năng gì", "bcode có những chức năng gì") }, new());
    }

    /// <summary>Thẻ chức năng Bcode thêm vào cuối 1 câu trả lời Fast khi câu hỏi khớp khá rõ 1 chức năng (vd "tạo báo cáo" → luồng Fast + nút Tạo báo cáo của Bcode).</summary>
    public static FastAskService.AskCard? Suggest(string text)
    {
        var m = Find(text, 1);
        return m.Hits.Count > 0 && m.TitleCoverage >= 0.6 && m.Hits[0].Kind is not ("help" or "helprow") ? Card(m.Hits[0]) : null;
    }

    /// <summary>Câu Hỏi Fast không tìm ra gì: thử đoạn Trợ giúp / chức năng khớp ≥ 50%.</summary>
    public static FastAskService.AskReply? Fallback(string text)
    {
        var m = Find(text, 3);
        if (m.Hits.Count == 0 || Math.Max(m.TitleCoverage, m.FullCoverage) < 0.5) return null;
        return new FastAskService.AskReply("Không thấy trong kiến thức Fast — trong Bcode có:", m.Hits.Select(Card).ToList(), Array.Empty<FastAskService.AskChoice>(), new());
    }

    /// <summary>"Bcode có những chức năng gì": nút công cụ / menu theo chỗ nằm.</summary>
    public static FastAskService.AskReply Overview()
    {
        var cat = Get();
        var cards = cat.Items.Where(i => i.Kind == "tool").GroupBy(i => i.Where)
            .Select(g => new FastAskService.AskCard(g.Key, string.Join(" · ", g.Select(i => i.Title)), "", Array.Empty<FastAskService.AskAction>(), Source) { Ref = "bcode|overview|" + g.Key })
            .ToList();
        cards.Add(new FastAskService.AskCard("Tìm nhanh mọi thứ", $"{PaletteKey()} (Command Palette): gõ tên công cụ, lệnh, menu WCommand, object SQL, snippet. Trợ giúp: {TrHelpKey()}.", "",
            Opener is null ? Array.Empty<FastAskService.AskAction>() : new List<FastAskService.AskAction> { new("bcode-open", "▶ Mở Command Palette", "sc:app.palette"), new("bcode-open", "📖 Trợ giúp", "help:khung-chinh") }, Source));
        return new FastAskService.AskReply($"Bcode có {cat.Items.Count(i => i.Kind == "tool")} công cụ, nằm ở:", cards,
            new List<FastAskService.AskChoice> { new("Menu tra ảnh lỗi ở đâu", "menu tra ảnh lỗi ở đâu"), new("Xem object được dùng ở đâu", "xem procedure được dùng ở đâu") }, new());
    }

    /// <summary>Mở chức năng theo id (nút ▶ Mở ngay / 📖 trên thẻ).</summary>
    public static bool Open(string id) => Opener?.Invoke(id) ?? false;

    // ---------------------------------------------------------------- tiện ích

    private static string Join(params string?[] parts) => string.Join("\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()).Distinct());
    private static string Trunc(string s, int n)
    {
        if (s.Length <= n) return s;
        var cut = s.LastIndexOf(' ', n);
        return s[..(cut > 40 ? cut : n)].TrimEnd(',', ';', ' ') + " …";
    }
    private static string Short(string s) { var i = s.IndexOfAny(new[] { '(', '—', ':' }); var t = (i > 3 ? s[..i] : s).Trim(); return t.Length > 40 ? t[..40] : t; }

    /// <summary>Số liệu để harness / log: tổng số mục theo loại.</summary>
    public static string Stats() { var c = Get(); return string.Join(", ", c.Items.GroupBy(i => i.Kind).Select(g => $"{g.Key} {g.Count()}")); }
}
