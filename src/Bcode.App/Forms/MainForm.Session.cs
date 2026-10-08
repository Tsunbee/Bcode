using System.Runtime.CompilerServices;
using Bcode.App.Controls;
using Bcode.App.Services;

namespace Bcode.App.Forms;

/// <summary>
/// Khôi phục phiên làm việc: ghi định kỳ các tab đang mở (kèm nội dung SQL chưa lưu) và dựng lại khi mở Bcode. Để KHÔNG làm chậm lúc mở:
/// chỉ tab đang chọn được dựng ngay (Monaco + WebView2), các tab SQL còn lại là tab "chờ" — có tiêu đề, nội dung giữ trong bộ nhớ — và chỉ dựng khi bạn bấm vào.
/// Chỉ bắt đầu ghi SAU khi đã khôi phục xong (nếu không, lần mở với 0 tab sẽ ghi đè phiên cũ).
/// </summary>
public partial class MainForm
{
    private sealed record LazySql(string Text, bool Sys, string? Key);
    private sealed class TextBox { public string Value = ""; }

    private readonly System.Windows.Forms.Timer _sessionTimer = new() { Interval = 4000 };
    private readonly ConditionalWeakTable<RawSqlControl, TextBox> _sessionText = new();
    private string? _lastSessionJson;
    private bool _sessionArmed, _sessionRestoring;
    private TabPage? _prevSelectedPage;

    private void InitSession()
    {
        _sessionTimer.Tick += async (_, _) => await SessionTickAsync();
        ApplySessionInterval();
        _sessionTimer.Start();
        _documentTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_sessionRestoring) return;
            var now = _documentTabs.SelectedTab;
            if (_prevSelectedPage?.Controls.OfType<RawSqlControl>().FirstOrDefault() is { IsReady: true } left) _ = CaptureSessionTextAsync(left);   // gõ dở rồi chuyển tab: giữ bản mới nhất
            _prevSelectedPage = now;
            if (now is not null) MaterializeLazy(now);
        };
        Disposed += (_, _) => _sessionTimer.Dispose();
    }

    private void ApplySessionInterval() => _sessionTimer.Interval = Math.Clamp(_settings.SessionSaveSeconds, 1, 600) * 1000;

    // ---- khôi phục --------------------------------------------------------------------------------------

    /// <summary>Gọi một lần sau khi màn hình Projects đóng lần đầu: dựng lại phiên của dự án đang chọn (nếu bật và chưa có tab nào), rồi mới bật ghi phiên.</summary>
    private void TryRestoreSession()
    {
        if (_sessionArmed) return;
        try
        {
            if (_settings.RestoreSession && _connections.Current is { } ws && _documentTabs.TabPages.Count == 0
                && SessionStore.Load(ws.Name) is { Tabs.Count: > 0 } state)
                RestoreSession(state);
        }
        catch { /* phiên hỏng thì bỏ qua, mở Bcode như bình thường */ }
        finally { _sessionArmed = true; }
    }

    private void RestoreSession(SessionState state)
    {
        TabPage? target = null;
        _sessionRestoring = true;
        try
        {
            for (var i = 0; i < state.Tabs.Count; i++)
            {
                var t = state.Tabs[i];
                TabPage? page = null;
                if (t.Kind == "sql")
                {
                    page = new TabPage(t.Title) { Tag = new LazySql(t.Text ?? "", t.Sys, t.Key) };
                    _documentTabs.TabPages.Add(page);
                    Bcode.App.UI.ThemeManager.Apply(page);
                    if (!string.IsNullOrEmpty(t.Key))
                    {
                        var key = t.Key;
                        _objectTabs[key] = page;
                        page.Disposed += (_, _) => _objectTabs.Remove(key);
                    }
                }
                else if (t.Kind == "tool" && !string.IsNullOrEmpty(t.Key))
                {
                    var before = _documentTabs.TabPages.Count;
                    RunAppShortcut("tool:" + t.Key);
                    if (_documentTabs.TabPages.Count > before) page = _documentTabs.TabPages[^1];
                }
                if (page is null) continue;
                if (t.Pinned) _documentTabs.SetPinned(page, true);
                if (i == state.Selected) target = page;
            }
        }
        finally { _sessionRestoring = false; }

        target ??= _documentTabs.TabPages.Count > 0 ? _documentTabs.TabPages[^1] : null;
        if (target is null) return;
        _documentTabs.SelectedTab = target;
        _prevSelectedPage = target;
        MaterializeLazy(target);
        UpdateQuickAccessOverlayBounds();
    }

    /// <summary>Dựng thật (Monaco + WebView2) cho tab SQL "chờ" khi nó được chọn.</summary>
    private void MaterializeLazy(TabPage page)
    {
        if (page.Tag is not LazySql lazy) return;
        page.Tag = null;
        var control = TakeSqlControl();
        control.SetDatabase(lazy.Sys);
        control.SetScriptText(lazy.Text);
        control.Dock = DockStyle.Fill;
        page.Controls.Add(control);
        Bcode.App.UI.ThemeManager.Apply(page);
        _sessionText.AddOrUpdate(control, new TextBox { Value = lazy.Text });
    }

    // ---- ghi --------------------------------------------------------------------------------------------

    private async Task CaptureSessionTextAsync(RawSqlControl c)
    {
        try
        {
            var text = await c.GetScriptTextAsync();
            _sessionText.AddOrUpdate(c, new TextBox { Value = text });
        }
        catch { /* editor đang đóng — giữ bản cũ */ }
    }

    private async Task SessionTickAsync()
    {
        if (!_sessionArmed || !_settings.RestoreSession || IsDisposed) return;
        if (WindowState != FormWindowState.Minimized && _documentTabs.SelectedTab?.Controls.OfType<RawSqlControl>().FirstOrDefault() is { IsReady: true } c)
            await CaptureSessionTextAsync(c);
        WriteSession(background: true);
    }

    private void WriteSession(bool background)
    {
        if (_connections.Current is not { } ws) return;
        var state = BuildSessionState(ws.Name);
        var json = SessionStore.Serialize(state);
        if (json == _lastSessionJson) return;
        _lastSessionJson = json;
        if (background) _ = Task.Run(() => SessionStore.Save(state)); else SessionStore.Save(state);
    }

    private SessionState BuildSessionState(string workspace)
    {
        var tabs = new List<SessionTab>();
        var selected = -1;
        foreach (TabPage p in _documentTabs.TabPages)
        {
            var pinned = _documentTabs.IsPinned(p);
            var before = tabs.Count;
            if (p.Tag is LazySql l) tabs.Add(new SessionTab("sql", p.Text, l.Key, l.Text, l.Sys, pinned));
            else if (p.Controls.OfType<RawSqlControl>().FirstOrDefault() is { } c)
            {
                var text = _sessionText.TryGetValue(c, out var box) ? box.Value : "";
                var key = _objectTabs.FirstOrDefault(kv => ReferenceEquals(kv.Value, p) && !kv.Key.StartsWith("usages:", StringComparison.Ordinal)).Key;
                tabs.Add(new SessionTab("sql", p.Text, key, text, c.UseSysDatabase, pinned));
            }
            else if (ToolKeyOf(p) is { } k) tabs.Add(new SessionTab("tool", p.Text, k, null, false, pinned));
            if (tabs.Count > before && ReferenceEquals(p, _documentTabs.SelectedTab)) selected = tabs.Count - 1;
        }
        return new SessionState(workspace, selected, tabs, DateTime.Now);
    }

    /// <summary>Khoá tool của các tab công cụ một-tab-duy-nhất (null = loại tab không khôi phục).</summary>
    private string? ToolKeyOf(TabPage p) =>
        ReferenceEquals(p, _lookupTabPage) ? "lookup" :
        ReferenceEquals(p, _fileLookupTabPage) ? "file_lookup" :
        ReferenceEquals(p, _genUpdatePackageTabPage) ? "gen_update_package" :
        ReferenceEquals(p, _fileReferenceTabPage) ? "file_reference" :
        ReferenceEquals(p, _compareTextTab) ? "compare_text" :
        ReferenceEquals(p, _sqlProfilerTab) ? "sql_profiler" :
        ReferenceEquals(p, _setupEInvoiceTab) ? "setup_einvoice" :
        ReferenceEquals(p, _stringBeautyTab) ? "string_beauty" :
        ReferenceEquals(p, _queryHistoryTab) ? "query_history" :
        ReferenceEquals(p, _compareObjectsTab) ? "compare_objects" :
        ReferenceEquals(p, _checkMailTab) ? "check_mail" :
        ReferenceEquals(p, _createRptTab) ? "create_rpt_xlsx" :
        ReferenceEquals(p, _excelToFrxTab) ? "excel_to_frx" :
        ReferenceEquals(p, _advanceNoteTab) ? "note_new" : null;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel || !_sessionArmed || !_settings.RestoreSession) return;
        try { WriteSession(background: false); } catch { /* không ghi được thì thôi */ }
    }
}
