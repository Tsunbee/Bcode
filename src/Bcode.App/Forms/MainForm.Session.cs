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
    private sealed record LazyTable(bool Sys, string Schema, string Table, string? FilterJson);
    private sealed record TableFilterDto(string Fields, string Where, string Order, int Top);

    /// <summary>Các tab vừa đóng (mới nhất đầu danh sách, tối đa 20) — "Reopen Closed Tab" (Ctrl+Shift+B) mở lại, kèm nội dung SQL lúc đóng.</summary>
    private readonly List<SessionTab> _closedTabs = new();
    private const int MaxClosedTabs = 20;
    private sealed class TextBox { public string Value = ""; public long Version = -1; }

    private readonly System.Windows.Forms.Timer _sessionTimer = new() { Interval = 4000 };
    private readonly ConditionalWeakTable<RawSqlControl, TextBox> _sessionText = new();
    private string? _lastSessionJson;
    private int _sessionWriting;
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
            {
                var mode = _settings.SessionAsk ? AskRestoreMode(state) : NormalizeMode(_settings.SessionMode);
                if (mode is not null) RestoreSession(state, mode);
            }
        }
        catch { /* phiên hỏng thì bỏ qua, mở Bcode như bình thường */ }
        finally { _sessionArmed = true; }
    }

    private static string? NormalizeMode(string? m) => m is "query" or "query_table" or "all" ? m : "all";

    /// <summary>Tab loại <paramref name="kind"/> có được khôi phục ở chế độ <paramref name="mode"/> không.</summary>
    private static bool KindAllowed(string kind, string mode) => kind switch
    {
        "sql" => true,
        "table" => mode is "query_table" or "all",
        _ => mode == "all",
    };

    /// <summary>Hộp hỏi lúc mở lại Bcode có phiên cũ: chọn kiểu khôi phục (hoặc không khôi phục). null = không khôi phục.</summary>
    private string? AskRestoreMode(SessionState state)
    {
        int q = state.Tabs.Count(x => x.Kind == "sql"), tb = state.Tabs.Count(x => x.Kind == "table"), tl = state.Tabs.Count(x => x.Kind != "sql" && x.Kind != "table");
        using var dlg = new RestoreSessionForm(q, tb, tl, state.SavedAt, NormalizeMode(_settings.SessionMode) ?? "all");
        dlg.ShowDialog(this);
        if (dlg.Mode is null) return null;
        if (dlg.DontAskAgain)
        {
            _settings.SessionAsk = false;
            _settings.SessionMode = dlg.Mode;
            try { _settings.Save(); } catch { /* chỉ áp cho phiên này */ }
        }
        return dlg.Mode;
    }

    /// <summary>Dựng 1 tab từ bản ghi phiên (tab SQL / Table dựng "chờ", tab công cụ mở thật). null nếu không dựng được.</summary>
    private TabPage? CreateTabFromSession(SessionTab t)
    {
        TabPage? page = null;
        if (t.Kind == "sql")
        {
            if (!string.IsNullOrEmpty(t.Key) && _objectTabs.TryGetValue(t.Key, out var existing) && _documentTabs.TabPages.Contains(existing)) return existing;
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
        else if (t.Kind == "table" && !string.IsNullOrEmpty(t.Key))
        {
            var parts = t.Key.Split('|');
            if (parts.Length == 3)
            {
                page = new TabPage(t.Title) { Tag = new LazyTable(parts[0] == "1", parts[1], parts[2], t.Text) };
                _documentTabs.TabPages.Add(page);
                Bcode.App.UI.ThemeManager.Apply(page);
            }
        }
        else if (t.Kind == "tool" && !string.IsNullOrEmpty(t.Key))
        {
            var before = _documentTabs.TabPages.Count;
            RunAppShortcut("tool:" + t.Key);
            if (_documentTabs.TabPages.Count > before) page = _documentTabs.TabPages[^1];
        }
        if (page is not null && t.Pinned) _documentTabs.SetPinned(page, true);
        return page;
    }

    private void RestoreSession(SessionState state, string mode)
    {
        TabPage? target = null;
        _sessionRestoring = true;
        try
        {
            for (var i = 0; i < state.Tabs.Count; i++)
            {
                var t = state.Tabs[i];
                if (!KindAllowed(t.Kind, mode)) continue;
                var page = CreateTabFromSession(t);
                if (page is null) continue;
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
        if (page.Tag is LazyTable lt)
        {
            page.Tag = null;
            var table = new TableEditControl(_tableDataService, _sqlObjectService, _dataScript, _scriptFileService, _genInsert, _genUpdate) { Dock = DockStyle.Fill };
            table.TableLoaded += name => { if (!page.IsDisposed) { page.Text = name; _documentTabs.Invalidate(); } };
            page.Controls.Add(table);
            Bcode.App.UI.ThemeManager.Apply(page);
            (string, string, string, int)? filter = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(lt.FilterJson) && System.Text.Json.JsonSerializer.Deserialize<TableFilterDto>(lt.FilterJson) is { } dto)
                    filter = (dto.Fields, dto.Where, dto.Order, dto.Top);
            }
            catch { /* bộ lọc hỏng — mở bảng không lọc */ }
            _ = table.OpenTableAsync(lt.Sys, lt.Schema, lt.Table, filter);
            return;
        }
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
            // Hỏi số phiên bản (nhẹ) trước: nội dung không đổi từ lần lấy trước thì khỏi kéo lại cả script (script 25 MB mỗi 4 giây là giật).
            var version = await c.GetEditorVersionAsync();
            if (version >= 0 && _sessionText.TryGetValue(c, out var known) && known.Version == version) return;
            var text = await c.GetScriptTextAsync();
            _sessionText.AddOrUpdate(c, new TextBox { Value = text, Version = version });
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
        if (!background)
        {
            var json = SessionStore.Serialize(state);
            if (json == _lastSessionJson) return;
            _lastSessionJson = json;
            SessionStore.Save(state);
            return;
        }
        // Tick định kỳ: dựng JSON (có thể hàng MB) + so sánh + ghi file đều ở luồng nền; mỗi lúc chỉ một lượt để không chồng nhau.
        if (Interlocked.Exchange(ref _sessionWriting, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try
            {
                var json = SessionStore.Serialize(state);
                if (json == _lastSessionJson) return;
                _lastSessionJson = json;
                SessionStore.Save(state);
            }
            catch { /* không ghi được phiên thì thôi */ }
            finally { Volatile.Write(ref _sessionWriting, 0); }
        });
    }

    private SessionState BuildSessionState(string workspace)
    {
        var tabs = new List<SessionTab>();
        var selected = -1;
        foreach (TabPage p in _documentTabs.TabPages)
        {
            if (DescribeTab(p) is not { } rec) continue;
            tabs.Add(rec);
            if (ReferenceEquals(p, _documentTabs.SelectedTab)) selected = tabs.Count - 1;
        }
        return new SessionState(workspace, selected, tabs, DateTime.Now);
    }

    /// <summary>Bản ghi để lưu / mở lại của 1 tab (null = loại tab không lưu).</summary>
    private SessionTab? DescribeTab(TabPage p)
    {
        var pinned = _documentTabs.IsPinned(p);
        if (p.Tag is LazySql l) return new SessionTab("sql", p.Text, l.Key, l.Text, l.Sys, pinned);
        if (p.Tag is LazyTable lt) return new SessionTab("table", p.Text, $"{(lt.Sys ? 1 : 0)}|{lt.Schema}|{lt.Table}", lt.FilterJson, lt.Sys, pinned);
        if (p.Controls.OfType<RawSqlControl>().FirstOrDefault() is { } c)
        {
            var text = _sessionText.TryGetValue(c, out var box) ? box.Value : "";
            var key = _objectTabs.FirstOrDefault(kv => ReferenceEquals(kv.Value, p) && !kv.Key.StartsWith("usages:", StringComparison.Ordinal)).Key;
            return new SessionTab("sql", p.Text, key, text, c.UseSysDatabase, pinned);
        }
        if (p.Controls.OfType<TableEditControl>().FirstOrDefault() is { LoadedTable: { } lo } tec)
        {
            var f = tec.LoadedFilter;
            return new SessionTab("table", p.Text, $"{(lo.Sys ? 1 : 0)}|{lo.Schema}|{lo.Table}", System.Text.Json.JsonSerializer.Serialize(new TableFilterDto(f.Fields, f.Where, f.Order, f.Top)), lo.Sys, pinned);
        }
        if (p.Controls.OfType<TableEditControl>().Any()) return new SessionTab("tool", p.Text, "table", null, false, pinned);   // tab Table chưa tải bảng nào: mở lại = tab Table trống
        if (ToolKeyOf(p) is { } k) return new SessionTab("tool", p.Text, k, null, false, pinned);
        return null;
    }

    // ---- mở lại tab vừa đóng ----------------------------------------------------------------------------

    /// <summary>Gọi ngay trước khi đóng 1 tab: nhớ lại để Ctrl+Shift+B mở lại được. Tab SQL trống cũng được nhớ.</summary>
    private void RecordClosedTab(TabPage page)
    {
        if (_sessionRestoring || DescribeTab(page) is not { } rec) return;
        _closedTabs.Insert(0, rec with { Pinned = false });
        if (_closedTabs.Count > MaxClosedTabs) _closedTabs.RemoveRange(MaxClosedTabs, _closedTabs.Count - MaxClosedTabs);
    }

    private static string ClosedTabLabel(SessionTab t)
    {
        var title = string.IsNullOrWhiteSpace(t.Title) ? "(không tên)" : t.Title.Trim();
        var kind = t.Kind switch { "sql" => "Query", "table" => "Table", _ => "Tool" };
        var first = t.Kind == "sql" ? (t.Text ?? "").Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0) ?? "" : "";
        if (first.Length > 40) first = first[..40] + "…";
        return first.Length > 0 && !first.Equals(title, StringComparison.OrdinalIgnoreCase) ? $"{kind} · {title} — {first}" : $"{kind} · {title}";
    }

    /// <summary>Mở lại tab đã đóng thứ <paramref name="index"/> (0 = vừa đóng gần nhất) rồi chọn nó.</summary>
    private void ReopenClosedTab(int index)
    {
        if (index < 0 || index >= _closedTabs.Count) { PushStatus("Chưa có tab nào vừa đóng để mở lại."); return; }
        var rec = _closedTabs[index];
        _closedTabs.RemoveAt(index);
        var page = CreateTabFromSession(rec);
        if (page is null) { PushStatus("Không mở lại được tab \"" + rec.Title + "\"."); return; }
        _documentTabs.SelectedTab = page;
        MaterializeLazy(page);
        UpdateQuickAccessOverlayBounds();
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

/// <summary>Hộp hỏi lúc mở lại Bcode có phiên làm việc cũ: khôi phục chỉ Query / Query + Table / tất cả / không.</summary>
internal sealed class RestoreSessionForm : Bcode.App.UI.ThemedForm
{
    public string? Mode { get; private set; }
    public bool DontAskAgain => _dontAsk.Checked;
    private readonly CheckBox _dontAsk = new() { Text = "Lần sau không hỏi nữa — dùng ngay lựa chọn này (đổi lại ở Template → Cửa sổ)", AutoSize = true };

    public RestoreSessionForm(int queries, int tables, int tools, DateTime savedAt, string defaultMode)
    {
        Text = "Khôi phục phiên làm việc";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 190);

        var when = savedAt == default ? "" : $" (lưu lúc {savedAt:dd/MM/yyyy HH:mm})";
        var info = new Label
        {
            Left = 16, Top = 14, Width = 608, Height = 58,
            Text = $"Phiên làm việc lần trước còn {queries} tab Query, {tables} tab Table, {tools} tab công cụ{when}.\nBạn muốn khôi phục kiểu nào?",
        };
        Controls.Add(info);

        Button Make(string text, string? mode, int x, int w)
        {
            var b = new Button { Text = text, Left = x, Top = 78, Width = w, Height = 34 };
            b.Click += (_, _) => { Mode = mode; DialogResult = DialogResult.OK; Close(); };
            Controls.Add(b);
            if (mode == defaultMode) { AcceptButton = b; ActiveControl = b; }
            return b;
        }
        Make("Chỉ Query", "query", 16, 130);
        Make("Query + Table", "query_table", 156, 150);
        Make("Tất cả", "all", 316, 120);
        Make("Không khôi phục", null, 446, 178);
        _dontAsk.Left = 16; _dontAsk.Top = 128;
        Controls.Add(_dontAsk);
        var hint = new Label { Left = 16, Top = 154, Width = 608, Height = 28, Text = "Tab đóng nhầm trong lúc làm việc: Ctrl+Shift+B (hoặc chuột phải thanh tab → Reopen Closed Tab) để mở lại." };
        Controls.Add(hint);
        FormClosing += (_, e) => { if (DialogResult == DialogResult.None) Mode = null; };
    }
}
