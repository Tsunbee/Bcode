using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

// =====================================================================================================================
//  Khai báo CHUẨN của Fast (bản release) — học từ database Release_FBOSP2422_App: v20gltc1 (CĐKT), v20GLTC6 (LCTT gián tiếp), v20GLTC5 (LCTT trực tiếp).
//  Dùng làm "cách Fast khai báo" để đối chiếu với khai báo của khách hàng, bên cạnh mẫu chuẩn thông tư (CfsStandards). Dữ liệu nằm trong
//  Templates\Cfs\fast\*.json (sinh một lần từ database, chạy không cần kết nối).
// =====================================================================================================================

public sealed class FastLine
{
    public int Stt { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Formula { get; set; } = "";
    public List<string> Tk { get; set; } = new();
    public List<string> TkDu { get; set; } = new();
    // CĐKT
    public int TsNv { get; set; }          // 1 = tài sản, 2 = nguồn vốn
    public int CongNo { get; set; }        // 1 = lấy chi tiết một vế của các đối tượng công nợ
    public int NgoaiBang { get; set; }
    public int KhongAm { get; set; }       // 1 = chỉ lấy giá trị không âm
    public int Kind { get; set; }          // 0 = tính theo mã số (công thức), 1 = số phát sinh (LCTT) / số dư (CĐKT), 2 = số dư đầu kỳ (LCTT)
    // LCTT
    public int NoCo { get; set; }          // 1 = Nợ, 2 = Có
    public int DauCuoi { get; set; }       // 1 = đầu kỳ, 2 = cuối kỳ
    public int ThuChi { get; set; }        // 1 = thu, 0 = chi
}

public sealed class FastDecl
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";       // bs | cf-indirect | cf-direct
    public string Table { get; set; } = "";
    public string Form { get; set; } = "";
    public string Circular { get; set; } = "";
    public bool Interim { get; set; }             // mẫu giữa niên độ
    public string FormNo { get; set; } = "";      // số hiệu mẫu: B 01 - DN…
    public string Basis { get; set; } = "";       // căn cứ ban hành
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string Customer { get; set; } = "";    // mẫu đặc thù của một khách hàng (đã chạy đúng) — rỗng = mẫu gốc của Fast
    public bool Verified { get; set; }            // mẫu của khách đã chạy đúng (đối chiếu số liệu khớp)
    public string BasedOn { get; set; } = "";     // id mẫu gốc mà mẫu đặc thù này biến đổi từ đó
    public string LearnedAt { get; set; } = "";
    public List<FastLine> Lines { get; set; } = new();

    private Dictionary<string, FastLine>? _by;
    public FastLine? Line(string code) { _by ??= Lines.GroupBy(l => l.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase); return _by.TryGetValue(code, out var l) ? l : null; }

    public static IEnumerable<string> Refs(string formula) => Regex.Matches(formula ?? "", @"\[([^\]]+)\]").Select(m => m.Groups[1].Value.Trim());

    /// <summary>TK (và TK đối ứng) của chỉ tiêu, gồm cả các chỉ tiêu con theo công thức.</summary>
    public (HashSet<string> Tk, HashSet<string> Du) Union(string code, int depth = 0)
    {
        var tk = new HashSet<string>(StringComparer.Ordinal); var du = new HashSet<string>(StringComparer.Ordinal);
        var l = Line(code); if (l is null || depth > 12) return (tk, du);
        foreach (var a in l.Tk) tk.Add(a); foreach (var a in l.TkDu) du.Add(a);
        foreach (var r in Refs(l.Formula)) { var (t, d) = Union(r, depth + 1); tk.UnionWith(t); du.UnionWith(d); }
        return (tk, du);
    }
}

public static class CfsFasts
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly Dictionary<string, FastDecl?> Cache = new(StringComparer.OrdinalIgnoreCase);
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "Templates", "Cfs", "fast");

    public static FastDecl? Get(string id)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(id, out var c)) return c;
            FastDecl? d = null;
            try { var p = Path.Combine(Dir, id + ".json"); if (File.Exists(p)) d = JsonSerializer.Deserialize<FastDecl>(File.ReadAllText(p), Json); } catch { d = null; }
            Cache[id] = d; return d;
        }
    }

    /// <summary>Một mẫu báo cáo Fast đã học (danh mục v20dmmaubc): form, thông tư, mẫu năm hay giữa niên độ, số dòng khai báo.</summary>
    public sealed class RegEntry { public string Id { get; set; } = ""; public string Kind { get; set; } = ""; public string Form { get; set; } = ""; public string Circular { get; set; } = ""; public bool Interim { get; set; } public string FormNo { get; set; } = ""; public string Title { get; set; } = ""; public int Lines { get; set; } public string Customer { get; set; } = ""; }
    private sealed class RegFile { public string Source { get; set; } = ""; public List<RegEntry> Forms { get; set; } = new(); }

    private static List<RegEntry>? _reg;
    public static List<RegEntry> Registry()
    {
        lock (Cache)
        {
            if (_reg is not null) return _reg;
            try { var p = Path.Combine(Dir, "fast-registry.json"); _reg = File.Exists(p) ? JsonSerializer.Deserialize<RegFile>(File.ReadAllText(p), Json)?.Forms ?? new() : new(); } catch { _reg = new(); }
            return _reg;
        }
    }

    public static List<FastDecl> All() => Registry().Select(r => Get(r.Id)).Where(d => d is not null).Select(d => d!).ToList();

    private static string NormName(string s) => Regex.Replace((s ?? "").ToLowerInvariant().Replace('–', '-'), @"[^\p{L}\p{N}]+", " ").Trim();

    /// <summary>Khai báo của khách hàng giống mẫu Fast nào nhất (trong các mẫu NĂM của cùng loại): điểm = trùng mã chỉ tiêu + trùng tên chỉ tiêu. Trả về mẫu và điểm 0–1.</summary>
    public static (FastDecl? Decl, double Score) Closest(string kind, IEnumerable<string> codes, IEnumerable<string> names)
    {
        var cs = new HashSet<string>(codes.Where(c => c.Length > 0), StringComparer.OrdinalIgnoreCase);
        var ns = new HashSet<string>(names.Select(NormName).Where(n => n.Length > 3));
        FastDecl? best = null; double bs = -1;
        foreach (var r in Registry().Where(r => r.Kind == kind && !r.Interim))
        {
            var d = Get(r.Id); if (d is null) continue;
            var fc = new HashSet<string>(d.Lines.Select(l => l.Code), StringComparer.OrdinalIgnoreCase);
            var fn = new HashSet<string>(d.Lines.Select(l => NormName(l.Name)).Where(n => n.Length > 3));
            double J<T>(HashSet<T> a, HashSet<T> b) { var u = a.Union(b).Count(); return u == 0 ? 0 : (double)a.Intersect(b).Count() / u; }
            var sc = 0.6 * J(cs, fc) + 0.4 * J(ns, fn);
            if (sc > bs) { bs = sc; best = d; }
        }
        return (best, Math.Max(bs, 0));
    }

    /// <summary>Bản khai báo Fast dùng làm tham chiếu cho khai báo của khách hàng: CĐKT theo có 280 (TT99) hay 270 (TT200); LCTT gián tiếp theo tên chỉ tiêu (đi vay / chờ phân bổ = TT99).</summary>
    public static FastDecl? Reference(string kind, IEnumerable<string> codes, IEnumerable<string> names)
    {
        var (d, score) = Closest(kind, codes, names);
        if (d is not null && score > 0.2) return d;
        return kind switch { "bs" => Get("fast-bs-v20gltc108"), "cf-indirect" => Get("fast-cf-indirect-v20gltc605"), "cf-direct" => Get("fast-cf-direct-v20gltc507"), _ => null };
    }

    /// <summary>Những chỗ mẫu đặc thù của khách khác mẫu gốc (TK thêm / bớt, công thức, dòng thêm / bỏ) — đây là "danh sách TK riêng của khách" đã chạy đúng.</summary>
    public static List<string> VariantDiffs(FastDecl v)
    {
        var res = new List<string>(); var b = v.BasedOn.Length > 0 ? Get(v.BasedOn) : null; if (b is null) return res;
        string J(IEnumerable<string> x) => string.Join(",", x);
        foreach (var l in v.Lines)
        {
            var o = b.Line(l.Code);
            if (o is null) { res.Add(l.Code + " " + l.Name.Trim() + ": dòng RIÊNG của khách (mẫu gốc không có)"); continue; }
            var d = new List<string>();
            if (!l.Tk.SequenceEqual(o.Tk)) { var add = l.Tk.Except(o.Tk).ToList(); var del = o.Tk.Except(l.Tk).ToList(); if (add.Count + del.Count > 0) d.Add("TK " + (add.Count > 0 ? "thêm " + J(add) : "") + (add.Count > 0 && del.Count > 0 ? ", " : "") + (del.Count > 0 ? "bỏ " + J(del) : "")); }
            if (!l.TkDu.SequenceEqual(o.TkDu)) { var add = l.TkDu.Except(o.TkDu).ToList(); var del = o.TkDu.Except(l.TkDu).ToList(); if (add.Count + del.Count > 0) d.Add("TK đối ứng " + (add.Count > 0 ? "thêm " + J(add) : "") + (add.Count > 0 && del.Count > 0 ? ", " : "") + (del.Count > 0 ? "bỏ " + J(del) : "")); }
            if (l.Formula != o.Formula) d.Add("công thức [" + l.Formula + "] thay vì [" + o.Formula + "]");
            if (l.Kind != o.Kind && l.Formula.Length == 0) d.Add("cách tính " + l.Kind + " thay vì " + o.Kind);
            if (d.Count > 0) res.Add(l.Code + " " + l.Name.Trim() + ": " + string.Join("; ", d));
        }
        foreach (var o in b.Lines.Where(x => v.Line(x.Code) is null)) res.Add(o.Code + " " + o.Name.Trim() + ": mẫu gốc có, khách BỎ");
        return res;
    }

    /// <summary>Bản Fast tham chiếu cho một mẫu chuẩn: CĐKT TT99 → V20GLTC108, LCTT gián tiếp TT99 → V20GLTC605, LCTT trực tiếp TT99 → V20GLTC507.</summary>
    public static FastDecl? ForStandard(CfsStandard std) => std.Circular.StartsWith("TT99", StringComparison.OrdinalIgnoreCase)
        ? Get(std.Kind switch { "bs" => "fast-bs-v20gltc108", "cf-indirect" => "fast-cf-indirect-v20gltc605", "cf-direct" => "fast-cf-direct-v20gltc507", _ => "" }) : null;

    /// <summary>Chỗ bản Fast release khai báo công thức tổng KHÁC mẫu chuẩn (thiếu / thừa / trùng chỉ tiêu con) — ví dụ 230 = 231+238+237+238, 08 không cộng 07.</summary>
    public static List<string> FormulaIssues(CfsStandard std)
    {
        var res = new List<string>(); var fast = ForStandard(std); if (fast is null) return res;
        foreach (var l in std.Lines.Where(x => x.Formula.Count > 0))
        {
            var fl = fast.Line(l.Code); if (fl is null || fl.Formula.Length == 0) continue;
            var refs = FastDecl.Refs(fl.Formula).ToList();
            var kidsFast = new HashSet<string>(refs, StringComparer.OrdinalIgnoreCase); var kidsStd = new HashSet<string>(l.Formula, StringComparer.OrdinalIgnoreCase);
            var miss = kidsStd.Where(k => !kidsFast.Contains(k) && fast.Line(k) is not null).ToList(); var extra = kidsFast.Where(k => !kidsStd.Contains(k)).ToList();
            var dup = refs.GroupBy(r => r, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (miss.Count + extra.Count + dup.Count == 0) continue;
            res.Add($"{fast.Form} · {l.Code}: công thức Fast [{fl.Formula}] " + (miss.Count > 0 ? "thiếu " + string.Join(", ", miss) + "; " : "") + (extra.Count > 0 ? "thừa " + string.Join(", ", extra) + "; " : "") + (dup.Count > 0 ? "cộng trùng " + string.Join(", ", dup) : ""));
        }
        return res;
    }

    /// <summary>Mô tả ngắn cách Fast khai báo chỉ tiêu <paramref name="code"/> (TK, TK đối ứng, cờ) — hiện ở tab Mẫu chuẩn.</summary>
    public static string Describe(FastDecl d, string code)
    {
        var l = d.Line(code); if (l is null) return "";
        var (tk, du) = d.Union(code);
        if (tk.Count == 0 && du.Count == 0) return l.Formula.Length > 0 ? "công thức " + l.Formula : "";
        var flags = new List<string>();
        if (l.CongNo == 1) flags.Add("chi tiết công nợ 1 vế"); if (l.KhongAm == 1) flags.Add("không âm");
        return "TK " + string.Join(",", tk.OrderBy(x => x, StringComparer.Ordinal).Take(14)) + (tk.Count > 14 ? "…" : "") + (du.Count > 0 ? " · đối ứng " + string.Join(",", du.OrderBy(x => x, StringComparer.Ordinal).Take(10)) : "") + (flags.Count > 0 ? " (" + string.Join("; ", flags) + ")" : "");
    }
}
