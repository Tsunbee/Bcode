using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bcode.App.Models;

namespace Bcode.App.Services;

/// <summary>1 dòng chữ OCR đọc được (toạ độ theo ảnh gốc).</summary>
public sealed record OcrLine(string Text, int X, int Y, int W, int H);

/// <summary>Màn hình (file controller) khả năng cao: điểm, menu trỏ tới, các chữ trên ảnh khớp với tiêu đề / nhãn của file.</summary>
public sealed record ShotScreen(string File, string Label, double Score, List<string> Menus, List<string> Reasons);

/// <summary>Chữ trên ảnh khớp nguyên câu trong source (câu báo lỗi, thông báo…): file + dòng.</summary>
public sealed record ShotText(string OcrText, string Text, string File, string Label, int Line, double Similarity);

/// <summary>
/// "Tra ảnh lỗi": ảnh chụp màn hình khách gửi → màn hình nào, câu chữ trên ảnh nằm ở file / dòng nào. Miễn phí, chạy trong máy, không dùng AI:
///  1. OCR có sẵn của Windows (Ocr\Bcode.Ocr.exe — phóng ảnh 2 lần trước khi đọc cho chữ nhỏ trên màn hình).
///  2. Chỉ mục màn hình từ source (App_Data\Controllers: Dir, Filter, Grid, Include, Options, Report): tiêu đề &lt;title v e&gt;, nhãn ô &lt;header v e&gt; và mọi chữ
///     hiển thị v="…" / e="…" kèm số dòng. Lưu ở %AppData%\Bcode\screen-index\, lần sau chỉ đọc lại file đã đổi (như chỉ mục "Ai đang dùng").
///  3. So khớp gần đúng: bỏ dấu, quy lỗi OCR hay gặp (1 / | → l, 0 → o, bỏ khoảng trắng), so cụm 3 ký tự (Dice); điểm theo trọng số: tiêu đề / tên menu nặng,
///     nhãn ô nhẹ hơn, nhãn càng hiếm (ít file có) càng nặng (IDF).
/// </summary>
public sealed class ErrorShotService
{
    // ---- OCR -----------------------------------------------------------------------------------------------------------

    public static string OcrExe => Path.Combine(AppContext.BaseDirectory, "Ocr", "Bcode.Ocr.exe");

    public sealed record OcrResult(string Lang, int Width, int Height, long Ms, List<OcrLine> Lines);

    /// <summary>
    /// Đọc chữ 2 lượt song song: ảnh gốc + ảnh tăng tương phản quanh màu nền (<c>--enhance</c>: màn hình bị lớp mờ của hộp thoại phủ, chữ
    /// phía sau như tiêu đề màn hình / tiêu đề cột chỉ hơi đậm hơn nền). Gộp: giữ dòng của lượt gốc, thêm dòng của lượt tăng tương phản
    /// nằm ở chỗ lượt gốc chưa đọc được.
    /// </summary>
    public static async Task<OcrResult> RunOcrAsync(string imagePath, CancellationToken ct)
    {
        if (!File.Exists(OcrExe)) throw new FileNotFoundException("Thiếu " + OcrExe + " — build lại Bcode (project src\\Bcode.Ocr build kèm, chép ra thư mục Ocr).");
        var sw = Stopwatch.StartNew();
        var plain = RunOnceAsync(imagePath, false, ct);
        var enh = RunOnceAsync(imagePath, true, ct);
        var a = await plain;
        OcrResult? b = null;
        try { b = await enh; } catch (Exception ex) when (ex is not OperationCanceledException) { /* lượt phụ lỗi thì bỏ qua */ }
        if (b is null) return a;
        static bool Overlap(OcrLine p, OcrLine q)
        {
            int ix = Math.Min(p.X + p.W, q.X + q.W) - Math.Max(p.X, q.X), iy = Math.Min(p.Y + p.H, q.Y + q.H) - Math.Max(p.Y, q.Y);
            if (ix <= 0 || iy <= 0) return false;
            return (double)ix * iy > 0.3 * Math.Min((double)p.W * p.H, (double)q.W * q.H);
        }
        var merged = new List<OcrLine>(a.Lines);
        merged.AddRange(b.Lines.Where(l => !a.Lines.Any(x => Overlap(x, l))));
        merged = merged.OrderBy(l => l.Y).ThenBy(l => l.X).ToList();
        return a with { Ms = sw.ElapsedMilliseconds, Lines = merged };
    }

    private static async Task<OcrResult> RunOnceAsync(string imagePath, bool enhance, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(OcrExe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        psi.ArgumentList.Add(imagePath);
        if (enhance) psi.ArgumentList.Add("--enhance");
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Không chạy được " + OcrExe);
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var output = await outTask; var err = await errTask;
        if (p.ExitCode != 0) throw new InvalidOperationException(err.Trim().Length > 0 ? err.Trim() : "OCR lỗi (mã " + p.ExitCode + ")");
        using var doc = JsonDocument.Parse(output);
        var r = doc.RootElement;
        var lang = r.GetProperty("lang").GetString() ?? "";
        // OCR tiếng Anh (máy chưa cài gói OCR tiếng Việt) đọc sai dấu → sửa dấu bằng từ điển cụm / từ hay gặp (dùng chung với ExcelToFrx).
        // Có gói vi-VN thì chữ đã đúng. So khớp source vẫn so không dấu nên không phụ thuộc bước này.
        bool fix = !lang.StartsWith("vi", StringComparison.OrdinalIgnoreCase);
        var lines = r.GetProperty("lines").EnumerateArray()
            .Select(l => new OcrLine(fix ? VnFix(l.GetProperty("text").GetString() ?? "") : l.GetProperty("text").GetString() ?? "",
                l.GetProperty("x").GetInt32(), l.GetProperty("y").GetInt32(), l.GetProperty("w").GetInt32(), l.GetProperty("h").GetInt32()))
            .Where(l => l.Text.Trim().Length > 0).ToList();
        return new OcrResult(lang, r.GetProperty("width").GetInt32(), r.GetProperty("height").GetInt32(), r.GetProperty("ms").GetInt64(), lines);
    }

    private static string VnFix(string s)
    {
        try { return ExcelToFrx.Parsing.VnTextFix.Fix(s, ui: true); } catch { return s; }
    }

    // ---- Chuẩn hoá chữ -------------------------------------------------------------------------------------------------

    /// <summary>Bỏ dấu, chữ thường, đ → d; mọi ký tự không phải chữ / số thành khoảng trắng.</summary>
    public static string Norm(string s)
    {
        var d = (s ?? "").Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var ch in d)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(ch);
            if (c is 'đ' or 'ð') c = 'd';
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    /// <summary>Dạng "lỏng" để so với chữ OCR: <see cref="Norm"/> + quy lỗi OCR hay gặp (1 / | / ! → l, 0 → o) + bỏ khoảng trắng.</summary>
    public static string Loose(string s)
    {
        var n = Norm((s ?? "").Replace('|', 'l').Replace('!', 'l'));
        var sb = new StringBuilder(n.Length);
        foreach (var c in n) { if (c == ' ') continue; sb.Append(c switch { '1' => 'l', '0' => 'o', _ => c }); }
        return sb.ToString();
    }

    private static int[] Trigrams(string loose)
    {
        if (loose.Length < 3) return loose.Length == 0 ? Array.Empty<int>() : new[] { loose.GetHashCode(StringComparison.Ordinal) };
        var set = new HashSet<int>();
        for (var i = 0; i + 3 <= loose.Length; i++) set.Add(HashTri(loose, i));
        var a = set.ToArray(); Array.Sort(a); return a;
    }

    private static int HashTri(string s, int i) => (s[i] * 31 + s[i + 1]) * 31 + s[i + 2];

    /// <summary>Tỉ lệ giống theo số ký tự phải sửa (Levenshtein) trên dạng lỏng — bắt lỗi OCR thay 1–2 chữ ("hoaddnbanhing" ~ "hoadonbanhang")
    /// mà so cụm 3 ký tự chấm thấp vì mỗi chữ sai làm hỏng 3 cụm.</summary>
    private static double LevRatio(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a.Length > 80 || b.Length > 80 || Math.Abs(a.Length - b.Length) > Math.Max(a.Length, b.Length) / 3) return 0;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return 1.0 - (double)prev[b.Length] / Math.Max(a.Length, b.Length);
    }

    private static int Intersect(int[] a, int[] b)
    {
        int i = 0, j = 0, n = 0;
        while (i < a.Length && j < b.Length) { if (a[i] == b[j]) { n++; i++; j++; } else if (a[i] < b[j]) i++; else j++; }
        return n;
    }

    // ---- Chỉ mục màn hình ----------------------------------------------------------------------------------------------

    private enum TextKind { Title = 0, Label = 1, Text = 2 }
    private sealed record IdxText(int Line, int Kind, string Value);
    private sealed class IdxFile { public long Ticks { get; set; } public long Length { get; set; } public List<IdxText> Texts { get; set; } = new(); }
    private sealed class IdxStore { public int Version { get; set; } = 1; public Dictionary<string, IdxFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase); }

    private static readonly Regex Attr = new(@"\b(v|e)=""([^""]{2,300})""", RegexOptions.Compiled);
    private static readonly Regex TagName = new(@"<\s*([\w:]+)[^<>]*$", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, (IdxStore Store, DateTime At, Matcher? M)> Memory = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(3);

    private static string StorePath(string controllers)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(controllers.ToLowerInvariant())))[..16];
        return Path.Combine(BcodePaths.AppData, "Bcode", "screen-index", key + ".json");
    }

    /// <summary>Chữ hiển thị trong 1 file controller: v="…"/e="…" kèm số dòng; trong thẻ &lt;title&gt; là tiêu đề, trong &lt;header&gt; là nhãn ô.</summary>
    private static List<IdxText> Extract(string path)
    {
        var list = new List<IdxText>();
        var lineNo = 0;
        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            if (line.IndexOf("v=\"", StringComparison.Ordinal) < 0 && line.IndexOf("e=\"", StringComparison.Ordinal) < 0) continue;
            foreach (Match m in Attr.Matches(line))
            {
                var val = System.Net.WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
                if (val.Length < 2 || val.StartsWith("&", StringComparison.Ordinal) || !val.Any(char.IsLetter)) continue;
                var tag = TagName.Match(line[..m.Index]);
                var t = tag.Success ? tag.Groups[1].Value.ToLowerInvariant() : "";
                var kind = t == "title" ? TextKind.Title : t == "header" ? TextKind.Label : TextKind.Text;
                list.Add(new IdxText(lineNo, (int)kind, val));
            }
        }
        return list;
    }

    private static (IdxStore Store, DateTime At, Matcher? M) GetStore(string controllers, bool force, Action<string> progress, CancellationToken ct)
    {
        var entry = Memory.GetOrAdd(controllers, c =>
        {
            try { var p = StorePath(c); if (File.Exists(p)) return (JsonSerializer.Deserialize<IdxStore>(File.ReadAllText(p)) ?? new IdxStore(), DateTime.MinValue, null); }
            catch { /* hỏng: dựng lại */ }
            return (new IdxStore(), DateTime.MinValue, null);
        });
        if (!force && DateTime.Now - entry.At < RefreshEvery && entry.Store.Files.Count > 0) return entry;

        progress("Đang liệt kê file controller…");
        var current = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in SourceIndexService.Folders)
        {
            var d = Path.Combine(controllers, folder);
            if (!Directory.Exists(d)) continue;
            try
            {
                foreach (var fi in new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories))
                    if (fi.Extension is ".f" or ".xml" or ".ent" or ".txt") current[fi.FullName] = fi;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        var store = entry.Store;
        var changed = current.Values.Where(fi => !store.Files.TryGetValue(fi.FullName, out var e) || e.Ticks != fi.LastWriteTimeUtc.Ticks || e.Length != fi.Length).ToList();
        var removed = store.Files.Keys.Where(k => !current.ContainsKey(k)).ToList();
        var done = 0;
        var fresh = new ConcurrentDictionary<string, IdxFile>(StringComparer.OrdinalIgnoreCase);
        Parallel.ForEach(changed, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, fi =>
        {
            try { fresh[fi.FullName] = new IdxFile { Ticks = fi.LastWriteTimeUtc.Ticks, Length = fi.Length, Texts = Extract(fi.FullName) }; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            var n = Interlocked.Increment(ref done);
            if (n % 100 == 0) progress($"Đang lập chỉ mục màn hình: {n}/{changed.Count} file…");
        });
        foreach (var kv in fresh) store.Files[kv.Key] = kv.Value;
        foreach (var k in removed) store.Files.Remove(k);
        var matcher = fresh.IsEmpty && removed.Count == 0 ? entry.M : null;
        if (!fresh.IsEmpty || removed.Count > 0)
            try
            {
                var p = StorePath(controllers);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, JsonSerializer.Serialize(store));
            }
            catch { /* không lưu được thì lần sau lập lại */ }
        var next = (store, DateTime.Now, matcher);
        Memory[controllers] = next;
        return next;
    }

    // ---- So khớp -------------------------------------------------------------------------------------------------------

    /// <summary>Bảng tra dựng từ chỉ mục: chữ (dạng lỏng, không trùng) → cụm 3 ký tự + nơi xuất hiện; cụm 3 ký tự → các chữ có nó.</summary>
    private sealed class Matcher
    {
        public readonly List<string> Files = new();
        public readonly List<string> Values = new();                 // chữ gốc (lần gặp đầu)
        public readonly List<string> LooseValues = new();            // dạng lỏng (để so Levenshtein)
        public readonly List<int[]> Tri = new();
        public readonly List<List<(int File, int Kind, int Line)>> Occ = new();
        public readonly Dictionary<int, List<int>> Postings = new();
        public readonly double[] Idf;

        public Matcher(IdxStore store)
        {
            var byLoose = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (path, f) in store.Files)
            {
                var fi = Files.Count; Files.Add(path);
                foreach (var t in f.Texts)
                {
                    var l = Loose(t.Value);
                    if (l.Length < 3) continue;
                    if (!byLoose.TryGetValue(l, out var id))
                    {
                        id = Values.Count; byLoose[l] = id;
                        Values.Add(t.Value); LooseValues.Add(l); Tri.Add(Trigrams(l)); Occ.Add(new());
                        foreach (var g in Tri[id]) { if (!Postings.TryGetValue(g, out var pl)) Postings[g] = pl = new List<int>(); pl.Add(id); }
                    }
                    Occ[id].Add((fi, t.Kind, t.Line));
                }
            }
            Idf = new double[Values.Count];
            var n = Math.Max(1, Files.Count);
            for (var i = 0; i < Values.Count; i++) Idf[i] = Math.Log(1 + (double)n / Occ[i].Select(o => o.File).Distinct().Count());
        }

        /// <summary>Các chữ trong chỉ mục giống <paramref name="text"/> (Dice trên cụm 3 ký tự ≥ ngưỡng). Bỏ qua cụm quá phổ biến khi tìm ứng viên cho nhanh.</summary>
        public List<(int Id, double Sim)> Similar(string text, double min, int take = 12)
        {
            var la = Loose(text);
            var a = Trigrams(la);
            if (a.Length == 0) return new();
            var hits = new Dictionary<int, int>();
            foreach (var g in a)
                if (Postings.TryGetValue(g, out var pl) && pl.Count <= 6000)
                    foreach (var id in pl) hits[id] = hits.TryGetValue(id, out var c) ? c + 1 : 1;
            var res = new List<(int, double)>();
            // lọc thô rộng tay (OCR sai 1 chữ làm hỏng 3 cụm), chấm kỹ bằng max(Dice, Levenshtein)
            foreach (var (id, _) in hits.Where(h => h.Value * 2 >= Math.Min(a.Length, Tri[h.Key].Length) * min * 0.5))
            {
                var b = Tri[id];
                var sim = Math.Max(2.0 * Intersect(a, b) / (a.Length + b.Length), LevRatio(la, LooseValues[id]));
                var need = Math.Min(a.Length, b.Length) <= 4 ? Math.Max(min, 0.9) : min;   // chữ ngắn phải gần như trùng hẳn
                if (sim >= need) res.Add((id, sim));
            }
            return res.OrderByDescending(r => r.Item2).Take(take).ToList();
        }
    }

    /// <param name="Fixed">Dòng chữ OCR → câu đúng dấu trong source / tên menu mà nó khớp (≥ 0,85) — để hiện chữ đúng thay cho chữ OCR sai dấu.</param>
    public sealed record MatchResult(List<ShotScreen> Screens, List<ShotText> Texts, int IndexedFiles, string Note, Dictionary<string, string> Fixed);

    /// <param name="menus">Cây WCommand (tên menu người dùng nhìn thấy trên tab là manh mối mạnh nhất cho "màn hình nào").</param>
    public static Task<MatchResult> MatchAsync(string sourceRoot, IReadOnlyList<OcrLine> lines, IReadOnlyList<(WCommandItem Item, string Path)> menus,
        bool forceIndex, Action<string> progress, CancellationToken ct) => Task.Run(() =>
    {
        var controllers = SourceIndexService.ControllersDir(sourceRoot);
        if (!Directory.Exists(controllers)) throw new DirectoryNotFoundException("Không thấy thư mục " + controllers);
        var entry = GetStore(controllers, forceIndex, progress, ct);
        progress("Đang so khớp…");
        var m = entry.M ?? new Matcher(entry.Store);
        Memory[controllers] = (entry.Store, entry.At, m);
        var root = controllers;
        string Rel(string f) { try { return Path.GetRelativePath(root, f); } catch { return Path.GetFileName(f); } }

        var score = new Dictionary<int, double>();
        var reasons = new Dictionary<int, List<(string, double)>>();
        void Credit(int file, double s, string why)
        {
            score[file] = (score.TryGetValue(file, out var v) ? v : 0) + s;
            if (!reasons.TryGetValue(file, out var l)) reasons[file] = l = new();
            l.Add((why, s));
        }

        // 1) chữ trên ảnh ↔ tiêu đề / nhãn / chữ hiển thị trong file
        var texts = new List<ShotText>();
        var labelHits = new Dictionary<int, int>();   // số dòng chữ khác nhau khớp nhãn ô của file — nhiều nhãn cùng khớp là manh mối mạnh
        var ocr = lines.Select(l => l.Text.Trim()).Where(t => Loose(t).Length >= 3).Distinct().ToList();
        var fixedText = new Dictionary<string, (double Sim, string Text)>();
        void Fix(string ocrText, string good, double sim)
        {
            if (sim < 0.85 || ocrText == good) return;
            // chỉ thay khi khác nhau ở dấu / vài chữ OCR đọc nhầm: cùng số từ, cùng độ dài khi bỏ dấu ("Tên hợp đồng" ≠ "Tiền hợp đồng",
            // "Mã hợp đồng" ≠ "Mã hợp đồng F1"); chữ quá ngắn ("vật" → "VAT") thì thôi
            string a = Norm(ocrText), b = Norm(good);
            if (a.Length < 5 || a.Length != b.Length || a.Split(' ').Length != b.Split(' ').Length) return;
            var score = sim + (a == b ? 1 : 0);   // trùng hẳn khi bỏ dấu thì ưu tiên
            if (!fixedText.TryGetValue(ocrText, out var cur) || cur.Sim < score) fixedText[ocrText] = (score, good);
        }
        foreach (var t in ocr)
        {
            ct.ThrowIfCancellationRequested();
            var perFileBest = new Dictionary<int, (double W, bool Label)>();
            // chữ ngắn chung chung ("Copy", "Lưu", "Thông báo") khớp ở rất nhiều nơi — cộng ít; câu dài khớp mới là manh mối mạnh
            var lenFactor = Math.Min(1.0, Loose(t).Length / 14.0);
            foreach (var (id, sim) in m.Similar(t, 0.75))
            {
                Fix(t, m.Values[id], sim);
                foreach (var o in m.Occ[id].GroupBy(o => o.File).Select(g => g.OrderBy(x => x.Kind).First()))
                {
                    var w = (o.Kind == (int)TextKind.Title ? 3.0 : o.Kind == (int)TextKind.Label ? 1.0 : 0.4) * m.Idf[id] * sim * lenFactor;
                    if (perFileBest.TryGetValue(o.File, out var b) && b.W >= w) continue;   // mỗi dòng chữ chỉ cộng 1 lần / file
                    perFileBest[o.File] = (w, o.Kind != (int)TextKind.Text);
                }
                // câu dài (≥ 3 từ) khớp nguyên câu: liệt kê file + dòng (câu báo lỗi / thông báo)
                // (bỏ nhãn ô: "Mã giao dịch" có ở hàng trăm file — đã tính vào điểm màn hình ở trên)
                if (Norm(t).Split(' ').Length >= 3 && sim >= 0.8)
                    foreach (var o in m.Occ[id].Where(o => o.Kind != (int)TextKind.Label).Take(5))
                        texts.Add(new ShotText(t, m.Values[id], m.Files[o.File], Rel(m.Files[o.File]), o.Line, Math.Round(sim, 2)));
            }
            foreach (var (f, (w, isLabel)) in perFileBest)
            {
                Credit(f, w, t);
                if (isLabel) labelHits[f] = (labelHits.TryGetValue(f, out var n) ? n : 0) + 1;
            }
        }
        foreach (var (f, n) in labelHits) if (n >= 2) Credit(f, 1.5 * (n - 1), $"{n} nhãn ô cùng khớp");

        // 2) chữ trên ảnh ↔ tên menu (tiêu đề tab FBO, manh mối mạnh nhất) → file Dir / Grid / Report của menu đó
        var menuByFile = new Dictionary<int, List<string>>();
        var fileIdx = m.Files.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i, StringComparer.OrdinalIgnoreCase);
        var screenFiles = fileIdx.Where(kv => kv.Key.Contains("\\Dir\\", StringComparison.OrdinalIgnoreCase) || kv.Key.Contains("\\Grid\\", StringComparison.OrdinalIgnoreCase)
                                              || kv.Key.Contains("\\Report\\", StringComparison.OrdinalIgnoreCase)).ToList();
        var menuLoose = menus.Select(x => (x.Item, Bar: Loose(x.Item.Bar), Bar2: Loose(x.Item.Bar2))).Where(x => x.Bar.Length >= 4).ToList();
        var menuMatched = new Dictionary<int, (double Sim, string Bar)>();
        foreach (var t in ocr)
        {
            var la = Loose(t);
            if (la.Length < 4) continue;
            var a = Trigrams(la);
            foreach (var (item, bar, bar2) in menuLoose)
            {
                double Sim(string b) => b.Length == 0 ? 0 : Math.Max(2.0 * Intersect(a, Trigrams(b)) / (a.Length + Trigrams(b).Length), LevRatio(la, b));
                var sim = Math.Max(Sim(bar), Sim(bar2));
                if (sim < 0.78) continue;
                Fix(t, Regex.Replace(item.Bar ?? "", @"\s*\(\*\)\s*$", ""), sim);
                foreach (var (path, fi) in screenFiles)
                    if (DependencyGraphService.MenuMatchesFile(item, path))
                    {
                        if (!menuMatched.TryGetValue(fi, out var cur) || cur.Sim < sim) menuMatched[fi] = (sim, item.Bar);
                        if (!menuByFile.TryGetValue(fi, out var ml)) menuByFile[fi] = ml = new();
                        if (!ml.Contains(item.Bar + " (" + item.WMenuId + ")")) ml.Add(item.Bar + " (" + item.WMenuId + ")");
                    }
            }
        }
        foreach (var (fi, (sim, bar)) in menuMatched) Credit(fi, 10.0 * sim, "tên menu: " + bar);

        // file có menu trỏ tới (màn hình người dùng mở được) cộng thêm; bản sao / bản lưu (" - Copy", bak, old) giảm điểm
        var menusOf = new Dictionary<int, List<string>>();
        foreach (var fi in score.OrderByDescending(kv => kv.Value).Take(40).Select(kv => kv.Key).ToList())
        {
            var path = m.Files[fi];
            var ms = menuByFile.TryGetValue(fi, out var ml) ? ml : menus.Where(x => DependencyGraphService.MenuMatchesFile(x.Item, path)).Select(x => x.Item.Bar + " (" + x.Item.WMenuId + ")").Take(4).ToList();
            menusOf[fi] = ms;
            if (ms.Count > 0) score[fi] += 2;
            if (Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"copy|bak|old|backup|_\d{6,}", RegexOptions.IgnoreCase)) score[fi] *= 0.6;
        }

        var screens = score.Where(kv => menusOf.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).Take(8).Select(kv =>
        {
            var path = m.Files[kv.Key];
            var why = reasons[kv.Key].OrderByDescending(r => r.Item2).Select(r => r.Item1).Distinct().Take(8).ToList();
            return new ShotScreen(path, Rel(path), Math.Round(kv.Value, 1), menusOf[kv.Key], why);
        }).ToList();
        var outTexts = texts.GroupBy(x => x.File + "|" + x.Line).Select(g => g.OrderByDescending(x => x.Similarity).First())
            .OrderByDescending(x => x.Similarity).ThenBy(x => x.Label).Take(30).ToList();
        var note = $"{m.Files.Count:N0} file trong chỉ mục · {ocr.Count} dòng chữ đọc được";
        return new MatchResult(screens, outTexts, m.Files.Count, note, fixedText.ToDictionary(kv => kv.Key, kv => kv.Value.Text));
    }, ct);
}
