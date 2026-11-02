namespace BcodeViewer.App.UI;

/// <summary>
/// Form gốc co giãn theo DPI màn hình (app chạy PerMonitorV2 — xem csproj). Form dựng bằng code: đặt AutoScaleMode trong constructor chỉ co
/// form lúc còn rỗng, control con thêm sau vẫn là px thiết kế (100% DPI) → OnLoad co cả cây 1 lần theo DPI màn đang hiển thị. Sau đó kéo sang
/// màn khác DPI thì WinForms tự co (AutoScaleMode.Dpi). Nội dung WebView2 tự co theo DPI nên không cần gì thêm.
/// </summary>
public class DpiForm : Form
{
    private float _scaled = 1f;   // tỉ lệ WinForms đã co form trước lúc mở (đổi DPI) — lượt co đầu trừ đi phần này
    private bool _loaded;

    public DpiForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        _scaled = 1f;   // hai dòng trên co form lúc còn rỗng — không tính
    }

    /// <summary>true (mặc định): lúc mở co form + control con (px thiết kế) theo DPI. MainForm tắt — nó tự dàn bằng Dock, chỉ cần co khi đổi DPI.</summary>
    protected virtual bool ScaleOnLoad => true;

    /// <summary>Đổi px thiết kế (100% DPI) sang px thật của màn mà form đang nằm — cho kích thước đặt sau khi mở (ItemHeight, SplitterDistance, vẽ tay).</summary>
    public int Px(int designPx) => (int)Math.Round(designPx * DeviceDpi / 96.0);

    protected override void ScaleControl(SizeF factor, BoundsSpecified specified)
    {
        base.ScaleControl(factor, specified);
        if (!_loaded) _scaled *= factor.Width;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var f = DeviceDpi / 96f / _scaled;
        if (ScaleOnLoad && Math.Abs(f - 1f) > 0.001f) Scale(new SizeF(f, f));
        _loaded = true;
    }
}
