namespace Bcode.Shared;

/// <summary>
/// Chuyển dữ liệu kiến thức (ghost text + Hỏi Fast) từ thư mục cũ sang thư mục mới — nút "Chuyển dữ liệu sang đây" ở Template → Đường dẫn →
/// Thư mục kiến thức (Bcode) và Settings → Gợi ý &amp; Hỏi Fast → Thư mục kiến thức chung (BcodeViewer). Dùng khi ổ C đầy: khai thư mục ở ổ khác
/// rồi bấm chuyển, dữ liệu ở chỗ cũ được xoá sau khi đã chép xong từng file (chép ra file tạm → đổi tên → so độ dài → mới xoá bản cũ).
/// File LINK (không chép) vào Bcode.App và BcodeViewer.App.
///
/// Chỗ mới đã có file cùng tên: <c>merge</c> gộp được (vd feedback.json của Bcode) → gộp rồi xoá bản cũ; giống hệt → xoá bản cũ;
/// file xác nhận (feedback*, thư mục feedback\) không gộp được → GIỮ ở chỗ cũ, báo lại; còn lại bản nào sửa sau thì giữ bản đó (bản cũ hơn bị bỏ).
/// File *.tmp (đang ghi dở) để nguyên. Thư mục rỗng ở chỗ cũ được xoá.
/// </summary>
internal static class KnowledgeMover
{
    public sealed class Result
    {
        public int Moved, Merged, Replaced, Dropped;
        public long Bytes;
        public readonly List<string> Kept = new();
        public readonly List<string> Errors = new();
        public bool Stopped;

        public string Summary(string from, string to)
        {
            var parts = new List<string> { $"đã chuyển {Moved + Replaced} file ({Mb(Bytes)})" };
            if (Merged > 0) parts.Add($"gộp {Merged} file xác nhận");
            if (Dropped > 0) parts.Add($"xoá {Dropped} file ở chỗ cũ vì chỗ mới đã có bản giống / mới hơn");
            if (Kept.Count > 0) parts.Add($"giữ lại ở chỗ cũ {Kept.Count} file trùng tên: " + string.Join(", ", Kept.Take(5)) + (Kept.Count > 5 ? "…" : ""));
            if (Errors.Count > 0) parts.Add($"{Errors.Count} lỗi: " + Errors[0] + (Stopped ? " — đã dừng, phần còn lại vẫn ở chỗ cũ (bấm lại để chuyển tiếp)" : ""));
            return $"{from} → {to}: " + string.Join(" · ", parts) + ".";
        }
    }

    public static string Mb(long bytes) => bytes >= 1024 * 1024 * 1024L ? $"{bytes / (1024.0 * 1024 * 1024):0.0} GB"
        : bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.0} MB" : $"{Math.Max(1, (bytes + 1023) / 1024)} KB";

    /// <summary>Cùng 1 thư mục (so đường dẫn đầy đủ, không phân biệt hoa thường)?</summary>
    public static bool SameDir(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Tổng dung lượng + số file của các thư mục con <paramref name="parts"/> trong <paramref name="root"/> (bỏ <paramref name="exclude"/>).</summary>
    public static (long Bytes, int Files) Measure(string root, IReadOnlyList<string> parts, IReadOnlyList<string>? exclude = null)
    {
        long bytes = 0; var files = 0;
        foreach (var f in Files(root, parts, exclude)) { try { bytes += new FileInfo(f).Length; files++; } catch { /* file vừa mất */ } }
        return (bytes, files);
    }

    private static IEnumerable<string> Files(string root, IReadOnlyList<string> parts, IReadOnlyList<string>? exclude)
    {
        foreach (var part in parts)
        {
            var dir = Path.Combine(root, part);
            if (!Directory.Exists(dir)) continue;
            IEnumerable<string> all;
            try { all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToList(); } catch { continue; }
            foreach (var f in all)
            {
                var rel = Path.GetRelativePath(root, f);
                if (f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                if (exclude is not null && exclude.Any(x => rel.StartsWith(x.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))) continue;
                yield return f;
            }
        }
    }

    /// <summary>Chuyển các thư mục con <paramref name="parts"/> (vd "ghost", "fastask", "viewer") từ <paramref name="from"/> sang <paramref name="to"/>.
    /// <paramref name="merge"/>(file cũ, file mới) trả true nếu đã gộp nội dung file cũ vào chỗ mới (xoá được bản cũ).</summary>
    public static Result Move(string from, string to, IReadOnlyList<string> parts, IReadOnlyList<string>? exclude = null,
        Func<string, string, bool>? merge = null, Action<string>? progress = null)
    {
        var r = new Result();
        if (SameDir(from, to)) return r;
        var list = Files(from, parts, exclude).ToList();
        var done = 0;
        foreach (var src in list)
        {
            done++;
            var rel = Path.GetRelativePath(from, src);
            var dst = Path.Combine(to, rel);
            if (done % 20 == 0 || done == list.Count) progress?.Invoke($"Đang chuyển {done}/{list.Count} file…");
            try
            {
                // đọc trước khi xoá (FileInfo đọc thuộc tính lúc dùng lần đầu — xoá rồi mới đọc là lỗi "không thấy file")
                var info = new FileInfo(src);
                var length = info.Length;
                var written = info.LastWriteTimeUtc;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (!File.Exists(dst))
                {
                    CopyVerified(src, dst, overwrite: false);
                    File.Delete(src);
                    r.Moved++; r.Bytes += length;
                    continue;
                }
                if (merge is not null && merge(src, dst)) { File.Delete(src); r.Merged++; continue; }
                if (SameContent(src, dst)) { File.Delete(src); r.Dropped++; continue; }
                if (IsFeedback(rel)) { r.Kept.Add(rel); continue; }
                if (File.GetLastWriteTimeUtc(dst) >= written) { File.Delete(src); r.Dropped++; continue; }
                CopyVerified(src, dst, overwrite: true);
                File.Delete(src);
                r.Replaced++; r.Bytes += length;
            }
            catch (Exception ex)
            {
                r.Errors.Add(rel + ": " + ex.Message);
                // Ổ mới đầy / mất mạng thì file sau cũng hỏng — dừng, dữ liệu chưa chuyển vẫn nguyên ở chỗ cũ.
                if (ex is IOException io && (io.HResult & 0xFFFF) is 0x70 or 0x27 or 0x35 or 0x40 or 0x43 || r.Errors.Count >= 20) { r.Stopped = true; break; }
            }
        }
        foreach (var part in parts) DeleteEmptyDirs(Path.Combine(from, part));
        return r;
    }

    /// <summary>File xác nhận / phản hồi của người dùng (feedback.json, thư mục feedback\&lt;máy-người&gt;.json…) — trùng tên mà không gộp được thì giữ cả hai.</summary>
    private static bool IsFeedback(string rel) =>
        rel.Split('\\', '/').Any(seg => seg.StartsWith("feedback", StringComparison.OrdinalIgnoreCase));

    /// <summary>Chép ra file tạm cạnh đích → kiểm tra độ dài → đổi tên thành đích. Hỏng giữa chừng thì xoá file tạm, đích không bị đụng.</summary>
    private static void CopyVerified(string src, string dst, bool overwrite)
    {
        var tmp = dst + "." + Environment.ProcessId + ".moving.tmp";
        try
        {
            File.Copy(src, tmp, overwrite: true);
            File.SetLastWriteTimeUtc(tmp, File.GetLastWriteTimeUtc(src));
            if (new FileInfo(tmp).Length != new FileInfo(src).Length) throw new IOException("Chép không đủ dung lượng file.");
            File.Move(tmp, dst, overwrite);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* bỏ qua */ }
            throw;
        }
    }

    private static bool SameContent(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead(); using var sb = fb.OpenRead();
        var ba = new byte[81920]; var bb = new byte[81920];
        while (true)
        {
            var na = sa.Read(ba, 0, ba.Length);
            if (na == 0) return true;
            var nb = 0;
            while (nb < na) { var n = sb.Read(bb, nb, na - nb); if (n == 0) return false; nb += n; }
            if (!ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, na))) return false;
        }
    }

    private static void DeleteEmptyDirs(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.GetDirectories(dir)) DeleteEmptyDirs(d);
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { /* đang có người dùng — để lại */ }
    }
}
