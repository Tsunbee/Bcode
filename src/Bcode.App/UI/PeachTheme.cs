namespace Bcode.App.UI;

/// <summary>
/// "Thanh trên màu hoa đào": khi bật hiệu ứng hoa đào rơi (StatusPetSettings.PetalRain + PetalBar), thanh trên của Bcode (NativeTopBar / topbar.html)
/// và dải công cụ ngay dưới (MainForm._toolsBar) nhuốm hồng đào. Chỉ những control đánh dấu là thanh (<see cref="MarkBar"/> hoặc <see cref="IBar"/>) và
/// con cháu của chúng mới đổi màu — BarFlow / PillButton dùng ở chỗ khác giữ nguyên. Màu = màu gốc của theme pha hồng (giữ độ sáng tối của theme).
/// </summary>
public static class PeachTheme
{
    /// <summary>Control là "thanh" nhuốm hồng khi bật (NativeTopBar).</summary>
    public interface IBar { }

    private static readonly HashSet<Control> Marked = new();

    public static bool On { get; private set; }

    /// <summary>Bật / tắt đổi — thanh vẽ lại (MainForm cũng đẩy window.setPeach xuống topbar.html).</summary>
    public static event Action? Changed;

    public static void Set(bool on)
    {
        if (On == on) return;
        On = on;
        Changed?.Invoke();
    }

    /// <summary>Đánh dấu thêm 1 control là thanh (ToolStrip dải công cụ của MainForm).</summary>
    public static void MarkBar(Control c)
    {
        Marked.Add(c);
        c.Disposed += (_, _) => Marked.Remove(c);
    }

    public static bool InBar(Control? c)
    {
        for (; c is not null; c = c.Parent) if (c is IBar || Marked.Contains(c)) return true;
        return false;
    }

    /// <summary>Đang bật và <paramref name="c"/> nằm trong thanh.</summary>
    public static bool Applies(Control? c) => On && InBar(c);

    // Hồng đào: theme tối pha hồng đậm, theme sáng pha hồng phấn.
    private static Color Pink => AppColors.IsDark ? Color.FromArgb(0xc2, 0x3b, 0x72) : Color.FromArgb(0xff, 0x9e, 0xbe);

    public static Color Mix(Color a, Color b, float k) =>
        Color.FromArgb(a.A, (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    /// <summary>Nền thanh.</summary>
    public static Color Bar => Mix(AppColors.PanelAlt, Pink, AppColors.IsDark ? 0.24f : 0.30f);
    /// <summary>Nền nút thường trên thanh.</summary>
    public static Color Button => Mix(AppColors.ButtonBack, Pink, AppColors.IsDark ? 0.26f : 0.32f);
    /// <summary>Nền nút khi rê chuột.</summary>
    public static Color Hover => Mix(AppColors.PanelAlt, Pink, AppColors.IsDark ? 0.42f : 0.50f);
    /// <summary>Viền / vạch ngăn.</summary>
    public static Color Line => Mix(AppColors.Border, Pink, 0.55f);
    /// <summary>Nút chính (Quick Access, App / Sys đang chọn) — hồng đào đậm thay xanh.</summary>
    public static Color Accent => AppColors.IsDark ? Color.FromArgb(0xe0, 0x5a, 0x8e) : Color.FromArgb(0xe0, 0x4f, 0x86);
    public static Color AccentHover => AppColors.IsDark ? Color.FromArgb(0xec, 0x74, 0xa2) : Color.FromArgb(0xc9, 0x3f, 0x74);

    /// <summary>Nền cho control: trong thanh và đang bật → <see cref="Bar"/>, không thì <paramref name="normal"/>.</summary>
    public static Color BarOr(Control? c, Color normal) => Applies(c) ? Bar : normal;
}
