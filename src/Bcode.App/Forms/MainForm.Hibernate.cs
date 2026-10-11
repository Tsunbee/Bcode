using Bcode.App.Controls;

namespace Bcode.App.Forms;

/// <summary>
/// "Ngủ đông" tab SQL Query (Chế độ hiệu năng): tab không được xem quá N phút thì chụp trạng thái (<see cref="RawSqlControl.TryCaptureSnapshotAsync"/>),
/// giải phóng control + các WebView2 của nó và để lại tab "chờ" (<see cref="LazySql"/> kèm Snapshot) — đúng cơ chế tab chờ của khôi phục phiên,
/// nên đóng tab / lưu phiên / mở lại tab đã đóng đều chạy như cũ. Bấm vào tab thì <see cref="MaterializeLazy"/> dựng lại y như trước, chỉ mất lịch sử Undo.
/// Đo 2026-10-10: tạm ngưng (TrySuspend) / hạ MemoryUsageTargetLevel tab ẩn KHÔNG bớt RAM đáng kể (226 → 223MB) nên không dùng.
/// Tab đang bận / debug / còn kết nối giữ transaction / có breakpoint / mở khung AI thì bỏ qua.
/// </summary>
public partial class MainForm
{
    private readonly System.Windows.Forms.Timer _hibernateTimer = new() { Interval = 60_000 };
    private readonly Dictionary<TabPage, long> _tabLastActive = new();
    private bool _hibernating;

    private void InitHibernation()
    {
        _hibernateTimer.Tick += async (_, _) => await HibernateIdleTabsAsync();
        _hibernateTimer.Start();
        _documentTabs.Deselected += (_, e) => { if (e.TabPage is { } p) _tabLastActive[p] = Environment.TickCount64; };
        _documentTabs.ControlRemoved += (_, e) => { if (e.Control is TabPage p) _tabLastActive.Remove(p); };
        Disposed += (_, _) => _hibernateTimer.Dispose();
    }

    private async Task HibernateIdleTabsAsync()
    {
        var minutes = Bcode.App.UI.PerformanceProfile.Resolve(_settings).HibernateSqlTabMinutes;
        if (minutes <= 0 || _hibernating || IsDisposed || _sessionRestoring) return;
        _hibernating = true;
        try
        {
            var now = Environment.TickCount64;
            var idleMs = minutes * 60_000L;
            foreach (var page in _documentTabs.TabPages.Cast<TabPage>().ToList())
            {
                if (ReferenceEquals(page, _documentTabs.SelectedTab) || page.Tag is not null || _closingPages.Contains(page)) continue;
                var sql = page.Controls.OfType<RawSqlControl>().FirstOrDefault();
                var web = sql is null ? page.Controls.OfType<ISleepableTab>().FirstOrDefault() : null;   // tab công cụ dạng trang web (Note, Check Mail...)
                if (sql is null && web is null) continue;
                if (!_tabLastActive.TryGetValue(page, out var last)) { _tabLastActive[page] = now; continue; }   // chưa biết lần xem cuối: bắt đầu đếm từ giờ
                if (now - last < idleMs) continue;
                if (sql is not null) await HibernateTabAsync(page, sql);
                else await web!.SleepAsync();   // huỷ WebView2, giữ control; tự dựng lại khi tab hiện (WebBarHost.OnVisibleChanged)
            }
        }
        finally { _hibernating = false; }
    }

    private async Task HibernateTabAsync(TabPage page, RawSqlControl c)
    {
        var snap = await c.TryCaptureSnapshotAsync();
        // Trong lúc chụp, tab có thể đã được chọn / đóng / thay control — khi đó thôi.
        if (snap is null || IsDisposed || c.IsDisposed || !_documentTabs.TabPages.Contains(page) || ReferenceEquals(page, _documentTabs.SelectedTab)
            || _closingPages.Contains(page) || !page.Controls.Contains(c))
            return;
        var key = _objectTabs.FirstOrDefault(kv => ReferenceEquals(kv.Value, page) && !kv.Key.StartsWith("usages:", StringComparison.Ordinal)).Key;
        page.Tag = new LazySql(snap.Text, snap.Sys, key, snap);
        page.Controls.Remove(c);
        _sessionText.Remove(c);
        c.Dispose();
    }
}
