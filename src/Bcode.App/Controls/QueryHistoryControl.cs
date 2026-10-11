using System.Text.Json;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Lịch sử SQL": tìm toàn văn trong các lần đã chạy, ghim câu hay dùng (nhãn + thẻ theo dự án / báo cáo) và mở lại câu với tham số lần chạy trước.
/// Giao diện là trang WebView2 (Web/Shell/queryhistory.html); dữ liệu do <see cref="QueryHistoryService"/> giữ — file chỉ được đọc (luồng nền) khi tab này mở lần đầu,
/// nên có hay không có tab này cũng không ảnh hưởng thời gian mở Bcode. Mở lại chỉ ĐẶT script vào tab SQL mới, không tự chạy.
/// </summary>
public class QueryHistoryControl : UserControl, ISleepableTab
{
    /// <summary>Ngủ đông khi tab ẩn lâu (xem <see cref="WebBarHost.Sleepable"/>).</summary>
    public Task<bool> SleepAsync() => _web.SleepAsync();

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const int PageSize = 200;

    private readonly WebBarHost _web = new("queryhistory.html") { Dock = DockStyle.Fill, Sleepable = true };
    private readonly QueryHistoryService _service = QueryHistoryService.Instance;
    private readonly Func<string> _currentWorkspace;
    private int _version;

    /// <summary>(script, dùng Sys Data, tiêu đề tab).</summary>
    public event Action<string, bool, string>? OpenSqlRequested;

    public QueryHistoryControl(Func<string> currentWorkspace)
    {
        _currentWorkspace = currentWorkspace;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => _ = HandleAsync(root.GetRawText());
        _web.Ready += async () =>
        {
            await _service.EnsureLoadedAsync();
            Js($"qh.init({J(new { workspace = _currentWorkspace(), workspaces = _service.Workspaces(), tags = _service.AllTags() })})");
            await SearchAsync("runs", "", "", "");
        };
    }

    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    private static string Str(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static bool Bool(JsonElement r, string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;

    private async Task HandleAsync(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            switch (r.GetProperty("action").GetString())
            {
                case "search": await SearchAsync(Str(r, "tab"), Str(r, "text"), Str(r, "ws"), Str(r, "tag")); break;

                case "tagRun":
                    _service.UpdateRun(Str(r, "id"), Str(r, "label"), Str(r, "tags").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries));
                    Js($"qh.onSaved({J(_service.Workspaces())}, {J(_service.AllTags())})");
                    break;

                case "delRuns":
                {
                    var ids = r.GetProperty("ids").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
                    if (ids.Count == 0) break;
                    if (MessageBox.Show(FindForm(), $"Xoá {ids.Count} mục đã tick khỏi Lịch sử SQL?", "Bcode — Lịch sử SQL", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) break;
                    _service.DeleteRuns(ids);
                    Js($"qh.onDeleted({J(ids)}, {J(_service.AllTags())})");
                    break;
                }

                case "open":
                {
                    var id = Str(r, "id"); var last = Bool(r, "last");
                    if (Str(r, "kind") == "pin")
                    {
                        if (_service.FindPin(id) is not { } pin) break;
                        // "Chạy lại với tham số lần trước": lấy script của lần chạy gần nhất cùng bộ khung (mang tham số lần đó); chưa từng chạy thì dùng chính câu ghim.
                        var run = last ? _service.LastRunOf(pin.Skeleton, _currentWorkspace()) : null;
                        OpenSqlRequested?.Invoke(run?.Sql ?? pin.Sql, run?.Sys ?? false, "★ " + pin.Label);
                    }
                    else if (_service.FindRun(id) is { } run)
                    {
                        var use = last ? _service.LastRunOf(run.Skeleton, _currentWorkspace()) ?? run : run;
                        OpenSqlRequested?.Invoke(use.Sql, use.Sys, "SQL " + use.At.ToString("dd/MM HH:mm"));
                    }
                    break;
                }

                case "savePin":
                {
                    var runId = Str(r, "runId");
                    var sql = Str(r, "sql");
                    if (sql.Length == 0 && runId.Length > 0) sql = _service.FindRun(runId)?.Sql ?? "";
                    if (sql.Length == 0 && Str(r, "id") is { Length: > 0 } pid) sql = _service.FindPin(pid)?.Sql ?? "";
                    if (sql.Length == 0) break;
                    var tags = Str(r, "tags").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    var label = Str(r, "label");
                    _service.SavePin(Str(r, "id") is { Length: > 0 } id2 ? id2 : null, label.Length > 0 ? label : FirstLine(sql), tags, Bool(r, "thisWs") ? _currentWorkspace() : null, sql);
                    Js($"qh.onSaved({J(_service.Workspaces())}, {J(_service.AllTags())})");
                    break;
                }

                case "delPin": _service.DeletePin(Str(r, "id")); break;
                case "clear":
                    if (MessageBox.Show(FindForm(), "Xoá toàn bộ lịch sử các lần chạy? (Câu đã ghim được giữ lại.)", "Bcode — Lịch sử SQL", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                        _service.ClearRuns();
                    break;
                case "copy":
                    try { Clipboard.SetText(Str(r, "text")); } catch { /* clipboard bận */ }
                    break;
            }
        }
        catch { /* tin nhắn lỗi — bỏ qua */ }
    }

    private static string FirstLine(string sql)
    {
        var line = sql.Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("--")) ?? "Câu SQL";
        return line.Length > 60 ? line[..60] + "…" : line;
    }

    private async Task SearchAsync(string tab, string text, string ws, string tag)
    {
        var version = ++_version;
        // Tìm trong bộ nhớ nhưng đẩy sang luồng nền để danh sách lớn không làm khựng giao diện lúc gõ.
        object payload = await Task.Run<object>(() =>
        {
            if (tab == "pins")
            {
                var pins = _service.SearchPins(text, string.IsNullOrEmpty(ws) ? null : ws)
                    .Where(p => tag.Length == 0 || p.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)).ToList();
                return new
                {
                    tab,
                    total = pins.Count,
                    items = pins.Select(p => new
                    {
                        kind = "pin", id = p.Id, label = p.Label, tags = p.Tags, ws = p.Workspace, sql = Clip(p.Sql), at = p.Created, @params = QueryHistoryService.Params(p.Sql),
                        hasLast = _service.LastRunOf(p.Skeleton) is not null,
                    }).ToList(),
                };
            }
            var runs = _service.SearchRuns(text, string.IsNullOrEmpty(ws) ? null : ws, tag.Length == 0 ? null : tag, PageSize);
            return new
            {
                tab,
                total = runs.Count,
                items = runs.Select(x => new { kind = "run", id = x.Id, ws = x.Workspace, sys = x.Sys, sql = Clip(x.Sql), at = x.At, label = x.Label, tags = x.Tags, @params = QueryHistoryService.Params(x.Sql) }).ToList(),
            };
        });
        if (version == _version) Js($"qh.onList({J(payload)})");
    }

    /// <summary>Chỉ đẩy phần đầu script sang trang (xem đầy đủ bằng cách mở vào tab SQL) để danh sách dài không nặng.</summary>
    private static string Clip(string sql) => sql.Length <= 1200 ? sql : sql[..1200] + "\n…";
}
