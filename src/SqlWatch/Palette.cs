namespace SqlWatch;

/// <summary>Bảng màu của SqlWatch. Chạy riêng dùng <see cref="Light"/>; Bcode nhúng thì truyền màu theo theme đang dùng (sáng / tối)
/// qua <see cref="MainForm.ApplyPalette"/>.</summary>
public sealed record Palette(
    Color Page,         // nền form, khoảng giữa các khung
    Color Surface,      // nền thanh trên, khung lưới, khung chi tiết, thanh trạng thái
    Color Header,       // nền tiêu đề cột lưới
    Color Ink,          // chữ chính
    Color Muted,        // nhãn, chữ phụ
    Color Line,         // viền, đường kẻ
    Color Accent,       // nút chính
    Color AccentHover,
    Color OnAccent,     // chữ trên nút chính
    Color Input,        // nền ô nhập (lọc, project)
    Color ReadOnly,     // nền ô chỉ xem (server, database)
    Color Button,       // nền nút phụ
    Color ButtonHover,
    Color Selected,     // dòng đang chọn
    Color AltRow,       // dòng xen kẽ
    Color Rpc,          // chữ dòng RPC:Starting
    Color Sp)           // chữ dòng SP:Starting
{
    public static Palette Light { get; } = new(
        Page: Color.FromArgb(244, 246, 248),
        Surface: Color.White,
        Header: Color.FromArgb(247, 248, 250),
        Ink: Color.FromArgb(31, 41, 51),
        Muted: Color.FromArgb(96, 110, 124),
        Line: Color.FromArgb(226, 230, 235),
        Accent: Color.FromArgb(14, 122, 114),
        AccentHover: Color.FromArgb(11, 104, 97),
        OnAccent: Color.White,
        Input: Color.White,
        ReadOnly: Color.FromArgb(247, 248, 250),
        Button: Color.White,
        ButtonHover: Color.FromArgb(240, 243, 246),
        Selected: Color.FromArgb(226, 240, 238),
        AltRow: Color.FromArgb(250, 251, 252),
        Rpc: Color.FromArgb(30, 78, 140),
        Sp: Color.FromArgb(14, 110, 86));
}
