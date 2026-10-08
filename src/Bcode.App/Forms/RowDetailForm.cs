using System.Text.Json;

namespace Bcode.App.Forms;

/// <summary>Một cột của dòng: <c>kind</c> = "text" | "bool" | "number" | "date"; <c>value</c> null = NULL.</summary>
public sealed record RowDetailField(string Name, string Kind, string? Value, bool ReadOnly, string TypeName);

/// <summary>
/// "View Detail Datarow" (F1 ở lưới Table): hiện MỌI cột của dòng đang chọn thành một form — nhãn + ô nhập (checkbox cho cột bit), giá trị dạng danh sách
/// ("a, b, c, ...") hiện thành dải ô nhỏ sửa từng giá trị — rồi OK ghi lại các ô đã đổi. Giao diện là trang WebView2 (Web/Shell/rowdetail.html);
/// form chỉ trả về các cột đổi (tên → chuỗi), việc đổi kiểu và lưu xuống DB do TableEditControl đảm nhiệm.
/// </summary>
public sealed class RowDetailForm : WebDialogForm
{
    private readonly string _caption;
    private readonly IReadOnlyList<RowDetailField> _fields;
    private Dictionary<string, string?> _changed = new(StringComparer.OrdinalIgnoreCase);

    public RowDetailForm(string caption, IReadOnlyList<RowDetailField> fields)
        : base("View Detail Datarow — " + caption, "rowdetail.html", 1100, 700, 560, 380)
    {
        _caption = caption;
        _fields = fields;
    }

    /// <summary>Các cột người dùng đã sửa (tên → giá trị mới; null = đặt NULL). Hợp lệ khi đóng với DialogResult.OK.</summary>
    public IReadOnlyDictionary<string, string?> Changed => _changed;

    protected override void OnReady() =>
        Js($"rowDetail.init({J(new { caption = _caption, fields = _fields.Select(f => new { name = f.Name, kind = f.Kind, value = f.Value, readOnly = f.ReadOnly, type = f.TypeName }) })})");

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action != "save") return Task.CompletedTask;
        _changed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in msg.GetProperty("changed").EnumerateObject())
            _changed[p.Name] = p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString();
        CloseWith(DialogResult.OK);
        return Task.CompletedTask;
    }
}
