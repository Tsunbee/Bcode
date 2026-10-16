namespace Bcode.App.Services.Rpt;

/// <summary>1 quy tắc đoán pivot (khi không đọc được &lt;pivot&gt; của Grid controller). Mỗi ô là danh sách tên cột thay thế nhau (lấy tên đầu tiên có trong bảng);
/// <see cref="RowKeys"/> rỗng = khoá dòng "*" (tự đoán: cột chữ đầu tiên không phải cột hệ thống/khoá/giá trị — chỉ áp khi có bảng định nghĩa id, name dạng field$N).
/// <see cref="Headers"/> / <see cref="DataFields"/> rỗng = cách đoán mặc định.</summary>
public record PivotRule(List<string> RowKeys, List<string> ColKeys, List<string> Headers, List<string> DataFields, string Text)
{
    public bool AutoRow => RowKeys.Count == 0;
}

/// <summary>Quy tắc đoán cấu trúc pivot cho tab Pivot của SQL Query — file mẫu <c>%AppData%\Bcode\pivot-rules.txt</c> (Settings → "Pivot SQL Query: quy tắc đoán cấu trúc...").
/// Chưa có file = dùng <see cref="DefaultText"/>. Sửa file là áp dụng ở lần chạy sau, không cần build/khởi động lại.</summary>
public static class PivotRules
{
    public const string FileName = "pivot-rules.txt";
    public static string FilePath => Path.Combine(BcodePaths.AppData, FileName);

    public const string DefaultText =
        """
        # Quy tắc đoán cấu trúc pivot — tab "Pivot" của SQL Query (Bcode).
        # Chỉ dùng khi KHÔNG đọc được <pivot rowField columnField dataFields> trong Grid controller của báo cáo
        # (controller = tên procedure trong lệnh exec cuối, bỏ tiền tố rs_; tìm Grid\<tên>.xml hoặc .f trong Source Path).
        #
        # Mỗi dòng 1 quy tắc, các ô cách nhau bằng dấu | (ô sau để trống được), xét từ trên xuống, gặp quy tắc khớp đầu tiên thì dùng:
        #   khoá dòng | khoá cột | cột tiêu đề cột | field giá trị
        # - Trong 1 ô có thể ghi nhiều tên cách nhau dấu phẩy: lấy tên đầu tiên có trong bảng kết quả. Không phân biệt hoa/thường.
        # - khoá dòng = * : tự đoán = cột chữ đầu tiên không phải cột hệ thống (sys..., stt, line_nbr...), khoá cột, tiêu đề hay giá trị.
        #   Quy tắc * chỉ áp khi kết quả có thêm bảng định nghĩa cột kiểu Fast (cột id, name; name dạng so_luong$1) để tránh nhận nhầm,
        #   và được xét SAU mọi quy tắc ghi tên khoá dòng cụ thể.
        # - cột tiêu đề cột trống = thử <khoá cột>Name, xHeader, sysColumnName, xColumnName, header.
        # - field giá trị trống = lấy theo bảng định nghĩa (name dạng field$N), không có thì cột số đầu tiên còn lại.
        # Dòng bắt đầu bằng # là ghi chú. Xoá file này = quay về mẫu mặc định.

        xRow   | xColumn
        sysRow | sysColumn
        KeyRow | KeyCol
        *      | sysColumn
        *      | xColumn
        """;

    private static (DateTime Stamp, List<PivotRule> Rules)? _cache;

    /// <summary>Quy tắc hiện hành (đọc lại file khi file đổi). Lỗi đọc = mẫu mặc định.</summary>
    public static List<PivotRule> Load()
    {
        try
        {
            var path = FilePath;
            if (File.Exists(path))
            {
                var stamp = File.GetLastWriteTimeUtc(path);
                if (_cache is { } c && c.Stamp == stamp) return c.Rules;
                var rules = Parse(File.ReadAllText(path));
                _cache = (stamp, rules);
                return rules;
            }
        }
        catch { /* không đọc được file: dùng mẫu mặc định */ }
        return Parse(DefaultText);
    }

    /// <summary>Tạo file mẫu (nếu chưa có) rồi trả đường dẫn — cho mục Settings mở ra sửa.</summary>
    public static string EnsureFile()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DefaultText.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n") + "\r\n", new System.Text.UTF8Encoding(true));
        }
        return path;
    }

    public static List<PivotRule> Parse(string text)
    {
        var list = new List<PivotRule>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var cells = line.Split('|').Select(Names).ToList();
            while (cells.Count < 4) cells.Add(new());
            var cols = cells[1];
            if (cols.Count == 0) continue;   // thiếu khoá cột: bỏ dòng
            var rows = cells[0].Where(n => n != "*").ToList();
            list.Add(new PivotRule(rows, cols, cells[2], cells[3], line));
        }
        return list;
    }

    private static List<string> Names(string cell) =>
        cell.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
}
