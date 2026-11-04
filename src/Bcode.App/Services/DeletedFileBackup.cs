using System.Text.Json;

namespace Bcode.App.Services;

/// <summary>
/// Bản sao file trước khi File Lookup "Delete file" xoá hẳn (file trên UNC không vào Thùng rác) — để "Khôi phục file vừa xoá".
/// Mỗi lần xoá = 1 thư mục <c>%AppData%\Bcode\deleted-files\yyyyMMdd-HHmmss-fff</c> gồm bản sao (đặt tên số thứ tự_tên gốc) + manifest.json
/// [{original, backup}]. Khôi phục lấy lần xoá mới nhất, chép trả về đúng đường dẫn cũ (không ghi đè file đã có), xong thì bỏ các mục đã xử lý —
/// hết mục thì xoá thư mục, nên bấm Khôi phục lần nữa sẽ lùi về lần xoá trước đó. Giữ <see cref="Models.AppSettings.FileLookupDeleteBackupDays"/> ngày
/// (0 = không sao lưu, xoá như cũ); thư mục cũ hơn bị dọn mỗi lần xoá.
/// </summary>
public static class DeletedFileBackup
{
    /// <param name="Attributes">Thuộc tính file gốc (read-only...) để trả lại lúc khôi phục.</param>
    public sealed record Entry(string Original, string Backup, FileAttributes Attributes = FileAttributes.Normal);

    public sealed class Batch
    {
        public string Dir { get; init; } = "";
        public DateTime Time { get; init; }
        public List<Entry> Files { get; init; } = new();
    }

    private const string ManifestName = "manifest.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Root => Path.Combine(BcodePaths.AppData, "Bcode", "deleted-files");

    /// <summary>Mở 1 lần xoá mới (dọn các lần cũ hơn <paramref name="keepDays"/> ngày trước).</summary>
    public static Batch Begin(int keepDays)
    {
        Cleanup(keepDays);
        var now = DateTime.Now;
        var dir = Path.Combine(Root, now.ToString("yyyyMMdd-HHmmss-fff"));
        for (var i = 1; Directory.Exists(dir); i++) dir = Path.Combine(Root, now.ToString("yyyyMMdd-HHmmss-fff") + "-" + i);
        Directory.CreateDirectory(dir);
        return new Batch { Dir = dir, Time = now };
    }

    /// <summary>Chép <paramref name="original"/> vào lần xoá; lỗi (không đọc được...) thì ném ra — nơi gọi KHÔNG xoá file đó.</summary>
    public static Entry Add(Batch batch, string original)
    {
        var backup = Path.Combine(batch.Dir, $"{batch.Files.Count + 1:D4}_{Path.GetFileName(original)}");
        var attributes = File.GetAttributes(original);
        File.Copy(original, backup, overwrite: true);
        File.SetAttributes(backup, FileAttributes.Normal);
        var e = new Entry(original, backup, attributes);
        batch.Files.Add(e);
        return e;
    }

    /// <summary>Bỏ 1 mục (file xoá không được nên không cần bản sao).</summary>
    public static void Drop(Batch batch, Entry entry)
    {
        batch.Files.Remove(entry);
        try { File.Delete(entry.Backup); } catch { /* bỏ qua */ }
    }

    /// <summary>Ghi manifest; lần xoá không còn file nào thì xoá luôn thư mục.</summary>
    public static void Commit(Batch batch)
    {
        if (batch.Files.Count == 0) { TryDeleteDir(batch.Dir); return; }
        File.WriteAllText(Path.Combine(batch.Dir, ManifestName), JsonSerializer.Serialize(batch.Files, Json));
    }

    /// <summary>Lần xoá mới nhất còn file để khôi phục (null = không có).</summary>
    public static Batch? Latest()
    {
        try
        {
            if (!Directory.Exists(Root)) return null;
            foreach (var dir in Directory.GetDirectories(Root).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
            {
                var manifest = Path.Combine(dir, ManifestName);
                if (!File.Exists(manifest)) continue;
                var files = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(manifest)) ?? new();
                if (files.Count == 0) continue;
                return new Batch { Dir = dir, Time = TimeOf(dir), Files = files };
            }
        }
        catch { /* thư mục sao lưu hỏng — coi như không có */ }
        return null;
    }

    public sealed record RestoreResult(List<string> Restored, List<string> AlreadyExists, List<string> Failed);

    /// <summary>Chép trả từng file về chỗ cũ (tạo lại thư mục nếu mất). File đích đã có thì bỏ qua (không ghi đè). Mục lỗi được giữ lại để thử lần sau.</summary>
    public static RestoreResult Restore(Batch batch)
    {
        var restored = new List<string>(); var exists = new List<string>(); var failed = new List<string>();
        var remaining = new List<Entry>();
        foreach (var e in batch.Files)
        {
            try
            {
                if (File.Exists(e.Original)) { exists.Add(e.Original); continue; }
                if (!File.Exists(e.Backup)) { failed.Add($"{e.Original}\n    → mất bản sao"); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(e.Original)!);
                File.Copy(e.Backup, e.Original);
                try { File.SetAttributes(e.Original, e.Attributes); } catch { /* giữ thuộc tính mặc định */ }
                restored.Add(e.Original);
            }
            catch (Exception ex) { failed.Add($"{e.Original}\n    → {ex.Message}"); remaining.Add(e); }
        }
        if (remaining.Count == 0) TryDeleteDir(batch.Dir);
        else
        {
            foreach (var e in batch.Files.Except(remaining)) { try { File.Delete(e.Backup); } catch { } }
            File.WriteAllText(Path.Combine(batch.Dir, ManifestName), JsonSerializer.Serialize(remaining, Json));
        }
        return new RestoreResult(restored, exists, failed);
    }

    private static void Cleanup(int keepDays)
    {
        try
        {
            if (!Directory.Exists(Root)) return;
            var limit = DateTime.Now.AddDays(-Math.Max(keepDays, 0));
            foreach (var dir in Directory.GetDirectories(Root))
                if (TimeOf(dir) < limit) TryDeleteDir(dir);
        }
        catch { /* dọn không được thì để lần sau */ }
    }

    /// <summary>Giờ xoá = tên thư mục (yyyyMMdd-HHmmss-fff); tên lạ thì lấy giờ tạo thư mục.</summary>
    private static DateTime TimeOf(string dir)
    {
        var name = Path.GetFileName(dir);
        return name.Length >= 19 && DateTime.TryParseExact(name[..19], "yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var t) ? t : Directory.GetCreationTime(dir);
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* bỏ qua */ }
    }
}
