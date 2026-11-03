namespace SqlWatch;

/// <summary>Phần nối để Bcode nhúng form SqlWatch vào tab (Bcode.App/Controls/SqlWatchControl.cs) — chỉ biên dịch trong Bcode,
/// project SqlWatch độc lập không có file này.</summary>
public sealed partial class MainForm
{
    private bool _hostStarting;

    /// <summary>Đang bắt (đã bấm Bắt đầu, chưa Dừng).</summary>
    public bool IsCapturing => _watcher is not null;

    /// <summary>Chọn project theo tên workspace Bcode — chỉ khi chưa bắt (đang bắt thì ô Project bị khoá như bấm tay).</summary>
    public void SelectProject(string name)
    {
        if (_watcher is not null || !_projects.TryGetValue(name, out var workspace)) return;
        _project.SelectedItem = workspace.Name;
        ApplyProject(showError: false);
        SetIdleStatus();
    }

    /// <summary>Như bấm "Bắt đầu" (bỏ qua nếu đang mở kết nối dở).</summary>
    public async Task StartCaptureAsync()
    {
        if (_hostStarting) return;
        _hostStarting = true;
        try { await StartAsync(); }
        finally { _hostStarting = false; }
    }

    /// <summary>Như bấm "Dừng" — đóng tab thì đóng kết nối.</summary>
    public void StopCapture()
    {
        CloseWatcher();
        if (!IsDisposed) SetRunning(false);
    }
}
