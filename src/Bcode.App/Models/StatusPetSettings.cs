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
        ("fish", "🐟 Cá", "bơi lượn sóng, nhả bong bóng, chuột lại gần thì quẫy đuôi bơi mất"),
        ("firefly", "✨ Đom đóm", "bay lững lờ, đuôi sáng nhấp nháy (đẹp nhất với theme tối)"),
    };

    /// <summary>Loài chọn được cho thú cưng trong editor SQL Query (cá / đom đóm chỉ có trên thanh trạng thái).</summary>
    public static readonly string[] EditorKinds = { "bee", "butterfly", "bird", "ladybug", "cat", "snail" };

    /// <summary>Tên mặc định của từng loài (hiện khi rê chuột lên con thú) — đổi được ở cửa sổ cài đặt.</summary>
    public static readonly Dictionary<string, string> DefaultNames = new()
    {
        ["bee"] = "Ong Vàng", ["butterfly"] = "Bướm Xinh", ["bird"] = "Chích Bông", ["ladybug"] = "Bọ Rùa",
        ["cat"] = "Mướp", ["snail"] = "Ốc Sên", ["fish"] = "Cá Vàng", ["firefly"] = "Đom Đóm",
    };

    /// <summary>Nhắc nghỉ sau bao nhiêu phút dùng Bcode liên tục (0 = tắt).</summary>
    public static readonly int[] RestChoices = { 0, 45, 60, 90, 120 };

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

    /// <summary>Phản ứng khi chạy SQL Query: chạy xong thì thả tim, lỗi thì chim bay mất / ốc rụt vỏ, chạy lâu thì mèo ngủ.</summary>
    public bool ReactSql { get; set; } = true;

    /// <summary>Workspace là database THẬT của khách: đang chọn workspace này thì thanh trạng thái có vạch đỏ, thú cưng thỉnh thoảng giơ "!".</summary>
    public List<string> WarnWorkspaces { get; set; } = new();

    /// <summary>Nhắc nghỉ sau N phút dùng Bcode liên tục (0 = tắt) — mèo ra giữa thanh, bấm vào thì mở Giải lao.</summary>
    public int RestMinutes { get; set; }

    /// <summary>Bấm vào con thú: hiện 1 mẹo (phím tắt / cách dùng Bcode). Rê chuột lên con thú: hiện tên + số ngày.</summary>
    public bool Tips { get; set; } = true;

    /// <summary>Trang trí theo mùa (Tết: hoa mai; Trung thu: đèn lồng; Noel: tuyết + mũ) và theo giờ (tối mèo buồn ngủ hơn).</summary>
    public bool Seasonal { get; set; } = true;

    /// <summary>Chọn mùa: (mã, tên hiện ở cửa sổ cài đặt). "auto" = theo ngày (<see cref="StatusPetEnv.SeasonOf"/>), không rơi vào mùa nào thì không trang trí.</summary>
    public static readonly (string Key, string Label)[] SeasonModes =
    {
        ("auto", "Tự động theo ngày"),
        ("tet", "Tết (cây mai, hoa mai)"),
        ("trungthu", "Trung thu (đèn lồng, hoa cúc)"),
        ("noel", "Noel (cây thông, hoa trạng nguyên)"),
    };

    /// <summary>Mùa trang trí: "auto" (mặc định, theo ngày) hoặc cố định "tet" / "trungthu" / "noel" — cây + hoa + hạt rơi / bay theo mùa đó quanh năm.</summary>
    public string SeasonMode { get; set; } = "auto";

    /// <summary>Mùa đang áp: "" khi tắt trang trí theo mùa (hoặc "auto" mà hôm nay không rơi vào mùa nào).</summary>
    public string EffectiveSeason(DateTime now) =>
        !Seasonal ? "" : SeasonMode is "tet" or "trungthu" or "noel" ? SeasonMode : StatusPetEnv.SeasonOf(now);

    /// <summary>Hiệu ứng hoa đào (hồng / trắng) rơi dọc 2 mép trái / phải cửa sổ Bcode — Controls/PetalRain.cs. Độc lập với công tắc thú cưng.</summary>
    public bool PetalRain { get; set; }

    /// <summary>Mật độ hoa đào: 1 = thưa, 2 = vừa, 3 = dày.</summary>
    public int PetalDensity { get; set; } = 2;

    /// <summary>Lúc bật hoa đào rơi: thanh trên + dải công cụ nhuốm hồng đào (UI/PeachTheme.cs).</summary>
    public bool PetalBar { get; set; } = true;

    public static readonly (int Key, string Label)[] PetalDensities = { (1, "Thưa"), (2, "Vừa"), (3, "Dày") };

    /// <summary>Tạm ẩn khi đang chạy một trong các chương trình <see cref="ShareApps"/> (chia sẻ màn hình / Xem chung).</summary>
    public bool HideOnShare { get; set; } = true;

    /// <summary>Tên tiến trình (không đuôi .exe, cách nhau dấu phẩy). Mặc định: Xem chung của Bcode + CptHost (Zoom đang chia sẻ màn hình).</summary>
    public string ShareApps { get; set; } = "BcodeWatch, CptHost";

    /// <summary>Tên thú cưng theo loài (trống = tên mặc định <see cref="DefaultNames"/>).</summary>
    public Dictionary<string, string> Names { get; set; } = new();

    /// <summary>Ngày bắt đầu nuôi (lần đầu bật thú cưng) — để hiện "ở với bạn N ngày".</summary>
    public DateTime? AdoptedOn { get; set; }

    public string NameOf(string kind) =>
        Names.TryGetValue(kind, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : DefaultNames.GetValueOrDefault(kind, kind);

    /// <summary>Workspace <paramref name="name"/> có được đánh dấu là database thật của khách.</summary>
    public bool IsWarnWorkspace(string? name) =>
        !string.IsNullOrWhiteSpace(name) && WarnWorkspaces.Any(w => string.Equals(w.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Danh sách tên tiến trình đã tách (bỏ đuôi .exe, bỏ trống).</summary>
    public IEnumerable<string> ShareAppList() =>
        (ShareApps ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s).Where(s => s.Length > 0);

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
        ReactSql = ReactSql, WarnWorkspaces = new List<string>(WarnWorkspaces ?? new()), RestMinutes = RestMinutes, Tips = Tips, Seasonal = Seasonal,
        HideOnShare = HideOnShare, ShareApps = ShareApps, SeasonMode = SeasonMode, PetalRain = PetalRain, PetalDensity = PetalDensity, PetalBar = PetalBar, Names = new Dictionary<string, string>(Names ?? new()), AdoptedOn = AdoptedOn,
    };

    /// <summary>Kẹp giá trị về khoảng hợp lệ (dữ liệu đọc từ file / trang gửi lên).</summary>
    public StatusPetSettings Normalize()
    {
        Counts ??= new();
        foreach (var k in Counts.Keys.ToList())
            if (!Kinds.Any(x => x.Key == k)) Counts.Remove(k); else Counts[k] = Math.Clamp(Counts[k], 0, MaxPerKind);
        Speed = Math.Clamp(Speed == 0 ? 100 : Speed, 25, 300);
        if (!EditorStyles.Any(x => x.Key == EditorStyle)) EditorStyle = "off";
        if (!EditorKinds.Contains(EditorKind)) EditorKind = "cat";
        EditorScale = Math.Clamp(EditorScale == 0 ? 150 : EditorScale, 100, 250);
        WarnWorkspaces = (WarnWorkspaces ?? new()).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!RestChoices.Contains(RestMinutes)) RestMinutes = RestChoices.Where(v => v <= Math.Max(0, RestMinutes)).DefaultIfEmpty(0).Max();
        ShareApps ??= "";
        if (!SeasonModes.Any(x => x.Key == SeasonMode)) SeasonMode = "auto";
        PetalDensity = Math.Clamp(PetalDensity == 0 ? 2 : PetalDensity, 1, 3);
        Names = (Names ?? new()).Where(kv => Kinds.Any(x => x.Key == kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value.Trim().Length > 24 ? kv.Value.Trim()[..24] : kv.Value.Trim());
        return this;
    }
}

/// <summary>Trạng thái lúc chạy gửi xuống engine thú cưng (không lưu): workspace có phải DB khách, mùa / giờ, Bcode đang được dùng không,
/// đang ẩn vì chia sẻ màn hình, danh sách mẹo. Web: statuspets.js <c>env(...)</c>; App: <c>Controls/StatusPets.SetEnv</c>; BcodeViewer: Host/ViewerPets.cs (file này link sang).</summary>
public sealed class StatusPetEnv
{
    public bool Warn { get; set; }
    /// <summary>"tet" / "trungthu" / "noel" / "" — <see cref="SeasonOf"/>.</summary>
    public string Season { get; set; } = "";
    /// <summary>Sau 18h hoặc trước 6h.</summary>
    public bool Night { get; set; }
    /// <summary>Cửa sổ Bcode đang được dùng (đang active) — không thì engine vẽ thưa lại cho đỡ tốn máy.</summary>
    public bool Active { get; set; } = true;
    /// <summary>Tạm ẩn (đang chia sẻ màn hình / Xem chung).</summary>
    public bool Hidden { get; set; }
    public string[] Tips { get; set; } = Array.Empty<string>();

    public StatusPetEnv Clone() => (StatusPetEnv)MemberwiseClone();

    /// <summary>Mùa trang trí theo ngày: Tết = 23 tháng Chạp → rằm tháng Giêng (âm lịch), Trung thu = 8 → 20 tháng Tám âm lịch,
    /// Noel = 15 → 26/12 dương lịch. Âm lịch tính bằng ChineseLunisolarCalendar (trùng lịch Việt gần như mọi năm).</summary>
    public static string SeasonOf(DateTime d)
    {
        if (d.Month == 12 && d.Day >= 15 && d.Day <= 26) return "noel";
        try
        {
            var cal = new System.Globalization.ChineseLunisolarCalendar();
            var y = cal.GetYear(d);
            var m = cal.GetMonth(d);
            var leap = cal.GetLeapMonth(y);              // năm nhuận: tháng thứ leap là tháng nhuận, các tháng từ đó lùi 1
            var lunarMonth = leap > 0 && m >= leap ? m - 1 : m;
            var isLeapMonth = leap > 0 && m == leap;
            var day = cal.GetDayOfMonth(d);
            var months = cal.GetMonthsInYear(y);
            if (!isLeapMonth && lunarMonth == 1 && day <= 15) return "tet";
            if (m == months && day >= 23) return "tet";  // tháng Chạp (tháng cuối năm âm)
            if (!isLeapMonth && lunarMonth == 8 && day >= 8 && day <= 20) return "trungthu";
        }
        catch { /* ngoài khoảng lịch hỗ trợ */ }
        return "";
    }

    public static bool IsNight(DateTime d) => d.Hour >= 18 || d.Hour < 6;
}
