using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "So sánh object": so phần thân procedure / function / view / trigger giữa hai database (thường dev ↔ khách) và sinh script ALTER.
/// Giao diện là trang WebView2 (Web/Shell/compareobjects.html); logic ở <see cref="ObjectCompareService"/> — chỉ đọc hai database và chỉ sinh chữ,
/// không chạy script nào. Bổ sung cho "Compare Structure" (so cột của bảng, giữ nguyên).
/// </summary>
public class CompareObjectsControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebBarHost _web = new("compareobjects.html") { Dock = DockStyle.Fill };
    private readonly AppSettings _settings;
    private readonly Func<Workspace?> _current;
    private readonly ObjectCompareService _service = new();
    private ObjectCompareService.Session? _session;
    private CancellationTokenSource? _cts;

    /// <summary>(script, tiêu đề tab) — mở script vừa sinh trong tab SQL để xem / sửa / chạy tay.</summary>
    public event Action<string, string>? OpenSqlRequested;

    public CompareObjectsControl(AppSettings settings, Func<Workspace?> current)
    {
        _settings = settings;
        _current = current;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => _ = HandleAsync(root.GetRawText());
        _web.Ready += () => Js($"co.init({J(new { workspaces = _settings.Workspaces.Select(w => new { name = w.Name, app = w.EffectiveAppDatabase, sys = w.SysDatabase }), current = _current()?.Name ?? "" })})");
        Disposed += (_, _) => { _cts?.Cancel(); _cts?.Dispose(); };
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

    private Workspace? Ws(string name) => _settings.Workspaces.FirstOrDefault(w => w.Name == name);

    private async Task HandleAsync(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var r = doc.RootElement;
            switch (r.GetProperty("action").GetString())
            {
                case "compare": await CompareAsync(Ws(Str(r, "leftWs")), Bool(r, "leftSys"), Ws(Str(r, "rightWs")), Bool(r, "rightSys")); break;
                case "detail": await DetailAsync(Str(r, "key")); break;
                case "script": await ScriptAsync(r.GetProperty("keys").EnumerateArray().Select(k => k.GetString() ?? "").ToList(), Bool(r, "open")); break;
                case "copy":
                    try { Clipboard.SetText(Str(r, "text")); } catch { /* clipboard bận */ }
                    break;
            }
        }
        catch (Exception ex) { Js($"co.onError({J(ex.Message)})"); }
    }

    private async Task CompareAsync(Workspace? left, bool leftSys, Workspace? right, bool rightSys)
    {
        if (left is null || right is null) { Js($"co.onError({J("Chọn workspace cho cả hai bên.")})"); return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _session = null;
        Js("co.onStart()");
        try
        {
            var ses = await _service.CompareAsync(left, leftSys, right, rightSys, msg => Js($"co.onProgress({J(msg)})"), cts.Token);
            if (cts.IsCancellationRequested) return;
            _session = ses;
            Js($"co.onResult({J(new { left = left.Name + " · " + ObjectCompareService.DbName(left, leftSys), right = right.Name + " · " + ObjectCompareService.DbName(right, rightSys), items = ses.Items.Select(i => new { key = i.Key, schema = i.Schema, name = i.Name, kind = i.Kind, status = i.Status.ToString(), lMod = i.LeftModified, rMod = i.RightModified }) })})");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cts.IsCancellationRequested) Js($"co.onError({J(ex.Message)})"); }
    }

    private async Task DetailAsync(string key)
    {
        if (_session is not { } ses) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        var l = await _service.GetDefinitionAsync(ses, true, key, ct);
        var r = await _service.GetDefinitionAsync(ses, false, key, ct);
        var lines = l is not null && r is not null ? await Task.Run(() => _service.BuildDiff(l, r), ct)
            : (l ?? r ?? "").Replace("\r\n", "\n").Split('\n').Select((t, i) => new ObjectDiffLine(l is not null ? "+" : "-", i + 1, t)).Take(1500).ToList();
        Js($"co.onDetail({J(new { key, lines, note = l is null && r is null ? "Object mã hoá hoặc không đọc được nội dung." : l is null ? "Chỉ có ở bên phải." : r is null ? "Chỉ có ở bên trái." : "" })})");
    }

    private async Task ScriptAsync(List<string> keys, bool open)
    {
        if (_session is not { } ses) return;
        var script = await _service.BuildScriptAsync(ses, keys, _cts?.Token ?? CancellationToken.None);
        if (open) OpenSqlRequested?.Invoke(script, "ALTER → " + ses.Right.Name);
        else Js($"co.onScript({J(script)})");
    }
}
