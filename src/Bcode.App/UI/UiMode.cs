namespace Bcode.App.UI;

public enum UiModeKind { Web, Native }

/// <summary>
/// Chế độ giao diện của phiên đang chạy: <see cref="UiModeKind.Web"/> (như trước — khung + công cụ là trang HTML trong WebView2)
/// hoặc <see cref="UiModeKind.Native"/> ("App/WinForms": khung vẽ bằng control WinForms, KHÔNG khởi động WebView2 cho tới khi một công cụ
/// chưa có bản native cần tới nó). Chọn lúc mở Bcode (<see cref="Forms.UiModeChooserForm"/>) hoặc bằng tham số dòng lệnh <c>--web</c> / <c>--native</c>;
/// lựa chọn lưu ở file cờ riêng (xem <see cref="Models.AppSettings.SavedUiMode"/>), đổi chế độ cần mở lại Bcode.
/// Code chỉ nên hỏi <see cref="IsNative"/> ở ranh giới dựng giao diện — nghiệp vụ (service) dùng chung cho cả hai chế độ.
/// </summary>
public static class UiMode
{
    public static UiModeKind Current { get; private set; } = UiModeKind.Web;
    public static bool IsNative => Current == UiModeKind.Native;
    public static string Arg(UiModeKind k) => k == UiModeKind.Native ? "--native" : "--web";
    public static string Label(UiModeKind k) => k == UiModeKind.Native ? "App (WinForms)" : "Web (WebView2)";

    public static void Set(UiModeKind k) => Current = k;

    /// <summary>Tham số dòng lệnh ép chế độ (bỏ qua hộp chọn); null = không có.</summary>
    public static UiModeKind? FromArgs(string[] args)
    {
        foreach (var a in args)
        {
            if (a.Equals("--native", StringComparison.OrdinalIgnoreCase) || a.Equals("--app", StringComparison.OrdinalIgnoreCase)) return UiModeKind.Native;
            if (a.Equals("--web", StringComparison.OrdinalIgnoreCase)) return UiModeKind.Web;
        }
        return null;
    }

    /// <summary>Quyết định chế độ cho phiên: tham số dòng lệnh → (tắt "hỏi mỗi lần" và đã có lựa chọn) → hỏi người dùng. Hỏi mà đóng hộp = thoát (null).</summary>
    public static UiModeKind? Resolve(string[] args)
    {
        if (FromArgs(args) is { } forced) return forced;
        var saved = Models.AppSettings.SavedUiMode;
        if (saved is { } s && !Models.AppSettings.AskUiMode) return s;
        using var dlg = new Forms.UiModeChooserForm(saved ?? UiModeKind.Web, Models.AppSettings.AskUiMode);
        if (dlg.ShowDialog() != DialogResult.OK) return null;
        Models.AppSettings.SavedUiMode = dlg.Chosen;
        Models.AppSettings.AskUiMode = dlg.AskNextTime;
        return dlg.Chosen;
    }
}
