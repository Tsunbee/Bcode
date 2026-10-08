using System.Data;
using System.Text.Json;
using Bcode.App.Models;
using Bcode.App.Services;

namespace Bcode.App.Controls;

/// <summary>
/// "Lookup" tool — searches SQL objects (Function/Store/Table/View/Trigger) by
/// name, previews the selected object's definition, and can additionally show
/// a data sample (for tables) or a "where does the search text appear in this
/// definition" list, plus a session History of recently viewed objects.
/// Giao diện là trang WebView2 (Web/Shell/lookup.html — tự co giãn theo cửa sổ, ăn theo Template giao diện); control này lo phần truy vấn
/// (<see cref="SqlObjectBrowserService"/> / <see cref="TableDataService"/>). Trang chỉ gửi bộ lọc và số thứ tự của object — không gửi tên/đường dẫn.
/// Ba ô "Show Search Text / Show Data / Show History" hoạt động như nút chọn một trong ba: hiện trong khung dưới bản xem định nghĩa các dòng khớp
/// từ khoá, Top-100 dữ liệu (chỉ bảng), hoặc lịch sử các object vừa xem.
/// </summary>
public class LookupControl : UserControl
{
    private readonly WebBarHost _web = new("lookup.html") { Dock = DockStyle.Fill };
    private readonly SqlObjectBrowserService _sqlObjectService;
    private readonly TableDataService _tableDataService;

    private readonly List<SqlObjectInfo> _results = new();   // danh sách đang hiện (trang tham chiếu bằng chỉ số)
    private readonly List<SqlObjectInfo> _history = new();
    private SqlObjectInfo? _currentObj;
    private string _mode = "";                                  // "", "search", "data", "history"
    private int _searchVersion;

    public event Action<SqlObjectInfo>? OpenInTabRequested;

    private static string J(object? o) => JsonSerializer.Serialize(o);

    public LookupControl(SqlObjectBrowserService sqlObjectService, TableDataService tableDataService)
    {
        _sqlObjectService = sqlObjectService;
        _tableDataService = tableDataService;
        Dock = DockStyle.Fill;
        Controls.Add(_web);
        _web.Ready += () => { };   // trang tự gửi "ready" + lọc đầu tiên khi nạp xong
        _web.Message += root => { var m = root.Clone(); _ = HandleAsync(m); };
    }

    private void Js(string script)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(() => _web.Call(script)); } catch { /* đang đóng */ } }
        else _web.Call(script);
    }

    private static string KindLabel(SqlObjectKind k) => k switch
    {
        SqlObjectKind.Table => "Table", SqlObjectKind.View => "View", SqlObjectKind.StoredProcedure => "Store", SqlObjectKind.Trigger => "Trigger", _ => "Function",
    };

    private static object Entry(SqlObjectInfo o, int i) => new { i, sys = o.FromSysDatabase, name = o.QualifiedName, kind = KindLabel(o.Kind) };

    private async Task HandleAsync(JsonElement m)
    {
        try
        {
            switch (m.TryGetProperty("action", out var a) ? a.GetString() : "")
            {
                case "search": await SearchAsync(m); break;
                case "select":
                    if (m.GetProperty("list").GetString() == "history") { if (Valid(_history, m, out var h)) await LoadObjectAsync(h); }
                    else if (Valid(_results, m, out var r)) await LoadObjectAsync(r);
                    break;
                case "mode": _mode = m.GetProperty("mode").GetString() ?? ""; await RefreshBottomAsync(); break;
                case "openInTab": if (_currentObj is { } obj) OpenInTabRequested?.Invoke(obj); break;
            }
        }
        catch (Exception ex) { Js($"lookup.onStatus({J("Lỗi: " + ex.Message)}, 'err')"); }
    }

    private static bool Valid(List<SqlObjectInfo> list, JsonElement m, out SqlObjectInfo info)
    {
        info = null!;
        if (!m.TryGetProperty("i", out var e) || !e.TryGetInt32(out var i) || i < 0 || i >= list.Count) return false;
        info = list[i];
        return true;
    }

    private async Task SearchAsync(JsonElement m)
    {
        var version = ++_searchVersion;
        var filter = (m.GetProperty("filter").GetString() ?? "").Trim();
        var db = m.GetProperty("db").GetInt32();
        var kinds = new HashSet<SqlObjectKind>();
        foreach (var k in m.GetProperty("kinds").EnumerateArray())
            switch (k.GetString())
            {
                case "fn": kinds.Add(SqlObjectKind.Function); break;
                case "proc": kinds.Add(SqlObjectKind.StoredProcedure); break;
                case "table": kinds.Add(SqlObjectKind.Table); break;
                case "view": kinds.Add(SqlObjectKind.View); break;
                case "trigger": kinds.Add(SqlObjectKind.Trigger); break;
            }
        Js("lookup.onStatus('Đang tìm...', '')");
        try
        {
            var dbChoices = db switch { 0 => new[] { false }, 1 => new[] { true }, _ => new[] { false, true } };
            var results = new List<SqlObjectInfo>();
            foreach (var useSys in dbChoices)
            {
                var objs = await _sqlObjectService.ListObjectsAsync(useSys, filter.Length == 0 ? null : filter);
                results.AddRange(objs.Where(o => kinds.Contains(o.Kind)));
            }
            if (version != _searchVersion || IsDisposed) return;   // đã có lần gõ mới hơn
            _results.Clear();
            _results.AddRange(results.OrderBy(o => o.Kind).ThenBy(o => o.QualifiedName, StringComparer.OrdinalIgnoreCase));
            Js($"lookup.onList({J(_results.Select(Entry))})");
            Js($"lookup.onStatus({J($"{_results.Count} object.")}, '')");
        }
        catch (Exception)
        {
            Js("lookup.onStatus('Lỗi (chưa kết nối?).', 'err')");   // không popup khi gõ tìm liên tục
        }
    }

    private async Task LoadObjectAsync(SqlObjectInfo info)
    {
        _currentObj = info;
        Js($"lookup.onDefinition({J(new { name = info.QualifiedName, text = "-- Đang tải..." })})");
        try
        {
            var def = await _sqlObjectService.GetDefinitionAsync(info);
            Js($"lookup.onDefinition({J(new { name = info.QualifiedName, text = def })})");
            _history.RemoveAll(h => h.FromSysDatabase == info.FromSysDatabase && h.QualifiedName.Equals(info.QualifiedName, StringComparison.OrdinalIgnoreCase));
            _history.Insert(0, info);
            if (_history.Count > 50) _history.RemoveAt(_history.Count - 1);
            Js($"lookup.onHistory({J(_history.Select(Entry))})");
            await RefreshBottomAsync();
        }
        catch (Exception ex)
        {
            Js($"lookup.onDefinition({J(new { name = info.QualifiedName, text = "-- Lỗi: " + ex.Message })})");
        }
    }

    private async Task RefreshBottomAsync()
    {
        if (_mode != "data") return;
        if (_currentObj is not { Kind: SqlObjectKind.Table } obj) { Js("lookup.onData(null)"); return; }
        try
        {
            var data = await _tableDataService.LoadTableAsync(obj.FromSysDatabase, obj.Schema, obj.Name, 100);
            var table = data;
            if (table is null) { Js("lookup.onData(null)"); return; }
            var cols = table.Columns.Cast<DataColumn>().Select(c => c.ColumnName).ToList();
            var rows = table.Rows.Cast<DataRow>().Select(r => cols.Select((_, i) => r.IsNull(i) ? null : Convert.ToString(r[i])).ToArray()).ToList();
            Js($"lookup.onData({J(new { columns = cols, rows })})");
        }
        catch (Exception ex) { Js($"lookup.onStatus({J("Show Data: " + ex.Message)}, 'err')"); }
    }
}
