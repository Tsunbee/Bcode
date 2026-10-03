namespace Bcode.App.UI;

/// <summary>Form gốc bật co giãn theo DPI màn hình: ClientSize/Size/Location và mọi control con viết cứng bằng px
/// (thiết kế ở 96 DPI) được nhân theo tỉ lệ DPI của màn hình đang hiển thị — cùng tỉ lệ mà WebView2 tự áp cho nội dung web.</summary>
public class DpiForm : Form
{
    public DpiForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
    }

    private float _layoutK = 1f;

    /// <summary>true (mặc định): cỡ form + control con nhân thêm hệ số UiScale — để khớp với font đã nhân UiScale (ThemeManager.BaseFont)
    /// và WebView2 (ZoomFactor). MainForm tắt vì nó tự dàn bằng Dock.</summary>
    protected virtual bool ScaleLayoutWithUiScale => true;

    /// <summary>Co giãn bố cục theo UiScale rồi không để cửa sổ/hộp thoại to hơn hoặc nằm ngoài vùng làm việc của màn hình đang hiển thị.</summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (ScaleLayoutWithUiScale)
        {
            ApplyLayoutScale();
            UiScale.Changed += ApplyLayoutScale;
            Disposed += (_, _) => UiScale.Changed -= ApplyLayoutScale;
        }
        if (WindowState != FormWindowState.Normal) return;
        var wa = Screen.FromControl(this).WorkingArea;
        var w = Math.Min(Width, wa.Width);
        var h = Math.Min(Height, wa.Height);
        if (w != Width || h != Height) Size = new Size(w, h);
        var x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
        var y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
        if (x != Left || y != Top) Location = new Point(x, y);
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
