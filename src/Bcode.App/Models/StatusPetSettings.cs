namespace Bcode.App.Models;

/// <summary>Thú cưng chạy / bay trên thanh trạng thái (trang trí) — Settings → "Thú cưng (thanh trạng thái, SQL Query)...".
/// Web: Web/Shell/statusbar.html (window.setPets); native: Controls/StatusPets.cs. Mặc định tắt.
/// Cùng lớp này giữ thú cưng trong editor SQL Query (EditorStyle / EditorKind / EditorScale) — Web: statuspets.js StatusPets.editor
/// (sqleditor.html window.setEditorPet); App: Controls/StatusPets.Editor.cs (EditorPets). Không phụ thuộc <see cref="Enabled"/>.</summary>
public sealed class StatusPetSettings
{
    /// <summary>Các con vật có sẵn: (mã, tên hiện ở cửa sổ cài đặt, mô tả ngắn).</summary>
    public static readonly (string Key, string Label, string Hint)[] Kinds =
    {
        ("bee", "🐝 Ong", "bay lượn, nhào lộn, hút mật hoa, bay theo chuột thả tim"),
        ("butterfly", "🦋 Bướm", "chập chờn, đậu hoa, màu ngẫu nhiên"),
        ("bird", "🐦 Chim", "bay vụt qua, đáp xuống nhảy lò cò mổ thóc, chuột lại gần thì bay mất"),
        ("ladybug", "🐞 Bọ rùa", "bò dưới đáy, thỉnh thoảng xoè cánh bay một quãng"),
        ("cat", "🐱 Mèo", "đi dạo, ngồi vẫy đuôi, ngủ khò khò, vồ chuột, rình chim"),
        ("snail", "🐌 Ốc sên", "bò chậm để lại vệt nhớt, chuột lại gần thì rụt vào vỏ"),
    };

    public const int MaxPerKind = 5;

    public bool Enabled { get; set; }

    /// <summary>Số con mỗi loại (mã → 0..<see cref="MaxPerKind"/>).</summary>
    public Dictionary<string, int> Counts { get; set; } = new() { ["bee"] = 1 };

    /// <summary>Tốc độ chung, phần trăm (25..300).</summary>
    public int Speed { get; set; } = 100;

    /// <summary>Thú cưng phản ứng với con trỏ chuột trên thanh trạng thái (bay theo / bỏ chạy / vồ / rụt vào vỏ).</summary>
    public bool Mouse { get; set; } = true;

    /// <summary>Thỉnh thoảng mọc hoa cho ong / bướm hút mật.</summary>
    public bool Flowers { get; set; } = true;

    /// <summary>Vệt phấn hoa / vệt nhớt.</summary>
    public bool Trail { get; set; } = true;

    /// <summary>Kiểu hiện trong editor SQL Query: (mã, tên hiện ở cửa sổ cài đặt).</summary>
    public static readonly (string Key, string Label)[] EditorStyles =
    {
        ("off", "Tắt"),
        ("follow", "Chạy theo chuột (giữ chữ I)"),
        ("cursor", "Thay hẳn con trỏ chuột"),
    };

    /// <summary>"off" (mặc định) · "follow" = 1 con chạy theo chuột, đứng cạnh chữ I · "cursor" = con trỏ chuột trong vùng chữ thành hình con thú.</summary>
    public string EditorStyle { get; set; } = "off";

    /// <summary>Loài hiện trong editor SQL Query (mã trong <see cref="Kinds"/>).</summary>
    public string EditorKind { get; set; } = "cat";

    /// <summary>Cỡ con thú trong editor, phần trăm hình gốc (100..250); kiểu "cursor" tối đa 32 px.</summary>
    public int EditorScale { get; set; } = 150;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool EditorOn => EditorStyle is "follow" or "cursor";

    public int CountOf(string kind) => Counts.TryGetValue(kind, out var n) ? Math.Clamp(n, 0, MaxPerKind) : 0;

    public StatusPetSettings Clone() => new()
    {
        Enabled = Enabled, Counts = new Dictionary<string, int>(Counts), Speed = Speed, Mouse = Mouse, Flowers = Flowers, Trail = Trail,
        EditorStyle = EditorStyle, EditorKind = EditorKind, EditorScale = EditorScale,
    };

    /// <summary>Kẹp giá trị về khoảng hợp lệ (dữ liệu đọc từ file / trang gửi lên).</summary>
    public StatusPetSettings Normalize()
    {
        Counts ??= new();
        foreach (var k in Counts.Keys.ToList())
            if (!Kinds.Any(x => x.Key == k)) Counts.Remove(k); else Counts[k] = Math.Clamp(Counts[k], 0, MaxPerKind);
        Speed = Math.Clamp(Speed == 0 ? 100 : Speed, 25, 300);
        if (!EditorStyles.Any(x => x.Key == EditorStyle)) EditorStyle = "off";
        if (!Kinds.Any(x => x.Key == EditorKind)) EditorKind = "cat";
        EditorScale = Math.Clamp(EditorScale == 0 ? 150 : EditorScale, 100, 250);
        return this;
    }
}
