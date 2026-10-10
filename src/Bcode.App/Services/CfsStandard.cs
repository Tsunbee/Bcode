using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bcode.App.Services;

// =====================================================================================================================
//  Mẫu CHUẨN của báo cáo tài chính (Thông tư 99/2025/TT-BTC làm gốc) cho Check LCTT / CĐKT.
//  - Mỗi mẫu là 1 file JSON (Templates\Cfs\standard\*.json): chỉ tiêu, công thức tổng, TK nguồn từng chỉ tiêu, chỉ tiêu đổi mã / đổi tên so với TT200,
//    nguồn đã học (URL) và nhật ký "đã học gì" — để suy ra khách hàng sai ở đâu từ CHUẨN, không từ khai báo của một doanh nghiệp cụ thể.
//  - Bản của người dùng (%AppData%\Bcode\cfs-standard\<id>.json) thay bản có sẵn cùng Id: sửa / ghi nhận thêm không mất khi cập nhật Bcode.
// =====================================================================================================================

public sealed class CfsStdSource { public string Title { get; set; } = ""; public string Url { get; set; } = ""; }

/// <summary>Một nguồn số liệu của chỉ tiêu: các TK (tiền tố) và bên Nợ / Có (N / C) của số dư; với LCTT trực tiếp / đầu tư / tài chính là TK tiền đối ứng TK này.</summary>
public sealed class CfsStdSrc
{
    public string Side { get; set; } = "N";
    public List<string> Acc { get; set; } = new();
    public string Term { get; set; } = "";       // "", "ngan", "dai" hoặc mô tả kỳ hạn
    public string Note { get; set; } = "";
}

public sealed class CfsStdLine
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Level { get; set; } = 2;
    /// <summary>std = chuẩn đã xác nhận · carried = suy từ TT200 (đã đối chiếu bảng so sánh) · new = chỉ tiêu mới TT99 · guide = thông lệ hướng dẫn · confirm = CHƯA xác nhận, cần đối chiếu Phụ lục TT99.</summary>
    public string Status { get; set; } = "std";
    public List<string> Formula { get; set; } = new();
    public List<CfsStdSrc> Sources { get; set; } = new();
    public bool Negative { get; set; }
    public string Pair { get; set; } = "";       // chỉ tiêu cùng TK nhưng khác kỳ hạn (131 ↔ 211…)
    public string Flow { get; set; } = "";       // LCTT: thu / chi
    public string Note { get; set; } = "";
}

public sealed class CfsStdDiff { public string From { get; set; } = ""; public string To { get; set; } = ""; public string Change { get; set; } = ""; public string Text { get; set; } = ""; }
public sealed class CfsStdLearn { public string Date { get; set; } = ""; public string What { get; set; } = ""; public string Source { get; set; } = ""; }
public sealed class CfsStdLegacy { public Dictionary<string, List<string>> Parents { get; set; } = new(); public List<string> Signature { get; set; } = new(); }

public sealed class CfsStandard
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";       // bs | cf-indirect | cf-direct
    public string Form { get; set; } = "";
    public string Circular { get; set; } = "";
    public string Title { get; set; } = "";
    public bool Verified { get; set; }
    public string Status { get; set; } = "";
    public List<CfsStdSource> Sources { get; set; } = new();
    public List<CfsStdLearn> Learned { get; set; } = new();
    public List<CfsStdDiff> Diffs { get; set; } = new();
    public CfsStdLegacy? Legacy { get; set; }
    public List<CfsStdLine> Lines { get; set; } = new();
    [JsonIgnore] public bool UserCopy { get; set; }

    public CfsStdLine? Line(string code) => Lines.FirstOrDefault(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
    /// <summary>Chỉ tiêu cha → các chỉ tiêu con cộng trực tiếp (công thức chuẩn).</summary>
    public Dictionary<string, string[]> Parents() => Lines.Where(l => l.Formula.Count > 0).ToDictionary(l => l.Code, l => l.Formula.ToArray(), StringComparer.OrdinalIgnoreCase);
}

public static class CfsStandards
{
    public static readonly string[] Ids = { "bs-tt99", "bs-tt200", "cf-indirect-tt99", "cf-direct-tt99" };

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static string BuiltinDir => Path.Combine(AppContext.BaseDirectory, "Templates", "Cfs", "standard");
    public static string UserDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "cfs-standard");

    private static readonly Dictionary<string, (DateTime Stamp, CfsStandard? Std)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static CfsStandard? Read(string path, bool user)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<CfsStandard>(File.ReadAllText(path), Json);
            if (s is null || string.IsNullOrWhiteSpace(s.Id)) return null;
            s.UserCopy = user; return s;
        }
        catch { return null; }
    }

    /// <summary>Mẫu chuẩn theo Id: bản của người dùng nếu có, không thì bản có sẵn. Null nếu không đọc được (nơi gọi dùng hằng số dự phòng).</summary>
    public static CfsStandard? Get(string id)
    {
        var user = Path.Combine(UserDir, id + ".json"); var built = Path.Combine(BuiltinDir, id + ".json");
        var stamp = (File.Exists(user) ? File.GetLastWriteTimeUtc(user) : DateTime.MinValue) + TimeSpan.FromTicks(File.Exists(built) ? File.GetLastWriteTimeUtc(built).Ticks % 1000 : 0);
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var c) && c.Stamp == stamp) return c.Std;
            var s = Read(user, true) ?? Read(built, false);
            Cache[id] = (stamp, s); return s;
        }
    }

    /// <summary>Mọi mẫu chuẩn: các mẫu có sẵn + mẫu người dùng tự tạo (file .json khác trong thư mục chuẩn của người dùng, vd khi có thông tư mới).</summary>
    public static List<CfsStandard> All()
    {
        var ids = new List<string>(Ids);
        try { if (Directory.Exists(UserDir)) foreach (var f in Directory.EnumerateFiles(UserDir, "*.json")) { var id = Path.GetFileNameWithoutExtension(f); if (!id.Equals("active", StringComparison.OrdinalIgnoreCase) && !ids.Contains(id, StringComparer.OrdinalIgnoreCase)) ids.Add(id); } } catch { /* thư mục không đọc được */ }
        return ids.Select(Get).Where(s => s is not null).Select(s => s!).ToList();
    }

    // ---- mẫu đang áp dụng cho từng loại báo cáo (bs | cf-indirect | cf-direct): mặc định TT99; chọn mẫu khác khi có thông tư mới ----
    private static string ActivePath => Path.Combine(UserDir, "active.json");
    private static readonly Dictionary<string, string> DefaultActive = new() { ["bs"] = "bs-tt99", ["cf-indirect"] = "cf-indirect-tt99", ["cf-direct"] = "cf-direct-tt99" };

    public static string ActiveId(string kind)
    {
        try
        {
            if (File.Exists(ActivePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ActivePath));
                if (d != null && d.TryGetValue(kind, out var id) && !string.IsNullOrWhiteSpace(id) && Get(id) is { } s && s.Kind == kind) return id;
            }
        }
        catch { /* file hỏng → mặc định */ }
        return DefaultActive.TryGetValue(kind, out var def) ? def : "";
    }

    public static void SetActive(string kind, string id)
    {
        Dictionary<string, string> d;
        try { d = File.Exists(ActivePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ActivePath)) ?? new() : new(); } catch { d = new(); }
        d[kind] = id;
        Directory.CreateDirectory(UserDir);
        File.WriteAllText(ActivePath, JsonSerializer.Serialize(d), new System.Text.UTF8Encoding(false));
    }

    private static string SafeId(string s) => System.Text.RegularExpressions.Regex.Replace((s ?? "").Trim().ToLowerInvariant(), @"[^a-z0-9_-]+", "-").Trim('-');

    /// <summary>Tạo mẫu chuẩn mới (vd thông tư mới) bằng cách sao chép một mẫu; chỉ tiêu và nguồn giữ nguyên để sửa tiếp.</summary>
    public static CfsStandard Clone(string fromId, string newId, string title, string circular)
    {
        var src = Get(fromId) ?? throw new InvalidOperationException("Không thấy mẫu nguồn " + fromId);
        newId = SafeId(newId); if (newId.Length == 0) throw new ArgumentException("Mã mẫu mới không hợp lệ (chỉ chữ thường, số, gạch ngang).");
        if (All().Any(x => x.Id.Equals(newId, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Đã có mẫu mang mã " + newId + ".");
        var copy = JsonSerializer.Deserialize<CfsStandard>(JsonSerializer.Serialize(src, Json), Json)!;
        copy.Id = newId; copy.Title = string.IsNullOrWhiteSpace(title) ? src.Title + " (bản sao)" : title.Trim(); copy.Circular = string.IsNullOrWhiteSpace(circular) ? src.Circular : circular.Trim();
        copy.Verified = false; copy.Status = "Mẫu do người dùng tạo từ " + src.Id + " — chỉnh chỉ tiêu theo thông tư mới.";
        copy.Learned = new List<CfsStdLearn>();
        SaveUser(copy, "Tạo mẫu mới từ " + src.Id + " (" + copy.Circular + ")");
        return copy;
    }

    /// <summary>Thêm hoặc sửa 1 chỉ tiêu (theo mã); <paramref name="afterCode"/> rỗng = giữ vị trí cũ / thêm cuối.</summary>
    public static void UpsertLine(CfsStandard s, CfsStdLine line, string afterCode, string what)
    {
        var i = s.Lines.FindIndex(l => l.Code.Equals(line.Code, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) { s.Lines[i] = line; if (!string.IsNullOrWhiteSpace(afterCode)) { s.Lines.RemoveAt(i); InsertAfter(s, line, afterCode); } }
        else InsertAfter(s, line, afterCode);
        SaveUser(s, what);
    }

    private static void InsertAfter(CfsStandard s, CfsStdLine line, string afterCode)
    {
        var j = string.IsNullOrWhiteSpace(afterCode) ? -1 : s.Lines.FindIndex(l => l.Code.Equals(afterCode, StringComparison.OrdinalIgnoreCase));
        if (j < 0) s.Lines.Add(line); else s.Lines.Insert(j + 1, line);
    }

    public static void DeleteLine(CfsStandard s, string code)
    {
        var n = s.Lines.RemoveAll(l => l.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        foreach (var l in s.Lines) l.Formula.RemoveAll(c => c.Equals(code, StringComparison.OrdinalIgnoreCase));      // bỏ khỏi công thức của chỉ tiêu cha
        if (n > 0) SaveUser(s, "Xoá chỉ tiêu " + code);
    }

    /// <summary>Ghi bản của người dùng (ghi nhận bổ sung / sửa): luôn kèm 1 dòng nhật ký.</summary>
    public static void SaveUser(CfsStandard s, string what, string source = "Người dùng ghi nhận")
    {
        s.Learned.Add(new CfsStdLearn { Date = DateTime.Now.ToString("yyyy-MM-dd"), What = what, Source = source });
        Directory.CreateDirectory(UserDir);
        File.WriteAllText(Path.Combine(UserDir, s.Id + ".json"), JsonSerializer.Serialize(s, Json), new System.Text.UTF8Encoding(false));
        lock (Cache) Cache.Remove(s.Id);
    }

    /// <summary>Bỏ bản của người dùng → quay về mẫu có sẵn.</summary>
    public static void ResetUser(string id)
    {
        var p = Path.Combine(UserDir, id + ".json");
        if (File.Exists(p)) File.Delete(p);
        lock (Cache) Cache.Remove(id);
    }

    public static object ToView(CfsStandard s) { var fast = CfsFasts.ForStandard(s); return new
    {
        s.Id, s.Kind, s.Form, s.Circular, s.Title, s.Verified, s.Status, s.UserCopy, s.Sources, s.Learned, s.Diffs, active = ActiveId(s.Kind) == s.Id, fastForm = fast is null ? "" : fast.Form + " (" + fast.Circular + ")", fastIssues = CfsFasts.FormulaIssues(s),
        lines = s.Lines.Select(l => new
        {
            l.Code, l.Name, l.Level, l.Status, l.Negative, l.Pair, l.Flow, l.Note, fast = fast is null ? "" : CfsFasts.Describe(fast, l.Code), noSource = l.Sources.Count == 0 && l.Formula.Count == 0, raw = new { l.Formula, Sources = l.Sources },
            formula = string.Join(" + ", l.Formula),
            sources = string.Join("; ", l.Sources.Select(x => (x.Side == "N" ? "dư Nợ " : x.Side == "C" ? "dư Có " : "") + string.Join(", ", x.Acc) + (x.Term.Length > 0 ? " (" + x.Term + ")" : "") + (x.Note.Length > 0 ? " — " + x.Note : ""))),
        }),
    }; }

    // ---- cho CashFlowDiagnosticService (Null = dùng hằng số dự phòng) ----
    public static Dictionary<string, string[]>? Parents(string id) { var s = Get(id); var p = s?.Parents(); return p is { Count: > 0 } ? p : null; }

    /// <summary>LCTT gián tiếp: chỉ tiêu → TK nguồn (chỉ các chỉ tiêu có Nguồn tính theo biến động số dư).</summary>
    public static Dictionary<string, string[]>? IndirectSources()
    {
        var s = Get("cf-indirect-tt99"); if (s is null) return null;
        var d = s.Lines.Where(l => l.Sources.Count > 0 && l.Sources.Any(x => x.Note.Contains("biến động"))).ToDictionary(l => l.Code, l => l.Sources.SelectMany(x => x.Acc).ToArray());
        // 02, 03 là phát sinh (không có chữ "biến động") nhưng cũng là TK nguồn của chỉ tiêu
        foreach (var c in new[] { "02", "03" }) { var l = s.Line(c); if (l is { Sources.Count: > 0 }) d[c] = l.Sources.SelectMany(x => x.Acc).ToArray(); }
        return d.Count > 0 ? d : null;
    }

    public static (string[] Positive, string[] Negative)? Flows(string id)
    {
        var s = Get(id); if (s is null) return null;
        var pos = s.Lines.Where(l => l.Flow == "thu").Select(l => l.Code).ToArray(); var neg = s.Lines.Where(l => l.Flow == "chi").Select(l => l.Code).ToArray();
        return pos.Length + neg.Length > 0 ? (pos, neg) : null;
    }
}
