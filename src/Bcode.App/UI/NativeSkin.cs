namespace Bcode.App.UI;

/// <summary>
/// "Skin" của chế độ App/WinForms: WinForms không có HTML/CSS nên các control tự vẽ (PillButton, RoundedInput, DropDownButton, IconRailControl, các thanh)
/// đọc cùng bộ "biến thiết kế" mà trang web đọc qua CSS (--r, --bw, --dens) — tức <see cref="UiTemplate"/>: màu theo theme, cỡ chữ/font theo Template,
/// <see cref="UiTemplate.CornerRadius"/> (bo góc), <see cref="UiTemplate.BorderWidth"/> (viền), <see cref="UiTemplate.Density"/> (mật độ).
/// Chỉnh ở "Giao diện (Template)" là cả hai chế độ đổi theo (control native vẽ lại ngay khi Template đổi). Mọi số đo đổi theo DPI màn hình nên tự scale.
/// Thang bo góc: nút = bán kính template + 2, ô nhập/menu = +4, thẻ/khung = +6 (mặc định 6 → 8 / 10 / 12 px: mềm, không còn góc nhọn).
/// </summary>
public static class NativeSkin
{
    private static UiTemplate T => UiTemplate.Current;
    // DPI màn hình × hệ số co giãn theo cỡ màn hình (UiScale.Factor, cùng hệ số đã áp vào cỡ chữ BaseFont): trước đây chỉ nhân DPI nên trên màn 1920×1080
    // (Factor ≈ 1,15) chữ to thêm 15% mà hàng / nút / ô giữ nguyên → chật, chữ bị cắt, nút xuống dòng. Nay chữ và khung co giãn cùng một tỉ lệ.
    /// <summary>Như <see cref="S"/> nhưng theo DPI của màn mà <paramref name="c"/> đang nằm — dùng khi đặt kích thước lúc control đã hiện (áp theme, vẽ tay).</summary>
    public static int Px(Control? c, int v) => (int)Math.Round(Math.Max(0, v) * DpiOf(c) / 96.0 * UiScale.Factor);

    /// <summary><see cref="Px"/> của 1 khoảng theo Mật độ Template (<see cref="UiTemplate.Dens"/>): lề / khoảng cách / chiều cao hàng.</summary>
    public static int Dens(Control? c, int designPx) => Px(c, UiTemplate.Dens(designPx));

    // Control đã có handle: DPI thật của màn nó nằm. Chưa có (đang dựng): DeviceDpi vẫn là DPI hệ thống → lấy DPI màn của cửa sổ chính.
    private static int DpiOf(Control? c) => c is { IsHandleCreated: true } ? c.DeviceDpi : UiScale.Dpi;

    /// <summary>Đổi 1 kích thước thiết kế (px ở 100% DPI, màn 1600×900) sang px thật: × DPI màn cửa sổ chính × hệ số màn hình. Dùng cho mọi bề rộng / chiều cao cố định của control native.</summary>
    public static int S(int designPx) => (int)Math.Round(designPx * UiScale.Dpi / 96.0 * UiScale.Factor);

    public static int ButtonRadius(Control c) => Px(c, T.CornerRadius + 2);
    public static int InputRadius(Control c) => Px(c, T.CornerRadius + 4);
    public static int CardRadius(Control c) => Px(c, T.CornerRadius + 6);
    public static float BorderWidth(Control c) => Math.Max(1f, T.BorderWidth * c.DeviceDpi / 96f);
    /// <summary>Bộ kiểu dựng sẵn (bo góc + viền + mật độ) — chọn nhanh ở Settings; tinh chỉnh từng số ở "Giao diện (Template)".</summary>
    public static readonly (string Id, string Label, int Radius, int Border, string Density)[] Presets =
    {
        ("soft", "Mềm (khuyên dùng)", 8, 1, "normal"),
        ("compact", "Gọn", 5, 1, "compact"),
        ("airy", "Thoáng", 11, 1, "comfortable"),
        ("sharp", "Vuông vức", 2, 1, "normal"),
    };

    public static bool IsPreset(string id)
    {
        var p = Presets.First(x => x.Id == id);
        return T.CornerRadius == p.Radius && T.BorderWidth == p.Border && T.Density == p.Density;
    }

    /// <summary>Áp 1 bộ kiểu: ghi vào Template (lưu ui-template.json) → mọi control native + trang web vẽ lại.</summary>
    public static void ApplyPreset(string id)
    {
        var p = Presets.First(x => x.Id == id);
        var t = UiTemplate.Current.Clone();
        t.CornerRadius = p.Radius; t.BorderWidth = p.Border; t.Density = p.Density;
        try { t.Save(); } catch { /* chỉ áp cho phiên này */ }
        UiTemplate.Current = t;
    }

    /// <summary>Chiều cao 1 hàng nút/ô theo Mật độ + DPI.</summary>
    public static int RowHeight(Control? c, int basePx = 26) => Px(c, UiTemplate.Dens(basePx));
}
