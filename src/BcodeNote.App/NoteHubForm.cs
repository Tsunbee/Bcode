using System.Diagnostics;
using System.Text.Json;
using Bcode.App;
using Bcode.App.Controls;
using Bcode.App.Forms;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;

namespace BcodeNote.App;

/// <summary>
/// Cửa sổ chính BcodeNote: trái = trang notehub.html (danh sách dự án + cây menu wcommand của dự án đang chọn), phải = Note (New) của Bcode
/// (<see cref="AdvanceNoteControl"/>) cho dự án đó. Mỗi dự án một bộ service riêng (<see cref="ProjectContext"/>, kết nối riêng) nên việc
/// đang chạy (Generate, Sync…) của dự án cũ không bao giờ chạy nhầm sang database của dự án vừa chọn.
/// </summary>
internal sealed class NoteHubForm : ThemedForm
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    /// <summary>Bộ service gắn với 1 dự án — tạo mới mỗi lần chọn dự án, không đổi workspace giữa chừng.</summary>
    private sealed class ProjectContext
    {
        public required Workspace Ws { get; init; }
        public required DbConnectionService Connections { get; init; }
        public required SqlObjectBrowserService Sql { get; init; }
        public required WCommandService WCommand { get; init; }
        public required GenAllService GenAll { get; init; }
        public required TableDataService TableData { get; init; }
        public List<WCommandItem> Menus { get; set; } = new();
        /// <summary>Chỉ số menu cha của từng dòng trong <see cref="Menus"/> (-1 = gốc).</summary>
        public List<int> Parents { get; set; } = new();
    }

    private readonly WebBarHost _hub = new("notehub.html") { Dock = DockStyle.Left };
    private readonly Splitter _split = new() { Dock = DockStyle.Left, Width = 5 };
    private readonly Panel _notePanel = new() { Dock = DockStyle.Fill };
    private readonly AdvanceNoteService _store = new();
    private readonly FileLookupService _files = new();
    private readonly PeriodTableQueryService _periods = new();
    private readonly DataScriptService _dataScript = new();
    private readonly HubPrefs _prefs = HubPrefs.Load();
    private readonly Dictionary<string, QuickListForm> _quickLists = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _startProject;
    private AppSettings _settings;
    private ProjectContext? _ctx;
    private AdvanceNoteControl? _note;
    private bool _closeFlushed;

    /// <summary>Bố cục tự dàn bằng Dock (như MainForm của Bcode) — không nhân thêm theo UiScale.</summary>
    protected override bool ScaleLayoutWithUiScale => false;

    public NoteHubForm(string? startProject)
    {
        _startProject = startProject;
        _settings = AppSettings.Load();
        UiScale.SetMode(_settings.UiScale, this);
        // Cùng tuỳ chọn dò file với Bcode (Gen All / gợi ý theo menu dùng chung FileLookupService).
        FileLookupService.SetIgnoreSuffixes(_settings.FileLookupIgnoreSuffixes);
        FileLookupService.SetSearchRules(_settings);
        FileLookupService.CacheMode = Enum.TryParse<FileLookupCacheMode>(_settings.FileLookupCacheMode, true, out var cacheMode) ? cacheMode : FileLookupCacheMode.On;

        Text = "Bcode Note";
        if (AppIcons.AppIcon is { } icon) Icon = icon;
        Width = 1500;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = _prefs.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        KeyPreview = true;

        _hub.Width = Math.Max(260, DpiScale.Px(this, _prefs.LeftWidth));
        _split.BackColor = AppColors.Border;
        _split.MinSize = 220;
        _split.SplitterMoved += (_, _) => { _prefs.LeftWidth = (int)Math.Round(_hub.Width * 96.0 / DeviceDpi); _prefs.Save(); };
        _notePanel.BackColor = AppColors.PanelAlt;
        // Dock: thêm Fill trước, rồi Splitter, rồi khung trái (control thêm sau dock trước).
        Controls.Add(_notePanel);
        Controls.Add(_split);
        Controls.Add(_hub);

        _hub.Ready += PushProjects;
        _hub.Message += root =>
        {
            var msg = root.Clone();
            _ = OnHubAsync(msg);
        };
    }

    // ===================================================================== dự án

    private void PushProjects()
    {
        var projects = _settings.Workspaces.Select(w => new
        {
            name = w.Name,
            id = w.ProjectId,
            server = w.Server,
            app = w.EffectiveAppDatabase,
            sys = w.SysDatabase,
            source = w.SourcePath,
        }).ToList();
        var want = new[] { _ctx?.Ws.Name, _startProject, _prefs.LastProject, _settings.LastWorkspace }
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n) && _settings.Workspaces.Any(w => w.Name.Equals(n, StringComparison.OrdinalIgnoreCase)));
        _hub.Call($"hub.init({J(new { projects, selected = want ?? "", collapsed = _prefs.ProjectsCollapsed })})");
        _ = PushCountsAsync();
        if (want is not null && _ctx is null) _ = SwitchProjectAsync(want);
        else if (projects.Count == 0) _hub.Call($"hub.status({J("Chưa có dự án nào — mở Bcode, vào Projects (Ctrl+Shift+P) để khai báo dự án.")}, 'err')");
    }

    /// <summary>Số y/c đã lưu của từng dự án (đọc các file AdvanceNotes ở luồng nền).</summary>
    private async Task PushCountsAsync()
    {
        var names = _settings.Workspaces.Select(w => w.Name).ToList();
        var counts = await Task.Run(() => names.ToDictionary(n => n, n => _store.Load(n).Count(r => !r.Done)));
        if (!IsDisposed) _hub.Call($"hub.setCounts({J(counts)})");
    }

    private async Task SwitchProjectAsync(string name)
    {
        var ws = _settings.Workspaces.FirstOrDefault(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (ws is null || ReferenceEquals(ws, _ctx?.Ws)) return;
        if (_note is not null) await _note.FlushAsync();   // ghi phần đang gõ dở vào ĐÚNG dự án cũ trước khi đổi

        var conn = new DbConnectionService();
        conn.SetWorkspace(ws);
        var sql = new SqlObjectBrowserService(conn);
        var wcmd = new WCommandService(conn);
        var tableData = new TableDataService(conn, _periods);
        var genAll = new GenAllService(_files, sql, wcmd) { TableData = tableData, DataScript = _dataScript };
        var ctx = new ProjectContext { Ws = ws, Connections = conn, Sql = sql, WCommand = wcmd, GenAll = genAll, TableData = tableData };
        _ctx = ctx;

        var old = _note;
        var note = new AdvanceNoteControl(_store, genAll, () => ws, AppSettings.Load()) { Dock = DockStyle.Fill };
        note.OpenScriptsRequested += scripts => OpenScripts(ws, scripts);
        note.AiCatalogRequested += (reqName, content) => OpenQuickList(ctx)?.StartAi(reqName, content);
        note.CatalogAssistRequested += (reqName, content, mode) =>
        {
            if (OpenQuickList(ctx) is not { } f) return;
            if (mode == "rules") f.StartRules(reqName, content); else f.StartAnalyze(reqName, content);
        };
        _note = note;
        _notePanel.Controls.Add(note);
        if (old is not null) { _notePanel.Controls.Remove(old); old.Dispose(); }

        _prefs.LastProject = ws.Name;
        _prefs.Save();
        Text = $"Bcode Note — {ws.Name}";
        _hub.Call($"hub.projectSelected({J(ws.Name)})");
        if (ws.PasswordUnreadable)
            _hub.Call($"hub.status({J("Mật khẩu của dự án này lưu bằng tài khoản / máy khác — vào Bcode, Edit Project nhập lại.")}, 'err')");
        await LoadMenusAsync(ctx, force: false);
    }

    // ===================================================================== cây menu

    private async Task LoadMenusAsync(ProjectContext ctx, bool force)
    {
        var cached = force ? null : ctx.WCommand.LoadCachedFlat();
        if (cached is { Count: > 0 }) PushMenus(ctx, ctx.WCommand.TreeFromFlat(cached), "bản lưu");
        else _hub.Call("hub.menusLoading()");
        try
        {
            if (cached is { Count: > 0 } && await ctx.WCommand.IsCacheCurrentAsync()) return;
            var tree = await ctx.WCommand.LoadTreeAsync();
            if (ctx == _ctx) PushMenus(ctx, tree, "");
        }
        catch (Exception ex)
        {
            if (ctx == _ctx) _hub.Call($"hub.menuError({J((cached is { Count: > 0 } ? "Đang dùng bản lưu — " : "") + "không đọc được wcommand: " + ex.Message)})");
        }
    }

    /// <summary>Đẩy cây menu xuống trang dạng phẳng: i = chỉ số (để hỏi gợi ý), p = chỉ số menu cha (-1 = gốc), giữ đúng thứ tự cây của WCommandService.</summary>
    private void PushMenus(ProjectContext ctx, List<WCommandItem> roots, string note)
    {
        var flat = new List<WCommandItem>();
        var parents = new List<int>();
        var rows = new List<object>();
        void Walk(WCommandItem m, int parent)
        {
            var i = flat.Count;
            flat.Add(m);
            parents.Add(parent);
            rows.Add(new { i, p = parent, bar = m.Bar, bar2 = m.Bar2, link = m.Link, sysid = m.SysId, id = m.WMenuId });
            foreach (var c in m.Children) Walk(c, i);
        }
        foreach (var r in roots) Walk(r, -1);
        ctx.Menus = flat;
        ctx.Parents = parents;
        _hub.Call($"hub.setMenus({J(new { project = ctx.Ws.Name, menus = rows, note, bar2 = AppSettings.MenuNameBar2 })})");
    }

    private async Task SuggestAsync(int index)
    {
        if (_ctx is not { } ctx || index < 0 || index >= ctx.Menus.Count) return;
        var menu = ctx.Menus[index];
        _hub.Call($"hub.busy({J("Đang dò file / procedure / table của menu “" + menu.Bar + "”…")})");
        MenuSuggestion s;
        var ancestors = new List<WCommandItem>();
        for (var p = ctx.Parents[index]; p >= 0; p = ctx.Parents[p]) ancestors.Insert(0, ctx.Menus[p]);
        try { s = await ctx.GenAll.SuggestForMenuAsync(ctx.Ws, menu, ancestors); }
        catch (Exception ex) { _hub.Call($"hub.busy(null); hub.status({J("Gợi ý lỗi: " + ex.Message)}, 'err')"); return; }
        _hub.Call("hub.busy(null)");
        if (ctx != _ctx || IsDisposed) return;   // đã chọn dự án khác trong lúc dò

        using var dlg = new MenuSuggestForm(s, ctx.Ws.Name);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Pick is not { } pick || ctx != _ctx || _note is null) return;
        pick.Menu = s.Menu;
        pick.NewName = string.IsNullOrWhiteSpace(menu.Bar) ? menu.WMenuId : menu.Bar.Trim();
        _note.ApplySuggestion(pick);
        _hub.Call($"hub.status({J("Đã điền gợi ý của menu “" + s.Menu + "” vào y/c — sửa lại nếu cần rồi Generate.")}, 'ok')");
    }

    private async Task OnHubAsync(JsonElement msg)
    {
        var action = msg.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
        string Str(string key) => msg.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        try
        {
            switch (action)
            {
                case "selectProject":
                    await SwitchProjectAsync(Str("name"));
                    break;
                case "reloadProjects":
                    _settings = AppSettings.Load();
                    PushProjects();
                    break;
                case "reloadMenus":
                    if (_ctx is { } ctx) await LoadMenusAsync(ctx, force: true);
                    break;
                case "suggest":
                    await SuggestAsync(msg.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var n) ? n : -1);
                    break;
                case "collapse":
                    _prefs.ProjectsCollapsed = msg.TryGetProperty("on", out var on) && on.ValueKind == JsonValueKind.True;
                    _prefs.Save();
                    break;
                case "openFolder":
                {
                    var path = Str("path");
                    if (path.Length > 0 && Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _hub.Call($"hub.busy(null); hub.status({J(ex.Message)}, 'err')");
        }
    }

    // ===================================================================== nút của Note (New)

    /// <summary>"Mở script vào tab Query": gửi sang Bcode đang mở (tab SQL Query mới đúng App / Sys). Bcode chưa mở → ghi các file .sql vào thư mục tạm
    /// rồi mở thư mục đó.</summary>
    private void OpenScripts(Workspace ws, IReadOnlyList<(string Title, bool Sys, string Text)> scripts)
    {
        var failed = new List<(string Title, bool Sys, string Text)>();
        var reason = "";
        foreach (var sc in scripts)
        {
            var r = BcodePipe.SendScript(sc.Text, sc.Title, ws.Name, sc.Sys);
            if (r.Length > 0) { failed.Add(sc); reason = r; }
        }
        if (failed.Count == 0)
        {
            _hub.Call($"hub.status({J($"Đã gửi {scripts.Count} script sang Bcode (tab SQL Query).")}, 'ok')");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "BcodeNote", SafeName(ws.Name), DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        Directory.CreateDirectory(dir);
        var i = 0;
        foreach (var (title, sys, text) in failed)
            File.WriteAllText(Path.Combine(dir, $"{++i:00}_{SafeName(title)}.sql"), $"-- Dự án: {ws.Name} · database {(sys ? "Sys" : "App")}\r\n" + text);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        _hub.Call($"hub.status({J($"{reason} Đã lưu {failed.Count} script vào {dir} — mở Bcode (đúng dự án) để chạy.")}, 'err')");
    }

    /// <summary>"Tạo nhanh danh mục" của Bcode (cần key bản quyền như trong Bcode), mỗi dự án 1 cửa sổ, chạy trên kết nối của chính dự án đó.</summary>
    private QuickListForm? OpenQuickList(ProjectContext ctx)
    {
        if (!LicenseService.IsUnlocked)
        {
            using var f = new LicenseKeyForm("Tính năng “Tạo nhanh danh mục” cần key bản quyền. Dán key rồi bấm Xác nhận.");
            f.ShowDialog(this);
            if (!LicenseService.IsUnlocked) return null;
        }
        if (_quickLists.TryGetValue(ctx.Ws.Name, out var open) && !open.IsDisposed)
        {
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
            return open;
        }
        var form = new QuickListForm(ctx.Sql, ctx.TableData, ctx.Connections, ctx.WCommand, AppSettings.Load());
        form.Text += " — " + ctx.Ws.Name;
        _quickLists[ctx.Ws.Name] = form;
        form.FormClosed += (_, _) => _quickLists.Remove(ctx.Ws.Name);
        form.AddScriptRequested += script =>
        {
            try { Clipboard.SetText(script); } catch { /* clipboard bận */ }
            _hub.Call($"hub.status({J("Đã copy script vào clipboard.")}, 'ok')");
        };
        form.OpenSqlRequested += (script, title) =>
        {
            var r = BcodePipe.SendScript(script, title, ctx.Ws.Name, sys: false);
            if (r.Length == 0) return;
            try { Clipboard.SetText(script); } catch { /* clipboard bận */ }
            MessageBox.Show(form, r + "\n\nScript đã được copy vào clipboard.", "Bcode Note", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };
        form.Show();
        return form;
    }

    private static string SafeName(string s)
    {
        var t = string.Concat((s ?? "").Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return t.Length == 0 ? "script" : t.Length > 60 ? t[..60] : t;
    }

    // ===================================================================== đóng cửa sổ

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_closeFlushed && _note is not null)
        {
            // Trang tự lưu sau 0,7 giây — đóng ngay sau khi gõ thì ghi nốt rồi mới đóng.
            e.Cancel = true;
            _closeFlushed = true;
            await _note.FlushAsync();
            Close();
            return;
        }
        _prefs.Maximized = WindowState == FormWindowState.Maximized;
        _prefs.Save();
        base.OnFormClosing(e);
    }
}
