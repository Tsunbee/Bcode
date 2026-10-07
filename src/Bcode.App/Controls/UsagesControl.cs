using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// Tab "Ai đang dùng …": liệt kê object trong database (cả App Data lẫn Sys Data) và file trong source có nhắc tới một procedure / function / view / bảng.
/// Giao diện là trang WebView2 (Web/Shell/usages.html); control chạy <see cref="UsageSearchService"/> ngầm (database và source song song, không chặn giao diện)
/// rồi đẩy kết quả từng phần vào trang. Bấm 1 dòng → mở object / file qua <see cref="OpenObjectRequested"/> / <see cref="OpenFileRequested"/>.
/// </summary>
public class UsagesControl : UserControl
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly WebBarHost _web = new("usages.html") { Dock = DockStyle.Fill };
    private readonly SqlObjectInfo _target;
    private readonly UsageSearchService _service;
    private readonly string? _sourceRoot;
    private readonly SourceIndexService _index = new();
    private readonly Func<IEnumerable<(Bcode.App.Models.WCommandItem Item, string Path)>>? _menus;
    private CancellationTokenSource? _cts;

    public event Action<SqlObjectInfo>? OpenObjectRequested;
    public event Action<string>? OpenFileRequested;
    /// <summary>Bấm tên menu dưới một file: mở cây menu và chọn menu đó (wmenu_id).</summary>
    public event Action<string>? RevealMenuRequested;

    public UsagesControl(SqlObjectInfo target, UsageSearchService service, string? sourceRoot,
        Func<IEnumerable<(Bcode.App.Models.WCommandItem Item, string Path)>>? menus = null)
    {
        _menus = menus;
        _target = target;
        _service = service;
        _sourceRoot = sourceRoot;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Message += root => Handle(root.GetRawText());
        _web.Ready += () =>
        {
            Js($"usages.init({J(new { name = target.QualifiedName, kind = target.Kind.ToString(), sys = target.FromSysDatabase, hasSource = !string.IsNullOrWhiteSpace(sourceRoot) })})");
            _ = RunAsync();
        };
        Disposed += (_, _) => { _cts?.Cancel(); _cts?.Dispose(); };
    }

    private static string J(object? o) => JsonSerializer.Serialize(o, JsonOpts);

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    private void Handle(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            switch (root.GetProperty("action").GetString())
            {
                case "rerun": _ = RunAsync(force: true); break;
                case "revealMenu": RevealMenuRequested?.Invoke(root.GetProperty("id").GetString() ?? ""); break;
                case "openObject":
                    OpenObjectRequested?.Invoke(new SqlObjectInfo
                    {
                        Schema = root.GetProperty("schema").GetString() ?? "dbo",
                        Name = root.GetProperty("name").GetString() ?? "",
                        Kind = Enum.TryParse<SqlObjectKind>(root.GetProperty("kind").GetString(), out var k) ? k : SqlObjectKind.StoredProcedure,
                        FromSysDatabase = root.GetProperty("sys").GetBoolean(),
                    });
                    break;
                case "openFile": OpenFileRequested?.Invoke(root.GetProperty("path").GetString() ?? ""); break;
                case "copy":
                    try { Clipboard.SetText(root.GetProperty("text").GetString() ?? ""); } catch { /* clipboard bận */ }
                    break;
            }
        }
        catch { /* tin nhắn lỗi — bỏ qua */ }
    }

    private async Task RunAsync(bool force = false)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        Js("usages.onStart()");

        // Hai database và source chạy song song; mỗi phần xong là hiện ngay phần đó.
        async Task Db(bool sys)
        {
            try
            {
                var list = await _service.FindInDatabaseAsync(_target, sys, ct);
                if (!ct.IsCancellationRequested) Js($"usages.onDb({J(sys)}, {J(list.Select(Map))})");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!ct.IsCancellationRequested) Js($"usages.onError('{(sys ? "sys" : "app")}', {J(ex.Message)})"); }
        }

        async Task Src()
        {
            if (string.IsNullOrWhiteSpace(_sourceRoot)) { Js("usages.onSource(null)"); return; }
            try
            {
                var res = await _index.FindAsync(_sourceRoot, _target.Name, force, msg => Js($"usages.onProgress({J(msg)})"), ct);
                if (ct.IsCancellationRequested) return;
                if (res.Warning is not null) { Js($"usages.onError('src', {J(res.Warning)})"); return; }
                var root = SourceIndexService.ControllersDir(_sourceRoot);
                var menuRows = _menus?.Invoke().ToList() ?? new();
                var files = res.Files.Select(f => new { path = f.File, rel = Rel(root, f.File), hits = f.Hits, lines = f.Snippets, menus = MenusFor(f.File, menuRows) });
                var info = $"{res.IndexedFiles:N0} file trong chỉ mục · {(res.FromCache ? "dùng chỉ mục có sẵn" : res.ReadFiles > 0 ? $"đọc {res.ReadFiles:N0} file mới/đổi" : "không có file đổi")} · {res.Elapsed.TotalSeconds:0.0}s";
                Js($"usages.onSource({J(files)}, {J(info)})");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!ct.IsCancellationRequested) Js($"usages.onError('src', {J(ex.Message)})"); }
        }

        await Task.WhenAll(Db(false), Db(true), Src());
        if (!ct.IsCancellationRequested) Js("usages.onDone()");
    }

    /// <summary>Menu WCommand trỏ tới file controller này: tên file (không đuôi) trùng SysId (hoặc SysId_ / SysId.) hoặc trùng tên file trang trong Link.</summary>
    private static List<object> MenusFor(string file, List<(Bcode.App.Models.WCommandItem Item, string Path)> rows)
    {
        var name = Path.GetFileNameWithoutExtension(file);
        var found = new List<object>();
        foreach (var (item, path) in rows)
        {
            var sys = (item.SysId ?? "").Trim();
            var link = Path.GetFileNameWithoutExtension((item.Link ?? "").Replace('/', '\\').Trim());
            var hit = (sys.Length > 0 && (name.Equals(sys, StringComparison.OrdinalIgnoreCase)
                           || name.StartsWith(sys + "_", StringComparison.OrdinalIgnoreCase) || name.StartsWith(sys + ".", StringComparison.OrdinalIgnoreCase)))
                      || (link.Length > 0 && name.Equals(link, StringComparison.OrdinalIgnoreCase));
            if (!hit) continue;
            found.Add(new { id = item.WMenuId, text = (path.Length > 0 ? path + " \\ " : "") + item.Bar + " (" + item.WMenuId + ")" });
            if (found.Count >= 5) break;
        }
        return found;
    }

    private static object Map(DbUsage u) => new { sys = u.Sys, schema = u.Schema, name = u.Name, kind = u.Kind.ToString(), hits = u.Hits, lines = u.Snippets };

    private static string Rel(string root, string file)
    {
        try { return Path.GetRelativePath(root, file); } catch { return file; }
    }
}
