using System.Diagnostics;
using System.Text.Json;
using Bcode.App.Services;
using Bcode.App.UI;
using Microsoft.Web.WebView2.WinForms;

namespace Bcode.App.Forms;

/// <summary>
/// "Lịch sử sửa procedure / function / view / trigger" của SQL Query: chọn dự án và object, xem các phiên bản đã lưu (kèm máy, tài khoản,
/// IP người sửa) và so sánh bên cạnh bản hiện tại trong database (hoặc bản trước / kế tiếp) bằng Monaco diff. Giao diện là trang WebView2
/// (Web/Shell/sqlhistory.html) tự co giãn theo cửa sổ và UiScale; dữ liệu do <see cref="SqlHistoryService"/> quản lý, thư mục lưu khai báo
/// ở màn hình "Giao diện (Template)". Khôi phục = đưa nội dung vào editor SQL Query (không tự chạy).
/// </summary>
public class SqlHistoryForm : ThemedForm
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

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
                case "versions":
                    var list = await Task.Run(() => SqlHistoryService.List(Str(data, "project"), Str(data, "db"), Str(data, "key")));
                    await Js($"window.setVersions({JsonSerializer.Serialize(list.Select(v => new
                    {
                        id = v.Id, savedAt = v.SavedAt.ToString("dd/MM/yyyy HH:mm:ss"), length = v.Length, action = v.Action,
                        machine = v.Machine, user = v.User, ip = v.Ip, project = v.Project, server = v.Server, database = v.Database, kind = v.Kind,
                    }), Web)})");
                    break;
                case "load":
                    var text = await Task.Run(() => SqlHistoryService.Read(Str(data, "project"), Str(data, "db"), Str(data, "key"), Str(data, "id"))) ?? "";
                    await Js($"window.setVersion({Json(Str(data, "id"))}, {Json(text)})");
                    break;
                case "current": await PushCurrentAsync(Str(data, "project"), Str(data, "db"), Str(data, "key")); break;
                case "restore":
                    await _loadIntoEditor(Str(data, "text"));
                    Close();
                    break;
                case "copy": Clipboard.SetText(Str(data, "text")); break;
                case "open-folder": OpenFolder(Str(data, "project"), Str(data, "db"), Str(data, "key")); break;
                case "close": Close(); break;
            }
        }
        catch (Exception ex)
        {
            await Js($"window.setStatus({Json(ex.Message)}, 'err')");
        }
    }

    private async Task PushStateAsync()
    {
        PushTheme();
        var ws = _service.Connections.Current;
        var objects = await Task.Run(SqlHistoryService.ListObjects);
        var current = ws is null ? null : new
        {
            project = SqlHistoryService.ProjectOf(ws),
            db = _useSys ? ws.SysDatabase : ws.AppDatabase,
            key = _focus?.Key ?? "",
        };
        var state = new
        {
            root = SqlHistoryService.Root,
            current = current ?? new { project = "", db = "", key = "" },
            objects = objects.Select(o => new { project = o.Project, database = o.Database, key = o.Key, kind = o.Kind, count = o.Count, last = o.Last.ToString("o") }),
        };
        await Js($"window.init({JsonSerializer.Serialize(state, Web)}, {(AppColors.IsDark ? "true" : "false")}, {UiThemes.PaletteJson()})");
    }

    /// <summary>Bản hiện tại trong database — chỉ lấy được khi dự án/database của lịch sử trùng workspace đang kết nối.</summary>
    private async Task PushCurrentAsync(string project, string database, string key)
    {
        var ws = _service.Connections.Current;
        if (ws is null || !SqlHistoryService.ProjectOf(ws).Equals(project, StringComparison.OrdinalIgnoreCase))
        {
            await Js($"window.setCurrent(null, {Json("khác dự án đang kết nối")})");
            return;
        }
        bool? sys = database.Equals(ws.SysDatabase, StringComparison.OrdinalIgnoreCase) ? true
                  : database.Equals(ws.AppDatabase, StringComparison.OrdinalIgnoreCase) ? false : null;
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
                : $"window.setCurrent({Json(def)}, '')");
        }
        catch (Exception ex)
        {
            await Js($"window.setCurrent(null, {Json("không đọc được database: " + ex.Message)})");
        }
    }

    private static void OpenFolder(string project, string database, string key)
    {
        var folder = string.IsNullOrEmpty(key) ? SqlHistoryService.Root : SqlHistoryService.FolderOf(project, database, key);
        if (!Directory.Exists(folder)) folder = SqlHistoryService.Root;
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
