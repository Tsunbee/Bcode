using System.Text.Json;

namespace Bcode.App.Services.Rpt.Builder;

/// <summary>
/// "Cây quan hệ đã học": danh sách quan hệ nối (khoá ngoại) giữa các bảng FBO ĐÃ THẤY TẬN MẮT trong procedure của các báo cáo từng phân tích
/// (<c>Data\relations.json</c>, nhúng trong DLL — xem comment trong file đó). <see cref="ExistingReportService"/> dùng để suy ra điều kiện nối khi
/// không tìm thấy mệnh đề ON rõ ràng trong văn bản procedure (thường vì nằm trong SQL động, hoặc alias bị dùng lại cho nhiều bảng khác nhau nên
/// không khớp được bằng regex) — THAY cho việc đoán mò theo "cột đầu tiên của bảng" hoặc "stt_rec", vốn hay sai. Càng phân tích nhiều báo cáo,
/// danh sách này càng đầy đủ và các báo cáo sau càng ít phải tự chọn cột nối hơn.
/// </summary>
public sealed class KnownRelations
{
    private readonly List<(string A, string Ca, string B, string Cb)> _rel = new();
    private static KnownRelations? _instance;
    public static KnownRelations Instance => _instance ??= Load();

    public static KnownRelations Load(string? path = null)
    {
        var k = new KnownRelations();
        try
        {
            using var stream = path is null ? typeof(KnownRelations).Assembly.GetManifestResourceStream("relations.json") : (File.Exists(path) ? File.OpenRead(path) : null);
            if (stream is null) return k;
            using var doc = JsonDocument.Parse(stream);
            foreach (var e in doc.RootElement.GetProperty("relations").EnumerateArray())
            {
                string S(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var (a, ca, b, cb) = (S("a"), S("ca"), S("b"), S("cb"));
                if (a.Length > 0 && ca.Length > 0 && b.Length > 0 && cb.Length > 0) k._rel.Add((a, ca, b, cb));
            }
        }
        catch { /* thiếu / hỏng file: vẫn chạy, chỉ là không có gợi ý — rơi về cách đoán cũ */ }
        return k;
    }

    /// <summary>Quan hệ đã biết giữa hai bảng (không phân biệt chiều ai hỏi trước). Trả về (cột của "from", cột của "to") nếu có.</summary>
    public (string FromCol, string ToCol)? TryGet(string fromTable, string toTable)
    {
        foreach (var r in _rel)
        {
            if (r.A.Equals(fromTable, StringComparison.OrdinalIgnoreCase) && r.B.Equals(toTable, StringComparison.OrdinalIgnoreCase)) return (r.Ca, r.Cb);
            if (r.B.Equals(fromTable, StringComparison.OrdinalIgnoreCase) && r.A.Equals(toTable, StringComparison.OrdinalIgnoreCase)) return (r.Cb, r.Ca);
        }
        return null;
    }
}
