namespace Bcode.App.Services;

/// <summary>Đoán kiểu SQL theo tên cột theo quy ước đặt tên field của Fast (ma_ / tien / ngay / _nt ...),
/// dùng cho hộp "Add New Column" (ColumnDesignForm – Web, ColumnDesignPage – App). Nguồn luật DUY NHẤT:
/// Web nhận bảng luật này qua init (serialize), App gọi thẳng <see cref="Guess"/>.
/// Khớp với kiểu Bcode đang sinh ở nơi khác (xem FastGhostRules.TypeOf, FastCatalogRules.GuessType).</summary>
public static class ColumnTypeGuess
{
    /// <summary>Một luật: tên cột (đã lower) khớp nếu CHỨA 1 trong <paramref name="Contains"/>, HOẶC bắt đầu bằng 1 trong
    /// <paramref name="Prefix"/>, HOẶC kết thúc bằng 1 trong <paramref name="Suffix"/> → đặt kiểu <paramref name="Type"/> và độ dài <paramref name="Length"/>.</summary>
    public sealed record Rule(string[] Contains, string[] Prefix, string[] Suffix, string Type, string Length);

    /// <summary>Bảng luật — SỬA/THÊM tại đây. Thứ tự = ưu tiên: luật khớp ĐẦU TIÊN thắng.
    /// numeric đặt trước nên tên khớp nhiều luật (vd "t_tien_nt") ra numeric(19,4).</summary>
    public static readonly Rule[] Rules =
    {
        // tiền / số lượng / thuế / tỷ giá ... → numeric(19,4)
        new(Contains: new[] { "tien", "so_luong", "sl", "thue" }, Prefix: new[] { "t_" }, Suffix: new[] { "_nt" }, Type: "numeric", Length: "19,4"),
        // ngày tháng → smalldatetime
        new(Contains: new[] { "ngay" }, Prefix: System.Array.Empty<string>(), Suffix: System.Array.Empty<string>(), Type: "smalldatetime", Length: ""),
        // mã / nhóm / tài khoản / loại ... → varchar(32)
        new(Contains: new[] { "ma_" }, Prefix: new[] { "nh_", "loai_" }, Suffix: System.Array.Empty<string>(), Type: "varchar", Length: "32"),
    };

    /// <summary>Kiểu hay dùng — tô nổi bật & đẩy lên đầu danh sách gợi ý.</summary>
    public static readonly string[] Favorites = { "nvarchar", "varchar", "char", "numeric", "smalldatetime" };

    /// <summary>Đoán (kiểu, độ dài) theo tên cột; null nếu không luật nào khớp.</summary>
    public static (string Type, string Length)? Guess(string? name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        if (n.Length == 0) return null;
        foreach (var r in Rules)
            if (System.Array.Exists(r.Contains, n.Contains)
                || System.Array.Exists(r.Prefix, n.StartsWith)
                || System.Array.Exists(r.Suffix, s => n.EndsWith(s)))
                return (r.Type, r.Length);
        return null;
    }
}
