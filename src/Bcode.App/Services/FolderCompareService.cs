using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bcode.App.Services;

/// <summary>
/// "So sánh thư mục" (Controls/FolderCompareControl.cs + Web/Shell/foldercompare.html): so 2 thư mục (vd 2 thư mục bin) theo đường dẫn tương đối,
/// So sánh CHỈ ĐỌC; chép A → B chỉ chạy khi người dùng tick file + xác nhận (<see cref="CopyAsync"/>, sao lưu file B bị ghi đè trước), không bao giờ xoá. Khác cỡ = khác; cùng cỡ + cùng giờ sửa (lệch ≤ 2 giây) = giống, không đọc file. Cùng cỡ khác giờ: chế độ chính xác
/// đọc so nội dung từng khối (bắt được file build lại y hệt → "chỉ khác giờ sửa"), chế độ nhanh coi là khác. Chạy song song vài luồng
/// (thư mục bin ~3.800 file: băm SHA-256 hết mất ~150 giây, cách này vài giây). File .dll / .exe khác nhau kèm FileVersion 2 bên. Bỏ qua theo mẫu
/// ("*.pdb; obj\; *.WebView2\") — thư mục khớp mẫu không bị duyệt vào. Chạy nền, báo tiến độ, huỷ được.
/// </summary>
public static class FolderCompareService
{
    public const string Diff = "diff", OnlyA = "onlyA", OnlyB = "onlyB", Same = "same";

    /// <summary>1 file. Size/Time null = bên đó không có. Reason: vì sao khác ("khác cỡ", "khác nội dung", "khác giờ sửa") hoặc ghi chú khi giống.
    /// Text = cả 2 bên là file chữ (mở được bằng Compare Text).</summary>
    public sealed record Entry(string Rel, string Status, long? SizeA, long? SizeB, string? TimeA, string? TimeB,
        string VerA, string VerB, bool Text, string Reason);

    public sealed record Result(List<Entry> Entries, int Ignored, List<string> Errors, long ElapsedMs, bool Exact);

    /// <summary>Stage: "scan" (liệt kê file) / "compare" (so từng file).</summary>
    public sealed record Progress(string Stage, int Done, int Total, string File);

    /// <summary>Mẫu bỏ qua mặc định cho thư mục bin.</summary>
    public const string DefaultIgnore = "*.pdb; obj\\; *.WebView2\\; *.log";

    public const long MaxTextBytes = 20L << 20;   // Compare Text: file lớn hơn 20 MB coi như không mở được

    public static Task<Result> CompareAsync(string folderA, string folderB, bool exact, string ignore, IProgress<Progress>? progress, CancellationToken ct)
        => Task.Run(() => Compare(folderA, folderB, exact, ignore, progress, ct), ct);

    private static Result Compare(string folderA, string folderB, bool exact, string ignore, IProgress<Progress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var a = Normalize(folderA, "A");
        var b = Normalize(folderB, "B");
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Hai thư mục trùng nhau.");

        var rules = IgnoreRules.Parse(ignore);
        var errors = new List<string>();
        var ignored = 0;
        var last = Stopwatch.StartNew();
        void Report(Progress p, bool force = false)
        {
            if (!force && last.ElapsedMilliseconds < 150) return;
            last.Restart(); progress?.Report(p);
        }

        var filesA = Scan(a, rules, errors, ref ignored, n => Report(new Progress("scan", n, 0, "A")), ct);
        var filesB = Scan(b, rules, errors, ref ignored, n => Report(new Progress("scan", filesA.Count + n, 0, "B")), ct);

        var all = filesA.Keys.Union(filesB.Keys, StringComparer.OrdinalIgnoreCase).ToList();
        var slots = new Entry[all.Count];
        var done = 0;
        // song song vài luồng: phần lớn thời gian là chờ đọc đĩa / mạng
        Parallel.For(0, all.Count, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 6) }, i =>
        {
            var rel = all[i];
            filesA.TryGetValue(rel, out var fa);
            filesB.TryGetValue(rel, out var fb);
            try { slots[i] = CompareOne(rel, fa, fb, exact, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lock (errors) errors.Add(rel + ": " + ex.Message);
                slots[i] = new Entry(rel, Diff, fa?.Length, fb?.Length, Stamp(fa), Stamp(fb), "", "", false, "không đọc được: " + ex.Message);
            }
            var n = Interlocked.Increment(ref done);
            lock (last) Report(new Progress("compare", n, all.Count, rel));
        });
        Report(new Progress("compare", all.Count, all.Count, ""), force: true);
        var entries = slots.ToList();

        static int Order(string s) => s switch { Diff => 0, OnlyA => 1, OnlyB => 2, _ => 3 };
        entries.Sort((x, y) => Order(x.Status) != Order(y.Status) ? Order(x.Status) - Order(y.Status) : string.Compare(x.Rel, y.Rel, StringComparison.OrdinalIgnoreCase));
        return new Result(entries, ignored, errors, sw.ElapsedMilliseconds, exact);
    }

    private static string Normalize(string folder, string side)
    {
        var f = (folder ?? "").Trim().Trim('"');
        if (f.Length == 0) throw new InvalidOperationException($"Chưa chọn thư mục {side}.");
        var full = Path.GetFullPath(f).TrimEnd('\\', '/');
        if (!Directory.Exists(full)) throw new InvalidOperationException($"Thư mục {side} không tồn tại hoặc không có quyền đọc: {full}");
        return full;
    }

    /// <summary>Cùng cỡ + cùng giờ sửa (lệch ≤ 2 giây) = giống, không đọc nội dung (như robocopy). Cùng cỡ khác giờ: chế độ chính xác đọc so từng khối
    /// cả 2 bên (dừng ở khối khác đầu tiên), chế độ nhanh coi là khác.</summary>
    private static Entry CompareOne(string rel, FileInfo? fa, FileInfo? fb, bool exact, CancellationToken ct)
    {
        if (fa is null) return new Entry(rel, OnlyB, null, fb!.Length, null, Stamp(fb), "", Ver(fb), false, "");
        if (fb is null) return new Entry(rel, OnlyA, fa.Length, null, Stamp(fa), null, Ver(fa), "", false, "");

        string status, reason;
        var timeSame = Math.Abs((fa.LastWriteTimeUtc - fb.LastWriteTimeUtc).TotalSeconds) <= 2;
        if (fa.Length != fb.Length) { status = Diff; reason = "khác cỡ"; }
        else if (timeSame) { status = Same; reason = ""; }
        else if (exact)
        {
            var same = SameContent(fa.FullName, fb.FullName, ct);
            status = same ? Same : Diff;
            reason = same ? "cùng nội dung, chỉ khác giờ sửa" : "cùng cỡ, khác nội dung";
        }
        else
        {
            status = timeSame ? Same : Diff;
            reason = timeSame ? "" : "cùng cỡ, khác giờ sửa (chưa so nội dung)";
        }

        if (status == Same) return new Entry(rel, Same, fa.Length, fb.Length, Stamp(fa), Stamp(fb), "", "", false, reason);
        var text = fa.Length <= MaxTextBytes && fb.Length <= MaxTextBytes && IsText(fa.FullName) && IsText(fb.FullName);
        return new Entry(rel, Diff, fa.Length, fb.Length, Stamp(fa), Stamp(fb), Ver(fa), Ver(fb), text, reason);
    }

    /// <summary>Liệt kê file (đường dẫn tương đối → FileInfo). Thư mục khớp mẫu bỏ qua thì không duyệt vào; thư mục không vào được ghi lỗi rồi đi tiếp.</summary>
    private static Dictionary<string, FileInfo> Scan(string root, IgnoreRules rules, List<string> errors, ref int ignored, Action<int> onCount, CancellationToken ct)
    {
        var map = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<DirectoryInfo>();
        stack.Push(new DirectoryInfo(root));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();
            IEnumerable<FileSystemInfo> items;
            try { items = dir.EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                errors.Add(Path.GetRelativePath(root, dir.FullName) + "\\: " + ex.Message);
                continue;
            }
            foreach (var it in items)
            {
                var rel = Path.GetRelativePath(root, it.FullName);
                if (it is DirectoryInfo d)
                {
                    if ((d.Attributes & FileAttributes.ReparsePoint) != 0) continue;   // junction / symlink: tránh vòng lặp
                    if (rules.IgnoreDir(d.Name, rel)) { ignored++; continue; }
                    stack.Push(d);
                }
                else if (it is FileInfo f)
                {
                    if (rules.IgnoreFile(f.Name, rel)) { ignored++; continue; }
                    map[rel] = f;
                    if ((map.Count & 63) == 0) onCount(map.Count);
                }
            }
        }
        onCount(map.Count);
        return map;
    }

    // ---------------------------------------------------------------- chép A → B (người dùng tick + xác nhận)

    public sealed record CopyResult(int Copied, int Added, int Overwritten, string BackupDir, List<string> Errors);

    /// <summary>Thư mục sao lưu file B bị ghi đè: %LocalAppData%\Bcode\foldercompare-backup\yyyyMMdd-HHmmss (giữ đường dẫn tương đối).</summary>
    public static string NewBackupDir() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bcode",
        "foldercompare-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    /// <summary>Đường dẫn tương đối hợp lệ (không thoát ra ngoài thư mục gốc).</summary>
    public static bool SafeRel(string rel) =>
        rel.Length > 0 && !Path.IsPathRooted(rel) && !rel.Split('\\', '/').Any(s => s == "..");

    /// <summary>Chép các file <paramref name="rels"/> từ A sang B (ghi đè, giữ giờ sửa). File B sắp bị ghi đè được sao vào <paramref name="backupDir"/> trước.
    /// File lỗi (đang bị khoá, chỉ đọc, thiếu quyền...) ghi vào Errors rồi chép tiếp file khác.</summary>
    public static Task<CopyResult> CopyAsync(string folderA, string folderB, IReadOnlyList<string> rels, string backupDir, IProgress<Progress>? progress, CancellationToken ct)
        => Task.Run(() =>
        {
            var a = Normalize(folderA, "A");
            var b = Normalize(folderB, "B");
            var errors = new List<string>();
            int copied = 0, added = 0, over = 0;
            for (var i = 0; i < rels.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var rel = rels[i];
                progress?.Report(new Progress("copy", i, rels.Count, rel));
                if (!SafeRel(rel)) { errors.Add(rel + ": đường dẫn không hợp lệ"); continue; }
                var src = Path.Combine(a, rel);
                var dst = Path.Combine(b, rel);
                try
                {
                    if (!File.Exists(src)) { errors.Add(rel + ": không còn file ở A"); continue; }
                    var exists = File.Exists(dst);
                    if (exists)
                    {
                        var bak = Path.Combine(backupDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(bak)!);
                        File.Copy(dst, bak, true);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, true);   // File.Copy giữ giờ sửa của file nguồn
                    copied++;
                    if (exists) over++; else added++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(rel + ": " + ex.Message);
                }
            }
            progress?.Report(new Progress("copy", rels.Count, rels.Count, ""));
            return new CopyResult(copied, added, over, over > 0 ? backupDir : "", errors);
        }, ct);

    private static string? Stamp(FileInfo? f) => f?.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>FileVersion của .dll / .exe ("" nếu không có / không phải).</summary>
    private static string Ver(FileInfo? f)
    {
        if (f is null) return "";
        var ext = f.Extension.ToLowerInvariant();
        if (ext is not (".dll" or ".exe")) return "";
        try { return FileVersionInfo.GetVersionInfo(f.FullName).FileVersion?.Trim() ?? ""; }
        catch { return ""; }
    }

    /// <summary>So nội dung 2 file cùng cỡ theo từng khối 64 KB — khác ở khối nào thì dừng luôn (không phải đọc hết như băm).</summary>
    private static bool SameContent(string a, string b, CancellationToken ct)
    {
        const int Block = 1 << 16;
        using var sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Block, FileOptions.SequentialScan);
        using var sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Block, FileOptions.SequentialScan);
        var ba = new byte[Block]; var bb = new byte[Block];
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var na = sa.ReadAtLeast(ba, Block, throwOnEndOfStream: false);
            var nb = sb.ReadAtLeast(bb, Block, throwOnEndOfStream: false);
            if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) return false;
            if (na < Block) return true;
        }
    }

    /// <summary>File chữ: có BOM (UTF-8 / UTF-16 — file .f của Fast là UTF-16) hoặc 8 KB đầu không có byte 0.</summary>
    public static bool IsText(string path)
    {
        try
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[8192];
            var n = s.Read(buf, 0, buf.Length);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true;
            if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return true;
            return Array.IndexOf(buf, (byte)0, 0, n) < 0;
        }
        catch { return false; }
    }

    /// <summary>Mẫu bỏ qua, ngăn bởi ; hoặc , : "obj\" (kết thúc bằng \ hoặc /) = thư mục tên đó ở mọi cấp; "*.pdb" = tên file;
    /// mẫu có \ ở giữa ("runtimes\*.dll") = so với đường dẫn tương đối. Dùng * và ?; không phân biệt hoa thường.</summary>
    private sealed class IgnoreRules
    {
        private readonly List<Regex> _dirs = new(), _names = new(), _paths = new();

        public static IgnoreRules Parse(string? text)
        {
            var r = new IgnoreRules();
            foreach (var raw in (text ?? "").Split(new[] { ';', ',', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var p = raw.Replace('/', '\\');
                if (p.EndsWith('\\')) r._dirs.Add(Glob(p.TrimEnd('\\')));
                else if (p.Contains('\\')) r._paths.Add(Glob(p.TrimStart('\\')));
                else r._names.Add(Glob(p));
            }
            return r;
        }

        private static Regex Glob(string p) =>
            new("^" + Regex.Escape(p).Replace(@"\*", @"[^\\]*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public bool IgnoreDir(string name, string rel) => _dirs.Any(x => x.IsMatch(name) || x.IsMatch(rel));
        public bool IgnoreFile(string name, string rel) => _names.Any(x => x.IsMatch(name)) || _paths.Any(x => x.IsMatch(rel));
    }
}
