namespace Bcode.App.Services.ExcelToRpt;

/// <summary>
/// Tự tìm template .rpt làm khuôn (logic của webapp/server.py):
/// <list type="number">
/// <item>File .rpt TRÙNG TÊN với file Excel (ZPATran_01.xlsx -> ZPATran_01.rpt)</item>
/// <item>File khai ở RptDefaultTemplate (mặc định rptCustomerBalanceOfMultiAccount_01.rpt)</item>
/// <item>BẤT KỲ file .rpt nào trong các thư mục tìm, mới nhất trước — thà mượn tạm một khuôn
///   còn hơn bắt người dùng đi tìm file. File do tool sinh ra bị loại: chúng là KẾT QUẢ, dùng
///   lại sẽ nhân bản mọi sai sót của lần trước.</item>
/// </list>
/// </summary>
internal sealed class TemplateLocator
{
    private static readonly string[] Generated = { ".generated.rpt", "_preview.rpt", ".__preview.rpt" };

    private readonly Func<IReadOnlyList<string>> _folders;
    private readonly Func<string> _defaultName;

    /// <param name="folders">Thư mục tìm template, theo thứ tự ưu tiên (có thể là đường dẫn mạng).</param>
    /// <param name="defaultName">Tên (hoặc đường dẫn tuyệt đối) template mặc định; rỗng = bỏ bước 2.</param>
    public TemplateLocator(Func<IReadOnlyList<string>> folders, Func<string> defaultName)
    {
        _folders = folders;
        _defaultName = defaultName;
    }

    public IReadOnlyList<string> Folders => _folders();

    public string DefaultName => _defaultName();

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    /// <summary>File &lt;stem&gt;.rpt trong các thư mục tìm (+ thư mục phiên), hoặc null.</summary>
    public string? FindByStem(string stem, string? workDir)
    {
        var folders = Folders.ToList();
        if (workDir is not null) folders.Add(workDir);
        foreach (var folder in folders)
        {
            var cand = Path.Combine(folder, stem + ".rpt");
            if (SafeExists(cand)) return cand;
        }
        return null;
    }

    /// <summary>Template mặc định (bước 2, rồi bước 3), hoặc null nếu không có gì cả.</summary>
    public string? FindDefault()
    {
        var name = DefaultName;
        if (!string.IsNullOrWhiteSpace(name))
        {
            if (Path.IsPathRooted(name) && SafeExists(name)) return name;
            foreach (var folder in Folders)
            {
                var cand = Path.Combine(folder, name);
                if (SafeExists(cand)) return cand;
            }
        }

        (DateTime mtime, string path)? best = null;
        foreach (var folder in Folders)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFiles(folder, "*.rpt"); }
            catch { continue; }
            foreach (var p in entries)
            {
                var low = Path.GetFileName(p).ToLowerInvariant();
                if (!low.EndsWith(".rpt") || Generated.Any(low.EndsWith)) continue;
                DateTime mt;
                try { mt = File.GetLastWriteTimeUtc(p); } catch { continue; }
                if (best is null || mt > best.Value.mtime) best = (mt, p);
            }
        }
        return best?.path;
    }
}
