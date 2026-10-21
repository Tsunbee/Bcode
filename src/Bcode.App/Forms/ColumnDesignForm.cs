using System.Text.Json;

namespace Bcode.App.Forms;

/// <summary>"Add New Column" / "Edit Column" — thiết kế cột để Gen Add / Alter Column (giống hộp Design của FCode): bảng Name / Type / Length / Not NULL,
/// thêm bớt dòng tuỳ ý. Mở sẵn các cột đã tick (kèm kiểu hiện tại) để sửa; OK → <see cref="Result"/> là danh sách (tên, kiểu SQL đầy đủ) dùng cho BuildColumnScript.
/// Giao diện là trang WebView2 (Web/Shell/columndesign.html).</summary>
public class ColumnDesignForm : WebDialogForm
{
    public sealed record Col(string Name, string Type, string Length, bool NotNull);

    private readonly bool _isAdd;
    private readonly string _table;
    private readonly List<Col> _initial;
    private readonly string? _hint;

    /// <summary>Danh sách cột sau khi bấm OK (đã bỏ dòng trống tên).</summary>
    public List<(string Name, string Type, bool NotNull)> Result { get; private set; } = new();

    /// <param name="hint">Dòng hướng dẫn thay cho câu mặc định của tab Table (null = giữ câu mặc định).</param>
    public ColumnDesignForm(bool isAdd, string table, IEnumerable<Col> initial, string? hint = null)
        : base(isAdd ? "Add New Column" : "Edit Column", "columndesign.html", 700, 480, 520, 320)
    {
        _isAdd = isAdd;
        _table = table;
        _initial = initial.ToList();
        _hint = hint;
    }

    /// <summary>"nvarchar(128)" → ("nvarchar","128"); "numeric(19,4)" → ("numeric","19,4"); "bit" → ("bit","").</summary>
    public static Col FromSqlType(string name, string sqlType, bool notNull)
    {
        var t = (sqlType ?? "").Trim();
        var p = t.IndexOf('(');
        if (p < 0) return new Col(name, t.ToLowerInvariant(), "", notNull);
        var end = t.IndexOf(')', p);
        return new Col(name, t[..p].Trim().ToLowerInvariant(), end > p ? t[(p + 1)..end].Trim() : "", notNull);
    }

    protected override void OnReady() =>
        Js($"colDesign.init({J(new { isAdd = _isAdd, table = _table, hint = _hint ?? "", cols = _initial.Select(c => new { name = c.Name, type = c.Type, length = c.Length, notNull = c.NotNull }) })})");

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action != "ok" || !msg.TryGetProperty("cols", out var arr) || arr.ValueKind != JsonValueKind.Array) return Task.CompletedTask;
        var list = new List<(string, string, bool)>();
        foreach (var c in arr.EnumerateArray())
        {
            var name = c.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
            var type = c.TryGetProperty("type", out var t) ? (t.GetString() ?? "").Trim() : "";
            var len = c.TryGetProperty("length", out var l) ? (l.GetString() ?? "").Trim() : "";
            var notNull = c.TryGetProperty("notNull", out var nn) && nn.ValueKind == JsonValueKind.True;
            if (name.Length == 0 || type.Length == 0) continue;
            list.Add((name, len.Length > 0 ? $"{type}({len})" : type, notNull));
        }
        Result = list;
        CloseWith(DialogResult.OK);
        return Task.CompletedTask;
    }
}
