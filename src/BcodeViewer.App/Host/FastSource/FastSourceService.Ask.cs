using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BcodeViewer.App.Host.FastSource;

/// <summary>Hỏi Fast của BcodeViewer: dựng kho tìm kiếm theo lớp (câu trả lời người dùng đã sửa → source dự án → source Fast mới nhất → lịch sử
/// phiên bản → mẫu đi kèm), trả thẻ trả lời, nhận ✔ / ✘ / ✎ Sửa, lưu lịch sử phiên chat.</summary>
public sealed partial class FastSourceService
{
    private List<AskDoc>? _shippedDocs;
    private List<AskDoc>? _fastDocs;
    private readonly ConcurrentDictionary<string, List<AskDoc>> _projectDocs = new(StringComparer.OrdinalIgnoreCase);
    private (string Project, SourceAsk Ask)? _askCache;
    private readonly object _askLock = new();

    public static string ShippedAskPath => Path.Combine(AppContext.BaseDirectory, "Assets", "fastsource", "ask.json");
    private static string AskFile(string name) => ViewerKnowledge.Viewer("ask", name + "." + Format + ".json");
    private static string FeedbackFile => ViewerKnowledge.Viewer("ask", "feedback.json");

    // ---------------------------------------------------------------- xác nhận ✔ / ✘ / ✎

    public sealed class Feedback
    {
        public List<Vote> Votes { get; set; } = new();
        public List<Custom> Customs { get; set; } = new();
        public sealed record Vote(string Q, string Doc, int V, DateTime At);
        public sealed record Custom(string Q, string Text, string Code, DateTime At);
    }

    private static Feedback LoadFeedback()
    {
        try { return File.Exists(FeedbackFile) ? JsonSerializer.Deserialize<Feedback>(File.ReadAllText(FeedbackFile)) ?? new() : new(); }
        catch { return new(); }
    }

    /// <summary>Câu hỏi chuẩn hoá để so (không dấu, bỏ dấu câu, gộp khoảng trắng).</summary>
    public static string NormQ(string q) => Regex.Replace(Regex.Replace(AskIndex.Fold(q), @"[^\w%$.]+", " "), @"\s+", " ").Trim();

    private static bool SameQ(string a, string b)
    {
        if (a == b) return true;
        var x = a.Split(' ').ToHashSet(); var y = b.Split(' ').ToHashSet();
        var inter = x.Intersect(y).Count();
        return inter > 0 && inter / (double)x.Union(y).Count() >= 0.75;
    }

    /// <summary>✔ (+1) / ✘ (-1) cho 1 thẻ của 1 câu hỏi; đọc lại file trước khi ghi (máy khác cùng thư mục kiến thức có thể vừa ghi).</summary>
    public void Vote(string question, string docKey, int vote)
    {
        lock (_askLock)
        {
            var f = LoadFeedback();
            var q = NormQ(question);
            f.Votes.RemoveAll(v => v.Q == q && v.Doc == docKey);
            f.Votes.Add(new Feedback.Vote(q, docKey, Math.Sign(vote), DateTime.Now));
            ViewerKnowledge.WriteAtomic(FeedbackFile, JsonSerializer.Serialize(f, ViewerKnowledge.Json));
        }
    }

    /// <summary>✎ Sửa: câu trả lời đúng do người dùng gõ — hiện đầu tiên cho câu hỏi giống.</summary>
    public void Correct(string question, string text, string code)
    {
        lock (_askLock)
        {
            var f = LoadFeedback();
            var q = NormQ(question);
            f.Customs.RemoveAll(c => c.Q == q);
            if (text.Trim().Length > 0 || code.Trim().Length > 0) f.Customs.Add(new Feedback.Custom(q, text.Trim(), code.TrimEnd(), DateTime.Now));
            ViewerKnowledge.WriteAtomic(FeedbackFile, JsonSerializer.Serialize(f, ViewerKnowledge.Json));
        }
    }

    // ---------------------------------------------------------------- kho tìm kiếm

    private SourceAsk AskFor(string? project)
    {
        lock (_askLock)
        {
            if (_askCache is { } c && c.Project == (project ?? "")) return c.Ask;
            _shippedDocs ??= File.Exists(ShippedAskPath) ? SourceAsk.FromJson(File.ReadAllText(ShippedAskPath)) : new();
            var ask = new SourceAsk();
            var pd = project is not null && _projectDocs.TryGetValue(project, out var d) ? d : new List<AskDoc>();
            // controller của dự án thắng (đúng bản đang sửa); tài liệu gộp (field / entity / lookup / hàm JS…) lấy từ nguồn lớn trước —
            // thử trong Viewer: dự án 1 file làm thẻ "entity DetailTax khai ở 1 chỗ" che mất 67 controller của mẫu Fast
            ask.AddRange(pd.Where(x => x.Kind == "controller"));
            if (_fastDocs is not null) ask.AddRange(_fastDocs);
            if (Versions is not null) ask.AddRange(SourceAsk.BuildVersions(Versions));
            ask.AddRange(SourceAsk.Topics());
            ask.AddRange(_shippedDocs);
            ask.AddRange(pd.Where(x => x.Kind != "controller"));
            _askCache = (project ?? "", ask);
            return ask;
        }
    }

    private static object Card(AskDoc d, string question, double score)
    {
        var text = d.Text;
        // câu hỏi nêu 1 bản (SP22.5) → thẻ lịch sử chỉ giữ các dòng của bản đó
        if (d.Kind == "version" && SourceAsk.VersionOf(question) is { } ver)
        {
            var lines = text.Split('\n');
            var keep = lines.Skip(1).Where(l => l.StartsWith("■ " + ver + " ", StringComparison.OrdinalIgnoreCase)).ToList();
            if (keep.Count > 0) text = lines[0] + "\n" + string.Join("\n", keep);
            else text = lines[0] + $"\n(Không thấy thay đổi nào ở {ver} — các bản có sửa ở dòng trên.)";
        }
        return new { kind = d.Kind, key = d.Kind + ":" + d.Key, title = TitleOf(d), text, code = d.Code, path = d.Path, source = d.Source, score = Math.Round(score, 2) };
    }

    private static string TitleOf(AskDoc d) => d.Kind switch
    {
        "controller" => "Controller " + d.Key,
        "field" => "Field " + d.Key,
        "lookup" => "Lookup " + d.Key,
        "entity" => "Entity " + d.Key,
        "include" => "Include " + d.Key,
        "func" => "Hàm JS " + d.Key,
        "button" => "Nút toolbar " + d.Key,
        "import" => (d.Key.StartsWith("Clipboard/") ? "Clipboard " : "Import ") + d.Key,
        "importfield" => "Field import " + d.Key,
        "version" => "Lịch sử sửa " + d.Key,
        "release" => "Bản " + d.Key,
        "topic" => d.Title.Length > 0 ? char.ToUpper(d.Title[0]) + d.Title[1..] : d.Key,
        _ => d.Key,
    };

    /// <summary>Trả lời 1 câu hỏi: thẻ đầu + tối đa 4 kết quả khác. "… này" (field này, controller này) = file đang mở.</summary>
    public string Ask(string question, string? activePath)
    {
        var (project, sourcePath) = SafeResolve(activePath);
        // hỏi trước khi gõ (chưa có ghost nào kích việc học source dự án) → bắt đầu học luôn
        if (project is not null && sourcePath is not null) StartProjectLearn(project, sourcePath);
        var ask = AskFor(project);
        var q = question;
        if (activePath is { Length: > 0 } && Regex.IsMatch(AskIndex.Fold(q), @"\b(nay|file nay|man hinh nay)\b"))
            q += " " + Path.GetFileNameWithoutExtension(activePath);
        var hits = ask.Search(q, 8);
        var fb = LoadFeedback();
        var nq = NormQ(question);
        var down = fb.Votes.Where(v => v.V < 0 && SameQ(v.Q, nq)).Select(v => v.Doc).ToHashSet();
        var up = fb.Votes.Where(v => v.V > 0 && SameQ(v.Q, nq)).Select(v => v.Doc).ToHashSet();
        hits = hits.Where(h => !down.Contains(h.Doc.Kind + ":" + h.Doc.Key)).ToList();
        foreach (var k in up)
            if (!hits.Any(h => h.Doc.Kind + ":" + h.Doc.Key == k) && ask.Get(k) is { } d) hits.Add((d, 0));
        hits = hits.OrderByDescending(h => up.Contains(h.Doc.Kind + ":" + h.Doc.Key)).ThenByDescending(h => h.Score).ToList();
        var cards = new List<object>();
        if (fb.Customs.Where(c => SameQ(c.Q, nq)).OrderByDescending(c => c.At).FirstOrDefault() is { } custom)
            cards.Add(new { kind = "custom", key = "custom:" + nq, title = "Câu trả lời bạn đã sửa", text = custom.Text, code = custom.Code, path = "", source = "đã sửa " + custom.At.ToString("dd/MM/yyyy"), score = 0.0 });
        if (hits.Count > 0) cards.Add(Card(hits[0].Doc, q, hits[0].Score));
        var more = hits.Skip(1).Take(4).Select(h => new { key = h.Doc.Kind + ":" + h.Doc.Key, title = TitleOf(h.Doc) }).ToList();
        return JsonSerializer.Serialize(new
        {
            question,
            cards,
            more,
            empty = cards.Count == 0 ? "Chưa tìm thấy gì. Thử nêu tên controller / field / entity (vd \"field ma_kh khai báo lookup thế nào\", \"include nào khai báo DetailTax\", \"AIDetail sửa gì ở SP22.5\")." : "",
            status = Status,
        });
    }

    /// <summary>Thẻ của 1 tài liệu (bấm "Kết quả khác").</summary>
    public string Doc(string kindKey, string question)
    {
        var d = AskFor(_askCache?.Project is { Length: > 0 } p ? p : null).Get(kindKey);
        return d is null ? "" : JsonSerializer.Serialize(Card(d, question ?? "", 0));
    }

    // ---------------------------------------------------------------- lịch sử phiên chat (luôn ở máy này)

    public static string SessionsList()
    {
        try
        {
            if (!Directory.Exists(ViewerKnowledge.Sessions)) return "[]";
            return JsonSerializer.Serialize(Directory.GetFiles(ViewerKnowledge.Sessions, "*.json")
                .Select(f => { try { using var d = JsonDocument.Parse(File.ReadAllText(f)); return new { id = Path.GetFileNameWithoutExtension(f), title = d.RootElement.GetProperty("title").GetString(), at = File.GetLastWriteTime(f) }; } catch { return null; } })
                .Where(x => x is not null).OrderByDescending(x => x!.at).Take(100));
        }
        catch { return "[]"; }
    }

    public static string SessionLoad(string id)
    {
        var f = Path.Combine(ViewerKnowledge.Sessions, ViewerKnowledge.SafeName(id) + ".json");
        return File.Exists(f) ? File.ReadAllText(f) : "";
    }

    public static void SessionSave(string id, string json)
    {
        JsonDocument.Parse(json).Dispose();   // chỉ ghi JSON hợp lệ
        ViewerKnowledge.WriteAtomic(Path.Combine(ViewerKnowledge.Sessions, ViewerKnowledge.SafeName(id) + ".json"), json);
    }

    public static void SessionDelete(string id)
    {
        var f = Path.Combine(ViewerKnowledge.Sessions, ViewerKnowledge.SafeName(id) + ".json");
        if (File.Exists(f)) File.Delete(f);
    }
}
