using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bcode.App.Services;

/// <summary>
/// File Lookup → "Offline Design Excel File" (như FCode: urFileLookup.Open → iuNfqFfMTB). Mẫu in Excel nằm ở <c>…\Templates\Excel\</c>
/// mà mở thẳng bằng Excel thì Excel khoá file → FBO in báo "file đang được sử dụng". Bật tuỳ chọn này thì File Lookup không mở file gốc:
/// chép ra <c>%AppData%\Bcode\offline-excel\&lt;dự án&gt;\_Offline___&lt;tên&gt;</c> (tiền tố như FCode — Excel không cho mở 2 workbook trùng tên),
/// mở bản sao bằng Excel, theo dõi thư mục đó (FileSystemWatcher); mỗi lần Excel lưu (ghi file tạm rồi đổi tên thành bản sao → Renamed)
/// thì chép bản sao về đè file gốc. Khác FCode (chép đè mù) ở chỗ giữ an toàn dữ liệu:
/// <list type="bullet">
/// <item>Chỉ ghi khi nội dung bản sao khác lần đồng bộ trước (so SHA-256) — lưu mà không đổi thì không đụng file gốc.</item>
/// <item>File gốc bị người khác sửa sau lúc chép ra (băm khác mốc) thì hỏi trước khi đè; không đồng ý thì bản sửa vẫn nằm ở bản sao.</item>
/// <item>Mở lại file khi bản sao còn bản sửa chưa đưa về (Bcode tắt lúc Excel còn mở…) thì hỏi đưa về trước / bỏ / huỷ.</item>
/// <item>Đọc bản sao + ghi file gốc có thử lại (Excel / FBO đang giữ file vài trăm ms); ghi xong đọc lại so băm.</item>
/// </list>
/// Mốc (đường dẫn gốc + băm nội dung lần chép / đồng bộ cuối) lưu ở file kèm <c>&lt;bản sao&gt;.src.json</c>.
/// </summary>
public static class OfflineExcelDesign
{
    private const string Prefix = "_Offline___";

    public static string Root => Path.Combine(BcodePaths.AppData, "Bcode", "offline-excel");

    /// <summary>Báo cho UI (thanh trạng thái File Lookup): (file gốc, null = đã đưa bản sửa về; khác null = lỗi / lý do không ghi).</summary>
    public static event Action<string, string?>? Synced;

    private sealed class Mark
    {
        public string Original { get; set; } = "";
        public string Hash { get; set; } = "";
    }

    private static readonly ConcurrentDictionary<string, Mark> Tracked = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, System.Threading.Timer> Pending = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, FileSystemWatcher> Watchers = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SyncLock = new();
    private static Control? _ui;

    /// <summary>Mẫu in Excel theo luật FCode: đuôi .xlsx / .xls (thêm .xlsm) và nằm ngay trong thư mục <c>Templates\Excel</c>.</summary>
    public static bool IsCandidate(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".xlsx" or ".xls" or ".xlsm")) return false;
        var dir = Path.GetDirectoryName(path) ?? "";
        return dir.Replace('/', '\\').TrimEnd('\\').EndsWith("\\Templates\\Excel", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Mở bản sao offline của <paramref name="original"/> bằng Excel. Trả false nếu người dùng huỷ / lỗi (đã báo).</summary>
    public static bool Open(Control owner, string original, string projectName)
    {
        _ui = owner.FindForm() ?? owner;
        try
        {
            if (!File.Exists(original)) { Warn(owner, $"Không thấy file:\n{original}"); return false; }
            var dir = Path.Combine(Root, Safe(string.IsNullOrWhiteSpace(projectName) ? "_" : projectName));
            Directory.CreateDirectory(dir);
            var copy = Path.Combine(dir, Prefix + Path.GetFileName(original));
            var origHash = HashOf(original);

            if (File.Exists(copy))
            {
                // Bản sao cũ còn bản sửa chưa đưa về file gốc (khác mốc, khác file gốc) → hỏi trước khi chép đè.
                var mark = ReadMark(copy);
                if (IsOpenElsewhere(copy))
                {
                    // Bản offline đang mở trong Excel (FCode báo lỗi "is in used") — không chép đè lên bản đang sửa; mở lại = Excel chuyển sang cửa sổ đó.
                    if (mark is null || !string.Equals(mark.Original, original, StringComparison.OrdinalIgnoreCase))
                        mark = new Mark { Original = original, Hash = origHash };
                    Watch(copy, mark);
                    Start(copy);
                    return true;
                }
                var copyHash = HashOf(copy);
                if (mark is not null && string.Equals(mark.Original, original, StringComparison.OrdinalIgnoreCase)
                    && copyHash != mark.Hash && copyHash != origHash)
                {
                    var ans = MessageBox.Show(owner,
                        $"Bản offline của \"{Path.GetFileName(original)}\" có sửa đổi CHƯA đưa về file gốc (lần trước Bcode không theo dõi được lúc lưu).\n\n" +
                        $"Yes = đưa bản sửa đó về file gốc rồi mở\nNo = bỏ bản sửa đó, chép lại từ file gốc\nCancel = không làm gì\n\nBản offline: {copy}",
                        "Bcode — Offline Design Excel", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
                    if (ans == DialogResult.Cancel) return false;
                    if (ans == DialogResult.Yes)
                    {
                        if (mark.Hash != origHash && MessageBox.Show(owner,
                                "File gốc cũng đã bị sửa (bởi người / chương trình khác) sau lần chép ra. Vẫn ghi đè file gốc bằng bản offline?",
                                "Bcode — Offline Design Excel", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                            return false;
                        WriteOriginal(copy, original, copyHash);
                        WriteMark(copy, new Mark { Original = original, Hash = copyHash });
                        Watch(copy, new Mark { Original = original, Hash = copyHash });
                        Start(copy);
                        return true;
                    }
                }
            }

            File.Copy(original, copy, overwrite: true);
            File.SetAttributes(copy, File.GetAttributes(copy) & ~FileAttributes.ReadOnly);
            var m = new Mark { Original = original, Hash = origHash };
            WriteMark(copy, m);
            Watch(copy, m);
            Start(copy);
            return true;
        }
        catch (Exception ex)
        {
            Warn(owner, "Không mở được bản offline:\n" + ex.Message);
            return false;
        }
    }

    private static void Start(string copy) => Process.Start(new ProcessStartInfo(copy) { UseShellExecute = true });

    private static void Watch(string copy, Mark mark)
    {
        Tracked[copy] = mark;
        var dir = Path.GetDirectoryName(copy)!;
        Watchers.GetOrAdd(dir, d =>
        {
            var w = new FileSystemWatcher(d, "*.*")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Renamed += (_, e) => Schedule(e.FullPath);
            w.Changed += (_, e) => Schedule(e.FullPath);
            w.Created += (_, e) => Schedule(e.FullPath);
            w.EnableRaisingEvents = true;
            return w;
        });
    }

    /// <summary>Excel lưu = nhiều sự kiện liền nhau (ghi tạm, đổi tên, xoá) → gom lại, 1,2 s sau sự kiện cuối mới đồng bộ.</summary>
    private static void Schedule(string path)
    {
        if (!Tracked.ContainsKey(path)) return;
        var t = Pending.GetOrAdd(path, p => new System.Threading.Timer(_ => { Pending.TryRemove(p, out var old); old?.Dispose(); SyncNow(p); }));
        t.Change(1200, Timeout.Infinite);
    }

    private static void SyncNow(string copy)
    {
        lock (SyncLock)
        {
            if (!Tracked.TryGetValue(copy, out var mark)) return;
            var original = mark.Original;
            try
            {
                if (!File.Exists(copy)) return;                       // đang giữa lúc Excel đổi tên — sự kiện sau sẽ gọi lại
                var copyHash = Retry(() => HashOf(copy));
                if (copyHash == mark.Hash) return;                    // không đổi nội dung → không ghi file gốc
                if (!File.Exists(original)) { Synced?.Invoke(original, "file gốc không còn — bản sửa vẫn ở " + copy); return; }
                var origHash = Retry(() => HashOf(original));
                if (origHash == copyHash) { mark.Hash = copyHash; WriteMark(copy, mark); return; }
                if (origHash != mark.Hash && !AskOnUi(
                        $"File gốc \"{Path.GetFileName(original)}\" đã bị sửa (bởi người / chương trình khác) sau lúc chép ra bản offline.\n\n" +
                        $"Ghi đè file gốc bằng bản bạn vừa lưu trong Excel?\n(No = giữ file gốc; bản sửa vẫn ở {copy})"))
                {
                    Synced?.Invoke(original, "không ghi đè (file gốc đã bị sửa ở nơi khác) — bản sửa vẫn ở bản offline");
                    return;
                }
                WriteOriginal(copy, original, copyHash);
                mark.Hash = copyHash;
                WriteMark(copy, mark);
                Synced?.Invoke(original, null);
            }
            catch (Exception ex)
            {
                Synced?.Invoke(original, ex.Message + " — bản sửa vẫn ở " + copy);
                var ui = _ui;
                if (ui is { IsDisposed: false, IsHandleCreated: true })
                    ui.BeginInvoke(new Action(() => Warn(ui, $"Không đưa được bản sửa về file gốc:\n{original}\n\n{ex.Message}\n\nBản sửa vẫn ở:\n{copy}\n(lưu lại trong Excel để thử lại)")));
            }
        }
    }

    /// <summary>Chép bản offline về đè file gốc (qua 1 bản chụp cục bộ để không đọc file Excel đang ghi dở), thử lại khi file gốc đang bị giữ, rồi đọc lại so băm.</summary>
    private static void WriteOriginal(string copy, string original, string expectHash)
    {
        var snap = copy + ".sync";
        Retry(() => { File.Copy(copy, snap, overwrite: true); return 0; });
        try
        {
            if (HashOf(snap) != expectHash) throw new IOException("bản offline vừa đổi trong lúc chép — lưu lại trong Excel để thử lại.");
            var attrs = File.GetAttributes(original);
            if ((attrs & FileAttributes.ReadOnly) != 0) throw new IOException("file gốc đang Read-only.");
            Retry(() => { File.Copy(snap, original, overwrite: true); return 0; });
            if (Retry(() => HashOf(original)) != expectHash) throw new IOException("đọc lại file gốc không khớp bản offline.");
        }
        finally { try { File.Delete(snap); } catch { } }
    }

    private static T Retry<T>(Func<T> f)
    {
        for (var i = 0; ; i++)
        {
            try { return f(); }
            catch (IOException) when (i < 15) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) when (i < 3) { Thread.Sleep(300); }
        }
    }

    private static bool AskOnUi(string text)
    {
        var ui = _ui;
        if (ui is not { IsDisposed: false, IsHandleCreated: true }) return false;
        return (bool)ui.Invoke(new Func<bool>(() => MessageBox.Show(ui, text, "Bcode — Offline Design Excel",
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes));
    }

    private static string HashOf(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <summary>Có chương trình khác (Excel) đang mở file — Excel giữ handle suốt lúc workbook còn mở.</summary>
    private static bool IsOpenElsewhere(string path)
    {
        try { using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
    }

    private static Mark? ReadMark(string copy)
    {
        try { return JsonSerializer.Deserialize<Mark>(File.ReadAllText(copy + ".src.json")); } catch { return null; }
    }

    private static void WriteMark(string copy, Mark m)
    {
        try { File.WriteAllText(copy + ".src.json", JsonSerializer.Serialize(m)); } catch { }
    }

    private static string Safe(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    private static void Warn(IWin32Window owner, string text) =>
        MessageBox.Show(owner, text, "Bcode — Offline Design Excel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
}
