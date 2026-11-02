namespace Bcode.App.UI;

/// <summary>Cách <see cref="DpiForm"/> co giãn form lúc mở (lần đầu) theo DPI màn hình × hệ số <see cref="UiScale.Factor"/>.</summary>
public enum DpiLayout
{
    /// <summary>Cỡ form và control con viết bằng px thiết kế (100% DPI, UiScale 1): co cả cây theo DPI × UiScale.</summary>
    Design,
    /// <summary>Cỡ form và control con đã tính bằng NativeSkin.S / RowHeight (đã gồm DPI × UiScale): không co thêm.</summary>
    PreScaled,
    /// <summary>Chỉ cỡ cửa sổ (Width / Height / MinimumSize) là px thiết kế; nội dung lấp đầy form tự co (trang WebView2, trang native dựng bằng NativeSkin).</summary>
    WindowOnly,
}

/// <summary>Form gốc co giãn theo DPI màn hình đang hiển thị × <see cref="UiScale.Factor"/> — cùng tỉ lệ mà WebView2 áp cho nội dung web
/// (DPI tự có + ZoomFactor) và cỡ chữ (ThemeManager.BaseFont pt × UiScale). Lúc mở co 1 lần theo <see cref="DpiLayoutMode"/>; sau đó
/// đổi UiScale thì co theo tỉ lệ mới, kéo sang màn khác DPI thì WinForms tự co (AutoScaleMode.Dpi).</summary>
public class DpiForm : Form
{
    public DpiForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        // Hai dòng trên chỉ co form lúc còn rỗng (form dựng bằng code, không bọc Suspend/ResumeLayout như Designer) → control con thêm sau
        // vẫn là px thiết kế, KHÔNG được WinForms nhân DPI. Việc nhân DPI làm ở OnLoad; AutoScaleMode.Dpi vẫn cần cho lúc đổi DPI (WM_DPICHANGED).
        _scaled = 1f;
    }

    private float _layoutK = 1f;
    private float _scaled = 1f;   // tỉ lệ form đã bị co trước lúc mở (WinForms co khi đổi DPI) — lượt co đầu trừ đi phần này
    private bool _loaded;

    [ThreadStatic] private static float t_firstPassDpi;

    /// <summary>Đang co lần đầu một form <see cref="DpiLayout.Design"/>.</summary>
    internal static bool InFirstPass => t_firstPassDpi != 0f;

    /// <summary>Phần DPI của hệ số đang co lần đầu (1 khi không trong lượt đó) — control đã tự đặt kích thước theo DPI (WebBarHost) chia bớt phần này.</summary>
    internal static float FirstPassDpi => InFirstPass ? t_firstPassDpi : 1f;

    /// <summary>true (mặc định): cỡ form + control con theo DPI × UiScale (xem <see cref="DpiLayoutMode"/>), đổi UiScale thì co theo.
    /// MainForm tắt vì nó tự dàn bằng Dock.</summary>
    protected virtual bool ScaleLayoutWithUiScale => true;

    /// <summary>Kích thước trong form được viết theo kiểu nào — mặc định px thiết kế. Hộp thoại dựng bằng NK.* / NativeSkin.S khai
    /// <see cref="DpiLayout.PreScaled"/>, form chỉ chứa 1 trang lấp đầy khai <see cref="DpiLayout.WindowOnly"/>.</summary>
    protected virtual DpiLayout DpiLayoutMode => DpiLayout.Design;

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        if (!_loaded) _scaled *= factor.Width;
    }

    /// <summary>Co giãn bố cục theo DPI × UiScale rồi không để cửa sổ/hộp thoại to hơn hoặc nằm ngoài vùng làm việc của màn hình đang hiển thị.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (ScaleLayoutWithUiScale)
        {
            var k = (float)UiScale.Factor;
            var f = DeviceDpi / 96f * k / _scaled;
            switch (DpiLayoutMode)
            {
                case DpiLayout.Design: ScaleFirst(f, f / k); break;
                case DpiLayout.WindowOnly: ScaleWindow(f); break;
            }
            _layoutK = k;
            UiScale.Changed += ApplyLayoutScale;
            Disposed += (_, _) => UiScale.Changed -= ApplyLayoutScale;
        }
        _loaded = true;
        if (WindowState != FormWindowState.Normal) return;
        var wa = Screen.FromControl(this).WorkingArea;
        var w = Math.Min(Width, wa.Width);
        var h = Math.Min(Height, wa.Height);
        if (w != Width || h != Height) Size = new Size(w, h);
        var x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
        var y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
        if (x != Left || y != Top) Location = new Point(x, y);
    }

    private void ScaleFirst(float f, float dpiPart)
    {
        if (Math.Abs(f - 1f) < 0.001f) return;
        t_firstPassDpi = dpiPart;
        try { Scale(new SizeF(f, f)); }
        finally { t_firstPassDpi = 0f; }
    }

    private void ScaleWindow(float f)
    {
        if (Math.Abs(f - 1f) < 0.001f) return;
        Size Mul(Size s) => new((int)Math.Round(s.Width * f), (int)Math.Round(s.Height * f));
        if (!MinimumSize.IsEmpty) MinimumSize = Mul(MinimumSize);
        ClientSize = Mul(ClientSize);
    }

    private void ApplyLayoutScale()
    {
        var k = (float)UiScale.Factor;
        if (IsDisposed || Math.Abs(k - _layoutK) < 0.001f) return;
        var ratio = k / _layoutK;
        _layoutK = k;
        Scale(new SizeF(ratio, ratio));
    }
}

internal static class DpiScale
{
    /// <summary>Đổi số px thiết kế ở 96 DPI sang px thật của màn hình mà <paramref name="c"/> đang nằm.</summary>
    public static int Px(Control c, int px) => (int)Math.Round(px * (c.DeviceDpi / 96.0));
}
