using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

// =====================================================================================================================
//  Check Include / Ignore: so khai báo thật của dự án với CATALOG tính năng của từng phiên bản (FBO / FAO / FAW).
//  - Catalog = các file .txt, mỗi file là 1 nhóm (thường theo phiên bản: SP22, SP24.2...), mỗi tính năng là 1 khối [[FEATURE]] (xem README trong Assets\includecheck).
//    Catalog có sẵn nằm trong exe; catalog của người dùng ở %AppData%\Bcode\includecheck\catalog — file cùng tên nhóm sẽ THAY bản có sẵn, nhóm mới thì thêm vào.
//  - Điều kiện của 1 tính năng: file Include phải là INCLUDE / IGNORE, option trong bảng options, wcommand trong bảng wcommand9, entity trong file .ent.
//  - Kiểm tra: đọc file thật trong App_Data\Controllers, đọc options (App DB) + wcommand9 (Sys DB) rồi so từng điều kiện.
// =====================================================================================================================

public sealed class IncCondition
{
    public string Kind { get; set; } = "include";   // include | option | wcommand | entity
    public string Key { get; set; } = "";           // đường dẫn file (Include\X.txt) | tên option | xgroup của wcommand | "file.ent : EntityName"
    public string Expected { get; set; } = "";      // INCLUDE / IGNORE / giá trị
}

public sealed class IncFeature
{
    public string Group { get; set; } = "";
    public string Title { get; set; } = "";
    public string Version { get; set; } = "";
    public string Id { get; set; } = "";
    public string Products { get; set; } = "";
    public string Mode { get; set; } = "";             // "default" = công tắc chưa có mô tả: so với MẶC ĐỊNH của source chuẩn (lệch = dự án đã chỉnh riêng, không phải lỗi)
    public List<IncCondition> Conditions { get; set; } = new();
    public List<string> Guide { get; set; } = new();
    public bool Builtin { get; set; }
    public string Key => Group + "|" + Title;
}

public sealed class IncCondResult
{
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Actual { get; set; } = "";
    public string State { get; set; } = "unknown";  // ok | bad | missing | unknown
    public string Note { get; set; } = "";
    public string Fix { get; set; } = "";             // câu lệnh sửa nhanh (SQL) nếu có
}

public sealed class IncFeatureResult
{
    public string Key { get; set; } = "";
    public string Status { get; set; } = "";           // ok | bad | partial | dbonly | none
    public string StatusText { get; set; } = "";
    public List<IncCondResult> Conditions { get; set; } = new();
}

public sealed class IncFileInfo
{
    public string Name { get; set; } = "";            // Include\X.txt
    public string State { get; set; } = "";           // INCLUDE | IGNORE
    public List<string> Features { get; set; } = new();
}

public static class IncludeCheckService
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    public static string UserFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Bcode", "includecheck", "catalog");

    // ------------------------------------------------------------------------------------------ đọc / ghi catalog
    private static readonly Regex KeyLine = new(@"^\s*(guide\+|[A-Za-z_]+)\s*=\s?(.*)$", RegexOptions.Compiled);

    /// <summary>Phân tích 1 file catalog (nhiều khối [[FEATURE]]).</summary>
    public static List<IncFeature> ParseCatalog(string text, string group, bool builtin)
    {
        var list = new List<IncFeature>(); IncFeature? cur = null;
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd().TrimStart('﻿');
            if (line.TrimStart().StartsWith('#')) continue;
            if (line.Trim().Equals("[[FEATURE]]", StringComparison.OrdinalIgnoreCase)) { if (cur != null) list.Add(cur); cur = new IncFeature { Group = group, Builtin = builtin }; continue; }
            if (cur == null) continue;
            if (line.Trim().Length == 0) { if (cur.Title.Length > 0) { list.Add(cur); cur = null; } continue; }
            var m = KeyLine.Match(line); if (!m.Success) continue;
            var key = m.Groups[1].Value.ToLowerInvariant(); var val = m.Groups[2].Value.Trim();
            switch (key)
            {
                case "title": cur.Title = val; break;
                case "version": cur.Version = val; break;
                case "id": cur.Id = val; break;
                case "products": cur.Products = val; break;
                case "mode": cur.Mode = val.ToLowerInvariant(); break;
                case "include": AddCond(cur, "include", val); break;
                case "option": AddCond(cur, "option", val); break;
                case "wcommand": AddCond(cur, "wcommand", val); break;
                case "entity": AddCond(cur, "entity", val); break;
                case "guide": case "guide+": cur.Guide.Add(val); break;
            }
        }
        if (cur != null && cur.Title.Length > 0) list.Add(cur);
        return list.Where(f => f.Title.Length > 0).ToList();
    }

    /// <summary>Điều kiện dạng "khoá = giá trị" hoặc câu UPDATE options / wcommand9 dán từ hướng dẫn.</summary>
    private static void AddCond(IncFeature f, string kind, string val)
    {
        var c = ParseCondition(kind, val);
        if (c != null) f.Conditions.Add(c);
    }

    public static IncCondition? ParseCondition(string kind, string val)
    {
        val = val.Trim(); if (val.Length == 0) return null;
        if (kind == "option" && Regex.Match(val, @"update\s+options\s+set\s+val\s*=\s*'?([^'\s]*)'?.*?name\s*=\s*'([^']+)'", RegexOptions.IgnoreCase) is { Success: true } mo)
            return new IncCondition { Kind = "option", Key = mo.Groups[2].Value, Expected = mo.Groups[1].Value };
        if (kind == "wcommand" && Regex.Match(val, @"update\s+wcommand9\s+set\s+status\s*=\s*'?(\d+)'?.*?xgroup\s*=\s*'([^']+)'", RegexOptions.IgnoreCase) is { Success: true } mw)
            return new IncCondition { Kind = "wcommand", Key = mw.Groups[2].Value, Expected = mw.Groups[1].Value };
        var i = val.LastIndexOf('=');
        if (i < 0) return new IncCondition { Kind = kind, Key = val, Expected = "" };
        var key = val[..i].Trim(); var exp = val[(i + 1)..].Trim().Trim('\'');
        if (kind == "entity" && val.Contains('|')) { var p = val.Split('|', 2); return new IncCondition { Kind = kind, Key = p[0].Trim() + " | " + p[1].Split('=')[0].Trim(), Expected = p[1].Contains('=') ? p[1][(p[1].IndexOf('=') + 1)..].Trim() : "" }; }
        return new IncCondition { Kind = kind, Key = key, Expected = exp };
    }

    public static string WriteCatalog(string group, IEnumerable<IncFeature> features)
    {
        var sb = new StringBuilder();
        sb.Append("# Tinh nang nhom ").Append(group).Append("  (sua bang tay hoac bang man hinh Check Include cua Bcode)\n");
        foreach (var f in features)
        {
            sb.Append("\n[[FEATURE]]\n");
            sb.Append("title    = ").Append(f.Title).Append('\n');
            if (f.Version.Length > 0) sb.Append("version  = ").Append(f.Version).Append('\n');
            if (f.Id.Length > 0) sb.Append("id       = ").Append(f.Id).Append('\n');
            if (f.Products.Length > 0) sb.Append("products = ").Append(f.Products).Append('\n');
            if (f.Mode.Length > 0) sb.Append("mode     = ").Append(f.Mode).Append('\n');
            foreach (var c in f.Conditions)
                sb.Append(c.Kind switch { "include" => "include  = ", "option" => "option   = ", "wcommand" => "wcommand = ", _ => "entity   = " }).Append(c.Key).Append(" = ").Append(c.Expected).Append('\n');
            for (var i = 0; i < f.Guide.Count; i++) sb.Append(i == 0 ? "guide    = " : "guide+   = ").Append(f.Guide[i]).Append('\n');
        }
        return sb.ToString();
    }

    private static string SafeName(string g) => Regex.Replace(g.Trim(), @"[\\/:*?""<>|]+", "_");

    /// <summary>Catalog có sẵn (nhúng trong exe) + của người dùng (cùng tên nhóm thì thay bản có sẵn).</summary>
    public static List<IncFeature> LoadAll()
    {
        var groups = new Dictionary<string, List<IncFeature>>(StringComparer.OrdinalIgnoreCase);
        var asm = typeof(IncludeCheckService).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("includecheck.", StringComparison.Ordinal) && n.EndsWith(".txt", StringComparison.Ordinal)))
        {
            var g = res["includecheck.".Length..^4];
            if (g.StartsWith('_')) continue;
            using var s = asm.GetManifestResourceStream(res)!; using var sr = new StreamReader(s, new UTF8Encoding(false), true);
            groups[g] = ParseCatalog(sr.ReadToEnd(), g, true);
        }
        try
        {
            if (Directory.Exists(UserFolder))
                foreach (var f in Directory.EnumerateFiles(UserFolder, "*.txt"))
                {
                    var g = Path.GetFileNameWithoutExtension(f);
                    groups[g] = ParseCatalog(File.ReadAllText(f, Encoding.UTF8), g, false);
                }
        }
        catch { /* catalog người dùng hỏng không được làm mất catalog có sẵn */ }
        return groups.Values.SelectMany(x => x).ToList();
    }

    public static bool HasUserGroup(string group) => File.Exists(Path.Combine(UserFolder, SafeName(group) + ".txt"));

    /// <summary>Lưu (thêm / sửa) 1 tính năng vào catalog người dùng của nhóm đó. Nhóm chưa có file riêng thì copy toàn bộ tính năng có sẵn của nhóm sang trước.</summary>
    public static void SaveFeature(IncFeature f, string? originalTitle)
    {
        if (string.IsNullOrWhiteSpace(f.Title)) throw new InvalidDataException("Nhập tên tính năng.");
        if (string.IsNullOrWhiteSpace(f.Group)) f.Group = string.IsNullOrWhiteSpace(f.Version) ? "Khac" : f.Version.Split('.')[0];
        var all = LoadAll().Where(x => string.Equals(x.Group, f.Group, StringComparison.OrdinalIgnoreCase)).ToList();
        var key = originalTitle ?? f.Title;
        var idx = all.FindIndex(x => string.Equals(x.Title, key, StringComparison.OrdinalIgnoreCase));
        f.Builtin = false;
        if (idx >= 0) all[idx] = f; else all.Add(f);
        Directory.CreateDirectory(UserFolder);
        File.WriteAllText(Path.Combine(UserFolder, SafeName(f.Group) + ".txt"), WriteCatalog(f.Group, all), new UTF8Encoding(true));
    }

    public static void DeleteFeature(string group, string title)
    {
        var all = LoadAll().Where(x => string.Equals(x.Group, group, StringComparison.OrdinalIgnoreCase) && !string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase)).ToList();
        Directory.CreateDirectory(UserFolder);
        File.WriteAllText(Path.Combine(UserFolder, SafeName(group) + ".txt"), WriteCatalog(group, all), new UTF8Encoding(true));
    }

    public static void ResetGroup(string group)
    {
        var p = Path.Combine(UserFolder, SafeName(group) + ".txt");
        if (File.Exists(p)) File.Delete(p);
    }

    // ------------------------------------------------------------------------------------------ dán hướng dẫn → điều kiện
    /// <summary>"Dán hướng dẫn": nhận các dòng như  Include\X.txt = INCLUDE / update options set val = '1' where name = 'm_x' / update wcommand9 set status = 1 where xgroup = 'Y' / m_opt = 1
    /// và tự tách thành điều kiện + giữ nguyên các dòng làm hướng dẫn.</summary>
    public static (List<IncCondition> Conds, List<string> Guide) ParseGuidePaste(string text)
    {
        var conds = new List<IncCondition>(); var guide = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim(); if (line.Length == 0) continue;
            guide.Add(line);
            var m = Regex.Match(line, @"^(?<f>[\w\\./\- ]+\.(?:txt|ent|Nested[\w.]*|xml|f))\s*=\s*(?<v>INCLUDE|IGNORE)\s*$", RegexOptions.IgnoreCase);
            if (m.Success) { conds.Add(new IncCondition { Kind = "include", Key = m.Groups["f"].Value.Trim(), Expected = m.Groups["v"].Value.ToUpperInvariant() }); continue; }
            var e = Regex.Match(line, @"<!ENTITY\s+%\s+(?<n>[\w.]+)\s+""(?<v>INCLUDE|IGNORE)""", RegexOptions.IgnoreCase);
            if (e.Success) { conds.Add(new IncCondition { Kind = "entity", Key = " | " + e.Groups["n"].Value, Expected = e.Groups["v"].Value.ToUpperInvariant() }); continue; }
            if (Regex.IsMatch(line, @"^update\s+options\b", RegexOptions.IgnoreCase) && ParseCondition("option", line) is { } co) { conds.Add(co); continue; }
            if (Regex.IsMatch(line, @"^update\s+wcommand9\b", RegexOptions.IgnoreCase) && ParseCondition("wcommand", line) is { } cw) { conds.Add(cw); continue; }
        }
        // entity cần biết file: lấy dòng đường dẫn .ent đứng trước (nếu có)
        var entFile = guide.FirstOrDefault(g => Regex.IsMatch(g, @"\.ent\s*$", RegexOptions.IgnoreCase));
        foreach (var c in conds.Where(c => c.Kind == "entity" && c.Key.StartsWith(" | "))) c.Key = (entFile ?? "") + c.Key;
        return (conds, guide);
    }

    // ------------------------------------------------------------------------------------------ import xlsx (cột: Nhóm | Nội dung | Phiên bản | ID | SP | Include | Options | WCommand | Hướng dẫn | Bật)
    public static List<IncFeature> ImportXlsx(string path)
    {
        var rows = ReadXlsx(path); var res = new List<IncFeature>();
        var hdr = rows.FirstOrDefault(r => r.Values.Any(v => v.Trim().Equals("Nội dung tính năng", StringComparison.OrdinalIgnoreCase) || v.Trim().Equals("Noi dung tinh nang", StringComparison.OrdinalIgnoreCase)));
        var start = hdr == null ? 0 : rows.IndexOf(hdr) + 1;
        for (var i = start; i < rows.Count; i++)
        {
            var r = rows[i]; string C(int k) => r.TryGetValue(k, out var v) ? v.Trim() : "";
            if (C(2).Length == 0) continue;
            if (C(10).Length > 0 && C(10) == "0") continue;
            var f = new IncFeature { Group = C(1).Length > 0 ? C(1) : "Import", Title = C(2), Version = C(3), Id = C(4), Products = C(5) };
            foreach (var l in C(6).Split('\n')) if (ParseCondition("include", l) is { } c1 && c1.Key.Length > 0) f.Conditions.Add(c1);
            foreach (var l in C(7).Split('\n')) if (ParseCondition("option", l) is { } c2 && c2.Key.Length > 0) f.Conditions.Add(c2);
            foreach (var l in C(8).Split('\n')) if (ParseCondition("wcommand", l) is { } c3 && c3.Key.Length > 0) f.Conditions.Add(c3);
            foreach (var l in C(9).Split('\n')) if (l.Trim().Length > 0) f.Guide.Add(l.Trim());
            res.Add(f);
        }
        return res;
    }

    private static List<Dictionary<int, string>> ReadXlsx(string path)
    {
        var rows = new List<Dictionary<int, string>>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
        System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var shared = new List<string>();
        var ss = zip.GetEntry("xl/sharedStrings.xml");
        if (ss != null) using (var s = ss.Open()) foreach (var si in System.Xml.Linq.XDocument.Load(s).Descendants(ns + "si")) shared.Add(string.Concat(si.Descendants(ns + "t").Select(t => t.Value)));
        var sheet = zip.GetEntry("xl/worksheets/sheet1.xml") ?? zip.Entries.First(e => e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml"));
        System.Xml.Linq.XDocument doc; using (var s = sheet.Open()) doc = System.Xml.Linq.XDocument.Load(s);
        foreach (var r in doc.Descendants(ns + "row"))
        {
            var row = new Dictionary<int, string>(); var ci = 0;
            foreach (var c in r.Elements(ns + "c"))
            {
                var ca = c.Attribute("r"); ci = ca != null ? ColIndex(new string(ca.Value.TakeWhile(char.IsLetter).ToArray())) : ci + 1;
                var t = c.Attribute("t")?.Value ?? ""; string val;
                if (t == "inlineStr") val = string.Concat(c.Descendants(ns + "t").Select(x => x.Value));
                else { var v = c.Element(ns + "v"); if (v == null) continue; val = t == "s" && int.TryParse(v.Value, out var si) && si >= 0 && si < shared.Count ? shared[si] : v.Value; }
                row[ci] = val;
            }
            if (row.Count > 0) rows.Add(row);
        }
        return rows;
    }
    private static int ColIndex(string letters) { var n = 0; foreach (var ch in letters) n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1); return n; }

    // ------------------------------------------------------------------------------------------ kiểm tra
    public static string ControllersOf(string sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot)) return "";
        var r = sourceRoot.Trim().TrimEnd('\\', '/');
        foreach (var c in new[] { Path.Combine(r, "App_Data", "Controllers"), r, Path.Combine(r, "Controllers") })
            if (Directory.Exists(Path.Combine(c, "Include"))) return c;
        return "";
    }

    private static readonly Regex StateRx = new(@"\b(INCLUDE|IGNORE)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>INCLUDE / IGNORE của 1 file include; null nếu không đọc được / không có chữ nào.</summary>
    public static string? ReadIncludeState(string controllers, string rel, out string note)
    {
        note = "";
        var path = Path.Combine(controllers, rel.Replace('/', '\\').TrimStart('\\'));
        if (!File.Exists(path)) { note = "Không thấy file"; return null; }
        try
        {
            var txt = File.ReadAllText(path);
            var m = StateRx.Match(txt);
            if (!m.Success) { note = "File không có INCLUDE / IGNORE"; return null; }
            return m.Groups[1].Value.ToUpperInvariant();
        }
        catch (Exception ex) { note = ex.Message; return null; }
    }

    private static bool SameValue(string actual, string expected)
    {
        string N(string s) => Regex.Replace(s ?? "", @"\s+", "").Trim('\'').ToLowerInvariant();
        if (N(actual) == N(expected)) return true;
        var a = (actual ?? "").Split(',').Select(N).Where(x => x.Length > 0).OrderBy(x => x).ToList();
        var e = (expected ?? "").Split(',').Select(N).Where(x => x.Length > 0).OrderBy(x => x).ToList();
        return a.Count > 1 && a.SequenceEqual(e);
    }

    /// <summary>Đánh giá toàn bộ catalog với dự án. <paramref name="options"/> / <paramref name="wcommands"/> null = chưa đọc DB (điều kiện DB để "chưa kiểm tra").</summary>
    public static List<IncFeatureResult> Evaluate(IEnumerable<IncFeature> features, string controllers, Dictionary<string, string?>? options, Dictionary<string, List<string>>? wcommands)
    {
        var res = new List<IncFeatureResult>();
        var stateCache = new Dictionary<string, (string? State, string Note)>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in features)
        {
            var r = new IncFeatureResult { Key = f.Key };
            foreach (var c in f.Conditions)
            {
                var cr = new IncCondResult { Kind = c.Kind, Key = c.Key, Expected = c.Expected };
                switch (c.Kind)
                {
                    case "include":
                    {
                        if (controllers.Length == 0) { cr.State = "unknown"; cr.Note = "Chưa chọn thư mục dự án"; break; }
                        if (!stateCache.TryGetValue(c.Key, out var st)) { var s = ReadIncludeState(controllers, c.Key, out var note); stateCache[c.Key] = st = (s, note); }
                        cr.Actual = st.State ?? ""; cr.Note = st.Note;
                        cr.State = st.State == null ? "missing" : (string.Equals(st.State, c.Expected, StringComparison.OrdinalIgnoreCase) ? "ok" : "bad");
                        break;
                    }
                    case "entity":
                    {
                        var p = c.Key.Split('|'); var file = p[0].Trim(); var ent = p.Length > 1 ? p[1].Trim() : "";
                        if (controllers.Length == 0 || file.Length == 0) { cr.State = "unknown"; cr.Note = file.Length == 0 ? "Chưa khai tên file .ent" : "Chưa chọn thư mục dự án"; break; }
                        var path = Path.Combine(controllers, file.Replace('/', '\\').TrimStart('\\'));
                        if (!File.Exists(path)) { cr.State = "missing"; cr.Note = "Không thấy file"; break; }
                        var m = Regex.Match(File.ReadAllText(path), @"<!ENTITY\s+%\s+" + Regex.Escape(ent) + @"\s+""([^""]*)""", RegexOptions.IgnoreCase);
                        if (!m.Success) { cr.State = "missing"; cr.Note = "Không thấy entity " + ent; break; }
                        cr.Actual = m.Groups[1].Value.ToUpperInvariant(); cr.State = string.Equals(cr.Actual, c.Expected, StringComparison.OrdinalIgnoreCase) ? "ok" : "bad";
                        break;
                    }
                    case "option":
                    {
                        if (options == null) { cr.State = "unknown"; cr.Note = "Chưa kiểm tra DB"; }
                        else if (!options.TryGetValue(c.Key, out var v)) { cr.State = "missing"; cr.Note = "Chưa có option này trong bảng options"; cr.Actual = "(không có)"; }
                        else { cr.Actual = (v ?? "").Trim(); cr.State = SameValue(cr.Actual, c.Expected) ? "ok" : "bad"; }
                        cr.Fix = "UPDATE options SET val = '" + c.Expected.Replace("'", "''") + "' WHERE name = '" + c.Key.Replace("'", "''") + "'";
                        break;
                    }
                    case "wcommand":
                    {
                        if (wcommands == null) { cr.State = "unknown"; cr.Note = "Chưa kiểm tra DB"; }
                        else if (!wcommands.TryGetValue(c.Key, out var vals) || vals.Count == 0) { cr.State = "missing"; cr.Note = "Không có wcommand9 nào thuộc nhóm này"; cr.Actual = "(không có)"; }
                        else { var d = vals.Distinct().ToList(); cr.Actual = string.Join(" / ", d) + (vals.Count > 1 ? "  (" + vals.Count + " dòng)" : ""); cr.State = d.Count == 1 && d[0] == c.Expected ? "ok" : "bad"; }
                        cr.Fix = "UPDATE wcommand9 SET status = '" + c.Expected.Replace("'", "''") + "' WHERE xgroup = '" + c.Key.Replace("'", "''") + "'";
                        break;
                    }
                }
                r.Conditions.Add(cr);
            }
            int ok = r.Conditions.Count(x => x.State == "ok"), bad = r.Conditions.Count(x => x.State is "bad" or "missing"), unk = r.Conditions.Count(x => x.State == "unknown");
            if (f.Mode == "default" && r.Conditions.Count > 0)
            {
                if (bad == 0 && unk == 0) { r.Status = "ok"; r.StatusText = "Đúng mặc định"; }
                else if (bad > 0) { r.Status = "diff"; r.StatusText = "Khác mặc định"; }
                else { r.Status = "dbonly"; r.StatusText = "Chưa kiểm tra"; }
            }
            else if (r.Conditions.Count == 0) { r.Status = "none"; r.StatusText = "Chỉ hướng dẫn"; }
            else if (bad == 0 && unk == 0) { r.Status = "ok"; r.StatusText = "Đúng khai báo"; }
            else if (ok == 0 && bad > 0) { r.Status = "bad"; r.StatusText = "Chưa bật / sai"; }
            else if (ok > 0 && bad > 0) { r.Status = "partial"; r.StatusText = "Một phần"; }
            else if (bad == 0) { r.Status = "dbonly"; r.StatusText = ok > 0 ? "Include đúng, chưa kiểm tra DB" : "Chưa kiểm tra DB"; }
            else { r.Status = "partial"; r.StatusText = "Một phần"; }
            res.Add(r);
        }
        return res;
    }

    /// <summary>Mọi file Include\*.txt chỉ chứa INCLUDE / IGNORE (file "công tắc") của dự án + các tính năng catalog nhắc tới nó.</summary>
    public static List<IncFileInfo> ListSwitchFiles(string controllers, IEnumerable<IncFeature> features)
    {
        var list = new List<IncFileInfo>();
        if (controllers.Length == 0) return list;
        var dir = Path.Combine(controllers, "Include");
        var featList = features.ToList();
        foreach (var f in Directory.EnumerateFiles(dir, "*.txt", SearchOption.AllDirectories))
        {
            try
            {
                var fi = new FileInfo(f); if (fi.Length > 64) continue;
                var txt = File.ReadAllText(f).Trim().TrimStart('﻿').Trim();
                if (!Regex.IsMatch(txt, @"^(INCLUDE|IGNORE)$", RegexOptions.IgnoreCase)) continue;
                var rel = "Include\\" + Path.GetRelativePath(dir, f).Replace('/', '\\');
                list.Add(new IncFileInfo { Name = rel, State = txt.ToUpperInvariant(), Features = featList.Where(x => x.Conditions.Any(c => c.Kind == "include" && string.Equals(c.Key.Replace('/', '\\'), rel, StringComparison.OrdinalIgnoreCase))).Select(x => x.Title).ToList() });
            }
            catch { /* file khoá / không đọc được */ }
        }
        return list.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Đặt file công tắc về INCLUDE / IGNORE (giữ BOM, lưu bản .bak 1 lần).</summary>
    public static void SetIncludeState(string controllers, string rel, string state)
    {
        state = state.ToUpperInvariant(); if (state is not ("INCLUDE" or "IGNORE")) throw new InvalidDataException("Giá trị phải là INCLUDE hoặc IGNORE.");
        var path = Path.Combine(controllers, rel.Replace('/', '\\').TrimStart('\\'));
        var bak = path + ".bak";
        if (File.Exists(path) && !File.Exists(bak)) File.Copy(path, bak);
        File.WriteAllText(path, state, new UTF8Encoding(true));
    }

    /// <summary>Các file trong Controllers nhắc tới tên file include này (entity khai báo ..\Include\X.txt...) — tối đa <paramref name="max"/> kết quả.</summary>
    public static List<(string Rel, int Line)> FindReferences(string controllers, string includeRel, int max = 60)
    {
        var name = includeRel.Replace('/', '\\');
        var needle = name.Contains('\\') ? name[(name.LastIndexOf('\\') + 1)..] : name;
        var hits = new System.Collections.Concurrent.ConcurrentBag<(string, int)>();
        var self = Path.Combine(controllers, name);
        Parallel.ForEach(new[] { "Include", "Dir", "Grid", "Filter", "Report", "Lookup" }.Select(d => Path.Combine(controllers, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.*", SearchOption.AllDirectories)).Where(f => f != self && !f.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)),
            new ParallelOptions { MaxDegreeOfParallelism = 8 }, (f, state) =>
            {
                if (hits.Count >= max) { state.Stop(); return; }
                try
                {
                    var n = 0;
                    foreach (var line in File.ReadLines(f)) { n++; if (line.Contains(needle, StringComparison.OrdinalIgnoreCase)) { hits.Add((Path.GetRelativePath(controllers, f), n)); break; } }
                }
                catch { /* bỏ qua */ }
            });
        return hits.OrderBy(h => h.Item1, StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }
}
