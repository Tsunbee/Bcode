using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "Lịch sử sửa procedure / function / view / trigger" của SQL Query: chọn dự án và object, xem các phiên bản đã lưu (kèm máy, tài khoản,
/// IP người sửa) và so sánh bên cạnh bản hiện tại trong database (hoặc bản trước / kế tiếp) bằng Monaco diff. Giao diện là trang WebView2
/// (Web/Shell/sqlhistory.html) tự co giãn theo cửa sổ và UiScale; dữ liệu do <see cref="SqlHistoryService"/> quản lý, thư mục lưu khai báo
/// ở màn hình "Giao diện (Template)". Khôi phục = đưa nội dung vào editor SQL Query (không tự chạy).
///
/// Ngoài các bản Bcode tự lưu, danh sách còn gộp thêm các thay đổi do công cụ khác (SSMS, tool deploy...) nếu dự án đã cài DDL trigger
/// theo dõi (<see cref="DdlTrackingService"/>, nút "Theo dõi ngoài Bcode" — cài riêng cho từng database của từng dự án).
/// </summary>
public class SqlHistoryForm : ThemedForm
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string DbPrefix = "db-";

    private readonly RawSqlService _service;
    private readonly SqlTrackedObject? _focus;
    private readonly bool _useSys;
    private readonly Func<string, Task> _loadIntoEditor;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    /// <param name="focus">Object đang soạn trong editor (chọn sẵn ở danh sách); null = object sửa gần nhất.</param>
    /// <param name="useSys">Database đang chọn ở SQL Query (Sys hay App).</param>
    /// <param name="loadIntoEditor">Đưa văn bản vào editor SQL Query.</param>
    public SqlHistoryForm(RawSqlService service, SqlTrackedObject? focus, bool useSys, Func<string, Task> loadIntoEditor)
    {
        _service = service;
        _focus = focus;
        _useSys = useSys;
        _loadIntoEditor = loadIntoEditor;

        Text = "Lịch sử sửa procedure / function";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        Width = 1280;
        Height = 780;
        MinimumSize = new Size(560, 480);
        StartPosition = FormStartPosition.CenterParent;
        ShowIcon = false;
        Controls.Add(_web);

        ThemeManager.ThemeChanged += PushTheme;
        FormClosed += (_, _) => ThemeManager.ThemeChanged -= PushTheme;
        Load += async (_, _) => await InitWebAsync();
    }

    private Workspace? Ws => _service.Connections.Current;

    private async Task InitWebAsync()
    {
        try
        {
            await WebViewEnvironment.InitAsync(_web);
            _web.CoreWebView2.WebMessageReceived += OnWebMessage;
            _web.CoreWebView2.Navigate(UiOverrides.UrlFor("sqlhistory.html"));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Không mở được giao diện WebView2:\n" + ex.Message, "Bcode — Lịch sử", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        }
    }

    private async void OnWebMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var action = doc.RootElement.GetProperty("action").GetString();
            var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;

            switch (action)
            {
                case "ready": await PushStateAsync(); break;
                case "versions": await PushVersionsAsync(Str(data, "project"), Str(data, "db"), Str(data, "key")); break;
                case "load": await PushVersionAsync(Str(data, "project"), Str(data, "db"), Str(data, "key"), Str(data, "id")); break;
                case "current": await PushCurrentAsync(Str(data, "project"), Str(data, "db"), Str(data, "key")); break;
                case "restore":
                    await _loadIntoEditor(Str(data, "text"));
                    Close();
                    break;
                case "copy": Clipboard.SetText(Str(data, "text")); break;
                case "open-folder": OpenFolder(Ws, Str(data, "project"), Str(data, "db"), Str(data, "key")); break;
                case "tracking-status": await PushTrackingAsync(); break;
                case "tracking-install": await TrackingInstallAsync(Str(data, "which")); break;
                case "tracking-remove": await TrackingRemoveAsync(Str(data, "which")); break;
                case "close": Close(); break;
            }
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
        }
    }

    // ---------------------------------------------------------------- trạng thái / danh sách

    private async Task PushStateAsync()
    {
        PushTheme();
        var ws = Ws;
        var objects = await Task.Run(() => SqlHistoryService.ListObjects(ws));
        var all = objects.Select(o => new { project = o.Project, database = o.Database, key = o.Key, kind = o.Kind, count = o.Count, last = o.Last }).ToList();

        // Object chỉ có dấu vết do công cụ khác sửa (chưa từng sửa qua Bcode) cũng đưa vào danh sách.
        if (ws is not null)
            foreach (var (sys, db) in Databases(ws))
                foreach (var o in await DdlTrackingService.ListObjectsAsync(_service.Connections, sys))
                {
                    var key = $"{o.Schema}.{o.Name}";
                    var project = SqlHistoryService.ProjectOf(ws);
                    var existing = all.FindIndex(x => x.project == project && x.database == db && x.key == key);
                    if (existing >= 0) all[existing] = all[existing] with { count = all[existing].count + o.Count };
                    else all.Add(new { project, database = db, key, kind = o.ObjectType, count = o.Count, last = o.Last });
                }

        var current = ws is null ? null : new
        {
            project = SqlHistoryService.ProjectOf(ws),
            db = _useSys ? ws.SysDatabase : ws.AppDatabase,
            key = _focus?.Key ?? "",
        };
        var state = new
        {
            root = SqlHistoryService.RootFor(ws),
            current = current ?? new { project = "", db = "", key = "" },
            objects = all.OrderByDescending(o => o.last).Select(o => new { o.project, o.database, o.key, o.kind, o.count, last = o.last.ToString("o") }),
        };
        await Js($"window.init({JsonSerializer.Serialize(state, Web)}, {(AppColors.IsDark ? "true" : "false")}, {UiThemes.PaletteJson()})");
    }

    private static IEnumerable<(bool Sys, string Db)> Databases(Workspace ws)
    {
        if (!string.IsNullOrWhiteSpace(ws.SysDatabase)) yield return (true, ws.SysDatabase);
        if (!string.IsNullOrWhiteSpace(ws.AppDatabase) && !ws.AppDatabase.Equals(ws.SysDatabase, StringComparison.OrdinalIgnoreCase)) yield return (false, ws.AppDatabase);
    }

    /// <summary>Database (Sys/App) tương ứng tên <paramref name="database"/> của dự án đang kết nối; null nếu khác dự án/database.</summary>
    private bool? SysOf(string project, string database)
    {
        var ws = Ws;
        if (ws is null || !SqlHistoryService.ProjectOf(ws).Equals(project, StringComparison.OrdinalIgnoreCase)) return null;
        return database.Equals(ws.SysDatabase, StringComparison.OrdinalIgnoreCase) ? true
             : database.Equals(ws.AppDatabase, StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    private async Task PushVersionsAsync(string project, string database, string key)
    {
        var ws = Ws;
        var files = await Task.Run(() => SqlHistoryService.List(ws, project, database, key));
        var rows = files.Select(v => new
        {
            id = v.Id, at = v.SavedAt, savedAt = v.SavedAt.ToString("dd/MM/yyyy HH:mm:ss"), length = v.Length, action = v.Action,
            machine = v.Machine, user = v.User, ip = v.Ip, project = v.Project, server = v.Server, database = v.Database, kind = v.Kind,
            source = "bcode", app = "Bcode",
        }).ToList();

        // Thay đổi do công cụ khác (DDL trigger) — chỉ có khi dự án đang kết nối đã cài theo dõi cho database này.
        var dot = key.IndexOf('.');
        if (SysOf(project, database) is { } sys && dot > 0)
            foreach (var x in await DdlTrackingService.ListAsync(_service.Connections, sys, key[..dot], key[(dot + 1)..]))
                rows.Add(new
                {
                    id = DbPrefix + x.Id, at = x.At, savedAt = x.At.ToString("dd/MM/yyyy HH:mm:ss"), length = x.Length, action = ActionOf(x.EventType),
                    machine = x.Host, user = x.Login, ip = x.Ip, project, server = ws?.Server ?? "", database, kind = x.ObjectType,
                    source = "external", app = x.App,
                });

        await Js($"window.setVersions({JsonSerializer.Serialize(rows.OrderByDescending(r => r.at), Web)})");
    }

    private static string ActionOf(string eventType) =>
        eventType.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ? "CREATE"
        : eventType.StartsWith("DROP", StringComparison.OrdinalIgnoreCase) ? "DROP" : "ALTER";

    private async Task PushVersionAsync(string project, string database, string key, string id)
    {
        string? text;
        if (id.StartsWith(DbPrefix, StringComparison.Ordinal) && long.TryParse(id[DbPrefix.Length..], out var logId) && SysOf(project, database) is { } sys)
            text = await DdlTrackingService.ReadCommandAsync(_service.Connections, sys, logId);
        else
        {
            var ws = Ws;
            text = await Task.Run(() => SqlHistoryService.Read(ws, project, database, key, id));
        }
        await Js($"window.setVersion({Json(id)}, {Json(text ?? "")})");
    }

    /// <summary>Bản hiện tại trong database — chỉ lấy được khi dự án/database của lịch sử trùng workspace đang kết nối.</summary>
    private async Task PushCurrentAsync(string project, string database, string key)
    {
        var ws = Ws;
        if (ws is null || !SqlHistoryService.ProjectOf(ws).Equals(project, StringComparison.OrdinalIgnoreCase))
        {
            await Js($"window.setCurrent(null, {Json("khác dự án đang kết nối")})");
            return;
        }
        var sys = SysOf(project, database);
        var dot = key.IndexOf('.');
        if (sys is null || dot < 0)
        {
            await Js($"window.setCurrent(null, {Json("khác database đang kết nối")})");
            return;
        }
        try
        {
            var def = await _service.GetObjectDefinitionAsync($"[{key[..dot]}].[{key[(dot + 1)..]}]", sys.Value);
            await Js(string.IsNullOrWhiteSpace(def)
                ? $"window.setCurrent(null, {Json("object không còn / bị mã hoá")})"
                : $"window.setCurrent({Json(SqlHistoryService.NormalizeHeader(def))}, '')");
        }
        catch (Exception ex)
        {
            await Js($"window.setCurrent(null, {Json("không đọc được database: " + ex.Message)})");
        }
    }

    // ---------------------------------------------------------------- theo dõi ngoài Bcode (DDL trigger)

    private async Task PushTrackingAsync()
    {
        var ws = Ws;
        if (ws is null) { await Js($"window.setTracking({{ project: '', items: [], note: {Json("Chưa chọn Workspace (WS).")} }})"); return; }
        var items = new List<object>();
        foreach (var (sys, db) in Databases(ws))
        {
            var st = await DdlTrackingService.GetStatusAsync(_service.Connections, sys);
            items.Add(new { which = sys ? "sys" : "app", label = sys ? "Sys Data" : "App Data", database = db, table = st.TableExists, trigger = st.TriggerExists, enabled = st.TriggerEnabled, rows = st.Rows, error = st.Error, canSee = st.CanSeeMetadata, found = st.TriggerFoundName });
        }
        await Js($"window.setTracking({JsonSerializer.Serialize(new { project = SqlHistoryService.ProjectOf(ws), server = ws.Server, items }, Web)})");
    }

    private IEnumerable<(bool Sys, string Db, string Label)> Targets(string which)
    {
        if (Ws is not { } ws) yield break;
        foreach (var (sys, db) in Databases(ws))
            if (which == "both" || (which == "sys" && sys) || (which == "app" && !sys))
                yield return (sys, db, sys ? "Sys Data" : "App Data");
    }

    private async Task TrackingInstallAsync(string which)
    {
        var targets = Targets(which).ToList();
        if (targets.Count == 0) return;
        var names = string.Join("\n", targets.Select(t => $"  • {t.Label}: {t.Db}"));
        var ask = MessageBox.Show(this,
            $"Cài theo dõi thay đổi vào database của dự án \"{SqlHistoryService.ProjectOf(Ws!)}\" (server {Ws!.Server}):\n\n{names}\n\n" +
            $"Bcode sẽ tạo trong mỗi database:\n" +
            $"  • bảng dbo.{DdlTrackingService.TableName} (lưu lịch sử CREATE/ALTER/DROP procedure, function, view, trigger)\n" +
            $"  • DDL trigger {DdlTrackingService.TriggerName} ghi vào bảng đó (có TRY/CATCH, không bao giờ chặn câu lệnh của người khác)\n" +
            $"  • quyền INSERT trên bảng cho PUBLIC (để mọi người sửa đều ghi được)\n\n" +
            $"Cần quyền tạo bảng và DDL trigger trên database. Gỡ lúc nào cũng được (nút \"Gỡ\").\n\nCài?",
            "Bcode — Theo dõi thay đổi ngoài Bcode", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (ask != DialogResult.Yes) return;

        await Js("window.setStatus('Đang cài theo dõi...')");
        var errors = new List<string>();
        foreach (var t in targets)
        {
            try { await DdlTrackingService.InstallAsync(_service.Connections, t.Sys); }
            catch (Exception ex) { errors.Add($"{t.Label} ({t.Db}): {ex.Message}"); }
        }
        await PushTrackingAsync();
        await Js(errors.Count == 0
            ? $"window.setStatus({Json("Đã cài theo dõi. Từ giờ thay đổi procedure/function ngoài Bcode sẽ được ghi lại.")}, 'ok')"
            : $"window.setStatus({Json("Lỗi: " + string.Join(" | ", errors))}, 'err')");
    }

    private async Task TrackingRemoveAsync(string which)
    {
        var targets = Targets(which).ToList();
        if (targets.Count == 0) return;
        if (MessageBox.Show(this,
                "Gỡ DDL trigger theo dõi khỏi:\n" + string.Join("\n", targets.Select(t => $"  • {t.Label}: {t.Db}")) +
                $"\n\nBảng dbo.{DdlTrackingService.TableName} giữ nguyên (không mất lịch sử đã ghi). Gỡ?",
                "Bcode — Theo dõi thay đổi ngoài Bcode", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;

        var errors = new List<string>();
        foreach (var t in targets)
        {
            try { await DdlTrackingService.RemoveAsync(_service.Connections, t.Sys); }
            catch (Exception ex) { errors.Add($"{t.Label} ({t.Db}): {ex.Message}"); }
        }
        await PushTrackingAsync();
        await Js(errors.Count == 0 ? $"window.setStatus({Json("Đã gỡ theo dõi.")}, 'ok')" : $"window.setStatus({Json("Lỗi: " + string.Join(" | ", errors))}, 'err')");
    }

    // ---------------------------------------------------------------- helpers

    private static void OpenFolder(Workspace? ws, string project, string database, string key)
    {
        var root = SqlHistoryService.RootFor(ws);
        var folder = string.IsNullOrEmpty(key) ? root : SqlHistoryService.FolderOf(ws, project, database, key);
        if (!Directory.Exists(folder)) folder = root;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }

    private static string Str(JsonElement d, string name) =>
        d.ValueKind == JsonValueKind.Object && d.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Json(string s) => JsonSerializer.Serialize(s);

    private async Task Js(string script)
    {
        if (IsDisposed || _web.CoreWebView2 is null) return;
        await _web.CoreWebView2.ExecuteScriptAsync(script);
    }

    private void PushTheme()
    {
        if (_web.CoreWebView2 is null) return;
        _ = _web.CoreWebView2.ExecuteScriptAsync($"window.setTheme && window.setTheme({(AppColors.IsDark ? "true" : "false")}, {UiThemes.PaletteJson()})");
    }
}
