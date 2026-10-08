using System.Text.Json;

namespace Bcode.App.Forms;

/// <summary>
/// "Khai báo nhanh" (F3 ở lưới Table): các ô đang chọn của MỘT dòng, mỗi ô là danh sách cách nhau dấu phẩy ("ma_kh, ten_kh, dia_chi"),
/// được tách thành bảng — mỗi cột đang chọn là một cột của bảng, mỗi giá trị là một dòng — để sửa / thêm / xoá / đổi thứ tự từng giá trị rồi Save
/// ghép lại thành "a, b, c" (bỏ giá trị rỗng). Giao diện là trang WebView2 (Web/Shell/listeditor.html).
/// </summary>
public sealed class ListEditorForm : WebDialogForm
{
    private readonly IReadOnlyList<(string Column, string Value)> _items;
    private readonly string _caption;
    private Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public ListEditorForm(string caption, IReadOnlyList<(string Column, string Value)> items)
        : base("Khai báo nhanh — " + caption, "listeditor.html", 880, 640, 520, 360)
    {
        _caption = caption;
        _items = items;
    }

    /// <summary>Cột → chuỗi đã ghép lại "a, b, c" (chỉ hợp lệ sau khi đóng với DialogResult.OK).</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    protected override void OnReady() =>
        Js($"listEditor.init({J(new { caption = _caption, columns = _items.Select(i => new { name = i.Column, items = Split(i.Value) }) })})");

    /// <summary>"a, b ,c" → ["a","b","c"] (bỏ khoảng trắng thừa; giữ các giá trị rỗng giữa chừng ra thành dòng trống để thấy rõ).</summary>
    internal static List<string> Split(string? value) =>
        string.IsNullOrWhiteSpace(value) ? new() : value.Split(',').Select(s => s.Trim()).ToList();

    protected override Task OnActionAsync(string action, JsonElement msg)
    {
        if (action != "save") return Task.CompletedTask;
        _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var col in msg.GetProperty("columns").EnumerateArray())
        {
            var parts = col.GetProperty("items").EnumerateArray().Select(e => (e.GetString() ?? "").Trim()).Where(s => s.Length > 0);
            _values[col.GetProperty("name").GetString() ?? ""] = string.Join(", ", parts);
        }
        CloseWith(DialogResult.OK);
        return Task.CompletedTask;
    }
}
