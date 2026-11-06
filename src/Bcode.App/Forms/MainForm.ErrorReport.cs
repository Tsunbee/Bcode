using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// Log lỗi (Services/ErrorLog + UI/ErrorReporter) phía cửa sổ chính: cung cấp ngữ cảnh "dự án / chức năng đang dùng" cho mỗi dòng error.log
/// (cửa sổ đang hoạt động, tab đang mở + loại control, hoặc hộp thoại đang bật) và báo trên thanh trạng thái khi vừa ghi 1 lỗi kèm phím gửi lỗi.
/// </summary>
public partial class MainForm
{
    private (string Project, string Feature) _errorContext = ("", "");
    private static MainForm? _errorOwner;   // nhiều cửa sổ Bcode: cửa sổ hoạt động gần nhất nhận ngữ cảnh + dòng báo lỗi

    private void InitErrorReport()
    {
        void Own() { _errorOwner = this; ErrorLog.ContextProvider = ErrorContext; ErrorContext(); }
        Own();
        Activated += (_, _) => Own();
        _documentTabs.SelectedIndexChanged += (_, _) => ErrorContext();
        _connections.WorkspaceChanged += () => { if (!IsDisposed && !InvokeRequired) ErrorContext(); };
        ErrorLog.Recorded += OnErrorRecorded;
        Disposed += (_, _) =>
        {
            ErrorLog.Recorded -= OnErrorRecorded;
            if (ReferenceEquals(_errorOwner, this))
            {
                _errorOwner = Application.OpenForms.OfType<MainForm>().FirstOrDefault(f => !ReferenceEquals(f, this) && !f.IsDisposed);
                ErrorLog.ContextProvider = _errorOwner is { } next ? next.ErrorContext : null;
            }
        };
    }

    /// <summary>(dự án, chức năng) lúc này. Gọi từ luồng nền (lỗi trong Task) thì trả bản đã nhớ gần nhất — không chạm control khác luồng.</summary>
    private (string Project, string Feature) ErrorContext()
    {
        if (IsDisposed || !IsHandleCreated || InvokeRequired) return _errorContext;
        try
        {
            var project = _connections.Current?.Name ?? "";
            string feature;
            if (Form.ActiveForm is { } active && !ReferenceEquals(active, this) && active is not MainForm)
                feature = $"Hộp thoại: {active.Text} ({active.GetType().Name})";
            else if (_documentTabs.SelectedTab is { } page)
                feature = FeatureControlName(page) is { } kind ? $"{page.Text.Trim()} ({kind})" : page.Text.Trim();
            else feature = "Cửa sổ chính";
            _errorContext = (project, feature);
        }
        catch { /* giữ ngữ cảnh cũ */ }
        return _errorContext;
    }

    /// <summary>Tên lớp control chức năng của tab (RawSqlControl, FileLookupControl...) — control đầu tiên thuộc Bcode trong vài lớp con đầu.</summary>
    private static string? FeatureControlName(Control root)
    {
        var queue = new Queue<(Control c, int depth)>();
        foreach (Control c in root.Controls) queue.Enqueue((c, 0));
        while (queue.Count > 0)
        {
            var (c, depth) = queue.Dequeue();
            var t = c.GetType();
            if (t.Namespace?.StartsWith("Bcode", StringComparison.Ordinal) == true && t.Name.EndsWith("Control", StringComparison.Ordinal)) return t.Name;
            if (depth < 3) foreach (Control k in c.Controls) queue.Enqueue((k, depth + 1));
        }
        return null;
    }

    private void OnErrorRecorded(ErrorEntry e)
    {
        if (!ReferenceEquals(_errorOwner, this) || IsDisposed || !IsHandleCreated) return;
        var key = Bcode.App.UI.ShortcutRegistry.Display(Bcode.App.UI.ErrorReporter.ShortcutId);
        var msg = e.Message.Replace('\r', ' ').Replace('\n', ' ');
        if (msg.Length > 140) msg = msg[..140] + "…";
        var hint = key.Length > 0 ? $" — bấm {key} để gửi lỗi" : " — gán phím Gửi lỗi ở Template → Phím tắt";
        try { BeginInvoke(new Action(() => { if (!IsDisposed) PushStatus($"⚠ Đã ghi log lỗi: {msg}{hint}"); })); }
        catch { /* cửa sổ đang đóng */ }
    }
}
