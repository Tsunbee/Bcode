using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace Bcode.App.Services;

/// <summary>
/// Kiến thức nạp thêm cho Hỏi Fast từ bảng tra (Excel) của nhóm — vd danh sách mã lỗi hoá đơn điện tử (HĐĐT): mỗi dòng = nghiệp vụ, mã số, nội dung, ghi chú /
/// diễn giải cách xử lý. Lưu ở thư mục kiến thức (<c>fastask\notes\*.json</c>, xem <see cref="KnowledgePaths"/>) — KHÔNG đưa vào repo (tài liệu nội bộ);
/// thư mục kiến thức dùng chung thì cả nhóm cùng thấy. Nạp lại cùng tên file = thay bản cũ.
/// </summary>
public static class FastAskNotes
{
    /// <summary>1 mục: chủ đề (tên bảng tra), nhóm (sheet / nghiệp vụ / mô hình), mã số, nội dung, diễn giải / cách xử lý, nguồn.</summary>
    public sealed record Note(string Id, string Topic, string Group, string Code, string Title, string Detail, string Source);

    public static string Dir => Path.Combine(KnowledgePaths.Root, "fastask", "notes");

    private static (string Key, List<Note> Notes)? _cache;

    /// <summary>Dấu thay đổi của thư mục (để kho tìm kiếm dựng lại khi nạp file mới).</summary>
    public static string Stamp
    {
        get
        {
            try
            {
                if (!Directory.Exists(Dir)) return "0";
                var files = new DirectoryInfo(Dir).GetFiles("*.json");
                return files.Length + ":" + (files.Length == 0 ? 0 : files.Max(f => f.LastWriteTimeUtc.Ticks));
            }
            catch { return "0"; }
        }
    }

    public static List<Note> All
    {
        get
        {
            var key = Dir + "|" + Stamp;
            if (_cache is { } c && c.Key == key) return c.Notes;
            var res = new List<Note>();
            try
            {
                if (Directory.Exists(Dir))
                    foreach (var f in Directory.GetFiles(Dir, "*.json"))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(f));
                        var r = doc.RootElement;
                        var topic = r.TryGetProperty("topic", out var t) ? t.GetString() ?? "" : Path.GetFileNameWithoutExtension(f);
                        var source = r.TryGetProperty("source", out var s) ? s.GetString() ?? "" : Path.GetFileName(f);
                        var i = 0;
                        foreach (var n in r.GetProperty("notes").EnumerateArray())
                        {
                            string P(string name) => n.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
                            res.Add(new Note($"{Path.GetFileNameWithoutExtension(f)}#{i++}", topic, P("group"), P("code"), P("title"), P("detail"), source));
                        }
                    }
            }
            catch { /* file hỏng → bỏ qua phần còn lại */ }
            _cache = (key, res);
            return res;
        }
    }

    /// <summary>Đọc bảng tra Excel: mỗi sheet tìm dòng tiêu đề có cột "Mã …" + "Nội dung" / "Mô tả" (thêm "Nghiệp vụ", "Mô hình", "Ghi chú" / "Diễn giải" nếu có);
    /// dòng chỉ có chữ ở 1 ô = nhóm (vd "ĐĂNG KÝ"). Ghi <c>notes\&lt;tên file&gt;.json</c>; trả số mục đã nạp.</summary>
    public static (int Count, string Path) ImportExcel(string xlsxPath, string? topic = null) => ImportExcelCore(xlsxPath, topic);

    /// <summary>Gộp mục trùng (cùng mã + nội dung, vd 1 lỗi có ở cả sheet "Token" lẫn "Tổng hợp"): nhóm nối lại, giữ diễn giải dài nhất.</summary>
    public static List<Note> Merged
    {
        get
        {
            var all = All;
            if (_merged is { } m && ReferenceEquals(m.Src, all)) return m.Notes;
            var res = all.GroupBy(n => (n.Topic, n.Code, T: FastAskService.Fold(n.Title).Trim().TrimEnd('.')))
                .Select(g => g.First() with
                {
                    Group = string.Join(" | ", g.Select(n => n.Group).Distinct()),
                    Detail = g.Select(n => n.Detail).OrderByDescending(d => d.Length).First(),
                }).ToList();
            _merged = (all, res);
            return res;
        }
    }
    private static (List<Note> Src, List<Note> Notes)? _merged;

    private static (int Count, string Path) ImportExcelCore(string xlsxPath, string? topic)
    {
        var notes = new List<object>();
        using var wb = new XLWorkbook(xlsxPath);
        foreach (var ws in wb.Worksheets)
        {
            var used = ws.RangeUsed();
            if (used is null) continue;
            int lastRow = used.LastRow().RowNumber(), lastCol = used.LastColumn().ColumnNumber();
            string Cell(int r, int c) => ws.Cell(r, c).GetFormattedString().Trim();
            int hdr = -1, cCode = -1, cText = -1, cNote = -1, cGroup = -1, cModel = -1;
            for (var r = 1; r <= Math.Min(lastRow, 15) && hdr < 0; r++)
                for (var c = 1; c <= lastCol; c++)
                {
                    var h = FastAskService.Fold(Cell(r, c));
                    if (Regex.IsMatch(h, @"^ma( so| loi)?$")) { cCode = c; hdr = r; }
                }
            if (hdr < 0) continue;
            for (var c = 1; c <= lastCol; c++)
            {
                var h = FastAskService.Fold(Cell(hdr, c));
                if (Regex.IsMatch(h, @"^(noi dung|mo ta)")) { if (cText < 0) cText = c; }
                else if (Regex.IsMatch(h, @"^(ghi chu|dien giai|dien giai|cach xu ly|huong dan)")) { if (cNote < 0 || h.StartsWith("dien")) cNote = c; }
                else if (h.StartsWith("nghiep vu")) cGroup = c;
                else if (h.StartsWith("mo hinh")) cModel = c;
            }
            if (cText < 0) continue;
            var section = "";
            for (var r = hdr + 1; r <= lastRow; r++)
            {
                var vals = Enumerable.Range(1, lastCol).Select(c => Cell(r, c)).ToList();
                var filled = vals.Count(v => v.Length > 0);
                if (filled == 0) continue;
                string V(int c) => c > 0 ? vals[c - 1] : "";
                var code = Regex.Replace(V(cCode), @"\.0$", "");
                var text = V(cText);
                // dòng chỉ có 1 ô = tên nhóm / mô hình (vd "ĐĂNG KÝ", "Mô hình U2 - …")
                if (filled == 1 || (code.Length == 0 && text.Length == 0)) { section = vals.First(v => v.Length > 0); continue; }
                // "1010: NNT chưa đăng ký…" — mã nằm ở đầu nội dung
                if (code.Length == 0 && Regex.Match(text, @"^(\d{3,6})\s*:\s*(.+)$", RegexOptions.Singleline) is { Success: true } m) { code = m.Groups[1].Value; text = m.Groups[2].Value; }
                if (text.Length == 0) continue;
                var group = string.Join(" · ", new[] { ws.Name, V(cModel), V(cGroup).Length > 0 ? V(cGroup) : section }.Where(x => x.Length > 0).Distinct());
                notes.Add(new { group, code, title = Regex.Replace(text, @"^Lỗi\s*\{0\}\s*-\s*", "").Trim(), detail = V(cNote) });
            }
        }
        var name = Path.GetFileNameWithoutExtension(xlsxPath);
        var path = Path.Combine(Dir, string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) + ".json");
        KnowledgePaths.WriteAtomic(path, JsonSerializer.Serialize(new { topic = topic ?? name, source = Path.GetFileName(xlsxPath), imported = DateTime.Now.ToString("yyyy-MM-dd HH:mm"), notes },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        _cache = null;
        return (notes.Count, path);
    }
}
